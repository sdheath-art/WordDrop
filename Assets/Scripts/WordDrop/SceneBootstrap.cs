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

        // 2026-06-08 Spencer: TEMP on-screen bloom/HDR diagnostic for the iOS build (can't
        // read device logs from the dev box, so put the state on screen). Flip to false /
        // delete once the no-glow-on-mobile cause is found.
        private const bool SHOW_BLOOM_DEBUG = false; // on-device bloom/HDR diagnostic — bloom resolved 2026-06-19, left wired for future use

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Phase 0 of Survival→Level migration: placeholder until GameMode enum lands in Phase 2.
        private bool _debugIsLevelMode;
#endif

        private void Awake()
        {
//             Debug.Log("[SceneBootstrap] Awake begin");

            // ── Mobile optimizations ──
            // Disable physics — no colliders, no rigidbodies
            Physics2D.simulationMode = SimulationMode2D.Script;
            Physics2D.autoSyncTransforms = false;
            Physics.autoSyncTransforms = false;
            // Accelerometer disabled via InputSystem settings (not needed for puzzle game)

            Application.targetFrameRate = 60;

#if UNITY_IOS && !UNITY_EDITOR
            // Override iOS silent switch — game audio should always play
            UnityEngine.iOS.Device.SetNoBackupFlag(Application.persistentDataPath);
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

//             Debug.Log("[SceneBootstrap] Awake complete — all managers created and initialized");
        }

        private void Start()
        {
#if WORDROP_PLAYTEST
            // PLAYTEST-ONLY coin grant. Gated behind the custom WORDROP_PLAYTEST scripting define
            // (Player Settings → Scripting Define Symbols) — it must NOT be set for the real launch
            // build, or every player would start rich and the economy breaks. Tops up to a floor each
            // launch so testers always have plenty to exercise the Continue / buy flows, while still
            // letting them spend down within a session. 2026-06-24 Spencer.
            const int PLAYTEST_COIN_FLOOR = 9999;
            if (CoinWallet.Balance < PLAYTEST_COIN_FLOOR)
                CoinWallet.Add(PLAYTEST_COIN_FLOOR - CoinWallet.Balance);
#endif

            // Show menu instead of auto-starting — let the player choose when to play
            if (MenuUI.Instance != null)
            {
                MenuUI.Instance.SetVisible(true);
//                 Debug.Log("[SceneBootstrap] Start — showing menu");
            }
            else if (GameManager.Instance != null)
            {
                // Fallback if MenuUI doesn't exist
                GameManager.Instance.TransitionTo(GameState.Playing);
            }
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // M = "Mode". F-keys are intercepted by macOS (Mission Control) before
        // reaching Unity unless the user enables "Use F1, F2 as standard keys",
        // so letter keys are safer for debug toggles. N and T are already taken
        // (SurvivalManager NoAssist, GameVisualBridge state dump).
        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.M))
            {
                _debugIsLevelMode = !_debugIsLevelMode;
                Debug.Log($"[SceneBootstrap] GameMode toggle → {(_debugIsLevelMode ? "Level" : "Survival")} (stub — Phase 2 wires this to actual mode routing)");
            }
        }
