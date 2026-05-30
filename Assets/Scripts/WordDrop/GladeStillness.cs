using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Glade Stillness". Untargeted strategic tool: pauses
    /// the rising-row timer for GLADE_STILLNESS_SECONDS while the player thinks /
    /// recovers. Does NOT pause tile-clear animations, music, or other timers —
    /// only the rise clock.
    ///
    /// Implementation: SurvivalManager has a `_gladeStillnessRemaining` field
    /// that decrements each Update. While > 0, the rise timer increment is
    /// skipped. This booster simply sets that field to GLADE_STILLNESS_SECONDS.
    /// </summary>
    public class GladeStillness : Booster
    {
        public override string Id               => "glade_stillness";
        public override string DisplayName      => "Glade Stillness";
        public override string ShortDescription
        {
            get { return $"Skip the next {GetRises()} rising rows."; }
        }
        public override bool NeedsTarget => false;

        // L1 = 2 rises, L2 = 3 rises, L3 = 4 rises
        public int GetRises() => 1 + Level;

        public override void Activate(System.Action onComplete)
        {
            var sm = SurvivalManager.Instance;
            int rises = GetRises();
            if (sm != null)
            {
                sm.GrantGladeStillnessRises(rises);
                Debug.Log($"[GladeStillness L{Level}] Skipping next {rises} rises");
            }
            else
            {
                Debug.LogWarning("[GladeStillness] SurvivalManager.Instance missing — no-op.");
            }
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
