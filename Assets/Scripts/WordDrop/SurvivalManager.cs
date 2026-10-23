using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages Survival Mode — solo real-time arcade word survival.
    /// Tiles auto-drop from above on a timer, rising rows push from below.
    /// No AI opponent. Score + time survived. Top-out = game over.
    /// </summary>
    public class SurvivalManager : MonoBehaviour
    {
        public static SurvivalManager Instance { get; private set; }

        // ── Mode flag ─────────────────────────────────────────────────────────────
        private static bool _isSurvivalMode = false;
        public static bool IsSurvivalMode
        {
            get => _isSurvivalMode;
            set => _isSurvivalMode = value;
        }

        /// <summary>Debug toggle: when true, every player drop fires a rising row
        /// immediately and the time-based rise clock is suppressed. Lets the player
        /// playtest pressure cadence one move at a time. Toggle via FXTestMenu.
        /// Not persisted — session-only. Default ON for current playtest pass.</summary>
        public static bool RisePerMoveDebug { get; set; } = true;

        // ── Resource drop tuning (only special tiles — no normal letter drops) ──
        public const float AUTO_DROP_INTERVAL_START = 45f;   // rare — almost a minute between drops
        public const float AUTO_DROP_INTERVAL_FLOOR = 25f;
        public const float AUTO_DROP_RAMP_RATE      = 0.2f;  // very slow ramp
        public const float AUTO_DROP_RAMP_PERIOD     = 60f;
        private const float WILD_DROP_CHANCE         = 0.12f; // 12% chance when player has resources

        // ── Rising row tuning (time-based, stage-aware) ───────────────────────────
        // Phase 11b pivot: rises fire on a wall-clock timer instead of move
        // counts. The stage's MOVE BUDGET still gates run-end (miss target in
        // N moves → fail), but the rising-row pressure now runs on real time —
        // closer to the Tetris/Columns feel the Survival-primary pivot was
        // asking for. Stage-aware curve preserves the old 6-12 rises-per-stage
        // calibration assuming ~5s average per move.
        //
        // Move-based CurrentMovesPerRise is retained as a compat read for any
        // debug/analytics paths that still query it; NotifyDropCommitted still
        // increments _movesSinceLastRise so the field isn't stale. Neither is
        // used for firing anymore — see Update() for the time-based gate.
        public const int MOVES_PER_RISE_FLOOR = 1;

        // Stage-aware seconds-per-rise. S1 relaxed (20s) → S11+ frantic (6s).
        public const float SECONDS_PER_RISE_FLOOR = 7f;

        // ── Stage chip targets (Balatro-shape, endless escalating) ────────────────
        // Each stage has a chip target; hit it in the stage's move budget or the
        // run ends. Scaling is formula-based so stages go to infinity. Placeholder
        // numbers — tune per project_wordrop_scoring_retune.md methodology after
        // measuring 10+ runs under new scoring.
        //
        // Target curve:
        //   S1 = STAGE_TARGET_BASE
        //   S2+ = previous * STAGE_TARGET_GROWTH  (until S_CAP, then plateau)
        // Budget curve:
        //   S1-2 = STAGE_MOVES_BASE
        //   S3-4 = -2  moves, S5-6 = -4, S7+ = floor at STAGE_MOVES_FLOOR
        // ── STAGE CURVE (2026-09-09 Spencer — A PROBE, NOT AN ANSWER) ────────────────
        // Retuned for the SWAP game. The old 400 / 1.5x was calibrated for the DROP game,
        // where a word scored its face value. Detonations now multiply by web size
        // (2x/6x/13x/20x/27x), so a single three-word web is ~650 — it cleared the old L1
        // of 400 before the player had made a real decision.
        //
        // WHY FLATTER, NOT STEEPER. Balatro's targets grow 2.5x early because the PLAYER's
        // engine grows exponentially too — jokers multiply, so the curve is chasing the
        // player. WordDrop has no engine-building yet: scoring power is FLAT across a run
        // while the board tightens. Against flat power, 1.5x per stage means each stage
        // takes 1.5x LONGER than the last — stage 8 would take ~17x stage 1. The run would
        // not get harder, it would get slower, which is the grind Spencer was feeling.
        // Stages here are an ODOMETER, not a gate — you cannot fail one, the run ends by
        // topping out — so their job is pacing, and near-flat growth is right.
        //
        // MEASURE THESE, DO NOT TRUST THEM. Play three runs with the bar visible and note
        // the clock at each stage-up. Aim: L1 ~45-60s, L5 no more than ~2x that. Adjust
        // BASE for the first, GROWTH for the second.
        // If the stage-up buff choice gets built, power will compound and the growth
        // should climb back toward Balatro's shape.
        // ── SCORE RUN (2026-09-09 Spencer) ──────────────────────────────────────────
        // "i went over the target and nothing happened."
        // Two blockers, both drop-game inheritance. (1) The endless level still carries an
        // objective — the fake LongWord goal of 999999 — so CheckStageClear took the
        // OBJECTIVE branch, where clearing requires the objective to be complete, which by
        // design it never can be. The score target is only consulted when no objective
        // exists. (2) The hook that calls CheckStageClear on a score change was gated on
        // IsSurvivalMode, and L11 is a LEVEL, so it was never called at all.
        // ScoreRunMode says "this run is scored, stages advance on the score target" and is
        // the SINGLE source of truth — HUDManager.ScoreHudMode reads this one rather than
        // keeping its own copy.
        public static bool ScoreRunMode = true;

        public const int   STAGE_TARGET_BASE    = 1200;  // ~2 good three-word webs
        public const float STAGE_TARGET_GROWTH  = 1.15f; // near-flat: power does not compound yet
        // Curve with base=400, growth=1.5 (rounded to nearest 50):
        //   S1=400, S2=600, S3=900, S4=1350, S5=2050, S6=3050, S7=4550, S8=6850
        // Target: Spencer-skill players routinely clear S1-S5, first real wall
        // at S6-S7. New casual players clear S1-S2 comfortably, bounce S3-S4.
        public const int   STAGE_MOVES_BASE     = 18;
        public const int   STAGE_MOVES_FLOOR    = 8;
        // Stage-clear reward: +1 rewrite charge on hitting target
        public const int   STAGE_CLEAR_REWRITES = 1;
        // Minimum rows of letters at stage start — if a big chain cleared the
        // board, fire rising rows until this minimum is met. Prevents the
        // anti-feel "clear the stage, now stare at empty board" moment.
        public const int   STAGE_START_MIN_ROWS = 2;
        // Max target — prevents int overflow in deep endless runs.
        // At growth=1.5× per stage, 500 * 1.5^40 ≈ 2e9 (near int.MaxValue).
        // Plateau at 100M so stages 30+ stay achievable-but-absurd.
        public const int   STAGE_TARGET_MAX     = 100_000_000;

        // ── Wild tile tuning ──────────────────────────────────────────────────────
        public const int   WILD_CHAIN_DEPTH_REQ     = 2;     // chain depth required to earn wild
        public const int   WILD_DROP_EXPIRY          = 3;     // expires after N player drops
        public const float WILD_TIME_EXPIRY          = 12f;   // expires after N seconds
        public const float CYAN_SPAWN_CHANCE          = 0.12f; // 12% of auto-drops are cyan
        public const float GOLD_SPAWN_CHANCE          = 0.10f; // 10% of auto-drops are gold
        public const float SWAP_REFILL_CHANCE         = 0.06f; // 6% of auto-drops refill a swap
        public const float EDIT_REFILL_CHANCE         = 0.04f; // 4% of auto-drops refill an edit

        // ── Grace period ──────────────────────────────────────────────────────────
        public const float AUTO_DROP_GRACE     = 5f;   // seconds before first resource drop
        // Rising-row grace is implicit: S1's 12-move cadence is the grace period.

        // ── Top-out mode ─────────────────────────────────────────────────────────
        // Strict: game over when ANY tile reaches the top row
        // Lenient: game over only when ALL columns are full (no place to drop)
        //   PLUS the overflow trigger from RisingRowManager (rise pushes tiles off the top)
        // MVP P5 (2026-05-23): switched default to Lenient. Tiles can reach the top row
        // without instant death — game over only fires when a rising row tries to push
        // tiles BEYOND the top (real overflow) or when every column is jammed full.
        public enum TopOutMode { Strict, Lenient }
        [SerializeField] public TopOutMode topOutMode = TopOutMode.Lenient;

        // ── Vault levels (rises OFF, move cap) — Inspector-tunable. Cap = buffer + perVault×count.
        // 3 + 4×3 = 15 moves for 3 vaults (generous). Tighten late-run for difficulty. 2026-06-09.
        [Header("Vault (loot) levels — move budget")]
        // Deliberately SHORT: ~enough to crack 2-3 of the chests, NOT all. The triage decision
        // (which chests to spend moves on; gamble for the high special one) IS the level. Flat,
        // NOT scaled by chest count. Tighten this / raise chest count for difficulty. 2026-06-09.
        [SerializeField] private int _vaultMoveBudget = 6;

        // ── Vault starting-board seed (density + height) — Inspector-tunable; Spencer feel-dials.
        // Bottom rows seeded with gaps (denser = letters to build words against the chests); top
        // rows left clear as drop headroom; vaults scattered across a height band, 1 biased high.
        [Header("Vault levels (starting board)")]
        [SerializeField] private int   _vaultStartFillRows = 7;     // target stack height (board is 8 rows); RulesEngine hard-caps the top 2 rows as headroom
        [SerializeField, Range(0f, 1f)] private float _vaultFillDensity = 0.85f; // fraction of startFillRows each column reaches → fuller board buries chests; top stays clear
        [SerializeField] private int   _vaultHeightSpread  = 4;     // height band the vaults spread across
        [SerializeField] private int   _vaultChestMinSpacing = 3;   // min Chebyshev gap between chests (full-width spread, no clustering)

        // ── Chest TIERS (2026-06-12): chests crack only when the exploding word's LENGTH meets the
        // tier's requirement (the word is the "key"). Telegraphed bronze/silver/gold. Total chests
        // = regular + mid + high. Tune counts + required lengths for feel.
        [Header("Vault levels (chest tiers)")]
        [SerializeField] private int _vaultRegularCount = 3;   // crack on ANY word (req 0)
        [SerializeField] private int _vaultMidCount     = 1;   // need a mid-length word
        [SerializeField] private int _vaultMidWordLen   = 4;   // mid requirement (≥ N letters)
        [SerializeField] private int _vaultHighCount    = 1;   // the jackpot chest(s)
        [SerializeField] private int _vaultHighWordLen  = 5;   // high requirement (≥ N letters)

        [Header("Ice (clear-the-blocker) levels")]
        [SerializeField] private int _iceTileCount = 7;        // how many letter tiles start frozen (IceObjective spawns this many)

        // ── Debug: no-assist playtest toggle ──────────────────────────────────────
        // Toggle with the N key (main menu OR gameplay — persists across scenes).
        // When ON: disables opening seed, first-word guarantee, board-aware draws,
        // DroughtAssist, mercy slowdown, PostClearBoost, Renewal Row.
        // Prototype gate for Session D DailyMode.IsActive — same logic shape.
        // Persisted via PlayerPrefs so restart isn't required.
        private const string NO_ASSIST_PREF_KEY = "wd_debug_no_assist";
        private static bool _noAssistMode;
        private static bool _noAssistLoaded;

        public static bool NoAssistMode
        {
            get
            {
                if (!_noAssistLoaded)
                {
                    _noAssistMode = PlayerPrefs.GetInt(NO_ASSIST_PREF_KEY, 0) == 1;
                    _noAssistLoaded = true;
                }
                return _noAssistMode;
            }
            set
            {
                _noAssistMode = value;
                _noAssistLoaded = true;
                PlayerPrefs.SetInt(NO_ASSIST_PREF_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
                Debug.LogWarning($"[SurvivalManager] NoAssistMode {(value ? "ENABLED" : "DISABLED")} — starts applying on next run.");
            }
        }

        /// <summary>Toggle NoAssistMode. Call from a keyboard shortcut or UI button.</summary>
        public static void ToggleNoAssistMode() => NoAssistMode = !NoAssistMode;

        // ── State ─────────────────────────────────────────────────────────────────
        private float _elapsedTime;          // kept for HUD/debrief stats, not for difficulty
        private float _autoDropTimer;
        private int   _movesSinceLastRise;   // vestigial (Phase 11b) — NotifyDropCommitted still increments, but rises fire off _riseTimerSeconds now
        private int   _turnsSinceLastTurnRise;   // turn-based mode (RisePerMoveDebug): counts player turns since last rise to gate cadence per stage
        private bool  _riseScheduledPending;     // true from when a rise is queued until it executes; used by GetMovesUntilTopOut to read 0 the instant a fatal rise is queued
        private float _riseTimerSeconds;     // Phase 11b: time-based rising rows; resets on rise fire, stage advance, StartSurvival
        private bool  _appPaused;            // Phase 11b: OnApplicationPause(true) sets this, Update skips timer accumulation while true
        private int   _movesPlayed;          // lifetime move count — drives stage + debrief
        private int   _totalAutoDrops;
        private int   _longestChain;
        private string _longestWord = "";    // Phase 11+ session stat — updated on every Survival word ≥ 5
        private bool  _isGameOver;
        private bool  _isAutoDropping;   // prevents re-entrant auto-drops
        private bool  _isRisingRow;      // prevents re-entrant rising rows
        public  bool  IsRisingRow => _isRisingRow;
        private bool  _pendingEditDroughtRebate;  // drought rebate: scored-while-tapped → forced edit refill on next auto-drop
        private bool  _isOverlayPaused;  // stage-clear modal (and future overlays) freeze rising-row + auto-drop timers
        public  bool  IsOverlayPaused => _isOverlayPaused;
        private int   _lastStage = 1;    // track for stage-up signal
        private float _tileRepairTimer;  // periodic blank tile repair

        // ── One-time tutorial (2026-07-14 Spencer) ────────────────────────────────
        // The tutorial (internal levels 1..TUTORIAL_LEVELS) plays ONCE ever. After it's been cleared, every new run
        // starts at the first real Area level instead of re-marching the player through the scripted coaching.
        private const string PREF_TUTORIAL_DONE = "wd_tutorial_done";
        public static bool TutorialDone
        {
            get => PlayerPrefs.GetInt(PREF_TUTORIAL_DONE, 0) == 1;
            set { PlayerPrefs.SetInt(PREF_TUTORIAL_DONE, value ? 1 : 0); PlayerPrefs.Save(); }
        }
        // The stage a fresh run starts on: level 1 the first time (tutorial), else the first real Area level.
        // LEGACY_TUTORIAL_ENABLED (2026-10-20, Spencer: the old tutorial does NOT ship): the internal 1..10 tutorial was
        // built for the DROP game — it coaches card drops while HandEnabled=false, so a fresh install started in a
        // probable soft-lock (found by the 2026-10-20 audit; never seen because this Mac has TutorialDone=1). Every
        // run now starts at LEVEL 1 (internal 11). Kept, switched off — flip true to bring the old flow back.
        public static bool LEGACY_TUTORIAL_ENABLED = false;
        private static int RunStartStage => (TutorialDone || !LEGACY_TUTORIAL_ENABLED) ? (LevelMapPanel.TUTORIAL_LEVELS + 1) : 1;

        // ── Stage chip target state ───────────────────────────────────────────────
        private int _currentStageIndex = 1;        // stage number (1, 2, 3, …)
        private int _stageStartScore = 0;           // PlayerScore snapshot at stage start
        private int _currentStageMovesUsed = 0;     // moves spent in current stage
        private int _risesInCurrentStage = 0;       // balance analytics — rises fired per stage
        // ── BLAST-LIMIT PROTOTYPE (2026-10-20 Spencer: "i just feel its pretty boring and meh") ──
        // Each level gives a fixed number of BLASTS (detonations). Swaps stay free. Rises from
        // moves are OFF; after each blast the board is topped back up to the level's starting
        // fill so it never drains. Out of blasts and short of the target = level lost.
        // The point: "fire now or build bigger?" becomes a real decision, because a small web
        // WASTES a blast. Switch: LevelRules.BlastLimitMode. Off = the rising-row game, untouched.
        public static bool BlastMode => LevelRules.BlastLimitMode && ScoreRunMode;
        private int   _blastBudget;
        private int   _blastsUsed;
        private bool  _blastLoseCheckRunning;
        private static float s_blastRefillOcc = 0.33f;
        public int  BlastsRemaining => Mathf.Max(0, _blastBudget - _blastsUsed);
        public static void SetBlastRefillOccupancy(float occ) => s_blastRefillOcc = Mathf.Clamp(occ, 0.1f, 0.8f);
        public void ResetBlasts(int budget)
        {
            _blastBudget = Mathf.Max(1, budget);
            _blastsUsed  = 0;
            WDLog.Log($"[Blast] level budget {_blastBudget} blasts");
        }
        /// <summary>Called once per player TAP-detonation, at the tap — a whole web is ONE blast.</summary>
        public void NotifyBlastFired()
        {
            if (!BlastMode) return;
            _blastsUsed++;
            WDLog.Log($"[Blast] fired {_blastsUsed}/{_blastBudget} — {BlastsRemaining} left");
        }
        /// <summary>Called when a tap-detonation has fully resolved (gravity done).</summary>
        public void NotifyBlastResolved()
        {
            if (!BlastMode || _isGameOver) return;
            // Top the board back up to the level's starting fill. Uses the existing stage-start
            // rise queue, which Update drains one row at a time once the board is still.
            if (RulesEngine.Instance != null)
            {
                float occ  = RulesEngine.Instance.GetBoardOccupancy();
                int   rows = Mathf.CeilToInt((s_blastRefillOcc - occ) * RulesEngine.ROWS);
                if (rows > 0)
                {
                    _stageStartRisesPending = Mathf.Max(_stageStartRisesPending, Mathf.Clamp(rows, 1, 4));
                    WDLog.Log($"[Blast] refill: occupancy {occ:P0} -> {_stageStartRisesPending} row(s) toward {s_blastRefillOcc:P0}");
                }
            }
            if (BlastsRemaining <= 0 && !_blastLoseCheckRunning) StartCoroutine(BlastLoseCheck());
        }
        private IEnumerator BlastLoseCheck()
        {
            _blastLoseCheckRunning = true;
            // Let the last blast's points finish landing, and any cascade finish, before judging —
            // the target check runs on score ARRIVAL, not on the tap.
            float waited = 0f;
            while (waited < 4f && (ScoreManager.Instance != null && ScoreManager.Instance.FlyScorePending
                                   || (MatchController.Instance != null && MatchController.Instance.IsProcessing)))
            { waited += Time.unscaledDeltaTime; yield return null; }
            yield return null;
            _blastLoseCheckRunning = false;
            if (AwaitingNextLevel || _isGameOver || BlastsRemaining > 0) yield break;
            WDLog.Log($"[Blast] OUT OF BLASTS — {CurrentStageScore}/{CurrentStageTarget}, level lost");
            TriggerTopOut("Out Of Blasts!");
        }

        // ── BIG WEBS PUSH THE BOARD DOWN (2026-10-20 Spencer, after the blast-limit test) ──
        // Rising rows stay — they are the pressure AND the supply of new letters. What was missing
        // is a reason to be greedy while the board climbs: a small blast and a big one did the same
        // thing to the board. Now a big web also knocks out the bottom row(s), so the whole board
        // drops. Holding becomes a bet: the board rises with every swap, and you are wagering the
        // web gets big enough to buy that height back. Switch: WebDropEnabled.
        public static bool WebDropEnabled = false;   // off while INSTANT POP is tested — one change at a time
        public const int   WEB_DROP_1_ROW  = 4;   // words in the web for one row down
        public const int   WEB_DROP_2_ROWS = 6;   // ...and for two
        public static int WebDropRows(int webWords)
            => !WebDropEnabled ? 0 : webWords >= WEB_DROP_2_ROWS ? 2 : webWords >= WEB_DROP_1_ROW ? 1 : 0;

        /// <summary>Removes the bottom <paramref name="rows"/> rows and lets the board fall. Called by
        /// the tap sequence AFTER its own resolution, inside its processing gate, and awaited — so no
        /// rise or input can land mid-drop. Ice is spared (only a word melts ice); armed words that
        /// lose letters are dropped by ValidatePrimedWords, same as any other wipe.</summary>
        public IEnumerator DropBoardRows(int rows, int webWords)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rows <= 0 || rules == null || grid == null) yield break;
            var toRemove = new System.Collections.Generic.List<Vector2Int>();
            for (int row = 0; row < Mathf.Min(rows, RulesEngine.ROWS); row++)
                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    var c = rules.GetCell(col, row);
                    if (c == null || c.IsFrozen || c.IsDropTarget || c.IsAnchored) continue;
                    toRemove.Add(new Vector2Int(col, row));
                }
            WDLog.Log($"[WebDrop] {webWords}-word web -> board down {rows} row(s), {toRemove.Count} tiles removed");
            if (toRemove.Count == 0) yield break;

            if (BonusPopup.Instance != null)
                BonusPopup.Instance.Show(rows >= 2 ? "BOARD DOWN x2!" : "BOARD DOWN!",
                    new Color(0.45f, 0.95f, 1f, 1f), Vector3.zero, 1.2f);
            grid.ShakeBoard(0.18f, 0.22f);
            GameAudio.Instance?.PlayScorePowerup();
            HapticsManager.Strong();

            for (int i = 0; i < toRemove.Count; i++) rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);
            var moves = rules.ApplyGravityInDataPublic();
            if (moves != null && moves.Count > 0)
                yield return grid.StartCoroutine(grid.ApplyGravityFromEvents(moves));
            rules.ValidatePrimedWords();
            grid.SyncToRulesState(rules);
            rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
            ClusterOutline.Instance?.Refresh();
        }

        // ── MOVE LIMIT (2026-10-20 Spencer: "ok lets try it") ──────────────────────────────────
        // Each level: score the target within N moves (LevelRules.MovesFor). ONE way to lose — out of
        // moves. A rise that would push tiles off the top no longer ends the run: the top row breaks
        // off and costs OVERFLOW_MOVE_PENALTY moves, so a high board still hurts, through the one
        // number the player watches. Switch: LevelRules.MoveLimitMode.
        public static bool MoveLimitMode => LevelRules.MoveLimitMode && ScoreRunMode && !BlastMode;
        public const int   OVERFLOW_MOVE_PENALTY = 3;
        public const int   CONTINUE_EXTRA_MOVES  = 5;
        private bool _moveLoseCheckRunning;

        /// <summary>Fresh move count for a level that is starting now (called by LevelRules.Apply).</summary>
        public void ResetStageMoves() { _currentStageMovesUsed = 0; StageStats.Reset(); }

        /// <summary>GOLD LETTER = +1 MOVE (2026-10-20 Spencer). Called when a gold letter's fly-up lands on the
        /// MOVES box. Can lift moves-left above the level's starting budget. No-op once the level is won.</summary>
        public void GrantBonusMoves(int n, string why)
        {
            if (!MoveLimitMode || n <= 0 || AwaitingNextLevel || _isGameOver) return;
            _currentStageMovesUsed -= n;   // may go negative = more than the starting budget
            StageStats.GoldMoves += n;
            WDLog.Log($"[MoveLimit] +{n} move(s) from {why} -> {CurrentStageMovesRemaining} left");
        }

        private void StartMoveLimitLoseCheck()
        {
            if (!_moveLoseCheckRunning && !_isGameOver) StartCoroutine(MoveLimitLoseCheck());
        }

        /// <summary>Out of moves -> wait for the last move's words to pop, cascade and LAND their points
        /// (the target check runs on arrival), then lose only if the target still isn't met.</summary>
        private IEnumerator MoveLimitLoseCheck()
        {
            _moveLoseCheckRunning = true;
            yield return null;
            float waited = 0f;
            while (waited < 8f && ((MatchController.Instance != null && MatchController.Instance.IsProcessing)
                                   || (ScoreManager.Instance != null && ScoreManager.Instance.FlyScorePending)
                                   || (HUDManager.Instance != null && HUDManager.Instance.LettersInFlight)   // a gold letter may be bringing a move
                                   || _isRisingRow
                                   || DuckCollectPending))   // a duck on the bottom row on the last move still counts (2026-10-22)
            { waited += Time.unscaledDeltaTime; yield return null; }
            float settle = 0f;
            while (settle < 0.3f) { settle += Time.unscaledDeltaTime; yield return null; }
            _moveLoseCheckRunning = false;
            if (AwaitingNextLevel || _isGameOver || CurrentStageMovesRemaining > 0) yield break;
            WDLog.Log($"[MoveLimit] OUT OF MOVES — {CurrentStageScore}/{CurrentStageTarget}, level lost");
            try
            {
                var kv = new System.Collections.Generic.List<object> {
                    "stage", _currentStageIndex, "target", CurrentStageTarget, "score", CurrentStageScore,
                    "moves_used", StageStats.MovesMade, "moves_net", _currentStageMovesUsed, "moves_budget", CurrentStageMoveBudget,
                    "continues_used", _continuesInRun };
                kv.AddRange(StageStats.Fields());
                AnalyticsManager.Log("stage_fail", kv.ToArray());
                AnalyticsManager.FlushToDisk();
            }
            catch (System.Exception ex) { Debug.LogError($"[MoveLimit] analytics threw: {ex.Message}"); }
            TriggerTopOut("Out Of Moves!");
        }

        /// <summary>The board was full when a row tried to rise: break off the top row and charge moves,
        /// instead of ending the run.</summary>
        private void ApplyOverflowPenalty()
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            var removed = new System.Collections.Generic.List<Vector2Int>();
            if (rules != null)
            {
                int top = RulesEngine.ROWS - 1;
                for (int c = 0; c < RulesEngine.COLS; c++)
                    if (rules.GetCell(c, top) != null) { rules.ClearCell(c, top); removed.Add(new Vector2Int(c, top)); }
            }
            if (grid != null && removed.Count > 0) grid.RemoveTiles(removed);
            rules?.ValidatePrimedWords();
            ClusterOutline.Instance?.Refresh();
            _currentStageMovesUsed = Mathf.Min(CurrentStageMoveBudget, _currentStageMovesUsed + OVERFLOW_MOVE_PENALTY);
            StageStats.RecordOverflow();
            try { AnalyticsManager.Log("overflow_penalty", "stage", _currentStageIndex, "moves_left", CurrentStageMovesRemaining); }
            catch (System.Exception) { }
            WDLog.Log($"[MoveLimit] BOARD FULL — top row ({removed.Count} tiles) broke off, -{OVERFLOW_MOVE_PENALTY} moves -> {CurrentStageMovesRemaining} left");
            if (BonusPopup.Instance != null)
                BonusPopup.Instance.Show($"BOARD FULL!  -{OVERFLOW_MOVE_PENALTY} MOVES", new Color(1f, 0.42f, 0.35f, 1f), Vector3.zero, 1.25f);
            grid?.ShakeBoard(0.22f, 0.28f);
            HapticsManager.Strong();
            HUDManager.Instance?.FlashMovesPenalty();
            if (CurrentStageMovesRemaining <= 0) StartMoveLimitLoseCheck();
        }

        private int _stageStartRisesPending = 0;    // extra rises to fire post-stage-advance to hit minimum
        private bool _currentStageCleared = false;  // hit the target yet?
        // 2026-06-03 Spencer: once a stage clears mid-resolution, the rest of that
        // move's cascade score is absorbed into the cleared stage (no carryover) so
        // a single move can never clear two stages / leak score into the next.
        private bool _clearedThisResolution = false;
        private bool _wasProcessing = false;        // tracks IsProcessing edge for resolution-end release
        private int _lastStageReached = 1;          // for debrief
        private int _lastStageShortfall = 0;        // for debrief near-miss copy
        private int _lastStageTarget = 0;           // for debrief
        private int _stagesCleared = 0;             // lifetime count — balance analytics
        private int _biggestWordScore = 0;          // biggest single word for run debrief

        /// <summary>Target chip count for stage n. Placeholder curve — tune per retune spec.</summary>
        /// <summary>FULL BOARD target scale (2026-10-21 Spencer: "you just pass the levels so quickly. sometimes i only make
        /// one more and its done"). Measured: the full board averages ~590 points a move (TNT / crossing bombs 2-4k) vs
        /// ~50-60 on the rising-row board the targets were tuned for — levels fell in 1-3 moves. x5 puts LEVEL 1 at ~10
        /// average moves of 24. FIRST GUESS — retune from moves-to-clear.</summary>
        public static float FULL_TARGET_MULT = 5f;

        public static int ComputeStageTarget(int stage)
        {
            int raw = ComputeStageTargetRaw(stage);
            if (!FullBoardActive) return raw;
            return (int)System.Math.Round(raw * FULL_TARGET_MULT / 50.0) * 50;
        }

        private static int ComputeStageTargetRaw(int stage)
        {
            if (stage <= 1) return STAGE_TARGET_BASE;
            // Exponential growth: target(n) = base * growth^(n-1)
            // Compute in double to avoid float precision + int overflow at high stages.
            double t = (double)STAGE_TARGET_BASE * System.Math.Pow(STAGE_TARGET_GROWTH, stage - 1);
            // Clamp to max BEFORE rounding so we never overflow int on deep runs.
            if (t > STAGE_TARGET_MAX) return STAGE_TARGET_MAX;
            // Round to nearest 50 for clean HUD numbers
            return (int)System.Math.Round(t / 50.0) * 50;
        }

        /// <summary>Move budget for stage n. Decays then plateaus at STAGE_MOVES_FLOOR.</summary>
        public static int ComputeStageMoves(int stage)
        {
            if (stage <= 2) return STAGE_MOVES_BASE;        // S1-2: 18 moves
            if (stage <= 4) return STAGE_MOVES_BASE - 2;    // S3-4: 16
            if (stage <= 6) return STAGE_MOVES_BASE - 4;    // S5-6: 14
            if (stage <= 8) return STAGE_MOVES_BASE - 6;    // S7-8: 12
            if (stage <= 10) return STAGE_MOVES_BASE - 8;   // S9-10: 10
            return STAGE_MOVES_FLOOR;                       // S11+: 8 (plateau)
        }

        /// <summary>
        /// Per-level rise cadence override set by the level sequencer (LevelTable via
        /// ObjectiveManager). Turns-per-rise: bigger = slower/easier; 1 = full pressure.
        /// &lt; 0 = "no override, use the default onboarding ramp below". 2026-06-15 (validation MVP).
        /// </summary>
        private static int _riseCadenceOverride = -1;
        public static void SetRiseCadenceOverride(int turnsPerRise)
            => _riseCadenceOverride = turnsPerRise < 1 ? -1 : turnsPerRise;

        /// <summary>
        /// Onboarding ramp for turn-based rises (RisePerMoveDebug = true). Stages 1-4
        /// rise every 2 turns to let new players get a feel for the board. Stages 5+
        /// rise every turn (full pressure). The level sequencer can OVERRIDE this per level
        /// (SetRiseCadenceOverride) so a Boss level can rise faster than its stage number implies.
        /// </summary>
        // 2026-09-09 Spencer: pressure pulled down — a rise every 2nd turn at EVERY stage.
        // Was 2 for stages 1-4 then 1 from stage 5 on, which doubled the pressure exactly when
        // the branch-priming rule was making words harder to find. Flat 2 while that settles.
        public static int GetTurnsPerRiseForStage(int stage)
            => _riseCadenceOverride > 0 ? _riseCadenceOverride : 2;

        // ── OCCUPANCY-RESPONSIVE CADENCE (2026-09-09 Spencer) ───────────────────────
        // The cadence was FIXED regardless of the board, so it was wrong at both ends: a
        // full board got a rise every 2 moves (a crush) and an empty board got a rise every
        // 2 moves (a dead screen with two tiles and nothing to do). Spencer cleared the board
        // and had nothing to play with — the best move in the game was punished.
        // Now the board looks after itself: clear it out and it refills fast, get buried and
        // it eases off so you can dig out. Also means CLEARING is rewarded with material.
        // NOTE: MovesUntilRise reads this same property, so the HUD countdown can never
        // disagree with what actually fires — the mistake that made it flick 8 -> 1.
        public const float RISE_OCC_EMPTY = 0.20f;   // below this, refill fast
        public const float RISE_OCC_FULL  = 0.70f;   // above this, give a breather
        public const float STARVED_OCC        = 0.18f;  // below this the board refills on its own
        public const float STARVED_TARGET_OCC = 0.32f;  // and stops once it reaches this

        public int TurnsPerRiseNow
        {
            get
            {
                // 2026-09-09 Spencer: FLAT cadence. The occupancy overrides were dropping this
                // to every 1 move whenever the board was under 20% full — which, with words
                // clearing regularly, was most of the time. So the "every 2 turns" setting was
                // effectively never in force.
                // One mechanism per problem: the CADENCE is now constant and predictable, and a
                // barren board is handled by the starved-board auto-refill below, which queues
                // rises without needing a move. Speeding up the cadence was solving the same
                // problem twice, and the two were fighting each other.
                return GetTurnsPerRiseForStage(_currentStageIndex);
            }
        }

        /// <summary>
        /// Estimated player MOVES until the board tops out, given the current board
        /// headroom AND this stage's rise cadence. = (rise headroom) × (turns per rise)
        /// − (turns since last rise), so it counts down ~1 per move and automatically
        /// reads double when the cadence is every-2-moves (Spencer's point). Drives the
        /// HUD top-out danger countdown. Dynamic: clearing a tall stack raises it,
        /// filling lowers it. Turn-based (RisePerMoveDebug) only; returns the raw rise
        /// headroom otherwise (time-based rises can't be expressed in moves).
        /// </summary>
        /// <summary>
        /// Raw "player turns until top out" estimate for the HUD danger counter. Top out =
        /// a rise is ATTEMPTED while a tile is already in the top row, pushing it OVER the
        /// board (RisingRowManager.RiseRow ~86-92, regardless of top-out mode). So a tile
        /// must first REACH the top row (headroom rises), then ONE MORE rise pushes it over
        /// → rises-until-overflow = headroom + 1, × cadence, minus turns since the last rise.
        /// This value can briefly jump UP (the cadence counter resets when a rise is
        /// SCHEDULED, before the board changes on EXECUTE); HUDManager.RefreshTopOutDanger
        /// applies a monotonic clamp so that jitter never reaches the player.
        /// </summary>
        public int GetMovesUntilTopOut()
        {
            if (RulesEngine.Instance == null) return 99;
            int strictRises = RulesEngine.Instance.GetRisesUntilTopOut() + 1; // headroom + the overflow rise
            if (!RisePerMoveDebug) return Mathf.Max(0, strictRises);
            int turnsPerRise = Mathf.Max(1, GetTurnsPerRiseForStage(_currentStageIndex));
            return Mathf.Max(0, strictRises * turnsPerRise - _turnsSinceLastTurnRise);
        }

        /// <summary>Current stage target.</summary>
        /// <summary>Score needed to clear the current stage.
        ///
        /// ⚠️ KEYED ON THE **RUN LEVEL**, NOT THE INTERNAL INDEX. 2026-10-19 — with the tutorial
        /// complete a run starts at INTERNAL stage 11, so this was handing the player's very
        /// first level stage-11's target: **4,850 instead of 1,200**. Spencer scored 1,471 on
        /// what the HUD called "LEVEL 1", saw an objective-complete flourish, and the stage
        /// never advanced — it looked like a freeze but the target genuinely was not met.
        /// Same root cause as the ice-on-level-1 bug: internal stage index != displayed level.
        /// Tutorial stages (1..10) keep the raw index — they are authored and untested against
        /// this mapping.</summary>
        public int CurrentStageTarget => ComputeStageTarget(
            LevelMapPanel.IsTutorialLevel(_currentStageIndex)
                ? _currentStageIndex
                : Mathf.Max(1, LevelMapPanel.RunLevel(_currentStageIndex)));
        /// <summary>Current stage move budget. Authored/tutorial levels can override it
        /// (SetStageMoveBudgetOverride); otherwise the per-stage ComputeStageMoves curve.</summary>
        public int CurrentStageMoveBudget =>
            _stageMoveBudgetOverride > 0 ? _stageMoveBudgetOverride : ComputeStageMoves(_currentStageIndex);
        /// <summary>
        /// Progress toward current stage's chip target. Derived from PlayerScore
        /// minus the score at stage-start — so EVERY source that adds to the
        /// running total (drops, edits, swaps, board-clear bonuses, detonation
        /// bonuses) counts without needing to plumb a NotifyScoreDelta into
        /// every code path.
        /// </summary>
        public int CurrentStageScore
        {
            get
            {
                if (ScoreManager.Instance == null) return 0;
                return Mathf.Max(0, ScoreManager.Instance.PlayerScore - _stageStartScore);
            }
        }
        /// <summary>Moves remaining in the current stage.</summary>
        public int CurrentStageMovesRemaining => Mathf.Max(0, CurrentStageMoveBudget - _currentStageMovesUsed);

        // ── Vault-level rules (read from the active objective). ──
        private Objective ActiveObjective => ObjectiveManager.Instance != null ? ObjectiveManager.Instance.Active : null;
        /// <summary>The active level runs rises OFF (e.g. a vault level).</summary>
        public bool ActiveRisesOff { get { if (FullBoardActive) return true; var o = ActiveObjective; return o != null && o.RisesOff; } }
        /// <summary>The active level uses a move budget instead of the rise clock (vault/loot levels).</summary>
        public bool IsMoveCapLevel { get { var o = ActiveObjective; return o != null && o.UsesMoveCap; } }
        // Per-level vault move budget override set by the level sequencer (LevelTable). < 0 = use the
        // Inspector default (_vaultMoveBudget). Lets each Vault level in the run set its own budget
        // (tighten the triage as the run escalates). 2026-06-15 (validation MVP).
        private int _vaultMoveBudgetOverride = -1;
        public void SetVaultMoveBudgetOverride(int moves) => _vaultMoveBudgetOverride = moves < 1 ? -1 : moves;
        // Per-level move-budget override for NON-vault authored/tutorial levels (e.g. Level 1 = 30).
        // Mirrors the vault override; set by the level sequencer each InstallLevel (<1 = no override
        // → use the ComputeStageMoves curve). 2026-06-25 Spencer.
        private int _stageMoveBudgetOverride = -1;
        public void SetStageMoveBudgetOverride(int moves) => _stageMoveBudgetOverride = moves < 1 ? -1 : moves;
        // Authored non-rising levels (e.g. L7/L8) can TOP OUT when the move budget is spent without
        // clearing the goal — re-adds a scoped move-limit fail (survival's global move-fail is off; only
        // rising tops out). Set per-InstallLevel; false for every level that doesn't opt in. 2026-07-09 Spencer.
        private bool _moveLimitTopOut = false;
        public void SetMoveLimitTopOut(bool on) => _moveLimitTopOut = on;
        // Edit-focused levels (e.g. L6) can opt edits/swaps INTO the move counter (normally only drops
        // count — edits are free recovery). Set per-InstallLevel; false everywhere else. 2026-07-09 Spencer.
        // DEBUG/playtest: when true, edits count as moves on EVERY level, overriding the per-level flag.
        // Toggle from the FX Test Menu. Lets us feel-test "edits always cost a turn" across the whole run.
        // 2026-07-09 Spencer.
        public static bool EditsCountAsMovesGlobalOverride = false;
        private bool _editsCountAsMoves = false;
        public bool EditsCountAsMoves => _editsCountAsMoves || EditsCountAsMovesGlobalOverride || MoveLimitMode;   // move limit: every swap/swipe is a move, on every level
        public void SetEditsCountAsMoves(bool on) => _editsCountAsMoves = on;
        /// <summary>This level runs on a fixed move budget (authored/tutorial) instead of the rise/
        /// top-out clock → the MOVES HUD shows budget-remaining, not moves-to-top-out. 2026-06-25.</summary>
        public bool UsesStageMoveBudget => _stageMoveBudgetOverride > 0;
        /// <summary>Flat move budget for the active vault/loot level (0 if not one).</summary>
        public int VaultMoveCap => IsMoveCapLevel ? (_vaultMoveBudgetOverride > 0 ? _vaultMoveBudgetOverride : _vaultMoveBudget) : 0;
        /// <summary>Moves left this vault level.</summary>
        public int VaultMovesRemaining => Mathf.Max(0, VaultMoveCap - _currentStageMovesUsed);
        // Vault starting-board seed params (read by VaultObjective.Tick → RulesEngine.SeedVaultBoard).
        public int   VaultStartFillRows   => _vaultStartFillRows;
        public float VaultFillDensity     => _vaultFillDensity;
        public int   VaultHeightSpread    => _vaultHeightSpread;
        public int   VaultChestMinSpacing => _vaultChestMinSpacing;
        public int   VaultRegularCount    => _vaultRegularCount;
        public int   VaultMidCount        => _vaultMidCount;
        public int   VaultMidWordLen      => _vaultMidWordLen;
        public int   VaultHighCount       => _vaultHighCount;
        public int   VaultHighWordLen     => _vaultHighWordLen;
        public int   VaultTotalChests     => Mathf.Max(1, _vaultRegularCount + _vaultMidCount + _vaultHighCount);
        public int   IceTileCount         => Mathf.Max(1, _iceTileCount); // ICE objective: tiles to freeze
        /// <summary>True if the current stage's target has been hit.</summary>
        public bool IsCurrentStageCleared => _currentStageCleared;

        /// <summary>TRUE from the moment a level's target is met until the NEXT level actually
        /// starts. 2026-10-19 Spencer: "is there anyway to just lock input after you have
        /// reached the goal score for the level?" — yes, and this is it.
        ///
        /// `_currentStageCleared` CANNOT be used for this: it is set true at the top of the
        /// clear block and AdvanceToNextStage sets it false ~10 lines later, so by the time any
        /// input path reads it, it is false again. That is why guarding on it did nothing.
        ///
        /// This latch is owned by the PLAYER'S view of the run, not the engine's: the engine
        /// advances the stage instantly, but for the player the level is not over until the
        /// next one is on screen. Everything in that gap — input, scoring, the HUD counter —
        /// must behave as "level finished". Cleared by LevelRules.Apply when the next level
        /// genuinely begins.</summary>
        public static bool AwaitingNextLevel { get; private set; }

        public static void MarkAwaitingNextLevel() => AwaitingNextLevel = true;
        public static void ClearAwaitingNextLevel() => AwaitingNextLevel = false;

        /// <summary>Drop any rises queued against the previous level's board. AdvanceToNextStage measures
        /// the OLD board at clear time; the new level reseeds its own, so those rows would arrive as a
        /// surprise rise on a fresh board. Called by LevelRules.Apply when a level installs. 2026-10-20.</summary>
        public void ClearRiseQueueForNewLevel()
        {
            if (_stageStartRisesPending > 0 || _riseScheduledPending)
                WDLog.Log($"[RisePerMove] new level — dropping {_stageStartRisesPending} queued rise(s), scheduled={_riseScheduledPending}");
            _stageStartRisesPending = 0;
            _riseScheduledPending   = false;
        }
        /// <summary>Current stage number (1, 2, 3, … unbounded).</summary>
        public int CurrentStageIndex => _currentStageIndex;

        /// <summary>Debrief: last stage reached before run ended.</summary>
        public int LastStageReached => _lastStageReached;
        /// <summary>Debrief: points short of last stage's target (0 if cleared).</summary>
        public int LastStageShortfall => _lastStageShortfall;
        /// <summary>Debrief: target the player needed to hit at run end.</summary>
        public int LastStageTarget => _lastStageTarget;

        /// <summary>
        /// v1.5 placeholder for a roguelite modifier offer presented at stage clear.
        /// Defined as an opaque type now so the StageClearContext.Offers field has
        /// a proper signature without committing to implementation. v1.5 will
        /// flesh this out with Id/DisplayName/Description/Icon/Apply hooks.
        /// </summary>
        public class StageRewardOffer { }

        /// <summary>
        /// Snapshot of a stage's state at the moment it was cleared. Captured BEFORE
        /// stage advancement so subscribers see the cleared stage's data, not the
        /// next stage's. Subscribers should treat payload fields as the source of
        /// truth for cleared-stage info — reading SurvivalManager.Instance state
        /// inside a handler returns stage N+1 because advancement already ran.
        /// Future-aware: `Offers` is reserved for v1.5 roguelite modifier choices
        /// (null/empty in v1; populated by reward system in v1.5).
        /// </summary>
        public struct StageClearContext
        {
            public int ClearedStage;
            public int TargetScore;
            public int StageScore;
            public int MovesUsed;
            public int MovesBudget;
            public int RisesFired;
            public float Occupancy;        // 0..1, board fill at moment of clear
            public int CoinsEarned;        // MVP P1: skill-tied via overshoot formula
            public System.Collections.Generic.IReadOnlyList<StageRewardOffer> Offers; // v1.5 placeholder (null in v1)
        }

        // MVP P1 overshoot coin formula: coinsEarned = clamp(5 + overshoot/5, 10, 60)
        // overshoot = StageScore - TargetScore on clinching move. Floor of 10 protects
        // letter-luck variance, cap of 60 prevents one-stage farming.
        public const int COIN_BASE        = 5;
        public const int COIN_SCALAR      = 5;   // larger = flatter curve
        public const int COIN_FLOOR       = 10;
        public const int COIN_CAP_PER_STAGE = 60;
        /// <summary>Move-limit mode: coins per move left over when the level is cleared (on top of LevelRules.BaseCoinsFor).</summary>
        public static int COINS_PER_LEFTOVER_MOVE = 3;
        public static bool BONUS_BLAST_ENABLED  = true;
        public static int  BONUS_MAX_ROCKETS    = 10;
        public static int  COINS_PER_BONUS_TILE = 1;
        /// <summary>Coins the bonus blast has paid for the level just cleared (already in the wallet); the Well Done
        /// modal adds it to its coin chip and clears it.</summary>
        public static int  PendingBonusCoins;

        /// <summary>Fires when the player hits a stage's chip target. Carries cleared stage snapshot.</summary>
        public System.Action<StageClearContext> OnStageCleared;
        /// <summary>Fires when the player fails a stage (move budget exhausted, target missed).</summary>
        public System.Action<int> OnStageFailed;

        /// <summary>
        /// Called from MatchController.CompleteDropBookkeeping for human drops.
        /// Increments move counters. Counter caps at threshold so a rise queued
        /// mid-animation can't double-fire when another drop lands.
        /// Also advances stage-move budget and checks for stage-fail.
        /// CheckStageClear runs BEFORE the fail check so a drop that both
        /// exhausts budget AND crosses target registers as a clear, not a fail.
        /// </summary>
        public void NotifyDropCommitted()
        {
            // Bonus Mode: drops are "free" — no move-counter increment, no rising-row
            // tick, no stage-budget consumption. Preserves flow-state (Schüll machine
            // zone) and matches the Balatro shop-phase pattern where run clock pauses.
            if (BonusMode.Instance != null && BonusMode.Instance.IsActive)
                return;

            _movesPlayed++;
            _movesSinceLastRise = Mathf.Min(_movesSinceLastRise + 1, CurrentMovesPerRise);
            _currentStageMovesUsed++;
            StageStats.MovesMade++;   // real moves made (moves_used no longer goes negative with gold refunds) — 2026-10-23

            // Snapshot the stages-cleared counter so we can detect whether
            // the upcoming CheckStageClear call advanced the stage. If it did,
            // we suppress this drop's rise (the win shouldn't be followed by
            // a punitive rise tick before the StageClearModal pops out).
            int stagesClearedBefore = _stagesCleared;

            // Clear-check — if this drop crossed the target, the stage advances.
            CheckStageClear();

            bool clearedStageThisDrop = _stagesCleared > stagesClearedBefore;

            // Debug toggle: per-move rises. Fires a rise after every human drop
            // resolves. NotifyDropCommitted is invoked from CompleteDropBookkeeping
            // while MatchController.IsProcessing is still true (word scoring,
            // detonations, gravity), so we defer the rise via a wait-coroutine
            // until processing clears.
            WDLog.Log($"[RisePerMove] drop committed | toggle={RisePerMoveDebug} rising={_isRisingRow} autoDrop={_isAutoDropping} paused={_isOverlayPaused} clearedThisDrop={clearedStageThisDrop}");
            if (RisePerMoveDebug && !_isRisingRow && !_isAutoDropping && !_isOverlayPaused && !clearedStageThisDrop && !ActiveRisesOff && !BlastMode)
            {
                // Onboarding ramp: stages 1-4 rise every 2 turns, stages 5+ every turn.
                _turnsSinceLastTurnRise++;
                int turnsPerRise = TurnsPerRiseNow;   // occupancy-responsive
                if (_turnsSinceLastTurnRise < turnsPerRise)
                {
                    WDLog.Log($"[RisePerMove] holding rise — stage {_currentStageIndex}, turn {_turnsSinceLastTurnRise}/{turnsPerRise}");
                }
                else
                {
                    _turnsSinceLastTurnRise = 0;
                    // Glade Stillness: consume a rise-skip charge if active; suppress this turn's rise.
                    if (ConsumeGladeRiseIfActive())
                    {
                        WDLog.Log("[RisePerMove] rise suppressed by Glade Stillness");
                    }
                    else
                    {
                        WDLog.Log($"[RisePerMove] scheduling rise (stage {_currentStageIndex}, cadence {turnsPerRise})");
                        _riseTimerSeconds = 0f;
                        _riseScheduledPending = true; // a rise is queued; if a tile is already in the top row it's FATAL
                        StartCoroutine(WaitAndExecuteRise());
                    }
                }
            }

            // Phase 11b+: stage-move-budget fail is REMOVED for time-based
            // Survival. Rising rows are now the sole pressure source — death
            // comes from topout, not from running out of an invisible move
            // counter. _currentStageMovesUsed still increments above so the
            // stage-clear analytics record retains a moves_used signal.
            //
            // EXCEPTION (2026-07-09 Spencer): authored non-rising levels that opt in
            // (MoveLimitTopOut, e.g. L7/L8) DO top out when the move budget is spent
            // without meeting the goal — otherwise a rises-off level has no lose
            // condition. CheckStageClear ran above, so a drop that BOTH hits the target
            // and empties the budget counts as a CLEAR (clearedStageThisDrop), not a
            // fail. Only drops reach here, so edits stay free (per design). The MOVES HUD
            // already shows CurrentStageMovesRemaining for these levels (UsesStageMoveBudget).
            if (MoveLimitMode)
            {
                // Deferred: the move that spent the budget has not POPPED yet (instant pop fires after
                // this), so judging now would fail a level its own last word was about to win.
                if (CurrentStageMovesRemaining <= 0 && !clearedStageThisDrop) StartMoveLimitLoseCheck();
            }
            else if (_moveLimitTopOut && UsesStageMoveBudget
                && !clearedStageThisDrop && CurrentStageMovesRemaining <= 0)
            {
                WDLog.Log($"[MoveLimit] budget spent ({_currentStageMovesUsed}/{CurrentStageMoveBudget}), goal not met → top out.");
                TriggerTopOut();
            }
        }

        /// <summary>
        /// Trigger for stage-clear detection. Called from MatchController after
        /// score is added, and also safe to call from any score-source path
        /// (BoardSwap, detonation clear bonuses, etc.) to ensure clear fires
        /// promptly instead of waiting for the next Update tick.
        /// CurrentStageScore is derived from PlayerScore so we don't need the
        /// points argument — kept for backwards compat with existing callers.
        /// </summary>
        /// <summary>
        /// Freeze (true) or thaw (false) the rising-row + post-clear-boost + auto-drop
        /// timers. Used by full-screen overlays like StageClearModal that should pause
        /// Survival's time-driven systems while the player views the overlay. Does NOT
        /// touch HandManager input — overlay owners gate input separately.
        /// </summary>
        public void SetOverlayPaused(bool paused)
        {
            _isOverlayPaused = paused;
        }

        public void NotifyScoreDelta(int pointsAdded = 0)
        {
            // Drought rebate: when player scores a word while at 0 edits, queue the
            // next auto-drop to be an edit refill. Single-use flag — consumed by
            // the next auto-drop. Rewards good play at rock-bottom without breaking
            // the panic-button economy (only triggers at literal 0).
            if (pointsAdded > 0 && !_pendingEditDroughtRebate)
            {
                var mc = MatchController.Instance;
                if (mc != null && mc.GetRewritesRemaining(MatchController.PLAYER_HUMAN) <= 0)
                {
                    _pendingEditDroughtRebate = true;
                }
            }

            CheckStageClear();
        }

        /// <summary>
        /// Checks whether the current stage's chip target has been hit. If so,
        /// fires OnStageCleared and advances to the next stage. Loops while the
        /// score still exceeds the next stage's target — a single big cascade
        /// can carry the player past multiple stage thresholds in one drop, and
        /// each crossing fires its own event so the modal queue can sequence
        /// them. The MAX_STAGE_CLEARS_PER_CALL cap is a safety net against
        /// runaway loops (e.g. a Score == int.MaxValue corruption).
        /// Idempotent within a stage — the _currentStageCleared guard prevents
        /// double-fire after AdvanceToNextStage resets it.
        /// </summary>
        public void CheckStageClear()
        {
            bool resolving = MatchController.Instance != null && MatchController.Instance.IsProcessing;

            // 2026-06-03 Spencer: NO score carries between stages, and a single move
            // can never clear two. Once we've cleared a stage during a still-resolving
            // move, keep the next stage's baseline PINNED to the running total so the
            // rest of this move's cascade score is absorbed into the cleared stage —
            // it can't accumulate in (or clear) the next stage mid-cascade. Released
            // at resolution-end (Update) and here once the board settles.
            if (_clearedThisResolution)
            {
                if (resolving)
                {
                    if (ScoreManager.Instance != null) _stageStartScore = ScoreManager.Instance.PlayerScore;
                    return;
                }
                _clearedThisResolution = false; // settled — resume normal evaluation
            }

            // Cap chosen generously — a realistic multi-stage cascade clears
            // 1-3 thresholds at once; 32 handles pathological scores without
            // bounding legitimate play. Loop is O(stages) and each iteration is
            // cheap (one event invocation + state advance).
            const int MAX_STAGE_CLEARS_PER_CALL = 32;
            int safety = 0;
            while (safety < MAX_STAGE_CLEARS_PER_CALL)
            {
                if (_currentStageCleared) return;
                // 2026-06-08: an active objective IS the win condition (replaces the score
                // target). Clear only when it's complete; otherwise the score target gates the
                // clear (endless mode with no objective).
                // ScoreRunMode overrides: the objective is decoration in an endless score
                // run, so the score target governs the clear.
                bool objMode = !ScoreRunMode
                               && ObjectiveManager.Instance != null && ObjectiveManager.Instance.HasObjective;
                if (objMode)
                {
                    if (IsMoveCapLevel)
                    {
                        // Vault (loot) level: a NO-FAIL triage beat. It ADVANCES (banks loot →
                        // next level) when the player runs OUT OF MOVES or has looted everything —
                        // never gated on "crack them all". Reuses this whole advance path (coins,
                        // AdvanceToNextStage, stage-clear modal) — NOT a run-end. 2026-06-09.
                        bool outOfMoves = _currentStageMovesUsed >= VaultMoveCap;
                        bool allLooted  = RulesEngine.Instance != null && RulesEngine.Instance.CountAnchoredCells() == 0;
                        if (!outOfMoves && !allLooted) return;
                    }
                    else if (!ObjectiveManager.Instance.Active.IsComplete) return;
                }
                else
                {
                    // DIAGNOSTIC 2026-10-19: say WHY a clear was refused. "It freezes after I
                    // finish the level" has twice turned out to be the target quietly not being
                    // met, and a silent `return` gives nothing to go on.
                    // CRATE GOAL (full board, 2026-10-21): the level is won when every crate is broken — score doesn't gate it.
                    if (LevelRules.GoalsActiveFor(_currentStageIndex))
                    {
                        if (!LevelRules.AllGoalsDone()) return;   // every goal done = win; score doesn't gate it
                    }
                    else if (CurrentStageScore < CurrentStageTarget)
                    {
                        if (Time.frameCount % 120 == 0)
                            WDLog.Log($"[StageGate] NOT cleared — stage {_currentStageIndex} " +
                                      $"(LEVEL {LevelMapPanel.DisplayNum(_currentStageIndex)}) " +
                                      $"score {CurrentStageScore}/{CurrentStageTarget} " +
                                      $"(player {(ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : -1)}, " +
                                      $"baseline {_stageStartScore})");
                        return;
                    }
                }
                WDLog.Log($"[StageGate] PASSED — stage {_currentStageIndex} " +
                          $"(LEVEL {LevelMapPanel.DisplayNum(_currentStageIndex)}) " +
                          $"score {CurrentStageScore}/{CurrentStageTarget} — advancing");
                safety++;

                // Capture everything we need BEFORE mutating state or calling handlers,
                // so an event-handler exception can't leave us stuck in a half-advanced
                // state (the earlier bug: _currentStageCleared=true but _currentStageIndex
                // didn't increment → CheckStageClear early-returns forever, stage stuck).
                int clearedStage    = _currentStageIndex;
                int clearedTarget   = CurrentStageTarget;
                int clearedScore    = CurrentStageScore;
                int clearedMoves    = _currentStageMovesUsed;
                int clearedBudget   = CurrentStageMoveBudget;
                int clearedRises    = _risesInCurrentStage;
                float occupancy     = RulesEngine.Instance != null ? RulesEngine.Instance.GetBoardOccupancy() : 0f;

                int overshoot = Mathf.Max(0, clearedScore - clearedTarget);
                int coinsEarned = Mathf.Clamp(COIN_BASE + (overshoot / COIN_SCALAR), COIN_FLOOR, COIN_CAP_PER_STAGE);
                // ROYAL-MATCH COINS (2026-10-21 Spencer: "the amount of coins should probably tie into how many moves are
                // left over"). Royal Match: base 10 / 20 / 30 by difficulty, leftover moves pay on top. Step 1 is a flat
                // per-move bonus; step 2 (planned) turns leftover moves into rockets that pay per tile destroyed.
                int movesLeftAtClear = Mathf.Max(0, clearedBudget - clearedMoves);
                if (MoveLimitMode)
                    coinsEarned = LevelRules.BaseCoinsFor(LevelMapPanel.RunLevel(clearedStage)) + movesLeftAtClear * COINS_PER_LEFTOVER_MOVE;
                // STEP 2 — BONUS BLAST (2026-10-21 Spencer "ok lets try it"): leftover moves become rockets (max
                // BONUS_MAX_ROCKETS) that fire with any unused specials; every tile destroyed pays a coin. Replaces the flat
                // per-move coins: the base pays now, the blast adds its coins as they land (PendingBonusCoins -> modal).
                if (MoveLimitMode && BONUS_BLAST_ENABLED && HandManager.Instance != null)
                {
                    coinsEarned = LevelRules.BaseCoinsFor(LevelMapPanel.RunLevel(clearedStage));
                    PendingBonusCoins = 0;
                    int rockets = Mathf.Min(BONUS_MAX_ROCKETS, movesLeftAtClear);
                    HUDManager.MovesDisplayOverride = movesLeftAtClear;   // the counter shows what is left, not the next level
                    HandManager.Instance.BeginBonusBlast(rockets, movesLeftAtClear, coinsEarned);
                }
                WDLog.Log($"[Coins] LEVEL {LevelMapPanel.DisplayNum(clearedStage)} cleared: {coinsEarned} coins " +
                          $"(base {LevelRules.BaseCoinsFor(LevelMapPanel.RunLevel(clearedStage))} + {movesLeftAtClear} moves left x {COINS_PER_LEFTOVER_MOVE})");
                CoinWallet.Add(coinsEarned);
                // Hand the reward to the map so its coins FLY from the just-cleared node's star into the coins pill
                // (Royal-Match cascade) when the map shows. 2026-07-14 Spencer.
                LevelMapPanel.Instance?.SetPendingCoinReward(coinsEarned);

                // ── OBJECTIVE-COMPLETE CELEBRATION (2026-09-10 Spencer) ───────────────
                // The checkmark drop + "bing" that used to fire when an objective completed.
                // Both still exist in full — they were wired only to ObjectiveManager, which
                // is not what ends a stage in score-run mode, so hitting the score target
                // passed in silence. Fires here, on the rising edge of the clear, so it lands
                // BEFORE the Well Done modal rather than competing with it.
                if (!_currentStageCleared)
                {
                    // Land the score on its true total BEFORE celebrating — it was still
                    // counting up as the win screen took over. 2026-09-10 Spencer.
                    HUDManager.Instance?.SnapScoreToTarget();
                    GameAudio.Instance?.PlayBing();
                    HUDManager.Instance?.FlashObjectiveComplete();
                }

                // Advance stage state FIRST — before any external callbacks — so that
                // even if the event handler blows up, state machine is consistent.
                _currentStageCleared = true;
                AwaitingNextLevel    = true;   // locks input + pins the HUD until the next level starts
                _stagesCleared++;
                AdvanceToNextStage();

                // If a cascade is still resolving, absorb the rest of its score into
                // THIS cleared stage (via the pin at the top of subsequent calls) so
                // nothing leaks into the next stage. Immediate one-shot scores don't
                // need this — the baseline already jumped by the full amount, so there
                // is no remaining score to carry. 2026-06-03 Spencer.
                if (resolving) _clearedThisResolution = true;

                // MVP P4: refill booster charge on stage clear ("1 charge per stage").
                BoosterManager.Instance?.RefillForStage();

                WDLog.Log($"[Stage] CLEARED stage {clearedStage}: target {clearedTarget}, scored {clearedScore}, overshoot {overshoot}, coins +{coinsEarned}, moves {clearedMoves}/{clearedBudget}, rises {clearedRises}");

                // Safe to fire analytics now — state is already advanced
                try
                {
                    AnalyticsManager.Log("stage_clear",
                        "stage", clearedStage,
                        "target", clearedTarget,
                        "score", clearedScore,
                        "overshoot", overshoot,
                        "coins_earned", coinsEarned,
                        "moves_used", StageStats.MovesMade, "moves_net", clearedMoves,
                        "moves_budget", clearedBudget,
                        "rises_fired", clearedRises,
                        "occupancy", Mathf.RoundToInt(occupancy * 100f),
                        "move_limit", MoveLimitMode ? 1 : 0,
                        "words_3", StageStats.Len3, "words_4", StageStats.Len4, "words_5", StageStats.Len5,
                        "words_6p", StageStats.Len6Plus, "plants_made", StageStats.PlantsMade,
                        "plants_solved", StageStats.PlantsSolved, "overflows", StageStats.Overflows,
                        "gold_moves", StageStats.GoldMoves, "specials_made", StageStats.SpecialsMade,
                        "specials_fired", StageStats.SpecialsFired, "showers", StageStats.Showers,
                        "rains", StageStats.Rains, "smart_seeds", StageStats.SmartSeeds, "moves_left", movesLeftAtClear);
                }
                catch (System.Exception ex) { Debug.LogError($"[Stage] Analytics log threw: {ex.Message}"); }

                // Finally, fire the subscribed handler. Wrap in try/catch so a handler
                // bug cannot prevent the stage-advance that already happened above.
                // Payload is the cleared-stage snapshot (data already captured before
                // AdvanceToNextStage ran, so subscribers see stage N, not stage N+1).
                var ctx = new StageClearContext
                {
                    ClearedStage = clearedStage,
                    TargetScore  = clearedTarget,
                    StageScore   = clearedScore,
                    MovesUsed    = clearedMoves,
                    MovesBudget  = clearedBudget,
                    RisesFired   = clearedRises,
                    Occupancy    = occupancy,
                    CoinsEarned  = coinsEarned,
                    Offers       = null,
                };
                // ── THE ENGINE (2026-09-09 Spencer) ───────────────────────────────
                // Auto-grant for now, deliberately: prove that compounding power makes runs
                // feel different BEFORE building a choice screen for it. If it does, this
                // becomes a pick-one-of-three; if it does not, we have not wasted a modal.
                if (ScoreRunMode)
                {
                    var ids = System.Enum.GetValues(typeof(RunBuffs.Id));
                    var pick = (RunBuffs.Id)ids.GetValue(Random.Range(0, ids.Length));
                    RunBuffs.Grant(pick);
                    HUDManager.Instance?.FlashBuffGranted(RunBuffs.Title(pick), RunBuffs.Describe(pick));
                }

                try { OnStageCleared?.Invoke(ctx); }
                catch (System.Exception ex) { Debug.LogError($"[Stage] OnStageCleared handler threw: {ex}"); }

                // Objective mode: clear exactly ONE stage per completed objective. RETIRE (don't
                // clear) so the completed 3/3 stays on the HUD behind the stage-clear modal; it
                // stops being the live win condition (so the loop won't re-clear) and resets to a
                // fresh objective when the modal closes. 2026-06-09.
                if (objMode) { ObjectiveManager.Instance?.RetireForStageClear(); return; }

                // ── SCORE MODE MUST RETIRE TOO, OR THE RUN NEVER ADVANCES. 2026-10-19 ────
                // THIS is why clearing a level hung on the "Level Cleared" screen forever.
                // RetireForStageClear only ran in objMode, and objMode = !ScoreRunMode — so
                // with the score-mode pivot it stopped running at all. Retiring is what sets
                // ObjectiveManager._retired, which on modal close fires ClearObjective(),
                // which nulls Active, which is the ONLY condition that lets InstallLevel run
                // for the next stage. Without it: stage clears, modal shows, modal dismisses,
                // and the game sits there with no HUD (overlay pause) and no next level.
                // No `return` here — score mode must keep looping so one huge cascade can
                // clear several stages, which is the objMode-only behaviour above.
                ObjectiveManager.Instance?.RetireForStageClear();

                // Loop: if the same NotifyScoreDelta call's score also crossed
                // the next stage's target, fire that event too. The modal
                // subscriber queues each, presenting them in sequence.
            }

            // Only warn if there's ACTUALLY a pending clear that got capped —
            // not just because we ran the loop the max number of times.
            if (safety >= MAX_STAGE_CLEARS_PER_CALL
                && !_currentStageCleared
                && CurrentStageScore >= CurrentStageTarget)
            {
                Debug.LogWarning($"[Stage] CheckStageClear hit safety cap ({MAX_STAGE_CLEARS_PER_CALL}) with more clears pending. Score={CurrentStageScore} Target={CurrentStageTarget} — investigate score corruption or runaway loop.");
            }
        }

        /// <summary>Internal stage transition. Snapshots new stage-start score
        /// and resets the rising-row counter so each stage starts with a full
        /// rise window. Also checks board state: if a big chain cleared out the
        /// board, schedules extra rises to bring it up to STAGE_START_MIN_ROWS
        /// so the player isn't punished for skilled clearing.
        ///
        /// MVP P5: NO score carry-over. Overshoot is rewarded via the overshoot
        /// coin formula (max 60 coins per clear). Bringing carry-over back would
        /// let a single huge word chain-clear multiple stages, which feels broken.</summary>
        private void AdvanceToNextStage()
        {
            // Advance by the player's CURRENT score so the next stage starts
            // at score 0 relative to its target. Excess from the clinching word
            // does NOT carry forward.
            int justClearedTarget = (ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : _stageStartScore) - _stageStartScore;
            _currentStageIndex++;
            _stageStartScore       += justClearedTarget;
            _currentStageMovesUsed = 0;
            _currentStageCleared   = false;
            // Fresh rise window per Spencer's ask — full cadence before first rise.
            _movesSinceLastRise     = 0;
            _riseTimerSeconds       = 0f;   // Phase 11b time-based rise clock
            _turnsSinceLastTurnRise = 0;    // turn-based rise counter — also fresh per stage

            // Reset per-stage rise counter for analytics
            _risesInCurrentStage   = 0;

            // Board replenish: if the clear-that-ended-the-stage also cleared
            // most of the board, queue enough rises to bring it up to a
            // playable minimum. Fires in Update() over subsequent frames.
            // 2026-10-21: NOT in score runs — LevelRules.Apply gives every level a fresh 4-row board, so this counted the
            // OLD board and fired a rise the moment the new level slid in (Spencer: "a row rise precedes any move").
            int rows = CountPopulatedRows();
            _stageStartRisesPending = ScoreRunMode ? 0 : Mathf.Max(0, STAGE_START_MIN_ROWS - rows);
        }

        /// <summary>DEBUG/TEST: jump straight to a stage and install its level (objective + board) so a
        /// specific level can be tested without playing up to it. Wired to the FX Test Menu. 2026-06-17.</summary>
        public void DebugJumpToStage(int stage)
        {
            _currentStageIndex      = Mathf.Max(1, stage);
            _currentStageMovesUsed  = 0;
            _currentStageCleared    = false;
            AwaitingNextLevel       = false;
            _movesSinceLastRise     = 0;
            _turnsSinceLastTurnRise = 0;
            _riseTimerSeconds       = 0f;
            // Clean-slate the map + world-complete modal so a jump made mid-flow doesn't leave them stuck (which
            // would block this and every later jump). 2026-07-13 Spencer.
            LevelMapPanel.Instance?.HardReset();
            WorldCompleteModal.Instance?.ForceHide();
            ObjectiveManager.Instance?.InstallLevel(_currentStageIndex, viaDebugJump: true);
            WDLog.Log($"[DebugJump] Jumped to stage {_currentStageIndex}");
        }

        /// <summary>True if any tile on the board is currently mid-animation.
        /// Used to defer rising-row fires so we don't interrupt gravity/drops/explosions.</summary>
        private bool IsBoardAnimating()
        {
            if (GridManager.Instance == null) return false;
            // BOARD ENTRY counts as animating. 2026-10-20 Spencer: "there was already a populated board
            // there and then another board animated over top of it". Log: PrepBoardEntry parked the board
            // and a stage-start rise fired the SAME frame. The rise's new row was never in the park
            // snapshot, so it sat at rest on screen (the "board already there"), and the slide-in then
            // wrote every tracked tile back to its PRE-rise position — all 16 MISPLACED by exactly one
            // row. The entry snapshot is only valid if nothing changes the board until the slide ends.
            if (GridManager.Instance.BoardEntryInProgress) return true;
            for (int col = 0; col < RulesEngine.COLS; col++)
            for (int row = 0; row < RulesEngine.ROWS; row++)
            {
                Tile t = GridManager.Instance.GetTile(col, row);
                if (t != null && t.IsAnimating) return true;
            }
            return false;
        }

        /// <summary>Counts how many rows have at least one tile in them.</summary>
        private int CountPopulatedRows()
        {
            if (RulesEngine.Instance == null) return 0;
            int count = 0;
            for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                for (int c = 0; c < RulesEngine.COLS; c++)
                {
                    if (RulesEngine.Instance.GetCell(c, r) != null)
                    {
                        count++;
                        break;  // this row has at least one tile, check next row
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Called by ExecuteRisingRow after a rise completes. Tracks how many
        /// rises fire within the current stage for balance analytics.
        /// </summary>
        private void NotifyRiseFired()
        {
            _risesInCurrentStage++;
        }

        /// <summary>Public hook for systems that track biggest-word-score this run.</summary>
        public void RecordWordScore(int wordScore)
        {
            if (wordScore > _biggestWordScore)
                _biggestWordScore = wordScore;
        }

        /// <summary>
        /// Fires a structured run_end analytics event. Call ONCE per run when
        /// the death condition is determined. cause = "topout" or "stage_fail".
        /// This is the top-level record — read it + per-stage events to compute
        /// median final stage, clear rates, etc.
        /// </summary>
        private void EmitRunEndAnalytics(string cause)
        {
            int finalScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            AnalyticsManager.Log("run_end",
                "cause", cause,
                "final_score", finalScore,
                "final_stage", _currentStageIndex,
                "stages_cleared", _stagesCleared,
                "moves_played", _movesPlayed,
                "biggest_chain", _longestChain,
                "biggest_word", _biggestWordScore,
                "run_duration_sec", Mathf.RoundToInt(_elapsedTime),
                "no_assist", NoAssistMode);

            // Flush to disk immediately so data survives even if Unity doesn't
            // properly fire Application.quitting (editor play-mode exit can
            // skip it). Safe to call multiple times per session — writes the
            // full event list each time.
            AnalyticsManager.FlushToDisk();
        }

        // ── Post-Clear Boost ─────────────────────────────────────────────────────
        // After a big detonation, temporarily boost assist systems so the board
        // regains momentum instead of becoming a sparse dead lull.
        // Fades after a few player actions.
        private int _postClearBoostDrops;  // how many more player drops get boosted
        private float _postClearBoostTime; // wall-clock falloff

        /// <summary>How many drops of boost remain (0 = no boost active).</summary>
        public int PostClearBoostRemaining => _postClearBoostDrops;

        /// <summary>Is a post-clear boost currently active? Always false under NoAssistMode.</summary>
        public bool IsPostClearBoosted => !NoAssistMode && _postClearBoostDrops > 0;

        /// <summary>
        /// Call after a detonation resolves. If it was a big clear (8+ cells),
        /// activate the boost for the next few draws and the next rising row.
        /// </summary>
        public void NotifyDetonation(int cellsCleared, int chainDepth)
        {
            // Only boost on significant clears — small detonations don't need it
            if (cellsCleared < 6) return;

            // NoAssistMode gates PostClearBoost (dynamic difficulty assist) but NOT
            // Renewal Row (pacing keepalive — prevents degenerate empty-board state
            // after a big clear). The two live in the same function but are different
            // mechanics.
            if (!NoAssistMode)
            {
                // Scale boost duration with clear size
                int boostDrops = cellsCleared >= 12 ? 4 : (cellsCleared >= 8 ? 3 : 2);

                // Don't stack — just refresh
                _postClearBoostDrops = Mathf.Max(_postClearBoostDrops, boostDrops);
                _postClearBoostTime = 15f; // falloff after 15s even if player is slow
            }

//             WDLog.Log($"[SurvivalManager] PostClearBoost activated: {boostDrops} drops " +
                      // $"(cleared={cellsCleared}, chain={chainDepth})");

            // ── Board Clear Bonus + Renewal Row ──
            // Use raw tile count instead of occupancy — occupancy is unreliable
            // mid-resolution because gravity hasn't run yet.
            if (RulesEngine.Instance != null)
            {
                int tilesRemaining = RulesEngine.Instance.CountOccupied();
                int totalCells = RulesEngine.COLS * RulesEngine.ROWS; // 63

                // BOARD CLEAR: 5 or fewer tiles left AND we just blew up 12+  (never during the bonus blast — it pays coins, not score)
                if (tilesRemaining <= 5 && cellsCleared >= 12 && !HandManager.BonusBlastRunning)
                {
                    int clearBonus = 50;
                    if (ScoreManager.Instance != null)
                        ScoreManager.Instance.AddScore(clearBonus, MatchController.PLAYER_HUMAN);
                    if (BonusPopup.Instance != null)
                        BonusPopup.Instance.Show($"CLEAR! +{clearBonus}",
                            new Color(1f, 0.85f, 0.2f, 1f), Vector3.zero, 1.3f);
                    GameAudio.Instance?.PlayScorePowerup();
                    HapticsManager.Strong();
                }
                // BIG CLEAR: 10 or fewer tiles left AND we just blew up 8+
                else if (tilesRemaining <= 10 && cellsCleared >= 8)
                {
                    int clearBonus = 25;
                    if (ScoreManager.Instance != null)
                        ScoreManager.Instance.AddScore(clearBonus, MatchController.PLAYER_HUMAN);
                    if (BonusPopup.Instance != null)
                        BonusPopup.Instance.Show($"BIG CLEAR! +{clearBonus}",
                            new Color(0.4f, 1f, 0.9f, 1f), Vector3.zero, 1.2f);
                    GameAudio.Instance?.PlayScorePowerup();
                }

                // Renewal Row — truly sparse board
                if (tilesRemaining <= 5 && !_isRisingRow)
                {
//                     WDLog.Log($"[SurvivalManager] Renewal row triggered — only {tilesRemaining} tiles remaining");
                    // Force-arm: next Update() tick (once resolution clears) fires a rise.
                    // Phase 11b: set both compat counter and the wall-clock
                    // timer so whichever gate Update() is reading picks it up.
                    _movesSinceLastRise = CurrentMovesPerRise;
                    _riseTimerSeconds   = CurrentSecondsPerRise;
                }
            }
        }

        /// <summary>Call after each player drop to decrement the boost counter.</summary>
        public void ConsumeBoostDrop()
        {
            if (_postClearBoostDrops > 0)
            {
                _postClearBoostDrops--;
//                 WDLog.Log($"[SurvivalManager] PostClearBoost consumed — {_postClearBoostDrops} remaining");
            }
        }

        public float ElapsedTime    => _elapsedTime;
        public int   TotalAutoDrops => _totalAutoDrops;
        public int   LongestChain   => _longestChain;
        public string LongestWord   => _longestWord ?? "";
        public bool  IsGameOver     => _isGameOver;

        /// <summary>
        /// Phase 11+ triangularity: track the longest word formed this session.
        /// Called from GameVisualBridge when a word is scored. Only tracks words
        /// length 3+ (below MIN_WORD_LENGTH shouldn't score anyway).
        /// </summary>
        public void UpdateLongestWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            if (word.Length > (_longestWord?.Length ?? 0))
                _longestWord = word.ToUpperInvariant();
        }

        /// <summary>
        /// Phase 11+ long-word reward: pause the rising-row timer by N seconds.
        /// Implemented by decrementing _riseTimerSeconds (which counts UP to
        /// CurrentSecondsPerRise before a rise fires) — subtracting effectively
        /// extends the countdown. Clamped to zero so a series of long-word
        /// freezes can't bank time beyond "fresh cadence."
        /// </summary>
        public void ApplyRiseFreeze(float seconds)
        {
            if (seconds <= 0f) return;
            _riseTimerSeconds = Mathf.Max(0f, _riseTimerSeconds - seconds);
        }

        // ── Computed intervals ────────────────────────────────────────────────────
        public float CurrentAutoDropInterval =>
            Mathf.Max(AUTO_DROP_INTERVAL_FLOOR,
                      AUTO_DROP_INTERVAL_START - (_elapsedTime / AUTO_DROP_RAMP_PERIOD) * AUTO_DROP_RAMP_RATE);

        /// <summary>
        /// Moves between rising rows at the current stage. Plateaus at
        /// MOVES_PER_RISE_FLOOR so the cadence is never faster than playable.
        /// Mercy slowdown adds +1/+2 when the board is dangerously full so a bad
        /// streak doesn't instantly spiral — "the board is dangerous, but there
        /// is usually at least one intelligent path to relief."
        /// </summary>
        /// <summary>Moves left before the next row rises — the ONE piece of pressure in the
        /// game, previously invisible. NOTE: this reads _turnsSinceLastTurnRise /
        /// GetTurnsPerRiseForStage, which is what ACTUALLY fires a rise. The obvious-looking
        /// CurrentMovesPerRise / _movesSinceLastRise pair is vestigial and still increments,
        /// so displaying that would show a number that does not match the board.
        /// 2026-09-09 Spencer.</summary>
        private System.Collections.IEnumerator SnapAfterRefill()
        {
            // Wait out the rise tween (plus the OutBack settle) before snapping, or we would
            // fight the animation instead of tidying up after it.
            yield return new WaitForSeconds(0.45f);
            GridManager.Instance?.SnapAllTilesToCells();
        }

        public int MovesUntilRise
        {
            get
            {
                if (ActiveRisesOff || !RisePerMoveDebug || BlastMode) return -1;   // -1 = no rise pressure
                int per = TurnsPerRiseNow;            // same source as the rise itself
                return Mathf.Max(0, per - _turnsSinceLastTurnRise);
            }
        }

        public int CurrentMovesPerRise
        {
            get
            {
                int stage = GetCurrentStage();
                int basePerRise;
                switch (stage)
                {
                    case 1:  basePerRise = 3; break;   // 6 rises per 18-move stage
                    case 2:  basePerRise = 2; break;   // 9 rises per stage — real pressure
                    case 3:  basePerRise = 2; break;   // 8 rises per 16-move stage
                    case 4:  basePerRise = 2; break;
                    case 5:  basePerRise = 2; break;   // 7 rises per 14-move stage
                    case 6:  basePerRise = 2; break;
                    default: basePerRise = 1; break;   // S7+: every move, full frantic
                }

                // Mercy slowdown: buy the active player breathing room when the
                // board is nearly full. Idle penalty is gone — in a move-based
                // system, not dropping means no rise, which is self-correcting.
                // Skipped under NoAssistMode — player eats the raw cadence.
                if (!NoAssistMode && RulesEngine.Instance != null)
                {
                    float occupancy = RulesEngine.Instance.GetBoardOccupancy();
                    if (occupancy >= 0.80f)       basePerRise += 2;
                    else if (occupancy >= 0.70f)  basePerRise += 1;
                }

                // Floor applies AFTER mercy so the mercy bonus can't push stage 6
                // past floor — but since floor is the minimum and mercy only adds,
                // the clamp is redundant. Keep it for safety.
                return Mathf.Max(basePerRise, MOVES_PER_RISE_FLOOR);
            }
        }

        public float AutoDropCountdown => Mathf.Max(0f, CurrentAutoDropInterval - _autoDropTimer);
        /// <summary>Moves remaining until the next rising row fires.</summary>
        public int RisingRowMovesRemaining => Mathf.Max(0, CurrentMovesPerRise - _movesSinceLastRise);
        public int MovesPlayed => _movesPlayed;

        /// <summary>
        /// Phase 11b: stage-aware seconds between rising rows. Gentler ramp
        /// than the original 15→6 over 6 stages — 20s S1 intro tapers across
        /// 10 transitions to the 6s floor at S11+, with -2s per stage early
        /// and -1s in the late game:
        ///   S1: 20s (relaxed intro)
        ///   S2: 18s
        ///   S3: 16s
        ///   S4: 14s
        ///   S5: 12s
        ///   S6: 11s
        ///   S7: 10s
        ///   S8:  9s
        ///   S9:  8s
        ///   S10: 7s
        ///   S11+: 6s (SECONDS_PER_RISE_FLOOR)
        /// Mercy slowdown adds +2s / +1s at dangerous board occupancy,
        /// mirroring the old move-based mercy bonus.
        /// </summary>
        public float CurrentSecondsPerRise
        {
            get
            {
                int stage = GetCurrentStage();
                float baseSeconds;
                switch (stage)
                {
                    case 1:  baseSeconds = 24f; break;
                    case 2:  baseSeconds = 22f; break;
                    case 3:  baseSeconds = 19f; break;
                    case 4:  baseSeconds = 17f; break;
                    case 5:  baseSeconds = 14f; break;
                    case 6:  baseSeconds = 13f; break;
                    case 7:  baseSeconds = 12f; break;
                    case 8:  baseSeconds = 11f; break;
                    case 9:  baseSeconds = 10f; break;
                    case 10: baseSeconds =  9f; break;
                    default: baseSeconds =  7f; break;   // S11+ frantic floor
                }

                if (!NoAssistMode && RulesEngine.Instance != null)
                {
                    float occupancy = RulesEngine.Instance.GetBoardOccupancy();
                    if (occupancy >= 0.80f)       baseSeconds += 2f;
                    else if (occupancy >= 0.70f)  baseSeconds += 1f;
                }

                // MVP P5: BOSS stages crank up the rise cadence by 30%. Stages 5,
                // 10, 15, 20... are boss stages — faster rises + visual treatment
                // signals the elevated stakes alongside the booster-choice reward.
                if (IsBossStage(stage))
                    baseSeconds *= 0.70f;

                return Mathf.Max(baseSeconds, SECONDS_PER_RISE_FLOOR);
            }
        }

        /// <summary>MVP P5: stage N is a boss stage if N > 0 AND N is a multiple of 5
        /// (5, 10, 15, 20...). Stage 1 is NOT a boss (intro pick gets its own modal
        /// without the boss difficulty boost).</summary>
        public static bool IsBossStage(int stageIndex)
        {
            return stageIndex >= 5 && stageIndex % 5 == 0;
        }

        /// <summary>MVP P5: a choice modal fires after stages 1, 5, 10, 15, 20...
        /// Stage 1 = intro pick. Stage 5+ = boss-pick combo.</summary>
        public static bool IsChoiceStage(int stageIndex)
        {
            return stageIndex == 1 || IsBossStage(stageIndex);
        }

        /// <summary>Seconds remaining until the next rising row fires. HUD reads this.</summary>
        public float RisingRowSecondsRemaining => Mathf.Max(0f, CurrentSecondsPerRise - _riseTimerSeconds);

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // Force-load NoAssistMode from PlayerPrefs on first access
            _ = NoAssistMode;
//             WDLog.Log("[SurvivalManager] Awake");
        }

        private void Update()
        {
            // Global debug keyboard shortcut — works in menu OR gameplay
            if (Input.GetKeyDown(KeyCode.N))
                ToggleNoAssistMode();

            // 2026-06-03 Spencer: detect the resolution-end edge. When a move that
            // cleared a stage finishes resolving, snap the next stage's baseline to
            // the final total (discarding all post-clear cascade overflow) and release
            // the absorb flag so the NEXT move starts the new stage cleanly at 0.
            bool processingNow = MatchController.Instance != null && MatchController.Instance.IsProcessing;
            if (_wasProcessing && !processingNow && _clearedThisResolution)
            {
                if (ScoreManager.Instance != null) _stageStartScore = ScoreManager.Instance.PlayerScore;
                _clearedThisResolution = false;
            }
            _wasProcessing = processingNow;

            TickDuckCollect();
            UpdateSurvival();
        }

        // ── DUCK COLLECT (2026-10-22). A duck on the bottom row is collected once the board is IDLE — no resolution, no
        // falling tiles — then the column falls and refills through the booster cascade path (BoosterManager), which already
        // runs gravity + refill + any chain the fall makes. Collecting mid-resolution would punch a hole in the data under a
        // live step, which is exactly the desync class the 2026-10-20 audit warned about. ──
        public static float DUCK_COLLECT_PAUSE = 0.18f;   // let the landing read before it lifts off
        private bool _duckCollecting;
        private bool DuckCollectPending => _duckCollecting
            || (FullBoardActive && LevelRules.HasDuckGoal && RulesEngine.Instance != null && RulesEngine.Instance.DucksOnBottomRow() != null);
        // LAUNCH AT ONCE (2026-10-22 Spencer: "when the ducks hit the bottom their fly up ... should probably be the first animation
        // that is triggered. right now it is waiting for all the cascading and explosions to finish"). The moment a duck LANDS on
        // the bottom row it quacks, counts, flies to the HUD and its tile hides — mid-cascade is fine, it's all visual. Removing it
        // from the board DATA (and refilling the gap) still waits for the board to settle, as before: clearing a cell under a live
        // resolution step is the desync class we keep out.
        private readonly HashSet<Tile> _launchedDucks = new HashSet<Tile>();
        private void LaunchLandedDucks(RulesEngine rules, GridManager grid)
        {
            var ducks = rules.DucksOnBottomRow();
            if (ducks == null) return;
            int q = 0;
            foreach (var p in ducks)
            {
                var t = grid.GetTile(p.x, p.y);
                if (t == null || _launchedDucks.Contains(t) || t.IsAnimating) continue;
                _launchedDucks.Add(t);
                Vector3 w = grid.CellToWorld(p.x, p.y) + new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
                LevelRules.Collected++; LevelRules.CollectAwaitingFly++; LevelRules.CollectAwaitingSince = Time.unscaledTime;
                HUDManager.Instance?.FlyCollectLetter(w, '\0');
                GameParticles.Instance?.PlayShimmerBurst(w, 12);
                GameAudio.Instance?.PlayDuckQuack(q++);
                HapticsManager.Strong();
                var sr = t.GetComponent<SpriteRenderer>(); if (sr != null) sr.enabled = false;   // the flight IS the duck now
                WDLog.Log($"[Duck] launched at ({p.x},0) -> {LevelRules.Collected} (board clears it when settled)");
            }
        }

        private void TickDuckCollect()
        {
            if (_duckCollecting || !FullBoardActive || !LevelRules.HasDuckGoal || !LevelRules.GoalsActiveFor(_currentStageIndex)) return;
            if (_isOverlayPaused || _currentStageCleared) return;
            var rules = RulesEngine.Instance; var grid = GridManager.Instance; var hm = HandManager.Instance;
            if (rules == null || grid == null || hm == null || BoosterManager.Instance == null) return;
            LaunchLandedDucks(rules, grid);   // visual + count: immediately, even mid-cascade
            // Out of moves leaves input closed — a duck that lands on the LAST move must still be collected.
            if ((!hm.IsInteractable && CurrentStageMovesRemaining > 0) || rules.IsResolving || hm.IsResolvingChain || (MatchController.Instance != null && MatchController.Instance.IsProcessing)) return;
            var ducks = rules.DucksOnBottomRow();
            if (ducks == null) return;
            foreach (var p in ducks) { var t = grid.GetTile(p.x, p.y); if (t != null && t.IsAnimating) return; }
            StartCoroutine(DuckCollectRoutine(ducks));
        }
        private System.Collections.IEnumerator DuckCollectRoutine(List<Vector2Int> ducks)
        {
            _duckCollecting = true;
            var hm = HandManager.Instance; var grid = GridManager.Instance; var rules = RulesEngine.Instance;
            bool wasInteractable = hm.IsInteractable;
            hm.IsInteractable = false;
            MatchController.Instance?.BeginProcessing();
            try
            {
                yield return new WaitForSeconds(DUCK_COLLECT_PAUSE);
                var before = BoosterManager.Instance.SnapshotOccupiedPublic();
                int quackIdx = 0;
                foreach (var p in ducks)
                {
                    if (rules.GetCell(p.x, p.y) == null || !rules.GetCell(p.x, p.y).IsDropTarget) continue;
                    var dt = grid.GetTile(p.x, p.y);
                    if (dt == null || !_launchedDucks.Remove(dt))
                    {   // not launched yet (shouldn't happen — LaunchLandedDucks runs first) — count + fly it now
                        Vector3 w = grid.CellToWorld(p.x, p.y);
                        LevelRules.Collected++; LevelRules.CollectAwaitingFly++; LevelRules.CollectAwaitingSince = Time.unscaledTime;
                        HUDManager.Instance?.FlyCollectLetter(w, '\0');
                        GameParticles.Instance?.PlayShimmerBurst(w, 12);
                        GameAudio.Instance?.PlayDuckQuack(quackIdx++);
                    }
                    rules.ClearCell(p.x, p.y);
                    WDLog.Log($"[Duck] cleared from the board at ({p.x},0)");
                }
                grid.RemoveTiles(ducks);   // (the buzz already fired at launch)
                yield return BoosterManager.Instance.StartCoroutine(BoosterManager.Instance.RunGravityAndCascadePublic(before));
            }
            finally
            {
                MatchController.Instance?.EndProcessing();
                if (hm != null) hm.IsInteractable = wasInteractable;
                _duckCollecting = false;
            }
            CheckStageClear();
        }

        /// <summary>
        /// Phase 11b: freeze every time accumulator (elapsed, auto-drop,
        /// rising-row clock) while the app is backgrounded. UpdateSurvival()
        /// short-circuits on _appPaused so the rise timer doesn't keep
        /// counting while the player is away.
        /// </summary>
        private void OnApplicationPause(bool pause)
        {
            _appPaused = pause;
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // 2026-06-24: the no-assist debug banner is a DEV-ONLY overlay — gated so it strips from
        // release/playtest builds (testers shouldn't see "N = no-assist playtest"). 2026-06-24 Spencer.
        private static GUIStyle _noAssistStyle;
        private static GUIStyle _noAssistHintStyle;

        private void OnGUI()
        {
            // Level mode owns its own HUD surface; the SurvivalManager debug
            // indicator (top-left red banner / hint line) belongs only to
            // non-Level modes. Keeps Level-mode playtests visually clean,
            // especially during fresh-eyes tutorial playtests.
            if (GameManager.IsLevelMode) return;

            if (_noAssistStyle == null)
            {
                _noAssistStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 18,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = new Color(1f, 0.3f, 0.3f, 0.95f) }
                };
                _noAssistHintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 11,
                    normal = { textColor = new Color(0.7f, 0.7f, 0.7f, 0.5f) }
                };
            }

            if (NoAssistMode)
            {
                GUI.Label(new Rect(10, 10, 400, 30), "● NO ASSIST MODE (press N)", _noAssistStyle);
            }
            else
            {
                GUI.Label(new Rect(10, 10, 400, 20), "N = no-assist playtest", _noAssistHintStyle);
            }
        }
