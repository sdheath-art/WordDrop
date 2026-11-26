#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// BOT REMOTE (2026-11-26 Spencer: "is it possible to cut this step out and you just run the bots you want ... yourself?").
    /// Editor only (the whole file is inside UNITY_EDITOR, nothing reaches a build). A request file starts bot runs with no clicks:
    ///
    ///   UnityProject/BotReports/bot_request.txt - one run per line, e.g.
    ///       name=scattered_17_19 levels=17,18,19 trials=60 focused=0 rescue=1
    ///   (set=Type.Field:value;Type.Field:value sets public static fields for that run and the ones after it;
    ///    focused: 1 goal-focused casual / 0 scattered; rescue = the END-OF-LEVEL TOME RESCUE master switch; '#' lines are skipped)
    ///
    /// The editor half sees the file (checked every 2 s, also while Unity is in the background), re-imports changed scripts, and
    /// enters Play mode. The play half takes the file (renames it to bot_request.taken so it can never loop), runs each line through
    /// BotPlaytester.RunSelected, copies the report to BotReports/remote_NAME.txt / .csv, writes BotReports/bot_remote_done.txt and
    /// leaves Play mode. It never starts while Play mode is already on (Spencer may be playing).
    /// </summary>
    public class BotRemote : MonoBehaviour
    {
        private static string Folder => BotPlaytester.Folder;
        private static string RequestPath => Path.Combine(Folder, "bot_request.txt");
        private static string TakenPath => Path.Combine(Folder, "bot_request.taken");
        private static string DonePath => Path.Combine(Folder, "bot_remote_done.txt");
        private const string REFRESHED_KEY = "WordDrop.BotRemote.refreshed";
        private const string STARTED_KEY = "WordDrop.BotRemote.startedPlay";

        // ───────────────────────── editor half ─────────────────────────
        private static double s_nextCheck;

        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditor()
        {
            UnityEditor.EditorApplication.update -= EditorTick;
            UnityEditor.EditorApplication.update += EditorTick;
        }

        private static void EditorTick()
        {
            if (UnityEditor.EditorApplication.timeSinceStartup < s_nextCheck) return;
            s_nextCheck = UnityEditor.EditorApplication.timeSinceStartup + 2.0;
            if (UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return;
            if (!File.Exists(RequestPath)) return;
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating) return;
            if (!UnityEditor.SessionState.GetBool(REFRESHED_KEY, false))
            {
                // pick up script edits made while Unity was in the background (it only re-imports by itself on focus)
                UnityEditor.SessionState.SetBool(REFRESHED_KEY, true);
                UnityEditor.AssetDatabase.Refresh();
                return;
            }
            if (UnityEditor.EditorUtility.scriptCompilationFailed)
            {
                Fail("scripts do not compile - see the Unity Console. No run was started.");
                return;
            }
            UnityEditor.SessionState.SetBool(REFRESHED_KEY, false);
            UnityEditor.SessionState.SetBool(STARTED_KEY, true);
            Debug.Log("[BotRemote] request found - entering Play mode");
            UnityEditor.EditorApplication.isPlaying = true;
        }

        private static void Fail(string why)
        {
            try
            {
                UnityEditor.SessionState.SetBool(REFRESHED_KEY, false);
                if (File.Exists(TakenPath)) File.Delete(TakenPath);
                if (File.Exists(RequestPath)) File.Move(RequestPath, TakenPath);
                File.WriteAllText(DonePath, $"FAILED {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{why}\n");
            }
            catch (System.Exception ex) { Debug.LogError($"[BotRemote] {ex.Message}"); }
            Debug.LogError("[BotRemote] " + why);
        }

        // ───────────────────────── play half ─────────────────────────
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void PlayStart()
        {
            // only a Play session this file started itself; a request dropped while Spencer is playing waits for him to stop
            if (!UnityEditor.SessionState.GetBool(STARTED_KEY, false)) return;
            UnityEditor.SessionState.SetBool(STARTED_KEY, false);
            if (!File.Exists(RequestPath)) return;
            Application.runInBackground = true;   // Unity is usually NOT the focused window here; without this the frame loop sleeps
            var go = new GameObject("BotRemote"); DontDestroyOnLoad(go);
            go.AddComponent<BotRemote>().StartCoroutine("RunAll");
        }

        private IEnumerator RunAll()
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(RequestPath);
                if (File.Exists(TakenPath)) File.Delete(TakenPath);
                File.Move(RequestPath, TakenPath);
                if (File.Exists(DonePath)) File.Delete(DonePath);
            }
            catch (System.Exception ex) { Fail("could not read the request: " + ex.Message); UnityEditor.EditorApplication.isPlaying = false; yield break; }

            var log = new System.Text.StringBuilder();
            float t0 = Time.realtimeSinceStartup;
            // let the game boot (dictionary, level table), then wait for the bot object and the full-board mode
            yield return new WaitForSecondsRealtime(4f);
            while ((BotPlaytester.Instance == null || !SurvivalManager.FullBoardActive) && Time.realtimeSinceStartup - t0 < 60f) yield return null;
            if (BotPlaytester.Instance == null || !SurvivalManager.FullBoardActive)
                log.AppendLine("FAILED: the game did not reach full-board mode within 60 s - nothing was run.");
            else
            {
                var bot = BotPlaytester.Instance;
                int n = 0;
                foreach (var raw in lines)
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    n++;
                    var kv = new Dictionary<string, string>();
                    foreach (var part in line.Split(' ', '\t'))
                    {
                        int eq = part.IndexOf('=');
                        if (eq > 0) kv[part.Substring(0, eq).Trim().ToLowerInvariant()] = part.Substring(eq + 1).Trim();
                    }
                    string name = kv.TryGetValue("name", out var nm) && nm.Length > 0 ? nm : "run" + n;
                    foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                    // set=LevelRules.OWL_DROP_OVERRIDE:3;RulesEngine.SOME_FLAG:true - public static fields, so a sweep needs no recompile
                    if (kv.TryGetValue("set", out var sets))
                        foreach (var one in sets.Split(';'))
                        {
                            int dot = one.IndexOf('.'), colon = one.IndexOf(':');
                            if (dot <= 0 || colon <= dot) continue;
                            try
                            {
                                var ty = System.Type.GetType("WordDrop." + one.Substring(0, dot));
                                var fi = ty != null ? ty.GetField(one.Substring(dot + 1, colon - dot - 1), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static) : null;
                                if (fi == null) { log.AppendLine($"{name}: set '{one}' - no such field"); continue; }
                                fi.SetValue(null, System.Convert.ChangeType(one.Substring(colon + 1), fi.FieldType, System.Globalization.CultureInfo.InvariantCulture));
                            }
                            catch (System.Exception ex) { log.AppendLine($"{name}: set '{one}' failed - {ex.Message}"); }
                        }
                    // prefs=tut2_claim:del;tut2_swap:1 - PlayerPrefs ints ("del" removes the key); fresh=1 = TUTORIAL RESET
                    if (kv.TryGetValue("fresh", out var fr) && fr != "0") TutorialBeats.ResetAll();
                    if (kv.TryGetValue("prefs", out var prefs))
                    {
                        foreach (var one in prefs.Split(';'))
                        {
                            int colon = one.IndexOf(':'); if (colon <= 0) continue;
                            string k = one.Substring(0, colon), v = one.Substring(colon + 1);
                            if (v == "del") PlayerPrefs.DeleteKey(k); else if (int.TryParse(v, out int iv)) PlayerPrefs.SetInt(k, iv);
                        }
                        PlayerPrefs.Save();
                    }
                    // watch=1 level=21 seconds=90 shot=1.5 - the fake finger PLAYS the visible game (MonkeyTester.PLAYER_MODE) while
                    // a screenshot is saved every `shot` seconds to BotReports/watch/NAME/ (+ the input log, fault report, debug log)
                    if (kv.TryGetValue("watch", out var wv) && wv != "0")
                    {
                        var mk = MonkeyTester.Instance;
                        if (mk == null) { log.AppendLine($"{name}: SKIPPED - no MonkeyTester"); continue; }
                        float seconds = 90f, shot = 1.5f; int level = 0;
                        if (kv.TryGetValue("seconds", out var sv)) float.TryParse(sv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds);
                        if (kv.TryGetValue("shot", out var shv)) float.TryParse(shv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out shot);
                        if (kv.TryGetValue("level", out var lv)) int.TryParse(lv, out level);
                        string dir = Path.Combine(Folder, "watch", name);
                        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); Directory.CreateDirectory(dir); } catch (System.Exception ex) { log.AppendLine($"{name}: folder - {ex.Message}"); }
                        if (level > 0 && SurvivalManager.Instance != null)
                        {
                            LevelRules.ForceFreshBoardOnNextApply();
                            SurvivalManager.Instance.DebugJumpToStage(level + LevelMapPanel.TUTORIAL_LEVELS);
                        }
                        else if (level > 0) log.AppendLine($"{name}: no SurvivalManager yet - level jump skipped");
                        yield return new WaitForSecondsRealtime(1f);
                        MonkeyTester.PLAYER_MODE = true; MonkeyTester.PLAY_BADLY = false; MonkeyTester.MINUTES = seconds / 60f + 2f;
                        float keepStuck = MonkeyTester.STUCK_GIVE_UP_SECONDS; MonkeyTester.STUCK_GIVE_UP_SECONDS = 9999f;
                        mk.StartRun();
                        float w0 = Time.realtimeSinceStartup, nextShot = 0f; int shots = 0;
                        while (mk.Running && Time.realtimeSinceStartup - w0 < seconds)
                        {
                            float el = Time.realtimeSinceStartup - w0;
                            if (el >= nextShot)
                            {
                                nextShot = el + Mathf.Max(0.2f, shot);
                                try { ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{shots:000}_{el:000.0}s.png")); shots++; } catch { }
                            }
                            yield return null;
                        }
                        if (mk.Running) mk.StopRun("watch time up");
                        MonkeyTester.PLAYER_MODE = false; MonkeyTester.STUCK_GIVE_UP_SECONDS = keepStuck;
                        yield return null;
                        try
                        {
                            foreach (var f in new[] { "monkey_latest.txt", "monkey_inputs_latest.txt" })
                            { string src = Path.Combine(MonkeyTester.Folder, f); if (File.Exists(src)) File.Copy(src, Path.Combine(dir, f), true); }
                            string dbg = Path.Combine(Path.GetDirectoryName(Application.dataPath), "debug_log.txt");
                            if (File.Exists(dbg)) File.Copy(dbg, Path.Combine(dir, "debug_log.txt"), true);
                        }
                        catch (System.Exception ex) { log.AppendLine($"{name}: copy - {ex.Message}"); }
                        log.AppendLine($"{name}: WATCH OK  level={level} {seconds:F0}s  {shots} screenshots -> BotReports/watch/{name}/  ({mk.Status})");
                        continue;
                    }
                    if (!kv.TryGetValue("levels", out var levels) || levels.Length == 0) { log.AppendLine($"{name}: SKIPPED - no levels= given"); continue; }
                    BotPlaytester.SELECTED = levels;
                    if (kv.TryGetValue("trials", out var tr) && int.TryParse(tr, out int trials) && trials > 0) BotPlaytester.SELECTED_TRIALS = trials;
                    if (kv.TryGetValue("focused", out var fo)) BotPlaytester.CASUAL_FOCUSED = fo != "0";
                    if (kv.TryGetValue("rescue", out var re)) RulesEngine.ENDGAME_AIRPLANE = re != "0";
                    BotPlaytester.RANDOM_PLAYER = 0;

                    string latestTxt = Path.Combine(Folder, "bot_latest.txt"), latestCsv = Path.Combine(Folder, "bot_latest.csv");
                    System.DateTime before = File.Exists(latestTxt) ? File.GetLastWriteTimeUtc(latestTxt) : System.DateTime.MinValue;
                    float j0 = Time.realtimeSinceStartup;
                    bot.RunSelected();
                    yield return null;
                    while (bot.Running) yield return null;
                    bool wrote = File.Exists(latestTxt) && File.GetLastWriteTimeUtc(latestTxt) > before;
                    if (wrote)
                    {
                        try
                        {
                            File.Copy(latestTxt, Path.Combine(Folder, $"remote_{name}.txt"), true);
                            if (File.Exists(latestCsv)) File.Copy(latestCsv, Path.Combine(Folder, $"remote_{name}.csv"), true);
                        }
                        catch (System.Exception ex) { log.AppendLine($"{name}: copy failed - {ex.Message}"); }
                    }
                    log.AppendLine($"{name}: {(wrote ? "OK" : "NO REPORT (" + bot.Status + ")")}  levels={levels} trials={BotPlaytester.SELECTED_TRIALS} focused={(BotPlaytester.CASUAL_FOCUSED ? 1 : 0)} rescue={(RulesEngine.ENDGAME_AIRPLANE ? 1 : 0)}  {(Time.realtimeSinceStartup - j0):F0}s");
                }
                if (n == 0) log.AppendLine("FAILED: the request had no run lines.");
            }
            try { File.WriteAllText(DonePath, $"DONE {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{log}"); }
            catch (System.Exception ex) { Debug.LogError($"[BotRemote] {ex.Message}"); }
            Debug.Log("[BotRemote] finished\n" + log);
            yield return null;
            UnityEditor.EditorApplication.isPlaying = false;
        }
    }
}
#endif
