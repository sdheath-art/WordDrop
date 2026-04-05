using UnityEngine;
using UnityEngine.EventSystems;

namespace WordDrop
{
    /// <summary>
    /// Entry point. Creates the camera and all core manager objects.
    ///
    /// KEY ORDERING GUARANTEE:
    ///   All GameObjects are created in Awake(). Because all manager MonoBehaviours
    ///   also initialize in their own Awake() calls, by the time SceneBootstrap.Awake()
    ///   returns, every manager has its Instance set and its data structures ready.
    ///
    ///   SceneBootstrap.Start() then safely calls GameManager.TransitionTo(Playing).
    ///
    /// Creation order:
    ///   Camera → EventSystem → GameManager → ScoreManager → MatchManager (stub) →
    ///   RulesEngine → MatchController → AdManager → AIAgent →
    ///   GridManager → HandManager → HUDManager →
    ///   ColumnArrowManager → GameVisualBridge → MenuUI → GameOverUI
    ///
    /// NOTE: PrimedWordRegistry is a plain C# class owned internally by RulesEngine.
    ///       RoundManager, WordleEvaluator, RoundOverUI are NOT created — removed in Job 12.
    /// </summary>
    public class SceneBootstrap : MonoBehaviour
    {
        private Camera _mainCamera;

        private void Awake()
        {
            Debug.Log("[SceneBootstrap] Awake begin");

            // ── Mobile optimizations ──
            // Disable physics — no colliders, no rigidbodies
            Physics2D.simulationMode = SimulationMode2D.Script;
            Physics2D.autoSyncTransforms = false;
            Physics.autoSyncTransforms = false;
#if UNITY_IOS || UNITY_ANDROID
            // Disable accelerometer polling — not used in a puzzle game
            Input.accelerometerFrequency = 0;
#endif

            SetupCamera();
            SetupEventSystem();
            SetupManagers();
            SetupGrid();
            SetupHand();
            SetupHUD();
            SetupColumnArrows();
            SetupVisualBridge();
            SetupUIScreens();

            Debug.Log("[SceneBootstrap] Awake complete — all managers created and initialized");
        }

        private void Start()
        {
            // Show menu instead of auto-starting — let the player choose when to play
            if (MenuUI.Instance != null)
            {
                MenuUI.Instance.SetVisible(true);
                Debug.Log("[SceneBootstrap] Start — showing menu");
            }
            else if (GameManager.Instance != null)
            {
                // Fallback if MenuUI doesn't exist
                GameManager.Instance.TransitionTo(GameState.Playing);
            }
        }

        // -----------------------------------------------------------------------
        // Camera
        // -----------------------------------------------------------------------

        private void SetupCamera()
        {
            GameObject camGO = new GameObject("MainCamera");
            _mainCamera = camGO.AddComponent<Camera>();
            _mainCamera.tag              = "MainCamera";
            _mainCamera.orthographic     = true;
            _mainCamera.orthographicSize = 10f;
            _mainCamera.clearFlags       = CameraClearFlags.SolidColor;
            _mainCamera.backgroundColor  = Color.white;
            _mainCamera.nearClipPlane    = -10f;
            _mainCamera.farClipPlane     =  10f;
            _mainCamera.transform.position = new Vector3(0f, 0f, -5f);

            // AudioListener required for any audio playback
            camGO.AddComponent<AudioListener>();

            // Enable post-processing on URP camera (bloom, vignette)
            var camData = camGO.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (camData == null) camData = camGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;

            float halfH = _mainCamera.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);

            // Vertical gradient background
            Color bgTop    = new Color(0.220f, 0.370f, 0.920f, 1f); // slightly deeper blue
            Color bgBottom = new Color(0.580f, 0.060f, 0.820f, 1f); // richer purple
            Sprite bgGrad = TileRenderer.CreateGradientRect(4, 128, bgBottom, bgTop);
            GameObject bgGO = new GameObject("BackgroundGradient");
            // NOT parented to camera — stays still while camera shakes
            SpriteRenderer bgSR = bgGO.AddComponent<SpriteRenderer>();
            bgSR.sprite = bgGrad;
            bgSR.sortingOrder = -10;
            float bgNativeW = 4f / 100f;
            float bgNativeH = 128f / 100f;
            // 3x screen size — camera shake can't reveal the edges
            bgGO.transform.localScale = new Vector3(halfW * 4f / bgNativeW, halfH * 4f / bgNativeH, 1f);
            bgGO.transform.position = new Vector3(0f, 0f, 4f); // in front of far clip, behind everything

