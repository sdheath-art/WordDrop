using System.Collections;
using System.IO;
using UnityEngine;

namespace WordDrop
{
    /// <summary>Burst-photo the game so a glitch can be looked at STOPPED.
    ///
    /// Written 2026-09-10. The problem it solves: a visual fault (board visible before the
    /// slide-in, a tile wearing the wrong art for two frames, an outline left on screen) happens
    /// in a fraction of a second and is then gone. There is nothing to pause, nothing to show
    /// anyone, and no way to tell a real fault from a misremembered one. This takes a photo every
    /// few frames and writes them to disk, numbered, so the moment can be flipped through frame
    /// by frame — the slow-motion replay, not the live broadcast.
    ///
    /// It complements [[BoardInvariants]] rather than repeating it: invariants catch faults you
    /// can DESCRIBE in advance, this catches the ones you can only recognise by eye.
    ///
    ///   F9   capture a burst RIGHT NOW (press it, then do the thing)
    ///   F10  arm/disarm: capture the next level entry automatically
    ///
    /// Output: UnityProject/captures/&lt;time&gt;_&lt;label&gt;/frame_000.png ...
    /// The folder is logged to the Console when the burst starts.
    ///
    /// SIZE: MEASURED 2026-09-10 — output is the Game view's TARGET resolution (1179x2556 for
    /// the iPhone 16 profile), NOT the size of the docked view. A tiny docked Game view still
    /// produces full-resolution photos, so there is no need to maximise anything before
    /// capturing. SuperSize is only for going ABOVE native.</summary>
    public class CaptureRig : MonoBehaviour
    {
        /// <summary>Master switch. Turn OFF for any build that leaves this machine.</summary>
        public static bool Enabled = true;

        /// <summary>How many photos per burst.</summary>
        public static int Frames = 48;

        /// <summary>Take a photo every Nth frame. 3 gives ~2.4s of coverage at 60fps, enough to
        /// trigger a burst manually and THEN do the thing. Transitions override this to 1 —
        /// see CaptureTransition. Measured 2026-09-10: a level entry is over in ~0.6s, so at
        /// every-3rd-frame you get about four usable photos of the slide and thirty of a board
        /// sitting still. For anything that MOVES, sample every frame.</summary>
        public static int EveryNthFrame = 3;

        /// <summary>Resolution multiplier. 1 = whatever the Game view is. Raise it if the images
        /// are too small to read; it costs time per shot.</summary>
        public static int SuperSize = 1;

        /// <summary>When true the NEXT level entry is captured automatically. Toggled with F10.</summary>
        public static bool CaptureNextLevelEntry = false;

        public static CaptureRig Instance { get; private set; }

        private bool _busy;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Statics survive a play-mode exit; a stale Instance here would be a destroyed
            // object that is NOT null (Unity's fake-null), which "?." does not protect against.
            Instance = null;
            CaptureNextLevelEntry = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Enabled) return;
            var go = new GameObject("CaptureRig");
            go.AddComponent<CaptureRig>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnEnable()
        {
            LevelIntroModal.OnPlayStarted -= HandleLevelStarted;
            LevelIntroModal.OnPlayStarted += HandleLevelStarted;
        }

        private void OnDisable()
        {
            LevelIntroModal.OnPlayStarted -= HandleLevelStarted;
        }

        private void HandleLevelStarted()
        {
            if (!CaptureNextLevelEntry) return;
            CaptureNextLevelEntry = false;   // one-shot: arm it again for the next one
            CaptureTransition("level_entry");
        }

        private void Update()
        {
            if (!Enabled) return;

            if (Input.GetKeyDown(KeyCode.F9))
                Capture("manual");

            if (Input.GetKeyDown(KeyCode.F10))
            {
                CaptureNextLevelEntry = !CaptureNextLevelEntry;
                Debug.Log($"[Capture] next level entry will {(CaptureNextLevelEntry ? "BE CAPTURED" : "NOT be captured")}");
            }
        }

        /// <summary>Burst for something SHORT and MOVING — every single frame, one second of it.
        /// A transition is over in well under a second; sampling it coarsely just gives you
        /// duplicate photos of the end state. 2026-09-10.</summary>
        public static void CaptureTransition(string label)
        {
            Capture(label, 60, 1);
        }

        /// <summary>Start a burst. Ignored if one is already running. Pass frames/everyNth to
        /// override the defaults for this burst only.</summary>
        public static void Capture(string label, int frames = -1, int everyNth = -1)
        {
            if (!Enabled) return;
            if (Instance == null)
            {
                Debug.LogWarning("[Capture] no CaptureRig in the scene — nothing captured.");
                return;
            }
            if (Instance._busy)
            {
                Debug.Log("[Capture] already capturing — ignoring.");
                return;
            }
            Instance.StartCoroutine(Instance.CaptureRoutine(
                label,
                frames   > 0 ? frames   : Frames,
                everyNth > 0 ? everyNth : EveryNthFrame));
        }

        private IEnumerator CaptureRoutine(string label, int frames, int everyNth)
        {
            _busy = true;

            string stamp  = System.DateTime.Now.ToString("HHmmss");
            string root   = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "captures"));
            string folder = Path.Combine(root, $"{stamp}_{label}");

            bool ok = true;
            try { Directory.CreateDirectory(folder); }
            catch (System.Exception e)
            {
                ok = false;
                Debug.LogWarning($"[Capture] could not create {folder}: {e.Message}");
            }

            if (!ok) { _busy = false; yield break; }

            Debug.Log($"[Capture] START {frames} frames (every {everyNth}) -> {folder}");

            int shot = 0;
            while (shot < frames)
            {
                // End of frame: everything for this frame has rendered, so the photo shows what
                // the player would actually have seen.
                yield return new WaitForEndOfFrame();

                string file = Path.Combine(folder, $"frame_{shot:000}.png");
                ScreenCapture.CaptureScreenshot(file, Mathf.Max(1, SuperSize));
                shot++;

                for (int i = 1; i < Mathf.Max(1, everyNth); i++)
                    yield return null;
            }

            Debug.Log($"[Capture] DONE {shot} frames -> {folder}");
            _busy = false;
        }
    }
}
