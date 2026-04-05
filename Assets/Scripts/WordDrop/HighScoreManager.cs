using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Persists personal-best scores per game mode via PlayerPrefs.
    /// Key format: "wd_best_{mode}_score"  (e.g. "wd_best_classic_score").
    /// Designed for future modes — just pass a different mode string.
    /// </summary>
    public static class HighScoreManager
    {
        private const string KEY_PREFIX = "wd_best_";
        private const string KEY_SUFFIX = "_score";
        private const string COMBO_SUFFIX = "_combo";

        /// <summary>Returns the PlayerPrefs key for the given mode.</summary>
        private static string Key(string mode) => $"{KEY_PREFIX}{mode}{KEY_SUFFIX}";
        private static string ComboKey(string mode) => $"{KEY_PREFIX}{mode}{COMBO_SUFFIX}";

        /// <summary>Get the stored best score for a mode. Returns 0 if none saved.</summary>
        public static int GetBest(string mode = "classic")
        {
            return PlayerPrefs.GetInt(Key(mode), 0);
        }

        /// <summary>
        /// Submit a final score. If it beats the stored best, saves it and returns true.
        /// Otherwise returns false.
        /// </summary>
        public static bool Submit(int score, string mode = "classic")
        {
            int prev = GetBest(mode);
            if (score > prev && score > 0)
            {
                PlayerPrefs.SetInt(Key(mode), score);
                PlayerPrefs.Save();
                Debug.Log($"[HighScoreManager] NEW BEST for {mode}: {score} (was {prev})");
                return true;
            }
            Debug.Log($"[HighScoreManager] Score {score} did not beat best {prev} for {mode}");
            return false;
        }

        // ── Best Combo (highest single-turn score) ──────────────────────────

        /// <summary>Get the stored best combo for a mode. Returns 0 if none saved.</summary>
        public static int GetBestCombo(string mode = "classic")
        {
            return PlayerPrefs.GetInt(ComboKey(mode), 0);
        }

        /// <summary>
        /// Submit a single-turn score. If it beats the stored best combo, saves and returns true.
        /// Call this after each turn resolves with the total points earned that turn.
        /// </summary>
        public static bool SubmitCombo(int turnScore, string mode = "classic")
        {
            int prev = GetBestCombo(mode);
            if (turnScore > prev && turnScore > 0)
            {
                PlayerPrefs.SetInt(ComboKey(mode), turnScore);
                PlayerPrefs.Save();
                Debug.Log($"[HighScoreManager] NEW BEST COMBO for {mode}: {turnScore} (was {prev})");
                return true;
            }
            return false;
        }

        /// <summary>Clears all stored high scores. Called from debug reset.</summary>
        public static void ResetAll()
        {
            // Clear known modes. Add future modes here.
            PlayerPrefs.DeleteKey(Key("classic"));
            PlayerPrefs.DeleteKey(Key("blitz"));
            PlayerPrefs.DeleteKey(ComboKey("classic"));
            PlayerPrefs.DeleteKey(ComboKey("blitz"));
            PlayerPrefs.Save();
            Debug.Log("[HighScoreManager] All high scores cleared.");
        }
    }
}
