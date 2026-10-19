using UnityEngine;

namespace WordDrop
{
    /// <summary>PER-STAGE RULES — the seed of the level generator. 2026-10-19 Spencer.
    ///
    /// The game is SCORE-ONLY: a stage clears when you bank its target, and nothing else.
    /// So a "level" is not an objective — it is a set of RULE TWEAKS plus a score target, and
    /// the design lever is WHERE THE POINTS ARE. Put most of a level's target inside ice and
    /// it becomes an ice level without a single line of objective text, because the efficient
    /// route to the target runs through the ice.
    ///
    /// Deliberately a small hand-written table for the first stages, then a formula. That order
    /// matters: the early stages are the ones players actually see, and they want hand-placed
    /// beats. Past the table it repeats the shape with counts climbing.
    ///
    /// EVERY FIELD HERE MAPS TO SOMETHING ALREADY IN THE ENGINE — no new systems:
    ///   IceCount      -> RulesEngine.SpawnFrozenTiles (existed; only IceObjective called it,
    ///                    and objectives do not run in score mode, so ice never appeared)
    ///   MinWordLength -> RulesEngine.MIN_WORD_LENGTH  (was a private const)
    ///   RiseCadence   -> SurvivalManager rise cadence (the real pressure dial)
    ///   BranchPriming -> RulesEngine.BranchPriming    (built, was off)
    ///   FuseOn        -> RulesEngine.FuseDisabled     (built, was off)
    ///   GoldTiles     -> RulesEngine.GoldTilesEnabled (built, off since June)
    /// </summary>
    public struct LevelRules
    {
        public string Name;           // shown to the player — a level needs to be A LEVEL, not a number
        public int    IceCount;       // frozen tiles seeded at stage start (each pays ICE_BOUNTY)
        public int    MinWordLength;  // 3 normally; 4 makes the board read completely differently
        public int    RiseCadence;    // moves per rise. 3 = gentle, 2 = normal, 1 = relentless
        public bool   BranchPriming;  // every new word must touch the existing web
        public bool   FuseOn;         // armed words expire if you sit on them
        public bool   GoldTiles;      // 2x score cells
        public int    FillRows;       // rows of letters on the FRESH board each level starts with
        public float  Density;        // how packed those rows are (0..1)

        private static LevelRules Make(string name, int ice = 0, int minLen = 3, int cadence = 2,
                                       bool branch = false, bool fuse = false, bool gold = false,
                                       int fillRows = 5, float density = 0.65f)
            => new LevelRules {
                Name = name, IceCount = ice, MinWordLength = minLen, RiseCadence = cadence,
                BranchPriming = branch, FuseOn = fuse, GoldTiles = gold,
                FillRows = fillRows, Density = density };

        /// <summary>The authored opening. One named twist per stage — that is what makes a level
        /// feel designed instead of a number going up. Keep each stage to ONE new idea.</summary>
        private static readonly LevelRules[] TABLE = {
            /* 1  */ Make("Welcome",      cadence:3, fillRows:4, density:0.55f),
            /* 2  */ Make("Warm Up",      fillRows:4, density:0.60f),
            /* 3  */ Make("Ice Breaker",  ice:8),
            /* 4  */ Make("Four Letter",  minLen:4),
            /* 5  */ Make("Treasure",     gold:true, cadence:3, fillRows:6, density:0.75f),
            /* 6  */ Make("Quickening",   cadence:2),
            /* 7  */ Make("Branching",    branch:true, cadence:3),
            /* 8  */ Make("Deep Freeze",  ice:12, minLen:4),
            /* 9  */ Make("Thin Ice",     ice:6, cadence:2),
            /* 10 */ Make("Tangle",       branch:true, cadence:1, fillRows:7, density:0.80f), // BOSS
            /* 11 */ Make("Payday",       gold:true, ice:4, cadence:3),     // relief
            /* 12 */ Make("Burning Fuse", fuse:true),
            /* 13 */ Make("Hard Water",   ice:10, fuse:true),
            /* 14 */ Make("Scarcity",     minLen:4, cadence:2),
            /* 15 */ Make("Crucible",     minLen:4, fuse:true, cadence:1),  // BOSS
        };

        public static LevelRules For(int stage)
        {
            if (stage < 1) stage = 1;
            if (stage <= TABLE.Length) return TABLE[stage - 1];

            // Past the table: repeat the 5-beat shape with counts climbing. slot 4 = boss,
            // slot 0 = the valley right after it. Peaks AND valleys both trend up.
            int over  = stage - TABLE.Length - 1;
            int block = over / 5;
            int slot  = over % 5;
            var r = Make($"Stage {stage}");
            switch (slot)
            {
                case 0: r = Make("Breather",  ice: 2 + block,      cadence: 3, gold: true);                  break;
                case 1: r = Make("Climb",     ice: 4 + block,      cadence: 2);                              break;
                case 2: r = Make("Pressure",  ice: 6 + block,      cadence: 2, minLen: 4);                   break;
                case 3: r = Make("Squeeze",   ice: 8 + block,      cadence: 2, fuse: true);                  break;
                default:r = Make("Boss",      ice: 10 + block * 2, cadence: 1, minLen: 4, branch: true);     break;
            }
            return r;
        }

