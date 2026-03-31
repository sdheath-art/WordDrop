using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Board-aware drought assist system.
    ///
    /// Instead of blindly forcing random consonants, this scans the actual board
    /// for near-complete words and suggests letters that would help form them.
    ///
    /// How it works:
    ///   1. Scans every row and column for runs of 2+ consecutive letters
    ///   2. For each run, tests what single letter (placed adjacent) would
    ///      form a valid dictionary word
    ///   3. Collects all "helper" letters into a weighted pool
    ///   4. During drought, draws from that pool instead of random
    ///
    /// This creates opportunity without scripting outcomes — the player still
    /// has to recognize and play the word.
    ///
    /// Design: plain static utility class, no MonoBehaviour needed.
    /// </summary>
    public static class DroughtAssist
    {
        // ── Tuning ──────────────────────────────────────────────────────────────

        /// <summary>Max helper letters to collect per scan (perf cap).</summary>
        private const int MAX_HELPERS = 30;

        /// <summary>Min run length to consider (2 = pairs, which need 1 more for a 3-letter word).</summary>
        private const int MIN_RUN_LENGTH = 2;

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Scans the board and returns a letter that would help complete a word.
        /// Returns '\0' if no good helper found (caller should fall back to normal draw).
        /// </summary>
        public static char GetHelperLetter()
        {
            if (RulesEngine.Instance == null) return '\0';

            List<char> helpers = CollectHelperLetters();

            if (helpers.Count == 0) return '\0';

            // Pick randomly from the helper pool
            char picked = helpers[Random.Range(0, helpers.Count)];
            Debug.Log($"[DroughtAssist] Picked helper '{picked}' from {helpers.Count} candidates");
            return picked;
        }

        /// <summary>
        /// Returns true if the board has any near-complete word opportunities.
        /// Useful for deciding whether to use board-aware assist or fall back to generic.
        /// </summary>
        public static bool HasOpportunities()
        {
            if (RulesEngine.Instance == null) return false;
            List<char> helpers = CollectHelperLetters();
            return helpers.Count > 0;
        }

        // ── Board scanning ──────────────────────────────────────────────────────

        private static List<char> CollectHelperLetters()
        {
            var helpers = new List<char>();
            var board = RulesEngine.Instance;
            if (board == null) return helpers;

            int cols = GridManager.COLS;
            int rows = GridManager.ROWS;

            // Scan horizontal runs (left to right)
            for (int row = 0; row < rows; row++)
            {
                ScanLine(board, 0, row, 1, 0, cols, helpers);
            }

            // Scan vertical runs (top to bottom)
            for (int col = 0; col < cols; col++)
            {
                ScanLine(board, col, rows - 1, 0, -1, rows, helpers);
            }

            return helpers;
        }

        /// <summary>
        /// Scans a single line (row or column) for runs of letters,
        /// then checks what letter could extend each run into a valid word.
        /// </summary>
        private static void ScanLine(
            RulesEngine board,
            int startCol, int startRow,
            int dCol, int dRow,
            int maxSteps,
            List<char> helpers)
        {
            if (helpers.Count >= MAX_HELPERS) return;

            // Collect all letters along this line
            var letters = new List<(int col, int row, char letter)>();
            int c = startCol, r = startRow;
            for (int step = 0; step < maxSteps; step++)
            {
                if (c < 0 || c >= GridManager.COLS || r < 0 || r >= GridManager.ROWS) break;
                var cell = board.GetCell(c, r);
                char ch = (cell != null) ? char.ToUpper(cell.Letter) : '\0';
                letters.Add((c, r, ch));
                c += dCol;
                r += dRow;
            }

            // Find runs of 2+ consecutive occupied cells
            int runStart = -1;
            for (int i = 0; i <= letters.Count; i++)
            {
                bool occupied = (i < letters.Count && letters[i].letter != '\0');

                if (occupied && runStart < 0)
                {
                    runStart = i;
                }
                else if (!occupied && runStart >= 0)
                {
                    int runLen = i - runStart;
                    if (runLen >= MIN_RUN_LENGTH)
                    {
                        // Extract the run's letters
                        string run = "";
                        for (int j = runStart; j < i; j++)
                            run += letters[j].letter;

                        // Check what letter before or after the run would make a word
                        TryExtendRun(run, runStart, i - 1, letters, dCol, dRow, helpers);
                    }
                    runStart = -1;
                }
            }
        }

        /// <summary>
        /// For a run of letters, checks if adding one letter before or after
        /// creates a valid dictionary word.
        /// </summary>
        private static void TryExtendRun(
            string run,
            int runStartIdx, int runEndIdx,
            List<(int col, int row, char letter)> line,
            int dCol, int dRow,
            List<char> helpers)
        {
            if (helpers.Count >= MAX_HELPERS) return;

            // Check if position BEFORE the run is empty (could place a letter there)
            bool canExtendBefore = (runStartIdx > 0 && line[runStartIdx - 1].letter == '\0');
            // Check if position AFTER the run is empty
            bool canExtendAfter = (runEndIdx < line.Count - 1 && line[runEndIdx + 1].letter == '\0');

            // Also check: for vertical lines, extending means the column must have space
            // For horizontal, the adjacent cell must be reachable (gravity means it needs support)

            // Try each letter A-Z
            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                if (helpers.Count >= MAX_HELPERS) return;

                // Try prepending
                if (canExtendBefore)
                {
                    string candidate = ch + run;
                    if (candidate.Length >= 3 && candidate.Length <= 7 &&
                        WordDictionary.IsValidWord(candidate))
                    {
                        helpers.Add(ch);
                        continue; // don't double-add
                    }
                }

                // Try appending
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
    }
}
