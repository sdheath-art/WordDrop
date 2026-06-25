using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Runs once at startup. Disables stack-trace capture for plain Debug.Log calls.
    ///
    /// WHY: this project has ~700 Debug.Log calls, and in the editor Unity captures a full managed
    /// stack trace on EVERY Log by default — that's the dominant cost of "the editor feels sluggish
    /// during play" in a log-heavy project. Turning it off keeps all log TEXT (still mirrored to
    /// debug_log.txt) but removes the per-call stack-walk. Warnings/Errors keep their stack traces so
    /// real problems are still traceable. 2026-06-18 Spencer.
    /// </summary>
    internal static class PerfBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.SetStackTraceLogType(LogType.Warning, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
            Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        }
    }
}
