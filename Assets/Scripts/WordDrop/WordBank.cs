using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Provides a curated list of four-letter words and a method to pick
    /// one at random (without immediate repetition).
    /// </summary>
    public static class WordBank
    {
        // 20 common four-letter words — all uppercase for consistency
        private static readonly string[] _words = new string[]
        {
            "CALM",
            "BOLD",
            "FIRE",
            "GLOW",
            "HELP",
            "JUMP",
            "KEEN",
            "LAND",
            "MILD",
            "NOOK",
            "OPEN",
            "PLAY",
            "QUIZ",
            "RISE",
            "SAIL",
            "TIDE",
            "UNIT",
            "VAST",
            "WARM",
            "ZEAL"
        };

        private static string _lastWord = string.Empty;

        /// <summary>
        /// Returns a random four-letter word from the bank.
        /// Avoids repeating the immediately previous word (as long as the
        /// bank has more than one entry).
        /// </summary>
        public static string GetRandomWord()
        {
            if (_words.Length == 1) return _words[0];

            string picked;
            int safetyMax = 50;
            int attempts  = 0;

            do
            {
                picked = _words[Random.Range(0, _words.Length)];
                attempts++;
            }
            while (picked == _lastWord && attempts < safetyMax);

            _lastWord = picked;
            Debug.Log($"[WordBank] Selected word: {picked}");
            return picked;
        }

        /// <summary>
        /// Returns all words in the bank (read-only copy).
        /// </summary>
        public static string[] AllWords => (string[])_words.Clone();

        /// <summary>Total number of words in the bank.</summary>
        public static int Count => _words.Length;
    }
}
