namespace WordDrop
{
    /// <summary>
    /// Which player tools are locked during early onboarding (2026-06-09 Spencer). Level 1 teaches
    /// only the core drop→prime→explode loop, so everything else starts locked (a padlock overlay
    /// on the HUD slot + the underlying mechanic gated). Later levels flip these false as each tool
    /// is introduced and taught — e.g. Edit unlocks at L2. Both the UI (BoosterHUDSlot) and the
    /// mechanics (HandManager rewrite entry, BoosterManager) read these.
    /// </summary>
    public static class TutorialLocks
    {
        public static bool EditLocked     = false; // rewrite/edit
        public static bool BagLocked      = false; // tile-bag swap
        public static bool BoostersLocked = false; // all four boosters
        public static bool SwapLocked     = false; // hand-card SWAPS panel + the swap action
        public static bool WildRewardLocked = false; // the chain-reward wild tile (combo bonus)

        // The level at which each tool UNLOCKS (a level < this keeps it locked). The first tutorial levels
        // teach only the core loop, so everything stays locked until the real game starts. There are now
        // FOUR tutorial levels — L1 (prime→explode), L2 (comprehension), L3 (combo teach), L4 (combo
        // practice) — so tools stay locked through L4 and unlock at L5, the first real level. Bump each one
        // down to its own teaching level as dedicated teach-levels for swaps/boosters/etc. get added.
        // 2026-07-06 Spencer.
        // 2026-07-07: L5 now TEACHES EDIT (its own gated level). EDIT unlocks at 5; everything else stays
        // locked until its own future teach-level (placeholders below at the design drip levels). So on L5 the
        // ONLY tool available is Edit — swaps/boosters/bag/wild are all still locked. Bump each down when its
        // teach-level is built.
        public const int SWAP_UNLOCK_LEVEL     = 31;  // hand-card swap — drip ~L31 (teach-level TBD)
        public const int BOOSTER_UNLOCK_LEVEL  = 41;  // boosters/mallet — drip ~L41 (teach-level TBD)
        public const int EDIT_UNLOCK_LEVEL     = 5;   // rewrite/EDIT — TAUGHT at tutorial L5
        public const int BAG_UNLOCK_LEVEL      = 41;  // tile-bag — drip later (teach-level TBD)
        public const int WILD_UNLOCK_LEVEL     = 7;   // chain-reward wild — unlocks at L7 (2026-07-10 Spencer; was a placeholder 36). Permanent from L7 on.

        /// <summary>Set every lock from the current level index. Called per-level in
        /// ObjectiveManager.InstallLevel. Both the UI (BoosterHUDSlot / SWAPS panel) and the mechanics
        /// (MatchController.UseSwap, HandManager rewrite, BoosterManager) read these flags.</summary>
        public static void ApplyForLevel(int level)
        {
            SwapLocked       = level < SWAP_UNLOCK_LEVEL;
            BoostersLocked   = level < BOOSTER_UNLOCK_LEVEL;
            EditLocked       = level < EDIT_UNLOCK_LEVEL;
            BagLocked        = level < BAG_UNLOCK_LEVEL;
            WildRewardLocked = level < WILD_UNLOCK_LEVEL;
        }
    }
}
