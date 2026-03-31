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
            titleRT.anchorMin = new Vector2(0.05f, 0.72f);
            titleRT.anchorMax = new Vector2(0.95f, 0.90f);
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
            subRT.anchorMin = new Vector2(0.05f, 0.63f);
            subRT.anchorMax = new Vector2(0.95f, 0.72f);
            subRT.offsetMin = Vector2.zero;
            subRT.offsetMax = Vector2.zero;

            Text subText      = subGO.AddComponent<Text>();
            subText.font      = GetFont();
            subText.text      = "Scrabble meets Connect Four";
            subText.fontSize  = 28;
            subText.fontStyle = FontStyle.Italic;
            subText.color     = new Color(0.70f, 0.70f, 0.75f, 1f);
            subText.alignment = TextAnchor.MiddleCenter;

            // Rules blurb
            GameObject rulesGO = new GameObject("RulesText");
            rulesGO.transform.SetParent(_panel.transform, false);

            RectTransform rulesRT = rulesGO.AddComponent<RectTransform>();
            rulesRT.anchorMin = new Vector2(0.05f, 0.32f);
            rulesRT.anchorMax = new Vector2(0.95f, 0.62f);
            rulesRT.offsetMin = Vector2.zero;
            rulesRT.offsetMax = Vector2.zero;

            Text rulesText        = rulesGO.AddComponent<Text>();
            rulesText.font        = GetFont();
            rulesText.text        =
                "Draw letters from a Scrabble bag.\n" +
                "Tap a column to drop your tile — it falls\n" +
                "to the lowest empty row, Connect Four style.\n\n" +
                "Spell valid words (3+ letters) in any line\n" +
                "to score points equal to their Scrabble values.\n\n" +
                "You vs AI — 30 turns each.\n" +
                "Highest score wins!";
            rulesText.fontSize    = 26;
            rulesText.color       = new Color(0.85f, 0.85f, 0.88f, 1f);
            rulesText.alignment   = TextAnchor.MiddleCenter;
            rulesText.lineSpacing = 1.25f;

            // Letter value hint
            GameObject hintGO = new GameObject("HintText");
            hintGO.transform.SetParent(_panel.transform, false);

            RectTransform hintRT = hintGO.AddComponent<RectTransform>();
            hintRT.anchorMin = new Vector2(0.05f, 0.24f);
            hintRT.anchorMax = new Vector2(0.95f, 0.32f);
            hintRT.offsetMin = Vector2.zero;
            hintRT.offsetMax = Vector2.zero;

            Text hintText      = hintGO.AddComponent<Text>();
            hintText.font      = GetFont();
            hintText.text      = "Q=10  Z=10  J=8  X=8  K=5  •  A/E/I/O/U=1";
            hintText.fontSize  = 22;
            hintText.fontStyle = FontStyle.Italic;
            hintText.color     = new Color(0.96f, 0.84f, 0.25f, 0.75f);
            hintText.alignment = TextAnchor.MiddleCenter;

            // PLAY button
            CreateButton(_panel.transform, "PlayButton",
                anchorMin: new Vector2(0.20f, 0.10f),
                anchorMax: new Vector2(0.80f, 0.22f),
                label:     "PLAY",
                bgColor:   new Color(0.20f, 0.72f, 0.35f, 1f),
                textColor: Color.white,
                fontSize:  44,
                onClick:   OnPlayClicked);
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
