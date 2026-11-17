using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// "Axe" (2026-10-29 Spencer: "a booster that clears out an entire row from top to bottom"). Tap any tile; the axe chops
    /// its whole COLUMN, top to bottom. The column partner of the lightning row clear (Royal Match's Arrow + Cannon pair).
    /// Column only refills, so it changes that column's vertical words and leaves the rest of the board alone.
    /// </summary>
    public class AxeChop : Booster
    {
        public override string Id               => BoosterManager.ID_AXE;
        public override string DisplayName      => "Axe";
        public override string ShortDescription => "Tap a tile - chop its whole column.";
        public override bool NeedsTarget     => true;
        public override bool TriggersGravity => true;

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            var toRemove = new List<Vector2Int>();
            for (int r = 0; r < RulesEngine.ROWS; r++)
                if (rules.GetCell(col, r) != null) toRemove.Add(new Vector2Int(col, r));

            var shelves = new HashSet<Vector2Int>(ShelfCellsIn(toRemove)); var shelfDone = new HashSet<int>();   // 11-07: shelf/safe take a hit
            StripObjectiveTiles(toRemove); // never smash level objectives (same rule as the other boosters)
            var specials = TakeSpecials(toRemove);   // 11-07: specials in the column are SET OFF after the chop, not smashed
            var knocks = PeelCratesDeferred(toRemove);   // 11-06: crates take the hit as the axe reaches them (was: on tap)
            System.Action clear = () =>
            {
                for (int i = 0; i < toRemove.Count; i++)
                    rules.ClearCellCountingGoals(toRemove[i].x, toRemove[i].y);   // counts gems / collect letters
                grid.RemoveTiles(toRemove);
                WDLog.Log($"[Axe] Chopped column {col}: {toRemove.Count} tiles");
                onComplete?.Invoke();
            };
            if (BoosterFX.ON && BoosterFX.Instance != null)
            {
                var set = new HashSet<Vector2Int>(toRemove);
                BoosterFX.Instance.AxeColumn(col, r =>
                {
                    var c = new Vector2Int(col, r);
                    if (set.Contains(c)) BoosterFX.BurstTile(grid.GetTile(col, r));
                    else if (knocks.TryGetValue(c, out var hp)) KnockCrate(c, hp, "axe_hit");   // 11-07 pick: the axe's own wood hit (C3)
                    else if (shelves.Contains(c)) HitShelfAt(c, shelfDone);
                    else if (specials.Contains(c)) NudgeSpecial(grid.GetTile(col, r));
                }, clear);
                return;
            }
            GameAudio.Instance?.PlayUIClick();
            foreach (var kv in knocks) KnockCrate(kv.Key, kv.Value);
            foreach (var c in shelves) HitShelfAt(c, shelfDone);
            clear();
        }
    }
}
