using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// MVP P3 continue offer. Shown after a Survival top-out (and after the
    /// TopOutPanel announcement plays). Two actions:
    ///   - CONTINUE: spend CONTINUE_COIN_COST coins → clear top 3 rows + refill
    ///     resources → resume play (no heart consumed)
    ///   - GIVE UP: heart consumed → finalize game over
    ///
    /// Canvas sortingOrder = 170 (above TopOutPanel @ 165, below GameOverUI @ 10000).
    /// </summary>
    public class ContinueModal : MonoBehaviour
    {
        public static ContinueModal Instance { get; private set; }

        /// <summary>True while the modal canvas is currently active on screen.
        /// HintManager + other ambient animation systems gate on this to stop
        /// background tile-hops/pulses while the modal is up — matches
        /// StageClearModal.IsShowing.</summary>
        public bool IsShowing => _canvas != null && _canvas.gameObject.activeSelf;

        private const int CANVAS_SORT_ORDER = 170;

        private Canvas _canvas;
        private GameObject _panel;
        private CanvasGroup _panelCanvasGroup; // alpha gate so the panel can be hidden across the SetActive→DropInWithBounce gap
        private Image _backdrop;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _bodyText;
        private GameObject  _objectiveIconHolder; // holds the remaining-objective ICON below the body text
        private CanvasGroup _objectiveIconCG;
        private GameObject  _objectiveIconGO;      // the built icon (rebuilt each show; destroyed first)
        private TextMeshProUGUI _continueLabel;
        // ── COIN IN THE LABEL (2026-10-22 Spencer: "add the coin icon where the bullet is on the play on button"). The label
        // leaves COIN_GAP where the ● was; a real coin Image (the HUD's coin3d_icon) is parked on the MIDDLE space of that gap,
        // measured from TMP's own layout, so it tracks the text whatever the cost or the auto-size. ──
        private const string COIN_GAP = "     ";
        private Image _coinInLabel; private string _coinPlacedFor; private float _coinPlacedSize = -1f;
        private static Sprite s_labelCoin; private static bool s_labelCoinTried;
        private static Sprite LabelCoinSprite()
        {
            if (s_labelCoinTried) return s_labelCoin;
            s_labelCoinTried = true;
            foreach (string path in new[] { "Tiles/coin3d_icon", "Tiles/Icon_ImageIcon_Coin" })
            {
                s_labelCoin = Resources.Load<Sprite>(path);
                if (s_labelCoin != null) break;
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null) { s_labelCoin = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f); break; }
            }
            return s_labelCoin;
        }
        private void LateUpdate()
        {
            var l = _continueLabel;
            if (l == null || !l.gameObject.activeInHierarchy) return;
            if (_coinPlacedFor == l.text && Mathf.Approximately(_coinPlacedSize, l.fontSize)) return;
            int gap = l.text.IndexOf(COIN_GAP, System.StringComparison.Ordinal);
            if (_coinInLabel == null)
            {
                var sp = LabelCoinSprite(); if (sp == null) return;
                var go = new GameObject("CoinInLabel", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(l.transform, false);
                _coinInLabel = go.GetComponent<Image>(); _coinInLabel.sprite = sp; _coinInLabel.preserveAspect = true; _coinInLabel.raycastTarget = false;
                var crt = (RectTransform)go.transform; crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            }
            l.ForceMeshUpdate();
            _coinPlacedFor = l.text; _coinPlacedSize = l.fontSize;
            int mid = gap + COIN_GAP.Length / 2;
            if (gap < 0 || mid >= l.textInfo.characterCount) { _coinInLabel.gameObject.SetActive(false); return; }
            var ci = l.textInfo.characterInfo[mid];
            var line = l.textInfo.lineInfo[ci.lineNumber];
            float x = (ci.origin + ci.xAdvance) * 0.5f;
            float y = (line.ascender + line.descender) * 0.5f;
            float h = (line.ascender - line.descender) * 0.85f;
            var rt = _coinInLabel.rectTransform;
            rt.localPosition = new Vector3(x, y, 0f);
            rt.sizeDelta = new Vector2(h, h);
            _coinInLabel.gameObject.SetActive(true);
        }
        private TextMeshProUGUI _adLabel;
        private Button _continueButton;
        private GameObject _continueButtonGO;
        private CanvasGroup _continueButtonCG;
        private Button _adButton;
        private GameObject _adButtonGO;
        private CanvasGroup _adButtonCG;
        private GameObject _giveUpButtonGO;
        private CanvasGroup _giveUpButtonCG;
        private GameObject _coinDisplay;
        private CanvasGroup _coinDisplayCG;
        private TextMeshProUGUI _coinAmountText;
        private SurvivalManager _survival;
        private int _currentCost;
        private bool _adInFlight;
        private bool _isPresenting;
        private bool _isDismissing;
        private Sequence _entranceSequence;
        private float _titleZRot; // animated z-rotation for the toss-in
        private Vector2 _panelRestAnchoredPos; // cached at build so AnimateEntrance can restore after ExitUp's SetRelative drift

        // Animation tuning — mirrors StageClearModal but snappier per Spencer's
        // 2026-06-01 ask ("should appear a lot faster"). The end-game continue
        // moment is high-anxiety; the player wants to act, not watch animations.
        private const float DROP_SPEED   = 2.5f;        // panel drop multiplier (StageClearModal uses 1.5)
        private const float BACKDROP_FADE_DUR = 0.10f;  // was 0.18
        private const float SETTLE_BEAT       = 0.05f;  // post-drop hold before title pops (was 0.25)
        private const float STAGGER           = 0.04f;  // child reveal stagger (was 0.06-0.10)
        private const float CHILD_FADE_DUR    = 0.12f;  // each child fade-in (was 0.18)
        private static readonly Color BACKDROP_DIM = new Color(0f, 0f, 0f, 0.55f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("ContinueModalCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CANVAS_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // 2026-06-01: switched reference resolution to the PSD canvas
            // (1179×2556 — iPhone 16/15 Pro) so the panel can use PSD coords
            // directly without translation math.
            scaler.referenceResolution = new Vector2(1179f, 2556f);
            scaler.matchWidthOrHeight = 0.5f;

            // Backdrop dim (full screen, tap-blocking). Stored as field so
            // Show/Hide can fade it independently of the panel drop animation.
            var dimGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            dimGO.transform.SetParent(canvasGO.transform, false);
            var dimRT = dimGO.GetComponent<RectTransform>();
            dimRT.anchorMin = Vector2.zero;
            dimRT.anchorMax = Vector2.one;
            dimRT.offsetMin = Vector2.zero;
            dimRT.offsetMax = Vector2.zero;
            _backdrop = dimGO.GetComponent<Image>();
            _backdrop.color = BACKDROP_DIM;

            // Panel card — sized + positioned per Spencer's PSD spec
            // (W=1180, H=1230 at PSD X=0, Y=500). PSD origin is top-left; Unity
            // anchors are bottom-left. Conversion:
            //   anchorMin.y = (2556 - 500 - 1230) / 2556 ≈ 0.323
            //   anchorMax.y = (2556 - 500)        / 2556 ≈ 0.805
            // Width spans the full canvas. Pivot stays centered so the
            // DropInWithBounce tween animates around the rect's centre.
            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO;
            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0f,    0.323f);
            panelRT.anchorMax = new Vector2(1f,    0.805f);
            panelRT.pivot     = new Vector2(0.5f,  0.5f);
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;
            panelGO.GetComponent<Image>().color = new Color(0.99f, 0.95f, 0.86f, 0.98f); // TEMP candy-unification
            // CanvasGroup lets us hide the panel across the SetActive→
            // DropInWithBounce gap. Without this, the panel renders briefly at
            // rest position before DropInWithBounce snaps it offscreen, which
            // reads as a "double pop in" (Spencer 2026-06-01).
            _panelCanvasGroup = panelGO.AddComponent<CanvasGroup>();

            // Cache the rest anchoredPosition NOW so we can restore it on
            // every Show. ExitUp uses SetRelative(true) which leaves the
            // panel high above the canvas after a hide — if we don't reset,
            // DropInWithBounce reads the drifted position as the new rest
            // and the panel ratchets further off-screen each cycle.
            _panelRestAnchoredPos = panelRT.anchoredPosition;

            // Title
            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(panelRT, false);
            _titleText = titleGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _titleText.font = displayFont;
            _titleText.text = "CONTINUE?";
            _titleText.fontSize = 150; // Spencer 2026-06-01 — bigger title for the CONTINUE? hero moment
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.82f, 0.28f, 0.46f, 1f);
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.75f);  // 2026-06-24: nudged down so the big title fits inside the panel
            titleRT.anchorMax = new Vector2(0.95f, 0.92f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            // Body — describes what continue does
            var bodyGO = new GameObject("Body", typeof(RectTransform));
            bodyGO.transform.SetParent(panelRT, false);
            _bodyText = bodyGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) _bodyText.font = displayFont;
            _bodyText.text = "Clear the top rows\nand refill resources";
            _bodyText.fontSize = 28;
            _bodyText.alignment = TextAlignmentOptions.Center;
            _bodyText.color = new Color(0.32f, 0.24f, 0.30f, 1f);
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = new Vector2(0.05f, 0.70f);   // raised to make room for the bigger objective icon below it
            bodyRT.anchorMax = new Vector2(0.95f, 0.745f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;
            _bodyText.alignment = TextAlignmentOptions.Bottom;

            // Remaining-objective ICON holder — point-anchored just below the "So close" line.
            _panelRTForHeart = panelRT;
            var iconHolderGO = new GameObject("ObjectiveIconHolder", typeof(RectTransform));
            iconHolderGO.transform.SetParent(panelRT, false);
            _objectiveIconHolder = iconHolderGO;
            _objectiveIconCG = iconHolderGO.AddComponent<CanvasGroup>();
            var iconHolderRT = iconHolderGO.GetComponent<RectTransform>();
            iconHolderRT.anchorMin = iconHolderRT.anchorMax = new Vector2(0.5f, 0.625f);
            iconHolderRT.pivot     = new Vector2(0.5f, 0.5f);
            iconHolderRT.sizeDelta = new Vector2(10f, 10f);

            // Continue (PLAY ON) button (paid path) — primary CTA. Cost is
            // dynamic per SurvivalManager.CurrentContinueCost (50→100 ladder).
            // 2026-06-01: label "CONTINUE" → "PLAY ON" per Spencer; coin glyph
            // preserved. Idle pulse + press-squash + split multi-pop SFX added
            // to mirror StageClearModal's continue button feel.
            var continueBtnGO = MakeButton(panelRT, "ContinueBtn",
                new Vector2(0.10f, 0.42f), new Vector2(0.90f, 0.56f),
                "PLAY ON" + COIN_GAP + "50",
                new Color(0.96f, 0.63f, 0.16f, 1f), // 2026-06-24: warm orange CTA (was green)
                OnContinueClicked);
            PinButtonSize(continueBtnGO, 0.45f);  // 2026-06-24: nudged down to open room above for the objective icon
            _continueButtonGO = continueBtnGO;
            _continueButton = continueBtnGO.GetComponent<Button>();
            _continueLabel = continueBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            _continueButtonCG = continueBtnGO.AddComponent<CanvasGroup>();

            // Split multi-pop SFX: PointerDown = press half, onClick = release
            // half. Same pattern wired on StageClearModal's CONTINUE button.
            var pressTrigger = continueBtnGO.GetComponent<UnityEngine.EventSystems.EventTrigger>();
            if (pressTrigger == null)
                pressTrigger = continueBtnGO.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var pdEntry = new UnityEngine.EventSystems.EventTrigger.Entry
            {
                eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown,
            };
            pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            pressTrigger.triggers.Add(pdEntry);

            // Watch Ad button — Codex rule: visually secondary but clearly available.
            // Muted blue. Same rescue path as paid continue.
            var adBtnGO = MakeButton(panelRT, "AdBtn",
                new Vector2(0.15f, 0.26f), new Vector2(0.85f, 0.39f),
                "WATCH AD",
                new Color(0.25f, 0.42f, 0.62f, 1f),
                OnAdClicked);
            PinButtonSize(adBtnGO, 0.285f);  // 2026-06-24: down with PLAY ON, keeps the gap
            _adButtonGO = adBtnGO;
            _adButton = adBtnGO.GetComponent<Button>();
            _adLabel = adBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            _adButtonCG = adBtnGO.AddComponent<CanvasGroup>();
            MenuUI.AddButtonIcon(adBtnGO, "UI/icon_clapper");   // 2026-10-22 Spencer's clapper
            // 2026-06-01: smaller-text override removed — Spencer wants all
            // three buttons at the 86px hero size set by MakeButton.

            // Same press-half / release-half SFX pattern as the PLAY ON button.
            var adPressTrigger = adBtnGO.GetComponent<UnityEngine.EventSystems.EventTrigger>();
            if (adPressTrigger == null)
                adPressTrigger = adBtnGO.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var adPdEntry = new UnityEngine.EventSystems.EventTrigger.Entry
            {
                eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown,
            };
            adPdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            adPressTrigger.triggers.Add(adPdEntry);

            // 2026-06-01: GIVE UP button replaced with a top-right Cancel X
            // (mirrors SettingsModal.BuildCloseButton). Tapping the X is the
            // give-up action — calls OnGiveUpClicked which forwards to
            // SurvivalManager.DeclineContinue. Same multi-pop press/release
            // SFX as the Settings X.
            BuildCancelX(panelRT);

            // Coin total + buy-more "+" button (Candy-Crush-style pill in the
            // top-left corner). Tapping "+" is a placeholder for the future
            // IAP / coin store flow.
            BuildCoinDisplay(panelRT);
        }

        private void BuildCoinDisplay(RectTransform panelRT)
        {
            // Pill container — top-left of panel
            var containerGO = new GameObject("CoinDisplay",
                typeof(RectTransform), typeof(Image));
            containerGO.transform.SetParent(panelRT, false);
            var rt = containerGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 0.5f); // anchored from left edge
            rt.sizeDelta = new Vector2(320f, 110f);
            rt.anchoredPosition = new Vector2(40f, -68f); // 2026-06-24: lowered so the whole pill sits inside the panel (was clipping the top)
            containerGO.GetComponent<Image>().color = new Color(0.96f, 0.89f, 0.79f, 0.92f);
            containerGO.GetComponent<Image>().raycastTarget = false;

            // Coin amount label ("● 1234"). Yellow, matches HUD coin counter.
            var labelGO = new GameObject("Amount", typeof(RectTransform));
            labelGO.transform.SetParent(containerGO.transform, false);
            _coinAmountText = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _coinAmountText.font = displayFont;
            _coinAmountText.text = "● 0";
            _coinAmountText.fontSize = 56;
            _coinAmountText.alignment = TextAlignmentOptions.MidlineRight;
            _coinAmountText.color = new Color(0.80f, 0.52f, 0.10f, 1f);
            _coinAmountText.raycastTarget = false;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = new Vector2(95f, 0f); // leave room for + button on the left
            labelRT.offsetMax = new Vector2(-20f, 0f);

            // "+" button — green circle on the left
            var plusGO = new GameObject("Plus",
                typeof(RectTransform), typeof(Image), typeof(Button));
            plusGO.transform.SetParent(containerGO.transform, false);
            var plusRT = plusGO.GetComponent<RectTransform>();
            plusRT.anchorMin = new Vector2(0f, 0.5f);
            plusRT.anchorMax = new Vector2(0f, 0.5f);
            plusRT.pivot     = new Vector2(0.5f, 0.5f);
            plusRT.sizeDelta = new Vector2(80f, 80f);
            plusRT.anchoredPosition = new Vector2(50f, 0f);
            plusGO.GetComponent<Image>().color = new Color(0.18f, 0.65f, 0.38f, 1f); // CTA green

            var plusGlyphGO = new GameObject("Glyph", typeof(RectTransform));
            plusGlyphGO.transform.SetParent(plusGO.transform, false);
            var plusTMP = plusGlyphGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) plusTMP.font = displayFont;
            plusTMP.text = "+";
            plusTMP.fontSize = 64;
            plusTMP.alignment = TextAlignmentOptions.Center;
            plusTMP.color = Color.white;
            plusTMP.raycastTarget = false;
            var plusGlyphRT = plusGlyphGO.GetComponent<RectTransform>();
            plusGlyphRT.anchorMin = Vector2.zero;
            plusGlyphRT.anchorMax = Vector2.one;
            plusGlyphRT.offsetMin = Vector2.zero;
            plusGlyphRT.offsetMax = Vector2.zero;

            plusGO.GetComponent<Button>().onClick.AddListener(OnBuyCoinsTapped);

            _coinDisplay = containerGO;
            _coinDisplayCG = containerGO.AddComponent<CanvasGroup>();
        }

        private void OnBuyCoinsTapped()
        {
            // Placeholder — wire to IAP / coin store flow when that lands.
            GameAudio.Instance?.PlayUIClick();
            WDLog.Log("[ContinueModal] Buy coins tapped — TODO: open coin store / IAP flow");
        }

        private void BuildCancelX(RectTransform panelRT)
        {
            var btnGO = new GameObject("CancelX",
                typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(panelRT, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(110f, 110f);
            rt.anchoredPosition = new Vector2(-40f, -40f);

            var img = btnGO.GetComponent<Image>();
            img.color = new Color(0.55f, 0.18f, 0.18f, 1f); // muted red, matches SettingsModal close

            var glyphGO = new GameObject("X", typeof(RectTransform));
            glyphGO.transform.SetParent(btnGO.transform, false);
            var glyphTMP = glyphGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) glyphTMP.font = displayFont;
            glyphTMP.text = "✕";
            glyphTMP.fontSize = 64;
            glyphTMP.alignment = TextAlignmentOptions.Center;
            glyphTMP.color = Color.white;
            glyphTMP.raycastTarget = false;
            var glyphRT = glyphGO.GetComponent<RectTransform>();
            glyphRT.anchorMin = Vector2.zero;
            glyphRT.anchorMax = Vector2.one;
            glyphRT.offsetMin = Vector2.zero;
            glyphRT.offsetMax = Vector2.zero;

            // Split multi-pop SFX, same as SettingsModal's close X.
            var trigger = btnGO.GetComponent<EventTrigger>();
            if (trigger == null) trigger = btnGO.AddComponent<EventTrigger>();
            var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            trigger.triggers.Add(pdEntry);

            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                // Same path as the old GIVE UP button — finalizes the game-over.
                OnGiveUpClicked();
            });

            // Re-use the give-up-button CanvasGroup slot for the staggered
            // fade-in. AnimateEntrance sets this to alpha=0 pre-show and
            // fades it back in alongside the other content.
            _giveUpButtonGO = btnGO;
            _giveUpButtonCG = btnGO.AddComponent<CanvasGroup>();
        }

        /// <summary>Pin a button to a fixed 550×120 PSD-pixel rect, centered
        /// horizontally on the panel at the given vertical anchor. Overrides
        /// whatever stretched anchorMin/anchorMax MakeButton applied.</summary>
        private static void PinButtonSize(GameObject btnGO, float verticalAnchorY)
        {
            if (btnGO == null) return;
            var rt = btnGO.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin        = new Vector2(0.5f, verticalAnchorY);
            rt.anchorMax        = new Vector2(0.5f, verticalAnchorY);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(550f, 120f);
            rt.anchoredPosition = Vector2.zero;
        }

        private GameObject MakeButton(RectTransform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string label, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var btnGO = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(parent, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var btnImg = btnGO.GetComponent<Image>();
            btnImg.color  = bgColor;
            btnImg.sprite = MenuUI.GetRoundedRectSprite(28); // 2026-06-24: rounded corners (match the rest of the buttons)
            btnImg.type   = Image.Type.Sliced;
            btnGO.GetComponent<Button>().onClick.AddListener(onClick);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(btnGO.transform, false);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) labelText.font = displayFont;
            labelText.text = label;
            labelText.fontSize = 72; // Spencer 2026-06-01 — bumped from 36; 72 ≈ 60% of the 120px button height for hero scale without clipping
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = Color.white;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            return btnGO;
        }

        public void Show(SurvivalManager survival)
        {
            _survival = survival;
            _adInFlight = false;

            // Dynamic cost from SurvivalManager (50 → 100 ladder).
            _currentCost = survival != null ? survival.CurrentContinueCost : 50;
            bool canAfford = CoinWallet.Balance >= _currentCost;

            // Refresh coin total in the top-left pill.
            if (_coinAmountText != null)
                _coinAmountText.text = $"● {CoinWallet.Balance}";

            // "So close!" — surface the remaining objective so the continue feels like one more push
            // at a goal in sight, not a generic "clear the top rows". 2026-06-15 Spencer.
            {
                var obj = ObjectiveManager.Instance != null ? ObjectiveManager.Instance.Active : null;

                // Rebuild the remaining-objective icon (chicken / vault / hidden-row / etc.) fresh each show.
                if (_objectiveIconGO != null) { Destroy(_objectiveIconGO); _objectiveIconGO = null; }

                bool showIcon = obj != null && !obj.IsComplete && obj.Icon != Objective.HudIcon.None;
                if (SurvivalManager.MoveLimitMode) showIcon = false;   // move-limit runs: the heart break plays here instead
                if (showIcon && _objectiveIconHolder != null)
                {
                    _objectiveIconGO = ObjectiveIconBuilder.Build(
                        obj.Icon, _objectiveIconHolder.transform, 76f, obj.RemainingCount, obj.IconWord);
                    if (_bodyText != null) _bodyText.text = "So close! You only have";
                }
                else if (_bodyText != null)
                {
                    _bodyText.text = (obj != null && !obj.IsComplete)
                        ? $"So close! You only have\n{obj.RemainingText}"
                        : "Clear the top rows\nand refill resources";
                }
                if (_objectiveIconHolder != null) _objectiveIconHolder.SetActive(showIcon);
                if (SurvivalManager.MoveLimitMode)
                {
                    var sm = SurvivalManager.Instance;
                    int shortBy = sm != null ? Mathf.Max(0, sm.CurrentStageTarget - sm.CurrentStageScore) : 0;
                    if (_bodyText != null)
                        _bodyText.text = shortBy > 0 ? $"So close! Only {shortBy:N0} points to go" : "Out of moves!";
                    PlayHeartBreak();
                }
            }

            if (_continueLabel != null)
                _continueLabel.text = canAfford
                    ? $"PLAY ON{COIN_GAP}{_currentCost}"
                    : $"NEED{COIN_GAP}{_currentCost - CoinWallet.Balance} MORE";
            if (_continueButton != null)
                _continueButton.interactable = canAfford;

            // Ad button always available (no cooldown per Codex). Disabled only
            // if AdManager is missing or an ad request is in-flight.
            bool adAvailable = AdManager.Instance != null;
            if (_adButton != null) _adButton.interactable = adAvailable;
            if (_adLabel != null) _adLabel.text = adAvailable ? "WATCH AD" : "AD UNAVAILABLE";

            try
            {
                AnalyticsManager.Log("continue_offered",
                    "stage", survival != null ? survival.CurrentStageIndex : -1,
                    "continue_number", survival != null ? survival.ContinuesUsedThisRun + 1 : -1,
                    "cost", _currentCost,
                    "coin_balance", CoinWallet.Balance,
                    "can_afford", canAfford ? 1 : 0,
                    "ad_available", adAvailable ? 1 : 0);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            AnimateEntrance();
        }

        /// <summary>Choreographed entrance — backdrop fades in first, then the
        /// panel drops from above with a damped bounce settle. Mirrors
        /// StageClearModal so both modals share the same "feel" identity.</summary>
        private void AnimateEntrance()
        {
            if (_canvas == null) return;
            _isPresenting = true;
            _isDismissing = false;

            _entranceSequence?.Kill();

            // Reset state: backdrop transparent, panel hidden (alpha 0). Panel
            // alpha is restored to 1 INSIDE the Phase 2 callback AFTER
            // DropInWithBounce has already snapped the panel offscreen — so it
            // never renders at rest position before the drop animation starts.
            if (_backdrop != null)
                _backdrop.color = new Color(BACKDROP_DIM.r, BACKDROP_DIM.g, BACKDROP_DIM.b, 0f);
            if (_panelCanvasGroup != null) _panelCanvasGroup.alpha = 0f;
            if (_panel != null)
            {
                _panel.transform.localScale = Vector3.one;
                // Restore the panel to its original rest anchoredPosition.
                // ExitUp left it ~1200 units higher; without this reset
                // DropInWithBounce treats THAT as the new rest and the panel
                // drifts further off-screen every Show.
                var prt = _panel.transform as RectTransform;
                if (prt != null) prt.anchoredPosition = _panelRestAnchoredPos;
            }

            // Hide ALL panel content pre-show so the children fade in
            // individually AFTER the panel lands (StageClearModal pattern).
            // Without this they ride the drop animation visible, defeating
            // the purpose of the staggered reveal.
            SetTextAlpha(_titleText, 0f);
            SetTextAlpha(_bodyText,  0f);
            if (_objectiveIconCG != null) _objectiveIconCG.alpha = 0f;
            if (_continueButtonCG != null) _continueButtonCG.alpha = 0f;
            if (_adButtonCG       != null) _adButtonCG.alpha       = 0f;
            if (_giveUpButtonCG   != null) _giveUpButtonCG.alpha   = 0f;
            if (_coinDisplayCG    != null) _coinDisplayCG.alpha    = 0f;

            _canvas.gameObject.SetActive(true);
            // Continue-modal entry sting per Spencer 2026-06-01. Distinct
            // from PlayUIClick (which read as "tap registered" rather than
            // "moment of arrival") and PlayEntry (reserved for StageClear).
            GameAudio.Instance?.PlayVictory1();

            // Lock the background the same way StageClearModal does so the
            // hint-manager tile-hops, rising-row timers, auto-drops, and any
            // other ambient gameplay animation freezes for the duration of
            // the modal. HintManager.GameplayActive() reads IsShowing (above)
            // and ResetIdleTimer-clears any in-flight hint sequences.
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(true);
            HintManager.Instance?.ClearVisuals();

            Sequence seq = DOTween.Sequence();

            // Phase 1 — backdrop fade-in (matches StageClearModal Phase 1).
            if (_backdrop != null)
                seq.Append(_backdrop.DOFade(BACKDROP_DIM.a, BACKDROP_FADE_DUR).SetEase(Ease.OutQuad));
            else
                seq.AppendInterval(BACKDROP_FADE_DUR);

            // Phase 2 — panel drops from above with damped bounce (StageClearModal Phase 2).
            if (_panel != null)
            {
                seq.AppendCallback(() =>
                {
                    if (_panel == null) return;
                    var rt = _panel.transform as RectTransform;
                    if (rt != null)
                    {
                        // ORDER MATTERS: DropInWithBounce snaps the panel
                        // offscreen first, THEN we make it visible. If we
                        // showed it before DropInWithBounce, the player would
                        // see it pop in at rest position before the drop.
                        UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED);
                        if (_panelCanvasGroup != null) _panelCanvasGroup.alpha = 1f;

                        // Hide the title immediately on reveal so TossInTitle
                        // (Phase 3, after the panel lands) is what actually
                        // brings it onto screen. Otherwise the title rides
                        // the drop visible, which makes the toss-in feel like
                        // a redundant second reveal.
                        if (_titleText != null)
                        {
                            Color c = _titleText.color;
                            _titleText.color = new Color(c.r, c.g, c.b, 0f);
                        }
                    }
                });
                // Short settle beat so the panel reads as "landed" before
                // the children pop in. Kept tiny — Spencer wants the modal
                // available fast.
                seq.AppendInterval(UIAnimations.DROP_TOTAL_DUR / DROP_SPEED + SETTLE_BEAT);
            }

            // Phase 3 — snappy staggered reveal of panel children. Reveals
            // come quick and close together but still cascade (not all at
            // once) so the panel reads as "alive" rather than a screenshot.
            seq.AppendCallback(() => TossInTitle());
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInText(_bodyText, CHILD_FADE_DUR);
                if (_objectiveIconCG != null) _objectiveIconCG.DOFade(1f, CHILD_FADE_DUR);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInCanvasGroup(_continueButtonCG, CHILD_FADE_DUR);
                StartButtonPulse(_continueButtonGO);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInCanvasGroup(_adButtonCG, CHILD_FADE_DUR);
                StartButtonPulse(_adButtonGO);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() => FadeInCanvasGroup(_giveUpButtonCG, CHILD_FADE_DUR));
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() => FadeInCanvasGroup(_coinDisplayCG, CHILD_FADE_DUR));

            _entranceSequence = seq;
        }

        private static void SetTextAlpha(TextMeshProUGUI t, float a)
        {
            if (t == null) return;
            Color c = t.color;
            t.color = new Color(c.r, c.g, c.b, a);
        }

        private static void FadeInText(TextMeshProUGUI t, float duration)
        {
            if (t == null) return;
            Color c = t.color;
            t.DOColor(new Color(c.r, c.g, c.b, 1f), duration).SetEase(Ease.OutQuad);
        }

        private static void FadeInCanvasGroup(CanvasGroup cg, float duration)
        {
            if (cg == null) return;
            cg.DOFade(1f, duration).SetEase(Ease.OutQuad);
        }

        /// <summary>Title scale + rotation + alpha toss-in. Mirrors
        /// StageClearModal.TossInTitle but adapted to TextMeshProUGUI
        /// (uses TMP's color tween for alpha — same DOColor API as UI Text).</summary>
        private void TossInTitle()
        {
            if (_titleText == null) return;
            Transform t = _titleText.transform;
            t.DOKill();

            // Pre-toss state: tiny, slight tilt so the rotation has somewhere
            // to swing from.
            t.localScale    = Vector3.one * 0.1f;
            t.localRotation = Quaternion.Euler(0f, 0f, 8f);
            Color c0 = _titleText.color;
            _titleText.color = new Color(c0.r, c0.g, c0.b, 0f);

            const float TOSS_DURATION = 0.28f;
            const float OVERSHOOT     = 3.0f;

            t.DOScale(1.0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            DG.Tweening.Core.DOGetter<float> getZ = () => _titleZRot;
            DG.Tweening.Core.DOSetter<float> setZ = (float z) =>
            {
                _titleZRot = z;
                t.localRotation = Quaternion.Euler(0f, 0f, z);
            };
            _titleZRot = 8f;
            DOTween.To(getZ, setZ, 0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            Color c = _titleText.color;
            _titleText.DOColor(new Color(c.r, c.g, c.b, 1f), 0.12f).SetEase(Ease.OutQuad);
        }

        private static void StartButtonPulse(GameObject btnGO)
        {
            if (btnGO == null) return;
            Transform t = btnGO.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            t.DOScale(1.07f, 0.7f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private static void StopButtonPulse(GameObject btnGO)
        {
            if (btnGO == null) return;
            btnGO.transform.DOKill();
            btnGO.transform.localScale = Vector3.one;
        }

        private static void PlayButtonPressSquash(GameObject btnGO)
        {
            if (btnGO == null) return;
            Transform t = btnGO.transform;
            t.DOKill();
            // Press-squash → pop → settle. Same numbers as
            // StageClearModal.PlayContinuePressSquash for a unified feel.
            Sequence press = DOTween.Sequence();
            press.Append(t.DOScale(0.92f, 0.06f).SetEase(Ease.OutQuad));
            press.Append(t.DOScale(1.06f, 0.10f).SetEase(Ease.OutBack, 4f));
            press.Append(t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad));
        }

        private void Hide()
        {
            if (_isDismissing) return;
            _isDismissing = true;

            _entranceSequence?.Kill();
            _entranceSequence = null;

            // Kill button pulses so the scale tween doesn't fight ExitUp.
            StopButtonPulse(_continueButtonGO);
            StopButtonPulse(_adButtonGO);

            if (_panel != null)
            {
                _panel.transform.DOKill();
                var rt = _panel.transform as RectTransform;
                if (rt != null)
                {
                    UIAnimations.ExitUp(rt, FinalizeHide, speedMult: 1.5f);
                }
                else
                {
                    FinalizeHide();
                }
            }
            else
            {
                FinalizeHide();
            }

            // Fade the backdrop out in parallel with the panel's exit so the
            // dim doesn't linger after the card has flown off-screen.
            if (_backdrop != null)
            {
                _backdrop.DOKill();
                _backdrop.DOFade(0f, BACKDROP_FADE_DUR).SetEase(Ease.InQuad);
            }
        }

        private void FinalizeHide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _isPresenting = false;
            _isDismissing = false;

            // Resume background gameplay timers — rising row, auto-drop,
            // hint loop, etc. Paired with the SetOverlayPaused(true) in
            // AnimateEntrance.
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(false);
        }

        private void OnContinueClicked()
        {
            if (_survival == null || _adInFlight) { Hide(); return; }
            if (!CoinWallet.Spend(_currentCost))
            {
                Debug.LogWarning($"[ContinueModal] Continue tapped without sufficient coins for cost={_currentCost} — should have been gated.");
                return;
            }

            // Release half of the split multi-pop SFX (press half fired on
            // PointerDown). Press-squash dropped on the dismiss path —
            // ExitUp's transform tween starts immediately and the press
            // squash would fight it. Same compromise StageClearModal made.
            GameAudio.Instance?.PlayMultiPopRelease();
            Hide();
            _survival.ApplyContinueRescue();
            _survival.ResumeFromContinue();
        }

        private void OnAdClicked()
        {
            if (_survival == null || _adInFlight) return;
            if (AdManager.Instance == null)
            {
                Debug.LogWarning("[ContinueModal] AdManager missing — ad continue path unavailable.");
                return;
            }

            // Release half of split multi-pop (press half from PointerDown).
            GameAudio.Instance?.PlayMultiPopRelease();
            _adInFlight = true;
            if (_adButton != null) _adButton.interactable = false;
            if (_continueButton != null) _continueButton.interactable = false;

            try
            {
                AnalyticsManager.Log("continue_ad_started",
                    "stage", _survival.CurrentStageIndex,
                    "continue_number", _survival.ContinuesUsedThisRun + 1);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            // Same rescue path as paid continue. AdManager.ShowRewardedAd fires
            // the callback only on successful ad completion.
            AdManager.Instance.ShowRewardedAd(onRewardGranted: () =>
            {
                try
                {
                    AnalyticsManager.Log("continue_ad_completed",
                        "stage", _survival.CurrentStageIndex,
                        "continue_number", _survival.ContinuesUsedThisRun + 1);
                }
                catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

                Hide();
                _adInFlight = false;
                _survival.ApplyContinueRescue();
                _survival.ResumeFromContinue();
            });
        }

        private void OnGiveUpClicked()
        {
            if (_adInFlight) return;
            // GIVE UP is the de-emphasized button (no pulse, no press squash);
            // keep the plain UI click here so it reads as the lesser action.
            GameAudio.Instance?.PlayUIClick();
            Hide();
            if (_survival != null) _survival.DeclineContinue();
        }
    
        // ── HEART BREAK (2026-10-21 Spencer: "when you hit the Continue? screen ... animate the heart breaking") ──────────
        // Spencer's art: Resources/Hearts/heart_whole + heart_broken, plus the broken heart SPLIT ALONG ITS CRACK into
        // heart_broken_left/right (made offline from his PNG) so the halves can genuinely fall apart.
        private RectTransform _panelRTForHeart;
        private GameObject _heartRoot;
        private static Sprite LoadHeart(string name)
        {
            var tex = Resources.Load<Texture2D>("Hearts/" + name);
            return tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }
        private Image MakeHeartImage(Transform parent, string name, Sprite sp, float size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>(); img.sprite = sp; img.preserveAspect = true; img.raycastTarget = false;
            return img;
        }
        public static float HEART_SIZE = 150f;
        private void PlayHeartBreak()
        {
            if (_panelRTForHeart == null) return;
            if (_heartRoot != null) Destroy(_heartRoot);
            Sprite whole = LoadHeart("heart_whole"), broken = LoadHeart("heart_broken"),
                   left = LoadHeart("heart_broken_left"), right = LoadHeart("heart_broken_right");
            if (whole == null) return;
            _heartRoot = new GameObject("HeartBreak", typeof(RectTransform));
            _heartRoot.transform.SetParent(_panelRTForHeart, false);
            var root = (RectTransform)_heartRoot.transform;
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.625f); root.sizeDelta = Vector2.zero;
            var wImg = MakeHeartImage(root, "Whole", whole, HEART_SIZE);
            var bImg = broken != null ? MakeHeartImage(root, "Broken", broken, HEART_SIZE) : null;
            var lImg = left   != null ? MakeHeartImage(root, "Left",   left,   HEART_SIZE) : null;
            var rImg = right  != null ? MakeHeartImage(root, "Right",  right,  HEART_SIZE) : null;
            if (bImg != null) bImg.gameObject.SetActive(false);
            if (lImg != null) lImg.gameObject.SetActive(false);
            if (rImg != null) rImg.gameObject.SetActive(false);

            var w = wImg.rectTransform;
            w.localScale = Vector3.zero;
            var seq = DOTween.Sequence().SetUpdate(true).SetTarget(_heartRoot);
            seq.AppendInterval(0.2f);
            seq.Append(w.DOScale(1.1f, 0.22f).SetEase(Ease.OutBack, 2.2f));            // pop in
            seq.Append(w.DOScale(1f, 0.1f));
            for (int i = 0; i < 2; i++)                                                  // two heartbeats
            {
                seq.Append(w.DOScale(1.12f, 0.11f).SetEase(Ease.OutQuad));
                seq.Append(w.DOScale(1f, 0.16f).SetEase(Ease.InQuad));
            }
            seq.AppendInterval(0.1f);
            seq.Append(w.DOShakeAnchorPos(0.32f, 9f, 28, 90f, false, true));            // strain
            seq.AppendCallback(() =>                                                    // CRACK
            {
                GameAudio.Instance?.PlayDeepImpact();
                HapticsManager.Strong();
                wImg.gameObject.SetActive(false);
                if (bImg != null) { bImg.gameObject.SetActive(true); bImg.rectTransform.localScale = Vector3.one * 1.12f; bImg.rectTransform.DOScale(1f, 0.14f).SetUpdate(true); }
            });
            seq.AppendInterval(0.32f);
            seq.AppendCallback(() =>                                                    // FALL APART
            {
                if (lImg == null || rImg == null) return;
                if (bImg != null) bImg.gameObject.SetActive(false);
                lImg.gameObject.SetActive(true); rImg.gameObject.SetActive(true);
                var l = lImg.rectTransform; var r = rImg.rectTransform;
                l.DOAnchorPos(new Vector2(-HEART_SIZE * 0.12f, -HEART_SIZE * 0.08f), 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                r.DOAnchorPos(new Vector2( HEART_SIZE * 0.12f, -HEART_SIZE * 0.08f), 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                l.DOLocalRotate(new Vector3(0f, 0f,  13f), 0.5f).SetEase(Ease.OutBack, 1.6f).SetUpdate(true);
                r.DOLocalRotate(new Vector3(0f, 0f, -13f), 0.5f).SetEase(Ease.OutBack, 1.6f).SetUpdate(true);
            });
        }
}
}
