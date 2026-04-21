using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Main menu screen. Built and hidden in Awake() — before SceneBootstrap.Start()
    /// transitions directly to Playing, so no menu flash occurs.
    /// </summary>
    public class MenuUI : MonoBehaviour
    {
        public static MenuUI Instance { get; private set; }

        private GameObject _panel;
        private Canvas     _canvas;
        private Text       _bestScoreText;
        private Text       _currenciesText;
        private Coroutine  _currenciesCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // Build UI in Awake and hide immediately.
            // SceneBootstrap.Start() transitions to Playing without showing the menu.
            BuildUI();
            SetVisible(false);
//             Debug.Log("[MenuUI] Awake — panel built and hidden");
        }

        private void BuildUI()
        {
            var cfg = UIConfig.Instance; // null-safe: we check per-field below

            GameObject canvasGO = new GameObject("MenuCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = cfg != null ? cfg.referenceResolution : new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = cfg != null ? cfg.canvasMatchWidthOrHeight : 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            _panel = CreatePanel(canvasGO.transform, "MenuPanel",
                cfg != null ? cfg.menuPanelBgColor : new Color(0.08f, 0.08f, 0.10f, 0.98f));

            // Title
            GameObject titleGO = new GameObject("TitleText");
            titleGO.transform.SetParent(_panel.transform, false);

            RectTransform titleRT = titleGO.AddComponent<RectTransform>();
            titleRT.anchorMin = cfg != null ? cfg.menuTitleAnchorMin : new Vector2(0.05f, 0.88f);
            titleRT.anchorMax = cfg != null ? cfg.menuTitleAnchorMax : new Vector2(0.95f, 0.98f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            Text titleText      = titleGO.AddComponent<Text>();
            titleText.font      = GetFont();
            titleText.text      = "WordDrop";
            titleText.fontSize  = cfg != null ? cfg.menuTitleFontSize : 64;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color     = cfg != null ? cfg.menuTitleColor : new Color(0.96f, 0.84f, 0.25f, 1f);
            titleText.alignment = TextAnchor.MiddleCenter;

            // Personal best score
            _bestScoreText = CreateLabel(_panel.transform, "BestScoreText",
                anchorMin: cfg != null ? cfg.menuBestScoreAnchorMin : new Vector2(0.05f, 0.83f),
                anchorMax: cfg != null ? cfg.menuBestScoreAnchorMax : new Vector2(0.95f, 0.88f),
                text: "",
                fontSize: cfg != null ? cfg.menuBestScoreFontSize : 22,
                color: cfg != null ? cfg.menuBestScoreColor : new Color(0.96f, 0.76f, 0.29f, 1f));
            RefreshBestScore();

            // Hearts + coins readout — top-right corner HUD, refreshes while the
            // menu is visible so the regen timer stays honest without a HUD canvas.
            _currenciesText = CreateLabel(_panel.transform, "CurrenciesText",
                anchorMin: new Vector2(0.55f, 0.93f),
                anchorMax: new Vector2(0.97f, 0.99f),
                text: "",
                fontSize: 20,
                color: UILayout.Gold);
            _currenciesText.alignment = TextAnchor.MiddleRight;
            _currenciesText.fontStyle = FontStyle.Bold;
            RefreshCurrencies();

            // ── CLASSIC 1v1 section ──────────────────────────────────────────

            // Difficulty selector
            _difficultyText = CreateLabel(_panel.transform, "DifficultyLabel",
                anchorMin: cfg != null ? cfg.menuDiffLabelAnchorMin : new Vector2(0.05f, 0.74f),
                anchorMax: cfg != null ? cfg.menuDiffLabelAnchorMax : new Vector2(0.95f, 0.82f),
                text: "Difficulty: Easy",
                fontSize: cfg != null ? cfg.menuDiffLabelFontSize : 22,
                color: cfg != null ? cfg.menuDiffLabelColor : new Color(0.75f, 0.75f, 0.80f, 1f));

            float diffY0 = cfg != null ? cfg.menuDiffButtonY0 : 0.67f;
            float diffY1 = cfg != null ? cfg.menuDiffButtonY1 : 0.74f;
            int diffFS   = cfg != null ? cfg.menuDiffButtonFontSize : 18;

            CreateButton(_panel.transform, "DiffEasy",
                anchorMin: new Vector2(0.05f, diffY0),
                anchorMax: new Vector2(0.33f, diffY1),
                label: "EASY", bgColor: cfg != null ? cfg.menuDiffEasyColor : new Color(0.25f, 0.65f, 0.35f, 1f),
                textColor: Color.white, fontSize: diffFS,
                onClick: () => SetDifficulty(0, "Easy"));

            CreateButton(_panel.transform, "DiffMedium",
                anchorMin: new Vector2(0.34f, diffY0),
                anchorMax: new Vector2(0.66f, diffY1),
                label: "MEDIUM", bgColor: cfg != null ? cfg.menuDiffMediumColor : new Color(0.80f, 0.65f, 0.15f, 1f),
                textColor: Color.white, fontSize: diffFS,
                onClick: () => SetDifficulty(1, "Medium"));

            CreateButton(_panel.transform, "DiffHard",
                anchorMin: new Vector2(0.67f, diffY0),
                anchorMax: new Vector2(0.95f, diffY1),
                label: "HARD", bgColor: cfg != null ? cfg.menuDiffHardColor : new Color(0.80f, 0.25f, 0.20f, 1f),
                textColor: Color.white, fontSize: diffFS,
                onClick: () => SetDifficulty(2, "Hard"));

            // AI profile selector
            _profileText = CreateLabel(_panel.transform, "ProfileLabel",
                anchorMin: cfg != null ? cfg.menuProfileLabelAnchorMin : new Vector2(0.05f, 0.59f),
                anchorMax: cfg != null ? cfg.menuProfileLabelAnchorMax : new Vector2(0.95f, 0.66f),
                text: "AI Style: Scorer",
                fontSize: cfg != null ? cfg.menuProfileLabelFontSize : 20,
                color: cfg != null ? cfg.menuDiffLabelColor : new Color(0.75f, 0.75f, 0.80f, 1f));

            float profBtnY0 = cfg != null ? cfg.menuProfileButtonY0 : 0.52f;
            float profBtnY1 = cfg != null ? cfg.menuProfileButtonY1 : 0.59f;
            int profFS      = cfg != null ? cfg.menuProfileButtonFontSize : 16;

            CreateButton(_panel.transform, "ProfScorer",
                anchorMin: new Vector2(0.05f, profBtnY0),
                anchorMax: new Vector2(0.33f, profBtnY1),
                label: "SCORER", bgColor: cfg != null ? cfg.menuProfileScorerColor : new Color(0.30f, 0.55f, 0.75f, 1f),
                textColor: Color.white, fontSize: profFS,
                onClick: () => SetProfile(AIAgent.AIProfile.Scorer));
            CreateButton(_panel.transform, "ProfBlocker",
                anchorMin: new Vector2(0.34f, profBtnY0),
                anchorMax: new Vector2(0.66f, profBtnY1),
                label: "BLOCKER", bgColor: cfg != null ? cfg.menuProfileBlockerColor : new Color(0.60f, 0.35f, 0.60f, 1f),
                textColor: Color.white, fontSize: profFS,
                onClick: () => SetProfile(AIAgent.AIProfile.Blocker));
            CreateButton(_panel.transform, "ProfHunter",
                anchorMin: new Vector2(0.67f, profBtnY0),
                anchorMax: new Vector2(0.95f, profBtnY1),
                label: "HUNTER", bgColor: cfg != null ? cfg.menuProfileHunterColor : new Color(0.75f, 0.30f, 0.25f, 1f),
                textColor: Color.white, fontSize: profFS,
                onClick: () => SetProfile(AIAgent.AIProfile.TriggerHunter));

            // PLAY button (Classic 1v1)
            CreateButton(_panel.transform, "PlayButton",
                anchorMin: cfg != null ? cfg.menuPlayAnchorMin : new Vector2(0.10f, 0.42f),
                anchorMax: cfg != null ? cfg.menuPlayAnchorMax : new Vector2(0.90f, 0.52f),
                label:     "PLAY",
                bgColor:   cfg != null ? cfg.menuPlayBgColor : new Color(0.20f, 0.72f, 0.35f, 1f),
                textColor: Color.white,
                fontSize:  cfg != null ? cfg.menuPlayFontSize : 40,
                onClick:   OnPlayClicked);

            // ── Solo modes ───────────────────────────────────────────────────

            // DAILY DROP button
            _dailyButton = CreateDailyButton(_panel.transform, "DailyButton",
                anchorMin: cfg != null ? cfg.menuDailyAnchorMin : new Vector2(0.05f, 0.28f),
                anchorMax: cfg != null ? cfg.menuDailyAnchorMax : new Vector2(0.48f, 0.38f));

            // Daily info label (streak / completed)
            _dailyInfoText = CreateLabel(_panel.transform, "DailyInfoText",
                anchorMin: new Vector2(0.05f, 0.24f),
                anchorMax: new Vector2(0.48f, 0.28f),
                text: "",
                fontSize: cfg != null ? cfg.menuDailyInfoFontSize : 14,
                color: cfg != null ? cfg.menuDailyInfoColor : new Color(0.60f, 0.75f, 0.90f, 1f));
            RefreshDailyInfo();

            // BLITZ button
            CreateButton(_panel.transform, "BlitzButton",
                anchorMin: cfg != null ? cfg.menuBlitzAnchorMin : new Vector2(0.52f, 0.28f),
                anchorMax: cfg != null ? cfg.menuBlitzAnchorMax : new Vector2(0.95f, 0.38f),
                label:     "BLITZ 90s",
                bgColor:   cfg != null ? cfg.menuBlitzBgColor : new Color(0.85f, 0.30f, 0.15f, 1f),
                textColor: Color.white,
                fontSize:  cfg != null ? cfg.menuBlitzFontSize : 24,
                onClick:   OnBlitzClicked);

            // Blitz best score
            _blitzBestScoreText = CreateLabel(_panel.transform, "BlitzBestScoreText",
                anchorMin: new Vector2(0.52f, 0.24f),
                anchorMax: new Vector2(0.95f, 0.28f),
                text: "",
                fontSize: cfg != null ? cfg.menuBlitzBestScoreFontSize : 14,
                color: cfg != null ? cfg.menuBlitzBestScoreColor : new Color(0.85f, 0.50f, 0.25f, 1f));
            RefreshBlitzBestScore();

            // SURVIVAL button
            CreateButton(_panel.transform, "SurvivalButton",
                anchorMin: new Vector2(0.05f, 0.16f),
                anchorMax: new Vector2(0.95f, 0.24f),
                label:     "SURVIVAL",
                bgColor:   new Color(0.10f, 0.70f, 0.75f, 1f),  // cyan
                textColor: Color.white,
                fontSize:  24,
                onClick:   OnSurvivalClicked);

            // ── Debug buttons ────────────────────────────────────────────────

            Color dbgBg  = cfg != null ? cfg.menuDebugBgColor : new Color(0.3f, 0.3f, 0.4f, 0.5f);
            Color dbgTxt = cfg != null ? cfg.menuDebugTextColor : new Color(0.7f, 0.7f, 0.8f, 0.7f);
            int dbgFS    = cfg != null ? cfg.menuDebugFontSize : 13;

            CreateButton(_panel.transform, "ResetTutorialBtn",
                anchorMin: new Vector2(0.05f, 0.02f),
                anchorMax: new Vector2(0.35f, 0.07f),
                label:     "RESET ALL",
                bgColor:   dbgBg,
                textColor: dbgTxt,
                fontSize:  dbgFS,
                onClick:   OnResetAllClicked);

            CreateButton(_panel.transform, "ResetDailyBtn",
                anchorMin: new Vector2(0.36f, 0.02f),
                anchorMax: new Vector2(0.65f, 0.07f),
                label:     "RESET DAILY",
                bgColor:   dbgBg,
                textColor: dbgTxt,
                fontSize:  dbgFS,
                onClick:   OnResetDailyClicked);

            // Rising Rows toggle
            _risingRowsText = CreateLabel(_panel.transform, "RisingRowsLabel",
                anchorMin: new Vector2(0.05f, 0.08f),
                anchorMax: new Vector2(0.95f, 0.12f),
                text: GetRisingRowsLabel(),
                fontSize: dbgFS,
                color: dbgTxt);

            CreateButton(_panel.transform, "RisingRowsBtn",
                anchorMin: new Vector2(0.66f, 0.02f),
                anchorMax: new Vector2(0.95f, 0.07f),
                label:     "RISING ROWS",
                bgColor:   dbgBg,
                textColor: dbgTxt,
                fontSize:  dbgFS,
                onClick:   OnRisingRowsToggle);

            // SFX toggle
            _sfxText = CreateLabel(_panel.transform, "SFXLabel",
                anchorMin: new Vector2(0.35f, 0.02f),
                anchorMax: new Vector2(0.65f, 0.06f),
                text: GetSFXLabel(),
                color: dbgTxt, fontSize: dbgFS);

            CreateButton(_panel.transform, "SFXBtn",
                anchorMin: new Vector2(0.35f, 0.02f),
                anchorMax: new Vector2(0.64f, 0.07f),
                label:     "SFX",
                bgColor:   dbgBg,
                textColor: dbgTxt,
                fontSize:  dbgFS,
                onClick:   OnSFXToggle);
        }

        private Text _difficultyText;
        private Text _profileText;
        private Text _blitzBestScoreText;
        private GameObject _dailyButton;
        private Text _risingRowsText;
        private Text _dailyInfoText;
        private Text _sfxText;

        private void SetProfile(AIAgent.AIProfile profile)
        {
            AIAgent.CurrentProfile = profile;
            if (_profileText != null)
                _profileText.text = $"AI Style: {profile}";
//             Debug.Log($"[MenuUI] AI profile set to {profile}");
        }

        private void SetDifficulty(int level, string name)
        {
            AIAgent.Difficulty = level;
            if (_difficultyText != null)
                _difficultyText.text = $"Difficulty: {name}";
//             Debug.Log($"[MenuUI] Difficulty set to {name} ({level})");
        }

        private void OnPlayClicked()
        {
            // Phase 6 override: if the tutorial hasn't been completed yet, PLAY launches
            // the next incomplete tutorial level (L1 for a fresh install, or the resume
            // point if the player quit mid-tutorial). Post-tutorial, PLAY opens the
            // Level Select screen. Classic/Blitz/Survival/Daily still reachable via their
            // dedicated buttons.
            AnalyticsManager.ButtonTap("play");

            if (!TutorialProgression.IsTutorialComplete())
            {
                int resumeId = TutorialProgression.NextIncompleteTutorialLevel();
                if (resumeId <= TutorialProgression.TUTORIAL_LAST_LEVEL_ID)
                {
                    StartLevelFromMenu(resumeId);
                    return;
                }
                // Fall through if already past the last tutorial level.
            }

            // Tutorial done → open Level Select on top of the main menu.
            SetVisible(false);
            if (LevelSelectScreen.Instance != null)
                LevelSelectScreen.Instance.SetVisible(true);
            AnalyticsManager.ScreenView("level_select");
        }

        /// <summary>
        /// Shared helper: wire mode flags + LevelController + transition for a Level
        /// launched directly from the main menu (tutorial flow).
        /// </summary>
        private void StartLevelFromMenu(int levelId)
        {
            LevelData data = LevelLoader.Load(levelId);
            if (data == null)
            {
                Debug.LogError($"[MenuUI] Failed to load level {levelId} from tutorial flow.");
                return;
            }
            var (ok, reason) = LevelValidator.Validate(data);
            if (!ok)
            {
                Debug.LogError($"[MenuUI] Level {levelId} invalid: {reason}");
                return;
            }

            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;
            SurvivalManager.IsSurvivalMode = false;
            GameManager.CurrentMode = GameMode.Level;

            if (LevelController.Instance != null)
                LevelController.Instance.StartLevel(data);
            LevelProgressManager.IncrementAttempts(levelId);

            AnalyticsManager.ScreenView($"playing_tutorial_{levelId}");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnBlitzClicked()
        {
            DailyDropManager.IsDailyMode = false;
            BlitzManager.IsBlitzMode = true;
            SurvivalManager.IsSurvivalMode = false;
            AnalyticsManager.ButtonTap("blitz");
            AnalyticsManager.ScreenView("playing_blitz");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnSurvivalClicked()
        {
            DailyDropManager.IsDailyMode = false;
            BlitzManager.IsBlitzMode = false;
            SurvivalManager.IsSurvivalMode = true;
            AnalyticsManager.ButtonTap("survival");
            AnalyticsManager.ScreenView("playing_survival");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnDailyClicked()
        {
            // Already played today → show the "Come back tomorrow" modal instead
            // of the silent no-op the pre-Phase-8 flow used.
            if (DailyDropManager.HasPlayedToday())
            {
                AnalyticsManager.ButtonTap("daily_already_played");
                if (DailyAlreadyPlayedModal.Instance != null)
                    DailyAlreadyPlayedModal.Instance.SetVisible(true);
                return;
            }

            // Life gate BEFORE save-streak. If the player is out of hearts they
            // can't actually play the daily, so offering to save the streak is
            // cruel — they'd save it but then watch it break tomorrow anyway.
            // Resolve hearts first; the save-streak prompt will surface on the
            // next tap once hearts are available.
            if (HeartsManager.Current <= 0)
            {
                AnalyticsManager.ButtonTap("daily_no_hearts");
                SetVisible(false);
                if (HeartWaitModal.Instance != null)
                    HeartWaitModal.Instance.SetVisible(true, HeartWaitModal.ReturnContext.DailyFlow);
                return;
            }

            // Save-streak opportunity: the player's on a streak but missed
            // yesterday. Offer the rewarded ad before letting them start a new
            // daily (which would reset the streak to 1).
            if (DailyDropManager.CanSaveStreak() && SaveStreakModal.Instance != null)
            {
                AnalyticsManager.ButtonTap("daily_save_streak_prompt");
                SaveStreakModal.Instance.SetVisible(true);
                return;
            }

            BeginDailyLevel();
        }

        /// <summary>
        /// Loads today's daily level and routes through LevelController (Phase 8).
        /// Exposed as a separate method so SaveStreakModal can continue into the
        /// level after restoring the streak, and DailyAlreadyPlayedModal can
        /// re-trigger for debug replay via the menu's RESET DAILY button.
        /// </summary>
        public void BeginDailyLevel()
        {
            int levelId = DailyDropManager.GetDailyLevelId();
            LevelData data = LevelLoader.Load(levelId);
            if (data == null)
            {
                Debug.LogError($"[MenuUI] Daily level {levelId} failed to load — aborting.");
                return;
            }
            var (ok, reason) = LevelValidator.Validate(data);
            if (!ok)
            {
                Debug.LogError($"[MenuUI] Daily level {levelId} invalid: {reason}");
                return;
            }

            // Life gate still applies: the daily attempt spends a heart like any
            // other level attempt. HeartWaitModal handles the 0-heart case and
            // returns to MainMenu (not LevelSelect) when dismissed from here —
            // daily is a main-menu feature, not a level-map feature.
            if (!HeartsManager.Consume())
            {
                SetVisible(false);
                if (HeartWaitModal.Instance != null)
                    HeartWaitModal.Instance.SetVisible(true, HeartWaitModal.ReturnContext.DailyFlow);
                return;
            }

            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = true;
            GameManager.CurrentMode = GameMode.Level;

            LevelController.Instance.StartLevel(data);
            LevelProgressManager.IncrementAttempts(levelId);

            AnalyticsManager.ButtonTap("daily_start");
            AnalyticsManager.ScreenView("playing_daily");
            AnalyticsManager.Log("daily_start",
                "level_id", levelId,
                "puzzle_number", DailyDropManager.GetPuzzleNumber(),
                "streak_before", DailyDropManager.GetStreak());

            SetVisible(false);
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private GameObject CreateDailyButton(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax)
        {
            var cfg = UIConfig.Instance;
            bool played = DailyDropManager.HasPlayedToday();
            string label = played ? "COMPLETED" : "TODAY'S PUZZLE";
            Color bgColor = played
                ? (cfg != null ? cfg.menuDailyCompletedColor : new Color(0.35f, 0.45f, 0.55f, 0.7f))
                : (cfg != null ? cfg.menuDailyBgColor : new Color(0.20f, 0.45f, 0.80f, 1f));

            GameObject btnGO = new GameObject(name);
            btnGO.transform.SetParent(parent, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image img = btnGO.AddComponent<Image>();
            img.color = bgColor;

            Button btn = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.highlightedColor = Color.Lerp(bgColor, Color.white, 0.25f);
            cb.pressedColor     = Color.Lerp(bgColor, Color.black, 0.25f);
            cb.normalColor      = bgColor;
            btn.colors          = cb;
            Transform dailyBtnTransform = btnGO.transform;
            btn.onClick.AddListener(() => UIAnimations.ButtonPress(dailyBtnTransform));
            btn.onClick.AddListener(OnDailyClicked);
            btn.interactable = !played;

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);

            RectTransform labelRT = labelGO.AddComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            Text t      = labelGO.AddComponent<Text>();
            t.font      = GetFont();
            t.text      = label;
            t.fontSize  = cfg != null ? cfg.menuDailyFontSize : 26;
            t.fontStyle = FontStyle.Bold;
            t.color     = Color.white;
            t.alignment = TextAnchor.MiddleCenter;

            return btnGO;
        }

        private void RefreshDailyInfo()
        {
            if (_dailyInfoText == null) return;

            bool played = DailyDropManager.HasPlayedToday();
            int streak = DailyDropManager.GetStreak();
            // Phase 8: streak prefix always visible when there's a live streak.
            // Flame glyph matches common daily-app conventions (Duolingo, NYT).

            if (played)
            {
                int todayScore = DailyDropManager.GetTodayBest();
                string streakStr = streak > 0 ? $"  ★ {streak}" : "";
                _dailyInfoText.text = $"Score: {todayScore}{streakStr}";
            }
            else if (streak > 0)
            {
                _dailyInfoText.text = $"★ Streak: {streak} day{(streak == 1 ? "" : "s")}";
            }
            else
            {
                _dailyInfoText.text = "Play today's puzzle";
            }

            // Update button appearance
            if (_dailyButton != null)
            {
                Button btn = _dailyButton.GetComponent<Button>();
                // Always interactable — taps on a completed day open the
                // "Come back tomorrow" modal instead of silently dropping.
                if (btn != null) btn.interactable = true;

                Text label = _dailyButton.GetComponentInChildren<Text>();
                if (label != null) label.text = played ? "COMPLETED" : "TODAY'S PUZZLE";

                Image img = _dailyButton.GetComponent<Image>();
                if (img != null)
                {
                    var rcfg = UIConfig.Instance;
                    img.color = played
                        ? (rcfg != null ? rcfg.menuDailyCompletedColor : new Color(0.35f, 0.45f, 0.55f, 0.7f))
                        : (rcfg != null ? rcfg.menuDailyBgColor : new Color(0.20f, 0.45f, 0.80f, 1f));
                }
            }
        }

        private void OnRisingRowsToggle()
        {
            RisingRowManager.Enabled = !RisingRowManager.Enabled;
            if (_risingRowsText != null)
                _risingRowsText.text = GetRisingRowsLabel();
//             Debug.Log($"[MenuUI] Rising Rows toggled: {(RisingRowManager.Enabled ? "ON" : "OFF")}");
        }

        private string GetRisingRowsLabel()
        {
            return $"Rising Rows: {(RisingRowManager.Enabled ? "ON" : "OFF")} (every {RisingRowManager.TurnInterval} turns)";
        }

        private void OnSFXToggle()
        {
            if (GameAudio.Instance == null) return;
            GameAudio.Instance.Muted = !GameAudio.Instance.Muted;
            if (_sfxText != null)
                _sfxText.text = GetSFXLabel();
//             Debug.Log($"[MenuUI] SFX toggled: {(GameAudio.Instance.Muted ? "OFF" : "ON")}");
        }

        private string GetSFXLabel()
        {
            bool muted = GameAudio.Instance != null && GameAudio.Instance.Muted;
            return $"SFX: {(muted ? "OFF" : "ON")}";
        }

        private void OnResetAllClicked()
        {
            TutorialManager.ResetTutorial();
            DailyDropManager.ResetDaily();
            RefreshBestScore();
            RefreshBlitzBestScore();
            RefreshDailyInfo();
//             Debug.Log("[MenuUI] Full reset — tutorial, daily, scores all cleared.");
        }

        private void OnResetDailyClicked()
        {
            DailyDropManager.ResetDaily();
            RefreshDailyInfo();
//             Debug.Log("[MenuUI] Daily reset — can replay today.");
        }

        public void SetVisible(bool visible)
        {
            if (_panel != null) _panel.SetActive(visible);
            if (visible)
            {
                // Defensive: the tutorial overlay's canvas sortingOrder (120) is
                // above the menu (100), and a prior daily/level's prompt can
                // stick around if OnLevelComplete's fade got interrupted. Main
                // menu should NEVER have a tutorial prompt hovering — force-hide.
                if (LevelTutorialOverlay.Instance != null)
                    LevelTutorialOverlay.Instance.ForceHide();

                RefreshBestScore();
                RefreshBlitzBestScore();
                RefreshDailyInfo();
                RefreshCurrencies();
                if (_currenciesCoroutine == null && gameObject.activeInHierarchy)
                    _currenciesCoroutine = StartCoroutine(CurrenciesTick());
            }
            else if (_currenciesCoroutine != null)
            {
                StopCoroutine(_currenciesCoroutine);
                _currenciesCoroutine = null;
            }
        }

        private IEnumerator CurrenciesTick()
        {
            var wait = new WaitForSecondsRealtime(1f);
            while (_panel != null && _panel.activeInHierarchy)
            {
                RefreshCurrencies();
                yield return wait;
            }
            _currenciesCoroutine = null;
        }

        private void RefreshCurrencies()
        {
            if (_currenciesText == null) return;
            int hearts = HeartsManager.Current;
            int coins = CoinWallet.Balance;
            _currenciesText.text = $"♥ {hearts}/{HeartsManager.MAX_HEARTS}   ● {coins}";
        }

        private void RefreshBestScore()
        {
            if (_bestScoreText == null) return;
            int best = HighScoreManager.GetBest("classic");
            _bestScoreText.text = best > 0 ? $"Best: {best}" : "";
        }

        private void RefreshBlitzBestScore()
        {
            if (_blitzBestScoreText == null) return;
            int best = HighScoreManager.GetBest("blitz");
            _blitzBestScoreText.text = best > 0 ? $"Blitz Best: {best}" : "";
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image img = go.AddComponent<Image>();
            img.color = color;

            return go;
        }

        private static Text CreateLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Text t = go.AddComponent<Text>();
            t.font = GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }

        internal static void CreateButton(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string label, Color bgColor, Color textColor,
            int fontSize,
            UnityEngine.Events.UnityAction onClick)
        {
            GameObject btnGO = new GameObject(name);
            btnGO.transform.SetParent(parent, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image img = btnGO.AddComponent<Image>();
            img.color = bgColor;

            Button btn    = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.highlightedColor = Color.Lerp(bgColor, Color.white, 0.25f);
            cb.pressedColor     = Color.Lerp(bgColor, Color.black, 0.25f);
            cb.normalColor      = bgColor;
            btn.colors          = cb;
            // Tactile press feedback — every button built through this helper
            // gets the scale-down/bounce-back on tap. Phase 6.5 canon animation.
            Transform btnTransform = btnGO.transform;
            btn.onClick.AddListener(() => UIAnimations.ButtonPress(btnTransform));
            btn.onClick.AddListener(() => GameAudio.Instance?.PlayButtonClick());
            btn.onClick.AddListener(onClick);

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);

            RectTransform labelRT = labelGO.AddComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            TextMeshProUGUI t = labelGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset btnFont = GameFont.GetUITMP();
            if (btnFont != null) t.font = btnFont;
            t.text      = label;
            t.fontSize  = fontSize;
            t.fontStyle = FontStyles.Bold;
            t.color     = textColor;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;

            // Button-tier outline
            t.outlineWidth = 0.1f;
            t.outlineColor = (Color32)Color.Lerp(textColor, Color.black, 0.5f);
        }

        internal static Font GetFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }
    }
}
