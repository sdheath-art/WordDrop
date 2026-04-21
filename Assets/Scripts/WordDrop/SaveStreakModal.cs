using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Life raft for a broken daily streak: the player has a live streak but
    /// missed yesterday, so their next play would reset to 1. Offers a rewarded
    /// ad (3s stub) to restore the streak. Capped to once per rolling 7 days
    /// via DailyDropManager.CanSaveStreak.
    ///
    /// Flow:
    ///   1. MenuUI.OnDailyClicked detects CanSaveStreak == true → shows this modal.
    ///   2. WATCH AD → 3s stub → DailyDropManager.RestoreStreakWithAd() → MenuUI.BeginDailyLevel().
    ///   3. SKIP → closes modal → MenuUI.BeginDailyLevel() runs anyway (streak resets
    ///      normally on MarkPlayedToday).
    ///
    /// Canvas sortingOrder = 150.
    /// </summary>
    public class SaveStreakModal : MonoBehaviour
    {
        public static SaveStreakModal Instance { get; private set; }

        private const float REWARDED_AD_STUB_SECONDS = 3f;

        private Canvas _canvas;
        private GameObject _card;
        private Text _streakText;
        private TextMeshProUGUI _watchLabel;
        private bool _adBusy;
        private Coroutine _adCoroutine;

        private static readonly Color PANEL_BG = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG  = new Color(0.25f, 0.18f, 0.28f, 1f);
        private static readonly Color TITLE    = new Color(1.00f, 0.60f, 0.35f, 1f);

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
                _adBusy = false;
                RefreshStatus();
                if (_card != null) UIAnimations.PopIn(_card.transform);
                return;
            }
            // Cancel any pending ad-stub so re-opening doesn't queue a second
            // RestoreStreakWithAd grant. Same pattern as HeartWaitModal.
            if (_adCoroutine != null) { StopCoroutine(_adCoroutine); _adCoroutine = null; }
            _adBusy = false;
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

        private void RefreshStatus()
        {
            int streak = PlayerPrefs.GetInt("daily_streak", 0);
            if (_streakText != null)
                _streakText.text = $"Your ★ {streak}-day streak is about to reset.";
            if (_watchLabel != null)
                _watchLabel.text = _adBusy ? "WATCHING…" : "WATCH AD";
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("SaveStreakCanvas");
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
                "SAVE YOUR STREAK", 30, TITLE);
            title.fontStyle = FontStyle.Bold;

            _streakText = CreateLabel(card.transform, "StreakLine",
                new Vector2(0.05f, 0.56f), new Vector2(0.95f, 0.74f),
                "", 20, new Color(0.90f, 0.88f, 0.80f, 1f));

            CreateLabel(card.transform, "PitchLine",
                new Vector2(0.05f, 0.42f), new Vector2(0.95f, 0.56f),
                "Watch a short ad to keep it going.", 18,
                new Color(0.75f, 0.80f, 0.90f, 1f));

            // Buttons
            float btnY0 = 0.10f;
            float btnY1 = 0.24f;
            MenuUI.CreateButton(card.transform, "BtnSkip",
                new Vector2(0.08f, btnY0), new Vector2(0.44f, btnY1),
                "SKIP", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 22,
                OnSkipClicked);

            int before = card.transform.childCount;
            MenuUI.CreateButton(card.transform, "BtnWatchAd",
                new Vector2(0.48f, btnY0), new Vector2(0.92f, btnY1),
                "WATCH AD", UILayout.Gold, Color.black, 22,
                OnWatchAdClicked);
            if (card.transform.childCount > before)
                _watchLabel = card.transform.GetChild(card.transform.childCount - 1)
                    .GetComponentInChildren<TextMeshProUGUI>();
        }

        private void OnWatchAdClicked()
        {
            if (_adBusy) return;
            if (_adCoroutine != null) return;
            if (!DailyDropManager.CanSaveStreak())
            {
                // Cooldown hit or state changed — fall through to SKIP path.
                OnSkipClicked();
                return;
            }
            _adCoroutine = StartCoroutine(WatchAdCoroutine());
        }

        private IEnumerator WatchAdCoroutine()
        {
            _adBusy = true;
            RefreshStatus();
            // TODO(Phase 11): swap for real rewarded-ad SDK callback.
            yield return new WaitForSecondsRealtime(REWARDED_AD_STUB_SECONDS);
            _adBusy = false;
            _adCoroutine = null;

            if (DailyDropManager.RestoreStreakWithAd())
            {
                AnalyticsManager.Log(LevelAnalyticsEvents.BOOSTER_USED,
                    "booster", "save_streak",
                    "source", "rewarded_ad");
            }

            SetVisible(false, onHidden: () =>
            {
                if (MenuUI.Instance != null)
                    MenuUI.Instance.BeginDailyLevel();
            });
        }

        private void OnSkipClicked()
        {
            // Player declined — proceed with a fresh streak (MarkPlayedToday
            // will reset it to 1 when they finish the puzzle).
            SetVisible(false, onHidden: () =>
            {
                if (MenuUI.Instance != null)
                    MenuUI.Instance.BeginDailyLevel();
            });
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
