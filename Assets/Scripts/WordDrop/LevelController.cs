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

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>Fired exactly once when CurrentScore >= CurrentLevel.target. Args: score, stars (0..3).</summary>
        public event Action<int, int> OnLevelComplete;

        /// <summary>Fired exactly once when MovesRemaining hits 0 before target met. Args: score, shortfall.</summary>
        public event Action<int, int> OnLevelFail;

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
                FireFail();
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

        /// <summary>Ends the current level without firing Complete/Fail. Used on abort/menu-return.</summary>
        public void AbortLevel()
        {
            IsActive = false;
            IsInputLocked = false;
        }

        // ── Internal ────────────────────────────────────────────────────────────

        private void FireComplete()
        {
            if (_completed) return;
            _completed = true;
            IsActive = false;
            IsInputLocked = true;
            LockHandInput();

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

        private void FireFail()
        {
            if (_failed) return;
            _failed = true;
            IsActive = false;
            IsInputLocked = true;
            LockHandInput();

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
