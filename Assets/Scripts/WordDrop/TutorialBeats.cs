using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// TUTORIAL BEATS (2026-10-23, rebuilt from scratch — the June tutorial was a drop-game system). Royal Match's pattern from
    /// Spencer's recording: each teaching level gets ONE forced move — board dimmed except the cells involved, a one-line
    /// bubble, the hand looping the move — then free play. No text walls, no separate tutorial mode.
    ///
    /// Which level teaches what = LevelRules.Teach (the TABLE_NEW rows). Built so far: "swap" (L1). Rocket / bomb / disco /
    /// airplane need the board RIGGED with the right near-word first; crates / ice / theme / goals / bonus get a tap-to-continue
    /// note. Each beat runs once per install (PlayerPrefs "tut2_&lt;key&gt;"; FX menu TUTORIAL RESET clears them).
    ///
    /// INPUT while a forced beat is up: only the taught swap goes through (HandManager.TryBoardSwap asks AllowSwap; anything
    /// else gets the normal refused-swap bounce); swipes, boosters and idle hints stand down (BlocksInput).
    /// Visuals: TutorialOverlay. NEW_TUTORIAL = false switches all of it off.
    /// </summary>
    public class TutorialBeats : MonoBehaviour
    {
        public static bool NEW_TUTORIAL = true;
        public static float SETTLE_BEAT = 0.6f;   // seconds between the board settling and the lesson dimming in
        public static TutorialBeats Instance { get; private set; }

        /// <summary>A forced beat is up: only the taught swap is allowed.</summary>
        public static bool BlocksInput => s_active;
        private static bool s_active;
        private static Vector2Int s_allowA, s_allowB;
        private static string s_key;
        private float _tipUntil = -1f;
        private float _tapCloseAfter = -1f;   // 11-06 show-and-tell tips (shelf / safe): a tap after this time closes the tip

        private const string PREF = "tut2_";
        public static bool Done(string key) => PlayerPrefs.GetInt(PREF + key, 0) == 1;
        private static void MarkDone(string key) { PlayerPrefs.SetInt(PREF + key, 1); PlayerPrefs.Save(); }
        public static readonly string[] KEYS = { "swap", "gem", "swipe", "goals", "rocket", "bomb", "disco", "crates", "airplane", "ice", "theme", "boosters", "bonus", "shelf", "safe", "use", "move", "hint", "wild" };
        public static void ResetAll() { foreach (var k in KEYS) PlayerPrefs.DeleteKey(PREF + k); MechanicSplash.ResetAll(); BoosterIntro.ResetAll(); PlayerPrefs.Save();   /* 11-06: booster intros too - they never replayed after a reset */ WDLog.Log("[Tut] all beats + splashes reset"); }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("TutorialBeats"); DontDestroyOnLoad(go);
            Instance = go.AddComponent<TutorialBeats>();
            LevelIntroModal.OnPlayStarted += () => { if (Instance != null) Instance.OnPlayStarted(); };
        }

        private void OnPlayStarted()
        {
            if (!NEW_TUTORIAL || !SurvivalManager.FullBoardActive) return;
            StopAllCoroutines();
            Cancel();
            StartCoroutine(RunForLevelLocked());
        }

        // ── 11-17 NO MOVES BEFORE THE LESSON (monkey 98181: it spelled RESHOT in the ~1.5 s between the board appearing and the
        // lesson dimming it, got a Royal Orb, and later used it to wreck the lesson's word). On a level whose forced lesson has
        // not been seen yet the board takes no press from PLAY until that lesson is on screen (or decides not to run). Booster
        // lessons have their own lock. Self-clears after PENDING_MAX so it can never leave the board dead. ──
        public static float PENDING_MAX = 12f;
        private static float s_pendingSince = -1f;
        public static bool Pending => s_pendingSince >= 0f && Time.unscaledTime - s_pendingSince < PENDING_MAX;
        private IEnumerator RunForLevelLocked()
        {
            s_pendingSince = -1f;
            var sm = SurvivalManager.Instance;
            if (sm != null)
            {
                string k = KeyFor(LevelRules.For(LevelMapPanel.RunLevel(sm.CurrentStageIndex)));
                if ((k == "swap" || k == "swipe" || (POWER_BEATS && IsPowerKey(k))) && !Done(k)) s_pendingSince = Time.unscaledTime;
            }
            yield return RunForLevel();
            s_pendingSince = -1f;
        }

        private static bool BoardIdle()
        {
            var mc = MatchController.Instance; var hm = HandManager.Instance;
            return (mc == null || !mc.IsProcessing) && (hm == null || (hm.IsInteractable && !hm.IsResolvingChain));
        }

        private IEnumerator RunForLevel()
        {
            yield return new WaitForSeconds(0.6f);   // the board's entry animation
            float waited = 0f;
            // idle AND the board finished sliding in (v1 measured mid-slide and the bubble landed off-screen right)
            while ((!BoardIdle() || (GridManager.Instance != null && Mathf.Abs(GridManager.Instance.BoardSlideOffsetX) > 0.01f)) && waited < 6f)
            { waited += Time.deltaTime; yield return null; }
            // A BEAT after the board lands (2026-10-23 Spencer: "it should wait just a short beat before it dims ... right now its
            // very abrupt") — let the player see the board settle before the lesson takes over.
            yield return new WaitForSeconds(SETTLE_BEAT);
            // 11-06 (Spencer's L19 recording: the crate lesson and the axe lesson ran AT ONCE - two dims, the axe stayed dark): a booster
            // unlock lesson goes first; the board lesson waits for it to finish
            { float bw = 0f; while ((BoosterIntro.Running || BoosterIntro.Pending) && bw < 60f) { bw += Time.unscaledDeltaTime; yield return null; } }
            var sm = SurvivalManager.Instance; if (sm == null) yield break;
            int stage = LevelMapPanel.RunLevel(sm.CurrentStageIndex);
            string key = KeyFor(LevelRules.For(stage));
            if (string.IsNullOrEmpty(key) || Done(key)) yield break;
            WDLog.Log($"[Tut] LEVEL {stage}: beat '{key}'");
            switch (key)
            {
                case "swap": BeatSwap(); break;
                case "swipe": BeatSwipe(); break;
                case "crates": case "ice": BeatObstacle(key); break;
                case "shelf": case "safe": BeatBigPiece(key); break;
                case "rocket": case "bomb": case "disco": case "airplane": BeatPower(key); break;
                default: WDLog.Log($"[Tut] beat '{key}' not built yet"); break;
            }
        }

        // ── POWER-UPS (11-17, Spencer: "follow exactly how royal match rolls it out" - one forced move that MAKES the power-up,
        // on the level that teaches it; the splash before the level stays). The level build planted the recipe one swap away
        // (RulesEngine.PlantPowerLesson). No plant / the board changed -> skipped and not marked done (it tries again next time).
        // POWER_BEATS = false: back to splash only. ──
        public static bool POWER_BEATS = true;
        public static bool IsPowerKey(string k) => k == "rocket" || k == "bomb" || k == "disco" || k == "airplane";
        private void BeatPower(string key)
        {
            var rules = RulesEngine.Instance; if (rules == null) return;
            if (!rules.PowerLessonStillThere(key)) { WDLog.Log($"[Tut] {key} beat: the planted move is not on this board - skipped (not marked done)"); return; }
            Vector2Int a = RulesEngine.TeachFrom, b = RulesEngine.TeachTo;
            var window = new List<Vector2Int>(RulesEngine.TeachCells);
            if (RulesEngine.TeachExtraCells != null) foreach (var c in RulesEngine.TeachExtraCells) if (!window.Contains(c)) window.Add(c);
            if (!window.Contains(a)) window.Add(a);
            if (!window.Contains(b)) window.Add(b);
            string w = RulesEngine.TeachWord;
            string text = key == "rocket"   ? $"Spell {w} to make a ROCKET!"
                        : key == "disco"    ? $"Spell {w} to make a ROYAL ORB!"
                        : key == "bomb"     ? "Cross two words to make a BOMB!"
                        :                     "Make two words for a FLYING TOME!";
            BeginForced(key, a, b);
            TutorialOverlay.Ensure().Show(window, text, a, b);   // the hand drags the waiting letter (TeachFrom) into its place
            WDLog.Log($"[Tut] beat '{key}' shows '{w}'{(RulesEngine.TeachWord2 != null ? " + '" + RulesEngine.TeachWord2 + "'" : "")} ({a.x},{a.y})<->({b.x},{b.y})");
        }

        // ── L1: SWAP ──────────────────────────────────────────────────────────────────────────────────────────────
        private void BeatSwap()
        {
            var rules = RulesEngine.Instance; if (rules == null) return;
            // Own picker, not the best-hint: one forward 4-5 letter common word (v1 showed ERASES spelled backwards).
            Vector2Int a, b; List<Vector2Int> wordCells; string word;
            if (rules.TeachSwapStillThere()) { a = RulesEngine.TeachFrom; b = RulesEngine.TeachTo; wordCells = RulesEngine.TeachCells; word = RulesEngine.TeachWord; }   // 11-02 planted
            else if (!rules.FindTutorialSwap(out a, out b, out wordCells, out word) || wordCells == null)
            { WDLog.Log("[Tut] swap beat: no move on this board — skipped (not marked done)"); return; }
            // The word runs through the cell a letter moves INTO; the hand drags the OTHER tile in (same rule as the hint).
            bool aIn = wordCells.Contains(a), bIn = wordCells.Contains(b);
            Vector2Int from = (aIn && !bIn) ? b : a, to = (aIn && !bIn) ? a : b;
            var window = new List<Vector2Int>(wordCells);
            if (!window.Contains(a)) window.Add(a);
            if (!window.Contains(b)) window.Add(b);
            // 11-20 MOVE BEAT (tiles stay; three testers "didn't know what to do"): the first guided move makes NO word - the
            // lesson letter starts one cell further away and the player slides it next to the word. Then this beat runs again
            // ("Slide again to spell X!"). No safe spot to park the letter -> the one-move lesson as before.
            if (MOVE_BEAT && !HandManager.BOUNCE_NO_WORD && !Done("move") && TryParkTeachLetter(rules, from, to, wordCells, out var parked))
            {
                var w2 = new List<Vector2Int>(window); if (!w2.Contains(parked)) w2.Add(parked);
                BeginForced("move", parked, from);
                TutorialOverlay.Ensure().Show(w2, "Slide a letter to move it!", parked, from);
                WDLog.Log($"[Tut] LEVEL 1: beat 'move' ({parked.x},{parked.y})->({from.x},{from.y}), then '{word}'");
                return;
            }
            BeginForced("swap", a, b);
            TutorialOverlay.Ensure().Show(window, s_afterMove ? $"Slide again to spell {word}!" : $"Slide to spell {word}!"   /* 11-06 balanced lines: 472 / WORD / 501 */, from, to);
            s_afterMove = false;
        }

        // ── 11-20 HINT LESSON (Spencer's boss played L1 without knowing the leaning tile was a hint): the FIRST time a free hint
        // leans, the board dims around it with one line. Only that swap goes through; the tile keeps leaning. Once per install
        // (key "hint"). The usual watchdog releases it if the board changes under it. HINT_BEAT = false: off. ──
        public static bool HINT_BEAT = true;
        public static bool HintBeatUp => s_active && s_key == "hint";
        public static void OnFreeHintShown(Vector2Int a, Vector2Int b, List<Vector2Int> cells, bool moverIsB)
        {
            if (!NEW_TUTORIAL || !HINT_BEAT || Instance == null || s_active || Pending || Done("hint")) return;
            if (Instance._tipUntil > 0f || Instance._useUntil > 0f || !SurvivalManager.FullBoardActive || SurvivalManager.AwaitingNextLevel) return;
            if (BoosterIntro.Running || BoosterIntro.Pending || cells == null) return;
            var window = new List<Vector2Int>(cells);
            if (!window.Contains(a)) window.Add(a);
            if (!window.Contains(b)) window.Add(b);
            s_active = true; s_key = "hint"; s_allowA = a; s_allowB = b;   // not BeginForced: that resets the idle timer and the hint would stop
            TutorialOverlay.Ensure().Show(window, "Stuck? Follow the nudge!", moverIsB ? b : a, moverIsB ? a : b);
            WDLog.Log($"[Tut] beat 'hint' on ({a.x},{a.y})<->({b.x},{b.y})");
        }

        // ── 11-20 WILD LESSON (testers: "didnt know til level 7 that you can move the power ups"; "I end up just moving the
        // powerups"). Once the rocket lessons are done, the first time a power-up sits one slide away from a word that runs
        // through the cell it moves INTO, that slide becomes a forced beat. Checked twice a second on an idle board, twice in a
        // row before it shows. Dynamite is skipped (it cannot be moved). Once per install (key "wild"). WILD_BEAT = false: off. ──
        public static bool WILD_BEAT = true;
        private float _wildAt; private int _wildSeen; private Vector2Int _wildA, _wildB;
        private void WatchForWildLesson()
        {
            if (!WILD_BEAT || !NEW_TUTORIAL || Time.unscaledTime < _wildAt) return;
            _wildAt = Time.unscaledTime + 0.5f;
            if (Done("wild") || !Done("use") || s_active || Pending || _tipUntil > 0f || !SurvivalManager.FullBoardActive || SurvivalManager.AwaitingNextLevel
                || BoosterIntro.Running || BoosterIntro.Pending || !BoardIdle() || (LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard)) { _wildSeen = 0; return; }
            var sm = SurvivalManager.Instance; var rules = RulesEngine.Instance;
            if (sm == null || rules == null || sm.IsOverlayPaused || LevelRules.For(LevelMapPanel.RunLevel(sm.CurrentStageIndex)).Bonus) { _wildSeen = 0; return; }
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 0; y < RulesEngine.LEVEL_ROWS; y++)
                {
                    var c = rules.GetCell(x, y);
                    if (c == null || c.SpecialKind == RulesEngine.SPECIAL_NONE || c.SpecialKind == RulesEngine.SPECIAL_TNT || c.IsFrozen) continue;
                    var a = new Vector2Int(x, y);
                    foreach (var d in s_dirs4)
                    {
                        var b = a + d; var cb = rules.GetCell(b.x, b.y);
                        if (!PlainLetter(cb)) continue;
                        foreach (var w in rules.WordsIfSwapped(a, b))
                        {
                            if (w.Cells == null || !w.Cells.Contains(b)) continue;   // the word must run through where the power-up lands
                            if (_wildSeen > 0 && _wildA == a && _wildB == b)
                            {
                                var window = new List<Vector2Int>(w.Cells); if (!window.Contains(a)) window.Add(a);
                                BeginForced("wild", a, b);
                                TutorialOverlay.Ensure().Show(window, "Power-ups are wild! Slide one into a word!", a, b);
                                WDLog.Log($"[Tut] beat 'wild': power-up kind {c.SpecialKind} ({a.x},{a.y})->({b.x},{b.y}) makes '{w.Word}'");
                                _wildSeen = 0;
                                return;
                            }
                            _wildSeen = 1; _wildA = a; _wildB = b;
                            return;
                        }
                    }
                }
            _wildSeen = 0;
        }

        public static bool MOVE_BEAT = true;
        private static bool s_afterMove;
        private static readonly Vector2Int[] s_dirs4 = { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
        private static bool PlainLetter(RulesCellData c) => c != null && !c.IsStone && !c.IsFrozen && !c.HasGem && !c.IsRainGold && !c.IsWild && !c.IsDropTarget && c.SpecialKind == RulesEngine.SPECIAL_NONE;
        /// <summary>Moves the lesson's letter from <paramref name="from"/> to a neighbour (letters trade places, board redrawn) so that
        /// sliding it back is a move that makes no word. False = nothing changed.</summary>
        private static bool TryParkTeachLetter(RulesEngine rules, Vector2Int from, Vector2Int to, List<Vector2Int> wordCells, out Vector2Int parked)
        {
            parked = default;
            var grid = GridManager.Instance;
            if (grid == null || wordCells == null || wordCells.Contains(from)) { WDLog.Log($"[Tut] move beat: no park - lesson letter ({from.x},{from.y}) is inside the word"); return false; }
            var cf = rules.GetCell(from.x, from.y);
            if (!PlainLetter(cf) || grid.GetTile(from.x, from.y) == null) { WDLog.Log($"[Tut] move beat: no park - lesson letter ({from.x},{from.y}) is not a plain tile ({DescribeCell(cf)})"); return false; }
            foreach (var d in s_dirs4)
            {
                var c = from + d;
                if (c == to || wordCells.Contains(c) || grid.GetTile(c.x, c.y) == null) continue;
                var cc = rules.GetCell(c.x, c.y);
                if (!PlainLetter(cc) || cc.Letter == cf.Letter) { WDLog.Log($"[Tut] move beat: ({c.x},{c.y}) rejected - {DescribeCell(cc)}"); continue; }
                var w1 = rules.WordsIfSwapped(c, from);
                if (w1.Count != 0) { WDLog.Log($"[Tut] move beat: ({c.x},{c.y}) rejected - parking there makes '{w1[0].Word}'"); continue; }   // the parked board must not hold a word here
                char t = cc.Letter; cc.Letter = cf.Letter; cf.Letter = t;           // park it
                var w2 = rules.WordsIfSwapped(c, from);                              // sliding it back must make no word
                if (w2.Count != 0) { t = cc.Letter; cc.Letter = cf.Letter; cf.Letter = t; WDLog.Log($"[Tut] move beat: ({c.x},{c.y}) rejected - sliding back makes '{w2[0].Word}'"); continue; }
                grid.SyncToRulesState(rules);
                parked = c;
                return true;
            }
            return false;
        }
        private static string DescribeCell(RulesCellData c) => c == null ? "no cell" : $"'{c.Letter}' stone={c.IsStone} ice={c.IsFrozen} gem={c.HasGem} gold={c.IsRainGold} wild={c.IsWild} drop={c.IsDropTarget} special={c.SpecialKind}";
        private IEnumerator AfterMoveBeat()
        {
            s_pendingSince = Time.unscaledTime;   // no free moves between the two steps
            yield return new WaitForSeconds(0.3f);
            float waited = 0f;
            while (!BoardIdle() && waited < 8f) { waited += Time.deltaTime; yield return null; }
            s_pendingSince = -1f;
            if (SurvivalManager.AwaitingNextLevel || Done("swap")) yield break;
            s_afterMove = true;
            BeatSwap();
            s_afterMove = false;
        }

        // ── L2: SWIPE — words already on the board are claimed by swiping across them (2026-10-23 Spencer). The level build
        // planted one (RulesEngine.PlantSwipeWord); swaps are refused until it's swiped. ──
        public static bool WantsSwipePlant(LevelRules r) => NEW_TUTORIAL && r.Teach == "swipe" && !Done("swipe");
        /// <summary>11-02: lesson levels whose beat shows a swap get a planted teaching word (RulesEngine.PlantTeachSwap).</summary>
        public static bool WantsTeachPlant(LevelRules r) { string k = KeyFor(r); return NEW_TUTORIAL && (k == "swap" || k == "crates" || k == "ice" || (POWER_BEATS && IsPowerKey(k))) && !Done(k); }
        /// <summary>11-06 (L1-10 recording: crates appear on L3 and L5, the crate lesson was only on L6): a level whose own lesson
        /// has no on-board beat (rocket / bomb / disco / airplane - their splash teaches them) teaches CRATES if it is the first
        /// level with a crate goal. Needs the level's goals placed (LevelRules.HasCrateGoal).</summary>
        public static bool CRATES_ON_FIRST_CRATE_LEVEL = true;
        public static string KeyFor(LevelRules r)
        {
            string k = r.Teach;
            bool hasBeat = k == "swap" || k == "swipe" || k == "crates" || k == "ice" || (POWER_BEATS && IsPowerKey(k) && !Done(k));   // 11-17: a power-up level's own forced move comes first
            if (CRATES_ON_FIRST_CRATE_LEVEL && !hasBeat && LevelRules.HasCrateGoal && !Done("crates")) return "crates";
            // 11-06 Spencer: the first bookshelf / safe level gets one line (RM L13 cupboard: "Make matches next to a CUPBOARD to
            // break it" - no hand, no forced move). Not set as Teach: Teach also turns off the win streak (WinStreak.cs).
            if (string.IsNullOrEmpty(k) && SHOW_BIG_PIECE_TIPS)
            {
                if (r.ShelfBooks > 0 && !Done("shelf")) return "shelf";
                if (r.Safes > 0 && !Done("safe")) return "safe";
            }
            return k;
        }
        private static List<Vector2Int> s_swipeCells;
        private void BeatSwipe()
        {
            var rules = RulesEngine.Instance; var cells = RulesEngine.TutorialSwipeCells; string word = RulesEngine.TutorialSwipeWord;
            if (rules == null || cells == null || word == null) { WDLog.Log("[Tut] swipe beat: no planted word — skipped (not marked done)"); return; }
            for (int i = 0; i < cells.Count; i++)
            {
                var d = rules.GetCell(cells[i].x, cells[i].y);
                if (d == null || char.ToUpperInvariant(d.Letter) != word[i]) { WDLog.Log("[Tut] swipe beat: planted word changed — skipped"); return; }
            }
            BeginForced("swipe", new Vector2Int(-9, -9), new Vector2Int(-9, -9));   // no swap matches -> every swap refused
            s_swipeCells = new List<Vector2Int>(cells);
            TutorialOverlay.Ensure().Show(cells, $"Swipe {word} to claim it!", cells[0], cells[cells.Count - 1], 0.25f * cells.Count);
        }

        // ── L6 CRATES / L9 ICE (11-01, Spencer picked "A"): RM's box beat - the board dims except one piece and its neighbours,
        // ONE sentence in the bubble, no hand, no forced move. Any move (or OBSTACLE_TIP_SECS) clears it. Shown once. ──
        public static float OBSTACLE_TIP_SECS = 6f;
        private void BeatObstacle(string key)
        {
            var rules = RulesEngine.Instance; if (rules == null) return;
            bool ice = key == "ice";
            Vector2Int best = new Vector2Int(-1, -1); float bestScore = float.MinValue;
            int cols = RulesEngine.COLS, rows = RulesEngine.ROWS;
            for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++)
            {
                var d = rules.GetCell(x, y);
                bool hit = d != null && (ice ? d.IsFrozen : (d.IsStone && !d.IsDropTarget && d.TreasureHits == 0));
                if (!hit) continue;
                int open = 0;   // letter neighbours: somewhere a word can touch it
                foreach (var o in new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) })
                {
                    var n = rules.GetCell(x + o.x, y + o.y);
                    if (n != null && !n.IsStone && char.IsLetter(n.Letter)) open++;
                }
                float score = open * 10f - (Mathf.Abs(x - (cols - 1) * 0.5f) + Mathf.Abs(y - (rows - 1) * 0.5f));   // prefer open + central
                if (score > bestScore) { bestScore = score; best = new Vector2Int(x, y); }
            }
            if (best.x < 0) { WDLog.Log($"[Tut] {key} beat: no piece on the board - skipped (not marked done)"); return; }
            string text = ice ? "Use the frozen letter in a word to melt it!" : "Make a word touching a crate to break it!";   // 11-04: 4 sides only (CRATE_DIAGONAL off). 11-04 ice back to THROUGH (ICE_ADJACENT_THAW off)
            // 11-01 Spencer: "shouldnt there be a word that you can make next to one?" - show a REAL move: a swap whose word touches a
            // crate (or runs through ice). Window = the word + the pieces it hits; the hand loops the swap (still passive, any move ok).
            Vector2Int sa = default, sb = default; List<Vector2Int> wcells = null; string w = null;
            bool planted = rules.TeachSwapStillThere();   // 11-02: the level build planted an easy word beside a piece - show that one
            if (planted) { sa = RulesEngine.TeachFrom; sb = RulesEngine.TeachTo; wcells = RulesEngine.TeachCells; w = RulesEngine.TeachWord; }
            if ((planted || rules.FindTutorialSwap(out sa, out sb, out wcells, out w, requireGoal: true)) && wcells != null)
            {
                var win = new List<Vector2Int>(wcells); if (!win.Contains(sa)) win.Add(sa); if (!win.Contains(sb)) win.Add(sb);
                // 11-06 Spencer: the ice lesson lit TWO ice letters (any frozen cell touching the word). Ice melts only THROUGH the
                // word, so spotlight just the frozen letter(s) in it; crates still show the ones the word touches.
                var offs = ice ? new[] { Vector2Int.zero }
                               : new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1), Vector2Int.zero };
                foreach (var c in wcells)
                    foreach (var o in offs)
                    {
                        var q = c + o; var qd = rules.GetCell(q.x, q.y);
                        if (qd != null && (ice ? qd.IsFrozen : (qd.IsStone && !qd.IsDropTarget)) && !win.Contains(q)) win.Add(q);
                    }
                bool aIn = wcells.Contains(sa), bIn = wcells.Contains(sb);
                Vector2Int from = (aIn && !bIn) ? sb : sa, to = (aIn && !bIn) ? sa : sb;
                // 11-06 Spencer: name the word - the bubble only said "make a word", so the shown swap was a guessing game
                if (!string.IsNullOrEmpty(w))
                    text = ice ? $"Spell {w} to melt the ice!" : $"Spell {w} to break the crate!";   // 11-06 cut toward RM length (6-8 words)
                TutorialOverlay.Ensure().Show(win, text, from, to);
                _tipUntil = Time.unscaledTime + OBSTACLE_TIP_SECS + 2f;
                MarkDone(key);
                WDLog.Log($"[Tut] beat '{key}' shows '{w}' ({sa.x},{sa.y})<->({sb.x},{sb.y})");
                return;
            }
            var window = new List<Vector2Int>();
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            {
                var c = new Vector2Int(best.x + dx, best.y + dy);
                if (c.x >= 0 && c.y >= 0 && c.x < cols && c.y < rows) window.Add(c);
            }
            TutorialOverlay.Ensure().Show(window, text, new Vector2Int(-1, -1), new Vector2Int(-1, -1));
            _tipUntil = Time.unscaledTime + OBSTACLE_TIP_SECS;
            MarkDone(key);
            WDLog.Log($"[Tut] beat '{key}' shown at ({best.x},{best.y})");
        }

        // ── L13 BOOKSHELF / L18 SAFE (11-06): show-and-tell. The piece + one ring of cells around it is lit, ONE line, no hand,
        // nothing forced. A tap (after TIP_TAP_GUARD), any move, or BIG_PIECE_TIP_SECS closes it. Shown once. ──
        public static bool  SHOW_BIG_PIECE_TIPS = true;
        public static float BIG_PIECE_TIP_SECS  = 10f;
        public static float TIP_TAP_GUARD       = 0.8f;   // a tap before this is the player still reaching for the board
        private void BeatBigPiece(string key)
        {
            var rules = RulesEngine.Instance; if (rules == null) return;
            int kind = key == "safe" ? 1 : 0;
            int cols = RulesEngine.COLS, rows = RulesEngine.ROWS;
            var piece = new List<Vector2Int>(); int id = -1, hits = 0;
            for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++)
            {
                var d = rules.GetCell(x, y);
                if (d == null || d.ShelfPart <= 0 || d.ShelfKind != kind || d.ShelfBooks <= 0) continue;
                if (id < 0) { id = d.ShelfId; hits = d.ShelfBooks; }
                if (d.ShelfId == id) piece.Add(new Vector2Int(x, y));
            }
            if (piece.Count == 0) { WDLog.Log($"[Tut] {key} beat: no piece on the board - skipped (not marked done)"); return; }
            var window = new List<Vector2Int>();
            foreach (var c in piece)
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                {
                    var q = new Vector2Int(c.x + dx, c.y + dy);
                    if (q.x >= 0 && q.y >= 0 && q.x < cols && q.y < rows && !window.Contains(q)) window.Add(q);
                }
            string text = kind == 1 ? $"Make {hits} words next to the SAFE!"
                                    : "Make words next to the BOOKSHELF!";
            TutorialOverlay.Ensure().Show(window, text, new Vector2Int(-1, -1), new Vector2Int(-1, -1));
            _tipUntil = Time.unscaledTime + BIG_PIECE_TIP_SECS;
            _tapCloseAfter = Time.unscaledTime + TIP_TAP_GUARD;
            MarkDone(key);
            WDLog.Log($"[Tut] beat '{key}' shown ({piece.Count} cells, {hits} hits)");
        }

        /// <summary>SwipeWordInput asks this while a beat is up: only the taught word's cells (either direction).</summary>
        public static bool AllowSwipe(List<Vector2Int> path)
        {
            if (!s_active || s_key != "swipe" || s_swipeCells == null || path == null || path.Count != s_swipeCells.Count) return false;
            foreach (var c in path) if (!s_swipeCells.Contains(c)) return false;
            return true;
        }

        /// <summary>SwipeWordInput calls this when a swiped word is accepted.</summary>
        public static void OnSwipeCommitted()
        {
            if (Instance != null && !s_active && Instance._tipUntil > 0f) { Instance.EndTip(); return; }   // a swipe clears a passive tip too
            if (Instance == null || !s_active || s_key != "swipe") return;
            s_active = false; s_key = null; s_swipeCells = null;
            TutorialOverlay.Instance?.Hide();
            MarkDone("swipe");
            OwlPortrait.Play("proud");   // 11-01: first claimed word
            WDLog.Log("[Tut] beat 'swipe' done");
        }

        private static void BeginForced(string key, Vector2Int a, Vector2Int b)
        {
            s_active = true; s_key = key; s_allowA = a; s_allowB = b;
            HintManager.Instance?.ResetIdleTimer();
        }

        /// <summary>11-16 (Spencer: "they shouldn't be able to make moves on the tinted out part of the board"): while a forced
        /// beat is up, only the lesson's own cells answer a press. A press anywhere else starts nothing - no glow, no lift, no
        /// "no" shake (mashing the dimmed tiles stacked those and left tiles stuck out of place).</summary>
        public static bool AllowsCell(Vector2Int c)
        {
            if (!s_active) return true;
            if (s_key == "swipe") return s_swipeCells != null && s_swipeCells.Contains(c);
            return c == s_allowA || c == s_allowB;
        }

        /// <summary>The move the lesson on screen is waiting for (monkey tester: lets it get past a lesson). Swap beats give 2
        /// cells, the swipe beat gives the word's cells in order.</summary>
        public static bool TaughtMove(List<Vector2Int> into)
        {
            into.Clear();
            if (!s_active) return false;
            if (s_key == "swipe") { if (s_swipeCells == null || s_swipeCells.Count < 2) return false; into.AddRange(s_swipeCells); return true; }
            if (s_allowA.x < 0 || s_allowB.x < 0) return false;
            into.Add(s_allowA); into.Add(s_allowB); return true;
        }

        /// <summary>HandManager.TryBoardSwap asks this first. False = refuse (the normal bounce-back).</summary>
        public static bool AllowSwap(Vector2Int a, Vector2Int b)
        {
            if (!s_active) return true;
            bool ok = (a == s_allowA && b == s_allowB) || (a == s_allowB && b == s_allowA);
            if (!ok) WDLog.Log($"[Tut] swap ({a.x},{a.y})<->({b.x},{b.y}) refused — beat '{s_key}' wants ({s_allowA.x},{s_allowA.y})<->({s_allowB.x},{s_allowB.y})");
            return ok;
        }

        /// <summary>HandManager calls this once a swap is committed.</summary>
        public static void OnSwapCommitted(Vector2Int a, Vector2Int b)
        {
            if (Instance == null) return;
            if (!s_active) { if (Instance._tipUntil > 0f) Instance.EndTip(); return; }   // any move dismisses a passive tip
            string key = s_key;
            s_active = false; s_key = null;
            TutorialOverlay.Instance?.Hide();
            MarkDone(key);
            OwlPortrait.Play(key == "swap" ? "proud" : "happy");   // 11-01: lesson done
            WDLog.Log($"[Tut] beat '{key}' done");
            // 11-17: the FIRST power-up lesson (rocket) has a second step - how to fire one. Taught once; bomb / orb / tome just get made.
            if (key == "move") { Instance.StartCoroutine(Instance.AfterMoveBeat()); return; }   // 11-20: step 2 of the L1 lesson
            if (key == "rocket" && USE_BEAT && !Done("use")) Instance.StartCoroutine(Instance.UseBeat());
            // 11-16: L1 beat 2 - the gem word (a gem only counts when the word runs THROUGH it); falls back to the goal tip
            if (key == "swap") Instance.StartCoroutine(LevelRules.HasGemGoal && !RulesEngine.GEM_ADJACENT_COLLECT && !Done("gem") ? Instance.GemBeat() : Instance.GoalTip());
            else if (key == "gem") Instance.StartCoroutine(Instance.GoalTip());   // "Collect all the gems!" closes the lesson
        }

        // 11-16 (Spencer: split L1 into "slide to make a word" + "pick up the gems"): beat 2 forces a word THROUGH a gem. Uses the word
        // planted at level build (RulesEngine.Teach2*) if beat 1's cascade left it intact, else plants one now; no spot -> goal tip.
        private IEnumerator GemBeat()
        {
            yield return new WaitForSeconds(0.3f);
            float waited = 0f;
            while (!BoardIdle() && waited < 8f) { waited += Time.deltaTime; yield return null; }
            if (SurvivalManager.AwaitingNextLevel) yield break;
            var rules = RulesEngine.Instance; if (rules == null) yield break;
            if (!rules.Teach2StillThere())
            {
                if (!rules.PlantTeachSwap(throughGem: true)) { WDLog.Log("[Tut] gem beat: no spot - goal tip instead"); StartCoroutine(GoalTip()); yield break; }
                rules.ParkTeachAsSecond(); GridManager.Instance?.SyncToRulesState(rules); yield return null;
                WDLog.Log("[Tut] gem beat: planted mid-level");
            }
            var cells = RulesEngine.Teach2Cells; var a = RulesEngine.Teach2From; var b = RulesEngine.Teach2To; string word = RulesEngine.Teach2Word;
            bool aIn = cells.Contains(a), bIn = cells.Contains(b);
            Vector2Int from = (aIn && !bIn) ? b : a, to = (aIn && !bIn) ? a : b;
            var window = new List<Vector2Int>(cells); if (!window.Contains(a)) window.Add(a); if (!window.Contains(b)) window.Add(b);
            BeginForced("gem", a, b);
            TutorialOverlay.Ensure().Show(window, $"Spell {word} to collect the gem!", from, to);   // 11-16 Spencer: "you arent really spelling the word WITH the gem"
            WDLog.Log($"[Tut] LEVEL 1: beat 'gem' shows '{word}'");
        }

        // After the first word: a short pointer at the goal (RM L1 does the same beat).
        private IEnumerator GoalTip()
        {
            yield return new WaitForSeconds(0.3f);
            float waited = 0f;
            while (!BoardIdle() && waited < 8f) { waited += Time.deltaTime; yield return null; }
            if (SurvivalManager.AwaitingNextLevel) yield break;
            string what = LevelRules.HasGemGoal ? "gems" : LevelRules.HasCrateGoal ? "crates" : null;
            string text = what == "gems" ? "Collect all the gems!" : what == "crates" ? "Break all the crates!" : "Finish the goal to win!";   // 11-06 Spencer: cut to RM length
            TutorialOverlay.Ensure().Show(null, text, new Vector2Int(-1, -1), new Vector2Int(-1, -1));
            _tipUntil = Time.unscaledTime + 3.5f;
        }
        // ── 11-17 "USE IT" (RM L4: "Swipe the PROPELLER to see what it does!"). Right after the rocket lesson's swap the new rocket
        // sits on the board: it alone is lit and only a double-tap on it does anything ("Double-tap the ROCKET to fire it!").
        // Double-tap is the one way to fire a power-up nobody finds by themselves; using it in a word they meet by playing.
        // Never a trap: gone rocket -> skipped; no double-tap within USE_BEAT_TIMEOUT -> the lesson lets go. ──
        public static bool USE_BEAT = true;
        public static float USE_BEAT_TIMEOUT = 15f;
        private float _useUntil = -1f;
        private IEnumerator UseBeat()
        {
            // 11-18 (Spencer's log, L3: the new rocket's lesson "was coming up and it didnt lock my inputs"): between the rocket being
            // made and this lesson appearing (~0.5 s plus the falls) the board took input - he dragged the rocket into GIST two
            // frames before the lesson showed, so the spotlight came up on a rocket that was already firing. The board is now
            // held (the same Pending lock the level-start lessons use, self-clearing after PENDING_MAX) until the lesson is up
            // or has decided not to show.
            s_pendingSince = Time.unscaledTime;
            yield return new WaitForSeconds(0.3f);
            float waited = 0f;
            while (!BoardIdle() && waited < 8f) { waited += Time.deltaTime; yield return null; }
            yield return new WaitForSeconds(0.25f);
            s_pendingSince = -1f;   // from here it is either the forced lesson's own lock or nothing
            if (SurvivalManager.AwaitingNextLevel || s_active) yield break;
            var rules = RulesEngine.Instance; if (rules == null) yield break;
            Vector2Int cell = new Vector2Int(-1, -1);
            for (int x = 0; x < RulesEngine.COLS && cell.x < 0; x++) for (int y = 0; y < RulesEngine.ROWS; y++)
            {
                var d = rules.GetCell(x, y);
                if (d != null && (d.SpecialKind == RulesEngine.SPECIAL_ROCKET_H || d.SpecialKind == RulesEngine.SPECIAL_ROCKET_V)) { cell = new Vector2Int(x, y); break; }
            }
            if (cell.x < 0) { WDLog.Log("[Tut] use beat: no rocket on the board after the lesson swap - skipped (not marked done)"); yield break; }
            BeginForced("use", cell, cell);   // only this cell answers a press; every swap is refused
            TutorialOverlay.Ensure().Show(new List<Vector2Int> { cell }, "Double-tap the ROCKET to fire it!", cell, cell);
            _useUntil = Time.unscaledTime + USE_BEAT_TIMEOUT;
            WDLog.Log($"[Tut] beat 'use' at ({cell.x},{cell.y})");
        }
        /// <summary>HandManager.FireSpecialAt calls this when a power-up was set off by hand.</summary>
        /// <summary>11-20 (Spencer: "if you double tap the power-up before the tutorial showing you they count as wilds comes up it
        /// will bug out a bit"): the first tap landed, the lesson appeared, the second tap fired the power-up the lesson was
        /// pointing at - leaving the lesson up over a tile that was gone. While a lesson is up, a double-tap fires a power-up
        /// only in the lesson that teaches the double-tap ("use").</summary>
        public static bool AllowsDoubleTap => !s_active || s_key == "use";
        public static void OnSpecialFired(Vector2Int cell)
        {
            if (Instance == null || !s_active || s_key != "use") return;
            EndUse("done");
            OwlPortrait.Play("happy");
        }
        private static void EndUse(string how)
        {
            s_active = false; s_key = null;
            if (Instance != null) Instance._useUntil = -1f;
            TutorialOverlay.Instance?.Hide();
            MarkDone("use");
            WDLog.Log($"[Tut] beat 'use' {how}");
        }

        // ── 11-17 SAFETY NET (monkey 98181, L2: the swipe lesson's word was destroyed behind the dim and the lesson waited for it
        // forever - swaps refused, nothing to swipe: a soft-lock). A forced lesson whose move no longer exists lets go. Checked
        // twice a second on a still board; it must be missing for a full second (a fall passing through is not "gone").
        // Not marked done, so the lesson comes back the next time its level is played. ──
        private float _watchAt, _goneFor;
        private void WatchLesson()
        {
            if (!s_active) { _goneFor = 0f; return; }
            if (Time.unscaledTime < _watchAt) return;
            _watchAt = Time.unscaledTime + 0.5f;
            var rules = RulesEngine.Instance; if (rules == null || !BoardIdle()) return;
            bool ok = true;
            if (s_key == "swipe")
            {
                string w = RulesEngine.TutorialSwipeWord; ok = s_swipeCells != null && w != null && s_swipeCells.Count == w.Length;
                for (int i = 0; ok && i < s_swipeCells.Count; i++)
                {
                    var d = rules.GetCell(s_swipeCells[i].x, s_swipeCells[i].y);
                    if (d == null || char.ToUpperInvariant(d.Letter) != w[i]) ok = false;
                }
            }
            else if (s_key == "use")
            {
                var d = rules.GetCell(s_allowA.x, s_allowA.y); ok = d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE;
            }
            else if (s_key == "move") ok = true;   // 11-20: this step makes no word on purpose; both tiles are plain and nothing else can move them
            else if (s_allowA.x >= 0 && s_allowB.x >= 0) ok = rules.WordsIfSwapped(s_allowA, s_allowB).Count > 0;
            if (ok) { _goneFor = 0f; return; }
            _goneFor += 0.5f;
            if (_goneFor < 1f) return;
            _goneFor = 0f;
            if (s_key == "use") { EndUse("its rocket is gone - let go"); return; }
            WDLog.Log($"[Tut] beat '{s_key}': its move is no longer on the board - lesson released (not marked done)");
            s_active = false; s_key = null; s_swipeCells = null;
            TutorialOverlay.Instance?.Hide();
        }

        private void EndTip() { _tipUntil = -1f; _tapCloseAfter = -1f; if (!s_active) TutorialOverlay.Instance?.Hide(); }

        private void Update()
        {
            if (_useUntil > 0f && s_active && s_key == "use" && Time.unscaledTime > _useUntil) EndUse("timed out - let go");
            WatchLesson();
            WatchForWildLesson();
            if (_tipUntil > 0f && Time.unscaledTime > _tipUntil) EndTip();
            else if (_tapCloseAfter > 0f && Time.unscaledTime > _tapCloseAfter && WDInput.GetMouseButtonDown(0)) EndTip();   // show-and-tell tips close on a tap
            // Leaving the level mid-beat (quit, map, lose) must never strand the dim or the input lock.
            if ((s_active || _tipUntil > 0f) && (!SurvivalManager.IsSurvivalMode || SurvivalManager.AwaitingNextLevel
                || (LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard)))
                Cancel();
        }

        public static void Cancel()
        {
            if (s_active) WDLog.Log($"[Tut] beat '{s_key}' cancelled (left the level) — will run again next time");
            s_active = false; s_key = null; s_swipeCells = null;
            if (Instance != null) { Instance._tipUntil = -1f; Instance._tapCloseAfter = -1f; Instance._useUntil = -1f; }
            s_pendingSince = -1f;
            TutorialOverlay.Instance?.Hide();
        }
    }
}
