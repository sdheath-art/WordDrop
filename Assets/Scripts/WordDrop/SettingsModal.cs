using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Mid-game settings modal — Glimbloom themed (purple/gold), Candy-Crush-
    /// style tabbed layout. Triggered from the ⚙ slot in BoosterHUDSlot.
    ///
    /// Layout (canvas 1179×2556):
    ///   Modal panel: 945×1205 PSD, centered
    ///   Header strip: "Settings" title + ✕ close
    ///   Top tabs row: Haptics / SFX / Music / Help (?)
    ///   Content area: switches based on active tab
    ///     - Haptics: ON/OFF toggle
    ///     - SFX:     0-100% slider (→ GameAudio.Volume)
    ///     - Music:   0-100% slider (→ GameAudio.MusicVolume)
    ///     - Help:    placeholder Q&A text
    ///   Bottom: [Save progress] [Quit level]
    ///
    /// Lazily built on first Show() call.
    /// </summary>
    public class SettingsModal : MonoBehaviour
    {
        public static SettingsModal Instance { get; private set; }

        // PSD layout constants (match BoosterHUDSlot / HandManager / GridManager).
        private const float CANVAS_W = 1179f;
        private const float CANVAS_H = 2556f;
        private const float PANEL_W  = 945f;
        private const float PANEL_H  = 1205f;

        private const int SORT_ORDER = 200; // above everything else

        // Theme colors — Glimbloom mystical-forest palette.
        private static readonly Color SCRIM_COLOR     = new Color(0f, 0f, 0f, 0.55f);
        private static readonly Color PANEL_BG        = new Color(0.20f, 0.13f, 0.32f, 0.98f);
        private static readonly Color PANEL_FRAME     = new Color(0.62f, 0.45f, 0.85f, 1f);   // purple highlight
        private static readonly Color HEADER_GOLD     = new Color(1.00f, 0.84f, 0.42f, 1f);
        private static readonly Color TAB_BG          = new Color(0.30f, 0.20f, 0.45f, 1f);
        private static readonly Color TAB_BG_ACTIVE   = new Color(0.55f, 0.40f, 0.78f, 1f);
        private static readonly Color SLIDER_TRACK    = new Color(0.10f, 0.08f, 0.18f, 1f);
        private static readonly Color SLIDER_FILL     = new Color(0.40f, 0.90f, 0.95f, 1f);
        private static readonly Color SAVE_BTN_COLOR  = new Color(0.30f, 0.62f, 0.95f, 1f);
        private static readonly Color QUIT_BTN_COLOR  = new Color(0.92f, 0.30f, 0.55f, 1f);
        private static readonly Color CLOSE_BTN_COLOR = new Color(0.92f, 0.22f, 0.30f, 1f);

        private enum Tab { Haptics, SFX, Music, Help }
        private Tab _activeTab = Tab.SFX;

        // SFX-sample throttle so dragging the slider doesn't spam audio.
        // Only fires a sample if >0.15s has passed since last sample.
        private float _lastSampleSfxTime = -1f;

        // Modal open time — used to gate the slider onValueChanged listener
        // so Unity's init-pass spurious events don't reach the setter and
        // zero out audio volumes before the user interacts.
        private float _modalOpenTime = -999f;
        private const float SLIDER_INIT_GRACE = 0.5f;

        // One-shot heal for stuck legacy mute flags (M-key debug hotkey).
        private bool _clearedLegacyMutesOnce = false;

        private GameObject _canvasGO;
        private GameObject _panelGO;
        private GameObject _contentRoot;

        // Tab buttons
        private Image[] _tabImages;

        // Content elements per tab (built once, toggled visible)
        private GameObject _hapticsView;
        private GameObject _sfxView;
        private GameObject _musicView;
        private GameObject _helpView;

        // Cached sprites
        private static Sprite _panelSprite;
        private static Sprite _pillSprite;
        private static Sprite _circleSprite;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // Same drop/exit speed multiplier as StageClearModal so settings + stage
        // clear share an identical landing/dismiss feel.
        private const float MODAL_SPEED_MULT = 1.5f;

        /// <summary>Open the modal. Lazily builds on first call.</summary>
        public void Show()
        {
            if (_canvasGO == null) BuildCanvas();
            _canvasGO.SetActive(true);
            _modalOpenTime = Time.unscaledTime;

            // BLOCK GAMEPLAY INPUT while the modal is up. The UI scrim
            // absorbs Canvas raycasts but does NOT block world-space input
            // (hand drag, tile selection, board taps) — those flow through
            // Camera.ScreenPointToRay and ignore UI raycasting. Pausing the
            // Survival overlay sets IsOverlayPaused=true, which HandManager
            // checks at line 820 to gate ALL hand input. Same pattern
            // StageClearModal + BoosterChoiceModal use.
            // Cleared in Hide()'s onComplete callback so input stays locked
            // until the modal is fully gone.
            SurvivalManager.Instance?.SetOverlayPaused(true);
            // 2026-05-29: clear mute flags ONCE on first open (heals any
            // stuck-true value from the M-key debug hotkey legacy). After
            // that the sliders are the source of truth — saved values
            // including 0% should persist across reboots.
            if (GameAudio.Instance != null && !_clearedLegacyMutesOnce)
            {
                GameAudio.Instance.MusicMuted = false;
                GameAudio.Instance.Muted      = false;
                _clearedLegacyMutesOnce = true;
            }
            RefreshTabState();

            // Same arrival animation as StageClearModal — drop from above,
            // dip past rest, rebound, settle.
            if (_panelGO != null)
            {
                var rt = _panelGO.GetComponent<RectTransform>();
                if (rt != null)
                {
                    rt.anchoredPosition = Vector2.zero; // ensure rest position before tween reads it
                    UIAnimations.DropInWithBounce(rt, speedMult: MODAL_SPEED_MULT);
                }
            }
            // Candy-Crush-style "menu opened — UI collapses" animation:
            // the 7-slot booster bench AND the hand tiles (cards + NEXT
            // preview) all converge to their respective centers and scale
            // out while the modal slides in. The hand HOLDER (pill,
            // divider, NEXT label/socket) stays put. Reverses in Hide().
            // Whoosh accompanies the collapse — no separate modal-arrival
            // sound anymore; the icon animation owns the audio.
            BoosterHUDSlot.Instance?.AnimateGroupOut();
            HandManager.Instance?.AnimateHandTilesOut();
            GameAudio.Instance?.PlayUIGroupCollapse();
        }

        public void Hide()
        {
            // Reverse of the converge-out animation in Show — booster bench
            // AND hand tiles expand back to rest as the modal flies away.
            // Whoosh accompanies the pop-back-in.
            BoosterHUDSlot.Instance?.AnimateGroupIn();
            HandManager.Instance?.AnimateHandTilesIn();
            GameAudio.Instance?.PlayUIGroupExpand();

            // Same dismiss animation as StageClearModal — InBack lift off-screen
            // upward. SetActive(false) only after the tween completes so the
            // exit reads instead of snapping. No sound on close per spec.
            //
            // Gameplay input stays LOCKED (IsOverlayPaused=true) until the
            // tween's onComplete fires — that way the player can't tap
            // through a half-visible modal during the exit animation.
            if (_panelGO == null)
            {
                if (_canvasGO != null) _canvasGO.SetActive(false);
                SurvivalManager.Instance?.SetOverlayPaused(false);
                return;
            }
            var rt = _panelGO.GetComponent<RectTransform>();
            if (rt == null)
            {
                if (_canvasGO != null) _canvasGO.SetActive(false);
                SurvivalManager.Instance?.SetOverlayPaused(false);
                return;
            }
            UIAnimations.ExitUp(rt, () => {
                if (_canvasGO != null) _canvasGO.SetActive(false);
                if (rt != null) rt.anchoredPosition = Vector2.zero; // reset for next open
                SurvivalManager.Instance?.SetOverlayPaused(false);
            }, speedMult: MODAL_SPEED_MULT);
        }

        // ── Build ───────────────────────────────────────────────────────────────

        private void BuildCanvas()
        {
            _canvasGO = new GameObject("SettingsModalCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            _canvasGO.transform.SetParent(transform, false);
            var canvas = _canvasGO.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SORT_ORDER;
            var scaler = _canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1.0f;

            BuildScrim(_canvasGO.transform);
            BuildPanel(_canvasGO.transform);
        }

        private void BuildScrim(Transform parent)
        {
            var scrim = new GameObject("Scrim", typeof(RectTransform), typeof(Image), typeof(Button));
            scrim.transform.SetParent(parent, false);
            var rt = scrim.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            scrim.GetComponent<Image>().color = SCRIM_COLOR;
            // Scrim absorbs taps OUTSIDE the panel so they don't fall through
            // to the game underneath, but no longer dismisses the modal —
            // Spencer's design: modals only close via explicit button taps
            // (X / Save Progress / Quit Level), never via outside-tap.
            var scrimBtn = scrim.GetComponent<Button>();
            scrimBtn.transition = Selectable.Transition.None;
        }

        private void BuildPanel(Transform parent)
        {
            _panelGO = new GameObject("Panel",
                typeof(RectTransform), typeof(Image));
            _panelGO.transform.SetParent(parent, false);
            var rt = _panelGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(PANEL_W, PANEL_H);
            rt.anchoredPosition = Vector2.zero;

            var img = _panelGO.GetComponent<Image>();
            img.sprite = GetPanelSprite();
            img.color  = PANEL_BG;

            // Panel has a Button with no onClick — purely to absorb taps so
            // tapping inside the panel doesn't bubble. Scrim handles outside
            // taps (also absorbed, no dismiss). Both layers exist so input
            // never leaks through to the gameplay underneath while the
            // modal is open.
            var blocker = _panelGO.AddComponent<Button>();
            blocker.transition = Selectable.Transition.None;

            BuildHeader(_panelGO.transform);
            BuildTabs(_panelGO.transform);
            BuildContent(_panelGO.transform);
            BuildBottomButtons(_panelGO.transform);
            BuildCloseButton(_panelGO.transform);
        }

        // ── Header ──────────────────────────────────────────────────────────────

        private void BuildHeader(Transform panel)
        {
            // Header strip — just a label and a thin highlight underline. The
            // Candy Crush cloud header is theme-specific; Glimbloom uses a
            // mystical-script "Settings" text + a thin gold underline.
            var headerGO = new GameObject("Header",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            headerGO.transform.SetParent(panel, false);
            var rt = headerGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 1f);
            rt.sizeDelta = new Vector2(0f, 140f);
            rt.anchoredPosition = new Vector2(0f, -32f);
            var tmp = headerGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) tmp.font = font;
            tmp.text = "Settings";
            tmp.fontSize = 72;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = HEADER_GOLD;
            tmp.raycastTarget = false;

            // Underline
            var lineGO = new GameObject("HeaderLine",
                typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(panel, false);
            var lrt = lineGO.GetComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.1f, 1f);
            lrt.anchorMax = new Vector2(0.9f, 1f);
            lrt.pivot     = new Vector2(0.5f, 1f);
            lrt.sizeDelta = new Vector2(0f, 4f);
            lrt.anchoredPosition = new Vector2(0f, -178f);
            lineGO.GetComponent<Image>().color = new Color(HEADER_GOLD.r, HEADER_GOLD.g, HEADER_GOLD.b, 0.4f);
        }

        // ── Tabs ────────────────────────────────────────────────────────────────

        private void BuildTabs(Transform panel)
        {
            // 4 tab buttons in a row near the top of the panel.
            const float TAB_SIZE = 130f;
            const float TAB_GAP  = 40f;
            const float ROW_Y    = -240f; // from panel top

            var tabRow = new GameObject("TabRow", typeof(RectTransform));
            tabRow.transform.SetParent(panel, false);
            var trrt = tabRow.GetComponent<RectTransform>();
            trrt.anchorMin = new Vector2(0.5f, 1f);
            trrt.anchorMax = new Vector2(0.5f, 1f);
            trrt.pivot     = new Vector2(0.5f, 1f);
            float rowW = TAB_SIZE * 4 + TAB_GAP * 3;
            trrt.sizeDelta = new Vector2(rowW, TAB_SIZE);
            trrt.anchoredPosition = new Vector2(0f, ROW_Y);

            _tabImages = new Image[4];
            string[] labels = { "≋", "♪", "♬", "?" }; // placeholder glyphs — TODO real icon art
            Tab[] tabIds = { Tab.Haptics, Tab.SFX, Tab.Music, Tab.Help };

            float startX = -rowW * 0.5f + TAB_SIZE * 0.5f;
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                var tabGO = new GameObject($"Tab_{tabIds[i]}",
                    typeof(RectTransform), typeof(Image), typeof(Button));
                tabGO.transform.SetParent(tabRow.transform, false);
                var trt = tabGO.GetComponent<RectTransform>();
                trt.anchorMin = new Vector2(0.5f, 0.5f);
                trt.anchorMax = new Vector2(0.5f, 0.5f);
                trt.pivot     = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(TAB_SIZE, TAB_SIZE);
                trt.anchoredPosition = new Vector2(startX + i * (TAB_SIZE + TAB_GAP), 0f);

                _tabImages[i] = tabGO.GetComponent<Image>();
                _tabImages[i].sprite = GetCircleSprite();
                _tabImages[i].color  = TAB_BG;

                tabGO.GetComponent<Button>().onClick.AddListener(() => OnTabTapped(tabIds[idx]));

                // Glyph label
                var lblGO = new GameObject("Label",
                    typeof(RectTransform), typeof(TextMeshProUGUI));
                lblGO.transform.SetParent(tabGO.transform, false);
                var lrt = lblGO.GetComponent<RectTransform>();
                lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
                lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
                var ltmp = lblGO.GetComponent<TextMeshProUGUI>();
                var font = GameFont.GetDisplayTMP();
                if (font != null) ltmp.font = font;
                ltmp.text = labels[i];
                ltmp.fontSize = 64;
                ltmp.alignment = TextAlignmentOptions.Center;
                ltmp.color = Color.white;
                ltmp.raycastTarget = false;
            }
        }

        private void OnTabTapped(Tab tab)
        {
            _activeTab = tab;
            // Just the RELEASE half of otherpop (no press pair) — tab
            // switching is a subtler interaction than committing an action,
            // so a single tick reads better than a full press+release.
            GameAudio.Instance?.PlaySettingsRelease();
            RefreshTabState();
        }

        private void RefreshTabState()
        {
            if (_tabImages != null)
            {
                Tab[] order = { Tab.Haptics, Tab.SFX, Tab.Music, Tab.Help };
                for (int i = 0; i < _tabImages.Length; i++)
                {
                    if (_tabImages[i] == null) continue;
                    _tabImages[i].color = order[i] == _activeTab ? TAB_BG_ACTIVE : TAB_BG;
                }
            }
            if (_hapticsView != null) _hapticsView.SetActive(_activeTab == Tab.Haptics);
            if (_sfxView    != null) _sfxView.SetActive(_activeTab == Tab.SFX);
            if (_musicView  != null) _musicView.SetActive(_activeTab == Tab.Music);
            if (_helpView   != null) _helpView.SetActive(_activeTab == Tab.Help);
        }

        // ── Content area (per-tab) ──────────────────────────────────────────────

        private void BuildContent(Transform panel)
        {
            _contentRoot = new GameObject("Content", typeof(RectTransform));
            _contentRoot.transform.SetParent(panel, false);
            var rt = _contentRoot.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.08f, 0.30f);
            rt.anchorMax = new Vector2(0.92f, 0.66f);
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            _hapticsView = BuildHapticsView(_contentRoot.transform);
            // SFX slider plays a sample on every drag tick so the user can
            // actually HEAR the new volume. Music slider doesn't need samples
            // — the currently-playing track responds live to volume changes.
            _sfxView     = BuildSliderView(_contentRoot.transform, "SFX",
                                          () => GameAudio.Instance?.Volume ?? 0.8f,
                                          v => { if (GameAudio.Instance != null) GameAudio.Instance.Volume = v; },
                                          sampleOnChange: true);
            _musicView   = BuildSliderView(_contentRoot.transform, "Music",
                                          () => GameAudio.Instance?.MusicVolume ?? 0.3f,
                                          v => { if (GameAudio.Instance != null) GameAudio.Instance.MusicVolume = v; },
                                          sampleOnChange: false);
            _helpView    = BuildHelpView(_contentRoot.transform);
        }

        private GameObject BuildHapticsView(Transform parent)
        {
            var go = new GameObject("HapticsView", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            BuildContentLabel(go.transform, "Haptics", anchorY: 0.82f);

            // ON/OFF toggle styled as a pill switch.
            var toggleGO = new GameObject("HapticsToggle",
                typeof(RectTransform), typeof(Image), typeof(Button));
            toggleGO.transform.SetParent(go.transform, false);
            var trt = toggleGO.GetComponent<RectTransform>();
            trt.anchorMin = new Vector2(0.5f, 0.40f);
            trt.anchorMax = new Vector2(0.5f, 0.40f);
            trt.pivot     = new Vector2(0.5f, 0.5f);
            trt.sizeDelta = new Vector2(320f, 110f);
            trt.anchoredPosition = Vector2.zero;
            var togImg = toggleGO.GetComponent<Image>();
            togImg.sprite = GetPillSprite();
            togImg.color  = HapticsManager.Enabled ? SLIDER_FILL : SLIDER_TRACK;

            var lblGO = new GameObject("Label",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGO.transform.SetParent(toggleGO.transform, false);
            var lrt = lblGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var ltmp = lblGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) ltmp.font = font;
            ltmp.text = HapticsManager.Enabled ? "ON" : "OFF";
            ltmp.fontSize = 52;
            ltmp.alignment = TextAlignmentOptions.Center;
            ltmp.color = Color.white;
            ltmp.raycastTarget = false;

            toggleGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                HapticsManager.Enabled = !HapticsManager.Enabled;
                togImg.color = HapticsManager.Enabled ? SLIDER_FILL : SLIDER_TRACK;
                ltmp.text    = HapticsManager.Enabled ? "ON" : "OFF";
                // Subtle UI selector — single tick (otherpop second half).
                GameAudio.Instance?.PlaySettingsRelease();
            });
            return go;
        }

        private GameObject BuildSliderView(Transform parent, string title,
                                           System.Func<float> getter, System.Action<float> setter,
                                           bool sampleOnChange = false)
        {
            var go = new GameObject($"{title}View", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            BuildContentLabel(go.transform, title, anchorY: 0.82f);

            // Slider with custom pill background and fill.
            var sliderGO = new GameObject($"{title}Slider",
                typeof(RectTransform), typeof(Slider));
            sliderGO.transform.SetParent(go.transform, false);
            var srt = sliderGO.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0.05f, 0.40f);
            srt.anchorMax = new Vector2(0.95f, 0.40f);
            srt.pivot     = new Vector2(0.5f, 0.5f);
            srt.sizeDelta = new Vector2(0f, 60f);
            srt.anchoredPosition = Vector2.zero;
            var slider = sliderGO.GetComponent<Slider>();

            // Background pill
            var bgGO = new GameObject("Background",
                typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(sliderGO.transform, false);
            var bgrt = bgGO.GetComponent<RectTransform>();
            bgrt.anchorMin = Vector2.zero; bgrt.anchorMax = Vector2.one;
            bgrt.offsetMin = Vector2.zero; bgrt.offsetMax = Vector2.zero;
            var bgImg = bgGO.GetComponent<Image>();
            bgImg.sprite = GetPillSprite();
            bgImg.color = SLIDER_TRACK;

            // Fill area (Slider needs a Fill child)
            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(sliderGO.transform, false);
            var fart = fillAreaGO.GetComponent<RectTransform>();
            fart.anchorMin = new Vector2(0f, 0f);
            fart.anchorMax = new Vector2(1f, 1f);
            fart.offsetMin = new Vector2(8f, 8f);
            fart.offsetMax = new Vector2(-8f, -8f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = new Vector2(1f, 1f);
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            var fillImg = fillGO.GetComponent<Image>();
            fillImg.sprite = GetPillSprite();
            fillImg.color = SLIDER_FILL;

            // Handle
            var handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaGO.transform.SetParent(sliderGO.transform, false);
            var hart = handleAreaGO.GetComponent<RectTransform>();
            hart.anchorMin = Vector2.zero; hart.anchorMax = Vector2.one;
            hart.offsetMin = new Vector2(30f, 0f);
            hart.offsetMax = new Vector2(-30f, 0f);
            var handleGO = new GameObject("Handle",
                typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            var hrt = handleGO.GetComponent<RectTransform>();
            hrt.sizeDelta = new Vector2(90f, 90f);
            hrt.anchorMin = new Vector2(0f, 0.5f);
            hrt.anchorMax = new Vector2(0f, 0.5f);
            hrt.pivot     = new Vector2(0.5f, 0.5f);
            var hImg = handleGO.GetComponent<Image>();
            hImg.sprite = GetCircleSprite();
            hImg.color  = Color.white;

            // Wire slider
            slider.targetGraphic = hImg;
            slider.fillRect      = fillRT;
            slider.handleRect    = hrt;
            slider.direction     = Slider.Direction.LeftToRight;
            slider.minValue      = 0f;
            slider.maxValue      = 1f;
            // SetValueWithoutNotify (vs slider.value =) guarantees no
            // onValueChanged event fires during construction. Previously, a
            // spurious frame-1 event was firing setter(0), zeroing MusicVolume
            // when the modal opened. Listener attached AFTER so only user
            // drags reach the setter.
            slider.SetValueWithoutNotify(Mathf.Clamp01(getter()));
            slider.onValueChanged.AddListener(v =>
            {
                // Time gate: suppress all onValueChanged firings during the
                // first 0.5s after the modal opens. Unity's Slider component
                // emits spurious value-change events during layout/init
                // passes — those were zeroing MusicVolume before the user
                // ever touched the slider. After the grace period, only
                // genuine user drags reach the setter.
                if (Time.unscaledTime - _modalOpenTime < SLIDER_INIT_GRACE) return;

                setter?.Invoke(v);
                if (sampleOnChange && GameAudio.Instance != null
                    && Time.unscaledTime - _lastSampleSfxTime > 0.20f)
                {
                    GameAudio.Instance.PlayTileDrop();
                    _lastSampleSfxTime = Time.unscaledTime;
                }
            });

            // Percentage label
            var pctGO = new GameObject("Pct",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            pctGO.transform.SetParent(go.transform, false);
            var pctRT = pctGO.GetComponent<RectTransform>();
            pctRT.anchorMin = new Vector2(0.5f, 0.15f);
            pctRT.anchorMax = new Vector2(0.5f, 0.15f);
            pctRT.pivot     = new Vector2(0.5f, 0.5f);
            pctRT.sizeDelta = new Vector2(400f, 60f);
            pctRT.anchoredPosition = Vector2.zero;
            var pctTmp = pctGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) pctTmp.font = font;
            pctTmp.fontSize = 44;
            pctTmp.alignment = TextAlignmentOptions.Center;
            pctTmp.color = new Color(0.95f, 0.92f, 0.86f, 0.9f);
            pctTmp.raycastTarget = false;
            pctTmp.text = $"{Mathf.RoundToInt(slider.value * 100)}%";
            slider.onValueChanged.AddListener(v => pctTmp.text = $"{Mathf.RoundToInt(v * 100)}%");

            return go;
        }

        private GameObject BuildHelpView(Transform parent)
        {
            var go = new GameObject("HelpView", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;

            BuildContentLabel(go.transform, "Help", anchorY: 0.85f);

            var bodyGO = new GameObject("HelpBody",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            bodyGO.transform.SetParent(go.transform, false);
            var brt = bodyGO.GetComponent<RectTransform>();
            brt.anchorMin = new Vector2(0.05f, 0.05f);
            brt.anchorMax = new Vector2(0.95f, 0.78f);
            brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
            var bTmp = bodyGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetUITMP();
            if (font != null) bTmp.font = font;
            bTmp.text =
                "How do I score?\n" +
                "Drop letters next to existing ones to spell words 3+ letters long.\n\n" +
                "What are primed words?\n" +
                "Detonate them by playing a word that touches their tiles.";
            bTmp.fontSize = 32;
            bTmp.alignment = TextAlignmentOptions.TopLeft;
            bTmp.color = new Color(0.95f, 0.92f, 0.86f, 0.9f);
            bTmp.enableWordWrapping = true;
            bTmp.raycastTarget = false;
            return go;
        }

        private void BuildContentLabel(Transform parent, string text, float anchorY)
        {
            var lblGO = new GameObject("ContentLabel",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGO.transform.SetParent(parent, false);
            var rt = lblGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, anchorY);
            rt.anchorMax = new Vector2(1f, anchorY);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(0f, 80f);
            rt.anchoredPosition = Vector2.zero;
            var tmp = lblGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = 56;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = HEADER_GOLD;
            tmp.raycastTarget = false;
        }

        // ── Bottom buttons ──────────────────────────────────────────────────────

        private void BuildBottomButtons(Transform panel)
        {
            BuildPillButton(panel, "Save progress", SAVE_BTN_COLOR,
                anchorY: -940f, onClick: OnSaveProgressTapped);
            BuildPillButton(panel, "Quit level", QUIT_BTN_COLOR,
                anchorY: -1075f, onClick: OnQuitLevelTapped);
        }

        private void BuildPillButton(Transform panel, string label, Color color,
                                     float anchorY, System.Action onClick)
        {
            var btnGO = new GameObject(label + "Btn",
                typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(panel, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 1f);
            rt.anchorMax = new Vector2(0.5f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(680f, 120f);
            rt.anchoredPosition = new Vector2(0f, anchorY);
            var img = btnGO.GetComponent<Image>();
            img.sprite = GetPillSprite();
            img.color  = color;

            var lblGO = new GameObject("Label",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGO.transform.SetParent(btnGO.transform, false);
            var lrt = lblGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var ltmp = lblGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) ltmp.font = font;
            ltmp.text = label;
            ltmp.fontSize = 52;
            ltmp.alignment = TextAlignmentOptions.Center;
            ltmp.color = Color.white;
            ltmp.raycastTarget = false;

            // Standardized "button click" sound pair — multipop press/release
            // halves. Same as the Continue button on StageClearModal so any
            // pill button in the game reads with the same tactile feel.
            var pillTrigger = btnGO.GetComponent<EventTrigger>();
            if (pillTrigger == null) pillTrigger = btnGO.AddComponent<EventTrigger>();
            var pillDown = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            pillDown.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            pillTrigger.triggers.Add(pillDown);

            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                onClick?.Invoke();
            });
        }

        private void OnSaveProgressTapped()
        {
            // PlayerPrefs.Save flushes audio/haptics persistence immediately.
            PlayerPrefs.Save();
            Debug.Log("[SettingsModal] Save progress tapped — preferences flushed.");
            Hide();
        }

        private void OnQuitLevelTapped()
        {
            // Hide the modal first so its ExitUp animation runs alongside
            // the screen-slide transition (cosmetic — the slide covers
            // most of the modal anyway). Then transition to Menu, which
            // is the canonical "return to main menu" path:
            //   • LevelController.AbortLevel cleanup
            //   • BlitzManager/SurvivalManager/DailyDropManager reset
            //   • MenuUI.SetVisible(true)
            //   • menu BGM swap
            // All driven from the GameState.Menu case in GameManager.
            Hide();
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Menu);
        }

        // ── Close button ────────────────────────────────────────────────────────

        private void BuildCloseButton(Transform panel)
        {
            var btnGO = new GameObject("CloseBtn",
                typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(panel, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(110f, 110f);
            rt.anchoredPosition = new Vector2(-40f, -40f);
            var img = btnGO.GetComponent<Image>();
            img.sprite = GetCircleSprite();
            img.color  = CLOSE_BTN_COLOR;

            var lblGO = new GameObject("X",
                typeof(RectTransform), typeof(TextMeshProUGUI));
            lblGO.transform.SetParent(btnGO.transform, false);
            var lrt = lblGO.GetComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
            var ltmp = lblGO.GetComponent<TextMeshProUGUI>();
            var font = GameFont.GetDisplayTMP();
            if (font != null) ltmp.font = font;
            ltmp.text = "✕";
            ltmp.fontSize = 64;
            ltmp.alignment = TextAlignmentOptions.Center;
            ltmp.color = Color.white;
            ltmp.raycastTarget = false;

            // Standardized "button click" sound pair — same as Continue
            // and the bottom pills. Press half on PointerDown, release half
            // on click.
            var closeTrigger = btnGO.GetComponent<EventTrigger>();
            if (closeTrigger == null) closeTrigger = btnGO.AddComponent<EventTrigger>();
            var closeDown = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            closeDown.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            closeTrigger.triggers.Add(closeDown);

            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                Hide();
            });
        }

        // ── Sprite factories ────────────────────────────────────────────────────

        private static Sprite GetPanelSprite()
        {
            if (_panelSprite == null)
                _panelSprite = TileRenderer.CreateSolidRoundedRect(512, 640, 60, Color.white);
            return _panelSprite;
        }

        private static Sprite GetPillSprite()
        {
            if (_pillSprite == null)
                _pillSprite = TileRenderer.CreateSolidRoundedRect(256, 64, 32, Color.white);
            return _pillSprite;
        }

        private static Sprite GetCircleSprite()
        {
            if (_circleSprite == null)
                _circleSprite = TileRenderer.CreateSolidRoundedRect(128, 128, 64, Color.white);
            return _circleSprite;
        }
    }
}
