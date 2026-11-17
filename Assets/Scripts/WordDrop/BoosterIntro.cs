using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>BOOSTER UNLOCK TUTORIAL (2026-10-29, from Spencer's Royal Kingdom GIFs - breakdown in memory 10-29). The first
    /// level a booster slot opens on (TutorialLocks.BOOSTER_SLOT_UNLOCKS: 8/14/17/19), once the board has slid in:
    ///  1. dim everything at 72% except the new slot; a plaque pops (0.45 -> 1.2 -> 0.96 -> 1, ~0.27s) with the icon,
    ///     the name and "Tap to select it!"; the gold arrow bobs above the slot (0.3 cell, 1.27s). Gift: at least 3 charges.
    ///  2. after the slot tap, only the valid TARGET stays lit (one tile / a row / the best swap pair) with the rule and the
    ///     arrow over it; taps elsewhere are ignored (BoosterManager.TutorialAllowed).
    ///  3. the player uses it -> the dim lifts and the booster's own animation (BoosterFX) plays. Spyglass is one step.
    /// Done flags: PlayerPrefs "booster_intro_<id>". FX Test Menu replays any of them.</summary>
    public class BoosterIntro : MonoBehaviour
    {
        public static bool ON = true;
        public static float STEP_TIMEOUT = 20f;
        public static BoosterIntro Instance { get; private set; }
        public static bool Running { get; private set; }
        /// <summary>11-06: a booster lesson is about to start this level (TutorialBeats waits for it).</summary>
        public static bool Pending { get; private set; }
        private const string PREF = "booster_intro_";
        public static float DIM => UISeparation.MODAL_DIM.a;   // 11-02 shared dim (was black .72)

        private Canvas _canvas; private RectTransform _root;
        private Image[] _dim = new Image[4];
        private RectTransform _plaque, _arrow; private TextMeshProUGUI _body; private Image _plaqueImg;
        private RectTransform _free;
        private Image[] _corner = new Image[4]; private static Sprite s_quarter;   // 11-06: rounds the spotlight hole (a circle on the slot)
        private GameObject _hiddenBadge;   // 11-06 RM: a "Free" tag on the new slot until it's used
        private Tween _bob;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("BoosterIntro"); DontDestroyOnLoad(go); Instance = go.AddComponent<BoosterIntro>();
            HUDManager.OnLevelEntryDone += () => { if (Instance != null) Instance.StartCoroutine(Instance.MaybeRunForLevel()); };
        }
        public static void ResetAll() { foreach (var id in Order()) PlayerPrefs.DeleteKey(PREF + id); PlayerPrefs.Save(); }

        // 11-06 BUG (Spencer's L11-19 recording: stuck on "Tap on SPYGLASS" at L19, the spyglass opens at L22): this list must be
        // the booster ROW's order (BoosterHUDSlot.DISPLAY_ORDER: swap, mallet, comet, axe, spyglass) - slot N opens at
        // TutorialLocks.BOOSTER_SLOT_UNLOCKS[N]. It had spyglass before axe, so L19 taught the still-locked spyglass.
        private static string[] Order() => new[] { BoosterManager.ID_FREE_SWAP, BoosterManager.ID_MALLET, BoosterManager.ID_BRAMBLE_SWEEP, BoosterManager.ID_AXE, BoosterManager.ID_SPYGLASS };
        private static string Rule(string id) =>
            // 11-06: RM L8 wording ("Tap on the item to clear it from the board!")
            id == BoosterManager.ID_MALLET ? "Tap on the tile to smash it off the board!" :
            id == BoosterManager.ID_BRAMBLE_SWEEP ? "Tap on a tile to clear its whole row!" :
            id == BoosterManager.ID_FREE_SWAP ? "Swap these two tiles - it costs no move!" :
            id == BoosterManager.ID_AXE ? "Tap on a tile to chop its whole column!" :
            id == BoosterManager.ID_SPYGLASS ? "It shows you the best word on the board!" : "";

        private IEnumerator MaybeRunForLevel()
        {
            if (!ON || !SurvivalManager.FullBoardActive || Running) yield break;
            int open = TutorialLocks.BoosterSlotsOpen(); var ids = Order();
            string id = null;
            for (int i = Mathf.Min(open, ids.Length) - 1; i >= 0; i--) if (PlayerPrefs.GetInt(PREF + ids[i], 0) == 0) { id = ids[i]; break; }
            if (id == null) yield break;
            Pending = true;
            // the newest unlocked one only; older ones the player has already met count as done
            for (int i = 0; i < Mathf.Min(open, ids.Length); i++) if (ids[i] != id) PlayerPrefs.SetInt(PREF + ids[i], 1);
            yield return Run(id);
        }

        /// <summary>Run the intro for <paramref name="id"/> now (FX menu replay or the unlock level).</summary>
        public void Play(string id) { if (!Running) StartCoroutine(Run(id)); }

        private IEnumerator Run(string id)
        {
            Running = true; Pending = false;
            // never on top of a board lesson that's already up (both dims would stack)
            { float tw = 0f; while (TutorialOverlay.Instance != null && TutorialOverlay.Instance.IsShowing && tw < 30f) { tw += Time.unscaledDeltaTime; yield return null; } }
            // let pre-level boosters / the board settle first
            float w = 0f; while (w < 6f && MatchController.Instance != null && MatchController.Instance.IsProcessing) { w += Time.unscaledDeltaTime; yield return null; }
            yield return new WaitForSecondsRealtime(0.2f);
            var bm = BoosterManager.Instance; var slot = BoosterHUDSlot.Instance; var hand = HandManager.Instance;
            if (bm == null || slot == null || slot.SlotRect(id) == null) { Running = false; yield break; }
            int have = bm.GetCharges(id); if (have < 3) bm.AddCharges(id, 3 - have);   // the gift (RK: new booster arrives with 3)
            PlayerPrefs.SetInt(PREF + id, 1); PlayerPrefs.Save();
            _currentId = id;
            WDLog.Log($"[BoosterIntro] {id}: start");
            Build();
            bool prevInteract = hand != null && hand.IsInteractable; if (hand != null) hand.IsInteractable = false;   // no swaps mid-tutorial

            int startCharges = bm.GetCharges(id);
            bool untargeted = id == BoosterManager.ID_SPYGLASS;
            while (true)
            {
                // STEP 1: tap the slot
                var sr = SlotScreenRect(slot.SlotRect(id));
                string nm = (bm.GetBoosterById(id)?.DisplayName ?? id).ToUpperInvariant();
                ShowStep(sr, null, null, $"Tap on {nm} to select it!", true);   // 11-06 RM: "Tap on ROYAL HAMMER to select it!"
                ShowFree(sr);
                // 11-06 SAFETY (recording #3 soft-locked at L19 on a locked slot): if step 1 isn't done within STEP_TIMEOUT, give up -
                // the lesson closes, the gift charges stay. A lesson must never trap the player.
                float t1 = Time.unscaledTime;
                while (!(bm.AimMode && bm.ArmedBooster != null && bm.ArmedBooster.Id == id) && bm.GetCharges(id) >= startCharges && Time.unscaledTime - t1 < STEP_TIMEOUT) yield return null;
                if (Time.unscaledTime - t1 >= STEP_TIMEOUT) { WDLog.Log($"[BoosterIntro] {id}: step 1 timed out - closing"); break; }
                if (untargeted || bm.GetCharges(id) < startCharges) break;
                // STEP 2: only the target lit
                var target = PickTargets(id);
                BoosterManager.TutorialAllowed = new HashSet<Vector2Int>(target);
                if (_free != null) _free.gameObject.SetActive(false);   // 11-17: back on in step 1 (ShowFree) if the aim is cancelled
                ShowStep(CellsScreenRect(target), null, null, Rule(id), false);
                while (bm.AimMode && bm.GetCharges(id) >= startCharges) yield return null;
                BoosterManager.TutorialAllowed = null;
                if (bm.GetCharges(id) < startCharges) break;   // used
                // aim cancelled -> back to step 1
            }
            Hide();
            if (_free != null) _free.gameObject.SetActive(false);
            HideBadge(false);
            if (hand != null) hand.IsInteractable = true;
            WDLog.Log($"[BoosterIntro] {id}: done");
            Running = false;
        }

        // ── targets ──
        private static List<Vector2Int> PickTargets(string id)
        {
            var rules = RulesEngine.Instance; var res = new List<Vector2Int>();
            if (rules == null) return res;
            if (id == BoosterManager.ID_FREE_SWAP && rules.FindBestSwapHint(out var a, out var b, out _)) { res.Add(a); res.Add(b); return res; }
            // a goal piece if there is one (crate / gem), else a plain tile near the middle
            Vector2Int best = new Vector2Int(-1, -1); int bestScore = int.MinValue;
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 0; y < RulesEngine.ROWS; y++)
                {
                    var d = rules.GetCell(x, y);
                    if (d == null || d.IsDropTarget || d.IsAnchored) continue;   // 10-29: ice is a valid booster target now
                    int s = (d.IsStone ? 50 : 0) + (d.IsFrozen ? 45 : 0) + (d.HasGem ? 40 : 0) - Mathf.Abs(x - RulesEngine.COLS / 2) - Mathf.Abs(y - RulesEngine.ROWS / 2);
                    if (s > bestScore) { bestScore = s; best = new Vector2Int(x, y); }
                }
            if (best.x < 0) return res;
            if (id == BoosterManager.ID_BRAMBLE_SWEEP) { for (int x = 0; x < RulesEngine.COLS; x++) if (rules.GetCell(x, best.y) != null) res.Add(new Vector2Int(x, best.y)); }
            else if (id == BoosterManager.ID_AXE) { for (int y = 0; y < RulesEngine.ROWS; y++) if (rules.GetCell(best.x, y) != null) res.Add(new Vector2Int(best.x, y)); }
            else res.Add(best);
            return res;
        }

        // ── screen rects ──
        private static Rect SlotScreenRect(RectTransform rt)
        {
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            var canvas = rt.GetComponentInParent<Canvas>();
            Camera cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, c[0]), b = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            var r = Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
            float pad = r.width * 0.08f; return Rect.MinMaxRect(r.xMin - pad, r.yMin - pad, r.xMax + pad, r.yMax + pad);
        }
        private static Rect CellsScreenRect(List<Vector2Int> cells)
        {
            var grid = GridManager.Instance; var cam = Camera.main;
            if (grid == null || cam == null || cells.Count == 0) return new Rect(0, 0, 0, 0);
            float hx = grid.CellSize * 0.52f, hy = grid.RowPitch * 0.52f; Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in cells)
            {
                Vector3 w = grid.CellToWorld(p.x, p.y) + slide;
                Vector3 lo = cam.WorldToScreenPoint(w + new Vector3(-hx, -hy, 0)), hi = cam.WorldToScreenPoint(w + new Vector3(hx, hy, 0));
                x0 = Mathf.Min(x0, lo.x); y0 = Mathf.Min(y0, lo.y); x1 = Mathf.Max(x1, hi.x); y1 = Mathf.Max(y1, hi.y);
            }
            return Rect.MinMaxRect(x0, y0, x1, y1);
        }

        // ── UI ──
        private void Build()
        {
            if (_canvas != null) return;
            var go = new GameObject("BoosterIntroCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
            go.transform.SetParent(transform, false);
            _canvas = go.GetComponent<Canvas>(); _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // 11-07 Spencer ("sure 1"): like the board lessons (TutorialOverlay.HUD_LIT) the top HUD stays LIT - this canvas draws
            // just under the HUD's overlay canvas (order 50), so the HUD's own art is the lit edge. 260 = everything dimmed (old).
            _canvas.sortingOrder = TutorialOverlay.HUD_LIT ? 49 : 260;
            go.GetComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;   // 1 unit = 1 screen px: rects map 1:1
            _root = (RectTransform)go.transform;
            for (int i = 0; i < 4; i++)
            {
                var d = new GameObject("Dim" + i, typeof(RectTransform), typeof(Image)); d.transform.SetParent(_root, false);
                _dim[i] = d.GetComponent<Image>(); _dim[i].color = UISeparation.MODAL_DIM; _dim[i].raycastTarget = false;   // board taps are gated in code
                var r = (RectTransform)d.transform; r.anchorMin = r.anchorMax = Vector2.zero; r.pivot = Vector2.zero;
            }
            // bubble (11-06 Spencer: "the tutorial blurb should stay consistent throughout") - the SAME gold tutorial bubble,
            // text size and line breaking as TutorialOverlay (RM measured: cap 3.5% of screen width, bubble 76% wide)
            var p = new GameObject("Bubble", typeof(RectTransform), typeof(Image)); p.transform.SetParent(_root, false);
            _plaque = (RectTransform)p.transform; _plaque.anchorMin = _plaque.anchorMax = Vector2.zero; _plaque.pivot = new Vector2(0.5f, 0.5f);
            _plaqueImg = p.GetComponent<Image>(); _plaqueImg.raycastTarget = false; _plaqueImg.type = Image.Type.Sliced;
            var btex = Resources.Load<Texture2D>("UI/Panel/tut_bubble");
            if (btex != null)
            {
                float bb = TutorialOverlay.GOLD_BORDER;
                _plaqueImg.sprite = Sprite.Create(btex, new Rect(0, 0, btex.width, btex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(bb, bb, bb, bb));
            }
            else { _plaqueImg.sprite = MenuUI.GetRoundedRectSprite(40); _plaqueImg.color = new Color32(0xFF, 0xF8, 0xE6, 0xFF); }
            var tg = new GameObject("Text", typeof(RectTransform), typeof(TextMeshProUGUI)); tg.transform.SetParent(_plaque, false);
            var tr = (RectTransform)tg.transform; tr.anchorMin = tr.anchorMax = new Vector2(0.5f, 0.5f); tr.pivot = new Vector2(0.5f, 0.5f);
            _body = tg.GetComponent<TextMeshProUGUI>(); _body.raycastTarget = false; _body.richText = true;
            var bf = Resources.Load<TMP_FontAsset>("WordSlop Button SDF"); if (bf != null) { _body.font = bf; _body.fontSharedMaterial = bf.material; }
            _body.alignment = TextAlignmentOptions.Center; _body.textWrappingMode = TextWrappingModes.NoWrap; _body.enableAutoSizing = false;
            _body.color = LevelIntroModal.V2_LABEL_INK; _body.lineSpacing = TutorialOverlay.LESSON_LINE_SPACING;
            for (int i = 0; i < 4; i++)
            {
                var cgo = new GameObject("DimCorner" + i, typeof(RectTransform), typeof(Image)); cgo.transform.SetParent(_root, false);
                _corner[i] = cgo.GetComponent<Image>(); _corner[i].sprite = QuarterSprite(); _corner[i].raycastTarget = false;
                var cr = (RectTransform)cgo.transform; cr.anchorMin = cr.anchorMax = Vector2.zero; cr.pivot = Vector2.zero;
            }
            // "Free" tag (RM L8: a green "Free" label on the new booster's slot)
            var fg = new GameObject("FreeTag", typeof(RectTransform), typeof(Image)); fg.transform.SetParent(_root, false);
            _free = (RectTransform)fg.transform; _free.anchorMin = _free.anchorMax = Vector2.zero; _free.pivot = new Vector2(0.5f, 0.5f);
            var fi = fg.GetComponent<Image>(); fi.sprite = MenuUI.GetRoundedRectSprite(40); fi.type = Image.Type.Sliced; fi.color = new Color32(0x3C, 0xB4, 0x3C, 0xFF); fi.raycastTarget = false;
            var fo = fg.AddComponent<Outline>(); fo.effectColor = new Color32(0x1A, 0x0C, 0x38, 0xFF); fo.effectDistance = new Vector2(3, -3);
            var ft = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); ft.transform.SetParent(_free, false);
            var ftr = (RectTransform)ft.transform; ftr.anchorMin = Vector2.zero; ftr.anchorMax = Vector2.one; ftr.offsetMin = ftr.offsetMax = Vector2.zero;
            var ftt = ft.GetComponent<TextMeshProUGUI>(); ftt.text = "Free"; ftt.alignment = TextAlignmentOptions.Center; ftt.raycastTarget = false;
            ftt.enableAutoSizing = true; ftt.fontSizeMin = 8f; ftt.fontSizeMax = 200f;
            HUDManager.ApplyRecipeCount(ftt, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP);
            fg.SetActive(false);
            // 11-17 (Spencer's L19 shot: the green "Free" tag sat ON the lesson bubble and covered a word): the bubble draws over the
            // tag (it was created earlier = underneath). Step 2 also hides the tag - by then the slot is not what is being shown.
            _plaque.SetSiblingIndex(_free.GetSiblingIndex());
            // arrow (points DOWN at the target)
            var a = new GameObject("Arrow", typeof(RectTransform), typeof(Image)); a.transform.SetParent(_root, false);
            _arrow = (RectTransform)a.transform; _arrow.anchorMin = _arrow.anchorMax = Vector2.zero; _arrow.pivot = new Vector2(0.5f, 0f);
            var ai = a.GetComponent<Image>(); ai.preserveAspect = true; ai.raycastTarget = false;
            var tex = Resources.Load<Texture2D>("UI/tut_arrow");
            if (tex != null) ai.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _arrow.localRotation = Quaternion.Euler(0, 0, 180f);
            _canvas.gameObject.SetActive(false);
        }
        /// <summary>Dim-coloured square with a quarter circle cut out of its top-right: 4 of these in the hole's corners make the
        /// rectangle a rounded rect (radius = the piece size) - a circle when the radius is half the hole.</summary>
        private static Sprite QuarterSprite()
        {
            if (s_quarter != null) return s_quarter;
            const int N = 128; var tex = new Texture2D(N, N, TextureFormat.RGBA32, false); tex.wrapMode = TextureWrapMode.Clamp;
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {   // circle centred on the piece's top-right corner (N, N), radius N
                float d = Mathf.Sqrt((N - 0.5f - x) * (N - 0.5f - x) + (N - 0.5f - y) * (N - 0.5f - y));
                px[y * N + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01(d - N + 1f)));
            }
            tex.SetPixels32(px); tex.Apply(false, true);
            return s_quarter = Sprite.Create(tex, new Rect(0, 0, N, N), Vector2.zero, 100f);
        }
        private void PlaceCorners(Rect hole, float r, Color c, bool fadeIn)
        {
            r = Mathf.Min(r, Mathf.Min(hole.width, hole.height) * 0.5f);
            // BL, BR, TL, TR: pieces sit INSIDE the hole's corners; scale flips put the cut-out toward the hole's centre
            var at = new[] { new Vector2(hole.xMin, hole.yMin), new Vector2(hole.xMax, hole.yMin), new Vector2(hole.xMin, hole.yMax), new Vector2(hole.xMax, hole.yMax) };
            var flip = new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) };
            for (int i = 0; i < 4; i++)
            {
                var rt = _corner[i].rectTransform; rt.anchoredPosition = at[i]; rt.sizeDelta = new Vector2(r, r); rt.localScale = new Vector3(flip[i].x, flip[i].y, 1f);
                if (fadeIn) { _corner[i].color = new Color(c.r, c.g, c.b, 0f); _corner[i].DOFade(c.a, 0.13f).SetUpdate(true); } else _corner[i].color = c;
            }
        }
        private void HideBadge(bool hide)
        {
            if (!hide) { if (_hiddenBadge != null) _hiddenBadge.SetActive(true); _hiddenBadge = null; return; }
            if (_hiddenBadge != null) return;
            var slot = BoosterHUDSlot.Instance; var rt = slot != null ? slot.SlotRect(_currentId) : null; if (rt == null) return;
            foreach (var t in rt.GetComponentsInChildren<Transform>(true)) if (t.name == "Badge" && t.gameObject.activeSelf) { _hiddenBadge = t.gameObject; t.gameObject.SetActive(false); break; }
        }
        private string _currentId;
        private void ShowFree(Rect slot)
        {
            if (_free == null) return;
            // 11-06 Spencer: the tag sat on top of the red charge badge - hide the slot's badge while the lesson runs, and hug the
            // tag to the slot's bottom rim (centred, a little narrower)
            HideBadge(true);
            float w = slot.width * 0.62f, h = w * 0.40f;
            _free.sizeDelta = new Vector2(w, h); _free.anchoredPosition = new Vector2(slot.center.x, slot.yMin + slot.height * 0.10f);
            if (!_free.gameObject.activeSelf) { _free.gameObject.SetActive(true); _free.localScale = Vector3.zero; _free.DOScale(1f, 0.25f).SetEase(Ease.OutBack).SetUpdate(true); }
        }

        private void ShowStep(Rect hole, Sprite icon, string title, string body, bool plaqueAbove)
        {
            if (_canvas == null) return;
            bool first = !_canvas.gameObject.activeSelf;
            _canvas.gameObject.SetActive(true);
            float W = Screen.width, H = Screen.height;
            Place(_dim[0], 0, hole.yMax, W, H - hole.yMax);          // above
            Place(_dim[1], 0, 0, W, hole.yMin);                      // below
            Place(_dim[2], 0, hole.yMin, hole.xMin, hole.height);    // left
            Place(_dim[3], hole.xMax, hole.yMin, W - hole.xMax, hole.height);   // right
            // 11-06 Spencer: "the spot light on the booster is a square when it should be formed around the button" - round it: a
            // circle on the slot, soft rounded corners on board targets
            PlaceCorners(hole, plaqueAbove ? Mathf.Min(hole.width, hole.height) * 0.5f : Mathf.Min(hole.width, hole.height) * 0.30f, TutorialOverlay.DIM, first);
            if (first) foreach (var d in _dim) { var dc = TutorialOverlay.DIM; d.color = new Color(dc.r, dc.g, dc.b, 0f); d.DOFade(dc.a, 0.13f).SetUpdate(true); }   // 11-06: the lessons' purple dim
            // plaque: centred, above the hole (or below it if there's no room)
            // same metrics as TutorialOverlay RM_TEXT, in screen px
            var fa = _body.font; float capEm = fa != null ? fa.faceInfo.capLine / Mathf.Max(1f, fa.faceInfo.pointSize) : 0.6f;
            _body.fontSize = TutorialOverlay.RM_CAP_FRAC * W / Mathf.Max(0.1f, capEm);
            var lines = TutorialOverlay.BalancedLines(_body, body ?? "", TutorialOverlay.RM_TEXT_W_FRAC * W);
            _body.text = TutorialOverlay.Highlight(string.Join("\n", lines));
            float lh = fa != null ? fa.faceInfo.lineHeight / Mathf.Max(1f, fa.faceInfo.pointSize) : 1.6f;
            float pitch = _body.fontSize * (lh + TutorialOverlay.LESSON_LINE_SPACING * 0.01f);
            float pw = TutorialOverlay.RM_BUBBLE_W_FRAC * W, ph = lines.Count * pitch + 2f * TutorialOverlay.RM_PAD_FRAC * W;
            // rim as thick as the world bubble's: GOLD_BORDER tut_bubble px = GOLD_BORDER / GOLD_PPU world units
            float pxPerUnit = Camera.main != null ? H / (2f * Camera.main.orthographicSize) : 100f;
            _plaqueImg.pixelsPerUnitMultiplier = TutorialOverlay.GOLD_PPU / Mathf.Max(1f, pxPerUnit);
            ph = Mathf.Max(ph, 2f * TutorialOverlay.GOLD_BORDER / TutorialOverlay.GOLD_PPU * pxPerUnit + 4f);
            _plaque.sizeDelta = new Vector2(pw, ph);
            ((RectTransform)_body.transform).sizeDelta = new Vector2(pw - 0.05f * W, ph);
            ((RectTransform)_body.transform).anchoredPosition = new Vector2(0f, TutorialOverlay.TEXT_RIM_LIFT / TutorialOverlay.GOLD_PPU * pxPerUnit);   // centred on the cream
            float py = hole.yMax + ph * 0.5f + H * 0.11f; if (py + ph * 0.5f > H * 0.92f) py = hole.yMin - ph * 0.5f - H * 0.06f;
            _plaque.anchoredPosition = new Vector2(W * 0.5f, py);
            _plaque.DOKill(); _plaque.localScale = Vector3.one * 0.45f;
            GameAudio.Instance?.PlayKit("bubble_pop", 0.8f);   // 11-07 pick: Mouth Classic B3
            DOTween.Sequence().SetUpdate(true).SetTarget(_plaque)
                .Append(_plaque.DOScale(1.2f, 0.07f).SetEase(Ease.OutQuad))
                .Append(_plaque.DOScale(0.96f, 0.10f).SetEase(Ease.InOutQuad))
                .Append(_plaque.DOScale(1f, 0.10f).SetEase(Ease.OutQuad));
            // arrow above the hole, bobbing
            float aw = Mathf.Clamp(hole.width * 0.55f, 70f, 140f);
            _arrow.sizeDelta = new Vector2(aw, aw);
            float baseY = hole.yMax + aw + 8f;   // rotated 180 about its bottom pivot: the tip sits at baseY - aw
            _arrow.anchoredPosition = new Vector2(hole.center.x, baseY);
            _bob?.Kill();
            float bobPx = (GridManager.Instance != null && Camera.main != null) ? Mathf.Abs(Camera.main.WorldToScreenPoint(Vector3.up * GridManager.Instance.CellSize * 0.3f).y - Camera.main.WorldToScreenPoint(Vector3.zero).y) : 18f;
            _bob = _arrow.DOAnchorPosY(baseY + bobPx, 0.635f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true);
            // 11-07: the bubble_pop above replaces this whoosh (they doubled up)
        }
        private static void Place(Image img, float x, float y, float w, float h)
        { var r = img.rectTransform; r.anchoredPosition = new Vector2(x, y); r.sizeDelta = new Vector2(Mathf.Max(0, w), Mathf.Max(0, h)); }

        private void Hide()
        {
            _bob?.Kill();
            if (_canvas == null || !_canvas.gameObject.activeSelf) return;
            foreach (var d in _dim) d.DOFade(0f, 0.15f).SetUpdate(true);
            foreach (var d in _corner) if (d != null) d.DOFade(0f, 0.15f).SetUpdate(true);
            _plaque.DOScale(0f, 0.15f).SetEase(Ease.InBack).SetUpdate(true);
            var c = _canvas;
            DOVirtual.DelayedCall(0.17f, () => { if (c != null) c.gameObject.SetActive(false); }, ignoreTimeScale: true);
        }
    }
}
