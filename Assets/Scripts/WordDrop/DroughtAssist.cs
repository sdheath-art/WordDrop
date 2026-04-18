using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Board-aware draw assist — gives the player letters that create real opportunities.
    ///
    /// Three scanning strategies (all contribute to a weighted helper pool):
    ///   1. Column Drop Scan: for each column, simulate dropping each letter A-Z.
    ///      Check if it creates a valid word horizontally or vertically with neighbors.
    ///      This is the PRIMARY source — it finds what would actually help the player.
    ///   2. Run Extension: scans existing 2+ letter runs and finds completions.
    ///   3. Gap Fill: finds X_Y patterns where one letter creates a word.
    ///
    /// The player still has to FIND and PLACE the word — the skill is intact.
    /// </summary>
    public static class DroughtAssist
    {
        // ── Tuning ──────────────────────────────────────────────────────────────

        private const int MAX_HELPERS = 40;
        private const int MIN_RUN_LENGTH = 2;

        // ── Public API ──────────────────────────────────────────────────────────

        public static char GetHelperLetter()
        {
            if (RulesEngine.Instance == null) return '\0';

            List<char> helpers = CollectHelperLetters();
            if (helpers.Count == 0) return '\0';

            char picked = helpers[Random.Range(0, helpers.Count)];
            return picked;
        }

        public static bool HasOpportunities()
        {
            if (RulesEngine.Instance == null) return false;
            return CollectHelperLetters().Count > 0;
        }

        // ── Core collection ─────────────────────────────────────────────────────

        private static List<char> CollectHelperLetters()
        {
            var helpers = new List<char>();
            var board = RulesEngine.Instance;
            if (board == null) return helpers;

            int cols = GridManager.COLS;
            int rows = GridManager.ROWS;

            // === STRATEGY 1: Column Drop Scan (highest value) ===
            // For each column, simulate dropping each letter and check if it
            // forms a word with adjacent tiles. Triple-weighted because these
            // are the moves the player can actually make.
            ScanColumnDrops(board, helpers);

            // === STRATEGY 2: Run extension ===
            for (int row = 0; row < rows; row++)
                ScanLine(board, 0, row, 1, 0, cols, helpers);
            for (int col = 0; col < cols; col++)
                ScanLine(board, col, rows - 1, 0, -1, rows, helpers);

            // === STRATEGY 3: Gap fills ===
            ScanGapPatterns(board, helpers);

            return helpers;
        }

        // ── Strategy 1: Column Drop Scan ────────────────────────────────────────

        /// <summary>
        /// For each column, find the landing row, then check what letters A-Z
        /// would form a horizontal or vertical word if dropped there.
        /// These are triple-weighted because they represent actual playable moves.
        /// </summary>
        private static void ScanColumnDrops(RulesEngine board, List<char> helpers)
        {
            int cols = GridManager.COLS;
            int rows = GridManager.ROWS;

            for (int col = 0; col < cols; col++)
            {
                if (helpers.Count >= MAX_HELPERS) return;

                // Find landing row (lowest empty row in this column)
                int landRow = -1;
                for (int row = 0; row < rows; row++)
                {
                    if (board.GetCell(col, row) == null)
                    {
                        landRow = row;
                        break;
                    }
                }
                if (landRow < 0) continue; // column full

                // Collect horizontal neighbors at landRow
                var hLetters = new List<(int col, char letter)>();
                for (int c = Mathf.Max(0, col - 6); c <= Mathf.Min(cols - 1, col + 6); c++)
                {
                    var cell = board.GetCell(c, landRow);
                    if (cell != null && !cell.IsStone)
                        hLetters.Add((c, char.ToUpper(cell.Letter)));
                }

                // Collect vertical neighbors at col (below landRow — tiles above don't exist yet)
                var vLetters = new List<(int row, char letter)>();
                for (int r = 0; r < rows; r++)
                {
                    if (r == landRow) continue;
                    var cell = board.GetCell(col, r);
                    if (cell != null && !cell.IsStone)
                        vLetters.Add((r, char.ToUpper(cell.Letter)));
                }

                // Try each letter A-Z as a hypothetical drop
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    if (helpers.Count >= MAX_HELPERS) return;

                    bool found = false;

                    // Check horizontal words (consecutive tiles including the dropped one)
                    found = found || CheckHorizontalWord(board, col, landRow, ch, cols);

                    // Check vertical words (tiles below + dropped tile)
                    found = found || CheckVerticalWord(board, col, landRow, ch, rows);

                    if (found)
                    {
                        // EXPERIMENT: reduced from 3x to 1x — revert if draws feel too random
                        helpers.Add(ch);
                    }
                }
            }
        }

        /// <summary>
        /// Check if placing 'ch' at (col, row) creates a valid horizontal word
        /// by reading the consecutive run of letters through that position.
        /// </summary>
        private static bool CheckHorizontalWord(RulesEngine board, int col, int row, char ch, int cols)
        {
            // Find the leftmost extent of the run through (col, row)
            int left = col;
            while (left > 0 && board.GetCell(left - 1, row) != null && !board.GetCell(left - 1, row).IsStone)
                left--;

            // Find the rightmost extent
            int right = col;
            while (right < cols - 1 && board.GetCell(right + 1, row) != null && !board.GetCell(right + 1, row).IsStone)
                right++;

            int totalLen = right - left + 1;
            if (totalLen < 3) return false;

            // Build the string with ch inserted at col
            var sb = new System.Text.StringBuilder(totalLen);
            for (int c = left; c <= right; c++)
            {
                if (c == col)
                    sb.Append(ch);
                else
                {
                    var cell = board.GetCell(c, row);
                    if (cell == null || cell.IsStone) return false; // gap — not a valid run
                    sb.Append(char.ToUpper(cell.Letter));
                }
            }

            // Check all substrings of length 3-7 that include position col
            string full = sb.ToString();
            int colIdx = col - left; // index of our letter in the string

            for (int start = 0; start < full.Length; start++)
            {
                for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                {
                    int end = start + len - 1;
                    if (colIdx >= start && colIdx <= end)
                    {
                        string candidate = full.Substring(start, len);
                        if (WordDictionary.IsValidWord(candidate))
                            return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Check if placing 'ch' at (col, row) creates a valid vertical word
        /// by reading the consecutive run of letters through that position.
        /// </summary>
        private static bool CheckVerticalWord(RulesEngine board, int col, int row, char ch, int rows)
        {
            // Find the bottom extent (below row)
            int bottom = row;
            while (bottom > 0 && board.GetCell(col, bottom - 1) != null && !board.GetCell(col, bottom - 1).IsStone)
                bottom--;

            // Find the top extent (above row)
            int top = row;
            while (top < rows - 1 && board.GetCell(col, top + 1) != null && !board.GetCell(col, top + 1).IsStone)
                top++;

            int totalLen = top - bottom + 1;
            if (totalLen < 3) return false;

            // Build the string top-to-bottom (how words read vertically)
            var sb = new System.Text.StringBuilder(totalLen);
            for (int r = top; r >= bottom; r--)
            {
                if (r == row)
                    sb.Append(ch);
                else
                {
                    var cell = board.GetCell(col, r);
                    if (cell == null || cell.IsStone) return false;
                    sb.Append(char.ToUpper(cell.Letter));
                }
            }

            string full = sb.ToString();
            int rowIdx = top - row; // index of our letter in the string

            for (int start = 0; start < full.Length; start++)
            {
                for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                {
                    int end = start + len - 1;
                    if (rowIdx >= start && rowIdx <= end)
                    {
                        string candidate = full.Substring(start, len);
                        if (WordDictionary.IsValidWord(candidate))
                            return true;
                    }
                }
            }

            return false;
        }

        // ── Strategy 2: Run extension ───────────────────────────────────────────

        private static void ScanLine(
            RulesEngine board,
            int startCol, int startRow,
            int dCol, int dRow,
            int maxSteps,
            List<char> helpers)
        {
            if (helpers.Count >= MAX_HELPERS) return;

            var letters = new List<(int col, int row, char letter)>();
            int c = startCol, r = startRow;
            for (int step = 0; step < maxSteps; step++)
            {
                if (c < 0 || c >= GridManager.COLS || r < 0 || r >= GridManager.ROWS) break;
                var cell = board.GetCell(c, r);
                char ch = (cell != null && !cell.IsStone) ? char.ToUpper(cell.Letter) : '\0';
                letters.Add((c, r, ch));
                c += dCol;
                r += dRow;
            }

            int runStart = -1;
            for (int i = 0; i <= letters.Count; i++)
            {
                bool occupied = (i < letters.Count && letters[i].letter != '\0');

                if (occupied && runStart < 0)
                    runStart = i;
                else if (!occupied && runStart >= 0)
                {
                    int runLen = i - runStart;
                    if (runLen >= MIN_RUN_LENGTH)
                    {
                        string run = "";
                        for (int j = runStart; j < i; j++)
                            run += letters[j].letter;
                        TryExtendRun(run, runStart, i - 1, letters, helpers);
                    }
                    runStart = -1;
                }
            }
        }

        private static void TryExtendRun(
            string run,
            int runStartIdx, int runEndIdx,
            List<(int col, int row, char letter)> line,
            List<char> helpers)
        {
            if (helpers.Count >= MAX_HELPERS) return;

            bool canExtendBefore = (runStartIdx > 0 && line[runStartIdx - 1].letter == '\0');
            bool canExtendAfter = (runEndIdx < line.Count - 1 && line[runEndIdx + 1].letter == '\0');

            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                if (helpers.Count >= MAX_HELPERS) return;

                if (canExtendBefore)
                {
                    string candidate = ch + run;
                    if (candidate.Length >= 3 && candidate.Length <= 7 &&
                        WordDictionary.IsValidWord(candidate))
                    {
                        helpers.Add(ch);
                        continue;
                    }
                }

                if (canExtendAfter)
                {
                    string candidate = run + ch;
                    if (candidate.Length >= 3 && candidate.Length <= 7 &&
                        WordDictionary.IsValidWord(candidate))
                    {
                        helpers.Add(ch);
                    }
                }
            }
        }

        // ── Strategy 3: Gap fills ───────────────────────────────────────────────

        private static void ScanGapPatterns(RulesEngine board, List<char> helpers)
        {
            if (helpers.Count >= MAX_HELPERS) return;

            int cols = GridManager.COLS;
            int rows = GridManager.ROWS;

            // Horizontal gaps
            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col <= cols - 3; col++)
                {
                    if (helpers.Count >= MAX_HELPERS) return;

                    var left = board.GetCell(col, row);
                    var mid = board.GetCell(col + 1, row);
                    var right = board.GetCell(col + 2, row);

                    if (left != null && !left.IsStone && mid == null && right != null && !right.IsStone)
                    {
                        for (char ch = 'A'; ch <= 'Z'; ch++)
                        {
                            if (helpers.Count >= MAX_HELPERS) return;
                            string candidate = "" + char.ToUpper(left.Letter) + ch + char.ToUpper(right.Letter);
                            if (WordDictionary.IsValidWord(candidate))
                            {
                                helpers.Add(ch);
                                helpers.Add(ch); // double-weight gap fills
                            }
                        }
                    }
                }
            }

            // Vertical gaps
            for (int col = 0; col < cols; col++)
            {
                for (int row = 0; row <= rows - 3; row++)
                {
                    if (helpers.Count >= MAX_HELPERS) return;

                    var bottom = board.GetCell(col, row);
                    var mid = board.GetCell(col, row + 1);
                    var top = board.GetCell(col, row + 2);

                    if (bottom != null && !bottom.IsStone && mid == null && top != null && !top.IsStone)
                    {
                        for (char ch = 'A'; ch <= 'Z'; ch++)
                        {
                            if (helpers.Count >= MAX_HELPERS) return;
                            string topDown = "" + char.ToUpper(top.Letter) + ch + char.ToUpper(bottom.Letter);
                            string bottomUp = "" + char.ToUpper(bottom.Letter) + ch + char.ToUpper(top.Letter);
                            if (WordDictionary.IsValidWord(topDown) || WordDictionary.IsValidWord(bottomUp))
                            {
                                helpers.Add(ch);
                                helpers.Add(ch);
                            }
                        }
                    }
                }
            }
        }
    }
}
