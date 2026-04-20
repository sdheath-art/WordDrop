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

                    if (t.y < 0 || t.y >= GridManager.MAX_ROWS)
                        return (false, $"startingBoard tile y={t.y} out of range [0, {GridManager.MAX_ROWS - 1}]");

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

            return (true, null);
        }
    }
}
