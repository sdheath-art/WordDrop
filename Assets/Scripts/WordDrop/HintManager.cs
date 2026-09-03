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

        public static bool IsEnabled()
        {
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
            _idleTime = 0f;
            _nextLoopTime = -1f;
            if (_activeStage > 0) ClearVisuals();
        }

        /// <summary>MVP P5 — fire a hint immediately, regardless of idle timer.
        /// Used by the Lantern Trace booster (manual hint as a roguelite pick).</summary>
        public void ForceShowHint()
        {
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
            if (_activeStage > 0) ClearVisuals();
            // Delay next re-fire so the board animation (row rise, cascade)
            // has time to settle before we cache new base positions.
            _nextLoopTime = Time.unscaledTime + 1.5f;
        }

        private void Update()
        {
            if (!IsEnabled()) return;
            if (!GameplayActive()) { ResetIdleTimer(); return; }

            // Defensive: any input resets the timer. Picks up taps/drags even
            // if some code path forgot to call ResetIdleTimer explicitly.
            if (Input.touchCount > 0 || Input.GetMouseButton(0))
            {
                ResetIdleTimer();
                return;
            }

            _idleTime += Time.deltaTime;

            // Time to fire / refresh hint?
            if (_idleTime >= IDLE_THRESHOLD && Time.unscaledTime >= _nextLoopTime)
            {
                RefreshHint();
                _nextLoopTime = Time.unscaledTime + LOOP_INTERVAL;
            }
        }

        // ── Gating ──────────────────────────────────────────────────────────────

        private bool GameplayActive()
        {
            // Only run while a match is active and it's the human's turn.
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive) return false;
            if (MatchController.Instance.CurrentPlayer != MatchController.PLAYER_HUMAN) return false;
            if (HandManager.Instance != null && !HandManager.Instance.IsInteractable) return false;
            if (GridManager.Instance == null || RulesEngine.Instance == null) return false;
            // Stage-clear modal up → suppress hints so background tiles don't
            // jiggle behind the panel. The modal's Show() also calls
            // ClearVisuals; this gate keeps Update() from re-firing.
            if (StageClearModal.Instance != null && StageClearModal.Instance.IsShowing) return false;
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