        /// <summary>Last stage whose ICE was seeded, so a repeated InstallLevel in the same
        /// stage cannot stack ice on top of ice. The rule switches are idempotent and can
        /// safely re-apply; seeding is not.</summary>
        private static int s_icedStage  = -1;
        /// <summary>Last stage this ran for. -1 means "no stage yet this run", which is how we
        /// tell a STAGE TRANSITION (reseed a fresh board) from the FIRST install of a run (leave
        /// the board alone — MatchController already built it through OpeningSeed, including the
        /// first-word guarantee, and stomping it would throw that away).</summary>
        private static int s_lastStage  = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { s_icedStage = -1; s_lastStage = -1; }

        /// <summary>Push this stage's rules into the engine. Called at the end of
        /// ObjectiveManager.InstallLevel, once per stage.</summary>
        public static void Apply(int internalStage)
        {
            // ⚠️ INTERNAL STAGE != THE LEVEL THE PLAYER SEES. 2026-10-19 — Spencer started a run,
            // saw "LEVEL 1", and it had ice in it. Cause: with the tutorial complete a run begins
            // at INTERNAL stage 11 (RunStartStage = TUTORIAL_LEVELS + 1), and LevelMapPanel maps
            // internal 11 -> displayed "LEVEL 1". This table was keyed on the internal index, so
            // the player's first level was pulling table row 11 ("Payday", 4 ice).
            // LevelMapPanel's own comment says it: "everything player-facing goes through these
            // transforms." This is player-facing, so it goes through them too.
            if (LevelMapPanel.IsTutorialLevel(internalStage))
            {
                // The tutorial levels are hand-authored with rigged boards and gated input.
                // They must not get generated rules — but DO reset the switches, or a value
                // left over from a previous run (4-letter minimum, branch priming) leaks in.
                RulesEngine.MIN_WORD_LENGTH  = 3;
                RulesEngine.BranchPriming    = false;
                RulesEngine.GoldTilesEnabled = false;
                return;
            }

            int stage = Mathf.Max(1, LevelMapPanel.RunLevel(internalStage));
            var r = For(stage);
            RulesEngine.MIN_WORD_LENGTH = Mathf.Max(3, r.MinWordLength);
            RulesEngine.BranchPriming   = r.BranchPriming;
            RulesEngine.FuseDisabled    = !r.FuseOn;      // field is inverted: Disabled == no decay
            RulesEngine.GoldTilesEnabled = r.GoldTiles;
            SurvivalManager.SetRiseCadenceOverride(Mathf.Max(1, r.RiseCadence));

            // ── FRESH BOARD ON EVERY NEW LEVEL ───────────────────────────────────────
            // Spencer: "a brand new starting board with a different configuration of starting
            // letters". AdvanceToNextStage only queued replenish RISES, so the next level
            // inherited the previous one's picked-over board. A reseed per level also makes
            // each level a distinct puzzle rather than a continuation.
            //
            // Same sequence ObjectiveManager already uses for its per-level reseed — including
            // ResetTurnCounter, which is THE documented trap: ClearBoard zeroes GlobalTurn and
            // MatchController's cached _currentTurn must be reset to match or the turn counter
            // desyncs. RebuildFromRulesEngine (not SyncToRulesState) because a full rebuild also
            // wipes stale per-tile visual state like leftover primed glow.
            var rules2 = RulesEngine.Instance;
            bool isTransition = s_lastStage >= 0 && s_lastStage != internalStage;
            if (isTransition && rules2 != null)
            {
                rules2.SeedVaultBoard(r.FillRows, r.Density, 0, 0, 0, 0, 0, 0, 0); // 0 vaults = fill only
                MatchController.Instance?.ResetTurnCounter();
                GridManager.Instance?.RebuildFromRulesEngine(rules2);
                WDLog.Log($"[LevelRules] fresh board for LEVEL {stage} — {r.FillRows} rows @ {r.Density:P0}");
            }
            s_lastStage = internalStage;

            int placed = 0;
            if (r.IceCount > 0 && RulesEngine.Instance != null && s_icedStage != internalStage)
            {
                placed = RulesEngine.Instance.SpawnFrozenTiles(r.IceCount);
                s_icedStage = internalStage;
                // SpawnFrozenTiles only picks from OCCUPIED cells, so a sparse board yields
                // fewer than asked. The log reports both so a shortfall is visible rather than
                // looking like the feature failed.
                GridManager.Instance?.SyncToRulesState(RulesEngine.Instance);
            }

            var sm = SurvivalManager.Instance;
            WDLog.Log($"[LevelRules] LEVEL {stage} (internal {internalStage}) '{r.Name}' — " +
                      $"TARGET {(sm != null ? sm.CurrentStageTarget : -1)} " +
                      $"(score so far {(sm != null ? sm.CurrentStageScore : -1)}), " +
                      $"ice {placed}/{r.IceCount}, " +
                      $"minLen {r.MinWordLength}, cadence {r.RiseCadence}, " +
                      $"branch {r.BranchPriming}, fuse {r.FuseOn}, gold {r.GoldTiles}");
        }
    }
}
