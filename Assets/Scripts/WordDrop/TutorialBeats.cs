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

        private const string PREF = "tut2_";
        public static bool Done(string key) => PlayerPrefs.GetInt(PREF + key, 0) == 1;
        private static void MarkDone(string key) { PlayerPrefs.SetInt(PREF + key, 1); PlayerPrefs.Save(); }
        public static readonly string[] KEYS = { "swap", "swipe", "goals", "rocket", "bomb", "disco", "crates", "airplane", "ice", "theme", "boosters", "bonus" };
        public static void ResetAll() { foreach (var k in KEYS) PlayerPrefs.DeleteKey(PREF + k); PlayerPrefs.Save(); WDLog.Log("[Tut] all beats reset"); }

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
            StartCoroutine(RunForLevel());
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
            var sm = SurvivalManager.Instance; if (sm == null) yield break;
            int stage = LevelMapPanel.RunLevel(sm.CurrentStageIndex);
            string key = LevelRules.For(stage).Teach;
            if (string.IsNullOrEmpty(key) || Done(key)) yield break;
            WDLog.Log($"[Tut] LEVEL {stage}: beat '{key}'");
            switch (key)
            {
                case "swap": BeatSwap(); break;
                case "swipe": BeatSwipe(); break;
                default: WDLog.Log($"[Tut] beat '{key}' not built yet"); break;
            }
        }

        // ── L1: SWAP ──────────────────────────────────────────────────────────────────────────────────────────────
        private void BeatSwap()
        {
            var rules = RulesEngine.Instance; if (rules == null) return;
            // Own picker, not the best-hint: one forward 4-5 letter common word (v1 showed ERASES spelled backwards).
            if (!rules.FindTutorialSwap(out var a, out var b, out var wordCells, out var word) || wordCells == null)
            { WDLog.Log("[Tut] swap beat: no move on this board — skipped (not marked done)"); return; }
            // The word runs through the cell a letter moves INTO; the hand drags the OTHER tile in (same rule as the hint).
            bool aIn = wordCells.Contains(a), bIn = wordCells.Contains(b);
            Vector2Int from = (aIn && !bIn) ? b : a, to = (aIn && !bIn) ? a : b;
            var window = new List<Vector2Int>(wordCells);
            if (!window.Contains(a)) window.Add(a);
            if (!window.Contains(b)) window.Add(b);
            BeginForced("swap", a, b);
            TutorialOverlay.Ensure().Show(window, $"Slide the letter to spell {word}!", from, to);
        }

        // ── L2: SWIPE — words already on the board are claimed by swiping across them (2026-10-23 Spencer). The level build
        // planted one (RulesEngine.PlantSwipeWord); swaps are refused until it's swiped. ──
        public static bool WantsSwipePlant(LevelRules r) => NEW_TUTORIAL && r.Teach == "swipe" && !Done("swipe");
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
            TutorialOverlay.Ensure().Show(cells, $"{word} is already on the board! Swipe across it to claim it.", cells[0], cells[cells.Count - 1], 0.25f * cells.Count);
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
            if (Instance == null || !s_active || s_key != "swipe") return;
            s_active = false; s_key = null; s_swipeCells = null;
            TutorialOverlay.Instance?.Hide();
            MarkDone("swipe");
            WDLog.Log("[Tut] beat 'swipe' done");
        }

        private static void BeginForced(string key, Vector2Int a, Vector2Int b)
        {
            s_active = true; s_key = key; s_allowA = a; s_allowB = b;
            HintManager.Instance?.ResetIdleTimer();
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
            WDLog.Log($"[Tut] beat '{key}' done");
            if (key == "swap") Instance.StartCoroutine(Instance.GoalTip());
        }

        // After the first word: a short pointer at the goal (RM L1 does the same beat).
        private IEnumerator GoalTip()
        {
            yield return new WaitForSeconds(0.3f);
            float waited = 0f;
            while (!BoardIdle() && waited < 8f) { waited += Time.deltaTime; yield return null; }
            if (SurvivalManager.AwaitingNextLevel) yield break;
            string what = LevelRules.HasGemGoal ? "gems" : LevelRules.HasCrateGoal ? "crates" : null;
            string text = what != null ? $"Nice! Collect every {what.TrimEnd('s')} to win!" : "Nice! Finish the goal to win!";
            TutorialOverlay.Ensure().Show(null, text, new Vector2Int(-1, -1), new Vector2Int(-1, -1));
            _tipUntil = Time.unscaledTime + 3.5f;
        }
        private void EndTip() { _tipUntil = -1f; if (!s_active) TutorialOverlay.Instance?.Hide(); }

        private void Update()
        {
            if (_tipUntil > 0f && Time.unscaledTime > _tipUntil) EndTip();
            // Leaving the level mid-beat (quit, map, lose) must never strand the dim or the input lock.
            if ((s_active || _tipUntil > 0f) && (!SurvivalManager.IsSurvivalMode || SurvivalManager.AwaitingNextLevel
                || (LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard)))
                Cancel();
        }

        public static void Cancel()
        {
            if (s_active) WDLog.Log($"[Tut] beat '{s_key}' cancelled (left the level) — will run again next time");
            s_active = false; s_key = null; s_swipeCells = null;
            if (Instance != null) Instance._tipUntil = -1f;
            TutorialOverlay.Instance?.Hide();
        }
    }
}
