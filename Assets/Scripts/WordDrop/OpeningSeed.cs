using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Curated opening board seeds. Places a few neutral letters on the board
    /// at match start so turn 1 feels engaging — "I already see possibilities."
    ///
    /// All seeds are validated at apply-time to ensure they don't form any
    /// valid words on the board. Seeded letters are neutral (playerIndex = -1),
    /// not primed, not owned.
    ///
    /// Board: 7 columns (0–6), 6 rows (0 = bottom, 5 = top).
    /// Words scan left-to-right and top-to-bottom.
    /// </summary>
    public static class OpeningSeed
    {
        public static bool Enabled = true;

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

        // ── Curated seed library ────────────────────────────────────────────────
        //
        // Design constraints:
        //   - 2–4 letters, mostly in bottom 2 rows
        //   - No 3+ adjacent letters forming a valid word (H or V)
        //   - Common letters that create many possible continuations
        //   - Spread to avoid one-sided advantage
        //
        // Validation happens at runtime via ValidateSeed().

        private static readonly Seed[] _seeds = new Seed[]
        {
            // -- Spread pairs: two useful letters, opposite sides --
            new Seed { Name = "Anchors",     Placements = new[] {
                new Placement(1, 0, 'R'), new Placement(5, 0, 'E') }},
            new Seed { Name = "Posts",       Placements = new[] {
                new Placement(2, 0, 'T'), new Placement(5, 0, 'A') }},
            new Seed { Name = "Bookends",    Placements = new[] {
                new Placement(0, 0, 'S'), new Placement(6, 0, 'N') }},
            new Seed { Name = "Wide",        Placements = new[] {
                new Placement(1, 0, 'A'), new Placement(6, 0, 'R') }},

            // -- Triplets: three letters creating multiple paths --
            new Seed { Name = "Scatter",     Placements = new[] {
                new Placement(1, 0, 'E'), new Placement(3, 0, 'T'), new Placement(5, 0, 'A') }},
            new Seed { Name = "Steps",       Placements = new[] {
                new Placement(2, 0, 'N'), new Placement(4, 0, 'O'), new Placement(6, 1, 'S') }},
            new Seed { Name = "Triangle",    Placements = new[] {
                new Placement(1, 0, 'L'), new Placement(5, 0, 'R'), new Placement(3, 1, 'E') }},
            new Seed { Name = "Zigzag",      Placements = new[] {
                new Placement(0, 0, 'T'), new Placement(3, 0, 'I'), new Placement(6, 0, 'S') }},
            new Seed { Name = "Bases",       Placements = new[] {
                new Placement(2, 0, 'A'), new Placement(4, 0, 'R'), new Placement(6, 0, 'E') }},
            new Seed { Name = "Spaced",      Placements = new[] {
                new Placement(0, 0, 'E'), new Placement(3, 0, 'N'), new Placement(5, 0, 'T') }},
            new Seed { Name = "Offset",      Placements = new[] {
                new Placement(1, 0, 'S'), new Placement(4, 0, 'A'), new Placement(2, 1, 'T') }},
            new Seed { Name = "Lean",        Placements = new[] {
                new Placement(0, 0, 'R'), new Placement(4, 0, 'E'), new Placement(2, 1, 'A') }},
            new Seed { Name = "Bridge",      Placements = new[] {
                new Placement(1, 0, 'O'), new Placement(3, 0, 'S'), new Placement(5, 0, 'T') }},
            new Seed { Name = "Corners",     Placements = new[] {
                new Placement(0, 0, 'N'), new Placement(6, 0, 'L'), new Placement(3, 0, 'A') }},

            // -- Quads: four letters, more structure --
            new Seed { Name = "Diamond",     Placements = new[] {
                new Placement(1, 0, 'R'), new Placement(5, 0, 'N'),
                new Placement(2, 1, 'E'), new Placement(4, 1, 'A') }},
            new Seed { Name = "Frame",       Placements = new[] {
                new Placement(0, 0, 'T'), new Placement(6, 0, 'S'),
                new Placement(2, 0, 'I'), new Placement(4, 0, 'E') }},
            new Seed { Name = "Pillars",     Placements = new[] {
                new Placement(1, 0, 'E'), new Placement(1, 1, 'R'),
                new Placement(5, 0, 'A'), new Placement(5, 1, 'N') }},
            new Seed { Name = "Wings",       Placements = new[] {
                new Placement(0, 0, 'A'), new Placement(2, 0, 'T'),
                new Placement(4, 0, 'E'), new Placement(6, 0, 'R') }},
        };

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Generate a unique procedural opening, or fall back to curated seeds.
        /// Daily mode uses seeded RNG for deterministic boards.
        /// Returns the seed name, or null if disabled.
        /// </summary>
        public static string ApplyRandomSeed(RulesEngine rules)
        {
            if (!Enabled || rules == null) return null;

            // Try procedural generation first — unique every match
            string proceduralResult = TryProceduralSeed(rules);
            if (proceduralResult != null) return proceduralResult;

            // Fallback: curated seeds
            return ApplyCuratedSeed(rules);
        }

        // ── Procedural seed generation ──────────────────────────────────────

        // Letters weighted by usefulness — vowels + common consonants more likely
        private static readonly char[] SEED_VOWELS     = { 'A', 'E', 'I', 'O', 'U' };
        private static readonly char[] SEED_CONSONANTS = {
            'R', 'S', 'T', 'N', 'L', 'D', 'H', 'C', 'M', 'P',  // common
            'B', 'F', 'G', 'W', 'K', 'Y'                         // less common
        };

        private const int PROCEDURAL_MAX_ATTEMPTS = 20;

        private static string TryProceduralSeed(RulesEngine rules)
        {
            System.Random rng;
            if (DailyDropManager.IsDailyMode)
                rng = new System.Random(DailyDropManager.GetDailySeed() + 7919);
            else
                rng = new System.Random(System.Environment.TickCount);

            for (int attempt = 0; attempt < PROCEDURAL_MAX_ATTEMPTS; attempt++)
            {
                int letterCount = 2 + rng.Next(0, 3); // 2, 3, or 4 letters
                var placements = GenerateLayout(letterCount, rng);
                if (placements == null) continue;

                Seed candidate = new Seed
                {
                    Name = $"Proc_{attempt}",
                    Placements = placements
                };

                if (ValidateSeed(candidate, rules))
                {
                    float quality = ScoreSeed(candidate);
                    if (quality >= 4f) // minimum quality threshold
                    {
                        ApplySeed(candidate, rules);
                        string letters = "";
                        for (int i = 0; i < placements.Length; i++)
                            letters += placements[i].Letter;
                        Debug.Log($"[OpeningSeed] Procedural '{letters}' (quality={quality:F1}, attempt={attempt + 1})");
                        return candidate.Name;
                    }
                }
            }

            return null; // fall through to curated
        }

        private static Placement[] GenerateLayout(int count, System.Random rng)
        {
            var placements = new Placement[count];
            var usedCols = new HashSet<int>();

            // Guarantee at least 1 vowel
            int vowelSlot = rng.Next(0, count);

            for (int i = 0; i < count; i++)
            {
                // Pick column — spread across board, no duplicates on row 0
                int col;
                int colAttempts = 0;
                do
                {
                    col = rng.Next(0, RulesEngine.COLS);
                    colAttempts++;
                    if (colAttempts > 20) return null;
                }
                while (usedCols.Contains(col) || !HasMinSpacing(col, usedCols, count <= 2 ? 3 : 2));
                usedCols.Add(col);

                // Pick letter
                char letter;
                if (i == vowelSlot)
                    letter = SEED_VOWELS[rng.Next(0, SEED_VOWELS.Length)];
                else if (rng.Next(0, 100) < 35) // 35% chance of extra vowel
                    letter = SEED_VOWELS[rng.Next(0, SEED_VOWELS.Length)];
                else
                    letter = SEED_CONSONANTS[rng.Next(0, SEED_CONSONANTS.Length)];

                // Most tiles on row 0, occasional row 1 (must be supported)
                int row = 0;
                if (i > 0 && rng.Next(0, 100) < 20) // 20% chance of stacking
                {
                    // Check if any existing placement is in same column at row 0
                    for (int j = 0; j < i; j++)
                    {
                        if (placements[j].Col == col && placements[j].Row == 0)
                        {
                            row = 1;
                            break;
                        }
                    }
                    // If no support, stay at row 0
                    if (row == 1)
                    {
                        // Already have support — keep row 1
                    }
                    else
                    {
                        row = 0;
                    }
                }

                placements[i] = new Placement(col, row, letter);
            }

            return placements;
        }

        private static bool HasMinSpacing(int col, HashSet<int> used, int minDist)
        {
            foreach (int c in used)
                if (Mathf.Abs(col - c) < minDist) return false;
            return true;
        }

        // ── Curated seed fallback ───────────────────────────────────────────

        private static string ApplyCuratedSeed(RulesEngine rules)
        {
            var candidates = new List<(Seed seed, float score)>();

            for (int i = 0; i < _seeds.Length; i++)
            {
                if (ValidateSeed(_seeds[i], rules))
                {
                    float quality = ScoreSeed(_seeds[i]);
                    candidates.Add((_seeds[i], quality));
                }
            }

            if (candidates.Count == 0)
            {
                Debug.LogWarning("[OpeningSeed] No valid seed found — starting with blank board");
                return null;
            }

            candidates.Sort((a, b) => b.score.CompareTo(a.score));

            int topCount = Mathf.Max(3, candidates.Count / 2);
            topCount = Mathf.Min(topCount, candidates.Count);
            int pick;
            if (DailyDropManager.IsDailyMode)
            {
                var seededRng = new System.Random(DailyDropManager.GetDailySeed() + 7919);
                pick = seededRng.Next(0, topCount);
            }
            else
            {
                pick = Random.Range(0, topCount);
            }

            Seed chosen = candidates[pick].seed;
            ApplySeed(chosen, rules);

            Debug.Log($"[OpeningSeed] Curated '{chosen.Name}' (quality={candidates[pick].score:F1}, " +
                      $"rank {pick + 1}/{candidates.Count})");

            return chosen.Name;
        }

        private static string FormatTop3(List<(Seed seed, float score)> list)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < Mathf.Min(3, list.Count); i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"{list[i].seed.Name}={list[i].score:F1}");
            }
            return sb.ToString();
        }

        // ── Seed quality scoring ────────────────────────────────────────────────

        /// <summary>
        /// Rates seed quality. Higher = more likely to create engaging first turns.
        /// </summary>
        private static float ScoreSeed(Seed seed)
        {
            float score = 0f;
            var placements = seed.Placements;

            // 1. Completion opportunities: how many near-word setups exist?
            //    Count pairs of letters that are adjacent (could become part of a word)
            for (int i = 0; i < placements.Length; i++)
            {
                for (int j = i + 1; j < placements.Length; j++)
                {
                    int dx = Mathf.Abs(placements[i].Col - placements[j].Col);
                    int dy = Mathf.Abs(placements[i].Row - placements[j].Row);
                    // Adjacent on same row (horizontal potential)
                    if (dy == 0 && dx == 2) score += 3f; // gap of 1 = fill to make 3-letter word
                    if (dy == 0 && dx == 1) score += 1f; // adjacent = 2-run, needs one more
                    // Vertical adjacency
                    if (dx == 0 && dy == 1) score += 1.5f;
                }
            }

            // 2. Centrality: reward letters near center columns (2-4)
            for (int i = 0; i < placements.Length; i++)
            {
                float centerDist = Mathf.Abs(placements[i].Col - 3f);
                score += Mathf.Max(0f, 2f - centerDist);
            }

            // 3. Spread / fairness: reward spread across both halves
            bool hasLeft = false, hasRight = false;
            for (int i = 0; i < placements.Length; i++)
            {
                if (placements[i].Col <= 2) hasLeft = true;
                if (placements[i].Col >= 4) hasRight = true;
            }
            if (hasLeft && hasRight) score += 3f;

            // 4. Multi-path: more letters = more paths (diminishing)
            score += placements.Length * 1.5f;

            // 5. Penalty: overly isolated letters (no other letter within 3 cols)
            for (int i = 0; i < placements.Length; i++)
            {
                bool hasNeighbor = false;
                for (int j = 0; j < placements.Length; j++)
                {
                    if (i == j) continue;
                    if (Mathf.Abs(placements[i].Col - placements[j].Col) <= 3)
                    {
                        hasNeighbor = true;
                        break;
                    }
                }
                if (!hasNeighbor) score -= 2f;
            }

            return score;
        }

        /// <summary>Debug: print all seeds ranked by quality. Call from console/editor.</summary>
        public static void DebugPrintSeedRankings()
        {
            Debug.Log("[OpeningSeed] ══ SEED QUALITY RANKINGS ══");
            var ranked = new List<(string name, float score, int letters)>();
            for (int i = 0; i < _seeds.Length; i++)
                ranked.Add((_seeds[i].Name, ScoreSeed(_seeds[i]), _seeds[i].Placements.Length));
            ranked.Sort((a, b) => b.score.CompareTo(a.score));
            for (int i = 0; i < ranked.Count; i++)
                Debug.Log($"  {i + 1}. {ranked[i].name} — quality={ranked[i].score:F1} ({ranked[i].letters} letters)");
        }

        // ── Validation ──────────────────────────────────────────────────────────

        /// <summary>
        /// Validates a seed by temporarily placing its letters and scanning
        /// for any valid words. Returns true if the seed is safe (no words formed).
        /// </summary>
        private static bool ValidateSeed(Seed seed, RulesEngine rules)
        {
            // Check all placements are in bounds and on empty cells
            for (int i = 0; i < seed.Placements.Length; i++)
            {
                var p = seed.Placements[i];
                if (p.Col < 0 || p.Col >= RulesEngine.COLS ||
                    p.Row < 0 || p.Row >= RulesEngine.ROWS)
                    return false;

                // Seed letters must stack properly (no floating tiles)
                if (p.Row > 0)
                {
                    // Check there's a tile below (either another seed letter or existing)
                    bool supported = rules.GetCell(p.Col, p.Row - 1) != null;
                    if (!supported)
                    {
                        // Check if another placement in this seed supports it
                        for (int j = 0; j < seed.Placements.Length; j++)
                        {
                            if (j == i) continue;
                            if (seed.Placements[j].Col == p.Col && seed.Placements[j].Row == p.Row - 1)
                            {
                                supported = true;
                                break;
                            }
                        }
                    }
                    if (!supported) return false;
                }
            }

            // Temporarily place letters
            for (int i = 0; i < seed.Placements.Length; i++)
            {
                var p = seed.Placements[i];
                rules.SetCell(p.Col, p.Row, new RulesCellData
                {
                    Letter = p.Letter,
                    Col = p.Col,
                    Row = p.Row,
                    PlayerIndex = -1,
                });
            }

            // Scan for words by checking all horizontal and vertical runs
            bool hasWords = ScanForAnyWord(rules);

            // Remove temporary letters
            for (int i = 0; i < seed.Placements.Length; i++)
            {
                var p = seed.Placements[i];
                rules.ClearCell(p.Col, p.Row);
            }

            if (hasWords)
                Debug.Log($"[OpeningSeed] Rejected seed '{seed.Name}' — forms a valid word");

            return !hasWords;
        }

        /// <summary>
        /// Scans the board for any valid 3–7 letter words (horizontal or vertical).
        /// </summary>
        private static bool ScanForAnyWord(RulesEngine rules)
        {
            // Horizontal scan: each row, left to right
            for (int row = 0; row < RulesEngine.ROWS; row++)
            {
                for (int startCol = 0; startCol <= RulesEngine.COLS - 3; startCol++)
                {
                    if (rules.GetCell(startCol, row) == null) continue;

                    var sb = new System.Text.StringBuilder();
                    for (int c = startCol; c < RulesEngine.COLS; c++)
                    {
                        var cell = rules.GetCell(c, row);
                        if (cell == null) break;
                        sb.Append(cell.Letter);

                        if (sb.Length >= 3 && sb.Length <= 7)
                        {
                            if (WordDictionary.IsValidWord(sb.ToString()))
                                return true;
                        }
                    }
                }
            }

            // Vertical scan: each column, top to bottom (high row to low)
            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                for (int startRow = RulesEngine.ROWS - 1; startRow >= 2; startRow--)
                {
                    if (rules.GetCell(col, startRow) == null) continue;

                    var sb = new System.Text.StringBuilder();
                    for (int r = startRow; r >= 0; r--)
                    {
                        var cell = rules.GetCell(col, r);
                        if (cell == null) break;
                        sb.Append(cell.Letter);

                        if (sb.Length >= 3 && sb.Length <= 7)
                        {
                            if (WordDictionary.IsValidWord(sb.ToString()))
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        // ── Apply ───────────────────────────────────────────────────────────────

        private static void ApplySeed(Seed seed, RulesEngine rules)
        {
            for (int i = 0; i < seed.Placements.Length; i++)
            {
                var p = seed.Placements[i];
                rules.SetCell(p.Col, p.Row, new RulesCellData
                {
                    Letter = p.Letter,
                    Col = p.Col,
                    Row = p.Row,
                    PlayerIndex = -1, // neutral — no owner
                });
            }

            // Mark these words as already "scored" so the first drop doesn't
            // re-detect them as new words (they aren't words, but just in case)
            // Not needed — validation ensures no words exist.
        }
    }
}
