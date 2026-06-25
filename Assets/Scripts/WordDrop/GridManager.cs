using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Owns the 7×6 Connect-Four grid.
    /// Renders empty cells as procedural rounded-rect sprites.
    /// Tracks occupied cells and exposes lowest empty row per column.
    /// Presentation-only — receives commands from MatchController via events.
    ///
    /// Gravity system:
    ///   RemoveTiles(cells) — destroys tile GameObjects at given positions, nulls cell data.
    ///   ApplyGravity() — coroutine that compacts each column downward and animates tiles.
    ///   Permanent glow is preserved through gravity — tiles keep their glow color.
    ///
    /// Grid coordinate convention:
    ///   col 0..6  = left to right
    ///   row 0..5  = bottom to top  (row 0 = floor)
    /// </summary>
    public class GridManager : MonoBehaviour
    {
        // ---------------------------------------------------------------------------
        // Constants
        // ---------------------------------------------------------------------------

        public const int COLS     = 6;
        public const int MAX_ROWS = 9;
        public static int ROWS => RulesEngine.ROWS;

        private const float GRID_HEIGHT_FRACTION          = 0.82f;  // denser, less bulky
        // Board width as fraction of screen width. Tile = fraction / COLS.
        // Target: 12-13% screen width per tile (Candy Crush / Royal Match benchmark).
        // 6 cols × 13% = 0.78. Survival gets a touch more (chunky, high-pressure feel).
        private const float SURVIVAL_GRID_WIDTH_FRACTION  = 0.882f;  // was 0.84 — +5% breathing room
        private const float DEFAULT_GRID_WIDTH_FRACTION   = 0.819f;  // was 0.78 — +5% breathing room
        private const float SURVIVAL_GRID_TOP_MARGIN      = 0.30f; // push grid down — more room above for HUD, thumb-friendly
        private const float GRAVITY_FALL_SPEED            = 14f; // was 10 originally — slightly faster

        // Board: deep indigo hero object — darker and cooler than background
        // 2026-05-30 candy-bright pivot: board panel is now Candy-Crush-style
        // desaturated mid-tone purple-gray. The board acts as a STAGE that
        // lets the saturated tile sprites pop — a light cream board would
        // mute them, a dark moody board fights the warm background.
        // FRAME_OUTER matches BOARD_INNER so the two-layer sprite reads as
        // a SINGLE solid panel (no visible inner frame). Same pattern as the
        // original dark-navy panel — just at a different value.
        // See project_wordrop_visual_direction_2026_05_30.
        // 2026-06-24 Spencer: shifted from cyan #0076b0 to a softer CORNFLOWER blue to match the Candy Crush
        // board (their blue is less green/cyan, more periwinkle).
        private static readonly Color FRAME_OUTER    = new Color(0.20f, 0.38f, 0.62f, 1f);  // deeper cornflower — matches BOARD_INNER
        private static readonly Color FRAME_EDGE     = new Color(0.220f, 0.270f, 0.540f, 1f);  // brighter top lip — sculpted
        private static readonly Color BOARD_INNER    = new Color(0.20f, 0.38f, 0.62f, 1f);  // deeper cornflower (darkened from too-light 0.27/0.45/0.70)

        // Cells: readable against board — lighter face, dark inset border
        private static readonly Color CELL_FILL_COLOR   = new Color(0.150f, 0.190f, 0.410f, 1f);  // slightly brighter than board inner
        private static readonly Color CELL_BORDER_COLOR = new Color(0.045f, 0.060f, 0.160f, 1f);  // deeper inset for contrast

        // ---------------------------------------------------------------------------
        // Runtime layout
        // ---------------------------------------------------------------------------

        public float CellSize   { get; private set; }
        public float GridLeft   { get; private set; }
        public float GridBottom { get; private set; }
        public float GridTop    { get; private set; }
        public float GridRight  { get; private set; }

        // ---------------------------------------------------------------------------
        // Cell state
        // ---------------------------------------------------------------------------

        private CellData[,]    _cells      = new CellData[COLS, MAX_ROWS];
        private Tile[,]        _tiles      = new Tile[COLS, MAX_ROWS];
        private GameObject[,]  _cellObjects = new GameObject[COLS, MAX_ROWS];

        // 2026-06-05 Spencer: authored cell-slot liner (Resources/menu psds/cell_liner.psd)
        // stamped on every cell. Tunable LIVE in the Inspector (Play mode) — see Update() —
        // so the look adjusts without recompiling; re-saving the PSD hot-reloads the art.
        // 2026-06-05 Spencer: TEMP compile-time switch — OFF to isolate device frame drops.
        // Flip back to true to restore the cell liner.
        private const bool CELL_LINER_ENABLED = false;
        [Header("Cell Liner (tune live in Play mode)")]
        [SerializeField] private bool  _cellLinerEnabled = true;
        [SerializeField, Range(0.5f, 1.3f)] private float _cellLinerScale   = 0.95f; // 2026-06-05 Spencer: back to original. Size as a fraction of the cell pitch.
        [SerializeField] private Color _cellLinerTint    = Color.white;
        [SerializeField, Range(0f, 1f)]     private float _cellLinerOpacity = 1f;
        private SpriteRenderer[,] _cellLinerSRs = new SpriteRenderer[COLS, MAX_ROWS];
        private Sprite _cellLinerSprite;

        // 2026-06-05 Spencer: live BOARD shadow darkness (multiply _Strength). Applied to
        // Tile's static shadow material every frame, so dragging this in Play mode dials
        // the board drop-shadow darkness without recompiling.
        [Header("Board Shadow (tune live in Play mode)")]
        [SerializeField, Range(0f, 2f)] private float _boardShadowStrength = 0.48f;

        // ---------------------------------------------------------------------------
        // Private refs
        // ---------------------------------------------------------------------------

        private Camera     _cam;
        private GameObject _gridRoot;
        private SpriteRenderer _gridBackgroundSR; // stored so booster aim-mode can bump its sortingOrder above the scrim

        private readonly List<GameObject> _allTileObjects = new List<GameObject>();

        // ── Tile Object Pool ─────────────────────────────────────────────
        private readonly Stack<Tile> _tilePool = new Stack<Tile>(80);

        private Tile CheckoutTile(char letter, int col, int row, float cellSize, int ownerIndex = -1)
        {
            Tile tile;
            if (_tilePool.Count > 0)
            {
                tile = _tilePool.Pop();
                tile.Reinitialise(letter, col, row, cellSize, ownerIndex);
            }
            else
            {
                GameObject go = new GameObject($"Tile_{letter}_{col}_{row}");
                tile = go.AddComponent<Tile>();
                tile.Initialise(letter, col, row, cellSize, ownerIndex);
            }
            _allTileObjects.Add(tile.gameObject);
            return tile;
        }

        private void ReturnTile(Tile tile)
        {
            if (tile == null) return;
            _allTileObjects.Remove(tile.gameObject);
            tile.ResetForPool();
            _tilePool.Push(tile);
        }

        // ── Board shake (chest crack) — jiggles the board frame + the loose board tiles together, but
        // NOT the hand rack (it isn't in _allTileObjects / under _gridRoot, so it stays still). Decaying
        // Perlin offset; doesn't hard-restore tiles so a tile pooled mid-shake can't get snapped back.
        // 2026-06-18 Spencer.
        public void ShakeBoard(float magnitude, float duration)
        {
            if (_gridRoot == null || magnitude <= 0f || duration <= 0f) return;
            StartCoroutine(ShakeBoardCoroutine(magnitude, duration));
        }

        private System.Collections.IEnumerator ShakeBoardCoroutine(float mag, float dur)
        {
            // Shake the board letter tiles via an ADDITIVE per-frame DELTA (not a captured base) so it
            // rides ON TOP of gravity/cascade motion instead of fighting it — fighting made the shake
            // invisible during cascade/edit detonations (gravity overwrote the offset). Board area only
            // (Y filter) → the hand rack stays still. 2026-06-18 Spencer.
            float boardBottom = GridBottom - CellSize * 0.5f;
            var ts = new System.Collections.Generic.List<Transform>();
            for (int i = 0; i < _allTileObjects.Count; i++)
            {
                var go = _allTileObjects[i];
                if (go == null || go.transform.position.y < boardBottom) continue;
                ts.Add(go.transform);
            }
            if (ts.Count == 0) yield break;
            float seedX = Random.value * 100f, seedY = Random.value * 100f + 50f;
            float t = 0f;
            Vector3 prevOff = Vector3.zero;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float decay = 1f - Mathf.Clamp01(t / dur);                 // ease the shake out to zero
                float nx = (Mathf.PerlinNoise(seedX, t * 38f) * 2f - 1f) * mag * decay;
                float ny = (Mathf.PerlinNoise(seedY, t * 38f) * 2f - 1f) * mag * decay;
                Vector3 off = new Vector3(nx, ny, 0f);
                Vector3 delta = off - prevOff;                              // apply only the CHANGE this frame
                prevOff = off;
                for (int i = 0; i < ts.Count; i++)
                    if (ts[i] != null) ts[i].position += delta;
                yield return null;
            }
            if (prevOff != Vector3.zero)                                    // remove residual so tiles end at rest
                for (int i = 0; i < ts.Count; i++)
                    if (ts[i] != null) ts[i].position -= prevOff;
        }

        // ---------------------------------------------------------------------------
        // Singleton
        // ---------------------------------------------------------------------------

        public static GridManager Instance { get; private set; }

        // ---------------------------------------------------------------------------
        // Unity lifecycle
        // ---------------------------------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _cam = Camera.main;
            CalculateLayout();
            BuildGridVisuals();

