#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using TMPro;

namespace WordDrop.EditorTools
{
    /// <summary>
    /// One-click creator for the three "Level N" title material assets (Face / Rim / Shadow) used by StageClearModal.
    /// Duplicates the WendyOne SDF font material (so the atlas matches) and seeds each with the current tuned values.
    /// Once these exist in Resources, StageClearModal.TryAssignSharedMaterial assigns them, and Spencer can tune the
    /// look LIVE in the Inspector — it persists on exit Play, with no code change and no dump/bake loop. 2026-07-15.
    /// </summary>
    public static class CreateLevelTitleMaterials
    {
        [MenuItem("WordDrop/Create Level Title Materials")]
        public static void Create()
        {
            var font = Resources.Load<TMP_FontAsset>("WendyOne SDF");
            if (font == null || font.material == null)
            {
                Debug.LogError("[LevelTitleMats] 'WendyOne SDF' font (or its material) not found in Resources. Aborting.");
                return;
            }

            // Seed each with the values Spencer dumped so they start where we are (not at font defaults).
            MakeMat(font.material, "LevelFace SDF",   new Color(255f / 255f, 252f / 255f, 251f / 255f, 1f), -0.07f, 0f);
            MakeMat(font.material, "LevelRim SDF",    new Color(44f / 255f, 60f / 255f, 120f / 255f, 1f),    0.59f, 0f);
            MakeMat(font.material, "LevelShadow SDF", new Color(24f / 255f, 38f / 255f, 82f / 255f, 1f),     0.54f, 0.827f);
            MakeMat(font.material, "TapPrompt SDF",   Color.white,                                           0f,    0f); // crisp white to start

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[LevelTitleMats] Created LevelFace/LevelRim/LevelShadow SDF in Assets/Resources/. " +
                      "Select them in the Inspector to tune the look live — it persists, no code needed.");
        }

        private static void MakeMat(Material baseMat, string assetName, Color face, float dilate, float softness)
        {
            string path = "Assets/Resources/" + assetName + ".mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
            {
                Debug.Log($"[LevelTitleMats] {assetName} already exists — skipping (delete it first to re-seed).");
                return; // idempotent: re-running only creates the ones that don't exist yet
            }

            var m = new Material(baseMat) { name = assetName };
            if (m.HasProperty(ShaderUtilities.ID_FaceColor))        m.SetColor(ShaderUtilities.ID_FaceColor, face);
            if (m.HasProperty(ShaderUtilities.ID_FaceDilate))       m.SetFloat(ShaderUtilities.ID_FaceDilate, dilate);
            if (m.HasProperty(ShaderUtilities.ID_OutlineWidth))     m.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f);
            if (m.HasProperty(ShaderUtilities.ID_OutlineSoftness))  m.SetFloat(ShaderUtilities.ID_OutlineSoftness, softness);
            if (m.HasProperty("_UnderlayColor"))                    m.DisableKeyword("UNDERLAY_ON");

            AssetDatabase.CreateAsset(m, path);
        }
    }
}
#endif
