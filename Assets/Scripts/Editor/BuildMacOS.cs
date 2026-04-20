using UnityEditor;
using UnityEngine;

public class BuildMacOS
{
    [MenuItem("Build/Build macOS")]
    public static void Build()
    {
        string[] scenes = new string[] { "Assets/Scenes/Game.unity" };
        string buildPath = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Application.dataPath),
            "Builds/WordDrop.app"
        );

        // Ensure build directory exists
        string buildDir = System.IO.Path.GetDirectoryName(buildPath);
        if (!System.IO.Directory.Exists(buildDir))
            System.IO.Directory.CreateDirectory(buildDir);

        BuildPipeline.BuildPlayer(scenes, buildPath, BuildTarget.StandaloneOSX, BuildOptions.Development);
        Debug.Log("Build complete: " + buildPath);
    }
}
