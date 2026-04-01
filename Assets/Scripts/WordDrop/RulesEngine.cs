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

        // Board dimensions — change here to experiment with board sizes.
        // GridManager mirrors these values; keep in sync.
        // 7x6 = default (more room to form words before clogging)
        // 7x5 = experimental (tighter, faster, but clogs sooner)
        public const int COLS = 7;
        public const int ROWS = 5;

        private const int MIN_WORD_LENGTH   = 3;
        private const int MAX_WORD_LENGTH   = 7;
        private const int MAX_CHAIN_DEPTH   = 12;
        private const int CHAIN_BONUS       = 3;

        // Primed words expire after this many turns if not detonated.
        // 3 turns: balanced — enough time to set up detonations without board clutter.
        // 2 turns: too tight — words often expire before opponent can interact with them.
        // 4 turns = player primes on their turn, survives AI turn, survives player's
        // next turn, detonatable on the turn AFTER that. Generous enough to actually use.
        private const int PRIMED_EXPIRY_TURNS = 4;

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
        public const float DETONATION_SCORE_MULTIPLIER = 0f;
        public const int   BREAKER_BONUS               = 5;

        // Heat Fuse: primed words gain +1 detonation bonus per survived turn, capped
        public const int   HEAT_FUSE_PER_TURN          = 1;
        public const int   HEAT_FUSE_MAX_BONUS         = 5;

        // Overlap Fuse Extension: existing primed words get +1 fuse when a new prime overlaps them
        public const int   OVERLAP_FUSE_EXTENSION      = 1;
        public const int   MAX_OVERLAP_FUSE_BONUS      = 2;

        // TWO directions only — matches how humans read:
        // Horizontal: left to right
        // Vertical: top to bottom (high row to low row)
        // With 51k words, 4 directions creates too many false matches.
        // Players place words in reading order. ORE = O then R then E left-to-right.
        private static readonly int[][] _directions = new int[][]
        {
            new int[] { 1,  0 },   // horizontal: left → right
            new int[] { 0, -1 },   // vertical: top → bottom (row 5 down to row 0)
        };

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static RulesEngine Instance { get; private set; }

        // ── Board state ───────────────────────────────────────────────────────────

        private RulesCellData[,] _board = new RulesCellData[COLS, ROWS];

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
        public event RulesEventHandler<TilesExplodedEvent>     OnTilesExploded;
        public event RulesEventHandler<GravityCollapseEvent>   OnGravityCollapse;
        public event RulesEventHandler<ChainStepEvent>         OnChainStep;
        public event RulesEventHandler<ResolutionCompleteEvent> OnResolutionComplete;

        // ── Scored word tracking (prevents re-scoring same word at same cells) ────

        private HashSet<string> _scoredWordKeys = new HashSet<string>();

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
            Debug.Log("[RulesEngine] Awake — 7×6 board initialized. " +
                      "Detecting horizontal + vertical words (3–7 letters). NO diagonals.");
        }

        // ── Board manipulation ────────────────────────────────────────────────────

        public void SetCell(int col, int row, RulesCellData data)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = data;
        }

        public RulesCellData GetCell(int col, int row)
        {
            if (!InBounds(col, row)) return null;
            return _board[col, row];
        }

        public void ClearCell(int col, int row)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = null;
        }

        public void ClearBoard()
        {
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    _board[col, row] = null;

            _scoredWordKeys.Clear();
            _primedRegistry.Clear();
            _globalTurn = 0;

            Debug.Log("[RulesEngine] Board cleared.");
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
            if (string.IsNullOrEmpty(word)) return 0;

            int raw = 0;
            for (int i = 0; i < word.Length; i++)
                raw += LetterData.GetPoints(word[i]);

            float multiplier;
            if (word.Length <= 3)      multiplier = 1.0f;
            else if (word.Length == 4) multiplier = 1.5f;
            else                      multiplier = 2.0f;

            int score = Mathf.RoundToInt(raw * multiplier);

            Debug.Log($"[RulesEngine] CalculateWordScore('{word}'): " +
                      $"raw={raw} × {multiplier:F1} = {score}");

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
                Debug.Log($"[RulesEngine] ProcessDrop: col {col} is full — cannot place.");
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

            Debug.Log($"[RulesEngine] ProcessDrop: placed '{letter}' at ({col},{targetRow}) " +
                      $"player={playerIndex}");

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

            int chainStep  = 0;
            int totalScore = 0;
            int baseScoreAccum = 0;
            int chainBonusAccum = 0;
            int detonationBonusAccum = 0;
            bool keepChaining = true;

            while (keepChaining && chainStep < MAX_CHAIN_DEPTH)
            {
                keepChaining = false;

                // 5a. Scan entire board for ALL valid words
                List<RulesWordMatch> allWords = ScanEntireBoard();

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
                    if (chainStep == 0)
                        Debug.Log($"[RulesEngine] ProcessDrop: no new words found.");
                    else
                        Debug.Log($"[RulesEngine] Chain ended at step {chainStep} — no new words.");
                    break;
                }

                // Emit ChainStep event
                OnChainStep?.Invoke(new ChainStepEvent
                {
                    StepIndex     = chainStep,
                    NewWordsFound = newWords.Count,
                });

                if (chainStep > 0)
                    Debug.Log($"[RulesEngine] CHAIN step {chainStep}: {newWords.Count} new word(s)!");

                // 5c. Score, prime, and check triggers for each new word
                // Collect all cells that need to be exploded this step
                HashSet<int> primedIdsToExplode = new HashSet<int>();
                List<PrimedTriggeredEvent> triggeredEvents = new List<PrimedTriggeredEvent>();

                for (int w = 0; w < newWords.Count; w++)
                {
                    RulesWordMatch match = newWords[w];
                    string key = match.Word + "|" + match.CellKey;
                    _scoredWordKeys.Add(key);

                    // Calculate score with chain bonus
                    int baseScore  = CalculateWordScore(match.Word);
                    int chainBonus = (chainStep > 0) ? CHAIN_BONUS * chainStep : 0;
                    int finalScore = baseScore + chainBonus;
                    match.Score    = finalScore;
                    totalScore    += finalScore;
                    baseScoreAccum += baseScore;
                    chainBonusAccum += chainBonus;

                    Debug.Log($"[RulesEngine] Scored '{match.Word}': base={baseScore}" +
                              (chainBonus > 0 ? $" +chain({chainBonus})" : "") +
                              $" = {finalScore} pts  [step={chainStep}]");

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

                    // Prime the word — store score for double word bonus on detonation
                    int expiresOn = _globalTurn + PRIMED_EXPIRY_TURNS;
                    int primedId  = _primedRegistry.AddPrimedWord(
                        match.Word,
                        match.Cells,
                        playerIndex,
                        _globalTurn,
                        expiresOn,
                        finalScore);

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

                            Debug.Log($"[RulesEngine] TRIGGER! New word '{match.Word}' " +
                                      $"at cell ({cell.x},{cell.y}) overlaps primed " +
                                      $"'{pw.Word}' (id={pw.Id})");

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
                            if (oldPw.OverlapFuseBonusGranted >= MAX_OVERLAP_FUSE_BONUS) continue;

                            bool overlaps = false;
                            for (int c = 0; c < newPw.Cells.Count && !overlaps; c++)
                                for (int d = 0; d < oldPw.Cells.Count && !overlaps; d++)
                                    if (newPw.Cells[c] == oldPw.Cells[d])
                                        overlaps = true;

                            if (overlaps)
                            {
                                oldPw.ExpiresOnTurn += OVERLAP_FUSE_EXTENSION;
                                oldPw.OverlapFuseBonusGranted += OVERLAP_FUSE_EXTENSION;
                                alreadyExtended.Add(oldPw.Id);
                                Debug.Log($"[OverlapFuse] Legacy: NewPrimed={newPw.Word} overlapped Existing={oldPw.Word} " +
                                          $"-> +{OVERLAP_FUSE_EXTENSION} fuse (expires={oldPw.ExpiresOnTurn}, " +
                                          $"bonusGranted={oldPw.OverlapFuseBonusGranted})");
                            }
                        }
                    }
                }

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
                        Debug.Log($"[PrimedChain] Legacy path: chain-connected '{pw.Word}' (id={pw.Id})");
                    }
                }

                // 5d. Explode all triggered primed words
                if (primedIdsToExplode.Count > 0)
                {
                    keepChaining = true; // gravity may create new words

                    // Collect all cells to remove
                    HashSet<Vector2Int> allCellsToRemove = new HashSet<Vector2Int>();

                    int detonationBonus = 0;

                    foreach (int pid in primedIdsToExplode)
                    {
                        PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                        if (pw == null) continue;

                        for (int c = 0; c < pw.Cells.Count; c++)
                            allCellsToRemove.Add(pw.Cells[c]);

                        // Detonation bonus: base + heat fuse
                        int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                        int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                        int bonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS + heatBonus;
                        detonationBonus += bonus;
                        Debug.Log($"[RulesEngine] DETONATION BONUS: '{pw.Word}' explodes for +{bonus} pts " +
                                  $"(base={BREAKER_BONUS} heat={heatBonus} survived={survivedTurns})");

                        // Remove from registry
                        _primedRegistry.RemovePrimedWord(pid);
                        justPrimedThisResolution.Remove(pid);
                    }

                    totalScore += detonationBonus;
                    detonationBonusAccum += detonationBonus;

                    // Remove cells from board data
                    List<Vector2Int> removedList = new List<Vector2Int>(allCellsToRemove);
                    ExplodeCells(removedList);

                    // Emit TilesExploded
                    var explodeEvt = new TilesExplodedEvent
                    {
                        RemovedCells = removedList,
                        SourceWord   = "triggered",
                        ChainStep    = chainStep,
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

                        Debug.Log($"[RulesEngine] Gravity applied — {gravityMoves.Count} tile(s) moved.");
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
            int expired = _primedRegistry.ExpireOldWords(_globalTurn);
            if (expired > 0)
                Debug.Log($"[RulesEngine] Expired {expired} primed word(s) at turn {_globalTurn}");

            // FINAL cleanup: remove any primed words whose letters no longer match
            RemoveInvalidPrimedWords();

            result.BaseWordScoreTotal = baseScoreAccum;
            result.ChainBonusTotal = chainBonusAccum;
            result.DetonationBonusTotal = detonationBonusAccum;

            Debug.Log($"[RulesEngine] ProcessDrop complete: " +
                      $"words={result.ScoredWords.Count}, " +
                      $"explosions={result.Explosions.Count}, " +
                      $"chains={result.ChainSteps}, " +
                      $"totalScore={totalScore} (base={baseScoreAccum} chain={chainBonusAccum} det={detonationBonusAccum}), " +
                      $"primedRemaining={_primedRegistry.Count}");

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
                    Debug.Log($"[RulesEngine] Removing invalid primed word '{pw.Word}' (id={pw.Id}) —{mismatchDetail}");
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
                    removed++;
                }
            }
            Debug.Log($"[RulesEngine] ExplodeCells: removed {removed} cell(s) from data.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // GRAVITY — Compact columns downward in data layer (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compacts each column downward in the logical board data.
        /// Returns a dictionary mapping old positions to new positions for tiles that moved.
        /// </summary>
        private Dictionary<Vector2Int, Vector2Int> ApplyGravityInData()
        {
            var moves = new Dictionary<Vector2Int, Vector2Int>();

            for (int col = 0; col < COLS; col++)
            {
                // Collect surviving cells bottom to top
                List<RulesCellData> surviving = new List<RulesCellData>();
                for (int row = 0; row < ROWS; row++)
                {
                    if (_board[col, row] != null)
                        surviving.Add(_board[col, row]);
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

                // Repack from row 0 upward
                for (int i = 0; i < surviving.Count; i++)
                {
                    RulesCellData cell  = surviving[i];
                    int oldRow          = cell.Row;
                    int newRow          = i;

                    cell.Row = newRow;
                    cell.Col = col;
                    _board[col, newRow] = cell;

                    if (oldRow != newRow)
                    {
                        moves[new Vector2Int(col, oldRow)] = new Vector2Int(col, newRow);
                    }
                }
            }

            return moves;
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

                        // Find max contiguous run length
                        int maxLen = 0;
                        int safety = 0;
                        while (safety < MAX_WORD_LENGTH + 1)
                        {
                            int c = startCol + dc * maxLen;
                            int r = startRow + dr * maxLen;
                            if (!InBounds(c, r) || _board[c, r] == null) break;
                            maxLen++;
                            safety++;
                        }

                        if (maxLen < MIN_WORD_LENGTH) continue;

                        int maxWordLen = Mathf.Min(maxLen, MAX_WORD_LENGTH);

                        for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxWordLen; wordLen++)
                        {
                            char[]           chars = new char[wordLen];
                            List<Vector2Int> cells = new List<Vector2Int>(wordLen);
                            bool valid = true;

                            for (int step = 0; step < wordLen; step++)
                            {
                                int c = startCol + dc * step;
                                int r = startRow + dr * step;
                                RulesCellData cell = _board[c, r];
                                if (cell == null) { valid = false; break; }
                                chars[step] = char.ToUpper(cell.Letter);
                                cells.Add(new Vector2Int(c, r));
                            }

                            if (!valid) continue;

                            string candidate = new string(chars);
                            if (!WordDictionary.IsValidWord(candidate)) continue;

                            // Dedup by sorted cell positions — prevents same word
                            // being found in both directions (e.g. CAT left-to-right
                            // and CAT right-to-left at same cells)
                            string sortedCellKey = BuildSortedCellKey(cells);
                            string dedupKey = candidate + "|" + sortedCellKey;
                            if (seenKeys.Contains(dedupKey)) continue;
                            seenKeys.Add(dedupKey);

                            results.Add(new RulesWordMatch
                            {
                                Word      = candidate,
                                Cells     = cells,
                                Direction = (WordDirection)(dirIdx % 2),
                                Score     = 0,
                            });
                        }
                    }
                }
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORED KEY MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Purges scored word keys that reference any of the removed cell coordinates.
        /// After gravity, those coordinates will hold different tiles, so words
        /// forming at those positions should score fresh.
        /// </summary>
        private void PurgeScoredKeysForCells(List<Vector2Int> removedCells)
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

            if (keysToRemove.Count > 0)
                Debug.Log($"[RulesEngine] Purged {keysToRemove.Count} scored keys " +
                          $"for {removedCells.Count} removed cells.");
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
                Debug.Log($"[RulesEngine] FindNewWords({col},{row}) — cell is empty or out of bounds.");
                return results;
            }

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                // Walk backward
                int runStart = 0;
                int safety   = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col - dc * (runStart + 1);
                    int nr = row - dr * (runStart + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    runStart++;
                    safety++;
                }

                // Walk forward
                int runEnd = 0;
                safety = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col + dc * (runEnd + 1);
                    int nr = row + dr * (runEnd + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    runEnd++;
                    safety++;
                }

                int runLength = runStart + 1 + runEnd;
                if (runLength < MIN_WORD_LENGTH) continue;

                int absStartCol = col - dc * runStart;
                int absStartRow = row - dr * runStart;

                char[]           runChars = new char[runLength];
                List<Vector2Int> runCells = new List<Vector2Int>(runLength);
                bool runValid = true;

                for (int step = 0; step < runLength && runValid; step++)
                {
                    int nc = absStartCol + dc * step;
                    int nr = absStartRow + dr * step;
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) { runValid = false; break; }
                    runChars[step] = char.ToUpper(_board[nc, nr].Letter);
                    runCells.Add(new Vector2Int(nc, nr));
                }

                if (!runValid) continue;

                int maxLen = Mathf.Min(runLength, MAX_WORD_LENGTH);

                for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxLen; wordLen++)
                {
                    for (int startOffset = 0; startOffset <= runLength - wordLen; startOffset++)
                    {
                        char[]           wordChars = new char[wordLen];
                        List<Vector2Int> wordCells = new List<Vector2Int>(wordLen);

                        for (int k = 0; k < wordLen; k++)
                        {
                            wordChars[k] = runChars[startOffset + k];
                            wordCells.Add(runCells[startOffset + k]);
                        }

                        string candidate = new string(wordChars);
                        if (!WordDictionary.IsValidWord(candidate)) continue;

                        string sortedCellKey = BuildSortedCellKey(wordCells);
                        string dedupKey = candidate + "|" + sortedCellKey;
                        if (seenKeys.Contains(dedupKey)) continue;
                        seenKeys.Add(dedupKey);

                        string dirName = dirIdx % 2 == 0 ? "horizontal" : "vertical";
                        Debug.Log($"[RulesEngine] Found word '{candidate}' " +
                                  $"({dirName}) at cells {BuildCellKey(wordCells)}");

                        results.Add(new RulesWordMatch
                        {
                            Word      = candidate,
                            Cells     = wordCells,
                            Direction = (WordDirection)(dirIdx % 2),
                            Score     = 0,
                        });
                    }
                }
            }

            if (results.Count > 0)
                Debug.Log($"[RulesEngine] FindNewWords({col},{row}) → {results.Count} word(s) found.");

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

            // Find all words passing through the newly placed cell
            List<RulesWordMatch> allMatches = FindNewWords(col, targetRow);

            // Filter out already-scored words (so AI only sees new value)
            List<RulesWordMatch> newMatches = new List<RulesWordMatch>();
            for (int i = 0; i < allMatches.Count; i++)
            {
                string key = allMatches[i].Word + "|" + allMatches[i].CellKey;
                if (!_scoredWordKeys.Contains(key))
                {
                    var m = allMatches[i];
                    m.Score = CalculateWordScore(m.Word);
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
            Debug.Log("[RulesEngine] ══ BEGIN UNIT TESTS ══");

            // ── Test 1: 3-cell horizontal CAT ─────────────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 0] = new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = 0 };
            _board[2, 0] = new RulesCellData { Letter = 'T', Col = 2, Row = 0, PlayerIndex = 0 };

            var matches1 = FindNewWords(1, 0);
            bool found1 = ContainsWord(matches1, "CAT");
            Debug.Log($"[RulesEngine] Test 1 — horizontal 'CAT': " +
                      $"{(found1 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 2: diagonal CAT — should NOT find ─────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 1] = new RulesCellData { Letter = 'A', Col = 1, Row = 1, PlayerIndex = 0 };
            _board[2, 2] = new RulesCellData { Letter = 'T', Col = 2, Row = 2, PlayerIndex = 0 };

            var matches2 = FindNewWords(1, 1);
            bool found2 = ContainsWord(matches2, "CAT");
            Debug.Log($"[RulesEngine] Test 2 — diagonal 'CAT': " +
                      $"{(!found2 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 3: CalculateWordScore ─────────────────────────────────────────
            int scoreCat  = CalculateWordScore("CAT");   // C(3)+A(1)+T(1)=5 × 1.0 = 5
            int scoreFire = CalculateWordScore("FIRE");  // F(4)+I(1)+R(1)+E(1)=7 × 1.5 = 11 (rounded)
            int scoreSlate = CalculateWordScore("SLATE"); // S(1)+L(1)+A(1)+T(1)+E(1)=5 × 2.0 = 10
            bool test3 = (scoreCat == 5) && (scoreFire == 11) && (scoreSlate == 10);
            Debug.Log($"[RulesEngine] Test 3 — CalculateWordScore: CAT={scoreCat}(exp5) " +
                      $"FIRE={scoreFire}(exp11) SLATE={scoreSlate}(exp10) " +
                      $"{(test3 ? "✓ PASS" : "✗ FAIL")}");

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
            Debug.Log($"[RulesEngine] Test 4 — ProcessDrop 'T' forms CAT: " +
                      $"scored={wordScoredFired} primed={wordPrimedFired} " +
                      $"{(test4 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 5: SimulateDrop filters already-scored words ───────────────────
            // CAT is already scored at cells (0,0),(1,0),(2,0).
            // Simulating a drop that would form CAT again should return empty
            // (since CAT at those cells is already in _scoredWordKeys).
            // But let's test a fresh simulation on a different position.
            List<RulesWordMatch> simMatches = SimulateDrop(3, 'S', 1);
            // 'S' at (3,0) — horizontal: ...T S — "ATS"? unlikely to be valid
            // This mainly verifies SimulateDrop runs without error.
            Debug.Log($"[RulesEngine] Test 5 — SimulateDrop('S', col=3): " +
                      $"{simMatches.Count} match(es) found ✓ (no crash)");

            // ── Test 6: SimulateDropWithTriggerCheck ────────────────────────────────
            // CAT is primed. Simulate placing 'C' at col 1, row 1 to form ACE vertically.
            // A at (1,0) is part of primed CAT.
            _board[1, 1] = new RulesCellData { Letter = 'C', Col = 1, Row = 1, PlayerIndex = 0 };

            bool wouldTrigger;
            List<RulesWordMatch> trigMatches = SimulateDropWithTriggerCheck(1, 'E', 0, out wouldTrigger);

            // Clean up the manually placed cell
            _board[1, 1] = null;

            Debug.Log($"[RulesEngine] Test 6 — SimulateDropWithTriggerCheck: " +
                      $"wouldTrigger={wouldTrigger} matches={trigMatches.Count}");

            // ── Summary ───────────────────────────────────────────────────────────
            bool allPassed = found1 && !found2 && test3 && test4;
            Debug.Log($"[RulesEngine] ══ UNIT TESTS {(allPassed ? "ALL PASSED ✓" : "SOME FAILED ✗")} ══");

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
                else
                    Debug.Log($"[RulesEngine] FilterSubstring: '{words[i].Word}' removed as substring of longer word");
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
            public Dictionary<Vector2Int, Vector2Int> GravityMoves;
            public int TotalScore;
            public bool ChainContinues;
            public int ChainTriggeredCount; // primed words triggered via chain connectivity
        }

        // ── Step-by-step state ───────────────────────────────────────────────────────

        private ResolutionPhase _currentPhase = ResolutionPhase.Idle;
        private int _stepPlayerIndex;
        private int _stepChainDepth;
        private int _stepTotalScore;
        private HashSet<int> _stepJustPrimed;
        private HashSet<string> _stepScoredKeys;
        private List<RulesWordMatch> _stepPendingWords;
        private List<int> _stepPendingTriggers;

        // ── Public accessors ─────────────────────────────────────────────────────────

        public ResolutionPhase CurrentPhase => _currentPhase;
        public bool IsResolving => _currentPhase != ResolutionPhase.Idle && _currentPhase != ResolutionPhase.Complete;

        // ── BeginDrop ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start a new step-by-step resolution. Places the tile and returns.
        /// Call NextStep() repeatedly to advance through the resolution.
        /// </summary>
        public StepResult BeginDrop(int col, char letter, int playerIndex)
        {
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0) return null;

            var cellData = new RulesCellData
            {
                Letter      = char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
            };
            _board[col, targetRow] = cellData;

            _stepPlayerIndex   = playerIndex;
            _stepChainDepth    = 0;
            _stepTotalScore    = 0;
            _stepJustPrimed    = new HashSet<int>();
            _stepScoredKeys    = new HashSet<string>();
            _stepPendingWords  = null;
            _stepPendingTriggers = null;
            _currentPhase      = ResolutionPhase.TileDropped;

            // Expiry moved to FinalizeDrop — gives last-turn detonation chance

            Debug.Log($"[RulesEngine] BeginDrop: placed '{letter}' at ({col},{targetRow}) player={playerIndex}");

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = targetRow };
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
        public StepResult BeginRewrite(int col, int row, char newLetter, int playerIndex)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;

            RulesCellData existing = _board[col, row];
            if (existing == null) return null;

            // Block rewriting primed tiles
            var primedAtCell = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primedAtCell != null && primedAtCell.Count > 0) return null;

            char oldLetter = existing.Letter;

            // Overwrite the cell
            var cellData = new RulesCellData
            {
                Letter      = char.ToUpper(newLetter),
                Col         = col,
                Row         = row,
                PlayerIndex = playerIndex,
            };
            _board[col, row] = cellData;

            // Purge scored word keys that reference this cell so new words can be detected
            PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

            // Invalidate any primed words that included the old letter at this cell
            RemoveInvalidPrimedWords();

            // Initialize step-by-step resolution (same as BeginDrop)
            _stepPlayerIndex     = playerIndex;
            _stepChainDepth      = 0;
            _stepTotalScore      = 0;
            _stepJustPrimed      = new HashSet<int>();
            _stepScoredKeys      = new HashSet<string>();
            _stepPendingWords    = null;
            _stepPendingTriggers = null;
            _currentPhase        = ResolutionPhase.TileDropped;

            Debug.Log($"[RulesEngine] BeginRewrite: replaced '{oldLetter}' with '{newLetter}' " +
                      $"at ({col},{row}) player={playerIndex}");

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = row };
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
                Debug.Log("[RulesEngine] FinalizeDrop: already Idle — skipping (idempotent guard).");
                return;
            }

            // Expire primed words AFTER resolution (not before).
            // This gives players one last chance to detonate on the expiry turn.
            int expired = _primedRegistry.ExpireOldWords(_globalTurn);
            if (expired > 0)
                Debug.Log($"[RulesEngine] FinalizeDrop: expired {expired} primed word(s)");

            RemoveInvalidPrimedWords();
            _globalTurn++;
            _currentPhase = ResolutionPhase.Idle;

            _stepJustPrimed      = null;
            _stepScoredKeys      = null;
            _stepPendingWords    = null;
            _stepPendingTriggers = null;

            Debug.Log($"[RulesEngine] FinalizeDrop: turn incremented to {_globalTurn}, phase=Idle.");
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

            List<RulesWordMatch> allWords = ScanEntireBoard();
            allWords = FilterSubstringWords(allWords);

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
                Debug.Log($"[RulesEngine] DoDetectWords: no new words — resolution complete (chainDepth={_stepChainDepth}).");
                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingWords = newWords;
            _currentPhase = ResolutionPhase.WordsDetected;

            Debug.Log($"[RulesEngine] DoDetectWords: found {newWords.Count} new word(s) at chainDepth={_stepChainDepth}.");

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

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];
                string key = match.Word + "|" + match.CellKey;

                int baseScore  = CalculateWordScore(match.Word);
                int chainBonus = (_stepChainDepth > 0) ? CHAIN_BONUS * _stepChainDepth : 0;
                int finalScore = baseScore + chainBonus;
                match.Score    = finalScore;
                _stepTotalScore += finalScore;

                _scoredWordKeys.Add(key);
                _stepScoredKeys.Add(key);

                // Prime in registry
                int expiresOn = _globalTurn + PRIMED_EXPIRY_TURNS;
                int primedId  = _primedRegistry.AddPrimedWord(
                    match.Word,
                    match.Cells,
                    _stepPlayerIndex,
                    _globalTurn,
                    expiresOn,
                    finalScore);

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

                Debug.Log($"[RulesEngine] DoScoreAndPrime: '{match.Word}' base={baseScore}" +
                          (chainBonus > 0 ? $" +chain({chainBonus})" : "") +
                          $" = {finalScore} pts  [chain={_stepChainDepth}] primedId={primedId}");
            }

            // ── Overlap Fuse Extension ──────────────────────────────────────────
            // Newly primed words can extend the fuse of already-existing primed words
            // that share at least one tile. At most +1 per existing word per resolution.
            if (_stepJustPrimed.Count > 0)
            {
                HashSet<int> alreadyExtended = new HashSet<int>();

                foreach (int newId in _stepJustPrimed)
                {
                    var newPw = _primedRegistry.GetById(newId);
                    if (newPw == null || newPw.Cells == null) continue;

                    for (int p = 0; p < _primedRegistry.Count; p++)
                    {
                        var oldPw = _primedRegistry.GetByIndex(p);
                        if (oldPw == null) continue;
                        if (_stepJustPrimed.Contains(oldPw.Id)) continue; // skip newly primed
                        if (alreadyExtended.Contains(oldPw.Id)) continue; // one extension per resolution
                        if (oldPw.OverlapFuseBonusGranted >= MAX_OVERLAP_FUSE_BONUS) continue; // at cap

                        // Check if they share any tile
                        bool overlaps = false;
                        for (int c = 0; c < newPw.Cells.Count && !overlaps; c++)
                            for (int d = 0; d < oldPw.Cells.Count && !overlaps; d++)
                                if (newPw.Cells[c] == oldPw.Cells[d])
                                    overlaps = true;

                        if (overlaps)
                        {
                            oldPw.ExpiresOnTurn += OVERLAP_FUSE_EXTENSION;
                            oldPw.OverlapFuseBonusGranted += OVERLAP_FUSE_EXTENSION;
                            alreadyExtended.Add(oldPw.Id);
                            Debug.Log($"[OverlapFuse] NewPrimed={newPw.Word} overlapped Existing={oldPw.Word} " +
                                      $"-> +{OVERLAP_FUSE_EXTENSION} fuse (expires={oldPw.ExpiresOnTurn}, " +
                                      $"bonusGranted={oldPw.OverlapFuseBonusGranted})");
                        }
                    }
                }
            }

            _currentPhase = ResolutionPhase.WordsScored;

            return new StepResult
            {
                Phase = ResolutionPhase.WordsScored,
                ScoredWords = scoredEvents,
                TotalScore = _stepTotalScore,
            };
        }

        private StepResult DoCheckTriggers()
        {
            var triggeredIds = new HashSet<int>();
            var triggerEvents = new List<PrimedTriggeredEvent>();

            Debug.Log($"[RulesEngine] DoCheckTriggers: {_stepPendingWords.Count} new word(s), " +
                      $"{_primedRegistry.Count} primed word(s) on board, " +
                      $"{_stepJustPrimed.Count} just-primed this resolution");

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];

                for (int c = 0; c < match.Cells.Count; c++)
                {
                    Vector2Int cell = match.Cells[c];
                    List<PrimedWordRegistry.PrimedWord> overlapping =
                        _primedRegistry.GetPrimedWordsContaining(cell);

                    for (int p = 0; p < overlapping.Count; p++)
                    {
                        PrimedWordRegistry.PrimedWord pw = overlapping[p];

                        // Cannot trigger words primed during this resolution
                        if (_stepJustPrimed.Contains(pw.Id))
                        {
                            Debug.Log($"[RulesEngine] DoCheckTriggers: SKIPPED primed '{pw.Word}' (id={pw.Id}) — was just-primed this resolution");
                            continue;
                        }

                        if (triggeredIds.Contains(pw.Id))
                            continue;

                        triggeredIds.Add(pw.Id);

                        Debug.Log($"[RulesEngine] DoCheckTriggers: '{match.Word}' triggers primed '{pw.Word}' (id={pw.Id}) at ({cell.x},{cell.y})");

                        triggerEvents.Add(new PrimedTriggeredEvent
                        {
                            TriggeredWord    = pw.Word,
                            TriggeredCells   = new List<Vector2Int>(pw.Cells),
                            TriggerWord      = match.Word,
                            OverlapCell      = cell,
                            OwnerPlayerIndex = pw.OwnerPlayer,
                            PrimedWordId     = pw.Id,
                        });

                        // Fire event for listeners (tutorial, etc.)
                        OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                    }
                }
            }

            // ── Connected chain expansion ──
            // If any primed words were directly triggered, find all connected primed words
            // (transitively via shared tiles) and include them in the detonation group.
            int chainTriggeredCount = 0;
            if (triggeredIds.Count > 0)
            {
                var connectedGroup = _primedRegistry.FindConnectedGroup(triggeredIds, _stepJustPrimed);
                for (int g = 0; g < connectedGroup.Count; g++)
                {
                    var pw = connectedGroup[g];
                    if (triggeredIds.Contains(pw.Id)) continue; // already a direct trigger

                    triggeredIds.Add(pw.Id);
                    chainTriggeredCount++;

                    Debug.Log($"[PrimedChain] Chain-connected: '{pw.Word}' (id={pw.Id}) added to detonation group");

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
                    Debug.Log($"[PrimedChain] ConnectedGroup size={triggeredIds.Count} " +
                              $"(direct={triggeredIds.Count - chainTriggeredCount}, chain={chainTriggeredCount}) " +
                              $"words=[{string.Join(", ", triggerEvents.ConvertAll(e => e.TriggeredWord))}]");
                }
            }

            if (triggeredIds.Count == 0)
            {
                // No triggers — resolution done for this chain
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;

                Debug.Log($"[RulesEngine] DoCheckTriggers: no triggers — resolution complete.");

                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingTriggers = new List<int>(triggeredIds);
            _currentPhase = ResolutionPhase.TriggersFound;

            return new StepResult
            {
                Phase = ResolutionPhase.TriggersFound,
                Triggers = triggerEvents,
                TotalScore = _stepTotalScore,
                ChainTriggeredCount = chainTriggeredCount,
            };
        }

        private StepResult DoExplode()
        {
            var allExplodedCells = new List<Vector2Int>();
            int detonationBonus = 0;

            for (int i = 0; i < _stepPendingTriggers.Count; i++)
            {
                int pid = _stepPendingTriggers[i];
                PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                if (pw == null) continue;

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Vector2Int cell = pw.Cells[c];
                    if (InBounds(cell.x, cell.y) && _board[cell.x, cell.y] != null)
                    {
                        _board[cell.x, cell.y] = null;
                        allExplodedCells.Add(cell);
                    }
                }

                int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                int bonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS + heatBonus;
                detonationBonus += bonus;

                Debug.Log($"[RulesEngine] DoExplode: '{pw.Word}' (id={pid}) exploded, +{bonus} pts " +
                          $"(heat={heatBonus} survived={survivedTurns})");

                _primedRegistry.RemovePrimedWord(pid);
                _stepJustPrimed.Remove(pid);
            }

            _stepTotalScore += detonationBonus;

            // Purge scored word keys for exploded cells
            PurgeScoredKeysForCells(allExplodedCells);

            _stepPendingTriggers = null;
            _currentPhase = ResolutionPhase.Exploding;

            return new StepResult
            {
                Phase = ResolutionPhase.Exploding,
                ExplodedCells = allExplodedCells,
                TotalScore = _stepTotalScore,
            };
        }

        private StepResult DoGravity()
        {
            var gravityMoves = ApplyGravityInData();

            if (gravityMoves.Count > 0)
            {
                _primedRegistry.UpdateCellPositions(gravityMoves);
                Debug.Log($"[RulesEngine] DoGravity: {gravityMoves.Count} tile(s) moved.");
            }

            RemoveInvalidPrimedWords();

            _stepChainDepth++;
            _currentPhase = ResolutionPhase.GravityApplied;

            Debug.Log($"[RulesEngine] DoGravity: chainDepth now {_stepChainDepth}, looping back to detect.");

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
        public char Letter      { get; set; }
        public int  Col         { get; set; }
        public int  Row         { get; set; }
        public int  PlayerIndex { get; set; }
    }

    public enum WordDirection
    {
        Horizontal = 0,
        Vertical   = 1,
    }

    public class RulesWordMatch
    {
        public string           Word      { get; set; }
        public List<Vector2Int> Cells     { get; set; }
        public WordDirection    Direction { get; set; }
        public int              Score     { get; set; }

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
