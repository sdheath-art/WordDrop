using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>BOTTOM NAV BAR (2026-10-27 Spencer: "get it into the game so that when you click on one of the panels it
    /// becomes the domed panel"). Five pre-rendered states (Resources/UI/NavBar/navbar_sel{i}.png, built by
    /// WordDrop_Art/vector_buttons/navbar/navbar_sel.py — purple, dome, the v3 rules + the signature contact shadow along its
    /// top). The bar spans the screen width at its art aspect, pinned to the bottom; five invisible hit areas pick a tab, the
    /// sprite swaps to that tab's state, and the new dome pops. Tabs have no content yet: <see cref="OnTabSelected"/> is the hook.</summary>
    public class NavBar : MonoBehaviour
    {
        public const int TABS = 5;
        // art geometry (1x px of the 1080-wide design; the PNGs are 2x)
        private const float ART_W = 1080f, ART_H = 272f, RISE = 66f, SEAM = 3f, SEL_W = 284f;   // = navbar_sel.py
        public static int DefaultTab = 2;
        public int Selected { get; private set; } = -1;
        public System.Action<int> OnTabSelected;

        private Image _img;
        private RectTransform _rt;
        private readonly Sprite[] _states = new Sprite[TABS];
        private readonly RectTransform[] _hits = new RectTransform[TABS];

        // TAB ICONS (2026-10-27). Shop · Daily · Home · Areas · Profile (project_wordrop_nav_tabs_2026_10_27). Each icon is two
        // pre-rendered 2x sprites with the tab-hue contact shadow baked in (navbar/icon_tab_shadow.py, measured from RK):
        // Resources/UI/NavBar/Icons/<key>_sel (on the dome) and <key>_flat (on a flat tab). Sprite px / 2 = design px of the
        // 1080-wide bar. SELECTED: top at 35.5, fitted fully inside the dome's purple face with a 4px margin (Spencer: "the icon
        // needs to be fully on the purple panel"), label below. FLAT: top at 82, no label. A null key = no icon yet.
        public static readonly string[] ICON_KEYS = { "nav_shop", "nav_daily", "nav_home", "nav_areas", "nav_settings" };
        public static readonly string[] LABELS = { "Shop", "Daily", "Home", "Areas", "Settings" };   // tab 5 = Settings (Spencer 2026-10-27); Profile = the HUD portrait
        // sprites are FIXED canvases over the slot (navbar/export_nav_icons.py): _sel = SEL_W x 220 from the top, _flat = slot x 203 from y 69
        private const float SEL_CANVAS_H = 220f, FLAT_TOP = 69f, LABEL_BASELINE = 238f, LABEL_CAP = 28f;
        private static readonly Color LABEL_INK = new Color32(0x30, 0x10, 0x50, 0xFF), LABEL_CREAM = new Color32(0xFF, 0xF8, 0xDC, 0xFF);
        private readonly Image[] _icons = new Image[TABS];
        private readonly Sprite[] _iconSel = new Sprite[TABS], _iconFlat = new Sprite[TABS];
        private TextMeshProUGUI _label;
        // SAFE AREA (2026-10-27, Spencer on his iPhone: "the shop and gear icons are getting cut off by the edges"). The bar sat in
        // the home-indicator zone, where the screen's rounded corners cut the end tabs. The bar is now lifted by the bottom safe
        // inset and the gap below is filled by the art's own bottom rows stretched down (seams and dome sides continue).
        private Image _fill; private RectTransform _canvasRT; private float _lastInset = -1f; private int _fillFor = -1;
        private float _labelK = -1f;

        public static NavBar Create(Transform parent)
        {
            var go = new GameObject("NavBar", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var nb = go.AddComponent<NavBar>();
            if (!nb.Build()) { Destroy(go); return null; }
            return nb;
        }

        private bool Build()
        {
            for (int i = 0; i < TABS; i++)
            {
                _states[i] = Resources.Load<Sprite>($"UI/NavBar/navbar_sel{i}");
                if (_states[i] == null) { Debug.LogWarning($"[NavBar] missing UI/NavBar/navbar_sel{i}"); return false; }
            }
            _rt = (RectTransform)transform;
            _rt.anchorMin = new Vector2(0f, 0f); _rt.anchorMax = new Vector2(1f, 0f);
            _rt.pivot = new Vector2(0.5f, 0f);
            _rt.offsetMin = _rt.offsetMax = Vector2.zero;
            var fit = gameObject.AddComponent<AspectRatioFitter>();
            fit.aspectMode = AspectRatioFitter.AspectMode.WidthControlsHeight;
            fit.aspectRatio = ART_W / ART_H;
            _img = GetComponent<Image>();
            _img.raycastTarget = false;
            _img.preserveAspect = false;

            for (int i = 0; i < TABS; i++)
            {
                int tab = i;
                var hit = new GameObject($"Tab{i}", typeof(RectTransform), typeof(Image), typeof(Button));
                hit.transform.SetParent(transform, false);
                _hits[i] = (RectTransform)hit.transform;
                var him = hit.GetComponent<Image>(); him.color = new Color(0, 0, 0, 0); him.raycastTarget = true;
                var b = hit.GetComponent<Button>(); b.transition = Selectable.Transition.None;
                b.onClick.AddListener(() => Select(tab, true));
            }
            BuildIcons();
            var fg = new GameObject("SafeAreaFill", typeof(RectTransform), typeof(Image));
            fg.transform.SetParent(transform, false); fg.transform.SetAsFirstSibling();
            _fill = fg.GetComponent<Image>(); _fill.raycastTarget = true;   // eats taps in the strip (no clicks through to the map)
            var fr = (RectTransform)fg.transform; fr.anchorMin = new Vector2(0f, 0f); fr.anchorMax = new Vector2(1f, 0f); fr.pivot = new Vector2(0.5f, 1f);
            fr.anchoredPosition = Vector2.zero; fr.sizeDelta = new Vector2(0f, 0f);
            Select(DefaultTab, false);
            return true;
        }

        private void BuildIcons()
        {
            for (int i = 0; i < TABS; i++)
            {
                if (string.IsNullOrEmpty(ICON_KEYS[i])) continue;
                _iconSel[i] = LoadSprite($"UI/NavBar/Icons/{ICON_KEYS[i]}_sel");
                _iconFlat[i] = LoadSprite($"UI/NavBar/Icons/{ICON_KEYS[i]}_flat");
                if (_iconSel[i] == null && _iconFlat[i] == null) continue;
                var go = new GameObject($"Icon{i}", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(transform, false);
                go.transform.SetSiblingIndex(0);                         // under the (invisible) hit buttons
                _icons[i] = go.GetComponent<Image>(); _icons[i].raycastTarget = false; _icons[i].preserveAspect = false;
            }
            var lg = new GameObject("TabLabel", typeof(RectTransform), typeof(TextMeshProUGUI));
            lg.transform.SetParent(transform, false); lg.transform.SetSiblingIndex(0);
            _label = lg.GetComponent<TextMeshProUGUI>();
            var f = Resources.Load<TMP_FontAsset>("WordSlop Button SDF") ?? GameFont.GetUITMP();
            if (f != null) _label.font = f;
            _label.alignment = TextAlignmentOptions.CenterGeoAligned;   // box centred on the caps (LayoutIcons) _label.raycastTarget = false;
            _label.enableWordWrapping = false; _label.overflowMode = TextOverflowModes.Overflow; _label.enableAutoSizing = false;
            _label.color = LABEL_CREAM;
            var m = new Material(_label.fontSharedMaterial) { name = "NavBar Label" };
            m.EnableKeyword("OUTLINE_ON"); m.SetColor("_OutlineColor", LABEL_INK); m.SetFloat("_OutlineWidth", 0.22f); m.SetFloat("_FaceDilate", 0.15f);
            m.EnableKeyword("UNDERLAY_ON"); m.SetColor("_UnderlayColor", LABEL_INK); m.SetFloat("_UnderlayOffsetY", -0.5f); m.SetFloat("_UnderlayDilate", 0.3f);
            ShaderUtilities.UpdateShaderRatios(m); _label.fontSharedMaterial = m;
        }

        private void ApplySafeArea()
        {
            if (_canvasRT == null) { var c = GetComponentInParent<Canvas>(); if (c == null) return; _canvasRT = (RectTransform)c.rootCanvas.transform; }
            float ch = _canvasRT.rect.height; if (ch < 1f || Screen.height <= 0) return;
            float inset = Screen.safeArea.yMin / Screen.height * ch;
            if (Mathf.Abs(inset - _lastInset) > 0.01f)
            {
                _lastInset = inset;
                _rt.anchoredPosition = new Vector2(0f, inset);
                ((RectTransform)_fill.transform).sizeDelta = new Vector2(0f, inset);
                _fill.enabled = inset > 0.5f;
            }
            if (_fillFor != Selected && Selected >= 0 && _states[Selected] != null)
            {
                _fillFor = Selected; var t = _states[Selected].texture;
                _fill.sprite = Sprite.Create(t, new Rect(0, 0, t.width, 4), new Vector2(0.5f, 0.5f), 100f);   // the art's bottom rows
            }
        }

        private static Sprite LoadSprite(string path)
        {
            var sp = Resources.Load<Sprite>(path);
            if (sp != null) return sp;
            var t = Resources.Load<Texture2D>(path);
            return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }

        // Icon rects follow the same slot maths as the hit areas; the label sits under the selected tab's icon.
        private void LayoutIcons(int sel)
        {
            float wo = (ART_W - SEL_W - SEAM * (TABS - 1)) / (TABS - 1), x = 0f;
            for (int i = 0; i < TABS; i++)
            {
                float w = i == sel ? SEL_W : wo, cx = x + w * 0.5f;
                x += w + SEAM;
                if (i == sel && _label != null)
                {
                    _label.text = LABELS[i];
                    var lr = _label.rectTransform; float half = w * 0.5f;
                    lr.anchorMin = new Vector2((cx - half) / ART_W, 1f - (LABEL_BASELINE + 10f) / ART_H);              // box centre =
                    lr.anchorMax = new Vector2((cx + half) / ART_W, 1f - (LABEL_BASELINE - LABEL_CAP - 10f) / ART_H);  // the caps' centre
                    lr.offsetMin = lr.offsetMax = Vector2.zero;
                    _label.margin = Vector4.zero;
                }
                var img = _icons[i]; if (img == null) continue;
                var sp = i == sel ? _iconSel[i] : _iconFlat[i];
                img.enabled = sp != null; if (sp == null) continue;
                img.sprite = sp;
                float top = i == sel ? 0f : FLAT_TOP, bottom = i == sel ? SEL_CANVAS_H : ART_H;
                var r = img.rectTransform;
                r.anchorMin = new Vector2((cx - w * 0.5f) / ART_W, 1f - bottom / ART_H);
                r.anchorMax = new Vector2((cx + w * 0.5f) / ART_W, 1f - top / ART_H);
                r.offsetMin = r.offsetMax = Vector2.zero;
            }
        }

        // The label's font size follows the bar's on-screen size: cap height = LABEL_CAP design px (WordSlop cap = 0.70 em).
        private void LateUpdate()
        {
            ApplySafeArea();
            if (_label == null) return;
            float k = _rt.rect.height / ART_H;
            if (k > 0f && Mathf.Abs(k - _labelK) > 0.0001f) { _labelK = k; _label.fontSize = LABEL_CAP / 0.70f * k; }
        }

        public void Select(int tab, bool animate)
        {
            tab = Mathf.Clamp(tab, 0, TABS - 1);
            if (tab == Selected && animate) { Bounce(); return; }
            Selected = tab;
            _img.sprite = _states[tab];
            LayoutHits(tab);
            LayoutIcons(tab);
            if (animate)
            {
                GameAudio.Instance?.PlayUIClick();
                HapticsManager.ButtonTap();
                Bounce();
                if (_icons[tab] != null)
                {
                    var t = _icons[tab].transform; t.DOKill(); t.localScale = Vector3.one;
                    t.DOPunchScale(Vector3.one * 0.12f, 0.28f, 6, 0.6f).SetUpdate(true);   // the icon pops as its dome rises
                }
                OnTabSelected?.Invoke(tab);
            }
            WDLog.Log($"[NavBar] tab {tab} selected");
        }

        // Tab slots match the art: the selected tab is SEL_W wide, the other four share the rest (navbar_sel.py LAYOUT).
        private void LayoutHits(int sel)
        {
            float wo = (ART_W - SEL_W - SEAM * (TABS - 1)) / (TABS - 1), x = 0f;
            for (int i = 0; i < TABS; i++)
            {
                float w = i == sel ? SEL_W : wo;
                var r = _hits[i];
                r.anchorMin = new Vector2(x / ART_W, 0f);
                r.anchorMax = new Vector2((x + w) / ART_W, (ART_H - RISE) / ART_H);   // the bar itself (above an unselected tab is scene)
                r.offsetMin = r.offsetMax = Vector2.zero;
                x += w + SEAM;
            }
        }

        // A short squash-pop on the whole bar, anchored at its bottom, so the new dome reads as rising into place.
        private void Bounce()
        {
            transform.DOKill();
            transform.localScale = Vector3.one;
            transform.DOScale(new Vector3(1f, 1.04f, 1f), 0.08f).SetEase(Ease.OutQuad).SetUpdate(true)
                     .OnComplete(() => transform.DOScale(Vector3.one, 0.18f).SetEase(Ease.OutBack, 3f).SetUpdate(true));
        }
    }
}
