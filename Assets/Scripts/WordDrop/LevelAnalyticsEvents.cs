namespace WordDrop
{
    /// <summary>
    /// String constants for level-mode analytics events. Use with
    /// AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_START, "level_id", 1, ...).
    ///
    /// Convention: snake_case, matches existing events like "stage_clear" and "run_end".
    /// Kept separate from AnalyticsManager.cs so this file is additive-only.
    /// </summary>
    public static class LevelAnalyticsEvents
    {
        public const string LEVEL_START    = "level_start";
        public const string LEVEL_COMPLETE = "level_complete";
        public const string LEVEL_FAIL     = "level_fail";
        public const string LEVEL_RETRY    = "level_retry";
        public const string COINS_EARNED   = "coins_earned";
        public const string COINS_SPENT    = "coins_spent";
        public const string BOOSTER_USED   = "booster_used";
    }
}
