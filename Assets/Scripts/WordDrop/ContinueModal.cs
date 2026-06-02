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
        private TextMeshProUGUI _continueLabel;
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
            panelGO.GetComponent<Image>().color = new Color(0.12f, 0.08f, 0.22f, 0.98f);
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
            _titleText.color = new Color(1f, 0.84f, 0.42f, 1f);
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.78f);
            titleRT.anchorMax = new Vector2(0.95f, 0.95f);
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
            _bodyText.color = new Color(0.94f, 0.90f, 0.84f, 1f);
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = new Vector2(0.05f, 0.48f);
            bodyRT.anchorMax = new Vector2(0.95f, 0.72f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;

            // Continue (PLAY ON) button (paid path) — primary CTA. Cost is
            // dynamic per SurvivalManager.CurrentContinueCost (50→100 ladder).
            // 2026-06-01: label "CONTINUE" → "PLAY ON" per Spencer; coin glyph
            // preserved. Idle pulse + press-squash + split multi-pop SFX added
            // to mirror StageClearModal's continue button feel.
            var continueBtnGO = MakeButton(panelRT, "ContinueBtn",
                new Vector2(0.10f, 0.42f), new Vector2(0.90f, 0.56f),
                "PLAY ON  ●  50",
                new Color(0.18f, 0.65f, 0.38f, 1f),
                OnContinueClicked);
            PinButtonSize(continueBtnGO, 0.49f);
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
            PinButtonSize(adBtnGO, 0.325f);
            _adButtonGO = adBtnGO;
            _adButton = adBtnGO.GetComponent<Button>();
            _adLabel = adBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            _adButtonCG = adBtnGO.AddComponent<CanvasGroup>();
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
            rt.anchoredPosition = new Vector2(40f, -40f); // mirrors cancel-X inset
            containerGO.GetComponent<Image>().color = new Color(0.10f, 0.06f, 0.18f, 0.92f);
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
            _coinAmountText.color = new Color(1f, 0.85f, 0.30f, 1f);
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
            Debug.Log("[ContinueModal] Buy coins tapped — TODO: open coin store / IAP flow");
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

            btnGO.GetComponent<Image>().color = bgColor;
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

            if (_continueLabel != null)
                _continueLabel.text = canAfford
                    ? $"PLAY ON  ●  {_currentCost}"
                    : $"NEED ● {_currentCost - CoinWallet.Balance} MORE";
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
            seq.AppendCallback(() => FadeInText(_bodyText, CHILD_FADE_DUR));
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
    }
}
