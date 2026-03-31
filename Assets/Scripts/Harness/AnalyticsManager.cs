using UnityEngine;
using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Lightweight analytics stub that logs player behavior events.
/// Ships with every harness-generated game. Events are stored locally
/// and can be swapped for Firebase/your own backend later.
///
/// Usage: AnalyticsManager.Log("game_over", "score", 1250, "wave", 5, "cause", "enemy_bullet");
/// </summary>
public static class AnalyticsManager
{
    private static List<Dictionary<string, object>> _events = new List<Dictionary<string, object>>();
    private static string _sessionId;
    private static float _sessionStart;
    private static int _gameCount = 0;

    // ─── Session ────────────────────────────────────────────────

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AutoInit()
    {
        _sessionId = Guid.NewGuid().ToString().Substring(0, 8);
        _sessionStart = Time.realtimeSinceStartup;
        _events.Clear();
        _gameCount = 0;

        Log("session_start",
            "device", SystemInfo.deviceModel,
            "os", SystemInfo.operatingSystem,
            "screen", $"{Screen.width}x{Screen.height}");

        Application.quitting += OnQuit;
    }

    private static void OnQuit()
    {
        float duration = Time.realtimeSinceStartup - _sessionStart;
        Log("session_end",
            "duration_sec", Mathf.RoundToInt(duration),
            "games_played", _gameCount);

        FlushToDisk();
    }

    // ─── Core API ───────────────────────────────────────────────

    /// <summary>
    /// Log an analytics event with key-value pairs.
    /// Example: AnalyticsManager.Log("game_over", "score", 500, "wave", 3);
    /// </summary>
    public static void Log(string eventName, params object[] kvPairs)
    {
        var evt = new Dictionary<string, object>();
        evt["event"] = eventName;
        evt["t"] = Mathf.RoundToInt((Time.realtimeSinceStartup - _sessionStart) * 10f) / 10f;
        evt["session"] = _sessionId;

        // Parse key-value pairs
        for (int i = 0; i + 1 < kvPairs.Length; i += 2)
        {
            string key = kvPairs[i]?.ToString() ?? "null";
            evt[key] = kvPairs[i + 1];
        }

        _events.Add(evt);

#if UNITY_EDITOR
        Debug.Log($"[Analytics] {eventName} | {FormatKV(evt)}");
#endif
    }

    // ─── Pre-Built Events ───────────────────────────────────────

    /// <summary>Call when a new game round starts.</summary>
    public static void GameStart()
    {
        _gameCount++;
        Log("game_start", "game_num", _gameCount);
    }

    /// <summary>Call when the player dies / game over.</summary>
    public static void GameOver(int score, string cause = "", int wave = 0, float duration = 0f)
    {
        Log("game_over",
            "score", score,
            "cause", cause,
            "wave", wave,
            "duration_sec", Mathf.RoundToInt(duration),
            "game_num", _gameCount);
    }

    /// <summary>Call when player reaches a milestone (score threshold, new wave, new level).</summary>
    public static void Milestone(string type, int value)
    {
        Log("milestone", "type", type, "value", value);
    }

    /// <summary>Call when player opens a screen / UI panel.</summary>
    public static void ScreenView(string screenName)
    {
        Log("screen_view", "screen", screenName);
    }

    /// <summary>Call when player taps a button.</summary>
    public static void ButtonTap(string buttonName)
    {
        Log("button_tap", "button", buttonName);
    }

    /// <summary>Call when an ad is shown.</summary>
    public static void AdShown(string adType, bool completed)
    {
        Log("ad_shown", "type", adType, "completed", completed);
    }

    /// <summary>Call when player makes a purchase (or taps IAP).</summary>
    public static void Purchase(string item, float price, bool completed)
    {
        Log("purchase", "item", item, "price", price, "completed", completed);
    }

    /// <summary>Call when player hits a difficulty wall (dies quickly, same spot repeatedly).</summary>
    public static void FrustrationEvent(string location, int attemptCount)
    {
        Log("frustration", "location", location, "attempts", attemptCount);
    }

    /// <summary>Call when player does something that indicates engagement (upgrades, retries immediately).</summary>
    public static void EngagementEvent(string action)
    {
        Log("engagement", "action", action);
    }

    // ─── Storage ────────────────────────────────────────────────

    /// <summary>
    /// Write all events to a JSON file on disk.
    /// In production, replace this with an HTTP POST to your backend.
    /// </summary>
    public static void FlushToDisk()
    {
        if (_events.Count == 0) return;

        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "analytics");
            Directory.CreateDirectory(dir);

            string filename = $"session_{_sessionId}_{DateTime.Now:yyyyMMdd_HHmmss}.json";
            string path = Path.Combine(dir, filename);

            // Simple JSON serialization (no external dependencies)
            string json = "[\n";
            for (int i = 0; i < _events.Count; i++)
            {
                json += "  {";
                int j = 0;
                foreach (var kv in _events[i])
                {
                    if (j > 0) json += ", ";
                    string val = kv.Value is string s ? $"\"{s}\"" :
                                 kv.Value is bool b ? (b ? "true" : "false") :
                                 kv.Value?.ToString() ?? "null";
                    json += $"\"{kv.Key}\": {val}";
                    j++;
                }
                json += "}" + (i < _events.Count - 1 ? ",\n" : "\n");
            }
            json += "]";

            File.WriteAllText(path, json);
            Debug.Log($"[Analytics] Flushed {_events.Count} events to {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Analytics] Failed to flush: {e.Message}");
        }
    }

    /// <summary>
    /// Read all analytics files from disk. Used by the analyst agent.
    /// </summary>
    public static string ReadAllSessions()
    {
        string dir = Path.Combine(Application.persistentDataPath, "analytics");
        if (!Directory.Exists(dir)) return "[]";

        string combined = "[\n";
        bool first = true;
        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            string content = File.ReadAllText(file);
            // Strip outer brackets and append
            content = content.Trim();
            if (content.StartsWith("[")) content = content.Substring(1);
            if (content.EndsWith("]")) content = content.Substring(0, content.Length - 1);
            content = content.Trim();
            if (!string.IsNullOrEmpty(content))
            {
                if (!first) combined += ",\n";
                combined += content;
                first = false;
            }
        }
        combined += "\n]";
        return combined;
    }

    // ─── Helpers ────────────────────────────────────────────────

    private static string FormatKV(Dictionary<string, object> d)
    {
        var parts = new List<string>();
        foreach (var kv in d)
        {
            if (kv.Key == "event" || kv.Key == "session") continue;
            parts.Add($"{kv.Key}={kv.Value}");
        }
        return string.Join(" ", parts);
    }
}
