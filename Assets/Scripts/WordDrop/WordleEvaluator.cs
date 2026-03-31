using UnityEngine;
using System.Collections.Generic;

namespace WordDrop
{
    /// <summary>
    /// After every tile drop, scans all possible 4-tile lines (horizontal, vertical,
    /// diagonal) and assigns Wordle-style coloring to each tile on the board.
    ///
    /// Green (#538D4E): tile's letter matches the target word at the same position
    ///                  index within at least one valid 4-tile line.
    /// Yellow (#B59F3B): tile's letter exists in the target word but no line gives
    ///                   it a green match.
    /// Gray (#3A3A3C):   tile's letter is not in the target word at all.
    ///
    /// After evaluation, notifies HUDManager to update the target word blanks.
    /// </summary>
    public class WordleEvaluator : MonoBehaviour
    {
        // -----------------------------------------------------------------------
        // Wordle colours
        // -----------------------------------------------------------------------

        public static readonly Color COLOR_GREEN  = new Color(0.325f, 0.553f, 0.306f, 1f); // #538D4E
        public static readonly Color COLOR_YELLOW = new Color(0.710f, 0.624f, 0.231f, 1f); // #B59F3B
        public static readonly Color COLOR_GRAY   = new Color(0.227f, 0.227f, 0.235f, 1f); // #3A3A3C

        // -----------------------------------------------------------------------
        // Singleton
        // -----------------------------------------------------------------------

        public static WordleEvaluator Instance { get; private set; }

        // -----------------------------------------------------------------------
        // Directions for line scanning
        // -----------------------------------------------------------------------

        private static readonly int[][] _directions = new int[][]
        {
            new int[] { 1,  0 },  // horizontal (left to right)
            new int[] { 0,  1 },  // vertical   (bottom to top)
            new int[] { 1,  1 },  // diagonal   (bottom-left to top-right)
            new int[] { 1, -1 },  // diagonal   (top-left to bottom-right)
        };

        // Cached list of all possible 4-tile line starting points
        private List<LineSpec> _allLines;

        // -----------------------------------------------------------------------
        // Unity lifecycle
        // -----------------------------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            PrecomputeAllLines();
            Debug.Log($"[WordleEvaluator] Awake — {_allLines.Count} possible 4-tile lines precomputed.");
        }

        // -----------------------------------------------------------------------
        // Precompute all valid line starting positions
        // -----------------------------------------------------------------------

        private void PrecomputeAllLines()
        {
            _allLines = new List<LineSpec>(128);

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                for (int col = 0; col < GridManager.COLS; col++)
                {
                    for (int row = 0; row < GridManager.ROWS; row++)
                    {
                        int endCol = col + dc * 3;
                        int endRow = row + dr * 3;

                        if (endCol < 0 || endCol >= GridManager.COLS) continue;
                        if (endRow < 0 || endRow >= GridManager.ROWS) continue;

                        _allLines.Add(new LineSpec
                        {
                            StartCol = col,
                            StartRow = row,
                            DirIndex = dirIdx
                        });
                    }
                }
            }
        }

        // -----------------------------------------------------------------------
        // Public API
        // -----------------------------------------------------------------------

        /// <summary>
        /// Re-evaluates all tiles on the board and updates their border colours.
        /// Also notifies HUDManager to refresh the target word blank display.
        /// Call this after every tile drop (player or AI).
        /// </summary>
        public void EvaluateBoard()
        {
            GridManager grid = GridManager.Instance;
            if (grid == null) return;

            string targetWord = RoundManager.Instance != null
                ? RoundManager.Instance.TargetWord
                : "";

            if (string.IsNullOrEmpty(targetWord) || targetWord.Length != 4)
            {
                Debug.LogWarning("[WordleEvaluator] Target word is invalid or not set.");
                return;
            }

            // --- Phase 1: Determine which tiles are GREEN ---
            bool[,] isGreen = new bool[GridManager.COLS, GridManager.ROWS];

            for (int i = 0; i < _allLines.Count; i++)
            {
                LineSpec line = _allLines[i];
                int dc = _directions[line.DirIndex][0];
                int dr = _directions[line.DirIndex][1];

                for (int step = 0; step < 4; step++)
                {
                    int c = line.StartCol + dc * step;
                    int r = line.StartRow + dr * step;
                    CellData cell = grid.GetCell(c, r);

                    if (cell != null &&
                        char.ToUpper(cell.Letter) == char.ToUpper(targetWord[step]))
                    {
                        isGreen[c, r] = true;
                    }
                }
            }

            // --- Phase 2: Build target letter set for yellow check ---
            HashSet<char> targetLetters = new HashSet<char>();
            for (int i = 0; i < targetWord.Length; i++)
                targetLetters.Add(char.ToUpper(targetWord[i]));

            // --- Phase 3: Apply colours to all tiles ---
            for (int col = 0; col < GridManager.COLS; col++)
            {
                for (int row = 0; row < GridManager.ROWS; row++)
                {
                    CellData cell = grid.GetCell(col, row);
                    if (cell == null) continue;

                    Tile tile = grid.GetTile(col, row);
                    if (tile == null) continue;

                    if (isGreen[col, row])
                    {
                        tile.SetColorState(TileColorState.Green);
                    }
                    else if (targetLetters.Contains(char.ToUpper(cell.Letter)))
                    {
                        tile.SetColorState(TileColorState.Yellow);
                    }
                    else
                    {
                        tile.SetColorState(TileColorState.Gray);
                    }
                }
            }

            // --- Phase 4: Notify HUD to update target word blanks ---
            if (HUDManager.Instance != null)
                HUDManager.Instance.UpdateTargetDisplay();
        }

        /// <summary>
        /// Checks if any line of 4 tiles spells the target word in order.
        /// Returns true if a winning line is found.
        /// </summary>
        public bool CheckForWin()
        {
            GridManager grid = GridManager.Instance;
            if (grid == null) return false;

            string targetWord = RoundManager.Instance != null
                ? RoundManager.Instance.TargetWord
                : "";

            if (string.IsNullOrEmpty(targetWord) || targetWord.Length != 4)
                return false;

            for (int i = 0; i < _allLines.Count; i++)
            {
                LineSpec line = _allLines[i];
                int dc = _directions[line.DirIndex][0];
                int dr = _directions[line.DirIndex][1];

                bool match = true;
                for (int step = 0; step < 4; step++)
                {
                    int c = line.StartCol + dc * step;
                    int r = line.StartRow + dr * step;
                    CellData cell = grid.GetCell(c, r);

                    if (cell == null ||
                        char.ToUpper(cell.Letter) != char.ToUpper(targetWord[step]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    Debug.Log($"[WordleEvaluator] WIN detected! Line starting at " +
                              $"({line.StartCol},{line.StartRow}) dir={line.DirIndex} " +
                              $"spells '{targetWord}'");
                    return true;
                }
            }

            return false;
        }

        // -----------------------------------------------------------------------
        // Internal types
        // -----------------------------------------------------------------------

        private struct LineSpec
        {
            public int StartCol;
            public int StartRow;
            public int DirIndex;
        }
    }

    /// <summary>
    /// The Wordle colour state of a tile.
    /// </summary>
    public enum TileColorState
    {
        None,
        Green,
        Yellow,
        Gray
    }
}
