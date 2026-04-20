using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// JSON schema for a single discrete level. Serialized via Unity JsonUtility.
    ///
    /// Loaded by LevelLoader from Assets/Resources/Levels/level_{id}.json.
    /// Validated by LevelValidator before being handed to LevelController (Phase 2+).
    ///
    /// Forward-compat: schemaVersion is checked by the validator so old JSONs can
    /// be rejected (or migrated) if the schema ever changes post-ship.
    ///
    /// Unity JsonUtility notes:
    ///   - Missing reference fields deserialize to default-constructed empty instances,
    ///     not null. Treat null/empty arrays as "absent" when interpreting.
    ///   - Fields must be public; properties are ignored.
    /// </summary>
    [Serializable]
    public class LevelData
    {
        public int schemaVersion = 1;

        public int levelId;
        public string displayName;

        public int target;
        public int moveBudget;

        /// <summary>Score thresholds for [1-star, 2-star, 3-star]. Must be monotonically increasing.</summary>
        public int[] starThresholds;

        /// <summary>
        /// Whitelist of mechanics allowed this level. "core" is implicit — not listed.
        /// Canonical values: "gold", "wild", "stone", "bonus_mode", "meltdown",
        /// "drought_assist", "jam_hint".
        /// </summary>
        public string[] allowedMechanics;

        /// <summary>Canonical values: "rising_rows".</summary>
        public string[] hazards;

        /// <summary>Optional starting-board pre-population. Null/empty = empty board.</summary>
        public PlacedTile[] startingBoard;

        /// <summary>Optional bag overrides. Null or empty letterOverrides = default TileBag distribution.</summary>
        public BagConfig bag;

        /// <summary>0 = non-deterministic random. Non-zero = seeded TileBag (for daily levels etc.).</summary>
        public int seed;

        /// <summary>Optional per-level tutorial prompts. Empty/null = no prompts. Wired in Phase 6.</summary>
        public string[] tutorialPrompts;
    }

    [Serializable]
    public class PlacedTile
    {
        public int x;
        public int y;
        /// <summary>Single A-Z character as a string. Validator enforces length==1.</summary>
        public string letter;
        /// <summary>"normal" | "gold" | "stone" | "wild". Empty/null treated as "normal".</summary>
        public string variant;
    }

    [Serializable]
    public class BagConfig
    {
        /// <summary>Delta from default TileBag distribution. Absent letters keep their default counts.</summary>
        public LetterWeight[] letterOverrides;
    }

    [Serializable]
    public class LetterWeight
    {
        /// <summary>Single A-Z character as a string. Validator enforces length==1.</summary>
        public string letter;
        public int count;
    }
}
