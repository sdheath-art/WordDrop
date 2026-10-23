using System.Collections;
using System.IO;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// ONE-TAP SCREENSHOT FOR CLAUDE (2026-10-23 Spencer: "build a function where i can just tap a button inside unity and it
    /// will take a screenshot of the game for you"). Press F8 in Play mode (or the FX Test Menu button) and the Game view is
    /// saved to UnityProject/Screenshots/claude_latest.png — a FIXED path Claude reads directly, no Desktop round-trip — plus a
    /// timestamped copy so a sequence can be compared. The FX Test Menu hides itself for the capture frame.
    /// Editor/dev tool only — nothing calls it in normal play.
    /// </summary>
    public class ClaudeScreenshot : MonoBehaviour
    {
        public static ClaudeScreenshot Instance { get; private set; }
        public static KeyCode HOTKEY = KeyCode.F8;   // F9/F10 belong to CaptureRig (frame-burst capture). On a Mac this needs fn+F8
        public static KeyCode HOTKEY_ALT = KeyCode.BackQuote;   // ` (under Esc) — no fn needed; F8 alone is the Mac play/pause media key
        /// <summary>True for the frame(s) a capture is being taken — overlays (the FX Test Menu) skip drawing.</summary>
        public static bool Capturing { get; private set; }
        public static int KEEP_TIMESTAMPED = 30;   // older timestamped shots are deleted so the folder doesn't balloon

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("ClaudeScreenshot");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ClaudeScreenshot>();
            WDLog.Log($"[ClaudeShot] ready — press ` (backtick) or fn+F8, or the FX Test Menu button -> {Folder}/claude_latest.png");
        }

        public static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

        private void Update()
        {
            if (Input.GetKeyDown(HOTKEY) || Input.GetKeyDown(HOTKEY_ALT)) Take();
        }

        public void Take()
        {
            if (!Capturing) StartCoroutine(CaptureRoutine());
        }

        private IEnumerator CaptureRoutine()
        {
            Capturing = true;
            yield return null;                         // one frame with overlays hidden
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            Capturing = false;
            if (tex == null) { Debug.LogWarning("[ClaudeShot] capture failed"); yield break; }
            byte[] png = tex.EncodeToPNG();
            Destroy(tex);
            try
            {
                Directory.CreateDirectory(Folder);
                string latest = Path.Combine(Folder, "claude_latest.png");
                File.WriteAllBytes(latest, png);
                string stamped = Path.Combine(Folder, $"claude_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");
                File.WriteAllBytes(stamped, png);
                var old = Directory.GetFiles(Folder, "claude_2*.png");
                System.Array.Sort(old);
                for (int i = 0; i < old.Length - KEEP_TIMESTAMPED; i++) File.Delete(old[i]);
                WDLog.Log($"[ClaudeShot] saved {latest} ({png.Length / 1024} KB) + {Path.GetFileName(stamped)}");
            }
            catch (System.Exception ex) { Debug.LogError($"[ClaudeShot] write failed: {ex.Message}"); }
        }
    }
}
