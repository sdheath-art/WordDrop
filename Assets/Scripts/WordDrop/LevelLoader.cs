using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Loads LevelData from Assets/Resources/Levels/level_{id}.json.
    ///
    /// Returns null and logs an error on missing file or parse failure. Callers must
    /// null-check. Validation is separate — use LevelValidator.Validate() after load.
    /// </summary>
    public static class LevelLoader
    {
        private const string LEVELS_PATH_PREFIX = "Levels/level_";

        public static LevelData Load(int levelId)
        {
            string resourcePath = LEVELS_PATH_PREFIX + levelId;
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);

            if (asset == null)
            {
                Debug.LogError($"[LevelLoader] Missing level resource: Assets/Resources/{resourcePath}.json");
                return null;
            }

            LevelData data;
            try
            {
                data = JsonUtility.FromJson<LevelData>(asset.text);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[LevelLoader] JSON parse failed for level {levelId}: {e.Message}");
                return null;
            }

            if (data == null)
            {
                Debug.LogError($"[LevelLoader] Deserialized LevelData was null for level {levelId}");
                return null;
            }

            return data;
        }
    }
}
