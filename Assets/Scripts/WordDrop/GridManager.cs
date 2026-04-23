using UnityEngine;
using System.Collections;
using System.Collections.Generic;

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
        private const float SURVIVAL_GRID_WIDTH_FRACTION  = 0.84f;
        private const float DEFAULT_GRID_WIDTH_FRACTION   = 0.78f;
        private const float SURVIVAL_GRID_TOP_MARGIN      = 0.30f; // push grid down — more room above for HUD, thumb-friendly
        private const float GRAVITY_FALL_SPEED            = 14f; // was 10 originally — slightly faster

        // Board: deep indigo hero object — darker and cooler than background
        private static readonly Color FRAME_OUTER    = new Color(0.040f, 0.055f, 0.150f, 1f);  // very dark indigo outer edge
        private static readonly Color FRAME_EDGE     = new Color(0.220f, 0.270f, 0.540f, 1f);  // brighter top lip — sculpted
        private static readonly Color BOARD_INNER    = new Color(0.040f, 0.055f, 0.150f, 1f);  // matches FRAME_OUTER — single dark panel

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

        // ---------------------------------------------------------------------------
        // Private refs
        // ---------------------------------------------------------------------------

        private Camera     _cam;
        private GameObject _gridRoot;

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

        private void CalculateLayout()
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);
            bool isSurvival = SurvivalManager.IsSurvivalMode;

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

            float bgPadding = CellSize * 0.16f;
            CreateBackgroundPanel(bgPadding);

            int texSize = Mathf.Clamp(Mathf.RoundToInt(CellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // slightly rounder corners
            int border  = Mathf.Max(3, texSize / 14);  // thicker border for stronger inset

            Sprite cellSprite = TileRenderer.CreateRoundedRect(
                texSize, texSize, radius,
                CELL_FILL_COLOR, CELL_BORDER_COLOR, border);

            // Cell background squares hidden — just the dark panel behind tiles
            // Keep the _cellObjects array populated with empty GOs for position tracking
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    Vector3 worldPos = CellToWorld(col, row);

                    GameObject cellGO = new GameObject($"Cell_{col}_{row}");
                    cellGO.transform.SetParent(_gridRoot.transform, false);
                    cellGO.transform.position = worldPos;

                    // No SpriteRenderer — cells are invisible
                    _cellObjects[col, row] = cellGO;
                    _cells[col, row]       = null;
                }
            }
        }

        private void CreateBackgroundPanel(float padding)
        {
            float bgW = (GridRight  - GridLeft)  + padding * 2f;
            float bgH = (GridTop    - GridBottom) + padding * 2f;

            int bgTexW   = Mathf.Clamp(Mathf.RoundToInt(bgW * 150f), 64, 1024);
            int bgTexH   = Mathf.Clamp(Mathf.RoundToInt(bgH * 150f), 64, 1024);
            int bgRadius = Mathf.Min(bgTexW, bgTexH) / 10;
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

            float nativeW = bgTexW / 100f;
            float nativeH = bgTexH / 100f;
            bgGO.transform.localScale = new Vector3(bgW / nativeW, bgH / nativeH, 1f);
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

        public Tile GetTile(int col, int row)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            return _tiles[col, row];
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
                    if (rulesCell.IsStone || rulesCell.Letter == '#') tile.SetStoneVisual(true);
                    if (rulesCell.IsSwapRefill) tile.SetSwapRefillVisual(true);
                    if (rulesCell.IsEditRefill) tile.SetEditRefillVisual(true);
                    if (rulesCell.IsWildRefill) tile.SetWildRefillVisual(true);
                    if (rulesCell.IsWild) tile.SetWild(true);
                    if (RulesEngine.Instance != null && RulesEngine.Instance.IsBonusCell(col, row))
                        tile.SetGoldBonus(true);

                    created++;
                }
            }

