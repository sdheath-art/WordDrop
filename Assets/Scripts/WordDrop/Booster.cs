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
    }
}
