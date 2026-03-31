using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Creates TextMeshPro world-space text with RTT-style professional effects:
    ///   - Thin outline (color-matched, 25% darker)
    ///   - Drop shadow (underlay)
    ///   - Bold styling
    ///
    /// Usage:
    ///   var tmp = TMPHelper.Create("Score", transform, pos, 6f, Color.green);
    ///   TMPHelper.ApplyEffects(tmp, Color.green);
    /// </summary>
    public static class TMPHelper
    {
        // ── Effect tuning (cranked up to be visible) ────────────────────────────
        private const float OUTLINE_WIDTH     = 0.45f;
        private const float SHADOW_OFFSET_X   = 1.5f;
        private const float SHADOW_OFFSET_Y   = -1.5f;
        private const float SHADOW_DILATE     = 0.4f;
        private const float SHADOW_SOFTNESS   = 0.2f;
        private const float SHADOW_ALPHA      = 0.9f;

        /// <summary>
        /// Creates a world-space TextMeshPro object with RTT-style effects.
        /// </summary>
        public static TextMeshPro Create(
            string name,
            Transform parent,
            Vector3 localPos,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center,
            int sortingOrder = 10)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "";
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.sortingOrder = sortingOrder;

            // Size the rect
            tmp.rectTransform.sizeDelta = new Vector2(10f, 2f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;

            ApplyEffects(tmp, color);

            return tmp;
        }

        /// <summary>
        /// Creates a UI-space TextMeshPro object with RTT-style effects.
        /// </summary>
        public static TextMeshProUGUI CreateUI(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
            tmp.text = "";
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;

            ApplyEffects(tmp, color);

            return tmp;
        }

        /// <summary>
        /// Apply RTT-style outline + drop shadow to any TMP text.
        /// </summary>
        public static void ApplyEffects(TMP_Text tmp, Color textColor)
        {
            if (tmp == null) return;

            // RTT ApplyTextPopStyle — exact values
            // Outline: darkened version of text color at 25%
            Color32 darkColor = new Color32(
                (byte)(textColor.r * 255f * 0.25f),
                (byte)(textColor.g * 255f * 0.25f),
                (byte)(textColor.b * 255f * 0.25f),
                255);

            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = darkColor;

            // Underlay shadow: darkened text color at 90% alpha
            if (tmp.fontSharedMaterial != null)
            {
                Material mat = new Material(tmp.fontSharedMaterial);
                tmp.fontMaterial = mat;

                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor(ShaderUtilities.ID_UnderlayColor,
                    new Color(darkColor.r / 255f, darkColor.g / 255f, darkColor.b / 255f, 0.9f));
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0.8f);
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -0.8f);
                mat.SetFloat(ShaderUtilities.ID_UnderlayDilate, 0.3f);
                mat.SetFloat(ShaderUtilities.ID_UnderlaySoftness, 0.1f);
            }
        }
    }
}
