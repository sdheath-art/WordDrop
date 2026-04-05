using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Lightweight haptic feedback for mobile devices.
    /// Wraps Handheld.Vibrate() with platform guards.
    /// Call at key gameplay moments for tactile feedback.
    /// </summary>
    public static class HapticsManager
    {
        private static bool _enabled = true;

        /// <summary>Enable or disable all haptic feedback.</summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>Light vibration — word scored.</summary>
        public static void Light()
        {
#if UNITY_IOS || UNITY_ANDROID
            if (_enabled) Handheld.Vibrate();
#endif
        }

        /// <summary>Medium vibration — tile primed, chain upgrade.</summary>
        public static void Medium()
        {
#if UNITY_IOS || UNITY_ANDROID
            if (_enabled) Handheld.Vibrate();
#endif
        }

        /// <summary>Strong vibration — detonation, meltdown.</summary>
        public static void Strong()
        {
#if UNITY_IOS || UNITY_ANDROID
            if (_enabled) Handheld.Vibrate();
#endif
        }
    }
}
