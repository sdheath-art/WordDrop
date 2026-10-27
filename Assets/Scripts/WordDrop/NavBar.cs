using DG.Tweening;
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
            Select(DefaultTab, false);
            return true;
        }

        public void Select(int tab, bool animate)
        {
            tab = Mathf.Clamp(tab, 0, TABS - 1);
            if (tab == Selected && animate) { Bounce(); return; }
            Selected = tab;
            _img.sprite = _states[tab];
            LayoutHits(tab);
            if (animate)
            {
                GameAudio.Instance?.PlayUIClick();
                HapticsManager.ButtonTap();
                Bounce();
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
