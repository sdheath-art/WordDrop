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
