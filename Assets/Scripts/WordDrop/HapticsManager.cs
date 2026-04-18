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

        /// <summary>Light tap — word scored, tile select.</summary>
        public static void Light()
        {
            if (!_enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            _playHaptic(0); // UIImpactFeedbackStyle.light
#elif UNITY_ANDROID
            Handheld.Vibrate();
#endif
        }

        /// <summary>Medium tap — tile primed, chain upgrade.</summary>
        public static void Medium()
        {
            if (!_enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            _playHaptic(1); // UIImpactFeedbackStyle.medium
#elif UNITY_ANDROID
            Handheld.Vibrate();
#endif
        }

        /// <summary>Strong tap — detonation, meltdown, edit mode activated.</summary>
        public static void Strong()
        {
            if (!_enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            _playHaptic(2); // UIImpactFeedbackStyle.heavy
#elif UNITY_ANDROID
            Handheld.Vibrate();
#endif
        }

        // ── Aliases for new call sites (map to existing levels) ──

        public static void TileLand() => Medium();
        public static void RisingRow() => Strong();
        public static void Meltdown() => Strong();
        public static void Explosion(int tier)
        {
            if (tier <= 1) Light();
            else if (tier <= 2) Medium();
            else Strong();
        }
    }
}
