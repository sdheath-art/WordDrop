using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// First-time tutorial = LEVEL 1. Target: "Explode 4 words" (LongWordObjective(2,4) shown in the
    /// HUD TARGET box). Taught as 4 gated, guided charge→explode cycles, drop-only. Reworked 2026-06-25.
    ///
    /// Each cycle (see CYCLES[]) authors a FRESH full board (deterministic; nothing auto-drops):
    ///   CHARGE step (state WaitForWord): drop Drop1 into the gap (col 2) → the Charge word forms
    ///     across row 0 → it CHARGES. (Cycle 0 then names the state once: "THE FUSE IS LIT".)
    ///   EXPLODE step (state WaitForDetonation): drop Drop2 into col 4 → the Blast word forms
    ///     vertically on the pre-placed tile, crossing the charged word's shared (Link) tile →
    ///     DETONATION → +1 toward the target.
    /// After each blast: count it, tick the TARGET badge down, then either re-author the next cycle
    /// ("AGAIN!") or, at 4/4, flash complete → "LEVEL COMPLETE!" → mark tutorial done → normal game.
    ///
    /// Cycles: CAT→TIP, DOG→GAP, SUN→NET, BUG→GAS (all words + safety bounds dictionary-verified).
    /// While IsActive, ObjectiveManager stands down so the tutorial owns the TARGET + completion.
    ///
    /// (SetupStep2 + the WaitForDrop state are legacy from an earlier flow — no longer in the path.)
    /// </summary>
    public class TutorialManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────────

        public static TutorialManager Instance { get; private set; }

        // ── Tutorial restrictions ──────────────────────────────────────────────────
        public static int AllowedColumn { get; private set; } = -1;
        public static int AllowedCardIndex { get; private set; } = -1;
        public static bool BlockShuffleAndSwap { get; private set; } = false;
        // EDIT-gating (2026-07-07): the ONLY board tile that may enter rewrite, and the ONLY hand letter that
        // may be stamped. -1 / '\0' = no edit gate. Enforced in HandManager.TryEnterRewriteMode/TryExecuteRewrite.
        public static int  AllowedEditCol    { get; private set; } = -1;
        public static int  AllowedEditRow    { get; private set; } = -1;
        public static char AllowedEditLetter { get; private set; } = '\0';
        public static int  AllowedSwapCol    { get; private set; } = -1; // board-swap step: the ONLY 2nd tile the swap may target
        public static int  AllowedSwapRow    { get; private set; } = -1;
        public static bool EditGateActive    { get; private set; } = false; // true during the L5 edit tutorial — disables the rewrite auto-timeout
        private const int  NO_INPUT = 99; // impossible column/card index → blocks all drops + card selection

        /// <summary>True when the current Survival level is a GATED tutorial level (scripted). Reads the level
        /// table directly, so it's timing-independent — covers the WHOLE level, even before the coaching/dim
        /// starts. Used to hard-lock hand reordering so a fresh player can't scramble the scripted rack and hit
        /// the ordering bugs. 2026-07-13 Spencer.</summary>
        /// <summary>True from the moment a scripted drop is consumed until the next step arms.
        /// Hand input must be HARD-blocked across this window. The tutorial calls
        /// SetInteractable(false) when the drop starts, but HandManager's resolution coroutines
        /// each end with IsInteractable = true (six sites), which stomps it and reopens input
        /// while the board is still animating and nothing is armed — so the player can drop a
        /// stray letter and desync the script. Checked directly in HandManager's input gate
        /// alongside levelLocked, which exists for exactly the same reason. 2026-07-29.</summary>
        public static bool GateClosed =>
            Instance != null && Instance._gateScript != null && Instance._gateAdvancing;

        public static bool CurrentLevelIsGated
        {
            get
            {
                if (!SurvivalManager.IsSurvivalMode || SurvivalManager.Instance == null) return false;
                return LevelTable.Get(SurvivalManager.Instance.CurrentStageIndex).Gated;
            }
        }

        /// <summary>
        /// The letter that should appear in the NEXT tile preview during tutorial.
        /// Set before each step so the preview stays consistent after DrawSlot's PreCacheNext.
        /// '\0' means no override.
        /// </summary>
        public static char NextPreviewLetter { get; private set; } = '\0';

        // ── State ─────────────────────────────────────────────────────────────────

        private enum TutorialStep
        {
            Inactive,
            WaitForDrop,        // Step 1: learn the input
            WaitForWord,        // Step 2: form a word
            ShowPrimed,         // Step 3: name the glow (no action)
            WaitForDetonation,  // Step 4: blow it up
            ShowBoom,           // Celebration
            Done
        }
        private TutorialStep _step = TutorialStep.Inactive;
        private static bool _skipForSession = false;
        private bool _transitioning = false;

        public bool IsActive => (_step != TutorialStep.Inactive && _step != TutorialStep.Done) || _gateScript != null || _editScript != null;

        // ── Gated-level coaching (2026-06-25) — input gating + hand_point on a REAL level ──
        private struct GateDrop { public char Letter; public int Col; public bool Charge; public Vector2Int[] Cells; public string Say; }
        // Level 1 solution (drop letter → column). Charge=true forms a word that charges; false detonates.
        // Cells = the word's cells (for the marching-ants hint outline, so it follows our order).
        private static readonly GateDrop[] LEVEL1_SCRIPT =
        {
            new GateDrop { Letter='C', Col=3, Charge=true,  Say="Make a word to charge it!",      Cells=new[]{ new Vector2Int(3,1), new Vector2Int(4,1), new Vector2Int(5,1) } }, // CAT  (row 1)
            new GateDrop { Letter='P', Col=5, Charge=false, Say="Now cross it to pop it!", Cells=new[]{ new Vector2Int(5,1), new Vector2Int(5,2), new Vector2Int(5,3) } }, // PIT  (col 5)
            new GateDrop { Letter='L', Col=0, Charge=true,  Say="Charge up another word!",               Cells=new[]{ new Vector2Int(0,1), new Vector2Int(0,2), new Vector2Int(0,3), new Vector2Int(0,4) } }, // LAND (col 0)
            new GateDrop { Letter='O', Col=1, Charge=false, Say="Cross it to pop it!",            Cells=new[]{ new Vector2Int(0,2), new Vector2Int(1,2), new Vector2Int(2,2) } }, // NOT  (row 2)
        };

        // Level 3 solution (the COMBO). Stack three charged words in a col-1 tower — they only TOUCH,
        // never share a cell, so they don't set each other off — then DAY triggers the whole cluster at
        // once (DAY overlaps STAR's A; TOW + ZIP are pulled in by the connected-group adjacency cascade).
        private static readonly GateDrop[] LEVEL3_SCRIPT =
        {
            new GateDrop { Letter='O', Col=1, Charge=true,  Say="Charge a word!",                    Cells=new[]{ new Vector2Int(0,1), new Vector2Int(1,1), new Vector2Int(2,1) } }, // TOW  (row 1) charges
            new GateDrop { Letter='S', Col=1, Charge=true,  Say="Stack a word on top!",                   Cells=new[]{ new Vector2Int(1,2), new Vector2Int(2,2), new Vector2Int(3,2), new Vector2Int(4,2) } }, // STAR (row 2) charges
            new GateDrop { Letter='I', Col=1, Charge=true,  Say="Stack one more!",                     Cells=new[]{ new Vector2Int(0,3), new Vector2Int(1,3), new Vector2Int(2,3) } }, // ZIP  (row 3) charges
            new GateDrop { Letter='D', Col=3, Charge=false, Say="Now cross the stack to pop them all!", Cells=new[]{ new Vector2Int(3,3), new Vector2Int(3,2), new Vector2Int(3,1) } }, // DAY  (col 3, vertical) → triggers all 3
        };
        private GateDrop[] _gateScript;
        private int  _gateStep = -1;
        private bool _gateAdvancing;
        public bool IsGating => _gateScript != null;

        // ── EDIT-gated coaching (2026-07-07) — teach the EDIT tool: gate tap-tile → tap-letter ──
        // A gated edit step is EITHER a hand-STAMP (tap tile → tap hand Letter) or a board-SWAP (tap tile → tap
        // a 2nd BOARD tile at SwapCol/SwapRow → the two letters swap). 2026-07-07 Spencer.
        // Say  = FIRST-tap prompt (pick the tile to change). Say2 = SECOND-tap prompt (shown once the
        // source tile is selected → pick what to swap it with). 2026-07-09 Spencer.
        private struct GateEdit { public int Col; public int Row; public char Letter; public bool IsSwap; public int SwapCol; public int SwapRow; public Vector2Int[] Cells; public string Say; public string Say2; }
        // Level 5 (EDIT teach) — teaches BOTH edit modes: STAMP a hand letter (steps 1-2) and SWAP two board
        // tiles (steps 3-4). Z→P=SWAP (stamp) · B→D=HAND (stamp, blast 1) · ANT (swap S↔T) · ANTS (swap H↔S, blast 2).
        private static readonly GateEdit[] LEVEL5_EDIT_SCRIPT =
        {
            new GateEdit { Col=3, Row=3, Letter='P', Say="Tap the letter to swap.", Say2="Now tap another to charge a word.", Cells=new[]{ new Vector2Int(0,3), new Vector2Int(1,3), new Vector2Int(2,3), new Vector2Int(3,3) } }, // Z→P = SWAP (stamp)
            new GateEdit { Col=2, Row=1, Letter='D', Say="Tap the letter to swap.", Say2="Now tap another to pop the words.", Cells=new[]{ new Vector2Int(2,4), new Vector2Int(2,3), new Vector2Int(2,2), new Vector2Int(2,1) } }, // B→D = HAND (stamp, blast 1)
            new GateEdit { Col=4, Row=1, IsSwap=true, SwapCol=0, SwapRow=1, Say="Perform another swap.", Say2="", Cells=new[]{ new Vector2Int(2,1), new Vector2Int(3,1), new Vector2Int(4,1) } },                     // ANT: swap S(4,1) ↔ T(0,1)
            new GateEdit { Col=5, Row=1, IsSwap=true, SwapCol=5, SwapRow=0, Say="Tap the letter to swap.", Say2="Now tap another to pop the words.", Cells=new[]{ new Vector2Int(2,1), new Vector2Int(3,1), new Vector2Int(4,1), new Vector2Int(5,1) } }, // ANTS: swap H(5,1) ↔ S(5,0)
        };
        private GateEdit[] _editScript;
        private int  _editStep = -1;
        private bool _editAdvancing;
        private Coroutine _editPointerCo;
        public bool IsGatingEdit => _editScript != null;

        // ── Level 1: target = "Explode 4 words", taught as 4 gated charge→explode cycles ──
        // Each cycle authors a fresh board: drop Drop1 into col 2 → Charge word (horizontal, row 0)
        // CHARGES; drop Drop2 into col 4 → Blast word (vertical, on the pre-placed Pre tile above
        // Link) crosses the charged word's Link tile → DETONATION (+1 toward the target). All words +
        // bounds dictionary-verified scan-safe (LBound+Charge and Charge+RBound are non-words).
        private struct WordCycle
        {
            public string Charge, Blast;   // Charge = Drop1 + Mid + Link  ;  Blast(vertical) = Link + Pre + Drop2
            public char Drop1, Mid, Link;  // Mid at (3,0), Link at (4,0)
            public char Pre, Drop2;        // Pre pre-placed at (4,1); Drop2 lands at (4,2)
            public char LBound, RBound;    // safe filler at (1,0)/(5,0) that can't extend Charge
        }
        private static readonly WordCycle[] CYCLES =
        {
            new WordCycle { Charge="CAT", Blast="TIP", Drop1='C', Mid='A', Link='T', Pre='I', Drop2='P', LBound='B', RBound='W' },
            new WordCycle { Charge="DOG", Blast="GAP", Drop1='D', Mid='O', Link='G', Pre='A', Drop2='P', LBound='X', RBound='X' },
            new WordCycle { Charge="SUN", Blast="NET", Drop1='S', Mid='U', Link='N', Pre='E', Drop2='T', LBound='X', RBound='X' },
            new WordCycle { Charge="BUG", Blast="GAS", Drop1='B', Mid='U', Link='G', Pre='A', Drop2='S', LBound='X', RBound='X' },
        };
        private const int GOAL_WORDS = 4;
        private int _cycleIndex = 0;
        private int _wordsExploded = 0;
        private LongWordObjective _levelObjective;

        private static RulesCellData NewCell(char letter, int col, int row)
            => new RulesCellData { Letter = letter, Col = col, Row = row, PlayerIndex = -1 };

        // ── Visual elements ───────────────────────────────────────────────────────

        private GameObject _instructionGO;
        private TMPro.TextMeshPro _instructionText;
        private GameObject _arrowGO;
        private Tweener    _arrowPulseTween;
        private GameObject _skipButtonCanvas;
        private GameObject _cardHighlightGO;
        private Tweener    _cardHighlightPulse;

        // ── Colors ────────────────────────────────────────────────────────────────

        private static readonly Color TEXT_COLOR  = new Color(1f, 1f, 1f, 1f);
        private static readonly Color ARROW_COLOR = new Color(0.2f, 0.85f, 0.4f, 0.8f);
        private static readonly Color BOOM_COLOR  = new Color(1f, 0.6f, 0.1f, 1f);

        // ── Auto-create ───────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("TutorialManager");
                go.AddComponent<TutorialManager>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LevelIntroModal.OnPlayStarted += OnLevelPlayStarted;   // start gated coaching when a level begins
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            LevelIntroModal.OnPlayStarted -= OnLevelPlayStarted;
            UnsubscribeEvents();
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnGatedTileDropped;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════════════

        public static bool ShouldRunTutorial()
        {
            // RETIRED 2026-06-25 — the tutorial is being rebuilt as REAL gated LEVELS at the front of
            // the level list (LevelTable + ObjectiveManager.InstallLevel), NOT this separate flow. So
            // this old intercept is OFF: the game now flows straight into the real levels (tutorial
            // Level 1 = Spencer's fixed board). The gating + hand_point coaching will be re-layered
            // onto those real levels (this class's AllowedColumn/RigNextDraw/hand_point helpers get
            // reused). See [[project_wordrop_tutorial_levels_2026_06_25]].
            return false;
        }

        public void BeginTutorial()
        {
            if (_step != TutorialStep.Inactive && _step != TutorialStep.Done)
            {
                Debug.LogWarning("[TutorialManager] Tutorial already running.");
                return;
            }

//             Debug.Log("[TutorialManager] === TUTORIAL BEGIN ===");

            // Level 1 target: explode 4 words. Taught as gated charge→explode cycles.
            _cycleIndex = 0;
            _wordsExploded = 0;
            _levelObjective = new LongWordObjective(2, GOAL_WORDS);

            _step = TutorialStep.WaitForWord;
            AllowedColumn = -1;
            AllowedCardIndex = -1;
            BlockShuffleAndSwap = true;
            _transitioning = false;

            SubscribeEvents();
            ShowSkipButton();
            // Show the TARGET box ("Explode 4 words"). The tutorial OWNS this objective + completion;
            // ObjectiveManager stands down while IsActive (see ObjectiveManager.Update guard).
            HUDManager.Instance?.SetObjective(_levelObjective);
            StartCoroutine(SetupCharge(0));
        }

        // ══════════════════════════════════════════════════════════════════════════
        // CHARGE STEP — "MAKE A WORD"  (state: WaitForWord)
        // Author a fresh full board for this cycle; the player drops Drop1 into col 2 to complete
        // the Charge word across row 0, which CHARGES. Nothing auto-drops — every tile is pre-placed.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupCharge(int cycleIndex)
        {
            yield return null; // wait a frame for everything to settle

            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            var cyc = CYCLES[cycleIndex];

            // Fresh, full, fixed board (deterministic + gated). Layout (row 0 = bottom):
            //   row2:  F  F  .  .  .  .
            //   row1:  F  F  .  F  Pre  F
            //   row0:  F  L  .  Mid Link R     (col 2 = Drop1 gap; col 4 above Pre = Drop2 gap)
            // Scan-safe: the scanner only reads the run through the DROPPED cell; L/R bound the Charge
            // word so no longer/other word can form through it; drop lanes + margins stay empty.
            rules.ClearBoard();
            grid.ClearAllCells();
            // Board-reset path → reset BOTH turn counters (HANDOFF §4.5): zeroing only GlobalTurn lets
            // MatchController._currentTurn slam a stale value back on the next drop, giving the fresh
            // charged word a bad fuse (primes RED / diffuses immediately) on later cycles.
            if (MatchController.Instance != null)
                MatchController.Instance.ResetTurnCounter();

            // — target words —
            rules.SetCell(3, 0, NewCell(cyc.Mid,  3, 0));
            rules.SetCell(4, 0, NewCell(cyc.Link, 4, 0));
            rules.SetCell(4, 1, NewCell(cyc.Pre,  4, 1)); // pre-placed; nothing auto-drops

            // — bounds + decorative filler (gravity-stable; never seeded by the two gated drops) —
            rules.SetCell(1, 0, NewCell(cyc.LBound, 1, 0)); // left bound of the Charge word
            rules.SetCell(5, 0, NewCell(cyc.RBound, 5, 0)); // right bound of the Charge word
            rules.SetCell(0, 0, NewCell('R', 0, 0));
            rules.SetCell(0, 1, NewCell('N', 0, 1));
            rules.SetCell(0, 2, NewCell('G', 0, 2));
            rules.SetCell(1, 1, NewCell('E', 1, 1));
            rules.SetCell(1, 2, NewCell('L', 1, 2));
            rules.SetCell(3, 1, NewCell('S', 3, 1));
            rules.SetCell(5, 1, NewCell('O', 5, 1));

            grid.RebuildFromRulesEngine(rules);

            // Hand: Drop1 in slot 0 (the only playable card); slot 0 refills to Drop2 after the drop.
            SetPlayerHand(new char[] { cyc.Drop1, 'X', 'R', 'E', 'L' });
            AllowedColumn = 2;
            AllowedCardIndex = 0;
            RigNextDraw(cyc.Drop2);
            NextPreviewLetter = cyc.Drop2;

            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            ShowInstruction(cycleIndex == 0 ? "DRAG TO MAKE A WORD" : "MAKE ANOTHER WORD");
            ShowCardHighlight(0);
            DimShuffle(true);
            // SPOTLIGHT DIM still shelved — reworking into a SELECTIVE spotlight is a later pass. The
            // guiding cursor + input-gating (which actually teach) stay on.
            ShowDragGestureToColumn(0, 2);

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            _transitioning = false;
//             Debug.Log($"[TutorialManager] Cycle {cycleIndex} charge ready — drop {cyc.Drop1} at col 2 → {cyc.Charge}.");
        }

        /// <summary>Loops the hand-point hint dragging from a hand card up into a board column,
        /// computed from real world positions (more robust than hardcoded viewport fractions).</summary>
        private void ShowDragGestureToColumn(int cardIndex, int col)
        {
            var grid = GridManager.Instance;
            var hand = HandManager.Instance;
            if (grid == null || hand == null) return;

            // Aim at the ACTUAL landing cell for this column (where the dropped tile comes to rest).
            // World-space cursor → the fingertip lands exactly here, no screen conversion needed.
            int landRow = RulesEngine.Instance != null ? RulesEngine.Instance.GetLowestEmptyRow(col) : 1;
            if (landRow < 0) landRow = RulesEngine.ROWS - 1;

            Vector3 cardW = new Vector3(hand.GetCardWorldX(cardIndex), hand.GetCardWorldY(), 0f);
            Vector3 colW  = grid.CellToWorld(col, landRow);
            TutorialSpotlight.ShowDragGesture(cardW, colW);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 2 — "NOW MAKE A WORD"
        // Board has O at (3,0). Place G at (4,0). Player drops D at (2,0) → DOG.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep2()
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            HideArrow();
            HideCardHighlight();
            HideInstruction();
            TutorialSpotlight.Hide();   // Step 2 will re-show its own focus once it's built out

            // Brief pause to let the drop settle
            yield return new WaitForSeconds(1.0f);

            // Drop G at (4,0) — animate it falling in like an AI move
            rules.SetCell(4, 0, new RulesCellData { Letter = 'G', Col = 4, Row = 0, PlayerIndex = -1 });
            grid.DropTile(4, 'G', TileOwner.AI);

            yield return new WaitForSeconds(0.5f);

            // Restrict to column 2, card 0 only
            AllowedColumn = 2;
            AllowedCardIndex = 0;

            // Force current player back to human (FullTurnSequence may have switched it)
            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            // Rig the next card deal so slot 0 refills with P after the D drop (for PIG in step 4)
            RigNextDraw('P');
            NextPreviewLetter = 'P';

            ShowInstruction("NOW MAKE A WORD");
            ShowArrow(2);
            ShowCardHighlight(0);

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            _transitioning = false;
//             Debug.Log("[TutorialManager] Step 2 ready — waiting for D drop at column 2 to form DOG.");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // AFTER CHARGE — verify the word charged; on the FIRST cycle, name the state.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator AfterCharge()
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            HideArrow();
            HideCardHighlight();
            HideInstruction();
            TutorialSpotlight.Hide();   // stop the charge-step hand_point

            // Wait for word scoring animation
            yield return new WaitForSeconds(1.2f);

            // Sync charged glow visuals
            grid.SyncToRulesState(rules);
            ApplyPrimedGlow(rules, grid);

            // Verify the word actually charged before asking for the blast.
            if (rules.PrimedRegistry == null || rules.PrimedRegistry.Count == 0)
            {
                Debug.LogError($"[TutorialManager] Cycle {_cycleIndex}: {CYCLES[_cycleIndex].Charge} not charged — aborting tutorial.");
                _transitioning = false;
                EndTutorial();
                StartNormalGame();
                yield break;
            }

            // Teach the "charged" state once (first cycle only); later cycles go straight to the blast.
            if (_cycleIndex == 0)
            {
                yield return new WaitForSeconds(0.3f);
                ShowInstruction("THE FUSE IS LIT");
                yield return new WaitForSeconds(2.0f);
                HideInstruction();
                yield return new WaitForSeconds(0.3f);
            }

            _step = TutorialStep.WaitForDetonation;
            StartCoroutine(SetupExplode(_cycleIndex));
        }

        // ══════════════════════════════════════════════════════════════════════════
        // EXPLODE STEP — "EXPLODE IT"  (state: WaitForDetonation)
        // The Pre tile is already above Link. Player drops Drop2 into col 4 → Blast word (vertical)
        // crosses the CHARGED word's Link → DETONATION. Nothing auto-drops.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupExplode(int cycleIndex)
        {
            HideInstruction();
            TutorialSpotlight.Hide();
            yield return new WaitForSeconds(0.2f);

            // Restrict to column 4, card 0 only (slot 0 was refilled with Drop2 after the first drop).
            AllowedColumn = 4;
            AllowedCardIndex = 0;

            ShowInstruction(cycleIndex == 0 ? "NOW POP IT" : "POP IT");
            ShowCardHighlight(0);
            ShowDragGestureToColumn(0, 4);   // hand_point guides to the detonating drop

            // Force current player back to human
            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            _transitioning = false;
//             Debug.Log($"[TutorialManager] Cycle {cycleIndex} explode ready — drop Drop2 at col 4 → Blast → detonation.");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // EVENT HANDLERS
        // ══════════════════════════════════════════════════════════════════════════

        private void SubscribeEvents()
        {
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnWordScored      += OnWordScored;
                RulesEngine.Instance.OnPrimedTriggered  += OnPrimedTriggered;
                RulesEngine.Instance.OnTileDropped      += OnTileDropped;
            }
        }

        private void UnsubscribeEvents()
        {
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnWordScored      -= OnWordScored;
                RulesEngine.Instance.OnPrimedTriggered  -= OnPrimedTriggered;
                RulesEngine.Instance.OnTileDropped      -= OnTileDropped;
            }
        }

        private void OnTileDropped(TileDroppedEvent evt)
        {
            if (_step != TutorialStep.WaitForDrop) return;
            if (_transitioning) return;

//             Debug.Log($"[TutorialManager] Tile dropped during step 1: '{evt.Letter}' at col {evt.Col}");

            // Step 1 complete — they learned to drop
            _step = TutorialStep.WaitForWord;
            _transitioning = true;
            StartCoroutine(SetupStep2());
        }

        private void OnWordScored(WordScoredEvent evt)
        {
            if (_step != TutorialStep.WaitForWord) return;
            if (_transitioning) return;

            // The Charge word formed → verify + (first cycle) name the charged state, then ask for the blast.
            _step = TutorialStep.ShowPrimed;
            _transitioning = true;
            StartCoroutine(AfterCharge());
        }

        private void OnPrimedTriggered(PrimedTriggeredEvent evt)
        {
            if (_step != TutorialStep.WaitForDetonation) return;
            if (_transitioning) return;

            _step = TutorialStep.ShowBoom;
            _transitioning = true;
            StartCoroutine(AfterBlast());
        }

        // ══════════════════════════════════════════════════════════════════════════
        // AFTER BLAST — count it toward the target; loop to the next cycle, or finish at 4/4.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator AfterBlast()
        {
            HideArrow();
            HideCardHighlight();
            HideInstruction();
            TutorialSpotlight.Hide();

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            // Count this explosion toward the level target + tick the TARGET badge down.
            _wordsExploded++;
            if (_levelObjective != null)
            {
                _levelObjective.OnWordExploded(CYCLES[_cycleIndex].Charge, MatchController.PLAYER_HUMAN);
                HUDManager.Instance?.SetObjective(_levelObjective);
                HUDManager.Instance?.PulseObjective();
            }
//             Debug.Log($"[TutorialManager] Cycle {_cycleIndex} blast — exploded {_wordsExploded}/{GOAL_WORDS}.");

            // Let the explosion read.
            yield return new WaitForSeconds(1.3f);

            if (_wordsExploded >= GOAL_WORDS)
            {
                HUDManager.Instance?.FlashObjectiveComplete();
                ShowInstruction("LEVEL COMPLETE!");
                yield return new WaitForSeconds(1.6f);
                HideInstruction();

                _skipForSession = true;
                PlayerPrefs.SetInt("tutorial_complete", 1);
                PlayerPrefs.Save();

                EndTutorial();
                yield return new WaitForSeconds(0.3f);
                StartNormalGame();
            }
            else
            {
                // Quick "again!" beat, then the next cycle on a fresh board.
                ShowInstruction("AGAIN!");
                yield return new WaitForSeconds(0.9f);
                HideInstruction();

                _cycleIndex++;
                _step = TutorialStep.WaitForWord;
                StartCoroutine(SetupCharge(_cycleIndex));
            }
        }

        private void EndTutorial()
        {
            _step = TutorialStep.Done;
            _transitioning = false;
            AllowedColumn = -1;
            AllowedCardIndex = -1;
            BlockShuffleAndSwap = false;
            NextPreviewLetter = '\0';

            UnsubscribeEvents();
            HideArrow();
            HideCardHighlight();
            HideInstruction();
            HideSkipButton();
            DimShuffle(false);
            TutorialSpotlight.Hide();   // never leave the dim stuck on screen (skip / end / abort)

//             Debug.Log("[TutorialManager] === TUTORIAL END ===");
        }

        private void StartNormalGame()
        {
//             Debug.Log("[TutorialManager] Starting normal game after tutorial.");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // GATED-LEVEL COACHING — training wheels on a REAL level (the level owns the
        // goal/modal/advance; this only rigs the hand, gates each drop, and guides with hand_point).
        // ══════════════════════════════════════════════════════════════════════════

        private const float LEVEL_ENTRY_DELAY = 1.05f; // let the board/HUD spring-in finish AND settle with breathing room before the tutorial dim

        private void OnLevelPlayStarted()
        {
            if (IsGating || IsGatingEdit) return;
            if (!SurvivalManager.IsSurvivalMode || SurvivalManager.Instance == null) return;
            int stage = SurvivalManager.Instance.CurrentStageIndex;
            var entry = LevelTable.Get(stage);

            // Rig the opening hand NOW (before the entry animation) so the tile holder slides in showing the EXACT
            // planned letters. For gated-drop levels the opening hand is derived straight from the coaching script —
            // the same { firstLetter, X, R, E, L } hand + NEXT preview that SetupGateStep(0) sets ~1s later — so when
            // the coaching fires nothing visibly changes. Non-gated levels fall back to the authored RigHand string.
            // (SetHand refreshes letters in place, so this doesn't disturb the parked cards.) 2026-07-13 Spencer.
            var openingGate = entry.Gated ? GetGateScript(stage) : null;
            if (openingGate != null && openingGate.Length > 0)
            {
                SetPlayerHand(new char[] { openingGate[0].Letter, 'X', 'R', 'E', 'L' });
                if (openingGate.Length > 1)
                {
                    RigNextDraw(openingGate[1].Letter);        // idempotent (SetCachedNextLetter) — SetupGateStep re-sets it
                    NextPreviewLetter = openingGate[1].Letter;
                }
                HandManager.Instance?.UpdateNextTilePreview();
            }
            else if (!string.IsNullOrEmpty(entry.RigHand))
                SetPlayerHand(entry.RigHand.ToUpperInvariant().ToCharArray());

            // GATING / coaching starts AFTER the board+HUD spring-in, so the entry animation plays first and the
            // tutorial dim can't cover or interrupt it. Non-gated levels just get the rigged hand above. 2026-07-13.
            if (entry.Gated)
                StartCoroutine(BeginGatingAfterEntry(stage));
        }

        private System.Collections.IEnumerator BeginGatingAfterEntry(int stage)
        {
            yield return new WaitForSecondsRealtime(LEVEL_ENTRY_DELAY);
            if (IsGating || IsGatingEdit) yield break; // a gate already started during the wait — don't double-fire

            if (stage == TutorialLocks.EDIT_UNLOCK_LEVEL) // L5 EDIT tutorial
            {
                BeginGatedEditLevel(LEVEL5_EDIT_SCRIPT);
                yield break;
            }
            var script = GetGateScript(stage);
            if (script != null && script.Length > 0) BeginGatedLevel(script);
        }

        // Gated tutorial levels: stage 1 = prime→explode, stage 3 = the combo (stack a cluster, detonate all).
        private GateDrop[] GetGateScript(int stage) =>
            stage == 1 ? LEVEL1_SCRIPT :
            stage == 3 ? LEVEL3_SCRIPT : null;

        // ── EDIT-gated level flow (mirrors the drop-gated flow, but for the EDIT tool) ──
        private void BeginGatedEditLevel(GateEdit[] script)
        {
            _editScript = script;
            _editStep = 0;
            _editAdvancing = false;
            EditGateActive = true; // no rewrite timeout — the player can take their time on the tutorial edit
            // Edits must WORK here (BlockShuffleAndSwap would block TryEnterRewriteMode) so leave it false —
            // boosters + hand-swap are locked via TutorialLocks anyway. Advance on each completed rewrite.
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnRewriteUsed -= OnGatedRewriteUsed;
                MatchController.Instance.OnRewriteUsed += OnGatedRewriteUsed;
            }
            HandManager.OnBoardSwapDone -= OnGatedBoardSwap;
            HandManager.OnBoardSwapDone += OnGatedBoardSwap;
            Debug.Log($"[TutorialGate] BEGIN edit-gated level — {script.Length} guided edits.");
            SetupEditGateStep(0);
        }

        private void SetupEditGateStep(int i)
        {
            if (_editScript == null) return;
            if (i >= _editScript.Length) { EndGatedEditLevel(); return; }

            GateEdit e = _editScript[i];
            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            // Lock input to ONLY this edit: no drops, no card selection, only THIS tile enters rewrite.
            AllowedColumn     = NO_INPUT;
            AllowedCardIndex  = NO_INPUT;
            AllowedEditCol    = e.Col;
            AllowedEditRow    = e.Row;
            if (e.IsSwap)
            {
                // Board-SWAP step: block hand-stamps ('#' matches no hand card), allow ONLY the scripted 2nd tile.
                AllowedEditLetter = '#';
                AllowedSwapCol    = e.SwapCol;
                AllowedSwapRow    = e.SwapRow;
            }
            else
            {
                // Hand-STAMP step: only THIS letter may be stamped; no board-swap.
                AllowedEditLetter = char.ToUpperInvariant(e.Letter);
                AllowedSwapCol    = -1;
                AllowedSwapRow    = -1;
            }

            ShowInstruction(!string.IsNullOrEmpty(e.Say) ? e.Say : (e.IsSwap ? "SWAP TWO TILES" : "SWAP A TILE"));
            if (HintManager.Instance != null && e.Cells != null)
            {
                // STAMP: bounce the hand card holding the target letter. SWAP: no card bounce (board-to-board). 2026-07-07.
                int hintCard = e.IsSwap ? -1 : System.Math.Max(0, FindHandSlot(char.ToUpperInvariant(e.Letter)));
                HintManager.Instance.SetForcedHint(hintCard, e.Col, new List<Vector2Int>(e.Cells));
            }
            // Dim everything except this beat's tiles. For a board SWAP only the SOURCE tile lights up first;
            // the word + swap-target reveal once the player taps the source (EditPointerRoutine). 2026-07-08 Spencer.
            ApplyEditSpotlight(e, 0);

            // Start the pointer + log FIRST, then do the non-essential HUD/interactable polish under a guard.
            // On level LOAD (step 0) the SWAPS HUD panel isn't built yet, so SetEditPanelBright threw and aborted
            // the REST of this method — the pointer never started and there was no log, even though the gates
            // were already set (so the edit still worked, just with no hand_point on the first word). Reordering
            // + guarding makes the pointer immune to that. 2026-07-10 Spencer.
            _editAdvancing = false;
            if (_editPointerCo != null) StopCoroutine(_editPointerCo);
            _editPointerCo = StartCoroutine(EditPointerRoutine(e));
            Debug.Log($"[TutorialGate] edit step {i}: tile ({e.Col},{e.Row}) → '{e.Letter}'.");
            try
            {
                if (HandManager.Instance != null) HandManager.Instance.SetInteractable(true);
                BoosterHUDSlot.Instance?.SetEditPanelBright(true); // keep the SWAPS pip panel lit + live
            }
            catch (System.Exception ex) { Debug.LogWarning($"[TutorialGate] step {i} HUD polish deferred at load: {ex.Message}"); }
        }

        // Compute + apply the beat's spotlight. A board SWAP is TWO-phase: phase 0 lights ONLY the source tile;
        // phase 1 (source tapped) reveals the word + the swap-target. Stamps light the word + hand letter. 2026-07-08 Spencer.
        private void ApplyEditSpotlight(GateEdit e, int phase)
        {
            if (e.Cells == null) return;
            List<Vector2Int> bright;
            List<Vector2Int> focus;
            int brightCard;
            if (e.IsSwap)
            {
                // Word region + source + the bright rectangle are lit from the START; only the far swap-TARGET
                // tile stays dimmed until the source is tapped (phase 1). 2026-07-08 Spencer.
                bright = new List<Vector2Int>(e.Cells);
                focus  = new List<Vector2Int>(e.Cells);
                if (phase == 1) bright.Add(new Vector2Int(e.SwapCol, e.SwapRow));
                brightCard = -1;
            }
            else
            {
                bright = new List<Vector2Int>(e.Cells);
                focus  = new List<Vector2Int>(e.Cells);
                brightCard = System.Math.Max(0, FindHandSlot(char.ToUpperInvariant(e.Letter)));
            }
            TutorialSpotlight.SpotlightCells(bright, focus, brightCard);
        }

        // Pointer: bounce the hand_point on the target TILE; once the player taps it (rewrite mode engages),
        // move it to the hand CARD carrying the target letter. Falls back to the tile if rewrite is cancelled.
        private IEnumerator EditPointerRoutine(GateEdit e)
        {
            // Wait until the board + hand are actually READY before showing the pointer. On a fresh level load
            // the grid geometry / hand can lag a frame or two; the old code grabbed them once up front and
            // bailed if either was momentarily null (or computed the tile position before the grid was laid
            // out), which intermittently left the hand_point missing or off-screen on the FIRST beat. Wait for a
            // laid-out grid, settle a couple frames, then compute the tile position LIVE below. 2026-07-10 Spencer.
            var grid = GridManager.Instance;
            var hand = HandManager.Instance;
            float readyWait = 0f;
            while ((grid == null || hand == null || grid.CellSize <= 0f) && readyWait < 3f)
            {
                readyWait += Time.unscaledDeltaTime;
                yield return null;
                grid = GridManager.Instance;
                hand = HandManager.Instance;
            }
            if (grid == null || hand == null) yield break;
            yield return null; // one more frame so any level-load spotlight cleanup has passed before we ShowTap
            char letter = char.ToUpperInvariant(e.Letter);
            // Offset the fingertip toward the lower-right CORNER so the hand sits off the tile face (doesn't
            // cover the letter) instead of dead-center. 2026-07-07 Spencer.
            float cell = grid.CellSize;
            Vector3 corner = new Vector3(cell * 0.30f, -cell * 0.10f, 0f);
            int shownPhase = -1; // -1 none, 0 = tapping the tile, 1 = tapping the card
            string shownInstrText = e.Say; // SetupEditGateStep already showed e.Say; only rebuild the banner when the TARGET text actually changes (avoids flicker; gracefully handles an empty Say2 → beat keeps one prompt)

            // Fire the gesture ONLY when the phase changes (ShowDragGesture is a fire-once looping animation —
            // calling it every frame restarts it and stutters). Gesture = hand descends ONTO the target (a tap).
            while (_editScript != null && !_editAdvancing)
            {
                int phase = (hand != null && hand.IsRewriteModeActive) ? 1 : 0;

                // Two-tap instruction: FIRST tap prompt (e.Say) → once the source tile is
                // selected, swap to the SECOND-tap prompt (e.Say2). Restore e.Say if the
                // player deselects. Fire only on phase change so the text box doesn't
                // re-animate every frame. 2026-07-09 Spencer.
                string wantInstr = (phase == 1 && !string.IsNullOrEmpty(e.Say2)) ? e.Say2 : e.Say;
                if (wantInstr != shownInstrText)
                {
                    if (!string.IsNullOrEmpty(wantInstr)) ShowInstruction(wantInstr);
                    shownInstrText = wantInstr;
                }

                if (phase != shownPhase)
                {
                    if (phase == 1) ApplyEditSpotlight(e, 1); // reveal word+target when source is tapped; STAY revealed (don't re-dim on deselect)
                    if (phase == 0)
                    {
                        Vector3 tileW = grid.CellToWorld(e.Col, e.Row); // computed live — grid is laid out by now
                        Debug.Log($"[EditPointerDiag] ShowTap on tile ({e.Col},{e.Row}) worldY={tileW.y:0.00} (readyWait={readyWait:0.00}s).");
                        TutorialSpotlight.ShowTap(tileW + corner);
                        shownPhase = 0;
                    }
                    else if (e.IsSwap)
                    {
                        // Board-swap step: point at the 2nd tile to swap with.
                        Vector3 swapW = grid.CellToWorld(e.SwapCol, e.SwapRow);
                        TutorialSpotlight.ShowTap(swapW + corner);
                        shownPhase = 1;
                    }
                    else
                    {
                        int slot = FindHandSlot(letter);
                        if (slot >= 0)
                        {
                            Vector3 cardW = new Vector3(hand.GetCardWorldX(slot), hand.GetCardWorldY(), 0f);
                            TutorialSpotlight.ShowTap(cardW + corner);
                            shownPhase = 1;
                        }
                        // slot not found yet (hand mid-refill) — leave shownPhase so we retry next frame
                    }
                }
                yield return null;
            }
        }

        private int FindHandSlot(char letter)
        {
            var hand = MatchController.Instance != null
                ? MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN) : null;
            if (hand == null) return -1;
            for (int i = 0; i < PlayerHand.HAND_SIZE; i++)
                if (char.ToUpperInvariant(hand.GetSlot(i)) == letter) return i;
            return -1;
        }

        private void OnGatedRewriteUsed(RewriteUsedEvent evt) => AdvanceEditStep();
        private void OnGatedBoardSwap() => AdvanceEditStep();

        // Advance the gated edit to the next step — shared by hand-STAMP (OnRewriteUsed) + board-SWAP (OnBoardSwapDone).
        private void AdvanceEditStep()
        {
            if (_editScript == null || _editAdvancing) return;
            // Input is gated to ONLY the scripted edit, so any completed edit = the correct one.
            _editAdvancing = true;
            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(false);
            HideInstruction();
            TutorialSpotlight.Hide();
            HintManager.Instance?.ClearForcedHint();
            AllowedEditCol = NO_INPUT; AllowedEditRow = NO_INPUT; AllowedEditLetter = '\0'; // block ALL edits while it resolves
            AllowedSwapCol = -1; AllowedSwapRow = -1;
            _editStep++;
            StartCoroutine(AdvanceEditGateAfterResolution());
        }

        private IEnumerator AdvanceEditGateAfterResolution()
        {
            // Let the edit resolve (word forms / detonates / gravity settles) before the next step.
            yield return new WaitForSeconds(1.6f);
            SetupEditGateStep(_editStep);
        }

        private void EndGatedEditLevel()
        {
            _editScript = null;
            _editStep = -1;
            _editAdvancing = false;
            EditGateActive = false;
            if (_editPointerCo != null) { StopCoroutine(_editPointerCo); _editPointerCo = null; }
            AllowedColumn = -1;
            AllowedCardIndex = -1;
            AllowedEditCol = -1; AllowedEditRow = -1; AllowedEditLetter = '\0';
            AllowedSwapCol = -1; AllowedSwapRow = -1;
            if (MatchController.Instance != null) MatchController.Instance.OnRewriteUsed -= OnGatedRewriteUsed;
            HandManager.OnBoardSwapDone -= OnGatedBoardSwap;
            HideInstruction();
            TutorialSpotlight.Hide();
            HintManager.Instance?.ClearForcedHint();
            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(true);
            Debug.Log("[TutorialGate] edit-gated level complete — coaching released.");
        }

        private void BeginGatedLevel(GateDrop[] script)
        {
            _gateScript = script;
            _gateStep = 0;
            _gateAdvancing = false;
            BlockShuffleAndSwap = true;
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnTileDropped -= OnGatedTileDropped;
                RulesEngine.Instance.OnTileDropped += OnGatedTileDropped;
            }
            Debug.Log($"[TutorialGate] BEGIN gated level — {script.Length} guided drops.");
            SetupGateStep(0);
        }

        private void SetupGateStep(int i)
        {
            if (_gateScript == null) return;
            if (i >= _gateScript.Length) { EndGatedLevel(); return; }

            GateDrop drop = _gateScript[i];

            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            // Slot 0 holds THIS drop's letter: set the whole hand on the first step; each later letter
            // arrives via the previous step's RigNextDraw refill into slot 0.
            if (i == 0)
                SetPlayerHand(new char[] { drop.Letter, 'X', 'R', 'E', 'L' });

            AllowedColumn = drop.Col;
            AllowedCardIndex = 0;

            if (i + 1 < _gateScript.Length)
            {
                RigNextDraw(_gateScript[i + 1].Letter);
                NextPreviewLetter = _gateScript[i + 1].Letter;
            }
            else NextPreviewLetter = '\0';
            // Refresh the NEXT preview so it shows the rigged upcoming letter, not the stale bag letter.
            if (HandManager.Instance != null) HandManager.Instance.UpdateNextTilePreview();

            ShowInstruction(!string.IsNullOrEmpty(drop.Say) ? drop.Say : (drop.Charge ? "MAKE A WORD" : "POP IT"));
            ShowCardHighlight(0);
            ShowDragGestureToColumn(0, drop.Col);
            // Pin the idle marching-ants hint to THIS step's word, so it follows CAT→PIT→LAND→NOT.
            if (HintManager.Instance != null && drop.Cells != null)
                HintManager.Instance.SetForcedHint(0, drop.Col, new List<Vector2Int>(drop.Cells));
            // Dim everything except THIS beat's target cells (mockup-driven spotlight). 2026-07-08 Spencer.
            if (drop.Cells != null)
                TutorialSpotlight.SpotlightCells(new List<Vector2Int>(drop.Cells));

            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(true);
            _gateAdvancing = false;
            Debug.Log($"[TutorialGate] step {i}: drop '{drop.Letter}' → col {drop.Col} ({(drop.Charge ? "charge" : "explode")}).");
        }

        private void OnGatedTileDropped(TileDroppedEvent evt)
        {
            if (_gateScript == null || _gateAdvancing) return;
            _gateAdvancing = true;

            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(false);
            HideCardHighlight();
            HideInstruction();
            TutorialSpotlight.Hide();   // stop the hand_point while the drop resolves
            HintManager.Instance?.ClearForcedHint();

            _gateStep++;
            StartCoroutine(AdvanceGateAfterResolution());
        }

        private IEnumerator AdvanceGateAfterResolution()
        {
            // Was a flat WaitForSeconds(1.4f) — a GUESS at how long a drop takes. Whenever the
            // resolution ran longer than that (chain, cascade, slow gravity) the next step armed
            // while the board was still animating, which is what let stray drops through.
            // Now: a minimum beat for readability, then wait for the engine to actually go idle.
            // The timeout is a backstop so a stuck resolution can't hang the tutorial forever.
            yield return new WaitForSeconds(GATE_MIN_BEAT);

            float waited = 0f;
            while (waited < GATE_RESOLVE_TIMEOUT && BoardStillResolving())
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (waited >= GATE_RESOLVE_TIMEOUT)
                Debug.LogWarning("[TutorialGate] resolution didn't settle within " +
                                 $"{GATE_RESOLVE_TIMEOUT}s — arming the next step anyway.");

            SetupGateStep(_gateStep);
        }

        private const float GATE_MIN_BEAT        = 0.85f; // readability pause after a drop lands
        private const float GATE_RESOLVE_TIMEOUT = 6.0f;  // backstop: never hang the tutorial

        /// <summary>Is the board still working? Covers the rules/turn machine and any explosion
        /// FX still on screen — both outlast the drop that started them.</summary>
        private static bool BoardStillResolving()
        {
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return true;
            if (WordDropFX.HasActiveExplosions) return true;
            return false;
        }

        private void EndGatedLevel()
        {
            _gateScript = null;
            _gateStep = -1;
            _gateAdvancing = false;
            BlockShuffleAndSwap = false;
            AllowedColumn = -1;
            AllowedCardIndex = -1;
            NextPreviewLetter = '\0';
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnGatedTileDropped;
            HideCardHighlight();
            HideInstruction();
            TutorialSpotlight.Hide();
            HintManager.Instance?.ClearForcedHint();
            if (HandManager.Instance != null) HandManager.Instance.SetInteractable(true);
//             Debug.Log("[TutorialGate] gated level complete — coaching released.");
        }

        /// <summary>End any active gated-level coaching (hand-pointer, input gate, forced hint) RIGHT NOW.
        /// Called from ObjectiveManager.InstallLevel on every level change so a gate from a PREVIOUS level
        /// (e.g. skipping L1 → L4 via the debug menu) can't linger and lock the player out. 2026-07-06 Spencer.</summary>
        public void CancelActiveCoaching()
        {
            if (_gateScript != null)
            {
                Debug.Log("[TutorialGate] CancelActiveCoaching — clearing a stale gate from a previous level.");
                EndGatedLevel();
            }
            if (_editScript != null)
            {
                Debug.Log("[TutorialGate] CancelActiveCoaching — clearing a stale EDIT gate.");
                EndGatedEditLevel();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // HAND MANIPULATION
        // ══════════════════════════════════════════════════════════════════════════

        private void SetPlayerHand(char[] letters)
        {
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                {
                    for (int i = 0; i < letters.Length && i < PlayerHand.HAND_SIZE; i++)
                        hand.SetSlot(i, letters[i]);
                }
            }

            if (HandManager.Instance != null)
                HandManager.Instance.SetHand(letters);
        }

        /// <summary>
        /// Replace a single card slot without rebuilding the entire hand.
        /// This avoids the visual "whole hand changed" confusion.
        /// </summary>
        private void ReplaceSingleCard(int slotIndex, char newLetter)
        {
            // Update data
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                    hand.SetSlot(slotIndex, newLetter);
            }

            // Update just this one card's visual
            if (HandManager.Instance != null)
            {
                // Update the internal hand array and refresh only this card
                HandManager.Instance.UpdateSingleCard(slotIndex, newLetter);
            }
        }

        /// <summary>
        /// Rigs the next card dealt into slot 0 to be a specific letter.
        /// Works by setting the PlayerHand's CachedNextLetter, which is what
        /// DrawSlot actually uses (not the tile bag directly).
        /// </summary>
        private void RigNextDraw(char letter)
        {
            if (MatchController.Instance == null) return;
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand != null)
                hand.SetCachedNextLetter(char.ToUpper(letter));
        }

        // ══════════════════════════════════════════════════════════════════════════
        // PRIMED GLOW HELPER
        // ══════════════════════════════════════════════════════════════════════════

        private void ApplyPrimedGlow(RulesEngine rules, GridManager grid)
        {
            PrimedWordRegistry registry = rules.PrimedRegistry;
            if (registry == null) return;

            for (int p = 0; p < registry.Count; p++)
            {
                var pw = registry.GetByIndex(p);
                if (pw == null) continue;
                int currentTurn = rules.GlobalTurn;
                int survived = Mathf.Max(0, currentTurn - pw.PrimedOnTurn);
                int heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                int fuse = Mathf.Max(0, pw.ExpiresOnTurn - currentTurn);
                Color glowColor = pw.IsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Tile t = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                    if (t != null) t.SetPrimedGlow(glowColor, playFlash: false, heatLevel: heatLevel, fuseRemaining: fuse, isGold: pw.IsGold);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // DIM SHUFFLE
        // ══════════════════════════════════════════════════════════════════════════

        private void DimShuffle(bool dim)
        {
            if (HandManager.Instance == null) return;
            Transform shuffleT = HandManager.Instance.transform.Find("ShuffleButton");
            if (shuffleT == null) return;
            SpriteRenderer sr = shuffleT.GetComponent<SpriteRenderer>();
            if (sr != null)
                sr.color = dim ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // INSTRUCTION TEXT
        // ══════════════════════════════════════════════════════════════════════════

        // Royal-Match-style instruction panel colours (2026-07-08 Spencer). FIRST PASS — tune to taste.
        private static readonly Color PANEL_FILL   = new Color(0.99f, 0.96f, 0.87f, 1f);  // warm cream
        private static readonly Color PANEL_BORDER = new Color(0.23f, 0.48f, 0.84f, 1f);  // royal blue
        private static readonly Color PANEL_TEXT   = new Color(0.16f, 0.30f, 0.60f, 1f);  // deep blue, bold

        // Cache the rounded-rect textures: CreateSolidRoundedRect builds an ~890x270 texture with per-pixel
        // corners — far too costly to regenerate on every beat (it was hitching the letter-collection fly-up).
        // Generate once, reuse forever. 2026-07-08 Spencer.
        private static Sprite s_panelBorderSprite;
        private static Sprite s_panelFillSprite;

        // ── Rising-intro pause beat (L9): on the FIRST rise, freeze, dim all but the newly-risen row, show the
        //    "board rises" line + Tap to Skip, resume on tap. Fired from SurvivalManager.NotifyRiseFired. 2026-07-08 Spencer.
        public static bool RisingIntroPending = false;
        private GameObject _tapToSkipGO;
        public void ShowRisingIntro()
        {
            StartCoroutine(RisingIntroRoutine());
        }
        private System.Collections.IEnumerator RisingIntroRoutine()
        {
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            var row0 = new List<Vector2Int>();
            for (int c = 0; c < RulesEngine.COLS; c++) row0.Add(new Vector2Int(c, 0));
            TutorialSpotlight.SpotlightCells(row0, row0, -1); // dim everything, spotlight the newly-risen bottom row
            ShowInstruction("The board rises as you play. Clear words before it fills up!");
            ShowTapToSkip();
            yield return null; // don't let the current frame's input instantly dismiss the pause
            while (!(Input.GetMouseButtonDown(0) || (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)))
                yield return null;
            HideTapToSkip();
            HideInstruction();
            TutorialSpotlight.Hide();
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
        }
        private void ShowTapToSkip()
        {
            HideTapToSkip();
            var grid = GridManager.Instance;
            float y = grid != null ? grid.GridBottom - grid.CellSize * 1.3f : -8f;
            _tapToSkipGO = new GameObject("TapToSkip");
            _tapToSkipGO.transform.position = new Vector3(0f, y, -5f);
            var tmp = _tapToSkipGO.AddComponent<TMPro.TextMeshPro>();
            var uiFont = GameFont.GetUITMP();
            if (uiFont != null) tmp.font = uiFont;
            tmp.text          = "Tap to Skip";
            tmp.fontSize      = 4.5f;
            tmp.color         = Color.white;
            tmp.alignment     = TMPro.TextAlignmentOptions.Center;
            tmp.sortingOrder  = 50;
            tmp.rectTransform.sizeDelta = new Vector2(10f, 2f);
            // Breathing pulse — same feel as the modal PLAY button (InOutSine yoyo scale).
            _tapToSkipGO.transform.localScale = Vector3.one;
            _tapToSkipGO.transform.DOScale(1.12f, 0.7f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
        }
        private void HideTapToSkip()
        {
            if (_tapToSkipGO != null) { _tapToSkipGO.transform.DOKill(); Destroy(_tapToSkipGO); _tapToSkipGO = null; }
        }

        // ── Near-top-out warning beat (first time the board crests into the danger zone): freeze, dim all but the
        //    top danger rows, show the warning + Tap to Skip, resume on tap. One-shot per session. 2026-07-08 Spencer.
        public static bool NearTopOutShown = false;
        public void ShowNearTopOutWarning()
        {
            StartCoroutine(NearTopOutRoutine());
        }
        private System.Collections.IEnumerator NearTopOutRoutine()
        {
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            var topRows = new List<Vector2Int>();
            int rows = RulesEngine.ROWS;
            for (int r = System.Math.Max(0, rows - 2); r < rows; r++)
                for (int c = 0; c < RulesEngine.COLS; c++)
                    topRows.Add(new Vector2Int(c, r));
            TutorialSpotlight.SpotlightCells(topRows, topRows, -1); // dim all, spotlight the top danger rows
            ShowInstruction("The board's almost full! Clear words fast or you'll top out.", 3.5f); // bubble low, below the top rows
            ShowTapToSkip();
            yield return null;
            while (!(Input.GetMouseButtonDown(0) || (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)))
                yield return null;
            HideTapToSkip();
            HideInstruction();
            TutorialSpotlight.Hide();
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
        }

        // ── Prime-decay teaching beat (L7): the FIRST time a charged word flips to its warning color (fuse ≤ 1,
        //    i.e. about to lose its charge), freeze, dim all but that word, teach "charged words fade", Tap to
        //    Skip, resume on tap. One-shot per session. Armed by PrimeDecayIntroPending (LevelEntry.PrimeDecayIntro);
        //    the transition is detected in Tile.SetPrimedGlow, which calls MaybeShowPrimeDecay. 2026-07-09 Spencer.
        public static bool PrimeDecayIntroPending = false;
        public static bool PrimeDecayShown = false;
        public static bool PrimeDecayDiagLogged = false; // rate-limit the diagnostic to one line per session
        // `cell` is one tile of the word that just entered its warning color; look the whole word up from the
        // primed registry so we can spotlight all of it. No-op unless armed + not already shown.
        public static void MaybeShowPrimeDecay(Vector2Int cell)
        {
            if (PrimeDecayShown) return;
            if (!PrimeDecayIntroPending)
            {
                if (!PrimeDecayDiagLogged) { PrimeDecayDiagLogged = true; Debug.Log($"[PrimeDecay] warning-color tile seen at {cell} but NOT armed (PrimeDecayIntroPending=false). Is this L7?"); }
                return;
            }
            if (Instance == null) return;
            var rules = RulesEngine.Instance;
            if (rules == null || rules.PrimedRegistry == null) return;
            var words = rules.PrimedRegistry.GetPrimedWordsContaining(cell);
            if (words == null || words.Count == 0 || words[0].Cells == null || words[0].Cells.Count == 0)
            {
                if (!PrimeDecayDiagLogged) { PrimeDecayDiagLogged = true; Debug.Log($"[PrimeDecay] armed + warning tile at {cell}, but registry lookup found no word (words={(words==null?"null":words.Count.ToString())})."); }
                return;
            }
            PrimeDecayShown = true;
            Debug.Log($"[PrimeDecay] FIRING beat — word of {words[0].Cells.Count} cells (trigger cell {cell}).");
            Instance.ShowPrimeDecayIntro(new List<Vector2Int>(words[0].Cells));
        }
        public void ShowPrimeDecayIntro(List<Vector2Int> wordCells)
        {
            StartCoroutine(PrimeDecayRoutine(wordCells));
        }
        private System.Collections.IEnumerator PrimeDecayRoutine(List<Vector2Int> wordCells)
        {
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            TutorialSpotlight.SpotlightCells(wordCells, wordCells, -1); // dim all, spotlight the decaying word
            ShowInstruction("Charged words fade fast. Pop them before they lose their charge!", 3.5f);
            ShowTapToSkip();
            yield return null; // don't let the triggering frame's input instantly dismiss the pause
            while (!(Input.GetMouseButtonDown(0) || (Input.touchCount > 0 && Input.GetTouch(0).phase == TouchPhase.Began)))
                yield return null;
            HideTapToSkip();
            HideInstruction();
            TutorialSpotlight.Hide();
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
        }

        private void ShowInstruction(string text, float topOffsetCells = 0.8f)
        {
            HideInstruction();

            var grid = GridManager.Instance;
            if (grid == null) return;

            _instructionGO = new GameObject("TutorialInstruction");
            float y = grid.GridTop - grid.CellSize * topOffsetCells; // default just below the board top; callers can push it lower
            _instructionGO.transform.position = new Vector3(0f, y, -5f);

            // Sprites authored at 890x270 px (PPU 100 => 8.9 world units); scale the container so the banner spans
            // ~4.5 cells wide (the raw px were huge in world space). FIRST-PASS numbers — tune to taste. 2026-07-08.
            float s = (grid.CellSize * 4.5f) / 8.9f;

            // Blue border (rounded rect) — the larger card behind the fill.
            var border = new GameObject("Border");
            border.transform.SetParent(_instructionGO.transform, false);
            border.transform.localPosition = new Vector3(0f, 0f, 0.02f);
            var bsr = border.AddComponent<SpriteRenderer>();
            if (s_panelBorderSprite == null) s_panelBorderSprite = TileRenderer.CreateSolidRoundedRect(890, 270, 74, Color.white);
            bsr.sprite = s_panelBorderSprite;
            bsr.color = PANEL_BORDER;
            bsr.sortingOrder = 48;

            // Cream fill, inset ~16px so the blue reads as a border rim.
            var fill = new GameObject("Fill");
            fill.transform.SetParent(_instructionGO.transform, false);
            fill.transform.localPosition = new Vector3(0f, 0f, 0.01f);
            var fsr = fill.AddComponent<SpriteRenderer>();
            if (s_panelFillSprite == null) s_panelFillSprite = TileRenderer.CreateSolidRoundedRect(858, 238, 58, Color.white);
            fsr.sprite = s_panelFillSprite;
            fsr.color = PANEL_FILL;
            fsr.sortingOrder = 49;

            // Bold blue text, wrapped, centred in the card.
            var textGO = new GameObject("Text");
            textGO.transform.SetParent(_instructionGO.transform, false);
            textGO.transform.localPosition = Vector3.zero;
            _instructionText = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset uiFont = GameFont.GetUITMP();
            if (uiFont != null) _instructionText.font = uiFont;
            _instructionText.text           = text;
            _instructionText.fontSize       = 4.4f;
            _instructionText.color          = PANEL_TEXT;
            _instructionText.alignment      = TMPro.TextAlignmentOptions.Center;
            _instructionText.sortingOrder   = 50;
            _instructionText.rectTransform.sizeDelta = new Vector2(7.6f, 2.4f);
            _instructionText.enableWordWrapping = true;
            _instructionText.overflowMode   = TMPro.TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(_instructionText, PANEL_TEXT, TMPHelper.TextTier.HUD);

            // Scale-in to the fitted scale.
            _instructionGO.transform.localScale = Vector3.zero;
            _instructionGO.transform.DOScale(Vector3.one * s, 0.35f).SetEase(Ease.OutBack);
        }

        private void HideInstruction()
        {
            if (_instructionGO != null)
            {
                _instructionGO.transform.DOKill();
                Object.Destroy(_instructionGO);
                _instructionGO = null;
                _instructionText = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // SKIP BUTTON
        // ══════════════════════════════════════════════════════════════════════════

        private void ShowSkipButton()
        {
            HideSkipButton();

            GameObject canvasGO = new GameObject("TutorialSkipCanvas");
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 1f;

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject btnGO = new GameObject("SkipButton");
            btnGO.transform.SetParent(canvasGO.transform, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 25f);
            rt.sizeDelta = new Vector2(100f, 34f);

            Image img = btnGO.AddComponent<Image>();
            img.sprite = TileRenderer.CreateSolidRoundedRect(512, 160, 60, Color.white);
            img.type = Image.Type.Simple;
            img.color = new Color(0.15f, 0.15f, 0.25f, 0.45f);

            Button btn = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor      = new Color(0.15f, 0.15f, 0.25f, 0.45f);
            cb.highlightedColor = new Color(0.20f, 0.20f, 0.30f, 0.60f);
            cb.pressedColor     = new Color(0.10f, 0.10f, 0.20f, 0.65f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(OnSkipClicked);

            GameObject lblGO = new GameObject("Label");
            lblGO.transform.SetParent(btnGO.transform, false);
            RectTransform lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;

            Text t = lblGO.AddComponent<Text>();
            t.font = GameFont.GetUI();
            t.text = "SKIP";
            t.fontSize = 16;
            t.fontStyle = FontStyle.Normal;
            t.color = new Color(0.7f, 0.7f, 0.8f, 0.85f);
            t.alignment = TextAnchor.MiddleCenter;

            _skipButtonCanvas = canvasGO;
        }

        private void HideSkipButton()
        {
            if (_skipButtonCanvas != null)
            {
                Object.Destroy(_skipButtonCanvas);
                _skipButtonCanvas = null;
            }
        }

        private void OnSkipClicked()
        {
//             Debug.Log("[TutorialManager] Tutorial skipped by player.");
            _skipForSession = true;
            PlayerPrefs.SetInt("tutorial_complete", 1);
            PlayerPrefs.Save();
            StopAllCoroutines();

            // BUG-11: Stop any in-flight HandManager coroutines (e.g. FullTurnSequence)
            if (HandManager.Instance != null)
            {
                HandManager.Instance.StopAllCoroutines();
                HandManager.Instance.SetInteractable(true);
            }

            // Safety: restore timeScale in case hitstop was mid-freeze
            WordDropFX.EnsureTimeScaleRestored();

            EndTutorial();
            StartNormalGame();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // CARD HIGHLIGHT — soft chevron above the target card
        // ══════════════════════════════════════════════════════════════════════════

        // Cached "which card" arrow art (green downward triangle) — replaces the old procedural chevron. 2026-07-08 Spencer.
        private static Sprite _cardArrowSprite;
        private static Sprite GetCardArrowSprite()
        {
            if (_cardArrowSprite != null) return _cardArrowSprite;
            _cardArrowSprite = Resources.Load<Sprite>("Tiles/arrow_basic_s");
            if (_cardArrowSprite == null)
            {
                // Tiles/ assets import as Texture2D (not Sprite), so Load<Sprite> is null — build the sprite
                // from the texture, same as hand_point. 2026-07-08 Spencer.
                var tex = Resources.Load<Texture2D>("Tiles/arrow_basic_s");
                if (tex != null)
                    _cardArrowSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _cardArrowSprite;
        }

        private void ShowCardHighlight(int cardIndex)
        {
            HideCardHighlight();

            if (HandManager.Instance == null || GridManager.Instance == null) return;

            float cardX = HandManager.Instance.GetCardWorldX(cardIndex);
            float cardY = HandManager.Instance.GetCardWorldY();
            float cellSize = GridManager.Instance.CellSize;

            _cardHighlightGO = new GameObject("TutorialCardHighlight");
            _cardHighlightGO.transform.position = new Vector3(cardX, cardY + cellSize * 0.75f, -5f);

            float arrowSize = cellSize * 0.30f; // target world height (2× the original small chevron)
            SpriteRenderer sr = _cardHighlightGO.AddComponent<SpriteRenderer>();
            Sprite arrowSprite = GetCardArrowSprite();
            if (arrowSprite != null) { sr.sprite = arrowSprite; } // arrow_basic_s is already green art — no tint
            else { sr.sprite = GetChevronSprite(); sr.color = ARROW_COLOR; } // fallback to the old chevron
            sr.sortingOrder = 50;

            float nativeH = (sr.sprite != null && sr.sprite.bounds.size.y > 0f) ? sr.sprite.bounds.size.y : 1f;
            float arrowScale = arrowSize / nativeH;
            _cardHighlightGO.transform.localScale = new Vector3(arrowScale, arrowScale, 1f);

            // Gentle pulse only — no bobbing
            float pulseScale = arrowScale * 1.1f;
            _cardHighlightPulse = _cardHighlightGO.transform
                .DOScale(new Vector3(pulseScale, pulseScale, 1f), 0.8f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        public void HideCardHighlightPublic() => HideCardHighlight();

        public void HideArrowsOnDrop()
        {
            HideArrow();
            HideCardHighlight();
            HideInstruction();
        }

        private void HideCardHighlight()
        {
            if (_cardHighlightPulse != null) { _cardHighlightPulse.Kill(); _cardHighlightPulse = null; }
            if (_cardHighlightGO != null)
            {
                _cardHighlightGO.transform.DOKill();
                Object.Destroy(_cardHighlightGO);
                _cardHighlightGO = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // COLUMN ARROW — soft rounded chevron above target column
        // ══════════════════════════════════════════════════════════════════════════

        private static Sprite _chevronSprite;

        private static Sprite GetChevronSprite()
        {
            if (_chevronSprite != null) return _chevronSprite;

            // Rounded chevron (˅) — softer than a sharp triangle
            int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];

            float thickness = 0.12f; // arm thickness
            float halfAngle = 0.42f; // spread angle

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float nx = (float)x / (size - 1) - 0.5f; // -0.5 to 0.5
                    float ny = (float)y / (size - 1);         // 0 at bottom (point), 1 at top

                    // Two diagonal arms meeting at bottom center
                    // Left arm: from (0.5, 0) to (-halfAngle, 1)
                    // Right arm: from (0.5, 0) to (halfAngle, 1)
                    float leftDist = Mathf.Abs(nx + halfAngle * ny);
                    float rightDist = Mathf.Abs(nx - halfAngle * ny);
                    float armDist = Mathf.Min(leftDist, rightDist);

                    // Only draw upper portion (the V shape, not below the point)
                    if (ny < 0.15f)
                    {
                        pixels[y * size + x] = Color.clear;
                    }
                    else if (armDist < thickness)
                    {
                        pixels[y * size + x] = Color.white;
                    }
                    else if (armDist < thickness + 0.025f)
                    {
                        // Soft AA edge
                        float aa = 1f - (armDist - thickness) / 0.025f;
                        pixels[y * size + x] = new Color(1f, 1f, 1f, aa);
                    }
                    else
                    {
                        pixels[y * size + x] = Color.clear;
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _chevronSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _chevronSprite;
        }

        private void ShowArrow(int col)
        {
            HideArrow();

            var grid = GridManager.Instance;
            if (grid == null) return;

            float x = grid.GetColumnCenterX(col);
            float y = grid.GridTop + grid.CellSize * 0.8f;
            float arrowSize = grid.CellSize * 0.22f;

            _arrowGO = new GameObject("TutorialArrow");
            _arrowGO.transform.position = new Vector3(x, y, -10f);

            SpriteRenderer sr = _arrowGO.AddComponent<SpriteRenderer>();
            sr.sprite = GetChevronSprite();
            sr.color = ARROW_COLOR;
            sr.sortingOrder = 100;

            float nativeSize = 128f / 100f;
            float scale = arrowSize / nativeSize;
            _arrowGO.transform.localScale = new Vector3(scale, scale, 1f);

            // Gentle pulse only — no bobbing
            _arrowPulseTween = _arrowGO.transform
                .DOScale(new Vector3(scale * 1.1f, scale * 1.1f, 1f), 0.8f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private void HideArrow()
        {
            if (_arrowPulseTween != null) { _arrowPulseTween.Kill(); _arrowPulseTween = null; }

            if (_arrowGO != null)
            {
                _arrowGO.transform.DOKill();
                Object.Destroy(_arrowGO);
                _arrowGO = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // DEBUG
        // ══════════════════════════════════════════════════════════════════════════

        public static void ResetTutorial()
        {
            PlayerPrefs.SetInt("tutorial_complete", 0);
            PlayerPrefs.SetInt("hint_rewrite", 0);
            PlayerPrefs.SetInt("hint_swap", 0);
            HighScoreManager.ResetAll();
            PlayerPrefs.Save();
            _skipForSession = false;
//             Debug.Log("[TutorialManager] Tutorial reset (+ high scores cleared) — will run on next game.");
        }
    }
}
