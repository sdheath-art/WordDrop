using UnityEngine;

namespace WordDrop
{
    // ════════════════════════════════════════════════════════════════════════════════════
    //  LEVEL TABLE — VALIDATION MVP (2026-06-15)
    // ════════════════════════════════════════════════════════════════════════════════════
    //  A hardcoded ~15-entry run sequencer. NOT the full procedural pickLevel(n) generator —
    //  that's deferred until the finite 15-level run validates as fun (see
    //  project_wordrop_validation_plan.md). This exists ONLY so the user can play a full run
    //  start-to-finish and feel whether the loop + emotional rhythm retains (the go/no-go gate).
    //
    //  The table is driven by ObjectiveManager: on each new stage/level it reads LevelTable.Get(n)
    //  and installs the entry's mode + difficulty dials. Stage advancement itself is UNTOUCHED —
    //  the survival STAGE system already steps stage→stage; we just key the install off the stage
    //  index (SurvivalManager.CurrentStageIndex, 1-based).
    //
    //  ── DESIGN PRINCIPLES (both encoded in the table below) ──
    //  1. BUMPY "stock-market" curve — a rising baseline WITH dips. hard → a hair easier
    //     (relief/reward) → harder. Peaks AND valleys both trend up. Reward/easy modes (Vault
    //     triage-loot, low Ice count) sit in the VALLEYS; hard modes (Boss LongWord, dense Ice,
    //     tight Vaults) sit at the PEAKS. NOT a straight ramp.
    //  2. KISHŌTENKETSU — each mode is introduced across its appearances in a Ki→Shō→Ten→Ketsu
    //     arc (safe intro → develop → twist → mastery), never cold.
    //
    //  Chain is DEMOTED (not a plannable objective — scoring/celebration bonus only, see launch
    //  direction). So the 4 rotated modes are: LongWord / HeroWord / Vault(loot) / Ice.
    //
    //  ── HOW TO TWEAK THE RHYTHM (Spencer) ──
    //  Everything is in the ENTRIES array below. Each row is one level. To make a level easier,
    //  flip its Profile down a notch (Boss→Tricky→Normal→Breezy) or turn LengthSeed/DetonSeed ON,
    //  raise AssistFreq, slow RiseCadence (bigger = slower). To make it harder, the reverse. The
    //  per-mode counts (Ice tiles, Vault chests/moves, LongWord goal/len, HeroWord count) are the
    //  numeric dials. Past level 15, Get() loops the last Boss profile and escalates counts (see
    //  the comment on Get).
    // ════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The objective mode a level installs. (Chain demoted — not in the rotation.)</summary>
    public enum LevelMode
    {
        LongWord,   // explode N words of minLen+ letters (vocabulary)
        HeroWord,   // escort N drop-targets to the bottom (FLAGSHIP escort-down)
        Vault,      // treasure triage-loot (PURE REWARD — never ends the run; rises OFF, move cap)
        Ice,        // clear-the-ice blocker (thaw N frozen tiles by detonating them in words)
        HiddenWord  // uncover a mystery word — exploded letters that match reveal in place (2026-06-17)
    }

    /// <summary>
    /// The 4 difficulty profiles from the curve spec. Each bakes the seeding toggles + assist freq
    /// + rise cadence default; a LevelEntry may override any dial for a specific level.
    /// A·Breezy: length ON, deton ON, freq 0.5  (onboard / breather — the VALLEY floor)
    /// B·Normal: length ON, deton ON, freq 0.38 (medium)
    /// C·Tricky: length ON, deton OFF, freq 0.38 (peel deton — "plan your explosions")
    /// D·Boss:   length OFF, deton OFF, freq 0.2, faster rise (the PEAK — sparse board)
    /// </summary>
    public enum DifficultyProfile { A_Breezy, B_Normal, C_Tricky, D_Boss }

