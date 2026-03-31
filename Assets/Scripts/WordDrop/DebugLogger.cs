using UnityEngine;
using System.IO;
using System;

namespace WordDrop
{
    /// <summary>
    /// Writes all Debug.Log messages to a file on disk so Claude can read them directly.
    /// File: [project]/debug_log.txt — overwritten each play session.
    /// </summary>
    public class DebugLogger : MonoBehaviour
    {
        private static string _logPath;
        private static StreamWriter _writer;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Init()
        {
            _logPath = Path.Combine(Application.dataPath, "..", "debug_log.txt");
            try
            {
                _writer = new StreamWriter(_logPath, false);
                _writer.AutoFlush = true;
                _writer.WriteLine($"=== Debug Log Started {DateTime.Now} ===");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[DebugLogger] Failed to open log file: {e.Message}");
            }

            Application.logMessageReceived += OnLogMessage;
            Application.quitting += OnQuit;
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (_writer == null) return;
            try
            {
                string prefix = type == LogType.Error ? "ERROR" :
                               type == LogType.Warning ? "WARN" :
                               type == LogType.Exception ? "EXCEPTION" : "LOG";
                _writer.WriteLine($"[{Time.frameCount}] [{prefix}] {condition}");
                if (type == LogType.Error || type == LogType.Exception)
                    _writer.WriteLine($"  Stack: {stackTrace.Split('\n')[0]}");
            }
            catch { }
        }

        private static void OnQuit()
        {
            if (_writer != null)
            {
                _writer.WriteLine($"=== Debug Log Ended {DateTime.Now} ===");
                _writer.Close();
                _writer = null;
            }
        }
    }
}
