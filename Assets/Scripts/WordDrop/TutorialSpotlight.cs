using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Onboarding focus overlay (2026-06-25) — copies the booster aim-scrim exactly.
    ///
    /// The booster scrim "nails" the board dim by a ScreenSpaceCamera full-screen scrim with the
    /// rounded board sprite + tiles BUMPED above it (so the board's own shape is the bright cutout —
    /// perfect rounded corners, no texture hacks). We reuse that verbatim (SetBoardBackgroundSortingOrder
    /// + Tile.AimModeTileOrder). The tile holder sits below the board and is simply left under the scrim,
    /// so it dims for free. The ONE thing the camera scrim can't reach is the HUD (a ScreenSpaceOverlay
    /// canvas that always draws above ScreenSpaceCamera) — so we add a separate Overlay dim over the HUD
    /// bar, plus the hand-point cursor on that same top layer so it stays visible over the bright board.
    /// </summary>
    public class TutorialSpotlight : MonoBehaviour
    {
        public static TutorialSpotlight Instance { get; private set; }

        // Matched to BoosterHUDSlot so the board cutout is identical.
        private const int SCRIM_ORDER    = 15;
        private const int BOARD_BG_ORDER = 17;
        private const int TILE_ORDER     = 25;
        private const int OVERLAY_ORDER  = 150;  // HUD dim + cursor — above the HUD canvas (50)
        private static readonly Color DIM = new Color(0f, 0f, 0f, 0.84f);
        private const float CURSOR_PX     = 140f;
        private const float DRAG_SECONDS  = 1.05f;
        private const float HUD_DIM_FALLBACK_TOP = 0.83f; // viewport y if board bounds unavailable

        private Canvas _scrimCanvas;   // ScreenSpaceCamera full-screen scrim (board bumped above it)
        private Canvas _overlayCanvas; // ScreenSpaceOverlay: HUD dim strip + cursor
        private Image  _hudDim;
        private RectTransform _cursor;
        private Sequence _cursorSeq;
        private bool _boardBumped;

        private static TutorialSpotlight Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TutorialSpotlight");
            Instance = go.AddComponent<TutorialSpotlight>();
            Instance.Build();
            return Instance;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Build()
        {
            // Camera-space scrim — dims all world-space chrome (board background gap, tile holder,
            // boosters); the board sprite + tiles get bumped above it to read bright.
            var scrimGO = new GameObject("TutorialScrim", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            scrimGO.transform.SetParent(transform, false);
            _scrimCanvas = scrimGO.GetComponent<Canvas>();
            _scrimCanvas.renderMode  = RenderMode.ScreenSpaceCamera;
            _scrimCanvas.worldCamera  = Camera.main;
            _scrimCanvas.planeDistance = 2f;
            _scrimCanvas.sortingOrder  = SCRIM_ORDER;
            var ss = scrimGO.GetComponent<CanvasScaler>();
            ss.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            ss.referenceResolution = new Vector2(1080f, 1920f);
            ss.matchWidthOrHeight = 1f;
            // raycastTarget MUST be false — this scrim is full-screen (the board shows through by
            // sort-order, not a hole), so blocking raycasts here would swallow every board tap/drop.
            // Input gating is handled by TutorialManager (AllowedColumn/Card), not by the dim.
            MakeRect(scrimGO.transform, "Scrim", DIM, Vector2.zero, Vector2.one, false);

            // Overlay layer — dims the HUD bar (camera scrim can't) + holds the cursor.
            var ovGO = new GameObject("TutorialOverlay", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            ovGO.transform.SetParent(transform, false);
            _overlayCanvas = ovGO.GetComponent<Canvas>();
            _overlayCanvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            _overlayCanvas.sortingOrder = OVERLAY_ORDER;
            var os = ovGO.GetComponent<CanvasScaler>();
            os.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            os.referenceResolution = new Vector2(1080f, 1920f);
            os.matchWidthOrHeight = 0.5f;
            _hudDim = MakeRect(ovGO.transform, "HudDim", DIM,
                new Vector2(0f, HUD_DIM_FALLBACK_TOP), Vector2.one, true);

            _scrimCanvas.enabled = false;
            _overlayCanvas.enabled = false;
        }

        private static Image MakeRect(Transform parent, string name, Color color,
            Vector2 anchorMin, Vector2 anchorMax, bool raycast)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = raycast;
            return img;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>Board bright (rounded cutout via sprite bump, exactly like the booster); tile holder
        /// + everything else dimmed by the camera scrim; HUD bar dimmed by the overlay strip.</summary>
        public static void ShowBoardFocus()
        {
            var inst = Ensure();
            if (inst._scrimCanvas != null)
            {
                if (inst._scrimCanvas.worldCamera == null) inst._scrimCanvas.worldCamera = Camera.main;
                inst._scrimCanvas.enabled = true;
            }
            if (inst._overlayCanvas != null) inst._overlayCanvas.enabled = true;
            inst.BumpBoard(true);
            inst.PositionHudDim();
            if (inst._hudDim != null) inst._hudDim.gameObject.SetActive(true);
        }

        private void BumpBoard(bool active)
        {
            var grid = GridManager.Instance;
            if (grid != null)
            {
                grid.SetBoardBackgroundSortingOrder(active ? BOARD_BG_ORDER : 0);
                Tile.AimModeTileOrder = active ? TILE_ORDER : 0;
                int tileOrder = active ? TILE_ORDER : 5;
                for (int c = 0; c < RulesEngine.COLS; c++)
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                    {
                        var t = grid.GetTile(c, r);
                        if (t != null) t.SetSortingOrder(tileOrder);
                    }
            }
            _boardBumped = active;
        }

        /// <summary>Dim from the board's top edge up to the top of the screen — covers the HUD bar.</summary>
        private void PositionHudDim()
        {
            if (_hudDim == null) return;
            float topVP = HUD_DIM_FALLBACK_TOP;
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid != null && cam != null && grid.BoardBackgroundWorldBounds.HasValue)
                topVP = Mathf.Clamp01(cam.WorldToViewportPoint(grid.BoardBackgroundWorldBounds.Value.max).y);
            var rt = _hudDim.rectTransform;
            rt.anchorMin = new Vector2(0f, topVP);
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        public static void Hide()
        {
            if (Instance == null) return;
            Instance.BumpBoard(false);
            Instance._cursorSeq?.Kill();
            Instance._cursorSeq = null;
            if (Instance._cursor != null) Instance._cursor.gameObject.SetActive(false);
            if (Instance._scrimCanvas   != null) Instance._scrimCanvas.enabled = false;
            if (Instance._overlayCanvas != null) Instance._overlayCanvas.enabled = false;
        }

        // ── Drag-gesture cursor (hand_point sprite, on the overlay layer) ─────────────
        public static void ShowDragGesture(Vector2 fromVP, Vector2 toVP)
        {
            var inst = Ensure();
            if (inst._overlayCanvas != null) inst._overlayCanvas.enabled = true;
            inst.BuildCursorGesture(fromVP, toVP);
        }

        private void BuildCursorGesture(Vector2 fromVP, Vector2 toVP)
        {
            if (_cursor == null)
            {
                var go = new GameObject("DragCursor", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_overlayCanvas.transform, false);
                var img = go.GetComponent<Image>();
                img.sprite = GetHandSprite();
                img.raycastTarget = false;
                img.preserveAspect = true;
                _cursor = go.GetComponent<RectTransform>();
                _cursor.sizeDelta = new Vector2(CURSOR_PX, CURSOR_PX);
                _cursor.anchorMin = _cursor.anchorMax = Vector2.zero;
                _cursor.pivot = new Vector2(0.3f, 0.85f); // fingertip near top-left of the sprite
            }
            _cursor.gameObject.SetActive(true);

            _cursorSeq?.Kill();
            Vector2 a = ViewportToCanvas(fromVP);
            Vector2 b = ViewportToCanvas(toVP);

            _cursorSeq = DOTween.Sequence().SetUpdate(true);
            _cursorSeq.AppendCallback(() => { _cursor.anchoredPosition = a; _cursor.localScale = Vector3.one; });
            _cursorSeq.Append(_cursor.DOScale(0.85f, 0.18f).SetEase(Ease.OutQuad));
            _cursorSeq.Append(_cursor.DOAnchorPos(b, DRAG_SECONDS).SetEase(Ease.InOutSine));
            _cursorSeq.Append(_cursor.DOScale(1f, 0.15f));
            _cursorSeq.AppendInterval(0.35f);
            _cursorSeq.SetLoops(-1);
        }

        private Vector2 ViewportToCanvas(Vector2 vp)
        {
            var canvasRT = _overlayCanvas != null ? _overlayCanvas.transform as RectTransform : null;
            Vector2 size = canvasRT != null ? canvasRT.rect.size : new Vector2(Screen.width, Screen.height);
            return new Vector2(vp.x * size.x, vp.y * size.y);
        }

        // ── hand_point sprite (Resources/Tiles/hand_point) ───────────────────────────
        private static Sprite _handSprite;
        private static Sprite GetHandSprite()
        {
            if (_handSprite != null) return _handSprite;
            _handSprite = Resources.Load<Sprite>("Tiles/hand_point");
            if (_handSprite == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/hand_point");
                if (tex != null)
                    _handSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                                new Vector2(0.3f, 0.85f), 100f);
            }
            return _handSprite;
        }
    }
}
