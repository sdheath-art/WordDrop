using System.Collections.Generic;

namespace WordDrop
{
    /// <summary>
    /// Letter point values and tile distribution counts.
    ///
    /// Point value system (inverse-frequency tiered, updated 2026-04-18):
    ///   Points map inversely to English letter frequency in cleaner tiers than
    ///   Scrabble. Fixes Scrabble anomalies (H was 4 despite 6% frequency;
    ///   K was 5 despite 0.77% frequency) and eliminates the 10-letter 1-pt
    ///   pileup that flattened scoring on 3-letter words.
    ///
    ///   1 pt  (12%+ freq):   E, T
    ///   2 pts (6-9% freq):   A, H, I, N, O, S
    ///   3 pts (4-6% freq):   D, L, R
    ///   4 pts (2-3% freq):   C, F, M, U, W
    ///   5 pts (~2% freq):    G, P, Y
    ///   6 pts (~1.5% freq):  B
    ///   7 pts (~1% freq):    V
    ///   8 pts (~0.8% freq):  K
    ///   9 pts (~0.15% freq): J, X
    ///   10 pts (~0.1% freq): Q, Z
    ///
    /// Distribution (_tileCounts) is unchanged — the fun-first vowel-boosted
    /// bag keeps WordDrop playable. Only base-letter scoring was rebalanced.
    ///
    /// GetPoints(), GetTileCount(), GetDistribution() signatures are unchanged.
    /// </summary>
    public static class LetterData
    {
        // ---------------------------------------------------------------------------
        // Inverse-frequency point values
        // ---------------------------------------------------------------------------

        private static readonly Dictionary<char, int> _pointValues = new Dictionary<char, int>
        {
            // 1-point letters — most common (12%+ frequency)
            { 'E', 1 }, { 'T', 1 },

            // 2-point letters (6-9% frequency)
            { 'A', 2 }, { 'H', 2 }, { 'I', 2 }, { 'N', 2 }, { 'O', 2 }, { 'S', 2 },

            // 3-point letters (4-6% frequency)
            { 'D', 3 }, { 'L', 3 }, { 'R', 3 },

            // 4-point letters (2-3% frequency)
            { 'C', 4 }, { 'F', 4 }, { 'M', 4 }, { 'U', 4 }, { 'W', 4 },

            // 5-point letters (~2% frequency)
            { 'G', 5 }, { 'P', 5 }, { 'Y', 5 },

            // 6-point letters (~1.5% frequency)
            { 'B', 6 },

            // 7-point letters (~1% frequency)
            { 'V', 7 },

            // 8-point letters (~0.8% frequency)
            { 'K', 8 },

            // 9-point letters (~0.15% frequency)
            { 'J', 9 }, { 'X', 9 },

            // 10-point letters (~0.1% frequency)
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
