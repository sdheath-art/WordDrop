using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Dev-only on-screen menu. L key toggles visibility. Hidden by default.
    /// Debug key convention (letters only — macOS eats F-keys): N = NoAssist
    /// (SurvivalManager), T = state dump (GameVisualBridge), M = GameMode stub
    /// (SceneBootstrap), L = this menu.
    ///
    /// Phase 1: persistence + loader sanity (CoinWallet, HeartsManager, etc.).
    /// Phase 2: "Play Test Level" button wires the full vertical slice — sets
    /// GameMode=Level, loads and validates JSON, calls LevelController.StartLevel,
    /// transitions to Playing. On Complete/Fail, logs and returns to Menu.
    ///
    /// Compiled out of release builds.
    /// </summary>
#if UNITY_EDITOR || DEVELOPMENT_BUILD
    public class LevelDebugMenu : MonoBehaviour
    {
        private bool _visible = false;
        private int _forceLevelId = 1;
        private int _forceStreak = 1;
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

        private void Start()
        {
            // Subscribe once to LevelController events. Safe because LevelController
            // is created in SceneBootstrap alongside the other managers.
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnLevelComplete += OnLevelComplete;
                LevelController.Instance.OnLevelFail     += OnLevelFail;
            }
            else
            {
                Debug.LogWarning("[LevelDebugMenu] LevelController.Instance null in Start — events not wired.");
            }
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnLevelComplete -= OnLevelComplete;
                LevelController.Instance.OnLevelFail     -= OnLevelFail;
            }
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
            const float H = 720f;
            GUILayout.BeginArea(new Rect(20f, 20f, W, H), GUI.skin.box);
            GUILayout.Label("<b>Level Debug Menu</b>", RichLabelStyle());

            GUILayout.Space(4f);
            GUILayout.Label($"Mode: {GameManager.CurrentMode}    Coins: {CoinWallet.Balance}    Hearts: {HeartsManager.Current}/{HeartsManager.MAX_HEARTS}");
            GUILayout.Label($"Highest unlocked: L{LevelProgressManager.GetHighestUnlocked()}");

            if (LevelController.Instance != null && LevelController.Instance.IsActive)
            {
                var lc = LevelController.Instance;
                GUILayout.Label($"<b>Level {lc.CurrentLevel.levelId}</b>  score={lc.CurrentScore}/{lc.CurrentLevel.target}  moves={lc.MovesRemaining}", RichLabelStyle());
            }

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
                TutorialProgression.ResetTutorialState();
                StarterPackModal.ResetState();
                DailyDropManager.ResetAll();
                PlayerPrefs.DeleteKey("wd_hearts_last_ad_grant_ticks");
                PlayerPrefs.Save();
                _lastStatus = "All progress, coins, hearts, tutorial, starter-pack, daily, and ad-cooldown reset.";
            }

            if (GUILayout.Button("Show Starter Pack"))
            {
                StarterPackModal.ResetState();
                if (StarterPackModal.Instance != null)
                    StarterPackModal.Instance.SetVisible(true);
                _lastStatus = "Starter Pack shown (persistence flags cleared).";
            }

            // ── Daily debug (Phase 8) ──
            GUILayout.Space(4f);
            GUILayout.Label($"<b>Daily</b>  streak={DailyDropManager.GetStreak()} playedToday={DailyDropManager.HasPlayedToday()} canSaveStreak={DailyDropManager.CanSaveStreak()} lvl→{DailyDropManager.GetDailyLevelId()}", RichLabelStyle());

            if (GUILayout.Button("Reset daily (replay today)"))
            {
                DailyDropManager.ResetDaily();
                _lastStatus = "Daily state reset — can replay today's puzzle.";
            }

            if (GUILayout.Button("Simulate: played yesterday (streak continues)"))
            {
                DailyDropManager.DebugBackdateLastPlayed(1);
                _lastStatus = "Backdated last-played to yesterday — next play increments streak.";
            }

            if (GUILayout.Button("Simulate: missed 2 days (triggers save-streak)"))
            {
                DailyDropManager.DebugBackdateLastPlayed(2);
                int currentStreak = PlayerPrefs.GetInt("daily_streak", 0);
                if (currentStreak == 0) DailyDropManager.DebugSetStreak(5);
                _lastStatus = "Backdated 2 days + forced streak=5 if empty. CanSaveStreak should now be true.";
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Set streak", GUILayout.Width(80f));
            string streakText = GUILayout.TextField(_forceStreak.ToString(), GUILayout.Width(60f));
            if (int.TryParse(streakText, out int streakParsed)) _forceStreak = Mathf.Max(0, streakParsed);
            if (GUILayout.Button("Apply", GUILayout.Width(80f)))
            {
                DailyDropManager.DebugSetStreak(_forceStreak);
                _lastStatus = $"Streak forced to {_forceStreak}.";
            }
            GUILayout.EndHorizontal();

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

            if (GUILayout.Button($"▶ Play Test Level {_forceLevelId}"))
            {
                StartLevelPlay(_forceLevelId);
            }

            if (GUILayout.Button("📋 Open Level Select"))
            {
                if (LevelSelectScreen.Instance != null)
                {
                    LevelSelectScreen.Instance.SetVisible(true);
                    _visible = false; // hide the debug menu so the screen is unobstructed
                    _lastStatus = "Opened Level Select.";
                }
                else
                {
                    _lastStatus = "LevelSelectScreen.Instance is null.";
                }
            }

            GUILayout.Space(8f);
            GUILayout.Label("Status:");
            GUILayout.Label(_lastStatus, GUI.skin.textArea, GUILayout.Height(60f));

            GUILayout.EndArea();

            GUI.matrix = prevMatrix;
        }

        // ── Play Test Level flow ────────────────────────────────────────────────

        private void StartLevelPlay(int levelId)
        {
            LevelData data = LevelLoader.Load(levelId);
            if (data == null)
            {
                _lastStatus = $"Load failed for level {levelId}.";
                return;
            }

            var (ok, reason) = LevelValidator.Validate(data);
            if (!ok)
            {
                _lastStatus = $"Level {levelId} INVALID — {reason}";
                Debug.LogError($"[LevelDebugMenu] {_lastStatus}");
                return;
            }

            // Clear competing mode flags so MatchController's mutual-exclusion
            // check passes and solo-mode checks resolve cleanly via IsLevelMode.
            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;

            GameManager.CurrentMode = GameMode.Level;

            // Initialize LevelController BEFORE MatchController.StartMatch runs.
            if (LevelController.Instance == null)
            {
                _lastStatus = "LevelController.Instance is null — cannot start.";
                Debug.LogError($"[LevelDebugMenu] {_lastStatus}");
                return;
            }

            LevelController.Instance.StartLevel(data);

            // TransitionTo(Playing) calls MatchController.StartMatch(). The edits
            // made for Phase 2 route solo-mode plumbing through IsLevelMode and
            // skip Survival's specific init.
            if (GameManager.Instance != null)
            {
                GameManager.Instance.TransitionTo(GameState.Playing);
                _lastStatus = $"▶ Playing level {data.levelId} '{data.displayName}'";
                Debug.Log($"[LevelDebugMenu] {_lastStatus}");
            }
        }

        private void OnLevelComplete(int score, int stars)
        {
            // Phase 4+: LevelCompletedModal owns the post-complete flow. Debug
            // menu only logs the status line for visibility in the debug menu.
            _lastStatus = $"✓ COMPLETE — score={score}, {stars}★";
            Debug.Log($"[LevelDebugMenu] {_lastStatus}");
        }

        private void OnLevelFail(int score, int shortfall)
        {
            // Phase 4+: OutOfMovesModal owns the post-fail flow.
            _lastStatus = $"✗ FAIL — score={score}, shortfall={shortfall}";
            Debug.Log($"[LevelDebugMenu] {_lastStatus}");
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
