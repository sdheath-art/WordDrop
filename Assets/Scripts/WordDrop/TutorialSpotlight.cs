using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Onboarding focus overlay (2026-06-25) — copies the booster aim-scrim exactly.
    ///
    /// The booster scrim "nails" the board dim by a ScreenSpaceCamera full-screen scrim with the
    /// rounded board sprite + tiles BUMPED above it (so the board's own shape is the bright cutout —
    /// perfect rounded corners, no texture hacks). We reuse that verbatim (SetBoardBackgroundSortingOrder
    /// + Tile.AimModeTileOrder). The tile holder sits below the board and is simply left under the scrim,
    /// so it dims for free. The ONE thing the camera scrim can't reach is the HUD (a ScreenSpaceOverlay
    /// canvas that always draws above ScreenSpaceCamera) — so we add a separate Overlay dim over the HUD
    /// bar, plus the hand-point cursor on that same top layer so it stays visible over the bright board.
    /// </summary>
    public class TutorialSpotlight : MonoBehaviour
    {
        public static TutorialSpotlight Instance { get; private set; }

        // Matched to BoosterHUDSlot so the board cutout is identical.
        // 2026-10-20: ALL RE-BASED ABOVE THE ROW BANDS. Since 2026-09-03 tiles sort by row
        // (Tile.BaseSortingOrder = 5 + row*3, face..letter = up to 37 on the top row). These were still the
        // pre-band values (scrim 9), so rows 0-1 dimmed and every row from 2 up drew OVER the scrim — Spencer's
        // "the whole board should be dimmed besides those top two rows but the middle of the board isnt", and
        // the dim on rows 0-1 reading as "blue writing" over the letters. Same cause as the invisible ice.
        // The booster aim mode was re-based on 09-03 (AIM_TILE_ORDER 70); the tutorial spotlight was missed.
        private const int SCRIM_ORDER    = 40;   // above every row band (max 37)
        private const int SPOTLIGHT_TILE_ORDER = 42; // spotlit tiles: LiftedOrder adds row*3 -> 42..72, all above the scrim
        private const int BOARD_BG_ORDER = 41;   // full-board spotlight: board panel above the scrim...
        private const int TILE_ORDER     = 43;   // ...and its tiles above that (band 43..73)
        private const int OVERLAY_ORDER  = 150;  // HUD dim + cursor — above the HUD canvas (50)
        // 2026-10-23 Spencer: "dims out everything in the same style and look as royal match". MEASURED from his RM recording
        // (L3 TNT beat @1:48): every colour drops to ~30% evenly (cream 225/217/176 -> 68/65/53, board bg x0.27-0.36), i.e. plain
        // black at ~70%, no tint. Was 0.84 (June drop-game tutorial).
        private static Color DIM => UISeparation.MODAL_DIM;   // 11-02 shared dim (was black .70)
        private const float CURSOR_PX     = 140f;
        private const float DRAG_SECONDS  = 1.05f;
        private const float HUD_DIM_FALLBACK_TOP = 0.83f; // viewport y if board bounds unavailable

        private Canvas _scrimCanvas;   // ScreenSpaceCamera full-screen scrim (board bumped above it)
        private Canvas _overlayCanvas; // ScreenSpaceOverlay: HUD dim strip + cursor
        private Image  _hudDim;
        private Image  _scrimBelow, _scrimLeft, _scrimRight;  // the 3 extra rects for ShowExceptRows
        private Image  _scrimImage; // the world-scrim rect — clipped to the board top so it doesn't overlap the HUD dim
        private Transform _cursor;          // hand_point is a WORLD-space sprite (same space as the board)
        private SpriteRenderer _cursorSR;
        private Sequence _cursorSeq;
        private bool _boardBumped;
        private const float CURSOR_Z = -5f;  // in front of the board tiles

        private static TutorialSpotlight Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TutorialSpotlight");
            Instance = go.AddComponent<TutorialSpotlight>();
            Instance.Build();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Build()
        {
            // Camera-space scrim — dims all world-space chrome (board background gap, tile holder,
            // boosters); the board sprite + tiles get bumped above it to read bright.
            var scrimGO = new GameObject("TutorialScrim", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            scrimGO.transform.SetParent(transform, false);
            _scrimCanvas = scrimGO.GetComponent<Canvas>();
            _scrimCanvas.renderMode  = RenderMode.ScreenSpaceCamera;
            _scrimCanvas.worldCamera  = Camera.main;
            _scrimCanvas.planeDistance = 2f;
            _scrimCanvas.sortingOrder  = SCRIM_ORDER;
            var ss = scrimGO.GetComponent<CanvasScaler>();
            ss.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            ss.referenceResolution = new Vector2(1080f, 1920f);
            ss.matchWidthOrHeight = 1f;
            // raycastTarget MUST be false — this scrim is full-screen (the board shows through by
            // sort-order, not a hole), so blocking raycasts here would swallow every board tap/drop.
            // Input gating is handled by TutorialManager (AllowedColumn/Card), not by the dim.
            _scrimImage = MakeRect(scrimGO.transform, "Scrim", DIM, Vector2.zero, Vector2.one, false);

            // Overlay layer — dims the HUD bar (camera scrim can't) + holds the cursor.
            var ovGO = new GameObject("TutorialOverlay", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            ovGO.transform.SetParent(transform, false);
            _overlayCanvas = ovGO.GetComponent<Canvas>();
            _overlayCanvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            _overlayCanvas.sortingOrder = OVERLAY_ORDER;
            var os = ovGO.GetComponent<CanvasScaler>();
            os.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            os.referenceResolution = new Vector2(1080f, 1920f);
            os.matchWidthOrHeight = 0.5f;
            _hudDim = MakeRect(ovGO.transform, "HudDim", DIM,
                new Vector2(0f, HUD_DIM_FALLBACK_TOP), Vector2.one, true);

            _scrimCanvas.enabled = false;
            _overlayCanvas.enabled = false;
        }

        private static Image MakeRect(Transform parent, string name, Color color,
            Vector2 anchorMin, Vector2 anchorMax, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>Board bright (rounded cutout via sprite bump, exactly like the booster); tile holder
        /// + everything else dimmed by the camera scrim; HUD bar dimmed by the overlay strip.</summary>
        public static void ShowBoardFocus()
        {
            var inst = Ensure();
            if (inst._scrimCanvas != null)
            {
                if (inst._scrimCanvas.worldCamera == null) inst._scrimCanvas.worldCamera = Camera.main;
                inst._scrimCanvas.enabled = true;
            }
            if (inst._overlayCanvas != null) inst._overlayCanvas.enabled = true;
            inst.BumpBoard(true);
            inst.PositionHudDim();
            if (inst._hudDim != null) inst._hudDim.gameObject.SetActive(true);
        }

        private void BumpBoard(bool active)
        {
            var grid = GridManager.Instance;
            if (grid != null)
            {
                grid.SetBoardBackgroundSortingOrder(active ? BOARD_BG_ORDER : 0);
                Tile.AimModeTileOrder = active ? TILE_ORDER : 0;
                // Lift the board above the scrim WITHOUT flattening the row bands — a flat
                // order made stacked tiles sort arbitrarily (spotlighted C drew behind the U
                // below it). Band is TILE_ORDER..TILE_ORDER+30, clear of FX (60). 2026-09-10.
                for (int c = 0; c < RulesEngine.COLS; c++)
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                    {
                        var t = grid.GetTile(c, r);
                        if (t != null) t.ReassertSortingOrder();
                    }
            }
            _boardBumped = active;
        }

        /// <summary>Selective focus for a tutorial beat: dim EVERYTHING (world scrim + HUD dim), then raise ONLY
        /// the given board cells above the scrim. The board panel + all other tiles stay dimmed — unlike
        /// ShowBoardFocus which bumps the whole board bright. 2026-07-08 Spencer (mockup-driven).</summary>

        /// <summary>Dim EVERYTHING except a horizontal band of board rows — the near-top-out
        /// warning. 2026-09-22 Spencer: "tint everything on screen except the top two rows just
        /// to show that the player is dangerously close to topping out."
        ///
        /// WHY NOT SpotlightCells: that path lights the chosen TILES by pushing them above the
        /// scrim and lays a flat blue slab behind them (ShowFocusPanel, board-slot blue #4A70AD
        /// and the old TILE_CORNER_FRAC radius). Both were authored against the pre-2026-09 board
        /// and neither matches the authored panel now — and the danger rows are EMPTY, so there
        /// are no tiles up there to light. What is wanted is the board itself showing through.
        ///
        /// So this CUTS A HOLE instead: four rects around the band rather than one full-screen
        /// rect. The board panel, its sockets and any tiles in the band all read normally with no
        /// sort-order games and no slab — they are simply not covered.</summary>
        public static void ShowExceptRows(int rowFrom, int rowTo)
        {
            var inst = Ensure();
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid == null || cam == null) { SpotlightCells(null, null, -1); return; }

            if (inst._scrimCanvas != null)
            {
                if (inst._scrimCanvas.worldCamera == null) inst._scrimCanvas.worldCamera = Camera.main;
                inst._scrimCanvas.enabled = true;
            }
            if (inst._overlayCanvas != null) inst._overlayCanvas.enabled = true;
            inst.PositionHudDim();
            if (inst._hudDim != null) inst._hudDim.gameObject.SetActive(true);

            // Every tile back to its BASE order, so an FX bump cannot punch a bright tile through
            // the dim. The band's tiles sit at base order too — they read because nothing covers
            // them, not because they were raised.
            Tile.SpotlightActive = true;
            inst.ApplyCellSpotlight(null);
            inst.HideFocusPanel();
            DropPreview.OrderBoost = 0;

            // Band bounds, in viewport. Rows are indexed from the FLOOR, so rowTo is the top one.
            float half  = grid.RowPitch * 0.5f;
            float yLo   = grid.CellToWorld(0, Mathf.Min(rowFrom, rowTo)).y - half;
            float yHi   = grid.CellToWorld(0, Mathf.Max(rowFrom, rowTo)).y + half;
            float bandB = Mathf.Clamp01(cam.WorldToViewportPoint(new Vector3(0f, yLo, 0f)).y);
            float bandT = Mathf.Clamp01(cam.WorldToViewportPoint(new Vector3(0f, yHi, 0f)).y);
            float bandL = Mathf.Clamp01(cam.WorldToViewportPoint(new Vector3(grid.GridLeft,  0f, 0f)).x);
            float bandR = Mathf.Clamp01(cam.WorldToViewportPoint(new Vector3(grid.GridRight, 0f, 0f)).x);

            float topVP = HUD_DIM_FALLBACK_TOP;
            if (grid.BoardBackgroundWorldBounds.HasValue)
                topVP = Mathf.Clamp01(cam.WorldToViewportPoint(grid.BoardBackgroundWorldBounds.Value.max).y);
            bandT = Mathf.Min(bandT, topVP);

            inst.EnsureExtraScrims();
            SetAnchors(inst._scrimImage, new Vector2(0f,    bandT), new Vector2(1f,    topVP)); // above the band
            SetAnchors(inst._scrimBelow, new Vector2(0f,    0f),    new Vector2(1f,    bandB)); // below it
            SetAnchors(inst._scrimLeft,  new Vector2(0f,    bandB), new Vector2(bandL, bandT)); // beside it
            SetAnchors(inst._scrimRight, new Vector2(bandR, bandB), new Vector2(1f,    bandT));
            inst._scrimBelow.gameObject.SetActive(true);
            inst._scrimLeft .gameObject.SetActive(true);
            inst._scrimRight.gameObject.SetActive(true);
            WDLog.Log($"[Spotlight] band rows {rowFrom}..{rowTo} clear; vp x {bandL:F3}-{bandR:F3} y {bandB:F3}-{bandT:F3}");
        }

        private void EnsureExtraScrims()
        {
            if (_scrimBelow != null) return;
            var parent = _scrimImage.transform.parent;
            _scrimBelow = MakeRect(parent, "ScrimBelow", DIM, Vector2.zero, Vector2.one, false);
            _scrimLeft  = MakeRect(parent, "ScrimLeft",  DIM, Vector2.zero, Vector2.one, false);
            _scrimRight = MakeRect(parent, "ScrimRight", DIM, Vector2.zero, Vector2.one, false);
        }

        private static void SetAnchors(Image img, Vector2 min, Vector2 max)
        {
            if (img == null) return;
            var rt = img.rectTransform;
            rt.anchorMin = min; rt.anchorMax = max;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        /// <summary>Put the three extra rects away so the next ordinary beat gets one full scrim.</summary>
        private void ClearExtraScrims()
        {
            if (_scrimBelow != null) _scrimBelow.gameObject.SetActive(false);
            if (_scrimLeft  != null) _scrimLeft .gameObject.SetActive(false);
            if (_scrimRight != null) _scrimRight.gameObject.SetActive(false);
        }

        public static void SpotlightCells(System.Collections.Generic.List<Vector2Int> cells)
            => SpotlightCells(cells, cells, TutorialManager.AllowedCardIndex);

        // brightCells = tiles lit above the scrim; focusCells = region for the bright board slab; brightCardIndex =
        // the one hand card left lit (-1 = dim them all, e.g. board-to-board swaps use no hand card). 2026-07-08.
        public static void SpotlightCells(System.Collections.Generic.List<Vector2Int> brightCells,
                                          System.Collections.Generic.List<Vector2Int> focusCells, int brightCardIndex)
        {
            var inst = Ensure();
            if (inst._scrimCanvas != null)
            {
                if (inst._scrimCanvas.worldCamera == null) inst._scrimCanvas.worldCamera = Camera.main;
                inst._scrimCanvas.enabled = true;
            }
            if (inst._overlayCanvas != null) inst._overlayCanvas.enabled = true;
            inst.ClearExtraScrims();      // a normal beat wants ONE full-screen rect
            inst.PositionScrim();
            inst.PositionHudDim();
            if (inst._hudDim != null) inst._hudDim.gameObject.SetActive(true);
            DropPreview.OrderBoost = 10;                    // lift the drop-preview "green letter" above the dim scrim
            Tile.SpotlightActive = true;                    // guard dimmed tiles against FX re-bumps (charged bleed-through)
            inst.ApplyCellSpotlight(brightCells);
            inst.ShowFocusPanel(focusCells);               // bright board section behind the word
            inst.DimHandCardsExcept(brightCardIndex);      // dim every hand card except the active one
            HandManager.Instance?.SetNextTileDimmed(true); // dim the whole NEXT unit uniformly (below the scrim)
            // NOTE: hand-holder brightening reverted — bumping card sort orders fought the drag system's own
            // RestoreAllCardSortOrder, hiding cards mid-drag. Needs a persistent hand sort-layer. 2026-07-08.
        }

        // Bright "undimmed board section" behind the target word — a board-blue rounded slab above the scrim but
        // below the tiles, so empty drop cells read as a lit open slot. 2026-07-08 Spencer (mockup-driven).
        private SpriteRenderer _focusPanel;
        public static float FOCUS_PAD_CELLS = 0.55f;   // half a cell = window edge just past the tile edge; RM pads ~0.05 cell beyond the tiles
        private int _focusPw = -1, _focusPh = -1;
        private void ShowFocusPanel(System.Collections.Generic.List<Vector2Int> cells)
        {
            var grid = GridManager.Instance;
            if (grid == null || cells == null || cells.Count == 0) { HideFocusPanel(); return; }
            float cs = grid.CellSize;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in cells)
            {
                Vector3 p = grid.CellToWorld(c.x, c.y);
                minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
            }
            float pad = cs * FOCUS_PAD_CELLS;   // from cell CENTRES: 0.5 = tile edge (old value), 0.55 = a hair past it like RM
            minX -= pad; maxX += pad; minY -= pad; maxY += pad;
            if (_focusPanel == null)
            {
                var go = new GameObject("TutorialFocusPanel");
                go.transform.SetParent(transform, false);
                _focusPanel = go.AddComponent<SpriteRenderer>();
                _focusPanel.color = new Color(0.29f, 0.44f, 0.68f, 1f); // board-slot blue
                _focusPanel.sortingOrder = SCRIM_ORDER + 1;             // above the scrim, below spotlit tiles
            }
            // Author the rounded-rect at the ACTUAL pixel size (PPU 100) with a FIXED corner radius, so the
            // transform stays scale-1 and the corners never stretch into a pill. Regen only on size change.
            int pw = Mathf.Max(8, Mathf.RoundToInt((maxX - minX) * 100f));
            int ph = Mathf.Max(8, Mathf.RoundToInt((maxY - minY) * 100f));
            if (pw != _focusPw || ph != _focusPh || _focusPanel.sprite == null)
            {
                // Match the BOARD's corner curve so the spotlight rect lines up with the rounded board it sits
                // on. The board uses an ABSOLUTE world-space radius (GridManager: CellSize * (154/172) *
                // TILE_CORNER_FRAC); this panel is authored at PPU 100 / scale-1, so ×100 converts world→px.
                // 2026-07-10 Spencer.
                float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 1f;
                float boardRadiusWorld = cellSize * (154f / 172f) * 0.283f; // TILE_CORNER_FRAC, kept in sync with GridManager
                int radius = Mathf.Clamp(Mathf.RoundToInt(boardRadiusWorld * 100f), 4, Mathf.Min(pw, ph) / 2);
                _focusPanel.sprite = TileRenderer.CreateSolidRoundedRect(pw, ph, radius, Color.white);
                _focusPw = pw; _focusPh = ph;
            }
            _focusPanel.transform.localScale = Vector3.one;
            _focusPanel.transform.position = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0.2f);
            _focusPanel.enabled = true;
        }

        private void HideFocusPanel()
        {
            if (_focusPanel != null) _focusPanel.enabled = false;
        }

        // Dim the UNUSED hand cards with dark overlay quads (same tone as the scrim), so only the active card
        // glows — robust against the drag/refill systems that own the cards' own colours/orders. 2026-07-08.
        private SpriteRenderer[] _cardDimQuads;
        private static Sprite _cardDimSprite;
        /// <summary>OFF since the 2026-09 swap pivot. These quads dimmed the UNUSED cards in
        /// the tile holder so only the active one glowed — a drop-game idea. With the hand gone
        /// there are no cards under them, so they rendered as four black blobs sitting over the
        /// bottom of the board during any tutorial beat. Delete the guard below to restore the
        /// behaviour if a card rack ever comes back. 2026-09-10 Spencer.</summary>
        private const bool DIM_HAND_CARDS = false;

        private void DimHandCardsExcept(int brightIndex)
        {
            if (!DIM_HAND_CARDS) { ClearHandCardDim(); return; }
            var hm = HandManager.Instance;
            if (hm == null) { ClearHandCardDim(); return; } // brightIndex < 0 => dim ALL cards (no active card)
            int n = PlayerHand.HAND_SIZE;
            if (_cardDimQuads == null || _cardDimQuads.Length < n) _cardDimQuads = new SpriteRenderer[n];
            if (_cardDimSprite == null) _cardDimSprite = TileRenderer.CreateSolidRoundedRect(160, 160, 34, Color.white);
            float cardW = hm.CardSize;
            for (int i = 0; i < n; i++)
            {
                if (_cardDimQuads[i] == null)
                {
                    var go = new GameObject("TutCardDim" + i);
                    go.transform.SetParent(transform, false);
                    var sr = go.AddComponent<SpriteRenderer>();
                    sr.sprite = _cardDimSprite;
                    sr.color = DIM;        // same dark tone as the scrim
                    sr.sortingOrder = 13;  // above the card renderers (10-12), below the dragged card (20)
                    _cardDimQuads[i] = sr;
                }
                bool dim = (i != brightIndex);
                _cardDimQuads[i].enabled = dim;
                if (dim)
                {
                    _cardDimQuads[i].transform.position = new Vector3(hm.GetCardWorldX(i), hm.GetCardWorldY(), -2f);
                    float s = (cardW * 1.02f) / (160f / 100f);
                    _cardDimQuads[i].transform.localScale = new Vector3(s, s, 1f);
                }
            }
        }
        private void ClearHandCardDim()
        {
            if (_cardDimQuads == null) return;
            foreach (var q in _cardDimQuads) if (q != null) q.enabled = false;
        }

        private void ApplyCellSpotlight(System.Collections.Generic.List<Vector2Int> cells)
        {
            var grid = GridManager.Instance;
            if (grid == null) return;
            // Reset every tile to dimmed (below the scrim), then light ONLY the requested cells.
            for (int c = 0; c < RulesEngine.COLS; c++)
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    var t = grid.GetTile(c, r);
                    if (t != null) t.SetSpotlight(-1);
                }
            if (cells != null)
                foreach (var cell in cells)
                {
                    var t = grid.GetTile(cell.x, cell.y);
                    if (t != null) t.SetSpotlight(SPOTLIGHT_TILE_ORDER); // bright, but below the dragged card
                }
        }

        /// <summary>Dim from the board's top edge up to the top of the screen — covers the HUD bar.</summary>
        // Clip the world scrim so its TOP lands exactly on the board top — the same edge the HUD dim starts at —
        // so the gap between the board and the HUD bar is dimmed ONCE, not twice (was a dark band). 2026-07-08 Spencer.
        private void PositionScrim()
        {
            if (_scrimImage == null) return;
            float topVP = HUD_DIM_FALLBACK_TOP;
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid != null && cam != null && grid.BoardBackgroundWorldBounds.HasValue)
                topVP = Mathf.Clamp01(cam.WorldToViewportPoint(grid.BoardBackgroundWorldBounds.Value.max).y);
            var rt = _scrimImage.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1f, topVP);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private void PositionHudDim()
        {
            if (_hudDim == null) return;
            float topVP = HUD_DIM_FALLBACK_TOP;
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid != null && cam != null && grid.BoardBackgroundWorldBounds.HasValue)
                topVP = Mathf.Clamp01(cam.WorldToViewportPoint(grid.BoardBackgroundWorldBounds.Value.max).y);
            var rt = _hudDim.rectTransform;
            rt.anchorMin = new Vector2(0f, topVP);
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public static void Hide()
        {
            if (Instance == null) return;
            // DIAG 2026-07-10: tracking an intermittent case where the edit-tutorial hand_point vanishes on
            // level load — logs WHO cleared the spotlight so we can catch a Hide() that races the ShowTap.
            if (Instance._cursor != null && Instance._cursor.gameObject.activeSelf)
                WDLog.Log($"[SpotlightDiag] Hide() cleared an ACTIVE tap cursor.\n{System.Environment.StackTrace}");
            Tile.SpotlightActive = false;
            Instance.BumpBoard(false);
            Instance.ApplyCellSpotlight(null); // clear any per-tile tutorial spotlight
            Instance.HideFocusPanel();
            Instance.ClearExtraScrims();
            Instance.ClearHandCardDim();
            HandManager.Instance?.SetNextTileDimmed(false);
            BoosterHUDSlot.Instance?.SetEditPanelBright(false); // revert the swap-panel spotlight
            DropPreview.OrderBoost = 0;
            Instance._cursorSeq?.Kill();
            Instance._cursorSeq = null;
            if (Instance._cursor != null) Instance._cursor.gameObject.SetActive(false);
            if (Instance._scrimCanvas   != null) Instance._scrimCanvas.enabled = false;
            if (Instance._overlayCanvas != null) Instance._overlayCanvas.enabled = false;
        }

        // ── Drag-gesture cursor (hand_point WORLD sprite — same coordinate space as the board, so the
        //    fingertip lands EXACTLY on its target with no screen/canvas conversion) ──────────────────
        /// <summary>Loop a hand_point drag from one WORLD position to another.</summary>
        public static void ShowDragGesture(Vector3 fromWorld, Vector3 toWorld)
        {
            var inst = Ensure();
            inst.BuildCursorGesture(fromWorld, toWorld);
        }

        // Soft drop shadow under the hand cursor so it separates from the tiles it rests on. 2026-07-08 Spencer.
        private SpriteRenderer _cursorShadowSR;
        private void EnsureCursorShadow()
        {
            if (_cursor == null || _cursorShadowSR != null) return;
            var sgo = new GameObject("CursorShadow");
            sgo.transform.SetParent(_cursor, false);
            sgo.transform.localPosition = new Vector3(0.06f, -0.06f, 0f); // offset down-right
            _cursorShadowSR = sgo.AddComponent<SpriteRenderer>();
            _cursorShadowSR.sprite       = GetHandSprite();
            _cursorShadowSR.color        = new Color(0f, 0f, 0f, 0.28f); // soft dark shadow
            _cursorShadowSR.sortingOrder = (_cursorSR != null ? _cursorSR.sortingOrder : 120) - 1;
        }

        private void BuildCursorGesture(Vector3 fromWorld, Vector3 toWorld)
        {
            if (_cursor == null)
            {
                var go = new GameObject("DragCursor");
                _cursorSR = go.AddComponent<SpriteRenderer>();
                _cursorSR.sprite = GetHandSprite();   // pivot baked at the fingertip
                _cursorSR.sortingOrder = 120;          // above the board tiles
                _cursor = go.transform;
            }
            _cursor.gameObject.SetActive(true);
            EnsureCursorShadow();

            // Scale so the hand is ~1.2 cells tall.
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 1f;
            float nativeH = (_cursorSR != null && _cursorSR.sprite != null) ? _cursorSR.sprite.bounds.size.y : 1f;
            float scale = (cell * 1.2f) / Mathf.Max(nativeH, 0.001f);

            fromWorld.z = CURSOR_Z; toWorld.z = CURSOR_Z;

            _cursorSeq?.Kill();
            _cursorSeq = DOTween.Sequence().SetUpdate(true);
            _cursorSeq.AppendCallback(() => { _cursor.position = fromWorld; _cursor.localScale = new Vector3(scale, scale, 1f); });
            _cursorSeq.Append(_cursor.DOScale(scale * 0.85f, 0.18f).SetEase(Ease.OutQuad));
            _cursorSeq.Append(_cursor.DOMove(toWorld, DRAG_SECONDS).SetEase(Ease.InOutSine));
            _cursorSeq.Append(_cursor.DOScale(scale, 0.15f));
            _cursorSeq.AppendInterval(0.35f);
            _cursorSeq.SetLoops(-1);
        }

        /// <summary>Looping "push down" TAP on a single world point (no travel) — the hand hovers just above
        /// the target, dips onto it with a press-squash, lifts, and repeats. Used to coach a TAP/edit, vs the
        /// drag gesture used for drops. 2026-07-07 Spencer.</summary>
        public static void ShowTap(Vector3 worldPos)
        {
            var inst = Ensure();
            inst.BuildTapGesture(worldPos);
        }

        private void BuildTapGesture(Vector3 world)
        {
            if (_cursor == null)
            {
                var go = new GameObject("DragCursor");
                _cursorSR = go.AddComponent<SpriteRenderer>();
                _cursorSR.sprite = GetHandSprite();   // pivot baked at the fingertip
                _cursorSR.sortingOrder = 120;
                _cursor = go.transform;
            }
            _cursor.gameObject.SetActive(true);
            EnsureCursorShadow();

            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 1f;
            float nativeH = (_cursorSR != null && _cursorSR.sprite != null) ? _cursorSR.sprite.bounds.size.y : 1f;
            float scale = (cell * 1.2f) / Mathf.Max(nativeH, 0.001f);

            world.z = CURSOR_Z;
            Vector3 hover = world + Vector3.up * (cell * 0.22f); // rest position: fingertip hovers just above

            _cursorSeq?.Kill();
            _cursorSeq = DOTween.Sequence().SetUpdate(true);
            _cursorSeq.AppendCallback(() => { _cursor.position = hover; _cursor.localScale = new Vector3(scale, scale, 1f); });
            // Press DOWN onto the target with a slight squash, hold a beat, lift back up, pause, repeat.
            _cursorSeq.Append(_cursor.DOMove(world, 0.15f).SetEase(Ease.OutQuad));
            _cursorSeq.Join(_cursor.DOScale(scale * 0.80f, 0.15f).SetEase(Ease.OutQuad));
            _cursorSeq.AppendInterval(0.08f);
            _cursorSeq.Append(_cursor.DOMove(hover, 0.20f).SetEase(Ease.InQuad));
            _cursorSeq.Join(_cursor.DOScale(scale, 0.20f).SetEase(Ease.InQuad));
            _cursorSeq.AppendInterval(0.32f);
            _cursorSeq.SetLoops(-1);
        }

        // ── hand_point sprite (Resources/Tiles/hand_point) — pivot baked at the FINGERTIP, measured
        //    from the art: opaque pixel (40,16) of 133² → (0.301, 0.880). So transform.position == tip. ──
        private static Sprite _handSprite;
        // Fingertip in the hand art, as a fraction (y up). 2026-10-23: Spencer's new pointer (Documents "1cursor copy.png", 515px,
        // finger up-left) — tip pad measured at px (85,30). Old hand was (0.301, 0.880); old art backed up to
        // ~/Documents/hand_point_OLD_backup_2026-10-23.png.
        public static float HAND_TIP_X = 0.17f, HAND_TIP_Y = 0.94f;
        private static Sprite GetHandSprite()
        {
            if (_handSprite != null) return _handSprite;
            var tex = Resources.Load<Texture2D>("Tiles/hand_point");
            if (tex == null)
            {
                var s = Resources.Load<Sprite>("Tiles/hand_point");
                if (s != null) tex = s.texture;
            }
            if (tex != null)
                _handSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                            new Vector2(HAND_TIP_X, HAND_TIP_Y), 100f); // pivot = fingertip
            return _handSprite;
        }
    }
}
