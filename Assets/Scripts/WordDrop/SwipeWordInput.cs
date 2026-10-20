using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>SWIPE-TO-CLAIM — drag across a straight line of tiles to arm the word you can
    /// see there. Horizontal or vertical, EITHER direction. Costs a move, exactly like a swap.
    /// Built 2026-10-19 (Spencer).
    ///
    /// WHY IT EXISTS: the pivot doc names the accessibility test as "is it fun when you play
    /// BADLY", and records that a random swap produces nothing. Swipe is the floor — MEASURED
    /// on 160 random boards, a claimable 4+ word was present on 160 of them, so the player is
    /// never without a move.
    ///
    /// WHY IT DOESN'T BREAK THE GAME: measured points-per-move, both costing one move —
    ///     hunting the best visible 4+ word   29.2
    ///     building with the best one swap    46.1   (0.63x)
    /// Hunting never beats good play, so it is a safety net rather than a dominant strategy.
    /// And because that build figure assumes a player who spots the optimal swap every turn,
    /// a weaker player's building falls BELOW 29 — so the net catches exactly who it should.
    ///
    /// WHY SWIPING GETS 8 DIRECTIONS WHEN THE AUTO-SCANNER ONLY GETS 2: auto-detecting
    /// backwards doubles the accidental words sitting on a random board (21 -> 42, measured),
    /// which is pure noise and would arm webs nobody intended. A word the PLAYER traces is not
    /// an accident. Intent is the filter, so direction stops being a liability.
    ///
    /// MINIMUM LENGTH is RulesEngine.SWIPE_MIN_LENGTH, now 3 to match the engine-wide
    /// MIN_WORD_LENGTH — see the note there for why the original 4 was wrong.
    ///
    /// GESTURE SAFETY: the board swap is TAP-TAP, not drag, so a press-drag-release collides
    /// with nothing. The one overlap is that pressing down pre-selects a tile as a swap source;
    /// the moment the drag crosses a second cell we call HandManager.AbortPendingSwapSelection.
    /// </summary>
    public class SwipeWordInput : MonoBehaviour
    {
        public static SwipeWordInput Instance { get; private set; }

        /// <summary>True while a drag has crossed 2+ cells — i.e. this gesture is a swipe.</summary>
        public static bool IsSwiping { get; private set; }

        private readonly List<Vector2Int> _path = new List<Vector2Int>();
        private bool _active;
        private Vector2Int _anchor;
        private bool _abortedSwapSelection;
        private bool _didHighlight;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Instance   = null;
            IsSwiping  = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (!RulesEngine.SWIPE_WORDS_ENABLED) return;
            var go = new GameObject("SwipeWordInput");
            go.AddComponent<SwipeWordInput>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy() { if (Instance == this) Instance = null; }

        private void Update()
        {
            if (!RulesEngine.SWIPE_WORDS_ENABLED) return;

            var grid = GridManager.Instance;
            var hand = HandManager.Instance;
            if (grid == null || hand == null) return;

            // Don't compete with a resolution in flight, a rising row, or a locked board.
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) { Cancel(); return; }
            if (!hand.IsInteractable) { Cancel(); return; }

            // ── NEVER RUN WHILE AN OVERLAY IS UP. 2026-10-19 ─────────────────────────
            // Spencer cleared a level and the Stage Clear modal froze. debug_log showed the
            // modal SHOWING, then three taps landing as "[Swipe] too short — 1 cells" with no
            // dismiss. The modal's whole backdrop is a tap button (overlayTapBtn.onClick ->
            // OnContinuePressed), so board input must stand down while it is up — this
            // component was polling raw Input every frame regardless of what was on screen.
            // HandManager.IsInteractable stays TRUE during the modal, so it was not enough.
            // ⚠️ DO NOT gate this on StageClearModal/LevelIntroModal.IsShowing. Those read
            // `_canvas.gameObject.activeSelf`, and SetCanvasVisible(false) deliberately keeps
            // the GameObject ACTIVE (it only flips Canvas.enabled + the GraphicRaycaster) — so
            // IsShowing is TRUE FOREVER once a modal has been shown once. Using it here killed
            // swiping permanently after the first level-clear. 2026-10-19.
            // IsOverlayPaused is the real "a modal owns the screen" flag: StageClearModal.Show
            // sets it and FinalizeDismiss clears it.
            var sm = SurvivalManager.Instance;
            if (sm != null && sm.IsOverlayPaused) { Cancel(); return; }

            // THE LEVEL IS WON — STOP TAKING INPUT. 2026-10-19 Spencer: "you shouldn't be able
            // to score more points". MEASURED: [Stage] CLEARED fires ~100 frames before the
            // clear modal appears, so there was a ~1.7s window where a beaten level was still
            // fully playable and still banking score. Nothing should land in that gap.
            if (SurvivalManager.AwaitingNextLevel) { Cancel(); return; }

            bool down = Input.GetMouseButtonDown(0);
            bool held = Input.GetMouseButton(0);
            bool up   = Input.GetMouseButtonUp(0);
            Vector3 screenPos = Input.mousePosition;

            if (Input.touchCount > 0)
            {
                Touch t = Input.GetTouch(0);
                screenPos = t.position;
                down = t.phase == TouchPhase.Began;
                held = t.phase == TouchPhase.Moved || t.phase == TouchPhase.Stationary || down;
                up   = t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled;
            }

            if (down)
            {
                _path.Clear();
                _abortedSwapSelection = false;
                _didHighlight = false;
                Vector2Int c = CellUnder(screenPos, grid);
                if (c.x >= 0) { _anchor = c; _path.Add(c); _active = true; }
                return;
            }

            if (_active && held)
            {
                Vector2Int cur = CellUnder(screenPos, grid);
                if (cur.x >= 0) RebuildPath(cur, grid, hand);
                return;
            }

            if (_active && up)
            {
                ClearHighlight(grid);
                TryCommit();
                Cancel();
            }
        }

        /// <summary>Recompute the WHOLE path from the anchor cell to wherever the finger is
        /// now, rather than appending cell-by-cell.
        ///
        /// 2026-10-19 — the append approach had two bugs that both produced "highlight shows,
        /// word never primes":
        ///   1. SKIPPED CELLS. Input is sampled once per frame; a quick drag crosses two cells
        ///      in one frame, leaving a step of 2 that BuildExplicitMatch rejects outright.
        ///   2. WOBBLE WAS FATAL. The step was measured against the LAST ADDED cell, so a
        ///      single off-line sample was dropped and every later cell was then 2 steps from
        ///      the last one — the path stalled at 2-3 cells and could never reach 4.
        /// Rebuilding from the anchor is immune to both: it only asks whether the anchor and
        /// the current cell are collinear, and fills in everything between them.</summary>
        private void RebuildPath(Vector2Int cur, GridManager grid, HandManager hand)
        {
            int dx = cur.x - _anchor.x;
            int dy = cur.y - _anchor.y;

            // Must be collinear with the anchor. Horizontal or vertical only unless diagonals
            // are switched on. If the finger strays off-axis we keep the last good path rather
            // than destroying it.
            if (RulesEngine.SWIPE_ALLOW_DIAGONAL)
            {
                if (dx != 0 && dy != 0 && Mathf.Abs(dx) != Mathf.Abs(dy)) return;
            }
            else if (dx != 0 && dy != 0) return;

            int steps = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy));
            int sx = (dx == 0) ? 0 : (dx > 0 ? 1 : -1);
            int sy = (dy == 0) ? 0 : (dy > 0 ? 1 : -1);

            var next = new List<Vector2Int>(steps + 1);
            for (int i = 0; i <= steps; i++)
                next.Add(new Vector2Int(_anchor.x + sx * i, _anchor.y + sy * i));

            if (next.Count == _path.Count && (next.Count == 0 || next[next.Count - 1] == _path[_path.Count - 1]))
                return;   // unchanged

            ClearHighlight(grid);            // only touches tiles WE lit
            _path.Clear();
            _path.AddRange(next);

            if (_path.Count >= 2)
            {
                // Only light the trace once this is genuinely a swipe. A 1-cell path is a TAP,
                // and HandManager has already lit that tile as the swap source — calling
                // SetEditSelected(false) on it during cleanup is what was wiping the cyan
                // selection outline the instant you clicked a tile. 2026-10-19 Spencer.
                Highlight(grid, true);
                _didHighlight = true;
                // Real word under the finger? Light it in the about-to-pop face so the player knows to let go.
                string _rej;
                bool valid = _path.Count >= RulesEngine.SWIPE_MIN_LENGTH && RulesEngine.Instance != null
                             && RulesEngine.Instance.BuildExplicitMatch(_path, out _rej) != null;
                for (int i = 0; i < _path.Count; i++) grid.GetTile(_path[i].x, _path[i].y)?.SetSwipeValidPreview(valid);
                IsSwiping = true;
                if (!_abortedSwapSelection)
                {
                    hand.AbortPendingSwapSelection();
                    _abortedSwapSelection = true;
                }
            }
        }

        private static Vector2Int CellUnder(Vector3 screenPos, GridManager grid)
        {
            Camera cam = Camera.main;
            if (cam == null) return new Vector2Int(-1, -1);
            Vector3 world = cam.ScreenToWorldPoint(screenPos);
            world.z = 0f;
            return grid.WorldToCell(world);
        }

        private void Highlight(GridManager grid, bool on)
        {
            for (int i = 0; i < _path.Count; i++)
            {
                var t = grid.GetTile(_path[i].x, _path[i].y);
                if (t == null) continue;
                if (on) t.SetEditSelected(true, false, Tile.SWIPE_LIFT_SCALE); else t.SetEditSelected(false);
                if (!on) t.SetSwipeValidPreview(false);
            }
        }

        /// <summary>Clear ONLY a trace this component actually lit. Never touches a tile that
        /// HandManager lit as a swap source.</summary>
        private void ClearHighlight(GridManager grid)
        {
            if (!_didHighlight || grid == null) return;
            Highlight(grid, false);
            _didHighlight = false;
        }

        private void TryCommit()
        {
            var rules = RulesEngine.Instance;
            var hand  = HandManager.Instance;
            if (rules == null || hand == null) return;
            // TWO CELLS = A SWAP (drag a tile onto its neighbour). Decided on release, so a word
            // swipe passing through its 2nd cell is never mistaken for one. Two-letter words
            // don't exist in this game, so nothing is lost. 2026-10-20 Spencer.
            if (_path.Count == 2)
            {
                hand.CommitDragSwap(_path[0], _path[1]);
                return;
            }
            // Was a SILENT return — no log, no sound, no feedback of any kind. A refusal the
            // player cannot perceive reads as the feature being broken, which is exactly how it
            // landed. 2026-10-19.
            if (_path.Count < RulesEngine.SWIPE_MIN_LENGTH)
            {
                WDLog.Log($"[Swipe] too short — {_path.Count} cells, need {RulesEngine.SWIPE_MIN_LENGTH}");
                GameAudio.Instance?.PlayButtonClick();
                return;
            }

            // DIAGNOSTIC 2026-10-19: dump the exact cells and letters the path resolved to,
            // so a reject can be told apart from a grid/rules coordinate mismatch.
            var dbg = new System.Text.StringBuilder();
            for (int i = 0; i < _path.Count; i++)
            {
                var rc = rules.GetCell(_path[i].x, _path[i].y);
                var tl = GridManager.Instance != null ? GridManager.Instance.GetTile(_path[i].x, _path[i].y) : null;
                dbg.Append($"({_path[i].x},{_path[i].y})rules={(rc != null ? rc.Letter.ToString() : "-")}" +
                           $"/tile={(tl != null ? tl.Letter.ToString() : "-")} ");
            }
            WDLog.Log($"[Swipe] path: {dbg}");

            string reject;
            RulesWordMatch match = rules.BuildExplicitMatch(_path, out reject);
            if (match == null)
            {
                WDLog.Log($"[Swipe] rejected ({reject}) — {_path.Count} cells");
                GameAudio.Instance?.PlayButtonClick();
                // Not a word: the traced letters shake "no" (2026-10-20) — a refusal the player can SEE.
                var g = GridManager.Instance;
                if (g != null) for (int i = 0; i < _path.Count; i++) g.GetTile(_path[i].x, _path[i].y)?.ShakeNo();
                return;
            }

            bool ok = hand.CommitSwipedWord(match);
            WDLog.Log($"[Swipe] '{match.Word}' {(ok ? "ARMED" : "refused by registry")} " +
                      $"({match.Cells.Count} cells, {match.Direction}, {match.Score} pts)");
        }

        private void Cancel()
        {
            if (_active) ClearHighlight(GridManager.Instance);
            _path.Clear();
            _active = false;
            IsSwiping = false;
        }
    }
}
