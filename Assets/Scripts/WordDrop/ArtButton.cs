using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// A button built from the Photoshop art (2026-10-25, ~/Documents/WordDrop_Art/vector_buttons/photoshop, style guide
    /// https://claude.ai/artifact/VJdz8j6sF3LXKjDetjBFvm). Three files per button in Resources/UI/Buttons:
    ///   &lt;key&gt;_frame.png  gold frame, groove and dark recess — never moves
    ///   &lt;key&gt;_face.png   the coloured face WITHOUT its label — squeezes on press
    ///   &lt;key&gt;_label.json where the label sits and how it is drawn, written by the same build
    /// The label is drawn live so it can say anything (Spencer: "change the level numbers"), in WordSlop Button with the
    /// approved cream style: cream top→bottom fill, a thin ink ring, and a SOLID extrusion in the same ink swept
    /// down-right. TMP's single underlay is only one shifted copy, which reads as a drop shadow; the art's extrusion is
    /// the outlined letter SWEPT along the offset, so it is several ink copies stepped along it.
    ///
    /// Everything is laid out in the art's own pixels (the PNG canvas) under one scaled node, so every number below is
    /// the Photoshop number and the whole thing scales to any button size with nothing to re-derive.
    /// </summary>
    [RequireComponent(typeof(RectTransform))]
    public class ArtButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
    {
        [System.Serializable]
        private class Spec
        {
            public float[] face;        // x, y, w, h in canvas px, y from the top
            public float[] textBox;     // x, y, w, h — the box the label's ink was fitted into
            public int[]   frameBox;    // x, y, w, h — the visible gold frame (the PNG has shadow margin around it)
            public string  label, ink, creamTop, creamBottom;
            public float   outlinePx, emPx, trackingPerMille, condense;
            public float[] extrudePx;   // x right, y DOWN
        }

        // Pressed state, approved 2026-10-25 ("awesome thats perfect"): the whole coloured face narrows about its own
        // centre; the frame, groove and dark recess stay put, so more of the dark shows round the face while held.
        private const float PRESS_X = 0.95f, PRESS_Y = 0.98f, PRESS_T = 0.08f;
        private const float OVERSHOOT = 1.01f;
        private const float EXTRUDE_MUL = 1.3f;     // sweep length vs the art's offset (calibrated against the Photoshop render)
        // 0 = OFF (2026-10-25, matched to Royal Kingdom: outline + ONE hard drop shadow from the label material, no swept
        // extrusion). 6 = the old swept extrusion (~1 copy per screen px at phone sizes).
        private const int   EXTRUDE_STEPS = 0;
        private const float FIT_WIDTH = 0.94f;      // of the face — longer labels shrink to stay inside it

        public Button Button { get; private set; }
        public RectTransform Face { get; private set; }
        /// <summary>Width / height of the visible gold frame. Size the root to this aspect.</summary>
        public float FrameAspect => _spec.frameBox[2] / (float)_spec.frameBox[3];
        public bool PlaySounds = true;

        private Spec _spec;
        private RectTransform _rt, _art;
        private Vector2 _canvas;
        private readonly List<TextMeshProUGUI> _texts = new List<TextMeshProUGUI>();
        private bool _down;

        public string Label
        {
            get => _texts.Count > 0 ? _texts[_texts.Count - 1].text : "";
            set { foreach (var t in _texts) t.text = value; }
        }

        /// <summary>Builds the button under <paramref name="parent"/>, or returns null if its art is missing (callers
        /// keep their old button then). The root is centre-anchored; set its size with FrameAspect.</summary>
        public static ArtButton Create(Transform parent, string key, string label = null, bool? lilita = null, bool? titleMedium = null)
        {
            var frame = Resources.Load<Sprite>($"UI/Buttons/{key}_frame");
            var face  = Resources.Load<Sprite>($"UI/Buttons/{key}_face");
            var json  = Resources.Load<TextAsset>($"UI/Buttons/{key}_label");
            if (frame == null || face == null || json == null)
            {
                Debug.LogWarning($"[ArtButton] missing art for '{key}' (frame {frame != null}, face {face != null}, json {json != null})");
                return null;
            }
            var go = new GameObject(key, typeof(RectTransform), typeof(Button));
            go.transform.SetParent(parent, false);
            var ab = go.AddComponent<ArtButton>();
            ab._lilitaOverride = lilita;
            ab._titleMediumOverride = titleMedium;   // 11-06 Spencer: Play stays WordSlop Button
            ab._key = key;
            ab.Build(frame, face, JsonUtility.FromJson<Spec>(json.text), label);
            return ab;
        }

        private void Build(Sprite frame, Sprite face, Spec spec, string label)
        {
            _spec = spec;
            _rt = (RectTransform)transform;
            _rt.anchorMin = _rt.anchorMax = _rt.pivot = new Vector2(0.5f, 0.5f);
            _canvas = frame.rect.size;

            // The art node: exactly the PNG canvas in px, scaled + offset so the FRAME fills the root.
            _art = NewRT("Art", _rt);
            _art.anchorMin = _art.anchorMax = new Vector2(0.5f, 0.5f);
            _art.sizeDelta = _canvas;

            var frameImg = NewImage("Frame", _art, frame);
            frameImg.raycastTarget = true;   // the frame takes the touch; it covers the face

            // Face pivots on the face's own centre, so the squeeze closes in evenly from both ends.
            Face = NewRT("Face", _art);
            Face.pivot = new Vector2((spec.face[0] + spec.face[2] * 0.5f) / _canvas.x,
                                     1f - (spec.face[1] + spec.face[3] * 0.5f) / _canvas.y);
            Stretch(Face);
            NewImage("FaceArt", Face, face).raycastTarget = false;
            BuildLabel(label ?? spec.label);

            Button = GetComponent<Button>();
            Button.transition = Selectable.Transition.None;   // the squeeze IS the feedback
            Button.targetGraphic = frameImg;
            // Release sound on CLICK (not pointer-up) so a drag-off cancel stays silent. One half per event only —
            // see feedback_wordrop_button_sound_system.md.
            Button.onClick.AddListener(() => { if (PlaySounds) GameAudio.Instance?.PlayMultiPopRelease(); });

            Fit();
        }

        private void BuildLabel(string text)
        {
            // LABEL FONT (2026-10-29, Spencer: the WordSlop label "looks off" next to Royal Kingdom; picked Lilita One from
            // vector_buttons/compare_level_fonts.png + compare_play_lilita.png, "lets switch it over for now"). "LilitaOne Button SDF"
            // is baked like WordSlop's (pt 90 / pad 9 = the same 0.1 spread, so the label material's outline/underlay values hold;
            // the old "LilitaOne SDF" is pt 71 / pad 4) by Editor/WordSlopSetup. Missing -> WordSlop, as before.
            // Per-button override (2026-10-31: the home Level button went back to WordSlop after the library-home mockup).
            var font = ((_lilitaOverride ?? LABEL_LILITA) ? Resources.Load<TMP_FontAsset>("LilitaOne Button SDF") : null);
            _lilita = font != null;
            // 2026-11-06 Spencer: WordSlop Title Medium on the labels (more distinct). LABEL_TITLE_MEDIUM = false -> WordSlop Button.
            if (font == null && (_titleMediumOverride ?? LABEL_TITLE_MEDIUM)) font = Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF");
            if (font == null) font = Resources.Load<TMP_FontAsset>("WordSlop Button SDF") ?? Resources.Load<TMP_FontAsset>("WordSlop SDF");
            if (font == null) { Debug.LogWarning("[ArtButton] no WordSlop font asset — label skipped"); return; }

            Color ink  = Hex(_spec.ink), top = Hex(_spec.creamTop), bot = Hex(_spec.creamBottom);
            if (RkText.ON && RkText.FACE_RAMP) { top = RkText.FACE_TOP; bot = RkText.FACE_BOT; }   // 11-18: one measured cream ramp on every label (RkText.cs)
            var mat = Styled(InkMaterial(font, ink), _key);

            // Label box centred on the measured text box (face-local, y from the top), widened so longer labels have
            // room before they shrink; condense is the horizontal stretch the art applied to the letters.
            var root = NewRT("Label", Face);
            root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.anchoredPosition = new Vector2(_spec.textBox[0] + _spec.textBox[2] * 0.5f,
                                                -(_spec.textBox[1] + _spec.textBox[3] * 0.5f));
            float c = _spec.condense > 0f ? _spec.condense : 1f;
            // Height: TMP auto-size also shrinks for HEIGHT, against the font's full line (ascender to descender ≈ 1.6 em
            // in WordSlop) — a box sized to the ink shrank every label to ~81%. 2 em clears it; width is the real limit.
            root.sizeDelta = new Vector2(_spec.face[2] * FIT_WIDTH / c, _spec.emPx * 2f);
            root.localScale = new Vector3(c, 1f, 1f);

            // Extrusion: farthest copy first so nearer ones draw over it, then the cream face on top.
            float ex = EXTRUDE_MUL * (_spec.extrudePx != null && _spec.extrudePx.Length > 1 ? _spec.extrudePx[0] : 4f);
            float ey = EXTRUDE_MUL * (_spec.extrudePx != null && _spec.extrudePx.Length > 1 ? _spec.extrudePx[1] : 6f);
            for (int k = EXTRUDE_STEPS; k >= 1; k--)
            {
                var t = NewText($"Extrude{k}", root, font, mat, text);
                t.color = ink;
                t.rectTransform.anchoredPosition = new Vector2(ex / c, -ey) * (k / (float)EXTRUDE_STEPS);
            }
            var main = NewText("Text", root, font, mat, text);
            // ONE gradient across the WORD, like Photoshop's Gradient Overlay on the text layer. TMP's colorGradient
            // restarts on every letter, so short letters (e, v) started white at their own top and the label read
            // paler than the art. Colours are written per vertex from its height within the word's ink bounds.
            main.color = top;   // also the probe that tells us whether TMP is writing linear vertex colours
            _creamTop = top; _creamBot = bot;
            main.OnPreRenderText += WordGradient;

            // INNER SHADE (the art's Inner Shadow: Multiply 90%, light from the top, dist 4.8px, size 9.6px): the cream
            // darkens softly just inside the top of each letter. TMP can't inner-shadow an opaque face, so this is a
            // copy with an INVISIBLE face whose INNER underlay draws only inside the letter, where the letter shifted
            // down doesn't reach. Spencer 2026-10-25: "the drop shadow effect from our button is not the same".
            // OFF 2026-10-26: RK Level 5's cream is flat and crisp to the edge; this soft shade read as a blurry edge.
            if (INNER_SHADE) { var shade = NewText("InnerShade", root, font, ShadeMaterial(font, top), text); shade.color = Color.white; }
        }

        public static bool INNER_SHADE = false;
        public static bool LABEL_LILITA = true;
        public static bool LABEL_TITLE_MEDIUM = true;
        // Lilita's capital is 0.702 em vs WordSlop Button's 0.594 em (measured from the .ttf 'L'), so the same em size would make
        // the capitals 18% taller: scale the size so the capital stays at the measured 10.1% of the frame width. Lilita's own
        // spacing is already tight, so it skips WordSlop's -18 tracking (LILITA_TRACKING, TMP units).
        public static float LILITA_SIZE = 0.594f / 0.702f, LILITA_TRACKING = 0f;
        private bool _lilita;
        private bool? _titleMediumOverride;
        private string _key;

        // Per-button label look on top of ArtButtonLabel.mat (11-06 Spencer: Play like RK's "Continue" button ref, Desktop
        // screenshot 8.40.40: thin mid-olive line (54,75,23) ~45% of the face value, barely any drop - the edge below is only
        // ~1 px thicker than the outline). (outline width, drop x, drop y, edge colour). Remove an entry = template look.
        public static readonly Dictionary<string, (float outline, float dropX, float dropY, Color edge)> LABEL_STYLE =
            new Dictionary<string, (float, float, float, Color)>
            {
                // 11-06 v2 (Spencer "the word play doesnt really pop"): the ref's green is a flat MID green (123,165,49); ours runs
                // bright lime (156,225,12) -> dark (49,118,1), so a 2 px mid-olive line vanished at both ends. Thicker + darker line,
                // short visible drop. v1 was (0.15, 0.12, -0.22, #364B17).
                // 11-16 Spencer ("lighten the stroke a bit around the lettering", picked C of ~/Desktop/continue_text_mock.png): RK style like the
                // Level header - THIN edge, depth in a down-right drop, deep green instead of near-black olive. Play + Continue share it.
                // v2 (above) was (0.22, 0.20, -0.40, #223308).
                // 11-16 later (Spencer: the mock's drop offset "is too much"): firmer edge .08 -> .11, drop halved (.45/-.90 -> .25/-.50)
                ["pill_play"] = (0.11f, 0.25f, -0.50f, new Color32(0x24, 0x4E, 0x10, 0xFF)),
            };
        private static readonly Dictionary<string, Material> s_styled = new Dictionary<string, Material>();
        private static Material Styled(Material baseMat, string key)
        {
            if (key == null || !LABEL_STYLE.TryGetValue(key, out var st)) return baseMat;
            string k = $"{baseMat.name}|{key}";
            if (s_styled.TryGetValue(k, out var m) && m != null) return m;
            m = new Material(baseMat) { name = $"{baseMat.name} {key}" };
            m.SetFloat("_OutlineWidth", st.outline); m.SetColor("_OutlineColor", st.edge);
            if (m.HasProperty("_UnderlayColor"))
            {
                m.SetColor("_UnderlayColor", st.edge);
                m.SetFloat("_UnderlayOffsetX", st.dropX); m.SetFloat("_UnderlayOffsetY", st.dropY);
                if (m.HasProperty("_UnderlayDilate")) m.SetFloat("_UnderlayDilate", st.outline);   // 11-16: the drop = the thin-edged letter, shifted
            }
            ShaderUtilities.UpdateShaderRatios(m);
            s_styled[k] = m; return m;
        }
        private bool? _lilitaOverride;
        private Color _creamTop, _creamBot;

        private void WordGradient(TMP_TextInfo info)
        {
            float yTop = float.MinValue, yBot = float.MaxValue;
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                yTop = Mathf.Max(yTop, ch.topLeft.y); yBot = Mathf.Min(yBot, ch.bottomLeft.y);
            }
            if (yTop <= yBot) return;
            for (int i = 0; i < info.characterCount; i++)
            {
                var ch = info.characterInfo[i];
                if (!ch.isVisible) continue;
                var mi = info.meshInfo[ch.materialReferenceIndex];
                // TMP has already written main.color (= cream top) into the vertices, converted to linear if the
                // project needs it. Compare against the gamma value to know which space to write in.
                Color32 probe = mi.colors32[ch.vertexIndex];
                bool linear = Mathf.Abs(probe.b - (int)(_creamTop.b * 255f)) > 3;
                for (int v = 0; v < 4; v++)
                {
                    int k = ch.vertexIndex + v;
                    // unclamped: the quad's padding extends past the ink, so its corners extrapolate the ramp and
                    // the GPU's interpolation lands the exact colour at the ink edge
                    float t = Mathf.Clamp((yTop - mi.vertices[k].y) / (yTop - yBot), -0.5f, 1.5f);
                    Color c = Color.LerpUnclamped(_creamTop, _creamBot, t);
                    if (linear) c = new Color(Mathf.GammaToLinearSpace(c.r), Mathf.GammaToLinearSpace(c.g), Mathf.GammaToLinearSpace(c.b), c.a);
                    mi.colors32[k] = c;
                }
            }
        }

        private TextMeshProUGUI NewText(string name, RectTransform parent, TMP_FontAsset font, Material mat, string text)
        {
            var t = NewRT(name, parent).gameObject.AddComponent<TextMeshProUGUI>();
            Stretch(t.rectTransform);
            t.font = font;
            t.fontSharedMaterial = mat;
            t.text = text;
            float em = _spec.emPx * (_lilita ? LILITA_SIZE : 1f);
            t.fontSize = em;
            t.enableAutoSizing = true;              // only ever SHRINKS: max is the art's size
            t.fontSizeMax = em;
            t.fontSizeMin = em * 0.5f;
            t.characterSpacing = _lilita ? LILITA_TRACKING : _spec.trackingPerMille / 10f;   // TMP spacing is in 1/100 em; Photoshop's in 1/1000
            t.textWrappingMode = TextWrappingModes.NoWrap;
            // Centre the INK, not the advance box — that is how the art placed it (bounds centred in the box).
            t.horizontalAlignment = HorizontalAlignmentOptions.Geometry;
            t.verticalAlignment   = VerticalAlignmentOptions.Geometry;
            t.raycastTarget = false;
            _texts.Add(t);
            return t;
        }

        // ── Ink ring ────────────────────────────────────────────────────────────────────────────
        private const float SHARPNESS = 0.6f;
        private static readonly Dictionary<string, Material> s_mats = new Dictionary<string, Material>();

        // SPENCER'S DIALED-IN LABEL (2026-10-25, tuned live in the inspector on the purple Level button, then made the default
        // for every button). TMP material units, so they hold at any label size. Replaces the old outlinePx/emPx calibration.
        // These are the FALLBACK only: the real look lives in the material asset Resources/UI/Buttons/ArtButtonLabel.mat
        // (FULL "TextMeshPro/Distance Field" shader, so it also has Bevel, Lighting and Glow). Edit that asset to change the
        // look of every button label; it is created with these values by WordDrop ▸ Art ▸ Create Label Material.
        public const string TEMPLATE_PATH = "UI/Buttons/ArtButtonLabel";
        public const float FACE_DILATE = 0.17f, FACE_SOFTNESS = 0f, OUTLINE_WIDTH = 0.19f;   // 2026-10-26 fitted to RK Level 5 PNG
        public const float DROP_X = 0.45f, DROP_Y = -0.6f, DROP_DILATE = 0f, DROP_SOFT = 0f;   // hard drop shadow, mostly DOWN
        // The template's outline colour is the PURPLE button's (ink #30005B; Spencer's outline #5A187E). Every other button
        // gets the same hue/saturation/value shift from its own ink, so green/blue get the matching outline.
        // Spencer's 2nd pass (21:14): outline #965AB6, thickness 0.21, shadow dilate 0.1 / softness 0.2 - then (21:45, his call
        // "give it a shot") FITTED TO ROYAL KINGDOM's Level 120 label: rendered at RK's letter size and matched the dark edge
        // round the 'L' (% of cap height, RK vs ours): left 5.6/5.3, right 11.1/11.0, below 13.9/13.7, stem 30.6/30.1.
        // Outline AND shadow are the button's own ink (RK: one dark tone of the face), hard-edged.
        public static readonly Color REF_INK = new Color32(0x30, 0x00, 0x5B, 0xFF);
        public static readonly Color REF_OUTLINE = new Color32(0x3F, 0x0D, 0x6B, 0xFF);   // 2026-10-27 Spencer "everything seems so black": RK in-game label outline ~V43 S~85 in the button's hue (was #1A0034 V20)

        private static Color OutlineFromInk(Color ink, Color refOutline) => Shift(REF_INK, refOutline, ink);

        // The colour change from -> to (hue turn, saturation and value ratios), applied to another colour.
        private static Color Shift(Color from, Color to, Color apply)
        {
            Color.RGBToHSV(from, out float rh, out float rs, out float rv);
            Color.RGBToHSV(to, out float oh, out float os, out float ov);
            Color.RGBToHSV(apply, out float h, out float sat, out float v);
            float dh = Mathf.DeltaAngle(rh * 360f, oh * 360f) / 360f;
            var c = Color.HSVToRGB(Mathf.Repeat(h + dh, 1f), Mathf.Clamp01(sat * (rs > 0.001f ? os / rs : 1f)),
                                   Mathf.Clamp01(v * (rv > 0.001f ? ov / rv : 1f)));
            c.a = to.a; return c;
        }

        /// <summary>Forget the cached label materials so the next button re-reads ArtButtonLabel.mat (editor renders).</summary>
        public static void ClearMaterialCache() { s_mats.Clear(); s_live.Clear(); s_styled.Clear(); }

        private class Live { public Material mat, snap; public TMP_FontAsset font; public Color ink; }
        private static readonly List<Live> s_live = new List<Live>();

        private static Material InkMaterial(TMP_FontAsset font, Color ink)
        {
            string key = $"{font.name}|{ColorUtility.ToHtmlStringRGB(ink)}";
            if (s_mats.TryGetValue(key, out var m) && m != null) return m;
            var template = Resources.Load<Material>(TEMPLATE_PATH);
            if (template != null) m = new Material(template);
            else
            {
                Debug.LogWarning($"[ArtButton] no Resources/{TEMPLATE_PATH}.mat — using built-in label defaults");
                m = new Material(font.material);
                ApplyDefaults(m);
            }
            m.name = $"{font.name} ArtButton {ColorUtility.ToHtmlStringRGB(ink)}";
            Rebind(m, font, ink, template);
            s_mats[key] = m;
            s_live.Add(new Live { mat = m, font = font, ink = ink });
            return m;
        }

        /// <summary>Spencer's values onto a material (used for the fallback and to create the template asset).</summary>
        public static void ApplyDefaults(Material m)
        {
            m.EnableKeyword("OUTLINE_ON");          // TMP_SDF-Mobile ignores _OutlineWidth without it (2026-10-22 find)
            m.SetColor("_FaceColor", Color.white);
            m.SetFloat("_FaceDilate", FACE_DILATE);
            m.SetFloat("_OutlineSoftness", FACE_SOFTNESS);
            m.SetColor("_OutlineColor", REF_OUTLINE);
            m.SetFloat("_OutlineWidth", OUTLINE_WIDTH);
            m.EnableKeyword("UNDERLAY_ON"); m.DisableKeyword("UNDERLAY_INNER");
            m.SetColor("_UnderlayColor", REF_OUTLINE);
            m.SetFloat("_UnderlayOffsetX", DROP_X);
            m.SetFloat("_UnderlayOffsetY", DROP_Y);
            m.SetFloat("_UnderlayDilate", DROP_DILATE);
            m.SetFloat("_UnderlaySoftness", DROP_SOFT);
            // Photoshop's edges are crisp after its 4x supersample; TMP's default edge is ~1px softer at the in-game
            // size (72% of the art), which read as a weaker ring. Measured on the "L" from Spencer's capture.
            if (m.HasProperty("_Sharpness")) m.SetFloat("_Sharpness", SHARPNESS);
        }

        // The template knows nothing about the font atlas or the button colour: point it at this font and recolour the outline.
        private static void Rebind(Material m, TMP_FontAsset font, Color ink, Material template)
        {
            var src = font.material;
            m.SetTexture("_MainTex", src.GetTexture("_MainTex"));
            foreach (var p in new[] { "_GradientScale", "_TextureWidth", "_TextureHeight", "_WeightNormal", "_WeightBold" })
                if (src.HasProperty(p) && m.HasProperty(p)) m.SetFloat(p, src.GetFloat(p));
            m.SetColor("_OutlineColor", OutlineFromInk(ink, template != null ? template.GetColor("_OutlineColor") : REF_OUTLINE));
            // the drop shadow is recoloured the same way (RK: outline and shadow are ONE dark tone of the face; black stays black)
            if (m.HasProperty("_UnderlayColor"))
                m.SetColor("_UnderlayColor", Shift(REF_INK, template != null ? template.GetColor("_UnderlayColor") : REF_INK, ink));
            ShaderUtilities.UpdateShaderRatios(m);   // keeps outline/underlay/glow inside the glyph padding
        }

