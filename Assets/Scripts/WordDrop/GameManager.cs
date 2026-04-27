using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    public enum GameState
    {
        Menu,
        Playing,
        RoundOver,
        GameOver
    }

    /// <summary>
    /// Top-level game mode selector. Orthogonal to GameState: mode decides WHAT
    /// is being played (Level vs Survival), state decides WHERE in the flow.
    ///
    /// Survival is the legacy/default mode. Level is the post-pivot discrete-level
    /// mode introduced in the 2026-04-20 migration (Candy Crush-style).
    /// Phase 2 only: debug menu flips CurrentMode before TransitionTo(Playing).
    /// </summary>
    public enum GameMode
    {
        Survival,
        Level
    }

    /// <summary>
    /// Central state machine for the Scrabble-drop game.
    /// Shows/hides UI screens on state transitions.
    /// States: Menu → Playing → GameOver → Playing (restart)
    ///
    /// Job 12 changes:
    ///   - Wires MatchController.OnMatchEnd → TransitionTo(GameOver) in Start()
    ///   - Removed all RoundManager references
    ///   - RoundOver state redirects to GameOver (legacy compatibility)
    ///   - Playing state starts via MatchController.StartMatch() directly
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }

        public GameState CurrentState { get; private set; } = GameState.Menu;

        /// <summary>Current game mode. Default Survival for backward compatibility during migration.</summary>
        public static GameMode CurrentMode { get; set; } = GameMode.Survival;

        /// <summary>Convenience check used by MatchController's solo-mode OR chains.</summary>
        public static bool IsLevelMode => CurrentMode == GameMode.Level;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
