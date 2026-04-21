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

        // ── Resource drop tuning (only special tiles — no normal letter drops) ──
        public const float AUTO_DROP_INTERVAL_START = 45f;   // rare — almost a minute between drops
        public const float AUTO_DROP_INTERVAL_FLOOR = 25f;
        public const float AUTO_DROP_RAMP_RATE      = 0.2f;  // very slow ramp
        public const float AUTO_DROP_RAMP_PERIOD     = 60f;
        private const float WILD_DROP_CHANCE         = 0.12f; // 12% chance when player has resources

        // ── Rising row tuning (move-based, stage-aware) ───────────────────────────
        // Calibrated to produce 6-12 rises per stage so rising rows actually matter
        // within a stage (they were cosmetic at old cadence). Move-based means the
        // player controls when rises fire — no wall-clock pressure — so "every
        // turn" at late stages is still cognitively fair. Mercy slowdown adds
        // +1/+2 moves at high board occupancy so bad RNG doesn't instant-kill.
        // Floor is now 1 (every move at S7+) since the cognitive-load concern that
        // justified the old 3-move floor was solved by move-based rising itself.
        public const int MOVES_PER_RISE_FLOOR = 1;

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
        public const int   STAGE_TARGET_BASE    = 400;
        public const float STAGE_TARGET_GROWTH  = 1.5f;  // ~1.5× per stage
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
        public const int   STAGE_START_MIN_ROWS = 4;
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
        public enum TopOutMode { Strict, Lenient }
        [SerializeField] public TopOutMode topOutMode = TopOutMode.Lenient;

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
        private int   _movesSinceLastRise;   // increments on drop commit, caps at threshold
        private int   _movesPlayed;          // lifetime move count — drives stage + debrief
        private int   _totalAutoDrops;
        private int   _longestChain;
        private bool  _isGameOver;
        private bool  _isAutoDropping;   // prevents re-entrant auto-drops
        private bool  _isRisingRow;      // prevents re-entrant rising rows
        public  bool  IsRisingRow => _isRisingRow;
        private int   _lastStage = 1;    // track for stage-up signal
        private float _tileRepairTimer;  // periodic blank tile repair

        // ── Stage chip target state ───────────────────────────────────────────────
        private int _currentStageIndex = 1;        // stage number (1, 2, 3, …)
        private int _stageStartScore = 0;           // PlayerScore snapshot at stage start
        private int _currentStageMovesUsed = 0;     // moves spent in current stage
        private int _risesInCurrentStage = 0;       // balance analytics — rises fired per stage
        private int _stageStartRisesPending = 0;    // extra rises to fire post-stage-advance to hit minimum
        private bool _currentStageCleared = false;  // hit the target yet?
        private int _lastStageReached = 1;          // for debrief
        private int _lastStageShortfall = 0;        // for debrief near-miss copy
        private int _lastStageTarget = 0;           // for debrief
        private int _stagesCleared = 0;             // lifetime count — balance analytics
        private int _biggestWordScore = 0;          // biggest single word for run debrief

        /// <summary>Target chip count for stage n. Placeholder curve — tune per retune spec.</summary>
        public static int ComputeStageTarget(int stage)
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

        /// <summary>Current stage target.</summary>
        public int CurrentStageTarget => ComputeStageTarget(_currentStageIndex);
        /// <summary>Current stage move budget.</summary>
        public int CurrentStageMoveBudget => ComputeStageMoves(_currentStageIndex);
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
        /// <summary>True if the current stage's target has been hit.</summary>
        public bool IsCurrentStageCleared => _currentStageCleared;
        /// <summary>Current stage number (1, 2, 3, … unbounded).</summary>
        public int CurrentStageIndex => _currentStageIndex;

        /// <summary>Debrief: last stage reached before run ended.</summary>
        public int LastStageReached => _lastStageReached;
        /// <summary>Debrief: points short of last stage's target (0 if cleared).</summary>
        public int LastStageShortfall => _lastStageShortfall;
        /// <summary>Debrief: target the player needed to hit at run end.</summary>
        public int LastStageTarget => _lastStageTarget;

        /// <summary>Fires when the player hits a stage's chip target. Int = stage cleared.</summary>
        public System.Action<int> OnStageCleared;
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

            // Clear-check first — if this drop crossed the target, the stage
            // advances before we check the fail condition.
            CheckStageClear();

            // Stage fail check: move budget hit AND target not cleared → run ends
            if (!_currentStageCleared && _currentStageMovesUsed >= CurrentStageMoveBudget)
            {
                _lastStageReached   = _currentStageIndex;
                _lastStageTarget    = CurrentStageTarget;
                _lastStageShortfall = Mathf.Max(0, CurrentStageTarget - CurrentStageScore);

                // Snapshot stage fail for balance analytics — this is the data
                // that tells us if a stage target is too brutal. Shortfall
                // distribution across 10 runs tells us whether to dial target
                // down (most players fail short by a lot) or dial it up (most
                // clear with ease, only fail on truly bad openings).
                float occupancy = RulesEngine.Instance != null ? RulesEngine.Instance.GetBoardOccupancy() : 0f;
                AnalyticsManager.Log("stage_fail",
                    "stage", _currentStageIndex,
                    "target", CurrentStageTarget,
                    "score", CurrentStageScore,
                    "shortfall", _lastStageShortfall,
                    "moves_used", _currentStageMovesUsed,
                    "moves_budget", CurrentStageMoveBudget,
                    "rises_fired", _risesInCurrentStage,
                    "occupancy", Mathf.RoundToInt(occupancy * 100f));

                Debug.Log($"[Stage] FAILED stage {_currentStageIndex}: needed {CurrentStageTarget}, got {CurrentStageScore} (short {_lastStageShortfall}, rises {_risesInCurrentStage})");
                OnStageFailed?.Invoke(_currentStageIndex);
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
        public void NotifyScoreDelta(int pointsAdded = 0)
        {
            CheckStageClear();
        }

        /// <summary>
        /// Checks whether the current stage's chip target has been hit. If so,
        /// fires OnStageCleared and advances to the next stage. Idempotent —
        /// the _currentStageCleared guard ensures it only fires once per stage.
        /// </summary>
        public void CheckStageClear()
        {
            if (_currentStageCleared) return;
            if (CurrentStageScore < CurrentStageTarget) return;

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

            // Advance stage state FIRST — before any external callbacks — so that
            // even if the event handler blows up, state machine is consistent.
            _currentStageCleared = true;
            _stagesCleared++;
            AdvanceToNextStage();

            Debug.Log($"[Stage] CLEARED stage {clearedStage}: target {clearedTarget}, scored {clearedScore}, moves {clearedMoves}/{clearedBudget}, rises {clearedRises}");

            // Safe to fire analytics now — state is already advanced
            try
            {
                AnalyticsManager.Log("stage_clear",
                    "stage", clearedStage,
                    "target", clearedTarget,
                    "score", clearedScore,
                    "moves_used", clearedMoves,
                    "moves_budget", clearedBudget,
                    "rises_fired", clearedRises,
                    "occupancy", Mathf.RoundToInt(occupancy * 100f));
            }
            catch (System.Exception ex) { Debug.LogError($"[Stage] Analytics log threw: {ex.Message}"); }

            // Finally, fire the subscribed handler. Wrap in try/catch so a handler
            // bug cannot prevent the stage-advance that already happened above.
            try { OnStageCleared?.Invoke(clearedStage); }
            catch (System.Exception ex) { Debug.LogError($"[Stage] OnStageCleared handler threw: {ex}"); }
        }

        /// <summary>Internal stage transition. Snapshots new stage-start score
        /// and resets the rising-row counter so each stage starts with a full
        /// rise window — no carry-over pressure from the previous stage's
        /// last move. Also checks board state: if a big chain cleared out the
        /// board, schedules extra rises to bring it up to STAGE_START_MIN_ROWS
        /// so the player isn't punished for skilled clearing.</summary>
        private void AdvanceToNextStage()
        {
            // Advance by the cleared target (not PlayerScore) so overflow from
            // the triggering drop carries into the new stage's progress.
            int justClearedTarget = CurrentStageTarget;
            _currentStageIndex++;
            _stageStartScore       += justClearedTarget;
            _currentStageMovesUsed = 0;
            _currentStageCleared   = false;
            // Fresh rise window per Spencer's ask — full cadence before first rise.
            _movesSinceLastRise    = 0;
            // Reset per-stage rise counter for analytics
            _risesInCurrentStage   = 0;

            // Board replenish: if the clear-that-ended-the-stage also cleared
            // most of the board, queue enough rises to bring it up to a
            // playable minimum. Fires in Update() over subsequent frames.
            int rows = CountPopulatedRows();
            _stageStartRisesPending = Mathf.Max(0, STAGE_START_MIN_ROWS - rows);
        }

        /// <summary>True if any tile on the board is currently mid-animation.
        /// Used to defer rising-row fires so we don't interrupt gravity/drops/explosions.</summary>
        private bool IsBoardAnimating()
        {
            if (GridManager.Instance == null) return false;
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

//             Debug.Log($"[SurvivalManager] PostClearBoost activated: {boostDrops} drops " +
                      // $"(cleared={cellsCleared}, chain={chainDepth})");

            // ── Board Clear Bonus + Renewal Row ──
            // Use raw tile count instead of occupancy — occupancy is unreliable
            // mid-resolution because gravity hasn't run yet.
            if (RulesEngine.Instance != null)
            {
                int tilesRemaining = RulesEngine.Instance.CountOccupied();
                int totalCells = RulesEngine.COLS * RulesEngine.ROWS; // 63

                // BOARD CLEAR: 5 or fewer tiles left AND we just blew up 12+
                if (tilesRemaining <= 5 && cellsCleared >= 12)
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
//                     Debug.Log($"[SurvivalManager] Renewal row triggered — only {tilesRemaining} tiles remaining");
                    // Force-arm: next Update() tick (once resolution clears) fires a rise.
                    // Counter cap in NotifyDropCommitted keeps this safe even if the
                    // triggering drop's bookkeeping runs before Update.
                    _movesSinceLastRise = CurrentMovesPerRise;
                }
            }
        }

        /// <summary>Call after each player drop to decrement the boost counter.</summary>
        public void ConsumeBoostDrop()
        {
            if (_postClearBoostDrops > 0)
            {
                _postClearBoostDrops--;
//                 Debug.Log($"[SurvivalManager] PostClearBoost consumed — {_postClearBoostDrops} remaining");
            }
        }

        public float ElapsedTime    => _elapsedTime;
        public int   TotalAutoDrops => _totalAutoDrops;
        public int   LongestChain   => _longestChain;
        public bool  IsGameOver     => _isGameOver;

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
//             Debug.Log("[SurvivalManager] Awake");
        }

        private void Update()
        {
            // Global debug keyboard shortcut — works in menu OR gameplay
            if (Input.GetKeyDown(KeyCode.N))
                ToggleNoAssistMode();

            UpdateSurvival();
        }

        private static GUIStyle _noAssistStyle;
        private static GUIStyle _noAssistHintStyle;

        private void OnGUI()
        {
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

            _elapsedTime += Time.deltaTime;

            // Post-clear boost time falloff
            if (_postClearBoostDrops > 0)
            {
                _postClearBoostTime -= Time.deltaTime;
                if (_postClearBoostTime <= 0f)
                {
                    _postClearBoostDrops = 0;
//                     Debug.Log("[SurvivalManager] PostClearBoost expired (time falloff)");
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
                // StartCoroutine(ExecuteAutoDrop());
            }

            // Stage-start replenish: if AdvanceToNextStage found a sparse board,
            // fire extra rising rows to bring it up to the minimum. Consume
            // one pending rise per Update frame so animations complete between
            // them (feels like a dramatic "board fills up, new stage ready").
            if (_stageStartRisesPending > 0)
            {
                if (!IsBoardAnimating())
                {
                    _stageStartRisesPending--;
                    NotifyRiseFired();
                    StartCoroutine(ExecuteRisingRow());
                }
                return; // don't process normal rising this frame — stage-start takes precedence
            }

            // Rising row: move-based. Counter is incremented by
            // NotifyDropCommitted and capped at CurrentMovesPerRise, so at most
            // one rise is queued even across deferred-animation windows.
            if (_movesSinceLastRise >= CurrentMovesPerRise)
            {
                if (!IsBoardAnimating())
                {
                    _movesSinceLastRise = 0;
                    NotifyRiseFired();
                    StartCoroutine(ExecuteRisingRow());
                }
                // else: counter stays at threshold, retries next frame
            }

            // Stage-up detection — fires on AdvanceToNextStage via the chip-target
            // system, not via move-count thresholds. When _currentStageIndex
            // advances, show the stage-up effect.
            if (_currentStageIndex > _lastStage)
            {
                _lastStage = _currentStageIndex;
                if (BonusPopup.Instance != null)
                    BonusPopup.Instance.Show($"STAGE {_currentStageIndex}", new Color(1f, 0.84f, 0.42f, 1f), Vector3.up * 2f, 1.5f);
                GameAudio.Instance?.PlayStageUp();
            }

            // Update HUD
            if (HUDManager.Instance != null)
                HUDManager.Instance.UpdateSurvivalHUD(this);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        public void StartSurvival()
        {
            _elapsedTime            = 0f;
            _autoDropTimer          = 0f;
            _movesSinceLastRise     = 0;
            _movesPlayed            = 0;
            _totalAutoDrops         = 0;
            _longestChain           = 0;
            _isGameOver             = false;
            _isAutoDropping         = false;
            _isRisingRow            = false;
            _lastStage              = 1;
            _postClearBoostDrops    = 0;
            _postClearBoostTime     = 0f;

            // Reset stage chip-target state
            _currentStageIndex      = 1;
            _stageStartScore        = 0;
            _currentStageMovesUsed  = 0;
            _currentStageCleared    = false;
            _lastStageReached       = 1;
            _lastStageShortfall     = 0;
            _lastStageTarget        = 0;
            _risesInCurrentStage    = 0;
            _stagesCleared          = 0;
            _biggestWordScore       = 0;
            _stageStartRisesPending = 0;

            ChainMeter.Instance?.ResetForNewRun();
            BonusMode.Instance?.ResetForNewRun();
//             Debug.Log("[SurvivalManager] Survival started!");
        }

        public void StopSurvival()
        {
            _isGameOver = true;

            BonusMode.Instance?.ResetForNewRun();
            ChainMeter.Instance?.ResetForNewRun();

            // Clear stage-event delegates so a recreated MatchController doesn't
            // inherit stale subscriptions from a destroyed one.
            OnStageCleared = null;
            OnStageFailed  = null;
//             Debug.Log($"[SurvivalManager] Survival ended — elapsed={_elapsedTime:F1}s drops={_totalAutoDrops}");
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
                Instance._movesPlayed           = 0;
                Instance._totalAutoDrops        = 0;
                Instance._longestChain          = 0;
                Instance._isGameOver            = false;
                Instance._isAutoDropping        = false;
                Instance._lastStage             = 1;
                Instance._isRisingRow           = false;

                // Reset stage chip-target state
                Instance._currentStageIndex     = 1;
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
        // AUTO-DROP
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator ExecuteAutoDrop()
        {
            _isAutoDropping = true;

            // If player is mid-drop, defer — timer will re-trigger next frame
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing)
            {
                _isAutoDropping = false;
                yield break;
            }

            RulesEngine rules = RulesEngine.Instance;
            GridManager grid  = GridManager.Instance;
            MatchController mc = MatchController.Instance;

            if (rules == null || grid == null || mc == null)
            {
                _isAutoDropping = false;
                yield break;
            }

            // Pick column with mercy bias — weight by empty rows
            int col = PickMercyColumn(rules);
            if (col < 0)
            {
                // Board is full — skip this resource drop
                _isAutoDropping = false;
                yield break;
            }

            // Draw letter from bag
            TileBag bag = mc.Bag;
            if (bag == null) { _isAutoDropping = false; yield break; }
            char letter = bag.DrawLetter();

            // Begin drop in rules engine
            int playerIdx = -1; // neutral / system drop
            int targetRow = rules.GetLowestEmptyRow(col);
            if (targetRow < 0)
            {
                _isAutoDropping = false;
                yield break;
            }

            // Decide what to drop BEFORE placing on board
            int editsLeft = mc.GetRewritesRemaining(MatchController.PLAYER_HUMAN);
            int swapsLeft = mc.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
            bool needsHelp = editsLeft <= 0 || swapsLeft <= 0;

            bool isGold = false;
            bool isCyan = false;
            bool isSwapRefill = false;
            bool isEditRefill = false;
            bool isWildRefill = false;

            if (!needsHelp)
            {
                // Player is fine — skip most drops, only 20% chance of gold/wild
                if (Random.value > 0.20f)
                {
                    _isAutoDropping = false;
                    yield break; // no drop — nothing placed on board
                }
                // Rare drop: wild or gold
                if (Random.value < WILD_DROP_CHANCE)
                    isWildRefill = true;
                else
                    isGold = true;
            }
            else if (editsLeft <= 0 && swapsLeft <= 0)
            {
                if (Random.value < 0.5f) isEditRefill = true;
                else isSwapRefill = true;
            }
            else if (editsLeft <= 0)
            {
                isEditRefill = true;
            }
            else
            {
                isSwapRefill = true;
            }

            // Now place the tile on the board
            rules.SetCell(col, targetRow, new RulesCellData
            {
                Letter = letter,
                Col = col,
                Row = targetRow,
                PlayerIndex = -1, // neutral / system drop
            });
            _totalAutoDrops++;

            // Phase 5 defense-in-depth: this auto-drop path is Survival-only, but if
            // anything ever triggers it in Level mode, honor allowedMechanics.
            if (isGold && !LevelController.IsMechanicAllowed("gold")) isGold = false;
            if (isWildRefill && !LevelController.IsMechanicAllowed("wild")) isWildRefill = false;

            if (isGold)
                rules.SetBonusCell(col, targetRow, true);

            // Mark refill on the cell data (collected when detonated)
            var cellData = rules.GetCell(col, targetRow);
            if (cellData != null)
            {
                if (isSwapRefill) cellData.IsSwapRefill = true;
                if (isEditRefill) cellData.IsEditRefill = true;
                if (isWildRefill) cellData.IsWildRefill = true;
            }

            // Visual: create tile and animate drop
            Tile droppedTile = grid.CreateSingleTile(col, targetRow, letter);
            if (droppedTile != null)
            {
                if (isGold) droppedTile.SetGoldBonus(true);
                if (isSwapRefill) droppedTile.SetSwapRefillVisual(true);
                if (isEditRefill) droppedTile.SetEditRefillVisual(true);
                if (isWildRefill) droppedTile.SetWildRefillVisual(true);

                Vector3 targetPos = droppedTile.transform.position;
                float spawnY = grid.GridTop + grid.CellSize * 1.5f;
                droppedTile.transform.position = new Vector3(targetPos.x, spawnY, targetPos.z);

                float elapsed = 0f;
                float duration = (spawnY - targetPos.y) / 38f;
                while (elapsed < duration && droppedTile != null)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    droppedTile.transform.position = new Vector3(
                        targetPos.x, Mathf.Lerp(spawnY, targetPos.y, t * t), targetPos.z);
                    yield return null;
                }
                if (droppedTile != null)
                {
                    droppedTile.transform.position = targetPos;
                    droppedTile.PlayLandingSquish();
                    GameAudio.Instance?.PlayTileDrop();
                }
            }

            // Scan for new words — skip if player started a drop during animation
            if (mc.IsProcessing) { _isAutoDropping = false; yield break; }
            var newWords = rules.DetectAndPrimeAtCell(col, targetRow);
            if (newWords != null && newWords.Count > 0)
            {
	                foreach (var word in newWords)
	                {
	                    if (word.Cells == null) continue;
	                    int fuse = rules.GetFuseLengthPublic(word.Word.Length);
	                    foreach (var cell in word.Cells)
	                    {
	                        Tile tile = grid.GetTile(cell.x, cell.y);
	                        if (tile != null)
	                        {
	                            tile.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: true, fuseRemaining: fuse);
	                            GameParticles.Instance?.PlayPrimed(tile.transform.position);
	                        }
                    }
                }
                GameAudio.Instance?.PlayTilePrimed();
            }

            string bonusTag = isGold ? " [GOLD]" : isWildRefill ? " [WILD]" : isSwapRefill ? " [SWAP]" : isEditRefill ? " [EDIT]" : "";
//             Debug.Log($"[SurvivalManager] Auto-drop #{_totalAutoDrops}: '{letter}' → col={col} row={targetRow}{bonusTag}");

            // Top-out check
            if (ShouldTopOut(rules))
            {
                TriggerTopOut();
                _isAutoDropping = false;
                yield break;
            }

            _isAutoDropping = false;
        }

        /// <summary>
        /// Picks a column with mercy bias — columns with more empty rows are weighted higher.
        /// This prevents auto-drops from piling into nearly-full columns.
        /// </summary>
        private int PickMercyColumn(RulesEngine rules)
        {
            float totalWeight = 0f;
            float[] weights = new float[RulesEngine.COLS];

            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                int emptyCount = 0;
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    if (rules.GetCell(c, r) == null) emptyCount++;
                }
                if (emptyCount <= 0) continue; // skip full columns

                // Mercy bias: weight = emptyCount^1.5 (strongly favors emptier columns)
                weights[c] = Mathf.Pow(emptyCount, 1.5f);
                totalWeight += weights[c];
            }

            if (totalWeight <= 0f) return -1; // all columns full

            float roll = Random.value * totalWeight;
            float cumulative = 0f;
            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                cumulative += weights[c];
                if (roll <= cumulative) return c;
            }

            // Fallback (shouldn't reach here)
            return -1;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // RISING ROW
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator ExecuteRisingRow()
        {
            _isRisingRow = true;

            // If player is mid-drop or in rewrite mode, defer (rewrite auto-cancels after 5s)
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing)
            {
                _isRisingRow = false;
                yield break;
            }
            if (HandManager.Instance != null && HandManager.Instance.IsRewriteModeActive)
            {
                _isRisingRow = false;
                yield break;
            }

            if (RisingRowManager.Instance == null)
            {
                _isRisingRow = false;
                yield break;
            }

