using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P5 booster — "Mooncall". Untargeted finisher. Clears every tile that
    /// is part of a currently-primed word, sequentially logged. Highest-ceiling
    /// booster — only as strong as the priming the player set up.
    ///
    /// MVP simplification: this clears the primed-word tiles directly via
    /// GridManager.RemoveTiles, same pattern as Bramble Sweep. Doesn't trigger
    /// the FULL detonation chain (cascade FX, score crediting, fuse extension).
    /// That richer detonation behavior is v1.5 polish work — for MVP, it's
    /// mechanically a "mass clear of all primed cells."
    /// </summary>
    public class Mooncall : Booster
    {
        public override string Id               => "mooncall";
        public override string DisplayName      => "Mooncall";
        // L1 = primed cells only, L2 = primed + cardinal neighbors, L3 = primed + all 8 neighbors (extended blast)
        public override string ShortDescription
        {
            get
            {
                switch (Level)
                {
                    case 2:  return "Detonate every primed word + their neighbors.";
                    case 3:  return "Detonate every primed word + a wide blast radius.";
                    default: return "Detonate every primed word at once.";
                }
            }
        }
        public override bool NeedsTarget     => false;
        public override bool TriggersGravity => true;

        public override void Activate(System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            var registry = rules.PrimedRegistry;
            if (registry == null || registry.Count == 0)
            {
                Debug.Log($"[Mooncall L{Level}] No primed words — no-op (charge refund not implemented).");
                onComplete?.Invoke();
                return;
            }

            // Collect all unique cells across all primed words.
            var cellSet = new HashSet<Vector2Int>();
            for (int i = 0; i < registry.Count; i++)
            {
                var pw = registry.GetByIndex(i);
                if (pw == null || pw.Cells == null) continue;
                for (int j = 0; j < pw.Cells.Count; j++) cellSet.Add(pw.Cells[j]);
            }

            // L2/L3 expand the kill zone around each primed cell.
            if (Level >= 2)
            {
                var expansion = new HashSet<Vector2Int>();
                int[][] cardinals = { new[]{0,1}, new[]{0,-1}, new[]{-1,0}, new[]{1,0} };
                int[][] diagonals = { new[]{1,1}, new[]{-1,1}, new[]{1,-1}, new[]{-1,-1} };
                foreach (var cell in cellSet)
                {
                    foreach (var d in cardinals)
                    {
                        int c = cell.x + d[0], r = cell.y + d[1];
                        if (c >= 0 && c < RulesEngine.COLS && r >= 0 && r < RulesEngine.ROWS
                            && rules.GetCell(c, r) != null)
                            expansion.Add(new Vector2Int(c, r));
                    }
                    if (Level >= 3)
                    {
                        foreach (var d in diagonals)
                        {
                            int c = cell.x + d[0], r = cell.y + d[1];
                            if (c >= 0 && c < RulesEngine.COLS && r >= 0 && r < RulesEngine.ROWS
                                && rules.GetCell(c, r) != null)
                                expansion.Add(new Vector2Int(c, r));
                        }
                    }
                }
                foreach (var v in expansion) cellSet.Add(v);
            }

            var toRemove = new List<Vector2Int>(cellSet);
            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            Debug.Log($"[Mooncall L{Level}] Detonated {registry.Count} primed words, cleared {toRemove.Count} cells");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