#endif

        // -----------------------------------------------------------------------
        // Camera
        // -----------------------------------------------------------------------

        private void SetupCamera()
        {
            GameObject camGO = new GameObject("MainCamera");
            _mainCamera = camGO.AddComponent<Camera>();
            _mainCamera.tag              = "MainCamera";
            // 2026-06-05 Spencer: explicitly enable HDR on the camera. Bloom only catches
            // values >1.0 (threshold 1.30), which requires an HDR render target. On iOS
            // builds the glow was completely absent; forcing allowHDR here rules out the
            // camera rendering to an LDR buffer (the other classic "no bloom on device" cause,
            // alongside the post-FX shader stripping fix in UniversalRenderPipelineGlobalSettings).
            _mainCamera.allowHDR         = true;
            _mainCamera.orthographic     = true;
            _mainCamera.orthographicSize = 10f;
            _mainCamera.clearFlags       = CameraClearFlags.SolidColor;
            _mainCamera.backgroundColor  = Color.white;
            _mainCamera.nearClipPlane    = -10f;
            _mainCamera.farClipPlane     =  10f;
            _mainCamera.transform.position = new Vector3(0f, 0f, -5f);

            // AudioListener required for any audio playback
            camGO.AddComponent<AudioListener>();

            // Post-processing RE-ENABLED (April 17) — bloom is needed for BigBurstFlash
            // and HDR primed glows to catch properly. Prior concern was that tonemapping
            // + color grading dimmed the scene; if that recurs, inspect the Volume profile
            // under /Assets/Settings/ and disable those overrides rather than the whole pipeline.
            var camData = camGO.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            if (camData == null) camData = camGO.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            camData.renderPostProcessing = true;

            float halfH = _mainCamera.orthographicSize;
            float halfW = halfH * ((float)Screen.width / Screen.height);

            // Vertical gradient background.
            // 2026-06-02: candy-bright CYAN per Spencer's Royal-Match/Candy-Crush
            // reference — vivid sky-cyan at top → mint-teal toward the board.
            Color bgTop    = new Color(0.40f, 0.82f, 0.96f, 1f); // vivid sky cyan (top)
            Color bgBottom = new Color(0.50f, 0.90f, 0.84f, 1f); // bright mint-teal (toward the board)
            Sprite bgGrad = TileRenderer.CreateGradientRect(4, 128, bgBottom, bgTop);
            GameObject bgGO = new GameObject("BackgroundGradient");
            // NOT parented to camera — stays still while camera shakes
            SpriteRenderer bgSR = bgGO.AddComponent<SpriteRenderer>();
            bgSR.sprite = bgGrad;
            bgSR.sortingOrder = -10;
            // Darken-tint removed. The 0.7735 multiplier was dimming the
            // gradient 23% so the dark-blue background didn't overpower the
            // HUD. With a warm/light gradient we WANT the brightness — the
            // happy-wrapper feel depends on the background being the
            // brightest layer on screen.
            bgSR.color = Color.white;
            float bgNativeW = 4f / 100f;
            float bgNativeH = 128f / 100f;
            // 3x screen size — camera shake can't reveal the edges
            bgGO.transform.localScale = new Vector3(halfW * 4f / bgNativeW, halfH * 4f / bgNativeH, 1f);
            bgGO.transform.position = new Vector3(0f, 0f, 4f); // in front of far clip, behind everything

            // Old sprite vignette removed — URP post-processing handles vignette now

//             Debug.Log($"[SceneBootstrap] Camera — halfH={halfH:F2}, halfW={halfW:F2}  " +
                      // $"({Screen.width}x{Screen.height})");
        }

        // -----------------------------------------------------------------------
        // Event System — MUST be created before any Canvas/UI
        // -----------------------------------------------------------------------

        private void SetupEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
//                 Debug.Log("[SceneBootstrap] EventSystem already exists — skipping creation.");
                return;
            }

            GameObject esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<StandaloneInputModule>();
//             Debug.Log("[SceneBootstrap] EventSystem created");
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

