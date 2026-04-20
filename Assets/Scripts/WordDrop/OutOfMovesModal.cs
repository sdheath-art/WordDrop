using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Modal shown when LevelController fires OnLevelFail (moves exhausted, target missed).
    ///
    /// Displays: "Out of Moves" banner, score vs target, shortfall, and
    /// +5 Moves (stub — Phase 7 wires cost) / Retry (spends a heart) / Menu buttons.
    ///
    /// Canvas sortingOrder = 150 (same as LevelCompletedModal). Only one modal can
    /// show at a time — LevelController's single-fire guards ensure this.
    ///
    /// Phase 4 scope: functional UX. Phase 7 wires the +5 Moves booster cost
    /// and heart-refill flow. Phase 11 can polish animation.
    /// </summary>
    public class OutOfMovesModal : MonoBehaviour
    {
        public static OutOfMovesModal Instance { get; private set; }

        private Canvas _canvas;
        private Text _scoreValueText;
        private Text _targetValueText;
        private Text _shortfallText;
        private Text _heartsText;
        private LevelData _levelData;

        private static readonly Color PANEL_BG = new Color(0.10f, 0.08f, 0.20f, 0.95f);
        private static readonly Color CARD_BG  = new Color(0.20f, 0.14f, 0.18f, 1f);
        private static readonly Color TITLE    = new Color(1.00f, 0.56f, 0.56f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            BuildUI();
            SetVisible(false);

            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelFail += HandleLevelFail;
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelFail -= HandleLevelFail;
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("OutOfMovesCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 150;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

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

            GameObject card = new GameObject("Card");
            card.transform.SetParent(canvasGO.transform, false);
            RectTransform cRT = card.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0.08f, 0.24f);
            cRT.anchorMax = new Vector2(0.92f, 0.76f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            Image cImg = card.AddComponent<Image>();
            cImg.color = CARD_BG;

            var title = CreateLabel(card.transform, "Title",
                new Vector2(0.05f, 0.80f), new Vector2(0.95f, 0.95f),
                "OUT OF MOVES", 40, TITLE);
            title.fontStyle = FontStyle.Bold;

            CreateLabel(card.transform, "SubHint",
                new Vector2(0.05f, 0.68f), new Vector2(0.95f, 0.78f),
                "So close — don't give up.", 20, new Color(0.90f, 0.85f, 0.80f, 1f));

            CreateLabel(card.transform, "ScoreLabel",
                new Vector2(0.08f, 0.56f), new Vector2(0.48f, 0.64f),
                "Score", 18, new Color(0.75f, 0.72f, 0.82f, 1f));
            CreateLabel(card.transform, "TargetLabel",
                new Vector2(0.52f, 0.56f), new Vector2(0.92f, 0.64f),
                "Target", 18, new Color(0.75f, 0.72f, 0.82f, 1f));

            _scoreValueText = CreateLabel(card.transform, "ScoreValue",
                new Vector2(0.08f, 0.46f), new Vector2(0.48f, 0.58f),
                "0", 36, Color.white);
            _scoreValueText.fontStyle = FontStyle.Bold;

            var targetValue = CreateLabel(card.transform, "TargetValue",
                new Vector2(0.52f, 0.46f), new Vector2(0.92f, 0.58f),
                "0", 36, new Color(1f, 0.84f, 0.25f, 1f));
            targetValue.fontStyle = FontStyle.Bold;

            _shortfallText = CreateLabel(card.transform, "Shortfall",
                new Vector2(0.08f, 0.36f), new Vector2(0.92f, 0.44f),
                "", 18, new Color(1f, 0.70f, 0.70f, 1f));

            _heartsText = CreateLabel(card.transform, "Hearts",
                new Vector2(0.08f, 0.30f), new Vector2(0.92f, 0.36f),
                "", 16, new Color(0.90f, 0.85f, 0.80f, 1f));

            // Buttons
            float btnY0 = 0.08f;
            float btnY1 = 0.22f;
            MenuUI.CreateButton(card.transform, "BtnMenu",
                new Vector2(0.06f, btnY0), new Vector2(0.32f, btnY1),
                "MENU", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 22,
                OnMenuClicked);
            MenuUI.CreateButton(card.transform, "BtnBooster",
                new Vector2(0.37f, btnY0), new Vector2(0.63f, btnY1),
                "+5 MOVES", new Color(0.55f, 0.45f, 0.80f, 1f), Color.white, 20,
                OnBoosterClicked);
            MenuUI.CreateButton(card.transform, "BtnRetry",
                new Vector2(0.68f, btnY0), new Vector2(0.94f, btnY1),
                "RETRY", new Color(0.85f, 0.45f, 0.35f, 1f), Color.white, 22,
                OnRetryClicked);

            _targetValueText = targetValue;
        }

        // ── Event handling ──────────────────────────────────────────────────────

        private void HandleLevelFail(int score, int shortfall)
        {
            _levelData = LevelController.Instance?.CurrentLevel;
            if (_levelData != null)
                LevelProgressManager.IncrementAttempts(_levelData.levelId);

            _scoreValueText.text = score.ToString();
            if (_targetValueText != null && _levelData != null)
                _targetValueText.text = _levelData.target.ToString();
            _shortfallText.text = $"You needed {shortfall} more to clear the target.";
            _heartsText.text = $"Hearts: {HeartsManager.Current}/{HeartsManager.MAX_HEARTS} — Retry spends 1.";

            SetVisible(true);
        }

        // ── Button actions ──────────────────────────────────────────────────────

        private void OnRetryClicked()
        {
            if (_levelData == null) { OnMenuClicked(); return; }

            if (!HeartsManager.Consume())
            {
                _heartsText.text = "No hearts left! Wait for regen or tap MENU.";
                return;
            }

            SetVisible(false);
            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;
            GameManager.CurrentMode = GameMode.Level;

            LevelController.Instance.StartLevel(_levelData);
            AnalyticsManager.Log(LevelAnalyticsEvents.LEVEL_RETRY,
                "level_id", _levelData.levelId);

            // TransitionTo(Playing) handles Playing→Playing as a restart and
            // re-enables HandManager input inside OnStateEntered.
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnBoosterClicked()
        {
            // Phase 7 wires: cost 10 coins OR rewarded ad. Phase 4 stub just logs.
            Debug.Log("[OutOfMovesModal] +5 MOVES stub — wired in Phase 7 (cost 10 coins or rewarded ad).");
            _heartsText.text = "+5 Moves unlocks in Phase 7.";
        }

        private void OnMenuClicked()
        {
            SetVisible(false);
            if (LevelController.Instance != null)
                LevelController.Instance.AbortLevel();
            GameManager.CurrentMode = GameMode.Survival;
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Menu);
            if (LevelSelectScreen.Instance != null)
                LevelSelectScreen.Instance.SetVisible(true);
        }

        // ── UI plumbing ─────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            if (_canvas != null) _canvas.gameObject.SetActive(visible);
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