//             Debug.Log($"[GridManager] Awake — cellSize={CellSize:F3}, " +
                      // $"gridLeft={GridLeft:F3}, gridBottom={GridBottom:F3}");
        }

        /// <summary>
        /// Destroys existing grid visuals and rebuilds for current ROWS.
        /// Call after mode flag is set (e.g., from MatchController.StartMatch).
        /// </summary>
        public void RebuildGrid()
        {
            // Destroy existing cell objects and tiles
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < MAX_ROWS; row++)
                {
                    if (_cellObjects[col, row] != null)
                    {
                        Destroy(_cellObjects[col, row]);
                        _cellObjects[col, row] = null;
                    }
                    if (_tiles[col, row] != null)
                    {
                        ReturnTile(_tiles[col, row]);
                        _tiles[col, row] = null;
                    }
                    _cells[col, row] = null;
                }
            }

            // Destroy grid root (background panel + cells)
            if (_gridRoot != null)
            {
                Destroy(_gridRoot);
                _gridRoot = null;
            }

            // Recalculate layout for new ROWS and rebuild
            CalculateLayout();
            BuildGridVisuals();

//             Debug.Log($"[GridManager] RebuildGrid — ROWS={ROWS}, cellSize={CellSize:F3}");
        }

        // ---------------------------------------------------------------------------
        // Layout calculation
        // ---------------------------------------------------------------------------

        // ── PSD layout (canvas 1179×2556, iPhone 16/15 Pro) ──────────────────
        // Camera ortho=10 (SceneBootstrap.cs:105) → worldH=20, so 1 PSD pixel
        // = 20/2556 = 0.007825 world units. Mirrors helpers in HandManager.cs
        // and BoosterHUDSlot.cs.
        private const float PSD_CANVAS_W   = 1179f;
        private const float PSD_CANVAS_H   = 2556f;
        // 2026-05-29: board sized + cells anchored so bottom-row tiles sit
        // cleanly inside the board's rounded bottom edge.
        //   - Slightly wider (1030→1070) for horizontal corner margin
        //   - Recentered (X=54.5) instead of 80
        //   - Slightly taller (1346→1380) to give cells more room
        //   - Moved up (Y=500→466) so the board BOTTOM stays at 1846 PSD
        //     (preserves the 87 PSD drag distance to the hand pill at 1933)
        //   - Cells anchored to board bottom with 30 PSD margin (see
        //     CalculateLayout below) — top rows overflow the board top
        //     but that's invisible in normal play.
        // 2026-05-30: board grown twice. Originally 1070×1380. Now 1110×1420
        // (+40 W, +40 H total). Each step adds +20 W (+10 per side) and +20 H
        // (all to TOP since cells anchor to bottom). X recentered each time
        // (canvas-center 589.5 - W/2 = 34.5). Y shifted up to keep bottom
        // edge fixed (cells don't move). Outer side margin: 31 → 41 → 51 PSD.
        private const float PSD_BOARD_X    = 34.5f;
        private const float PSD_BOARD_Y    = 426f;
        private const float PSD_BOARD_W    = 1110f;
        private const float PSD_BOARD_H    = 1420f;
        private const float PSD_BOARD_BOTTOM_MARGIN = 30f; // gap above board bottom for cells
        // 2026-05-28 (Path A, Phase 2): cell pitch = 165 PSD (visible tile
        // 150 + 15 px inter-tile gap). 6×165 = 990, leaving ~20 px margin on
        // each side inside the 1030 board frame. Visible tile sized via the
        // tile-fraction in Tile.cs (= 150/165 ≈ 0.909).
        // 2026-05-30: pitch bumped 165 → 168 for a hair more inter-tile gap
        // WITHOUT shrinking tiles. Outer board margin trims from 40 → 31 PSD
        // per side; inter-tile gap grows 15 → 18 PSD. Tile visual size stays
        // 150 PSD (Tile.cs TILE_DISPLAY_RATIO MUST stay in sync — currently
        // 150f / 168f. If you change pitch, update Tile.cs to match.)
        private const float PSD_CELL_PITCH = 172f; // 2026-06-05 Spencer: 168→172, board a hair bigger + wider tile spacing (gap 21→25 PSD; tile stays 147)

        private float PsdToWorld(float psdPx)
        {
            float halfH = _cam != null ? _cam.orthographicSize : 10f;
            return psdPx * (2f * halfH / PSD_CANVAS_H);
        }

        private float PsdXToWorld(float xPsd)
        {
            return PsdToWorld(xPsd - PSD_CANVAS_W * 0.5f);
        }

        private float PsdYToWorld(float yPsd)
        {
            return PsdToWorld(PSD_CANVAS_H * 0.5f - yPsd);
        }

        private void CalculateLayout()
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);
            bool isSurvival = SurvivalManager.IsSurvivalMode;

            // 2026-05-28 (Path A, Phase 2): Survival board pinned to exact PSD
            // spec — tiles are 150 px, board is 1030×1346, centered at PSD
            // (595, 1275). Other modes keep the auto-fit math below.
            if (isSurvival)
            {
                CellSize = PsdToWorld(PSD_CELL_PITCH);
                float psdCellAreaW = CellSize * COLS;
                float psdCellAreaH = CellSize * ROWS;

                // 2026-05-29: cells anchored to the BOARD BOTTOM (not the
                // board center) so the visible bottom row always sits inside
                // the rounded bottom edge. The cell stack extends upward; top
                // rows may extend above the board top but those are usually
                // empty (game ends before reaching the top row), so the
                // overflow is invisible in normal play.
                float cellAreaCenterX     = PsdXToWorld(PSD_BOARD_X + PSD_BOARD_W * 0.5f);
                float cellAreaBottom_w    = PsdYToWorld(PSD_BOARD_Y + PSD_BOARD_H - PSD_BOARD_BOTTOM_MARGIN);

                GridLeft   = cellAreaCenterX - psdCellAreaW * 0.5f;
                GridRight  = cellAreaCenterX + psdCellAreaW * 0.5f;
                GridBottom = cellAreaBottom_w;
                GridTop    = cellAreaBottom_w + psdCellAreaH;
                return;
            }

            float widthFraction = isSurvival ? SURVIVAL_GRID_WIDTH_FRACTION : DEFAULT_GRID_WIDTH_FRACTION;

            // Reserve space for HUD (top) and hand cards (bottom).
            // Benchmark targets (Candy Crush / Royal Match / Wordscapes):
            //   top: 10-14% of screen height, bottom: 28-32%.
            // topReserve = 0.22 * halfH → 11% of screen.
            // bottomReserve = 0.50 * halfH → 25% of screen (slightly under benchmark so
            // survival 9-row grid at 0.84 width fraction still fits without collision).
            bool mobile = Application.isMobilePlatform;
            float topReserveBase = halfH * (mobile ? 0.22f : 0.20f);
            float bottomReserveBase = halfH * (mobile ? 0.50f : 0.48f);

            // Device-adaptive safe-area insets — notch/Dynamic Island top, home-indicator bottom.
            // Expressed in world units (halfH = half screen height in world units).
            // Industry standard (Supercell, King, Playrix): layout respects safe area, not just
            // screen rect. Otherwise UI bleeds under notch / gets eaten by home indicator.
            float safeTopInset = 0f;
            float safeBottomInset = 0f;
            if (Screen.height > 0)
            {
                float h = Screen.height;
                float safeBottomPx = Screen.safeArea.y;
                float safeTopPx = h - (Screen.safeArea.y + Screen.safeArea.height);
                safeBottomInset = (safeBottomPx / h) * halfH * 2f;
                safeTopInset    = (safeTopPx    / h) * halfH * 2f;
            }

            float topReserve    = topReserveBase    + safeTopInset;
            float bottomReserve = bottomReserveBase + safeBottomInset;

            float availableWidth = halfW * 2f * widthFraction;
            float availableHeight = (halfH * 2f) - topReserve - bottomReserve;

            float cellFromWidth = availableWidth / COLS;
            float cellFromHeight = availableHeight / ROWS;

            CellSize = Mathf.Min(cellFromWidth, cellFromHeight);

            float gridWorldWidth = CellSize * COLS;
            float gridWorldHeight = CellSize * ROWS;

            // Anchor grid to bottom of available space — tiles build upward
            float gridCenterX = 0f;
            float gridAreaBottom = -halfH + bottomReserve;

            // Vertical fine-tune: positive = move board up, negative = down.
            // Expressed as fraction of halfH. The hand area anchors to
            // GridBottom (via GetCardRowY), so it shifts with the board.
            // 2026-05-28 (Path A): raised from -0.10 → +0.05 (+0.15 halfH net,
            // ≈ +7.5% screen height) to free bottom-screen real estate for the
            // tools + booster rows. Massive empty space above the board in
            // earlier screenshots indicated this was wasted vertical budget.
            const float BOARD_Y_OFFSET = 0.05f;
            gridAreaBottom += halfH * BOARD_Y_OFFSET;

            GridLeft   = gridCenterX - gridWorldWidth  / 2f;
            GridRight  = gridCenterX + gridWorldWidth  / 2f;
            GridBottom = gridAreaBottom;
            GridTop    = gridAreaBottom + gridWorldHeight;
        }

        // ---------------------------------------------------------------------------
        // Visual construction
        // ---------------------------------------------------------------------------

        private void BuildGridVisuals()
        {
            _gridRoot = new GameObject("GridRoot");
            _gridRoot.transform.SetParent(transform, false);

            // 2026-05-28 (Path A, Phase 2): Survival board sprite is locked
            // to exact PSD spec (1030×1346) so the painted frame matches
            // Spencer's mock. Cells inside are 6×150 = 900 wide, leaving 65 px
            // padding each side; height is ~tile-flush. Other modes keep the
            // legacy 16%-of-cellSize uniform padding.
            if (SurvivalManager.IsSurvivalMode)
            {
                // 2026-06-04 Spencer: even margins — board now WRAPS the cell area with
                // padding = HALF the inter-tile gap on every side, so the distance from
                // the board edge to the outer tiles equals the gap between tiles. (Was
                // PSD-pinned 1110×1420, whose ~51 PSD side margin was fatter than the
                // 21 PSD tile gap.) The outer tile is already inset half-a-gap from the
                // cell-area edge, so half-gap padding makes board→tile == one full gap.
                // gap = CellSize*(1 - TILE_DISPLAY_RATIO) where ratio = 158/172 (tightened 2026-06-16).
                float halfGapPad = CellSize * (1f - 154f / 172f) * 0.5f; // 2026-06-24: 158→154 (a hair more spacing + margin)
                CreateBackgroundPanel((GridRight - GridLeft) + halfGapPad * 2f,
                                      (GridTop   - GridBottom) + halfGapPad * 2f);
            }
            else
            {
                float bgPadding = CellSize * 0.16f;
                CreateBackgroundPanel((GridRight - GridLeft) + bgPadding * 2f,
                                      (GridTop   - GridBottom) + bgPadding * 2f);
            }

            int texSize = Mathf.Clamp(Mathf.RoundToInt(CellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // slightly rounder corners
            int border  = Mathf.Max(3, texSize / 14);  // thicker border for stronger inset

            Sprite cellSprite = TileRenderer.CreateRoundedRect(
                texSize, texSize, radius,
                CELL_FILL_COLOR, CELL_BORDER_COLOR, border);

            // 2026-06-05 Spencer: load the authored cell-slot liner once. Imported as a
            // Default texture, so load as Texture2D and build the sprite in code (full
            // frame; sized per cell via _cellLinerScale in ApplyCellLinerTuning).
            // CELL_LINER_ENABLED is a compile-time gate (temp OFF to isolate frame drops
            // on device — guaranteed off in the build, no Inspector-serialization gotcha).
            if (CELL_LINER_ENABLED && _cellLinerSprite == null)
            {
                Texture2D linerTex = Resources.Load<Texture2D>("menu psds/cell_liner");
                if (linerTex != null)
                    _cellLinerSprite = Sprite.Create(linerTex, new Rect(0, 0, linerTex.width, linerTex.height),
                                                     new Vector2(0.5f, 0.5f), 100f);
            }

            // Each cell GO carries the liner SpriteRenderer (order 1: above board, below tiles).
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    Vector3 worldPos = CellToWorld(col, row);

                    GameObject cellGO = new GameObject($"Cell_{col}_{row}");
                    cellGO.transform.SetParent(_gridRoot.transform, false);
                    cellGO.transform.position = worldPos;

                    if (_cellLinerSprite != null)
                    {
                        var lsr = cellGO.AddComponent<SpriteRenderer>();
                        lsr.sprite       = _cellLinerSprite;
                        lsr.sortingOrder = 1; // above board panel (0), below tiles (5)
                        _cellLinerSRs[col, row] = lsr;
                    }

                    _cellObjects[col, row] = cellGO;
                    _cells[col, row]       = null;
                }
            }

            ApplyCellLinerTuning(); // apply size/tint/opacity immediately (also re-applied live in Update)
        }

        /// <summary>2026-06-05 Spencer: pushes the Inspector cell-liner values (size/tint/
        /// opacity/enabled) onto every cell renderer. Called on build AND every frame in
        /// Update, so dragging the sliders in Play mode updates the whole grid instantly.</summary>
        private void ApplyCellLinerTuning()
        {
            if (_cellLinerSRs == null || _cellLinerSprite == null) return;
            float spriteBounds = _cellLinerSprite.bounds.size.x > 0.0001f ? _cellLinerSprite.bounds.size.x : 1f;
            float scale = (_cellLinerScale * CellSize) / spriteBounds;
            Color c = _cellLinerTint; c.a *= Mathf.Clamp01(_cellLinerOpacity);
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    var sr = _cellLinerSRs[col, row];
                    if (sr == null) continue;
                    sr.enabled = _cellLinerEnabled;
                    sr.transform.localScale = new Vector3(scale, scale, 1f);
                    sr.color = c;
                }
        }

        private void Update()
        {
            // Live cell-liner tuning — see ApplyCellLinerTuning. Cheap (stored SRs, no GetComponent).
            ApplyCellLinerTuning();

            // 2026-06-05 Spencer: \ flips the BOARD drop shadow A↔B in place (logs which).
            if (Input.GetKeyDown(KeyCode.Backslash)) Tile.FlipBoardShadow();

            // Live board-shadow darkness — drag _boardShadowStrength in the Inspector.
            Tile.SetBoardShadowStrength(_boardShadowStrength);
        }

        private void CreateBackgroundPanel(float bgW, float bgH)
        {

            int bgTexW   = Mathf.Clamp(Mathf.RoundToInt(bgW * 150f), 64, 1024);
            int bgTexH   = Mathf.Clamp(Mathf.RoundToInt(bgH * 150f), 64, 1024);
            // 2026-06-04 Spencer: match the board corners to the TILE corners — same
            // ABSOLUTE world-space radius, so a board corner reads as the same curve as a
            // tile corner (not proportional, which would balloon on the big board). The
            // baked glossy tile's corner radius measures ~0.283 of the tile size; tile
            // world size = CellSize * TILE_DISPLAY_RATIO (147/172). If the match looks
            // off, tweak TILE_CORNER_FRAC.
            const float TILE_CORNER_FRAC = 0.283f;
            float tileRadiusWorld = CellSize * (154f / 172f) * TILE_CORNER_FRAC; // 2026-06-24: synced with TILE_DISPLAY_RATIO (158→154)
            float texelsPerWorld  = bgTexW / Mathf.Max(bgW, 0.0001f);
            int bgRadius = Mathf.Clamp(
                Mathf.RoundToInt(tileRadiusWorld * texelsPerWorld),
                4, Mathf.Min(bgTexW, bgTexH) / 2);
            int framePx  = Mathf.Max(4, Mathf.Min(bgTexW, bgTexH) / 14);

            // Two-layer frame: dark outer border → inner fill (no highlight edge — it distorts on stretch)
            Sprite bgSprite = TileRenderer.CreateRoundedRect(
                bgTexW, bgTexH, bgRadius,
                BOARD_INNER, FRAME_OUTER, framePx);

            GameObject bgGO = new GameObject("GridBackground");
            bgGO.transform.SetParent(_gridRoot.transform, false);
            bgGO.transform.position = new Vector3(
                (GridLeft + GridRight)  / 2f,
                (GridBottom + GridTop)  / 2f,
                0f);

            SpriteRenderer bgSR = bgGO.AddComponent<SpriteRenderer>();
            bgSR.sprite       = bgSprite;
            bgSR.sortingOrder = 0;
            _gridBackgroundSR = bgSR;

            // 2026-05-30 candy-bright pivot: custom material removed so the
            // BOARD_INNER / FRAME_OUTER colors render cleanly (the material
            // was overriding/multiplying the sprite color, blocking palette
            // changes). The default sprite material is used instead. The
            // .mat file is still on disk at Assets/Resources — re-enable by
            // restoring the Resources.Load below if needed.
            //
            // Material bgMat = Resources.Load<Material>("FeelSnakeWhiteParticlesMaterial");
            // if (bgMat != null) bgSR.material = bgMat;

            float nativeW = bgTexW / 100f;
            float nativeH = bgTexH / 100f;
            bgGO.transform.localScale = new Vector3(bgW / nativeW, bgH / nativeH, 1f);

            // 2026-06-24 Spencer: depth pass — overlay a subtle vertical gradient (soft sheen at the
            // top, grounding shade at the bottom) so the flat board reads as a LIT panel, not a blue
            // div. Generated as a ROUNDED-RECT matching the board (same texW/texH/radius) so its corners
            // follow the board's rounded corners — NOT a square overlay. Same scale as the board sprite
            // → aligns exactly. Sits over the fill, UNDER cells/tiles. Touches no tile/colour state.
            var gradGO = new GameObject("GridBackgroundGradient");
            gradGO.transform.SetParent(_gridRoot.transform, false);
            gradGO.transform.position = bgGO.transform.position + new Vector3(0f, 0f, -0.01f);
            var gradSR = gradGO.AddComponent<SpriteRenderer>();
            gradSR.sprite       = BuildBoardGradientSprite(bgTexW, bgTexH, bgRadius);
            gradSR.sortingOrder = bgSR.sortingOrder + 1;
            gradGO.transform.localScale = new Vector3(bgW / nativeW, bgH / nativeH, 1f); // same as board bg
        }

        /// <summary>Vertical-gradient board-depth sprite, generated as a ROUNDED-RECT (same texW/texH/
        /// radius as the board background) so its corners match — soft white sheen near the top,
        /// transparent middle, dark grounding shade near the bottom, transparent outside the corners.</summary>
        private static Sprite BuildBoardGradientSprite(int texW, int texH, int radius)
        {
            var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[texW * texH];
            for (int y = 0; y < texH; y++)
            {
                float t = y / (float)(texH - 1); // 0 = bottom, 1 = top
                Color g;
                if (t > 0.74f)      g = new Color(1f, 1f, 1f, Mathf.InverseLerp(0.74f, 1f, t) * 0.10f); // top sheen (tamer)
                else if (t < 0.30f) g = new Color(0f, 0f, 0f, Mathf.InverseLerp(0.30f, 0f, t) * 0.12f); // bottom shade — softer falloff
                else                g = new Color(0f, 0f, 0f, 0f);
                for (int x = 0; x < texW; x++)
                {
                    // Rounded-corner coverage so the overlay follows the board's rounded corners.
                    float dx = 0f, dy = 0f;
                    if (x < radius) dx = radius - 0.5f - x; else if (x > texW - 1 - radius) dx = x - (texW - 0.5f - radius);
                    if (y < radius) dy = radius - 0.5f - y; else if (y > texH - 1 - radius) dy = y - (texH - 0.5f - radius);
                    float cov = 1f;
                    if (dx > 0f && dy > 0f) { float d = Mathf.Sqrt(dx * dx + dy * dy); cov = Mathf.Clamp01(radius - d + 0.5f); }
                    Color c = g; c.a *= cov;
                    px[y * texW + x] = (Color32)c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, texW, texH), new Vector2(0.5f, 0.5f), 100f);
        }

        // ---------------------------------------------------------------------------
        // Public grid API
        // ---------------------------------------------------------------------------

        public Vector3 CellToWorld(int col, int row)
        {
            float x = GridLeft   + (col + 0.5f) * CellSize;
            float y = GridBottom + (row + 0.5f) * CellSize;
            return new Vector3(x, y, 0f);
        }

        /// <summary>MVP P5 booster aim mode: convert a world-space position to
        /// the grid cell beneath it. Returns false if the position is outside
        /// the board bounds.</summary>
        public bool WorldToCell(Vector3 worldPos, out int col, out int row)
        {
            col = Mathf.FloorToInt((worldPos.x - GridLeft) / CellSize);
            row = Mathf.FloorToInt((worldPos.y - GridBottom) / CellSize);
            if (col < 0 || col >= COLS) return false;
            if (row < 0 || row >= ROWS) return false;
            return true;
        }

        public Vector3 GetColumnSpawnPosition(int col)
        {
            float x = GridLeft + (col + 0.5f) * CellSize;
            float y = GridTop  + CellSize * 0.6f;
            return new Vector3(x, y, 0f);
        }

        public int GetLowestEmptyRow(int col)
        {
            if (col < 0 || col >= COLS) return -1;
            for (int row = 0; row < ROWS; row++)
                if (_cells[col, row] == null) return row;
            return -1;
        }

        public bool IsColumnAvailable(int col)
            => GetLowestEmptyRow(col) >= 0;

        public bool DropTile(int col, char letter, TileOwner owner)
        {
            Tile t = DropTileAndGetTile(col, letter, owner);
            return t != null;
        }

        public Tile DropTileAndGetTile(int col, char letter, TileOwner owner)
        {
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0)
            {
//                 Debug.Log($"[GridManager] Column {col} is full — ignoring drop.");
                return null;
            }

            CellData data = new CellData
            {
                Letter = letter,
                Col    = col,
                Row    = targetRow,
                Owner  = owner
            };
            _cells[col, targetRow] = data;

            int ownerIdx = (owner == TileOwner.AI) ? 1 : 0;
            Tile tile = CheckoutTile(letter, col, targetRow, CellSize, ownerIdx);

            _tiles[col, targetRow] = tile;

            Vector3 spawnPos  = GetColumnSpawnPosition(col);
            Vector3 targetPos = CellToWorld(col, targetRow);
            tile.AnimateFall(spawnPos, targetPos);

            // Gold bonus cells only apply to rising row tiles, not player/AI drops

