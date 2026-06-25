using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// "Stone Splitter" booster. 2026-06-24 (Spencer): now an UNTARGETED, board-wide
    /// clear — tap the booster and EVERY rock on the board shatters at once (was a
    /// tap-a-rock connected-cluster destroy). Triggers gravity so letter tiles fall
    /// into the cleared gaps and the normal cascade resolves any words formed.
    ///
    /// Vault chests (anchored), HeroWord escort drop-targets, and frozen ICE are
    /// stones under the hood but are LEVEL OBJECTIVES — StripObjectiveTiles protects
    /// them so the splitter can't trivialize an objective.
    ///
    /// Internal Id matches BoosterManager.ID_ROCK_CRUSHER ("stone_splitter").
    /// </summary>
    public class RockCrusher : Booster
    {
        public override string Id               => "stone_splitter";
        public override string DisplayName      => "Stone Splitter";
        public override string ShortDescription
            => "Shatter every rock on the board.";
        // 2026-06-24 (Spencer): TARGETED activation even though the effect is board-wide — tapping the
        // slot only ARMS aim mode (no charge spent); the player taps the board to CONFIRM. This way an
        // accidental slot tap doesn't drain a charge (they can cancel aim instead), and we refuse to
        // spend when there are no rocks. The tapped cell is ignored — every rock shatters.
        public override bool NeedsTarget     => true;
        public override bool TriggersGravity => true;

        /// <summary>Any board tap CONFIRMS, but only if there's at least one crushable rock — otherwise
        /// the tap is ignored and the booster stays armed (no wasted charge).</summary>
        public override bool CanResolveAt(int col, int row)
        {
            return CollectCrushableRocks().Count > 0;
        }

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            // Tapped cell is ignored — clear EVERY crushable rock on the board. Same data+visual sync
            // pattern as the other destructive boosters (ClearCell on rules, RemoveTiles on grid).
            var toRemove = CollectCrushableRocks();
            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            Debug.Log($"[StoneSplitter] Shattered {toRemove.Count} rock(s) board-wide");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }

        /// <summary>All stone cells that may be crushed — excludes level objectives (vault chests,
        /// HeroWord drop-targets, frozen ICE) via StripObjectiveTiles.</summary>
        private static System.Collections.Generic.List<Vector2Int> CollectCrushableRocks()
        {
            var rules = RulesEngine.Instance;
            var list  = new System.Collections.Generic.List<Vector2Int>();
            if (rules == null) return list;
            for (int c = 0; c < RulesEngine.COLS; c++)
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    var cell = rules.GetCell(c, r);
                    if (cell != null && cell.IsStone)
                        list.Add(new Vector2Int(c, r));
                }
            StripObjectiveTiles(list); // never crush objectives — they resolve by their own mechanic
            return list;
        }
    }
}
