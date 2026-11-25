using System.Collections;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// MECHANIC SPLASH (2026-11-01, Spencer approved mock WordDrop_Art/composition/library_home/mech_splash_mock.png; RM 1-20
    /// recording frames rm_mechanic_splash.png). The first time a level teaches a POWER-UP (LevelRules Teach = rocket / bomb /
    /// disco / airplane) a full-screen splash replaces the level card: deep purple radial ground with slow rays, the name in the
    /// hero lettering, the recipe built from real tiles, the tutorial arrow (Resources/UI/tut_arrow.png) pointing down, and the
    /// power-up bursting in with a glow and sparkles. Tap to skip, or it moves on by itself after AUTO_SECS.
    /// RM only does this for power-ups; obstacles (crates, ice) are taught in-level by TutorialBeats (dim + one sentence).
    /// Done flags: PlayerPrefs "splash_&lt;key&gt;" (TutorialBeats.ResetAll clears them). ON = false -> the normal card.
    /// </summary>
    public class MechanicSplash : MonoBehaviour
    {
        public static bool ON = true;
        public static bool CAPTION = true;          // one short line under the name ("Make a 5-letter word"); not in the mock
        public static bool WAIT_FOR_TAP = true;     // 11-06 Spencer "shouldnt they wait for input from the player": no auto-close; false = AUTO_SECS
        public static bool  SPLASH_STATIC = true;                    // 11-06: RM loading-screen splash (still, auto-advance, tap skips)
        public static float STATIC_SECS = 2.5f, STATIC_TAP_GUARD = 0.4f;
        public static float AUTO_SECS = 4.2f, MIN_SECS = 0.5f;   // 11-03: 3.4 -> 4.2, one more line to read (the wild note)
        public static float HOLD_FOR_MAP = 0.32f;   // LevelMapPanel.FadeMapOut is 0.30s
        public static readonly string[] KEYS = { "rocket", "bomb", "disco", "airplane" };
        private const string PREF = "splash_";
        private const float RW = 1179f, RH = 2556f;   // reference canvas = the mock's pixels

        public static MechanicSplash Instance { get; private set; }
        public static bool Showing { get; private set; }

        private Canvas _canvas; private RectTransform _root; private CanvasGroup _group;
        private System.Action _onDone; private float _shownAt, _readyAt; private bool _closing, _preview;

        public static bool Done(string key) => PlayerPrefs.GetInt(PREF + key, 0) == 1;
        public static void ResetAll() { foreach (var k in KEYS) PlayerPrefs.DeleteKey(PREF + k); PlayerPrefs.Save(); }

        /// <summary>The splash key this level should show, or null.</summary>
        public static string KeyFor(int levelNum)
        {
            if (!ON || !SurvivalManager.FullBoardActive || !LevelRules.OwnsBoardSeed(levelNum) || LevelMapPanel.IsTutorialLevel(levelNum)) return null;
            string key = LevelRules.For(LevelMapPanel.RunLevel(levelNum)).Teach;
            if (string.IsNullOrEmpty(key) || System.Array.IndexOf(KEYS, key) < 0 || Done(key)) return null;
            return key;
        }

        /// <summary>Show the splash for <paramref name="key"/>; <paramref name="onDone"/> runs once it has faded out.</summary>
        public static void Show(string key, System.Action onDone, bool markDone = true)
        {
            if (Instance == null) { var go = new GameObject("MechanicSplash"); DontDestroyOnLoad(go); Instance = go.AddComponent<MechanicSplash>(); }
            if (markDone) { PlayerPrefs.SetInt(PREF + key, 1); PlayerPrefs.Save(); }
            Instance._preview = !markDone;   // FX menu preview: stays up until tapped (no auto-close)
            if (markDone) GameAudio.Instance?.StopMusic(0.35f);   // 11-01 Spencer: music stops while the splash shows (the level's track starts on PLAY)
            WDLog.Log($"[Splash] '{key}'");
            Instance.Present(key, onDone);
        }

        // ── recipes ──────────────────────────────────────────────────────────────────────────────────────────────────
        public static bool   SPLASH_TITLE_ANIM = false;   // title pop-in (11-06 off: the title is simply there)
        public static bool   SPLASH_TITLE_LIVE = true; public static float SPLASH_TITLE_CAP = 150f;   // live "Spellbound!" recipe (false = the baked splash_title_*.png)
        public static bool   SPLASH_EFFECT_LINE = false;
        public static bool   SPLASH_WILD_NOTE = false;     // 11-06 Spencer: "they will probably just learn this" - title + picture only, like RM   // 11-06: "Clears a whole row!" etc. (true brings it back)
        public static string WILD_NOTE = "Counts as ANY letter in a word!";   // 11-03 wild power-ups
        private struct Recipe { public string title, caption, effect; public string[] rows; public int crossCol; public string special; }
        private static Recipe For(string key)
        {
            switch (key)
            {
                case "bomb":     return new Recipe { title = "Bomb",     caption = "Cross two words",        effect = "Blasts the tiles around it!", rows = new[] { "BEAR" }, crossCol = 1, special = "special_bomb" };
                case "disco":    return new Recipe { title = "Royal Orb",    caption = "Make a 6-letter word",   effect = "Clears every tile of one letter!", rows = new[] { "PLANET" }, crossCol = -1, special = "special_disco" };
                case "airplane": return new Recipe { title = "Flying Tome",   /* 11-06 Spencer: "its no longer airplane" -> "lets call it flying tome" */ caption = "Make two words in one move", effect = "Flies to a goal and hits it!", rows = new[] { "FISH", "BIRD" }   /* 11-25: was CAT / DOG - three letters, which the game does not accept */, crossCol = -1, special = "special_airplane" };
                default:         return new Recipe { title = "Rocket",   caption = "Make a 5-letter word",   effect = "Clears a whole row!", rows = new[] { "TRAIN" }, crossCol = -1, special = "special_rocket" };
            }
        }

        private void Present(string key, System.Action onDone)
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            _onDone = onDone; _closing = false; Showing = true; _shownAt = Time.unscaledTime;
            var r = For(key);

            var cgo = new GameObject("MechanicSplashCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(CanvasGroup));
            cgo.transform.SetParent(transform, false);
            _canvas = cgo.GetComponent<Canvas>(); _canvas.renderMode = RenderMode.ScreenSpaceOverlay; _canvas.sortingOrder = 300;   // over the map + HUD, under ChapterEndCard
            var sc = cgo.GetComponent<CanvasScaler>(); sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(RW, RH); sc.matchWidthOrHeight = 0f;
            _root = (RectTransform)cgo.transform; _group = cgo.GetComponent<CanvasGroup>(); _group.alpha = 0f;

            // ground: the mock's own background, pre-rendered (Resources/UI/splash_bg.png, 1179x2556). 11-01 v1 built it at runtime
            // from a 128px gradient + a 256px ray texture stretched over the screen - blurry, and the LINEAR blend turned the 10%
            // rays milky (Spencer: "looks pretty terrible in comparison to the preview"). Fills the screen, cropping the long side.
            var bg = Img("Ground", _root, LoadSprite("UI/splash_bg") ?? Ground(), Color.white); bg.raycastTarget = true;
            SetAnchor(bg.rectTransform, 0.5f, 0.5f);
            var fit = bg.gameObject.AddComponent<AspectRatioFitter>(); fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; fit.aspectRatio = RW / RH;
            var tap = bg.gameObject.AddComponent<Button>(); tap.transition = Selectable.Transition.None; tap.onClick.AddListener(Close);

            // name + caption
            // the HERO lettering from the mock (gold rim backing, brown lip, soft shadow, cream face) - pre-rendered by mock_library_home.tier1
            // into Resources/UI/splash_title_<key>.png (11-01 Spencer: "werent we going to give ... the word rocket ... a backing").
            // Falls back to the HUD recipe text if the image is missing.
            RectTransform titleRT;
            var titleSpr = SPLASH_TITLE_LIVE && StageClearModal.HERO_GOLD ? null : LoadSprite("UI/splash_title_" + key);   // 11-06: live hero text
            if (titleSpr != null)
            {
                var ti = Img("Title", _root, titleSpr, Color.white);
                Place(ti.rectTransform, RW * 0.5f, RH * 0.28f, titleSpr.rect.width, titleSpr.rect.height); titleRT = ti.rectTransform;
            }
            else if (SPLASH_TITLE_LIVE && StageClearModal.HERO_GOLD)
            {   // 11-06 Spencer: "the spellbound ... is perfectly what i like. cant we just reproduce this for the splash screens?" -
                // the SAME live recipe as the win title (StageClearModal.ApplyHeroStyle), not a baked image
                var tt = Text("Title", r.title, SPLASH_TITLE_CAP / 0.594f / HUDManager.RECIPE_MAX_MUL, 0.28f, 1100f, 260f);
                StageClearModal.ApplyHeroStyle(tt); titleRT = tt.rectTransform;
            }
            else { var tt = Text("Title", r.title, 150f, 0.28f, 900f, 220f); HUDManager.ApplyRecipeCount(tt, HUDManager.HUD_INK, 0.22f, -0.9f); titleRT = tt.rectTransform; }
            TMP_Text cap = null;
            if (CAPTION) { cap = Text("Caption", r.caption, 60f, 0.36f, 1000f, 100f); HUDManager.ApplyRecipeCount(cap, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP); }

            // recipe tiles
            const float TS = 140f, STEP = 136f;
            float cy = RH * 0.47f;
            var tiles = new System.Collections.Generic.List<RectTransform>();
            float rowsH = r.rows.Length * TS + (r.rows.Length - 1) * 24f;
            float top = cy - rowsH * 0.5f, bottom = cy + rowsH * 0.5f;
            for (int ri = 0; ri < r.rows.Length; ri++)
            {
                string w = r.rows[ri]; float y = top + TS * 0.5f + ri * (TS + 24f);
                float x0 = RW * 0.5f - (w.Length - 1) * STEP * 0.5f;
                for (int i = 0; i < w.Length; i++) tiles.Add(Tile(w[i], x0 + i * STEP, y, TS));
                if (r.crossCol >= 0)
                {   // a word crossing down through the shared letter: P(E)NS. 11-25 (Spencer: "PEN is a 3 letter word") - the game needs 4+ letters, so the example does too
                    float x = x0 + r.crossCol * STEP;
                    tiles.Insert(0, Tile('P', x, y - STEP, TS)); tiles.Add(Tile('N', x, y + STEP, TS)); tiles.Add(Tile('S', x, y + STEP * 2f, TS));
                    top = Mathf.Min(top, y - STEP - TS * 0.5f); bottom = Mathf.Max(bottom, y + STEP * 2f + TS * 0.5f);
                }
            }

            // arrow, glow, power-up
            float ay = bottom + 115f, ry = ay + 265f;
            var arrow = Img("Arrow", _root, LoadSprite("UI/tut_arrow"), Color.white); arrow.preserveAspect = true;
            Place(arrow.rectTransform, RW * 0.5f, ay, 150f, 150f); arrow.rectTransform.localRotation = Quaternion.Euler(0, 0, 180f);
            var glow = ProgressComet.AddFx(_root, ProgressComet.GlowSprite, new Color(1f, 0.85f, 0.45f, 0.9f), 560f);
            Place(glow.rectTransform, RW * 0.5f, ry, 560f, 560f);
            var pu = Img("PowerUp", _root, LoadSprite("Specials/" + r.special), Color.white); pu.preserveAspect = true;
            Place(pu.rectTransform, RW * 0.5f, ry, 300f, 300f);
            if (r.special == "special_airplane" && global::WordDrop.Tile.ROTOR_ON && global::WordDrop.Tile.WINGS_ON && global::WordDrop.Tile.WingSprite != null)
            {   // 11-16 winged book: both wings spread (full size), behind the book
                var wsp = global::WordDrop.Tile.WingSprite; float ww = global::WordDrop.Tile.WING_WIDTH * 300f, wh = ww * wsp.rect.height / wsp.rect.width;
                for (int side = 1; side >= -1; side -= 2)
                {
                    var wi = Img(side > 0 ? "WingR" : "WingL", pu.rectTransform, wsp, Color.white); wi.preserveAspect = true;
                    var wr = wi.rectTransform; wr.anchorMin = wr.anchorMax = new Vector2(0.5f, 0.5f);
                    wr.pivot = new Vector2(0.040f, 0.362f);   // the root; the left wing is mirrored around it
                    wr.localScale = new Vector3(side, 1f, 1f);
                    float ux = side > 0 ? global::WordDrop.Tile.WING_MOUNT_UV.x : 1f - global::WordDrop.Tile.WING_MOUNT_UV.x;
                    wr.anchoredPosition = new Vector2((ux - 0.5f) * 300f, (global::WordDrop.Tile.WING_MOUNT_UV.y - 0.5f) * 300f);
                    wr.sizeDelta = new Vector2(ww, wh); wr.localRotation = Quaternion.Euler(0f, 0f, side * global::WordDrop.Tile.WING_BASE_DEG);
                    // a UI child always draws over its parent, so move the wing next to the book, just BEHIND it (keeps its placement)
                    wr.SetParent(pu.rectTransform.parent, true); wr.SetSiblingIndex(pu.rectTransform.GetSiblingIndex());
                }
            }
            else if (r.special == "special_airplane" && global::WordDrop.Tile.ROTOR_ON && global::WordDrop.Tile.ROTOR_FLIPBOOK && global::WordDrop.Tile.RotorFlipFrames != null)
            {   // 11-16 flipbook propeller: first frame, squashed to the board's angle
                var fi = Img("Rotor", pu.rectTransform, global::WordDrop.Tile.RotorFlipFrames[0], Color.white); fi.preserveAspect = false;
                var fr = fi.rectTransform; fr.anchorMin = fr.anchorMax = new Vector2(0.5f, 0.5f); fr.pivot = new Vector2(0.5f, 0.5f);
                fr.anchoredPosition = new Vector2((global::WordDrop.Tile.ROTOR_HUB_UV.x - 0.5f) * 300f, (global::WordDrop.Tile.ROTOR_HUB_UV.y + global::WordDrop.Tile.ROTOR_FLIP_LIFT - 0.5f) * 300f);
                fr.sizeDelta = new Vector2(global::WordDrop.Tile.ROTOR_FLIP_WIDTH * 300f, global::WordDrop.Tile.ROTOR_FLIP_WIDTH * global::WordDrop.Tile.ROTOR_FLIP_SQUASH * 300f);
            }
            else if (r.special == "special_airplane" && global::WordDrop.Tile.ROTOR_ON)
            {   // 11-06 Spencer: "the book is missing its propeller in the pic" - the rotor + gold hub are separate layers on the board
                // (Tile.UpdateRotor); same placement here: centred on the mast top (ROTOR_HUB_UV), ROTOR_WIDTH of the art wide
                foreach (var layer in new[] { "Specials/airplane_rotor", "Specials/airplane_rotor_hub" })
                {
                    var spr = LoadSprite(layer); if (spr == null) continue;
                    var li = Img(layer.EndsWith("hub") ? "RotorHub" : "Rotor", pu.rectTransform, spr, Color.white); li.preserveAspect = true;
                    var lr = li.rectTransform; lr.anchorMin = lr.anchorMax = new Vector2(0.5f, 0.5f); lr.pivot = new Vector2(0.5f, 0.5f);
                    lr.anchoredPosition = new Vector2((global::WordDrop.Tile.ROTOR_HUB_UV.x - 0.5f) * 300f, (global::WordDrop.Tile.ROTOR_HUB_UV.y + global::WordDrop.Tile.ROTOR_LIFT - 0.5f) * 300f);
                    lr.sizeDelta = Vector2.one * global::WordDrop.Tile.ROTOR_WIDTH * 300f;
                }
            }

            var hint = Text("TapHint", "Tap to continue", 62f, 0.90f, 900f, 100f); HUDManager.ApplyRecipeCount(hint, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP);   // 11-06: 52 -> 62 ("small and dim")
            // 11-03 (Spencer: "when introducing these powerups you should probably mention to the player that they are wild tiles"):
            // power-ups have no letter now (RulesEngine.SPECIALS_ARE_WILD) - say so under the power-up.
            TMP_Text wild = null;
            if (RulesEngine.SPECIALS_ARE_WILD && SPLASH_WILD_NOTE)
            {   // 11-06 (L1-10 recording: every splash said only the wild note - none said what the piece DOES): effect line + smaller note
                // 11-06 Spencer: "these splash screens have too much text" (RM shows the title + the picture) - the effect line is off
                string line = (!SPLASH_EFFECT_LINE || string.IsNullOrEmpty(r.effect)) ? WILD_NOTE : $"{r.effect}\n<size=78%>{WILD_NOTE}</size>";
                wild = Text("WildNote", line, 58f, (ry + 270f) / RH, 1000f, 190f); HUDManager.ApplyRecipeCount(wild, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP); wild.alpha = 0f;
                wild.richText = true;
            }

            // ── animation: ground, name pops, letters pop in, arrow drops, power-up bursts ──
            foreach (var t in tiles) t.localScale = Vector3.zero;
            titleRT.localScale = SPLASH_TITLE_ANIM ? Vector3.zero : Vector3.one; if (cap != null) cap.alpha = 0f;   // 11-06 Spencer: "dont give it an animation"
            arrow.color = new Color(1, 1, 1, 0); glow.rectTransform.localScale = Vector3.zero; pu.rectTransform.localScale = Vector3.zero; hint.alpha = 0f;
            var seq = DOTween.Sequence().SetUpdate(true).SetLink(cgo);
            seq.Append(_group.DOFade(1f, 0.18f));
            if (SPLASH_STATIC)
            {   // 11-06 Spencer: "the splash screens are basically just like loading screens" (RM) - everything is simply THERE (no
                // pop-ins, bobbing or sparkles), no "Tap to continue"; it moves on by itself after STATIC_SECS, a tap skips early
                foreach (var t in tiles) t.localScale = Vector3.one;
                titleRT.localScale = Vector3.one; if (cap != null) cap.alpha = 1f; if (wild != null) wild.alpha = 1f;
                arrow.color = Color.white; glow.rectTransform.localScale = Vector3.one; pu.rectTransform.localScale = Vector3.one;
                hint.gameObject.SetActive(false);
                _readyAt = _shownAt + STATIC_TAP_GUARD;
                GameAudio.Instance?.PlayWhooshFast();
                StartCoroutine(AutoClose());
                return;
            }
            if (SPLASH_TITLE_ANIM) seq.Insert(0.08f, titleRT.DOScale(1f, 0.38f).SetEase(Ease.OutBack, 2.2f));
            if (cap != null) seq.Insert(0.28f, cap.DOFade(1f, 0.25f));
            float t0 = 0.32f;
            for (int i = 0; i < tiles.Count; i++) seq.Insert(t0 + i * 0.055f, tiles[i].DOScale(1f, 0.26f).SetEase(Ease.OutBack, 2.4f));
            float ta = t0 + tiles.Count * 0.055f + 0.12f;
            var arT = arrow.rectTransform; Vector2 arP = arT.anchoredPosition;
            arT.anchoredPosition = arP + new Vector2(0f, 60f);
            seq.Insert(ta, arrow.DOFade(1f, 0.15f));
            seq.Insert(ta, arT.DOAnchorPos(arP, 0.3f).SetEase(Ease.OutBack, 2f));
            float tb = ta + 0.28f;
            seq.Insert(tb, glow.rectTransform.DOScale(1f, 0.35f).SetEase(Ease.OutQuad));
            seq.Insert(tb, pu.rectTransform.DOScale(1f, 0.42f).SetEase(Ease.OutBack, 2.6f));
            seq.InsertCallback(tb, () => Burst(pu.rectTransform, 18));
            seq.InsertCallback(tb, () => GameAudio.Instance?.PlayPowerupShimmer());
            if (wild != null) seq.Insert(tb + 0.35f, wild.DOFade(1f, 0.3f));
            seq.Insert(tb + 0.5f, hint.DOFade(1f, 0.3f));
            _readyAt = _shownAt + tb + 0.5f;   // 11-06: taps ignored until "Tap to continue" shows (the Royal Orb was skipped at ~1 s, unseen)
            seq.OnComplete(() =>
            {   // idle life until it closes
                if (pu == null) return;
                pu.rectTransform.DOScale(1.05f, 0.8f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(pu.gameObject);
                glow.DOFade(0.55f, 0.9f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(glow.gameObject);
                arT.DOAnchorPos(arP + new Vector2(0f, -14f), 0.6f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(arrow.gameObject);
                hint.DOFade(0.70f, 0.7f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(hint.gameObject);   // 11-06: dimmed to .45 -> .70
                hint.rectTransform.DOScale(1.06f, 0.7f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(hint.gameObject);   // gentle pulse
                StartCoroutine(IdleSparkles(pu.rectTransform));
            });
            GameAudio.Instance?.PlayWhooshFast();
            StartCoroutine(AutoClose());
        }

        private IEnumerator AutoClose()
        {
            while ((_preview || (WAIT_FOR_TAP && !SPLASH_STATIC) || Time.unscaledTime - _shownAt < (SPLASH_STATIC ? STATIC_SECS : AUTO_SECS)) && !_closing) yield return null;   // 11-06 Spencer: wait for the player
            Close();
        }

        private IEnumerator IdleSparkles(RectTransform at)
        {
            while (at != null && !_closing) { Burst(at, 1); yield return new WaitForSecondsRealtime(0.22f); }
        }

        private void Burst(RectTransform at, int n)
        {
            if (at == null) return;
            float k = _root.lossyScale.x;   // canvas units -> screen px
            for (int i = 0; i < n; i++)
            {
                float a = Random.value * Mathf.PI * 2f, d = Random.Range(90f, 200f);
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                Color c = Random.value < 0.6f ? new Color(1f, 0.94f, 0.75f, 1f) : Color.white;
                ProgressComet.Sparkle(_root, at.position + dir * d * k * (n == 1 ? 1f : 0.4f), Random.Range(26f, 50f), c,
                                      dir * Random.Range(60f, 160f) * k, Random.Range(0.6f, 0.95f));
            }
        }

        private void Close()
        {
            if (_closing || _canvas == null) return;
            if (Time.unscaledTime - _shownAt < MIN_SECS || (!_preview && Time.unscaledTime < _readyAt)) return;
            _closing = true;
            GameAudio.Instance?.PlayButtonClick();
            var cg = _group; var go = _canvas.gameObject;
            cg.blocksRaycasts = false;
            // START THE LEVEL UNDER THE SPLASH (11-01 Spencer: "its loading back to the main screen and then to the level"): the
            // hand-off fades the map out (0.30s) and then slides the board in, so fire it while the splash still covers the
            // screen, hold through the map fade, then dissolve straight onto the level.
            var cb = _onDone; _onDone = null; cb?.Invoke();
            DOTween.Sequence().SetUpdate(true).SetLink(go)
                .AppendInterval(cb != null ? HOLD_FOR_MAP : 0f)
                .Append(cg.DOFade(0f, 0.25f))
                .OnComplete(() => { if (go != null) Destroy(go); Showing = false; });
        }

        // ── builders ─────────────────────────────────────────────────────────────────────────────────────────────────
        private RectTransform Tile(char letter, float x, float y, float size)
        {
            var spr = LoadSprite(WordDrop.Tile.RESTING_FACE_ASSET);   // 11-02: follows the board face (board C ivory)
            float h = spr != null ? size * spr.rect.height / spr.rect.width : size;
            var im = Img("Tile_" + letter, _root, spr, Color.white); Place(im.rectTransform, x, y, size, h);
            var lg = new GameObject("L", typeof(RectTransform), typeof(TextMeshProUGUI)); lg.transform.SetParent(im.transform, false);
            var lt = lg.GetComponent<TextMeshProUGUI>(); Stretch(lt.rectTransform);
            var f = global::WordDrop.Tile.LetterFont(); if (f != null) lt.font = f;   // the board's letter face (Nunito Black; WordSlop if Tile.LETTER_WORDSLOP)
            bool slop = f != null && f.name.StartsWith("WordSlop");
            lt.text = letter.ToString(); lt.fontSize = size * (slop ? 0.95f : 0.62f); lt.color = global::WordDrop.Tile.LETTER_INK;
            lt.alignment = TextAlignmentOptions.Center; lt.raycastTarget = false; lt.rectTransform.anchoredPosition = new Vector2(0f, size * 0.03f);
            return im.rectTransform;
        }

        private TMP_Text Text(string name, string s, float size, float yFrac, float w, float h)
        {
            var g = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); g.transform.SetParent(_root, false);
            var t = g.GetComponent<TextMeshProUGUI>(); t.text = s; t.fontSize = size; t.alignment = TextAlignmentOptions.Center;
            t.textWrappingMode = TextWrappingModes.NoWrap; t.raycastTarget = false;
            Place(t.rectTransform, RW * 0.5f, RH * yFrac, w, h);
            return t;
        }

        private static Image Img(string name, Transform parent, Sprite s, Color c)
        {
            var g = new GameObject(name, typeof(RectTransform), typeof(Image)); g.transform.SetParent(parent, false);
            var im = g.GetComponent<Image>(); im.sprite = s; im.color = c; im.raycastTarget = false; return im;
        }
        /// <summary>Centre at (x, y) in mock pixels measured from the TOP-left.</summary>
        private static void Place(RectTransform rt, float x, float y, float w, float h)
        {
            SetAnchor(rt, x / RW, 1f - y / RH); rt.sizeDelta = new Vector2(w, h); rt.anchoredPosition = Vector2.zero;
        }
        private static void SetAnchor(RectTransform rt, float ax, float ay) { rt.anchorMin = rt.anchorMax = new Vector2(ax, ay); rt.pivot = new Vector2(0.5f, 0.5f); }
        private static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_sprites = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite LoadSprite(string path)
        {
            if (s_sprites.TryGetValue(path, out var sp) && sp != null) return sp;
            var tex = Resources.Load<Texture2D>(path);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
            s_sprites[path] = sp; return sp;
        }

        // the mock's ground: #6B2EC8 centre -> #220C4A edge, radial, centred 45% down
        private static Sprite s_ground, s_rays;
        private static Sprite Ground()
        {
            if (s_ground != null) return s_ground;
            const int W = 128, H = 256;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H]; Color a = new Color32(0x6B, 0x2E, 0xC8, 0xFF), b = new Color32(0x22, 0x0C, 0x4A, 0xFF);
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W - 0.5f, v = (1f - (y + 0.5f) / H) - 0.45f;   // v from the top
                float d = Mathf.Sqrt(u * u + Sq(v / 0.6f));                          // mock: x / W, (y - 0.45H) / 0.6H
                px[y * W + x] = Color.Lerp(a, b, Mathf.Clamp01(d / 0.75f));
            }
            t.SetPixels(px); t.Apply();
            return s_ground = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }
        // 16 soft rays fading out from the centre (white; tinted + faded by the Image colour)
        private static Sprite Rays()
        {
            if (s_rays != null) return s_rays;
            const int S = 256;
            var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[S * S];
            for (int y = 0; y < S; y++) for (int x = 0; x < S; x++)
            {
                float u = (x + 0.5f) / S - 0.5f, v = (y + 0.5f) / S - 0.5f, d = Mathf.Sqrt(u * u + v * v) * 2f;
                float ray = Mathf.SmoothStep(0.35f, 0.75f, Mathf.Cos(Mathf.Atan2(v, u) * 16f) * 0.5f + 0.5f);
                px[y * S + x] = new Color(1f, 1f, 1f, ray * Mathf.Clamp01(1f - d) * Mathf.Clamp01(d * 6f));
            }
            t.SetPixels(px); t.Apply();
            return s_rays = Sprite.Create(t, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }
        private static float Sq(float f) => f * f;
    }
}
