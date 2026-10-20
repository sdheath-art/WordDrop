using UnityEngine;
using System.Collections.Generic;

namespace WordDrop
{
    /// <summary>Continuously checks the things that keep going wrong, and LOGS when one breaks.
    ///
    /// Written 2026-09-10 after a day of "fixed" changes that were not. Spencer asked how he
    /// could get me to verify my own work: this is the answer. Every bug chased today was an
    /// invariant being violated — data and display disagreeing — and each is checkable in code.
    /// The game now catches them itself, at the moment they happen, with the cell and the values.
    ///
    /// Runs a few times a second, only when the board is settled (mid-animation states are
    /// legitimately inconsistent). Each distinct violation logs ONCE until it clears, so a
    /// persistent fault does not flood the log.</summary>
    public class BoardInvariants : MonoBehaviour
    {
        public static bool Enabled = true;
        private const float CHECK_INTERVAL = 0.35f;

        private float _next;
        private readonly HashSet<string> _reported = new HashSet<string>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Enabled) return;
            var go = new GameObject("BoardInvariants");
            go.AddComponent<BoardInvariants>();
            DontDestroyOnLoad(go);
        }

        private void Update()
        {
            if (!Enabled || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + CHECK_INTERVAL;

            var grid  = GridManager.Instance;
            var rules = RulesEngine.Instance;
            if (grid == null || rules == null) return;
            // GOLD ART is checked ALWAYS — including mid-animation. A gold-primed tile must
            // never wear rose art at ANY moment, and the scored flash is precisely the window
            // Spencer reports it in. Gating this behind "board settled" made the checker blind
            // to the only time the fault appears. 2026-09-10 Spencer.
            CheckGoldArtAlways(grid, rules);

            // Everything else is only meaningful once the board has stopped moving.
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;
            if (grid.BoardEntryInProgress) return;

            // HEARTBEAT: zero [Invariant] lines is ambiguous — it could mean "no faults" or
            // "this never ran". Say so once so the next log is readable. 2026-10-19.
            if (!_saidAlive)
            {
                _saidAlive = true;
                WDLog.Log($"[Invariant] integrity checker ALIVE (board settled, " +
                          $"every {CHECK_INTERVAL:0.00}s) — silence from here means NO faults found.");
            }

            CheckBoardIntegrity(grid, rules);

            // The only check that can see a tile missing from _tiles[] entirely — every other
            // check reads that array, so a true scene orphan is invisible to them. 2026-10-19.
            grid.ReportSceneOrphanTiles();

            var live = new HashSet<string>();

            for (int c = 0; c < RulesEngine.COLS; c++)
            for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                var t = grid.GetTile(c, r);
                if (t == null || t.IsAnimating) continue;

                bool primedInData = rules.GetPrimedWordsContainingPublic(new Vector2Int(c, r)) > 0;

                // 1. PRIMED STATE: does the tile agree with the registry?
                if (primedInData != t.HasPrimedGlow)
                    Flag(live, $"prime|{c},{r}",
                        $"({c},{r}) '{t.Letter}' registry says primed={primedInData} but tile says {t.HasPrimedGlow}");

                // 2. SORTING ORDER: a settled tile must sit in its own row band.
                // Row 0 is the row being pushed in by a rise; it is deliberately parked at a
                // low order so it slides in from behind. Not a fault. 2026-09-10.
                if (r > 0 && t.SortingOrderNow != t.BaseSortingOrder)
                    Flag(live, $"sort|{c},{r}",
                        $"({c},{r}) '{t.Letter}' sortingOrder={t.SortingOrderNow} expected {t.BaseSortingOrder} — a lift outlived its effect");

                // 3. GOLD FACE vs GOLD STATE. The registry and the colour can both say GOLD
                //    while the tile is still wearing the ROSE armed art — gold tint over rose
                //    art is the peach/orange Spencer kept reporting. This is the check that
                //    names it. 2026-09-10.
                if (primedInData && t.IsGoldPrimedState && !t.ShowingGoldPrimedArt)
                    Flag(live, $"goldface|{c},{r}",
                        $"({c},{r}) '{t.Letter}' is GOLD-primed but is showing the ROSE armed art");

                // 4. LETTER COLOUR: white glyphs belong to primed tiles only.
                if (!primedInData && t.LetterIsWhitened)
                    Flag(live, $"letter|{c},{r}",
                        $"({c},{r}) '{t.Letter}' letter still WHITE but the tile is not primed");
            }

            // 4. PRIMED WORDS must be a straight unbroken run.
            foreach (var pw in rules.DebugAllPrimedWords())
            {
                if (pw.Cells == null || pw.Cells.Count < 2) continue;
                bool sameCol = true, sameRow = true;
                for (int i = 1; i < pw.Cells.Count; i++)
                {
                    if (pw.Cells[i].x != pw.Cells[0].x) sameCol = false;
                    if (pw.Cells[i].y != pw.Cells[0].y) sameRow = false;
                }
                bool ok = sameCol || sameRow;
                for (int i = 1; ok && i < pw.Cells.Count; i++)
                {
                    int step = sameCol ? Mathf.Abs(pw.Cells[i].y - pw.Cells[i - 1].y)
                                       : Mathf.Abs(pw.Cells[i].x - pw.Cells[i - 1].x);
                    if (step != 1) ok = false;
                }
                if (!ok) Flag(live, $"run|{pw.Id}", $"primed word '{pw.Word}' is no longer a straight unbroken run");
            }

            _reported.RemoveWhere(k => !live.Contains(k));   // cleared faults can report again
        }

        private readonly HashSet<string> _goldSeen = new HashSet<string>();


        /// <summary>Three structural faults the board must never show once it has settled.
        /// Added 2026-10-19 after a tile appeared FLOATING mid-board with letters blended
        /// together, and four separate diagnostics (double-Initialise, double pool-return,
        /// flag mismatch, gravity collision) all came back with ZERO hits — meaning the fault
        /// was structural and nothing was looking for it. This checker already existed but
        /// only inspected gold art.
        ///
        /// Reports once per fault per cell per run, so a persistent fault does not spam.</summary>
        private readonly HashSet<string> _integrityReported = new HashSet<string>();
        private bool _saidAlive;

        private void CheckBoardIntegrity(GridManager grid, RulesEngine rules)
        {
            for (int c = 0; c < RulesEngine.COLS; c++)
            for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                var cell = rules.GetCell(c, r);
                var tile = grid.GetTile(c, r);

                // 1. FLOATING DATA — a cell with empty space beneath it. Gravity should make
                //    this impossible; if it happens the rules board itself is wrong.
                if (cell != null && r > 0 && rules.GetCell(c, r - 1) == null
                    && !cell.IsAnchored)                      // vaults legitimately float
                    ReportIntegrity($"FLOATING ({c},{r}) '{cell.Letter}' — nothing beneath it");

                // 2. ORPHAN VISUAL — a tile on screen with no data behind it. SyncToRulesState
                //    cleans these, so one surviving means it is not in _tiles[] and nothing can
                //    see it.
                if (cell == null && tile != null)
                    ReportIntegrity($"ORPHAN VISUAL ({c},{r}) '{tile.Letter}' — tile with no data cell");

                // 3. INVISIBLE DATA — data with no tile. The player cannot see or play it.
                if (cell != null && tile == null && (cell.Letter != '\0' || cell.IsWild))
                    ReportIntegrity($"INVISIBLE DATA ({c},{r}) '{cell.Letter}' — data with no tile");

                // 4. MISPLACED TILE — the tile exists, the data exists, everything is wired
                //    correctly, and it is simply DRAWN somewhere else. 2026-10-19: the recurring
                //    "floating tile with blended letters" passes all three checks above because
                //    all three ask about BOOKKEEPING. This asks about PIXELS. A tile sitting a
                //    long way from its own cell looks like it is floating in mid-board, and two
                //    tiles landing on the same spot is exactly what reads as one tile with
                //    several letters stacked in it.
                if (cell != null && tile != null && !tile.IsAnimating && grid != null)
                {
                    // CellToWorld is the board's RESTING layout. While an entry/exit slide is
                    // running the whole board is displaced, so the rest position is NOT where the
                    // tile should be drawn — add the live offset or every tile reads as misplaced.
                    // 2026-10-20: the entry slide was already excluded by the BoardEntryInProgress
                    // guard above, but the EXIT slide deliberately leaves that flag false (see
                    // GridManager.PrepBoardExit: "Leave _boardEntryPrepped FALSE"). So on every
                    // stage clear all 22 tiles reported MISPLACED by 6.30 then 11.99 world units,
                    // and the repair below teleported each one back onto a board that was busy
                    // leaving the screen. Adding the offset fixes it WITHOUT blinding the checker
                    // during the slide, which a flag-gate would have done.
                    Vector3 want = grid.CellToWorld(c, r) + new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
                    Vector3 have = tile.transform.position;
                    float   off  = Vector2.Distance(new Vector2(want.x, want.y),
                                                    new Vector2(have.x, have.y));
                    if (off > grid.CellSize * 0.5f)
                    {
                        ReportIntegrity($"MISPLACED ({c},{r}) '{tile.Letter}' — drawn {off:F2} world units " +
                                        $"({off / Mathf.Max(0.01f, grid.CellSize):F1} cells) from its own cell. " +
                                        $"want {want} have {have}");

                        // ── AND REPAIR IT. 2026-10-19 ────────────────────────────────────
                        // Five separate sources have produced a misplaced tile now: pooled
                        // tiles left at the origin, risen tiles holding their old row, the
                        // Wispwhirl shuffle, a grid rebuild, and now the board-entry slide.
                        // Each was fixed where it occurred, and a sixth turned up each time.
                        //
                        // GridManager.ReconcileSpecialVisuals only runs after gravity, rises
                        // and shuffles — at LEVEL ENTRY none of those have happened, so
                        // [PosSync] reported 0 fixes while the whole board sat a row low.
                        //
                        // This checker already detects it correctly every single time, runs
                        // every 0.35s, and only ever moves a tile to where ITS OWN DATA says
                        // it belongs. Repairing here bounds the damage to a third of a second
                        // whatever the cause. The ERROR above still names the cell, so the
                        // real sources stay findable — this is a floor, not a cure.
                        tile.transform.position = want;
                    }
                }
            }
        }

        private void ReportIntegrity(string msg)
        {
            if (!_integrityReported.Add(msg)) return;   // once per distinct fault per run
            Debug.LogError($"[Invariant] {msg}");
        }

        private void CheckGoldArtAlways(GridManager grid, RulesEngine rules)
        {
            for (int c = 0; c < RulesEngine.COLS; c++)
            for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                var t = grid.GetTile(c, r);
                if (t == null || !t.HasPrimedGlow || !t.IsGoldPrimedState) continue;
                if (t.ShowingGoldPrimedArt) { _goldSeen.Remove($"{c},{r}"); continue; }
                if (_goldSeen.Add($"{c},{r}"))
                    Debug.LogWarning($"[INVARIANT] GOLD-ART t={Time.unscaledTime:F2} ({c},{r}) '{t.Letter}' " +
                                     $"gold-primed but wearing '{t.ArmedArtName}' (colour={t.SortingOrderNow})");
            }
        }

        private void Flag(HashSet<string> live, string key, string msg)
        {
            live.Add(key);
            if (_reported.Add(key))
                Debug.LogWarning($"[INVARIANT] {msg}");
        }
    }
}
