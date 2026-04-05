using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Rising Rows mechanic: every N global turns, a row of random neutral letters
    /// rises from the bottom of the board. Existing tiles shift up to make room.
    /// If any tile would be pushed above the top row, the game ends (board overflow).
    ///
    /// Toggleable via RisingRowManager.Enabled — off by default for testing.
    /// Skipped in Blitz mode. Active in Classic 1v1 and Daily Drop.
    /// </summary>
    public class RisingRowManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────────

        public static RisingRowManager Instance { get; private set; }

        // ── Configuration ─────────────────────────────────────────────────────────

        /// <summary>Master toggle — set from MenuUI or Inspector.</summary>
        public static bool Enabled = false;

        /// <summary>How many global turns between each rising row. Default 4.</summary>
        public static int TurnInterval = 4;

        /// <summary>No rising rows before this global turn — let players establish their board.</summary>
        public static int GracePeriod = 8; // ~4 turns per player before first rise

        // ── Animation tuning ──────────────────────────────────────────────────────

        private const float RISE_DURATION = 0.25f;  // seconds for tiles to slide up
        private const float NEW_ROW_DELAY = 0.08f;  // pause before new row appears

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[RisingRowManager] Awake — Enabled=" + Enabled +
                      " TurnInterval=" + TurnInterval);
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if a rising row should trigger this turn.
        /// Called after both players have gone (or after the human turn in solo modes).
        /// </summary>
        public bool ShouldRiseThisTurn(int globalTurn)
        {
            if (!Enabled) return false;
            if (BlitzManager.IsBlitzMode) return false;
            if (globalTurn <= 0) return false;
            if (TurnInterval <= 0) return false;
            if (globalTurn < GracePeriod) return false; // let players establish board first
            return (globalTurn % TurnInterval) == 0;
        }

        /// <summary>
        /// Executes the full rising row sequence: checks overflow, shifts data + visuals,
        /// fills new bottom row. Returns a coroutine that animates the rise.
        /// Sets overflowed=true if the board overflows (game should end).
        /// </summary>
        public IEnumerator RiseRow(System.Action<bool> onComplete)
        {
            RulesEngine rules = RulesEngine.Instance;
            GridManager grid = GridManager.Instance;

            if (rules == null || grid == null)
            {
                Debug.LogError("[RisingRowManager] Missing RulesEngine or GridManager.");
                onComplete?.Invoke(false);
                yield break;
            }

            // 1. Check for overflow: if any tile exists in the top row, game over
            if (rules.HasTilesInTopRow())
            {
                Debug.Log("[RisingRowManager] Board overflow! Tiles in top row would be pushed off.");
                onComplete?.Invoke(true); // overflow = true
                yield break;
            }

            TileBag bag = MatchController.Instance != null
                ? MatchController.Instance.Bag
                : new TileBag();

            // 2. Shift board data up one row in RulesEngine
            var shiftMoves = rules.ShiftBoardUp();

            // 2b. Shift bonus cells up before filling new row
            rules.ShiftBonusCellsUp();

            // 3. Fill bottom row with random neutral letters
            char[] newLetters = rules.FillBottomRow(bag);

            // 3b. Board Blessing: place 1-2 bonus cells on the new bottom row
            var bonusCols = rules.PlaceBonusCellsOnBottomRow();

            // 4. Update primed word cell positions (shift all cells up by 1 row)
            if (rules.PrimedRegistry != null && shiftMoves.Count > 0)
                rules.PrimedRegistry.UpdateCellPositions(shiftMoves);

            // 5. Detect new words created by the rise, prime them, THEN rebuild scored keys
            var newWords = rules.DetectAndPrimeRiseWords();

            // 5b. Rebuild scored word keys — marks all current words (including new primed ones)
            // as already scored so they don't re-trigger on the next drop
            rules.RebuildScoredKeysAfterRise();

            // 6. Play rising row sound
            GameAudio.Instance?.PlayRisingRow();

            // 7. Animate: shift existing visual tiles up, create new bottom row tiles
            yield return StartCoroutine(grid.AnimateRiseRow(rules, shiftMoves, newLetters));

            // 8. Refresh bonus cell visual overlays
            grid.RefreshBonusCellOverlays();

            // 9. Apply primed glow to any tiles in newly created words
            if (newWords.Count > 0)
            {
                foreach (var word in newWords)
                {
                    if (word.Cells == null) continue;
                    foreach (var cell in word.Cells)
                    {
                        Tile tile = grid.GetTile(cell.x, cell.y);
                        if (tile != null)
                        {
                            tile.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: true);
                            GameParticles.Instance?.PlayPrimed(tile.transform.position);
                        }
                    }
                }
                GameAudio.Instance?.PlayTilePrimed();
            }

            Debug.Log("[RisingRowManager] Rising row complete. New bottom row: " +
                      new string(newLetters));

            onComplete?.Invoke(false); // no overflow
        }
    }
}
