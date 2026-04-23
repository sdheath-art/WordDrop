using System.Collections.Generic;

namespace WordDrop
{
    /// <summary>
    /// Sanity-checks LevelData before handing it to LevelController. Returns a plain
    /// (bool ok, string reason) tuple so callers can log or reject without throwing.
    /// </summary>
    public static class LevelValidator
    {
        public const int SUPPORTED_SCHEMA_VERSION = 1;

        // Canonical mechanic/hazard names. Must match the gates added in Phase 5.
        private static readonly HashSet<string> AllowedMechanics = new HashSet<string>
        {
            "gold", "wild", "stone", "bonus_mode", "meltdown", "drought_assist", "jam_hint"
        };

        private static readonly HashSet<string> AllowedHazards = new HashSet<string>
        {
            "rising_rows"
        };

        private static readonly HashSet<string> AllowedTileVariants = new HashSet<string>
        {
            "", "normal", "gold", "stone", "wild"
        };

        // Canonical tutorial prompt triggers. Must match events LevelController fires.
        private static readonly HashSet<string> AllowedTutorialTriggers = new HashSet<string>
        {
            "start", "first_word", "first_detonation", "first_chain", "first_rewrite"
        };

        public static (bool ok, string reason) Validate(LevelData data)
        {
            if (data == null)
                return (false, "LevelData is null");

            if (data.schemaVersion != SUPPORTED_SCHEMA_VERSION)
                return (false, $"Unsupported schemaVersion {data.schemaVersion} (expected {SUPPORTED_SCHEMA_VERSION})");

            if (data.levelId <= 0)
                return (false, $"levelId must be positive (got {data.levelId})");

            if (data.target <= 0)
                return (false, $"target must be > 0 (got {data.target})");

            if (data.moveBudget < 5 || data.moveBudget > 30)
                return (false, $"moveBudget must be in [5, 30] (got {data.moveBudget})");

            if (data.starThresholds == null || data.starThresholds.Length != 3)
                return (false, "starThresholds must be length 3");

            if (!(data.starThresholds[0] <= data.starThresholds[1] &&
                  data.starThresholds[1] <= data.starThresholds[2]))
                return (false, $"starThresholds must be monotonically increasing (got [{data.starThresholds[0]}, {data.starThresholds[1]}, {data.starThresholds[2]}])");

            if (data.starThresholds[0] > data.target)
                return (false, $"1-star threshold ({data.starThresholds[0]}) exceeds target ({data.target}) — level would be unwinnable with 1 star");

            if (data.allowedMechanics != null)
            {
                foreach (string m in data.allowedMechanics)
                {
                    if (!AllowedMechanics.Contains(m))
                        return (false, $"Unknown mechanic '{m}' in allowedMechanics");
                }
            }

            if (data.hazards != null)
            {
                foreach (string h in data.hazards)
                {
                    if (!AllowedHazards.Contains(h))
                        return (false, $"Unknown hazard '{h}' in hazards");
                }
            }

            if (data.startingBoard != null)
            {
                foreach (PlacedTile t in data.startingBoard)
                {
                    if (t == null)
                        return (false, "startingBoard contains a null entry");

                    if (t.x < 0 || t.x >= GridManager.COLS)
                        return (false, $"startingBoard tile x={t.x} out of range [0, {GridManager.COLS - 1}]");

                    // Use RulesEngine.LEVEL_ROWS (8 post-Path-B) — the constant
                    // row count for Level mode specifically. Validator runs BEFORE
                    // GameMode.CurrentMode is set to Level (all callers: Validate
                    // → set mode → TransitionTo), so the dynamic RulesEngine.ROWS
                    // property would still return 5 at this point. LEVEL_ROWS is
                    // a compile-time constant that always reflects Level's runtime
                    // row count regardless of when validation fires.
                    int effectiveRows = RulesEngine.LEVEL_ROWS;
                    if (t.y < 0 || t.y >= effectiveRows)
                        return (false, $"startingBoard tile y={t.y} out of range [0, {effectiveRows - 1}] (effective ROWS)");

                    if (string.IsNullOrEmpty(t.letter) || t.letter.Length != 1)
                        return (false, $"startingBoard tile at ({t.x},{t.y}) has invalid letter '{t.letter}' (must be single character)");

                    char c = char.ToUpperInvariant(t.letter[0]);
                    if (c < 'A' || c > 'Z')
                        return (false, $"startingBoard tile at ({t.x},{t.y}) letter '{t.letter}' is not A-Z");

                    string variant = t.variant ?? "";
                    if (!AllowedTileVariants.Contains(variant))
                        return (false, $"startingBoard tile at ({t.x},{t.y}) has unknown variant '{t.variant}'");
                }
            }

            if (data.bag != null && data.bag.letterOverrides != null)
            {
                foreach (LetterWeight w in data.bag.letterOverrides)
                {
                    if (w == null)
                        return (false, "bag.letterOverrides contains a null entry");

                    if (string.IsNullOrEmpty(w.letter) || w.letter.Length != 1)
                        return (false, $"bag.letterOverrides letter '{w.letter}' must be single character");

                    char c = char.ToUpperInvariant(w.letter[0]);
                    if (c < 'A' || c > 'Z')
                        return (false, $"bag.letterOverrides letter '{w.letter}' is not A-Z");

                    if (w.count < 0)
                        return (false, $"bag.letterOverrides letter '{w.letter}' has negative count {w.count}");
                }
            }

            if (data.scriptedInitialHand != null)
            {
                for (int i = 0; i < data.scriptedInitialHand.Length; i++)
                {
                    string s = data.scriptedInitialHand[i];
                    if (string.IsNullOrEmpty(s) || s.Length != 1)
                        return (false, $"scriptedInitialHand[{i}] '{s}' must be single character");
                    char c = char.ToUpperInvariant(s[0]);
                    if (c < 'A' || c > 'Z')
                        return (false, $"scriptedInitialHand[{i}] '{s}' is not A-Z");
                }
            }

            if (data.scriptedDrawQueue != null)
            {
                for (int i = 0; i < data.scriptedDrawQueue.Length; i++)
                {
                    string s = data.scriptedDrawQueue[i];
                    if (string.IsNullOrEmpty(s) || s.Length != 1)
                        return (false, $"scriptedDrawQueue[{i}] '{s}' must be single character");
                    char c = char.ToUpperInvariant(s[0]);
                    if (c < 'A' || c > 'Z')
                        return (false, $"scriptedDrawQueue[{i}] '{s}' is not A-Z");
                }
            }

            if (data.tutorialPrompts != null)
            {
                foreach (TutorialPrompt p in data.tutorialPrompts)
                {
                    if (p == null)
                        return (false, "tutorialPrompts contains a null entry");
                    if (string.IsNullOrEmpty(p.on))
                        return (false, "tutorialPrompts entry missing 'on' trigger");
                    if (!AllowedTutorialTriggers.Contains(p.on))
                        return (false, $"tutorialPrompts entry has unknown trigger '{p.on}' (valid: start, first_word, first_detonation, first_chain, first_rewrite)");
                    if (string.IsNullOrEmpty(p.text))
                        return (false, $"tutorialPrompts entry for '{p.on}' has empty text");
                }
            }

            if (data.visualCues != null)
            {
                var cue = data.visualCues;
                int cols = GridManager.COLS;
                int rows = RulesEngine.LEVEL_ROWS;
                if (cue.subtleGlowCells != null)
                    foreach (var c in cue.subtleGlowCells)
                        if (c == null || c.x < 0 || c.x >= cols || c.y < 0 || c.y >= rows)
                            return (false, $"visualCues.subtleGlowCells entry out of grid range ({cue.subtleGlowCells})");
                if (cue.prominentPulseCells != null)
                    foreach (var c in cue.prominentPulseCells)
                        if (c == null || c.x < 0 || c.x >= cols || c.y < 0 || c.y >= rows)
                            return (false, $"visualCues.prominentPulseCells entry out of grid range");
                if (cue.ghostDemoIdleSeconds > 0)
                {
                    if (cue.ghostDemoCell == null
                        || cue.ghostDemoCell.x < 0 || cue.ghostDemoCell.x >= cols
                        || cue.ghostDemoCell.y < 0 || cue.ghostDemoCell.y >= rows)
                        return (false, "visualCues.ghostDemoCell out of grid range");
                    if (string.IsNullOrEmpty(cue.ghostDemoLetter) || cue.ghostDemoLetter.Length != 1)
                        return (false, "visualCues.ghostDemoLetter must be a single A-Z character when ghostDemoIdleSeconds > 0");
                    char gdc = char.ToUpperInvariant(cue.ghostDemoLetter[0]);
                    if (gdc < 'A' || gdc > 'Z')
                        return (false, $"visualCues.ghostDemoLetter '{cue.ghostDemoLetter}' is not A-Z");
                }

                // Drop-arrow column: -1 = no arrow (default), otherwise must be a valid column.
                if (cue.dropArrowCol != -1
                    && (cue.dropArrowCol < 0 || cue.dropArrowCol >= cols))
                    return (false, $"visualCues.dropArrowCol must be -1 or in [0, {cols - 1}] (got {cue.dropArrowCol})");
            }

            return (true, null);
        }
    }
}
