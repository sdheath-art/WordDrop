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
        // ── BACKGROUND OPTIONS (2026-11-03, Spencer: "might as well have some background options"). The in-level room art; the
        // choice is saved per device (PlayerPrefs) and the FX test menu cycles it live. Both arts are 2358x5110 @ 100 ppu, so the
        // cover fit is identical. 0 = the reading room (bg@2x), 1 = the observatory (bg_observatory@2x, ChatGPT, calm centre). ──
        public static readonly string[] BACKGROUNDS = { "UI/bg@2x" };          // 11-03: observatory removed (Spencer: lanterns peeking out) - art kept in WordDrop_Art/room_1103
        public static readonly string[] BACKGROUND_NAMES = { "Reading room" };
        private const string BG_PREF = "wd_bg_choice";
        public static int BackgroundIndex
        {
            get { return Mathf.Clamp(PlayerPrefs.GetInt(BG_PREF, 0), 0, BACKGROUNDS.Length - 1); }
            set { PlayerPrefs.SetInt(BG_PREF, Mathf.Clamp(value, 0, BACKGROUNDS.Length - 1)); PlayerPrefs.Save(); }
        }
        public static string BackgroundPath => BACKGROUNDS[BackgroundIndex];
        /// <summary>FX menu: switch to the next background now (swaps the live ScreenBackground sprite).</summary>
        public static void CycleBackground()
        {
            BackgroundIndex = (BackgroundIndex + 1) % BACKGROUNDS.Length;
            var go = GameObject.Find("ScreenBackground"); var sr = go != null ? go.GetComponent<SpriteRenderer>() : null;
            var sp = Resources.Load<Sprite>(BackgroundPath);
            if (sr != null && sp != null) sr.sprite = sp;
            WDLog.Log($"[Background] now '{BACKGROUND_NAMES[BackgroundIndex]}' ({BackgroundPath})");
        }
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
//             WDLog.Log("[SceneBootstrap] Awake begin");

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

