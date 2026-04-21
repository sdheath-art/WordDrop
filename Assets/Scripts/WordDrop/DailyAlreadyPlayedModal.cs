using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Shown when the player taps TODAY'S PUZZLE after having already completed
    /// it for the current UTC day. Displays today's score, current streak, and
    /// a live countdown to the next daily unlock (UTC midnight).
    ///
    /// Canvas sortingOrder = 150 — same tier as the other level modals.
    /// </summary>
    public class DailyAlreadyPlayedModal : MonoBehaviour
    {
        public static DailyAlreadyPlayedModal Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _card;
        private Text _scoreText;
        private Text _streakText;
        private Text _countdownText;
        private Coroutine _tickCoroutine;

        private static readonly Color PANEL_BG = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG  = new Color(0.14f, 0.20f, 0.30f, 1f);
        private static readonly Color TITLE    = new Color(0.60f, 0.85f, 1.00f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);
        }

        public void SetVisible(bool visible)
        {
            SetVisible(visible, null);
        }

        public void SetVisible(bool visible, System.Action onHidden)
        {
            if (_canvas == null) { onHidden?.Invoke(); return; }
            if (visible)
            {
                _canvas.gameObject.SetActive(true);
                var raycaster = _canvas.GetComponent<GraphicRaycaster>();
                if (raycaster != null) raycaster.enabled = true;
                RefreshStatus();
                if (_card != null) UIAnimations.PopIn(_card.transform);
                if (_tickCoroutine == null && gameObject.activeInHierarchy)
                    _tickCoroutine = StartCoroutine(TickCountdown());
                return;
            }
            if (_tickCoroutine != null) { StopCoroutine(_tickCoroutine); _tickCoroutine = null; }
            if (!_canvas.gameObject.activeSelf) { onHidden?.Invoke(); return; }
            var rc = _canvas.GetComponent<GraphicRaycaster>();
            if (rc != null) rc.enabled = false;
            if (_card == null)
            {
                _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
                return;
            }
            UIAnimations.PopOut(_card.transform, onComplete: () =>
            {
                if (_canvas != null) _canvas.gameObject.SetActive(false);
                onHidden?.Invoke();
            });
        }

        private IEnumerator TickCountdown()
        {
            var wait = new WaitForSecondsRealtime(1f);
            while (_canvas != null && _canvas.gameObject.activeSelf)
            {
                RefreshStatus();
                yield return wait;
            }
            _tickCoroutine = null;
        }

        private void RefreshStatus()
        {
            int score = DailyDropManager.GetTodayBest();
            int streak = DailyDropManager.GetStreak();
            if (_scoreText != null) _scoreText.text = $"Today's score: {score}";
            if (_streakText != null)
                _streakText.text = streak > 0
                    ? $"🔥 {streak} day{(streak == 1 ? "" : "s")} streak"
                    : "";
            if (_countdownText != null)
            {
                TimeSpan until = TimeUntilNextUtcMidnight();
                _countdownText.text =
                    $"Next puzzle in {(int)until.TotalHours:00}:{until.Minutes:00}:{until.Seconds:00}";
            }
        }

        /// <summary>
        /// Time from now until the next UTC midnight — when the daily seed rolls
        /// to the next calendar day (same globally, matching DailyDropManager's
        /// UTC-based seed/play-tracking).
        /// </summary>
        private static TimeSpan TimeUntilNextUtcMidnight()
        {
            DateTime now = DateTime.UtcNow;
            DateTime nextMidnight = now.Date.AddDays(1);
            TimeSpan remaining = nextMidnight - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("DailyAlreadyPlayedCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject dim = new GameObject("Dim");
            dim.transform.SetParent(canvasGO.transform, false);
            RectTransform dRT = dim.AddComponent<RectTransform>();
            dRT.anchorMin = Vector2.zero;
            dRT.anchorMax = Vector2.one;
            dRT.offsetMin = Vector2.zero;
            dRT.offsetMax = Vector2.zero;
            Image dImg = dim.AddComponent<Image>();
            dImg.color = PANEL_BG;
            dImg.raycastTarget = true;

            _card = new GameObject("Card");
            _card.transform.SetParent(canvasGO.transform, false);
            RectTransform cRT = _card.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0.10f, 0.30f);
            cRT.anchorMax = new Vector2(0.90f, 0.70f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            Image cImg = _card.AddComponent<Image>();
            cImg.color = CARD_BG;
            GameObject card = _card;

            var title = CreateLabel(card.transform, "Title",
                new Vector2(0.05f, 0.78f), new Vector2(0.95f, 0.94f),
                "COME BACK TOMORROW", 32, TITLE);
            title.fontStyle = FontStyle.Bold;

            _scoreText = CreateLabel(card.transform, "Score",
                new Vector2(0.05f, 0.60f), new Vector2(0.95f, 0.72f),
                "Today's score: 0", 24, Color.white);

            _streakText = CreateLabel(card.transform, "Streak",
                new Vector2(0.05f, 0.50f), new Vector2(0.95f, 0.60f),
                "", 20, UILayout.Gold);

            CreateLabel(card.transform, "CountdownLabel",
                new Vector2(0.05f, 0.34f), new Vector2(0.95f, 0.42f),
                "A new puzzle drops at UTC midnight", 16,
                new Color(0.75f, 0.80f, 0.90f, 1f));

            _countdownText = CreateLabel(card.transform, "Countdown",
                new Vector2(0.05f, 0.24f), new Vector2(0.95f, 0.34f),
                "--:--:--", 26, Color.white);
            _countdownText.fontStyle = FontStyle.Bold;

            MenuUI.CreateButton(card.transform, "BtnClose",
                new Vector2(0.30f, 0.06f), new Vector2(0.70f, 0.18f),
                "OK", new Color(0.25f, 0.55f, 0.85f, 1f), Color.white, 22,
                OnCloseClicked);
        }

        private void OnCloseClicked()
        {
            SetVisible(false, null);
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
    }
}
