using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// One-time Starter Pack offer. Surfaced after the tutorial completes (see
    /// <see cref="TryAutoShow"/>) to pitch a coins/hearts bundle at the moment
    /// the player first experiences a hard level and a low wallet.
    ///
    /// IAP is stubbed for Phase 7 — BUY logs a TODO marker and grants the bundle
    /// directly so the flow is testable end-to-end in-editor. Phase 11 replaces
    /// the stub with a real IAP SDK callback.
    ///
    /// Canvas sortingOrder = 150, same tier as the other level modals. Dimensions
    /// come from <see cref="UILayout.ModalStarterPack"/>.
    /// </summary>
    public class StarterPackModal : MonoBehaviour
    {
        public static StarterPackModal Instance { get; private set; }

        // ── Bundle contents ─────────────────────────────────────────────────────
        public const int BUNDLE_COINS  = 100;
        public const string BUNDLE_PRICE_DISPLAY = "$2.99";

        /// <summary>Hearts granted with the bundle — tied to MAX_HEARTS so the
        /// display can't drift from the actual grant.</summary>
        public static int BundleHearts => HeartsManager.MAX_HEARTS;

        // ── Persistence ─────────────────────────────────────────────────────────
        private const string KEY_SEEN      = "wd_starter_pack_seen";
        private const string KEY_PURCHASED = "wd_starter_pack_purchased";

        private Canvas _canvas;
        private GameObject _card;

        private static readonly Color PANEL_BG  = new Color(0.06f, 0.05f, 0.12f, 0.90f);
        private static readonly Color CARD_BG   = new Color(0.14f, 0.10f, 0.30f, 1f);
        private static readonly Color ACCENT    = new Color(1.00f, 0.82f, 0.26f, 1f);
        private static readonly Color BODY_TEXT = new Color(0.92f, 0.88f, 0.80f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);
        }

        // ── Show/hide ───────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            SetVisible(visible, null);
        }

        public void SetVisible(bool visible, System.Action onHidden)
        {
            if (_canvas == null) { onHidden?.Invoke(); return; }
            if (visible)
            {
                _canvas.gameObject.SetActive(true);
                var raycaster = _canvas.GetComponent<GraphicRaycaster>();
                if (raycaster != null) raycaster.enabled = true;
                if (_card != null) UIAnimations.PopIn(_card.transform);
                return;
            }
            if (!_canvas.gameObject.activeSelf) { onHidden?.Invoke(); return; }
            var rc = _canvas.GetComponent<GraphicRaycaster>();
            if (rc != null) rc.enabled = false;
            if (_card == null)
            {
                _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
                return;
            }
            UIAnimations.PopOut(_card.transform, onComplete: () =>
            {
                if (_canvas != null) _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
            });
        }

        /// <summary>
        /// Idempotent entry-point for systems that want to offer the pack. Returns
        /// true if the modal actually shows. Skips when the player has already
        /// seen or purchased it, or when the tutorial isn't complete yet (we don't
        /// want to pitch until the player's played a bit).
        /// </summary>
        public bool TryAutoShow()
        {
            if (PlayerPrefs.GetInt(KEY_SEEN, 0) != 0) return false;
            if (PlayerPrefs.GetInt(KEY_PURCHASED, 0) != 0) return false;
            if (!TutorialProgression.IsTutorialComplete()) return false;

            PlayerPrefs.SetInt(KEY_SEEN, 1);
            PlayerPrefs.Save();
            SetVisible(true);
            return true;
        }

        /// <summary>Debug-only: clear persistence so the pack can surface again.</summary>
        public static void ResetState()
        {
            PlayerPrefs.DeleteKey(KEY_SEEN);
            PlayerPrefs.DeleteKey(KEY_PURCHASED);
            PlayerPrefs.Save();
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("StarterPackCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen dimmer — blocks board input while the offer is up.
            GameObject dim = new GameObject("Dim");
            dim.transform.SetParent(canvasGO.transform, false);
            RectTransform dRT = dim.AddComponent<RectTransform>();
            dRT.anchorMin = Vector2.zero;
            dRT.anchorMax = Vector2.one;
            dRT.offsetMin = Vector2.zero;
            dRT.offsetMax = Vector2.zero;
            Image dImg = dim.AddComponent<Image>();
            dImg.color = PANEL_BG;
            dImg.raycastTarget = true;

            // Card — UILayout.ModalStarterPack spec: 90% width, up to 80% height.
            var spec = UILayout.ModalStarterPack;
            float halfW = spec.WidthFraction * 0.5f;
            float halfH = spec.MaxHeightFraction * 0.5f;
            _card = new GameObject("Card");
            _card.transform.SetParent(canvasGO.transform, false);
            RectTransform cRT = _card.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0.5f - halfW, 0.5f - halfH);
            cRT.anchorMax = new Vector2(0.5f + halfW, 0.5f + halfH);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            Image cImg = _card.AddComponent<Image>();
            cImg.color = CARD_BG;
            GameObject card = _card;
            // TODO(Phase 11): swap for a sprite with the spec's CornerRadius baked in,
            // or use a RoundedCornerImage shader — UGUI Image doesn't do radii natively.

            // Banner ribbon
            GameObject ribbon = new GameObject("Ribbon");
            ribbon.transform.SetParent(card.transform, false);
            RectTransform rRT = ribbon.AddComponent<RectTransform>();
            rRT.anchorMin = new Vector2(0f, 0.88f);
            rRT.anchorMax = new Vector2(1f, 1.00f);
            rRT.offsetMin = Vector2.zero;
            rRT.offsetMax = Vector2.zero;
            Image rImg = ribbon.AddComponent<Image>();
            rImg.color = ACCENT;

            var ribbonLabel = CreateLabel(ribbon.transform, "RibbonLabel",
                new Vector2(0f, 0f), new Vector2(1f, 1f),
                "ONE-TIME STARTER PACK", 22, Color.black);
            ribbonLabel.fontStyle = FontStyle.Bold;

            // Title
            var title = CreateLabel(card.transform, "Title",
                new Vector2(0.05f, 0.72f), new Vector2(0.95f, 0.86f),
                "BEST VALUE", 42, ACCENT);
            title.fontStyle = FontStyle.Bold;

            // Bundle contents list
            CreateLabel(card.transform, "Line1",
                new Vector2(0.08f, 0.58f), new Vector2(0.92f, 0.70f),
                $"●  {BUNDLE_COINS} coins", 28, BODY_TEXT);
            CreateLabel(card.transform, "Line2",
                new Vector2(0.08f, 0.46f), new Vector2(0.92f, 0.58f),
                $"♥  Full {BundleHearts} hearts", 28, BODY_TEXT);
            CreateLabel(card.transform, "Line3",
                new Vector2(0.08f, 0.34f), new Vector2(0.92f, 0.46f),
                "✨  No ads for today", 28, BODY_TEXT);

            // Savings callout
            var savings = CreateLabel(card.transform, "Savings",
                new Vector2(0.08f, 0.26f), new Vector2(0.92f, 0.32f),
                "Save 50% vs buying separately", 16, new Color(0.75f, 0.95f, 0.55f, 1f));
            savings.fontStyle = FontStyle.Italic;

            // Buttons
            float btnY0 = 0.08f;
            float btnY1 = 0.22f;
            MenuUI.CreateButton(card.transform, "BtnNoThanks",
                new Vector2(0.08f, btnY0), new Vector2(0.42f, btnY1),
                "NO THANKS", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 20,
                OnNoThanksClicked);
            MenuUI.CreateButton(card.transform, "BtnBuy",
                new Vector2(0.46f, btnY0), new Vector2(0.92f, btnY1),
                $"BUY  {BUNDLE_PRICE_DISPLAY}", ACCENT, Color.black, 22,
                OnBuyClicked);
        }

        // ── Buttons ─────────────────────────────────────────────────────────────

        private void OnBuyClicked()
        {
            // TODO(Phase 11): replace with real IAP flow. Expected shape:
            //   IAPService.Purchase("starter_pack_01", result => {
            //       if (result.Success) GrantBundle();
            //       else ShowError(result.Error);
            //   });
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Debug.Log("[StarterPackModal] BUY clicked — IAP stubbed (dev build). Granting bundle locally.");
            GrantBundle();
            PlayerPrefs.SetInt(KEY_PURCHASED, 1);
            PlayerPrefs.Save();
            SetVisible(false, null);
#else
            // Production build: never grant paid content without a real receipt.
            // Until IAP ships, the button is a visual pitch only — surface that
            // clearly instead of silently dropping the tap.
            Debug.LogWarning("[StarterPackModal] IAP not wired — BUY tapped in a non-dev build. No-op.");
#endif
        }

        private void OnNoThanksClicked()
        {
            // KEY_SEEN was already set by TryAutoShow so this auto-dismisses forever.
            SetVisible(false, null);
        }

        private void GrantBundle()
        {
            CoinWallet.Add(BUNDLE_COINS);
            HeartsManager.GrantFull();
            // Ad-free flag would go here; left as a stub to keep the Phase 7 surface small.
            AnalyticsManager.Log("starter_pack_purchased",
                "coins", BUNDLE_COINS,
                "hearts", BundleHearts,
                "price_display", BUNDLE_PRICE_DISPLAY);
        }

        // ── UI plumbing ─────────────────────────────────────────────────────────

        private static Text CreateLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Text t = go.AddComponent<Text>();
            t.font = MenuUI.GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }
}
