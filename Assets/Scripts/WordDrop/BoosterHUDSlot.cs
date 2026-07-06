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
                    texW, texH, 0, Color.white); // 2026-06-24 Spencer: square corners on the bottom bench (was rounded)
            }
            return _benchSprite;
        }

        // Soft radial glow + additive material for the EDITS pip restore burst (fake bloom over the
        // dark navy panel). Glow textures import as Default → load Texture2D + Sprite.Create. 2026-06-24.
        private static Sprite _pipGlowSprite; private static bool _pipGlowTried;
        private Sprite LoadGlowSprite()
        {
            if (_pipGlowTried) return _pipGlowSprite;
            _pipGlowTried = true;
            _pipGlowSprite = Resources.Load<Sprite>("Particles/glow")
                          ?? Resources.Load<Sprite>("Particles/soft_circle");
            if (_pipGlowSprite != null) return _pipGlowSprite;
            Texture2D tex = Resources.Load<Texture2D>("Particles/glow")
                         ?? Resources.Load<Texture2D>("Particles/soft_circle")
                         ?? Resources.Load<Texture2D>("Particles/vfx_glow");
            if (tex != null)
                _pipGlowSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _pipGlowSprite;
        }

        private static Material _pipAddMat;
        private Material LoadAdditiveGlowMaterial()
        {
            if (_pipAddMat != null) return _pipAddMat;
            Shader s = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            if (s != null) _pipAddMat = new Material(s);
            return _pipAddMat;
        }

        private static Sprite _lockSprite;
        private static Sprite GetLockSprite()
        {
            if (_lockSprite == null)
            {
                _lockSprite = Resources.Load<Sprite>("Tiles/icon_lock");
                if (_lockSprite == null) // PNG imported as a plain Texture (default) → build a Sprite
                {
                    var tex = Resources.Load<Texture2D>("Tiles/icon_lock");
                    if (tex != null)
                        _lockSprite = Sprite.Create(
                            tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            return _lockSprite;
        }

        private enum SlotType { Booster, Edit, TileBag, Settings }

        /// <summary>Onboarding lock: true while this tool is gated for early levels. Settings is
        /// never locked. 2026-06-09.</summary>
        private static bool IsSlotLocked(SlotSpec spec)
        {
            switch (spec.Type)
            {
                case SlotType.Edit:    return TutorialLocks.EditLocked;
                case SlotType.TileBag: return TutorialLocks.BagLocked;
                case SlotType.Booster: return TutorialLocks.BoostersLocked;
                default:               return false; // Settings always available
            }
        }

        private struct SlotSpec
        {
            public SlotType Type;
            public string   BoosterId;
            public string   PlaceholderChar;
        }

        // 2026-06-24 layout (Spencer): Edit + Bag moved OUT of the bench row and up to
        // FLANK the tile holder (Edit left, Bag right). The bench row is now the 5
        // boosters + Settings, evenly spaced full-width. Mallet is the new 5th booster.
        // Edit/Bag are still listed here (so they keep their slot wiring/charges/locks);
        // their positions are computed specially in BuildSlotCanvas — see FLANK_* below.
        private static readonly SlotSpec[] DISPLAY_ORDER = new[]
        {
            new SlotSpec { Type = SlotType.Edit,                                                  PlaceholderChar = "E" },
            // 2026-06-24 (Spencer): Tile Bag swap CUT for launch — rarely used, non-obvious, and the
            // Mallet booster covers "ditch a bad tile." Swap code left dormant (IsScreenPointOverTileBag
            // returns false with no bag slot, so drag-onto-bag simply never triggers). Re-add this
            // SlotSpec line to bring it back.
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_MALLET,        PlaceholderChar = "M" }, // 2026-06-24: Mallet first
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_ROCK_CRUSHER,  PlaceholderChar = "S" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BRAMBLE_SWEEP, PlaceholderChar = "C" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BLOOMBURST,    PlaceholderChar = "B" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_WISPWHIRL,     PlaceholderChar = "J" },
            new SlotSpec { Type = SlotType.Settings,                                              PlaceholderChar = "⚙" },
        };

        // ── 2026-06-24 layout constants ─────────────────────────────────────────
        // Bench row now holds 6 slots (5 boosters + Settings) evenly spaced across the
        // SAME usable span the old 7-slot row used (left margin 37.98, span 1103.04),
        // so the bench backdrop still frames them.
        private const int   BENCH_SLOT_COUNT = 6;
        private const float BENCH_ROW_SPAN   = 1103.04f; // 1179 - 2*37.98
        private const float SLOT_STEP_BENCH  = (BENCH_ROW_SPAN - BENCH_SLOT_COUNT * SLOT_SIZE) / (BENCH_SLOT_COUNT - 1) + SLOT_SIZE; // ≈193.61
        // 2026-06-24 (Spencer): Edit is a rounded-SQUARE TOOL attached UNDER the tile holder
        // panel's bottom edge, CENTRED (Bag cut for launch), distinct from the round consumable
        // boosters. Holder raised 45 PSD → pill now spans Y 1888..2088; the tab tucks up ~12px
        // under the bottom edge so it reads as attached and hangs into the gap above the boosters.
        // FLANK_Y_PSD is the slot's PSD top edge. BAG_X_PSD kept for the dormant swap-X fallback.
        private const float FLANK_SIZE  = 120f;
        private const float FLANK_Y_PSD = 2114f;   // 2026-06-24: lowered ~38 PSD (was 2076, tucked under the pill) — drops the SWAPS panel into the gap
        private const float EDIT_X_PSD  = 529.5f;  // (legacy) single-square Edit left edge — superseded by the panel
        private const float BAG_X_PSD   = 889f;    // (dormant) swap-X fallback only — Bag slot removed
        // "EDITS" pip panel (replaces the Edit square): wide rounded panel, label + 3 gold pips.
        private const float EDIT_PANEL_W = 340f;
        private const float EDIT_PANEL_H = 100f;   // condensed height (was 150 → 120 → 100)
        private const int   EDIT_PIP_COUNT = 3;    // matches the survival rewrite cap of 3
        private static readonly Color PIP_FULL  = new Color(0.95f, 0.48f, 0.64f, 1f);   // 2026-06-24: candy pink (matches HUD/modal/stripe pink) — was gold, off vs the cool palette
        private static readonly Color PIP_EMPTY = new Color(0.30f, 0.36f, 0.46f, 0.85f); // dim = used
        private Image[]         _editPips;
        private RectTransform[] _editPipRects;
        private GameObject      _editPanelSlot;
        private CanvasGroup     _editPanelGroup; // dims the whole panel when out of edits
        private TextMeshProUGUI _editAdHint;     // "▶ FREE +3" shown when out + a rewarded ad is offered
        private Tween           _editPulseTween; // gentle pulse inviting the ad tap
        private bool            _swapAdInFlight; // guards double-tap while a rewarded ad is showing
        private int             _lastPipCount = -1;

        private GameObject[]      _slotButtons;
        private Image[]           _slotImages;

        // ── Tap-to-swap mode (full-screen dim with X over bag) ──
        private Canvas     _swapModeCanvas;       // overlay scrim + X
        private GameObject _swapModeScrim;        // full-screen dim Image
        private GameObject _swapModeXButton;      // X positioned over the bag slot
        public  bool       TileBagSwapModeActive { get; private set; }

        // ── Booster aim-mode scrim ──
        // 2026-06-01: cutout scrim — two Image rectangles covering the area
        // ABOVE the board (HUD + gap where "TAP A TILE" sits) and BELOW the
        // board (hand row + booster bench). The board area is left UNCOVERED
        // so tiles and the board panel render at their natural sortingOrder
        // without needing per-tile bumps (which fought Tile.cs's internal
        // SetSortingOrder(5) calls during animations).
        private Canvas     _aimScrimCanvas;
        private GameObject _aimScrimTop;     // above the board
        private GameObject _aimScrimBottom;  // below the board
        private GameObject _aimScrimLeft;    // left of the board panel
        private GameObject _aimScrimRight;   // right of the board panel
        private bool       _wasAim;
        private const int  AIM_SCRIM_SORT_ORDER = 15;  // above hand cards (10) and bench (8), below dragged card (20)
        private const int  ARMED_SLOT_SORT_ORDER = 20; // armed booster slot Canvas.overrideSorting target — above scrim

        // Board region in normalized canvas anchor coords (bottom-left origin).
        // Calibrated from PSD spec: board X=44.5 W=1110 (so right=1154.5),
        // Y=446 H=1420 (so bottom=1866) on a 1179×2556 canvas.
        //   left   X PSD 44.5   / 1179 = 0.0377
        //   right  X PSD 1154.5 / 1179 = 0.979
        //   top    Y → 1 - (446/2556)  = 0.826
        //   bottom Y → 1 - (1866/2556) = 0.270
        private const float BOARD_LEFT_ANCHOR_X   = 0.0377f;
        private const float BOARD_RIGHT_ANCHOR_X  = 0.979f;
        private const float BOARD_TOP_ANCHOR_Y    = 0.826f;
        private const float BOARD_BOTTOM_ANCHOR_Y = 0.270f;

        /// <summary>Hit-test a screen-space point against the TileBag slot's
        /// rect. Used by HandManager's drag-release path to detect when the
        /// player drops a hand card onto the bag for a tile-swap. The legacy
        /// world-space bag (HandManager._tileBagButton) was retired in the
        /// Path A HUD rework, so drag-release now has to query this
        /// screen-space slot instead.</summary>
        public bool IsScreenPointOverTileBag(Vector2 screenPoint)
        {
            if (_slotButtons == null) return false;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
            {
                if (DISPLAY_ORDER[i].Type != SlotType.TileBag) continue;
                if (_slotButtons[i] == null) return false;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                if (rt == null) return false;
                // 2026-06-01: pass the canvas's render camera now that the
                // canvas is in ScreenSpaceCamera mode (was Overlay → null was
                // correct previously). RectangleContainsScreenPoint needs the
                // camera for Camera/WorldSpace canvases to map screen point to
                // local rect correctly.
                Camera cam = _slotCanvas != null ? _slotCanvas.worldCamera : null;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, cam);
            }
            return false;
        }
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
            if (MatchController.Instance != null)
                MatchController.Instance.OnSwapUsed += OnSwapUsed_RefreshBadge;
        }

        private void OnDisable()
        {
            if (BoosterManager.Instance != null)
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
            if (MatchController.Instance != null)
                MatchController.Instance.OnSwapUsed -= OnSwapUsed_RefreshBadge;
        }

        private void Start()
        {
            if (BoosterManager.Instance != null)
            {
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
                BoosterManager.Instance.OnStateChanged += RefreshDisplay;
            }
            // MatchController may not have existed at OnEnable time (it's
            // created on demand). Re-bind here defensively.
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnSwapUsed -= OnSwapUsed_RefreshBadge;
                MatchController.Instance.OnSwapUsed += OnSwapUsed_RefreshBadge;
            }
            RefreshDisplay();
        }

        /// <summary>Fires when MatchController.UseSwap decrements the swap
        /// counter. Forces the TileBag chip badge to re-read the live value.</summary>
        private void OnSwapUsed_RefreshBadge(SwapUsedEvent evt)
        {
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
                RefreshEditPips(editCount); // update + animate the EDITS pips (glow burst on gain)
            }

            // Onboarding locks can flip between levels — rebuild the slots when they change so the
            // padlock overlays appear/disappear correctly (the overlay is built in BuildSlotCanvas). 2026-06-25.
            int lockHash = (TutorialLocks.EditLocked ? 1 : 0)
                         | (TutorialLocks.BagLocked ? 2 : 0)
                         | (TutorialLocks.BoostersLocked ? 4 : 0);
            if (lockHash != _lastLockHash)
            {
                _lastLockHash = lockHash;
                RebuildSlots();
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
                Debug.Log($"[BoosterDbg] AimTap screen=({screenPos.x:0},{screenPos.y:0}) " +
                          $"world=({worldPos.x:0.00},{worldPos.y:0.00}) → cell=({col},{row})");
                BoosterManager.Instance.ResolveAim(col, row);
            }
            else
            {
                Debug.Log($"[BoosterDbg] AimTap screen=({screenPos.x:0},{screenPos.y:0}) " +
                          $"world=({worldPos.x:0.00},{worldPos.y:0.00}) → WorldToCell FAILED");
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

        private int _lastLockHash = -1; // tracks TutorialLocks state so a change triggers a rebuild

        /// <summary>Tear down + rebuild the slot canvas so the padlock overlays match the current
        /// TutorialLocks state (the overlay is created in BuildSlotCanvas). Called on a lock change.</summary>
        public void RebuildSlots()
        {
            if (_slotCanvas != null) Destroy(_slotCanvas.gameObject);
            _slotCanvas = null;
            BuildSlotCanvas();
            RefreshDisplay();
        }

        private void BuildSlotCanvas()
        {
            var canvasGO = new GameObject("BoosterHUDSlotCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _slotCanvas = canvasGO.GetComponent<Canvas>();
            // 2026-06-01: switched from ScreenSpaceOverlay to ScreenSpaceCamera
            // so the canvas's sortingOrder can compete with world-space
            // SpriteRenderers. Overlay always renders above the scene
            // regardless of order, which left the dragged hand card hidden
            // behind the bag slot during a drag-to-swap. Camera mode + a low
            // sortingOrder lets the card (sortingOrder=20 while dragged via
            // BoostCardSortOrder) draw above the bag while still keeping the
            // bag above the board tiles (sortingOrder=5-6).
            _slotCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _slotCanvas.worldCamera = Camera.main;
            _slotCanvas.planeDistance = 2f;
            _slotCanvas.sortingOrder = 8;

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
            benchImg.color  = new Color(0.10f, 0.27f, 0.50f, 0.92f); // 2026-06-02: deep ocean blue (was garden-purple) — candy-palette chrome
            benchImg.raycastTarget = false;

            int slotCount = DISPLAY_ORDER.Length;
            _slotButtons           = new GameObject[slotCount];
            _slotImages            = new Image[slotCount];
            _slotLabels            = new TextMeshProUGUI[slotCount];
            _chargeBadges          = new TextMeshProUGUI[slotCount];
            _chargeBadgeContainers = new GameObject[slotCount];

            var displayFont = GameFont.GetDisplayTMP();

            int benchIdx = 0; // counts only the bench-row slots (boosters + Settings)
            for (int i = 0; i < slotCount; i++)
            {
                int slotIndex = i;
                var spec = DISPLAY_ORDER[i];

                // Edit is the wide "EDITS" pip panel centred under the holder; everything else
                // sits in the evenly spaced bench row along the bottom (full size).
                Vector2 anchored;
                float slotSize;     // square size for round slots
                Vector2 slotDim;    // actual RectTransform sizeDelta
                if (spec.Type == SlotType.Edit)
                {
                    slotSize = FLANK_SIZE;
                    slotDim  = new Vector2(EDIT_PANEL_W, EDIT_PANEL_H);
                    anchored = PsdAnchoredCenter(CANVAS_W * 0.5f - EDIT_PANEL_W * 0.5f, FLANK_Y_PSD, EDIT_PANEL_W, EDIT_PANEL_H);
                }
                else if (spec.Type == SlotType.TileBag)
                {
                    slotSize = FLANK_SIZE;
                    slotDim  = new Vector2(FLANK_SIZE, FLANK_SIZE);
                    anchored = PsdAnchoredCenter(BAG_X_PSD, FLANK_Y_PSD, FLANK_SIZE, FLANK_SIZE);
                }
                else
                {
                    slotSize = SLOT_SIZE;
                    slotDim  = new Vector2(SLOT_SIZE, SLOT_SIZE);
                    float xPsd = SLOT_LEFT_X + benchIdx * SLOT_STEP_BENCH;
                    anchored = PsdAnchoredCenter(xPsd, SLOT_TOP_Y, SLOT_SIZE, SLOT_SIZE);
                    benchIdx++;
                }

                var slot = new GameObject($"Slot_{i}_{spec.Type}",
                    typeof(RectTransform), typeof(Image), typeof(Button));
                slot.transform.SetParent(canvasGO.transform, false);
                _slotButtons[i] = slot;

                var rt = slot.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = slotDim;
                rt.anchoredPosition = anchored;

                _slotImages[i] = slot.GetComponent<Image>();
                // Edit + Bag are rounded-SQUARE tools; boosters/Settings stay round.
                if (spec.Type == SlotType.Edit || spec.Type == SlotType.TileBag)
                {
                    _slotImages[i].sprite = MenuUI.GetRoundedRectSprite(34);
                    _slotImages[i].type   = Image.Type.Sliced;
                }
                else
                {
                    _slotImages[i].sprite = GetCircleSpriteBig();
                }
                _slotImages[i].color  = SlotBaseColor(spec.Type);

                var slotBtn = slot.GetComponent<Button>();
                slotBtn.onClick.AddListener(() => OnSlotTapped(slotIndex));

                // 2026-06-01: override Unity's default disabled-state tint
                // (which dims the button to ~50% alpha) so the Edit slot —
                // which we set interactable=false on per Spencer — doesn't
                // look "see through" relative to the active booster slots.
                // disabledColor == normalColor means there's no visual
                // difference between enabled and disabled states.
                var cb = slotBtn.colors;
                cb.normalColor      = Color.white;
                cb.highlightedColor = Color.white;
                cb.pressedColor     = new Color(0.85f, 0.85f, 0.85f, 1f);
                cb.selectedColor    = Color.white;
                cb.disabledColor    = Color.white;
                cb.colorMultiplier  = 1f;
                slotBtn.colors = cb;

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
                else if (spec.Type == SlotType.Booster)
                {
                    // Booster slots get a PointerDown handler that fires the
                    // multi-pop press half ONLY when this slot is currently
                    // styled as the aim-cancel button (red ✕). Otherwise the
                    // PointerDown is a no-op and the slot's onClick handler
                    // plays PlaySettingsRelease via OnSlotTapped's top branch.
                    var trigger = slot.GetComponent<EventTrigger>();
                    if (trigger == null) trigger = slot.AddComponent<EventTrigger>();
                    int capturedIndex = slotIndex;
                    var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                    pdEntry.callback.AddListener((_) =>
                    {
                        var bm = BoosterManager.Instance;
                        if (bm == null || !bm.AimMode) return;
                        if (capturedIndex < 0 || capturedIndex >= DISPLAY_ORDER.Length) return;
                        var s = DISPLAY_ORDER[capturedIndex];
                        if (s.Type != SlotType.Booster) return;
                        if (bm.ArmedBooster == null || s.BoosterId != bm.ArmedBooster.Id) return;
                        GameAudio.Instance?.PlayMultiPopPress();
                    });
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
                    iconRT.sizeDelta = new Vector2(slotSize * 0.70f, slotSize * 0.78f);
                    iconRT.anchoredPosition = Vector2.zero;
                    _slotLabels[i] = null;
                }
                else if (spec.Type == SlotType.Edit)
                {
                    // EDITS pip panel — "EDITS" label + 3 gold pips (charge readout). No letter, no badge.
                    BuildEditsPanel(slot, displayFont);
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

                // Charge badge — boosters and TileBag have one. Edit now uses the EDITS pip
                // panel instead of a numeric badge; Settings has none.
                bool needsBadge = spec.Type == SlotType.Booster
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
                    badgeImg.color  = new Color(0.20f, 0.58f, 0.14f, 1f); // 2026-06-24 Spencer: darker green than SCORED_TINT so the white number reads (bright green washed it out)
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

                // ── Onboarding lock overlay ──────────────────────────────────────
                // Locked tools (Edit/Bag/boosters in early levels) show a padlock and
                // ignore taps. Added last so it renders above the icon/badge. 2026-06-09.
                if (IsSlotLocked(spec))
                {
                    slotBtn.interactable = false;
                    // Locked tools show ONLY the lock — hide the icon/letter/badge beneath it.
                    for (int ch = slot.transform.childCount - 1; ch >= 0; ch--)
                        slot.transform.GetChild(ch).gameObject.SetActive(false);

                    var lockGO = new GameObject("LockOverlay", typeof(RectTransform), typeof(Image));
                    lockGO.transform.SetParent(slot.transform, false);
                    var lockRT = lockGO.GetComponent<RectTransform>();
                    lockRT.anchorMin = lockRT.anchorMax = new Vector2(0.5f, 0.5f);
                    lockRT.pivot     = new Vector2(0.5f, 0.5f);
                    lockRT.sizeDelta = new Vector2(SLOT_SIZE * 0.62f, SLOT_SIZE * 0.62f);
                    lockRT.anchoredPosition = Vector2.zero;
                    var lockImg = lockGO.GetComponent<Image>();
                    lockImg.sprite         = GetLockSprite();
                    lockImg.preserveAspect = true;
                    lockImg.raycastTarget  = false;
                }
            }
        }

        private static Color SlotBaseColor(SlotType type)
        {
            switch (type)
            {
                case SlotType.Edit:     return new Color(0.10f, 0.27f, 0.50f, 0.95f); // navy panel (matches bench)
                case SlotType.TileBag:  return new Color(0.18f, 0.25f, 0.20f, 0.92f);
                case SlotType.Settings: return new Color(0.20f, 0.18f, 0.22f, 0.92f);
                default:                return new Color(0.18f, 0.12f, 0.28f, 0.92f);
            }
        }

        /// <summary>Builds the "EDITS" pip panel inside the Edit slot: a title label + a row of
        /// EDIT_PIP_COUNT gold pips (charge readout). Pips fill/empty with the rewrite count and
        /// pop with an additive glow when an edit is RESTORED. 2026-06-24 Spencer.</summary>
        private void BuildEditsPanel(GameObject slot, TMP_FontAsset font)
        {
            _editPanelSlot = slot;
            _lastPipCount  = -1; // first RefreshEditPips just sets state, no animation
            _editPanelGroup = slot.GetComponent<CanvasGroup>() ?? slot.AddComponent<CanvasGroup>();

            // "EDITS" title (upper portion).
            var lblGO = new GameObject("EditsLabel", typeof(RectTransform));
            lblGO.transform.SetParent(slot.transform, false);
            var lbl = lblGO.AddComponent<TextMeshProUGUI>();
            var uiFont = GameFont.GetUITMP();   // 2026-06-24: match the HUD font (Montserrat-Black), not the Cartoon display font
            if (uiFont != null) lbl.font = uiFont;
            lbl.text = "SWAPS"; // 2026-06-24: edits renamed "swaps" for players (tile-bag swap was cut)
            lbl.fontSize = 24;
            lbl.fontStyle = TMPro.FontStyles.Bold;                       // match the NEXT label (bold)
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.color = new Color(0.78f, 0.78f, 0.88f, 0.95f);          // match the NEXT label colour
            lbl.raycastTarget = false;
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0.05f, 0.50f); lrt.anchorMax = new Vector2(0.95f, 0.94f);
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;

            // Pip row (lower portion).
            _editPips     = new Image[EDIT_PIP_COUNT];
            _editPipRects = new RectTransform[EDIT_PIP_COUNT];
            const float pipW = 0.26f, gap = 0.06f;
            float totalW = EDIT_PIP_COUNT * pipW + (EDIT_PIP_COUNT - 1) * gap;
            float startX = (1f - totalW) * 0.5f;
            for (int p = 0; p < EDIT_PIP_COUNT; p++)
            {
                var pipGO = new GameObject($"Pip{p}", typeof(RectTransform), typeof(Image));
                pipGO.transform.SetParent(slot.transform, false);
                var prt = pipGO.GetComponent<RectTransform>();
                float x0 = startX + p * (pipW + gap);
                prt.anchorMin = new Vector2(x0, 0.12f);
                prt.anchorMax = new Vector2(x0 + pipW, 0.42f);
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
                var pimg = pipGO.GetComponent<Image>();
                pimg.sprite = MenuUI.GetRoundedRectSprite(16);
                pimg.type   = Image.Type.Sliced;
                pimg.color  = PIP_EMPTY;
                pimg.raycastTarget = false;
                _editPips[p]     = pimg;
                _editPipRects[p] = prt;
            }

            // "watch ad for swaps" hint — overlays the pip row, shown only when out of swaps
            // AND a rewarded ad is on offer (see RefreshEditPips). Green = free/positive.
            var hintGO = new GameObject("AdHint", typeof(RectTransform));
            hintGO.transform.SetParent(slot.transform, false);
            _editAdHint = hintGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _editAdHint.font = font;
            _editAdHint.text = "▶ FREE +3";
            _editAdHint.fontSize = 28;
            _editAdHint.alignment = TextAlignmentOptions.Center;
            _editAdHint.color = new Color(0.45f, 1f, 0.45f, 1f);
            _editAdHint.raycastTarget = false;
            var hrt = _editAdHint.rectTransform;
            hrt.anchorMin = new Vector2(0.04f, 0.08f); hrt.anchorMax = new Vector2(0.96f, 0.46f);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            hintGO.SetActive(false);
        }

        /// <summary>Refreshes the EDITS pips to reflect the current rewrite count. Newly-FILLED pips
        /// (an edit was restored) pop with an additive gold glow burst.</summary>
        private void RefreshEditPips(int count)
        {
            if (_editPips == null) return;

            // Locked (early tutorial levels) → show ONLY the padlock. Hide the pips + ad hint and bail:
            // RefreshEditPips runs every frame and would otherwise re-activate the pips, leaving the pink
            // charges visible ON TOP of the lock (inconsistent with how L1–L3 look). 2026-07-06 Spencer.
            if (TutorialLocks.EditLocked)
            {
                for (int p = 0; p < _editPips.Length; p++)
                    if (_editPips[p] != null) _editPips[p].gameObject.SetActive(false);
                if (_editAdHint != null) _editAdHint.gameObject.SetActive(false);
                return;
            }

            // Out of swaps AND a rewarded ad is on offer → show the "watch ad" affordance instead of dim.
            bool adOffer = count <= 0 && IsSwapAdEligible();

            for (int p = 0; p < _editPips.Length; p++)
            {
                bool filled    = p < count;
                bool wasFilled = p < _lastPipCount;
                if (_editPips[p] != null)
                {
                    _editPips[p].gameObject.SetActive(!adOffer); // hide pips behind the ad hint
                    _editPips[p].color = filled ? PIP_FULL : PIP_EMPTY;
                }
                if (filled && !wasFilled && _lastPipCount >= 0)
                    AnimateEditPipGain(p); // restored this pip → celebrate (green glow)
            }
            _lastPipCount = count;

            if (_editAdHint != null) _editAdHint.gameObject.SetActive(adOffer);

            // Panel alpha: bright when you have swaps OR when the ad offer is live (inviting);
            // dimmed only when you're out AND no ad is available.
            if (_editPanelGroup != null)
            {
                _editPanelGroup.DOKill();
                _editPanelGroup.DOFade(count > 0 || adOffer ? 1f : 0.45f, 0.2f);
            }

            // Gentle invite pulse while the ad offer is live; resting otherwise.
            if (_editPanelSlot != null)
            {
                _editPulseTween?.Kill();
                var t = _editPanelSlot.transform;
                if (adOffer)
                {
                    t.localScale = Vector3.one;
                    _editPulseTween = t.DOScale(1.06f, 0.6f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
                }
                else t.localScale = Vector3.one;
            }
        }

        /// <summary>True when the player is out of swaps, swaps are unlocked (taught), and a rewarded
        /// ad is available — i.e. we can offer "watch an ad to refill swaps". 2026-06-24 Spencer.</summary>
        private bool IsSwapAdEligible()
        {
            if (TutorialLocks.EditLocked) return false;     // not during onboarding (before swaps are taught)
            if (AdManager.Instance == null) return false;   // no ad system available
            int swaps = MatchController.Instance != null
                ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN) : 0;
            return swaps <= 0;
        }

        /// <summary>Opt-in rewarded ad → refill swaps to the survival cap. Self-limits: refilling takes
        /// you off 0, so the offer only reappears after you've spent them again. 2026-06-24 Spencer.</summary>
        private void OfferSwapAd()
        {
            if (_swapAdInFlight || !IsSwapAdEligible()) return;
            if (AdManager.Instance == null || MatchController.Instance == null) return;
            _swapAdInFlight = true;
            AdManager.Instance.ShowRewardedAd(onRewardGranted: () =>
            {
                // RefundRewriteCharge self-caps at the survival cap (3); call to-cap.
                for (int k = 0; k < EDIT_PIP_COUNT; k++)
                    MatchController.Instance.RefundRewriteCharge(MatchController.PLAYER_HUMAN);
                GameAudio.Instance?.PlayPersonalBest();
                _swapAdInFlight = false; // a real async SDK should also reset on ad-failed
            });
        }

        /// <summary>Pop + additive-glow burst on a pip that just got restored.</summary>
        private void AnimateEditPipGain(int p)
        {
            if (_editPipRects == null || p < 0 || p >= _editPipRects.Length) return;
            var rt = _editPipRects[p];
            if (rt == null) return;
            rt.DOKill();
            rt.localScale = Vector3.one * 0.45f;
            rt.DOScale(1f, 0.34f).SetEase(Ease.OutBack, 3.2f);
            SpawnEditPipGlow(rt);
        }

        /// <summary>Fake-bloom: an additive gold glow behind the pip that expands and fades out.
        /// Over the dark navy panel the additive blend reads as a real glow. Self-destroys.</summary>
        private void SpawnEditPipGlow(RectTransform pip)
        {
            if (_editPanelSlot == null || pip == null) return;
            Sprite glow = LoadGlowSprite();
            if (glow == null) return;
            var gGO = new GameObject("PipGlow", typeof(RectTransform), typeof(Image));
            gGO.transform.SetParent(_editPanelSlot.transform, false);
            gGO.transform.SetSiblingIndex(0); // behind the pips
            var grt = (RectTransform)gGO.transform;
            grt.anchorMin = pip.anchorMin; grt.anchorMax = pip.anchorMax;
            grt.offsetMin = new Vector2(-34f, -34f); grt.offsetMax = new Vector2(34f, 34f); // spill past the pip
            var gimg = gGO.GetComponent<Image>();
            gimg.sprite = glow;
            gimg.color  = new Color(0.40f, 1f, 0.35f, 0.95f); // green = "you gained an edit"
            gimg.raycastTarget = false;
            var addMat = LoadAdditiveGlowMaterial();
            if (addMat != null) gimg.material = addMat;
            grt.localScale = Vector3.one * 0.5f;
            var sq = DOTween.Sequence();
            sq.Append(grt.DOScale(1.7f, 0.48f).SetEase(Ease.OutCubic));
            sq.Join(gimg.DOFade(0f, 0.48f).SetEase(Ease.InQuad));
            sq.OnComplete(() => { if (gGO != null) Destroy(gGO); });
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

            // 2026-06-01: dark blue background panel removed per Spencer.
            // Banner rect stays as the layout container (so the centered text
            // child still positions correctly), but its Image is transparent
            // and the anchoredPosition is pushed down so the text sits near
            // the same Y as where the +WORDS / scored-word popup renders —
            // roughly the gap between the HUD bar and the top of the board.
            var bannerGO = new GameObject("Banner",
                typeof(RectTransform), typeof(Image));
            bannerGO.transform.SetParent(canvasGO.transform, false);
            var bannerRT = bannerGO.GetComponent<RectTransform>();
            bannerRT.anchorMin = new Vector2(0f, 1f);
            bannerRT.anchorMax = new Vector2(1f, 1f);
            bannerRT.pivot     = new Vector2(0.5f, 1f);
            bannerRT.sizeDelta = new Vector2(0f, 220f);
            bannerRT.anchoredPosition = new Vector2(0f, -200f); // centers the text in the gap between the HUD bar (~y=180 PSD) and the top of the board panel (~y=446 PSD) — Spencer 2026-06-01
            bannerGO.GetComponent<Image>().color = Color.clear;
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

            // 2026-06-01: banner cancel button removed per Spencer. Cancel now
            // happens by tapping the armed booster's slot directly (which
            // RefreshDisplay re-styles as a red ✕ during aim mode), matching
            // the tile-bag swap-mode UX. _aimCancelButton stays null —
            // _aimTapHandler's rectangle-contains check guards against null.

            _aimCanvas.gameObject.SetActive(false);
        }

        // ── Tap-to-swap overlay (scrim + X over bag) ─────────────────────────────

        /// <summary>Build the swap-mode canvas lazily. ScreenSpaceCamera mode
        /// at sortingOrder=9 sits ABOVE the booster bench (sortingOrder=8) but
        /// BELOW the hand cards (sortingOrder=10) so the cards stay bright
        /// against the dim scrim. HUD/modal canvases stay above the scrim
        /// because they're ScreenSpaceOverlay (always renders over scene).</summary>
        private void BuildSwapModeCanvas()
        {
            if (_swapModeCanvas != null) return;

            var canvasGO = new GameObject("TileBagSwapModeCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _swapModeCanvas = canvasGO.GetComponent<Canvas>();
            _swapModeCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _swapModeCanvas.worldCamera = Camera.main;
            _swapModeCanvas.planeDistance = 2f;
            _swapModeCanvas.sortingOrder = 9;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1f;

            // Full-screen scrim image. raycastTarget=true so taps on the dim
            // area are absorbed (don't accidentally reach the board behind).
            _swapModeScrim = new GameObject("Scrim", typeof(RectTransform), typeof(Image));
            _swapModeScrim.transform.SetParent(canvasGO.transform, false);
            var scrimRT = _swapModeScrim.GetComponent<RectTransform>();
            scrimRT.anchorMin = Vector2.zero;
            scrimRT.anchorMax = Vector2.one;
            scrimRT.offsetMin = Vector2.zero;
            scrimRT.offsetMax = Vector2.zero;
            var scrimImg = _swapModeScrim.GetComponent<Image>();
            scrimImg.color = new Color(0f, 0f, 0f, 0.72f);
            scrimImg.raycastTarget = true;

            // X button positioned over the TileBag slot. Anchored from the
            // bottom of the screen to match the bag slot's anchor scheme so
            // it sits exactly where the bag is.
            _swapModeXButton = new GameObject("CancelX",
                typeof(RectTransform), typeof(Image), typeof(Button));
            _swapModeXButton.transform.SetParent(canvasGO.transform, false);
            var xRT = _swapModeXButton.GetComponent<RectTransform>();
            xRT.anchorMin = new Vector2(0.5f, 0f);
            xRT.anchorMax = new Vector2(0.5f, 0f);
            xRT.pivot     = new Vector2(0.5f, 0.5f);
            xRT.sizeDelta = new Vector2(FLANK_SIZE, FLANK_SIZE); // matches the (smaller) bag slot it overlays
            // Line the X up over the TileBag slot's ACTUAL position (the bag now flanks
            // the tile holder rather than sitting in the bench row). Read the built slot's
            // anchoredPosition directly; fall back to the computed flank position.
            int bagIndex = 0;
            for (int i = 0; i < DISPLAY_ORDER.Length; i++)
                if (DISPLAY_ORDER[i].Type == SlotType.TileBag) { bagIndex = i; break; }
            Vector2 bagAnchored = (_slotButtons != null && bagIndex < _slotButtons.Length && _slotButtons[bagIndex] != null)
                ? _slotButtons[bagIndex].GetComponent<RectTransform>().anchoredPosition
                : PsdAnchoredCenter(BAG_X_PSD, FLANK_Y_PSD, SLOT_SIZE, SLOT_SIZE);
            xRT.anchoredPosition = bagAnchored;

            var xImg = _swapModeXButton.GetComponent<Image>();
            xImg.sprite = GetCircleSpriteBig();
            xImg.color  = new Color(0.50f, 0.18f, 0.18f, 0.95f); // muted red

            // X glyph on top of the button
            var xGlyphGO = new GameObject("Glyph", typeof(RectTransform));
            xGlyphGO.transform.SetParent(_swapModeXButton.transform, false);
            var xGlyphRT = xGlyphGO.GetComponent<RectTransform>();
            xGlyphRT.anchorMin = Vector2.zero;
            xGlyphRT.anchorMax = Vector2.one;
            xGlyphRT.offsetMin = Vector2.zero;
            xGlyphRT.offsetMax = Vector2.zero;
            var xText = xGlyphGO.AddComponent<TextMeshProUGUI>();
            xText.text = "✕";
            xText.alignment = TextAlignmentOptions.Center;
            xText.fontSize = 56;
            xText.color = Color.white;
            xText.raycastTarget = false;
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) xText.font = displayFont;

            // 2026-06-01: same split multi-pop SFX as the SettingsModal close
            // button (SettingsModal.BuildCloseButton). PointerDown plays the
            // press half, onClick plays the release half. Spencer asked for
            // the swap-mode X to "have the exact sound" as the settings X.
            var xTrigger = _swapModeXButton.GetComponent<EventTrigger>();
            if (xTrigger == null) xTrigger = _swapModeXButton.AddComponent<EventTrigger>();
            var xDownEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            xDownEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            xTrigger.triggers.Add(xDownEntry);

            _swapModeXButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                ExitTileBagSwapMode();
            });

            _swapModeCanvas.gameObject.SetActive(false);
        }

        // ── Booster aim-mode scrim helpers ──────────────────────────────────────

        /// <summary>Build the aim-mode scrim canvas lazily. Two Image
        /// rectangles cover the area ABOVE the board and BELOW the board,
        /// leaving a CUTOUT for the board itself. The board panel + tiles
        /// stay at their natural sortingOrder and are simply not covered by
        /// the scrim — which avoids fighting Tile.cs's internal SetSortingOrder
        /// resets during animations. raycastTarget=false on both rects so
        /// taps pass through to BoosterHUDSlot.Update's aim-tap handler.</summary>
        private void BuildAimScrimCanvas()
        {
            if (_aimScrimCanvas != null) return;

            var canvasGO = new GameObject("BoosterAimScrimCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _aimScrimCanvas = canvasGO.GetComponent<Canvas>();
            _aimScrimCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _aimScrimCanvas.worldCamera = Camera.main;
            _aimScrimCanvas.planeDistance = 2f;
            _aimScrimCanvas.sortingOrder = AIM_SCRIM_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1f;

            Color scrimColor = new Color(0f, 0f, 0f, 0.72f);

            // Four-rectangle frame around the board. Anchors are placeholders
            // at build time — the actual cutout is computed from the board
            // background SR's world bounds in UpdateScrimGeometry, called on
            // every aim-mode entry. This way the cutout always matches the
            // ACTUAL rendered board panel, regardless of PSD vs runtime drift.
            _aimScrimTop    = MakeScrimRect(canvasGO.transform, "ScrimTop",    Vector2.zero, Vector2.one, scrimColor);
            _aimScrimBottom = MakeScrimRect(canvasGO.transform, "ScrimBottom", Vector2.zero, Vector2.one, scrimColor);
            _aimScrimLeft   = MakeScrimRect(canvasGO.transform, "ScrimLeft",   Vector2.zero, Vector2.one, scrimColor);
            _aimScrimRight  = MakeScrimRect(canvasGO.transform, "ScrimRight",  Vector2.zero, Vector2.one, scrimColor);

            _aimScrimCanvas.gameObject.SetActive(false);
        }

        /// <summary>Snap the 4 scrim rects to frame the board's actual world
        /// bounds. Called when aim mode activates so the cutout precisely
        /// matches whatever the lavender board panel renders at — width,
        /// height, position — without hardcoded PSD constants.</summary>
        private void UpdateScrimGeometry()
        {
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid == null || cam == null) return;

            Bounds? b = grid.BoardBackgroundWorldBounds;
            if (!b.HasValue) return;

            // World-space corners → viewport (0-1) coords. For a ScreenSpaceCamera
            // canvas the viewport coords map directly to anchor coords (0,0 =
            // bottom-left of viewport, 1,1 = top-right). Orthographic camera
            // means the conversion is a clean affine map.
            Vector3 minVP = cam.WorldToViewportPoint(b.Value.min);
            Vector3 maxVP = cam.WorldToViewportPoint(b.Value.max);

            float left   = Mathf.Clamp01(minVP.x);
            float right  = Mathf.Clamp01(maxVP.x);
            float bottom = Mathf.Clamp01(minVP.y);
            float top    = Mathf.Clamp01(maxVP.y);

            SetRectAnchors(_aimScrimTop,    new Vector2(0f,    top),    new Vector2(1f,    1f));
            SetRectAnchors(_aimScrimBottom, new Vector2(0f,    0f),     new Vector2(1f,    bottom));
            SetRectAnchors(_aimScrimLeft,   new Vector2(0f,    bottom), new Vector2(left,  top));
            SetRectAnchors(_aimScrimRight,  new Vector2(right, bottom), new Vector2(1f,    top));
        }

        private static void SetRectAnchors(GameObject go, Vector2 anchorMin, Vector2 anchorMax)
        {
            if (go == null) return;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static GameObject MakeScrimRect(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // taps pass through to the aim handler
            return go;
        }

        /// <summary>Toggle the cutout scrim + armed-slot sort override.
        /// Called from RefreshDisplay when the cached _wasAim flag disagrees
        /// with the live state.
        ///
        /// Bright during aim (uncovered by scrim): the entire board area —
        /// background panel, tiles, board background sprites. No per-element
        /// sortingOrder manipulation required (which previously fought Tile.cs's
        /// internal SetSortingOrder resets during tile animations).
        /// Dim during aim (covered by scrim): HUD area, the "TAP A TILE"
        /// banner gap, hand row, NEXT preview, bench panel + un-armed slots.
        /// Armed booster slot uses Canvas.overrideSorting to stay bright on
        /// top of the bottom scrim so the red ✕ cancel reads.</summary>
        private void SetAimModeVisuals(bool active)
        {
            BuildAimScrimCanvas();
            // Full-screen scrim now; corners-of-board are handled by bumping the
            // board panel's rounded sprite ABOVE the scrim's sortingOrder, so
            // the panel itself naturally clips out only its rounded shape
            // (slivers stay dim with the scrim, panel area is bright).
            if (active) StretchScrimsFullScreen();
            if (_aimScrimCanvas != null) _aimScrimCanvas.gameObject.SetActive(active);

            // Bump board background + all tiles above the scrim so they
            // render bright inside the panel cutout. The board bg's rounded
            // sprite shape provides the perfect cutout for free — the corner
            // slivers (where the bg has 0 alpha) stay dim with the scrim.
            var grid = GridManager.Instance;
            if (grid != null)
            {
                grid.SetBoardBackgroundSortingOrder(active ? AIM_BOARD_BG_ORDER : 0);
                int tileOrder = active ? AIM_TILE_ORDER : 5;
                Tile.AimModeTileOrder = active ? AIM_TILE_ORDER : 0;
                for (int c = 0; c < RulesEngine.COLS; c++)
                {
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                    {
                        var tile = grid.GetTile(c, r);
                        if (tile != null) tile.SetSortingOrder(tileOrder);
                    }
                }
            }

            ApplyArmedSlotSortOverride(active);

            // The "TAP A TILE" banner sits in the same slot as the last-word
            // score readout ("TOE +7"); shown together they overlap and read as
            // jumbled text. Clear the score readout while aim mode is up — it
            // repopulates on its own the next time a word scores after aim exits.
            if (active) LastWordDisplay.Instance?.Clear();
        }

        private const int AIM_BOARD_BG_ORDER = 17; // above scrim (15), below tiles
        private const int AIM_TILE_ORDER     = 25; // well above scrim and board bg

        /// <summary>Set up the scrim as a single full-screen dim layer.
        /// Uses _aimScrimTop as the sole active rect; the other 3 (Bottom /
        /// Left / Right) are hidden so the 72% alpha doesn't stack 4×
        /// (which would compound to ~99% opacity and read as pure black).
        /// The cutout for the board area comes from bumping the board panel
        /// + tiles above the scrim's sortingOrder, not from anchor geometry.</summary>
        private void StretchScrimsFullScreen()
        {
            SetRectAnchors(_aimScrimTop, Vector2.zero, Vector2.one);
            if (_aimScrimBottom != null) _aimScrimBottom.SetActive(false);
            if (_aimScrimLeft   != null) _aimScrimLeft.SetActive(false);
            if (_aimScrimRight  != null) _aimScrimRight.SetActive(false);
        }

        /// <summary>Use Canvas.overrideSorting on the armed booster's slot
        /// GameObject so it renders above the aim scrim. On exit, disable
        /// the override and the slot reverts to the bench canvas's order.</summary>
        private void ApplyArmedSlotSortOverride(bool active)
        {
            if (_slotButtons == null) return;
            for (int i = 0; i < _slotButtons.Length && i < DISPLAY_ORDER.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var spec = DISPLAY_ORDER[i];
                if (spec.Type != SlotType.Booster) continue;

                var bm = BoosterManager.Instance;
                bool isArmed = active && bm != null && bm.ArmedBooster != null
                                       && spec.BoosterId == bm.ArmedBooster.Id;

                var subCanvas = _slotButtons[i].GetComponent<Canvas>();
                if (isArmed)
                {
                    if (subCanvas == null) subCanvas = _slotButtons[i].AddComponent<Canvas>();
                    subCanvas.overrideSorting = true;
                    subCanvas.sortingOrder = ARMED_SLOT_SORT_ORDER; // above the scrim's sortingOrder
                    // GraphicRaycaster needed so the Button still receives
                    // taps under the new sub-canvas. Idempotent: AddComponent
                    // returns the existing one if already present.
                    if (_slotButtons[i].GetComponent<GraphicRaycaster>() == null)
                        _slotButtons[i].AddComponent<GraphicRaycaster>();
                }
                else if (subCanvas != null)
                {
                    subCanvas.overrideSorting = false;
                }
            }
        }

        /// <summary>Enter tap-to-swap mode. Caller must verify swaps remaining > 0.
        /// Shows the dim scrim + X over the bag, and tells HandManager to
        /// start the card-pulse + listen-for-card-tap behaviour.</summary>
        public void EnterTileBagSwapMode()
        {
            if (TileBagSwapModeActive) return;
            BuildSwapModeCanvas();
            if (_swapModeCanvas != null) _swapModeCanvas.gameObject.SetActive(true);
            TileBagSwapModeActive = true;
            // 2026-06-01: PlayUIClick removed — the slot tap that invoked
            // this already fired PlaySettingsRelease in OnSlotTapped.
            HandManager.Instance?.EnterTapToSwapMode();
        }

        /// <summary>Exit tap-to-swap mode. Called by the X button, by
        /// HandManager after a successful swap, or by any external code path
        /// that wants to bail (e.g. modal-open guards).</summary>
        public void ExitTileBagSwapMode()
        {
            if (!TileBagSwapModeActive) return;
            TileBagSwapModeActive = false;
            if (_swapModeCanvas != null) _swapModeCanvas.gameObject.SetActive(false);
            HandManager.Instance?.ExitTapToSwapMode();
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
                        // 2026-06-01: source from MatchController's live swap
                        // counter so the badge actually decrements when a tile
                        // is swapped via drag-onto-bag. Was hardcoded to 2.
                        charges = MatchController.Instance != null
                            ? MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN)
                            : 0;
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
                    bool showBadge = (spec.Type == SlotType.Booster
                                  || spec.Type == SlotType.Edit
                                  || spec.Type == SlotType.TileBag)
                                  && !IsSlotLocked(spec); // locked tools show no charge badge
                    _chargeBadgeContainers[i].SetActive(showBadge);
                }

                var btn = _slotButtons[i].GetComponent<Button>();

                // 2026-06-01: Edit icon is non-interactable for now (Commit 3
                // wiring still pending — tapping it does nothing useful, so
                // Spencer wants it un-clickable to avoid implying functionality).
                bool isEditSlot   = spec.Type == SlotType.Edit;
                bool isArmedSlot  = isAim && spec.Type == SlotType.Booster
                                          && bm.ArmedBooster != null
                                          && spec.BoosterId == bm.ArmedBooster.Id;

                if (btn != null)
                {
                    if (IsSlotLocked(spec))         btn.interactable = false; // onboarding lock
                    else if (isEditSlot)            btn.interactable = IsSwapAdEligible(); // tappable only to offer the swap-refill ad
                    else if (isAim && !isArmedSlot) btn.interactable = false;
                    else                            btn.interactable = true;
                }

                // Visual: armed slot during aim mode becomes the cancel button —
                // red bg + ✕ glyph (same look as the tile-bag swap-mode X).
                // Every other booster slot dims to its base color and is
                // non-interactable.
                if (_slotImages[i] != null)
                {
                    _slotImages[i].color = isArmedSlot
                        ? new Color(0.55f, 0.18f, 0.18f, 1f)
                        : SlotBaseColor(spec.Type);
                }
                if (_slotLabels[i] != null && spec.Type == SlotType.Booster)
                {
                    if (isArmedSlot)
                        _slotLabels[i].text = "✕";
                    else if (slotActive)
                    {
                        var booster = bm != null ? bm.GetBoosterById(spec.BoosterId) : null;
                        if (booster != null) _slotLabels[i].text = booster.DisplayName.Substring(0, 1);
                    }
                }
                // Hide the charge badge on the armed slot so the ✕ reads cleanly.
                if (_chargeBadgeContainers[i] != null && isArmedSlot)
                    _chargeBadgeContainers[i].SetActive(false);
            }

            if (_aimCanvas != null) _aimCanvas.gameObject.SetActive(isAim);

            // Scrim + board-tile sort boost — toggled on aim-state transitions.
            // Tracked separately from per-slot state so the scrim's tile-sorting
            // sweep only fires on the actual enter/exit edge, not on every
            // OnStateChanged callback (which can fire mid-aim too, e.g. when
            // charges change).
            if (isAim != _wasAim)
            {
                SetAimModeVisuals(isAim);
                _wasAim = isAim;
            }
            // Safety net (2026-06-03 Spencer: the dim scrim stayed stuck after using
            // Stone Splitter). The edge-gate above can miss the exit edge if _wasAim
            // desyncs during a booster resolve+cascade, leaving the screen dark with
            // no way back. Force a full re-sync whenever the scrim's ACTUAL state
            // disagrees with the aim state — the dim can never get stuck on.
            else if (_aimScrimCanvas != null && _aimScrimCanvas.gameObject.activeSelf != isAim)
            {
                SetAimModeVisuals(isAim);
                _wasAim = isAim;
            }
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

            // Locked during early onboarding — ignore the tap (defensive; the button is also
            // interactable=false). 2026-06-09.
            if (IsSlotLocked(spec)) { GameAudio.Instance?.PlayInvalidDrop(); return; }

            // Aim-mode cancel: tapping the armed booster's slot (now styled
            // as a red ✕) cancels aim. Same multi-pop release sound as the
            // tile-bag swap-mode X (press half fires on PointerDown — see
            // EventTrigger wired in BuildSlotCanvas).
            var bm = BoosterManager.Instance;
            if (bm != null && bm.AimMode && spec.Type == SlotType.Booster
                && bm.ArmedBooster != null && spec.BoosterId == bm.ArmedBooster.Id)
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                bm.CancelAim();
                return;
            }

            // 2026-06-01: every non-Settings slot click plays the same
            // settings-tab release sound per Spencer ("the sound played in the
            // settings menu when you click between buttons"). Settings has its
            // own paired press+release (a fuller 2-stage feel) wired below.
            if (spec.Type != SlotType.Settings)
                GameAudio.Instance?.PlaySettingsRelease();

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
                    // The SWAPS panel is a readout (editing is via tapping a board tile). The only tap
                    // it accepts is the out-of-swaps rewarded-ad refill offer.
                    if (IsSwapAdEligible())
                        OfferSwapAd();
                    break;
                }
                case SlotType.TileBag:
                {
                    // Tap-to-swap mode: dim everything except the hand cards,
                    // replace the bag with an X. Tapping a card swaps it;
                    // tapping the X cancels without consuming a swap.
                    int swapsLeft = MatchController.Instance != null
                        ? MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN)
                        : 0;
                    if (swapsLeft <= 0)
                    {
                        OnBuyMoreTapped(spec);
                        break;
                    }
                    EnterTileBagSwapMode();
                    suppressClick = true; // EnterTileBagSwapMode handles its own SFX
                    break;
                }
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

            // 2026-06-01: dropped the bottom PlayUIClick fallback — the
            // settings-tab release sound is now fired at the top of this
            // method for every non-Settings slot, so there's no need for a
            // second click sound here. suppressClick is left in place in case
            // future case branches need to opt out of the top sound.
            _ = suppressClick;
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
