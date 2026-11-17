using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// BOT PLAYTESTER (2026-10-23 Spencer: "build the bot please"). Plays each full-board level many times on a HIDDEN second
    /// RulesEngine and reports win rate, moves left, words, specials — so level difficulty is measured, not guessed.
    ///
    /// SAFETY: one game per frame. Around EVERY game the live goal/rule statics are saved and restored
    /// (LevelRules.TakeSimSnapshot / StageStats.Save), RulesEngine.Simulating silences every reach into the live board / audio /
    /// FX / objectives, and WDLog is muted. The visible game is never touched; between games it runs normally.
    ///
    /// HOW IT PLAYS (the real loop, data-only): pick a swap (the hint's usefulness score — length, power-ups, goal progress — with
    /// noise), swap the cells' letter/special/gold/gem like TryBoardSwap, arm the words the swap makes, then pop every armed word
    /// through BeginTapDetonation -> NextStep -> FinalizeDrop (cascades arm themselves; keep popping until none are left), collect
    /// ducks on the bottom row (SurvivalManager's job in the game) and refund a move per gold letter that pops (GrantBonusMoves).
    ///
    /// LIMITS: it knows every dictionary word (finds more than a person) but plans one move ahead; never swaps specials into each
    /// other (no combos). Calibrate once against Spencer's own play.
    /// Output: UnityProject/BotReports/bot_latest.txt (+ timestamped .csv). FX Test Menu: "BOT PLAYTEST".
    /// </summary>
    public class BotPlaytester : MonoBehaviour
    {
        public static BotPlaytester Instance { get; private set; }
        public static int   FIRST_LEVEL = 1, LAST_LEVEL = 20, TRIALS = 40;
        /// <summary>Share of the CASUAL bot's moves that make no word (calibrated from Spencer's 10-23 run: 54%). 0 = old bot.</summary>
        public static float CASUAL_NOWORD_RATE = 0.5f;
        private static readonly System.Random s_shuffleRng = new System.Random(1234);
        private static int GoalPiecesLeftSim() => RulesEngine.GoalPiecesLeft();
        /// <summary>Casual bot only sees COMMON words (people don't spot GAGE / TARO). Stale turn = fewer than STALE_BELOW common-word swaps.</summary>
        public static bool  CASUAL_COMMON_ONLY = true;
        /// <summary>Model the game's fishing hint: after FISHING_SWAPS no-word moves the casual bot plays the hint.</summary>
        public static bool  FISHING_HINT_MODEL = true;
        public static int   STALE_BELOW = 3;   // 10 -> 20 -> 40 (2026-10-23: 20 games = ±10pp, too coarse to tune budgets by 2-move steps)
        public static float CHOICE_NOISE = 0.5f;
        public static int   CASUAL_SEES = 4;        // CASUAL bot: words it 'notices' per move (research: model a limited-vision player)
        public static int   UNCAPPED_MOVES = 80;
        public static int   NEAR_MISS_PIECES = 3;   // a loss with this few goal pieces left = a Play On moment    // games play to the finish; over this = did not finish   // score * (1 + rand[0, noise]) — some variety, still plays sensibly
        public bool Running { get; private set; }
        public string Status { get; private set; } = "";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("BotPlaytester"); DontDestroyOnLoad(go);
            Instance = go.AddComponent<BotPlaytester>();
        }

        public static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "BotReports");

        public void Run() { if (!Running) { s_only = null; StartCoroutine(RunRoutine(FIRST_LEVEL, LAST_LEVEL, TRIALS)); } }
        // SELECTED LEVELS (2026-10-30, Spencer: "one good thing is that we can iterate and tune very quickly"): run only the levels
        // that changed, with more games each (tighter numbers) - ~4 min instead of ~25. SELECTED = "11,17,19" style list.
        public static string SELECTED = "8,18,19"; public static int SELECTED_TRIALS = 100;   // 11-03: shelf + safe re-check (was "11,17,19")
        private static System.Collections.Generic.HashSet<int> s_only;
        public void RunSelected()
        {
            if (Running) return;
            s_only = new System.Collections.Generic.HashSet<int>();
            foreach (var part in (SELECTED ?? "").Split(',', ' ', ';'))
                if (int.TryParse(part.Trim(), out int v) && v >= 1) s_only.Add(v);
            if (s_only.Count == 0) { Status = "BOT: no levels selected"; return; }
            int lo = int.MaxValue, hi = 0; foreach (var v in s_only) { lo = Mathf.Min(lo, v); hi = Mathf.Max(hi, v); }
            StartCoroutine(RunRoutine(lo, hi, SELECTED_TRIALS));
        }

        public static int GOAL_HELP_DEAD_TURNS = 3;   // bot proxy for 'stuck' (the game uses misses / idle seconds)
        private class GameResult { public string goals = ""; public bool won, error; public int remainingAtBudget = -1; public int drags, used, left, words, w4, w5, w6, specialsMade, specialsFired, goldRefunds, cascadeWords, noWordMoves, shuffles, refreshes, goalDeadTurns, endDeadTurns, turns, staleTurns, commonSum; public long points; }

        private IEnumerator RunRoutine(int first, int last, int trials)
        {
            Running = true;
            // Keep playing while Unity isn't the focused window (2026-10-23 Spencer: "is there anyway i can run the test in the
            // background while i do other things?"). Play mode pauses on focus loss unless runInBackground; restored at the end.
            bool prevBg = Application.runInBackground;
            Application.runInBackground = true;
            if (!SurvivalManager.FullBoardActive) { Status = "BOT: switch the board to FULL first"; Debug.LogWarning("[Bot] full board is off — nothing to test"); Running = false; Application.runInBackground = prevBg; yield break; }
            RulesEngine.CreatingSimulation = true;
            var simGO = new GameObject("BotSimEngine");
            var sim = simGO.AddComponent<RulesEngine>();
            RulesEngine.CreatingSimulation = false;
            simGO.hideFlags = HideFlags.HideInHierarchy;

            var csv = new StringBuilder("level,name,mode,trial,goals,finished,net_moves,budget,win_at_budget,moves_used,words,w4,w5,w6plus,cascade_words,no_word_moves,specials_made,specials_fired,gold_refunds,points\n");
            var summary = new StringBuilder();
            summary.AppendLine($"BOT PLAYTEST {System.DateTime.Now:yyyy-MM-dd HH:mm}  levels {(s_only != null ? string.Join(",", s_only) : first + "-" + last)}  x{trials} games per bot  (EXPERT sees every word; CASUAL sees {CASUAL_SEES} per move)");
            summary.AppendLine("Games play UNCAPPED; 'net' = moves to finish after gold refunds. win% = share of games finishing within today's budget.");
            summary.AppendLine("Suggested budget = the CASUAL bot's 95th-percentile net moves (~95% of casual players would finish).\n");
            summary.AppendLine("LVL  name                      budget | EXPERT win%  net p50/p90 | CASUAL win%  net p50/p90  left(wins)  near-miss | SUGGEST | goals");
            var t0 = Time.realtimeSinceStartup;
            for (int lvl = first; lvl <= last; lvl++)
            if (s_only == null || s_only.Contains(lvl))
            {
                if (LevelRules.For(lvl).Bonus) { summary.AppendLine($"{lvl,3}  {Trunc(LevelRules.For(lvl).Name, 24),-24}  bonus level — no fail, not tested"); continue; }
                int budget = 0;
                System.Array.Clear(RulesEngine.SimFiredByKind, 0, RulesEngine.SimFiredByKind.Length);
                var byMode = new List<GameResult>[2];
                for (int mode = 0; mode < 2; mode++)
                {
                    bool casual = mode == 1;
                    byMode[mode] = new List<GameResult>();
                    for (int t = 0; t < trials; t++)
                    {
                        Status = $"BOT: level {lvl}/{last}  {(casual ? "casual" : "expert")} game {t + 1}/{trials}";
                        var r = PlayOneSafely(sim, lvl, casual, out budget);
                        byMode[mode].Add(r);
                        int net = r.used - r.goldRefunds;
                        csv.AppendLine($"{lvl},{LevelRules.For(lvl).Name.Replace(',', ' ')},{(casual ? "casual" : "expert")},{t},{r.goals},{(r.won ? 1 : 0)},{net},{budget},{(r.won && net <= budget ? 1 : 0)},{r.used},{r.words},{r.w4},{r.w5},{r.w6},{r.cascadeWords},{r.noWordMoves},{r.specialsMade},{r.specialsFired},{r.goldRefunds},{r.points}");
                        yield return null;   // one game per frame — the editor stays responsive
                    }
                }
                var ex = Stats(byMode[0], budget); var ca = Stats(byMode[1], budget);
                string goals = byMode[0].Count > 0 ? byMode[0][0].goals : "";
                summary.AppendLine($"{lvl,3}  {Trunc(LevelRules.For(lvl).Name, 24),-24}  {budget,4}   |   {ex.win,5:F0}%     {ex.p50,3}/{ex.p90,-3}  |   {ca.win,5:F0}%     {ca.p50,3}/{ca.p90,-3}     {ca.leftPct,5:F0}%      {ca.nearMiss,4:F0}%    |  {ca.p95,4}   | {goals}{(ex.errs + ca.errs > 0 ? $"  ERRORS {ex.errs + ca.errs}" : "")}");
                summary.AppendLine($"       fired per game (both bots): {FiredLabel(byMode[0].Count + byMode[1].Count)}");
                { int tt = 0, st = 0; long cs = 0; foreach (var r in byMode[1]) { tt += r.turns; st += r.staleTurns; cs += r.commonSum; }
                  int gd = 0, rf = 0, sh = 0, gw = 0, ed = 0, dg = 0, games = byMode[1].Count; foreach (var r in byMode[1]) { dg += r.drags; gd += r.goalDeadTurns; rf += r.refreshes; sh += r.shuffles; ed += r.endDeadTurns; if (r.refreshes > 0) gw++; }
                  summary.AppendLine($"       casual board: {(tt > 0 ? (float)cs / tt : 0):F1} common-word swaps per turn, STALE (<{STALE_BELOW}) {(tt > 0 ? 100f * st / tt : 0):F0}% of turns, GOAL-DEAD {(tt > 0 ? 100f * gd / tt : 0):F0}% of turns END-STALL {(games > 0 ? (float)ed / games : 0):F1} turns/game stuck on the last {RulesEngine.ENDGAME_PIECES}, goal help {(games > 0 ? (float)rf / games : 0):F1}/game in {(games > 0 ? 100f * gw / games : 0):F0}% of games (board shuffles {(games > 0 ? (float)sh / games : 0):F1}/game), DRAGS {(games > 0 ? (float)dg / games : 0):F1}/game"); }
            }
            Destroy(simGO);
            summary.AppendLine($"\n{(Time.realtimeSinceStartup - t0):F0}s. Board {RulesEngine.COLS}x{s_rowsSeen}. 'left(wins)' = casual moves left as % of budget on its wins (target 25-40% early). 'near-miss' = casual games that run out with 1-{NEAR_MISS_PIECES} pieces left (Play On moments). Cascade cap {CapLabel}. Casual wastes {CASUAL_NOWORD_RATE:P0} of moves on no-word swaps (human-calibrated), sees common words only: {CASUAL_COMMON_ONLY}, fishing hint: {FISHING_HINT_MODEL}, endgame airplane: {RulesEngine.ENDGAME_AIRPLANE}, re-letter rescue: {RulesEngine.ENDGAME_RESCUE_ON}.");
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(Path.Combine(Folder, "bot_latest.txt"), summary.ToString());
                File.WriteAllText(Path.Combine(Folder, $"bot_{System.DateTime.Now:yyyyMMdd_HHmmss}.csv"), csv.ToString());
                File.WriteAllText(Path.Combine(Folder, "bot_latest.csv"), csv.ToString());
            }
            catch (System.Exception ex) { Debug.LogError($"[Bot] write failed: {ex.Message}"); }
            WDLog.Log("[Bot] DONE\n" + summary);
            Status = "BOT: done — BotReports/bot_latest.txt";
            Running = false;
            Application.runInBackground = prevBg;
        }

        private struct Summ { public float win, leftPct, nearMiss; public int p50, p90, p95, errs; }
        private static Summ Stats(List<GameResult> rs, int budget)
        {
            var nets = new List<int>(); int wins = 0, errs = 0, near = 0; float left = 0;
            foreach (var r in rs)
            {
                if (r.error) { errs++; continue; }
                int net = r.won ? r.used - r.goldRefunds : 999;   // didn't finish even uncapped
                nets.Add(net);
                if (net <= budget) { wins++; left += budget - net; }
                else if (r.remainingAtBudget >= 1 && r.remainingAtBudget <= NEAR_MISS_PIECES) near++;
            }
            nets.Sort();
            int P(float q) => nets.Count == 0 ? 0 : nets[Mathf.Clamp(Mathf.CeilToInt(q * nets.Count) - 1, 0, nets.Count - 1)];
            return new Summ { win = nets.Count == 0 ? 0 : 100f * wins / nets.Count, leftPct = wins == 0 || budget == 0 ? 0 : 100f * left / wins / budget,
                              p50 = P(0.5f), p90 = P(0.9f), p95 = P(0.95f), errs = errs, nearMiss = nets.Count == 0 ? 0 : 100f * near / nets.Count };
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

        /// <summary>One simulated game, fenced so the live game's statics come back exactly as they were, whatever happens.</summary>
        private GameResult PlayOneSafely(RulesEngine sim, int level, bool casual, out int budget)
        {
            budget = 0;
            var snap = LevelRules.TakeSimSnapshot();
            bool rngWasSeeded = SurvivalRng.IsSeeded; int rngSeed = SurvivalRng.CurrentSeed;
            var stats = StageStats.Save(); var planted = StageStats.SavePlanted();
            bool wasMuted = WDLog.Muted;
            // Play the REAL level ruleset even when launched from the menu: RulesEngine.ROWS is 5 outside survival/level mode,
            // and the refill plants / smart rain letters are survival-gated too (runs 1-2 played a 9x5 board — crates didn't fit).
            bool wasSurvival = SurvivalManager.IsSurvivalMode;
            SurvivalManager.IsSurvivalMode = true;
            RulesEngine.Simulating = true; WDLog.Muted = true;
            try { return PlayOne(sim, level, casual, out budget); }
            catch (System.Exception ex)
            {
                WDLog.Muted = wasMuted;
                Debug.LogWarning($"[Bot] level {level} game threw: {ex.Message}\n{ex.StackTrace}");
                return new GameResult { error = true };
            }
            finally
            {
                sim.FinalizeDrop();   // a game that ended mid-resolution must not leave the sim engine stuck
                RulesEngine.Simulating = false; WDLog.Muted = wasMuted;
                SurvivalManager.IsSurvivalMode = wasSurvival;
                LevelRules.RestoreSimSnapshot(snap);
                StageStats.Load(stats, planted);
                if (rngWasSeeded) SurvivalRng.SetSeed(rngSeed); else SurvivalRng.Reset();   // hand the live game its RNG back
            }
        }

        private static int s_rowsSeen;
        private GameResult PlayOne(RulesEngine sim, int level, bool casual, out int budget)
        {
            var res = new GameResult();
            int internalStage = level + LevelMapPanel.TUTORIAL_LEVELS;
            if (SurvivalRng.LEVEL_SEEDS) SurvivalRng.SetSeed(SurvivalRng.LevelSeed(internalStage));   // 11-15: the board testers get
            budget = LevelRules.SetupSimLevel(sim, level, internalStage);
            s_rowsSeen = RulesEngine.ROWS;   // reported — runs 1-2 silently played 5 rows
            bool scoreLevel = LevelRules.Goals.Count == 0;
            foreach (var g in LevelRules.Goals) res.goals += (res.goals.Length > 0 ? " + " : "") + $"{g.Type} {g.Target}";   // what THIS board actually got
            if (scoreLevel) res.goals = $"Points {SurvivalManager.ComputeStageTarget(level)}";
            int target = scoreLevel ? SurvivalManager.ComputeStageTarget(level) : 0;
            int left = UNCAPPED_MOVES;   // play to the finish; the report judges against the budget afterwards
            int guard = 0, missStreak = 0, bounces = 0, deadStreak = 0; bool hintNext = false;
            while (left > 0 && guard++ < 200)
            {
                if (GoalsDone(sim, scoreLevel, res.points, target)) break;
                sim.TryStaleRescue(out _, out _, out _);   // the game re-letters a tile on a stale board (HintManager.StaleCheck)
                // GOAL AREA DEAD (2026-10-30): goals remain but no common word reaches one -> the game re-deals the letters around the
                // goals ("Fresh letters!"). Counted BEFORE the fix, so 'goal-dead %' says how often the board would have stalled.
                // 10-30: ONLY WHEN STRUGGLING, like the game (BoardShuffle: 3 misses in a row, or 12s idle). The bot's proxy: its miss
                // streak hits GOAL_HELP_MISSES, or the goal area has stayed dead GOAL_HELP_DEAD_TURNS turns in a row (it kept playing
                // elsewhere and the fall never fixed it). Spencer: "if it has to reshuffle just for them to pass a level thats not good".
                bool goalDead = GoalPiecesLeftSim() > 0 && sim.GoalAreaDead();
                if (casual && goalDead && GoalPiecesLeftSim() <= RulesEngine.ENDGAME_PIECES) res.endDeadTurns++;   // stuck on the LAST pieces
                if (casual && goalDead)
                {
                    res.goalDeadTurns++; deadStreak++;
                    if (RulesEngine.GOAL_REFRESH_ON && res.refreshes < BoardShuffle.GOAL_HELP_MAX_PER_LEVEL && (missStreak >= BoardShuffle.GOAL_HELP_MISSES || deadStreak >= GOAL_HELP_DEAD_TURNS)
                        && (sim.ShuffleGoalArea(s_shuffleRng) != null || sim.RefreshGoalArea() != null)) { res.refreshes++; deadStreak = 0; hintNext = true; }   // the game then SHOWS the word (ForceShowHint)   // shuffle first, like the game
                }
                else deadStreak = 0;
                // NO WORDS -> SHUFFLE (2026-10-30): the game re-deals the plain letters when no swap makes a common word
                if (HandManager.BOUNCE_NO_WORD && BoardShuffle.ON && sim.CountCommonWordSwaps() < BoardShuffle.SHUFFLE_BELOW)
                    { sim.ShuffleForWords(BoardShuffle.TARGET_SWAPS, s_shuffleRng); res.shuffles++; }
                var cands = sim.EnumerateWordSwaps(casual && CASUAL_COMMON_ONLY);   // casual: only words a person would spot
                if (casual) { res.turns++; int common = CASUAL_COMMON_ONLY ? cands.Count : sim.EnumerateWordSwaps(true).Count; res.commonSum += common; if (common < STALE_BELOW) res.staleTurns++; }
                // HUMAN WASTE (2026-10-23, Spencer's full run L1-10: 100 of 185 swaps made NO word — setups, fishing, misses;
                // L10 Cold Snap 24 of 42). The bots never did that, so every budget assumed a perfectly efficient player and
                // L10 "seemed impossible". The casual bot now spends that share of its moves on a swap that makes no word.
                // FISHING HINT model: after HintManager.FISHING_SWAPS misses in a row the game shows the goal-aware hint — the
                // casual player takes it (no waste roll that turn, and the hint's pick replaces its own).
                bool hinted = casual && FISHING_HINT_MODEL && (missStreak >= HintManager.FISHING_SWAPS || hintNext); hintNext = false;
                if (hinted && sim.FindSwapHint(false, out var ha, out var hb, out _)) { cands.Clear(); cands.Add((ha, hb, 1)); }
                else if (casual && CASUAL_NOWORD_RATE > 0f && Random.value < CASUAL_NOWORD_RATE) cands.Clear();
                if (casual && cands.Count > CASUAL_SEES)
                {   // a person spots a handful of the available words, not all of them — keep a random few
                    for (int i = 0; i < CASUAL_SEES; i++) { int j = Random.Range(i, cands.Count); var tmp = cands[i]; cands[i] = cands[j]; cands[j] = tmp; }
                    cands.RemoveRange(CASUAL_SEES, cands.Count - CASUAL_SEES);
                }
                // DRAG (10-31, Spencer: "teach it to drag"): words already lying on the board. A casual player notices one only
                // some of the time (BOT_DRAG_SEE) and takes it when it beats the best swap it sees; the expert always considers them.
                if (BOT_DRAGS && !hinted)
                {
                    var drags = sim.EnumerateSwipeWords(casual && CASUAL_COMMON_ONLY);
                    if (drags.Count > 0 && (!casual || Random.value < BOT_DRAG_SEE))
                    {
                        var pick = drags[0];
                        if (casual) pick = drags[Random.Range(0, drags.Count)];   // a person spots one, not the best
                        else foreach (var dw in drags) if (dw.score > pick.score) pick = dw;
                        float bestSwap = 0f; foreach (var c in cands) bestSwap = Mathf.Max(bestSwap, c.score);
                        if (pick.score * (1f + Random.Range(0f, CHOICE_NOISE)) >= bestSwap)
                        {
                            RulesEngine.GoalDeadAtSwap = goalDead;
                            DoDrag(sim, pick.m, res);
                            left--; res.used++; res.drags++; missStreak = 0;
                            left += ResolveEverything(sim, res);
                            if (res.remainingAtBudget < 0 && res.used - res.goldRefunds >= budget)
                                res.remainingAtBudget = GoalsDone(sim, scoreLevel, res.points, target) ? 0 : GoalPiecesLeft(sim);
                            continue;
                        }
                    }
                }
                Vector2Int a, b;
                if (cands.Count > 0)
                {
                    float best = float.MinValue; a = b = default;
                    foreach (var c in cands)
                    {
                        float v = c.score * (1f + Random.Range(0f, CHOICE_NOISE));
                        if (v > best) { best = v; a = c.a; b = c.b; }
                    }
                }
                else if (HandManager.BOUNCE_NO_WORD)
                {   // BOUNCE-BACK (2026-10-30): a no-word swap slides back and costs nothing - the miss still feeds the fishing hint
                    res.noWordMoves++; missStreak++;
                    if (++bounces > 400) break;   // safety: a board with nothing findable even after a shuffle
                    continue;
                }
                else if (!RandomLegalSwap(sim, out a, out b)) break;   // nothing movable at all
                else res.noWordMoves++;
                missStreak = cands.Count > 0 ? 0 : missStreak + 1;

                RulesEngine.GoalDeadAtSwap = goalDead;   // stranded goal: a special this swap earns becomes an airplane (like the game)
                DoSwap(sim, a, b);
                left--; res.used++;
                ArmSwapWords(sim, a, b, res);
                left += ResolveEverything(sim, res);
                if (res.remainingAtBudget < 0 && res.used - res.goldRefunds >= budget)
                    res.remainingAtBudget = GoalsDone(sim, scoreLevel, res.points, target) ? 0 : GoalPiecesLeft(sim);   // what the player would see as moves hit 0
            }
            res.won = GoalsDone(sim, scoreLevel, res.points, target);
            res.left = left;
            return res;
        }

        private static string FiredLabel(int games)
        {
            var k = RulesEngine.SimFiredByKind; float g = Mathf.Max(1, games);
            int combos = 0; for (int i = RulesEngine.SPECIAL_COMBO_CROSS; i <= RulesEngine.SPECIAL_COMBO_DISCO_DISCO; i++) combos += k[i];
            return $"rocket {(k[RulesEngine.SPECIAL_ROCKET_H] + k[RulesEngine.SPECIAL_ROCKET_V]) / g:F1}  bomb {k[RulesEngine.SPECIAL_BOMB] / g:F1}  disco {k[RulesEngine.SPECIAL_DISCO] / g:F1}  " +
                   $"TNT {k[RulesEngine.SPECIAL_TNT] / g:F1}  airplane {k[RulesEngine.SPECIAL_AIRPLANE] / g:F1}  combos {combos / g:F1}";
        }
        private static string CapLabel => RulesEngine.CASCADE_CAP_ON ? $"ON ({RulesEngine.CASCADE_FREE_PER_MOVE} free fall word/move)" : "OFF";

        private static int GoalPiecesLeft(RulesEngine sim)
        {
            int n = 0;
            foreach (var g in LevelRules.Goals)
                switch (g.Type)
                {
                    case LevelRules.GoalType.Crates: n += sim.CountCrateCells(); break;
                    case LevelRules.GoalType.Ice:    n += sim.CountFrozenCells(); break;
                    case LevelRules.GoalType.Books:  n += sim.CountShelfBooks(); break;   // BOOKSHELF (11-03)
                    case LevelRules.GoalType.Safes:  n += sim.CountSafes(); break;        // SAFE (11-03)
                    case LevelRules.GoalType.Theme:  n += Mathf.Max(0, g.Target - LevelRules.ThemeMade); break;
                    default:                         n += Mathf.Max(0, g.Target - LevelRules.Collected); break;
                }
            return n;
        }

        private static bool GoalsDone(RulesEngine sim, bool scoreLevel, long points, int target)
        {
            if (scoreLevel) return points >= target;
            foreach (var g in LevelRules.Goals)
            {
                switch (g.Type)
                {
                    case LevelRules.GoalType.Crates: if (sim.CountCrateCells() > 0) return false; break;
                    case LevelRules.GoalType.Ice:    if (sim.CountFrozenCells() > 0) return false; break;
                    case LevelRules.GoalType.Books:  if (sim.CountShelfBooks() > 0) return false; break;   // BOOKSHELF (11-03)
                    case LevelRules.GoalType.Safes:  if (sim.CountSafes() > 0) return false; break;        // SAFE (11-03)
                    case LevelRules.GoalType.Theme:  if (LevelRules.ThemeMade < g.Target) return false; break;
                    default:                         if (LevelRules.Collected < g.Target) return false; break;   // gems / collect / ducks
                }
            }
            return true;
        }

        private static bool RandomLegalSwap(RulesEngine sim, out Vector2Int a, out Vector2Int b)
        {
            var opts = new List<(Vector2Int, Vector2Int)>();
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 0; y < RulesEngine.ROWS; y++)
                {
                    var ca = sim.GetCell(x, y); if (!RulesEngine.IsBotMovable(ca)) continue;
                    if (x + 1 < RulesEngine.COLS) { var cb = sim.GetCell(x + 1, y); if (RulesEngine.IsBotMovable(cb) && cb.Letter != ca.Letter) opts.Add((new Vector2Int(x, y), new Vector2Int(x + 1, y))); }
                    if (y + 1 < RulesEngine.ROWS) { var cb = sim.GetCell(x, y + 1); if (RulesEngine.IsBotMovable(cb) && cb.Letter != ca.Letter) opts.Add((new Vector2Int(x, y), new Vector2Int(x, y + 1))); }
                }
            a = b = default;
            if (opts.Count == 0) return false;
            var p = opts[Random.Range(0, opts.Count)]; a = p.Item1; b = p.Item2; return true;
        }

        /// <summary>Exactly TryBoardSwap's data exchange.</summary>
        private static void DoSwap(RulesEngine sim, Vector2Int a, Vector2Int b)
        {
            RulesEngine.BeginPlayerMove();   // per-move cascade budget, like the game's swap
            var c1 = sim.GetCell(a.x, a.y); var c2 = sim.GetCell(b.x, b.y);
            char l = c1.Letter; c1.Letter = c2.Letter; c2.Letter = l;
            int sk = c1.SpecialKind; c1.SpecialKind = c2.SpecialKind; c2.SpecialKind = sk;
            bool rg = c1.IsRainGold; c1.IsRainGold = c2.IsRainGold; c2.IsRainGold = rg;
            bool gm = c1.HasGem; c1.HasGem = c2.HasGem; c2.HasGem = gm;
            RulesEngine.LastSwapCells = new[] { a, b };
            RulesEngine.ShortWordCells = RulesEngine.LastSwapCells;
        }

        public static bool  BOT_DRAGS = true;
        public static float BOT_DRAG_SEE = 0.35f;   // casual: share of turns it notices a word already on the board (players under-use drags)
        /// <summary>SwipeCommitSequence's data: the dragged word is armed (no swapped cell), with the found-it special bonus.</summary>
        private static void DoDrag(RulesEngine sim, RulesWordMatch m, GameResult res)
        {
            RulesEngine.BeginPlayerMove();
            RulesEngine.LastSwapCells = null; RulesEngine.ShortWordCells = null;
            sim.RegisterScoredKey(m.Word + "|" + m.CellKey);
            int turn = sim.GlobalTurn;
            int id = sim.PrimedRegistry.AddPrimedWord(m.Word, m.Cells, MatchController.PLAYER_HUMAN, turn, turn + sim.GetFuseLengthPublic(m.Word.Length),
                RulesEngine.CalculateWordScore(m.Word, m.WildLetterIndices), false, sim.HasHighValueLetter(m));
            if (id >= 0 && RulesEngine.DRAG_REWARD) RulesEngine.SwipedPrimedIds.Add(id);
            CountWord(res, m.Word, false);
        }

        /// <summary>BoardSwapTurnSequence's arming: scan the two cells, register each new word as armed.</summary>
        private static void ArmSwapWords(RulesEngine sim, Vector2Int a, Vector2Int b, GameResult res)
        {
            var found = sim.ScanSeedCellsPublic(new List<Vector2Int> { a, b });
            if (found == null) return;
            var armed = new List<RulesWordMatch>();
            foreach (var m in found)
            {
                if (m == null || m.Cells == null || string.IsNullOrEmpty(m.Word)) continue;
                string key = m.Word + "|" + m.CellKey;
                if (sim.IsClaimBlocked(key)) continue;
                sim.RegisterScoredKey(key);
                armed.Add(m);
                int turn = sim.GlobalTurn;
                sim.PrimedRegistry.AddPrimedWord(m.Word, m.Cells, MatchController.PLAYER_HUMAN, turn, turn + sim.GetFuseLengthPublic(m.Word.Length),
                    RulesEngine.CalculateWordScore(m.Word, m.WildLetterIndices), false, sim.HasHighValueLetter(m));
                CountWord(res, m.Word, false);
            }
            RulesEngine.NoteSwapWords(armed);   // airplane, like the game's swap
        }

        private static void CountWord(GameResult res, string w, bool cascade)
        {
            res.words++; if (cascade) res.cascadeWords++;
            if (w.Length >= 6) res.w6++; else if (w.Length == 5) res.w5++; else if (w.Length == 4) res.w4++;
        }

        /// <summary>Pop every armed word (cascades arm themselves), collect landed ducks, repeat until quiet. Returns moves refunded.</summary>
        private static int ResolveEverything(RulesEngine sim, GameResult res)
        {
            int refunds = 0, guard = 0;
            while (guard++ < 60)
            {
                var armed = sim.PrimedRegistry.GetAllPrimedWords();
                if (armed.Count > 0)
                {
                    var cell = armed[0].Cells != null && armed[0].Cells.Count > 0 ? armed[0].Cells[0] : new Vector2Int(-1, -1);
                    if (cell.x < 0 || !sim.BeginTapDetonation(cell, MatchController.PLAYER_HUMAN)) { sim.PrimedRegistry.Clear(); continue; }
                    refunds += RunSteps(sim, res);
                    sim.FinalizeDrop();
                    continue;
                }
                var ducks = sim.DucksOnBottomRow();
                if (ducks != null && ducks.Count > 0)
                {
                    foreach (var d in ducks) { sim.ClearCell(d.x, d.y); LevelRules.Collected++; }
                    var moves = sim.BeginCascadeAfterBoosterClear(ducks, MatchController.PLAYER_HUMAN);
                    if (moves != null && moves.Count > 0) refunds += RunSteps(sim, res);
                    sim.FinalizeBoosterCascade();
                    continue;
                }
                break;
            }
            res.goldRefunds += refunds;
            return refunds;
        }

        /// <summary>Drive NextStep to Complete, counting cascade words, specials, points and gold letters popped (one move each).</summary>
        private static int RunSteps(RulesEngine sim, GameResult res)
        {
            int refunds = 0, guard = 0;
            HashSet<Vector2Int> goldBefore = null;
            long stepPoints = 0;
            while (guard++ < 400)
            {
                var step = sim.NextStep();
                if (step == null) break;
                if (step.TotalScore > stepPoints) stepPoints = step.TotalScore;
                if (step.Phase == RulesEngine.ResolutionPhase.TriggersFound) goldBefore = GoldCells(sim);
                if (step.Phase == RulesEngine.ResolutionPhase.Exploding)
                {
                    if (goldBefore != null && HandManager.GoldToMovesActive)
                        foreach (var g in goldBefore) { var c = sim.GetCell(g.x, g.y); if (c == null) refunds++; }
                    goldBefore = null;
                    if (step.SpecialsCreated != null) res.specialsMade += step.SpecialsCreated.Count;
                    if (step.SpecialsFired != null)   res.specialsFired += step.SpecialsFired.Count;
                }
                if (step.Phase == RulesEngine.ResolutionPhase.WordsScored && step.ScoredWords != null)
                    foreach (var w in step.ScoredWords) if (w != null && !string.IsNullOrEmpty(w.Word)) CountWord(res, w.Word, true);
                if (step.Phase == RulesEngine.ResolutionPhase.Complete) break;
            }
            res.points += stepPoints;
            return refunds;
        }

        private static HashSet<Vector2Int> GoldCells(RulesEngine sim)
        {
            var set = new HashSet<Vector2Int>();
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 0; y < RulesEngine.ROWS; y++)
                {
                    var c = sim.GetCell(x, y);
                    if (c != null && (c.IsRainGold || (char.IsLetter(c.Letter) && LetterData.GetPoints(c.Letter) >= Tile.HIGH_VALUE_POINTS))) set.Add(new Vector2Int(x, y));
                }
            return set;
        }
    }
}
