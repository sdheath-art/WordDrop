using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// ONE TEXT TREATMENT FOR EVERY OUTLINED UI LABEL (2026-11-18).
    /// Spencer, with five Royal Kingdom references: "anyway you can get ours to look like that? ... there is an offset on the
    /// drop shadow. I would like this kind of effect and for the thickness of the drop shadow and text effect to match on
    /// ours", then of the full-screen mocks "they look great ... i need them to match perfectly please in game".
    ///
    /// MEASURED on RK (pixel profiles through letter stems - Leaderboard Stats, the World tab, the HUD "30"):
    ///   a thin, even stroke ~4% of the capital height, plus a HARD shadow in the SAME colour, offset right ~4.5% and
    ///   down ~6% of the capital height. No blur. Face = cream ramp FFFCE9 (top) -> FFF4BC (bottom).
    /// Mocks: ~/Documents/WordDrop_Art/text_mock_1118/ (game_rk, menu_rk, cards_rk_sheet).
    ///
    /// WHY ONE PLACE: outlined text was styled in ~40 spots (HUDManager.RecipeMat, ArtButton, NavBar, HudBar ...), each with
    /// its own stroke / drop numbers in TextMeshPro material units, so no two labels had the same edge. Here every UI text
    /// whose material has an outline gets a copy of that material with the stroke and shadow set from the FONT'S OWN
    /// capital height, so the result is the same share of the letter at any size, in any font. The label keeps its own
    /// line colour. ON = false puts every label back exactly as its own code styled it.
    ///
    /// UNITS: see Params. K_X is calibrated from a 10-31 measurement; check a screenshot before trusting the numbers.
    /// </summary>
    public static class RkText
    {
        public static bool  ON = true;
        public static float STROKE = 0.040f, DX = 0.045f, DY = 0.060f;          // fractions of the capital height
        // 11-18 MEASURED IN GAME over six labels at K_OFFSET 1.0 (card titles, button labels, 36-62 px letters):
        //   stroke 3.9 3.7 4.0 4.1 4.2 3.8 = mean 3.95% (target 4.0)   right 4.8 4.8 4.7 4.0 4.5 = mean 4.56% (4.5)
        //   down 6.4 6.4 5.5 5.4 5.9 = mean 5.9% (6.0).  The spread is +-0.3 px of measuring error, so both stay at their derived values.
        public static float K_X = 1.855f, K_OFFSET = 1.0f;
        public static bool  FACE_RAMP = true;
        public static Color FACE_TOP = new Color32(0xFF, 0xFC, 0xE9, 0xFF), FACE_BOT = new Color32(0xFF, 0xF4, 0xBC, 0xFF);

        // BADGE NUMBERS (11-18): the counts on goal icons and booster badges. Spencer: "in the reference the badge text treatment
        // is different than the others ... i think it doesnt have the slanted offset to the side". MEASURED on RK's HUD goal
        // counts ("32", digits 45 px tall): the dark edge is 3-4 px on ALL four sides (top 3, bottom 3, left 4, right 4) = an
        // even ~8% stroke and NO offset shadow - where their titles / moves number use 4% + the down-right shadow.
        // (First version, from badge_numbers_mock.png: 0.075 with a 0.055 / 0.075 offset.) Texts registered with Firm() use these.
        public static float FIRM_STROKE = 0.080f, FIRM_DX = 0f, FIRM_DY = 0f;
        private static readonly HashSet<int> s_firm = new HashSet<int>();
        /// <summary>Give this text the firmer small-text edge, whether or not its own material has a working outline.</summary>
        public static void Firm(TMP_Text t) { if (t != null) s_firm.Add(t.GetInstanceID()); }

        private static readonly Dictionary<string, Material> s_map = new Dictionary<string, Material>();   // source material + line colour + kind -> RK copy
        private static readonly HashSet<int> s_ours = new HashSet<int>();                            // ids of the RK copies
        private static readonly HashSet<int> s_ramped = new HashSet<int>();                          // texts whose face ramp is set

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            TMPro_EventManager.TEXT_CHANGED_EVENT.Remove(OnText);
            TMPro_EventManager.TEXT_CHANGED_EVENT.Add(OnText);
            var go = new GameObject("RkTextRunner") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go); go.AddComponent<Runner>();
        }

        /// <summary>Stroke + shadow for this font, in TextMeshPro material units. Follows the shader's own arithmetic
        /// (TMP_ShaderUtilities.UpdateShaderRatios): an alpha unit of the distance field is K_X x GradientScale texels;
        /// outline and face dilate are scaled by ratio A = (GS-1)/GS; the underlay's dilate and offset by ratio C =
        /// ratio A x (1 - face dilate) - so the shadow numbers have to allow for the face dilate.</summary>
        public static void Params(TMP_FontAsset font, Material m, float stroke, float dx, float dy, out float outline, out float dilate, out float under, out float offX, out float offY)
        {
            float gs = m != null && m.HasProperty("_GradientScale") ? Mathf.Max(2f, m.GetFloat("_GradientScale")) : font.atlasPadding + 1f;
            float cap = font.faceInfo.capLine > 1f ? font.faceInfo.capLine : font.faceInfo.pointSize * 0.6f;   // texels at the atlas size
            float ratioA = (gs - 1f) / gs;
            float t = stroke * cap;                                              // the stroke, in atlas texels
            outline = t / (ratioA * K_X * gs);
            dilate  = outline;                                                   // face edge stays put: the whole stroke is OUTSIDE the letter
            // 11-18 measured on the first build (card title 'L', 62 px): stroke 3.8% (target 4.0) but the shadow only 3.3% right /
            // 4.8% down (targets 4.5 / 6.0) = 0.77x. Cause: ratio C also subtracts the material's WEIGHT (max of normal / bold, / 4 -
            // 0.1875 with TMP's default bold weight 0.75), which the first version left out: 0.615 / 0.784 = 0.78.
            float weight = m != null && m.HasProperty("_WeightBold") ? Mathf.Max(m.GetFloat("_WeightNormal"), m.GetFloat("_WeightBold")) / 4f : 0f;
            float ratioC = ratioA * Mathf.Max(0.05f, 1f - dilate - weight);
            under   = outline * ratioA / ratioC;                                 // the shadow's edge = the stroke's outer edge
            offX    =  dx * cap / (ratioC * gs) * K_OFFSET;
            offY    = -dy * cap / (ratioC * gs) * K_OFFSET;
        }

        /// <summary>Write the RK stroke + shadow onto <paramref name="m"/> (colour = <paramref name="line"/>).</summary>
        public static void Apply(Material m, TMP_FontAsset font, Color line, bool firm = false)
        {
            Params(font, m, firm ? FIRM_STROKE : STROKE, firm ? FIRM_DX : DX, firm ? FIRM_DY : DY, out float w, out float d, out float u, out float ox, out float oy);
            m.EnableKeyword("OUTLINE_ON");
            m.SetFloat("_OutlineWidth", w); m.SetColor("_OutlineColor", line);
            m.SetFloat("_FaceDilate", d);
            if (m.HasProperty("_OutlineSoftness")) m.SetFloat("_OutlineSoftness", 0f);
            m.EnableKeyword("UNDERLAY_ON"); m.DisableKeyword("UNDERLAY_INNER");
            m.SetColor("_UnderlayColor", line);
            m.SetFloat("_UnderlayOffsetX", ox); m.SetFloat("_UnderlayOffsetY", oy);
            m.SetFloat("_UnderlayDilate", u); m.SetFloat("_UnderlaySoftness", 0f);
            ShaderUtilities.UpdateShaderRatios(m);
        }

        private static bool IsCream(Color c) => c.r > 0.92f && c.g > 0.86f && c.b > 0.60f && c.b < 0.94f;

        // 11-18 ROUND 2 BUG (Spencer's screenshots: the win screen's "Continue" was a solid cream block, "Quit Level?" and "Quit"
        // were blurred): this callback runs INSIDE TextMeshPro's own mesh build. Swapping the material there left the text
        // flagged as changed with no rebuild queued, and TMP only re-sharpens a text for its on-screen SCALE while that flag
        // is clear - so anything that pops in from a small scale (cards, the Continue button from zero) kept the sharpness
        // of the size it was first drawn at. Now the callback only NOTES the text; the restyle happens next frame, outside
        // the build, where the change is queued and finished the normal way.
        private static readonly List<TextMeshProUGUI> s_queue = new List<TextMeshProUGUI>();
        private static readonly HashSet<int> s_queued = new HashSet<int>();
        private static void OnText(Object o)
        {
            if (!ON) return;
            var t = o as TextMeshProUGUI; if (t == null) return;                 // UI text only: board letters are world text
            var m = t.fontSharedMaterial; if (m == null || t.font == null) return;
            bool needsMat = !s_ours.Contains(m.GetInstanceID()) && (HasOutline(m) || s_firm.Contains(t.GetInstanceID()));
            bool needsRamp = FACE_RAMP && !s_ramped.Contains(t.GetInstanceID()) && (needsMat || s_ours.Contains(m.GetInstanceID()));
            if ((needsMat || needsRamp) && s_queued.Add(t.GetInstanceID())) s_queue.Add(t);
        }

        // an outline is ON when the width is set and - on the Mobile shader only - the OUTLINE_ON keyword is enabled. The full
        // "Distance Field" shader (ArtButtonLabel.mat) has no such keyword: the first build skipped every button label.
        private static bool HasOutline(Material m)
        {
            if (!m.HasProperty("_OutlineWidth") || m.GetFloat("_OutlineWidth") < 0.01f) return false;                          // plain text stays plain
            // 11-18 round 3 (Spencer: "weird orange lines above the lettering" on the win screen): the hero title has its OWN look -
            // gold face, thin warm edge, SOFT dark shadow (StageClearModal.HeroFace, his 11-06 direction). The recipe turned its
            // shadow hard and orange and stray slivers showed above the letters. A material with a soft shadow is a deliberate
            // different style: leave it exactly as its own code made it.
            if (m.IsKeywordEnabled("UNDERLAY_ON") && m.HasProperty("_UnderlaySoftness") && m.GetFloat("_UnderlaySoftness") > 0.03f) return false;   // 0.03: the hero's 0.35 becomes ~0.09 on its wide-margin font
            return !(m.shader != null && m.shader.name.Contains("Mobile") && !m.IsKeywordEnabled("OUTLINE_ON"));
        }

        /// <summary>Restyle the texts noted since the last frame. Called by the runner, outside TextMeshPro's mesh build.</summary>
        internal static void Flush()
        {
            if (s_queue.Count == 0) return;
            for (int i = 0; i < s_queue.Count; i++)
            {
                var t = s_queue[i]; if (t == null) continue;
                s_queued.Remove(t.GetInstanceID());
                if (!ON) continue;
                var m = t.fontSharedMaterial; var font = t.font; if (m == null || font == null) continue;
                int id = m.GetInstanceID();
                if (!s_ours.Contains(id))
                {
                    bool firm = s_firm.Contains(t.GetInstanceID());
                    if (!firm && !HasOutline(m)) continue;
                    Color line = m.HasProperty("_OutlineColor") ? m.GetColor("_OutlineColor") : Color.black;
                    // keyed on the line colour too: a label whose code re-colours its outline later (badge: red count / green "+")
                    // comes back with its own material in the new colour and must get a copy in THAT colour, not the first one
                    string key = id + "|" + ColorUtility.ToHtmlStringRGB(line) + (firm ? "|firm" : "");
                    if (!s_map.TryGetValue(key, out var rk) || rk == null)
                    {
                        rk = new Material(m) { name = m.name + " RK" };          // a COPY: never write into a font's or an asset's material
                        Apply(rk, font, line, firm);
                        s_map[key] = rk; s_ours.Add(rk.GetInstanceID());
                    }
                    t.fontSharedMaterial = rk;                                   // (sets the padding and queues the rebuild itself)
                }
                // face: the measured cream ramp, on labels that were cream (white and coloured text keep their colour).
                // ArtButton writes its own per-word gradient - it reads FACE_TOP / FACE_BOT itself.
                if (FACE_RAMP && s_ramped.Add(t.GetInstanceID()) && t.GetComponentInParent<ArtButton>() == null)
                {
                    Color c = t.enableVertexGradient ? (t.colorGradient.topLeft + t.colorGradient.bottomLeft) * 0.5f * t.color : t.color;
                    if (IsCream(c))
                    {
                        float a = t.color.a;
                        t.enableVertexGradient = true; t.colorGradient = new VertexGradient(FACE_TOP, FACE_TOP, FACE_BOT, FACE_BOT);
                        t.color = new Color(1f, 1f, 1f, a);
                    }
                }
            }
            s_queue.Clear();
        }

        private class Runner : MonoBehaviour { private void Update() => Flush(); private void LateUpdate() => Flush(); }

        /// <summary>Forget every copy (after changing ON or the numbers at runtime); texts re-style on their next redraw.</summary>
        public static void Reset() { s_map.Clear(); s_ours.Clear(); s_ramped.Clear(); s_queue.Clear(); s_queued.Clear(); }
    }
}
