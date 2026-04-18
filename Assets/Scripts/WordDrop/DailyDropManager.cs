using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages the Daily Drop mode — a once-per-day seeded solo puzzle.
    /// Same seed for all players on the same calendar day.
    ///
    /// PlayerPrefs keys:
    ///   "daily_last_played"      — date string (YYYYMMDD) of last play
    ///   "daily_streak"           — consecutive days played
    ///   "daily_best_YYYYMMDD"    — best score for that specific day
    ///   "daily_launch_date"      — first-ever daily play date (for puzzle numbering)
    /// </summary>
    public static class DailyDropManager
    {
        // ── Global mode flag ────────────────────────────────────────────────────

        /// <summary>True while a Daily Drop game is active.</summary>
        public static bool IsDailyMode { get; set; } = false;

        /// <summary>Turn limit for Daily Drop (shorter than classic).</summary>
        public const int DAILY_TURNS = 6;

        // ── Date helpers ────────────────────────────────────────────────────────

        private static string TodayString()
        {
            return DateTime.UtcNow.ToString("yyyyMMdd");
        }

        private static int TodayInt()
        {
            return int.Parse(TodayString());
        }

        // ── Seed ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a deterministic seed based on today's date.
        /// Same value for every player on the same calendar day.
        /// </summary>
        public static int GetDailySeed()
        {
            return TodayInt();
        }

        // ── Puzzle number ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the puzzle number (days since launch date).
        /// If no launch date is stored yet, stores today as day 1.
        /// </summary>
        public static int GetPuzzleNumber()
        {
            string launchStr = PlayerPrefs.GetString("daily_launch_date", "");
            DateTime launchDate;

            if (string.IsNullOrEmpty(launchStr) ||
                !DateTime.TryParseExact(launchStr, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out launchDate))
            {
                // First ever daily play — store today as launch date
                launchStr = TodayString();
                PlayerPrefs.SetString("daily_launch_date", launchStr);
                PlayerPrefs.Save();
                return 1;
            }

            int days = (DateTime.UtcNow.Date - launchDate.Date).Days;
            return Mathf.Max(1, days + 1);
        }

        // ── Play tracking ───────────────────────────────────────────────────────

        /// <summary>True if the player has already completed today's Daily Drop.</summary>
        public static bool HasPlayedToday()
        {
            return PlayerPrefs.GetString("daily_last_played", "") == TodayString();
        }

        /// <summary>
        /// Records today's play and updates the streak.
        /// Call this when the daily game ends.
        /// </summary>
        /// <returns>True if this is a new daily best.</returns>
        public static bool MarkPlayedToday(int score)
        {
            string today = TodayString();
            string lastPlayed = PlayerPrefs.GetString("daily_last_played", "");

            // Update streak
            if (IsYesterday(lastPlayed))
            {
                // Consecutive day — increment streak
                int streak = PlayerPrefs.GetInt("daily_streak", 0) + 1;
                PlayerPrefs.SetInt("daily_streak", streak);
            }
            else if (lastPlayed != today)
            {
                // Not consecutive and not already played today — reset to 1
                PlayerPrefs.SetInt("daily_streak", 1);
            }
            // If lastPlayed == today, streak stays unchanged (replay/update score)

            // Save the play date
            PlayerPrefs.SetString("daily_last_played", today);

            // Save score (keep best for the day)
            string scoreKey = $"daily_best_{today}";
            int prevBest = PlayerPrefs.GetInt(scoreKey, 0);
            if (score > prevBest)
            {
                PlayerPrefs.SetInt(scoreKey, score);
//                 Debug.Log($"[DailyDropManager] New daily best: {score} (was {prevBest})");
            }

            // Submit to HighScoreManager for mode "daily"
            bool isNewBest = HighScoreManager.Submit(score, "daily");

            // Ensure launch date is set
            if (string.IsNullOrEmpty(PlayerPrefs.GetString("daily_launch_date", "")))
            {
                PlayerPrefs.SetString("daily_launch_date", today);
            }

            PlayerPrefs.Save();

//             Debug.Log($"[DailyDropManager] MarkPlayedToday: score={score} streak={GetStreak()} " +
                      // $"puzzle=#{GetPuzzleNumber()} newBest={isNewBest}");

            return isNewBest;
        }

        // ── Streak ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the current streak (consecutive days played).
        /// If the player didn't play yesterday and hasn't played today, returns 0.
        /// </summary>
        public static int GetStreak()
        {
            string lastPlayed = PlayerPrefs.GetString("daily_last_played", "");
            string today = TodayString();

            // Currently on a streak if last played is today or yesterday
            if (lastPlayed == today || IsYesterday(lastPlayed))
                return PlayerPrefs.GetInt("daily_streak", 0);

            // Streak broken — haven't played recently
            return 0;
        }

        // ── Today's best ────────────────────────────────────────────────────────

        /// <summary>Returns the best score for today's puzzle, or 0 if not played.</summary>
        public static int GetTodayBest()
        {
            return PlayerPrefs.GetInt($"daily_best_{TodayString()}", 0);
        }

        // ── Share text ──────────────────────────────────────────────────────────

        /// <summary>
        /// Generates a Wordle-style shareable text summary.
        /// </summary>
        public static string GenerateShareText(int score, int streak)
        {
            int puzzleNum = GetPuzzleNumber();
            string streakText = streak > 1 ? $"\nStreak: {streak} days" : "";
            int best = HighScoreManager.GetBest("daily");

            string bestText = "";
            if (score >= best && best > 0)
                bestText = " (NEW BEST!)";

            return $"WordDrop Daily #{puzzleNum}\n" +
                   $"Score: {score}{bestText}" +
                   streakText +
                   $"\n\nwordrop.game";
        }

        // ── Debug ───────────────────────────────────────────────────────────────

        /// <summary>Resets daily state so it can be replayed today. Debug only.</summary>
        public static void ResetDaily()
        {
            PlayerPrefs.DeleteKey("daily_last_played");
            PlayerPrefs.DeleteKey("daily_streak");
            PlayerPrefs.DeleteKey($"daily_best_{TodayString()}");
            PlayerPrefs.Save();
//             Debug.Log("[DailyDropManager] Daily reset — can replay today's puzzle.");
        }

        // ── Internal helpers ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the given date string (YYYYMMDD) is yesterday.
        /// </summary>
        private static bool IsYesterday(string dateStr)
        {
            if (string.IsNullOrEmpty(dateStr)) return false;

            DateTime date;
            if (!DateTime.TryParseExact(dateStr, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out date))
                return false;

            DateTime yesterday = DateTime.UtcNow.Date.AddDays(-1);
            return date.Date == yesterday;
        }

        /// <summary>Resets all daily data. For debug/testing only.</summary>
        public static void ResetAll()
        {
            PlayerPrefs.DeleteKey("daily_last_played");
            PlayerPrefs.DeleteKey("daily_streak");
            PlayerPrefs.DeleteKey("daily_launch_date");
            HighScoreManager.Submit(0, "daily"); // won't save (0 never beats)
            PlayerPrefs.Save();
//             Debug.Log("[DailyDropManager] All daily data reset.");
        }
    }
}
