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
    ///   - swapsRemaining[2] — 2 each at start (hand card trades)
    ///   - rewritesRemaining[2] — 1 each at start (board tile replacements)
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
        public const int MAX_TURNS       = 12;
        public const int INITIAL_SWAPS    = 2;  // per player (hand card trades)
        public const int INITIAL_REWRITES = 1;  // per player (board tile replacements)
        public const int PLAYER_HUMAN    = 0;
        public const int PLAYER_AI       = 1;
        public const int NUM_PLAYERS     = 2;

        /// <summary>Effective max turns per player for the current match (6 for daily, MAX_TURNS for classic).</summary>
        public int EffectiveMaxTurns => DailyDropManager.IsDailyMode
            ? DailyDropManager.DAILY_TURNS : MAX_TURNS;

        /// <summary>Effective player count (1 for daily solo, NUM_PLAYERS for classic).</summary>
        public int EffectivePlayerCount => DailyDropManager.IsDailyMode ? 1 : NUM_PLAYERS;

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static MatchController Instance { get; private set; }

        // ── State ─────────────────────────────────────────────────────────────────

        private int   _currentTurn   = 0;  // global turn counter (0-based, increments each drop/swap)
        private int   _currentPlayer = 0;  // 0=human, 1=AI

        private int[] _playerTurns      = new int[NUM_PLAYERS];   // drops per player
        private int[] _swapsRemaining    = new int[NUM_PLAYERS];
        private int[] _rewritesRemaining = new int[NUM_PLAYERS];

        private PlayerHand[] _hands = new PlayerHand[NUM_PLAYERS];
        private TileBag      _bag;

        private bool _isMatchActive  = false;
        private bool _isGameOver    = false;
        private bool _isProcessing  = false; // prevents re-entrant drops
        private bool _isSuddenDeath = false; // tie at end of turns → next score wins
        private bool _isLastWord   = false; // overflow imminent → one final turn each at 3x
        private int  _lastWordTurnsRemaining = 0; // how many players still get their last word

        /// <summary>Set by resolution paths before CompleteDropBookkeeping. The first word formed this turn.</summary>
        public string LastTurnWord { get; set; }

        public bool IsSuddenDeath => _isSuddenDeath;
        public bool IsLastWord    => _isLastWord;

        /// <summary>Score multiplier for Last Word phase (3x).</summary>
        public const int LAST_WORD_MULTIPLIER = 3;

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
        public void BeginProcessing() { _isProcessing = true; }
        public void EndProcessing()   { _isProcessing = false; }

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

        public int  GetRewritesRemaining(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return 0;
            return _rewritesRemaining[playerIndex];
        }

        public PlayerHand GetHand(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return null;
            return _hands[playerIndex];
        }

        public TileBag Bag => _bag;

        /// <summary>Total turns across both players combined (for display as "Turn N/40").</summary>
        public int TotalTurnsUsed => DailyDropManager.IsDailyMode
            ? _playerTurns[0]
            : _playerTurns[0] + _playerTurns[1];

        /// <summary>Maximum total turns = EffectiveMaxTurns × EffectivePlayerCount.</summary>
        public int TotalMaxTurns => EffectiveMaxTurns * EffectivePlayerCount;

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
            // Enforce mutual exclusion — only one mode can be active
            if (DailyDropManager.IsDailyMode && BlitzManager.IsBlitzMode)
            {
                Debug.LogWarning("[MatchController] Both daily and blitz active — forcing classic.");
                DailyDropManager.IsDailyMode = false;
                BlitzManager.IsBlitzMode = false;
            }

            Debug.Log("[MatchController] StartMatch()");

            // Reset state
            _currentTurn   = 0;
            _currentPlayer = PLAYER_HUMAN;
            _isMatchActive = true;
            _isGameOver    = false;
            _isProcessing  = false;
            _isSuddenDeath = false;
            _isLastWord    = false;
            _lastWordTurnsRemaining = 0;

            for (int p = 0; p < NUM_PLAYERS; p++)
            {
                _playerTurns[p]      = 0;
                _swapsRemaining[p]   = INITIAL_SWAPS;
                _rewritesRemaining[p] = INITIAL_REWRITES;
            }

            // Create shared bag (seeded for daily mode, random otherwise)
            bool isDaily = DailyDropManager.IsDailyMode;
            if (isDaily)
            {
                _bag = new TileBag(DailyDropManager.GetDailySeed());
                Debug.Log($"[MatchController] Daily Drop mode — seeded bag (seed={DailyDropManager.GetDailySeed()})");
            }
            else
            {
                _bag = new TileBag();
            }

            // Create hands (no AI hand in solo modes: daily or blitz)
            bool soloMode = isDaily || BlitzManager.IsBlitzMode;
            _hands[PLAYER_HUMAN] = new PlayerHand(PLAYER_HUMAN);
            if (!soloMode)
            {
                _hands[PLAYER_AI] = new PlayerHand(PLAYER_AI);
            }
            else
            {
                _hands[PLAYER_AI] = null; // no AI in solo modes
            }

            // Fill hands
            _hands[PLAYER_HUMAN].FillAll(_bag);
            if (_hands[PLAYER_AI] != null)
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

            // Place initial bonus cells on the board
            if (RulesEngine.Instance != null && !BlitzManager.IsBlitzMode)
            {
                RulesEngine.Instance.PlaceInitialBonusCells();
                if (GridManager.Instance != null)
                    GridManager.Instance.RefreshBonusCellOverlays();
            }

            // Clear chain counter from previous match
            if (ChainCounter.Instance != null)
                ChainCounter.Instance.ResetForNewMatch();

            // Reset scores
            if (ScoreManager.Instance != null)
                ScoreManager.Instance.ResetScore();

            // Update HUD
            int totalMaxTurns = EffectiveMaxTurns * EffectivePlayerCount;
            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.SetPlayerScore(0);
                HUDManager.Instance.SetAIScore(0);
                if (BlitzManager.IsBlitzMode)
                    HUDManager.Instance.SetBlitzMode(true);
                else
                    HUDManager.Instance.SetBlitzMode(false);
                HUDManager.Instance.SetDailyMode(isDaily);
                HUDManager.Instance.SetTurnsRemaining(totalMaxTurns, totalMaxTurns);
            }

            // Start blitz timer if in blitz mode
            if (BlitzManager.IsBlitzMode && BlitzManager.Instance != null)
                BlitzManager.Instance.StartTimer();

            // Pick a rival for Classic mode
            if (!BlitzManager.IsBlitzMode && !isDaily && RivalSystem.Instance != null)
            {
                var rival = RivalSystem.Instance.PickRandomRival();
                if (HUDManager.Instance != null)
                    HUDManager.Instance.SetRivalName(rival.Name, rival.AccentColor);
            }

            // Rising rows: ON by default in Classic, OFF in Blitz (too short)
            // Interval 4 in Classic (gentler pressure), 2 in Blitz if ever enabled
            if (!BlitzManager.IsBlitzMode && !isDaily)
            {
                RisingRowManager.Enabled = true;
                RisingRowManager.TurnInterval = 4;
            }
            else
            {
                RisingRowManager.Enabled = false;
            }

            // Emit hand refilled events
            EmitHandRefilled(PLAYER_HUMAN);
            if (_hands[PLAYER_AI] != null)
                EmitHandRefilled(PLAYER_AI);

            AnalyticsManager.GameStart();

            string aiHandStr = _hands[PLAYER_AI] != null ? _hands[PLAYER_AI].HandString() : "(none)";
            string modeStr = isDaily ? "DAILY DROP" : BlitzManager.IsBlitzMode ? "BLITZ" : "CLASSIC";
            Debug.Log($"[MatchController] Match started [{modeStr}]. " +
                      $"Human hand: {_hands[PLAYER_HUMAN].HandString()} " +
                      $"AI hand: {aiHandStr} " +
                      $"Bag: {_bag.Count} tiles  Swaps: {INITIAL_SWAPS} each  Rewrites: {INITIAL_REWRITES} each " +
                      $"Turns: {EffectiveMaxTurns}x{EffectivePlayerCount}");
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
            int legacyTotalMax = TotalMaxTurns;
            int totalRemaining = legacyTotalMax - TotalTurnsUsed;
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnsRemaining(totalRemaining, legacyTotalMax);

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
            // ── Tutorial guard: skip turn counting, player switching, match-end checks.
            // Only refill the hand slot so the card deal animation works naturally.
            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
            {
                if (handSlotIndex >= 0 && handSlotIndex < PlayerHand.HAND_SIZE)
                {
                    _hands[playerIndex].DrawSlot(handSlotIndex, _bag);

                    // Re-rig the NEXT preview after DrawSlot's PreCacheNext overwrote it
                    char nextPreview = TutorialManager.NextPreviewLetter;
                    if (nextPreview != '\0')
                        _hands[playerIndex].SetCachedNextLetter(nextPreview);

                    EmitHandRefilled(playerIndex);
                }
                return;
            }

            // Compact score log
            if (totalScore > 0)
            {
                string breakdown = (baseScore >= 0)
                    ? $"(base={baseScore}, chain={chainBonus}, det={detonationBonus})"
                    : "";
                Debug.Log($"[Score] P{playerIndex} +{totalScore} {breakdown}");
            }

            // Last Word multiplier: 3x scoring during final desperation turns
            if (_isLastWord && totalScore > 0)
            {
                totalScore *= LAST_WORD_MULTIPLIER;
                Debug.Log($"[MatchController] LAST WORD 3x applied! Score: {totalScore / LAST_WORD_MULTIPLIER} → {totalScore}");
            }

            // 1. Apply score — MatchController is the SOLE ScoreManager writer
            if (totalScore > 0 && ScoreManager.Instance != null)
            {
                // Match arc bonuses (skip in solo modes — no opponent)
                bool isSoloMode = BlitzManager.IsBlitzMode || DailyDropManager.IsDailyMode;
                int turnsLeft = isSoloMode ? 999 : TotalMaxTurns - TotalTurnsUsed;
                int arcBonus = 0;
                int finalPush = MatchArcRules.GetFinalPushBonus(turnsLeft);
                if (finalPush > 0)
                {
                    arcBonus += finalPush;
                    Debug.Log($"[MatchArc] Final Push: +{finalPush} (turns left={turnsLeft})");
                    bool isHuman = (playerIndex == PLAYER_HUMAN);
                    if (BonusPopup.Instance != null)
                        BonusPopup.Instance.ShowFinalPush(finalPush, Vector3.up * 2f, isHuman);
                }
                bool isHumanPlayer = (playerIndex == PLAYER_HUMAN);
                int opponentScore = isHumanPlayer
                    ? (ScoreManager.Instance.AIScore) : (ScoreManager.Instance.PlayerScore);
                int myScore = isHumanPlayer
                    ? (ScoreManager.Instance.PlayerScore) : (ScoreManager.Instance.AIScore);
                int comeback = MatchArcRules.GetComebackBonus(myScore, opponentScore, turnsLeft);
                if (comeback > 0)
                {
                    arcBonus += comeback;
                    Debug.Log($"[MatchArc] Comeback: +{comeback} (trailing by {opponentScore - myScore})");
                    if (BonusPopup.Instance != null)
                        BonusPopup.Instance.ShowComeback(comeback, Vector3.up * 2.5f, isHumanPlayer);
                }
                totalScore += arcBonus;

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

                // Sudden death: any score ends the match immediately (but not during Last Word)
                if (_isSuddenDeath && !_isLastWord)
                {
                    Debug.Log($"[MatchController] SUDDEN DEATH — P{playerIndex} scored {totalScore}! Match over.");
                    EndMatch("sudden_death");
                    return;
                }
            }

            // Last Word: consume turn and end match when both players have gone
            if (_isLastWord)
            {
                ConsumeLastWordTurn();
                if (_isGameOver) return;
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
            int effTotalMax = TotalMaxTurns;
            int totalRemaining = effTotalMax - TotalTurnsUsed;
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnsRemaining(totalRemaining, effTotalMax);

            // 6. Emit TurnEnd
            var turnEndEvt = new TurnEndEvent
            {
                PlayerIndex      = playerIndex,
                PlayerTurnNumber = _playerTurns[playerIndex],
                GlobalTurnIndex  = _currentTurn - 1,
            };
            OnTurnEnd?.Invoke(turnEndEvt);

            // Track best combo (highest single-turn score)
            if (totalScore > 0 && playerIndex == PLAYER_HUMAN)
            {
                string comboMode = DailyDropManager.IsDailyMode ? "daily"
                    : BlitzManager.IsBlitzMode ? "blitz" : "classic";
                HighScoreManager.SubmitCombo(totalScore, comboMode);
            }

            // Update last word display with full turn total
            if (totalScore > 0 && LastWordDisplay.Instance != null && !string.IsNullOrEmpty(LastTurnWord))
            {
                LastWordDisplay.Instance.ShowWord(LastTurnWord, totalScore, playerIndex == PLAYER_HUMAN);
                LastTurnWord = null;
            }

            Debug.Log($"[MatchController] CompleteDropBookkeeping: player={playerIndex} " +
                      $"score={totalScore} playerTurns=[{_playerTurns[0]},{_playerTurns[1]}] " +
                      $"globalTurn={_currentTurn}");

            // 7. Check match end
            if (CheckMatchEnd())
                return;

            // 8. Switch player (skip in blitz — always human)
            if (!BlitzManager.IsBlitzMode)
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

        /// <summary>Refund 1 rewrite charge for a player (capped at INITIAL_REWRITES).</summary>
        public void RefundSwapCharge(int playerIndex)
        {
            if (playerIndex < 0 || playerIndex >= NUM_PLAYERS) return;
            if (_rewritesRemaining[playerIndex] < INITIAL_REWRITES)
            {
                _rewritesRemaining[playerIndex]++;
                Debug.Log($"[MatchController] Refunded rewrite charge for P{playerIndex}. " +
                          $"Remaining: {_rewritesRemaining[playerIndex]}");

                // Update HUD rewrite display
                if (playerIndex == _currentPlayer && HUDManager.Instance != null)
                    HUDManager.Instance.ShowRewriteCount(_rewritesRemaining[playerIndex]);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // REWRITE TILE
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Uses the Rewrite Tile action: replaces any non-primed board tile with
        /// a hand tile. Costs 1 rewrite charge, consumes the hand tile, and the turn
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

            if (_rewritesRemaining[player] <= 0)
            {
                Debug.Log("[MatchController] UseRewrite: no rewrites remaining.");
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

            // Consume rewrite charge
            _rewritesRemaining[player]--;

            // Clear the hand slot (will be refilled in CompleteDropBookkeeping)
            _hands[player].SetSlot(handSlot, '\0');

            Debug.Log($"[MatchController] UseRewrite: player={player} slot={handSlot} " +
                      $"'{handLetter}' replaces '{oldBoardLetter}' at ({targetCol},{targetRow}) " +
                      $"rewritesRemaining={_rewritesRemaining[player]}");

            // Emit event
            var evt = new RewriteUsedEvent
            {
                PlayerIndex    = player,
                HandSlot       = handSlot,
                HandLetter     = handLetter,
                OldBoardLetter = oldBoardLetter,
                TargetCol      = targetCol,
                TargetRow      = targetRow,
                SwapsRemaining = _rewritesRemaining[player],
            };
            OnRewriteUsed?.Invoke(evt);
            GameAudio.Instance?.PlayRewrite();

            if (HUDManager.Instance != null)
                HUDManager.Instance.ShowRewriteCount(_rewritesRemaining[player]);

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

        /// <summary>
        /// Trigger Last Word phase: both players get one final turn with 3x scoring.
        /// Called when rising rows would overflow instead of immediately ending.
        /// </summary>
        public void TriggerLastWord()
        {
            if (_isLastWord || _isGameOver) return;
            _isLastWord = true;
            _lastWordTurnsRemaining = 2; // both players get one turn
            Debug.Log("[MatchController] LAST WORD! Both players get one final turn at 3x scoring.");

            // Disable rising rows so the board doesn't overflow during last word
            RisingRowManager.Enabled = false;

            // Show HUD indicator via turn counter
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetTurnsRemaining(1, 1); // triggers "LAST TURN" display
        }

        /// <summary>
        /// Called after each last-word turn completes. Ends game when both players have gone.
        /// </summary>
        public void ConsumeLastWordTurn()
        {
            if (!_isLastWord) return;
            _lastWordTurnsRemaining--;
            Debug.Log($"[MatchController] Last Word turn consumed. Remaining: {_lastWordTurnsRemaining}");
            if (_lastWordTurnsRemaining <= 0)
            {
                Debug.Log("[MatchController] Last Word complete — ending match.");
                EndMatch("last_word");
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // INTERNAL HELPERS
        // ═══════════════════════════════════════════════════════════════════════════

        private void SwitchPlayer()
        {
            // In solo modes (daily/blitz), always stay on human player
            if (DailyDropManager.IsDailyMode || BlitzManager.IsBlitzMode)
            {
                _currentPlayer = PLAYER_HUMAN;
                return;
            }
            _currentPlayer = (_currentPlayer + 1) % NUM_PLAYERS;
            Debug.Log($"[MatchController] Switched to player {_currentPlayer}");
        }

        private bool CheckMatchEnd()
        {
            // Blitz mode: timer controls match end, not turn count
            if (BlitzManager.IsBlitzMode)
            {
                if (BlitzManager.Instance != null && BlitzManager.Instance.IsTimeUp)
                {
                    Debug.Log("[MatchController] Blitz time expired — ending match.");
                    EndMatch("blitz_time_up");
                    return true;
                }
                // Still check board full
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
                        Debug.Log("[MatchController] Blitz board full — ending match.");
                        EndMatch("board_full");
                        return true;
                    }
                }
                return false;
            }

            // Daily mode: solo, check only human turns
            if (DailyDropManager.IsDailyMode)
            {
                if (_playerTurns[PLAYER_HUMAN] >= EffectiveMaxTurns)
                {
                    Debug.Log($"[MatchController] Daily Drop — all {EffectiveMaxTurns} turns used. Match ending.");
                    EndMatch("turn_limit");
                    return true;
                }

                // Board full check
                if (RulesEngine.Instance != null)
                {
                    bool anyOpen = false;
                    for (int c = 0; c < GridManager.COLS; c++)
                    {
                        if (RulesEngine.Instance.IsColumnAvailable(c)) { anyOpen = true; break; }
                    }
                    if (!anyOpen)
                    {
                        Debug.Log("[MatchController] Daily Drop board full — ending match.");
                        EndMatch("board_full");
                        return true;
                    }
                }
                return false;
            }

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

            // Board full — players can now replace top tiles, so no game-over here.
            // Match ends only when turns run out (above).

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

            // Force-finish any running score count-up animations so HUD shows final values
            if (HUDManager.Instance != null)
                HUDManager.Instance.ForceFinishCountUp();

            int playerScore = ScoreManager.Instance != null ? ScoreManager.Instance.PlayerScore : 0;
            int aiScore     = ScoreManager.Instance != null ? ScoreManager.Instance.AIScore     : 0;

            int winner;
            if (BlitzManager.IsBlitzMode || DailyDropManager.IsDailyMode)
                winner = PLAYER_HUMAN; // solo mode — player always "wins"
            else if (playerScore > aiScore)      winner = PLAYER_HUMAN;
            else if (aiScore > playerScore) winner = PLAYER_AI;
            else                            winner = -1; // tie

            string winnerStr = winner == PLAYER_HUMAN ? "Player" :
                               winner == PLAYER_AI    ? "AI"     : "Tie";

            Debug.Log($"[MatchController] Match ended! Cause={cause} " +
                      $"Player={playerScore} AI={aiScore} Winner={winnerStr} " +
                      $"Turns=[{_playerTurns[0]},{_playerTurns[1]}]");

            // Record rival win/loss (Classic mode only)
            if (!BlitzManager.IsBlitzMode && !DailyDropManager.IsDailyMode && RivalSystem.Instance != null)
            {
                if (winner == PLAYER_HUMAN)
                    RivalSystem.Instance.RecordPlayerWin();
                else if (winner == PLAYER_AI)
                    RivalSystem.Instance.RecordPlayerLoss();
            }

            AnalyticsManager.GameOver(playerScore, cause: cause,
                wave: _playerTurns[PLAYER_HUMAN],
                duration: Time.timeSinceLevelLoad);

            // Emit MatchEnd event
            var matchEndEvt = new MatchEndEvent
            {
                WinnerPlayerIndex = winner,
                PlayerScore       = playerScore,
                AIScore           = aiScore,
                TotalTurnsEach    = EffectiveMaxTurns,
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
            // Blitz mode: human is never "done" by turn count — timer controls it.
            // AI doesn't exist in blitz.
            if (BlitzManager.IsBlitzMode)
            {
                if (playerIndex == PLAYER_AI) return true;
                return false; // human plays until timer runs out
            }
            // In daily mode, AI is always "done" (doesn't exist)
            if (DailyDropManager.IsDailyMode && playerIndex == PLAYER_AI) return true;
            return _playerTurns[playerIndex] >= EffectiveMaxTurns;
        }

        /// <summary>True if all active players have exhausted all turns.</summary>
        public bool IsTurnLimitReached
        {
            get
            {
                if (BlitzManager.IsBlitzMode) return false; // timer controls end
                if (DailyDropManager.IsDailyMode)
                    return _playerTurns[PLAYER_HUMAN] >= EffectiveMaxTurns;
                return _playerTurns[PLAYER_HUMAN] >= EffectiveMaxTurns &&
                       _playerTurns[PLAYER_AI] >= EffectiveMaxTurns;
            }
        }
    }
}
