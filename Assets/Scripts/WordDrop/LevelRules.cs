using UnityEngine;

namespace WordDrop
{
    /// <summary>PER-STAGE RULES — the seed of the level generator. 2026-10-19 Spencer.
    ///
    /// The game is SCORE-ONLY: a stage clears when you bank its target, and nothing else.
    /// So a "level" is not an objective — it is a set of RULE TWEAKS plus a score target, and
    /// the design lever is WHERE THE POINTS ARE. Put most of a level's target inside ice and
    /// it becomes an ice level without a single line of objective text, because the efficient
    /// route to the target runs through the ice.
    ///
    /// Deliberately a small hand-written table for the first stages, then a formula. That order
    /// matters: the early stages are the ones players actually see, and they want hand-placed
    /// beats. Past the table it repeats the shape with counts climbing.
    ///
    /// EVERY FIELD HERE MAPS TO SOMETHING ALREADY IN THE ENGINE — no new systems:
    ///   IceCount      -> RulesEngine.SpawnFrozenTiles (existed; only IceObjective called it,
    ///                    and objectives do not run in score mode, so ice never appeared)
    ///   MinWordLength -> RulesEngine.MIN_WORD_LENGTH  (was a private const)
    ///   RiseCadence   -> SurvivalManager rise cadence (the real pressure dial)
    ///   BranchPriming -> RulesEngine.BranchPriming    (built, was off)
    ///   FuseOn        -> RulesEngine.FuseDisabled     (built, was off)
    ///   GoldTiles     -> RulesEngine.GoldTilesEnabled (built, off since June)
    /// </summary>
    public struct LevelRules
    {
        /// <summary>Shown to the player. ⚠️ IF A LEVEL CHANGES A RULE, THE NAME MUST SAY SO.
        /// 2026-10-19: level 4 set MIN_WORD_LENGTH = 4 and was called "Four Letter". Spencer
        /// swapped, made three-letter words, and nothing primed — which is indistinguishable
        /// from the game being broken. A rule the player cannot see is a bug no matter how
        /// deliberate it is.</summary>
        public string Name;
        public int    IceCount;       // frozen tiles seeded at stage start (each pays ICE_BOUNTY)
        public int    MinWordLength;  // 3 normally; 4 makes the board read completely differently
        public int    RiseCadence;    // moves per rise. 3 = gentle, 2 = normal, 1 = relentless
        public bool   BranchPriming;  // every new word must touch the existing web
        public bool   FuseOn;         // armed words expire if you sit on them
        public bool   GoldTiles;      // 2x score cells
        public int    FillRows;       // rows of letters on the FRESH board each level starts with
        public float  Density;        // how packed those rows are (0..1)
        public int    Crates;         // FULL BOARD only: crates placed at start; when > 0 the GOAL is "break every crate" (2026-10-21)
        public char   CollectLetter;  // FULL BOARD only: "Collect N of this letter" goal ('\0' = none) (2026-10-21)
        public int    CollectCount;
        public int    Gems;           // FULL BOARD only: "Collect N gems" goal (2026-10-22)
        public int    Ducks;          // FULL BOARD only: "Bring N ducks down" goal (2026-10-22)
        public string Theme;          // FULL BOARD only: "Make N <theme> words" goal — Resources/themes/<Theme>.txt (2026-10-22)
        public int    ThemeCount;
        public static bool HasGemGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Gems) return true; return false; } }
        /// <summary>How many more gems the board should carry right now: up to GEM_MIN_ON_BOARD, but never more than the goal
        /// still needs (gems already on the board count toward it).</summary>
        public static int GemGoalRemainingOnBoardNeed(int onBoard)
        {
            foreach (var g in Goals)
                if (g.Type == GoalType.Gems)
                {
                    int stillNeeded = g.Target - Collected;
                    return Mathf.Max(0, Mathf.Min(RulesEngine.GEM_MIN_ON_BOARD, stillNeeded) - onBoard);
                }
            return 0;
        }

        // ── LEVEL GOALS (2026-10-21 Spencer: "the objectives levels seem funner"). A level has one or two goals; it is won
        // when ALL are done (score then only feeds coins / the bonus). Goals belong to GoalsStage — the clear check re-runs
        // on the NEXT stage before it installs (the L2 -> L34 runaway), so a goal only counts on its own stage. ──
        public enum GoalType { Crates, Collect, Ice, Gems, Theme, Ducks }
        public class LevelGoal { public GoalType Type; public char Letter; public int Target; public string Theme; }

        // ── THEME WORDS (2026-10-22 Spencer: "sure" to theme words as the next goal) ───────────────────────────────
        // "Make 3 ANIMAL words". The word list is Resources/themes/<theme>.txt (draft lists, filtered to dictionary
        // words of 4-7 letters — Spencer reviews them). Counted when a theme word POPS (RulesEngine.DoExplode), and
        // near-theme-words are planted one swap from done so the goal is findable (RulesEngine.ThemePlantPool).
        // ── BOT PLAYTESTER SUPPORT (2026-10-23) ─────────────────────────────────────────────────────────────────────────────
        // The bot runs on its own hidden engine but goals live in THESE statics, so each simulated game saves the live values,
        // sets up its own, plays, and restores — the live level never sees a thing. SetupSimLevel mirrors Apply()'s FULL-BOARD
        // build call-for-call (fill, plants, short words, crates, TNT, gems, ducks, scrub, ice) — keep the two in step.
        public class SimSnapshot
        {
            public System.Collections.Generic.List<LevelGoal> Goals; public int GoalsStage, Collected, CollectAwaitingFly, ThemeMade, ThemeAwaitingFly, CrateGoal, CrateGoalStage;
            public float PendingThemeSince; public System.Collections.Generic.List<System.Collections.Generic.List<Vector2Int>> PendingThemeWords;
            public System.Collections.Generic.HashSet<string> ThemeWords;
            public int MinWordLength; public bool BranchPriming, GoldTiles;
            public System.Collections.Generic.List<string> ThemePlantPool; public Vector2Int[] ShortWordCells, LastSwapCells; public System.Collections.Generic.List<Vector2Int> LastRefillCells;
            public int RefillGoldPending, CratesBroken;
        }
        public static SimSnapshot TakeSimSnapshot() => new SimSnapshot
        {
            Goals = new System.Collections.Generic.List<LevelGoal>(Goals), GoalsStage = GoalsStage, Collected = Collected, CollectAwaitingFly = CollectAwaitingFly,
            ThemeMade = ThemeMade, ThemeAwaitingFly = ThemeAwaitingFly, CrateGoal = CrateGoal, CrateGoalStage = CrateGoalStage, PendingThemeSince = PendingThemeSince,
            PendingThemeWords = new System.Collections.Generic.List<System.Collections.Generic.List<Vector2Int>>(PendingThemeWords),
            ThemeWords = new System.Collections.Generic.HashSet<string>(s_themeWords),
            MinWordLength = RulesEngine.MIN_WORD_LENGTH, BranchPriming = RulesEngine.BranchPriming, GoldTiles = RulesEngine.GoldTilesEnabled,
            ThemePlantPool = RulesEngine.ThemePlantPool, ShortWordCells = RulesEngine.ShortWordCells, LastSwapCells = RulesEngine.LastSwapCells,
            LastRefillCells = new System.Collections.Generic.List<Vector2Int>(RulesEngine.LastRefillCells),
            RefillGoldPending = RulesEngine.RefillGoldPending, CratesBroken = RulesEngine.CratesBroken,
        };
        public static void RestoreSimSnapshot(SimSnapshot s)
        {
            if (s == null) return;
            Goals.Clear(); Goals.AddRange(s.Goals); GoalsStage = s.GoalsStage; Collected = s.Collected; CollectAwaitingFly = s.CollectAwaitingFly;
            ThemeMade = s.ThemeMade; ThemeAwaitingFly = s.ThemeAwaitingFly; CrateGoal = s.CrateGoal; CrateGoalStage = s.CrateGoalStage; PendingThemeSince = s.PendingThemeSince;
            PendingThemeWords.Clear(); PendingThemeWords.AddRange(s.PendingThemeWords);
            s_themeWords.Clear(); s_themeWords.UnionWith(s.ThemeWords);
            RulesEngine.MIN_WORD_LENGTH = s.MinWordLength; RulesEngine.BranchPriming = s.BranchPriming; RulesEngine.GoldTilesEnabled = s.GoldTiles;
            RulesEngine.ThemePlantPool = s.ThemePlantPool; RulesEngine.ShortWordCells = s.ShortWordCells; RulesEngine.LastSwapCells = s.LastSwapCells;
            RulesEngine.LastRefillCells.Clear(); RulesEngine.LastRefillCells.AddRange(s.LastRefillCells);
            RulesEngine.RefillGoldPending = s.RefillGoldPending; RulesEngine.CratesBroken = s.CratesBroken;
        }

        /// <summary>Build DISPLAY level <paramref name="stage"/> on the bot's engine and set its goals into the statics. Returns the
        /// move budget. Mirrors Apply()'s full-board branch.</summary>
        public static int SetupSimLevel(RulesEngine sim, int stage, int internalStage)
        {
            var r = For(stage);
            sim.ClearBoard();
            RulesEngine.MIN_WORD_LENGTH = Mathf.Max(3, r.MinWordLength);
            RulesEngine.BranchPriming   = r.BranchPriming;
            RulesEngine.GoldTilesEnabled = r.GoldTiles;
            RulesEngine.ShortWordCells = null; RulesEngine.LastSwapCells = null; RulesEngine.RefillGoldPending = 0;
            Goals.Clear(); Collected = 0; CollectAwaitingFly = 0; GoalsStage = internalStage; CrateGoal = 0; CrateGoalStage = internalStage;
            ThemeMade = 0; ThemeAwaitingFly = 0; PendingThemeWords.Clear(); s_themeWords.Clear(); RulesEngine.ThemePlantPool = null;
            if (!string.IsNullOrEmpty(r.Theme) && r.ThemeCount > 0)
            {
                var tl = LoadTheme(r.Theme);
                if (tl.Count > 0) { s_themeWords.UnionWith(tl); RulesEngine.ThemePlantPool = tl; Goals.Add(new LevelGoal { Type = GoalType.Theme, Theme = r.Theme, Target = r.ThemeCount }); }
            }
            if (r.Gems > 0)  Goals.Add(new LevelGoal { Type = GoalType.Gems, Target = r.Gems });
            if (r.Ducks > 0) Goals.Add(new LevelGoal { Type = GoalType.Ducks, Target = r.Ducks });
            if (r.CollectLetter != '\0' && r.CollectCount > 0) Goals.Add(new LevelGoal { Type = GoalType.Collect, Letter = char.ToUpperInvariant(r.CollectLetter), Target = r.CollectCount });
            sim.FillBoardNoWords();
            sim.SnapshotForRain(out var before, out var _);
            sim.PlantNearWordsOnBoard(RulesEngine.FULL_START_PLANTS, spreadRows: true);
            sim.SnapshotForRain(out var after, out var _);
            var keep = new System.Collections.Generic.HashSet<Vector2Int>();
            for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++) if (before[x, y] != after[x, y]) keep.Add(new Vector2Int(x, y));
            sim.SeedShortWords(RulesEngine.FULL_START_SHORT_WORDS, keep);
            int crates = r.Crates > 0 ? sim.PlaceCrates(Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT), keep) : 0;
            sim.PlaceStartTnt(RulesEngine.FULL_START_TNT, keep);
            if (r.Gems > 0) sim.PlaceGems(Mathf.Min(r.Gems, RulesEngine.GEM_MIN_ON_BOARD + 2), keep);
            if (r.Ducks > 0) sim.PlaceDucks(Mathf.Min(r.Ducks, RulesEngine.DUCKS_ON_BOARD), keep);
            CrateGoal = crates;
            if (crates > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Crates, Target = crates });
            sim.ScrubAccidentalWords(keep);
            if (r.IceCount > 0)
            {
                int placed = sim.SpawnFrozenTiles(r.IceCount);
                if (placed > 0) Goals.Add(new LevelGoal { Type = GoalType.Ice, Target = placed });
            }
            return MovesForRules(stage, r);
        }

        public static int ThemeMade;
        // THEME LETTERS FLY TO THE GOAL (2026-10-23 Spencer: "instead of the words that count towards the objective blowing up
        // ... those letters doing the fly up animation to the goal in the HUD"). OnWordPopped queues the word's cells; the
        // visual detonation lifts those tiles out of the explosion and flies each letter into the theme picture; the badge
        // ticks when the word's LAST letter lands (ThemeAwaitingFly), like gems. false = explode + "ANIMAL!" popup.
        public static bool THEME_FLY_TO_GOAL = true;
        public static int  ThemeAwaitingFly;
        public static float PendingThemeSince;
        public static readonly System.Collections.Generic.List<System.Collections.Generic.List<Vector2Int>> PendingThemeWords
            = new System.Collections.Generic.List<System.Collections.Generic.List<Vector2Int>>();
        public static bool HasDuckGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Ducks) return true; return false; } }
        /// <summary>Ducks the next refill should bring in: never more than DUCKS_ON_BOARD at once, never more than the goal still needs.</summary>
        public static int DuckRefillNeed(int onBoard)
        {
            foreach (var g in Goals)
                if (g.Type == GoalType.Ducks)
                    return Mathf.Max(0, Mathf.Min(RulesEngine.DUCKS_ON_BOARD - onBoard, g.Target - Collected - onBoard));
            return 0;
        }
        private static readonly System.Collections.Generic.HashSet<string> s_themeWords = new System.Collections.Generic.HashSet<string>();
        /// <summary>Theme levels let 3-letter THEME words count (2026-10-22 Spencer: "sure give it a shot") — PIG, CAT, COW are
        /// the first animals anyone looks for, and the full board's 4+ rule refused them.</summary>
        public static bool ThemeShortActive => SurvivalManager.FullBoardActive && s_themeWords.Count > 0 && ActiveTheme != null;
        public static bool IsThemeWord(string word)
        {
            if (string.IsNullOrEmpty(word) || s_themeWords.Count == 0) return false;
            string w = word.ToUpperInvariant();
            if (s_themeWords.Contains(w)) return true;
            char[] rv = w.ToCharArray(); System.Array.Reverse(rv);
            return s_themeWords.Contains(new string(rv));
        }
        public static string ActiveTheme { get { foreach (var g in Goals) if (g.Type == GoalType.Theme) return g.Theme; return null; } }
        /// <summary>Singular label: "ANIMAL" for the animals list — used in "ANIMAL!" pops and the briefing.</summary>
        /// <summary>Picture for a theme goal — something that SAYS the theme (2026-10-22: the book read as "collect 3 books").</summary>
        public static string ThemeIconPath(string theme)
        {
            if (string.IsNullOrEmpty(theme)) return "Goals/book";
            string p = "Goals/theme_" + theme.ToLowerInvariant();
            return Resources.Load<Texture2D>(p) != null ? p : "Goals/book";
        }
        public static string ThemeSingular(string theme)
        {
            if (string.IsNullOrEmpty(theme)) return "";
            string t = theme.ToUpperInvariant();
            return t.EndsWith("S") ? t.Substring(0, t.Length - 1) : t;
        }
        private static System.Collections.Generic.List<string> LoadTheme(string theme)
        {
            var list = new System.Collections.Generic.List<string>();
            var ta = Resources.Load<TextAsset>("themes/" + theme);
            if (ta == null) { Debug.LogWarning($"[Theme] themes/{theme}.txt missing"); return list; }
            foreach (var raw in ta.text.Split('\n'))
            {
                string w = raw.Trim().ToUpperInvariant();
                if (w.Length >= 3 && w.Length <= RulesEngine.COLS && WordDictionary.IsValidWord(w)) list.Add(w);
            }
            return list;
        }
        /// <summary>Called for every popped word. Counts theme words and shows "ANIMAL!" over the word.</summary>
        public static void OnWordPopped(string word, System.Collections.Generic.List<Vector2Int> cells)
        {
            if (string.IsNullOrEmpty(word) || s_themeWords.Count == 0) return;
            string theme = ActiveTheme;
            if (theme == null) return;
            string w = word.ToUpperInvariant();
            char[] rv = w.ToCharArray(); System.Array.Reverse(rv);
            if (!s_themeWords.Contains(w) && !s_themeWords.Contains(new string(rv))) return;
            ThemeMade++;
            if (RulesEngine.Simulating) return;   // bot playtester: count it, no flights / popups
            int target = 0; foreach (var g in Goals) if (g.Type == GoalType.Theme) target = g.Target;
            WDLog.Log($"[Theme] '{w}' is a {theme} word — {ThemeMade}/{target}");
            var gm = GridManager.Instance;
            if (THEME_FLY_TO_GOAL && cells != null && cells.Count > 0)
            {   // the letters FLY to the goal picture instead of exploding (HandManager.LaunchThemeFlights picks these up)
                ThemeAwaitingFly++;
                PendingThemeSince = Time.unscaledTime;
                PendingThemeWords.Add(new System.Collections.Generic.List<Vector2Int>(cells));
                return;
            }
            if (BonusPopup.Instance != null && gm != null && cells != null && cells.Count > 0)
            {
                Vector3 c = Vector3.zero; foreach (var v in cells) c += gm.CellToWorld(v.x, v.y); c /= cells.Count;
                BonusPopup.Instance.Show($"{ThemeSingular(theme)}!", new Color(1f, 0.85f, 0.3f), c + Vector3.up * 0.6f, 1.2f);
            }
        }
        public static readonly System.Collections.Generic.List<LevelGoal> Goals = new System.Collections.Generic.List<LevelGoal>();
        public static int GoalsStage = -1;
        public static int Collected;   // target letters destroyed this level (RulesEngine.DoExplode counts them)
        /// <summary>Counted by the rules but not yet launched toward the HUD icon (the tile hides a beat after the rules
        /// count it). The panel shows Collected minus these and the in-flight ones, so it only ever ticks UP, on landing.</summary>
        public static int CollectAwaitingFly; public static float CollectAwaitingSince;        public static char ActiveCollectLetter
        { get { foreach (var g in Goals) if (g.Type == GoalType.Collect) return g.Letter; return '\0'; } }
        public static bool GoalsActiveFor(int stage) => Goals.Count > 0 && GoalsStage == stage && SurvivalManager.FullBoardActive;
        public static int GoalProgress(LevelGoal g)
        {
            if (g.Type == GoalType.Crates) return RulesEngine.Instance != null ? Mathf.Clamp(g.Target - RulesEngine.Instance.CountCrateCells(), 0, g.Target) : 0;
            if (g.Type == GoalType.Ice)    return RulesEngine.Instance != null ? Mathf.Clamp(g.Target - RulesEngine.Instance.CountFrozenCells(), 0, g.Target) : 0;
            if (g.Type == GoalType.Theme)  return Mathf.Min(ThemeMade, g.Target);
            return Mathf.Min(Collected, g.Target);   // Collect + Gems share the Collected counter (one per level)
        }
        public static bool AllGoalsDone() { foreach (var g in Goals) if (GoalProgress(g) < g.Target) return false; return Goals.Count > 0; }
        public static string GoalsHudTitle()
            => HUDManager.GOAL_ICONS_HUD ? (Goals.Count > 1 ? "GOALS" : Goals.Count == 1 ? (Goals[0].Type == GoalType.Theme ? ThemeSingular(Goals[0].Theme) + " WORDS" : "GOAL") : "SCORE")   // 2026-10-22: the pictures say WHAT
             : Goals.Count > 1 ? "GOALS" : Goals.Count == 1 ? (Goals[0].Type == GoalType.Ducks ? "DUCKS" : Goals[0].Type == GoalType.Theme ? Goals[0].Theme.ToUpperInvariant() : Goals[0].Type == GoalType.Crates ? "CRATES" : Goals[0].Type == GoalType.Ice ? "ICE" : Goals[0].Type == GoalType.Gems ? "GEMS" : "COLLECT") : "SCORE";
        public static string GoalsHudText()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var g in Goals)
            {
                if (sb.Length > 0) sb.Append("   ");
                // A lone collect goal shows just "3/15" — the violet letter icon beside it says which letter.
                string label = g.Type == GoalType.Collect ? (Goals.Count > 1 ? $"{g.Letter} " : "")
                             : Goals.Count > 1 ? (g.Type == GoalType.Ducks ? "Ducks " : g.Type == GoalType.Theme ? char.ToUpperInvariant(g.Theme[0]) + g.Theme.Substring(1) + " " : g.Type == GoalType.Ice ? "Ice " : g.Type == GoalType.Gems ? "Gems " : "Crates ") : "";
                bool flies = g.Type == GoalType.Collect || g.Type == GoalType.Gems || g.Type == GoalType.Ducks;
                int shown = flies ? Mathf.Max(0, GoalProgress(g) - HUDManager.CollectInFlight - CollectAwaitingFly) : GoalProgress(g);
                sb.Append($"{label}{shown}/{g.Target}");
            }
            return sb.ToString();
        }
        /// <summary>Crates placed on the current level (0 = score goal). Set by Apply; full-board mode only.</summary>
        public static int CrateGoal;
        public static float FULL_CRATE_MULT = 2f;   // 2026-10-21: 10 crates fell in 3 moves — double them (first guess, retune)
        /// <summary>The internal stage CrateGoal belongs to. The clear check runs again on the NEXT stage before that stage
        /// installs — without this it saw "crate goal, 0 crates left" and cleared stage after stage (2026-10-21: L2 -> L34).</summary>
        public static int CrateGoalStage = -1;
        public static bool CrateGoalFor(int stage) => CrateGoal > 0 && CrateGoalStage == stage && SurvivalManager.FullBoardActive;
        private static LevelRules Make(string name, int ice = 0, int minLen = 3, int cadence = 2,
                                       bool branch = false, bool fuse = false, bool gold = false,
                                       int fillRows = 5, float density = 0.92f, int crates = 0, char collect = '\0', int collectN = 0, int gems = 0, string theme = null, int themeN = 0, int ducks = 0)
            => new LevelRules {
                Name = name, IceCount = ice, MinWordLength = minLen, RiseCadence = cadence,
                BranchPriming = branch, FuseOn = fuse, GoldTiles = gold,
                FillRows = fillRows, Density = density, Crates = crates, CollectLetter = collect, CollectCount = collectN, Gems = gems, Theme = theme, ThemeCount = themeN, Ducks = ducks };

        /// <summary>The authored opening. One named twist per stage — that is what makes a level
        /// feel designed instead of a number going up. Keep each stage to ONE new idea.</summary>
        private static readonly LevelRules[] TABLE = {
            /* 1  */ Make("Welcome",      cadence:3, fillRows:4, density:0.90f, crates:6),   // 2026-10-21 Spencer: "start off with the crate level" (x2 on the full board = 12)
            /* 2  */ Make("Warm Up",      fillRows:4, density:0.92f, gems:10),   // 2026-10-22: first GEM level (was collect 15 E)
            /* 3  */ Make("Animal Kingdom", themeN:3, theme:"animals"),   // 2026-10-22: first THEME level (inserted; later rows shift down one)
            /* 4  */ Make("Duck Pond",    ducks:4),   // 2026-10-22: first DUCK level (inserted; later rows shift down one)
            /* 5  */ Make("Ice Breaker",  ice:8),
            /* 4  */ Make("4+ Letters Only",  minLen:4, gems:12),
            /* 5  */ Make("Treasure",     gold:true, cadence:3, fillRows:6, density:0.95f, crates:12),
            /* 6  */ Make("Quickening",   cadence:2, crates:14),
            /* 7  */ Make("Branching — Connect to the Web",    branch:true, cadence:3),
            /* 8  */ Make("Deep Freeze — 4+ Letters",  ice:12, minLen:4),
            /* 9  */ Make("Thin Ice",     ice:6, cadence:2, crates:8),
            /* 10 */ Make("Tangle — Connect to the Web", branch:true, cadence:1, fillRows:7, density:0.96f), // BOSS
            /* 11 */ Make("Payday",       gold:true, ice:4, cadence:3, crates:12),     // relief
            /* 12 */ Make("Burning Fuse — Words Expire", fuse:true),
            /* 13 */ Make("Hard Water",   ice:10, fuse:true),
            /* 14 */ Make("Scarcity — 4+ Letters",     minLen:4, cadence:2, crates:16),
            /* 15 */ Make("Crucible — 4+ Letters",     minLen:4, fuse:true, cadence:1),  // BOSS
        };

        /// <summary>BLAST-LIMIT PROTOTYPE switch (2026-10-20). True = each level gives a fixed number
        /// of blasts and rows do NOT rise from moves; false = the rising-row game exactly as before.
        /// See SurvivalManager.BlastMode for the rules.</summary>
        /// <summary>MOVE LIMIT switch (2026-10-20). Each level: score the target within MovesFor(stage) moves.
        /// See SurvivalManager.MoveLimitMode. false = unlimited moves, rising-row top-out as before.</summary>
        public static bool MoveLimitMode = true;
        /// <summary>Moves per level. 2026-10-20 pass 2: 18 was too tight — Spencer's first LEVEL 1 under the limit
        /// failed at 1,097/1,200 (91%) with 18+1 moves at ~58 pts/move; the first level must be a win. 24 lets
        /// that pace clear L1 with ~4-5 spare; +1 every 3 levels while targets grow x1.15, so the points-per-move
        /// demand climbs ~12%/level (L1 50, L3 67, L5 84, L8 123). TUNE FROM tools/balance_report.py.</summary>
        public static int MovesFor(int stage) => 24 + (Mathf.Max(1, stage) - 1) / 3;

        /// <summary>Royal-Match-style base coins for clearing a level (2026-10-21): 10 normal, 20 hard, 30 boss.
        /// Boss = every 5th level from 10 (the table's BOSS rows and the procedural slot-4 boss). Hard = a 4+ letter
        /// minimum, heavy ice (8+), or a rise every move.</summary>
        public static int BaseCoinsFor(int stage)
        {
            stage = Mathf.Max(1, stage);
            if (stage >= 10 && stage % 5 == 0) return 30;
            var r = For(stage);
            if (r.MinWordLength >= 4 || r.IceCount >= 8 || r.RiseCadence <= 1) return 20;
            return 10;
        }

        // ── 4+ LETTER LEVELS (2026-10-20 tuning, Spencer "sure") ──────────────────────────────────────────
        // Measured: LEVEL 4 ("4+ Letters Only") 0/4 clears, every loss ~91%, 2-3 board-full penalties per try —
        // 3-letter words don't pop there, so the board fills and the -3 penalties ate 6-9 moves. The rule should
        // be the challenge, not the penalties stacked on it: such levels rise one move slower and get extra moves.
        public const int MIN4_EXTRA_MOVES = 4;
        public const int MIN4_EXTRA_CADENCE = 1;
        public static int MovesForRules(int stage, LevelRules r) => MovesFor(stage) + (r.MinWordLength >= 4 ? MIN4_EXTRA_MOVES : 0);
        public static int CadenceForRules(LevelRules r) => Mathf.Max(1, r.RiseCadence) + (r.MinWordLength >= 4 ? MIN4_EXTRA_CADENCE : 0);

        /// <summary>Cap on fresh-board fill density. 2026-10-20: at 0.90-0.96 the opening board was packed with
        /// accidental words, so the first moves cascaded LEVEL 1 to a win in 2-4 moves (CASE->ROB->ETA, 288).
        /// 0.80 keeps cascades common but less automatic.</summary>
        public static float FRESH_BOARD_DENSITY_CAP = 0.80f;

        public static bool BlastLimitMode = false;   // 2026-10-20 Spencer tried it: "meh ... at least with the row rising there was some pressure and it introduces new letters"
        /// <summary>Blasts per level. One number for every level while the idea is being felt —
        /// per-level tuning comes after we know whether it is fun.</summary>
        public static int  BlastsPerLevel = 5;

        public static LevelRules For(int stage)
        {
            if (stage < 1) stage = 1;
            if (stage <= TABLE.Length) return TABLE[stage - 1];

            // Past the table: repeat the 5-beat shape with counts climbing. slot 4 = boss,
            // slot 0 = the valley right after it. Peaks AND valleys both trend up.
            int over  = stage - TABLE.Length - 1;
            int block = over / 5;
            int slot  = over % 5;
            var r = Make($"Stage {stage}");
            switch (slot)
            {
                case 0: r = Make("Breather",  ice: 2 + block,      cadence: 3, gold: true);                  break;
                case 1: r = Make("Climb",     ice: 4 + block,      cadence: 2, crates: 10 + block * 2);                              break;
                case 2: r = Make("Pressure — 4+ Letters",  ice: 6 + block,      cadence: 2, minLen: 4);                   break;
                case 3: r = Make("Squeeze — Words Expire",   ice: 8 + block,      cadence: 2, fuse: true, crates: 12 + block * 2);                  break;
                default:r = Make("Boss — 4+ Letters, Connect",      ice: 10 + block * 2, cadence: 1, minLen: 4, branch: true);     break;
            }
            return r;
        }

        /// <summary>Last stage whose ICE was seeded, so a repeated InstallLevel in the same
        /// stage cannot stack ice on top of ice. The rule switches are idempotent and can
        /// safely re-apply; seeding is not.</summary>
        private static int s_icedStage  = -1;
        /// <summary>Last stage this ran for (-1 = none yet this run). Any change of stage — INCLUDING the
        /// first install of a run since 2026-10-20 — gets a fresh LevelRules board; a repeated install of
        /// the same stage does not. (The first level used to keep OpeningSeed's sparse board; see Apply.)</summary>
        private static int s_lastStage  = -1;
        /// <summary>Next Apply rebuilds the board even on the same stage (full-board / rising-row toggle).</summary>
        public static void ForceFreshBoardOnNextApply() => s_lastStage = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { s_icedStage = -1; s_lastStage = -1; }

        /// <summary>Forget the previous run. Called by SurvivalManager.StartSurvival. These statics used to
        /// reset only on domain reload, so Play Again carried the last run's stage: a stage-11 restart
        /// after dying at 13 counted as a TRANSITION (double board), and a stage already iced last run had
        /// its ice silently skipped. Found by the 2026-10-20 audit.</summary>
        public static void ResetForNewRun() => ResetStatics();

        /// <summary>Push this stage's rules into the engine. Called at the end of
        /// ObjectiveManager.InstallLevel, once per stage.</summary>
        /// <summary>True when THIS table is the one that seeds the board for a given stage, so
        /// the older ObjectiveManager per-level reseed must stand down.
        ///
        /// 2026-10-20 — measured in a live log, one frame after CONTINUE was pressed on LEVEL 1:
        ///     [Vault] SeedVaultBoard: startFillRows=5 density=0.65     <- ObjectiveManager
        ///     [LevelTable] Install L12 ... Generated level 2 'Warm Up'
        ///     [Vault] SeedVaultBoard: startFillRows=4 density=0.92     <- LevelRules
        /// Two full board wipes and rebuilds back to back. LevelRules runs last so its board is
        /// the one that survives, but the player still watches the first one get built and
        /// animated in before it is thrown away — which is exactly the report "there was already
        /// a board that was there before i entered the level and then another board animated over
        /// top of it". One owner, decided in one place.</summary>
        public static bool OwnsBoardSeed(int internalStage) =>
            SurvivalManager.ScoreRunMode && !LevelMapPanel.IsTutorialLevel(internalStage);

        /// <summary>The whole player-facing briefing for a level: the points target, then any
        /// rule that makes THIS level different, then — for the first few levels — how the game
        /// is actually played.
        ///
        /// 2026-10-20 Spencer: "take out the goals and just say how many points you need to pass
        /// the level and any instructions as to what the certain conditions are". The modal used
        /// to show a tile-cluster icon and the level's internal NAME ("Warm Up"), which tells a
        /// new player nothing — it named the level instead of explaining it.
        ///
        /// Built HERE rather than in the modal because this file already owns every rule being
        /// described. A rule added to the table above and not described below is a rule the
        /// player has to discover by losing, so the two belong in one file. (That failure
        /// already happened once: LEVEL 4's hidden 4-letter minimum read as "swaps do nothing".)
        ///
        /// The core-loop lines stop after LEVEL 3 — by then it is clutter above the PLAY button,
        /// and the only thing worth saying is what is different about today.</summary>
        // ── FULL-BOARD BRIEFING (2026-10-22 Spencer: "for each level can you fix the instructions on the panel before the level
        // to explain the objectives"). The rising-row briefing led with "Score N points" on every level, even crate / gem / duck
        // levels that are won by the GOAL, and taught rows rising and lit words — neither exists on the full board. This one
        // leads with GOAL (house rule: every level modal has a GOAL heading), one line per goal with the real count, then
        // one line on HOW, then the basics on the first three levels only. ──
        public struct GoalIconInfo { public GoalType Type; public int Count; public char Letter; public string Theme; }
        /// <summary>The level's goals with their counts, for the intro card's picture row (2026-10-22 Spencer: "if it
        /// involves some sort of an icon or tile it would be helpful to have a visual representation"). Same counts as
        /// the briefing text — both read GoalCountFor.</summary>
        public static System.Collections.Generic.List<GoalIconInfo> GoalIconsFor(int internalStage)
        {
            var list = new System.Collections.Generic.List<GoalIconInfo>();
            if (!SurvivalManager.FullBoardActive) return list;
            var r = For(Mathf.Max(1, LevelMapPanel.RunLevel(internalStage)));
            void Add(GoalType t, int planned, char l = '\0') { if (planned > 0) list.Add(new GoalIconInfo { Type = t, Count = GoalCountFor(internalStage, t, planned), Letter = l }); }
            Add(GoalType.Crates, r.Crates > 0 ? Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT) : 0);
            Add(GoalType.Ice, r.IceCount);
            Add(GoalType.Gems, r.Gems);
            Add(GoalType.Ducks, r.Ducks);
            Add(GoalType.Theme, !string.IsNullOrEmpty(r.Theme) ? r.ThemeCount : 0);
            for (int i = 0; i < list.Count; i++) if (list[i].Type == GoalType.Theme) { var t = list[i]; t.Theme = r.Theme; list[i] = t; }
            Add(GoalType.Collect, r.CollectLetter != '\0' ? r.CollectCount : 0, char.ToUpperInvariant(r.CollectLetter));
            return list;
        }
        /// <summary>Real count once the level is installed (ice can place fewer than asked); the planned count before.</summary>
        private static int GoalCountFor(int internalStage, GoalType t, int planned)
        {
            if (GoalsStage == internalStage) foreach (var g in Goals) if (g.Type == t) return g.Target;
            return planned;
        }

        private static string FullBoardBriefing(int internalStage, int stage, LevelRules r, int target)
        {
            var sb = new System.Text.StringBuilder();
            int moves = MovesForRules(stage, r);
            // Real counts once the level is installed (ice can place fewer than asked); planned counts before that.
            int GoalCount(GoalType t, int planned) => GoalCountFor(internalStage, t, planned);
            int crates = r.Crates > 0 ? GoalCount(GoalType.Crates, Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT)) : 0;
            int ice    = r.IceCount > 0 ? GoalCount(GoalType.Ice, r.IceCount) : 0;
            int gems   = r.Gems > 0 ? GoalCount(GoalType.Gems, r.Gems) : 0;
            int ducks  = r.Ducks > 0 ? GoalCount(GoalType.Ducks, r.Ducks) : 0;
            int themeN = !string.IsNullOrEmpty(r.Theme) && r.ThemeCount > 0 ? GoalCount(GoalType.Theme, r.ThemeCount) : 0;
            int collectN = r.CollectLetter != '\0' && r.CollectCount > 0 ? GoalCount(GoalType.Collect, r.CollectCount) : 0;
            char L = char.ToUpperInvariant(r.CollectLetter);
            string themeWord = themeN > 0 ? ThemeSingular(r.Theme).ToLowerInvariant() : "";

            var goals = new System.Collections.Generic.List<string>();
            var how   = new System.Collections.Generic.List<string>();
            if (crates > 0)  { goals.Add($"Break <b>{crates} crates</b>");
                               how.Add("<b>Crates</b> break when you make a word right next to them. Rockets and bombs smash them too."); }
            if (ice > 0)     { goals.Add($"Melt <b>{ice} ice</b>");
                               how.Add("<b>Frozen letters</b> can't be moved. Make a word that runs <b>through</b> one to melt it. Rockets and bombs melt ice too."); }
            if (gems > 0)    { goals.Add($"Collect <b>{gems} gems</b>");
                               how.Add(HandManager.GEMS_LOCKED
                                   ? "<b>Gems</b> can't be moved — make a word that uses a gem's letter to collect it. Rockets and bombs grab them too."
                                   : "<b>Gems</b> are collected when their letter is used in a word. Rockets and bombs grab them too."); }
            if (ducks > 0)   { goals.Add($"Bring <b>{ducks} ducks</b> down");
                               how.Add("<b>Ducks</b> drop when the letters under them clear. A duck is collected when it reaches the <b>bottom row</b>."); }
            if (themeN > 0)  { goals.Add($"Make <b>{themeN} {themeWord} words</b>");
                               how.Add($"Any word that names a <b>{themeWord}</b> counts, even short ones like {(r.Theme == "animals" ? "CAT" : r.Theme == "food" ? "PIE" : "SUN")}. Some are hiding <b>one swap</b> from complete."); }
            if (collectN > 0){ goals.Add($"Collect <b>{collectN} {L}'s</b>");
                               how.Add($"Every <b>{L}</b> you pop counts. They're the blue tiles."); }

            // (The card shows the GOAL heading and a picture of each goal above this text.)
            if (goals.Count == 0) sb.Append($"Score <b>{target:N0} points</b> in <b>{moves} moves</b>.");
            else
            {
                sb.Append(string.Join("  and  ", goals));
                sb.Append($" in <b>{moves} moves</b>.");
            }
            foreach (var h in how) sb.Append("\n\n").Append(h);

            if (r.GoldTiles)
                sb.Append("\n\n<b>Gold tiles score double.</b> Use them in a word.");

            if (stage <= 3)
            {
                sb.Append(HandManager.AdjacentSwapsOnly
                    ? $"\n\n<b>Swap</b> two touching letters or <b>swipe</b> a word. Words need <b>{RulesEngine.EffectiveMinWordLength}+ letters</b>."
                    : $"\n\n<b>Swap</b> any two letters or <b>swipe</b> a word. Words need <b>{RulesEngine.EffectiveMinWordLength}+ letters</b>.");
                sb.Append("\n<b>Power-ups:</b> 5 letters = rocket, 6+ = disco ball, crossing words = bomb.");
            }
            else if (r.MinWordLength > RulesEngine.FULL_MIN_LEN)
                sb.Append($"\n\nOnly words of <b>{r.MinWordLength}+ letters</b> count on this level.");
            return sb.ToString();
        }

        public static string BriefingFor(int internalStage, int target)
        {
            int stage = Mathf.Max(1, LevelMapPanel.RunLevel(internalStage));
            var r = For(stage);
            if (SurvivalManager.FullBoardActive && MoveLimitMode) return FullBoardBriefing(internalStage, stage, r, target);
            var sb = new System.Text.StringBuilder();

            sb.Append(BlastLimitMode
                ? $"Score <b>{target:N0} points</b> in <b>{BlastsPerLevel} blasts</b>."
                : MoveLimitMode
                    ? $"Score <b>{target:N0} points</b> in <b>{MovesForRules(stage, r)} moves</b>."
                    : $"Score <b>{target:N0} points</b>");

            // ── what is different about THIS level ───────────────────────────────
            // Each line names the rule AND what to do about it. "4+ letters only" alone
            // leaves the player staring at a board; saying it refuses shorter words tells
            // them why nothing is happening.
            if (r.MinWordLength > 3)
                sb.Append($"\n\nOnly words of <b>{r.MinWordLength} letters or more</b> count here — " +
                          "shorter ones will not light up.");
            if (r.Gems > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Collect {r.Gems} gems!</b> Use a gem's letter in a word to collect it — rockets and bombs grab them too.");
            if (r.Ducks > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Bring {r.Ducks} ducks down!</b> Clear the letters under a duck and it drops. It's collected when it reaches the bottom row.");
            if (!string.IsNullOrEmpty(r.Theme) && r.ThemeCount > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Make {r.ThemeCount} {ThemeSingular(r.Theme).ToLowerInvariant()} words!</b> Any word that is a {ThemeSingular(r.Theme).ToLowerInvariant()} counts — look for ones that are one swap away.");
            if (r.CollectLetter != '\0' && r.CollectCount > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Collect {r.CollectCount} {char.ToUpperInvariant(r.CollectLetter)}'s!</b> Every {char.ToUpperInvariant(r.CollectLetter)} you blow up counts — they're the blue tiles.");
            if (r.Crates > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Break every crate!</b> Make words next to them — rockets and bombs smash them too.");
            if (r.IceCount > 0 && SurvivalManager.FullBoardActive)
                sb.Append($"\n\n<b>Melt all the ice!</b> Frozen letters can't move — make a word THROUGH one to melt it. Rockets and bombs smash ice too.");
            else if (r.IceCount > 0)
                sb.Append($"\n\n<b>{r.IceCount} frozen tiles.</b> You cannot move them. Make a word " +
                          $"THROUGH one and blow it up to melt it — worth <b>{RulesEngine.ICE_BOUNTY}</b> each.");
            if (r.BranchPriming)
                sb.Append("\n\nYour first word can be anywhere. After that, <b>every new word must " +
                          "touch the ones you have already lit.</b>");
            if (r.FuseOn)
                sb.Append("\n\n<b>Lit words burn out</b> if you leave them too long. Set them off " +
                          "before they go dark.");
            if (r.GoldTiles)
                sb.Append("\n\n<b>Gold tiles are worth double.</b> Work them into a word before you blow it up.");
            // Cadence 2 is the DEFAULT, so stating it is noise on a level where nothing is
            // unusual — it reads as a special condition when it is just Tuesday. Only the
            // relentless setting earns a warning line; the fact that rows rise AT ALL is
            // taught once, below, to players who have never seen it.
            if (CadenceForRules(r) <= 1 && !BlastLimitMode)   // no move-rises in blast mode
                sb.Append("\n\nA new row pushes up <b>every single move</b>. Keep the board low.");

            // ── how the game works, for someone who has never played ─────────────
            // Order matters: how to act, what that does, and THEN how points happen. The
            // third line is the one most new players get wrong, and since 2026-10-20 it is
            // literally the only way to score — making a word pays nothing on its own.
            if (stage <= 3)
            {
                sb.Append(HandManager.AdjacentSwapsOnly
                    ? "\n\n<b>Swap</b> two touching tiles, or <b>swipe</b> across a word you spot."
                    : "\n\n<b>Swap</b> any two tiles, or <b>swipe</b> across a word you spot.");
                if (HandManager.InstantPopMode)
                    sb.Append("\n\nEvery word you make <b>pops</b> and scores. Letters fall — " +
                              "<b>new words they make pop too.</b>" +
                              "\n\n<b>Longer words score far more.</b> Watch the new rows for a word " +
                              "that is <b>one swap</b> from complete.");
                else
                {
                    sb.Append("\n\nThe word <b>stays lit on the board</b> instead of vanishing.");
                    sb.Append("\n\n<b>Tap a lit word to blow it up — that is the only way to score.</b> " +
                              "Lit words that touch go off together, and each one in the chain is worth more " +
                              "than the last.");
                }
                if (MoveLimitMode)
                    sb.Append("\n\nNew rows push up every few moves. <b>If the board is full, the top row " +
                              $"breaks off and costs you {SurvivalManager.OVERFLOW_MOVE_PENALTY} moves.</b>");
                else if (BlastLimitMode)
                    sb.Append("\n\nEach blast uses one of your blasts — <b>a bigger web is worth far more</b>, " +
                              "so build before you fire.");
                else
                    sb.Append("\n\nNew rows push up from the bottom as you play. Reach the top and the run ends.");
            }

            return sb.ToString();
        }

        public static void Apply(int internalStage)
        {
            // ⚠️ INTERNAL STAGE != THE LEVEL THE PLAYER SEES. 2026-10-19 — Spencer started a run,
            // saw "LEVEL 1", and it had ice in it. Cause: with the tutorial complete a run begins
            // at INTERNAL stage 11 (RunStartStage = TUTORIAL_LEVELS + 1), and LevelMapPanel maps
            // internal 11 -> displayed "LEVEL 1". This table was keyed on the internal index, so
            // the player's first level was pulling table row 11 ("Payday", 4 ice).
            // LevelMapPanel's own comment says it: "everything player-facing goes through these
            // transforms." This is player-facing, so it goes through them too.
            if (LevelMapPanel.IsTutorialLevel(internalStage))
            {
                // The tutorial levels are hand-authored with rigged boards and gated input.
                // They must not get generated rules — but DO reset the switches, or a value
                // left over from a previous run (4-letter minimum, branch priming) leaks in.
                RulesEngine.MIN_WORD_LENGTH  = 3;
                RulesEngine.BranchPriming    = false;
                RulesEngine.GoldTilesEnabled = false;
                return;
            }

            int stage = Mathf.Max(1, LevelMapPanel.RunLevel(internalStage));
            var r = For(stage);

            // The next level is now genuinely starting — release the lock that has been
            // holding input and pinning the HUD since the previous level's target was met.
            SurvivalManager.ClearAwaitingNextLevel();
            HUDManager.Instance?.ExitRewardMode();                // REWARD panel back to SCORE for the new level
            HUDManager.Instance?.SnapScoreReadoutForNewLevel();   // 0 / new target before the HUD is seen (2026-10-21)
            HUDManager.MovesDisplayOverride = -1;                 // bonus-blast moves display ends with the old level
            SurvivalManager.Instance?.ClearRiseQueueForNewLevel();
            if (MoveLimitMode && SurvivalManager.Instance != null)
            {
                SurvivalManager.Instance.SetStageMoveBudgetOverride(MovesForRules(stage, r));
                SurvivalManager.Instance.SetMoveLimitTopOut(true);
                SurvivalManager.Instance.ResetStageMoves();
            }
            if (BlastLimitMode)
            {
                SurvivalManager.Instance?.ResetBlasts(BlastsPerLevel);
                SurvivalManager.SetBlastRefillOccupancy(r.FillRows * r.Density / RulesEngine.ROWS);
            }
            RulesEngine.MIN_WORD_LENGTH = Mathf.Max(3, r.MinWordLength);
            RulesEngine.BranchPriming   = r.BranchPriming;
            RulesEngine.FuseDisabled    = !r.FuseOn;      // field is inverted: Disabled == no decay
            RulesEngine.GoldTilesEnabled = r.GoldTiles;
            SurvivalManager.SetRiseCadenceOverride(CadenceForRules(r));

            // ── FRESH BOARD ON EVERY NEW LEVEL ───────────────────────────────────────
            // Spencer: "a brand new starting board with a different configuration of starting
            // letters". AdvanceToNextStage only queued replenish RISES, so the next level
            // inherited the previous one's picked-over board. A reseed per level also makes
            // each level a distinct puzzle rather than a continuation.
            //
            // Same sequence ObjectiveManager already uses for its per-level reseed — including
            // ResetTurnCounter, which is THE documented trap: ClearBoard zeroes GlobalTurn and
            // MatchController's cached _currentTurn must be reset to match or the turn counter
            // desyncs. RebuildFromRulesEngine (not SyncToRulesState) because a full rebuild also
            // wipes stale per-tile visual state like leftover primed glow.
            var rules2 = RulesEngine.Instance;
            // 2026-10-20: LEVEL 1 gets a fresh board too. It used to keep OpeningSeed's board — built for the
            // old 6-column grid, 17 tiles = 17% full, just under the Starved line (18%) — so the empty-board
            // check queued two refill rows the moment Play was pressed and they rose as soon as the board slid
            // in. Spencer: "upon entry to level one why it immediately did a row rise". Built while the home
            // screen still covers the board, same as every transition reseed.
            bool isTransition = s_lastStage != internalStage;
            if (isTransition && rules2 != null)
            {
                CrateGoal = 0;
                Goals.Clear(); Collected = 0; CollectAwaitingFly = 0; GoalsStage = internalStage;
                ThemeMade = 0; ThemeAwaitingFly = 0; PendingThemeWords.Clear(); s_themeWords.Clear(); RulesEngine.ThemePlantPool = null;
                if (SurvivalManager.FullBoardActive && !string.IsNullOrEmpty(r.Theme) && r.ThemeCount > 0)
                {
                    var tl = LoadTheme(r.Theme);
                    if (tl.Count > 0)
                    {
                        s_themeWords.UnionWith(tl);
                        RulesEngine.ThemePlantPool = tl;
                        Goals.Add(new LevelGoal { Type = GoalType.Theme, Theme = r.Theme, Target = r.ThemeCount });
                        WDLog.Log($"[Theme] LEVEL {stage}: make {r.ThemeCount} '{r.Theme}' words ({tl.Count} in the list)");
                    }
                }
                if (SurvivalManager.FullBoardActive && r.Gems > 0)
                    Goals.Add(new LevelGoal { Type = GoalType.Gems, Target = r.Gems });
                if (SurvivalManager.FullBoardActive && r.Ducks > 0)
                    Goals.Add(new LevelGoal { Type = GoalType.Ducks, Target = r.Ducks });   // shares Collected with gems/collect — don't combine them on one level
                if (SurvivalManager.FullBoardActive && r.CollectLetter != '\0' && r.CollectCount > 0)
                    Goals.Add(new LevelGoal { Type = GoalType.Collect, Letter = char.ToUpperInvariant(r.CollectLetter), Target = r.CollectCount });
                Tile.CollectLetter = ActiveCollectLetter;   // tint the target letter BEFORE the board is built
                if (SurvivalManager.FullBoardActive)
                {
                    // FULL BOARD (2026-10-21): every cell filled, no word spelled by accident; then the usual plants,
                    // then scrub anything the plants spelled with their neighbours.
                    rules2.FillBoardNoWords();
                    rules2.SnapshotForRain(out var before, out var _);
                    rules2.PlantNearWordsOnBoard(RulesEngine.FULL_START_PLANTS, spreadRows: true);
                    rules2.SnapshotForRain(out var after, out var _);
                    var keep = new System.Collections.Generic.HashSet<Vector2Int>();
                    for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++) if (before[x, y] != after[x, y]) keep.Add(new Vector2Int(x, y));
                    int shortWords = rules2.SeedShortWords(RulesEngine.FULL_START_SHORT_WORDS, keep);
                    int crates = r.Crates > 0 ? rules2.PlaceCrates(Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT), keep) : 0;
                    int tnt = rules2.PlaceStartTnt(RulesEngine.FULL_START_TNT, keep);
                    int gemsPlaced = r.Gems > 0 ? rules2.PlaceGems(Mathf.Min(r.Gems, RulesEngine.GEM_MIN_ON_BOARD + 2), keep) : 0;
                    int ducksPlaced = r.Ducks > 0 ? rules2.PlaceDucks(Mathf.Min(r.Ducks, RulesEngine.DUCKS_ON_BOARD), keep) : 0;
                    CrateGoal = crates; CrateGoalStage = internalStage;
                    if (crates > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Crates, Target = crates });
                    int scrubbed = rules2.ScrubAccidentalWords(keep);
                    WDLog.Log($"[FullBoard] start structure: {shortWords} short word(s), {crates} crate(s) (goal), {tnt} TNT");
                    HUDManager.Instance?.SnapScoreReadoutForNewLevel();   // redraw now the crate goal exists (it read 0/6000 — snapped before the crates)
                    MatchController.Instance?.ResetTurnCounter();
                    GridManager.Instance?.RebuildFromRulesEngine(rules2);
                    var gm = GridManager.Instance;   // the rebuild doesn't paint specials — put the TNT on now, not a sync later
                    if (gm != null) for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++)
                    { var d = rules2.GetCell(x, y); if (d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE) gm.GetTile(x, y)?.SetSpecial(d.SpecialKind);
                      if (d != null && d.HasGem) gm.GetTile(x, y)?.SetGem(true);
                      if (d != null && d.IsDropTarget) gm.GetTile(x, y)?.SetDropTargetVisual(true); }
                    WDLog.Log($"[FullBoard] fresh FULL board for LEVEL {stage} — {keep.Count} planted cell(s), {scrubbed} accidental word cell(s) redrawn");
                }
                else {
                rules2.SeedVaultBoard(r.FillRows, Mathf.Min(r.Density, FRESH_BOARD_DENSITY_CAP), 0, 0, 0, 0, 0, 0, 0); // 0 vaults = fill only
                // SETTLE IT. SeedVaultBoard scatters at `density`, so at 0.65 a third of the
                // cells are holes — including holes UNDER letters, which is the "massive gaps
                // with no letters" and the floating single tiles. Gravity packs them into solid
                // rows from the bottom, which is what a starting board should look like.
                // 2026-10-19 Spencer.
                rules2.ApplyGravityInDataPublic();
                // Something to hunt from move 1 — not only once a row has risen. 2026-10-20 (RNG pass).
                if (HandManager.InstantPopMode) rules2.PlantNearWordsOnBoard(RulesEngine.PLANTS_ON_FRESH_BOARD);
                MatchController.Instance?.ResetTurnCounter();
                GridManager.Instance?.RebuildFromRulesEngine(rules2);
                WDLog.Log($"[LevelRules] fresh board for LEVEL {stage} — {r.FillRows} rows @ {Mathf.Min(r.Density, FRESH_BOARD_DENSITY_CAP):P0}");
                }
            }
            s_lastStage = internalStage;

            int placed = 0;
            if (r.IceCount > 0 && RulesEngine.Instance != null && s_icedStage != internalStage)
            {
                placed = RulesEngine.Instance.SpawnFrozenTiles(r.IceCount);
                s_icedStage = internalStage;
                // SpawnFrozenTiles only picks from OCCUPIED cells, so a sparse board yields
                // fewer than asked. The log reports both so a shortfall is visible rather than
                // looking like the feature failed.
                // Reconcile, not plain sync: this is the ONE path that drives frost onto
                // already-existing tiles, and a cell frozen in DATA but not showing frost is
                // unselectable while looking perfectly normal (TryEnterRewriteMode and
                // TryBoardSwap both refuse on cell.IsFrozen). 2026-10-19.
                GridManager.Instance?.ReconcileSpecialVisuals(RulesEngine.Instance);
                // ICE GOAL (2026-10-21 Spencer: "for the ice level shouldnt it just be melt the ice to pass the level?").
                if (SurvivalManager.FullBoardActive && placed > 0 && GoalsStage == internalStage)
                {
                    Goals.Add(new LevelGoal { Type = GoalType.Ice, Target = placed });
                    HUDManager.Instance?.SnapScoreReadoutForNewLevel();
                }
                int dataFrozen = RulesEngine.Instance.CountFrozenCells();
                if (dataFrozen != placed)
                    Debug.LogWarning($"[LevelRules] ICE MISMATCH — spawned {placed} but board reports " +
                                     $"{dataFrozen} frozen cells. Stale ice from a previous level?");
            }

            var sm = SurvivalManager.Instance;
            if (r.MinWordLength > 3)
                WDLog.Log($"[LevelRules] ⚠️ LEVEL {stage} refuses words under {r.MinWordLength} letters — " +
                          $"if swaps 'do nothing' here, that is why. Name says so: '{r.Name}'");
            WDLog.Log($"[LevelRules] LEVEL {stage} (internal {internalStage}) '{r.Name}' — " +
                      $"TARGET {(sm != null ? sm.CurrentStageTarget : -1)} " +
                      $"(score so far {(sm != null ? sm.CurrentStageScore : -1)}), " +
                      $"ice {placed}/{r.IceCount}, " +
                      $"minLen {r.MinWordLength}, cadence {CadenceForRules(r)}, moves {MovesForRules(stage, r)}, " +
                      $"branch {r.BranchPriming}, fuse {r.FuseOn}, gold {r.GoldTiles}");
        }
    }

    /// <summary>Per-level play stats for TUNING (2026-10-20 Spencer: "if i just play through over and over will we
    /// be able to dial the numbers a bit better?"). Attached to the stage_clear / stage_fail analytics events,
    /// which persist per session under Application Support/.../WordDrop/analytics. Reset when a level installs.</summary>
    public static class StageStats
    {
        public static int Len3, Len4, Len5, Len6Plus, PlantsMade, PlantsSolved, Overflows, GoldMoves, SpecialsMade, SpecialsFired, Showers, Rains, SmartSeeds;
        /// <summary>Moves the player actually MADE this level (2026-10-23). "moves_used" was budget-minus-remaining, which goes
        /// NEGATIVE when gold letters refund moves (GoldMoves) — e.g. -10 on a fast level full of gold.</summary>
        public static int MovesMade;
        private static readonly System.Collections.Generic.HashSet<string> s_planted = new System.Collections.Generic.HashSet<string>();
        public static void Reset() { Len3 = Len4 = Len5 = Len6Plus = PlantsMade = PlantsSolved = Overflows = GoldMoves = SpecialsMade = SpecialsFired = Showers = Rains = SmartSeeds = MovesMade = 0; s_planted.Clear(); }
        public static void RecordPlanted(string word) { if (string.IsNullOrEmpty(word)) return; PlantsMade++; s_planted.Add(word.ToUpperInvariant()); }
        public static void RecordOverflow() => Overflows++;
        /// <summary>Bot playtester: save/restore the live level's tallies around simulated games.</summary>
        public static int[] Save() => new[] { Len3, Len4, Len5, Len6Plus, PlantsMade, PlantsSolved, Overflows, GoldMoves, SpecialsMade, SpecialsFired, Showers, Rains, SmartSeeds, MovesMade };
        public static System.Collections.Generic.HashSet<string> SavePlanted() => new System.Collections.Generic.HashSet<string>(s_planted);
        public static void Load(int[] v, System.Collections.Generic.HashSet<string> planted)
        {
            if (v == null || v.Length < 13) return;
            Len3 = v[0]; Len4 = v[1]; Len5 = v[2]; Len6Plus = v[3]; PlantsMade = v[4]; PlantsSolved = v[5]; Overflows = v[6]; GoldMoves = v[7];
            SpecialsMade = v[8]; SpecialsFired = v[9]; Showers = v[10]; Rains = v[11]; SmartSeeds = v[12]; if (v.Length > 13) MovesMade = v[13];
            s_planted.Clear(); if (planted != null) s_planted.UnionWith(planted);
        }
        public static void RecordPopped(string word)
        {
            if (string.IsNullOrEmpty(word)) return;
            int n = word.Length;
            if (n <= 3) Len3++; else if (n == 4) Len4++; else if (n == 5) Len5++; else Len6Plus++;
            if (s_planted.Remove(word.ToUpperInvariant())) PlantsSolved++;
        }
        /// <summary>Key/value pairs to append to an analytics event.</summary>
        public static object[] Fields() => new object[] {
            "words_3", Len3, "words_4", Len4, "words_5", Len5, "words_6p", Len6Plus,
            "plants_made", PlantsMade, "plants_solved", PlantsSolved, "overflows", Overflows, "gold_moves", GoldMoves,
            "specials_made", SpecialsMade, "specials_fired", SpecialsFired, "showers", Showers, "rains", Rains, "smart_seeds", SmartSeeds };
    }
}
