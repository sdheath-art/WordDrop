using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// TREASURE CHEST REWARD (2026-11-27, Spencer: "the opening of a chest would be a big event ... much like in vampire survivors
    /// where it random rolls and you get your pick of 3 rewards"; "the chance of something really good dropping should be less and
    /// coins should drop more often"). PLAIN first version: when a chest has been opened and the board has settled, three reward
    /// cards are rolled from a weighted table and the player picks ONE. Card 1 is always coins; the other two come from the table
    /// (coins are the heaviest entries, the big rewards the lightest). No dismiss on an outside tap - a pick is required.
    /// </summary>
    public class ChestRewardModal : MonoBehaviour
    {
        private static bool s_open;
        public static bool Open { get => s_open; private set { s_open = value; WDInput.Blocked = value; } }   // the board is frozen while the event is up
        public static bool ENABLED = true;
        private static ChestRewardModal s_inst;

        private enum Kind { Coins, PowerUps, Moves, Booster }
        private struct Reward { public Kind kind; public int amount; public int tier; public float weight; public string title; public string sub; public string id; public string icon; }
        // tier: 0 common, 1 rare, 2 epic. Weights are relative.
        private static readonly Reward[] TABLE =
        {
            new Reward { kind = Kind.Coins,    amount = 20,  tier = 0, weight = 34f, title = "20 COINS",     sub = "Common" },
            new Reward { kind = Kind.Coins,    amount = 40,  tier = 0, weight = 22f, title = "40 COINS",     sub = "Common" },
            new Reward { kind = Kind.PowerUps, amount = 1,   tier = 0, weight = 14f, title = "1 POWER-UP",   sub = "Common - lands on the board" },
            new Reward { kind = Kind.Moves,    amount = 2,   tier = 0, weight = 12f, title = "+2 MOVES",     sub = "Common" },
            new Reward { kind = Kind.Coins,    amount = 100, tier = 1, weight = 7f,  title = "100 COINS",    sub = "RARE" },
            new Reward { kind = Kind.Moves,    amount = 5,   tier = 1, weight = 5f,  title = "+5 MOVES",     sub = "RARE" },
            new Reward { kind = Kind.PowerUps, amount = 3,   tier = 1, weight = 4f,  title = "3 POWER-UPS",  sub = "RARE - land on the board" },
            // 11-27 (Spencer: "booster bonuses should be included as well"): one charge of an in-level booster - rare, they are what the shop sells
            new Reward { kind = Kind.Booster,  amount = 1,   tier = 1, weight = 3f,  title = "HAMMER",       sub = "RARE", id = BoosterManager.ID_MALLET,        icon = "1hammer" },
            new Reward { kind = Kind.Booster,  amount = 1,   tier = 1, weight = 3f,  title = "MAGIC GLOVE",  sub = "RARE", id = BoosterManager.ID_FREE_SWAP,     icon = "2glove" },
            new Reward { kind = Kind.Booster,  amount = 1,   tier = 1, weight = 2.5f, title = "AXE",         sub = "RARE", id = BoosterManager.ID_AXE,           icon = "1axe" },
            new Reward { kind = Kind.Booster,  amount = 1,   tier = 1, weight = 2.5f, title = "SPYGLASS",    sub = "RARE", id = BoosterManager.ID_SPYGLASS,      icon = "2spyglass" },
            new Reward { kind = Kind.Booster,  amount = 1,   tier = 2, weight = 1.2f, title = "LIGHTNING",   sub = "EPIC", id = BoosterManager.ID_BRAMBLE_SWEEP, icon = "1goldlightning" },
            new Reward { kind = Kind.Moves,    amount = 10,  tier = 2, weight = 1.2f, title = "+10 MOVES",   sub = "EPIC" },
            new Reward { kind = Kind.Coins,    amount = 300, tier = 2, weight = 0.8f, title = "300 COINS",   sub = "EPIC" },
        };
        private static readonly Color[] TIER_BG = { new Color(0.25f, 0.55f, 0.85f, 1f), new Color(0.62f, 0.32f, 0.86f, 1f), new Color(0.95f, 0.66f, 0.12f, 1f) };

        // ── THE EVENT (11-27, after Spencer's Vampire Survivors recording: "i want it to be an event not just like a panel"). The board
        // dims, the chest lands big and shakes, a tap bursts it open, three beams of light fan up with reward icons racing along them
        // like slot reels, and each beam stops in turn on one reward (a bigger burst for rare / epic). The three landed rewards are
        // the pick. A tap during the spin lands everything at once. All unscaled time. ──
        public static float SPIN_FIRST = 1.9f, SPIN_GAP = 0.7f, BEAM_LEN = 1250f, BEAM_W = 150f, BEAM_ANGLE = 18f, LAND_AT = 455f;   // beams run off the top of the screen; rewards land LAND_AT up them
        private Canvas _canvas; private RectTransform _root; private float _settled;
        public static float TITLE_STROKE = 0.30f, DIM = 0.93f, TOP_SHADE = 0.75f;   // title outline width; how dark the board goes; extra darkness behind the title (fades out below it)
        public static bool TIER_TAGS = false;    // 11-27 Spencer: "maybe just remove the epic and rare writing altogether" - the glow's colour and size carry the tier
        public static float STRETCH = 0.82f;     // 11-27 Spencer: "stretch just a hair less" - scales the straining chest's squash, stretch and hop (1 = as first built)
        public static bool CHEST_GLOW = false;   // 11-27 Spencer: "just remove the glow from the chest for now" (true = the rayed gold burst behind it)
        private RawImage _chest, _front, _seam, _seamRays; private RectTransform _coins, _glowLayer, _numLayer; private TMPro.TextMeshProUGUI _title, _hint; private Button _openBtn;
        private const float CHEST = 250f, FRONT_CUT = 0.46f, MOUTH_Y = 0.272f;   // FRONT_CUT: share of the open-chest art below its mouth
        private readonly RectTransform[] _beam = new RectTransform[3]; private readonly List<RectTransform>[] _reel = { new List<RectTransform>(), new List<RectTransform>(), new List<RectTransform>() };
        private readonly GameObject[] _landed = new GameObject[3];
        private Reward[] _rolled; private bool _spinning, _skip; private int _stage;   // 0 closed, 1 spinning, 2 pick
        private static Texture2D s_shadeTex; private static Texture2D s_beamTex, s_burstTex, s_discTex; private static Texture2D[] s_icons;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (s_inst != null) return;
            var go = new GameObject("ChestRewardModal"); DontDestroyOnLoad(go); s_inst = go.AddComponent<ChestRewardModal>();
        }

        private void Update()
        {
            if (Open || !ENABLED) return;
            if (RulesEngine.ChestsPending <= 0) { _settled = 0f; return; }
            var sm = SurvivalManager.Instance; var hm = HandManager.Instance;
            if (sm == null || hm == null || !SurvivalManager.FullBoardActive || SurvivalManager.AwaitingNextLevel) { RulesEngine.ChestsPending = 0; ClearHover(); return; }   // level over: the chest's prize is forfeit
            if (hm.IsResolvingChain) { _settled = 0f; return; }
            _settled += Time.unscaledDeltaTime;
            if (_settled < 0.7f) return;   // let the fall finish before the board dims
            _settled = 0f; RulesEngine.ChestsPending--; Show();
        }

        // ── ON THE BOARD (11-27 Spencer: "when you destroy a treasure chest on the board it needs to be a little bit more fantastic than
        // just disappearing or like every other piece"). The chest does not break: it LEAPS off its square - a flash, a ring of
        // light, gold sparks and a burst of coins - and hangs there glowing, bobbing and rattling above the board while the letters
        // settle underneath. Then that same chest flies up to centre stage and the event begins.
        private static GameObject s_hover; private Vector2 _home;
        public static float WAIT_SCALE = 0.86f, HOVER_SCALE = 2.6f;   // the waiting chest's size (1 = its size on the spot); the board chest's hover size in cells
        public static void ClearHover() { if (s_hover != null) { Destroy(s_hover); s_hover = null; } }
        public static void BoardChestOpened(Vector3 world, float cell)
        {
            if (s_inst == null) Boot();
            if (s_hover != null) Destroy(s_hover);
            var tex = Resources.Load<Texture2D>("Blockers/chest"); if (tex == null) return;
            s_hover = new GameObject("ChestHover"); s_hover.transform.position = world + new Vector3(0f, 0f, -1f);
            var sr = s_hover.AddComponent<SpriteRenderer>(); sr.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width); sr.sortingOrder = 58;
            LoadArt();
            var ring = new GameObject("Ring"); ring.transform.SetParent(s_hover.transform, false); ring.transform.localPosition = new Vector3(0f, 0f, 0.01f);
            var rr = ring.AddComponent<SpriteRenderer>(); rr.sprite = Sprite.Create(s_burstTex, new Rect(0, 0, s_burstTex.width, s_burstTex.height), new Vector2(0.5f, 0.5f), s_burstTex.width); rr.sortingOrder = 57; rr.color = new Color(1f, 0.9f, 0.5f, 0.95f);
            WordDropFX.Instance?.PlayChestCoinBurst(world, cell * 1.6f); WordDropFX.Instance?.PlayScreenFlash(1);
            GridManager.Instance?.ShakeBoard(0.2f, 0.28f); HapticsManager.Strong();
            GameAudio.Instance?.PlayCrateBreak(); GameAudio.Instance?.PlayCoinLand();
            s_inst.StartCoroutine(s_inst.Hover(s_hover.transform, rr, cell));
        }
        private System.Collections.IEnumerator Hover(Transform tr, SpriteRenderer ring, float cell)
        {
            Vector3 home = tr.position; float t = 0f;
            while (tr != null)
            {
                t += Time.unscaledDeltaTime;
                float pop = t < 0.28f ? Mathf.LerpUnclamped(1f, HOVER_SCALE, 1f - Mathf.Pow(1f - t / 0.28f, 3f)) + Mathf.Sin(t / 0.28f * Mathf.PI) * 0.5f : HOVER_SCALE + Mathf.Sin(t * 5f) * 0.08f;
                float lift = Mathf.Min(1f, t / 0.28f) * cell * 0.75f + Mathf.Sin(t * 3.2f) * cell * 0.09f;
                tr.position = home + new Vector3(0f, lift, 0f); tr.localScale = Vector3.one * cell * pop;
                tr.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(t * 26f) * 4f * Mathf.Clamp01(Mathf.Sin(t * 2.4f)));
                if (ring != null) { float rk = Mathf.Clamp01(t / 0.45f); ring.transform.localScale = Vector3.one * Mathf.Lerp(0.8f, 3.6f, 1f - (1f - rk) * (1f - rk)); ring.transform.localRotation = Quaternion.Euler(0f, 0f, t * 70f); ring.color = new Color(1f, 0.9f, 0.5f, Mathf.Lerp(0.95f, 0.28f + Mathf.Sin(t * 6f) * 0.1f, rk)); }
                yield return null;
            }
        }

        /// <summary>FX Test Menu: play the event now, wherever the game is. forceTier 1 / 2 = the middle card is a rare / an epic.</summary>
        public static void DebugOpen(int forceTier)
        {
            if (s_inst == null) Boot();
            if (Open) return;
            s_inst._forceTier = forceTier; s_inst.Show();
        }
        private int _forceTier = -1;

        private static Reward Roll(System.Predicate<Reward> ok)
        {
            float total = 0f; foreach (var r in TABLE) if (ok(r)) total += r.weight;
            float pick = Random.value * total;
            foreach (var r in TABLE) { if (!ok(r)) continue; pick -= r.weight; if (pick <= 0f) return r; }
            return TABLE[0];
        }

        private static Texture2D Tex(int w, int h, System.Func<float, float, Color> f)
        {
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = f((x + 0.5f) / w, (y + 0.5f) / h);
            t.SetPixels(px); t.Apply(); return t;
        }
        private static void LoadArt()
        {
            if (s_beamTex != null) return;
            s_beamTex = Tex(64, 256, (u, v) => { float w = Mathf.Lerp(0.22f, 1f, Mathf.Pow(v, 0.55f)), e = 1f - Mathf.Abs(u - 0.5f) * 2f / w; return new Color(1f, 1f, 1f, Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(e) * 1.5f) * Mathf.Lerp(1f, 0.55f, v)); });   // a cone: narrow at the chest's mouth, widening upward
            s_burstTex = Tex(128, 128, (u, v) =>
            {
                float dx = u - 0.5f, dy = v - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f, ang = Mathf.Atan2(dy, dx);
                float ray = Mathf.Pow(Mathf.Abs(Mathf.Cos(ang * 6f)), 6f), core = Mathf.Clamp01(1f - r * 1.9f);
                return new Color(1f, 1f, 1f, Mathf.Clamp01(core * 1.2f + ray * Mathf.Clamp01(1f - r) * 0.8f));
            });
            s_discTex = Tex(96, 96, (u, v) => { float dx = u - 0.5f, dy = v - 0.5f, r = Mathf.Sqrt(dx * dx + dy * dy) * 2f; return new Color(1f, 1f, 1f, Mathf.Clamp01((1f - r) * 14f)); });
            s_icons = new[] { Resources.Load<Texture2D>("Tiles/coin3d_icon"), Resources.Load<Texture2D>("Specials/special_rocket"), Resources.Load<Texture2D>("UI/Chest/moves"), Resources.Load<Texture2D>("Specials/special_bomb"),
                              Resources.Load<Texture2D>("Boosters/1hammer"), Resources.Load<Texture2D>("Boosters/2glove"), Resources.Load<Texture2D>("Boosters/1axe"), Resources.Load<Texture2D>("Boosters/1goldlightning") };
        }
        private static Texture2D IconFor(Reward r) => r.kind == Kind.Booster ? Resources.Load<Texture2D>("Boosters/" + r.icon) : r.kind == Kind.Coins ? s_icons[0] : r.kind == Kind.PowerUps ? (r.amount > 1 ? s_icons[3] : s_icons[1]) : null;

        private RectTransform Node(string name, Transform parent, Vector2 anchor, Vector2 size, Vector2 pivot)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>(); rt.anchorMin = rt.anchorMax = anchor; rt.pivot = pivot; rt.sizeDelta = size; rt.anchoredPosition = Vector2.zero; return rt;
        }
        private RawImage Pic(string name, Transform parent, Vector2 anchor, Vector2 size, Texture tex, Color c, Vector2? pivot = null)
        {
            var rt = Node(name, parent, anchor, size, pivot ?? new Vector2(0.5f, 0.5f)); var ri = rt.gameObject.AddComponent<RawImage>(); ri.texture = tex; ri.color = c; ri.raycastTarget = false; return ri;
        }
        // 11-27 (Spencer: "for the lettering you arent using the fonts that match our game"): the game's own text. hero = the gold
        // "Spellbound!" title recipe (StageClearModal.ApplyHeroStyle); otherwise the HUD's cream letters with the purple outline and drop.
        private TMPro.TextMeshProUGUI Txt(string name, Transform parent, Vector2 anchor, Vector2 size, string s, int fs, Color c, bool hero = false)
        {
            var rt = Node(name, parent, anchor, size, new Vector2(0.5f, 0.5f)); var t = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
            t.text = s; t.fontSize = fs; t.alignment = TMPro.TextAlignmentOptions.Center; t.raycastTarget = false; t.enableWordWrapping = false; t.overflowMode = TMPro.TextOverflowModes.Overflow;
            if (hero) StageClearModal.ApplyHeroStyle(t);   // 11-27: the gold 'Spellbound!' recipe again - my cream + thick gold outline came out blobby ("it looked better before. change it back")
            else HUDManager.ApplyRecipeCount(t, new Color32(0x1E, 0x0A, 0x52, 0xFF), 0.19f, -0.9f);
            t.fontSize = fs;   // the recipes may resize - keep ours
            if (!hero && c != Color.white) { t.enableVertexGradient = false; t.color = c; }
            return t;
        }

        private void Show()
        {
            LoadArt();
            _rolled = new Reward[3];
            _rolled[0] = Roll(r => r.kind == Kind.Coins);                                              // coins are always on offer
            _rolled[1] = _forceTier > 0 ? Roll(r => r.tier == _forceTier) : Roll(r => r.title != _rolled[0].title); _forceTier = -1;
            _rolled[2] = Roll(r => r.title != _rolled[0].title && r.title != _rolled[1].title && !(r.kind == _rolled[1].kind && r.kind == _rolled[0].kind));
            WDLog.Log($"[Chest] rolled: {_rolled[0].title} | {_rolled[1].title} | {_rolled[2].title}");
            Build(); Open = true; _stage = 0; _skip = false; _home = Vector2.zero;
            _canvas.gameObject.SetActive(true);
            StartCoroutine(Arrive());
        }

        private void Build()
        {
            if (_canvas != null) Destroy(_canvas.gameObject);
            for (int i = 0; i < 3; i++) { _reel[i].Clear(); _landed[i] = null; }
            _numLayer = null;
            var canvasGO = new GameObject("ChestRewardCanvas"); canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>(); _canvas.renderMode = RenderMode.ScreenSpaceOverlay; _canvas.sortingOrder = 150;
            var scaler = canvasGO.AddComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(540f, 960f); scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();
            _root = canvasGO.GetComponent<RectTransform>();
            var dim = Node("Dim", _root, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f)); dim.anchorMin = Vector2.zero; dim.anchorMax = Vector2.one; dim.offsetMin = dim.offsetMax = Vector2.zero;
            var dImg = dim.gameObject.AddComponent<Image>(); dImg.color = new Color(0.03f, 0.02f, 0.09f, DIM); dImg.raycastTarget = true;   // swallows taps: a pick is required
            var skip = dim.gameObject.AddComponent<Button>(); skip.transition = Selectable.Transition.None; skip.onClick.AddListener(() => { if (_stage == 1) _skip = true; });
            // a dark band down from the top, behind the title, so the HUD (owl, Goals, Moves) cannot show through the lettering
            if (s_shadeTex == null) s_shadeTex = Tex(4, 64, (u, v) => new Color(0.02f, 0.01f, 0.06f, Mathf.SmoothStep(0f, 1f, v)));
            var shade = Pic("TopShade", _root, new Vector2(0.5f, 1f), new Vector2(0f, 330f), s_shadeTex, new Color(1f, 1f, 1f, TOP_SHADE), new Vector2(0.5f, 1f));
            shade.rectTransform.anchorMin = new Vector2(0f, 1f); shade.rectTransform.anchorMax = new Vector2(1f, 1f); shade.rectTransform.sizeDelta = new Vector2(0f, 330f);
            var origin = new Vector2(0.5f, MOUTH_Y);   // the open chest's mouth
            var glow = Pic("Glow", _root, new Vector2(0.5f, 0.27f), new Vector2(520f, 520f), s_burstTex, new Color(1f, 0.86f, 0.4f, 0f)); glow.name = "ChestGlow";   // 11-27 Spencer: back to the ORIGINAL chest glow (the rayed gold burst), not the soft halo
            _chest = Pic("Chest", _root, new Vector2(0.5f, 0.27f), new Vector2(CHEST, CHEST), Resources.Load<Texture2D>("Blockers/chest"), Color.white);
            Color[] beamCol = { new Color(0.78f, 0.36f, 1f, 0.75f), new Color(0.45f, 0.42f, 1f, 0.75f), new Color(0.78f, 0.36f, 1f, 0.75f) };
            for (int i = 0; i < 3; i++)
            {
                var ri = Pic("Beam" + i, _root, origin, new Vector2(BEAM_W, 0f), s_beamTex, beamCol[i], new Vector2(0.5f, 0f));
                _beam[i] = ri.rectTransform; _beam[i].localRotation = Quaternion.Euler(0f, 0f, (1 - i) * BEAM_ANGLE);
                var core = Pic("Core", _beam[i], new Vector2(0.5f, 0f), Vector2.zero, s_beamTex, new Color(1f, 0.97f, 0.9f, 0.5f)); core.rectTransform.anchorMin = new Vector2(0.32f, 0f); core.rectTransform.anchorMax = new Vector2(0.68f, 1f); core.rectTransform.offsetMin = core.rectTransform.offsetMax = Vector2.zero;
                for (int k = 0; k < 9; k++) { var ic = Pic("Reel", _beam[i], new Vector2(0.5f, 0f), new Vector2(92f, 92f), s_icons[0], Color.white); ic.gameObject.SetActive(false); _reel[i].Add(ic.rectTransform); }
            }
            // the FRONT of the open chest again, over the beams, so the light comes out of its mouth and not through its box
            _front = Pic("ChestFront", _root, new Vector2(0.5f, 0.27f), new Vector2(CHEST, CHEST * FRONT_CUT), Resources.Load<Texture2D>("Blockers/chest_open"), Color.white, new Vector2(0.5f, 0.5f / FRONT_CUT));
            _front.uvRect = new Rect(0f, 0f, 1f, FRONT_CUT); _front.gameObject.SetActive(false);
            // the lid-seam light: children of the closed chest so they squash and hop with it. Seam = a wide, very thin soft line at the
            // lid's edge (about 55% up the closed-chest art); rays = the upper half of the soft ray texture, fanning up from the crack.
            {
                Material add = null; var probe = ProgressComet.AddFx(_root, ProgressComet.GlowSprite, Color.clear, 4f); add = probe.material; Destroy(probe.gameObject);
                var glowTex = ProgressComet.GlowSprite != null ? (Texture)ProgressComet.GlowSprite.texture : s_burstTex;
                _seamRays = Pic("SeamRays", _chest.transform, new Vector2(0.5f, 0.56f), new Vector2(CHEST * 1.05f, CHEST * 0.55f), Resources.Load<Texture2D>("Particles/vfx_thin_rays") ?? s_burstTex, new Color(1f, 0.9f, 0.55f, 0f), new Vector2(0.5f, 0f));
                _seamRays.uvRect = new Rect(0f, 0.5f, 1f, 0.5f);
                _seam = Pic("Seam", _chest.transform, new Vector2(0.5f, 0.56f), new Vector2(CHEST * 0.80f, 16f), glowTex, new Color(1f, 0.96f, 0.72f, 0f));
                if (add != null) { _seam.material = add; _seamRays.material = add; }
            }
            _coins = Node("Coins", _root, new Vector2(0.5f, MOUTH_Y), Vector2.zero, new Vector2(0.5f, 0.5f));
            // 11-27 (Spencer: the title, then "same with the words rare or epic", were fading under the glow): EVERY reward's glow lives in
            // this one layer, drawn before any reward - so no glow can ever cover a neighbour's icon, number or Rare / Epic tag.
            _glowLayer = Node("RewardGlows", _root, new Vector2(0.5f, MOUTH_Y), Vector2.zero, new Vector2(0.5f, 0.5f));
            _title = Txt("Title", _root, new Vector2(0.5f, 0.90f), new Vector2(500f, 70f), "", 66, Color.white, true);   // 11-27 Spencer: "remove the Treasure title" - the title only appears for the pick
            _hint = Txt("Hint", _root, new Vector2(0.5f, 0.115f), new Vector2(500f, 44f), "Tap to open", 36, Color.white);
            var ob = Node("BtnClaimOpen", _root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0.5f, 0.5f)); ob.anchorMin = Vector2.zero; ob.anchorMax = Vector2.one; ob.offsetMin = ob.offsetMax = Vector2.zero;   // tap anywhere   // "Claim": the visible test bot presses it
            var obi = ob.gameObject.AddComponent<Image>(); obi.color = new Color(1f, 1f, 1f, 0f);
            _openBtn = ob.gameObject.AddComponent<Button>(); _openBtn.transition = Selectable.Transition.None; _openBtn.onClick.AddListener(() => { if (_stage == 0) StartCoroutine(Spin()); });
        }

        private System.Collections.IEnumerator Arrive()
        {
            var rt = _chest.rectTransform; float t = 0f; GameAudio.Instance?.PlayCoinLand();
            // 11-27 (Spencer: "instead of coming out and shaking could it come out, shake and land on the spot when you open it"): the
            // chest WAITS where it came out of the board - big, straining - and the tap sends it to its spot; the landing opens it.
            rt.localScale = Vector3.zero; yield return null; Canvas.ForceUpdateCanvases();   // the new canvas has its real size from here on
            float halfW = _root.rect.width * 0.5f, H = _root.rect.height; float s0 = 0.3f;
            _home = new Vector2(0f, H * 0.22f);                                                  // no board chest (test menu): above the spot
            if (s_hover != null && Camera.main != null)
            {
                Vector3 sp = Camera.main.WorldToScreenPoint(s_hover.transform.position), sp2 = Camera.main.WorldToScreenPoint(s_hover.transform.position + Vector3.right * s_hover.transform.localScale.x);
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, sp, null, out var local))
                {
                    _home = new Vector2(local.x, local.y + (0.5f - 0.27f) * H);               // the chest's anchor sits 27% up the screen
                    s0 = Mathf.Clamp(Mathf.Abs(sp2.x - sp.x) / Mathf.Max(1f, CHEST * _canvas.scaleFactor), 0.15f, 1f);
                }
                Destroy(s_hover); s_hover = null;
            }
            float m = CHEST * WAIT_SCALE * 0.5f + 8f;
            _home = new Vector2(Mathf.Clamp(_home.x, -halfW + m, halfW - m), Mathf.Clamp(_home.y, -0.27f * H + m + 70f, 0.73f * H - m - 40f));
            Vector2 start = rt.anchoredPosition = _home; 
            while (t < 0.32f) { t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / 0.32f), e = 1f - Mathf.Pow(1f - k, 3f); rt.anchoredPosition = _home; rt.localScale = Vector3.one * (Mathf.Lerp(s0, WAIT_SCALE, e) + Mathf.Sin(k * Mathf.PI) * 0.14f); yield return null; }
            // STRAINING TO OPEN (11-27 Spencer: "an animation of the chest tweening with squash animation like it is trying to get open and
            // burst"). A loop: it squashes down, springs up tall with a hop and a rattle, lands with a wobble - and each try is a little
            // harder, with light leaking out behind it. Squash is from the base (the chest's feet stay on the ground).
            var glow = _root.Find("ChestGlow").GetComponent<RawImage>(); float c0 = 0f; int tries = 0;
            while (_stage == 0 && Open)
            {
                c0 += Time.unscaledDeltaTime; const float CYCLE = 1.05f; float u = c0 / CYCLE;
                if (u >= 1f) { c0 = 0f; u = 0f; tries++; }
                float power = Mathf.Min(1f + tries * 0.12f, 1.6f) * STRETCH, sx = 1f, sy = 1f, hop = 0f, rot = 0f;
                if (u < 0.30f) { float k = Mathf.SmoothStep(0f, 1f, u / 0.30f); sx = 1f + 0.13f * k * power; sy = 1f - 0.15f * k * power; rot = Mathf.Sin(u * 150f) * 1.2f * k; }                     // squash down, trembling
                else if (u < 0.44f) { float k = (u - 0.30f) / 0.14f, e = 1f - (1f - k) * (1f - k); sx = Mathf.Lerp(1f + 0.13f * power, 1f - 0.10f * power, e); sy = Mathf.Lerp(1f - 0.15f * power, 1f + 0.17f * power, e); hop = 26f * power * e; rot = Mathf.Sin(u * 260f) * 4f; }   // spring up
                else if (u < 0.60f) { float k = (u - 0.44f) / 0.16f; sx = Mathf.Lerp(1f - 0.10f * power, 1.06f, k); sy = Mathf.Lerp(1f + 0.17f * power, 0.94f, k); hop = 26f * power * (1f - k * k); rot = Mathf.Sin(u * 260f) * 4f * (1f - k); }   // drop back
                else { float k = (u - 0.60f) / 0.40f, w2 = Mathf.Sin(k * Mathf.PI * 3f) * (1f - k); sx = 1f + 0.06f * w2; sy = 1f - 0.06f * w2; }                                                 // settle wobble
                rt.localScale = new Vector3(sx, sy, 1f) * WAIT_SCALE; rt.localRotation = Quaternion.Euler(0f, 0f, rot);
                rt.anchoredPosition = _home + new Vector2(0f, (hop + (sy - 1f) * CHEST * 0.5f) * WAIT_SCALE);
                // 11-27 (Spencer: "the glow just looks like fuzz on the chest when its struggling to open"): no cloud behind the chest while
                // it is shut. The light is INSIDE it: on each spring a thin bright line flares along the lid's seam, with a few rays
                // spraying up from the crack and a couple of sparks - gone again as it lands.
                float leak = u >= 0.30f && u < 0.60f ? Mathf.Sin((u - 0.30f) / 0.30f * Mathf.PI) : 0f;
                glow.color = new Color(1f, 0.86f, 0.4f, leak * 0.45f * power); if (!CHEST_GLOW) glow.color = Color.clear; glow.rectTransform.localScale = Vector3.one * (0.7f + 0.3f * leak);   // as it originally was
                if (false && _seam != null)
                {
                    _seam.color = new Color(1f, 0.96f, 0.72f, Mathf.Clamp01(leak * 1.1f * power)); _seam.rectTransform.localScale = new Vector3(0.75f + 0.35f * leak, 0.6f + 1.0f * leak * power, 1f);
                    _seamRays.color = new Color(1f, 0.9f, 0.55f, Mathf.Clamp01(leak * 0.6f * power)); _seamRays.rectTransform.localScale = new Vector3(1f, 0.5f + 0.9f * leak * power, 1f);
                }
                if (_hint != null) _hint.rectTransform.localScale = Vector3.one * (1f + Mathf.Sin(Time.unscaledTime * 5f) * 0.05f);
                yield return null;
            }
        }

        private Vector2 BeamTip(int i)
        {
            float a = (1 - i) * BEAM_ANGLE * Mathf.Deg2Rad;   // the beam's local +Y, rotated about Z
            return new Vector2(-Mathf.Sin(a), Mathf.Cos(a)) * LAND_AT;
        }

        private System.Collections.IEnumerator Spin()
        {
            _stage = 1; _openBtn.gameObject.SetActive(false); _hint.gameObject.SetActive(false);
            {   // the leap to its spot and the landing
                var lrt = _chest.rectTransform; Vector2 from = lrt.anchoredPosition; float lt = 0f; const float LD = 0.36f; float arc = Mathf.Max(90f, from.magnitude * 0.22f);
                while (lt < LD)
                {
                    lt += Time.unscaledDeltaTime; float k = Mathf.Clamp01(lt / LD), e = k * k;   // accelerates into the ground
                    lrt.anchoredPosition = Vector2.Lerp(from, Vector2.zero, e) + new Vector2(0f, Mathf.Sin(k * Mathf.PI) * arc);
                    lrt.localScale = Vector3.one * Mathf.Lerp(WAIT_SCALE, 1f, e); lrt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin(k * Mathf.PI * 2f) * 10f * (1f - k));
                    yield return null;
                }
                lrt.anchoredPosition = Vector2.zero; lrt.localRotation = Quaternion.identity; lrt.localScale = new Vector3(1.28f, 0.70f, 1f);   // the impact squash
                lrt.anchoredPosition = new Vector2(0f, -CHEST * 0.15f);
                GridManager.Instance?.ShakeBoard(0.3f, 0.3f); yield return null; yield return null;
                lrt.anchoredPosition = Vector2.zero; _skip = false;
            }
            var open = Resources.Load<Texture2D>("Blockers/chest_open"); if (open != null) { _chest.texture = open; _front.gameObject.SetActive(true); }
            if (_seam != null) { _seam.gameObject.SetActive(false); _seamRays.gameObject.SetActive(false); }
            var coinList = new List<RectTransform>(); var coinVel = new List<Vector2>(); var coinFrame = new List<float>(); float coinAcc = 0f;
            var crt = _chest.rectTransform; crt.localRotation = Quaternion.identity; crt.anchoredPosition = Vector2.zero;
            var glow = _root.Find("ChestGlow").GetComponent<RawImage>(); glow.rectTransform.localScale = Vector3.one;
            GameAudio.Instance?.PlayCrateBreak(); HapticsManager.Strong();
            // THE OPENING HIT (11-27 Spencer: "there needs to be an immediate response, maybe the screen whites out or something to show
            // something amazing has happened"): on the tap the whole screen goes white and clears in a third of a second, a ring of light
            // blasts out from the chest, and the board behind gets a knock.
            var flash = Node("Flash", _root, Vector2.zero, Vector2.zero, new Vector2(0.5f, 0.5f)); flash.anchorMin = Vector2.zero; flash.anchorMax = Vector2.one; flash.offsetMin = flash.offsetMax = Vector2.zero;
            var flashImg = flash.gameObject.AddComponent<Image>(); flashImg.color = Color.white; flashImg.raycastTarget = false;
            var ring = Pic("Shock", _root, new Vector2(0.5f, MOUTH_Y), new Vector2(300f, 300f), s_burstTex, new Color(1f, 0.95f, 0.75f, 1f));
            ring.transform.SetSiblingIndex(_chest.transform.GetSiblingIndex());   // behind the chest
            GridManager.Instance?.ShakeBoard(0.3f, 0.35f); WordDropFX.Instance?.PlayScreenFlash(2);
            float t = 0f; var done = new bool[3]; var phase = new float[3]; float speed = 1500f;
            for (int i = 0; i < 3; i++) for (int k = 0; k < _reel[i].Count; k++) { _reel[i][k].gameObject.SetActive(true); SetReelIcon(_reel[i][k]); }
            while (true)
            {
                float dt = Time.unscaledDeltaTime; t += dt;
                if (flashImg != null) { float fa = 1f - Mathf.Clamp01((t - 0.06f) / 0.34f); flashImg.color = new Color(1f, 1f, 1f, fa * fa); if (fa <= 0f) { Destroy(flash.gameObject); flashImg = null; } }
                if (ring != null) { float rk = Mathf.Clamp01(t / 0.55f); ring.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.6f, 6.5f, 1f - Mathf.Pow(1f - rk, 2f)); ring.color = new Color(1f, 0.95f, 0.75f, 1f - rk); ring.rectTransform.localRotation = Quaternion.Euler(0f, 0f, t * 90f); if (rk >= 1f) { Destroy(ring.gameObject); ring = null; } }
                float grow = Mathf.Clamp01((t - 0.05f) / 0.22f); float pop = 1f + Mathf.Sin(Mathf.Clamp01(t / 0.34f) * Mathf.PI) * 0.38f; crt.localScale = Vector3.one * pop; _front.rectTransform.localScale = crt.localScale;
                // COINS (11-27 Spencer: "the coin animation is just flat, you could add the rotation to the coins that we already have ... and they
                // should all drop off and clear by the time you choose"): the game's own spinning-coin sheet (Coins/coin3d_spin, 6 x 4 frames),
                // each coin on its own frame; the fountain stops before the last beam lands and what is in the air falls away.
                bool emit = !_skip && t < SPIN_FIRST + 2f * SPIN_GAP - 0.75f;
                coinAcc += dt; while (emit && coinAcc > 0.03f && coinList.Count < 70) { coinAcc -= 0.03f; SpawnCoin(coinList, coinVel, coinFrame); }
                StepCoins(coinList, coinVel, coinFrame, dt, emit);
                bool all = true;
                for (int i = 0; i < 3; i++)
                {
                    _beam[i].sizeDelta = new Vector2(BEAM_W * (done[i] ? 0.8f : 1f + Mathf.Sin(t * 14f + i) * 0.08f), BEAM_LEN * grow);
                    if (done[i]) continue;
                    all = false; phase[i] += speed * dt;
                    for (int k = 0; k < _reel[i].Count; k++)
                    {
                        float span = BEAM_LEN, y = (phase[i] + k * span / _reel[i].Count) % span; var rr = _reel[i][k];
                        if (y < speed * dt * 1.5f) SetReelIcon(rr);
                        rr.anchoredPosition = new Vector2(0f, y); rr.localRotation = Quaternion.Inverse(_beam[i].localRotation);
                        float s = Mathf.Lerp(0.45f, 1.15f, Mathf.Clamp01(y / 700f)); rr.localScale = new Vector3(s, s, 1f); rr.gameObject.SetActive(y < BEAM_LEN * grow);
                    }
                    if (_skip || t >= SPIN_FIRST + i * SPIN_GAP) { done[i] = true; Land(i); }
                }
                if (all) break;
                yield return null;
            }
            // let the last coins fall off the screen before the choice (they drop faster now)
            // 11-27 (Spencer: "shut off the lazer looking rays from the chest once the 3 icons are displayed" - "yes please"): as the last
            // coins fall away the beams narrow and fade out (0.35 s), so the pick is a calm beat - three lit rewards on a dark screen.
            float drain = 0f; const float BEAM_OUT = 0.35f;
            var beamImg = new RawImage[3][]; var beamA = new float[3][]; var beamW = new float[3];
            for (int i = 0; i < 3; i++) { beamImg[i] = _beam[i].GetComponentsInChildren<RawImage>(true); beamA[i] = new float[beamImg[i].Length]; for (int k = 0; k < beamImg[i].Length; k++) beamA[i][k] = beamImg[i][k].color.a; beamW[i] = _beam[i].sizeDelta.x; }
            while ((coinList.Count > 0 && drain < 1.2f) || drain < BEAM_OUT)
            {
                float dt2 = Time.unscaledDeltaTime; drain += dt2; StepCoins(coinList, coinVel, coinFrame, dt2 * 1.6f, false);
                float k2 = Mathf.Clamp01(drain / BEAM_OUT), e2 = 1f - k2 * k2;
                for (int i = 0; i < 3; i++)
                {
                    _beam[i].sizeDelta = new Vector2(beamW[i] * Mathf.Lerp(0.25f, 1f, e2), _beam[i].sizeDelta.y);
                    for (int k = 0; k < beamImg[i].Length; k++) { var c = beamImg[i][k].color; c.a = beamA[i][k] * e2; beamImg[i][k].color = c; }
                }
                yield return null;
            }
            for (int i = 0; i < 3; i++) _beam[i].gameObject.SetActive(false);
            foreach (var c in coinList) if (c != null) Destroy(c.gameObject);
            _stage = 2; _title.text = "Pick One!";
            StartCoroutine(ArchTitle());
            _title.transform.SetAsLastSibling();   // 11-27 Spencer: on top of the beams and the rewards' glow (it was drawn under them and washed out)
        }

        // 11-27 (Spencer: "could we give the pick one title a little bit of an arch?"): each letter is lifted along a shallow curve and
        // tilted to follow it, highest in the middle. Re-applied every frame while the title is up (TextMeshPro rebuilds its mesh).
        public static float ARCH_HEIGHT = 20f, ARCH_TILT = 9f;
        private System.Collections.IEnumerator ArchTitle()
        {
            while (Open && _title != null)
            {
                _title.ForceMeshUpdate(); var info = _title.textInfo; int n = info.characterCount;
                if (n > 1)
                {
                    float x0 = info.characterInfo[0].bottomLeft.x, x1 = info.characterInfo[n - 1].topRight.x, half = Mathf.Max(1f, (x1 - x0) * 0.5f), mid = (x0 + x1) * 0.5f;
                    for (int i = 0; i < n; i++)
                    {
                        var ch = info.characterInfo[i]; if (!ch.isVisible) continue;
                        var verts = info.meshInfo[ch.materialReferenceIndex].vertices; int vi = ch.vertexIndex;
                        Vector3 c = (verts[vi] + verts[vi + 2]) * 0.5f; float u = Mathf.Clamp((c.x - mid) / half, -1f, 1f);
                        var m = Matrix4x4.TRS(new Vector3(0f, (1f - u * u) * ARCH_HEIGHT - ARCH_HEIGHT * 0.5f, 0f), Quaternion.Euler(0f, 0f, -u * ARCH_TILT), Vector3.one);
                        for (int k = 0; k < 4; k++) verts[vi + k] = m.MultiplyPoint3x4(verts[vi + k] - c) + c;
                    }
                    for (int mi = 0; mi < info.meshInfo.Length; mi++) { info.meshInfo[mi].mesh.vertices = info.meshInfo[mi].vertices; _title.UpdateGeometry(info.meshInfo[mi].mesh, mi); }
                }
                yield return null;
            }
        }

        private static Texture2D s_coinSheet; private const int COIN_COLS = 6, COIN_ROWS = 4;
        private void SpawnCoin(List<RectTransform> list, List<Vector2> vel, List<float> frame)
        {
            if (s_coinSheet == null) s_coinSheet = Resources.Load<Texture2D>("Coins/coin3d_spin");
            float size = Random.Range(40f, 58f);
            var c = Pic("Coin", _coins, new Vector2(0.5f, 0.5f), new Vector2(size, size), s_coinSheet != null ? s_coinSheet : s_icons[0], Color.white);
            list.Add(c.rectTransform); vel.Add(new Vector2(Random.Range(-360f, 360f), Random.Range(560f, 1050f))); frame.Add(Random.Range(0f, COIN_COLS * COIN_ROWS));
        }
        private void StepCoins(List<RectTransform> list, List<Vector2> vel, List<float> frame, float dt, bool recycle)
        {
            for (int c = list.Count - 1; c >= 0; c--)
            {
                var rt = list[c]; if (rt == null) { list.RemoveAt(c); vel.RemoveAt(c); frame.RemoveAt(c); continue; }
                var v = vel[c]; v.y -= 1900f * dt; vel[c] = v; rt.anchoredPosition += v * dt;
                frame[c] = (frame[c] + 26f * dt) % (COIN_COLS * COIN_ROWS);   // the spin
                if (s_coinSheet != null) { int f = (int)frame[c], col = f % COIN_COLS, row = f / COIN_COLS; rt.GetComponent<RawImage>().uvRect = new Rect(col / (float)COIN_COLS, 1f - (row + 1) / (float)COIN_ROWS, 1f / COIN_COLS, 1f / COIN_ROWS); }
                if (rt.anchoredPosition.y < -430f)
                {
                    if (recycle) { rt.anchoredPosition = Vector2.zero; vel[c] = new Vector2(Random.Range(-360f, 360f), Random.Range(560f, 1050f)); }
                    else { Destroy(rt.gameObject); list.RemoveAt(c); vel.RemoveAt(c); frame.RemoveAt(c); }
                }
            }
        }

        private static Texture2D s_capTex;
        /// <summary>A solid pill (two half-discs and a bar) - the tier ribbon's plate.</summary>
        private void Pill(RectTransform parent, Vector2 anchor, Vector2 size, Color c)
        {
            float r = size.y;
            Pic("PillL", parent, anchor, new Vector2(r, r), s_discTex, c).rectTransform.anchoredPosition = new Vector2(-(size.x - r) * 0.5f, 0f);
            Pic("PillR", parent, anchor, new Vector2(r, r), s_discTex, c).rectTransform.anchoredPosition = new Vector2((size.x - r) * 0.5f, 0f);
            if (s_capTex == null) s_capTex = Tex(4, 4, (u, v) => Color.white);
            Pic("PillM", parent, anchor, new Vector2(size.x - r, r * 0.93f), s_capTex, c);
        }
        private static string TitleCase(string s) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());
        private void SetReelIcon(RectTransform rr)
        {
            var ri = rr.GetComponent<RawImage>(); int pick = Random.Range(0, 4);
            pick = Random.Range(0, 8); ri.texture = s_icons[pick] ?? s_icons[0]; ri.color = Color.white;   // coin, rocket, moves, bomb or a booster
        }

        private void Land(int i)
        {
            foreach (var rr in _reel[i]) rr.gameObject.SetActive(false);
            var r = _rolled[i]; Color tc = TIER_BG[r.tier]; string[] names = { "BtnClaimA", "BtnClaimB", "BtnClaimC" };
            var node = Node(names[i], _root, new Vector2(0.5f, MOUTH_Y), new Vector2(176f, 250f), new Vector2(0.5f, 0.5f)); node.anchoredPosition = BeamTip(i) + new Vector2(0f, i == 1 ? 70f : 0f);
            var hit = node.gameObject.AddComponent<Image>(); hit.color = new Color(1f, 1f, 1f, 0f);
            int idx = i; var btn = node.gameObject.AddComponent<Button>(); btn.transition = Selectable.Transition.None; btn.onClick.AddListener(() => { if (_stage == 2) Pick(idx); });
            // 11-27 (Spencer: "instead of the white circles could you use the gold rimmed circles that the boosters use coloured
            // differently and ... the number hangs like a badge below the icon"): the booster bar's gold-rimmed button as the plate
            // (UI/Chest/slot_common blue / slot_rare purple / slot_epic orange), the reward's icon on it, and its AMOUNT on a red badge
            // hanging off the bottom of the rim - the same layout as the booster counts. No text label.
            // 11-27 (Spencer, with a Royal Match card-pack screenshot: "make the glow around the objects more like the glow we are using for
            // the boosters so it matches the look of our game"): the booster reveal card's glow - ProgressComet's soft ADDITIVE warm-gold
            // halo (MechanicSplash uses it behind a new power-up), with the game's soft ray texture (Particles/vfx_rays) turning slowly
            // inside it. Gold for every tier; rare / epic are bigger and brighter (the plate carries the tier colour).
            float burst = r.tier == 0 ? 330f : r.tier == 1 ? 400f : 470f;
            var halo = ProgressComet.AddFx(_glowLayer, ProgressComet.GlowSprite, r.tier == 0 ? new Color(1f, 0.85f, 0.45f, 0.9f) : r.tier == 1 ? new Color(0.35f, 0.95f, 1f, 1f) : new Color(1f, 0.72f, 0.3f, 1f), burst);   // rare = CYAN (11-27 Spencer: "maybe try cyan?") - violet vanished into the violet beams
            halo.raycastTarget = false; halo.rectTransform.anchorMin = halo.rectTransform.anchorMax = new Vector2(0.5f, 0.5f); halo.rectTransform.anchoredPosition = node.anchoredPosition + new Vector2(0f, 15f); halo.rectTransform.sizeDelta = new Vector2(burst, burst);
            var b = Pic("Rays", _glowLayer, new Vector2(0.5f, 0.5f), new Vector2(burst * 0.92f, burst * 0.92f), Resources.Load<Texture2D>("Particles/vfx_rays") ?? s_burstTex, r.tier == 1 ? new Color(0.6f, 0.98f, 1f, 0.85f) : new Color(1f, 0.88f, 0.5f, r.tier == 0 ? 0.7f : 0.9f));
            b.rectTransform.anchoredPosition = node.anchoredPosition + new Vector2(0f, 15f);
            if (halo.material != null) b.material = halo.material;   // the same additive material
            // 11-27 (Spencer, with a Royal Match reward screenshot: "by the badges with the number on the icon i mean this kind of idea";
            // "we probably dont even need the circles at all"): NO plate, no badge. The reward's own icon, large, on the glow, with its
            // amount in the game's chunky cream number overlapping the icon's lower-right corner. Rare / epic show in the glow (bigger,
            // brighter, tinted) and a small tag underneath.
            var icon = r.kind == Kind.Moves ? Resources.Load<Texture2D>("UI/Chest/moves") : IconFor(r);   // 11-27 Spencer's swap-tiles art = the Moves icon
            // 11-27 (Spencer's screenshot: "the bomb is much bigger than the other icons"; the hammer's "1" floated away from it): every
            // icon is sized by its ART, not its canvas - the art's larger side is always ICON_ART px - and the number sits on the art's
            // own lower-right corner. Fractions measured from the files (content width, height as a share of the canvas).
            float cw = 0.92f, ch = 0.93f, fudge = 1f;
            if (icon != null)
            {
                string n = icon.name;
                if (n == "moves") { cw = 0.94f; ch = 0.94f; }
                else if (n == "special_rocket") { cw = 0.59f; ch = 0.86f; } else if (n == "special_bomb") { cw = 0.92f; ch = 0.94f; fudge = 0.88f; }   // the bomb drew ~14% large on his screen
                else if (n == "1hammer") { cw = 0.75f; ch = 0.98f; } else if (n == "2glove") { cw = 0.88f; ch = 0.98f; } else if (n == "1axe") { cw = 0.86f; ch = 0.91f; }
                else if (n == "2spyglass") { cw = 0.92f; ch = 0.83f; } else if (n == "1goldlightning") { cw = 0.72f; ch = 0.98f; }
            }
            const float ICON_ART = 136f; float isz = ICON_ART / Mathf.Max(cw, ch) * fudge;
            if (icon != null) Pic("Icon", node, new Vector2(0.5f, 0.56f), new Vector2(isz, isz), icon, Color.white);
            else Txt("Moves", node, new Vector2(0.5f, 0.60f), new Vector2(170f, 50f), "Moves", 38, Color.white);
            // 11-27 (Spencer's screenshot: the "100" was hidden behind the next reward's icon - "these numbers should be higher in sorting
            // order than the icons"): every number lives in one layer drawn after ALL the rewards (and the title after that).
            if (_numLayer == null) _numLayer = Node("RewardNumbers", _root, new Vector2(0.5f, MOUTH_Y), Vector2.zero, new Vector2(0.5f, 0.5f));
            _numLayer.SetAsLastSibling();
            var cnt = Txt("Count", _numLayer, new Vector2(0.5f, 0.5f), new Vector2(140f, 60f), (r.kind == Kind.Moves ? "+" : "") + r.amount, r.amount >= 100 ? 44 : 52, Color.white);
            cnt.rectTransform.anchoredPosition = node.anchoredPosition + new Vector2(0f, 15f) + (icon != null ? new Vector2(isz * cw * 0.5f - 14f, -isz * ch * 0.5f + 20f) : new Vector2(0f, -44f));
            if (TIER_TAGS && r.tier > 0)
            {   // 11-27 (Spencer, from his screenshot: "you cant really see the rare and epic tags cause of the colours. they dont stand out"):
                // a solid ribbon - a pill with a dark rim and a strong fill (rare = deep violet, epic = hot orange) and white lettering,
                // tucked close under the icon - so it reads on any glow or beam.
                // 11-27 Spencer: "use the same art pill box we use for the tutorial blurbs ... just with the center coloured different" -
                // UI/Chest/tag_rare / tag_epic = UI/Panel/tut_bubble with its cream face recoloured (violet / orange), gold rim kept.
                Pic("TagPlate", node, new Vector2(0.5f, 0.02f), new Vector2(150f, 75f), Resources.Load<Texture2D>(r.tier == 1 ? "UI/Chest/tag_rare" : "UI/Chest/tag_epic"), Color.white);
                var tg = Txt("Tier", node, new Vector2(0.5f, 0.026f), new Vector2(120f, 30f), r.tier == 1 ? "RARE" : "EPIC", 20, Color.white);   // 11-27: below the icon with a clear gap (it was cutting off the booster art) tg.characterSpacing = 4f;
            }
            _landed[i] = node.gameObject; StartCoroutine(LandPop(node, b.rectTransform, r.tier));
            if (_title != null) _title.transform.SetAsLastSibling();   // the title stays above every landed reward
            GameAudio.Instance?.PlayCoinLand(); if (r.tier > 0) { HapticsManager.Medium(); if (r.tier == 2) WordDropFX.Instance?.PlayScreenFlash(2); }
        }

        private System.Collections.IEnumerator LandPop(RectTransform node, RectTransform burst, int tier)
        {
            float t = 0f; float d = 0.32f;
            while (t < d && node != null) { t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / d); node.localScale = Vector3.one * (1f + Mathf.Sin(k * Mathf.PI) * (0.16f + 0.07f * tier)); yield return null; }
            while (node != null && Open) { float w = Time.unscaledTime; if (burst != null) { burst.localRotation = Quaternion.Euler(0f, 0f, w * (14f + tier * 8f)); burst.localScale = Vector3.one * (1f + Mathf.Sin(w * 3f) * 0.05f); } node.localScale = Vector3.one * (1f + (_stage == 2 ? Mathf.Sin(w * 4f) * 0.04f : 0f)); yield return null; }
        }

        private void Pick(int i)
        {
            if (!Open) return;
            var r = _rolled[i]; Open = false;
            WDLog.Log($"[Chest] picked {r.title}");
            StopAllCoroutines(); _canvas.gameObject.SetActive(false);
            switch (r.kind)
            {
                case Kind.Coins:
                    CoinWallet.Add(r.amount); SurvivalManager.PendingBonusCoins += r.amount; LevelMapPanel.Instance?.AddPendingCoinReward(r.amount);
                    GameAudio.Instance?.PlayCoinLand(); break;
                case Kind.Moves:
                    SurvivalManager.Instance?.GrantBonusMoves(r.amount, "chest"); break;
                case Kind.Booster:
                    BoosterManager.Instance?.AddCharges(r.id, r.amount); break;
                case Kind.PowerUps:
                    var rules = RulesEngine.Instance;
                    if (rules != null)
                    {
                        var keep = new HashSet<Vector2Int>(); int[] kinds = { RulesEngine.SPECIAL_BOMB, RulesEngine.SPECIAL_ROCKET_H, RulesEngine.SPECIAL_ROCKET_V };
                        for (int k = 0; k < r.amount; k++) rules.PlaceStartSpecial(kinds[k % kinds.Length], 1, keep);
                        GridManager.Instance?.ReconcileSpecialVisuals(rules);
                    }
                    break;
            }
        }

    }
}
