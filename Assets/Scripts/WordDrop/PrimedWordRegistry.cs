using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Stores and manages primed word records.
    ///
    /// A word becomes "primed" after it is scored on the board. Primed words can be
    /// triggered (exploded) when a new word shares one of their tiles. Primed words
    /// expire automatically after a set number of turns if not triggered.
    ///
    /// Plain C# class — no MonoBehaviour, no Unity dependency.
    ///
    /// Job 4 integration:
    ///   RulesEngine.ProcessDrop() calls AddPrimedWord() for each scored word,
    ///   ExpireOldWords() before each resolution, and RemovePrimedWord() when
    ///   a primed word is triggered and exploded.
    /// </summary>
    public class PrimedWordRegistry
    {
        // ── PrimedWord record ─────────────────────────────────────────────────────

        public class PrimedWord
        {
            public int Id { get; internal set; }
            public string Word { get; set; }
            public int Score { get; set; }  // Original score — awarded again on detonation (double word score)
            public List<Vector2Int> Cells { get; set; }
            public int OwnerPlayer { get; set; }
            public int PrimedOnTurn { get; set; }
            public int ExpiresOnTurn { get; set; }
            public int OverlapFuseBonusGranted { get; set; } = 0; // total +turns from overlap extensions
            public int GlobalFuseResetCount { get; set; } = 0; // capped board-wide fuse resets
            public bool IsGold { get; set; } = false; // gold primed word — worth 3x on detonation
            public float CreatedAtTime { get; set; } // wall-clock time when primed (for idle expiry)
            public float MaxAgeSeconds { get; set; } = 30f; // how long this prime lives (longer words get more time)

            public override string ToString()
                => $"PrimedWord[id={Id} word={Word} owner={OwnerPlayer} " +
                   $"turns={PrimedOnTurn}→{ExpiresOnTurn} cells={CellsString()}]";

            public string CellsString()
            {
                if (Cells == null || Cells.Count == 0) return "[]";
                var sb = new System.Text.StringBuilder("[");
                for (int i = 0; i < Cells.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append($"({Cells[i].x},{Cells[i].y})");
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        // ── Internal state ────────────────────────────────────────────────────────

        private readonly List<PrimedWord> _primedWords = new List<PrimedWord>();
        private int _nextId = 1;

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a new primed word to the registry.
        /// Returns the unique ID assigned to this primed word.
        /// </summary>
        public int AddPrimedWord(
            string word,
            List<Vector2Int> cells,
            int ownerPlayer,
            int primedOnTurn,
            int expiresOnTurn,
            int score = 0,
            bool isGold = false)
        {
            if (string.IsNullOrEmpty(word))
            {
                Debug.LogWarning("[PrimedWordRegistry] AddPrimedWord: word is null or empty.");
                return -1;
            }

            if (cells == null || cells.Count == 0)
            {
                Debug.LogWarning($"[PrimedWordRegistry] AddPrimedWord: cells list is null/empty for '{word}'.");
                return -1;
            }

            int id = _nextId++;

            // Longer words earn more time before expiry:
            // 3 letters = 25s, 4 = 30s, 5 = 38s, 6 = 45s, 7 = 50s
            int wordLen = word.Trim().Length;
            float maxAge = wordLen <= 3 ? 25f
                         : wordLen == 4 ? 30f
                         : wordLen == 5 ? 38f
                         : wordLen == 6 ? 45f
                         : 50f;

            var record = new PrimedWord
            {
                Id            = id,
                Word          = word.ToUpper().Trim(),
                Score         = score,
                Cells         = new List<Vector2Int>(cells),
                OwnerPlayer   = ownerPlayer,
                PrimedOnTurn  = primedOnTurn,
                ExpiresOnTurn = expiresOnTurn,
                IsGold        = isGold,
                CreatedAtTime = Time.time,
                MaxAgeSeconds = maxAge,
            };

            _primedWords.Add(record);

//             Debug.Log($"[PrimedWordRegistry] Added: {record}  " +
                      // $"(registry size: {_primedWords.Count})");

            return id;
        }

        /// <summary>
        /// Returns all primed words whose cell list contains the given cell.
        /// Returns an empty list if no match is found.
        /// </summary>
        public List<PrimedWord> GetPrimedWordsContaining(Vector2Int cell)
        {
            var results = new List<PrimedWord>();

            for (int i = 0; i < _primedWords.Count; i++)
            {
                PrimedWord pw = _primedWords[i];
                if (pw.Cells == null) continue;

                for (int j = 0; j < pw.Cells.Count; j++)
                {
                    if (pw.Cells[j] == cell)
                    {
                        results.Add(pw);
                        break;
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Returns all primed words that have at least one cell orthogonally adjacent
        /// to the given cell (but NOT containing it — that's GetPrimedWordsContaining).
        /// Used for adjacency-based detonation triggering.
        /// </summary>
        public List<PrimedWord> GetPrimedWordsAdjacentTo(Vector2Int cell)
        {
            var results = new List<PrimedWord>();
            int[] dx = { 1, -1, 0, 0 };
            int[] dy = { 0, 0, 1, -1 };

            for (int i = 0; i < _primedWords.Count; i++)
            {
                PrimedWord pw = _primedWords[i];
                if (pw.Cells == null) continue;

                bool found = false;
                for (int j = 0; j < pw.Cells.Count && !found; j++)
                {
                    for (int d = 0; d < 4; d++)
                    {
                        if (pw.Cells[j].x + dx[d] == cell.x &&
                            pw.Cells[j].y + dy[d] == cell.y)
                        {
                            results.Add(pw);
                            found = true;
                            break;
                        }
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Finds the full connected group of primed words starting from the given seed IDs.
        /// Two primed words are connected if they share at least one tile.
        /// Traverses transitively: if A shares with B and B shares with C, all three are connected.
        /// Excludes any primed word whose ID is in excludeIds (e.g. words just primed this resolution).
        /// </summary>
        /// <summary>
        /// Finds all primed words connected to the seed set — through shared tiles
        /// OR orthogonal adjacency. One trigger cascades through the entire cluster.
        /// </summary>
        public List<PrimedWord> FindConnectedGroup(HashSet<int> seedIds, HashSet<int> excludeIds)
        {
            var visited = new HashSet<int>(seedIds);
            var queue = new Queue<int>(seedIds);
            var result = new List<PrimedWord>();

            // Orthogonal neighbor offsets (+ self)
            int[] dx = { 0, 1, -1, 0, 0 };
            int[] dy = { 0, 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                int id = queue.Dequeue();
                PrimedWord pw = GetById(id);
                if (pw == null) continue;

                result.Add(pw);

                // Check each cell of this word + orthogonal neighbors
                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    for (int d = 0; d < 5; d++)
                    {
                        var checkCell = new Vector2Int(pw.Cells[c].x + dx[d], pw.Cells[c].y + dy[d]);
                        var overlapping = GetPrimedWordsContaining(checkCell);
                        for (int o = 0; o < overlapping.Count; o++)
                        {
                            int otherId = overlapping[o].Id;
                            if (visited.Contains(otherId)) continue;
                            if (excludeIds != null && excludeIds.Contains(otherId)) continue;

                            visited.Add(otherId);
                            queue.Enqueue(otherId);
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Removes the primed word with the given ID from the registry.
        /// Returns true if found and removed, false if not found.
        /// </summary>
        public bool RemovePrimedWord(int id)
        {
            for (int i = 0; i < _primedWords.Count; i++)
            {
                if (_primedWords[i].Id == id)
                {
                    string word = _primedWords[i].Word;
                    _primedWords.RemoveAt(i);
//                     Debug.Log($"[PrimedWordRegistry] Removed id={id} ('{word}'). " +
                              // $"Registry size: {_primedWords.Count}");
                    return true;
                }
            }

            Debug.LogWarning($"[PrimedWordRegistry] RemovePrimedWord: id={id} not found.");
            return false;
        }

        /// <summary>
        /// Removes all primed words whose ExpiresOnTurn is less than or equal to currentTurn.
        /// Returns the number of words expired.
        ///
        /// Also removes primed words whose cells are no longer occupied on the board
        /// (e.g., tiles were removed by a previous explosion in the same chain).
        /// Pass a board-check callback for full validation, or null to skip.
        /// </summary>
        /// <summary>
        /// Expires primed words that have lived longer than maxAge seconds.
        /// Called from SurvivalManager.Update so idle players can't stockpile primes.
        /// Returns the number of expired words.
        /// </summary>
        public int ExpireByTime(float unusedParam = 0f, List<PrimedWord> expiredOut = null)
        {
            int expired = 0;
            float now = Time.time;

            for (int i = _primedWords.Count - 1; i >= 0; i--)
            {
                PrimedWord pw = _primedWords[i];
                float age = now - pw.CreatedAtTime;
                if (age >= pw.MaxAgeSeconds)
                {
//                     Debug.Log($"[PrimedWordRegistry] Time-expired '{pw.Word}' (id={pw.Id}, age={age:F1}s/{pw.MaxAgeSeconds:F0}s)");
                    if (expiredOut != null) expiredOut.Add(pw);
                    _primedWords.RemoveAt(i);
                    expired++;
                }
            }

            return expired;
        }

        public int ExpireOldWords(int currentTurn, List<PrimedWord> expiredOut = null)
        {
            int expiredCount = 0;

            for (int i = _primedWords.Count - 1; i >= 0; i--)
            {
                PrimedWord pw = _primedWords[i];
                if (pw.ExpiresOnTurn <= currentTurn)
                {
//                     Debug.Log($"[PrimedWordRegistry] Expiring '{pw.Word}' (id={pw.Id}) — " +
                              // $"expiresOnTurn={pw.ExpiresOnTurn} <= currentTurn={currentTurn}");
                    if (expiredOut != null) expiredOut.Add(pw);
                    _primedWords.RemoveAt(i);
                    expiredCount++;
                }
            }

            return expiredCount;
        }

        /// <summary>
        /// Removes any primed words that reference cells which are now empty.
        /// <summary>
        /// After gravity shifts tiles, update primed word cell references to new positions.
        /// </summary>
        public void UpdateCellPositions(Dictionary<Vector2Int, Vector2Int> moves)
        {
            if (moves == null || moves.Count == 0) return;

            for (int i = 0; i < _primedWords.Count; i++)
            {
                PrimedWord pw = _primedWords[i];
                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    if (moves.TryGetValue(pw.Cells[c], out Vector2Int newPos))
                    {
                        pw.Cells[c] = newPos;
                    }
                }
            }
        }

        /// <summary>
        /// Called after explosions + gravity to clean up stale primed references.
        /// </summary>
        public int RemoveStaleWords(System.Func<int, int, bool> isCellOccupied)
        {
            if (isCellOccupied == null) return 0;

            int removedCount = 0;

            for (int i = _primedWords.Count - 1; i >= 0; i--)
            {
                PrimedWord pw = _primedWords[i];
                bool stale = false;

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    if (!isCellOccupied(pw.Cells[c].x, pw.Cells[c].y))
                    {
                        stale = true;
                        break;
                    }
                }

                if (stale)
                {
//                     Debug.Log($"[PrimedWordRegistry] Removing stale primed word " +
                              // $"'{pw.Word}' (id={pw.Id}) — cell(s) no longer occupied.");
                    _primedWords.RemoveAt(i);
                    removedCount++;
                }
            }

            return removedCount;
        }

        /// <summary>
        /// Returns a read-only snapshot of all currently primed words.
        /// </summary>
        public int Count => _primedWords.Count;

        public List<PrimedWord> GetAllPrimedWords()
        {
            return new List<PrimedWord>(_primedWords);
        }

        public PrimedWord GetByIndex(int index)
        {
            if (index < 0 || index >= _primedWords.Count) return null;
            return _primedWords[index];
        }

        /// <summary>
        /// Returns the primed word with the given ID, or null if not found.
        /// </summary>
        public PrimedWord GetById(int id)
        {
            for (int i = 0; i < _primedWords.Count; i++)
                if (_primedWords[i].Id == id) return _primedWords[i];
            return null;
        }

        /// <summary>
        /// Removes all primed words from the registry. Call on match reset.
        /// </summary>
        public void Clear()
        {
            int count = _primedWords.Count;
            _primedWords.Clear();
            _nextId = 1;
//             Debug.Log($"[PrimedWordRegistry] Cleared {count} primed word(s). ID counter reset.");
        }

        // Count property defined above (line 244)

        // ── Console verification helper ───────────────────────────────────────────

        public static void RunVerificationTests()
        {
//             Debug.Log("[PrimedWordRegistry] ══ BEGIN VERIFICATION TESTS ══");

            var registry = new PrimedWordRegistry();

            // Test 1: Add + query
            var cells = new List<Vector2Int>
            {
                new Vector2Int(0, 0),
                new Vector2Int(1, 0),
                new Vector2Int(2, 0),
            };

            int id1 = registry.AddPrimedWord("CAT", cells, ownerPlayer: 0, primedOnTurn: 1, expiresOnTurn: 5);
            var found1 = registry.GetPrimedWordsContaining(new Vector2Int(1, 0));
            bool test1Pass = found1.Count == 1 && found1[0].Word == "CAT" && found1[0].Id == id1;
//             Debug.Log($"[PrimedWordRegistry] Test 1 — query cell in 'CAT': " +
                      // $"{(test1Pass ? "✓ PASS" : "✗ FAIL")}");

            // Test 2: Query cell NOT in any word
            var found2 = registry.GetPrimedWordsContaining(new Vector2Int(5, 3));
            bool test2Pass = found2.Count == 0;
//             Debug.Log($"[PrimedWordRegistry] Test 2 — query empty cell: " +
                      // $"{(test2Pass ? "✓ PASS" : "✗ FAIL")}");

            // Test 3: Expiry
            int expiredCount = registry.ExpireOldWords(currentTurn: 6);
            bool test3Pass = expiredCount == 1 && registry.Count == 0;
//             Debug.Log($"[PrimedWordRegistry] Test 3 — expire: " +
                      // $"{(test3Pass ? "✓ PASS" : "✗ FAIL")}");

            // Test 4: Remove by ID
            var cells2 = new List<Vector2Int> { new Vector2Int(3, 2), new Vector2Int(4, 2) };
            int id2 = registry.AddPrimedWord("IT", cells2, ownerPlayer: 1, primedOnTurn: 2, expiresOnTurn: 10);
            bool removed = registry.RemovePrimedWord(id2);
            bool test4Pass = removed && registry.Count == 0;
//             Debug.Log($"[PrimedWordRegistry] Test 4 — remove by ID: " +
                      // $"{(test4Pass ? "✓ PASS" : "✗ FAIL")}");

            // Test 5: GetAllPrimedWords returns copy
            var cells3 = new List<Vector2Int> { new Vector2Int(0, 1), new Vector2Int(1, 1), new Vector2Int(2, 1) };
            var cells4 = new List<Vector2Int> { new Vector2Int(0, 2), new Vector2Int(1, 2), new Vector2Int(2, 2) };
            registry.AddPrimedWord("DOG", cells3, 0, 3, 8);
            registry.AddPrimedWord("CAT", cells4, 1, 3, 8);

            var allWords = registry.GetAllPrimedWords();
            bool test5Pass = allWords.Count == 2;
            allWords.Clear();
            bool test5bPass = registry.Count == 2;
//             Debug.Log($"[PrimedWordRegistry] Test 5 — snapshot copy: " +
                      // $"{(test5Pass && test5bPass ? "✓ PASS" : "✗ FAIL")}");

            // Test 6: RemoveStaleWords
            // DOG at cells (0,1),(1,1),(2,1) — simulate (1,1) being empty
            int staleRemoved = registry.RemoveStaleWords((c, r) =>
            {
                // All cells occupied EXCEPT (1,1)
                return !(c == 1 && r == 1);
            });
            bool test6Pass = staleRemoved == 1 && registry.Count == 1;
//             Debug.Log($"[PrimedWordRegistry] Test 6 — remove stale (DOG): " +
                      // $"removed={staleRemoved} remaining={registry.Count} " +
                      // $"{(test6Pass ? "✓ PASS" : "✗ FAIL")}");

            bool allPassed = test1Pass && test2Pass && test3Pass && test4Pass
                          && test5Pass && test5bPass && test6Pass;
//             Debug.Log($"[PrimedWordRegistry] ══ TESTS {(allPassed ? "ALL PASSED ✓" : "SOME FAILED ✗")} ══");

            registry.Clear();
        }
    }
}
