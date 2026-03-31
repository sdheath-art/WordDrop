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
        private static readonly Color PANEL_BG       = new Color(0.118f, 0.173f, 0.412f, 0.98f); // board frame dark
        private static readonly Color OVERLAY_COLOR   = new Color(0f, 0f, 0f, 0.75f);
        private static readonly Color PLAYER_GREEN    = new Color(0.200f, 0.851f, 0.424f, 1f);
        private static readonly Color AI_ORANGE       = new Color(1.000f, 0.604f, 0.239f, 1f);
        private static readonly Color GOLD            = new Color(0.961f, 0.761f, 0.294f, 1f);
        private static readonly Color DEFEAT_RED      = new Color(0.90f, 0.30f, 0.30f, 1f);
        private static readonly Color TEXT_WHITE      = new Color(0.95f, 0.95f, 1f, 1f);
        private static readonly Color TEXT_DIM        = new Color(0.65f, 0.65f, 0.75f, 1f);
        private static readonly Color BTN_COLOR       = new Color(0.961f, 0.761f, 0.294f, 1f); // gold CTA

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

        // Elements for stagger animation
        private GameObject[] _staggerElements;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            SetVisible(false);
            Debug.Log("[GameOverUI] Awake — panel built and hidden");
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
            _panelRT.sizeDelta = new Vector2(460f, 420f);
            _panelHomePos = Vector2.zero;

            // Panel background with rounded corners
            Image panelImg = _panel.AddComponent<Image>();
            panelImg.sprite = TileRenderer.CreateSolidRoundedRect(1024, 1024, 120, Color.white);
            panelImg.type = Image.Type.Simple;
            panelImg.color = PANEL_BG;

            // ── Content ──
            var stagger = new System.Collections.Generic.List<GameObject>();

            // Title: "GAME OVER"
            _titleText = MakeLabel(_panel.transform, "Title",
                new Vector2(0.05f, 0.78f), new Vector2(0.95f, 0.95f),
                "GAME OVER", 48, FontStyle.Normal, GOLD, TextAnchor.MiddleCenter);
            stagger.Add(_titleText.gameObject);

            // Winner text
            _winnerText = MakeLabel(_panel.transform, "Winner",
                new Vector2(0.05f, 0.62f), new Vector2(0.95f, 0.78f),
                "PLAYER WINS!", 34, FontStyle.Normal, PLAYER_GREEN, TextAnchor.MiddleCenter);
            stagger.Add(_winnerText.gameObject);

            // Player score
            _playerScoreText = MakeLabel(_panel.transform, "PlayerScore",
                new Vector2(0.05f, 0.48f), new Vector2(0.95f, 0.62f),
                "Player: 0", 30, FontStyle.Normal, PLAYER_GREEN, TextAnchor.MiddleCenter);
            stagger.Add(_playerScoreText.gameObject);

            // AI score
            _aiScoreText = MakeLabel(_panel.transform, "AIScore",
                new Vector2(0.05f, 0.36f), new Vector2(0.95f, 0.48f),
                "AI: 0", 30, FontStyle.Normal, AI_ORANGE, TextAnchor.MiddleCenter);
            stagger.Add(_aiScoreText.gameObject);

            // Turns played
            _turnsPlayedText = MakeLabel(_panel.transform, "Turns",
                new Vector2(0.05f, 0.26f), new Vector2(0.95f, 0.36f),
                "", 20, FontStyle.Normal, TEXT_DIM, TextAnchor.MiddleCenter);
            stagger.Add(_turnsPlayedText.gameObject);

            // Play Again button
            GameObject btnGO = MakeButton(_panel.transform, "PlayAgain",
                new Vector2(0.15f, 0.06f), new Vector2(0.85f, 0.22f),
                "PLAY AGAIN", 32, BTN_COLOR, OnPlayAgainClicked);
            stagger.Add(btnGO);

            _staggerElements = stagger.ToArray();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SHOW — whoosh in from bottom
        // ═══════════════════════════════════════════════════════════════════════════

        public void Show()
        {
            // Gather data
            int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            int aiScore     = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore : 0;
            int totalTurns  = MatchController.Instance != null ? MatchController.MAX_TURNS * 2 : 0;

            // Determine winner
            string winnerLabel; Color winnerColor;
            if (playerScore > aiScore)
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

            // Populate
            if (_titleText != null) _titleText.text = "GAME OVER";
            if (_winnerText != null) { _winnerText.text = winnerLabel; _winnerText.color = winnerColor; }
            if (_playerScoreText != null) _playerScoreText.text = $"Player:  {playerScore} pts";
            if (_aiScoreText != null) _aiScoreText.text = $"AI:  {aiScore} pts";
            if (_turnsPlayedText != null) _turnsPlayedText.text = $"{totalTurns} turns played";

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
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // HIDE — whoosh out to bottom
        // ═══════════════════════════════════════════════════════════════════════════

        private void OnPlayAgainClicked()
        {
            AnalyticsManager.ButtonTap("play_again");
            StartCoroutine(HideAnimation());
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
            }
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
            TMPHelper.ApplyEffects(t, t.color);

            return btnGO;
        }
    }
}