#endif

        private void UpdateSurvival()
        {
            if (!_isSurvivalMode || _isGameOver) return;
            if (GameManager.Instance == null || GameManager.Instance.CurrentState != GameState.Playing) return;

            // Tile repair runs ALWAYS — even during processing, because that's
            // when blank tiles are most likely to appear (mid-chain visual desync)
            _tileRepairTimer += Time.deltaTime;
            if (_tileRepairTimer >= 2f) // less frequent on mobile for performance
            {
                _tileRepairTimer = 0f;
                if (GridManager.Instance != null)
                {
                    for (int col = 0; col < RulesEngine.COLS; col++)
                        for (int row = 0; row < RulesEngine.ROWS; row++)
                        {
                            Tile tile = GridManager.Instance.GetTile(col, row);
                            if (tile != null) tile.RepairLetterVisibility();
                        }
                }
            }

            // Pause timers during resolution (Approach A: input blocked during resolution)
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;

            // Pause timers during rewrite mode (auto-cancels after 5s so can't abuse)
            if (HandManager.Instance != null && HandManager.Instance.IsRewriteModeActive) return;

            // Pause during active auto-drop or rising row coroutine
            if (_isAutoDropping || _isRisingRow) return;

            // Phase 11b: when the app is backgrounded (OnApplicationPause=true),
            // freeze every time accumulator so the rise timer doesn't keep
            // counting while the player is away. Unity clamps Time.deltaTime
            // via Time.maximumDeltaTime on resume, but an explicit freeze is
            // safer + more predictable.
            // Overlay pause (stage-clear modal, etc.) uses the same gate — any
            // full-screen UI that should freeze gameplay timers sets this.
            if (_appPaused || _isOverlayPaused) return;

            // Vault (loot) levels: out-of-moves does NOT end the run — it's a no-fail loot/triage
            // beat. The level just ENDS → bank → advance, handled by CheckStageClear (called from
            // NotifyDropCommitted). No continue, no top-out. See CheckStageClear's vault gate.

            _elapsedTime += Time.deltaTime;

            // Bonus Mode pause (2026-05-15): freeze the rising-row timer while
            // the player is in the 5-move bonus round. Drops in bonus mode are
            // "free" (no move-counter tick, no stage-budget consumption) and
            // the rising row pressure should pause too — otherwise the bonus
            // round feels rushed when it's supposed to be a flow-state breather.
            // _elapsedTime keeps counting (session timer + auto-drop grace).
            bool inBonusMode = BonusMode.Instance != null && BonusMode.Instance.IsActive;
            // Debug toggle: per-move rises suppresses the time-based clock entirely.
            // (Glade Stillness rise-skips are consumed in the rise-firing path,
            // not by stalling the clock — same code works for both rise modes.)
            if (!inBonusMode && !RisePerMoveDebug)
                _riseTimerSeconds += Time.deltaTime;

            // Post-clear boost time falloff
            if (_postClearBoostDrops > 0)
            {
                _postClearBoostTime -= Time.deltaTime;
                if (_postClearBoostTime <= 0f)
                {
                    _postClearBoostDrops = 0;
//                     WDLog.Log("[SurvivalManager] PostClearBoost expired (time falloff)");
                }
            }

            // Time-based prime expiry REMOVED (April 17) — moved to move-based only.
            // Primed words now expire purely on ExpiresOnTurn (drops remaining, word
            // length-scaled). Rising rows provide all the "don't stall" time pressure
            // Survival needs; a second time clock on primes fought against strategic
            // chain-building. ExpireByTime() is still defined on PrimedWordRegistry
            // but no longer called — safe to remove later if desired.

            // Auto-drop sub-timer (still time-based)
            if (_elapsedTime >= AUTO_DROP_GRACE)
                _autoDropTimer += Time.deltaTime;

            if (_elapsedTime >= AUTO_DROP_GRACE && _autoDropTimer >= CurrentAutoDropInterval)
            {
                _autoDropTimer = 0f;
                // Auto-drops disabled — rising rows are the only pressure mechanic
                // (ExecuteAutoDrop removed 2026-10-20 as dead code — restore from tag pre-cleanup-2026-10-20)
            }

            // Won level: nothing below should queue or fire rises on the board being left behind. 2026-10-20.
            if (AwaitingNextLevel) return;

            if (FullBoardActive) UpdateFullBoardRefill();   // full-board mode: refill from the top; rises are off (ActiveRisesOff)

            // Stage-start replenish: if AdvanceToNextStage found a sparse board,
            // fire extra rising rows to bring it up to the minimum. Consume
            // one pending rise per Update frame so animations complete between
            // them (feels like a dramatic "board fills up, new stage ready").
            // ── STARVED BOARD (2026-09-09 Spencer) ──────────────────────────────────
            // The occupancy-responsive cadence only changes how often a MOVE triggers a rise.
            // On a nearly-empty board there is often nothing worth doing, so no moves happen,
            // so nothing rises — the refill was gated behind the very problem it fixes.
            // This tops up the pending-rise queue, which Update drains WITHOUT needing a move.
            // Deliberately refills to a modest 32%: enough to have something to play with,
            // not so much that clearing the board stops being a good outcome.
            if (!ActiveRisesOff && _stageStartRisesPending == 0 && !_isRisingRow && !_isGameOver
                && RulesEngine.Instance != null)
            {
                float occ = RulesEngine.Instance.GetBoardOccupancy();
                if (occ < STARVED_OCC)
                {
                    int rows = Mathf.CeilToInt((STARVED_TARGET_OCC - occ) * RulesEngine.MAX_ROWS);
                    _stageStartRisesPending = Mathf.Clamp(rows, 1, 4);
                    WDLog.Log($"[Starved] occupancy {occ:P0} -> queueing {_stageStartRisesPending} refill row(s)");
                }
            }

            if (_stageStartRisesPending > 0 && !ActiveRisesOff)
            {
                if (!IsBoardAnimating())
                {
                    _stageStartRisesPending--;
                    NotifyRiseFired();
                    _nextRiseIsRefill = true;   // board-empty / level-start top-up -> rain, not a row (2026-10-21)
                    StartCoroutine(ExecuteRisingRow());
                    // Last refill row — clean up any sub-pixel residue the stacked
                    // overshooting rises left behind, or it shows as dark seams.
                    if (_stageStartRisesPending == 0) StartCoroutine(SnapAfterRefill());
                }
                return; // don't process normal rising this frame — stage-start takes precedence
            }

            // Rising row: time-based (Phase 11b). Timer counts wall-clock
            // seconds (advanced above, frozen when _appPaused, in bonus mode,
            // or resolution is in flight). When it crosses CurrentSecondsPerRise
            // and the board isn't mid-animation, a rise fires + timer resets.
            // Move counter is kept vestigially for analytics compat.
            if (!inBonusMode && !ActiveRisesOff && _riseTimerSeconds >= CurrentSecondsPerRise)
            {
                if (!IsBoardAnimating())
                {
                    _riseTimerSeconds = 0f;
                    _movesSinceLastRise = 0;   // keep compat field consistent
                    // Glade Stillness: consume a rise-skip charge if active; suppress this rise.
                    if (ConsumeGladeRiseIfActive()) { /* rise suppressed */ }
                    else
                    {
                        NotifyRiseFired();
                        StartCoroutine(ExecuteRisingRow());
                    }
                }
                // else: timer stays past threshold, retries next frame
            }

            // Stage-up detection — fires on AdvanceToNextStage via the chip-target
            // system. _lastStage is tracked here so any future per-frame logic
            // can react to the transition (currently no-op — StageClearModal owns
            // celebration via OnStageCleared subscription). The old "STAGE N"
            // popup + PlayStageUp audio were removed when the modal landed —
            // they showed the next stage's number while the modal still showed
            // the cleared stage, causing UX confusion.
            if (_currentStageIndex > _lastStage)
            {
                _lastStage = _currentStageIndex;
            }

            // Update HUD
            if (HUDManager.Instance != null)
                HUDManager.Instance.UpdateSurvivalHUD(this);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        public void StartSurvival()
        {
            LevelRules.ResetForNewRun();   // a new run must not inherit the last run's stage memory (2026-10-20)
            _elapsedTime            = 0f;
            _autoDropTimer          = 0f;
            _movesSinceLastRise     = 0;
            _turnsSinceLastTurnRise = 0;    // onboarding ramp — fresh turn counter per run
            _riseTimerSeconds       = 0f;   // Phase 11b — fresh rise clock per run
            _appPaused              = false;
            _movesPlayed            = 0;
            _totalAutoDrops         = 0;
            _longestChain           = 0;
            _longestWord            = "";
            _isGameOver             = false;
            _isAutoDropping         = false;
            _isRisingRow            = false;
            _pendingEditDroughtRebate = false;
            _isOverlayPaused        = false;
            _lastStage              = 1;
            _postClearBoostDrops    = 0;
            _postClearBoostTime     = 0f;

            // Reset stage chip-target state. Fresh runs start AT the first real Area once the tutorial's been done.
            _currentStageIndex      = RunStartStage;
            // Clear any objective left over from the previous run / a debug stage-jump, so the level-1
            // objective re-installs fresh. The auto-installer (ObjectiveManager.Update) only fires when
            // Active == null, so without this the stale objective sticks while the level number shows 1.
            // 2026-06-18 Spencer.
            ObjectiveManager.Instance?.ClearObjective();
            _stageStartScore        = 0;
            _currentStageMovesUsed  = 0;
            _currentStageCleared    = false;
            _lastStageReached       = 1;
            // Drop the HUD's latched "score/target" number, or the previous run's target
            // survives into this one. 2026-10-19.
            HUDManager.Instance?.ResetScoreTargetLatch();
            _lastStageShortfall     = 0;
            _lastStageTarget        = 0;
            _risesInCurrentStage    = 0;
            _stagesCleared          = 0;
            _biggestWordScore       = 0;
            _stageStartRisesPending = 0;

            // Validation MVP (2026-06-15): clear any leftover level-sequencer dial overrides from a
            // prior run so level 1 starts from defaults until ObjectiveManager.InstallLevel(1) applies
            // L1's dials. Belt-and-suspenders — the install fires immediately, but no stale Boss
            // cadence/assist can leak into the first frame.
            _riseCadenceOverride    = -1;   // static — restore default onboarding ramp
            _vaultMoveBudgetOverride = -1;  // restore Inspector default vault budget

            // MVP P3 Path B: continue ladder resets on each new run.
            _continuesInRun         = 0;
            _continueOffered        = false;

            // MVP P5: Glade Stillness rise-skip counter resets each run.
            _glaedStillnessRisesRemaining = 0;

            // MVP P3.5: seed gameplay RNG for Daily Seeded Survival; passthrough otherwise.
            if (DailyDropManager.IsDailyMode)
                SurvivalRng.SetSeed(DailyDropManager.GetDailySeed());
            else
                SurvivalRng.Reset();

            // MVP P4: reset booster state. ActiveBooster gets granted by choice
            // modal pick (P5); for now P4 dev-test path auto-grants via debug menu.
            BoosterManager.Instance?.StartRun();
            RunBuffs.ResetRun();          // buffs are RUN-scoped — never carry between runs
            // MVP P5: reset switch-rescue counter for the new run.
            BoosterChoiceModal.Instance?.ResetForNewRun();

            ChainMeter.Instance?.ResetForNewRun();
            BonusMode.Instance?.ResetForNewRun();

            // Phase 11+ — Survival BGM. No-op if the music clip isn't loaded
            // (GameAudio warns once). Fades any prior track out first.
            GameAudio.Instance?.PlaySurvivalMusic();
//             WDLog.Log("[SurvivalManager] Survival started!");
        }

        public void StopSurvival()
        {
            _isGameOver = true;

            BonusMode.Instance?.ResetForNewRun();
            ChainMeter.Instance?.ResetForNewRun();

            // Music intentionally NOT stopped on top-out (per Spencer 2026-05-21).
            // Gameplay BGM keeps playing through the TopOutPanel + GameOverUI
            // until the player taps Play Again, which restarts the run via
            // StartSurvival (PlaySurvivalMusic is idempotent on the same pool).

            // Clear stage-event delegates so a recreated MatchController doesn't
            // inherit stale subscriptions from a destroyed one.
            OnStageCleared = null;
            OnStageFailed  = null;

            // MVP P3.5: return RNG to passthrough so non-daily runs aren't bound by leftover seed.
            SurvivalRng.Reset();

            // MVP P4: wipe booster state at run end.
            BoosterManager.Instance?.EndRun();
//             WDLog.Log($"[SurvivalManager] Survival ended — elapsed={_elapsedTime:F1}s drops={_totalAutoDrops}");
        }

        /// <summary>Track longest chain for game-over stats.</summary>
        public void RecordChainDepth(int depth)
        {
            if (depth > _longestChain)
                _longestChain = depth;
        }

        /// <summary>Formatting for HUD display.</summary>
        public string GetFormattedElapsedTime()
        {
            int minutes = Mathf.FloorToInt(_elapsedTime / 60f);
            int seconds = Mathf.FloorToInt(_elapsedTime % 60f);
            return $"{minutes}:{seconds:D2}";
        }

        /// <summary>Stone tile chance per rising row cell, controlled by stage.</summary>
        public float GetStoneChance()
        {
            int stage = GetCurrentStage();
            if (stage <= 1) return 0f;       // no stones in stage 1
            if (stage == 2) return 0.08f;     // ~0-1 stones per row
            if (stage == 3) return 0.15f;     // ~1 stone per row
            if (stage == 4) return 0.22f;     // ~1-2 stones
            if (stage == 5) return 0.32f;     // ~2-3 stones per row
            return 0.40f;                     // stage 6+: ~3 stones — nearly half the row is junk
        }

        /// <summary>
        /// Current stage — now driven by the chip-target system. Advances only
        /// when the player hits a stage's chip target (via NotifyScoreDelta →
        /// AdvanceToNextStage). Previously was move-threshold-based; the stage
        /// number is now an actual earned progression, not a time proxy.
        /// Callers that branch on stage difficulty (stone chance, rising row
        /// cadence, mercy slowdown) get the earned stage here.
        /// </summary>
        public int GetCurrentStage() => _currentStageIndex;

        public static void Reset()
        {
            _isSurvivalMode = false;
            if (Instance != null)
            {
                Instance._elapsedTime           = 0f;
                Instance._autoDropTimer         = 0f;
                Instance._movesSinceLastRise    = 0;
                Instance._riseTimerSeconds      = 0f;   // Phase 11b
                Instance._appPaused             = false; // Phase 11b
                Instance._movesPlayed           = 0;
                Instance._totalAutoDrops        = 0;
                Instance._longestChain          = 0;
                Instance._longestWord           = "";
                Instance._isGameOver            = false;
                Instance._isAutoDropping        = false;
                Instance._lastStage             = 1;
                Instance._isRisingRow           = false;
                Instance._pendingEditDroughtRebate = false;
                Instance._isOverlayPaused       = false;

                // Reset stage chip-target state (fresh run starts at the first real Area once the tutorial's done)
                Instance._currentStageIndex     = RunStartStage;
                Instance._stageStartScore       = 0;
                Instance._currentStageMovesUsed = 0;
                Instance._currentStageCleared   = false;
                Instance._lastStageReached      = 1;
                Instance._lastStageShortfall    = 0;
                Instance._lastStageTarget       = 0;
                Instance._risesInCurrentStage   = 0;
                Instance._stagesCleared         = 0;
                Instance._biggestWordScore      = 0;
                Instance._stageStartRisesPending = 0;

                // Clear event delegates so subscriptions from a destroyed
                // MatchController don't leak into a new match.
                Instance.OnStageCleared = null;
                Instance.OnStageFailed  = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RISING ROW
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>Per-move debug rise: waits for the drop resolution chain
        /// (word scoring, detonations, gravity) to clear before firing the rise.
        /// Prevents the IsProcessing guard from cancelling our intended rise.</summary>
        private IEnumerator WaitAndExecuteRise()
        {
            // Cap wait — if resolution somehow hangs, don't block forever.
            const float MAX_WAIT_SECONDS = 8f;
            float waited = 0f;
            while (MatchController.Instance != null
                   && MatchController.Instance.IsProcessing
                   && waited < MAX_WAIT_SECONDS)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            // Re-check guards (player might've topped out or modal opened during the wait).
            if (_isRisingRow || _isAutoDropping || _isOverlayPaused || _isGameOver) { _riseScheduledPending = false; yield break; }
            WDLog.Log($"[RisePerMove] firing rise after {waited:F2}s wait");
            yield return StartCoroutine(ExecuteRisingRow());
        }

        // ── LETTER RAIN (2026-10-21 Spencer: "instead of a row rise ... a rain of letters ... could help us potentially seed
        // possibilities better"). Replaces the rising row when RAIN_MODE is on: RAIN_COUNT letters fall onto the tops of
        // columns (biased to the LOW ones) with the old drop animation. If a run of 4+ columns share a landing height, a
        // planted near-word falls across them as a row. Every column full = the board-full penalty. Off = rising rows.
        // 2026-10-21 Spencer: "i like row rising better. we could sprinkle in the rain" -> rows = the PRESSURE, rain = a GIFT.
        // RAIN_MODE (every rise rains) is OFF; RAIN_FOR_REFILL OFF 2026-10-21 (Spencer: rows are the normal refill). Rain is ONLY a
        // SHOWER reward after a big play (5+ letter word or a special firing), at most one per move.
        public static bool RAIN_MODE   = false;
        public static bool RAIN_FOR_REFILL = false;
        public static bool RAIN_SHOWERS    = true;
        public static int  SHOWER_COUNT    = 4;
        private bool _nextRiseIsRefill;
        private bool _showerRunning;

        /// <summary>Queue a reward shower (called when a big play's whole chain has finished).</summary>
        public void QueueShower()
        {
            if (!RAIN_SHOWERS || _showerRunning || AwaitingNextLevel || _isGameOver || HandManager.BonusBlastRunning) return;
            // Full board: there is no room to rain into, so the reward rides on the NEXT refill as gold letters.
            if (FullBoardActive) { RulesEngine.RefillGoldPending = SHOWER_GOLD; WDLog.Log("[FullBoard] big play -> next refill brings gold"); return; }
            StartCoroutine(ShowerRoutine());
        }
        private IEnumerator ShowerRoutine()
        {
            _showerRunning = true;
            float w = 0f;
            while (w < 4f && ((MatchController.Instance != null && MatchController.Instance.IsProcessing) || _isRisingRow || IsBoardAnimating()))
            { w += Time.unscaledDeltaTime; yield return null; }
            if (!AwaitingNextLevel && !_isGameOver)
            {
                _isRisingRow = true;   // input waits for the letters to land, like a rise
                WDLog.Log("[Rain] SHOWER — reward for a big play");
                StageStats.Showers++;
                BonusPopup.Instance?.Show("LETTER SHOWER!", new Color(0.55f, 0.85f, 1f, 1f), Vector3.zero, 1.1f);
                yield return StartCoroutine(RainLetters(SHOWER_COUNT, shower: true));
                _isRisingRow = false;
            }
            _showerRunning = false;
        }
        public static int  RAIN_COUNT  = 6;
        public static float RAIN_STAGGER = 0.06f;
        public static int  RAIN_SMART   = 2;   // smart (one-swap-from-a-word) letters per rain

        // ── FULL BOARD MODE (2026-10-21 Spencer: "just like candy crush and royal match where letters are always fully
        // stacked, then as you make words it cascades from above to fill the open space"). Every level starts brim-full;
        // after a chain settles, every column is topped up from above with letters that spell nothing on their own (plus
        // a couple of "one swap from a word" smart letters). Rising rows, topping out and the empty-board refill are OFF
        // (ActiveRisesOff). Flip FULL_BOARD_MODE off to get the rising-row game back exactly as it was.
        public static bool FULL_BOARD_MODE = true;   // default the first time; after that the FX Test Menu toggle (PlayerPrefs) wins
        private static int s_fullBoardPref = -1;
        public static bool FullBoardEnabled
        {
            get { if (s_fullBoardPref < 0) s_fullBoardPref = PlayerPrefs.GetInt("WD_FullBoard", FULL_BOARD_MODE ? 1 : 0); return s_fullBoardPref == 1; }
            set { s_fullBoardPref = value ? 1 : 0; PlayerPrefs.SetInt("WD_FullBoard", s_fullBoardPref); PlayerPrefs.Save(); }
        }
        public static bool FullBoardActive => FullBoardEnabled && ScoreRunMode && HandManager.InstantPopMode;

        /// <summary>FX Test Menu: flip full board <-> rising rows and restart the current level with the right board.</summary>
        public void ToggleFullBoardAndRestart()
        {
            FullBoardEnabled = !FullBoardEnabled;
            WDLog.Log($"[FullBoard] mode -> {(FullBoardEnabled ? "FULL BOARD" : "RISING ROWS")} (restarting stage {_currentStageIndex})");
            LevelRules.ForceFreshBoardOnNextApply();
            DebugJumpToStage(_currentStageIndex);
        }
        public static float FULL_REFILL_SETTLE  = 0.12f;  // board must be idle this long before topping up
        private float _fullIdleFor;
        private bool  _fullRefilling;

        /// <summary>Top of each column's stack (one above the highest tile) — holes UNDER ice stay holes, like match-3 blockers.</summary>
        private static int StackTop(RulesEngine rules, int c)
        {
            for (int r = RulesEngine.ROWS - 1; r >= 0; r--) { var d = rules.GetCell(c, r); if (d != null && d.Letter != '\0') return r + 1; }
            return 0;
        }

        private void UpdateFullBoardRefill()
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null || _fullRefilling || _isRisingRow || _isGameOver || _isOverlayPaused) { _fullIdleFor = 0f; return; }
            bool busy = (MatchController.Instance != null && MatchController.Instance.IsProcessing)
                        || (HandManager.Instance != null && HandManager.Instance.IsResolvingChain)
                        || HandManager.BonusBlastRunning || IsBoardAnimating();
            bool gap = false;
            for (int c = 0; c < RulesEngine.COLS && !gap; c++) if (StackTop(rules, c) < RulesEngine.ROWS) gap = true;
            if (busy || !gap) { _fullIdleFor = 0f; return; }
            _fullIdleFor += Time.unscaledDeltaTime;
            if (_fullIdleFor < FULL_REFILL_SETTLE) return;
            _fullIdleFor = 0f;
            StartCoroutine(FullBoardRefill());
        }

        /// <summary>FALLBACK only — the refill normally happens inside gravity. This catches any gap left by a path that
        /// cleared tiles without a gravity step.</summary>
        private IEnumerator FullBoardRefill()
        {
            _fullRefilling = true; _isRisingRow = true;   // input waits for the letters to land, like a rise
            WDLog.Log("[FullBoard] fallback refill (a gap survived without a gravity step)");
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            var anim = new List<Tile>();
            if (rules.FillTopsInData() > 0) grid.SpawnRefillTiles(anim);
            float w = 0f;
            while (w < 2f && anim.Exists(t => t != null && t.IsAnimating)) { w += Time.unscaledDeltaTime; yield return null; }
            GameAudio.Instance?.PlayTileDrop();
            _isRisingRow = false; _fullRefilling = false;
        }

        public static int SHOWER_GOLD = 0;   // 2026-10-22 Spencer: "there shouldnt be any of these golden tiles anymore" — big plays no longer drop gold letters (was 2)   // shower only: this many of the smart letters land GOLD (+1 move when popped)

        private IEnumerator RainLetters(int count, bool shower = false)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null) yield break;
            int cols = RulesEngine.COLS, rows = RulesEngine.ROWS;
            var heights = new int[cols];
            for (int c = 0; c < cols; c++) heights[c] = rules.GetColumnHeight(c);

            bool anyRoom = false; for (int c = 0; c < cols; c++) if (heights[c] < rows) anyRoom = true;
            if (!anyRoom && MoveLimitMode)
            {
                ApplyOverflowPenalty();                                // top row breaks off, -3 moves
                for (int c = 0; c < cols; c++) heights[c] = rules.GetColumnHeight(c);
            }

            // Plan the drops: (col, letter) in order.
            var plan = new List<KeyValuePair<int, char>>();
            var planned = new int[cols];
            var goldIdx = new HashSet<int>();   // plan indices that land as shower gold
            TileBag bag = MatchController.Instance != null ? MatchController.Instance.Bag : new TileBag();

            // 1) Planted near-word across a run of equal landing heights (one swap from complete, as with rising rows).
            if (HandManager.InstantPopMode && SurvivalRng.Value < RulesEngine.PLANT_CHANCE)
            {
                int bestStart = -1, bestLen = 0;
                for (int c = 0; c < cols; )
                {
                    int e = c; while (e + 1 < cols && heights[e + 1] == heights[c]) e++;
                    int len = e - c + 1;
                    if (heights[c] < rows && len >= 4 && len > bestLen) { bestLen = len; bestStart = c; }
                    c = e + 1;
                }
                if (bestStart >= 0)
                {
                    var rowChars = new char[cols];
                    // '*' marks the free run (not a letter, not '#'/'\0', so the planter treats it as a usable slot); every slot the
                    // planter overwrites is part of the word.
                    for (int c = 0; c < cols; c++) rowChars[c] = (c >= bestStart && c < bestStart + bestLen) ? '*' : '\0';
                    rules.TryPlantNearWord(rowChars);
                    for (int c = bestStart; c < bestStart + bestLen; c++)
                        if (rowChars[c] != '*') { plan.Add(new KeyValuePair<int, char>(c, rowChars[c])); planned[c]++; }
                }
            }

            // 1b) SMART LANDING LETTERS: up to RAIN_SMART letters that make a common word ONE SWAP away with what is already
            //     there. Searched on a virtual board that includes everything planned so far.
            int smart = 0;
            if (HandManager.InstantPopMode)
            try
            {
                rules.SnapshotForRain(out var vg, out var vm);
                // apply planned drops to the virtual board
                var vh = (int[])heights.Clone();
                foreach (var kv in plan) { if (vh[kv.Key] < rows) { vg[kv.Key, vh[kv.Key]] = kv.Value; vm[kv.Key, vh[kv.Key]] = true; vh[kv.Key]++; } }
                var order = new List<int>(); for (int c = 0; c < cols; c++) order.Add(c);
                for (int i = order.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); int tmp = order[i]; order[i] = order[j]; order[j] = tmp; }
                foreach (int c in order)
                {
                    if (smart >= RAIN_SMART || plan.Count >= count) break;
                    if (vh[c] >= rows || planned[c] >= 2) continue;
                    if (rules.FindSmartRainLetter(vg, vm, c, vh[c], out char L, out string w))
                    {
                        if (shower && smart < SHOWER_GOLD) goldIdx.Add(plan.Count);
                        plan.Add(new KeyValuePair<int, char>(c, L)); planned[c]++; vh[c]++; smart++;
                        WDLog.Log($"[Rain] smart '{L}' -> col {c} (one swap from '{w}')");
                    }
                }
                // 1c) FALLBACK: nothing smart and no row plant — a scrambled 4-letter word stacked down the lowest column
                //     with room (one vertical swap fixes it). Read top->bottom, so the LAST letter lands first.
                if (smart == 0 && plan.Count == 0)
                {
                    int best = -1;
                    for (int c = 0; c < cols; c++) if (heights[c] + 4 <= rows && (best < 0 || heights[c] < heights[best])) best = c;
                    string scr = best >= 0 ? rules.PickScrambledPlant(4, out string pw) : null;
                    if (scr != null)
                    {
                        for (int k = scr.Length - 1; k >= 0; k--) { plan.Add(new KeyValuePair<int, char>(best, scr[k])); planned[best]++; }
                        WDLog.Log($"[Rain] column plant '{scr}' (top->bottom) in col {best}");
                    }
                }
            }
            catch (System.Exception ex)
            {
                // Seeding must never stop the rain itself (2026-10-21: an NRE here killed every rain, board emptied).
                Debug.LogError($"[Rain] seeding failed, raining plain letters instead: {ex.Message}");
            }

            // 2) Fill up to RAIN_COUNT with bag letters, weighted to the lowest columns, max 2 per column.
            int vowels = 0;
            int guard = 0;
            while (plan.Count < Mathf.Max(count, plan.Count) && guard++ < 60)
            {
                if (plan.Count >= count && plan.Count > 0) break;
                float total = 0f; var w = new float[cols];
                for (int c = 0; c < cols; c++)
                {
                    int h = heights[c] + planned[c];
                    if (h >= rows || planned[c] >= 2) continue;
                    w[c] = (rows - h) * (rows - h); total += w[c];
                }
                if (total <= 0f) break;
                float pick = SurvivalRng.Value * total; int col = 0;
                for (int c = 0; c < cols; c++) { pick -= w[c]; if (pick <= 0f && w[c] > 0f) { col = c; break; } col = c; }
                char ch = bag.DrawLetter();
                bool isV = "AEIOU".IndexOf(char.ToUpperInvariant(ch)) >= 0;
                if (!isV && vowels < 2 && plan.Count >= 3) { ch = "AEIOU"[SurvivalRng.Range(0, 5)]; isV = true; }   // keep it spellable
                if (isV) vowels++;
                plan.Add(new KeyValuePair<int, char>(col, ch)); planned[col]++;
            }

            StageStats.Rains++; StageStats.SmartSeeds += smart;
            WDLog.Log($"[Rain] {plan.Count} letter(s): " + string.Join(" ", plan.ConvertAll(p => $"{p.Value}@{p.Key}")));
            GameAudio.Instance?.PlayWhooshFast();

            // 3) Drop them: data first (so nothing can claim the slot), then the old fall animation, staggered like rain.
            float longest = 0f;
            for (int pi = 0; pi < plan.Count; pi++)
            {
                var kv = plan[pi];
                int c = kv.Key;
                int row = rules.GetColumnHeight(c);
                if (row >= rows) continue;
                bool gold = goldIdx.Contains(pi);
                rules.SetCell(c, row, new RulesCellData { Letter = kv.Value, Col = c, Row = row, PlayerIndex = -1, IsRainGold = gold });
                var t = grid.DropTileAndGetTile(c, kv.Value, TileOwner.Player);
                if (t != null) { t.MarkAnimating(0.5f); if (gold) { t.SetRainGold(true); WDLog.Log($"[Rain] GOLD '{kv.Value}' -> col {c}"); } }
                longest = 0.5f;
                yield return new WaitForSeconds(RAIN_STAGGER);
            }
            if (longest > 0f) yield return new WaitForSeconds(longest);
            GameAudio.Instance?.PlayTileDrop();
        }

        private IEnumerator ExecuteRisingRow()
        {
            // LEVEL WON = NO MORE RISES. 2026-10-20 Spencer: "whenever you hit the scoring objective the
            // level should probably not have row rises firing after this". Log: the winning swap had
            // already SCHEDULED a rise; the points landed, the level cleared, and the rise fired 1.3s
            // later on the won board, right before "Well Done". Five different triggers can start a rise
            // (per-move, its deferred wait, stage-start queue, starved refill, clock) and none checked the
            // won state, so the refusal lives HERE, at the one place they all end up.
            if (AwaitingNextLevel)
            {
                WDLog.Log("[RisePerMove/Execute] blocked: level already won");
                _riseScheduledPending = false;
                yield break;
            }
            _isRisingRow = true;
            _riseScheduledPending = false; // the queued rise is now executing — no longer "pending"

            // Rising rows disabled for this level (tutorial RisesOff levels set RisingRowManager.Enabled
            // = false). The TIME-BASED rise clock here doesn't go through ShouldRiseThisTurn, so honor the
            // disable explicitly or rows still creep up on a free-play level. 2026-06-25 Spencer.
            if (RisingRowManager.Instance == null || !RisingRowManager.Enabled)
            {
                _isRisingRow = false;
                yield break;
            }

            bool refill = _nextRiseIsRefill; _nextRiseIsRefill = false;
            if (ScoreRunMode && (RAIN_MODE || (refill && RAIN_FOR_REFILL)))
            {
                yield return StartCoroutine(RainLetters(RAIN_COUNT));
                _isRisingRow = false;
                yield break;
            }

            bool isProcessing = MatchController.Instance != null && MatchController.Instance.IsProcessing;
            bool isRewriting  = HandManager.Instance != null && HandManager.Instance.IsRewriteModeActive;
            bool rmExists     = RisingRowManager.Instance != null;
            WDLog.Log($"[RisePerMove/Execute] isProcessing={isProcessing} isRewriting={isRewriting} rmExists={rmExists}");

            // If player is mid-drop or in rewrite mode, defer (rewrite auto-cancels after 5s)
            if (isProcessing)
            {
                WDLog.Log("[RisePerMove/Execute] blocked: IsProcessing=true");
                _isRisingRow = false;
                yield break;
            }
            if (isRewriting)
            {
                WDLog.Log("[RisePerMove/Execute] blocked: IsRewriteModeActive=true");
                _isRisingRow = false;
                yield break;
            }

            if (!rmExists)
            {
                WDLog.Log("[RisePerMove/Execute] blocked: RisingRowManager null");
                _isRisingRow = false;
                yield break;
            }

            // Objective already met → the stage is WON; don't let a row rise after a win. POLL-BASED
            // objectives (ice/vault) complete a frame AFTER the drop that scheduled this rise, so the
            // schedule-time !clearedStageThisDrop gate misses them — freeze the rise here. 2026-06-15 Spencer.
            // EscortWinPending: HeroWord wins lock in the instant the last chicken is collected, but
            // IsComplete only flips when its fly-up lands — block the rise across that window too. 2026-06-23.
            if (ObjectiveManager.Instance != null && ObjectiveManager.Instance.Active != null
                && (ObjectiveManager.Instance.Active.IsComplete || ObjectiveManager.Instance.EscortWinPending))
            {
                WDLog.Log("[RisePerMove/Execute] blocked: objective complete / escort-win pending (stage won) — freezing the rise");
                _isRisingRow = false;
                yield break;
            }

//             WDLog.Log($"[SurvivalManager] Rising row at {_elapsedTime:F1}s");

            bool overflowed = false;
            yield return StartCoroutine(RisingRowManager.Instance.RiseRow((overflow) =>
            {
                overflowed = overflow;
            }));

            if (overflowed && MoveLimitMode)
            {
                ApplyOverflowPenalty();
                overflowed = false;
                yield return StartCoroutine(RisingRowManager.Instance.RiseRow((overflow) => { overflowed = overflow; }));
            }

            if (overflowed)
            {
                TriggerTopOut();
                _isRisingRow = false;
                yield break;
            }

            // Move-limit mode: a tile in the top row is fine — only the NEXT rise overflowing costs anything.
            if (!MoveLimitMode && RulesEngine.Instance != null && ShouldTopOut(RulesEngine.Instance))
            {
                TriggerTopOut();
                _isRisingRow = false;
                yield break;
            }

            // First rise on a rising-intro level (L9): row + new tiles settled, no top-out — fire the one-time
            // spotlight pause. MUST fire after the full rise so the new bottom-row tiles have popped in (an
            // early fire hid them behind the dim before their pop-scale finished). 2026-07-08 Spencer.
            if (TutorialManager.RisingIntroPending)
            {
                TutorialManager.RisingIntroPending = false;
                TutorialManager.Instance?.ShowRisingIntro();
            }
            // Move-limit mode: you can no longer top out (a full board costs moves instead), so the old
            // "clear words fast or you'll top out" coaching is false — don't show it. 2026-10-20.
            else if (!MoveLimitMode && !TutorialManager.NearTopOutShown && TutorialManager.Instance != null
                     && RulesEngine.Instance != null && RulesEngine.Instance.GetRisesUntilTopOut() <= 1)
            {
                // First time the board crests into the danger zone (highest tile within 1 row of the top) —
                // one-shot near-top-out warning beat. 2026-07-08 Spencer.
                TutorialManager.NearTopOutShown = true;
                TutorialManager.Instance.ShowNearTopOutWarning();
            }

            // ── Prime ONLY words a wild completes because of this rise ─────────────
            // 2026-06-08 Spencer (chosen behavior): if the rise slides a WILD into place
            // so it completes a word (X-I-wild → FIX), light it up now instead of making
            // the player wait until their next drop's scan. PrimeWildWordsAfterRise seeds
            // only the wilds it resolves, so incidental all-real-letter words the rise
            // happens to align (SEAR) are NOT primed — only your action primes those.
            var riseRules = RulesEngine.Instance;
            var riseGrid  = GridManager.Instance;
            if (riseRules != null && riseGrid != null)
            {
                var primedByRise = riseRules.PrimeWildWordsAfterRise();
                if (primedByRise != null && primedByRise.Count > 0)
                {
                    foreach (var word in primedByRise)
                    {
                        if (word.Cells == null) continue;
                        int fuse = riseRules.GetFuseLengthPublic(word.Word.Length);
                        foreach (var cell in word.Cells)
                        {
                            Tile glowTile = riseGrid.GetTile(cell.x, cell.y);
                            if (glowTile != null)
                            {
                                // Wild repaint: the rise just committed this wild's letter in data;
                                // push it to the visual so it shows the real letter instead of a
                                // blank magenta tile (mirrors the drop-path repaint). 2026-06-09.
                                if (glowTile.IsWild)
                                {
                                    var rc = riseRules.GetCell(cell.x, cell.y);
                                    if (rc != null && rc.Letter != '\0' && rc.Letter != TileBag.WILD_CHAR
                                        && glowTile.Letter != rc.Letter)
                                        glowTile.SetLetter(rc.Letter);
                                }
                                glowTile.SetPrimedGlow(RulesEngine.PrimedGlowForCell(new Vector2Int(glowTile.Col, glowTile.Row)), playFlash: true, fuseRemaining: fuse);
                                GameParticles.Instance?.PlayPrimed(glowTile.transform.position);
                            }
                        }
                    }
                    GameAudio.Instance?.PlayTilePrimed();
                }
            }

            _isRisingRow = false;
        }

        /// <summary>Check if the game should end based on current top-out mode.</summary>
        private bool ShouldTopOut(RulesEngine rules)
        {
            if (rules == null) return false;
            if (topOutMode == TopOutMode.Strict)
                return rules.HasAnyTileInTopRow();
            else
                return !HasAnyOpenColumn(rules);
        }

        /// <summary>Returns true if at least one column has room for a tile.</summary>
        private bool HasAnyOpenColumn(RulesEngine rules)
        {
            for (int c = 0; c < RulesEngine.COLS; c++)
                if (rules.IsColumnAvailable(c)) return true;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // TOP-OUT / GAME OVER
        // ══════════════════════════════════════════════════════════════════════════

        private void TriggerTopOut(string announce = "Out Of Moves!")
        {
            if (_isGameOver) return;
            if (_continueOffered) return;  // Already mid-continue flow — don't re-fire

            // Capture stage state for debrief.
            _lastStageReached   = _currentStageIndex;
            _lastStageTarget    = CurrentStageTarget;
            _lastStageShortfall = _currentStageCleared ? 0 : Mathf.Max(0, CurrentStageTarget - CurrentStageScore);

            _continueOffered = true;

            // Disable player input immediately so the announcement panel
            // can't be tapped through to anything underneath.
            if (HandManager.Instance != null)
                HandManager.Instance.IsInteractable = false;

            // Freeze rising rows / auto-drops during the announcement + continue offer.
            SetOverlayPaused(true);

            // Announce → Continue offer (if continues remain this run).
            // Codex rule: max 2 save events per run. After 2nd save → forced game over.
            void ShowContinueOrFinalize()
            {
                if (!CanOfferContinue)
                {
                    WDLog.Log($"[Continue] Run continue cap reached ({_continuesInRun}/{MAX_CONTINUES_PER_RUN}) — forcing game over.");
                    FinalizeGameOver();
                    return;
                }
                if (ContinueModal.Instance != null)
                {
                    ContinueModal.Instance.Show(this);
                }
                else
                {
                    Debug.LogWarning("[SurvivalManager] ContinueModal missing — falling through to game over.");
                    FinalizeGameOver();
                }
            }

            if (TopOutPanel.Instance != null)
            {
                TopOutPanel.Instance.SetText(announce);
                TopOutPanel.Instance.Show(ShowContinueOrFinalize);
            }
            else
            {
                ShowContinueOrFinalize();
            }
        }

        // MVP P3: continue flow
        private bool _continueOffered;

        // MVP P5: Glade Stillness booster — counter for "skip the next N rises."
        // Works identically in turn-based mode (skip N turns of rises) and
        // time-based mode (skip the next N scheduled clock rises). The counter
        // is decremented on each suppressed rise attempt.
        private int _glaedStillnessRisesRemaining;
        public int GladeStillnessRisesRemaining => _glaedStillnessRisesRemaining;

        /// <summary>Grant N rise-skips. Stacks via Max so re-firing Glade doesn't
        /// reduce a longer existing pause. Caller passes the turn count (e.g., 2 for L1).</summary>
        public void GrantGladeStillnessRises(int rises)
        {
            _glaedStillnessRisesRemaining = Mathf.Max(_glaedStillnessRisesRemaining, rises);
        }

        /// <summary>True if Glade is currently suppressing rises. Consumes one
        /// charge as a side effect — call this exactly at the moment a rise
        /// would otherwise fire.</summary>
        public bool ConsumeGladeRiseIfActive()
        {
            if (_glaedStillnessRisesRemaining <= 0) return false;
            _glaedStillnessRisesRemaining--;
            WDLog.Log($"[GladeStillness] Rise skipped. Remaining: {_glaedStillnessRisesRemaining}");
            return true;
        }

        // MVP P3 Path B: continue ladder + cap. Codex rule — max 2 save events
        // (ad or paid combined) per run, paid cost escalates 50→100, ad always free.
        private int _continuesInRun;
        public const int MAX_CONTINUES_PER_RUN = 3; // 2026-07-13 Spencer: 3 continues per run (map hearts show ♥ 3/3)
        public const int CONTINUE_BASE_COST = 50;

        /// <summary>Coin cost for the NEXT continue in this run. 50 → 100 then capped.
        /// Reset on StartSurvival.</summary>
        public int CurrentContinueCost
        {
            get
            {
                // 50 * 2^n for n=0,1 → 50, 100. Cap at 100 since we hard-cap continues at 2.
                int scale = 1 << Mathf.Min(_continuesInRun, 1);
                return CONTINUE_BASE_COST * scale;
            }
        }

        /// <summary>True if the player has any continues remaining this run.</summary>
        public bool CanOfferContinue => _continuesInRun < MAX_CONTINUES_PER_RUN;

        public int ContinuesUsedThisRun => _continuesInRun;

        /// <summary>Apply the "Continue" rescue: clears top 3 rows of tiles and
        /// refills edits/swaps. Caller (ContinueModal) has already spent coins.
        /// Resets the top-out latch so play resumes.</summary>
        public MatchController.StageClearRefillSummary ApplyContinueRescue()
        {
            // Blast-limit: the rescue is MORE BLASTS, not a cleared board — the board was never full.
            if (BlastMode)
            {
                _blastBudget += 2;
                WDLog.Log($"[Blast] continue: +2 blasts -> {BlastsRemaining} left");
                return default;
            }
            // Move limit: the rescue is MORE MOVES (the genre's "+5 moves"), not a cleared board.
            if (MoveLimitMode)
            {
                _currentStageMovesUsed = Mathf.Max(0, _currentStageMovesUsed - CONTINUE_EXTRA_MOVES);
                _continuesInRun++;
                WDLog.Log($"[MoveLimit] continue: +{CONTINUE_EXTRA_MOVES} moves -> {CurrentStageMovesRemaining} left, continues_used={_continuesInRun}");
                return default;
            }
            // 1) Clear top 3 rows of tiles (highest row indices in the board).
            var toRemove = new System.Collections.Generic.List<Vector2Int>();
            if (RulesEngine.Instance != null)
            {
                int rows = RulesEngine.ROWS;
                int firstRowToClear = Mathf.Max(0, rows - 3);
                for (int row = firstRowToClear; row < rows; row++)
                {
                    for (int col = 0; col < RulesEngine.COLS; col++)
                    {
                        var c = RulesEngine.Instance.GetCell(col, row);
                        if (c == null) continue;
                        // SPARE objective tiles: HeroWord escort drop-targets, vaults (anchored chests),
                        // and ice — the continue frees space by clearing NORMAL tiles only, it must not
                        // wipe out the things the player is working toward. 2026-06-15 Spencer.
                        if (c.IsDropTarget || c.IsAnchored || c.IsFrozen) continue;
                        toRemove.Add(new Vector2Int(col, row));
                    }
                }
                // Clear board-side state first so GridManager.RemoveTiles' grid
                // view and RulesEngine's data view stay in sync.
                for (int i = 0; i < toRemove.Count; i++)
                    RulesEngine.Instance.ClearCell(toRemove[i].x, toRemove[i].y);
            }
            if (GridManager.Instance != null && toRemove.Count > 0)
                GridManager.Instance.RemoveTiles(toRemove);

            // 1b) SETTLE the board so SPARED objective tiles (HeroWord escort drop-targets) fall down
            // onto the stack instead of hanging in midair where the cleared rows used to be — the same
            // gravity pass every explosion runs. The continue-clear was missing it. 2026-06-18 Spencer.
            if (toRemove.Count > 0 && RulesEngine.Instance != null && GridManager.Instance != null)
            {
                var gravityMoves = RulesEngine.Instance.ApplyGravityInDataPublic();
                if (gravityMoves != null && gravityMoves.Count > 0)
                    GridManager.Instance.StartCoroutine(GridManager.Instance.ApplyGravityFromEvents(gravityMoves));
            }

            // Words lost letters to the wipe — drop any that no longer exist, and retrace.
            // Without this a word kept ONE surviving tile primed and its outline stretched up
            // into the empty rows the rescue had just cleared, still showing a payout for a
            // word that was gone. Same five-event rule as every other board display: a wipe is
            // a board change. 2026-09-10 Spencer.
            RulesEngine.Instance?.ValidatePrimedWords();
            ClusterOutline.Instance?.Refresh();

            // 2) Refill edits + swaps using the existing stage-clear helper.
            MatchController.StageClearRefillSummary summary = default;
            if (MatchController.Instance != null)
                summary = MatchController.Instance.RefillStageClearResources(MatchController.PLAYER_HUMAN);

            // Increment continue counter — ad and coin paths both call this.
            // Caller (ContinueModal) has already paid (coins or ad) before calling.
            _continuesInRun++;

            WDLog.Log($"[Continue] Rescue applied: cleared {toRemove.Count} tiles, edits→{summary.RewritesAfter}, swaps→{summary.SwapsAfter}, continues_used={_continuesInRun}/{MAX_CONTINUES_PER_RUN}");
            try
            {
                AnalyticsManager.Log("continue_accepted",
                    "stage", _currentStageIndex,
                    "tiles_cleared", toRemove.Count,
                    "edits_after", summary.RewritesAfter,
                    "swaps_after", summary.SwapsAfter,
                    "continue_number", _continuesInRun);
            }
            catch (System.Exception ex) { Debug.LogError($"[Continue] Analytics log threw: {ex.Message}"); }

            return summary;
        }

        /// <summary>Reset state so play resumes after a successful continue.
        /// Pairs with ApplyContinueRescue — call after rescue is done.</summary>
        public void ResumeFromContinue()
        {
            _continueOffered = false;
            SetOverlayPaused(false);
            if (HandManager.Instance != null)
                HandManager.Instance.IsInteractable = true;
        }

        /// <summary>Player declined the continue offer. End run, route to game-over.
        /// MVP: no heart cost (hearts dropped from Survival 2026-05-22 after
        /// Claude+Codex review — coin-bypass made the heart system decorative).</summary>
        public void DeclineContinue()
        {
            try
            {
                AnalyticsManager.Log("continue_declined",
                    "stage", _currentStageIndex,
                    "coin_balance", CoinWallet.Balance);
            }
            catch (System.Exception ex) { Debug.LogError($"[Continue] Analytics log threw: {ex.Message}"); }

            FinalizeGameOver();
        }

        private void FinalizeGameOver()
        {
            // MVP P3 Path B: personal best tracking. Score best lives in
            // HighScoreManager (already wired); stage best + total runs live in
            // our own PlayerPrefs. Award +50 coins if EITHER bested (once per run).
            int finalScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            // "Level reached" for the PB / leaderboard is the RUN level — the one-time tutorial doesn't count. A
            // game-over during the tutorial reports 0 (never a best). 2026-07-14 Spencer.
            int finalStage = Mathf.Max(0, LevelMapPanel.RunLevel(_lastStageReached));

            int priorBestStage = PlayerPrefs.GetInt(PB_BEST_STAGE_KEY, 0);
            int priorTotalRuns = PlayerPrefs.GetInt(PB_TOTAL_RUNS_KEY, 0);

            // HighScoreManager handles score-best persistence + returns true on improvement.
            int priorBestScore = HighScoreManager.GetBest("survival");
            bool newBestScore = HighScoreManager.Submit(finalScore, "survival");
            bool newBestStage = finalStage > priorBestStage;
            bool anyNewBest = newBestStage || newBestScore;

            if (newBestStage) PlayerPrefs.SetInt(PB_BEST_STAGE_KEY, finalStage);
            PlayerPrefs.SetInt(PB_TOTAL_RUNS_KEY, priorTotalRuns + 1);
            PlayerPrefs.Save();

            WDLog.Log($"[PB] Compare → stage {finalStage} vs prior best {priorBestStage} → newStage={newBestStage} | score {finalScore} vs prior best {priorBestScore} → newScore={newBestScore} | run #{priorTotalRuns + 1}");

            if (anyNewBest)
            {
                CoinWallet.Add(PB_BONUS_COINS);
                WDLog.Log($"[PB] NEW PERSONAL BEST — stage:{newBestStage} score:{newBestScore} (+{PB_BONUS_COINS} coins)");
                try
                {
                    AnalyticsManager.Log("personal_best",
                        "new_best_stage", newBestStage ? 1 : 0,
                        "new_best_score", newBestScore ? 1 : 0,
                        "final_stage", finalStage,
                        "final_score", finalScore,
                        "total_runs", priorTotalRuns + 1);
                }
                catch (System.Exception ex) { Debug.LogError($"[PB] Analytics threw: {ex.Message}"); }
            }

            _wasNewBestStage = newBestStage;
            _wasNewBestScore = newBestScore;

            EmitRunEndAnalytics("topout");
            StopSurvival();
            SetOverlayPaused(false);
            _continueOffered = false;

            if (MatchController.Instance != null)
                MatchController.Instance.ForceGameOver();
            else if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.GameOver);
        }

        // MVP P3 Path B: personal best tracking constants + display flags.
        // Score best is delegated to HighScoreManager.Submit("survival").
        // Stage best + total runs tracked here in our own PlayerPrefs.
        public const string PB_BEST_STAGE_KEY = "wd_best_stage";
        public const string PB_TOTAL_RUNS_KEY = "wd_total_runs";
        public const int    PB_BONUS_COINS    = 50;
        private bool _wasNewBestStage;
        private bool _wasNewBestScore;
        /// <summary>Set during FinalizeGameOver; GameOverUI reads these to celebrate.</summary>
        public bool WasNewBestStage => _wasNewBestStage;
        public bool WasNewBestScore => _wasNewBestScore;
    }
}
