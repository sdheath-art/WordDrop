using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// 5-heart life system with 30-minute regeneration per heart. PlayerPrefs-backed.
    ///
    /// State stored:
    ///   wd_hearts_count             — stored heart count when _lastRegenTicks was last set
    ///   wd_hearts_last_regen_ticks  — UTC ticks marking the anchor for regen math
    ///
    /// Current hearts are COMPUTED lazily from stored count + elapsed ticks, never
    /// advanced on a timer. This avoids needing a running update loop.
    ///
    /// Wired into Level Start / Out of Moves Retry in Phase 7.
    /// </summary>
    public static class HeartsManager
    {
        public const int MAX_HEARTS = 5;
        public const int REGEN_SECONDS_PER_HEART = 30 * 60; // 30 min

        private const string COUNT_KEY    = "wd_hearts_count";
        private const string LAST_REGEN_KEY = "wd_hearts_last_regen_ticks";

        private static readonly TimeSpan RegenInterval = TimeSpan.FromSeconds(REGEN_SECONDS_PER_HEART);

        /// <summary>Current heart count, accounting for regen since last write. Always 0..MAX_HEARTS.</summary>
        public static int Current
        {
            get
            {
                SyncRegen();
                return PlayerPrefs.GetInt(COUNT_KEY, MAX_HEARTS);
            }
        }

        public static bool IsFull => Current >= MAX_HEARTS;

        /// <summary>Returns false if no hearts available; otherwise decrements and persists.</summary>
        public static bool Consume()
        {
            SyncRegen();
            int current = PlayerPrefs.GetInt(COUNT_KEY, MAX_HEARTS);
            if (current <= 0) return false;

            // If we were at MAX, start the regen clock now — otherwise leave it alone.
            if (current >= MAX_HEARTS)
            {
                PlayerPrefs.SetString(LAST_REGEN_KEY, DateTime.UtcNow.Ticks.ToString());
            }

            PlayerPrefs.SetInt(COUNT_KEY, current - 1);
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>Time until next heart regenerates. Zero if already full.</summary>
        public static TimeSpan TimeUntilNextHeart
        {
            get
            {
                SyncRegen();
                int current = PlayerPrefs.GetInt(COUNT_KEY, MAX_HEARTS);
                if (current >= MAX_HEARTS) return TimeSpan.Zero;

                long anchorTicks = ReadAnchorTicks();
                DateTime anchor = new DateTime(anchorTicks, DateTimeKind.Utc);
                DateTime nextHeartAt = anchor + RegenInterval;
                TimeSpan remaining = nextHeartAt - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        public static void GrantFull()
        {
            PlayerPrefs.SetInt(COUNT_KEY, MAX_HEARTS);
            PlayerPrefs.DeleteKey(LAST_REGEN_KEY);
            PlayerPrefs.Save();
        }

        /// <summary>
        /// Adds exactly one heart (capped at MAX_HEARTS). If the grant reaches MAX
        /// the regen anchor is cleared; otherwise the existing anchor is preserved
        /// so in-progress regen isn't reset. Used by the rewarded-ad +1 path.
        /// </summary>
        public static void GrantOne()
        {
            SyncRegen();
            int current = PlayerPrefs.GetInt(COUNT_KEY, MAX_HEARTS);
            if (current >= MAX_HEARTS) return;
            int next = current + 1;
            PlayerPrefs.SetInt(COUNT_KEY, next);
            if (next >= MAX_HEARTS)
                PlayerPrefs.DeleteKey(LAST_REGEN_KEY);
            PlayerPrefs.Save();
        }

        public static void ResetAll()
        {
            PlayerPrefs.DeleteKey(COUNT_KEY);
            PlayerPrefs.DeleteKey(LAST_REGEN_KEY);
            PlayerPrefs.Save();
        }

        // ── Internal ────────────────────────────────────────────────────────────

        /// <summary>
        /// Advances stored heart count based on elapsed time since the anchor.
        /// Pushes anchor forward by exactly N * regenInterval (N = hearts regenerated)
        /// so partial progress toward the next heart is preserved.
        /// </summary>
        private static void SyncRegen()
        {
            int current = PlayerPrefs.GetInt(COUNT_KEY, MAX_HEARTS);
            if (current >= MAX_HEARTS) return;

            long anchorTicks = ReadAnchorTicks();
            DateTime anchor = new DateTime(anchorTicks, DateTimeKind.Utc);
            TimeSpan elapsed = DateTime.UtcNow - anchor;
            if (elapsed <= TimeSpan.Zero) return;

            int regenerated = (int)(elapsed.TotalSeconds / REGEN_SECONDS_PER_HEART);
            if (regenerated <= 0) return;

            int next = Mathf.Min(MAX_HEARTS, current + regenerated);
            PlayerPrefs.SetInt(COUNT_KEY, next);

            if (next >= MAX_HEARTS)
            {
                PlayerPrefs.DeleteKey(LAST_REGEN_KEY);
            }
            else
            {
                DateTime newAnchor = anchor + TimeSpan.FromSeconds(regenerated * REGEN_SECONDS_PER_HEART);
                PlayerPrefs.SetString(LAST_REGEN_KEY, newAnchor.Ticks.ToString());
            }
            PlayerPrefs.Save();
        }

        private static long ReadAnchorTicks()
        {
            string s = PlayerPrefs.GetString(LAST_REGEN_KEY, "");
            if (string.IsNullOrEmpty(s) || !long.TryParse(s, out long ticks))
            {
                // Never consumed yet — anchor to now so first regen is a full interval away.
                ticks = DateTime.UtcNow.Ticks;
                PlayerPrefs.SetString(LAST_REGEN_KEY, ticks.ToString());
            }
            return ticks;
        }
    }
}
