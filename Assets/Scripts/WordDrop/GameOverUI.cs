using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Game over panel — RTT-style whoosh-in from bottom, whoosh-out on play again.
    /// </summary>
    public class GameOverUI : MonoBehaviour
    {
        public static GameOverUI Instance { get; private set; }

        // ── Animation settings (from RTT RunResultPanel) ────────────────────────
        private const float SLIDE_OFFSET_Y = 800f;
        private const float SHOW_DURATION  = 0.2f;
        private const float HIDE_DURATION  = 0.3f;
        private const float STAGGER_DELAY  = 0.08f;

        // ── Colors (matching WordDrop palette) ──────────────────────────────────
        // Colors — read from UIConfig if available, otherwise use hardcoded defaults
        private static Color PANEL_BG
        {
            get { return new Color(0.22f, 0.14f, 0.38f, 0.97f); } // medium purple — distinct from dark background
        }
        private static readonly Color OVERLAY_COLOR   = new Color(0.05f, 0.02f, 0.12f, 0.75f); // dark purple overlay
        private static Color PLAYER_GREEN
        {
            get { return new Color(1.00f, 0.84f, 0.42f, 1f); } // gold (match player)
        }
        private static Color AI_ORANGE
        {
            get { return new Color(1.00f, 0.56f, 0.67f, 1f); } // pink (match AI)
        }
        private static Color GOLD
        {
            get { return new Color(1.00f, 0.84f, 0.42f, 1f); } // warm gold
        }
        private static Color DEFEAT_RED
        {
            get { return new Color(1.00f, 0.40f, 0.40f, 1f); } // soft red
        }
        private static Color TEXT_WHITE
        {
            get { return new Color(0.94f, 0.90f, 0.84f, 1f); } // warm white
        }
        private static Color TEXT_DIM
        {
            get { return new Color(0.63f, 0.53f, 0.75f, 0.8f); } // light purple
        }
        private static Color BTN_COLOR
        {
            get { return new Color(0.91f, 0.72f, 0.29f, 1f); } // amber gold button
        }

        // ── UI refs ─────────────────────────────────────────────────────────────
        private Canvas     _canvas;
        private GameObject _overlay;
        private GameObject _panel;
        private RectTransform _panelRT;
        private Vector2 _panelHomePos;

        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _winnerText;
        private TextMeshProUGUI _playerScoreText;
        private TextMeshProUGUI _aiScoreText;
        private TextMeshProUGUI _turnsPlayedText;
        private TextMeshProUGUI _bestScoreText;

        // Daily mode elements
        private TextMeshProUGUI _dailyStreakText;
        private GameObject _shareButton;
        private GameObject _playAgainButton;

        // Elements for stagger animation
        private GameObject[] _staggerElements;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            SetVisible(false);
//             Debug.Log("[GameOverUI] Awake — panel built and hidden");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD
        // ═══════════════════════════════════════════════════════════════════════════

        private void BuildUI()
        {
            // Canvas
            GameObject canvasGO = new GameObject("GameOverCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 10000;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = 1f; // height-based like RTT

            canvasGO.AddComponent<GraphicRaycaster>();

            // Dark overlay
            _overlay = new GameObject("Overlay");
            _overlay.transform.SetParent(canvasGO.transform, false);
            RectTransform overlayRT = _overlay.AddComponent<RectTransform>();
            overlayRT.anchorMin = Vector2.zero;
            overlayRT.anchorMax = Vector2.one;
            overlayRT.offsetMin = Vector2.zero;
            overlayRT.offsetMax = Vector2.zero;
            Image overlayImg = _overlay.AddComponent<Image>();
            overlayImg.color = new Color(0f, 0f, 0f, 0f); // starts transparent

            // Panel — centered card
            _panel = new GameObject("ResultPanel");
            _panel.transform.SetParent(canvasGO.transform, false);

            _panelRT = _panel.AddComponent<RectTransform>();
            _panelRT.anchorMin = new Vector2(0.5f, 0.5f);
            _panelRT.anchorMax = new Vector2(0.5f, 0.5f);
            _panelRT.pivot     = new Vector2(0.5f, 0.5f);
            var cfg = UIConfig.Instance;
            _panelRT.sizeDelta = cfg != null ? cfg.gameOverPanelSize : new Vector2(460f, 420f);
            _panelHomePos = Vector2.zero;

            // Panel background with rounded corners
            Image panelImg = _panel.AddComponent<Image>();
            panelImg.sprite = TileRenderer.CreateSolidRoundedRect(1024, 1024, 120, Color.white);
            panelImg.type = Image.Type.Simple;
            panelImg.color = PANEL_BG;

            // ── Content ──
            var stagger = new System.Collections.Generic.List<GameObject>();

            int titleFS  = cfg != null ? cfg.gameOverTitleFontSize : 48;
            int winnerFS = cfg != null ? cfg.gameOverWinnerFontSize : 34;
            int scoreFS  = cfg != null ? cfg.gameOverScoreFontSize : 30;
            int turnsFS  = cfg != null ? cfg.gameOverTurnsFontSize : 20;
            int bestFS   = cfg != null ? cfg.gameOverBestFontSize : 22;
            int playFS   = cfg != null ? cfg.gameOverPlayAgainFontSize : 32;
            int shareFS  = cfg != null ? cfg.gameOverShareFontSize : 26;
            Color shareBg = cfg != null ? cfg.gameOverShareColor : new Color(0.40f, 0.65f, 0.90f, 1f);

            // Title: "GAME OVER" — celebration font
            _titleText = MakeLabel(_panel.transform, "Title",
                new Vector2(0.05f, 0.78f), new Vector2(0.95f, 0.95f),
                "GAME OVER", titleFS, FontStyle.Normal, GOLD, TextAnchor.MiddleCenter);
            TMP_FontAsset displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _titleText.font = displayFont;
            stagger.Add(_titleText.gameObject);

            // Winner text — celebration font
            _winnerText = MakeLabel(_panel.transform, "Winner",
                new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.78f),
                "PLAYER WINS!", winnerFS, FontStyle.Normal, PLAYER_GREEN, TextAnchor.MiddleCenter);
            if (displayFont != null) _winnerText.font = displayFont;
            stagger.Add(_winnerText.gameObject);

            // Player score
            _playerScoreText = MakeLabel(_panel.transform, "PlayerScore",
                new Vector2(0.05f, 0.48f), new Vector2(0.95f, 0.62f),
                "Player: 0", scoreFS, FontStyle.Normal, PLAYER_GREEN, TextAnchor.MiddleCenter);
            stagger.Add(_playerScoreText.gameObject);

            // AI score
            _aiScoreText = MakeLabel(_panel.transform, "AIScore",
                new Vector2(0.05f, 0.36f), new Vector2(0.95f, 0.48f),
                "AI: 0", scoreFS, FontStyle.Normal, AI_ORANGE, TextAnchor.MiddleCenter);
            stagger.Add(_aiScoreText.gameObject);

            // Turns played
            _turnsPlayedText = MakeLabel(_panel.transform, "Turns",
                new Vector2(0.05f, 0.28f), new Vector2(0.95f, 0.36f),
                "", turnsFS, FontStyle.Normal, TEXT_DIM, TextAnchor.MiddleCenter);
            stagger.Add(_turnsPlayedText.gameObject);

            // Best score line
            _bestScoreText = MakeLabel(_panel.transform, "BestScore",
                new Vector2(0.05f, 0.22f), new Vector2(0.95f, 0.28f),
                "", bestFS, FontStyle.Normal, GOLD, TextAnchor.MiddleCenter);
            stagger.Add(_bestScoreText.gameObject);

            // Daily streak text (hidden in classic mode)
            _dailyStreakText = MakeLabel(_panel.transform, "DailyStreak",
                new Vector2(0.05f, 0.22f), new Vector2(0.95f, 0.28f),
                "", bestFS, FontStyle.Normal, new Color(0.40f, 0.70f, 1f, 1f), TextAnchor.MiddleCenter);
            _dailyStreakText.gameObject.SetActive(false);
            stagger.Add(_dailyStreakText.gameObject);

            // Share button (daily mode only)
            _shareButton = MakeButton(_panel.transform, "ShareButton",
                new Vector2(0.15f, 0.14f), new Vector2(0.85f, 0.22f),
                "SHARE", shareFS, shareBg, OnShareClicked);
            _shareButton.SetActive(false);
            stagger.Add(_shareButton);

            // Play Again button
            GameObject btnGO = MakeButton(_panel.transform, "PlayAgain",
                new Vector2(0.15f, 0.02f), new Vector2(0.85f, 0.16f),
                "PLAY AGAIN", playFS, BTN_COLOR, OnPlayAgainClicked);
            _playAgainButton = btnGO;
            stagger.Add(btnGO);

            _staggerElements = stagger.ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SHOW — whoosh in from bottom
        // ═══════════════════════════════════════════════════════════════════════════

        private bool _showInProgress = false;

        public void Show()
        {
            // Guard against multiple calls
            if (_showInProgress) return;
            _showInProgress = true;
            GameAudio.Instance?.PlayGameOver();
            GameAudio.Instance?.PlayWhooshBig();

            // Check for detonation replay before showing the panel
            if (DetonationRecorder.Instance != null && DetonationRecorder.Instance.HasReplay
                && DetonationReplay.Instance != null)
            {
                StartCoroutine(ShowWithReplay());
                return;
            }

            ShowPanel();
        }

        private IEnumerator ShowWithReplay()
        {
//             Debug.Log("[GameOverUI] Playing detonation replay before showing scores.");

            var chain = DetonationRecorder.Instance.GetBestChain();
            Coroutine replay = DetonationReplay.Instance.PlayReplay(chain);
            if (replay != null)
                yield return replay;

            ShowPanel();
        }

        private void ShowPanel()
        {
            // Fire victory music (Skybound canonical) as the score panel begins
            // to fly up. 2026-05-16 — replaces the silent transition that used
            // to occur here; the panel reveal now lands on a swelling track.
            GameAudio.Instance?.PlayVictoryMusic();

            bool isBlitz = BlitzManager.IsBlitzMode;
            bool isDaily = DailyDropManager.IsDailyMode;

            // Gather data
            int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            int aiScore     = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore : 0;
            int totalTurns  = MatchController.Instance != null
                ? MatchController.Instance.TotalMaxTurns : 0;

            // ── Daily Drop mode ─────────────────────────────────────────────────
            if (isDaily)
            {
                bool isNewDailyBest = DailyDropManager.MarkPlayedToday(playerScore);
                int streak = DailyDropManager.GetStreak();
                int puzzleNum = DailyDropManager.GetPuzzleNumber();

                if (_titleText != null) _titleText.text = $"DAILY DROP #{puzzleNum}";
                if (_winnerText != null) { _winnerText.text = $"Score: {playerScore}"; _winnerText.color = GOLD; }
                if (_playerScoreText != null)
                    _playerScoreText.text = $"{DailyDropManager.DAILY_TURNS} turns played";
                if (_aiScoreText != null) _aiScoreText.gameObject.SetActive(false);

                // Streak
                if (_dailyStreakText != null)
                {
                    _dailyStreakText.gameObject.SetActive(streak > 1);
                    _dailyStreakText.text = streak > 1 ? $"Streak: {streak} days" : "";
                }

                // Best score (already submitted by MarkPlayedToday above)
                int dailyBest = HighScoreManager.GetBest("daily");
                if (_bestScoreText != null)
                {
                    _bestScoreText.text  = isNewDailyBest ? "NEW DAILY BEST!" : $"Daily Best: {dailyBest}";
                    _bestScoreText.color = isNewDailyBest ? GOLD : TEXT_DIM;
                }

                if (_shareButton != null) _shareButton.SetActive(true);
                if (_turnsPlayedText != null) _turnsPlayedText.text = "";

                SetVisible(true);
                StartCoroutine(ShowAnimation());
                return;
            }

            // ── Survival mode ────────────────────────────────────────────────────
            if (SurvivalManager.IsSurvivalMode)
            {
                float timeSurvived = SurvivalManager.Instance != null ? SurvivalManager.Instance.ElapsedTime : 0f;
                int totalDrops = SurvivalManager.Instance != null ? SurvivalManager.Instance.TotalAutoDrops : 0;
                int longestChain = SurvivalManager.Instance != null ? SurvivalManager.Instance.LongestChain : 0;
                int humanDrops = MatchController.Instance != null
                    ? MatchController.Instance.GetPlayerTurns(MatchController.PLAYER_HUMAN) : 0;

                string timeStr = FormatSurvivalTime(timeSurvived);
                bool survNewBest = HighScoreManager.Submit(playerScore, "survival");
                int survBest = HighScoreManager.GetBest("survival");

                TMP_FontAsset displayFont = GameFont.GetDisplayTMP();
                if (_titleText != null)
                {
                    _titleText.text = "TOP OUT";
                    _titleText.color = DEFEAT_RED;
                    if (displayFont != null) _titleText.font = displayFont;
                }
                if (_winnerText != null)
                {
                    _winnerText.text = $"{playerScore} PTS";
                    _winnerText.color = GOLD;
                    if (displayFont != null) _winnerText.font = displayFont;
                }
                int stage = SurvivalManager.Instance != null ? SurvivalManager.Instance.GetCurrentStage() : 1;
                if (_playerScoreText != null)
                {
                    _playerScoreText.text = $"{timeStr}  |  S{stage}  |  {humanDrops} drops";
                    _playerScoreText.fontSize = 24; // tighter fit for stats line
                }
                if (_aiScoreText != null)
                {
                    string longestWord = SurvivalManager.Instance != null
                        ? SurvivalManager.Instance.LongestWord : "";
                    string wordLine = string.IsNullOrEmpty(longestWord)
                        ? "—"
                        : $"{longestWord} ({longestWord.Length})";
                    _aiScoreText.gameObject.SetActive(true);
                    _aiScoreText.text = $"Best Word: {wordLine}  |  Best Chain: {longestChain}";
                    _aiScoreText.fontSize = 22;
                    _aiScoreText.color = TEXT_DIM;
                }
                if (_turnsPlayedText != null)
                    _turnsPlayedText.text = "";
                if (_bestScoreText != null)
                {
                    _bestScoreText.text = survNewBest ? "NEW BEST!" : $"Best: {survBest}";
                    _bestScoreText.color = survNewBest ? GOLD : TEXT_DIM;
                }
                if (_dailyStreakText != null) _dailyStreakText.gameObject.SetActive(false);
                if (_shareButton != null) _shareButton.SetActive(false);

                SetVisible(true);
                StartCoroutine(ShowAnimation());
                return;
            }

            // ── Classic / Blitz mode — hide daily elements ──────────────────────
            if (_dailyStreakText != null) _dailyStreakText.gameObject.SetActive(false);
            if (_shareButton != null) _shareButton.SetActive(false);
            if (_aiScoreText != null) _aiScoreText.gameObject.SetActive(true);

            // Determine winner / header
            string winnerLabel; Color winnerColor;
            if (isBlitz)
            {
                winnerLabel = "TIME'S UP!";
                winnerColor = new Color(0.85f, 0.30f, 0.15f, 1f); // blitz red/orange
            }
            else if (playerScore > aiScore)
            {
                winnerLabel = "YOU WIN!";
                winnerColor = PLAYER_GREEN;
            }
            else if (aiScore > playerScore)
            {
                winnerLabel = "AI WINS!";
                winnerColor = DEFEAT_RED;
            }
            else
            {
                winnerLabel = "IT'S A TIE!";
                winnerColor = GOLD;
            }

            // Submit score to appropriate mode leaderboard
            string scoreMode = isBlitz ? "blitz" : "classic";
            bool isNewBest = HighScoreManager.Submit(playerScore, scoreMode);
            int bestScore  = HighScoreManager.GetBest(scoreMode);

            // Populate
            if (_titleText != null) _titleText.text = isBlitz ? "BLITZ" : "GAME OVER";
            if (_winnerText != null) { _winnerText.text = winnerLabel; _winnerText.color = winnerColor; }
            if (_playerScoreText != null)
                _playerScoreText.text = isBlitz ? $"Score:  {playerScore} pts" : $"Player:  {playerScore} pts";
            if (_aiScoreText != null)
            {
                if (isBlitz)
                {
                    _aiScoreText.gameObject.SetActive(false); // hide AI score in blitz
                }
                else
                {
                    _aiScoreText.gameObject.SetActive(true);
                    _aiScoreText.text = $"AI:  {aiScore} pts";
                }
            }
            if (_turnsPlayedText != null)
            {
                if (isBlitz)
                {
                    int turnsPlayed = MatchController.Instance != null
                        ? MatchController.Instance.GetPlayerTurns(MatchController.PLAYER_HUMAN) : 0;
                    _turnsPlayedText.text = $"{turnsPlayed} drops in 90s";
                }
                else
                {
                    _turnsPlayedText.text = $"{totalTurns} turns played";
                }
            }

            // Best score + best combo line
            int bestCombo = HighScoreManager.GetBestCombo(scoreMode);
            if (_bestScoreText != null)
            {
                string comboStr = bestCombo > 0 ? $"  |  Best Combo: {bestCombo}" : "";
                if (isNewBest)
                {
                    _bestScoreText.text  = $"NEW BEST!{comboStr}";
                    _bestScoreText.color = GOLD;
                }
                else
                {
                    _bestScoreText.text  = $"Best: {bestScore}{comboStr}";
                    _bestScoreText.color = TEXT_DIM;
                }
            }

            // Show and animate
            SetVisible(true);
            StartCoroutine(ShowAnimation());
        }

        private IEnumerator ShowAnimation()
        {
            // Start panel off-screen below
            _panelRT.anchoredPosition = _panelHomePos + new Vector2(0, -SLIDE_OFFSET_Y);
            _panel.transform.localScale = Vector3.one;

            // Fade overlay
            Image overlayImg = _overlay.GetComponent<Image>();
            if (overlayImg != null)
                overlayImg.DOFade(0.75f, SHOW_DURATION * 1.2f);

            // Hide stagger elements initially
            foreach (var el in _staggerElements)
            {
                if (el == null) continue;
                CanvasGroup cg = el.GetComponent<CanvasGroup>();
                if (cg == null) cg = el.AddComponent<CanvasGroup>();
                cg.alpha = 0f;
                el.transform.localScale = Vector3.one * 0.7f;
            }

            // Slide panel in from bottom with OutBack overshoot
            _panelRT.DOAnchorPos(_panelHomePos, SHOW_DURATION)
                .SetEase(Ease.OutBack, 3f);

            // Punch scale on arrival
            _panel.transform.DOPunchScale(Vector3.one * 0.1f, 0.2f, 10, 0.5f)
                .SetDelay(0.08f);

            yield return new WaitForSeconds(SHOW_DURATION * 0.6f);

            // Stagger in each element
            for (int i = 0; i < _staggerElements.Length; i++)
            {
                var el = _staggerElements[i];
                if (el == null) continue;

                CanvasGroup cg = el.GetComponent<CanvasGroup>();
                if (cg != null)
                    DOTween.To(() => cg.alpha, a => cg.alpha = a, 1f, 0.2f)
                        .SetEase(Ease.OutQuad);

                el.transform.DOScale(Vector3.one, 0.25f)
                    .SetEase(Ease.OutBack, 1.5f);

                el.transform.DOPunchScale(Vector3.one * 0.08f, 0.15f, 8, 0.5f)
                    .SetDelay(0.12f);

                yield return new WaitForSeconds(STAGGER_DELAY);
            }

            // Title celebration pop
            if (_winnerText != null)
            {
                _winnerText.transform.DOScale(1.15f, 0.15f).SetEase(Ease.OutBack, 3f)
                    .OnComplete(() =>
                    {
                        if (_winnerText != null)
                            _winnerText.transform.DOScale(1f, 0.2f).SetEase(Ease.OutElastic, 0.8f, 0.3f);
                    });
                _winnerText.transform.DOPunchRotation(Vector3.forward * 5f, 0.4f, 10, 0.5f);
            }

            // NEW BEST celebration — extra pop + punch on the best-score line
            if (_bestScoreText != null && _bestScoreText.text.Contains("NEW BEST"))
            {
                yield return new WaitForSeconds(0.15f);
                GameAudio.Instance?.PlayPersonalBest();
                _bestScoreText.transform.DOScale(1.25f, 0.15f).SetEase(Ease.OutBack, 4f)
                    .OnComplete(() =>
                    {
                        if (_bestScoreText != null)
                            _bestScoreText.transform.DOScale(1f, 0.25f).SetEase(Ease.OutElastic, 0.8f, 0.3f);
                    });
                _bestScoreText.transform.DOPunchRotation(Vector3.forward * 8f, 0.4f, 12, 0.5f);
            }

            // Candy-Crush style cartoon idle bounce on the PLAY AGAIN button —
            // continuous gentle pulse signals "tap me" without being demanding.
            // Loops until the panel hides or the button is clicked.
            StartPlayAgainPulse();
        }

        private void StartPlayAgainPulse()
        {
            if (_playAgainButton == null) return;
            Transform t = _playAgainButton.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            // 1.0 → 1.07 → 1.0 over 1.4s, infinite yoyo with smooth InOutSine.
            // Pulse is intentionally subtle — not screaming, just breathing.
            t.DOScale(1.07f, 0.7f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private void StopPlayAgainPulse()
        {
            if (_playAgainButton == null) return;
            _playAgainButton.transform.DOKill();
            _playAgainButton.transform.localScale = Vector3.one;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // HIDE — whoosh out to bottom
        // ═══════════════════════════════════════════════════════════════════════════

        private void OnPlayAgainClicked()
        {
            StopPlayAgainPulse();

            // Press-squash on the button so the tap feels physical, not a
            // dead input. Quick squash → pop → settle, matches Candy Crush
            // button-press feel. Runs alongside the HideAnimation slide-out.
            if (_playAgainButton != null)
            {
                Transform t = _playAgainButton.transform;
                t.DOKill();
                Sequence pressSeq = DOTween.Sequence();
                pressSeq.Append(t.DOScale(0.92f, 0.06f).SetEase(Ease.OutQuad));
                pressSeq.Append(t.DOScale(1.06f, 0.10f).SetEase(Ease.OutBack, 4f));
                pressSeq.Append(t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad));
            }

            // If daily mode, go back to menu (can't replay daily)
            if (DailyDropManager.IsDailyMode)
            {
                DailyDropManager.IsDailyMode = false;
                AnalyticsManager.ButtonTap("daily_back_to_menu");
                StartCoroutine(HideAnimationToMenu());
                return;
            }

            AnalyticsManager.ButtonTap("play_again");
            StartCoroutine(HideAnimation());
        }

        private void OnShareClicked()
        {
            int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            int streak = DailyDropManager.GetStreak();
            string shareText = DailyDropManager.GenerateShareText(playerScore, streak);

            GUIUtility.systemCopyBuffer = shareText;
//             Debug.Log($"[GameOverUI] Share text copied to clipboard:\n{shareText}");

            // Brief visual feedback on share button
            if (_shareButton != null)
            {
                var label = _shareButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = "COPIED!";
                // Reset after a delay
                StartCoroutine(ResetShareButtonLabel(1.5f));
            }

            AnalyticsManager.ButtonTap("share_daily");
        }

        private IEnumerator ResetShareButtonLabel(float delay)
        {
            yield return new WaitForSeconds(delay);
            if (_shareButton != null)
            {
                var label = _shareButton.GetComponentInChildren<TextMeshProUGUI>();
                if (label != null) label.text = "SHARE";
            }
        }

        private IEnumerator HideAnimation()
        {
            // Slide panel down with InBack
            _panelRT.DOAnchorPos(_panelHomePos + new Vector2(0, -SLIDE_OFFSET_Y), HIDE_DURATION)
                .SetEase(Ease.InBack, 1.5f);

            // Fade overlay out
            Image overlayImg = _overlay.GetComponent<Image>();
            if (overlayImg != null)
                overlayImg.DOFade(0f, HIDE_DURATION);

            yield return new WaitForSeconds(HIDE_DURATION + 0.05f);

            SetVisible(false);

            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private IEnumerator HideAnimationToMenu()
        {
            _panelRT.DOAnchorPos(_panelHomePos + new Vector2(0, -SLIDE_OFFSET_Y), HIDE_DURATION)
                .SetEase(Ease.InBack, 1.5f);

            Image overlayImg = _overlay.GetComponent<Image>();
            if (overlayImg != null)
                overlayImg.DOFade(0f, HIDE_DURATION);

            yield return new WaitForSeconds(HIDE_DURATION + 0.05f);

            SetVisible(false);

            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Menu);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // VISIBILITY
        // ═══════════════════════════════════════════════════════════════════════════

        public void SetVisible(bool visible)
        {
            if (_overlay != null) _overlay.SetActive(visible);
            if (_panel != null) _panel.SetActive(visible);

            if (!visible)
            {
                // Reset overlay alpha for next show
                Image overlayImg = _overlay != null ? _overlay.GetComponent<Image>() : null;
                if (overlayImg != null) overlayImg.color = new Color(0f, 0f, 0f, 0f);
                _showInProgress = false;
            }
        }

        private static string FormatSurvivalTime(float seconds)
        {
            int m = Mathf.FloorToInt(seconds / 60f);
            int s = Mathf.FloorToInt(seconds % 60f);
            return $"{m}:{s:D2}";
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // UI BUILDERS
        // ═══════════════════════════════════════════════════════════════════════════

        private TextMeshProUGUI MakeLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string text, int fontSize, FontStyle style, Color color, TextAnchor align)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset uiFont = GameFont.GetUITMP();
            if (uiFont != null) t.font = uiFont;
            t.text      = text;
            t.fontSize  = fontSize;
            t.fontStyle = FontStyles.Normal;
            t.color     = color;
            t.alignment = align == TextAnchor.MiddleCenter ? TextAlignmentOptions.Center
                        : align == TextAnchor.MiddleLeft ? TextAlignmentOptions.MidlineLeft
                        : TextAlignmentOptions.MidlineRight;
            t.enableWordWrapping = false;
            TMPHelper.ApplyEffects(t, color);
            return t;
        }

        private GameObject MakeButton(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string label, int fontSize, Color bgColor,
            UnityEngine.Events.UnityAction onClick)
        {
            GameObject btnGO = new GameObject(name);
            btnGO.transform.SetParent(parent, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            // Rounded button background
            Image img = btnGO.AddComponent<Image>();
            img.sprite = TileRenderer.CreateSolidRoundedRect(1024, 256, 96, Color.white);
            img.type = Image.Type.Simple;
            img.color = bgColor;

            Button btn = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor      = bgColor;
            cb.highlightedColor = Color.Lerp(bgColor, Color.white, 0.2f);
            cb.pressedColor     = Color.Lerp(bgColor, Color.black, 0.2f);
            cb.fadeDuration     = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(() => GameAudio.Instance?.PlayButtonClick());
            btn.onClick.AddListener(onClick);

            // Label
            GameObject lblGO = new GameObject("Label");
            lblGO.transform.SetParent(btnGO.transform, false);
            RectTransform lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;

            TextMeshProUGUI t = lblGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset btnFont = GameFont.GetUITMP();
            if (btnFont != null) t.font = btnFont;
            t.text      = label;
            t.fontSize  = fontSize;
            t.fontStyle = FontStyles.Normal;
            t.color     = new Color(0.12f, 0.12f, 0.18f, 1f);
            t.alignment = TextAlignmentOptions.Center;
            t.overflowMode = TextOverflowModes.Overflow;
            t.enableWordWrapping = false;
            t.margin = new Vector4(0f, 8f, 0f, 8f);

            // Button-tier outline (between HUD and Announcement)
            t.outlineWidth = 0.1f;
            Color btnTextColor = t.color;
            t.outlineColor = (Color32)Color.Lerp(btnTextColor, Color.black, 0.5f);

            return btnGO;
        }
    }
}
