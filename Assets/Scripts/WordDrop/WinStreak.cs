using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// WIN STREAK (2026-10-23 — Royal Match's streak; Spencer: "do we give them more rockets and stuff to start the level based on
    /// how long they have played?" -> a visible streak instead of hidden scaling). Win levels in a row and the next level STARTS
    /// with specials on the board: 1 win -> rocket, 2 -> rocket + bomb, 3+ -> rocket + bomb + disco. A loss the player doesn't
    /// rescue with a continue resets it (RM: "use Play On or lose your streak"). Saved in PlayerPrefs. ON = false switches it off.
    ///
    /// Level budgets are tuned WITHOUT a streak (research: tune with no boosters) — the streak is a bonus on top. The bot never
    /// has one (it plays through LevelRules.SetupSimLevel, which doesn't call Apply).
    /// </summary>
    public static class WinStreak
    {
        public static bool ON = true;
        /// <summary>Streak specials land at least this many cells from every other special (2026-10-23 Spencer: a disco and a bomb
        /// started side by side -> combo on move 1 left 2 gems: "cool but maybe too easy"). A combo stays possible — it costs swaps.</summary>
        public static int MIN_GAP = 3;
        /// <summary>First level where the streak places start specials (teaching levels never do).</summary>
        public static int FROM_LEVEL = 10;
        private const string PREF = "win_streak";
        public static int Count => PlayerPrefs.GetInt(PREF, 0);
        private static bool s_pendingLoss;

        public static void Win()
        {
            if (!ON) return;
            s_pendingLoss = false;
            PlayerPrefs.SetInt(PREF, Count + 1); PlayerPrefs.Save();
            WDLog.Log($"[Streak] win -> streak {Count}");
        }
        /// <summary>Out of moves. Not final yet — a continue (Play On) keeps the streak.</summary>
        public static void MarkLoss() { if (ON && Count > 0) { s_pendingLoss = true; WDLog.Log($"[Streak] out of moves — streak {Count} at risk"); } }
        public static void Rescued() { if (s_pendingLoss) WDLog.Log("[Streak] continue — streak kept"); s_pendingLoss = false; }
        public static void Reset() { PlayerPrefs.SetInt(PREF, 0); PlayerPrefs.Save(); s_pendingLoss = false; }

        /// <summary>Called at a full-board level build. Settles an unrescued loss, then places this streak's starting specials.
        /// Returns how many were placed.</summary>
        public static int Apply(RulesEngine rules, System.Collections.Generic.HashSet<Vector2Int> keep)
        {
            if (s_pendingLoss) { WDLog.Log($"[Streak] loss not rescued — streak {Count} reset to 0"); Reset(); }
            if (!ON || rules == null || LevelRules.BonusLevelActive) return 0;
            // NOT ON EARLY / TEACHING LEVELS (2026-10-23 Spencer: "i just won that level in 2 moves" — streak 6 put rocket + bomb +
            // disco + the start TNT on 6-gem L1; one combo took all 5 gems). Wins still count; the reward waits for bigger levels.
            var sm = SurvivalManager.Instance;
            int lvl = sm != null ? LevelMapPanel.RunLevel(sm.CurrentStageIndex) : 0;
            if (lvl < FROM_LEVEL || !string.IsNullOrEmpty(LevelRules.For(lvl).Teach))
            { if (Count > 0) WDLog.Log($"[Streak] streak {Count} — no start specials on L{lvl} (teaching / before L{FROM_LEVEL})"); return 0; }
            int n = Count, placed = 0;
            if (n >= 1) placed += rules.PlaceStartSpecial(Random.value < 0.5f ? RulesEngine.SPECIAL_ROCKET_H : RulesEngine.SPECIAL_ROCKET_V, 1, keep, MIN_GAP);
            if (n >= 2) placed += rules.PlaceStartSpecial(RulesEngine.SPECIAL_BOMB, 1, keep, MIN_GAP);
            if (n >= 3) placed += rules.PlaceStartSpecial(RulesEngine.SPECIAL_DISCO, 1, keep, MIN_GAP);
            if (placed > 0) WDLog.Log($"[Streak] streak {n}: started with {placed} special(s)");
            return placed;
        }
    }
}
