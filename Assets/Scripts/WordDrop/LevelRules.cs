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
        /// <summary>Shown to the player. ⚠️ IF A LEVEL CHANGES A RULE, THE NAME MUST SAY SO.
        /// 2026-10-19: level 4 set MIN_WORD_LENGTH = 4 and was called "Four Letter". Spencer
        /// swapped, made three-letter words, and nothing primed — which is indistinguishable
        /// from the game being broken. A rule the player cannot see is a bug no matter how
        /// deliberate it is.</summary>
        public string Name;
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
                                       int fillRows = 5, float density = 0.92f)
            => new LevelRules {
                Name = name, IceCount = ice, MinWordLength = minLen, RiseCadence = cadence,
                BranchPriming = branch, FuseOn = fuse, GoldTiles = gold,
                FillRows = fillRows, Density = density };

        /// <summary>The authored opening. One named twist per stage — that is what makes a level
        /// feel designed instead of a number going up. Keep each stage to ONE new idea.</summary>
        private static readonly LevelRules[] TABLE = {
            /* 1  */ Make("Welcome",      cadence:3, fillRows:4, density:0.90f),
            /* 2  */ Make("Warm Up",      fillRows:4, density:0.92f),
            /* 3  */ Make("Ice Breaker",  ice:8),
            /* 4  */ Make("4+ Letters Only",  minLen:4),
            /* 5  */ Make("Treasure",     gold:true, cadence:3, fillRows:6, density:0.95f),
            /* 6  */ Make("Quickening",   cadence:2),
            /* 7  */ Make("Branching — Connect to the Web",    branch:true, cadence:3),
            /* 8  */ Make("Deep Freeze — 4+ Letters",  ice:12, minLen:4),
            /* 9  */ Make("Thin Ice",     ice:6, cadence:2),
            /* 10 */ Make("Tangle — Connect to the Web", branch:true, cadence:1, fillRows:7, density:0.96f), // BOSS
            /* 11 */ Make("Payday",       gold:true, ice:4, cadence:3),     // relief
            /* 12 */ Make("Burning Fuse — Words Expire", fuse:true),
            /* 13 */ Make("Hard Water",   ice:10, fuse:true),
            /* 14 */ Make("Scarcity — 4+ Letters",     minLen:4, cadence:2),
            /* 15 */ Make("Crucible — 4+ Letters",     minLen:4, fuse:true, cadence:1),  // BOSS
        };

        /// <summary>BLAST-LIMIT PROTOTYPE switch (2026-10-20). True = each level gives a fixed number
        /// of blasts and rows do NOT rise from moves; false = the rising-row game exactly as before.
        /// See SurvivalManager.BlastMode for the rules.</summary>
        public static bool BlastLimitMode = false;   // 2026-10-20 Spencer tried it: "meh ... at least with the row rising there was some pressure and it introduces new letters"
        /// <summary>Blasts per level. One number for every level while the idea is being felt —
        /// per-level tuning comes after we know whether it is fun.</summary>
        public static int  BlastsPerLevel = 5;

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
                case 2: r = Make("Pressure — 4+ Letters",  ice: 6 + block,      cadence: 2, minLen: 4);                   break;
                case 3: r = Make("Squeeze — Words Expire",   ice: 8 + block,      cadence: 2, fuse: true);                  break;
                default:r = Make("Boss — 4+ Letters, Connect",      ice: 10 + block * 2, cadence: 1, minLen: 4, branch: true);     break;
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
        /// <summary>True when THIS table is the one that seeds the board for a given stage, so
        /// the older ObjectiveManager per-level reseed must stand down.
        ///
        /// 2026-10-20 — measured in a live log, one frame after CONTINUE was pressed on LEVEL 1:
        ///     [Vault] SeedVaultBoard: startFillRows=5 density=0.65     <- ObjectiveManager
        ///     [LevelTable] Install L12 ... Generated level 2 'Warm Up'
        ///     [Vault] SeedVaultBoard: startFillRows=4 density=0.92     <- LevelRules
        /// Two full board wipes and rebuilds back to back. LevelRules runs last so its board is
        /// the one that survives, but the player still watches the first one get built and
        /// animated in before it is thrown away — which is exactly the report "there was already
        /// a board that was there before i entered the level and then another board animated over
        /// top of it". One owner, decided in one place.</summary>
        public static bool OwnsBoardSeed(int internalStage) =>
            SurvivalManager.ScoreRunMode && !LevelMapPanel.IsTutorialLevel(internalStage);

        /// <summary>The whole player-facing briefing for a level: the points target, then any
        /// rule that makes THIS level different, then — for the first few levels — how the game
        /// is actually played.
        ///
        /// 2026-10-20 Spencer: "take out the goals and just say how many points you need to pass
        /// the level and any instructions as to what the certain conditions are". The modal used
        /// to show a tile-cluster icon and the level's internal NAME ("Warm Up"), which tells a
        /// new player nothing — it named the level instead of explaining it.
        ///
        /// Built HERE rather than in the modal because this file already owns every rule being
        /// described. A rule added to the table above and not described below is a rule the
        /// player has to discover by losing, so the two belong in one file. (That failure
        /// already happened once: LEVEL 4's hidden 4-letter minimum read as "swaps do nothing".)
        ///
        /// The core-loop lines stop after LEVEL 3 — by then it is clutter above the PLAY button,
        /// and the only thing worth saying is what is different about today.</summary>
        public static string BriefingFor(int internalStage, int target)
        {
            int stage = Mathf.Max(1, LevelMapPanel.RunLevel(internalStage));
            var r = For(stage);
            var sb = new System.Text.StringBuilder();

            sb.Append(BlastLimitMode
                ? $"Score <b>{target:N0} points</b> in <b>{BlastsPerLevel} blasts</b>."
                : $"Score <b>{target:N0} points</b>");

            // ── what is different about THIS level ───────────────────────────────
            // Each line names the rule AND what to do about it. "4+ letters only" alone
            // leaves the player staring at a board; saying it refuses shorter words tells
            // them why nothing is happening.
            if (r.MinWordLength > 3)
                sb.Append($"\n\nOnly words of <b>{r.MinWordLength} letters or more</b> count here — " +
                          "shorter ones will not light up.");
            if (r.IceCount > 0)
                sb.Append($"\n\n<b>{r.IceCount} frozen tiles.</b> You cannot move them. Make a word " +
                          $"THROUGH one and blow it up to melt it — worth <b>{RulesEngine.ICE_BOUNTY}</b> each.");
            if (r.BranchPriming)
                sb.Append("\n\nYour first word can be anywhere. After that, <b>every new word must " +
                          "touch the ones you have already lit.</b>");
            if (r.FuseOn)
                sb.Append("\n\n<b>Lit words burn out</b> if you leave them too long. Set them off " +
                          "before they go dark.");
            if (r.GoldTiles)
                sb.Append("\n\n<b>Gold tiles are worth double.</b> Work them into a word before you blow it up.");
            // Cadence 2 is the DEFAULT, so stating it is noise on a level where nothing is
            // unusual — it reads as a special condition when it is just Tuesday. Only the
            // relentless setting earns a warning line; the fact that rows rise AT ALL is
            // taught once, below, to players who have never seen it.
            if (r.RiseCadence <= 1 && !BlastLimitMode)   // no move-rises in blast mode
                sb.Append("\n\nA new row pushes up <b>every single move</b>. Keep the board low.");

            // ── how the game works, for someone who has never played ─────────────
            // Order matters: how to act, what that does, and THEN how points happen. The
            // third line is the one most new players get wrong, and since 2026-10-20 it is
            // literally the only way to score — making a word pays nothing on its own.
            if (stage <= 3)
            {
                sb.Append(HandManager.AdjacentSwapsOnly
                    ? "\n\n<b>Swap</b> two touching tiles, or <b>swipe</b> across a word you spot."
                    : "\n\n<b>Swap</b> any two tiles, or <b>swipe</b> across a word you spot.");
                if (HandManager.InstantPopMode)
                    sb.Append("\n\nEvery word you make <b>pops</b> and scores. Letters fall — " +
                              "<b>new words they make pop too.</b>");
                else
                {
                    sb.Append("\n\nThe word <b>stays lit on the board</b> instead of vanishing.");
                    sb.Append("\n\n<b>Tap a lit word to blow it up — that is the only way to score.</b> " +
                              "Lit words that touch go off together, and each one in the chain is worth more " +
                              "than the last.");
                }
                if (BlastLimitMode)
                    sb.Append("\n\nEach blast uses one of your blasts — <b>a bigger web is worth far more</b>, " +
                              "so build before you fire.");
                else
                    sb.Append("\n\nNew rows push up from the bottom as you play. Reach the top and the run ends.");
            }

            return sb.ToString();
        }

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

            // The next level is now genuinely starting — release the lock that has been
            // holding input and pinning the HUD since the previous level's target was met.
            SurvivalManager.ClearAwaitingNextLevel();
            if (BlastLimitMode)
            {
                SurvivalManager.Instance?.ResetBlasts(BlastsPerLevel);
                SurvivalManager.SetBlastRefillOccupancy(r.FillRows * r.Density / RulesEngine.ROWS);
            }
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
                // SETTLE IT. SeedVaultBoard scatters at `density`, so at 0.65 a third of the
                // cells are holes — including holes UNDER letters, which is the "massive gaps
                // with no letters" and the floating single tiles. Gravity packs them into solid
                // rows from the bottom, which is what a starting board should look like.
                // 2026-10-19 Spencer.
                rules2.ApplyGravityInDataPublic();
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
                // Reconcile, not plain sync: this is the ONE path that drives frost onto
                // already-existing tiles, and a cell frozen in DATA but not showing frost is
                // unselectable while looking perfectly normal (TryEnterRewriteMode and
                // TryBoardSwap both refuse on cell.IsFrozen). 2026-10-19.
                GridManager.Instance?.ReconcileSpecialVisuals(RulesEngine.Instance);
                int dataFrozen = RulesEngine.Instance.CountFrozenCells();
                if (dataFrozen != placed)
                    Debug.LogWarning($"[LevelRules] ICE MISMATCH — spawned {placed} but board reports " +
                                     $"{dataFrozen} frozen cells. Stale ice from a previous level?");
            }

            var sm = SurvivalManager.Instance;
            if (r.MinWordLength > 3)
                WDLog.Log($"[LevelRules] ⚠️ LEVEL {stage} refuses words under {r.MinWordLength} letters — " +
                          $"if swaps 'do nothing' here, that is why. Name says so: '{r.Name}'");
            WDLog.Log($"[LevelRules] LEVEL {stage} (internal {internalStage}) '{r.Name}' — " +
                      $"TARGET {(sm != null ? sm.CurrentStageTarget : -1)} " +
                      $"(score so far {(sm != null ? sm.CurrentStageScore : -1)}), " +
                      $"ice {placed}/{r.IceCount}, " +
                      $"minLen {r.MinWordLength}, cadence {r.RiseCadence}, " +
                      $"branch {r.BranchPriming}, fuse {r.FuseOn}, gold {r.GoldTiles}");
        }
    }
}
