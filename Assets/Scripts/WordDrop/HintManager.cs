using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Candy Crush / Royal Match style idle hint system.
    ///
    /// After N seconds of inactivity, finds the best-scoring drop the player
    /// could make (any hand card × any column) and gently animates:
    ///   - The recommended hand card (subtle scale pulse)
    ///   - The existing board tiles that would form the resulting word
    ///     (glow + scale pulse — escalates at second threshold)
    ///
    /// Any player input resets the idle timer and clears active hint visuals.
    ///
    /// Lower priority than the active DropPreview (which fires during drag).
    /// These never overlap — DropPreview = active interaction, HintManager =
    /// player has been inactive for 15+s.
    /// </summary>
    public class HintManager : MonoBehaviour
    {
        public static HintManager Instance { get; private set; }

        // ── Tuning ──────────────────────────────────────────────────────────────
        private const float IDLE_THRESHOLD = 10f;      // seconds idle before hint fires (CC ~5s; word puzzle wants more think time)
        private const float LOOP_INTERVAL  = 3.5f;     // re-find + re-animate every N seconds

        // Squash-and-stretch hop — CC-style cartoony jump animation.
        // Sequence: rest → stretch up + lift → hang briefly → fall + restore →
        // squash on land → bounce back to rest → pause → repeat.
        // CC-style burst rhythm: 6 fast hops in succession, then long pause,
        // then repeat. Single hops feel weak — the burst is what catches the eye.
        private const int   HOPS_PER_BURST    = 6;
        private const float BURST_PAUSE_DURATION = 2.0f; // pause between bursts

        // Each individual hop — fast & snappy
        private const float HOP_AMPLITUDE     = 0.18f;  // visible lift — was 0.08, too subtle
        private const float HOP_UP_DURATION   = 0.12f;  // fast lift
        private const float HOP_HANG_DURATION = 0.02f;  // brief apex (snappier — was 0.03, stretch held too long)
        private const float HOP_DOWN_DURATION = 0.10f;  // fast fall
        private const float HOP_SQUASH_DURATION = 0.05f; // quick landing squash
        private const float HOP_REST_DURATION = 0.08f;  // snap back to rest
        private const float HOP_GAP_DURATION  = 0.04f;  // tiny gap between hops in burst
        // Cartoon squash-and-stretch (Royal Match feel): a MODEST stretch up,
        // then a PRONOUNCED wide squash on landing (the impact beat). 2026-06-02:
        // stretch eased back (1.06→1.04) and landing squash widened a lot
        // (1.03/0.97 → 1.14/0.87) so the "hits the surface" moment reads as a
        // proper cartoon squash instead of a barely-there 3%.
        private const float STRETCH_Y         = 1.04f;
        private const float STRETCH_X         = 0.97f;
        private const float SQUASH_Y          = 0.87f;
        private const float SQUASH_X          = 1.14f;

        // Subtle color tint synced to hop. Brightens at apex, returns at rest.
        // Kept low so the scene's bloom doesn't blow out letters.
        private static readonly Color HINT_GLOW_LOW  = new Color(1f, 1f, 1f, 1f);
        // 2026-06-03 Spencer: glint value set to 1.14 (apex brighten). Still under
        // the 1.30 bloom line, so it brightens without blowing out.
        private static readonly Color HINT_GLOW_HIGH = new Color(1.14f, 1.14f, 1.14f, 1f);

        // PlayerPrefs key for the off/on toggle
        public const string PREF_KEY = "hint_enabled";

        // ── State ───────────────────────────────────────────────────────────────
        private float _idleTime = 0f;
        private float _nextLoopTime = -1f;
        private int   _activeStage = 0;             // 0 = none, 1 = subtle, 2 = full
        private int   _bestCardIndex = -1;
        private Vector3 _cardBasePosition = Vector3.zero;
        private Vector3 _cardBaseScale = Vector3.one;
        private Sequence _cardSequence;
        private List<Tile> _highlightedTiles = new List<Tile>();
        private List<Vector3> _highlightedTileBasePositions = new List<Vector3>();
        private List<Vector3> _highlightedTileBaseScales = new List<Vector3>();
        private List<Sequence> _tileSequences = new List<Sequence>();

        // ── Lifecycle ───────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("HintManager");
                go.AddComponent<HintManager>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>2026-09-08 Spencer: MASTER OFF for the whole word-hint system while
        /// the swap pivot is being felt out — the marching-ants outline, the idle jam hint
        /// and the lantern trace all read through here. Set true to bring them back;
        /// the per-user settings toggle still works underneath.</summary>
        public static bool HINTS_DISABLED = false;   // 2026-10-22: back ON for the swap game (Spencer: idle hint system)
        /// <summary>Seconds idle before the SWAP hint shows (full board). Royal Match is ~5s; a word game wants a little
        /// more think time.</summary>
        public static float SWAP_IDLE_SECONDS = 5f;           // 2026-10-22: 6 -> 15, a SAFETY NET for the stuck, not a move-by-move crutch. 11-04: 15 -> 8, 11-06: 8 -> 5 (L1-20 recording: static boards 10-13.5 s; RM nudges at ~4 s) (Spencer, after a full L1-20 run at ~12 s/move: "its mostly word hunting, finding no words and then waiting for the hint system to kick in" - the WAIT was the boredom; RM hints at ~5 s)
        public static float SWAP_OUTLINE_EXTRA_SECONDS = 5f;  // free hint: lean only, then the word outline this much later. 11-04: 10 -> 5 (full word shown at 13 s idle, was 25 s)
        // 11-19 HINT BY STRUGGLE (Spencer's phone runs 11-18: with hints he cleared levels in 5-8 moves of 12-24, L10-12 in 2-3;
        // with hints off he went over budget on L1 and L7 - the 5 s hint was playing the game for him. He OK'd: 5 s on the first
        // levels (teaching), 10 s after that (a looking player finds their own word first), back to 5 s in the last few moves.
        // The quick nets for real struggle are unchanged (3 missed swaps -> 1.5 s, stale board -> 4 s).
        public static bool  HINT_BY_STRUGGLE = true;
        public static int   HINT_TEACH_LEVELS = 8;        // levels 1..this keep SWAP_IDLE_SECONDS. 11-19: 3 -> 8 (through the first booster level). Spencer on the TestFlight build: "considerably harder" - phone log: slow hint from L4 = 61% of moves used (fast hint 48%), 2 fails in 23 levels incl. LEVEL 4; an early fail is the churn risk, so the opening stays gentle
        public static float HINT_LATER_SECONDS = 10f;     // idle wait from the next level on
        public static int   HINT_LAST_MOVES = 3;          // this many moves left or fewer: back to SWAP_IDLE_SECONDS
        public static float SwapIdleNow
        {
            get
            {
                var sm = SurvivalManager.Instance;
                if (!HINT_BY_STRUGGLE || sm == null) return SWAP_IDLE_SECONDS;
                if (LevelMapPanel.DisplayNum(sm.CurrentStageIndex) <= HINT_TEACH_LEVELS) return SWAP_IDLE_SECONDS;
                if (SurvivalManager.MoveLimitMode && sm.CurrentStageMovesRemaining <= HINT_LAST_MOVES) return SWAP_IDLE_SECONDS;
                return Mathf.Max(SWAP_IDLE_SECONDS, HINT_LATER_SECONDS);
            }
        }
        private float IdleThreshold => SurvivalManager.FullBoardActive
            ? (HandManager.NoWordStreak >= FISHING_SWAPS ? FISHING_HINT_SECONDS : _staleNow ? STALE_HINT_SECONDS : SwapIdleNow)
            : IDLE_THRESHOLD;
        /// <summary>FISHING HINT (2026-10-23: Spencer's run — 54% of swaps made no word although the bot measured ~17 common-word
        /// swaps on every board: the words were THERE, he couldn't see them). After FISHING_SWAPS no-word swaps in a row the
        /// goal-aware hint shows after FISHING_HINT_SECONDS instead of 15s.</summary>
        // 2026-10-30: with bounce-back a miss is FREE, so 2 misses -> a hint in 1.5s showed a word almost on demand (and made the
        // Spyglass pointless). Now it takes a real fishing run and a few seconds' wait before the nudge.
        // 10-30 (Spencer: "is the hint supposed to take so long to fire?"): he missed ~20 swaps on L6 and it never came - each swap
        // restarted the 4s wait, and the goal shuffle reset the miss count at 3. Now 3 misses + a 1.5s pause.
        public static int   FISHING_SWAPS = 3;
        public static float GOAL_HELP_WAIT_MAX = 20f;   // safety: never hold the free hint back longer than this
        public static float FISHING_HINT_SECONDS = 1.5f;

        // ── STALE BOARD (2026-10-23 Spencer: "when you have a stale board and nothing looks available thats when i feel the fun
        // trail off"). Once per move, after the board settles: RulesEngine.TryStaleRescue re-letters one plain tile if fewer
        // than STALE_MIN common-word swaps exist; if the board is STILL stale, the hint comes after STALE_HINT_SECONDS. ──
        public static float STALE_CHECK_DELAY = 0.6f, STALE_HINT_SECONDS = 4f;
        private bool _staleChecked, _staleNow;
        public static int NUDGE_AFTER_MOVES = 3;
        private int _stuckMoves, _lastPieces = -1;
        private void StaleCheck()
        {
            var rules = RulesEngine.Instance; if (rules == null || !SurvivalManager.FullBoardActive) return;
            if (rules.TryStaleRescue(out var c, out var L, out int before))
            {
                var t = GridManager.Instance != null ? GridManager.Instance.GetTile(c.x, c.y) : null;
                if (t != null)
                {
                    t.SetLetter(L);
                    t.transform.DOPunchScale(Vector3.one * 0.22f, 0.35f, 6, 0.6f).SetUpdate(true);
                    GameAudio.Instance?.PlayScoreTick(1.3f);
                }
                _hintCache = null;
                WDLog.Log($"[Stale] board had {before} common-word swap(s) — ({c.x},{c.y}) re-lettered to '{L}'");
            }
            // BOOSTER NUDGE (2026-10-23, Spencer OK'd): stuck on the last goal pieces for NUDGE_AFTER_MOVES moves -> the first
            // unlocked booster with charges pulses (RM pattern; with no charges left it's the moment a purchase makes sense).
            int pieces = RulesEngine.GoalPiecesLeft();
            if (pieces > 0 && pieces <= RulesEngine.ENDGAME_PIECES)
            {
                if (pieces < _lastPieces || _lastPieces < 0) _stuckMoves = 0; else _stuckMoves++;
                if (_stuckMoves >= NUDGE_AFTER_MOVES) { BoosterHUDSlot.Instance?.NudgeBooster(); _stuckMoves = 0; }
            }
            else _stuckMoves = 0;
            _lastPieces = pieces;
            _staleNow = rules.CountCommonWordSwaps() < RulesEngine.STALE_MIN;
            if (_staleNow) WDLog.Log("[Stale] still stale after the rescue — quick hint");
        }

        public static bool IsEnabled()
        {
            if (HINTS_DISABLED) return false;
            // Default ON; user can disable in settings.
            return PlayerPrefs.GetInt(PREF_KEY, 1) != 0;
        }

        public static void SetEnabled(bool enabled)
        {
            PlayerPrefs.SetInt(PREF_KEY, enabled ? 1 : 0);
            PlayerPrefs.Save();
            if (!enabled && Instance != null) Instance.ResetIdleTimer();
        }

        /// <summary>Call from input handlers — taps, drags, swaps, menu opens.</summary>
        public void ResetIdleTimer()
        {
            _staleChecked = false;
            _strongHint = false; _swapOnly = false; _hintCache = null; _swipeCache = null;   // a new board state after the player's move = a new hint later
            _idleTime = 0f;
            _nextLoopTime = -1f;
            if (_activeStage > 0) ClearVisuals();
        }

        /// <summary>MVP P5 — fire a hint immediately, regardless of idle timer.
        /// Used by the Lantern Trace booster (manual hint as a roguelite pick).</summary>
        public void ForceShowHint() => ForceShowHint(false);
        /// <summary>strong = the Spyglass booster: BEST move, outline at once, until the player touches the board.</summary>
        public void ForceShowHint(bool strong) => ForceShowHint(strong, false);
        /// <summary>swapOnly (10-31): the goal-area shuffle's follow-up hint must point at the goal word the shuffle made - the
        /// swipe preference had it show an unrelated on-board word ("GEAR") a split second after the shuffle.</summary>
        public void ForceShowHint(bool strong, bool swapOnly)
        {
            _strongHint = strong; _swapOnly = swapOnly; _hintCache = null; _swipeCache = null;
            ClearVisuals();
            _nextLoopTime = -1f;
            RefreshHint();
        }

        // ── Forced hint (tutorial) ────────────────────────────────────────────────
        // The gating layer pins the idle hint to the CURRENT scripted move, so the marching-ants
        // outline follows CAT → PIT → LAND → NOT instead of auto-picking the highest-scoring move.
        // Still fires on the normal idle timer; it just shows OUR target. 2026-06-25 Spencer.
        private bool     _forcedActive;
        private BestMove _forcedMove;
        public void SetForcedHint(int cardIndex, int col, List<Vector2Int> wordCells)
        {
            _forcedActive = true;
            _forcedMove = new BestMove { CardIndex = cardIndex, Col = col, WordCells = wordCells, Score = 1 };
        }
        public void ClearForcedHint()
        {
            if (!_forcedActive) return;
            _forcedActive = false;
            ClearVisuals();
        }

        /// <summary>
        /// Call when the board state changes (row rise, cascade settle, etc.)
        /// but the player has NOT interacted. Clears stale visuals so the hint
        /// can re-find a move on the new board state, but preserves idle time
        /// so it resumes quickly (after a brief settle delay).
        /// </summary>
        public void OnBoardChanged()
        {
            _hintCache = null; _swipeCache = null;
            if (_activeStage > 0) ClearVisuals();
            // Delay next re-fire so the board animation (row rise, cascade)
            // has time to settle before we cache new base positions.
            _nextLoopTime = Time.unscaledTime + 1.5f;
        }

        private void Update()
        {
            if (!IsEnabled())
            {
                GateDiag(HINTS_DISABLED ? "HINTS_DISABLED" : "player setting hint_enabled=0");
                // 11-17 (monkey 436301 + Spencer's shot: marching ants left around letters that had since fallen in). With Hints OFF
                // this returned before anything below ran, but the SPYGLASS still shows its word (it is a booster) - so that hint
                // was never taken down: the outline stayed and its leaning tile kept being pulled back to its old cell (the
                // checker's MISPLACED x4). A showing hint still goes away on a touch or when the board starts moving.
                if (_activeStage > 0 && (WDInput.touchCount > 0 || WDInput.GetMouseButton(0) || !GameplayActive())) ResetIdleTimer();
                return;
            }
            if (!GameplayActive()) { ResetIdleTimer(); return; }

            // Defensive: any input resets the timer. Picks up taps/drags even
            // if some code path forgot to call ResetIdleTimer explicitly.
            if (WDInput.touchCount > 0 || WDInput.GetMouseButton(0))
            {
                ResetIdleTimer();
                return;
            }

            _idleTime += Time.deltaTime;

            if (!_staleChecked && _idleTime >= STALE_CHECK_DELAY) { _staleChecked = true; StaleCheck(); }

            // ONE THING AT A TIME (10-31): the fishing hint and the goal-area shuffle both trigger on 3 misses + a pause, so
            // the player saw a lean, then letters hopping, then a different hint inside a second. While the shuffle is
            // pending (goal area dead, help not used this level) the free hint waits; the shuffle shows its own hint after.
            if (_activeStage == 0 && !_strongHint && BoardShuffle.GoalHelpPending && _idleTime < GOAL_HELP_WAIT_MAX) return;

            // Time to fire / refresh hint?
            if (_idleTime >= IdleThreshold && Time.unscaledTime >= _nextLoopTime)
            {
                RefreshHint();
                _nextLoopTime = Time.unscaledTime + LOOP_INTERVAL;
            }
        }

        // ── Gating ──────────────────────────────────────────────────────────────

        // TEMP DIAG 2026-10-22 (Spencer: "i dont think the hint system is working" — the log had ZERO [Hint] lines, so the
        // finder never ran). Logs WHY the hint is blocked, at most every 4s. Strip once the hint is verified.
        public static bool HINT_GATE_DIAG = true;
        private float _nextGateLog;
        private string _lastGateReason;
        private void GateDiag(string why)
        {
            if (!HINT_GATE_DIAG || Time.unscaledTime < _nextGateLog) return;
            if (why == _lastGateReason && Time.unscaledTime < _nextGateLog + 8f) return;
            _nextGateLog = Time.unscaledTime + 4f; _lastGateReason = why;
            WDLog.Log($"[HintGate] blocked: {why} (idle={_idleTime:F1}s)");
        }

        /// <summary>Full-board gate (2026-10-22): the drop-game checks (match active / human's turn / IsInteractable) were
        /// never designed for the swap game. Ask what matters here: a run in play, no overlay or modal, board settled.</summary>
        private bool SwapGameplayActive()
        {
            var sm = SurvivalManager.Instance;
            if (sm == null || !SurvivalManager.IsSurvivalMode) { GateDiag("no survival run"); return false; }
            if (SurvivalManager.AwaitingNextLevel) { GateDiag("level won / awaiting next"); return false; }
            if (TutorialBeats.BlocksInput && !TutorialBeats.HintBeatUp) { GateDiag("tutorial beat"); return false; }   // 11-20: the hint lesson keeps its own hint leaning
            if (sm.IsOverlayPaused) { GateDiag("overlay paused"); return false; }
            if (LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard) { GateDiag("map/home on screen"); return false; }
            if (StageClearModal.Instance != null && StageClearModal.Instance.IsVisible) { GateDiag("stage clear modal"); return false; }
            if (ContinueModal.Instance != null && ContinueModal.Instance.IsShowing) { GateDiag("continue modal"); return false; }
            if (GridManager.Instance == null || RulesEngine.Instance == null) { GateDiag("no grid/rules"); return false; }
            if (RulesEngine.Instance.IsResolving) { GateDiag("rules resolving"); return false; }
            var hm = HandManager.Instance;
            if (hm != null && (hm.IsResolvingChain || HandManager.BonusBlastRunning)) { GateDiag("chain / bonus running"); return false; }
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) { GateDiag("match processing"); return false; }
            if (BoosterManager.CascadeBusy) { GateDiag("booster resolving"); return false; }   // 11-17: a booster's fall + cascade is the board moving
            return true;
        }

        private bool GameplayActive()
        {
            if (SurvivalManager.FullBoardActive) return SwapGameplayActive();
            // Only run while a match is active and it's the human's turn.
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive) return false;
            if (MatchController.Instance.CurrentPlayer != MatchController.PLAYER_HUMAN) return false;
            if (HandManager.Instance != null && !HandManager.Instance.IsInteractable) return false;
            if (GridManager.Instance == null || RulesEngine.Instance == null) return false;
            // Stage-clear modal up → suppress hints so background tiles don't
            // jiggle behind the panel. The modal's Show() also calls
            // ClearVisuals; this gate keeps Update() from re-firing.
            if (StageClearModal.Instance != null && StageClearModal.Instance.IsVisible) return false;
            // Continue modal (top-out rescue) — same rule. Background tile-hops
            // shouldn't compete with the dim scrim while the player decides
            // whether to play on.
            if (ContinueModal.Instance != null && ContinueModal.Instance.IsShowing) return false;
            // Overlay pause (rising-intro spotlight beat) up → suppress the idle hint so a dimmed hand card
            // doesn't hop out from behind the dim scrim. 2026-07-08 Spencer.
            if (SurvivalManager.Instance != null && SurvivalManager.Instance.IsOverlayPaused) return false;
            return true;
        }

        // ── Move-finder ─────────────────────────────────────────────────────────

        private struct BestMove
        {
            public int CardIndex;
            public int Col;
            public List<Vector2Int> WordCells; // existing-tile cells in the resulting word
            public int Score;
        }

        private bool TryFindBestMove(out BestMove best)
        {
            best = new BestMove { CardIndex = -1, Col = -1, Score = -1, WordCells = null };

            var rules = RulesEngine.Instance;
            var hand  = HandManager.Instance;
            if (rules == null || hand == null) return false;

            var pHand = MatchController.Instance != null
                ? MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN) : null;
            if (pHand == null) return false;
            char[] handArr = pHand.GetAllSlots();
            if (handArr == null) return false;

            int cols = RulesEngine.COLS;
            int handSize = handArr.Length;

            for (int i = 0; i < handSize; i++)
            {
                char letter = handArr[i];
                if (letter == '\0') continue;
                if (letter == TileBag.WILD_CHAR)
                {
                    // WILD: it can be ANY letter, so evaluate it as every A-Z and take the best word it forms
                    // in each column. Lets the hint point at a good wild placement instead of ignoring it.
                    for (int col = 0; col < cols; col++)
                    {
                        if (rules.GetLowestEmptyRow(col) < 0) continue;
                        int wScore; List<Vector2Int> wCells;
                        if (!BestWildDropInColumn(rules, col, out wScore, out wCells)) continue;
                        if (wScore <= best.Score) continue;
                        best = new BestMove { CardIndex = i, Col = col, WordCells = wCells, Score = wScore };
                    }
                    continue;
                }

                for (int col = 0; col < cols; col++)
                {
                    if (rules.GetLowestEmptyRow(col) < 0) continue; // column full

                    bool wouldTrigger;
                    List<RulesWordMatch> matches = rules.SimulateDropWithTriggerCheck(
                        col, letter, MatchController.PLAYER_HUMAN, out wouldTrigger);

                    if (matches == null || matches.Count == 0) continue;

                    int score = ScoreMatches(matches, wouldTrigger);
                    if (score <= best.Score) continue;

                    // Collect the cells of the FIRST match (primary word) — used to
                    // glow the existing board tiles that contribute to it.
                    var cells = new List<Vector2Int>(matches[0].Cells != null ? matches[0].Cells : new List<Vector2Int>());
                    best = new BestMove { CardIndex = i, Col = col, WordCells = cells, Score = score };
                }
            }

            return best.CardIndex >= 0;
        }

        /// <summary>Evaluate a WILD dropped in <paramref name="col"/> by trying every letter A-Z and returning
        /// the best word it forms (highest ScoreMatches). Shared by the general hint scan and the first-wild
        /// teaching hint. 2026-07-07 Spencer.</summary>
        private bool BestWildDropInColumn(RulesEngine rules, int col, out int bestScore, out List<Vector2Int> bestCells)
        {
            bestScore = -1; bestCells = null;
            if (rules.GetLowestEmptyRow(col) < 0) return false; // column full
            for (char c = 'A'; c <= 'Z'; c++)
            {
                bool wouldTrigger;
                var matches = rules.SimulateDropWithTriggerCheck(col, c, MatchController.PLAYER_HUMAN, out wouldTrigger);
                if (matches == null || matches.Count == 0) continue;
                int score = ScoreMatches(matches, wouldTrigger);
                if (score <= bestScore) continue;
                bestScore = score;
                bestCells = new List<Vector2Int>(matches[0].Cells != null ? matches[0].Cells : new List<Vector2Int>());
            }
            return bestScore > 0 && bestCells != null;
        }

        /// <summary>Teaching hook: find the WILD in hand + the single best placement for it, and pin the idle
        /// hint there (card-hop + marching-ants, same render as the swap nudge). For the first-wild "here's how
        /// to use it" moment on a live board. Returns false if there's no wild or nowhere useful to place it.
        /// 2026-07-07 Spencer.</summary>
        public bool ForceWildHint()
        {
            var rules = RulesEngine.Instance;
            var pHand = MatchController.Instance != null ? MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN) : null;
            if (rules == null || pHand == null) return false;
            char[] handArr = pHand.GetAllSlots();
            if (handArr == null) return false;

            int wildSlot = -1;
            for (int i = 0; i < handArr.Length; i++)
                if (handArr[i] == TileBag.WILD_CHAR) { wildSlot = i; break; }
            if (wildSlot < 0) return false;

            int bestCol = -1, bestScore = -1;
            List<Vector2Int> bestCells = null;
            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                int s; List<Vector2Int> cells;
                if (!BestWildDropInColumn(rules, col, out s, out cells)) continue;
                if (s <= bestScore) continue;
                bestScore = s; bestCol = col; bestCells = cells;
            }
            if (bestCol < 0) return false;

            SetForcedHint(wildSlot, bestCol, bestCells);
            return true;
        }

        private int ScoreMatches(List<RulesWordMatch> matches, bool wouldTriggerPrimed)
        {
            int total = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                int len = (m.Cells != null) ? m.Cells.Count : 0;
                total += len * 10;
            }
            if (wouldTriggerPrimed) total += 200; // big bonus for chain-triggering plays
            return total;
        }

        // ── Visuals ─────────────────────────────────────────────────────────────

        private void RefreshHint()
        {
            if (SurvivalManager.FullBoardActive) { RefreshSwapHint(); return; }   // the forced (tutorial) hint is a drop-game card hint — never on the full board
            BestMove move;
            if (_forcedActive)
            {
                move = _forcedMove;   // tutorial: show OUR scripted target, not the auto-best move
            }
            else if (!TryFindBestMove(out move))
            {
                ClearVisuals();
                return;
            }

            // ALWAYS clear visuals before re-applying so cached base positions
            // are fresh from the resting state (not mid-hop). Without this,
            // each refresh captured the current mid-hop position as the new
            // base, drifting tiles upward over time.
            ClearVisuals();

            _bestCardIndex = move.CardIndex;
            _activeStage = 1;

            // Hand card hops (shows WHICH card to drop). Board letters LIGHT UP
            // only — glow pulse, no hop/scale (2026-06-03 Spencer). The marching-
            // ants outline carries "which word"; the glow ties the letters to the
            // hand-card rhythm.
            AnimateHandCard(move.CardIndex);
            AnimateBoardTiles(move.WordCells, move.Col);

            // 2026-06-03 Spencer: marching-ants outline around the whole word
            // (WordCells already includes the to-be-dropped cell), shown ON TOP
            // of the hops. Royal Match-style "here's the word" cue.
            EnsureOutline();
            if (_outline != null) _outline.Show(move.WordCells);
        }

        // ── SWAP HINT visuals (2026-10-22): the two tiles to swap NUDGE toward each other (Royal Match's idle hint), and the
        // marching-ants outline traces the word the swap would make. Tiles go back to their exact cell on clear. ──
        public static float SWAP_NUDGE = 0.22f;   // fraction of a cell the moving tile leans toward the word (one tile since 2026-10-22, so a touch further)
        private readonly List<Sequence> _swapSeqs = new List<Sequence>();
        private readonly List<Vector2Int> _swapCells = new List<Vector2Int>();
        private class SwapHintCache { public Vector2Int A, B; public List<Vector2Int> Cells; }
        /// <summary>The last hint picked — HandManager logs a MISMATCH if the player makes exactly this swap and no word arms.</summary>
        /// <summary>A swap hint is animating right now (lean / wiggle loops running). Period-key diag.</summary>
        public bool IsShowingHint => _swapSeqs.Count > 0;
        public static Vector2Int LastHintA = new Vector2Int(-1, -1), LastHintB = new Vector2Int(-1, -1);
        public static string LastHintWord;
        private SwapHintCache _hintCache;   // ONE hint per idle stretch — a random pick must not jump around on every refresh
        private bool _strongHint, _swapOnly;
        private List<Vector2Int> _swipeCache; private string _swipeWord;
        private static bool SwipeStillThere(GridManager grid, List<Vector2Int> cells, string word)
        {
            if (cells == null || word == null || cells.Count != word.Length) return false;
            for (int i = 0; i < cells.Count; i++) { var t = grid.GetTile(cells[i].x, cells[i].y); if (t == null || char.ToUpperInvariant(t.Letter) != char.ToUpperInvariant(word[i])) return false; }
            return true;
        }
        private void RefreshSwapHint()
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            ClearVisuals();
            if (rules == null || grid == null) return;
            // RE-CHECK the cached hint against the board as it is NOW (2026-10-23 Spencer: hints that "didnt make a word"). Nothing
            // told the hint when late pops / refills / flights changed the board, so a hint picked earlier could point at letters
            // that had moved. If its swap no longer arms a word, pick again.
            if (_hintCache != null && rules.WordsIfSwapped(_hintCache.A, _hintCache.B).Count == 0)
            {
                WDLog.Log($"[Hint] cached hint ({_hintCache.A.x},{_hintCache.A.y})<->({_hintCache.B.x},{_hintCache.B.y}) went stale (board changed) — re-picking");
                _hintCache = null;
            }
            // SWIPE HINT, every other free hint (2026-10-23 Spencer: "maybe i didnt drag to make words cause they werent hinted at
            // much"). A word already on the board (held cascade words etc.) gets the hand tracing across it + a letter-by-letter
            // glow — the L2 lesson's gesture — so swiping stays taught after L2. The Spyglass always shows the strongest SWAP.
            // 10-31 (Spencer: "the hints system gives one hint and then quickly gives another"): the swipe hint was never cached,
            // so the 3.5s refresh swapped it for an unrelated SWAP hint. Now it is re-shown until the player moves or the board
            // changes, exactly like the cached swap hint.
            if (_swipeCache != null)
            {
                if (SwipeStillThere(grid, _swipeCache, _swipeWord)) { ShowSwipeHint(grid, _swipeCache, _swipeWord); return; }
                _swipeCache = null;
            }
            if (_hintCache == null && !_strongHint && !_swapOnly && SWIPE_HINTS && (_preferSwipeNext || !rules.FindSwapHint(false, out _, out _, out _))
                && rules.FindSwipeHint(out var swCells, out var swWord))
            {
                _preferSwipeNext = false;
                _swipeCache = swCells; _swipeWord = swWord;
                ShowSwipeHint(grid, swCells, swWord);
                return;
            }
            if (_hintCache == null)
            {
                _preferSwipeNext = true;   // next fresh hint tries a swipe first
                if (!rules.FindSwapHint(_strongHint, out var fa, out var fb, out var fc)) { WDLog.Log("[Hint] no swap makes a word"); return; }
                _hintCache = new SwapHintCache { A = fa, B = fb, Cells = fc };
                var expect = rules.WordsIfSwapped(fa, fb);
                LastHintA = fa; LastHintB = fb; LastHintWord = expect.Count > 0 ? expect[0].Word : "?";
                WDLog.Log($"[Hint] picked ({fa.x},{fa.y})<->({fb.x},{fb.y}) expects '{LastHintWord}'");
            }
            var a = _hintCache.A; var b = _hintCache.B; var cells = _hintCache.Cells;
            var ta = grid.GetTile(a.x, a.y); var tb = grid.GetTile(b.x, b.y);
            if (ta == null || tb == null) return;
            _activeStage = 1;
            float cell = grid.CellSize;
            Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
            Vector3 pa = grid.CellToWorld(a.x, a.y) + slide, pb = grid.CellToWorld(b.x, b.y) + slide;
            // ONE tile leans (2026-10-22 Spencer: "instead of pushing both of the letters together can you have just one that is
            // leaning towards the group of words with the indicator"). The word runs through the cell a letter MOVES INTO, so
            // the mover is the tile on the OTHER side: word covers b (not a) -> a leans toward b, and vice versa.
            bool aIn = cells.Contains(a), bIn = cells.Contains(b);
            bool moverIsB = aIn && !bIn;
            Tile mover = moverIsB ? tb : ta;
            Vector3 from = moverIsB ? pb : pa, to = moverIsB ? pa : pb;
            Vector3 dir = to - from; dir.z = 0f; dir.Normalize();
            // TOUCH, DON'T OVERLAP (2026-10-23 Spencer: "they can touch but they shouldnt overlap"). Lean exactly the GAP between
            // the two tiles' drawn edges along the swap axis (tiles fill ~88% of a cell side to side, so ~12% of a cell). Stacked
            // tiles already meet/overlap top-to-bottom (the art is taller than wide), so a vertical hint has no gap — it WIGGLES
            // toward its partner instead of sliding.
            Tile partner = moverIsB ? ta : tb;
            float gap = TileGapAlong(mover, partner, dir, Vector3.Distance(from, to));
            float lean = Mathf.Min(cell * SWAP_NUDGE, gap);
            if (lean >= cell * 0.03f) NudgeLoop(mover, from, dir * lean);
            else WiggleLoop(mover, from, dir);
            _swapCells.Add(moverIsB ? b : a);
            // Free hint shows the WORD only after a further SWAP_OUTLINE_EXTRA_SECONDS of idling; the Spyglass shows it at once.
            bool showWord = _strongHint || _idleTime >= SwapIdleNow + SWAP_OUTLINE_EXTRA_SECONDS;
            if (showWord) { EnsureOutline(); if (_outline != null) _outline.Show(cells); }
            if (_strongHint) PlaythroughStats.Spyglass++; else PlaythroughStats.FreeHints++;
            WDLog.Log($"[Hint] {(_strongHint ? "SPYGLASS" : "free")} swap ({a.x},{a.y})<->({b.x},{b.y}) {cells.Count}-letter word, outline={(showWord ? "on" : "not yet")}");
            if (!_strongHint) TutorialBeats.OnFreeHintShown(a, b, cells, moverIsB);   // 11-20: the first free hint gets a one-line lesson
        }
        /// <summary>Empty space between two tiles' drawn edges along the axis joining them (0 if they meet or overlap).</summary>
        private static float TileGapAlong(Tile a, Tile b, Vector3 dir, float centreDist)
        {
            var ra = a != null ? a.GetComponent<SpriteRenderer>() : null; var rb = b != null ? b.GetComponent<SpriteRenderer>() : null;
            if (ra == null || rb == null || ra.sprite == null || rb.sprite == null) return 0f;
            bool horizontal = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y);
            float ea = horizontal ? ra.bounds.extents.x : ra.bounds.extents.y;
            float eb = horizontal ? rb.bounds.extents.x : rb.bounds.extents.y;
            return Mathf.Max(0f, centreDist - ea - eb);
        }
        /// <summary>No room to slide: tilt toward the partner and back (a wiggle), twice, then pause — same rhythm as the lean.</summary>
        private void WiggleLoop(Tile t, Vector3 home, Vector3 dir)
        {
            if (!_swapTiles.Contains(t)) _swapTiles.Add(t);   // track the TILE, not its cell (it can fall)
            t.MarkAnimating(LOOP_INTERVAL + 1f);
            t.LiftForFx(LOOP_INTERVAL + 1f);   // draw OVER the neighbour it leans into (2026-10-23 Spencer) — self-restoring
            t.transform.DOKill(); t.transform.position = home; t.transform.localRotation = Quaternion.identity;
            float ang = (Mathf.Abs(dir.x) >= Mathf.Abs(dir.y) ? -Mathf.Sign(dir.x) : (dir.y > 0 ? 1f : -1f)) * HINT_WIGGLE_DEG;
            var seq = DOTween.Sequence().SetUpdate(true);
            for (int i = 0; i < 2; i++)
            {
                seq.Append(t.transform.DOLocalRotate(new Vector3(0f, 0f, ang), 0.1f).SetEase(Ease.OutQuad));
                seq.Append(t.transform.DOLocalRotate(new Vector3(0f, 0f, -ang * 0.6f), 0.12f).SetEase(Ease.InOutQuad));
                seq.Append(t.transform.DOLocalRotate(Vector3.zero, 0.1f).SetEase(Ease.OutQuad));
            }
            seq.AppendInterval(1.1f);
            seq.SetLoops(-1);
            _swapSeqs.Add(seq);
        }
        public static float HINT_WIGGLE_DEG = 7f;

        private void NudgeLoop(Tile t, Vector3 home, Vector3 offset)
        {
            if (!_swapTiles.Contains(t)) _swapTiles.Add(t);   // track the TILE, not its cell (it can fall)
            t.MarkAnimating(LOOP_INTERVAL + 1f);   // keep the position checkers off it while it leans
            t.LiftForFx(LOOP_INTERVAL + 1f);       // ...and draw it OVER the neighbour it leans into (2026-10-23) — self-restoring
            t.transform.DOKill(); t.transform.position = home;
            var seq = DOTween.Sequence().SetUpdate(true);
            for (int i = 0; i < 2; i++)
            {
                seq.Append(t.transform.DOMove(home + offset, 0.14f).SetEase(Ease.OutQuad));
                seq.Append(t.transform.DOMove(home, 0.16f).SetEase(Ease.InOutQuad));
            }
            seq.AppendInterval(1.1f);
            seq.SetLoops(-1);
            _swapSeqs.Add(seq);
        }
        public static bool SWIPE_HINTS = true, SWIPE_HINT_HAND = false;
        public static Color SWIPE_HINT_GLOW = new Color(1.3f, 1.35f, 1.45f, 1f);
        // 10-31 Spencer: a hinted GEM's letter wasn't readable - the HDR white glow on the bright cyan face pushed it past 1 and the
        // bloom washed out the tile, letter included. Gems get a soft, non-HDR glow (brightens without blooming).
        public static Color SWIPE_HINT_GLOW_GEM = new Color(0.55f, 0.6f, 0.65f, 1f);
        public static float SWIPE_HINT_GEM_ALPHA = 0.35f;
        private bool _preferSwipeNext;
        private void ShowSwipeHint(GridManager grid, List<Vector2Int> cells, string word)
        {
            if (cells == null || cells.Count < 2) return;
            _activeStage = 1;
            // letters light up one after another along the word, hold, go dark — looped
            var seq = DOTween.Sequence().SetUpdate(true);
            for (int i = 0; i < cells.Count; i++)
            {
                var t = grid.GetTile(cells[i].x, cells[i].y); if (t == null) continue;
                if (!_swapTiles.Contains(t)) _swapTiles.Add(t);
                var tt = t;
                // A WHITE GLOW, never the gold "real word" face (2026-10-23 Spencer: "some letters were glitching out and turning into gems
                // and then back" — that gold face reads as a GEM). Soft bloom ripple along the word + a small punch; the face never changes.
                seq.InsertCallback(0.25f + i * 0.09f, () => { if (tt != null && !tt.HasPrimedGlow) { if (tt.IsGem) tt.SetBloomGlow(SWIPE_HINT_GLOW_GEM, SWIPE_HINT_GEM_ALPHA, forceDesktop: true); else tt.SetBloomGlow(SWIPE_HINT_GLOW, 0.55f, forceDesktop: true); tt.transform.DOPunchScale(Vector3.one * 0.08f, 0.2f, 4, 0.5f).SetUpdate(true); } });
                seq.InsertCallback(0.25f + cells.Count * 0.09f + 0.7f, () => { if (tt != null && !tt.HasPrimedGlow) tt.ClearBloomGlow(); });
            }
            seq.AppendInterval(1.2f);
            seq.SetLoops(-1);
            _swapSeqs.Add(seq);
            // 10-31 Spencer: "instead of having the glove shown dragging across the letters just have them light up" - the letter
            // ripple alone is the hint. SWIPE_HINT_HAND = true brings the hand back (the L2 lesson still teaches the gesture).
            if (SWIPE_HINT_HAND) TutorialOverlay.Ensure().ShowHandTrace(cells[0], cells[cells.Count - 1], 0.18f * cells.Count);
            LastHintA = cells[0]; LastHintB = cells[cells.Count - 1]; LastHintWord = word;
            WDLog.Log($"[Hint] SWIPE '{word}' ({cells[0].x},{cells[0].y})->({cells[cells.Count - 1].x},{cells[cells.Count - 1].y})");
        }

        private readonly List<Tile> _swapTiles = new List<Tile>();
        private void ClearSwapVisuals()
        {
            TutorialOverlay.Instance?.HideHandTrace();
            foreach (var st in _swapTiles) if (st != null && !st.HasPrimedGlow) st.ClearBloomGlow();
            foreach (var s in _swapSeqs) if (s != null && s.IsActive()) s.Kill();
            _swapSeqs.Clear();
            // THE HINTED TILES THEMSELVES (2026-10-23 — "a pop happened and then a long delay before another word popped", x3).
            // Cleanup went by cell: a hinted tile that had FALLEN kept its lean, its lift and its 4.5s MarkAnimating claim, and
            // the next gravity wait sat on that claim (falls of 1.8s and 3.2s). Release the claim on the real tiles.
            foreach (var ht in _swapTiles)
            {
                if (ht == null) continue;
                ht.ReleaseAnimatingClaim();
                ht.transform.localRotation = Quaternion.identity;
                ht.EndFxLift();
                if (!ht.IsFalling && GridManager.Instance != null && ht.Col >= 0 && ht.Row >= 0)
                    ht.transform.position = GridManager.Instance.CellToWorld(ht.Col, ht.Row) + new Vector3(GridManager.Instance.BoardSlideOffsetX, 0f, 0f);
            }
            _swapTiles.Clear();
            var grid = GridManager.Instance;
            if (grid != null)
            {
                Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
                foreach (var c in _swapCells)
                {
                    var t = grid.GetTile(c.x, c.y);
                    if (t != null) { t.transform.position = grid.CellToWorld(c.x, c.y) + slide; t.transform.localRotation = Quaternion.identity; t.EndFxLift(); }   // exact cell + upright + normal layer
                }
            }
            _swapCells.Clear();
        }

        private WordHintOutline _outline;
        private void EnsureOutline()
        {
            if (_outline != null) return;
            var go = new GameObject("WordHintOutline");
            go.transform.SetParent(transform, false);
            _outline = go.AddComponent<WordHintOutline>();
        }

        private void AnimateHandCard(int cardIndex)
        {
            if (HandManager.Instance == null) return;
            var cardObjects = HandManager.Instance.GetCardObjects();
            if (cardObjects == null || cardIndex < 0 || cardIndex >= cardObjects.Length) return;
            var go = cardObjects[cardIndex];
            if (go == null) return;

            if (_cardSequence != null && _cardSequence.IsActive()) _cardSequence.Kill();

            _cardBasePosition = go.transform.localPosition;
            _cardBaseScale = go.transform.localScale;
            var sr = go.GetComponent<SpriteRenderer>();

            _cardSequence = BuildHopSequence(go.transform, sr, _cardBasePosition, _cardBaseScale,
                                             useLocalPosition: true, enablePosition: true);
        }

        private void AnimateBoardTiles(List<Vector2Int> cells, int dropCol)
        {
            if (cells == null || cells.Count == 0) return;
            var grid = GridManager.Instance;
            if (grid == null) return;

            // Highlight only EXISTING tiles (skip the would-be-dropped cell
            // because that tile doesn't exist yet). The hand card pulse
            // represents the to-be-dropped tile; the board glow represents
            // the existing letters that would complete the word.
            int dropRow = RulesEngine.Instance != null
                ? RulesEngine.Instance.GetLowestEmptyRow(dropCol) : -1;

            for (int i = 0; i < cells.Count; i++)
            {
                Vector2Int cell = cells[i];
                if (cell.x == dropCol && cell.y == dropRow) continue; // skip drop position

                Tile tile = grid.GetTile(cell.x, cell.y);
                if (tile == null) continue;
                // Don't fight existing primed/scored/cyan animations — let those
                // win and skip glow on those tiles. Their pulses already draw
                // attention, so the hint is redundant on them.
                if (tile.HasPermanentGlow) continue;

                var sr = tile.GetComponent<SpriteRenderer>();
                if (sr == null) continue;

                // Cache base transform state for restoration
                Vector3 basePos   = tile.transform.position;
                Vector3 baseScale = tile.transform.localScale;

                // Board tiles LIGHT UP only (glow pulse, no hop/scale) in sync with
                // the hand-card hops. 2026-06-03 Spencer: motion removed from board
                // letters; the marching-ants outline carries "which word", the glow
                // ties the letters to the hand-card rhythm.
                var seq = BuildGlowPulseSequence(sr);
                _tileSequences.Add(seq);

                _highlightedTiles.Add(tile);
                _highlightedTileBasePositions.Add(basePos);
                _highlightedTileBaseScales.Add(baseScale);
            }
        }

        /// <summary>
        /// Builds the CC-style cartoony hop animation as a looping DOTween
        /// Sequence on the given transform. Phases:
        ///   1. Lift + vertical stretch (squash horizontally)
        ///   2. Hang briefly at apex
        ///   3. Fall + restore proportions
        ///   4. Squash on landing (horizontal bulge)
        ///   5. Rest scale (with a tiny OutBack settle)
        ///   6. Pause, then repeat
        /// Color tint pulses up during lift and back at rest.
        /// </summary>
        private Sequence BuildHopSequence(Transform t, SpriteRenderer sr,
                                          Vector3 basePos, Vector3 baseScale,
                                          bool useLocalPosition, bool enablePosition = true,
                                          float stretchMultiplier = 1f)
        {
            Vector3 peakPos = basePos + new Vector3(0f, HOP_AMPLITUDE, 0f);
            // stretchMultiplier scales the deformation from base — 1.0 = full
            // stretch (hand card), 0.5 = half stretch (board tiles, less
            // chance of touching neighbors).
            float stretchYAmt = (STRETCH_Y - 1f) * stretchMultiplier;
            float stretchXAmt = (STRETCH_X - 1f) * stretchMultiplier;
            float squashYAmt  = (SQUASH_Y  - 1f) * stretchMultiplier;
            float squashXAmt  = (SQUASH_X  - 1f) * stretchMultiplier;
            Vector3 stretchScale = new Vector3(baseScale.x * (1f + stretchXAmt), baseScale.y * (1f + stretchYAmt), baseScale.z);
            Vector3 squashScale  = new Vector3(baseScale.x * (1f + squashXAmt),  baseScale.y * (1f + squashYAmt),  baseScale.z);

            var seq = DOTween.Sequence().SetUpdate(true);

            // Build N hops in succession. Each hop:
            //   lift+stretch → hang → fall+restore → squash → rest → tiny gap
            for (int h = 0; h < HOPS_PER_BURST; h++)
            {
                // Lift + stretch + brighten. Position tween is OPTIONAL —
                // disabled for board tiles to avoid them hopping into tiles
                // above them. The scale + color pulse keeps them in sync
                // with the hand card's rhythm visually.
                if (enablePosition)
                {
                    if (useLocalPosition)
                        seq.Append(t.DOLocalMoveY(peakPos.y, HOP_UP_DURATION).SetEase(Ease.OutQuad));
                    else
                        seq.Append(t.DOMoveY(peakPos.y, HOP_UP_DURATION).SetEase(Ease.OutQuad));
                    seq.Join(t.DOScale(stretchScale, HOP_UP_DURATION).SetEase(Ease.OutBack, 1.5f));
                }
                else
                {
                    // No position — scale tween is the primary visual.
                    seq.Append(t.DOScale(stretchScale, HOP_UP_DURATION).SetEase(Ease.OutBack, 1.5f));
                }
                if (sr != null)
                    seq.Join(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; }, HINT_GLOW_HIGH, HOP_UP_DURATION).SetEase(Ease.OutSine));

                // Hang at apex
                seq.AppendInterval(HOP_HANG_DURATION);

                // Fall + restore scale + return color — InCubic accelerates
                // INTO the ground (gravity feel, juice principle: weight)
                if (enablePosition)
                {
                    if (useLocalPosition)
                        seq.Append(t.DOLocalMoveY(basePos.y, HOP_DOWN_DURATION).SetEase(Ease.InCubic));
                    else
                        seq.Append(t.DOMoveY(basePos.y, HOP_DOWN_DURATION).SetEase(Ease.InCubic));
                    seq.Join(t.DOScale(baseScale, HOP_DOWN_DURATION).SetEase(Ease.InCubic));
                }
                else
                {
                    seq.Append(t.DOScale(baseScale, HOP_DOWN_DURATION).SetEase(Ease.InCubic));
                }
                if (sr != null)
                    seq.Join(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; }, HINT_GLOW_LOW, HOP_DOWN_DURATION).SetEase(Ease.InSine));

                // Squash on landing — OutBack for springy impact
                seq.Append(t.DOScale(squashScale, HOP_SQUASH_DURATION).SetEase(Ease.OutBack, 2f));

                // Settle back to rest — OutElastic for that final cartoony jiggle
                seq.Append(t.DOScale(baseScale, HOP_REST_DURATION).SetEase(Ease.OutElastic, 0.6f, 0.3f));

                // Tiny gap before next hop in burst
                seq.AppendInterval(HOP_GAP_DURATION);
            }

            // Long pause after the burst before next cycle
            seq.AppendInterval(BURST_PAUSE_DURATION);

            seq.SetLoops(-1);
            return seq;
        }

        /// <summary>Board-tile hint: glow pulse ONLY (no hop, no scale). The colour
        /// brightens to HINT_GLOW_HIGH and back, on the SAME per-hop rhythm as
        /// BuildHopSequence (up → hang → down → squash+settle hold → gap), so the
        /// board letters "light up" in sync with the hand-card hop without moving.
        /// 2026-06-03 Spencer.</summary>
        private Sequence BuildGlowPulseSequence(SpriteRenderer sr)
        {
            var seq = DOTween.Sequence().SetUpdate(true);
            for (int h = 0; h < HOPS_PER_BURST; h++)
            {
                if (sr != null)
                    seq.Append(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; },
                                          HINT_GLOW_HIGH, HOP_UP_DURATION).SetEase(Ease.OutSine));
                seq.AppendInterval(HOP_HANG_DURATION);
                if (sr != null)
                    seq.Append(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; },
                                          HINT_GLOW_LOW, HOP_DOWN_DURATION).SetEase(Ease.InSine));
                // Hold at rest for the span the card spends squashing + settling, so
                // the glow cycle length matches the hop cycle length (stays in sync).
                seq.AppendInterval(HOP_SQUASH_DURATION + HOP_REST_DURATION);
                seq.AppendInterval(HOP_GAP_DURATION);
            }
            seq.AppendInterval(BURST_PAUSE_DURATION);
            seq.SetLoops(-1);
            return seq;
        }

        public void ClearVisuals()
        {
            // Hide the marching-ants word outline.
            if (_outline != null) _outline.Hide();
            ClearSwapVisuals();

            // Kill card sequence and restore card position + scale
            if (_cardSequence != null && _cardSequence.IsActive()) _cardSequence.Kill();
            _cardSequence = null;

            if (HandManager.Instance != null && _bestCardIndex >= 0)
            {
                var cardObjects = HandManager.Instance.GetCardObjects();
                if (cardObjects != null && _bestCardIndex < cardObjects.Length && cardObjects[_bestCardIndex] != null)
                {
                    cardObjects[_bestCardIndex].transform.localPosition = _cardBasePosition;
                    cardObjects[_bestCardIndex].transform.localScale    = _cardBaseScale;
                    var sr = cardObjects[_bestCardIndex].GetComponent<SpriteRenderer>();
                    if (sr != null) sr.color = Color.white;
                }
            }

            // Kill all tile sequences
            for (int i = 0; i < _tileSequences.Count; i++)
            {
                if (_tileSequences[i] != null && _tileSequences[i].IsActive())
                    _tileSequences[i].Kill();
            }
            _tileSequences.Clear();

            // Restore color, position, and scale on each highlighted tile
            for (int i = 0; i < _highlightedTiles.Count; i++)
            {
                var tile = _highlightedTiles[i];
                if (tile == null) continue;
                var sr = tile.GetComponent<SpriteRenderer>();
                if (sr != null) sr.color = Color.white;
                if (i < _highlightedTileBasePositions.Count)
                    tile.transform.position = _highlightedTileBasePositions[i];
                if (i < _highlightedTileBaseScales.Count)
                    tile.transform.localScale = _highlightedTileBaseScales[i];
            }
            _highlightedTiles.Clear();
            _highlightedTileBasePositions.Clear();
            _highlightedTileBaseScales.Clear();

            _bestCardIndex = -1;
            _activeStage = 0;
        }
    }
}
