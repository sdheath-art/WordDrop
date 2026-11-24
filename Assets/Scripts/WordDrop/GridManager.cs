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

        // 2026-09-03: these were a SECOND hardcoded copy of the grid size (6 x 9)
        // that silently disagreed with RulesEngine once the grid changed — the
        // loops ran to RulesEngine's size while these arrays stayed 6x9, giving
        // IndexOutOfRangeException in BuildGridVisuals. Single source of truth now.
        // ── Toon-Blast style vertical OVERLAP (2026-09-03) ───────────────────────
        // Tile art is 0.782 w/h (taller than wide) with a 15.6% "top face" that is
        // meant to be HIDDEN behind the tile above, so the board reads as stacked
        // blocks rather than flat cards. Column pitch = CellSize (tiles butt up
        // edge to edge). ROW pitch is SHORTER than the tile is tall:
        //     tile height = CellSize / 0.782      = 1.279 * CellSize
        //     row pitch   = tile height * (1-0.156) = 1.079 * CellSize
        // Upper rows must also DRAW IN FRONT or every top face shows and it reads
        // as flat cards again.
        // Solved from Spencer's measurement of Toon Blast's own board (1143x1166
        // board / 1121x1146 playable, 9x9, on a 1179x2556 screen):
        //   grid pitch 124.56 SQUARE  ·  tile 124.56 x 149.56  ·  aspect 0.833
        //   overlap 25px = 16.7% of tile height
        // So the pitch is a PLAIN SQUARE GRID — the overlap comes entirely from the
        // tile sprite being TALLER than its cell. (An earlier pass used a separate
        // non-square row pitch from a 0.782 aspect measured off a 70px screenshot
        // crop; that was wrong and made the tiles look tall and skinny.)
        /// <summary>Tile width / height, taken from the ACTUAL art. 2026-09-15: the tile is now
        /// rendered FRONT-ON at 249x239, so 249/239 = 1.0418. It was **0.833** (= 249/299), which
        /// described the old TILTED art whose extra ~20% height was the top face.
        ///
        /// Two places derive from it and BOTH were adding phantom height at the top:
        ///   playH        = CellSize * ((ROWS-1) + 1/TILE_ASPECT)   -- reserved 1.20 cells for the
        ///                  top row instead of 0.96, so the board carried 0.24 of a cell of dead
        ///                  space up there and the top margin read fatter than the bottom.
        ///   overshootHalf= ((1/TILE_ASPECT) - 1) * 0.5             -- biased EVERY row up by 0.10
        ///                  of a cell to let the top face stick up behind the tile above.
        /// At 1.0418 the first becomes 0.96 and the second becomes -0.02, i.e. both effectively
        /// vanish — margins equal on all four sides. Spencer: "you dont have to account for the
        /// weird perspective sizing anymore so every side should have the same border".</summary>
        public const float TILE_ASPECT   = 1.0040f;  // 502/500 — Spencer's peach_tile, front-on
        public const float TILE_TOP_FACE = 0.167f;   // hidden behind the tile above
        /// <summary>Vertical pitch = the tile's REAL height. 2026-09-15: was `CellSize` (a square
        /// pitch), which was right when the tile overhung its cell by 20% and rows OVERLAPPED.
        /// Front-on the tile is 0.96 cells tall, so a square pitch left a 0.04-cell gap between
        /// rows while columns butted edge-to-edge (TILE_DISPLAY_RATIO = 1.0) — uneven spacing.
        /// Now rows butt exactly like columns. Spencer: "each letter should have equal spacing
        /// between the top and sides".</summary>
        public float RowPitch => CellSize / TILE_ASPECT;
        public const float ROW_Z_STEP = 0.01f;   // per-row depth so upper rows draw in front

        public static int COLS     => RulesEngine.COLS;
        public static int MAX_ROWS => RulesEngine.MAX_ROWS;
        public static int ROWS     => RulesEngine.ROWS;

        private const float GRID_HEIGHT_FRACTION          = 0.82f;  // denser, less bulky
        // Board width as fraction of screen width. Tile = fraction / COLS.
        // Target: 12-13% screen width per tile (Candy Crush / Royal Match benchmark).
        // 6 cols × 13% = 0.78. Survival gets a touch more (chunky, high-pressure feel).
        private const float SURVIVAL_GRID_WIDTH_FRACTION  = 0.882f;  // was 0.84 — +5% breathing room
        private const float DEFAULT_GRID_WIDTH_FRACTION   = 0.819f;  // was 0.78 — +5% breathing room
        private const float SURVIVAL_GRID_TOP_MARGIN      = 0.30f; // push grid down — more room above for HUD, thumb-friendly
        private const float GRAVITY_FALL_SPEED            = 14f; // was 10 originally — slightly faster (world units/s, tuned on the 9-wide board)
        // FALLS ARE TIMED IN CELLS (2026-10-23, 7x9 board): 14 world units/s was tuned for ~0.96-unit cells; the 7-wide board's
        // cells are ~29% bigger, so every fall took ~29% longer (log: a refill fall ran 1.75s). Same CELLS per second either board.
        private float FallSpeed => GRAVITY_FALL_SPEED * (CellSize > 0.01f ? CellSize / 0.96f : 1f);

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
        // 2026-09-10 palette pass: was deep cornflower (0.20,0.38,0.62), which sat within a
        // few points of BOARD_INNER — so the board had no edge and read as a hole punched in
        // the cyan background. Warm gold does three jobs at once: separates board from
        // background, echoes the gold high-value tiles, and is the frame colour measured off
        // Royal Match in [[reference-match3-hud-anatomy]] (#F6A212). Revert = restore the line below.
        // 2026-09-10: REVERTED to the original warm gold at Spencer's request. (I had retuned it
        // to hue 50 to match the tile gold on my own theory that the two were fighting — he did
        // not ask for that. Don't change the frame as a side effect of a tile change.)
        private static readonly Color FRAME_OUTER    = new Color(0.93f, 0.64f, 0.13f, 1f);  // warm gold rim
        private static readonly Color FRAME_EDGE     = new Color(0.220f, 0.270f, 0.540f, 1f);  // brighter top lip — sculpted
        // 2026-09-09 Spencer: #201E2C -> #453860. Compared five board values with the real
        // tiles, correct row overlap and letters on. The old near-black made the empty area
        // ABOVE the stack read as a hole rather than a surface — that void was the source of
        // the "too dark / not happy" feeling, not the tiles. Deep plum is the first value
        // where it reads as a surface while cream and gold still pop; going lighter (mid
        // violet, or RM's #A2AEDE) starts merging the CREAM tile into the field, and cream is
        // the majority piece. Warm violet-red bias rather than the old cold blue-black.
        // 2026-09-10: was #453860 deep plum — measured at only 26% saturation, i.e. grey with a
        // purple lean, which is why it read dusty rather than candy. Spencer picked mid blue from
        // a 4-up and then a light 4-up, and he was right on both counts:
        //  • LIGHTER is happier — the dark options all read moody next to it.
        //  • but NOT Royal Match's lavender (#99A8D3, sat 30%). RM can use a desaturated board
        //    because its pieces are saturated reds/greens/golds that break it up. WordDrop's
        //    tiles are ALL ONE CREAM, so a low-chroma board just repeats the dusty problem.
        // Mid blue at ~42% sat: complements the warm cream tiles (hue 215 vs tile hue 40) and
        // is bright enough to feel happy without washing the tiles out.
        // Revert = the line below.
        //   was: new Color(0.271f, 0.220f, 0.376f, 1f)  // #453860 deep plum
        // 2026-09-10: ROYAL MATCH lavender, measured off ~/Documents/WordDrop_Art/refs_2026_09_10/
        // royalmatch.png = #99A8D3 (hue 224, sat 30%, L 66). Specified ~8% brighter than target
        // because MEASURED: this project's post (bloom 1.3 / vignette) renders colours ~8% under
        // the literal — #59739A came out #516A8F, #6696CC came out #5F8DC2.
        // 2026-09-10 (Spencer's idea): TRANSLUCENT DARK TINT instead of an opaque slab — the
        // Candy Crush construction, where the board is a smoked panel and the background reads
        // THROUGH it. Two reasons it is the right call here:
        //  • it ties the board to the scene instead of parking an opaque rectangle on top of it,
        //  • it is the right foundation for the painted background he wants later — the art will
        //    show through the play area rather than being hidden behind it.
        // Alpha survives CreateRoundedRect (it does col.a *= coverage for the AA edge) and the
        // default sprite material blends normally.
        // 2026-09-10: TOON BLAST construction. Measured off the reference: TB has NO board
        // surface at all — a thin outline round the play area and the painted scene straight
        // through it (visible in the notches where no cube sits). The earlier 52% smoked panel
        // still read as a slab because a tint over a FLAT gradient just makes another flat
        // colour; there was nothing behind it to see.
        // NOTE: TB gets away with this because its board is always FULL — every cell holds a
        // saturated cube, so the surface is never visible. WordDrop's board is ~2/3 EMPTY, so
        // this shows a lot of background. It needs the painted art behind it to pay off.
        // 2026-09-10 — SPENCER CALLED IT: "i think its just black with alpha down to look like a
        // tint". MEASURED off toonblast.png and he is right. Cross-section of TB's board edge:
        //   x=10-19  #C1D2D0 L=81  background
        //   x=20-26  #E4C795 L=79  cream frame rim (~7px)
        //   x=27-46  #3A3B3D L=24  BOARD  (~20px of padding before the first cube)
        //   x=47+                  first cube
        // #3A3B3D is 58,59,61 — NEUTRAL grey, R~=G~=B. A coloured panel never lands that
        // neutral. And background #C1D2D0 x 0.30 = (58,63,62), i.e. pure BLACK at ~70% alpha.
        // So: no hue at all. Just black, alpha down. Every tinted-navy attempt before this was
        // wrong for the same reason — it added a colour where the reference has none.
        /// <summary>Board surface. 2026-09-21 Spencer: **#113B77**.
        /// Was pure black at alpha 0.62, i.e. the colour came from whatever showed THROUGH it —
        /// which is why it read as teal rather than any chosen value. Now opaque, so the hex is
        /// exactly what renders. CELL_GRID_A/B still stack on top as low-alpha black, so the
        /// checkerboard continues to work off this as its base.</summary>
        // 2026-09-22: #113B77 -> #1A468E, sampled from Spencer's authored inner_board@2x.
        // Kept in sync so anything deriving from BOARD_INNER (the checkerboard washes, the
        // mini-board at CreateBoardSprite) agrees with the panel the player actually sees.
        private static readonly Color BOARD_INNER    = new Color(0.1020f, 0.2745f, 0.5569f, 1f); // #1A468E


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
        // ── BAKED CELL GRID (2026-09-10) ───────────────────────────────────────────
        // The board interior was flat plum, so a settled row read as ONE SLAB of letters
        // rather than nine movable pieces — which fights the swap verb, since the thing the
        // player must see is individual, rearrangeable objects.
        //
        // The cell-liner system above already existed for this, but (a) it was compile-gated
        // OFF to chase frame drops on device and (b) its authored texture
        // ("menu psds/cell_liner") is MISSING from Resources, so turning it back on draws
        // nothing. It also cost 99 SpriteRenderers — one per cell — which is why it cost
        // frames in the first place.
        //
        // This bakes the whole grid into ONE sprite instead: zero per-cell renderers, one
        // draw call, no missing asset. Colours are derived from BOARD_INNER rather than
        // imported from the Royal Match reference, so it adds STRUCTURE without changing
        // Spencer's palette. Set CELL_GRID_ENABLED=false to revert entirely.
        /// <summary>Margin between the board's inner edge and the tiles, as a fraction of the
        /// playable width, per side. 2026-09-10 Spencer: "there's still no space padding between
        /// the edges and the letters" — and he was right, there was mathematically NONE:
        /// `CellSize = playW / COLS` sized the tiles to fill the playable area exactly, so they
        /// ran straight into the frame. MEASURED on toonblast.png: ~20px of board between the
        /// frame rim and the first cube, against a ~63px cube — about a third of a cell.
        /// 0 restores the old flush behaviour.</summary>
        /// 2026-09-10: started at 0.025 (24% of a cell) — Spencer: "the margin you gave me is
        /// huge in comparison to the toon blast margin". My measurement was inflated because the
        /// scan kept hitting TB's side rail rather than a normal cube. Halved to ~12% of a cell.
        public static float BOARD_PAD_FRAC = 0.008f;   // 2026-09-10: 0.025 too big, 0.012 still a touch wide — Spencer wanted the tiles a hair closer to the rim

        public static bool  CELL_GRID_ENABLED = true;

        // 2026-09-22: Spencer's authored socket (rectangle_pattern@2x, re-centred on import).
        // When present it REPLACES the procedural rounded rect and the A/B checkerboard — his
        // art is one design, drawn identically in every cell. Clear the string to go back.
        public static string CELL_SOCKET_ASSET  = Tile.BOARD_C ? "" : "Tiles/cell_socket@2x";   // 11-02 board C: procedural flat checker instead
        // Fraction of the CELL the socket occupies. His export is 226 px against a 245 px cell
        // pitch at 2x (0.922), which is the size he drew it — but tiles butt edge-to-edge at
        // TILE_DISPLAY_RATIO = 1.0, so at 0.922 an empty cell was 8% smaller than the tile that
        // fills it and the grid changed rhythm as the board filled. 1.0 makes the empty slot the
        // exact footprint of its tile. Back to 226f/245f for his authored gutter.
        public static float  CELL_SOCKET_W_FRAC = 1.0f;
        public static float CELL_GRID_INSET   = Tile.BOARD_C ? 0f : 0.05f;   // board C: full-cell checker squares   // gap around each cell, as a fraction of the cell
        public static float CELL_GRID_RADIUS  = Tile.BOARD_C ? 0f : 0.20f;   // corner radius, as a fraction of the cell
        /// <summary>The two checkerboard shades. Both start from BOARD_INNER (#453860) nudged
        /// lighter, so the board still reads plum. Push them apart for a stronger check.</summary>
        // 2026-09-10: on the old DARK plum board these were LIGHTER than it. On the new mid-blue
        // board they must be DARKER — a socket is a recess, so it reads as shadow, not highlight.
        // Sockets sit slightly DARKER than the board — a recess reads as shadow, not highlight.
        // Sockets sit slightly DARKER than the board — a recess reads as shadow, not highlight.
        // Sockets are a SECOND light dark wash on top of the smoked panel, so they read as
        // recesses. Keep the alpha low — it stacks on BOARD_INNER's.
        // With no panel behind them the sockets carry the grid structure on their own, so they
        // need a touch more presence than they did over the smoked slab.
        // Sockets: also pure black, a touch more alpha so they read as recesses.
        public static Color CELL_GRID_A = Tile.BOARD_C ? new Color(0f, 0f, 0f, 0f) : new Color(0f, 0f, 0f, 0.16f);
        public static Color CELL_GRID_B = Tile.BOARD_C ? new Color(0.10f, 0.08f, 0.30f, 0f) : new Color(0f, 0f, 0f, 0.07f);   // board C: checker OFF (11-02 Spencer: 'i dont really like it'); alpha .10 = the mock's 6066AE check
        private GameObject _cellGridGO;
        private GameObject _specShadowGO;

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
        private GameObject _specFrameGO, _specInnerGO;   // board + frame, for the entry slide
        private float _specBoardW, _specBoardH, _specBoardCentreY;
        private bool  _specUsed;
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
            // POSITION IT HERE, not only at the call sites. 2026-10-19: tiles were turning up
            // at (0,0,0) — the grid origin, which is mid-board — because ResetForPool zeroes
            // localPosition and a caller that forgets to place the tile leaves it there. That
            // is the stray floating tile, and several landing on the same spot is what read as
            // one tile with its letters blended. CheckoutTile already knows col/row, so it can
            // guarantee a sane position; callers that set their own just overwrite this.
            tile.transform.position = CellToWorld(col, row);
            tile.MarkBorn();   // [GemBorn] diag: a gem on a tile older than its spawn = a plain tile converted

            // A level entry was requested before the grid existed — run it now, before this
            // tile (and every tile after it) is placed. 2026-09-10 Spencer.
            if (_boardEntryRequested && _gridRoot != null) PrepBoardEntry();
            if (!_boardEntryPrepped)
                WDLog.Log($"[EntryDiag] tile '{letter}' at ({col},{row}) created with NO entry active (frame {Time.frameCount})");

            _allTileObjects.Add(tile.gameObject);

            // BORN DURING A LEVEL ENTRY? PrepBoardEntry snapshots and parks the tiles that exist
            // AT THAT MOMENT. A tile created afterwards was never in that list, so it sat at its
            // rest position while the rest of the board was parked off-screen — which is a fully
            // populated board showing through the level map before the slide-in. Intermittent,
            // because it depended on whether the board finished populating before or after the
            // park. Same pattern the spec frame/inner panel already uses below. 2026-09-10 Spencer.
            if (_boardEntryPrepped)
            {
                _boardEntryTfs.Add(tile.transform);
                _boardEntryRests.Add(tile.transform.position);
                tile.transform.position += new Vector3(_boardEntryOffX, 0f, 0f);
            }

            return tile;
        }

        /// <summary>Make every tile's SPECIAL STATE match the rules data. 2026-10-19.
        ///
        /// WHY THIS IS GENERIC AND NOT ICE-ONLY. An audit of all eight special-cell flags found
        /// the SAME two holes in every one of them:
        ///   • `AnimateRiseRow` references NONE of them — it moves tiles and checks fresh ones
        ///     out of the pool, but never carries special state across.
        ///   • `ShiftBoardUp` moves the DATA cell OBJECTS correctly, so the flags travel fine in
        ///     the data — the VISUAL layer was simply never told.
        /// Ice was the first to be switched on, so ice was the first to show the damage: tiles
        /// that refused to swap, tiles picking up frost they should not have, ice vanishing on a
        /// rise. Stone, vaults, wild, gold and the refills would each fail identically the
        /// moment they are enabled. Fixing it per-mechanic would mean finding this bug five
        /// more times, so this reconciles ALL of them in one pass.
        ///
        /// Costs one board walk comparing bools. A mechanic that is switched OFF has no cells
        /// carrying its flag, so this is a no-op for it. Every correction is LOGGED: if a line
        /// keeps appearing, something upstream is still desyncing and the real fix belongs
        /// there — this is a safety net, not a licence to skip carrying state properly.</summary>
        // ── GRASS VISUALS (2026-10-23; mowing since the same day). A green patch per grass square UNDER the tiles (shows round the tile edges) plus a small leaf
        // (Spencer's pack, 2leaf) in the tile's corner so a covered square is readable even under a full tile. Patches belong to
        // the board (children of the grid root — they slide with it), not to tiles. Synced from RulesEngine whenever visuals reconcile. ──
        public static Color GRASS_COLOR = new Color(0.36f, 0.72f, 0.24f, 1f);
        public static float GRASS_LEAF_SIZE = 0.34f;   // cells
        private GameObject[,] _grassPatch, _grassLeaf;
        private static Sprite s_grassPatch, s_grassLeaf;
        public void SyncGrass(RulesEngine rules)
        {
            if (rules == null || _gridRoot == null) return;
            if (_grassPatch == null || _grassPatch.GetLength(0) != COLS || _grassPatch.GetLength(1) != ROWS) { _grassPatch = new GameObject[COLS, ROWS]; _grassLeaf = new GameObject[COLS, ROWS]; }
            if (s_grassPatch == null) s_grassPatch = TileRenderer.CreateSolidRoundedRect(128, 128, 26, Color.white);
            if (s_grassLeaf == null) { var tex = Resources.Load<Texture2D>("Boosters/2leaf"); if (tex != null) s_grassLeaf = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width); }
            for (int x = 0; x < COLS; x++)
                for (int y = 0; y < ROWS; y++)
                {
                    bool want = rules.HasGrass(x, y);
                    var go = _grassPatch[x, y];
                    if (!want)
                    {   // MOWED: the patch shrinks away and the leaf flips up and fades (a little "mow" beat)
                        if (go != null) { var g0 = go; g0.transform.DOKill(); g0.transform.DOScale(Vector3.zero, 0.22f).SetEase(Ease.InBack).OnComplete(() => { if (g0 != null) Destroy(g0); }); _grassPatch[x, y] = null; }
                        var lf = _grassLeaf[x, y];
                        if (lf != null)
                        {
                            _grassLeaf[x, y] = null; lf.transform.DOKill();
                            lf.transform.DOMove(lf.transform.position + new Vector3(Random.Range(-0.3f, 0.3f), 0.7f, 0f) * CellSize, 0.4f).SetEase(Ease.OutQuad);
                            lf.transform.DORotate(new Vector3(0f, 0f, Random.Range(-160f, 160f)), 0.4f, RotateMode.FastBeyond360);
                            var lsr = lf.GetComponent<SpriteRenderer>(); if (lsr != null) lsr.DOFade(0f, 0.4f);
                            DOVirtual.DelayedCall(0.45f, () => { if (lf != null) Destroy(lf); });
                        }
                        continue;
                    }
                    Vector3 at = CellToWorld(x, y);
                    if (go == null)
                    {
                        go = new GameObject($"Grass_{x}_{y}"); go.transform.SetParent(_gridRoot.transform, true);
                        var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = s_grassPatch; sr.color = GRASS_COLOR; sr.sortingOrder = 2;   // grid 1 < GRASS 2 < tiles 5
                        float w = CellSize * 1.02f, h = RowPitch * 1.02f;
                        go.transform.localScale = new Vector3(w / s_grassPatch.bounds.size.x, h / s_grassPatch.bounds.size.y, 1f);
                        go.transform.position = new Vector3(at.x + BoardSlideOffsetX, at.y, 0.05f);
                        go.transform.DOKill(); var s0 = go.transform.localScale; go.transform.localScale = s0 * 0.4f; go.transform.DOScale(s0, 0.3f).SetEase(Ease.OutBack, 2f);
                        _grassPatch[x, y] = go;
                        if (s_grassLeaf != null)
                        {
                            var lg = new GameObject($"GrassLeaf_{x}_{y}"); lg.transform.SetParent(_gridRoot.transform, true);
                            var lsr = lg.AddComponent<SpriteRenderer>(); lsr.sprite = s_grassLeaf; lsr.sortingOrder = 46;   // over the tiles (row bands top out ~37), under board FX (48)
                            lg.transform.localScale = Vector3.one * CellSize * GRASS_LEAF_SIZE;
                            lg.transform.position = new Vector3(at.x + BoardSlideOffsetX - CellSize * 0.32f, at.y - RowPitch * 0.30f, -0.5f);
                            lg.transform.localRotation = Quaternion.Euler(0f, 0f, 20f);
                            _grassLeaf[x, y] = lg;
                        }
                    }
                }
        }

        public void ReconcileSpecialVisuals(RulesEngine rules)
        {
            if (rules == null) return;
            SyncGrass(rules);
            int fixes = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
            for (int row = 0; row < RulesEngine.ROWS; row++)
            {
                Tile t = GetTile(col, row);
                if (t == null) continue;
                var c = rules.GetCell(col, row);
                if (c == null) continue;

                if (t.IsFrozen     != c.IsFrozen)     { t.SetFrozenVisual(c.IsFrozen);        fixes++; Report(col,row,t,"ice",      c.IsFrozen); }
                if (t.IsGem        != c.HasGem)       { t.SetGem(c.HasGem);                   fixes++; WDLog.Log($"[StateSync] ({col},{row}) '{c.Letter}' gem: visual={t.IsGem} data={c.HasGem} → corrected"); }
                if (t.IsRainGold   != c.IsRainGold)   { t.SetRainGold(c.IsRainGold);          fixes++; WDLog.Log($"[StateSync] ({col},{row}) '{c.Letter}' rain-gold: visual={t.IsRainGold} data={c.IsRainGold} → corrected"); }
                if (t.SpecialKind  != c.SpecialKind)  { t.SetSpecial(c.SpecialKind);          fixes++; WDLog.Log($"[StateSync] ({col},{row}) '{c.Letter}' special: visual={t.SpecialKind} data={c.SpecialKind} → corrected"); }
                // FLAGS AGREEING IS NOT THE SAME AS LOOKING RIGHT. 2026-10-20 — "the ice is
                // completely disappearing in some cases on some letters when there is a row
                // rise": a tile recycled mid-melt kept IsFrozen TRUE with its overlay scaled to
                // nothing, so this comparison said "both true, nothing to do" while the player
                // saw a locked tile with no ice on it. Ask the pixels as well as the bookkeeping.
                else if (c.IsFrozen && !t.FrostVisualHealthy)
                {
                    t.RepairFrostVisual(); fixes++;
                    Debug.LogWarning($"[IceRepair] ({col},{row}) '{t.Letter}' was flagged frozen with no " +
                                     "visible frost — overlay re-asserted. If this keeps printing, something " +
                                     "upstream is interrupting the defrost without releasing it.");
                }
                // THE PIXELS, for faces too (2026-10-23: gem/gold faces appeared on plain letters right after a fall — on cells where
                // gems had just been collected). Flags agreed, so nothing above caught it. TEMP-ish: the log line names the culprit.
                if (t.FaceIsStale(out string showing))
                {
                    WDLog.Log($"[StateSync] ({col},{row}) '{t.Letter}' FACE stale: showing '{showing}' (gem data={c.HasGem}, tile gem={t.IsGem}, " +
                              $"gold={t.IsRainGold}, special={t.SpecialKind}) → resting face");
                    t.ForceRestingFace(); fixes++;
                }
                if (t.TreasureHits != c.TreasureHits) { t.SetTreasureHits(c.TreasureHits);    fixes++; }   // treasure repaints after a hit
                if (t.CrateHP      != c.CrateHP)      { t.SetCrateHP(c.CrateHP);              fixes++; }   // 11-03 multi-hit crate repaints after a hit
                if (t.IsStone      != c.IsStone)      { t.SetStoneVisual(c.IsStone);          fixes++; Report(col,row,t,"stone",    c.IsStone); }
                if (c.ShelfPart > 0 && (t.ShelfPart != c.ShelfPart || t.ShelfBooks != c.ShelfBooks || t.ShelfStyle != c.ShelfStyle || t.ShelfKind != c.ShelfKind)) { t.SetShelf(c.ShelfPart, c.ShelfBooks, c.ShelfStyle, c.ShelfKind); t.SetStoneVisual(true); fixes++; }   // BOOKSHELF (11-03)
                if (t.IsAnchored   != c.IsAnchored)   { t.SetAnchored(c.IsAnchored);
                                                        if (c.ShelfPart > 0) t.SetStoneVisual(true); else t.SetVaultVisual(c.IsAnchored);   fixes++; Report(col,row,t,"vault",    c.IsAnchored); }
                if (t.IsWild       != c.IsWild)       { t.SetWild(c.IsWild);                  fixes++; Report(col,row,t,"wild",     c.IsWild); }
                if (t.IsSwapRefill != c.IsSwapRefill) { t.SetSwapRefillVisual(c.IsSwapRefill);fixes++; Report(col,row,t,"swapfill", c.IsSwapRefill); }
                if (t.IsEditRefill != c.IsEditRefill) { t.SetEditRefillVisual(c.IsEditRefill);fixes++; Report(col,row,t,"editfill", c.IsEditRefill); }
                if (t.IsWildRefill != c.IsWildRefill) { t.SetWildRefillVisual(c.IsWildRefill);fixes++; Report(col,row,t,"wildfill", c.IsWildRefill); }
            }
            // ── POSITION, NOT JUST FLAGS ─────────────────────────────────────────────
            // 2026-10-19: the MISPLACED invariant finally named this. Two signatures, both
            // from a rise that moved the DATA and left the VISUALS behind:
            //   • "have (0,0,0)"            — a pooled tile checked out and never positioned.
            //                                 ResetForPool zeroes localPosition, so it sits at
            //                                 the grid origin. THIS is the floating mid-board
            //                                 tile with letters apparently blended: several of
            //                                 them stacked on the same spot.
            //   • "have == the row BELOW's want" — including the per-row z (-0.02 where -0.03
            //                                 was wanted). The tile holds its pre-rise slot.
            // Snapping here is the same safety net the frost reconcile is: it logs every fix,
            // so if this keeps firing the real repair belongs in AnimateRiseRow.
            int moved = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
            for (int row = 0; row < RulesEngine.ROWS; row++)
            {
                Tile t = GetTile(col, row);
                if (t == null || t.IsAnimating) continue;
                if (rules.GetCell(col, row) == null) continue;

                Vector3 want = CellToWorld(col, row);
                Vector3 have = t.transform.position;
                if (Vector2.Distance(new Vector2(want.x, want.y), new Vector2(have.x, have.y)) <= CellSize * 0.5f)
                    continue;

                t.transform.position = want;
                moved++;
                WDLog.Log($"[PosSync] ({col},{row}) '{t.Letter}' snapped from {have} to {want}");
            }
            if (moved > 0)
                WDLog.Log($"[PosSync] repositioned {moved} tile(s) left behind by a board move");

            if (fixes > 0)
                WDLog.Log($"[StateSync] corrected {fixes} tile/data mismatch(es) after a board change");
        }

        private static void Report(int col, int row, Tile t, string what, bool shouldBe)
            => WDLog.Log($"[StateSync] ({col},{row}) '{t.Letter}' {what}: visual={!shouldBe} data={shouldBe} → corrected");

        /// <summary>Back-compat alias — ice was the first mechanic this caught.</summary>
        public void ReconcileFrozenVisuals(RulesEngine rules) => ReconcileSpecialVisuals(rules);

        /// <summary>Find Tile components that exist in the SCENE but are in neither the
        /// _tiles[,] grid nor the pool — true orphans that NOTHING can see or clean up.
        ///
        /// 2026-10-19: a tile kept appearing FLOATING mid-board with its glyph blended. Five
        /// diagnostics came back zero (double-Initialise, double pool-return, flag mismatch,
        /// gravity collision, unregistered checkout), and so did the new BoardInvariants
        /// floating/orphan checks — because EVERY one of those reads `_tiles[,]`. A GameObject
        /// that was never in, or was dropped from, that array is invisible to all of them.
        /// This is the only check that can see it, so it has to walk the scene.
        ///
        /// Debug-only and not cheap (FindObjectsByType); BoardInvariants calls it on its
        /// 0.35s tick, only while the board is settled.</summary>
        private static int s_lastOrphanSig;
        public int ReportSceneOrphanTiles()
        {
            var all = Object.FindObjectsByType<Tile>(FindObjectsSortMode.None);
            if (all == null || all.Length == 0) return 0;

            // Walk the WHOLE array, not RulesEngine.ROWS: off a level (main menu) ROWS drops to 5, so a full 9x11 board's rows
            // 5-10 were reported as 54 "orphans" every time - a false alarm (2026-10-27; RebuildGrid already clears to MAX_ROWS).
            var known = new HashSet<Tile>();
            for (int c = 0; c < _tiles.GetLength(0); c++)
            for (int r = 0; r < _tiles.GetLength(1); r++)
                if (_tiles[c, r] != null) known.Add(_tiles[c, r]);
            foreach (var pooled in _tilePool) if (pooled != null) known.Add(pooled);

            // 2026-10-27: ONE summary line per new orphan set (was one LogError with a stack trace per tile - 54 at once, every
            // 0.35s tick, which stuttered the editor). Same information; the first 12 are named.
            int orphans = 0; var names = new System.Text.StringBuilder(); int sig = 17;
            foreach (var t in all)
            {
                if (t == null || known.Contains(t)) continue;
                if (!t.gameObject.activeInHierarchy) continue;   // parked/inactive is fine
                orphans++; sig = sig * 31 + t.GetInstanceID();
                if (orphans <= 12) names.Append($"\n  '{t.Letter}' col={t.Col} row={t.Row} pos={t.transform.position} name='{t.gameObject.name}'");
            }
            if (orphans > 0 && sig != s_lastOrphanSig)
            {
                s_lastOrphanSig = sig;
                Debug.LogError($"[Invariant] {orphans} SCENE ORPHAN tile(s) - in the scene but in NEITHER _tiles[] NOR the pool, " +
                               $"nothing can clean them up (frame {Time.frameCount}):{names}{(orphans > 12 ? $"\n  ... +{orphans - 12} more" : "")}");
            }
            return orphans;
        }

        private void ReturnTile(Tile tile)
        {
            if (tile == null) return;

            // DOUBLE-RETURN GUARD. 2026-10-19 — ReturnTile has FOUR call sites and pushed
            // unconditionally, so the same Tile could land in the pool twice. CheckoutTile then
            // hands the SAME live object out for two different cells: one cell's tile silently
            // becomes the other's, leaving an orphan rendering on top of its neighbour. That
            // is one of the two things that can produce the "glitchy board" Spencer sees (a
            // tile showing several letters blended together); the other is a double Initialise,
            // guarded separately in Tile.BuildVisuals. Logging it rather than failing silently
            // so the next occurrence names the caller.
            if (_tilePool.Contains(tile))
            {
                Debug.LogWarning($"[TilePool] DOUBLE RETURN of tile '{tile.Letter}' at " +
                                 $"({tile.Col},{tile.Row}) — ignored. A caller is releasing a " +
                                 $"tile it had already released.");
                return;
            }

            _allTileObjects.Remove(tile.gameObject);
            tile.ResetForPool();
            _tilePool.Push(tile);
        }

        // ── Board shake (chest crack) — jiggles the board frame + the loose board tiles together, but
        // NOT the hand rack (it isn't in _allTileObjects / under _gridRoot, so it stays still). Decaying
        // Perlin offset; doesn't hard-restore tiles so a tile pooled mid-shake can't get snapped back.
        // 2026-06-18 Spencer.
        private Coroutine _shakeCo;

        /// <summary>Shake the board. Only ONE shake runs at a time — six call sites can fire
        /// this, and overlapping shakes each drive the same transform every frame, so they
        /// fight and the board judders or sticks off-centre. A new shake REPLACES the running
        /// one (keeping the stronger of the two) rather than stacking on top of it.
        /// 2026-09-09 Spencer — this is what made the score slam "fire at weird times".</summary>
        public void ShakeBoard(float magnitude, float duration,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = "",
            [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            // TEMP 2026-09-09: the board jiggles when nothing is happening. CallerMemberName
            // is filled in by the COMPILER, so this names the culprit with no stack walking.
            WDLog.Log($"[ShakeDbg] ShakeBoard(mag={magnitude:F3}, dur={duration:F2}) from {caller}:{line}");
            if (_gridRoot == null || magnitude <= 0f || duration <= 0f) return;
            // NEVER interrupt a running shake. It moves every tile by an additive DELTA
            // each frame and only subtracts the residual when it completes — killing it
            // mid-flight strands every tile permanently off-grid. So a second shake is
            // DROPPED rather than stacked or swapped in. Overlapping shakes also fight
            // over the same transforms, which is the juddering. 2026-09-09 Spencer.
            if (_shakeCo != null) return;
            _shakeCo = StartCoroutine(ShakeBoardTracked(magnitude, duration));
        }


        private readonly List<Transform> _boardEntryTfs   = new List<Transform>();
        private readonly List<Vector3>   _boardEntryRests = new List<Vector3>();
        private bool _boardEntryPrepped; // true between PrepBoardEntry (park) and AnimateBoardIn completion
        private bool _boardEntryRequested; // entry asked for before the grid existed — run it as soon as it does

        /// <summary>True while the board is parked off-screen or sliding in. Input must be
        /// dead for this whole window — a tap during it was landing as a tile selection the
        /// moment the board arrived. 2026-09-09 Spencer.</summary>
        public bool BoardEntryInProgress => _boardEntryPrepped;

        /// <summary>Level-entry: the board SHOOTS IN from the right, overshoots, and settles. The grid FRAME
        /// (cells/bg under _gridRoot) AND every letter tile (tiles live at scene root, NOT under _gridRoot) are
        /// offset together by the same amount, so the letters slide WITH the board. Snaps back to exact rest on
        /// complete. 2026-07-13 Spencer.</summary>
        private float _boardEntryOffX;

        /// <summary>t=0 of the level-entry: snapshot the board's rest + PARK it off-screen to the right immediately,
        /// so it's never seen sitting at centre during the beat before it slides in. 2026-07-13 Spencer.</summary>
        public void PrepBoardEntry()
        {
            if (_gridRoot == null)
            {
                WDLog.Log($"[EntryDiag] PrepBoardEntry DEFERRED — no grid root yet (frame {Time.frameCount})");
                // The grid does not exist YET. Returning silently meant the park never happened,
                // _boardEntryPrepped stayed false, and the board built itself at rest in full
                // view — then AnimateBoardIn parked it late and slid it in, which reads as "the
                // board was already there, then tiles flew in from the side". REMEMBER the
                // request and honour it the moment the grid exists. 2026-09-10 Spencer.
                _boardEntryRequested = true;
                return;
            }
            _boardEntryRequested = false;
            ClusterOutline.Instance?.Clear();   // never carry an outline into a level entry
            if (_boardEntryPrepped)
            {
                WDLog.Log($"[EntryDiag] PrepBoardEntry SKIPPED — already prepped (frame {Time.frameCount}, tracked={_boardEntryTfs.Count})");
                return; // already snapshotted + parked — don't re-snapshot the parked pos as "rest"
            }
            _boardEntryPrepped = true;
            _boardEntryTfs.Clear(); _boardEntryRests.Clear();
            _boardEntryTfs.Add(_gridRoot.transform); _boardEntryRests.Add(_gridRoot.transform.position);
            // 2026-09-03: the spec board + frame must slide in WITH the tiles. They are
            // children of _gridRoot, but the root can be rebuilt after this snapshot is
            // taken — so drive them explicitly rather than relying on parenting.
            if (_specFrameGO != null)
            { _boardEntryTfs.Add(_specFrameGO.transform); _boardEntryRests.Add(_specFrameGO.transform.position); }
            if (_specInnerGO != null)
            { _boardEntryTfs.Add(_specInnerGO.transform); _boardEntryRests.Add(_specInnerGO.transform.position); }
            if (_cellGridGO != null)
            { _boardEntryTfs.Add(_cellGridGO.transform); _boardEntryRests.Add(_cellGridGO.transform.position); }
            for (int i = 0; i < _allTileObjects.Count; i++)
                if (_allTileObjects[i] != null)
                {
                    _boardEntryTfs.Add(_allTileObjects[i].transform);
                    _boardEntryRests.Add(_allTileObjects[i].transform.position);
                }
            _boardEntryOffX = 12f;
            var cam = Camera.main;
            // 2.6 half-widths clears the FULL board off the right edge (1 half-width to reach the edge + up to 1 for
            // the board's own half-width + margin) — at 1.35 the board's left half was still peeking in. 2026-07-13.
            if (cam != null) _boardEntryOffX = cam.orthographicSize * cam.aspect * 2.6f;
            WDLog.Log($"[EntryDiag] PrepBoardEntry PARKED {_boardEntryTfs.Count} transform(s), offX={_boardEntryOffX:F2} (frame {Time.frameCount})");
            ApplyBoardEntryOffset(_boardEntryOffX); // park off-screen NOW
        }

        /// <summary>Slide the (already-parked) board in from the right, overshoot, settle. 2026-07-13.</summary>
        public void AnimateBoardIn()
        {
            if (_gridRoot == null) return;
            WDLog.Log($"[EntryDiag] AnimateBoardIn — tracked={_boardEntryTfs.Count} prepped={_boardEntryPrepped} (frame {Time.frameCount})");
            if (_boardEntryTfs.Count == 0) PrepBoardEntry(); // FX-test / direct call: park first
            DOTween.Kill("boardEntry");
            DOTween.To(() => _boardEntryOffX, ApplyBoardEntryOffset, 0f, 0.52f)
                .SetEase(Ease.OutBack, 1.3f).SetId("boardEntry")
                .OnComplete(() => { ApplyBoardEntryOffset(0f); _boardEntryPrepped = false; });
        }

        /// <summary>How far the whole board is currently displaced from its rest position by an
        /// entry/exit slide. ANYTHING that compares a tile's world position against CellToWorld
        /// must add this — CellToWorld returns the board's RESTING layout and knows nothing about
        /// the slide. 2026-10-20: BoardInvariants did not, so the moment the board slid out after
        /// a stage clear every tile on it read as MISPLACED by up to 12 world units, and the
        /// checker's repair then teleported each one back onto the departing board. That is the
        /// "everything goes weird after level one" report — see the note on BoardSlideOffset's
        /// use in BoardInvariants.</summary>
        public float BoardSlideOffsetX => _boardSlideOffsetX;
        private float _boardSlideOffsetX;

        private void ApplyBoardEntryOffset(float dx)
        {
            _boardSlideOffsetX = dx;
            for (int i = 0; i < _boardEntryTfs.Count; i++)
            {
                var tf = _boardEntryTfs[i]; if (tf == null) continue;
                // TILES go to THEIR CELL, never to a remembered position (2026-10-23: "there was an error just now when loading
                // the board" — the park was snapshotted while the board was still settling, and columns slid in one cell high:
                // "[Invariant] MISPLACED (7,6) 'P' — 1.0 cells from its own cell"). A tile's rest is CellToWorld(col,row), which
                // stays right however late or early the park happened. Frame / grid / root keep their snapshot.
                var tile = tf.GetComponent<Tile>();
                Vector3 rest = _boardEntryRests[i];
                if (tile != null && tile.Col >= 0 && tile.Row >= 0) { var cw = CellToWorld(tile.Col, tile.Row); rest = new Vector3(cw.x, cw.y, rest.z); }
                tf.position = rest + new Vector3(dx, 0f, 0f);
            }
        }

        /// <summary>Level-entry UNIFIED slide: t=1 → parked off the right edge, t=0 → rest, t&lt;0 → overshoot left.
        /// Driven by a single shared tween (HUDManager) so the board moves in perfect lockstep with the tile holder
        /// + SWAPS and they all overshoot the exact same distance. 2026-07-13 Spencer.</summary>
        public void SetEntrySlideNorm(float t)
        {
            if (_boardEntryTfs.Count == 0) PrepBoardEntry(); // standalone: park first
            ApplyBoardEntryOffset(t * _boardEntryOffX);       // _boardEntryOffX is the park distance
        }

        /// <summary>Snap the board to exact rest + clear the prep guard. Called from the shared slide's OnComplete.</summary>
        public void EndEntrySlide()
        {
            ApplyBoardEntryOffset(0f);
            _boardEntryPrepped = false;
        }

        /// <summary>Level-EXIT prep: re-snapshot the CURRENT board (grid frame + whatever tiles exist right now) at
        /// rest WITHOUT parking, so the shared slide can drive it OUT to the right (norm 0→1) — the mirror of the
        /// entry. Must re-snapshot because tiles were created/destroyed during play; the entry's list is stale.
        /// 2026-07-14 Spencer.</summary>
        public void PrepBoardExit()
        {
            // The cluster outline is a separate world-space mesh — it does NOT ride the board's
            // entry/exit slide, so it hung in mid-air with its payout badge while the board slid
            // away behind the win screen. Clearing here covers EVERY route out of a level
            // (stage clear, level complete, quit) instead of chasing them one at a time — which
            // is how the LevelController-only clear missed the score-run path. 2026-09-10 Spencer.
            ClusterOutline.Instance?.Clear();
            if (_gridRoot == null) return;
            DOTween.Kill("boardEntry");
            // Leave _boardEntryPrepped FALSE — the next level's PrepBoardEntry must run fully to re-snapshot the new
            // tiles. SetEntrySlideNorm won't re-prep during the exit because the list below is non-empty.
            _boardEntryTfs.Clear(); _boardEntryRests.Clear();
            _boardEntryTfs.Add(_gridRoot.transform); _boardEntryRests.Add(_gridRoot.transform.position);
            for (int i = 0; i < _allTileObjects.Count; i++)
                if (_allTileObjects[i] != null)
                {
                    _boardEntryTfs.Add(_allTileObjects[i].transform);
                    _boardEntryRests.Add(_allTileObjects[i].transform.position);
                }
            _boardEntryOffX = 12f;
            var cam = Camera.main;
            if (cam != null) _boardEntryOffX = cam.orthographicSize * cam.aspect * 2.6f;
            // NOTE: no ApplyBoardEntryOffset here — the board stays at rest until the exit tween slides it out.
        }

        private System.Collections.IEnumerator ShakeBoardTracked(float mag, float dur)
        {
            yield return StartCoroutine(ShakeBoardCoroutine(mag, dur));
            _shakeCo = null;                       // free the slot only once it has cleaned up
        }

        private System.Collections.IEnumerator ShakeBoardCoroutine(float mag, float dur)
        {
            // ── CAMERA SHAKE (2026-09-09 Spencer) ────────────────────────────────────────
            // This used to nudge EVERY TILE's position individually, additively, each frame.
            // Two things broke it:
            //  1. Tiles are simultaneously driven by DOTween (gravity, pops, landings), and a
            //     tween OVERWRITES position every frame. So mid-tween tiles ignored the shake
            //     while idle ones moved — and during a cascade the set of tweening tiles
            //     changes constantly, so the shake rippled across the board as a WAVE.
            //     That is Spencer's "jiggle shockwave across the screen".
            //  2. It held Transform references for the whole shake while tiles were being
            //     pooled and re-checked-out elsewhere, so the residual removal at the end
            //     could shove an unrelated tile.
            // Moving the CAMERA fixes both by construction: it is one transform, it is uniform,
            // no tween can fight it, and it always returns to rest so nothing can be stranded.
            // The HUD is a screen-space overlay so it stays still, and the background is a flat
            // gradient so its movement is invisible.
            var cam = Camera.main;
            if (cam == null || mag <= 0f || dur <= 0f) yield break;
            Vector3 rest = cam.transform.position;
            float seedX = Random.value * 100f, seedY = Random.value * 100f + 50f;
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float decay = 1f - Mathf.Clamp01(t / dur);
                float nx = (Mathf.PerlinNoise(seedX, t * 38f) * 2f - 1f) * mag * decay;
                float ny = (Mathf.PerlinNoise(seedY, t * 38f) * 2f - 1f) * mag * decay;
                if (cam == null) yield break;
                cam.transform.position = rest + new Vector3(nx, ny, 0f);
                yield return null;
            }
            if (cam != null) cam.transform.position = rest;   // always exactly back to rest
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

//             WDLog.Log($"[GridManager] Awake — cellSize={CellSize:F3}, " +
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

//             WDLog.Log($"[GridManager] RebuildGrid — ROWS={ROWS}, cellSize={CellSize:F3}");
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
        /// <summary>2026-09-03: pin the survival board to the old PSD spec. OFF for the
        /// 9x12 swap-pivot board — the spec is sized for 6x8. Turn back on when the new
        /// board frame art is drawn and the constants below are re-derived.</summary>
        // ── BOARD SPEC (2026-09-03, authored on 1179x2556) ──────────────────────
        // Two rounded rectangles:
        //   outer frame  1143 x 1415, corner radius 38
        //   inner board  1121 x 1395, corner radius 27
        // Everything is expressed as a FRACTION OF SCREEN WIDTH so the board keeps
        // its designed proportions on any phone aspect (a taller phone just leaves
        // more room above/below; a wider one scales the whole board down).
        public const float SPEC_SCREEN_W = 1179f;
        public const float SPEC_BOARD_W  = 1161f;   // 2026-11-06: 1143 -> 1161, side gap 18 -> 9 px to match Royal Match (~0.8%); tiles +1.6%
        public const float SPEC_BOARD_H  = 1415f;
        public const float SPEC_PLAY_W   = 1139f;   // 2026-11-06: 1121 -> 1139 with SPEC_BOARD_W (rim stays 11 px)
        public const float SPEC_PLAY_H   = 1395f;
        public const float SPEC_R_OUTER  = 38f;
        public const float SPEC_R_INNER  = 27f;
        public const int   SPEC_ROWS     = 11;   // the spec's row count; board height holds even if ROWS differs
        /// <summary>Use the 2026-09-03 board spec instead of the legacy auto-fit.</summary>
        public static bool UseBoardSpec = true;
        /// <summary>Vertical nudge for the board as a fraction of screen height.
        /// 0 = centred between the HUD and booster bands. Negative = higher.</summary>
        public static float BOARD_BIAS = 0f;

        public static bool PinBoardToPSD = false;

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

        public static float MOCKUP_TOP_BAND = 0.215f, MOCKUP_BOTTOM_BAND = 0.115f;
        private void CalculateLayout()
        {
            float halfH = _cam.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);
            // 2026-09-03: the PSD pin hardcodes a 172px cell for a 6x8 board inside a
            // 1110x1420 frame. At 9x12 that needs 1548px of cells in an 1110px board —
            // it overflows. Auto-fit until the new board frame art exists.
            bool isSurvival = SurvivalManager.IsSurvivalMode && PinBoardToPSD;

            // ── BOARD SPEC path (2026-09-03) ────────────────────────────────────────
            // Derive everything from SCREEN WIDTH so the board matches the design on
            // any aspect ratio, then centre it in the space left between the HUD
            // reserve above and the booster reserve below.
            if (UseBoardSpec)
            {
                float screenWorldW = halfW * 2f;
                float boardW = screenWorldW * (SPEC_BOARD_W / SPEC_SCREEN_W);
                float playW  = screenWorldW * (SPEC_PLAY_W  / SPEC_SCREEN_W);
                // Inset the TILES inside the playable area so the board breathes at the rim.
                // The board itself keeps its full size; only the grid shrinks. 2026-09-10.
                float pad = playW * Mathf.Max(0f, BOARD_PAD_FRAC);
                CellSize = (playW - pad * 2f) / COLS;

                // playable height for the ACTUAL row count: (ROWS-1) square pitches
                // plus one full tile height (the tile is 1/TILE_ASPECT tall).
                float playH  = CellSize * (ROWS * (1f / TILE_ASPECT));
                float borderY = screenWorldW * ((SPEC_BOARD_H - SPEC_PLAY_H) * 0.5f / SPEC_SCREEN_W);
                // Same margin top and bottom — boardH carries the pad so the rows sit inside it.
                float boardH = playH + pad * 2f + borderY * 2f;

                // Vertical placement: centre the board in the gap between the HUD
                // band at the top and the booster band at the bottom.
                // 2026-09-04: bands were 14.5% top / 24.5% bottom, which pushed the board
                // ~130px ABOVE balanced and left a dead gap underneath. Derived from what
                // each band must actually HOLD on a 1179x2556:
                //   top    = 59 notch + ~40 breathing + ~300 HUD plaques = ~399
                //   bottom = ~300 booster row + 34 home indicator + ~40  = ~374
                // Near-equal, so the leftover splits evenly and the board sits centred.
                // BOARD_BIAS nudges it: negative = higher, positive = lower.
                // 2026-10-22 MOCKUP HUD: the new top bar ends at ~20.2% (level star bottom, 515/2556) and the booster row starts
                // at ~90% (2302/2556), so the bands are 21.5% / 11.5% with a little breathing room each side.
                float topFrac = HUDManager.MOCKUP_HUD ? MOCKUP_TOP_BAND : 0.195f;
                float botFrac = HUDManager.MOCKUP_HUD ? MOCKUP_BOTTOM_BAND : 0.195f;
                float topBand    = halfH * 2f * (topFrac - BOARD_BIAS);
                float bottomBand = halfH * 2f * (botFrac + BOARD_BIAS);
                float freeTop    = halfH - topBand;
                float freeBottom = -halfH + bottomBand;
                float boardCentreY = (freeTop + freeBottom) * 0.5f;

                GridLeft   = -playW * 0.5f + pad;
                GridRight  =  playW * 0.5f - pad;
                GridBottom = boardCentreY - playH * 0.5f;
                GridTop    = GridBottom + playH;

                _specBoardW = boardW; _specBoardH = boardH; _specBoardCentreY = boardCentreY;
                _specUsed = true;
                WDLog.Log($"[Grid] SPEC board {boardW:F2}x{boardH:F2} world, cell {CellSize:F3}, " +
                          $"{COLS}x{ROWS}, playable {playW:F2}x{playH:F2}, pad {pad:F3} " +
                          $"({(CellSize > 0.0001f ? pad / CellSize : 0f):P0} of a cell)");
                // NOTE: do NOT call BuildGridVisuals here. Every caller of
                // CalculateLayout calls it immediately afterwards, and a second call
                // destroys the root (and the board panel) that the first one built.
                // BuildGridVisuals creates the spec panel itself — see below.
                return;
            }

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
            // rows are RowPitchRatio tall (plus the one un-overlapped tile top)
            float cellFromHeight = availableHeight / (ROWS * (1f / TILE_ASPECT));

            CellSize = Mathf.Min(cellFromWidth, cellFromHeight);

            float gridWorldWidth = CellSize * COLS;
            float gridWorldHeight = CellSize * (ROWS * (1f / TILE_ASPECT));

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
            // 2026-09-03: this used to create a NEW GridRoot without destroying the old
            // one. CalculateLayout runs more than once on level entry, so the first root
            // was ORPHANED in the scene — still holding its board panel, which then sat
            // at rest position while everything else slid in (a stray rectangle in the
            // middle of the screen), and leaked a full board's worth of objects per call.
            if (_gridRoot != null)
            {
                // ── TILES ARE NOT CHILDREN OF _gridRoot ──────────────────────────────
                // 2026-10-19 Spencer: "two boards were populating at once, one was
                // underneath this one." Destroying the root takes the board panel, frame
                // and cell grid with it, but TILES are parented elsewhere — so every tile
                // from the PREVIOUS layout stayed alive in the scene, still sitting at
                // coordinates computed for the old cell size and row count. The grid is
                // genuinely built twice per run (measured in debug_log: 9x5 at frame 0,
                // 9x11 at frame 38), so the menu board's tiles were still on screen
                // underneath the survival board. The stray "floating tile with blended
                // letters" was one of them, landing mid-board in the NEW layout.
                //
                // DESTROY rather than pool them: the cell size changes between layouts, so
                // a recycled tile would carry a stale scale and position. The pool must go
                // too — it holds tiles built for the old geometry.
                int killed = 0;
                for (int c = 0; c < COLS; c++)
                for (int r = 0; r < ROWS; r++)
                {
                    if (_tiles[c, r] != null) { Destroy(_tiles[c, r].gameObject); _tiles[c, r] = null; killed++; }
                    _cells[c, r] = null;
                }
                foreach (var pooled in _tilePool) if (pooled != null) { Destroy(pooled.gameObject); killed++; }
                _tilePool.Clear();
                _allTileObjects.Clear();
                if (killed > 0)
                    WDLog.Log($"[Grid] rebuild — destroyed {killed} tile(s) from the previous layout " +
                              $"(they are not children of GridRoot, so Destroy(root) would have left them).");

                Destroy(_gridRoot);
                _gridRoot = null;
                _specFrameGO = null; _specInnerGO = null; _specShadowGO = null; _gridBackgroundSR = null;
                _cellGridGO = null;
            }
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
                if (!UseBoardSpec)
                    CreateBackgroundPanel((GridRight - GridLeft) + halfGapPad * 2f,
                                          (GridTop   - GridBottom) + halfGapPad * 2f);
            }
            else
            {
                float bgPadding = CellSize * 0.16f;
                if (!UseBoardSpec)
                    CreateBackgroundPanel((GridRight - GridLeft) + bgPadding * 2f,
                                          (GridTop   - GridBottom) + bgPadding * 2f);
            }
            // 2026-09-03: with UseBoardSpec the board is drawn to the exact spec HERE,
            // paired with the root that was just created. (Drawing the legacy
            // grid-derived panel as well put two offset rounded rects on screen.)
            if (UseBoardSpec && _specUsed)
                CreateSpecBoardPanel(_specBoardW, _specBoardH, _specBoardCentreY);

            // Cell sockets, baked into one sprite. Must come AFTER the board panel so it
            // layers on top of it (order 1 vs 0) and below the tiles (5).
            CreateCellGridOverlay();

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

        // ── TOP-OUT DANGER PULSE (2026-09-03 Spencer) ────────────────────────────────
        // Tiles sitting in the top row(s) pulse RED. The HUD already shows a
        // moves-until-top-out number, but nobody watching the board reads a number —
        // this puts the warning where the player is actually looking.
        // Driven through the ADDITIVE bloom overlay (SetBloomGlow) rather than the face
        // colour, so it layers over primed magenta / scored green / gold without
        // fighting any of them.
        public static bool  DANGER_PULSE_ENABLED = true;
        public static int   DANGER_ROWS          = 2;      // how many top rows count as danger
        private static readonly Color DANGER_HDR = new Color(1.7f, 0.25f, 0.2f, 1f); // HDR red
        private const float DANGER_BLINK_SECONDS = 1.1f;   // length of one alert burst
        private const int   DANGER_BLINK_COUNT   = 3;      // blinks within that burst
        private float _dangerBlinkUntil;
        private readonly System.Collections.Generic.HashSet<Tile> _dangerTiles
            = new System.Collections.Generic.HashSet<Tile>();

        private void UpdateTopOutDanger()
        {
            // 2026-09-08: was gated on IsSurvivalMode, so this never fired in the endless
            // LEVEL — the mode we actually playtest. The real condition is "can this board
            // top out", i.e. rows are rising. Survival qualifies; so does any rising level.
            bool canTopOut = SurvivalManager.IsSurvivalMode
                          || (SurvivalManager.Instance != null && !SurvivalManager.Instance.ActiveRisesOff);
            if (SurvivalManager.FullBoardActive) canTopOut = false;   // full board is ALWAYS at the top — nothing to warn about
            if (!DANGER_PULSE_ENABLED || !canTopOut)
            {
                if (_dangerTiles.Count > 0)
                {
                    foreach (var t in _dangerTiles) if (t != null) t.SetDangerPulse(0f);
                    _dangerTiles.Clear();
                }
                return;
            }

            // ── BLINK, DON'T HOLD (2026-09-09 Spencer) ───────────────────────────────
            // This used to pulse CONTINUOUSLY between 0.25 and 0.85, so a tile in the top
            // rows was permanently part-red. Two problems: a warning that is always on stops
            // being a warning, and a permanently tinted tile reads as a different KIND of
            // tile rather than a state — which is why it looked like an orange piece.
            // Now it fires a short BURST of blinks when tiles first enter the danger zone,
            // then releases them back to their real colour. It re-arms whenever a NEW tile
            // enters, so each rise that puts you in trouble gets its own alert.
            var stillInDanger = new System.Collections.Generic.HashSet<Tile>();
            int firstDangerRow = Mathf.Max(0, ROWS - DANGER_ROWS);
            for (int row = firstDangerRow; row < ROWS; row++)
                for (int col = 0; col < COLS; col++)
                {
                    Tile t = _tiles[col, row];
                    if (t == null) continue;
                    stillInDanger.Add(t);
                    if (!_dangerTiles.Contains(t)) _dangerBlinkUntil = Time.time + DANGER_BLINK_SECONDS;
                }

            // Full-strength blinks while the burst is live, then clear. Reaching 1.0 at the
            // peak (the old pulse capped at 0.85) is what makes it read as a FLASH rather
            // than a tint; the troughs go to 0 so the tile's real colour shows between them.
            float pulse = 0f;
            if (Time.time < _dangerBlinkUntil)
            {
                float t01 = 1f - (_dangerBlinkUntil - Time.time) / DANGER_BLINK_SECONDS;
                pulse = Mathf.Abs(Mathf.Sin(t01 * Mathf.PI * DANGER_BLINK_COUNT));
            }
            foreach (var t in stillInDanger) if (t != null) t.SetDangerPulse(pulse);

            // anything that LEFT the danger zone (detonated, or gravity pulled it down)
            foreach (var t in _dangerTiles)
                if (t != null && !stillInDanger.Contains(t)) t.SetDangerPulse(0f);

            _dangerTiles.Clear();
            foreach (var t in stillInDanger) _dangerTiles.Add(t);
        }

        private void Update()
        {
            UpdateTopOutDanger();

            // Live cell-liner tuning — see ApplyCellLinerTuning. Cheap (stored SRs, no GetComponent).
            ApplyCellLinerTuning();

            // 2026-06-05 Spencer: \ flips the BOARD drop shadow A↔B in place (logs which).
            if (Input.GetKeyDown(KeyCode.Backslash)) Tile.FlipBoardShadow();

            // Live board-shadow darkness — drag _boardShadowStrength in the Inspector.
            Tile.SetBoardShadowStrength(_boardShadowStrength);
        }

        /// <summary>Draw every cell socket into ONE sprite and lay it over the board panel.
        /// Aligned off the real grid geometry (GridLeft/GridBottom/CellSize, and RowPitch ==
        /// CellSize) rather than by dividing the panel up, so it cannot drift out of register
        /// with the tiles. 2026-09-10.</summary>
        /// <summary>Re-bake the sockets for the current level's board shape (voids get none). Called after a level installs.</summary>
        public void RebuildCellGridForShape()
        {
            if (_cellGridGO != null) { UnregisterEntry(_cellGridGO.transform); Destroy(_cellGridGO); _cellGridGO = null; }
            CreateCellGridOverlay();
            RebuildShapeCover();
        }

        private void UnregisterEntry(Transform t)
        {
            int i = _boardEntryTfs.IndexOf(t);
            if (i >= 0) { _boardEntryTfs.RemoveAt(i); if (i < _boardEntryRests.Count) _boardEntryRests.RemoveAt(i); }
        }

        // ── SHAPED BOARD COVER (2026-10-28): on a level with void cells the rectangular frame / panel / shadow are hidden and ONE
        // pre-rendered picture takes their place: Resources/Boards/shape_<name>.png, made by WordDrop_icon_gen/board_frame/
        // render_shapes.py from the same mask (frame.py: RK gold rod round the real cells, rounded convex + concave corners,
        // dark-glass panel at GLASS_ALPHA with its inner shade, soft shadow). The picture covers the cell grid plus
        // SHAPE_PAD_CELLS of a cell on every side; the frosted background is clipped to its alpha. Rectangles keep the old art.
        public const float SHAPE_PAD_CELLS = 0.35f;
        private GameObject _shapeGO;
        private void RebuildShapeCover()
        {
            if (_shapeGO != null) { UnregisterEntry(_shapeGO.transform); Destroy(_shapeGO); _shapeGO = null; }
            bool shaped = RulesEngine.HasVoids && !string.IsNullOrEmpty(RulesEngine.BoardShape);
            Texture2D tex = shaped ? Resources.Load<Texture2D>("Boards/shape_" + RulesEngine.BoardShape + (RulesEngine.BOARD_PRESET == 1 ? "_8x10" : RulesEngine.BOARD_PRESET == 2 ? "_7x9" : "")) : null;   // 11-02: 8x10 art
            if (shaped && tex == null) WDLog.Log($"[Shape] no art Boards/shape_{RulesEngine.BoardShape} - keeping the rectangular frame");
            bool use = tex != null;
            void Show(GameObject go, bool on) { if (go == null) return; var r = go.GetComponent<SpriteRenderer>(); if (r != null) r.enabled = on; }
            Show(_specFrameGO, !use); Show(_specInnerGO, !use); Show(_specShadowGO, !use);
            SpriteMask oldMask = _specInnerGO != null ? _specInnerGO.GetComponentInChildren<SpriteMask>(true) : null;
            if (oldMask != null && oldMask.gameObject.name == "BoardGlassMask") oldMask.enabled = !use;
            ApplyRefillMaskShape();   // 11-02: the fall clip follows the real cells on a shaped board
            if (!use) return;
            float gw = COLS * CellSize, gh = ROWS * RowPitch, padX = SHAPE_PAD_CELLS * CellSize, padY = SHAPE_PAD_CELLS * RowPitch;
            _shapeGO = new GameObject("ShapedBoard");
            _shapeGO.transform.SetParent(_gridRoot.transform, false);
            _shapeGO.transform.position = new Vector3(GridLeft + gw * 0.5f, GridBottom + gh * 0.5f, 0f);   // rest position (the entry slide offsets it, like CellGrid)
            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            var sr = _shapeGO.AddComponent<SpriteRenderer>(); sr.sprite = sp; sr.sortingOrder = 0;   // panel slot: frost -1 < this < sockets 1
            _shapeGO.transform.localScale = new Vector3((gw + 2 * padX) / (tex.width / 100f), (gh + 2 * padY) / (tex.height / 100f), 1f);
            if (BOARD_GLASS)
            {   // frost shows through the shaped panel only
                var mGO = new GameObject("ShapedGlassMask"); mGO.transform.SetParent(_shapeGO.transform, false);
                var m = mGO.AddComponent<SpriteMask>(); m.sprite = sp; m.alphaCutoff = 0.3f;
                m.isCustomRangeActive = true; m.backSortingOrder = -2; m.frontSortingOrder = -1;
            }
            if (_boardEntryPrepped) { _boardEntryTfs.Add(_shapeGO.transform); _boardEntryRests.Add(_shapeGO.transform.position); ApplyBoardEntryOffset(_boardEntryOffX); }
            WDLog.Log($"[Shape] board cover '{RulesEngine.BoardShape}' {tex.width}x{tex.height}");
        }

        private void CreateCellGridOverlay()
        {
            if (!CELL_GRID_ENABLED || _gridRoot == null) return;

            // 2026-09-22: 96 -> 128. A cell is ~110 px on a 1179-wide phone, so at 96 the socket
            // was baked at 86 texels and upsampled 28% — which softens exactly the 3 px bevel
            // Spencer's art relies on. 128 puts it near 1:1. Costs 9x11x128^2 RGBA = ~6.5 MB,
            // in line with the tile sprites already resident.
            const int PX = 128;                      // texels per cell
            int texW = COLS * PX, texH = ROWS * PX;
            var px = new Color[texW * texH];
            for (int i = 0; i < px.Length; i++) px[i] = Color.clear;

            var socketTex = string.IsNullOrEmpty(CELL_SOCKET_ASSET)
                          ? null : Resources.Load<Texture2D>(CELL_SOCKET_ASSET);

            if (socketTex != null)
            {
                // Resample the authored socket ONCE, then memcpy it into all 99 cells. Sampling
                // per cell would be ~1.6M bilinear fetches on the frame the board is built —
                // the same shape as the first-row-rise hitch that cost a session on 2026-09-21.
                int sw = Mathf.Clamp(Mathf.RoundToInt(PX * CELL_SOCKET_W_FRAC), 2, PX);
                int sh = Mathf.Clamp(Mathf.RoundToInt(sw * (socketTex.height / (float)socketTex.width)), 2, PX);
                var cell = new Color[sw * sh];
                for (int y = 0; y < sh; y++)
                    for (int x = 0; x < sw; x++)
                        cell[y * sw + x] = socketTex.GetPixelBilinear((x + 0.5f) / sw, (y + 0.5f) / sh);

                int ox0 = (PX - sw) / 2, oy0 = (PX - sh) / 2;   // his art is centred, so this centres the shape
                for (int col = 0; col < COLS; col++)
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (RulesEngine.IsVoid(col, row)) continue;   // shaped board: no socket on a void
                        int ox = col * PX + ox0, oy = row * PX + oy0;
                        for (int y = 0; y < sh; y++)
                            System.Array.Copy(cell, y * sw, px, (oy + y) * texW + ox, sw);
                    }
                WDLog.Log($"[Grid] cell sockets = authored {CELL_SOCKET_ASSET} " +
                          $"({socketTex.width}x{socketTex.height} -> {sw}x{sh} of a {PX} cell)");
            }
            else
            {
                int inset  = Mathf.RoundToInt(PX * Mathf.Clamp01(CELL_GRID_INSET));
                int side   = Mathf.Max(2, PX - inset * 2);
                int radius = Mathf.Clamp(Mathf.RoundToInt(PX * CELL_GRID_RADIUS), 0, side / 2);

                for (int col = 0; col < COLS; col++)
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (RulesEngine.IsVoid(col, row)) continue;
                        Color c = (((col + row) & 1) == 0) ? CELL_GRID_A : CELL_GRID_B;
                        int ox = col * PX + inset, oy = row * PX + inset;
                        for (int y = 0; y < side; y++)
                            for (int x = 0; x < side; x++)
                            {
                                float cov = SocketCoverage(x, y, side, radius);
                                if (cov <= 0f) continue;
                                Color cc = c; cc.a *= cov;
                                px[(oy + y) * texW + (ox + x)] = cc;
                            }
                    }
                if (Tile.BOARD_C && TILE_CONTACT_SHADOW) BakeTileContactShadows(px, texW, texH, PX);
                WDLog.Log("[Grid] cell sockets = procedural checkerboard (no authored asset)");
            }

            var tex = new Texture2D(texW, texH, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode   = TextureWrapMode.Clamp;
            tex.SetPixels(px); tex.Apply();

            float gw = COLS * CellSize, gh = ROWS * RowPitch;
            var go = new GameObject("CellGrid"); _cellGridGO = go;
            go.transform.SetParent(_gridRoot.transform, false);
            go.transform.position = new Vector3(GridLeft + gw * 0.5f, GridBottom + gh * 0.5f, 0f);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = Sprite.Create(tex, new Rect(0, 0, texW, texH), new Vector2(0.5f, 0.5f), 100f);
            sr.sortingOrder = 1;                     // board panel 0 < grid 1 < tiles 5
            // GLASS BOARD (2026-10-28 Spencer: "the box indentations on the board are still that blue colour"). The sockets
            // were opaque navy 113B77 over the see-through panel, covering most of it. With the glass on they become a dark,
            // partly transparent layer: the socket shape + bevel still read as indentations, the frosted scene still shows.
            if (BOARD_GLASS) sr.color = new Color(GLASS_SOCKET_MUL.r, GLASS_SOCKET_MUL.g, GLASS_SOCKET_MUL.b, GLASS_SOCKET_ALPHA);
            go.transform.localScale = new Vector3(gw / (texW / 100f), gh / (texH / 100f), 1f);

            // Born mid-entry? Park it with everything else, same as the panel does.
            if (_boardEntryPrepped)
            {
                _boardEntryTfs.Add(go.transform); _boardEntryRests.Add(go.transform.position);
                ApplyBoardEntryOffset(_boardEntryOffX);
            }
        }

        /// <summary>2x2-supersampled coverage of a rounded square — antialiases the socket
        /// corners so they do not read as stair-steps when the sprite is scaled.</summary>
        // ── BOARD C CONTACT SHADOW (11-02 Spencer: "the mock up had a bit more depth with the letter tiles"). The mock drew a
        // soft dark-violet shadow under every tile (offset below the base, blurred) - the board's hue, not black. Baked into the
        // cell grid (one sprite, no per-tile renderers), so it sits under each RESTING cell; while a cell is empty mid-refill it
        // reads as a soft recess. Numbers are fractions of the tile width (mock: 100px tile, offset 12, blur 4, alpha .55).
        public static bool  TILE_CONTACT_SHADOW = false;   // 11-06 Spencer: OFF - static per-cell shadow reads as an off-shaped smudge behind specials/moving tiles. true = back
        public static float TCS_OFFSET = 0.11f, TCS_BLUR = 0.05f, TCS_ALPHA = 0.50f, TCS_RADIUS = 0.22f;
        public static Color TCS_COLOR = new Color(0.165f, 0.141f, 0.408f, 1f);   // #2A2468
        private static void BakeTileContactShadows(Color[] px, int texW, int texH, int PX)
        {
            float tw = PX * Tile.TILE_DISPLAY_RATIO, half = tw * 0.5f, rad = tw * TCS_RADIUS, blur = Mathf.Max(0.5f, tw * TCS_BLUR);
            float off = tw * TCS_OFFSET;
            int pad = Mathf.CeilToInt(blur * 2f + off);
            int sw = PX + 2 * pad;
            var stamp = new float[sw * sw];   // stamp centred on the cell centre, shadow shape shifted DOWN (texture y is up)
            for (int y = 0; y < sw; y++)
                for (int x = 0; x < sw; x++)
                {
                    float dx = Mathf.Abs(x + 0.5f - sw * 0.5f) - (half - rad);
                    float dy = Mathf.Abs(y + 0.5f - (sw * 0.5f - off)) - (half - rad);
                    float sd = new Vector2(Mathf.Max(dx, 0f), Mathf.Max(dy, 0f)).magnitude + Mathf.Min(Mathf.Max(dx, dy), 0f) - rad;
                    stamp[y * sw + x] = TCS_ALPHA * (1f - Mathf.SmoothStep(0f, 1f, (sd + blur) / (2f * blur)));
                }
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    if (RulesEngine.IsVoid(col, row)) continue;
                    int ox = col * PX - pad, oy = row * PX - pad;
                    for (int y = 0; y < sw; y++)
                    {
                        int ty = oy + y; if (ty < 0 || ty >= texH) continue;
                        for (int x = 0; x < sw; x++)
                        {
                            int tx = ox + x; if (tx < 0 || tx >= texW) continue;
                            float sa = stamp[y * sw + x]; if (sa <= 0.002f) continue;
                            int i = ty * texW + tx; Color d = px[i];
                            float oa = sa + d.a * (1f - sa);
                            if (oa <= 0f) continue;
                            float k = d.a * (1f - sa);
                            px[i] = new Color((TCS_COLOR.r * sa + d.r * k) / oa, (TCS_COLOR.g * sa + d.g * k) / oa, (TCS_COLOR.b * sa + d.b * k) / oa, oa);
                        }
                    }
                }
        }

        private static float SocketCoverage(int x, int y, int side, int r)
        {
            float acc = 0f;
            for (int sy = 0; sy < 2; sy++)
                for (int sx = 0; sx < 2; sx++)
                {
                    float fx = x + 0.25f + sx * 0.5f;
                    float fy = y + 0.25f + sy * 0.5f;
                    if (r <= 0)
                    {
                        if (fx >= 0f && fy >= 0f && fx < side && fy < side) acc += 0.25f;
                        continue;
                    }
                    float cx = Mathf.Clamp(fx, r, side - r);
                    float cy = Mathf.Clamp(fy, r, side - r);
                    float dx = fx - cx, dy = fy - cy;
                    if (dx * dx + dy * dy <= (float)r * r) acc += 0.25f;
                }
            return acc;
        }

        /// <summary>Board + frame drawn to the 2026-09-03 spec: two rounded rects,
        /// outer 1143x1415 r38 and inner 1121x1395 r27, radii scaled with the board.</summary>
        /// <summary>The authored frame as a 9-sliced sprite, or null to fall back.
        /// FRAME_SOURCE_W is the width of the ORIGINAL export the compact cut came from — the
        /// corner must draw at (border / sourceWidth) of the board, not at its texel size.</summary>
        // 2026-10-28 Spencer: "reproduce the board golden frame" (Royal Kingdom). board_frame_rk@2x = Spencer's frame cut, SAME
        // geometry (so it still meets inner_board exactly), band repainted with RK's measured gold ROD profile (dark amber edges ->
        // gold -> pale #FFE37A ridge in the middle) + a warm seam on the panel side. "Tiles/board_frame@2x" = his original flat band.
        public static string FRAME_ASSET    = "Tiles/board_frame_rk@2x";
        public static int    FRAME_BORDER_PX = 90;     // matches spriteBorder in the .meta
        public static float  FRAME_SOURCE_W  = 2294f;  // Spencer's export width
        public static int    FRAME_CUT_W      = 188;   // the cut on disk: 90 + 8 + 90
        private static Sprite s_slicedFrame; private static float s_slicedFrameW;

        // 11-16 LOOK PASS (Spencer picked mock B): the lavender panel (107,111,184, S .42) was 36% of the board and read as dusk.
        // inner_board_royal@2x = the same 9-slice, every texel scaled to (64,104,226). BOARD_TINT is declared ABOVE INNER_ASSET:
        // static initializers run in file order, so below it INNER_ASSET read it as false (11-16 shot: still lavender).
        // 11-16 Spencer: "can we try the sky blue board?" -> BOARD_TINT picks the panel: "royal" (64,104,226), "sky" (88,160,238,
        // inner_board_sky@2x, same per-channel scaling), "lavender" (the original). Watch ice on sky - the mock showed it losing contrast.
        public static string BOARD_TINT = "sky";
        public static string INNER_ASSET     = Tile.BOARD_C ? "Tiles/inner_board_" + BOARD_TINT + "@2x" : "Tiles/inner_board@2x";   // 11-02 board C
        public static int    INNER_BORDER_PX  = 120;   // clears the 47px corner arc with room to spare
        public static int    INNER_CUT_W      = 248;   // the cut on disk: 120 + 8 + 120
        public static float  INNER_SOURCE_W   = 2242f; // Spencer's export width (= SPEC_PLAY_W x2)
        private static Sprite s_slicedInner; private static float s_slicedInnerW;

        private static Sprite LoadSlicedInner(float innerW)
        {
            if (innerW <= 0.0001f) return null;
            if (s_slicedInner != null && Mathf.Abs(s_slicedInnerW - innerW) < 0.001f) return s_slicedInner;
            var tex = Resources.Load<Texture2D>(INNER_ASSET);
            if (tex == null) return null;
            float texScale = tex.width / (float)INNER_CUT_W;      // see the note in LoadSlicedFrame
            if (Mathf.Abs(texScale - 1f) > 0.001f)
                Debug.LogWarning($"[Grid] inner_board loaded at {tex.width}px, not {INNER_CUT_W} — check its .meta.");
            float bpx = INNER_BORDER_PX * texScale;
            float ppu = INNER_SOURCE_W / innerW * texScale;
            s_slicedInner = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                          new Vector2(0.5f, 0.5f), ppu, 0,
                                          SpriteMeshType.FullRect, new Vector4(bpx, bpx, bpx, bpx));
            s_slicedInnerW = innerW;
            return s_slicedInner;
        }

        // ── Board drop shadow (2026-09-22) ────────────────────────────────────────
        // Spencer's frame_shadow@2x, rebuilt for LINEAR blending and cut as a 9-slice.
        // PAD_* is where the FRAME sits inside the shadow's canvas. MEASURED off the KNOCKOUT
        // boundary: Photoshop's "Layer Knocks Out Drop Shadow" zeroes the shadow exactly where
        // the casting layer is opaque, so the first zero after the ramp IS the frame's own edge.
        // That is exact, unlike correlating a blurred silhouette — a correlation fit put PAD_TOP
        // at 98 and left a visible 6 px gap between the shadow and the top of the rim.
        // Self-checks: the knockout box came out 2284x2828 against a frame of 2294x2838 — 5 px
        // inset on every side, which is the frame's own AA ramp, identical on all four edges.
        // 74 + 2838 + 154 = 3066 and 32 + 2294 + 32 = 2358, both exactly the canvas.
        // 2026-10-28 Spencer: "we need some shadowing and blending of the board to the background". frame_shadow_std@2x = the
        // CONTACT SHADOW STANDARD (measured RK): straight down, warm brown #3A1E12, bg 0.51 at the edge -> 0.96 over ~1.2% of the
        // screen width, short soft sides, nothing on top. Same canvas/pads/9-slice as Spencer's frame_shadow@2x (built by
        // WordDrop_icon_gen/board_frame/shadow/board_shadow.py). His (dark grey band on the sides, faint below): "Tiles/frame_shadow@2x".
        public static string FRAME_SHADOW_ASSET     = "Tiles/frame_shadow_std@2x";
        public static int    FRAME_SHADOW_BORDER_PX = 280;
        public static int    FRAME_SHADOW_CUT_W     = 568;   // 280 + 8 + 280
        public static float  FRAME_SHADOW_SRC_W     = 2358f; // his canvas
        public static float  FRAME_SHADOW_PAD_X     = 80f;   // frame inset inside that canvas. 11-02: 32 -> 80, the STRONGER shadow (board_shadow_strong.py) needs room for its blur at the sides
        public static float  FRAME_SHADOW_PAD_TOP   = 74f;
        public static float  FRAME_SHADOW_PAD_BOT   = 154f;  // => a 40 texel drop at 2x
        private static Sprite s_slicedShadow; private static float s_slicedShadowW;

        private static Sprite LoadSlicedShadow(float boardW)
        {
            if (boardW <= 0.0001f) return null;
            if (s_slicedShadow != null && Mathf.Abs(s_slicedShadowW - boardW) < 0.001f) return s_slicedShadow;
            var tex = Resources.Load<Texture2D>(FRAME_SHADOW_ASSET);
            if (tex == null) return null;
            float texScale = tex.width / (float)FRAME_SHADOW_CUT_W;
            if (Mathf.Abs(texScale - 1f) > 0.001f)
                Debug.LogWarning($"[Grid] frame_shadow loaded at {tex.width}px, not {FRAME_SHADOW_CUT_W} — check its .meta.");
            float bpx = FRAME_SHADOW_BORDER_PX * texScale;
            // SAME scale as the frame (FRAME_SOURCE_W / boardW), so the blur keeps a constant
            // world thickness against the frame's corners instead of drifting with board height.
            float ppu = FRAME_SOURCE_W / boardW * texScale;
            s_slicedShadow = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                           new Vector2(0.5f, 0.5f), ppu, 0,
                                           SpriteMeshType.FullRect, new Vector4(bpx, bpx, bpx, bpx));
            s_slicedShadowW = boardW;
            return s_slicedShadow;
        }

        private static Sprite LoadSlicedFrame(float boardW)
        {
            if (boardW <= 0.0001f) return null;
            if (s_slicedFrame != null && Mathf.Abs(s_slicedFrameW - boardW) < 0.001f) return s_slicedFrame;
            var tex = Resources.Load<Texture2D>(FRAME_ASSET);
            if (tex == null) return null;

            // 🚨 THE BORDER IS IN TEXELS OF THE TEXTURE UNITY ACTUALLY LOADED, which is not
            // necessarily the file on disk. 2026-09-22: the .meta shipped with nPOTScale: 1, so
            // Unity silently resampled the 188x188 cut up to 256x256 on import. A hardcoded 90
            // then pointed at the wrong texels — the corners drew undersized and the edge strips
            // sampled the empty middle, giving four gold curls and no frame. nPOTScale is 0 now,
            // but scaling by the loaded width means a future rescale degrades instead of breaking.
            float texScale = tex.width / (float)FRAME_CUT_W;
            if (Mathf.Abs(texScale - 1f) > 0.001f)
                Debug.LogWarning($"[Grid] board_frame loaded at {tex.width}px, not {FRAME_CUT_W} " +
                                 $"— check nPOTScale/maxTextureSize in its .meta. Border scaled x{texScale:F3}.");
            float bpx = FRAME_BORDER_PX * texScale;

            // the whole source maps to boardW, so a texel is boardW / FRAME_SOURCE_W world units
            float ppu = FRAME_SOURCE_W / boardW * texScale;
            var border = new Vector4(bpx, bpx, bpx, bpx);
            s_slicedFrame = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                          new Vector2(0.5f, 0.5f), ppu, 0,
                                          SpriteMeshType.FullRect, border);
            s_slicedFrameW = boardW;
            return s_slicedFrame;
        }

        private void CreateSpecBoardPanel(float boardW, float boardH, float centreY)
        {
            if (_gridRoot == null)
            {
                Debug.LogError("[Grid] CreateSpecBoardPanel: _gridRoot is NULL — BuildGridVisuals must run first.");
                return;
            }
            int texW = Mathf.Clamp(Mathf.RoundToInt(boardW * 150f), 64, 2048);
            int texH = Mathf.Clamp(Mathf.RoundToInt(boardH * 150f), 64, 2048);
            WDLog.Log($"[Grid] spec panel tex {texW}x{texH}  rOuter/rInner/border scaled from spec");
            float pxPerSpec = texW / SPEC_BOARD_W;               // texels per spec pixel
            int rOuter = Mathf.Max(2, Mathf.RoundToInt(SPEC_R_OUTER * pxPerSpec));
            int rInner = Mathf.Max(2, Mathf.RoundToInt(SPEC_R_INNER * pxPerSpec));
            int borderPx = Mathf.Max(1, Mathf.RoundToInt((SPEC_BOARD_W - SPEC_PLAY_W) * 0.5f * pxPerSpec));

            // outer frame — a HOLLOW RING, not a filled slab. 2026-09-10: this used to pass
            // FRAME_OUTER as BOTH fill and border, i.e. a solid gold rectangle that the opaque
            // inner panel happened to cover all but the rim of. The moment the panel was made
            // see-through, that gold slab showed straight through the whole board and it went
            // brown. A transparent fill + gold border of the real border width gives the same
            // rim when the panel is opaque, and lets the scene through when it is not.
            // AUTHORED 9-SLICE FRAME (2026-09-22). Spencer's board_frame@2x is a compact
            // 188x188 cut of a 2294x2838 export: four corners at full detail plus an 8px strip of
            // each edge, which stretches. Falls back to the procedural ring if the asset is gone.
            //
            // 🔑 THE PPU IS THE WHOLE TRICK. Sliced draw mode renders the border at
            // borderPixels / PPU world units, so a FIXED ppu would keep the corners a constant
            // world size while the board resized — thin corners on a big screen, fat on a small
            // one. Deriving it from boardW keeps the corner a constant PROPORTION of the board,
            // which is the reason to 9-slice instead of just stretching the sprite.
            // Shadow first, behind everything (scene bg is the camera, not a sprite, in spec mode).
            Sprite shadowSprite = LoadSlicedShadow(boardW);
            if (shadowSprite != null)
            {
                float u = boardW / FRAME_SOURCE_W;                    // world units per canvas texel
                var shGO = new GameObject("BoardFrameShadow");
                shGO.transform.SetParent(_gridRoot.transform, false);
                // the canvas is padded 98 above / 130 below the frame, so its centre sits
                // (130-98)/2 = 16 texels BELOW the board's — that offset IS the drop.
                shGO.transform.position = new Vector3(
                    0f, centreY - (FRAME_SHADOW_PAD_BOT - FRAME_SHADOW_PAD_TOP) * 0.5f * u, 0f);
                var shSR = shGO.AddComponent<SpriteRenderer>();
                shSR.sprite    = shadowSprite;
                shSR.drawMode  = SpriteDrawMode.Sliced;
                shSR.size      = new Vector2(boardW + FRAME_SHADOW_PAD_X * 2f * u,
                                             boardH + (FRAME_SHADOW_PAD_TOP + FRAME_SHADOW_PAD_BOT) * u);
                shSR.sortingOrder = -2;                               // shadow -2 < frame -1 < panel 0
                _specShadowGO = shGO;
                if (_boardEntryPrepped)
                { _boardEntryTfs.Add(shGO.transform); _boardEntryRests.Add(shGO.transform.position); }
                WDLog.Log($"[Grid] board shadow = 9-slice frame_shadow@2x, size {shSR.size.x:F2}x{shSR.size.y:F2}");
            }

            var frameGO = new GameObject("BoardFrame"); _specFrameGO = frameGO;
            frameGO.transform.SetParent(_gridRoot.transform, false);
            frameGO.transform.position = new Vector3(0f, centreY, 0f);
            var fsr = frameGO.AddComponent<SpriteRenderer>();
            fsr.sortingOrder = -1;

            Sprite sliced = LoadSlicedFrame(boardW);
            if (sliced != null)
            {
                fsr.sprite    = sliced;
                fsr.drawMode  = SpriteDrawMode.Sliced;
                fsr.size      = new Vector2(boardW, boardH);
                frameGO.transform.localScale = Vector3.one;
                WDLog.Log($"[Grid] board frame = 9-slice board_frame@2x, size {boardW:F2}x{boardH:F2}");
            }
            else
            {
                var frame = TileRenderer.CreateRoundedRect(texW, texH, rOuter, new Color(0f, 0f, 0f, 0f), FRAME_OUTER, borderPx);
                if (frame == null) { Debug.LogError("[Grid] frame sprite NULL"); return; }
                fsr.sprite = frame;
                frameGO.transform.localScale = new Vector3(boardW / (texW / 100f), boardH / (texH / 100f), 1f);
                WDLog.Log("[Grid] board frame = procedural ring (board_frame@2x not found)");
            }

            // inner board
            int itexW = Mathf.Max(8, texW - borderPx * 2);
            int itexH = Mathf.Max(8, texH - borderPx * 2);
            var innerGO = new GameObject("GridBackground"); _specInnerGO = innerGO;
            innerGO.transform.SetParent(_gridRoot.transform, false);
            innerGO.transform.position = new Vector3(0f, centreY, 0f);
            var isr = innerGO.AddComponent<SpriteRenderer>();
            isr.sortingOrder = 0;
            _gridBackgroundSR = isr;
            float innerW = boardW * (SPEC_PLAY_W / SPEC_BOARD_W);
            float innerH = boardH * (SPEC_PLAY_H / SPEC_BOARD_H);

            // Authored panel, 9-sliced for the same reason the frame is: the board is NOT a
            // fixed aspect — it is built at 9x5 and at 9x11 in the same session — so a stretched
            // sprite would squash the corner radius by half between them. Sliced keeps the
            // corner a constant proportion of the board's WIDTH on every layout.
            Sprite innerSliced = LoadSlicedInner(innerW);
            if (innerSliced != null)
            {
                isr.sprite   = innerSliced;
                isr.drawMode = SpriteDrawMode.Sliced;
                isr.size     = new Vector2(innerW, innerH);
                innerGO.transform.localScale = Vector3.one;
                WDLog.Log($"[Grid] inner board = 9-slice inner_board@2x, size {innerW:F2}x{innerH:F2}");
            }
            else
            {
                var inner = TileRenderer.CreateRoundedRect(itexW, itexH, rInner, BOARD_INNER, BOARD_INNER, 1);
                if (inner == null) { Debug.LogError("[Grid] inner sprite NULL"); return; }
                isr.sprite = inner;
                innerGO.transform.localScale = new Vector3(innerW / (itexW / 100f), innerH / (itexH / 100f), 1f);
                WDLog.Log("[Grid] inner board = procedural rounded rect (inner_board@2x not found)");
            }

            BuildRefillMask(innerGO, innerW, innerH);
            BuildBoardGlass(innerGO, isr, innerW, innerH);

            // If the entry slide already parked the board off-screen before this panel
            // existed, register + park it now so it does not pop in at rest position.
            if (_boardEntryPrepped)
            {
                _boardEntryTfs.Add(frameGO.transform); _boardEntryRests.Add(frameGO.transform.position);
                _boardEntryTfs.Add(innerGO.transform); _boardEntryRests.Add(innerGO.transform.position);
                ApplyBoardEntryOffset(_boardEntryOffX);
            }
        }

        // ── DARK GLASS BOARD (2026-10-28 Spencer: "tinted very dark and see through where you can slightly see the
        // background" - chose 80% + light frost from board_spec/compare_glass.png). The panel (inner_board 9-slice) is tinted
        // toward GLASS_TINT and made GLASS_ALPHA opaque; behind it a FROSTED copy of the scene background (UI/bg_blur = bg@2x
        // half-res + gaussian, same world size) sits exactly over ScreenBackground, clipped to the panel by a SpriteMask whose
        // custom sorting range only covers the frosted copy (tiles, refill masks untouched). The frosted copy does NOT move
        // with the board (it's a window onto the static scene). BOARD_GLASS = false -> the old solid navy panel.
        public static bool  BOARD_GLASS  = !Tile.BOARD_C;   // 11-02 board C = an opaque lavender panel, no glass
        public static float GLASS_ALPHA  = 0.80f;
        // panel texel 1A468E -> tint 0A183E: per-channel ratio computed in LINEAR space (the project is linear), as sRGB
        public static Color GLASS_PANEL_MUL = new Color(0.578f, 0.422f, 0.459f, 1f);
        // socket fill 113B77 -> a deep 040A1C, ratio in LINEAR space; drawn at GLASS_SOCKET_ALPHA over the glass panel
        public static Color GLASS_SOCKET_MUL = new Color(0.503f, 0.292f, 0.278f, 1f);
        public static float GLASS_SOCKET_ALPHA = 0.35f;
        private GameObject _glassBgGO;
        private void BuildBoardGlass(GameObject innerGO, SpriteRenderer panel, float innerW, float innerH)
        {
            if (_glassBgGO != null) { Destroy(_glassBgGO); _glassBgGO = null; }
            if (!BOARD_GLASS || panel == null) return;
            var c = GLASS_PANEL_MUL; c.a = GLASS_ALPHA; panel.color = c;
            var blur = Resources.Load<Sprite>("UI/bg_blur");
            var screenBg = GameObject.Find("ScreenBackground");
            if (blur == null || screenBg == null) { WDLog.Log($"[Glass] frosted bg skipped (blur {(blur != null)}, ScreenBackground {(screenBg != null)}) - panel is see-through to the plain scene"); return; }
            _glassBgGO = new GameObject("BoardGlassFrost");
            _glassBgGO.transform.position = screenBg.transform.position + new Vector3(0f, 0f, -0.01f);
            _glassBgGO.transform.localScale = screenBg.transform.localScale;
            var fsr = _glassBgGO.AddComponent<SpriteRenderer>();
            fsr.sprite = blur; fsr.sortingOrder = -1;
            fsr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
            // mask = the panel's rounded rect (procedural; SpriteMask can't use a sliced sprite)
            int mw = 256, mh = Mathf.Max(8, Mathf.RoundToInt(256f * innerH / Mathf.Max(innerW, 0.001f)));
            int mr = Mathf.Max(2, Mathf.RoundToInt(SPEC_R_INNER / SPEC_PLAY_W * mw));
            var mSprite = TileRenderer.CreateRoundedRect(mw, mh, mr, Color.white, Color.white, 1);
            var mGO = new GameObject("BoardGlassMask"); mGO.transform.SetParent(innerGO.transform, false);
            mGO.transform.localScale = new Vector3(innerW / (mw / 100f), innerH / (mh / 100f), 1f);
            var mask = mGO.AddComponent<SpriteMask>(); mask.sprite = mSprite;
            mask.isCustomRangeActive = true; mask.backSortingOrder = -2; mask.frontSortingOrder = -1;   // only the frost (-1)
            WDLog.Log($"[Glass] panel alpha {GLASS_ALPHA} tint {GLASS_PANEL_MUL} + frosted bg, mask {mw}x{mh} r{mr}");
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
            // Bias every row up by half the tile's overshoot past one cell. With the old
            // tilted art that was +0.10 of a cell, letting the top face stick up behind the
            // tile above. FRONT-ON art is 0.96 cells tall, so this is now ~-0.02 — near zero,
            // which is correct: there is no top face to hide any more. 2026-09-15.
            const float overshootHalf = 0f;   // front-on art fills its pitch exactly — no overhang to bias for
            float y = GridBottom + (row + 0.5f + overshootHalf) * RowPitch;
            // UPPER rows must draw IN FRONT so each tile's top face is hidden behind the
            // tile above it. Done with Z (sprites of equal sortingOrder sort by depth) so
            // the existing sortingOrder logic — including the FX bump to 15 — is untouched.
            float z = -row * ROW_Z_STEP;
            return new Vector3(x, y, z);
        }

        /// <summary>MVP P5 booster aim mode: convert a world-space position to
        /// the grid cell beneath it. Returns false if the position is outside
        /// the board bounds.</summary>
        public bool WorldToCell(Vector3 worldPos, out int col, out int row)
        {
            col = Mathf.FloorToInt((worldPos.x - GridLeft) / CellSize);
            // 2026-09-03: MUST invert CellToWorld exactly. CellToWorld biases every row
            // UP by half the tile's overshoot (so the bottom row sits flush with the board
            // floor); without the same bias here, world->cell and cell->world disagree and
            // a tap resolves to the WRONG ROW — taps landing on unrelated tiles, swaps
            // starting on the wrong cell.
            const float overshootHalfW = 0f;  // must mirror CellToWorld or taps hit the wrong row
            row = Mathf.FloorToInt((worldPos.y - GridBottom) / RowPitch - overshootHalfW);
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
//                 WDLog.Log($"[GridManager] Column {col} is full — ignoring drop.");
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

//             WDLog.Log($"[GridManager] Dropped '{letter}' → col={col}, row={targetRow}");

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
                for (int row = 0; row < MAX_ROWS; row++)   // all rows — see RebuildFromRulesEngine (home-screen 5-row leak)
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

//             WDLog.Log("[GridManager] All cells cleared.");
        }

        /// <summary>
        /// FULL REBUILD: Destroys all visual tiles and recreates from RulesEngine data.
        /// This is the nuclear option — guarantees visuals match data with zero drift.
        /// </summary>
        public void RebuildFromRulesEngine(RulesEngine rules)
        {
            if (rules == null) return;

            // Return all existing tiles to pool — ALL rows, not just the current layout's. 2026-10-21: the home screen shrinks
            // ROWS to 5, so a rebuild there skipped rows 5-10 and leaked a full board's top rows (162 SCENE ORPHAN errors —
            // invisible on the old sparse board, a whole 54-tile block on the full board).
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < MAX_ROWS; row++)
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
                    if (rulesCell.ShelfPart > 0) { tile.SetShelf(rulesCell.ShelfPart, rulesCell.ShelfBooks, rulesCell.ShelfStyle, rulesCell.ShelfKind); tile.SetStoneVisual(true); }   // BOOKSHELF (11-03): anchored, but not a vault
                    else if (rulesCell.IsAnchored) { tile.SetVaultRequirement(rulesCell.RequiredWordLength); tile.SetVaultVisual(true); } // anchored = treasure vault
                    else if (rulesCell.IsDropTarget) tile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                    else if (rulesCell.IsStone || rulesCell.Letter == '#') { tile.SetTreasureHits(rulesCell.TreasureHits); tile.SetCrateHP(rulesCell.CrateHP); tile.SetStoneVisual(true); }
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