//             Debug.Log($"[GridManager] Dropped '{letter}' → col={col}, row={targetRow}");

            return tile;
        }

        /// <summary>
        /// Creates a single tile at the specified grid position. Does NOT rebuild the whole board.
        /// Returns the created Tile so the caller can animate it.
        /// </summary>
        public Tile CreateSingleTile(int col, int row, char letter)
        {
            return CreateSingleTile(col, row, letter, isWild: false);
        }

        public Tile CreateSingleTile(int col, int row, char letter, bool isWild)
        {
            // Guards
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            if (!isWild && letter == '\0')
            {
                Debug.LogError($"[GridManager] CreateSingleTile: REJECTED blank tile at ({col},{row})");
                return null;
            }

            // Return existing tile to pool if any
            if (_tiles[col, row] != null)
            {
                ReturnTile(_tiles[col, row]);
                _tiles[col, row] = null;
                _cells[col, row] = null;
            }

            Vector3 worldPos = CellToWorld(col, row);
            // Uncommitted wilds pass the sentinel char so Tile.Initialise doesn't reject.
            char displayLetter = isWild && letter == '\0' ? TileBag.WILD_CHAR : letter;
            Tile tile = CheckoutTile(displayLetter, col, row, CellSize);
            tile.transform.position = worldPos;
            if (isWild) tile.SetWild(true);

            _tiles[col, row] = tile;
            _cells[col, row] = new CellData
            {
                Letter = isWild ? '\0' : letter,
                Col = col,
                Row = row,
                Owner = TileOwner.Player
            };

            return tile;
        }

        public void SetCell(int col, int row, CellData data)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return;
            _cells[col, row] = data;
        }

        public CellData GetCell(int col, int row)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            return _cells[col, row];
        }

        /// <summary>Bump or restore the board background sprite's sortingOrder.
        /// Used by BoosterHUDSlot's aim-mode scrim — when aim is active, the
        /// scrim canvas covers the board bg (which sits at sortingOrder=0)
        /// and dims it. Boosting the bg above the scrim's sortingOrder keeps
        /// the panel bright alongside the tiles.</summary>
        public void SetBoardBackgroundSortingOrder(int order)
        {
            if (_gridBackgroundSR != null) _gridBackgroundSR.sortingOrder = order;
        }

        /// <summary>World-space bounds of the board background sprite (the
        /// lavender rounded-rect panel behind the tiles). Used by the aim-mode
        /// scrim to compute its cutout from the actual rendered board geometry
        /// instead of hardcoded PSD constants.</summary>
        public Bounds? BoardBackgroundWorldBounds =>
            _gridBackgroundSR != null ? _gridBackgroundSR.bounds : (Bounds?)null;

        public Tile GetTile(int col, int row)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            return _tiles[col, row];
        }

        /// <summary>MVP P5: clear the tile reference + cell at (col, row) WITHOUT
        /// destroying the Tile GameObject. Used by boosters that reposition tiles
        /// (Wispwhirl shuffle). Caller is responsible for placing the tile back
        /// somewhere with PlaceTileAt, or the reference is leaked.</summary>
        public void ClearTileRefAt(int col, int row)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return;
            _tiles[col, row] = null;
            _cells[col, row] = null;
        }

        /// <summary>MVP P5: place an existing Tile reference at (col, row),
        /// update its internal Col/Row tracking. Does NOT move the GameObject —
        /// caller animates the move via DOTween or sets transform.position.</summary>
        public void PlaceTileAt(int col, int row, Tile tile, CellData cellData = null)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return;
            _tiles[col, row] = tile;
            _cells[col, row] = cellData;
            if (tile != null) tile.UpdateGridPosition(col, row);
        }

        /// <summary>
        /// Destroys all tile GameObjects and clears cell/tile arrays.
        /// </summary>
        public void ClearAllCells()
        {
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    if (_tiles[col, row] != null)
                        ReturnTile(_tiles[col, row]);
                    _cells[col, row] = null;
                    _tiles[col, row] = null;
                }

            // Clear bonus cell overlays
            for (int i = 0; i < _bonusOverlays.Count; i++)
                if (_bonusOverlays[i] != null) Destroy(_bonusOverlays[i]);
            _bonusOverlays.Clear();

