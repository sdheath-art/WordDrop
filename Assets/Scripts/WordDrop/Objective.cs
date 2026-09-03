using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Base for a level objective (2026-06-08): holds the goal, tracks progress from gameplay
    /// events, decides completion, and surfaces a short HUD label. Each objective TYPE is a
    /// small plug-in subclass; ObjectiveManager owns the active instance and feeds it events.
    /// The win-check (Objective.IsComplete) is what replaces the old "score >= target"
    /// condition — see project_wordrop_objective_direction. Override only the hooks a given
    /// objective needs; the rest are no-ops.
    /// </summary>
    public abstract class Objective
    {
        /// <summary>Short HUD title, e.g. "5+ letter words".</summary>
        public abstract string Title { get; }

        /// <summary>Progress readout, e.g. "1 / 3".</summary>
        public abstract string ProgressText { get; }

        /// <summary>True once the goal is met. ObjectiveManager fires the win on the rising edge.</summary>
        public abstract bool IsComplete { get; }

        /// <summary>Friendly "what's left" phrase for the CONTINUE screen ("So close! You only have…"),
        /// e.g. "2 ice tiles to clear". Defaults to the progress readout. 2026-06-15 Spencer.</summary>
        public virtual string RemainingText => ProgressText;

        /// <summary>HUD target ICON for the Royal-Match-style "TARGET" panel — the player sees a picture
        /// of WHAT they need plus a remaining-count badge, instead of reading a sentence. 2026-06-15 Spencer.
        ///   DropTarget = amber escort tile · Word = primed "WORD" tile cluster · Ice = frosted tile · Vault = chest.</summary>
        public enum HudIcon { None, DropTarget, Word, Ice, Vault, HiddenWord }
        public virtual HudIcon Icon => HudIcon.None;

        /// <summary>How many are still LEFT (drives the badge number on the Target panel). 0 by default.</summary>
        public virtual int RemainingCount => 0;

        /// <summary>For the Word icon: which word the tile-cluster spells. Length = number of mini-tiles,
        /// so the icon literally shows how many letters are required (FOUR = 4 tiles, WORDS = 5).
        /// 2026-06-17 Spencer.</summary>
        public virtual string IconWord => "WORD";

        /// <summary>Verbose, INSTRUCTIVE one-liner for the PRE-LEVEL modal — more spelled-out than the
        /// terse HUD Title (e.g. "Explode 3 words" vs the HUD's "Explode words"), so a fresh player isn't
        /// confused about the goal. Defaults to Title. 2026-06-15 Spencer.</summary>
        public virtual string IntroDescription => Title;

        // ── Event hooks — ObjectiveManager calls these. Override what you need. ──
        /// <summary>A word was scored/PRIMED (made). Use for "make N words" goals.</summary>
        public virtual void OnWordScored(WordScoredEvent evt) { }
        /// <summary>A primed word's tiles EXPLODED — fires for every word destroyed in a blast,
        /// whether it was the trigger, the triggered, or swept in via the connected-group /
        /// splash sweep. Use for "explode N words" goals; priming is trivial, detonating is the
        /// real achievement. Called once per exploded word. 2026-06-08.</summary>
        public virtual void OnWordExploded(string word, int ownerPlayerIndex) { }
        /// <summary>One detonation blew up N charged words AT ONCE (the combo size). Use for combo goals.
        /// Fired once per detonation via ObjectiveManager.NotifyComboDetonated. 2026-07-06.</summary>
        public virtual void OnComboDetonated(int chargedWordsInBlast) { }
        public virtual void OnTilesExploded(List<Vector2Int> cells) { }
        /// <summary>A drop-target tile reached the bottom row and was collected (count this tick).
        /// Use for "bring N to the bottom" / hero-word goals.</summary>
        public virtual void OnDropTargetCollected(int count) { }
        /// <summary>Per-frame hook from ObjectiveManager — for objectives that need to set up board
        /// state once it's ready (e.g. spawn drop-targets) or poll. No-op by default.</summary>
        public virtual void Tick() { }
        public virtual void Reset() { }

        // ── Level-rule hints — SurvivalManager reads these to shape the level. ──
        /// <summary>True if this objective's levels run with rising rows OFF (e.g. vault/loot
        /// levels, whose pressure is a move budget instead). Default: rises on. 2026-06-09.</summary>
        public virtual bool RisesOff => false;
        /// <summary>True if this level runs on a flat MOVE BUDGET instead of the rise/top-out clock
        /// (vault/loot levels). SurvivalManager uses _vaultMoveBudget. 2026-06-09.</summary>
        public virtual bool UsesMoveCap => false;
    }

    /// <summary>
    /// "Explode N words of L+ letters." Counts DETONATIONS, not primes — priming a long word
    /// is trivial, but detonating one requires the full setup→trigger→cascade play, so this is
    /// the real achievement (Spencer, 2026-06-08). Hooks OnWordDetonated (PrimedTriggeredEvent).
    /// </summary>
    public sealed class LongWordObjective : Objective
    {
        private readonly int _minLen;
        private readonly int _goal;
        private int _count;

        private readonly string _customIntro; // per-level GOAL-text override (tutorial/authored); null = auto-generate

        public LongWordObjective(int minLen, int goal, string customIntro = null)
        {
            _minLen = Mathf.Max(2, minLen);
            _goal   = Mathf.Max(1, goal);
            _customIntro = customIntro;
        }

        /// <summary>Minimum word length that counts toward this goal — read by the WordDropFX fly-up
        /// so a qualifying detonation sends its letters up to the objective icon. 2026-07-06 Spencer.</summary>
        public int MinLen => _minLen;

        public override string Title        => _minLen <= 2 ? "Pop words" : $"Pop {_minLen}+ letter words";
        public override string ProgressText => $"{Mathf.Min(_count, _goal)} / {_goal}";
        public override bool   IsComplete   => _count >= _goal;
        public override string RemainingText { get { int r = Mathf.Max(0, _goal - _count); return $"{r} more word{(r == 1 ? "" : "s")} to pop"; } }
        public override HudIcon Icon         => HudIcon.Word;
        // Icon spells a word whose LENGTH = the required letter count: 5+ → "WORDS" (5 tiles),
        // 4 → "FOUR" (4 tiles), generic "explode words" → "WORD". 2026-06-17 Spencer.
        public override string IconWord      => _minLen >= 5 ? "WORDS" : _minLen >= 4 ? "FOUR" : "WORD";
        public override int     RemainingCount => Mathf.Max(0, _goal - _count);
        public override string  IntroDescription =>
            !string.IsNullOrEmpty(_customIntro) ? _customIntro
            : (_minLen <= 2 ? $"Pop {_goal} words!"
                            : $"Pop {_goal} words of {_minLen}+ letters!");

        // Counts a word whenever its tiles actually blow up — doesn't matter if it was the
        // primed word that got set off or the word that set it off; if it explodes, it counts.
        public override void OnWordExploded(string word, int ownerPlayerIndex)
        {
            if (IsComplete) return;
            if (ownerPlayerIndex == MatchController.PLAYER_HUMAN
                && !string.IsNullOrEmpty(word) && word.Length >= _minLen)
                _count++;
        }

        public override void Reset() => _count = 0;
    }

    /// <summary>
    /// "Blow up N words in ONE detonation" — the COMBO. Completes on the first single blast that
    /// detonates _target+ charged words at once (a stacked cluster set off together), so separate one-word
    /// pops do NOT satisfy it. Progress shows the best combo reached so far. Fed by RulesEngine.DoExplode
    /// → ObjectiveManager.NotifyComboDetonated. 2026-07-06 Spencer.
    /// </summary>
    public sealed class ComboObjective : Objective
    {
        private readonly int _target;
        private readonly string _customIntro;
        private int  _best;
        private bool _done;

        public ComboObjective(int target, string customIntro = null)
        {
            _target = Mathf.Max(2, target);
            _customIntro = customIntro;
        }

        public override string  Title          => $"Combo {_target} words";
        public override string  ProgressText   => $"{Mathf.Min(_best, _target)} / {_target}";
        public override bool     IsComplete     => _done;
        public override HudIcon  Icon           => HudIcon.Word;
        public override string   IconWord       => "WORD";
        public override int      RemainingCount => _done ? 0 : _target;
        public override string   RemainingText  => _done ? "combo!" : $"blow up {_target} words at once";
        public override string   IntroDescription =>
            !string.IsNullOrEmpty(_customIntro) ? _customIntro : $"Blow up {_target} words in one combo!";

        /// <summary>A single blast just detonated <paramref name="chargedWordsInBlast"/> charged words.
        /// Completes on the first blast that reaches the target; tracks the best for the progress readout.</summary>
        public override void OnComboDetonated(int chargedWordsInBlast)
        {
            if (chargedWordsInBlast > _best) _best = chargedWordsInBlast;
            if (chargedWordsInBlast >= _target) _done = true;
        }

        public override void Reset() { _best = 0; _done = false; }
    }

    /// <summary>
    /// "Find the hidden word." A mystery word shows at the top as blanks. Whenever the player explodes
    /// a word, EVERY letter in it that matches a still-hidden slot of the mystery word is revealed (each
    /// letter drops into its own correct position). The level passes when the whole word is uncovered.
    /// 2026-06-17 Spencer. THIN SLICE: instant reveal via the HUD text fallback (Icon = None → masked
    /// word shown as ProgressText); the letter-fly-up animation + dedicated slot widget come later.
    /// </summary>
    public sealed class HiddenWordObjective : Objective
    {
        private readonly string _target;    // mystery word, UPPERCASE
        private readonly bool[] _revealed;  // per-slot reveal state
        private readonly bool[] _claimed;   // a fly-up has already been launched for this slot (independent of reveal order)

        public HiddenWordObjective(string target)
        {
            _target   = string.IsNullOrEmpty(target) ? "WORD" : target.ToUpperInvariant();
            _revealed = new bool[_target.Length];
            _claimed  = new bool[_target.Length];
        }

        /// <summary>The mystery word (for the future slot widget / fly-up animation).</summary>
        public string Target => _target;
        public bool IsSlotRevealed(int i) => i >= 0 && i < _revealed.Length && _revealed[i];

        public override string Title => "Hidden word";
        // Masked readout: "S _ A _" → "S T A R" as letters fill in. Drives the HUD text fallback.
        public override string ProgressText
        {
            get
            {
                var sb = new System.Text.StringBuilder(_target.Length * 2);
                for (int i = 0; i < _target.Length; i++)
                {
                    if (i > 0) sb.Append(' ');
                    sb.Append(_revealed[i] ? _target[i] : '_');
                }
                return sb.ToString();
            }
        }
        public override bool   IsComplete
        {
            get { for (int i = 0; i < _revealed.Length; i++) if (!_revealed[i]) return false; return _revealed.Length > 0; }
        }
        public override int    RemainingCount { get { int c = 0; for (int i = 0; i < _revealed.Length; i++) if (!_revealed[i]) c++; return c; } }
        public override string IntroDescription => "Pop words to uncover the hidden word at the top!";

        // Standard Target panel (icon + count badge), same as the other modes. The icon is a row of black
        // rocks — one per blank — with revealed letters showing in place. IconWord carries the MASKED state
        // ('_' = blank) so the HUD icon-cache rebuilds the cluster each time a slot is filled (rock → letter).
        public override HudIcon Icon => HudIcon.HiddenWord;
        public override string IconWord
        {
            get
            {
                var sb = new System.Text.StringBuilder(_target.Length);
                for (int i = 0; i < _target.Length; i++) sb.Append(_revealed[i] ? _target[i] : '_');
                return sb.ToString();
            }
        }

        // Every matching letter in the exploded word reveals its slot. Duplicates handled naturally:
        // each letter fills the FIRST still-blank slot of that letter, so a 2nd 'E' finds none once the
        // word's single 'E' slot is taken. 2026-06-17 Spencer.
        // Reveal is driven by the fly-up LANDING (HUDManager → RevealSlot), not here — so the slot fills
        // exactly when its letter arrives, and so it stays consistent with the flight (which fires for EVERY
        // matching detonated tile, including splash-cleared ones, per "any exploded letter counts"). 2026-06-17.
        public override void OnWordExploded(string word, int ownerPlayerIndex) { }

        /// <summary>Marks a slot revealed — called when its fly-up letter lands. 2026-06-17 Spencer.</summary>
        public void RevealSlot(int i)
        {
            if (i >= 0 && i < _revealed.Length) _revealed[i] = true;
        }

        public override void Reset()
        {
            for (int i = 0; i < _revealed.Length; i++) { _revealed[i] = false; _claimed[i] = false; }
        }

        /// <summary>CLAIMS the first unclaimed slot this letter fills and returns its index (-1 if none).
        /// Claiming is permanent + independent of the reveal, so a fly-up fires exactly once per slot no
        /// matter whether the reveal or the explosion FX runs first. Called at EXPLODE time. 2026-06-17.</summary>
        public int ClaimSlotForLetter(char letter)
        {
            char ch = char.ToUpperInvariant(letter);
            for (int i = 0; i < _target.Length; i++)
                if (!_claimed[i] && _target[i] == ch) { _claimed[i] = true; return i; }
            return -1;
        }

        /// <summary>Distinct letters for slots still NEEDED (not yet claimed by a fly-up). The completability
        /// safety net rigs the bag toward any of these that's gone scarce. 2026-06-17 Spencer.</summary>
        public System.Collections.Generic.List<char> NeededLetters()
        {
            var set = new System.Collections.Generic.HashSet<char>();
            for (int i = 0; i < _target.Length; i++)
                if (!_claimed[i]) set.Add(_target[i]);
            return new System.Collections.Generic.List<char>(set);
        }
    }

    /// <summary>
    /// "Bring N to the bottom" — the flagship hero-word / drop-to-bottom goal (Candy-Crush ingredient
    /// drop). Spawns N drop-target tiles once the board is ready; the player clears beneath them so
    /// gravity escorts them to row 0, where ObjectiveManager collects them. Feel-tested positive as a
    /// prototype (2026-06-01); ported into the framework 2026-06-09.
    /// </summary>
    public sealed class HeroWordObjective : Objective
    {
        private readonly int _goal;
        private int  _collected;
        private bool _spawned;

        public HeroWordObjective(int goal) => _goal = Mathf.Max(1, goal);

        public override string Title        => $"Drop {_goal} to the bottom";
        public override string ProgressText => $"{Mathf.Min(_collected, _goal)} / {_goal}";
        public override bool   IsComplete   => _collected >= _goal;
        public override string RemainingText { get { int r = Mathf.Max(0, _goal - _collected); return $"{r} more to drop to the bottom"; } }
        public override HudIcon Icon         => HudIcon.DropTarget;
        public override int     RemainingCount => Mathf.Max(0, _goal - _collected);
        public override string  IntroDescription => $"Drop {_goal} rubber chicken{(_goal == 1 ? "" : "s")} down to the bottom row!";

        public override void OnDropTargetCollected(int count)
        {
            if (!IsComplete) _collected += count;
        }

        // Spawn the targets once the board actually has tiles to sit on.
        public override void Tick()
        {
            if (_spawned || RulesEngine.Instance == null) return;
            if (RulesEngine.Instance.GetBoardOccupancy() <= 0f) return; // board not seeded yet
            RulesEngine.Instance.SpawnDropTargetsForTest(_goal);
            GridManager.Instance?.SyncToRulesState(RulesEngine.Instance);
            _spawned = true;
        }

        public override void Reset()
        {
            _collected = 0;
            _spawned   = false;
        }
    }

    /// <summary>
    /// TREASURE-VAULT LOOT level — a NO-FAIL triage beat (NOT a "crack them all" challenge).
    /// Seeds a board of N fixed treasure vaults (more than the short move budget can crack); the
    /// player triages WHICH chests to spend moves on, gambling moves to reach the high SPECIAL one.
    /// Vaults don't fall/rise, render as chests, are immune to the Rock Crusher, and crack via the
    /// stone-splash (adjacent detonation). The level ENDS on moves=0 (or all looted) → bank →
    /// advance, and NEVER ends the run (SurvivalManager.CheckStageClear handles that). So this is
    /// NOT a win-condition objective — IsComplete stays false; it just tracks loot for the HUD.
    /// 2026-06-09.
    /// </summary>
    public sealed class VaultObjective : Objective
    {
        private readonly int _goal;
        private int  _spawnedCount; // vaults actually placed (board may host fewer than requested)
        private int  _remaining;
        private bool _spawned;
        private int  _rewardCoins;  // running total of coins earned this level (chests cracked → coins)

        public VaultObjective(int goal) => _goal = Mathf.Max(1, goal);

        // Coins awarded per chest by tier (RequiredWordLength key). Tunable. 2026-06-18 Spencer.
        public const int COIN_REGULAR = 5;
        public const int COIN_MID     = 15;
        public const int COIN_HIGH    = 40;
        public static int CoinsForTier(int requiredLen)
            => requiredLen <= 0 ? COIN_REGULAR : (requiredLen >= 5 ? COIN_HIGH : COIN_MID);

        /// <summary>Running total of reward coins earned this level (shown in the HUD badge). Stage 2
        /// will tick this up as coins LAND on the reward icon; for now it accrues as chests crack.</summary>
        public int RewardCoins => _rewardCoins;
        public void AddRewardCoins(int coins) { if (coins > 0) _rewardCoins += coins; }

        // Loot level: rises OFF + a flat move budget. NOT a win-condition objective.
        public override bool RisesOff    => true;
        public override bool UsesMoveCap => true;

        private int Looted => _spawned ? Mathf.Clamp(_spawnedCount - _remaining, 0, _spawnedCount) : 0;

        public override string Title        => "Grab what you can!";
        public override string ProgressText => _spawned ? $"{Looted} / {_spawnedCount} looted" : "";
        public override HudIcon Icon         => HudIcon.Vault;
        public override int     RemainingCount => _spawned ? Mathf.Max(0, _remaining) : _goal;
        public override string  IntroDescription => "Crack open as many chests as you can before you run out of moves!";
        // The level-END gate is in SurvivalManager.CheckStageClear (out-of-moves OR all-looted) —
        // NOT here — so this is NOT a required "crack-them-all" win. But if the player DOES loot
        // every chest, IsComplete flips true so the normal objective-complete celebration fires
        // (green check + bing), same as clearing the obstacles on a regular level. Out-of-moves
        // with chests still remaining keeps this false → no false "you won". 2026-06-09.
        public override bool   IsComplete   => _spawned && _remaining <= 0;

        // Spawn the vaults once the board has tiles to host them, then poll remaining each Tick.
        public override void Tick()
        {
            var rules = RulesEngine.Instance;
            if (rules == null) return;
            if (!_spawned)
            {
                if (rules.GetBoardOccupancy() <= 0f) return; // board not seeded yet
                var sm = SurvivalManager.Instance;
                int   fillRows   = sm != null ? sm.VaultStartFillRows   : 7;
                float density    = sm != null ? sm.VaultFillDensity     : 0.85f;
                int   spread     = sm != null ? sm.VaultHeightSpread    : 4;
                int   minSpacing = sm != null ? sm.VaultChestMinSpacing : 3;
                // Chest tiers: total = regular + mid + high; high-tier = the elevated jackpot chest.
                int   midCount   = sm != null ? sm.VaultMidCount        : 1;
                int   midLen     = sm != null ? sm.VaultMidWordLen      : 4;
                int   highCount  = sm != null ? sm.VaultHighCount       : 1;
                int   highLen    = sm != null ? sm.VaultHighWordLen     : 5;
                int   total      = sm != null ? sm.VaultTotalChests     : _goal;
                rules.SeedVaultBoard(fillRows, density, total, spread, minSpacing, midCount, midLen, highCount, highLen);
                // SeedVaultBoard ClearBoard'd → globalTurn reset to 0. MatchController owns the
                // turn counter and overwrites GlobalTurn each drop, so its cached _currentTurn
                // (still high from the prior level) MUST be reset too — else the next drop slams a
                // stale turn back on and the new board's primed tiles fuse to 0 / heat 10. 2026-06-10.
                MatchController.Instance?.ResetTurnCounter();
                // FULL rebuild (not SyncToRulesState): SeedVaultBoard clears + reseeds the whole
                // board, so we must return every old tile to the pool — that wipes stale per-tile
                // visual state (e.g. leftover primed glow from the previous level). 2026-06-09.
                GridManager.Instance?.RebuildFromRulesEngine(rules);
                _spawnedCount = rules.CountAnchoredCells();
                _remaining    = _spawnedCount;
                _spawned      = true;
                return;
            }
            _remaining = rules.CountAnchoredCells();
        }

        public override void Reset()
        {
            _spawnedCount = 0;
            _remaining    = 0;
            _spawned      = false;
            _rewardCoins  = 0;
        }
    }

    /// <summary>
    /// CLEAR-THE-ICE / blocker level (Candy-Crush ice/jelly genre staple, 2026-06-12). N letter tiles
    /// start FROZEN (ice overlay) — they're NORMAL, MATCHABLE letters that participate in word
    /// detection like any other tile. To clear a frozen tile the player must include it in a word and
    /// DETONATE that word: the tile then THAWS (ice clears) and SURVIVES in place while the rest of the
    /// word explodes normally (see RulesEngine.DoExplode/TryThawCell). Objective = clear ALL ice.
    /// Poll-based: spawns the ice once the board is seeded, then polls CountFrozenCells each Tick;
    /// IsComplete when 0 frozen tiles remain (relies on the ObjectiveManager.Update Tick→complete
    /// wrapper to fire the win, same as VaultObjective). Sits on the normal Survival loop (rises ON,
    /// no move cap). v1 = single ice layer; a future multi-layer is an easy add (IceLayers int).
    /// </summary>
    public sealed class IceObjective : Objective
    {
        private readonly int _goal;     // requested frozen-tile count
        private int  _frozenCount;      // actually frozen (board may host fewer than requested)
        private int  _remaining;        // polled each Tick
        private bool _spawned;

        public IceObjective(int goal) => _goal = Mathf.Max(1, goal);

        private int Cleared => _spawned ? Mathf.Clamp(_frozenCount - _remaining, 0, _frozenCount) : 0;

        // Title always matches the progress denominator: before spawn show the requested _goal;
        // after spawn show what ACTUALLY froze (board may host fewer than requested). Prevents the
        // "Clear 3 ice tiles  0/7" mismatch Spencer caught — title and count are now one number. 2026-06-15.
        public override string Title
        {
            get { int shown = _spawned ? _frozenCount : _goal; return shown == 1 ? "Clear the ice" : $"Clear {shown} ice tiles"; }
        }
        public override string ProgressText => _spawned ? $"{Cleared} / {_frozenCount}" : "";
        // Complete only once ice was actually placed AND all of it has thawed. Before spawn,
        // _spawned is false so this stays false (no premature win on an empty board).
        public override bool   IsComplete   => _spawned && _remaining <= 0;
        public override string RemainingText { get { int r = Mathf.Max(0, _remaining); return $"{r} ice tile{(r == 1 ? "" : "s")} left to clear"; } }
        public override HudIcon Icon         => HudIcon.Ice;
        public override int     RemainingCount => _spawned ? Mathf.Max(0, _remaining) : _goal;
        public override string  IntroDescription => $"Clear {_goal} ice tiles by spelling words through them!";

        // Spawn the ice once the board has letters to freeze, then poll remaining each Tick.
        public override void Tick()
        {
            var rules = RulesEngine.Instance;
            if (rules == null) return;
            if (!_spawned)
            {
                if (rules.GetBoardOccupancy() <= 0f) return; // board not seeded yet
                var sm = SurvivalManager.Instance;
                // FULLER starting board (Spencer 2026-06-15): reuse the vault bottom-up fill with ZERO
                // vaults so the ice level starts packed with letters to build words through the ice.
                // Mirrors VaultObjective's seed — incl. the turn-counter reset trap (MatchController owns
                // the turn counter; SeedVaultBoard's ClearBoard only zeroes GlobalTurn, so its stale
                // _currentTurn must be reset too) + a full rebuild to wipe stale per-tile visual state.
                int   fillRows = sm != null ? sm.VaultStartFillRows : 7;
                float density  = sm != null ? sm.VaultFillDensity   : 0.85f;
                rules.SeedVaultBoard(fillRows, density, 0, 0, 0, 0, 0, 0, 0); // 0 vaults = fill only
                MatchController.Instance?.ResetTurnCounter();
                GridManager.Instance?.RebuildFromRulesEngine(rules);

                // Per-level ice count comes from the LevelTable (_goal) — NOT the legacy global
                // SurvivalManager.IceTileCount, which forced every ice level to the same number and
                // overrode the table's curve (L4=3 intro → L8=6 → L14=7 → L15=8). 2026-06-15 Spencer.
                _frozenCount = rules.SpawnFrozenTiles(_goal);
                GridManager.Instance?.SyncToRulesState(rules);
                _remaining = _frozenCount;
                _spawned   = true;
                return;
            }
            _remaining = rules.CountFrozenCells();
        }

        public override void Reset()
        {
            _frozenCount = 0;
            _remaining   = 0;
            _spawned     = false;
        }
    }

    /// <summary>
    /// "Land a K-chain cascade." Reuses ChainStep on OnWordScored (ChainStep+1 = words in the
    /// cascade). Cheap to build, teaches the setup→trigger→cascade wow loop.
    /// </summary>
    public sealed class ChainObjective : Objective
    {
        private readonly int _goalChain;
        private int _best;

        public ChainObjective(int goalChain) => _goalChain = Mathf.Max(2, goalChain);

        public override string Title        => $"Land a {_goalChain}-chain";
        public override string ProgressText => $"best {_best} / {_goalChain}";
        public override bool   IsComplete   => _best >= _goalChain;

        public override void OnWordScored(WordScoredEvent evt)
        {
            if (IsComplete || evt == null || evt.PlayerIndex != MatchController.PLAYER_HUMAN) return;
            int chainLen = evt.ChainStep + 1; // ChainStep 0 = first word, so a k-chain reaches ChainStep k-1
            if (chainLen > _best) _best = chainLen;
        }

        public override void Reset() => _best = 0;
    }
}
