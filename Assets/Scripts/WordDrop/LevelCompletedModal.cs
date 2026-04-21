using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Modal shown when LevelController fires OnLevelComplete.
    ///
    /// Displays: "Level Complete!" banner, 3-star row (lit based on score),
    /// final score + best-score line, and Next / Replay / Menu buttons.
    ///
    /// Canvas sortingOrder = 150 so it overlays MenuUI (100) and GameOverUI.
    /// Built once in Awake, hidden until event fires.
    /// Phase 4 scope: functional UX, modest polish (star pop-in + scale bounce).
    /// Phase 11 can deepen the polish (confetti, score tally animation, etc.).
    /// </summary>
    public class LevelCompletedModal : MonoBehaviour
    {
        public static LevelCompletedModal Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _panel;
        private Text _titleText;
        private Text _scoreLabelText;
        private Text _scoreValueText;
        private Text _bestValueText;
        private TextMeshProUGUI _coinRewardText;
        private int _coinRewardPending;
        private Tween _coinTween;
        private Image[] _starIcons = new Image[3];
        private GameObject _btnMenu;
        private GameObject _btnReplay;
        private GameObject _btnNext;
        private LevelData _levelData;
        private int _starsEarned;

        private static readonly Color STAR_LIT   = new Color(1.00f, 0.84f, 0.25f, 1f);
        private static readonly Color STAR_DARK  = new Color(0.25f, 0.23f, 0.32f, 1f);
        private static readonly Color PANEL_BG   = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG    = new Color(0.16f, 0.14f, 0.30f, 1f);
        private static readonly Color TITLE      = new Color(1.00f, 0.84f, 0.25f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);

            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelComplete += HandleLevelComplete;
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelComplete -= HandleLevelComplete;
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("LevelCompletedCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen dim overlay — also blocks board input because it captures raycasts.
            GameObject overlay = new GameObject("Overlay");
            overlay.transform.SetParent(canvasGO.transform, false);
            RectTransform oRT = overlay.AddComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero;
            oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero;
            oRT.offsetMax = Vector2.zero;
            Image oImg = overlay.AddComponent<Image>();
            oImg.color = PANEL_BG;
            oImg.raycastTarget = true;

            // Centered card
            _panel = new GameObject("Card");
            _panel.transform.SetParent(canvasGO.transform, false);
            RectTransform pRT = _panel.AddComponent<RectTransform>();
            pRT.anchorMin = new Vector2(0.08f, 0.22f);
            pRT.anchorMax = new Vector2(0.92f, 0.78f);
            pRT.offsetMin = Vector2.zero;
            pRT.offsetMax = Vector2.zero;
            Image pImg = _panel.AddComponent<Image>();
            pImg.color = CARD_BG;

            // Title
            _titleText = CreateLabel(_panel.transform, "Title",
                new Vector2(0.02f, 0.80f), new Vector2(0.98f, 0.95f),
                "LEVEL COMPLETE!", 38, TITLE);
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;
            _titleText.verticalOverflow = VerticalWrapMode.Overflow;

            // Star row
            float starY0 = 0.58f;
            float starY1 = 0.78f;
            float[] starX = { 0.18f, 0.43f, 0.68f };
            for (int i = 0; i < 3; i++)
            {
                GameObject starGO = new GameObject($"Star{i + 1}");
                starGO.transform.SetParent(_panel.transform, false);
                RectTransform sRT = starGO.AddComponent<RectTransform>();
                sRT.anchorMin = new Vector2(starX[i], starY0);
                sRT.anchorMax = new Vector2(starX[i] + 0.14f, starY1);
                sRT.offsetMin = Vector2.zero;
                sRT.offsetMax = Vector2.zero;
                Image sImg = starGO.AddComponent<Image>();
                sImg.color = STAR_DARK;
                sImg.sprite = CreateStarSprite();
                sImg.preserveAspect = true;
                _starIcons[i] = sImg;
            }

            // Score labels
            _scoreLabelText = CreateLabel(_panel.transform, "ScoreLabel",
                new Vector2(0.10f, 0.44f), new Vector2(0.90f, 0.52f),
                "Score", 22, new Color(0.75f, 0.72f, 0.82f, 1f));

            _scoreValueText = CreateLabel(_panel.transform, "ScoreValue",
                new Vector2(0.10f, 0.36f), new Vector2(0.90f, 0.46f),
                "0", 46, Color.white);
            _scoreValueText.fontStyle = FontStyle.Bold;

            _bestValueText = CreateLabel(_panel.transform, "BestValue",
                new Vector2(0.10f, 0.30f), new Vector2(0.90f, 0.36f),
                "", 18, new Color(0.70f, 0.67f, 0.80f, 1f));

            // Coin reward readout (TMP — needed for NumberIncrement count-up).
            // Sits between best-score and the button row.
            GameObject coinGO = new GameObject("CoinReward", typeof(RectTransform));
            coinGO.transform.SetParent(_panel.transform, false);
            RectTransform cRT = (RectTransform)coinGO.transform;
            cRT.anchorMin = new Vector2(0.10f, 0.23f);
            cRT.anchorMax = new Vector2(0.90f, 0.30f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            _coinRewardText = coinGO.AddComponent<TextMeshProUGUI>();
            _coinRewardText.text = "";
            _coinRewardText.fontSize = 26;
            _coinRewardText.color = UILayout.Gold;
            _coinRewardText.alignment = TextAlignmentOptions.Center;
            _coinRewardText.fontStyle = FontStyles.Bold;
            _coinRewardText.enableWordWrapping = false;

            // Buttons row — Menu / Replay / Next
            float btnY0 = 0.08f;
            float btnY1 = 0.22f;
            _btnMenu = CreateButton(_panel.transform, "BtnMenu",
                new Vector2(0.06f, btnY0), new Vector2(0.32f, btnY1),
                "MENU", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 22,
                OnMenuClicked);
            _btnReplay = CreateButton(_panel.transform, "BtnReplay",
                new Vector2(0.37f, btnY0), new Vector2(0.63f, btnY1),
                "REPLAY", new Color(0.25f, 0.55f, 0.85f, 1f), Color.white, 22,
                OnReplayClicked);
            _btnNext = CreateButton(_panel.transform, "BtnNext",
                new Vector2(0.68f, btnY0), new Vector2(0.94f, btnY1),
                "NEXT ▶", new Color(0.25f, 0.75f, 0.40f, 1f), Color.white, 22,
                OnNextClicked);
        }

        /// <summary>
        /// MenuUI.CreateButton returns void; this wrapper captures the created
        /// GameObject so we can show/hide buttons per variant.
        /// </summary>
        private static GameObject CreateButton(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string label,
            Color bgColor, Color textColor, int fontSize,
            UnityEngine.Events.UnityAction onClick)
        {
            int before = parent.childCount;
            MenuUI.CreateButton(parent, name, anchorMin, anchorMax,
                label, bgColor, textColor, fontSize, onClick);
            if (parent.childCount > before)
                return parent.GetChild(parent.childCount - 1).gameObject;
            // Fallback: find by name
            var t = parent.Find(name);
            return t != null ? t.gameObject : null;
        }

        // ── Event handling ──────────────────────────────────────────────────────

        private bool _tutorialCompleteVariant;
        private bool _dailyVariant;
        private int _dailyStreak;
        private int _dailyMilestoneBonus;

        private void HandleLevelComplete(int score, int stars)
        {
            _levelData = LevelController.Instance?.CurrentLevel;
            _starsEarned = stars;

            int prevBest = _levelData != null
                ? LevelProgressManager.GetBestScore(_levelData.levelId) : 0;

            if (_levelData != null)
            {
                LevelProgressManager.SetStarsIfHigher(_levelData.levelId, stars);
                LevelProgressManager.SetBestScoreIfHigher(_levelData.levelId, score);
                LevelProgressManager.MarkCompleted(_levelData.levelId);
                LevelProgressManager.UnlockThrough(_levelData.levelId + 1);
            }

            // Phase 6: the first time L3 completes, swap to Tutorial Complete variant.
            _tutorialCompleteVariant = _levelData != null
                && TutorialProgression.OnLevelCompleted(_levelData.levelId);

            // Phase 8: daily-variant detection. MarkPlayedToday updates the streak
            // and per-day best score. ClaimMilestoneBonusIfDue returns coin bonus
            // on the first time the player hits 3/7/14/30-day streaks.
            _dailyVariant = DailyDropManager.IsDailyMode;
            _dailyStreak = 0;
            _dailyMilestoneBonus = 0;
            if (_dailyVariant)
            {
                DailyDropManager.MarkPlayedToday(score);
                _dailyStreak = DailyDropManager.GetStreak();
                _dailyMilestoneBonus = DailyDropManager.ClaimMilestoneBonusIfDue(_dailyStreak);
                if (_dailyMilestoneBonus > 0)
                {
                    CoinWallet.Add(_dailyMilestoneBonus);
                    AnalyticsManager.Log(LevelAnalyticsEvents.COINS_EARNED,
                        "source", "daily_milestone",
                        "streak", _dailyStreak,
                        "amount", _dailyMilestoneBonus);
                }
            }

            // Phase 7: standard-variant coin reward = 1 per star, + 10 one-shot bonus
            // the first time a level earns 3 stars. Tutorial-complete variant already
            // grants +100 via TutorialProgression.OnLevelCompleted — skip the standard
            // award to avoid double-paying on L3's first clear.
            _coinRewardPending = 0;
            if (!_tutorialCompleteVariant && _levelData != null && stars > 0)
            {
                bool firstThreeStar = stars >= 3
                    && LevelProgressManager.ClaimFirstTimeThreeStarBonus(_levelData.levelId);
                int reward = stars + (firstThreeStar ? 10 : 0);
                if (reward > 0)
                {
                    CoinWallet.Add(reward);
                    _coinRewardPending = reward + _dailyMilestoneBonus;
                    AnalyticsManager.Log(LevelAnalyticsEvents.COINS_EARNED,
                        "level_id", _levelData.levelId,
                        "stars", stars,
                        "amount", reward,
                        "first_three_star", firstThreeStar ? 1 : 0);
                }
            }
            // Daily with no-star reward still shows the milestone bonus in the
            // coin count-up, so players see their reward.
            if (_dailyVariant && _coinRewardPending == 0 && _dailyMilestoneBonus > 0)
                _coinRewardPending = _dailyMilestoneBonus;

            ApplyVariant(score, prevBest);

            for (int i = 0; i < _starIcons.Length; i++)
            {
                _starIcons[i].color = STAR_DARK;
                _starIcons[i].transform.localScale = Vector3.one * 0.6f;
            }

            SetVisible(true);
            StartCoroutine(AnimateStars(stars));
        }

        private void ApplyVariant(int score, int prevBest)
        {
            if (_tutorialCompleteVariant)
            {
                if (_titleText != null) _titleText.text = "TUTORIAL COMPLETE!";
                if (_scoreLabelText != null) _scoreLabelText.text = "Reward";
                _scoreValueText.text = $"+{TutorialProgression.TUTORIAL_COMPLETE_COIN_REWARD} coins";
                if (_bestValueText != null)
                    _bestValueText.text = "You've learned the basics. Keep playing to earn stars!";
                if (_coinRewardText != null)
                {
                    _coinRewardText.gameObject.SetActive(false);
                    _coinRewardText.text = "";
                }
                if (_btnReplay != null) _btnReplay.SetActive(false);
                if (_btnNext != null)
                {
                    _btnNext.SetActive(true);
                    // Repurpose the existing Next button as the lone "PLAY" button.
                    var label = _btnNext.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (label != null) label.text = "PLAY";
                }
                if (_btnMenu != null) _btnMenu.SetActive(false);
                return;
            }

            if (_dailyVariant)
            {
                int puzzleNum = DailyDropManager.GetPuzzleNumber();
                if (_titleText != null) _titleText.text = $"DAILY #{puzzleNum}";
                if (_scoreLabelText != null) _scoreLabelText.text = "Score";
                _scoreValueText.text = score.ToString();
                if (_bestValueText != null)
                {
                    string streakLine = _dailyStreak > 0
                        ? $"🔥 {_dailyStreak}-day streak"
                        : "";
                    string milestoneLine = _dailyMilestoneBonus > 0
                        ? $"   ·   +{_dailyMilestoneBonus} milestone bonus!"
                        : "";
                    _bestValueText.text = streakLine + milestoneLine;
                }
                if (_coinRewardText != null)
                {
                    bool hasReward = _coinRewardPending > 0;
                    _coinRewardText.gameObject.SetActive(hasReward);
                    _coinRewardText.text = hasReward ? "+0 coins" : "";
                }
                // Daily is one-per-day — REPLAY and NEXT don't apply. Single HOME button.
                if (_btnReplay != null) _btnReplay.SetActive(false);
                if (_btnNext != null) _btnNext.SetActive(false);
                if (_btnMenu != null)
                {
                    _btnMenu.SetActive(true);
                    var label = _btnMenu.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                    if (label != null) label.text = "HOME";
                }
                return;
            }

            // Standard variant — restore default UI.
            if (_titleText != null) _titleText.text = "LEVEL COMPLETE!";
            if (_scoreLabelText != null) _scoreLabelText.text = "Score";
            _scoreValueText.text = score.ToString();
            int newBest = _levelData != null
                ? LevelProgressManager.GetBestScore(_levelData.levelId) : score;
            if (_bestValueText != null)
                _bestValueText.text = score > prevBest ? $"New best!  ({newBest})" : $"Best: {newBest}";

            // Coin reward — shown only when something was awarded. AnimateStars drives
            // the count-up so the stars land first, then the coin tally rolls.
            if (_coinRewardText != null)
            {
                bool hasReward = _coinRewardPending > 0;
                _coinRewardText.gameObject.SetActive(hasReward);
                _coinRewardText.text = hasReward ? "+0 coins" : "";
            }

            if (_btnReplay != null) _btnReplay.SetActive(true);
            if (_btnMenu != null)
            {
                _btnMenu.SetActive(true);
                var menuLabel = _btnMenu.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (menuLabel != null) menuLabel.text = "MENU";
            }
            if (_btnNext != null)
            {
                _btnNext.SetActive(true);
                var label = _btnNext.GetComponentInChildren<TMPro.TextMeshProUGUI>();
                if (label != null) label.text = "NEXT ▶";
            }
        }

        private IEnumerator AnimateStars(int stars)
        {
            // Route the star pop through UIAnimations — scale/stagger/easing comes
            // from the style-guide token set there (StarPopIn = 0→1.3→1.0, Heavy
            // overshoot, DurStagger=0.2s between stars).
            yield return new WaitForSeconds(0.15f);

            // Light the earned stars FIRST (color), then animate scale.
            for (int i = 0; i < stars && i < _starIcons.Length; i++)
                if (_starIcons[i] != null) _starIcons[i].color = STAR_LIT;

            var transforms = new Transform[_starIcons.Length];
            for (int i = 0; i < _starIcons.Length; i++)
                transforms[i] = _starIcons[i] != null ? _starIcons[i].transform : null;

            UIAnimations.StarPopIn(transforms, stars);

            // Audio still staggered in the caller so we stay in sync with the pop cadence.
            for (int i = 0; i < stars && i < _starIcons.Length; i++)
            {
                GameAudio.Instance?.PlayScoreImpact(8 + i * 4);
                yield return new WaitForSeconds(UIAnimations.DurStagger);
            }

            // Coin count-up lands after the last star so it reads as a reward for them.
            if (_coinRewardPending > 0 && _coinRewardText != null && !_tutorialCompleteVariant)
            {
                int reward = _coinRewardPending;
                RunCoinCountUp(reward, 0.6f);
                GameAudio.Instance?.PlayScoreImpact(20);
            }
        }

        /// <summary>
        /// Formatted coin count-up — preserves the "+N coins" framing on every frame.
        /// NumberIncrement from UIAnimations overwrites target.text with the raw int,
        /// which would drop the prefix/suffix mid-tween.
        /// </summary>
        private void RunCoinCountUp(int reward, float duration)
        {
            if (_coinRewardText == null) return;
            // Always kill any prior tween — fast REPLAY/NEXT transitions can stack
            // a new count-up on top of an in-flight one targeting the same label.
            _coinTween?.Kill();
            _coinTween = null;

            if (UIAnimations.ReducedMotion)
            {
                _coinRewardText.text = $"+{reward} coins";
                return;
            }
            int current = 0;
            _coinRewardText.text = "+0 coins";
            _coinTween = DOTween.To(() => current, v =>
            {
                current = v;
                if (_coinRewardText != null) _coinRewardText.text = $"+{current} coins";
            }, reward, duration)
                .SetEase(Ease.OutCubic)
                .OnComplete(() =>
                {
                    if (_coinRewardText != null) _coinRewardText.text = $"+{reward} coins";
                    _coinTween = null;
                });
        }

        // ── Button actions ──────────────────────────────────────────────────────

        private void OnNextClicked()
        {
            // Tutorial-complete variant — NEXT button is repurposed as PLAY → Level Select.
            if (_tutorialCompleteVariant)
            {
                SetVisible(false);
                if (LevelController.Instance != null)
                    LevelController.Instance.AbortLevel();
                GameManager.CurrentMode = GameMode.Survival;
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.Menu);
                if (LevelSelectScreen.Instance != null)
                    LevelSelectScreen.Instance.SetVisible(true);
                return;
            }

            if (_levelData == null) { OnMenuClicked(); return; }
            int nextId = _levelData.levelId + 1;
            LevelData nextData = LevelLoader.Load(nextId);
            if (nextData == null)
            {
                Debug.Log($"[LevelCompletedModal] No level_{nextId}.json — returning to Level Select.");
                OnMenuClicked();
                return;
            }
            var (ok, reason) = LevelValidator.Validate(nextData);
            if (!ok)
            {
                Debug.LogError($"[LevelCompletedModal] Level {nextId} invalid: {reason}");
                OnMenuClicked();
                return;
            }
            StartNewLevel(nextData);
        }

        private void OnReplayClicked()
        {
            if (_levelData == null) { OnMenuClicked(); return; }
            StartNewLevel(_levelData);
        }

        private void OnMenuClicked()
        {
            SetVisible(false);
            if (LevelController.Instance != null)
                LevelController.Instance.AbortLevel();

            // Daily variant: clear the IsDailyMode flag and land on the main menu
            // (not Level Select — daily is a main-menu feature). Standard variant
            // returns to Level Select so the player can pick their next level.
            bool daily = _dailyVariant;
            DailyDropManager.IsDailyMode = false;
            GameManager.CurrentMode = GameMode.Survival;
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Menu);
            if (!daily && LevelSelectScreen.Instance != null)
                LevelSelectScreen.Instance.SetVisible(true);
        }

        private void StartNewLevel(LevelData data)
        {
            SetVisible(false);
            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;
            GameManager.CurrentMode = GameMode.Level;

            LevelController.Instance.StartLevel(data);
            LevelProgressManager.IncrementAttempts(data.levelId);

            // Always go through TransitionTo(Playing). It handles Playing→Playing as a
            // restart and runs OnStateEntered which calls InitialiseHand +
            // SetInteractable(true) — critical for restoring input.
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        // ── UI plumbing ─────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
            if (visible)
            {
                // Pop the card in AFTER the canvas is active so the tween is
                // visible. PopIn resets scale to 0 internally before the bounce
                // so this is safe to call on a canvas that was previously shown.
                if (_panel != null) UIAnimations.PopIn(_panel.transform);
            }
            else
            {
                _coinTween?.Kill();
                _coinTween = null;
            }
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
            t.font = MenuUI.GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }

        /// <summary>
        /// Generates a simple 5-point star sprite procedurally so no texture asset is needed.
        /// </summary>
        private static Sprite _starSprite;
        private static Sprite CreateStarSprite()
        {
            if (_starSprite != null) return _starSprite;

            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32[] px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            float outer = size * 0.48f;
            float inner = outer * 0.45f;
            Vector2[] pts = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float r = (i % 2 == 0) ? outer : inner;
                float a = Mathf.PI / 2f + i * Mathf.PI / 5f;
                pts[i] = center + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
            }

            // Point-in-polygon fill
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (PointInPolygon(new Vector2(x + 0.5f, y + 0.5f), pts))
                        px[y * size + x] = white;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            tex.filterMode = FilterMode.Bilinear;
            _starSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _starSprite;
        }

        private static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                bool intersect = ((poly[i].y > p.y) != (poly[j].y > p.y)) &&
                    (p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
