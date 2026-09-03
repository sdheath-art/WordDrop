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
        private const int SCRIM_ORDER    = 9;  // between board tiles (5) and hand cards (10) so the WHOLE hand stays
                                               // bright through drag without bumping it (which fought the drag). 2026-07-08
        private const int SPOTLIGHT_TILE_ORDER = 12; // bright (>scrim 9) but BELOW the dragged card (20) so it never ducks
        private const int BOARD_BG_ORDER = 17;
        private const int TILE_ORDER     = 25;
        private const int OVERLAY_ORDER  = 150;  // HUD dim + cursor — above the HUD canvas (50)
        private static readonly Color DIM = new Color(0f, 0f, 0f, 0.84f);
        private const float CURSOR_PX     = 140f;
        private const float DRAG_SECONDS  = 1.05f;
        private const float HUD_DIM_FALLBACK_TOP = 0.83f; // viewport y if board bounds unavailable

        private Canvas _scrimCanvas;   // ScreenSpaceCamera full-screen scrim (board bumped above it)
        private Canvas _overlayCanvas; // ScreenSpaceOverlay: HUD dim strip + cursor
        private Image  _hudDim;
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
                int tileOrder = active ? TILE_ORDER : 5;
                for (int c = 0; c < RulesEngine.COLS; c++)
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                    {
                        var t = grid.GetTile(c, r);
                        if (t != null) t.SetSortingOrder(tileOrder);
                    }
            }
            _boardBumped = active;
        }

        /// <summary>Selective focus for a tutorial beat: dim EVERYTHING (world scrim + HUD dim), then raise ONLY
        /// the given board cells above the scrim. The board panel + all other tiles stay dimmed — unlike
        /// ShowBoardFocus which bumps the whole board bright. 2026-07-08 Spencer (mockup-driven).</summary>
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
            float pad = cs * 0.5f;
            minX -= pad; maxX += pad; minY -= pad; maxY += pad;
            if (_focusPanel == null)
            {
                var go = new GameObject("TutorialFocusPanel");
                go.transform.SetParent(transform, false);
                _focusPanel = go.AddComponent<SpriteRenderer>();
                _focusPanel.color = new Color(0.29f, 0.44f, 0.68f, 1f); // board-slot blue
                _focusPanel.sortingOrder = SCRIM_ORDER + 1;             // above scrim (15), below tiles (25)
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
        private void DimHandCardsExcept(int brightIndex)
        {
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
                Debug.Log($"[SpotlightDiag] Hide() cleared an ACTIVE tap cursor.\n{System.Environment.StackTrace}");
            Tile.SpotlightActive = false;
            Instance.BumpBoard(false);
            Instance.ApplyCellSpotlight(null); // clear any per-tile tutorial spotlight
            Instance.HideFocusPanel();
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
                                            new Vector2(0.301f, 0.880f), 100f); // pivot = fingertip
            return _handSprite;
        }
    }
}
