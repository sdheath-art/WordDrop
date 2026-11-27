using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P4: abstract base for all roguelite boosters. Subclasses implement
    /// one of two activation models:
    ///   1. Untargeted boosters — call Activate(onComplete). Examples: Wispwhirl
    ///      (shuffle a row), Mooncall (detonate all primed).
    ///   2. Targeted boosters — set NeedsTarget = true, then call
    ///      ResolveWithTarget(col, row, onComplete) after the player taps a cell.
    ///      Examples: Bloomburst (3x3 area), Lantern Trace (highlight word at tile).
    ///
    /// Boosters do NOT manage charges or refills — that lives in BoosterManager.
    /// They're pure effect implementations: "given a board, mutate it."
    /// </summary>
    public abstract class Booster
    {
        /// <summary>Unique identifier (used for analytics, lifetime-unlock keys).</summary>
        public abstract string Id { get; }

        /// <summary>Player-facing name (e.g., "Bloomburst").</summary>
        public abstract string DisplayName { get; }

        /// <summary>One-line UX description for choice modal / tooltips. Concrete
        /// boosters typically build this dynamically from Level so the card text
        /// reflects the tier ("3×3 area" vs "5×5 area").</summary>
        public abstract string ShortDescription { get; }

        /// <summary>MVP P5: Hades-style rarity tiers. 1 = Common, 2 = Rare, 3 = Epic.
        /// Each concrete booster scales its effect by Level. Choice modal weights
        /// draws inverted (Epic 3× weight) so players see strong stuff often.</summary>
        public int Level { get; set; } = 1;

        /// <summary>Roman numeral display for card chrome ("II", "III"). Empty for L1.</summary>
        public string TierLabel
        {
            get
            {
                switch (Level)
                {
                    case 2: return "II";
                    case 3: return "III";
                    default: return "";
                }
            }
        }

        /// <summary>If true, BoosterManager enters aim mode after Activate() —
        /// player taps a board cell and ResolveWithTarget runs. If false,
        /// Activate() resolves the booster immediately.</summary>
        public virtual bool NeedsTarget => false;

        /// <summary>If true, BoosterManager runs GridManager.ApplyGravity()
        /// after the booster resolves so floating tiles fall into the gap.
        /// Destructive boosters (Bloomburst, Motepluck, Bramble, Mooncall) → true.
        /// Non-destructive (Lantern, Starseed, Glade, Wispwhirl) → false.</summary>
        public virtual bool TriggersGravity => false;

        /// <summary>Untargeted resolution. Called by BoosterManager.TryActivate
        /// when NeedsTarget=false. Subclass mutates board state, runs FX,
        /// then calls onComplete (or null-checks it). May be async via coroutine.</summary>
        public virtual void Activate(System.Action onComplete)
        {
            onComplete?.Invoke();
        }

        /// <summary>Targeted resolution. Called by BoosterManager after the
        /// player taps cell (col, row) in aim mode.</summary>
        public virtual void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            onComplete?.Invoke();
        }

        /// <summary>Pre-consume validity check for targeted boosters. BoosterManager
        /// calls this in aim mode BEFORE spending a charge — if it returns false the
        /// tap is ignored and the booster stays armed (no charge wasted). Default true
        /// (any tap is valid). Override for boosters that only work on specific tiles
        /// (e.g. Stone Splitter → only a stone). 2026-06-03 Spencer.</summary>
        public virtual bool CanResolveAt(int col, int row) => true;

        /// <summary>Two-tap boosters (Free Swap, 2026-10-23): the first aim tap SELECTS, the second resolves the pair via
        /// TryResolvePair. A charge is spent only when TryResolvePair returns true.</summary>
        public virtual bool NeedsTwoTargets => false;
        public virtual bool TryResolvePair(Vector2Int a, Vector2Int b) => false;

        /// <summary>Remove LEVEL-OBJECTIVE tiles from a booster's cleared-cell list so boosters can
        /// NEVER blow up an objective (Spencer 2026-06-15: "boosters can't just blow up objectives").
        /// Protects the HeroWord escort drop-targets, frozen ICE tiles, and anchored VAULT chests —
        /// those must be resolved by their intended mechanic (escorted down / worded through / cracked
        /// by a word splash), not smashed. Call this on the to-remove list before ClearCell/RemoveTiles.</summary>
        /// <summary>MULTI-HIT CRATE (2026-11-03): a booster takes ONE layer off a multi-layer crate. Those cells are removed from the list
        /// (the booster must not clear them), their data loses a layer and the tile repaints to the next stage with the crate knock.</summary>
        protected static void PeelCrates(List<Vector2Int> cells)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || cells == null) return;
            cells.RemoveAll(c =>
            {
                if (!rules.PeelCrateLayer(c.x, c.y)) return false;
                var t = grid != null ? grid.GetTile(c.x, c.y) : null; var d = rules.GetCell(c.x, c.y);
                if (t != null && d != null) { WordDropFX.Instance?.PlayCrateDebris(t.transform.position, t.CrateKind); WordDropFX.Instance?.PlayPlankOff(t, d.CrateHP + 1); t.SetCrateHP(d.CrateHP); }
                return true;
            });
        }

        /// <summary>11-06 Spencer ("when you use the axe ... the crates dont even look like they took a hit"): PeelCrates played the
        /// plank/debris the instant the booster was TAPPED, before its swing - by the time the axe reached the crates nothing happened.
        /// This peels the DATA now (same rule) but returns the hits so the caller plays <see cref="KnockCrate"/> as the swing lands.</summary>
        protected static Dictionary<Vector2Int, Vector2Int> PeelCratesDeferred(List<Vector2Int> cells)
        {
            var hits = new Dictionary<Vector2Int, Vector2Int>();   // cell -> (hp before, hp after)
            var rules = RulesEngine.Instance;
            if (rules == null || cells == null) return hits;
            cells.RemoveAll(c =>
            {
                if (!rules.PeelCrateLayer(c.x, c.y)) return false;
                var d = rules.GetCell(c.x, c.y); int after = d != null ? d.CrateHP : 1;
                hits[c] = new Vector2Int(after + 1, after);
                return true;
            });
            return hits;
        }
        /// <summary>The word-hit knock (HandManager Knock): debris, the plank tumbling off, a punch, the damaged crate art, the crate sound.</summary>
        protected static void KnockCrate(Vector2Int c, Vector2Int hp, string sound = null)
        {
            var grid = GridManager.Instance; var t = grid != null ? grid.GetTile(c.x, c.y) : null; if (t == null) return;
            WordDropFX.Instance?.PlayCrateDebris(t.transform.position, t.CrateKind);
            WordDropFX.Instance?.PlayPlankOff(t, hp.x);
            if (!t.IsAnimating) t.transform.DOPunchScale(Vector3.one * 0.14f, 0.24f, 6, 0.6f);
            t.SetCrateHP(hp.y);
            if (sound != null && GameAudio.Instance != null && GameAudio.Instance.HasKit(sound)) GameAudio.Instance.PlayKit(sound); else GameAudio.Instance?.PlayCrateKnock();
        }

        /// <summary>11-07 (Spencer: a booster that hits a special should set it off): takes every special out of
        /// <paramref name="cells"/> so it isn't smashed, and queues it to FIRE once the booster's cascade settles
        /// (BoosterManager.FireQueuedSpecials). Call after StripObjectiveTiles. Returns the cells taken.</summary>
        protected static HashSet<Vector2Int> TakeSpecials(List<Vector2Int> cells)
        {
            var taken = new HashSet<Vector2Int>();
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null || cells == null) return taken;
            cells.RemoveAll(c =>
            {
                var d = rules.GetCell(c.x, c.y);
                if (d == null || d.SpecialKind == RulesEngine.SPECIAL_NONE || d.IsFrozen) return false;   // frozen: the ice takes the hit
                var t = grid.GetTile(c.x, c.y); if (t == null) return false;
                BoosterManager.QueueSpecialFire(t); taken.Add(c); return true;
            });
            return taken;
        }
        /// <summary>The struck look on a special the booster just hit (it fires after the cascade).</summary>
        protected static void NudgeSpecial(Tile t) { if (t != null && !t.IsAnimating) t.transform.DOPunchScale(Vector3.one * 0.16f, 0.24f, 6, 0.6f); }

        /// <summary>11-07: the shelf/safe cells in a booster's area. Read BEFORE StripObjectiveTiles drops them (they're anchored).</summary>
        protected static List<Vector2Int> ShelfCellsIn(List<Vector2Int> cells)
        {
            var rules = RulesEngine.Instance; var list = new List<Vector2Int>();
            if (rules == null || cells == null) return list;
            foreach (var c in cells) { var d = rules.GetCell(c.x, c.y); if (d != null && d.ShelfPart > 0) list.Add(c); }
            return list;
        }
        /// <summary>11-07: the booster lands on a shelf/safe - knocks ONE book (or lights one lamp) per piece, once per use
        /// (<paramref name="done"/> holds the ShelfIds already hit). An emptied shelf's tiles are removed here; gravity picks
        /// the cells up from BoosterManager's before/after snapshot like any other cleared cell.</summary>
        protected static void HitShelfAt(Vector2Int c, HashSet<int> done)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null) return;
            var d = rules.GetCell(c.x, c.y);
            if (d == null || d.ShelfPart <= 0 || done == null || !done.Add(d.ShelfId)) return;
            var cleared = new List<Vector2Int>();
            var hits = rules.BoosterShelfHits(new[] { c }, cleared);
            HandManager.Instance?.HitShelvesFromBooster(hits);   // FX read the shelf tile, so before it's removed
            if (cleared.Count > 0) grid.RemoveTiles(cleared);
        }

        protected static void StripObjectiveTiles(List<Vector2Int> cells)
        {
            var rules = RulesEngine.Instance;
            if (rules == null || cells == null) return;
            cells.RemoveAll(c =>
            {
                var cell = rules.GetCell(c.x, c.y);
                // 2026-10-29 Spencer: "boosters that should have cleared the ice ... eliminated everything but the ice". ICE is a goal you
                // BREAK, so boosters now hit it like explosions do (DoExplode destroys frozen tiles; the ice goal counts the frozen
                // cells left). Drop targets + anchored tiles are still protected.
                return cell != null && (cell.IsDropTarget || cell.IsAnchored);
            });
        }
    }
}
