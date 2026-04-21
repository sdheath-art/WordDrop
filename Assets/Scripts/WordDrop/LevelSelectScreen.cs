using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Scrollable vertical list of levels. Each tile shows level number,
    /// earned stars (0–3), and locked/unlocked state. Clicking an unlocked
    /// level starts it (mirrors LevelDebugMenu's ▶ Play flow).
    ///
    /// Canvas sortingOrder = 105 (above MenuUI 100, below modals 150).
    /// Auto-discovers available levels by scanning Resources/Levels at startup.
    /// Phase 4 MVP — simple list, single world. Phase 10/11 can upgrade to an
    /// island-map visual and world theming.
    /// </summary>
    public class LevelSelectScreen : MonoBehaviour
    {
        public static LevelSelectScreen Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _content;
        private Text _headerStatusText;
        private Coroutine _statusCoroutine;
        private readonly List<int> _availableLevels = new List<int>();

        private static readonly Color PANEL_BG    = new Color(0.10f, 0.08f, 0.18f, 1f);
        private static readonly Color HEADER_BG   = new Color(0.16f, 0.12f, 0.28f, 1f);
        private static readonly Color TILE_OPEN   = new Color(0.25f, 0.35f, 0.65f, 1f);
        private static readonly Color TILE_DONE   = new Color(0.20f, 0.55f, 0.35f, 1f);
        private static readonly Color TILE_LOCKED = new Color(0.22f, 0.22f, 0.28f, 1f);
        private static readonly Color TUTORIAL_BADGE_COLOR = new Color(0.30f, 0.80f, 0.95f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            ScanAvailableLevels();
            BuildUI();
            SetVisible(false);
        }

        /// <summary>
        /// Finds every level_{N}.json under Resources/Levels via LoadAll so that
        /// gaps (e.g., dev test levels at 101-103 with nothing between 3 and 100)
        /// don't terminate the scan. Runs once at Awake.
        /// </summary>
        private void ScanAvailableLevels()
        {
            _availableLevels.Clear();
            var assets = Resources.LoadAll<TextAsset>("Levels");
            const string prefix = "level_";
            foreach (var asset in assets)
            {
                if (asset == null || string.IsNullOrEmpty(asset.name)) continue;
                if (!asset.name.StartsWith(prefix)) continue;
                if (int.TryParse(asset.name.Substring(prefix.Length), out int id) && id > 0)
                    _availableLevels.Add(id);
            }
            _availableLevels.Sort();
            Debug.Log($"[LevelSelectScreen] Scanned {_availableLevels.Count} level JSONs: "
                + string.Join(",", _availableLevels));
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("LevelSelectCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 105;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject bg = new GameObject("Background");
            bg.transform.SetParent(canvasGO.transform, false);
            RectTransform bgRT = bg.AddComponent<RectTransform>();
            bgRT.anchorMin = Vector2.zero;
            bgRT.anchorMax = Vector2.one;
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            Image bgImg = bg.AddComponent<Image>();
            bgImg.color = PANEL_BG;
            bgImg.raycastTarget = true;

            // Header bar with title + back button
            GameObject header = new GameObject("Header");
            header.transform.SetParent(canvasGO.transform, false);
            RectTransform hRT = header.AddComponent<RectTransform>();
            hRT.anchorMin = new Vector2(0f, 0.90f);
            hRT.anchorMax = new Vector2(1f, 1f);
            hRT.offsetMin = Vector2.zero;
            hRT.offsetMax = Vector2.zero;
            Image hImg = header.AddComponent<Image>();
            hImg.color = HEADER_BG;

            var title = CreateLabel(header.transform, "Title",
                new Vector2(0.15f, 0f), new Vector2(0.85f, 1f),
                "LEVELS", 36, new Color(1f, 0.84f, 0.25f, 1f));
            title.fontStyle = FontStyle.Bold;

            MenuUI.CreateButton(header.transform, "BtnBack",
                new Vector2(0.02f, 0.15f), new Vector2(0.14f, 0.85f),
                "← BACK", new Color(0.35f, 0.35f, 0.45f, 1f), Color.white, 16,
                OnBackClicked);

            // Header status line — hearts + coins. Right-aligned, refreshes on
            // SetVisible and once per second while the screen is open so the
            // regen timer stays honest without an always-on Update loop.
            _headerStatusText = CreateLabel(header.transform, "HeaderStatus",
                new Vector2(0.65f, 0f), new Vector2(0.98f, 1f),
                "", 20, UILayout.Gold);
            _headerStatusText.alignment = TextAnchor.MiddleRight;
            _headerStatusText.fontStyle = FontStyle.Bold;

            // ScrollRect for the level tiles
            GameObject scrollGO = new GameObject("Scroll");
            scrollGO.transform.SetParent(canvasGO.transform, false);
            RectTransform scrollRT = scrollGO.AddComponent<RectTransform>();
            scrollRT.anchorMin = new Vector2(0f, 0f);
            scrollRT.anchorMax = new Vector2(1f, 0.90f);
            scrollRT.offsetMin = Vector2.zero;
            scrollRT.offsetMax = Vector2.zero;
            Image scrollBg = scrollGO.AddComponent<Image>();
            scrollBg.color = new Color(0.08f, 0.06f, 0.15f, 1f);

            ScrollRect scroll = scrollGO.AddComponent<ScrollRect>();
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;

            // Viewport
            GameObject viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollGO.transform, false);
            RectTransform vRT = viewport.AddComponent<RectTransform>();
            vRT.anchorMin = Vector2.zero;
            vRT.anchorMax = Vector2.one;
            vRT.offsetMin = new Vector2(16f, 16f);
            vRT.offsetMax = new Vector2(-16f, -16f);
            Image vImg = viewport.AddComponent<Image>();
            vImg.color = new Color(0f, 0f, 0f, 0.01f);
            Mask mask = viewport.AddComponent<Mask>();
            mask.showMaskGraphic = false;
            scroll.viewport = vRT;

            // Content
            _content = new GameObject("Content");
            _content.transform.SetParent(viewport.transform, false);
            RectTransform cRT = _content.AddComponent<RectTransform>();
            cRT.anchorMin = new Vector2(0f, 1f);
            cRT.anchorMax = new Vector2(1f, 1f);
            cRT.pivot = new Vector2(0.5f, 1f);
            cRT.offsetMin = Vector2.zero;
            cRT.offsetMax = Vector2.zero;
            VerticalLayoutGroup vlg = _content.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 12f;
            vlg.padding = new RectOffset(8, 8, 8, 8);
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlWidth = true;
            vlg.childControlHeight = false;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            ContentSizeFitter csf = _content.AddComponent<ContentSizeFitter>();
            csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scroll.content = cRT;

            BuildLevelTiles();
        }

        private void BuildLevelTiles()
        {
            if (_content == null) return;
            foreach (Transform child in _content.transform)
                Destroy(child.gameObject);

            int highestUnlocked = LevelProgressManager.GetHighestUnlocked();
            int highestExisting = _availableLevels.Count > 0
                ? _availableLevels[_availableLevels.Count - 1] : 0;
            int displayMax = Mathf.Max(20, highestExisting);

            for (int id = 1; id <= displayMax; id++)
            {
                bool exists = _availableLevels.Contains(id);
                bool unlocked = id <= highestUnlocked;
                int stars = exists ? LevelProgressManager.GetStars(id) : 0;
                bool completed = exists && LevelProgressManager.IsCompleted(id);

                CreateLevelTile(id, exists, unlocked, stars, completed);
            }
        }

        private void CreateLevelTile(int id, bool exists, bool unlocked, int stars, bool completed)
        {
            GameObject tile = new GameObject($"Level{id}Tile", typeof(RectTransform));
            tile.transform.SetParent(_content.transform, false);

            LayoutElement le = tile.AddComponent<LayoutElement>();
            le.minHeight = 88f;
            le.preferredHeight = 88f;

            // RectTransform was created in the GameObject constructor above.
            // VerticalLayoutGroup on the parent controls positioning;
            // we don't need to set anchors explicitly here.

            bool playable = exists && unlocked;
            Color tileColor = !exists ? TILE_LOCKED
                : !unlocked ? TILE_LOCKED
                : completed ? TILE_DONE : TILE_OPEN;

            Image bg = tile.AddComponent<Image>();
            bg.color = tileColor;

            // Level number
            var idLabel = CreateLabel(tile.transform, "Num",
                new Vector2(0.04f, 0.10f), new Vector2(0.30f, 0.90f),
                id.ToString(), 34, playable ? Color.white : new Color(0.55f, 0.55f, 0.60f, 1f));
            idLabel.fontStyle = FontStyle.Bold;

            // Status line (stars or locked or missing)
            string statusText;
            if (!exists) statusText = "— not built yet —";
            else if (!unlocked) statusText = "🔒 locked";
            else if (stars > 0) statusText = RenderStars(stars);
            else statusText = "☆ ☆ ☆";
            CreateLabel(tile.transform, "Status",
                new Vector2(0.32f, 0.10f), new Vector2(0.80f, 0.90f),
                statusText, 20, playable ? new Color(1f, 0.90f, 0.55f, 1f) : new Color(0.60f, 0.60f, 0.65f, 1f));

            // Tutorial badge on L1-3 — subtle indicator that these are onboarding levels.
            if (TutorialProgression.IsTutorialLevel(id))
            {
                var badge = CreateLabel(tile.transform, "TutorialBadge",
                    new Vector2(0.82f, 0.55f), new Vector2(0.98f, 0.90f),
                    "TUTORIAL", 11, TUTORIAL_BADGE_COLOR);
                badge.alignment = TextAnchor.MiddleRight;
            }

            if (playable)
            {
                Button btn = tile.AddComponent<Button>();
                ColorBlock cb = btn.colors;
                cb.highlightedColor = Color.Lerp(tileColor, Color.white, 0.15f);
                cb.pressedColor = Color.Lerp(tileColor, Color.black, 0.20f);
                cb.normalColor = tileColor;
                btn.colors = cb;
                int capturedId = id;
                btn.onClick.AddListener(() => OnLevelTileClicked(capturedId));
            }
        }

        private static string RenderStars(int stars)
        {
            stars = Mathf.Clamp(stars, 0, 3);
            return new string('★', stars) + new string('☆', 3 - stars);
        }

        // ── Actions ─────────────────────────────────────────────────────────────

        private void OnLevelTileClicked(int levelId)
        {
            LevelData data = LevelLoader.Load(levelId);
            if (data == null)
            {
                Debug.LogError($"[LevelSelectScreen] Load failed for level {levelId}.");
                return;
            }
            var (ok, reason) = LevelValidator.Validate(data);
            if (!ok)
            {
                Debug.LogError($"[LevelSelectScreen] Level {levelId} invalid: {reason}");
                return;
            }

            if (!HeartsManager.Consume())
            {
                // Phase 7: surface the life-gate instead of silently dropping the tap.
                // LevelSelect is the source, so CLOSE returns here (Phase 8 return-context routing).
                SetVisible(false);
                if (HeartWaitModal.Instance != null)
                    HeartWaitModal.Instance.SetVisible(true, HeartWaitModal.ReturnContext.LevelSelect);
                else
                    Debug.LogWarning("[LevelSelectScreen] HeartWaitModal missing — level tap dropped.");
                return;
            }

            SetVisible(false);

            SurvivalManager.IsSurvivalMode = false;
            BlitzManager.IsBlitzMode = false;
            DailyDropManager.IsDailyMode = false;
            GameManager.CurrentMode = GameMode.Level;

            LevelController.Instance.StartLevel(data);
            LevelProgressManager.IncrementAttempts(levelId);

            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        private void OnBackClicked()
        {
            SetVisible(false);
            if (MenuUI.Instance != null)
                MenuUI.Instance.SetVisible(true);
        }

        // ── Public API ──────────────────────────────────────────────────────────

        public void SetVisible(bool visible)
        {
            if (_canvas == null) return;
            if (visible) BuildLevelTiles(); // refresh progress on each open
            _canvas.gameObject.SetActive(visible);

            // Hearts regen over wall-clock time, so the header status needs to
            // tick while the player sits here. Only runs while the screen is up.
            if (visible)
            {
                RefreshHeaderStatus();
                if (_statusCoroutine == null && gameObject.activeInHierarchy)
                    _statusCoroutine = StartCoroutine(HeaderStatusTick());

                // One-shot Starter Pack pitch after tutorial complete. The modal
                // self-gates on PlayerPrefs so repeat SetVisible calls are safe.
                if (StarterPackModal.Instance != null)
                    StarterPackModal.Instance.TryAutoShow();
            }
            else if (_statusCoroutine != null)
            {
                StopCoroutine(_statusCoroutine);
                _statusCoroutine = null;
            }
        }

        private IEnumerator HeaderStatusTick()
        {
            var wait = new WaitForSecondsRealtime(1f);
            while (_canvas != null && _canvas.gameObject.activeSelf)
            {
                RefreshHeaderStatus();
                yield return wait;
            }
            _statusCoroutine = null;
        }

        private void RefreshHeaderStatus()
        {
            if (_headerStatusText == null) return;
            int hearts = HeartsManager.Current;
            int coins = CoinWallet.Balance;
            _headerStatusText.text = $"♥ {hearts}/{HeartsManager.MAX_HEARTS}   🪙 {coins}";
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
            t.alignment = TextAnchor.MiddleLeft;
            return t;
        }
    }
}
