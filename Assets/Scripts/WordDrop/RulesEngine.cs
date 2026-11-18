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
        // BOARD SIZE SWITCH (2026-10-23 Spencer: "do you think the screen would be better and more readable if we make the tiles a
        // bit bigger and cut down on so many words and columns?" -> "yes please"). Half his swaps made no word although 14-28
        // common-word swaps existed every turn: a FINDABILITY problem on 99 letters. SMALL_BOARD = 7x9 (63 tiles, ~29% bigger);
        // false = the 9x11 board. Read once at startup (every board array is sized from these) — flip it OUT of Play mode.
        // 2026-10-26 Spencer: back to the bigger 9x11 board. 2026-11-02 (Spencer, after the 8x10 full-screen mock: "i think we should do
        // the smaller rows as long as we can make it difficult enough to be a puzzle challenge"): BOARD_PRESET 0 = 9x11, 1 = 8x10,
        // 2 = 7x9. Tiles grow 9/8 at 8x10 (~44 pt on a 375 pt phone - Apple's minimum touch target; 9x11 was ~39).
        // SMALL_BOARD = anything below 9x11: smaller blast areas + blockers scaled by cell count (LevelRules.ScaleForBoard).
        public static int  BOARD_PRESET = 2;   // 11-18: 7x9 is the working board. Spencer on the PHONE (11-17): 8x10 read as 'letter soup', 'the smaller board reads better'. Levels are tuned for 7x9 (bot 11-18 x100); the 8x10 game + its numbers = tag checkpoint-2026-11-17-8x10-phone-pass.
        public static bool SMALL_BOARD => BOARD_PRESET != 0;
        public static int COLS       => BOARD_PRESET == 2 ? 7 : BOARD_PRESET == 1 ? 8 : 9;
        public static int MAX_ROWS   => BOARD_PRESET == 2 ? 9 : BOARD_PRESET == 1 ? 10 : 11;
        public static int LEVEL_ROWS => MAX_ROWS;
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
                    if (data == null || (char.ToUpperInvariant(data.Letter) != char.ToUpperInvariant(pw.Word[c]) && ScanChar(data) != WILD_CHAR))   // 11-03: a wild power-up matches any letter
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
                    if (deadCells != null && SimGrid != null)
                    {
                        for (int d = 0; d < deadCells.Count; d++)
                        {
                            var dc = deadCells[d];
                            var still = _primedRegistry.GetPrimedWordsContaining(dc);
                            if (still != null && still.Count > 0) continue;
                            var t = SimGrid.GetTile(dc.x, dc.y);
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
        /// <summary>The bot's hidden engine (set when it is created).</summary>
        public static RulesEngine SimEngine { get; private set; }
        /// <summary>The engine whose board is being PLAYED right now: the bot's while Simulating, else the live one. Anything that
        /// counts board pieces for a goal must use this (2026-10-23: goal progress read the LIVE board during bot runs — the live
        /// board on the home screen has no crates, so every crate goal read "done" and the bot stopped aiming for crates).</summary>
        public static RulesEngine Current => Simulating && SimEngine != null ? SimEngine : Instance;

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
            var t = SimGrid != null ? SimGrid.GetTile(cell.x, cell.y) : null;
            if (t != null && !t.IsWild && (t.IsGoldLetter || t.IsGem))
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
                if (pts >= Tile.HIGH_VALUE_POINTS || cell.IsRainGold || cell.HasGem) hit = true;   // shower gold + GEMS arm the whole word gold/gem
            }
            return hit;
        }

        public bool HasGoldTile(RulesWordMatch match)
        {
            if (match.Cells == null || SimGrid == null) return false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = SimGrid.GetTile(match.Cells[i].x, match.Cells[i].y);
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
            if (match.Cells == null || SimGrid == null) return 1;
            bool hitGold = false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = SimGrid.GetTile(match.Cells[i].x, match.Cells[i].y);
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

        // ── BOT SIMULATION (2026-10-23, BotPlaytester) ─────────────────────────────────────────────────────────────────────
        // The bot plays thousands of games on a SECOND, hidden engine. While Simulating, every reach from the engine into the
        // live game — board tiles, audio, haptics, FX, objectives — resolves to null/no-op, so the visible game is untouched.
        // CreatingSimulation lets that second engine skip the singleton logic below (which would otherwise Destroy it).
        public static bool Simulating;
        public static bool CreatingSimulation;
        private static GridManager      SimGrid       => Simulating ? null : GridManager.Instance;
        private static ObjectiveManager SimObjectives => Simulating ? null : ObjectiveManager.Instance;
        private static GameAudio        SimAudio      => Simulating ? null : GameAudio.Instance;
        private static WordDropFX       SimFX         => Simulating ? null : WordDropFX.Instance;

        private void Awake()
        {
            if (CreatingSimulation) { ClearBoard(); SimEngine = this; return; }   // bot engine: not the singleton, never destroyed
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

        /// <summary>BOOSTER CLEAR (2026-10-29 Spencer: "says i have one gem left but its not on the board" - a Comet/Mallet cleared a gem
        /// without counting it; only DoExplode counted gems / collect letters). Count the cell's goal pieces like DoExplode does,
        /// then clear it. Boosters call this instead of ClearCell.</summary>
        public void ClearCellCountingGoals(int col, int row)
        {
            if (!InBounds(col, row)) return;
            var d = _board[col, row];
            if (d != null && !(SurvivalManager.AwaitingNextLevel && !Simulating))
            {
                char cl = LevelRules.ActiveCollectLetter;
                int got = 0;
                if (d.HasGem && LevelRules.HasGemGoal) got++;
                if (cl != '\0' && !d.IsStone && char.ToUpperInvariant(d.Letter) == cl) got++;
                if (got > 0) { LevelRules.Collected += got; LevelRules.CollectAwaitingFly += got; LevelRules.CollectAwaitingSince = Time.unscaledTime;
                               if (!Simulating) WDLog.Log($"[Goal] booster collected {got} at ({col},{row}) -> {LevelRules.Collected}"); }
                if (LevelRules.HasGrassGoal) MowGrass(new List<Vector2Int> { new Vector2Int(col, row) });
            }
            ClearCell(col, row);
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
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) if (_board[c, r] != null && _board[c, r].IsStone && !_board[c, r].IsDropTarget && _board[c, r].TreasureHits == 0 && _board[c, r].ShelfPart == 0) n++;
            return n;
        }
        /// <summary>BOOKSHELF: books still on the board's shelves (the Books goal reads this).</summary>
        public int CountShelfBooks()
        {
            int n = 0;
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) if (_board[c, r] != null && _board[c, r].ShelfPart == 1 && _board[c, r].ShelfKind == 0) n += _board[c, r].ShelfBooks;
            return n;
        }
        /// <summary>SAFE (11-03): safes still closed on the board (the Safes goal reads this).</summary>
        public int CountSafes()
        {
            int n = 0;
            for (int c = 0; c < COLS; c++) for (int r = 0; r < ROWS; r++) if (_board[c, r] != null && _board[c, r].ShelfPart == 1 && _board[c, r].ShelfKind == 1 && _board[c, r].ShelfBooks > 0) n++;   // opened safes (SAFE_STAYS_OPEN) don't count
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
        public static bool ICE_AVOID_EDGES = true;
        // 11-04 GOALS CENTRAL (RM structure study + bot 08:03: END-STALL 6-7 turns on L7/8/10/15/19 - the last goal pieces sat on
        // edges / corners / bottom rows, where a word has 1-2 directions to reach them; RM's stragglers were the same spots). Letter
        // goals (gems, ice) start in the open middle: columns 1..COLS-2, rows 2..ROWS-3. false = the old edge-only rule.
        public static bool GOALS_CENTRAL = true;
        public static bool InGoalCentre(int x, int y) => x >= 1 && x <= COLS - 2 && y >= 2 && y <= ROWS - 3;
        public static int ICE_SPREAD_MIN = 2, ICE_PER_LINE = 2;   // spread ice: min Chebyshev gap, max per row/column
        /// <summary>11-02 (Spencer: "anything else i can do to make the board more interesting?" - picked the designed-layout pass):
        /// level-start goal pieces are placed LEFT/RIGHT SYMMETRIC - spread ice in mirror pairs (same gap / per-line rules), gems in
        /// mirror pairs, crate patterns never cut off on one side. Pieces still fall and shift in play; this is the opening picture.
        /// false = the old placement exactly.</summary>
        public static bool SYMMETRIC_LAYOUTS = true;
        private void SpreadMirrored(List<Vector2Int> eligible, List<Vector2Int> picked, int[] rowN, int[] colN, int count)
        {
            int mid = COLS / 2;
            while (picked.Count < count)
            {
                int left = count - picked.Count;
                Vector2Int best = new Vector2Int(-1, -1); int bestD = -1;
                foreach (var p in eligible)
                {
                    if (p.x > mid || picked.Contains(p)) continue;
                    var m = new Vector2Int(COLS - 1 - p.x, p.y); bool centre = m == p;
                    if (centre ? left % 2 == 0 : left < 2) continue;
                    if (!centre && (!eligible.Contains(m) || picked.Contains(m) || m.x - p.x < ICE_SPREAD_MIN)) continue;
                    if (rowN[p.y] + (centre ? 1 : 2) > ICE_PER_LINE || colN[p.x] >= ICE_PER_LINE || colN[m.x] >= ICE_PER_LINE) continue;
                    int d = 999;
                    foreach (var q in picked) d = Mathf.Min(d, Mathf.Min(Mathf.Max(Mathf.Abs(p.x - q.x), Mathf.Abs(p.y - q.y)), Mathf.Max(Mathf.Abs(m.x - q.x), Mathf.Abs(m.y - q.y))));
                    if (picked.Count > 0 && d < ICE_SPREAD_MIN) continue;
                    if (d > bestD) { bestD = d; best = p; }
                }
                if (best.x < 0) break;   // no legal pair left - the caller's single-pick loop finishes the count
                var mb = new Vector2Int(COLS - 1 - best.x, best.y);
                picked.Add(best); rowN[best.y]++; colN[best.x]++;
                if (mb != best) { picked.Add(mb); rowN[mb.y]++; colN[mb.x]++; }
            }
        }
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
                    // NOT ON THE EDGES on the full board (2026-10-23 bot, 7x9: ice levels had the worst tails — L9 p50 23 / p90 54).
                    // Ice can't move, so a corner / edge / bottom-row ice has 1-2 word directions instead of 4.
                    if (SurvivalManager.FullBoardActive && ICE_AVOID_EDGES && (c == 0 || c == COLS - 1 || r == 0)) continue;
                    if (SurvivalManager.FullBoardActive && GOALS_CENTRAL && !InGoalCentre(c, r)) continue;   // 11-04 open middle only
                    eligible.Add(new Vector2Int(c, r));
                }
            }

            ShuffleCells(eligible); // SurvivalRng-backed (daily-seed deterministic)
            if (IceLayout == "spread" && SurvivalManager.FullBoardActive)
            {   // SPREAD (2026-10-29 Spencer: "very easy to pass an ice level when all the ice bricks are laid out so closely together
                // ... one explosion usually completes the level"). Farthest-point picks from the shuffled eligible cells: each new ice
                // is as far as possible from the ones placed, never within ICE_SPREAD_MIN (Chebyshev) of another, and at most
                // ICE_PER_LINE in any row or column - so no single bomb (5x5) or rocket line can take most of them.
                var picked = new System.Collections.Generic.List<Vector2Int>();
                var rowN = new int[ROWS]; var colN = new int[COLS];
                if (SYMMETRIC_LAYOUTS) SpreadMirrored(eligible, picked, rowN, colN, count);
                while (picked.Count < count)
                {
                    Vector2Int best = new Vector2Int(-1, -1); int bestD = -1;
                    foreach (var p in eligible)
                    {
                        if (picked.Contains(p) || rowN[p.y] >= ICE_PER_LINE || colN[p.x] >= ICE_PER_LINE) continue;
                        int d = 999; foreach (var q in picked) d = Mathf.Min(d, Mathf.Max(Mathf.Abs(p.x - q.x), Mathf.Abs(p.y - q.y)));
                        if (picked.Count > 0 && d < ICE_SPREAD_MIN) continue;
                        if (d > bestD) { bestD = d; best = p; }
                    }
                    if (best.x < 0) break;
                    picked.Add(best); rowN[best.y]++; colN[best.x]++;
                }
                foreach (var p in eligible) if (!picked.Contains(p)) picked.Add(p);   // fallback order if the rules ran out of room
                eligible = picked;
                if (!Simulating) WDLog.Log("[Layout] ice 'spread'");
            }
            else if (!string.IsNullOrEmpty(IceLayout) && SurvivalManager.FullBoardActive)
            {   // DESIGNED LAYOUT first (pattern order), the shuffled rest after — see LayoutCells
                var pat = LayoutCells(IceLayout, true); var ordered = new System.Collections.Generic.List<Vector2Int>();
                foreach (var p in pat) if (eligible.Contains(p)) ordered.Add(p);
                foreach (var p in eligible) if (!ordered.Contains(p)) ordered.Add(p);
                eligible = ordered;
                if (!Simulating) WDLog.Log($"[Layout] ice '{IceLayout}'");
            }

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
        // AIRPLANE (2026-10-23, Spencer OK'd option B): earned by ONE swap that makes two words that don't cross (crossing = bomb).
        // Fires a small plus at home, then flies to a REMAINING goal piece and smashes it — Royal Match's propeller. Also the
        // fix for the bot's "last ice / last gem" tail: the final piece in a corner no word reaches.
        public const int SPECIAL_AIRPLANE = 11;
        /// <summary>Bot playtester: specials fired in simulation, by kind (2026-10-23 — how often does the airplane appear?).</summary>
        public static readonly int[] SimFiredByKind = new int[16];
        public static bool AIRPLANE_ON = true;
        public static bool TOME_NO_SELF_REFILL = true;
        public static bool AirplanePending;   // set by NoteSwapWords, spent by the first eligible swap word's birth
        /// <summary>The swap just armed these words: two or more that don't share a cell earn an airplane.</summary>
        /// <summary>Words the last player swap armed (10-31: a "double word" = 2+, for the big-moment boom; spent by the first blast).</summary>
        public static int PendingSwapWordCount;
        public static void NoteSwapWords(System.Collections.Generic.IList<RulesWordMatch> words)
        {
            PendingSwapWordCount = words != null ? words.Count : 0;
            if (!AIRPLANE_ON || !SurvivalManager.FullBoardActive || words == null || words.Count < 2) return;
            for (int i = 0; i < words.Count; i++)
                for (int j = i + 1; j < words.Count; j++)
                {
                    if (words[i]?.Cells == null || words[j]?.Cells == null) continue;
                    bool share = false;
                    foreach (var c in words[i].Cells) if (words[j].Cells.Contains(c)) { share = true; break; }
                    if (!share) { AirplanePending = true; WDLog.Log($"[Airplane] earned: '{words[i].Word}' + '{words[j].Word}' in one swap"); return; }
                }
        }
        public static bool IsBombLike(int k) => k == SPECIAL_BOMB || k == SPECIAL_TNT;
        // SWAP COMBOS (2026-10-22 Spencer, from the Royal Match recording @1:21 — TNT swapped into a rocket became a 3x3 of
        // rockets firing 3 rows + 3 columns). Swapping two specials together fires one of these at the swap cell:
        public const int SPECIAL_COMBO_CROSS = 6, SPECIAL_COMBO_BIGCROSS = 7, SPECIAL_COMBO_MEGA = 8, SPECIAL_COMBO_DISCO = 9, SPECIAL_COMBO_DISCO_DISCO = 10;
        /// <summary>AIRPLANE + AIRPLANE (2026-10-28 Spencer: "two airplanes together needs a combo like when the reference game matches
        /// two propellers"). RK/RM propeller+propeller: a burst at home, then several propellers split off and each smashes a
        /// different target. Ours: 3x3 at home, then COMBO_PLANES_COUNT airplanes, each to its own target (PickAirplaneTarget skips
        /// cells already cleared, so they spread), leaving AIRPLANE_COMBO_STAGGER s apart.</summary>
        public const int SPECIAL_COMBO_PLANES = 12;
        /// <summary>AIRPLANE + BOMB/TNT (2026-11-01, Spencer picked "B"; it fell through to BIGCROSS and wound up flashing a ROCKET).
        /// Genre standard (RM propeller+TNT, Playrix plane+bomb): the bomb swells + flashes, the airplane carries it to a goal piece
        /// (PickAirplaneTarget) and it blows a powered bomb square there. Home cell takes the airplane's plus.</summary>
        public const int SPECIAL_COMBO_PLANE_BOMB = 13;
        public static int   COMBO_PLANES_COUNT = 3;
        public static float AIRPLANE_COMBO_STAGGER = 0.12f;
        public static bool IsRocket(int k) => k == SPECIAL_ROCKET_H || k == SPECIAL_ROCKET_V;
        /// <summary>rocket+rocket = cross · bomb/TNT+rocket = 3 rows+3 cols · bomb+bomb = 7x7 · disco+X = every tile of the
        /// commonest letter becomes X and fires · disco+disco = whole board.</summary>
        public static int ComboKindFor(int a, int b)
        {
            bool da = a == SPECIAL_DISCO, db = b == SPECIAL_DISCO;
            if (da && db) return SPECIAL_COMBO_DISCO_DISCO;
            if (da || db) return SPECIAL_COMBO_DISCO;
            if (a == SPECIAL_AIRPLANE && b == SPECIAL_AIRPLANE) return SPECIAL_COMBO_PLANES;
            if ((a == SPECIAL_AIRPLANE && IsBombLike(b)) || (b == SPECIAL_AIRPLANE && IsBombLike(a))) return SPECIAL_COMBO_PLANE_BOMB;
            if (IsRocket(a) && IsRocket(b)) return SPECIAL_COMBO_CROSS;
            if (IsBombLike(a) && IsBombLike(b)) return SPECIAL_COMBO_MEGA;
            return SPECIAL_COMBO_BIGCROSS;
        }
        /// <summary>For SPECIAL_COMBO_DISCO: the special the disco multiplies (set by the swap just before it fires).</summary>
        public static int PendingComboPartner = SPECIAL_ROCKET_H;
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
            if (word != null && len >= MIN_WORD_LENGTH && LevelRules.ThemeShortActive && LevelRules.IsThemeWord(word)) return false;   // theme level: PIG counts
            bool bySwap = false;
            if (ShortWordCells != null) foreach (var sc in ShortWordCells) if (cells.Contains(sc)) { bySwap = true; break; }
            // GOLD RUSH (2026-10-30): the bonus level lets a SWAP make 3-letter words (falls still need 4) - fast, easy, rewarding
            if (len < FULL_MIN_LEN && !((FULL_SHORT_BY_SWAP || LevelRules.BonusLevelActive) && bySwap)) return true;
            if (FULL_SHORT_BY_SWAP && len < FULL_CASCADE_MIN_LEN && !bySwap) return true;
            if (FULL_FALL_COMMON_ONLY && !bySwap && word != null && !IsCommonWord(word)) return true;   // fall-made: common words only
            return false;
        }
        /// <summary>Shortest word that counts right now (swipes use this too).</summary>
        public static int EffectiveMinWordLength => SurvivalManager.FullBoardActive ? Mathf.Max(MIN_WORD_LENGTH, FULL_MIN_LEN) : MIN_WORD_LENGTH;
        /// <summary>One-shot: the next tap detonation seeds EVERY armed word (the bonus blast fires all its specials in one
        /// detonation, no gaps). Consumed by BeginTapDetonation.</summary>
        public static bool SeedAllOnNextTap;
        public struct SpecialFire { public Vector2Int Cell; public int Kind; public char Letter; public string Word; public List<Vector2Int> Cleared; public Vector2Int Target; public float Delay; public List<Vector2Int> Gold; }   // Gold: gold coin tiles this blast took (Gold Rush, 10-30)   // Target: airplane only; Delay: extra launch delay (airplane combo, 2026-10-28)
        // ── POWER-UPS ARE WILD (2026-11-03, Spencer: drop the letter badge so power-ups read cleanly, "these letters count as wilds if
        // they make words"; Claude's limit, Spencer "ok lets try it": ONE wild per word so word-spotting stays the skill).
        // A power-up cell reads as '?' in every word scan (auto scanners + swipes); a word with exactly one '?' is valid if any letter
        // A-Z makes it a word (first hit, alphabetical); two or more power-ups in one word = not a word. false = the old rule (the
        // power-up's own letter, shown on its badge).
        public static bool SPECIALS_ARE_WILD = true;
        public const char WILD_CHAR = '?';
        public static char ScanChar(RulesCellData c) =>
            c == null ? '\0' : (SPECIALS_ARE_WILD && c.SpecialKind != SPECIAL_NONE) ? WILD_CHAR : char.ToUpper(c.Letter);
        /// <summary>The real word a pattern with one WILD_CHAR stands for, or null. No wild = the pattern itself if valid.
        /// intentional = also accept the wider intentional list (swaps / swipes).</summary>
        public static string ResolveWildPattern(string pattern, bool intentional)
        {
            int i = pattern.IndexOf(WILD_CHAR);
            if (i < 0) return (WordDictionary.IsValidWord(pattern) || (intentional && WordDictionary.IsIntentionalWord(pattern))) ? pattern : null;
            if (pattern.IndexOf(WILD_CHAR, i + 1) >= 0) return null;   // one wild per word
            var buf = pattern.ToCharArray();
            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                buf[i] = ch; string w = new string(buf);
                if (WordDictionary.IsValidWord(w) || (intentional && WordDictionary.IsIntentionalWord(w))) return w;
            }
            return null;
        }

        public static string SpecialName(int k) => k == SPECIAL_ROCKET_H ? "ROCKET-H" : k == SPECIAL_ROCKET_V ? "ROCKET-V"
                                                 : k == SPECIAL_BOMB ? "BOMB" : k == SPECIAL_TNT ? "TNT" : k == SPECIAL_DISCO ? "DISCO"
                                                 : k == SPECIAL_COMBO_CROSS ? "COMBO-CROSS" : k == SPECIAL_COMBO_BIGCROSS ? "COMBO-BIGCROSS" : k == SPECIAL_COMBO_MEGA ? "COMBO-MEGA"
                                                 : k == SPECIAL_AIRPLANE ? "AIRPLANE" : k == SPECIAL_COMBO_PLANES ? "COMBO-PLANES" : k == SPECIAL_COMBO_PLANE_BOMB ? "COMBO-PLANE-BOMB" : k == SPECIAL_COMBO_DISCO ? "COMBO-DISCO" : k == SPECIAL_COMBO_DISCO_DISCO ? "COMBO-DISCO-DISCO" : "none";

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
                    movable[x, y] = !d.IsFrozen && !d.IsStone && !d.IsWild && !d.IsDropTarget && !d.IsAnchored
                                    && !(HandManager.GEMS_LOCKED && d.HasGem);   // smart refills must not plan a swap through a locked gem
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
                    if (s_wordSetForSmart.Contains(w) && WordDictionary.IsValidWord(w)) { found = w; return true; }
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
                    if (s_wordSetForSmart.Contains(w) && WordDictionary.IsValidWord(w)) { found = w; return true; }
                }
            return false;
        }

        /// <summary>Find a letter to land at (col,row) so that ONE legal swap of two touching tiles near it spells a common
        /// 4-5 letter word — and it is NOT already a word before the swap (that would be free, not a find).</summary>
        public bool FindSmartRainLetter(char[,] g, bool[,] movable, int col, int row, out char letter, out string word)
        {
            EnsurePlantLists();
            // THEME LEVEL: try the theme words first (THEME_PLANT_SHARE of the time), so refills keep making new
            // "one swap from an animal" chances after the starting plants are used up.
            if (ThemePlantPool != null && ThemePlantPool.Count > 0 && SurvivalRng.Value < THEME_PLANT_SHARE)
            {
                if (s_themeSetSource != ThemePlantPool) { s_themeSet = new HashSet<string>(ThemePlantPool); s_themeSetSource = ThemePlantPool; }
                s_wordSetForSmart = s_themeSet;
                bool ok = FindSmartRainLetterIn(g, movable, col, row, out letter, out word);
                s_wordSetForSmart = s_plantSet;
                if (ok) { WDLog.Log($"[Theme] refill letter '{letter}' at ({col},{row}) sets up '{word}'"); return true; }
            }
            s_wordSetForSmart = s_plantSet;
            return FindSmartRainLetterIn(g, movable, col, row, out letter, out word);
        }
        private static HashSet<string> s_themeSet, s_wordSetForSmart = new HashSet<string>();
        private static List<string> s_themeSetSource;
        private bool FindSmartRainLetterIn(char[,] g, bool[,] movable, int col, int row, out char letter, out string word)
        {
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
        public static int BOMB_RADIUS_BASIC = 2, BOMB_RADIUS_POWERED = 2;
        public static int DISCO_MIN_TILES = 8;   // basic disco: own letter; under this many, + the most common other letter (11-01)   // 2 = 5x5 (Royal Match TNT); 3 = 7x7
        public static int FULL_START_TNT  = 1;   // dynamite letters on every full-board level (2 -> 1, 2026-10-21: levels fell in 1-3 moves)
        public static int FULL_MAX_HIGH   = 2;   // gold K/Q/J/X/Z/V (locked) on the board at once — 8 in one screenshot clogged it
        public static int FULL_MAX_RAIN_GOLD = 2;

        /// <summary>Crates in a mirrored, designed-looking pattern (Royal Match boards are symmetric). Rows 1..ROWS-3.</summary>
        // ── DESIGNED LAYOUTS (2026-10-23 Spencer: "feels a bit bland and repetitive in a way that royal match isnt" -> "sure"). Every
        // RM level LOOKS different on load — L6 a wall of boxes, L7 side columns, L8 a pyramid, L10 gaps — while every one of ours
        // was the same rectangle with blockers sprinkled at random. A level can name a pattern; its cells are filled in this order
        // (symmetric, readable shapes), and anything the pattern can't hold falls back to the old random placement. Crates aren't
        // anchored, so a pattern is the STARTING picture — it loosens as the level is played. null = random, as before. ──
        public static string CrateLayout, IceLayout;

        // ── SHAPED BOARDS / VOID CELLS (2026-10-28 Spencer: "plan how we are going to use them and implement them" - Royal Match
        // L1-20 board shapes, catalogued in WordDrop_icon_gen/board_frame/rm_board_shapes.md). A VOID cell does not exist on this
        // level's board: it never holds a tile, gravity drops tiles straight THROUGH it (a split board's top still feeds the
        // bottom), refills enter at each column's highest real cell, and it breaks words like an edge. Static (not per engine)
        // so the visuals and the bot's sim engine read the same mask. Masks are written TOP row first ('#' cell, '.' void) and
        // sized for the full board (COLS x LEVEL_ROWS); a mask that doesn't fit is ignored (plain rectangle). ──
        public static string BoardShape;             // name in SHAPES, or null = rectangle
        private static bool[,] s_void;
        // Only on the full-board level game and only while the mask matches the board size: a stale mask from the last level
        // can never punch holes in the menu board / other modes.
        private static bool VoidActive => s_void != null && SurvivalManager.FullBoardActive && s_void.GetLength(0) == COLS && s_void.GetLength(1) == ROWS;
        public static bool HasVoids => VoidActive;
        public static bool IsVoid(int x, int y) => VoidActive && x >= 0 && y >= 0 && x < s_void.GetLength(0) && y < s_void.GetLength(1) && s_void[x, y];
        /// <summary>Highest row in column x that is a real cell (ROWS-1 on a rectangle; -1 if the whole column is void).</summary>
        public static int TopRealRow(int x) { for (int y = ROWS - 1; y >= 0; y--) if (!IsVoid(x, y)) return y; return -1; }
        /// <summary>Lowest real row in column x (0 on a rectangle; -1 if the whole column is void).</summary>
        public static int BottomRealRow(int x) { for (int y = 0; y < ROWS; y++) if (!IsVoid(x, y)) return y; return -1; }
        public static int RealCellCount() { int n = 0; for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (!IsVoid(x, y)) n++; return n; }
        public static readonly Dictionary<string, string> SHAPES = new Dictionary<string, string>
        {   // 9 wide x 11 tall, top row first
            ["octagon"]   = "..#####../.#######./#########/#########/#########/#########/#########/#########/#########/.#######./..#####..",
            ["funnel"]    = "#########/#########/#########/#########/#########/#########/#########/.#######./.#######./..#####../..#####..",   // 10-30: bottom 3 wide -> 5 wide (Spencer's L8 lull: 4-letter words couldn't cross the pocket)
            ["notches"]   = "####.####/####.####/#########/#########/#########/..#####../#########/#########/#########/####.####/####.####",
            ["split"]     = "####.####/####.####/####.####/####.####/####.####/####.####/####.####/####.####/####.####/####.####/####.####",
            ["sidenotch"] = "#########/#########/#########/#########/#########/#######../#########/#########/#########/#########/#########",
            ["hole"]      = "#########/#########/#########/#########/#########/####.####/####.####/#########/#########/#########/#########",
            ["legs"]      = "#########/#########/#########/#########/#########/####.####/####.####/####.####/####.####/####.####/####.####",
            // 10-30 REACH (bot: L11 'split' goals dead 52% of turns, L19 'legs' 45%): a 4-wide lane takes ONLY exact 4-letter words
            // across it, and crates FALL into the bottom lanes. Split only in the middle so the top and bottom stay full width.
            ["lanes"]     = "#########/#########/#########/#########/####.####/####.####/####.####/#########/#########/#########/#########",
            ["arch"]      = "#########/#########/#########/#########/#########/####.####/####.####/#########/#########/#########/#########",
        };
        /// <summary>8x10 versions (11-02, BOARD_PRESET 1). Same idea per shape; an even width can't split on one centre column, so
        /// centre splits / holes are 2 wide (the tiles are 9/8 bigger, so the gaps read the same).</summary>
        public static readonly Dictionary<string, string> SHAPES_8x10 = new Dictionary<string, string>
        {   // 8 wide x 10 tall, top row first
            ["octagon"]   = "..####../.######./########/########/########/########/########/########/.######./..####..",
            ["funnel"]    = "########/########/########/########/########/########/.######./.######./..####../..####..",
            ["notches"]   = "###..###/###..###/########/########/..####../########/########/########/###..###/###..###",
            ["lanes"]     = "########/########/########/########/####.###/####.###/####.###/########/########/########",
            ["hole"]      = "########/########/########/########/###..###/###..###/########/########/########/########",
            ["arch"]      = "########/########/########/########/###..###/###..###/########/########/########/########",
        };
        /// <summary>7x9 versions (11-17, BOARD_PRESET 2 - the phone readability test: Spencer "the smaller board reads better", and
        /// the shapes "are important"). Odd width like 9x11, so centre holes are 1 wide. The funnel's foot is 5 wide, not 3 (the
        /// 10-30 lesson: a 3-wide foot takes only 3-letter words). 'lanes' / 'split' / 'legs' / 'sidenotch' are not carried over:
        /// at 7 wide their lanes would be 3 tiles. Art: Boards/shape_&lt;name&gt;_7x9.png (render_shapes.py).</summary>
        public static readonly Dictionary<string, string> SHAPES_7x9 = new Dictionary<string, string>
        {   // 7 wide x 9 tall, top row first
            ["octagon"]   = "..###../.#####./#######/#######/#######/#######/#######/.#####./..###..",
            ["funnel"]    = "#######/#######/#######/#######/#######/#######/.#####./.#####./.#####.",
            ["notches"]   = "###.###/###.###/#######/#######/.#####./#######/#######/###.###/###.###",
            ["hole"]      = "#######/#######/#######/#######/###.###/###.###/#######/#######/#######",
            ["arch"]      = "#######/#######/#######/#######/###.###/###.###/#######/#######/#######",
        };
        /// <summary>Install the void mask for <paramref name="name"/> (null/unknown/size mismatch = rectangle). Call before the
        /// board is filled. Logs [Shape].</summary>
        /// <summary>11-03: the bot's sim snapshot saves/restores the shape - SetupSimLevel installs each level's mask into these
        /// statics and the bot skips L20 (bonus), so L19's 'arch' stayed live after a run and broke the real board's gravity.</summary>
        public static void GetShapeState(out string name, out bool[,] mask) { name = BoardShape; mask = s_void; }
        public static void SetShapeState(string name, bool[,] mask) { BoardShape = name; s_void = mask; }
        public static void SetBoardShape(string name)
        {
            BoardShape = null; s_void = null;
            var table = BOARD_PRESET == 1 ? SHAPES_8x10 : BOARD_PRESET == 2 ? SHAPES_7x9 : SHAPES;
            if (string.IsNullOrEmpty(name) || !table.TryGetValue(name, out var mask)) return;
            var rows = mask.Split('/');
            if (rows.Length != ROWS || rows[0].Length != COLS)
            { Debug.LogWarning($"[Shape] '{name}' is {rows[0].Length}x{rows.Length}, board is {COLS}x{ROWS} - using the rectangle"); return; }
            var v = new bool[COLS, ROWS]; int n = 0;
            for (int r = 0; r < ROWS; r++) for (int x = 0; x < COLS; x++) { bool vd = rows[r][x] == '.'; v[x, ROWS - 1 - r] = vd; if (vd) n++; }
            s_void = v; BoardShape = name;
            if (!Simulating) WDLog.Log($"[Shape] board shape '{name}': {n} void cell(s), {COLS * ROWS - n} real");
        }

        // ── GRASS = MOWING (2026-10-23 Spencer: "royal match has other ideas ... the grass underneath"; then "i thought it mows it and
        // gets rid of it" — RIGHT: verified from his RM recording, L10 grass 60 -> 0, L11 70 -> 0, the green squares disappear under
        // matches. The first build had it backwards, spreading). Grass belongs to the BOARD SQUARE (tiles fall over it). The level
        // starts with N squares covered in a designed pattern; any letter popped on a grass square — word, rocket, bomb — mows it.
        // Goal: mow them all. ──
        public static bool GRASS_ON = true;
        private bool[,] _grass;
        public int GrassAtStart { get; private set; }
        public bool HasGrass(int x, int y) => _grass != null && InBounds(x, y) && _grass[x, y];
        public int CountGrass() { if (_grass == null) return 0; int n = 0; for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (_grass[x, y]) n++; return n; }
        public void ClearGrass() { _grass = null; GrassAtStart = 0; }
        /// <summary>Cover <paramref name="count"/> squares in the named pattern (LayoutCells; whole board allowed). Returns squares covered.</summary>
        public int PlaceGrass(int count, string layout)
        {
            _grass = new bool[COLS, ROWS]; GrassAtStart = 0;
            var order = LayoutCells(string.IsNullOrEmpty(layout) ? "wall" : layout, false);
            for (int x = 0; x < COLS && order.Count < COLS * ROWS; x++) order.Add(new Vector2Int(x, ROWS - 1));   // top row last
            foreach (var p in order)
            {
                if (GrassAtStart >= count) break;
                if (_grass[p.x, p.y] || IsVoid(p.x, p.y)) continue;   // never grass on a void (unmowable)
                _grass[p.x, p.y] = true; GrassAtStart++;
            }
            return GrassAtStart;
        }
        /// <summary>Mow every grass square in <paramref name="cleared"/>. Returns squares mowed.</summary>
        private int MowGrass(List<Vector2Int> cleared)
        {
            if (_grass == null || !GRASS_ON || cleared == null) return 0;
            int n = 0;
            foreach (var c in cleared) if (InBounds(c.x, c.y) && _grass[c.x, c.y]) { _grass[c.x, c.y] = false; n++; }
            if (n > 0 && !Simulating) WDLog.Log($"[Grass] mowed {n} -> {CountGrass()} left");
            return n;
        }

        public static List<Vector2Int> LayoutCells(string pattern, bool forIce)
        {
            var list = new List<Vector2Int>();
            if (string.IsNullOrEmpty(pattern)) return list;
            int top = ROWS - 2;                       // never the top row (refills land there)
            int cx = COLS / 2, cy = ROWS / 2;
            int xMin = forIce ? 1 : 0, xMax = forIce ? COLS - 2 : COLS - 1, yMin = forIce ? 1 : 0;   // ice stays off the edges
            void Add(int x, int y) { var p = new Vector2Int(x, y); if (x >= xMin && x <= xMax && y >= yMin && y <= top && !list.Contains(p)) list.Add(p); }
            void Row(int y) { Add(cx, y); for (int d = 1; d <= COLS; d++) { Add(cx - d, y); Add(cx + d, y); } }   // centre-out
            switch (pattern)
            {
                case "wall":     for (int y = 0; y <= top; y++) Row(y); break;
                case "columns":  for (int k = 0; k < cx; k++) for (int y = 0; y <= top; y++) { Add(k, y); Add(COLS - 1 - k, y); } break;
                case "pyramid":  for (int y = 0, w = COLS; w > 0 && y <= top; y++, w -= 2) for (int d = 0; d <= w / 2; d++) { Add(cx - d, y); Add(cx + d, y); } break;
                case "band":     for (int r = 0; r <= ROWS; r++) { Row(cy - r); Row(cy + r); } break;
                case "line":     for (int r = 0; r <= ROWS; r++) Row(cy + 1 - r); break;
                case "ring":     for (int ring = 1; ring <= cx; ring++) for (int x = 0; x < COLS; x++) for (int y = 0; y <= top; y++)
                                     if (Mathf.Max(Mathf.Abs(x - cx), Mathf.Abs(y - cy)) == ring) Add(x, y);
                                 break;
                case "diamond":
                case "center":   for (int r = 0; r <= COLS + ROWS; r++) for (int x = 0; x < COLS; x++) for (int y = 0; y <= top; y++)
                                     if (Mathf.Abs(x - cx) + Mathf.Abs(y - cy) == r) Add(x, y);
                                 break;
                case "cross":    Add(cx, cy); for (int d = 1; d <= ROWS; d++) { Add(cx - d, cy); Add(cx + d, cy); Add(cx, cy - d); Add(cx, cy + d); } break;
                case "diagonal": for (int d = 0; d <= ROWS; d++) { Add(cx - d, cy - d); Add(cx + d, cy + d); Add(cx - d, cy + d); Add(cx + d, cy - d); } break;
                case "corners":  for (int k = 0; k < 3; k++) for (int j = 0; j <= k; j++) { int a = j, b = k - j; Add(a, b); Add(COLS - 1 - a, b); Add(a, top - b); Add(COLS - 1 - a, top - b); } break;
                case "checker":  for (int y = 0; y <= top; y++) for (int x = 0; x < COLS; x++) if ((x + y) % 2 == 0) Add(x, y); break;
                // 11-04 (RM structure study; crates are FIXED now so shapes hold):
                // PILLARS: two inner columns from the floor (cols 2 and COLS-3) - every crate touchable from BOTH sides, vertical rockets
                // pay; replaces edge "columns" whose crates had one open face (L7 END-STALL 6.9).
                case "pillars":  for (int y = 0; y <= top; y++) { Add(2, y); Add(COLS - 3, y); } break;
                // LID: a crate roof at row ROWS-4 (cols 1..COLS-2, centre-out), then a narrower row under it - the letter goals sit
                // under it in the open middle (GOALS_CENTRAL). The edge columns stay open so letters keep flowing down past it (RM L8).
                case "lid":      { int ly = ROWS - 4; for (int d = 0; d < cx - 1; d++) { Add(cx - 1 - d, ly); Add(cx + d, ly); }
                                   Add(cx - 1, ly - 1); Add(cx, ly - 1); Add(cx - 2, ly - 1); Add(cx + 1, ly - 1); Add(cx - 1, ly - 2); Add(cx, ly - 2); } break;   // 6 + 4 + 2 = 12 (an inverted step-pyramid roof)
            }
            return list;
        }

        /// <summary>Share of letter cells that lie in a horizontal or vertical run of >= 4 non-crate cells — a pattern that fragments
        /// the board (Spencer 2026-10-23: "if words have to be 4 letters long the odd shapes might ... where you cant make words")
        /// shows up here as a low number.</summary>
        public float RunCoverage()
        {
            int letters = 0, covered = 0;
            for (int x = 0; x < COLS; x++)
                for (int y = 0; y < ROWS; y++)
                {
                    var d = _board[x, y]; if (d == null || d.IsStone) continue;
                    letters++;
                    int h = 1, v = 1;
                    for (int i = x - 1; i >= 0 && _board[i, y] != null && !_board[i, y].IsStone; i--) h++;
                    for (int i = x + 1; i < COLS && _board[i, y] != null && !_board[i, y].IsStone; i++) h++;
                    for (int j = y - 1; j >= 0 && _board[x, j] != null && !_board[x, j].IsStone; j--) v++;
                    for (int j = y + 1; j < ROWS && _board[x, j] != null && !_board[x, j].IsStone; j++) v++;
                    if (h >= 4 || v >= 4) covered++;
                }
            return letters == 0 ? 0f : (float)covered / letters;
        }

        public int PlaceCrates(int count, HashSet<Vector2Int> keep)
        {
            int placed = 0, mid = COLS / 2;
            if (!string.IsNullOrEmpty(CrateLayout))
            {
                bool Free(Vector2Int q) { var dq = _board[q.x, q.y]; return dq != null && !dq.IsStone && !dq.IsFrozen && !dq.HasGem && dq.SpecialKind == SPECIAL_NONE; }
                foreach (var p in LayoutCells(CrateLayout, false))
                {
                    if (placed >= count) break;
                    if (!Free(p)) continue;
                    // 11-02 SYMMETRIC_LAYOUTS: a pattern cut off part-way left a lone crate on one side; place the mirror with it
                    var m = new Vector2Int(COLS - 1 - p.x, p.y);
                    var put = (SYMMETRIC_LAYOUTS && m != p) ? (placed + 2 <= count && Free(m) ? new[] { p, m } : null) : new[] { p };
                    if (put == null) continue;
                    foreach (var q in put)
                    {
                        var d = _board[q.x, q.y];
                        d.IsStone = true; d.IsAnchored = false; d.RequiredWordLength = 0; d.IsFrozen = false;
                        keep.Add(q); placed++;
                    }
                }
                if (!Simulating) WDLog.Log($"[Layout] crates '{CrateLayout}': {placed}/{count} placed by pattern — {RunCoverage():P0} of letters sit in a straight run of 4+ (words need one)");
            }
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
            // FALLBACK (2026-10-23 bot: a 24-28 crate level only fit 10-24 — the same level was far easier on some boards).
            // Symmetric pairs ran out, so top up with single crates: first on free cells, then on planted cells (a near-word
            // under a crate is just lost). Never the top row (refills land there) or a special/gem/duck/ice cell.
            for (int pass = 0; pass < 2 && placed < count; pass++)
            {
                var spots = new List<Vector2Int>();
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS - 1; y++)
                    {
                        var p = new Vector2Int(x, y); var d = _board[x, y];
                        if (d == null || d.IsStone || d.IsFrozen || d.HasGem || d.SpecialKind != SPECIAL_NONE) continue;
                        if ((pass == 0) == keep.Contains(p)) continue;   // pass 0 = free cells only, pass 1 = planted cells only
                        spots.Add(p);
                    }
                while (placed < count && spots.Count > 0)
                {
                    int i = SurvivalRng.Range(0, spots.Count); var p = spots[i]; spots.RemoveAt(i);
                    var d = _board[p.x, p.y]; d.IsStone = true; d.IsAnchored = false; d.RequiredWordLength = 0; d.IsFrozen = false;
                    keep.Add(p); placed++;
                }
            }
            return placed;
        }

        // ── TREASURE (2026-10-27, Spencer: "added randomly for extra rewards"). Bonus loot, never a goal. Each level rolls
        // TREASURE_CHANCE for one (and TREASURE_CHANCE2 for a second); from TREASURE_FROM_LEVEL on; never the top row or the
        // bottom row; never on a special/gem/duck/ice/crate cell. Opening one pays TREASURE_COINS (HandManager.PayTreasure). ──
        public static float TREASURE_CHANCE = 0.55f, TREASURE_CHANCE2 = 0.2f;
        public static int   TREASURE_FROM_LEVEL = 4, TREASURE_HITS = 3, TREASURE_COINS = 5;
        public static bool  TREASURE_TEST = false;   // TEMP test switch: true = force 2 treasures on every level
        /// <summary>2026-10-28 Spencer: "the treasure chests look out of place, can we remove them for now until we get art that
        /// goes with this stuff?" false = no treasure tiles spawn (code + FX kept; flip back on with the new chest art).</summary>
        public static bool  TREASURE_ON = false;
        public int PlaceTreasures(int level, HashSet<Vector2Int> keep)
        {
            if (!TREASURE_ON) return 0;
            if (level < TREASURE_FROM_LEVEL && !TREASURE_TEST) return 0;
            int want = SurvivalRng.Value < TREASURE_CHANCE ? (SurvivalRng.Value < TREASURE_CHANCE2 ? 2 : 1) : 0;
            if (TREASURE_TEST && !Simulating) want = 2;   // TEMP 2026-10-27: every level gets 2 so Spencer can see them - set false before a build
            var spots = new List<Vector2Int>();
            for (int x = 0; x < COLS; x++)
                for (int y = 1; y < ROWS - 1; y++)
                {
                    var p = new Vector2Int(x, y); var d = _board[x, y];
                    if (d == null || d.IsStone || d.IsFrozen || d.HasGem || d.IsDropTarget || d.SpecialKind != SPECIAL_NONE || keep.Contains(p)) continue;
                    spots.Add(p);
                }
            int placed = 0;
            while (placed < want && spots.Count > 0)
            {
                int i = SurvivalRng.Range(0, spots.Count); var p = spots[i]; spots.RemoveAt(i);
                var d = _board[p.x, p.y]; d.IsStone = true; d.IsAnchored = false; d.RequiredWordLength = 0; d.IsFrozen = false;
                d.TreasureHits = TREASURE_HITS; keep.Add(p); placed++;
            }
            if (placed > 0 && !Simulating) WDLog.Log($"[Treasure] {placed} treasure tile(s) placed (level {level})");
            return placed;
        }

        // ── DUCKS (2026-10-22): bring-it-down goal. A duck is the old escort drop-target — a non-letter cell that falls with
        // gravity, survives every blast, can't be swapped, and is collected on the bottom row (SurvivalManager.TickDuckCollect). ──
        public static int DUCKS_ON_BOARD = 3;   // at most this many on the board at once; refills bring the rest
        public int CountDucks() { int n = 0; for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (_board[x, y] != null && _board[x, y].IsDropTarget) n++; return n; }
        private static void MakeDuck(RulesCellData d)
        {
            d.Letter = '#'; d.IsStone = true; d.IsDropTarget = true; d.IsAnchored = false; d.IsFrozen = false;
            d.HasGem = false; d.IsRainGold = false; d.SpecialKind = SPECIAL_NONE; d.RequiredWordLength = 0;
        }
        /// <summary>Ducks in different columns, in the middle band of the board — high enough to need real digging,
        /// low enough that the first one arrives within a few moves.</summary>
        public int PlaceDucks(int count, HashSet<Vector2Int> keep)
        {
            var cols = new List<int>(); for (int c = 1; c < COLS - 1; c++) cols.Add(c);
            for (int i = cols.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); int t = cols[i]; cols[i] = cols[j]; cols[j] = t; }
            int placed = 0;
            foreach (int c in cols)
            {
                if (placed >= count) break;
                int y = SurvivalRng.Range(ROWS / 2 - 1, ROWS - 3);
                var d = _board[c, y];
                if (d == null || d.IsStone || keep.Contains(new Vector2Int(c, y))) continue;
                if (IsVoid(c, 0)) continue;   // shaped board: ducks leave from row 0, so their column must reach it
                MakeDuck(d); keep.Add(new Vector2Int(c, y)); placed++;
                WDLog.Log($"[Duck] placed at ({c},{y})");
            }
            return placed;
        }
        /// <summary>Bottom-row ducks that have landed (data side).</summary>
        public List<Vector2Int> DucksOnBottomRow()
        {
            List<Vector2Int> list = null;
            for (int c = 0; c < COLS; c++) if (_board[c, 0] != null && _board[c, 0].IsDropTarget) (list ??= new List<Vector2Int>()).Add(new Vector2Int(c, 0));
            return list;
        }

        public const string GEM_LETTERS = "EARIOTNSLDU";   // gems only ride common letters, so a gem is always usable
        public static int GEM_MAX_PER_REFILL = 2;
        public static int GEM_SPARES = 3;                       // gems on the board beyond what the goal still needs (2026-10-23)
        // FIXED GEMS (2026-10-26 Spencer: "go back to how it used to be where there are only a certain amount of gems that spawn and
        // extra gems are not added"). true = the level places ALL its goal gems at the start and refills never add gems.
        public static bool GEMS_FIXED = true;
        public static int GEM_MIN_ON_BOARD = 4;               // refills top the board back up to this many until the goal is met
        /// <summary>Where an airplane goes: a remaining goal piece (ice, crate, gem, collect letter; for ducks, the tile under the
        /// lowest duck), picked at random; no goal piece = a random letter. (-1,-1) if the board is empty.</summary>
        public Vector2Int PickAirplaneTarget(Vector2Int from, HashSet<Vector2Int> exploded, List<Vector2Int> area)
        {
            var goal = new List<Vector2Int>(); var any = new List<Vector2Int>();
            char collect = LevelRules.ActiveCollectLetter;
            Vector2Int lowDuck = new Vector2Int(-1, ROWS);
            // ONLY GOALS STILL OPEN (2026-10-23 Spencer: "if all objectives are finished cept one ... the airplane will still fly to
            // them and destroy them even if you dont need them"). Gem spares made it worse: finished gem goal, spare gems still
            // on the board, the plane hit a spare instead of the crate you needed.
            bool needIce = GoalOpen(LevelRules.GoalType.Ice), needCrates = GoalOpen(LevelRules.GoalType.Crates),
                 needGems = GoalOpen(LevelRules.GoalType.Gems), needCollect = GoalOpen(LevelRules.GoalType.Collect),
                 needDucks = GoalOpen(LevelRules.GoalType.Ducks), needBooks = GoalOpen(LevelRules.GoalType.Books), needSafes = GoalOpen(LevelRules.GoalType.Safes) || (LevelRules.BonusLevelActive && LevelRules.GOLD_RUSH_SAFES);   // BOOKSHELF / SAFE (11-03); Gold Rush safes too
            for (int x = 0; x < COLS; x++)
                for (int y = 0; y < ROWS; y++)
                {
                    var p = new Vector2Int(x, y); var d = _board[x, y];
                    if (d == null || exploded.Contains(p) || area.Contains(p)) continue;
                    if (d.IsDropTarget) { if (needDucks && y > 0 && y < lowDuck.y) lowDuck = p; continue; }
                    bool isGoal = (d.IsFrozen && needIce) || (d.IsStone && d.TreasureHits == 0 && d.ShelfPart == 0 && needCrates) || (d.HasGem && needGems)
                               || (d.ShelfPart > 0 && (d.ShelfKind == 1 ? needSafes && d.ShelfBooks > 0 : needBooks))   // the propeller book homes on the bookshelf / safe (11-03 Spencer)
                               || (needCollect && collect != '\0' && char.ToUpperInvariant(d.Letter) == collect && !d.IsStone);
                    if (isGoal) goal.Add(p); else if (!d.IsStone) any.Add(p);
                }
            if (goal.Count == 0 && lowDuck.x >= 0) { var under = new Vector2Int(lowDuck.x, lowDuck.y - 1); if (_board[under.x, under.y] != null && !area.Contains(under)) goal.Add(under); }
            var pool = goal.Count > 0 ? goal : any;
            return pool.Count == 0 ? new Vector2Int(-1, -1) : pool[SurvivalRng.Range(0, pool.Count)];
        }

        /// <summary>Does the level have a goal of this type that still needs pieces?</summary>
        private static bool GoalOpen(LevelRules.GoalType t)
        {
            foreach (var g in LevelRules.Goals) if (g.Type == t && LevelRules.GoalProgress(g) < g.Target) return true;
            return false;
        }
        public int CountGems() { int n = 0; for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (_board[x, y] != null && _board[x, y].HasGem) n++; return n; }
        /// <summary>Gems on <paramref name="count"/> ordinary common letters, spread over the board.</summary>
        // 11-17 GEM SPACING (Spencer: "a lot of the gems are all close together in a line ... not the best look"). Placement was
        // random mirror pairs with nothing keeping gems apart, so a pair by the centre sat side by side and two pairs on one row
        // made a line of four - which one word could collect (a balance swing, not only a look). Now: no gem touches another
        // (8 neighbours), at most GEM_MAX_PER_LINE in any row or column, and a mirror pair keeps 2+ tiles between its halves.
        // If the count cannot be reached that way the last loop falls back to the old free placement, so the goal count is
        // always met. GEM_SPACING = false: the old placement.
        public static bool GEM_SPACING = true;
        public static int  GEM_MAX_PER_LINE = 2;
        /// <param name="loose">second-best: may touch a gem by a CORNER and sit 3 to a line - still never side by side.</param>
        private bool GemFits(Vector2Int p, Vector2Int? also = null, bool loose = false)
        {
            if (!GEM_SPACING) return true;
            int row = 0, col = 0;
            for (int x = 0; x < COLS; x++) { var d = _board[x, p.y]; if ((d != null && d.HasGem) || (also.HasValue && also.Value == new Vector2Int(x, p.y))) row++; }
            for (int y = 0; y < ROWS; y++) { var d = _board[p.x, y]; if ((d != null && d.HasGem) || (also.HasValue && also.Value == new Vector2Int(p.x, y))) col++; }
            int cap = GEM_MAX_PER_LINE + (loose ? 1 : 0);
            if (row >= cap || col >= cap) return false;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                if (loose && dx != 0 && dy != 0) continue;
                var q = new Vector2Int(p.x + dx, p.y + dy);
                if (also.HasValue && also.Value == q) return false;
                if (InBounds(q.x, q.y) && _board[q.x, q.y] != null && _board[q.x, q.y].HasGem) return false;
            }
            return true;
        }
        public int PlaceGems(int count, HashSet<Vector2Int> keep)
        {
            int placed = 0;
            if (SYMMETRIC_LAYOUTS)
            {   // 11-02 (Spencer: make the board more interesting - designed-looking layouts): gems in left/right mirror pairs, one on
                // the centre column only when the count is odd. Same eligibility as below; the random loop tops up any shortfall.
                bool Ok(Vector2Int q) { var dq = _board[q.x, q.y]; return dq != null && !dq.IsStone && !dq.IsFrozen && !dq.HasGem && dq.SpecialKind == SPECIAL_NONE && !keep.Contains(q) && GEM_LETTERS.IndexOf(char.ToUpperInvariant(dq.Letter)) >= 0; }
                int mid = COLS / 2;
                for (int attempt = 0; attempt < count * 60 && placed < count; attempt++)
                {
                    int left = count - placed;
                    bool central = GOALS_CENTRAL && SurvivalManager.FullBoardActive && attempt < count * 45;   // 11-04: the open middle first, the old area as a fallback
                    var a = central ? new Vector2Int(SurvivalRng.Range(1, mid + 1), SurvivalRng.Range(2, ROWS - 2)) : new Vector2Int(SurvivalRng.Range(0, mid + 1), SurvivalRng.Range(1, ROWS - 1)); var b = new Vector2Int(COLS - 1 - a.x, a.y);
                    if (a == b) { if (left % 2 == 0 || !Ok(a) || !GemFits(a)) continue; _board[a.x, a.y].HasGem = true; placed++; continue; }
                    if (left < 2 || !Ok(a) || !Ok(b)) continue;
                    if (GEM_SPACING && (Mathf.Abs(b.x - a.x) < 3 || !GemFits(a, b) || !GemFits(b, a))) continue;   // 2+ tiles between the halves; neither touches a gem
                    _board[a.x, a.y].HasGem = true; _board[b.x, b.y].HasGem = true; placed += 2;
                }
            }
            for (int attempt = 0; attempt < count * 40 && placed < count; attempt++)
            {
                // 11-17 v2 (his L1-5 recording: L4 and L5 still had touching gems - the spaced search ran out in the cramped middle and
                // the free fallback dropped the rest anywhere). Steps: spaced in the open middle -> spaced anywhere -> loosely
                // spaced anywhere (corner contact allowed, 3 to a line) -> free, only so the goal count is always met.
                bool central = GOALS_CENTRAL && SurvivalManager.FullBoardActive && attempt < (GEM_SPACING ? count * 14 : count * 30);   // 11-04: open middle first
                var p = central ? new Vector2Int(SurvivalRng.Range(1, COLS - 1), SurvivalRng.Range(2, ROWS - 2)) : new Vector2Int(SurvivalRng.Range(0, COLS), SurvivalRng.Range(1, ROWS - 1));
                var d = _board[p.x, p.y];
                if (d == null || d.IsStone || d.IsFrozen || d.HasGem || d.SpecialKind != SPECIAL_NONE || keep.Contains(p)) continue;
                if (GEM_LETTERS.IndexOf(char.ToUpperInvariant(d.Letter)) < 0) continue;
                if (attempt < count * 28) { if (!GemFits(p)) continue; }
                else if (attempt < count * 37) { if (!GemFits(p, null, true)) continue; }
                d.HasGem = true; placed++;
            }
            // 11-17 SWEEP (7x9 bot survey: every level with 6+ gems was unwinnable - the random search above can come up short on
            // a 63-cell board, and a fixed-gem goal that asks for more gems than exist can never be met). Random tries are not a
            // guarantee, so finish by walking EVERY cell from a random start, loosening one rule per pass: spaced -> loosely
            // spaced -> anywhere off the planted words -> on a planted word -> any letter.
            for (int pass = 0; pass < 5 && placed < count; pass++)
            {
                int start = SurvivalRng.Range(0, COLS * ROWS);
                for (int i = 0; i < COLS * ROWS && placed < count; i++)
                {
                    int k = (start + i * 5) % (COLS * ROWS);   // step 5 is coprime with 63 / 80 / 99: visits every cell, scattered
                    var p = new Vector2Int(k % COLS, k / COLS);
                    var d = _board[p.x, p.y];
                    if (d == null || d.IsStone || d.IsFrozen || d.HasGem || d.IsDropTarget || d.SpecialKind != SPECIAL_NONE || !char.IsLetter(d.Letter)) continue;
                    if (pass < 4 && GEM_LETTERS.IndexOf(char.ToUpperInvariant(d.Letter)) < 0) continue;
                    if (pass < 3 && keep.Contains(p)) continue;
                    if (pass == 0 && !GemFits(p)) continue;
                    if (pass == 1 && !GemFits(p, null, true)) continue;
                    d.HasGem = true; placed++;
                }
            }
            if (placed < count && !Simulating) WDLog.Log($"[Gems] sweep still short: {placed}/{count}");
            return placed;
        }

        /// <summary>TNT letters: put dynamite on <paramref name="count"/> ordinary letters in the middle band of the board.</summary>
        public int PlaceStartTnt(int count, HashSet<Vector2Int> keep) => PlaceStartSpecial(SPECIAL_TNT, count, keep);
        /// <summary>Put <paramref name="count"/> specials of <paramref name="kind"/> on plain, low-value letters away from the edges
        /// (start TNT, win-streak rockets/bombs/discos). Cells join <paramref name="keep"/>.</summary>
        public int PlaceStartSpecial(int kind, int count, HashSet<Vector2Int> keep, int minGap = 0)
        {
            int placed = 0;
            for (int attempt = 0; attempt < count * 30 && placed < count; attempt++)
            {
                var p = new Vector2Int(SurvivalRng.Range(1, COLS - 1), SurvivalRng.Range(2, ROWS - 2));
                var d = _board[p.x, p.y];
                if (d == null || d.IsStone || d.IsFrozen || d.SpecialKind != SPECIAL_NONE || keep.Contains(p)) continue;
                if (LetterData.GetPoints(d.Letter) >= Tile.HIGH_VALUE_POINTS) continue;
                if (d.HasGem || d.IsDropTarget) continue;
                if (minGap > 0 && SpecialWithin(p, minGap - 1)) continue;   // streak specials spread out — no free combo on move 1
                d.SpecialKind = kind; keep.Add(p); placed++;
            }
            return placed;
        }
        /// <summary>Is there a special within <paramref name="r"/> cells (Chebyshev) of <paramref name="p"/>?</summary>
        private bool SpecialWithin(Vector2Int p, int r)
        {
            for (int dx = -r; dx <= r; dx++) for (int dy = -r; dy <= r; dy++)
            {
                int x = p.x + dx, y = p.y + dy;
                if (InBounds(x, y) && _board[x, y] != null && _board[x, y].SpecialKind != SPECIAL_NONE) return true;
            }
            return false;
        }
        public static int FULL_START_SHORT_WORDS = 8;
        private static List<string> s_common3;

        /// <summary>Write up to <paramref name="count"/> common 3-letter words intact (across or down) where they spell no
        /// 4+ word with their neighbours. Cells used are added to <paramref name="keep"/>. Returns words placed.</summary>
        /// <summary>SWIPE LESSON PLANT (2026-10-23 Spencer: the tutorial "should probably tell players that can swipe across to make
        /// words that are already on the board"). Words already sitting on the board are what a swipe is FOR (the auto scanner
        /// only reads the cells a swap touched), but a fresh board is scrubbed of them — so the lesson level gets one: a common
        /// 4-letter word written left-to-right into a middle row, away from cells already planted. The caller adds the cells to
        /// its keep set so the scrub / crates leave it alone. Returns null if nothing fits.</summary>
        public List<Vector2Int> PlantSwipeWord(HashSet<Vector2Int> avoid, out string word)
        {
            word = null;
            IsCommonWord("A");   // loads s_commonWords
            var pool = new List<string>();
            foreach (var w in s_commonWords) if (w.Length == 4 && w.IndexOfAny(new[] { 'Q', 'X', 'Z', 'J' }) < 0 && WordDictionary.IsValidWord(w)) pool.Add(w);
            if (pool.Count == 0) return null;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int y = SurvivalRng.Range(2, ROWS - 3), x0 = SurvivalRng.Range(0, COLS - 3);
                var cells = new List<Vector2Int>();
                bool ok = true;
                for (int i = 0; i < 4 && ok; i++)
                {
                    var c = new Vector2Int(x0 + i, y); var d = _board[c.x, c.y];
                    ok = d != null && char.IsLetter(d.Letter) && !d.IsStone && !d.IsFrozen && !d.IsWild && !d.HasGem && !d.IsDropTarget
                         && d.SpecialKind == SPECIAL_NONE && (avoid == null || !avoid.Contains(c));
                    cells.Add(c);
                }
                if (!ok) continue;
                word = pool[SurvivalRng.Range(0, pool.Count)];
                for (int i = 0; i < 4; i++) _board[cells[i].x, cells[i].y].Letter = word[i];
                TutorialSwipeCells = cells; TutorialSwipeWord = word;
                WDLog.Log($"[Tut] swipe lesson: planted '{word}' at row {y}, cols {x0}-{x0 + 3}");
                return cells;
            }
            return null;
        }
        public static List<Vector2Int> TutorialSwipeCells; public static string TutorialSwipeWord;

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
            // Theme level: a 3-letter THEME word now pops, so new letters must not spell one either (a free PIG out of a refill).
            int askedMin = minLen;
            if (LevelRules.ThemeShortActive && minLen > MIN_WORD_LENGTH) minLen = MIN_WORD_LENGTH;
            var sb = new System.Text.StringBuilder(7);
            int l = x, r = x;
            while (l - 1 >= 0 && g[l - 1, y] != '\0') l--;
            while (r + 1 < COLS && g[r + 1, y] != '\0') r++;
            for (int s = l; s <= x; s++)
                for (int e = Mathf.Max(x, s + minLen - 1); e <= r && e - s + 1 <= MAX_WORD_LENGTH; e++)
                {
                    sb.Length = 0; for (int k = s; k <= e; k++) sb.Append(g[k, y]);
                    if (e - s + 1 < askedMin && !LevelRules.IsThemeWord(sb.ToString())) continue;
                    if (WordDictionary.IsValidWord(sb.ToString())) return true;
                }
            int top = y, bot = y;
            while (top + 1 < ROWS && g[x, top + 1] != '\0') top++;
            while (bot - 1 >= 0 && g[x, bot - 1] != '\0') bot--;
            for (int s = top; s >= y; s--)
                for (int e = Mathf.Min(y, s - minLen + 1); e >= bot && s - e + 1 <= MAX_WORD_LENGTH; e--)
                {
                    sb.Length = 0; for (int k = s; k >= e; k--) sb.Append(g[x, k]);
                    if (s - e + 1 < askedMin && !LevelRules.IsThemeWord(sb.ToString())) continue;
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
            // LETTER SUPPLY (2026-10-30, Spencer's playthrough: boards at 27-32% vowels, ~8-15% around the last goals, 2 Q + Z + X
            // on one board; "mostly hunting for any option at all"). A THERMOSTAT: the board's own mix decides what falls next.
            //  - vowels below VOWEL_MIN on the board, or below LOCAL_VOWEL_MIN within 2 cells of this one -> draw a vowel;
            //    above VOWEL_MAX -> no vowel. (Refusing letters that spell a word with their neighbours used to bias the refill
            //    toward consonants - vowels make words most easily - so the board drifted vowel-poor every move.)
            //  - hard letters capped by count (HARD_CAPS); the old FULL_MAX_HIGH cap is dead since HIGH_VALUE_POINTS went to 99.
            int vowels = 0; foreach (var vc in VOWELS) vowels += counts[vc - 'A'];
            int localN = 0, localV = 0;
            for (int cx = Mathf.Max(0, x - 2); cx <= Mathf.Min(COLS - 1, x + 2); cx++)
                for (int cy = Mathf.Max(0, y - 2); cy <= Mathf.Min(ROWS - 1, y + 2); cy++)
                {
                    if (cx == x && cy == y) continue;
                    char c = g[cx, cy]; if (c < 'A' || c > 'Z') continue;
                    localN++; if (IsVowelLetter(c)) localV++;
                }
            float share = filled > 0 ? (float)vowels / filled : 0.4f;
            bool wantVowel = SUPPLY_ON && filled >= 12 && (share < VOWEL_MIN || (localN >= 6 && (float)localV / localN < LOCAL_VOWEL_MIN));
            bool noVowel   = SUPPLY_ON && filled >= 12 && share > VOWEL_MAX;
            char L = 'E';
            for (int i = 0; i < FULL_NOWORD_TRIES; i++)
            {
                L = (wantVowel && i < FULL_NOWORD_TRIES - 3) ? DrawWeightedVowel() : DrawWeightedLetter(); g[x, y] = L;
                bool isHigh = LetterData.GetPoints(L) >= Tile.HIGH_VALUE_POINTS;
                if (isHigh && high >= FULL_MAX_HIGH) continue;
                if (noVowel && IsVowelLetter(L)) continue;
                if (SUPPLY_ON && OverHardCap(L, counts)) continue;
                if (filled >= 12 && counts[L - 'A'] + 1 > 1.5f * LetterShare(L) * (filled + 1) + 1f) continue;
                if (NO_CLUMPS && i < FULL_NOWORD_TRIES - 2 && Clumps(g, x, y, L)) continue;   // 11-04: no III / three-of-a-kind huddles
                if (!FormsWordAt(g, x, y)) return L;
            }
            if (SUPPLY_ON && OverHardCap(L, counts)) { L = 'E'; g[x, y] = L; }   // never leave a capped letter as the fallback
            return L;   // very rare: give up and keep the last draw
        }
        public static bool  SUPPLY_ON = true;
        /// <summary>11-04 NO CLUMPS (Spencer, after a full L1-20 run: "a lot of times where there would be like three Is in a row or three
        /// of the same letters all jumbled together"). The supply balanced the board's MIX but never looked at neighbours, so the
        /// same letter could land beside itself again and again - dead patches no word runs through. A drawn letter is refused if
        /// its 8 neighbours already hold 2 of it (a clump of 3), or 1 of it for a letter English rarely doubles (I, U, A, V...).
        /// Real doubles (EE OO LL SS TT ...) stay possible. false = old behaviour.</summary>
        public static bool  NO_CLUMPS = true;
        private const string DOUBLES_OK = "BCDEFGLMNOPRSTZ";   // letters that double in common words (BOOK, BALL, LETTER, PIZZA...)
        private bool Clumps(char[,] g, int x, int y, char L)
        {
            int n = 0;
            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            {
                if (dx == 0 && dy == 0) continue;
                int cx = x + dx, cy = y + dy;
                if (cx < 0 || cy < 0 || cx >= g.GetLength(0) || cy >= g.GetLength(1)) continue;
                if (g[cx, cy] == L) n++;
            }
            return n >= 2 || (n >= 1 && DOUBLES_OK.IndexOf(L) < 0);
        }
        public static float VOWEL_MIN = 0.38f, VOWEL_MAX = 0.46f, LOCAL_VOWEL_MIN = 0.30f;
        private const string VOWELS = "AEIOU";
        private static bool IsVowelLetter(char c) => VOWELS.IndexOf(char.ToUpperInvariant(c)) >= 0;
        /// <summary>Max on the board at once. Q/Z/X/J clog a word grid (research: cap at 1); K/V/W rarely fit, so 2.</summary>
        public static readonly System.Collections.Generic.Dictionary<char, int> HARD_CAPS = new System.Collections.Generic.Dictionary<char, int>
            { { 'Q', 1 }, { 'Z', 1 }, { 'X', 1 }, { 'J', 1 }, { 'K', 2 }, { 'V', 2 }, { 'W', 2 } };
        private static bool OverHardCap(char L, int[] counts) => HARD_CAPS.TryGetValue(L, out int cap) && counts[L - 'A'] + 1 > cap;
        private static char DrawWeightedVowel()
        {
            for (int i = 0; i < 40; i++) { char c = DrawWeightedLetter(); if (IsVowelLetter(c)) return c; }   // bag-weighted: E/A/I/O > U
            return "EAIO"[SurvivalRng.Range(0, 4)];
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
                    if (!IsVoid(c, r)) _board[c, r] = new RulesCellData { Letter = DrawNoWordLetter(g, c, r), Col = c, Row = r, PlayerIndex = -1 };   // shaped board: voids stay empty
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

        /// <summary>Theme-level plant pool (set by LevelRules.Apply, null otherwise) and the share of plants drawn from it.</summary>
        public static List<string> ThemePlantPool;
        public static float THEME_PLANT_SHARE = 0.65f;

        public void TryPlantNearWord(char[] row)
        {
            EnsurePlantLists();   // one loader (builds the lists AND the set)
            for (int attempt = 0; attempt < 6; attempt++)
            {
                bool five = SurvivalRng.Value < (SurvivalManager.FullBoardActive ? FULL_PLANT_FIVE_SHARE : PLANT_FIVE_SHARE) && s_plant5.Count > 0;
                var pool = five ? s_plant5 : s_plant4;
                // THEME LEVEL: most plants are theme words, so "make 3 animal words" is findable, not a hunt.
                if (ThemePlantPool != null && ThemePlantPool.Count > 0 && SurvivalRng.Value < THEME_PLANT_SHARE) pool = ThemePlantPool;
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
        /// <summary>FOUND-IT reward for DRAGGED words (10-31, Spencer OK'd): special one size earlier + bigger pop/sound.</summary>
        public static bool DRAG_REWARD = true;
        public static int  DRAG_SPECIAL_BONUS = 1;
        public static readonly HashSet<int> SwipedPrimedIds = new HashSet<int>();
        public static readonly HashSet<string> SwipedWords = new HashSet<string>();
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
        public static int SWIPE_MIN_LENGTH => LevelRules.ThemeShortActive ? MIN_WORD_LENGTH : EffectiveMinWordLength;   // full board: 4+ (2026-10-21); theme levels: 3 — BuildExplicitMatch then allows only theme words that short

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
                sb.Append(ScanChar(cell));   // 11-03: a power-up reads as the wild
            }

            string word = sb.ToString();
            if (cells.Count < EffectiveMinWordLength && !LevelRules.IsThemeWord(word)) { reject = "too short"; return null; }   // only theme words may be short

            // ACCEPT THE PATH IN EITHER ORDER. 2026-10-19 — this is the whole point of the
            // feature and the first build missed it. The words a player can actually FIND by
            // eye are precisely the ones the auto-scanner cannot see, i.e. the backwards and
            // diagonal ones; a left-to-right word is already primed before they could trace it.
            // But a player reading a backwards word still drags left-to-right, which spells it
            // reversed. So try the trace order, then its reverse, and keep whichever is real.
            // Cells are reversed alongside the word because downstream code maps Word[i] to
            // Cells[i] (WildLetterIndices, the primed glow, CellKey).
            var ordered = new List<Vector2Int>(cells);
            string fwd = ResolveWildPattern(word, true);   // a swipe is always on purpose — the wider list counts (LOIN, HONK); 11-03 wild power-ups
            if (fwd != null) word = fwd;
            else
            {
                char[] rev = word.ToCharArray();
                System.Array.Reverse(rev);
                string back = ResolveWildPattern(new string(rev), true);
                if (back == null)
                { reject = $"neither '{word}' nor its reverse is a word"; return null; }
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
                    if (actual != expected && ScanChar(_board[col, row]) != WILD_CHAR)   // 11-03: a wild power-up matches any letter
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
                    if (SimGrid != null)
                    {
                        for (int c3 = 0; c3 < pw.Cells.Count; c3++)
                        {
                            Tile t = SimGrid.GetTile(pw.Cells[c3].x, pw.Cells[c3].y);
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

            // One shared plan (PlanFall) for the data AND GridManager.ApplyGravity - both read the same occupancy + fixed cells,
            // so the visual can't drift from the data (HANDOFF 4.2). Positions come from where each cell IS in _board, never
            // cell.Row (2026-06-03: a Jester Hat shuffle left cell.Row stale -> scrambled moves -> desync).
            var occ = new bool[COLS, ROWS]; var fix = new bool[COLS, ROWS];
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
            { var d = _board[x, y]; if (d == null) continue; occ[x, y] = true; fix[x, y] = IsFixedCell(d); }
            var plan = PlanFall(occ, fix, FallSpawnRows);
            var refill = new List<Vector2Int>();   // cells the plan filled with NEW pieces (from above the board)
            var real = new List<FallMove>();
            foreach (var m in plan) { if (m.from.y >= ROWS) refill.Add(m.to); else real.Add(m); }
            if (real.Count > 0)
            {
                var moved = new RulesCellData[real.Count];
                for (int i = 0; i < real.Count; i++) { moved[i] = _board[real[i].from.x, real[i].from.y]; _board[real[i].from.x, real[i].from.y] = null; }
                for (int i = 0; i < real.Count; i++) { _board[real[i].to.x, real[i].to.y] = moved[i]; moves[real[i].from] = real[i].to; }
            }
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) if (_board[x, y] != null) { _board[x, y].Col = x; _board[x, y].Row = y; }

            // FULL BOARD: new letters arrive IN the same fall, the Candy Crush way (2026-10-21 Spencer: "i dont think the
            // letters should wait before all the explosions are cleared"). The data gets them here; the grid's gravity
            // spawns their tiles above the board so each column falls as one piece (GridManager.SpawnRefillTiles).
            LastRefillCells.Clear();
            if (SurvivalManager.FullBoardActive && FULL_REFILL_IN_GRAVITY) FillTopsInData(refill);   // 11-04: exactly the cells the plan's new pieces reached (incl. under crates)

            return moves;
        }

        // ── FIXED CRATES + DIAGONAL SLIDE (2026-11-04, Spencer: "yes please. i noticed that too on my playthrough"). RM's boxes
        // never fall; the tiles above slide DIAGONALLY around them into the gap below. Our crates fell with the letters, so every
        // layout (wall / columns / ring / lid) collapsed into a bottom-row pile in a few moves - the END-STALL (8.8 turns on L7).
        // Crates were anchored once before and Spencer saw holes under them (10-21: "the crates are not dropping") because our
        // fall was straight down only; the slide fills those holes. FIXED_CRATES false = crates fall again (old behaviour).
        // Shelves / safes / vaults were already anchored; they get the slide too. ──
        public static bool FIXED_CRATES = true;
        /// <summary>11-04 (Spencer: "maybe just leave it open on the board when its cracked"): a safe's last hit no longer clears its
        /// 2x2 - it stays as the OPEN, empty vault (ShelfBooks 0), a fixed piece the letters flow around. false = it clears.</summary>
        public static bool SAFE_STAYS_OPEN = true;
        public static bool DIAGONAL_SLIDE = true;
        public static bool SLIDE_PAST_SEALED = true;     // 11-17: a shaft with a sealed top cell still fills from the side lower down (false = the 11-04 rule)
        public static bool SLIDE_ONE_PER_SHAFT = true;   // 11-04: one slide per shaft per wave (stream), not the whole shaft at once
        /// <summary>A cell that never falls: anchored pieces, and (FIXED_CRATES, full board) crates. Ducks fall.</summary>
        public static bool IsFixedCell(RulesCellData d) => d != null && (d.IsAnchored || (FIXED_CRATES && SurvivalManager.FullBoardActive && d.IsStone && !d.IsDropTarget));

        public class FallMove { public Vector2Int from, to; public List<Vector2Int> path; public List<int> phase; }   // phase[i] = the wave of the step INTO path[i+1]
        /// <summary>Spawn rows the fall plan stacks above the board when the refill rides the fall (full board).</summary>
        public static int FallSpawnRows => SurvivalManager.FullBoardActive && FULL_REFILL_IN_GRAVITY ? ROWS * 2 : 0;

        /// <summary>The fall: every piece that moves, with the cells it passes through (path[0] = start, last = end). Shared by
        /// the data (ApplyGravityInData) and the visual (GridManager.ApplyGravity) so both land every piece on the same cell and
        /// the visual can draw the real route. Pure: reads only the two grids + the void mask.
        /// 1) each column segment between fixed cells packs down (voids are skipped, as before); 2) a hole that nothing can fall
        /// into - a fixed cell above it - takes the piece diagonally above it (left or right, alternating by cell); repeat.
        /// <paramref name="spawnRows"/> > 0: that many NEW pieces wait above every column and fall in with the rest, so the
        /// refill also flows diagonally into holes under crates (RM L8 lid: pieces pour through the gap and step sideways one by
        /// one). Moves with from.y >= the board height are those new pieces.</summary>
        public static List<FallMove> PlanFall(bool[,] occ, bool[,] fix, int spawnRows = 0)
        {
            int W = occ.GetLength(0), H = occ.GetLength(1), HT = H + Mathf.Max(0, spawnRows);
            var org = new Vector2Int?[W, HT];   // where the piece now in each cell started
            var path = new List<Vector2Int>[W, HT]; var phs = new List<int>[W, HT]; int ph = 0;   // waves: 0 = first fall, then slide / fall / slide ...
            for (int x = 0; x < W; x++) for (int y = 0; y < HT; y++)
                if (y >= H || occ[x, y]) { org[x, y] = new Vector2Int(x, y); path[x, y] = new List<Vector2Int> { new Vector2Int(x, y) }; phs[x, y] = new List<int>(); }
            bool Fixed(int x, int y) => y < H && org[x, y].HasValue && org[x, y].Value.y < H && fix[org[x, y].Value.x, org[x, y].Value.y];   // new pieces are never fixed
            bool Free(int x, int y) => y < H && !org[x, y].HasValue && !IsVoid(x, y);
            void Move(int fx, int fy, int tx, int ty)
            {
                org[tx, ty] = org[fx, fy]; path[tx, ty] = path[fx, fy]; path[tx, ty].Add(new Vector2Int(tx, ty)); phs[tx, ty] = phs[fx, fy]; phs[tx, ty].Add(ph);
                org[fx, fy] = null; path[fx, fy] = null; phs[fx, fy] = null;
            }
            void Pack()
            {
                for (int x = 0; x < W; x++)
                {
                    int y0 = 0;
                    while (y0 < HT)
                    {
                        int y1 = y0; while (y1 < HT && !Fixed(x, y1)) y1++;   // segment [y0, y1)
                        int w = y0;
                        for (int y = y0; y < y1; y++)
                        {
                            if (!org[x, y].HasValue) continue;
                            while (w < y && w < H && IsVoid(x, w)) w++;
                            if (w < y) Move(x, y, x, w);
                            w++;
                        }
                        y0 = y1 + 1;
                    }
                }
            }
            // 11-04 (Spencer: tiles "moving like in a chunk formation and sliding altogether at the same time"): a shaft of
            // several empty cells under a crate took a diagonal piece into EVERY cell in one wave - the neighbour column slid
            // over as a block. RM streams: only the cell DIRECTLY under the blocker takes a piece; the next fall wave drops it
            // down the shaft, then the next piece slides in.
            bool UnderFixed(int x, int y)
            {
                for (int j = y + 1; j < HT; j++) { if (j < H && IsVoid(x, j)) continue; return Fixed(x, j); }
                return false;
            }
            bool BlockedAbove(int x, int y)
            {
                for (int j = y + 1; j < HT; j++) { if (j < H && IsVoid(x, j)) continue; if (Fixed(x, j)) return true; if (org[x, j].HasValue) return false; }
                return false;   // open to the top: the refill comes in here
            }
            // 11-17 (Spencer's L19 recording; "what would royal match do?" -> "yes please go ahead"): a shaft whose TOP cell is sealed
            // (crates on both of its upper corners) starved - only the top cell was allowed to take a piece, it never could, so
            // the cells below it stayed empty although a letter sat on their own upper corner. Match-3 rule: any empty cell that
            // cannot be fed from above takes the piece diagonally above it. Still one slide per shaft per wave: the HIGHEST cell in
            // the shaft that has a piece on an upper corner takes it. A cell with nothing on either corner stays empty (RM too).
            bool HasSource(int x, int y)
            {
                int sy = y + 1; if (sy >= HT) return false;
                for (int sx = x - 1; sx <= x + 1; sx += 2) if (sx >= 0 && sx < W && org[sx, sy].HasValue && !Fixed(sx, sy)) return true;
                return false;
            }
            bool NoSourceAbove(int x, int y)
            {
                for (int j = y + 1; j < HT; j++) { if (j < H && IsVoid(x, j)) continue; if (!Free(x, j)) break; if (HasSource(x, j)) return false; }
                return true;
            }
            Pack();
            for (int guard = 0; DIAGONAL_SLIDE && guard < W * HT; guard++)
            {
                bool slid = false; ph++;
                for (int y = 0; y < H - 1; y++)
                    for (int x = 0; x < W; x++)
                    {
                        if (!Free(x, y) || !(SLIDE_ONE_PER_SHAFT ? (UnderFixed(x, y) || (SLIDE_PAST_SEALED && BlockedAbove(x, y) && NoSourceAbove(x, y))) : BlockedAbove(x, y))) continue;
                        int first = ((x + y) & 1) == 0 ? -1 : 1;
                        for (int k = 0; k < 2; k++)
                        {
                            int sx = x + (k == 0 ? first : -first), sy = y + 1;
                            if (sx < 0 || sx >= W || !org[sx, sy].HasValue || Fixed(sx, sy)) continue;
                            Move(sx, sy, x, y); slid = true; break;
                        }
                    }
                if (!slid) break;
                ph++; Pack();
            }
            var plan = new List<FallMove>();
            for (int x = 0; x < W; x++) for (int y = 0; y < H; y++)
                if (org[x, y].HasValue && org[x, y].Value != new Vector2Int(x, y)) plan.Add(new FallMove { from = org[x, y].Value, to = new Vector2Int(x, y), path = path[x, y], phase = phs[x, y] });
            return plan;
        }

        public static bool FULL_REFILL_IN_GRAVITY = true;
        // ── FALL SEEDS = WHAT MOVED (2026-10-22 Spencer: a word "all the way at the bottom of the board and no cascading even
        // affected it ... nested at the start of the level but it auto fired"). After a fall the scan used to seed EVERY cell of
        // every column that moved, so a word already lying still on the board (WORE in the bottom row) "formed" the moment
        // anything fell in one of its columns. Match-3 rule: a new match must include a piece that MOVED. Seeds are now the
        // landed cells + the freshly refilled cells; still words stay put for the player to find and swipe. Full board only. ──
        public static bool FALL_SEEDS_MOVED_ONLY = true;
        // ── CASCADE CAP (2026-10-23, option C — Spencer OK'd). Bot: 41% of all words were free fall words, and games swung
        // from 2 to 50 moves on identical settings (fast games 2.1 free words/move, slow 0.9) — luck, not play, decided the level,
        // which kills near-miss tension (and the Play On offer with it). Rule: after a move, at most CASCADE_FREE_PER_MOVE fall
        // words pop on their own — UNLESS a power-up fired that move (earned chaos stays). Words over the cap stay on the board,
        // not auto-firing, for the player to find and swipe. false = old behaviour. ──
        public static bool CASCADE_CAP_ON = true;
        public static int  CASCADE_FREE_PER_MOVE = 1;
        public static bool CASCADE_LIFT_AFTER_SPECIAL = false;   // 2026-10-23: false = the cap holds even after a special fired
        public static bool CASCADE_HOLD_SPECIAL_WORDS = true;    // 2026-10-23: a falling word through a power-up is held, never fires it
        public static int  CascadeFreeThisMove, CascadeWordsHeld;
        public static bool SpecialFiredThisMove;
        /// <summary>Call when the PLAYER commits a move (swap / swipe / bot swap): resets the per-move cascade budget.</summary>
        public static void BeginPlayerMove() { CascadeFreeThisMove = 0; SpecialFiredThisMove = false; AirplanePending = false; if (!Simulating) PlaythroughStats.MoveCommitted(); }
        private static bool FullBoardActive => SurvivalManager.FullBoardActive;
        public static readonly List<Vector2Int> LastRefillCells = new List<Vector2Int>();
        private void AddFallSeeds(Dictionary<Vector2Int, Vector2Int> gravityMoves, List<Vector2Int> seeds)
        {
            var set = new HashSet<Vector2Int>(seeds);
            if (gravityMoves != null) foreach (var kv in gravityMoves) if (InBounds(kv.Value.x, kv.Value.y) && _board[kv.Value.x, kv.Value.y] != null && set.Add(kv.Value)) seeds.Add(kv.Value);
            foreach (var c in LastRefillCells) if (InBounds(c.x, c.y) && _board[c.x, c.y] != null && set.Add(c)) seeds.Add(c);
        }
        public static int  FULL_REFILL_SMART      = 2;    // smart (one swap from a common word) letters per refill
        public static int  RefillGoldPending;             // a big play's reward: this many of the next refill's smart letters land gold

        /// <summary>Full board: top every column up to the ceiling (above the highest tile — holes under ice stay) with
        /// letters that spell nothing on their own, plus a few smart letters. Data only. Returns letters added.</summary>
        public int FillTopsInData(List<Vector2Int> only = null)
        {
            SnapshotForRain(out var g, out var mv);
            var cells = new List<Vector2Int>();
            if (only != null) { foreach (var p in only) if (InBounds(p.x, p.y) && _board[p.x, p.y] == null) cells.Add(p); }
            else
            for (int c = 0; c < COLS; c++)
            {
                int top = 0;
                for (int r = ROWS - 1; r >= 0; r--) if (g[c, r] != '\0') { top = r + 1; break; }
                for (int r = top; r < ROWS; r++) if (!IsVoid(c, r)) cells.Add(new Vector2Int(c, r));   // shaped board: never into a void
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
            // GOAL-AIMED (2026-10-23): with few goal pieces left, the smart letters go to the columns holding them first (they
            // land nearest the pieces). Organic — only letters that are falling in anyway. Weak when a piece sits low on the board.
            int smartWant = FULL_REFILL_SMART;
            if (ENDGAME_REFILL_AIM && FullBoardActive && GoalPiecesLeft() is int gpl && gpl > 0 && gpl <= ENDGAME_PIECES)
            {
                var goalCols = new HashSet<int>();
                for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                { var d = _board[x, y]; if (d != null && ((d.HasGem && LevelRules.HasGemGoal) || d.IsFrozen || (d.IsStone && LevelRules.HasCrateGoal) || d.IsDropTarget)) { goalCols.Add(x); if (x > 0) goalCols.Add(x - 1); if (x < COLS - 1) goalCols.Add(x + 1); } }
                order.Sort((a, b) => (goalCols.Contains(a.x) ? 0 : 1).CompareTo(goalCols.Contains(b.x) ? 0 : 1));
                smartWant = FULL_REFILL_SMART + 1;
            }
            try
            {
                int tried = 0;
                foreach (var p in order)
                {
                    if (smart >= Mathf.Max(smartWant, wantGold) || tried++ >= 10) break;   // cap the search (phones)
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

            // GEMS keep flowing while the gem goal still needs them: top the board back up to GEM_MIN_ON_BOARD (or what's left).
            var gemCells = new HashSet<Vector2Int>();
            int gemsBefore = CountGems();
            int gemNeed = GEMS_FIXED ? 0 : LevelRules.GemGoalRemainingOnBoardNeed(gemsBefore);
            // HARD CAP (2026-10-23 Spencer, twice: "a bunch of letters just turned into gems" — a whole refill row turned gem).
            // Whatever the goal math says, a single refill adds at most GEM_MAX_PER_REFILL gems. TEMP DIAG logs the inputs.
            if (gemNeed > GEM_MAX_PER_REFILL) { WDLog.Log($"[Gems] refill wanted {gemNeed} gems (on board {gemsBefore}) — capped to {GEM_MAX_PER_REFILL}"); gemNeed = GEM_MAX_PER_REFILL; }
            if (gemNeed > 0 && !Simulating) WDLog.Log($"[Gems] refill: on board {gemsBefore}, adding up to {gemNeed}");
            if (gemNeed > 0)
            {
                var pool = new List<Vector2Int>();
                foreach (var p in cells) if (!gold.Contains(p) && GEM_LETTERS.IndexOf(letters[p]) >= 0) pool.Add(p);
                for (int i = pool.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = pool[i]; pool[i] = pool[j]; pool[j] = t; }
                for (int i = 0; i < pool.Count && gemCells.Count < gemNeed; i++) gemCells.Add(pool[i]);
                if (!Simulating) foreach (var gc in gemCells) WDLog.Log($"[GemBorn] refill puts a gem in the NEW letter '{letters[gc]}' at ({gc.x},{gc.y}) (it falls from there)");
            }
            // DUCKS keep coming while the duck goal needs more: they enter at the TOP of a column, like Candy Crush ingredients.
            var duckCells = new HashSet<Vector2Int>();
            int duckNeed = LevelRules.DuckRefillNeed(CountDucks());
            if (duckNeed > 0)
            {
                var tops = new List<Vector2Int>();
                foreach (var p in cells) if (p.y == ROWS - 1 && p.x > 0 && p.x < COLS - 1 && !gold.Contains(p) && !gemCells.Contains(p)) tops.Add(p);
                for (int i = tops.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = tops[i]; tops[i] = tops[j]; tops[j] = t; }
                for (int i = 0; i < tops.Count && duckCells.Count < duckNeed; i++) duckCells.Add(tops[i]);
            }
            foreach (var p in cells)
            {
                var nd = new RulesCellData { Letter = letters[p], Col = p.x, Row = p.y, PlayerIndex = -1, IsRainGold = gold.Contains(p) || (LevelRules.BonusLevelActive && LevelRules.GoldBoardOn), HasGem = gemCells.Contains(p) };   // Gold Rush: every letter gold, refills too
                if (duckCells.Contains(p)) { MakeDuck(nd); WDLog.Log($"[Duck] refill duck enters at ({p.x},{p.y})"); }
                _board[p.x, p.y] = nd;
                LastRefillCells.Add(p);   // new letters are "moved" pieces for the next word scan
            }
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
                                chars[step] = ScanChar(cell);   // 11-03: a power-up reads as the wild
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

                            string candidate = ResolveWildPattern(new string(chars), TouchesSwap(cells));   // 11-03 wild power-ups
                            if (candidate == null) continue;
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
        // ── SWAP HINT (2026-10-22 Spencer: "a hint system that helps players if idle for a certain amount of time"). Tries every
        // legal touching swap on the LIVE board by trading the two cells' letters in place, asks FindNewWords what that spells
        // (same full-board rules as a real swap: 4+ letters, theme words, ShortWordCells), scores it, and puts the letters
        // back (finally). Score = word length + specials it would make + goal progress (crates beside, gems/ice/collect
        // letters in it, theme words, clearing under a duck), so the hint points at something USEFUL, not just any word. ──
        public bool FindBestSwapHint(out Vector2Int a, out Vector2Int b, out List<Vector2Int> wordCells) => FindSwapHint(true, out a, out b, out wordCells);

        /// <summary>best = true: the strongest move (Spyglass booster). best = false: the FREE idle hint (2026-10-22 Spencer: "the
        /// hint system makes it incredibly easy") — a RANDOM swap among those making the SHORTEST available word, with no credit
        /// for power-ups or goals. It says "a word is here", not "this wins the level" — Royal Match's idle hint is also just
        /// some valid move.</summary>
        public static bool LONG_HINT_ON = true; public static int LONG_HINT_MIN = 5;
        public bool FindSwapHint(bool best, out Vector2Int a, out Vector2Int b, out List<Vector2Int> wordCells)
        {
            a = b = new Vector2Int(-1, -1); wordCells = null;
            int bestScore = 0;
            int plainLen = int.MaxValue; var plain = new List<(Vector2Int, Vector2Int, List<Vector2Int>)>();
            int longLen = 0; var longest = new List<(Vector2Int, Vector2Int, List<Vector2Int>)>();   // stranded-goal route (10-30)
            var prevShort = ShortWordCells;
            char collect = LevelRules.ActiveCollectLetter;
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            try
            {
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS; y++)
                    {
                        var ca = _board[x, y];
                        if (!HintMovable(ca)) continue;
                        foreach (var d in dirs)
                        {
                            int x2 = x + d.x, y2 = y + d.y;
                            if (x2 >= COLS || y2 >= ROWS) continue;
                            var cb = _board[x2, y2];
                            if (!HintMovable(cb) || char.ToUpperInvariant(ca.Letter) == char.ToUpperInvariant(cb.Letter)) continue;
                            {
                                var found = WordsIfSwapped(new Vector2Int(x, y), new Vector2Int(x2, y2));   // same judge as the real swap
                                if (found.Count == 0) continue;
                                int score = 0; RulesWordMatch top = null; int topLen = 0;
                                foreach (var m in found)
                                {
                                    if (m == null || m.Cells == null || string.IsNullOrEmpty(m.Word)) continue;
                                    score += HintWordScore(m, collect);
                                    if (m.Word.Length > topLen) { topLen = m.Word.Length; top = m; }
                                }
                                score += SurvivalRng.Range(0, 3);   // tie-break so the same board doesn't always show the same hint
                                if (!best && top != null)
                                {
                                    // GOAL-AWARE, COMMON-FIRST (2026-10-23 Spencer: "maybe the hints should apply towards your goals though
                                    // right?"; earlier: the free hint offered MISE / GAGE / TARO). Class 0 = common word that hits a goal
                                    // (ice / gem / crate beside / collect letter / theme / under a duck), 1 = common word, 2 = obscure + goal,
                                    // 3 = obscure. Within the best class: the SHORTEST, picked at random — it points the way, it still
                                    // doesn't hunt power-ups or solve the level (10-22: "the hint system makes it incredibly easy").
                                    int cls = 9, sl = int.MaxValue; RulesWordMatch shortest = null;
                                    foreach (var m in found)
                                    {
                                        if (m == null || m.Cells == null || string.IsNullOrEmpty(m.Word)) continue;
                                        bool common = IsCommonWord(m.Word.ToUpperInvariant()) || LevelRules.IsThemeWord(m.Word);
                                        int len = m.Word.Length;
                                        bool goal = HintWordScore(m, collect) > len * 10 + (len >= 6 ? 25 : len >= 5 ? 15 : 0);
                                        int c = (common ? 0 : 2) + (goal ? 0 : 1);
                                        if (c < cls || (c == cls && len < sl)) { cls = c; sl = len; shortest = m; }
                                    }
                                    foreach (var m in found)   // longest COMMON word this swap makes (5+ = a special)
                                    {
                                        if (m == null || m.Cells == null || m.Word.Length < LONG_HINT_MIN || !IsCommonWord(m.Word.ToUpperInvariant())) continue;
                                        if (m.Word.Length > longLen) { longLen = m.Word.Length; longest.Clear(); }
                                        if (m.Word.Length == longLen) longest.Add((new Vector2Int(x, y), new Vector2Int(x2, y2), new List<Vector2Int>(m.Cells)));
                                    }
                                    if (shortest != null)
                                    {
                                        int key = cls * 100 + sl;
                                        if (key < plainLen) { plainLen = key; plain.Clear(); }
                                        if (key == plainLen) plain.Add((new Vector2Int(x, y), new Vector2Int(x2, y2), new List<Vector2Int>(shortest.Cells)));
                                    }
                                    continue;
                                }
                                if (top != null && score > bestScore)
                                {
                                    bestScore = score; a = new Vector2Int(x, y); b = new Vector2Int(x2, y2);
                                    wordCells = new List<Vector2Int>(top.Cells);
                                }
                            }
                        }
                    }
            }
            finally { ShortWordCells = prevShort; }
            // STRANDED GOAL (10-30 Spencer: the last crates went unreachable and the game "was giving you the level"): no common
            // word touches a goal (best class isn't 0) -> point at a LONG word anywhere instead. It makes a special, and with the goal
            // stranded that special becomes an AIRPLANE that flies to a goal (GoalDeadAtSwap). The player still makes the word.
            if (!best && LONG_HINT_ON && longest.Count > 0 && plain.Count > 0 && plainLen >= 100 && SurvivalManager.FullBoardActive && GoalPiecesLeft() > 0)
            {
                var pick = longest[SurvivalRng.Range(0, longest.Count)];
                a = pick.Item1; b = pick.Item2; wordCells = pick.Item3;
                if (!Simulating) WDLog.Log($"[Hint] goal stranded -> long word ({longLen} letters) for an airplane");
                return true;
            }
            if (!best && plain.Count > 0)
            {
                var pick = plain[SurvivalRng.Range(0, plain.Count)];
                a = pick.Item1; b = pick.Item2; wordCells = pick.Item3;
            }
            return wordCells != null;
        }
        /// <summary>SWIPE HINT (2026-10-23 Spencer: "maybe i didnt drag to make words cause they werent hinted at much" -> "ok sure").
        /// A word already lying on the board that a swipe can claim right now (held cascade words, words the fall left behind):
        /// straight line, 4+ letters, either direction (BuildExplicitMatch validates exactly like a real swipe), COMMON words only.
        /// Prefers words that make goal progress, then shorter (easier to read). False = nothing to swipe.</summary>
        /// <summary>BOT DRAG (10-31): every straight word already on the board a player could drag (4+ letters, horizontal or vertical,
        /// common/theme when commonOnly, not already claimed), scored like EnumerateWordSwaps.</summary>
        public List<(RulesWordMatch m, int score)> EnumerateSwipeWords(bool commonOnly)
        {
            var list = new List<(RulesWordMatch, int)>(); char collect = LevelRules.ActiveCollectLetter;
            int min = Mathf.Max(4, SWIPE_MIN_LENGTH), max = Mathf.Min(7, Mathf.Max(COLS, ROWS));
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            foreach (var d in dirs)
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS; y++)
                        for (int len = min; len <= max; len++)
                        {
                            int ex = x + d.x * (len - 1), ey = y + d.y * (len - 1);
                            if (ex >= COLS || ey >= ROWS) break;
                            var path = new List<Vector2Int>(len);
                            for (int i = 0; i < len; i++) path.Add(new Vector2Int(x + d.x * i, y + d.y * i));
                            var m = BuildExplicitMatch(path, out _);
                            if (m == null || string.IsNullOrEmpty(m.Word)) continue;
                            string w = m.Word.ToUpperInvariant();
                            if (commonOnly && !IsCommonWord(w) && !LevelRules.IsThemeWord(w)) continue;
                            if (IsClaimBlocked(m.Word + "|" + m.CellKey)) continue;
                            int sc = HintWordScore(m, collect); if (sc > 0) list.Add((m, sc));
                        }
            return list;
        }

        public bool FindSwipeHint(out List<Vector2Int> cells, out string word)
        {
            cells = null; word = null;
            int best = int.MinValue; int min = Mathf.Max(4, SWIPE_MIN_LENGTH), max = Mathf.Min(7, Mathf.Max(COLS, ROWS));
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            foreach (var d in dirs)
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS; y++)
                        for (int len = min; len <= max; len++)
                        {
                            int ex = x + d.x * (len - 1), ey = y + d.y * (len - 1);
                            if (ex >= COLS || ey >= ROWS) break;
                            var path = new List<Vector2Int>(len);
                            for (int i = 0; i < len; i++) path.Add(new Vector2Int(x + d.x * i, y + d.y * i));
                            var m = BuildExplicitMatch(path, out _);
                            if (m == null || string.IsNullOrEmpty(m.Word)) continue;
                            string w = m.Word.ToUpperInvariant();
                            if (!IsCommonWord(w) && !LevelRules.IsThemeWord(w)) continue;
                            int score = (TouchesGoal(m) ? 100 : 0) - len * 3 + SurvivalRng.Range(0, 4);
                            if (score > best) { best = score; cells = new List<Vector2Int>(m.Cells); word = w; }
                        }
            return cells != null;
        }

        /// <summary>TUTORIAL SWAP (2026-10-23 Spencer, L1 lesson: "i also dont understand what word it is trying to spell" — the
        /// best-hint picked ERASES read right-to-left). The FIRST word a player is shown must be obvious: exactly ONE word, read
        /// FORWARDS (left->right / top->bottom), 4-5 letters, on the common-word list; preferring a swap that slides ALONG the
        /// word's line (the lit window is then one clean strip) and a word through a gem / goal piece. Each rule relaxes in turn
        /// if the board has nothing that strict. word = the word the swap spells, for the bubble.</summary>
        public bool FindTutorialSwap(out Vector2Int a, out Vector2Int b, out List<Vector2Int> wordCells, out string word, bool requireGoal = false)
        {
            a = b = new Vector2Int(-1, -1); wordCells = null; word = null;
            char collect = LevelRules.ActiveCollectLetter;
            for (int pass = 0; pass < 3; pass++)   // 0 strict, 1 allow any length 4-6 / uncommon, 2 allow extra words / backwards
            {
                int bestScore = int.MinValue;
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS; y++)
                    {
                        if (!HintMovable(_board[x, y])) continue;
                        foreach (var d in new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) })
                        {
                            var pa = new Vector2Int(x, y); var pb = new Vector2Int(x + d.x, y + d.y);
                            if (pb.x >= COLS || pb.y >= ROWS || !HintMovable(_board[pb.x, pb.y])) continue;
                            if (char.ToUpperInvariant(_board[x, y].Letter) == char.ToUpperInvariant(_board[pb.x, pb.y].Letter)) continue;
                            var found = WordsIfSwapped(pa, pb);
                            if (found.Count == 0 || (pass < 2 && found.Count != 1)) continue;
                            RulesWordMatch m = found[0];
                            foreach (var f in found) if (f.Word.Length > m.Word.Length) m = f;
                            if (m.Cells == null || m.Cells.Count < 2) continue;
                            if (requireGoal && !TouchesGoal(m)) continue;   // 11-01: the crate / ice lesson needs a word that actually hits the piece
                            bool forwards = m.Direction == WordDirection.Horizontal ? m.Cells[1].x > m.Cells[0].x : m.Cells[1].y < m.Cells[0].y;
                            int len = m.Word.Length;
                            if (pass < 2 && !forwards) continue;
                            if (pass == 0 && (len > 5 || !IsCommonWord(m.Word.ToUpperInvariant()))) continue;
                            if (pass == 1 && len > 6) continue;
                            bool inLine = m.Cells.Contains(pa) && m.Cells.Contains(pb);
                            // 11-06 Spencer (L6 crate lesson showed DOZE via an in-word swap - "not really clear"): for a goal LESSON
                            // prefer a letter sliding in from BESIDE the word (the rest of the word already reads in place, like the
                            // planted teach word); swapping two letters inside the word is hard to spot. Other callers keep in-line.
                            int score = ((requireGoal ? !inLine : inLine) ? 30 : 0) + (forwards ? 20 : 0) + (len == 4 ? 10 : 0) + SurvivalRng.Range(0, 5);
                            foreach (var c in m.Cells)
                            {
                                var cd = _board[c.x, c.y]; if (cd == null) continue;
                                if (cd.HasGem || (pa == c && _board[pb.x, pb.y].HasGem) || (pb == c && _board[pa.x, pa.y].HasGem)) score += 15;
                                if (collect != '\0' && char.ToUpperInvariant(cd.Letter) == char.ToUpperInvariant(collect)) score += 5;
                            }
                            if (score > bestScore)
                            {
                                bestScore = score; a = pa; b = pb; wordCells = new List<Vector2Int>(m.Cells); word = m.Word.ToUpperInvariant();
                            }
                        }
                    }
                if (wordCells != null) { WDLog.Log($"[Tut] tutorial swap pass {pass}: '{word}' ({a.x},{a.y})<->({b.x},{b.y})"); return true; }
            }
            return false;
        }
        /// <summary>The words swapping a and b would ARM, judged exactly as the real swap judges them (2026-10-23 Spencer: "every
        /// hint ive done ... didnt make a word"): seed scan of the two cells, words must contain one of them, substring words
        /// dropped (BoardSwapTurnSequence -> ScanSeedCellsPublic -> FilterSubstringWords). Letters swapped in place and
        /// restored; no wild resolution (the full board has none). Used by the hint, its re-check, and the bot.</summary>
        public List<RulesWordMatch> WordsIfSwapped(Vector2Int a, Vector2Int b)
        {
            var res = new List<RulesWordMatch>();
            if (!InBounds(a.x, a.y) || !InBounds(b.x, b.y)) return res;
            var ca = _board[a.x, a.y]; var cb = _board[b.x, b.y];
            if (ca == null || cb == null) return res;
            var prevShort = ShortWordCells;
            char la = ca.Letter, lb = cb.Letter;
            // SAME DATA AS THE REAL SWAP (2026-10-30: a gem swap and a bomb swap passed this check, then the real scan found no word
            // and the move was charged). HandManager.TryBoardSwap moves the special / gold / gem flags WITH the letters and the real
            // scan drops claim-blocked words - do exactly that here, so the judge and the swap can't disagree.
            int ka = ca.SpecialKind, kb = cb.SpecialKind; bool ga = ca.IsRainGold, gb = cb.IsRainGold, ha = ca.HasGem, hb = cb.HasGem;
            ca.Letter = lb; cb.Letter = la;
            ca.SpecialKind = kb; cb.SpecialKind = ka; ca.IsRainGold = gb; cb.IsRainGold = ga; ca.HasGem = hb; cb.HasGem = ha;
            ShortWordCells = new[] { a, b };
            try
            {
                var seen = new HashSet<string>();
                foreach (var sc in new[] { a, b })
                    foreach (var w in FindNewWords(sc.x, sc.y))
                    {
                        if (w == null || w.Cells == null || string.IsNullOrEmpty(w.Word)) continue;
                        if (!w.Cells.Contains(a) && !w.Cells.Contains(b)) continue;
                        string key = w.Word + "|" + w.CellKey;
                        if (IsClaimBlocked(key)) continue;
                        if (seen.Add(key)) res.Add(w);
                    }
                res = FilterSubstringWords(res);
            }
            finally
            {
                ca.Letter = la; cb.Letter = lb; ShortWordCells = prevShort;
                ca.SpecialKind = ka; cb.SpecialKind = kb; ca.IsRainGold = ga; cb.IsRainGold = gb; ca.HasGem = ha; cb.HasGem = hb;
            }
            return res;
        }

        /// <summary>Every legal touching swap that makes a word, with the hint's usefulness score (length + power-ups + goal
        /// progress). The bot playtester picks from these. Same in-place try-and-restore as FindSwapHint.</summary>
        // ── STALE-BOARD RESCUE (2026-10-23 Spencer: "when you have a stale board and nothing looks available thats when i feel the
        // fun trail off"; his run: 54% of swaps made no word). Once per move, when the board has fewer than STALE_MIN swaps that
        // make a COMMON word, one plain tile changes letter so a common word is one swap away. Research: <3 common moves = dead.
        // Bot does the same each turn, so its numbers measure the game as shipped. false = off. ──
        public static bool STALE_RESCUE_ON = false;   // 2026-10-23 Spencer: "it just giving you the letter might not be the way" — a tile visibly changing reads as cheating. Off; see ENDGAME_AIRPLANE / goal-aimed refill / booster nudge
        public static int  STALE_MIN = 3;
        public static int  STALE_RESCUE_CELLS = 18;   // cells tried per rescue (cost cap: each tries ~18 letters x 4 swaps)
        private static readonly char[] RESCUE_LETTERS = "EARSTIONLDUCMPHGBYW".ToCharArray();
        public int CountCommonWordSwaps() => EnumerateWordSwaps(true).Count;

        // ── BOARD SHUFFLE (2026-10-30, paired with HandManager.BOUNCE_NO_WORD): when no swap makes a common word, the PLAIN letters
        // are re-dealt among their own cells - openly, with a "No words!" card (BoardShuffle), the match-3 convention. The silent
        // one-tile letter change above (STALE_RESCUE) stays off: Spencer said a tile changing on its own reads as cheating.
        // Only letters move: crates, ice, gems, gold, specials, wilds, refills, armed words and every cell's own state stay put.
        public static int SHUFFLE_TRIES = 60;
        private bool ShuffleMovable(RulesCellData c, Vector2Int p) =>
            HintMovable(c) && !c.HasGem && (!c.IsRainGold || LevelRules.BonusLevelActive) && !c.IsSwapRefill && !c.IsEditRefill && !c.IsWildRefill && !c.IsCyan
            && (PrimedRegistry == null || PrimedRegistry.GetPrimedWordsContaining(p).Count == 0);
        /// <summary>Re-deals the plain letters so at least <paramref name="minCommon"/> swaps make a common word and no word is
        /// already sitting on the board (instant pop would fire it for free). Tries SHUFFLE_TRIES deals and keeps the best.
        /// Applies it and returns the letter moves (from cell -> to cell) for the animation; null = no plain letters / nothing
        /// better than the board as it is.</summary>
        public List<(Vector2Int from, Vector2Int to)> ShuffleForWords(int minCommon, System.Random rng)
        {
            var cells = new List<Vector2Int>();
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                { var c = _board[x, y]; if (c != null && ShuffleMovable(c, new Vector2Int(x, y))) cells.Add(new Vector2Int(x, y)); }
            if (cells.Count < 4) return null;
            var orig = new char[cells.Count];
            for (int i = 0; i < cells.Count; i++) orig[i] = _board[cells[i].x, cells[i].y].Letter;
            int[] best = null; int bestN = 0;
            var perm = new int[cells.Count];
            for (int t = 0; t < SHUFFLE_TRIES; t++)
            {
                for (int i = 0; i < perm.Length; i++) perm[i] = i;
                for (int i = perm.Length - 1; i > 0; i--) { int j = rng.Next(i + 1); int k = perm[i]; perm[i] = perm[j]; perm[j] = k; }
                for (int i = 0; i < cells.Count; i++) _board[cells[i].x, cells[i].y].Letter = orig[perm[i]];   // cell i gets the letter of cell perm[i]
                bool clean = true;
                foreach (var p in cells) if (CellHasUnscoredWord(p.x, p.y)) { clean = false; break; }
                int n = clean ? CountCommonWordSwaps() : -1;
                if (n > bestN) { bestN = n; best = (int[])perm.Clone(); }
                for (int i = 0; i < cells.Count; i++) _board[cells[i].x, cells[i].y].Letter = orig[i];
                if (bestN >= minCommon) break;
            }
            if (best == null) return null;
            var moves = new List<(Vector2Int, Vector2Int)>();
            for (int i = 0; i < cells.Count; i++)
            {
                _board[cells[i].x, cells[i].y].Letter = orig[best[i]];
                if (best[i] != i) moves.Add((cells[best[i]], cells[i]));
            }
            if (!Simulating) UnityEngine.Debug.Log($"[Shuffle] re-dealt {cells.Count} letters, {moves.Count} moved -> {bestN} common-word swaps");
            return moves;
        }
        public static bool ENDGAME_RESCUE_ON = false;  // off with STALE_RESCUE_ON (visible letter change) — 2026-10-23
        public static int  ENDGAME_PIECES = 3;
        public static bool ENDGAME_AIRPLANE = true;
        public static bool ENDGAME_REFILL_AIM = true;   // refill smart letters prefer the goal pieces' columns once few remain
        /// <summary>Goal pieces still to collect across every goal (0 = none / score level).</summary>
        public static int GoalPiecesLeft()
        {
            int n = 0;
            foreach (var g in LevelRules.Goals) n += Mathf.Max(0, g.Target - LevelRules.GoalProgress(g));
            return n;
        }
        /// <summary>Does this word make goal progress (ice / gem / crate beside / collect / theme / under a duck)?</summary>
        /// <summary>Word pop-up colour (10-31): does the armed word on these cells make goal progress (crate beside, ice, gem,
        /// collect letter, theme, duck)? Same test the hint uses.</summary>
        public bool WordCellsHitGoal(List<Vector2Int> cells)
        {
            if (cells == null || cells.Count < 2) return false;
            var m = BuildExplicitMatch(cells, out _);
            return m != null && !string.IsNullOrEmpty(m.Word) && TouchesGoal(m);
        }
        private bool TouchesGoal(RulesWordMatch m)
        {
            int len = m.Word.Length;
            return HintWordScore(m, LevelRules.ActiveCollectLetter) > len * 10 + (len >= 6 ? 25 : len >= 5 ? 15 : 0);
        }
        private bool AnyCommonGoalSwap()
        {
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
            {
                if (!HintMovable(_board[x, y])) continue;
                foreach (var d in dirs)
                {
                    var b = new Vector2Int(x + d.x, y + d.y);
                    if (!InBounds(b.x, b.y) || !HintMovable(_board[b.x, b.y])) continue;
                    foreach (var m in WordsIfSwapped(new Vector2Int(x, y), b))
                        if ((IsCommonWord(m.Word.ToUpperInvariant()) || LevelRules.IsThemeWord(m.Word)) && TouchesGoal(m)) return true;
                }
            }
            return false;
        }
        /// <summary>Cells within 2 of a remaining goal piece (gem, ice, crate, duck column) — where an endgame re-letter helps.</summary>
        private HashSet<Vector2Int> GoalNeighbourhood()
        {
            var set = new HashSet<Vector2Int>();
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
            {
                var d = _board[x, y]; if (d == null) continue;
                bool piece = (d.HasGem && LevelRules.HasGemGoal) || d.IsFrozen || (d.IsStone && d.TreasureHits == 0 && LevelRules.HasCrateGoal) || d.IsDropTarget;
                if (!piece) continue;
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) set.Add(new Vector2Int(x + dx, y + dy));
            }
            return set;
        }

        // ── GOAL-AREA REFRESH (2026-10-30, Spencer: three stale boards in a row, "the whole side of the board is very stale").
        // Letters only refresh where words POP, and nothing pops around the last goals - that's why they're last - so the goal area
        // never gets fresh letters. When goal pieces remain and NO swap makes a common word that touches one, the plain letters
        // within 2 cells of the goals are re-dealt from the thermostat (vowel-balanced, hard letters capped) until one does.
        // OPEN, not silent: BoardShuffle shows it ("Fresh letters!") - the silent one-tile change stays off. ──
        // 10-30 OFF (Spencer: "if it shuffles the letters and it doesnt actually help you get a word then what is the point?" - it
        // made a goal word POSSIBLE but hidden; he missed ~14 more swaps after it). The HINT now does this job: it SHOWS a word.
        // 10-30 BACK ON (bot 16:30 without it: casual win L10 100->63, L11 93->53, L19 78->38, goal area dead 17-66% of turns).
        // Now struggle-gated (BoardShuffle), minimal (2-6 of the area's own letters hop), no text, and the hint then SHOWS the word.
        // 11-02 OFF (Spencer: "it feels like the game is giving you the win" / "does royal match do this?" - it doesn't). Gems now
        // collect by touch like crates and ice (GEM_ADJACENT_COLLECT), which is what made endings dead. Confirm with a bot run.
        public static bool GOAL_REFRESH_ON = false;
        public static bool ICE_ADJACENT_THAW = false;   // 11-04 OFF (Spencer: "ice do have letters though"): ice is a letter piece like a gem - a word must run THROUGH it (the briefing already says so)
        public static bool GEM_ADJACENT_COLLECT = false;  // 11-02: a word collects the gems beside it, not only the ones it runs through. 11-04 OFF (Spencer: "if you are trying to get gems you need to make words with those letters")
        /// <summary>11-02 (Spencer: "when a rocket fires it doesnt just blow up its row, it blows up the one next to it"). The
        /// beside-a-word pass (crates / ice / gems next to a cleared cell) was seeded from EVERY exploded cell, special blasts
        /// included - so a rocket also collected / broke every goal piece along BOTH sides of its line (log: rocket at row 2 ->
        /// "gem beside a word" at (8,1),(0,1)). Genre rule (RM/RK): a special hits only its own area; "beside" is for matches.
        /// false = blast cells no longer seed that pass (a word's own letters still do). true = the old behaviour.</summary>
        public static bool SPECIAL_BLAST_HITS_NEIGHBOURS = false;
        public static int  LONG_WORD_DIAGONAL = 0;   // 0 = off. 11-04 OFF (was 5): strict rules - a word hits only the 4 sides of its letters
        // 11-02 CRATE_DIAGONAL (Spencer: "whatever you think is right"): EVERY word breaks the crates it touches diagonally too
        // (8 sides, not 4). Bot 11:35: crate levels had no crate-touching word on 35-52% of turns and spent ~6.7 turns stuck on
        // the last 3 crates (corners / bottom rows). Crates only - ice and gems keep the 4-side rule. Splash/hint text says TOUCHING.
        // 11-04 OFF (Spencer: "taking out any adjacent row blasts that make things easier" / "the player knows what to expect"):
        // 4 sides only, the RM/RK rule. Crates / shelves / safes still break by TOUCH - they can't be spelled through.
        public static bool CRATE_DIAGONAL = false;
        /// <summary>11-04 STRICT RULES: in full-board levels the old survival extras are off - the junk splash (a 4+ word cleared
        /// 1-4 random tiles beside it, which collected gems and fired specials) and the 7-letter row clear. A word clears its own
        /// letters; a special clears its own area. false = the old behaviour.</summary>
        public static bool STRICT_FULL_BOARD = true;
        /// <summary>Set by the swap (HandManager.TryBoardSwap / the bot) BEFORE it commits: no common word reached a goal. A special
        /// the swap earns then becomes an AIRPLANE (flies to a goal) - the skill route out of a stranded goal (10-30).</summary>
        public static bool GoalDeadAtSwap;
        public static int  GOAL_REFRESH_TRIES = 30;
        /// <summary>Goals remain and no common word made by one swap touches any of them.</summary>
        public bool GoalAreaDead() => SurvivalManager.FullBoardActive && GoalPiecesLeft() > 0 && !AnyCommonGoalSwap();
        /// <summary>Re-deals the plain letters around the remaining goals until a common goal word is one swap away (and no word
        /// is standing ready on its own). Returns the changed cells (data already written), or null if nothing could be fixed.</summary>
        // GOAL-AREA SHUFFLE (2026-10-30, Spencer on "Fresh letters!": "i dont understand the fresh letters thing" - it re-dealt 19
        // letters at once - then "i would do a shuffle"). Rearrange as FEW of the goal area's own letters as possible (a cycle of
        // 2, then 3 ... GOAL_SHUFFLE_MAX tiles) until a common goal word is one swap away and nothing stands ready on its own. No
        // letter appears or disappears - the board visibly moves. Returns (from, to) letter moves, data already written, or null.
        public static int GOAL_SHUFFLE_MAX = 6, GOAL_SHUFFLE_TRIES = 60;
        public List<(Vector2Int from, Vector2Int to)> ShuffleGoalArea(System.Random rng)
        {
            var area = new List<Vector2Int>();
            foreach (var p in GoalNeighbourhood())
                if (InBounds(p.x, p.y) && _board[p.x, p.y] != null && ShuffleMovable(_board[p.x, p.y], p)) area.Add(p);
            if (area.Count < 2) return null;
            for (int k = 2; k <= Mathf.Min(GOAL_SHUFFLE_MAX, area.Count); k++)
                for (int t = 0; t < GOAL_SHUFFLE_TRIES; t++)
                {
                    var pick = new List<Vector2Int>(area);
                    for (int i = pick.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var tmp = pick[i]; pick[i] = pick[j]; pick[j] = tmp; }
                    pick.RemoveRange(k, pick.Count - k);
                    var was = new char[k]; for (int i = 0; i < k; i++) was[i] = _board[pick[i].x, pick[i].y].Letter;
                    bool allMove = true;   // a cycle: cell i takes cell i+1's letter - every picked tile must actually change
                    for (int i = 0; i < k; i++) if (was[i] == was[(i + 1) % k]) { allMove = false; break; }
                    if (!allMove) continue;
                    for (int i = 0; i < k; i++) _board[pick[i].x, pick[i].y].Letter = was[(i + 1) % k];
                    bool clean = true;
                    foreach (var p in pick) if (CellHasUnscoredWord(p.x, p.y)) { clean = false; break; }
                    if (clean && AnyCommonGoalSwap())
                    {
                        var moves = new List<(Vector2Int, Vector2Int)>();
                        for (int i = 0; i < k; i++) moves.Add((pick[(i + 1) % k], pick[i]));
                        if (!Simulating) UnityEngine.Debug.Log($"[GoalShuffle] moved {k} letters near the goals -> a goal word is one swap away");
                        return moves;
                    }
                    for (int i = 0; i < k; i++) _board[pick[i].x, pick[i].y].Letter = was[i];
                }
            return null;
        }

        public List<Vector2Int> RefreshGoalArea()
        {
            var area = new List<Vector2Int>();
            foreach (var p in GoalNeighbourhood())
                if (InBounds(p.x, p.y) && _board[p.x, p.y] != null && ShuffleMovable(_board[p.x, p.y], p)) area.Add(p);
            if (area.Count < 3) return null;
            var full = new List<Vector2Int>(area);
            var orig = new char[area.Count];
            for (int i = 0; i < area.Count; i++) orig[i] = _board[area[i].x, area[i].y].Letter;
            for (int t = 0; t < GOAL_REFRESH_TRIES; t++)
            {
                // 10-30: re-deal a SMALL random subset (2 letters, growing with the tries) - not the whole area (19 letters at once)
                area = new List<Vector2Int>(full);
                for (int i = area.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var tmp = area[i]; area[i] = area[j]; area[j] = tmp; }
                area.RemoveRange(Mathf.Min(area.Count, 2 + t / 5), area.Count - Mathf.Min(area.Count, 2 + t / 5));
                for (int i = 0; i < full.Count; i++) _board[full[i].x, full[i].y].Letter = orig[i];
                SnapshotForRain(out var g, out var _);
                foreach (var p in area) g[p.x, p.y] = '\0';                 // the thermostat sees the area as empty
                area.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
                foreach (var p in area) { char L = DrawNoWordLetter(g, p.x, p.y); _board[p.x, p.y].Letter = L; }
                bool clean = true;
                foreach (var p in area) if (CellHasUnscoredWord(p.x, p.y)) { clean = false; break; }
                if (clean && AnyCommonGoalSwap())
                {
                    var changed = new List<Vector2Int>();
                    for (int i = 0; i < full.Count; i++) if (_board[full[i].x, full[i].y].Letter != orig[i]) changed.Add(full[i]);
                    if (!Simulating) UnityEngine.Debug.Log($"[GoalRefresh] re-dealt {area.Count} letters around the goals (try {t + 1}) -> a goal word is one swap away");
                    return changed;
                }
            }
            for (int i = 0; i < full.Count; i++) _board[full[i].x, full[i].y].Letter = orig[i];   // couldn't fix it: put it back
            if (!Simulating) UnityEngine.Debug.Log($"[GoalRefresh] {GOAL_REFRESH_TRIES} deals found no goal word - left as is");
            return null;
        }

        // ── GIFT WORD (2026-10-30, Spencer: "sprinkle in some already made words ... at the start of a level one of the ice
        // objectives is already a made word"). At level start one common word already stands on the board THROUGH a gem or iced
        // goal letter, or beside a crate - the player can swipe it as an opening move. Standing words don't pop on their own
        // (FALL_SEEDS_MOVED_ONLY), so it waits. ──
        public static bool GIFT_WORD_ON = true;

        // ── TEACH SWAP (2026-11-02, Spencer: "when a tutorial is teaching a mechanic you need to be able to make a word and do what
        // it is asking you to do. you probably will have to embed words"). On a lesson level (swap / crates / ice, first time only)
        // the build plants ONE easy, familiar 4-letter word beside a goal piece with one letter swapped out of place, so a single
        // swap spells it. TutorialBeats shows exactly that swap with the hand. Replaces the gift word on that level. ──
        public static readonly string[] TEACH_WORDS = {
            "STAR","FISH","CAKE","BOOK","TREE","RAIN","SNOW","GOLD","SHIP","BIRD","MOON","DOOR","HAND","LAMP","ROSE","WIND","BOAT",
            "FROG","LION","DUCK","MILK","NEST","RING","TENT","SALT","SEAL","PEAR","LEAF","NOTE","SAND","HOME","ROAD","KITE","BELL",
            "DESK","FARM","GIFT","HILL","KING","LAKE","PARK","SOCK","TOWN","WOLF","COIN","BEAR","DEER","GOAT","SEED","PLUM","CORN",
            "SING","READ","PLAY","WORD","GAME","SAIL","TOAD","LOAF","SOUP","RICE","CAPE","DOLL","DRUM","HORN","NAIL","PEAS","POND" };
        public static List<Vector2Int> TeachCells; public static Vector2Int TeachFrom, TeachTo; public static string TeachWord;
        public bool TeachSwapStillThere()
        {
            if (TeachCells == null || TeachWord == null) return false;
            foreach (var m in WordsIfSwapped(TeachFrom, TeachTo)) if (m.Word.ToUpperInvariant() == TeachWord) return true;
            return false;
        }
        /// <param name="through">11-06 ICE lesson: strict rules melt ice only when the word runs THROUGH the frozen letter
        /// (ICE_ADJACENT_THAW off), so the planted word must contain it - the frozen letter stays put and one OTHER letter is the
        /// one knocked out. Without this the ice lesson planted a word BESIDE the ice that could not melt it.</param>
        /// <param name="throughGem">11-16 (Spencer: "theres no tutorial beat to show you how to make a word with a gem to collect the gem"):
        /// gems are collected only when SPELLED (GEM_ADJACENT_COLLECT off), but the L1 lesson word was planted BESIDE a gem - it
        /// collected nothing. With throughGem the word runs THROUGH one gem letter, same mechanics as the ice lesson.</param>
        public static bool TeachThroughGem;
        // 11-16 (Spencer: split L1 into two beats - "slide to make a word", then "pick up the gems"): the gem word is planted at level build
        // too and parked here; beat 1's word is planted AWAY from it (avoid). Beat 2 uses it if it survived beat 1's cascade.
        public static List<Vector2Int> Teach2Cells; public static Vector2Int Teach2From, Teach2To; public static string Teach2Word;
        public bool Teach2StillThere()
        {
            if (Teach2Cells == null || Teach2Word == null) return false;
            foreach (var c in Teach2Cells) { if (!InBounds(c.x, c.y) || _board[c.x, c.y] == null) return false; }
            bool gemIn = false; foreach (var c in Teach2Cells) if (_board[c.x, c.y].HasGem) gemIn = true;
            if (!gemIn) return false;
            foreach (var m in WordsIfSwapped(Teach2From, Teach2To)) if (m.Word.ToUpperInvariant() == Teach2Word) return true;
            return false;
        }
        public void ParkTeachAsSecond() { Teach2Cells = TeachCells; Teach2From = TeachFrom; Teach2To = TeachTo; Teach2Word = TeachWord; }
        public bool PlantTeachSwap(bool through = false, bool throughGem = false, HashSet<Vector2Int> avoid = null)
        {
            TeachCells = null; TeachWord = null; TeachThroughGem = false;
            if (!SurvivalManager.FullBoardActive) return false;
            var pool = new List<string>();
            foreach (var w in TEACH_WORDS) if (WordDictionary.IsValidWord(w)) pool.Add(w);
            if (pool.Count == 0) return false;
            bool Goal(int x, int y) { if (!InBounds(x, y)) return false; var d = _board[x, y]; return d != null && ((d.IsStone && !d.IsDropTarget) || d.IsFrozen || d.HasGem); }
            bool Plain(Vector2Int c) { if (!InBounds(c.x, c.y)) return false; if (avoid != null && avoid.Contains(c)) return false; var d = _board[c.x, c.y]; return d != null && char.IsLetter(d.Letter) && !d.IsStone && !d.IsFrozen && !d.HasGem && !d.IsWild && !d.IsDropTarget && !d.IsRainGold && d.SpecialKind == SPECIAL_NONE && HintMovable(d); }
            var starts = new List<(Vector2Int p, Vector2Int d)>();
            for (int x = 0; x < COLS; x++) for (int y = 1; y < ROWS - 1; y++) { starts.Add((new Vector2Int(x, y), new Vector2Int(1, 0))); starts.Add((new Vector2Int(x, y), new Vector2Int(0, -1))); }
            for (int i = starts.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = starts[i]; starts[i] = starts[j]; starts[j] = t; }
            // prefer the middle of the board: easier to see, and the bubble never covers it
            starts.Sort((u, v) => (Mathf.Abs(u.p.y - ROWS / 2) + Mathf.Abs(u.p.x - COLS / 2)).CompareTo(Mathf.Abs(v.p.y - ROWS / 2) + Mathf.Abs(v.p.x - COLS / 2)));
            bool GemLetter(Vector2Int c) { if (!InBounds(c.x, c.y)) return false; var d = _board[c.x, c.y]; return d != null && d.HasGem && !d.IsFrozen && char.IsLetter(d.Letter) && !d.IsStone && d.SpecialKind == SPECIAL_NONE; }
            bool FrozenLetter(Vector2Int c) { if (!InBounds(c.x, c.y)) return false; var d = _board[c.x, c.y]; return d != null && d.IsFrozen && char.IsLetter(d.Letter) && !d.IsStone && !d.HasGem && d.SpecialKind == SPECIAL_NONE; }
            List<string> common4 = null;
            foreach (var (p0, dir) in starts)
            {
                var cells = new List<Vector2Int>(); bool ok = true, touches = false; int fz = -1;
                for (int i = 0; i < 4 && ok; i++)
                {
                    var c = p0 + dir * i;
                    if (through && fz < 0 && FrozenLetter(c)) { fz = i; cells.Add(c); continue; }   // the ONE frozen letter the word runs through
                    if (throughGem && fz < 0 && GemLetter(c)) { fz = i; cells.Add(c); continue; }    // 11-16: or the ONE gem letter
                    if (!Plain(c)) { ok = false; break; }
                    if (!through && !throughGem && (Goal(c.x + 1, c.y) || Goal(c.x - 1, c.y) || Goal(c.x, c.y + 1) || Goal(c.x, c.y - 1))) touches = true;
                    cells.Add(c);
                }
                if (through || throughGem) touches = fz >= 0;
                if (!ok || !touches) continue;
                var picks = pool;
                if (through || throughGem)
                {   // words with the frozen letter in the frozen cell's place (easy TEACH words first, then any common 4-letter word)
                    char L = char.ToUpperInvariant(_board[cells[fz].x, cells[fz].y].Letter);
                    picks = pool.FindAll(w => w[fz] == L);
                    if (picks.Count == 0)
                    {
                        if (common4 == null)
                        {
                            IsCommonWord("A"); common4 = new List<string>();
                            foreach (var w in s_commonWords) if (w.Length == 4 && w.IndexOfAny(new[] { 'Q', 'X', 'Z', 'J' }) < 0 && WordDictionary.IsValidWord(w)) common4.Add(w);
                        }
                        picks = common4.FindAll(w => w[fz] == L);
                    }
                    if (picks.Count == 0) continue;
                }
                var perp = dir.x != 0 ? new Vector2Int(0, 1) : new Vector2Int(1, 0);
                for (int tries = 0; tries < 6; tries++)
                {
                    string pick = picks[SurvivalRng.Range(0, picks.Count)];
                    var was = new char[4]; for (int i = 0; i < 4; i++) { was[i] = _board[cells[i].x, cells[i].y].Letter; if (i != fz) _board[cells[i].x, cells[i].y].Letter = pick[i]; }
                    bool done = false;
                    foreach (int i in new[] { 1, 2, 0, 3 })
                    {
                        if (i == fz) continue;   // the frozen letter never moves
                        foreach (var n in new[] { cells[i] + perp, cells[i] - perp })
                        {
                            if (!Plain(n) || char.ToUpperInvariant(_board[n.x, n.y].Letter) == pick[i]) continue;
                            char nl = _board[n.x, n.y].Letter;
                            _board[n.x, n.y].Letter = pick[i]; _board[cells[i].x, cells[i].y].Letter = nl;   // knock one letter out of place
                            bool standing = false;
                            foreach (var c in cells) if (FindNewWords(c.x, c.y).Count > 0) { standing = true; break; }
                            if (!standing && FindNewWords(n.x, n.y).Count > 0) standing = true;
                            bool fixes = false;
                            if (!standing) foreach (var m in WordsIfSwapped(n, cells[i])) if (m.Word.ToUpperInvariant() == pick) { fixes = true; break; }
                            if (fixes)
                            {
                                TeachCells = cells; TeachWord = pick; TeachFrom = n; TeachTo = cells[i]; done = true; TeachThroughGem = throughGem;
                                if (!Simulating) WDLog.Log($"[Teach] '{pick}' one swap away: ({n.x},{n.y})->({cells[i].x},{cells[i].y}), {(dir.x != 0 ? "across" : "down")} from ({cells[0].x},{cells[0].y})");
                                break;
                            }
                            _board[cells[i].x, cells[i].y].Letter = pick[i]; _board[n.x, n.y].Letter = nl;   // undo
                        }
                        if (done) break;
                    }
                    if (done) return true;
                    for (int i = 0; i < 4; i++) _board[cells[i].x, cells[i].y].Letter = was[i];
                }
            }
            if (!Simulating) WDLog.Log("[Teach] no spot for a teaching word - the lesson falls back to searching the board");
            return false;
        }
        // ── POWER-UP LESSONS (2026-11-17, Spencer: "follow exactly how royal match rolls it out ... lets yes put in the forced move
        // stuff"). RM: the lesson level opens on a rigged board and ONE forced move makes the power-up. Ours: the level build plants
        // the recipe one swap away -
        //   rocket   : a 5-letter word            disco (Royal Orb) : a 6-letter word
        //   bomb     : two 4-letter words that CROSS, both finished by the same swap (the shared letter is the one knocked out)
        //   airplane (Flying Tome) : two 4-letter words that do NOT cross, both finished by the same swap
        // TutorialBeats.BeatPower shows exactly that swap. Sets Teach* like PlantTeachSwap (+ TeachExtraCells / TeachWord2 / TeachKind).
        public static readonly string[] TEACH_WORDS5 = {
            "APPLE","BREAD","CHAIR","CLOUD","DREAM","EARTH","GRAPE","HEART","HORSE","HOUSE","LEMON","LIGHT","MOUSE","MUSIC","OCEAN",
            "PAPER","PLANT","RIVER","SHEEP","SMILE","SNAKE","STONE","TABLE","TIGER","TRAIN","WATER","WHALE","BEACH","CANDY","CROWN",
            "EAGLE","HONEY","PIANO","STORM","SUGAR","TOWER","BERRY","CAMEL","DAISY","FAIRY","GHOST","MAGIC","NIGHT","PEARL","ROBIN" };
        public static readonly string[] TEACH_WORDS6 = {
            "GARDEN","CASTLE","FLOWER","ORANGE","PLANET","RABBIT","SILVER","WINTER","SUMMER","SPRING","BRIDGE","CANDLE","DRAGON",
            "FOREST","ISLAND","MONKEY","PENCIL","ROCKET","TURTLE","WINDOW","BUTTER","CHEESE","DINNER","FRIEND","MARKET","PIRATE",
            "SCHOOL","STREAM","TEMPLE","COOKIE","CIRCLE","LETTER","POCKET","BASKET","KITTEN","MIRROR","HAMMER","LADDER","SPIDER" };
        public static List<Vector2Int> TeachExtraCells; public static string TeachWord2, TeachKind;
        /// <summary>Both lesson words (one for rocket / disco) are still one swap away - the board may have changed since the plant.</summary>
        public bool PowerLessonStillThere(string kind)
        {
            if (TeachKind != kind || TeachCells == null || TeachWord == null) return false;
            bool w1 = false, w2 = TeachWord2 == null;
            foreach (var m in WordsIfSwapped(TeachFrom, TeachTo))
            {
                string u = m.Word.ToUpperInvariant();
                if (u == TeachWord) w1 = true; else if (u == TeachWord2) w2 = true;
            }
            return w1 && w2;
        }
        public bool PlantPowerLesson(string kind)
        {
            TeachCells = null; TeachWord = null; TeachThroughGem = false; TeachExtraCells = null; TeachWord2 = null; TeachKind = null;
            if (!SurvivalManager.FullBoardActive) return false;
            bool Plain(Vector2Int c) { if (!InBounds(c.x, c.y)) return false; var d = _board[c.x, c.y]; return d != null && char.IsLetter(d.Letter) && !d.IsStone && !d.IsFrozen && !d.HasGem && !d.IsWild && !d.IsDropTarget && !d.IsRainGold && d.SpecialKind == SPECIAL_NONE && HintMovable(d); }
            List<string> Pool(string[] src) { var p = new List<string>(); foreach (var w in src) if (WordDictionary.IsValidWord(w)) p.Add(w); return p; }
            char Get(Vector2Int c) => char.ToUpperInvariant(_board[c.x, c.y].Letter);
            void Put(Vector2Int c, char l) { _board[c.x, c.y].Letter = l; }
            // every cell the plant touches is saved so a failed try puts the board back exactly
            var saved = new Dictionary<Vector2Int, char>();
            void Set(Vector2Int c, char l) { if (!saved.ContainsKey(c)) saved[c] = _board[c.x, c.y].Letter; Put(c, l); }
            void Undo() { foreach (var kv in saved) Put(kv.Key, kv.Value); saved.Clear(); }
            bool Standing(IEnumerable<Vector2Int> cs) { foreach (var c in cs) if (FindNewWords(c.x, c.y).Count > 0) return true; return false; }
            var H = new Vector2Int(1, 0); var V = new Vector2Int(0, -1);
            List<Vector2Int> Line(Vector2Int p0, Vector2Int dir, int n) { var l = new List<Vector2Int>(); for (int i = 0; i < n; i++) { var c = p0 + dir * i; if (!Plain(c)) return null; l.Add(c); } return l; }
            var origins = new List<Vector2Int>();
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) origins.Add(new Vector2Int(x, y));
            for (int i = origins.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = origins[i]; origins[i] = origins[j]; origins[j] = t; }
            origins.Sort((u, v) => (Mathf.Abs(u.y - ROWS / 2) + Mathf.Abs(u.x - COLS / 2)).CompareTo(Mathf.Abs(v.y - ROWS / 2) + Mathf.Abs(v.x - COLS / 2)));   // middle first

            if (kind == "rocket" || kind == "disco")
            {
                int n = kind == "rocket" ? 5 : 6;
                var pool = Pool(kind == "rocket" ? TEACH_WORDS5 : TEACH_WORDS6); if (pool.Count == 0) return false;
                foreach (var p0 in origins) foreach (var dir in new[] { H, V })
                {
                    var cells = Line(p0, dir, n); if (cells == null) continue;
                    var perp = dir.x != 0 ? new Vector2Int(0, 1) : new Vector2Int(1, 0);
                    for (int tries = 0; tries < 6; tries++)
                    {
                        string pick = pool[SurvivalRng.Range(0, pool.Count)];
                        for (int i = 0; i < n; i++) Set(cells[i], pick[i]);
                        foreach (int i in new[] { n / 2, n / 2 - 1, n / 2 + 1, 1, n - 2 })   // knock out a MIDDLE letter: no shorter word is left standing
                            foreach (var nb in new[] { cells[i] + perp, cells[i] - perp })
                            {
                                if (!Plain(nb) || Get(nb) == pick[i]) continue;
                                char nl = _board[nb.x, nb.y].Letter;
                                Set(nb, pick[i]); Put(cells[i], nl);
                                var all = new List<Vector2Int>(cells) { nb };
                                if (!Standing(all))
                                {
                                    var ms = WordsIfSwapped(nb, cells[i]); bool full = false, stray = false;
                                    foreach (var m in ms)
                                    {
                                        if (m.Word.ToUpperInvariant() == pick) full = true;
                                        foreach (var c in m.Cells) if (!cells.Contains(c)) stray = true;   // a second word elsewhere would make it a bomb / tome
                                    }
                                    if (full && !stray)
                                    {
                                        TeachCells = cells; TeachWord = pick; TeachFrom = nb; TeachTo = cells[i]; TeachKind = kind;
                                        if (!Simulating) WDLog.Log($"[Teach] {kind}: '{pick}' one swap away: ({nb.x},{nb.y})->({cells[i].x},{cells[i].y}), {(dir.x != 0 ? "across" : "down")} from ({cells[0].x},{cells[0].y})");
                                        return true;
                                    }
                                }
                                Put(cells[i], pick[i]); Put(nb, nl);
                            }
                        Undo();
                    }
                }
            }
            else if (kind == "bomb" || kind == "airplane")
            {
                // 11-17 v2 (playthrough: "[Teach] bomb: no spot for the lesson" on L4 - and the level's board is seeded, so it would
                // have failed for every player). v1 drew two random words and hoped their letters matched; almost every try died
                // before it reached the board. Now: words are PAIRED by the letter they share, words whose own 3-letter stub is a
                // word are left out (it would stand on the board before the swap), all four orientations are tried at every cell,
                // and the letter left on the crossing is chosen so it spells nothing. Counters say where tries die if it still fails.
                var all4 = Pool(TEACH_WORDS); if (all4.Count < 2) return false;
                bool bomb = kind == "bomb";
                var headOk = all4.FindAll(w => !WordDictionary.IsValidWord(w.Substring(0, 3)));   // "BOO|K": BOO would stand
                var tailOk = all4.FindAll(w => !WordDictionary.IsValidWord(w.Substring(1, 3)));   // "B|OAT": OAT would stand
                int nGeo = 0, nStand = 0, nWords = 0, nTried = 0;
                const string FILL = "RNLSTDCMPGBHFWYKVEAIOU";
                foreach (var p0 in origins)
                {
                    // dirA = the way word A runs; B runs across it. flip = A starts (not ends) on the shared cell.
                    foreach (var dirA in new[] { H, V }) foreach (bool flip in new[] { false, true })
                    {
                        var dirB = dirA.x != 0 ? V : H;
                        for (int tries = 0; tries < 8; tries++)
                        {
                            List<Vector2Int> A, B; Vector2Int from, to; string wa, wb;
                            if (bomb)
                            {
                                // A ends (or starts) on X; B runs THROUGH X; X's letter waits one cell further along A's line.
                                var srcA = flip ? tailOk : headOk; if (srcA.Count == 0) break;
                                wa = srcA[SurvivalRng.Range(0, srcA.Count)];
                                int xi = flip ? 0 : 3; char shared = wa[xi];
                                int j = (tries & 1) == 0 ? 1 : 2;   // B is crossed on a MIDDLE letter: its other letters are split 1 + 2, nothing stands
                                var cand = all4.FindAll(w => w != wa && w[j] == shared); if (cand.Count == 0) { j = 3 - j; cand = all4.FindAll(w => w != wa && w[j] == shared); }
                                if (cand.Count == 0) continue;
                                wb = cand[SurvivalRng.Range(0, cand.Count)];
                                A = Line(flip ? p0 : p0, dirA, 4); if (A == null) { nGeo++; break; }
                                var X = A[xi];
                                B = Line(X - dirB * j, dirB, 4); if (B == null) { nGeo++; break; }
                                from = flip ? X - dirA : X + dirA; to = X; if (!Plain(from) || B.Contains(from)) { nGeo++; break; }
                                for (int i = 0; i < 4; i++) if (i != xi) Set(A[i], wa[i]);
                                for (int i = 0; i < 4; i++) if (i != j) Set(B[i], wb[i]);
                                Set(from, shared); Set(X, Get(X));   // X's own letter is tried below
                            }
                            else
                            {
                                // a | b are neighbours along dirB; A runs along dirA and ENDS on a, B runs along dirA and STARTS on b.
                                if (headOk.Count == 0 || tailOk.Count == 0) break;
                                wa = headOk[SurvivalRng.Range(0, headOk.Count)]; wb = tailOk[SurvivalRng.Range(0, tailOk.Count)];
                                if (wa == wb || wa[3] == wb[0]) continue;
                                var a = p0; var b = flip ? p0 - dirB : p0 + dirB; if (!Plain(a) || !Plain(b)) { nGeo++; break; }
                                A = Line(a - dirA * 3, dirA, 4); B = Line(b, dirA, 4); if (A == null || B == null) { nGeo++; break; }
                                from = a; to = b;
                                for (int i = 0; i < 3; i++) Set(A[i], wa[i]);
                                for (int i = 1; i < 4; i++) Set(B[i], wb[i]);
                                Set(a, wb[0]); Set(b, wa[3]);   // each holds the OTHER word's letter until the swap
                            }
                            nTried++;
                            var all = new List<Vector2Int>(A); foreach (var c in B) if (!all.Contains(c)) all.Add(c); if (!all.Contains(from)) all.Add(from); if (!all.Contains(to)) all.Add(to);
                            // bomb: the letter sitting on the crossing until the swap must spell nothing either way - try a few
                            bool ok = false;
                            for (int fi = 0; fi < (bomb ? FILL.Length : 1) && !ok; fi++)
                            {
                                if (bomb) { char fl = FILL[(fi + p0.x + p0.y) % FILL.Length]; if (fl == Get(from)) continue; Put(to, fl); }
                                if (Standing(all)) { nStand++; continue; }
                                bool ha = false, hb = false, stray = false;
                                foreach (var m in WordsIfSwapped(from, to))
                                {
                                    string u = m.Word.ToUpperInvariant();
                                    if (u == wa) ha = true; else if (u == wb) hb = true;
                                    else
                                    {   // a shorter word INSIDE one of the two is fine; anything reaching outside them is a third word
                                        bool inA = true, inB = true;
                                        foreach (var c in m.Cells) { if (!A.Contains(c)) inA = false; if (!B.Contains(c)) inB = false; }
                                        if (!inA && !inB) stray = true;
                                    }
                                }
                                if (ha && hb && !stray) ok = true; else nWords++;
                            }
                            if (ok)
                            {
                                TeachCells = A; TeachExtraCells = B; TeachWord = wa; TeachWord2 = wb; TeachFrom = from; TeachTo = to; TeachKind = kind;
                                if (!Simulating) WDLog.Log($"[Teach] {kind}: '{wa}' + '{wb}' one swap away: ({from.x},{from.y})<->({to.x},{to.y}) [{nTried} tries: {nStand} left a word standing, {nWords} wrong words]");
                                return true;
                            }
                            Undo();
                        }
                        Undo();
                    }
                }
                if (!Simulating) WDLog.Log($"[Teach] {kind}: FAILED after {nTried} placements - {nGeo} had no room, {nStand} left a word standing, {nWords} did not make exactly the two words (pool {all4.Count}, head-ok {headOk.Count}, tail-ok {tailOk.Count})");
            }
            Undo();
            if (!Simulating) WDLog.Log($"[Teach] {kind}: no spot for the lesson - it is skipped on this board");
            return false;
        }
        public List<Vector2Int> PlantGiftWord(out string word)
        {
            word = null;
            if (!SurvivalManager.FullBoardActive) return null;
            IsCommonWord("A");
            var pool = new List<string>();
            foreach (var w in s_commonWords) if (w.Length == 4 && w.IndexOfAny(new[] { 'Q', 'X', 'Z', 'J' }) < 0 && WordDictionary.IsValidWord(w)) pool.Add(w);
            if (pool.Count == 0) return null;
            bool GoalCell(RulesCellData d) => d != null && ((d.HasGem && LevelRules.HasGemGoal) || d.IsFrozen);
            bool Crate(int cx, int cy) => InBounds(cx, cy) && _board[cx, cy] != null && _board[cx, cy].IsStone && !_board[cx, cy].IsDropTarget && LevelRules.HasCrateGoal;
            var starts = new List<(Vector2Int p, Vector2Int d)>();
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) { starts.Add((new Vector2Int(x, y), new Vector2Int(1, 0))); starts.Add((new Vector2Int(x, y), new Vector2Int(0, -1))); }
            for (int i = starts.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var tmp = starts[i]; starts[i] = starts[j]; starts[j] = tmp; }
            foreach (var (p0, d) in starts)
            {
                var cells = new List<Vector2Int>(); var pattern = new char[4]; bool ok = true, touches = false;
                for (int i = 0; i < 4 && ok; i++)
                {
                    var c = p0 + d * i;
                    if (!InBounds(c.x, c.y)) { ok = false; break; }
                    var cd = _board[c.x, c.y];
                    if (cd == null || !char.IsLetter(cd.Letter) || cd.IsStone || cd.IsWild || cd.IsDropTarget || cd.SpecialKind != SPECIAL_NONE) { ok = false; break; }
                    if (GoalCell(cd)) { pattern[i] = char.ToUpperInvariant(cd.Letter); touches = true; }
                    else if (cd.HasGem || cd.IsRainGold) { ok = false; break; }
                    else pattern[i] = '\0';
                    if (Crate(c.x + 1, c.y) || Crate(c.x - 1, c.y) || Crate(c.x, c.y + 1) || Crate(c.x, c.y - 1)) touches = true;
                    if (CRATE_DIAGONAL && (Crate(c.x + 1, c.y + 1) || Crate(c.x + 1, c.y - 1) || Crate(c.x - 1, c.y + 1) || Crate(c.x - 1, c.y - 1))) touches = true;
                    cells.Add(c);
                }
                if (!ok || !touches) continue;
                var fits = pool.FindAll(w => { for (int i = 0; i < 4; i++) if (pattern[i] != '\0' && w[i] != pattern[i]) return false; return true; });
                if (fits.Count == 0) continue;
                string pick = fits[SurvivalRng.Range(0, fits.Count)];
                var was = new char[4]; for (int i = 0; i < 4; i++) { was[i] = _board[cells[i].x, cells[i].y].Letter; _board[cells[i].x, cells[i].y].Letter = pick[i]; }
                // exactly ONE word may stand here: no accidental crossing words through the gift's letters
                bool only = true;
                foreach (var c in cells) foreach (var m in FindNewWords(c.x, c.y))
                    if (m.Word.ToUpperInvariant() != pick || m.Cells.Count != 4) { only = false; break; }
                if (!only) { for (int i = 0; i < 4; i++) _board[cells[i].x, cells[i].y].Letter = was[i]; continue; }
                word = pick;
                if (!Simulating) UnityEngine.Debug.Log($"[Gift] '{pick}' stands at ({cells[0].x},{cells[0].y}) {(d.x != 0 ? "across" : "down")} - touches a goal");
                return cells;
            }
            return null;
        }

        /// <summary>If the board is stale, re-letter one plain tile so a common word is one swap away. True = changed
        /// <paramref name="cell"/> to <paramref name="letter"/> (data only; the caller updates the tile).</summary>
        public bool TryStaleRescue(out Vector2Int cell, out char letter, out int commonBefore)
        {
            cell = new Vector2Int(-1, -1); letter = '\0'; commonBefore = -1;
            if (!STALE_RESCUE_ON || !SurvivalManager.FullBoardActive) return false;
            commonBefore = CountCommonWordSwaps();
            // ENDGAME (2026-10-23 bot run 14:52: casual p50 28 but p75 47 on L7 — the long tail is the LAST few goal pieces no
            // common word reaches). With <= ENDGAME_PIECES left and no common-word swap touching one, re-letter a tile NEAR a
            // goal piece so one does. Stale boards (the original rule) still rescue anywhere.
            bool endgame = ENDGAME_RESCUE_ON && GoalPiecesLeft() is int left && left > 0 && left <= ENDGAME_PIECES && !AnyCommonGoalSwap();
            if (commonBefore >= STALE_MIN && !endgame) return false;
            var cells = new List<Vector2Int>();
            var near = endgame ? GoalNeighbourhood() : null;
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
            {
                var d = _board[x, y]; var c0 = new Vector2Int(x, y);
                if (HintMovable(d) && !d.HasGem && !d.IsRainGold && (near == null || near.Contains(c0))) cells.Add(c0);
            }
            for (int i = cells.Count - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = cells[i]; cells[i] = cells[j]; cells[j] = t; }
            var letters = (char[])RESCUE_LETTERS.Clone();
            var nb = new[] { new Vector2Int(1, 0), new Vector2Int(-1, 0), new Vector2Int(0, 1), new Vector2Int(0, -1) };
            int tried = 0;
            foreach (var c in cells)
            {
                if (tried++ >= STALE_RESCUE_CELLS) break;
                var d = _board[c.x, c.y]; char orig = d.Letter;
                for (int i = letters.Length - 1; i > 0; i--) { int j = SurvivalRng.Range(0, i + 1); var t = letters[i]; letters[i] = letters[j]; letters[j] = t; }
                foreach (var L in letters)
                {
                    if (char.ToUpperInvariant(orig) == L) continue;
                    d.Letter = L;
                    foreach (var o in nb)
                    {
                        var n = c + o;
                        if (!InBounds(n.x, n.y) || !HintMovable(_board[n.x, n.y]) || char.ToUpperInvariant(_board[n.x, n.y].Letter) == L) continue;
                        foreach (var m in WordsIfSwapped(c, n))
                            if ((IsCommonWord(m.Word.ToUpperInvariant()) || LevelRules.IsThemeWord(m.Word)) && (!endgame || TouchesGoal(m)))
                            { cell = c; letter = L; if (endgame && !Simulating) WDLog.Log($"[Stale] ENDGAME rescue — {GoalPiecesLeft()} goal piece(s) left, none reachable by a common word"); return true; }
                    }
                }
                d.Letter = orig;
            }
            return false;
        }

        /// <summary>commonOnly (2026-10-23 Spencer: "when you have a stale board and nothing looks available thats when i feel the fun
        /// trail off"): count only swaps whose word is on the common list (or a theme word) — the ones a person actually spots.
        /// His hints offered MISE / GAGE / TARO / BROS: legal, invisible to a normal player.</summary>
        public List<(Vector2Int a, Vector2Int b, int score)> EnumerateWordSwaps(bool commonOnly = false)
        {
            var list = new List<(Vector2Int, Vector2Int, int)>();
            var prevShort = ShortWordCells;
            char collect = LevelRules.ActiveCollectLetter;
            var dirs = new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) };
            try
            {
                for (int x = 0; x < COLS; x++)
                    for (int y = 0; y < ROWS; y++)
                    {
                        var ca = _board[x, y];
                        if (!BotSwapMovable(ca)) continue;
                        foreach (var d in dirs)
                        {
                            int x2 = x + d.x, y2 = y + d.y;
                            if (x2 >= COLS || y2 >= ROWS) continue;
                            var cb = _board[x2, y2];
                            if (!BotSwapMovable(cb) || char.ToUpperInvariant(ca.Letter) == char.ToUpperInvariant(cb.Letter)) continue;
                            if (ca.SpecialKind != SPECIAL_NONE && cb.SpecialKind != SPECIAL_NONE) continue;   // two power-ups = a combo, not a word swap
                            int score = 0;
                            foreach (var m in WordsIfSwapped(new Vector2Int(x, y), new Vector2Int(x2, y2)))
                                if (!commonOnly || IsCommonWord(m.Word.ToUpperInvariant()) || LevelRules.IsThemeWord(m.Word)) score += HintWordScore(m, collect);
                            if (score > 0) list.Add((new Vector2Int(x, y), new Vector2Int(x2, y2), score));
                        }
                    }
            }
            finally { ShortWordCells = prevShort; }
            return list;
        }
        public static bool IsBotMovable(RulesCellData c) => HintMovable(c);
        /// <summary>11-03 (Spencer: "update the bot"): the BOT also moves rockets / bombs / discos / airplanes into words, like a player
        /// can (HandManager: only the dynamite is locked; a power-up swap must make a word or it bounces). Simulating only - live hints
        /// still never suggest moving a power-up. false = the bot never moves power-ups (every tuning pass before 11-03).</summary>
        public static bool BOT_MOVES_SPECIALS = true;
        private static bool BotSwapMovable(RulesCellData c)
        {
            if (HintMovable(c)) return true;
            if (!BOT_MOVES_SPECIALS || !Simulating || c == null) return false;
            return c.SpecialKind != SPECIAL_NONE && !(HandManager.SPECIALS_LOCKED && c.SpecialKind == SPECIAL_TNT)
                && !c.IsFrozen && !c.IsStone && !c.IsDropTarget && !c.IsAnchored && !c.HasGem;
        }

        private static bool HintMovable(RulesCellData c) =>
            c != null && char.IsLetter(c.Letter) && !c.IsFrozen && !c.IsStone && !c.IsWild && !c.IsDropTarget && !c.IsAnchored
            && c.SpecialKind == SPECIAL_NONE && !(HandManager.GEMS_LOCKED && c.HasGem);   // hints never suggest moving a locked gem
        private int HintWordScore(RulesWordMatch m, char collect)
        {
            int len = m.Word.Length;
            int s = len * 10 + (len >= 6 ? 25 : len >= 5 ? 15 : 0);   // power-ups
            if (LevelRules.IsThemeWord(m.Word)) s += 40;
            var seen = new HashSet<Vector2Int>();
            foreach (var p in m.Cells)
            {
                var c = _board[p.x, p.y]; if (c == null) continue;
                if (c.HasGem && GoalOpen(LevelRules.GoalType.Gems)) s += 15;   // only goals still open (2026-10-23)
                if (c.IsFrozen && GoalOpen(LevelRules.GoalType.Ice)) s += 15;
                if (_grass != null && _grass[p.x, p.y] && GoalOpen(LevelRules.GoalType.Grass)) s += 8;   // each grass square it would mow
                if (collect != '\0' && char.ToUpperInvariant(c.Letter) == collect) s += 10;
                for (int k = 0; k < (CRATE_DIAGONAL && SurvivalManager.FullBoardActive ? 8 : 4); k++)   // crates beside the word break when it pops (8 sides with CRATE_DIAGONAL)
                {
                    var q = p + (k == 0 ? Vector2Int.right : k == 1 ? Vector2Int.left : k == 2 ? Vector2Int.up : k == 3 ? Vector2Int.down
                                 : k == 4 ? new Vector2Int(1, 1) : k == 5 ? new Vector2Int(1, -1) : k == 6 ? new Vector2Int(-1, 1) : new Vector2Int(-1, -1));
                    if (!InBounds(q.x, q.y)) continue;
                    if (k >= 4 && !(_board[q.x, q.y] != null && _board[q.x, q.y].IsStone && !_board[q.x, q.y].IsDropTarget)) continue;   // diagonals: crates only
                    if (!seen.Add(q)) continue;
                    var n = _board[q.x, q.y];
                    if (n != null && n.IsStone && !n.IsDropTarget && n.ShelfPart == 0 && GoalOpen(LevelRules.GoalType.Crates)) s += 12;
                    if (n != null && n.ShelfPart > 0 && !(n.ShelfKind == 1 && n.ShelfBooks <= 0) && GoalOpen(n.ShelfKind == 1 ? LevelRules.GoalType.Safes : LevelRules.GoalType.Books)) s += 12;   // BOOKSHELF / SAFE beside the word - diagonals too (11-03: shelf cells are stones, so CRATE_DIAGONAL already lets a word hit them diagonally; the bot + hints didn't know)
                    // 11-02: ice (10-30) and gems (11-02) beside the word count too - otherwise hints, the goal-dead check and the
                    // tutorial's goal swap never knew a touching word reaches them
                    if (n != null && !n.IsStone && !m.Cells.Contains(q))
                    {
                        if (ICE_ADJACENT_THAW && SurvivalManager.FullBoardActive && n.IsFrozen && GoalOpen(LevelRules.GoalType.Ice)) s += 12;
                        if (GEM_ADJACENT_COLLECT && SurvivalManager.FullBoardActive && n.HasGem && GoalOpen(LevelRules.GoalType.Gems)) s += 12;
                    }
                }
                if (LevelRules.HasDuckGoal)   // clearing under a duck brings it down
                    for (int yy = p.y + 1; yy < ROWS; yy++) { var u = _board[p.x, yy]; if (u != null && u.IsDropTarget) { s += 10; break; } }
            }
            return s;
        }

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
                    runChars[step]  = ScanChar(_board[nc, nr]);   // 11-03: a power-up reads as the wild
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

                        string candidate = ResolveWildPattern(new string(wordChars), TouchesSwap(wordCells));   // 11-03 wild power-ups
                        if (candidate == null)
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
            public List<KeyValuePair<string, List<Vector2Int>>> PoppedWords;
            public List<Vector2Int> GemsCollectedOnSpecials;   // gem tiles that became a special this step (collected, tile survives)   // every armed word this step exploded + its cells (word pop-ups, 2026-10-28)
            public List<KeyValuePair<Vector2Int, int>> ShelfHits;   // BOOKSHELF (11-03): shelf anchor (bottom-left) -> slot of the book knocked off
            public List<Vector2Int> TreasureHitCells, TreasureOpenedCells, CrateHitCells;   // CrateHitCells: crates that lost a layer (11-03)   // treasure lost a band (survives) / opened (cleared, pays coins)
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
                if (SimGrid != null)
                {
                    Tile oldTile = SimGrid.GetTile(col, targetRow);
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
            _boosterFallCascade = true;   // words this fall spells are fall words: the cascade cap applies from depth 0
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

                if (FullBoardActive && FALL_SEEDS_MOVED_ONLY) AddFallSeeds(gravityMoves, _stepSeedCells);
                else
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
                if (SimGrid != null && SimFX != null)
                {
                    var diffuseTiles = new List<Tile>();
                    foreach (var pw in expiredList)
                    {
                        if (pw.Cells == null) continue;
                        for (int c = 0; c < pw.Cells.Count; c++)
                        {
                            Tile t = SimGrid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                            if (t != null) diffuseTiles.Add(t);
                        }
                    }
                    WDLog.Log($"[DiffusePop] FinalizeDrop expired {expired} word(s) → queued {diffuseTiles.Count} tile(s) (turn={_globalTurn})");
                    SimFX.QueueDiffusePops(diffuseTiles);
                }
            }

            RemoveInvalidPrimedWords();

            // Prime pitch-ramp: keep CLIMBING across consecutive priming drops while charged words are still on the
            // board (the combo is building); reset to base pitch only once they've ALL resolved — detonated or
            // expired — so the next charge-up starts the scale over. 2026-07-14 Spencer.
            if (_primedRegistry.Count == 0) SimAudio?.ResetPrimeRamp();

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
        private bool _boosterFallCascade;   // set by BeginCascadeAfterBoosterClear (duck landings, boosters), cleared on finalize
        public void FinalizeBoosterCascade()
        {
            _boosterFallCascade = false;
            if (_currentPhase == ResolutionPhase.Idle) return;

            RemoveInvalidPrimedWords();
            if (_primedRegistry.Count == 0) SimAudio?.ResetPrimeRamp(); // reset only once all charged words resolved
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

            // LEVEL ALREADY WON (2026-10-23 Spencer: "during the bonus stage words that are made through cascade are just lighting up
            // in green and freezing"). After the win nothing may pop (auto-pop is blocked), so fall words primed green and sat
            // there through the bonus blast (log: ITEM, GRIM, NOSE, SENT). Words formed after the win are recorded as seen and
            // never armed. The bonus blast's own 1-letter seeds are added straight to the registry, not detected, so it's unaffected.
            if (FullBoardActive && SurvivalManager.AwaitingNextLevel && !Simulating && newWords.Count > 0)
            {
                foreach (var w in newWords) _scoredWordKeys.Add(w.Word + "|" + w.CellKey);
                newWords.Clear();
            }

            if (FullBoardActive && CASCADE_CAP_ON && (_stepChainDepth > 0 || _boosterFallCascade) && newWords.Count > 0)
            {
                // FALL WORDS NEVER SET OFF A SPECIAL (2026-10-23 Spencer: "i dont understand why the bomb went off on the side cause
                // i didnt really touch anything around it" — log: MEMO's bomb dropped letters that spelled REEF through a TNT).
                // A falling word is invisible to the player the way a falling match-3 line is not; a power-up should go off from
                // the player's word, a blast, or a tap. Such words are HELD (a swipe can still claim them).
                if (CASCADE_HOLD_SPECIAL_WORDS)
                    for (int i = newWords.Count - 1; i >= 0; i--)
                    {
                        bool hasSpecial = false;
                        foreach (var c in newWords[i].Cells) { var d = _board[c.x, c.y]; if (d != null && d.SpecialKind != SPECIAL_NONE) { hasSpecial = true; break; } }
                        if (!hasSpecial) continue;
                        string hk = newWords[i].Word + "|" + newWords[i].CellKey;
                        _scoredWordKeys.Add(hk); _riseSeededKeys.Add(hk); CascadeWordsHeld++;
                        WDLog.Log($"[CascadeCap] held '{newWords[i].Word}' — a falling word through a power-up doesn't set it off");
                        newWords.RemoveAt(i);
                    }
            }
            // The 1-free-fall-word cap now holds even after a special fired this move (was lifted then — that lift let MEMO's bomb
            // pour out REEF + SAGE + NEAR at once). CASCADE_LIFT_AFTER_SPECIAL = true restores the old lift.
            if (FullBoardActive && CASCADE_CAP_ON && (_stepChainDepth > 0 || _boosterFallCascade) && (!SpecialFiredThisMove || !CASCADE_LIFT_AFTER_SPECIAL) && newWords.Count > 0)
            {
                newWords.Sort((a, b) => b.Word.Length.CompareTo(a.Word.Length));   // the free one is the longest
                int allow = Mathf.Max(0, CASCADE_FREE_PER_MOVE - CascadeFreeThisMove);
                for (int i = newWords.Count - 1; i >= allow; i--)
                {
                    string key = newWords[i].Word + "|" + newWords[i].CellKey;
                    _scoredWordKeys.Add(key); _riseSeededKeys.Add(key);   // held: no auto-pop; a swipe may still claim it
                    CascadeWordsHeld++;
                    WDLog.Log($"[CascadeCap] held '{newWords[i].Word}' on the board (free words this move: {CascadeFreeThisMove})");
                    newWords.RemoveAt(i);
                }
                CascadeFreeThisMove += newWords.Count;
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
                if (!(HandManager.QUICK_POP && SurvivalManager.FullBoardActive)) SimAudio?.PlayTilePrimed();   // QUICK POP: no prime chime
                if (!Simulating) HapticsManager.Light();
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
            if (SimGrid == null || pw == null || pw.Cells == null) return;

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
                Tile tile = SimGrid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
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
                if (!Simulating) WDLog.Log($"[Goal] ice thawed at ({cell.x},{cell.y}) — {CountFrozenCells()} left");   // 2026-10-23: L10 loss had no ice trail
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

        // TREASURE per-step bookkeeping (reset at the top of every DoExplode; read into the StepResult)
        private readonly HashSet<Vector2Int> _treasureHitThisStep = new HashSet<Vector2Int>();
        private List<Vector2Int> _treasureHitCells = new List<Vector2Int>(), _treasureOpenedCells = new List<Vector2Int>();
        private List<KeyValuePair<string, List<Vector2Int>>> _poppedWords = new List<KeyValuePair<string, List<Vector2Int>>>();
        private List<Vector2Int> _gemsOnSpecials = new List<Vector2Int>();
        public static int TreasuresOpened;   // monotonic stat
        private readonly HashSet<Vector2Int> _crateHitThisStep = new HashSet<Vector2Int>();
        private List<Vector2Int> _crateHitCells = new List<Vector2Int>();
        /// <summary>One hit on a crate. TRUE = this hit BREAKS it (caller clears the cell as before); FALSE = it lost a layer and stays
        /// (or was already hit this step - one layer per crate per resolution, like Royal Match).</summary>
        private bool CrateHit(Vector2Int p, RulesCellData d)
        {
            if (d.CrateHP <= 1) { _crateHitThisStep.Add(p); return true; }   // last layer: breaks
            if (!_crateHitThisStep.Add(p)) return false;
            d.CrateHP--; _crateHitCells.Add(p);
            if (!Simulating) WDLog.Log($"[Crate] ({p.x},{p.y}) lost a layer -> {d.CrateHP} left");
            return false;
        }
        // ── BOOKSHELF (2026-11-03) ──
        public static int SHELF_MAX_BOOKS = 6;   // books one shelf holds (the art has 6 slots)
        public static int ShelfStyleNext;        // set per level (LevelRules.ShelfStyle); a level's 2nd shelf gets the other style
        public static int SAFE_HITS = 6;         // 11-03 SAFE: hits to open (one lamp each - the art has 6)
        public static int SAFE_COINS = 10;
        public static int SafeHitsNow = 6;        // hits a safe placed THIS level takes (Gold Rush: LevelRules.GOLD_RUSH_SAFE_HITS)       // coins a safe pays when it opens (HandManager.HitShelfTiles)
        private readonly HashSet<int> _shelfHitThisStep = new HashSet<int>();
        private List<KeyValuePair<Vector2Int, int>> _shelfHits = new List<KeyValuePair<Vector2Int, int>>();   // shelf anchor -> index of the book knocked off
        private readonly List<Vector2Int> _shelfClearedCells = new List<Vector2Int>();
        private static Vector2Int ShelfOffset(int part) => part == 2 ? new Vector2Int(1, 0) : part == 3 ? new Vector2Int(0, 1) : part == 4 ? new Vector2Int(1, 1) : Vector2Int.zero;
        /// <summary>One hit on a shelf: knocks ONE book (once per shelf per resolution step). TRUE when that was the last book - the
        /// shelf's four cells are cleared HERE (all of them, not just <paramref name="p"/>) and queued in _shelfClearedCells.</summary>
        private bool ShelfHit(Vector2Int p, RulesCellData d)
        {
            if (d.ShelfPart <= 0 || (d.ShelfKind == 1 && d.ShelfBooks <= 0) || !_shelfHitThisStep.Add(d.ShelfId)) return false;   // an OPENED safe (SAFE_STAYS_OPEN) is inert
            var a = p - ShelfOffset(d.ShelfPart);
            int left = Mathf.Max(0, d.ShelfBooks - 1);
            for (int i = 1; i <= 4; i++) { var q = a + ShelfOffset(i); if (InBounds(q.x, q.y) && _board[q.x, q.y] != null && _board[q.x, q.y].ShelfId == d.ShelfId) _board[q.x, q.y].ShelfBooks = left; }
            _shelfHits.Add(new KeyValuePair<Vector2Int, int>(a, left));   // the book at slot 'left' flies (books leave from the last slot back)
            _stepTotalScore += ICE_BOUNTY;
            if (!Simulating)
            {
                if (d.ShelfKind == 0) { LevelRules.BooksAwaitingFly++; LevelRules.BooksAwaitingSince = Time.unscaledTime; WDLog.Log($"[Shelf] ({a.x},{a.y}) knocked a book -> {left} left"); }
                else WDLog.Log($"[Safe] ({a.x},{a.y}) lamp lit -> {left} hit(s) to open");
            }
            if (left > 0) return false;
            if (d.ShelfKind == 1 && SAFE_STAYS_OPEN) { if (!Simulating) WDLog.Log($"[Safe] ({a.x},{a.y}) OPENED - stays on the board"); return false; }
            for (int i = 1; i <= 4; i++)
            {
                var q = a + ShelfOffset(i);
                if (!InBounds(q.x, q.y) || _board[q.x, q.y] == null || _board[q.x, q.y].ShelfId != d.ShelfId) continue;
                _board[q.x, q.y] = null; _shelfClearedCells.Add(q);
            }
            if (!Simulating) WDLog.Log($"[Shelf] ({a.x},{a.y}) empty -> cleared");
            return true;
        }
        /// <summary>BOOSTERS (11-07, Spencer: boosters must hit the bookshelf and safe): a booster's area hits each shelf/safe in it
        /// ONCE, the way a blast does. Returns the hits (anchor -> slot) for HandManager.HitShelvesFromBooster; the cells of a shelf
        /// that just emptied are cleared in data and added to <paramref name="cleared"/> (the caller removes their tiles).</summary>
        public List<KeyValuePair<Vector2Int, int>> BoosterShelfHits(IEnumerable<Vector2Int> cells, List<Vector2Int> cleared)
        {
            var saved = _shelfHits; _shelfHits = new List<KeyValuePair<Vector2Int, int>>();
            _shelfHitThisStep.Clear(); _shelfClearedCells.Clear();
            foreach (var p in cells) { if (!InBounds(p.x, p.y)) continue; var d = _board[p.x, p.y]; if (d != null && d.ShelfPart > 0) ShelfHit(p, d); }
            var hits = _shelfHits; _shelfHits = saved;
            cleared?.AddRange(_shelfClearedCells); _shelfClearedCells.Clear(); _shelfHitThisStep.Clear();
            return hits;
        }
        /// <summary>BOOKSHELF placement: shelves of up to SHELF_MAX_BOOKS books holding <paramref name="books"/> in total, standing on the
        /// FLOOR (rows 0-1) - they're anchored, and nothing refills underneath an anchored piece. One shelf = centre columns, two =
        /// mirrored near the sides. Returns books placed.</summary>
        public int PlaceShelves(int books, HashSet<Vector2Int> keep) => PlaceShelves(books, keep, 0, out _);
        /// <summary>BOOKSHELVES + SAFES (11-03): all 2x2 anchored pieces of a level, standing on the floor (rows 0-1), spread
        /// over the columns (1 piece = centre, 2 = near the sides, 3 = left / centre / right). Shelves first, then safes.</summary>
        public int PlaceShelves(int books, HashSet<Vector2Int> keep, int safes, out int safesPlaced)
        {
            safesPlaced = 0;
            int shelves = books > 0 ? Mathf.Clamp((books + SHELF_MAX_BOOKS - 1) / SHELF_MAX_BOOKS, 1, 2) : 0;
            safes = Mathf.Clamp(safes, 0, 3 - shelves);
            int pieces = shelves + safes;
            if (pieces <= 0) return 0;
            var cols = pieces == 1 ? new[] { COLS / 2 - 1 } : pieces == 2 ? new[] { 1, COLS - 3 } : new[] { 0, COLS / 2 - 1, COLS - 2 };
            int placed = 0;
            for (int s = 0; s < pieces; s++)
            {
                bool safe = s >= shelves;
                int n = safe ? SafeHitsNow : Mathf.Min(SHELF_MAX_BOOKS, books - placed); if (n <= 0) continue;
                var a = new Vector2Int(cols[s], 0);
                bool ok = true;
                for (int i = 1; i <= 4; i++) { var q = a + ShelfOffset(i); if (!InBounds(q.x, q.y) || IsVoid(q.x, q.y)) ok = false; }
                if (!ok) { if (!Simulating) Debug.LogWarning($"[Shelf] no room at ({a.x},{a.y}) (board shape) - skipped"); continue; }
                for (int i = 1; i <= 4; i++)
                {
                    var q = a + ShelfOffset(i);
                    var d = new RulesCellData { Letter = '#', Col = q.x, Row = q.y, PlayerIndex = -1, IsStone = true, IsAnchored = true, ShelfPart = i, ShelfId = s + 1, ShelfBooks = n,
                                                ShelfStyle = (ShelfStyleNext + s) % 2, ShelfKind = safe ? 1 : 0 };
                    _board[q.x, q.y] = d; _bonusCells[q.x, q.y] = false; _cyanCells[q.x, q.y] = false;
                    keep?.Add(q);
                }
                if (safe) safesPlaced++; else placed += n;
            }
            if (!Simulating && pieces > 0) WDLog.Log($"[Shelf] {placed} book(s) on {shelves} shelf/shelves, {safesPlaced} safe(s)");
            return placed;
        }
        /// <summary>Booster hit (mallet / axe / lightning): peel one layer. TRUE = the crate survives (the booster must NOT clear it).</summary>
        public bool PeelCrateLayer(int col, int row)
        {
            if (!InBounds(col, row)) return false; var d = _board[col, row];
            if (d == null || !d.IsStone || d.IsDropTarget || d.TreasureHits > 0 || d.CrateHP <= 1) return false;
            d.CrateHP--; if (!Simulating) WDLog.Log($"[Crate] booster peeled ({col},{row}) -> {d.CrateHP} left"); return true;
        }
        /// <summary>Give the placed crates their starting layers. mix = comma list picked per crate (e.g. "1,2,3"; "3" = all full);
        /// a crate and its mirror get the same, so symmetric layouts stay symmetric. null/empty = all 1 (single hit).</summary>
        public void AssignCrateHits(string mix)
        {
            var opts = new List<int>();
            if (!string.IsNullOrEmpty(mix)) foreach (var t in mix.Split(',')) if (int.TryParse(t.Trim(), out int v)) opts.Add(Mathf.Clamp(v, 1, 3));
            int n = 0, layers = 0;
            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
            {
                var d = _board[x, y]; if (d == null || !d.IsStone || d.IsDropTarget || d.TreasureHits > 0) continue;
                int mx = COLS - 1 - x; var dm = _board[mx, y];
                if (mx < x && dm != null && dm.IsStone && !dm.IsDropTarget && dm.TreasureHits == 0) { d.CrateHP = Mathf.Max(1, dm.CrateHP); }
                else d.CrateHP = opts.Count == 0 ? 1 : opts[SurvivalRng.Range(0, opts.Count)];
                n++; layers += d.CrateHP;
            }
            if (!Simulating && n > 0) WDLog.Log($"[Crate] {n} crate(s), {layers} layer(s) (mix '{mix}')");
        }
        /// <summary>One hit on a treasure. Returns TRUE when this hit OPENS it (caller then clears the cell like a crate);
        /// FALSE when it survives (lost a band) or was already hit this step.</summary>
        private bool TreasureHit(Vector2Int p, RulesCellData d)
        {
            if (!_treasureHitThisStep.Add(p)) return false;
            d.TreasureHits--;
            if (d.TreasureHits > 0) { _treasureHitCells.Add(p); return false; }
            _treasureOpenedCells.Add(p); TreasuresOpened++;
            return true;
        }

        private StepResult DoExplode()
        {
            _treasureHitThisStep.Clear(); _treasureHitCells = new List<Vector2Int>(); _treasureOpenedCells = new List<Vector2Int>();
            _crateHitThisStep.Clear(); _crateHitCells = new List<Vector2Int>();
            _shelfHitThisStep.Clear(); _shelfHits = new List<KeyValuePair<Vector2Int, int>>(); _shelfClearedCells.Clear();
            _poppedWords = new List<KeyValuePair<string, List<Vector2Int>>>();
            _gemsOnSpecials = new List<Vector2Int>();
            // COLLECT GOAL: remember letters before this step clears anything, count target letters among the exploded.
            char collectL = LevelRules.ActiveCollectLetter;
            char[,] preLetters = null;
            bool[,] preGems = null;
            if (collectL != '\0')
            {
                preLetters = new char[COLS, ROWS];
                for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                { var d = _board[x, y]; preLetters[x, y] = (d == null || d.IsStone) ? '\0' : char.ToUpperInvariant(d.Letter); }
            }
            if (LevelRules.HasGemGoal)
            {
                preGems = new bool[COLS, ROWS];
                for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) { var d = _board[x, y]; preGems[x, y] = d != null && d.HasGem; }
            }
            ShortWordCells = null;   // full board: from here on, only 4+ letter words can form by falling
            bool goalDeadSwap = GoalDeadAtSwap; GoalDeadAtSwap = false;   // the airplane route is for the swap's own words only (this step)
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
                            SimObjectives?.NotifyWordExploded(trigWord.Word, MatchController.PLAYER_HUMAN);
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
            if (comboWordCount > 0) SimObjectives?.NotifyComboDetonated(comboWordCount);

            // FULL BOARD SPECIALS LADDER (2026-10-21 Spencer: "you arent making enough rockets and i dont think ive ever even
            // made a disco ball"). With 4+ letter words, 7 letters for a disco was near impossible (58 four-letter pops vs 5
            // of 5+ in one session). Candy Crush's ladder, one step up from its minimum:
            //   5 letters -> rocket   ·   two words CROSSING in one pop -> bomb at the crossing (CC's L/T wrapped candy)
            //   6+ letters -> disco ball.        Rising-row mode keeps 5 / 6 / 7+.
            // A CROSSING = an ACROSS word and a DOWN word sharing a tile (2026-10-22 fix: two words in the SAME line that just
            // overlap — a word and its reverse, STAR inside STARER — were counted too). One bomb per crossing (see below).
            var crossH = new HashSet<Vector2Int>(); var crossV = new HashSet<Vector2Int>();
            if (SurvivalManager.FullBoardActive)
                for (int ci = 0; ci < _stepPendingTriggers.Count; ci++)
                {
                    var cpw = _primedRegistry.GetById(_stepPendingTriggers[ci]);
                    if (cpw == null || cpw.Cells == null || cpw.Cells.Count < 2 || cpw.Word == null || cpw.Word.Length < 4) continue;
                    bool across = cpw.Cells[0].y == cpw.Cells[cpw.Cells.Count - 1].y;
                    foreach (var cc in cpw.Cells) (across ? crossH : crossV).Add(cc);
                }
            var crossUse = new Dictionary<Vector2Int, int>();
            foreach (var cc in crossH) if (crossV.Contains(cc)) crossUse[cc] = 2;

            // 11-17 (Spencer: "if a flying tome is used it shouldnt be able to create another flying tome with the words that it made"):
            // a Tome is a wild letter, so swapping one into a word while the same swap finished a second word earned a NEW Tome
            // from that second word. A swap that spends a Tome cannot earn one. Checked before any word resolves (order-proof).
            if (AirplanePending && TOME_NO_SELF_REFILL)
                for (int i = 0; i < _stepPendingTriggers.Count && AirplanePending; i++)
                {
                    var tw = _primedRegistry.GetById(_stepPendingTriggers[i]);
                    if (tw?.Cells == null) continue;
                    foreach (var tc in tw.Cells)
                        if (InBounds(tc.x, tc.y) && _board[tc.x, tc.y] != null && _board[tc.x, tc.y].SpecialKind == SPECIAL_AIRPLANE)
                        { AirplanePending = false; if (!Simulating) WDLog.Log($"[Airplane] not earned: '{tw.Word}' spent a Flying Tome in this swap"); break; }
                }
            for (int i = 0; i < _stepPendingTriggers.Count; i++)
            {
                int pid = _stepPendingTriggers[i];
                PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                if (pw == null) continue;

                // Objective tracking: this primed word is about to explode (live path). 2026-06-09.
                SimObjectives?.NotifyWordExploded(pw.Word, pw.OwnerPlayer, pw.PrimedByEdit);

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
                // FOUND-IT BONUS (10-31): a DRAGGED word counts DRAG_SPECIAL_BONUS letters longer for its special (4-letter drag =
                // rocket, 5 = disco on the full board). Spent here.
                int dragBonus = (DRAG_REWARD && SwipedPrimedIds.Remove(pw.Id)) ? DRAG_SPECIAL_BONUS : 0;
                int effLen = pw.Word != null ? pw.Word.Length + dragBonus : 0;
                if (SpecialTilesActive && !firesSpecial && pw.Word != null && pw.Cells != null
                    && ((effLen >= 5 && pw.Cells.Count + dragBonus >= 5) || crossAt.x >= 0))
                {
                    bool horizontal = true;
                    for (int c = 1; c < pw.Cells.Count; c++) if (pw.Cells[c].y != pw.Cells[0].y) { horizontal = false; break; }
                    if (fullLadder)
                        specialKind = effLen >= 6 ? SPECIAL_DISCO : crossAt.x >= 0 ? SPECIAL_BOMB
                                    : (horizontal ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V);
                    else
                        specialKind = effLen >= 7 ? SPECIAL_DISCO : effLen == 6 ? SPECIAL_BOMB
                                    : (horizontal ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V);
                    if (dragBonus > 0) WDLog.Log($"[FoundIt] dragged '{pw.Word}' ({pw.Word.Length}) -> {SpecialName(specialKind)} (counts as {effLen})");
                    System.Func<Vector2Int, bool> eligible = p => InBounds(p.x, p.y) && _board[p.x, p.y] != null
                        && !_board[p.x, p.y].IsFrozen && _board[p.x, p.y].SpecialKind == SPECIAL_NONE
                        && !_board[p.x, p.y].IsDropTarget && !specialSurvivors.Contains(p);
                    if (specialKind == SPECIAL_BOMB && crossAt.x >= 0)
                    {
                        // ONE bomb per crossing (2026-10-22 Spencer: "too many bombs" — REST x RIOT made TWO bombs, STAR x PATS x
                        // TAPE made THREE: each crossing word dropped its own). The first word puts it on the shared tile; the
                        // others just pop.
                        if (eligible(crossAt)) specialAt = crossAt;
                        else specialKind = pw.Word.Length >= 5 ? (horizontal ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V) : SPECIAL_NONE;   // a 5+ word still earns its own rocket
                    }
                    if (specialAt.x < 0 && LastSwapCells != null)
                        foreach (var sc in LastSwapCells)
                            if (specialAt.x < 0 && pw.Cells.Contains(sc) && eligible(sc)) specialAt = sc;
                    if (specialAt.x < 0 && eligible(pw.Cells[pw.Cells.Count / 2])) specialAt = pw.Cells[pw.Cells.Count / 2];
                    if (specialAt.x < 0) foreach (var p in pw.Cells) if (specialAt.x < 0 && eligible(p)) specialAt = p;
                    if (specialAt.x < 0) specialKind = SPECIAL_NONE;
                }
                // ENDGAME AIRPLANE (2026-10-23, Spencer OK'd "lets try it"): with <= ENDGAME_PIECES goal pieces left, an earned
                // rocket or bomb becomes an AIRPLANE — it flies to a remaining piece by itself (RM's propeller is its endgame tool).
                // Discos stay discos. Replaces the visible re-letter rescue.
                if (ENDGAME_AIRPLANE && SurvivalManager.FullBoardActive && specialKind != SPECIAL_NONE && specialKind != SPECIAL_DISCO && specialKind != SPECIAL_AIRPLANE
                    && !HandManager.BonusBlastRunning && GoalPiecesLeft() is int gl && gl > 0 && (gl <= ENDGAME_PIECES || goalDeadSwap))
                {
                    if (!Simulating) WDLog.Log($"[Special] {(goalDeadSwap ? "goal stranded" : "endgame")} ({gl} goal piece(s) left): {SpecialName(specialKind)} from '{pw.Word}' becomes an AIRPLANE");
                    specialKind = SPECIAL_AIRPLANE;
                }
                // AIRPLANE: a swap word with no special of its own (short, not crossing) carries the swap's airplane.
                if (specialKind == SPECIAL_NONE && AirplanePending && SpecialTilesActive && !firesSpecial && LastSwapCells != null && pw.Cells != null)
                    foreach (var sc in LastSwapCells)
                    {
                        if (!pw.Cells.Contains(sc) || !InBounds(sc.x, sc.y)) continue;
                        var sd = _board[sc.x, sc.y];
                        if (sd == null || sd.IsFrozen || sd.IsDropTarget || sd.SpecialKind != SPECIAL_NONE || specialSurvivors.Contains(sc)) continue;
                        specialKind = SPECIAL_AIRPLANE; specialAt = sc; AirplanePending = false; break;
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
                            // GEM IN THE WORD COUNTS (2026-10-28 Spencer: "since you already made a word with it, it should count").
                            // The survivor tile used to keep its gem flag, hidden under the special's art. Collect it now (the gem's
                            // flight to the goal plays from this cell - HandManager, SpecialsCreated).
                            if (_board[cell.x, cell.y].HasGem)
                            {
                                _board[cell.x, cell.y].HasGem = false;
                                if (LevelRules.HasGemGoal && !(SurvivalManager.AwaitingNextLevel && !Simulating))
                                {
                                    LevelRules.Collected++; LevelRules.CollectAwaitingFly++; LevelRules.CollectAwaitingSince = Time.unscaledTime;
                                    _gemsOnSpecials.Add(cell);
                                    WDLog.Log($"[Goal] collected 1 gem at special birth ({cell.x},{cell.y}) -> {LevelRules.Collected}");
                                }
                            }
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
                LevelRules.OnWordPopped(pw.Word, pw.Cells);   // theme-word goal (2026-10-22)
                if (pw.Word != null && pw.Cells != null) _poppedWords.Add(new KeyValuePair<string, List<Vector2Int>>(pw.Word, new List<Vector2Int>(pw.Cells)));
                _primedRegistry.RemovePrimedWord(pid);
                _stepJustPrimed.Remove(pid);

                // Escalate chain depth per cluster word — load-bearing for
                // splash/meltdown/burst gates that expect chain depth to climb
                // across a cluster.
                _stepChainDepth++;
            }

            _stepTotalScore += detonationBonus;

            // SPECIAL TILES FIRE: every special in a popping word (and any special caught in a blast) clears its area.
            // Wrapped as a local function (2026-10-21) so it can run AGAIN after the junk splash / 7-letter row clear:
            // a special those passes hit used to be DELETED without firing (Spencer: "an explosion blew up an adjacent
            // rocket tile and it didnt fire it" — REES's 1-tile splash took the rocket at (6,3)).
            void FireQueuedSpecials()
            {
                if (specialQueue.Count > 0)
                {
                    var exploded = new HashSet<Vector2Int>(allExplodedCells);
                    int guard = 0;
                    while (specialQueue.Count > 0 && guard++ < 64)
                    {
                        var f = specialQueue.Dequeue();
                        SpecialFiredThisMove = true;
                        if (Simulating && f.Kind >= 0 && f.Kind < SimFiredByKind.Length) SimFiredByKind[f.Kind]++;   // bot report
                        f.Cleared = new List<Vector2Int>();
                        var area = new List<Vector2Int>();
                        // POWERED = fired by a real word the player made (2026-10-21 Spencer: double-tap paid the same as making a
                        // word "think that making a word should raise the power of it?"). Basic = double-tap / caught in a blast.
                        //   rocket: one line -> powered: row AND column    bomb: 3x3 -> powered: 5x5
                        //   disco:  most common letter on the board -> powered: every letter of the word
                        bool powered = !string.IsNullOrEmpty(f.Word) && f.Word.Length > 1;
                        bool rocketRow = f.Kind == SPECIAL_ROCKET_H || (powered && f.Kind == SPECIAL_ROCKET_V);
                        bool rocketCol = f.Kind == SPECIAL_ROCKET_V || (powered && f.Kind == SPECIAL_ROCKET_H);
                        if (f.Kind == SPECIAL_COMBO_CROSS)
                        {
                            for (int x = 0; x < COLS; x++) area.Add(new Vector2Int(x, f.Cell.y));
                            for (int y = 0; y < ROWS; y++) area.Add(new Vector2Int(f.Cell.x, y));
                        }
                        else if (f.Kind == SPECIAL_COMBO_BIGCROSS)
                        {
                            for (int d = -1; d <= 1; d++)
                            {
                                for (int x = 0; x < COLS; x++) area.Add(new Vector2Int(x, f.Cell.y + d));
                                if (!SMALL_BOARD || d == 0) for (int y = 0; y < ROWS; y++) area.Add(new Vector2Int(f.Cell.x + d, y));   // small board: 3 rows + 1 column (43%, was 62%)
                            }
                        }
                        else if (f.Kind == SPECIAL_COMBO_MEGA)
                        {
                            int mr = SMALL_BOARD ? 3 : 4;   // small board: 7x7 (78% of 7x9 — the 9x9 was the whole board)
                            for (int dx = -mr; dx <= mr; dx++) for (int dy = -mr; dy <= mr; dy++) area.Add(new Vector2Int(f.Cell.x + dx, f.Cell.y + dy));   // stays bigger than a powered bomb
                        }
                        else if (f.Kind == SPECIAL_COMBO_DISCO_DISCO)
                        {
                            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++) area.Add(new Vector2Int(x, y));
                        }
                        else if (f.Kind == SPECIAL_COMBO_DISCO)
                        {
                            // every tile of the commonest letter becomes the partner special and fires (rockets pick a random axis)
                            var cnt = new Dictionary<char, int>();
                            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                            { var d0 = _board[x, y]; if (d0 == null || d0.Letter == '\0' || d0.IsStone) continue; char L = char.ToUpperInvariant(d0.Letter); cnt.TryGetValue(L, out int n0); cnt[L] = n0 + 1; }
                            char best = '\0'; int bn = 0; foreach (var kv in cnt) if (kv.Value > bn) { bn = kv.Value; best = kv.Key; }
                            area.Add(f.Cell);
                            for (int x = 0; x < COLS; x++) for (int y = 0; y < ROWS; y++)
                            {
                                var d0 = _board[x, y]; var p0 = new Vector2Int(x, y);
                                if (d0 == null || d0.IsStone || p0 == f.Cell || char.ToUpperInvariant(d0.Letter) != best) continue;
                                int k = PendingComboPartner;
                                if (IsRocket(k)) k = SurvivalRng.Value < 0.5f ? SPECIAL_ROCKET_H : SPECIAL_ROCKET_V;
                                if (k == SPECIAL_DISCO) k = SPECIAL_BOMB;
                                d0.SpecialKind = k;   // fires when its own blast queue entry runs
                                specialQueue.Enqueue(new SpecialFire { Cell = p0, Kind = k, Letter = d0.Letter });
                            }
                        }
                        else if (f.Kind == SPECIAL_COMBO_PLANES)
                        {
                            for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) area.Add(f.Cell + new Vector2Int(dx, dy));
                            for (int i = 0; i < COMBO_PLANES_COUNT; i++)
                                specialQueue.Enqueue(new SpecialFire { Cell = f.Cell, Kind = SPECIAL_AIRPLANE, Letter = f.Letter, Delay = (i + 1) * AIRPLANE_COMBO_STAGGER });
                        }
                        else if (f.Kind == SPECIAL_COMBO_PLANE_BOMB)
                        {
                            foreach (var o in new[] { Vector2Int.zero, Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down }) area.Add(f.Cell + o);
                            var t = PickAirplaneTarget(f.Cell, exploded, area);
                            if (t.x >= 0)
                            {
                                f.Target = t;
                                int r = BOMB_RADIUS_POWERED; bool diamond = SMALL_BOARD && BOARD_PRESET != 1;   // 11-03: 8x10 powered bomb = 5x5 (as above)
                                for (int dx = -r; dx <= r; dx++) for (int dy = -r; dy <= r; dy++)
                                    if (!diamond || Mathf.Abs(dx) + Mathf.Abs(dy) <= r) area.Add(new Vector2Int(t.x + dx, t.y + dy));
                                WDLog.Log($"[Airplane] carries a BOMB ({f.Cell.x},{f.Cell.y}) -> ({t.x},{t.y})");
                            }
                        }
                        else if (f.Kind == SPECIAL_AIRPLANE)
                        {
                            foreach (var o in new[] { Vector2Int.zero, Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down }) area.Add(f.Cell + o);
                            var t = PickAirplaneTarget(f.Cell, exploded, area);
                            if (t.x >= 0) { area.Add(t); f.Target = t; WDLog.Log($"[Airplane] flies ({f.Cell.x},{f.Cell.y}) -> ({t.x},{t.y})"); }
                        }
                        else if (f.Kind == SPECIAL_ROCKET_H || f.Kind == SPECIAL_ROCKET_V)
                        {
                            if (rocketRow) for (int x = 0; x < COLS; x++) area.Add(new Vector2Int(x, f.Cell.y));
                            if (rocketCol) for (int y = 0; y < ROWS; y++) area.Add(new Vector2Int(f.Cell.x, y));
                        }
                        else if (IsBombLike(f.Kind))
                        {
                            // 2026-10-22 Spencer: "the blast radius of the bombs might be a bit much". Royal Match TNT is 5x5 however
                            // it is set off. Ours went 7x7 when powered — and most bombs fire powered (a word uses the letter), so a
                            // typical blast took 38-44 of 99 tiles. Now 5x5 both ways (max 24).
                            int r = powered ? BOMB_RADIUS_POWERED : BOMB_RADIUS_BASIC;
                            // SMALL BOARD (2026-10-23 Spencer: "the radius of the blasts too takes up more of the board right?"). A 5x5 is
                            // 40% of 7x9. Basic -> 3x3 (14%); powered -> a radius-2 DIAMOND (13 tiles, 21% — the 9x11 board's 5x5 was 25%).
                            bool diamond = SMALL_BOARD && powered;
                            if (SMALL_BOARD && !powered) r = 1;
                            // 8x10 (11-03): the 7x9 cuts made a bomb clear LESS than a rocket (3x3 = 9 vs a line of 8-10; diamond 13 vs a
                            // powered rocket's cross ~17) - but a bomb is the longer-word reward. 8x10: basic = radius-2 DIAMOND (13, 16%),
                            // powered = 5x5 (25, 31% - Royal Match's TNT). Rocket < bomb < combos < disco. 7x9 keeps the cuts above.
                            if (BOARD_PRESET == 1) { r = 2; diamond = !powered; }
                            for (int dx = -r; dx <= r; dx++) for (int dy = -r; dy <= r; dy++)
                                if (!diamond || Mathf.Abs(dx) + Mathf.Abs(dy) <= r) area.Add(new Vector2Int(f.Cell.x + dx, f.Cell.y + dy));
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
                                // 11-01 (Spencer: "used a disco ball that had an A and it didnt eliminate all the As"): the badge letter is
                                // the promise - a basic disco clears ITS OWN letter. If that's fewer than DISCO_MIN_TILES on the board,
                                // it also takes the most common other letter, so a Q / J disco is never a dud.
                                char own = char.ToUpperInvariant(f.Letter);
                                int ownN = counts.TryGetValue(own, out int on0) ? on0 : 0;
                                set = own.ToString();
                                if (SPECIALS_ARE_WILD) { own = '\0'; ownN = 0; set = ""; }   // 11-03: no letter shown on the disco any more -> the most common letter (RM light ball)
                                if (ownN < DISCO_MIN_TILES)
                                {
                                    char best = own; int bestN = -1;
                                    foreach (var kv in counts) if (kv.Key != own && kv.Value > bestN) { bestN = kv.Value; best = kv.Key; }
                                    if (best != own) set += best;
                                }
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
                            if (d.ShelfPart > 0) { ShelfHit(p, d); continue; }   // BOOKSHELF: a blast knocks one book (the shelf clears itself on the last)
                            if (d.TreasureHits > 0) { if (!TreasureHit(p, d)) continue; _stepTotalScore += ICE_BOUNTY; }   // treasure: one band per hit, opens on the last
                            else if (d.IsStone && SurvivalManager.FullBoardActive) { if (!CrateHit(p, d)) continue; CratesBroken++; _stepTotalScore += ICE_BOUNTY; }   // full board: specials smash crates (Royal Match boxes); 11-03 multi-hit: one layer per blast
                            // A special's blast takes ICE too (2026-10-21 Spencer: a rocket on the ice level left frozen tiles in its
                            // row — "it should destroy the whole row"). Candy Crush / Royal Match specials break blockers in their
                            // path; the "only a word melts ice" rule stays for ordinary splash. Pays the ice bounty.
                            if (d.IsFrozen) { _stepTotalScore += ICE_BOUNTY; d.IsFrozen = false; }   // destroyed, not thawed — no defrost anim on a dying tile
                            if (d.SpecialKind != SPECIAL_NONE) specialQueue.Enqueue(new SpecialFire { Cell = p, Kind = d.SpecialKind, Letter = d.Letter });
                            if (d.IsRainGold && LevelRules.BonusLevelActive) (f.Gold ??= new List<Vector2Int>()).Add(p);   // GOLD RUSH coin tile
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
            }
            FireQueuedSpecials();

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
                                        SimObjectives?.NotifyWordExploded(primedAt[p].Word, primedAt[p].OwnerPlayer, primedAt[p].PrimedByEdit);
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
                            SimObjectives?.NotifyWordExploded(pw.Word, pw.OwnerPlayer, pw.PrimedByEdit);

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

            // Seed the first pass from cells already in allExplodedCells - minus the cells a SPECIAL's blast cleared, unless
            // SPECIAL_BLAST_HITS_NEIGHBOURS (11-02: a rocket was hitting the goal pieces in the rows beside its line)
            HashSet<Vector2Int> blastOnly = null;
            if (!SPECIAL_BLAST_HITS_NEIGHBOURS)
                foreach (var sf in specialsFired) if (sf.Cleared != null) foreach (var c in sf.Cleared) (blastOnly ??= new HashSet<Vector2Int>()).Add(c);
            var currentSources = new List<Vector2Int>(allExplodedCells.Count);
            for (int i = 0; i < allExplodedCells.Count; i++)
                if (blastOnly == null || !blastOnly.Contains(allExplodedCells[i])) currentSources.Add(allExplodedCells[i]);

            bool stoneChainPropagate = GameManager.IsLevelMode;
            const int STONE_CHAIN_SAFETY_CAP = 50;
            int stoneChainIterations = 0;
            int[] sdx = { 1, -1, 0, 0, 1, 1, -1, -1 };
            int[] sdy = { 0, 0, 1, -1, 1, -1, 1, -1 };   // 4..7 = diagonals: LONG words only (below)
            // LONG WORDS REACH DIAGONALLY (2026-10-30, Spencer "sure" to option A): crates sink to the bottom rows where few words touch
            // them, so a word of LONG_WORD_DIAGONAL+ letters also breaks the crates DIAGONALLY next to its letters (first pass only).
            // Rewards the long word; 4-letter words keep the plain 4-side rule.
            var longCells = new HashSet<Vector2Int>();
            if (LONG_WORD_DIAGONAL > 0 && SurvivalManager.FullBoardActive && _poppedWords != null)
                foreach (var kv in _poppedWords) if (kv.Key != null && kv.Key.Length >= LONG_WORD_DIAGONAL && kv.Value != null) foreach (var c in kv.Value) longCells.Add(c);

            while (currentSources.Count > 0 && stoneChainIterations < STONE_CHAIN_SAFETY_CAP)
            {
                stoneChainIterations++;
                var nextSources = new List<Vector2Int>();

                for (int ec = 0; ec < currentSources.Count; ec++)
                {
                    var cell = currentSources[ec];
                    bool diagCrates = CRATE_DIAGONAL && SurvivalManager.FullBoardActive && stoneChainIterations == 1;
                    int dirs = (stoneChainIterations == 1 && (longCells.Contains(cell) || diagCrates)) ? 8 : 4;
                    for (int d = 0; d < dirs; d++)
                    {
                        int sx = cell.x + sdx[d];
                        int sy = cell.y + sdy[d];
                        if (!InBounds(sx, sy)) continue;
                        var stonePos = new Vector2Int(sx, sy);
                        // CRATE_DIAGONAL: a diagonal step only reaches crates; anything else there is left for its own 4-side check
                        if (d >= 4 && diagCrates && !longCells.Contains(cell) && !(_board[sx, sy] != null && _board[sx, sy].IsStone && !_board[sx, sy].IsDropTarget)) continue;
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
                            if (adj.ShelfPart > 0) { ShelfHit(stonePos, adj); continue; }   // BOOKSHELF: a touching word knocks one book; never chains
                            bool wasTreasure = adj.TreasureHits > 0;
                            if (wasTreasure && !TreasureHit(stonePos, adj)) continue;   // lost a band, stays (and doesn't chain)
                            if (!wasTreasure && SurvivalManager.FullBoardActive && !CrateHit(stonePos, adj)) continue;   // 11-03 multi-hit crate: lost a layer, stays
                            _board[sx, sy] = null;
                            stoneCleared.Add(stonePos);
                            stoneClearCount++;
                            if (SurvivalManager.FullBoardActive) { if (!wasTreasure) CratesBroken++; _stepTotalScore += ICE_BOUNTY; }
                            nextSources.Add(stonePos);
                        }
                        // ICE BESIDE A WORD (2026-10-30, Spencer "sure"): a word cracks the ice it TOUCHES, like crates (and Royal Match's
                        // ice). Before, only a word THROUGH the frozen letter melted it - the bot found ice goals unreachable 50-66% of
                        // turns (L9-11). First pass only (from the word's own cells), never chained.
                        else if (ICE_ADJACENT_THAW && SurvivalManager.FullBoardActive && stoneChainIterations == 1 && adj != null && adj.IsFrozen)
                            TryThawCell(stonePos, thawedCells);
                        // GEM BESIDE A WORD (2026-11-02, Spencer: endings "always come down to the last piece and then the game has to
                        // switch up tiles" / "feels like the game is giving you the win"). A gem only counted when a word ran THROUGH it,
                        // so the last gem jammed and the goal re-deal had to rescue it. Now a word collects the gems it TOUCHES too -
                        // one rule for every goal piece (crates, ice, gems). The tile survives as a plain letter; the gem flies to the
                        // goal through the same path as a gem collected at a special's birth (_gemsOnSpecials -> ApplySpecialStep).
                        else if (GEM_ADJACENT_COLLECT && SurvivalManager.FullBoardActive && stoneChainIterations == 1 && adj != null && adj.HasGem
                                 && !adj.IsStone && LevelRules.HasGemGoal && !(SurvivalManager.AwaitingNextLevel && !Simulating) && !_gemsOnSpecials.Contains(stonePos)
                                 && !allExplodedCells.Contains(stonePos))
                        {
                            adj.HasGem = false;
                            if (preGems != null) preGems[sx, sy] = false;   // a later blast this step must not count it a second time
                            LevelRules.Collected++; LevelRules.CollectAwaitingFly++; LevelRules.CollectAwaitingSince = Time.unscaledTime;
                            _gemsOnSpecials.Add(stonePos);
                            if (!Simulating) WDLog.Log($"[Goal] collected 1 gem beside a word ({sx},{sy}) -> {LevelRules.Collected}");
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
            foreach (var q in _shelfClearedCells) if (!allExplodedCells.Contains(q)) allExplodedCells.Add(q);   // BOOKSHELF emptied this step
            // if (stoneClearCount > 0)
//                 WDLog.Log($"[RulesEngine] DoExplode: cleared {stoneClearCount} stone tile(s) via splash damage");

            // ── Junk splash: scales with PRIMED word length ──
            // 3-letter: 0 splash (tactical glue, not artillery)
            // 4-letter: 1 splash
            // 5-letter: 2 splash
            // 6-letter: 3 splash
            // 7-letter: 4 splash + full row clear
            if (JunkSplashEnabled && SurvivalManager.IsSurvivalMode && !(STRICT_FULL_BOARD && SurvivalManager.FullBoardActive))
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
                    var jd = _board[jPos.x, jPos.y];
                    if (jd != null && jd.SpecialKind != SPECIAL_NONE)   // a splash-hit special FIRES (below), it isn't just deleted
                        specialQueue.Enqueue(new SpecialFire { Cell = jPos, Kind = jd.SpecialKind, Letter = jd.Letter });
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

            if (specialQueue.Count > 0) FireQueuedSpecials();   // specials the splash caught

            // ── 7-letter jackpot: clear the entire row of the longest primed word ──
            if (longestPrimedWord >= 7 && SurvivalManager.IsSurvivalMode && !(STRICT_FULL_BOARD && SurvivalManager.FullBoardActive))
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
                            var rd = _board[col, clearRow];
                            if (rd.SpecialKind != SPECIAL_NONE) specialQueue.Enqueue(new SpecialFire { Cell = pos, Kind = rd.SpecialKind, Letter = rd.Letter });
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

            if (specialQueue.Count > 0) FireQueuedSpecials();   // specials the 7-letter row clear caught
            _stepPendingTriggers = null; // safe to null now — row-clear logic above is done

            // Purge scored word keys AFTER all splash damage finalized
            PurgeScoredKeysForCells(allExplodedCells);
            PurgeStepScoredKeysForCells(allExplodedCells);
            if (LevelRules.HasGrassGoal && !(SurvivalManager.AwaitingNextLevel && !Simulating)) MowGrass(allExplodedCells);
            // After the win (bonus blast) nothing counts toward the finished goals — and so nothing new is owed a HUD flight.
            if (SurvivalManager.AwaitingNextLevel && !Simulating) { preLetters = null; preGems = null; }
            if (preLetters != null)
            {
                int got = 0;
                foreach (var c in allExplodedCells) if (InBounds(c.x, c.y) && preLetters[c.x, c.y] == collectL) got++;
                if (got > 0) { LevelRules.Collected += got; LevelRules.CollectAwaitingFly += got; LevelRules.CollectAwaitingSince = Time.unscaledTime; WDLog.Log($"[Goal] collected {got} '{collectL}' -> {LevelRules.Collected}"); }
            }
            if (preGems != null)
            {
                int gg = 0;
                var where = new System.Text.StringBuilder();
                foreach (var c in allExplodedCells) if (InBounds(c.x, c.y) && preGems[c.x, c.y]) { gg++; where.Append($" ({c.x},{c.y})"); }
                if (gg > 0) { LevelRules.Collected += gg; LevelRules.CollectAwaitingFly += gg; LevelRules.CollectAwaitingSince = Time.unscaledTime; WDLog.Log($"[Goal] collected {gg} gem(s) at{where} -> {LevelRules.Collected}"); }
            }
            ExplodedTilesTotal += allExplodedCells.Count;   // running count — bonus coins read the delta (full board refills, so occupancy can't)

            // if (swapRefillCount > 0 || editRefillCount > 0 || wildRefillCount > 0)
//                 WDLog.Log($"[RulesEngine] DoExplode: refills collected — swap={swapRefillCount} edit={editRefillCount} wild={wildRefillCount}");

            return new StepResult
            {
                Phase = ResolutionPhase.Exploding,
                ExplodedCells = allExplodedCells,
                FormedWordCells = formedWordCells.Count > 0 ? new List<Vector2Int>(formedWordCells) : null,
                ThawedCells = thawedCells,
                PoppedWords = _poppedWords.Count > 0 ? _poppedWords : null,
                GemsCollectedOnSpecials = _gemsOnSpecials.Count > 0 ? _gemsOnSpecials : null,
                TreasureHitCells = _treasureHitCells.Count > 0 ? _treasureHitCells : null,
                CrateHitCells = _crateHitCells.Count > 0 ? _crateHitCells : null,
                ShelfHits = _shelfHits.Count > 0 ? _shelfHits : null,
                TreasureOpenedCells = _treasureOpenedCells.Count > 0 ? _treasureOpenedCells : null,
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
                if (FullBoardActive && FALL_SEEDS_MOVED_ONLY) AddFallSeeds(gravityMoves, _stepSeedCells);
                else
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
        /// <summary>GEM (2026-10-22): this cell carries a collectable gem (gem goal). Travels like SpecialKind.</summary>
        public bool HasGem        { get; set; }
        // CHEST TIER (2026-06-12): the word-LENGTH "key" required to crack this vault via the
        // adjacent-detonation stone-splash. 0 = regular (any word), 4 = mid, 5 = high (the
        // telegraphed jackpot). Gated in DoExplode; telegraphed on the tile (bronze/silver/gold).
        // Replaces the old random IsSpecialVault.
        public int RequiredWordLength { get; set; }
        /// <summary>TREASURE (2026-10-27, Spencer: "its own type of tile. a treasure tile ... added randomly for extra rewards").
        /// 0 = not a treasure. >0 = a locked treasure blocker (also IsStone) with this many HITS LEFT (3 = two gold bands, 2 = one,
        /// 1 = bare). Each adjacent pop / special blast takes ONE hit per resolution step; the last hit OPENS it -> coins.
        /// Never a goal: CountCrateCells ignores it.</summary>
        public int TreasureHits { get; set; }
        /// <summary>MULTI-HIT CRATE (2026-11-03, Spencer: X-brace -> one diagonal -> bare slats). Layers left on a crate (IsStone,
        /// TreasureHits 0): 0/1 = breaks on the next hit (today's crate), 2 = one plank left, 3 = full X. A word beside it, a special's
        /// blast or a booster each take ONE layer per resolution step; the crate goal counts it only when it is gone.</summary>
        public int CrateHP { get; set; }
        /// <summary>BOOKSHELF (2026-11-03, Spencer's library piece): a 2x2 ANCHORED blocker made of four stone cells sharing ShelfId.
        /// ShelfPart 0 = not a shelf; 1 = bottom-left (draws the whole shelf), 2 = bottom-right, 3 = top-left, 4 = top-right.
        /// ShelfBooks = books left (same on all four parts). Each word touching it / blast through it knocks ONE book per
        /// resolution step to the Books goal; the last book clears all four cells. Never a crate (CountCrateCells skips it).</summary>
        public int ShelfPart { get; set; }
        public int ShelfId { get; set; }
        public int ShelfBooks { get; set; }
        public int ShelfStyle { get; set; }   // art: 0 = rounded frame, 1 = ornate bookcase (Tile.SHELF_SLOT_SETS)
        /// <summary>11-03 SAFE: the same 2x2 piece with a different payload. 0 = bookshelf (each hit = a book to the Books goal),
        /// 1 = safe (ShelfBooks = hits left; each hit lights a lamp, the last opens it -> Safes goal + SAFE_COINS).</summary>
        public int ShelfKind { get; set; }
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
