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
        [Conditional("UNITY_EDITOR"), Conditional("WORDROP_LOG")]
        public static void Log(object message) => UnityEngine.Debug.Log(message);

        [Conditional("UNITY_EDITOR"), Conditional("WORDROP_LOG")]
        public static void Log(object message, UnityEngine.Object context)
            => UnityEngine.Debug.Log(message, context);
    }
}
