using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// MVP P3.5: gameplay-only seeded RNG for Survival mode. Daily Seeded
    /// Survival uses this so all players on the same UTC day get identical
    /// runs (bonus cells, stones, vowel injection, etc).
    ///
    /// When NOT seeded (default), falls through to UnityEngine.Random so
    /// regular Survival stays non-deterministic. When SetSeed is called,
    /// switches to System.Random for deterministic, cross-device output.
    ///
    /// Cosmetic randomness (audio variants, particle jitter, visual seeds)
    /// stays on UnityEngine.Random — sharing a deterministic stream with
    /// gameplay would let frame-rate variance drift the seed.
    ///
    /// API mirrors UnityEngine.Random for drop-in replacement at call sites:
    /// Range(int,int), Range(float,float), Value.
    /// </summary>
    public static class SurvivalRng
    {
        private static System.Random _rng;
        private static int _seed;
        private static int _callCount;

        /// <summary>True if a deterministic seed has been set. False = passthrough mode.</summary>
        public static bool IsSeeded => _rng != null;

        /// <summary>Current seed (0 if not seeded).</summary>
        public static int CurrentSeed => _seed;

        /// <summary>Calls since the most recent SetSeed or Reset.</summary>
        public static int CallCount => _callCount;

        /// <summary>Activate deterministic mode with the given seed. All subsequent
        /// Range/Value calls produce identical sequences across devices.</summary>
        public static void SetSeed(int seed)
        {
            _rng = new System.Random(seed);
            _seed = seed;
            _callCount = 0;
            Debug.Log($"[SurvivalRng] Seeded with {seed} — gameplay random is now deterministic.");
        }

        /// <summary>Return to passthrough mode (UnityEngine.Random).</summary>
        public static void Reset()
        {
            if (_rng != null)
                Debug.Log($"[SurvivalRng] Reset after {_callCount} calls (was seeded with {_seed}).");
            _rng = null;
            _seed = 0;
            _callCount = 0;
        }

        /// <summary>Inclusive min, exclusive max. Matches UnityEngine.Random.Range(int,int).</summary>
        public static int Range(int minInclusive, int maxExclusive)
        {
            _callCount++;
            return _rng != null
                ? _rng.Next(minInclusive, maxExclusive)
                : UnityEngine.Random.Range(minInclusive, maxExclusive);
        }

        /// <summary>Inclusive min, inclusive max. Matches UnityEngine.Random.Range(float,float).</summary>
        public static float Range(float minInclusive, float maxInclusive)
        {
            _callCount++;
            if (_rng == null) return UnityEngine.Random.Range(minInclusive, maxInclusive);
            return minInclusive + (float)(_rng.NextDouble() * (maxInclusive - minInclusive));
        }

        /// <summary>[0..1). Matches UnityEngine.Random.value.</summary>
        public static float Value
        {
            get
            {
                _callCount++;
                return _rng != null ? (float)_rng.NextDouble() : UnityEngine.Random.value;
            }
        }
    }
}