#if UNITY_EDITOR
        // LIVE TUNING (both ways): while playing in the editor, edit EITHER ArtButtonLabel.mat OR the material on a button's
        // Text object - the change is written into ArtButtonLabel.mat (kept when Play stops) and shown on every button.
        // Spencer 2026-10-25: ticking Lighting on the Text object's material snapped back, because only the asset synced.
        private static float s_nextSync; private static int s_syncFrame = -1;
        private void Update()
        {
            if (s_syncFrame == Time.frameCount || Time.unscaledTime < s_nextSync) return;
            s_syncFrame = Time.frameCount; s_nextSync = Time.unscaledTime + 0.2f;
            var template = Resources.Load<Material>(TEMPLATE_PATH);
            if (template == null) return;
            s_live.RemoveAll(l => l.mat == null);
            foreach (var l in s_live)   // an edit made on a button's own copy -> into the template
            {
                if (l.snap == null || !Differs(l.mat, l.snap)) continue;
                var outline = l.mat.GetColor("_OutlineColor"); var under = l.mat.GetColor("_UnderlayColor");
                if (template.shader != l.mat.shader) template.shader = l.mat.shader;
                template.CopyPropertiesFromMaterial(l.mat);
                template.shaderKeywords = l.mat.shaderKeywords;
                template.SetColor("_OutlineColor", Shift(l.ink, outline, REF_INK));   // stored as the PURPLE button's colours
                template.SetColor("_UnderlayColor", Shift(l.ink, under, REF_INK));
                UnityEditor.EditorUtility.SetDirty(template);
                break;
            }
            foreach (var l in s_live)   // the template -> every button's copy
            {
                if (l.mat.shader != template.shader) l.mat.shader = template.shader;
                l.mat.CopyPropertiesFromMaterial(template);
                l.mat.shaderKeywords = template.shaderKeywords;
                Rebind(l.mat, l.font, l.ink, template);
                if (l.snap == null) l.snap = new Material(l.mat) { hideFlags = HideFlags.HideAndDontSave };
                else { l.snap.shader = l.mat.shader; l.snap.CopyPropertiesFromMaterial(l.mat); l.snap.shaderKeywords = l.mat.shaderKeywords; }
            }
        }

        private static bool Differs(Material a, Material b)
        {
            if (a.shader != b.shader) return true;
            if (string.Join(",", a.shaderKeywords) != string.Join(",", b.shaderKeywords)) return true;
            var sh = a.shader;
            for (int i = 0, n = sh.GetPropertyCount(); i < n; i++)
            {
                int id = sh.GetPropertyNameId(i);
                switch (sh.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        if (a.GetVector(id) != b.GetVector(id)) return true; break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        if (!Mathf.Approximately(a.GetFloat(id), b.GetFloat(id))) return true; break;
                }
            }
            return false;
        }