//             Debug.Log("[GameManager] Awake — state: Menu");
        }

        private float _gameOverCheckTimer = 0f;

        private void Update()
        {
            // Safety net: if match is over OR all turns used, force GameOver
            // Skip the turn-count check during sudden death — MatchController owns that decision.
            // Skip entirely for Level mode — LevelController + modals own the end flow and
            // stacking Classic GameOverUI on top is a visual regression.
            if (CurrentState == GameState.Playing && MatchController.Instance != null
                && !IsLevelMode)
            {
                bool matchOver = MatchController.Instance.IsGameOver;
                bool isSuddenDeath = MatchController.Instance.IsSuddenDeath;
                // Skip turn-count safety check in blitz — timer handles it
                // Use TotalMaxTurns for correct daily/classic calculation
                bool allTurnsUsed = !BlitzManager.IsBlitzMode && !SurvivalManager.IsSurvivalMode
                    && !isSuddenDeath &&
                    MatchController.Instance.TotalTurnsUsed >= MatchController.Instance.TotalMaxTurns;

                if (matchOver || allTurnsUsed)
                {
                    _gameOverCheckTimer += Time.deltaTime;
                    if (_gameOverCheckTimer > 0.5f)
                    {
                        Debug.LogWarning($"[GameManager] Safety: forcing GameOver. " +
                            $"IsGameOver={matchOver} TotalTurns={MatchController.Instance.TotalTurnsUsed}/{MatchController.Instance.TotalMaxTurns}");
                        if (!matchOver)
                            MatchController.Instance.ForceGameOver();
                        TransitionTo(GameState.GameOver);
                        _gameOverCheckTimer = 0f;
                    }
                }
                else
                {
                    _gameOverCheckTimer = 0f;
                }
            }
        }

        private void Start()
        {
            // Wire MatchController.OnMatchEnd → TransitionTo(GameOver)
            // This must be done in Start() because MatchController.Instance
            // is set in MatchController.Awake(), which runs before GameManager.Start().
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnMatchEnd += OnMatchEnded;
//                 Debug.Log("[GameManager] Subscribed to MatchController.OnMatchEnd");
            }
            else
            {
                Debug.LogWarning("[GameManager] MatchController.Instance is null in Start() — " +
                                 "OnMatchEnd will not be wired. Check creation order in SceneBootstrap.");
            }

            // CurrentState initialises to Menu but TransitionTo never fires for
            // it — so OnStateEntered(Menu) doesn't run on cold boot. Kick the
            // menu BGM directly here so the first launch has music. Subsequent
            // transitions back to Menu run the full OnStateEntered path.
            GameAudio.Instance?.PlayMenuMusic();
        }

        private void OnDestroy()
        {
            if (MatchController.Instance != null)
                MatchController.Instance.OnMatchEnd -= OnMatchEnded;
        }

        private void OnMatchEnded(MatchEndEvent evt)
        {
            if (CurrentState == GameState.GameOver) return;

//             Debug.Log($"[GameManager] OnMatchEnded received: winner={evt.WinnerPlayerIndex} " +
                      // $"P={evt.PlayerScore} AI={evt.AIScore} turns={evt.TotalTurnsEach} each");

            // Level mode owns its own end-of-match flow via LevelController events
            // + the LevelCompletedModal / OutOfMovesModal. Skip the Classic GameOverUI
            // transition, otherwise both would show stacked.
            if (IsLevelMode) return;

            TransitionTo(GameState.GameOver);
        }

        public void TransitionTo(GameState newState)
        {
            // Allow re-entering Playing to restart the match
            if (CurrentState == newState && newState != GameState.Playing)
            {
//                 Debug.Log($"[GameManager] Already in {newState} — ignoring.");
                return;
            }

            // Block transitions during screen transition
            if (ScreenTransition.Instance != null && ScreenTransition.Instance.IsTransitioning)
                return;

//             Debug.Log($"[GameManager] {CurrentState} → {newState}");

            // Use slide transition for Menu↔Playing
            bool useSlide = ScreenTransition.Instance != null
                && ((CurrentState == GameState.Menu && newState == GameState.Playing)
                 || (CurrentState == GameState.Playing && newState == GameState.Menu));

            if (useSlide)
            {
                bool slideFromRight = (newState == GameState.Playing);
                ScreenTransition.Instance.Play(slideFromRight, () =>
                {
                    // Don't hide menu here — ScreenTransition controls its visibility
                    if (GameOverUI.Instance != null) GameOverUI.Instance.SetVisible(false);
                    CurrentState = newState;
                    OnStateEntered(newState, deferHand: true);
                }, () =>
                {
                    // Transition done — deal the hand
                    if (newState == GameState.Playing)
                    {
                        if (HandManager.Instance != null)
                            HandManager.Instance.InitialiseHand();
                        if (HandManager.Instance != null)
                            HandManager.Instance.SetInteractable(true);
                        if (ColumnArrowManager.Instance != null)
                            ColumnArrowManager.Instance.ShowArrows(true);
                    }
                });
            }
            else
            {
                HideAllScreens();
                CurrentState = newState;
                OnStateEntered(newState, deferHand: false);
            }
        }

        private void HideAllScreens()
        {
            if (MenuUI.Instance         != null) MenuUI.Instance.SetVisible(false);
            if (GameOverUI.Instance     != null) GameOverUI.Instance.SetVisible(false);
            if (LevelSelectScreen.Instance != null) LevelSelectScreen.Instance.SetVisible(false);
        }

        private void OnStateEntered(GameState state, bool deferHand = false)
        {
            switch (state)
            {
                // ── Menu ──────────────────────────────────────────────────────────
                case GameState.Menu:
//                     Debug.Log("[GameManager] Entered Menu");
                    AnalyticsManager.ScreenView("main_menu");
                    // Menu appear SFX removed — whoosh from ScreenTransition is enough

                    // Abort any in-flight Level before resetting mode flags — this
                    // clears level state (IsActive, IsInputLocked, RunToken) AND
                    // force-hides the tutorial overlay so prompts can't leak onto
                    // the main menu. Modal MENU buttons (LevelCompletedModal /
                    // OutOfMovesModal) already call AbortLevel directly, but the
                    // in-game HUD ≡ menu button didn't — this is the single
                    // choke point that covers every Playing→Menu path regardless
                    // of entry point. Idempotent (AbortLevel checks IsActive).
                    if (LevelController.Instance != null)
                        LevelController.Instance.AbortLevel();

                    // Reset all mode flags when returning to menu
                    BlitzManager.Reset();
                    SurvivalManager.Reset();
                    DailyDropManager.IsDailyMode = false;

                    if (MenuUI.Instance != null)
                        MenuUI.Instance.SetVisible(true);

                    // Main-menu BGM. Idempotent if already playing; replaces
                    // any survival music left running after a return-to-menu.
                    GameAudio.Instance?.PlayMenuMusic();
                    break;

                // ── Playing ───────────────────────────────────────────────────────
                case GameState.Playing:
//                     Debug.Log("[GameManager] Entered Playing");
                    AnalyticsManager.ScreenView("playing");

                    // Reset detonation recorder and last word display for new match
                    if (DetonationRecorder.Instance != null)
                        DetonationRecorder.Instance.Reset();
                    if (LastWordDisplay.Instance != null)
                        LastWordDisplay.Instance.Clear();

                    // Start or restart the match via MatchController
                    if (MatchController.Instance != null)
                    {
                        MatchController.Instance.StartMatch();
//                         Debug.Log("[GameManager] MatchController.StartMatch() called");
                    }
                    else if (MatchManager.Instance != null)
                    {
                        MatchManager.Instance.StartMatch();
                        Debug.LogWarning("[GameManager] Using legacy MatchManager.StartMatch()");
                    }
                    else
                    {
                        Debug.LogError("[GameManager] Neither MatchController nor MatchManager found!");
                        AnalyticsManager.GameStart();
                    }

                    // Check for first-time tutorial
                    if (TutorialManager.ShouldRunTutorial() && TutorialManager.Instance != null)
                    {
//                         Debug.Log("[GameManager] First game — starting tutorial.");
                        TutorialManager.Instance.BeginTutorial();
                        break; // Tutorial manages hand/input itself
                    }

                    // Normal flow: init hand + enable input (unless deferred for transition)
                    if (!deferHand)
                    {
                        if (HandManager.Instance != null)
                            HandManager.Instance.InitialiseHand();

                        if (HandManager.Instance != null)
                            HandManager.Instance.SetInteractable(true);

                        if (ColumnArrowManager.Instance != null)
                            ColumnArrowManager.Instance.ShowArrows(true);
                    }

                    break;

                // ── Round Over (legacy redirect) ─────────────────────────────────
                case GameState.RoundOver:
                    // Round-based flow removed in Job 12. Redirect to GameOver.
                    Debug.LogWarning("[GameManager] RoundOver state is deprecated — " +
                                     "redirecting to GameOver.");
                    TransitionTo(GameState.GameOver);
                    break;

                // ── Game Over ─────────────────────────────────────────────────────
                case GameState.GameOver:
//                     Debug.Log("[GameManager] Entered GameOver");

                    // Stop blitz timer
                    if (BlitzManager.Instance != null)
                        BlitzManager.Instance.StopTimer();

                    int playerScore = ScoreManager.Instance != null
                        ? ScoreManager.Instance.PlayerScore : 0;
                    int aiScore = ScoreManager.Instance != null
                        ? ScoreManager.Instance.AIScore : 0;

                    AnalyticsManager.GameOver(playerScore, cause: "match_end");
                    AnalyticsManager.ScreenView("game_over");

                    // Disable player input
                    if (HandManager.Instance != null)
                        HandManager.Instance.SetInteractable(false);

                    // Hide column arrows
                    if (ColumnArrowManager.Instance != null)
                        ColumnArrowManager.Instance.ShowArrows(false);

                    // Show game over screen
                    if (GameOverUI.Instance != null)
                        GameOverUI.Instance.Show();
                    else
                        Debug.LogError("[GameManager] GameOverUI.Instance is null in GameOver state!");

                    break;
            }
        }

        // ── Public helpers for UI buttons ─────────────────────────────────────────

        /// <summary>
        /// Restarts the entire game from the GameOver screen.
        /// Called by the "PLAY AGAIN" button in GameOverUI.
        /// </summary>
        public void RequestRestartGame()
        {
            if (CurrentState != GameState.GameOver)
            {
//                 Debug.Log("[GameManager] RequestRestartGame ignored — not in GameOver state.");
                return;
            }

            AnalyticsManager.ButtonTap("play_again");
            AnalyticsManager.EngagementEvent("play_again");

            TransitionTo(GameState.Playing);
        }

        /// <summary>
        /// Resets the current match in-place without leaving the Playing state.
        /// Called by the reset (↺) button in HUDManager.
        /// </summary>
        public void RequestReset()
        {
            if (CurrentState != GameState.Playing)
            {
//                 Debug.Log("[GameManager] RequestReset ignored — not in Playing state.");
                return;
            }

//             Debug.Log("[GameManager] RequestReset — resetting match in-place.");

            // Reset via MatchController (full board + score + hand clear)
            if (MatchController.Instance != null)
            {
                MatchController.Instance.StartMatch();
            }
            else
            {
                // Legacy fallback
                if (GridManager.Instance != null)
                    GridManager.Instance.ClearAllCells();
                if (ScoreManager.Instance != null)
                    ScoreManager.Instance.ResetScore();
                if (MatchManager.Instance != null)
                    MatchManager.Instance.StartMatch();
            }

            // Refresh hand display
            if (HandManager.Instance != null)
                HandManager.Instance.InitialiseHand();

            // Sync HUD
            if (HUDManager.Instance != null)
            {
                HUDManager.Instance.SetPlayerScore(0);
                HUDManager.Instance.SetAIScore(0);
            }

            // Re-enable input
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);

            AnalyticsManager.ButtonTap("reset");
            AnalyticsManager.EngagementEvent("mid_game_reset");

//             Debug.Log("[GameManager] RequestReset complete.");
        }
    }
}