    /// <summary>
    /// One level's full config: the mode, the difficulty profile, per-mode numeric params, and the
    /// resolved difficulty dials. Dials default from the Profile but a row can override any of them
    /// (that's how the bumpy curve gets its small per-level wobbles without changing profile).
    /// </summary>
    public struct LevelEntry
    {
        public LevelMode         Mode;
        public DifficultyProfile Profile;

        // ── per-mode params (only the ones relevant to Mode are read) ──
        public int LongWordMinLen;   // LongWord: required word length (2 = "any word")
        public int LongWordGoal;     // LongWord: how many to explode
        public int HeroWordCount;    // HeroWord: drop-targets to escort down
        public int IceCount;         // Ice: tiles to freeze/thaw
        public string HiddenWord;    // HiddenWord: the mystery word to uncover (e.g. "STAR")
        public int VaultChests;      // Vault: requested chest count (NOTE: actual board chest tiers
                                     //        are read from the SurvivalManager Inspector tier fields —
                                     //        regular/mid/high — so this is a fallback/hint only. The
                                     //        per-level triage dial that DOES bite is VaultMoves.)
        public int VaultMoves;       // Vault: flat move budget for THIS level (deliberately < chests).
                                     //        Drives SurvivalManager.SetVaultMoveBudgetOverride — the
                                     //        live per-level triage tightness.

        // ── difficulty dials (resolved from Profile, override-able per row) ──
        public bool LengthSeed;      // DroughtAssist.OpportunitySeeding — anti-frustration, peel LAST
        public bool DetonSeed;       // DroughtAssist.DetonationSeeding — the "easy mode", peel FIRST
        public float AssistFreq;     // SURVIVAL_BOARD_ASSIST_BASE override (0.5 high → 0.2 low)
        public int  RiseCadence;     // turns-per-rise (bigger = slower/easier; Vault ignores — rises OFF)

        // ── TUTORIAL / authored level (2026-06-25) ──
        public string[] FixedBoard;  // null = normal procedural/seeded board (every existing level).
                                     // Non-null = an EXACT hand-authored starting board placed verbatim
                                     // (top-down rows; '.'/'_'/' ' = empty; last row sits on the bottom).
                                     // Read in ObjectiveManager.InstallLevel — replaces the random reseed.
        public bool RisesOff;        // tutorial/authored: run with rising rows OFF (board stays put).
        public int  MoveBudget;      // tutorial/authored: move budget for THIS level (0 = default curve).
        public bool Gated;           // tutorial: run the input-gating + hand_point coaching on this level.
        public bool CountConnecting; // tutorial: also count the connecting/trigger word toward the goal,
                                     // so a "2 explosions of 4 words" board reads 4 on the TARGET.

        public string Why;           // one-line rationale (dip/peak/Kishōtenketsu beat) — for logging
    }

