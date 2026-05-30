using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Production main menu — player-facing only. Keeps just logo, hearts,
    /// coins, streak, PLAY, TODAY'S PUZZLE. Every debug / dev / testing
    /// control lives in LevelDebugMenu (press L, dev-build-only) — see
    /// project_wordrop_migration_plan.md Phase 9 3-category rule.
    ///
    /// Built and hidden in Awake() — SceneBootstrap.Start() transitions
    /// directly to Playing so no menu flash occurs on fresh launch.
    /// </summary>
    public class MenuUI : MonoBehaviour
    {
        public static MenuUI Instance { get; private set; }

        private GameObject _panel;
        private Canvas     _canvas;
        private Text       _currenciesText;
        private Text       _dailyInfoText;
        private GameObject _dailyButton;
        private Coroutine  _currenciesCoroutine;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);
        }

        private void BuildUI()
        {
            var cfg = UIConfig.Instance;

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

            // Title — big WordDrop logo, upper portion of the screen.
            GameObject titleGO = new GameObject("TitleText");
            titleGO.transform.SetParent(_panel.transform, false);
            RectTransform titleRT = titleGO.AddComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.70f);
            titleRT.anchorMax = new Vector2(0.95f, 0.85f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;
            Text titleText      = titleGO.AddComponent<Text>();
            titleText.font      = GetFont();
            titleText.text      = "WordDrop";
            titleText.fontSize  = cfg != null ? cfg.menuTitleFontSize : 72;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color     = cfg != null ? cfg.menuTitleColor : new Color(0.96f, 0.84f, 0.25f, 1f);
            titleText.alignment = TextAnchor.MiddleCenter;

            // Hearts + coins — top-right corner, refreshes each second while visible.
            _currenciesText = CreateLabel(_panel.transform, "CurrenciesText",
                anchorMin: new Vector2(0.55f, 0.93f),
                anchorMax: new Vector2(0.97f, 0.99f),
                text: "",
                fontSize: 20,
                color: UILayout.Gold);
            _currenciesText.alignment = TextAnchor.MiddleRight;
            _currenciesText.fontStyle = FontStyle.Bold;
            RefreshCurrencies();

            // SURVIVAL — hero CTA (Phase 11a pivot). Large, top, bold red-orange.
            // Unlocked from first launch; no tutorial gate (Survival IS the game,
            // first-time onboarding lives WITHIN the mode, Phase 11c).
            CreateButton(_panel.transform, "SurvivalButton",
                anchorMin: new Vector2(0.12f, 0.48f),
                anchorMax: new Vector2(0.88f, 0.66f),
                label:     "SURVIVAL",
                bgColor:   new Color(0.88f, 0.32f, 0.20f, 1f),  // bold red-orange
                textColor: Color.white,
                fontSize:  56,
                onClick:   OnSurvivalClicked);

            // PUZZLES — demoted from primary CTA. Routes via OnPlayClicked to
            // tutorial L1 if incomplete, else Level Select. Same behavior as the
            // old PLAY button; smaller visual weight under the Survival hero.
            CreateButton(_panel.transform, "PlayButton",
                anchorMin: new Vector2(0.24f, 0.36f),
                anchorMax: new Vector2(0.76f, 0.44f),
                label:     "PUZZLES",
                bgColor:   cfg != null ? cfg.menuPlayBgColor : new Color(0.20f, 0.72f, 0.35f, 1f),
                textColor: Color.white,
                fontSize:  30,
                onClick:   OnPlayClicked);

            // TODAY'S PUZZLE — unchanged behavior, repositioned under Puzzles.
            _dailyButton = CreateDailyButton(_panel.transform, "DailyButton",
                anchorMin: new Vector2(0.24f, 0.24f),
                anchorMax: new Vector2(0.76f, 0.32f));

            // Streak / daily-info line directly under the daily CTA.
            _dailyInfoText = CreateLabel(_panel.transform, "DailyInfoText",
                anchorMin: new Vector2(0.18f, 0.18f),
                anchorMax: new Vector2(0.82f, 0.24f),
                text: "",
                fontSize: cfg != null ? cfg.menuDailyInfoFontSize : 16,
                color: cfg != null ? cfg.menuDailyInfoColor : new Color(0.60f, 0.75f, 0.90f, 1f));
            RefreshDailyInfo();
        }

        /// <summary>
        /// Phase 11a Survival-primary pivot. Hero CTA. Sets the mode flags +
        /// transitions to Playing; MatchController.StartMatch does the rest
        /// (new TileBag, fill hands, SurvivalManager.StartSurvival init).
        ///
        /// MVP: no hearts gate. Hearts were dropped from Survival (2026-05-22)
        /// after Claude+Codex review — coin-bypass made hearts decorative; Daily
        /// Survival Modifier carries retention instead. Coins gate continues.
        /// </summary>
        private void OnSurvivalClicked()
        {
            AnalyticsManager.ButtonTap("survival");

            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;
            SurvivalManager.IsSurvivalMode = true;
            GameManager.CurrentMode = GameMode.Survival;

            SetVisible(false);
            AnalyticsManager.ScreenView("playing_survival");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnPlayClicked()
        {
            // First-launch: tutorial not complete → auto-launch next incomplete
            // tutorial level. Post-tutorial → open Level Select. Debug Force-Level
            // remains the override path for testing arbitrary levels.
            AnalyticsManager.ButtonTap("play");

            if (!TutorialProgression.IsTutorialComplete())
            {
                int resumeId = TutorialProgression.NextIncompleteTutorialLevel();
                if (resumeId <= TutorialProgression.TUTORIAL_LAST_LEVEL_ID)
                {
                    StartLevelFromMenu(resumeId);
                    return;
                }
            }

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

        private void OnDailyClicked()
        {
            // Already played today → show the "Come back tomorrow" modal.
            if (DailyDropManager.HasPlayedToday())
            {
                AnalyticsManager.ButtonTap("daily_already_played");
                if (DailyAlreadyPlayedModal.Instance != null)
                    DailyAlreadyPlayedModal.Instance.SetVisible(true);
                return;
            }

            // MVP: no hearts gate (hearts dropped from Survival 2026-05-22). Daily
            // is now free-play participation — same UTC seed for everyone.

            if (DailyDropManager.CanSaveStreak() && SaveStreakModal.Instance != null)
            {
                AnalyticsManager.ButtonTap("daily_save_streak_prompt");
                SaveStreakModal.Instance.SetVisible(true);
                return;
            }

            BeginDailySurvival();
        }

        /// <summary>
        /// MVP P3.5: launch today's Daily Seeded Survival. Same UTC seed for all
        /// players → deterministic letter bag (TileBag's seeded path) + deterministic
        /// gameplay random (SurvivalRng auto-seeded by SurvivalManager.StartSurvival
        /// when DailyDropManager.IsDailyMode is true).
        ///
        /// Exposed separately so SaveStreakModal can continue into the run after
        /// restoring the streak, and the debug menu can re-trigger.
        ///
        /// Note: the old Level-mode-based Daily Drop (BeginDailyLevel) was deprecated
        /// with the Survival pivot. This is its Survival-mode replacement.
        /// </summary>
        public void BeginDailySurvival()
        {
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = true;
            SurvivalManager.IsSurvivalMode = true;
            GameManager.CurrentMode = GameMode.Survival;

            int seed = DailyDropManager.GetDailySeed();

            AnalyticsManager.ButtonTap("daily_start");
            AnalyticsManager.ScreenView("playing_daily");
            AnalyticsManager.Log("daily_start",
                "seed", seed,
                "puzzle_number", DailyDropManager.GetPuzzleNumber(),
                "streak_before", DailyDropManager.GetStreak());

            SetVisible(false);
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);

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

            if (_dailyButton != null)
            {
                Button btn = _dailyButton.GetComponent<Button>();
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

        public void SetVisible(bool visible)
        {
            if (_panel != null) _panel.SetActive(visible);
            if (visible)
            {
                // The tutorial overlay lives on a higher sortingOrder canvas. A
                // prior daily/level's prompt can stick around if OnLevelComplete's
                // fade got interrupted. Main menu should never have a tutorial
                // prompt hovering — force-hide on every show.
                //
                // EXCEPTION: during an active ScreenTransition, SetVisible(true)
                // fires mid-slide while a brand-new level's persistent "start"
                // prompt was just shown by LevelController.StartLevel. Force-
                // hiding here kills the prompt before the transition completes.
                // The original "prompt leak to menu" guard only needs to apply
                // on genuine menu-returns — post-transition — not mid-flight.
                bool midTransition = ScreenTransition.Instance != null
                                  && ScreenTransition.Instance.IsTransitioning;
                if (!midTransition && LevelTutorialOverlay.Instance != null)
                    LevelTutorialOverlay.Instance.ForceHide();

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
