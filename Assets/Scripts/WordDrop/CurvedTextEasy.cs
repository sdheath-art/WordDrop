using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Drop-in curved / arc text for any TextMeshPro object (UI or world). Warps the text mesh along an
    /// AnimationCurve, with per-letter rotation that follows the curve's tangent. Add it to a TMP object and adjust
    /// in the Inspector; use the preset buttons for common shapes. Runs in edit mode (ExecuteAlways) so you can dial
    /// it in live. Only works on TextMeshPro (NOT legacy uGUI Text). 2026-07-16 Spencer.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public class CurvedTextEasy : MonoBehaviour
    {
        public enum Mode { Curve }
        public enum Axis { Y, X }

        [Header("Curve Mode Settings")]
        public Mode curveMode = Mode.Curve;

        [Header("General Parameters")]
        [Tooltip("Which axis the curve displaces letters along. Y = a normal arc / rainbow; X = a sideways bulge.")]
        public Axis curveAxis = Axis.Y;

        [Range(0f, 1f)]
        [Tooltip("How much each letter ROTATES to follow the curve. 0 = letters stay upright, 1 = fully follow the arc.")]
        public float rotationStrength = 1f;

        [Header("Curve Mode Parameters")]
        [Tooltip("The arc's shape across the text width (left → right). Use the preset buttons below or draw your own.")]
        public AnimationCurve curve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(0.5f, 1f), new Keyframe(1f, 0f));

        [Header("Scaling")]
        [Tooltip("Arc amplitude in text units — how tall the curve is. Negative flips the arc.")]
        public float yAxisScaling = 15f;

        [Header("Letter Spacing")]
        [Tooltip("Tracking — pulls all letters closer (negative) or further apart (positive). Same as TMP's Character Spacing.")]
        public float letterSpacing = 0f;

        private TMP_Text _tmp;

        private void OnEnable()
        {
            _tmp = GetComponent<TMP_Text>();
            Apply();
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                // Defer out of OnValidate (calling ForceMeshUpdate directly here can warn), so live Inspector tweaks refresh.
                UnityEditor.EditorApplication.delayCall += () => { if (this != null) Apply(); };
                return;
            }
#endif
            Apply();
        }

        // Re-warp every frame so text changes, live Inspector tweaks, and entrance animations all stay curved.
        private void LateUpdate() => Apply();

        /// <summary>Warps the current text along the curve. Safe to call any time; no-ops on empty text or non-TMP.</summary>
        public void Apply()
        {
            if (_tmp == null) _tmp = GetComponent<TMP_Text>();
            if (_tmp == null) return;

            // Apply tracking BEFORE the mesh rebuild so the warp uses the re-spaced layout.
            if (!Mathf.Approximately(_tmp.characterSpacing, letterSpacing)) _tmp.characterSpacing = letterSpacing;

            _tmp.ForceMeshUpdate();
            TMP_TextInfo textInfo = _tmp.textInfo;
            int count = textInfo != null ? textInfo.characterCount : 0;
            if (count == 0) return;

            float boundsMinX = _tmp.bounds.min.x;
            float boundsMaxX = _tmp.bounds.max.x;
            float width = Mathf.Max(0.0001f, boundsMaxX - boundsMinX);

            for (int i = 0; i < count; i++)
            {
                if (!textInfo.characterInfo[i].isVisible) continue;

                int vertexIndex = textInfo.characterInfo[i].vertexIndex;
                int materialIndex = textInfo.characterInfo[i].materialReferenceIndex;
                Vector3[] verts = textInfo.meshInfo[materialIndex].vertices;

                // Character midpoint on its baseline — the pivot we offset + rotate around.
                Vector3 mid = new Vector2((verts[vertexIndex + 0].x + verts[vertexIndex + 2].x) * 0.5f,
                                          textInfo.characterInfo[i].baseLine);

                for (int k = 0; k < 4; k++) verts[vertexIndex + k] -= mid; // move glyph to origin

                float x0 = (mid.x - boundsMinX) / width;   // 0..1 across the text
                float x1 = x0 + 0.0001f;
                float y0 = curve.Evaluate(x0) * yAxisScaling;
                float y1 = curve.Evaluate(x1) * yAxisScaling;

                // Rotate the letter to follow the curve's local tangent (scaled by rotationStrength). Y arc only.
                float angle = 0f;
                if (curveAxis == Axis.Y)
                {
                    Vector3 horizontal = new Vector3(1f, 0f, 0f);
                    Vector3 tangent = new Vector3((x1 - x0) * width, y1 - y0, 0f);
                    float dot = Mathf.Acos(Mathf.Clamp(Vector3.Dot(horizontal, tangent.normalized), -1f, 1f)) * Mathf.Rad2Deg;
                    Vector3 cross = Vector3.Cross(horizontal, tangent);
                    angle = (cross.z >= 0f ? dot : -dot) * rotationStrength;
                }

                Vector3 offset = curveAxis == Axis.Y ? new Vector3(0f, y0, 0f) : new Vector3(y0, 0f, 0f);
                Matrix4x4 m = Matrix4x4.TRS(offset, Quaternion.Euler(0f, 0f, angle), Vector3.one);
                for (int k = 0; k < 4; k++) verts[vertexIndex + k] = m.MultiplyPoint3x4(verts[vertexIndex + k]);

                for (int k = 0; k < 4; k++) verts[vertexIndex + k] += mid; // move glyph back
            }

            for (int i = 0; i < textInfo.meshInfo.Length; i++)
            {
                textInfo.meshInfo[i].mesh.vertices = textInfo.meshInfo[i].vertices;
                _tmp.UpdateGeometry(textInfo.meshInfo[i].mesh, i);
            }
        }

        // ── Presets (called by the custom Inspector buttons) ──────────────────────────────────────────────
        public void PresetArcUp()     => SetCurve(new[] { (0f, 0f), (0.5f, 1f), (1f, 0f) });
        public void PresetArcDown()   => SetCurve(new[] { (0f, 0f), (0.5f, -1f), (1f, 0f) });
        public void PresetSShape()    => SetCurve(new[] { (0f, 0f), (0.33f, 1f), (0.66f, -1f), (1f, 0f) });
        public void PresetWave()      => SetCurve(new[] { (0f, 0f), (0.2f, 1f), (0.4f, -1f), (0.6f, 1f), (0.8f, -1f), (1f, 0f) });
        public void PresetLeftHigh()  => SetCurve(new[] { (0f, 1f), (1f, -1f) });
        public void PresetRightHigh() => SetCurve(new[] { (0f, -1f), (1f, 1f) });

        private void SetCurve((float t, float v)[] pts)
        {
            var keys = new Keyframe[pts.Length];
            for (int i = 0; i < pts.Length; i++) keys[i] = new Keyframe(pts[i].t, pts[i].v);
            curve = new AnimationCurve(keys);
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            Apply();
        }
    }
}