//             Debug.Log("[SceneBootstrap] Core managers created: " +
                      // "GameManager, ScoreManager, MatchManager(stub), RulesEngine, " +
                      // "MatchController, AdManager, AIAgent");
        }

        private void SetupGrid()
        {
            new GameObject("GridManager").AddComponent<GridManager>();
//             Debug.Log("[SceneBootstrap] GridManager created");
        }

        private void SetupHand()
        {
            new GameObject("HandManager").AddComponent<HandManager>();
//             Debug.Log("[SceneBootstrap] HandManager created");
        }

        private void SetupHUD()
        {
            new GameObject("HUDManager").AddComponent<HUDManager>();
//             Debug.Log("[SceneBootstrap] HUDManager created");
        }

        private void SetupColumnArrows()
        {
            new GameObject("ColumnArrowManager").AddComponent<ColumnArrowManager>();
//             Debug.Log("[SceneBootstrap] ColumnArrowManager created");
        }

        private void SetupVisualBridge()
        {
            // GameVisualBridge subscribes to RulesEngine + MatchController events in Start().
            // Must be created after RulesEngine and MatchController exist.
            new GameObject("GameVisualBridge").AddComponent<GameVisualBridge>();
//             Debug.Log("[SceneBootstrap] GameVisualBridge created");
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
            new GameObject("SurvivalManager").AddComponent<SurvivalManager>();
            new GameObject("LevelController").AddComponent<LevelController>();
            new GameObject("LevelSelectScreen").AddComponent<LevelSelectScreen>();
            new GameObject("LevelCompletedModal").AddComponent<LevelCompletedModal>();
            new GameObject("StageClearModal").AddComponent<StageClearModal>();
            new GameObject("LevelIntroModal").AddComponent<LevelIntroModal>();
            new GameObject("TopOutPanel").AddComponent<TopOutPanel>();
            new GameObject("BoosterManager").AddComponent<BoosterManager>();
            new GameObject("BoosterHUDSlot").AddComponent<BoosterHUDSlot>();
            new GameObject("BoosterChoiceModal").AddComponent<BoosterChoiceModal>();
            new GameObject("ContinueModal").AddComponent<ContinueModal>();
            new GameObject("OutOfMovesModal").AddComponent<OutOfMovesModal>();
            new GameObject("HeartWaitModal").AddComponent<HeartWaitModal>();
            new GameObject("StarterPackModal").AddComponent<StarterPackModal>();
            new GameObject("DailyAlreadyPlayedModal").AddComponent<DailyAlreadyPlayedModal>();
            new GameObject("SaveStreakModal").AddComponent<SaveStreakModal>();
            new GameObject("LevelTutorialOverlay").AddComponent<LevelTutorialOverlay>();
            new GameObject("TutorialVisualCues").AddComponent<TutorialVisualCues>();
            new GameObject("BonusMode").AddComponent<BonusMode>();
            new GameObject("ChainMeter").AddComponent<ChainMeter>();
            new GameObject("BonusHUD").AddComponent<BonusHUD>();
            new GameObject("JamHint").AddComponent<JamHint>();
            new GameObject("RisingRowManager").AddComponent<RisingRowManager>();
            new GameObject("ScreenTransition").AddComponent<ScreenTransition>();
            new GameObject("LastWordDisplay").AddComponent<LastWordDisplay>();

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            new GameObject("LevelDebugMenu").AddComponent<LevelDebugMenu>();
#endif

            // NOTE: RoundOverUI, RoundManager, WordleEvaluator are NOT created.
            // The Scrabble-drop game does not use round-based flow.
            // RulesEngine handles all word detection.

//             Debug.Log("[SceneBootstrap] UI screens created (MenuUI hidden, GameOverUI hidden). " +
                      // "No RoundManager / WordleEvaluator / RoundOverUI — removed in Job 12.");
        }

        // 2026-06-08 Spencer: on-device bloom/HDR diagnostic. Reads the ACTUAL render
        // state on the phone so we can see why bloom is absent. Read it off the screen.
        private void OnGUI()
        {
            if (!SHOW_BLOOM_DEBUG) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 30, normal = { textColor = Color.yellow } };
            float y = 60f;
            void L(string s) { GUI.Label(new Rect(16f, y, 3000f, 40f), s, style); y += 40f; }

            L($"GFX: {SystemInfo.graphicsDeviceType}");
            L($"HDR DefaultHDR supported: {SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.DefaultHDR)}");
            L($"HDR RGB111110Float: {SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.RGB111110Float)}");
            L($"HDR ARGBHalf: {SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBHalf)}");
            L($"cam.allowHDR: {(_mainCamera != null ? _mainCamera.allowHDR.ToString() : "NO CAM")}");

            var camData = _mainCamera != null
                ? _mainCamera.GetComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>() : null;
            L($"cam postFX: {(camData != null ? camData.renderPostProcessing.ToString() : "no camData")}");

            int q = QualitySettings.GetQualityLevel();
            L($"Quality: {q} ({(QualitySettings.names != null && q < QualitySettings.names.Length ? QualitySettings.names[q] : "?")})");
            var rp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            L($"Pipeline: {(rp != null ? rp.name : "NULL (built-in!)")}");

            var vol = Object.FindFirstObjectByType<UnityEngine.Rendering.Volume>();
            L($"Volume found: {(vol != null)}  profile: {(vol != null && vol.profile != null)}");
            if (vol != null && vol.profile != null)
            {
                if (vol.profile.TryGet<UnityEngine.Rendering.Universal.Bloom>(out var b))
                    L($"Bloom: active={b.active} intensity={b.intensity.value} threshold={b.threshold.value}");
                else
                    L("Bloom: NOT in profile!");
            }
        }
    }
}
