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

        /// <summary>
        /// Optional per-level tutorial prompts. Each entry fires at a specific gameplay
        /// event (see TutorialPrompt.on). Empty/null = no tutorial overlay. Phase 6 uses
        /// this for the first 3 onboarding levels; later levels typically leave it empty.
        /// </summary>
        public TutorialPrompt[] tutorialPrompts;

        /// <summary>
        /// Optional per-level visual cues that replace verbose text coaching.
        /// Null = no visual cues (most levels). Phase 6 uses this for onboarding.
        /// </summary>
        public VisualCues visualCues;

        /// <summary>
        /// Per-level HUD visibility gates. Hides UI elements on levels before
        /// their mechanic is introduced (e.g. Chain Meter shown from L26+ with
        /// Bonus Mode intro). Missing / all-false on a LevelData means "hide
        /// optional HUD" — score/target/moves/hearts are always visible.
        /// </summary>
        public HudFlags hudFlags;

        /// <summary>
        /// Number of rewrite (edit) charges granted for this level. Default 0 —
        /// the player tool is NOT exposed until a level explicitly opts in.
        /// L4 (tutorial intro) sets this to 3. Non-Level modes (Survival /
        /// Daily / Blitz) use their own charge sources and ignore this field.
        /// </summary>
        public int rewriteCharges;
    }

    /// <summary>
    /// HUD element visibility per level. All default false — each level
    /// explicitly opts in. Score, target, and move counter are always on
    /// (not gated here). See project_wordrop_level_authoring_doctrine.md
    /// for the canonical level→flag table.
    /// </summary>
    [Serializable]
    public class HudFlags
    {
        public bool showSwapCharges;
        public bool showEditCharges;
        public bool showChainMeter;
    }

    /// <summary>
    /// Single tutorial prompt keyed to an in-game event.
    ///
    /// Valid `on` values (canonical list also in LevelValidator):
    ///   "start"             — fires once when the level begins
    ///   "first_word"        — fires the first time the player scores a word
    ///   "first_detonation"  — fires the first time a primed word detonates
    ///   "first_chain"       — fires the first time a chain bonus lands
    ///
    /// Unknown `on` values are rejected by the validator at load time.
    /// </summary>
    [Serializable]
    public class TutorialPrompt
    {
        public string on;
        public string text;
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

    /// <summary>
    /// Per-level visual cues (Phase 6). Swaps verbose text coaching for the
    /// Royal-Match/Wordscapes "less text, more visual signal" pattern.
    /// </summary>
    [Serializable]
    public class VisualCues
    {
        /// <summary>Cells to breathe-pulse at low intensity on level start (L1 hint).</summary>
        public CellCoord[] subtleGlowCells;

        /// <summary>Seconds of player inactivity before the ghost-play demo fires.
        /// 0 or negative = no ghost demo. Fires at most once per level instance.</summary>
        public int ghostDemoIdleSeconds;

        /// <summary>The letter that falls in the ghost-play demo animation (single char A-Z).</summary>
        public string ghostDemoLetter;

        /// <summary>Column+row the ghost letter lands in.</summary>
        public CellCoord ghostDemoCell;

        /// <summary>Bump the priming pulse intensity for the tutorial.</summary>
        public bool ampedPrimingPulse;

        /// <summary>Cells to pulse prominently (louder than subtle) for spectacle setup (L3).</summary>
        public CellCoord[] prominentPulseCells;
    }

    [Serializable]
    public class CellCoord
    {
        public int x;
        public int y;
    }
}