//             WDLog.Log($"[GridManager] RebuildFromRulesEngine — created {created} tiles.");
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
        /// <summary>Force every tile onto its exact cell position.
        ///
        /// Rises tween with Ease.OutBack, which OVERSHOOTS the target and settles back. Fire
        /// several in quick succession — as the starved-board refill does — and a new rise can
        /// begin from a not-yet-settled position, compounding small errors. The board sync
        /// only corrects drift above 0.01 world units (~1.5px here), so anything under that
        /// survives as a dark seam where the board shows between tiles. This removes the
        /// residue outright. 2026-09-09 Spencer.</summary>
        public void SnapAllTilesToCells()
        {
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    var t = _tiles[col, row];
                    if (t == null || t.IsAnimating) continue;
                    t.transform.DOKill();
                    t.transform.position = CellToWorld(col, row);
                    // Scale too. Position-only left tiles stranded mid-pop at the wrong
                    // size, which shows as purple gaps between letters. 2026-09-10 Spencer.
                    t.SnapRestScale();
                }
        }

        public void SyncToRulesState(RulesEngine rules)
        {
            // Every board sync re-asserts sorting order, so an expired FX lift cannot persist.
            for (int _c = 0; _c < COLS; _c++)
                for (int _r = 0; _r < ROWS; _r++)
                {
                    _tiles[_c, _r]?.ReassertSortingOrder();
                    _tiles[_c, _r]?.RefreshArmedFace();   // gold art converges, never sticks rose
                }
            // Cluster outlines are rebuilt at the END of this method — see the call below.
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
                        // Both exist — snap position and sync letter if mismatched.
                        //
                        // 2026-10-20: this used a 0.01 threshold against a ~0.96 cell, i.e. 1% of
                        // a cell, and did NOT skip tiles that were mid-tween. Measured in a live
                        // log: 23 of 37 "pos drift" fixes printed an identical before and after —
                        // it was snapping tiles that were already correct, every single sync.
                        // Worse, a tile one hundredth of a cell into a gravity drop got TELEPORTED
                        // to its destination, which is where the dropped frames and the stutter in
                        // gravity/rise/swap animations come from. Two changes:
                        //   • skip IsAnimating tiles — a tween is the legitimate owner of that
                        //     position until it finishes (BoardInvariants already does this),
                        //   • threshold at a quarter cell: large enough to ignore float noise and
                        //     easing overshoot, small enough that a genuinely wrong tile (always
                        //     out by a whole cell or more) is still caught.
                        Vector3 correctPos = CellToWorld(col, row) + new Vector3(_boardSlideOffsetX, 0f, 0f);
                        if (!visualTile.IsAnimating &&
                            Vector3.Distance(visualTile.transform.position, correctPos) > CellSize * 0.25f)
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
                        // The resting face is per-letter (gold for high-value). SetLetter only
                        // paints it while the tile is already resting, so a letter that arrived
                        // mid-cascade kept the wrong face forever. This sync runs after every
                        // board change and is the one place that sees every tile — correct it
                        // here rather than chasing each path. No-op when not resting. 2026-09-08.
                        visualTile.RefreshRestingFace();
                        visualTile.SyncSelectRim();   // rim must agree with selection — see Tile.SyncSelectRim
                        // Vaults: a live letter tile can be converted into a vault in-place
                        // (SeedVaultBoard flips IsStone+IsAnchored on an existing cell). Reflect it —
                        // anchored → treasure chest, plain stone → grey.
                        if (rulesCell.ShelfPart > 0)
                        {   // BOOKSHELF (11-03): anchored, but drawn as the shelf - never the vault chest
                            if (visualTile.ShelfPart != rulesCell.ShelfPart || visualTile.ShelfBooks != rulesCell.ShelfBooks || visualTile.ShelfStyle != rulesCell.ShelfStyle || visualTile.ShelfKind != rulesCell.ShelfKind || !visualTile.IsStone)
                            { visualTile.SetShelf(rulesCell.ShelfPart, rulesCell.ShelfBooks, rulesCell.ShelfStyle, rulesCell.ShelfKind); visualTile.SetStoneVisual(true); fixed_count++; }
                        }
                        else if (rulesCell.IsAnchored && !visualTile.IsVault)
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
                            visualTile.SetTreasureHits(rulesCell.TreasureHits); visualTile.SetCrateHP(rulesCell.CrateHP);
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
                        if (rulesCell.ShelfPart > 0) { tile.SetShelf(rulesCell.ShelfPart, rulesCell.ShelfBooks, rulesCell.ShelfStyle, rulesCell.ShelfKind); tile.SetStoneVisual(true); }   // BOOKSHELF (11-03)
                        else if (rulesCell.IsAnchored) tile.SetVaultVisual(true);   // anchored = treasure vault (chest)
                        else if (rulesCell.IsDropTarget) tile.SetDropTargetVisual(true); // HeroWord escort object — NOT a grey rock
                        else if (rulesCell.IsStone || rulesCell.Letter == '#') { tile.SetTreasureHits(rulesCell.TreasureHits); tile.SetCrateHP(rulesCell.CrateHP); tile.SetStoneVisual(true); }
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

        
            // ONE rebuild per sync — the outline must never outlive the cluster it traces.
            ClusterOutline.Instance?.Refresh();
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
            const float overshootHalfW2 = 0f; // see WorldToCell above
            int row = Mathf.FloorToInt((worldPos.y - GridBottom) / RowPitch - overshootHalfW2);  // was CellSize — a real bug once RowPitch != CellSize
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

//             WDLog.Log($"[GridManager] RemoveTiles — removed {removedCount} tile(s) " +
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
        /// <summary>Visual mirror of RulesEngine.IsFixedCell (anchored, or a crate with FIXED_CRATES on the full board; ducks fall).</summary>
        /// <summary>11-04: falls follow the plan's route (down, diagonal round a crate, down). false = straight line to the end cell.</summary>
        public static bool FALL_ROUTES = true;
        private float _routedFallSince = -1f;
        /// <summary>A booster's routed fall is still stepping through its waves (self-clears after 12 s so a lost coroutine can
        /// never silence the integrity checker for good).</summary>
        public bool RoutedFallPlaying => _routedFallSince >= 0f && Time.unscaledTime - _routedFallSince < 12f;
        public static bool FALL_ROUTE_LOG = true;   // TEMP DIAG 11-04 - see PlayFallWaves
        /// <summary>TEMP (11-04): slow-motion for routed falls so an odd path can be watched (FX menu toggle). 1 = normal.</summary>
        public static float FALL_SLOWMO = 1f;
        private readonly Dictionary<Vector2Int, List<Vector2Int>> _refillRoutes = new Dictionary<Vector2Int, List<Vector2Int>>();
        private readonly Dictionary<Vector2Int, List<int>> _refillPhases = new Dictionary<Vector2Int, List<int>>();
        private readonly List<(Tile tile, List<Vector3> pts, List<int> phase, float delay)> _waveFalls = new List<(Tile, List<Vector3>, List<int>, float)>();
        private bool _wavesOn;
        private readonly HashSet<Tile> _waveClip = new HashSet<Tile>();
        /// <summary>11-04: play a routed fall in its plan's waves - each wave every piece takes its ONE step for that wave (a fall
        /// into the slot, or a diagonal step), then the next wave starts once they've all arrived. Wave 0 keeps the column stagger.</summary>
        private IEnumerator PlayFallWaves()
        {
            var falls = new List<(Tile tile, List<Vector3> pts, List<int> phase, float delay)>(_waveFalls); _waveFalls.Clear();
            int maxPh = 0; foreach (var f in falls) foreach (var p in f.phase) maxPh = Mathf.Max(maxPh, p);
            // TEMP DIAG (11-04, Spencer: a letter "seemed to thread between a crate in a very odd pattern"): every routed piece's
            // cells + waves, so the odd one can be read back from the log. Strip once the slide is signed off.
            if (FALL_ROUTE_LOG)
                foreach (var f in falls)
                {
                    if (f.tile == null) continue;
                    var sb = new System.Text.StringBuilder($"[FallRoute] '{f.tile.Letter}'");
                    for (int k = 0; k < f.pts.Count; k++) { var cxy = WorldToCell(f.pts[k]); sb.Append(k == 0 ? " " : $" -w{f.phase[k - 1]}-> ").Append($"({cxy.x},{cxy.y})"); }
                    WDLog.Log(sb.ToString());
                }
            for (int ph = 0; ph <= maxPh; ph++)
            {
                var moving = new List<Tile>();
                foreach (var f in falls)
                {
                    if (f.tile == null) continue;
                    int k = f.phase.IndexOf(ph); if (k < 0) continue;
                    var a = f.pts[k]; var b = f.pts[k + 1];
                    bool diag = Mathf.Abs(a.x - b.x) > 0.01f;
                    f.tile.AnimateFallStep(b, diag, k + 1 == f.pts.Count - 1, ph == 0 ? f.delay : 0f);
                    moving.Add(f.tile);
                }
                for (int guard = 0; guard < 600; guard++)
                {
                    bool any = false; foreach (var t in moving) if (t != null && t.IsFalling) { any = true; break; }
                    if (!any) break;
                    ClusterOutline.Instance?.Refresh();
                    yield return null;
                }
            }
            foreach (var t in _waveClip) if (t != null) SetRefillClip(t, false);
            _waveClip.Clear();
        }
        private List<Vector3> RouteToWorld(List<Vector2Int> cells, float z)
        {
            var pts = new List<Vector3>(cells.Count);
            foreach (var c in cells) { var w = CellToWorld(c.x, c.y); w.z = z; pts.Add(w); }
            return pts;
        }
        /// <summary>Any board tile still in a gravity fall (pace tracking).</summary>
        public bool AnyTileFalling()
        {
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) { var t = _tiles[c, r]; if (t != null && t.IsFalling) return true; }
            return false;
        }
        public static bool IsFixedTile(Tile t) => t != null && (t.IsAnchored || (RulesEngine.FIXED_CRATES && SurvivalManager.FullBoardActive && t.IsStone && !t.IsDropTargetVisual));

        // ── LATE HOLE (11-24, Spencer: the flying tome "holding the letters from falling until the animation is done" -> "i want it
        // to be like royal match"). The rules clear the tome's target in the same step as the word, and the picture used to freeze
        // the whole board for the flight (~0.6 s) before one fall. Now the picture plays that SAME fall in two parts, RM's way:
        //   A) at once: everything falls as if the target were still standing. The target tile (a "ghost": off the grid arrays, so
        //      this fall is planned on exactly the board the rules have) drops with its column to where it will rest, and the tiles
        //      above it stop ONE ROW short of their final cells.
        //   B) when the tome lands: the ghost pops and those tiles drop the last row (the column's last new letter comes in).
        // The grid arrays hold the FINAL cells from the start of A, so the picture and the rules never disagree about who is where;
        // ApplyGravity only returns after B, so no word is looked for on a half-fallen board.
        // HandManager.TryFallDuringFlight decides when this applies (one plain tome, a plain letter target, no crates/shelves/gaps).
        private class LateHole
        {
            public Tile Ghost; public int Col, Row0, RowA; public bool Landed;
            public readonly List<KeyValuePair<Tile, Vector3>> Waiting = new List<KeyValuePair<Tile, Vector3>>();   // tile -> its final spot
            public readonly List<Tile> Hidden = new List<Tile>();                                                   // new letters parked above the board
            public readonly List<System.Action> AtLanding = new List<System.Action>();
        }
        public static float LATE_DROP_AFTER = 0.22f;   // seconds between the tome landing and the column above its target dropping
        private LateHole _late;
        private bool LateStaging => _late != null && !_late.Landed;
        /// <summary>Take the tome's target tile off the grid (it stays on screen). <paramref name="rowA"/> = the row it will rest on
        /// once the tiles under it have fallen. Null = not possible (then nothing changes).</summary>
        public Tile BeginLateHole(int col, int row, int rowA)
        {
            if (_late != null || col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            var t = _tiles[col, row]; if (t == null) return null;
            _tiles[col, row] = null; _cells[col, row] = null;
            _late = new LateHole { Ghost = t, Col = col, Row0 = row, RowA = Mathf.Clamp(rowA, 0, row) };
            WDLog.Log($"[Tome] the board falls during the flight: target ({col},{row}) will rest on row {_late.RowA}");
            return t;
        }
        public void LateHoleLanded() { if (_late != null) _late.Landed = true; }
        /// <summary>The ghost's pop has played: back to the pool. Also drops the late hole if no fall ever picked it up.</summary>
        public void EndLateHole(Tile ghost)
        {
            if (ghost != null) ReturnTile(ghost);
            if (_late != null && _late.Ghost == ghost && _late.Waiting.Count == 0 && _late.Hidden.Count == 0 && _late.AtLanding.Count == 0) _late = null;
        }
        /// <summary>Part B: the tome has landed (or 3 s passed) - the held tiles drop their last row. Returns when they have.</summary>
        private IEnumerator LateHoleFinish()
        {
            var late = _late; if (late == null) yield break;
            float w = 0f; while (!late.Landed && w < 3f) { w += Time.unscaledDeltaTime; yield return null; }
            if (!late.Landed) WDLog.Log("[Tome] late hole: the landing never came - releasing the column");
            // 11-24 (Spencer's recording Movie_037: the tome broke the tile under an owl "and the owl dropped very quickly"): the
            // column used to drop on the very frame the tome landed, INTO the tile that was still bursting, so the owl seemed to
            // snap down. A normal pop gets ~0.3 s before its fall; the landing gets a beat too - the tile breaks, THEN the column drops.
            else { float h = 0f; while (h < LATE_DROP_AFTER) { h += Time.unscaledDeltaTime; yield return null; } }
            if (_late == late) _late = null;
            var moving = new List<Tile>();
            foreach (var kv in late.Waiting)
            {
                var tile = kv.Key; if (tile == null) continue;
                float d = Vector3.Distance(tile.transform.position, kv.Value);
                if (d <= 0.02f) { tile.transform.position = kv.Value; continue; }
                tile.AnimateGravityFall(kv.Value, d / FallSpeed, 0f);
                if (late.Hidden.Contains(tile) && REFILL_CLIP) StartCoroutine(ClipWhileFalling(tile));
                moving.Add(tile);
            }
            foreach (var act in late.AtLanding) act?.Invoke();
            int safety = 0; bool any = true;
            while (any && safety < 600)
            {
                safety++; any = false;
                for (int i = 0; i < moving.Count; i++) if (moving[i] != null && moving[i].IsFalling) { any = true; break; }
                if (any) { ClusterOutline.Instance?.Refresh(); yield return null; }
            }
        }

        /// <summary>11-24 OWLS FALL OUT (see RulesEngine.OwlsFallOutInData): take the owls the data has just sent home off the grid,
        /// BEFORE this fall is planned, and let each one fall on its own to the bottom row of its exit column and on into its nest.
        /// The column above it then falls all the way in the same pass.</summary>
        private void OwlsFallOutVisual()
        {
            if (RulesEngine.OwlsLeft.Count == 0) return;
            var list = new List<RulesEngine.OwlExit>(RulesEngine.OwlsLeft); RulesEngine.OwlsLeft.Clear();
            bool levelUniform = GameManager.IsLevelMode;
            foreach (var e in list)
            {
                Vector3 target = CellToWorld(e.Col, 0);
                Tile t = (e.From.x >= 0 && e.From.x < COLS && e.From.y >= 0 && e.From.y < ROWS) ? _tiles[e.From.x, e.From.y] : null;
                if (t == null || !t.IsDropTargetVisual)
                {   // the picture has no owl there (should not happen): still count it home so the level can finish
                    WDLog.Log($"[Owl] fall-out: NO owl tile at ({e.From.x},{e.From.y}) - sent home without the fall");
                    SurvivalManager.Instance?.OwlArrived(e.Col, target + new Vector3(_boardSlideOffsetX, 0f, 0f));
                    continue;
                }
                if (LateStaging && e.From.x == _late.Col && e.Col == _late.Col && e.From.y > _late.Row0)
                {   // a tome is flying to a tile UNDER this owl: it comes down with its column to just above that tile, and falls out
                    // when the tome lands (part B of the late hole)
                    int between = 0; for (int y = _late.Row0 + 1; y < e.From.y; y++) if (_tiles[e.From.x, y] != null) between++;
                    _tiles[e.From.x, e.From.y] = null; _cells[e.From.x, e.From.y] = null;
                    Vector3 wait = CellToWorld(e.Col, _late.RowA + 1 + between);
                    float dw = Vector3.Distance(t.transform.position, wait);
                    if (dw > 0.02f) t.AnimateGravityFall(wait, dw / FallSpeed, e.From.x * 0.045f);
                    var ot = t; int oc = e.Col; Vector3 otarget = target;
                    _late.AtLanding.Add(() =>
                    {
                        if (ot == null) { SurvivalManager.Instance?.OwlArrived(oc, otarget + new Vector3(_boardSlideOffsetX, 0f, 0f)); return; }
                        float d2 = Vector3.Distance(ot.transform.position, otarget);
                        if (d2 > 0.02f) ot.AnimateGravityFall(otarget, d2 / FallSpeed, 0f);
                        StartCoroutine(OwlFallOutRoutine(ot, oc, otarget));
                    });
                    continue;
                }
                _tiles[e.From.x, e.From.y] = null; _cells[e.From.x, e.From.y] = null;
                float dist = Vector3.Distance(t.transform.position, target);
                if (dist > 0.02f) t.AnimateGravityFall(target, levelUniform ? 0.30f : dist / FallSpeed, levelUniform ? 0f : e.From.x * 0.045f);   // the same fall the column above it gets
                StartCoroutine(OwlFallOutRoutine(t, e.Col, target));
            }
        }
        private IEnumerator OwlFallOutRoutine(Tile t, int col, Vector3 target)
        {
            yield return null;
            float waited = 0f;
            while (t != null && waited < 4f && (t.IsAnimating || Mathf.Abs(t.transform.position.y - target.y) > CellSize * 0.06f)) { waited += Time.deltaTime; yield return null; }
            SurvivalManager.Instance?.OwlArrived(col, target + new Vector3(_boardSlideOffsetX, 0f, 0f));
            if (t != null) ReturnTile(t);
        }

        public IEnumerator ApplyGravity()
        {
            OwlsFallOutVisual();   // 11-24: owls the data sent home leave the grid before the fall is planned
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

            // 11-04: the SAME plan as the data (RulesEngine.PlanFall) - fixed crates + the diagonal slide. Before, this method
            // re-derived the fall column by column on its own, which is the desync landmine (HANDOFF 4.2) once pieces can move
            // sideways. A tile here without its CellData (or the reverse) is dropped from the arrays, as the old repack did.
            var occ = new bool[COLS, ROWS]; var fix = new bool[COLS, ROWS];
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                {
                    if (_tiles[col, row] == null || _cells[col, row] == null) { _tiles[col, row] = null; _cells[col, row] = null; continue; }
                    // Fixed pieces never move in the data, so the data cell at the same spot says whether this one is fixed - more
                    // reliable than the tile's own flags (a stone face not yet synced). The tile flags are the fallback.
                    var rc = RulesEngine.Instance != null ? RulesEngine.Instance.GetCell(col, row) : null;
                    occ[col, row] = true; fix[col, row] = rc != null ? RulesEngine.IsFixedCell(rc) : IsFixedTile(_tiles[col, row]);
                }
            var plan = RulesEngine.PlanFall(occ, fix, RulesEngine.FallSpawnRows);
            _refillRoutes.Clear();
            for (int i = plan.Count - 1; i >= 0; i--) if (plan[i].from.y >= ROWS) { _refillRoutes[plan[i].to] = plan[i].path; _refillPhases[plan[i].to] = plan[i].phase; plan.RemoveAt(i); }   // new pieces: SpawnRefillTiles
            // A diagonal step anywhere in this fall -> play it in WAVES (fall, slide, fall, ...), RM style; else the plain falls.
            bool phased = false;
            if (FALL_ROUTES) foreach (var m in plan) if (m.path != null) for (int k = 1; k < m.path.Count; k++) if (m.path[k].x != m.path[k - 1].x) phased = true;
            if (FALL_ROUTES) foreach (var kv in _refillRoutes) for (int k = 1; k < kv.Value.Count; k++) if (kv.Value[k].x != kv.Value[k - 1].x) phased = true;
            _waveFalls.Clear(); _wavesOn = phased;
            // LATE HOLE part A (see above): the ghost drops to its resting row; tiles that end at or above that row stop one row short
            bool lateStage = LateStaging && !phased;
            if (LateStaging && phased) WDLog.Log("[Tome] late hole: this fall has a diagonal step - the column is not staged");
            if (lateStage && _late.Ghost != null)
            {
                Vector3 gp = CellToWorld(_late.Col, _late.RowA); float gd = Vector3.Distance(_late.Ghost.transform.position, gp);
                if (gd > 0.02f) _late.Ghost.AnimateGravityFall(gp, levelUniform ? UNIFORM_FALL_DURATION : gd / FallSpeed, levelUniform ? 0f : _late.Col * STAGGER_PER_COL);
            }
            var mTiles = new Tile[plan.Count]; var mCells = new CellData[plan.Count];
            for (int i = 0; i < plan.Count; i++)
            {
                var f = plan[i].from;
                mTiles[i] = _tiles[f.x, f.y]; mCells[i] = _cells[f.x, f.y];
                _tiles[f.x, f.y] = null; _cells[f.x, f.y] = null;
            }
            for (int i = 0; i < plan.Count; i++)
            {
                var t = plan[i].to; Tile tile = mTiles[i]; CellData cell = mCells[i];
                cell.Row = t.y; cell.Col = t.x;
                _tiles[t.x, t.y] = tile; _cells[t.x, t.y] = cell;
                tile.UpdateGridPosition(t.x, t.y);

                Vector3 targetPos = CellToWorld(t.x, t.y);
                if (lateStage && t.x == _late.Col && t.y >= _late.RowA)
                {   // above the tome's target: one row short for now, the last row when the tome lands
                    _late.Waiting.Add(new KeyValuePair<Tile, Vector3>(tile, targetPos));
                    targetPos = CellToWorld(t.x, t.y + 1);
                }
                float   dist      = Vector3.Distance(tile.transform.position, targetPos);
                if (dist > 0.02f)
                {
                    float fallDuration = levelUniform ? UNIFORM_FALL_DURATION : dist / FallSpeed;
                    float colDelay = levelUniform ? 0f : plan[i].from.x * STAGGER_PER_COL;
                    if (phased) _waveFalls.Add((tile, RouteToWorld(plan[i].path, targetPos.z), plan[i].phase, colDelay));   // played in waves below
                    else tile.AnimateGravityFall(targetPos, fallDuration, colDelay);
                    if (GRAVITY_CLIP && REFILL_CLIP && RulesEngine.HasVoids) StartCoroutine(ClipWhileFalling(tile));   // 11-02: hidden while crossing a gap (GRAVITY_CLIP off: suspected in the row-of-specials bug)
                    animatingTiles.Add(tile);
                    totalMoved++;
                }
                else tile.transform.position = targetPos;
            }

            _lateStageRefill = lateStage;
            totalMoved += SpawnRefillTiles(animatingTiles);   // full board: new letters fall in with the rest
            _lateStageRefill = false;
            if (_wavesOn) { _wavesOn = false; yield return StartCoroutine(PlayFallWaves()); }

            if (totalMoved > 0)
            {
//                 WDLog.Log($"[GridManager] ApplyGravity — {totalMoved} tile(s) falling.");

                // Wait for all gravity animations to complete
                int  safety       = 0;
                bool anyAnimating = true;

                while (anyAnimating && safety < 600)
                {
                    safety++;
                    anyAnimating = false;
                    for (int i = 0; i < animatingTiles.Count; i++)
                    {
                        // FALLING, not "animating" (2026-10-23): IsAnimating includes external FX claims (the hint claimed 4.5s),
                        // and a fall waited on them. Only the fall itself decides when gravity is done.
                        if (animatingTiles[i] != null && animatingTiles[i].IsFalling)
                        {
                            anyAnimating = true;
                            break;
                        }
                    }
                    if (anyAnimating)
                    {
                        // Ride the fall. The outline traces the tiles' LIVE positions, so
                        // retracing each frame keeps the line on the letters all the way
                        // down instead of waiting at the destination for them to arrive.
                        // 2026-09-09 Spencer.
                        ClusterOutline.Instance?.Refresh();
                        yield return null;
                    }
                }

//                 WDLog.Log("[GridManager] ApplyGravity — all tiles settled.");
            }
            else
            {
//                 WDLog.Log("[GridManager] ApplyGravity — no tiles needed to move.");
            }
        
            if (_late != null) yield return LateHoleFinish();   // LATE HOLE part B: wait for the tome, then the held tiles drop the last row

            // Letters may have been pulled apart — drop any word that no longer exists
            // BEFORE retracing, so the outline never traces a word that just died.
            RulesEngine.Instance?.ValidatePrimedWords();
            ClusterOutline.Instance?.Refresh();   // tiles moved — retrace the cluster outlines
}

        /// <summary>
        /// Event-driven gravity: moves specific tiles based on RulesEngine event data.
        /// More reliable than re-scanning because it uses the source-of-truth positions.
        /// </summary>
        /// <summary>FULL BOARD (2026-10-21): make tiles for cells the data refilled during gravity (RulesEngine.FillTopsInData).
        /// Each column's new letters start stacked just above the board and fall the same distance, so the column comes
        /// down as one piece together with the tiles already falling. Adds them to <paramref name="animating"/>.</summary>
        private bool _lateStageRefill;
        public int SpawnRefillTiles(List<Tile> animating)
        {
            if (!SurvivalManager.FullBoardActive) return 0;
            var rules = RulesEngine.Instance; if (rules == null) return 0;
            const float STAGGER_PER_COL = 0.045f;
            int spawned = 0; bool anyGold = false;
            for (int col = 0; col < COLS; col++)
            {
                int newCount = 0;
                for (int row = 0; row < ROWS; row++) { var d = rules.GetCell(col, row); if (d != null && d.Letter != '\0' && _tiles[col, row] == null) newCount++; }
                if (newCount == 0) continue;
                for (int row = 0; row < ROWS; row++)
                {
                    var d = rules.GetCell(col, row);
                    if (d == null || d.Letter == '\0' || _tiles[col, row] != null) continue;
                    Tile tile = CreateSingleTile(col, row, d.Letter);
                    if (tile == null) continue;
                    Vector3 target = CellToWorld(col, row);
                    int voidTop = RulesEngine.HasVoids ? Mathf.Max(0, ROWS - 1 - RulesEngine.TopRealRow(col)) : 0;   // shaped board: start above the BOARD top, not inside a void corner
                    Vector3 start  = CellToWorld(col, row + newCount + voidTop); start.z = target.z;
                    tile.transform.position = start;
                    bool lateTile = _lateStageRefill && _late != null && col == _late.Col;   // LATE HOLE: this column's new letters stop one row short too
                    if (lateTile)
                    {
                        _late.Waiting.Add(new KeyValuePair<Tile, Vector3>(tile, target));
                        if (row + 1 >= ROWS)
                        {   // its waiting place is above the board: it stays out of sight up there and comes in when the tome lands
                            _late.Hidden.Add(tile); if (REFILL_CLIP) SetRefillClip(tile, true);
                            if (d.IsRainGold) { tile.SetRainGold(true); anyGold = true; }
                            tile.SetGem(d.HasGem); if (!d.IsRainGold) tile.ForceRestingFace();
                            if (d.IsDropTarget) tile.SetDropTargetVisual(true);
                            spawned++;
                            continue;
                        }
                        target = CellToWorld(col, row + 1);
                    }
                    if (d.IsRainGold) { tile.SetRainGold(true); anyGold = true; }
                    // ALWAYS set the gem state, both ways (2026-10-23: "a bunch of letters just turned into gems" — a whole refilled
                    // row arrived gold). Recycled tiles can carry the gold face of a gem-armed word; SetGem(false) re-applies the
                    // plain resting face. Was: only SetGem(true) for gems, so a leftover face was never cleared.
                    tile.SetGem(d.HasGem);
                    if (!d.IsRainGold) tile.ForceRestingFace();   // SetGem is a no-op when the flag already matches — the stale FACE is the bug
                    if (d.IsDropTarget) tile.SetDropTargetVisual(true);   // duck (2026-10-22)
                    if (_wavesOn && _refillRoutes.TryGetValue(new Vector2Int(col, row), out var rr) && rr.Count >= 2 && _refillPhases.TryGetValue(new Vector2Int(col, row), out var rp))
                    {   // 11-04: the plan's route, played in waves with the rest (it may step diagonally into a gap under a crate)
                        var pts = RouteToWorld(rr, target.z);
                        tile.transform.position = pts[0];
                        _waveFalls.Add((tile, pts, rp, rr[0].x * STAGGER_PER_COL));
                        if (REFILL_CLIP) { SetRefillClip(tile, true); _waveClip.Add(tile); }   // clipped for ALL its waves (it can wait above the rim between them)
                    }
                    else
                    {
                        tile.AnimateGravityFall(target, Vector3.Distance(start, target) / FallSpeed, col * STAGGER_PER_COL);
                        if (REFILL_CLIP) StartCoroutine(ClipWhileFalling(tile));   // hidden above the board, clipped at the rim
                    }
                    animating?.Add(tile);
                    spawned++;
                }
            }
            _refillRoutes.Clear(); _refillPhases.Clear();   // the routes belong to this one fall
            if (anyGold) BonusPopup.Instance?.Show("GOLDEN LETTERS!", new Color(1f, 0.85f, 0.3f, 1f), Vector3.zero, 1.1f);
            return spawned;
        }

        /// <summary>REFILL ENTRY (2026-10-21 Spencer: "i dont think the letters should be visible above the board ... shouldnt
        /// they look like they are coming from inside the board like in other match 3 games?"). A real SpriteMask can't clip
        /// the TextMeshPro letters, so new tiles are fully transparent while above the board's top edge and fade in as they
        /// cross it — at falling speed it reads as emerging from the board.</summary>
        // ── REFILL LID (2026-10-21 Spencer: "it looks like it is a ghost appearing. shouldnt it just be masked so its invisible
        // until it passes the yellow of the rim on the board?"). A SpriteMask can't clip the TextMeshPro letters, so instead
        // of cutting the tiles we COVER them: an exact copy of the screen background + the board's frame and its shadow,
        // drawn in FRONT of every tile (44/45 > tiles' max 43) but behind FX (46+), and masked to the area ABOVE the inner
        // edge of the rim. Above the rim you see the identical scene; tiles appear crisply as they pass under the rim.
        // Copies are children of their originals so they ride the entry slide; the bg copy + mask are tracked here.
        public static bool REFILL_LID = false;   // 2026-10-21: the lid showed a seam above the board (bg copy misaligned). Replaced by REFILL_CLIP.
        private GameObject _lidMaskGO, _lidBgGO;
        private static Sprite s_lidMaskSprite;
        private void BuildRefillLid(SpriteRenderer frameSR, float innerTopY)
        {
            if (_lidMaskGO != null) Destroy(_lidMaskGO);
            if (_lidBgGO != null) Destroy(_lidBgGO);
            if (!REFILL_LID || frameSR == null) return;
            if (s_lidMaskSprite == null)
            {
                var tex = new Texture2D(4, 4); var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
                tex.SetPixels(px); tex.Apply();
                s_lidMaskSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0f), 4f);   // 1x1 unit, pivot bottom-centre
            }
            _lidMaskGO = new GameObject("RefillLidMask");
            _lidMaskGO.transform.position = new Vector3(0f, innerTopY, 0f);
            _lidMaskGO.transform.localScale = new Vector3(200f, 200f, 1f);   // everything above the rim, full width
            var mask = _lidMaskGO.AddComponent<SpriteMask>();
            mask.sprite = s_lidMaskSprite;

            System.Func<SpriteRenderer, int, GameObject> copy = (src, order) =>
            {
                if (src == null) return null;
                var go = new GameObject(src.gameObject.name + "_Lid");
                go.transform.SetParent(src.transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = src.sprite; sr.drawMode = src.drawMode; if (src.drawMode != SpriteDrawMode.Simple) sr.size = src.size;
                sr.color = src.color; sr.sortingLayerID = src.sortingLayerID; sr.sortingOrder = order;
                sr.sharedMaterial = src.sharedMaterial;
                sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                return go;
            };
            var bg = GameObject.Find("ScreenBackground");
            if (bg != null) _lidBgGO = copy(bg.GetComponent<SpriteRenderer>(), 44);
            if (_specShadowGO != null) copy(_specShadowGO.GetComponent<SpriteRenderer>(), 44);   // same order as bg, nearer z → draws over it
            copy(frameSR, 45);
            WDLog.Log($"[Grid] refill lid built above y={innerTopY:F2} (bg={(bg != null)})");
        }

        // ── REFILL CLIP (2026-10-21 Spencer: "the rim should obscure the letters but you shouldnt be able to see anything odd").
        // A real SpriteMask the size of the board interior, child of the inner board (rides the entry slide). ONLY tiles
        // falling in from a refill opt into it, and only until they land — every other tile, the slide and all FX are
        // untouched. The letters (TextMeshPro) don't take SpriteMask, so their material stencil is set to read the same
        // stencil the mask writes (ref 1, LessEqual) for the duration of the fall.
        public static bool REFILL_CLIP = true;
        public static bool GRAVITY_CLIP = false;   // 11-02: clip falling board tiles on shaped boards - OFF while the 'row turned into rockets' bug is open
        private GameObject _refillMaskGO;
        private static Sprite s_refillMaskSprite;
        private void BuildRefillMask(GameObject innerGO, float innerW, float innerH)
        {
            if (_refillMaskGO != null) Destroy(_refillMaskGO);
            if (!REFILL_CLIP || innerGO == null) return;
            if (s_refillMaskSprite == null)
            {
                var tex = new Texture2D(4, 4); var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
                tex.SetPixels(px); tex.Apply();
                s_refillMaskSprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);   // 1x1 unit, centred
            }
            _refillMaskGO = new GameObject("RefillClipMask");
            _refillMaskGO.transform.SetParent(innerGO.transform, false);
            _refillMaskGO.transform.localPosition = Vector3.zero;
            var ls = innerGO.transform.lossyScale;
            _refillMaskGO.transform.localScale = new Vector3(innerW / Mathf.Max(0.0001f, ls.x), innerH / Mathf.Max(0.0001f, ls.y), 1f);
            var m = _refillMaskGO.AddComponent<SpriteMask>();
            m.sprite = s_refillMaskSprite;
            WDLog.Log($"[Grid] refill clip mask {innerW:F2}x{innerH:F2}");
            _refillMaskRectSaved = false;   // a new mask: remember ITS rectangle
            ApplyRefillMaskShape();
        }

        // ── SHAPED FALL CLIP (2026-11-02, Spencer: "i can see the letters dropping through the openings of the board ... the letters
        // need to not be visible outside the board when they are falling or being added"). On a board with void cells the clip mask
        // is the REAL CELLS only (one texel per cell, point-filtered), not the panel rectangle - so a tile falling through a gap, or
        // arriving above a notched column, is hidden until it is inside a cell. Rectangular boards keep the panel rectangle.
        private Vector3 _refillMaskRectPos, _refillMaskRectScale; private bool _refillMaskRectSaved;
        private static Sprite s_rectMaskSprite;
        private void ApplyRefillMaskShape()
        {
            if (_refillMaskGO == null) return;
            var m = _refillMaskGO.GetComponent<SpriteMask>(); if (m == null) return;
            if (!_refillMaskRectSaved) { _refillMaskRectPos = _refillMaskGO.transform.localPosition; _refillMaskRectScale = _refillMaskGO.transform.localScale; s_rectMaskSprite = m.sprite; _refillMaskRectSaved = true; }
            if (!RulesEngine.HasVoids)
            {
                m.sprite = s_rectMaskSprite;
                _refillMaskGO.transform.localPosition = _refillMaskRectPos; _refillMaskGO.transform.localScale = _refillMaskRectScale;
                return;
            }
            var tex = new Texture2D(COLS, ROWS, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) tex.SetPixel(c, r, RulesEngine.IsVoid(c, r) ? Color.clear : Color.white);
            tex.Apply();
            // FullRect: the default Tight mesh is traced from the alpha, which is unreliable on a 9x11 texture (11-02: tiles vanished)
            m.sprite = Sprite.Create(tex, new Rect(0, 0, COLS, ROWS), new Vector2(0.5f, 0.5f), 1f, 0, SpriteMeshType.FullRect);   // 1 world unit per cell before scaling
            m.alphaCutoff = 0.5f;
            Vector3 lo = CellToWorld(0, 0), hi = CellToWorld(COLS - 1, ROWS - 1);
            var centre = new Vector3((lo.x + hi.x) * 0.5f, (lo.y + hi.y) * 0.5f, _refillMaskGO.transform.position.z);
            // CellToWorld is the REST layout; if the board is parked off-screen for its entry slide, shift by the parent's offset
            var par = _refillMaskGO.transform.parent;
            int pi = par != null ? _boardEntryTfs.IndexOf(par) : -1;
            if (pi >= 0 && pi < _boardEntryRests.Count) { var dlt = par.position - _boardEntryRests[pi]; centre.x += dlt.x; centre.y += dlt.y; }
            float pitchY = ROWS > 1 ? (hi.y - lo.y) / (ROWS - 1) : RowPitch;
            _refillMaskGO.transform.position = centre;
            var pls = _refillMaskGO.transform.parent != null ? _refillMaskGO.transform.parent.lossyScale : Vector3.one;
            _refillMaskGO.transform.localScale = new Vector3(CellSize / Mathf.Max(0.0001f, pls.x), pitchY / Mathf.Max(0.0001f, pls.y), 1f);
            WDLog.Log($"[Grid] fall clip = the shape's {COLS}x{ROWS} real cells (shaped board) - TEMP DIAG mask bounds {m.bounds.min:F2}..{m.bounds.max:F2} | cells {lo:F2}..{hi:F2} | parent lossy {pls:F3} pos {_refillMaskGO.transform.position:F2}");
        }

        private IEnumerator ClipWhileFalling(Tile tile)
        {
            if (tile == null) yield break;
            SetRefillClip(tile, true);
            yield return null;   // the fall starts this frame
            float guard = 0f;
            while (tile != null && tile.IsAnimating && guard < 3f) { guard += Time.deltaTime; yield return null; }
            if (tile != null) SetRefillClip(tile, false);
        }

        private static void SetRefillClip(Tile tile, bool on)
        {
            foreach (var sr in tile.GetComponentsInChildren<SpriteRenderer>(true))
                sr.maskInteraction = on ? SpriteMaskInteraction.VisibleInsideMask : SpriteMaskInteraction.None;
            foreach (var tmp in tile.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                var mat = tmp.fontMaterial;   // per-tile instance
                if (mat == null) continue;
                mat.SetFloat("_Stencil", on ? 1f : 0f);
                mat.SetFloat("_StencilComp", on ? (float)UnityEngine.Rendering.CompareFunction.LessEqual : (float)UnityEngine.Rendering.CompareFunction.Always);
                mat.SetFloat("_StencilReadMask", 255f);
            }
        }

        private IEnumerator RevealFromBoardTop(Tile tile)
        {
            if (tile == null) yield break;
            float h = RowPitch;
            float topY = CellToWorld(0, ROWS - 1).y + h * 0.5f;
            var srs  = tile.GetComponentsInChildren<SpriteRenderer>(true);
            var tmps = tile.GetComponentsInChildren<TMPro.TMP_Text>(true);
            var srA  = new float[srs.Length]; for (int i = 0; i < srs.Length; i++) srA[i] = srs[i].color.a;
            var tmA  = new float[tmps.Length]; for (int i = 0; i < tmps.Length; i++) tmA[i] = tmps[i].alpha;
            float guard = 0f;
            while (tile != null && guard < 3f)
            {
                float bottom = tile.transform.position.y - h * 0.5f;
                float a = Mathf.Clamp01((topY - bottom) / h);   // 0 until its bottom edge reaches the board top, 1 once fully inside
                for (int i = 0; i < srs.Length; i++) if (srs[i] != null) { var c = srs[i].color; c.a = srA[i] * a; srs[i].color = c; }
                for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) tmps[i].alpha = tmA[i] * a;
                if (a >= 1f) break;
                guard += Time.deltaTime;
                yield return null;
            }
            if (tile == null) yield break;
            for (int i = 0; i < srs.Length; i++) if (srs[i] != null) { var c = srs[i].color; c.a = srA[i]; srs[i].color = c; }
            for (int i = 0; i < tmps.Length; i++) if (tmps[i] != null) tmps[i].alpha = tmA[i];
        }

        public IEnumerator ApplyGravityFromEvents(Dictionary<Vector2Int, Vector2Int> tileMoves)
        {
            OwlsFallOutVisual();   // 11-24: owls the data sent home leave the grid before the moves are played
            if (tileMoves == null) tileMoves = new Dictionary<Vector2Int, Vector2Int>();
            if (tileMoves.Count == 0 && !SurvivalManager.FullBoardActive)
            {
//                 WDLog.Log("[GridManager] ApplyGravityFromEvents — no moves.");
                yield break;
            }

            // Per-column stagger — match ApplyGravity for consistent feel
            const float STAGGER_PER_COL = 0.045f;

            List<Tile> animatingTiles = new List<Tile>();

            // 11-17 (Spencer's axe recording, L19: after the chop, tiles from TWO columns away flew in straight diagonal lines THROUGH
            // other tiles to fill the cells under the crates). This path only knew each tile's start and end cell and tweened a
            // straight line; a normal move's fall (ApplyGravity) follows the plan's route in waves - down, one diagonal step round a
            // crate, down. Same thing here now: re-run the plan on the picture as it stands (read-only) and, ONLY if it agrees with
            // the data's moves cell for cell, give each tile its route. Any disagreement -> the old straight falls, nothing else changes.
            Dictionary<Vector2Int, RulesEngine.FallMove> routeOf = null; bool phased = false;
            if (FALL_ROUTES && RulesEngine.DIAGONAL_SLIDE && SurvivalManager.FullBoardActive && tileMoves.Count > 0)
            {
                var occ = new bool[COLS, ROWS]; var fix = new bool[COLS, ROWS];
                for (int col = 0; col < COLS; col++)
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (_tiles[col, row] == null || _cells[col, row] == null) continue;
                        var rc = RulesEngine.Instance != null ? RulesEngine.Instance.GetCell(col, row) : null;
                        occ[col, row] = true; fix[col, row] = rc != null ? RulesEngine.IsFixedCell(rc) : IsFixedTile(_tiles[col, row]);
                    }
                var plan = RulesEngine.PlanFall(occ, fix, RulesEngine.FallSpawnRows);
                routeOf = new Dictionary<Vector2Int, RulesEngine.FallMove>();
                var refR = new Dictionary<Vector2Int, List<Vector2Int>>(); var refP = new Dictionary<Vector2Int, List<int>>();
                bool agree = true;
                foreach (var m in plan)
                {
                    if (m.from.y >= ROWS) { refR[m.to] = m.path; refP[m.to] = m.phase; continue; }   // a new piece (SpawnRefillTiles)
                    if (tileMoves.TryGetValue(m.from, out var mto) && mto == m.to) routeOf[m.from] = m; else agree = false;
                }
                foreach (var kv in tileMoves) if (!routeOf.ContainsKey(kv.Key)) agree = false;
                if (!agree) { routeOf = null; WDLog.Log($"[Fall] booster fall: the picture's plan differs from the data's {tileMoves.Count} move(s) - straight falls"); }
                else
                {
                    foreach (var m in routeOf.Values) if (m.path != null) for (int k = 1; k < m.path.Count; k++) if (m.path[k].x != m.path[k - 1].x) phased = true;
                    foreach (var kv in refR) if (kv.Value != null) for (int k = 1; k < kv.Value.Count; k++) if (kv.Value[k].x != kv.Value[k - 1].x) phased = true;
                    if (phased)
                    {
                        _refillRoutes.Clear(); _refillPhases.Clear();
                        foreach (var kv in refR) { _refillRoutes[kv.Key] = kv.Value; _refillPhases[kv.Key] = refP[kv.Key]; }
                    }
                }
            }
            _waveFalls.Clear(); _wavesOn = phased;

            // 11-04 TWO-PHASE: lift every moving tile first, then place. With the diagonal slide a move's destination can be another
            // move's source in any order; one-at-a-time placing (fine for straight-down falls packed bottom-up) would collide.
            var lifted = new Dictionary<Vector2Int, (Tile tile, CellData cell)>();
            foreach (var kvp in tileMoves) lifted[kvp.Key] = (_tiles[kvp.Key.x, kvp.Key.y], _cells[kvp.Key.x, kvp.Key.y]);
            foreach (var kvp in tileMoves) if (lifted[kvp.Key].tile != null) { _tiles[kvp.Key.x, kvp.Key.y] = null; _cells[kvp.Key.x, kvp.Key.y] = null; }

            foreach (var kvp in tileMoves)
            {
                int fromCol = kvp.Key.x;
                int fromRow = kvp.Key.y;
                int toCol   = kvp.Value.x;
                int toRow   = kvp.Value.y;

                Tile tile = lifted[kvp.Key].tile;
                CellData cell = lifted[kvp.Key].cell;

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

                // (old position already cleared in the lift pass)

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
                    float fallDuration = dist / FallSpeed;
                    float colDelay = toCol * STAGGER_PER_COL;
                    if (phased && routeOf != null && routeOf.TryGetValue(kvp.Key, out var fm) && fm.path != null && fm.path.Count >= 2)
                        _waveFalls.Add((tile, RouteToWorld(fm.path, targetPos.z), fm.phase, fromCol * STAGGER_PER_COL));   // 11-17: its route, in waves
                    else tile.AnimateGravityFall(targetPos, fallDuration, colDelay);
                    if (GRAVITY_CLIP && REFILL_CLIP && RulesEngine.HasVoids) StartCoroutine(ClipWhileFalling(tile));   // 11-02: hidden while crossing a gap (GRAVITY_CLIP off: suspected in the row-of-specials bug)
                    animatingTiles.Add(tile);
                }
                else
                {
                    tile.transform.position = targetPos;
                }
            }

            SpawnRefillTiles(animatingTiles);   // full board: new letters fall in with the rest
            if (_wavesOn)
            {   // 11-17: routed booster falls. Between waves a tile rests on a cell that is not its own yet - tell the integrity
                // checker (a booster cascade is not "processing", so it kept checking and logged MISPLACED all the way down).
                _wavesOn = false; _routedFallSince = Time.unscaledTime;
                yield return StartCoroutine(PlayFallWaves());
                _routedFallSince = -1f;
            }

            if (animatingTiles.Count > 0)
            {
//                 WDLog.Log($"[GridManager] ApplyGravityFromEvents — {animatingTiles.Count} tile(s) falling.");

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
                    if (anyAnimating)
                    {
                        // Ride the fall. The outline traces the tiles' LIVE positions, so
                        // retracing each frame keeps the line on the letters all the way
                        // down instead of waiting at the destination for them to arrive.
                        // 2026-09-09 Spencer.
                        ClusterOutline.Instance?.Refresh();
                        yield return null;
                    }
                }
            }

