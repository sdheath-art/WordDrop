using UnityEngine;
using System.IO;
using System.Collections.Generic;

namespace GameHarness
{
    /// <summary>
    /// Runtime test runner that captures screenshots and game state.
    /// Attaches itself automatically via [RuntimeInitializeOnLoadMethod].
    /// Only runs in batchmode.
    /// </summary>
    public class AutoPlayTest : MonoBehaviour
    {
        private static string _outputDir;
        private static List<string> _logEntries = new List<string>();
        private static int _screenshotIndex = 0;

        /// <summary>
        /// Auto-runs when Unity enters Play mode (works in batchmode).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoRun()
        {
            // Only run in batchmode (orchestrator test runs)
            if (!Application.isBatchMode)
                return;

            // Parse output dir from command line
            string[] args = System.Environment.GetCommandLineArgs();
            _outputDir = Path.Combine(Application.dataPath, "..", "TestOutput");
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "-outputDir" && i + 1 < args.Length)
                    _outputDir = args[i + 1];
            }
            Directory.CreateDirectory(_outputDir);

            Log("AutoPlayTest: Starting (runtime)");
            Log($"Screen: {Screen.width}x{Screen.height}");
            Log($"Output dir: {_outputDir}");

            // Listen for errors
            Application.logMessageReceived += OnLogMessage;

