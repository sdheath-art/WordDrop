using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Path A (2026-05-28) — bottom action row, pixel-perfect port of
    /// Spencer's PSD design (canvas 1179×2556, iPhone 16/15 Pro reference).
    ///
    /// Display order (left to right):
    ///   1. Stone Splitter  — RockCrusher  (BoosterManager)
    ///   2. Comet           — BrambleSweep (BoosterManager)
    ///   3. Bloom Bomb      — Bloomburst   (BoosterManager)
    ///   4. Jester Hat      — Wispwhirl    (BoosterManager)
    ///   5. Settings        — opens settings modal (TODO wiring)
    ///
    /// Behind the buttons sits a "bench" panel (W=1222 px, bleeds past
    /// canvas edges) — a single backdrop that visually groups all five
    /// buttons into one bottom-of-screen module.
    ///
    /// Edit + Tile Bag are NOT in this row per Spencer's final design —
    /// edit charges show in the top HUD, Tile Bag is shelved for v1.
    ///
    /// Slot canvas (sortingOrder 50) — bench + buttons.
    /// Aim canvas (sortingOrder 60) — overlay when any targeted booster is armed.
    /// </summary>
    public class BoosterHUDSlot : MonoBehaviour
    {
        public static BoosterHUDSlot Instance { get; private set; }

        private const int SLOT_CANVAS_ORDER = 50;
        private const int AIM_CANVAS_ORDER  = 60;

        // ── PSD-derived layout constants (canvas 1179×2556) ─────────────────────
        // All values in PSD pixels; CanvasScaler with matching reference scales
        // them to whatever device the game is running on.
        //
        // PSD coords are top-left origin. Unity Canvas uses bottom-left origin
        // with anchor at (0.5, 0) so X is from center, Y is from bottom.
        //   anchoredX = (X_psd + W/2) - (CANVAS_W / 2)
        //   anchoredY = CANVAS_H - (Y_psd + H/2)   for pivot (0.5, 0.5)
        //   anchoredY = CANVAS_H - (Y_psd + H)     for pivot (0.5, 0)
        // ───────────────────────────────────────────────────────────────────────
        private const float CANVAS_W = 1179f;
        private const float CANVAS_H = 2556f;

        // 2026-05-28: shrunk 204.16 → 135 PSD so boosters are subordinate to
        // the 150 PSD board tiles (ratio 0.90×, AAA-typical Royal Match /
        // Candy Crush proportions). Row expanded from 5 slots → 7 (Edit + Bag
        // moved into the bottom bar now that boosters are smaller).
        //   7 × 135 + 6 × 26.34 = 1103.04 row span
        //   (1179 - 1103.04) / 2 = 37.98 left margin
        private const float SLOT_SIZE   = 135f;
        private const float SLOT_STEP   = 135f + 26.34f;   // 161.34
        private const float SLOT_LEFT_X = 37.98f;
        private const float SLOT_TOP_Y  = 2270.31f;       // unchanged — keeps the "pops above bench" silhouette

        // Bench panel: W=1222, H=246, X=-16, Y=2322 (bleeds past canvas edges).
        private const float BENCH_W = 1222f;
        private const float BENCH_H = 246f;
        private const float BENCH_X = -16f;
        private const float BENCH_Y = 2322f;

        // Charge badges sized + positioned proportionally to the smaller
        // button. PSD originals were 77.49 size / (+86.36, -63.36) offset
        // tuned to 204.16-px buttons; scale factor = 135/204.16 ≈ 0.6612.
        private const float BADGE_SIZE     = 77.49f * (135f / 204.16f); // ≈ 51.24
        private const float BADGE_OFFSET_X = 86.36f * (135f / 204.16f); // ≈ 57.11
        private const float BADGE_OFFSET_Y = -63.36f * (135f / 204.16f); // ≈ -41.89

        // Procedural sprites built once, reused across all slots/badges.
        private static Sprite _circleSpriteBig;   // for slot buttons
        private static Sprite _circleSpriteSmall; // for charge badges
        private static Sprite _benchSprite;       // for the bottom bench

        private static Sprite GetCircleSpriteBig()
        {
            if (_circleSpriteBig == null)
                _circleSpriteBig = TileRenderer.CreateSolidRoundedRect(256, 256, 128, Color.white);
            return _circleSpriteBig;
        }

        private static Sprite GetCircleSpriteSmall()
        {
            if (_circleSpriteSmall == null)
                _circleSpriteSmall = TileRenderer.CreateSolidRoundedRect(64, 64, 32, Color.white);
            return _circleSpriteSmall;
        }

        private static Sprite GetBenchSprite()
        {
            if (_benchSprite == null)
            {
                // Placeholder rounded-rect — real garden-themed art will replace
                // this later. Radius ~ height/4 for a soft pill look.
                int texW = 1024;
                int texH = Mathf.RoundToInt(texW * (BENCH_H / BENCH_W));
                _benchSprite = TileRenderer.CreateSolidRoundedRect(
                    texW, texH, texH / 4, Color.white);
            }
            return _benchSprite;
        }

        private enum SlotType { Booster, Edit, TileBag, Settings }

        private struct SlotSpec
        {
            public SlotType Type;
            public string   BoosterId;
            public string   PlaceholderChar;
        }

        // 7-slot row: tools (Edit, Bag) left → 4 boosters middle → Settings right.
        private static readonly SlotSpec[] DISPLAY_ORDER = new[]
        {
            new SlotSpec { Type = SlotType.Edit,                                                  PlaceholderChar = "E" },
            new SlotSpec { Type = SlotType.TileBag,                                               PlaceholderChar = "" }, // bag sprite, no letter
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_ROCK_CRUSHER,  PlaceholderChar = "S" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BRAMBLE_SWEEP, PlaceholderChar = "C" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BLOOMBURST,    PlaceholderChar = "B" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_WISPWHIRL,     PlaceholderChar = "J" },
            new SlotSpec { Type = SlotType.Settings,                                              PlaceholderChar = "⚙" },
        };

        private GameObject[]      _slotButtons;
        private Image[]           _slotImages;
        private TextMeshProUGUI[] _slotLabels;
        private TextMeshProUGUI[] _chargeBadges;
        private GameObject[]      _chargeBadgeContainers;

        private Canvas _slotCanvas;
        private Canvas _aimCanvas;
        private TextMeshProUGUI _aimBanner;
        private Button _aimCancelButton;

        // Bench panel + group-animation rest state for the
        // AnimateGroupOut / AnimateGroupIn Candy-Crush-style converge/expand.
        private GameObject _benchGO;
        private bool       _restPositionsCached;
        private Vector2[]  _slotRestPositions;
        private Vector3[]  _slotRestScales;
        private Vector2    _benchCenterAnchored;

        // ── Unity lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildSlotCanvas();
            BuildAimCanvas();
            RefreshDisplay();
        }

        private void OnEnable()
        {
            if (BoosterManager.Instance != null)
                BoosterManager.Instance.OnStateChanged += RefreshDisplay;
        }

        private void OnDisable()
        {
            if (BoosterManager.Instance != null)
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
        }

        private void Start()
        {
            if (BoosterManager.Instance != null)
            {
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
                BoosterManager.Instance.OnStateChanged += RefreshDisplay;
            }
            RefreshDisplay();
        }

        private int _lastEditCount = -1;

        private void Update()
        {
            // Edit-counter polling — refresh the badge when MatchController's
            // GetRewritesRemaining changes (no event to subscribe to).
            int editCount = MatchController.Instance != null
                ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN)
                : 0;
            if (editCount != _lastEditCount)
            {
                _lastEditCount = editCount;
                RefreshDisplay();
            }

            // Aim-mode board-tap polling.
            if (BoosterManager.Instance == null || !BoosterManager.Instance.AimMode) return;
            if (!Input.GetMouseButtonDown(0) && Input.touchCount == 0) return;
            if (Camera.main == null) return;

            Vector3 screenPos = Input.touchCount > 0
                ? (Vector3)Input.GetTouch(0).position
                : Input.mousePosition;

            if (_aimCancelButton != null)
            {
                var rt = _aimCancelButton.GetComponent<RectTransform>();
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null))
                    return;
            }

            Vector3 worldPos = Camera.main.ScreenToWorldPoint(
                new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z));
            worldPos.z = 0f;

            if (GridManager.Instance != null
                && GridManager.Instance.WorldToCell(worldPos, out int col, out int row))
            {
                BoosterManager.Instance.ResolveAim(col, row);
            }
        }

        // ── PSD coord → Unity anchored position helpers ─────────────────────────

        private static Vector2 PsdAnchoredCenter(float xPsd, float yPsd, float wPsd, float hPsd)
        {
            // Caller anchors with (0.5, 0) (bottom-center) and pivot (0.5, 0.5).
            float ax = (xPsd + wPsd * 0.5f) - (CANVAS_W * 0.5f);
            float ay = CANVAS_H - (yPsd + hPsd * 0.5f);
            return new Vector2(ax, ay);
        }

        // ── Slot canvas (5 buttons + bench panel) ───────────────────────────────

        private void BuildSlotCanvas()
        {
            var canvasGO = new GameObject("BoosterHUDSlotCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _slotCanvas = canvasGO.GetComponent<Canvas>();
            _slotCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _slotCanvas.sortingOrder = SLOT_CANVAS_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            // 1.0 = match by height — preferred for portrait phones where the
            // design width-fits the device. PSD canvas IS the iPhone canvas, so
            // any matchWidthOrHeight choice should produce identical layout on
            // iPhone; we pick height-match to be tolerant of taller devices.
            scaler.matchWidthOrHeight = 1.0f;

            // ── Bench panel (drawn first → sits behind buttons) ─────────────────
            var benchGO = new GameObject("BenchPanel",
                typeof(RectTransform), typeof(Image));
            benchGO.transform.SetParent(canvasGO.transform, false);
            _benchGO = benchGO;
            var benchRT = benchGO.GetComponent<RectTransform>();
            benchRT.anchorMin = new Vector2(0.5f, 0f);
            benchRT.anchorMax = new Vector2(0.5f, 0f);
            benchRT.pivot     = new Vector2(0.5f, 0.5f);
            benchRT.sizeDelta = new Vector2(BENCH_W, BENCH_H);
            benchRT.anchoredPosition = PsdAnchoredCenter(BENCH_X, BENCH_Y, BENCH_W, BENCH_H);
            var benchImg = benchGO.GetComponent<Image>();
            benchImg.sprite = GetBenchSprite();
            benchImg.color  = new Color(0.36f, 0.20f, 0.58f, 0.92f); // placeholder garden-purple
            benchImg.raycastTarget = false;

            int slotCount = DISPLAY_ORDER.Length;
            _slotButtons           = new GameObject[slotCount];
            _slotImages            = new Image[slotCount];
            _slotLabels            = new TextMeshProUGUI[slotCount];
            _chargeBadges          = new TextMeshProUGUI[slotCount];
            _chargeBadgeContainers = new GameObject[slotCount];

            var displayFont = GameFont.GetDisplayTMP();

            for (int i = 0; i < slotCount; i++)
            {
                int slotIndex = i;
                var spec = DISPLAY_ORDER[i];

                float xPsd = SLOT_LEFT_X + i * SLOT_STEP;
                Vector2 anchored = PsdAnchoredCenter(xPsd, SLOT_TOP_Y, SLOT_SIZE, SLOT_SIZE);

                var slot = new GameObject($"Slot_{i}_{spec.Type}",
                    typeof(RectTransform), typeof(Image), typeof(Button));
                slot.transform.SetParent(canvasGO.transform, false);
                _slotButtons[i] = slot;

                var rt = slot.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(SLOT_SIZE, SLOT_SIZE);
                rt.anchoredPosition = anchored;

                _slotImages[i] = slot.GetComponent<Image>();
                _slotImages[i].sprite = GetCircleSpriteBig();
                _slotImages[i].color  = SlotBaseColor(spec.Type);

                slot.GetComponent<Button>().onClick.AddListener(() => OnSlotTapped(slotIndex));

                // Settings slot gets a two-stage click feel: PointerDown
                // fires the press half (otherpop_press), release fires the
                // release half (handled in OnSlotTapped below). Other slots
                // keep the standard single-click PlayUIClick.
                if (spec.Type == SlotType.Settings)
                {
                    var trigger = slot.GetComponent<EventTrigger>();
                    if (trigger == null) trigger = slot.AddComponent<EventTrigger>();
                    var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                    pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlaySettingsPress());
                    trigger.triggers.Add(pdEntry);
                }

                // TileBag slot uses the procedural bag sprite as its icon
                // instead of a letter placeholder. All other slots get a
                // text label until real icon art lands.
                if (spec.Type == SlotType.TileBag)
                {
                    var iconGO = new GameObject("BagIcon",
                        typeof(RectTransform), typeof(Image));
                    iconGO.transform.SetParent(slot.transform, false);
                    var iconImg = iconGO.GetComponent<Image>();
                    iconImg.sprite = HandManager.BuildTileBagSpriteForUI();
                    iconImg.color = new Color(1f, 0.95f, 0.78f, 1f);
                    iconImg.raycastTarget = false;
                    iconImg.preserveAspect = true;
                    var iconRT = iconGO.GetComponent<RectTransform>();
                    iconRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iconRT.pivot     = new Vector2(0.5f, 0.5f);
                    iconRT.sizeDelta = new Vector2(SLOT_SIZE * 0.70f, SLOT_SIZE * 0.78f);
                    iconRT.anchoredPosition = Vector2.zero;
                    _slotLabels[i] = null;
                }
                else
                {
                    var labelGO = new GameObject("Label", typeof(RectTransform));
                    labelGO.transform.SetParent(slot.transform, false);
                    _slotLabels[i] = labelGO.AddComponent<TextMeshProUGUI>();
                    if (displayFont != null) _slotLabels[i].font = displayFont;
                    _slotLabels[i].text = spec.PlaceholderChar;
                    _slotLabels[i].fontSize = 52;
                    _slotLabels[i].alignment = TextAlignmentOptions.Center;
                    _slotLabels[i].color = new Color(1f, 0.84f, 0.42f, 1f);
                    _slotLabels[i].raycastTarget = false;
                    var labelRT = labelGO.GetComponent<RectTransform>();
                    labelRT.anchorMin = Vector2.zero;
                    labelRT.anchorMax = Vector2.one;
                    labelRT.offsetMin = Vector2.zero;
                    labelRT.offsetMax = Vector2.zero;
                }

                // Charge badge — boosters, Edit, and TileBag have one.
                // Settings does not.
                bool needsBadge = spec.Type == SlotType.Booster
                              || spec.Type == SlotType.Edit
                              || spec.Type == SlotType.TileBag;
                if (needsBadge)
                {
                    var badgeGO = new GameObject("Badge",
                        typeof(RectTransform), typeof(Image));
                    badgeGO.transform.SetParent(slot.transform, false);
                    _chargeBadgeContainers[i] = badgeGO;
                    var badgeRT = badgeGO.GetComponent<RectTransform>();
                    badgeRT.anchorMin = new Vector2(0.5f, 0.5f);
                    badgeRT.anchorMax = new Vector2(0.5f, 0.5f);
                    badgeRT.pivot     = new Vector2(0.5f, 0.5f);
                    badgeRT.sizeDelta = new Vector2(BADGE_SIZE, BADGE_SIZE);
                    badgeRT.anchoredPosition = new Vector2(BADGE_OFFSET_X, BADGE_OFFSET_Y);
                    var badgeImg = badgeGO.GetComponent<Image>();
                    badgeImg.sprite = GetCircleSpriteSmall();
                    badgeImg.color  = new Color(0.95f, 0.30f, 0.30f, 1f);
                    badgeImg.raycastTarget = false;

                    var chargeGO = new GameObject("Charge", typeof(RectTransform));
                    chargeGO.transform.SetParent(badgeGO.transform, false);
                    _chargeBadges[i] = chargeGO.AddComponent<TextMeshProUGUI>();
                    if (displayFont != null) _chargeBadges[i].font = displayFont;
                    _chargeBadges[i].text = "0";
                    _chargeBadges[i].fontSize = 29; // was 44 — scaled with the smaller badge
                    _chargeBadges[i].alignment = TextAlignmentOptions.Center;
                    _chargeBadges[i].color = Color.white;
                    _chargeBadges[i].raycastTarget = false;
                    var chargeRT = chargeGO.GetComponent<RectTransform>();
                    chargeRT.anchorMin = Vector2.zero;
                    chargeRT.anchorMax = Vector2.one;
                    chargeRT.offsetMin = Vector2.zero;
                    chargeRT.offsetMax = Vector2.zero;
                }
            }
        }

        private static Color SlotBaseColor(SlotType type)
        {
            switch (type)
            {
                case SlotType.Edit:     return new Color(0.16f, 0.22f, 0.32f, 0.92f);
                case SlotType.TileBag:  return new Color(0.18f, 0.25f, 0.20f, 0.92f);
                case SlotType.Settings: return new Color(0.20f, 0.18f, 0.22f, 0.92f);
                default:                return new Color(0.18f, 0.12f, 0.28f, 0.92f);
            }
        }

        // ── Aim-mode canvas ─────────────────────────────────────────────────────

        private void BuildAimCanvas()
        {
            var canvasGO = new GameObject("BoosterAimCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _aimCanvas = canvasGO.GetComponent<Canvas>();
            _aimCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _aimCanvas.sortingOrder = AIM_CANVAS_ORDER;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1.0f;

            var bannerGO = new GameObject("Banner",
                typeof(RectTransform), typeof(Image));
            bannerGO.transform.SetParent(canvasGO.transform, false);
            var bannerRT = bannerGO.GetComponent<RectTransform>();
            bannerRT.anchorMin = new Vector2(0f, 1f);
            bannerRT.anchorMax = new Vector2(1f, 1f);
            bannerRT.pivot     = new Vector2(0.5f, 1f);
            bannerRT.sizeDelta = new Vector2(0f, 220f);
            bannerRT.anchoredPosition = new Vector2(0f, -260f);
            bannerGO.GetComponent<Image>().color = new Color(0.10f, 0.08f, 0.20f, 0.92f);
            bannerGO.GetComponent<Image>().raycastTarget = false;

            var labelGO = new GameObject("Text", typeof(RectTransform));
            labelGO.transform.SetParent(bannerGO.transform, false);
            _aimBanner = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _aimBanner.font = displayFont;
            _aimBanner.text = "TAP A TILE";
            _aimBanner.fontSize = 82;
            _aimBanner.alignment = TextAlignmentOptions.Center;
            _aimBanner.color = new Color(1f, 0.84f, 0.42f, 1f);
            _aimBanner.raycastTarget = false;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            var cancelGO = new GameObject("Cancel",
                typeof(RectTransform), typeof(Image), typeof(Button));
            cancelGO.transform.SetParent(bannerGO.transform, false);
            var cancelRT = cancelGO.GetComponent<RectTransform>();
            cancelRT.anchorMin = new Vector2(1f, 0.5f);
            cancelRT.anchorMax = new Vector2(1f, 0.5f);
            cancelRT.pivot     = new Vector2(1f, 0.5f);
            cancelRT.sizeDelta = new Vector2(150f, 150f);
            cancelRT.anchoredPosition = new Vector2(-44f, 0f);
            cancelGO.GetComponent<Image>().color = new Color(0.55f, 0.18f, 0.18f, 1f);
            _aimCancelButton = cancelGO.GetComponent<Button>();
            _aimCancelButton.onClick.AddListener(OnCancelAimTapped);

            var xGO = new GameObject("X", typeof(RectTransform));
            xGO.transform.SetParent(cancelGO.transform, false);
            var xText = xGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) xText.font = displayFont;
            xText.text = "X";
            xText.fontSize = 82;
            xText.alignment = TextAlignmentOptions.Center;
            xText.color = Color.white;
            xText.raycastTarget = false;
            var xRT = xGO.GetComponent<RectTransform>();
            xRT.anchorMin = Vector2.zero;
            xRT.anchorMax = Vector2.one;
            xRT.offsetMin = Vector2.zero;
            xRT.offsetMax = Vector2.zero;

            _aimCanvas.gameObject.SetActive(false);
        }

        // ── Refresh ─────────────────────────────────────────────────────────────

        private void RefreshDisplay()
        {
            var bm = BoosterManager.Instance;
            bool isAim = bm != null && bm.AimMode;

            for (int i = 0; i < DISPLAY_ORDER.Length; i++)
            {
                var spec = DISPLAY_ORDER[i];

                int charges = 0;
                bool slotActive = true;

                switch (spec.Type)
                {
                    case SlotType.Booster:
                    {
                        Booster booster = bm != null ? bm.GetBoosterById(spec.BoosterId) : null;
                        slotActive = booster != null;
                        charges = bm != null ? bm.GetCharges(spec.BoosterId) : 0;
                        if (slotActive && _slotLabels[i] != null && booster != null)
                            _slotLabels[i].text = booster.DisplayName.Substring(0, 1);
                        break;
                    }
                    case SlotType.Edit:
                        charges = MatchController.Instance != null
                            ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN)
                            : 0;
                        break;
                    case SlotType.TileBag:
                        // TODO Commit 3: source from a per-level Tile Bag
                        // Exchange counter. Hardcoded to 2 for now.
                        charges = 2;
                        break;
                    case SlotType.Settings:
                        break;
                }

                if (_slotButtons[i] != null)
                    _slotButtons[i].SetActive(slotActive);
                if (!slotActive) continue;

                // Charge badge — Royal-Match-style: number when charges > 0,
                // "+" when 0 (acts as a buy-more indicator). Settings has none.
                if (_chargeBadges[i] != null)
                    _chargeBadges[i].text = charges > 0 ? charges.ToString() : "+";
                if (_chargeBadgeContainers[i] != null)
                {
                    bool showBadge = spec.Type == SlotType.Booster
                                  || spec.Type == SlotType.Edit
                                  || spec.Type == SlotType.TileBag;
                    _chargeBadgeContainers[i].SetActive(showBadge);
                }

                var btn = _slotButtons[i].GetComponent<Button>();
                if (btn != null) btn.interactable = !isAim;

                if (_slotImages[i] != null)
                    _slotImages[i].color = SlotBaseColor(spec.Type);
            }

            if (_aimCanvas != null) _aimCanvas.gameObject.SetActive(isAim);
        }

        // ── Group animation (Candy-Crush-style converge/expand when a menu opens) ────
        //
        // When the Settings modal (or any blocking menu) opens, the booster
        // bench scales toward the bench center and disappears. When the modal
        // closes, the reverse animation plays. Bench panel scales in-place;
        // each slot converges its anchoredPosition to the bench center while
        // scaling to zero.

        private const float GROUP_OUT_DUR   = 0.28f;
        private const float GROUP_IN_DUR    = 0.32f;
        private const float GROUP_OVERSHOOT = 1.7f;

        // Group-scale state. A single float drives position + scale on all
        // children in lockstep, simulating "scale a parent transform" without
        // restructuring the hierarchy. groupT = 1 → rest; groupT = 0 →
        // collapsed at the group center.
        private float _groupT = 1f;
        private Tween _groupTween;

        private void CacheRestPositionsIfNeeded()
        {
            if (_restPositionsCached) return;
            if (_slotButtons == null) return;
            // Group center = midpoint of leftmost-to-rightmost slot anchored X.
            // Pinning to slot row (not bench center) keeps the whole group on
            // its own horizontal baseline — bench center sits lower and would
            // pull the row downward.
            float leftX  = float.MaxValue;
            float rightX = float.MinValue;
            _slotRestPositions = new Vector2[_slotButtons.Length];
            _slotRestScales    = new Vector3[_slotButtons.Length];
            for (int i = 0; i < _slotButtons.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                _slotRestPositions[i] = rt.anchoredPosition;
                _slotRestScales[i]    = rt.localScale;
                if (_slotRestPositions[i].x < leftX)  leftX  = _slotRestPositions[i].x;
                if (_slotRestPositions[i].x > rightX) rightX = _slotRestPositions[i].x;
            }
            _benchCenterAnchored = new Vector2((leftX + rightX) * 0.5f, 0f);
            _restPositionsCached = true;
        }

        /// <summary>
        /// True group-scale animation: a single float tweens 1→0, and every
        /// slot's position lerps proportionally to its distance from the
        /// group center, scale lerps proportionally to its rest scale. Looks
        /// like the whole row is one object zooming out into a point —
        /// stage-clear-toss-style cartoon perspective. Bench panel stays put.
        /// </summary>
        public void AnimateGroupOut(float speedMult = 1f)
        {
            CacheRestPositionsIfNeeded();
            float dur = GROUP_OUT_DUR / Mathf.Max(0.001f, speedMult);

            if (_benchGO != null)
            {
                _benchGO.transform.DOKill();
                _benchGO.transform.localScale = Vector3.one;
            }

            if (_slotButtons == null) return;

            // Kill any in-flight per-slot tweens (residue from prior implementation).
            for (int i = 0; i < _slotButtons.Length; i++)
                if (_slotButtons[i] != null) _slotButtons[i].transform.DOKill();

            _groupTween?.Kill();
            _groupTween = DOTween.To(() => _groupT, v =>
            {
                _groupT = v;
                ApplyGroupTransform(v);
            }, 0f, dur).SetEase(Ease.InBack, GROUP_OVERSHOOT);
        }

        /// <summary>
        /// Reverse of AnimateGroupOut — group scale tweens 0→1 with OutBack
        /// cartoon overshoot, so the row pops back into existence at its
        /// rest position. No-op if AnimateGroupOut never ran.
        /// </summary>
        public void AnimateGroupIn(float speedMult = 1f)
        {
            if (!_restPositionsCached) return;
            float dur = GROUP_IN_DUR / Mathf.Max(0.001f, speedMult);

            if (_benchGO != null)
            {
                _benchGO.transform.DOKill();
                _benchGO.transform.localScale = Vector3.one;
            }

            if (_slotButtons == null) return;
            for (int i = 0; i < _slotButtons.Length; i++)
                if (_slotButtons[i] != null) _slotButtons[i].transform.DOKill();

            _groupTween?.Kill();
            _groupTween = DOTween.To(() => _groupT, v =>
            {
                _groupT = v;
                ApplyGroupTransform(v);
            }, 1f, dur).SetEase(Ease.OutBack, GROUP_OVERSHOOT);
        }

        private void ApplyGroupTransform(float groupT)
        {
            // groupT clamped to a sensible visual range — InBack/OutBack
            // overshoot can push it slightly below 0 or above 1, which is
            // fine for scale (overshoot = momentary punch) but we apply it
            // uniformly so position + scale always read as one motion.
            for (int i = 0; i < _slotButtons.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                float deltaX = _slotRestPositions[i].x - _benchCenterAnchored.x;
                rt.anchoredPosition = new Vector2(
                    _benchCenterAnchored.x + deltaX * groupT,
                    _slotRestPositions[i].y); // Y stays — only X converges
                rt.localScale = _slotRestScales[i] * groupT;
            }
        }

        // ── Input handlers ──────────────────────────────────────────────────────

        private void OnSlotTapped(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= DISPLAY_ORDER.Length) return;
            var spec = DISPLAY_ORDER[slotIndex];

            // Booster activations play their OWN SFX (e.g. Jester Hat fires
            // PlayShuffle). The generic UI click was muddying those sounds —
            // suppressed when a booster activates successfully. Only Settings
            // and the "+" buy-more path get the click feedback now.
            bool suppressClick = false;

            switch (spec.Type)
            {
                case SlotType.Booster:
                {
                    int charges = BoosterManager.Instance != null
                        ? BoosterManager.Instance.GetCharges(spec.BoosterId)
                        : 0;
                    if (charges > 0)
                    {
                        BoosterManager.Instance?.TryActivate(spec.BoosterId);
                        suppressClick = true;
                    }
                    else
                    {
                        OnBuyMoreTapped(spec);
                    }
                    break;
                }
                case SlotType.Edit:
                {
                    int charges = MatchController.Instance != null
                        ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN)
                        : 0;
                    if (charges > 0)
                        Debug.Log("[BoosterHUD] Edit tapped — wiring pending (Commit 3)");
                    else
                        OnBuyMoreTapped(spec);
                    break;
                }
                case SlotType.TileBag:
                    // TODO Commit 3: enter Tile Bag Exchange mode.
                    Debug.Log("[BoosterHUD] Tile Bag tapped — wiring pending (Commit 3)");
                    break;
                case SlotType.Settings:
                    if (SettingsModal.Instance == null)
                    {
                        var modalGO = new GameObject("SettingsModalRoot");
                        modalGO.AddComponent<SettingsModal>();
                    }
                    SettingsModal.Instance?.Show();
                    // Release half of the split otherpop — press half fired
                    // from the EventTrigger.PointerDown listener on the slot.
                    GameAudio.Instance?.PlaySettingsRelease();
                    suppressClick = true; // skip the generic UI click below
                    break;
            }

            if (!suppressClick)
                GameAudio.Instance?.PlayUIClick();
        }

        private void OnCancelAimTapped()
        {
            BoosterManager.Instance?.CancelAim();
            GameAudio.Instance?.PlayUIClick();
        }

        private void OnBuyMoreTapped(SlotSpec spec)
        {
            string slotName = spec.Type == SlotType.Booster
                ? (BoosterManager.Instance?.GetBoosterById(spec.BoosterId)?.DisplayName ?? spec.BoosterId)
                : spec.Type.ToString();
            Debug.Log($"[BoosterHUD] '+' tapped on {slotName} — buy flow pending");
        }
    }
}
