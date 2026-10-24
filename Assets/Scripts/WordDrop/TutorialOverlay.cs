using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// TUTORIAL OVERLAY (2026-10-23, rebuilt — Spencer: "our other tutorial was janky ... the spotlight was always screwing up").
    /// VISUALS ONLY; TutorialBeats decides what to show and owns input.
    ///
    /// WHY A NEW ONE: the June TutorialSpotlight lit the taught tiles by pushing THEIR sorting orders above the dim, and the
    /// lift / hint / FX / special-icon systems all write tile sorting orders too — they fought, tiles popped under the dim.
    /// This one never touches a tile: it CUTS A HOLE in the dim (SpriteMask, scrim = VisibleOutsideMask), so everything inside
    /// the window — pops, falls, glows — renders exactly as in normal play.
    ///
    /// LOOK (measured from Spencer's Royal Match recording, L3 TNT beat @1:48): plain black ~70%, no tint; one rounded window
    /// hugging the cells; the top bar dimmed too; a one-line bubble just above the window; the hand loops the move.
    /// Layers: world scrim 1000 (above every board sprite, max ~205; booster row canvas is camera-space order 8 so it dims too),
    /// bubble 1010-1012, hand 1020. The HUD is a screen-OVERLAY canvas (order 50) the world can't cover, so it gets its own
    /// strip on an overlay canvas at order 60, from the board's top edge up.
    /// </summary>
    public class TutorialOverlay : MonoBehaviour
    {
        public static TutorialOverlay Instance { get; private set; }

        // 0.90, not RM's measured 70%: the project is LINEAR, so alpha blends in linear light — 0.70 only took a white tile
        // 244 -> 142 on screen (~42% darker; Spencer's 11:58 shot). 0.90 lands ~65-70% darker in sRGB, RM's look. 2026-10-23.
        public static Color DIM          = new Color(0f, 0f, 0f, 0.90f);
        public static float WINDOW_PAD   = 0.55f;   // cells, from cell CENTRES (0.5 = tile edge)
        public static float FADE_IN      = 0.45f, FADE_OUT = 0.15f;   // 0.25 -> 0.45 (2026-10-23 "very abrupt")
        public static Color BUBBLE_FILL  = new Color(1f, 0.973f, 0.902f, 1f);    // HUD plaque cream
        public static Color BUBBLE_EDGE  = new Color(0.333f, 0.165f, 0.651f, 1f); // HUD bar purple
        public static float BUBBLE_W     = 7.6f;    // cells
        public static float BUBBLE_H     = 1.45f;   // cells
        public static float HAND_CELLS   = 1.25f;   // hand height in cells
        public static float HAND_MOVE    = 0.55f;   // seconds per drag
        private const int SCRIM_ORDER = 1000, BUBBLE_ORDER = 1010, HAND_ORDER = 1020;

        public bool IsShowing { get; private set; }

        // The dim is 4 rects AROUND the window + 4 corner pieces that round it off (2026-10-23 v2). v1 used a SpriteMask hole and
        // the board never dimmed in Spencer's first look — no masks now, nothing to go wrong: plain sprites, no overlaps.
        private Transform _follow;                       // rides GridManager.BoardSlideOffsetX every frame (board slides in)
        private readonly SpriteRenderer[] _rects = new SpriteRenderer[4];    // above, below, left, right
        private readonly SpriteRenderer[] _corners = new SpriteRenderer[4];  // TL, TR, BL, BR
        private static Sprite s_corner;
        private const int CORNER_PX = 64;
        private float _dimAlpha;                         // tweened 0 -> DIM.a; applied to every piece in LateUpdate
        private Canvas _hudCanvas; private Image _hudDim;
        private Transform _bubble; private SpriteRenderer _bubbleEdge, _bubbleFill; private TextMeshPro _bubbleText;
        private int _bubblePw = -1, _bubblePh = -1;
        private SpriteRenderer _hand; private Sequence _handSeq;
        private static Sprite s_white, s_handSprite;

        public static TutorialOverlay Ensure()
        {
            if (Instance != null) return Instance;
            var go = new GameObject("TutorialOverlay");
            Instance = go.AddComponent<TutorialOverlay>();
            Instance.Build();
            return Instance;
        }

        private void Build()
        {
            if (s_white == null)
            {
                var t = new Texture2D(4, 4); var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white;
                t.SetPixels(px); t.Apply(); s_white = Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            }
            _follow = new GameObject("TutFollow").transform; _follow.SetParent(transform, false);
            if (s_corner == null)
            {   // black where OUTSIDE a quarter disc centred on the texture's bottom-right corner (antialiased) — the window's TL corner
                var ct = new Texture2D(CORNER_PX, CORNER_PX, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
                var cp = new Color[CORNER_PX * CORNER_PX];
                for (int j = 0; j < CORNER_PX; j++)
                    for (int i = 0; i < CORNER_PX; i++)
                    {
                        float d = Vector2.Distance(new Vector2(i + 0.5f, j + 0.5f), new Vector2(CORNER_PX, 0f));
                        cp[j * CORNER_PX + i] = new Color(1f, 1f, 1f, Mathf.Clamp01(d - CORNER_PX + 0.5f));
                    }
                ct.SetPixels(cp); ct.Apply();
                s_corner = Sprite.Create(ct, new Rect(0, 0, CORNER_PX, CORNER_PX), new Vector2(0.5f, 0.5f), CORNER_PX);
            }
            for (int i = 0; i < 4; i++)
            {
                _rects[i] = new GameObject("TutDim" + i).AddComponent<SpriteRenderer>();
                _rects[i].transform.SetParent(_follow, false); _rects[i].sprite = s_white; _rects[i].sortingOrder = SCRIM_ORDER;
                _corners[i] = new GameObject("TutCorner" + i).AddComponent<SpriteRenderer>();
                _corners[i].transform.SetParent(_follow, false); _corners[i].sprite = s_corner; _corners[i].sortingOrder = SCRIM_ORDER;
                _corners[i].flipX = i == 1 || i == 3;   // TR, BR
                _corners[i].flipY = i == 2 || i == 3;   // BL, BR
            }

            var cgo = new GameObject("TutHudDimCanvas", typeof(Canvas), typeof(CanvasScaler));
            cgo.transform.SetParent(transform, false);
            _hudCanvas = cgo.GetComponent<Canvas>();
            _hudCanvas.renderMode = RenderMode.ScreenSpaceOverlay; _hudCanvas.sortingOrder = 60;   // HUD is 50
            var igo = new GameObject("HudDim", typeof(RectTransform), typeof(Image));
            igo.transform.SetParent(cgo.transform, false);
            _hudDim = igo.GetComponent<Image>(); _hudDim.color = DIM; _hudDim.raycastTarget = false;

            _bubble = new GameObject("TutBubble").transform; _bubble.SetParent(_follow, false);
            _bubbleEdge = new GameObject("Edge").AddComponent<SpriteRenderer>(); _bubbleEdge.transform.SetParent(_bubble, false);
            _bubbleEdge.color = BUBBLE_EDGE; _bubbleEdge.sortingOrder = BUBBLE_ORDER;
            _bubbleFill = new GameObject("Fill").AddComponent<SpriteRenderer>(); _bubbleFill.transform.SetParent(_bubble, false);
            _bubbleFill.color = BUBBLE_FILL; _bubbleFill.sortingOrder = BUBBLE_ORDER + 1;
            _bubbleText = new GameObject("Text").AddComponent<TextMeshPro>(); _bubbleText.transform.SetParent(_bubble, false);
            var f = GameFont.GetTMP(); if (f != null) { _bubbleText.font = f; _bubbleText.fontSharedMaterial = f.material; }
            _bubbleText.alignment = TextAlignmentOptions.Center; _bubbleText.textWrappingMode = TextWrappingModes.Normal;
            _bubbleText.enableAutoSizing = true; _bubbleText.fontSizeMin = 2f; _bubbleText.fontSizeMax = 6f;
            _bubbleText.color = Tile.LETTER_INK; _bubbleText.fontStyle = FontStyles.Bold;
            var tr = _bubbleText.GetComponent<MeshRenderer>(); if (tr != null) tr.sortingOrder = BUBBLE_ORDER + 2;

            _hand = new GameObject("TutHand").AddComponent<SpriteRenderer>();
            _hand.transform.SetParent(_follow, false);
            _hand.sprite = HandSprite(); _hand.sortingOrder = HAND_ORDER;

            SetAllVisible(false);
        }

        private void SetAllVisible(bool on)
        {
            SetDimVisible(on); _hudCanvas.enabled = on;
            _bubble.gameObject.SetActive(on); _hand.enabled = on;
        }

        private void SetDimVisible(bool on) { for (int i = 0; i < 4; i++) { _rects[i].enabled = on; _corners[i].enabled = on; } }

        private void LateUpdate()
        {
            if (!IsShowing && _dimAlpha <= 0f) return;
            var grid = GridManager.Instance;
            if (grid != null) _follow.position = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);   // follow the board, always
            var c = DIM; c.a = _dimAlpha;
            for (int i = 0; i < 4; i++) { _rects[i].color = c; _corners[i].color = c; }
            _hudDim.color = c;
        }

        private static Sprite HandSprite()
        {
            if (s_handSprite != null) return s_handSprite;
            var tex = Resources.Load<Texture2D>("Tiles/hand_point");
            if (tex != null) s_handSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                                                          new Vector2(TutorialSpotlight.HAND_TIP_X, TutorialSpotlight.HAND_TIP_Y), 100f);
            return s_handSprite;
        }

        /// <summary>Dim everything except a rounded window around <paramref name="cells"/>; bubble above it with
        /// <paramref name="text"/>; the hand loops a drag from <paramref name="dragFrom"/> to <paramref name="dragTo"/>
        /// (pass the same cell twice for a tap, or dragFrom.x &lt; 0 for no hand). cells == null = bubble only, no dim.</summary>
        public void Show(List<Vector2Int> cells, string text, Vector2Int dragFrom, Vector2Int dragTo, float moveTime = -1f)
        {
            var grid = GridManager.Instance; var cam = Camera.main;
            if (grid == null || cam == null) return;
            float cs = grid.CellSize;
            IsShowing = true;
            SetAllVisible(true);
            bool dim = cells != null && cells.Count > 0;

            // Positions are BOARD-LOCAL (no slide): _follow carries the slide every frame.
            float big = cam.orthographicSize * 4f + 20f;   // far past every screen edge
            Vector3 cc = cam.transform.position;
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            if (dim)
            {
                foreach (var c in cells)
                {
                    Vector3 p = grid.CellToWorld(c.x, c.y);
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
                float pad = cs * WINDOW_PAD;
                minX -= pad; maxX += pad; minY -= pad; maxY += pad;
                float r = Mathf.Min(cs * 0.30f, (maxX - minX) * 0.5f, (maxY - minY) * 0.5f);
                const float Z = -1f;
                void Rect(SpriteRenderer sr, float x0, float y0, float x1, float y1)
                { sr.transform.localPosition = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, Z); sr.transform.localScale = new Vector3(x1 - x0, y1 - y0, 1f); }
                Rect(_rects[0], cc.x - big, maxY, cc.x + big, cc.y + big);   // above
                Rect(_rects[1], cc.x - big, cc.y - big, cc.x + big, minY);   // below
                Rect(_rects[2], cc.x - big, minY, minX, maxY);               // left
                Rect(_rects[3], maxX, minY, cc.x + big, maxY);               // right
                Vector3[] at = { new Vector3(minX + r * 0.5f, maxY - r * 0.5f, Z), new Vector3(maxX - r * 0.5f, maxY - r * 0.5f, Z),
                                 new Vector3(minX + r * 0.5f, minY + r * 0.5f, Z), new Vector3(maxX - r * 0.5f, minY + r * 0.5f, Z) };
                for (int i = 0; i < 4; i++) { _corners[i].transform.localPosition = at[i]; _corners[i].transform.localScale = Vector3.one * r; }

                // ── HUD strip: from the board's top edge to the top of the screen ──
                float boardTop = grid.CellToWorld(0, RulesEngine.ROWS - 1).y + cs * 0.75f;
                float vy = Mathf.Clamp01(cam.WorldToViewportPoint(new Vector3(0f, boardTop, 0f)).y);
                var rt = _hudDim.rectTransform;
                rt.anchorMin = new Vector2(0f, vy); rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            }
            else
            {   // bubble only: anchor it to the middle of the board
                Vector3 mid = grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2);
                minX = maxX = mid.x; minY = maxY = mid.y;
            }

            // ── bubble: just above the window; below it if it would leave the board ──
            float bubbleWCells = Mathf.Min(BUBBLE_W, RulesEngine.COLS - 0.6f);   // never wider than the board (7x9 small board, 2026-10-23)
            int bw = Mathf.RoundToInt(bubbleWCells * cs * 100f), bh = Mathf.RoundToInt(BUBBLE_H * cs * 100f);
            if (bw != _bubblePw || bh != _bubblePh)
            {
                int edge = Mathf.Max(4, Mathf.RoundToInt(cs * 0.09f * 100f));
                int r = Mathf.RoundToInt(bh * 0.30f);
                _bubbleEdge.sprite = TileRenderer.CreateSolidRoundedRect(bw, bh, r, Color.white);
                _bubbleFill.sprite = TileRenderer.CreateSolidRoundedRect(bw - edge * 2, bh - edge * 2, Mathf.Max(2, r - edge), Color.white);
                _bubblePw = bw; _bubblePh = bh;
            }
            float bubbleH = BUBBLE_H * cs;
            float top = grid.CellToWorld(0, RulesEngine.ROWS - 1).y + cs * 0.5f;
            float by = maxY + cs * 0.25f + bubbleH * 0.5f;
            if (by + bubbleH * 0.5f > top) by = minY - cs * 0.25f - bubbleH * 0.5f;
            float bx = grid.CellToWorld(RulesEngine.COLS / 2, 0).x;   // centred on the board (board-local), like RM
            _bubble.localPosition = new Vector3(bx, by, -1.2f);
            _bubbleText.rectTransform.sizeDelta = new Vector2((bubbleWCells - 0.6f) * cs, (BUBBLE_H - 0.35f) * cs);
            _bubbleText.text = text ?? "";
            _bubble.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _bubble.DOKill(); _bubble.localScale = Vector3.one * 0.6f;
            _bubble.DOScale(1f, 0.28f).SetEase(Ease.OutBack, 2f).SetUpdate(true);

            // ── fade the dim in (LateUpdate applies _dimAlpha to every piece + the HUD strip) ──
            DOTween.Kill(this);
            if (dim) DOTween.To(() => _dimAlpha, v => _dimAlpha = v, DIM.a, FADE_IN).SetTarget(this).SetUpdate(true);
            else _dimAlpha = 0f;

            // ── hand ──
            _handSeq?.Kill();
            if (dragFrom.x < 0 || _hand.sprite == null) { _hand.enabled = false; return; }
            float scale = HAND_CELLS * cs / Mathf.Max(0.01f, _hand.sprite.bounds.size.y);
            Vector3 from = grid.CellToWorld(dragFrom.x, dragFrom.y), to = grid.CellToWorld(dragTo.x, dragTo.y);   // board-local
            from.z = to.z = -1.3f;
            _hand.enabled = true;
            _hand.transform.localPosition = from; _hand.transform.localScale = Vector3.one * scale;
            _hand.color = new Color(1f, 1f, 1f, 0f);
            _handSeq = DOTween.Sequence().SetUpdate(true).SetLoops(-1);
            _handSeq.AppendCallback(() => { _hand.transform.localPosition = from; _hand.transform.localScale = Vector3.one * scale; });
            _handSeq.Append(_hand.DOFade(1f, 0.18f));
            _handSeq.Append(_hand.transform.DOScale(scale * 0.88f, 0.12f));            // press
            if (from != to) _handSeq.Append(_hand.transform.DOLocalMove(to, moveTime > 0f ? moveTime : HAND_MOVE).SetEase(Ease.InOutSine));
            _handSeq.Append(_hand.transform.DOScale(scale, 0.12f));                    // release
            _handSeq.Append(_hand.DOFade(0f, 0.2f).SetDelay(0.15f));
            _handSeq.AppendInterval(0.35f);
        }

        /// <summary>HAND ONLY (no dim, no bubble): loop a drag from <paramref name="from"/> to <paramref name="to"/> — the swipe HINT
        /// (2026-10-23). Hide() / HideHandTrace() stop it.</summary>
        public bool HandTraceShowing { get; private set; }
        public void ShowHandTrace(Vector2Int from, Vector2Int to, float moveTime)
        {
            var grid = GridManager.Instance; if (grid == null || _hand.sprite == null || IsShowing) return;
            HandTraceShowing = true;
            float cs = grid.CellSize;
            float scale = HAND_CELLS * cs / Mathf.Max(0.01f, _hand.sprite.bounds.size.y);
            Vector3 a = grid.CellToWorld(from.x, from.y), b = grid.CellToWorld(to.x, to.y); a.z = b.z = -1.3f;
            _follow.position = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
            _handSeq?.Kill();
            _hand.enabled = true; _hand.transform.localPosition = a; _hand.transform.localScale = Vector3.one * scale; _hand.color = new Color(1f, 1f, 1f, 0f);
            _handSeq = DOTween.Sequence().SetUpdate(true).SetLoops(-1);
            _handSeq.AppendCallback(() => { _hand.transform.localPosition = a; _hand.transform.localScale = Vector3.one * scale; });
            _handSeq.Append(_hand.DOFade(1f, 0.18f));
            _handSeq.Append(_hand.transform.DOScale(scale * 0.88f, 0.12f));
            _handSeq.Append(_hand.transform.DOLocalMove(b, moveTime).SetEase(Ease.InOutSine));
            _handSeq.Append(_hand.transform.DOScale(scale, 0.12f));
            _handSeq.Append(_hand.DOFade(0f, 0.2f).SetDelay(0.15f));
            _handSeq.AppendInterval(1.4f);
        }
        public void HideHandTrace()
        {
            if (!HandTraceShowing) return;
            HandTraceShowing = false;
            if (IsShowing) return;   // a tutorial beat took the hand over — leave it
            _handSeq?.Kill(); _hand.enabled = false;
        }

        public void Hide()
        {
            if (!IsShowing) return;
            IsShowing = false;
            _handSeq?.Kill(); _hand.enabled = false;
            _bubble.DOKill(); _bubble.gameObject.SetActive(false);
            DOTween.Kill(this);
            DOTween.To(() => _dimAlpha, v => _dimAlpha = v, 0f, FADE_OUT).SetTarget(this).SetUpdate(true)
                   .OnComplete(() => { if (!IsShowing) { SetDimVisible(false); _hudCanvas.enabled = false; } });
        }
    }
}
