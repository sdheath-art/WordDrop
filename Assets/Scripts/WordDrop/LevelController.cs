using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Orchestrates a discrete level: holds the current LevelData, tracks moves
    /// remaining and accumulated score, fires Complete / Fail events.
    ///
    /// Sibling of SurvivalManager. Both exist so the migration can land one mode
    /// at a time (adapter-first pattern). MatchController routes drop-committed
    /// bookkeeping to whichever is active based on GameManager.CurrentMode.
    ///
    /// Phase 2 scope: vertical slice — start level, count moves, accumulate score,
    /// fire events with Debug.Log only. Phase 3 adds single-fire guards, input
    /// locking, analytics. Phase 4 wires the Level Completed / Out of Moves
    /// modals. Phase 5 adds per-level mechanic gates.
    /// </summary>
    public class LevelController : MonoBehaviour
    {
        public static LevelController Instance { get; private set; }

        // ── Active state ────────────────────────────────────────────────────────

        public LevelData CurrentLevel { get; private set; }
        public int MovesRemaining { get; private set; }
        public int CurrentScore { get; private set; }
        public bool IsActive { get; private set; }

        /// <summary>
        /// Wired up properly in Phase 3; already here so HandManager/debug menu
        /// can consult it without caring which phase we're in.
        /// </summary>
        public bool IsInputLocked { get; private set; }

        // Single-fire guards (expanded in Phase 3).
        private bool _completed;
        private bool _failed;

        /// <summary>
        /// Monotonic id for the current run. Incremented on every StartLevel so
        /// in-flight coroutines (rewarded-ad stubs, tween callbacks) can verify
        /// their grant is still targeting the same attempt before mutating state.
        /// </summary>
        public int RunToken { get; private set; }

        /// <summary>
        /// True when the most recent fail was caused by move-budget exhaustion
        /// (a +5-moves booster can legitimately continue the same attempt).
        /// False when the fail was ForceFail'd externally — e.g. board-full or
        /// rising-row overflow — where adding moves alone wouldn't unstick play.
        /// </summary>
        public bool LastFailIsRecoverable { get; private set; }

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>Fired exactly once when CurrentScore >= CurrentLevel.target. Args: score, stars (0..3).</summary>
        public event Action<int, int> OnLevelComplete;

        /// <summary>Fired exactly once when MovesRemaining hits 0 before target met. Args: score, shortfall.</summary>
        public event Action<int, int> OnLevelFail;

        // ── Tutorial event triggers (Phase 6) ───────────────────────────────────
        //
        // Each of these fires at most once per level. LevelTutorialOverlay
        // subscribes and shows the matching TutorialPrompt from LevelData.

        public event Action OnFirstWord;
        public event Action OnFirstDetonation;
        public event Action OnFirstChain;

        private bool _firstWordFired;
        private bool _firstDetonationFired;
        private bool _firstChainFired;

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts a new level. Resets all state. Expected to be called BEFORE
        /// GameManager.TransitionTo(Playing) so MatchController.StartMatch() sees
        /// the level already initialized.
        /// </summary>
        public void StartLevel(LevelData data)
        {
            if (data == null)
            {
                Debug.LogError("[LevelController] StartLevel called with null LevelData.");
                return;
            }

            CurrentLevel = data;
            MovesRemaining = data.moveBudget;
            CurrentScore = 0;
            IsActive = true;
            IsInputLocked = false;
            _completed = false;
            _failed = false;
            LastFailIsRecoverable = false;
            RunToken++;
            _firstWordFired = false;
            _firstDetonationFired = false;
            _firstChainFired = false;

            // Dismiss any leftover modal from a prior run — StartLevel is the single
            // point of entry for every level start (Level Select, REPLAY, NEXT, RETRY).
            if (LevelCompletedModal.Instance != null)
                LevelCompletedModal.Instance.SetVisible(false);
            if (OutOfMovesModal.Instance != null)
                OutOfMovesModal.Instance.SetVisible(false);

            // Push the level's tutorial prompt set (if any) to the overlay.
            if (LevelTutorialOverlay.Instance != null)
                LevelTutorialOverlay.Instance.OnLevelStarted(data);

            // Visual cues are applied by MatchController.ApplyLevelStartingBoard
            // AFTER tiles are placed + auto-primed. Calling from here would target
            // null or stale tiles.

            Debug.Log($"[LevelController] StartLevel id={data.levelId} " +
                      $"name='{data.displayName}' target={data.target} moves={data.moveBudget}");

            AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_START,
                "level_id", data.levelId,
                "target", data.target,
                "move_budget", data.moveBudget);
        }

        /// <summary>
        /// Called by MatchController after a human drop is fully resolved.
        /// Adds scoreDelta, decrements MovesRemaining, then checks Win (score >= target)
        /// before Fail (moves == 0). Win takes precedence — see Phase 3 plan.
        /// </summary>
        public void NotifyDrop(int scoreDelta)
        {
            if (!IsActive) return;
            if (_completed || _failed) return;

            CurrentScore += Mathf.Max(0, scoreDelta);
            MovesRemaining = Mathf.Max(0, MovesRemaining - 1);

            // Win check FIRST — a drop that simultaneously hits target and
            // exhausts moves must fire Complete, not Fail.
            if (CurrentScore >= CurrentLevel.target)
            {
                FireComplete();
                return;
            }

            if (MovesRemaining <= 0)
            {
                FireFail(recoverable: true);
            }
        }

        /// <summary>Direct score bump without consuming a move (placeholder for Phase 5 / Phase 7 boosters).</summary>
        public void NotifyScore(int scoreDelta)
        {
            if (!IsActive || _completed || _failed) return;
            CurrentScore += Mathf.Max(0, scoreDelta);

            if (CurrentScore >= CurrentLevel.target)
                FireComplete();
        }

        /// <summary>
        /// Side-channel for the tutorial event triggers. Called from MatchController
        /// after each drop with the drop's full scoring breakdown. Fires at-most-once
        /// events based on what happened.
        /// </summary>
        public void NotifyDropDetails(int scoreDelta, int chainBonus, int detonationBonus)
        {
            if (!IsActive) return;

            if (!_firstWordFired && scoreDelta > 0)
            {
                _firstWordFired = true;
                OnFirstWord?.Invoke();
            }
            if (!_firstDetonationFired && detonationBonus > 0)
            {
                _firstDetonationFired = true;
                OnFirstDetonation?.Invoke();
            }
            if (!_firstChainFired && chainBonus > 0)
            {
                _firstChainFired = true;
                OnFirstChain?.Invoke();
            }
        }

        /// <summary>
        /// Booster entry point: adds moves after an Out-of-Moves fail so the player can
        /// continue the same attempt. Strict preconditions — ignores calls when the
        /// level isn't in a recoverable failed state, or when the caller's runToken
        /// doesn't match the current run (rewarded-ad coroutines can outlive an abort
        /// or retry). Returns true iff the grant actually applied.
        /// </summary>
        public bool GrantExtraMoves(int amount, int runToken)
        {
            if (amount <= 0) return false;
            if (runToken != RunToken)
            {
                Debug.Log($"[LevelController] GrantExtraMoves rejected: stale runToken {runToken} vs {RunToken}");
                return false;
            }
            if (!_failed)
            {
                Debug.Log("[LevelController] GrantExtraMoves rejected: level not in failed state.");
                return false;
            }
            if (_completed) return false;
            if (!LastFailIsRecoverable)
            {
                Debug.Log("[LevelController] GrantExtraMoves rejected: last fail was non-recoverable (board full / rising-row overflow).");
                return false;
            }
            _failed = false;
            IsActive = true;
            IsInputLocked = false;
            MovesRemaining += amount;
            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(true);
            Debug.Log($"[LevelController] GrantExtraMoves +{amount} (now {MovesRemaining})");
            return true;
        }

        /// <summary>Ends the current level without firing Complete/Fail. Used on abort/menu-return.</summary>
        public void AbortLevel()
        {
            IsActive = false;
            IsInputLocked = false;
            RunToken++; // invalidate any in-flight booster coroutines targeting this run
            if (TutorialVisualCues.Instance != null) TutorialVisualCues.Instance.ClearAll();
            // Abort doesn't fire OnLevelComplete/Fail, so LevelTutorialOverlay's
            // fade-out subscription wouldn't trigger. Force the overlay off here
            // so tutorial prompts can't leak onto the next screen.
            if (LevelTutorialOverlay.Instance != null)
                LevelTutorialOverlay.Instance.ForceHide();
        }

        /// <summary>
        /// External trigger for a forced fail — used when gameplay paths outside the
        /// normal move-budget flow cause the level to end (e.g., MatchController's
        /// board-full safety net when rising rows overflow the grid in Level mode).
        /// Idempotent: if the level already ended, does nothing. Marks the fail as
        /// non-recoverable so +5-moves booster won't pretend to fix an unwinnable
        /// board.
        /// </summary>
        public void ForceFail()
        {
            if (!IsActive || _completed || _failed) return;
            FireFail(recoverable: false);
        }

        // ── Internal ────────────────────────────────────────────────────────────

        private void FireComplete()
        {
            if (_completed) return;
            _completed = true;
            IsActive = false;
            IsInputLocked = true;
            LockHandInput();
            if (TutorialVisualCues.Instance != null) TutorialVisualCues.Instance.ClearAll();

            int stars = ComputeStars(CurrentScore, CurrentLevel.starThresholds);
            int movesUsed = CurrentLevel.moveBudget - MovesRemaining;

            Debug.Log($"[LevelController] COMPLETE score={CurrentScore} target={CurrentLevel.target} " +
                      $"stars={stars} movesUsed={movesUsed}");

            AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_COMPLETE,
                "level_id", CurrentLevel.levelId,
                "score", CurrentScore,
                "stars", stars,
                "moves_used", movesUsed);

            OnLevelComplete?.Invoke(CurrentScore, stars);
        }

        private void FireFail(bool recoverable)
        {
            if (_failed) return;
            _failed = true;
            LastFailIsRecoverable = recoverable;
            IsActive = false;
            IsInputLocked = true;
            LockHandInput();
            if (TutorialVisualCues.Instance != null) TutorialVisualCues.Instance.ClearAll();

            int shortfall = Mathf.Max(0, CurrentLevel.target - CurrentScore);
            int movesUsed = CurrentLevel.moveBudget - MovesRemaining;

            Debug.Log($"[LevelController] FAIL score={CurrentScore} target={CurrentLevel.target} " +
                      $"shortfall={shortfall}");

            AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_FAIL,
                "level_id", CurrentLevel.levelId,
                "score", CurrentScore,
                "shortfall", shortfall,
                "moves_used", movesUsed);

            OnLevelFail?.Invoke(CurrentScore, shortfall);
        }

        /// <summary>
        /// Defense-in-depth: the Complete/Fail event is fired synchronously but the
        /// debug menu / Phase 4 modals transition to GameOver asynchronously. Directly
        /// disable hand input so no extra drops sneak in during that window.
        /// IsInputLocked remains the source of truth for any future code paths.
        /// </summary>
        private void LockHandInput()
        {
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);
        }

        // ── Per-level mechanic/hazard gating (Phase 5) ──────────────────────────

        /// <summary>
        /// Gates per-level mechanic availability. Callers use
        ///   if (!LevelController.IsMechanicAllowed("gold")) return;
        /// at the choke point for each mechanic (Arm/Trigger/Inject).
        ///
        /// Fail-safe default: when NOT in a Level (Survival/Classic/Daily/Blitz),
        /// returns true so existing mode behavior is untouched. When IN a Level,
        /// returns true only if the mechanic name appears in allowedMechanics.
        ///
        /// Canonical mechanic strings live in LevelValidator.AllowedMechanics.
        /// </summary>
        public static bool IsMechanicAllowed(string mechanic)
        {
            var inst = Instance;
            if (inst == null || !inst.IsActive) return true;
            var list = inst.CurrentLevel?.allowedMechanics;
            if (list == null) return false;
            for (int i = 0; i < list.Length; i++)
                if (list[i] == mechanic) return true;
            return false;
        }

        /// <summary>
        /// Gates per-level hazard availability (e.g., "rising_rows"). Same
        /// default-true-outside-Level behavior as IsMechanicAllowed.
        /// </summary>
        public static bool IsHazardActive(string hazard)
        {
            var inst = Instance;
            if (inst == null || !inst.IsActive) return true;
            var list = inst.CurrentLevel?.hazards;
            if (list == null) return false;
            for (int i = 0; i < list.Length; i++)
                if (list[i] == hazard) return true;
            return false;
        }

        /// <summary>
        /// stars = 1 (for reaching target) + 1 per crossed extra threshold.
        /// Returns 0 if score is below target.
        /// </summary>
        public static int ComputeStars(int score, int[] thresholds)
        {
            if (thresholds == null || thresholds.Length != 3) return 0;
            if (score < thresholds[0]) return 0;
            int stars = 1;
            if (score >= thresholds[1]) stars++;
            if (score >= thresholds[2]) stars++;
            return stars;
        }
    }
}
