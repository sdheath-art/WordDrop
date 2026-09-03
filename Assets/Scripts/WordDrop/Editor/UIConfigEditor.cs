using UnityEditor;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Custom editor that auto-applies UIConfig changes during Play mode.
    /// When you modify any value on the UIConfig asset while playing,
    /// this detects the change and updates live UI elements (colors, sizes, text)
    /// without rebuilding anything.
    /// </summary>
    [CustomEditor(typeof(UIConfig))]
    public class UIConfigEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();

            if (EditorGUI.EndChangeCheck() && Application.isPlaying)
            {
                // Value changed during play mode — apply live
                ApplyLiveChanges();
            }
        }

        private void ApplyLiveChanges()
        {
            var cfg = (UIConfig)target;
            if (cfg == null) return;

            // Force the static instance to pick up changes
            var field = typeof(UIConfig).GetField("_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            if (field != null)
                field.SetValue(null, cfg);

            // Re-apply icon size / hue / saturation / value to every live coin + heart.
            // These are built once, so without this a slider does nothing until a rebuild.
            UIConfig.RefreshIcons();

            // Update HUD colors live
            if (HUDManager.Instance != null)
                HUDManager.Instance.ApplyLiveConfig();

            Debug.Log("[UIConfig] Live update applied.");
        }
    }
}
