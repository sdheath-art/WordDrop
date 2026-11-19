using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// 2026-11-18 SAFETY NET for a frozen game clock.
    /// Spencer, on the phone (level 7, three times, once right as a Messenger notification arrived): the screen "froze ... the
    /// letter tiles stay on the board and it sounds like im making moves behind the board still". Sound and input running
    /// while nothing on screen moves is what Time.timeScale stuck at 0 looks like: the rules and the audio do not use the game
    /// clock, every tile animation does. Two places set it to 0 on purpose for a fraction of a second (WordDropFX.HitStop,
    /// MeltdownManager's stamp) and put it back themselves; if either is cut short - an interruption, an exception, a
    /// coroutine that never resumes - it stays 0 for good.
    ///
    /// THIS IS NOT A CONFIRMED DIAGNOSIS (the phone keeps no log). It is a guard that is correct whatever the cause: nothing
    /// in the game holds the clock at 0 for a whole second, so if it has been 0 that long - or is 0 when the app comes back
    /// from an interruption - it is put back to 1. Every rescue is counted (PlayerPrefs "clock_guard_fired") and shown as a
    /// small number after the version in Settings, so the next phone session TELLS us whether this was the cause.
    /// </summary>
    public static class ClockGuard
    {
        public static bool  ON = true;
        public static float MAX_FROZEN = 1.0f;   // seconds of real time; the longest deliberate freeze is ~0.3 s
        public const string PREF = "clock_guard_fired";
        public static int Fired => PlayerPrefs.GetInt(PREF, 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            var go = new GameObject("ClockGuard") { hideFlags = HideFlags.HideAndDontSave };
            Object.DontDestroyOnLoad(go); go.AddComponent<Runner>();
            // ERROR NOTES (11-18): the phone build switches Unity's logger off (DebugLogger), so a code error on the device left no
            // trace at all - the freezes could not be read. Exceptions ONLY are let through again (ordinary logs stay off) and
            // each one is counted and written to Playthroughs/errors.txt, next to the play log in the Files app.
#if !UNITY_EDITOR
            Debug.unityLogger.logEnabled = true; Debug.unityLogger.filterLogType = LogType.Exception;
#endif
            Application.logMessageReceived -= OnLog; Application.logMessageReceived += OnLog;
        }

        public const string PREF_ERR = "error_notes_count";
        public static int Errors => PlayerPrefs.GetInt(PREF_ERR, 0);
        private static int s_thisSession;
        private static void OnLog(string condition, string stack, LogType type)
        {
            if (type != LogType.Exception) return;
            PlayerPrefs.SetInt(PREF_ERR, Errors + 1);
            if (++s_thisSession > 40) return;   // a repeating error must not fill the disk
            try
            {
                string dir = System.IO.Path.Combine(Application.persistentDataPath, "Playthroughs");
                System.IO.Directory.CreateDirectory(dir);
                string st = stack ?? ""; if (st.Length > 1500) st = st.Substring(0, 1500);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.txt"),
                    $"[{System.DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{Application.version} t={Time.realtimeSinceStartup:F0}s timeScale={Time.timeScale}\n{condition}\n{st}\n\n");
            }
            catch { }
        }

        private static void Rescue(string why)
        {
            Time.timeScale = 1f;
            PlayerPrefs.SetInt(PREF, Fired + 1); PlayerPrefs.Save();
            WDLog.Log($"[ClockGuard] game clock was stuck at 0 ({why}) - restored. Rescues so far: {Fired}");
        }

        private class Runner : MonoBehaviour
        {
            private float _frozenFor;
            private void Update()
            {
                if (!ON) return;
                if (Time.timeScale > 0.0001f) { _frozenFor = 0f; return; }
                _frozenFor += Time.unscaledDeltaTime;
                if (_frozenFor > MAX_FROZEN) { _frozenFor = 0f; Rescue("frozen for over a second"); }
            }
            private void OnApplicationPause(bool paused) { if (ON && !paused && Time.timeScale <= 0.0001f) Rescue("on return from an interruption"); }
            private void OnApplicationFocus(bool focus)  { if (ON && focus && Time.timeScale <= 0.0001f) Rescue("on regaining focus"); }
        }
    }
}