//             Debug.Log($"[GridManager] RebuildFromRulesEngine — created {created} tiles.");
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
                    }
                    else if (rulesCell != null && visualTile == null
                             && (rulesCell.Letter != '\0' || rulesCell.IsWild))
                    {
                        // Data has a tile but visual doesn't — checkout from pool
                        Vector3 worldPos = CellToWorld(col, row);
                        char displayLetter = (rulesCell.IsWild && rulesCell.Letter == '\0')
                            ? TileBag.WILD_CHAR : rulesCell.Letter;
                        Tile tile = CheckoutTile(displayLetter, col, row, CellSize, rulesCell.PlayerIndex);
                        tile.transform.position = worldPos;

                        if (rulesCell.IsStone || rulesCell.Letter == '#') tile.SetStoneVisual(true);
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
                // Collect all surviving tiles in this column, bottom to top
                List<Tile>     columnTiles = new List<Tile>();
                List<CellData> columnCells = new List<CellData>();

                for (int row = 0; row < ROWS; row++)
                {
                    if (_tiles[col, row] != null && _cells[col, row] != null)
                    {
                        columnTiles.Add(_tiles[col, row]);
                        columnCells.Add(_cells[col, row]);
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

                // Repack from row 0 (floor) upward
                for (int i = 0; i < columnTiles.Count; i++)
                {
                    int      newRow = i;
                    Tile     tile   = columnTiles[i];
                    CellData cell   = columnCells[i];

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

                if (tile == null) continue;

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
            const float RISE_DURATION = 0.25f;
            const float RISE_SPEED = 1f / RISE_DURATION;

            List<Tile> animatingTiles = new List<Tile>();

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

                    // Animate to new world position — mechanical shift, matches the
                    // new-bottom rise so the whole event reads as one machine motion
                    Vector3 targetPos = CellToWorld(newPos.x, newPos.y);
                    float dist = Vector3.Distance(tile.transform.position, targetPos);
                    if (dist > 0.01f)
                    {
                        float dur = RISE_DURATION;
                        tile.AnimateGravityFall(targetPos, dur, 0f, mechanical: true);
                        animatingTiles.Add(tile);
                    }
                }
            }

            // 2. Create new tiles at row 0 — spawn below the grid and rise up
            if (newBottomLetters != null)
            {
                for (int col = 0; col < COLS && col < newBottomLetters.Length; col++)
                {
                    char letter = newBottomLetters[col];
                    if (letter == '\0') continue; // intentional gap — no tile in this column
                    Vector3 targetPos = CellToWorld(col, 0);
                    Vector3 spawnPos = new Vector3(targetPos.x, GridBottom - CellSize * 0.8f, targetPos.z);

                    Tile tile = CheckoutTile(letter, col, 0, CellSize, -1);
                    tile.transform.position = spawnPos;

                    _tiles[col, 0] = tile;
                    _cells[col, 0] = new CellData
                    {
                        Letter = letter,
                        Col = col,
                        Row = 0,
                        Owner = TileOwner.Player // neutral tiles display as player style
                    };

                    // Stone tile visual — check both the data layer AND the letter
                    bool isStone = (letter == '#');
                    if (!isStone && RulesEngine.Instance != null)
                    {
                        var cellData = RulesEngine.Instance.GetCell(col, 0);
                        if (cellData != null && cellData.IsStone)
                            isStone = true;
                    }
                    if (isStone)
                    {
                        tile.SetStoneVisual(true);
//                         Debug.Log($"[GridManager] Stone visual applied at ({col},0) letter='{letter}'");
                    }
                    else if (letter == '\0' || letter == '#')
                    {
                        // Safety: should never reach here — log if we do
                        Debug.LogError($"[GridManager] BAD TILE at ({col},0): letter='{letter}' isStone={isStone}");
                    }

                    // Gold bonus: only rising row tiles can become gold
                    if (!isStone && RulesEngine.Instance != null && RulesEngine.Instance.IsBonusCell(col, 0))
                        tile.SetGoldBonus(true);

                    // Mechanical rise — linear, hard-stop. Machine pushing a row up,
                    // not an organic drop. Uniform across columns (no stagger).
                    tile.AnimateGravityFall(targetPos, RISE_DURATION, 0f, mechanical: true);
                    animatingTiles.Add(tile);
                }
            }

            // 3. Wait for all animations to complete
            if (animatingTiles.Count > 0)
            {
//                 Debug.Log($"[GridManager] AnimateRiseRow — {animatingTiles.Count} tile(s) rising.");

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

//                 Debug.Log("[GridManager] AnimateRiseRow — all tiles settled.");
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