            // Create the runner
            var go = new GameObject("AutoPlayTestRunner");
            DontDestroyOnLoad(go);
            go.AddComponent<AutoPlayTest>();
        }

        private float _elapsed = 0f;
        private int _step = 0;
        private float _nextStepTime = 0f;
        private List<TestStep> _steps = new List<TestStep>();
        private bool _done = false;

        private struct TestStep
        {
            public float delay;
            public string description;
            public System.Action action;
        }

        void Start()
        {
            Log("TestRunner: Building test sequence");

            // Capture initial state
            AddStep(0.5f, "Initial capture", () =>
            {
                CaptureState("initial");
                LogSceneContents();
            });

            // Look for and tap Play/Start button
            AddStep(1.0f, "Find start button", () =>
            {
                TapButton("Play", "Start", "Begin", "Tap", "Go");
                CaptureScreenshot("after_start");
            });

            // Gameplay captures
            AddStep(2.0f, "Gameplay capture 1", () =>
            {
                CaptureScreenshot("gameplay_1");
                CaptureState("gameplay_1");
            });

            // Simulate touch in play area
            for (int i = 0; i < 5; i++)
            {
                int n = i;
                AddStep(0.5f, $"Tap {n}", () =>
                {
                    SimulateTap(
                        Screen.width * Random.Range(0.2f, 0.8f),
                        Screen.height * Random.Range(0.3f, 0.7f)
                    );
                });
            }

            AddStep(2.0f, "Gameplay capture 2", () =>
            {
                CaptureScreenshot("gameplay_2");
                CaptureState("gameplay_2");
            });

            for (int i = 0; i < 5; i++)
            {
                int n = i;
                AddStep(0.3f, $"Fast tap {n}", () =>
                {
                    SimulateTap(
                        Screen.width * Random.Range(0.1f, 0.9f),
                        Screen.height * Random.Range(0.2f, 0.8f)
                    );
                });
            }

            AddStep(2.0f, "Late capture", () =>
            {
                CaptureScreenshot("lategame");
                CaptureState("lategame");
            });

            // Try restart
            AddStep(1.0f, "Restart", () =>
            {
                TapButton("Retry", "Restart", "Again", "Menu", "Home", "Continue", "Play");
                CaptureScreenshot("after_retry");
            });

            AddStep(0.5f, "Done", () =>
            {
                CaptureState("final");
                Log("TestRunner: Complete");
                WriteResults();
                _done = true;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
                UnityEditor.EditorApplication.Exit(0);
#else
                Application.Quit();
#endif
            });

            if (_steps.Count > 0)
                _nextStepTime = _steps[0].delay;

            Log($"TestRunner: {_steps.Count} steps queued");
        }

        void Update()
        {
            if (_done) return;

            _elapsed += Time.deltaTime;

            if (_step < _steps.Count && _elapsed >= _nextStepTime)
            {
                var step = _steps[_step];
                Log($"Step {_step}: {step.description}");
                try { step.action?.Invoke(); }
                catch (System.Exception e) { Log($"Step {_step} ERROR: {e.Message}"); }

                _step++;
                if (_step < _steps.Count)
                    _nextStepTime = _elapsed + _steps[_step].delay;
            }

            // Safety timeout at 25s
            if (_elapsed > 25f && !_done)
            {
                Log($"Safety timeout at {_elapsed:F1}s (step {_step}/{_steps.Count})");
                CaptureScreenshot("safety_timeout");
                CaptureState("safety_timeout");
                WriteResults();
                _done = true;
#if UNITY_EDITOR
                UnityEditor.EditorApplication.isPlaying = false;
                UnityEditor.EditorApplication.Exit(0);
#else
                Application.Quit();
#endif
            }
        }

        private void AddStep(float delay, string description, System.Action action)
        {
            _steps.Add(new TestStep { delay = delay, description = description, action = action });
        }

        // ─── Utilities ──────────────────────────────────────────

        private void LogSceneContents()
        {
            var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            Log($"Root objects: {rootObjects.Length}");
            foreach (var obj in rootObjects)
            {
                var comps = obj.GetComponents<Component>();
                string compNames = "";
                foreach (var c in comps)
                    if (c != null && c.GetType().Namespace != "UnityEngine")
                        compNames += $"[{c.GetType().Name}] ";
                Log($"  {obj.name} active={obj.activeSelf} {compNames}");
            }

            var canvases = Object.FindObjectsOfType<Canvas>();
            Log($"Canvases: {canvases.Length}");

            var buttons = Object.FindObjectsOfType<UnityEngine.UI.Button>();
            Log($"Buttons: {buttons.Length}");
            foreach (var btn in buttons)
            {
                var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
                string label = txt != null ? txt.text : "(no text)";
                Log($"  Button: {btn.gameObject.name} label=\"{label}\" active={btn.gameObject.activeInHierarchy}");
            }

            var texts = Object.FindObjectsOfType<UnityEngine.UI.Text>();
            Log($"Text elements: {texts.Length}");
            foreach (var txt in texts)
                if (txt.gameObject.activeInHierarchy)
                    Log($"  Text: [{txt.gameObject.name}] \"{txt.text}\"");
        }

        private void TapButton(params string[] names)
        {
            var buttons = Object.FindObjectsOfType<UnityEngine.UI.Button>();
            foreach (var btn in buttons)
            {
                if (!btn.gameObject.activeInHierarchy || !btn.interactable) continue;
                string goName = btn.gameObject.name.ToLower();
                var txt = btn.GetComponentInChildren<UnityEngine.UI.Text>();
                string label = txt != null ? txt.text.ToLower() : "";

                foreach (var n in names)
                {
                    if (goName.Contains(n.ToLower()) || label.Contains(n.ToLower()))
                    {
                        Log($"Tapping: {btn.gameObject.name} (\"{label}\")");
                        btn.onClick.Invoke();
                        return;
                    }
                }
            }
            Log($"No button found for: {string.Join(", ", names)}");
        }

        private void SimulateTap(float x, float y)
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null) { Log($"Tap ({x:F0},{y:F0}): no EventSystem"); return; }

            var pointer = new UnityEngine.EventSystems.PointerEventData(eventSystem);
            pointer.position = new Vector2(x, y);

            var results = new List<UnityEngine.EventSystems.RaycastResult>();
            eventSystem.RaycastAll(pointer, results);

            foreach (var r in results)
            {
                var btn = r.gameObject.GetComponent<UnityEngine.UI.Button>();
                if (btn != null && btn.interactable)
                {
                    Log($"Tap hit: {btn.gameObject.name}");
                    btn.onClick.Invoke();
                    return;
                }
            }
        }

        public static void CaptureScreenshot(string label)
        {
            if (string.IsNullOrEmpty(_outputDir)) return;
            string filename = $"screenshot_{_screenshotIndex:D3}_{label}.png";
            ScreenCapture.CaptureScreenshot(Path.Combine(_outputDir, filename));
            Log($"Screenshot: {filename}");
            _screenshotIndex++;
        }

        public static void CaptureState(string label)
        {
            if (string.IsNullOrEmpty(_outputDir)) return;
            var state = new StateCapture
            {
                label = label,
                time = Time.time,
                frameCount = Time.frameCount,
                gameObjectCount = Object.FindObjectsOfType<GameObject>().Length
            };
            string path = Path.Combine(_outputDir, $"state_{label}.json");
            File.WriteAllText(path, JsonUtility.ToJson(state, true));
        }

        private static void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception)
                Log($"ERROR: {condition}");
            else if (type == LogType.Warning && !condition.Contains("Internal:"))
                Log($"WARNING: {condition}");
        }

        public static void Log(string msg)
        {
            string entry = $"[{Time.time:F2}] {msg}";
            _logEntries.Add(entry);
            Debug.Log($"[AutoPlayTest] {msg}");
        }

        private static void WriteResults()
        {
            if (string.IsNullOrEmpty(_outputDir)) return;

            File.WriteAllText(
                Path.Combine(_outputDir, "test_log.txt"),
                string.Join("\n", _logEntries)
            );

            int errors = 0, warnings = 0;
            foreach (var l in _logEntries)
            {
                if (l.Contains("ERROR:")) errors++;
                if (l.Contains("WARNING:")) warnings++;
            }

            File.WriteAllText(
                Path.Combine(_outputDir, "summary.txt"),
                $"Test Complete\nScreenshots: {_screenshotIndex}\nErrors: {errors}\nWarnings: {warnings}\nLog lines: {_logEntries.Count}"
            );

            Log($"Results written: {_screenshotIndex} screenshots, {errors} errors, {warnings} warnings");
        }

        [System.Serializable]
        private class StateCapture
        {
            public string label;
            public float time;
            public int frameCount;
            public int gameObjectCount;
        }
    }
}
