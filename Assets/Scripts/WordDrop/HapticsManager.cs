using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace WordDrop
{
    /// <summary>
    /// Haptic feedback using iOS native Taptic Engine.
    /// Falls back to Handheld.Vibrate() on Android.
    /// </summary>
    public static class HapticsManager
    {
        private static bool _enabled = true;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void _playHaptic(int style);
#endif

        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Light tap — UI taps, card selects. ROUTED to Nice Vibrations
        /// PlayEmphasis (0.25/0.65) — lighter and crisper than the old UIImpactFeedbackGenerator.light preset.</summary>
        public static void Light()
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayEmphasis(0.25f, 0.65f);
        }

        /// <summary>Medium tap — word scored, tile primed. ROUTED to Nice Vibrations
        /// PlayEmphasis (0.40/0.60) — lighter than the old UIImpactFeedbackGenerator.medium preset.</summary>
        public static void Medium()
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayEmphasis(0.40f, 0.60f);
        }

        /// <summary>Strong tap — big detonation. ROUTED to Nice Vibrations
        /// PlayEmphasis (0.60/0.55) — significantly lighter than the old UIImpactFeedbackGenerator.heavy preset
        /// which felt "chunky". For true hero impacts use MeltdownHit() (0.85/0.50).</summary>
        public static void Strong()
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayEmphasis(0.60f, 0.55f);
        }

        /// <summary>Detonation impact (= Strong), DEBOUNCED ~100ms. Explosions are fired from two
        /// code paths (GameVisualBridge's Exploding step + WordDropFX's tier/cascade pop FX); some
        /// blasts only hit one of them. Calling this from BOTH guarantees every explosion buzzes,
        /// while the debounce collapses the same blast's two calls into ONE. Cascade LAYERS are
        /// ~0.4s apart, so each still fires. 2026-06-11 Spencer.</summary>
        private static float _lastExplosionTime = -1f;
        public static void ExplosionImpact()
        {
            if (!_enabled) return;
            float now = Time.unscaledTime;
            if (_lastExplosionTime >= 0f && now - _lastExplosionTime < 0.10f) return; // same-blast double → one
            _lastExplosionTime = now;
            Lofelt.NiceVibrations.HapticPatterns.PlayEmphasis(0.60f, 0.55f);
        }

        // ── Aliases for new call sites (now route to new semantic API) ──

        public static void TileLand() => TilePlacedOnBoard();
        public static void RisingRow() => Emphasis(0.55f, 0.35f); // pressure cue — bassy for tension feel
        public static void Meltdown() => MeltdownHit();
        public static void Explosion(int tier)
        {
            // Tiered fallback for legacy call sites — new code should use
            // the semantic methods directly (WordScored / CascadePop / PrimedDetonation / MeltdownHit).
            if (tier <= 1)      WordScored();
            else if (tier <= 2) Emphasis(0.55f, 0.55f);
            else if (tier <= 3) PrimedDetonation();
            else                MeltdownHit();
        }

        // ── NiceVibrations-backed continuous rumble + heavy impact ──
        // Uses Lofelt.NiceVibrations.HapticPatterns which routes to Core
        // Haptics on iOS (CHHapticEngine) for true continuous low-frequency
        // rumble — discrete taps don't feel like an earthquake.

        /// <summary>
        /// Continuous rumble for the meltdown windup. amplitude (0-1) =
        /// intensity, frequency (0-1) = sharpness (low = bassy rumble,
        /// high = crisp buzz). Duration in seconds. Cuts via RumbleStop().
        /// </summary>
        public static void Rumble(float amplitude, float frequency, float duration)
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayConstant(amplitude, frequency, duration);
        }

        /// <summary>Cuts any active continuous rumble immediately.</summary>
        public static void RumbleStop()
        {
            Lofelt.NiceVibrations.HapticController.Stop();
        }

        /// <summary>
        /// Sets the live amplitude multiplier on the currently-playing rumble.
        /// Use this to ramp intensity over time (call inside a coroutine).
        /// Range 0-1. Has effect immediately on iOS, on next Play() on Android.
        /// </summary>
        public static void SetRumbleLevel(float level)
        {
            Lofelt.NiceVibrations.HapticController.clipLevel = Mathf.Clamp01(level);
        }

        /// <summary>
        /// Meaty explosion impact — heavy preset PLUS a brief constant burst
        /// stacked on top so it's noticeably more tactile than a single
        /// UIImpactFeedbackGenerator.Heavy tap (which can disappear under
        /// the audio bang).
        /// </summary>
        public static void MegaImpact()
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayPreset(
                Lofelt.NiceVibrations.HapticPatterns.PresetType.HeavyImpact);
            // Brief 80ms high-amplitude burst stacked for extra body.
            Lofelt.NiceVibrations.HapticPatterns.PlayConstant(1f, 0.4f, 0.08f);
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PREMIUM CORE-HAPTICS API (Path C — added 2026-05-15)
        // ═══════════════════════════════════════════════════════════════════════
        // Routes through Lofelt Nice Vibrations' PlayEmphasis API, which maps
        // to iOS Core Haptics CHHapticEvent under the hood. Lets us tune
        // intensity (amplitude) AND sharpness (frequency) independently —
        // unlike the preset Light/Medium/Strong API which is coarse and
        // chunky-feeling at the high end.
        //
        // Tuning philosophy (informed by Royal Match + Marvel Snap inferred
        // patterns + Apple HIG):
        //   - Routine actions live in amplitude 0.25-0.50 ("light" feel)
        //   - Hero moments earn amplitude 0.7-0.9
        //   - Frequency (sharpness) 0.2-0.4 = organic/rumbly
        //   - Frequency 0.5-0.7 = balanced/clean
        //   - Frequency 0.7-1.0 = crisp/clicky (UI feel)
        //
        // Test on actual iPhone hardware — simulator has no Taptic Engine.

        /// <summary>
        /// Single transient haptic with explicit amplitude (0-1) and
        /// frequency/sharpness (0-1) control. The Nice Vibrations equivalent
        /// of a custom Core Haptics transient.
        /// </summary>
        public static void Emphasis(float amplitude, float frequency)
        {
            if (!_enabled) return;
            Lofelt.NiceVibrations.HapticPatterns.PlayEmphasis(
                Mathf.Clamp01(amplitude),
                Mathf.Clamp01(frequency));
        }

        // ── Semantic event API — tuned values for each game moment ───────────

        /// <summary>Tile drops into the hand from the bag. Subtle, frequent — keep light.</summary>
        public static void TileDropIntoHand() => Emphasis(0.25f, 0.60f);

        /// <summary>Tile placed on the board by the player. Slightly firmer thunk than hand-drop.</summary>
        public static void TilePlacedOnBoard() => Emphasis(0.35f, 0.65f);

        /// <summary>Initial word scored by the player. Satisfying pop, not heavy.</summary>
        public static void WordScored() => Emphasis(0.45f, 0.55f);

        /// <summary>Cascade pop (gravity-formed word detonates). Same as initial pop —
        /// audio pitch escalation carries the chain-reaction feel, haptic stays consistent.</summary>
        public static void CascadePop() => Emphasis(0.45f, 0.55f);

        /// <summary>Primed-word detonation impact. Stronger than a simple pop.</summary>
        public static void PrimedDetonation() => Emphasis(0.65f, 0.50f);

        /// <summary>Meltdown impact — the hero moment. Reserved for big rare events.</summary>
        public static void MeltdownHit() => Emphasis(0.85f, 0.50f);

        /// <summary>Edit/rewrite confirmed. Crisp UI feel.</summary>
        public static void EditConfirm() => Emphasis(0.30f, 0.75f);

        /// <summary>Swap card action. Same UI-crisp feel as edit.</summary>
        public static void SwapConfirm() => Emphasis(0.30f, 0.75f);

        /// <summary>Standard UI button (Play, Stage Select, Settings). Debounced ~80ms so it can be
        /// wired into BOTH the click-SOUND path (GameAudio.PlayButtonClick) and the press-ANIMATION
        /// path (UIAnimations.ButtonPress) — buttons that fire both still get ONE haptic per press,
        /// and buttons that fire only one are still covered. 2026-06-10.</summary>
        private static float _lastButtonTapTime = -1f;
        public static void ButtonTap()
        {
            if (!_enabled) return;
            float now = Time.unscaledTime;
            if (_lastButtonTapTime >= 0f && now - _lastButtonTapTime < 0.08f) return; // collapse double-fire
            _lastButtonTapTime = now;
            Emphasis(0.25f, 0.70f);
        }

        /// <summary>Invalid action / error. Single transient at low sharpness for "thud" feel.</summary>
        public static void InvalidAction() => Emphasis(0.50f, 0.30f);

        /// <summary>Stage clear celebration — 3-tick ascending "chord". Coroutine-driven.</summary>
        public static System.Collections.IEnumerator StageClearChord()
        {
            if (!_enabled) yield break;
            Emphasis(0.40f, 0.50f);
            yield return new WaitForSecondsRealtime(0.08f);
            Emphasis(0.55f, 0.60f);
            yield return new WaitForSecondsRealtime(0.08f);
            Emphasis(0.70f, 0.80f);
        }

        /// <summary>Edit rescue mechanic fires (player gets edit back from cascade or cooldown).
        /// 2-tick ascending — "recovery" feel.</summary>
        public static System.Collections.IEnumerator EditRescueChord()
        {
            if (!_enabled) yield break;
            Emphasis(0.50f, 0.60f);
            yield return new WaitForSecondsRealtime(0.10f);
            Emphasis(0.65f, 0.80f);
        }
    }
}
