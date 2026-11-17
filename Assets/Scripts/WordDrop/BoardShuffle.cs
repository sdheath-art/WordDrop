using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// NO WORDS -> SHUFFLE (2026-10-30, Spencer: "in these games when no match is available the pieces scramble and reformat a
    /// new board. am i right?" - yes; built with HandManager.BOUNCE_NO_WORD). Watches the board while the player can act; once
    /// it has been still for SETTLE seconds and no swap makes a COMMON word (CountCommonWordSwaps &lt; SHUFFLE_BELOW), shows a
    /// "No words! Shuffling" card, re-deals the plain letters (RulesEngine.ShuffleForWords, aiming for TARGET_SWAPS) and flies
    /// each letter to its new cell. No move is charged. Only on full-board levels with moves left.
    /// </summary>
    public class BoardShuffle : MonoBehaviour
    {
        public static bool  ON = true;
        public static int   SHUFFLE_BELOW = 1, TARGET_SWAPS = 3;
        public static float SETTLE = 0.6f, CARD_IN = 0.25f, CARD_HOLD = 0.55f, FLY = 0.45f, FLY_STAGGER = 0.12f;
        private static BoardShuffle s_inst;
        private float _stillSince = -1f, _nextPoll;
        private string _checkedSig;
        public static bool Running { get; private set; }
        private readonly System.Random _rng = new System.Random();

        public static void Ensure()
        {
            if (s_inst != null) return;
            s_inst = new GameObject("BoardShuffle").AddComponent<BoardShuffle>();
            DontDestroyOnLoad(s_inst.gameObject);
        }

        private static bool BoardIdle()
        {
            var hm = HandManager.Instance; var mc = MatchController.Instance; var sm = SurvivalManager.Instance;
            return hm != null && hm.IsInteractable && RulesEngine.Instance != null && GridManager.Instance != null
                && !RulesEngine.Simulating && SurvivalManager.FullBoardActive && !SurvivalManager.AwaitingNextLevel
                && (mc == null || !mc.IsProcessing) && !WordDropFX.HasActiveExplosions && !TutorialBeats.BlocksInput
                && Time.timeScale > 0f && sm != null && sm.CurrentStageMovesRemaining > 0
                && !WDInput.GetMouseButton(0) && WDInput.touchCount == 0;   // 10-30: never start under the player's finger
        }

        private void Update()
        {
            if (!ON || Running) return;
            if (!BoardIdle()) { _stillSince = -1f; return; }
            if (_stillSince < 0f) _stillSince = Time.unscaledTime;
            if (Time.unscaledTime - _stillSince < SETTLE || Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.5f;
            var rules = RulesEngine.Instance;
            string sig = BoardSig(rules);
            if (sig != _checkedSig)
            {
                _checkedSig = sig;
                _goalDead = RulesEngine.GOAL_REFRESH_ON && rules.GoalAreaDead();   // judged once per board
                int n = rules.CountCommonWordSwaps();
                if (n < SHUFFLE_BELOW)
                {
                    PlaythroughStats.Shuffles++;
                    WDLog.Log($"[Shuffle] board stuck ({n} common-word swaps) - shuffling");
                    StartCoroutine(Run());
                    return;
                }
            }
            // GOAL AREA STALE (2026-10-30): words exist elsewhere but none reaches a goal. ONLY WHEN THEY NEED IT (Spencer: "i would
            // only help them if they need it dont you think?") - the player missed GOAL_HELP_MISSES swaps in a row, or has sat on
            // this board GOAL_HELP_IDLE seconds (before the 15s hint). Otherwise they might be about to find it, or work toward it.
            int moves = StageStats.MovesMade;
            float still = Time.unscaledTime - _stillSince;
            bool struggling = (HandManager.NoWordStreak >= GOAL_HELP_MISSES && still >= GOAL_HELP_MISS_PAUSE) || still >= GOAL_HELP_IDLE;
            // AT MOST GOAL_HELP_MAX_PER_LEVEL per level (10-30: bot had it in 35-93% of games, ~2/game on crate levels; Spencer: "we dont
            // want this happening on every level"). After that the hint is the only help.
            int lvl = SurvivalManager.Instance != null ? SurvivalManager.Instance.CurrentStageIndex : -1;
            if (lvl != _helpLevel) { _helpLevel = lvl; _helpsThisLevel = 0; }
            if (_goalDead && struggling && moves != _refreshedAtMove && _helpsThisLevel < GOAL_HELP_MAX_PER_LEVEL)
            {
                _refreshedAtMove = moves; _helpsThisLevel++;   // at most once per move: if it can't be fixed, the player moves on
                WDLog.Log($"[GoalShuffle] goal area dead + struggling (misses {HandManager.NoWordStreak}, idle {Time.unscaledTime - _stillSince:F0}s)");
                StartCoroutine(RefreshRun());
            }
        }
        private bool _goalDead;
        /// <summary>The goal-area help will run when the player struggles (goal area dead, not yet used this level) - HintManager
        /// holds the free hint back meanwhile so the two never fire on top of each other (10-31).</summary>
        public static bool GoalHelpPending => ON && s_inst != null && s_inst._goalDead && !Running
            && s_inst._helpsThisLevel < GOAL_HELP_MAX_PER_LEVEL
            && (SurvivalManager.Instance == null || s_inst._helpLevel == SurvivalManager.Instance.CurrentStageIndex);
        public static int   GOAL_HELP_MISSES = 3, GOAL_HELP_MAX_PER_LEVEL = 1;
        private int _helpLevel = -1, _helpsThisLevel;
        public static float GOAL_HELP_IDLE = 12f, GOAL_HELP_MISS_PAUSE = 1.5f;   // after the 3rd miss, wait until they pause (not mid-attempt)

        private static string BoardSig(RulesEngine r)
        {
            var sb = new System.Text.StringBuilder(RulesEngine.COLS * RulesEngine.ROWS);
            for (int y = 0; y < RulesEngine.ROWS; y++) for (int x = 0; x < RulesEngine.COLS; x++)
                { var c = r.GetCell(x, y); sb.Append(c == null ? '.' : c.Letter); }
            return sb.ToString();
        }

        private IEnumerator Run()
        {
            Running = true;
            var hm = HandManager.Instance; var grid = GridManager.Instance; var rules = RulesEngine.Instance;
            hm.IsInteractable = false;
            SwipeWordInput.Instance?.DropRemembered();
            var card = ShowCard(grid);
            yield return new WaitForSeconds(CARD_IN + CARD_HOLD);

            var moves = rules.ShuffleForWords(TARGET_SWAPS, _rng);
            if (moves != null && moves.Count > 0) yield return FlyMoves(moves, grid, rules);
            else WDLog.Log("[Shuffle] no better deal found - board left as is");

            if (card != null) card.DOFade(0f, 0.25f).OnComplete(() => { if (card != null) Destroy(card.gameObject); });
            HandManager.NoWordStreak = 0;
            _checkedSig = BoardSig(rules);
            SwipeWordInput.Instance?.DropRemembered();
            hm.IsInteractable = true;
            Running = false;
        }

        /// <summary>Fly each letter's tile from its old cell to its new one (an arc, staggered), then hand the board to the sync.</summary>
        private IEnumerator FlyMoves(List<(Vector2Int from, Vector2Int to)> moves, GridManager grid, RulesEngine rules)
        {
            {
                var flying = new List<(Tile t, Vector3 home)>();
                float longest = 0f;
                foreach (var (from, to) in moves)
                {
                    var t = grid.GetTile(from.x, from.y); if (t == null) continue;
                    Vector3 home = t.transform.position, dest = grid.CellToWorld(to.x, to.y); dest.z = home.z;
                    float delay = (float)_rng.NextDouble() * FLY_STAGGER;
                    t.MarkAnimating(FLY + delay + 0.1f);
                    t.transform.DOKill();
                    t.transform.DOJump(dest, grid.CellSize * 0.6f, 1, FLY).SetDelay(delay).SetEase(Ease.InOutSine);
                    flying.Add((t, home));
                    longest = Mathf.Max(longest, FLY + delay);
                }
                GameAudio.Instance?.PlayShuffle();
                yield return new WaitForSeconds(longest + 0.02f);
                // every tile goes back to ITS cell, and the sync paints the letter that cell now holds - same picture, no pop
                foreach (var (t, home) in flying) if (t != null) { t.transform.DOKill(); t.transform.position = home; }
                grid.SyncToRulesState(rules);
                rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
            }
        }

        private int _refreshedAtMove = -1;
        public static float GOAL_SHUFFLE_BEAT = 0.25f;   // card up -> pause -> the goal-area letters hop
        public static float REFRESH_FLIP = 0.18f, REFRESH_STAGGER = 0.025f;
        /// <summary>Goal-area refresh, shown openly: a small "Fresh letters!" card, then each changed tile flips over to its new
        /// letter one after another (squash to a sliver, swap, pop back) - the player sees the letters were re-dealt.</summary>
        private IEnumerator RefreshRun()
        {
            Running = true;
            var hm = HandManager.Instance; var grid = GridManager.Instance; var rules = RulesEngine.Instance;
            hm.IsInteractable = false;
            // SHUFFLE FIRST (10-30 Spencer: "i would do a shuffle"): move a few of the goal area's own letters
            var gmoves = rules.ShuffleGoalArea(_rng);
            if (gmoves != null && gmoves.Count > 0)
            {
                PlaythroughStats.Refreshes++;
                // NO TEXT (10-30 Spencer: on-screen text "looks cheezy" until the display-text style exists) - the board locks
                // (drags made meanwhile are dropped, not replayed onto the new letters), the letters hop, then the HINT shows the
                // word they made (his complaint: the shuffle made a word possible but he still couldn't see it).
                SwipeWordInput.Instance?.DropRemembered();
                yield return new WaitForSeconds(GOAL_SHUFFLE_BEAT);
                yield return FlyMoves(gmoves, grid, rules);
                SwipeWordInput.Instance?.DropRemembered();
                HandManager.NoWordStreak = 0;
                HintManager.Instance?.ClearForcedHint();
                _checkedSig = BoardSig(rules); _goalDead = false;
                hm.IsInteractable = true;
                Running = false;
                HintManager.Instance?.ForceShowHint(false, true);   // the goal-aware free hint now finds the word the hop made
                yield break;
            }
            var changed = rules.RefreshGoalArea();   // fallback: no rearrangement works -> a few fresh letters, flipped openly
            if (changed == null || changed.Count == 0) { hm.IsInteractable = true; Running = false; yield break; }
            PlaythroughStats.Refreshes++;
            SwipeWordInput.Instance?.DropRemembered();
            var card = ShowCard(grid, "Fresh letters!");
            yield return new WaitForSeconds(CARD_IN);
            changed.Sort((a, b) => a.y != b.y ? b.y.CompareTo(a.y) : a.x.CompareTo(b.x));
            GameAudio.Instance?.PlayShuffle();
            for (int i = 0; i < changed.Count; i++)
            {
                var t = grid.GetTile(changed[i].x, changed[i].y); if (t == null) continue;
                char L = rules.GetCell(changed[i].x, changed[i].y).Letter;
                t.MarkAnimating(REFRESH_FLIP * 2f + 0.05f);
                var tr = t.transform; Vector3 s0 = tr.localScale; tr.DOKill();
                DOTween.Sequence()
                    .Append(tr.DOScaleX(s0.x * 0.05f, REFRESH_FLIP).SetEase(Ease.InQuad))
                    .AppendCallback(() => { if (t != null) t.SetLetter(L); })
                    .Append(tr.DOScaleX(s0.x, REFRESH_FLIP).SetEase(Ease.OutBack, 2f))
                    .OnKill(() => { if (tr != null) tr.localScale = s0; });
                yield return new WaitForSeconds(REFRESH_STAGGER);
            }
            yield return new WaitForSeconds(REFRESH_FLIP * 2f + 0.1f);
            grid.SyncToRulesState(rules);
            rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
            if (card != null) card.DOFade(0f, 0.25f).OnComplete(() => { if (card != null) Destroy(card.gameObject); });
            HandManager.NoWordStreak = 0;
            HintManager.Instance?.ClearForcedHint();
            _checkedSig = BoardSig(rules); _goalDead = false;
            SwipeWordInput.Instance?.DropRemembered();
            hm.IsInteractable = true;
            Running = false;
            HintManager.Instance?.ForceShowHint(false, true);   // show the word the fresh letters made (10-30)
        }

        /// <summary>"No words! Shuffling" over the board centre - plain text with a soft shadow, no pill (10-29 Spencer called the
        /// BONUS! pill cheesy).</summary>
        public static bool SHOW_CARDS = false;   // 10-30: no shuffle text until the display-text style is approved (Spencer: text "looks cheezy")
        private static TextMeshPro ShowCard(GridManager grid, string text = null)
        {
            if (!SHOW_CARDS) return null;
            var go = new GameObject("ShuffleCard");
            var tmp = go.AddComponent<TextMeshPro>();
            var font = Resources.Load<TMP_FontAsset>("LilitaOne Button SDF"); if (font != null) tmp.font = font;
            tmp.text = text ?? "No words!\n<size=70%>Shuffling...</size>";
            tmp.alignment = TextAlignmentOptions.Center; tmp.fontSize = 9f; tmp.sortingOrder = 130;
            tmp.color = new Color(1f, 0.97f, 0.88f);
            var m = tmp.fontMaterial;
            m.EnableKeyword("UNDERLAY_ON");
            m.SetColor("_UnderlayColor", new Color(0.12f, 0.03f, 0.25f, 0.85f));
            m.SetFloat("_UnderlayOffsetY", -0.7f); m.SetFloat("_UnderlaySoftness", 0.35f); m.SetFloat("_UnderlayDilate", 0.4f);
            go.transform.position = grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2) + new Vector3(0f, 0f, -2f);
            if (RulesEngine.COLS % 2 == 0) go.transform.position -= new Vector3(grid.CellSize * 0.5f, 0f, 0f);
            go.transform.localScale = Vector3.one * 0.6f;
            go.transform.DOScale(1f, CARD_IN).SetEase(Ease.OutBack, 2f);
            tmp.alpha = 0f; tmp.DOFade(1f, CARD_IN * 0.8f);
            return tmp;
        }
    }
}
