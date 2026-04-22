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

            // PLAY — primary CTA. Routes to Tutorial L1 if incomplete, else Level Select.
            CreateButton(_panel.transform, "PlayButton",
                anchorMin: new Vector2(0.18f, 0.44f),
                anchorMax: new Vector2(0.82f, 0.58f),
                label:     "PLAY",
                bgColor:   cfg != null ? cfg.menuPlayBgColor : new Color(0.20f, 0.72f, 0.35f, 1f),
                textColor: Color.white,
                fontSize:  cfg != null ? cfg.menuPlayFontSize : 48,
                onClick:   OnPlayClicked);

            // TODAY'S PUZZLE — secondary CTA. Hearts gate + save-streak + already-played
            // modals all handled inside OnDailyClicked.
            _dailyButton = CreateDailyButton(_panel.transform, "DailyButton",
                anchorMin: new Vector2(0.18f, 0.28f),
                anchorMax: new Vector2(0.82f, 0.40f));

            // Streak / daily-info line directly under the daily CTA.
            _dailyInfoText = CreateLabel(_panel.transform, "DailyInfoText",
                anchorMin: new Vector2(0.18f, 0.22f),
                anchorMax: new Vector2(0.82f, 0.28f),
                text: "",
                fontSize: cfg != null ? cfg.menuDailyInfoFontSize : 16,
                color: cfg != null ? cfg.menuDailyInfoColor : new Color(0.60f, 0.75f, 0.90f, 1f));
            RefreshDailyInfo();
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

            // Life gate BEFORE save-streak. Out of hearts = daily unplayable today
            // regardless, so don't pitch save-streak only to have them save + break
            // tomorrow. Resolve hearts first; save-streak surfaces on next tap.
            if (HeartsManager.Current <= 0)
            {
                AnalyticsManager.ButtonTap("daily_no_hearts");
                SetVisible(false);
                if (HeartWaitModal.Instance != null)
                    HeartWaitModal.Instance.SetVisible(true, HeartWaitModal.ReturnContext.DailyFlow);
                return;
            }

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
        /// Exposed separately so SaveStreakModal can continue into the level after
        /// restoring the streak, and the debug menu's RESET DAILY can re-trigger.
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
                if (LevelTutorialOverlay.Instance != null)
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