//             Debug.Log("[GridManager] All cells cleared.");
        }

        /// <summary>
        /// FULL REBUILD: Destroys all visual tiles and recreates from RulesEngine data.
        /// This is the nuclear option — guarantees visuals match data with zero drift.
        /// </summary>
        public void RebuildFromRulesEngine(RulesEngine rules)
        {
            if (rules == null) return;

            // Return all existing tiles to pool
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    if (_tiles[col, row] != null)
                        ReturnTile(_tiles[col, row]);
                    _tiles[col, row] = null;
                    _cells[col, row] = null;
                }

            // Recreate tiles from RulesEngine board data
            int created = 0;
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var rulesCell = rules.GetCell(col, row);
                    if (rulesCell == null) continue;
                    // Auto-heal: if a cell has the wild sentinel '*' as a literal letter
                    // but IsWild is false, it's a corrupted wild from a pre-fix bug —
                    // promote to IsWild so ResolveUncommittedWilds can pick a letter.
                    if (rulesCell.Letter == TileBag.WILD_CHAR && !rulesCell.IsWild)
                    {
                        rulesCell.IsWild = true;
                        rulesCell.Letter = '\0';
                    }
                    // Skip genuinely empty cells, but keep uncommitted wilds (IsWild + Letter=='\0').
                    if (rulesCell.Letter == '\0' && !rulesCell.IsWild) continue;

                    Vector3 worldPos = CellToWorld(col, row);
                    // For uncommitted wilds pass the sentinel char so Tile.Initialise
                    // doesn't reject '\0'. SetWild below drives the ★ visual.
                    char displayLetter = (rulesCell.IsWild && rulesCell.Letter == '\0')
                        ? TileBag.WILD_CHAR : rulesCell.Letter;
                    Tile tile = CheckoutTile(displayLetter, col, row, CellSize, rulesCell.PlayerIndex);
                    tile.transform.position = worldPos;

                    _tiles[col, row] = tile;
                    _cells[col, row] = new CellData
                    {
                        Letter = rulesCell.Letter,
                        Col = col,
                        Row = row,
                        Owner = rulesCell.PlayerIndex == 0 ? TileOwner.Player : TileOwner.AI
                    };

                    // Special tile visuals
                    tile.SetAnchored(rulesCell.IsAnchored);
                    if (rulesCell.IsAnchored) { tile.SetVaultRequirement(rulesCell.RequiredWordLength); tile.SetVaultVisual(true); } // anchored = treasure vault
                    else if (rulesCell.IsDropTarget) tile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                    else if (rulesCell.IsStone || rulesCell.Letter == '#') tile.SetStoneVisual(true);
                    if (rulesCell.IsFrozen) tile.SetFrozenVisual(true);    // ICE: frost overlay (frozen tiles are normal letters)
                    if (rulesCell.IsSwapRefill) tile.SetSwapRefillVisual(true);
                    if (rulesCell.IsEditRefill) tile.SetEditRefillVisual(true);
                    if (rulesCell.IsWildRefill) tile.SetWildRefillVisual(true);
                    if (rulesCell.IsWild) tile.SetWild(true);
                    if (RulesEngine.Instance != null && RulesEngine.Instance.IsBonusCell(col, row))
                        tile.SetGoldBonus(true);

                    // Bouncy pop-in: each tile starts invisible (scale 0) and
                    // OutBack-eases to its target scale with a high overshoot so
                    // the letter punches past 1.0 and settles back. Slight per-
                    // tile stagger keyed by row + column gives a bottom-up,
                    // left-to-right wave that reads as the board materialising
                    // rather than blinking in. Tight (~0.28s per tile) so the
                    // whole board lands in well under a second.
                    Vector3 targetScale = tile.transform.localScale;
                    tile.transform.localScale = Vector3.zero;
                    float popDelay = (row * 0.012f) + (col * 0.020f);
                    tile.transform
                        .DOScale(targetScale, 0.28f)
                        .SetDelay(popDelay)
                        .SetEase(Ease.OutBack, 3.0f);

                    created++;
                }
            }

