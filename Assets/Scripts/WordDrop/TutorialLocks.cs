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
        // Parked while we prove the loop (2026-06-09) — everything unlocked for normal testing.
        // Flip back to true per-tool when the onboarding/tutorial work resumes pre-launch.
        public static bool EditLocked     = false; // rewrite/edit — introduced L2
        public static bool BagLocked      = false; // tile-bag swap — introduced later
        public static bool BoostersLocked = false; // all four boosters — introduced later
    }
}
