using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Authoritative board state owner for the Scrabble-drop game.
    /// Maintains a 7×6 cell array INDEPENDENT of GridManager's visual layer.
    ///
    /// Job 2: FindNewWords — horizontal + vertical word detection (no diagonals)
    /// Job 4: CalculateWordScore, ProcessDrop, ExplodePrimedWord, gravity, chain loop
    /// Job 9: Enhanced SimulateDrop — non-mutating evaluation for AI
    ///
    /// Event flow:
    ///   ProcessDrop() emits events via C# delegates:
    ///     OnTileDropped, OnWordScored, OnWordPrimed, OnPrimedTriggered,
    ///     OnTilesExploded, OnGravityCollapse, OnChainStep, OnResolutionComplete
    /// </summary>
    public class RulesEngine : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        // Board dimensions — COLS is fixed, ROWS is mode-dependent.
        // GridManager mirrors ROWS via RulesEngine.ROWS; keep in sync.
        // Classic/Blitz = 5 rows (legacy). Survival = 8 rows. Level = 8 rows
        // (Path B upgrade 2026-04-22: raised from 5 to match Survival's proven
        // cascade grid — cascades require vertical room above detonation
        // clusters for gravity-fed secondary triggers).
        //
        // LEVEL_ROWS is a compile-time constant so LevelValidator can reference
        // it directly without depending on the mode-dependent ROWS property.
        // The validator runs BEFORE GameMode.CurrentMode is set to Level
        // (LevelDebugMenu/MenuUI/LevelCompletedModal all Validate → then set
        // mode → then TransitionTo(Playing)), so at validation time the
        // dynamic ROWS property would still return 5. Using LEVEL_ROWS keeps
        // the validator consistent with Level's actual runtime row count.
        // 2026-09-02 swap-pivot grid. Was 6x8. Chosen over 11x9: 9x12 gives MORE
        // cells (108 vs 99), BIGGER tiles (122px vs 100px on a 1179-wide phone),
        // and 12 rows of build room instead of 9. 11x9 used only 58% of the
        // screen height — wasted space in a game about building before you fire.
        public const int COLS       = 9;
        public const int MAX_ROWS   = 11;
        public const int LEVEL_ROWS = 11;
        public static int ROWS => (SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode) ? LEVEL_ROWS : 5;

        /// <summary>
        /// Kill switch for adjacency-based detonation triggering.
        /// When true: a new word touching a primed word (orthogonal adjacency) triggers it.
        /// When false: only direct cell overlap triggers (original behavior).
        /// </summary>
        public static bool AdjacencyTriggerEnabled = false;

        /// <summary>
        /// Kill switch for junk splash damage on detonation.
        /// When true: detonations clear 1-2 nearby non-special tiles as collateral.
        /// Prevents junk towers from accumulating on the sides.
        /// </summary>
        public static bool JunkSplashEnabled = true;

        /// <summary>OFF for the armed-web game (2026-09-10 Spencer).
        ///
        /// The big-moment SPLASH: when a detonation was large enough (chain depth >= 2, or a
        /// primed word of 6+, or 2+ primed clusters) it cleared the entire ROW or COLUMN of the
        /// word that went off — and any primed word caught in that sweep detonated too, as
        /// "chain continuation". It was built for the drop game, where cascades were meant to
        /// propagate on their own.
        ///
        /// In the swap game the player pulls every trigger: a cluster you never chose, on the
        /// far side of the board, must not go off because something else was big. Spencer asked
        /// for this originally and called it "just not sensible in this game" once the core verb
        /// changed. Flip to true to restore the row/column sweep and its collateral scoring.</summary>
        public static bool BigMomentSplashEnabled = false;

        // True while the CURRENT resolution was initiated by a SWAP/EDIT (BeginSwapResolution); a DROP
        // (FullTurnSequence) sets it false. Lets swap-tutorial objectives count ONLY swap-caused explosions.
        // 2026-07-07 Spencer.
        public static bool LastResolutionWasSwap = false;

        /// <summary>
        /// Kill switch for post-gravity fertility repair.
        /// When true: after gravity, if the board has too few near-words,
        /// silently nudge 1-2 tiles to create opportunities.
        /// </summary>
        public static bool PostGravityFertilityEnabled = true;

        // 2026-09-09 Spencer: 3 -> 4, as an experiment. With 99 cells, unlimited swaps and 735
        // three-letter words, a valid word was ALWAYS available — the board never posed a
        // question, and turning the letter assist off changed nothing because scarcity was
        // never the constraint. There are MORE four-letter words (2,098) than three-letter
        // ones, so this does not shrink the vocabulary; it tightens the GEOMETRY, because
        // four tiles in a row is far harder to arrange than three.
        // Set back to 3 to revert.
        /// <summary>Shortest word the board will DETECT. Was a const 3; made settable
        /// 2026-10-19 so a level can demand 4+ letters. That single change makes the same board
        /// read completely differently — all the three-letter filler stops counting and the
        /// player has to actually look. One of the cheapest level modifiers available.</summary>
        public static int MIN_WORD_LENGTH = 3;
        private const int MAX_WORD_LENGTH   = 9;   // 2026-10-21: was 7 — CREATION (8) could never count. 9 = board width.

        /// <summary>
        /// Phase 11+ rare-letter premium set. Any word in Survival containing
        /// at least one of these letters gets a flat ×2 multiplier on top of
        /// the length-tier mult. Kept tight — these are the four letters
        /// every Scrabble player instinctively prices as "hard."
        /// </summary>
        private static readonly char[] RARE_LETTERS = { 'Q', 'Z', 'X', 'J' };
        private const int MAX_CHAIN_DEPTH   = 12;
        private const int CHAIN_BONUS       = 3;

        // Primed words expire after this many turns if not detonated.
        // 3 turns: balanced — enough time to set up detonations without board clutter.
        // 2 turns: too tight — words often expire before opponent can interact with them.
        // 4 turns = player primes on their turn, survives AI turn, survives player's
        // next turn, detonatable on the turn AFTER that. Generous enough to actually use.
        // Classic: flat 4-turn expiry. Survival: word length (3-6 placements).
        private const int PRIMED_EXPIRY_TURNS = 4;
        private const int SURVIVAL_FUSE_CAP   = 6;

        /// <summary>Returns the fuse length for a primed word. Survival: word length capped at 6. Classic: flat 4.</summary>
        // Tutorial levels disable the fuse so primed words never fizzle on a learner (the fuse is its
        // own mechanic, taught later). Set per-level in ObjectiveManager.InstallLevel. 2026-06-25 Spencer.
        public static bool FuseDisabled = false;
        public static bool StonesDisabled = false;

        /// <summary>Points awarded for THAWING one ice tile. 2026-10-19 Spencer.
        ///
        /// Ice used to pay nothing — it was pure friction, which is why "what is ice even for"
        /// was a fair question in a score-only game. With a bounty it does two jobs at once:
        /// it is still an obstacle (a frozen tile cannot be swapped, so it shrinks the board you
        /// can work with) AND it is treasure sitting in your way. That turns it from something
        /// you resent into something you aim at.
        ///
        /// ~2x a good word (measured: a good swapped word pays about 46), so detouring through
        /// ice is worth real moves. The LEVEL is then designed purely by what fraction of its
        /// score target is locked inside blockers — no objective text, no completion check.</summary>
        public static int ICE_BOUNTY = 100; // per-level: suppress rising-row stone/rock tiles (L9 intro — rocks introduced later). 2026-07-08 Spencer.

        /// <summary>MASTER rock switch — 2026-09-22 Spencer: "remove all the rock blockers for
        /// now" while the board art is being reworked. Separate from StonesDisabled because that
        /// one is PER-LEVEL and gets rewritten from the level table on every level start
        /// (ObjectiveManager, e.StonesOff) — setting it here would last until the next level and
        /// then silently come back. This one nothing else writes. Set false to restore rocks.</summary>
        public static bool ROCKS_OFF = true;

        private static int GetFuseLength(int wordLength)
        {
            if (FuseDisabled) return 9999;   // effectively infinite — primed words don't fizzle this level
            if (SurvivalManager.IsSurvivalMode)
                return Mathf.Min(wordLength, SURVIVAL_FUSE_CAP);
            return PRIMED_EXPIRY_TURNS;
        }

        // ── Detonation scoring tuning ───────────────────────────────────────────
        // Controls how much score a detonation awards.
        //   DETONATION_SCORE_MULTIPLIER: fraction of primed word's stored score
        //     (0 = ignore stored score, 0.5 = half, 1.0 = full double-word)
        //   BREAKER_BONUS: flat points per detonation regardless of word value
        //
        // Formula: bonus = RoundToInt(pw.Score * MULTIPLIER) + BREAKER_BONUS
        //
        // Tuning presets:
        //   Flat only:    MULTIPLIER=0,   BONUS=2  (current — simple, predictable)
        //   Scaled:       MULTIPLIER=0.5, BONUS=1  (rewards big-word primes)
        //   Full double:  MULTIPLIER=1.0, BONUS=0  (maximum detonation reward)
        //   Hybrid:       MULTIPLIER=0.5, BONUS=2  (scaled + flat floor)
        // 2026-10-20 Spencer: 1.5 -> 2.0, paired with REMOVING the form-time base payment
        // in HandManager (see the note at the old swapBaseScore site). Points are now earned
        // ONLY by detonating.
        //
        // The multiplier is set so a single word armed and fired on the spot pays exactly what
        // it used to, i.e. this is a redistribution, not a nerf:
        //     before = Score + (1.5*Score + 2) * 2.0   = 4.0*Score + 4
        //     after  =         (M  *Score + 2) * 2.0   = 2M *Score + 4   ->  M = 2.0
        // (the 2.0 is TriangularChainMultiplier(1) — detonation sites evaluate at depth+1,
        //  so even the opening boom is already 2x.)
        //
        // Multi-word webs come out AHEAD of the old numbers, because the part that used to be
        // paid flat at form time now rides the triangular chain multiplier with everything
        // else. That widens the gap between building a web and spamming three-letter words,
        // which is the behaviour the change exists to reward.
        public const float DETONATION_SCORE_MULTIPLIER = 2.0f; // 2x primed word's score on detonation
        // 2026-09-09 Spencer: 10 -> 2. "there are so many words in our dictionary that you can
        // basically always find a word" — and the flat bonuses meant it barely mattered WHICH
        // one. A flat +10 (plus +10 heat) dominates a 3-letter word's ~6 points, compressing a
        // 6-letter word from 8.4x a 3-letter down to 3.8x. So short-word spam was nearly as
        // efficient as playing well. Cutting the floor makes word QUALITY the thing that pays,
        // without making words hard to FIND — raising MIN_WORD_LENGTH to 4 did that and was
        // "extremely hard mode". Never stuck, but rewarded for doing better.
        public const int   BREAKER_BONUS               = 2;

        // Chain-Depth Scaling: TRIANGULAR curve — each depth feels dramatically bigger.
        // Uses triangular formula: multiplier = 1 + (depth² + depth) / 2
        // depth 0: 1.0x, depth 1: 2.0x, depth 2: 4.0x, depth 3: 7.0x, depth 4+: capped at 7.0x
        // Detonation sites evaluate at (_stepChainDepth + 1) so the OPENING boom is 2x,
        // not a flat 1x — fixes the "first detonation feels identical to a safe word" gap.
        public const float CHAIN_DEPTH_SCALE_PER_DEPTH = 0.5f; // unused — kept for compat, see TriangularChainMultiplier
        /// <summary>2026-09-03 ARMED-WEB MODE. Words that form stay PRIMED on the board
        /// instead of auto-detonating: crossing an armed word LINKS it into the web
        /// (adds to the connected group) but does NOT fire it, the fuse never expires,
        /// and the player TAPS a primed word to detonate it plus everything connected —
        /// through the same staggered chain payoff the auto-fuse used.
        /// Flip false to get the old auto-fuse game back in the same build.</summary>
        public static bool ArmedWebMode = true;

        // ── BRANCH PRIMING (2026-09-09 Spencer) ─────────────────────────────────────
        // "the first word you prime can be from anywhere, but to make a bigger combo you
        // need to branch out from that word."
        // The first word is free — it plants your flag. After that, a word can only arm if
        // it TOUCHES the existing web (shares a cell or sits orthogonally adjacent), so the
        // seed word becomes a real commitment and the board's geography starts to matter.
        // Cascade-formed words are exempt and become new branch points — playing well buys
        // back freedom, which is a better reward than points.
        // Gate lives in PrimedWordRegistry.AddPrimedWord: ONE chokepoint, so all five
        // priming call sites are covered without five copies of the rule.
        // 2026-09-09 Spencer: OFF. The rule and the cluster display are two solutions to the
        // SAME problem — nobody could tell that connecting words was worth anything. The rule
        // solved it by FORCING the behaviour; the outline + payout badge solves it by SHOWING
        // the reward. If showing works you do not want the rule, because a restriction you do
        // not need is just friction. Left in place, flag-gated, in case the display alone
        // proves too weak an incentive.
        public static bool BranchPriming = false;

        /// <summary>True while a resolution cascade is running — gravity-formed words are
        /// exempt from the branch rule.</summary>
        public bool InCascade => _stepChainDepth > 0;

        /// <summary>Drop any primed word whose letters no longer sit where it thinks they do.
        ///
        /// A primed word is a word ON THE BOARD. Gravity can pull its letters apart — columns
        /// fall different distances — and nothing re-checked that afterwards, so a word could
        /// stay armed (and detonatable, and counted in a cluster payout) when it no longer
        /// existed. Spencer flagged it: stray letters that fall out of a word should fall out
        /// of being primed too. 2026-09-09.</summary>
        /// <summary>Every primed word, for BoardInvariants' self-checks. 2026-09-10 Spencer.</summary>
        public System.Collections.Generic.List<PrimedWordRegistry.PrimedWord> DebugAllPrimedWords()
            => _primedRegistry != null ? _primedRegistry.GetAllPrimedWords()
                                       : new System.Collections.Generic.List<PrimedWordRegistry.PrimedWord>();

        public int ValidatePrimedWords()
        {
            if (_primedRegistry == null) return 0;
            var all = _primedRegistry.GetAllPrimedWords();
            int dropped = 0;
            for (int i = 0; i < all.Count; i++)
            {
                var pw = all[i];
                if (pw.Cells == null || string.IsNullOrEmpty(pw.Word)) continue;
                bool ok = pw.Cells.Count == pw.Word.Length;
                for (int c = 0; ok && c < pw.Cells.Count; c++)
                {
                    var cell = pw.Cells[c];
                    if (!InBounds(cell.x, cell.y)) { ok = false; break; }
                    var data = _board[cell.x, cell.y];
                    // A wild resolves to a real letter, so compare against the CELL, and treat a
                    // missing tile as a broken word rather than assuming it is still there.
                    if (data == null || char.ToUpperInvariant(data.Letter) != char.ToUpperInvariant(pw.Word[c]))
                        ok = false;
                }
                // STILL A STRAIGHT, UNBROKEN RUN? The letter check above passes even when the
                // word has been torn apart, because UpdateCellPositions faithfully drags each
                // cell along with its tile — so a word whose letters scattered across the board
                // kept every letter in the right ORDER and stayed primed. That is what left
                // fragments like the "AN" and "GI" of a broken BAG outlined, badged and still
                // detonating. A word is only a word while its letters are adjacent, in a line.
                // 2026-09-10 Spencer.
                if (ok && pw.Cells.Count > 1)
                {
                    // ONE CONSTANT STEP of at most 1 in each axis, repeated. This covers
                    // horizontal, vertical AND diagonal in a single test.
                    //
                    // 2026-10-19: this used to require sameCol || sameRow, which silently
                    // unprimed every DIAGONAL word the moment anything validated — and a rise
                    // validates. Swipe-to-claim allows all 8 directions, so a diagonal claim
                    // armed, glowed, then vanished on the next rise. The auto-scanner only ever
                    // produced straight runs, so the old test was correct until swipes existed.
                    int sx = pw.Cells[1].x - pw.Cells[0].x;
                    int sy = pw.Cells[1].y - pw.Cells[0].y;
                    if (Mathf.Abs(sx) > 1 || Mathf.Abs(sy) > 1 || (sx == 0 && sy == 0))
                        ok = false;
                    else
                        for (int c = 1; ok && c < pw.Cells.Count; c++)
                            if (pw.Cells[c].x - pw.Cells[c - 1].x != sx
                             || pw.Cells[c].y - pw.Cells[c - 1].y != sy) ok = false;
                }

                if (!ok)
                {
                    WDLog.Log($"[PrimeValidate] '{pw.Word}' no longer a straight unbroken run — unprimed");
                    var deadCells = pw.Cells != null ? new List<Vector2Int>(pw.Cells) : null;
                    _primedRegistry.RemovePrimedWord(pw.Id);
                    dropped++;

                    // TELL THE TILES. Dropping the word from the registry killed its outline and
                    // its payout, but the tiles kept wearing the primed rose — letters that had
                    // fallen out of a word still looked armed. Skip any cell another primed word
                    // still covers: a tile can belong to two crossing words, and only one of them
                    // died. 2026-09-10 Spencer.
                    if (deadCells != null && GridManager.Instance != null)
                    {
                        for (int d = 0; d < deadCells.Count; d++)
                        {
                            var dc = deadCells[d];
                            var still = _primedRegistry.GetPrimedWordsContaining(dc);
                            if (still != null && still.Count > 0) continue;
                            var t = GridManager.Instance.GetTile(dc.x, dc.y);
                            if (t != null) t.ClearPrimedGlow();
                        }
                    }
                }
            }
            return dropped;
        }

        /// <summary>What a group of connected primed words would pay if tapped RIGHT NOW.
        /// Mirrors the DoExplode maths — same multiplier, same flat bonus, same triangular
        /// chain scaling — so the number on the board is the number the player receives.
        /// If these ever drift the display becomes a lie, which is worse than no display.
        /// 2026-09-09 Spencer.</summary>
        public int PreviewGroupPayout(System.Collections.Generic.List<PrimedWordRegistry.PrimedWord> group)
        {
            if (group == null || group.Count == 0) return 0;
            int total = 0;
            for (int i = 0; i < group.Count; i++)
            {
                var pw = group[i];
                int survived  = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                int heatBonus = Mathf.Min(survived * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                int raw = Mathf.RoundToInt(pw.Score * RunBuffs.DetonationMultiplier(DETONATION_SCORE_MULTIPLIER))
                        + RunBuffs.BreakerBonus(BREAKER_BONUS) + heatBonus;
                float chainMult = TriangularChainMultiplier(i + 1);
                float goldMult  = pw.IsGold ? RunBuffs.GoldMultiplier(2f) : 1f;
                total += Mathf.RoundToInt(raw * chainMult * goldMult);
            }
            return total;
        }

        /// <summary>Every connected cluster of primed words on the board, each with the cells
        /// it covers and what it is currently worth. Drives the on-board outline + badge.</summary>
        public System.Collections.Generic.List<(System.Collections.Generic.List<Vector2Int> cells, int payout, int wordCount)> GetPrimedClusters()
        {
            var outList = new System.Collections.Generic.List<(System.Collections.Generic.List<Vector2Int>, int, int)>();
            if (_primedRegistry == null) return outList;
            var all = _primedRegistry.GetAllPrimedWords();
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < all.Count; i++)
            {
                if (seen.Contains(all[i].Id)) continue;
                var seeds = new System.Collections.Generic.HashSet<int> { all[i].Id };
                var group = _primedRegistry.FindConnectedGroup(seeds, null);
                if (group == null || group.Count == 0) group = new System.Collections.Generic.List<PrimedWordRegistry.PrimedWord> { all[i] };
                var cells = new System.Collections.Generic.List<Vector2Int>();
                for (int g = 0; g < group.Count; g++)
                {
                    seen.Add(group[g].Id);
                    var gc = group[g].Cells;
                    if (gc == null) continue;
                    for (int c = 0; c < gc.Count; c++) if (!cells.Contains(gc[c])) cells.Add(gc[c]);
                }
                if (cells.Count > 0) outList.Add((cells, PreviewGroupPayout(group), group.Count));
            }
            return outList;
        }

        /// <summary>Will a word on these cells actually ARM? Asks the SAME rule the priming
        /// gate uses, so the visuals can never disagree with the rules. Needed because the
        /// scored flash runs BEFORE priming and used to assume scored implies armed — true
        /// before branch priming, false now. 2026-09-09 Spencer.</summary>
        public bool WillPrime(System.Collections.Generic.List<Vector2Int> cells)
        {
            if (!BranchPriming) return true;
            if (_primedRegistry == null || _primedRegistry.Count == 0) return true;  // seed word
            if (InCascade) return true;                                              // cascades exempt
            return _primedRegistry.TouchesExistingWeb(cells);
        }

        /// <summary>Chance a rising row gets a HIGH-VALUE letter deliberately seeded into
        /// it. Without this the best-of-N row picker filters them out entirely — see the
        /// note in FillBottomRow. 0 = off.</summary>
        public static float HIGH_VALUE_ROW_CHANCE = 0.35f;
        private static readonly char[] HIGH_VALUE_LETTERS = { 'V', 'K', 'J', 'X', 'Q', 'Z' };

        public const int   CHAIN_DEPTH_SCALE_CAP       = 3;   // max chain depth for triangular scaling (tried 8 on 2026-04-18 + cluster boost in DoExplode — combined effect made every move a board-clearing mega-combo. Reverted to 3.)

        // Heat Fuse: primed words gain +N detonation bonus per survived turn, capped.
        // 2 pts/turn capped at 10 → 5 turns to max, matches Survival word-length fuse cap.
        // Patience pays — rewards defending primed words across turns.
        public const int   HEAT_FUSE_PER_TURN          = 2;
        // Zeroed with the same reasoning. This rewarded HOLDING a word as its fuse burned
        // down — but the fuse is off in the swap game, so survivedTurns just climbs and every
        // word collects the full +10. A flat bonus for everyone is not a reward, it is noise
        // that flattens the difference between a good word and a bad one. 2026-09-09.
        public const int   HEAT_FUSE_MAX_BONUS         = 0;

        // Overlap Fuse Extension: existing primed words get +1 fuse when a new prime overlaps them
        public const int   OVERLAP_FUSE_EXTENSION      = 1;
        public const int   MAX_OVERLAP_FUSE_BONUS      = 2;
        private const int  MAX_GLOBAL_FUSE_RESETS      = 2;

        /// <summary>
        /// Triangular chain multiplier: each depth level adds MORE than the last.
        /// Formula: 1 + (d² + d) / 2 where d = min(chainDepth, cap)
        /// d=0: 1.0x, d=1: 2.0x, d=2: 4.0x, d=3: 7.0x, d=4: 11.0x
        /// Much punchier than the old linear 1.0/1.5/2.0/2.5 — chains should feel explosive.
        /// </summary>
        /// <summary>Ceiling on the per-word chain multiplier in instant-pop mode. See TriangularChainMultiplier.</summary>
        public static float INSTANT_POP_CHAIN_MAX = 3f;

        public static float TriangularChainMultiplier(int chainDepth)
        {
            // DEEP CHAIN buff extends how far the triangular multiplier keeps climbing.
            int d = Mathf.Min(Mathf.Max(chainDepth, 0), RunBuffs.ChainDepthCap(CHAIN_DEPTH_SCALE_CAP));
            // INSTANT POP: a gentle climb (x2, x2.5, then x3 max) instead of the triangular x2, x4, x7.
            // The triangle was built for a web the player BUILT on purpose. With instant pop, falling
            // letters on a dense board form a dozen crossing words by accident — measured 2026-10-20:
            // ONE swap on LEVEL 5 made a 12-word web, ten words paid x7, 4,644 points, level won in one
            // move (target 2,100). The first word still pays x2, so a single word is worth exactly what
            // it was and the level targets keep their meaning; luck just stops winning levels.
            if (HandManager.InstantPopMode)
                return d <= 0 ? 1f : Mathf.Min(2f + 0.5f * (d - 1), INSTANT_POP_CHAIN_MAX);
            return 1f + (d * d + d) / 2f;
        }

        // FOUR directions — both ways along each axis. 2026-10-19 Spencer: "it should be
        // counting words backwards too."
        //
        // This was TWO for a long time, on the reasoning that "4 directions creates too many
        // false matches". MEASURED before changing it: that worry came from scanning the WHOLE
        // board, where backwards does roughly double the accidental words (21 -> 42). But the
        // game never scans the whole board per move — it scans the SEED CELLS a swap touched,
        // and bottom-row words are pre-registered by RegisterWordsAtRow so they never auto-prime.
        // Measured at the seed cells, the real cost is 1.06 -> 2.08 words per swap: ONE extra
        // word, and a word the player genuinely made. Before this, swapping to build a word
        // backwards paid nothing at all.
        //
        // The scanners were already written for this — see the dedupe below, whose comment
        // predates the change: "prevents same word being found in both directions".
        private static readonly int[][] _directions = new int[][]
        {
            new int[] {  1,  0 },  // horizontal: left → right
            new int[] {  0, -1 },  // vertical:   top → bottom
            new int[] { -1,  0 },  // horizontal: right → left   (backwards)
            new int[] {  0,  1 },  // vertical:   bottom → top   (backwards)
        };

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static RulesEngine Instance { get; private set; }

        // ── Board state ───────────────────────────────────────────────────────────

        private RulesCellData[,] _board = new RulesCellData[COLS, MAX_ROWS];

        // ── Board Blessing: bonus cells that double word scores ──────────────────

        // 2026-06-23 Spencer: 2× (gold) tiles temporarily REMOVED from play until a clear
        // purpose is defined. Master kill-switch — flip to true to re-enable everywhere
        // (initial placement, rising-row drops, and survival auto-drops all gate on this).
        public static bool GoldTilesEnabled = false;

        private bool[,] _bonusCells = new bool[COLS, MAX_ROWS];

        // ── Cyan tiles: edit-refund on detonation (Survival mode) ────────────────

        private bool[,] _cyanCells = new bool[COLS, MAX_ROWS];

        public bool IsCyanCell(int col, int row)
        {
            if (!InBounds(col, row)) return false;
            return _cyanCells[col, row];
        }

        public void SetCyanCell(int col, int row, bool val)
        {
            if (InBounds(col, row)) _cyanCells[col, row] = val;
        }

        public void ClearAllCyanCells()
        {
            System.Array.Clear(_cyanCells, 0, _cyanCells.Length);
        }

        /// <summary>Check if a cell is a bonus cell.</summary>
        public bool IsBonusCell(int col, int row)
        {
            if (!InBounds(col, row)) return false;
            return _bonusCells[col, row];
        }

        /// <summary>Set a cell as a bonus cell. Refuses wild cells — wilds never carry gold.</summary>
        public void SetBonusCell(int col, int row, bool isBonus)
        {
            if (!InBounds(col, row)) return;
            if (isBonus && _board[col, row] != null && _board[col, row].IsWild)
            {
//                 WDLog.Log($"[RulesEngine] SetBonusCell refused: ({col},{row}) is a wild tile — no gold on wilds.");
                return;
            }
            _bonusCells[col, row] = isBonus;
        }

        /// <summary>Clear all bonus cells (called on match start).</summary>
        public void ClearAllBonusCells()
        {
            System.Array.Clear(_bonusCells, 0, _bonusCells.Length);
        }

        /// <summary>Shift bonus cells up by one row (called during rising rows).</summary>
        public void ShiftBonusCellsUp()
        {
            for (int col = 0; col < COLS; col++)
            {
                for (int row = ROWS - 1; row > 0; row--)
                    _bonusCells[col, row] = _bonusCells[col, row - 1];
                _bonusCells[col, 0] = false; // bottom row cleared for new bonus placement
            }
        }

        /// <summary>
        /// Place 1-2 random bonus cells scattered across the board at game start.
        /// Enforces minimum 2-cell spacing to prevent clumping.
        /// Returns the positions for visual overlay.
        /// </summary>
        public List<Vector2Int> PlaceInitialBonusCells()
        {
            var positions = new List<Vector2Int>();
            if (!GoldTilesEnabled) return positions; // 2x tiles removed from play (2026-06-23)
            // MVP P3.5: SurvivalRng for gameplay-affecting random (Daily Seeded Survival).
            int count = SurvivalRng.Range(1, 3); // 1 or 2 starting bonus cells (rarer = more exciting)
            var available = new List<Vector2Int>();

            // Scatter across the whole board — any cell can be gold
            // Tiles landing on these positions later will become gold via CreateSingleTile
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    available.Add(new Vector2Int(col, row));

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                int idx = SurvivalRng.Range(0, available.Count);
                var pos = available[idx];
                available.RemoveAt(idx);
                _bonusCells[pos.x, pos.y] = true;
                positions.Add(pos);

                // Remove nearby cells to prevent clumping (min 2 cells apart)
                available.RemoveAll(p =>
                    Mathf.Abs(p.x - pos.x) <= 2 && Mathf.Abs(p.y - pos.y) <= 2);
            }

//             WDLog.Log($"[RulesEngine] Initial Board Blessing: placed {positions.Count} bonus cell(s)");
            return positions;
        }

        /// <summary>
        /// Place 0-1 random bonus cells in the bottom row after a rising row.
        /// 40% chance per rising row — gold should feel like a surprise, not routine.
        /// Returns the columns that got bonus cells for visual overlay.
        /// </summary>
        public List<int> PlaceBonusCellsOnBottomRow()
        {
            var bonusCols = new List<int>();
            if (!GoldTilesEnabled) return bonusCols; // 2x tiles removed from play (2026-06-23)

            // MVP P3.5: SurvivalRng for gameplay-affecting random (Daily Seeded Survival).
            // 40% chance to place a gold tile on a rising row (was 100% with 1-2 tiles)
            if (SurvivalRng.Value > 0.40f)
            {
//                 WDLog.Log("[RulesEngine] Board Blessing: no bonus cell this rising row (60% skip chance).");
                return bonusCols;
            }

            int count = 1; // at most 1 per rising row
            var available = new List<int>();
            for (int col = 0; col < COLS; col++)
            {
                // Don't place gold adjacent to existing gold in row 1 (the row that just shifted up)
                bool adjacentGold = false;
                if (col > 0 && _bonusCells[col - 1, 1]) adjacentGold = true;
                if (col < COLS - 1 && _bonusCells[col + 1, 1]) adjacentGold = true;
                if (_bonusCells[col, 1]) adjacentGold = true; // directly above
                // Never place gold on stone tiles
                bool isStone = _board[col, 0] != null && _board[col, 0].IsStone;
                if (!adjacentGold && !isStone) available.Add(col);
            }

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                int idx = SurvivalRng.Range(0, available.Count);
                int col = available[idx];
                available.RemoveAt(idx);
                _bonusCells[col, 0] = true;
                bonusCols.Add(col);
            }

