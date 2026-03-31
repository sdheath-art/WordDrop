using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// RoundOverUI — no-op stub.
    ///
    /// The round-based flow has been removed from the Scrabble-drop game.
    /// The game now runs until the board is full (42 tiles) and transitions
    /// directly to GameOverUI. This class is kept as a compile stub so that
    /// any residual references in old code paths do not cause build errors.
    ///
    /// Do NOT instantiate this in SceneBootstrap.
    /// </summary>
    public class RoundOverUI : MonoBehaviour
    {
        public static RoundOverUI Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>No-op stub — round-over UI has been removed.</summary>
        public void Show(bool won, string targetWord, int baseScore, int bonus, int totalScore)
        {
            Debug.LogWarning("[RoundOverUI] Show() called on stub — round flow has been removed.");
        }

        /// <summary>No-op stub.</summary>
        public void SetVisible(bool visible)
        {
            // No-op: panel is not built, nothing to show/hide
        }
    }
}
