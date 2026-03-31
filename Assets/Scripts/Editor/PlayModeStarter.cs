using UnityEditor;
using UnityEngine;

namespace GameHarness
{
    /// <summary>
    /// Simple Editor script called via -executeMethod to enter Play mode.
    /// The actual test logic lives in AutoPlayTest (runtime MonoBehaviour)
    /// which auto-starts via [RuntimeInitializeOnLoadMethod].
    /// </summary>
    public static class PlayModeStarter
    {
        public static void Run()
        {
            Debug.Log("[PlayModeStarter] Entering Play mode for automated test...");

            // Subscribe to know when play mode fully starts
            EditorApplication.playModeStateChanged += (state) =>
            {
                Debug.Log($"[PlayModeStarter] PlayModeStateChange: {state}");
            };

            // Enter play mode — this will trigger assembly reload,
            // after which [RuntimeInitializeOnLoadMethod] in AutoPlayTest fires
            EditorApplication.EnterPlaymode();
        }
    }
}
