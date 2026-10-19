using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace WordDrop
{
    /// <summary>Records the game BACKWARDS — the dashcam / "save the last 30 seconds" button.
    ///
    /// Written 2026-09-10, because [[CaptureRig]] has a hole Spencer spotted immediately: by the
    /// time you SEE a glitch, reach for the menu and click, the moment is a second gone and you
    /// photograph the aftermath. Arming beforehand only works for faults you can reproduce on
    /// demand. For the ones that surprise you, the recorder has to already be running.
    ///
    /// So it keeps the last couple of seconds of frames in memory, throwing away the oldest, and
    /// "Save Replay" writes out what ALREADY happened. Pressing it late stops mattering.
    ///
    /// HOW IT STAYS CHEAP:
    ///  • frames are downscaled by Divisor before storage (quarter size = 1/16th the memory),
    ///  • the GPU readback is ASYNC (AsyncGPUReadback), so it does not stall the render thread
    ///    the way ReadPixels would — which matters, because a recorder that tanks the frame rate
    ///    changes the very timing you are trying to observe,
    ///  • buffers are allocated once and reused, so it does not churn the GC,
    ///  • it only runs while Recording is true.
    ///
    /// Quarter size is plenty to see "the board is on screen when it should not be". It is NOT
    /// enough to judge fine art detail — use CaptureRig's full-resolution burst for that.</summary>
    public class ReplayBuffer : MonoBehaviour
    {
        public static bool Enabled = true;

        /// <summary>Roughly how many seconds of history to keep. MEASURED 2026-09-10: 2s is NOT
        /// enough. Walking the 3-level menu to click "Save Replay" took longer than that, and the
        /// transition had already rolled out of the buffer — the lateness problem simply moved.
        /// 5s covers a menu trip. Use the ⇧⌘8 shortcut and you need far less.</summary>
        public static float Seconds = 5.0f;

        /// <summary>Downscale factor for stored frames. MEASURED 2026-09-10: in the Editor
        /// Screen.width is the Game view's DISPLAYED size (~736), not the 1179 target — so
        /// divisor 4 gave a thin 184x276. 3 keeps 5 seconds inside ~100 MB and is still legible;
        /// drop to 2 for more detail if you also shorten Seconds.</summary>
        public static int Divisor = 3;

        /// <summary>Store every Nth frame. 1 = every frame (best for catching a 2-frame flicker).</summary>
        public static int EveryNthFrame = 1;

        public static bool Recording { get; private set; }
        public static ReplayBuffer Instance { get; private set; }

        private struct Shot
        {
            public byte[] Data;
            public int    Frame;
            public float  Time;
            public bool   Valid;
        }

        private Shot[]        _ring;
        private int           _write;      // next slot to write
        private int           _stored;     // how many slots hold a valid shot
        private int           _w, _h;
        private RenderTexture _full;
        private RenderTexture _small;
        private Texture2D     _encodeTex;
        private bool          _dumping;
        private byte[]        _flip;      // scratch for the vertical flip on encode

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance  = null;
            Recording = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!Enabled) return;
            var go = new GameObject("ReplayBuffer");
            go.AddComponent<ReplayBuffer>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Release();
        }

        // ── control ────────────────────────────────────────────────────────────

        public static void StartRecording()
        {
            if (!Enabled) return;
            if (Instance == null) { Debug.LogWarning("[Replay] no ReplayBuffer — enter Play mode."); return; }
            if (Recording) { Debug.Log("[Replay] already recording."); return; }
            Instance.Begin();
        }

        public static void StopRecording()
        {
            if (Instance == null || !Recording) return;
            Recording = false;
            Debug.Log($"[Replay] STOPPED — {Instance._stored} frame(s) held.");
        }

        /// <summary>Write everything currently held, oldest first. Safe to call from anywhere,
        /// including an invariant that just tripped.</summary>
        public static void Save(string label = "replay")
        {
            if (Instance == null) { Debug.LogWarning("[Replay] nothing to save — not running."); return; }
            if (Instance._stored == 0) { Debug.LogWarning("[Replay] nothing held yet — is recording on?"); return; }
            if (Instance._dumping) { Debug.Log("[Replay] already saving."); return; }
            Instance.StartCoroutine(Instance.DumpRoutine(label));
        }

        private void Begin()
        {
            Release();

            _w = Mathf.Max(8, Screen.width  / Mathf.Max(1, Divisor));
            _h = Mathf.Max(8, Screen.height / Mathf.Max(1, Divisor));

            int capacity = Mathf.Max(8, Mathf.RoundToInt(60f * Seconds / Mathf.Max(1, EveryNthFrame)));
            _ring = new Shot[capacity];
            for (int i = 0; i < capacity; i++)
                _ring[i] = new Shot { Data = new byte[_w * _h * 4], Valid = false };

            _full  = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGB32);
            _small = new RenderTexture(_w, _h, 0, RenderTextureFormat.ARGB32);
            _encodeTex = new Texture2D(_w, _h, TextureFormat.RGBA32, false);
            _flip      = new byte[_w * _h * 4];

            _write = 0; _stored = 0;
            Recording = true;

            float mb = capacity * _w * _h * 4f / (1024f * 1024f);
            Debug.Log($"[Replay] RECORDING {_w}x{_h}, {capacity} frames (~{Seconds:0.0}s), ~{mb:0} MB");

            StartCoroutine(RecordLoop());
        }

        private void Release()
        {
            if (_full  != null) { _full.Release();  Destroy(_full);  _full  = null; }
            if (_small != null) { _small.Release(); Destroy(_small); _small = null; }
            if (_encodeTex != null) { Destroy(_encodeTex); _encodeTex = null; }
            _ring = null; _flip = null; _write = 0; _stored = 0;
        }

        // ── capture ────────────────────────────────────────────────────────────

        private IEnumerator RecordLoop()
        {
            int tick = 0;
            while (Recording)
            {
                // Everything for this frame has rendered, so what we grab is what the player saw.
                yield return new WaitForEndOfFrame();
                if (!Recording) break;

                if (tick % Mathf.Max(1, EveryNthFrame) == 0 && _full != null)
                {
                    // The final composited screen -> full RT -> downscaled RT -> async readback.
                    ScreenCapture.CaptureScreenshotIntoRenderTexture(_full);
                    Graphics.Blit(_full, _small);

                    int slot  = _write;
                    int frame = Time.frameCount;
                    float t   = Time.unscaledTime;
                    _write = (_write + 1) % _ring.Length;
                    if (_stored < _ring.Length) _stored++;

                    AsyncGPUReadback.Request(_small, 0, TextureFormat.RGBA32, req =>
                    {
                        // Fires a few frames later; the component may be gone by then.
                        if (req.hasError || _ring == null || slot >= _ring.Length) return;
                        var src = req.GetData<byte>();
                        var dst = _ring[slot].Data;
                        if (dst == null || src.Length != dst.Length) return;
                        src.CopyTo(dst);
                        _ring[slot].Frame = frame;
                        _ring[slot].Time  = t;
                        _ring[slot].Valid = true;
                    });
                }
                tick++;
            }
        }

        // ── write out ──────────────────────────────────────────────────────────

        private IEnumerator DumpRoutine(string label)
        {
            _dumping = true;

            // Let the outstanding async readbacks land, or the newest frames — the ones nearest
            // the glitch, i.e. the ones actually worth having — arrive blank.
            AsyncGPUReadback.WaitAllRequests();
            yield return null;

            string stamp  = System.DateTime.Now.ToString("HHmmss");
            string root   = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "captures"));
            string folder = Path.Combine(root, $"{stamp}_{label}");
            Directory.CreateDirectory(folder);

            // Oldest first. The ring is circular, so start at the write head when it has wrapped.
            int count = _stored;
            int start = (_stored == _ring.Length) ? _write : 0;

            int written = 0;
            for (int i = 0; i < count; i++)
            {
                int slot = (start + i) % _ring.Length;
                if (!_ring[slot].Valid) continue;

                // FLIP VERTICALLY. MEASURED 2026-09-10: AsyncGPUReadback hands back rows
                // bottom-up while Texture2D.LoadRawTextureData wants them top-down, so the
                // first replay came out upside down — booster row at the top, letters
                // inverted. Reverse the rows on the way in. Scratch buffer is reused.
                int stride = _w * 4;
                var srcRows = _ring[slot].Data;
                for (int y = 0; y < _h; y++)
                    System.Array.Copy(srcRows, y * stride, _flip, (_h - 1 - y) * stride, stride);

                _encodeTex.LoadRawTextureData(_flip);
                _encodeTex.Apply(false);
                byte[] png = _encodeTex.EncodeToPNG();

                // Name by AGE so the ordering survives: 000 is the oldest held frame, the last
                // file is the instant Save was pressed.
                string file = Path.Combine(folder, $"frame_{written:000}_f{_ring[slot].Frame}.png");
                File.WriteAllBytes(file, png);
                written++;

                // Spread the encode over several frames — doing ~150 PNGs in one go freezes the
                // editor for seconds and looks like a hang.
                if ((written % 12) == 0) yield return null;
            }

            Debug.Log($"[Replay] SAVED {written} frame(s) (oldest first) -> {folder}");
            _dumping = false;
        }
    }
}
