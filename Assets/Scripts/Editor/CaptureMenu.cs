using UnityEditor;
using UnityEngine;

/// <summary>Menu triggers for the CaptureRig burst-photo tool.
///
/// A menu item rather than only a hotkey because F9/F10 are macOS system keys and get eaten
/// before the game ever sees them, and because the Game view has to be focused for key input
/// to arrive at all. The menu works regardless. 2026-09-10.</summary>
public static class CaptureMenu
{
    [MenuItem("WordDrop/Capture/Burst Now %#9")]
    public static void BurstNow()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("[Capture] enter Play mode first — there is nothing to photograph.");
            return;
        }
        WordDrop.CaptureRig.Capture("manual");
    }

    [MenuItem("WordDrop/Capture/Capture Next Level Entry")]
    public static void ArmLevelEntry()
    {
        WordDrop.CaptureRig.CaptureNextLevelEntry = true;
        Debug.Log("[Capture] ARMED — the next level entry will be captured.");
    }

    // ── Replay (records BACKWARDS — see ReplayBuffer) ──────────────────────────

    [MenuItem("WordDrop/Capture/Start Recording (rolling)")]
    public static void StartRec()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Replay] enter Play mode first."); return; }
        WordDrop.ReplayBuffer.StartRecording();
    }

    [MenuItem("WordDrop/Capture/Stop Recording")]
    public static void StopRec() { WordDrop.ReplayBuffer.StopRecording(); }

    /// <summary>The point of the whole thing: press this AFTER you saw the glitch.</summary>
    [MenuItem("WordDrop/Capture/Save Replay (last few seconds) %#8")]
    public static void SaveReplay()
    {
        if (!Application.isPlaying) { Debug.LogWarning("[Replay] enter Play mode first."); return; }
        WordDrop.ReplayBuffer.Save("replay");
    }

    [MenuItem("WordDrop/Capture/Open Captures Folder")]
    public static void OpenFolder()
    {
        string root = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(Application.dataPath, "..", "captures"));
        System.IO.Directory.CreateDirectory(root);
        EditorUtility.RevealInFinder(root + "/");
    }
}