#endif

        // Tuned against the Photoshop render, like the ring above.
        private const float SHADE_OFFSET = 0.30f, SHADE_SOFT = 0.38f, SHADE_ALPHA = 0.85f;
        // the art's inner shadow is a #D6EDD2 MULTIPLY over the cream, so the shade is derived from the label's own cream
        // (#FFFCDE gave the measured green-tinted #D6EAB7; the whiter RK cream gives a near-neutral shade). 2026-10-25.
        private static readonly Color SHADE_MUL = new Color32(0xD6, 0xED, 0xD2, 0xFF);

        private static Material ShadeMaterial(TMP_FontAsset font, Color cream)
        {
            string key = $"{font.name}|shade|{ColorUtility.ToHtmlStringRGB(cream)}";
            if (s_mats.TryGetValue(key, out var m) && m != null) return m;
            m = new Material(font.material) { name = $"{font.name} ArtButton InnerShade" };
            m.DisableKeyword("OUTLINE_ON"); m.DisableKeyword("UNDERLAY_ON");
            m.EnableKeyword("UNDERLAY_INNER");
            m.SetColor("_FaceColor", new Color(1f, 1f, 1f, 0f));     // face invisible: only the inner underlay shows
            var c = cream * SHADE_MUL; c.a = SHADE_ALPHA;
            m.SetColor("_UnderlayColor", c);
            m.SetFloat("_UnderlayOffsetX", 0f);
            m.SetFloat("_UnderlayOffsetY", -SHADE_OFFSET);            // shape shifted DOWN -> shade along the TOP inside edge
            m.SetFloat("_UnderlayDilate", 0f);
            m.SetFloat("_UnderlaySoftness", SHADE_SOFT);
            s_mats[key] = m;
            return m;
        }

        // ── Size ────────────────────────────────────────────────────────────────────────────────
        private void OnRectTransformDimensionsChange() { if (_art != null) Fit(); }

        /// <summary>Re-scale the art to the root's current size. Call after resizing the root — the resize callback
        /// above only runs in Play mode, and the size at Create time is Unity's default 100x100.</summary>
        public void Refit() { if (_art != null) Fit(); }

        /// <summary>11-18 (card text hierarchy): make this button's label <paramref name="mul"/> x the art's size. The label still
        /// only ever shrinks to fit the face's width. For the small card buttons (Quit, Reset): Spencer picked 6.8% of the
        /// card's width from ~/Documents/WordDrop_Art/text_mock_1118/quit_button_label_options.png (was 5.2%; RK's 9.3% sits
        /// on a bigger button - "the text on the buttons are too big on ours cause our buttons are smaller").</summary>
        public static float SMALL_BUTTON_LABEL = 6.8f / 5.2f;
        public void ScaleLabel(float mul)
        {
            if (mul <= 0f || Face == null) return;
            foreach (var t in Face.GetComponentsInChildren<TextMeshProUGUI>(true))
            { t.fontSizeMax *= mul; t.fontSizeMin *= mul; t.fontSize *= mul; }
            // the label box is 2 em tall because auto-size also shrinks for HEIGHT (WordSlop's line is ~1.6 em): grow it with the text
            if (Face.Find("Label") is RectTransform lbl) lbl.sizeDelta = new Vector2(lbl.sizeDelta.x, lbl.sizeDelta.y * mul);
        }

        /// <summary>11-16 (Spencer: "why dont you just extend the buttons by width since arent they 9-sliced? then you can fit all the
        /// letters in there"): widen the pill by <paramref name="extra"/> art px. Frame + face become 9-sliced with fixed ends of
        /// <paramref name="endPx"/> (pill_play: the frame's round end reaches x 166, the face's 187 -> 200 covers both), so the rounded
        /// ends keep their shape and only the straight middle stretches. The face pivot, label box and fit box move with it.
        /// Call AFTER any face-sprite swap, then size the root to the wider aspect and Refit.</summary>
        public void Widen(float extra, float endPx = 200f)
        {
            if (_art == null || Face == null || extra <= 0f) return;
            var frameImg = _art.Find("Frame") != null ? _art.Find("Frame").GetComponent<Image>() : null;
            var faceImg = Face.Find("FaceArt") != null ? Face.Find("FaceArt").GetComponent<Image>() : null;
            foreach (var img in new[] { frameImg, faceImg })
            {
                if (img == null || img.sprite == null) continue;
                var t = img.sprite.texture;
                img.sprite = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(endPx, 0f, endPx, 0f));
                img.type = Image.Type.Sliced; img.pixelsPerUnitMultiplier = 1f;
            }
            _canvas.x += extra; _art.sizeDelta = _canvas;
            _spec.frameBox[2] += Mathf.RoundToInt(extra); _spec.face[2] += extra; _spec.textBox[0] += extra * 0.5f;
            Face.pivot = new Vector2((_spec.face[0] + _spec.face[2] * 0.5f) / _canvas.x, Face.pivot.y);
            var label = Face.Find("Label") as RectTransform;
            if (label != null)
            {
                float c = _spec.condense > 0f ? _spec.condense : 1f;
                label.anchoredPosition += new Vector2(extra * 0.5f, 0f);
                label.sizeDelta = new Vector2(_spec.face[2] * FIT_WIDTH / c, label.sizeDelta.y);
            }
            Fit();
        }
        /// <summary>Width/height of the visible frame in art px (after any Widen) - for sizing the root to the art's aspect.</summary>
        public Vector2 FrameSize => _spec != null ? new Vector2(_spec.frameBox[2], _spec.frameBox[3]) : Vector2.one;

        private void Fit()
        {
            Vector2 size = _rt.rect.size;
            if (size.x <= 1f || size.y <= 1f) return;
            var fb = _spec.frameBox;
            float s = Mathf.Min(size.x / fb[2], size.y / fb[3]);
            _art.localScale = new Vector3(s, s, 1f);
            // canvas centre -> frame centre, in art px (y up)
            float dx = _canvas.x * 0.5f - (fb[0] + fb[2] * 0.5f);
            float dy = (fb[1] + fb[3] * 0.5f) - _canvas.y * 0.5f;
            _art.anchoredPosition = new Vector2(dx, dy) * s;
        }

        // ── Press ───────────────────────────────────────────────────────────────────────────────
        public void OnPointerDown(PointerEventData e)
        {
            if (Button == null || !Button.IsInteractable()) return;
            _down = true;
            Face.DOKill();
            Face.DOScale(new Vector3(PRESS_X, PRESS_Y, 1f), PRESS_T).SetEase(Ease.OutQuad).SetUpdate(true);
            if (PlaySounds) GameAudio.Instance?.PlayMultiPopPress();
            HapticsManager.ButtonTap();
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (!_down) return;
            _down = false;
            Face.DOKill();
            DOTween.Sequence().SetTarget(Face).SetUpdate(true)
                .Append(Face.DOScale(new Vector3(OVERSHOOT, OVERSHOOT, 1f), 0.07f).SetEase(Ease.OutQuad))
                .Append(Face.DOScale(Vector3.one, 0.09f).SetEase(Ease.InOutSine));
        }

        private void OnDisable()
        {
            _down = false;
            if (Face != null) { Face.DOKill(); Face.localScale = Vector3.one; }
        }

        // ── helpers ─────────────────────────────────────────────────────────────────────────────
        private static RectTransform NewRT(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        private static Image NewImage(string name, Transform parent, Sprite sprite)
        {
            var img = NewRT(name, parent).gameObject.AddComponent<Image>();
            Stretch(img.rectTransform);
            img.sprite = sprite;
            return img;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
        }

        private static Color Hex(string h) => ColorUtility.TryParseHtmlString(h, out var c) ? c : Color.black;
    }
}
