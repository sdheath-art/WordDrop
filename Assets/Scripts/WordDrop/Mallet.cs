using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// "Mallet" booster (2026-06-24 Spencer) — player taps ONE tile and it's smashed.
    /// Targeted, single-cell destroy. TriggersGravity so tiles above fall into the gap
    /// and the normal cascade resolves any words formed (same clear→gravity→cascade path
    /// as the other destructive boosters — no bespoke detonation, no desync).
    ///
    /// Objective tiles (escort drop-targets, frozen ICE, anchored vault chests) are
    /// protected via StripObjectiveTiles + CanResolveAt — the mallet must not trivialize
    /// level objectives, same rule as Bloomburst / Stone Splitter.
    /// </summary>
    public class Mallet : Booster
    {
        public override string Id               => BoosterManager.ID_MALLET;
        public override string DisplayName      => "Mallet";
        public override string ShortDescription => "Tap any tile to smash it.";
        public override bool   NeedsTarget      => true;
        public override bool   TriggersGravity  => true;

        /// <summary>Any non-objective tile is a valid target — objective taps keep the
        /// booster armed instead of wasting the charge.</summary>
        public override bool CanResolveAt(int col, int row)
        {
            var cell = RulesEngine.Instance?.GetCell(col, row);
            if (cell == null) return false;
            bool shelf = cell.ShelfPart > 0 && !(cell.ShelfKind == 1 && cell.ShelfBooks <= 0);   // 11-07: a shelf/safe takes the hit (an opened safe is inert)
            return !cell.IsDropTarget && (!cell.IsAnchored || shelf);   // 10-29: the hammer can smash ICE too (was refused)
        }

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            var toRemove = new List<Vector2Int> { new Vector2Int(col, row) };
            var shelves = ShelfCellsIn(toRemove);   // 11-07: the bookshelf / safe takes the hit
            StripObjectiveTiles(toRemove); // never smash a level objective
            var specials = TakeSpecials(toRemove);   // 11-07: a special is SET OFF, not smashed (fires after the swing)
            int beforePeel = toRemove.Count;
            var knocks = PeelCratesDeferred(toRemove);   // 11-06: the crate takes the hit when the hammer lands (was: on tap)
            if (toRemove.Count == 0 && (beforePeel > 0 || shelves.Count > 0 || specials.Count > 0))
            {   // the hammer knocked a crate layer / a book / a lamp, or struck a special: swing, no tile to clear
                WDLog.Log($"[Mallet] hit at ({col},{row}): crate={beforePeel > 0} shelf={shelves.Count > 0} special={specials.Count > 0}");
                System.Action impact = () =>
                {
                    foreach (var kv in knocks) KnockCrate(kv.Key, kv.Value);
                    var done = new HashSet<int>(); foreach (var c in shelves) HitShelfAt(c, done);
                    foreach (var c in specials) NudgeSpecial(grid.GetTile(c.x, c.y));
                };
                if (BoosterFX.ON && BoosterFX.Instance != null)
                { var at = GridManager.Instance.CellToWorld(col, row) + new Vector3(GridManager.Instance.BoardSlideOffsetX, 0f, -0.5f); BoosterFX.Instance.Mallet(at, impact, () => onComplete?.Invoke()); }
                else { impact(); onComplete?.Invoke(); }
                return;
            }
            if (toRemove.Count == 0)
            {
                WDLog.Log($"[Mallet] Tap at ({col},{row}) is an objective tile — fizzled");
                GameAudio.Instance?.PlayUIClick();
                onComplete?.Invoke();
                return;
            }

            System.Action clear = () =>
            {
                // Same data+visual sync pattern as Bloomburst / Stone Splitter.
                for (int i = 0; i < toRemove.Count; i++)
                    rules.ClearCellCountingGoals(toRemove[i].x, toRemove[i].y);   // counts gems / collect letters (2026-10-29)
                grid.RemoveTiles(toRemove);
                WDLog.Log($"[Mallet] Smashed tile at ({col},{row})");
                onComplete?.Invoke();
            };
            // HAMMER STRIKE (2026-10-29, RK): wind-up arc, swing, hit-stop on a starburst; the tile bursts on impact.
            if (BoosterFX.ON && BoosterFX.Instance != null)
            {
                var tile = grid.GetTile(col, row);
                Vector3 at = grid.CellToWorld(col, row) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.5f);
                BoosterFX.Instance.Mallet(at, () => BoosterFX.BurstTile(tile), clear);
                return;
            }
            GameAudio.Instance?.PlayUIClick();
            clear();
        }
    }
}
