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
        public static Color DIM          = new Color(0.06f, 0.02f, 0.14f, 0.90f);   // 11-06: deep purple tint (was black) - matches the game's purple overlays
        public static float WINDOW_PAD   = 0.55f;   // cells, from cell CENTRES (0.5 = tile edge)
        public static float FADE_IN      = 0.45f, FADE_OUT = 0.15f;   // 0.25 -> 0.45 (2026-10-23 "very abrupt")
        public static Color BUBBLE_FILL  = new Color(1f, 0.973f, 0.902f, 1f);    // HUD plaque cream
        public static Color BUBBLE_EDGE  = new Color(0.333f, 0.165f, 0.651f, 1f); // HUD bar purple
        public static float BUBBLE_W     = 7.6f;    // cells
        public static float BUBBLE_H     = 1.45f;   // cells
        // ── 11-06 GOLD BUBBLE (Spencer picked the gold button frame, no tail: "people will know its the owl speaking"). The home
        // Level button's gold rim with a cream inside (UI/Panel/tut_bubble.png, 9-sliced), WordSlop text in the level card's ink,
        // the word to spell on its own line, big and blue, and the owl at the left, beak flapping while the line appears. ──
        public static bool  BUBBLE_GOLD  = true;
        public static float GOLD_H       = 2.05f;   // cells (3 lines)
        public static float GOLD_PPU     = 175f;    // tut_bubble px per world unit: keeps the rim as thick as the buttons'
        public static float GOLD_BORDER  = 112f;    // 9-slice corners in tut_bubble px
        // 11-06 Spencer: light the WHOLE top HUD during a lesson (the HUD owl is the one talking) - the HUD strip dim is simply
        // not drawn, so the HUD's own art is the lit edge, exact at every corner. The bubble stays by the lit cells, without its
        // owl head (one owl on screen). false = the HUD dims and the bubble carries the owl head (the 11-06 gold bubble).
        public static bool  HUD_LIT      = true;
        // 11-06 Spencer: "text size should match the tutorial in royal match" + stay consistent. MEASURED RM L1/L2 (592 px wide
        // recording): cap height 3.5% of screen width, line pitch ~1.95x cap, 2 balanced lines, bubble 76% of screen width,
        // ~3.2% padding. RM_TEXT: one fixed size for EVERY tip, balanced line breaks, bubble height = its lines. false = autosize.
        public static bool  RM_TEXT          = true;
        public static float RM_CAP_FRAC      = 0.035f;   // cap height / screen width
        public static float RM_BUBBLE_W_FRAC = 0.76f;    // bubble width / screen width
        public static float RM_TEXT_W_FRAC   = 0.64f;    // longest line allowed / screen width
        public static float RM_PAD_FRAC      = 0.042f;   // top + bottom padding each (incl. the gold rim) / screen width
        public const  string SIZE_REFERENCE = "Swap to spell WORDS to break a crate!";   // the longest lesson shape: 3 lines
        // 11-06 Spencer: "the text inside the golden trim is not centered ... counting the orange bottom of the bevel" - tut_bubble's
        // rim is 29 px on top, 52 px at the bottom (measured), so the cream's centre sits (52-29)/2 tut px above the bubble's centre
        public static float TEXT_RIM_LIFT = 11.5f;
        public static float LESSON_LINE_SPACING = -45f;   // TMP em/100: WordSlop Button line height 144 / cap 54 at 90 pt -> line pitch ~1.8x cap
        public static float OWL_CELLS    = 1.85f;   // owl head height
        public static float OWL_ROOM     = 1.55f;   // cells of the bubble's left end the owl covers (text sits right of it)
        public static Color WORD_COLOR   = new Color32(0x1E, 0x6E, 0xE1, 0xFF);
        private SpriteRenderer _owl; private Sprite _owlOpen, _owlShut; private Sequence _owlTalk;
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
            if (BUBBLE_GOLD) SetupGold();

            _hand = new GameObject("TutHand").AddComponent<SpriteRenderer>();
            _hand.transform.SetParent(_follow, false);
            _hand.sprite = HandSprite(); _hand.sortingOrder = HAND_ORDER;

            SetAllVisible(false);
        }

        private void SetupGold()
        {
            var tex = Resources.Load<Texture2D>("UI/Panel/tut_bubble");
            if (tex == null) { Debug.LogWarning("[TutorialOverlay] UI/Panel/tut_bubble missing - plain bubble"); BUBBLE_GOLD = false; return; }
            float b = GOLD_BORDER;
            _bubbleEdge.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), GOLD_PPU, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
            _bubbleEdge.drawMode = SpriteDrawMode.Sliced; _bubbleEdge.color = Color.white;
            _bubbleFill.enabled = false;
            var bf = Resources.Load<TMP_FontAsset>("WordSlop Button SDF");
            if (bf != null) { _bubbleText.font = bf; _bubbleText.fontSharedMaterial = bf.material; }
            _bubbleText.fontStyle = FontStyles.Normal; _bubbleText.color = LevelIntroModal.V2_LABEL_INK; _bubbleText.richText = true;
            _bubbleText.lineSpacing = LESSON_LINE_SPACING;   // 11-06 recording: 0 left WordSlop's 1.6 em line height = 3 lines too tall -> autosize shrank the text tiny
            Sprite Load(string n) { var t = Resources.Load<Texture2D>("Owl3D/" + n); return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f) : null; }
            _owlOpen = Load("head"); _owlShut = Load("head_talk");
            if (_owlOpen != null)
            {
                _owl = new GameObject("TutOwl").AddComponent<SpriteRenderer>(); _owl.transform.SetParent(_bubble, false);
                _owl.sprite = _owlOpen; _owl.sortingOrder = BUBBLE_ORDER + 3;
            }
        }

        /// <summary>The word to spell (the first ALL-CAPS word of 3+ letters) goes on its own line, big and blue.</summary>
        private static string FormatLesson(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\b[A-Z]{3,}\b");
            if (!m.Success) return text;
            string before = text.Substring(0, m.Index).TrimEnd(), after = text.Substring(m.Index + m.Length);
            string tail = after.TrimStart(); string punct = "";
            while (tail.Length > 0 && (tail[0] == '!' || tail[0] == '.' || tail[0] == '?')) { punct += tail[0]; tail = tail.Substring(1).TrimStart(); }
            string word = $"<size=135%><color=#{ColorUtility.ToHtmlStringRGB(WORD_COLOR)}>{m.Value}</color></size>{punct}";
            string s = word;
            if (before.Length > 0) s = before + "\n" + s;
            if (tail.Length > 0) s = s + "\n" + tail;
            return s;
        }

        /// <summary>RM_TEXT: the fewest lines whose longest fits <paramref name="maxW"/>, split so the lines are as even as possible
        /// (balanced, not greedy). Measured with the bubble's own font + size.</summary>
        private List<string> BalancedLines(string text, float maxW) => BalancedLines(_bubbleText, text, maxW);
        /// <summary>Shared with BoosterIntro (11-06): same line breaking for every tutorial bubble.</summary>
        public static List<string> BalancedLines(TMP_Text tmp, string text, float maxW)
        {
            var words = text.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
            float W(int a, int b) => tmp.GetPreferredValues(string.Join(" ", words, a, b - a), 999f, 999f).x;
            List<string> best = null;
            for (int n = 1; n <= 4 && n <= words.Length; n++)
            {
                float bestMax = float.MaxValue; int[] bestCut = null;
                var cut = new int[n + 1]; cut[n] = words.Length;
                void Try(int k, int from)
                {
                    if (k == n) { float m = 0f; for (int i = 0; i < n; i++) m = Mathf.Max(m, W(cut[i], cut[i + 1])); if (m < bestMax) { bestMax = m; bestCut = (int[])cut.Clone(); } return; }
                    for (int c = from + 1; c <= words.Length - (n - k); c++) { cut[k] = c; Try(k + 1, c); }
                }
                cut[0] = 0; Try(1, 0);
                if (bestCut == null) continue;
                best = new List<string>();
                for (int i = 0; i < n; i++) best.Add(string.Join(" ", words, bestCut[i], bestCut[i + 1] - bestCut[i]));
                if (bestMax <= maxW) break;
            }
            return best ?? new List<string> { text };
        }

        /// <summary>RM_TEXT: the key word (first ALL-CAPS word of 3+ letters) in blue, inline - RM writes "create a ROCKET!".</summary>
        public static string Highlight(string s)
        {
            var m = System.Text.RegularExpressions.Regex.Match(s, @"\b[A-Z]{3,}(?:[ \n][A-Z]{2,})*\b");   // multi-word names too ("FREE SWAP")
            if (!m.Success) return s;
            return s.Substring(0, m.Index) + $"<color=#{ColorUtility.ToHtmlStringRGB(WORD_COLOR)}>{m.Value}</color>" + s.Substring(m.Index + m.Length);
        }

        private void OwlTalk(float secs)
        {
            if (_owl == null || _owlShut == null) return;
            _owlTalk?.Kill();
            _owlTalk = DOTween.Sequence().SetUpdate(true);
            int flaps = Mathf.Clamp(Mathf.RoundToInt(secs / 0.24f), 2, 8);
            for (int i = 0; i < flaps; i++)
            {
                _owlTalk.AppendCallback(() => { if (_owl != null) _owl.sprite = _owlOpen; }).AppendInterval(0.12f);
                _owlTalk.AppendCallback(() => { if (_owl != null) _owl.sprite = _owlShut; }).AppendInterval(0.12f);
            }
            _owlTalk.AppendCallback(() => { if (_owl != null) _owl.sprite = _owlOpen; });   // settles beak-open, friendly
        }

        private void SetAllVisible(bool on)
        {
            SetDimVisible(on); _hudCanvas.enabled = on && !HUD_LIT;
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
            OwlPortrait.TutorialFocus(cells != null && cells.Count > 0);   // 11-01: the owl looks down at the lesson
            OwlPortrait.Talk(0.25f + 0.045f * (text?.Length ?? 0));   // 11-02: he says the lesson line
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
            float hCells = BUBBLE_GOLD ? GOLD_H : BUBBLE_H;
            string rmText = null; float rmSize = 0f;
            if (BUBBLE_GOLD && RM_TEXT && !string.IsNullOrEmpty(text) && _bubbleText.font != null)
            {
                float screenW = cam.orthographicSize * 2f * cam.aspect;
                var fi = _bubbleText.font.faceInfo;
                float capEm = fi.capLine / Mathf.Max(1f, fi.pointSize);
                rmSize = RM_CAP_FRAC * screenW / Mathf.Max(0.1f, capEm) * 10f;   // TextMeshPro: fontSize 10 = 1 world unit em
                _bubbleText.enableAutoSizing = false; _bubbleText.fontSize = rmSize; _bubbleText.textWrappingMode = TextWrappingModes.NoWrap;
                var lines = BalancedLines(text, RM_TEXT_W_FRAC * screenW);
                rmText = Highlight(string.Join("\n", lines));
                float pitch = rmSize * 0.1f * (fi.lineHeight / Mathf.Max(1f, fi.pointSize) + LESSON_LINE_SPACING * 0.01f);
                float hW = lines.Count * pitch + 2f * RM_PAD_FRAC * screenW;
                hW = Mathf.Max(hW, 2f * GOLD_BORDER / GOLD_PPU + 0.05f);   // the 9-slice corners need this much
                bubbleWCells = RM_BUBBLE_W_FRAC * screenW / cs;
                hCells = hW / cs;
            }
            int bw = Mathf.RoundToInt(bubbleWCells * cs * 100f), bh = Mathf.RoundToInt(hCells * cs * 100f);
            if (BUBBLE_GOLD) { _bubbleEdge.size = new Vector2(bubbleWCells * cs, hCells * cs); _bubblePw = bw; _bubblePh = bh; }
            else if (bw != _bubblePw || bh != _bubblePh)
            {
                int edge = Mathf.Max(4, Mathf.RoundToInt(cs * 0.09f * 100f));
                int r = Mathf.RoundToInt(bh * 0.30f);
                _bubbleEdge.sprite = TileRenderer.CreateSolidRoundedRect(bw, bh, r, Color.white);
                _bubbleFill.sprite = TileRenderer.CreateSolidRoundedRect(bw - edge * 2, bh - edge * 2, Mathf.Max(2, r - edge), Color.white);
                _bubblePw = bw; _bubblePh = bh;
            }
            float bubbleH = hCells * cs;
            float top = grid.CellToWorld(0, RulesEngine.ROWS - 1).y + cs * 0.5f;
            float by = maxY + cs * 0.25f + bubbleH * 0.5f;
            if (by + bubbleH * 0.5f > top) by = minY - cs * 0.25f - bubbleH * 0.5f;
            float bx = grid.CellToWorld(RulesEngine.COLS / 2, 0).x;   // centred on the board (board-local), like RM
            _bubble.localPosition = new Vector3(bx, by, -1.2f);
            if (BUBBLE_GOLD)
            {
                bool owlOn = _owl != null && !HUD_LIT;
                if (_owl != null) _owl.enabled = owlOn;
                float room = owlOn ? OWL_ROOM : 0f;
                _bubbleText.rectTransform.sizeDelta = new Vector2((bubbleWCells - room - 0.7f) * cs, (hCells - 0.55f) * cs);
                _bubbleText.transform.localPosition = new Vector3(room * 0.5f * cs, TEXT_RIM_LIFT / GOLD_PPU, 0f);   // 11-06: centred on the CREAM, not the bubble
                // 11-06 Spencer: one-line tips ("Nice! Collect every gem to win!") autosized far bigger than the 3-line lessons.
                // Every tip is capped at the size a reference 3-line lesson gets in this bubble, so they all read the same.
                if (rmText != null) { _bubbleText.rectTransform.sizeDelta = new Vector2((bubbleWCells - room - 0.3f) * cs, hCells * cs); _bubbleText.text = rmText; }
                else
                {
                    _bubbleText.enableAutoSizing = true; _bubbleText.textWrappingMode = TextWrappingModes.Normal;
                    _bubbleText.fontSizeMax = 6f; _bubbleText.text = FormatLesson(SIZE_REFERENCE); _bubbleText.ForceMeshUpdate();
                    _bubbleText.fontSizeMax = _bubbleText.fontSize;
                    _bubbleText.text = FormatLesson(text ?? "");
                }
                if (owlOn && _owl.sprite != null)
                {
                    float k = OWL_CELLS * cs / Mathf.Max(0.01f, _owlOpen.bounds.size.y);
                    _owl.transform.localScale = Vector3.one * k;
                    _owl.transform.localPosition = new Vector3((-bubbleWCells * 0.5f + room * 0.42f) * cs, 0.12f * cs, -0.05f);
                    OwlTalk(0.25f + 0.045f * (text?.Length ?? 0));
                }
            }
            else
            {
                _bubbleText.rectTransform.sizeDelta = new Vector2((bubbleWCells - 0.6f) * cs, (BUBBLE_H - 0.35f) * cs);
                _bubbleText.text = text ?? "";
            }
            _bubble.gameObject.SetActive(!string.IsNullOrEmpty(text));
            _bubble.DOKill(); _bubble.localScale = Vector3.one * 0.6f;
            _bubble.DOScale(1f, 0.28f).SetEase(Ease.OutBack, 2f).SetUpdate(true);
            if (!string.IsNullOrEmpty(text)) GameAudio.Instance?.PlayKit("bubble_pop", 0.8f);   // 11-07 pick: Mouth Classic B3 (the bubble appeared silently)

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
            OwlPortrait.TutorialFocus(false);
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
