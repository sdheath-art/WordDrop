using System.Diagnostics;

namespace WordDrop
{
    /// <summary>
    /// Informational logging that COMPILES AWAY in a shipped build.
    ///
    /// Why this exists (2026-09-08 Spencer). There were 649 plain Debug.Log calls, 528 of them
    /// building an interpolated string. C# evaluates those strings BEFORE the call, so every one
    /// of them did the work of assembling text on every move even in a release build where
    /// nothing reads it. [Conditional] removes the call AND its arguments at compile time, so the
    /// string is never built at all — that is the whole point, and it is why a runtime
    /// `if (verbose)` check would not have helped.
    ///
    /// Two Conditional attributes are OR'd, so logs still fire:
    ///   - in the Editor (UNITY_EDITOR) — Spencer diagnoses from debug_log.txt, and DebugLogger
    ///     only sees messages that actually fire, so stripping here would silently empty that file
    ///   - in any build that defines WORDROP_LOG — add it to get logs on device
    /// and are stripped entirely from a normal player build.
    ///
    /// Warnings and errors are deliberately NOT routed through here — they should survive into
    /// release. Use Debug.LogWarning / Debug.LogError directly for those.
    /// </summary>
    public static class WDLog
    {
        /// <summary>Bot playtester: thousands of simulated games would bury the real log. Muted while it runs.</summary>
        public static bool Muted;

        /// <summary>2026-10-27 (Spencer: frame drops while playing). Debug.Log is slow in the editor and these tags fire per tile,
        /// per frame or per word - ~9,500 lines in one session. They stay silent unless VERBOSE is on. Everything else still logs.</summary>
        public static bool VERBOSE = false;
        private static readonly string[] NOISY = {
            "[Tile] ResetForPool", "[EntryDiag]", "[EntryPerf]", "[PayoutTier]", "[SFX]", "[VFX]", "[FX]", "[FX-",
            "[ExplosionTier]", "[Meltdown]",
        };
        private static bool Quiet(object message)
        {
            if (Muted) return true;
            if (VERBOSE || !(message is string s)) return false;
            foreach (var p in NOISY) if (s.StartsWith(p, System.StringComparison.Ordinal)) return true;
            return false;
        }

        [Conditional("UNITY_EDITOR"), Conditional("WORDROP_LOG")]
        public static void Log(object message) { if (!Quiet(message)) UnityEngine.Debug.Log(message); }

        [Conditional("UNITY_EDITOR"), Conditional("WORDROP_LOG")]
        public static void Log(object message, UnityEngine.Object context) { if (!Quiet(message)) UnityEngine.Debug.Log(message, context); }
    }
}

namespace WordDrop
{
    /// <summary>TEMP PERF PROBE (2026-10-28 Spencer: "a lot of frame rate problems ... on everything"). Every 3s logs
    /// [Perf]: average + worst frame, GameObject count, live DOTween tweens, active SpriteRenderers. A count that only
    /// ever grows = a leak. Strip once the slowdown is found.</summary>
    public class PerfProbe : UnityEngine.MonoBehaviour
    {
        public static bool ENABLED = true;
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!ENABLED) return;
            var go = new UnityEngine.GameObject("PerfProbe"); DontDestroyOnLoad(go); go.AddComponent<PerfProbe>();
        }
        private float _t0, _worst; private int _frames;
        private void Update()
        {
            float dt = UnityEngine.Time.unscaledDeltaTime;
            _frames++; if (dt > _worst) _worst = dt;
            if (_t0 == 0f) _t0 = UnityEngine.Time.unscaledTime;
            float el = UnityEngine.Time.unscaledTime - _t0;
            if (el < 3f) return;
            int gos = FindObjectsByType<UnityEngine.Transform>(UnityEngine.FindObjectsSortMode.None).Length;
            int srs = 0; foreach (var s in FindObjectsByType<UnityEngine.SpriteRenderer>(UnityEngine.FindObjectsSortMode.None)) if (s.enabled && s.gameObject.activeInHierarchy) srs++;
            int tw = DG.Tweening.DOTween.TotalPlayingTweens();
            UnityEngine.Debug.Log($"[Perf] avg {el / _frames * 1000f:F1}ms ({_frames / el:F0} fps) worst {_worst * 1000f:F0}ms | objects {gos} | sprites on {srs} | tweens {tw}");
            _t0 = UnityEngine.Time.unscaledTime; _frames = 0; _worst = 0f;
        }
    }
}
