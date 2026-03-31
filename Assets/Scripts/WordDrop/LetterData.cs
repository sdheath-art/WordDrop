using System.Collections.Generic;

namespace WordDrop
{
    /// <summary>
    /// Simplified letter point values and tile distribution counts.
    ///
    /// Point value system (simplified, fun-first):
    ///   Common vowels/consonants  = 1 pt   (A, E, I, O, U, L, N, R, S, T)
    ///   Slightly uncommon         = 2 pts  (D, G)
    ///   Medium uncommon           = 3 pts  (B, C, M, P)
    ///   Less common               = 4 pts  (F, H, V, W, Y)
    ///   Rare                      = 5 pts  (K)
    ///   Very rare                 = 8 pts  (J, X)
    ///   Rarest                    = 10 pts (Q, Z)
    ///
    /// GetPoints(), GetTileCount(), GetDistribution() signatures are unchanged.
    /// </summary>
    public static class LetterData
    {
        // ---------------------------------------------------------------------------
        // Simplified point values
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<char, int> _pointValues = new Dictionary<char, int>
        {
            // 1-point letters — most common, appear most often
            { 'A', 1 }, { 'E', 1 }, { 'I', 1 }, { 'O', 1 }, { 'U', 1 },
            { 'L', 1 }, { 'N', 1 }, { 'R', 1 }, { 'S', 1 }, { 'T', 1 },

            // 2-point letters
            { 'D', 2 }, { 'G', 2 },

            // 3-point letters
            { 'B', 3 }, { 'C', 3 }, { 'M', 3 }, { 'P', 3 },

            // 4-point letters
            { 'F', 4 }, { 'H', 4 }, { 'V', 4 }, { 'W', 4 }, { 'Y', 4 },

            // 5-point letters
            { 'K', 5 },

            // 8-point letters
            { 'J', 8 }, { 'X', 8 },

            // 10-point letters
            { 'Q', 10 }, { 'Z', 10 },
        };

        // ---------------------------------------------------------------------------
        // Tile counts — mirrors TileBag._distribution for reference/API compatibility.
        // GetDistribution() is used by callers that want the canonical tile counts.
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<char, int> _tileCounts = new Dictionary<char, int>
        {
            // Vowels (boosted for fun-first play)
            { 'A', 12 }, { 'E', 14 }, { 'I',  9 }, { 'O',  9 }, { 'U',  4 },

            // High-frequency consonants
            { 'R',  9 }, { 'S',  9 }, { 'T',  9 }, { 'N',  7 }, { 'L',  6 },
            { 'D',  5 }, { 'H',  4 }, { 'G',  4 }, { 'M',  4 }, { 'P',  4 },
            { 'C',  4 },

            // Medium-frequency consonants
            { 'B',  3 }, { 'F',  3 }, { 'W',  3 }, { 'Y',  3 },

            // Low-frequency letters
            { 'V',  2 }, { 'K',  2 },

            // Rare letters
            { 'J',  1 }, { 'X',  1 }, { 'Q',  1 }, { 'Z',  1 },
        };

        // ---------------------------------------------------------------------------
        // Public API — signatures unchanged
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Returns the point value for a given letter (uppercase or lowercase).
        /// Returns 0 if the letter is not found (e.g. wild sentinel '*').
        ///
        /// Examples:
        ///   GetPoints('A') → 1
        ///   GetPoints('E') → 1
        ///   GetPoints('K') → 5
        ///   GetPoints('Q') → 10
        ///   GetPoints('Z') → 10
        /// </summary>
        public static int GetPoints(char letter)
        {
            char upper = char.ToUpper(letter);
            return _pointValues.TryGetValue(upper, out int pts) ? pts : 0;
        }

        /// <summary>
        /// Returns the tile count for a given letter in the fun-first distribution.
        /// Returns 0 if the letter is not found.
        /// </summary>
        public static int GetTileCount(char letter)
        {
            char upper = char.ToUpper(letter);
            return _tileCounts.TryGetValue(upper, out int count) ? count : 0;
        }

        /// <summary>
        /// Returns a copy of the tile distribution dictionary.
        /// Keys are uppercase letters. Used by callers that need the full distribution.
        /// </summary>
        public static Dictionary<char, int> GetDistribution()
        {
            return new Dictionary<char, int>(_tileCounts);
        }

        /// <summary>
        /// Total number of tiles across all letters in the distribution.
        /// </summary>
        public static int TotalTileCount
        {
            get
            {
                int total = 0;
                foreach (var kv in _tileCounts)
                    total += kv.Value;
                return total; // Should be ~120 tiles
            }
        }
    }
}
