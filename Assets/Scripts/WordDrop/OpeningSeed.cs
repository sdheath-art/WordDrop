using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Rich procedural opening boards for Survival mode.
    ///
    /// Goal: the first 20-30 seconds should create:
    ///   - first word quickly
    ///   - first prime quickly
    ///   - first "oh I get this" moment
    ///   - first pressure signal (rising row)
    ///   - ideally, a believable early mini-payoff
    ///
    /// Generates 2-row openings (8-10 tiles with gaps) that contain:
    ///   - At least 1 pre-primed word (glowing, ready to detonate)
    ///   - At least 2 near-words completable with the player's hand
    ///   - Strategic gaps for drop targets
    ///   - Vowel/consonant balance
    ///
    /// NOT canned — procedurally varied each run. Same emotional arc,
    /// different exact letters every time.
    ///
    /// Classic/Daily/Blitz modes use the simpler legacy seeds.
    /// </summary>
    public static class OpeningSeed
    {
        public static bool Enabled = true;

        // First-level board: deterministic instead of random, so L1 is the SAME rich tutorial
        // board every run (board-only scoring → hand-independent). Reroll FirstBoardSeed to
        // audition different fixed boards; once we like one it's locked. 2026-06-09 Spencer.
        // Hand-authored exact placement is the upgrade path if we want full per-tile control.
        public static bool UseFixedFirstBoard = false; // OFF while testing loop feel — fresh random board each run.
        public static int  FirstBoardSeed     = 7;   // only used if FixedFirstBoardLayout is null

        // Hand-authored L1 board (top row first, '_' = empty). Dense, common, vowel-balanced
        // letters → many word possibilities for the tutorial. Gravity-stable: every column is
        // filled contiguously from the bottom. 6 cols × up to 4 rows. Edit freely to retune.
        // Rich seams here: CAR/ROT/ROTS verticals, CAR(E)(S)(S)/RAT(E)(S) horizontals. 2026-06-09.
        public static string[] FixedFirstBoardLayout = new[]
        {
            "__ST__",
            "RATEST",
            "ANONED",
            "CARESS",
        };

        // ── Placement ───────────────────────────────────────────────────────────

        public struct Placement
        {
            public int Col;
            public int Row;
            public char Letter;

            public Placement(int col, int row, char letter)
            {
                Col = col;
                Row = row;
                Letter = char.ToUpper(letter);
            }
        }

        public struct Seed
        {
            public string Name;
            public Placement[] Placements;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static char PickOpeningLetter(System.Random rng, HashSet<char> used, ref int vowelCount)
        {
            char letter;
            if (vowelCount < 4 || rng.Next(0, 100) < 30)
            {
                letter = VOWELS[rng.Next(0, VOWELS.Length)];
                vowelCount++;
            }
            else if (rng.Next(0, 100) < 70)
            {
                letter = COMMON_CONSONANTS[rng.Next(0, COMMON_CONSONANTS.Length)];
            }
            else
            {
                letter = LESS_COMMON[rng.Next(0, LESS_COMMON.Length)];
            }

            // Try to avoid duplicates (soft — allows after 10 retries)
            int retry = 0;
            while (used.Contains(letter) && retry < 10)
            {
                if (rng.Next(0, 100) < 30)
                    letter = VOWELS[rng.Next(0, VOWELS.Length)];
                else
                    letter = COMMON_CONSONANTS[rng.Next(0, COMMON_CONSONANTS.Length)];
                retry++;
            }
            used.Add(letter);
            return letter;
        }

        private static void ShuffleList(List<int> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(0, i + 1);
                int tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        // ── Letter pools ────────────────────────────────────────────────────────

        private static readonly char[] VOWELS = { 'A', 'E', 'I', 'O', 'U' };
        private static readonly char[] COMMON_CONSONANTS = {
            'R', 'S', 'T', 'N', 'L', 'D', 'H', 'C', 'M', 'P'
        };
        private static readonly char[] LESS_COMMON = {
            'B', 'F', 'G', 'W', 'K', 'Y'
        };

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Generates a rich opening board. Survival gets a 2-row fertile start.
        /// Other modes get the simpler legacy seeds.
        /// </summary>
        public static string ApplyRandomSeed(RulesEngine rules)
        {
            if (!Enabled || rules == null) return null;

            if (SurvivalManager.IsSurvivalMode)
                return ApplySurvivalOpening(rules);

            return ApplyLegacySeed(rules);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // SURVIVAL OPENING — Rich 2-row board with pre-primed word
        // ═══════════════════════════════════════════════════════════════════════

        private const int NUM_CANDIDATES = 10;
        private const int OPENING_ROWS = 4;    // how many rows to pre-fill
        private const int OPENING_TILES = 20;  // target tiles across 4 rows

        private static string ApplySurvivalOpening(RulesEngine rules)
        {
            // Read the player's hand so we can ensure hand+board synergy. For the fixed L1 board
            // we deliberately IGNORE the hand (null) so the chosen board is fully deterministic —
            // otherwise the random hand would sway candidate selection and the board would vary.
            char[] hand = null;
            if (!UseFixedFirstBoard && MatchController.Instance != null)
            {
                var playerHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (playerHand != null)
                    hand = playerHand.GetAllSlots();
            }

            System.Random rng;
            if (UseFixedFirstBoard)
                rng = new System.Random(FirstBoardSeed);      // deterministic L1 tutorial board
            else if (DailyDropManager.IsDailyMode)
                rng = new System.Random(DailyDropManager.GetDailySeed() + 4219);
            else
                rng = new System.Random(System.Environment.TickCount);

            char[,] bestBoard = null;
            int bestScore = -999;
            string bestName = "Opening";

            if (UseFixedFirstBoard && FixedFirstBoardLayout != null && FixedFirstBoardLayout.Length > 0)
            {
                // Hand-authored L1 board — dense common letters for many word possibilities,
                // beats gambling on a generator seed for the tutorial. 2026-06-09.
                bestBoard = BuildBoardFromLayout(FixedFirstBoardLayout);
                bestScore = ScoreOpening(bestBoard, null, rules);
                bestName  = "FixedAuthoredL1";
            }
            else
            {
                // Generate candidates and pick the best
                for (int attempt = 0; attempt < NUM_CANDIDATES; attempt++)
                {
                    char[,] candidate = GenerateOpeningBoard(rng);
                    int score = ScoreOpening(candidate, hand, rules);

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBoard = candidate;
                        bestName = $"SurvOpen_{attempt}";
                    }
                }
            }

            if (bestBoard == null) return null;

            // Place tiles on the rules board
            int placed = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                for (int row = 0; row < OPENING_ROWS; row++)
                {
                    char letter = bestBoard[col, row];
                    if (letter == '\0') continue;

                    rules.SetCell(col, row, new RulesCellData
                    {
                        Letter = letter,
                        Col = col,
                        Row = row,
                        PlayerIndex = -1,
                    });
                    placed++;
                }
            }

            string primedWord = null;

            // Log the opening
            var rowStrs = new string[OPENING_ROWS];
            for (int row = 0; row < OPENING_ROWS; row++)
            {
                rowStrs[row] = "";
                for (int c = 0; c < RulesEngine.COLS; c++)
                    rowStrs[row] += bestBoard[c, row] != '\0' ? bestBoard[c, row].ToString() : "_";
            }
            string logRows = "";
            for (int row = OPENING_ROWS - 1; row >= 0; row--)
                logRows += $"  Row {row}: {rowStrs[row]}\n";
            Debug.Log($"[OpeningSeed] Opening (fixed={UseFixedFirstBoard} seed={FirstBoardSeed} score={bestScore} placed={placed}):\n{logRows}" +
                      $"  Primed: {(primedWord ?? "none")}");

            return bestName;
        }

        /// <summary>Builds a board grid from an authored layout (rows top→bottom, '_' = empty).
        /// layout[0] is the TOP row; mapped to board[col, OPENING_ROWS-1] down to row 0. 2026-06-09.</summary>
        private static char[,] BuildBoardFromLayout(string[] rowsTopDown)
        {
            char[,] board = new char[RulesEngine.COLS, OPENING_ROWS];
            int rowCount = Mathf.Min(rowsTopDown.Length, OPENING_ROWS);
            for (int r = 0; r < rowCount; r++)
            {
                int boardRow = OPENING_ROWS - 1 - r; // first layout line = top row
                string line = rowsTopDown[r] ?? "";
                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    char c = col < line.Length ? line[col] : '_';
                    board[col, boardRow] = (c == '_' || c == ' ') ? '\0' : char.ToUpper(c);
                }
            }
            return board;
        }

        /// <summary>
        /// Generates a 2-row opening board with 8-10 tiles and 3-5 gaps.
        /// Ensures vowel balance, variety, and structural opportunity.
        /// </summary>
        private static char[,] GenerateOpeningBoard(System.Random rng)
        {
            char[,] board = new char[RulesEngine.COLS, OPENING_ROWS];
            var used = new HashSet<char>();
            int vowelCount = 0;

            // Row 0 (bottom): full or near-full — 5-7 tiles
            int row0Gaps = 1 + rng.Next(0, 2); // 1 or 2 gaps
            var gapCols0 = new HashSet<int>();
            while (gapCols0.Count < row0Gaps)
                gapCols0.Add(rng.Next(0, RulesEngine.COLS));

            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                if (gapCols0.Contains(col)) continue;
                board[col, 0] = PickOpeningLetter(rng, used, ref vowelCount);
            }

            // Row 1: 4-6 tiles (stacked on row 0)
            int row1Gaps = 1 + rng.Next(0, 2);
            var gapCols1 = new HashSet<int>();
            while (gapCols1.Count < row1Gaps)
            {
                int c = rng.Next(0, RulesEngine.COLS);
                if (board[c, 0] != '\0') gapCols1.Add(c); // only gap where row 0 has a tile
            }

            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                if (board[col, 0] == '\0') continue; // must stack on row 0
                if (gapCols1.Contains(col)) continue;
                board[col, 1] = PickOpeningLetter(rng, used, ref vowelCount);
            }

            // Row 2: sparser — 3-5 tiles
            int row2Count = 3 + rng.Next(0, 3);
            var row2Candidates = new List<int>();
            for (int col = 0; col < RulesEngine.COLS; col++)
                if (board[col, 1] != '\0') row2Candidates.Add(col);
            ShuffleList(row2Candidates, rng);

            for (int i = 0; i < Mathf.Min(row2Count, row2Candidates.Count); i++)
                board[row2Candidates[i], 2] = PickOpeningLetter(rng, used, ref vowelCount);

            // Row 3: very sparse — 1-3 tiles
            int row3Count = 1 + rng.Next(0, 3);
            var row3Candidates = new List<int>();
            for (int col = 0; col < RulesEngine.COLS; col++)
                if (board[col, 2] != '\0') row3Candidates.Add(col);
            ShuffleList(row3Candidates, rng);

            for (int i = 0; i < Mathf.Min(row3Count, row3Candidates.Count); i++)
                board[row3Candidates[i], 3] = PickOpeningLetter(rng, used, ref vowelCount);

            return board;
        }

        /// <summary>
        /// Scores an opening board by how many real opportunities it creates
        /// in combination with the player's hand.
        ///
        /// High scores = immediate words possible, near-words visible, fertile layout.
        /// </summary>
        private static int ScoreOpening(char[,] board, char[] hand, RulesEngine rules)
        {
            int score = 0;

            // 1. Check horizontal words already on the board (3-letter)
            //    These will get auto-primed — exciting!
            int wordsOnBoard = 0;
            for (int row = 0; row < OPENING_ROWS; row++)
            {
                for (int startCol = 0; startCol <= RulesEngine.COLS - 3; startCol++)
                {
                    for (int len = 3; len <= Mathf.Min(5, RulesEngine.COLS - startCol); len++)
                    {
                        bool valid = true;
                        var sb = new System.Text.StringBuilder(len);
                        for (int c = startCol; c < startCol + len; c++)
                        {
                            if (board[c, row] == '\0') { valid = false; break; }
                            sb.Append(board[c, row]);
                        }
                        if (valid && WordDictionary.IsValidWord(sb.ToString()))
                        {
                            wordsOnBoard++;
                            score += 8; // great — a word to pre-prime
                        }
                    }
                }
            }

            // Cap: 1 pre-primed word is ideal, 2 is fine, more is too easy
            if (wordsOnBoard > 2) score -= (wordsOnBoard - 2) * 4;

            // 2. Check near-words completable with hand letters
            //    (one gap in a horizontal run, hand has the letter to fill it)
            if (hand != null)
            {
                var handSet = new HashSet<char>();
                for (int i = 0; i < hand.Length; i++)
                    if (hand[i] != '\0') handSet.Add(char.ToUpper(hand[i]));

                int nearWords = 0;
                for (int row = 0; row < OPENING_ROWS; row++)
                {
                    for (int col = 0; col < RulesEngine.COLS; col++)
                    {
                        if (board[col, row] != '\0') continue; // occupied
                        // Check if dropping any hand letter here creates a word
                        foreach (char h in handSet)
                        {
                            if (CheckHorizontalWordAt(board, col, row, h))
                            {
                                nearWords++;
                                score += 5; // hand + board synergy
                                break; // one per gap
                            }
                        }
                    }
                }

                // Also check: can the hand drop into a gap column on row 0?
                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    if (board[col, 0] == '\0') // gap column
                    {
                        // Check if any hand letter + adjacent board tiles = word
                        foreach (char h in handSet)
                        {
                            if (CheckHorizontalWordAt(board, col, 0, h) ||
                                CheckVerticalWordAt(board, col, 0, h))
                            {
                                score += 6; // gap is a viable drop target — great
                                break;
                            }
                        }
                    }
                }
            }

            // 3. Vowel balance
            int vowels = 0, total = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                for (int row = 0; row < OPENING_ROWS; row++)
                {
                    if (board[col, row] == '\0') continue;
                    total++;
                    char c = char.ToUpper(board[col, row]);
                    if (c == 'A' || c == 'E' || c == 'I' || c == 'O' || c == 'U')
                        vowels++;
                }
            }
            if (vowels >= 2 && vowels <= 4) score += 3;
            else score -= 2;

            // 4. Tile count (prefer 8-10)
            if (total >= 8 && total <= 10) score += 2;
            else if (total < 7) score -= 3;

            // 5. Gap count (prefer 2-3 gaps in row 0 for drop targets)
            int gaps = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
                if (board[col, 0] == '\0') gaps++;
            if (gaps >= 2 && gaps <= 3) score += 2;
            else if (gaps < 2) score -= 3; // too packed

            // 6. Penalize if NO words on board (nothing to prime)
            if (wordsOnBoard == 0) score -= 5;

            return score;
        }

        /// <summary>
        /// Check if placing 'letter' at (col, row) in the opening board creates
        /// a valid horizontal word of length 3+.
        /// </summary>
        private static bool CheckHorizontalWordAt(char[,] board, int col, int row, char letter)
        {
            int left = col, right = col;
            while (left > 0 && board[left - 1, row] != '\0') left--;
            while (right < RulesEngine.COLS - 1 && board[right + 1, row] != '\0') right++;

            int runLen = right - left + 1;
            if (runLen < 3) return false;

            var sb = new System.Text.StringBuilder(runLen);
            for (int c = left; c <= right; c++)
                sb.Append(c == col ? letter : board[c, row]);

            string full = sb.ToString();
            int idx = col - left;

            for (int start = 0; start < full.Length; start++)
            {
                for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                {
                    if (idx >= start && idx < start + len)
                    {
                        if (WordDictionary.IsValidWord(full.Substring(start, len)))
                            return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if placing 'letter' at (col, row) creates a valid vertical word.
        /// </summary>
        private static bool CheckVerticalWordAt(char[,] board, int col, int row, char letter)
        {
            // Opening is only 2 rows — vertical words need both rows + the new letter
            if (row == 0 && board[col, 1] != '\0')
            {
                // Letter at row 0, tile at row 1: 2-letter vertical, needs 3+
                // Can't form a 3-letter vertical in a 2-row opening
                return false;
            }
            return false; // Vertical words unlikely in 2-row opener
        }

        /// <summary>
        /// After placing the opening board, scan for valid words and pre-prime one.
        /// This gives the player an immediate glowing target to detonate.
        /// </summary>
        private static string TryPrimeOneWord(RulesEngine rules)
        {
            if (rules == null || rules.PrimedRegistry == null) return null;

            // Scan the board for valid words
            var words = rules.ScanEntireBoardPublic();
            if (words == null || words.Count == 0) return null;

            // Pick the shortest word (easiest to understand as the first prime)
            RulesWordMatch best = null;
            for (int i = 0; i < words.Count; i++)
            {
                if (best == null || words[i].Word.Length < best.Word.Length)
                    best = words[i];
            }

            if (best == null) return null;

            // Prime it with a generous fuse (8 turns — plenty of time)
            int score = RulesEngine.CalculateWordScore(best.Word);
            int fuse = 8;
            rules.PrimedRegistry.AddPrimedWord(
                best.Word, best.Cells,
                -1, // neutral owner
                0,  // primed on turn 0
                fuse, // expires on turn 8
                score
            );

            // Mark as scored so it doesn't get re-detected
            rules.RegisterScoredKey(best.Word + "|" + best.CellKey);

//             Debug.Log($"[OpeningSeed] Pre-primed '{best.Word}' at {best.CellKey} (score={score}, fuse={fuse})");

            return best.Word;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // LEGACY SEEDS — for Classic, Daily, Blitz
        // ═══════════════════════════════════════════════════════════════════════

        private static readonly Seed[] _legacySeeds = new Seed[]
        {
            new Seed { Name = "Anchors", Placements = new[] {
                new Placement(1, 0, 'R'), new Placement(5, 0, 'E') }},
            new Seed { Name = "Posts", Placements = new[] {
                new Placement(2, 0, 'T'), new Placement(5, 0, 'A') }},
            new Seed { Name = "Scatter", Placements = new[] {
                new Placement(1, 0, 'E'), new Placement(3, 0, 'T'), new Placement(5, 0, 'A') }},
            new Seed { Name = "Triangle", Placements = new[] {
                new Placement(1, 0, 'L'), new Placement(5, 0, 'R'), new Placement(3, 1, 'E') }},
            new Seed { Name = "Diamond", Placements = new[] {
                new Placement(1, 0, 'R'), new Placement(5, 0, 'N'),
                new Placement(2, 1, 'E'), new Placement(4, 1, 'A') }},
            new Seed { Name = "Wings", Placements = new[] {
                new Placement(0, 0, 'A'), new Placement(2, 0, 'T'),
                new Placement(4, 0, 'E'), new Placement(6, 0, 'R') }},
        };

        private static string ApplyLegacySeed(RulesEngine rules)
        {
            // Pick a random legacy seed
            var rng = DailyDropManager.IsDailyMode
                ? new System.Random(DailyDropManager.GetDailySeed() + 7919)
                : new System.Random(System.Environment.TickCount);

            int pick = rng.Next(0, _legacySeeds.Length);
            Seed chosen = _legacySeeds[pick];

            for (int i = 0; i < chosen.Placements.Length; i++)
            {
                var p = chosen.Placements[i];
                rules.SetCell(p.Col, p.Row, new RulesCellData
                {
                    Letter = p.Letter,
                    Col = p.Col,
                    Row = p.Row,
                    PlayerIndex = -1,
                });
            }

//             Debug.Log($"[OpeningSeed] Legacy seed '{chosen.Name}'");
            return chosen.Name;
        }
    }
}
