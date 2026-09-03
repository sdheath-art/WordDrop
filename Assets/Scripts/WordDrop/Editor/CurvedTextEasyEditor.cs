#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace WordDrop
{
    /// <summary>Inspector for CurvedTextEasy: the default fields plus the "Curve Presets" buttons. 2026-07-16 Spencer.</summary>
    [CustomEditor(typeof(CurvedTextEasy))]
    public class CurvedTextEasyEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var t = (CurvedTextEasy)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Curve Presets", EditorStyles.boldLabel);

            DrawRow(t, "Arc Up", t.PresetArcUp, "Arc Down", t.PresetArcDown);
            DrawRow(t, "S Shape", t.PresetSShape, "Wave", t.PresetWave);
            DrawRow(t, "Left High", t.PresetLeftHigh, "Right High", t.PresetRightHigh);
        }

        private void DrawRow(CurvedTextEasy t, string labelA, System.Action a, string labelB, System.Action b)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(labelA)) { Undo.RecordObject(t, labelA); a(); EditorUtility.SetDirty(t); }
            if (GUILayout.Button(labelB)) { Undo.RecordObject(t, labelB); b(); EditorUtility.SetDirty(t); }
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
