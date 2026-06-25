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
            return !cell.IsDropTarget && !cell.IsFrozen && !cell.IsAnchored;
        }

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            var toRemove = new List<Vector2Int> { new Vector2Int(col, row) };
            StripObjectiveTiles(toRemove); // never smash a level objective
            if (toRemove.Count == 0)
            {
                Debug.Log($"[Mallet] Tap at ({col},{row}) is an objective tile — fizzled");
                GameAudio.Instance?.PlayUIClick();
                onComplete?.Invoke();
                return;
            }

            // Same data+visual sync pattern as Bloomburst / Stone Splitter.
            for (int i = 0; i < toRemove.Count; i++)
                rules.ClearCell(toRemove[i].x, toRemove[i].y);
            grid.RemoveTiles(toRemove);

            Debug.Log($"[Mallet] Smashed tile at ({col},{row})");
            GameAudio.Instance?.PlayUIClick();
            onComplete?.Invoke();
        }
    }
}
