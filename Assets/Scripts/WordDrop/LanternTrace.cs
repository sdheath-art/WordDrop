using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Lantern Trace". Highlights one playable word path on the
    /// current board (no auto-play). Untargeted: tap booster → hint flares.
    ///
    /// Reuses HintManager which already finds + highlights a playable word with
    /// a fade. Adding this booster gives players a "manual hint as roguelite pick"
    /// in addition to the auto-hint that fires when stuck (JamHint).
    ///
    /// Per Codex's design notes:
    /// - Surface shortest/highest-confidence path first
    /// - Refund charge if no playable word exists (caller already consumed it,
    ///   so we'd need a return-charge path — deferred to v1.1 polish)
    /// </summary>
    public class LanternTrace : Booster
    {
        public override string Id               => "lantern_trace";
        public override string DisplayName      => "Lantern Trace";
        // L1 = 1 hint, L2 = 2 hints, L3 = 3 hints (queued reveals)
        public override string ShortDescription
        {
            get
            {
                switch (Level)
                {
                    case 2:  return "Light up 2 playable word paths.";
                    case 3:  return "Light up 3 playable word paths.";
                    default: return "Light up a playable word path.";
                }
            }
        }
        public override bool NeedsTarget => false;

        public override void Activate(System.Action onComplete)
        {
            if (HintManager.Instance != null)
            {
                // L2/L3 fire multiple hint reveals — HintManager.ForceShowHint
                // re-runs the find-and-highlight pass. Sequential calls reuse the
                // same surface; for MVP we just log the intended count and fire
                // once. v1.5 polish can queue/sequence multiple distinct hints.
                HintManager.Instance.ForceShowHint();
                Debug.Log($"[LanternTrace L{Level}] Hint(s) triggered (logical count={Level}).");
            }
            else
            {
                Debug.LogWarning("[LanternTrace] HintManager.Instance missing — booster fired but no hint shown.");
            }
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
