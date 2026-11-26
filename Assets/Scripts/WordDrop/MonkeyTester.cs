#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>MONKEY TEST (2026-11-16 Spencer: "make sure someone can't slam on input while an animation is going and it bugs
    /// out ... or between menus, tapping on the screen causing unintended effects").
    ///
    /// Plays the REAL game in Play mode with one fake finger (WDInput + a BaseInput override for Unity UI, so buttons go through
    /// the normal EventSystem path). It mixes real word-making swaps (so levels progress) with random taps, drags, swipes,
    /// double-taps, button presses and rapid "mash" bursts, fired with NO regard for what is animating - that is the point.
    ///
    /// While it plays it watches for FAULTS and writes them to MonkeyReports/monkey_latest.txt (+ a screenshot each):
    ///   ERROR        any error / exception in the log (includes BoardInvariants' [Invariant] lines)
    ///   STUCK        nothing about the game changed for STUCK_SECONDS although the monkey kept tapping (soft lock)
    ///   PASS-THROUGH a swap / swipe / booster was ACCEPTED while a card or the home screen had been covering the board
    ///   DOUBLE-FIRE  the same one-off event (level start, level clear, booster use ...) logged twice within DOUBLE_FIRE_SECONDS
    ///   COUNTER      moves / coins went somewhere impossible (negative)
    ///   CHECK        two cards visible at once for a while (may be intended - look at the screenshot)
    ///
    /// LIMITS: finds things that BREAK, not things that look off. One finger, editor only. The board's letters are random, so a
    /// seed repeats the monkey's choices but not the exact game. It changes save data (coins, progress, settings) - a copy of
    /// the PlayerPrefs file is saved next to the report before it starts.
    /// Start/stop: FX Test Menu "MONKEY TEST" button, or Esc to stop.</summary>
    [DefaultExecutionOrder(-32000)]   // set the fake pointer before the EventSystem and every gameplay script read it
    public class MonkeyTester : MonoBehaviour
    {
        public static MonkeyTester Instance { get; private set; }

        // ── tuning ──
        public static float MINUTES = 10f;
        // PLAY BADLY (2026-11-16): the hint-fed monkey never loses, so Out of Moves / Play On / Try Again / lives never got hammered.
        // On: shortly after most levels start, it leaves itself only a few moves (writes the moves-used counter), then plays as usual.
        /// <summary>11-17: when the monkey gets past the last level it jumps back to this level and carries on (12 = where the
        /// boosters start to unlock, so a loop covers every blocker and every booster).</summary>
        public static int   LOOP_BACK_TO = 12;
        private float _loopJumpAt; private int _loops;
        public static bool  PLAY_BADLY = false;
        public static float BADLY_SHARE = 0.75f;        // share of level starts that get cut short
        public static float BADLY_NO_LIVES_SECONDS = 12f; // let it sit on zero lives this long before topping up
        public static int   SEED = 0;                   // 0 = pick from the clock (printed in the report)
        public static float STUCK_SECONDS = 30f;        // no change at all for this long = STUCK
        public static float STUCK_GIVE_UP_SECONDS = 60f; // still stuck this much later = end the run, leave the game as it is
        public static float COVER_GRACE = 0.4f;         // a card must have covered the board this long before a move under it is a fault
        public static float DOUBLE_FIRE_SECONDS = 0.8f;
        public static float MULTI_CARD_SECONDS = 1.5f;
        public static int   MAX_SHOTS = 40;
        public static float REPORT_EVERY = 30f;

        // PLAYER MODE (2026-11-26 Spencer: "is it possible for you to get the bot to play and you use the screen record function
        // for sight?"). The same fake finger, but it PLAYS: does a lesson's taught move, swipes a word that is waiting to be
        // claimed, else makes the game's own hint swap; on a card / the home screen it presses the forward button (Play, Continue,
        // Next ...) and keeps off Close / Shop / Settings. One gesture every PLAYER_PACE seconds so frames show settled boards.
        // Started by BotRemote ("watch=1" request line), which also saves the screenshots.
        public static bool  PLAYER_MODE = false;
        public static float PLAYER_PACE = 1.1f;
        private string _playerButtons = "";
        public bool Running { get; private set; }
        public string Status { get; private set; } = "";
        public static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "MonkeyReports");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("MonkeyTester"); DontDestroyOnLoad(go);
            Instance = go.AddComponent<MonkeyTester>();
        }

        // ── pointer script ──
        private enum Ph : byte { None, Down, Held, Up }
        private struct Sample { public Ph ph; public Vector3 pos; }
        private readonly Queue<Sample> _q = new Queue<Sample>();
        private Vector3 _lastPos;
        private bool _fingerDown;
        private int _gapFrames; private float _gapUntil;

        // ── run state ──
        private System.Random _rng; private int _seed;
        private float _t0, _endAt, _nextReport, _nextSlowCheck, _nextHookTry;
        private string _endReason = "", _stamp = "", _prefsBackup = "";
        private bool _prevRunInBackground;
        private MonkeyUIInput _uiInput;
        private readonly List<string> _recent = new List<string>();
        private readonly StringBuilder _inputLog = new StringBuilder();
        private readonly Dictionary<string, int> _gestureCounts = new Dictionary<string, int>();
        private float _burnAt, _zeroLivesSince, _lastContinueAt; private int _burns;
        private int _gestures, _swaps, _swipes, _boosters, _levelsStarted, _levelsCleared, _levelsFailed, _heartRefills, _spikes, _frames, _shots;
        private float _worstFrame;

        // ── watching ──
        private class Fault { public string kind, msg, detail, state, shot; public float at; public int level, count; public List<string> inputs; }
        private readonly List<Fault> _faults = new List<Fault>();
        private readonly Dictionary<string, Fault> _faultByKey = new Dictionary<string, Fault>();
        private readonly Dictionary<string, int> _warnings = new Dictionary<string, int>();
        private readonly Dictionary<string, float> _lastEventAt = new Dictionary<string, float>();
        private bool _wasCovered; private float _coveredSince;
        private string _fingerprint = ""; private float _lastChange; private bool _stuckReported;
        private string _multiCards = ""; private float _multiSince;
        private static readonly FieldInfo s_movesUsedField = typeof(SurvivalManager).GetField("_currentStageMovesUsed", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type[] s_cardTypes =
        {
            typeof(StageClearModal), typeof(ContinueModal), typeof(LevelIntroModal), typeof(HeartWaitModal), typeof(StarterPackModal),
            typeof(OutOfMovesModal), typeof(LevelCompletedModal), typeof(UnlockModal), typeof(SaveStreakModal), typeof(BoosterChoiceModal),
            typeof(DailyAlreadyPlayedModal), typeof(WorldCompleteModal),
        };
        private static readonly HashSet<string> s_oneOffEvents = new HashSet<string>
        {
            "level_start", "stage_clear", "level_failed", "stage_fail", "game_start", "game_over", "run_end",
            "continue_ad_started", "continue_ad_completed", "continue_declined", "bonus_blast",
        };

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        public void Toggle() { if (Running) StopRun("stopped from the menu"); else StartRun(); }

        public void StartRun()
        {
            if (Running) return;
            _seed = SEED != 0 ? SEED : (int)(DateTime.Now.Ticks % 1000000) + 1;
            _rng = new System.Random(_seed);
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _q.Clear(); _recent.Clear(); _inputLog.Length = 0; _gestureCounts.Clear(); _faults.Clear(); _faultByKey.Clear();
            _warnings.Clear(); _lastEventAt.Clear();
            _gestures = _swaps = _swipes = _boosters = _levelsStarted = _levelsCleared = _levelsFailed = _heartRefills = _spikes = _frames = _shots = 0;
            _worstFrame = 0f; _loopJumpAt = 0f; _loops = 0; _burnAt = 0f; _lastContinueAt = 0f; _burns = 0; _zeroLivesSince = 0f; _wasCovered = false; _fingerprint = ""; _stuckReported = false; _multiCards = ""; _endReason = "";
            _t0 = Time.unscaledTime; _endAt = _t0 + MINUTES * 60f; _lastChange = _t0; _nextReport = _t0 + REPORT_EVERY; _nextSlowCheck = 0f;
            _gapFrames = 30; _gapUntil = 0f; _lastPos = new Vector3(Screen.width * 0.5f, Screen.height * 0.5f, 0f); _fingerDown = false;

            try { Directory.CreateDirectory(Folder); } catch { }
            BackupPrefs();
            _prevRunInBackground = Application.runInBackground; Application.runInBackground = true;
            WDInput.BeginFake();
            HookUI(true);
            Application.logMessageReceived += OnLog;
            Running = true;
            Debug.Log($"[Monkey] START seed {_seed}, {MINUTES:F0} min{(PLAY_BADLY ? ", PLAY BADLY" : "")}. Esc stops it. Report -> {Folder}/monkey_latest.txt");
        }

        public void StopRun(string why)
        {
            if (!Running) return;
            Running = false; _endReason = why;
            Application.logMessageReceived -= OnLog;
            WDInput.EndFake();
            HookUI(false);
            Application.runInBackground = _prevRunInBackground;
            WriteReport(true);
            Status = $"last run: {_faults.Count} fault(s), {why}";
            Debug.Log($"[Monkey] END ({why}) - {_faults.Count} fault(s), {_gestures} gestures. Report -> {Folder}/monkey_latest.txt");
        }

        private void OnDisable() { if (Running) StopRun("play mode ended"); }

        private void BackupPrefs()
        {
            _prefsBackup = "";
            try
            {
                PlayerPrefs.Save();
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string src = Path.Combine(home, "Library/Preferences", $"unity.{Application.companyName}.{Application.productName}.plist");
                if (File.Exists(src)) { _prefsBackup = Path.Combine(Folder, $"playerprefs_before_{_stamp}.plist"); File.Copy(src, _prefsBackup, true); }
            }
            catch (Exception ex) { Debug.Log($"[Monkey] could not back up PlayerPrefs: {ex.Message}"); }
        }

        /// <summary>Route Unity UI (buttons, scroll views) through the fake finger too, and undo it afterwards.</summary>
        private void HookUI(bool on)
        {
            var es = EventSystem.current;
            var module = es != null ? es.GetComponent<StandaloneInputModule>() : null;
            if (on)
            {
                if (module == null) { Debug.Log("[Monkey] no StandaloneInputModule - UI buttons will not get fake taps"); return; }
                _uiInput = module.gameObject.GetComponent<MonkeyUIInput>();
                if (_uiInput == null) _uiInput = module.gameObject.AddComponent<MonkeyUIInput>();
                module.inputOverride = _uiInput;
            }
            else
            {
                if (module != null) module.inputOverride = null;
                if (_uiInput != null) Destroy(_uiInput);
                _uiInput = null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private void Update()
        {
            if (!Running) return;
            float now = Time.unscaledTime;
            if (Input.GetKeyDown(KeyCode.Escape)) { StopRun("Esc"); return; }
            if (now >= _endAt) { StopRun("time up"); return; }
            if (_loopJumpAt > 0f && now >= _loopJumpAt)
            {   // 11-17: past the last level -> start the loop again (see LOOP_BACK_TO)
                _loopJumpAt = 0f;
                var smj = SurvivalManager.Instance;
                if (smj != null)
                {
                    int to = Mathf.Clamp(LOOP_BACK_TO, 1, Mathf.Max(1, ChapterEndCard.CONTENT_END_LEVEL));
                    Debug.Log($"[Monkey] end of the levels - jumping back to level {to} (loop {++_loops})");
                    LevelRules.ForceFreshBoardOnNextApply();
                    smj.DebugJumpToStage(to + LevelMapPanel.TUTORIAL_LEVELS);
                }
            }

            // The UI module drops events while the Game view is not focused - keep it listening.
            var es = EventSystem.current;
            if (es != null && !es.isFocused) es.SendMessage("OnApplicationFocus", true, SendMessageOptions.DontRequireReceiver);
            if (_uiInput == null && now >= _nextHookTry) { _nextHookTry = now + 1f; HookUI(true); }   // the EventSystem was rebuilt (scene change)

            _frames++;
            if (_frames > 10) { float dt = Time.unscaledDeltaTime; if (dt > _worstFrame) _worstFrame = dt; if (dt > 0.25f) _spikes++; }

            try { Watch(now); } catch (Exception ex) { Debug.Log($"[Monkey] watch skipped a beat: {ex.Message}"); }
            if (!Running) return;

            if (_q.Count > 0)
            {
                var s = _q.Dequeue();
                WDInput.SetFake(s.ph == Ph.Down, s.ph == Ph.Down || s.ph == Ph.Held, s.ph == Ph.Up, s.pos);
                _lastPos = s.pos; _fingerDown = s.ph == Ph.Down || s.ph == Ph.Held;
                if (_q.Count == 0) ChooseGap(now);
            }
            else
            {
                WDInput.SetFake(false, false, false, _lastPos); _fingerDown = false;
                if (_gapFrames > 0) _gapFrames--;
                else if (now >= _gapUntil)
                {
                    try { PlanGesture(); } catch (Exception ex) { Debug.Log($"[Monkey] gesture skipped: {ex.Message}"); _q.Clear(); }
                    if (_q.Count == 0) _gapFrames = 5;
                }
            }

            if (now >= _nextReport) { _nextReport = now + REPORT_EVERY; WriteReport(false); }
            Status = $"MONKEY RUNNING  {(_endAt - now) / 60f:F1} min left  -  {_faults.Count} fault(s)  -  tap to STOP";
        }

        /// <summary>Pause between gestures: mostly none (hammering), sometimes a breath so the board can settle and real play happens.</summary>
        private void ChooseGap(float now)
        {
            if (PLAYER_MODE) { _gapFrames = 0; _gapUntil = now + PLAYER_PACE; return; }
            double r = _rng.NextDouble();
            if (r < 0.50) { _gapFrames = _rng.Next(0, 7); _gapUntil = 0f; }
            else if (r < 0.85) { _gapFrames = 0; _gapUntil = now + 0.1f + (float)_rng.NextDouble() * 0.5f; }
            else { _gapFrames = 0; _gapUntil = now + 0.8f + (float)_rng.NextDouble() * 1.7f; }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // GESTURES
        private void PlanGesture()
        {
            bool board = BoardExists();
            bool covered = Covered(out _);
            if (PLAYER_MODE) { PlanPlayerGesture(board, covered); return; }
            double r = _rng.NextDouble();
            // share of each gesture: board open vs a card / the home screen on top
            double real = covered ? 0.06 : 0.32, drag = covered ? 0.05 : 0.10, swipe = covered ? 0.04 : 0.08, cell = covered ? 0.04 : 0.10,
                   dbl = covered ? 0.02 : 0.05, booster = covered ? 0.02 : 0.05, button = covered ? 0.50 : 0.12, any = covered ? 0.09 : 0.04;
            if (!board) { real = drag = swipe = cell = dbl = 0; button = 0.65; any = 0.12; }

            // a lesson is waiting for its one taught move: mash wrong things most of the time, do the taught move now and then
            if (board && !covered && TutorialBeats.BlocksInput && _rng.NextDouble() < 0.2 && TaughtMove()) return;
            if ((r -= real) < 0) { if (RealMove()) return; }
            else if ((r -= drag) < 0) { if (RandomDrag()) return; }
            else if ((r -= swipe) < 0) { if (RandomSwipe()) return; }
            else if ((r -= cell) < 0) { if (CellTap(false)) return; }
            else if ((r -= dbl) < 0) { if (CellTap(true)) return; }
            else if ((r -= booster) < 0) { if (BoosterTap()) return; }
            else if ((r -= button) < 0) { if (ButtonTap()) return; }
            else if ((r -= any) < 0) { AnywhereTap(); return; }
            else { Mash(board); return; }
            // the chosen gesture had no target right now - press a button instead, or tap anywhere
            if (!ButtonTap()) AnywhereTap();
        }

        private static readonly string[] s_goNames = { "Play", "Continue", "Advance", "Confirm", "Next", "Claim", "Collect", "Start", "Retry", "Again", "Skip", "Ok", "OK" };
        private static readonly string[] s_avoidNames = { "Close", "Settings", "Help", "Exit", "Ad", "Share", "Shop", "Buy", "Daily", "Menu", "Survival", "TileBag", "Shuffle", "Hint", "Tab", "Nav", "Profile", "Reset", "Booster", "Mallet", "Store", "Pack", "Coin" };
        private void PlanPlayerGesture(bool board, bool covered)
        {
            if (board && !covered)
            {
                if (BoosterIntro.Running)
                {   // the booster lesson: press the lit slot, then (once it is armed) a tile in the middle of the board
                    var bmI = BoosterManager.Instance;
                    if (bmI != null && bmI.AimMode)
                    {
                        var pc = new Vector2Int(RulesEngine.COLS / 2, Mathf.Max(1, RulesEngine.ROWS / 2));
                        if (BoosterManager.TutorialAllowed != null) foreach (var c in BoosterManager.TutorialAllowed) { pc = c; break; }
                        AddTap(CellScreen(pc.x, pc.y), 2); Note("PLAYER", $"booster lesson: tap tile ({pc.x},{pc.y})"); return;
                    }
                    if (BoosterIntro.StepSlot != null && ScreenPointOf(BoosterIntro.StepSlot, out var ps)) { AddTap(ps, 2); Note("PLAYER", "booster lesson: tap the lit slot " + BoosterIntro.StepSlot.name); return; }
                }
                if (TutorialBeats.BlocksInput)
                {
                    if (TutorialBeats.TaughtMove(_taught) && _taught.Count >= 2 && _taught[0] == _taught[_taught.Count - 1])
                    { var p0 = CellScreen(_taught[0].x, _taught[0].y); AddTap(p0, 2); AddGap(5); AddTap(p0, 2); Note("PLAYER", $"double-tap ({_taught[0].x},{_taught[0].y})"); return; }
                    if (TaughtMove()) return;
                    if (_taught.Count == 1) { var p1 = CellScreen(_taught[0].x, _taught[0].y); AddTap(p1, 2); AddGap(4); AddTap(p1, 2); Note("PLAYER", $"double-tap ({_taught[0].x},{_taught[0].y})"); return; }
                }
                var hm = HandManager.Instance;
                if (hm != null && hm.TryGetWaitingWord(out var cells, out var word) && cells != null && cells.Count >= 2)
                {
                    var a = cells[0]; var b = cells[cells.Count - 1];
                    AddDrag(CellScreen(a.x, a.y), CellScreen(b.x, b.y), cells.Count * 5);
                    Note("PLAYER", $"claim '{word}' ({a.x},{a.y})->({b.x},{b.y})");
                    return;
                }
                Vector2Int sa, sb; List<Vector2Int> hc; bool found;
                try { found = RulesEngine.Instance.FindSwapHint(false, out sa, out sb, out hc); } catch { found = false; sa = sb = default; }
                if (found && sa.x >= 0 && sb.x >= 0)
                {
                    AddDrag(CellScreen(sa.x, sa.y), CellScreen(sb.x, sb.y), 8);
                    Note("PLAYER", $"swap ({sa.x},{sa.y})->({sb.x},{sb.y})");
                    return;
                }
                if (TutorialBeats.BlocksInput) { AddTap(new Vector3(Screen.width * 0.5f, Screen.height * 0.12f, 0f), 2); Note("PLAYER", "tap (lesson up, no taught move)"); return; }
                Note("PLAYER", "no move found - waiting");
                AddGap(30);
                return;
            }
            // a card or the home screen: the forward button if there is one, never a way out / a shop
            CollectButtons();
            var sbn = new StringBuilder(); foreach (var t in _targets) sbn.Append(t.name).Append(' ');
            if (sbn.ToString() != _playerButtons) { _playerButtons = sbn.ToString(); Note("PLAYER", "buttons on screen: " + _playerButtons); }
            foreach (var go in s_goNames)
                foreach (var t in _targets)
                    if (t.name.IndexOf(go, StringComparison.Ordinal) >= 0) { AddTap(t.pos, 2); Note("PLAYER", "button " + t.name); return; }
            var ok = new List<(Vector3 pos, string name)>();
            foreach (var t in _targets)
            {
                bool bad = false; foreach (var av in s_avoidNames) if (t.name.IndexOf(av, StringComparison.Ordinal) >= 0) { bad = true; break; }
                if (!bad) ok.Add(t);
            }
            if (ok.Count > 0) { var t = ok[_rng.Next(0, ok.Count)]; AddTap(t.pos, 2); Note("PLAYER", "button (unlisted) " + t.name); return; }
            AddTap(new Vector3(Screen.width * 0.5f, Screen.height * 0.3f, 0f), 2);
            Note("PLAYER", "tap to continue");
        }

        private bool BoardExists() => GridManager.Instance != null && RulesEngine.Instance != null && Camera.main != null
                                      && (SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode);

        private Vector3 CellScreen(int c, int r) { var p = Camera.main.WorldToScreenPoint(GridManager.Instance.CellToWorld(c, r)); p.z = 0f; return p; }
        private Vector2Int RandomCell() => new Vector2Int(_rng.Next(0, RulesEngine.COLS), _rng.Next(0, Mathf.Max(1, RulesEngine.ROWS)));

        private void AddTap(Vector3 p, int holdFrames = -1)
        {
            if (holdFrames < 0) holdFrames = _rng.Next(0, 3);
            _q.Enqueue(new Sample { ph = Ph.Down, pos = p });
            for (int i = 0; i < holdFrames; i++) _q.Enqueue(new Sample { ph = Ph.Held, pos = p });
            _q.Enqueue(new Sample { ph = Ph.Up, pos = p });
        }
        private void AddGap(int frames) { for (int i = 0; i < frames; i++) _q.Enqueue(new Sample { ph = Ph.None, pos = _lastPos }); }
        private void AddDrag(Vector3 a, Vector3 b, int frames)
        {
            _q.Enqueue(new Sample { ph = Ph.Down, pos = a });
            for (int i = 1; i <= frames; i++) _q.Enqueue(new Sample { ph = Ph.Held, pos = Vector3.Lerp(a, b, i / (float)frames) });
            _q.Enqueue(new Sample { ph = Ph.Up, pos = b });
        }

        /// <summary>A swap that makes a word (the game's own hint finder) - keeps levels moving so the monkey reaches every screen.</summary>
        private bool RealMove()
        {
            Vector2Int a, b; List<Vector2Int> cells;
            bool found;
            try { found = RulesEngine.Instance.FindSwapHint(false, out a, out b, out cells); } catch { return false; }
            if (!found || a.x < 0 || b.x < 0) return false;
            AddDrag(CellScreen(a.x, a.y), CellScreen(b.x, b.y), _rng.Next(2, 9));
            Note("REAL MOVE", $"drag ({a.x},{a.y})->({b.x},{b.y})");
            return true;
        }

        private readonly List<Vector2Int> _taught = new List<Vector2Int>();
        private bool TaughtMove()
        {
            if (!TutorialBeats.TaughtMove(_taught) || _taught.Count < 2) return false;
            var a = _taught[0]; var b = _taught[_taught.Count - 1];
            AddDrag(CellScreen(a.x, a.y), CellScreen(b.x, b.y), Mathf.Max(6, _taught.Count * 4));
            Note("TAUGHT MOVE", $"drag ({a.x},{a.y})->({b.x},{b.y})");
            return true;
        }

        private bool _triedClose;
        private bool PressClose()
        {
            CollectButtons();
            foreach (var t in _targets)
                if (t.name == "CloseBtn") { _q.Clear(); AddTap(t.pos, 2); Note("BUTTON", "CloseBtn (stuck check)"); return true; }
            return false;
        }

        private bool RandomDrag()
        {
            var a = RandomCell(); var b = a;
            switch (_rng.Next(0, 4)) { case 0: b.x++; break; case 1: b.x--; break; case 2: b.y++; break; default: b.y--; break; }
            if (b.x < 0 || b.y < 0 || b.x >= RulesEngine.COLS || b.y >= RulesEngine.ROWS) return false;
            AddDrag(CellScreen(a.x, a.y), CellScreen(b.x, b.y), _rng.Next(1, 10));
            Note("RANDOM DRAG", $"({a.x},{a.y})->({b.x},{b.y})");
            return true;
        }

        private bool RandomSwipe()
        {
            var a = RandomCell(); int len = _rng.Next(2, 6); bool horiz = _rng.Next(0, 2) == 0; int dir = _rng.Next(0, 2) == 0 ? 1 : -1;
            var b = new Vector2Int(Mathf.Clamp(a.x + (horiz ? dir * len : 0), 0, RulesEngine.COLS - 1), Mathf.Clamp(a.y + (horiz ? 0 : dir * len), 0, RulesEngine.ROWS - 1));
            if (a == b) return false;
            AddDrag(CellScreen(a.x, a.y), CellScreen(b.x, b.y), _rng.Next(2, 14));
            Note("RANDOM SWIPE", $"({a.x},{a.y})->({b.x},{b.y})");
            return true;
        }

        private bool CellTap(bool twice)
        {
            var c = RandomCell();
            // half the double-taps go for a power-up (double-tap fires it)
            if (twice && _rng.Next(0, 2) == 0)
                for (int tries = 0; tries < 30; tries++)
                {
                    var t = RandomCell(); var cell = RulesEngine.Instance.GetCell(t.x, t.y);
                    if (cell != null && cell.SpecialKind != RulesEngine.SPECIAL_NONE) { c = t; break; }
                }
            var p = CellScreen(c.x, c.y);
            AddTap(p);
            if (twice) { AddGap(_rng.Next(1, 7)); AddTap(p); }
            Note(twice ? "DOUBLE TAP" : "CELL TAP", $"({c.x},{c.y})");
            return true;
        }

        private bool BoosterTap()
        {
            var slots = FindObjectsByType<BoosterHUDSlot>(FindObjectsSortMode.None);
            if (slots == null || slots.Length == 0) return false;
            var s = slots[_rng.Next(0, slots.Length)];
            if (!s.gameObject.activeInHierarchy || !ScreenPointOf(s.transform, out var p)) return false;
            AddTap(p);
            Note("BOOSTER TAP", s.name);
            return true;
        }

        private readonly List<RaycastResult> _hits = new List<RaycastResult>();
        private readonly List<(Vector3 pos, string name)> _targets = new List<(Vector3, string)>();

        /// <summary>Every button the finger could actually reach right now (it is the top thing under its own centre).</summary>
        private void CollectButtons()
        {
            _targets.Clear();
            var es = EventSystem.current; if (es == null) return;
            var seen = new HashSet<GameObject>();
            void Consider(Component c)
            {
                if (c == null || !c.gameObject.activeInHierarchy || !seen.Add(c.gameObject)) return;
                if (!ScreenPointOf(c.transform, out var p)) return;
                if (p.x < 0 || p.y < 0 || p.x > Screen.width || p.y > Screen.height) return;
                var ped = new PointerEventData(es) { position = p };
                _hits.Clear(); es.RaycastAll(ped, _hits);
                if (_hits.Count == 0) return;
                var top = _hits[0].gameObject.transform;
                if (top != c.transform && !top.IsChildOf(c.transform) && !c.transform.IsChildOf(top)) return;   // something else is on top of it
                _targets.Add((p, c.name));
            }
            foreach (var s in FindObjectsByType<Selectable>(FindObjectsSortMode.None)) if (s.IsInteractable()) Consider(s);
            foreach (var a in FindObjectsByType<ArtButton>(FindObjectsSortMode.None)) Consider(a);
        }

        private bool ButtonTap()
        {
            // 1 in 4: probe random points for anything tappable that is not a standard button (map nodes, tabs, cards)
            if (_rng.Next(0, 4) == 0 && ProbeTap()) return true;
            CollectButtons();
            if (_targets.Count == 0) return ProbeTap();
            var t = _targets[_rng.Next(0, _targets.Count)];
            AddTap(Jitter(t.pos, 6f));
            Note("BUTTON", t.name);
            return true;
        }

        private bool ProbeTap()
        {
            var es = EventSystem.current; if (es == null) return false;
            for (int i = 0; i < 12; i++)
            {
                var p = new Vector3((float)_rng.NextDouble() * Screen.width, (float)_rng.NextDouble() * Screen.height, 0f);
                var ped = new PointerEventData(es) { position = p };
                _hits.Clear(); es.RaycastAll(ped, _hits);
                if (_hits.Count == 0) continue;
                var go = _hits[0].gameObject;
                var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(go) ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(go);
                if (handler == null) continue;
                AddTap(p);
                Note("TAPPABLE", handler.name);
                return true;
            }
            return false;
        }

        private void AnywhereTap()
        {
            var p = new Vector3((float)_rng.NextDouble() * Screen.width, (float)_rng.NextDouble() * Screen.height, 0f);
            AddTap(p);
            Note("TAP ANYWHERE", $"{p.x:F0},{p.y:F0}");
        }

        /// <summary>The "slam on the screen" case: a burst of taps a frame or two apart, on one button, on the board, or all over.</summary>
        private void Mash(bool board)
        {
            int n = _rng.Next(4, 15); int kind = _rng.Next(0, 5); string what;
            if (kind <= 1)
            {
                CollectButtons();
                if (_targets.Count > 0)
                {
                    var t = _targets[_rng.Next(0, _targets.Count)];
                    for (int i = 0; i < n; i++) { AddTap(Jitter(t.pos, 5f), _rng.Next(0, 2)); AddGap(_rng.Next(0, 3)); }
                    Note("MASH BUTTON", $"{t.name} x{n}"); return;
                }
                kind = 4;
            }
            if (kind <= 3 && board)
            {
                bool same = kind == 2; var c = RandomCell();
                for (int i = 0; i < n; i++)
                {
                    if (!same) c = RandomCell();
                    // some of the burst are quick drags to a neighbour, the rest taps
                    if (_rng.Next(0, 3) == 0 && c.x + 1 < RulesEngine.COLS) AddDrag(CellScreen(c.x, c.y), CellScreen(c.x + 1, c.y), _rng.Next(1, 4));
                    else AddTap(CellScreen(c.x, c.y), _rng.Next(0, 2));
                    AddGap(_rng.Next(0, 3));
                }
                what = same ? $"one cell ({c.x},{c.y}) x{n}" : $"board x{n}";
                Note("MASH BOARD", what); return;
            }
            for (int i = 0; i < n; i++)
            {
                AddTap(new Vector3((float)_rng.NextDouble() * Screen.width, (float)_rng.NextDouble() * Screen.height, 0f), _rng.Next(0, 2));
                AddGap(_rng.Next(0, 3));
            }
            Note("MASH ANYWHERE", $"x{n}");
        }

        private Vector3 Jitter(Vector3 p, float px) => new Vector3(p.x + ((float)_rng.NextDouble() * 2f - 1f) * px, p.y + ((float)_rng.NextDouble() * 2f - 1f) * px, 0f);

        /// <summary>Screen position of a UI element (any canvas mode) or a world object.</summary>
        private static bool ScreenPointOf(Transform t, out Vector3 p)
        {
            p = default;
            var rt = t as RectTransform; var canvas = t.GetComponentInParent<Canvas>();
            if (rt != null && canvas != null)
            {
                var root = canvas.rootCanvas;
                if (!root.enabled) return false;
                Camera cam = root.renderMode == RenderMode.ScreenSpaceOverlay ? null : (root.worldCamera != null ? root.worldCamera : Camera.main);
                Vector2 sp = RectTransformUtility.WorldToScreenPoint(cam, rt.TransformPoint(rt.rect.center));
                p = new Vector3(sp.x, sp.y, 0f); return true;
            }
            if (Camera.main == null) return false;
            p = Camera.main.WorldToScreenPoint(t.position); p.z = 0f; return true;
        }

        private void Note(string kind, string what)
        {
            _gestures++;
            _gestureCounts.TryGetValue(kind, out int n); _gestureCounts[kind] = n + 1;
            bool busy = MatchController.Instance != null && MatchController.Instance.IsProcessing;
            string cover = Covered(out string cn) ? $", under {cn}" : "";
            string line = $"{Clock(Time.unscaledTime)} f{Time.frameCount}  {kind}  {what}   [board {(busy ? "BUSY" : "idle")}{cover}]";
            _recent.Add(line); if (_recent.Count > 20) _recent.RemoveAt(0);
            if (_inputLog.Length < 4000000) _inputLog.AppendLine(line);
        }

        private string Clock(float t) { int s = Mathf.Max(0, (int)(t - _t0)); return $"{s / 60:00}:{s % 60:00}"; }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // WATCHING
        /// <summary>Something that should stop board input is on screen.</summary>
        private static bool Covered(out string name)
        {
            name = "";
            if (LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard) name = "the home screen";
            else if (SurvivalManager.Instance != null && SurvivalManager.Instance.IsOverlayPaused) name = "a full-screen card";
            else if (LevelController.Instance != null && LevelController.Instance.IsInputLocked) name = "a level-end card";
            else if (SurvivalManager.AwaitingNextLevel) name = "the level-won wait";
            return name.Length > 0;
        }

        private void Watch(float now)
        {
            bool covered = Covered(out _);
            if (covered && !_wasCovered) _coveredSince = now;
            _wasCovered = covered;

            var sm = SurvivalManager.Instance;
            if (CoinWallet.Balance < 0) AddFault("COUNTER", $"coin balance is negative ({CoinWallet.Balance})", "");
            if (sm != null && s_movesUsedField != null && SurvivalManager.MoveLimitMode)
            {
                int budget = sm.CurrentStageMoveBudget, used = (int)s_movesUsedField.GetValue(sm);
                if (used > budget + 1) AddFault("COUNTER", $"moves used ({used}) is past the level's budget ({budget})", "");
            }

            if (now < _nextSlowCheck) return;
            _nextSlowCheck = now + 0.5f;

            if (HeartsManager.Current > 0) _zeroLivesSince = 0f;
            else
            {
                // keep the monkey playing; in PLAY BADLY give the game's own no-lives screens a while first
                if (_zeroLivesSince <= 0f) _zeroLivesSince = now;
                if (!PLAY_BADLY || now - _zeroLivesSince >= BADLY_NO_LIVES_SECONDS) { HeartsManager.GrantFull(); _heartRefills++; _zeroLivesSince = 0f; }
            }

            if (_burnAt > 0f && now >= _burnAt && sm != null && s_movesUsedField != null)
            {
                if (SurvivalManager.AwaitingNextLevel || !SurvivalManager.MoveLimitMode) _burnAt = 0f;
                else if (!Covered(out _) && BoardExists())
                {
                    int leave = _rng.Next(1, 5), budget = sm.CurrentStageMoveBudget, used = (int)s_movesUsedField.GetValue(sm);
                    if (budget - leave > used) { s_movesUsedField.SetValue(sm, budget - leave); _burns++; Debug.Log($"[Monkey] PLAY BADLY: left itself {leave} move(s) on this level"); }
                    _burnAt = 0f;
                }
            }

            // STUCK: nothing about the game has changed although the monkey keeps tapping
            string fp = Fingerprint();
            if (fp != _fingerprint) { _fingerprint = fp; _lastChange = now; _stuckReported = false; _triedClose = false; }
            else if (!_stuckReported && !_triedClose && now - _lastChange >= STUCK_SECONDS * 0.6f && PressClose())
            {
                // loitering inside a card that changes nothing the fingerprint sees (Settings tabs / toggles): press its close
                // button once before calling it stuck. If nothing changes after that either, the STUCK fault below still fires.
                _triedClose = true;
            }
            else if (!_stuckReported && now - _lastChange >= STUCK_SECONDS)
            {
                _stuckReported = true;
                bool tutorial = TutorialManager.GateClosed || TutorialBeats.BlocksInput;
                AddFault("STUCK", tutorial ? $"nothing changed for {STUCK_SECONDS:F0}s - a TUTORIAL lesson is waiting for its taught move (the monkey cannot follow lessons; probably not a bug)"
                                           : $"nothing changed for {STUCK_SECONDS:F0}s while the monkey kept tapping", "");
            }
            else if (_stuckReported && now - _lastChange >= STUCK_SECONDS + STUCK_GIVE_UP_SECONDS) { StopRun("game stayed stuck - left as it is so you can look"); return; }

            // two cards at once
            string cards = VisibleCards();
            if (cards != _multiCards) { _multiCards = cards; _multiSince = now; }
            else if (cards.Contains("+") && now - _multiSince >= MULTI_CARD_SECONDS) AddFault("CHECK", $"two cards visible together for {MULTI_CARD_SECONDS:F1}s+: {cards}", "");
        }

        private readonly StringBuilder _fp = new StringBuilder();
        private string Fingerprint()
        {
            _fp.Length = 0;
            var sm = SurvivalManager.Instance;
            if (sm != null) _fp.Append(sm.CurrentStageIndex).Append('|').Append(sm.CurrentStageMovesRemaining).Append('|');
            _fp.Append(CoinWallet.Balance).Append('|').Append(HeartsManager.Current).Append('|');
            var rules = RulesEngine.Instance;
            if (rules != null && BoardExists())
            {
                int h = 17;
                for (int x = 0; x < RulesEngine.COLS; x++)
                    for (int y = 0; y < RulesEngine.ROWS; y++) { var c = rules.GetCell(x, y); h = unchecked(h * 31 + (c != null ? c.Letter + c.SpecialKind * 131 : 7)); }
                _fp.Append(h).Append('|');
            }
            // which screens are up
            int ch = 0;
            foreach (var cv in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (cv.isRootCanvas && cv.enabled) { var gr = cv.GetComponent<GraphicRaycaster>(); if (gr != null && gr.enabled) ch = unchecked(ch + cv.name.GetHashCode() * (cv.sortingOrder + 3)); }
            _fp.Append(ch);
            return _fp.ToString();
        }

        private static string VisibleCards()
        {
            string s = "";
            foreach (var t in s_cardTypes)
            {
                var inst = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as MonoBehaviour;
                if (inst == null) continue;
                var cv = t.GetField("_canvas", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(inst) as Canvas;
                if (cv == null || !cv.enabled || !cv.gameObject.activeInHierarchy) continue;
                var grp = cv.GetComponent<CanvasGroup>(); if (grp != null && grp.alpha < 0.05f) continue;
                s += (s.Length > 0 ? " + " : "") + t.Name;
            }
            return s;
        }

        private static string Gates()
        {
            var hand = HandManager.Instance; var sm = SurvivalManager.Instance; var mc = MatchController.Instance; var grid = GridManager.Instance;
            int level = sm != null ? LevelMapPanel.RunLevel(sm.CurrentStageIndex) : -1;
            return $"level {level}, moves left {(sm != null ? sm.CurrentStageMovesRemaining : -1)}, coins {CoinWallet.Balance}, hearts {HeartsManager.Current}, timeScale {Time.timeScale:F2} | " +
                   $"board accepts input={(hand != null && hand.IsInteractable)}, resolving a move={(mc != null && mc.IsProcessing)}, card pause={(sm != null && sm.IsOverlayPaused)}, " +
                   $"level-end lock={(LevelController.Instance != null && LevelController.Instance.IsInputLocked)}, level won wait={SurvivalManager.AwaitingNextLevel}, " +
                   $"home covers board={(LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard)}, board sliding in={(grid != null && grid.BoardEntryInProgress)}, " +
                   $"booster aiming={(BoosterManager.Instance != null && BoosterManager.Instance.AimMode)}, tutorial gate={TutorialManager.GateClosed}, lesson tip={TutorialBeats.BlocksInput}, " +
                   $"splash={MechanicSplash.Showing}, cards=[{VisibleCards()}]";
        }

        private void OnLog(string cond, string stack, LogType type)
        {
            if (!Running || string.IsNullOrEmpty(cond) || cond.StartsWith("[Monkey]")) return;
            float now = Time.unscaledTime;
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                AddFault("ERROR", FirstLine(cond, 300), TrimStack(stack));
                return;
            }
            if (type == LogType.Warning)
            {
                string k = Digitless(FirstLine(cond, 140));
                _warnings.TryGetValue(k, out int n); _warnings[k] = n + 1;
                return;
            }
            if (cond.StartsWith("[Analytics] "))
            {
                int bar = cond.IndexOf(" |", StringComparison.Ordinal);
                string ev = (bar > 12 ? cond.Substring(12, bar - 12) : cond.Substring(12)).Trim();
                if (ev == "level_start")
                {   // 11-17 (Spencer: "if it cant reset progress then it will only play two levels"): reaching the level after the
                    // last one used to leave the monkey tapping the "coming soon" card for the rest of the run.
                    int li = cond.IndexOf(" level=", StringComparison.Ordinal);
                    if (li > 0 && int.TryParse(cond.Substring(li + 7).Trim().Split(' ')[0], out int lv) && ChapterEndCard.CONTENT_END_LEVEL > 0 && lv > ChapterEndCard.CONTENT_END_LEVEL) _loopJumpAt = now + 1.5f;
                }
                if (ev == "level_start") { _levelsStarted++; if (PLAY_BADLY && _rng.NextDouble() < BADLY_SHARE) _burnAt = now + 1.5f; }
                else if (ev == "stage_clear") _levelsCleared++;
                else if (ev == "level_failed" || ev == "stage_fail") _levelsFailed++;
                else if (ev == "booster_activate") { _boosters++; UnderCover("a booster was used", now); }
                if (s_oneOffEvents.Contains(ev))
                {
                    // same event + same details (minus the timestamp) twice in a row, almost at once
                    string payload = bar > 0 ? cond.Substring(bar) : "";
                    int tAt = payload.IndexOf("t=", StringComparison.Ordinal);
                    if (tAt >= 0) { int sp = payload.IndexOf(' ', tAt); payload = sp > 0 ? payload.Substring(sp) : ""; }
                    string key = ev + payload;
                    if (_lastEventAt.TryGetValue(key, out float last) && now - last < DOUBLE_FIRE_SECONDS)
                        AddFault("DOUBLE-FIRE", $"'{ev}' fired twice within {now - last:F2}s", FirstLine(cond, 200));
                    _lastEventAt[key] = now;
                }
                return;
            }
            if (cond.StartsWith("[MoveLimit] continue:"))
            {
                if (_lastContinueAt > 0f && now - _lastContinueAt < 2f) AddFault("DOUBLE-FIRE", "Play On gave its moves twice within 2s (a double tap bought two continues)", "");
                _lastContinueAt = now;
            }
            else if (cond.StartsWith("[DragSwap]") && cond.Contains("swapped")) { _swaps++; UnderCover("a swap was accepted", now); }
            else if (cond.StartsWith("[Swipe] '") && cond.Contains("ARMED")) { _swipes++; UnderCover("a swiped word was accepted", now); }
        }

        private void UnderCover(string what, float now)
        {
            if (_wasCovered && now - _coveredSince >= COVER_GRACE && Covered(out string name))
                AddFault("PASS-THROUGH", $"{what} while {name} had been covering the board for {now - _coveredSince:F1}s", "");
        }

        private void AddFault(string kind, string msg, string detail)
        {
            string key = kind + ":" + Digitless(msg);
            if (_faultByKey.TryGetValue(key, out var f)) { f.count++; return; }
            var sm = SurvivalManager.Instance;
            f = new Fault { kind = kind, msg = msg, detail = detail, at = Time.unscaledTime, count = 1, inputs = new List<string>(_recent),
                            level = sm != null ? LevelMapPanel.RunLevel(sm.CurrentStageIndex) : -1 };
            try { f.state = Gates(); } catch (Exception ex) { f.state = "(state unavailable: " + ex.Message + ")"; }
            if (_shots < MAX_SHOTS)
            {
                _shots++;
                f.shot = $"fault_{_stamp}_{_faults.Count + 1:00}.png";
                try { ScreenCapture.CaptureScreenshot(Path.Combine(Folder, f.shot)); } catch { f.shot = null; }
            }
            _faults.Add(f); _faultByKey[key] = f;
            Debug.Log($"[Monkey] FAULT #{_faults.Count} {kind}: {msg}");
        }

        private static string FirstLine(string s, int max) { int nl = s.IndexOf('\n'); if (nl >= 0) s = s.Substring(0, nl); return s.Length > max ? s.Substring(0, max) : s; }
        private static string TrimStack(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Split('\n'); var sb = new StringBuilder();
            for (int i = 0; i < lines.Length && i < 6; i++) if (lines[i].Trim().Length > 0) sb.Append("      ").AppendLine(lines[i].Trim());
            return sb.ToString();
        }
        /// <summary>Same message with different numbers = the same fault.</summary>
        private static string Digitless(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) if (!char.IsDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        // REPORT
        private void WriteReport(bool final)
        {
            try
            {
                float ran = Time.unscaledTime - _t0;
                var sb = new StringBuilder();
                sb.AppendLine($"MONKEY TEST  {_stamp}   seed {_seed}   ran {ran / 60f:F1} min   {(final ? "ended: " + _endReason : "STILL RUNNING (partial report)")}");
                sb.AppendLine($"Screen {Screen.width}x{Screen.height}.  One fake finger, real game, Play mode.{(PLAY_BADLY ? "  PLAY BADLY mode." : "")}");
                sb.AppendLine();
                sb.Append($"INPUTS  {_gestures} gestures:");
                foreach (var kv in _gestureCounts) sb.Append($"  {kv.Key} {kv.Value}");
                sb.AppendLine();
                sb.AppendLine($"GAME    levels started {_levelsStarted}, cleared {_levelsCleared}, failed {_levelsFailed};  swaps accepted {_swaps}, swiped words {_swipes}, boosters used {_boosters};  hearts refilled by the monkey {_heartRefills}x" + (PLAY_BADLY ? $";  PLAY BADLY: cut {_burns} level(s) down to a few moves" : ""));
                sb.AppendLine($"FRAMES  worst {_worstFrame * 1000f:F0} ms, {_spikes} frame(s) over 250 ms, {_frames} frames ({(ran > 0 ? _frames / ran : 0):F0} fps average)");
                sb.AppendLine();
                var byKind = new Dictionary<string, int>();
                foreach (var f in _faults) { byKind.TryGetValue(f.kind, out int n); byKind[f.kind] = n + 1; }
                sb.Append($"FAULTS  {_faults.Count} different");
                foreach (var kv in byKind) sb.Append($"   {kv.Key} {kv.Value}");
                sb.AppendLine();
                if (_faults.Count == 0) sb.AppendLine("        none found in this run (that is not proof there are none - run it again, longer, with another seed)");
                for (int i = 0; i < _faults.Count; i++)
                {
                    var f = _faults[i];
                    sb.AppendLine();
                    sb.AppendLine($"--- {i + 1}. [{f.kind}] {f.msg}");
                    sb.AppendLine($"    first at {Clock(f.at)}, level {f.level}, seen {f.count}x{(f.shot != null ? ", screenshot " + f.shot : "")}");
                    if (!string.IsNullOrEmpty(f.detail)) sb.AppendLine(f.detail.TrimEnd());
                    sb.AppendLine($"    state: {f.state}");
                    sb.AppendLine("    last inputs before it:");
                    int from = Mathf.Max(0, f.inputs.Count - 12);
                    for (int k = from; k < f.inputs.Count; k++) sb.AppendLine("      " + f.inputs[k]);
                }
                if (_warnings.Count > 0)
                {
                    sb.AppendLine(); sb.AppendLine("WARNINGS in the log (not counted as faults), most frequent first:");
                    var list = new List<KeyValuePair<string, int>>(_warnings); list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    for (int i = 0; i < list.Count && i < 15; i++) sb.AppendLine($"    {list[i].Value,5}x  {list[i].Key}");
                }
                sb.AppendLine();
                sb.AppendLine("SAVE DATA: the monkey spends coins, changes progress and may flip settings.");
                sb.AppendLine(_prefsBackup.Length > 0 ? $"    Copy from before this run: {_prefsBackup}\n    To restore: quit Unity, copy that file over ~/Library/Preferences/unity.{Application.companyName}.{Application.productName}.plist, run 'killall cfprefsd', reopen Unity."
                                                      : "    (no PlayerPrefs backup was made)");
                sb.AppendLine($"Every input of this run: {Path.Combine(Folder, "monkey_inputs_latest.txt")}");
                File.WriteAllText(Path.Combine(Folder, "monkey_latest.txt"), sb.ToString());
                File.WriteAllText(Path.Combine(Folder, "monkey_inputs_latest.txt"), _inputLog.ToString());
                if (final) File.WriteAllText(Path.Combine(Folder, $"monkey_{_stamp}.txt"), sb.ToString());
            }
            catch (Exception ex) { Debug.Log($"[Monkey] report write failed: {ex.Message}"); }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────────
        private static Texture2D s_dot;
        private void OnGUI()
        {
            if (!Running) return;
            if (s_dot == null) { s_dot = new Texture2D(1, 1); s_dot.SetPixel(0, 0, Color.white); s_dot.Apply(); }
            var old = GUI.color;
            // the fake finger, so you can see what it is doing
            GUI.color = _fingerDown ? new Color(1f, 0.2f, 0.2f, 0.85f) : new Color(1f, 1f, 1f, 0.35f);
            float d = Screen.width * 0.03f;
            GUI.DrawTexture(new Rect(_lastPos.x - d * 0.5f, Screen.height - _lastPos.y - d * 0.5f, d, d), s_dot);
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height * 0.022f), s_dot);
            GUI.color = Color.white;
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.Max(10, (int)(Screen.height * 0.014f)), alignment = TextAnchor.MiddleCenter };
            GUI.Label(new Rect(0, 0, Screen.width, Screen.height * 0.022f), $"MONKEY  seed {_seed}  {(_endAt - Time.unscaledTime) / 60f:F1} min left  faults {_faults.Count}  (Esc = stop)", style);
            GUI.color = old;
        }
    }

    /// <summary>Feeds the monkey's fake finger to Unity UI (StandaloneInputModule.inputOverride), so buttons, toggles and scroll
    /// views receive it through the normal EventSystem path.</summary>
    public class MonkeyUIInput : BaseInput
    {
        public override Vector2 mousePosition => WDInput.mousePosition;
        public override bool GetMouseButtonDown(int button) => WDInput.GetMouseButtonDown(button);
        public override bool GetMouseButtonUp(int button) => WDInput.GetMouseButtonUp(button);
        public override bool GetMouseButton(int button) => WDInput.GetMouseButton(button);
        public override bool mousePresent => true;
        public override bool touchSupported => false;
        public override int touchCount => 0;
        public override Vector2 mouseScrollDelta => Vector2.zero;
        public override float GetAxisRaw(string axisName) => 0f;
        public override bool GetButtonDown(string buttonName) => false;
    }
}
#endif
