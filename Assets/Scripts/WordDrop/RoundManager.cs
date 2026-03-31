using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages the lifecycle of individual rounds within a game.
    /// Now also notifies HUDManager when a round starts or ends.
    /// Calls AdManager.ShowInterstitial() every 2 rounds.
    /// </summary>
    public class RoundManager : MonoBehaviour
    {
        public const int MAX_ROUNDS     = 5;
        public const int MAX_MOVES      = 20;
        public const int BASE_SCORE     = 100;
        public const int BONUS_PER_MOVE = 10;

        public static RoundManager Instance { get; private set; }

        public string TargetWord    { get; private set; } = string.Empty;
        public int    CurrentRound  { get; private set; } = 0;
        public int    MovesUsed     { get; private set; } = 0;
        public bool   IsRoundActive { get; private set; } = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[RoundManager] Awake");
        }

        public void StartGame()
        {
            CurrentRound = 0;
            MovesUsed    = 0;
            BeginNextRound();
        }

        public void BeginNextRound()
        {
            if (GridManager.Instance != null)
                GridManager.Instance.ClearAllCells();

            CurrentRound++;

            if (CurrentRound > MAX_ROUNDS)
            {
                Debug.Log("[RoundManager] All rounds complete → GameOver");
                GameManager.Instance.TransitionTo(GameState.GameOver);
                IsRoundActive = false;
                return;
            }

            // Show interstitial ad every 2 rounds (rounds 2, 4)
            if (CurrentRound > 1 && (CurrentRound % 2 == 0))
            {
                if (AdManager.Instance != null)
                    AdManager.Instance.ShowInterstitial();
            }

            MovesUsed     = 0;
            TargetWord    = WordBank.GetRandomWord();
            IsRoundActive = true;

            Debug.Log($"[RoundManager] Round {CurrentRound}/{MAX_ROUNDS} started — " +
                      $"target word: {TargetWord}");

            AnalyticsManager.Milestone("round", CurrentRound);

            // ── HUD updates ──────────────────────────────────────────────────────
            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.SetRound(CurrentRound, MAX_ROUNDS);
                HUDManager.Instance.ResetTargetDisplay(TargetWord);
            }

            // Score display refresh (score persists across rounds)
            if (HUDManager.Instance != null && ScoreManager.Instance != null)
                HUDManager.Instance.SetScore(ScoreManager.Instance.TotalScore);

            if (HandManager.Instance != null)
                HandManager.Instance.InitialiseHand();

            // Show column arrows when hand is initialised
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);
        }

        /// <summary>
        /// Records that the player used a move.
        /// </summary>
        public bool RecordPlayerMove()
        {
            if (!IsRoundActive) return false;

            MovesUsed++;
            Debug.Log($"[RoundManager] Move recorded — total moves: {MovesUsed}");
            return false;
        }

        public void RoundWon()
        {
            if (!IsRoundActive) return;
            IsRoundActive = false;

            int movesRemaining = Mathf.Max(0, MAX_MOVES - MovesUsed);
            int bonus          = movesRemaining * BONUS_PER_MOVE;
            int roundScore     = BASE_SCORE + bonus;

            Debug.Log($"[RoundManager] Round {CurrentRound} WON! " +
                      $"Base={BASE_SCORE}, Bonus={bonus} ({movesRemaining} moves left), " +
                      $"RoundScore={roundScore}");

            // Reveal all target letters on win
            if (HUDManager.Instance != null)
                HUDManager.Instance.RevealAllTargetLetters();

            if (ScoreManager.Instance != null)
                ScoreManager.Instance.AddScore(roundScore, movesRemaining);

            AnalyticsManager.Log("round_won",
                "round",           CurrentRound,
                "moves_used",      MovesUsed,
                "moves_remaining", movesRemaining,
                "round_score",     roundScore);

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            // Hide column arrows
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            GameManager.Instance.TransitionTo(GameState.RoundOver);
        }

        public void RoundLost()
        {
            if (!IsRoundActive) return;
            IsRoundActive = false;

            Debug.Log($"[RoundManager] Round {CurrentRound} LOST (grid full). " +
                      $"Target was: {TargetWord}");

            // Reveal target word on loss so player can see what they missed
            if (HUDManager.Instance != null)
                HUDManager.Instance.RevealAllTargetLetters();

            if (ScoreManager.Instance != null)
                ScoreManager.Instance.AddScore(0, 0);

            AnalyticsManager.Log("round_lost",
                "round",      CurrentRound,
                "moves_used", MovesUsed,
                "target",     TargetWord);

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            // Hide column arrows
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            GameManager.Instance.TransitionTo(GameState.RoundOver);
        }

        public int RoundsRemaining => MAX_ROUNDS - CurrentRound;
        public int MovesRemaining  => Mathf.Max(0, MAX_MOVES - MovesUsed);
    }
}
