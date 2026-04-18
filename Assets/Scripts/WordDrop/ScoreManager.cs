using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Tracks player and AI scores separately across the match.
    /// TotalScore is an alias for PlayerScore for backward compatibility.
    ///
    /// Combo system:
    ///   ComboCount tracks consecutive player turns where at least one word was scored.
    ///   TurnsUntilComboExpires counts down each player turn; reset to 2 when a word is scored.
    ///   GetComboMultiplier() returns 1x / 1.5x / 2x based on combo count.
    ///
    ///   Timeline example:
    ///     Turn 1: AdvancePlayerTurn() → expires stays 0 (no-op)
    ///             RecordWordScored()  → ComboCount=1, expires=2
    ///     Turn 2: AdvancePlayerTurn() → expires=1
    ///             RecordWordScored()  → ComboCount=2 (×1.5!), expires=2
    ///     Turn 3: AdvancePlayerTurn() → expires=1
    ///             RecordWordScored()  → ComboCount=3 (×2.0!), expires=2
    ///     Turn 4: AdvancePlayerTurn() → expires=1
    ///             No word scored      → (expires stays 1)
    ///     Turn 5: AdvancePlayerTurn() → expires=0 → ComboCount reset to 0
    /// </summary>
    public class ScoreManager : MonoBehaviour
    {
        public static ScoreManager Instance { get; private set; }

        // ── Score state ───────────────────────────────────────────────────────────
        public int PlayerScore { get; private set; } = 0;
        public int AIScore     { get; private set; } = 0;

        /// <summary>Backward-compat alias for PlayerScore.</summary>
        public int TotalScore => PlayerScore;

        // ── Combo state ───────────────────────────────────────────────────────────

        /// <summary>
        /// How many consecutive player turns the player has scored at least one word.
        ///   0 = no combo
        ///   1 = scored last turn (no bonus yet, multiplier still ×1.0)
        ///   2 = scored 2 turns in a row → ×1.5
        ///   3+ = scored 3+ turns in a row → ×2.0
        /// </summary>
        public int ComboCount { get; private set; } = 0;

        /// <summary>
        /// How many more player turns remain before the combo expires.
        /// Set to 2 whenever a word is scored on the player's turn.
        /// Decremented by AdvancePlayerTurn() at the start of each player turn.
        /// When it hits 0, ComboCount resets to 0.
        /// </summary>
        public int TurnsUntilComboExpires { get; private set; } = 0;

        /// <summary>True if a combo bonus is currently active (ComboCount >= 2).</summary>
        public bool IsComboActive => ComboCount >= 2;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
//             Debug.Log("[ScoreManager] Awake");
        }

        // ── Combo API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called at the START of each player turn (before the tile is dropped).
        /// Ticks down TurnsUntilComboExpires. If it reaches 0, the combo resets.
        /// AI turns do NOT call this — combo is player-only.
        /// </summary>
        public void AdvancePlayerTurn()
        {
            if (TurnsUntilComboExpires <= 0)
            {
                // No active combo window — nothing to tick
                return;
            }

            TurnsUntilComboExpires--;
//             Debug.Log($"[ScoreManager] AdvancePlayerTurn — " +
                      // $"TurnsUntilComboExpires={TurnsUntilComboExpires} ComboCount={ComboCount}");

            if (TurnsUntilComboExpires == 0 && ComboCount > 0)
            {
                int oldCombo = ComboCount;
                ComboCount = 0;
//                 Debug.Log($"[ScoreManager] Combo EXPIRED — was {oldCombo} → reset to 0. " +
                          // $"No word scored for 2 consecutive turns.");
                NotifyHUDCombo();
            }
        }

        /// <summary>
        /// Called after a new word is scored on the board.
        ///   isPlayer = true  → advances player combo counter, resets expiry window.
        ///   isPlayer = false → AI scored; player combo is NOT affected.
        ///
        /// Combo progression:
        ///   First word scored:  ComboCount 0→1  (no bonus yet, ×1.0)
        ///   Second consecutive: ComboCount 1→2  (×1.5 bonus active)
        ///   Third consecutive:  ComboCount 2→3  (×2.0 bonus active)
        ///   Subsequent:         ComboCount stays at 3+ (×2.0 cap)
        ///
        /// Each call resets TurnsUntilComboExpires to 2, giving the player
        /// a 2-turn window to score again before the combo breaks.
        /// </summary>
        public void RecordWordScored(bool isPlayer)
        {
            if (!isPlayer)
            {
//                 Debug.Log("[ScoreManager] RecordWordScored(AI) — player combo unchanged.");
                return;
            }

            // Increment combo count (no cap — GetComboMultiplier handles the ceiling)
            ComboCount++;

            // Reset the expiry window to 2 turns from now
            TurnsUntilComboExpires = 2;

            float mult = GetComboMultiplier();
//             Debug.Log($"[ScoreManager] RecordWordScored(Player) — " +
                      // $"ComboCount={ComboCount}  multiplier=×{mult:F1}  " +
                      // $"expiresIn={TurnsUntilComboExpires} turns");

            NotifyHUDCombo();
        }

        /// <summary>
        /// Returns the score multiplier based on the current combo count.
        ///   ComboCount 0-1 → ×1.0 (no bonus)
        ///   ComboCount 2   → ×1.5
        ///   ComboCount 3+  → ×2.0
        /// </summary>
        public float GetComboMultiplier()
        {
            if (ComboCount >= 3) return 2.0f;
            if (ComboCount == 2) return 1.5f;
            return 1.0f;
        }

        /// <summary>Resets the combo state. Call on match reset.</summary>
        private void ResetCombo()
        {
            ComboCount             = 0;
            TurnsUntilComboExpires = 0;
            NotifyHUDCombo();
        }

        private void NotifyHUDCombo()
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetCombo(ComboCount, GetComboMultiplier());
        }

        // ── Score API ─────────────────────────────────────────────────────────────

        /// <summary>Adds points to the player's running total.</summary>
        public void AddPlayerScore(int pts)
        {
            PlayerScore += pts;
//             Debug.Log($"[ScoreManager] AddPlayerScore({pts}) → PlayerScore={PlayerScore}");

            if (HUDManager.Instance != null)
                HUDManager.Instance.SetScore(PlayerScore);
        }

        /// <summary>Adds points to the AI's running total.</summary>
        public void AddAIScore(int pts)
        {
            AIScore += pts;
//             Debug.Log($"[ScoreManager] AddAIScore({pts}) → AIScore={AIScore}");
        }

        /// <summary>
        /// Legacy method kept for backward compatibility with RoundManager.
        /// Routes the round score to PlayerScore.
        /// </summary>
        public void AddScore(int roundScore, int movesRemaining)
        {
            AddPlayerScore(roundScore);
        }

        /// <summary>Resets both scores and the combo state to zero.</summary>
        public void ResetScore()
        {
            PlayerScore = 0;
            AIScore     = 0;
            ResetCombo();

            if (HUDManager.Instance != null)
                HUDManager.Instance.SetScore(0);

//             Debug.Log("[ScoreManager] Scores reset — PlayerScore=0, AIScore=0, Combo=0");
        }

        // ── Legacy per-round API stubs ────────────────────────────────────────────

        public int GetRoundScore(int roundNumber)          => 0;
        public int GetRoundMovesRemaining(int roundNumber) => 0;
    }
}
