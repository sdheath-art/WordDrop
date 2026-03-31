using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Central turn-flow controller for the Scrabble-drop game.
    /// Replaces MatchManager + hand coordination from HandManager.
    ///
    /// Owns:
    ///   - currentTurn (0-based global turn index)
    ///   - currentPlayer (0=human, 1=AI)
    ///   - playerTurns[2] — how many drops each player has made
    ///   - swapsRemaining[2] — 3 each at start
    ///   - PlayerHand[2] — letter hands for both players
    ///   - shared TileBag
    ///
    /// Exposes:
    ///   StartMatch(), DropLetter(col, letter), UseSwap(handSlot)
    ///
    /// Calls RulesEngine.ProcessDrop() for game logic, then emits events
    /// for visual bridge / HUD to consume.
    ///
    /// Match ends when playerTurns[0] >= MAX_TURNS && playerTurns[1] >= MAX_TURNS.
    /// </summary>
    public class MatchController : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        // Per-player turn count. Tuning: 12=fast, 15=default, 20=long
        public const int MAX_TURNS       = 15;
        public const int INITIAL_SWAPS   = 3;  // per player
        public const int PLAYER_HUMAN    = 0;
        public const int PLAYER_AI       = 1;
        public const int NUM_PLAYERS     = 2;

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static MatchController Instance { get; private set; }

        // ── State ─────────────────────────────────────────────────────────────────

        private int   _currentTurn   = 0;  // global turn counter (0-based, increments each drop/swap)
        private int   _currentPlayer = 0;  // 0=human, 1=AI

        private int[] _playerTurns      = new int[NUM_PLAYERS];   // drops per player
        private int[] _swapsRemaining   = new int[NUM_PLAYERS];

        private PlayerHand[] _hands = new PlayerHand[NUM_PLAYERS];
        private TileBag      _bag;

        private bool _isMatchActive  = false;
        private bool _isGameOver    = false;
        private bool _isProcessing  = false; // prevents re-entrant drops
        private bool _isSuddenDeath = false; // tie at end of turns → next score wins

        public bool IsSuddenDeath => _isSuddenDeath;

        // ── Events ────────────────────────────────────────────────────────────────

        public event RulesEventHandler<TurnEndEvent>      OnTurnEnd;
        public event RulesEventHandler<HandRefilledEvent>  OnHandRefilled;
        public event RulesEventHandler<MatchEndEvent>      OnMatchEnd;
        public event RulesEventHandler<SwapUsedEvent>      OnSwapUsed;
        public event RulesEventHandler<RewriteUsedEvent>   OnRewriteUsed;

        // ── Public read-only accessors ────────────────────────────────────────────

        public int  CurrentTurn       => _currentTurn;
        public int  CurrentPlayer     { get => _currentPlayer; set => _currentPlayer = value; }
        public bool IsMatchActive     => _isMatchActive;
        public bool IsGameOver        => _isGameOver;
        public bool IsProcessing      => _isProcessing;

        public int  GetPlayerTurns(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return 0;
            return _playerTurns[playerIndex];
        }

        public int  GetSwapsRemaining(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return 0;
            return _swapsRemaining[playerIndex];
        }

        public PlayerHand GetHand(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return null;
            return _hands[playerIndex];
        }

        public TileBag Bag => _bag;

        /// <summary>Total turns across both players combined (for display as "Turn N/40").</summary>
        public int TotalTurnsUsed => _playerTurns[0] + _playerTurns[1];

        /// <summary>Maximum total turns = MAX_TURNS × 2.</summary>
        public int TotalMaxTurns => MAX_TURNS * NUM_PLAYERS;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Debug.Log("[MatchController] Awake");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // START MATCH
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Starts a fresh match. Resets all state, creates a new bag,
        /// fills both hands, clears the RulesEngine board.
        /// </summary>
        public void StartMatch()
        {
            Debug.Log("[MatchController] StartMatch()");

            // Reset state
            _currentTurn   = 0;
            _currentPlayer = PLAYER_HUMAN;
            _isMatchActive = true;
            _isGameOver    = false;
            _isProcessing  = false;
            _isSuddenDeath = false;

            for (int p = 0; p < NUM_PLAYERS; p++)
            {
                _playerTurns[p]    = 0;
                _swapsRemaining[p] = INITIAL_SWAPS;
            }

            // Create shared bag
            _bag = new TileBag();

            // Create hands
            _hands[PLAYER_HUMAN] = new PlayerHand(PLAYER_HUMAN);
            _hands[PLAYER_AI]    = new PlayerHand(PLAYER_AI);

            // Fill both hands
            _hands[PLAYER_HUMAN].FillAll(_bag);
            _hands[PLAYER_AI].FillAll(_bag);

            // Clear RulesEngine board
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.ClearBoard();
                RulesEngine.Instance.GlobalTurn = 0;
            }

            // Apply opening seed (places neutral letters on the board)
            OpeningSeed.ApplyRandomSeed(RulesEngine.Instance);

            // Sync visual board (shows seeded letters)
            if (GridManager.Instance != null)
            {
                GridManager.Instance.ClearAllCells();
                GridManager.Instance.RebuildFromRulesEngine(RulesEngine.Instance);
            }

            // Reset scores
            if (ScoreManager.Instance != null)
                ScoreManager.Instance.ResetScore();

            // Update HUD
            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.SetPlayerScore(0);
                HUDManager.Instance.SetAIScore(0);
                HUDManager.Instance.SetTurnsRemaining(MAX_TURNS * NUM_PLAYERS, MAX_TURNS * NUM_PLAYERS);
            }

            // Emit hand refilled events
            EmitHandRefilled(PLAYER_HUMAN);
            EmitHandRefilled(PLAYER_AI);

            AnalyticsManager.GameStart();

            Debug.Log($"[MatchController] Match started. " +
                      $"Human hand: {_hands[PLAYER_HUMAN].HandString()} " +
                      $"AI hand: {_hands[PLAYER_AI].HandString()} " +
                      $"Bag: {_bag.Count} tiles  Swaps: {INITIAL_SWAPS} each");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DROP LETTER
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// LEGACY — not called in live gameplay. Player/AI turns use the step-by-step
        /// path (BeginDrop/NextStep/FinalizeDrop → CompleteDropBookkeeping).
        /// Kept for test compatibility (RulesEngineTests uses ProcessDrop directly).
        /// </summary>
        public ResolutionResult DropLetter(int col, char letter, int handSlotIndex = -1)
        {
            if (!_isMatchActive)
            {
                Debug.LogWarning("[MatchController] DropLetter: match not active.");
                return null;
            }

            if (_isGameOver)
            {
                Debug.LogWarning("[MatchController] DropLetter: game already over.");
                return null;
            }

            if (_isProcessing)
            {
                Debug.LogWarning("[MatchController] DropLetter: already processing a drop.");
                return null;
            }

            int player = _currentPlayer;

            // Validate column
            if (RulesEngine.Instance == null)
            {
                Debug.LogError("[MatchController] DropLetter: RulesEngine not available.");
                return null;
            }

            if (!RulesEngine.Instance.IsColumnAvailable(col))
            {
                Debug.Log($"[MatchController] DropLetter: col {col} is full.");
                return null;
            }

            _isProcessing = true;

            Debug.Log($"[MatchController] DropLetter: player={player} col={col} letter='{letter}' " +
                      $"turn={_currentTurn} playerTurns[{player}]={_playerTurns[player]}");

            // 1. Call RulesEngine.ProcessDrop
            ResolutionResult result = RulesEngine.Instance.ProcessDrop(col, letter, player);

            // 2. Apply scores — MatchController is the sole score writer.
            if (result != null && result.TotalScore > 0)
            {
                if (ScoreManager.Instance != null)
                {
                    if (player == PLAYER_HUMAN)
                        ScoreManager.Instance.AddPlayerScore(result.TotalScore);
                    else
                        ScoreManager.Instance.AddAIScore(result.TotalScore);
                }

                // Update HUD scores
                if (HUDManager.Instance != null && ScoreManager.Instance != null)
                {
                    HUDManager.Instance.SetPlayerScore(ScoreManager.Instance.PlayerScore);
                    HUDManager.Instance.SetAIScore(ScoreManager.Instance.AIScore);
                }

                // NOTE: Word-found popups are NOT shown here.
                // GameVisualBridge.PlayWordScored() is the sole word-presentation path.
                // Showing them here would cause duplicate popups.

                Debug.Log($"[MatchController] Player {player} scored {result.TotalScore} pts " +
                          $"({result.ScoredWords.Count} word(s), {result.ChainSteps} chain step(s))");
            }

            // 3. Update drought tracker
            int score = (result != null) ? result.TotalScore : 0;
            if (score > 0)
                _hands[player].ResetDrought();
            else
                _hands[player].IncrementDrought();

            // 4. Refill hand slot
            if (handSlotIndex >= 0 && handSlotIndex < PlayerHand.HAND_SIZE)
            {
                _hands[player].DrawSlot(handSlotIndex, _bag);
                EmitHandRefilled(player);
            }

            // 5. Increment turn counters
            _playerTurns[player]++;
            _currentTurn++;

            // Update RulesEngine global turn
            if (RulesEngine.Instance != null)
                RulesEngine.Instance.GlobalTurn = _currentTurn;

            AnalyticsManager.Milestone("drop", _currentTurn);

            // 6. Update HUD turn counter
            int totalRemaining = (MAX_TURNS * NUM_PLAYERS) - TotalTurnsUsed;
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnsRemaining(totalRemaining, MAX_TURNS * NUM_PLAYERS);

            // 7. Emit TurnEnd
            var turnEndEvt = new TurnEndEvent
            {
                PlayerIndex      = player,
                PlayerTurnNumber = _playerTurns[player],
                GlobalTurnIndex  = _currentTurn - 1,
            };
            OnTurnEnd?.Invoke(turnEndEvt);

            Debug.Log($"[MatchController] TurnEnd: player={player} " +
                      $"playerTurns=[{_playerTurns[0]},{_playerTurns[1]}] " +
                      $"globalTurn={_currentTurn}");

            // 8. Check match end
            if (CheckMatchEnd())
            {
                _isProcessing = false;
                return result;
            }

            // 9. Switch player
            SwitchPlayer();

            _isProcessing = false;
            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // POST-RESOLUTION BOOKKEEPING (called by GameVisualBridge after step-by-step)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Performs all turn bookkeeping AFTER the visual bridge has completed
        /// step-by-step resolution: applies score, refills the hand slot,
        /// increments turn counters, emits TurnEnd, checks match end, switches player.
        /// This replaces the bookkeeping portion of DropLetter when using the
        /// step-by-step resolution flow.
        /// </summary>
        public void CompleteDropBookkeeping(int playerIndex, int totalScore, int handSlotIndex,
            int baseScore = -1, int chainBonus = -1, int detonationBonus = -1)
        {
            // Compact score log
            if (totalScore > 0)
            {
                string breakdown = (baseScore >= 0)
                    ? $"(base={baseScore}, chain={chainBonus}, det={detonationBonus})"
                    : "";
                Debug.Log($"[Score] P{playerIndex} +{totalScore} {breakdown}");
            }

            // 1. Apply score — MatchController is the SOLE ScoreManager writer
            if (totalScore > 0 && ScoreManager.Instance != null)
            {
                if (playerIndex == PLAYER_HUMAN)
                    ScoreManager.Instance.AddPlayerScore(totalScore);
                else
                    ScoreManager.Instance.AddAIScore(totalScore);

                // Sync HUD scores immediately so display is never stale
                if (HUDManager.Instance != null)
                {
                    HUDManager.Instance.SetPlayerScore(ScoreManager.Instance.PlayerScore);
                    HUDManager.Instance.SetAIScore(ScoreManager.Instance.AIScore);
                    HUDManager.Instance.SyncDisplayScores();
                }

                // Sudden death: any score ends the match immediately
                if (_isSuddenDeath)
                {
                    Debug.Log($"[MatchController] SUDDEN DEATH — P{playerIndex} scored {totalScore}! Match over.");
                    EndMatch("sudden_death");
                    return;
                }
            }

            // 2. Update drought tracker (before refill so draw benefits from it)
            if (totalScore > 0)
                _hands[playerIndex].ResetDrought();
            else
                _hands[playerIndex].IncrementDrought();

            // 3. Refill hand slot
            if (handSlotIndex >= 0 && handSlotIndex < PlayerHand.HAND_SIZE)
            {
                _hands[playerIndex].DrawSlot(handSlotIndex, _bag);
                EmitHandRefilled(playerIndex);
            }

            // 4. Increment turn counters
            _playerTurns[playerIndex]++;
            _currentTurn++;

            if (RulesEngine.Instance != null)
                RulesEngine.Instance.GlobalTurn = _currentTurn;

            AnalyticsManager.Milestone("drop", _currentTurn);

            // 5. Update HUD turn counter
            int totalRemaining = (MAX_TURNS * NUM_PLAYERS) - TotalTurnsUsed;
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnsRemaining(totalRemaining, MAX_TURNS * NUM_PLAYERS);

            // 6. Emit TurnEnd
            var turnEndEvt = new TurnEndEvent
            {
                PlayerIndex      = playerIndex,
                PlayerTurnNumber = _playerTurns[playerIndex],
                GlobalTurnIndex  = _currentTurn - 1,
            };
            OnTurnEnd?.Invoke(turnEndEvt);

            Debug.Log($"[MatchController] CompleteDropBookkeeping: player={playerIndex} " +
                      $"score={totalScore} playerTurns=[{_playerTurns[0]},{_playerTurns[1]}] " +
                      $"globalTurn={_currentTurn}");

            // 7. Check match end
            if (CheckMatchEnd())
                return;

            // 8. Switch player
            SwitchPlayer();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // USE SWAP
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Swaps a single hand card for the current player.
        /// Does NOT count as a drop turn — swapsRemaining decrements instead.
        /// Does NOT increment playerTurns. Does NOT switch player.
        /// The player still gets to drop after swapping.
        /// </summary>
        // RULE: Swap is free hand-smoothing, not a turn cost. Change this if swaps should be strategic.
        // Design intent: swaps do NOT consume the turn. The player can swap and then still drop
        // a letter on the same turn. This keeps swaps feeling like a quality-of-life feature
        // for smoothing bad hands rather than a strategic sacrifice. If you want swaps to cost
        // a turn, increment _playerTurns[player] and call SwitchPlayer() after the swap.
        public bool UseSwap(int handSlot)
        {
            if (!_isMatchActive || _isGameOver)
            {
                Debug.LogWarning("[MatchController] UseSwap: match not active or game over.");
                return false;
            }

            int player = _currentPlayer;

            if (_swapsRemaining[player] <= 0)
            {
                Debug.Log($"[MatchController] UseSwap: player {player} has no swaps remaining.");
                return false;
            }

            if (handSlot < 0 || handSlot >= PlayerHand.HAND_SIZE)
            {
                Debug.LogWarning($"[MatchController] UseSwap: invalid slot {handSlot}.");
                return false;
            }

            char oldLetter = _hands[player].GetSlot(handSlot);
            char newLetter = _hands[player].SwapSlot(handSlot, _bag);

            // SwapSlot returns the old letter; the new letter is now in the slot
            newLetter = _hands[player].GetSlot(handSlot);

            _swapsRemaining[player]--;

            Debug.Log($"[MatchController] UseSwap: player={player} slot={handSlot} " +
                      $"'{oldLetter}' → '{newLetter}' " +
                      $"swapsRemaining={_swapsRemaining[player]}");

            // Emit SwapUsed event
            var swapEvt = new SwapUsedEvent
            {
                PlayerIndex    = player,
                HandSlot       = handSlot,
                OldLetter      = oldLetter,
                NewLetter      = newLetter,
                SwapsRemaining = _swapsRemaining[player],
            };
            OnSwapUsed?.Invoke(swapEvt);

            // Emit HandRefilled so visuals update
            EmitHandRefilled(player);

            // Update HUD swap display
            if (HUDManager.Instance != null)
                HUDManager.Instance.ShowSwapCount(_swapsRemaining[player]);

            AnalyticsManager.ButtonTap("swap");

            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // REWRITE TILE
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Uses the Rewrite Tile action: replaces a player-owned board tile with
        /// a hand tile. Costs 1 swap charge, consumes the hand tile, and the turn
        /// is consumed after resolution (via CompleteDropBookkeeping).
        ///
        /// Returns true if the rewrite was accepted. The caller (HandManager) is
        /// responsible for running the resolution coroutine and calling
        /// CompleteDropBookkeeping afterward.
        /// </summary>
        public bool UseRewrite(int handSlot, int targetCol, int targetRow)
        {
            if (!_isMatchActive || _isGameOver)
            {
                Debug.LogWarning("[MatchController] UseRewrite: match not active.");
                return false;
            }

            int player = _currentPlayer;

            if (_swapsRemaining[player] <= 0)
            {
                Debug.Log("[MatchController] UseRewrite: no swaps remaining.");
                return false;
            }

            if (handSlot < 0 || handSlot >= PlayerHand.HAND_SIZE)
            {
                Debug.LogWarning($"[MatchController] UseRewrite: invalid hand slot {handSlot}.");
                return false;
            }

            if (RulesEngine.Instance == null) return false;
            var cell = RulesEngine.Instance.GetCell(targetCol, targetRow);
            if (cell == null)
            {
                Debug.Log($"[MatchController] UseRewrite: no tile at ({targetCol},{targetRow}).");
                return false;
            }

            // Check not primed
            var primedAtCell = RulesEngine.Instance.PrimedRegistry
                .GetPrimedWordsContaining(new Vector2Int(targetCol, targetRow));
            if (primedAtCell != null && primedAtCell.Count > 0)
            {
                Debug.Log($"[MatchController] UseRewrite: tile at ({targetCol},{targetRow}) is primed.");
                return false;
            }

            char handLetter = _hands[player].GetSlot(handSlot);
            char oldBoardLetter = cell.Letter;

            // Block same-letter rewrite — prevents re-scoring expired words for free
            if (handLetter == oldBoardLetter)
            {
                Debug.Log($"[MatchController] UseRewrite: same letter '{handLetter}' — no-op.");
                return false;
            }

            // Consume swap charge
            _swapsRemaining[player]--;

            // Clear the hand slot (will be refilled in CompleteDropBookkeeping)
            _hands[player].SetSlot(handSlot, '\0');

            Debug.Log($"[MatchController] UseRewrite: player={player} slot={handSlot} " +
                      $"'{handLetter}' replaces '{oldBoardLetter}' at ({targetCol},{targetRow}) " +
                      $"swapsRemaining={_swapsRemaining[player]}");

            // Emit event
            var evt = new RewriteUsedEvent
            {
                PlayerIndex    = player,
                HandSlot       = handSlot,
                HandLetter     = handLetter,
                OldBoardLetter = oldBoardLetter,
                TargetCol      = targetCol,
                TargetRow      = targetRow,
                SwapsRemaining = _swapsRemaining[player],
            };
            OnRewriteUsed?.Invoke(evt);

            if (HUDManager.Instance != null)
                HUDManager.Instance.ShowSwapCount(_swapsRemaining[player]);

            AnalyticsManager.ButtonTap("rewrite");

            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FORCE GAME OVER (safety fallback, e.g. grid full)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Immediately ends the match. Called as safety fallback when grid is full.
        /// </summary>
        public void ForceGameOver()
        {
            if (_isGameOver) return;
            Debug.Log("[MatchController] ForceGameOver called.");
            EndMatch("board_full");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // INTERNAL HELPERS
        // ═══════════════════════════════════════════════════════════════════════════

        private void SwitchPlayer()
        {
            _currentPlayer = (_currentPlayer + 1) % NUM_PLAYERS;
            Debug.Log($"[MatchController] Switched to player {_currentPlayer}");
        }

        private bool CheckMatchEnd()
        {
            int totalTurns = _playerTurns[PLAYER_HUMAN] + _playerTurns[PLAYER_AI];
            int maxTotal = MAX_TURNS * NUM_PLAYERS;

            if (totalTurns >= maxTotal && !_isSuddenDeath)
            {
                int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
                int aiScore     = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore     : 0;

                if (playerScore == aiScore)
                {
                    // Tie → sudden death: next score wins
                    _isSuddenDeath = true;
                    Debug.Log($"[MatchController] Tied {playerScore}-{aiScore} — entering SUDDEN DEATH!");
                    return false; // don't end, keep playing
                }

                Debug.Log($"[MatchController] All {maxTotal} turns used — match ending.");
                EndMatch("turn_limit");
                return true;
            }

            // Board full — no columns available
            if (RulesEngine.Instance != null)
            {
                bool anyOpen = false;
                for (int c = 0; c < GridManager.COLS; c++)
                {
                    if (RulesEngine.Instance.IsColumnAvailable(c))
                    {
                        anyOpen = true;
                        break;
                    }
                }
                if (!anyOpen)
                {
                    int pScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
                    int aScore = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore : 0;

                    if (pScore == aScore)
                    {
                        // Tie + board full → clear the board for sudden death
                        _isSuddenDeath = true;
                        Debug.Log($"[MatchController] Board full + tied {pScore}-{aScore} — clearing board for SUDDEN DEATH!");
                        RulesEngine.Instance.ClearBoard();
                        if (GridManager.Instance != null)
                        {
                            GridManager.Instance.ClearAllCells();
                            GridManager.Instance.RebuildFromRulesEngine(RulesEngine.Instance);
                        }
                        return false; // keep playing
                    }

                    Debug.Log("[MatchController] Board full — match ending.");
                    EndMatch("board_full");
                    return true;
                }
            }

            // During sudden death, both players alternate freely
            if (_isSuddenDeath) return false;

            // If current player is done but other isn't, skip to other player
            bool p0Done = _playerTurns[PLAYER_HUMAN] >= MAX_TURNS;
            bool p1Done = _playerTurns[PLAYER_AI]    >= MAX_TURNS;

            if (p0Done && _currentPlayer == PLAYER_HUMAN)
            {
                Debug.Log("[MatchController] Human done, switching to AI for remaining turns.");
                _currentPlayer = PLAYER_AI;
            }
            else if (p1Done && _currentPlayer == PLAYER_AI)
            {
                Debug.Log("[MatchController] AI done, switching to Human for remaining turns.");
                _currentPlayer = PLAYER_HUMAN;
            }

            return false;
        }

        private void EndMatch(string cause)
        {
            if (_isGameOver) return;

            _isGameOver    = true;
            _isMatchActive = false;

            int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            int aiScore     = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore     : 0;

            int winner;
            if (playerScore > aiScore)      winner = PLAYER_HUMAN;
            else if (aiScore > playerScore) winner = PLAYER_AI;
            else                            winner = -1; // tie

            string winnerStr = winner == PLAYER_HUMAN ? "Player" :
                               winner == PLAYER_AI    ? "AI"     : "Tie";

            Debug.Log($"[MatchController] Match ended! Cause={cause} " +
                      $"Player={playerScore} AI={aiScore} Winner={winnerStr} " +
                      $"Turns=[{_playerTurns[0]},{_playerTurns[1]}]");

            AnalyticsManager.GameOver(playerScore, cause: cause,
                wave: _playerTurns[PLAYER_HUMAN],
                duration: Time.timeSinceLevelLoad);

            // Emit MatchEnd event
            var matchEndEvt = new MatchEndEvent
            {
                WinnerPlayerIndex = winner,
                PlayerScore       = playerScore,
                AIScore           = aiScore,
                TotalTurnsEach    = MAX_TURNS,
            };
            OnMatchEnd?.Invoke(matchEndEvt);

            // Also update the legacy MatchManager if it exists, for backward compat
            // with GameOverUI and other consumers
            if (MatchManager.Instance != null)
            {
                MatchManager.Instance.SyncFromController(
                    playerScore, aiScore,
                    _playerTurns[PLAYER_HUMAN],
                    TotalTurnsUsed);
            }

            // Disable player input
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Transition to GameOver
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.GameOver);
        }

        private void EmitHandRefilled(int playerIndex)
        {
            if (_hands[playerIndex] == null) return;

            var evt = new HandRefilledEvent
            {
                PlayerIndex = playerIndex,
                Letters     = _hands[playerIndex].GetAllSlots(),
            };
            OnHandRefilled?.Invoke(evt);
        }

        // ── Convenience for checking if a player still has turns ──────────────────

        /// <summary>True if the given player has used all their turns.</summary>
        public bool IsPlayerDone(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return true;
            return _playerTurns[playerIndex] >= MAX_TURNS;
        }

        /// <summary>True if both players have exhausted all turns.</summary>
        public bool IsTurnLimitReached
            => _playerTurns[PLAYER_HUMAN] >= MAX_TURNS &&
               _playerTurns[PLAYER_AI] >= MAX_TURNS;
    }
}
