using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// PlayerPrefs-backed per-level progress: stars, best score, attempt count,
    /// completion flag, plus a global highest-unlocked counter.
    ///
    /// Matches the wd_ prefix used by HighScoreManager.cs.
    /// Upgrade path (v1.1): replace PlayerPrefs with cloud save — all callers go through this class.
    /// </summary>
    public static class LevelProgressManager
    {
        private const string STARS_KEY_PREFIX       = "wd_level_";
        private const string STARS_KEY_SUFFIX       = "_stars";
        private const string BEST_SCORE_SUFFIX      = "_best_score";
        private const string ATTEMPTS_SUFFIX        = "_attempts";
        private const string COMPLETED_SUFFIX       = "_completed";
        private const string HIGHEST_UNLOCKED_KEY   = "wd_levels_highest_unlocked";

        // ── Stars ───────────────────────────────────────────────────────────────

        public static int GetStars(int levelId)
        {
            return PlayerPrefs.GetInt(StarsKey(levelId), 0);
        }

        /// <summary>Only updates if stars > current. Never demotes.</summary>
        public static void SetStarsIfHigher(int levelId, int stars)
        {
            int current = GetStars(levelId);
            if (stars > current)
            {
                PlayerPrefs.SetInt(StarsKey(levelId), Mathf.Clamp(stars, 0, 3));
                PlayerPrefs.Save();
            }
        }

        // ── Best score ──────────────────────────────────────────────────────────

        public static int GetBestScore(int levelId)
        {
            return PlayerPrefs.GetInt(BestScoreKey(levelId), 0);
        }

        public static void SetBestScoreIfHigher(int levelId, int score)
        {
            int current = GetBestScore(levelId);
            if (score > current)
            {
                PlayerPrefs.SetInt(BestScoreKey(levelId), score);
                PlayerPrefs.Save();
            }
        }

        // ── Attempts ────────────────────────────────────────────────────────────

        public static int GetAttempts(int levelId)
        {
            return PlayerPrefs.GetInt(AttemptsKey(levelId), 0);
        }

        public static void IncrementAttempts(int levelId)
        {
            int current = GetAttempts(levelId);
            PlayerPrefs.SetInt(AttemptsKey(levelId), current + 1);
            PlayerPrefs.Save();
        }

        // ── Completion flag ─────────────────────────────────────────────────────

        public static bool IsCompleted(int levelId)
        {
            return PlayerPrefs.GetInt(CompletedKey(levelId), 0) == 1;
        }

        public static void MarkCompleted(int levelId)
        {
            PlayerPrefs.SetInt(CompletedKey(levelId), 1);
            PlayerPrefs.Save();
        }

        // ── Unlock chain ────────────────────────────────────────────────────────

        public static int GetHighestUnlocked()
        {
            return PlayerPrefs.GetInt(HIGHEST_UNLOCKED_KEY, 1);
        }

        public static void UnlockThrough(int levelId)
        {
            int current = GetHighestUnlocked();
            if (levelId > current)
            {
                PlayerPrefs.SetInt(HIGHEST_UNLOCKED_KEY, levelId);
                PlayerPrefs.Save();
            }
        }

        // ── Debug / reset ───────────────────────────────────────────────────────

        /// <summary>Clears all per-level progress and global unlock state. Debug menu only.</summary>
        public static void ResetAll(int maxLevelIdToScan = 200)
        {
            for (int i = 1; i <= maxLevelIdToScan; i++)
            {
                PlayerPrefs.DeleteKey(StarsKey(i));
                PlayerPrefs.DeleteKey(BestScoreKey(i));
                PlayerPrefs.DeleteKey(AttemptsKey(i));
                PlayerPrefs.DeleteKey(CompletedKey(i));
            }
            PlayerPrefs.DeleteKey(HIGHEST_UNLOCKED_KEY);
            PlayerPrefs.Save();
            Debug.Log("[LevelProgressManager] All level progress reset.");
        }

        // ── Key builders ────────────────────────────────────────────────────────

        private static string StarsKey(int levelId)      => STARS_KEY_PREFIX + levelId + STARS_KEY_SUFFIX;
        private static string BestScoreKey(int levelId)  => STARS_KEY_PREFIX + levelId + BEST_SCORE_SUFFIX;
        private static string AttemptsKey(int levelId)   => STARS_KEY_PREFIX + levelId + ATTEMPTS_SUFFIX;
        private static string CompletedKey(int levelId)  => STARS_KEY_PREFIX + levelId + COMPLETED_SUFFIX;
    }
}
