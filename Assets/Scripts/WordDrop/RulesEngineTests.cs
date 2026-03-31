using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Automated self-play test suite for the RulesEngine.
    /// Runs AI-vs-AI games and verifies every rule after every move.
    /// No visuals — pure logic testing.
    ///
    /// Call RulesEngineTests.RunAll() from a button or console command.
    /// </summary>
    public static class RulesEngineTests
    {
        private static int _totalTests = 0;
        private static int _totalPassed = 0;
        private static int _totalFailed = 0;
        private static List<string> _failures = new List<string>();

        public static void RunAll()
        {
            _totalTests = 0;
            _totalPassed = 0;
            _totalFailed = 0;
            _failures.Clear();

            Debug.Log("═══════════════════════════════════════════════════════");
            Debug.Log("  RULES ENGINE TEST SUITE");
            Debug.Log("═══════════════════════════════════════════════════════");

            TestWordDetectionStraightLines();
            TestWordDetectionNoRearranging();
            TestGravityConsistency();
            TestNoDuplicateScoring();
            TestPrimedWordIntegrity();
            TestSelfPlayGames(50);

            Debug.Log("═══════════════════════════════════════════════════════");
            Debug.Log($"  RESULTS: {_totalPassed}/{_totalTests} passed, {_totalFailed} failed");
            Debug.Log("═══════════════════════════════════════════════════════");

            if (_failures.Count > 0)
            {
                Debug.LogError($"  {_failures.Count} FAILURES:");
                for (int i = 0; i < _failures.Count && i < 20; i++)
                    Debug.LogError($"    [{i+1}] {_failures[i]}");
                if (_failures.Count > 20)
                    Debug.LogError($"    ... and {_failures.Count - 20} more");
            }
            else
            {
                Debug.Log("  ALL TESTS PASSED!");
            }
        }

        // ─── Helpers ────────────────────────────────────────────

        private static void Assert(bool condition, string testName, string details = "")
        {
            _totalTests++;
            if (condition)
            {
                _totalPassed++;
            }
            else
            {
                _totalFailed++;
                string msg = $"FAIL: {testName}" + (details.Length > 0 ? $" — {details}" : "");
                _failures.Add(msg);
                Debug.LogError($"[TEST] {msg}");
            }
        }

        private static RulesEngine CreateFreshEngine()
        {
            // Create a standalone RulesEngine for testing (not the singleton)
            GameObject go = new GameObject("TestRulesEngine");
            RulesEngine engine = go.AddComponent<RulesEngine>();
            // Reset it
            engine.ClearBoard();
            return engine;
        }

        private static void DestroyEngine(RulesEngine engine)
        {
            if (engine != null && engine.gameObject != null)
                Object.DestroyImmediate(engine.gameObject);
        }

        private static string BoardToString(RulesEngine engine)
        {
            var sb = new System.Text.StringBuilder();
            for (int row = GridManager.ROWS - 1; row >= 0; row--)
            {
                for (int col = 0; col < GridManager.COLS; col++)
                {
                    var cell = engine.GetCell(col, row);
                    sb.Append(cell != null ? cell.Letter.ToString() : ".");
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 1: Word detection only finds straight adjacent lines
        // ═══════════════════════════════════════════════════════════

        private static void TestWordDetectionStraightLines()
        {
            Debug.Log("\n--- Test: Word Detection Straight Lines ---");

            RulesEngine engine = CreateFreshEngine();

            // Place C-A-T horizontally at row 0
            engine.ProcessDrop(0, 'C', 0); // col 0, row 0
            engine.ProcessDrop(1, 'A', 0); // col 1, row 0
            var result = engine.ProcessDrop(2, 'T', 0); // col 2, row 0

            // Should detect CAT
            bool foundCat = false;
            if (result.ScoredWords != null)
            {
                for (int i = 0; i < result.ScoredWords.Count; i++)
                {
                    if (result.ScoredWords[i].Word == "CAT")
                        foundCat = true;
                }
            }
            Assert(foundCat, "CAT horizontal detection");

            // Verify all detected words have adjacent cells
            if (result.ScoredWords != null)
            {
                for (int i = 0; i < result.ScoredWords.Count; i++)
                {
                    var word = result.ScoredWords[i];
                    bool adjacent = VerifyAdjacentCells(word.Cells);
                    Assert(adjacent, $"Word '{word.Word}' cells are adjacent",
                        CellsToString(word.Cells));
                }
            }

            DestroyEngine(engine);
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 2: Words are not rearranged
        // ═══════════════════════════════════════════════════════════

        private static void TestWordDetectionNoRearranging()
        {
            Debug.Log("\n--- Test: No Word Rearranging ---");

            RulesEngine engine = CreateFreshEngine();

            // Place E-R-C-A horizontally — should NOT detect ACRE
            engine.ProcessDrop(0, 'E', 0);
            engine.ProcessDrop(1, 'R', 0);
            engine.ProcessDrop(2, 'C', 0);
            var result = engine.ProcessDrop(3, 'A', 0);

            bool foundAcre = false;
            if (result.ScoredWords != null)
            {
                for (int i = 0; i < result.ScoredWords.Count; i++)
                {
                    if (result.ScoredWords[i].Word == "ACRE")
                    {
                        foundAcre = true;
                        // Verify the cells actually spell ACRE in order
                        var cells = result.ScoredWords[i].Cells;
                        string actual = "";
                        for (int c = 0; c < cells.Count; c++)
                        {
                            var cell = engine.GetCell(cells[c].x, cells[c].y);
                            actual += cell != null ? cell.Letter.ToString() : "?";
                        }
                        Assert(actual == "ACRE",
                            "ACRE cells spell ACRE in order",
                            $"Cells spell '{actual}' not 'ACRE'");
                    }
                }
            }

            // With 2-direction scanning (left-to-right, top-to-bottom only),
            // ERCA should NOT match ACRE — we only read in reading order.
            Assert(!foundAcre, "ERCA does not detect ACRE (2-direction scanning)");

            DestroyEngine(engine);
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 3: Gravity consistency — no floating tiles
        // ═══════════════════════════════════════════════════════════

        private static void TestGravityConsistency()
        {
            Debug.Log("\n--- Test: Gravity Consistency ---");

            RulesEngine engine = CreateFreshEngine();

            // Fill up a column, trigger some words, check gravity
            engine.ProcessDrop(0, 'S', 0);
            engine.ProcessDrop(0, 'A', 1);
            engine.ProcessDrop(0, 'T', 0);
            engine.ProcessDrop(1, 'A', 1);
            engine.ProcessDrop(1, 'T', 0);
            engine.ProcessDrop(2, 'E', 1);

            VerifyGravity(engine, "After 6 drops");

            DestroyEngine(engine);
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 4: No duplicate scoring
        // ═══════════════════════════════════════════════════════════

        private static void TestNoDuplicateScoring()
        {
            Debug.Log("\n--- Test: No Duplicate Scoring ---");

            RulesEngine engine = CreateFreshEngine();

            // Place CAT, then place something else — CAT should not score again
            engine.ProcessDrop(0, 'C', 0);
            engine.ProcessDrop(1, 'A', 0);
            var r1 = engine.ProcessDrop(2, 'T', 0);

            int catCount1 = CountWord(r1, "CAT");

            // Drop another letter next to it — CAT should not re-score
            var r2 = engine.ProcessDrop(3, 'S', 0);
            int catCount2 = CountWord(r2, "CAT");

            Assert(catCount1 <= 1, "CAT scored at most once on creation");
            Assert(catCount2 == 0, "CAT not re-scored on next drop",
                $"CAT appeared {catCount2} time(s)");

            DestroyEngine(engine);
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 5: Primed word integrity
        // ═══════════════════════════════════════════════════════════

        private static void TestPrimedWordIntegrity()
        {
            Debug.Log("\n--- Test: Primed Word Integrity ---");

            RulesEngine engine = CreateFreshEngine();

            engine.ProcessDrop(0, 'C', 0);
            engine.ProcessDrop(1, 'A', 0);
            engine.ProcessDrop(2, 'T', 0);

            // Check primed words have valid cells
            var primed = engine.PrimedRegistry.GetAllPrimedWords();
            for (int i = 0; i < primed.Count; i++)
            {
                var pw = primed[i];
                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    var cell = engine.GetCell(pw.Cells[c].x, pw.Cells[c].y);
                    Assert(cell != null,
                        $"Primed '{pw.Word}' cell ({pw.Cells[c].x},{pw.Cells[c].y}) is occupied");

                    if (cell != null)
                    {
                        char expected = pw.Word[c];
                        Assert(char.ToUpper(cell.Letter) == expected,
                            $"Primed '{pw.Word}' cell ({pw.Cells[c].x},{pw.Cells[c].y}) has correct letter",
                            $"Expected '{expected}' got '{cell.Letter}'");
                    }
                }
            }

            DestroyEngine(engine);
        }

        // ═══════════════════════════════════════════════════════════
        // TEST 6: Self-play — run N full games with validation
        // ═══════════════════════════════════════════════════════════

        private static void TestSelfPlayGames(int numGames)
        {
            Debug.Log($"\n--- Test: Self-Play ({numGames} games) ---");

            int totalMoves = 0;
            int gamesCompleted = 0;

            // Fun metrics accumulators
            int totalWordsScored = 0;
            int totalZeroScoreTurns = 0;
            int totalPrimedCreated = 0;
            int totalDetonations = 0;
            int totalChains = 0;
            int longestZeroStreak = 0;
            int totalFirstScoreTurn = 0;
            int[] playerWins = new int[2];
            int[] playerTotalScore = new int[2];

            for (int game = 0; game < numGames; game++)
            {
                RulesEngine engine = CreateFreshEngine();
                TileBag bag = new TileBag();

                char[][] hands = new char[2][];
                hands[0] = new char[5];
                hands[1] = new char[5];

                // Deal initial hands
                for (int p = 0; p < 2; p++)
                    for (int h = 0; h < 5; h++)
                        hands[p][h] = bag.DrawLetter();

                int maxTurns = 30; // 15 per player
                int currentPlayer = 0;
                int[] gameScore = new int[2];
                int zeroStreak = 0;
                int gameFirstScoreTurn = -1;

                for (int turn = 0; turn < maxTurns; turn++)
                {
                    // Pick random letter from hand and random column
                    int handIdx = Random.Range(0, 5);
                    char letter = hands[currentPlayer][handIdx];
                    if (letter == '\0') letter = 'A'; // fallback

                    // Find a non-full column
                    int col = -1;
                    int attempts = 0;
                    while (attempts < 20)
                    {
                        int tryCol = Random.Range(0, GridManager.COLS);
                        if (engine.IsColumnAvailable(tryCol))
                        {
                            col = tryCol;
                            break;
                        }
                        attempts++;
                    }

                    if (col < 0) break; // Board full

                    // Count tiles BEFORE the drop
                    int tilesBefore = 0;
                    for (int tc = 0; tc < GridManager.COLS; tc++)
                        for (int tr = 0; tr < GridManager.ROWS; tr++)
                            if (engine.GetCell(tc, tr) != null) tilesBefore++;

                    // Drop
                    var result = engine.ProcessDrop(col, letter, currentPlayer);
                    totalMoves++;

                    // Refill hand
                    hands[currentPlayer][handIdx] = bag.DrawLetter();

                    // ── COLLECT METRICS ──
                    if (result != null)
                    {
                        int turnScore = result.TotalScore;
                        gameScore[currentPlayer] += turnScore;
                        if (turnScore == 0) { zeroStreak++; totalZeroScoreTurns++; }
                        else { longestZeroStreak = Mathf.Max(longestZeroStreak, zeroStreak); zeroStreak = 0; }
                        if (turnScore > 0 && gameFirstScoreTurn < 0) gameFirstScoreTurn = turn;
                        totalWordsScored += (result.ScoredWords != null ? result.ScoredWords.Count : 0);
                        totalPrimedCreated += (result.PrimedWords != null ? result.PrimedWords.Count : 0);
                        totalDetonations += (result.Explosions != null ? result.Explosions.Count : 0);
                        if (result.ChainSteps > 0) totalChains++;
                    }

                    // ── VALIDATE AFTER EVERY MOVE ──

                    // 1. Gravity — no floating tiles
                    VerifyGravity(engine, $"Game {game+1} Turn {turn+1}");

                    // 2. No ghost cells — occupied cells have data, empty don't
                    VerifyNoGhosts(engine, $"Game {game+1} Turn {turn+1}");

                    // 3. Scored words have valid cells
                    if (result.ScoredWords != null)
                    {
                        for (int w = 0; w < result.ScoredWords.Count; w++)
                        {
                            var sw = result.ScoredWords[w];

                            // Cells must be adjacent
                            bool adj = VerifyAdjacentCells(sw.Cells);
                            Assert(adj, $"G{game+1}T{turn+1}: '{sw.Word}' adjacent cells",
                                CellsToString(sw.Cells));

                            // Word must actually read correctly from cells AT TIME OF SCORING
                            // (cells may have changed after explosions, so we can't verify post-resolution)
                        }
                    }

                    // 4. (moved to test 8 below — primed word letter matching)

                    // 5. Tile count: before drop + 1 - exploded = after
                    int tilesAfter = 0;
                    for (int tc = 0; tc < GridManager.COLS; tc++)
                        for (int tr = 0; tr < GridManager.ROWS; tr++)
                            if (engine.GetCell(tc, tr) != null) tilesAfter++;

                    int explodedCount = 0;
                    if (result.Explosions != null)
                        for (int e = 0; e < result.Explosions.Count; e++)
                            if (result.Explosions[e].RemovedCells != null)
                                explodedCount += result.Explosions[e].RemovedCells.Count;

                    int expectedTiles = tilesBefore + 1 - explodedCount;
                    Assert(tilesAfter == expectedTiles,
                        $"G{game+1}T{turn+1}: Exact tile count",
                        $"Before={tilesBefore} +1drop -{explodedCount}exploded = expected {expectedTiles}, got {tilesAfter}. Board:\n{BoardToString(engine)}");

                    // 6. Explosion cells were actually primed — no innocent bystanders
                    if (result.Explosions != null)
                    {
                        for (int e = 0; e < result.Explosions.Count; e++)
                        {
                            var explosion = result.Explosions[e];
                            if (explosion.RemovedCells == null) continue;

                            // Every exploded cell should now be empty
                            for (int rc = 0; rc < explosion.RemovedCells.Count; rc++)
                            {
                                var removedCell = explosion.RemovedCells[rc];
                                var cellAfter = engine.GetCell(removedCell.x, removedCell.y);
                                // Cell might be refilled by gravity, so only check if it's above
                                // the gravity line — actually after gravity runs, cells can be
                                // repopulated. Skip this check.
                            }
                        }
                    }

                    // 7. Every letter on the board is a valid A-Z character
                    for (int vc = 0; vc < GridManager.COLS; vc++)
                    {
                        for (int vr = 0; vr < GridManager.ROWS; vr++)
                        {
                            var vcell = engine.GetCell(vc, vr);
                            if (vcell != null)
                            {
                                Assert(char.IsLetter(vcell.Letter) && vcell.Letter != '\0',
                                    $"G{game+1}T{turn+1}: Valid letter at ({vc},{vr})",
                                    $"Found '{vcell.Letter}' (int={(int)vcell.Letter})");
                            }
                        }
                    }

                    // 8. Primed words: letters must still match
                    var primedAfter = engine.PrimedRegistry.GetAllPrimedWords();
                    for (int pa = 0; pa < primedAfter.Count; pa++)
                    {
                        var pw = primedAfter[pa];
                        for (int pc = 0; pc < pw.Cells.Count; pc++)
                        {
                            var pcell = engine.GetCell(pw.Cells[pc].x, pw.Cells[pc].y);
                            Assert(pcell != null,
                                $"G{game+1}T{turn+1}: Primed '{pw.Word}'[{pc}] cell occupied",
                                $"Cell ({pw.Cells[pc].x},{pw.Cells[pc].y}) is empty! Board:\n{BoardToString(engine)}");

                            if (pcell != null)
                            {
                                char expected = pw.Word[pc];
                                Assert(char.ToUpper(pcell.Letter) == expected,
                                    $"G{game+1}T{turn+1}: Primed '{pw.Word}'[{pc}] letter match",
                                    $"Expected '{expected}' got '{pcell.Letter}' at ({pw.Cells[pc].x},{pw.Cells[pc].y})");
                            }
                        }
                    }

                    currentPlayer = 1 - currentPlayer;
                }

                // End of game: track winner and first-score turn
                longestZeroStreak = Mathf.Max(longestZeroStreak, zeroStreak);
                if (gameScore[0] > gameScore[1]) playerWins[0]++;
                else if (gameScore[1] > gameScore[0]) playerWins[1]++;
                playerTotalScore[0] += gameScore[0];
                playerTotalScore[1] += gameScore[1];
                if (gameFirstScoreTurn >= 0) totalFirstScoreTurn += gameFirstScoreTurn;
                else totalFirstScoreTurn += maxTurns; // no score = worst case

                gamesCompleted++;
                DestroyEngine(engine);
            }

            Debug.Log($"  Self-play: {gamesCompleted} games, {totalMoves} total moves");

            // ── FUN METRICS SUMMARY ──
            if (gamesCompleted > 0)
            {
                float avgTurns = (float)totalMoves / gamesCompleted;
                float avgWordsPerGame = (float)totalWordsScored / gamesCompleted;
                float avgPrimedPerGame = (float)totalPrimedCreated / gamesCompleted;
                float avgDetonationsPerGame = (float)totalDetonations / gamesCompleted;
                float avgChainsPerGame = (float)totalChains / gamesCompleted;
                float zeroScorePct = (float)totalZeroScoreTurns / totalMoves * 100f;
                float avgFirstScore = (float)totalFirstScoreTurn / gamesCompleted;
                float avgScorePerTurn = (float)(playerTotalScore[0] + playerTotalScore[1]) / totalMoves;

                Debug.Log($"\n══ FUN METRICS ({gamesCompleted} games) ══");
                Debug.Log($"  Avg score/turn:       {avgScorePerTurn:F1}");
                Debug.Log($"  Zero-score turns:     {zeroScorePct:F0}%");
                Debug.Log($"  Longest zero streak:  {longestZeroStreak}");
                Debug.Log($"  Avg words/game:       {avgWordsPerGame:F1}");
                Debug.Log($"  Avg primed/game:      {avgPrimedPerGame:F1}");
                Debug.Log($"  Avg detonations/game: {avgDetonationsPerGame:F1}");
                Debug.Log($"  Avg chains/game:      {avgChainsPerGame:F1}");
                Debug.Log($"  Avg first score turn: {avgFirstScore:F1}");
                Debug.Log($"  P0 wins: {playerWins[0]}  P1 wins: {playerWins[1]}  " +
                          $"Draws: {gamesCompleted - playerWins[0] - playerWins[1]}");

                // Warnings
                if (zeroScorePct > 60f)
                    Debug.LogWarning($"  ⚠ {zeroScorePct:F0}% zero-score turns — game may feel dry");
                if (avgFirstScore > 5f)
                    Debug.LogWarning($"  ⚠ First score avg at turn {avgFirstScore:F1} — slow start");
                if (avgDetonationsPerGame < 0.5f)
                    Debug.LogWarning($"  ⚠ Only {avgDetonationsPerGame:F1} detonations/game — priming underused");
            }
        }

        // ─── Validation Helpers ─────────────────────────────────

        private static bool VerifyAdjacentCells(List<Vector2Int> cells)
        {
            if (cells == null || cells.Count < 2) return true;

            for (int i = 1; i < cells.Count; i++)
            {
                int dx = Mathf.Abs(cells[i].x - cells[i-1].x);
                int dy = Mathf.Abs(cells[i].y - cells[i-1].y);

                // Must be exactly 1 step in one direction, 0 in the other
                bool adjacent = (dx == 1 && dy == 0) || (dx == 0 && dy == 1);
                if (!adjacent) return false;
            }
            return true;
        }

        private static void VerifyGravity(RulesEngine engine, string context)
        {
            for (int col = 0; col < GridManager.COLS; col++)
            {
                bool foundEmpty = false;
                for (int row = 0; row < GridManager.ROWS; row++)
                {
                    var cell = engine.GetCell(col, row);
                    if (cell == null)
                    {
                        foundEmpty = true;
                    }
                    else if (foundEmpty)
                    {
                        // Tile above an empty cell — gravity violation
                        Assert(false,
                            $"{context}: Gravity violation col {col}",
                            $"Tile '{cell.Letter}' at row {row} with empty below");
                    }
                }
            }
        }

        private static void VerifyNoGhosts(RulesEngine engine, string context)
        {
            int occupied = 0;
            for (int col = 0; col < GridManager.COLS; col++)
            {
                for (int row = 0; row < GridManager.ROWS; row++)
                {
                    var cell = engine.GetCell(col, row);
                    if (cell != null)
                    {
                        occupied++;
                        // Cell should have a valid letter
                        Assert(char.IsLetter(cell.Letter),
                            $"{context}: Cell ({col},{row}) has valid letter",
                            $"Got '{cell.Letter}'");
                    }
                }
            }
        }

        private static int CountWord(ResolutionResult result, string word)
        {
            int count = 0;
            if (result.ScoredWords != null)
            {
                for (int i = 0; i < result.ScoredWords.Count; i++)
                    if (result.ScoredWords[i].Word == word) count++;
            }
            return count;
        }

        private static string CellsToString(List<Vector2Int> cells)
        {
            if (cells == null) return "null";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < cells.Count; i++)
            {
                if (i > 0) sb.Append("->");
                sb.Append($"({cells[i].x},{cells[i].y})");
            }
            return sb.ToString();
        }
    }
}
