using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Fun-first weighted letter bag — no wild tiles, guaranteed vowel-rich draws.
    ///
    /// Distribution philosophy:
    ///   - Vowels (A,E,I,O,U) appear ~40% of draws (heavily boosted vs Scrabble)
    ///   - Common consonants (R,S,T,L,N,D,G,M,P,C) appear frequently
    ///   - Rare letters (Q,Z,J,X,K,V,W) appear rarely but are present
    ///   - No wild tiles ever returned — DrawLetter() always returns A-Z
    ///
    /// WILD_CHAR is kept as a compile-time constant for backward compatibility
    /// with existing callers (AIAgent, HandManager, Tile). It is never drawn.
    /// IsWild() always returns false for any real letter.
    ///
    /// This is a plain C# class (not MonoBehaviour) — instantiate directly.
    /// </summary>
    public class TileBag
    {
        // ---------------------------------------------------------------------------
        // Backward-compat sentinel — kept so existing code compiles,
        // but NEVER returned by DrawLetter() or DrawRealLetter().
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Legacy wild-tile sentinel. Never drawn in this version.
        /// Kept only so existing callers (AIAgent, HandManager, Tile) compile.
        /// </summary>
        public const char WILD_CHAR = '*';

        // ---------------------------------------------------------------------------
        // Fun-first weighted distribution
        //
        // Design targets (per full bag of ~120 tiles):
        //   Vowels total:      ~48 tiles  (~40%)
        //   Common consonants: ~54 tiles  (~45%)
        //   Uncommon letters:  ~12 tiles  (~10%)
        //   Rare letters:       ~6 tiles  (~5%)
        //
        // Each entry: (letter, count)
        // ---------------------------------------------------------------------------

        private static readonly (char letter, int count)[] _distribution = new (char, int)[]
        {
            // ── Vowels (~40% total) ──────────────────────────────────────────────
            ('A',  9),   // common but capped to prevent flooding
            ('E', 13),   // most frequent letter overall
            ('I',  9),
            ('O',  8),
            ('U',  4),

            // ── High-frequency consonants (boosted for word-rich play) ──────────
            ('R',  7),   // key connector
            ('S',  7),   // plurals, word endings
            ('T',  7),   // common but capped to prevent flooding
            ('L',  6),
            ('N',  6),   // common
            ('D',  5),   // past tense, common words
            ('H',  5),   // THE, HE, HER, etc
            ('M',  4),
            ('P',  4),
            ('C',  4),

            // ── Medium-frequency consonants ──────────────────────────────────────
            ('B',  3),
            ('F',  3),
            ('G',  3),
            ('W',  3),
            ('Y',  3),

            // ── Low-frequency letters (reduced to prevent ugly clustering) ──────
            ('V',  1),   // was 2
            ('K',  2),

            // ── Rare letters (minimal — present but very uncommon) ──────────────
            ('J',  1),
            ('X',  1),
            ('Q',  1),
            ('Z',  1),
        };

        // ---------------------------------------------------------------------------
        // Internal state
        // ---------------------------------------------------------------------------

        private List<char> _bag       = new List<char>(128);
        private int        _totalDrawn = 0;
        private int        _refillCount = 0;

        /// <summary>
        /// When non-null, this seeded System.Random is used instead of UnityEngine.Random
        /// to ensure deterministic, cross-device reproducible letter sequences.
        /// </summary>
        private System.Random _seededRng = null;

        // ---------------------------------------------------------------------------
        // Constructors
        // ---------------------------------------------------------------------------

        /// <summary>Default constructor — uses UnityEngine.Random (non-deterministic).</summary>
        public TileBag()
        {
            Refill();
        }

        /// <summary>
        /// Seeded constructor — uses System.Random with the given seed.
        /// Produces identical letter sequences for the same seed on every device.
        /// Used by Daily Drop mode for fair, reproducible puzzles.
        /// </summary>
        public TileBag(int seed)
        {
            _seededRng = new System.Random(seed);
            Refill();
        }

        // ---------------------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------------------

        /// <summary>Number of tiles currently remaining in the bag.</summary>
        public int Count => _bag.Count;

        /// <summary>Total letters drawn since this bag was created (across all refills).</summary>
        public int TotalDrawn => _totalDrawn;

        /// <summary>Number of times the bag has been refilled (first fill = 0 refills).</summary>
        public int RefillCount => _refillCount - 1; // first fill doesn't count as a refill

        /// <summary>
        /// If set, the next DrawLetter() call returns this letter instead of drawing from the bag.
        /// Automatically clears after one use. Used by the tutorial to rig draws.
        /// </summary>
        private char _forcedNextDraw = '\0';

        /// <summary>
        /// Forces the next DrawLetter() call to return the given letter.
        /// One-shot: automatically clears after the draw.
        /// </summary>
        public void ForceNextDraw(char letter)
        {
            _forcedNextDraw = char.ToUpper(letter);
            Debug.Log($"[TileBag] Next draw forced to '{_forcedNextDraw}'");
        }

        /// <summary>
        /// Draws a single letter from the bag.
        /// ALWAYS returns a valid A-Z character — never returns WILD_CHAR ('*').
        /// If the bag is empty, it automatically refills first.
        /// </summary>
        public char DrawLetter()
        {
            if (_forcedNextDraw != '\0')
            {
                char forced = _forcedNextDraw;
                _forcedNextDraw = '\0';
                _totalDrawn++;
                return forced;
            }

            if (_bag.Count == 0)
            {
                Debug.Log("[TileBag] Bag empty — refilling.");
                Refill();
            }

            int  lastIndex = _bag.Count - 1;
            char letter    = _bag[lastIndex];
            _bag.RemoveAt(lastIndex);

            _totalDrawn++;
            return letter;
        }

        /// <summary>
        /// Draws a real letter (same as DrawLetter in this version — no wilds exist).
        /// Kept for API compatibility with HandManager's vowel-guarantee logic.
        /// </summary>
        public char DrawRealLetter()
        {
            return DrawLetter();
        }

        /// <summary>
        /// Refills the bag with a fresh weighted distribution and shuffles it.
        /// Called automatically when the bag runs empty.
        /// </summary>
        public void Refill()
        {
            _bag.Clear();

            int totalTiles = 0;
            foreach (var entry in _distribution)
            {
                for (int i = 0; i < entry.count; i++)
                    _bag.Add(entry.letter);
                totalTiles += entry.count;
            }

            if (_seededRng != null)
                ShuffleSeeded(_bag, _seededRng);
            else
                Shuffle(_bag);

            if (_refillCount == 0)
                Debug.Log($"[TileBag] Initial fill — {_bag.Count} tiles " +
                          $"(no wilds, vowel-rich distribution). " +
                          $"Total unique letters: {_distribution.Length}");
            else
                Debug.Log($"[TileBag] Refilled — {_bag.Count} tiles, refill #{_refillCount}.");

            _refillCount++;
        }

        /// <summary>
        /// Peeks at the next letter without removing it from the bag.
        /// Returns '\0' if the bag is empty.
        /// </summary>
        public char PeekNext()
        {
            if (_bag.Count == 0) return '\0';
            return _bag[_bag.Count - 1];
        }

        /// <summary>
        /// Always returns false in this version — no wild tiles exist.
        /// Kept for API compatibility.
        /// </summary>
        public static bool IsWild(char c)
        {
            return false; // No wilds in fun-first distribution
        }

        /// <summary>
        /// Returns a debug frequency report of letters currently in the bag.
        /// </summary>
        public string GetFrequencyReport()
        {
            var freq = new Dictionary<char, int>();
            foreach (char c in _bag)
            {
                if (!freq.ContainsKey(c)) freq[c] = 0;
                freq[c]++;
            }

            // Count vowels for verification
            int vowelCount = 0;
            foreach (var kv in freq)
                if ("AEIOU".IndexOf(kv.Key) >= 0) vowelCount += kv.Value;

            float vowelPct = _bag.Count > 0 ? (float)vowelCount / _bag.Count * 100f : 0f;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"TileBag — {_bag.Count} tiles remaining " +
                          $"(vowels: {vowelCount} = {vowelPct:F1}%):");

            // Sort by frequency descending for readability
            var sorted = new List<KeyValuePair<char, int>>(freq);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

            foreach (var kv in sorted)
                sb.AppendLine($"  {kv.Key}: {kv.Value}x  ({LetterData.GetPoints(kv.Key)} pts)");

            return sb.ToString();
        }

        /// <summary>
        /// Returns the total tile count across the full distribution.
        /// Useful for verifying vowel percentage.
        /// </summary>
        public static int GetFullBagSize()
        {
            int total = 0;
            foreach (var entry in _distribution)
                total += entry.count;
            return total;
        }

        /// <summary>
        /// Returns the total vowel count across the full distribution.
        /// </summary>
        public static int GetFullBagVowelCount()
        {
            int vowels = 0;
            foreach (var entry in _distribution)
                if ("AEIOU".IndexOf(entry.letter) >= 0)
                    vowels += entry.count;
            return vowels;
        }

        // ---------------------------------------------------------------------------
        // Fisher-Yates shuffle
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Shuffles using UnityEngine.Random (non-deterministic).
        /// Used when no seeded RNG is set.
        /// </summary>
        private static void Shuffle(List<char> list)
        {
            int n          = list.Count;
            int safetyMax  = n * 2 + 10;
            int iterations = 0;

            for (int i = n - 1; i > 0 && iterations < safetyMax; i--, iterations++)
            {
                int  j    = Random.Range(0, i + 1);
                char temp = list[i];
                list[i]   = list[j];
                list[j]   = temp;
            }
        }

        /// <summary>
        /// Shuffles using a seeded System.Random for deterministic, cross-device results.
        /// </summary>
        private static void ShuffleSeeded(List<char> list, System.Random rng)
        {
            int n = list.Count;
            for (int i = n - 1; i > 0; i--)
            {
                int  j    = rng.Next(0, i + 1);
                char temp = list[i];
                list[i]   = list[j];
                list[j]   = temp;
            }
        }
    }
}
