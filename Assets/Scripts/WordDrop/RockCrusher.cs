using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Path A (2026-05-28) — "Stone Splitter" booster. Player taps a rock tile;
    /// that rock AND all 4-cardinal connected rocks are crushed (cluster destroy).
    /// Triggers gravity so adjacent letter tiles fall into the cleared gap.
    ///
    /// If the player taps a non-stone tile, the booster bails silently without
    /// consuming the charge — wait, charges are consumed in BoosterManager
    /// BEFORE ResolveWithTarget is called. To avoid wasted charges on misclicks,
    /// the HUD/aim layer should ideally pre-filter taps to stone tiles only.
    /// For MVP we accept the wasted-charge edge case (Royal Match's rocket
    /// has the same forgiveness gap and players adapt fast).
    ///
    /// File + Id are new (no legacy class to inherit from). Internal name
    /// matches BoosterManager.ID_ROCK_CRUSHER ("stone_splitter").
    /// </summary>
    public class RockCrusher : Booster
    {
        public override string Id               => "stone_splitter";
        public override string DisplayName      => "Stone Splitter";
        public override string ShortDescription
            => "Tap a rock — shatter it and all connected rocks.";
        public override bool NeedsTarget     => true;
        public override bool TriggersGravity => true;

        /// <summary>Only a stone tile is a valid target — taps elsewhere keep the
        /// booster armed instead of wasting the charge. 2026-06-03 Spencer.</summary>
        public override bool CanResolveAt(int col, int row)
        {
            var cell = RulesEngine.Instance?.GetCell(col, row);
            return cell != null && cell.IsStone;
        }

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            // Validate the target IS a stone tile. Non-stone taps bail without
            // crushing anything — the charge was already consumed by BoosterManager
            // before we got here, so this is the "user misclicked" case.
            var targetCell = rules.GetCell(col, row);
            if (targetCell == null || !targetCell.IsStone)
            {
                Debug.Log($"[StoneSplitter] Tap at ({col},{row}) not a stone — booster fizzled");
                GameAudio.Instance?.PlayUIClick();
                onComplete?.Invoke();
                return;
            }

            // BFS flood-fill — find all 4-cardinal-connected stones starting
            // from the target. Diagonals NOT included (matches Candy Crush /
            // Royal Match cluster expectations).
            var cluster = new List<Vector2Int>();
            var visited = new HashSet<Vector2Int>();
            var queue   = new Queue<Vector2Int>();
            queue.Enqueue(new Vector2Int(col, row));
            visited.Add(new Vector2Int(col, row));

            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { 1, -1, 0, 0 };

            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                var cell = rules.GetCell(p.x, p.y);
                if (cell == null || !cell.IsStone) continue;

                cluster.Add(p);

                for (int i = 0; i < 4; i++)
                {
                    int nc = p.x + dx[i];
                    int nr = p.y + dy[i];
                    if (nc < 0 || nc >= RulesEngine.COLS) continue;
                    if (nr < 0 || nr >= RulesEngine.ROWS) continue;
                    var neighbor = new Vector2Int(nc, nr);
                    if (visited.Contains(neighbor)) continue;
                    visited.Add(neighbor);

                    var neighborCell = rules.GetCell(nc, nr);
                    if (neighborCell != null && neighborCell.IsStone)
                        queue.Enqueue(neighbor);
                }
            }

            // Crush all stones in the cluster. Same data+visual sync pattern
            // as Bloomburst — ClearCell on rules layer, RemoveTiles on grid.
            for (int i = 0; i < cluster.Count; i++)
                rules.ClearCell(cluster[i].x, cluster[i].y);
            grid.RemoveTiles(cluster);

            Debug.Log($"[StoneSplitter] Crushed {cluster.Count} stone(s) connected to ({col},{row})");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