    public static class LevelTable
    {
        // Dial defaults per profile (the spec's "4 profiles"). A LevelEntry starts from these.
        private static (bool length, bool deton, float freq, int rise) Dials(DifficultyProfile p)
        {
            switch (p)
            {
                // rise: 2 = gentle (intro levels only). 1 = every-move (the new standard from L3 on).
                // Floor raised 2026-06-15 — run felt "way too easy"; pressure now arrives by L3, not late.
                case DifficultyProfile.A_Breezy: return (true,  true,  0.50f, 2);
                case DifficultyProfile.B_Normal: return (true,  true,  0.30f, 1);
                case DifficultyProfile.C_Tricky: return (true,  false, 0.30f, 1);
                case DifficultyProfile.D_Boss:   return (false, false, 0.18f, 1);
                default:                         return (true,  true,  0.30f, 1);
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════
        //  THE 15-LEVEL RUN. Seeded from the spec's "First ~15 levels" anchor table, mapped to
        //  the 4 real modes (Chain dropped → rotate LongWord / HeroWord / Vault / Ice) and shaped
        //  to the bumpy curve. Profiles trend up; valleys (A/B + reward modes) sit between peaks
        //  (C/D + hard modes). Per-mode Kishōtenketsu arcs noted in each Why.
        //
        //  LONGWORD arc:  L1 Ki (any word, 3×)   → L3 Shō (any, 4×) → L7 Ten (2× 4-letter, deton OFF)
        //                 → L10 Ketsu/BOSS (3× 5-letter, length+deton OFF).
        //  HEROWORD arc:  L2 Ki (drop 2)          → L6 Shō (drop 3, breather) → L12 Ten (drop 4, Tricky).
        //  ICE arc:       L4 Ki (3 ice, Breezy)   → L8 Shō (5 ice, Normal)    → L14 Ten (7 ice, Tricky)
        //                 → L15 reuse as Boss density elsewhere; Ice Ketsu = a deep-run escalation.
        //  VAULT (reward, always a VALLEY — never ends the run): L5 first treat, L11 post-boss treat,
        //                 L13 mid treat. Tighter moves / more chests each time (reward still escalates).
        // ════════════════════════════════════════════════════════════════════════════════
        private static readonly LevelEntry[] ENTRIES = BuildEntries();

        public static int Count => ENTRIES.Length;

        private static LevelEntry[] BuildEntries()
        {
            return new[]
            {
                // ════════════════════════════════════════════════════════════════════════════════
                //  TUTORIAL LEVELS (2026-06-25 Spencer) — inserted at the FRONT; everything below
                //  slides back a number (old L1 → L2, etc.) but is otherwise untouched. These teach
                //  ONE mechanic each on a FIXED, gated board, but run as REAL levels (real TARGET,
                //  real win modal, real advance). More tutorial levels get added here over time.
                // ════════════════════════════════════════════════════════════════════════════════

                // TUTORIAL L1 — teaches PRIME → EXPLODE. Spencer's authored board: two spaced
                //   "CAT-style" instances that don't interfere. The gated 4-drop solution:
                //     C→col3 (CAT charges) · P→col5 (PIT, explodes CAT) ·
                //     L→col0 (LAND charges) · O→col1 (NOT, explodes LAND).
                //   Goal "explode 4 words". (NEXT STEPS, not yet wired: count the connecting words so
                //   the target reads 4, a 30-move budget, rises OFF, and the input gating + hand_point.)
                Entry(LevelMode.LongWord, DifficultyProfile.A_Breezy, longMin:2, longGoal:4,
                    fixedBoard: new[] {
                        "A.....",   // row 3 (top of the cluster)
                        "N.T.SI",   // row 2
                        "DRW.AT",   // row 1
                        "NGZYEX",   // row 0 (bottom)
                    },
                    risesOff:true, moveBudget:30, gated:true, countConnecting:true,
                    why:"TUTORIAL L1 — prime→explode on a fixed gated board (CAT/PIT + LAND/NOT)."),

                // L1 — LongWord Ki. Knife-through-butter hook: explode 3 ANY words, every helper ON. VALLEY (run floor).
                Entry(LevelMode.LongWord, DifficultyProfile.A_Breezy, longMin:2, longGoal:3,
                    why:"L1 VALLEY — LongWord Ki: 3 any-words, all helpers ON. Feel-good fast start."),

                // ⚠️ TEST PLACEMENT (2026-06-17 Spencer) — HiddenWord thin slice, parked at L2 so it's reachable
                //    fast. Common 4-letter word + Breezy board so it completes without seeding-guarantee work.
                //    Move/remove once the mode is validated; instant reveal for now (no fly-up yet).
                Entry(LevelMode.HiddenWord, DifficultyProfile.A_Breezy, hidden:"STAR",
                    why:"L2 TEST — HiddenWord thin slice: uncover STAR. Feel-test the reveal loop."),

                // L2 — HeroWord Ki. Introduce the escort-down flagship gently (drop 2). Still breezy.
                Entry(LevelMode.HeroWord, DifficultyProfile.A_Breezy, hero:2,
                    why:"L2 VALLEY — HeroWord Ki: escort 2 down, breezy. Teach the flagship escort, cold-free."),

                // L3 — LongWord Shō. Same mode, a touch harder (4 any-words), Normal. Small rise.
                Entry(LevelMode.LongWord, DifficultyProfile.B_Normal, longMin:2, longGoal:5,
                    why:"L3 small rise — LongWord Shō: 5 any-words, Normal, rise-every-move starts here. Develop the core verb under the clock."),

                // L4 — Ice Ki. New mechanic, SAFE intro: 3 ice on a generous board (Breezy). Dip vs L3? ~level.
                Entry(LevelMode.Ice, DifficultyProfile.A_Breezy, ice:3,
                    why:"L4 DIP — Ice Ki: 3 ice, generous board. 'Clear blockers by wording through them.'"),

                // L5 — Vault (reward). First treasure treat = a VALLEY after the first cluster. Generous moves.
                Entry(LevelMode.Vault, DifficultyProfile.B_Normal, vChests:5, vMoves:8,
                    why:"L5 VALLEY/REWARD — first Vault triage-loot: 5 chests / 8 moves. Treat + coin faucet, no fail."),

                // L6 — HeroWord Shō. Make them USE the escort, drop 3, Normal. Gentle climb after the treat.
                Entry(LevelMode.HeroWord, DifficultyProfile.B_Normal, hero:3,
                    why:"L6 rise — HeroWord Shō: escort 3, Normal. Develop the escort under mild pressure."),

                // L7 — LongWord Ten. The TWIST + first real squeeze: 2× 4-letter words, deton seeding OFF (Tricky).
                Entry(LevelMode.LongWord, DifficultyProfile.C_Tricky, longMin:4, longGoal:2,
                    why:"L7 PEAK-ish — LongWord Ten: 2× 4-letter, deton OFF. 'Plan your explosions' — first squeeze."),

                // L8 — Ice Shō. More ice (5), Normal — a small dip from L7's deton-off tension (relief), still climbing.
                Entry(LevelMode.Ice, DifficultyProfile.B_Normal, ice:6,
                    why:"L8 DIP — Ice Shō: 6 ice, Normal (helpers back ON). Relief after L7, fuller board."),

                // MID-CURVE HiddenWord (2026-06-19 Spencer): the mode's first REAL outing after the L2
                //    teaching slice — a 5-letter word under Normal so it shows up well before the endless
                //    stretch. Variety beat + a different verb between the Ice relief and the pre-boss rise.
                Entry(LevelMode.HiddenWord, DifficultyProfile.B_Normal, hidden:"CROWN",
                    why:"mid VARIETY — HiddenWord: uncover CROWN (5 letters), Normal. First real hidden-word level."),

                // L9 — HeroWord/LongWord step toward boss: escort under Tricky (deton OFF). Tension creeps.
                Entry(LevelMode.HeroWord, DifficultyProfile.C_Tricky, hero:3,
                    why:"L9 rise — HeroWord under Tricky: escort 3, deton OFF. Tension creeps pre-boss."),

                // L10 — LongWord KETSU / BOSS. The first WALL: 3× 5-letter, length+deton OFF, fast rise, low assist.
                Entry(LevelMode.LongWord, DifficultyProfile.D_Boss, longMin:5, longGoal:3,
                    why:"L10 PEAK/BOSS — LongWord Ketsu: 3× 5-letter, length+deton OFF, fast rise. The real wall."),

                // L11 — Vault (reward). Post-boss treat = the deepest VALLEY of the climb. You just beat the wall.
                Entry(LevelMode.Vault, DifficultyProfile.B_Normal, vChests:5, vMoves:7,
                    why:"L11 VALLEY/REWARD — post-boss Vault: 5 chests / 7 moves (tighter than L5). Relief + reward."),

                // L12 — HeroWord Ten. The escort TWIST: drop 4 under Tricky (deton OFF). Higher than L9's relief.
                Entry(LevelMode.HeroWord, DifficultyProfile.C_Tricky, hero:4,
                    why:"L12 rise — HeroWord Ten: escort 4, deton OFF. Escort twist; valley-after rises above L9."),

                // L13 — Vault (reward), tighter still. A mid-climb breather valley, but reward escalates (4 moves).
                Entry(LevelMode.Vault, DifficultyProfile.C_Tricky, vChests:6, vMoves:7,
                    why:"L13 small VALLEY/REWARD — Vault: 6 chests / 7 moves. Reward escalates (harder triage)."),

                // L14 — Ice Ten. The ice TWIST: 7 ice under Tricky (deton OFF). Dense blockers, board squeeze.
                Entry(LevelMode.Ice, DifficultyProfile.C_Tricky, ice:7,
                    why:"L14 PEAK-ish — Ice Ten: 7 ice, deton OFF. Dense blockers in awkward spots."),

                // L15 — Ice KETSU / BOSS (fast-rise). The run's capstone: 8 ice, length+deton OFF, fast rise.
                Entry(LevelMode.Ice, DifficultyProfile.D_Boss, ice:8,
                    why:"L15 PEAK/BOSS — Ice Ketsu: 8 ice, length+deton OFF, fast rise. Synthesis capstone."),
            };
        }

        // Convenience builder: fills dials from the profile, then applies the per-mode params.
        private static LevelEntry Entry(
            LevelMode mode, DifficultyProfile profile, string why,
            int longMin = 3, int longGoal = 3, int hero = 2, int ice = 4,
            int vChests = 5, int vMoves = 8, string hidden = null, string[] fixedBoard = null,
            bool risesOff = false, int moveBudget = 0, bool gated = false, bool countConnecting = false)
        {
            var d = Dials(profile);
            return new LevelEntry
            {
                Mode    = mode,
                Profile = profile,
                LongWordMinLen = longMin,
                LongWordGoal   = longGoal,
                HeroWordCount  = hero,
                IceCount       = ice,
                VaultChests    = vChests,
                VaultMoves     = vMoves,
                HiddenWord     = hidden,
                LengthSeed  = d.length,
                DetonSeed   = d.deton,
                AssistFreq  = d.freq,
                RiseCadence = d.rise,
                FixedBoard  = fixedBoard,
                RisesOff    = risesOff,
                MoveBudget  = moveBudget,
                Gated           = gated,
                CountConnecting = countConnecting,
                Why = why,
            };
        }

        /// <summary>
        /// Resolve the level entry for a 1-based level index (= SurvivalManager.CurrentStageIndex).
        /// In-table levels (1..Count) return the authored row verbatim. PAST the table (16+) we keep
        /// it SIMPLE per the validation-MVP brief: loop the last BOSS profile and escalate the counts
        /// by how many blocks past the table we are (every 5 levels = one harder block). This is the
        /// stand-in for the real pickLevel(n) generator (deferred); it just keeps a run that overruns
        /// the table from going flat or crashing.
        /// </summary>
        public static LevelEntry Get(int levelIndex)
        {
            if (levelIndex < 1) levelIndex = 1;
            if (levelIndex <= ENTRIES.Length)
                return ENTRIES[levelIndex - 1];

            // ── Past the table (L16+): the procedural generator. Rotate ALL FIVE modes on a fixed
            //    6-level cycle so variety never stops, with counts escalating every block and a Vault
            //    reward valley closing each cycle. Deterministic by levelIndex (same index → same level,
            //    for replay / daily-seed consistency). 2026-06-19 Spencer. (Replaces the old KISS stand-in
            //    that only looped Ice/LongWord — that's why mode variety stopped past L15.)
            int over  = levelIndex - ENTRIES.Length;     // 1-based count past the table
            int idx   = over - 1;                         // 0-based
            int cycle = idx % 7;                          // position in the 7-level cycle
            int block = idx / 7;                          // difficulty block — escalates every full cycle

            // 7-cycle: LongWord → HeroWord → HiddenWord → Ice → HiddenWord → LongWord(peak) → Vault(reward).
            // HiddenWord features TWICE (it's the standout mode) — a shorter one then a longer/harder one.
            // Profiles give the valley→rise→peak rhythm; counts scale with block; reward closes the cycle.
            LevelMode mode; DifficultyProfile prof;
            switch (cycle)
            {
                case 0:  mode = LevelMode.LongWord;   prof = DifficultyProfile.B_Normal; break;
                case 1:  mode = LevelMode.HeroWord;   prof = DifficultyProfile.B_Normal; break;
                case 2:  mode = LevelMode.HiddenWord; prof = DifficultyProfile.B_Normal; break; // shorter hidden word
                case 3:  mode = LevelMode.Ice;        prof = DifficultyProfile.C_Tricky; break;
                case 4:  mode = LevelMode.HiddenWord; prof = DifficultyProfile.C_Tricky; break; // longer hidden word
                case 5:  mode = LevelMode.LongWord;   prof = DifficultyProfile.D_Boss;   break; // peak
                default: mode = LevelMode.Vault;      prof = DifficultyProfile.B_Normal; break; // reward valley
            }

            var e = default(LevelEntry);
            e.Mode = mode;
            e.Profile = prof;
            var d = Dials(prof);
            e.LengthSeed = d.length; e.DetonSeed = d.deton; e.AssistFreq = d.freq; e.RiseCadence = d.rise;

            switch (mode)
            {
                case LevelMode.LongWord:
                    e.LongWordMinLen = (cycle == 4) ? 5 : 4;                 // peak step = 5-letter words
                    e.LongWordGoal   = Mathf.Min(3 + block, 8);
                    break;
                case LevelMode.HeroWord:
                    e.HeroWordCount  = Mathf.Min(3 + block, 6);
                    break;
                case LevelMode.Ice:
                    e.IceCount       = Mathf.Min(6 + block, 12);
                    break;
                case LevelMode.HiddenWord:
                    // Word LENGTH scales the difficulty (more letters to uncover). The first slot (cycle 2)
                    // is shorter, the second (cycle 4) longer; both grow with the block. cap 7.
                    if (cycle == 2) e.HiddenWord = HiddenWordFor(Mathf.Clamp(5 + block, 5, 6), block * 2);
                    else            e.HiddenWord = HiddenWordFor(Mathf.Clamp(6 + block, 6, 7), block * 2 + 1);
                    break;
                case LevelMode.Vault:
                    e.VaultChests    = Mathf.Min(5 + block, 10);
                    e.VaultMoves     = 8;                                    // generous triage budget (reward, no fail)
                    break;
            }
            e.Why = $"L{levelIndex} ENDLESS gen — {mode} ({prof}), block {block}.";
            return e;
        }

        // Theme words for procedurally-generated HiddenWord levels, by LENGTH (longer = harder: more
        // letters to uncover). The target is uncovered by exploding words that CONTAIN its letters (it is
        // never spelled itself) and the safety net rigs any letter that goes unavailable, so these don't
        // need to be dictionary words — they just need common-ish letters to stay completable. Curated to
        // avoid rare letters (Q/X/Z). 2026-06-19 Spencer.
        // Early HiddenWord levels draw from the 4-letter set; later levels step up to 5/6/7.
        // Same curation rule as the others: no rare letters (Q/X/Z). 2026-06-23 Spencer.
        private static readonly string[] HIDDEN_4 =
            { "STAR","MOON","FISH","BIRD","CAKE","KING","GOLD","ROSE","LEAF","RAIN","SNOW","WAVE",
              "FROG","BEAR","LION","DEER","WOLF","DUCK","BOAT","KITE","BELL","RING","CORN","PLUM",
              "MINT","LIME","PEAR","NEST","WING","HIVE","COIN","TREE","POND","ROCK","SAND","SHIP" };
        private static readonly string[] HIDDEN_5 =
            { "CROWN","HEART","LIGHT","STORM","BEACH","PLANT","RIVER","CLOUD","STONE","FLAME","OCEAN","TIGER",
              "BREAD","CHAIR","GRASS","SHINE","EARTH","PEARL","NIGHT","FROST",
              "APPLE","LEMON","BERRY","HONEY","MAPLE","SUGAR","CANDY","MAGIC","COMET","EAGLE","WHALE","PANDA",
              "DAISY","TULIP","CORAL","AMBER","JEWEL","TOWER","MOUSE","HORSE","GOOSE","SNAIL","JELLY","MELON",
              "PEACH","GRAPE" };
        private static readonly string[] HIDDEN_6 =
            { "GARDEN","FLOWER","CASTLE","DRAGON","SILVER","WINTER","ORANGE","FOREST","SUNSET","PALACE","MEADOW",
              "SPRING","BRIDGE","CANDLE","MARBLE","PLANET","ROCKET","GOLDEN",
              "SUMMER","AUTUMN","VALLEY","ISLAND","JUNGLE","DESERT","TURTLE","RABBIT","KITTEN","MONKEY","PARROT",
              "CHERRY","BANANA","CARROT","PEPPER","COOKIE","MUFFIN","SUNDAE","RIBBON","BUBBLE","PURPLE","YELLOW",
              "COPPER","FALCON","KNIGHT","TEMPLE","DONKEY","PEBBLE" };
        private static readonly string[] HIDDEN_7 =
            { "DIAMOND","RAINBOW","THUNDER","CRYSTAL","HARVEST","EMERALD","DOLPHIN","LANTERN","COMPASS","PYRAMID",
              "CAPTAIN","MONARCH" };

        private static string[] PoolFor(int len) =>
            len <= 4 ? HIDDEN_4 : len == 5 ? HIDDEN_5 : len == 6 ? HIDDEN_6 : HIDDEN_7;

        // Deterministic pick of a word of the requested length. seed varies it per level
        // (used by the past-L15 escalation, where determinism-by-level is intended).
        private static string HiddenWordFor(int len, int seed)
        {
            string[] pool = PoolFor(len);
            return pool[((seed % pool.Length) + pool.Length) % pool.Length];
        }

        // Remembers the last word served per length so two runs in a row don't repeat it.
        private static readonly System.Collections.Generic.Dictionary<int, string> _lastHiddenByLen
            = new System.Collections.Generic.Dictionary<int, string>();

        /// <summary>Random word of the given length, avoiding an immediate repeat. Run-random so the
        /// mystery word differs every run. (When the daily/run-seed generator lands, route the daily
        /// through a seeded pick instead, so all players share that day's word.)</summary>
        public static string RandomHiddenWord(int len)
        {
            string[] pool = PoolFor(len);
            if (pool == null || pool.Length == 0) return null;
            string pick = pool[UnityEngine.Random.Range(0, pool.Length)];
            if (pool.Length > 1 && _lastHiddenByLen.TryGetValue(len, out var last) && pick == last)
                pick = pool[(System.Array.IndexOf(pool, pick) + 1) % pool.Length]; // bump off a repeat
            _lastHiddenByLen[len] = pick;
            return pick;
        }

        /// <summary>Build the live Objective instance for an entry (the one-line SetObjective install).</summary>
        public static Objective MakeObjective(in LevelEntry e)
        {
            switch (e.Mode)
            {
                case LevelMode.LongWord: return new LongWordObjective(e.LongWordMinLen, e.LongWordGoal);
                case LevelMode.HeroWord: return new HeroWordObjective(e.HeroWordCount);
                case LevelMode.Vault:    return new VaultObjective(e.VaultChests);
                case LevelMode.Ice:      return new IceObjective(e.IceCount);
                case LevelMode.HiddenWord:
                    // Randomize the mystery word per run so it's not the same one every time.
                    // Length comes from the authored entry, preserving the designed curve
                    // (4-letter early → 5/6 later). Falls back to the authored word if needed.
                    int len = string.IsNullOrEmpty(e.HiddenWord) ? 4 : e.HiddenWord.Length;
                    return new HiddenWordObjective(RandomHiddenWord(len) ?? e.HiddenWord);
                default:                 return new LongWordObjective(2, 3);
            }
        }
    }
}