//             WDLog.Log("[SceneBootstrap] Awake complete — all managers created and initialized");
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

            // 11-16: no title screen - straight to the home screen, one frame later so every manager's Start has run.
            if (GameManager.SKIP_TITLE && GameManager.Instance != null)
            {
                if (MenuUI.Instance != null) MenuUI.Instance.SetVisible(false);
                StartCoroutine(StartGameNextFrame());
            }
            // Show menu instead of auto-starting — let the player choose when to play
            else if (MenuUI.Instance != null)
            {
                MenuUI.Instance.SetVisible(true);
//                 WDLog.Log("[SceneBootstrap] Start — showing menu");
            }
            else if (GameManager.Instance != null)
            {
                // Fallback if MenuUI doesn't exist
                GameManager.Instance.TransitionTo(GameState.Playing);
            }
        }

        private System.Collections.IEnumerator StartGameNextFrame()
        {
            yield return null;
            if (GameManager.Instance != null) GameManager.Instance.StartGameDirect();
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
                WDLog.Log($"[SceneBootstrap] GameMode toggle → {(_debugIsLevelMode ? "Level" : "Survival")} (stub — Phase 2 wires this to actual mode routing)");
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
            // 2026-09-10 palette pass — MEASURED against Candy Crush / Royal Match / Toon Blast /
            // Monopoly Go / Scrabble GO (~/Documents/WordDrop_Art/refs_2026_09_10).
            //
            // FOUR of those five make the PLAY FIELD the brightest thing on screen (Royal Match:
            // chrome L=15, board L=66). Only Candy Crush inverts it — bright bg L=79 over a dark
            // slab L=41 — and WordDrop had copied that (bg L=76, board L=25) but pushed it further,
            // so the board read as a HOLE punched in the wrapper and the eye landed on empty cyan.
            //
            // We do NOT fix that by lightening the board: WordDrop's tiles are CREAM (L=89), and on
            // a Royal-Match-light board they would only clear it by ~23 points and go mushy. The
            // dark board is RIGHT for this tile art. So the background comes down instead, until
            // the TILES are the brightest thing on screen — which is what players should look at.
            //
            // Still clearly a bright candy sky (L≈49→36), just no longer competing with the board.
            // Revert = the two lines in the comment below.
            //   was: bgTop (0.40,0.82,0.96) L=76   bgBottom (0.50,0.90,0.84) L=82
            // 2026-09-10: first pass went to L 49/36 and Spencer called it "pretty dark" — too far.
            // Walked back to L 62/52: still a bright candy sky, but the cream tiles (L=89) stay the
            // brightest thing on screen and the board (L=24) is no longer competing with the wrapper.
            Color bgTop    = new Color(0.32f, 0.68f, 0.84f, 1f); // bright sky (L~62)
            Color bgBottom = new Color(0.24f, 0.58f, 0.70f, 1f); // slightly deeper toward the board (L~52)

            // ── PAINTED SCREEN BACKGROUND (2026-10-19 Spencer) ────────────────────────
            // Spencer's castle/terrace painting, exported at 2358x5110 — aspect 0.4614,
            // which is the 1179x2556 phone to within 0.0001, so it is composed for the
            // full screen rather than tiled. Loaded as a sprite and COVER-fitted below.
            // Falls back to the old two-colour gradient if the asset is missing, so a bad
            // import cannot ship a blank screen.
            // IMPORT GOTCHA: its .meta must carry nPOTScale: 0 — the default (1) silently
            // resamples non-power-of-two textures to the nearest POT and has cost this
            // project hours before (see the 2026-09-11 session note).
            Sprite bgArt = Resources.Load<Sprite>(BackgroundPath);   // 11-03: selectable (FX menu "BACKGROUND")
            if (bgArt == null) bgArt = Resources.Load<Sprite>("bg@2x");

            GameObject bgGO = new GameObject(bgArt != null ? "ScreenBackground" : "BackgroundGradient");
            if (bgArt != null)
            {
                var artSR = bgGO.AddComponent<SpriteRenderer>();
                artSR.sprite       = bgArt;
                artSR.sortingOrder = -10;
                artSR.color        = Color.white;

                // COVER fit: scale uniformly so the art fills the camera on BOTH axes and
                // crops the overflow, rather than letterboxing on a different aspect. The
                // 1.04 overscan keeps camera shake from exposing an edge.
                const float OVERSCAN = 1.15f;   // 2026-10-22 Spencer: big camera shakes exposed white space at 1.04 — pairs with WordDropFX.CAMERA_SHAKE_MAX
                float nativeW = bgArt.rect.width  / bgArt.pixelsPerUnit;
                float nativeH = bgArt.rect.height / bgArt.pixelsPerUnit;
                float fit = Mathf.Max((halfW * 2f * OVERSCAN) / nativeW,
                                      (halfH * 2f * OVERSCAN) / nativeH);
                bgGO.transform.localScale = new Vector3(fit, fit, 1f);
                bgGO.transform.position   = new Vector3(0f, 0f, 4f);
                if (UISeparation.GAME_SCRIM_ON)   // 10-31: darken behind the Goals/Moves bar (reading-room chandelier sits there)
                {
                    var sGO = new GameObject("ScreenScrim");
                    var ssr = sGO.AddComponent<SpriteRenderer>();
                    ssr.sprite = UISeparation.ScrimSprite(OVERSCAN, UISeparation.GAME_TOP_ALPHA, UISeparation.GAME_VIG_ALPHA, UISeparation.GAME_BOT_ALPHA);
                    ssr.sortingOrder = -9;
                    float sw = ssr.sprite.rect.width / ssr.sprite.pixelsPerUnit, sh = ssr.sprite.rect.height / ssr.sprite.pixelsPerUnit;
                    sGO.transform.localScale = new Vector3(halfW * 2f * OVERSCAN / sw, halfH * 2f * OVERSCAN / sh, 1f);
                    sGO.transform.position = new Vector3(0f, 0f, 3.9f);
                }
                WDLog.Log($"[SceneBootstrap] screen background '{bgArt.name}' " +
                          $"{bgArt.rect.width}x{bgArt.rect.height}px → fit {fit:F3} " +
                          $"(view {halfW * 2f:F2}x{halfH * 2f:F2} world units)");
                return;
            }

            Debug.LogWarning("[SceneBootstrap] UI/bg@2x not found — falling back to the gradient.");
            Sprite bgGrad = TileRenderer.CreateGradientRect(4, 128, bgBottom, bgTop);
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

//             WDLog.Log($"[SceneBootstrap] Camera — halfH={halfH:F2}, halfW={halfW:F2}  " +
                      // $"({Screen.width}x{Screen.height})");
        }

        // -----------------------------------------------------------------------
        // Event System — MUST be created before any Canvas/UI
        // -----------------------------------------------------------------------

        private void SetupEventSystem()
        {
            if (FindAnyObjectByType<EventSystem>() != null)
            {
//                 WDLog.Log("[SceneBootstrap] EventSystem already exists — skipping creation.");
                return;
            }

            GameObject esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<StandaloneInputModule>();
//             WDLog.Log("[SceneBootstrap] EventSystem created");
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

//             WDLog.Log("[SceneBootstrap] Core managers created: " +
                      // "GameManager, ScoreManager, MatchManager(stub), RulesEngine, " +
                      // "MatchController, AdManager, AIAgent");
        }

        private void SetupGrid()
        {
            new GameObject("GridManager").AddComponent<GridManager>();
//             WDLog.Log("[SceneBootstrap] GridManager created");
        }

        private void SetupHand()
        {
            new GameObject("HandManager").AddComponent<HandManager>();
//             WDLog.Log("[SceneBootstrap] HandManager created");
        }

        private void SetupHUD()
        {
            new GameObject("HUDManager").AddComponent<HUDManager>();
//             WDLog.Log("[SceneBootstrap] HUDManager created");
        }

        private void SetupColumnArrows()
        {
            new GameObject("ColumnArrowManager").AddComponent<ColumnArrowManager>();
//             WDLog.Log("[SceneBootstrap] ColumnArrowManager created");
        }

        private void SetupVisualBridge()
        {
            // GameVisualBridge subscribes to RulesEngine + MatchController events in Start().
            // Must be created after RulesEngine and MatchController exist.
            new GameObject("GameVisualBridge").AddComponent<GameVisualBridge>();
//             WDLog.Log("[SceneBootstrap] GameVisualBridge created");
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
            new GameObject("LevelMapPanel").AddComponent<LevelMapPanel>();   // between-level progression map (2026-07-10)
            new GameObject("TopOutPanel").AddComponent<TopOutPanel>();
            new GameObject("BoosterManager").AddComponent<BoosterManager>();
            new GameObject("BoosterHUDSlot").AddComponent<BoosterHUDSlot>();
            new GameObject("BoosterChoiceModal").AddComponent<BoosterChoiceModal>();
            new GameObject("ClusterOutline").AddComponent<ClusterOutline>();
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

//             WDLog.Log("[SceneBootstrap] UI screens created (MenuUI hidden, GameOverUI hidden). " +
                      // "No RoundManager / WordleEvaluator / RoundOverUI — removed in Job 12.");
        }

        // 2026-06-08 Spencer: on-device bloom/HDR diagnostic. Reads the ACTUAL render
        // state on the phone so we can see why bloom is absent. Read it off the screen.
        private void OnGUI()
        {
            if (LoadingScreen.Showing) return;   // 11-19: stay off the loading screen
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
