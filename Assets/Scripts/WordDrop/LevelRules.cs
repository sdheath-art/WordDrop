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
        public int    Moves;          // move budget for this level; 0 = the old MovesFor formula (2026-10-23 curve rebuild)
        public bool   Bonus;          // no-fail coin level (Royal Match L20) — 2026-10-23
        public string Teach;          // the one new thing this level introduces (tutorial beat key; see TutorialBeats) — 2026-10-23
        public string CrateLayout, IceLayout;
        public int    ShelfStyle;     // 11-03 BOOKSHELF art: 0 = rounded frame, 1 = ornate bookcase (a 2nd shelf gets the other)
        public int    Safes;          // 11-03 SAFE: safes on the floor (6 hits each); > 0 = an "Open N safes" goal
        public int    ShelfBooks;     // 11-03 BOOKSHELF: books on the level's shelf/shelves (RulesEngine.PlaceShelves); > 0 = a Books goal
        public string CrateHits;     // 11-03 multi-hit crates: starting layers picked per crate from this list ("1,2,3"); null = all single-hit
        public string Shape;          // board shape (RulesEngine.SHAPES name: octagon, funnel, notches, split, sidenotch, hole, legs); null = rectangle (2026-10-28)
        public int    Grass, GrassSeeds; public string GrassLayout;   // squares of grass to MOW + their starting pattern (GrassSeeds unused since the flip) — 2026-10-23   // designed starting arrangement (RulesEngine.LayoutCells) — 2026-10-23 "bland and repetitive"
        public static bool HasGrassGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Grass) return true; return false; } }
        public static bool HasCrateGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Crates) return true; return false; } }
        public static bool HasBooksGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Books) return true; return false; } }
        /// <summary>BOOKSHELF: books knocked off (counted by the board) whose flight to the goal hasn't launched yet - the badge waits for
        /// them to land, like gems. HUDManager.FlyBook takes one off as it launches.</summary>
        public static int BooksAwaitingFly; public static float BooksAwaitingSince;
        public static bool HasGemGoal { get { foreach (var g in Goals) if (g.Type == GoalType.Gems) return true; return false; } }
        /// <summary>How many more gems the board should carry right now: up to GEM_MIN_ON_BOARD, but never more than the goal
        /// still needs (gems already on the board count toward it).</summary>
        public static int GemGoalRemainingOnBoardNeed(int onBoard)
        {
            foreach (var g in Goals)
                if (g.Type == GoalType.Gems)
                {
                    int stillNeeded = g.Target - Collected;
                    if (stillNeeded <= 0) return 0;
                    // SPARES (2026-10-23 Spencer: "this is when it gets a bit boring. when you see you need to make a lot of moves just
                    // to make the last couple words to clear the objective"). It kept exactly what was still needed (2 left = 2 on the
                    // board, wherever they fell), so the end was a hunt. Royal Match's board is FULL of the goal colour — the last
                    // pieces are a choice. Keep GEM_SPARES extra on the board.
                    // FIXED same day: it was max(4, needed + 3) — an 8-gem level wanted 11 on the board and the first refill turned a
                    // pile of letters into gems (Spencer: "a bunch of letters just turned into gems"). Intended rule: the board holds
                    // GEM_MIN_ON_BOARD gems the whole time the goal is open, so the LAST few are still a choice of 4.
                    return Mathf.Max(0, Mathf.Min(RulesEngine.GEM_MIN_ON_BOARD, stillNeeded + RulesEngine.GEM_SPARES) - onBoard);
                }
            return 0;
        }

        // ── LEVEL GOALS (2026-10-21 Spencer: "the objectives levels seem funner"). A level has one or two goals; it is won
        // when ALL are done (score then only feeds coins / the bonus). Goals belong to GoalsStage — the clear check re-runs
        // on the NEXT stage before it installs (the L2 -> L34 runaway), so a goal only counts on its own stage. ──
        public enum GoalType { Crates, Collect, Ice, Gems, Theme, Ducks, Grass, Books, Safes }   // Books = BOOKSHELF, Safes = SAFE (11-03)
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
            public string Shape; public bool[,] ShapeMask;   // 11-03: board shape (the bot left L19's mask live)
        }
        public static SimSnapshot TakeSimSnapshot() { var s = TakeSimSnapshotCore(); RulesEngine.GetShapeState(out s.Shape, out s.ShapeMask); return s; }
        private static SimSnapshot TakeSimSnapshotCore() => new SimSnapshot
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
            RulesEngine.SetShapeState(s.Shape, s.ShapeMask);
        }

        /// <summary>Build DISPLAY level <paramref name="stage"/> on the bot's engine and set its goals into the statics. Returns the
        /// move budget. Mirrors Apply()'s full-board branch.</summary>
        /// <summary>11-16: the lesson's planted word(s). L1 (swap + gems, strict gem rule) gets TWO: the gem word first (parked as the beat-2
        /// word, RulesEngine.Teach2*), then beat 1's plain word kept away from it. Other lessons: one word as before.</summary>
        /// <summary>11-17 (Spencer: "you are getting powerups right away"; RM hands the player no power-up on its first levels): the
        /// dynamite letter every full-board level starts with (RulesEngine.FULL_START_TNT) begins at this DISPLAY level. 1 = every
        /// level, as before. Levels 1-5 were bot-tuned WITH it - they need a bot pass.</summary>
        public static int START_TNT_FROM_LEVEL = 6;
        private static int StartTntFor(int stage) => stage < START_TNT_FROM_LEVEL ? 0 : RulesEngine.FULL_START_TNT;
        private static bool PlantLessonWords(LevelRules r)
        {
            var re = RulesEngine.Instance; RulesEngine.Teach2Cells = null; RulesEngine.Teach2Word = null;
            RulesEngine.TeachExtraCells = null; RulesEngine.TeachWord2 = null; RulesEngine.TeachKind = null;
            { string pk = TutorialBeats.KeyFor(r); if (TutorialBeats.POWER_BEATS && TutorialBeats.IsPowerKey(pk)) return re.PlantPowerLesson(pk); }   // 11-17 power-up lessons
            if (r.Teach == "swap" && r.Gems > 0 && !RulesEngine.GEM_ADJACENT_COLLECT && !TutorialBeats.Done("gem"))
            {
                System.Collections.Generic.HashSet<Vector2Int> avoid = null;
                if (re.PlantTeachSwap(throughGem: true))
                {
                    re.ParkTeachAsSecond();
                    avoid = new System.Collections.Generic.HashSet<Vector2Int>(RulesEngine.Teach2Cells) { RulesEngine.Teach2From, RulesEngine.Teach2To };
                    foreach (var c in RulesEngine.Teach2Cells) { avoid.Add(c + Vector2Int.up); avoid.Add(c + Vector2Int.down); avoid.Add(c + Vector2Int.left); avoid.Add(c + Vector2Int.right); }
                }
                bool one = re.PlantTeachSwap(avoid: avoid);
                return one || RulesEngine.Teach2Cells != null;
            }
            return re.PlantTeachSwap(through: r.Teach == "ice" && !RulesEngine.ICE_ADJACENT_THAW);
        }
        public static int SetupSimLevel(RulesEngine sim, int stage, int internalStage)
        {
            var r = For(stage);
            sim.ClearBoard();
            RulesEngine.MIN_WORD_LENGTH = Mathf.Max(3, r.MinWordLength);
            RulesEngine.BranchPriming   = r.BranchPriming;
            RulesEngine.GoldTilesEnabled = r.GoldTiles;
            RulesEngine.ShortWordCells = null; RulesEngine.LastSwapCells = null; RulesEngine.RefillGoldPending = 0;
            Goals.Clear(); Collected = 0; CollectAwaitingFly = 0; BooksAwaitingFly = 0; GoalsStage = internalStage; CrateGoal = 0; CrateGoalStage = internalStage; BonusLevelActive = r.Bonus;
            ThemeMade = 0; ThemeAwaitingFly = 0; PendingThemeWords.Clear(); s_themeWords.Clear(); RulesEngine.ThemePlantPool = null;
            if (!string.IsNullOrEmpty(r.Theme) && r.ThemeCount > 0)
            {
                var tl = LoadTheme(r.Theme);
                if (tl.Count > 0) { s_themeWords.UnionWith(tl); RulesEngine.ThemePlantPool = tl; Goals.Add(new LevelGoal { Type = GoalType.Theme, Theme = r.Theme, Target = r.ThemeCount }); }
            }
            if (r.Gems > 0)  Goals.Add(new LevelGoal { Type = GoalType.Gems, Target = r.Gems });
            if (r.Ducks > 0) Goals.Add(new LevelGoal { Type = GoalType.Ducks, Target = r.Ducks });
            if (r.CollectLetter != '\0' && r.CollectCount > 0) Goals.Add(new LevelGoal { Type = GoalType.Collect, Letter = char.ToUpperInvariant(r.CollectLetter), Target = r.CollectCount });
            RulesEngine.SetBoardShape(SHAPES_ON ? r.Shape : null);   // shaped board (void cells) - same mask as the live level
            sim.FillBoardNoWords();
            sim.SnapshotForRain(out var before, out var _);
            sim.PlantNearWordsOnBoard(RulesEngine.FULL_START_PLANTS, spreadRows: true);
            sim.SnapshotForRain(out var after, out var _);
            var keep = new System.Collections.Generic.HashSet<Vector2Int>();
            for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++) if (before[x, y] != after[x, y]) keep.Add(new Vector2Int(x, y));
            sim.SeedShortWords(RulesEngine.FULL_START_SHORT_WORDS, keep);
            RulesEngine.CrateLayout = r.CrateLayout; RulesEngine.IceLayout = r.IceLayout;
            RulesEngine.ShelfStyleNext = r.ShelfStyle; RulesEngine.SafeHitsNow = RulesEngine.SAFE_HITS; int safes = 0; int books = (r.ShelfBooks > 0 || r.Safes > 0) ? sim.PlaceShelves(r.ShelfBooks, keep, r.Safes, out safes) : 0;   // BOOKSHELF first: crates must not land on its cells (mirrors Apply)
            int crates = r.Crates > 0 ? sim.PlaceCrates(Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT), keep) : 0;
            if (crates > 0) sim.AssignCrateHits(r.CrateHits);   // 11-03 multi-hit crates (mirrors Apply)
            if (r.Grass > 0 && RulesEngine.GRASS_ON) { int gp = sim.PlaceGrass(r.Grass, r.GrassLayout); Goals.Add(new LevelGoal { Type = GoalType.Grass, Target = gp }); }
            else sim.ClearGrass();
            sim.PlaceStartTnt(StartTntFor(stage), keep);
            if (!r.Bonus) sim.PlaceTreasures(stage, keep);   // TREASURE (bonus loot) - mirrors Apply
            int simGems = r.Gems > 0 ? sim.PlaceGems((RulesEngine.GEMS_FIXED ? r.Gems : Mathf.Min(r.Gems, RulesEngine.GEM_MIN_ON_BOARD + 2)), keep) : 0;
            // 11-17: mirrors Apply - a fixed-gem goal can never ask for more than were placed (the 7x9 survey: 0% on every 6+ gem level)
            if (RulesEngine.GEMS_FIXED && r.Gems > 0) foreach (var gg in Goals) if (gg.Type == GoalType.Gems && gg.Target > simGems) { SimGemShortfall++; gg.Target = simGems; }
            if (r.Ducks > 0) sim.PlaceDucks(Mathf.Min(r.Ducks, RulesEngine.DUCKS_ON_BOARD), keep);
            CrateGoal = crates;
            if (crates > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Crates, Target = crates });
            if (books > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Books, Target = books });
            if (safes > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Safes, Target = safes });
            sim.ScrubAccidentalWords(keep);
            if (r.IceCount > 0)
            {
                int placed = sim.SpawnFrozenTiles(r.IceCount);
                if (placed > 0) Goals.Add(new LevelGoal { Type = GoalType.Ice, Target = placed });
            }
            if (RulesEngine.GIFT_WORD_ON && Goals.Count > 0 && !r.Bonus && !TutorialBeats.WantsSwipePlant(r)) sim.PlantGiftWord(out _);   // mirrors the live gift word
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
        // BONUS LEVEL (2026-10-23, Spencer: "bonus level now"; Royal Match L20). No goals, no fail: every popped word pays
        // BONUS_COINS_PER_LETTER per letter into the HUD's REWARD panel; the level clears when the moves run out.
        public static bool BonusLevelActive;
        public static int  BONUS_COINS_PER_LETTER = 1;
        // GOLD RUSH (2026-10-30, Spencer: the L20 bonus "is kind of boring and lame" + "there really is no reward for making and
        // firing off the powerups"). Short and loud: GOLD_RUSH_MOVES moves, the board starts LOADED with specials, a swap may
        // make 3-letter words (RulesEngine.ShortWordRejected), every tile a special clears pays coins (HandManager), and the
        // payout multiplier climbs as specials are fired (see v2 below) up to GOLD_RUSH_MAX_MULT. Leftover specials fire in the finale
        // (the end-of-level bonus blast already does that).
        public const  int  GOLD_RUSH_MOVES = 10;
        public static int  GOLD_RUSH_ROCKETS = 3, GOLD_RUSH_BOMBS = 2, GOLD_RUSH_AIRPLANES = 1, GOLD_RUSH_DISCOS = 1, GOLD_RUSH_GAP = 2;
        // 10-30 v2 (Spencer: "what makes the badge climb?" - it was just the move count, i.e. a timer): the payout climbs one
        // step for every GOLD_RUSH_SPECIALS_PER_STEP specials the player fires, so firing early is a (small, smart) choice.
        // Tuned for ~250 coins on a typical run (normal level ~50-70) - his move-based run paid ~340.
        public static int  GOLD_RUSH_SPECIALS_PER_STEP = 2, GOLD_RUSH_MAX_MULT = 3;
        // 10-30 v3 (his first specials-climb run paid ~746 - a 44-tile combo + x4 by move 4): the multiplier now applies to
        // SPECIAL blasts only (words pay a flat 1/letter), specials pay 1 coin per GOLD_RUSH_TILES_PER_COIN tiles, cap x3.
        // Replayed on that run's log: ~281. Words stay steady pocket money; the specials are the jackpot.
        public static int  GOLD_RUSH_TILES_PER_COIN = 2;   // special blasts: 1 coin per this many gold tiles, x the multiplier
        // v4 (2026-10-30, Spencer asked what Royal Match's L20 does: "coins everywhere except a + shaped channel"): the board
        // opens GOLD - every letter is a gold coin tile (v5: no + channel). Coins come ONLY
        // from gold tiles: GOLD_RUSH_COIN_TILE each, times the multiplier, whether a word or a special takes them. Refills
        // fall in gold too (v5). (Gold here = coins, never +1 move.)
        public static int  GOLD_RUSH_COIN_TILE = 1;   // all-gold board = the v3 economy (measured ~206/run, Spencer "felt fine")
        public static bool GOLD_RUSH_GOLD_BOARD = true;   // (forced off while GOLD_RUSH_SAFES - see GoldBoardOn)
        // ── v6 SAFES (2026-11-03, Spencer: the safe is "a good mechanic for the gold level instead of what we have right now"). Three
        // safes on the floor, GOLD_RUSH_SAFE_HITS hits each (two lamps per hit). Each lamp hit pays GOLD_RUSH_LAMP_COINS, each
        // safe that blows pays GOLD_RUSH_JACKPOT. Specials stay loaded - blasts are the fun way to crack safes. No gold board and
        // no multiplier badge: one thing to chase. Words still pay a flat 1 coin/letter. ~3 x (2x10 + 60) + words = ~250-280/run.
        // The level ends early once every safe is open (the finale blast then pays the leftover moves). false = the v5 gold board.
        public static bool GOLD_RUSH_SAFES = true;
        public static int  GOLD_RUSH_SAFE_COUNT = 3, GOLD_RUSH_SAFE_HITS = 3, GOLD_RUSH_LAMP_COINS = 10, GOLD_RUSH_JACKPOT = 60;
        public static bool GoldBoardOn => GOLD_RUSH_GOLD_BOARD && !GOLD_RUSH_SAFES;
        public static int  GoldRushSpecialsFired;
        /// <summary>11-17 (7x9 board; Spencer: "two is fine"): three 2-wide safes cannot sit evenly on 7 columns (two touch, one
        /// stands apart), so a board under 8 wide gets TWO, one each side. GoldRushPay keeps the level's total coins the same.</summary>
        public static int  GoldRushSafeCount => RulesEngine.COLS < 8 ? Mathf.Min(2, GOLD_RUSH_SAFE_COUNT) : GOLD_RUSH_SAFE_COUNT;
        public static int  GoldRushPay(int coins) => GoldRushSafesPlaced > 0 && GoldRushSafesPlaced < GOLD_RUSH_SAFE_COUNT ? Mathf.RoundToInt(coins * (float)GOLD_RUSH_SAFE_COUNT / GoldRushSafesPlaced) : coins;
        public static int  GoldRushSafesPlaced;   // v6: safes this Gold Rush started with (the level ends early when they're all open)
        public static int  GoldRushSafesStage = -1;   // ...and the internal stage they belong to (the early end only applies there)
        private static int s_rushMultShown = 1;
        /// <summary>Gold Rush payout multiplier (1 off the bonus level); announces each step up with the banner.</summary>
        /// <summary>The multiplier right now, no side effects (the HUD badge reads this every frame).</summary>
        public static int GoldRushMultNow()
            => !BonusLevelActive || GOLD_RUSH_SAFES ? 1 : Mathf.Clamp(1 + GoldRushSpecialsFired / Mathf.Max(1, GOLD_RUSH_SPECIALS_PER_STEP), 1, GOLD_RUSH_MAX_MULT);
        public static int GoldRushMult()
        {
            if (!BonusLevelActive) return 1;
            int m = GoldRushMultNow();
            if (m > s_rushMultShown && !RulesEngine.Simulating)
            {
                s_rushMultShown = m;
                BonusPopup.Instance?.Show($"GOLD RUSH x{m}!", new Color(1f, 0.85f, 0.3f), Vector3.zero, 1.3f);
                WDLog.Log($"[GoldRush] payout now x{m} ({GoldRushSpecialsFired} specials fired)");
            }
            return m;
        }
        /// <summary>Loads the Gold Rush board with specials (live build only; the bot never plays the bonus).</summary>
        private static void LoadGoldRush(RulesEngine rules, System.Collections.Generic.HashSet<Vector2Int> keep)
        {
            s_rushMultShown = 1; GoldRushSpecialsFired = 0;
            GoldRushSafesPlaced = 0; GoldRushSafesStage = GoalsStage;   // Apply sets GoalsStage = this level's internal stage first
            if (GOLD_RUSH_SAFES)
            {   // v6: safes first (they're 2x2 on the floor), so the specials land around them
                RulesEngine.SafeHitsNow = GOLD_RUSH_SAFE_HITS;
                rules.PlaceShelves(0, keep, GoldRushSafeCount, out GoldRushSafesPlaced);
            }
            int n = 0;
            for (int i = 0; i < GOLD_RUSH_ROCKETS; i++) n += rules.PlaceStartSpecial(i % 2 == 0 ? RulesEngine.SPECIAL_ROCKET_H : RulesEngine.SPECIAL_ROCKET_V, 1, keep, GOLD_RUSH_GAP);
            n += rules.PlaceStartSpecial(RulesEngine.SPECIAL_BOMB, GOLD_RUSH_BOMBS, keep, GOLD_RUSH_GAP);
            n += rules.PlaceStartSpecial(RulesEngine.SPECIAL_AIRPLANE, GOLD_RUSH_AIRPLANES, keep, GOLD_RUSH_GAP);
            n += rules.PlaceStartSpecial(RulesEngine.SPECIAL_DISCO, GOLD_RUSH_DISCOS, keep, GOLD_RUSH_GAP);
            int g = 0;
            if (GoldBoardOn)
            {   // EVERY letter gold (10-30 Spencer: the + channel "looks kind of stupid unless all the letters are gold") - gold
                // words already glow gold and gold letters already throw gold debris, so the whole level reads gold
                for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++)
                {
                    var d = rules.GetCell(x, y);
                    if (d == null || !char.IsLetter(d.Letter) || d.IsStone || d.SpecialKind != RulesEngine.SPECIAL_NONE) continue;
                    d.IsRainGold = true; g++;
                }
            }
            WDLog.Log($"[GoldRush] board loaded with {n} special(s), {g} gold coin tile(s)");
        }
        private static void PayBonusWord(string word, System.Collections.Generic.List<Vector2Int> cells)
        {
            int coins = word.Length * BONUS_COINS_PER_LETTER;   // flat - the Gold Rush multiplier is for special blasts (v3)
            if (GoldBoardOn)
            {   // v4: only the word's GOLD tiles pay (still on the board here - DoExplode clears them after this call)
                var re = RulesEngine.Instance; var gold = new System.Collections.Generic.List<Vector2Int>();
                if (re != null) foreach (var c in cells) { var d = re.GetCell(c.x, c.y); if (d != null && d.IsRainGold) gold.Add(c); }
                cells = gold;
                coins = gold.Count * GOLD_RUSH_COIN_TILE;   // flat, like v3 - the multiplier is for special blasts
                if (coins <= 0) return;
            }
            CoinWallet.Add(coins);
            SurvivalManager.PendingBonusCoins += coins;
            LevelMapPanel.Instance?.AddPendingCoinReward(coins);
            var gm = GridManager.Instance; var hud = HUDManager.Instance;
            if (gm == null || hud == null || cells == null) return;
            var starts = new System.Collections.Generic.List<Vector3>();
            foreach (var c in cells) starts.Add(gm.CellToWorld(c.x, c.y) + new Vector3(gm.BoardSlideOffsetX, 0f, -0.3f));
            hud.SpawnBonusCoins(starts, coins);
        }

        public static void OnWordPopped(string word, System.Collections.Generic.List<Vector2Int> cells)
        {
            if (BonusLevelActive && !RulesEngine.Simulating && !string.IsNullOrEmpty(word)) PayBonusWord(word, cells);
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
            var re = RulesEngine.Current;   // the board being played — the bot's while it simulates (was always the live one)
            if (g.Type == GoalType.Crates) return re != null ? Mathf.Clamp(g.Target - re.CountCrateCells(), 0, g.Target) : 0;
            if (g.Type == GoalType.Ice)    return re != null ? Mathf.Clamp(g.Target - re.CountFrozenCells(), 0, g.Target) : 0;
            if (g.Type == GoalType.Safes)  return re != null ? Mathf.Clamp(g.Target - re.CountSafes(), 0, g.Target) : 0;   // opened
            if (g.Type == GoalType.Books)  return re != null ? Mathf.Clamp(g.Target - re.CountShelfBooks(), 0, g.Target) : 0;   // knocked off
            if (g.Type == GoalType.Grass)  return re != null ? Mathf.Clamp(g.Target - re.CountGrass(), 0, g.Target) : 0;   // mowed
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
                             : Goals.Count > 1 ? (g.Type == GoalType.Ducks ? "Ducks " : g.Type == GoalType.Theme ? char.ToUpperInvariant(g.Theme[0]) + g.Theme.Substring(1) + " " : g.Type == GoalType.Ice ? "Ice " : g.Type == GoalType.Gems ? "Gems " : g.Type == GoalType.Books ? "Books " : g.Type == GoalType.Safes ? "Safes " : "Crates ") : "";
                bool flies = g.Type == GoalType.Collect || g.Type == GoalType.Gems || g.Type == GoalType.Ducks;
                int shown = flies ? Mathf.Max(0, GoalProgress(g) - HUDManager.CollectInFlight - CollectAwaitingFly)
                          : g.Type == GoalType.Books ? Mathf.Max(0, GoalProgress(g) - HUDManager.BooksInFlight - BooksAwaitingFly) : GoalProgress(g);
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
                                       int fillRows = 5, float density = 0.92f, int crates = 0, char collect = '\0', int collectN = 0, int gems = 0, string theme = null, int themeN = 0, int ducks = 0,
                                       int moves = 0, bool bonus = false, string teach = null, string crateLayout = null, string iceLayout = null, int grass = 0, int grassSeeds = 3, string grassLayout = null, string shape = null, string crateHits = null, int shelfBooks = 0, int shelfStyle = 0, int safes = 0)
            => new LevelRules {
                Name = name, IceCount = ice, MinWordLength = minLen, RiseCadence = cadence,
                BranchPriming = branch, FuseOn = fuse, GoldTiles = gold,
                FillRows = fillRows, Density = density, Crates = crates, CollectLetter = collect, CollectCount = collectN, Gems = gems, Theme = theme, ThemeCount = themeN, Ducks = ducks,
                Moves = moves, Bonus = bonus, Teach = teach, CrateLayout = crateLayout, IceLayout = iceLayout, Grass = grass, GrassSeeds = grassSeeds, GrassLayout = grassLayout, Shape = shape, CrateHits = crateHits, ShelfBooks = shelfBooks, ShelfStyle = shelfStyle, Safes = safes };

        /// <summary>The authored opening. One named twist per stage — that is what makes a level
        /// feel designed instead of a number going up. Keep each stage to ONE new idea.</summary>
        private static readonly LevelRules[] TABLE_OLD = {
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

        // ── LEVEL CURVE 2026-10-23 (level design delegated to Claude; Royal Match L1-20 pacing from Spencer's recording —
        // memory reference_royal_match_levels_1_20). L1-5: no blockers, one power-up taught per level. Then a new blocker about
        // every 4 levels with variations, then mixes; first TIGHT stretch L17-19; L20 = no-fail bonus. crates:N places 2N
        // (FULL_CRATE_MULT). Moves from the two-bot run of 10-23 09:53 (casual p50/p90: crates12 9/17, crates24 15/22,
        // crates28 18/27, gems10 17/30, gems12 20/31, ice8 17/26, ice12 23/41, theme3 6/11, ducks4 38/57) — RETUNE WITH THE BOT.
        // Ducks are slow (casual 15% at 25 moves) — kept out of 1-20. NEW_CURVE=false restores TABLE_OLD. ──
        public static bool NEW_CURVE = true;
        /// <summary>Shaped boards on/off (2026-10-28). false = every level is the plain rectangle again.</summary>
        public static bool SHAPES_ON = true;
        private static LevelRules[] TABLE => NEW_CURVE ? TABLE_NEW : TABLE_OLD;
        private static readonly LevelRules[] TABLE_NEW = {
            // MOVES re-tuned 2026-10-23 from bot run 12:43 (20 games/bot): casual p95 for tutorial/teach levels (target >=98% casual),
            // ~90% for onboarding, L17-19 left as the spike (casual 75-80%), L18 eased 65%->~75% so no two <70% levels sit together.
            // Pass 5c (Spencer's 2nd L1-20 run: spike didn't bite — L17 15/26, L19 14/28 with a win streak): L17 26 -> 22, L19 28 -> 24
            // (casual ~35-45%; RM's spike = the first 'Play On?' moments). L18 stays the breather.
            // Pass 5b (18:53 run, goal-progress bug fixed — expert 95-100%): ice L9-12 -> 24 (88-93%, ~50% left now ice is off the
            // edges), L7 26 (90%), L6 28 (73%), L14 20 (68%), L16 28 (70%, teaches boosters), L17 26 (45% -> ~55%).
            // Pass 5 (Spencer played L1-20 on 7x9: cleared all, no losses, avg ~55% moves left on L6-16 and 19-47% on the L17-19
            // spike vs Royal Match's 20-50% / 0-5% — "theres just way too many moves on most levels"). Moves cut ~20-30%: L1-5 at
            // casual-bot p80, L6-11 p70, L12-16 p65, L17/L19 p50, L18 p70 (17:57 run). Lands in RM's 20-30 move range.
            // Pass 4 (17:57 run, 7x9 board — crates/ice already x63/99 in ScaleForBoard): L1-5 tightened to casual p95 (were 93-100%
            // with 50-58% left), L6-12 loosened (65-80%), L7/12/15/16/19 goal trims, ice spawns off the edges (ICE_AVOID_EDGES).
            // Pass 5 (2026-10-30 10:09 run of pass 4: L1-13 on target 93-100%; L14 73% -> 20, L17 93% (easy) -> 24, L18 85% (relief
            // should be ~95) -> 22, L19 65% -> 23).
            // Pass 4 (2026-10-30, the run 09:44 AFTER the letter-supply fix; casual win jumped from 30-98% to 62-100% at the same budgets):
            // budgets set from the casual bot's own distribution to the research bands. L1-5 kept generous (tutorial); onboarding at
            // 92-96%; L14 the soft check (~82%); L17 the first tight level eased 22->25 (62% -> ~80%); L18 relief (~95%); L19 ~85%.
            // +1 buffer where a cut was over 4 moves (40 games per level = about ±7 points). Tuned with NO win streak.
            // Pass 3 (15:20 run — FIRST human-calibrated: casual wastes 50% of moves, sees common words only, fishing hint + endgame
            // rescue on): goal COUNTS cut to each level's casual p85-p90 so budgets stay 26-34; L2 / L8 single goal (RM mixes kinds from L12).
            // Pass 2 (12:56 run, 40 games/bot): L5 20->22 (teach level at 90%), L10 38->32 (overshot: 100%, 57% left), L19 30->28 (93% — the
            // Gauntlet should bite). Curve now: L1-16 casual 93-98%, L17 the first hard (70%, 28% near-miss), L18 relief 90%, L19 ~85%.
            /* 1  */ Make("First Gems",     gems:6,               moves:14, teach:"swap"),   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 13->14.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 15->13.   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 12->15   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 8->12 moves      // RM L1 "collect 12 red" -> yellow gems   // 11-02 pass 13: 20->14 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 14->12 (bot 11:35 rerun)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 12->13   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 13->11   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 11->8
            /* 2  */ Make("Gem Swipe",      gems:7,               moves:14, teach:"swipe"),   // 11-17 BOT (10:25 x100, casual): 94% at 13, p95 = 14 -> 14.   // 11-04 pass 27 (bot 09:06 x40): 11->13 (80%, tutorial ~95)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 10->11 moves     // ONE goal: RM mixes goal KINDS only from L12 (Spencer 10-23); teaches SWIPE   // 11-02 pass 13: 20->13 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 13->11 (bot 11:35 rerun)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 11->10
            /* 3  */ Make("Rocket",         gems:7,               moves:16, teach:"rocket"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 15->16.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 17->15.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 21->17.   // 11-17 BOT (10:25 x100, casual): 88% at 18, p95 = 21 -> 21.   // 11-17: 14->18. Spencer's own run (no start TNT, spaced gems): 12 of 14 used = 14% left; RM leaves 50-65% on L1-5. PROVISIONAL until the bot pass.   // 11-17 RM ROLLOUT (Spencer: follow RM exactly): was crates:6 band - RM has NO obstacle before L6, the lesson is the only new thing. NOT bot-checked.   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 11->14   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 9->11 moves    // 5-letter word   // 11-02 pass 13: 18->16 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 16->14 (bot 11:35 rerun)   // 11-02 pass 15: 14->13 (bot 13:18, diagonal crates)   // 11-02 pass 16: 13->12 (bot 13:36)   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 12->8   // 11-03 pass 23 (bot 19:28 full x40): 8->9
            /* 4  */ Make("Bomb",           gems:8,               moves:17, teach:"bomb"),   // 11-17: 13->17. Spencer's own run: 11 of 13 used = 15% left. PROVISIONAL until the bot pass.   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 9->13 moves      // crossing words   // 11-02 pass 13: 20->14 (bot after gem-touch + goal re-deal off)   // 11-02 pass 15: 14->12 (bot 13:18, diagonal crates)   // 11-02 pass 16: 12->11 (bot 13:36)   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 11->9
            /* 5  */ Make("Royal Orb",      gems:9,               moves:21, teach:"disco", shape:"octagon"),   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 19->21.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 23->19.   // 11-17 BOT (10:25 x100, casual): 81% at 16 (19% near-miss), p95 = 23 -> 23.   // 11-17 RM ROLLOUT (Spencer: follow RM exactly): was crates:6 corners, moves 19 - gems instead (no obstacle before L6). NOT bot-checked.   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 17->19   // 11-04 pass 30 (bot 10:46 x40): 15->17 (85%, disco teach level wants ~95%)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 13->15 moves   // first SHAPED board (RM L4/5: first shape)     // 6+ letter word   // 11-02 pass 15: 20->17 (bot 13:18, diagonal crates)   // 11-02 pass 16: 17->16 (bot 13:36)   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 16->15   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 15->11   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 11->13
            /* 6  */ Make("Crate Yard",     crates:8,             moves:19, teach:"crates", crateLayout:"pyramid"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 17->19.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 21->17.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 22->21.   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 19->22   // 11-04 pass 27 (bot 09:06 x40): 17->19 (pyramid fixed END-STALL 7.5->3.1; 75%)   // 11-04 pass 26 (bot 08:42 x40, layout pass): wall -> pyramid (fixed crates: a floor slab only shows its top faces - 73%, END-STALL 7.5)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 16->17 moves    // FIRST BLOCKER, big count (RM L6: 48 boxes)   // 10-31 pass 11: 26->23 (bot w/ drags)   // 11-02 pass 13: 23->25 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 25->27 (bot 11:35 rerun)   // 11-02 pass 15: 27->22 (bot 13:18, diagonal crates)   // 11-02 pass 16: 22->24 (bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 24->22   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 22->14   // 11-03 pass 23 (bot 19:28 full x40): 14->16
            /* 7  */ Make("Stacked",        crates:5,             moves:24, crateLayout:"pillars", crateHits:"2"),   // 11-04 pass 28 (bot 09:30 x40): 23->24 (90%)   // 11-04 pass 27 (bot 09:06 x40): 21->23 (85%)   // 11-04 pass 26 (bot 08:42 x40, layout pass): 26->21 moves (100%, 52% left)   // 11-04 LAYOUT PASS (RM structure study; fixed crates; bot 08:03 END-STALL): columns -> pillars, crates 8 -> 5 (x2 = two 5-tall pillars: rows 5-9 stay full width for across-words) + hits 1,2 -> 2 (20 hits vs ~24) (edge crates had one open face, END-STALL 6.9)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 22->26 moves, 9->8 crates   // 11-02 pass 13: 27->25 (bot after gem-touch + goal re-deal off)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 25->22   // 11-03 multi-hit crates: "1,2"
            /* 8  */ Make("Crate Run",      crates:9,             moves:12, crateLayout:"lid", shape:"funnel"),   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): crates 6->9, moves 15->12.   // 11-17 RM ROLLOUT (Spencer: follow RM exactly): the Flying Tome lesson moved to L10 (L8 already has the first booster-slot lesson).   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 12->15   // 11-04 pass 26 (bot 08:42 x40, layout pass): 25->12 moves (100%, 75% left; airplane teach level stays generous)   // 11-04 LAYOUT PASS (RM structure study; fixed crates; bot 08:03 END-STALL): pyramid -> lid, crates 10 -> 6 (x2 = 12 = the whole lid; GOAL-DEAD 40%: the pyramid sat in the funnel tip)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 21->25 moves, 12->10 crates  // ONE goal (was crates + gems); airplane flies to a crate   // 11-02 pass 13: 26->31 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 31->33 (bot 11:35 rerun)   // 11-02 pass 15: 33->30 (bot 13:18, diagonal crates)   // 11-02 pass 16: 30->28 (bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 28->31   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 31->17   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 17->21
            /* 9  */ Make("Ice Breaker",    ice:11, iceLayout:"spread", moves:12, teach:"ice"),   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): ice 7->11, moves 15->12.   // 11-06 Spencer L1-10 recording (moves left 17-35% / lost vs RM 50-65% L1-5, 35-50% L6-11): 13->15   // 11-06 Spencer recording: lost the ice lesson level at 12 (bot 85% vs ~95% teach target): 12->13   // 11-04 pass 30 (bot 10:46 x40): 11->12 (85%, ice teach level wants ~95%)   // 11-04 pass 25 (bot 06:48 x40): 13->11 moves (overshot: L9 100%, L10 98%, L17 80% vs 65, L19 85% vs 70)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 8->13 moves, 9->7 ice       // SECOND BLOCKER (grass tried here 10-23: "lame ... boring and it looks bad" — tiles hide the lawn; code kept, GRASS_ON)   // 10-31 pass 11: 18->12 (bot w/ drags)   // 11-02 pass 13: 12->11 (bot after gem-touch + goal re-deal off)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 11->8
            /* 10 */ Make("Cold Snap",      ice:8, iceLayout:"spread", moves:13, teach:"airplane"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 12->13.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 15->12.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 12->15.   // 11-17 BOT (10:25 x100, casual): 100% at 14, p90 9 -> back to 12.   // 11-17 RM ROLLOUT (Spencer: follow RM exactly): Flying Tome lesson here (RM teaches nothing on L10); 12->14 as a lesson level. NOT bot-checked.   // 11-04 pass 27 (bot 09:06 x40): 14->12 (no notches: GOAL-DEAD 54->31%, 98%)   // 11-04 pass 26 (bot 08:42 x40, layout pass): shape notches -> plain (GOAL-DEAD 54%, 11 word swaps/turn vs ~18)   // 11-04 pass 25 (bot 06:48 x40): 17->14 moves (overshot: L9 100%, L10 98%, L17 80% vs 65, L19 85% vs 70)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 10->17 moves, 11->8 ice   // 10-31 pass 11: 16->13 (bot w/ drags)   // 11-02 pass 13: 13->12 (bot after gem-touch + goal re-deal off)   // 11-02 pass 16: 12->14 (bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 14->11   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 11->8   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 8->10
            /* 11 */ Make("Deep Freeze",    ice:9, iceLayout:"spread", moves:10, shape:"notches"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 11->10.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 13->11.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 14->13.   // 11-17 shape added for variety (Spencer: the same board every level "gets pretty stale") - bot to confirm.   // 11-04 pass 27b (bot 09:06): shape lanes -> plain (GOAL-DEAD 39%, END-STALL 5.7 - same fix as L10)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 9->14 moves, 12->9 ice   // 10-30: was "split" (goal dead 52% of turns)   // RM L11 split board   // 11-02 pass 13: 13->11 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 11->13 (bot 11:35 rerun)   // 11-02 pass 15: 13->14 (bot 13:18, diagonal crates)   // 11-02 pass 16: 14->13 (bot 13:36)   // 11-02 pass 17 CURVE: 13->10 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 10->11   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 11->12   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 12->9
            /* 12 */ Make("Frozen Crates",  crates:5, ice:6,      moves:12, crateLayout:"ring", iceLayout:"spread", crateHits:"1,2"),   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 15->12.   // 11-04 pass 29 (bot 10:06 x40): 14->15 (78%, soft check ~82)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 12->14 moves                    // FIRST goal MIX (RM: L12)   // 10-31 pass 11: 20->19 (bot w/ drags)   // pass 12: 19->16   // 11-02 pass 13: 16->19 (bot after gem-touch + goal re-deal off)   // 11-02 pass 15: 19->16 (bot 13:18, diagonal crates)   // 11-02 pass 16: 16->15 (bot 13:36)   // 11-02 pass 17 CURVE: 15->12 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 12->10   // 11-03 multi-hit crates: "1,2"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 10->12
            /* 13 */ Make("The Library",    shelfBooks:6,         moves:22),   // 11-04 pass 29 (bot 10:06 x40): 21->22 (85%)   // 11-04 pass 28 (bot 09:30 x40): 20->21 (90%)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 19->20 moves   // 11-03 BOOKSHELF intro (Spencer's library piece; alone, per the one-new-thing rule). Was: Make("Gem Mine",       gems:6, crates:4,     moves:11, crateLayout:"diamond", crateHits:"2"),                    // was "Animal Kingdom" (theme) — Spencer 10-23: "the animal words idea isnt fun"; theme goals = vocabulary luck. Stone (2-hit crate) planned here   // 10-31 pass 11: 20->16 (bot w/ drags)   // 11-02 pass 13: 16->19 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 19->14 (bot 11:35 rerun)   // 11-02 pass 15: 14->13 (bot 13:18, diagonal crates)   // 11-02 pass 17 CURVE: 13->11 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 11->10   // 11-03 multi-hit crates: "2"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 10->11   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 10->17   // 11-03 pass 22 (bot 17:37 x80, diagonal-aware bot): 17->19
            /* 14 */ Make("Crate & Ice",    crates:5, ice:4, crateLayout:"lid", iceLayout:"spread", moves:15, crateHits:"1,2"),   // 11-18 7x9 FINAL (bot 06:57 x100): 84% at 13, p90 14 / p95 16 -> 15.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 15->13.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 18->15.   // 11-17 BOT (10:25 x100, casual): 99% at 21 (the new fill rule made it easier), p95 = 17 -> 18.   // 11-04 pass 27b (bot 09:06): pyramid -> lid (END-STALL 7.2: the pyramid floor row shows only its top faces)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 17->21 moves   // 11-02 pass 17 CURVE: 20->18 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 18->13   // 11-03 multi-hit crates: "1,2"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 13->14   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 14->17
            /* 15 */ Make("Triple",         crates:5, ice:4, gems:4, moves:22, crateLayout:"lid", iceLayout:"spread", crateHits:"1,2"),   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 21->22.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 22->21.   // 11-17 BOT (10:25 x100, casual): 82% at 19 (17% near-miss; spaced gems), p90 22 -> 22.   // 11-04 pass 26 (bot 08:42 x40, layout pass): 25->19 moves (100%)   // 11-04 LAYOUT PASS (RM structure study; fixed crates; bot 08:03 END-STALL): corners -> lid (END-STALL 6.8: corner crates)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 19->25 moves, gems 5->4                 // first THREE-goal level   // 10-31 pass 11: 26->24 (bot w/ drags)   // 11-02 pass 13: 24->26 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 26->24 (bot 11:35 rerun)   // 11-02 pass 15: 24->21 (bot 13:18, diagonal crates)   // 11-02 pass 17 CURVE: 21->20 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 20->16   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 16->19   // 11-03 multi-hit crates: "1,2"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 19->16   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 16->19
            /* 16 */ Make("Gearing Up",     crates:5, ice:4,      moves:23, teach:"boosters", crateLayout:"pillars", iceLayout:"spread", crateHits:"1,2", shape:"octagon"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 21->23.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 23->21.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 22->23.   // 11-17 shape added for variety (Spencer: the same board every level "gets pretty stale") - bot to confirm.   // 11-04 pass 26 (bot 08:42 x40, layout pass): 30->22 moves (100%)   // 11-04 LAYOUT PASS (RM structure study; fixed crates; bot 08:03 END-STALL): columns -> pillars, crates 6 -> 5 (x2 = 5-tall pillars)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 26->30 moves, ice 6->4, crateHits 2,3->1,2  // RM L16: booster pick before play   // 10-31 pass 11: 24->21 (bot w/ drags)   // 11-02 pass 13: 21->23 (bot after gem-touch + goal re-deal off)   // 11-02 pass 15: 23->21 (bot 13:18, diagonal crates)   // 11-02 pass 16: 21->20 (bot 13:36)   // 11-02 pass 17 CURVE: 20->17 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 17->21   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 21->19   // 11-03 multi-hit crates: "2,3"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 19->24   // 11-03 pass 23 (bot 19:28 full x40): 24->26
            /* 17 */ Make("The Squeeze",    shelfBooks:6, crates:6, moves:21, crateLayout:"band", crateHits:"2,3", shelfStyle:1),   // 11-04 pass 31: 20->21 (same settings over 4 runs x40: 70/48/65/45% = ~57% avg vs ~65% target)   // 11-04 FIXED CRATES: 8 crates in "band" = one FULL-WIDTH row, which now seals the rows below it (nothing can slide past) and starves the shelf on the floor. 6 = cols 1-6, the edge columns feed letters down; hits 1,2,3 -> 2,3 keeps the total (~16 -> ~15).   // 11-04 pass 25 (bot 06:48 x40): 23->20 moves (overshot: L9 100%, L10 98%, L17 80% vs 65, L19 85% vs 70)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 18->23 moves   // 11-03 v2: bot 19:50 casual 43% (expert 98%, near-miss 41%) with "2,3" -> mix in single-layer crates (~6 fewer hits) toward ~65%.   // 11-03 CONTENT-HARD (Spencer: hard via content, not fewer moves - RM style): shelf (L13) + 2-3 layer crates, same 18 moves; target ~65%. Was: Make("The Squeeze",    crates:10, ice:5,     moves:18, crateLayout:"diamond", iceLayout:"spread", crateHits:"1,2,3"),   // 10-31: was wall + sidenotch (goal dead 46%, help in 90% of games)                    // FIRST TIGHT (RM: won with 1 move) — target ~60%   // 10-31 pass 11: 18->16 (bot w/ drags)   // 11-02 pass 13: 16->19 (bot after gem-touch + goal re-deal off)   // 11-02 pass 15: 19->18 (bot 13:18, diagonal crates)   // 11-02 pass 16: 18->16 (bot 13:36)   // 11-02 pass 17 CURVE: 16->10 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 10->12   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 12->11   // 11-03 multi-hit crates: "1,2,3"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 11->15   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 15->18
            /* 18 */ Make("The Vault",      safes:1,              moves:18),   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 15->18.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 18->15.   // 11-04 pass 28 (bot 09:30 x40): 16->18 (88%, relief target ~92)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 14->16 moves   // 11-03 SAFE intro (Spencer's safe; alone, relief slot). Was: Make("Cold Hands",     ice:7, gems:6,        moves:11, iceLayout:"spread", shape:"hole"),                    // relief-ish ~80%   // 10-31 pass 11: 22->19 (bot w/ drags)   // pass 12: 19->17   // 11-02 pass 13: 17->14 (bot after gem-touch + goal re-deal off)   // 11-02 pass 14: 14->12 (bot 11:35 rerun)   // 11-02 pass 15: 12->13 (bot 13:18, diagonal crates)   // 11-02 pass 16: 13->14 (bot 13:36)   // 11-02 pass 17 CURVE: 14->13 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 13->12   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 12->11   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 12->16   // 11-03 pass 22 (bot 17:37 x80, diagonal-aware bot): 16->14
            /* 19 */ Make("Gauntlet",       crates:6, ice:6, gems:6, moves:21, crateLayout:"lid", iceLayout:"spread", crateHits:"2", shape:"arch"),   // 11-18 7x9 PASS 3 (bot 06:45 x40): moves 20->21.   // 11-17 7x9 PASS 2 (bot 15:17 x40): moves 22->20.   // 11-17 7x9 PASS 1 (bots 14:21 + 14:51 x40; the 8x10 numbers live in tag checkpoint-2026-11-17-8x10-phone-pass): moves 15->22.   // 11-17 shape added for variety (Spencer: the same board every level "gets pretty stale") - bot to confirm.   // 11-16 SEEDED (reroll 1, bot 07:12 x100): 20->15 (96% -> 82% casual; 2 seeds both ~95% at 20)   // 11-04 pass 30 (bot 10:46 x40): gems 5->6 (83%, expert 95%; content not moves toward ~75%)   // 11-04 pass 29 (bot 10:06 x40): 22->20 (90% after ice 4->6; 75% quantile = 19)   // 11-04 pass 28 (bot 09:30 x40): ice 4->6 + moves 24->22 (95% / expert 100% / END-STALL 2.2 - fair now, too easy for the ~70% hard slot; content first, 2 moves)   // 11-04 pass 27b (bot 09:06): arch shape removed + hits 2,3 -> 2 (expert only 83%, END-STALL 8.5, 12 word swaps/turn: luck-decided; keep the lid as the structure)   // 11-04 pass 26 (bot 08:42 x40, layout pass): 23->24 moves (60%, target ~65-70)   // 11-04 LAYOUT PASS (RM structure study; fixed crates; bot 08:03 END-STALL): ring -> lid, crates 7 -> 6 (x2 = 12 = the whole lid), hits 1,2 -> 2,3 (93% vs ~70% target: hard by STRUCTURE - goals under a 2-3 layer roof)   // 11-04 pass 25 (bot 06:48 x40): 26->23 moves (overshot: L9 100%, L10 98%, L17 80% vs 65, L19 85% vs 70)   // 11-04 pass 24 STRICT RULES (bot 04:22 x40, casual quantile): 21->26 moves, ice 6->4, gems 6->5, crateHits 2,3->1,2   // 10-30: was "legs" (goal dead 45%)                 // tightest ~65% (RM: won on the last move)   // 10-31 pass 11: 22->19 (bot w/ drags)   // 11-02 pass 17 CURVE: 19->12 (Spencer "the game is pretty easy now"; L11-13/15-16 ~88%, L14 ~80%, L17 ~65%, L18 relief, L19 ~75%; casual-bot quantiles, bot 13:36)   // 11-03 pass 18 (8x10, bot 11-02 20:24 casual quantile): 12->14   // 11-03 pass 19 (bot 07:08, 8x10 + bigger bombs): 14->15   // 11-03 multi-hit crates: "2,3"   // 11-03 pass 20 (wild power-ups + bot moves specials + multi-hit crates, bot 10:20): 15->18   // 11-03 pass 21 (bot 17:17, shelf/safe/upright rocket/graded crates): 18->21
            /* 20 */ Make("Gold Rush",      bonus:true,           moves:GOLD_RUSH_MOVES, teach:"bonus"),     // no fail: preloaded specials, 3-letter swaps, climbing payout (10-30)
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
        public static int MovesForRules(int stage, LevelRules r) => r.Moves > 0 ? r.Moves : MovesFor(stage) + (r.MinWordLength >= 4 ? MIN4_EXTRA_MOVES : 0);
        public static int CadenceForRules(LevelRules r) => Mathf.Max(1, r.RiseCadence) + (r.MinWordLength >= 4 ? MIN4_EXTRA_CADENCE : 0);

        /// <summary>Cap on fresh-board fill density. 2026-10-20: at 0.90-0.96 the opening board was packed with
        /// accidental words, so the first moves cascaded LEVEL 1 to a win in 2-4 moves (CASE->ROB->ETA, 288).
        /// 0.80 keeps cascades common but less automatic.</summary>
        public static float FRESH_BOARD_DENSITY_CAP = 0.80f;

        public static bool BlastLimitMode = false;   // 2026-10-20 Spencer tried it: "meh ... at least with the row rising there was some pressure and it introduces new letters"
        /// <summary>Blasts per level. One number for every level while the idea is being felt —
        /// per-level tuning comes after we know whether it is fun.</summary>
        public static int  BlastsPerLevel = 5;

        /// <summary>SMALL BOARD (2026-10-23): blocker counts were tuned on 99 cells — on 7x9's 63 the same crates would be over a
        /// third of the board. Crates and ice scale by the cell ratio (63/99); gems / theme / ducks are about words, not area,
        /// and keep their counts. Moves get re-tuned from the bot.</summary>
        public static float SMALL_BOARD_BLOCKER_SCALE => RulesEngine.COLS * RulesEngine.LEVEL_ROWS / 99f;   // 11-02: by cell count (7x9 63/99, 8x10 80/99)
        /// <summary>Bot: sim games whose gem goal had to be lowered because the gems did not all fit (should stay 0).</summary>
        public static int SimGemShortfall;
        private static LevelRules ScaleForBoard(LevelRules r)
        {
            if (!RulesEngine.SMALL_BOARD) return r;
            if (r.Crates > 0) r.Crates = Mathf.Max(1, Mathf.RoundToInt(r.Crates * SMALL_BOARD_BLOCKER_SCALE));
            if (r.IceCount > 0) r.IceCount = Mathf.Max(1, Mathf.RoundToInt(r.IceCount * SMALL_BOARD_BLOCKER_SCALE));
            return r;
        }

        public static LevelRules For(int stage)
        {
            if (stage < 1) stage = 1;
            if (stage <= TABLE.Length) return ScaleForBoard(TABLE[stage - 1]);

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
        private static int s_giftStage = -1;
        /// <summary>Last stage this ran for (-1 = none yet this run). Any change of stage — INCLUDING the
        /// first install of a run since 2026-10-20 — gets a fresh LevelRules board; a repeated install of
        /// the same stage does not. (The first level used to keep OpeningSeed's sparse board; see Apply.)</summary>
        private static int s_lastStage  = -1;
        /// <summary>Next Apply rebuilds the board even on the same stage (full-board / rising-row toggle).</summary>
        public static void ForceFreshBoardOnNextApply() => s_lastStage = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { s_icedStage = -1; s_lastStage = -1; s_giftStage = -1; }

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
            Add(GoalType.Safes, r.Safes);
            Add(GoalType.Books, r.ShelfBooks);
            Add(GoalType.Crates, r.Crates > 0 ? Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT) : 0);
            Add(GoalType.Ice, r.IceCount);
            Add(GoalType.Grass, r.Grass);
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
            if (r.Bonus && GOLD_RUSH_SAFES)   // v6 (11-03): crack the safes
                return $"<b>Gold Rush!</b>\nYou can't lose this one. Crack open the <b>{GoldRushSafeCount} safes</b> in <b>{moves} moves</b> - every hit pays, and each safe you blow open pays a <b>jackpot</b>. Specials hit hardest!";
            if (r.Bonus)   // BONUS LEVEL (2026-10-23): nothing to lose, just coins
                return $"<b>Gold Rush!</b>\nYou can't lose this one. <b>{moves} moves</b>, 3-letter swaps count, and every letter you pop pays coins. <b>Fire specials</b> to raise the payout!";
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
            int grassN = r.Grass > 0 ? GoalCount(GoalType.Grass, r.Grass) : 0;
            if (grassN > 0)  { goals.Add($"Mow <b>{grassN} grass</b>");
                               how.Add("<b>Grass</b> is mowed when a letter on it pops — words, rockets and bombs all mow it."); }
            int safesN = r.Safes > 0 ? GoalCount(GoalType.Safes, r.Safes) : 0;
            if (safesN > 0)  { goals.Add(safesN == 1 ? "Crack open <b>the safe</b>" : $"Crack open <b>{safesN} safes</b>");
                               how.Add($"Each word <b>touching the safe</b> lights a lamp. Light all {RulesEngine.SAFE_HITS} to open it - and keep the coins inside!"); }
            int booksN = r.ShelfBooks > 0 ? GoalCount(GoalType.Books, r.ShelfBooks) : 0;
            if (booksN > 0)  { goals.Add($"Knock <b>{booksN} books</b> off the shelf");
                               how.Add("Make a word <b>touching the bookshelf</b> to knock a book off. Rockets and bombs knock books off too."); }
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
            if (!RulesEngine.Simulating) { AnalyticsManager.Log("level_start", "stage", internalStage, "level", LevelMapPanel.DisplayNum(internalStage)); PlaythroughStats.Reset(); }   // 11-04 play log: start line + fresh pace clock
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
                Goals.Clear(); Collected = 0; CollectAwaitingFly = 0; BooksAwaitingFly = 0; GoalsStage = internalStage; GoldRushSafesPlaced = 0;   // 11-03: a new level never inherits Gold Rush's early end
                BonusLevelActive = SurvivalManager.FullBoardActive && r.Bonus;
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
                    RulesEngine.SetBoardShape(SHAPES_ON ? r.Shape : null);   // shaped board (2026-10-28): void cells never get a tile
                    rules2.FillBoardNoWords();
                    rules2.SnapshotForRain(out var before, out var _);
                    rules2.PlantNearWordsOnBoard(RulesEngine.FULL_START_PLANTS, spreadRows: true);
                    rules2.SnapshotForRain(out var after, out var _);
                    var keep = new System.Collections.Generic.HashSet<Vector2Int>();
                    for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++) if (before[x, y] != after[x, y]) keep.Add(new Vector2Int(x, y));
                    RulesEngine.TutorialSwipeCells = null;
                    if (TutorialBeats.WantsSwipePlant(r)) { var sw = rules2.PlantSwipeWord(keep, out _); if (sw != null) foreach (var c in sw) keep.Add(c); }
                    int shortWords = rules2.SeedShortWords(RulesEngine.FULL_START_SHORT_WORDS, keep);
                    RulesEngine.CrateLayout = r.CrateLayout; RulesEngine.IceLayout = r.IceLayout;
                    RulesEngine.ShelfStyleNext = r.ShelfStyle; RulesEngine.SafeHitsNow = RulesEngine.SAFE_HITS; int safes = 0; int books = (r.ShelfBooks > 0 || r.Safes > 0) ? rules2.PlaceShelves(r.ShelfBooks, keep, r.Safes, out safes) : 0;   // BOOKSHELF (11-03) first: crates must not land on its cells
                    int crates = r.Crates > 0 ? rules2.PlaceCrates(Mathf.RoundToInt(r.Crates * FULL_CRATE_MULT), keep) : 0;
                    if (crates > 0) rules2.AssignCrateHits(r.CrateHits);   // 11-03 multi-hit crates
                    if (r.Grass > 0 && RulesEngine.GRASS_ON) { int gp = rules2.PlaceGrass(r.Grass, r.GrassLayout); Goals.Add(new LevelGoal { Type = GoalType.Grass, Target = gp }); }
                    else rules2.ClearGrass();
                    int tnt = rules2.PlaceStartTnt(StartTntFor(stage), keep);
                    if (!r.Bonus) rules2.PlaceTreasures(stage, keep);   // TREASURE (2026-10-27): random bonus loot, never a goal
                    if (BonusLevelActive) LoadGoldRush(rules2, keep);   // GOLD RUSH (10-30): the bonus starts loaded with specials
                    WinStreak.Apply(rules2, keep);   // 2026-10-23: streak rockets / bombs / disco (live only — the bot's SetupSimLevel skips it)
                    int gemsPlaced = r.Gems > 0 ? rules2.PlaceGems((RulesEngine.GEMS_FIXED ? r.Gems : Mathf.Min(r.Gems, RulesEngine.GEM_MIN_ON_BOARD + 2)), keep) : 0;
                    // fixed gems: the board can't get more later, so the goal can never ask for more than were placed
                    if (RulesEngine.GEMS_FIXED && r.Gems > 0)
                        foreach (var gg in Goals) if (gg.Type == GoalType.Gems && gg.Target > gemsPlaced) { WDLog.Log($"[Gems] only {gemsPlaced}/{gg.Target} placed — goal lowered"); gg.Target = gemsPlaced; }
                    int ducksPlaced = r.Ducks > 0 ? rules2.PlaceDucks(Mathf.Min(r.Ducks, RulesEngine.DUCKS_ON_BOARD), keep) : 0;
                    CrateGoal = crates; CrateGoalStage = internalStage;
                    if (crates > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Crates, Target = crates });
                    if (books > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Books, Target = books });
                    if (safes > 0) Goals.Insert(0, new LevelGoal { Type = GoalType.Safes, Target = safes });
                    int scrubbed = rules2.ScrubAccidentalWords(keep);
                    WDLog.Log($"[FullBoard] start structure: {shortWords} short word(s), {crates} crate(s) (goal), {tnt} TNT");
                    HUDManager.Instance?.SnapScoreReadoutForNewLevel();   // redraw now the crate goal exists (it read 0/6000 — snapped before the crates)
                    if (BonusLevelActive) HUDManager.Instance?.EnterRewardMode(0);   // bonus level: the panel counts coins from the start
                    MatchController.Instance?.ResetTurnCounter();
                    GridManager.Instance?.RebuildFromRulesEngine(rules2);
                    GridManager.Instance?.RebuildCellGridForShape();   // sockets + shaped frame for this level's shape (rectangle restores the old art)
                    var gm = GridManager.Instance;   // the rebuild doesn't paint specials — put the TNT on now, not a sync later
                    if (gm != null) for (int x = 0; x < RulesEngine.COLS; x++) for (int y = 0; y < RulesEngine.ROWS; y++)
                    { var d = rules2.GetCell(x, y); if (d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE) gm.GetTile(x, y)?.SetSpecial(d.SpecialKind);
                      if (d != null && d.HasGem) gm.GetTile(x, y)?.SetGem(true);
                      if (d != null && d.IsRainGold) gm.GetTile(x, y)?.SetRainGold(true);   // Gold Rush gold board (10-30)
                      if (d != null && d.IsDropTarget) gm.GetTile(x, y)?.SetDropTargetVisual(true); }
                    WDLog.Log($"[FullBoard] fresh FULL board for LEVEL {stage} — {keep.Count} planted cell(s), {scrubbed} accidental word cell(s) redrawn");
                }
                else {
                RulesEngine.SetBoardShape(null);
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

            // GIFT WORD (2026-10-30): one ready-made word through / beside a goal piece, swipeable as an opening move. Once per level,
            // after every goal piece (crates, gems, ice) is placed; not on the swipe-lesson level (it has its own) or the bonus level.
            if (RulesEngine.GIFT_WORD_ON && SurvivalManager.FullBoardActive && RulesEngine.Instance != null && s_giftStage != internalStage
                && Goals.Count > 0 && GoalsStage == internalStage && !BonusLevelActive && RulesEngine.TutorialSwipeCells == null)
            {
                s_giftStage = internalStage;
                RulesEngine.TeachCells = null; RulesEngine.TeachWord = null;
                // 11-02: a lesson level gets a TEACHING word (one easy swap, beside a goal piece) instead of the gift word
                if (TutorialBeats.WantsTeachPlant(r)) { if (PlantLessonWords(r)) GridManager.Instance?.SyncToRulesState(RulesEngine.Instance); }
                else if (RulesEngine.Instance.PlantGiftWord(out _) != null) GridManager.Instance?.SyncToRulesState(RulesEngine.Instance);
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
