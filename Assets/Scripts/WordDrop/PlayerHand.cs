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
        public const int HAND_SIZE = 5;

        // ══════════════════════════════════════════════════════════════════════════
        // LAYER 2 — Hand-aware refill tuning
        // ══════════════════════════════════════════════════════════════════════════

        // Vowel band: target 2 vowels in a 5-card hand
        // 0-1 vowels: force one if last chance
        // 2 vowels: ideal
        // 3+ vowels: actively reject
        private const int VOWEL_FLOOR   = 2;   // guarantee at least 2 vowels
        private const int VOWEL_CEILING = 3;   // reject vowels at or above this count

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

        private readonly char[] _slots = new char[HAND_SIZE];
        private readonly int    _playerIndex;
        private int  _droughtTurns = 0;
        private char _cachedNextLetter = '\0';

        public int  PlayerIndex      => _playerIndex;
        public int  DroughtTurns     => _droughtTurns;
        public char CachedNextLetter => _cachedNextLetter;
        public void SetCachedNextLetter(char c) { _cachedNextLetter = c; }

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
            if (_droughtTurns == DROUGHT_TIER1 || _droughtTurns == DROUGHT_TIER2 || _droughtTurns == DROUGHT_TIER3)
                Debug.Log($"[Hand P{_playerIndex}] Drought tier reached: {_droughtTurns} turns");
        }

        public void ResetDrought()
        {
            if (_droughtTurns > 0)
                Debug.Log($"[Hand P{_playerIndex}] Drought ended after {_droughtTurns} turns");
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
            for (int i = 0; i < HAND_SIZE; i++)
                _slots[i] = '\0';
            for (int i = 0; i < HAND_SIZE; i++)
                _slots[i] = GovernedDraw(bag, i);
            PostDrawEnforce();
            PreCacheNext(bag);
            Debug.Log($"[Hand P{_playerIndex}] FillAll: {HandString()} vowels={CountVowels()} " +
                      $"playability={CalcPlayability()} next={_cachedNextLetter}");
        }

        public void DrawSlot(int index, TileBag bag)
        {
            if (bag == null || index < 0 || index >= HAND_SIZE) return;
            _slots[index] = '\0';

            if (_cachedNextLetter != '\0')
            {
                _slots[index] = _cachedNextLetter;
                _cachedNextLetter = '\0';
            }
            else
            {
                _slots[index] = GovernedDraw(bag, index);
            }
            PostDrawEnforce();
            PreCacheNext(bag);

            Debug.Log($"[Hand P{_playerIndex}] DrawSlot({index}): {HandString()} " +
                      $"playability={CalcPlayability()} drought={_droughtTurns} next={_cachedNextLetter}");
        }

        public char SwapSlot(int index, TileBag bag)
        {
            if (bag == null || index < 0 || index >= HAND_SIZE) return '\0';
            char old = _slots[index];
            _slots[index] = '\0';

            // Swap uses a fair draw — no vowel forcing, no drought assist.
            char replacement = '\0';
            for (int attempt = 0; attempt < REROLL_ATTEMPTS; attempt++)
            {
                char drawn = bag.DrawLetter();
                if (char.ToUpper(drawn) == char.ToUpper(old)) continue;
                if (CountLetter(drawn) >= MAX_SAME_LETTER) continue;
                if (IsLowUtility(drawn) && CountLowUtilityExcluding(index) >= MAX_LOW_UTILITY) continue;
                replacement = drawn;
                break;
            }

            _slots[index] = (replacement != '\0') ? replacement : bag.DrawLetter();
            // Don't change NEXT tile on swap — preserve what the player was shown
            // PreCacheNext(bag);  // removed: swap should not alter the next tile preview
            return old;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // THREE-LAYER GOVERNED DRAW
        // ══════════════════════════════════════════════════════════════════════════

        private char GovernedDraw(TileBag bag, int slotIndex)
        {
            int vowelsInHand = CountVowelsExcluding(slotIndex);

            // ── LAYER 3: Stale-state assist ─────────────────────────────────────
            int effectiveDrought = _droughtTurns;
            if (RulesEngine.Instance != null)
            {
                float occupancy = RulesEngine.Instance.GetBoardOccupancy();
                if (occupancy >= CLOG_THRESHOLD && _droughtTurns >= 1)
                    effectiveDrought++;
            }

            if (effectiveDrought >= DROUGHT_TIER1)
            {
                float chance;
                if (effectiveDrought >= DROUGHT_TIER3)      chance = DROUGHT_CHANCE_T3;
                else if (effectiveDrought >= DROUGHT_TIER2)  chance = DROUGHT_CHANCE_T2;
                else                                          chance = DROUGHT_CHANCE_T1;

                if (Random.value < chance)
                {
                    char helper = DroughtAssist.GetHelperLetter();
                    if (helper != '\0' && CountLetter(helper) < MAX_SAME_LETTER)
                    {
                        // Respect vowel ceiling even for drought assist
                        if (!IsVowel(helper) || vowelsInHand < VOWEL_CEILING)
                        {
                            Debug.Log($"[Hand P{_playerIndex}] L3 board-assist: '{helper}' (drought={_droughtTurns})");
                            return helper;
                        }
                    }
                }
            }

            // ── LAYER 2: Hand-aware refill ──────────────────────────────────────

            // 2a. Vowel floor — only force a vowel when hand has ZERO vowels
            //     and remaining unfilled slots can't naturally provide one
            if (vowelsInHand < VOWEL_FLOOR)
            {
                int unfilled = 0;
                for (int i = slotIndex; i < HAND_SIZE; i++)
                    if (_slots[i] == '\0') unfilled++;

                // Only force if this is the last chance to get a vowel
                if (VOWEL_FLOOR - vowelsInHand >= unfilled)
                {
                    for (int a = 0; a < VOWEL_DRAW_ATTEMPTS; a++)
                    {
                        char drawn = bag.DrawLetter();
                        if (IsVowel(drawn) && CountLetter(drawn) < MAX_SAME_LETTER)
                            return drawn;
                    }
                    // Hard fallback: pick a vowel we don't have
                    for (int v = 0; v < VOWELS.Length; v++)
                        if (CountLetter(VOWELS[v]) < MAX_SAME_LETTER)
                            return VOWELS[v];
                }
            }

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
                if (needsConnector && !IsConnector(drawn) && Random.value < 0.5f)
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
            // Only enforce low-utility max. Vowel balance is handled by GovernedDraw.
            // No vowel replacement here — that was causing overcorrection.
            int lowCount = CountLowUtility();
            if (lowCount > MAX_LOW_UTILITY)
            {
                int excess = lowCount - MAX_LOW_UTILITY;
                for (int i = HAND_SIZE - 1; i >= 0 && excess > 0; i--)
                {
                    if (IsLowUtility(_slots[i]))
                    {
                        _slots[i] = CONNECTORS[Random.Range(0, CONNECTORS.Length)];
                        excess--;
                    }
                }
            }
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
            _cachedNextLetter = GovernedDraw(bag, 0);
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