//             Debug.Log($"[GridManager] RebuildFromRulesEngine — created {created} tiles.");
        }

        /// <summary>Self-heal for the HeroWord escort object ("drop to the bottom"). A drop-target is
        /// a STONE (IsStone=true) under the hood, so a resolution path that pools + re-checks-out the
        /// tile on a frame where the data flag reads momentarily off can re-stone it grey — and no later
        /// sync re-ambers it before the player sees it (Spencer caught one sitting dark). This sweep
        /// re-applies the amber to any drop-target data cell whose tile lost the visual. Cheap
        /// (COLS×ROWS) and a no-op on non-HeroWord boards. Logs each heal so the root path can be
        /// pinned from a playtest log. 2026-06-15 Spencer. </summary>
        public void EnsureDropTargetVisuals(RulesEngine rules)
        {
            if (rules == null) return;
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    var cell = rules.GetCell(col, row);
                    if (cell == null || !cell.IsDropTarget) continue;
                    var tile = _tiles[col, row];
                    if (tile == null) continue;
                    if (!tile.IsDropTargetVisual)
                        Debug.LogWarning($"[DropTargetHeal] re-ambering escort at ({col},{row}) — its drop-target flag was cleared.");
                    // Force amber EVERY frame an escort exists — covers BOTH a cleared flag AND the
                    // case where a path overwrote the sprite colour grey while LEAVING the flag set
                    // (which the flag check above would miss). Cheap (≤ a couple tiles). 2026-06-15.
                    tile.SetDropTargetVisual(true);
                }
        }

        /// <summary>
        /// Syncs the visual grid to match the RulesEngine's board state exactly.
        /// Fixes any drift between data and visuals after async playback.
        /// Tiles that exist in data but are in wrong visual position get snapped.
        /// Tiles that exist visually but not in data get destroyed.
        /// </summary>
        public void SyncToRulesState(RulesEngine rules)
        {
            if (rules == null) return;

            int fixed_count = 0;

            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var rulesCell = rules.GetCell(col, row);
                    Tile visualTile = _tiles[col, row];

                    // Auto-heal corrupted wilds in-place before sync logic runs.
                    if (rulesCell != null && rulesCell.Letter == TileBag.WILD_CHAR && !rulesCell.IsWild)
                    {
                        rulesCell.IsWild = true;
                        rulesCell.Letter = '\0';
                    }

                    if (rulesCell == null && visualTile != null)
                    {
                        // Data says empty but visual has a tile — return to pool
                        Debug.LogWarning($"[BoosterDbg] Sync FIX ({col},{row}): data null + visual '{visualTile.Letter}' → returning to pool");
                        ReturnTile(visualTile);
                        _tiles[col, row] = null;
                        _cells[col, row] = null;
                        fixed_count++;
                    }
                    else if (rulesCell != null && visualTile != null)
                    {
                        // Both exist — snap position and sync letter if mismatched
                        Vector3 correctPos = CellToWorld(col, row);
                        if (Vector3.Distance(visualTile.transform.position, correctPos) > 0.01f)
                        {
                            Debug.LogWarning($"[BoosterDbg] Sync FIX ({col},{row}): pos drift " +
                                             $"({visualTile.transform.position.x:0.0},{visualTile.transform.position.y:0.0}) → " +
                                             $"({correctPos.x:0.0},{correctPos.y:0.0})");
                            visualTile.transform.position = correctPos;
                            fixed_count++;
                        }
                        // Sync wild flag (wild cells can get committed mid-resolution)
                        if (visualTile.IsWild != rulesCell.IsWild)
                        {
                            visualTile.SetWild(rulesCell.IsWild);
                            fixed_count++;
                        }
                        if ((rulesCell.Letter != '\0' || rulesCell.IsWild) && visualTile.Letter != rulesCell.Letter)
                        {
                            visualTile.SetLetter(rulesCell.Letter);
                            fixed_count++;
                        }
                        // Vaults: a live letter tile can be converted into a vault in-place
                        // (SeedVaultBoard flips IsStone+IsAnchored on an existing cell). Reflect it —
                        // anchored → treasure chest, plain stone → grey.
                        if (rulesCell.IsAnchored && !visualTile.IsVault)
                        {
                            visualTile.SetVaultRequirement(rulesCell.RequiredWordLength);
                            visualTile.SetVaultVisual(true); // also sets IsStone
                            fixed_count++;
                        }
                        else if (rulesCell.IsDropTarget && !visualTile.IsDropTargetVisual)
                        {
                            visualTile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                            fixed_count++;
                        }
                        else if (rulesCell.IsStone && !visualTile.IsStone)
                        {
                            visualTile.SetStoneVisual(true);
                            fixed_count++;
                        }
                        if (visualTile.IsAnchored != rulesCell.IsAnchored)
                        {
                            visualTile.SetAnchored(rulesCell.IsAnchored);
                            fixed_count++;
                        }
                        // ICE: frost overlay tracks RulesCellData.IsFrozen. Covers both freeze
                        // (objective spawn) and thaw (data cleared in DoExplode) — though thaw normally
                        // routes through GameVisualBridge's defrost VFX first; this is the safety sync.
                        if (visualTile.IsFrozen != rulesCell.IsFrozen)
                        {
                            visualTile.SetFrozenVisual(rulesCell.IsFrozen);
                            fixed_count++;
                        }
                    }
                    else if (rulesCell != null && visualTile == null
                             && (rulesCell.Letter != '\0' || rulesCell.IsWild))
                    {
                        // Data has a tile but visual doesn't — checkout from pool
                        Debug.LogWarning($"[BoosterDbg] Sync FIX ({col},{row}): data '{rulesCell.Letter}' + visual null → checking out from pool");
                        Vector3 worldPos = CellToWorld(col, row);
                        char displayLetter = (rulesCell.IsWild && rulesCell.Letter == '\0')
                            ? TileBag.WILD_CHAR : rulesCell.Letter;
                        Tile tile = CheckoutTile(displayLetter, col, row, CellSize, rulesCell.PlayerIndex);
                        tile.transform.position = worldPos;

                        tile.SetAnchored(rulesCell.IsAnchored);
                        if (rulesCell.IsAnchored) tile.SetVaultVisual(true);   // anchored = treasure vault (chest)
                        else if (rulesCell.IsDropTarget) tile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                        else if (rulesCell.IsStone || rulesCell.Letter == '#') tile.SetStoneVisual(true);
                        if (rulesCell.IsFrozen) tile.SetFrozenVisual(true);    // ICE: frost overlay (frozen tiles are normal letters)
                        if (rulesCell.IsSwapRefill) tile.SetSwapRefillVisual(true);
                        if (rulesCell.IsEditRefill) tile.SetEditRefillVisual(true);
                        if (rulesCell.IsWildRefill) tile.SetWildRefillVisual(true);
                        if (rulesCell.IsWild) tile.SetWild(true);
                        if (rules.IsBonusCell(col, row)) tile.SetGoldBonus(true);

                        _tiles[col, row] = tile;
                        _cells[col, row] = new CellData
                        {
                            Letter = rulesCell.Letter,
                            Col = col,
                            Row = row,
                            Owner = rulesCell.PlayerIndex == 0 ? TileOwner.Player : TileOwner.AI
                        };
                        fixed_count++;
                    }
                }
            }

        }

        public bool IsGridFull()
        {
            for (int col = 0; col < COLS; col++)
                if (IsColumnAvailable(col)) return false;
            return true;
        }

        public float GetColumnCenterX(int col)
            => GridLeft + (col + 0.5f) * CellSize;

        public int WorldXToColumn(float worldX)
        {
            if (worldX < GridLeft || worldX > GridRight) return -1;
            int col = Mathf.FloorToInt((worldX - GridLeft) / CellSize);
            return Mathf.Clamp(col, 0, COLS - 1);
        }

        /// <summary>
        /// Converts a world position to grid (col, row). Returns (-1,-1) if outside the grid.
        /// </summary>
        public Vector2Int WorldToCell(Vector3 worldPos)
        {
            if (worldPos.x < GridLeft || worldPos.x > GridRight) return new Vector2Int(-1, -1);
            if (worldPos.y < GridBottom || worldPos.y > GridTop) return new Vector2Int(-1, -1);
            int col = Mathf.FloorToInt((worldPos.x - GridLeft) / CellSize);
            int row = Mathf.FloorToInt((worldPos.y - GridBottom) / CellSize);
            col = Mathf.Clamp(col, 0, COLS - 1);
            row = Mathf.Clamp(row, 0, ROWS - 1);
            return new Vector2Int(col, row);
        }

        public bool IsYInTapZone(float worldY)
        {
            float tapZoneTop = GridTop + CellSize;
            return worldY >= GridBottom && worldY <= tapZoneTop;
        }

        // ---------------------------------------------------------------------------
        // Stub multiplier API
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Stub: multiplier system has been removed. Always returns 1.
        /// </summary>
        public int GetMultiplier(int col, int row) => 1;

        /// <summary>
        /// Stub: multiplier system has been removed. Always returns false.
        /// </summary>
        public bool HasMultiplier(int col, int row) => false;

        /// <summary>
        /// Stub: multiplier system has been removed. Returns empty dictionary.
        /// </summary>
        public IReadOnlyDictionary<(int, int), int> GetAllMultipliers()
            => new Dictionary<(int, int), int>();

        // ---------------------------------------------------------------------------
        // Gravity system
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Removes tiles at the given cell positions. Destroys their GameObjects
        /// and nulls out the cell/tile arrays. Does NOT apply gravity.
        /// </summary>
        public void RemoveTiles(List<Vector2Int> cells)
        {
            if (cells == null || cells.Count == 0) return;

            int removedCount = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                int col = cells[i].x;
                int row = cells[i].y;

                if (col < 0 || col >= COLS || row < 0 || row >= ROWS) continue;

                Tile tile = _tiles[col, row];
                if (tile != null)
                {
                    ReturnTile(tile);
                    removedCount++;
                }

                _tiles[col, row] = null;
                _cells[col, row] = null;
            }

