using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Run-scoped buffs — the "engine" the swap game was missing.
    ///
    /// WHY (2026-09-09 Spencer). Balatro's targets can grow 2.5x per ante because the
    /// PLAYER's engine grows too — jokers multiply. WordDrop had no engine: scoring power
    /// was FLAT for a whole run while the board tightened, so every run played the same and
    /// stages could only get slower. These are the compounding half.
    ///
    /// Deliberately NOT built on Booster. A Booster is an activatable consumable
    /// (Activate / NeedsTarget / ResolveWithTarget); a buff is a passive modifier that
    /// changes the maths for the rest of the run. Different things, so different type.
    ///
    /// Every value here is a DELTA on an existing tuned constant, never a replacement — so
    /// with no buffs granted the game behaves exactly as before and this file is inert.
    /// </summary>
    public static class RunBuffs
    {
        public enum Id
        {
            RichVeins,      // gold letters appear far more often
            DeepChain,      // chain multiplier keeps scaling one step further
            BiggerBlast,    // detonations pay more
            ShortFuse,      // flat bonus per detonated word up
            SlowTide,       // rows rise less often
            GoldenTouch,    // gold words pay more
        }

        // ── granted counts (buffs stack; taking one twice doubles its delta) ──
        private static readonly int[] _count = new int[6];

        public static void ResetRun() { for (int i = 0; i < _count.Length; i++) _count[i] = 0; }
        public static int  CountOf(Id id) => _count[(int)id];
        public static void Grant(Id id)
        {
            _count[(int)id]++;
            WDLog.Log($"[RunBuffs] granted {id} (now x{_count[(int)id]})");
        }

        public static string Title(Id id) => id switch
        {
            Id.RichVeins   => "RICH VEINS",
            Id.DeepChain   => "DEEP CHAIN",
            Id.BiggerBlast => "BIGGER BLAST",
            Id.ShortFuse   => "SHORT FUSE",
            Id.SlowTide    => "SLOW TIDE",
            _              => "GOLDEN TOUCH",
        };

        public static string Describe(Id id) => id switch
        {
            Id.RichVeins   => "High-value letters appear far more often",
            Id.DeepChain   => "Big webs keep multiplying one step further",
            Id.BiggerBlast => "Detonations pay more",
            Id.ShortFuse   => "Every word you blow up is worth more",
            Id.SlowTide    => "The board rises less often",
            _              => "Gold words pay double again",
        };

        // ── the modifiers, read at the point of use ──────────────────────────────────

        /// <summary>Chance a rising row carries a high-value letter. Base 0.35.</summary>
        public static float HighValueChance(float base_) =>
            Mathf.Clamp01(base_ + 0.22f * _count[(int)Id.RichVeins]);

        /// <summary>How deep the triangular chain multiplier keeps scaling. Base 3.</summary>
        public static int ChainDepthCap(int base_) => base_ + _count[(int)Id.DeepChain];

        /// <summary>Multiplier on a primed word's score when it detonates. Base 1.5.</summary>
        public static float DetonationMultiplier(float base_) =>
            base_ + 0.5f * _count[(int)Id.BiggerBlast];

        /// <summary>Flat bonus per detonated word. Base 10.</summary>
        public static int BreakerBonus(int base_) => base_ + 15 * _count[(int)Id.ShortFuse];

        /// <summary>Extra moves between rises. Base 0.</summary>
        public static int RiseMoveBonus() => _count[(int)Id.SlowTide];

        /// <summary>Multiplier applied to a gold word. Base 2.</summary>
        public static float GoldMultiplier(float base_) =>
            base_ + 1f * _count[(int)Id.GoldenTouch];

        /// <summary>One-line summary for the HUD / debug.</summary>
        public static string Summary()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _count.Length; i++)
                if (_count[i] > 0) sb.Append($"{Title((Id)i)}x{_count[i]}  ");
            return sb.Length > 0 ? sb.ToString().TrimEnd() : "(none)";
        }
    }
}
