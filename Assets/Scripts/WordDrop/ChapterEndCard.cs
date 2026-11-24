using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// END OF CONTENT (2026-10-30, for the TestFlight chapter). Past level CONTENT_END_LEVEL the game would fall into the OLD
    /// drop-era generator (LevelRules.For past the table: fuse, branch, rising rows) - a different game. So the home screen's
    /// Level button and the auto level card stop there: a "Chapter complete! More levels coming soon" card instead.
    /// Raise CONTENT_END_LEVEL as levels 21+ are rebuilt; 0 = no cap.
    /// </summary>
    public static class ChapterEndCard
    {
        public static int CONTENT_END_LEVEL = 21;   // TEMP 11-24: 20 -> 21 so the owl test level (L21 "Night Owls") opens. Back to 20 before any tester build unless L21 ships.
        private static GameObject s_root;

        /// <summary>True when <paramref name="internalLevel"/> (the map's level index) is past the last finished level.</summary>
        public static bool IsPastContent(int internalLevel)
            => CONTENT_END_LEVEL > 0 && !LevelMapPanel.IsTutorialLevel(internalLevel) && LevelMapPanel.RunLevel(internalLevel) > CONTENT_END_LEVEL;

        public static void Show(System.Action onClosed = null)
        {
            if (s_root != null) return;
            WDLog.Log($"[ChapterEnd] past level {CONTENT_END_LEVEL} - showing 'coming soon'");
            s_root = new GameObject("ChapterEndCard", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = s_root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 400;   // 10-31: was 160, UNDER the home canvas (170) - its full-screen TapToEnter ate the OK tap and dropped to the bare board
            var scaler = s_root.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920); scaler.matchWidthOrHeight = 0.5f;

            var dim = NewImage("Dim", s_root.transform, null, new Color(0x1A / 255f, 0x0B / 255f, 0x2E / 255f, 0f));
            Stretch(dim.rectTransform);
            dim.DOFade(UISeparation.MODAL_DIM.a, 0.25f).SetUpdate(true);

            // 11-01 Spencer: "use the text we are using and make it suit the game" - the level card v2 build: gold-framed purple
            // panel (UI/Panel/panel, 9-sliced), arched header plate with the title in our recipe lettering, a cream box for the
            // message, the green pill button. Laid out in the mock's pixels (1179-wide screen) inside a root scaled to the canvas.
            var rootRT = (RectTransform)s_root.transform;
            // 11-06 Spencer: the card ran off both sides. k was read from the canvas rect right after creation, before the
            // CanvasScaler had sized it (k = 1 -> ~20% too big). Now a parent re-scales every frame like the level card's
            // V2Scale; the card under it keeps its own scale free for PopIn/PopOut.
            var scaleRT = new GameObject("Scale", typeof(RectTransform)).GetComponent<RectTransform>(); scaleRT.SetParent(s_root.transform, false);
            scaleRT.anchorMin = scaleRT.anchorMax = new Vector2(0.5f, 0.5f); scaleRT.pivot = new Vector2(0.5f, 0.5f);
            scaleRT.gameObject.AddComponent<FitScale>().canvasRT = rootRT;
            var card = new GameObject("Card", typeof(RectTransform)).GetComponent<RectTransform>(); card.SetParent(scaleRT, false);
            card.anchorMin = card.anchorMax = new Vector2(0.5f, 0.5f); card.pivot = new Vector2(0.5f, 0.5f);
            const float CW = 1000f, CH = 210f + 300f + 60f + 204f + 60f;
            card.sizeDelta = new Vector2(CW, CH);
            card.anchoredPosition = new Vector2(0f, -40f);

            var frame = NewImage("Frame", card, PanelSprite("panel", new Vector4(205, 225, 205, 205)), Color.white); frame.type = Image.Type.Sliced; frame.raycastTarget = true;
            var frt = frame.rectTransform; frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = new Vector2(-90f, -110f); frt.offsetMax = new Vector2(90f, 90f);
            var box = NewImage("Box", card, PanelSprite("cream", new Vector4(52, 52, 52, 52)), Color.white); box.type = Image.Type.Sliced;
            Place(box.rectTransform, CW / 2f, 210f + 150f, CW - 140f, 300f);
            var header = NewImage("Header", card, PanelSprite("header"), Color.white);
            Place(header.rectTransform, 500f, 11f, 820f, 358f);

            // 11-06 Spencer "same formatting as the level card": its header font (WordSlop Title Medium), ink and normal spacing.
            // Cap 66 so "Chapter Complete!" (MEASURED 828 px at cap 74 in this font) fits the plate's 760.
            // 11-07 Spencer's screenshot: at cap 66 the title (+ its outline) still ran past both ends of the header plaque. Cap 56 =
            // ~670 px of ink inside the plate's ~760 px with a margin each side.
            var title = Text(card, "Chapter Complete!", 56f, new Vector2(CW / 2f, 10f - LevelIntroModal.V2_TITLE_LIFT), new Vector2(700f, 150f));   // 11-17 lifted with every card header
            HUDManager.ApplyRecipeCount(title, LevelIntroModal.V2_TITLE_LINE, 0.19f, -0.9f, Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF"));
            title.characterSpacing = 0f;
            // body: cap 44 wrapped the 916 px first line into 4 lines that spilled out of the box. Cap 38 in an 820 px rect
            // (box 860): lines MEASURED 789 / 664 px, one line each, no wrap.
            var body = Text(box.transform, "You've finished every level so far.\nMore levels are coming soon!", 38f, new Vector2((CW - 140f) / 2f, 150f), new Vector2(CW - 180f, 220f));
            HUDManager.ApplyRecipeFlat(body); body.color = LevelIntroModal.V2_LABEL_INK;
            body.lineSpacing = 8f;

            var ok = ArtButton.Create(card, "pill_play", "OK", lilita: false, titleMedium: false);   // 11-07: WordSlop Button like Play / Continue (the default is Title Medium)
            if (ok != null)
            {
                const float FIT = 560f / 534f;   // same measured fit as the level card's PLAY
                Place((RectTransform)ok.transform, CW / 2f, CH - 60f - 102f, 600f * FIT, 204f * FIT);
                ok.Refit();
                ok.Button.onClick.AddListener(() =>
                {
                    var root = s_root; s_root = null;
                    if (root == null) return;
                    UIAnimations.PopOut(card.transform, () => { if (root != null) Object.Destroy(root); onClosed?.Invoke(); });
                });
            }

            UIAnimations.PopIn(card.transform);
            GameAudio.Instance?.PlayPersonalBest();
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_sprites = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite PanelSprite(string name, Vector4 border = default)
        {
            string key = name + border; if (s_sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            var tex = Resources.Load<Texture2D>("UI/Panel/" + name);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border) : null;
            s_sprites[key] = sp; return sp;
        }
        /// <summary>Centre (x, y) in mock px from the parent's TOP-LEFT (y down).</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }
        private static TextMeshProUGUI Text(Transform parent, string s, float capPx, Vector2 centre, Vector2 size)
        {
            var go = new GameObject("Text", typeof(RectTransform)); go.transform.SetParent(parent, false);
            var t = go.AddComponent<TextMeshProUGUI>(); t.text = s; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            t.textWrappingMode = TextWrappingModes.NoWrap; t.fontSize = capPx / 0.594f / HUDManager.RECIPE_MAX_MUL;
            Place(t.rectTransform, centre.x, centre.y, size.x, size.y); return t;
        }

        private static Image NewImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.sprite = sprite; im.color = color; return im;
        }
        private class FitScale : MonoBehaviour
        {   // mock px -> canvas units every frame (the canvas is only sized after the scaler runs)
            public RectTransform canvasRT;
            private void LateUpdate()
            {
                if (canvasRT == null || canvasRT.rect.width <= 1f) return;
                float k = canvasRT.rect.width / 1179f; transform.localScale = new Vector3(k, k, 1f);
            }
        }
        private static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }
    }
}
