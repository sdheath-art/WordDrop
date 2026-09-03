using System;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Owns the level's active Objective (2026-06-08): feeds it gameplay events, pushes the
    /// HUD readout, and fires OnObjectiveComplete on the rising edge of IsComplete. The win
    /// condition that used to be "score >= target" routes through here. Auto-creates on scene
    /// load. For now a debug objective auto-installs in Survival so we can FEEL-TEST one
    /// objective atom before wiring level JSON / the LevelController win-check.
    /// </summary>
    public class ObjectiveManager : MonoBehaviour
    {
        public static ObjectiveManager Instance { get; private set; }

        public Objective Active { get; private set; }
        // A retired objective is complete-and-consumed: still shown on the HUD (so the player
        // sees 3/3 behind the stage-clear modal) but no longer the live win condition, so the
        // stage-clear loop won't instantly re-clear. Reset to a fresh objective once the modal
        // closes. 2026-06-09.
        public bool HasObjective => Active != null && !_retired;

        /// <summary>Fires once, when the active objective transitions to complete.</summary>
        public event Action<Objective> OnObjectiveComplete;

        // FEEL-TEST: auto-install one objective in Survival so it's playable without level
        // JSON. Flip false / replace with per-level objectives once the framework is proven.
        private const bool DEBUG_AUTO_OBJECTIVE = true;

        // Tutorial counting: when true, the connecting/trigger word in a detonation ALSO counts toward
        // the active objective (read in RulesEngine.DoExplode). Set per-level in InstallLevel — OFF for
        // every normal level, so existing objective balance is unchanged. 2026-06-25 Spencer.
        public static bool CountConnectingWords = false;

        private RulesEngine _subscribedTo;
        private bool _firedComplete;
        private bool  _completePendingLand;  // objective completed; holding bing/checkmark/modal until fly-ups land
        private float _pendingLandDeadline;  // unscaled-time safety release if no fly-up lands
        private bool  _celebratePending;     // fly-ups landed; holding the ding+checkmark a hair so the hit SFX clears
        private float _celebrateTime;        // unscaled-time to fire the ding+checkmark
        private const float CELEBRATE_AFTER_LAND_DELAY = 0.18f; // gap between the landing hit SFX and the ding
        private bool  _modalPending;         // ding+checkmark fired; holding the Level Completed modal a beat
        private float _modalPendingTime;     // unscaled-time to release the modal
        private bool _retired;          // complete + consumed, holding on HUD until modal closes

        // HeroWord (chicken) win-lock: set the instant the LAST escort is collected (cell cleared),
        // even though IsComplete only flips when its fly-up animation lands. A rising row must not fire
        // during that fly-up window once the win is locked in. 2026-06-23 Spencer.
        private bool _escortWinPending;
        public bool EscortWinPending => _escortWinPending;
        private bool _modalWasShowing;  // edge-detect the stage-clear modal closing

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
                new GameObject("ObjectiveManager").AddComponent<ObjectiveManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>Install the level's objective (resets progress, refreshes the HUD).</summary>
        public void SetObjective(Objective obj)
        {
            Active = obj;
            _firedComplete = false;
            _completePendingLand = false;
            _celebratePending = false;
            _modalPending = false;
            _retired = false;
            _escortWinPending = false;
            obj?.Reset();
            PushHud();
            Debug.Log($"[Objective] Set: {(obj != null ? obj.Title : "none")}");
        }

        public void ClearObjective()
        {
            Active = null;
            _retired = false;
            PushHud();
        }

        /// <summary>
        /// VALIDATION MVP (2026-06-15): install the level for a 1-based level index (=
        /// SurvivalManager.CurrentStageIndex) from the hardcoded LevelTable — the simple run
        /// sequencer that stands in for the full procedural pickLevel(n) generator (deferred).
        /// Reads LevelTable.Get(n), installs its mode via SetObjective, and applies that level's
        /// difficulty dials: length/detonation seeding toggles, board-assist frequency, rise
        /// cadence, and (vault levels) the move budget. Any randomness inside the modes routes
        /// through SurvivalRng already — this only flips deterministic per-level dials.
        /// </summary>
        private List<char> _pendingNearWordLetters;
        private bool _swapExplosionsOnly; // swap-tutorial levels: only explosions caused by a swap/edit count toward the objective

        /// <summary>True if an explosion happening RIGHT NOW would count toward the objective — false on a
        /// swap-only level when the current resolution was a DROP. Fly-up is suppressed when false, so letters
        /// only fly to the target when they actually count. 2026-07-07 Spencer.</summary>
        public bool CurrentExplosionCounts => !_swapExplosionsOnly || RulesEngine.LastResolutionWasSwap;
        // Short common words for "obvious near-word" seeding on edit-practice levels. First 2 letters get placed
        // on the board; the 3rd is the completing letter the player edits in.
        private static readonly string[] NEAR_WORDS = { "CAT","DOG","SUN","PEN","BUS","HAT","MAP","BAT","PIG","TOP","RUN","BED","FAN","JAR","CUP" };

        /// <summary>Seed N obvious near-words into row 0 (each = the word's first 2 letters + a DECOY tile the
        /// player edits) and RETURN the completing letters (the caller rigs them into the hand). 2026-07-07 Spencer.</summary>
        private static void SeedSet(RulesEngine rules, int c, int r, char ch)
            => rules.SetCell(c, r, new RulesCellData { Letter = ch, Col = c, Row = r, PlayerIndex = -1 });

        // Hybrid opportunity seed: on a normally-generated (filled, random) board, stamp ONE guaranteed crossing
        // pop and carve its drop lane OPEN — so a fresh player looking at a real, full board still has one obvious
        // drop-to-explode. COIN (horizontal) x WORD (vertical) share the O: drop O (completes+charges COIN, reveals
        // _ORD), then W (WORD forms, crosses COIN's O → pop). Returns [O, W] to seed into the hand. 2026-07-09 Spencer.
        private static int s_crossCol = -1;              // drop-lane column of the seeded crossing (for the idle nudge)
        private static List<Vector2Int> s_crossCells;    // COIN cells to marching-ants for the nudge
        private static List<char> SeedCrossingOpportunity(RulesEngine rules, int fillRows)
        {
            var completing = new List<char>();
            s_crossCol = -1; s_crossCells = null;
            if (rules == null) return completing;
            int rows = RulesEngine.ROWS, cols = RulesEngine.COLS;
            int X   = 2;                                        // drop-lane column (O falls here)
            int top = Mathf.Clamp(fillRows - 1, 0, rows - 1);   // top of the filled band
            int R   = Mathf.Clamp(top - 1, 3, rows - 2);        // crossing row, one below the fill top (W-drop headroom)
            if (X - 1 < 0 || X + 2 >= cols || R - 2 < 0 || R + 1 >= rows) return completing;

            // COIN across row R with the crossing cell (X,R) left OPEN:  C _ I N
            SeedSet(rules, X - 1, R, 'C'); SeedSet(rules, X + 1, R, 'I'); SeedSet(rules, X + 2, R, 'N');
            // WORD's lower letters directly beneath the crossing cell (R just under the gap, D under that):
            SeedSet(rules, X, R - 1, 'R'); SeedSet(rules, X, R - 2, 'D');

            // GRAVITY: the random fill stacks each column bottom-up to a JAGGED height, so a column shorter than
            // the seed row would leave a seed tile floating (Spencer saw C/I in mid-air). Fill any gap beneath each
            // seed tile down to the existing stack so everything is supported. 2026-07-09 Spencer.
            const string FILL = "TNRSLBKPDMGF"; int fi = 0;
            void Support(int c, int fromRow)
            {
                for (int r = fromRow; r >= 0; r--)
                {
                    if (rules.GetCell(c, r) != null) break;         // reached the existing stack — supported below
                    SeedSet(rules, c, r, FILL[(fi++) % FILL.Length]);
                }
            }
            Support(X - 1, R - 1); Support(X + 1, R - 1); Support(X + 2, R - 1); Support(X, R - 3);

            // Carve the drop lane OPEN from the crossing cell upward so the dropped tile falls into the gap:
            for (int r = R; r < rows; r++) rules.ClearCell(X, r);

            s_crossCol   = X;
            s_crossCells = new List<Vector2Int> { new Vector2Int(X - 1, R), new Vector2Int(X, R), new Vector2Int(X + 1, R), new Vector2Int(X + 2, R) };
            completing.Add('O'); // 1st drop → completes + charges COIN, reveals _ORD
            completing.Add('W'); // 2nd drop → completes WORD → crosses COIN's O → POP
            Debug.Log($"[CrossSeed] stamped COIN x WORD @ (col {X}, row {R}); lane carved; supports filled.");
            return completing;
        }

        // Clear the crossing drop-nudge after the first drop so the normal idle hint then guides the W->WORD step.
        private void OnCrossSeedDrop(TileDroppedEvent e)
        {
            HintManager.Instance?.ClearForcedHint();
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnCrossSeedDrop;
        }

        // Authored crossing levels (fixedBoard): force the idle nudge to walk the completing letters into the drop
        // lane IN ORDER (O→COIN, then W→WORD). Advances when the expected letter lands in the lane; clears when the
        // sequence is done, handing back to the normal idle hint. 2026-07-09 Spencer.
        private int    _crossHintCol = -1;
        private int[]  _crossHintCols;
        private string _crossHintLetters;
        private int    _crossHintStep;
        private int CrossColForStep(int step)
            => (_crossHintCols != null && step >= 0 && step < _crossHintCols.Length) ? _crossHintCols[step] : _crossHintCol;

        // Authored EDIT nudge (sequential): like the cross-hint above but for EDITS instead of drops.
        // Points the idle hint at each scripted edit target in turn (bounce the completing hand card +
        // marching-ants the word it makes), advancing as each target cell reaches its completing letter.
        // Used by L6: step0 LOVE (edit N@(5,1)→E), step1 HOME (edit Z@(4,1)→M). 2026-07-09 Spencer.
        private int[]  _editHintCols;
        private int[]  _editHintRows;
        private string _editHintLetters;
        private int    _editHintStep;
        private void SetupCrossHint(in LevelEntry e)
        {
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnCrossHintDrop;
            _crossHintCol = -1; _crossHintCols = null; _crossHintLetters = null; _crossHintStep = 0;
            bool hasCols = e.HintCrossCol >= 0 || (e.HintCrossCols != null && e.HintCrossCols.Length > 0);
            if (!hasCols || string.IsNullOrEmpty(e.HintCrossLetters)) return;
            _crossHintCol     = e.HintCrossCol;
            _crossHintCols    = e.HintCrossCols;
            _crossHintLetters = e.HintCrossLetters;
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped += OnCrossHintDrop;
            RefreshCrossHint();
        }
        private void RefreshCrossHint()
        {
            if (HintManager.Instance == null) return;
            if (_crossHintLetters == null || _crossHintStep >= _crossHintLetters.Length)
            {
                HintManager.Instance.ClearForcedHint();
                if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnCrossHintDrop;
                return;
            }
            char L = char.ToUpperInvariant(_crossHintLetters[_crossHintStep]);
            var hand = MatchController.Instance?.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            int cardSlot = -1;
            for (int i = 0; i < PlayerHand.HAND_SIZE; i++)
                if (char.ToUpperInvariant(hand.GetSlot(i)) == L) { cardSlot = i; break; }
            // Completing letter isn't in hand (player used it elsewhere / diverged from the scripted path):
            // DROP the pin so the normal auto-hint (valid moves only) takes over. Never leave a stale pin
            // outlining a word the player can no longer make. 2026-07-09 Spencer.
            if (cardSlot < 0) { HintManager.Instance.ClearForcedHint(); return; }
            int col = CrossColForStep(_crossHintStep);
            int landRow = 0;
            var rules = RulesEngine.Instance;
            if (rules != null)
                for (int r = RulesEngine.ROWS - 1; r >= 0; r--)
                    if (rules.GetCell(col, r) != null) { landRow = Mathf.Min(r + 1, RulesEngine.ROWS - 1); break; }
            // Only pin the hint if dropping the letter here forms a REAL word. If the board has changed so the
            // scripted word is no longer makeable, clear the pin — never outline a non-word (e.g. "C_IN" after the
            // player spent the O). The auto-hint takes over. 2026-07-09 Spencer.
            var wordCells = ComputeWordCells(col, landRow, _crossHintLetters[_crossHintStep]);
            if (wordCells == null || wordCells.Count < 2) { HintManager.Instance.ClearForcedHint(); return; }
            HintManager.Instance.SetForcedHint(cardSlot, col, wordCells);
        }

        // Cells of the WHOLE word the given letter completes when dropped at (col,row) — the longest valid word
        // through that cell, horizontal (COIN) or vertical (WORD) — so the nudge marches the whole word. 2026-07-09.
        private List<Vector2Int> ComputeWordCells(int col, int row, char letter)
        {
            var rules = RulesEngine.Instance;
            if (rules == null) return null;
            char Ch(int c, int r)
            {
                if (c == col && r == row) return char.ToUpperInvariant(letter);
                var cell = rules.GetCell(c, r);
                return cell != null ? char.ToUpperInvariant(cell.Letter) : '\0';
            }
            List<Vector2Int> best = null;
            // Horizontal run through (col,row)
            int hl = col, hr = col;
            while (hl - 1 >= 0 && Ch(hl - 1, row) != '\0') hl--;
            while (hr + 1 < RulesEngine.COLS && Ch(hr + 1, row) != '\0') hr++;
            var hrun = new System.Text.StringBuilder();
            for (int c = hl; c <= hr; c++) hrun.Append(Ch(c, row));
            var hspan = LongestWordSpan(hrun.ToString(), col - hl);
            if (hspan.HasValue)
            {
                best = new List<Vector2Int>();
                for (int c = hl + hspan.Value.Item1; c <= hl + hspan.Value.Item2; c++) best.Add(new Vector2Int(c, row));
            }
            // Vertical run through (col,row) (top-down string; run index i = row (vt - i))
            int vb = row, vt = row;
            while (vb - 1 >= 0 && Ch(col, vb - 1) != '\0') vb--;
            while (vt + 1 < RulesEngine.ROWS && Ch(col, vt + 1) != '\0') vt++;
            var vrun = new System.Text.StringBuilder();
            for (int r = vt; r >= vb; r--) vrun.Append(Ch(col, r));
            var vspan = LongestWordSpan(vrun.ToString(), vt - row);
            if (vspan.HasValue)
            {
                var cells = new List<Vector2Int>();
                for (int i = vspan.Value.Item1; i <= vspan.Value.Item2; i++) cells.Add(new Vector2Int(col, vt - i));
                if (best == null || cells.Count > best.Count) best = cells;
            }
            return best;
        }
        // Start/end indices in `run` of the longest valid word (>=2) that includes index `mustInclude`, or null.
        private (int, int)? LongestWordSpan(string run, int mustInclude)
        {
            int best = -1, bs = 0, be = 0;
            for (int s = 0; s <= mustInclude; s++)
                for (int e = mustInclude; e < run.Length; e++)
                {
                    int len = e - s + 1;
                    if (len >= 2 && len > best && WordDictionary.IsValidWord(run.Substring(s, len))) { best = len; bs = s; be = e; }
                }
            return best > 0 ? (bs, be) : ((int, int)?)null;
        }
        private void OnCrossHintDrop(TileDroppedEvent ev)
        {
            if (_crossHintLetters != null && _crossHintStep < _crossHintLetters.Length
                && ev.Col == CrossColForStep(_crossHintStep)
                && char.ToUpperInvariant(ev.Letter) == char.ToUpperInvariant(_crossHintLetters[_crossHintStep]))
                _crossHintStep++;
            RefreshCrossHint();
        }

        private static List<char> SeedNearWordsOnBoard(RulesEngine rules, int count)
        {
            var completing = new List<char>();
            if (rules == null || count <= 0) return completing;
            int cols = RulesEngine.COLS;
            int c = 0;
            for (int n = 0; n < count; n++)
            {
                if (c + 2 >= cols) break;                    // need 3 columns for this near-word
                string w = NEAR_WORDS[(n * 5 + 2) % NEAR_WORDS.Length];
                rules.SetCell(c,   0, new RulesCellData { Letter = w[0], Col = c,   Row = 0, PlayerIndex = -1 });
                rules.SetCell(c+1, 0, new RulesCellData { Letter = w[1], Col = c+1, Row = 0, PlayerIndex = -1 });
                rules.SetCell(c+2, 0, new RulesCellData { Letter = 'Z',  Col = c+2, Row = 0, PlayerIndex = -1 }); // decoy tile the player edits
                completing.Add(w[2]);
                c += 3;                                       // next near-word starts right after this one
            }
            Debug.Log($"[NearWord] seeded {completing.Count} near-word(s) in row 0.");
            return completing;
        }

        // One-shot: the moment the player performs ANY swap/edit on a near-word level, drop the pinned swap-nudge
        // so it can't point at an already-completed near-word. 2026-07-07 Spencer.
        private void OnNearWordEditMove(RewriteUsedEvent e) => ClearNearWordHint();
        private void ClearNearWordHint()
        {
            HintManager.Instance?.ClearForcedHint();
            if (MatchController.Instance != null) MatchController.Instance.OnRewriteUsed -= OnNearWordEditMove;
            HandManager.OnBoardSwapDone -= ClearNearWordHint;
        }

        // ── Authored EDIT nudge (sequential) ───────────────────────────────────────
        // Pin the idle hint to the current scripted EDIT: bounce the completing hand card and
        // marching-ants the word that edit would make. Advances once the target cell holds its
        // completing letter (the edit was made), then points at the next step; clears when the
        // sequence is done, handing back to the normal idle hint. 2026-07-09 Spencer.
        private void SetupEditHint(in LevelEntry e)
        {
            EndEditHint();
            if (string.IsNullOrEmpty(e.HintEditLetters) || e.HintEditCols == null || e.HintEditRows == null) return;
            _editHintCols    = e.HintEditCols;
            _editHintRows    = e.HintEditRows;
            _editHintLetters = e.HintEditLetters;
            _editHintStep    = 0;
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnRewriteUsed -= OnEditHintRewrite;
                MatchController.Instance.OnRewriteUsed += OnEditHintRewrite;
            }
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnTileDropped -= OnEditHintDrop;
                RulesEngine.Instance.OnTileDropped += OnEditHintDrop;
            }
            HandManager.OnBoardSwapDone -= RefreshEditHint;
            HandManager.OnBoardSwapDone += RefreshEditHint;
            RefreshEditHint();
        }

        private void OnEditHintRewrite(RewriteUsedEvent ev) => RefreshEditHint();
        private void OnEditHintDrop(TileDroppedEvent ev)   => RefreshEditHint();

        private void RefreshEditHint()
        {
            var rules = RulesEngine.Instance;
            var hint  = HintManager.Instance;
            if (_editHintLetters == null || rules == null || hint == null) return;

            // Skip past any steps whose target cell already holds the completing letter (edit done).
            while (_editHintStep < _editHintLetters.Length
                   && _editHintStep < _editHintCols.Length && _editHintStep < _editHintRows.Length)
            {
                var doneCell = rules.GetCell(_editHintCols[_editHintStep], _editHintRows[_editHintStep]);
                char want = char.ToUpperInvariant(_editHintLetters[_editHintStep]);
                if (doneCell != null && char.ToUpperInvariant(doneCell.Letter) == want) { _editHintStep++; continue; }
                break;
            }
            if (_editHintStep >= _editHintLetters.Length) { EndEditHint(); return; }

            int tcol = _editHintCols[_editHintStep];
            int trow = _editHintRows[_editHintStep];
            char L   = char.ToUpperInvariant(_editHintLetters[_editHintStep]);

            // The completing letter must currently be in hand (wait if it's still NEXT or used elsewhere).
            var hand = MatchController.Instance?.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            int cardSlot = -1;
            for (int i = 0; i < PlayerHand.HAND_SIZE; i++)
                if (char.ToUpperInvariant(hand.GetSlot(i)) == L) { cardSlot = i; break; }
            if (cardSlot < 0) { hint.ClearForcedHint(); return; }

            // Only pin the edit hint if the stamp actually forms a REAL word here. If the player changed the
            // board so the scripted word is no longer makeable, clear the pin — never outline a non-word. The
            // auto-hint takes over. 2026-07-09 Spencer.
            var wordCells = ComputeWordCells(tcol, trow, L);
            if (wordCells == null || wordCells.Count < 2) { hint.ClearForcedHint(); return; }
            hint.SetForcedHint(cardSlot, tcol, wordCells);
        }

        private void EndEditHint()
        {
            // Only clear the shared forced hint if an edit-hint actually owned it — otherwise this
            // would wipe a cross-hint / near-word nudge that a different level just set. 2026-07-09.
            bool wasActive = _editHintLetters != null;
            _editHintLetters = null; _editHintCols = null; _editHintRows = null; _editHintStep = 0;
            if (wasActive) HintManager.Instance?.ClearForcedHint();
            if (MatchController.Instance != null) MatchController.Instance.OnRewriteUsed -= OnEditHintRewrite;
            if (RulesEngine.Instance != null) RulesEngine.Instance.OnTileDropped -= OnEditHintDrop;
            HandManager.OnBoardSwapDone -= RefreshEditHint;
        }

        public void InstallLevel(int levelIndex) => InstallLevel(levelIndex, viaDebugJump: false);

        public void InstallLevel(int levelIndex, bool viaDebugJump)
        {
            // Reaching a real Area level means the one-time tutorial is behind us — mark it done so future runs
            // start at Area 1 instead of replaying the coaching. 2026-07-14 Spencer.
            if (levelIndex > LevelMapPanel.TUTORIAL_LEVELS) SurvivalManager.TutorialDone = true;

            var e = LevelTable.Get(levelIndex);

            // Clear any stale authored EDIT nudge from a PRIOR level BEFORE the near-word / cross-hint
            // setters below run, so leaving an edit-hint level (e.g. L6) can't wipe the new level's
            // forced hint. SetupEditHint (called later) then installs THIS level's edit nudge, if any. 2026-07-09.
            EndEditHint();

            // ── Difficulty dials ──
            // Seeding toggles: OpportunitySeeding (length, anti-frustration, peel LAST) +
            // DetonationSeeding (the "easy mode", peel FIRST). Static — apply before the draw runs.
            DroughtAssist.OpportunitySeeding = e.LengthSeed;
            DroughtAssist.DetonationSeeding  = e.DetonSeed;
            // Board-assist frequency (0.5 high → 0.2 low).
            PlayerHand.SetBoardAssistBase(e.AssistFreq);
            // Rise cadence (turns per rise; bigger = slower). Vault levels run rises OFF regardless.
            // TUTORIAL levels that have rises ON always rise every 3 moves at minimum — gentler while the player is
            // still learning the rising board. 2026-07-14 Spencer.
            int riseCadence = (levelIndex <= LevelMapPanel.TUTORIAL_LEVELS && !e.RisesOff)
                              ? Mathf.Max(3, e.RiseCadence)
                              : e.RiseCadence;
            SurvivalManager.SetRiseCadenceOverride(riseCadence);
            // Vault move budget for THIS level (only read on a move-cap/vault level).
            SurvivalManager.Instance?.SetVaultMoveBudgetOverride(e.VaultMoves);
            // Authored/tutorial levels: rising rows OFF + a custom move budget. Force rises back ON for
            // normal non-Vault levels so a prior tutorial level's "off" can't leak forward; Vault keeps
            // its own rises-off handling. 2026-06-25 Spencer.
            if (e.RisesOff)                     RisingRowManager.Enabled = false;
            else if (e.Mode != LevelMode.Vault) RisingRowManager.Enabled = true;
            SurvivalManager.Instance?.SetStageMoveBudgetOverride(e.MoveBudget);
            SurvivalManager.Instance?.SetMoveLimitTopOut(e.MoveLimitTopOut); // authored non-rising levels (L7/L8) top out when the budget is spent
            SurvivalManager.Instance?.SetEditsCountAsMoves(e.EditsCountAsMoves); // edit-focused levels (L6) count edits/swaps toward moves
            // Tutorial: count the connecting word toward the goal on this level (OFF for normal levels).
            CountConnectingWords = e.CountConnecting;
            // Tutorial: disable the fuse so primed words don't fizzle on a learner (OFF for normal levels).
            RulesEngine.FuseDisabled = e.FuseOff;
            RulesEngine.StonesDisabled = e.StonesOff; // L9: no rocks yet
            // Splash collateral off for authored/gated tutorial levels so detonations are DETERMINISTIC
            // (clear only the word tiles — no junk-splash scrambling the pre-placed board). Self-restoring:
            // every non-splashOff level install sets it back true. 2026-07-07 Spencer.
            RulesEngine.JunkSplashEnabled = !e.SplashOff;
            _swapExplosionsOnly = e.SwapExplosionsOnly; // swap-tutorial: only swap-caused explosions count
            // Per-level swap/edit charge grant (bootstrap swap-only levels — the running total can be depleted).
            if (e.RewriteCharges > 0) MatchController.Instance?.SetRewriteCharges(e.RewriteCharges);
            TutorialManager.RisingIntroPending = e.RisingIntro; // arm the one-time first-rise pause on L9
            TutorialManager.PrimeDecayIntroPending = e.PrimeDecayIntro; // arm the one-time "charged words fade" beat on L7 (fires when a prime first hits its warning color)
            if (e.PrimeDecayIntro) { TutorialManager.PrimeDecayShown = false; TutorialManager.PrimeDecayDiagLogged = false; } // re-arm on a fresh L7 install so a replay shows it again
            // A gate from a PREVIOUS level (e.g. skipping L1 → L4 via the debug menu) must not linger and
            // lock the player out of this level — cancel any active coaching before installing. 2026-07-06.
            TutorialManager.Instance?.CancelActiveCoaching();

            // Onboarding locks: lock the tools not yet taught (swaps/boosters/edit/bag) for early levels;
            // they unlock when the real game starts (see TutorialLocks unlock-level constants).
            TutorialLocks.ApplyForLevel(levelIndex);

            // ── Starting board ──
            // Tutorial / authored levels place an EXACT hand-built board (FixedBoard) verbatim and skip
            // the random reseed. Everything else keeps the existing behavior. 2026-06-25 Spencer.
            if (e.FixedBoard != null && e.FixedBoard.Length > 0)
            {
                var rules = RulesEngine.Instance;
                if (rules != null)
                {
                    rules.ClearBoard();
                    int h = e.FixedBoard.Length;
                    for (int i = 0; i < h; i++)
                    {
                        string row = e.FixedBoard[i];
                        if (string.IsNullOrEmpty(row)) continue;
                        int boardRow = (h - 1) - i;   // FixedBoard[0] = TOP row; last row → bottom (row 0)
                        for (int col = 0; col < row.Length && col < RulesEngine.COLS; col++)
                        {
                            char ch = row[col];
                            if (ch == '.' || ch == '_' || ch == ' ') continue;
                            rules.SetCell(col, boardRow, new RulesCellData
                            {
                                Letter = char.ToUpper(ch), Col = col, Row = boardRow, PlayerIndex = -1
                            });
                        }
                    }
                    MatchController.Instance?.ResetTurnCounter();
                    GridManager.Instance?.RebuildFromRulesEngine(rules);
                }
            }
            // ── Fresh, fuller starting board per level ──
            // Vault + Ice levels self-seed a full board (SeedVaultBoard in their objective Tick). The
            // other modes (LongWord / HeroWord) would otherwise inherit the PREVIOUS level's DEPLETED
            // board → a barren start with almost no word possibilities (Spencer saw an L3 LongWord with
            // ~3 tiles). Reseed a fuller board here for those modes. 2026-06-15 Spencer.
            else if (e.Mode != LevelMode.Vault && e.Mode != LevelMode.Ice)
            {
                var rules = RulesEngine.Instance;
                if (rules != null)
                {
                    // Per-mode STARTING DENSITY (Spencer 2026-06-15): Ice + Vault self-seed a NEARLY-FULL
                    // board (they need letters packed around the ice/chests). LongWord/HeroWord want a
                    // LESS-packed board — LongWord room to maneuver, HeroWord drop HEADROOM so the escort
                    // tiles can actually fall to the bottom. Starting values; tune by feel.
                    int fillRows; float density;
                    switch (e.Mode)
                    {
                        case LevelMode.HeroWord: fillRows = 6; density = 0.80f; break; // 2026-06-15 Spencer: FULLER board so the escort takes longer to reach the bottom
                        default:                 fillRows = 5; density = 0.65f; break; // LongWord: room to maneuver
                    }
                    // Per-level override (tutorial levels keep the board lighter/forgiving). 2026-06-25.
                    if (e.BoardFillRows > 0) fillRows = e.BoardFillRows;
                    if (e.BoardDensity > 0f) density = e.BoardDensity;
                    rules.SeedVaultBoard(fillRows, density, 0, 0, 0, 0, 0, 0, 0); // 0 vaults = fill only
                    // Edit-practice: sprinkle a couple of OBVIOUS near-words into row 0 so the player has clear
                    // "change this tile → make a word" opportunities. Hand gets the completing letters below.
                    _pendingNearWordLetters = e.SeedCrossing      ? SeedCrossingOpportunity(rules, fillRows)
                                            : e.SeedNearWords > 0 ? SeedNearWordsOnBoard(rules, e.SeedNearWords)
                                            : null;
                    MatchController.Instance?.ResetTurnCounter();                 // ClearBoard zeroed GlobalTurn; reset the cached _currentTurn too (the trap)
                    GridManager.Instance?.RebuildFromRulesEngine(rules);          // full rebuild wipes stale per-tile visual state
                }
            }

            // Tutorial: guarantee the opening hand can make a word on this board (no cold start). 2026-06-25.
            if (e.GuaranteeFirstWord)
            {
                // These levels are DESIGNED around the assists (board-aware draws + opening guarantee).
                // A stale NoAssist debug toggle disables ALL of it → a cold, dead board (Spencer hit this
                // repeatedly). Force it OFF for assisted tutorial levels so it can't bite. 2026-06-25.
                if (SurvivalManager.NoAssistMode)
                {
                    SurvivalManager.NoAssistMode = false;
                    Debug.Log("[LevelTable] Tutorial level forced NoAssistMode OFF (assists are required here).");
                }
                MatchController.Instance?.GuaranteeFirstWordForCurrentBoard();
            }

            // Rig the seeded near-words' COMPLETING letters into the hand — AFTER GuaranteeFirstWord (which may
            // have rerolled the hand) so the near-words are actually completable by an edit. 2026-07-07 Spencer.
            if (_pendingNearWordLetters != null && _pendingNearWordLetters.Count > 0 && MatchController.Instance != null)
            {
                var nwHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (nwHand != null)
                    for (int i = 0; i < _pendingNearWordLetters.Count && i < PlayerHand.HAND_SIZE; i++)
                        nwHand.SetSlot(i, _pendingNearWordLetters[i]);
                if (HandManager.Instance != null) HandManager.Instance.RefreshHandFromMatchController();

                // Swap NUDGE: pin the IDLE hint to the FIRST near-word (row 0, cols 0-2). After the player idles
                // it hops the completing letter + marching-ants the near-word — a SWAP cue (card hop + outline,
                // NOT a drop gesture). Cleared on the first swap/edit so it can't go stale. 2026-07-07 Spencer.
                if (HintManager.Instance != null && nwHand != null)
                {
                    char lc = char.ToUpperInvariant(_pendingNearWordLetters[0]);
                    int cardSlot = 0;
                    for (int i = 0; i < PlayerHand.HAND_SIZE; i++)
                        if (char.ToUpperInvariant(nwHand.GetSlot(i)) == lc) { cardSlot = i; break; }
                    if (e.SeedCrossing && s_crossCells != null)
                    {
                        // Crossing-seed (drop) level: hop the O card, point at the lane, outline COIN — a DROP cue.
                        // Cleared after the first drop so the normal idle hint then guides the W->WORD step.
                        HintManager.Instance.SetForcedHint(cardSlot, s_crossCol, s_crossCells);
                        if (RulesEngine.Instance != null)
                        {
                            RulesEngine.Instance.OnTileDropped -= OnCrossSeedDrop;
                            RulesEngine.Instance.OnTileDropped += OnCrossSeedDrop;
                        }
                    }
                    else
                    {
                        // Near-word EDIT level: the original SWAP cue pinned to the first near-word (row 0, cols 0-2).
                        var nwCells = new List<Vector2Int> { new Vector2Int(0, 0), new Vector2Int(1, 0), new Vector2Int(2, 0) };
                        HintManager.Instance.SetForcedHint(cardSlot, 2, nwCells);
                        MatchController.Instance.OnRewriteUsed -= OnNearWordEditMove;
                        MatchController.Instance.OnRewriteUsed += OnNearWordEditMove;
                        HandManager.OnBoardSwapDone -= ClearNearWordHint;
                        HandManager.OnBoardSwapDone += ClearNearWordHint;
                    }
                }
            }
            _pendingNearWordLetters = null;

            // Forced idle nudge for authored crossing levels: walk the player through the seeded opportunity —
            // nudge each completing letter into the drop lane in order (O→COIN, then W→WORD). 2026-07-09 Spencer.
            SetupCrossHint(in e);

            // Authored EDIT nudge: walk the player through the seeded edit opportunities in order
            // (L6: LOVE then HOME). Parallel to the cross-hint but for edits, not drops. 2026-07-09 Spencer.
            SetupEditHint(in e);

            // ── Install the mode ──
            SetObjective(LevelTable.MakeObjective(in e));

            // ── Per-level music ── treasure-chest reward levels get their signature track (Glitter Blast);
            // every other mode uses the normal gameplay rotation. Both calls are idempotent, so this is a
            // safe no-op when the right music is already playing. 2026-06-17 Spencer.
            if (e.Mode == LevelMode.Vault)
                GameAudio.Instance?.PlayChestMusic();
            else
                GameAudio.Instance?.PlaySurvivalMusic();

            Debug.Log($"[LevelTable] Install L{levelIndex} → {e.Mode} ({e.Profile}) " +
                      $"len-seed:{e.LengthSeed} deton-seed:{e.DetonSeed} assist:{e.AssistFreq:0.00} " +
                      $"rise:{e.RiseCadence} | {e.Why}");

            // Pre-level "here's your goal" modal — pauses gameplay until the player taps PLAY so a
            // fresh tester always knows the objective before the board starts moving. 2026-06-15 Spencer.
            // If THIS level (the swap-unlock level) is being reached via a clear celebration, DEFER the intro
            // so the order reads cleared → UNLOCKED → objective — the UnlockModal's Claim shows it. On a
            // direct/debug jump (no clear celebration up), show it normally. 2026-07-06 Spencer.
            if (LevelMapPanel.MapFlowEnabled && LevelMapPanel.Instance != null)
            {
                // Candy-Crush loop: show the MAP for this level first; the play modal auto-pops on landing.
                Objective obj = Active; int lvl = levelIndex; // capture for the deferred callbacks
                System.Action showIntro = () => LevelIntroModal.Instance?.Show(obj, lvl);

                int justCleared = lvl - 1;
                if (!viaDebugJump && LevelMapPanel.IsBossLevel(justCleared))
                {
                    // Cleared a WORLD-ending (boss) level → trophy drop + "World Completed" modal → ADVANCE pages to
                    // the next world and shows its play modal. (Skipped on debug jumps — you just want the level.)
                    // 2026-07-13 Spencer.
                    LevelMapPanel.Instance.PresentWorldComplete(justCleared, lvl, showIntro);
                }
                else if (lvl == TutorialLocks.EDIT_UNLOCK_LEVEL && StageClearModal.UnlockRewardPending)
                {
                    // Cleared → MAP → UNLOCKED → hop → play: on the map, show the Swap unlock reward BEFORE the avatar
                    // hops (as a pre-hop action). Claiming it hops the avatar to the next node; the play modal then
                    // auto-pops on landing. 2026-07-13 Spencer.
                    System.Action showUnlock = () =>
                    {
                        if (UnlockModal.Instance != null)
                            UnlockModal.Instance.Show("Swap", "Swap tiles on the board to make new words!",
                                LoadUnlockIcon("Tiles/Icon_ItemIcon_Energy"),
                                onClaimed: () => LevelMapPanel.Instance.ContinueToHop());
                        else
                            LevelMapPanel.Instance.ContinueToHop();
                    };
                    LevelMapPanel.Instance.PresentThenIntro(lvl, showIntro, preHop: showUnlock);
                }
                else
                    LevelMapPanel.Instance.PresentThenIntro(lvl, showIntro);
            }
            else if (levelIndex == TutorialLocks.EDIT_UNLOCK_LEVEL && StageClearModal.UnlockRewardPending)
                LevelIntroModal.Instance?.SetDeferred(Active, levelIndex); // non-map: unlock modal shows the deferred intro
            else
                LevelIntroModal.Instance?.Show(Active, levelIndex);
        }

        // Load an icon that may be imported as a plain Texture (default type) rather than a Sprite. 2026-07-13.
        private static Sprite LoadUnlockIcon(string path)
        {
            var s = Resources.Load<Sprite>(path);
            if (s == null)
            {
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null) s = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return s;
        }

        /// <summary>Stage cleared via this objective: keep showing it (3/3) but stop it being the
        /// live win condition so the clear loop won't re-fire. It resets to a fresh objective when
        /// the stage-clear modal closes (see Update). 2026-06-09.</summary>
        public void RetireForStageClear()
        {
            if (Active != null) _retired = true;
        }

        private void Update()
        {
            // RulesEngine.Instance may not exist at Awake — (re)subscribe to OnWordScored
            // whenever it changes (mirrors how other managers verify their subscription).
            var re = RulesEngine.Instance;
            if (re != _subscribedTo)
            {
                if (_subscribedTo != null)
                {
                    _subscribedTo.OnWordScored        -= HandleWordScored;
                    _subscribedTo.OnTilesExploded     -= HandleTilesExploded;
                    _subscribedTo.OnResolutionComplete -= HandleResolutionComplete;
                }
                if (re != null)
                {
                    re.OnWordScored        += HandleWordScored;        // prime-time (make a word)
                    re.OnTilesExploded     += HandleTilesExploded;     // explode-time (tiles blow up)
                    re.OnResolutionComplete += HandleResolutionComplete; // collect drop-targets at the bottom
                }
                _subscribedTo = re;
            }

            // Hold a completed objective on the HUD through the stage-clear modal, then reset it
            // for the next stage once the modal closes (edge: was showing → now hidden).
            // "Active" = the modal is up AND not yet dismissing. Once Continue is tapped (IsDismissing), we install
            // the next stage → present the world map, so the map fades in OVER the still-opaque beige (no board flash
            // during the transition). 2026-07-14 Spencer.
            bool modalActive = StageClearModal.Instance != null
                               && StageClearModal.Instance.IsShowing
                               && !StageClearModal.Instance.IsDismissing;
            if (_retired && _modalWasShowing && !modalActive)
                ClearObjective(); // → auto-install fires below for the fresh stage
            _modalWasShowing = modalActive;

            // ── LEVEL-SEQUENCER INSTALL (validation MVP, 2026-06-15) ──
            // Once we're in a live Survival run with no objective set, install the level keyed off
            // the current stage index. The survival STAGE system already advances stage→stage; on
            // each new stage Active goes null (RetireForStageClear → ClearObjective on modal close),
            // so this fires once per level. LevelTable.Get(n) returns the mode + difficulty dials;
            // we install the mode AND apply that level's dials (seeding toggles, assist freq, rise
            // cadence, vault move budget). Replaces the old single hardcoded SetObjective(new IceObjective(4)).
            if (DEBUG_AUTO_OBJECTIVE && Active == null
                && SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                && !SurvivalManager.Instance.IsGameOver)
            {
                InstallLevel(SurvivalManager.Instance.CurrentStageIndex);
            }

            // Authored EDIT nudge: keep it live each frame while a sequence is active. SetupEditHint at
            // install can run BEFORE the rigged hand is dealt, so the completing letter may not be in hand
            // yet — retry here until it is (then pin the forced hint), keep the bounced card slot fresh, and
            // advance as each edit lands. No-op once the sequence completes (EndEditHint nulls the letters).
            // Mirrors why the drop cross-hint works: the letter has to be present before the hint sticks. 2026-07-09.
            if (_editHintLetters != null)
                RefreshEditHint();

            // Safety release for a deferred objective-complete payoff — if no fly-up landed to release it
            // (e.g. fly-up suppressed), fire the bing/checkmark once the deadline passes. 2026-07-10.
            if (_completePendingLand && Time.unscaledTime >= _pendingLandDeadline)
            {
                _completePendingLand = false;
                FireCompleteCelebration();  // no fly-up landed → no hit SFX to clash with, fire now
            }
            // After the fly-ups land, wait a hair (so the hit SFX clears) then fire the ding + checkmark.
            if (_celebratePending && Time.unscaledTime >= _celebrateTime)
            {
                _celebratePending = false;
                FireCompleteCelebration();
            }
            // Release the Level Completed modal a beat AFTER the ding + checkmark.
            if (_modalPending && Time.unscaledTime >= _modalPendingTime)
            {
                _modalPending = false;
                SurvivalManager.Instance?.CheckStageClear();
            }

            // Per-frame hook: HeroWord spawns its drop-targets, BreakRocks spawns + polls its rocks.
            // POLL-BASED objectives (BreakRocks) update their progress and reach IsComplete INSIDE
            // Tick — there's no event — so we must wrap Tick to refresh the HUD and fire the win on
            // the rising edge, or the objective would silently complete but never win. The
            // _firedComplete guard inside FireCompleteIfJust makes this safe for event-driven
            // objectives too (no double-fire). 2026-06-09.
            if (Active != null)
            {
                bool   wasComplete  = Active.IsComplete;
                string prevProgress = Active.ProgressText;
                Active.Tick();
                PushHud();
                // Poll-based progress changed this Tick (a vault cracked) → pop the counter. The
                // crack itself already plays the stone-splash FX; this is the HUD reaction.
                if (!wasComplete && Active.ProgressText != prevProgress)
                    HUDManager.Instance?.PulseObjective();
                FireCompleteIfJust(wasComplete);
            }

            // Backup poll: catch any drop-target sitting at row 0 before a rising row shoves it
            // back up. The timing-safe collect is on OnResolutionComplete; this mops up stragglers.
            CollectBottomDropTargets();

            // Self-heal: keep escort objects amber even if a resolution path momentarily re-stoned one
            // grey (Spencer caught a dark escort). Cheap sweep; no-op unless drop-targets exist. 2026-06-15.
            if (Active != null)
                GridManager.Instance?.EnsureDropTargetVisuals(RulesEngine.Instance);
        }

        private void HandleResolutionComplete(ResolutionCompleteEvent evt) => CollectBottomDropTargets();

        /// <summary>Collect any drop-target that reached the bottom row and credit the active
        /// objective (hero-word / drop-to-bottom). Idempotent — clears the cell so it can't
        /// double-count. Called on OnResolutionComplete AND polled each frame. 2026-06-09.</summary>
        private void CollectBottomDropTargets()
        {
            if (Active == null) return;
            var rules = RulesEngine.Instance;
            if (rules == null) return;
            var grid = GridManager.Instance;

            List<Vector2Int> collected = null;
            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                var cell = rules.GetCell(c, 0);
                if (cell == null || !cell.IsDropTarget) continue;

                // Wait until the tile has visually LANDED (not mid-fall) so the player actually
                // sees it hit the bottom before it's collected.
                var tile = grid != null ? grid.GetTile(c, 0) : null;
                if (tile != null && tile.IsAnimating) continue;

                // Celebrate at the landing spot.
                if (grid != null)
                    GameParticles.Instance?.PlayShimmerBurst(grid.CellToWorld(c, 0), 12);

                rules.ClearCell(c, 0);
                (collected ??= new List<Vector2Int>()).Add(new Vector2Int(c, 0));
            }
            if (collected == null) return;

            grid?.RemoveTiles(collected);   // visuals for just these cells
            GameAudio.Instance?.PlayChickenCluck(); // rubber chicken hit the bottom row — first 1.5s cluck
            HapticsManager.Strong();         // solid impact buzz to match the chicken landing. 2026-06-24 Spencer

            // Each collected escort FLIES UP to the Target icon (same animation as the HiddenWord letters).
            // The decrement + completion check happen when it LANDS, so the counter ticks as each one
            // arrives. Staggered so multiples pop together then land one-by-one. 2026-06-17 Spencer.
            var obj = Active;
            for (int i = 0; i < collected.Count; i++)
            {
                Vector3 startWorld = grid != null ? grid.CellToWorld(collected[i].x, collected[i].y) : Vector3.zero;
                HUDManager.Instance?.FlyEscortToTarget(startWorld, () =>
                {
                    if (obj == null) return;
                    bool wasComplete = obj.IsComplete;
                    obj.OnDropTargetCollected(1);
                    PushHud();
                    HUDManager.Instance?.PulseObjective();   // counter reacts to each collect
                    FireCompleteIfJust(wasComplete);
                }, i * 0.25f);
            }

            // Win-lock: HeroWord spawns ALL escorts upfront, so once none remain on the board, every
            // chicken has been collected and the level WILL be won when the fly-ups land. Flag it now so
            // a rising row can't sneak in during the fly-up window (IsComplete hasn't flipped yet). 2026-06-23.
            if (!rules.HasAnyDropTarget()) _escortWinPending = true;
        }

        /// <summary>Notify the active objective of a word scored via a path that does NOT
        /// fire RulesEngine.OnWordScored — specifically the board-swap scoring path in
        /// HandManager (the "dual scoring paths" tech debt). Keeps objectives accurate no
        /// matter how the player formed the word. 2026-06-08.</summary>
        public void NotifyWordScored(string word, int playerIndex, int chainStep = 0)
        {
            if (string.IsNullOrEmpty(word)) return;
            HandleWordScored(new WordScoredEvent { Word = word, PlayerIndex = playerIndex, ChainStep = chainStep });
        }

        private void HandleWordScored(WordScoredEvent evt)
        {
            if (Active == null) return;
            bool wasComplete = Active.IsComplete;
            Active.OnWordScored(evt);
            PushHud();

            FireCompleteIfJust(wasComplete);
        }

        /// <summary>Notify the active objective that a primed word's tiles EXPLODED, called
        /// directly from the live resolution path (RulesEngine.DoExplode) — which removes tiles
        /// via StepResult and does NOT fire OnTilesExploded. This is the path that actually runs;
        /// the OnTilesExploded event only fires from the dead ProcessDrop path. 2026-06-09.</summary>
        public void NotifyWordExploded(string word, int ownerPlayerIndex, bool primedByEdit = false)
        {
            if (Active == null || string.IsNullOrEmpty(word)) return;
            // Swap-tutorial levels: a word counts if a swap/EDIT was INVOLVED — either it TRIGGERED this blast
            // (LastResolutionWasSwap) OR the word itself was FORMED/PRIMED by an edit (primedByEdit). So the
            // natural combo "edit to prime the word, drop to set it off" still counts — the edit did the work.
            // 2026-07-10 Spencer (was: only edit-TRIGGERED pops counted, which read as "why didn't that count?").
            Debug.Log($"[PopDiag] pop '{word}' primedByEdit={primedByEdit} lastResWasSwap={RulesEngine.LastResolutionWasSwap} swapOnly={_swapExplosionsOnly} → {((!_swapExplosionsOnly || RulesEngine.LastResolutionWasSwap || primedByEdit) ? "COUNT" : "SKIP")}");
            if (_swapExplosionsOnly && !RulesEngine.LastResolutionWasSwap && !primedByEdit) return;
            bool wasComplete = Active.IsComplete;
            Active.OnWordExploded(word, ownerPlayerIndex);
            PushHud();
            FireCompleteIfJust(wasComplete, deferToLand: true); // hold the payoff until the fly-up letters land
        }

        /// <summary>A single detonation blew up <paramref name="chargedWordsInBlast"/> charged words AT ONCE
        /// (the combo size, reported once per blast by RulesEngine.DoExplode). Feeds ComboObjective. 2026-07-06.</summary>
        public void NotifyComboDetonated(int chargedWordsInBlast)
        {
            if (Active == null || chargedWordsInBlast <= 0) return;
            bool wasComplete = Active.IsComplete;
            Active.OnComboDetonated(chargedWordsInBlast);
            PushHud();
            FireCompleteIfJust(wasComplete, deferToLand: true); // hold the payoff until the fly-up letters land
        }

        /// <summary>Tiles EXPLODED — feed every destroyed primed word to detonation-based
        /// objectives ("explode N words"). Covers trigger, triggered, connected-group, and
        /// splash-sweep words alike — the complete "what blew up" list.</summary>
        private void HandleTilesExploded(TilesExplodedEvent evt)
        {
            if (Active == null || evt?.ExplodedWords == null) return;
            bool wasComplete = Active.IsComplete;
            for (int i = 0; i < evt.ExplodedWords.Count; i++)
            {
                var w = evt.ExplodedWords[i];
                Active.OnWordExploded(w.Word, w.OwnerPlayerIndex);
            }
            PushHud();
            FireCompleteIfJust(wasComplete);
        }

        private void FireCompleteIfJust(bool wasComplete, bool deferToLand = false)
        {
            if (Active != null && !wasComplete && Active.IsComplete && !_firedComplete)
            {
                _firedComplete = true;
                Debug.Log($"[Objective] COMPLETE: {Active.Title}");
                OnObjectiveComplete?.Invoke(Active);
                if (deferToLand)
                {
                    // Hold the bing + checkmark + stage-clear (→ Level Completed modal) until the completing
                    // word's fly-up letters LAND at the target HUD, so the payoff reads as a RESULT of them
                    // hitting — not before. NotifyObjectiveFlyUpsLanded (fired by the last fly-up letter)
                    // releases it; a fallback deadline releases it if no fly-up ever lands. 2026-07-10 Spencer.
                    _completePendingLand = true;
                    _pendingLandDeadline = Time.unscaledTime + 1.6f;
                }
                else
                {
                    FireCompleteCelebration();
                }
            }
        }

        private const float MODAL_AFTER_CHECKMARK_DELAY = 0.7f; // hold the Level Completed modal this long after the checkmark

        // The objective-complete payoff: bing + checkmark flash NOW, then the Level Completed modal a beat later.
        private void FireCompleteCelebration()
        {
            GameAudio.Instance?.PlayBing();                 // objective-complete "bing"
            HUDManager.Instance?.FlashObjectiveComplete();  // checkmark drop
            // Hold the stage clear (→ Level Completed modal) until AFTER the ding + checkmark land — the modal
            // shouldn't pop on top of them. Fired from Update once the delay passes. POLL-BASED objectives
            // (BreakRocks) finish inside Tick with no score delta, so the modal must be driven from here;
            // CheckStageClear is idempotent + guarded → safe to call for all types. 2026-07-10 Spencer.
            _modalPending = true;
            _modalPendingTime = Time.unscaledTime + MODAL_AFTER_CHECKMARK_DELAY;
        }

        /// <summary>Called when the completing word's fly-up letters LAND at the target HUD — releases a held
        /// objective-complete payoff so the bing/checkmark/modal fire AS A RESULT of the hit. No-op if nothing
        /// is pending. Called from WordDropFX on the last fly-up letter's landing. 2026-07-10 Spencer.</summary>
        public void NotifyObjectiveFlyUpsLanded()
        {
            if (!_completePendingLand) return;
            _completePendingLand = false;
            // Small gap so the tile's LANDING "hit" SFX clears before the ding + checkmark — they were bleeding
            // into each other. Fired from Update once the gap passes. 2026-07-10 Spencer.
            _celebratePending = true;
            _celebrateTime = Time.unscaledTime + CELEBRATE_AFTER_LAND_DELAY;
        }

        private void PushHud()
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetObjective(Active);
        }

        /// <summary>Called by a HiddenWord fly-up when its letter LANDS in the Target panel: the slot has
        /// just been revealed, so refresh the HUD (rock→letter) and fire stage-clear if the word's now
        /// complete. `wasComplete` is the objective's IsComplete state captured BEFORE this reveal.
        /// 2026-06-17 Spencer.</summary>
        public void NotifyHiddenReveal(bool wasComplete)
        {
            PushHud();
            FireCompleteIfJust(wasComplete);
        }

        private void OnDestroy()
        {
            if (_subscribedTo != null)
            {
                _subscribedTo.OnWordScored        -= HandleWordScored;
                _subscribedTo.OnTilesExploded     -= HandleTilesExploded;
                _subscribedTo.OnResolutionComplete -= HandleResolutionComplete;
            }
        }
    }
}
