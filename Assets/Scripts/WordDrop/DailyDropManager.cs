using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages the Daily Drop mode — a once-per-day seeded solo puzzle.
    /// Same seed for all players on the same calendar day.
    ///
    /// PlayerPrefs keys:
    ///   "daily_last_played"            — date string (YYYYMMDD) of last play
    ///   "daily_streak"                 — consecutive days played
    ///   "daily_best_YYYYMMDD"          — best score for that specific day
    ///   "daily_launch_date"            — first-ever daily play date (for puzzle numbering)
    ///   "daily_milestone_claimed_{N}"  — one-shot claim flag per milestone streak value (3/7/14/30)
    ///   "daily_save_streak_last_ticks" — UTC ticks of last save-streak ad use (1×/week cap)
    /// </summary>
    public static class DailyDropManager
    {
        // ── Global mode flag ────────────────────────────────────────────────────

        /// <summary>True while a Daily Drop game is active.</summary>
        public static bool IsDailyMode { get; set; } = false;

        /// <summary>Turn limit for Daily Drop (shorter than classic).</summary>
        public const int DAILY_TURNS = 6;

        // ── Phase 8 config ──────────────────────────────────────────────────────

        /// <summary>
        /// Streak milestones that award bonus coins the first time they're reached.
        /// Keep array sorted ascending — ClaimMilestoneBonusIfDue walks descending.
        /// </summary>
        public static readonly int[] STREAK_MILESTONES = { 3, 7, 14, 30 };

        /// <summary>Coin reward per milestone, index-aligned with STREAK_MILESTONES.</summary>
        public static readonly int[] MILESTONE_REWARDS = { 25, 75, 200, 500 };

        /// <summary>Pool of level IDs to rotate through for daily puzzles until Phase 10 ships real daily content.</summary>
        private static readonly int[] DAILY_LEVEL_POOL = { 1, 2, 3 };

        /// <summary>Save-streak rewarded ad is capped to once per rolling 7 days.</summary>
        private const double SAVE_STREAK_COOLDOWN_SECONDS = 7 * 24 * 60 * 60;

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
        /// Returns a deterministic seed based on today's UTC date. Same value for
        /// every player on the same UTC calendar day (shared global puzzle).
        /// Phase 8 spec: DateTime.UtcNow.ToString("yyyy-MM-dd").GetHashCode().
        /// </summary>
        public static int GetDailySeed()
        {
            return DateTime.UtcNow.ToString("yyyy-MM-dd").GetHashCode();
        }

        // ── Daily → level mapping (Phase 8) ────────────────────────────────────

        /// <summary>
        /// Picks which Level JSON to use for today's daily puzzle. Deterministic
        /// per-day via the seed so every player gets the same level. Rotates
        /// through DAILY_LEVEL_POOL — Phase 10 replaces the pool with real daily
        /// level content.
        /// </summary>
        public static int GetDailyLevelId()
        {
            if (DAILY_LEVEL_POOL == null || DAILY_LEVEL_POOL.Length == 0) return 1;
            // Use unsigned modulo so negative hash codes don't map out of range.
            uint seed = (uint)GetDailySeed();
            return DAILY_LEVEL_POOL[seed % (uint)DAILY_LEVEL_POOL.Length];
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

        // ── Streak milestones (Phase 8) ────────────────────────────────────────

        /// <summary>
        /// Returns the coin reward for reaching <paramref name="streak"/> the first
        /// time, or 0 if the milestone has already been claimed (or isn't a milestone
        /// value). Persists a one-shot flag per milestone so subsequent streak passes
        /// through the same threshold don't re-pay.
        /// </summary>
        public static int ClaimMilestoneBonusIfDue(int streak)
        {
            if (streak <= 0) return 0;
            for (int i = STREAK_MILESTONES.Length - 1; i >= 0; i--)
            {
                int threshold = STREAK_MILESTONES[i];
                if (streak < threshold) continue;
                string key = $"daily_milestone_claimed_{threshold}";
                if (PlayerPrefs.GetInt(key, 0) != 0) return 0;
                PlayerPrefs.SetInt(key, 1);
                PlayerPrefs.Save();
                return MILESTONE_REWARDS[i];
            }
            return 0;
        }

        /// <summary>Returns true if <paramref name="streak"/> matches a milestone threshold.</summary>
        public static bool IsMilestoneStreak(int streak)
        {
            if (streak <= 0) return false;
            for (int i = 0; i < STREAK_MILESTONES.Length; i++)
                if (STREAK_MILESTONES[i] == streak) return true;
            return false;
        }

        // ── Save streak (Phase 8) ──────────────────────────────────────────────

        /// <summary>
        /// True when a save-streak rewarded ad is eligible: current streak > 0,
        /// the player missed yesterday (so the streak WILL reset on next play),
        /// and the 7-day cooldown has elapsed since the last save-streak use.
        /// </summary>
        public static bool CanSaveStreak()
        {
            int storedStreak = PlayerPrefs.GetInt("daily_streak", 0);
            if (storedStreak <= 0) return false;

            string lastPlayed = PlayerPrefs.GetString("daily_last_played", "");
            if (string.IsNullOrEmpty(lastPlayed)) return false;
            string today = TodayString();
            if (lastPlayed == today) return false;      // already played today — nothing to save
            if (IsYesterday(lastPlayed)) return false;  // still consecutive — no save needed

            // Cooldown check (rolling 7 days).
            string lastSaveStr = PlayerPrefs.GetString("daily_save_streak_last_ticks", "");
            if (!string.IsNullOrEmpty(lastSaveStr) && long.TryParse(lastSaveStr, out long lastTicks))
            {
                DateTime lastUse = new DateTime(lastTicks, DateTimeKind.Utc);
                if ((DateTime.UtcNow - lastUse).TotalSeconds < SAVE_STREAK_COOLDOWN_SECONDS)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Restores the streak by back-dating last-played to yesterday, so the
        /// next MarkPlayedToday increments rather than resetting. Stamps the
        /// cooldown. No-op if CanSaveStreak is false.
        /// </summary>
        public static bool RestoreStreakWithAd()
        {
            if (!CanSaveStreak()) return false;
            string yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyyMMdd");
            PlayerPrefs.SetString("daily_last_played", yesterday);
            PlayerPrefs.SetString("daily_save_streak_last_ticks", DateTime.UtcNow.Ticks.ToString());
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>
        /// Remaining cooldown (TimeSpan.Zero if ready). Used by UI to show
        /// "Save-Streak available in Nd" when the cap is active.
        /// </summary>
        public static TimeSpan TimeUntilSaveStreakReady()
        {
            string s = PlayerPrefs.GetString("daily_save_streak_last_ticks", "");
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out long ticks))
                return TimeSpan.Zero;
            DateTime last = new DateTime(ticks, DateTimeKind.Utc);
            TimeSpan elapsed = DateTime.UtcNow - last;
            TimeSpan cooldown = TimeSpan.FromSeconds(SAVE_STREAK_COOLDOWN_SECONDS);
            return elapsed >= cooldown ? TimeSpan.Zero : cooldown - elapsed;
        }

        // ── Debug ───────────────────────────────────────────────────────────────

        /// <summary>Resets daily state so it can be replayed today. Debug only.</summary>
        public static void ResetDaily()
        {
            PlayerPrefs.DeleteKey("daily_last_played");
            PlayerPrefs.DeleteKey("daily_streak");
            PlayerPrefs.DeleteKey($"daily_best_{TodayString()}");
            for (int i = 0; i < STREAK_MILESTONES.Length; i++)
                PlayerPrefs.DeleteKey($"daily_milestone_claimed_{STREAK_MILESTONES[i]}");
            PlayerPrefs.DeleteKey("daily_save_streak_last_ticks");
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Debug: back-date lastPlayed by <paramref name="days"/> so the next play
        /// either continues the streak (days=1, counts as yesterday), resets it
        /// (days>=2, counts as missed), or simulates a specific missed-day offset.
        /// </summary>
        public static void DebugBackdateLastPlayed(int days)
        {
            if (days <= 0) return;
            string target = DateTime.UtcNow.Date.AddDays(-days).ToString("yyyyMMdd");
            PlayerPrefs.SetString("daily_last_played", target);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Debug: inject a streak value AND back-date last-played to yesterday so
        /// the next MarkPlayedToday legitimately increments (setStreak=29 + play
        /// → streak=30, milestone triggers). Without the back-date, MarkPlayedToday
        /// sees lastPlayed as blank/stale and resets the streak to 1.
        /// </summary>
        public static void DebugSetStreak(int streak)
        {
            int clamped = Mathf.Max(0, streak);
            PlayerPrefs.SetInt("daily_streak", clamped);
            if (clamped > 0)
            {
                // Mark "played yesterday" so today's play continues the streak.
                string yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyyMMdd");
                PlayerPrefs.SetString("daily_last_played", yesterday);
            }
            PlayerPrefs.Save();
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
            PlayerPrefs.DeleteKey("daily_save_streak_last_ticks");
            for (int i = 0; i < STREAK_MILESTONES.Length; i++)
                PlayerPrefs.DeleteKey($"daily_milestone_claimed_{STREAK_MILESTONES[i]}");
            HighScoreManager.Submit(0, "daily"); // won't save (0 never beats)
            PlayerPrefs.Save();
        }
    }
}
