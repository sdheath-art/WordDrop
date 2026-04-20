using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Dev-only on-screen menu for exercising Phase 1 persistence + loader.
    /// L key toggles visibility. Hidden by default.
    /// Debug key convention (letters only — macOS eats F-keys): N = NoAssist
    /// (SurvivalManager), T = state dump (GameVisualBridge), M = GameMode stub
    /// (SceneBootstrap), L = this menu.
    ///
    /// Phase 1 scope: verify LevelLoader.Load, LevelValidator.Validate,
    /// CoinWallet, HeartsManager, LevelProgressManager across an app restart.
    /// Phase 2 will add "Play Test Level" routing once LevelController exists.
    ///
    /// Compiled out of release builds.
    /// </summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public class LevelDebugMenu : MonoBehaviour
    {
        private bool _visible = false;
        private int _forceLevelId = 1;
        private string _lastStatus = "(no action yet)";

        private static LevelDebugMenu _instance;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.L))
            {
                _visible = !_visible;
            }
        }

        private void OnGUI()
        {
            if (!_visible) return;

            // Unity OnGUI uses raw device pixels — tiny on Retina. Scale the matrix
            // so the menu renders at a consistent physical size across resolutions.
            const float DESIGN_HEIGHT = 800f;
            float scale = Mathf.Max(1f, Screen.height / DESIGN_HEIGHT);
            Matrix4x4 prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            const float W = 360f;
            const float H = 440f;
            GUILayout.BeginArea(new Rect(20f, 20f, W, H), GUI.skin.box);
            GUILayout.Label("<b>Level Debug Menu</b> (Phase 1)", RichLabelStyle());

            GUILayout.Space(4f);
            GUILayout.Label($"Coins: {CoinWallet.Balance}    Hearts: {HeartsManager.Current}/{HeartsManager.MAX_HEARTS}");
            GUILayout.Label($"Highest unlocked: L{LevelProgressManager.GetHighestUnlocked()}");

            GUILayout.Space(6f);
            if (GUILayout.Button("Grant 1000 coins"))
            {
                CoinWallet.Add(1000);
                _lastStatus = $"Granted 1000 coins (balance now {CoinWallet.Balance}).";
            }

            if (GUILayout.Button("Grant full hearts"))
            {
                HeartsManager.GrantFull();
                _lastStatus = $"Hearts restored to {HeartsManager.Current}/{HeartsManager.MAX_HEARTS}.";
            }

            if (GUILayout.Button("Consume 1 heart"))
            {
                bool ok = HeartsManager.Consume();
                _lastStatus = ok
                    ? $"Heart consumed (now {HeartsManager.Current}/{HeartsManager.MAX_HEARTS})."
                    : "No hearts to consume.";
            }

            if (GUILayout.Button("Reset all progress"))
            {
                LevelProgressManager.ResetAll();
                CoinWallet.ResetAll();
                HeartsManager.ResetAll();
                _lastStatus = "All progress, coins, and hearts reset.";
            }

            GUILayout.Space(6f);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Force level", GUILayout.Width(80f));
            string idText = GUILayout.TextField(_forceLevelId.ToString(), GUILayout.Width(60f));
            if (int.TryParse(idText, out int parsed)) _forceLevelId = Mathf.Max(1, parsed);
            GUILayout.EndHorizontal();

            if (GUILayout.Button($"Load + Validate level_{_forceLevelId}.json"))
            {
                LevelData data = LevelLoader.Load(_forceLevelId);
                if (data == null)
                {
                    _lastStatus = $"Load failed for level {_forceLevelId} (see console).";
                }
                else
                {
                    var (ok, reason) = LevelValidator.Validate(data);
                    _lastStatus = ok
                        ? $"Level {_forceLevelId} OK — target={data.target}, moves={data.moveBudget}, name='{data.displayName}'"
                        : $"Level {_forceLevelId} INVALID — {reason}";
                    Debug.Log($"[LevelDebugMenu] {_lastStatus}");
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("Status:");
            GUILayout.Label(_lastStatus, GUI.skin.textArea, GUILayout.Height(60f));

            GUILayout.EndArea();

            GUI.matrix = prevMatrix;
        }

        private static GUIStyle _richLabel;
        private static GUIStyle RichLabelStyle()
        {
            if (_richLabel == null)
            {
                _richLabel = new GUIStyle(GUI.skin.label) { richText = true };
            }
            return _richLabel;
        }
    }
#endif
}
