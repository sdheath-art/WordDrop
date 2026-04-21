using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Phase 6 tutorial state + progression helper.
    ///
    /// Single source of truth for:
    ///   • Is the tutorial complete? (L3 cleared)
    ///   • Has the L3-first-time reward been awarded?
    ///   • What is the next tutorial level to resume at?
    ///
    /// All persisted via PlayerPrefs so state survives app restart.
    /// </summary>
    public static class TutorialProgression
    {
        public const int TUTORIAL_FIRST_LEVEL_ID = 1;
        public const int TUTORIAL_LAST_LEVEL_ID  = 3;
        public const int TUTORIAL_COMPLETE_COIN_REWARD = 100;

        private const string KEY_TUTORIAL_COMPLETE     = "wd_tutorial_complete";
        private const string KEY_TUTORIAL_BONUS_AWARDED = "wd_tutorial_complete_bonus_awarded";

        public static bool IsTutorialComplete()
            => PlayerPrefs.GetInt(KEY_TUTORIAL_COMPLETE, 0) == 1;

        public static bool IsTutorialLevel(int levelId)
            => levelId >= TUTORIAL_FIRST_LEVEL_ID && levelId <= TUTORIAL_LAST_LEVEL_ID;

        /// <summary>
        /// Resume point for the tutorial flow. Returns the lowest tutorial level the
        /// player has not yet completed, or TUTORIAL_LAST_LEVEL_ID+1 if fully done.
        /// </summary>
        public static int NextIncompleteTutorialLevel()
        {
            for (int id = TUTORIAL_FIRST_LEVEL_ID; id <= TUTORIAL_LAST_LEVEL_ID; id++)
            {
                if (!LevelProgressManager.IsCompleted(id)) return id;
            }
            return TUTORIAL_LAST_LEVEL_ID + 1;
        }

        /// <summary>
        /// Called when any tutorial level completes. Marks tutorial-complete + awards
        /// first-time coin bonus when the player first clears L3.
        /// Returns true if this completion triggered the tutorial-complete handshake
        /// (the caller should show the Tutorial Complete modal variant instead of the
        /// standard one).
        /// </summary>
        public static bool OnLevelCompleted(int levelId)
        {
            if (levelId != TUTORIAL_LAST_LEVEL_ID) return false;
            if (IsTutorialComplete()) return false; // already handled a prior first-time

            PlayerPrefs.SetInt(KEY_TUTORIAL_COMPLETE, 1);
            if (PlayerPrefs.GetInt(KEY_TUTORIAL_BONUS_AWARDED, 0) == 0)
            {
                CoinWallet.Add(TUTORIAL_COMPLETE_COIN_REWARD);
                PlayerPrefs.SetInt(KEY_TUTORIAL_BONUS_AWARDED, 1);
            }
            PlayerPrefs.Save();
            return true;
        }

        /// <summary>Debug-only: clear tutorial state so the tutorial runs again on next launch.</summary>
        public static void ResetTutorialState()
        {
            PlayerPrefs.DeleteKey(KEY_TUTORIAL_COMPLETE);
            PlayerPrefs.DeleteKey(KEY_TUTORIAL_BONUS_AWARDED);
            PlayerPrefs.Save();
        }
    }
}
