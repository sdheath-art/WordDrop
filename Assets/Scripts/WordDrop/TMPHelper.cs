using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// TextMeshPro helper with tiered visual effects:
    ///   HUD:          thin outline, subtle shadow, no glow — quiet and readable
    ///   Popup:        medium outline, visible shadow, subtle glow — informational feedback
    ///   Announcement: thick outline, heavy shadow, strong glow — celebrations and big moments
    /// </summary>
    public static class TMPHelper
    {
        // ── Text tiers ──────────────────────────────────────────────────────────

        public enum TextTier
        {
            HUD,          // scores, labels, turn counter
            Popup,        // flying scores, word popups
            Announcement  // DOUBLE!, MELTDOWN, CHAIN x3, YOU WIN
        }

        /// <summary>
        /// Creates a world-space TextMeshPro object with tiered effects.
        /// </summary>
        public static TextMeshPro Create(
            string name,
            Transform parent,
            Vector3 localPos,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center,
            int sortingOrder = 10,
            TextTier tier = TextTier.Popup)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = "";
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Normal;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.sortingOrder = sortingOrder;

            tmp.rectTransform.sizeDelta = new Vector2(14f, 2f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;

            ApplyEffects(tmp, color, tier);

            return tmp;
        }

        /// <summary>
        /// Creates a UI-space TextMeshPro object with tiered effects.
        /// </summary>
        public static TextMeshProUGUI CreateUI(
            string name,
            Transform parent,
            Vector2 anchorMin,
            Vector2 anchorMax,
            float fontSize,
            Color color,
            TextAlignmentOptions alignment = TextAlignmentOptions.Center,
            TextTier tier = TextTier.HUD)
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
            tmp.fontStyle = FontStyles.Normal;
            tmp.alignment = alignment;
            tmp.color = color;
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            tmp.overflowMode = TextOverflowModes.Overflow;

            ApplyEffects(tmp, color, tier);

            return tmp;
        }

        /// <summary>
        /// Apply tiered outline + shadow + optional glow + gradient to any TMP text.
        /// Default tier is Popup for backwards compatibility.
        /// </summary>
        public static void ApplyEffects(TMP_Text tmp, Color textColor, TextTier tier = TextTier.Popup)
        {
            if (tmp == null) return;

            // Dark purple outline to match game background
            Color outlineColor = new Color(0.15f, 0.1f, 0.25f, 1f);

            // Shadow uses dark purple to blend with game background
            Color shadowColor = new Color(0.12f, 0.08f, 0.2f, 1f); // deep dark purple

            // Tier-specific values
            float outlineWidth;
            float shadowOffX, shadowOffY, shadowDilate, shadowSoftness, shadowAlpha;
            bool enableGlow = false;
            float glowOuter = 0f, glowPower = 0f;
            bool enableGradient = false;

            switch (tier)
            {
                case TextTier.HUD:
                    outlineWidth = 0.15f;
                    shadowOffX = 0.5f;  shadowOffY = -0.5f;
                    shadowDilate = 0.1f; shadowSoftness = 0.25f; shadowAlpha = 0.5f;
                    break;

                case TextTier.Announcement:
                    outlineWidth = 0f;
                    shadowOffX = 0.2f;  shadowOffY = -0.2f;
                    shadowDilate = 0f; shadowSoftness = 0.4f; shadowAlpha = 0.25f;
                    enableGradient = true;
                    break;

                default: // Popup
                    outlineWidth = 0f;
                    shadowOffX = 0.2f;  shadowOffY = -0.2f;
                    shadowDilate = 0f; shadowSoftness = 0.4f; shadowAlpha = 0.25f;
                    enableGradient = true;
                    break;
            }

            // Outline
            tmp.outlineWidth = outlineWidth;
            tmp.outlineColor = (Color32)outlineColor;

            // Vertex gradient (lighter top, darker bottom — simulates top-down lighting)
            if (enableGradient && textColor != Color.white)
            {
                tmp.enableVertexGradient = true;
                tmp.colorGradient = new VertexGradient(
                    Color.Lerp(textColor, Color.white, 0.25f),
                    Color.Lerp(textColor, Color.white, 0.25f),
                    Color.Lerp(textColor, Color.black, 0.15f),
                    Color.Lerp(textColor, Color.black, 0.15f)
                );
            }

            // Material effects (underlay shadow + optional glow)
            if (tmp.fontSharedMaterial != null)
            {
                Material mat = new Material(tmp.fontSharedMaterial);
                tmp.fontMaterial = mat;

                // Shadow
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor(ShaderUtilities.ID_UnderlayColor,
                    new Color(shadowColor.r, shadowColor.g, shadowColor.b, shadowAlpha));
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, shadowOffX);
                mat.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, shadowOffY);
                mat.SetFloat(ShaderUtilities.ID_UnderlayDilate, shadowDilate);
                mat.SetFloat(ShaderUtilities.ID_UnderlaySoftness, shadowSoftness);

                // Glow (announcements only)
                if (enableGlow)
                {
                    mat.EnableKeyword("GLOW_ON");
                    mat.SetColor("_GlowColor", new Color(textColor.r, textColor.g, textColor.b, 0.4f));
                    mat.SetFloat("_GlowOffset", 0f);
                    mat.SetFloat("_GlowOuter", glowOuter);
                    mat.SetFloat("_GlowPower", glowPower);
                }
            }
        }
    }
}
