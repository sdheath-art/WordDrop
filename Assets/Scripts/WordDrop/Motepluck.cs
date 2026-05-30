using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Motepluck". Targeted single-tile removal. Player taps a
    /// tile; that tile vanishes. Lighter, more precise version of Bloomburst.
    ///
    /// Per Codex's design notes:
    /// - Strong precision tool (can be "always picked" — balanced by per-stage
    ///   charge limit, not per-use cost)
    /// - Primed words containing the popped tile get invalidated by removal
    /// </summary>
    public class Motepluck : Booster
    {
        public override string Id               => "motepluck";
        public override string DisplayName      => "Motepluck";
        // L1 = tap → 1 tile, L2 = + 1 adjacent cardinal neighbor, L3 = + all 4 cardinal neighbors
        public override string ShortDescription
        {
            get
            {
                switch (Level)
                {
                    case 2:  return "Tap a tile — vanish it + 1 neighbor.";
                    case 3:  return "Tap a tile — vanish it + all 4 neighbors.";
                    default: return "Tap a tile — vanish it cleanly.";
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

            var toRemove = new List<Vector2Int>();
            void TryAdd(int c, int r)
            {
                if (c < 0 || c >= RulesEngine.COLS) return;
                if (r < 0 || r >= RulesEngine.ROWS) return;
                if (rules.GetCell(c, r) == null) return;
                toRemove.Add(new Vector2Int(c, r));
            }

            TryAdd(col, row);
            if (Level >= 2)
            {
                // L2: add closest non-empty neighbor (deterministic ordering: up first)
                int[][] dirs = { new[]{0,1}, new[]{0,-1}, new[]{-1,0}, new[]{1,0} };
                for (int i = 0; i < dirs.Length; i++)
                {
                    int c = col + dirs[i][0], r = row + dirs[i][1];
                    if (c >= 0 && c < RulesEngine.COLS && r >= 0 && r < RulesEngine.ROWS
                        && rules.GetCell(c, r) != null)
                    {
                        toRemove.Add(new Vector2Int(c, r));
                        break;
                    }
                }
            }
            if (Level >= 3)
            {
                // L3: all 4 cardinal neighbors (skip duplicates already added)
                TryAdd(col, row + 1);
                TryAdd(col, row - 1);
                TryAdd(col - 1, row);
                TryAdd(col + 1, row);
            }

            // Dedupe
            var unique = new HashSet<Vector2Int>(toRemove);
            toRemove = new List<Vector2Int>(unique);

            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            Debug.Log($"[Motepluck L{Level}] Removed {toRemove.Count} tile(s) centered at ({col},{row})");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
