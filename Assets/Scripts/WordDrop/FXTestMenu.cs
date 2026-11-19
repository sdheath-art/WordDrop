#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Editor / dev-build debug overlay for iterating on detonation FX without
    /// playing a full Survival session. Auto-spawns at scene load. Adds a
    /// top-right OnGUI panel with:
    ///   - Direct-fire buttons for each FX layer (one-shot, no real tiles needed)
    ///   - "Forced Meltdown" button that flips MeltdownManager.IsActive on,
    ///     fires a faked explosion stack at screen center, flips back off
    ///   - Toggle checkboxes for every WordDropFX.FX_* flag (no recompile)
    ///
    /// Stripped from release builds via #if UNITY_EDITOR || DEVELOPMENT_BUILD.
    /// </summary>
    public class FXTestMenu : MonoBehaviour
    {
        public static FXTestMenu Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("FXTestMenu");
            go.AddComponent<FXTestMenu>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ── UI state ──────────────────────────────────────────────────────────────
        private bool _open = false;   // 11-19 Spencer: "closed until i open it" (was open at launch, dropping over the home screen)
        private Vector2 _scroll;
        private GUIStyle _btnStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _tfStyle;
        private string   _jumpLevelInput = "";

        private const int PANEL_W   = 460;
        private const int BTN_H     = 44;
        private const int GAP       = 6;
        private const int TOGGLE_H  = 32;
        private const int HEADER_H  = 34;
        private const int CONTENT_H = 2300;   // +combo buttons 2026-10-22

        private void Update()
        {
            // Hotkey: B fires a tier-3 (12-tile) detonation at screen centre, so
            // the effect can be triggered with the FX menu hidden/out of the way.
            if (Input.GetKeyDown(KeyCode.B))
                FireFakeDetonation(chainStep: 0, fakeTileCount: 12, forceMeltdown: false);
        }

        // ── FX Bloom isolation tests — fire ONE glow on a live board tile so the
        //    matching FX-Bloom-Tuning slider can be dialed in isolation. ─────────
        private Tile FirstLiveTile()
        {
            if (GridManager.Instance == null) return null;
            for (int r = 0; r < RulesEngine.ROWS; r++)
                for (int c = 0; c < RulesEngine.COLS; c++)
                {
                    Tile t = GridManager.Instance.GetTile(c, r);
                    if (t != null && t.gameObject.activeSelf) return t;
                }
            return null;
        }

        private List<Tile> FirstLiveTiles(int n)
        {
            var list = new List<Tile>();
            if (GridManager.Instance == null) return list;
            for (int r = 0; r < RulesEngine.ROWS && list.Count < n; r++)
                for (int c = 0; c < RulesEngine.COLS && list.Count < n; c++)
                {
                    Tile t = GridManager.Instance.GetTile(c, r);
                    if (t != null && t.gameObject.activeSelf) list.Add(t);
                }
            return list;
        }

        private System.Collections.IEnumerator TestGlint()
        {
            Tile t = FirstLiveTile(); if (t == null) yield break;
            var sr = t.GetComponent<SpriteRenderer>(); if (sr == null) yield break;
            Color orig = sr.color;
            float cap = WordDropFX.GlintCap;
            sr.color = new Color(Mathf.Min(orig.r * 1.2f, cap), Mathf.Min(orig.g * 1.2f, cap), Mathf.Min(orig.b * 1.2f, cap), 1f);
            yield return new WaitForSecondsRealtime(0.7f);
            if (t != null && sr != null) sr.color = orig;
        }

        /// <summary>Flash a real word on the board in a specific state. Gold is forced by
        /// arming the tiles with the gold face first, which is exactly what a gold word does on
        /// its own. 2026-09-10 Spencer.</summary>
        /// <summary>Fire the flash on a REAL primed cluster from the board.
        ///
        /// The synthetic variants below are NOT faithful: they pre-arm the tiles before calling,
        /// so the flash starts from the armed face instead of a tan one, and PlayWordScored's
        /// willArm check answers FALSE for four unrelated tiles — which changes both the settle
        /// colour and whether the armed face is applied at all. That is why they never matched
        /// the game. This one uses an actual primed word, so every one of those answers is the
        /// real one. 2026-09-10 Spencer.</summary>
        private void TestFlashRealCluster(int chainStep)
        {
            var rules = RulesEngine.Instance; var g = GridManager.Instance;
            if (rules == null || g == null || WordDropFX.Instance == null) return;
            var clusters = rules.GetPrimedClusters();
            if (clusters.Count == 0)
            {
                Debug.LogWarning("[FXTest] No primed cluster on the board — make a word first, then press this.");
                return;
            }
            var tiles = new System.Collections.Generic.List<Tile>();
            foreach (var c in clusters[0].cells)
            {
                var t = g.GetTile(c.x, c.y);
                if (t != null) tiles.Add(t);
            }
            if (tiles.Count > 0)
                WordDropFX.Instance.PlayWordScored(tiles, Tile.PRIMED_GLOW, chainStep);
        }

        private void TestFlashVariant(bool gold, int chainStep)
        {
            // Fire on REAL gold-letter tiles (V K J X Q Z) when testing gold, so what lights up
            // is genuinely a gold tile rather than an ordinary one wearing the gold face.
            // 2026-09-10 Spencer. Falls back to any tiles if the board has none.
            var tiles = gold ? FirstGoldTiles(4) : FirstLiveTiles(4);
            if (gold && tiles.Count == 0)
            {
                Debug.LogWarning("[FXTest] No gold-letter tiles (V K J X Q Z) on the board — using ordinary tiles.");
                tiles = FirstLiveTiles(4);
            }
            if (tiles.Count == 0 || WordDropFX.Instance == null) return;
            foreach (var t in tiles)
            {
                if (t == null) continue;
                t.ApplyArmedFaceNow(gold);
                t.SetPrimedGlow(gold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW, gold);
            }
            // forceGold so the button tests the GOLD path even when the board has no gold
            // letters to hand — otherwise Flash 2 was silently identical to Flash 1.
            WordDropFX.Instance.PlayWordScored(tiles, gold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW, chainStep, forceGold: gold);
        }

        /// <summary>A word that SCORES but never arms: flashes, then settles to a plain white
        /// tile instead of the primed colour. My best guess at the "doesn't look great" one.</summary>
        private void TestFlashNoArm()
        {
            var tiles = FirstLiveTiles(4);
            if (tiles.Count == 0 || WordDropFX.Instance == null) return;
            foreach (var t in tiles) if (t != null) t.ClearPrimedGlow();
            WordDropFX.Instance.PlayWordScored(tiles, Tile.PRIMED_GLOW, 0);
        }

        /// <summary>The OTHER presentation entirely — the flat green "scored sprite" the
        /// detonation path pre-tints word tiles with, no flash and no chime. Drop-game era.</summary>
        private void TestFlashPreGreen()
        {
            foreach (var t in FirstLiveTiles(4))
                if (t != null && !t.IsShowingScoredSprite) t.SetScoredSprite(true);
        }

        /// <summary>Every tile on the board whose LETTER is high-value, i.e. an actual gold
        /// tile. 2026-09-10 Spencer.</summary>
        private System.Collections.Generic.List<Tile> FirstGoldTiles(int n)
        {
            var outList = new System.Collections.Generic.List<Tile>();
            var g = GridManager.Instance;
            if (g == null) return outList;
            for (int r = 0; r < RulesEngine.ROWS && outList.Count < n; r++)
                for (int c = 0; c < RulesEngine.COLS && outList.Count < n; c++)
                {
                    var t = g.GetTile(c, r);
                    if (t != null && t.IsGoldLetter) outList.Add(t);
                }
            return outList;
        }

        private void TestScoredFlash()
        {
            var tiles = FirstLiveTiles(4);
            if (tiles.Count > 0 && WordDropFX.Instance != null)
                WordDropFX.Instance.PlayWordScored(tiles, new Color(0.5f, 1f, 0.6f, 1f), 0);
        }

        private System.Collections.IEnumerator TestPrimedGlow()
        {
            Tile t = FirstLiveTile(); if (t == null) yield break;
            var sr = t.GetComponent<SpriteRenderer>(); if (sr == null) yield break;
            Color orig = sr.color;
            float elapsed = 0f;
            while (elapsed < 2.5f)
            {
                elapsed += Time.unscaledDeltaTime;
                float pulse = Mathf.Abs(Mathf.Sin(elapsed * 3.5f));
                float tint = 0.35f + pulse * 0.3f;
                Color pc = Color.Lerp(Color.white, Tile.PRIMED_GLOW, tint);
                float pmax = Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b));
                float cap = WordDropFX.PrimedGlowCap;
                if (pmax > cap) { float k = cap / pmax; pc.r *= k; pc.g *= k; pc.b *= k; }
                sr.color = pc;
                yield return null;
            }
            if (t != null && sr != null) sr.color = orig;
        }

        private void TestBubbleGlow()
        {
            Tile t = FirstLiveTile();
            if (t != null && FlipbookExplosion.Instance != null)
            {
                var sr = t.GetComponent<SpriteRenderer>();
                Color tint = sr != null ? sr.color : Color.white;
                FlipbookExplosion.Instance.PlayBubble(t.transform.position, tint, 1f, 0.35f);
            }
        }

        private void TestPopAura()
        {
            Tile t = FirstLiveTile();
            if (t != null && FlipbookExplosion.Instance != null)
            {
                var sr = t.GetComponent<SpriteRenderer>();
                Color tc = sr != null ? sr.color : Color.white;
                float hdr = WordDropFX.PopAuraHDR;
                Color squareTint = new Color(tc.r * hdr, tc.g * hdr, tc.b * hdr, 1f);
                float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                FlipbookExplosion.Instance.PlayPopOverlaySquare(t.transform.position, cell, 0.5f, squareTint);
            }
        }

        private void TestSparkle()
        {
            Tile t = FirstLiveTile();
            if (t != null && SparkleSpray.Instance != null)
                SparkleSpray.Instance.Play(t.transform.position, intensity: 0.5f);
        }

        /// <summary>Whole-menu scale (2026-10-23 Spencer: "increase its font and size so i can see it a little better"). The menu
        /// lays every button out in fixed pixels, so it is scaled as ONE piece via GUI.matrix — fonts, buttons and gaps grow
        /// together, and IMGUI maps clicks through the matrix. 1 = the old size.</summary>
        /// <summary>2026-10-23 Spencer: "is there anything on the FX menu that we dont really need anymore". ~45 buttons from the
        /// drop / armed-word game and one-off tuning passes (scored-flash variants, unlock/wild flows, top-out, rise-per-move,
        /// FX bloom tests, forced tier/meltdown detonations, layer toggles) are hidden. true = show them again; code untouched.</summary>
        public static bool SHOW_LEGACY_TOOLS = false;
        private int _contentH;
        public static float MENU_SCALE = 1.5f;
        private void OnGUI()
        {
            if (ClaudeScreenshot.Capturing) return;   // stay out of Claude's screenshots (2026-10-23)
            if (LoadingScreen.Showing) return;   // 11-19: debug menus draw over every canvas - stay off the loading screen
            var prevMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(MENU_SCALE, MENU_SCALE, 1f));
            try { DrawMenu(); }
            finally { GUI.matrix = prevMatrix; }
        }

        private int ScaledScreenW => Mathf.RoundToInt(Screen.width / Mathf.Max(0.1f, MENU_SCALE));
        private int ScaledScreenH => Mathf.RoundToInt(Screen.height / Mathf.Max(0.1f, MENU_SCALE));

        private void DrawMenu()
        {
            EnsureStyles();

            int x = ScaledScreenW - PANEL_W - 12;
            int y = 12;

            // Collapse / expand header
            if (GUI.Button(new Rect(x, y, PANEL_W, BTN_H),
                           _open ? "▼ FX Test Menu (click to hide)" : "▶ FX Test Menu", _btnStyle))
            {
                _open = !_open;
            }
            y += BTN_H + GAP;
            if (!_open) return;

            int panelH = Mathf.Min(ScaledScreenH - y - 20, 1100);
            GUI.Box(new Rect(x - 4, y - 4, PANEL_W + 8, panelH + 8), GUIContent.none, _panelStyle);

            _scroll = GUI.BeginScrollView(new Rect(x, y, PANEL_W, panelH),
                                          _scroll,
                                          new Rect(0, 0, PANEL_W - 20, _contentH > 0 ? _contentH : CONTENT_H));   // fits what is shown (legacy tools hidden 2026-10-23)

            int innerY = 0;

            // ── Direct-fire buttons ──────────────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Direct Fire ──", _headerStyle);
            innerY += HEADER_H + 4;

            // Skip-to-level: type a number, hit Go (or Enter).
            GUI.Label(new Rect(0, innerY + 10, 110, BTN_H), "Go to LEVEL:", _headerStyle);
            GUI.SetNextControlName("JumpLevelField");
            _jumpLevelInput = GUI.TextField(new Rect(112, innerY, 90, BTN_H), _jumpLevelInput, 3, _tfStyle);
            bool enterPressed = Event.current.type == EventType.KeyDown
                && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter)
                && GUI.GetNameOfFocusedControl() == "JumpLevelField";
            if (GUI.Button(new Rect(208, innerY, PANEL_W - 20 - 208, BTN_H), "Go ▶", _btnStyle) || enterPressed)
            {
                // The box takes the LEVEL NUMBER THE PLAYER SEES (2026-10-21 Spencer: "does the fx test menu not transfer me to
                // the correct level?" — typing 4 jumped to INTERNAL stage 4, an old tutorial slot; LEVEL 4 is internal 14).
                if (int.TryParse(_jumpLevelInput, out int lvl) && lvl > 0)
                {
                    LevelRules.ForceFreshBoardOnNextApply();   // same-stage jumps still get a fresh board
                    SurvivalManager.Instance?.DebugJumpToStage(lvl + LevelMapPanel.TUTORIAL_LEVELS);
                }
            }
            innerY += BTN_H + GAP;

            // TEMP (11-04): watch the crate slide slowly (Spencer saw a letter "thread between a crate in a very odd pattern")
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"FALL SLOW-MO: x{GridManager.FALL_SLOWMO:0}  (tap to toggle x4)", _btnStyle))
                GridManager.FALL_SLOWMO = GridManager.FALL_SLOWMO > 1f ? 1f : 4f;
            innerY += BTN_H + GAP;
            // ── SPECIAL TILE TESTS (2026-10-21 Spencer: "give me an opportunity to test each of the animations") ──
            // Each puts that special on a tile near the middle of the board, shows it for a beat, then fires it through
            // the REAL pipeline (rules clear + FX + gravity + cascades) — what you see is what play produces.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Rocket ↔ (row)", _btnStyle))   StartCoroutine(TestSpecial(RulesEngine.SPECIAL_ROCKET_H));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Rocket ↕ (column)", _btnStyle)) StartCoroutine(TestSpecial(RulesEngine.SPECIAL_ROCKET_V));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Bomb (3x3)", _btnStyle))           StartCoroutine(TestSpecial(RulesEngine.SPECIAL_BOMB));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Royal Orb / disco (most common letter)", _btnStyle))  StartCoroutine(TestSpecial(RulesEngine.SPECIAL_DISCO));
            innerY += BTN_H + GAP;
            // 11-03: single-fire tests for the new art (propeller book flight + trail, dynamite)
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Flying Tome (flies to a goal)", _btnStyle)) StartCoroutine(TestSpecial(RulesEngine.SPECIAL_AIRPLANE));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"BACKGROUND: next (now {SceneBootstrap.BACKGROUND_NAMES[SceneBootstrap.BackgroundIndex]})", _btnStyle)) SceneBootstrap.CycleBackground();   // 11-03
            innerY += BTN_H + GAP;
            // 11-03: SAFE tests - the door blow-off (anywhere, no safe needed) and one lamp hit on a safe on the board (L18)
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SAFE: door blow-off", _btnStyle)) { if (HandManager.Instance != null) HandManager.Instance.TestSafeBlowOff(); else Debug.LogWarning("[FXTest] safe: start a level first"); }
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SAFE: lamp hit (needs a safe on the board - L18)", _btnStyle)) { if (HandManager.Instance == null || !HandManager.Instance.TestSafeHit()) Debug.LogWarning("[FXTest] safe hit: no safe on the board (play L18)"); }
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SPECIAL: Dynamite", _btnStyle)) StartCoroutine(TestSpecial(RulesEngine.SPECIAL_TNT));
            innerY += BTN_H + GAP;
            // 11-03 COINS (Spencer: "give me a button to test the coins animation"): the new crown-coin spin sheet, two ways
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COINS: burst in level (fly to the coin counter)", _btnStyle))
            {
                var g = GridManager.Instance; var hud = HUDManager.Instance;
                if (hud != null && g != null) hud.SpawnTreasureCoins(g.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2) + new Vector3(g.BoardSlideOffsetX, 0f, -0.5f), 25);
                else Debug.LogWarning("[FXTest] coins: start a level first");
            }
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COINS: level-map cascade (+250)", _btnStyle))
            {
                if (LevelMapPanel.Instance != null) { _open = false; LevelMapPanel.Instance.CoinCascadeForDebug(250); }
                else Debug.LogWarning("[FXTest] LevelMapPanel.Instance missing - open the map once first.");
            }
            innerY += BTN_H + GAP;

            // ── SWAP COMBOS (2026-10-22) — two specials placed side by side, then swapped together ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: Rocket + Rocket (cross)", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_ROCKET_H, RulesEngine.SPECIAL_ROCKET_V));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: TNT + Rocket (3 rows + 3 cols)", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_TNT, RulesEngine.SPECIAL_ROCKET_H));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: Bomb + Bomb (7x7)", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_BOMB, RulesEngine.SPECIAL_BOMB));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: Disco + Rocket", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_DISCO, RulesEngine.SPECIAL_ROCKET_H));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: Disco + Disco (board)", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_DISCO, RulesEngine.SPECIAL_DISCO));
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "COMBO: Flying Tome + Flying Tome", _btnStyle)) StartCoroutine(TestCombo(RulesEngine.SPECIAL_AIRPLANE, RulesEngine.SPECIAL_AIRPLANE));
            innerY += BTN_H + GAP;
            // ── BOOSTER UNLOCK INTROS + USE ANIMATIONS (2026-10-29): replays the full tutorial, then the booster's animation ──
            {
                string[] bids = { BoosterManager.ID_FREE_SWAP, BoosterManager.ID_MALLET, BoosterManager.ID_BRAMBLE_SWEEP, BoosterManager.ID_AXE, BoosterManager.ID_SPYGLASS };
                string[] bnames = { "Swap", "Mallet", "Lightning", "Axe", "Spyglass" };
                float bw = (PANEL_W - 20 - 4 * 4) / 5f;
                GUI.Label(new Rect(0, innerY, PANEL_W - 20, BTN_H), "BOOSTER INTRO (tutorial + animation):", _btnStyle);
                innerY += BTN_H + GAP;
                for (int bi = 0; bi < bids.Length; bi++)
                    if (GUI.Button(new Rect(bi * (bw + 4), innerY, bw, BTN_H), bnames[bi], _btnStyle)) BoosterIntro.Instance?.Play(bids[bi]);
                innerY += BTN_H + GAP;
            }
            // ── HUD PROGRESS RING test (2026-10-29): drive the ring without playing; 'Real' hands it back to goal progress ──
            {
                float[] vals = { 0f, 0.3f, 0.6f, 1f, -1f }; string[] nm = { "0%", "30%", "60%", "100%", "Real" };
                float rw = (PANEL_W - 20 - 4 * 4) / 5f;
                GUI.Label(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"HUD RING (now {(HUDManager.RING_TEST < 0 ? "real" : (HUDManager.RING_TEST * 100f).ToString("0") + "%")}):", _btnStyle);
                innerY += BTN_H + GAP;
                for (int ri = 0; ri < vals.Length; ri++)
                    if (GUI.Button(new Rect(ri * (rw + 4), innerY, rw, BTN_H), nm[ri], _btnStyle)) HUDManager.RING_TEST = vals[ri];
                innerY += BTN_H + GAP;
            }
            // ── UNLOCK ALL BOOSTERS (2026-11-02): levels stop at 20, the spyglass opens at L22 ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"UNLOCK ALL BOOSTERS: {(TutorialLocks.DEBUG_ALL_BOOSTERS ? "ON (tap for normal)" : "OFF (tap to unlock)")}", _btnStyle))
            { TutorialLocks.DEBUG_ALL_BOOSTERS = !TutorialLocks.DEBUG_ALL_BOOSTERS; if (TutorialLocks.DEBUG_ALL_BOOSTERS) RefillAllBoosters(3); }
            innerY += BTN_H + GAP;
            // ── BOOSTER SLOT UNLOCK MOMENT (2026-10-29): replays it on the live slot ──
            {
                string[] uids = { BoosterManager.ID_FREE_SWAP, BoosterManager.ID_MALLET, BoosterManager.ID_BRAMBLE_SWEEP, BoosterManager.ID_SPYGLASS, BoosterManager.ID_AXE };
                string[] unames = { "Swap", "Mallet", "Lightning", "Spyglass", "Axe" };
                float uw = (PANEL_W - 20 - 4 * 4) / 5f;
                GUI.Label(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SLOT UNLOCK animation (slot must be open):", _btnStyle);
                innerY += BTN_H + GAP;
                for (int ui = 0; ui < uids.Length; ui++)
                    if (GUI.Button(new Rect(ui * (uw + 4), innerY, uw, BTN_H), unames[ui], _btnStyle)) BoosterHUDSlot.Instance?.TestUnlock(uids[ui]);
                innerY += BTN_H + GAP;
            }
            // ── SHINE A/B (2026-11-01): the dodge shine vs the old white band ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"SHINE: {(ShineSweep.V4 ? "NEW researched (tap for OLD white)" : "OLD white (tap for NEW researched)")}", _btnStyle)) { ShineSweep.V4 = !ShineSweep.V4; ShineSweep.V2 = ShineSweep.V3 = false; }
            innerY += BTN_H + GAP;
            // ── OWL moods (2026-11-01): play each mascot expression ──
            {
                int per = 4; float ow = (PANEL_W - 20 - 4 * (per - 1)) / per;
                for (int oi = 0; oi < OwlPortrait.MOODS.Length; oi++)
                {
                    if (oi > 0 && oi % per == 0) innerY += BTN_H + GAP;
                    if (GUI.Button(new Rect((oi % per) * (ow + 4), innerY, ow, BTN_H), "OWL " + OwlPortrait.MOODS[oi], _btnStyle)) OwlPortrait.Play(OwlPortrait.MOODS[oi]);
                }
                innerY += BTN_H + GAP;
            }
            // ── LEVEL CARD v2 preview (2026-11-01): drop the new card in for this level, plain or with the booster row ──
            {
                float cw = (PANEL_W - 20 - 4) / 2f;
                if (GUI.Button(new Rect(0, innerY, cw, BTN_H), "CARD: this level", _btnStyle)) { _open = false; LevelIntroModal.Instance?.PreviewV2(false); }
                if (GUI.Button(new Rect(cw + 4, innerY, cw, BTN_H), "CARD: + boosters", _btnStyle)) { _open = false; LevelIntroModal.Instance?.PreviewV2(true); }
                innerY += BTN_H + GAP;
                if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "CARD: chapter complete", _btnStyle)) { _open = false; ChapterEndCard.Show(); }
                innerY += BTN_H + GAP;
                if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "WELL DONE (reveal)", _btnStyle)) { _open = false; FireStageClearShow(); }   // 11-02
                innerY += BTN_H + GAP;
            }
            // ── MECHANIC SPLASH (2026-11-01): preview each power-up splash (doesn't mark it seen) ──
            {
                float sw = (PANEL_W - 20 - 12) / 4f;
                for (int si = 0; si < MechanicSplash.KEYS.Length; si++)
                    if (GUI.Button(new Rect(si * (sw + 4), innerY, sw, BTN_H), "SPLASH " + MechanicSplash.KEYS[si], _btnStyle))
                    {   // collapse the menu: it draws over the splash and a tap on its header would also close the splash
                        _open = false;
                        MechanicSplash.Show(MechanicSplash.KEYS[si], null, markDone: false);
                    }
                innerY += BTN_H + GAP;
            }
            // ── PRE-LEVEL BOOSTERS (2026-10-29) ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "BOOSTERS: test drop-in (rocket + bomb + disco)", _btnStyle)) PreBoosters.TestDropIn();
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"BOOSTERS: +3 each  (now {PreBoosters.Count(0)}/{PreBoosters.Count(1)}/{PreBoosters.Count(2)})", _btnStyle))
            { for (int bi = 0; bi < PreBoosters.KINDS.Length; bi++) PreBoosters.Add(bi, 3); }
            innerY += BTN_H + GAP;

            if (SHOW_LEGACY_TOOLS) {   // ── settled / not being tested (hidden 2026-10-29) ──
            // ── BOARD MODE (2026-10-21 Spencer: "as long as we can easily go back to the whole row rise game") ──
            // Remembered between sessions. Restarts the current level with the matching board.
            string modeLabel = SurvivalManager.FullBoardEnabled ? "BOARD: FULL (Candy Crush) — tap for RISING ROWS" : "BOARD: RISING ROWS — tap for FULL";
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), modeLabel, _btnStyle)) SurvivalManager.Instance?.ToggleFullBoardAndRestart();
            innerY += BTN_H + GAP;

            }   // ── end hidden ──

            // ── MONKEY TEST (2026-11-16) — a fake finger hammers the REAL game (taps, drags, mashing, buttons) and logs faults; report in MonkeyReports/ ──
            {
                var monkey = MonkeyTester.Instance;
                string mLabel = monkey != null && monkey.Running ? monkey.Status
                              : $"MONKEY TEST: slam inputs for {MonkeyTester.MINUTES:F0} min" + (monkey != null && !string.IsNullOrEmpty(monkey.Status) ? $"  ({monkey.Status})" : "");
                if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), mLabel, _btnStyle) && monkey != null) { if (!monkey.Running) MonkeyTester.PLAY_BADLY = false; monkey.Toggle(); }
                innerY += BTN_H + GAP;
                // same monkey, but it runs itself out of moves on most levels so the lose screens get hammered too
                if (monkey != null && !monkey.Running)
                {
                    if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"MONKEY TEST (PLAY BADLY): lose levels for {MonkeyTester.MINUTES:F0} min", _btnStyle)) { MonkeyTester.PLAY_BADLY = true; monkey.Toggle(); }
                    innerY += BTN_H + GAP;
                }
            }

            // ── BOT PLAYTEST (2026-10-23) — plays levels FIRST..LAST x TRIALS on a hidden engine; report in BotReports/ ──
            {
                var bot = BotPlaytester.Instance;
                string botLabel = bot != null && bot.Running ? bot.Status
                                : $"BOT PLAYTEST: levels {BotPlaytester.FIRST_LEVEL}-{BotPlaytester.LAST_LEVEL} x{BotPlaytester.TRIALS}"
                                  + (bot != null && !string.IsNullOrEmpty(bot.Status) ? $"  ({bot.Status})" : "");
                if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), botLabel, _btnStyle) && bot != null && !bot.Running) bot.Run();
                innerY += BTN_H + GAP;
                // SELECTED LEVELS (10-30): type the levels, then run just those with more games each
                BotPlaytester.SELECTED = GUI.TextField(new Rect(0, innerY, (PANEL_W - 20) * 0.32f, BTN_H), BotPlaytester.SELECTED);
                if (GUI.Button(new Rect((PANEL_W - 20) * 0.34f, innerY, (PANEL_W - 20) * 0.66f, BTN_H), $"BOT: ONLY THESE x{BotPlaytester.SELECTED_TRIALS}", _btnStyle) && bot != null && !bot.Running) bot.RunSelected();
                innerY += BTN_H + GAP;
            }

            // ── SCREENSHOT FOR CLAUDE (2026-10-23) — same as pressing F8: saves Screenshots/claude_latest.png ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "SCREENSHOT FOR CLAUDE (or press ` key)", _btnStyle))
                ClaudeScreenshot.Instance?.Take();
            innerY += BTN_H + GAP;

            if (SHOW_LEGACY_TOOLS) {   // ── settled / not being tested (hidden 2026-10-29) ──
            // ── MULTI-WORD POP (2026-10-22): several words detonating together — word by word, or all at once ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), HandManager.MULTI_WORD_STAGGER ? "MULTI-WORD POP: WORD BY WORD — tap for all at once" : "MULTI-WORD POP: ALL AT ONCE — tap for word by word", _btnStyle))
            { HandManager.MULTI_WORD_STAGGER = !HandManager.MULTI_WORD_STAGGER; WDLog.Log($"[WordPop] multiStagger={HandManager.MULTI_WORD_STAGGER}"); }
            innerY += BTN_H + GAP;

            // ── WORD POP (2026-10-22 Spencer: one pop vs the per-letter ripple) ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), WordDropFX.SINGLE_WORD_POP ? "WORD POP: ONE POP — tap for letter ripple" : "WORD POP: LETTER RIPPLE — tap for one pop", _btnStyle))
            { WordDropFX.SINGLE_WORD_POP = !WordDropFX.SINGLE_WORD_POP; WDLog.Log($"[WordPop] single={WordDropFX.SINGLE_WORD_POP}"); }
            innerY += BTN_H + GAP;

            // ── TILE SIZE (2026-10-21 Spencer: "more padding in between letters") — cycles 100/92/88/84% of the cell ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), $"TILE SIZE: {Tile.TILE_DISPLAY_RATIO:P0} of cell — tap to cycle", _btnStyle))
            {
                var steps = Tile.TILE_RATIO_STEPS; int i = 0;
                for (int k = 0; k < steps.Length; k++) if (Mathf.Abs(steps[k] - Tile.TILE_DISPLAY_RATIO) < 0.005f) i = k;
                Tile.TILE_DISPLAY_RATIO = steps[(i + 1) % steps.Length];
                var g = GridManager.Instance;
                if (g != null) for (int c = 0; c < RulesEngine.COLS; c++) for (int r = 0; r < RulesEngine.ROWS; r++) g.GetTile(c, r)?.RefitRestScale();
                WDLog.Log($"[TileSize] ratio -> {Tile.TILE_DISPLAY_RATIO:F2}");
            }
            innerY += BTN_H + GAP;

            }   // ── end hidden ──

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            // ── SCORED FLASH VARIANTS (2026-09-10 Spencer) ─────────────────────────────
            // Five CALLERS all funnel into the same PlayWordScored, so buttons per caller
            // would look identical. What actually differs is the STATE of the word when it
            // arrives — whether it arms, whether it is gold, and how deep in the chain it is.
            // These are those states, so the one he means can be identified rather than guessed.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 0 — REAL primed cluster \u2713", _btnStyle))
                TestFlashRealCluster(0);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 0b — REAL cluster, chain 3", _btnStyle))
                TestFlashRealCluster(3);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 1 — arms, ROSE", _btnStyle))
                TestFlashVariant(gold: false, chainStep: 0);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 2 — arms, GOLD", _btnStyle))
                TestFlashVariant(gold: true, chainStep: 0);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 3 — arms, GOLD, chain 3", _btnStyle))
                TestFlashVariant(gold: true, chainStep: 3);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 4 — scores, does NOT arm", _btnStyle))
                TestFlashNoArm();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flash 5 — detonation pre-green", _btnStyle))
                TestFlashPreGreen();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Board Shake (tier 2)", _btnStyle))
                WordDropFX.Instance?.PlayBoardShake(1, 5);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Cascade Pops (3x climb)", _btnStyle))
                FireCascadePops();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "★ LEVEL COMPLETED — Test celebration", _btnStyle))
                FireStageClearShow();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "✓ Checkmark badge — pop it", _btnStyle))
                HUDManager.Instance?.DebugFlashObjectiveCheck();
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            if (SHOW_LEGACY_TOOLS) {   // ── settled / not being tested (hidden 2026-10-29) ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Level Clear Modal — Show", _btnStyle))
                FireStageClearShow();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Level Clear Modal — Hide", _btnStyle))
                FireStageClearHide();
            innerY += BTN_H + GAP;

            }   // ── end hidden ──

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Unlock Modal — Show", _btnStyle))
                UnlockModal.Instance?.ShowForDebug();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Unlock Modal — Hide", _btnStyle))
                UnlockModal.Instance?.HideForDebug();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "WILD Unlock — Full Flow (modal→claim→inject→hint)", _btnStyle))
                HandManager.Instance?.TriggerWildUnlockFlow();
            innerY += BTN_H + GAP;

            // Re-arm the ONE-TIME first-wild tutorial: clear the persisted flag so the NEXT wild earned in real
            // gameplay fires the unlock flow again (the flow otherwise never repeats). 2026-07-10 Spencer.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "WILD Unlock — RESET flag (arm first-wild)", _btnStyle))
            {
                PlayerPrefs.DeleteKey("wild_unlock_taught");
                PlayerPrefs.Save();
                WDLog.Log("[FXTest] wild_unlock_taught cleared — next wild earned in gameplay will re-trigger the unlock tutorial.");
            }
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H),
                    $"Tutorial — RESET (as if never played)  done={SurvivalManager.TutorialDone}", _btnStyle))
            {
                // Full clean-slate: the one-time run flag, the interactive-coaching flags (ResetTutorial), the
                // wild-unlock coaching, and the tool-teach hints — so the whole onboarding runs fresh. 2026-07-14.
                SurvivalManager.TutorialDone = false;
                TutorialManager.ResetTutorial();            // clears tutorial_complete + hint_rewrite/hint_swap
                TutorialBeats.ResetAll();                   // 2026-10-23 rebuilt tutorial beats
                SurvivalManager.ClearSavedProgress();       // 11-16 saved level: "as if never played" starts at level 1 again
                BoosterHUDSlot.ResetSlotsSeen();            // 2026-10-23 booster unlock moments replay
                PlayerPrefs.DeleteKey("wild_unlock_taught"); // wild-unlock tutorial re-arms
                PlayerPrefs.Save();
                WDLog.Log("[FXTest] tutorial fully reset — next run starts at level 1 and every onboarding beat re-fires.");
            }
            innerY += BTN_H + GAP;

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Top Out Panel (drop / dwell / exit)", _btnStyle))
                FireTopOutPanel();
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            // 11-16: back in the visible menu - the Continue card was rebuilt (ContinueModal.CARD_V2) and needs checking
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "CONTINUE SCREEN — show (out of moves)", _btnStyle))
                FireContinueModalShow();
            innerY += BTN_H + GAP;

            // 11-16: the Out of Moves banner (full-width band + dim) needs checking too
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "OUT OF MOVES BANNER — drop", _btnStyle))
                FireTopOutPanel();
            innerY += BTN_H + GAP;

            if (SHOW_LEGACY_TOOLS) {   // ── settled / not being tested (hidden 2026-10-29) ──

            // ── Target completion celebration (icon pop + white spark burst + check) ──
            // Needs an active objective on screen (the Target panel) to celebrate.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Target Completion (celebrate + check)", _btnStyle))
                HUDManager.Instance?.FlashObjectiveComplete();
            innerY += BTN_H + GAP;

            }   // ── end hidden ──

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            // ── MVP P5 turn-based playtest toggle ────────────────────────────────
            string riseLabel = SurvivalManager.RisePerMoveDebug
                ? "Rise-Per-Move:  ON  (turn-based)"
                : "Rise-Per-Move:  OFF (time-based)";
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), riseLabel, _btnStyle))
                SurvivalManager.RisePerMoveDebug = !SurvivalManager.RisePerMoveDebug;
            innerY += BTN_H + GAP;

            // ── Global "edits count as moves" override (feel-test on every level) ─────
            string editMovesLabel = SurvivalManager.EditsCountAsMovesGlobalOverride
                ? "Edits Count As Moves:  ON  (all levels)"
                : "Edits Count As Moves:  OFF (per-level)";
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), editMovesLabel, _btnStyle))
                SurvivalManager.EditsCountAsMovesGlobalOverride = !SurvivalManager.EditsCountAsMovesGlobalOverride;
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            // ── Booster cheat: refill all 4 boosters to 99 charges ───────────────
            // Lets us spam boosters during the cascade-bug repro session.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Refill Boosters (99 each)", _btnStyle))
                RefillAllBoosters(99);
            innerY += BTN_H + GAP;

            // ── Between-level progression map (preview the avatar hop in isolation) ──
            if (SHOW_LEGACY_TOOLS) {   // ── settled / not being tested (hidden 2026-10-29) ──
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Level Map — Show (avatar hop)", _btnStyle))
                LevelMapPanel.Instance?.ShowForDebug();
            innerY += BTN_H + GAP;

            // Coin cascade in isolation — for tuning coin size / spin / landing feel without
            // replaying a level. Shows the map first if it isn't up. 2026-07-29.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Coins — Test cascade (+250 to pill)", _btnStyle))
            {
                if (LevelMapPanel.Instance != null) LevelMapPanel.Instance.CoinCascadeForDebug(250);
                else Debug.LogWarning("[FXTest] LevelMapPanel.Instance missing — open the map once first.");
            }
            innerY += BTN_H + GAP;

            // Phase 1: route the level-intro loop through the map (Candy-Crush: completed → MAP → play modal).
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H),
                    LevelMapPanel.MapFlowEnabled ? "Map-in-Loop:  ON  (map between levels)" : "Map-in-Loop:  OFF (direct to level)", _btnStyle))
                LevelMapPanel.MapFlowEnabled = !LevelMapPanel.MapFlowEnabled;
            innerY += BTN_H + GAP;

            // Preview the level-entry spring-in (board pop + HUD bar slide) without re-entering a level.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Level Entry — Test spring-in (board + HUD)", _btnStyle))
                HUDManager.Instance?.PlayLevelEntry();
            innerY += BTN_H + GAP;

            }   // ── end hidden ──

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            // ── Force a WILD into the hand (to test the iridescent wild tile) ────
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Force WILD into hand", _btnStyle))
                HandManager.Instance?.DebugForceWildIntoHand();
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            if (SHOW_LEGACY_TOOLS) {   // ── legacy tools (drop / armed-word era, one-off tuning) — hidden 2026-10-23 ──
            innerY += 14;

            // ── FX Bloom isolation tests — fire each glow alone so its slider
            //    can be tuned without triggering a whole detonation. ─────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── FX Bloom Tests ──", _headerStyle);
            innerY += HEADER_H + 4;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Glint  (Glint Cap)", _btnStyle))
                StartCoroutine(TestGlint());
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Scored Flash  (Scored Flash Green)", _btnStyle))
                TestScoredFlash();
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Primed Glow  (Primed Glow Cap)", _btnStyle))
                StartCoroutine(TestPrimedGlow());
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Bubble Glow  (Bubble Glow HDR)", _btnStyle))
                TestBubbleGlow();
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Pop Aura  (Pop Aura HDR)", _btnStyle))
                TestPopAura();
            innerY += BTN_H + GAP;
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▸ Sparkle  (Sparkle Bright)", _btnStyle))
                TestSparkle();
            innerY += BTN_H + GAP;
            innerY += 14;

            // ── Forced full-stack detonations ────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Forced Detonation ──", _headerStyle);
            innerY += HEADER_H + 4;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 1 (3 tiles)", _btnStyle))
                FireFakeDetonation(chainStep: 0, fakeTileCount: 3, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 1 Pop (CC-style)", _btnStyle))
                FireTier1Pop();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 2 (8 tiles, initial drop)", _btnStyle))
                FireFakeDetonation(chainStep: 0, fakeTileCount: 8, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 3 (12 tiles, initial drop)", _btnStyle))
                FireFakeDetonation(chainStep: 0, fakeTileCount: 12, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 3 CASCADE (10 tiles, chain=2)", _btnStyle))
                FireFakeDetonation(chainStep: 2, fakeTileCount: 10, forceMeltdown: false);
            innerY += BTN_H + GAP;

            // Watch the FULL cascade explosion on visible tiles — holds so you see the tiles,
            // then plays the cascade pop in slow motion (whole arc: light-up → pop → fragments → fade).
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▶ WATCH Cascade — SLOW-MO (visible)", _btnStyle))
                FireWatchCascade(fakeTileCount: 6, chainStep: 2, slow: true);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "▶ WATCH Cascade — normal speed (visible)", _btnStyle))
                FireWatchCascade(fakeTileCount: 6, chainStep: 2, slow: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 4 MELTDOWN (15 tiles)", _btnStyle))
                FireFakeDetonation(chainStep: 3, fakeTileCount: 15, forceMeltdown: true);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown Intro Only (text + flash)", _btnStyle))
                FireMeltdownIntroOnly();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown FULL SEQUENCE (intro + hold + outro)", _btnStyle))
                FireMeltdownFullSequence();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown BLAST ONLY (skip windup)", _btnStyle))
                FireMeltdownBlastOnly();
            innerY += BTN_H + GAP;

            innerY += 8;

            // ── Real-tile detonations — fires FX on actual board tiles, then
            // reactivates them so game state stays clean. Useful for seeing
            // how the explosion reads against the populated grid (vs dummy
            // tiles in empty space).
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 8 board tiles (Tier 2 initial)", _btnStyle))
                DetonateRealBoardTiles(count: 8, chainStep: 0, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 12 board tiles (Tier 3 initial)", _btnStyle))
                DetonateRealBoardTiles(count: 12, chainStep: 0, forceMeltdown: false);
            innerY += BTN_H + GAP;

            // ── Isolated tier-3 burst: glow+rays+flash ONLY at a known tile ──────
            // No detonation, no meltdown, no tile destruction — just fires
            // PlayTier3Burst at the center tile so you can study where the glow
            // lands and how it reads, without the 8+ meltdown layer burying it.
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier-3 BURST ONLY @ center tile", _btnStyle))
                FireTier3BurstOnly();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 8 board tiles CASCADE (chain=2)", _btnStyle))
                DetonateRealBoardTiles(count: 8, chainStep: 2, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 12 tiles MELTDOWN (Tier 4)", _btnStyle))
                DetonateRealBoardTiles(count: 12, chainStep: 3, forceMeltdown: true);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown BLAST on REAL tiles (skip windup)", _btnStyle))
                FireMeltdownBlastOnRealTiles();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Cascade Word + BigBurst (horizontal)", _btnStyle))
                FireCascadeWordBigBurst(vertical: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Cascade Word + BigBurst (vertical)", _btnStyle))
                FireCascadeWordBigBurst(vertical: true);
            innerY += BTN_H + GAP;

            innerY += 14;

            // ── Layer toggles ────────────────────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Layer Toggles ──", _headerStyle);
            innerY += HEADER_H + 4;

            innerY = ToggleRow(innerY, "Meltdown Prefab",        ref WordDropFX.FX_MeltdownPrefab);
            innerY = ToggleRow(innerY, "Meltdown Intro Flash",   ref WordDropFX.FX_MeltdownIntroFlash);
            innerY = ToggleRow(innerY, "Tile Heat Overlay",      ref WordDropFX.FX_TileHeatOverlay);
            innerY = ToggleRow(innerY, "Primed Glow Orb",        ref WordDropFX.FX_PrimedGlowOrb);
            innerY = ToggleRow(innerY, "Meltdown Tile Punch",    ref WordDropFX.FX_MeltdownTilePunch);
            innerY = ToggleRow(innerY, "Meltdown Windup Shake",  ref WordDropFX.FX_MeltdownWindupShake);
            innerY = ToggleRow(innerY, "Flipbook Glow",          ref WordDropFX.FX_FlipbookGlow);
            innerY = ToggleRow(innerY, "Tile Flash",             ref WordDropFX.FX_TileFlash);
            innerY = ToggleRow(innerY, "Tile Fragments",         ref WordDropFX.FX_TileFragments);
            innerY = ToggleRow(innerY, "Tile Flash Box",         ref WordDropFX.FX_TileFlashBox);
            innerY = ToggleRow(innerY, "Sparkle Particles",      ref WordDropFX.FX_SparkleParticles);
            innerY = ToggleRow(innerY, "Sparkle Spray",          ref WordDropFX.FX_SparkleSpray);
            innerY = ToggleRow(innerY, "Sparkle Line",           ref WordDropFX.FX_SparkleLine);
            innerY = ToggleRow(innerY, "Big Burst Flash",        ref WordDropFX.FX_BigBurstFlash);
            innerY = ToggleRow(innerY, "Board Shake",            ref WordDropFX.FX_BoardShake);
            innerY = ToggleRow(innerY, "Detonation Audio",       ref WordDropFX.FX_DetonationAudio);
            innerY = ToggleRow(innerY, "Haptics",                ref WordDropFX.FX_Haptics);

            innerY += 10;

            // ── Bulk ─────────────────────────────────────────────────────────────
            if (GUI.Button(new Rect(0, innerY, (PANEL_W - 24) / 2, BTN_H), "All ON", _btnStyle))
                SetAll(true);
            if (GUI.Button(new Rect((PANEL_W - 24) / 2 + 4, innerY, (PANEL_W - 24) / 2, BTN_H), "All OFF", _btnStyle))
                SetAll(false);
            innerY += BTN_H + GAP;
            }   // ── end legacy tools ──

            _contentH = innerY + 10;
            GUI.EndScrollView();
        }

        private int ToggleRow(int innerY, string label, ref bool flag)
        {
            flag = GUI.Toggle(new Rect(0, innerY, PANEL_W - 20, TOGGLE_H), flag, " " + label, _toggleStyle);
            return innerY + TOGGLE_H;
        }

        private void SetAll(bool v)
        {
            WordDropFX.FX_MeltdownPrefab     = v;
            WordDropFX.FX_MeltdownIntroFlash = v;
            WordDropFX.FX_TileHeatOverlay    = v;
            WordDropFX.FX_PrimedGlowOrb      = v;
            WordDropFX.FX_MeltdownTilePunch  = v;
            WordDropFX.FX_MeltdownWindupShake = v;
            WordDropFX.FX_FlipbookGlow       = v;
            WordDropFX.FX_TileFlash          = v;
            WordDropFX.FX_TileFragments      = v;
            WordDropFX.FX_TileFlashBox       = v;
            WordDropFX.FX_SparkleParticles   = v;
            WordDropFX.FX_SparkleSpray       = v;
            WordDropFX.FX_SparkleLine        = v;
            WordDropFX.FX_BigBurstFlash      = v;
            WordDropFX.FX_BoardShake         = v;
            WordDropFX.FX_DetonationAudio    = v;
            WordDropFX.FX_Haptics            = v;
        }

        // ── Direct-fire helpers ───────────────────────────────────────────────────

        private Vector3 ScreenCenterWorld()
        {
            Camera cam = Camera.main;
            if (cam == null) return Vector3.zero;
            return new Vector3(cam.transform.position.x, cam.transform.position.y, 0f);
        }

        private void FireMeltdownPrefab()
        {
            if (FlipbookExplosion.Instance == null) { Debug.LogWarning("[FXTest] FlipbookExplosion missing"); return; }
            FlipbookExplosion.Instance.PlayMeltdown(ScreenCenterWorld());
        }

        private void RefillAllBoosters(int targetCount)
        {
            var bm = BoosterManager.Instance;
            if (bm == null) { Debug.LogWarning("[FXTest] BoosterManager missing — start a Survival run first"); return; }

            // AddCharges only works for booster IDs already in inventory.
            // StartRun populates the 4 MVP boosters; if a run hasn't started,
            // _charges is empty and AddCharges is a no-op.
            string[] ids = {
                BoosterManager.ID_BLOOMBURST,
                BoosterManager.ID_BRAMBLE_SWEEP,
                BoosterManager.ID_WISPWHIRL,
                BoosterManager.ID_ROCK_CRUSHER,
                BoosterManager.ID_FREE_SWAP, BoosterManager.ID_MALLET, BoosterManager.ID_AXE, BoosterManager.ID_SPYGLASS,   // the current row (11-02)
            };

            int refilled = 0;
            foreach (string id in ids)
            {
                int current = bm.GetCharges(id);
                int delta = targetCount - current;
                if (delta > 0)
                {
                    bm.AddCharges(id, delta);
                    refilled++;
                }
            }

            WDLog.Log($"[FXTest] Refilled {refilled} booster(s) to {targetCount} charges each");
        }

        private void FireFlipbookGlow()
        {
            bool savedGlow = WordDropFX.FX_FlipbookGlow;
            WordDropFX.FX_FlipbookGlow = true;
            FlipbookExplosion.Instance?.Play(ScreenCenterWorld(), tier: 3);
            WordDropFX.FX_FlipbookGlow = savedGlow;
        }

        private void FireBigBurst(bool vertical)
        {
            if (BigBurstFlash.Instance == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            float halfH = cam.orthographicSize;
            float halfW = halfH * cam.aspect;
            float length = (vertical ? halfH : halfW) * 2.4f;
            float thickness = 1.0f;
            BigBurstFlash.Instance.Play(ScreenCenterWorld(), length, thickness, vertical, null);
        }

        private void FireSparkleSpray()
        {
            if (SparkleSpray.Instance == null) return;
            SparkleSpray.Instance.Play(ScreenCenterWorld(), 1f);
        }

        private void FireSparkleLine()
        {
            if (GameParticles.Instance == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            float halfW = cam.orthographicSize * cam.aspect;
            Vector3 c = ScreenCenterWorld();
            Vector3 a = new Vector3(c.x - halfW * 1.1f, c.y, c.z);
            Vector3 b = new Vector3(c.x + halfW * 1.1f, c.y, c.z);
            GameParticles.Instance.PlaySparkleLine(a, b, 20);
        }

        private void FireConfetti()
        {
            GameParticles.Instance?.PlayMeltdown(ScreenCenterWorld());
        }

        private void FireTopOutPanel()
        {
            if (TopOutPanel.Instance == null)
            {
                Debug.LogWarning("[FXTest] TopOutPanel.Instance missing — not bootstrapped?");
                return;
            }
            TopOutPanel.Instance.SetText("Out of Moves!");
            TopOutPanel.Instance.Show();
        }

        private void FireContinueModalShow()
        {
            // Spawns the ContinueModal singleton on demand if it doesn't
            // exist yet (mirrors how SurvivalManager creates it the first
            // time the player tops out). Then calls Show with the actual
            // SurvivalManager so the cost ladder + ad availability render
            // correctly. If Survival isn't active, fall back to null —
            // Show() handles the null path with a default cost of 50.
            if (ContinueModal.Instance == null)
            {
                var modalGO = new GameObject("ContinueModalRoot");
                modalGO.AddComponent<ContinueModal>();
            }
            if (ContinueModal.Instance == null)
            {
                Debug.LogWarning("[FXTest] ContinueModal failed to bootstrap.");
                return;
            }
            ContinueModal.Instance.Show(SurvivalManager.Instance);
        }

        private void FireStageClearShow()
        {
            if (StageClearModal.Instance == null)
            {
                Debug.LogWarning("[FXTest] StageClearModal.Instance missing.");
                return;
            }
            StageClearModal.Instance.ShowForDebug();
        }

        private void FireStageClearHide()
        {
            if (StageClearModal.Instance == null) return;
            StageClearModal.Instance.DismissForDebug();
        }

        private void FireCascadePops()
        {
            if (GameAudio.Instance == null)
            {
                Debug.LogWarning("[FXTest] GameAudio.Instance missing.");
                return;
            }
            StartCoroutine(CascadePopsCoroutine());
        }

        private System.Collections.IEnumerator CascadePopsCoroutine()
        {
            // 3 pops in rapid succession to exercise the cascade pitch climb.
            // Each call within MATCH_LINE_BURST_WINDOW (500ms) increments the
            // burst counter → the matchline pop pitches up 2 semitones each.
            // Uses PlayMatchLine (matchline6/7/8 — the cascade tile-pop chime)
            // not PlayWordScored (word-scored chord — sounds similar to prime).
            for (int i = 0; i < 3; i++)
            {
                GameAudio.Instance.PlayMatchLine(i);
                yield return new WaitForSeconds(0.18f);
            }
        }

        // ── Forced full-stack detonation ─────────────────────────────────────────

        private void FireFakeDetonation(int chainStep, int fakeTileCount, bool forceMeltdown)
        {
            if (WordDropFX.Instance == null) { Debug.LogWarning("[FXTest] WordDropFX missing"); return; }

            var fake = BuildVisibleFakeTiles(fakeTileCount);

            if (forceMeltdown)
                StartCoroutine(ForcedMeltdownDetonation(fake, chainStep, fakeTileCount));
            else
                // big_pop + anticipation hold, then explode — mirrors the real
                // resolvers so the test buttons reproduce the synced timing
                // (original explosion 8+ tiles booms with the swell building
                // into the pop; cascade buttons stay silent).
                StartCoroutine(FakeBigPopThenExplode(fake, chainStep, fakeTileCount));
        }

        /// <summary>Builds a row of REAL, visible tiles (letter + sprite) at screen center for FX tests.
        /// Initialise() is self-contained (no GridManager dependency), so the tiles render their actual
        /// look — you see the tile itself explode, not just bare particles. Auto-cleaned after 5s.
        /// 2026-06-23 Spencer (was invisible un-Initialised dummies).</summary>
        private List<Tile> BuildVisibleFakeTiles(int count)
        {
            const string LETTERS = "WORDPLAYSTARGEMBLOOMCANDY"; // arbitrary visible letters, cycled
            var fake = new List<Tile>();
            Vector3 c = ScreenCenterWorld();
            float spacing = 1.0f; // visible tiles are ~0.9 wide; 1.0 keeps them from overlapping
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject($"FXTest_DummyTile_{i}");
                var tile = go.AddComponent<Tile>();
                tile.Initialise(LETTERS[i % LETTERS.Length], i, 0, 1f); // letter + sprite → visible
                go.transform.position = new Vector3(c.x + (i - count * 0.5f + 0.5f) * spacing, c.y, 0f);
                fake.Add(tile);
                Destroy(go, 5f); // safety cleanup
            }
            return fake;
        }

        private System.Collections.IEnumerator FakeBigPopThenExplode(List<Tile> fake, int chainStep, int fakeTileCount)
        {
            yield return WordDropFX.MaybeBigPopAndHold(fake);
            WordDropFX.Instance.PlayExplosion(fake, chainStep, fakeTileCount);
        }

        /// <summary>"Watch the whole cascade explosion" test: spawns visible tiles, holds so you can SEE
        /// them sitting there, then runs the cascade detonation — optionally in slow motion so the full
        /// arc (light-up → pop → fragments → fade) is observable. Restores timeScale when done.
        /// 2026-06-23 Spencer.</summary>
        private void FireWatchCascade(int fakeTileCount, int chainStep, bool slow)
        {
            if (WordDropFX.Instance == null) { Debug.LogWarning("[FXTest] WordDropFX missing"); return; }
            StartCoroutine(WatchCascadeCoroutine(fakeTileCount, chainStep, slow));
        }

        private System.Collections.IEnumerator WatchCascadeCoroutine(int fakeTileCount, int chainStep, bool slow)
        {
            var fake = BuildVisibleFakeTiles(fakeTileCount);
            // Beat to let you see the tiles BEFORE they go (realtime so slow-mo below doesn't stretch it).
            yield return new WaitForSecondsRealtime(0.8f);

            float prevTimeScale = Time.timeScale;
            if (slow) Time.timeScale = 0.3f; // slow-mo the whole sequence so it's watchable
            try
            {
                // STEP 1 — word-scored glow on the word the cascade forms. This is the green
                // bloom that lights up the tiles BEFORE detonation; the real resolver fires it
                // (GameVisualBridge.cs:566 / HandManager) right before PlayExplosion. Was missing
                // from this test, which jumped straight to the boom. 2026-06-23.
                WordDropFX.Instance.PlayWordScored(fake, new Color(0.45f, 1f, 0.55f, 1f), chainStep);
                yield return new WaitForSecondsRealtime(slow ? 1.2f : 0.45f); // let the glow read

                // STEP 2 — the detonation (big-pop hold, then explode).
                yield return WordDropFX.MaybeBigPopAndHold(fake);
                WordDropFX.Instance.PlayExplosion(fake, chainStep, fakeTileCount);
                // Hold in realtime long enough to cover the slowed animation, then restore.
                yield return new WaitForSecondsRealtime(slow ? 2.0f : 1.0f);
            }
            finally
            {
                Time.timeScale = prevTimeScale; // always restore, even if interrupted
            }
        }

        /// <summary>
        /// Picks a row/column of real board tiles, force-enables the standard
        /// detonation FX + the BigBurst override, and fires PlayExplosion
        /// with chainStep=2 to simulate a cascade word detonation. Uses real
        /// tiles so the flash/fragments/sparkles actually show on visible
        /// sprites.
        /// </summary>
        private void FireCascadeWordBigBurst(bool vertical)
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            // Find the row/column with the MOST active tiles (3-min, no upper cap).
            // Lenient — a sparse board still gets a usable line for the test.
            const int MIN_TILES = 3;
            var tiles = new List<Tile>();

            if (vertical)
            {
                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    var colTiles = new List<Tile>();
                    for (int row = 0; row < RulesEngine.ROWS; row++)
                    {
                        var t = GridManager.Instance.GetTile(col, row);
                        if (t != null && t.gameObject.activeSelf) colTiles.Add(t);
                    }
                    if (colTiles.Count > tiles.Count) tiles = colTiles;
                }
            }
            else
            {
                for (int row = 0; row < RulesEngine.ROWS; row++)
                {
                    var rowTiles = new List<Tile>();
                    for (int col = 0; col < RulesEngine.COLS; col++)
                    {
                        var t = GridManager.Instance.GetTile(col, row);
                        if (t != null && t.gameObject.activeSelf) rowTiles.Add(t);
                    }
                    if (rowTiles.Count > tiles.Count) tiles = rowTiles;
                }
            }

            if (tiles.Count < MIN_TILES)
            {
                Debug.LogWarning($"[FXTest] Cascade word — best {(vertical ? "column" : "row")} has only {tiles.Count} active tiles (need {MIN_TILES}+); drop a few more on the board.");
                return;
            }
            WDLog.Log($"[FXTest] Cascade word — using {tiles.Count} tiles in best {(vertical ? "column" : "row")}");

            StartCoroutine(CascadeWordBigBurstCoroutine(tiles, tiles.Count));
        }

        private IEnumerator CascadeWordBigBurstCoroutine(List<Tile> tiles, int wordLen)
        {
            // Save current toggle states so the test fire doesn't permanently
            // change the user's selections.
            bool savedBigBurst        = WordDropFX.FX_BigBurstFlashCascadeTest;
            bool savedTileFlash       = WordDropFX.FX_TileFlash;
            bool savedTileFragments   = WordDropFX.FX_TileFragments;
            bool savedSparkleSpray    = WordDropFX.FX_SparkleSpray;
            bool savedDetonationAudio = WordDropFX.FX_DetonationAudio;
            bool savedSparkleParticles = WordDropFX.FX_SparkleParticles;
            bool savedFlipbookGlow    = WordDropFX.FX_FlipbookGlow;
            bool savedBoardShake      = WordDropFX.FX_BoardShake;

            // Force the standard detonation FX layers ON so the test shows
            // what a real cascade word detonation will look like — not just
            // the BigBurst beam in isolation.
            WordDropFX.FX_BigBurstFlashCascadeTest = true;
            WordDropFX.FX_TileFlash       = true;
            WordDropFX.FX_TileFragments   = true;
            WordDropFX.FX_SparkleSpray    = true;
            WordDropFX.FX_DetonationAudio = true;
            WordDropFX.FX_SparkleParticles = true;
            WordDropFX.FX_FlipbookGlow    = true;
            WordDropFX.FX_BoardShake      = true;
            WDLog.Log("[FXTest] Cascade word test — forcing standard detonation FX ON");

            // Cache positions + scales so we can restore tiles after PlayExplosion
            // hides them (otherwise the board permanently loses these tiles).
            var positions = new List<Vector3>(tiles.Count);
            var scales    = new List<Vector3>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                positions.Add(tiles[i].transform.position);
                scales.Add(tiles[i].transform.localScale);
            }

            yield return WordDropFX.Instance.PlayExplosion(tiles, chainStep: 2, wordLength: wordLen);

            // Hold a beat so any tail FX (fragments fade, sparkle fly-out,
            // beam fade) finish before restoring tiles + toggle state.
            yield return new WaitForSeconds(0.8f);

            // Restore the tiles — re-activate, kill any leftover tweens, snap
            // position + scale back to their pre-explosion values.
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                tiles[i].gameObject.SetActive(true);
                tiles[i].transform.DOKill();
                tiles[i].transform.position    = positions[i];
                tiles[i].transform.localScale  = scales[i];
                tiles[i].transform.localRotation = Quaternion.identity;
            }

            WordDropFX.FX_BigBurstFlashCascadeTest = savedBigBurst;
            WordDropFX.FX_TileFlash       = savedTileFlash;
            WordDropFX.FX_TileFragments   = savedTileFragments;
            WordDropFX.FX_SparkleSpray    = savedSparkleSpray;
            WordDropFX.FX_DetonationAudio = savedDetonationAudio;
            WordDropFX.FX_SparkleParticles = savedSparkleParticles;
            WordDropFX.FX_FlipbookGlow    = savedFlipbookGlow;
            WordDropFX.FX_BoardShake      = savedBoardShake;
        }

        private void FireMeltdownIntroOnly()
        {
            if (MeltdownManager.Instance == null) { Debug.LogWarning("[FXTest] MeltdownManager missing"); return; }
            StartCoroutine(MeltdownIntroOnlyCoroutine());
        }

        /// <summary>
        /// Plays the TRUE meltdown moment: intro title stamp + tier-4 explosion
        /// + outro fade, all stacked together. Matches the documented sequence
        /// in MeltdownManager.cs lines 24-26 (intro before explosion → explosion
        /// while title visible → outro after). Re-added 2026-05-30 per Spencer
        /// — the meltdown mechanic isn't currently wired into gameplay, but
        /// this button keeps the animation iterable from the FXTestMenu.
        /// </summary>
        private void FireMeltdownFullSequence()
        {
            if (MeltdownManager.Instance == null) { Debug.LogWarning("[FXTest] MeltdownManager missing"); return; }
            if (WordDropFX.Instance == null)      { Debug.LogWarning("[FXTest] WordDropFX missing"); return; }
            StartCoroutine(MeltdownFullSequenceCoroutine());
        }

        private IEnumerator MeltdownFullSequenceCoroutine()
        {
            // Flip all meltdown-gated FX flags ON for the duration so the
            // sequence reads correctly even if Spencer toggled some off.
            bool savedPrefab     = WordDropFX.FX_MeltdownPrefab;
            bool savedIntroFlash = WordDropFX.FX_MeltdownIntroFlash;
            bool savedTilePunch  = WordDropFX.FX_MeltdownTilePunch;
            bool savedWindup     = WordDropFX.FX_MeltdownWindupShake;
            WordDropFX.FX_MeltdownPrefab      = true;
            WordDropFX.FX_MeltdownIntroFlash  = true;
            WordDropFX.FX_MeltdownTilePunch   = true;
            WordDropFX.FX_MeltdownWindupShake = true;

            // Build fake tiles for the explosion — same dummy-tile pattern as
            // FireFakeDetonation. PlayExplosion's per-tile loops iterate over
            // them; missing components short-circuit cleanly.
            const int FAKE_TILE_COUNT = 15;
            const float SPACING = 0.7f;
            var fakeTiles = new List<Tile>();
            Vector3 center = ScreenCenterWorld();
            for (int i = 0; i < FAKE_TILE_COUNT; i++)
            {
                var go = new GameObject($"FXTest_MeltdownTile_{i}");
                go.transform.position = new Vector3(
                    center.x + (i - FAKE_TILE_COUNT * 0.5f + 0.5f) * SPACING,
                    center.y, 0f);
                fakeTiles.Add(go.AddComponent<Tile>());
                Destroy(go, 6f); // safety cleanup
            }

            // 1. Intro — title stamp appears, screen flash, MeltdownManager
            //    flips its _isPlaying flag so PlayExplosion's meltdown gate
            //    inside ExplosionCoroutine fires the prefab + tile punch.
            //    chainDepth=4 lands on "MELTDOWN" tier (5+ = AFTERSHOCK,
            //    2-3 = CHAIN REACTION).
            Coroutine intro = MeltdownManager.Instance.TryMeltdownIntro(
                chainDepth: 4, triggerCount: 8, detonationBonus: 200, isLastTurn: false);
            if (intro != null) yield return intro;

            // 2. Explosion stacked WHILE the title stamp is still on screen —
            //    this is the actual "meltdown moment." chainStep=3 = tier 4.
            WordDropFX.Instance.PlayExplosion(fakeTiles, chainStep: 3, wordLength: FAKE_TILE_COUNT);

            // 3. Hold so the explosion's full lifecycle (pops, fragments,
            //    bigburst, shake) plays out under the title stamp before
            //    the outro fades it.
            yield return new WaitForSeconds(1.6f);

            // 4. Outro — title fades, MeltdownManager clears _isPlaying so
            //    subsequent re-fires aren't blocked by the IsActive guard.
            MeltdownManager.Instance.TryMeltdownOutro();

            WordDropFX.FX_MeltdownPrefab      = savedPrefab;
            WordDropFX.FX_MeltdownIntroFlash  = savedIntroFlash;
            WordDropFX.FX_MeltdownTilePunch   = savedTilePunch;
            WordDropFX.FX_MeltdownWindupShake = savedWindup;
        }

        /// <summary>
        /// Plays JUST the climactic blast moment of the meltdown — no intro
        /// title, no flash, no windup, no earthquake rumble. The Magic
        /// Explosive Spell prefab is spawned per tile but fast-forwarded via
        /// ParticleSystem.Simulate past its ~1.7s windup so it spawns
        /// VISIBLE at its blast peak. Tier-4 pops + fragments + detonation
        /// sound fire simultaneously (meltdownActive stays false so
        /// PlayExplosion takes the non-meltdown path with no internal
        /// windup wait). The "test the climax as a standalone animation"
        /// button per Spencer's 2026-05-30 ask.
        /// </summary>
        private void FireMeltdownBlastOnly()
        {
            if (WordDropFX.Instance == null) { Debug.LogWarning("[FXTest] WordDropFX missing"); return; }
            StartCoroutine(MeltdownBlastOnlyCoroutine());
        }

        private IEnumerator MeltdownBlastOnlyCoroutine()
        {
            // Build fake tiles in a horizontal row at screen center.
            const int FAKE_TILE_COUNT = 15;
            const float SPACING = 0.7f;
            var fakeTiles = new List<Tile>();
            Vector3 center = ScreenCenterWorld();
            for (int i = 0; i < FAKE_TILE_COUNT; i++)
            {
                var go = new GameObject($"FXTest_BlastTile_{i}");
                go.transform.position = new Vector3(
                    center.x + (i - FAKE_TILE_COUNT * 0.5f + 0.5f) * SPACING,
                    center.y, 0f);
                fakeTiles.Add(go.AddComponent<Tile>());
                Destroy(go, 6f);
            }

            // Spawn the Magic Explosive Spell prefab per tile, fast-forwarded
            // past its windup via ParticleSystem.Simulate so it appears
            // already at its blast peak.
            GameObject magicPrefab = Resources.Load<GameObject>("Prefabs/FX/Magic Explosive Spell");
            if (magicPrefab != null)
            {
                float fastForwardTime =
                    FlipbookExplosion.MELTDOWN_BLAST_PEAK_AT_REAL_SPEED
                    / FlipbookExplosion.MELTDOWN_PREFAB_SPEED;

                for (int i = 0; i < fakeTiles.Count; i++)
                {
                    if (fakeTiles[i] == null) continue;
                    GameObject inst = Instantiate(magicPrefab, fakeTiles[i].transform.position, Quaternion.identity);

                    // Strip AllIn1 demo shakers — they reference an AllIn1Shaker
                    // singleton that doesn't exist in our scene and would error.
                    // Same cleanup FlipbookExplosion.PlayMeltdown does.
                    var demoShakers = inst.GetComponentsInChildren<AllIn1VfxToolkit.Demo.Scripts.AllIn1DoShake>(true);
                    for (int s = 0; s < demoShakers.Length; s++)
                        if (demoShakers[s] != null) Destroy(demoShakers[s]);

                    // Fast-forward all ParticleSystems past the windup so they
                    // become visible AT the blast peak. Simulate(time, false,
                    // true) advances each system by `time` seconds with restart.
                    // Then Play() to resume natural playback from that point.
                    var systems = inst.GetComponentsInChildren<ParticleSystem>(true);
                    for (int j = 0; j < systems.Length; j++)
                    {
                        var ps = systems[j];
                        if (ps == null) continue;
                        var main = ps.main;
                        main.playOnAwake = false;
                        ps.Simulate(fastForwardTime, false, true);
                        ps.Play(false);
                    }

                    Destroy(inst, 3f);
                }
            }

            // Fire tier-4 pops + fragments + BigBurst + detonation audio
            // immediately. _isPlaying stays FALSE so PlayExplosion takes the
            // non-meltdown path (no prefab spawn since we did it manually
            // above, no 1.7s WINDUP_DELAY wait, no earthquake rumble).
            WordDropFX.Instance.PlayExplosion(fakeTiles, chainStep: 3, wordLength: FAKE_TILE_COUNT);

            yield return new WaitForSeconds(2.5f);
        }

        /// <summary>
        /// Same as FireMeltdownBlastOnly but fires on REAL live board tiles
        /// instead of fake center-screen dummies — shows how the meltdown
        /// blast moment reads against the actual populated grid. Tiles are
        /// re-activated + restored to their original position/scale after
        /// the FX so RulesEngine + MatchController state stays clean.
        /// </summary>
        private void FireMeltdownBlastOnRealTiles()
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            // Grab up to 12 live board tiles — same picking pattern as
            // DetonateRealBoardTiles (top-down, left-to-right).
            var tiles = new List<Tile>();
            for (int row = 0; row < RulesEngine.ROWS && tiles.Count < 12; row++)
                for (int col = 0; col < RulesEngine.COLS && tiles.Count < 12; col++)
                {
                    Tile t = GridManager.Instance.GetTile(col, row);
                    if (t != null && t.gameObject.activeSelf) tiles.Add(t);
                }

            if (tiles.Count == 0)
            {
                Debug.LogWarning("[FXTest] No live tiles on the board — drop a few first.");
                return;
            }

            StartCoroutine(MeltdownBlastOnRealTilesCoroutine(tiles));
        }

        private IEnumerator MeltdownBlastOnRealTilesCoroutine(List<Tile> tiles)
        {
            // Cache pre-FX state so we can restore tiles after PlayExplosion
            // disables them. Same restore pattern RealDetonationCoroutine uses.
            var positions = new List<Vector3>(tiles.Count);
            var scales    = new List<Vector3>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                positions.Add(tiles[i].transform.position);
                scales.Add(tiles[i].transform.localScale);
            }

            // Spawn the Magic Explosive Spell prefab on each REAL tile,
            // fast-forwarded past its ~1.7s windup via ParticleSystem.Simulate
            // so it spawns visible AT its blast peak.
            GameObject magicPrefab = Resources.Load<GameObject>("Prefabs/FX/Magic Explosive Spell");
            if (magicPrefab != null)
            {
                float fastForwardTime =
                    FlipbookExplosion.MELTDOWN_BLAST_PEAK_AT_REAL_SPEED
                    / FlipbookExplosion.MELTDOWN_PREFAB_SPEED;

                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    GameObject inst = Instantiate(magicPrefab, tiles[i].transform.position, Quaternion.identity);

                    // Strip AllIn1 demo shakers — they reference a singleton
                    // that doesn't exist in our scene. Same cleanup
                    // FlipbookExplosion.PlayMeltdown does.
                    var demoShakers = inst.GetComponentsInChildren<AllIn1VfxToolkit.Demo.Scripts.AllIn1DoShake>(true);
                    for (int s = 0; s < demoShakers.Length; s++)
                        if (demoShakers[s] != null) Destroy(demoShakers[s]);

                    // Fast-forward all ParticleSystems past the windup phase.
                    var systems = inst.GetComponentsInChildren<ParticleSystem>(true);
                    for (int j = 0; j < systems.Length; j++)
                    {
                        var ps = systems[j];
                        if (ps == null) continue;
                        var main = ps.main;
                        main.playOnAwake = false;
                        ps.Simulate(fastForwardTime, false, true);
                        ps.Play(false);
                    }

                    Destroy(inst, 3f);
                }
            }

            // Fire tier-4 pops + fragments + BigBurst + detonation audio
            // immediately on the real tiles. _isPlaying stays FALSE so
            // PlayExplosion takes the non-meltdown path — no prefab spawn
            // (we did it manually fast-forwarded), no internal windup wait,
            // no earthquake rumble. Just the blast moment.
            yield return WordDropFX.Instance.PlayExplosion(tiles, chainStep: 3, wordLength: tiles.Count);

            // Hold briefly for the prefab tail to settle before restoring tiles.
            yield return new WaitForSeconds(0.6f);

            // Restore tiles to their pre-explosion state — same pattern as
            // RealDetonationCoroutine. Re-activate, kill leftover tweens,
            // snap transforms back. Keeps RulesEngine/MatchController state
            // consistent so subsequent play continues normally.
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                tiles[i].gameObject.SetActive(true);
                tiles[i].transform.DOKill();
                tiles[i].transform.position    = positions[i];
                tiles[i].transform.localScale  = scales[i];
                tiles[i].transform.localRotation = Quaternion.identity;
            }
        }

        private IEnumerator MeltdownIntroOnlyCoroutine()
        {
            // Flip FX_MeltdownIntroFlash on for the duration so the intro
            // doesn't return null at its own gate, then restore.
            bool saved = WordDropFX.FX_MeltdownIntroFlash;
            WordDropFX.FX_MeltdownIntroFlash = true;

            // High thresholds so GetMeltdownTitle returns a non-null title
            // (chainDepth >= 3 + triggerCount >= 3 lands on the top tier).
            Coroutine intro = MeltdownManager.Instance.TryMeltdownIntro(
                chainDepth: 4, triggerCount: 4, detonationBonus: 200, isLastTurn: false);

            if (intro != null) yield return intro;

            // Hold the stamp briefly so Spencer can read the title clearly.
            yield return new WaitForSeconds(1.0f);

            // Cleanup — Outro fades the overlay + clears _isPlaying so
            // subsequent test fires aren't blocked by the IsActive guard.
            MeltdownManager.Instance.TryMeltdownOutro();

            WordDropFX.FX_MeltdownIntroFlash = saved;
        }

        // ── Real-tile detonation — picks live tiles from the grid, runs FX on
        // them, then reactivates them so RulesEngine/MatchController state
        // stays correct. Caller doesn't need a primed word, edits, or chain.
        private void DetonateRealBoardTiles(int count, int chainStep, bool forceMeltdown)
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            var tiles = new List<Tile>();
            for (int row = 0; row < RulesEngine.ROWS && tiles.Count < count; row++)
                for (int col = 0; col < RulesEngine.COLS && tiles.Count < count; col++)
                {
                    Tile t = GridManager.Instance.GetTile(col, row);
                    if (t != null && t.gameObject.activeSelf) tiles.Add(t);
                }

            if (tiles.Count == 0)
            {
                Debug.LogWarning("[FXTest] No live tiles on the board — drop a few first or run Tier-buttons instead");
                return;
            }

            // No real drop happens in a test detonation, so pin the trigger cell to
            // the top-left-most tile of the set (clearly OFF the cluster centroid).
            // That makes the tier-3 burst deterministic AND lets you verify it's
            // honoring LastTriggerCell — if the glow lands here, not the middle, it works.
            WordDropFX.LastTriggerCell = new Vector2Int(tiles[0].Col, tiles[0].Row);
            WDLog.Log($"[FXTest] LastTriggerCell pinned to ({tiles[0].Col},{tiles[0].Row}) for this test detonation");

            // big_pop + anticipation hold (self-gates: original explosion
            // chainStep < 2 with 8+ tiles fires; cascade/meltdown buttons pass
            // chainStep >= 2 and stay silent), then run the real detonation.
            StartCoroutine(RealBigPopThenDetonate(tiles, chainStep, forceMeltdown));
        }

        private System.Collections.IEnumerator RealBigPopThenDetonate(List<Tile> tiles, int chainStep, bool forceMeltdown)
        {
            yield return WordDropFX.MaybeBigPopAndHold(tiles);
            yield return StartCoroutine(RealDetonationCoroutine(tiles, chainStep, forceMeltdown));
        }

        // Isolated tier-3 energy burst (glow + rays + flash) at the board's center
        // tile — NO detonation, NO meltdown, NO tile destruction. Pure look-test for
        // the tier-3 layer so you can see exactly where/how the glow reads without
        // the 8+ meltdown stack on top of it.
        private void FireTier3BurstOnly()
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            int cCol = RulesEngine.COLS / 2;
            int cRow = RulesEngine.ROWS / 2;
            Vector3 center = GridManager.Instance.CellToWorld(cCol, cRow);
            float cell = GridManager.Instance.CellSize;
            float span = cell * 3f; // ~3-tile-wide burst

            WDLog.Log($"[FXTest] Tier-3 BURST ONLY @ ({cCol},{cRow}) world={center} span={span:F2} scale={WordDropFX.Tier3BurstScale:F2}");
            WordDropFX.Instance.PlayTier3Burst(center, span);
        }

        // ── Tier-1 Candy-Crush-style pop test fire ───────────────────────────
        // Picks a single live tile from the board, plays the new
        // PlayTier1Pop orchestrated sequence on it, then restores the tile
        // (re-activate, snap localScale + position + alpha) so RulesEngine
        // / MatchController state stays clean.
        private void FireTier1Pop()
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            Tile target = null;
            for (int row = 0; row < RulesEngine.ROWS && target == null; row++)
                for (int col = 0; col < RulesEngine.COLS && target == null; col++)
                {
                    Tile t = GridManager.Instance.GetTile(col, row);
                    if (t != null && t.gameObject.activeSelf) target = t;
                }

            if (target == null)
            {
                Debug.LogWarning("[FXTest] Tier 1 Pop — no live tiles on the board; drop a few first.");
                return;
            }

            StartCoroutine(Tier1PopRestoreCoroutine(target));
        }

        private IEnumerator Tier1PopRestoreCoroutine(Tile tile)
        {
            // Cache pre-pop state BEFORE PlayTier1Pop mutates scale/alpha/visibility.
            Vector3 origPos = tile.transform.position;
            Vector3 origScale = tile.transform.localScale;
            SpriteRenderer sr = tile.GetComponent<SpriteRenderer>();
            Color origColor = sr != null ? sr.color : Color.white;

            WordDropFX.Instance.PlayTier1Pop(tile);

            // Hold long enough for the full sequence (~350ms) plus a tail
            // for fragments / sparkle to clear before restoring.
            yield return new WaitForSeconds(0.5f);

            if (tile == null) yield break;

            // Restore: kill any leftover tweens, snap transform back, re-show.
            tile.transform.DOKill();
            if (sr != null) sr.DOKill();
            tile.gameObject.SetActive(true);
            tile.transform.position    = origPos;
            tile.transform.localScale  = origScale;
            tile.transform.localRotation = Quaternion.identity;
            if (sr != null) sr.color = new Color(origColor.r, origColor.g, origColor.b, 1f);
        }

        private IEnumerator RealDetonationCoroutine(List<Tile> tiles, int chainStep, bool forceMeltdown)
        {
            FieldInfo isPlayingField = forceMeltdown
                ? typeof(MeltdownManager).GetField("_isPlaying", BindingFlags.NonPublic | BindingFlags.Instance)
                : null;
            if (forceMeltdown && isPlayingField != null && MeltdownManager.Instance != null)
                isPlayingField.SetValue(MeltdownManager.Instance, true);

            // Force the meltdown FX toggles on for the test — the user might
            // not have flipped them in the panel. Save and restore so the
            // global state doesn't leak across test fires.
            bool savedMeltdownPrefab    = WordDropFX.FX_MeltdownPrefab;
            bool savedTileHeatOverlay   = WordDropFX.FX_TileHeatOverlay;
            bool savedMeltdownTilePunch = WordDropFX.FX_MeltdownTilePunch;
            bool savedPrimedGlowOrb     = WordDropFX.FX_PrimedGlowOrb;
            bool savedMeltdownWindupShake = WordDropFX.FX_MeltdownWindupShake;
            if (forceMeltdown)
            {
                WordDropFX.FX_MeltdownPrefab    = true;
                WordDropFX.FX_TileHeatOverlay   = true;
                WordDropFX.FX_MeltdownTilePunch = true;
                WordDropFX.FX_PrimedGlowOrb     = true;
                WordDropFX.FX_MeltdownWindupShake = true;
                WDLog.Log("[FXTest] Forced FX_MeltdownPrefab + FX_TileHeatOverlay + FX_MeltdownTilePunch + FX_PrimedGlowOrb + FX_MeltdownWindupShake ON for the duration of this test fire");
            }

            // Cache positions AND scales so we can re-place tiles after
            // PlayExplosion's SetActive(false) at end of its loop.
            // PlayDetonation's squeeze→pop→settle tween can leave the tile
            // mid-tween if SetActive interrupts it, so the cached scale is
            // what we restore when re-activating.
            var positions = new List<Vector3>(tiles.Count);
            var scales    = new List<Vector3>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++)
            {
                positions.Add(tiles[i].transform.position);
                scales.Add(tiles[i].transform.localScale);
            }

            yield return WordDropFX.Instance.PlayExplosion(tiles, chainStep, tiles.Count);

            // Hold a beat for the prefab tail / fragments to settle.
            yield return new WaitForSeconds(0.6f);

            // Restore tiles — re-activate the GameObjects, kill any tweens
            // still attached to the transform, snap position + scale back
            // to the pre-explosion values.
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                tiles[i].gameObject.SetActive(true);
                tiles[i].transform.DOKill(); // stops any leftover squeeze/pop tween
                tiles[i].transform.position    = positions[i];
                tiles[i].transform.localScale  = scales[i];
                tiles[i].transform.localRotation = Quaternion.identity;
            }

            if (forceMeltdown && isPlayingField != null && MeltdownManager.Instance != null)
                isPlayingField.SetValue(MeltdownManager.Instance, false);

            // Restore the toggles we forced on at the start so global state
            // doesn't leak past this test fire.
            if (forceMeltdown)
            {
                WordDropFX.FX_MeltdownPrefab    = savedMeltdownPrefab;
                WordDropFX.FX_TileHeatOverlay   = savedTileHeatOverlay;
                WordDropFX.FX_MeltdownTilePunch = savedMeltdownTilePunch;
                WordDropFX.FX_PrimedGlowOrb     = savedPrimedGlowOrb;
                WordDropFX.FX_MeltdownWindupShake = savedMeltdownWindupShake;
            }
        }

        private IEnumerator ForcedMeltdownDetonation(List<Tile> fake, int chainStep, int wordLen)
        {
            // Flip MeltdownManager.IsActive for the duration of the explosion so
            // ExplosionCoroutine's meltdown gate fires the prefab. Use reflection
            // to write the private _isPlaying field; avoids adding a public test
            // setter to production code.
            FieldInfo f = typeof(MeltdownManager).GetField("_isPlaying", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && MeltdownManager.Instance != null)
            {
                f.SetValue(MeltdownManager.Instance, true);
            }

            WordDropFX.Instance.PlayExplosion(fake, chainStep, wordLen);

            // Hold true for ~3s so the prefab's full lifecycle plays under the
            // active flag, then release.
            yield return new WaitForSeconds(3f);
            if (f != null && MeltdownManager.Instance != null)
            {
                f.SetValue(MeltdownManager.Instance, false);
            }
        }

        // ── Styles ────────────────────────────────────────────────────────────────

        private GUIStyle _panelStyle;
        private static Texture2D SolidTex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }
        private void EnsureStyles()
        {
            if (_btnStyle != null) return;
            // SOLID, NOT SEE-THROUGH (2026-10-23 Spencer: "make it so theres no opacity to it. its really hard for me to read").
            // The default IMGUI skin is translucent; every background here is an opaque 1x1 texture with white text.
            var btnN = SolidTex(new Color32(0x2E, 0x34, 0x46, 0xFF));
            var btnH = SolidTex(new Color32(0x3E, 0x47, 0x60, 0xFF));
            var btnA = SolidTex(new Color32(0x50, 0x5C, 0x7C, 0xFF));
            _btnStyle    = new GUIStyle(GUI.skin.button) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 6, 6) };
            _btnStyle.normal.background = btnN;  _btnStyle.normal.textColor = Color.white;
            _btnStyle.hover.background  = btnH;  _btnStyle.hover.textColor  = Color.white;
            _btnStyle.active.background = btnA;  _btnStyle.active.textColor = Color.white;
            _btnStyle.focused.background = btnN; _btnStyle.focused.textColor = Color.white;
            _btnStyle.border = new RectOffset(0, 0, 0, 0);
            _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 16, padding = new RectOffset(6, 6, 4, 4) };
            _toggleStyle.normal.textColor = Color.white; _toggleStyle.onNormal.textColor = Color.white;
            _toggleStyle.hover.textColor = Color.white;  _toggleStyle.onHover.textColor = Color.white;
            _headerStyle = new GUIStyle(GUI.skin.label)  { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.7f, 0.9f, 1f) } };
            _tfStyle     = new GUIStyle(GUI.skin.textField) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
            _tfStyle.normal.background = SolidTex(new Color32(0x12, 0x14, 0x1C, 0xFF)); _tfStyle.normal.textColor = Color.white;
            _tfStyle.focused.background = _tfStyle.normal.background; _tfStyle.focused.textColor = Color.white;
            _panelStyle  = new GUIStyle(GUI.skin.box);
            _panelStyle.normal.background = SolidTex(new Color32(0x16, 0x19, 0x22, 0xFF));
        }
    
        /// <summary>Put two specials side by side near the middle and swap them into a combo (no capture — keeps it smooth).</summary>
        private System.Collections.IEnumerator TestCombo(int kindA, int kindB)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null || HandManager.Instance == null) yield break;
            Vector2Int a = new Vector2Int(-1, -1), b = a;
            for (int r = 3; r < RulesEngine.ROWS - 2 && a.x < 0; r++)
                for (int c = 2; c < RulesEngine.COLS - 3 && a.x < 0; c++)
                {
                    var c1 = rules.GetCell(c, r); var c2 = rules.GetCell(c + 1, r);
                    if (c1 == null || c2 == null || c1.IsFrozen || c2.IsFrozen || c1.IsStone || c2.IsStone) continue;
                    if (rules.PrimedRegistry.GetPrimedWordsContaining(new Vector2Int(c, r)).Count > 0) continue;
                    a = new Vector2Int(c, r); b = new Vector2Int(c + 1, r);
                }
            if (a.x < 0) { WDLog.Log("[Combo] TEST: no free pair"); yield break; }
            rules.GetCell(a.x, a.y).SpecialKind = kindA; grid.GetTile(a.x, a.y)?.SetSpecial(kindA, pop: true);
            rules.GetCell(b.x, b.y).SpecialKind = kindB; grid.GetTile(b.x, b.y)?.SetSpecial(kindB, pop: true);
            yield return new WaitForSeconds(0.8f);
            HandManager.Instance.TestCombo(a, b);
        }

        private System.Collections.IEnumerator TestSpecial(int kind)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            if (rules == null || grid == null || HandManager.Instance == null) yield break;
            // A tile near the middle: centre column, lowest few rows outward, skipping ice / armed / special tiles.
            Vector2Int pick = new Vector2Int(-1, -1);
            int mid = RulesEngine.COLS / 2;
            for (int r = 1; r < RulesEngine.ROWS && pick.x < 0; r++)
                for (int d = 0; d <= mid && pick.x < 0; d++)
                    foreach (int c in new[] { mid - d, mid + d })
                    {
                        if (c < 0 || c >= RulesEngine.COLS || pick.x >= 0) continue;
                        var cd = rules.GetCell(c, r);
                        if (cd != null && !cd.IsFrozen && cd.SpecialKind == RulesEngine.SPECIAL_NONE
                            && rules.PrimedRegistry.GetPrimedWordsContaining(new Vector2Int(c, r)).Count == 0)
                            pick = new Vector2Int(c, r);
                    }
            if (pick.x < 0) { WDLog.Log("[Special] TEST: no free tile to place a special on"); yield break; }
            // Record it automatically — a 2.4s burst covering the sticker pop AND the fire, so nobody has to time F9.
            CaptureRig.Capture("special_" + RulesEngine.SpecialName(kind).ToLowerInvariant());
            rules.GetCell(pick.x, pick.y).SpecialKind = kind;
            grid.GetTile(pick.x, pick.y)?.SetSpecial(kind, pop: true);
            WDLog.Log($"[Special] TEST placed {RulesEngine.SpecialName(kind)} at ({pick.x},{pick.y})");
            yield return new WaitForSeconds(0.8f);   // see the sticker before it goes
            if (!HandManager.Instance.FireSpecialAt(pick)) WDLog.Log("[Special] TEST fire refused (board busy?) — the special stays on the tile");
        }
}
}
#endif