//             WDLog.Log($"[RulesEngine] Board Blessing: placed {bonusCols.Count} bonus cell(s) on bottom row at col(s): {string.Join(",", bonusCols)}");
            return bonusCols;
        }

        /// <summary>
        /// Check if any tile in a word match is a gold bonus tile.
        /// Returns the bonus multiplier (1 = no bonus, 2 = has gold tile).
        /// Consumes the gold status on used tiles (one-time use).
        /// </summary>
        /// <summary>Check if any tile in the word is gold. Does NOT consume.</summary>
        /// <summary>Glow colour for a primed tile at this cell — GOLD if the primed word
        /// covering it contains a high-value letter, else magenta. One chokepoint so the
        /// several hardcoded PRIMED_GLOW call sites can stop guessing. 2026-09-03.</summary>
        /// <summary>Has the primed word containing this cell reached the registry yet?
        /// While FALSE the gold/not-gold answer for the cell is a GUESS from its own letter —
        /// which is what made a gold word paint green for a frame and then flip. 2026-09-21.</summary>
        public static bool PrimedWordRegisteredForCell(Vector2Int cell)
        {
            var inst = Instance;
            if (inst == null || inst._primedRegistry == null) return false;
            var words = inst._primedRegistry.GetPrimedWordsContaining(cell);
            return words != null && words.Count > 0;
        }

        public static Color PrimedGlowForCell(Vector2Int cell)
        {
            var inst = Instance;
            if (inst != null && inst._primedRegistry != null)
            {
                var words = inst._primedRegistry.GetPrimedWordsContaining(cell);
                if (words != null)
                    for (int i = 0; i < words.Count; i++)
                        if (words[i].IsGold || words[i].HasHighValueLetter)
                            return Tile.PRIMED_GOLD_GLOW;
            }
            // Not in the registry yet (visuals can run before the word is registered):
            // fall back to the tile's own letter.
            var t = GridManager.Instance != null ? GridManager.Instance.GetTile(cell.x, cell.y) : null;
            if (t != null && !t.IsWild && t.IsGoldLetter)
                return Tile.PRIMED_GOLD_GLOW;
            return Tile.PRIMED_GLOW;
        }

        /// <summary>Does this word contain a HIGH-VALUE letter (4+ points)? Drives the
        /// GOLD primed colour + gold fly-up. Visual only — no score multiplier.</summary>
        public bool HasHighValueLetter(RulesWordMatch match)
        {
            if (match.Cells == null) return false;
            var dbg = new System.Text.StringBuilder();
            bool hit = false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell == null) { dbg.Append("[null]"); continue; }
                if (cell.IsWild)  { dbg.Append("[wild]"); continue; }
                int pts = LetterData.GetPoints(cell.Letter);
                dbg.Append($"{cell.Letter}={pts} ");
                if (pts >= Tile.HIGH_VALUE_POINTS || cell.IsRainGold) hit = true;   // shower gold reads gold too
            }
            return hit;
        }

        public bool HasGoldTile(RulesWordMatch match)
        {
            if (match.Cells == null || GridManager.Instance == null) return false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = GridManager.Instance.GetTile(match.Cells[i].x, match.Cells[i].y);
                if (tile != null && tile.IsGoldBonus) return true;
            }
            return false;
        }

        /// <summary>
        /// Consume gold tiles in a word and return the bonus multiplier.
        /// Call AFTER checking HasGoldTile for priming decisions.
        /// </summary>
        public int ConsumeGoldAndGetMultiplier(RulesWordMatch match)
        {
            if (match.Cells == null || GridManager.Instance == null) return 1;
            bool hitGold = false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = GridManager.Instance.GetTile(match.Cells[i].x, match.Cells[i].y);
                if (tile != null && tile.IsGoldBonus)
                {
                    hitGold = true;
                    tile.SetGoldBonus(false); // consumed!
                    _bonusCells[match.Cells[i].x, match.Cells[i].y] = false; // clear position too
//                     WDLog.Log($"[RulesEngine] Gold tile consumed at ({match.Cells[i].x},{match.Cells[i].y})!");
                }
            }
            return hitGold ? 2 : 1;
        }

        // ── Word Echo: letter streak bonus ───────────────────────────────────────

        // Tracks how many words each player has scored starting with each letter.
        // Each subsequent word starting with the same letter gets +echoCount bonus.
        private Dictionary<char, int>[] _echoCounters = new Dictionary<char, int>[]
        {
            new Dictionary<char, int>(), // player 0 (human)
            new Dictionary<char, int>(), // player 1 (AI)
        };

        /// <summary>Get the echo bonus for a word scored by a player. Increments the counter.</summary>
        public int ConsumeEchoBonus(string word, int playerIndex)
        {
            if (string.IsNullOrEmpty(word) || playerIndex < 0 || playerIndex >= _echoCounters.Length)
                return 0;

            char startLetter = char.ToUpper(word[0]);
            var counters = _echoCounters[playerIndex];

            int echoCount = 0;
            if (counters.TryGetValue(startLetter, out echoCount) && echoCount > 0)
            {
//                 WDLog.Log($"[RulesEngine] Word Echo: '{word}' starts with '{startLetter}' — echo #{echoCount} → +{echoCount} bonus");
            }

            // Increment for next time
            counters[startLetter] = echoCount + 1;
            return Mathf.Min(echoCount, 3); // capped at +3 to prevent runaway scoring
        }

        /// <summary>Clear echo counters (called on match start via ClearBoard).</summary>
        private void ClearEchoCounters()
        {
            for (int i = 0; i < _echoCounters.Length; i++)
                _echoCounters[i].Clear();
        }

        // ── Primed word registry ──────────────────────────────────────────────────

        private PrimedWordRegistry _primedRegistry = new PrimedWordRegistry();

        /// <summary>Public access to the primed word registry.</summary>
        public PrimedWordRegistry PrimedRegistry => _primedRegistry;

        // ── Global turn counter (incremented externally by MatchController) ───────

        private int _globalTurn = 0;

        /// <summary>Current global turn number. Increment after each player's full turn.</summary>
        public int GlobalTurn
        {
            get => _globalTurn;
            set => _globalTurn = value;
        }

        // ── Events (C# delegates) ────────────────────────────────────────────────

        public event RulesEventHandler<TileDroppedEvent>       OnTileDropped;
        public event RulesEventHandler<WordScoredEvent>        OnWordScored;
        public event RulesEventHandler<WordPrimedEvent>        OnWordPrimed;
        public event RulesEventHandler<PrimedTriggeredEvent>   OnPrimedTriggered;
        /// <summary>Fired per primed-word detonation AFTER DoExplode computes the
        /// final bonus (includes chain multiplier + cluster boost + gold). Use this
        /// for downstream systems that need the actual scoring impact, not the raw
        /// primed word's original score (ChainMeter fill, FX intensity scaling, etc).
        /// The int payload is the final bonus awarded for this detonation.</summary>
        public event System.Action<int>                        OnDetonationScored;
        public event RulesEventHandler<TilesExplodedEvent>     OnTilesExploded;
        public event RulesEventHandler<GravityCollapseEvent>   OnGravityCollapse;
        public event RulesEventHandler<ChainStepEvent>         OnChainStep;
        public event RulesEventHandler<ResolutionCompleteEvent> OnResolutionComplete;


        // ── Scored word tracking (prevents re-scoring same word at same cells) ────

        private HashSet<string> _scoredWordKeys = new HashSet<string>();

        /// <summary>Keys that are in _scoredWordKeys ONLY because they arrived pre-formed in a
        /// risen bottom row, not because anybody scored them.
        ///
        /// 2026-10-19 — RegisterWordsAtRow(0) marks every word in a new bottom row as "scored"
        /// so it will not AUTO-prime. That is right for the scanner and wrong for a player: a
        /// deliberate swipe of a word you spotted is exactly what the feature is for, and those
        /// words were being refused as "already claimed". Worse, RebuildScoredKeysAfterRise
        /// remaps the keys upward, so the block followed the word up the board forever — the
        /// word was unclaimable for the rest of the run. (Spencer: HOT on the bottom row.)
        /// Tracked separately so a swipe can allow them while genuinely-scored words stay
        /// blocked.</summary>
        private HashSet<string> _riseSeededKeys = new HashSet<string>();

        /// <summary>True when this key is blocked ONLY by the rise-seeding rule above, so a
        /// deliberate player claim should be allowed through.</summary>
        public bool IsRiseSeededKey(string key) => _riseSeededKeys.Contains(key);

        /// <summary>Should a DELIBERATE player claim of this word be refused?
        ///
        /// Only when it was genuinely SCORED. A key that is in _scoredWordKeys purely because
        /// RegisterWordsAtRow(0) seeded it — i.e. the word arrived pre-formed in a risen row and
        /// was marked so it would not AUTO-prime — must still be claimable by hand.
        ///
        /// 2026-10-19: this exemption existed only in BuildExplicitMatch (the swipe path). All
        /// FOUR swap sites checked IsScoredKey raw, so swapping to form a word that happened to
        /// already exist in a risen row did nothing at all, silently. Rises fire every two
        /// moves, so the lower rows are full of such words — which is why it read as "it's not
        /// priming words that are made when swapped". One helper now, so the two verbs cannot
        /// drift apart again.</summary>
        public bool IsClaimBlocked(string key) => IsScoredKey(key) && !IsRiseSeededKey(key);

        /// <summary>Consume the rise-seeding exemption once a player actually claims it.</summary>
        public void ClearRiseSeededKey(string key) => _riseSeededKeys.Remove(key);

        /// <summary>Check if a word key has already been scored (for external validation).</summary>
        public bool IsScoredKey(string key) => _scoredWordKeys.Contains(key);

        /// <summary>Register a word key as scored (for swap resolution).</summary>
        public void RegisterScoredKey(string key) => _scoredWordKeys.Add(key);
        public void RemoveScoredKey(string key) => _scoredWordKeys.Remove(key);

        /// <summary>Public wrapper for GetFuseLength.</summary>
        public int GetFuseLengthPublic(int wordLength) => GetFuseLength(wordLength);

        /// <summary>Public wrapper for ScanEntireBoard (for legal swap validation).</summary>
        public List<RulesWordMatch> ScanEntireBoardPublic() => ScanEntireBoard();

        /// <summary>Public wrapper for the wild-resolving seed scan. Swap/edit paths use
        /// this instead of raw FindNewWords so a word that runs THROUGH an uncommitted wild
        /// is detected: ScanSeedCellsOnly first resolves any uncommitted wilds (committing a
        /// letter), seeds the resolved cells, and returns words through the given seeds plus
        /// those wilds. Raw FindNewWords treats an uncommitted wild as a wall, so words like
        /// PAD (P-wild-D) formed by a swap stayed invisible until the next drop. 2026-06-08.</summary>
        public List<RulesWordMatch> ScanSeedCellsPublic(List<Vector2Int> seedCells) => ScanSeedCellsOnly(seedCells);

        /// <summary>True if the tile at (col,row) currently sits in at least one valid
        /// dictionary word. Lets an otherwise-no-op same-letter edit/swap "claim" a real
        /// word the player didn't build (e.g. one a rising row formed) — priming it or
        /// triggering a connected detonation. Does NOT exclude already-scored words:
        /// re-claiming costs an edit charge each time (Spencer's call), and a currently
        /// primed word is already blocked upstream (can't edit/swap a primed tile).
        /// Non-mutating (does not commit wilds). 2026-06-08 Spencer.</summary>
        public bool CellHasUnscoredWord(int col, int row)
        {
            if (!InBounds(col, row) || _board[col, row] == null) return false;
            return FindNewWords(col, row).Count > 0;
        }

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            ClearBoard();
//             WDLog.Log("[RulesEngine] Awake — 7×6 board initialized. " +
                      // "Detecting horizontal + vertical words (3–7 letters). NO diagonals.");
        }

        // ── Board manipulation ────────────────────────────────────────────────────

        public void SetCell(int col, int row, RulesCellData data)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = data;
            // 2026-06-03 Spencer bug: keep the cell's OWN coordinates in sync with
            // where it now lives. The Jester Hat shuffle relocates cells via SetCell;
            // without this, data.Row/Col stayed stale → ApplyGravityInData read the
            // wrong source rows → scrambled gravity, collisions, desync mess.
            if (data != null) { data.Col = col; data.Row = row; }
        }

        public RulesCellData GetCell(int col, int row)
        {
            if (!InBounds(col, row)) return null;
            return _board[col, row];
        }

        /// <summary>True if any HeroWord escort (drop-target) tile remains anywhere on the board.
        /// Used to detect "all escorts collected → level win is locked in" before the collect fly-up
        /// animation finishes (IsComplete only flips on landing). 2026-06-23 Spencer.</summary>
        public bool HasAnyDropTarget()
        {
            for (int c = 0; c < COLS; c++)
                for (int r = 0; r < ROWS; r++)
                {
                    var cell = _board[c, r];
                    if (cell != null && cell.IsDropTarget) return true;
                }
            return false;
        }

        public void ClearCell(int col, int row)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = null;
            _bonusCells[col, row] = false; // gold consumed with the cell
            _cyanCells[col, row]  = false; // cyan refill consumed too — parity with DoExplode
        }

        // ── SCRAPPY PROTOTYPE (2026-06-01): drop-to-bottom feel test ──────────────
        /// <summary>Spawn an inert drop-target RESTING ON TOP of the tallest existing
        /// column, so there are letters directly beneath it to clear (the whole point:
        /// clear below to drop it down). Stone-like (word barrier + falls with gravity)
        /// but survives detonations (guarded in DoExplode). Throwaway — LevelDebugMenu.</summary>
        public void SpawnDropTargetsForTest(int count)
        {
            // Place one on top of each of the `count` tallest columns (distinct
            // columns = juggling multiple at once, which is where the thought is).
            var cols = new System.Collections.Generic.List<Vector2Int>(); // x=col, y=height
            for (int c = 0; c < COLS; c++)
            {
                int h = 0;
                for (int r = 0; r < ROWS; r++)
                    if (_board[c, r] != null) h = r + 1;
                if (h > 0) cols.Add(new Vector2Int(c, h));
            }
            cols.Sort((a, b) => b.y.CompareTo(a.y)); // tallest first
            int n = Mathf.Min(count, cols.Count);
            for (int i = 0; i < n; i++)
            {
                int col = cols[i].x;
                int restRow = Mathf.Clamp(cols[i].y, 0, ROWS - 1); // just above the stack
                _board[col, restRow] = new RulesCellData
                {
                    Letter = '#',
                    Col = col,
                    Row = restRow,
                    IsStone = true,
                    IsDropTarget = true,
                };
            }
        }

        // Weighted letter bag, built once from the game's canonical letter distribution.
        private static char[] s_letterBag;
        private static char DrawWeightedLetter()
        {
            if (s_letterBag == null)
            {
                var list = new System.Collections.Generic.List<char>();
                foreach (var kv in LetterData.GetDistribution())
                    for (int i = 0; i < kv.Value; i++) list.Add(char.ToUpper(kv.Key));
                s_letterBag = list.Count > 0 ? list.ToArray() : new[] { 'E' };
            }
            return s_letterBag[SurvivalRng.Range(0, s_letterBag.Length)];
        }

        private static void ShuffleCells(System.Collections.Generic.List<Vector2Int> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        /// <summary>Vault levels: build the STARTING BOARD. (1) Densify the bottom `startFillRows`
        /// rows to ~`fillDensity` — fill EMPTY cells only (preserves the opening) and KEEP GAPS so
        /// gravity + word-forming have room; the top rows stay CLEAR as drop headroom. (2) Place
        /// `vaultCount` FIXED treasure vaults across the fill band, biasing ONE notably HIGHER
        /// (mid-board) — the reach-it-with-a-chain/splash skill moment — the rest spaced ≥3 apart
        /// (relaxed if the board is tight). Vaults are IsStone (crack via adjacent detonation) +
        /// IsAnchored (don't fall, don't rise; chest sprite; Rock-Crusher-immune). All three knobs
        /// are generator parameters (SurvivalManager exposes them in the Inspector). 2026-06-09.</summary>
        public void SeedVaultBoard(int startFillRows, float fillDensity, int vaultCount, int vaultHeightSpread, int chestMinSpacing,
                                   int midCount, int midWordLen, int highCount, int highWordLen)
        {
            startFillRows = Mathf.Clamp(startFillRows, 1, ROWS);
            fillDensity   = Mathf.Clamp01(fillDensity);

            // 0. Fresh board each loot level — clears any carryover from the previous level so the
            // player judges a clean board, not a crowded one. (The Chunk-3 generator owns level
            // boundaries later; this is the stop-gap so a run feel-tests cleanly.) 2026-06-09.
            ClearBoard();

            // 1. Fill each column BOTTOM-UP to a jagged target height ≈ fillDensity × startFillRows
            // (±1 jitter for a ragged surface). Raising EITHER dial raises the stack. Gaps live ONLY
            // at the column tops, never internal (random per-cell holes would float tiles that
            // collapse on the first gravity pass). HARD CAP: the top VAULT_TOP_HEADROOM rows always
            // stay clear (drop headroom) no matter how high the dials go — this is a drop game, so a
            // brim-full board would have nowhere to drop. 2026-06-09.
            const int VAULT_TOP_HEADROOM = 2;
            int fillCap = Mathf.Clamp(ROWS - VAULT_TOP_HEADROOM, 1, ROWS);
            int minH = int.MaxValue, maxH = 0;
            for (int c = 0; c < COLS; c++)
            {
                int targetH = Mathf.Clamp(
                    Mathf.RoundToInt(fillDensity * startFillRows) + SurvivalRng.Range(-1, 2),
                    1, fillCap);
                minH = Mathf.Min(minH, targetH); maxH = Mathf.Max(maxH, targetH);
                for (int r = 0; r < targetH; r++)
                    if (_board[c, r] == null)
                        _board[c, r] = new RulesCellData { Letter = DrawWeightedLetter(), Col = c, Row = r, PlayerIndex = -1 };
            }

            // 2. Gather plain candidate cells in the fill band; flag a HIGH band for the bias.
            int highMin = Mathf.Clamp(startFillRows - Mathf.Max(1, vaultHeightSpread), 0, startFillRows - 1);
            var all  = new System.Collections.Generic.List<Vector2Int>();
            var high = new System.Collections.Generic.List<Vector2Int>();
            for (int c = 0; c < COLS; c++)
                for (int r = 0; r < startFillRows; r++)
                {
                    var cell = _board[c, r];
                    if (cell == null) continue;
                    if (cell.IsStone || cell.IsAnchored || cell.IsDropTarget) continue;
                    if (cell.IsWild || cell.IsCyan || cell.IsSwapRefill || cell.IsEditRefill || cell.IsWildRefill) continue;
                    if (cell.Letter == '\0' || IsBonusCell(c, r)) continue;
                    var primed = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(c, r));
                    if (primed != null && primed.Count > 0) continue;
                    var p = new Vector2Int(c, r);
                    all.Add(p);
                    if (r >= highMin) high.Add(p);
                }
            ShuffleCells(all);

            var chosen = new System.Collections.Generic.List<Vector2Int>();

            // (a) One HIGH vault first: the HIGHEST candidate in the high band — the
            //     reach-it-with-a-chain/splash skill target. (Position only — NOT the special chest.)
            if (vaultCount > 0 && high.Count > 0)
            {
                Vector2Int top = high[0];
                foreach (var p in high) if (p.y > top.y) top = p;
                _board[top.x, top.y].IsStone = true; _board[top.x, top.y].IsAnchored = true;
                chosen.Add(top);
            }

            // (b) The rest: SPREAD across the full width — one chest per DISTINCT column, visiting
            //     evenly-sampled columns first so they reach the edges/corners instead of clustering
            //     in the letter mass (a couple end up in isolated pockets that cost setup to reach =
            //     the triage). Keep a min Chebyshev gap, relaxing only down to 2 (never adjacent).
            int minSpace = Mathf.Clamp(chestMinSpacing, 2, COLS);
            var byCol = new System.Collections.Generic.List<Vector2Int>[COLS];
            for (int c = 0; c < COLS; c++) byCol[c] = new System.Collections.Generic.List<Vector2Int>();
            foreach (var p in all) byCol[p.x].Add(p);
            for (int c = 0; c < COLS; c++) ShuffleCells(byCol[c]);

            var colOrder = new System.Collections.Generic.List<int>();
            for (int i = 0; i < vaultCount; i++) // evenly-spaced anchor columns across the width
            {
                int tc = Mathf.RoundToInt(i * (COLS - 1f) / Mathf.Max(1, vaultCount - 1));
                if (!colOrder.Contains(tc)) colOrder.Add(tc);
            }
            for (int c = 0; c < COLS; c++) if (!colOrder.Contains(c)) colOrder.Add(c); // then any remaining columns

            for (int minDist = minSpace; minDist >= 2 && chosen.Count < vaultCount; minDist--)
                foreach (int c in colOrder)
                {
                    if (chosen.Count >= vaultCount) break;
                    bool colUsed = false;
                    foreach (var q in chosen) if (q.x == c) { colUsed = true; break; } // one chest per column
                    if (colUsed) continue;
                    foreach (var p in byCol[c])
                    {
                        if (_board[p.x, p.y].IsAnchored) continue;
                        bool far = true;
                        foreach (var q in chosen)
                            if (Mathf.Max(Mathf.Abs(p.x - q.x), Mathf.Abs(p.y - q.y)) < minDist) { far = false; break; }
                        if (!far) continue;
                        _board[p.x, p.y].IsStone = true; _board[p.x, p.y].IsAnchored = true;
                        chosen.Add(p); break;
                    }
                }

            // (c) Fallback (tight board / more chests than columns): drop the one-per-column rule,
            //     place any remaining at distance ≥2, then ≥1, so we always place the full set.
            for (int minDist = 2; minDist >= 1 && chosen.Count < vaultCount; minDist--)
                foreach (var p in all)
                {
                    if (chosen.Count >= vaultCount) break;
                    if (_board[p.x, p.y].IsAnchored) continue;
                    bool far = true;
                    foreach (var q in chosen)
                        if (Mathf.Max(Mathf.Abs(p.x - q.x), Mathf.Abs(p.y - q.y)) < minDist) { far = false; break; }
                    if (!far) continue;
                    _board[p.x, p.y].IsStone = true; _board[p.x, p.y].IsAnchored = true;
                    chosen.Add(p);
                }

            // Assign chest TIERS by the word-LENGTH key needed to crack them (telegraphed on the
            // tile). The HIGH-tier chest(s) get the elevated reach-it position (placed first in
            // `chosen`) AND the hardest requirement = the telegraphed jackpot. A random subset of
            // the rest get the MID requirement; the remainder stay regular (RequiredWordLength=0 =
            // any word). SurvivalRng keeps the mid pick daily-seed-deterministic. 2026-06-12.
            int hi = Mathf.Clamp(highCount, 0, chosen.Count);
            for (int i = 0; i < hi; i++)
                _board[chosen[i].x, chosen[i].y].RequiredWordLength = Mathf.Max(2, highWordLen);

            var rest = new System.Collections.Generic.List<int>();
            for (int i = hi; i < chosen.Count; i++) rest.Add(i);
            for (int i = rest.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); (rest[i], rest[j]) = (rest[j], rest[i]); }
            int mid = Mathf.Clamp(midCount, 0, rest.Count);
            for (int i = 0; i < mid; i++)
                _board[chosen[rest[i]].x, chosen[rest[i]].y].RequiredWordLength = Mathf.Max(2, midWordLen);
            // Remaining chests keep RequiredWordLength = 0 (regular — crack on any word).

            WDLog.Log($"[Vault] SeedVaultBoard: startFillRows={startFillRows} density={fillDensity:0.00} → column heights {minH}-{maxH} of {ROWS} (cap {fillCap}); placed {chosen.Count}/{vaultCount} — {hi} high(≥{highWordLen}) {mid} mid(≥{midWordLen}) {Mathf.Max(0, chosen.Count - hi - mid)} regular.");
        }

        /// <summary>Vaults: how many anchored vaults remain on the board (objective polls this).</summary>
        /// <summary>Crates left on the board (full board: every stone is a crate).</summary>
        public int CountCrateCells()
        {
            int n = 0;
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) if (_board[c, r] != null && _board[c, r].IsStone && !_board[c, r].IsDropTarget) n++;
            return n;
        }

        public int CountAnchoredCells()
        {
            int n = 0;
            for (int c = 0; c < COLS; c++)
                for (int r = 0; r < ROWS; r++)
                    if (_board[c, r] != null && _board[c, r].IsAnchored) n++;
            return n;
        }

        // ── FROZEN / ICE objective (2026-06-12) ──────────────────────────────────────
        // Letters that are too rare to reliably re-use in a word — freezing one of these
        // would make the ice frustrating to clear (e.g. a lone frozen Q). Skip them.
        private static bool IsRareLetterForFreeze(char letter)
        {
            char u = char.ToUpper(letter);
            return u == 'Q' || u == 'Z' || u == 'X'; // skip the rarest letters (a lone frozen Q is frustrating)
        }

        /// <summary>ICE objective: freeze up to `count` existing OCCUPIED letter tiles (ice overlay).
        /// A frozen tile is a NORMAL, MATCHABLE letter (IsStone=false, real Letter) — it participates
        /// in word detection exactly like any other tile. To clear the ice it must be included in a
        /// detonated word, at which point it THAWS and survives (see DoExplode/TryThawCell). Picks
        /// common-letter, non-special cells at random via SurvivalRng (daily-seed deterministic).
        /// v1 = single layer. Returns the number actually frozen.</summary>
        public int SpawnFrozenTiles(int count)
        {
            if (count <= 0) return 0;

            // Gather eligible cells: occupied, real common letter, not a stone/anchor/drop-target/
            // vault/special/wild/gold, and not already frozen. Avoid the very top row (a rise could
            // push it off before it can be cleared).
            var eligible = new System.Collections.Generic.List<Vector2Int>();
            for (int c = 0; c < COLS; c++)
            {
                for (int r = 0; r < ROWS; r++)
                {
                    var cell = _board[c, r];
                    if (cell == null) continue;
                    if (cell.IsStone || cell.IsAnchored || cell.IsDropTarget) continue; // non-letter / blocker tiles
                    if (cell.IsWild || cell.Letter == '\0') continue;                    // unresolved wilds
                    if (cell.IsSwapRefill || cell.IsEditRefill || cell.IsWildRefill) continue; // protect specials
                    if (cell.IsFrozen) continue;                                         // already frozen
                    if (IsRareLetterForFreeze(cell.Letter)) continue;                    // skip Q/Z/X
                    if (r >= ROWS - 1) continue;                                          // not the top row (a rise could push it off)
                    eligible.Add(new Vector2Int(c, r));
                }
            }

            ShuffleCells(eligible); // SurvivalRng-backed (daily-seed deterministic)

            int n = Mathf.Min(count, eligible.Count);
            for (int i = 0; i < n; i++)
            {
                var p = eligible[i];
                _board[p.x, p.y].IsFrozen = true;
            }
            return n;
        }

        public int CountFrozenCells()
        {
            int n = 0;
            for (int c = 0; c < COLS; c++)
                for (int r = 0; r < ROWS; r++)
                    // A REAL ice tile is a frozen cell that still holds a LETTER. A cell emptied via the
                    // Letter='\0' re-resolution path keeps IsFrozen but has no tile — counting that GHOST left
                    // the ice objective un-winnable ("1 ice left" with nothing on the board). 2026-07-07 Spencer.
                    if (_board[c, r] != null && _board[c, r].IsFrozen && _board[c, r].Letter != '\0') n++;
            return n;
        }

        /// <summary>Column of a drop-target sitting on the bottom row (row 0), or -1.
        /// Poll-based bottom check for the prototype payoff.</summary>
        public int FindDropTargetAtBottomRow()
        {
            for (int c = 0; c < COLS; c++)
            {
                var cell = _board[c, 0];
                if (cell != null && cell.IsDropTarget) return c;
            }
            return -1;
        }

        public void ClearBoard()
        {
            // Clear entire array (MAX_ROWS) regardless of current mode
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < MAX_ROWS; row++)
                    _board[col, row] = null;

            _scoredWordKeys.Clear();
            _riseSeededKeys.Clear();
            _primedRegistry.Clear();
            _globalTurn = 0;
            ClearAllBonusCells();
            ClearAllCyanCells();
            ClearEchoCounters();

