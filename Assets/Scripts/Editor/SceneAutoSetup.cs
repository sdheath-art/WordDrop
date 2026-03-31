using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using System;
using System.Linq;

namespace GameHarness
{
    /// <summary>
    /// Automatically sets up the Unity scene so the generated game is immediately playable.
    /// Runs on domain reload (when Unity opens or recompiles).
    /// Finds SceneBootstrap in the project, creates a clean scene, attaches it, and saves.
    /// </summary>
    [InitializeOnLoad]
    public static class SceneAutoSetup
    {
        private const string SETUP_DONE_KEY = "GameHarness_SceneSetupDone";

        static SceneAutoSetup()
        {
            // Defer to avoid running during import
            EditorApplication.delayCall += TrySetupScene;
        }

        [MenuItem("Harness/Force Scene Setup")]
        public static void ForceSetup()
        {
            SessionState.EraseBool(SETUP_DONE_KEY);
            TrySetupScene();
        }

        private static void TrySetupScene()
        {
            // Never run during Play mode — domain reloads trigger [InitializeOnLoad] again
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return;

            // Only run once per session unless forced
            if (SessionState.GetBool(SETUP_DONE_KEY, false))
                return;

            SessionState.SetBool(SETUP_DONE_KEY, true);

            // Check if a SceneBootstrap already exists in the scene
            var existingBootstrap = FindBootstrapInScene();
            if (existingBootstrap != null)
            {
                Debug.Log("[GameHarness] SceneBootstrap already in scene. Skipping setup.");
                return;
            }

            // Find the SceneBootstrap MonoScript in the project
            Type bootstrapType = FindBootstrapType();
            if (bootstrapType == null)
            {
                Debug.LogWarning("[GameHarness] No SceneBootstrap class found in project. Scene setup skipped.");
                return;
            }

            Debug.Log("[GameHarness] Setting up scene with SceneBootstrap...");

            // Create a new empty scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // Create the bootstrap GameObject and attach the script
            var bootstrapGO = new GameObject("GameBootstrap");
            bootstrapGO.AddComponent(bootstrapType);

            // Mark scene dirty and save to Assets
            EditorSceneManager.MarkSceneDirty(scene);

            string scenePath = "Assets/Scenes/Game.unity";
            if (!AssetDatabase.IsValidFolder("Assets/Scenes"))
                AssetDatabase.CreateFolder("Assets", "Scenes");

            EditorSceneManager.SaveScene(scene, scenePath);

            // Set this as the default scene in Build Settings
            var buildScenes = EditorBuildSettings.scenes.ToList();
            bool alreadyInBuild = buildScenes.Any(s => s.path == scenePath);
            if (!alreadyInBuild)
            {
                buildScenes.Insert(0, new EditorBuildSettingsScene(scenePath, true));
                EditorBuildSettings.scenes = buildScenes.ToArray();
            }

            Debug.Log($"[GameHarness] Scene created and saved to {scenePath}");
            Debug.Log("[GameHarness] Hit Play to test the game!");
        }

        private static MonoBehaviour FindBootstrapInScene()
        {
            var rootObjects = SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var go in rootObjects)
            {
                // Check by component name since we don't know the exact type/namespace
                foreach (var comp in go.GetComponents<MonoBehaviour>())
                {
                    if (comp != null && comp.GetType().Name == "SceneBootstrap")
                        return comp;
                }
            }
            return null;
        }

        private static Type FindBootstrapType()
        {
            // Search all loaded assemblies for a class named SceneBootstrap
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == "SceneBootstrap" && typeof(MonoBehaviour).IsAssignableFrom(type))
                            return type;
                    }
                }
                catch
                {
                    // Skip assemblies that throw on GetTypes
                }
            }
            return null;
        }
    }
}