//             Debug.Log($"[SurvivalManager] Rising row at {_elapsedTime:F1}s");

            bool overflowed = false;
            yield return StartCoroutine(RisingRowManager.Instance.RiseRow((overflow) =>
            {
                overflowed = overflow;
            }));

            if (overflowed)
            {
                TriggerTopOut();
                _isRisingRow = false;
                yield break;
            }

            if (RulesEngine.Instance != null && ShouldTopOut(RulesEngine.Instance))
            {
                TriggerTopOut();
                _isRisingRow = false;
                yield break;
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

        private void TriggerTopOut()
        {
            if (_isGameOver) return;

            // Capture stage state for debrief — top-out is a secondary death
            // condition, but the game-over screen still needs to know what
            // stage/progress the player was at.
            _lastStageReached   = _currentStageIndex;
            _lastStageTarget    = CurrentStageTarget;
            _lastStageShortfall = _currentStageCleared ? 0 : Mathf.Max(0, CurrentStageTarget - CurrentStageScore);

            EmitRunEndAnalytics("topout");

//             Debug.Log($"[SurvivalManager] TOP OUT! Stage={_currentStageIndex} Score={CurrentStageScore}/{CurrentStageTarget}");
            StopSurvival();

            // Tell MatchController the match is over
            if (MatchController.Instance != null)
                MatchController.Instance.ForceGameOver();

            // Disable player input
            if (HandManager.Instance != null)
                HandManager.Instance.IsInteractable = false;

            // Transition to game over
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.GameOver);
        }
    }
}