//             WDLog.Log("[GridManager] ApplyGravityFromEvents — complete.");
        
            // Letters may have been pulled apart — drop any word that no longer exists
            // BEFORE retracing, so the outline never traces a word that just died.
            RulesEngine.Instance?.ValidatePrimedWords();
            ClusterOutline.Instance?.Refresh();   // tiles moved — retrace the cluster outlines

            // Gravity moves tiles and carries NO special state — measured: zero references to
            // IsFrozen/IsStone/IsAnchored/IsWild/IsSwapRefill in this whole method. The rules
            // data is fine (the cell OBJECTS move), but the visual layer is never told, so
            // frost/stone/vault/wild/refill overlays end up on the wrong tiles after any
            // detonation. Reconciling HERE rather than at the four call sites means every
            // caller is covered, including future ones. 2026-10-19.
            ReconcileSpecialVisuals(RulesEngine.Instance);
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
            // Heal anything the LAST rise left stranded before starting this one. The new-tile
            // pop is a 0.85s scale bounce but input unblocks at 0.18s, so a second rise (or a
            // gravity step) can kill that tween mid-bounce and leave the tile at the wrong
            // size — the purple gaps Spencer saw after a three-row rise. Doing it here, at the
            // START, heals the previous rise without cutting this one's pop short.
            // 2026-09-10 Spencer.
            SnapAllTilesToCells();

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
                        // Tell the tile its position is spoken for. 2026-10-20: this tween is
                        // driven from OUT HERE, so Tile.IsAnimating stayed FALSE for the whole
                        // rise — and one frame after a rise started, BoardInvariants logged every
                        // tile on the board as MISPLACED by 0.71 units (data already shifted up a
                        // row, tiles still travelling) and TELEPORTED each one to the top of its
                        // rise. The rise animation was being destroyed a frame after it began.
                        // Pad the claim past the OutBack overshoot's settle.
                        tile.MarkAnimating(dur * 1.35f);
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

                // Outlines ride the rise with the tiles instead of snapping after it.
                ClusterOutline.Instance?.RiseBy(CellSize, RISE_DURATION);

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
                        else          { tile.SetTreasureHits(rockCell != null ? rockCell.TreasureHits : 0); tile.SetCrateHP(rockCell != null ? rockCell.CrateHP : 0); tile.SetStoneVisual(true); }
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

                // ── POST-RISE LAG (2026-09-08 Spencer) ──────────────────────────────
                // "there's a bit of lag after a row rise to settle before the game is
                // responsive." This waited max(shift, pop) — and POP_DURATION is 0.85s
                // while the shift is 0.18s, so the game blocked input for ~0.90s after a
                // rise even though the tiles stopped MOVING after 0.18s. Roughly seven
                // tenths of a second of dead time with the board already settled.
                //
                // The pop is a scale bounce on the newly spawned tiles. It moves nothing
                // between cells, so a tap during it still resolves to the right cell — it
                // is safe to let it finish on its own while the player plays. Only the
                // MOVEMENT has to complete before input reopens, because that is what
                // world-position-to-cell lookups depend on.
                if (popTweens.Count > 0)
                    yield return new WaitForSeconds(RISE_DURATION + 0.05f);

            // Rows moved — the outline traces CELL coordinates, so it must be rebuilt or it
            // stays drawn where the words used to be. The rise path never calls
            // SyncToRulesState, so hooking it there was not enough. 2026-09-09 Spencer.
            ClusterOutline.Instance?.Refresh();
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
