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
            Debug.Log("[SceneBootstrap] Start — transitioning directly to Playing state");

            if (GameManager.Instance != null)
            {
                GameManager.Instance.TransitionTo(GameState.Playing);
            }
            else
            {
                Debug.LogError("[SceneBootstrap] GameManager.Instance is null in Start!");
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

            // Subtle vignette overlay — darkens edges for depth
            int vigW = 64, vigH = 64;
            Texture2D vigTex = new Texture2D(vigW, vigH, TextureFormat.RGBA32, false);
            vigTex.filterMode = FilterMode.Bilinear;
            Color[] vigPx = new Color[vigW * vigH];
            for (int vy = 0; vy < vigH; vy++)
            {
                for (int vx = 0; vx < vigW; vx++)
                {
                    float nx = (vx / (float)(vigW - 1)) * 2f - 1f;
                    float ny = (vy / (float)(vigH - 1)) * 2f - 1f;
                    float dist = Mathf.Sqrt(nx * nx + ny * ny);
                    float vig = Mathf.Clamp01((dist - 0.6f) / 1.0f) * 0.20f; // was 0.5/0.8/0.35 — softer
                    vigPx[vy * vigW + vx] = new Color(0f, 0f, 0f, vig);
                }
            }
            vigTex.SetPixels(vigPx);
            vigTex.Apply();
            Sprite vigSprite = Sprite.Create(vigTex, new Rect(0, 0, vigW, vigH), new Vector2(0.5f, 0.5f), 100f);
            GameObject vigGO = new GameObject("Vignette");
            SpriteRenderer vigSR = vigGO.AddComponent<SpriteRenderer>();
            vigSR.sprite = vigSprite;
            vigSR.sortingOrder = -9;
            float vigNW = vigW / 100f;
            float vigNH = vigH / 100f;
            vigGO.transform.localScale = new Vector3(halfW * 2.2f / vigNW, halfH * 2.2f / vigNH, 1f);
            vigGO.transform.position = new Vector3(0f, 0f, 4.5f);

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

            // NOTE: RoundOverUI, RoundManager, WordleEvaluator are NOT created.
            // The Scrabble-drop game does not use round-based flow.
            // RulesEngine handles all word detection.

            Debug.Log("[SceneBootstrap] UI screens created (MenuUI hidden, GameOverUI hidden). " +
                      "No RoundManager / WordleEvaluator / RoundOverUI — removed in Job 12.");
        }
    }
}
