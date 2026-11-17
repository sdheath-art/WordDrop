using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>TOP HUD BAR on the level map (2026-10-27 Spencer: "a bevelled HUD bar ... purple bar, creme panels to display
    /// numbers"). One pre-rendered bar (Resources/UI/HudBar/hud_bar.png: frame, cream panels, gold portrait ring + placeholder
    /// avatar, contact shadow) plus the heart, coin and '+' as their own sprites, all built by
    /// WordDrop_Art/vector_buttons/hud/hud_bar.py (EXPORT_DIR). Every position here is in that script's 1179 x 250 design
    /// space (see hud_layout.json next to the art) and is applied as ANCHORS, so the whole bar scales with the screen width.
    /// The numbers are live text:
    ///   - heart: lives left ON the heart (cream, heart-dark outline), and "Full" or the time to the next heart in its panel
    ///     (HeartsManager: 5 lives, one every 30 min) - ticks every half second while the bar is alive;
    ///   - coins: set by LevelMapPanel, which also flies the reward coins into <see cref="CoinIcon"/>.</summary>
    public class HudBar : MonoBehaviour
    {
        // design space (hud_bar.py: W1, H1) and the spots it exported
        private const float DW = 1179f, DH = 250f;
        public static float PORTRAIT_R = 83f;   // match_home_ring3.py: photo r 85 inside a thicker gold ring; 83 leaves its dark separation line showing
        private const float BAR_TOP = 28f, BAR_CY = 114f;                 // bar box 28..200, portrait centre 114
        private const float VIS_TOP = 4f;                                 // highest drawn pixel: the portrait ring + outline (centre 114, r ~110)
        public static float SAFE_MARGIN_FRAC = 0.012f;                    // clear gap under the safe area (~10pt on an iPhone)
        private const float CENTER_FRAC = 0.082f;                         // bar centre at 8.2% of screen height (Spencer's RK ref)
        private static readonly Color INK = new Color32(0x41, 0x1A, 0x6B, 0xFF);           // dark purple numbers
        private static readonly Color COUNT_OUTLINE = new Color32(0x73, 0x17, 0x26, 0xFF); // the heart's own dark (ref '5')
        private static readonly Color CREAM_TOP = new Color32(0xFF, 0xFA, 0xE0, 0xFF), CREAM_BOT = new Color32(0xFC, 0xEE, 0xC4, 0xFF);

        public TextMeshProUGUI HeartCount { get; private set; }
        public TextMeshProUGUI HeartLabel { get; private set; }
        public TextMeshProUGUI CoinLabel { get; private set; }
        public RectTransform HeartIcon { get; private set; }
        public RectTransform CoinIcon { get; private set; }
        public Button PlusButton { get; private set; }
        public Button PortraitButton { get; private set; }

        private RectTransform _rt, _canvasRT;
        private readonly System.Collections.Generic.List<(TextMeshProUGUI t, float em)> _texts = new System.Collections.Generic.List<(TextMeshProUGUI, float)>();
        private float _k = -1f;
        private bool _loggedPlace;
        private float _nextTick;

        public static HudBar Create(Transform parent)
        {
            var go = new GameObject("HudBar", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var hb = go.AddComponent<HudBar>();
            if (!hb.Build()) { Destroy(go); return null; }
            return hb;
        }

        private bool Build()
        {
            var bar = Load("hud_bar"); var heart = Load("hud_heart"); var coin = Load("hud_coin"); var plus = Load("hud_plus");
            if (bar == null || heart == null || coin == null || plus == null) { Debug.LogWarning("[HudBar] art missing in Resources/UI/HudBar"); return false; }

            _rt = (RectTransform)transform;
            _rt.anchorMin = new Vector2(0f, 1f); _rt.anchorMax = new Vector2(1f, 1f);
            _rt.pivot = new Vector2(0.5f, 1f);
            _rt.offsetMin = _rt.offsetMax = Vector2.zero;
            var fit = gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
            fit.aspectRatio = DW / DH;
            var img = GetComponent<Image>(); img.sprite = bar; img.raycastTarget = false;

            // portrait: the SAME picture as the in-level HUD (PlayerProfile, 2026-10-29 Spencer: "they should be linked in code so
            // their same profile appears") laid over the baked placeholder in the photo well (hud_layout.json portrait: centre
            // 116,114 r 76) - tappable later for the profile screen
            var pic = MakeIcon("ProfilePicture", PlayerProfile.Avatar, 116f, 114f, 2f * PORTRAIT_R);
            PlayerProfile.Bind(pic.GetComponent<Image>());
            PortraitButton = Hit("Portrait", 116f, 114f, 180f, 180f);

            HeartIcon = MakeIcon("Heart", heart, 314f, 110f, 192f);
            HeartCount = Text("HeartCount", 314f, 106f, 50f, 100f);
            StyleCount(HeartCount);
            HeartLabel = Text("HeartLabel", 487f, 113f, 46f, 230f);

            CoinIcon = MakeIcon("Coin", coin, 746f, 110f, 183f);
            var plusRT = MakeIcon("Plus", plus, 786f, 148f, 103.5f);
            PlusButton = Hit("PlusHit", 786f, 148f, 80f, 80f);
            plusRT.SetAsLastSibling(); PlusButton.transform.SetAsLastSibling();
            CoinLabel = Text("CoinLabel", 965f, 113f, 46f, 290f);

            // SHINE ON TAP (2026-10-29, Spencer's RK crown GIF): tapping a panel sweeps a glint across it (ShineSweep). The cream
            // panels are baked into the bar, so each gets an invisible panel-sized rect (hud_layout heartPanel / coinPanel) to clip to.
            var heartPanel = Box("HeartPanelShine", 455f, 112f, 310f, 96f, typeof(RectTransform));
            var coinPanel = Box("CoinPanelShine", 930f, 112f, 380f, 96f, typeof(RectTransform));
            var heartHit = Hit("HeartPanelHit", 455f, 112f, 330f, 110f); heartHit.transform.SetAsFirstSibling();
            var coinHit = Hit("CoinPanelHit", 930f, 112f, 400f, 110f); coinHit.transform.SetAsFirstSibling();
            heartHit.onClick.AddListener(() => { ShineSweep.PlayMask(_rt, "heart_panel"); GameAudio.Instance?.PlayUIClick(); });
            coinHit.onClick.AddListener(() => { ShineSweep.PlayMask(_rt, "coin_panel"); GameAudio.Instance?.PlayUIClick(); });
            var picImg = pic.GetComponent<Image>();
            PortraitButton.onClick.AddListener(() => ShineSweep.Play(picImg));
            RefreshHearts();
            return true;
        }

        // Sprite import (ButtonArtImporter) or, if the PNG came in as a plain texture, a sprite made from it - the art
        // landed before the importer knew this folder on 2026-10-27 and the bar silently fell back to the old pills.
        private static Sprite Load(string name)
        {
            var s = Resources.Load<Sprite>("UI/HudBar/" + name);
            if (s != null) return s;
            var t = Resources.Load<Texture2D>("UI/HudBar/" + name);
            if (t == null) return null;
            Debug.LogWarning($"[HudBar] {name} imported as a Texture, not a Sprite - using it anyway (Reimport UI/HudBar to fix)");
            return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private void OnEnable() { _nextTick = 0f; }

        private void Update()
        {
            Place();
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + 0.5f;
            RefreshHearts();
        }

        /// <summary>Lives on the heart; "Full" or the countdown to the next heart in the panel.</summary>
        public void RefreshHearts()
        {
            if (HeartCount == null) return;
            int n = HeartsManager.Current;
            HeartCount.text = n.ToString();
            if (n >= HeartsManager.MAX_HEARTS) HeartLabel.text = "Full";
            else
            {
                TimeSpan t = HeartsManager.TimeUntilNextHeart;
                HeartLabel.text = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
            }
        }

        // Vertical placement: the bar's centre at 8.2% of the screen height, but its top edge never above the safe area.
        private void Place()
        {
            if (_canvasRT == null) { var c = GetComponentInParent<Canvas>(); if (c == null) return; _canvasRT = (RectTransform)c.rootCanvas.transform; }
            float ch = _canvasRT.rect.height, h = _rt.rect.height;
            if (ch < 1f || h < 1f) return;
            float k = h / DH;                                                     // canvas units per design px
            float top = ch * CENTER_FRAC - BAR_CY * k;                            // rect top below the screen top
            Rect sa = Screen.safeArea;
            float inset = Screen.height > 0 ? (Screen.height - sa.yMax) / Screen.height * ch : 0f;
            // 2026-10-27 Spencer (on his iPhone): "the camera pill is still on top of it a bit". The check kept the BAR's top
            // edge below the safe area, but the portrait ring rises ~24 design px above the bar. Now the highest drawn pixel
            // (the ring) clears the safe area by a small margin.
            top = Mathf.Max(top, inset + ch * SAFE_MARGIN_FRAC - VIS_TOP * k);
            if (Mathf.Abs(k - _k) > 0.0001f) { _k = k; foreach (var (t, em) in _texts) if (t != null) t.fontSize = em * k; }
            if (!_loggedPlace) { _loggedPlace = true; Debug.Log($"[HudBar] screen {Screen.width}x{Screen.height} safeArea {Screen.safeArea} canvasH {ch:F0} barH {h:F0} top {top:F1} (inset {inset:F1})"); }
            var p = new Vector2(0f, -top);
            if ((_rt.anchoredPosition - p).sqrMagnitude > 0.01f) _rt.anchoredPosition = p;
        }

        // ---- builders: design-space centre + size -> anchors, so everything scales with the bar ----
        private RectTransform Box(string name, float cx, float cy, float w, float h, params Type[] comps)
        {
            var go = new GameObject(name, comps); go.transform.SetParent(transform, false);
            var r = (RectTransform)go.transform;
            r.anchorMin = new Vector2((cx - w / 2f) / DW, 1f - (cy + h / 2f) / DH);
            r.anchorMax = new Vector2((cx + w / 2f) / DW, 1f - (cy - h / 2f) / DH);
            r.offsetMin = r.offsetMax = Vector2.zero;
            return r;
        }

        private RectTransform MakeIcon(string name, Sprite s, float cx, float cy, float box)
        {
            var r = Box(name, cx, cy, box, box, typeof(RectTransform), typeof(Image));
            var im = r.GetComponent<Image>(); im.sprite = s; im.raycastTarget = false; im.preserveAspect = true;
            return r;
        }

        private Button Hit(string name, float cx, float cy, float w, float h)
        {
            var r = Box(name, cx, cy, w, h, typeof(RectTransform), typeof(Image), typeof(Button));
            r.GetComponent<Image>().color = new Color(0, 0, 0, 0);
            var b = r.GetComponent<Button>(); b.transition = Selectable.Transition.None;
            return b;
        }

        // A text box one em tall for the measured cap height (WordSlop cap = 0.70 em); auto-size keeps it to that box.
        private TextMeshProUGUI Text(string name, float cx, float cy, float cap, float width)
        {
            float em = cap / 0.70f;
            var r = Box(name, cx, cy, width, em * 1.15f, typeof(RectTransform), typeof(TextMeshProUGUI));
            var t = r.GetComponent<TextMeshProUGUI>();
            // the BUTTON cut (bold, the one the mockup and every ArtButton label use), not the regular "WordSlop SDF"
            var f = Resources.Load<TMP_FontAsset>("WordSlop Button SDF") ?? Resources.Load<TMP_FontAsset>(GameFont.WORDSLOP_FONT) ?? GameFont.GetUITMP();
            if (f != null) t.font = f;
            // centred on the GLYPHS (like the mockup), not on the line box
            t.color = INK; t.alignment = TextAlignmentOptions.CenterGeoAligned; t.raycastTarget = false;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow;
            t.enableAutoSizing = false;                   // sized from the bar in Place(): em = cap / 0.70 (auto-size fit the LINE box, ~25% small)
            _texts.Add((t, em));
            return t;
        }

        // The number on the heart: cream with a thin outline + a small straight-down drop in the heart's own dark (the ref "5",
        // scaled down twice by Spencer: outline ~8% of the cap, drop ~2%).
        private static void StyleCount(TextMeshProUGUI t)
        {
            t.enableVertexGradient = true;
            t.colorGradient = new VertexGradient(CREAM_TOP, CREAM_TOP, CREAM_BOT, CREAM_BOT);
            t.color = Color.white;
            var m = new Material(t.fontSharedMaterial) { name = "HudBar HeartCount" };
            m.EnableKeyword("OUTLINE_ON");
            m.SetColor("_OutlineColor", COUNT_OUTLINE);
            m.SetFloat("_OutlineWidth", 0.22f);
            m.SetFloat("_FaceDilate", 0.18f);
            m.EnableKeyword("UNDERLAY_ON");
            m.SetColor("_UnderlayColor", COUNT_OUTLINE);
            m.SetFloat("_UnderlayOffsetX", 0.12f);
            m.SetFloat("_UnderlayOffsetY", -0.3f);
            m.SetFloat("_UnderlayDilate", 0.4f);
            m.SetFloat("_UnderlaySoftness", 0f);
            ShaderUtilities.UpdateShaderRatios(m);
            t.fontSharedMaterial = m;
        }
    }
}

// recompile nudge 2026-10-27 12:46 (Unity cached these at 12:40:16 and missed the 12:40:17 save)
