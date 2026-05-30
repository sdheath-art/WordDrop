using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages 5 letter slots for one player with a three-layer playability governor.
    ///
    /// LAYER 1 — BASE BAG (TileBag.cs)
    ///   Bag distribution is already tuned. This layer just draws from it.
    ///
    /// LAYER 2 — HAND-AWARE REFILL
    ///   On every draw, inspects the current hand and applies bounded rules:
    ///   - Vowel band: prefer 1–2 vowels, allow 0 sometimes, reject 3+
    ///   - Low-utility clamp: max MAX_LOW_UTILITY (1) of Q/X/Z/J/V/K
    ///   - Duplicate clamp: max 1 of any single letter
    ///   - Connector diversity: bias toward useful consonants when hand is weak
    ///
    /// LAYER 3 — STALE-STATE ASSIST
    ///   After consecutive non-scoring turns, uses board-aware DroughtAssist
    ///   to suggest letters that could help complete near-visible words.
    ///
    /// No MonoBehaviour — instantiate directly.
    /// </summary>
    public class PlayerHand
    {
        public const int MAX_HAND_SIZE = 5;
        public static int HAND_SIZE => SurvivalManager.IsSurvivalMode ? 4 : 5;

        // ══════════════════════════════════════════════════════════════════════════
        // LAYER 2 — Hand-aware refill tuning
        // ══════════════════════════════════════════════════════════════════════════

        // Vowel band: target ratio scales with hand size
        // 5-card hand: 2 vowels (40%) — 3+ rejected
        // 4-card hand (Survival): 1 vowel (25%) — 2+ rejected
        private static int VOWEL_FLOOR   => SurvivalManager.IsSurvivalMode ? 1 : 2;
        private static int VOWEL_CEILING => SurvivalManager.IsSurvivalMode ? 2 : 3;

        public const int MAX_LOW_UTILITY = 1;
        private static readonly string LOW_UTILITY = "QXZJVK";
        private static readonly char[] CONNECTORS = { 'R', 'S', 'T', 'N', 'L', 'D', 'H', 'C', 'M', 'P' };
        private const int MAX_SAME_LETTER = 1;
        private const int PLAYABILITY_THRESHOLD = 3;

        // ══════════════════════════════════════════════════════════════════════════
        // LAYER 3 — Stale-state assist tuning
        // ══════════════════════════════════════════════════════════════════════════

        public const int DROUGHT_TIER1 = 2;
        public const int DROUGHT_TIER2 = 3;
        public const int DROUGHT_TIER3 = 4;
        public const float CLOG_THRESHOLD = 0.65f;

        private const float DROUGHT_CHANCE_T1 = 0.25f;
        private const float DROUGHT_CHANCE_T2 = 0.45f;
        private const float DROUGHT_CHANCE_T3 = 0.65f;

        private const int VOWEL_DRAW_ATTEMPTS = 10;
        private const int REROLL_ATTEMPTS      = 12;
        private static readonly char[] VOWELS = { 'A', 'E', 'I', 'O', 'U' };

        // ══════════════════════════════════════════════════════════════════════════
        // State
        // ══════════════════════════════════════════════════════════════════════════

        private readonly char[] _slots     = new char[MAX_HAND_SIZE];
        // _wildFlags was a parallel bool[] tracking which slots held wilds. It
        // kept desyncing from _slots (shuffles, swaps, window focus, etc.) which
        // caused the wild-becomes-asterisk and wild-disappears-on-shuffle bugs.
        // Replaced with a DERIVED definition: a slot is wild iff its letter IS
        // the wild sentinel (TileBag.WILD_CHAR '*'). Letter and flag can no
        // longer disagree because there's only one source of truth.
        private readonly int    _playerIndex;
        private int  _droughtTurns = 0;
        private char _cachedNextLetter = '\0';

        // Wild Tiles Phase C — pending-wild queue. When true, the next DrawSlot
        // fills the drawn slot with a wild instead of consulting GovernedDraw.
        // Set by InjectWildFromChainReward(); cleared by DrawSlot on consumption.
        private bool _pendingWildInjection = false;

        // Drops-since-wild-injected counter for expiry (3 drops => convert to vowel).
        // Incremented by DrawSlot when a non-wild slot refills while a wild is in hand.
        private int _wildDropsElapsed = 0;

        // Unscaled time the current wild BECAME VISIBLE in hand (i.e. DrawSlot
        // filled the pending injection). Used by HandManager for the 20s time
        // expiry. Owned here so HandManager can't drift out of sync with the
        // actual injection moment (previous bug: stale HandManager-side timestamp
        // from a prior wild caused immediate expiry on the NEXT wild).
        private float _wildVisibleSinceUnscaled = -1f;
        public float WildVisibleSinceUnscaled => _wildVisibleSinceUnscaled;

        public int  PlayerIndex      => _playerIndex;
        public int  DroughtTurns     => _droughtTurns;
        public char CachedNextLetter => _cachedNextLetter;

        public bool IsWildSlot(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return false;
            return _slots[index] == TileBag.WILD_CHAR;
        }

        public bool HasWild
        {
            get
            {
                for (int i = 0; i < HAND_SIZE; i++)
                    if (_slots[i] == TileBag.WILD_CHAR) return true;
                return false;
            }
        }

        public int WildSlotIndex
        {
            get
            {
                for (int i = 0; i < HAND_SIZE; i++)
                    if (_slots[i] == TileBag.WILD_CHAR) return i;
                return -1;
            }
        }

        public bool HasPendingWildInjection => _pendingWildInjection;
        public int  WildDropsElapsed        => _wildDropsElapsed;

        /// <summary>
        /// Queue a wild injection as a chain-reward. The next DrawSlot will fill
        /// that slot as a wild. Rejects if a wild already exists in hand or is
        /// already queued (max 1 wild in hand invariant).
        /// Returns true if queued, false if skipped.
        /// </summary>
        public bool InjectWildFromChainReward()
        {
            if (HasWild || _pendingWildInjection) return false;
            _pendingWildInjection = true;
            return true;
        }

        /// <summary>
        /// Clear a specific wild slot when the player drops it. The slot is blanked
        /// immediately so any resolution abort before bookkeeping cannot leave the
        /// wild sentinel visible as a normal '*' card.
        /// </summary>
        public void ConsumeWildSlot(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_slots[index] != TileBag.WILD_CHAR) return;
            Debug.Log($"[WildExpiry] ConsumeWildSlot FIRED — slot {index} (player dropped their wild)");
            _slots[index]      = '\0';
            _wildDropsElapsed  = 0;
            _wildVisibleSinceUnscaled = -1f;
        }

        /// <summary>
        /// Swap two hand slots. Kept for API compat — callers passed wild flags
        /// separately before, but wildness is now derived from _slots content so
        /// a plain slot swap preserves wild identity automatically.
        /// </summary>
        public void SwapSlotsWithFlags(int a, int b)
        {
            if (a < 0 || a >= HAND_SIZE || b < 0 || b >= HAND_SIZE) return;
            if (a == b) return;
            char cTmp = _slots[a]; _slots[a] = _slots[b]; _slots[b] = cTmp;
        }

        /// <summary>
        /// Apply a full-hand permutation of letters. Second array (wild flags)
        /// ignored — wildness travels with the letter automatically.
        /// </summary>
        public void ReorderSlotsWithFlags(char[] newSlots, bool[] newWildFlags)
        {
            if (newSlots == null) return;
            int n = Mathf.Min(HAND_SIZE, newSlots.Length);
            for (int i = 0; i < n; i++)
                _slots[i] = newSlots[i];
        }

        /// <summary>
        /// Returns a derived wild-flag snapshot. Each entry is true iff that slot's
        /// letter is the wild sentinel. Callers can still use this for ReorderSlotsWithFlags.
        /// </summary>
        public bool[] GetAllWildFlags()
        {
            bool[] flags = new bool[MAX_HAND_SIZE];
            for (int i = 0; i < MAX_HAND_SIZE; i++)
                flags[i] = _slots[i] == TileBag.WILD_CHAR;
            return flags;
        }

        /// <summary>
        /// Convert the current wild slot into a random vowel and clear its flag.
        /// Called by HandManager when the wild's 3-drop / 20s playable-time expiry fires.
        /// No-op if no wild is present.
        /// </summary>
        public void ExpireWildToVowel()
        {
            int idx = WildSlotIndex;
            if (idx < 0) return;
            char[] vowels = VOWELS;
            char pick = vowels[SurvivalRng.Range(0, vowels.Length)];
            // Avoid duplicate collisions when possible
            for (int attempt = 0; attempt < 5; attempt++)
            {
                if (CountLetter(pick) == 0) break;
                pick = vowels[SurvivalRng.Range(0, vowels.Length)];
            }
            Debug.Log($"[WildExpiry] ExpireWildToVowel FIRED — slot {idx} → '{pick}'. _wildDropsElapsed was {_wildDropsElapsed}");
            _slots[idx] = pick;
            _wildDropsElapsed = 0;
            _wildVisibleSinceUnscaled = -1f;
        }

        /// <summary>
        /// Push the wall-clock wild expiry anchor forward while the player cannot act.
        /// Drop-count expiry is intentionally unaffected.
        /// </summary>
        public void DeferWildExpiryTimer(float seconds)
        {
            if (seconds <= 0f) return;
            if (!HasWild || _wildVisibleSinceUnscaled < 0f) return;
            _wildVisibleSinceUnscaled += seconds;
        }

        public void SetCachedNextLetter(char c) { _cachedNextLetter = c; }
        public void EnsureCachedNextLetter(TileBag bag)
        {
            if (_cachedNextLetter == '\0')
                PreCacheNext(bag);
        }

        // Backward compat
        public const int MIN_VOWELS = 1;

        public PlayerHand(int playerIndex)
        {
            _playerIndex = playerIndex;
            for (int i = 0; i < HAND_SIZE; i++)
                _slots[i] = '\0';
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Drought tracking
        // ══════════════════════════════════════════════════════════════════════════

        public void IncrementDrought()
        {
            _droughtTurns++;
        }

        public void ResetDrought()
        {
            _droughtTurns = 0;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Slot access
        // ══════════════════════════════════════════════════════════════════════════

        public char GetSlot(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return '\0';
            return _slots[index];
        }

        public void SetSlot(int index, char letter)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            _slots[index] = letter;
        }

        public char[] GetAllSlots() => (char[])_slots.Clone();

        // ══════════════════════════════════════════════════════════════════════════
        // Fill / Draw / Swap
        // ══════════════════════════════════════════════════════════════════════════

        public void FillAll(TileBag bag)
        {
            if (bag == null) return;
            for (int i = 0; i < HAND_SIZE; i++) _slots[i] = '\0';
            _pendingWildInjection = false;
            _wildDropsElapsed = 0;
            _wildVisibleSinceUnscaled = -1f;
            for (int i = 0; i < HAND_SIZE; i++)
                _slots[i] = GovernedDraw(bag, i);
            PostDrawEnforce();
            PreCacheNext(bag);
//             Debug.Log($"[Hand P{_playerIndex}] FillAll: {HandString()} vowels={CountVowels()} " +
                      // $"playability={CalcPlayability()} next={_cachedNextLetter}");
        }

        public void DrawSlot(int index, TileBag bag)
        {
            DrawSlot(index, bag, countsAsWildDrop: true);
        }

        /// <summary>
        /// Draw a fresh letter into the slot. When countsAsWildDrop=false, this
        /// refill does NOT tick the wild-expiry drops counter — used by rewrite
        /// bookkeeping so editing a board tile doesn't shorten an untouched wild's
        /// life. Rewrites consume a rewrite charge, not a tile-drop.
        /// </summary>
        public void DrawSlot(int index, TileBag bag, bool countsAsWildDrop)
        {
            if (bag == null || index < 0 || index >= HAND_SIZE) return;

            // Clear the slot being refilled. Wildness is derived from _slots so
            // setting _slots[index] = '\0' implicitly clears the wild flag too.
            _slots[index] = '\0';

            // Pending chain-reward wild takes priority over the cached next letter.
            // The cached letter is preserved for the *next* draw.
            if (_pendingWildInjection)
            {
                _slots[index]     = TileBag.WILD_CHAR;
                _pendingWildInjection = false;
                _wildDropsElapsed = 0;
                // Anchor the time-expiry clock exactly when the wild becomes
                // visible — prevents the HandManager-side clock from firing
                // immediately on a new wild due to stale state from a previous one.
                _wildVisibleSinceUnscaled = Time.unscaledTime;
                Debug.Log($"[WildExpiry] Wild INJECTED into slot {index} at t={Time.unscaledTime:F1}s");
                PostDrawEnforce();
                // Do NOT re-cache here; PreCacheNext will recompute against the new hand.
                PreCacheNext(bag);
                return;
            }

            // If another slot still holds a wild, this draw counts toward its expiry
            // — BUT only if the caller says this refill is a genuine tile-drop.
            // Rewrites pass countsAsWildDrop=false so editing a board tile doesn't
            // shorten an untouched wild's life.
            if (HasWild && countsAsWildDrop)
            {
                _wildDropsElapsed++;
                Debug.Log($"[WildExpiry] Non-wild drop refilled slot {index} — wild dropsElapsed now {_wildDropsElapsed}/3");
            }

            if (_cachedNextLetter != '\0')
            {
                // The preview is a contract with the player: whatever the next-tile
                // socket showed must be the letter that lands in the hand. Do NOT
                // re-validate against the current hand state here — any vowel/dupe
                // adjustments happen in the next PreCacheNext, never on this draw.
                _slots[index] = _cachedNextLetter;
                _cachedNextLetter = '\0';
            }
            else
            {
                _slots[index] = GovernedDraw(bag, index);
            }
            // No post-draw vowel forcing — GovernedDraw handles vowel balance
            // during the draw itself. Tough hands are part of the game.

            PostDrawEnforce();
            PreCacheNext(bag);

//             Debug.Log($"[Hand P{_playerIndex}] DrawSlot({index}): {HandString()} " +
                      // $"playability={CalcPlayability()} drought={_droughtTurns} next={_cachedNextLetter}");
        }

        public char SwapSlot(int index, TileBag bag)
        {
            if (bag == null || index < 0 || index >= HAND_SIZE) return '\0';
            // Swap cannot target a wild — hand-to-board swap UI already gates this,
            // but defend the invariant here too.
            if (_slots[index] == TileBag.WILD_CHAR) return '\0';
            char old = _slots[index];
            _slots[index] = '\0';

            // Swap uses a fair draw — no vowel forcing, no drought assist.
            // Scripted determinism: if the bag has a rigged letter, use it
            // unconditionally (tutorials may script swap results later).
            char replacement = '\0';
            if (bag.HasRiggedNext)
            {
                replacement = bag.DrawLetter();
            }
            else
            {
                for (int attempt = 0; attempt < REROLL_ATTEMPTS; attempt++)
                {
                    char drawn = bag.DrawLetter();
                    if (char.ToUpper(drawn) == char.ToUpper(old)) continue;
                    if (CountLetter(drawn) >= MAX_SAME_LETTER) continue;
                    if (IsLowUtility(drawn) && CountLowUtilityExcluding(index) >= MAX_LOW_UTILITY) continue;
                    replacement = drawn;
                    break;
                }
            }

            _slots[index] = (replacement != '\0') ? replacement : bag.DrawLetter();
            // Don't change NEXT tile on swap — preserve what the player was shown
            // PreCacheNext(bag);  // removed: swap should not alter the next tile preview
            return old;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // THREE-LAYER GOVERNED DRAW
        // ══════════════════════════════════════════════════════════════════════════

        // Survival board-aware draw — the board should almost always be playable.
        // 70% of draws try to give a letter that could form a word somewhere on the board.
        // The player still has to FIND and PLACE the word — the skill is intact.
        // Scales up further during drought so the player never gets completely stuck.
        // EXPERIMENT: reduced from 0.55/0.85 — revert if game feels too hard
        private const float SURVIVAL_BOARD_ASSIST_BASE    = 0.38f; // was 0.55
        private const float SURVIVAL_BOARD_ASSIST_DROUGHT = 0.85f; // kept — drought should still help

        private char GovernedDraw(TileBag bag, int slotIndex)
        {
            // Tutorial / scripted determinism: when the bag has a rigged
            // letter queued (from LevelData scriptedInitialHand /
            // scriptedDrawQueue / bag.letterOverrides), bypass every
            // governance filter so the rigged letter can't be rejected by
            // vowel-ceiling, low-utility, or connector gates. Without this
            // bypass, GovernedDraw's rejection loops would drain the rig
            // queue silently on filter misses, breaking tutorials.
            if (bag != null && bag.HasRiggedNext)
                return bag.DrawLetter();

            int vowelsInHand = CountVowelsExcluding(slotIndex);

            // ── VOWEL-FLOOR PRIORITY ─────────────────────────────────────────────
            // When the hand is below the vowel floor (i.e., 0 vowels in Survival)
            // and this is the last-chance slot to get a vowel, prioritize a vowel
            // draw BEFORE any other governance layer. Up to N bag attempts —
            // if no vowel comes out, fall through to the normal layers. No
            // synthesized fallback: the NEXT preview must always reflect a real
            // bag draw, otherwise the cached letter and the bag desync and the
            // preview lies to the player.
            //
            // For PreCacheNext (slotIndex = -1) the hand is always full so
            // unfilled = 0 and this gate fires whenever vowelsInHand < FLOOR —
            // exactly the "next-in-line after NEXT is dealt" slot the player
            // expects to be biased toward a vowel when they're tapped out.
            if (vowelsInHand < VOWEL_FLOOR)
            {
                int unfilled = 0;
                for (int i = Mathf.Max(0, slotIndex); i < HAND_SIZE; i++)
                    if (_slots[i] == '\0') unfilled++;

                if (VOWEL_FLOOR - vowelsInHand >= unfilled)
                {
                    for (int a = 0; a < VOWEL_DRAW_ATTEMPTS; a++)
                    {
                        char drawn = bag.DrawLetter();
                        if (IsVowel(drawn) && CountLetter(drawn) < MAX_SAME_LETTER)
                            return drawn;
                    }
                    // No vowel found — fall through. Bag stays in sync with preview.
                }
            }

            // ── SURVIVAL LAYER: Board-aware draw as DEFAULT behavior ────────────
            // Skipped entirely under NoAssistMode — raw bag draws only.
            if (SurvivalManager.IsSurvivalMode && !SurvivalManager.NoAssistMode)
            {
                // Post-clear boost: after big detonations, ramp board-assist to 95%
                // so the next few draws reconnect the board instead of random fill
                bool postClearBoosted = SurvivalManager.Instance != null && SurvivalManager.Instance.IsPostClearBoosted;
                float chance = postClearBoosted ? 0.85f
                    : (_droughtTurns >= 2 ? SURVIVAL_BOARD_ASSIST_DROUGHT : SURVIVAL_BOARD_ASSIST_BASE);
                if (SurvivalRng.Value < chance)
                {
                    char helper = DroughtAssist.GetHelperLetter();
                    if (helper != '\0' && CountLetter(helper) < MAX_SAME_LETTER)
                    {
                        if (!IsVowel(helper) || vowelsInHand < VOWEL_CEILING)
                            return helper;
                    }
                }
                // Board-assist didn't find anything — fall through to normal draw
            }

            // ── LAYER 3: Stale-state assist (Classic/Blitz/Daily) ───────────────
            // Skipped under NoAssistMode — no drought-tier rescue.
            int effectiveDrought = _droughtTurns;
            if (RulesEngine.Instance != null)
            {
                float occupancy = RulesEngine.Instance.GetBoardOccupancy();
                if (occupancy >= CLOG_THRESHOLD && _droughtTurns >= 1)
                    effectiveDrought++;
            }

            if (!SurvivalManager.NoAssistMode && effectiveDrought >= DROUGHT_TIER1)
            {
                float chance;
                if (effectiveDrought >= DROUGHT_TIER3)      chance = DROUGHT_CHANCE_T3;
                else if (effectiveDrought >= DROUGHT_TIER2)  chance = DROUGHT_CHANCE_T2;
                else                                          chance = DROUGHT_CHANCE_T1;

                if (SurvivalRng.Value < chance)
                {
                    char helper = DroughtAssist.GetHelperLetter();
                    if (helper != '\0' && CountLetter(helper) < MAX_SAME_LETTER)
                    {
                        // Respect vowel ceiling even for drought assist
                        if (!IsVowel(helper) || vowelsInHand < VOWEL_CEILING)
                        {
//                             Debug.Log($"[Hand P{_playerIndex}] L3 board-assist: '{helper}' (drought={_droughtTurns})");
                            return helper;
                        }
                    }
                }
            }

            // ── LAYER 2: Hand-aware refill ──────────────────────────────────────
            // (Vowel floor moved to top of GovernedDraw so it pre-empts the
            // Survival board-assist layer when the hand is tapped out of vowels.)

            // 2b. Calculate hand state
            int playability    = CalcPlayabilityExcluding(slotIndex);
            bool needsConnector = (playability < PLAYABILITY_THRESHOLD);
            bool clampLowUtil   = (CountLowUtilityExcluding(slotIndex) >= MAX_LOW_UTILITY);
            bool vowelsFull     = (vowelsInHand >= VOWEL_CEILING);

            // 2c. Draw with rejection loop
            for (int a = 0; a < REROLL_ATTEMPTS; a++)
            {
                char drawn = bag.DrawLetter();

                // Reject low-utility if at max
                if (clampLowUtil && IsLowUtility(drawn)) continue;

                // Reject duplicates beyond max
                if (CountLetter(drawn) >= MAX_SAME_LETTER) continue;

                // Reject vowels if hand already has enough
                if (vowelsFull && IsVowel(drawn)) continue;

                // If hand needs a connector, reject non-connectors (including vowels) sometimes
                if (needsConnector && !IsConnector(drawn) && SurvivalRng.Value < 0.5f)
                    continue;

                return drawn;
            }

            // 2d. Fallback: force a connector we don't have
            for (int i = 0; i < CONNECTORS.Length; i++)
            {
                if (CountLetter(CONNECTORS[i]) < MAX_SAME_LETTER)
                    return CONNECTORS[i];
            }

            // ── LAYER 1: Raw bag draw ───────────────────────────────────────────
            return bag.DrawLetter();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // POST-DRAW ENFORCEMENT (safety net — minimal)
        // ══════════════════════════════════════════════════════════════════════════

        private void PostDrawEnforce()
        {
            // Low-utility enforcement REMOVED — was corrupting existing hand cards.
            // GovernedDraw already blocks low-utility letters during draw, and
            // the vowel-floor priority pass runs at the top of GovernedDraw.
        }

        // ══════════════════════════════════════════════════════════════════════════
        // PLAYABILITY SCORE
        // ══════════════════════════════════════════════════════════════════════════

        private int CalcPlayability()
        {
            return CalcPlayabilityCore(-1);
        }

        private int CalcPlayabilityExcluding(int excludeSlot)
        {
            return CalcPlayabilityCore(excludeSlot);
        }

        private int CalcPlayabilityCore(int excludeSlot)
        {
            int score = 0;
            int vowelCount = 0;
            int connectorCount = 0;
            int otherConsonants = 0;
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (i == excludeSlot) continue;
                char c = _slots[i];
                if (c == '\0') continue;
                if (IsVowel(c)) vowelCount++;
                else if (IsConnector(c)) connectorCount++;
                else if (IsLowUtility(c)) score--;
                else otherConsonants++;
            }
            // Vowels: good at 1-2, penalize 0 and 3+
            if (vowelCount == 0) score -= 2;
            else if (vowelCount <= VOWEL_CEILING) score += vowelCount;
            else score += VOWEL_CEILING - (vowelCount - VOWEL_CEILING); // diminishing, then negative

            // Consonant diversity matters more than vowel count
            score += connectorCount;
            score += otherConsonants; // non-low-utility, non-connector consonants still useful
            if (connectorCount == 0 && otherConsonants == 0) score -= 2;
            return score;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // NEXT-TILE CACHE
        // ══════════════════════════════════════════════════════════════════════════

        private void PreCacheNext(TileBag bag)
        {
            if (bag == null) { _cachedNextLetter = '\0'; return; }
            // Use slot -1 so CountVowelsExcluding counts ALL current slots, giving
            // GovernedDraw an accurate picture of the hand post-draw. This is the
            // ONLY point where hand-state rules can steer the preview — once cached,
            // DrawSlot commits the cached letter unconditionally to preserve the
            // "what you see is what you deal" contract with the player.
            _cachedNextLetter = GovernedDraw(bag, -1);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Utility helpers
        // ══════════════════════════════════════════════════════════════════════════

        private int CountLetter(char letter)
        {
            int count = 0;
            char upper = char.ToUpper(letter);
            for (int i = 0; i < HAND_SIZE; i++)
                if (char.ToUpper(_slots[i]) == upper) count++;
            return count;
        }

        private static bool IsVowel(char c)
        {
            if (c == '\0') return false;
            c = char.ToUpper(c);
            return c == 'A' || c == 'E' || c == 'I' || c == 'O' || c == 'U';
        }

        private static bool IsLowUtility(char c)
        {
            return LOW_UTILITY.IndexOf(char.ToUpper(c)) >= 0;
        }

        private static bool IsConnector(char c)
        {
            c = char.ToUpper(c);
            for (int i = 0; i < CONNECTORS.Length; i++)
                if (CONNECTORS[i] == c) return true;
            return false;
        }

        private int CountVowels()
        {
            int count = 0;
            for (int i = 0; i < HAND_SIZE; i++)
                if (IsVowel(_slots[i])) count++;
            return count;
        }

        private int CountVowelsExcluding(int excludeSlot)
        {
            int count = 0;
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (i == excludeSlot) continue;
                if (IsVowel(_slots[i])) count++;
            }
            return count;
        }

        private int CountLowUtility()
        {
            int count = 0;
            for (int i = 0; i < HAND_SIZE; i++)
                if (IsLowUtility(_slots[i])) count++;
            return count;
        }

        private int CountLowUtilityExcluding(int excludeSlot)
        {
            int count = 0;
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (i == excludeSlot) continue;
                if (IsLowUtility(_slots[i])) count++;
            }
            return count;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // Display
        // ══════════════════════════════════════════════════════════════════════════

        public string HandString()
        {
            var sb = new System.Text.StringBuilder(HAND_SIZE);
            for (int i = 0; i < HAND_SIZE; i++)
            {
                char c = _slots[i];
                sb.Append(c == '\0' ? '∅' : c);
            }
            return sb.ToString();
        }
    }
}