            // Old sprite vignette removed — URP post-processing handles vignette now

            Debug.Log($"[SceneBootstrap] Camera — halfH={halfH:F2}, halfW={halfW:F2}  " +
                      $"({Screen.width}x{Screen.height})");
        }

        // -----------------------------------------------------------------------
        // Event System — MUST be created before any Canvas/UI
        // -----------------------------------------------------------------------

        private void SetupEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
                Debug.Log("[SceneBootstrap] EventSystem already exists — skipping creation.");
                return;
            }

            GameObject esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<StandaloneInputModule>();
            Debug.Log("[SceneBootstrap] EventSystem created");
        }

        // -----------------------------------------------------------------------
        // Core managers
        // -----------------------------------------------------------------------

        private void SetupManagers()
        {
            // GameManager must come first — other managers may reference it
            new GameObject("GameManager").AddComponent<GameManager>();

            // ScoreManager tracks P1 and AI scores
            new GameObject("ScoreManager").AddComponent<ScoreManager>();

            // MatchManager kept as legacy stub for backward compat with GameOverUI
            new GameObject("MatchManager").AddComponent<MatchManager>();

            // RulesEngine must exist before MatchController.
            // PrimedWordRegistry is a plain C# class owned INTERNALLY by RulesEngine —
            // no separate GameObject needed.
            new GameObject("RulesEngine").AddComponent<RulesEngine>();

            // MatchController — new turn-flow owner, depends on RulesEngine
            new GameObject("MatchController").AddComponent<MatchController>();

            // AdManager stub
            new GameObject("AdManager").AddComponent<AdManager>();

            // AIAgent — depends on MatchController + RulesEngine
            new GameObject("AIAgent").AddComponent<AIAgent>();

            Debug.Log("[SceneBootstrap] Core managers created: " +
                      "GameManager, ScoreManager, MatchManager(stub), RulesEngine, " +
                      "MatchController, AdManager, AIAgent");
        }

        private void SetupGrid()
        {
            new GameObject("GridManager").AddComponent<GridManager>();
            Debug.Log("[SceneBootstrap] GridManager created");
        }

        private void SetupHand()
        {
            new GameObject("HandManager").AddComponent<HandManager>();
            Debug.Log("[SceneBootstrap] HandManager created");
        }

        private void SetupHUD()
        {
            new GameObject("HUDManager").AddComponent<HUDManager>();
            Debug.Log("[SceneBootstrap] HUDManager created");
        }

        private void SetupColumnArrows()
        {
            new GameObject("ColumnArrowManager").AddComponent<ColumnArrowManager>();
            Debug.Log("[SceneBootstrap] ColumnArrowManager created");
        }

        private void SetupVisualBridge()
        {
            // GameVisualBridge subscribes to RulesEngine + MatchController events in Start().
            // Must be created after RulesEngine and MatchController exist.
            new GameObject("GameVisualBridge").AddComponent<GameVisualBridge>();
            Debug.Log("[SceneBootstrap] GameVisualBridge created");
        }

        private void SetupUIScreens()
        {
            // MenuUI and GameOverUI both build their panels in Awake() and hide them.
            // SceneBootstrap.Start() transitions directly to Playing, bypassing the menu.
            new GameObject("MenuUI").AddComponent<MenuUI>();
            new GameObject("GameOverUI").AddComponent<GameOverUI>();
            new GameObject("DropPreview").AddComponent<DropPreview>();
            new GameObject("BonusPopup").AddComponent<BonusPopup>();
            new GameObject("ChainCounter").AddComponent<ChainCounter>();
            new GameObject("MeltdownManager").AddComponent<MeltdownManager>();
            new GameObject("DetonationRecorder").AddComponent<DetonationRecorder>();
            new GameObject("DetonationReplay").AddComponent<DetonationReplay>();
            new GameObject("BlitzManager").AddComponent<BlitzManager>();
            new GameObject("RisingRowManager").AddComponent<RisingRowManager>();
            new GameObject("ScreenTransition").AddComponent<ScreenTransition>();
            new GameObject("LastWordDisplay").AddComponent<LastWordDisplay>();

            // NOTE: RoundOverUI, RoundManager, WordleEvaluator are NOT created.
            // The Scrabble-drop game does not use round-based flow.
            // RulesEngine handles all word detection.

            Debug.Log("[SceneBootstrap] UI screens created (MenuUI hidden, GameOverUI hidden). " +
                      "No RoundManager / WordleEvaluator / RoundOverUI — removed in Job 12.");
        }
    }
}