//             WDLog.Log("[RulesEngine] Board cleared.");
        }

        public int GetLowestEmptyRow(int col)
        {
            if (col < 0 || col >= COLS) return -1;
            for (int row = 0; row < ROWS; row++)
                if (_board[col, row] == null) return row;
            return -1;
        }

        public bool IsColumnAvailable(int col)
            => GetLowestEmptyRow(col) >= 0;

        /// <summary>2026-06-03 Spencer: preview helper. If a WILD dropped in `col`
        /// would form a word, returns the letter it would RESOLVE to (the one that
        /// makes the longest word through the drop cell, alphabetical tiebreak).
        /// Returns '\0' if the column is full or no letter makes any word.
        /// Non-mutating (uses SimulateDropWithTriggerCheck).</summary>
        public char PreviewWildResolveLetter(int col, int playerIndex)
        {
            if (GetLowestEmptyRow(col) < 0) return '\0';
            char best = '\0';
            int bestLen = 0;
            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                bool wouldTrigger;
                var matches = SimulateDropWithTriggerCheck(col, ch, playerIndex, out wouldTrigger);
                if (matches == null || matches.Count == 0) continue;
                int len = 0;
                for (int i = 0; i < matches.Count; i++)
                    if (matches[i].Word != null && matches[i].Word.Length > len)
                        len = matches[i].Word.Length;
                if (len > bestLen) { bestLen = len; best = ch; } // strict > → alphabetical tiebreak
            }
            return best;
        }

        /// <summary>How many tiles are in this column (0 = empty, ROWS = full).</summary>
        public int GetColumnHeight(int col)
        {
            int emptyRow = GetLowestEmptyRow(col);
            return emptyRow < 0 ? ROWS : emptyRow;
        }

        public int CountOccupied()
        {
            int count = 0;
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    if (_board[col, row] != null) count++;
            return count;
        }

        /// <summary>Returns board occupancy as a fraction (0.0 = empty, 1.0 = full).</summary>
        public float GetBoardOccupancy()
        {
            return (float)CountOccupied() / (COLS * ROWS);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RISING ROW SUPPORT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if ALL columns have a tile in the top row (board is full).
        /// Used by RisingRowManager to check for overflow before shifting.
        /// </summary>
        public bool HasTilesInTopRow()
        {
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
                if (_board[col, topRow] == null) return false;
            return true;
        }

        /// <summary>
        /// Returns true if ANY column has a tile in the top row.
        /// Used by Survival mode for top-out detection after auto-drops.
        /// </summary>
        public bool HasAnyTileInTopRow()
        {
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
                if (_board[col, topRow] != null) return true;
            return false;
        }

        /// <summary>
        /// How many more rising rows the board can absorb before topping out — the
        /// empty-row headroom above the HIGHEST occupied cell (gaps-aware; not the
        /// lowest-empty-row used by GetColumnHeight). 0 = a tile is already in the top
        /// row, so the very next rise overflows = game over. Drives the HUD danger
        /// indicator. Dynamic: clearing a tall stack raises it, a rise lowers it.
        /// </summary>
        public int GetRisesUntilTopOut()
        {
            int highestOccupiedRow = -1;
            for (int row = ROWS - 1; row >= 0; row--)
            {
                for (int col = 0; col < COLS; col++)
                {
                    // Break-Rocks: anchored rocks are fixed obstacles, not the player's playable
                    // stack — exclude them so the top-out counter reflects real pressure. 2026-06-09.
                    if (_board[col, row] != null && !_board[col, row].IsAnchored) { highestOccupiedRow = row; break; }
                }
                if (highestOccupiedRow >= 0) break;
            }
            // Empty board → full headroom. Else headroom = rows above the highest tile.
            return (ROWS - 1) - highestOccupiedRow;
        }

        /// <summary>
        /// Shifts all board data up one row. Row 0 becomes empty.
        /// Returns a move dictionary mapping old positions to new positions.
        /// IMPORTANT: Does NOT check for overflow — caller must check HasTilesInTopRow() first.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> ShiftBoardUp()
        {
            return ShiftBoardUp(out _);
        }

        /// <summary>
        /// Shifts all board data up one row. Row 0 becomes empty.
        /// Returns a move dictionary mapping old positions to new positions.
        /// Also outputs a list of top-row cells that were pushed off the board.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> ShiftBoardUp(out List<Vector2Int> crushedCells)
        {
            var moves = new Dictionary<Vector2Int, Vector2Int>();
            crushedCells = new List<Vector2Int>();

            // Collect tiles in top row that will be pushed off
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
            {
                if (_board[col, topRow] != null)
                {
                    crushedCells.Add(new Vector2Int(col, topRow));
                    _board[col, topRow] = null;
                }
            }

            // Shift from top to bottom to avoid overwriting
            for (int col = 0; col < COLS; col++)
            {
                for (int row = ROWS - 1; row >= 1; row--)
                {
                    _board[col, row] = _board[col, row - 1];
                    if (_board[col, row] != null)
                    {
                        var oldPos = new Vector2Int(col, row - 1);
                        var newPos = new Vector2Int(col, row);
                        moves[oldPos] = newPos;
                        _board[col, row].Row = row;
                    }
                }
                // Clear bottom row
                _board[col, 0] = null;
            }

//             WDLog.Log($"[RulesEngine] ShiftBoardUp — moved {moves.Count} tiles up by 1 row.");
            return moves;
        }

        /// <summary>
        /// Fills the bottom row (row 0) with random neutral letters from the bag.
        /// Returns the array of letters placed (length = COLS).
        /// Neutral tiles have PlayerIndex = -1.
        /// </summary>
        private static readonly char[] FERTILE_VOWELS     = { 'A', 'E', 'I', 'O', 'U' };
        private static readonly char[] FERTILE_CONNECTORS = { 'R', 'S', 'T', 'N', 'L', 'D', 'H', 'C', 'M', 'P' };

        // Old FindColumnHelper removed — replaced by FindColumnHelperImproved + candidate scoring

        // ── Candidate row generation for fertile rising rows ─────────────────

        /// <summary>
        /// Generates a candidate bottom row with variety and basic balance.
        /// Not yet scored — that happens in ScoreRowPlayability.
        /// </summary>
        private char[] GenerateCandidateRow(TileBag bag, HashSet<int> stoneColumns)
        {
            char[] row = new char[COLS];
            var used = new HashSet<char>();
            int vowelCount = 0;

            // Place stones first
            foreach (int sc in stoneColumns)
                row[sc] = '#';

            // Tall-column relief: if an edge column is significantly taller than
            // average, occasionally skip it (leave a gap) so it doesn't grow further.
            var skipCols = new HashSet<int>();
            if (SurvivalManager.IsSurvivalMode)
            {
                int avgHeight = 0;
                for (int c = 0; c < COLS; c++) avgHeight += GetColumnHeight(c);
                avgHeight /= COLS;

                // Check edge columns (0 and COLS-1) and near-edge (1 and COLS-2)
                int[] edgeCols = { 0, COLS - 1, 1, COLS - 2 };
                for (int e = 0; e < edgeCols.Length; e++)
                {
                    int c = edgeCols[e];
                    if (stoneColumns.Contains(c)) continue;
                    int h = GetColumnHeight(c);
                    // If this column is 3+ rows taller than average, 50% chance to skip
                    if (h >= avgHeight + 3 && SurvivalRng.Value < 0.50f)
                    {
                        skipCols.Add(c);
                    }
                }
            }

            // Fill non-stone, non-skip columns
            int nonStone = COLS - stoneColumns.Count;
            int targetVowels = 2;

            var colOrder = new List<int>();
            for (int c = 0; c < COLS; c++)
                if (!stoneColumns.Contains(c) && !skipCols.Contains(c)) colOrder.Add(c);
            for (int i = colOrder.Count - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                int tmp = colOrder[i]; colOrder[i] = colOrder[j]; colOrder[j] = tmp;
            }

            // First pass: try to place board-aware helpers via column simulation
            int helpersPlaced = 0;
            foreach (int col in colOrder)
            {
                if (helpersPlaced >= 4) break;
                if (SurvivalRng.Value > 0.38f) continue; // EXPERIMENT: was 0.60 — revert if rows feel dead

                char helper = FindColumnHelperImproved(col);
                if (helper != '\0' && !used.Contains(helper))
                {
                    row[col] = helper;
                    used.Add(helper);
                    if (IsVowelChar(helper)) vowelCount++;
                    helpersPlaced++;
                }
            }

            // Second pass: fill remaining with balanced letters
            foreach (int col in colOrder)
            {
                if (row[col] != '\0') continue;

                char picked;
                if (vowelCount < targetVowels)
                {
                    picked = PickUnused(FERTILE_VOWELS, used);
                    vowelCount++;
                }
                else if (SurvivalRng.Value < 0.25f && vowelCount < 3)
                {
                    picked = PickUnused(FERTILE_VOWELS, used);
                    vowelCount++;
                }
                else
                {
                    picked = PickUnused(FERTILE_CONNECTORS, used);
                }

                row[col] = picked;
                used.Add(picked);
            }

            return row;
        }

        /// <summary>
        /// Scores a candidate row by simulating placement at row 0 (which becomes
        /// row 1 after the rise) and checking for ACTUAL word opportunities.
        ///
        /// Scoring:
        ///   +5 per immediate word formed with existing board tiles
        ///   +3 per one-letter-away near-word (drop one more letter to complete)
        ///   +2 per horizontal word opportunity within the new row itself
        ///   +1 per useful adjacency (new letter next to existing tile)
        ///   -3 per dead column (new letter has no tiles nearby)
        ///   -2 if row has 0 or 1 vowels
        ///   -1 per duplicate letter
        /// </summary>
        private int ScoreRowPlayability(char[] row, HashSet<int> stoneColumns)
        {
            int score = 0;

            // Simulate: after rise, current board shifts up by 1. New row sits at row 1.
            // But we're scoring BEFORE the shift, so the new row will be at row 0
            // and existing tiles are still at their current positions (they'll shift to +1).
            // For scoring purposes: new row at 0, existing board at rows 1+.

            // Count immediate word opportunities
            // Check horizontal words WITHIN the new row
            for (int startCol = 0; startCol <= COLS - 3; startCol++)
            {
                for (int len = 3; len <= Mathf.Min(7, COLS - startCol); len++)
                {
                    bool valid = true;
                    var sb = new System.Text.StringBuilder(len);
                    for (int c = startCol; c < startCol + len; c++)
                    {
                        if (row[c] == '\0' || row[c] == '#') { valid = false; break; }
                        sb.Append(row[c]);
                    }
                    if (valid && WordDictionary.IsValidWord(sb.ToString()))
                    {
                        // Words within the new row are mildly good (they'll get primed)
                        // But too many means the row is "too easy" — cap at +4
                        score += 2;
                    }
                }
            }

            // Check vertical opportunities: new row letter + existing tiles above
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;

                // After rise, new letter at (col, 0), existing tile at (col, 0) moves to (col, 1)
                // So new letter is adjacent to what WAS at (col, 0)
                var above = _board[col, 0]; // will become (col, 1) after rise
                if (above == null || above.IsStone) continue;

                // Two adjacent letters — check if any A-Z above or below completes a word
                char newLetter = row[col];
                char aboveLetter = char.ToUpper(above.Letter);

                // Check 3-letter vertical: [above's above] + above + new
                var above2 = _board[col, 1]; // will become (col, 2)
                if (above2 != null && !above2.IsStone)
                {
                    string vert3 = "" + char.ToUpper(above2.Letter) + aboveLetter + newLetter;
                    if (WordDictionary.IsValidWord(vert3))
                        score += 5; // immediate word!
                    // Also check reverse
                    string vert3r = "" + newLetter + aboveLetter + char.ToUpper(above2.Letter);
                    if (WordDictionary.IsValidWord(vert3r))
                        score += 5;
                }

                // Near-word: new + above, needs one more letter dropped on top
                // Check if any letter X makes (X + above + new) or (above + new + X) a word
                bool hasNearWord = false;
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    string try1 = "" + ch + aboveLetter + newLetter;
                    string try2 = "" + aboveLetter + newLetter + ch;
                    // Reverse direction
                    string try3 = "" + ch + newLetter + aboveLetter;
                    string try4 = "" + newLetter + aboveLetter + ch;
                    if (WordDictionary.IsValidWord(try1) || WordDictionary.IsValidWord(try2) ||
                        WordDictionary.IsValidWord(try3) || WordDictionary.IsValidWord(try4))
                    {
                        hasNearWord = true;
                        break;
                    }
                }
                if (hasNearWord)
                    score += 3; // good adjacency — one drop away from a word

                // Basic adjacency bonus
                score += 1;
            }

            // Check horizontal near-words: pairs in the new row with a gap
            for (int col = 0; col < COLS - 2; col++)
            {
                char left = row[col];
                char right = row[col + 2];
                if (left == '\0' || left == '#' || right == '\0' || right == '#') continue;
                if (row[col + 1] != '\0' && row[col + 1] != '#') continue; // no gap

                // Gap pattern: left _ right — check if filling creates a word
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    string fill = "" + left + ch + right;
                    if (WordDictionary.IsValidWord(fill))
                    {
                        score += 2;
                        break; // count once per gap
                    }
                }
            }

            // Penalties
            int vowels = 0;
            var letterCounts = new Dictionary<char, int>();
            for (int col = 0; col < COLS; col++)
            {
                char c = row[col];
                if (c == '\0' || c == '#') continue;
                if (IsVowelChar(c)) vowels++;
                if (!letterCounts.ContainsKey(c)) letterCounts[c] = 0;
                letterCounts[c]++;
            }

            if (vowels < 2) score -= 2;
            foreach (var kv in letterCounts)
                if (kv.Value > 1) score -= (kv.Value - 1);

            // Vertical-stack penalty: discourage placing a letter in a column
            // that already has the same letter stacked above. Over multiple
            // rises this prevents 3-4 of the same letter (esp. vowels) from
            // building up in a single column. Penalty scales with existing
            // stack count, so adding a 2nd I costs 2, a 3rd costs 4, etc.
            for (int col = 0; col < COLS; col++)
            {
                char newLetter = row[col];
                if (newLetter == '\0' || newLetter == '#') continue;
                int sameLetterInColumn = 0;
                for (int r = 0; r < ROWS; r++)
                {
                    var cell = _board[col, r];
                    if (cell != null && !cell.IsStone && cell.Letter == newLetter)
                        sameLetterInColumn++;
                }
                if (sameLetterInColumn >= 1) score -= sameLetterInColumn * 2;
            }

            // Dead columns: new letter with no existing tiles nearby
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;
                bool hasNeighbor = false;
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nc = col + dc;
                    if (nc < 0 || nc >= COLS) continue;
                    for (int r = 0; r < Mathf.Min(3, ROWS); r++)
                    {
                        if (_board[nc, r] != null && !_board[nc, r].IsStone)
                        { hasNeighbor = true; break; }
                    }
                    if (hasNeighbor) break;
                }
                if (!hasNeighbor) score -= 1;
            }

            // Tall column relief: reward rows that create word opportunities
            // in the tallest columns (edge junk tower prevention).
            // Bonus if the letter in a tall column forms a near-word with its neighbors.
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;
                int height = GetColumnHeight(col);
                if (height < 4) continue; // only care about tall columns

                // Bonus for creating word opportunities in tall columns
                char aboveChar = (_board[col, 0] != null && !_board[col, 0].IsStone)
                    ? char.ToUpper(_board[col, 0].Letter) : '\0';
                if (aboveChar != '\0')
                {
                    // Check if new letter + above letter could be part of a word
                    for (char ch = 'A'; ch <= 'Z'; ch++)
                    {
                        string t1 = "" + ch + aboveChar + row[col];
                        string t2 = "" + row[col] + aboveChar + ch;
                        if (WordDictionary.IsValidWord(t1) || WordDictionary.IsValidWord(t2))
                        {
                            score += 3; // tall column gets a word opportunity — great
                            break;
                        }
                    }
                }

                // Penalty if a tall edge column gets a junk letter with no opportunity
                bool isEdge = (col == 0 || col == COLS - 1);
                if (isEdge && aboveChar == '\0')
                    score -= 2; // adding to a tall edge with nothing above = more junk
            }

            return score;
        }

        /// <summary>
        /// Improved column helper: checks what letter at (col, 0) would create
        /// a word with existing tiles at rows 0-2 (which shift to 1-3 after rise).
        /// Also checks horizontal neighbors in the new row.
        /// </summary>
        private char FindColumnHelperImproved(int col)
        {
            var candidates = new List<char>(8);

            // What's directly above? (will be at row 1 after rise)
            var above = _board[col, 0];
            char aboveLetter = (above != null && !above.IsStone) ? char.ToUpper(above.Letter) : '\0';

            // What's two above? (will be at row 2 after rise)
            var above2 = _board[col, 1];
            char above2Letter = (above2 != null && !above2.IsStone) ? char.ToUpper(above2.Letter) : '\0';

            // Horizontal neighbors at row 0 (other new row letters aren't placed yet,
            // but existing tiles at row 0 will shift to row 1)
            // Check what letters in adjacent columns at rows 0-1 could form horizontal words

            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                if (candidates.Count >= 8) break;

                // Vertical check: ch + above + above2 (3-letter vertical word)
                if (aboveLetter != '\0' && above2Letter != '\0')
                {
                    string v1 = "" + above2Letter + aboveLetter + ch;
                    string v2 = "" + ch + aboveLetter + above2Letter;
                    if (WordDictionary.IsValidWord(v1) || WordDictionary.IsValidWord(v2))
                    { candidates.Add(ch); continue; }
                }

                // Near-word check: ch + above, any X makes a word
                if (aboveLetter != '\0')
                {
                    for (char x = 'A'; x <= 'Z'; x++)
                    {
                        string t1 = "" + x + aboveLetter + ch;
                        string t2 = "" + aboveLetter + ch + x;
                        string t3 = "" + x + ch + aboveLetter;
                        string t4 = "" + ch + aboveLetter + x;
                        if (WordDictionary.IsValidWord(t1) || WordDictionary.IsValidWord(t2) ||
                            WordDictionary.IsValidWord(t3) || WordDictionary.IsValidWord(t4))
                        { candidates.Add(ch); goto nextChar; }
                    }
                }

                // Horizontal check: what letters in adjacent columns make words?
                for (int dc = -2; dc <= 0; dc++)
                {
                    // Check if col+dc to col+dc+2 has tiles that form a word with ch
                    int startC = col + dc;
                    if (startC < 0 || startC + 2 >= COLS) continue;

                    // Read adjacent letters from the CURRENT board at row 0
                    // (these will shift up, but we're checking if letters nearby
                    //  would form words in the new row context)
                    char[] buf = new char[3];
                    bool allValid = true;
                    for (int i = 0; i < 3; i++)
                    {
                        int c = startC + i;
                        if (c == col)
                            buf[i] = ch;
                        else
                        {
                            var cell = _board[c, 0];
                            if (cell == null || cell.IsStone) { allValid = false; break; }
                            buf[i] = char.ToUpper(cell.Letter);
                        }
                    }
                    if (allValid && WordDictionary.IsValidWord(new string(buf)))
                    { candidates.Add(ch); goto nextChar; }
                }

                nextChar:;
            }

            if (candidates.Count == 0) return '\0';
            return candidates[SurvivalRng.Range(0, candidates.Count)];
        }

        private static bool IsVowelChar(char c)
        {
            c = char.ToUpper(c);
            return c == 'A' || c == 'E' || c == 'I' || c == 'O' || c == 'U';
        }

        // ── Post-gravity fertility helpers ──────────────────────────────────────

        /// <summary>
        /// Counts how many "near-words" exist on the board — positions where
        /// dropping one letter would complete a 3+ letter word. This measures
        /// how alive the board feels.
        /// </summary>
        private int CountNearWords()
        {
            int count = 0;

            for (int col = 0; col < COLS; col++)
            {
                // Find the lowest empty row in this column
                int emptyRow = -1;
                for (int row = 0; row < ROWS; row++)
                {
                    if (_board[col, row] == null) { emptyRow = row; break; }
                }
                if (emptyRow < 0) continue; // column full

                // Try each letter A-Z as a hypothetical drop
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    if (CheckWordAt(col, emptyRow, ch))
                    {
                        count++;
                        break; // one per column
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Checks if placing 'letter' at (col, row) would create a valid word
        /// horizontally or vertically.
        /// </summary>
        private bool CheckWordAt(int col, int row, char letter)
        {
            // Horizontal check
            int left = col, right = col;
            while (left > 0 && _board[left - 1, row] != null && !_board[left - 1, row].IsStone) left--;
            while (right < COLS - 1 && _board[right + 1, row] != null && !_board[right + 1, row].IsStone) right++;

            if (right - left + 1 >= 3)
            {
                var sb = new System.Text.StringBuilder(right - left + 1);
                for (int c = left; c <= right; c++)
                    sb.Append(c == col ? letter : char.ToUpper(_board[c, row].Letter));

                string full = sb.ToString();
                int idx = col - left;
                for (int start = 0; start < full.Length; start++)
                    for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                        if (idx >= start && idx < start + len)
                            if (WordDictionary.IsValidWord(full.Substring(start, len)))
                                return true;
            }

            // Vertical check
            int bottom = row, top = row;
            while (bottom > 0 && _board[col, bottom - 1] != null && !_board[col, bottom - 1].IsStone) bottom--;
            while (top < ROWS - 1 && _board[col, top + 1] != null && !_board[col, top + 1].IsStone) top++;

            if (top - bottom + 1 >= 3)
            {
                var sb = new System.Text.StringBuilder(top - bottom + 1);
                for (int r = top; r >= bottom; r--)
                    sb.Append(r == row ? letter : char.ToUpper(_board[col, r].Letter));

                string full = sb.ToString();
                int idx = top - row;
                for (int start = 0; start < full.Length; start++)
                    for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                        if (idx >= start && idx < start + len)
                            if (WordDictionary.IsValidWord(full.Substring(start, len)))
                                return true;
            }

            return false;
        }

        /// <summary>
        /// Silently nudges up to 'maxNudge' tiles to create word opportunities.
        /// Picks isolated junk consonants and replaces them with letters that
        /// would create near-words with their neighbors.
        /// Returns the number of tiles nudged.
        /// </summary>
        private int NudgeTilesForFertility(int maxNudge)
        {
            int nudged = 0;

            // Collect nudge candidates: non-primed, non-special, non-gold tiles
            // Prefer tiles in the bottom half and on the edges (junk tower zones)
            var candidates = new List<(int col, int row, int priority)>();

            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var cell = _board[col, row];
                    if (cell == null || cell.IsStone) continue;
                    if (cell.IsSwapRefill || cell.IsEditRefill || cell.IsWildRefill) continue;
                    if (cell.IsWild) continue; // wilds are never nudged
                    if (_bonusCells[col, row]) continue;

                    // Check if part of a primed word — skip
                    var primed = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
                    if (primed != null && primed.Count > 0) continue;

                    // Priority: edge columns + lower rows = more likely junk
                    int edgeDist = Mathf.Min(col, COLS - 1 - col);
                    int priority = (3 - edgeDist) + (ROWS - row); // higher = more likely to nudge
                    candidates.Add((col, row, priority));
                }
            }

            // Sort by priority (highest first = edge/bottom tiles)
            candidates.Sort((a, b) => b.priority.CompareTo(a.priority));

            for (int i = 0; i < candidates.Count && nudged < maxNudge; i++)
            {
                int col = candidates[i].col;
                int row = candidates[i].row;

                // Try each letter A-Z: would replacing this tile create a near-word?
                // A "near-word" means: if someone dropped one more letter adjacent,
                // it would form a word. We approximate: does the NEW letter participate
                // in a 2-letter run that can be extended to 3?
                char bestLetter = '\0';
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    // Check if this letter creates a horizontal or vertical near-word
                    if (CheckWordAt(col, row, ch))
                    {
                        // Don't pick the same letter that's already there
                        if (char.ToUpper(_board[col, row].Letter) == ch) continue;
                        bestLetter = ch;
                        break;
                    }
                }

                if (bestLetter != '\0')
                {
                    char oldLetter = _board[col, row].Letter;
                    _board[col, row].Letter = bestLetter;

                    // Purge scored keys for this cell so new words can be detected
                    PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

                    nudged++;
//                     WDLog.Log($"[PostGravityFertility] Nudged ({col},{row}): '{oldLetter}' → '{bestLetter}'");
                }
            }

            return nudged;
        }

        /// <summary>Pick a letter from the pool that hasn't been used yet (for variety).</summary>
        private static char PickUnused(char[] pool, HashSet<char> used)
        {
            // Try to find an unused letter
            for (int attempt = 0; attempt < 10; attempt++)
            {
                char c = pool[SurvivalRng.Range(0, pool.Length)];
                if (!used.Contains(c)) return c;
            }
            // Fallback: just pick any
            return pool[SurvivalRng.Range(0, pool.Length)];
        }

        /// <summary>Chance a rising row carries a planted near-word (instant-pop mode).</summary>
        // ── SPECIAL TILES (2026-10-21 Spencer: Candy-Crush style; "whatever you think is best for now") ─────────
        // A long word leaves ONE tile behind as a special. It KEEPS its letter; when a later word pops through it
        // (or it is caught in another special's blast) it fires. 5 letters -> ROCKET along the word's direction,
        // 6 -> BOMB (3x3), 7+ -> DISCO (every tile with its letter). Created on the swapped cell if it is in the
        // word, else the middle letter. Instant-pop mode only.
        public static bool SPECIAL_TILES_ENABLED = true;
        public const int SPECIAL_NONE = 0, SPECIAL_ROCKET_H = 1, SPECIAL_ROCKET_V = 2, SPECIAL_BOMB = 3, SPECIAL_DISCO = 4;
        /// <summary>TNT (2026-10-21 Spencer): a letter that starts the level carrying dynamite. Blows like a bomb (3x3, 5x5
        /// powered) when its letter is used in a word or it is double-tapped. Never born from words — placed at level start.</summary>
        public const int SPECIAL_TNT = 5;
        public static bool IsBombLike(int k) => k == SPECIAL_BOMB || k == SPECIAL_TNT;
        public static int SPECIAL_TILE_PAY = 15;   // points per extra tile a special clears
        public static bool SpecialTilesActive => SPECIAL_TILES_ENABLED && HandManager.InstantPopMode;
        /// <summary>The two cells of the most recent player swap — where a special is born if it is in the word.</summary>
        public static Vector2Int[] LastSwapCells;
        /// <summary>Every tile ever exploded (monotonic). The bonus pays per tile from its delta — a full board refills in the
        /// same fall, so "tiles before minus tiles after" read 0 (2026-10-21: 69 tiles cleared, 0 coins).</summary>
        public static int ExplodedTilesTotal;

        // ── FULL BOARD: SHORT WORDS ONLY ON PURPOSE (2026-10-21 Spencer: full board was "pretty insane. you make one word
        // and there are explosions everywhere"). Log: GAP popped, the columns slid, and the fall spelled 8 three-letter
        // words at once — 3-letter words are everywhere in a full grid. Rule: a word shorter than FULL_CASCADE_MIN_LEN
        // counts only if it runs through a cell the player just SWAPPED. ShortWordCells is set by the swap and closed at
        // the first explosion, so nothing the fall makes afterwards can qualify. Off outside full-board mode. ──
        public static int FULL_CASCADE_MIN_LEN = 4;
        /// <summary>Does this word run through a tile the player just swapped? (= made on purpose.)</summary>
        private static bool TouchesSwap(List<Vector2Int> cells)
        {
            if (ShortWordCells == null || cells == null) return false;
            foreach (var sc in ShortWordCells) if (cells.Contains(sc)) return true;
            return false;
        }
        public static Vector2Int[] ShortWordCells;
        // ROUND 2 (2026-10-21): still "any exchange i make creates a word" — 17 of 19 swaps. Simulated 2,400 random swaps on
        // full boards: 3+ letters 54% hit, 4+ letters 12%. So in full-board mode EVERY word is 4+ (FULL_MIN_LEN), swaps and
        // swipes included. FULL_SHORT_BY_SWAP = true brings back round 1 (3-letter words allowed when the swap makes them).
        public static int  FULL_MIN_LEN      = 4;
        public static bool FULL_SHORT_BY_SWAP = false;
        // Words the FALL makes must also be COMMON (Resources/common_words.txt = common-words sweep ∩ core, ~4.7k words).
        // Spencer: the dictionary "counts things you wouldnt even think of as a word" — the log popped SKA, PIA, LAC, LEA,
        // DEE, NAH. Words the player makes on purpose (swap / swipe) keep the full dictionary, so a real word like HONK
        // still counts when you go for it. Not a dictionary edit — a second, stricter list for automatic words only.
        public static bool FULL_FALL_COMMON_ONLY = true;
        private static HashSet<string> s_commonWords;
        private static bool IsCommonWord(string w)
        {
            if (s_commonWords == null)
            {
                s_commonWords = new HashSet<string>();
                var ta = Resources.Load<TextAsset>("common_words");
                if (ta != null) foreach (var raw in ta.text.Split('\n')) { var t = raw.Trim().ToUpperInvariant(); if (t.Length > 0) s_commonWords.Add(t); }
                WDLog.Log($"[FullBoard] common-word list: {s_commonWords.Count} words{(ta == null ? " (MISSING — allowing all)" : "")}");
            }
            return s_commonWords.Count == 0 || s_commonWords.Contains(w);
        }
        private static bool ShortWordRejected(int len, List<Vector2Int> cells, string word = null)
        {
            if (!SurvivalManager.FullBoardActive) return false;
            bool bySwap = false;
            if (ShortWordCells != null) foreach (var sc in ShortWordCells) if (cells.Contains(sc)) { bySwap = true; break; }
            if (len < FULL_MIN_LEN && !(FULL_SHORT_BY_SWAP && bySwap)) return true;
            if (FULL_SHORT_BY_SWAP && len < FULL_CASCADE_MIN_LEN && !bySwap) return true;
            if (FULL_FALL_COMMON_ONLY && !bySwap && word != null && !IsCommonWord(word)) return true;   // fall-made: common words only
            return false;
        }
        /// <summary>Shortest word that counts right now (swipes use this too).</summary>
        public static int EffectiveMinWordLength => SurvivalManager.FullBoardActive ? Mathf.Max(MIN_WORD_LENGTH, FULL_MIN_LEN) : MIN_WORD_LENGTH;
        /// <summary>One-shot: the next tap detonation seeds EVERY armed word (the bonus blast fires all its specials in one
        /// detonation, no gaps). Consumed by BeginTapDetonation.</summary>
        public static bool SeedAllOnNextTap;
        public struct SpecialFire { public Vector2Int Cell; public int Kind; public char Letter; public string Word; public List<Vector2Int> Cleared; }
        public static string SpecialName(int k) => k == SPECIAL_ROCKET_H ? "ROCKET-H" : k == SPECIAL_ROCKET_V ? "ROCKET-V"
                                                 : k == SPECIAL_BOMB ? "BOMB" : k == SPECIAL_TNT ? "TNT" : k == SPECIAL_DISCO ? "DISCO" : "none";

        public static float PLANT_CHANCE      = 1.0f;   // 0.6 -> 1.0 2026-10-20: Spencer "RNG seems to be a thing" — whether a
                                                          // level passed hinged on how many plants happened to appear
        /// <summary>Near-words planted into a FRESH level board (so there is something to hunt from move 1).</summary>
        public static int   PLANTS_ON_FRESH_BOARD = 2;

        /// <summary>Plant up to <paramref name="count"/> near-words into the lowest full rows of the current board
        /// (one per row). Called on a fresh level board before it is rebuilt on screen.</summary>
        public void PlantNearWordsOnBoard(int count, bool spreadRows = false)
        {
            int planted = 0;
            var rowOrder = new List<int>(); for (int i = 0; i < ROWS; i++) rowOrder.Add(i);
            if (spreadRows)   // full board: plants anywhere, not stacked in the bottom rows
                for (int i = rowOrder.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); int t = rowOrder[i]; rowOrder[i] = rowOrder[j]; rowOrder[j] = t; }
            foreach (int r in rowOrder)
            {
                if (planted >= count) break;
                var row = new char[COLS];
                for (int c = 0; c < COLS; c++)
                {
                    var cell = _board[c, r];
                    row[c] = (cell == null || cell.IsStone || cell.IsFrozen || cell.Letter == '\0') ? '\0' : cell.Letter;
                }
                var before = (char[])row.Clone();
                TryPlantNearWord(row);
                bool changed = false;
                for (int c = 0; c < COLS; c++)
                    if (row[c] != before[c] && _board[c, r] != null) { _board[c, r].Letter = row[c]; changed = true; }
                if (changed) planted++;
            }
            WDLog.Log($"[Plant] fresh board: {planted} near-word(s) planted");
        }
        /// <summary>Of planted words, the share that are 5 letters (the rest are 4).</summary>
        public static float PLANT_FIVE_SHARE  = 0.35f;
        public static float FULL_PLANT_FIVE_SHARE = 0.6f;   // full board: more 5-letter near-words = more rockets within reach
        private static List<string> s_plant4, s_plant5;

        /// <summary>Writes a common 4-5 letter word into the row with two neighbouring letters swapped,
        /// so ONE horizontal drag completes it. Words come from Resources/plant_words.txt (the
        /// common-words sweep intersected with the core dictionary — recognisable, never obscure).
        /// The scrambled form is checked not to be a word itself, either direction, so it can't pop
        /// on its own.</summary>
        private static HashSet<string> s_plantSet;
        private void EnsurePlantLists()
        {
            // 2026-10-21: check the SET, not the lists — TryPlantNearWord used to load the lists itself without building the
            // set, so the set stayed null and every rain NRE'd in CommonWordThrough before a single letter fell.
            if (s_plantSet != null) return;
            if (s_plant4 != null) { s_plantSet = new HashSet<string>(s_plant4); s_plantSet.UnionWith(s_plant5); return; }
            s_plant4 = new List<string>(); s_plant5 = new List<string>();
            var ta = Resources.Load<TextAsset>("plant_words");
            if (ta != null)
                foreach (var raw in ta.text.Split('\n'))
                {
                    string w = raw.Trim().ToUpperInvariant();
                    if (w.Length == 4) s_plant4.Add(w); else if (w.Length == 5) s_plant5.Add(w);
                }
            s_plantSet = new HashSet<string>(s_plant4); s_plantSet.UnionWith(s_plant5);
            WDLog.Log($"[Plant] word list: {s_plant4.Count} four-letter, {s_plant5.Count} five-letter");
        }

        // ── RAIN SEEDING (2026-10-21 Spencer: "is the rain seeding possible words like row rise does?" — only 2 of 13
        // rains did, so: smart landing letters + a column-plant fallback). ─────────────────────────────────────────────
        /// <summary>Letters on the board as a grid ('\0' empty) plus which tiles may move in a swap (not frozen/stone).</summary>
        public void SnapshotForRain(out char[,] g, out bool[,] movable)
        {
            g = new char[COLS, ROWS]; movable = new bool[COLS, ROWS];
            for (int x = 0; x < COLS; x++)
                for (int y = 0; y < ROWS; y++)
                {
                    var d = _board[x, y];
                    if (d == null || (d.Letter == '\0' && !d.IsStone)) continue;
                    g[x, y] = d.IsStone ? '#' : char.ToUpperInvariant(d.Letter);   // crates/stones: occupied walls, never part of a word
                    movable[x, y] = !d.IsFrozen && !d.IsStone && !d.IsWild && !d.IsDropTarget && !d.IsAnchored;
                }
        }

        /// <summary>Is there a COMMON 4-5 letter word (plant list) through <paramref name="c"/>, read the way the game spots
        /// words (left-to-right, top-to-bottom; row 0 is the bottom)?</summary>
        private bool CommonWordThrough(char[,] g, Vector2Int c, out string found)
        {
            found = null;
            // horizontal run
            int l = c.x, r = c.x;
            while (l - 1 >= 0 && g[l - 1, c.y] != '\0') l--;
            while (r + 1 < COLS && g[r + 1, c.y] != '\0') r++;
            for (int len = 5; len >= 4; len--)
                for (int s = Mathf.Max(l, c.x - len + 1); s <= c.x && s + len - 1 <= r; s++)
                {
                    var sb = new System.Text.StringBuilder(len);
                    for (int k = 0; k < len; k++) sb.Append(g[s + k, c.y]);
                    string w = sb.ToString();
                    if (s_plantSet.Contains(w) && WordDictionary.IsValidWord(w)) { found = w; return true; }
                }
            // vertical run, top -> bottom = high row -> low row
            int top = c.y, bot = c.y;
            while (top + 1 < ROWS && g[c.x, top + 1] != '\0') top++;
            while (bot - 1 >= 0 && g[c.x, bot - 1] != '\0') bot--;
            for (int len = 5; len >= 4; len--)
                for (int s = Mathf.Min(top, c.y + len - 1); s >= c.y && s - len + 1 >= bot; s--)
                {
                    var sb = new System.Text.StringBuilder(len);
                    for (int k = 0; k < len; k++) sb.Append(g[c.x, s - k]);
                    string w = sb.ToString();
                    if (s_plantSet.Contains(w) && WordDictionary.IsValidWord(w)) { found = w; return true; }
                }
            return false;
        }

        /// <summary>Find a letter to land at (col,row) so that ONE legal swap of two touching tiles near it spells a common
        /// 4-5 letter word — and it is NOT already a word before the swap (that would be free, not a find).</summary>
        public bool FindSmartRainLetter(char[,] g, bool[,] movable, int col, int row, out char letter, out string word)
        {
            EnsurePlantLists();
            letter = '\0'; word = null;
            const string ABC0 = "EARIOTNSLCUDPMHGBY";   // 18 useful letters — keeps the search cheap on phones (rare letters seldom complete common words)
            // SHUFFLED per call (2026-10-21). It used to rotate from one of the first 6 letters, so smart letters were almost
            // always E/A/R/I/O/T — the log's smart picks were R 78, I 77, T 74, E 61, A 56, O 55 — and the board filled with O's.
            var abcArr = ABC0.ToCharArray();
            for (int i = abcArr.Length - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); char t0 = abcArr[i]; abcArr[i] = abcArr[j]; abcArr[j] = t0; }
            string ABC = new string(abcArr); int start = 0;
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            for (int li = 0; li < ABC.Length; li++)
            {
                char L = ABC[(li + start) % ABC.Length];
                g[col, row] = L; movable[col, row] = true;
                string pre; bool already = CommonWordThrough(g, new Vector2Int(col, row), out pre);
                if (!already)
                    for (int dx = -2; dx <= 2 && word == null; dx++)
                        for (int dy = -2; dy <= 1 && word == null; dy++)
                        {
                            var a = new Vector2Int(col + dx, row + dy);
                            if (a.x < 0 || a.x >= COLS || a.y < 0 || a.y >= ROWS || g[a.x, a.y] == '\0' || !movable[a.x, a.y]) continue;
                            foreach (var d in dirs)
                            {
                                var b = a + d;
                                if (b.x >= COLS || b.y >= ROWS || g[b.x, b.y] == '\0' || !movable[b.x, b.y]) continue;
                                if (g[a.x, a.y] == g[b.x, b.y]) continue;
                                char t = g[a.x, a.y]; g[a.x, a.y] = g[b.x, b.y]; g[b.x, b.y] = t;
                                string w1, w2;
                                bool hit = CommonWordThrough(g, a, out w1) || CommonWordThrough(g, b, out w2) && (w1 = w2) != null;
                                g[b.x, b.y] = g[a.x, a.y]; g[a.x, a.y] = t;   // undo
                                if (hit) { word = w1; break; }
                            }
                        }
                if (word != null) { letter = L; return true; }   // leave L in the grid for the caller's next search
                g[col, row] = '\0'; movable[col, row] = false;
            }
            return false;
        }

        // ── FULL BOARD (2026-10-21 Spencer: "just like candy crush and royal match where letters are always fully stacked,
        // then as you make words it cascades from above"). Refill letters must not spell words by themselves — the
        // match-3 rule that new pieces never arrive pre-matched — or a full grid pops on its own like a slot machine. ──
        public static int FULL_NOWORD_TRIES = 14;

        /// <summary>Is there any dictionary word (>= minLen, <= MAX_WORD_LENGTH) through (x,y), read the way the game reads
        /// (left-to-right, top-to-bottom; row 0 is the bottom)?</summary>
        /// <summary>Shortest word new full-board letters must avoid spelling. 4 since round 3 (2026-10-21 Spencer: "more nested
        /// word possibilities ... something just feels off"): 3-letter words can't pop (FULL_MIN_LEN 4), so letting them sit
        /// on the board keeps the stepping stones (CAT -> CART / CATS / SCAT). At 3 the fill scrubbed them all = letter soup.</summary>
        public static int FULL_FILL_AVOID_MIN = 4;

        // ── FULL BOARD START STRUCTURE (2026-10-21 Spencer: "there isnt enough seeded words off the start of the level, it
        // just looks like letter salad"). 2 near-words in 99 random letters reads as noise. Now: FULL_START_PLANTS near-words
        // spread over the board + FULL_START_SHORT_WORDS real common 3-letter words left intact — they can't pop (words are
        // 4+ on the full board) but the eye reads them, and each is a stepping stone (CAT -> CART / CATS). ──
        public static int FULL_START_PLANTS      = 6;
        public static int CratesBroken;          // monotonic; the crate goal reads CountAnchoredCells(), this is for stats
        public static int FULL_START_TNT  = 1;   // dynamite letters on every full-board level (2 -> 1, 2026-10-21: levels fell in 1-3 moves)
        public static int FULL_MAX_HIGH   = 2;   // gold K/Q/J/X/Z/V (locked) on the board at once — 8 in one screenshot clogged it
        public static int FULL_MAX_RAIN_GOLD = 2;

        /// <summary>Crates in a mirrored, designed-looking pattern (Royal Match boards are symmetric). Rows 1..ROWS-3.</summary>
        public int PlaceCrates(int count, HashSet<Vector2Int> keep)
        {
            int placed = 0, mid = COLS / 2;
            for (int attempt = 0; attempt < count * 30 && placed < count; attempt++)
            {
                int x = SurvivalRng.Range(0, mid + 1), y = SurvivalRng.Range(1, ROWS - 2);
                var a = new Vector2Int(x, y); var b = new Vector2Int(COLS - 1 - x, y);
                bool pair = a != b;
                if (pair && placed + 2 > count) continue;
                if (keep.Contains(a) || (pair && keep.Contains(b))) continue;
                var da = _board[a.x, a.y]; var db = _board[b.x, b.y];
                if (da == null || db == null || da.IsStone || db.IsStone || da.SpecialKind != SPECIAL_NONE || db.SpecialKind != SPECIAL_NONE) continue;
                foreach (var p in pair ? new[] { a, b } : new[] { a })
                {
                    // NOT anchored (2026-10-21 Spencer: "the crates are not dropping"): an anchored crate held its column up and
                    // left permanent holes beneath it — our board only drops straight down, with no Royal-Match diagonal slide.
                    var d = _board[p.x, p.y]; d.IsStone = true; d.IsAnchored = false; d.RequiredWordLength = 0; d.IsFrozen = false;
                    keep.Add(p); placed++;
                }
            }
            return placed;
        }

        /// <summary>TNT letters: put dynamite on <paramref name="count"/> ordinary letters in the middle band of the board.</summary>
        public int PlaceStartTnt(int count, HashSet<Vector2Int> keep)
        {
            int placed = 0;
            for (int attempt = 0; attempt < count * 30 && placed < count; attempt++)
            {
                var p = new Vector2Int(SurvivalRng.Range(1, COLS - 1), SurvivalRng.Range(2, ROWS - 2));
                var d = _board[p.x, p.y];
                if (d == null || d.IsStone || d.IsFrozen || d.SpecialKind != SPECIAL_NONE || keep.Contains(p)) continue;
                if (LetterData.GetPoints(d.Letter) >= Tile.HIGH_VALUE_POINTS) continue;
                d.SpecialKind = SPECIAL_TNT; keep.Add(p); placed++;
            }
            return placed;
        }
        public static int FULL_START_SHORT_WORDS = 8;
        private static List<string> s_common3;

        /// <summary>Write up to <paramref name="count"/> common 3-letter words intact (across or down) where they spell no
        /// 4+ word with their neighbours. Cells used are added to <paramref name="keep"/>. Returns words placed.</summary>
        public int SeedShortWords(int count, HashSet<Vector2Int> keep)
        {
            if (s_common3 == null)
            {
                s_common3 = new List<string>();
                var ta = Resources.Load<TextAsset>("common_words");
                if (ta != null) foreach (var raw in ta.text.Split('\n')) { var t = raw.Trim().ToUpperInvariant(); if (t.Length == 3 && WordDictionary.IsValidWord(t)) s_common3.Add(t); }
            }
            if (s_common3.Count == 0) return 0;
            SnapshotForRain(out var g, out var mv);
            int placed = 0;
            for (int attempt = 0; attempt < count * 25 && placed < count; attempt++)
            {
                string w = s_common3[SurvivalRng.Range(0, s_common3.Count)];
                bool across = SurvivalRng.Value < 0.6f;
                int x0 = SurvivalRng.Range(0, across ? COLS - 2 : COLS), y0 = SurvivalRng.Range(across ? 0 : 2, ROWS);
                var cells = new Vector2Int[3];
                bool ok = true;
                for (int k = 0; k < 3 && ok; k++)
                {
                    var p = across ? new Vector2Int(x0 + k, y0) : new Vector2Int(x0, y0 - k);   // down = read top -> bottom
                    if (p.x >= COLS || p.y < 0 || p.y >= ROWS || keep.Contains(p) || !mv[p.x, p.y] || _board[p.x, p.y] == null) ok = false;
                    cells[k] = p;
                }
                if (!ok) continue;
                var was = new char[3];
                for (int k = 0; k < 3; k++) { was[k] = g[cells[k].x, cells[k].y]; g[cells[k].x, cells[k].y] = w[k]; }
                for (int k = 0; k < 3 && ok; k++) if (FormsWordAt(g, cells[k].x, cells[k].y)) ok = false;   // no 4+ word with neighbours
                if (!ok) { for (int k = 0; k < 3; k++) g[cells[k].x, cells[k].y] = was[k]; continue; }
                for (int k = 0; k < 3; k++) { _board[cells[k].x, cells[k].y].Letter = w[k]; keep.Add(cells[k]); }
                placed++;
            }
            return placed;
        }

        public bool FormsWordAt(char[,] g, int x, int y, int minLen = -1)
        {
            if (minLen < 0) minLen = FULL_FILL_AVOID_MIN;
            if (g[x, y] == '\0') return false;
            var sb = new System.Text.StringBuilder(7);
            int l = x, r = x;
            while (l - 1 >= 0 && g[l - 1, y] != '\0') l--;
            while (r + 1 < COLS && g[r + 1, y] != '\0') r++;
            for (int s = l; s <= x; s++)
                for (int e = Mathf.Max(x, s + minLen - 1); e <= r && e - s + 1 <= MAX_WORD_LENGTH; e++)
                {
                    sb.Length = 0; for (int k = s; k <= e; k++) sb.Append(g[k, y]);
                    if (WordDictionary.IsValidWord(sb.ToString())) return true;
                }
            int top = y, bot = y;
            while (top + 1 < ROWS && g[x, top + 1] != '\0') top++;
            while (bot - 1 >= 0 && g[x, bot - 1] != '\0') bot--;
            for (int s = top; s >= y; s--)
                for (int e = Mathf.Min(y, s - minLen + 1); e >= bot && s - e + 1 <= MAX_WORD_LENGTH; e--)
                {
                    sb.Length = 0; for (int k = s; k >= e; k--) sb.Append(g[x, k]);
                    if (WordDictionary.IsValidWord(sb.ToString())) return true;
                }
            return false;
        }

        /// <summary>Draw a bag letter for (x,y) that spells no word with its neighbours. Leaves it in <paramref name="g"/>.</summary>
        public char DrawNoWordLetter(char[,] g, int x, int y)
        {
            // LETTER BALANCE (2026-10-21: a screenshot had 14 O's in 99 = 2x English, and 8 gold tiles). Count what is on
            // the board; refuse a letter already over 1.5x its bag share, and gold (locked high-value) past FULL_MAX_HIGH.
            var counts = new int[26]; int filled = 0, high = 0;
            for (int cx = 0; cx < COLS; cx++) for (int cy = 0; cy < ROWS; cy++)
            {
                if (cx == x && cy == y) continue;
                char c = g[cx, cy]; if (c < 'A' || c > 'Z') continue;
                counts[c - 'A']++; filled++;
                if (LetterData.GetPoints(c) >= Tile.HIGH_VALUE_POINTS) high++;
            }
            char L = 'E';
            for (int i = 0; i < FULL_NOWORD_TRIES; i++)
            {
                L = DrawWeightedLetter(); g[x, y] = L;
                bool isHigh = LetterData.GetPoints(L) >= Tile.HIGH_VALUE_POINTS;
                if (isHigh && high >= FULL_MAX_HIGH) continue;
                if (filled >= 12 && counts[L - 'A'] + 1 > 1.5f * LetterShare(L) * (filled + 1) + 1f) continue;
                if (!FormsWordAt(g, x, y)) return L;
            }
            return L;   // very rare: give up and keep the last draw
        }
        private static float LetterShare(char L)
        {
            if (s_letterBag == null) DrawWeightedLetter();
            int n = 0; foreach (var c in s_letterBag) if (c == L) n++;
            return (float)n / Mathf.Max(1, s_letterBag.Length);
        }

        /// <summary>Data only: clear the board and fill EVERY cell with letters that spell no word.</summary>
        public void FillBoardNoWords()
        {
            ClearBoard();
            var g = new char[COLS, ROWS];
            for (int r = 0; r < ROWS; r++)
                for (int c = 0; c < COLS; c++)
                    _board[c, r] = new RulesCellData { Letter = DrawNoWordLetter(g, c, r), Col = c, Row = r, PlayerIndex = -1 };
        }

        /// <summary>After planting near-words: redraw any OTHER cell that now spells a word by accident.</summary>
        public int ScrubAccidentalWords(HashSet<Vector2Int> keep)
        {
            SnapshotForRain(out var g, out var _);
            int fixedCount = 0;
            for (int r = 0; r < ROWS; r++)
                for (int c = 0; c < COLS; c++)
                {
                    var d = _board[c, r];
                    if (d == null || d.Letter == '\0' || d.IsFrozen || d.IsStone || keep.Contains(new Vector2Int(c, r))) continue;
                    if (!FormsWordAt(g, c, r)) continue;
                    d.Letter = DrawNoWordLetter(g, c, r); fixedCount++;
                }
            return fixedCount;
        }

        /// <summary>A common 4-letter word with two neighbouring letters swapped (not itself a word either way).</summary>
        public string PickScrambledPlant(int len, out string word)
        {
            EnsurePlantLists();
            word = null;
            var pool = len >= 5 ? s_plant5 : s_plant4;
            for (int attempt = 0; attempt < 8 && pool.Count > 0; attempt++)
            {
                string w = pool[Mathf.Clamp((int)(SurvivalRng.Value * pool.Count), 0, pool.Count - 1)];
                int i = Mathf.Clamp((int)(SurvivalRng.Value * (w.Length - 1)), 0, w.Length - 2);
                if (w[i] == w[i + 1]) continue;
                var sc = w.ToCharArray(); char t = sc[i]; sc[i] = sc[i + 1]; sc[i + 1] = t;
                string s = new string(sc);
                var rv = s.ToCharArray(); System.Array.Reverse(rv);
                if (WordDictionary.IsValidWord(s) || WordDictionary.IsValidWord(new string(rv))) continue;
                word = w; return s;
            }
            return null;
        }

        public void TryPlantNearWord(char[] row)
        {
            EnsurePlantLists();   // one loader (builds the lists AND the set)
            for (int attempt = 0; attempt < 6; attempt++)
            {
                bool five = SurvivalRng.Value < (SurvivalManager.FullBoardActive ? FULL_PLANT_FIVE_SHARE : PLANT_FIVE_SHARE) && s_plant5.Count > 0;
                var pool = five ? s_plant5 : s_plant4;
                if (pool.Count == 0) return;
                string word = pool[Mathf.Clamp((int)(SurvivalRng.Value * pool.Count), 0, pool.Count - 1)];
                int len = word.Length;

                // A run of real letter slots (no stone, no tall-column gap) long enough for the word.
                var starts = new List<int>();
                for (int c = 0; c + len <= COLS; c++)
                {
                    bool ok = true;
                    for (int k = 0; k < len; k++) { char ch = row[c + k]; if (ch == '\0' || ch == '#') { ok = false; break; } }
                    if (ok) starts.Add(c);
                }
                if (starts.Count == 0) return;
                int start = starts[Mathf.Clamp((int)(SurvivalRng.Value * starts.Count), 0, starts.Count - 1)];

                // Swap one neighbouring pair (different letters) and make sure the result is NOT a word.
                int i = Mathf.Clamp((int)(SurvivalRng.Value * (len - 1)), 0, len - 2);
                if (word[i] == word[i + 1]) continue;
                char[] sc = word.ToCharArray();
                char t = sc[i]; sc[i] = sc[i + 1]; sc[i + 1] = t;
                string scrambled = new string(sc);
                char[] rv = scrambled.ToCharArray(); System.Array.Reverse(rv);
                if (WordDictionary.IsValidWord(scrambled) || WordDictionary.IsValidWord(new string(rv))) continue;

                for (int k = 0; k < len; k++) row[start + k] = sc[k];
                StageStats.RecordPlanted(word);
                WDLog.Log($"[Plant] '{word}' planted as '{scrambled}' at cols {start}-{start + len - 1} (swap {i}<->{i + 1})");
                return;
            }
        }

        public char[] FillBottomRow(TileBag bag)
        {
            char[] letters = new char[COLS];
            // Fertile row (8-16 candidates → pick best by ScoreRowPlayability)
            // is an assist: it serves word-friendly rows instead of raw random
            // draws. Gate behind NoAssistMode so hardest-difficulty runs really
            // get raw bag draws. Without this, "no assist" quietly still biased
            // rows toward playability.
            bool fertility = SurvivalManager.IsSurvivalMode && !SurvivalManager.NoAssistMode;

            // Stone tiles
            float stoneChance = !ROCKS_OFF && SurvivalManager.IsSurvivalMode && !StonesDisabled && SurvivalManager.Instance != null
                ? SurvivalManager.Instance.GetStoneChance() : 0f;
            var stoneColumns = new HashSet<int>();
            for (int col = 0; col < COLS; col++)
                if (stoneColumns.Count < 2 && SurvivalRng.Value < stoneChance)
                    stoneColumns.Add(col);

            if (fertility)
            {
                // CANDIDATE-GENERATION FERTILE ROW
                // Generate multiple candidate rows, simulate placement against the
                // current board, score by ACTUAL playability, pick the best one.
                // Goal: "dangerous but solvable — this row gave me real options"

                // Post-clear boost: generate more candidates for a better row
                bool boosted = SurvivalManager.Instance != null && SurvivalManager.Instance.IsPostClearBoosted;
                int numCandidates = boosted ? 24 : 12;

                char[] bestRow = null;
                int bestScore = -999;

                for (int attempt = 0; attempt < numCandidates; attempt++)
                {
                    char[] candidate = GenerateCandidateRow(bag, stoneColumns);
                    int score = ScoreRowPlayability(candidate, stoneColumns);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRow = candidate;
                    }
                }

                // 2026-09-03: HIGH-VALUE LETTER INJECTION.
                // Best-of-N selection quietly EXCLUDES rare letters: J/X/Q/Z/K/V form far
                // fewer words, so every candidate row containing one scores worse and
                // loses. Across 12-24 attempts they essentially never survive — which is
                // why the gold high-value tiles never appeared on the board. Nothing
                // penalises them explicitly; it is emergent from picking the most
                // "playable" row every time. So seed one deliberately, sometimes.
                // RICH VEINS raises how often a rising row carries a high-value letter.
                float hvChance = RunBuffs.HighValueChance(HIGH_VALUE_ROW_CHANCE);
                if (bestRow != null && hvChance > 0f
                    && SurvivalRng.Value < hvChance)
                {
                    // pick a column that is not a stone and not already high-value
                    var slots = new List<int>();
                    for (int c = 0; c < COLS; c++)
                        if (!stoneColumns.Contains(c) && bestRow[c] != '\0' && bestRow[c] != '#'
                            && LetterData.GetPoints(bestRow[c]) < Tile.HIGH_VALUE_POINTS)
                            slots.Add(c);
                    if (slots.Count > 0)
                    {
                        int col = slots[Mathf.Clamp((int)(SurvivalRng.Value * slots.Count), 0, slots.Count - 1)];
                        int pick = Mathf.Clamp((int)(SurvivalRng.Value * HIGH_VALUE_LETTERS.Length),
                                               0, HIGH_VALUE_LETTERS.Length - 1);
                        bestRow[col] = HIGH_VALUE_LETTERS[pick];
                    }
                }

                if (bestRow != null)
                    System.Array.Copy(bestRow, letters, COLS);
                else
                {
                    // Fallback: random row with basic vowel balance
                    for (int col = 0; col < COLS; col++)
                    {
                        if (stoneColumns.Contains(col)) { letters[col] = '#'; continue; }
                        letters[col] = bag.DrawLetter();
                    }
                }

//                 WDLog.Log($"[RulesEngine] FillBottomRow FERTILE — score={bestScore} row={new string(letters)}");
            }
            else
            {
                // Classic mode: plain random draw with light anti-stacking.
                // Up to 3 retries per column if the drawn letter would create
                // a vertical stack (same letter already in this column).
                // Prevents 3-4 of the same letter accumulating in one column
                // over multiple rises while keeping NoAssistMode's "raw RNG"
                // character mostly intact.
                for (int col = 0; col < COLS; col++)
                {
                    if (stoneColumns.Contains(col)) { letters[col] = '#'; continue; }

                    char drawn = bag.DrawLetter();
                    for (int retry = 0; retry < 3; retry++)
                    {
                        bool conflict = false;
                        for (int r = 0; r < ROWS; r++)
                        {
                            var cell = _board[col, r];
                            if (cell != null && !cell.IsStone && cell.Letter == drawn)
                            { conflict = true; break; }
                        }
                        if (!conflict) break;
                        drawn = bag.DrawLetter();
                    }
                    letters[col] = drawn;
                }

//                 WDLog.Log($"[RulesEngine] FillBottomRow — {new string(letters)}");
            }

            // INSTANT POP: plant a 4-5 letter word ONE SWAP from complete. 2026-10-20 Spencer: "we should
            // just seed more 4 letter and 5 letter word opportunities". The candidate scorer above only
            // values 3-letter words and one-letter-away pairs (a drop-game rule), so it has been steering
            // every row toward 3s — measured: 76 of 83 words in his run were 3 letters.
            if (HandManager.InstantPopMode && SurvivalManager.IsSurvivalMode && SurvivalRng.Value < PLANT_CHANCE)
                TryPlantNearWord(letters);

            // Commit to board — skip empty columns (gaps from tall-column relief)
            for (int col = 0; col < COLS; col++)
            {
                if (letters[col] == '\0') continue; // intentional gap — no tile here

                bool isStone = letters[col] == '#';
                _board[col, 0] = new RulesCellData
                {
                    Letter = letters[col],
                    Col = col,
                    Row = 0,
                    PlayerIndex = -1,
                    IsStone = isStone,
                };
            }

            return letters;
        }

        /// <summary>
        /// <summary>
        /// After a rising row, detect any NEW words that weren't on the board before.
        /// These words are primed as neutral (playerIndex = -1) — free bonus for both players!
        /// Returns the list of newly primed words.
        /// </summary>
        /// <summary>
        /// Scans the board for words after a rising row and registers them as scored
        /// so they don't get double-counted, but does NOT prime them.
        /// The player must create words themselves — rising rows just provide material.
        /// </summary>
        // RegisterRiseRowWords REMOVED — was permanently blocking board words from
        // ever being scored/primed. Replaced by RegisterWordsAtRow which only
        // registers words touching the specified row (targeted, not whole-board).

        /// <summary>
        /// Registers all valid words touching a specific row as scored, so they
        /// don't auto-prime. Used after rising rows to suppress the new bottom
        /// row from immediately priming. Words become primeable when a player
        /// action purges their scored keys (drop/gravity at those cells).
        /// </summary>
        public void RegisterWordsAtRow(int row)
        {
            int registered = 0;
            for (int col = 0; col < COLS; col++)
            {
                if (_board[col, row] == null) continue;
                var words = FindNewWords(col, row);
                for (int i = 0; i < words.Count; i++)
                {
                    string key = words[i].Word + "|" + words[i].CellKey;
                    if (_scoredWordKeys.Add(key))
                        registered++;
                    _riseSeededKeys.Add(key);   // blocked for AUTO-priming only — a swipe may still claim it
                }
            }
            // if (registered > 0)
//                 WDLog.Log($"[RulesEngine] RegisterWordsAtRow({row}): registered {registered} word(s)");
        }

        /// <summary>
        /// Detects and primes words passing through a specific cell (used by auto-drops).
        /// Only words physically containing (col, row) are primed.
        /// </summary>
        public List<RulesWordMatch> DetectAndPrimeAtCell(int col, int row)
        {
            var newWords = new List<RulesWordMatch>();
            var justPrimedIds = new HashSet<int>();
            var seedCells = new List<Vector2Int> { new Vector2Int(col, row) };
            var allWords = ScanSeedCellsOnly(seedCells);

            for (int i = 0; i < allWords.Count; i++)
            {
                string key = allWords[i].Word + "|" + allWords[i].CellKey;
                if (_scoredWordKeys.Contains(key)) continue;

                var match = allWords[i];
                int score = CalculateWordScore(match.Word, match.WildLetterIndices);
                match.Score = score;

                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId = _primedRegistry.AddPrimedWord(
                    match.Word, match.Cells, -1, _globalTurn, expiresOn, score,
                    isGold: false, hasHighValueLetter: HasHighValueLetter(match));
                if (primedId >= 0) justPrimedIds.Add(primedId);   // -1 = rejected by the branch gate

                _scoredWordKeys.Add(key);

                newWords.Add(match);
//                 WDLog.Log($"[RulesEngine] Auto-drop created word '{match.Word}' ({score} pts) at ({col},{row}) — PRIMED!");
            }

            // if (newWords.Count > 0)
//                 WDLog.Log($"[RulesEngine] Auto-drop primed {newWords.Count} new word(s)!");
            if (justPrimedIds.Count > 0)
                ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);

            return newWords;
        }

        // ── SWIPE-TO-CLAIM (2026-10-19 Spencer) ───────────────────────────────────
        /// <summary>Master switch for the swipe-to-claim mechanic.</summary>
        public static bool SWIPE_WORDS_ENABLED = true;

        /// <summary>Minimum length for a SWIPED word.
        ///
        /// REVISED 2026-10-19 from 4 down to 3, same day it was written. The 4 was my call on
        /// anti-spam grounds and it was wrong — Spencer swiped MAD and it silently refused.
        ///
        /// The original reasoning conflated ABUNDANT with EFFICIENT. 3-letter words are
        /// everywhere (66 of the 75 accidental words on a random board), but MEASURED:
        ///   • allowing 3 raises the best-swipe-available from 29.8 to 31.8 pts/move (+2.1),
        ///     because the best word on the board is usually 4+ anyway;
        ///   • a lone 3-letter word scores ~6 pts against building's 46 — SEVEN TIMES worse;
        ///   • across 220 boards there was never one where 3-letter was the only option.
        /// Each swipe costs a move, so a 6-point claim is self-limiting. The short-word-spam
        /// problem was flat bonuses making them efficient, and that was already fixed on
        /// 2026-09-09 by cutting BREAKER_BONUS 10 -> 2.
        ///
        /// MIN_WORD_LENGTH (3) is the engine-wide floor; this matches it deliberately, so
        /// "it is a word in the dictionary" is the only rule a player has to learn.
        ///
        /// DERIVED, not a copy. 2026-10-20: this was its own `= 3`, so when LevelRules raised
        /// MIN_WORD_LENGTH to 4 the swipe never heard — on '4+ Letters Only' a swiped 'PEE'
        /// ARMED. Spencer: "3 letter words are still counting when i click and drag across".</summary>
        public static int SWIPE_MIN_LENGTH => EffectiveMinWordLength;   // full board: 4+ (2026-10-21)

        /// <summary>Whether a swipe may run diagonally. OFF — Spencer 2026-10-19: "you
        /// shouldn't be able to make diagonal words". Claude added 8 directions unprompted;
        /// "straight lines only" was answering straight-line-vs-Boggle-path, not endorsing
        /// diagonals. Swipes are horizontal or vertical, in EITHER direction — which is what
        /// the original ask ("shouldn't words work backwards too?") actually needed.</summary>
        public static bool SWIPE_ALLOW_DIAGONAL = false;

        /// <summary>Validates an explicit, player-selected straight-line path and returns the
        /// word match for it, or null with a reason. Scoring and priming are deliberately NOT
        /// done here — the caller routes the match through HandManager.PrimeAndPresentWords so
        /// swipes and swaps share one scoring pipeline (chain multiplier, echo bonus, gold
        /// multiplier, scored-key registration). A second scoring path is the documented
        /// desync landmine.
        ///
        /// FOUR directions by default — horizontal and vertical, either way round. The AUTO
        /// scanner reads only two (L-to-R, top-to-bottom), so the extra two are exactly the
        /// backwards words it cannot see, which is what Spencer asked for. Auto-detecting
        /// those would double the accidental words on a random board (21 -> 42, measured) and
        /// arm webs nobody intended; a word the PLAYER traces is not an accident, so intent is
        /// the filter. Diagonals are OFF — see SWIPE_ALLOW_DIAGONAL.</summary>
        public RulesWordMatch BuildExplicitMatch(List<Vector2Int> cells, out string reject)
        {
            reject = null;
            if (cells == null || cells.Count < SWIPE_MIN_LENGTH) { reject = "too short"; return null; }

            // Straight line, one of the 8 compass directions, every step the same.
            int dc = cells[1].x - cells[0].x;
            int dr = cells[1].y - cells[0].y;
            if (Mathf.Abs(dc) > 1 || Mathf.Abs(dr) > 1 || (dc == 0 && dr == 0))
            { reject = "not a straight line"; return null; }
            if (!SWIPE_ALLOW_DIAGONAL && dc != 0 && dr != 0)
            { reject = "diagonal"; return null; }
            for (int i = 1; i < cells.Count; i++)
                if (cells[i].x - cells[i - 1].x != dc || cells[i].y - cells[i - 1].y != dr)
                { reject = "not a straight line"; return null; }

            var sb = new System.Text.StringBuilder(cells.Count);
            for (int i = 0; i < cells.Count; i++)
            {
                var cell = GetCell(cells[i].x, cells[i].y);
                if (cell == null) { reject = "empty cell"; return null; }
                // Same exclusions TryBoardSwap applies — a swipe must not claim a tile a swap
                // could not touch, or the two verbs disagree about what the board contains.
                // ICE IS THE EXCEPTION. 2026-10-20 Spencer: "ice should be inside the word to melt.
                // detonating it thaws it". Ice can't MOVE, but its letter can be READ — the auto
                // scanner already arms words across ice. Refusing it here meant a swipe could never
                // thaw ice, the one thing ice asks you to do (FALSE over a frozen F, refused twice).
                if (cell.IsStone || cell.IsWild) { reject = "blocked tile"; return null; }
                if (cell.IsSwapRefill || cell.IsEditRefill || cell.IsWildRefill) { reject = "blocked tile"; return null; }
                if (_primedRegistry != null
                    && _primedRegistry.GetPrimedWordsContaining(cells[i]).Count > 0)
                { reject = "already armed"; return null; }
                sb.Append(cell.Letter);
            }

            string word = sb.ToString();

            // ACCEPT THE PATH IN EITHER ORDER. 2026-10-19 — this is the whole point of the
            // feature and the first build missed it. The words a player can actually FIND by
            // eye are precisely the ones the auto-scanner cannot see, i.e. the backwards and
            // diagonal ones; a left-to-right word is already primed before they could trace it.
            // But a player reading a backwards word still drags left-to-right, which spells it
            // reversed. So try the trace order, then its reverse, and keep whichever is real.
            // Cells are reversed alongside the word because downstream code maps Word[i] to
            // Cells[i] (WildLetterIndices, the primed glow, CellKey).
            var ordered = new List<Vector2Int>(cells);
            if (!WordDictionary.IsIntentionalWord(word))   // a swipe is always on purpose — the wider list counts (LOIN, HONK)
            {
                char[] rev = word.ToCharArray();
                System.Array.Reverse(rev);
                string back = new string(rev);
                if (!WordDictionary.IsIntentionalWord(back))
                { reject = $"neither '{word}' nor '{back}' is a word"; return null; }
                word = back;
                ordered.Reverse();
            }

            var match = new RulesWordMatch
            {
                Word      = word,
                Cells     = ordered,
                Direction = (dr == 0) ? WordDirection.Horizontal
                          : (dc == 0) ? WordDirection.Vertical
                                      : WordDirection.Diagonal,
                Score     = 0,
            };

            // CellKey SORTS its cells before hashing, so a reversed claim over cells that have
            // already scored collides with the existing key and is refused here with no extra
            // rule — e.g. swiping TAC across the letters a CAT already used.
            string mkey = word + "|" + match.CellKey;
            if (IsClaimBlocked(mkey))
            { reject = "already claimed"; return null; }
            return match;
        }
        /// <summary>Scans the ENTIRE board and primes every valid word that isn't
        /// already primed/scored. Used by the Jester Hat shuffle so a rearrangement
        /// that happens to form words primes them (the booster's upside). Mirrors
        /// DetectAndPrimeAtCell but board-wide. Turn-neutral (uses the current
        /// _globalTurn, does NOT tick). Returns the newly-primed words so the caller
        /// can apply the glow.</summary>
        public List<RulesWordMatch> PrimeAllWordsOnBoard()
        {
            var newWords = new List<RulesWordMatch>();
            var justPrimedIds = new HashSet<int>();
            var allWords = ScanEntireBoard();

            for (int i = 0; i < allWords.Count; i++)
            {
                string key = allWords[i].Word + "|" + allWords[i].CellKey;
                if (_scoredWordKeys.Contains(key)) continue; // already primed or scored

                var match = allWords[i];
                int score = CalculateWordScore(match.Word, match.WildLetterIndices);
                match.Score = score;

                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId = _primedRegistry.AddPrimedWord(
                    match.Word, match.Cells, -1, _globalTurn, expiresOn, score,
                    isGold: false, hasHighValueLetter: HasHighValueLetter(match));
                justPrimedIds.Add(primedId);

                _scoredWordKeys.Add(key);
                newWords.Add(match);
            }

            if (justPrimedIds.Count > 0)
                ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);

            return newWords;
        }

        /// <summary>
        /// Resolves any uncommitted wilds board-wide and primes ONLY the words that run
        /// through a wild that JUST resolved this call — nothing else. Used by the rising
        /// row so a wild the rise slides into position (e.g. P-wild-D → PAD, X-I-wild → FIX)
        /// lights up immediately, WITHOUT priming incidental all-real-letter words the rise
        /// happens to align (those must come from a player action — the SEAR case). Works by
        /// passing an EMPTY seed list to ScanSeedCellsOnly: it seeds only the wilds it
        /// resolved, so a rise with no newly-resolvable wild primes nothing. Turn-neutral.
        /// Returns the newly-primed words so the caller can apply the glow. 2026-06-08.
        /// </summary>
        public List<RulesWordMatch> PrimeWildWordsAfterRise()
        {
            var newWords = new List<RulesWordMatch>();
            var justPrimedIds = new HashSet<int>();
            var wildWords = ScanSeedCellsOnly(new List<Vector2Int>());

            for (int i = 0; i < wildWords.Count; i++)
            {
                string key = wildWords[i].Word + "|" + wildWords[i].CellKey;
                if (_scoredWordKeys.Contains(key)) continue; // already primed or scored

                var match = wildWords[i];
                int score = CalculateWordScore(match.Word, match.WildLetterIndices);
                match.Score = score;

                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId = _primedRegistry.AddPrimedWord(
                    match.Word, match.Cells, -1, _globalTurn, expiresOn, score,
                    isGold: false, hasHighValueLetter: HasHighValueLetter(match));
                justPrimedIds.Add(primedId);

                _scoredWordKeys.Add(key);
                newWords.Add(match);
            }

            if (justPrimedIds.Count > 0)
                ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);

            return newWords;
        }

        /// <summary>
        /// After a rising row shift, remap _scoredWordKeys to new cell positions.
        /// Only words that were ALREADY scored keep their "scored" status at the
        /// new coordinates.  Words that coincidentally exist on the board but were
        /// never scored by a player are NOT registered — this prevents the
        /// ghost-registration bug where unscored words became un-detectable.
        /// </summary>
        public void RebuildScoredKeysAfterRise()
        {
            // Rising rows shift every tile up by 1 row.
            // Remap each existing scored key: y → y+1.
            // Drop any key whose cell would be at or above ROWS (tile was crushed).
            var oldKeys  = new List<string>(_scoredWordKeys);
            var oldSeeded = new HashSet<string>(_riseSeededKeys);
            _scoredWordKeys.Clear();
            _riseSeededKeys.Clear();

            int remapped = 0;
            int dropped  = 0;

            for (int k = 0; k < oldKeys.Count; k++)
            {
                string key = oldKeys[k];
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;

                string wordPart = key.Substring(0, pipeIdx);
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');

                bool valid = true;
                var newCoords = new System.Text.StringBuilder();

                for (int i = 0; i < coords.Length; i++)
                {
                    string[] xy = coords[i].Split(',');
                    if (xy.Length != 2) { valid = false; break; }

                    int x, y;
                    if (!int.TryParse(xy[0], out x) || !int.TryParse(xy[1], out y))
                    { valid = false; break; }

                    int newY = y + 1;
                    if (newY >= ROWS) { valid = false; break; } // crushed off top

                    if (i > 0) newCoords.Append(';');
                    newCoords.Append(x).Append(',').Append(newY);
                }

                if (valid)
                {
                    string newKey = wordPart + "|" + newCoords.ToString();
                    _scoredWordKeys.Add(newKey);
                    // The exemption has to ride along, or the word becomes unclaimable the
                    // instant it rises one row.
                    if (oldSeeded.Contains(key)) _riseSeededKeys.Add(newKey);
                    remapped++;
                }
                else
                {
                    dropped++;
                }
            }

//             WDLog.Log($"[RulesEngine] RebuildScoredKeysAfterRise — remapped {remapped} keys, dropped {dropped} (crushed/invalid).");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORING (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Calculates the score for a word.
        /// Formula: sum of letter point values × length multiplier.
        ///   3 letters → ×1
        ///   4 letters → ×1.5 (rounded to int)
        ///   5+ letters → ×2
        /// </summary>
        public static int CalculateWordScore(string word)
        {
            return CalculateWordScore(word, null);
        }

        /// <summary>
        /// Wild-aware scoring. Positions listed in wildIndices contribute 0 letter
        /// points but still count toward the length multiplier — the wild "earned"
        /// the length, just not the tile value.
        /// </summary>
        /// <summary>Instant-pop length multipliers for 3, 4, 5, 6, 7+ letters. See CalculateWordScore.</summary>
        public static float[] INSTANT_LEN_MULT = { 0.4f, 1.5f, 3f, 5f, 8f };

        public static int CalculateWordScore(string word, List<int> wildIndices)
        {
            if (string.IsNullOrEmpty(word)) return 0;

            int raw = 0;
            for (int i = 0; i < word.Length; i++)
            {
                if (wildIndices != null && wildIndices.Contains(i)) continue;
                raw += LetterData.GetPoints(word[i]);
            }

            // Triangular length multiplier — rewards longer words exponentially.
            // Survival uses a steeper 7-letter tier (Phase 11+ triangularity)
            // so SPARKLE-tier words feel like a marquee moment instead of just
            // "slightly more than a 6-letter." Other modes keep the legacy
            // curve (Level/Daily/Classic don't have the same time pressure so
            // the bump is Survival-specific).
            float multiplier;
            bool isSurvival = SurvivalManager.IsSurvivalMode;
            switch (word.Length)
            {
                case 3:  multiplier = 1.0f; break;
                case 4:  multiplier = 1.5f; break;
                case 5:  multiplier = 2.5f; break;
                case 6:  multiplier = 4.0f; break;
                default: multiplier = word.Length >= 7
                    ? (isSurvival ? 7.0f : 6.0f)
                    : 1.0f; break;
            }

            // INSTANT POP: short words keep you alive, long words win levels. 2026-10-20 Spencer
            // ("sounds good"). Measured in his 11:07 run: 26 of 30 words on normal levels were 3
            // letters and nearly every swap made one — see, swap, pop, no thought. The 4+-only level
            // was the first where he PLANNED (9 of 17 swaps were setup). So 3-letter words still pop
            // (they clear space) but pay little; each extra letter pays a lot more. Letter points
            // already grow with length, so 3 -> 4 letters is roughly 5x the score.
            if (HandManager.InstantPopMode)
            {
                int n = word.Length;
                multiplier = n <= 3 ? INSTANT_LEN_MULT[0]
                           : n >= 7 ? INSTANT_LEN_MULT[4]
                           : INSTANT_LEN_MULT[n - 3];
            }

            // Rare-letter premium (Survival-only, Phase 11+): words that
            // include at least one of Q/Z/X/J get a flat ×2 on top of the
            // length mult. Flat multiplier per word — QUIZ doesn't get
            // stacked bonuses for having Q and Z both. Deliberate play
            // pattern: "can I make a Q word?"
            if (isSurvival)
            {
                string upper = word.ToUpperInvariant();
                if (upper.IndexOfAny(RARE_LETTERS) >= 0)
                    multiplier *= 2.0f;
            }

            int score = Mathf.RoundToInt(raw * multiplier);

            if (wildIndices != null && wildIndices.Count > 0)
            {
//                 WDLog.Log($"[RulesEngine] CalculateWordScore('{word}'): " +
                          // $"raw={raw} (wilds={wildIndices.Count} zeroed) × {multiplier:F1} = {score}");
            }
            else
            {
//                 WDLog.Log($"[RulesEngine] CalculateWordScore('{word}'): " +
                          // $"raw={raw} × {multiplier:F1} = {score}");
            }

            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PROCESS DROP — Full resolution logic (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Drops a tile into the logical board, finds words, scores them, primes them,
        /// checks for primed word triggers, explodes triggered words, applies gravity,
        /// and chains until stable.
        ///
        /// Words primed in the CURRENT resolution step cannot be triggered in the
        /// same resolution — tracked via _justPrimedThisResolution.
        ///
        /// Returns a ResolutionResult summarizing everything that happened.
        /// </summary>
        public ResolutionResult ProcessDrop(int col, char letter, int playerIndex)
        {
            var result = new ResolutionResult
            {
                Col         = col,
                Letter      = letter,
                PlayerIndex = playerIndex,
            };

            // 1. Find target row
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0)
            {
//                 WDLog.Log($"[RulesEngine] ProcessDrop: col {col} is full — cannot place.");
                return result;
            }

            result.Row = targetRow;

            // 2. Place tile in board data
            var cellData = new RulesCellData
            {
                Letter      = char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
            };
            _board[col, targetRow] = cellData;

//             WDLog.Log($"[RulesEngine] ProcessDrop: placed '{letter}' at ({col},{targetRow}) " +
                      // $"player={playerIndex}");

            // 3. Emit TileDropped event
            OnTileDropped?.Invoke(new TileDroppedEvent
            {
                Col         = col,
                Row         = targetRow,
                Letter      = char.ToUpper(letter),
                PlayerIndex = playerIndex,
            });

            // 4. Expiry moved to AFTER resolution (see step 8 below).
            // This gives the player one last chance to detonate a primed word
            // on the turn it would expire — feels much fairer.

            // 5. Resolution chain loop
            // Track all primed word IDs created during THIS entire resolution
            // They cannot be triggered within the same resolution.
            HashSet<int> justPrimedThisResolution = new HashSet<int>();

            // Reset big-moment splash gate for this legacy resolution path too.
            _splashFiredThisResolution = false;

            int chainStep  = 0;
            int totalScore = 0;
            int baseScoreAccum = 0;
            int chainBonusAccum = 0;
            int detonationBonusAccum = 0;
            bool keepChaining = true;
            var localSeedCells = new List<Vector2Int> { new Vector2Int(col, targetRow) };

            while (keepChaining && chainStep < MAX_CHAIN_DEPTH)
            {
                keepChaining = false;

                // 5a. Seed-cell scan — only words containing the drop/gravity position
                List<RulesWordMatch> allWords = ScanSeedCellsOnly(localSeedCells);

                // 5b. Remove substring words — only keep longest per line.
                // If TAMER exists, remove TAM and TAME from same cells.
                allWords = FilterSubstringWords(allWords);

                // 5c. Filter to only NEW (unscored) words
                List<RulesWordMatch> newWords = new List<RulesWordMatch>();
                for (int i = 0; i < allWords.Count; i++)
                {
                    string key = allWords[i].Word + "|" + allWords[i].CellKey;
                    if (!_scoredWordKeys.Contains(key))
                        newWords.Add(allWords[i]);
                }

                if (newWords.Count == 0)
                {
                    break;
                }

                // Emit ChainStep event
                OnChainStep?.Invoke(new ChainStepEvent
                {
                    StepIndex     = chainStep,
                    NewWordsFound = newWords.Count,
                });

                // if (chainStep > 0)
//                     WDLog.Log($"[RulesEngine] CHAIN step {chainStep}: {newWords.Count} new word(s)!");

                // 5c. Score, prime, and check triggers for each new word
                // Collect all cells that need to be exploded this step
                HashSet<int> primedIdsToExplode = new HashSet<int>();
                List<PrimedTriggeredEvent> triggeredEvents = new List<PrimedTriggeredEvent>();

                // Cluster bonus: when multiple primed words trigger together in one
                // step (connected-cluster detonation), boost each word's effective
                // chain depth by (count - 1). Fixes the signature-spectacle gap where
                // 4 primed words firing simultaneously would otherwise score at
                // chainStep=0 with no multiplier, making huge visual moments feel flat.
                int effectiveChainStep = chainStep + Mathf.Max(0, newWords.Count - 1);

                for (int w = 0; w < newWords.Count; w++)
                {
                    RulesWordMatch match = newWords[w];
                    string key = match.Word + "|" + match.CellKey;
                    _scoredWordKeys.Add(key);

                    // Calculate score with chain bonus + gold bonus + echo
                    //
                    // Chain bonus is MULTIPLICATIVE on the word's base score — a long
                    // word formed mid-chain should pay substantially more than the
                    // same word formed safely. Caps at depth 3 (2.5x) so absurd
                    // chains still feel bounded. Fixes the Schell-triangularity gap
                    // where risky chain plays barely beat safe long-word drops.
                    int baseScore  = CalculateWordScore(match.Word, match.WildLetterIndices);
                    float chainMult = (effectiveChainStep > 0)
                        ? 1f + Mathf.Min(effectiveChainStep, CHAIN_DEPTH_SCALE_CAP) * 0.5f
                        : 1f;
                    int chainBoosted = Mathf.RoundToInt(baseScore * chainMult);
                    int echoBonus  = ConsumeEchoBonus(match.Word, playerIndex);
                    bool isGoldWord = HasGoldTile(match); // check BEFORE consuming
                    int bonusMult  = ConsumeGoldAndGetMultiplier(match);
                    int finalScore = (chainBoosted + echoBonus) * bonusMult;
                    // Reporting fields expect a separate chainBonus number.
                    int chainBonus = chainBoosted - baseScore;
                    match.Score    = finalScore;
                    totalScore    += finalScore;
                    baseScoreAccum += baseScore;
                    chainBonusAccum += chainBonus;

                    WDLog.Log($"[RulesEngine] Scored '{match.Word}': base={baseScore}" +
                              (chainBonus > 0 ? $" +chain({chainBonus}, effStep={effectiveChainStep})" : "") +
                              (echoBonus > 0 ? $" +echo({echoBonus})" : "") +
                              $" = {finalScore} pts  [rawStep={chainStep}, clusterSize={newWords.Count}]");

                    // Emit WordScored
                    var scoredEvt = new WordScoredEvent
                    {
                        Word        = match.Word,
                        Cells       = new List<Vector2Int>(match.Cells),
                        BaseScore   = baseScore,
                        FinalScore  = finalScore,
                        PlayerIndex = playerIndex,
                        ChainStep   = chainStep,
                    };
                    result.ScoredWords.Add(scoredEvt);
                    OnWordScored?.Invoke(scoredEvt);

                    // Prime the word — gold words get gold primed status
                    int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                    int primedId  = _primedRegistry.AddPrimedWord(
                        match.Word,
                        match.Cells,
                        playerIndex,
                        _globalTurn,
                        expiresOn,
                        finalScore,
                        isGoldWord,
                        HasHighValueLetter(match));

                    justPrimedThisResolution.Add(primedId);

                    var primedEvt = new WordPrimedEvent
                    {
                        Word          = match.Word,
                        Cells         = new List<Vector2Int>(match.Cells),
                        PlayerIndex   = playerIndex,
                        PrimedOnTurn  = _globalTurn,
                        ExpiresOnTurn = expiresOn,
                        PrimedWordId  = primedId,
                    };
                    result.PrimedWords.Add(primedEvt);
                    OnWordPrimed?.Invoke(primedEvt);
                    // 2026-05-30: PlayTilePrimed removed here — was double-
                    // firing with GameVisualBridge.cs:603 which also plays
                    // the primed sound for every word being scored/primed.
                    // RulesEngine is the gameplay state machine, shouldn't
                    // play audio directly — that's the visual bridge's job.

                    // Check if any cell in this new word overlaps an EXISTING primed word
                    // (not one primed in THIS resolution)
                    for (int c = 0; c < match.Cells.Count; c++)
                    {
                        Vector2Int cell = match.Cells[c];
                        List<PrimedWordRegistry.PrimedWord> overlapping =
                            _primedRegistry.GetPrimedWordsContaining(cell);

                        for (int p = 0; p < overlapping.Count; p++)
                        {
                            PrimedWordRegistry.PrimedWord pw = overlapping[p];

                            // Skip if this primed word was created in THIS resolution
                            if (justPrimedThisResolution.Contains(pw.Id))
                                continue;

                            // Skip if already queued for explosion
                            if (primedIdsToExplode.Contains(pw.Id))
                                continue;

                            primedIdsToExplode.Add(pw.Id);

//                             WDLog.Log($"[RulesEngine] TRIGGER! New word '{match.Word}' " +
                                      // $"at cell ({cell.x},{cell.y}) overlaps primed " +
                                      // $"'{pw.Word}' (id={pw.Id})");

                            var trigEvt = new PrimedTriggeredEvent
                            {
                                TriggeredWord    = pw.Word,
                                TriggeredCells   = new List<Vector2Int>(pw.Cells),
                                TriggerWord      = match.Word,
                                OverlapCell      = cell,
                                OwnerPlayerIndex = pw.OwnerPlayer,
                                PrimedWordId     = pw.Id,
                            };
                            triggeredEvents.Add(trigEvt);
                            result.TriggeredPrimedWords.Add(trigEvt);
                            OnPrimedTriggered?.Invoke(trigEvt);
                        }
                    }
                }

                // 5c-overlap. Overlap Fuse Extension for non-triggered existing primed words
                {
                    HashSet<int> alreadyExtended = new HashSet<int>();
                    foreach (int newId in justPrimedThisResolution)
                    {
                        var newPw = _primedRegistry.GetById(newId);
                        if (newPw == null || newPw.Cells == null) continue;
                        for (int p = 0; p < _primedRegistry.Count; p++)
                        {
                            var oldPw = _primedRegistry.GetByIndex(p);
                            if (oldPw == null) continue;
                            if (justPrimedThisResolution.Contains(oldPw.Id)) continue;
                            if (primedIdsToExplode.Contains(oldPw.Id)) continue; // already detonating
                            if (alreadyExtended.Contains(oldPw.Id)) continue;

                            bool overlaps = false;
                            for (int c = 0; c < newPw.Cells.Count && !overlaps; c++)
                                for (int d = 0; d < oldPw.Cells.Count && !overlaps; d++)
                                {
                                    int ddx = Mathf.Abs(newPw.Cells[c].x - oldPw.Cells[d].x);
                                    int ddy = Mathf.Abs(newPw.Cells[c].y - oldPw.Cells[d].y);
                                    if (ddx + ddy <= 1) overlaps = true;
                                }

                            if (overlaps)
                            {
                                if (oldPw.OverlapFuseBonusGranted >= MAX_OVERLAP_FUSE_BONUS) continue;

                                oldPw.ExpiresOnTurn += OVERLAP_FUSE_EXTENSION;
                                oldPw.OverlapFuseBonusGranted += OVERLAP_FUSE_EXTENSION;
                                alreadyExtended.Add(oldPw.Id);
                                RefreshPrimedWordTiles(oldPw, _globalTurn);
                                WDLog.Log($"[OverlapFuse] Legacy: '{newPw.Word}' touched '{oldPw.Word}' " +
                                          $"→ +{OVERLAP_FUSE_EXTENSION} fuse " +
                                          $"(expires={oldPw.ExpiresOnTurn}, " +
                                          $"granted={oldPw.OverlapFuseBonusGranted}/{MAX_OVERLAP_FUSE_BONUS})");
                            }
                        }
                    }
                }

                if (justPrimedThisResolution.Count > 0)
                    ResetExistingPrimedWordsForNewPrime(justPrimedThisResolution, _globalTurn);

                // 5c-b. Expand triggers to connected primed group
                if (primedIdsToExplode.Count > 0)
                {
                    var connectedGroup = _primedRegistry.FindConnectedGroup(
                        primedIdsToExplode, justPrimedThisResolution);
                    for (int g = 0; g < connectedGroup.Count; g++)
                    {
                        var pw = connectedGroup[g];
                        if (primedIdsToExplode.Contains(pw.Id)) continue;
                        primedIdsToExplode.Add(pw.Id);
//                         WDLog.Log($"[PrimedChain] Legacy path: chain-connected '{pw.Word}' (id={pw.Id})");
                    }
                }

                // 5d. Explode all triggered primed words
                if (primedIdsToExplode.Count > 0)
                {
                    keepChaining = true; // gravity may create new words

                    // Collect all cells to remove
                    HashSet<Vector2Int> allCellsToRemove = new HashSet<Vector2Int>();

                    int detonationBonus = 0;
                    int longestPrimedWord = 0;
                    var primedSplashSources = new List<List<Vector2Int>>();
                    // Complete record of every primed word that blows up here (trigger +
                    // connected group + splash) for objective tracking. 2026-06-08.
                    var explodedWords = new List<ExplodedWordInfo>();

                    foreach (int pid in primedIdsToExplode)
                    {
                        PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                        if (pw == null) continue;

                        explodedWords.Add(new ExplodedWordInfo { Word = pw.Word, OwnerPlayerIndex = pw.OwnerPlayer });

                        if (pw.Word.Length > longestPrimedWord)
                            longestPrimedWord = pw.Word.Length;
                        if (pw.Cells != null && pw.Cells.Count > 0)
                            primedSplashSources.Add(new List<Vector2Int>(pw.Cells));

                        for (int c = 0; c < pw.Cells.Count; c++)
                            allCellsToRemove.Add(pw.Cells[c]);

                        // Detonation bonus: base + heat fuse + chain-depth scaling
                        int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                        int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                        // BIGGER BLAST scales the re-score; SHORT FUSE raises the flat floor.
                int rawBonus = Mathf.RoundToInt(pw.Score * RunBuffs.DetonationMultiplier(DETONATION_SCORE_MULTIPLIER))
                             + RunBuffs.BreakerBonus(BREAKER_BONUS) + heatBonus;

                        // Evaluate multiplier at depth+1 so the opening detonation is 2x,
                        // not a flat 1x. The triangular cap still applies inside the helper.
                        float chainMultiplier = TriangularChainMultiplier(chainStep + 1);
                        float goldMultiplier = pw.IsGold ? RunBuffs.GoldMultiplier(2f) : 1f;   // GOLDEN TOUCH // balanced: same 2x as normal gold scoring // gold primed words detonate for 3x
                        int bonus = Mathf.RoundToInt(rawBonus * chainMultiplier * goldMultiplier);

                        detonationBonus += bonus;
//                         WDLog.Log($"[RulesEngine] DETONATION BONUS: '{pw.Word}' explodes for +{bonus} pts " +
                                  // $"(base={BREAKER_BONUS} heat={heatBonus} survived={survivedTurns} chain={chainStep} x{chainMultiplier:F1})");

                        // Remove from registry
                        _primedRegistry.RemovePrimedWord(pid);
                        justPrimedThisResolution.Remove(pid);
                    }

                    totalScore += detonationBonus;
                    detonationBonusAccum += detonationBonus;

                    if (!_splashFiredThisResolution
                        && BigMomentSplashEnabled       // 2026-09-10: off for the armed-web game
                        && JunkSplashEnabled            // master splash switch — off for splashOff tutorial levels
                        && SurvivalManager.IsSurvivalMode
                        && (chainStep >= 2 || longestPrimedWord >= 6 || primedIdsToExplode.Count >= 2))
                    {
                        _splashFiredThisResolution = true; // ONE splash per resolution
                        int splashBaseBonus = 0;
                        int splashPrimedBonus = 0;
                        var splashPrimedIds = new HashSet<int>();

                        foreach (var cells in primedSplashSources)
                        {
                            if (cells == null || cells.Count == 0) continue;

                            int minCol = int.MaxValue, maxCol = int.MinValue;
                            int minRow = int.MaxValue, maxRow = int.MinValue;
                            foreach (var c in cells)
                            {
                                if (c.x < minCol) minCol = c.x;
                                if (c.x > maxCol) maxCol = c.x;
                                if (c.y < minRow) minRow = c.y;
                                if (c.y > maxRow) maxRow = c.y;
                            }

                            bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                            if (vertical)
                            {
                                int sweepCol = minCol;
                                for (int row = 0; row < ROWS; row++)
                                {
                                    var cell = new Vector2Int(sweepCol, row);
                                    if (allCellsToRemove.Contains(cell)) continue;
                                    if (!InBounds(sweepCol, row) || _board[sweepCol, row] == null) continue;
                                    if (_board[sweepCol, row].IsFrozen) continue; // STRICT ICE: collateral splash never clears frozen tiles — only a word detonation does. 2026-06-23

                                    var data = _board[sweepCol, row];
                                    splashBaseBonus += LetterData.GetPoints(data.Letter) * (_bonusCells[sweepCol, row] ? 2 : 1);

                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var hitPw in primedHere) splashPrimedIds.Add(hitPw.Id);

                                    allCellsToRemove.Add(cell);
                                }
                            }
                            else
                            {
                                int sweepRow = minRow;
                                // Loop var renamed from 'col' — that name shadows the
                                // ProcessDrop(int col, ...) parameter and causes CS0136.
                                for (int sc = 0; sc < COLS; sc++)
                                {
                                    var cell = new Vector2Int(sc, sweepRow);
                                    if (allCellsToRemove.Contains(cell)) continue;
                                    if (!InBounds(sc, sweepRow) || _board[sc, sweepRow] == null) continue;
                                    if (_board[sc, sweepRow].IsFrozen) continue; // STRICT ICE: collateral splash never clears frozen tiles — only a word detonation does. 2026-06-23

                                    var data = _board[sc, sweepRow];
                                    splashBaseBonus += LetterData.GetPoints(data.Letter) * (_bonusCells[sc, sweepRow] ? 2 : 1);

                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var hitPw in primedHere) splashPrimedIds.Add(hitPw.Id);

                                    allCellsToRemove.Add(cell);
                                }
                            }
                        }

                        foreach (int splashPid in splashPrimedIds)
                        {
                            var splashPw = _primedRegistry.GetById(splashPid);
                            if (splashPw == null) continue;

                            explodedWords.Add(new ExplodedWordInfo { Word = splashPw.Word, OwnerPlayerIndex = splashPw.OwnerPlayer });

                            if (splashPw.Cells != null)
                                for (int c = 0; c < splashPw.Cells.Count; c++)
                                    allCellsToRemove.Add(splashPw.Cells[c]);

                            splashPrimedBonus += Mathf.RoundToInt(splashPw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS;
                            _primedRegistry.RemovePrimedWord(splashPid);
                            justPrimedThisResolution.Remove(splashPid);
                        }

                        totalScore += splashBaseBonus + splashPrimedBonus;
                        detonationBonusAccum += splashBaseBonus + splashPrimedBonus;
                    }

                    // Remove cells from board data
                    List<Vector2Int> removedList = new List<Vector2Int>(allCellsToRemove);
                    ExplodeCells(removedList);

                    // Emit TilesExploded
                    var explodeEvt = new TilesExplodedEvent
                    {
                        RemovedCells  = removedList,
                        SourceWord    = "triggered",
                        ChainStep     = chainStep,
                        ExplodedWords = explodedWords,
                    };
                    result.Explosions.Add(explodeEvt);
                    OnTilesExploded?.Invoke(explodeEvt);

                    // Purge scored word keys that reference removed cells
                    PurgeScoredKeysForCells(removedList);

                    // 5e. Apply gravity in data layer
                    var gravityMoves = ApplyGravityInData();

                    if (gravityMoves.Count > 0)
                    {
                        var gravEvt = new GravityCollapseEvent
                        {
                            TileMoves = gravityMoves,
                            ChainStep = chainStep,
                        };
                        result.GravityEvents.Add(gravEvt);
                        OnGravityCollapse?.Invoke(gravEvt);

                        // Seed all occupied cells in columns that had movement
                        var movedCols = new HashSet<int>();
                        foreach (var kvp in gravityMoves)
                            movedCols.Add(kvp.Value.x);
                        localSeedCells = new List<Vector2Int>();
                        foreach (int c in movedCols)
                            for (int r = 0; r < ROWS; r++)
                                if (_board[c, r] != null)
                                    localSeedCells.Add(new Vector2Int(c, r));

//                         WDLog.Log($"[RulesEngine] Gravity applied — {gravityMoves.Count} tile(s) moved.");
                    }

                    // Update primed word cell positions after gravity shift
                    if (gravityMoves.Count > 0)
                        _primedRegistry.UpdateCellPositions(gravityMoves);

                    // Remove primed words where tiles no longer match letters
                    RemoveInvalidPrimedWords();
                }
                else
                {
                    // No explosions — no further chaining
                    keepChaining = false;
                }

                chainStep++;
            }

            if (chainStep >= MAX_CHAIN_DEPTH)
                Debug.LogWarning("[RulesEngine] Chain hit MAX_CHAIN_DEPTH — stopping.");

            result.TotalScore = totalScore;
            result.ChainSteps = Mathf.Max(0, chainStep - 1);

            // Emit ResolutionComplete
            OnResolutionComplete?.Invoke(new ResolutionCompleteEvent
            {
                TotalScoreEarned = totalScore,
                TotalChainSteps  = result.ChainSteps,
                WordsScored      = result.ScoredWords.Count,
                PlayerIndex      = playerIndex,
            });

            // 8. Expire old primed words AFTER resolution (moved from step 4).
            // This gives players one last chance to detonate on the expiry turn.
            var expiredList2 = new List<PrimedWordRegistry.PrimedWord>();
            int expired = _primedRegistry.ExpireOldWords(_globalTurn, expiredList2);
            if (expired > 0)
            {
                foreach (var pw in expiredList2)
                    _scoredWordKeys.Remove(pw.Word + "|" + pw.CellsString());
//                 WDLog.Log($"[RulesEngine] Expired {expired} primed word(s) at turn {_globalTurn}, scored keys purged");
            }

            // FINAL cleanup: remove any primed words whose letters no longer match
            RemoveInvalidPrimedWords();

            result.BaseWordScoreTotal = baseScoreAccum;
            result.ChainBonusTotal = chainBonusAccum;
            result.DetonationBonusTotal = detonationBonusAccum;

//             WDLog.Log($"[RulesEngine] ProcessDrop complete: " +
                      // $"words={result.ScoredWords.Count}, " +
                      // $"explosions={result.Explosions.Count}, " +
                      // $"chains={result.ChainSteps}, " +
                      // $"totalScore={totalScore} (base={baseScoreAccum} chain={chainBonusAccum} det={detonationBonusAccum}), " +
                      // $"primedRemaining={_primedRegistry.Count}");

            return result;
        }

        /// <summary>
        /// Removes primed words whose stored letters no longer match the actual board.
        /// This handles the case where gravity shifted tiles and the primed word's cells
        /// now contain different letters than when the word was originally formed.
        /// </summary>
        private void RemoveInvalidPrimedWords()
        {
            var allPrimed = _primedRegistry.GetAllPrimedWords();
            for (int i = allPrimed.Count - 1; i >= 0; i--)
            {
                var pw = allPrimed[i];
                bool valid = true;

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    int col = pw.Cells[c].x;
                    int row = pw.Cells[c].y;

                    if (!InBounds(col, row) || _board[col, row] == null)
                    {
                        valid = false;
                        break;
                    }

                    char expected = pw.Word[c];
                    char actual = char.ToUpper(_board[col, row].Letter);
                    if (actual != expected)
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    // Enhanced logging: show exactly which cell/letter failed
                    string mismatchDetail = "";
                    for (int c2 = 0; c2 < pw.Cells.Count; c2++)
                    {
                        int cc = pw.Cells[c2].x, rr = pw.Cells[c2].y;
                        char exp = pw.Word[c2];
                        char act = (InBounds(cc, rr) && _board[cc, rr] != null) ? _board[cc, rr].Letter : '?';
                        bool ok = (act == exp);
                        mismatchDetail += $" [{cc},{rr}]:{exp}{(ok ? "=" : "≠")}{act}";
                    }
//                     WDLog.Log($"[RulesEngine] Removing invalid primed word '{pw.Word}' (id={pw.Id}) —{mismatchDetail}");

                    // Clear primed glow on surviving tiles so they don't keep glowing
                    if (GridManager.Instance != null)
                    {
                        for (int c3 = 0; c3 < pw.Cells.Count; c3++)
                        {
                            Tile t = GridManager.Instance.GetTile(pw.Cells[c3].x, pw.Cells[c3].y);
                            if (t != null) t.ClearPrimedGlow();
                        }
                    }

                    _primedRegistry.RemovePrimedWord(pw.Id);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPLOSION — Remove cells from board data (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes tiles at given cell positions from the logical board.
        /// Does NOT touch GridManager visuals — emits events for that.
        /// </summary>
        private void ExplodeCells(List<Vector2Int> cells)
        {
            int removed = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i].x;
                int r = cells[i].y;
                if (InBounds(c, r) && _board[c, r] != null)
                {
                    _board[c, r] = null;
                    _bonusCells[c, r] = false; // gold consumed with tile
                    removed++;
                }
            }
//             WDLog.Log($"[RulesEngine] ExplodeCells: removed {removed} cell(s) from data.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // GRAVITY — Compact columns downward in data layer (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>MVP P5: public surface so boosters can sync RulesEngine + GridManager
        /// after destructive resolves. Internally delegates to ApplyGravityInData.</summary>
        public Dictionary<Vector2Int, Vector2Int> ApplyGravityInDataPublic() => ApplyGravityInData();

        /// <summary>
        /// Compacts each column downward in the logical board data.
        /// Returns a dictionary mapping old positions to new positions for tiles that moved.
        /// </summary>
        private Dictionary<Vector2Int, Vector2Int> ApplyGravityInData()
        {
            var moves = new Dictionary<Vector2Int, Vector2Int>();

            for (int col = 0; col < COLS; col++)
            {
                // Collect surviving cells bottom to top, tracking the ACTUAL board
                // row each was found at. 2026-06-03 Spencer bug: the move source
                // below must use this found row, NOT cell.Row — a Jester Hat shuffle
                // can leave cell.Row stale (relocated in _board without updating the
                // field), and trusting cell.Row produced scrambled gravity moves →
                // collisions → desync mess.
                List<RulesCellData> surviving = new List<RulesCellData>();
                List<int> survivingRows = new List<int>();
                for (int row = 0; row < ROWS; row++)
                {
                    if (_board[col, row] != null)
                    {
                        surviving.Add(_board[col, row]);
                        survivingRows.Add(row);
                    }
                }

                // If column is full or empty, nothing to compact
                bool anyGaps = surviving.Count < ROWS;
                if (!anyGaps)
                {
                    // Check if any gaps exist between tiles
                    bool hasGap = false;
                    for (int row = 0; row < surviving.Count; row++)
                    {
                        if (_board[col, row] == null) { hasGap = true; break; }
                    }
                    if (!hasGap) continue;
                }

                // Clear the column
                for (int row = 0; row < ROWS; row++)
                    _board[col, row] = null;

                // Repack from row 0 upward. Anchored rocks DON'T fall — they stay at their found
                // row and act as a fixed floor; other tiles compact into the free rows below the
                // next anchor (and above any anchor below them). 2026-06-09.
                int writeRow = 0;
                for (int i = 0; i < surviving.Count; i++)
                {
                    RulesCellData cell  = surviving[i];
                    int oldRow          = survivingRows[i]; // actual board row (robust to stale cell.Row)
                    int newRow;

                    if (cell.IsAnchored)
                    {
                        newRow   = oldRow;        // fixed — never moves
                        writeRow = oldRow + 1;    // following tiles stack on top of it
                    }
                    else
                    {
                        newRow   = writeRow;
                        writeRow++;
                    }

                    cell.Row = newRow;
                    cell.Col = col;
                    _board[col, newRow] = cell;

                    if (oldRow != newRow)
                    {
                        moves[new Vector2Int(col, oldRow)] = new Vector2Int(col, newRow);
                    }
                }
            }

            // FULL BOARD: new letters arrive IN the same fall, the Candy Crush way (2026-10-21 Spencer: "i dont think the
            // letters should wait before all the explosions are cleared"). The data gets them here; the grid's gravity
            // spawns their tiles above the board so each column falls as one piece (GridManager.SpawnRefillTiles).
            if (SurvivalManager.FullBoardActive && FULL_REFILL_IN_GRAVITY) FillTopsInData();

            return moves;
        }

        public static bool FULL_REFILL_IN_GRAVITY = true;
        public static int  FULL_REFILL_SMART      = 2;    // smart (one swap from a common word) letters per refill
        public static int  RefillGoldPending;             // a big play's reward: this many of the next refill's smart letters land gold

        /// <summary>Full board: top every column up to the ceiling (above the highest tile — holes under ice stay) with
        /// letters that spell nothing on their own, plus a few smart letters. Data only. Returns letters added.</summary>
        public int FillTopsInData()
        {
            SnapshotForRain(out var g, out var mv);
            var cells = new List<Vector2Int>();
            for (int c = 0; c < COLS; c++)
            {
                int top = 0;
                for (int r = ROWS - 1; r >= 0; r--) if (g[c, r] != '\0') { top = r + 1; break; }
                for (int r = top; r < ROWS; r++) cells.Add(new Vector2Int(c, r));
            }
            if (cells.Count == 0) return 0;
            cells.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));   // bottom-up: each checked against all below/beside
            var letters = new Dictionary<Vector2Int, char>();
            foreach (var p in cells) { letters[p] = DrawNoWordLetter(g, p.x, p.y); mv[p.x, p.y] = true; }

            int wantGold = RefillGoldPending; RefillGoldPending = 0;
            int goldNow = 0; for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (_board[x, y] != null && _board[x, y].IsRainGold) goldNow++;
            wantGold = Mathf.Clamp(Mathf.Min(wantGold, FULL_MAX_RAIN_GOLD - goldNow), 0, 9);   // never more than 2 shower-gold on the board
            int smart = 0; var gold = new HashSet<Vector2Int>();
            var order = new List<Vector2Int>(cells);
            for (int i = order.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = order[i]; order[i] = order[j]; order[j] = t; }
            try
            {
                int tried = 0;
                foreach (var p in order)
                {
                    if (smart >= Mathf.Max(FULL_REFILL_SMART, wantGold) || tried++ >= 10) break;   // cap the search (phones)
                    char was = g[p.x, p.y];
                    if (FindSmartRainLetter(g, mv, p.x, p.y, out char L, out string w) && !FormsWordAt(g, p.x, p.y))
                    {
                        letters[p] = L; smart++;
                        if (gold.Count < wantGold) gold.Add(p);
                        WDLog.Log($"[FullBoard] smart '{L}' at ({p.x},{p.y}) (one swap from '{w}'){(gold.Contains(p) ? " GOLD" : "")}");
                    }
                    else { g[p.x, p.y] = was; mv[p.x, p.y] = true; }
                }
            }
            catch (System.Exception ex) { Debug.LogError($"[FullBoard] smart seeding failed: {ex.Message}"); }

            foreach (var p in cells)
                _board[p.x, p.y] = new RulesCellData { Letter = letters[p], Col = p.x, Row = p.y, PlayerIndex = -1, IsRainGold = gold.Contains(p) };
            WDLog.Log($"[FullBoard] refill {cells.Count} letter(s) in the fall, {smart} smart, {gold.Count} gold");
            return cells.Count;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FULL BOARD SCAN — Scan every cell for words (used in chain loop)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans the entire board for all valid words (horizontal + vertical, 3-7 letters).
        /// Returns deduplicated list of RulesWordMatch.
        /// </summary>
        private List<RulesWordMatch> ScanEntireBoard()
        {
            // Phase A+B wild resolution: before scanning, commit a letter to every
            // uncommitted wild tile on the board. Algorithm: for each wild, try A-Z
            // and pick the letter that yields the longest valid word passing through
            // that cell (alphabetical tiebreak). Commit is one-way — cell.Letter is
            // written permanently but IsWild stays true for visuals/scoring.
            //
            // Design note: a wild is "uncommitted" iff IsWild && Letter == '\0'.
            // Uncommitted wilds act as run-breakers (like stones) until resolved.
            // Max 1 wild on the board in Phase C, so the O(26 * cells) cost is fine.
            ResolveUncommittedWilds();

            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                for (int startCol = 0; startCol < COLS; startCol++)
                {
                    for (int startRow = 0; startRow < ROWS; startRow++)
                    {
                        if (_board[startCol, startRow] == null) continue;

                        // Find max contiguous run length. Breaks: out of bounds, null,
                        // stone, or an uncommitted wild (Letter == '\0').
                        int maxLen = 0;
                        int safety = 0;
                        while (safety < MAX_WORD_LENGTH + 1)
                        {
                            int c = startCol + dc * maxLen;
                            int r = startRow + dr * maxLen;
                            if (!InBounds(c, r) || _board[c, r] == null || _board[c, r].IsStone) break;
                            if (_board[c, r].Letter == '\0') break; // unresolved wild blocks run
                            maxLen++;
                            safety++;
                        }

                        if (maxLen < MIN_WORD_LENGTH) continue;

                        int maxWordLen = Mathf.Min(maxLen, MAX_WORD_LENGTH);

                        for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxWordLen; wordLen++)
                        {
                            char[]           chars = new char[wordLen];
                            List<Vector2Int> cells = new List<Vector2Int>(wordLen);
                            List<int>        wildIndices = null;
                            bool valid = true;
                            bool hasRealLetter = false;

                            for (int step = 0; step < wordLen; step++)
                            {
                                int c = startCol + dc * step;
                                int r = startRow + dr * step;
                                RulesCellData cell = _board[c, r];
                                if (cell == null) { valid = false; break; }
                                chars[step] = char.ToUpper(cell.Letter);
                                cells.Add(new Vector2Int(c, r));
                                if (cell.IsWild)
                                {
                                    if (wildIndices == null) wildIndices = new List<int>(1);
                                    wildIndices.Add(step);
                                }
                                else
                                {
                                    hasRealLetter = true;
                                }
                            }

                            if (!valid) continue;
                            if (!hasRealLetter) continue; // reject wild-only words

                            string candidate = new string(chars);
                            if (!WordDictionary.IsValidWord(candidate) && !(TouchesSwap(cells) && WordDictionary.IsIntentionalWord(candidate))) continue;
                            if (ShortWordRejected(wordLen, cells, candidate)) continue;   // full board: short words only by swap

                            // ONE CLAIM PER CELL SET. Dedup on the CELLS ALONE, not word+cells.
                            // 2026-10-19: with backwards scanning on, a reversible pair (BAT/TAB
                            // — 186 such pairs in dict_core, 2% of it) is TWO different words over
                            // the SAME tiles, so a word+cells key let both prime and paid twice for
                            // one set of letters. Crossing words are unaffected: they share at most
                            // one cell, so their cell keys differ.
                            string sortedCellKey = BuildSortedCellKey(cells);
                            string dedupKey = sortedCellKey;
                            if (seenKeys.Contains(dedupKey)) continue;
                            seenKeys.Add(dedupKey);

                            results.Add(new RulesWordMatch
                            {
                                Word              = candidate,
                                Cells             = cells,
                                Direction         = (WordDirection)(dirIdx % 2),
                                Score             = 0,
                                WildLetterIndices = wildIndices,
                            });
                        }
                    }
                }
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SEED-CELL-ONLY SCAN — direct detection, no expansion
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds all valid words that physically pass through at least one seed
        /// cell. No BFS, no adjacency expansion. Only words whose cell list
        /// includes a seed cell are returned.
        ///
        /// This is the core detection for drops and rewrites: the only words
        /// that should prime are words containing the tile the player placed.
        /// Pre-existing words on the board that don't include the action cell
        /// stay dormant.
        /// </summary>
        private List<RulesWordMatch> ScanSeedCellsOnly(List<Vector2Int> seedCells)
        {
            var resolvedWilds = ResolveUncommittedWilds();

            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            // 2026-06-08 Spencer: include just-resolved wild cells as SEEDS. A wild that
            // resolves into a word on a LATER turn just became meaningful; without seeding
            // it, the seed-scan misses the word formed through it, so it never primes or
            // triggers (the delayed-wild bug). ScanEntireBoard already covers everything.
            if (resolvedWilds != null && resolvedWilds.Count > 0)
            {
                seedCells = (seedCells == null) ? new List<Vector2Int>() : new List<Vector2Int>(seedCells);
                for (int i = 0; i < resolvedWilds.Count; i++)
                    if (!seedCells.Contains(resolvedWilds[i])) seedCells.Add(resolvedWilds[i]);
            }

            if (seedCells == null || seedCells.Count == 0) return results;

            var seedSet = new HashSet<Vector2Int>(seedCells);

            // STEP 1: Find "direct" words — words physically containing a seed cell.
            // These are the words the player's action created.
            var directWords = new List<RulesWordMatch>();

            for (int s = 0; s < seedCells.Count; s++)
            {
                var seed = seedCells[s];
                if (!InBounds(seed.x, seed.y) || _board[seed.x, seed.y] == null) continue;

                var words = FindNewWords(seed.x, seed.y);
                for (int i = 0; i < words.Count; i++)
                {
                    bool containsSeed = false;
                    for (int c = 0; c < words[i].Cells.Count; c++)
                    {
                        if (seedSet.Contains(words[i].Cells[c]))
                        {
                            containsSeed = true;
                            break;
                        }
                    }
                    if (!containsSeed) continue;

                    string key = words[i].Word + "|" + words[i].CellKey;
                    if (seenKeys.Add(key))
                        results.Add(words[i]);
                }
            }

            return FilterSubstringWords(results);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // WILD TILE RESOLUTION (Phase A+B)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Iterates every unlocked wild cell on the board and commits a letter.
        /// Commit rule: pick the letter A-Z that yields the longest valid word
        /// passing through this cell; alphabetical tiebreak on equal length.
        /// If no valid word exists for any letter, leave/reset uncommitted ('\0').
        ///
        /// Writes _board[col, row].Letter directly. IsWild stays true so the tile
        /// continues to render as a star (Phase C visual) and scoring knows to
        /// zero the wild position.
        ///
        /// Cap: this resolver only resolves wilds in runs containing at least one
        /// real letter. Two adjacent wilds cannot mutually resolve — they stay
        /// uncommitted until a real letter lands next to them.
        /// </summary>
        private List<Vector2Int> ResolveUncommittedWilds()
        {
            var resolvedCells = new List<Vector2Int>();
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var cell = _board[col, row];
                    if (cell == null) continue;
                    if (!cell.IsWild) continue;

                    var wildPos = new Vector2Int(col, row);
                    var primedAtWild = _primedRegistry.GetPrimedWordsContaining(wildPos);
                    if (primedAtWild != null && primedAtWild.Count > 0)
                    {
                        WDLog.Log($"[WildResolve] SKIP ({col},{row}) — locked by {primedAtWild.Count} primed word(s): " +
                                  string.Join(",", primedAtWild.ConvertAll(p => p.Word)));
                        continue; // locked by an active primed word
                    }

                    int  bestLen    = 0;
                    char bestLetter = '\0';
                    char oldLetter  = cell.Letter;

                    for (char tryCh = 'A'; tryCh <= 'Z'; tryCh++)
                    {
                        cell.Letter = tryCh; // temporary probe
                        int longestThrough = FindLongestValidWordThroughCell(col, row);
                        if (longestThrough > bestLen)
                        {
                            bestLen    = longestThrough;
                            bestLetter = tryCh;
                        }
                        // alpha tiebreak: first letter to hit a length wins,
                        // so the strict > above is correct — don't overwrite.
                    }

                    cell.Letter = bestLetter; // commit (or '\0' if nothing found)
                    if (oldLetter != bestLetter)
                        PurgeScoredKeysForCells(new List<Vector2Int> { wildPos });

                    if (bestLetter != '\0')
                    {
                        // 2026-06-08 Spencer: a wild whose committed letter CHANGED (incl. a
                        // first-time '\0'→X resolution) is a NEW seed — words through it must
                        // be (re)scanned so a delayed-wild word actually primes/triggers.
                        if (oldLetter != bestLetter) resolvedCells.Add(wildPos);
                        WDLog.Log($"[WildResolve] OK ({col},{row}) '{oldLetter}'→'{bestLetter}' (longest word len={bestLen})");
                    }
                    else
                    {
                        // Dump neighbor context so we can see why no word was possible.
                        string ctx = DumpWildNeighborhood(col, row);
                        WDLog.Log($"[WildResolve] NO-WORD ({col},{row}) stays uncommitted. {ctx}");
                    }
                }
            }
            return resolvedCells;
        }

        /// <summary>
        /// Debug helper — returns a short string describing the cells in the same
        /// row and column as (col,row). Used by the wild-resolver trace logs.
        /// </summary>
        private string DumpWildNeighborhood(int col, int row)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("row=[");
            for (int c = 0; c < COLS; c++)
            {
                var bc = _board[c, row];
                char ch = (bc == null) ? '.' : (bc.IsStone ? '#' : (bc.Letter == '\0' ? '?' : bc.Letter));
                sb.Append(c == col ? $"*{ch}*" : ch.ToString());
            }
            sb.Append("] col=[");
            for (int r = 0; r < ROWS; r++)
            {
                var bc = _board[col, r];
                char ch = (bc == null) ? '.' : (bc.IsStone ? '#' : (bc.Letter == '\0' ? '?' : bc.Letter));
                sb.Append(r == row ? $"*{ch}*" : ch.ToString());
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Probe helper for wild resolution. Assumes (col, row) currently holds the
        /// candidate letter (set by caller). Returns the length of the longest valid
        /// word that passes through (col, row), considering both scan directions.
        /// Respects stone and wild-blocker run-break rules.
        /// </summary>
        private int FindLongestValidWordThroughCell(int col, int row)
        {
            int longest = 0;

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                // Walk backward from (col,row) to find how many cells precede it.
                int backExtent = 0;
                while (backExtent < MAX_WORD_LENGTH)
                {
                    int c = col - dc * (backExtent + 1);
                    int r = row - dr * (backExtent + 1);
                    if (!InBounds(c, r)) break;
                    var bc = _board[c, r];
                    if (bc == null || bc.IsStone || bc.Letter == '\0') break;
                    backExtent++;
                }

                // Walk forward from (col,row) to find how many cells follow.
                int fwdExtent = 0;
                while (fwdExtent < MAX_WORD_LENGTH)
                {
                    int c = col + dc * (fwdExtent + 1);
                    int r = row + dr * (fwdExtent + 1);
                    if (!InBounds(c, r)) break;
                    var fc = _board[c, r];
                    if (fc == null || fc.IsStone || fc.Letter == '\0') break;
                    fwdExtent++;
                }

                int maxTotal = backExtent + 1 + fwdExtent;
                if (maxTotal > MAX_WORD_LENGTH) maxTotal = MAX_WORD_LENGTH;
                if (maxTotal < MIN_WORD_LENGTH) continue;

                // Try every sub-run of every length that contains (col,row).
                // backOffset = how many cells to the left/below of the wild the run starts.
                for (int len = MIN_WORD_LENGTH; len <= maxTotal; len++)
                {
                    int maxBack = Mathf.Min(backExtent, len - 1);
                    int minBack = Mathf.Max(0, len - 1 - fwdExtent);
                    for (int backOff = minBack; backOff <= maxBack; backOff++)
                    {
                        int startC = col - dc * backOff;
                        int startR = row - dr * backOff;

                        char[] chars = new char[len];
                        bool ok = true;
                        for (int s = 0; s < len; s++)
                        {
                            int c = startC + dc * s;
                            int r = startR + dr * s;
                            if (!InBounds(c, r)) { ok = false; break; }
                            var pc = _board[c, r];
                            if (pc == null || pc.IsStone || pc.Letter == '\0') { ok = false; break; }
                            chars[s] = char.ToUpper(pc.Letter);
                        }
                        if (!ok) continue;

                        string cand = new string(chars);
                        if (WordDictionary.IsValidWord(cand))
                        {
                            if (len > longest) longest = len;
                        }
                    }
                }
            }

            return longest;
        }

        /// <summary>
        /// DEBUG: Toggle a board cell into a wild tile for Phase A+B playtesting.
        /// Clears the cell's letter (will re-resolve on next scan), sets IsWild,
        /// and clears any gold/cyan bonus on the cell (invariant: wilds carry no
        /// bonuses). The tile must already exist at (col, row).
        ///
        /// This is the ONLY way to place wilds in Phase A+B — HandManager injection
        /// comes in Phase C. Call from a console command or Inspector button.
        /// </summary>
        public void DebugSetWild(int col, int row)
        {
            if (!InBounds(col, row)) { Debug.LogWarning($"[RulesEngine] DebugSetWild: ({col},{row}) out of bounds."); return; }
            var cell = _board[col, row];
            if (cell == null) { Debug.LogWarning($"[RulesEngine] DebugSetWild: no tile at ({col},{row})."); return; }
            if (cell.IsStone) { Debug.LogWarning($"[RulesEngine] DebugSetWild: cannot wildify a stone tile."); return; }

            cell.IsWild = true;
            cell.Letter = '\0'; // forces re-resolution on next scan
            _bonusCells[col, row] = false; // wilds never carry gold
            _cyanCells[col, row] = false;  // wilds never carry cyan refund

            // Purge any scored keys involving this cell so new words through the wild score fresh.
            PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

//             WDLog.Log($"[RulesEngine] DebugSetWild: ({col},{row}) is now a wild tile (letter cleared).");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORED KEY MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Purges scored word keys that reference any of the removed cell coordinates.
        /// After gravity, those coordinates will hold different tiles, so words
        /// forming at those positions should score fresh.
        /// </summary>
        public void PurgeScoredKeysForCells(List<Vector2Int> removedCells)
        {
            if (removedCells == null || removedCells.Count == 0) return;

            HashSet<string> removedCoords = new HashSet<string>();
            for (int i = 0; i < removedCells.Count; i++)
                removedCoords.Add($"{removedCells[i].x},{removedCells[i].y}");

            List<string> keysToRemove = new List<string>();
            foreach (string key in _scoredWordKeys)
            {
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');
                for (int i = 0; i < coords.Length; i++)
                {
                    if (removedCoords.Contains(coords[i]))
                    {
                        keysToRemove.Add(key);
                        break;
                    }
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
                _scoredWordKeys.Remove(keysToRemove[i]);

        }

        /// <summary>
        /// Purges per-resolution _stepScoredKeys that reference any removed cell.
        /// Mirrors PurgeScoredKeysForCells but for the step-local set, so words
        /// that reform at exploded cells within the same resolution can re-score.
        /// </summary>
        private void PurgeStepScoredKeysForCells(List<Vector2Int> removedCells)
        {
            if (_stepScoredKeys == null || removedCells == null || removedCells.Count == 0) return;

            HashSet<string> removedCoords = new HashSet<string>();
            for (int i = 0; i < removedCells.Count; i++)
                removedCoords.Add($"{removedCells[i].x},{removedCells[i].y}");

            List<string> keysToRemove = new List<string>();
            foreach (string key in _stepScoredKeys)
            {
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');
                for (int i = 0; i < coords.Length; i++)
                {
                    if (removedCoords.Contains(coords[i]))
                    {
                        keysToRemove.Add(key);
                        break;
                    }
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
                _stepScoredKeys.Remove(keysToRemove[i]);

        }

        // ═══════════════════════════════════════════════════════════════════════════
        // WORD DETECTION — FindNewWords (Job 2, kept)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans horizontal and vertical lines passing through the cell at (col, row).
        /// Returns a list of RulesWordMatch structs for all valid words of 3–7 letters found.
        /// </summary>
        public List<RulesWordMatch> FindNewWords(int col, int row)
        {
            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            if (!InBounds(col, row) || _board[col, row] == null)
            {
//                 WDLog.Log($"[RulesEngine] FindNewWords({col},{row}) — cell is empty or out of bounds.");
                return results;
            }

            // Wild handling (Phase A+B): FindNewWords is called from non-mutating
            // paths (swap/edit validation, drop simulation), so we do NOT call
            // ResolveUncommittedWilds here — that would commit letters as a side
            // effect of a preview. Instead: uncommitted wilds (Letter=='\0') act
            // as run-breakers, and already-committed wilds track WildLetterIndices
            // so scoring stays correct.
            //
            // Phase A+B limitation: swap/edit validation will not "see through"
            // an uncommitted wild. The next real drop → ScanEntireBoard will
            // resolve it. Revisit in Phase C if the debug flow needs it.

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                // If the starting cell itself is an uncommitted wild, we can't
                // build any word through it in this path.
                if (_board[col, row].Letter == '\0') continue;

                // Walk backward (break on null, stone, or uncommitted wild)
                int runStart = 0;
                int safety   = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col - dc * (runStart + 1);
                    int nr = row - dr * (runStart + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    if (_board[nc, nr].IsStone) break;
                    if (_board[nc, nr].Letter == '\0') break;
                    runStart++;
                    safety++;
                }

                // Walk forward (same run-break rules)
                int runEnd = 0;
                safety = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col + dc * (runEnd + 1);
                    int nr = row + dr * (runEnd + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    if (_board[nc, nr].IsStone) break;
                    if (_board[nc, nr].Letter == '\0') break;
                    runEnd++;
                    safety++;
                }

                int runLength = runStart + 1 + runEnd;
                if (runLength < MIN_WORD_LENGTH) continue;

                int absStartCol = col - dc * runStart;
                int absStartRow = row - dr * runStart;

                char[]           runChars     = new char[runLength];
                bool[]           runIsWild    = new bool[runLength];
                List<Vector2Int> runCells     = new List<Vector2Int>(runLength);
                bool runValid = true;

                for (int step = 0; step < runLength && runValid; step++)
                {
                    int nc = absStartCol + dc * step;
                    int nr = absStartRow + dr * step;
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) { runValid = false; break; }
                    runChars[step]  = char.ToUpper(_board[nc, nr].Letter);
                    runIsWild[step] = _board[nc, nr].IsWild;
                    runCells.Add(new Vector2Int(nc, nr));
                }

                if (!runValid) continue;

                int maxLen = Mathf.Min(runLength, MAX_WORD_LENGTH);

                for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxLen; wordLen++)
                {
                    for (int startOffset = 0; startOffset <= runLength - wordLen; startOffset++)
                    {
                        char[]           wordChars   = new char[wordLen];
                        List<Vector2Int> wordCells   = new List<Vector2Int>(wordLen);
                        List<int>        wildIndices = null;
                        bool             hasRealLetter = false;

                        for (int k = 0; k < wordLen; k++)
                        {
                            wordChars[k] = runChars[startOffset + k];
                            wordCells.Add(runCells[startOffset + k]);
                            if (runIsWild[startOffset + k])
                            {
                                if (wildIndices == null) wildIndices = new List<int>(1);
                                wildIndices.Add(k);
                            }
                            else
                            {
                                hasRealLetter = true;
                            }
                        }

                        if (!hasRealLetter) continue; // reject wild-only words

                        string candidate = new string(wordChars);
                        if (!WordDictionary.IsValidWord(candidate) && !(TouchesSwap(wordCells) && WordDictionary.IsIntentionalWord(candidate)))
                            continue;
                        if (ShortWordRejected(wordLen, wordCells, candidate)) continue;   // full board: short words only by swap

                        // ONE CLAIM PER CELL SET — see the note on the other scanner.
                        // A reversible pair (BAT/TAB) is two words over identical cells.
                        string sortedCellKey = BuildSortedCellKey(wordCells);
                        string dedupKey = sortedCellKey;
                        if (seenKeys.Contains(dedupKey)) continue;
                        seenKeys.Add(dedupKey);

                        string dirName = dirIdx % 2 == 0 ? "horizontal" : "vertical";
//                         WDLog.Log($"[RulesEngine] Found word '{candidate}' " +
                                  // $"({dirName}) at cells {BuildCellKey(wordCells)}");

                        results.Add(new RulesWordMatch
                        {
                            Word              = candidate,
                            Cells             = wordCells,
                            Direction         = (WordDirection)(dirIdx % 2),
                            Score             = 0,
                            WildLetterIndices = wildIndices,
                        });
                    }
                }
            }

            // if (results.Count > 0)
//                 WDLog.Log($"[RulesEngine] FindNewWords({col},{row}) → {results.Count} word(s) found.");

            return results;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SIMULATE DROP — Non-mutating evaluation for AI (Job 9 enhanced)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Simulates placing a letter at (col, lowestEmptyRow) without committing.
        /// Returns word matches with scores calculated.
        /// Filters out already-scored words so AI only sees NEW words the drop would create.
        ///
        /// Also checks for overlap with primed words — returned matches that share
        /// cells with primed words indicate a potential trigger.
        ///
        /// Does NOT modify the board state — places and then removes the simulated cell.
        /// </summary>
        public List<RulesWordMatch> SimulateDrop(int col, char letter, int playerIndex)
        {
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0) return new List<RulesWordMatch>();

            var simCell = new RulesCellData
            {
                Letter      = char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
            };
            _board[col, targetRow] = simCell;

            // Find words that physically contain the drop cell
            List<RulesWordMatch> allMatches = FindNewWords(col, targetRow);
            var dropCell = new Vector2Int(col, targetRow);

            // Filter: only words containing the drop cell + not already scored
            List<RulesWordMatch> newMatches = new List<RulesWordMatch>();
            for (int i = 0; i < allMatches.Count; i++)
            {
                bool containsDrop = false;
                for (int c = 0; c < allMatches[i].Cells.Count; c++)
                {
                    if (allMatches[i].Cells[c] == dropCell) { containsDrop = true; break; }
                }
                if (!containsDrop) continue;

                string key = allMatches[i].Word + "|" + allMatches[i].CellKey;
                if (!_scoredWordKeys.Contains(key))
                {
                    var m = allMatches[i];
                    m.Score = CalculateWordScore(m.Word, m.WildLetterIndices);
                    newMatches.Add(m);
                }
            }

            // Restore board — remove the simulated cell
            _board[col, targetRow] = null;

            return newMatches;
        }

        /// <summary>
        /// Enhanced simulation that also reports whether the drop would trigger
        /// any existing primed words. Returns matches and sets wouldTriggerPrimed.
        ///
        /// Non-mutating — restores board state after simulation.
        /// </summary>
        public List<RulesWordMatch> SimulateDropWithTriggerCheck(
            int col, char letter, int playerIndex, out bool wouldTriggerPrimed)
        {
            wouldTriggerPrimed = false;

            List<RulesWordMatch> matches = SimulateDrop(col, letter, playerIndex);

            if (matches.Count > 0 && _primedRegistry.Count > 0)
            {
                for (int w = 0; w < matches.Count && !wouldTriggerPrimed; w++)
                {
                    RulesWordMatch match = matches[w];
                    if (match.Cells == null) continue;

                    for (int c = 0; c < match.Cells.Count && !wouldTriggerPrimed; c++)
                    {
                        Vector2Int cell = match.Cells[c];
                        List<PrimedWordRegistry.PrimedWord> overlapping =
                            _primedRegistry.GetPrimedWordsContaining(cell);

                        if (overlapping.Count > 0)
                            wouldTriggerPrimed = true;
                    }
                }
            }

            return matches;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // UNIT TESTS (updated for Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        [ContextMenu("Run Unit Tests")]
        public void RunUnitTests()
        {
//             WDLog.Log("[RulesEngine] ══ BEGIN UNIT TESTS ══");

            // ── Test 1: 3-cell horizontal CAT ─────────────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 0] = new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = 0 };
            _board[2, 0] = new RulesCellData { Letter = 'T', Col = 2, Row = 0, PlayerIndex = 0 };

            var matches1 = FindNewWords(1, 0);
            bool found1 = ContainsWord(matches1, "CAT");
//             WDLog.Log($"[RulesEngine] Test 1 — horizontal 'CAT': " +
                      // $"{(found1 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 2: diagonal CAT — should NOT find ─────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 1] = new RulesCellData { Letter = 'A', Col = 1, Row = 1, PlayerIndex = 0 };
            _board[2, 2] = new RulesCellData { Letter = 'T', Col = 2, Row = 2, PlayerIndex = 0 };

            var matches2 = FindNewWords(1, 1);
            bool found2 = ContainsWord(matches2, "CAT");
//             WDLog.Log($"[RulesEngine] Test 2 — diagonal 'CAT': " +
                      // $"{(!found2 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 3: CalculateWordScore ─────────────────────────────────────────
            int scoreCat  = CalculateWordScore("CAT");   // C(3)+A(1)+T(1)=5 × 1.0 = 5
            int scoreFire = CalculateWordScore("FIRE");  // F(4)+I(1)+R(1)+E(1)=7 × 1.5 = 11 (rounded)
            int scoreSlate = CalculateWordScore("SLATE"); // S(1)+L(1)+A(1)+T(1)+E(1)=5 × 2.0 = 10
            bool test3 = (scoreCat == 5) && (scoreFire == 11) && (scoreSlate == 10);
//             WDLog.Log($"[RulesEngine] Test 3 — CalculateWordScore: CAT={scoreCat}(exp5) " +
                      // $"FIRE={scoreFire}(exp11) SLATE={scoreSlate}(exp10) " +
                      // $"{(test3 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 4: ProcessDrop — form CAT, verify events ──────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 0] = new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = 0 };

            bool wordScoredFired = false;
            bool wordPrimedFired = false;

            RulesEventHandler<WordScoredEvent> scoredHandler = (evt) =>
            {
                if (evt.Word == "CAT") wordScoredFired = true;
            };
            RulesEventHandler<WordPrimedEvent> primedHandler = (evt) =>
            {
                if (evt.Word == "CAT") wordPrimedFired = true;
            };

            OnWordScored += scoredHandler;
            OnWordPrimed += primedHandler;

            ResolutionResult res4 = ProcessDrop(2, 'T', 0);

            OnWordScored -= scoredHandler;
            OnWordPrimed -= primedHandler;

            bool test4 = wordScoredFired && wordPrimedFired && res4.AnyWordScored;
//             WDLog.Log($"[RulesEngine] Test 4 — ProcessDrop 'T' forms CAT: " +
                      // $"scored={wordScoredFired} primed={wordPrimedFired} " +
                      // $"{(test4 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 5: SimulateDrop filters already-scored words ───────────────────
            // CAT is already scored at cells (0,0),(1,0),(2,0).
            // Simulating a drop that would form CAT again should return empty
            // (since CAT at those cells is already in _scoredWordKeys).
            // But let's test a fresh simulation on a different position.
            List<RulesWordMatch> simMatches = SimulateDrop(3, 'S', 1);
            // 'S' at (3,0) — horizontal: ...T S — "ATS"? unlikely to be valid
            // This mainly verifies SimulateDrop runs without error.
//             WDLog.Log($"[RulesEngine] Test 5 — SimulateDrop('S', col=3): " +
                      // $"{simMatches.Count} match(es) found ✓ (no crash)");

            // ── Test 6: SimulateDropWithTriggerCheck ────────────────────────────────
            // CAT is primed. Simulate placing 'C' at col 1, row 1 to form ACE vertically.
            // A at (1,0) is part of primed CAT.
            _board[1, 1] = new RulesCellData { Letter = 'C', Col = 1, Row = 1, PlayerIndex = 0 };

            bool wouldTrigger;
            List<RulesWordMatch> trigMatches = SimulateDropWithTriggerCheck(1, 'E', 0, out wouldTrigger);

            // Clean up the manually placed cell
            _board[1, 1] = null;

//             WDLog.Log($"[RulesEngine] Test 6 — SimulateDropWithTriggerCheck: " +
                      // $"wouldTrigger={wouldTrigger} matches={trigMatches.Count}");

            // ── Summary ───────────────────────────────────────────────────────────
            bool allPassed = found1 && !found2 && test3 && test4;
//             WDLog.Log($"[RulesEngine] ══ UNIT TESTS {(allPassed ? "ALL PASSED ✓" : "SOME FAILED ✗")} ══");

            ClearBoard();
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static bool InBounds(int col, int row)
            => col >= 0 && col < COLS && row >= 0 && row < ROWS;

        /// <summary>
        /// Removes substring words — if TAMER and TAM share cells in the same line,
        /// only keep TAMER. A word is a substring if ALL its cells are contained
        /// in a longer word's cells.
        /// </summary>
        private static List<RulesWordMatch> FilterSubstringWords(List<RulesWordMatch> words)
        {
            if (words.Count <= 1) return words;

            var result = new List<RulesWordMatch>();

            for (int i = 0; i < words.Count; i++)
            {
                bool isSubstring = false;
                HashSet<Vector2Int> myCells = new HashSet<Vector2Int>(words[i].Cells);

                for (int j = 0; j < words.Count; j++)
                {
                    if (i == j) continue;
                    if (words[j].Cells.Count <= words[i].Cells.Count) continue;

                    // Check if all of my cells are in the longer word
                    bool allContained = true;
                    for (int c = 0; c < words[i].Cells.Count; c++)
                    {
                        bool found = false;
                        for (int d = 0; d < words[j].Cells.Count; d++)
                        {
                            if (words[i].Cells[c] == words[j].Cells[d])
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found) { allContained = false; break; }
                    }

                    if (allContained)
                    {
                        isSubstring = true;
                        break;
                    }
                }

                if (!isSubstring)
                    result.Add(words[i]);
            }

            return result;
        }

        private static string BuildCellKey(List<Vector2Int> cells)
        {
            var sorted = new List<Vector2Int>(cells);
            sorted.Sort((a, b) =>
            {
                int cmp = a.x.CompareTo(b.x);
                return cmp != 0 ? cmp : a.y.CompareTo(b.y);
            });

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(sorted[i].x).Append(',').Append(sorted[i].y);
            }
            return sb.ToString();
        }

        // Alias — BuildCellKey already sorts
        private static string BuildSortedCellKey(List<Vector2Int> cells) => BuildCellKey(cells);

        private static bool ContainsWord(List<RulesWordMatch> matches, string word)
        {
            for (int i = 0; i < matches.Count; i++)
                if (matches[i].Word == word.ToUpper()) return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // STEP-BY-STEP RESOLUTION STATE MACHINE
        // ═════════════════════════════════════════════════════════════════════════════

        // ── Resolution phases ────────────────────────────────────────────────────────

        public enum ResolutionPhase
        {
            Idle,           // No resolution in progress
            TileDropped,    // Tile placed, ready for word detection
            WordsDetected,  // Words found, ready to score
            WordsScored,    // Words scored + primed, ready for trigger check
            TriggersFound,  // Triggers identified, ready to explode
            Exploding,      // Tiles exploding, ready for gravity
            GravityApplied, // Gravity done, check for chain words
            Complete        // Resolution finished
        }

        public class StepResult
        {
            public ResolutionPhase Phase;
            public int Row = -1;
            public List<RulesWordMatch> NewWords;
            public List<WordScoredEvent> ScoredWords;
            public List<PrimedTriggeredEvent> Triggers;
            public List<Vector2Int> ExplodedCells;
            // FORMED word cells for this detonation step: the player's scored word and/or
            // gravity-formed cascade words that CAUSED the detonation. Distinct from
            // detonating primed words and all collateral splash cells. Added so the live
            // HandManager Exploding path can color word tiles green and collateral white.
            public List<Vector2Int> FormedWordCells;
            // FROZEN / ICE (2026-06-12): cells that were caught in a detonation while frozen and
            // THAWED instead of being destroyed — they survive on the board (ice cleared). The
            // visual layer reads this to play a defrost VFX + clear the frost overlay, and must
            // NOT route these through the explode animation. Never overlaps ExplodedCells.
            public List<Vector2Int> ThawedCells;
            public List<KeyValuePair<Vector2Int, int>> SpecialsCreated;   // cell -> kind, born this step
            public Dictionary<Vector2Int, List<Vector2Int>> SpecialSources;   // birth cell -> the word's cells (merge animation)
            public List<SpecialFire> SpecialsFired;                         // fired this step, with what they cleared
            public Dictionary<Vector2Int, Vector2Int> GravityMoves;
            public int TotalScore;
            public bool ChainContinues;
            public int ChainTriggeredCount;
            public int DetonationBonus;  // total detonation bonus this step
            public int DetonationHeat;   // heat portion of detonation bonus
            public int ChainDepth;       // chain depth when this detonation fired
            public bool ReplacedTopTile; // true if this drop replaced the top tile in a full column
            public int SwapRefillCount;  // swap refill tiles detonated this step
            public int EditRefillCount;  // edit refill tiles detonated this step
            public int WildRefillCount;  // wild refill tiles detonated this step
            public int LongestWordLength; // longest primed word in this detonation (for VFX tier)
            // Trigger words: the NEW words formed this step that caused the detonation.
            // Exposed so VFX (BigBurstFlash) can blast the player's word too, not only
            // the primed words it ignited. Null when the step didn't trigger anything.
            public List<RulesWordMatch> TriggerWords;
        }

        // ── Step-by-step state ───────────────────────────────────────────────────────

        private ResolutionPhase _currentPhase = ResolutionPhase.Idle;
        private int _stepPlayerIndex;
        private int _stepChainDepth;
        private int _stepTotalScore;
        // Big-moment splash fires ONCE per resolution, not every chain iteration.
        // Prevents the feedback loop where splash clears tiles → gravity creates
        // new words → those chain-score huge → trigger more primed → splash again.
        private bool _splashFiredThisResolution;
        private HashSet<int> _stepJustPrimed;
        private HashSet<string> _stepScoredKeys;
        private List<RulesWordMatch> _stepPendingWords;
        private List<RulesWordMatch> _stepTriggerWords; // words that caused the detonation (for Survival clear-both)
        private List<int> _stepPendingTriggers;
        private int _stepChainTriggeredCount;

        // Seed cells for connected-word detection. Set in BeginDrop/BeginRewrite.
        // After gravity, updated to the new positions of moved tiles.
        private List<Vector2Int> _stepSeedCells;

        // ── Public accessors ─────────────────────────────────────────────────────────

        public ResolutionPhase CurrentPhase => _currentPhase;
        public bool IsResolving => _currentPhase != ResolutionPhase.Idle && _currentPhase != ResolutionPhase.Complete;

        /// <summary>
        /// Lightweight read-only check: will DoCheckTriggers find any triggers?
        /// Call during WordsScored phase to decide whether to play full scoring
        /// animation or skip it in favour of the detonation presentation.
        /// </summary>
        public bool PeekHasTriggers()
        {
            if (_stepPendingWords == null || _stepPendingWords.Count == 0) return false;

            // Phase 9.10: mirror the Phase 9.7 DoCheckTriggers filter-skip so the
            // peek matches actual trigger behavior for cascade self-overlaps.
            // Without this, PeekHasTriggers returns false for gravity-formed words
            // that will self-detonate mid-chain, which caused GameVisualBridge to
            // play the slow non-detonation scoring branch (0.35s) during cascades.
            //
            // Phase 11+ extension: apply to Survival too. Spencer's ask — "I wish
            // the cascade worked the same way in Survival as Level mode" — means
            // gravity-formed words should self-detonate mid-chain instead of
            // sitting there waiting for another trigger. One gate, both modes.
            bool skipJustPrimedFilter =
                (GameManager.IsLevelMode || SurvivalManager.IsSurvivalMode)
                && _stepChainDepth > 0;

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                var match = _stepPendingWords[w];
                for (int c = 0; c < match.Cells.Count; c++)
                {
                    // Check direct overlap
                    var overlapping = _primedRegistry.GetPrimedWordsContaining(match.Cells[c]);
                    for (int p = 0; p < overlapping.Count; p++)
                    {
                        if (skipJustPrimedFilter || !_stepJustPrimed.Contains(overlapping[p].Id))
                            return true;
                    }

                    // Check adjacency (if enabled)
                    if (AdjacencyTriggerEnabled)
                    {
                        var adjacent = _primedRegistry.GetPrimedWordsAdjacentTo(match.Cells[c]);
                        for (int p = 0; p < adjacent.Count; p++)
                        {
                            if (skipJustPrimedFilter || !_stepJustPrimed.Contains(adjacent[p].Id))
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        // ── BeginDrop ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start a new step-by-step resolution. Places the tile and returns.
        /// Call NextStep() repeatedly to advance through the resolution.
        /// </summary>
        public StepResult BeginDrop(int col, char letter, int playerIndex)
        {
            return BeginDrop(col, letter, playerIndex, isWild: false);
        }

        /// <summary>
        /// Phase C wild-aware drop. When isWild=true the letter arg is ignored and
        /// the cell is placed uncommitted (IsWild=true, Letter='\0'); the next
        /// scan resolves it to the best letter via ResolveUncommittedWilds.
        /// </summary>
        public StepResult BeginDrop(int col, char letter, int playerIndex, bool isWild)
        {
            // Defensive: '*' is the wild sentinel and must never land as a literal
            // letter on the board (no words would form through it). Promote to wild
            // regardless of what the caller passed — covers any upstream desync.
            if (!isWild && letter == TileBag.WILD_CHAR)
            {
                isWild = true;
                Debug.LogWarning("[RulesEngine] BeginDrop: auto-promoted literal '*' drop to wild.");
            }
            if (!isWild && letter == '\0')
            {
                Debug.LogError("[RulesEngine] BeginDrop: REJECTED null letter");
                return null;
            }
            int targetRow = GetLowestEmptyRow(col);
            bool replaced = false;

            if (targetRow < 0)
            {
                // Column full — replace top tile (sacrifice)
                targetRow = ROWS - 1;
                var oldCell = _board[col, targetRow];
                char oldLetter = oldCell != null ? oldCell.Letter : '?';

                // Clear any primed words involving the replaced tile
                if (GridManager.Instance != null)
                {
                    Tile oldTile = GridManager.Instance.GetTile(col, targetRow);
                    if (oldTile != null) oldTile.ClearPrimedGlow();
                }
                _bonusCells[col, targetRow] = false;

                replaced = true;
//                 WDLog.Log($"[RulesEngine] BeginDrop: column {col} full — replacing top tile '{oldLetter}' with '{letter}'");
            }

            var cellData = new RulesCellData
            {
                Letter      = isWild ? '\0' : char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
                IsWild      = isWild,
            };
            _board[col, targetRow] = cellData;
            // Wilds never carry gold/cyan — clear any bonus on the landing cell.
            if (isWild)
            {
                _bonusCells[col, targetRow] = false;
                _cyanCells[col, targetRow]  = false;
            }

            _stepPlayerIndex   = playerIndex;
            _stepChainDepth    = 0;
            _stepTotalScore    = 0;
            _splashFiredThisResolution = false;
            _stepChainTriggeredCount = 0;
            _stepJustPrimed    = new HashSet<int>();
            _stepScoredKeys    = new HashSet<string>();
            _stepPendingWords    = null;
            _stepTriggerWords    = null;
            _stepPendingTriggers = null;
            _stepSeedCells     = new List<Vector2Int> { new Vector2Int(col, targetRow) };
            _currentPhase      = ResolutionPhase.TileDropped;

            // Expiry moved to FinalizeDrop — gives last-turn detonation chance

//             WDLog.Log($"[RulesEngine] BeginDrop: placed '{letter}' at ({col},{targetRow}) player={playerIndex}");

            OnTileDropped?.Invoke(new TileDroppedEvent
            {
                Col         = col,
                Row         = targetRow,
                Letter      = char.ToUpper(letter),
                PlayerIndex = playerIndex,
            });

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = targetRow, ReplacedTopTile = replaced };
        }

        // ── BeginRewrite ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces an existing board tile with a new letter, then enters
        /// the same step-by-step resolution as BeginDrop. Used by the
        /// Rewrite Tile action.
        ///
        /// Returns null if the cell is invalid, empty, not owned by the player,
        /// or contains a primed tile.
        /// </summary>
        public StepResult BeginRewrite(int col, int row, char newLetter, int playerIndex, bool isWild = false)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            // A wild rewrite carries no letter ('\0' is expected); only reject a null
            // letter for a normal (non-wild) rewrite.
            if (!isWild && newLetter == '\0') { Debug.LogError("[RulesEngine] BeginRewrite: REJECTED null letter"); return null; }

            RulesCellData existing = _board[col, row];
            if (existing == null) return null;

            // Block rewriting primed tiles
            var primedAtCell = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primedAtCell != null && primedAtCell.Count > 0) return null;

            char oldLetter = existing.Letter;

            // Overwrite the cell. For a wild rewrite, place an uncommitted wild
            // (Letter='\0', IsWild=true) exactly like a dropped wild — it then resolves
            // through ResolveUncommittedWilds to whatever forms a word at this cell.
            var cellData = new RulesCellData
            {
                Letter      = isWild ? '\0' : char.ToUpper(newLetter),
                Col         = col,
                Row         = row,
                PlayerIndex = playerIndex,
                IsWild      = isWild,
            };
            _board[col, row] = cellData;
            // Wilds never carry gold/cyan bonus — clear any on the target cell.
            if (isWild)
            {
                _bonusCells[col, row] = false;
                _cyanCells[col, row]  = false;
            }

            // Purge scored word keys that reference this cell so new words can be detected
            PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

            // Invalidate any primed words that included the old letter at this cell
            RemoveInvalidPrimedWords();

            // Initialize step-by-step resolution (same as BeginDrop)
            _stepPlayerIndex        = playerIndex;
            _stepChainDepth         = 0;
            _stepTotalScore         = 0;
            _stepChainTriggeredCount = 0;
            _splashFiredThisResolution = false;
            _stepJustPrimed         = new HashSet<int>();
            _stepScoredKeys         = new HashSet<string>();
            _stepPendingWords       = null;
            _stepTriggerWords       = null;
            _stepPendingTriggers    = null;
            _stepSeedCells          = new List<Vector2Int> { new Vector2Int(col, row) };
            _currentPhase           = ResolutionPhase.TileDropped;

//             WDLog.Log($"[RulesEngine] BeginRewrite: replaced '{oldLetter}' with '{newLetter}' " +
                      // $"at ({col},{row}) player={playerIndex}");

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = row };
        }

        // ── BeginSwapResolution ──────────────────────────────────────────────────────

        /// <summary>
        /// Initializes the step-by-step resolution for a legal swap.
        /// The swap has already been performed and words detected externally.
        /// This sets up the state so NextStep() can run trigger detection,
        /// detonation, gravity, and chain resolution.
        /// </summary>
        public void BeginSwapResolution(List<RulesWordMatch> detectedWords, int playerIndex, HashSet<int> justPrimedIds)
        {
            _tapResolution = false;   // a word the PLAYER made may chain; see _tapResolution
            LastResolutionWasSwap   = true; // swap/edit-initiated → swap-tutorial objectives count these explosions
            _stepPlayerIndex        = playerIndex;
            _stepChainDepth         = 0;
            _stepTotalScore         = 0;
            _stepChainTriggeredCount = 0;
            _splashFiredThisResolution = false;
            _stepJustPrimed         = justPrimedIds ?? new HashSet<int>();
            _stepScoredKeys         = new HashSet<string>();
            _stepPendingWords       = detectedWords;
            _stepTriggerWords       = null;
            _stepPendingTriggers    = null;

            // Seed cells from all detected swap words (needed for post-gravity chain detection)
            var seeds = new List<Vector2Int>();
            if (detectedWords != null)
                foreach (var w in detectedWords)
                    if (w.Cells != null)
                        seeds.AddRange(w.Cells);
            _stepSeedCells = seeds;

            // Skip straight to trigger checking — words are already scored and primed.
            // NextStep() at WordsScored calls DoCheckTriggers().
            _currentPhase = ResolutionPhase.WordsScored;

//             WDLog.Log($"[RulesEngine] BeginSwapResolution: {detectedWords.Count} word(s) from swap, " +
                      // $"{_stepJustPrimed.Count} just-primed, player={playerIndex}");
        }

        // ── BeginTapDetonation (ARMED-WEB) ───────────────────────────────────────────

        /// <summary>2026-09-03 ARMED-WEB: the PLAYER taps a primed word to fire it.
        /// Seeds the resolution with that word plus every primed word connected to it,
        /// then hands off to the SAME staggered chain payoff the auto-fuse used
        /// (DoCheckTriggers → DoExplode → gravity → cascade). Returns false if the tap
        /// did not land on a primed word.</summary>
        /// <summary>How many primed words contain this cell — lets the input layer ask
        /// "is this tile part of an armed word?" without reaching into the registry.</summary>
        public int GetPrimedWordsContainingPublic(Vector2Int cell)
        {
            var hits = _primedRegistry != null ? _primedRegistry.GetPrimedWordsContaining(cell) : null;
            return hits != null ? hits.Count : 0;
        }

        /// <summary>Every cell of every primed word containing this cell — i.e. exactly what a
        /// tap here is about to detonate. Exists so the press can react on the WHOLE word rather
        /// than the one tile under the finger. Empty list if nothing is primed here. 2026-09-08.</summary>
        public System.Collections.Generic.List<Vector2Int> GetPrimedCellsContaining(Vector2Int cell)
        {
            var outCells = new System.Collections.Generic.List<Vector2Int>();
            var words = _primedRegistry != null ? _primedRegistry.GetPrimedWordsContaining(cell) : null;
            if (words == null) return outCells;
            for (int i = 0; i < words.Count; i++)
            {
                var c = words[i].Cells;
                if (c == null) continue;
                for (int j = 0; j < c.Count; j++)
                    if (!outCells.Contains(c[j])) outCells.Add(c[j]);
            }
            return outCells;
        }

        public bool BeginTapDetonation(Vector2Int cell, int playerIndex)
        {
            if (!ArmedWebMode) return false;
            var hits = _primedRegistry.GetPrimedWordsContaining(cell);
            if (hits == null || hits.Count == 0) return false;
            // INSTANT POP: everything lit goes off TOGETHER, like simultaneous matches in a match-3.
            // 2026-10-20 Spencer: "if a cascade ... creates two seperate clusters ... there is a long
            // delayed pause before the second cluster detonates". Seeding only the tapped web made the
            // second cluster wait out the first one's whole explode -> fall -> rescan (~1.6s, measured)
            // while sitting lit on screen. Each seed still expands to its own web below.
            // Short words (under INSTANT_POP_AUTO_MIN) are NOT swept in — they are waiting for the player
            // to tap them or extend them; only the web of the one actually tapped/fired goes. 2026-10-20.
            if (SeedAllOnNextTap)
            {
                SeedAllOnNextTap = false;
                var everything = _primedRegistry.GetAllPrimedWords();
                if (everything != null && everything.Count > 0) hits = new List<PrimedWordRegistry.PrimedWord>(everything);
            }
            else if (HandManager.InstantPopMode)
            {
                var all = _primedRegistry.GetAllPrimedWords();
                if (all != null)
                {
                    var merged = new List<PrimedWordRegistry.PrimedWord>(hits);
                    for (int i = 0; i < all.Count; i++)
                        if (all[i].Word != null && all[i].Word.Length >= HandManager.INSTANT_POP_AUTO_MIN && !merged.Contains(all[i]))
                            merged.Add(all[i]);
                    hits = merged;
                }
            }

            LastResolutionWasSwap    = true;   // player-initiated, like a swap
            _stepPlayerIndex         = playerIndex;
            _stepChainDepth          = 0;
            _stepTotalScore          = 0;
            _stepChainTriggeredCount = 0;
            _splashFiredThisResolution = false;
            _tapResolution           = true;
            _stepJustPrimed          = new HashSet<int>();   // nothing is exempt — the player chose
            _stepScoredKeys          = new HashSet<string>();
            _stepPendingWords        = new List<RulesWordMatch>();
            _stepTriggerWords        = null;
            _stepPendingTriggers     = null;

            // Seed with the tapped word(s); DoCheckTriggers expands to the whole
            // connected web via FindConnectedGroup, exactly as an auto-trigger did.
            _tapCell    = cell;
            _tapDiag    = true;
            _tapSeedIds = new HashSet<int>();
            for (int i = 0; i < hits.Count; i++) _tapSeedIds.Add(hits[i].Id);

            var seeds = new List<Vector2Int>();
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Cells != null) seeds.AddRange(hits[i].Cells);
            _stepSeedCells = seeds;

            _currentPhase = ResolutionPhase.WordsScored;
            WDLog.Log($"[ArmedWeb] Tap at ({cell.x},{cell.y}) -> seeding {_tapSeedIds.Count} primed word(s)");
            return true;
        }

        /// <summary>True while a resolution that the player STARTED BY TAPPING is running.
        ///
        /// Crossing a primed word to link it into the web is the core verb — but it is meant to
        /// happen when the PLAYER makes a word, not off the back of a cascade they did not
        /// author. Without this flag, tapping one web detonated it, the falling tiles happened
        /// to form a word that overlapped a completely separate primed cluster on the other side
        /// of the board, and that cluster detonated too — the player never chose it, and its
        /// outline was left hanging over the empty cells. 2026-09-10 Spencer.</summary>
        private bool _tapResolution;

        /// <summary>Ids the player's tap seeded this resolution; consumed by DoCheckTriggers.</summary>
        private HashSet<int> _tapSeedIds;
        private Vector2Int   _tapCell;
        private bool         _tapDiag;

        // ── BeginCascadeAfterBoosterClear ────────────────────────────────────────────

        /// <summary>
        /// Booster-clear entry point. Caller (a booster) has already nulled the
        /// destroyed cells in _board via ClearCell. This method performs the
        /// equivalent of DoExplode's post-clear bookkeeping (PurgeScoredKeys for
        /// the cleared cells) plus the full DoGravity routine (gravity in data,
        /// PrimedRegistry position update, scored-key purge for old+new gravity
        /// cells, seed ALL occupied cells in moved columns, RemoveInvalidPrimedWords).
        ///
        /// After this returns, _currentPhase is GravityApplied and the next
        /// NextStep() call routes into DoDetectWords against the seed cells —
        /// so post-booster gravity will form, prime, score, and detonate words
        /// the same way a player-drop cascade does.
        ///
        /// Returns the gravity moves dictionary (old → new positions) so the
        /// visual layer can animate falls via GridManager.ApplyGravityFromEvents.
        /// An empty result means no tiles fell and no cascade is possible.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> BeginCascadeAfterBoosterClear(
            IEnumerable<Vector2Int> clearedCells, int playerIndex)
        {
            // 1. Purge scored-key bookkeeping for the cells the booster cleared —
            //    parallel to DoExplode line ~4406. Without this, a previously
            //    scored word at these cells would silently block re-detection.
            var clearedList = new List<Vector2Int>();
            if (clearedCells != null)
                foreach (var c in clearedCells) clearedList.Add(c);
            if (clearedList.Count > 0)
                PurgeScoredKeysForCells(clearedList);

            // 2. Init step state — mirrors BeginDrop / BeginRewrite.
            _stepPlayerIndex           = playerIndex;
            _stepChainDepth            = 0;
            _stepTotalScore            = 0;
            _splashFiredThisResolution = false;
            _stepChainTriggeredCount   = 0;
            _stepJustPrimed            = new HashSet<int>();
            _stepScoredKeys            = new HashSet<string>();
            _stepPendingWords          = null;
            _stepTriggerWords          = null;
            _stepPendingTriggers       = null;
            _stepSeedCells             = new List<Vector2Int>();

            // 3. Apply gravity + full DoGravity bookkeeping. Mirrors DoGravity
            //    at line ~4428 — must stay in sync.
            var gravityMoves = ApplyGravityInData();

            if (gravityMoves.Count > 0)
            {
                _primedRegistry.UpdateCellPositions(gravityMoves);

                var affectedCells = new List<Vector2Int>(gravityMoves.Count * 2);
                foreach (var kvp in gravityMoves)
                {
                    affectedCells.Add(kvp.Key);
                    affectedCells.Add(kvp.Value);
                }
                PurgeScoredKeysForCells(affectedCells);
                PurgeStepScoredKeysForCells(affectedCells);

                // Seed every occupied cell in moved columns — stationary tiles
                // can combine with newly-fallen tiles to form words (e.g. AMP
                // where A and M were already in place and only P fell).
                var movedCols = new HashSet<int>();
                foreach (var kvp in gravityMoves)
                    movedCols.Add(kvp.Value.x);

                foreach (int col in movedCols)
                {
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (_board[col, row] != null)
                            _stepSeedCells.Add(new Vector2Int(col, row));
                    }
                }
            }

            RemoveInvalidPrimedWords();

            _currentPhase = ResolutionPhase.GravityApplied;

            WDLog.Log($"[RulesEngine] BeginCascadeAfterBoosterClear: " +
                      $"cleared={clearedList.Count}, moves={gravityMoves.Count}, " +
                      $"seeds={_stepSeedCells.Count}, player={playerIndex}");

            // BoosterDbg: full moves dump + cleared list — lets us audit every
            // tile that should have fallen vs what actually rendered.
            if (clearedList.Count > 0)
            {
                var cb = new System.Text.StringBuilder();
                for (int i = 0; i < clearedList.Count; i++)
                {
                    if (i > 0) cb.Append(",");
                    cb.Append($"({clearedList[i].x},{clearedList[i].y})");
                }
                WDLog.Log($"[BoosterDbg] CascadeClearedList: {cb}");
            }
            if (gravityMoves.Count > 0)
            {
                var mb = new System.Text.StringBuilder();
                bool first = true;
                foreach (var kvp in gravityMoves)
                {
                    if (!first) mb.Append(",");
                    mb.Append($"({kvp.Key.x},{kvp.Key.y})→({kvp.Value.x},{kvp.Value.y})");
                    first = false;
                }
                WDLog.Log($"[BoosterDbg] CascadeGravityMoves: {mb}");
            }

            return gravityMoves;
        }

        // ── NextStep ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance the resolution by one step. Returns what happened.
        /// Keep calling until Phase == Complete.
        /// </summary>
        public StepResult NextStep()
        {
            switch (_currentPhase)
            {
                case ResolutionPhase.TileDropped:
                case ResolutionPhase.GravityApplied:
                    return DoDetectWords();

                case ResolutionPhase.WordsDetected:
                    return DoScoreAndPrime();

                case ResolutionPhase.WordsScored:
                    return DoCheckTriggers();

                case ResolutionPhase.TriggersFound:
                    return DoExplode();

                case ResolutionPhase.Exploding:
                    return DoGravity();

                default:
                    return new StepResult { Phase = ResolutionPhase.Complete };
            }
        }

        // ── FinalizeDrop ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Clean up after step-by-step resolution. The visual bridge calls this
        /// after all animation is complete. Increments _globalTurn.
        /// </summary>
        public void FinalizeDrop()
        {
            // Guard: FinalizeDrop is NOT safe to call twice — it increments _globalTurn
            // and expires primed words. The safe wrapper and Complete phase both call it,
            // so we must be idempotent.
            if (_currentPhase == ResolutionPhase.Idle)
            {
//                 WDLog.Log("[RulesEngine] FinalizeDrop: already Idle — skipping (idempotent guard).");
                return;
            }

            // Expire primed words AFTER resolution (not before).
            // This gives players one last chance to detonate on the expiry turn.
            var expiredList = new List<PrimedWordRegistry.PrimedWord>();
            int expired = _primedRegistry.ExpireOldWords(_globalTurn, expiredList);
            if (expired > 0)
            {
                // Purge scored keys so expired words can be re-primed
                foreach (var pw in expiredList)
                    _scoredWordKeys.Remove(pw.Word + "|" + pw.CellsString());
//                 WDLog.Log($"[RulesEngine] FinalizeDrop: expired {expired} primed word(s), scored keys purged");

                // 2026-06-03 Spencer: diffuse cue — these words extinguished without
                // detonating. Collect their tiles and hand them to WordDropFX for a
                // DEFERRED springy pop (deferred so the rising row can't DOComplete it
                // away). Tile objects survive gravity/rise; the pop lands wherever they
                // end up.
                if (GridManager.Instance != null && WordDropFX.Instance != null)
                {
                    var diffuseTiles = new List<Tile>();
                    foreach (var pw in expiredList)
                    {
                        if (pw.Cells == null) continue;
                        for (int c = 0; c < pw.Cells.Count; c++)
                        {
                            Tile t = GridManager.Instance.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                            if (t != null) diffuseTiles.Add(t);
                        }
                    }
                    WDLog.Log($"[DiffusePop] FinalizeDrop expired {expired} word(s) → queued {diffuseTiles.Count} tile(s) (turn={_globalTurn})");
                    WordDropFX.Instance.QueueDiffusePops(diffuseTiles);
                }
            }

            RemoveInvalidPrimedWords();

            // Prime pitch-ramp: keep CLIMBING across consecutive priming drops while charged words are still on the
            // board (the combo is building); reset to base pitch only once they've ALL resolved — detonated or
            // expired — so the next charge-up starts the scale over. 2026-07-14 Spencer.
            if (_primedRegistry.Count == 0) GameAudio.Instance?.ResetPrimeRamp();

            _globalTurn++;
            _currentPhase = ResolutionPhase.Idle;

            _stepJustPrimed      = null;
            _stepScoredKeys      = null;
            _stepPendingWords    = null;
            _stepPendingTriggers = null;

//             WDLog.Log($"[RulesEngine] FinalizeDrop: turn incremented to {_globalTurn}, phase=Idle.");
        }

        // ── FinalizeBoosterCascade ───────────────────────────────────────────────────

        /// <summary>
        /// Cleanup variant for booster-initiated cascades. Mirrors FinalizeDrop
        /// EXCEPT it does NOT increment _globalTurn and does NOT expire primed
        /// words. Boosters are turn-neutral by design — using a booster should
        /// not age primed-word fuses or consume the player's "turn budget."
        ///
        /// Still runs RemoveInvalidPrimedWords so any primed-word entries that
        /// became invalid mid-cascade are culled, and resets the step state
        /// machine back to Idle.
        /// </summary>
        public void FinalizeBoosterCascade()
        {
            if (_currentPhase == ResolutionPhase.Idle) return;

            RemoveInvalidPrimedWords();
            if (_primedRegistry.Count == 0) GameAudio.Instance?.ResetPrimeRamp(); // reset only once all charged words resolved
            _currentPhase = ResolutionPhase.Idle;

            _stepJustPrimed      = null;
            _stepScoredKeys      = null;
            _stepPendingWords    = null;
            _stepPendingTriggers = null;

            WDLog.Log("[RulesEngine] FinalizeBoosterCascade: phase=Idle (globalTurn unchanged).");
        }

        // ── Do* step methods ─────────────────────────────────────────────────────────

        private StepResult DoDetectWords()
        {
            // Safety cap on chain depth
            if (_stepChainDepth >= 10)
            {
                Debug.LogWarning("[RulesEngine] Step chain hit depth cap (10) — completing.");
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;
                return new StepResult { Phase = ResolutionPhase.Complete, TotalScore = _stepTotalScore };
            }

            // Seed-cell scan: only detect words physically containing a seed cell.
            // On first pass, seeds = drop position. After gravity, seeds = landing positions.
            List<RulesWordMatch> allWords = ScanSeedCellsOnly(_stepSeedCells);

            // Filter out already-scored words
            List<RulesWordMatch> newWords = new List<RulesWordMatch>();
            for (int i = 0; i < allWords.Count; i++)
            {
                string key = allWords[i].Word + "|" + allWords[i].CellKey;
                if (!_scoredWordKeys.Contains(key) && !_stepScoredKeys.Contains(key))
                    newWords.Add(allWords[i]);
            }

            if (newWords.Count == 0)
            {
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;
//                 WDLog.Log($"[RulesEngine] DoDetectWords: no new words — resolution complete (chainDepth={_stepChainDepth}).");
                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingWords = newWords;
            _currentPhase = ResolutionPhase.WordsDetected;

//             WDLog.Log($"[RulesEngine] DoDetectWords: found {newWords.Count} new word(s) at chainDepth={_stepChainDepth}.");

            return new StepResult
            {
                Phase = ResolutionPhase.WordsDetected,
                NewWords = new List<RulesWordMatch>(newWords),
                TotalScore = _stepTotalScore,
            };
        }

        private StepResult DoScoreAndPrime()
        {
            var scoredEvents = new List<WordScoredEvent>();

            // Cluster bonus: when multiple primed words trigger in one step
            // (connected-cluster detonation), each word's effective chain depth
            // gets boosted by (count - 1). Without this, a 4-word cluster
            // detonation would score each word at chainStep=0 — massive visual
            // moment, flat score. See decision log 2026-04-17.
            int effectiveChainStep = _stepChainDepth + Mathf.Max(0, _stepPendingWords.Count - 1);

            // NOTE: this loop SCORES and PRIMES the words a cascade forms. It is NOT the
            // trigger loop — that is the second pass over _stepPendingWords further down, and
            // that is the one gated on _tapResolution. Gating THIS one (2026-09-10, briefly)
            // stopped cascade words scoring or priming at all during a tap, which is how a
            // cascade could spell BOMB and leave it sitting there dead.
            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];
                string key = match.Word + "|" + match.CellKey;

                int baseScore  = CalculateWordScore(match.Word, match.WildLetterIndices);
                // Multiplicative chain bonus — matches the legacy ProcessDrop path.
                // Risky chain plays pay substantially more than safe solo drops.
                float chainMult = (effectiveChainStep > 0)
                    ? 1f + Mathf.Min(effectiveChainStep, CHAIN_DEPTH_SCALE_CAP) * 0.5f
                    : 1f;
                int chainBoosted = Mathf.RoundToInt(baseScore * chainMult);
                int echoBonus  = ConsumeEchoBonus(match.Word, _stepPlayerIndex);
                bool isGoldWord = HasGoldTile(match);
                int bonusMult  = ConsumeGoldAndGetMultiplier(match);
                int finalScore = (chainBoosted + echoBonus) * bonusMult;
                match.Score    = finalScore;
                _stepTotalScore += finalScore;

                WDLog.Log($"[RulesEngine] Scored '{match.Word}': base={baseScore}" +
                          (chainBoosted - baseScore > 0 ? $" +chain({chainBoosted - baseScore}, effStep={effectiveChainStep})" : "") +
                          (echoBonus > 0 ? $" +echo({echoBonus})" : "") +
                          $" = {finalScore} pts  [rawStep={_stepChainDepth}, clusterSize={_stepPendingWords.Count}]");

                _scoredWordKeys.Add(key);
                _stepScoredKeys.Add(key);

                // Prime in registry — gold words get gold primed status
                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId  = _primedRegistry.AddPrimedWord(
                    match.Word,
                    match.Cells,
                    _stepPlayerIndex,
                    _globalTurn,
                    expiresOn,
                    finalScore,
                    isGoldWord,
                    HasHighValueLetter(match));

                _stepJustPrimed.Add(primedId);

                var evt = new WordScoredEvent
                {
                    Word        = match.Word,
                    Cells       = new List<Vector2Int>(match.Cells),
                    BaseScore   = baseScore,
                    FinalScore  = finalScore,
                    PlayerIndex = _stepPlayerIndex,
                    ChainStep   = _stepChainDepth,
                };
                scoredEvents.Add(evt);

                // Fire event so listeners (tutorial, etc.) can react
                OnWordScored?.Invoke(evt);

                // 2026-05-28: Path A bugfix — this scoring path adds the word to
                // the primed registry above (AddPrimedWord) but historically
                // didn't fire OnWordPrimed, unlike the ProcessDrop path at
                // line ~1691. Any listener relying on OnWordPrimed (e.g., the
                // ZeroFloorRefund handler in MatchController) missed events
                // formed through this step-resolver path. Adding the missing
                // invoke to match the other path's contract.
                var primedEvt = new WordPrimedEvent
                {
                    Word          = match.Word,
                    Cells         = new List<Vector2Int>(match.Cells),
                    PlayerIndex   = _stepPlayerIndex,
                    PrimedOnTurn  = _globalTurn,
                    ExpiresOnTurn = expiresOn,
                    PrimedWordId  = primedId,
                };
                OnWordPrimed?.Invoke(primedEvt);

//                 WDLog.Log($"[RulesEngine] DoScoreAndPrime: '{match.Word}' base={baseScore}" +
//                           (chainBonus > 0 ? $" +chain({chainBonus})" : "") +
//                           $" = {finalScore} pts  [chain={_stepChainDepth}] primedId={primedId}");
            }

            // ── Global Fuse Reset ──────────────────────────────────────────
            // When ANY new word primes, reset ALL existing primed words to
            // the same timer. Keeps the entire board in sync.
            if (_stepJustPrimed.Count > 0)
                ResetExistingPrimedWordsForNewPrime(_stepJustPrimed, _globalTurn);

            _currentPhase = ResolutionPhase.WordsScored;

            return new StepResult
            {
                Phase = ResolutionPhase.WordsScored,
                ScoredWords = scoredEvents,
                TotalScore = _stepTotalScore,
            };
        }

        /// <summary>
        /// Public wrapper so non-standard prime paths (BoardSwap) can trigger
        /// the global fuse reset. Normal BeginDrop / BeginRewrite flows already
        /// hit this through DoScoreAndPrime.
        /// </summary>
        public void ResetExistingPrimedWordsExternal(HashSet<int> justPrimedIds)
        {
            ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);
        }

        private void ResetExistingPrimedWordsForNewPrime(HashSet<int> justPrimedIds, int currentTurn)
        {
            // DISABLED 2026-04-18 per Spencer's diagnosis. Global fuse reset removed
            // the "fuse pressure" design goal: a player could indefinitely prime new
            // words to reset ALL existing primes' timers, making setups immortal.
            // Without it, primes now have independent timers from creation — players
            // must commit to detonating rather than infinitely stacking.
            return;

#pragma warning disable CS0162 // Unreachable code (kept for easy revert)
            if (justPrimedIds == null || justPrimedIds.Count == 0 || _primedRegistry == null) return;

            int newFuseLength = 0;
            float newMaxAge = 25f;
            foreach (int newId in justPrimedIds)
            {
                var np = _primedRegistry.GetById(newId);
                if (np == null) continue;
                int fuse = GetFuseLength(np.Word.Length);
                if (fuse > newFuseLength) newFuseLength = fuse;
                if (np.MaxAgeSeconds > newMaxAge) newMaxAge = np.MaxAgeSeconds;
            }
            if (newFuseLength <= 0) return;

            float now = Time.time;
            int resetCount = 0;
            for (int p = 0; p < _primedRegistry.Count; p++)
            {
                var pw = _primedRegistry.GetByIndex(p);
                if (pw == null) continue;
                if (justPrimedIds.Contains(pw.Id)) continue;
                if (pw.GlobalFuseResetCount >= MAX_GLOBAL_FUSE_RESETS) continue;

                pw.CreatedAtTime = now;
                pw.MaxAgeSeconds = newMaxAge;
                pw.ExpiresOnTurn = currentTurn + newFuseLength;
                pw.PrimedOnTurn = currentTurn;
                pw.GlobalFuseResetCount++;

                RefreshPrimedWordTiles(pw, currentTurn, heatOverride: 0);
                resetCount++;
            }

            if (resetCount > 0)
                WDLog.Log($"[FuseReset] Total: {resetCount} words reset, {_primedRegistry.Count} total primed");
#pragma warning restore CS0162
        }

        /// <summary>Ids already given the "you armed a word" flash, so a refresh after
        /// every gravity step re-applies the glow WITHOUT re-flashing the whole board.</summary>
        private readonly HashSet<int> _flashedPrimedIds = new HashSet<int>();

        public void RefreshAllPrimedWordTiles(int currentTurn = -1, bool flashNew = false)
        {
            if (_primedRegistry == null) return;
            int turn = currentTurn >= 0 ? currentTurn : _globalTurn;

            bool anyNew = false;
            for (int p = 0; p < _primedRegistry.Count; p++)
            {
                var pw = _primedRegistry.GetByIndex(p);
                if (pw == null) continue;
                // 2026-09-03: words formed by a CASCADE prime just like a dropped word,
                // so they get the same arm flash. Judged by the word's OWN PrimedOnTurn —
                // tracking flashed ids failed because primed-word ids are REUSED, so new
                // cascade words came back with ids already marked as flashed.
                bool isNew = flashNew && pw.PrimedOnTurn >= turn;
                RefreshPrimedWordTiles(pw, turn, playFlash: isNew);
                if (isNew) anyNew = true;
            }

            if (anyNew)
            {
                GameAudio.Instance?.PlayTilePrimed();
                HapticsManager.Light();
            }
        }

        /// <summary>Does the connected web this word belongs to contain ANY gold word — a
        /// gold-bonus word, or one carrying a high-value letter?
        ///
        /// Deliberately NOT cached. A flood fill over the primed registry sounds expensive until
        /// you count it: a live web is a handful of words, so this is a few dozen set operations
        /// on a refresh that is already walking every primed tile. A frame-keyed cache would buy
        /// nothing measurable and would have to be invalidated on prime, unprime, detonation and
        /// every rise that shifts cell positions — four chances to serve a stale colour, which is
        /// precisely the bug class this repaint already exists to fix. 2026-10-20.</summary>
        private bool ClusterHasGold(PrimedWordRegistry.PrimedWord pw)
        {
            if (_primedRegistry == null || pw == null) return false;
            var group = _primedRegistry.FindConnectedGroup(new HashSet<int> { pw.Id }, null);
            if (group == null) return false;
            for (int i = 0; i < group.Count; i++)
                if (group[i].IsGold || group[i].HasHighValueLetter) return true;
            return false;
        }

        private void RefreshPrimedWordTiles(PrimedWordRegistry.PrimedWord pw, int currentTurn, int heatOverride = -1, bool playFlash = false)
        {
            if (GridManager.Instance == null || pw == null || pw.Cells == null) return;

            int survived = Mathf.Max(0, currentTurn - pw.PrimedOnTurn);
            int heatLevel = heatOverride >= 0 ? heatOverride : Mathf.Min(survived, HEAT_FUSE_MAX_BONUS);
            int fuseRemaining = Mathf.Max(0, pw.ExpiresOnTurn - currentTurn);
            // GOLD primed colour for gold-bonus words AND for words containing a
            // high-value letter (visual parity with the gold resting face).
            //
            // 2026-10-20 Spencer: "when you have a cluster that contains a gold letter I think
            // the WHOLE cluster should be gold." Gold used to spread only as far as a SHARED
            // CELL, so a web of four words with one gold letter in it rendered as a gold patch
            // welded to rose words — which reads as a rendering fault rather than a feature,
            // because the player already thinks of the web as one object they are about to
            // detonate as one object. Ask the whole connected GROUP, not just this word and the
            // cells it overlaps.
            bool goldLook = pw.IsGold || pw.HasHighValueLetter || ClusterHasGold(pw);
            Color glowColor = goldLook ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;

            for (int c = 0; c < pw.Cells.Count; c++)
            {
                Tile tile = GridManager.Instance.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                if (tile == null) continue;

                // GOLD WINS, ALWAYS (2026-09-10 Spencer: "a gold word should take precedence
                // over everything else. no half colours if there's two words that overlap").
                // This method repaints PER WORD, so on a shared tile whichever word refreshed
                // last won — a rose word would stomp the gold another word had just set, and the
                // tile flickered between the two. Ask the CELL whether any word on it is gold,
                // not just the word being refreshed.
                // With goldLook now derived from the whole cluster this is a subset of it —
                // a word sharing a cell is by definition connected. Kept because a tile can
                // also be shared with a word that is NOT in this registry pass yet (mid-prime),
                // where the per-cell question still answers correctly and costs nothing.
                bool cellGold = goldLook;
                if (!cellGold)
                {
                    var onCell = _primedRegistry.GetPrimedWordsContaining(pw.Cells[c]);
                    if (onCell != null)
                        for (int w = 0; w < onCell.Count && !cellGold; w++)
                            if (onCell[w].IsGold || onCell[w].HasHighValueLetter) cellGold = true;
                }
                Color cellColor = cellGold ? Tile.PRIMED_GOLD_GLOW : glowColor;

                tile.SetPrimedGlow(cellColor, playFlash: playFlash, heatLevel: heatLevel, fuseRemaining: fuseRemaining, isGold: cellGold, maxAge: pw.MaxAgeSeconds);
            }
        }

        private StepResult DoCheckTriggers()
        {
            var triggeredIds = new HashSet<int>();
            var triggerEvents = new List<PrimedTriggeredEvent>();

            // ARMED-WEB: a player TAP seeds the trigger set directly. Everything below
            // (connected-group expansion, DoExplode, the staggered payoff) then runs
            // exactly as it did for an auto-trigger.
            if (_tapSeedIds != null && _tapSeedIds.Count > 0)
            {
                Vector2Int cellTapped = _tapCell;
                foreach (int id in _tapSeedIds)
                {
                    var pw = _primedRegistry.GetById(id);
                    if (pw == null || triggeredIds.Contains(id)) continue;
                    triggeredIds.Add(id);
                    triggerEvents.Add(new PrimedTriggeredEvent
                    {
                        TriggeredWord  = pw.Word,
                        TriggeredCells = new List<Vector2Int>(pw.Cells),
                        TriggerWord      = "TAP",        // player-initiated, no crossing word
                        OverlapCell      = cellTapped,
                        OwnerPlayerIndex = pw.OwnerPlayer,
                        PrimedWordId     = pw.Id,
                    });
                }
                _tapSeedIds = null;
            }

            // Bonus Mode redesign (2026-04-18): auto-detonate removed. Bonus is now
            // "5 free moves" — normal gameplay continues, just without rising-row
            // tick or stage-budget cost. No triangular scoring, no forced ignition.

//             WDLog.Log($"[RulesEngine] DoCheckTriggers: {_stepPendingWords.Count} new word(s), " +
                      // $"{_primedRegistry.Count} primed word(s) on board, " +
                      // $"{_stepJustPrimed.Count} just-primed this resolution");

            // A tap detonates the web the player CHOSE and nothing else. Words the cascade
            // throws up still score and prime normally (loop above) — they just cannot ignite
            // an unrelated primed cluster on the far side of the board. 2026-09-10 Spencer.
            for (int w = 0; !_tapResolution && w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];

                for (int c = 0; c < match.Cells.Count; c++)
                {
                    Vector2Int cell = match.Cells[c];

                    // 1. Direct overlap triggers (original behavior — always active)
                    List<PrimedWordRegistry.PrimedWord> overlapping =
                        _primedRegistry.GetPrimedWordsContaining(cell);

                    for (int p = 0; p < overlapping.Count; p++)
                    {
                        PrimedWordRegistry.PrimedWord pw = overlapping[p];

                        // Phase 9.7: cascade rule — at chainDepth 0 (initial
                        // drop/rewrite) the just-primed filter stays (trigger
                        // word shouldn't self-detonate on its own drop). At
                        // chainDepth >= 1 (gravity-formed words during a
                        // cascade) skip the filter so new words can trigger
                        // prior-step primes AND self-overlap to propagate.
                        //
                        // Phase 11+ extension: Survival now uses the same
                        // cascade rule as Level mode. Spencer's ask —
                        // gravity-formed Survival words should fall, prime,
                        // and explode on their own the way Level-mode cascades
                        // do. Stone-chain propagation stays Level-only
                        // (Phase 9.9): Survival's sparse stones + one-hop
                        // clear is still the intended tuning for that axis.
                        bool skipJustPrimedFilter =
                            (GameManager.IsLevelMode || SurvivalManager.IsSurvivalMode)
                            && _stepChainDepth > 0;
                        if (!skipJustPrimedFilter && _stepJustPrimed.Contains(pw.Id)) continue;
                        if (triggeredIds.Contains(pw.Id)) continue;
                        // ARMED-WEB: crossing LINKS the words (they are already in the
                        // registry, so FindConnectedGroup will treat them as one web)
                        // but must NOT detonate. The player pulls the trigger.
                        if (ArmedWebMode) continue;

                        triggeredIds.Add(pw.Id);
//                         WDLog.Log($"[RulesEngine] DoCheckTriggers: '{match.Word}' OVERLAPS primed '{pw.Word}' (id={pw.Id}) at ({cell.x},{cell.y})");

                        triggerEvents.Add(new PrimedTriggeredEvent
                        {
                            TriggeredWord    = pw.Word,
                            TriggeredCells   = new List<Vector2Int>(pw.Cells),
                            TriggerWord      = match.Word,
                            OverlapCell      = cell,
                            OwnerPlayerIndex = pw.OwnerPlayer,
                            PrimedWordId     = pw.Id,
                        });
                        OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                    }

                    // 2. Adjacency triggers (new — behind kill switch)
                    // A new word TOUCHING a primed word (orthogonal neighbor) triggers it.
                    // This makes buried primes reachable and improves flow.
                    if (AdjacencyTriggerEnabled)
                    {
                        List<PrimedWordRegistry.PrimedWord> adjacent =
                            _primedRegistry.GetPrimedWordsAdjacentTo(cell);

                        for (int p = 0; p < adjacent.Count; p++)
                        {
                            PrimedWordRegistry.PrimedWord pw = adjacent[p];

                            if (_stepJustPrimed.Contains(pw.Id)) continue;
                            if (triggeredIds.Contains(pw.Id)) continue;
                            if (ArmedWebMode) continue;   // adjacency links, never fires

                            triggeredIds.Add(pw.Id);
//                             WDLog.Log($"[RulesEngine] DoCheckTriggers: '{match.Word}' ADJACENT to primed '{pw.Word}' (id={pw.Id}) at ({cell.x},{cell.y})");

                            triggerEvents.Add(new PrimedTriggeredEvent
                            {
                                TriggeredWord    = pw.Word,
                                TriggeredCells   = new List<Vector2Int>(pw.Cells),
                                TriggerWord      = match.Word,
                                OverlapCell      = cell,
                                OwnerPlayerIndex = pw.OwnerPlayer,
                                PrimedWordId     = pw.Id,
                            });
                            OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                        }
                    }
                }
            }

            // ── Connected chain expansion ──
            // If any primed words were directly triggered, find all connected primed words
            // (transitively via shared tiles) and include them in the detonation group.
            int chainTriggeredCount = 0;
            if (triggeredIds.Count > 0)
            {
                // Align the just-primed exclusion with the trigger gate above (skipJustPrimedFilter): during a
                // cascade (chainDepth > 0) in level/survival, words primed THIS resolution ARE allowed to trigger,
                // so they must also be allowed to JOIN the connected chain group. Was: always excluded here →
                // cascade-formed connected primes silently failed to chain. 2026-07-08 Spencer.
                // ARMED-WEB (2026-09-09 Spencer): words primed THIS resolution — the ones
                // gravity just formed — must NOT be swept into the detonation that created
                // them. That is the "explode one set and another primed area goes off by
                // itself" behaviour. It was correct for the drop game, where cascades were
                // meant to self-propagate; here the player pulls every trigger, so a cascade
                // word primes and WAITS to be tapped. Excluding them always keeps that true.
                var chainExclude = (!ArmedWebMode
                                    && (GameManager.IsLevelMode || SurvivalManager.IsSurvivalMode)
                                    && _stepChainDepth > 0)
                    ? null : _stepJustPrimed;
                var connectedGroup = _primedRegistry.FindConnectedGroup(triggeredIds, chainExclude);
                for (int g = 0; g < connectedGroup.Count; g++)
                {
                    var pw = connectedGroup[g];
                    if (triggeredIds.Contains(pw.Id)) continue; // already a direct trigger

                    triggeredIds.Add(pw.Id);
                    chainTriggeredCount++;
                    // 2026-09-10: is the chained word REALLY sharing a tile with the tapped web,
                    // or is the grouping looser than the outline's? Prints the cells so a run can
                    // answer it instead of another guess. Remove once settled.
                    if (_tapDiag)
                        WDLog.Log($"[ChainDiag] chained '{pw.Word}' cells=[{string.Join(" ", (pw.Cells ?? new List<Vector2Int>()).ConvertAll(c => $"({c.x},{c.y})"))}]");

//                     WDLog.Log($"[PrimedChain] Chain-connected: '{pw.Word}' (id={pw.Id}) added to detonation group");

                    triggerEvents.Add(new PrimedTriggeredEvent
                    {
                        TriggeredWord    = pw.Word,
                        TriggeredCells   = new List<Vector2Int>(pw.Cells),
                        TriggerWord      = "chain",
                        OverlapCell      = pw.Cells[0],
                        OwnerPlayerIndex = pw.OwnerPlayer,
                        PrimedWordId     = pw.Id,
                        IsChainTrigger   = true,
                    });

                    OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                }

                if (chainTriggeredCount > 0)
                {
//                     WDLog.Log($"[PrimedChain] ConnectedGroup size={triggeredIds.Count} " +
                              // $"(direct={triggeredIds.Count - chainTriggeredCount}, chain={chainTriggeredCount}) " +
                              // $"words=[{string.Join(", ", triggerEvents.ConvertAll(e => e.TriggeredWord))}]");
                }
            }

            // ── Touch-Extends-Fuse (Phase 11f) ──
            // New primes that orthogonally touch (or overlap) a NON-detonating
            // existing primed word grant it +1 fuse, capped at
            // MAX_OVERLAP_FUSE_BONUS per prime over its lifetime. Does NOT
            // trigger detonation.
            if (_stepJustPrimed != null && _stepJustPrimed.Count > 0)
            {
                HashSet<int> alreadyExtendedThisStep = new HashSet<int>();
                foreach (int newId in _stepJustPrimed)
                {
                    var newPw = _primedRegistry.GetById(newId);
                    if (newPw == null || newPw.Cells == null) continue;

                    for (int p = 0; p < _primedRegistry.Count; p++)
                    {
                        var oldPw = _primedRegistry.GetByIndex(p);
                        if (oldPw == null) continue;
                        if (_stepJustPrimed.Contains(oldPw.Id)) continue;
                        if (triggeredIds.Contains(oldPw.Id)) continue;
                        if (alreadyExtendedThisStep.Contains(oldPw.Id)) continue;
                        if (oldPw.OverlapFuseBonusGranted >= MAX_OVERLAP_FUSE_BONUS) continue;

                        bool touches = false;
                        for (int c = 0; c < newPw.Cells.Count && !touches; c++)
                            for (int d = 0; d < oldPw.Cells.Count && !touches; d++)
                            {
                                int ddx = Mathf.Abs(newPw.Cells[c].x - oldPw.Cells[d].x);
                                int ddy = Mathf.Abs(newPw.Cells[c].y - oldPw.Cells[d].y);
                                if (ddx + ddy <= 1) touches = true;
                            }

                        if (touches)
                        {
                            oldPw.ExpiresOnTurn += OVERLAP_FUSE_EXTENSION;
                            oldPw.OverlapFuseBonusGranted += OVERLAP_FUSE_EXTENSION;
                            alreadyExtendedThisStep.Add(oldPw.Id);
                            RefreshPrimedWordTiles(oldPw, _globalTurn);
                            WDLog.Log($"[OverlapFuse] '{newPw.Word}' touched '{oldPw.Word}' " +
                                      $"→ +{OVERLAP_FUSE_EXTENSION} fuse " +
                                      $"(expires={oldPw.ExpiresOnTurn}, " +
                                      $"granted={oldPw.OverlapFuseBonusGranted}/{MAX_OVERLAP_FUSE_BONUS})");
                        }
                    }
                }
            }

            if (_tapDiag)
            {
                _tapDiag = false;
                WDLog.Log($"[ArmedWeb] DoCheckTriggers end: triggeredIds={triggeredIds.Count} " +
                          $"chainAdded={chainTriggeredCount} phase->{(triggeredIds.Count==0?"Complete":"TriggersFound")}");
            }
            if (triggeredIds.Count == 0)
            {
                // No triggers — resolution done for this chain
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;

//                 WDLog.Log($"[RulesEngine] DoCheckTriggers: no triggers — resolution complete.");

                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingTriggers = new List<int>(triggeredIds);
            _stepChainTriggeredCount = chainTriggeredCount;
            _stepTriggerWords = _stepPendingWords != null ? new List<RulesWordMatch>(_stepPendingWords) : null;
            _currentPhase = ResolutionPhase.TriggersFound;

            // Compute longest primed word NOW so BigBurstFlash's big-moment gate
            // can evaluate it at TriggersFound. Without this, LongestWordLength
            // defaults to 0 here (only Exploding populates it), causing the gate
            // to silently fail on word-length qualification.
            int longestInCluster = 0;
            foreach (int pid in triggeredIds)
            {
                var pw = _primedRegistry.GetById(pid);
                if (pw != null && pw.Word != null && pw.Word.Length > longestInCluster)
                    longestInCluster = pw.Word.Length;
            }

            return new StepResult
            {
                Phase = ResolutionPhase.TriggersFound,
                Triggers = triggerEvents,
                TotalScore = _stepTotalScore,
                ChainTriggeredCount = chainTriggeredCount,
                // Chain depth at trigger time — drives BigBurstFlash's big-moment gate.
                // Exploding's own _stepChainDepth increments PER CLUSTER WORD; we need
                // the iteration-level depth here, which is what ProcessDrop's chainStep
                // tracks. _stepChainDepth at this point reflects the chain iteration.
                ChainDepth = _stepChainDepth,
                LongestWordLength = longestInCluster,
                // The words the player/chain just formed that caused the trigger.
                // HandManager blasts each one separately so the "your word" gets a
                // flash too, not only the primed words it ignited.
                TriggerWords = _stepTriggerWords != null ? new List<RulesWordMatch>(_stepTriggerWords) : null,
            };
        }

        // FROZEN / ICE (2026-06-12): a frozen tile caught in a detonation THAWS instead of being
        // destroyed — it survives in place with its ice cleared. Returns true if the cell was frozen
        // (caller must NOT null the board cell and must NOT add it to allExplodedCells — the tile
        // stays). Returns false for normal cells (caller proceeds with the usual destroy). v1 clears
        // the single ice layer outright; a future multi-layer would decrement a counter and only
        // return true (skip destroy) once it reaches 0 — but ALWAYS skip destroy while frozen.
        private bool TryThawCell(Vector2Int cell, List<Vector2Int> thawed)
        {
            if (!InBounds(cell.x, cell.y)) return false;
            var data = _board[cell.x, cell.y];
            if (data != null && data.IsFrozen)
            {
                data.IsFrozen = false;          // thaw: ice cleared, tile becomes a normal letter
                thawed.Add(cell);               // surface to the visual layer for the defrost VFX
                // Bounty rides the SAME accumulator the words use (_stepTotalScore -> StepResult
                // .TotalScore), so it flows through the one scoring path rather than a second one.
                _stepTotalScore += ICE_BOUNTY;
                return true;                    // tile survives — NOT destroyed, NOT in allExplodedCells
            }
            // Already thawed earlier THIS resolution (by the word) — a later splash sweep must not
            // then destroy the surviving letter. Strict: a once-frozen tile is NEVER destroyed.
            if (thawed.Contains(cell)) return true;
            return false;
        }

        // STRICT ICE (2026-06-15 Spencer): collateral AREA splash (column/row sweeps, junk-splash, the
        // 7-letter jackpot row) must NEVER touch ice — a still-frozen tile stays frozen, an already-
        // thawed survivor stays put. ONLY a WORD detonation (TryThawCell at the word-cell sites) clears
        // ice. Returns true = the caller skips this cell entirely (no thaw, no destroy).
        private bool IsIceProtected(Vector2Int cell, List<Vector2Int> thawed)
        {
            if (!InBounds(cell.x, cell.y)) return false;
            var data = _board[cell.x, cell.y];
            return (data != null && data.IsFrozen) || thawed.Contains(cell);
        }

        private StepResult DoExplode()
        {
            ShortWordCells = null;   // full board: from here on, only 4+ letter words can form by falling
            var allExplodedCells = new List<Vector2Int>();
            var formedWordCells = new HashSet<Vector2Int>();
            var thawedCells = new List<Vector2Int>(); // frozen tiles caught in this detonation → survive + defrost
            var specialSurvivors = new HashSet<Vector2Int>();
            var specialsCreated  = new List<KeyValuePair<Vector2Int, int>>();
            var specialSources   = new Dictionary<Vector2Int, List<Vector2Int>>();
            var specialQueue     = new Queue<SpecialFire>();
            var specialsFired    = new List<SpecialFire>();
            int detonationBonus = 0;
            int totalHeat = 0;
            int swapRefillCount = 0;
            int editRefillCount = 0;
            int wildRefillCount = 0;
            int longestPrimedWord = 0; // track for splash scaling
            int keyWordLen = 0; // longest word (primed OR trigger) detonated this resolution = chest-tier "key" (2026-06-12)
            // Per-cluster-word chain depth inflation. Reverted 2026-05-04 —
            // the per-word increment is load-bearing for the game's reward
            // economy: splash gates, meltdown thresholds, big-moment burst
            // gates, and Survival long-word rewards are all calibrated to
            // expect chain depth values that climb across a cluster's
            // word loop. Removing the inflation made the game flatter and
            // less fun. Codex flagged it as "wrong" but the gates around it
            // were tuned to it; the apparent bug is de facto game design.
            // Cluster-size boost in DoExplode removed 2026-04-18 — combined with
            // DoScoreAndPrime's cluster boost + raised cap=8, it made mega-combos
            // the only viable path. DoScoreAndPrime cluster boost alone is enough.

            // Snapshot splash sources BEFORE the loop destroys cells. Include both
            // detonating primed words and the trigger words that ignited them so
            // each word can contribute its own row/column sweep axis.
            var primedSplashSources = new List<List<Vector2Int>>();
            if (_stepPendingTriggers != null && _primedRegistry != null)
            {
                foreach (int pid in _stepPendingTriggers)
                {
                    var snapshotPw = _primedRegistry.GetById(pid);
                    if (snapshotPw?.Cells != null && snapshotPw.Cells.Count > 0)
                        primedSplashSources.Add(new List<Vector2Int>(snapshotPw.Cells));
                }
            }
            if (_stepTriggerWords != null)
            {
                foreach (var trigWord in _stepTriggerWords)
                    if (trigWord?.Cells != null && trigWord.Cells.Count > 0)
                    {
                        primedSplashSources.Add(new List<Vector2Int>(trigWord.Cells));
                        for (int tc = 0; tc < trigWord.Cells.Count; tc++)
                            formedWordCells.Add(trigWord.Cells[tc]);
                        if (trigWord.Word != null) keyWordLen = Mathf.Max(keyWordLen, trigWord.Word.Length); // trigger word is also a chest "key"
                        // Tutorial counting: the connecting word visibly blows up too — count it toward the
                        // objective so a "2 explosions of 4 words" board reads 4. Scoped to gated tutorial
                        // levels (flag OFF for every normal level). 2026-06-25 Spencer.
                        if (ObjectiveManager.CountConnectingWords && trigWord.Word != null)
                            // trigWord is a RulesWordMatch (freshly formed THIS resolution), not a registry PrimedWord —
                            // its edit-ness is already the current LastResolutionWasSwap, so the 2-arg gate is correct.
                            ObjectiveManager.Instance?.NotifyWordExploded(trigWord.Word, MatchController.PLAYER_HUMAN);
                    }
            }

            // COMBO objective: report how many CHARGED (human) words THIS single blast detonates at once —
            // ComboObjective completes on the first blast that reaches its target. Read-only / FX-neutral. 2026-07-06.
            int comboWordCount = 0;
            for (int ci = 0; ci < _stepPendingTriggers.Count; ci++)
            {
                var cpw = _primedRegistry.GetById(_stepPendingTriggers[ci]);
                if (cpw != null && cpw.OwnerPlayer == MatchController.PLAYER_HUMAN) comboWordCount++;
            }
            if (comboWordCount > 0) ObjectiveManager.Instance?.NotifyComboDetonated(comboWordCount);

            // FULL BOARD SPECIALS LADDER (2026-10-21 Spencer: "you arent making enough rockets and i dont think ive ever even
            // made a disco ball"). With 4+ letter words, 7 letters for a disco was near impossible (58 four-letter pops vs 5
            // of 5+ in one session). Candy Crush's ladder, one step up from its minimum:
            //   5 letters -> rocket   ·   two words CROSSING in one pop -> bomb at the crossing (CC's L/T wrapped candy)
            //   6+ letters -> disco ball.        Rising-row mode keeps 5 / 6 / 7+.
            var crossUse = new Dictionary<Vector2Int, int>();
            if (SurvivalManager.FullBoardActive)
                for (int ci = 0; ci < _stepPendingTriggers.Count; ci++)
                {
                    var cpw = _primedRegistry.GetById(_stepPendingTriggers[ci]);
                    if (cpw == null || cpw.Cells == null || cpw.Word == null || cpw.Word.Length < 4) continue;
                    foreach (var cc in cpw.Cells) { crossUse.TryGetValue(cc, out int n); crossUse[cc] = n + 1; }
                }

            for (int i = 0; i < _stepPendingTriggers.Count; i++)
            {
                int pid = _stepPendingTriggers[i];
                PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                if (pw == null) continue;

                // Objective tracking: this primed word is about to explode (live path). 2026-06-09.
                ObjectiveManager.Instance?.NotifyWordExploded(pw.Word, pw.OwnerPlayer, pw.PrimedByEdit);

                if (pw.Word.Length > longestPrimedWord)
                    longestPrimedWord = pw.Word.Length;
                keyWordLen = Mathf.Max(keyWordLen, pw.Word.Length); // primed word counts as a chest "key" too

                // SPECIAL TILE BIRTH: pick the survivor cell for a 5+ letter word.
                Vector2Int specialAt = new Vector2Int(-1, -1);
                int specialKind = SPECIAL_NONE;
                // A word that FIRES an existing special does not also birth one. 2026-10-21 Spencer: a cascade EATER ran
                // through a rocket (fired it) and was also 5 letters, so a NEW rocket was born in the same row and survived
                // the blast — "it fired the rocket but it didnt destroy it". Firing the special IS that word's reward.
                bool firesSpecial = false;
                if (pw.Cells != null)
                    foreach (var pc in pw.Cells)
                        if (InBounds(pc.x, pc.y) && _board[pc.x, pc.y] != null && _board[pc.x, pc.y].SpecialKind != SPECIAL_NONE
                            && !specialSurvivors.Contains(pc)) { firesSpecial = true; break; }
                bool fullLadder = SurvivalManager.FullBoardActive;
                Vector2Int crossAt = new Vector2Int(-1, -1);
                if (fullLadder && pw.Cells != null && pw.Word != null && pw.Word.Length < 6)
                    foreach (var pc in pw.Cells) if (crossUse.TryGetValue(pc, out int n) && n >= 2) { crossAt = pc; break; }
                if (SpecialTilesActive && !firesSpecial && pw.Word != null && pw.Cells != null
                    && ((pw.Word.Length >= 5 && pw.Cells.Count >= 5) || crossAt.x >= 0))
                {
                    bool horizontal = true;
                    for (int c = 1; c < pw.Cells.Count; c++) if (pw.Cells[c].y != pw.Cells[0].y) { horizontal = false; break; }
                    if (fullLadder)
                        specialKind = pw.Word.Length >= 6 ? SPECIAL_DISCO : crossAt.x >= 0 ? SPECIAL_BOMB
                                    : (horizontal ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V);
                    else
                        specialKind = pw.Word.Length >= 7 ? SPECIAL_DISCO : pw.Word.Length == 6 ? SPECIAL_BOMB
                                    : (horizontal ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V);
                    System.Func<Vector2Int, bool> eligible = p => InBounds(p.x, p.y) && _board[p.x, p.y] != null
                        && !_board[p.x, p.y].IsFrozen && _board[p.x, p.y].SpecialKind == SPECIAL_NONE
                        && !_board[p.x, p.y].IsDropTarget && !specialSurvivors.Contains(p);
                    if (specialKind == SPECIAL_BOMB && crossAt.x >= 0 && eligible(crossAt)) specialAt = crossAt;   // bomb sits on the crossing
                    if (specialAt.x < 0 && LastSwapCells != null)
                        foreach (var sc in LastSwapCells)
                            if (specialAt.x < 0 && pw.Cells.Contains(sc) && eligible(sc)) specialAt = sc;
                    if (specialAt.x < 0 && eligible(pw.Cells[pw.Cells.Count / 2])) specialAt = pw.Cells[pw.Cells.Count / 2];
                    if (specialAt.x < 0) foreach (var p in pw.Cells) if (specialAt.x < 0 && eligible(p)) specialAt = p;
                    if (specialAt.x < 0) specialKind = SPECIAL_NONE;
                }

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Vector2Int cell = pw.Cells[c];
                    if (specialSurvivors.Contains(cell)) continue;   // born this step — a crossing word must not destroy it
                    if (InBounds(cell.x, cell.y) && _board[cell.x, cell.y] != null)
                    {
                        if (specialKind != SPECIAL_NONE && cell == specialAt)
                        {
                            _board[cell.x, cell.y].SpecialKind = specialKind;   // keeps its letter, survives, becomes special
                            specialSurvivors.Add(cell);
                            specialsCreated.Add(new KeyValuePair<Vector2Int, int>(cell, specialKind));
                            specialSources[cell] = new List<Vector2Int>(pw.Cells);
                            if (!HandManager.BonusBlastRunning) StageStats.SpecialsMade++;
                            WDLog.Log($"[Special] {SpecialName(specialKind)} born at ({cell.x},{cell.y}) '{_board[cell.x, cell.y].Letter}' from '{pw.Word}'");
                            continue;
                        }
                        if (_board[cell.x, cell.y].SpecialKind != SPECIAL_NONE)
                            specialQueue.Enqueue(new SpecialFire { Cell = cell, Kind = _board[cell.x, cell.y].SpecialKind, Letter = _board[cell.x, cell.y].Letter, Word = pw.Word });
                        // Escort drop-targets (rubber chickens) ONLY leave at the bottom row — never destroyed as
                        // primed-word / chain-detonation collateral. Higher-tier chains detonate more primed words
                        // through here, so this path was blowing up chickens on big explosions. 2026-07-23.
                        if (_board[cell.x, cell.y].IsDropTarget) continue;
                        // ICE: a frozen tile in the detonating word thaws + survives (never destroyed).
                        if (TryThawCell(cell, thawedCells)) continue;

                        // Check for refill tiles before destroying
                        var cellData = _board[cell.x, cell.y];
                        if (cellData.IsSwapRefill) swapRefillCount++;
                        if (cellData.IsEditRefill) editRefillCount++;
                        if (cellData.IsWildRefill) wildRefillCount++;

                        _board[cell.x, cell.y] = null;
                        _bonusCells[cell.x, cell.y] = false; // gold consumed with tile
                        _cyanCells[cell.x, cell.y] = false;  // cyan consumed with tile
                        allExplodedCells.Add(cell);
                    }
                }

                int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                int rawBonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS + heatBonus;

                // Chain-depth scaling: each word in a cluster escalates the chain.
                // Evaluate at depth+1 so the OPENING boom is 2x, not a flat 1x.
                // First detonation = 2x, second = 4x, third = 7x (capped by CHAIN_DEPTH_SCALE_CAP=3).
                // _stepChainDepth is incremented per word at the bottom of this
                // loop — by design, see top-of-method comment.
                float chainMultiplier = TriangularChainMultiplier(_stepChainDepth + 1);
                float goldMultiplier = pw.IsGold ? 2f : 1f;
                int bonus = Mathf.RoundToInt(rawBonus * chainMultiplier * goldMultiplier);

                detonationBonus += bonus;
                totalHeat += heatBonus;

                // Fire per-detonation event with FINAL bonus (post-multiplier) so
                // downstream systems (ChainMeter, etc.) see actual scoring impact
                // instead of raw pw.Score — which undercounts big-cluster detonations
                // by the full triangular + cluster multiplier.
                OnDetonationScored?.Invoke(bonus);

//                 WDLog.Log($"[RulesEngine] DoExplode: '{pw.Word}' (id={pid}) exploded, +{bonus} pts " +
                          // $"(rescore={Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER)} base={BREAKER_BONUS} heat={heatBonus} chain={_stepChainDepth} x{chainMultiplier:F1})");

                StageStats.RecordPopped(pw.Word);   // tuning stats (2026-10-20)
                _primedRegistry.RemovePrimedWord(pid);
                _stepJustPrimed.Remove(pid);

                // Escalate chain depth per cluster word — load-bearing for
                // splash/meltdown/burst gates that expect chain depth to climb
                // across a cluster.
                _stepChainDepth++;
            }

            _stepTotalScore += detonationBonus;

            // SPECIAL TILES FIRE: every special in a popping word (and any special caught in a blast) clears its area.
            if (specialQueue.Count > 0)
            {
                var exploded = new HashSet<Vector2Int>(allExplodedCells);
                int guard = 0;
                while (specialQueue.Count > 0 && guard++ < 64)
                {
                    var f = specialQueue.Dequeue();
                    f.Cleared = new List<Vector2Int>();
                    var area = new List<Vector2Int>();
                    // POWERED = fired by a real word the player made (2026-10-21 Spencer: double-tap paid the same as making a
                    // word "think that making a word should raise the power of it?"). Basic = double-tap / caught in a blast.
                    //   rocket: one line -> powered: row AND column    bomb: 3x3 -> powered: 5x5
                    //   disco:  most common letter on the board -> powered: every letter of the word
                    bool powered = !string.IsNullOrEmpty(f.Word) && f.Word.Length > 1;
                    bool rocketRow = f.Kind == SPECIAL_ROCKET_H || (powered && f.Kind == SPECIAL_ROCKET_V);
                    bool rocketCol = f.Kind == SPECIAL_ROCKET_V || (powered && f.Kind == SPECIAL_ROCKET_H);
                    if (f.Kind == SPECIAL_ROCKET_H || f.Kind == SPECIAL_ROCKET_V)
                    {
                        if (rocketRow) for (int x = 0; x < COLS; x++) area.Add(new Vector2Int(x, f.Cell.y));
                        if (rocketCol) for (int y = 0; y < ROWS; y++) area.Add(new Vector2Int(f.Cell.x, y));
                    }
                    else if (IsBombLike(f.Kind))
                    {
                        int r = powered ? 2 : 1;
                        for (int dx = -r; dx <= r; dx++) for (int dy = -r; dy <= r; dy++) area.Add(new Vector2Int(f.Cell.x + dx, f.Cell.y + dy));
                    }
                    else if (f.Kind == SPECIAL_DISCO)
                    {
                        string set;
                        if (powered) set = f.Word.ToUpperInvariant();
                        else
                        {
                            // Basic disco: the MOST COMMON letter on the board (Royal Match's tapped light ball) — it was
                            // the weakest special when it only took its own letter.
                            var counts = new Dictionary<char, int>();
                            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                            {
                                var d0 = _board[x, y];
                                if (d0 == null || d0.Letter == '\0' || d0.IsStone) continue;
                                char ch = char.ToUpperInvariant(d0.Letter);
                                counts[ch] = (counts.TryGetValue(ch, out int k) ? k : 0) + 1;
                            }
                            char best = char.ToUpperInvariant(f.Letter); int bestN = -1;
                            foreach (var kv in counts) if (kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                            set = best.ToString();
                        }
                        for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                        {
                            var d = _board[x, y];
                            if (d != null && !d.IsStone && set.IndexOf(char.ToUpperInvariant(d.Letter)) >= 0) area.Add(new Vector2Int(x, y));
                        }
                    }
                    // Rockets sweep outward from the special, so order the cells by distance (the FX zips along).
                    area.Sort((a, b) => (Mathf.Abs(a.x - f.Cell.x) + Mathf.Abs(a.y - f.Cell.y)).CompareTo(Mathf.Abs(b.x - f.Cell.x) + Mathf.Abs(b.y - f.Cell.y)));
                    foreach (var p in area)
                    {
                        if (!InBounds(p.x, p.y) || exploded.Contains(p) || specialSurvivors.Contains(p)) continue;
                        var d = _board[p.x, p.y];
                        if (d == null || d.IsDropTarget || (d.IsAnchored && !SurvivalManager.FullBoardActive)) continue;
                        if (d.IsStone && SurvivalManager.FullBoardActive) { CratesBroken++; _stepTotalScore += ICE_BOUNTY; }   // full board: specials smash crates (Royal Match boxes)
                        // A special's blast takes ICE too (2026-10-21 Spencer: a rocket on the ice level left frozen tiles in its
                        // row — "it should destroy the whole row"). Candy Crush / Royal Match specials break blockers in their
                        // path; the "only a word melts ice" rule stays for ordinary splash. Pays the ice bounty.
                        if (d.IsFrozen) { _stepTotalScore += ICE_BOUNTY; d.IsFrozen = false; }   // destroyed, not thawed — no defrost anim on a dying tile
                        if (d.SpecialKind != SPECIAL_NONE) specialQueue.Enqueue(new SpecialFire { Cell = p, Kind = d.SpecialKind, Letter = d.Letter });
                        _board[p.x, p.y] = null;
                        _bonusCells[p.x, p.y] = false;
                        _cyanCells[p.x, p.y]  = false;
                        exploded.Add(p);
                        allExplodedCells.Add(p);
                        f.Cleared.Add(p);
                        _stepTotalScore += SPECIAL_TILE_PAY;
                    }
                    specialsFired.Add(f);
                    if (!HandManager.BonusBlastRunning) StageStats.SpecialsFired++;
                    WDLog.Log($"[Special] {SpecialName(f.Kind)} fired at ({f.Cell.x},{f.Cell.y}) '{f.Letter}'{(powered ? " POWERED by '" + f.Word + "'" : " basic")} — cleared {f.Cleared.Count} tile(s)");
                }
                ValidatePrimedWords();   // armed words that lost letters to a blast are no longer words
            }

            // Phase 9.7: trigger-word tiles clear in Survival AND Level mode.
            // Survival uses this to free more cells per detonation (board-survival mechanic).
            // Level mode uses it so the player's drop-formed trigger word doesn't occupy
            // cells that gravity needs to fill with falling letters forming cascade words.
            // The big-moment row/column splash below stays Survival-only via its own gate.
            if (SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode)
            {
                var alreadyExploded = new HashSet<Vector2Int>(allExplodedCells);
                alreadyExploded.UnionWith(specialSurvivors);   // newborn specials survive every follow-up pass (2026-10-21: GUESS's rocket was wiped by the trigger-word clear)
                if (_stepTriggerWords != null)
                {
                    for (int tw = 0; tw < _stepTriggerWords.Count; tw++)
                    {
                        var trigWord = _stepTriggerWords[tw];
                        if (trigWord.Cells == null) continue;
                        for (int tc = 0; tc < trigWord.Cells.Count; tc++)
                        {
                            var cell = trigWord.Cells[tc];
                            if (alreadyExploded.Contains(cell)) continue;
                            if (InBounds(cell.x, cell.y) && _board[cell.x, cell.y] != null)
                            {
                                // Escort drop-targets (rubber chickens) only ever leave at the bottom row —
                                // they must NEVER be destroyed as trigger-word collateral, or the HeroWord
                                // level becomes uncompletable. The two splash sweeps below already guard this;
                                // this block was missing the guard, so a chicken caught here blew up. 2026-06-23.
                                if (_board[cell.x, cell.y].IsDropTarget) continue;

                                // ICE: a frozen tile in the trigger word thaws + survives (never destroyed).
                                if (TryThawCell(cell, thawedCells)) { alreadyExploded.Add(cell); continue; }

                                _board[cell.x, cell.y] = null;
                                _bonusCells[cell.x, cell.y] = false;
                                _cyanCells[cell.x, cell.y] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                    }

                    // Remove any primed words that used the trigger word cells
                    if (_primedRegistry != null)
                    {
                        for (int tw = 0; tw < _stepTriggerWords.Count; tw++)
                        {
                            var trigWord = _stepTriggerWords[tw];
                            if (trigWord.Cells == null) continue;
                            for (int tc = 0; tc < trigWord.Cells.Count; tc++)
                            {
                                var primedAt = _primedRegistry.GetPrimedWordsContaining(trigWord.Cells[tc]);
                                if (primedAt != null)
                                    for (int p = primedAt.Count - 1; p >= 0; p--)
                                    {
                                        // Objective tracking: trigger-word cells take these primed words with them. 2026-06-09.
                                        ObjectiveManager.Instance?.NotifyWordExploded(primedAt[p].Word, primedAt[p].OwnerPlayer, primedAt[p].PrimedByEdit);
                                        _primedRegistry.RemovePrimedWord(primedAt[p].Id);
                                    }
                            }
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // BIG-MOMENT ROW/COLUMN SPLASH — ties the BigBurstFlash visual to
                // actual gameplay impact. Horizontal detonated word → clear its full
                // row. Vertical detonated word → clear its full column. Splash tiles score at
                // base letter value × gold mult (no chain mult — they're collateral).
                // Any primed word caught in the splash detonates as chain
                // continuation. Gated to match BigBurstFlash: chain depth ≥ 2 OR
                // longest primed word ≥ 6 OR 2+ primed cluster.
                // The chain >= 2 threshold relies on per-cluster-word chain
                // depth inflation in DoExplode — see top-of-method comment.
                // Phase 9.7: big-moment splash stays Survival-only even though the
                // outer trigger-word-clear block now runs in Level mode too.
                bool splashGate = BigMomentSplashEnabled   // 2026-09-10: off for the armed-web game
                    && SurvivalManager.IsSurvivalMode
                    && !_splashFiredThisResolution
                    && ((_stepChainDepth >= 2)
                        || (longestPrimedWord >= 6)
                        || (_stepPendingTriggers != null && _stepPendingTriggers.Count >= 2));

                if (splashGate)
                {
                    _splashFiredThisResolution = true; // ONE splash per drop — prevents feedback loop
                    int splashBaseBonus = 0;
                    var splashPrimedIds = new HashSet<int>();

                    // Splash sources were snapshotted before the main detonation loop
                    // destroyed cells. Each source word's orientation determines its
                    // splash axis.
                    var splashSources = primedSplashSources;
                    // Fallback to trigger words if no primed info (defensive — should
                    // be non-empty since splashGate requires at least one trigger).
                    if (splashSources.Count == 0)
                    {
                        splashSources = new List<List<Vector2Int>>();
                        if (_stepTriggerWords != null)
                        {
                            foreach (var trigWord in _stepTriggerWords)
                                if (trigWord?.Cells != null && trigWord.Cells.Count > 0)
                                    splashSources.Add(trigWord.Cells);
                        }
                    }

                    foreach (var cells in splashSources)
                    {
                        if (cells == null || cells.Count == 0) continue;

                        // Detect orientation — same logic as BigBurstFlash uses so
                        // the splash axis matches the sweep axis exactly.
                        int minCol = int.MaxValue, maxCol = int.MinValue;
                        int minRow = int.MaxValue, maxRow = int.MinValue;
                        foreach (var c in cells)
                        {
                            if (c.x < minCol) minCol = c.x;
                            if (c.x > maxCol) maxCol = c.x;
                            if (c.y < minRow) minRow = c.y;
                            if (c.y > maxRow) maxRow = c.y;
                        }
                        bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;

                        if (vertical)
                        {
                            int col = minCol;
                            for (int row = 0; row < ROWS; row++)
                            {
                                var cell = new Vector2Int(col, row);
                                if (alreadyExploded.Contains(cell)) continue;
                                if (!InBounds(col, row) || _board[col, row] == null) continue;
                                if (_board[col, row].IsDropTarget) continue; // drop-targets only exit at the bottom
                                // ICE (strict): collateral splash never touches a frozen OR already-thawed tile — only a word detonation clears ice.
                                if (IsIceProtected(cell, thawedCells)) continue;
                                if (specialSurvivors.Contains(cell)) continue;   // newborn special survives the splash (2026-10-21)

                                var data = _board[col, row];
                                int letterPts = LetterData.GetPoints(data.Letter);
                                int goldMult  = _bonusCells[col, row] ? 2 : 1;
                                splashBaseBonus += letterPts * goldMult;

                                // Gather any primed words this cell belongs to —
                                // they'll chain-detonate after the splash pass.
                                if (_primedRegistry != null)
                                {
                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var pw in primedHere) splashPrimedIds.Add(pw.Id);
                                }

                                _board[col, row] = null;
                                _bonusCells[col, row] = false;
                                _cyanCells[col, row] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                        else
                        {
                            int row = minRow;
                            for (int col = 0; col < COLS; col++)
                            {
                                var cell = new Vector2Int(col, row);
                                if (alreadyExploded.Contains(cell)) continue;
                                if (!InBounds(col, row) || _board[col, row] == null) continue;
                                if (_board[col, row].IsDropTarget) continue; // drop-targets only exit at the bottom
                                // ICE (strict): collateral splash never touches a frozen OR already-thawed tile — only a word detonation clears ice.
                                if (IsIceProtected(cell, thawedCells)) continue;
                                if (specialSurvivors.Contains(cell)) continue;   // newborn special survives the splash (2026-10-21)

                                var data = _board[col, row];
                                int letterPts = LetterData.GetPoints(data.Letter);
                                int goldMult  = _bonusCells[col, row] ? 2 : 1;
                                splashBaseBonus += letterPts * goldMult;

                                if (_primedRegistry != null)
                                {
                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var pw in primedHere) splashPrimedIds.Add(pw.Id);
                                }

                                _board[col, row] = null;
                                _bonusCells[col, row] = false;
                                _cyanCells[col, row] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                    }

                    // Chain-detonate any primed words the splash hit. Score each
                    // at their stored pw.Score × DETONATION_SCORE_MULTIPLIER + BREAKER_BONUS
                    // (no chain mult — splash is collateral, not a chain tier).
                    int splashPrimedBonus = 0;
                    if (_primedRegistry != null)
                    {
                        foreach (int pid in splashPrimedIds)
                        {
                            var pw = _primedRegistry.GetById(pid);
                            if (pw == null) continue;

                            // Objective tracking: splash swept this primed word into the blast. 2026-06-09.
                            ObjectiveManager.Instance?.NotifyWordExploded(pw.Word, pw.OwnerPlayer, pw.PrimedByEdit);

                            // Sweep remaining cells of this primed word (splash may
                            // have cleared some already).
                            if (pw.Cells != null)
                            {
                                foreach (var cell in pw.Cells)
                                {
                                    if (alreadyExploded.Contains(cell)) continue;
                                    if (!InBounds(cell.x, cell.y) || _board[cell.x, cell.y] == null) continue;
                                    // Escort drop-targets survive splash-detonated primed words too — never collateral. 2026-07-23.
                                    if (_board[cell.x, cell.y].IsDropTarget) { alreadyExploded.Add(cell); continue; }
                                    // ICE: a frozen tile in a splash-detonated primed word thaws + survives.
                                    if (TryThawCell(cell, thawedCells)) { alreadyExploded.Add(cell); continue; }
                                    _board[cell.x, cell.y] = null;
                                    _bonusCells[cell.x, cell.y] = false;
                                    _cyanCells[cell.x, cell.y] = false;
                                    allExplodedCells.Add(cell);
                                    alreadyExploded.Add(cell);
                                }
                            }

                            int pwBonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS;
                            splashPrimedBonus += pwBonus;
                            _primedRegistry.RemovePrimedWord(pid);
                        }
                    }

                    _stepTotalScore += splashBaseBonus + splashPrimedBonus;
//                     WDLog.Log($"[DoExplode] Splash: +{splashBaseBonus} base + {splashPrimedBonus} primed detonation = {splashBaseBonus + splashPrimedBonus}");
                }

                _stepTriggerWords = null;
//                 WDLog.Log($"[RulesEngine] DoExplode: Survival trigger-word clear — total exploded cells: {allExplodedCells.Count}");
            }

            // NOTE: _stepPendingTriggers nulled AFTER splash/row-clear logic below
            // (7-letter row clear at line ~3619 reads it).
            _currentPhase = ResolutionPhase.Exploding;

            // Stone splash: destroy stone tiles adjacent to any exploded cell.
            // Phase 9.9 (Level mode only): chain-propagate so a newly-cleared stone
            // becomes a splash source for the next pass. Enables stone-column
            // cascade catalysts where a single detonation rips through a stone
            // run, opening the column for gravity-fed follow-ups. Survival path
            // stays single-pass — its stones are sparse (max 2 per row) and the
            // existing tuning depends on the one-hop limit.
            int stoneClearCount = 0;
            var stoneCleared = new List<Vector2Int>();
            var checkedStones = new HashSet<Vector2Int>();

            // Seed the first pass from cells already in allExplodedCells
            var currentSources = new List<Vector2Int>(allExplodedCells.Count);
            for (int i = 0; i < allExplodedCells.Count; i++)
                currentSources.Add(allExplodedCells[i]);

            bool stoneChainPropagate = GameManager.IsLevelMode;
            const int STONE_CHAIN_SAFETY_CAP = 50;
            int stoneChainIterations = 0;
            int[] sdx = { 1, -1, 0, 0 };
            int[] sdy = { 0, 0, 1, -1 };

            while (currentSources.Count > 0 && stoneChainIterations < STONE_CHAIN_SAFETY_CAP)
            {
                stoneChainIterations++;
                var nextSources = new List<Vector2Int>();

                for (int ec = 0; ec < currentSources.Count; ec++)
                {
                    var cell = currentSources[ec];
                    for (int d = 0; d < 4; d++)
                    {
                        int sx = cell.x + sdx[d];
                        int sy = cell.y + sdy[d];
                        if (!InBounds(sx, sy)) continue;
                        var stonePos = new Vector2Int(sx, sy);
                        if (checkedStones.Contains(stonePos)) continue;
                        checkedStones.Add(stonePos);
                        var adj = _board[sx, sy];
                        // Drop-targets (scrappy prototype) are stone-like but must
                        // SURVIVE detonations — the only way to move them is gravity.
                        // CHEST TIERS (2026-06-12): a vault only cracks if the longest word that
                        // detonated this resolution meets its RequiredWordLength "key" (regular=0=any,
                        // mid≥4, high≥5). A too-short word leaves the chest intact for a bigger word.
                        if (adj != null && adj.IsStone && !adj.IsDropTarget
                            && keyWordLen >= adj.RequiredWordLength)
                        {
                            _board[sx, sy] = null;
                            stoneCleared.Add(stonePos);
                            stoneClearCount++;
                            if (SurvivalManager.FullBoardActive) { CratesBroken++; _stepTotalScore += ICE_BOUNTY; }
                            nextSources.Add(stonePos);
                        }
                    }
                }

                // Survival: one pass only. Level mode: chain until no new stones clear.
                if (!stoneChainPropagate) break;
                currentSources = nextSources;
            }

            if (stoneChainIterations >= STONE_CHAIN_SAFETY_CAP)
                Debug.LogWarning($"[RulesEngine] Stone chain-clear hit safety cap ({STONE_CHAIN_SAFETY_CAP}) — terminating chain.");

            // Now safe to add stone positions to the exploded list
            allExplodedCells.AddRange(stoneCleared);
            // if (stoneClearCount > 0)
//                 WDLog.Log($"[RulesEngine] DoExplode: cleared {stoneClearCount} stone tile(s) via splash damage");

            // ── Junk splash: scales with PRIMED word length ──
            // 3-letter: 0 splash (tactical glue, not artillery)
            // 4-letter: 1 splash
            // 5-letter: 2 splash
            // 6-letter: 3 splash
            // 7-letter: 4 splash + full row clear
            if (JunkSplashEnabled && SurvivalManager.IsSurvivalMode)
            {
                int maxSplash = longestPrimedWord <= 3 ? 0
                              : longestPrimedWord == 4 ? 1
                              : longestPrimedWord == 5 ? 2
                              : longestPrimedWord == 6 ? 3
                              : 4; // 7+
                int junkCleared = 0;
                var junkCandidates = new List<Vector2Int>();
                var alreadyExplodedSet = new HashSet<Vector2Int>(allExplodedCells);
                alreadyExplodedSet.UnionWith(specialSurvivors);   // newborn specials survive every follow-up pass (2026-10-21: GUESS's rocket was wiped by the trigger-word clear)

                // Collect all junk neighbors of exploded cells
                int snapCount = allExplodedCells.Count;
                for (int ec = 0; ec < snapCount; ec++)
                {
                    var cell = allExplodedCells[ec];
                    int[] jdx = { 1, -1, 0, 0 };
                    int[] jdy = { 0, 0, 1, -1 };
                    for (int d = 0; d < 4; d++)
                    {
                        int jx = cell.x + jdx[d];
                        int jy = cell.y + jdy[d];
                        if (!InBounds(jx, jy)) continue;
                        var jPos = new Vector2Int(jx, jy);
                        if (alreadyExplodedSet.Contains(jPos)) continue;
                        var jCell = _board[jx, jy];
                        if (jCell == null) continue;
                        if (jCell.IsStone) continue; // stones handled separately
                        if (jCell.IsDropTarget) continue; // escort chickens ONLY leave at the bottom row — never junk-splash collateral (2026-07-21: this filter was missing the guard every other clear path has, so a chicken next to an exploded word blew up)
                        if (IsIceProtected(jPos, thawedCells)) continue; // ICE (strict): never destroy a frozen OR already-thawed tile via junk-splash
                        if (jCell.IsSwapRefill || jCell.IsEditRefill || jCell.IsWildRefill) continue; // protect specials
                        if (_bonusCells[jx, jy]) continue; // protect gold

                        // Check if this tile is part of a primed word — protect it
                        var primedAt = _primedRegistry.GetPrimedWordsContaining(jPos);
                        if (primedAt != null && primedAt.Count > 0) continue;

                        if (!junkCandidates.Contains(jPos))
                            junkCandidates.Add(jPos);
                    }
                }

                // Shuffle and pick up to maxSplash
                for (int i = junkCandidates.Count - 1; i > 0; i--)
                {
                    int j = SurvivalRng.Range(0, i + 1);
                    var tmp = junkCandidates[i]; junkCandidates[i] = junkCandidates[j]; junkCandidates[j] = tmp;
                }

                for (int i = 0; i < junkCandidates.Count && junkCleared < maxSplash; i++)
                {
                    var jPos = junkCandidates[i];
                    _board[jPos.x, jPos.y] = null;
                    _bonusCells[jPos.x, jPos.y] = false;
                    _cyanCells[jPos.x, jPos.y] = false;
                    allExplodedCells.Add(jPos);
                    alreadyExplodedSet.Add(jPos);
                    junkCleared++;
                }

                // if (junkCleared > 0)
//                     WDLog.Log($"[RulesEngine] DoExplode: junk splash cleared {junkCleared} tile(s) (wordLen={longestPrimedWord})");
            }

            // ── 7-letter jackpot: clear the entire row of the longest primed word ──
            if (longestPrimedWord >= 7 && SurvivalManager.IsSurvivalMode)
            {
                // Find which row the longest word was on (use first exploded cell from that word)
                int clearRow = -1;
                for (int i = 0; i < _stepPendingTriggers.Count && clearRow < 0; i++)
                {
                    var pw = _primedRegistry.GetById(_stepPendingTriggers[i]);
                    if (pw != null && pw.Word.Length >= 7 && pw.Cells.Count > 0)
                        clearRow = pw.Cells[0].y;
                }
                // Note: primed words already removed from registry above, so GetById may return null
                // Use the allExplodedCells to find the row instead
                if (clearRow < 0 && allExplodedCells.Count > 0)
                    clearRow = allExplodedCells[0].y;

                if (clearRow >= 0)
                {
                    var alreadyExploded = new HashSet<Vector2Int>(allExplodedCells);
                    alreadyExploded.UnionWith(specialSurvivors);   // newborn specials survive every follow-up pass (2026-10-21: GUESS's rocket was wiped by the trigger-word clear)
                    int rowCleared = 0;
                    for (int col = 0; col < COLS; col++)
                    {
                        var pos = new Vector2Int(col, clearRow);
                        if (alreadyExploded.Contains(pos)) continue;
                        // ICE (strict): the jackpot row clear never touches a frozen OR already-thawed tile.
                        if (IsIceProtected(pos, thawedCells)) continue;
                        if (_board[col, clearRow] != null && !_board[col, clearRow].IsDropTarget) // drop-targets survive the jackpot too
                        {
                            _board[col, clearRow] = null;
                            _bonusCells[col, clearRow] = false;
                            _cyanCells[col, clearRow] = false;
                            allExplodedCells.Add(pos);
                            rowCleared++;
                        }
                    }
                    // if (rowCleared > 0)
//                         WDLog.Log($"[RulesEngine] DoExplode: 7-LETTER JACKPOT — cleared row {clearRow} ({rowCleared} extra tiles)");
                }
            }

            _stepPendingTriggers = null; // safe to null now — row-clear logic above is done

            // Purge scored word keys AFTER all splash damage finalized
            PurgeScoredKeysForCells(allExplodedCells);
            PurgeStepScoredKeysForCells(allExplodedCells);
            ExplodedTilesTotal += allExplodedCells.Count;   // running count — bonus coins read the delta (full board refills, so occupancy can't)

            // if (swapRefillCount > 0 || editRefillCount > 0 || wildRefillCount > 0)
//                 WDLog.Log($"[RulesEngine] DoExplode: refills collected — swap={swapRefillCount} edit={editRefillCount} wild={wildRefillCount}");

            return new StepResult
            {
                Phase = ResolutionPhase.Exploding,
                ExplodedCells = allExplodedCells,
                FormedWordCells = formedWordCells.Count > 0 ? new List<Vector2Int>(formedWordCells) : null,
                ThawedCells = thawedCells,
                SpecialsCreated = specialsCreated.Count > 0 ? specialsCreated : null,
                SpecialSources  = specialSources.Count > 0 ? specialSources : null,
                SpecialsFired   = specialsFired.Count > 0 ? specialsFired : null,
                TotalScore = _stepTotalScore,
                DetonationBonus = detonationBonus,
                DetonationHeat = totalHeat,
                ChainDepth = _stepChainDepth,
                ChainTriggeredCount = _stepChainTriggeredCount,
                SwapRefillCount = swapRefillCount,
                EditRefillCount = editRefillCount,
                WildRefillCount = wildRefillCount,
                LongestWordLength = longestPrimedWord,
            };
        }

        private StepResult DoGravity()
        {
            var gravityMoves = ApplyGravityInData();

            if (gravityMoves.Count > 0)
            {
                _primedRegistry.UpdateCellPositions(gravityMoves);

                // Purge scored keys for all cells involved in gravity movement
                // (both old and new positions) so words at new positions can be
                // re-evaluated as fresh words in the chain loop.
                var affectedCells = new List<Vector2Int>(gravityMoves.Count * 2);
                foreach (var kvp in gravityMoves)
                {
                    affectedCells.Add(kvp.Key);   // old position (tile left here)
                    affectedCells.Add(kvp.Value); // new position (tile arrived here)
                }
                PurgeScoredKeysForCells(affectedCells);
                PurgeStepScoredKeysForCells(affectedCells);

                // Seed ALL occupied cells in columns that had gravity movement.
                // Not just the tiles that moved — tiles already below them can
                // now form new words with the landed tiles (e.g. AMP where A and M
                // were already in place but P fell into position).
                var movedCols = new HashSet<int>();
                foreach (var kvp in gravityMoves)
                    movedCols.Add(kvp.Value.x);

                _stepSeedCells = new List<Vector2Int>();
                foreach (int col in movedCols)
                {
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (_board[col, row] != null)
                            _stepSeedCells.Add(new Vector2Int(col, row));
                    }
                }

//                 WDLog.Log($"[RulesEngine] DoGravity: {gravityMoves.Count} tile(s) moved.");
            }

            RemoveInvalidPrimedWords();

            // ── Post-gravity fertility repair — DISABLED ──────────────────────
            // This was mutating board tiles mid-chain without updating visuals,
            // causing phantom "words" that aren't real words on screen.
            // Replaced by PostClearBoost in SurvivalManager which biases future
            // draws instead of mutating existing tiles.
            // if (PostGravityFertilityEnabled && SurvivalManager.IsSurvivalMode && gravityMoves.Count > 0)
            // {
            //     int nearWords = CountNearWords();
            //     if (nearWords < 2)
            //     {
            //         int nudged = NudgeTilesForFertility(2 - nearWords);
            //         if (nudged > 0)
            //             WDLog.Log($"[RulesEngine] PostGravityFertility: nudged {nudged} tile(s) (nearWords was {nearWords})");
            //     }
            // }

            _stepChainDepth++;
            _currentPhase = ResolutionPhase.GravityApplied;

//             WDLog.Log($"[RulesEngine] DoGravity: chainDepth now {_stepChainDepth}, looping back to detect.");

            return new StepResult
            {
                Phase = ResolutionPhase.GravityApplied,
                GravityMoves = gravityMoves,
                TotalScore = _stepTotalScore,
                ChainContinues = true,
            };
        }
    }

    // ── Supporting types ──────────────────────────────────────────────────────────

    public class RulesCellData
    {
        public char Letter        { get; set; }
        public int  Col           { get; set; }
        public int  Row           { get; set; }
        public int  PlayerIndex   { get; set; }
        public bool IsWild        { get; set; }
        public bool IsCyan        { get; set; }
        public bool IsSwapRefill  { get; set; }
        public bool IsEditRefill  { get; set; }
        public bool IsWildRefill  { get; set; }
        public bool IsStone       { get; set; } // grey junk tile — can't be used in words, cleared by adjacent detonation
        // SCRAPPY PROTOTYPE (2026-06-01): drop-to-bottom feel test. A drop-target is
        // an inert stone-like tile that FALLS with gravity but SURVIVES detonations,
        // so the only way to move it down is to clear cells beneath it. Reaching
        // row 0 (bottom) = "collected". Throwaway — gated behind LevelDebugMenu.
        public bool IsDropTarget  { get; set; }
        // ANCHORED rock (2026-06-09): a stone that does NOT fall with gravity — it stays fixed at
        // its cell so it can be placed anywhere (e.g. near the top) as a "break the box" obstacle.
        // Breaks like any stone (adjacent detonation). Other tiles fall around it.
        public bool IsAnchored    { get; set; }
        // FROZEN / ICE (2026-06-12): a NORMAL, MATCHABLE letter tile covered in ice. It participates
        // in word detection exactly like any other letter (word detection only walls off IsStone cells).
        // To clear the ice it must be included in a word that DETONATES — the tile then THAWS (IsFrozen
        // cleared) and SURVIVES in place while the rest of the word explodes. A frozen tile is NEVER
        // destroyed (mirrors the drop-target survive-explosion precedent). v1 = single layer. A future
        // multi-layer is an easy add: replace this bool with an IceLayers int and decrement per detonation.
        public bool IsFrozen      { get; set; }
        /// <summary>Special tile (2026-10-21): 0 none, see RulesEngine.SPECIAL_* constants. Lives on the cell DATA
        /// object, so it travels with rises and gravity (which move the object); swaps move it explicitly.</summary>
        public int  SpecialKind   { get; set; }
        /// <summary>RAIN GOLD (2026-10-21): a shower's smart letter, made gold as the visible reward. Reads + pays like a
        /// K/Q gold letter (+1 move when it pops) but is NOT locked. Travels like SpecialKind: with the object on
        /// rises/gravity, explicitly on swaps.</summary>
        public bool IsRainGold    { get; set; }
        // CHEST TIER (2026-06-12): the word-LENGTH "key" required to crack this vault via the
        // adjacent-detonation stone-splash. 0 = regular (any word), 4 = mid, 5 = high (the
        // telegraphed jackpot). Gated in DoExplode; telegraphed on the tile (bronze/silver/gold).
        // Replaces the old random IsSpecialVault.
        public int RequiredWordLength { get; set; }
    }

    public enum WordDirection
    {
        Horizontal = 0,
        Vertical   = 1,
        Diagonal   = 2,   // swipe-only; the auto scanner never produces this
    }

    public class RulesWordMatch
    {
        public string           Word      { get; set; }
        public List<Vector2Int> Cells     { get; set; }
        public WordDirection    Direction { get; set; }
        public int              Score     { get; set; }

        // Positions within Word (0..Word.Length-1) that were resolved from a wild
        // tile during scanning. These positions score 0. Null/empty when no wilds.
        public List<int>        WildLetterIndices { get; set; }

        public string CellKey
        {
            get
            {
                if (Cells == null || Cells.Count == 0) return "";
                var sorted = new List<Vector2Int>(Cells);
                sorted.Sort((a, b) =>
                {
                    int cmp = a.x.CompareTo(b.x);
                    return cmp != 0 ? cmp : a.y.CompareTo(b.y);
                });
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(sorted[i].x).Append(',').Append(sorted[i].y);
                }
                return sb.ToString();
            }
        }
    }
}