//             Debug.Log($"[GridManager] RemoveTiles — removed {removedCount} tile(s) " +
                      // $"from {cells.Count} requested positions.");

            // Refresh bonus overlays in case any were consumed by scoring
            RefreshBonusCellOverlays();
        }

        /// <summary>
        /// For each column, compacts tiles downward to fill gaps left by removed tiles.
        /// Animates surviving tiles to their new positions.
        /// Yields until all animations complete.
        /// Preserves permanent/primed glow on tiles that move.
        /// </summary>
        public IEnumerator ApplyGravity()
        {
            // Per-column stagger — cascades feel organic instead of synchronized.
            // Candy Crush / Royal Match use ~40-60ms per column.
            // Phase 9.10: Level mode uses uniform (no-stagger, fixed-duration)
            // fall so cascade layers land in one beat rather than feeling
            // disjointed across columns / fall-distances. Survival keeps the
            // organic staggered feel for rising-row-driven gameplay.
            bool levelUniform = GameManager.IsLevelMode;
            const float STAGGER_PER_COL = 0.045f;
            // Match Tile.FALL_DURATION (0.30f) so post-detonation gravity
            // reads at the same pace as the player's initial tile drop.
            const float UNIFORM_FALL_DURATION = 0.30f;

            List<Tile> animatingTiles = new List<Tile>();
            int totalMoved = 0;

            for (int col = 0; col < COLS; col++)
            {
                // Collect all surviving tiles in this column, bottom to top,
                // tracking the actual row each was found at (needed for anchored rocks).
                List<Tile>     columnTiles = new List<Tile>();
                List<CellData> columnCells = new List<CellData>();
                List<int>      columnRows  = new List<int>();

                for (int row = 0; row < ROWS; row++)
                {
                    if (_tiles[col, row] != null && _cells[col, row] != null)
                    {
                        columnTiles.Add(_tiles[col, row]);
                        columnCells.Add(_cells[col, row]);
                        columnRows.Add(row);
                    }
                }

                if (columnTiles.Count == ROWS)
                    continue; // Column is fully packed — no gravity needed

                // Clear the entire column first
                for (int row = 0; row < ROWS; row++)
                {
                    _tiles[col, row] = null;
                    _cells[col, row] = null;
                }

                // Repack from row 0 (floor) upward. Break-Rocks: anchored rocks DON'T fall —
                // they stay at their found row and act as a fixed floor; other tiles compact
                // into the free rows around them. Mirrors RulesEngine.ApplyGravityInData so the
                // visual array stays index-aligned with the data layer (no desync). 2026-06-09.
                int writeRow = 0;
                for (int i = 0; i < columnTiles.Count; i++)
                {
                    Tile     tile   = columnTiles[i];
                    CellData cell   = columnCells[i];
                    int      oldRow = columnRows[i];
                    int      newRow;

                    if (tile.IsAnchored)
                    {
                        newRow   = oldRow;     // fixed — never moves
                        writeRow = oldRow + 1; // following tiles stack on top of it
                    }
                    else
                    {
                        newRow   = writeRow;
                        writeRow++;
                    }

                    // Update grid position tracking
                    cell.Row = newRow;
                    cell.Col = col;

                    _tiles[col, newRow] = tile;
                    _cells[col, newRow] = cell;

                    // Update the tile's internal position info
                    tile.UpdateGridPosition(col, newRow);

                    // Animate to new world position if it actually moved
                    Vector3 targetPos = CellToWorld(col, newRow);
                    float   dist      = Vector3.Distance(tile.transform.position, targetPos);

                    if (dist > 0.02f)
                    {
                        float fallDuration = levelUniform
                            ? UNIFORM_FALL_DURATION
                            : dist / GRAVITY_FALL_SPEED;
                        float colDelay = levelUniform ? 0f : col * STAGGER_PER_COL;
                        tile.AnimateGravityFall(targetPos, fallDuration, colDelay);
                        animatingTiles.Add(tile);
                        totalMoved++;
                    }
                    else
                    {
                        // Already in place — snap to exact position
                        tile.transform.position = targetPos;
                    }
                }
            }

            if (totalMoved > 0)
            {
//                 Debug.Log($"[GridManager] ApplyGravity — {totalMoved} tile(s) falling.");

                // Wait for all gravity animations to complete
                int  safety       = 0;
                bool anyAnimating = true;

                while (anyAnimating && safety < 600)
                {
                    safety++;
                    anyAnimating = false;
                    for (int i = 0; i < animatingTiles.Count; i++)
                    {
                        if (animatingTiles[i] != null && animatingTiles[i].IsAnimating)
                        {
                            anyAnimating = true;
                            break;
                        }
                    }
                    if (anyAnimating) yield return null;
                }

//                 Debug.Log("[GridManager] ApplyGravity — all tiles settled.");
            }
            else
            {
//                 Debug.Log("[GridManager] ApplyGravity — no tiles needed to move.");
            }
        }

        /// <summary>
        /// Event-driven gravity: moves specific tiles based on RulesEngine event data.
        /// More reliable than re-scanning because it uses the source-of-truth positions.
        /// </summary>
        public IEnumerator ApplyGravityFromEvents(Dictionary<Vector2Int, Vector2Int> tileMoves)
        {
            if (tileMoves == null || tileMoves.Count == 0)
            {
//                 Debug.Log("[GridManager] ApplyGravityFromEvents — no moves.");
                yield break;
            }

            // Per-column stagger — match ApplyGravity for consistent feel
            const float STAGGER_PER_COL = 0.045f;

            List<Tile> animatingTiles = new List<Tile>();

            foreach (var kvp in tileMoves)
            {
                int fromCol = kvp.Key.x;
                int fromRow = kvp.Key.y;
                int toCol   = kvp.Value.x;
                int toRow   = kvp.Value.y;

                Tile tile = _tiles[fromCol, fromRow];
                CellData cell = _cells[fromCol, fromRow];

                if (tile == null)
                {
                    // BoosterDbg: data says a tile fell from here, but visual
                    // array has nothing. That's a sign the visual layer drifted
                    // out of sync earlier — the falling tile is orphaned.
                    Debug.LogWarning($"[BoosterDbg] AGFE: source ({fromCol},{fromRow}) is null in _tiles — " +
                                     $"data wanted to move it to ({toCol},{toRow}). Skipping.");
                    continue;
                }

                // BoosterDbg: if the destination already has a tile in _tiles,
                // we're about to ORPHAN that tile (the existing reference gets
                // overwritten and the GameObject lingers in the scene without
                // an array reference). This is the most likely root cause of
                // "letters falling into other letters."
                if (_tiles[toCol, toRow] != null && _tiles[toCol, toRow] != tile)
                {
                    Debug.LogError($"[BoosterDbg] AGFE COLLISION: moving ({fromCol},{fromRow})→({toCol},{toRow}), " +
                                   $"but _tiles[{toCol},{toRow}] already has tile '{_tiles[toCol, toRow].Letter}'. " +
                                   $"Existing tile will be orphaned.");
                }

                // Clear old position
                _tiles[fromCol, fromRow] = null;
                _cells[fromCol, fromRow] = null;

                // Set new position
                if (cell != null)
                {
                    cell.Row = toRow;
                    cell.Col = toCol;
                }
                _tiles[toCol, toRow] = tile;
                _cells[toCol, toRow] = cell;

                tile.UpdateGridPosition(toCol, toRow);

                // Animate
                Vector3 targetPos = CellToWorld(toCol, toRow);
                float dist = Vector3.Distance(tile.transform.position, targetPos);

                if (dist > 0.02f)
                {
                    float fallDuration = dist / GRAVITY_FALL_SPEED;
                    float colDelay = toCol * STAGGER_PER_COL;
                    tile.AnimateGravityFall(targetPos, fallDuration, colDelay);
                    animatingTiles.Add(tile);
                }
                else
                {
                    tile.transform.position = targetPos;
                }
            }

            if (animatingTiles.Count > 0)
            {
//                 Debug.Log($"[GridManager] ApplyGravityFromEvents — {animatingTiles.Count} tile(s) falling.");

                int safety = 0;
                bool anyAnimating = true;
                while (anyAnimating && safety < 600)
                {
                    safety++;
                    anyAnimating = false;
                    for (int i = 0; i < animatingTiles.Count; i++)
                    {
                        if (animatingTiles[i] != null && animatingTiles[i].IsAnimating)
                        {
                            anyAnimating = true;
                            break;
                        }
                    }
                    if (anyAnimating) yield return null;
                }
            }

//             Debug.Log("[GridManager] ApplyGravityFromEvents — complete.");
        }

        // ---------------------------------------------------------------------------
        // Rising Row animation
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Animates all existing tiles shifting up one row, then creates new tiles
        /// at row 0 with a rise-from-below animation.
        /// Called by RisingRowManager after RulesEngine data has been shifted.
        /// </summary>
        public IEnumerator AnimateRiseRow(
            RulesEngine rules,
            Dictionary<Vector2Int, Vector2Int> shiftMoves,
            char[] newBottomLetters)
        {
            // 2026-05-28: shift at 0.18s — completes BEFORE the new tile
            // reaches its pop overshoot peak (~0.26s with period 0.30 at
            // the longer 0.85s duration).
            const float RISE_DURATION = 0.18f;
            const float RISE_SPEED = 1f / RISE_DURATION;

            // 1. Shift existing visual tiles up using the move dictionary
            //    Process in reverse row order (top first) to avoid overwriting
            for (int row = ROWS - 1; row >= 0; row--)
            {
                for (int col = 0; col < COLS; col++)
                {
                    var oldPos = new Vector2Int(col, row);
                    if (!shiftMoves.ContainsKey(oldPos)) continue;

                    var newPos = shiftMoves[oldPos];
                    Tile tile = _tiles[col, row];
                    CellData cell = _cells[col, row];

                    if (tile == null) continue;

                    // Clear old position in visual grid
                    _tiles[col, row] = null;
                    _cells[col, row] = null;

                    // Update tile tracking
                    tile.UpdateGridPosition(newPos.x, newPos.y);
                    if (cell != null)
                    {
                        cell.Row = newPos.y;
                        cell.Col = newPos.x;
                    }

                    // Place in new position
                    _tiles[newPos.x, newPos.y] = tile;
                    _cells[newPos.x, newPos.y] = cell;

                    // 2026-05-29: existing-tile shift now has a subtle single
                    // overshoot (OutBack, magnitude 0.15) instead of a linear
                    // mechanical slide. Adds a hint of aliveness — each tile
                    // lifts past its target then settles — without competing
                    // with the new bottom row's OutElastic pop. Tuning:
                    //   overshoot 0.0  = original linear (boring)
                    //   overshoot 0.15 = barely-perceptible lift (current)
                    //   overshoot 0.4  = noticeable lift (probably too much)
                    Vector3 targetPos = CellToWorld(newPos.x, newPos.y);
                    float dist = Vector3.Distance(tile.transform.position, targetPos);
                    if (dist > 0.01f)
                    {
                        float dur = RISE_DURATION;
                        tile.transform.DOKill();
                        tile.transform.DOMove(targetPos, dur)
                            .SetEase(Ease.OutBack, 0.15f);
                    }
                }
            }

            // 2. Spawn new bottom-row tiles SIMULTANEOUSLY with the existing-
            // tiles shift — the row pushes up and the new tiles burst out of
            // the bottom in the same moment. Reads as one event.
            // 2026-05-28: switched from "spawn below, slide up" to "spawn at
            // target, pop with OutElastic" — snappier sprout feel per
            // Spencer's brief. Per his clarification: pop fires concurrently
            // with the shift, not after.
            if (newBottomLetters != null)
            {
                // Pop curve is now shared with the hand-deal pop via
                // UIAnimations.NewTilePop — any tuning of the sprout feel
                // (amplitude, period, easing, baseline duration) propagates
                // to every "new tile/card arrives" moment in the game.
                float POP_DURATION = UIAnimations.NEW_TILE_POP_DURATION;
                List<Tween> popTweens = new List<Tween>();

                for (int col = 0; col < COLS && col < newBottomLetters.Length; col++)
                {
                    char letter = newBottomLetters[col];
                    if (letter == '\0') continue; // intentional gap

                    Vector3 targetPos = CellToWorld(col, 0);

                    Tile tile = CheckoutTile(letter, col, 0, CellSize, -1);
                    tile.transform.position = targetPos;

                    // Capture the natural scale CheckoutTile set, then collapse
                    // to zero so the pop tween animates back to it.
                    Vector3 finalScale = tile.transform.localScale;
                    tile.transform.localScale = Vector3.zero;

                    _tiles[col, 0] = tile;
                    _cells[col, 0] = new CellData
                    {
                        Letter = letter,
                        Col    = col,
                        Row    = 0,
                        Owner  = TileOwner.Player
                    };

                    bool isStone = (letter == '#');
                    if (!isStone && RulesEngine.Instance != null)
                    {
                        var cellData = RulesEngine.Instance.GetCell(col, 0);
                        if (cellData != null && cellData.IsStone)
                            isStone = true;
                    }
                    if (isStone)
                    {
                        var rockCell = RulesEngine.Instance?.GetCell(col, 0);
                        bool anchored = rockCell != null && rockCell.IsAnchored;
                        tile.SetAnchored(anchored);
                        if (anchored) { tile.SetVaultRequirement(rockCell != null ? rockCell.RequiredWordLength : 0); tile.SetVaultVisual(true); } // anchored = treasure vault
                        else if (rockCell != null && rockCell.IsDropTarget) tile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                        else          tile.SetStoneVisual(true);
                    }
                    else if (letter == '\0' || letter == '#')
                    {
                        Debug.LogError($"[GridManager] BAD TILE at ({col},0): letter='{letter}' isStone={isStone}");
                    }

                    if (!isStone && RulesEngine.Instance != null && RulesEngine.Instance.IsBonusCell(col, 0))
                        tile.SetGoldBonus(true);

                    // Push the new tile BEHIND the row-above tiles during the
                    // pop. As the row above shifts up, the growing tile
                    // emerges from underneath it — reads as "the new row is
                    // pushing the existing rows up because it's growing
                    // beneath them" instead of materializing in empty space.
                    //
                    // Important: lower ALL the tile's renderers uniformly
                    // (sprite body, shadow, letter, point) so the INTERNAL
                    // sorting (shadow behind body, text in front of body)
                    // is preserved while the whole stack drops behind the
                    // row above. Was just SetSortingOrder(-5) before, which
                    // only adjusted body+text — left the shadow (order 4) in
                    // front of the body (-5), tinting the tile dark.
                    Tile capturedTile = tile;
                    // Offset just deep enough to drop BEHIND row-above tiles
                    // (body sortingOrder=5) but stay ABOVE the board panel
                    // (sortingOrder=0). With offset -3: body 5→2, shadow 4→1,
                    // letter 6→3, points 6→3. Was -10 — pushed the tile
                    // behind the board, making it invisible during the pop;
                    // then sorting restore on complete read as a sudden
                    // light-up flash.
                    const int Z_OFFSET = 3;

                    var caprSprites = capturedTile.GetComponentsInChildren<SpriteRenderer>(true);
                    var caprTexts   = capturedTile.GetComponentsInChildren<TMPro.TextMeshPro>(true);
                    int[] origSpriteOrders = new int[caprSprites.Length];
                    int[] origTextOrders   = new int[caprTexts.Length];
                    for (int s = 0; s < caprSprites.Length; s++)
                    {
                        origSpriteOrders[s] = caprSprites[s].sortingOrder;
                        caprSprites[s].sortingOrder = origSpriteOrders[s] - Z_OFFSET;
                    }
                    for (int t = 0; t < caprTexts.Length; t++)
                    {
                        origTextOrders[t] = caprTexts[t].sortingOrder;
                        caprTexts[t].sortingOrder = origTextOrders[t] - Z_OFFSET;
                    }

                    // Curve identity (OutElastic + amplitude + period) lives
                    // in UIAnimations.NewTilePop so it stays in sync with the
                    // hand-deal pop. sortingOrder restore is per-site (hand
                    // cards don't use this trick) so it's wrapped here.
                    Tween popTween = UIAnimations.NewTilePop(
                        tile.transform,
                        finalScale,
                        speedMult: 1f,
                        onComplete: () => {
                            if (capturedTile == null) return;
                            for (int s = 0; s < caprSprites.Length; s++)
                                if (caprSprites[s] != null)
                                    caprSprites[s].sortingOrder = origSpriteOrders[s];
                            for (int t = 0; t < caprTexts.Length; t++)
                                if (caprTexts[t] != null)
                                    caprTexts[t].sortingOrder = origTextOrders[t];
                        });
                    if (popTween != null) popTweens.Add(popTween);
                }

                // Total animation length = max(shift, pop). They run together
                // so we just wait long enough for both to finish.
                if (popTweens.Count > 0)
                    yield return new WaitForSeconds(Mathf.Max(RISE_DURATION, POP_DURATION) + 0.05f);
            }
        }

        // ── Board Blessing visual overlays ──────────────────────────────────────

        private List<GameObject> _bonusOverlays = new List<GameObject>();

        /// <summary>
        /// Refresh bonus cell visual overlays to match RulesEngine bonus state.
        /// Call after rising rows or board clear.
        /// </summary>
        /// <summary>
        /// Gold status lives on the tile — this only applies gold to NEW tiles on bonus cells.
        /// Does NOT remove gold from existing tiles (gold persists until used in a word).
        /// </summary>
        public void RefreshBonusCellOverlays()
        {
            // Clear old overlays (legacy — no longer used)
            for (int i = 0; i < _bonusOverlays.Count; i++)
                if (_bonusOverlays[i] != null) Destroy(_bonusOverlays[i]);
            _bonusOverlays.Clear();

            if (RulesEngine.Instance == null) return;

            // Gold is only granted during rising rows — don't re-apply here
            // Existing gold tiles keep their status through gravity/movement
        }

        private Sprite _cachedBonusSprite;

        private Sprite CreateBonusCellSprite()
        {
            if (_cachedBonusSprite != null) return _cachedBonusSprite;

            int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            float half = size / 2f;
            float borderWidth = 3f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x - half) / half;
                    float dy = (y - half) / half;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy); // circular, not square

                    // Soft radial glow — bright center fading to transparent edges
                    float glow = Mathf.Clamp01(1f - dist * 1.1f);
                    glow = glow * glow; // quadratic falloff for softer edges
                    // Thin border ring
                    float ring = Mathf.Clamp01(1f - Mathf.Abs(dist - 0.7f) / 0.1f) * 0.4f;
                    float alpha = Mathf.Clamp01(glow + ring);

                    tex.SetPixel(x, y, new Color(1f, 0.95f, 0.8f, alpha));
                }
            }
            tex.Apply();

            _cachedBonusSprite = Sprite.Create(tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);
            return _cachedBonusSprite;
        }

        /// <summary>
        /// Returns the number of occupied cells on the board.
        /// </summary>
        public int CountOccupiedCells()
        {
            int count = 0;
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    if (_cells[col, row] != null) count++;
            return count;
        }
    }

    // ---------------------------------------------------------------------------
    // Supporting types
    // ---------------------------------------------------------------------------

    public enum TileOwner
    {
        Player,
        AI
    }

    public class CellData
    {
        public char      Letter { get; set; }
        public int       Col    { get; set; }
        public int       Row    { get; set; }
        public TileOwner Owner  { get; set; }
    }
}
