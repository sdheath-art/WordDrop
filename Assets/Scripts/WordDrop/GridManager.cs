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

        public const int COLS = 7;
        public const int ROWS = 5;

        private const float GRID_HEIGHT_FRACTION = 0.82f;  // denser, less bulky
        private const float GRAVITY_FALL_SPEED   = 14f; // was 10 originally — slightly faster

        // Board: deep indigo hero object — darker and cooler than background
        private static readonly Color FRAME_OUTER    = new Color(0.040f, 0.055f, 0.150f, 1f);  // very dark indigo outer edge
        private static readonly Color FRAME_EDGE     = new Color(0.220f, 0.270f, 0.540f, 1f);  // brighter top lip — sculpted
        private static readonly Color BOARD_INNER    = new Color(0.080f, 0.110f, 0.280f, 1f);  // deep cool indigo

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

        private CellData[,]    _cells      = new CellData[COLS, ROWS];
        private Tile[,]        _tiles      = new Tile[COLS, ROWS];
        private GameObject[,]  _cellObjects = new GameObject[COLS, ROWS];

        // ---------------------------------------------------------------------------
        // Private refs
        // ---------------------------------------------------------------------------

        private Camera     _cam;
        private GameObject _gridRoot;

        private readonly List<GameObject> _allTileObjects = new List<GameObject>();

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

            Debug.Log($"[GridManager] Awake — cellSize={CellSize:F3}, " +
                      $"gridLeft={GridLeft:F3}, gridBottom={GridBottom:F3}");
        }

        // ---------------------------------------------------------------------------
        // Layout calculation
        // ---------------------------------------------------------------------------

        private void CalculateLayout()
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);

            float availableWidth  = halfW * 2f * 0.90f;
            float cellFromWidth   = availableWidth / COLS;

            float availableHeight = halfH * 2f * GRID_HEIGHT_FRACTION;
            float cellFromHeight  = availableHeight / ROWS;

            CellSize = Mathf.Min(cellFromWidth, cellFromHeight);

            float gridWorldWidth  = CellSize * COLS;
            float gridWorldHeight = CellSize * ROWS;

            float gridCenterX = 0f;
            float gridCenterY = halfH * 0.14f;  // push grid up to reduce dead space above, make room below

            GridLeft   = gridCenterX - gridWorldWidth  / 2f;
            GridRight  = gridCenterX + gridWorldWidth  / 2f;
            GridBottom = gridCenterY - gridWorldHeight / 2f;
            GridTop    = gridCenterY + gridWorldHeight / 2f;
        }

        // ---------------------------------------------------------------------------
        // Visual construction
        // ---------------------------------------------------------------------------

        private void BuildGridVisuals()
        {
            _gridRoot = new GameObject("GridRoot");
            _gridRoot.transform.SetParent(transform, false);

            float bgPadding = CellSize * 0.16f;  // sculpted frame
            CreateBackgroundPanel(bgPadding);

            int texSize = Mathf.Clamp(Mathf.RoundToInt(CellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // slightly rounder corners
            int border  = Mathf.Max(3, texSize / 14);  // thicker border for stronger inset

            Sprite cellSprite = TileRenderer.CreateRoundedRect(
                texSize, texSize, radius,
                CELL_FILL_COLOR, CELL_BORDER_COLOR, border);

            float cellDisplaySize = CellSize * 0.90f;

            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    Vector3 worldPos = CellToWorld(col, row);

                    GameObject cellGO = new GameObject($"Cell_{col}_{row}");
                    cellGO.transform.SetParent(_gridRoot.transform, false);
                    cellGO.transform.position = worldPos;

                    SpriteRenderer sr = cellGO.AddComponent<SpriteRenderer>();
                    sr.sprite         = cellSprite;
                    sr.sortingOrder   = 1;

                    float nativeSize = texSize / 100f;
                    float scale      = cellDisplaySize / nativeSize;
                    cellGO.transform.localScale = new Vector3(scale, scale, 1f);

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
                Debug.Log($"[GridManager] Column {col} is full — ignoring drop.");
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

            GameObject tileGO = new GameObject($"Tile_{col}_{targetRow}_{letter}");
            _allTileObjects.Add(tileGO);

            Tile tile = tileGO.AddComponent<Tile>();
            tile.Initialise(letter, col, targetRow, CellSize);

            _tiles[col, targetRow] = tile;

            Vector3 spawnPos  = GetColumnSpawnPosition(col);
            Vector3 targetPos = CellToWorld(col, targetRow);
            tile.AnimateFall(spawnPos, targetPos);

            Debug.Log($"[GridManager] Dropped '{letter}' → col={col}, row={targetRow}");

            return tile;
        }

        /// <summary>
        /// Creates a single tile at the specified grid position. Does NOT rebuild the whole board.
        /// Returns the created Tile so the caller can animate it.
        /// </summary>
        public Tile CreateSingleTile(int col, int row, char letter)
        {
            // Remove existing tile at this position if any
            if (_tiles[col, row] != null)
            {
                GameObject old = _tiles[col, row].gameObject;
                _allTileObjects.Remove(old);
                Destroy(old);
                _tiles[col, row] = null;
                _cells[col, row] = null;
            }

            Vector3 worldPos = CellToWorld(col, row);

            GameObject tileGO = new GameObject($"Tile_{letter}_{col}_{row}");
            tileGO.transform.position = worldPos;

            Tile tile = tileGO.AddComponent<Tile>();
            tile.Initialise(letter, col, row, CellSize);

            _tiles[col, row] = tile;
            _cells[col, row] = new CellData
            {
                Letter = letter,
                Col = col,
                Row = row,
                Owner = TileOwner.Player
            };
            _allTileObjects.Add(tileGO);

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
            foreach (GameObject go in _allTileObjects)
                if (go != null) Destroy(go);
            _allTileObjects.Clear();

            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    _cells[col, row] = null;
                    _tiles[col, row] = null;
                }

            Debug.Log("[GridManager] All cells cleared.");
        }

        /// <summary>
        /// FULL REBUILD: Destroys all visual tiles and recreates from RulesEngine data.
        /// This is the nuclear option — guarantees visuals match data with zero drift.
        /// </summary>
        public void RebuildFromRulesEngine(RulesEngine rules)
        {
            if (rules == null) return;

            // Destroy all existing visual tiles
            foreach (GameObject go in _allTileObjects)
                if (go != null) Object.Destroy(go);
            _allTileObjects.Clear();

            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
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

                    Vector3 worldPos = CellToWorld(col, row);

                    // Create tile GameObject
                    GameObject tileGO = new GameObject($"Tile_{rulesCell.Letter}_{col}_{row}");
                    tileGO.transform.position = worldPos;

                    Tile tile = tileGO.AddComponent<Tile>();
                    tile.Initialise(rulesCell.Letter, col, row, CellSize);

                    // Store in grid
                    _tiles[col, row] = tile;
                    _cells[col, row] = new CellData
                    {
                        Letter = rulesCell.Letter,
                        Col = col,
                        Row = row,
                        Owner = rulesCell.PlayerIndex == 0 ? TileOwner.Player : TileOwner.AI
                    };
                    _allTileObjects.Add(tileGO);
                    created++;
                }
            }

            Debug.Log($"[GridManager] RebuildFromRulesEngine — created {created} tiles.");
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

                    if (rulesCell == null && visualTile != null)
                    {
                        // Data says empty but visual has a tile — destroy it
                        GameObject go = visualTile.gameObject;
                        _allTileObjects.Remove(go);
                        Destroy(go);
                        _tiles[col, row] = null;
                        _cells[col, row] = null;
                        fixed_count++;
                    }
                    else if (rulesCell != null && visualTile != null)
                    {
                        // Both exist — snap position to correct world pos
                        Vector3 correctPos = CellToWorld(col, row);
                        if (Vector3.Distance(visualTile.transform.position, correctPos) > 0.01f)
                        {
                            visualTile.transform.position = correctPos;
                            fixed_count++;
                        }
                    }
                    else if (rulesCell != null && visualTile == null)
                    {
                        // Data has a tile but visual doesn't — create it
                        Vector3 worldPos = CellToWorld(col, row);
                        GameObject tileGO = new GameObject($"Tile_{rulesCell.Letter}_{col}_{row}");
                        tileGO.transform.position = worldPos;
                        Tile tile = tileGO.AddComponent<Tile>();
                        tile.Initialise(rulesCell.Letter, col, row, CellSize);
                        _tiles[col, row] = tile;
                        _cells[col, row] = new CellData
                        {
                            Letter = rulesCell.Letter,
                            Col = col,
                            Row = row,
                            Owner = rulesCell.PlayerIndex == 0 ? TileOwner.Player : TileOwner.AI
                        };
                        _allTileObjects.Add(tileGO);
                        fixed_count++;
                    }
                }
            }

            if (fixed_count > 0)
                Debug.Log($"[GridManager] SyncToRulesState — fixed {fixed_count} tile(s).");
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
                    GameObject go = tile.gameObject;
                    _allTileObjects.Remove(go);
                    Destroy(go);
                    removedCount++;
                }

                _tiles[col, row] = null;
                _cells[col, row] = null;
            }

            Debug.Log($"[GridManager] RemoveTiles — removed {removedCount} tile(s) " +
                      $"from {cells.Count} requested positions.");
        }

        /// <summary>
        /// For each column, compacts tiles downward to fill gaps left by removed tiles.
        /// Animates surviving tiles to their new positions.
        /// Yields until all animations complete.
        /// Preserves permanent/primed glow on tiles that move.
        /// </summary>
        public IEnumerator ApplyGravity()
        {
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
                        float fallDuration = dist / GRAVITY_FALL_SPEED;
                        tile.AnimateGravityFall(targetPos, fallDuration);
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
                Debug.Log($"[GridManager] ApplyGravity — {totalMoved} tile(s) falling.");

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

                Debug.Log("[GridManager] ApplyGravity — all tiles settled.");
            }
            else
            {
                Debug.Log("[GridManager] ApplyGravity — no tiles needed to move.");
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
                Debug.Log("[GridManager] ApplyGravityFromEvents — no moves.");
                yield break;
            }

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
                    tile.AnimateGravityFall(targetPos, fallDuration);
                    animatingTiles.Add(tile);
                }
                else
                {
                    tile.transform.position = targetPos;
                }
            }

            if (animatingTiles.Count > 0)
            {
                Debug.Log($"[GridManager] ApplyGravityFromEvents — {animatingTiles.Count} tile(s) falling.");

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

            Debug.Log("[GridManager] ApplyGravityFromEvents — complete.");
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
