using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Legacy MatchManager — kept as a compile-compatible stub.
    /// Real turn flow is now managed by MatchController.
    ///
    /// This stub preserves the singleton Instance and public API surface
    /// so that GameOverUI, HandManager, and other existing callers compile
    /// without modification. Data is synced from MatchController.EndMatch()
    /// via SyncFromController().
    /// </summary>
    public class MatchManager : MonoBehaviour
    {
        public const int MAX_TILES = 42;
        public const int MAX_TURNS = 30; // DEPRECATED — MatchController.MAX_TURNS is authoritative

        public static MatchManager Instance { get; private set; }

        public bool IsPlayerTurn     { get; private set; } = true;
        public int  TotalTilesPlaced { get; private set; } = 0;
        public int  PlayerTurns      { get; private set; } = 0;
        public bool IsGameOver       { get; private set; } = false;
        public bool IsMatchActive    { get; private set; } = false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[MatchManager] Awake (legacy stub)");
        }

        /// <summary>
        /// Legacy StartMatch — now delegates to MatchController if available.
        /// Kept so GameManager.OnStateEntered(Playing) still compiles.
        /// </summary>
        public void StartMatch()
        {
            IsPlayerTurn     = true;
            TotalTilesPlaced = 0;
            PlayerTurns      = 0;
            IsGameOver       = false;
            IsMatchActive    = true;

            // Delegate to MatchController if it exists
            if (MatchController.Instance != null)
            {
                MatchController.Instance.StartMatch();
                Debug.Log("[MatchManager] StartMatch delegated to MatchController.");
            }
            else
            {
                Debug.Log("[MatchManager] StartMatch (legacy stub — no MatchController).");
            }
        }

        /// <summary>
        /// Called by MatchController.EndMatch() to sync final state
        /// so GameOverUI can read PlayerTurns, IsGameOver, etc.
        /// </summary>
        public void SyncFromController(int playerScore, int aiScore, int playerTurns, int totalTiles)
        {
            PlayerTurns      = playerTurns;
            TotalTilesPlaced = totalTiles;
            IsGameOver       = true;
            IsMatchActive    = false;
            Debug.Log($"[MatchManager] Synced from MatchController: " +
                      $"playerTurns={playerTurns} totalTiles={totalTiles}");
        }

        // ── Legacy stubs — kept so HandManager and others compile ──────────────────

        public void RecordPlayerTurnTaken()
        {
            // No-op: MatchController handles turn tracking
        }

        public void RecordTilePlaced(bool wasPlayerTurn)
        {
            TotalTilesPlaced++;
            IsPlayerTurn = !wasPlayerTurn;
        }

        public void ForceGameOver()
        {
            if (MatchController.Instance != null)
            {
                MatchController.Instance.ForceGameOver();
                return;
            }

            if (!IsMatchActive) return;
            IsGameOver    = true;
            IsMatchActive = false;

            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.GameOver);
        }

        public bool IsTurnLimitReached =>
            MatchController.Instance != null
                ? MatchController.Instance.IsTurnLimitReached
                : (PlayerTurns >= MAX_TURNS);

        public int CountOccupiedCells()
        {
            if (GridManager.Instance != null)
                return GridManager.Instance.CountOccupiedCells();
            return TotalTilesPlaced;
        }
    }
}
