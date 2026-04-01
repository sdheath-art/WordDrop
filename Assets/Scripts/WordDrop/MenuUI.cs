using UnityEngine;
using UnityEngine.UI;

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

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // Build UI in Awake and hide immediately.
            // SceneBootstrap.Start() transitions to Playing without showing the menu.
            BuildUI();
            SetVisible(false);
            Debug.Log("[MenuUI] Awake — panel built and hidden");
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("MenuCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            _panel = CreatePanel(canvasGO.transform, "MenuPanel",
                new Color(0.08f, 0.08f, 0.10f, 0.98f));

            // Title
            GameObject titleGO = new GameObject("TitleText");
            titleGO.transform.SetParent(_panel.transform, false);

            RectTransform titleRT = titleGO.AddComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.82f);
            titleRT.anchorMax = new Vector2(0.95f, 0.95f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            Text titleText      = titleGO.AddComponent<Text>();
            titleText.font      = GetFont();
            titleText.text      = "WordDrop";
            titleText.fontSize  = 72;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color     = new Color(0.96f, 0.84f, 0.25f, 1f);
            titleText.alignment = TextAnchor.MiddleCenter;

            // Subtitle
            GameObject subGO = new GameObject("SubtitleText");
            subGO.transform.SetParent(_panel.transform, false);

            RectTransform subRT = subGO.AddComponent<RectTransform>();
            subRT.anchorMin = new Vector2(0.05f, 0.76f);
            subRT.anchorMax = new Vector2(0.95f, 0.82f);
            subRT.offsetMin = Vector2.zero;
            subRT.offsetMax = Vector2.zero;

            Text subText      = subGO.AddComponent<Text>();
            subText.font      = GetFont();
            subText.text      = "A Word Game With Explosions";
            subText.fontSize  = 28;
            subText.fontStyle = FontStyle.Italic;
            subText.color     = new Color(0.70f, 0.70f, 0.75f, 1f);
            subText.alignment = TextAnchor.MiddleCenter;

            // Rules blurb
            GameObject rulesGO = new GameObject("RulesText");
            rulesGO.transform.SetParent(_panel.transform, false);

            RectTransform rulesRT = rulesGO.AddComponent<RectTransform>();
            rulesRT.anchorMin = new Vector2(0.05f, 0.42f);
            rulesRT.anchorMax = new Vector2(0.95f, 0.75f);
            rulesRT.offsetMin = Vector2.zero;
            rulesRT.offsetMax = Vector2.zero;

            Text rulesText        = rulesGO.AddComponent<Text>();
            rulesText.font        = GetFont();
            rulesText.text        =
                "Drop letters into a shared board.\n" +
                "Form words to score and PRIME them.\n\n" +
                "Primed words are bombs — reuse a\n" +
                "primed tile in a new word to DETONATE.\n\n" +
                "Chains react. Longer fuses pay more.\n" +
                "12 turns each. Highest score wins!";
            rulesText.fontSize    = 26;
            rulesText.color       = new Color(0.85f, 0.85f, 0.88f, 1f);
            rulesText.alignment   = TextAnchor.MiddleCenter;
            rulesText.lineSpacing = 1.25f;

            // Difficulty selector
            _difficultyText = CreateLabel(_panel.transform, "DifficultyLabel",
                anchorMin: new Vector2(0.15f, 0.34f),
                anchorMax: new Vector2(0.85f, 0.40f),
                text: "Difficulty: Easy",
                fontSize: 28,
                color: new Color(0.85f, 0.85f, 0.90f, 1f));

            CreateButton(_panel.transform, "DiffEasy",
                anchorMin: new Vector2(0.08f, 0.27f),
                anchorMax: new Vector2(0.36f, 0.34f),
                label: "EASY", bgColor: new Color(0.25f, 0.65f, 0.35f, 1f),
                textColor: Color.white, fontSize: 22,
                onClick: () => SetDifficulty(0, "Easy"));

            CreateButton(_panel.transform, "DiffMedium",
                anchorMin: new Vector2(0.37f, 0.27f),
                anchorMax: new Vector2(0.63f, 0.34f),
                label: "MEDIUM", bgColor: new Color(0.80f, 0.65f, 0.15f, 1f),
                textColor: Color.white, fontSize: 22,
                onClick: () => SetDifficulty(1, "Medium"));

            CreateButton(_panel.transform, "DiffHard",
                anchorMin: new Vector2(0.64f, 0.27f),
                anchorMax: new Vector2(0.92f, 0.34f),
                label: "HARD", bgColor: new Color(0.80f, 0.25f, 0.20f, 1f),
                textColor: Color.white, fontSize: 22,
                onClick: () => SetDifficulty(2, "Hard"));

            // AI profile selector
            _profileText = CreateLabel(_panel.transform, "ProfileLabel",
                anchorMin: new Vector2(0.15f, 0.19f),
                anchorMax: new Vector2(0.85f, 0.25f),
                text: "AI Style: Scorer",
                fontSize: 22,
                color: new Color(0.75f, 0.75f, 0.80f, 1f));

            float profBtnY0 = 0.12f, profBtnY1 = 0.19f;
            CreateButton(_panel.transform, "ProfScorer",
                anchorMin: new Vector2(0.05f, profBtnY0),
                anchorMax: new Vector2(0.35f, profBtnY1),
                label: "SCORER", bgColor: new Color(0.30f, 0.55f, 0.75f, 1f),
                textColor: Color.white, fontSize: 18,
                onClick: () => SetProfile(AIAgent.AIProfile.Scorer));
            CreateButton(_panel.transform, "ProfBlocker",
                anchorMin: new Vector2(0.36f, profBtnY0),
                anchorMax: new Vector2(0.64f, profBtnY1),
                label: "BLOCKER", bgColor: new Color(0.60f, 0.35f, 0.60f, 1f),
                textColor: Color.white, fontSize: 18,
                onClick: () => SetProfile(AIAgent.AIProfile.Blocker));
            CreateButton(_panel.transform, "ProfHunter",
                anchorMin: new Vector2(0.65f, profBtnY0),
                anchorMax: new Vector2(0.95f, profBtnY1),
                label: "HUNTER", bgColor: new Color(0.75f, 0.30f, 0.25f, 1f),
                textColor: Color.white, fontSize: 18,
                onClick: () => SetProfile(AIAgent.AIProfile.TriggerHunter));

            // PLAY button
            CreateButton(_panel.transform, "PlayButton",
                anchorMin: new Vector2(0.20f, 0.03f),
                anchorMax: new Vector2(0.80f, 0.11f),
                label:     "PLAY",
                bgColor:   new Color(0.20f, 0.72f, 0.35f, 1f),
                textColor: Color.white,
                fontSize:  44,
                onClick:   OnPlayClicked);
        }

        private Text _difficultyText;
        private Text _profileText;

        private void SetProfile(AIAgent.AIProfile profile)
        {
            AIAgent.CurrentProfile = profile;
            if (_profileText != null)
                _profileText.text = $"AI Style: {profile}";
            Debug.Log($"[MenuUI] AI profile set to {profile}");
        }

        private void SetDifficulty(int level, string name)
        {
            AIAgent.Difficulty = level;
            if (_difficultyText != null)
                _difficultyText.text = $"Difficulty: {name}";
            Debug.Log($"[MenuUI] Difficulty set to {name} ({level})");
        }

        private void OnPlayClicked()
        {
            AnalyticsManager.ButtonTap("play");
            AnalyticsManager.ScreenView("playing");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        public void SetVisible(bool visible)
        {
            if (_panel != null) _panel.SetActive(visible);
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
            btn.onClick.AddListener(onClick);

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
            t.fontSize  = fontSize;
            t.fontStyle = FontStyle.Bold;
            t.color     = textColor;
            t.alignment = TextAnchor.MiddleCenter;
        }

        internal static Font GetFont()
        {
            Font f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }
    }
}
