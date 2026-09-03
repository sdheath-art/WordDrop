using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P4 first booster — "Bloomburst". Player taps a tile; the 3×3 area
    /// around it (centered on the tap cell) is cleared. Uses GridManager.RemoveTiles
    /// + RulesEngine.ClearCell to sync visual and data layers (same pattern as
    /// SurvivalManager.ApplyContinueRescue).
    ///
    /// Edge cases (per the booster design memory):
    /// - Out-of-bounds cells in the 3×3 footprint skipped silently
    /// - Empty cells skipped
    /// - Primed words on cleared tiles get invalidated by the tile removal
    /// - No gravity called — leaves a clean hole the rising rows will fill
    /// - FX deferred to v1.5 polish pass (just tile-pop reuse for MVP)
    /// </summary>
    public class Bloomburst : Booster
    {
        public override string Id               => "bloomburst";
        // 2026-05-28 (Path A): renamed for player-facing UI. Internal class +
        // Id stay "Bloomburst" / "bloomburst" so analytics + memory continuity
        // stays intact.
        public override string DisplayName      => "Bloom Bomb";
        // L1 = 3×3 (radius 1), L2 = 5×5 (radius 2), L3 = 7×7 (radius 3, full-width on 7-col board)
        public override string ShortDescription
        {
            get
            {
                int side = 2 * Level + 1;
                return $"Tap a tile — pop the {side}×{side} around it.";
            }
        }
        public override bool   NeedsTarget      => true;
        public override bool   TriggersGravity  => true;

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            int radius = Level;  // 1 → 3×3, 2 → 5×5, 3 → 7×7
            var toRemove = new List<Vector2Int>();
            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    int c = col + dx;
                    int r = row + dy;
                    if (c < 0 || c >= RulesEngine.COLS) continue;
                    if (r < 0 || r >= RulesEngine.ROWS) continue;
                    if (rules.GetCell(c, r) == null) continue;
                    toRemove.Add(new Vector2Int(c, r));
                }
            }

            // Never smash level objectives (escort drop-targets / ice / chests). 2026-06-15 Spencer.
            StripObjectiveTiles(toRemove);

            // Sync RulesEngine + GridManager (same pattern as ApplyContinueRescue).
            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            // BoosterDbg: full cleared-cell dump so we can verify the bomb hit
            // the cells the player expected for that aim tap.
            var coords = new System.Text.StringBuilder();
            for (int i = 0; i < toRemove.Count; i++)
            {
                if (i > 0) coords.Append(",");
                coords.Append($"({toRemove[i].x},{toRemove[i].y})");
            }
            Debug.Log($"[BoosterDbg] Bloomburst target=({col},{row}) radius={radius} " +
                      $"cleared={toRemove.Count}: {coords}");
            Debug.Log($"[Bloomburst] Cleared {toRemove.Count} tiles centered on ({col},{row})");
            GameAudio.Instance?.PlayUIClick();

            onComplete?.Invoke();
        }
    }
}
