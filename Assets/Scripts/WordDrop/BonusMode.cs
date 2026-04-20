using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Bonus Mode state machine. Triggered by a filled ChainMeter. During bonus:
    ///   - Every valid word auto-detonates (no priming step) — wired in RulesEngine (B2).
    ///   - Each successive word scores at a triangular multiplier: base * (WordCount + 1).
    ///   - Rising rows + stage move-budget pause (flow state) — wired in SurvivalManager (B2).
    ///   - Swap button disabled — wired in HandManager (B2).
    /// Exits on first drop that forms no valid word OR when DropsRemaining hits 0.
    /// </summary>
    public class BonusMode : MonoBehaviour
    {
        public static BonusMode Instance { get; private set; }

        // ── Tuning ────────────────────────────────────────────────────────────────
        public const int DURATION_DROPS = 5;

        // ── State ─────────────────────────────────────────────────────────────────
        /// <summary>True between Arm() and first drop — bonus will enter on next drop.</summary>
        public bool Armed { get; private set; }

        /// <summary>True while bonus is actively running (between EnterOnDrop and ExitBonus).</summary>
        public bool IsActive { get; private set; }

        /// <summary>Drops remaining in the current bonus. Counts down on each drop that scores.</summary>
        public int DropsRemaining { get; private set; }

        /// <summary>Number of words scored so far this bonus (triangular multiplier driver).</summary>
        public int WordCount { get; private set; }

        /// <summary>Total points banked during the current (or most recent) bonus.</summary>
        public int TotalBanked { get; private set; }

        // ── Events ────────────────────────────────────────────────────────────────
        /// <summary>Fires when meter fills — bonus will enter on next drop.</summary>
        public event Action OnBonusArmed;

        /// <summary>Fires on the drop that actually enters bonus (slam-in moment).</summary>
        public event Action OnBonusEnter;

        /// <summary>Fires each time a word scores inside bonus. int = running WordCount AFTER this word.</summary>
        public event Action<int> OnBonusWordScored;

        /// <summary>Fires when bonus exits. int = TotalBanked, string = reason ("no_word" / "drops_exhausted" / "run_end").</summary>
        public event Action<int, string> OnBonusExit;

        // ── Unity lifecycle ───────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>Called by ChainMeter when capacity is reached. Next drop enters bonus.</summary>
        public void Arm()
        {
            if (Armed || IsActive) return;
            Armed = true;
            Debug.Log("[BonusMode] ARMED — next drop enters bonus.");
            OnBonusArmed?.Invoke();
        }

        /// <summary>
        /// Called at the START of a drop resolution (from HandManager or MatchController)
        /// when Armed is true. Transitions Armed → IsActive for this drop onward.
        /// </summary>
        public void EnterOnDrop()
        {
            if (!Armed || IsActive) return;
            Armed           = false;
            IsActive        = true;
            DropsRemaining  = DURATION_DROPS;
            WordCount       = 0;
            TotalBanked     = 0;
            Debug.Log($"[BonusMode] ENTER — {DropsRemaining} drops queued.");
            OnBonusEnter?.Invoke();
        }

        /// <summary>
        /// Called by RulesEngine during DoScoreAndPrime for each word scored under bonus.
        /// Increments WordCount, accumulates points, returns the multiplier to use
        /// (triangular: 1, 2, 3, … — WordCount AFTER this call is the multiplier).
        /// </summary>
        public int NotifyWordScored(int finalScore)
        {
            if (!IsActive) return 1;
            WordCount++;
            TotalBanked += finalScore;
            Debug.Log($"[BonusMode] word #{WordCount} +{finalScore} (banked={TotalBanked})");
            OnBonusWordScored?.Invoke(WordCount);
            return WordCount;
        }

        /// <summary>
        /// Called from MatchController.CompleteDropBookkeeping after each drop while bonus
        /// is active. Always decrement DropsRemaining (whiffs are forgiven — see
        /// decision log 2026-04-18: "no-word exits" clause removed after playtest
        /// showed sparse-board first drops killing bonus in <30ms). Triangular
        /// multiplier still only advances on successful words via NotifyWordScored,
        /// so whiffs don't cheapen the payoff.
        /// </summary>
        public void NotifyDropCompleted(bool formedWord)
        {
            if (!IsActive) return;

            DropsRemaining--;
            if (DropsRemaining <= 0)
                ExitBonus("drops_exhausted");
        }

        /// <summary>Force-exit bonus mode (e.g. game over mid-bonus).</summary>
        public void ExitBonus(string reason)
        {
            if (!IsActive) return;
            int banked      = TotalBanked;
            IsActive        = false;
            DropsRemaining  = 0;
            Debug.Log($"[BonusMode] EXIT ({reason}) — banked={banked} words={WordCount}");
            OnBonusExit?.Invoke(banked, reason);
        }

        /// <summary>Called from SurvivalManager.StartSurvival / StopSurvival to clean state.</summary>
        public void ResetForNewRun()
        {
            if (IsActive) ExitBonus("run_end");
            Armed           = false;
            IsActive        = false;
            DropsRemaining  = 0;
            WordCount       = 0;
            TotalBanked     = 0;
        }
    }
}
