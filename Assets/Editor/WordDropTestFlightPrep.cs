using UnityEditor;
using UnityEngine;

/// <summary>
/// 2026-11-19: one menu item that sets everything the TestFlight upload needs, so nothing is typed by hand each time:
///  - signing: Team ID + "Automatically Sign" (both were empty in Player Settings, so Build And Run stopped in Xcode)
///  - a NEW build number (App Store Connect refuses a number it has already seen)
///  - Development Build OFF
/// Menu: WordDrop > Build > Prepare TestFlight build. It only changes settings - it does not build or upload.
/// The playtest define (WORDROP_PLAYTEST, 9999 coins) is left as it is: ON is right for testers, OFF for the store.
/// </summary>
public static class WordDropTestFlightPrep
{
    private const string TEAM_ID = "8D8KVM4WDZ";

    [MenuItem("WordDrop/Build/Prepare TestFlight build")]
    public static void Prepare()
    {
        PlayerSettings.iOS.appleDeveloperTeamID = TEAM_ID;
        PlayerSettings.iOS.appleEnableAutomaticSigning = true;
        int n; if (!int.TryParse(PlayerSettings.iOS.buildNumber, out n)) n = 0;
        PlayerSettings.iOS.buildNumber = (n + 1).ToString();
        EditorUserBuildSettings.development = false;
        AssetDatabase.SaveAssets();
        string defs = PlayerSettings.GetScriptingDefineSymbols(UnityEditor.Build.NamedBuildTarget.iOS);
        bool playtest = defs.Contains("WORDROP_PLAYTEST");
        string msg = "Signing team: " + TEAM_ID + " (automatic)\n" +
                     "Version " + PlayerSettings.bundleVersion + ", build number " + PlayerSettings.iOS.buildNumber + " (was " + n + ")\n" +
                     "Development Build: OFF\n" +
                     "Playtest coins (WORDROP_PLAYTEST): " + (playtest ? "ON - right for testers, must be OFF for the store" : "OFF") + "\n\n" +
                     "Next: File > Build Settings > Build (iOS), then archive in Xcode.";
        Debug.Log("[TestFlightPrep] " + msg.Replace("\n", " | "));
        EditorUtility.DisplayDialog("TestFlight build prepared", msg, "OK");
    }
}
