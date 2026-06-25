using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Bramble Sweep". Player taps a board cell; the entire
    /// row containing that tap is cleared (all non-empty tiles in that row).
    ///
    /// Single-axis variant for MVP (row only). The full design supports row OR
    /// column on tap orientation, but that adds aim UX complexity — defer column
    /// variant to v1.5 polish.
    /// </summary>
    public class BrambleSweep : Booster
    {
        public override string Id               => "bramble_sweep";
        // 2026-05-28 (Path A): renamed for player-facing UI. Column-axis variant
        // (clear column on vertical-orientation tap) still deferred — Comet
        // clears the tapped row for now. TODO: add direction picker in Commit 3+.
        public override string DisplayName      => "Comet";
        // L1 = 1 row, L2 = row + 1 adjacent (row±1), L3 = row + both adjacent (3 rows total)
        public override string ShortDescription
        {
            get
            {
                switch (Level)
                {
                    case 2:  return "Tap a row — clear it + 1 adjacent.";
                    case 3:  return "Tap a row — clear 3 rows in a sweep.";
                    default: return "Tap a row — vines lash it clean.";
                }
            }
        }
        public override bool NeedsTarget     => true;
        public override bool TriggersGravity => true;

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            int rowsBelow = (Level >= 3) ? 1 : 0;
            int rowsAbove = (Level >= 2) ? 1 : 0;
            int rMin = Mathf.Max(0, row - rowsBelow);
            int rMax = Mathf.Min(RulesEngine.ROWS - 1, row + rowsAbove);

            var toRemove = new List<Vector2Int>();
            for (int r = rMin; r <= rMax; r++)
            {
                for (int c = 0; c < RulesEngine.COLS; c++)
                {
                    if (rules.GetCell(c, r) != null)
                        toRemove.Add(new Vector2Int(c, r));
                }
            }

            StripObjectiveTiles(toRemove); // never smash level objectives. 2026-06-15 Spencer.
            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            Debug.Log($"[BrambleSweep L{Level}] Cleared rows {rMin}-{rMax}: {toRemove.Count} tiles");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
