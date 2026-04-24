using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Manages the player's hand of 5 letter cards (display + input).
    ///
    /// New system (Job 8):
    ///   - Works with PlayerHand + MatchController instead of old TileBag flow
    ///
    /// Job 3 changes:
    ///   - FullTurnSequence: after safety timeout fires, calls GameVisualBridge.ForceReset()
    ///     to guarantee _isPlayingBack is false before proceeding to AI turn check.
    ///
    /// Job 4 changes:
    ///   - Added explicit logging of CurrentPlayer, IsMatchActive, and aiShouldAct
    ///   - Log player index before and after the wait loop to verify player switched
    ///   - Added fallback: if aiShouldAct is false but both players have turns remaining,
    ///     force-trigger the AI turn anyway
    /// </summary>
    public class HandManager : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        private static int  HAND_SIZE            => PlayerHand.HAND_SIZE;
        private const float CARD_SIZE_FRACTION  = 0.85f;

        // Card visual colors
        private static readonly Color CARD_FILL_NORMAL    = new Color(0.973f, 0.961f, 0.937f, 1f);    // warm cream #F8F5EF
        private static readonly Color CARD_BORDER_NORMAL  = new Color(0.800f, 0.745f, 0.640f, 1f);  // desaturated warm tan
        private static readonly Color CARD_BORDER_SELECT  = new Color(0.200f, 0.851f, 0.424f, 1f);  // player green #33D96C
        private static readonly Color CARD_TEXT_COLOR     = new Color(0.145f, 0.153f, 0.200f, 1f);   // text dark #252733
        private static readonly Color CARD_PTS_COLOR      = new Color(0.35f, 0.35f, 0.40f, 1f);
        private static readonly Color CARD_BORDER_SWAP    = new Color(0.85f, 0.60f, 0.10f, 1f);
        private static readonly Color CARD_BORDER_SWAP_SEL= new Color(1.00f, 0.80f, 0.20f, 1f);
        private static readonly Color CARD_TEXT_SWAP      = new Color(0.75f, 0.45f, 0.05f, 1f);

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static HandManager Instance { get; private set; }
        public GameObject[] GetCardObjects() => _cardObjects;

        // ── Runtime state ─────────────────────────────────────────────────────────

        private char[]           _hand          = new char[PlayerHand.MAX_HAND_SIZE];
        private int              _selectedIndex = -1;
        private bool             _swapModeActive = false;

        private GameObject[]     _cardObjects   = new GameObject[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[] _cardSRs       = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];
        private TMPro.TextMeshPro[] _cardTexts    = new TMPro.TextMeshPro[PlayerHand.MAX_HAND_SIZE];
        private TMPro.TextMeshPro[] _cardPtsTexts = new TMPro.TextMeshPro[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[]   _cardShadows  = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];

        private Sprite           _spriteNormal;
        private Sprite           _spriteSelected;
        private Sprite           _spriteSwap;
        private Sprite           _spriteSwapSelected;
        // Wild halo — multicolor glow sprite placed behind the wild card so the
        // slot reads as "special" at a glance. One child GO per card, toggled
        // on/off via RefreshCardVisual based on IsWildSlot.
        private Sprite           _spriteWildHalo;
        private Material         _wildHaloMaterial;
        private GameObject[]     _cardHalos = new GameObject[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[] _cardHaloSRs = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];

        private Camera           _cam;
        private GridManager      _grid;
        private float            _cardSize;

        // ── Next tile preview ────────────────────────────────────────────────────
        private int              _lastCarryCol = -1; // track column for tick sound during carry
        private GameObject       _nextTilePreview;
        private SpriteRenderer   _nextTileSR;
        private TMPro.TextMeshPro _nextTileLetter;
        private TMPro.TextMeshPro _nextTileLabel;
        private GameObject       _nextTileSocket;
        private GameObject       _controlTray;

        public bool IsInteractable { get; set; } = false;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _intentSlopPx = Mathf.Max(14f, Screen.dpi * 0.08f);

            _cam  = Camera.main;
            _grid = GridManager.Instance;

            if (_grid == null)
            {
                Debug.LogError("[HandManager] GridManager not found in Awake!");
                return;
            }

            BuildCardSprites();
            BuildControlTray();
            BuildCardObjects();

            // Initialize hand to empty display
            for (int i = 0; i < HAND_SIZE; i++)
                _hand[i] = '\0';

            BuildShuffleButton();
            BuildNextTilePreview();
            BuildTileBagButton();
//             Debug.Log("[HandManager] Awake complete — cards + shuffle + next-tile + tile-bag built, IsInteractable=false.");
        }

        private void Start()
        {
            // Subscribe to MatchController events for hand updates
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnHandRefilled += OnHandRefilled;
//                 Debug.Log("[HandManager] Subscribed to MatchController.OnHandRefilled");
            }
            else
            {
                Debug.LogWarning("[HandManager] MatchController not found in Start — cannot subscribe.");
            }

        }

        private void OnDestroy()
        {
            if (MatchController.Instance != null)
                MatchController.Instance.OnHandRefilled -= OnHandRefilled;
        }

        // ── MatchController event handler ─────────────────────────────────────────

        private void OnHandRefilled(HandRefilledEvent evt)
        {
            // Only update visuals for the human player
            if (evt.PlayerIndex != MatchController.PLAYER_HUMAN) return;
            if (evt.Letters == null) return;

            SetHand(evt.Letters);
//             Debug.Log($"[HandManager] Hand updated from OnHandRefilled: {new string(evt.Letters)}");
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Repositions all hand UI elements (cards, shuffle, next, tile bag) for the current
        /// grid layout. Call after GridManager.RebuildGrid() changes grid dimensions.
        /// </summary>
        public void RebuildHandLayout()
        {
            _cam = Camera.main;
            _grid = GridManager.Instance;
            if (_grid == null) return;

            DestroyHandUI();
            BuildCardSprites();
            BuildControlTray();
            BuildCardObjects();
            BuildShuffleButton();
            BuildNextTilePreview();
            BuildTileBagButton();
            RefreshAllCardVisuals();

//             Debug.Log($"[HandManager] RebuildHandLayout — cardRowY={GetCardRowY():F2} shuffleY={_shuffleButtonY:F2}");
        }

        private void DestroyHandUI()
        {
            CancelInvoke(nameof(ResetShuffleButtonColor));

            if (_shadowAnimCoroutine != null)
            {
                StopCoroutine(_shadowAnimCoroutine);
                _shadowAnimCoroutine = null;
            }

            if (_shuffleFillCoroutine != null)
            {
                StopCoroutine(_shuffleFillCoroutine);
                _shuffleFillCoroutine = null;
            }

            if (_controlTray != null)
            {
                Destroy(_controlTray);
                _controlTray = null;
            }

            if (_shuffleFillSR != null)
            {
                Destroy(_shuffleFillSR.gameObject);
                _shuffleFillSR = null;
            }

            if (_shuffleButton != null)
            {
                Destroy(_shuffleButton);
                _shuffleButton = null;
            }

            if (_nextTileSocket != null)
            {
                Destroy(_nextTileSocket);
                _nextTileSocket = null;
            }

            if (_nextTilePreview != null)
            {
                Destroy(_nextTilePreview);
                _nextTilePreview = null;
            }

            if (_nextTileLabel != null)
            {
                Destroy(_nextTileLabel.gameObject);
                _nextTileLabel = null;
            }

            if (_tileBagButton != null)
            {
                Destroy(_tileBagButton);
                _tileBagButton = null;
            }

            if (_swapLabel != null)
            {
                Destroy(_swapLabel);
                _swapLabel = null;
            }

            DismissSwapTilePopup();

            for (int i = 0; i < _cardObjects.Length; i++)
            {
                if (_cardObjects[i] != null)
                    Destroy(_cardObjects[i]);

                if (_cardShadows[i] != null)
                    Destroy(_cardShadows[i].gameObject);

                _cardObjects[i] = null;
                _cardSRs[i] = null;
                _cardTexts[i] = null;
                _cardPtsTexts[i] = null;
                _cardShadows[i] = null;
            }

            _nextTileSR = null;
            _nextTileLetter = null;
            _shuffleButtonY = 0f;
            _shuffleButtonX = 0f;
            _shuffleButtonSize = 0f;
            _tileBagX = 0f;
            _tileBagY = 0f;
            _tileBagSize = 0f;
            _shuffleFilling = false;
        }

        /// <summary>
        /// Updates the hand display from current MatchController PlayerHand data.
        /// Called at match start and after each turn via InitialiseHand().
        /// </summary>
        public void InitialiseHand()
        {
            if (_grid == null) _grid = GridManager.Instance;

            if (_spriteNormal == null)
            {
                _cam = Camera.main;
                BuildCardSprites();
                BuildCardObjects();
            }

            // Pull current hand from MatchController
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                {
                    SetHand(hand.GetAllSlots());
                }
                else
                {
                    // Fallback: blank hand
                    char[] blank = new char[HAND_SIZE];
                    for (int i = 0; i < HAND_SIZE; i++) blank[i] = '?';
                    SetHand(blank);
                }
            }

            _selectedIndex  = -1; // Nothing selected at start
            _swapModeActive = false;
            _inputMode = InputMode.Idle;
            _touchCardIndex = -1;
            _dragIndex = -1;
            _isDragging = false;

            RefreshAllCardVisuals();

            // Hide all cards before deal animation starts (prevents flash of cards in final position)
            for (int i = 0; i < HAND_SIZE; i++)
                if (_cardObjects[i] != null) _cardObjects[i].SetActive(false);

            // Deal cards in one at a time (Balatro style)
            StartCoroutine(DealCardsAnimation(() =>
            {
                IsInteractable = true;
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false); // No arrows until card selected
//                 Debug.Log($"[HandManager] InitialiseHand complete — hand: {new string(_hand)}");
            }));
        }

        /// <summary>
        /// Updates all 5 card displays to show the given letters.
        /// Letters array should have length == HAND_SIZE.
        /// </summary>
        public void SetHand(char[] letters)
        {
            if (letters == null)
            {
                Debug.LogWarning("[HandManager] SetHand: letters array is null.");
                return;
            }

            int count = Mathf.Min(letters.Length, HAND_SIZE);
            for (int i = 0; i < count; i++)
                _hand[i] = letters[i];

            // Fill any remaining slots with '\0' if array is short
            for (int i = count; i < HAND_SIZE; i++)
                _hand[i] = '\0';

            RefreshAllCardVisuals();
        }

        /// <summary>
        /// Update a single card slot without rebuilding the entire hand.
        /// Used by the tutorial to avoid the "whole hand changed" visual glitch.
        /// </summary>
        public void UpdateSingleCard(int index, char letter)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            _hand[index] = letter;
            RefreshCardVisual(index);
        }

        /// <summary>
        /// Returns the letter on the currently selected card.
        /// Returns '\0' if no valid selection.
        /// </summary>
        public char GetSelectedLetter()
        {
            if (_selectedIndex < 0 || _selectedIndex >= HAND_SIZE)
                return '\0';
            return _hand[_selectedIndex];
        }

        public int GetSelectedIndex() => _selectedIndex;

        /// <summary>
        /// Enables or disables all card interaction.
        /// Also hides column arrows when disabled.
        /// </summary>
        public void SetInteractable(bool interactable)
        {
            IsInteractable = interactable;

            if (!interactable)
            {
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
            }
        }

        /// <summary>Get world X position of a hand card (for tutorial arrow).</summary>
        public float GetCardWorldX(int index) => GetCardX(index);

        /// <summary>Get world Y position of the hand card row (for tutorial arrow).</summary>
        public float GetCardWorldY() => GetCardRowY();

        /// <summary>Tutorial: force-select a card by index.</summary>
        public void ForceSelectCard(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            _selectedIndex = index;
            RefreshAllCardVisuals();
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);
        }

        /// <summary>
        /// Activates swap mode. In swap mode, tapping a card swaps it
        /// (calls MatchController.UseSwap) instead of selecting it.
        /// </summary>
        public void ShowSwapButton(bool active)
        {
            _swapModeActive = active;
            RefreshAllCardVisuals();
//             Debug.Log($"[HandManager] Swap mode: {active}");
        }

        // ── Input handling ────────────────────────────────────────────────────────

        // ── Drag state machine ──────────────────────────────────────────
        private enum InputMode { Idle, PressedCard, CarryToBoard, Reordering }
        private InputMode _inputMode = InputMode.Idle;
        private int   _touchCardIndex = -1;
        private Vector3 _touchStartWorld;
        private Vector2 _touchStartScreen;
        private float _intentSlopPx = 14f;
        private const float DROP_LOCK_RATIO = 1.1f;
        private const float REORDER_LOCK_RATIO = 1.25f;

        // ── Legacy drag compat ──
        private int  _dragIndex = -1;
        private bool _isDragging = false;
        private float _dragStartX;

        // ── Long-press swap state ──────────────────────────────────────────
        private const float LONG_PRESS_TIME = 0.5f;
        private float _holdTimer = 0f;
        private int   _holdIndex = -1;
        private bool  _holdTriggered = false;
        private int   _swapConfirmIndex = -1; // -1 = no confirmation pending
        private GameObject _swapLabel;        // "SWAP?" floating label
        // _rewriteLabel removed — rewrite is now triggered by board long-press

        // ── Rewrite mode state ──────────────────────────────────────────
        private const float REWRITE_TIMEOUT = 5f; // auto-cancel after 5s to prevent pause abuse
        private bool  _rewriteModeActive = false;
        private int   _rewriteTargetCol = -1;
        private int   _rewriteTargetRow = -1;
        private float _rewriteTimeoutTimer = 0f;

        /// <summary>
        /// Called after a rising row shifts the board up by 1.
        /// Updates the rewrite target so it follows the selected tile.
        /// </summary>
        public void OnBoardShiftedUp()
        {
            if (_rewriteModeActive && _rewriteTargetRow >= 0)
            {
                // Clear highlight on old tile
                Tile oldTile = _grid != null ? _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow) : null;
                if (oldTile != null) { oldTile.Highlight(false); oldTile.ResetVisuals(); }

                _rewriteTargetRow += 1;

                // If shifted off the top, cancel rewrite
                if (_rewriteTargetRow >= RulesEngine.ROWS)
                {
//                     Debug.Log("[HandManager] Rewrite target shifted off board — cancelling");
                    CancelRewriteMode();
                    return;
                }

                // Re-apply highlight to tile at new position
                Tile newTile = _grid != null ? _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow) : null;
                if (newTile != null)
                {
                    newTile.Highlight(true, REWRITE_HIGHLIGHT_COLOR);
                    // Restart pulse on the new tile reference
                    if (_rewritePulseCoroutine != null) StopCoroutine(_rewritePulseCoroutine);
                    _rewritePulseCoroutine = StartCoroutine(RewritePulseCoroutine(newTile));
                }
                else
                {
                    // Tile disappeared during shift — cancel
                    CancelRewriteMode();
                    return;
                }

//                 Debug.Log($"[HandManager] Rewrite target shifted up → ({_rewriteTargetCol},{_rewriteTargetRow})");
            }

            // Also update board hold tracking if player is mid-long-press
            if (_boardHoldCell.x >= 0 && _boardHoldCell.y >= 0)
            {
                _boardHoldCell = new Vector2Int(_boardHoldCell.x, _boardHoldCell.y + 1);
                if (_boardHoldCell.y >= RulesEngine.ROWS)
                {
                    _boardHoldCell = new Vector2Int(-1, -1);
                    _boardHoldTimer = 0f;
                    _boardHoldTriggered = false;
                }
            }
        }
        public  bool IsRewriteModeActive => _rewriteModeActive;
        private int  _rewriteMatchRewriteCount = 0; // debug: total rewrites this match
        private static readonly Color REWRITE_HIGHLIGHT_COLOR = new Color(0.6f, 1.9f, 1.8f, 1f); // HDR teal — blooms on pulse

        // ── Wild Tiles Phase C — per-resolution injection cap ───────────────────────
        // Multiple injection triggers (wild-refill tile detonation + chain depth reward)
        // can fire during one resolution pass. Only the first one lands.
        private bool _wildInjectedThisResolution = false;

        // Wall-clock time (unscaled) the wild BECAME VISIBLE in hand. Anchored by
        // TickWildExpiry on the first frame HasWild is true (NOT when queued via
        // TryInjectWildReward — the chain resolution + popup + refill delay can
        // eat several seconds before the wild actually appears in a slot, which
        // used to expire the wild almost immediately after it showed up).
        private float _wildInjectedAt = -1f;
        private const float WILD_EXPIRY_SECONDS = 20f; // generous — player needs time to plan a wild drop
        private const int   WILD_EXPIRY_DROPS   = 3;
        private bool  _wildExpiryPaused = false;
        private float _wildExpiryPauseStarted = -1f;

        // ── Board long-press tracking for rewrite ──
        private Vector2Int _boardHoldCell = new Vector2Int(-1, -1);
        private float _boardHoldTimer = 0f;
        private bool  _boardHoldTriggered = false;
        private Coroutine _rewritePulseCoroutine;

        // ── Swap Tile confirmation popup ──
        private bool _swapTileConfirmActive = false;
        private GameObject _swapTilePopup;
        private GameObject _swapTileYesLabel;
        private GameObject _swapTileNoLabel;

        // (bag button triggers hand swap directly, board rewrite is long-press on board)

        private void Update()
        {
            if (_grid == null) return;

            // Phase C: wild expiry. Fires on 3-drop count OR 20s playable-time elapsed,
            // whichever comes first. Drop count is tracked in PlayerHand.DrawSlot.
            TickWildExpiry();

            Vector3 screenPos = Vector3.zero;
            bool mouseDown = Input.GetMouseButtonDown(0);
            bool mouseHeld = Input.GetMouseButton(0);
            bool mouseUp   = Input.GetMouseButtonUp(0);

            if (Input.touchCount > 0)
            {
                Touch touch = Input.GetTouch(0);
                screenPos = touch.position;
                mouseDown = (touch.phase == TouchPhase.Began);
                mouseHeld = (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary);
                mouseUp   = (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled);
            }
            else
            {
                screenPos = Input.mousePosition;
            }

            screenPos.z = Mathf.Abs(_cam.transform.position.z);
            Vector3 worldPos = _cam.ScreenToWorldPoint(screenPos);

            UpdateSelectedCardShadow();

            // Block ALL input when not interactable or during processing (rising rows, chain resolution).
            // Also block when LevelController has locked input (Level Complete / Out of Moves modal
            // is up) — otherwise the FullTurnSequence re-enable at the end of the drop resolution
            // coroutine can unlock input AFTER FireComplete/FireFail fired, letting SHUFFLE/drag
            // still work while a terminal modal is on screen.
            bool levelLocked = LevelController.Instance != null && LevelController.Instance.IsInputLocked;
            if (!IsInteractable ||
                levelLocked ||
                (MatchController.Instance != null && MatchController.Instance.IsProcessing))
            {
                if (DropPreview.Instance != null)
                    DropPreview.Instance.ClearPreview();
                if (_inputMode != InputMode.Idle)
                    CancelCurrentGesture();
                return;
            }

            // Board long-press for rewrite (runs in Idle only, blocked during rising row)
            bool risingRowActive = SurvivalManager.Instance != null && SurvivalManager.Instance.IsRisingRow;
            if (_boardHoldCell.x >= 0 && mouseHeld && !_boardHoldTriggered && !_rewriteModeActive
                && _inputMode == InputMode.Idle && !risingRowActive)
            {
                _boardHoldTimer += Time.deltaTime;
                if (_boardHoldTimer >= LONG_PRESS_TIME)
                {
                    _boardHoldTriggered = true;
                    TryEnterRewriteMode(_boardHoldCell.x, _boardHoldCell.y);
                }
            }

            // Rewrite mode timeout — auto-cancel to prevent indefinite pause
            if (_rewriteModeActive)
            {
                _rewriteTimeoutTimer -= Time.deltaTime;
                if (_rewriteTimeoutTimer <= 0f)
                {
                    CancelRewriteMode();
                }
            }

            // ── STATE MACHINE ──────────────────────────────────────────

            switch (_inputMode)
            {
                // ── IDLE ────────────────────────────────────────────────
                case InputMode.Idle:
                {
                    if (mouseUp)
                    {
                        _boardHoldCell = new Vector2Int(-1, -1);
                        _boardHoldTimer = 0f;
                        _boardHoldTriggered = false;
                    }

                    if (!mouseDown) break;

                    // Modal states first
                    if (_rewriteModeActive)
                    {
                        int rewriteTapped = GetCardIndexAtPosition(worldPos);
                        if (rewriteTapped >= 0)
                        {
                            // Tap hand card → Replace (existing rewrite)
                            TryExecuteRewrite(_rewriteTargetCol, _rewriteTargetRow, rewriteTapped);
                        }
                        else
                        {
                            // Check if tapped any board tile → Board Swap (any two regular tiles)
                            Vector2Int boardTap = _grid.WorldToCell(worldPos);
                            if (boardTap.x >= 0 && boardTap.y >= 0
                                && !(boardTap.x == _rewriteTargetCol && boardTap.y == _rewriteTargetRow)
                                && TryBoardSwap(_rewriteTargetCol, _rewriteTargetRow, boardTap.x, boardTap.y))
                            {
                                // Swap succeeded — rewrite mode exits inside TryBoardSwap
                            }
                            else
                            {
                                CancelRewriteMode();
                            }
                        }
                        return;
                    }

                    if (_swapConfirmIndex >= 0)
                    {
                        int tappedForSwap = GetCardIndexAtPosition(worldPos);
                        if (tappedForSwap == _swapConfirmIndex)
                            ExecuteSwap(_swapConfirmIndex);
                        CancelSwapConfirmation();
                        return;
                    }

                    if (_swapTileConfirmActive)
                    {
                        if (_swapTileYesLabel != null && IsWorldPosNearObject(_swapTileYesLabel, worldPos, _cardSize * 0.5f))
                        {
                            int savedCard = _selectedIndex;
                            DismissSwapTilePopup();
                            if (savedCard >= 0) { ExecuteSwap(savedCard); _selectedIndex = -1; }
                            return;
                        }
                        DismissSwapTilePopup();
                        return;
                    }

                    // Action row buttons
                    if (TryHandleShuffleButton(worldPos)) return;
                    if (_selectedIndex >= 0 && TryHandleTileBagButton(worldPos))
                    {
                        ShowSwapTilePopup();
                        return;
                    }

                    // Card touch → begin gesture
                    int tappedCard = GetCardIndexAtPosition(worldPos);
                    if (tappedCard >= 0)
                    {
                        _inputMode = InputMode.PressedCard;
                        _touchCardIndex = tappedCard;
                        _touchStartWorld = worldPos;
                        _touchStartScreen = screenPos;
                        _dragStartX = worldPos.x;
                        return;
                    }

                    // Tapping the board with a selected card no longer drops — drag only
                    // Clear selection if player taps the board
                    if (_selectedIndex >= 0 && worldPos.y >= _grid.GridBottom - _grid.CellSize * 0.5f)
                        _selectedIndex = -1;

                    // Start rewrite hold tracking
                    Vector2Int boardCell = _grid.WorldToCell(worldPos);
                    if (boardCell.x >= 0 && boardCell.y >= 0)
                    {
                        _boardHoldCell = boardCell;
                        _boardHoldTimer = 0f;
                        _boardHoldTriggered = false;
                    }

                    break;
                }

                // ── PRESSED CARD (deciding intent) ──────────────────────
                case InputMode.PressedCard:
                {
                    if (mouseUp)
                    {
                        // Quick tap — ignore. Drag-only mode.
                        // No tap-to-select: cards must be dragged to the board.
                        _inputMode = InputMode.Idle;
                        _touchCardIndex = -1;
                        _selectedIndex = -1;
                        return;
                    }

                    if (mouseHeld)
                    {
                        float dx = Mathf.Abs(screenPos.x - _touchStartScreen.x);
                        float dy = Mathf.Abs(screenPos.y - _touchStartScreen.y);
                        float totalMove = Mathf.Max(dx, dy);

                        if (totalMove > _intentSlopPx)
                        {
                            if (dy > dx * DROP_LOCK_RATIO)
                            {
                                // Lock into CARRY TO BOARD
                                _inputMode = InputMode.CarryToBoard;
                                _selectedIndex = _touchCardIndex;
                                _lastCarryCol = -1; // reset so first column gets a tick
                                RestoreAllCardSortOrder();
                                BoostCardSortOrder(_touchCardIndex);
                                GameAudio.Instance?.PlayTileSelect();

                                // Hide ALL shadows first, then show only the dragged card's
                                for (int s = 0; s < HAND_SIZE; s++)
                                    if (_cardShadows[s] != null)
                                        _cardShadows[s].color = Color.clear;

                                if (_cardObjects[_touchCardIndex] != null)
                                {
                                    _cardObjects[_touchCardIndex].transform.position = new Vector3(
                                        worldPos.x, worldPos.y, -3f);
                                    _cardObjects[_touchCardIndex].transform.localScale = GetCardBaseScale() * 1.1f;

                                    // Move shadow to finger immediately
                                    if (_touchCardIndex < HAND_SIZE && _cardShadows[_touchCardIndex] != null)
                                    {
                                        _cardShadows[_touchCardIndex].transform.position = new Vector3(
                                            worldPos.x, worldPos.y - _cardSize * 0.06f, 0f);
                                        _cardShadows[_touchCardIndex].color = new Color(0f, 0f, 0f, 0.3f);
                                        _cardShadows[_touchCardIndex].transform.localScale = GetCardBaseScale() * 1.06f;
                                    }

                                    // Swap to selected sprite while dragging
                                    SpriteRenderer dragSR = _cardObjects[_touchCardIndex].GetComponent<SpriteRenderer>();
                                    if (dragSR != null && _spriteSelected != null)
                                        dragSR.sprite = _spriteSelected;
                                }

//                                 Debug.Log($"[Input] CarryToBoard: card={_touchCardIndex} letter={_hand[_touchCardIndex]}");
                            }
                            else if (dx > dy * REORDER_LOCK_RATIO
                                     && !TutorialManager.BlockShuffleAndSwap)
                            {
                                // Lock into REORDERING (blocked during tutorial)
                                _inputMode = InputMode.Reordering;
                                _dragIndex = _touchCardIndex;
                                _isDragging = true;
                                RestoreAllCardSortOrder();
                                BoostCardSortOrder(_touchCardIndex);
                                GameAudio.Instance?.PlayButtonClick();
                                _dragStartX = worldPos.x;

//                                 Debug.Log($"[Input] Reordering: card={_touchCardIndex}");
                            }
                        }
                    }
                    break;
                }

                // ── CARRY TO BOARD (drag tile toward board, preview active) ──
                case InputMode.CarryToBoard:
                {
                    if (mouseHeld)
                    {
                        // Move card to follow finger
                        if (_touchCardIndex >= 0 && _cardObjects[_touchCardIndex] != null)
                        {
                            _cardObjects[_touchCardIndex].transform.position = new Vector3(
                                worldPos.x, worldPos.y, -3f);

                            // Shadow follows underneath — offset based on distance from screen center
                            // simulating a light source above center of the board
                            if (_touchCardIndex < HAND_SIZE && _cardShadows[_touchCardIndex] != null)
                            {
                                float centerX = 0f;
                                float maxHOffset = _cardSize * 0.15f;
                                float hOffset = -Mathf.Sign(worldPos.x - centerX)
                                    * Mathf.Clamp01(Mathf.Abs(worldPos.x - centerX) / 3f) * maxHOffset;

                                // Y offset grows as card lifts higher from rest — simulates lifting off surface
                                float restY = GetCardRowY();
                                float liftDist = Mathf.Max(0f, worldPos.y - restY);
                                float baseShadowDrop = _cardSize * 0.06f;
                                float maxExtraDrop = _cardSize * 0.15f;
                                float shadowDrop = baseShadowDrop + Mathf.Clamp01(liftDist / (_cardSize * 3f)) * maxExtraDrop;

                                _cardShadows[_touchCardIndex].transform.position = new Vector3(
                                    worldPos.x + hOffset, worldPos.y - shadowDrop, 0f);
                                _cardShadows[_touchCardIndex].color = new Color(0f, 0f, 0f, 0.3f); // see-through
                                _cardShadows[_touchCardIndex].transform.localScale = GetCardBaseScale() * 1.06f; // tighter spread
                            }
                        }

                        // Reorder hand while carrying — if near the hand row, swap with neighbors
                        float cardRowY = GetCardRowY();
                        float reorderZone = _cardSize * 2.0f; // within 2 card-heights of hand
                        if (worldPos.y < cardRowY + reorderZone && _touchCardIndex >= 0
                            && !TutorialManager.BlockShuffleAndSwap)
                        {
                            float cardSpacing = (HAND_SIZE > 1) ? GetCardX(1) - GetCardX(0) : _cardSize * 1.2f;
                            for (int i = 0; i < HAND_SIZE; i++)
                            {
                                if (i == _touchCardIndex) continue;
                                float targetX = GetCardX(i);
                                if (Mathf.Abs(worldPos.x - targetX) < cardSpacing * 0.4f)
                                {
                                    GameAudio.Instance?.PlayReorderTick();
                                    SwapCardPositions(_touchCardIndex, i);
                                    _touchCardIndex = i;
                                    _selectedIndex = i;
                                    UpdateCardPositionsExcept(_touchCardIndex);
                                    break;
                                }
                            }
                        }

                        // Update preview based on column under finger
                        char letter = (_touchCardIndex >= 0 && _touchCardIndex < HAND_SIZE)
                            ? _hand[_touchCardIndex] : '\0';
                        int col = _grid.WorldXToColumn(worldPos.x);

                        if (letter != '\0' && col >= 0 && worldPos.y >= _grid.GridBottom - _grid.CellSize)
                        {
                            if (col != _lastCarryCol)
                            {
                                _lastCarryCol = col;
                            }
                            if (DropPreview.Instance != null)
                                DropPreview.Instance.UpdatePreview(letter, col);

                            if (ColumnArrowManager.Instance != null)
                                ColumnArrowManager.Instance.ShowArrows(true);
                        }
                        else
                        {
                            _lastCarryCol = -1;
                            if (DropPreview.Instance != null)
                                DropPreview.Instance.ClearPreview();
                        }
                    }

                    if (mouseUp)
                    {
                        if (DropPreview.Instance != null)
                            DropPreview.Instance.ClearPreview();
                        if (ColumnArrowManager.Instance != null)
                            ColumnArrowManager.Instance.ShowArrows(false);

                        // Check if releasing over a valid column
                        int dropCol = _grid.WorldXToColumn(worldPos.x);
                        bool overBoard = worldPos.y >= _grid.GridBottom - _grid.CellSize * 0.5f;

                        // Tutorial restrictions on drag-to-drop
                        bool tutColOk  = TutorialManager.AllowedColumn < 0    || dropCol == TutorialManager.AllowedColumn;
                        bool tutCardOk = TutorialManager.AllowedCardIndex < 0 || _touchCardIndex == TutorialManager.AllowedCardIndex;

                        // Check if released over the tile bag → swap
                        bool overBag = TryHandleTileBagButton(worldPos);
                        if (overBag && _touchCardIndex >= 0 && !TutorialManager.BlockShuffleAndSwap)
                        {
                            // Dissolve the card into the bag, then deal replacement
                            StartCoroutine(SwapViaBagDrop(_touchCardIndex));
                        }
                        else if (dropCol >= 0 && overBoard && _touchCardIndex >= 0
                            && _grid.IsColumnAvailable(dropCol)
                            && tutColOk && tutCardOk
                            && MatchController.Instance != null
                            && MatchController.Instance.IsMatchActive
                            && MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN
                            && !MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN))
                        {
                            _selectedIndex = _touchCardIndex;

                            // Reset card scale and sprite before drop
                            if (_cardObjects[_touchCardIndex] != null)
                            {
                                _cardObjects[_touchCardIndex].transform.localScale = GetCardBaseScale();
                                SpriteRenderer csr = _cardObjects[_touchCardIndex].GetComponent<SpriteRenderer>();
                                if (csr != null) csr.sprite = _spriteNormal;
                            }

                            // Hide tutorial arrows on successful drop
                            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
                                TutorialManager.Instance.HideArrowsOnDrop();

                            DropSelectedLetterInColumn(dropCol);
                        }
                        else
                        {
                            SnapCardBack(_touchCardIndex);
                        }

                        _inputMode = InputMode.Idle;
                        _touchCardIndex = -1;
                    }
                    break;
                }

                // ── REORDERING (horizontal drag within hand) ──────────
                case InputMode.Reordering:
                {
                    if (mouseHeld)
                    {
                        // If player pulls card upward past threshold, switch to CarryToBoard
                        float cardRowY = GetCardRowY();
                        float liftThreshold = _cardSize * 1.2f;
                        if (worldPos.y > cardRowY + liftThreshold)
                        {
                            // Transition: end reorder, start carry
                            int cardIdx = _dragIndex;
                            HandleDragEnd();
                            _isDragging = false;
                            _dragIndex = -1;

                            _inputMode = InputMode.CarryToBoard;
                            _touchCardIndex = cardIdx;
                            _selectedIndex = cardIdx;
                            _lastCarryCol = -1;

                            if (_cardObjects[cardIdx] != null)
                            {
                                _cardObjects[cardIdx].transform.position = new Vector3(
                                    worldPos.x, worldPos.y, -3f);
                                _cardObjects[cardIdx].transform.localScale = GetCardBaseScale() * 1.1f;

                                SpriteRenderer dragSR = _cardObjects[cardIdx].GetComponent<SpriteRenderer>();
                                if (dragSR != null && _spriteSelected != null)
                                    dragSR.sprite = _spriteSelected;
                            }

//                             Debug.Log($"[Input] Reordering → CarryToBoard: card={cardIdx}");
                            break;
                        }

                        HandleDragMove(worldPos);
                    }

                    if (mouseUp)
                    {
                        HandleDragEnd();
                        _inputMode = InputMode.Idle;
                        _touchCardIndex = -1;
                        _dragIndex = -1;
                        _isDragging = false;
                    }
                    break;
                }
            }
        }

        /// <summary>Cancel any in-progress gesture and reset to Idle.</summary>
        private void CancelCurrentGesture()
        {
            if (_inputMode == InputMode.CarryToBoard)
            {
                if (DropPreview.Instance != null)
                    DropPreview.Instance.ClearPreview();
                SnapCardBack(_touchCardIndex);
                _selectedIndex = -1; // Clear selection so tapping the board doesn't drop this card
            }
            else if (_inputMode == InputMode.Reordering)
            {
                HandleDragEnd();
            }

            _inputMode = InputMode.Idle;
            _touchCardIndex = -1;
            _dragIndex = -1;
            _isDragging = false;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);
        }

        /// <summary>Snap a card back to its resting position after a cancelled carry.</summary>
        private void SnapCardBack(int index)
        {
            if (index < 0 || index >= HAND_SIZE || _cardObjects[index] == null) return;
            RestoreAllCardSortOrder();
            float baseY = GetCardRowY();
            _cardObjects[index].transform.position = new Vector3(GetCardX(index), baseY, -1f);
            _cardObjects[index].transform.localScale = GetCardBaseScale();
            // Reset sprite back to normal
            SpriteRenderer csr = _cardObjects[index].GetComponent<SpriteRenderer>();
            if (csr != null) csr.sprite = _spriteNormal;
            // Reset shadow to rest state
            if (index < HAND_SIZE && _cardShadows[index] != null)
            {
                _cardShadows[index].color = new Color(0f, 0f, 0f, 0.15f);
                _cardShadows[index].transform.position = new Vector3(
                    GetCardX(index), baseY - _cardSize * 0.03f, 0f);
            }
        }

        private int GetCardIndexAtPosition(Vector3 worldPos)
        {
            float halfCard = _cardSize * 0.5f;
            float cardY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float cardX = _cardObjects[i].transform.position.x;
                float cardActualY = _cardObjects[i].transform.position.y;

                bool inX = worldPos.x >= cardX - halfCard && worldPos.x <= cardX + halfCard;
                bool inY = worldPos.y >= cardActualY - halfCard && worldPos.y <= cardActualY + halfCard;

                if (inX && inY) return i;
            }
            return -1;
        }

        private void HandleDragMove(Vector3 worldPos)
        {
            if (_dragIndex < 0 || _dragIndex >= HAND_SIZE) return;

            float dragDelta = worldPos.x - _dragStartX;

            // Activate drag if past threshold
            if (!_isDragging && Mathf.Abs(dragDelta) > 0.3f)
                _isDragging = true;

            if (!_isDragging) return;

            // Move the dragged card to follow finger
            float baseY = GetCardRowY();
            if (_cardObjects[_dragIndex] != null)
                _cardObjects[_dragIndex].transform.position = new Vector3(
                    worldPos.x, baseY + CARD_SELECT_RAISE * 0.5f, -2f); // Slightly raised, in front

            // Hide ALL shadows, then show only the dragged card's shadow
            for (int s = 0; s < HAND_SIZE; s++)
                if (_cardShadows[s] != null) _cardShadows[s].color = Color.clear;

            if (_dragIndex >= 0 && _dragIndex < HAND_SIZE && _cardShadows[_dragIndex] != null)
            {
                float centerX = 0f;
                float maxHOffset = _cardSize * 0.1f;
                float hOffset = -Mathf.Sign(worldPos.x - centerX) * Mathf.Clamp01(Mathf.Abs(worldPos.x - centerX) / 3f) * maxHOffset;

                // Y offset grows as card lifts higher — shadow separates from card
                float cardY = baseY + CARD_SELECT_RAISE * 0.5f;
                float liftDist = Mathf.Max(0f, cardY - baseY);
                float baseShadowDrop = _cardSize * 0.03f;
                float maxExtraDrop = _cardSize * 0.15f;
                float shadowDrop = baseShadowDrop + Mathf.Clamp01(liftDist / (_cardSize * 3f)) * maxExtraDrop;

                _cardShadows[_dragIndex].transform.position = new Vector3(worldPos.x + hOffset, cardY - shadowDrop, 0f);
                _cardShadows[_dragIndex].color = new Color(0f, 0f, 0f, 0.3f);
                _cardShadows[_dragIndex].transform.localScale = GetCardBaseScale() * 1.06f;
            }

            // Check if we should swap with a neighbor
            float cardSpacing = (HAND_SIZE > 1) ? GetCardX(1) - GetCardX(0) : _cardSize * 1.2f;

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (i == _dragIndex) continue;
                float targetX = GetCardX(i);

                if (Mathf.Abs(worldPos.x - targetX) < cardSpacing * 0.4f)
                {
                    // Swap dragged card with this position
                    GameAudio.Instance?.PlayReorderTick();
                    SwapCardPositions(_dragIndex, i);
                    _dragIndex = i; // Now tracking the new index
                    _dragStartX = worldPos.x;

                    // Animate the displaced card to its new spot
                    UpdateCardPositionsExcept(_dragIndex);
                    break;
                }
            }
        }

        private void HandleDragEnd()
        {
            bool wasDragging = _isDragging; // did user actually move past drag threshold?
            _isDragging = false;
            _dragIndex = -1;
            RestoreAllCardSortOrder();

            if (wasDragging)
            {
                // Actual drag happened — deselect and snap all cards to rest
                _selectedIndex = -1;
                HideAllCardShadows();
                float baseY = GetCardRowY();
                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    _cardObjects[i].transform.position = new Vector3(GetCardX(i), baseY, -1f);
                    _cardObjects[i].transform.localScale = GetCardBaseScale();
                }
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
            }
            // If not dragging, it was just a tap — keep the selection from SelectCard
        }

        // ── Swap confirmation ────────────────────────────────────────────────

        private void TriggerSwapConfirmation(int cardIndex)
        {
            if (MatchController.Instance == null) return;
            int swapsLeft = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
            if (swapsLeft <= 0)
            {
//                 Debug.Log("[HandManager] No swaps remaining.");
                return;
            }

            _swapConfirmIndex = cardIndex;

            // Raise the tile higher to indicate swap mode
            if (_cardObjects[cardIndex] != null)
            {
                float baseY = GetCardRowY();
                _cardObjects[cardIndex].transform.position = new Vector3(
                    GetCardX(cardIndex), baseY + CARD_SELECT_RAISE * 1.8f, -1f);
            }

            // Show "SWAP?" label above the raised card
            if (_swapLabel != null) Destroy(_swapLabel);
            _swapLabel = CreateConfirmLabel("SWAP?",
                GetCardX(cardIndex),
                GetCardRowY() + CARD_SELECT_RAISE * 1.8f + _cardSize * 0.7f,
                Color.white);

//             Debug.Log($"[HandManager] Swap confirmation triggered for card {cardIndex} ({swapsLeft} swaps left)");
        }

        private GameObject CreateConfirmLabel(string text, float x, float y, Color color)
        {
            GameObject go = new GameObject(text.Replace("?", "Label"));
            go.transform.position = new Vector3(x, y, -2f);

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = 42;
            tm.characterSize = 0.07f;
            tm.fontStyle = FontStyle.Bold;
            tm.color = color;
            GameFont.ApplyUI(tm);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 25;

            return go;
        }

        // IsWorldPosNearLabel removed — no longer needed

        private void CancelSwapConfirmation()
        {
            _swapConfirmIndex = -1;
            if (_swapLabel != null) { Destroy(_swapLabel); _swapLabel = null; }

            // Snap card back down
            float baseY = GetCardRowY();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), targetY, -1f);
            }
        }

        /// <summary>
        /// Drag-to-bag swap: card dissolves with particles, then replacement deals in.
        /// </summary>
        private IEnumerator SwapViaBagDrop(int cardIndex)
        {
            if (TutorialManager.BlockShuffleAndSwap) yield break;
            if (MatchController.Instance == null) yield break;
            if (cardIndex < 0 || cardIndex >= HAND_SIZE) yield break;
            if (_hand[cardIndex] == '\0') yield break;

            // Preflight: check swap is actually possible before animating
            if (!MatchController.Instance.IsMatchActive ||
                MatchController.Instance.IsGameOver ||
                MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN) <= 0)
            {
                SnapCardBack(cardIndex);
                GameAudio.Instance?.PlayButtonClick();
                yield break;
            }

            IsInteractable = false;

            GameObject cardGO = _cardObjects[cardIndex];
            if (cardGO == null) { IsInteractable = true; yield break; }

            // Move card to bag position with a quick tween
            Vector3 bagPos = new Vector3(_tileBagX, _tileBagY, -2f);
            cardGO.transform.DOMove(bagPos, 0.12f).SetEase(DG.Tweening.Ease.InQuad);
            yield return WaitCache.Get(0.10f);

            // Dissolve with particles
            GameParticles.Instance?.PlayDetonation(bagPos, 0);
            GameAudio.Instance?.PlayPoofExplosion();
            HapticsManager.Light();

            // Shrink + fade
            cardGO.transform.DOScale(Vector3.zero, 0.15f).SetEase(DG.Tweening.Ease.InBack);
            SpriteRenderer cardSR = cardGO.GetComponent<SpriteRenderer>();
            if (cardSR != null)
            {
                Color startCol = cardSR.color;
                float fadeElapsed = 0f;
                while (fadeElapsed < 0.15f)
                {
                    fadeElapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(fadeElapsed / 0.15f);
                    cardSR.color = new Color(startCol.r, startCol.g, startCol.b, 1f - t);
                    yield return null;
                }
            }
            else
            {
                yield return WaitCache.Get(0.15f);
            }

            // Execute the actual swap in data
            bool success = MatchController.Instance.UseSwap(cardIndex);
            if (success)
            {
//                 Debug.Log($"[HandManager] SwapViaBagDrop: card {cardIndex} swapped");
                RestoreAllCardSortOrder();
                RefreshHandFromMatchController();

                // Reset card visual for the new letter
                if (_cardObjects[cardIndex] != null)
                {
                    _cardObjects[cardIndex].transform.localScale = Vector3.zero;
                    if (cardSR != null) cardSR.color = Color.white;

                    // Deal animation: pop in from zero
                    Vector3 restPos = new Vector3(GetCardX(cardIndex), GetCardRowY(), -1f);
                    _cardObjects[cardIndex].transform.position = restPos;
                    _cardObjects[cardIndex].transform.DOScale(GetCardBaseScale(), 0.2f)
                        .SetEase(DG.Tweening.Ease.OutBack, 2f);
                    GameAudio.Instance?.PlayCardDeal();
                }

                RefreshAllCardVisuals();
            }
            else
            {
                // Swap failed — restore card fully
                if (cardSR != null) cardSR.color = Color.white;
                if (_cardObjects[cardIndex] != null)
                    _cardObjects[cardIndex].transform.localScale = GetCardBaseScale();
                SnapCardBack(cardIndex);
            }

            yield return WaitCache.Get(0.1f);
            IsInteractable = true;
        }

        private void ExecuteSwap(int cardIndex)
        {
            if (TutorialManager.BlockShuffleAndSwap) return;

            // One-time contextual hint for first swap
            if (PlayerPrefs.GetInt("hint_swap", 0) == 0)
            {
                PlayerPrefs.SetInt("hint_swap", 1);
                PlayerPrefs.Save();
                if (BonusPopup.Instance != null)
                {
                    float cardX = GetCardWorldX(cardIndex);
                    float cardY = GetCardWorldY();
                    BonusPopup.Instance.Show("TRADE A CARD", new Color(0.3f, 0.85f, 0.9f, 1f),
                        new Vector3(cardX, cardY + 0.5f, -5f), 1.1f);
                }
            }

            if (MatchController.Instance == null) return;
            bool success = MatchController.Instance.UseSwap(cardIndex);
            if (success)
            {
//                 Debug.Log($"[HandManager] Swap executed on card {cardIndex}");
                RefreshHandFromMatchController();
                RefreshAllCardVisuals();
            }
        }

        // ── Rewrite Tile ─────────────────────────────────────────────────────

        /// <summary>
        /// Called when a board tile is long-pressed. Validates it's a valid
        /// rewrite target and enters rewrite mode if so.
        /// </summary>
        private void TryEnterRewriteMode(int col, int row)
        {
            if (TutorialManager.BlockShuffleAndSwap) return;
            if (MatchController.Instance == null || RulesEngine.Instance == null) return;

            int rewritesLeft = MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN);
            if (rewritesLeft <= 0)
            {
//                 Debug.Log("[HandManager] Rewrite: no rewrites remaining.");
                return;
            }

            var cell = RulesEngine.Instance.GetCell(col, row);
            if (cell == null) return;

            // Gold and stone tiles cannot be rewritten
            Tile existingTile = _grid.GetTile(col, row);
            if (existingTile != null && existingTile.IsGoldBonus)
            {
//                 Debug.Log($"[HandManager] Rewrite: tile at ({col},{row}) is gold — cannot rewrite.");
                return;
            }
            if (cell.IsStone)
            {
//                 Debug.Log($"[HandManager] Rewrite: tile at ({col},{row}) is stone — cannot rewrite.");
                return;
            }

            var primed = RulesEngine.Instance.PrimedRegistry
                .GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primed != null && primed.Count > 0)
            {
//                 Debug.Log($"[HandManager] Rewrite: tile at ({col},{row}) is primed.");
                return;
            }

            // One-time contextual hint for first rewrite (only after validation passes)
            if (PlayerPrefs.GetInt("hint_rewrite", 0) == 0)
            {
                PlayerPrefs.SetInt("hint_rewrite", 1);
                PlayerPrefs.Save();
                if (BonusPopup.Instance != null && GridManager.Instance != null)
                {
                    Vector3 worldPos = GridManager.Instance.CellToWorld(col, row);
                    BonusPopup.Instance.Show("REPLACE A TILE", new Color(0.3f, 0.85f, 0.9f, 1f), worldPos, 1.1f);
                }
            }

            _rewriteModeActive = true;
            _rewriteTargetCol = col;
            _rewriteTargetRow = row;
            _rewriteTimeoutTimer = REWRITE_TIMEOUT;
            GameAudio.Instance?.PlayTileSelect();
            HapticsManager.Strong(); // haptic feedback so player knows edit mode is active

            // Pulse the target tile
            Tile targetTile = _grid.GetTile(col, row);
            if (targetTile != null)
            {
                targetTile.Highlight(true, REWRITE_HIGHLIGHT_COLOR);
                if (_rewritePulseCoroutine != null) StopCoroutine(_rewritePulseCoroutine);
                _rewritePulseCoroutine = StartCoroutine(RewritePulseCoroutine(targetTile));
            }

            // Deselect any selected card
            _selectedIndex = -1;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

//             Debug.Log($"[HandManager] Entered REWRITE mode: target ({col},{row}) " +
                      // $"letter='{cell.Letter}' — tap a hand card to replace it");
        }

        private static bool IsAdjacent(int c1, int r1, int c2, int r2)
        {
            int dx = Mathf.Abs(c1 - c2);
            int dy = Mathf.Abs(r1 - r2);
            return (dx + dy) == 1; // orthogonal only
        }

        /// <summary>
        /// Legal board swap: swap two adjacent non-primed tiles IF the result creates a valid word.
        /// Costs 1 rewrite charge. Returns true if swap executed.
        /// </summary>
        /// <summary>
        /// Board Swap: swap any two regular tiles on the board (no adjacency required,
        /// no word requirement). Costs 1 edit charge. Cannot swap primed, gold, or stone tiles.
        /// </summary>
        private bool TryBoardSwap(int col1, int row1, int col2, int row2)
        {
            var rules = RulesEngine.Instance;
            var mc = MatchController.Instance;
            if (rules == null || mc == null) return false;

            // Both tiles must exist
            var cell1 = rules.GetCell(col1, row1);
            var cell2 = rules.GetCell(col2, row2);
            if (cell1 == null || cell2 == null) return false;

            // Cannot swap special tiles
            if (cell1.IsStone || cell2.IsStone) return false;
            if (cell1.IsSwapRefill || cell1.IsEditRefill || cell1.IsWildRefill) return false;
            if (cell2.IsSwapRefill || cell2.IsEditRefill || cell2.IsWildRefill) return false;
            if (cell1.IsWild || cell2.IsWild) return false;

            // Cannot swap gold tiles
            Tile t1 = _grid.GetTile(col1, row1);
            Tile t2 = _grid.GetTile(col2, row2);
            if (t1 != null && t1.IsGoldBonus) return false;
            if (t2 != null && t2.IsGoldBonus) return false;

            // Cannot swap primed tiles
            var reg = rules.PrimedRegistry;
            if (reg != null)
            {
                if (reg.GetPrimedWordsContaining(new Vector2Int(col1, row1)).Count > 0) return false;
                if (reg.GetPrimedWordsContaining(new Vector2Int(col2, row2)).Count > 0) return false;
            }

            // Must have edit charges
            if (mc.GetRewritesRemaining(MatchController.PLAYER_HUMAN) <= 0)
            {
                GameAudio.Instance?.PlayButtonClick();
                return false;
            }

            // Reject same-letter swaps — no-op waste of edit
            char letter1 = cell1.Letter;
            char letter2 = cell2.Letter;
            if (char.ToUpper(letter1) == char.ToUpper(letter2))
            {
                GameAudio.Instance?.PlayButtonClick();
                return false;
            }

            // Consume edit charge
            mc.UseRewriteCharge(MatchController.PLAYER_HUMAN);

            // Phase 11d — both swapped cells count as edit targets. Either one
            // appearing in a 5+ directly-formed word earns the refund.
            mc.RecordEditCells(
                new Vector2Int(col1, row1),
                new Vector2Int(col2, row2));

            // Swap the letters in data
            cell1.Letter = letter2;
            cell2.Letter = letter1;

            // Purge scored keys for both cells so new words can be detected
            rules.PurgeScoredKeysForCells(new System.Collections.Generic.List<Vector2Int> {
                new Vector2Int(col1, row1), new Vector2Int(col2, row2)
            });

            CancelRewriteMode();

//             Debug.Log($"[HandManager] Board swap ({col1},{row1})↔({col2},{row2}): '{letter1}'↔'{letter2}'");

            // Disable input and run the SAME resolution pipeline as a regular edit
            IsInteractable = false;
            _selectedIndex = -1;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Use BoardSwapTurnSequence which animates both tiles then runs full resolution
            StartCoroutine(BoardSwapTurnSequence(col1, row1, letter2, col2, row2, letter1));
            return true;
        }

        /// <summary>
        /// Animates a board swap (dissolve both tiles, pop in with new letters)
        /// then runs the full rewrite resolution pipeline for scoring/priming/detonation.
        /// </summary>
        private IEnumerator BoardSwapTurnSequence(int col1, int row1, char newLetter1, int col2, int row2, char newLetter2)
        {
            if (JamHint.Instance != null) JamHint.Instance.ClearHint();
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            var rules = RulesEngine.Instance;
            var grid = GridManager.Instance;

            if (rules == null || grid == null)
            {
                IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // Animate both tiles — shake, swap letters, squish (same as edit)
            Tile tile1 = grid.GetTile(col1, row1);
            Tile tile2 = grid.GetTile(col2, row2);

            float shakeDur = 0.2f;
            float elapsed = 0f;
            float cellSize = grid.CellSize;
            float posJitter = cellSize * 0.08f;
            float rotJitter = 10f;
            Vector3 restPos1 = tile1 != null ? tile1.transform.position : Vector3.zero;
            Vector3 restPos2 = tile2 != null ? tile2.transform.position : Vector3.zero;

            GameAudio.Instance?.PlayShuffle();

            // Phase 1: shake both
            while (elapsed < shakeDur)
            {
                elapsed += Time.deltaTime;
                if (tile1 != null)
                {
                    tile1.transform.position = restPos1 + new Vector3(
                        Random.Range(-posJitter, posJitter), Random.Range(-posJitter, posJitter) * 0.5f, 0f);
                    tile1.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-rotJitter, rotJitter));
                }
                if (tile2 != null)
                {
                    tile2.transform.position = restPos2 + new Vector3(
                        Random.Range(-posJitter, posJitter), Random.Range(-posJitter, posJitter) * 0.5f, 0f);
                    tile2.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-rotJitter, rotJitter));
                }
                yield return null;
            }

            // Phase 2: swap letters
            if (tile1 != null) { tile1.SetLetter(newLetter1); tile1.transform.position = restPos1; tile1.transform.localRotation = Quaternion.identity; }
            if (tile2 != null) { tile2.SetLetter(newLetter2); tile2.transform.position = restPos2; tile2.transform.localRotation = Quaternion.identity; }

            // Phase 3: squish
            if (tile1 != null) tile1.PlayLandingSquish();
            if (tile2 != null) tile2.PlayLandingSquish();

            // Now run the FULL rewrite resolution on the first cell
            // BeginRewrite won't re-swap since we already swapped in TryBoardSwap
            // Instead, use BeginSwapResolution with detected words from both positions
            var words1 = rules.FindNewWords(col1, row1);
            var words2 = rules.FindNewWords(col2, row2);
            var allNew = new System.Collections.Generic.List<RulesWordMatch>();

            if (words1 != null)
                for (int i = 0; i < words1.Count; i++)
                {
                    string key = words1[i].Word + "|" + words1[i].CellKey;
                    if (!rules.IsScoredKey(key)) allNew.Add(words1[i]);
                }
            if (words2 != null)
                for (int i = 0; i < words2.Count; i++)
                {
                    string key = words2[i].Word + "|" + words2[i].CellKey;
                    if (!rules.IsScoredKey(key))
                    {
                        bool dup = false;
                        for (int j = 0; j < allNew.Count; j++)
                            if (allNew[j].CellKey == words2[i].CellKey) { dup = true; break; }
                        if (!dup) allNew.Add(words2[i]);
                    }
                }

            if (allNew.Count > 0)
            {
                // Score, prime, and show visuals for each word — same as RewriteTurnSequence
                var registry = rules.PrimedRegistry;
                int globalTurn = rules.GlobalTurn;
                int playerIdx = MatchController.PLAYER_HUMAN;
                var justPrimedIds = new HashSet<int>();

                for (int i = 0; i < allNew.Count; i++)
                {
                    var match = allNew[i];
                    match.Score = RulesEngine.CalculateWordScore(match.Word, match.WildLetterIndices);
                    rules.RegisterScoredKey(match.Word + "|" + match.CellKey);

                    int fuse = rules.GetFuseLengthPublic(match.Word.Length);
                    int primedId = registry.AddPrimedWord(match.Word, match.Cells, playerIdx, globalTurn, globalTurn + fuse, match.Score);
                    justPrimedIds.Add(primedId);

                    // Visual: primed glow + particles
                    var scoredTiles = new System.Collections.Generic.List<Tile>();
                    if (match.Cells != null)
                    {
                        for (int c = 0; c < match.Cells.Count; c++)
                        {
                            Tile t = grid.GetTile(match.Cells[c].x, match.Cells[c].y);
                            if (t != null)
                            {
                                t.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: true, fuseRemaining: fuse, maxAge: match.Word.Length <= 3 ? 25f : match.Word.Length == 4 ? 30f : match.Word.Length == 5 ? 38f : 45f);
                                GameParticles.Instance?.PlayPrimed(t.transform.position);
                                scoredTiles.Add(t);
                            }
                        }
                    }

                    // Word scored FX + popup
                    if (WordDropFX.Instance != null)
                        WordDropFX.Instance.PlayWordScored(scoredTiles, new Color(0.9f, 0.2f, 0.8f), i);
                    HapticsManager.Light();

                    if (BonusPopup.Instance != null && scoredTiles.Count > 0)
                    {
                        Vector3 center = Vector3.zero;
                        for (int c = 0; c < scoredTiles.Count; c++)
                            if (scoredTiles[c] != null) center += scoredTiles[c].transform.position;
                        center /= Mathf.Max(1, scoredTiles.Count);
                        BonusPopup.Instance.ShowWordScore(match.Word, match.Score, center);
                    }

                    // Survival rewrite meter
                    if (MatchController.Instance != null)
                        MatchController.Instance.SurvivalWordScored();
                }
                GameAudio.Instance?.PlayTilePrimed();

                // Global fuse reset: sync all existing primed words to the new
                // fuse timeline, same as BeginDrop/BeginRewrite paths do via
                // DoScoreAndPrime. Without this the board-swap path leaves
                // pre-existing primes on their original, stale timers.
                rules.ResetExistingPrimedWordsExternal(justPrimedIds);

                // Swaps intentionally do NOT count as a move (Balatro-discard
                // analog — no hand card consumed, pure board reshuffle).
                // They DO contribute score (CurrentStageScore auto-derives
                // from PlayerScore so swap points count automatically).
                // Trigger the clear check in case this swap crossed target.
                if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                    SurvivalManager.Instance.CheckStageClear();

                yield return WaitCache.Get(0.3f);

                // Check for detonation triggers
                bool triggeredPrimed = false;
                for (int i = 0; i < allNew.Count && !triggeredPrimed; i++)
                {
                    if (allNew[i].Cells == null) continue;
                    for (int c = 0; c < allNew[i].Cells.Count; c++)
                    {
                        var overlapping = registry.GetPrimedWordsContaining(allNew[i].Cells[c]);
                        if (overlapping != null)
                            for (int p = 0; p < overlapping.Count; p++)
                                if (!justPrimedIds.Contains(overlapping[p].Id))
                                { triggeredPrimed = true; break; }
                        if (triggeredPrimed) break;

                        if (RulesEngine.AdjacencyTriggerEnabled)
                        {
                            var adj = registry.GetPrimedWordsAdjacentTo(allNew[i].Cells[c]);
                            if (adj != null)
                                for (int p = 0; p < adj.Count; p++)
                                    if (!justPrimedIds.Contains(adj[p].Id))
                                    { triggeredPrimed = true; break; }
                        }
                        if (triggeredPrimed) break;
                    }
                }

//                 Debug.Log($"[BoardSwap] triggeredPrimed={triggeredPrimed}, allNew={allNew.Count}, justPrimed={justPrimedIds.Count}, registry={registry.Count}");

                // Apply base scores for the swap-created words
                int swapBaseScore = 0;
                for (int i = 0; i < allNew.Count; i++)
                    swapBaseScore += allNew[i].Score;
                if (swapBaseScore > 0 && ScoreManager.Instance != null)
                {
                    ScoreManager.Instance.AddScore(swapBaseScore, MatchController.PLAYER_HUMAN);
                    // Phase 5.1 fix: board-swap scoring bypasses CompleteDropBookkeeping,
                    // so LevelController.NotifyDrop never fires. Route the score delta through
                    // NotifyScore (no move consumed) so target-cross completes the level.
                    if (GameManager.IsLevelMode)
                        LevelController.Instance?.NotifyScore(swapBaseScore);
                }

                // If detonation triggered, run full step resolution
                if (triggeredPrimed)
                {
                    rules.BeginSwapResolution(allNew, MatchController.PLAYER_HUMAN, justPrimedIds);

                    bool resolving = true;
                    int swapScore = 0;

                    while (resolving)
                    {
                        var step = rules.NextStep();
                        if (step == null) { resolving = false; break; }

                        switch (step.Phase)
                        {
                            case RulesEngine.ResolutionPhase.WordsDetected:
                                break;
                            case RulesEngine.ResolutionPhase.WordsScored:
                                if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                                {
                                    HapticsManager.Light();
                                    GameAudio.Instance?.PlayTilePrimed();
                                }
                                break;

                            case RulesEngine.ResolutionPhase.TriggersFound:
                                CacheBurstTriggers(step);
                                yield return WaitCache.Get(0.05f);
                                break;

                            case RulesEngine.ResolutionPhase.Exploding:
                            {
                                if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                                {
                                    var dyingTiles = new System.Collections.Generic.List<Tile>();
                                    foreach (var c in step.ExplodedCells)
                                    {
                                        Tile t = grid.GetTile(c.x, c.y);
                                        if (t != null) dyingTiles.Add(t);
                                    }

                                    HapticsManager.Strong();

                                    // Tiered explosion (handles sound + visuals)
                                    if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                    {
                                        if (step.ChainDepth >= 2)
                                            yield return StartCoroutine(WordDropFX.HitStop(0.04f));
                                        // Flash fires AFTER hitstop, immediately before
                                        // PlayExplosion, so timeScale=0 doesn't freeze the
                                        // tween during the pause and the flash plays in sync
                                        // with the actual tile-dissolve animation.
                                        FirePerWordBurst();
                                        FireTileFlashBoxes(dyingTiles);
                                        int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dyingTiles.Count;
                                        yield return WordDropFX.Instance.PlayExplosion(dyingTiles, step.ChainDepth, wLen);
                                    }
                                    grid.RemoveTiles(step.ExplodedCells);

                                    if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                                        SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                                    if (step.DetonationBonus > 0 && BonusPopup.Instance != null && dyingTiles.Count > 0)
                                    {
                                        Vector3 center = Vector3.zero;
                                        foreach (var t in dyingTiles)
                                            if (t != null) center += t.transform.position;
                                        center /= Mathf.Max(1, dyingTiles.Count);
                                        BonusPopup.Instance.ShowDetonation("", step.DetonationBonus, center, step.ChainDepth);
                                    }
                                }
                                swapScore = step.TotalScore;
                                break;
                            }

                            case RulesEngine.ResolutionPhase.GravityApplied:
                                yield return StartCoroutine(grid.ApplyGravity());
                                yield return WaitCache.Get(0.1f);
                                break;

                            case RulesEngine.ResolutionPhase.Complete:
                                resolving = false;
                                rules.FinalizeDrop();
                                grid.SyncToRulesState(rules);
                                break;

                            default:
                                resolving = false;
                                break;
                        }
                    }

                    if (swapScore > 0 && ScoreManager.Instance != null)
                    {
                        ScoreManager.Instance.AddScore(swapScore, MatchController.PLAYER_HUMAN);
                        // Phase 5.1 fix: same bypass as the base-score path above.
                        if (GameManager.IsLevelMode)
                            LevelController.Instance?.NotifyScore(swapScore);
                    }

                    if (MatchController.Instance != null)
                    {
                        MatchController.Instance.RefundRewriteCharge(MatchController.PLAYER_HUMAN);
//                         Debug.Log("[BoardSwap] Detonation → edit refunded");
                    }
                }
            }

            // Finalize — even if no detonation, sync state
            if (rules != null)
            {
                rules.FinalizeDrop();
                if (grid != null) grid.SyncToRulesState(rules);
            }

            IsInteractable = true;
            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
        }

        private bool TryLegalSwap(int col1, int row1, int col2, int row2)
        {
            var rules = RulesEngine.Instance;
            var mc = MatchController.Instance;
            if (rules == null || mc == null) return false;

            // Validate both tiles exist and aren't primed/stone/gold/wild
            var cell1 = rules.GetCell(col1, row1);
            var cell2 = rules.GetCell(col2, row2);
            if (cell1 == null || cell2 == null) return false;
            if (cell1.IsStone || cell2.IsStone) return false;
            if (cell1.IsWild || cell2.IsWild) return false;

            // Gold tiles can't be swapped
            Tile t1Check = _grid.GetTile(col1, row1);
            Tile t2Check = _grid.GetTile(col2, row2);
            if (t1Check != null && t1Check.IsGoldBonus) return false;
            if (t2Check != null && t2Check.IsGoldBonus) return false;

            var reg = rules.PrimedRegistry;
            if (reg != null)
            {
                if (reg.GetPrimedWordsContaining(new Vector2Int(col1, row1)).Count > 0) return false;
                if (reg.GetPrimedWordsContaining(new Vector2Int(col2, row2)).Count > 0) return false;
            }

            // Temporarily swap in rules engine
            char letter1 = cell1.Letter;
            char letter2 = cell2.Letter;
            cell1.Letter = letter2;
            cell2.Letter = letter1;

            // Check if the swap creates any new valid word
            var words1 = rules.FindNewWords(col1, row1);
            var words2 = rules.FindNewWords(col2, row2);

            bool createsWord = false;
            if (words1 != null)
                for (int i = 0; i < words1.Count; i++)
                {
                    string key = words1[i].Word + "|" + words1[i].CellKey;
                    if (!rules.IsScoredKey(key)) { createsWord = true; break; }
                }
            if (!createsWord && words2 != null)
                for (int i = 0; i < words2.Count; i++)
                {
                    string key = words2[i].Word + "|" + words2[i].CellKey;
                    if (!rules.IsScoredKey(key)) { createsWord = true; break; }
                }

            if (!createsWord)
            {
                // Swap doesn't create a word — revert
                cell1.Letter = letter1;
                cell2.Letter = letter2;
//                 Debug.Log($"[HandManager] Legal swap ({col1},{row1})↔({col2},{row2}): no valid word — rejected");
                GameAudio.Instance?.PlayButtonClick(); // feedback: "nope"
                return false;
            }

            // Swap creates a word! Commit it.
            // Update cell positions
            cell1.Col = col1; cell1.Row = row1;
            cell2.Col = col2; cell2.Row = row2;

            // Consume rewrite charge
            mc.UseRewriteCharge(MatchController.PLAYER_HUMAN);

            // Update visuals
            if (_grid != null)
            {
                Tile t1 = _grid.GetTile(col1, row1);
                Tile t2 = _grid.GetTile(col2, row2);
                if (t1 != null) t1.SetLetter(letter2);
                if (t2 != null) t2.SetLetter(letter1);

                // Quick swap animation
                if (t1 != null) t1.PlayLandingSquish();
                if (t2 != null) t2.PlayLandingSquish();
            }

            CancelRewriteMode();
            GameAudio.Instance?.PlayTilePrimed(); // satisfying feedback

//             Debug.Log($"[HandManager] Legal swap ({col1},{row1})↔({col2},{row2}): '{letter1}'↔'{letter2}' — word created!");

            // Run resolution from the swap (words will be detected, primed, possibly detonated)
            StartCoroutine(SwapResolutionSequence(col1, row1, col2, row2));
            return true;
        }

        private System.Collections.IEnumerator SwapResolutionSequence(int col1, int row1, int col2, int row2)
        {
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();
            if (JamHint.Instance != null) JamHint.Instance.ClearHint();

            var rules = RulesEngine.Instance;
            var grid = GridManager.Instance;
            if (rules == null || grid == null)
            {
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            int playerIdx = MatchController.PLAYER_HUMAN;

            // Find new words at both swap positions
            var words1 = rules.FindNewWords(col1, row1);
            var words2 = rules.FindNewWords(col2, row2);
            var allNew = new List<RulesWordMatch>();

            // Collect genuinely new words (not already scored)
            if (words1 != null)
                for (int i = 0; i < words1.Count; i++)
                {
                    string key = words1[i].Word + "|" + words1[i].CellKey;
                    if (!rules.IsScoredKey(key)) allNew.Add(words1[i]);
                }
            if (words2 != null)
                for (int i = 0; i < words2.Count; i++)
                {
                    string key = words2[i].Word + "|" + words2[i].CellKey;
                    if (!rules.IsScoredKey(key))
                    {
                        // Avoid duplicates
                        bool dup = false;
                        for (int j = 0; j < allNew.Count; j++)
                            if (allNew[j].CellKey == words2[i].CellKey) { dup = true; break; }
                        if (!dup) allNew.Add(words2[i]);
                    }
                }

            // Score and prime new words — attributed to HUMAN player
            bool triggeredPrimed = false;
            var justPrimedIds = new HashSet<int>();
            if (allNew.Count > 0)
            {
                var registry = rules.PrimedRegistry;
                int globalTurn = rules.GlobalTurn;

                for (int i = 0; i < allNew.Count; i++)
                {
                    var match = allNew[i];
                    match.Score = RulesEngine.CalculateWordScore(match.Word, match.WildLetterIndices);
                    rules.RegisterScoredKey(match.Word + "|" + match.CellKey);

                    // Check if this new word overlaps or is adjacent to existing primed words → trigger
                    if (registry != null && match.Cells != null)
                    {
                        for (int c = 0; c < match.Cells.Count && !triggeredPrimed; c++)
                        {
                            var overlapping = registry.GetPrimedWordsContaining(match.Cells[c]);
                            if (overlapping != null && overlapping.Count > 0)
                            {
                                triggeredPrimed = true;
                                break;
                            }

                            if (RulesEngine.AdjacencyTriggerEnabled)
                            {
                                var adjacent = registry.GetPrimedWordsAdjacentTo(match.Cells[c]);
                                if (adjacent != null && adjacent.Count > 0)
                                {
                                    triggeredPrimed = true;
                                    break;
                                }
                            }
                        }
                    }

                    // Prime the word (human-owned)
                    int fuse = rules.GetFuseLengthPublic(match.Word.Length);
                    int primedId = registry.AddPrimedWord(match.Word, match.Cells, playerIdx, globalTurn, globalTurn + fuse, match.Score);
                    justPrimedIds.Add(primedId);

                    // Visual: apply primed glow
                    if (match.Cells != null)
                    {
                        for (int c = 0; c < match.Cells.Count; c++)
                        {
                            Tile tile = grid.GetTile(match.Cells[c].x, match.Cells[c].y);
                            if (tile != null)
                            {
                                tile.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: true, fuseRemaining: fuse);
                                GameParticles.Instance?.PlayPrimed(tile.transform.position);
                            }
                        }
                    }

                    // Score popup
                    if (BonusPopup.Instance != null && match.Cells != null && match.Cells.Count > 0)
                    {
                        Vector3 center = Vector3.zero;
                        for (int c = 0; c < match.Cells.Count; c++)
                        {
                            Tile t = grid.GetTile(match.Cells[c].x, match.Cells[c].y);
                            if (t != null) center += t.transform.position;
                        }
                        center /= Mathf.Max(1, match.Cells.Count);
                        BonusPopup.Instance.ShowWordScore(match.Word, match.Score, center);
                    }
                }
                GameAudio.Instance?.PlayTilePrimed();
            }

            // If the swap created words that trigger primed words, run full
            // detonation resolution via RulesEngine's step system.
            // This is the fix for Codex audit #10: swaps must detonate, not just prime.
            if (triggeredPrimed && allNew.Count > 0)
            {
//                 Debug.Log("[SwapResolution] Swap triggered primed word — running full resolution");

                // Feed swap words into RulesEngine for resolution.
                // Use BeginRewrite-style init since the tile is already on the board.
                // We pick the first swap cell as the "drop" origin.
                rules.BeginSwapResolution(allNew, playerIdx, justPrimedIds);

                bool resolving = true;
                int swapScore = 0;

                while (resolving)
                {
                    var step = rules.NextStep();
                    if (step == null) { resolving = false; break; }

                    switch (step.Phase)
                    {
                        case RulesEngine.ResolutionPhase.WordsDetected:
                            break;
                        case RulesEngine.ResolutionPhase.WordsScored:
                            // Play word scored feedback for chain words found after gravity
                            if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                            {
                                HapticsManager.Light();
                                GameAudio.Instance?.PlayTilePrimed();
                            }
                            break;

                        case RulesEngine.ResolutionPhase.TriggersFound:
                        {
                            if (step.Triggers != null)
                            {
                                foreach (var trig in step.Triggers)
                                {
                                    var pw = rules.PrimedRegistry != null ? rules.PrimedRegistry.GetById(trig.PrimedWordId) : null;
                                    int currentTurn = rules.GlobalTurn;
                                    int heatLevel = pw != null ? Mathf.Min(Mathf.Max(0, currentTurn - pw.PrimedOnTurn), RulesEngine.HEAT_FUSE_MAX_BONUS) : 0;
                                    int fuse = pw != null ? Mathf.Max(0, pw.ExpiresOnTurn - currentTurn) : 0;
                                    bool isGold = pw != null && pw.IsGold;
                                    Color glowColor = isGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                                    foreach (var c in trig.TriggeredCells)
                                    {
                                        Tile t = grid.GetTile(c.x, c.y);
                                        if (t != null)
                                            t.SetPrimedGlow(glowColor, playFlash: true, heatLevel: heatLevel, fuseRemaining: fuse, isGold: isGold);
                                    }
                                }
                            }
                            CacheBurstTriggers(step);
                            yield return WaitCache.Get(0.05f);
                            break;
                        }

                        case RulesEngine.ResolutionPhase.Exploding:
                        {
                            if (ChainCounter.Instance != null)
                                ChainCounter.Instance.OnDetonation(step.ChainDepth);

                            if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                            {
                                var dyingTiles = new System.Collections.Generic.List<Tile>();
                                foreach (var c in step.ExplodedCells)
                                {
                                    Tile t = grid.GetTile(c.x, c.y);
                                    if (t != null) dyingTiles.Add(t);
                                }

                                // Haptic + hitstop
                                HapticsManager.Strong();
                                if (dyingTiles.Count > 0)
                                {
                                    float hitStopDur = step.ChainDepth >= 2 ? 0.08f : 0.05f;
                                    yield return StartCoroutine(WordDropFX.HitStop(hitStopDur));
                                }

                                // Flash after hitstop so timeScale=0 doesn't freeze its tween.
                                FirePerWordBurst();
                                FireTileFlashBoxes(dyingTiles);
                                if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                    yield return WordDropFX.Instance.PlayExplosion(dyingTiles, 0);
                                grid.RemoveTiles(step.ExplodedCells);

                                // Notify post-clear boost
                                if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                                    && step.ExplodedCells != null)
                                    SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                                // Show detonation score
                                if (step.DetonationBonus > 0 && BonusPopup.Instance != null && dyingTiles.Count > 0)
                                {
                                    Vector3 center = Vector3.zero;
                                    foreach (var t in dyingTiles)
                                        if (t != null) center += t.transform.position;
                                    center /= Mathf.Max(1, dyingTiles.Count);
                                    BonusPopup.Instance.Show($"+{step.DetonationBonus}", new Color(1f, 0.5f, 0.1f), center);
                                }

//                                 Debug.Log($"[SwapResolution] Exploded {step.ExplodedCells.Count} tiles, bonus={step.DetonationBonus}");
                            }
                            swapScore = step.TotalScore; // cumulative — just take the latest
                            break;
                        }

                        case RulesEngine.ResolutionPhase.GravityApplied:
                        {
                            yield return StartCoroutine(grid.ApplyGravity());
                            yield return WaitCache.Get(0.1f);
                            break;
                        }

                        case RulesEngine.ResolutionPhase.Complete:
                        {
                            if (ChainCounter.Instance != null)
                                ChainCounter.Instance.OnChainComplete();
                            resolving = false;
                            rules.FinalizeDrop();
                            grid.SyncToRulesState(rules);
                            break;
                        }

                        default:
                            resolving = false;
                            break;
                    }
                }

                // Refund rewrite on detonation
                if (MatchController.Instance != null)
                {
                    MatchController.Instance.RefundRewriteCharge(playerIdx);
//                     Debug.Log("[SwapResolution] Swap detonation → rewrite refunded");
                }

                // Update score
                if (swapScore > 0 && ScoreManager.Instance != null)
                {
                    ScoreManager.Instance.AddScore(swapScore, playerIdx);
                    // Phase 5.1 fix: swap-resolution detonation score bypasses
                    // CompleteDropBookkeeping. Route through NotifyScore for Level mode
                    // so target-cross completes the level.
                    if (GameManager.IsLevelMode && playerIdx == MatchController.PLAYER_HUMAN)
                        LevelController.Instance?.NotifyScore(swapScore);
                }
            }
            else
            {
                yield return WaitCache.Get(0.3f);
            }

            IsInteractable = true;
            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
        }

        private void CancelRewriteMode()
        {
            if (_rewritePulseCoroutine != null) { StopCoroutine(_rewritePulseCoroutine); _rewritePulseCoroutine = null; }

            if (_rewriteTargetCol >= 0 && _rewriteTargetRow >= 0)
            {
                Tile targetTile = _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow);
                if (targetTile != null)
                {
                    targetTile.Highlight(false);
                    targetTile.ResetVisuals();
                }
            }

            _rewriteModeActive = false;
            _rewriteTargetCol = -1;
            _rewriteTargetRow = -1;
//             Debug.Log("[HandManager] Rewrite mode cancelled.");
        }

        private static readonly Color REWRITE_LOW  = new Color(0.85f, 0.95f, 0.95f, 1f); // subtle teal tint
        private static readonly Color REWRITE_HIGH = new Color(0.6f, 1.9f, 1.8f, 1f);   // HDR teal — bloom peak

        // ═══════════════════════════════════════════════════════════════════════════
        // Big Burst Flash — per-word wall-of-light on detonation
        // ═══════════════════════════════════════════════════════════════════════════

        // step.Triggers is populated in TriggersFound and null'd by the time
        // Exploding fires — but we want the flash to land WITH the actual explosion,
        // not during the pre-anticipation. Cache here, then fire in Exploding.
        private List<PrimedTriggeredEvent>       _pendingBurstTriggers;
        private List<RulesWordMatch> _pendingBurstTriggerWords;
        private int  _pendingBurstChainDepth;
        private int  _pendingBurstLongestWord;
        // ScreenFlash should fire ONCE per burst pass, not once per primed word.
        // FirePerWordBurst iterates cluster members and we only want one screen tint.
        private bool _screenFlashFiredThisBurst;

        /// <summary>
        /// Fires a TileFlashBox under each dying tile. Single coin flip PER
        /// DETONATION — either the whole cluster lights up, or none does. Keeps
        /// the effect as a moment-to-moment surprise rather than a uniform overlay.
        /// Tune TILE_FLASH_BOX_CHANCE to taste.
        /// </summary>
        private const float TILE_FLASH_BOX_CHANCE = 0.6f; // 60% of detonations show boxes
        private void FireTileFlashBoxes(IList<Tile> dying)
        {
            if (TileFlashBox.Instance == null || _grid == null || dying == null) return;
            if (dying.Count == 0) return;

            // One roll for the entire detonation — all-or-nothing variety.
            if (Random.value >= TILE_FLASH_BOX_CHANCE) return;

            float cellSize = _grid.CellSize;
            for (int i = 0; i < dying.Count; i++)
            {
                Tile t = dying[i];
                if (t == null) continue;
                TileFlashBox.Instance.Play(t.transform.position, cellSize);
            }
        }

        private void CacheBurstTriggers(RulesEngine.StepResult step)
        {
            if (step == null || step.Triggers == null || step.Triggers.Count == 0)
            {
                _pendingBurstTriggers     = null;
                _pendingBurstTriggerWords = null;
                return;
            }
            _pendingBurstTriggers     = new List<PrimedTriggeredEvent>(step.Triggers);
            _pendingBurstTriggerWords = step.TriggerWords != null
                ? new List<RulesWordMatch>(step.TriggerWords) : null;
            _pendingBurstChainDepth  = step.ChainDepth;
            _pendingBurstLongestWord = step.LongestWordLength;
        }

        /// <summary>
        /// Fires one BigBurstFlash per triggered primed word, using the cached
        /// triggers captured during TriggersFound. Called from every Exploding
        /// phase handler so the flash lands in sync with the actual explosion.
        ///
        /// Gated to BIG moments so it stays a rare "whoa" beat:
        ///   - chain depth >= 2 (cascading detonation)
        ///   - OR longest primed word >= 6 letters
        ///   - OR 3+ primed words in a single cluster
        /// All other (small solo) detonations fall back to the existing flipbook +
        /// particle stack without the screen-wide flash.
        /// </summary>
        private void FirePerWordBurst()
        {
            _screenFlashFiredThisBurst = false; // reset per burst pass
            if (BigBurstFlash.Instance == null) { _pendingBurstTriggers = null; _pendingBurstTriggerWords = null; return; }
            if (_grid == null) { _pendingBurstTriggers = null; _pendingBurstTriggerWords = null; return; }
            if (_pendingBurstTriggers == null || _pendingBurstTriggers.Count == 0) return;

            // Loosened gate — chainDepth >= 1 means "this is a chain continuation"
            // (the 2nd+ detonation in a chain reaction), so any chain reaction fires
            // the sweep on its continuation step. Word/cluster thresholds dropped
            // so big first detonations also qualify without being freakishly rare.
            bool bigMoment = _pendingBurstChainDepth >= 1
                          || _pendingBurstLongestWord >= 5
                          || _pendingBurstTriggers.Count >= 2;
            Debug.Log($"[BigBurst] bigMoment={bigMoment} — chainDepth={_pendingBurstChainDepth} " +
                      $"longestWord={_pendingBurstLongestWord} triggers={_pendingBurstTriggers.Count}");
            if (!bigMoment)
            {
                // Small detonation — no screen-wide flash. Clear cache and bail so
                // we don't leak stale triggers into the next big moment.
                _pendingBurstTriggers     = null;
                _pendingBurstTriggerWords = null;
                return;
            }

            Color burstTint = (_pendingBurstChainDepth >= 2 && _pendingBurstLongestWord >= 6)
                ? new Color(1.8f, 1.4f, 0.7f, 1f)   // HDR warm gold — big + skilled
                : new Color(1.6f, 1.6f, 1.6f, 1f);  // HDR white

            foreach (var trig in _pendingBurstTriggers)
            {
                if (trig.TriggeredCells == null || trig.TriggeredCells.Count == 0) continue;

                int minCol = int.MaxValue, maxCol = int.MinValue;
                int minRow = int.MaxValue, maxRow = int.MinValue;
                Vector3 wordCenter = Vector3.zero;
                int tileCount = 0;
                foreach (var cell in trig.TriggeredCells)
                {
                    if (cell.x < minCol) minCol = cell.x;
                    if (cell.x > maxCol) maxCol = cell.x;
                    if (cell.y < minRow) minRow = cell.y;
                    if (cell.y > maxRow) maxRow = cell.y;
                    Tile wt = _grid.GetTile(cell.x, cell.y);
                    if (wt != null) { wordCenter += wt.transform.position; tileCount++; }
                }
                if (tileCount == 0) continue;
                wordCenter /= tileCount;

                bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                int wordLen = vertical ? (maxRow - minRow + 1) : (maxCol - minCol + 1);

                // Flash spans the full viewport along the word's axis — extends past
                // the screen edges so the impact feels unbounded.
                // Horizontal word → flash covers the full screen WIDTH.
                // Vertical word   → flash covers the full screen HEIGHT.
                Camera cam = _cam != null ? _cam : Camera.main;
                float halfH = cam != null ? cam.orthographicSize : 10f;
                float halfW = halfH * ((float)Screen.width / Screen.height);
                float targetLength = (vertical ? halfH : halfW) * 2.4f; // 2x = screen edge; 2.4x bleeds past

                // Thickness spans just a bit more than one tile row (cell + bleed) so
                // the blast reads as a narrow beam through the word, not a fat slab.
                float thickness = _grid.CellSize * 1.4f;
                BigBurstFlash.Instance.Play(wordCenter, targetLength, thickness, vertical, burstTint);

                // Radial burst accent at the word's center — "the star at the center
                // of the explosion" that BigBurstFlash radiates from. Size matches
                // ~3 cells so it reads as a punchy point, not another wide sweep.
                if (RadialBurst.Instance != null)
                    RadialBurst.Instance.Play(wordCenter, _grid.CellSize * 3.0f, burstTint);

                // HDR sparkle spray — 8-16 tiny stars flying radially outward.
                // Intensity scales with word length so longer words throw more stars.
                if (SparkleSpray.Instance != null)
                {
                    float intensity = Mathf.Clamp01((wordLen - 3) / 4f + 0.4f);
                    SparkleSpray.Instance.Play(wordCenter, intensity);
                }

                // Full-screen color wash — briefly tints the whole screen so the
                // moment reads "something big just happened" even outside the
                // detonation area. Fires once per burst pass (not per trigger
                // word) via the shared _screenFlashFiredThisBurst gate below.
                if (ScreenFlash.Instance != null && !_screenFlashFiredThisBurst)
                {
                    float intensity = Mathf.Clamp01((wordLen - 3) / 4f + 0.5f);
                    ScreenFlash.Instance.Play(burstTint, intensity);
                    _screenFlashFiredThisBurst = true;
                }

                // Scatter flare_star sparkles along the full blast line — matches
                // the Candy Crush look where stars are scattered across the entire
                // flash, not just the detonation cells.
                if (GameParticles.Instance != null)
                {
                    Vector3 lineStart, lineEnd;
                    if (vertical)
                    {
                        lineStart = new Vector3(wordCenter.x, wordCenter.y - halfH * 1.1f, wordCenter.z);
                        lineEnd   = new Vector3(wordCenter.x, wordCenter.y + halfH * 1.1f, wordCenter.z);
                    }
                    else
                    {
                        lineStart = new Vector3(wordCenter.x - halfW * 1.1f, wordCenter.y, wordCenter.z);
                        lineEnd   = new Vector3(wordCenter.x + halfW * 1.1f, wordCenter.y, wordCenter.z);
                    }
                    // Sparkle count scales lightly with word length so longer words
                    // get proportionally more stars without flooding the screen.
                    int sparkleCount = Mathf.Clamp(14 + wordLen * 2, 14, 26);
                    GameParticles.Instance.PlaySparkleLine(lineStart, lineEnd, sparkleCount);
                }
            }
            // Second pass: the TRIGGER words themselves (the new words the player
            // just formed that ignited the primed cluster). Each gets its own blast
            // so the player's own word lights up too, not only the primed casualties.
            if (_pendingBurstTriggerWords != null)
            {
                foreach (var tw in _pendingBurstTriggerWords)
                {
                    if (tw == null || tw.Cells == null || tw.Cells.Count == 0) continue;

                    int minCol = int.MaxValue, maxCol = int.MinValue;
                    int minRow = int.MaxValue, maxRow = int.MinValue;
                    Vector3 twCenter = Vector3.zero;
                    int twTileCount = 0;
                    foreach (var cell in tw.Cells)
                    {
                        if (cell.x < minCol) minCol = cell.x;
                        if (cell.x > maxCol) maxCol = cell.x;
                        if (cell.y < minRow) minRow = cell.y;
                        if (cell.y > maxRow) maxRow = cell.y;
                        Tile wtTile = _grid.GetTile(cell.x, cell.y);
                        if (wtTile != null) { twCenter += wtTile.transform.position; twTileCount++; }
                    }
                    if (twTileCount == 0) continue;
                    twCenter /= twTileCount;

                    bool twVertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                    int  twWordLen  = twVertical ? (maxRow - minRow + 1) : (maxCol - minCol + 1);

                    Camera cam = _cam != null ? _cam : Camera.main;
                    float halfH = cam != null ? cam.orthographicSize : 10f;
                    float halfW = halfH * ((float)Screen.width / Screen.height);
                    float targetLength = (twVertical ? halfH : halfW) * 2.4f;
                    float thickness    = _grid.CellSize * 1.4f;

                    BigBurstFlash.Instance.Play(twCenter, targetLength, thickness, twVertical, burstTint);

                    // Radial burst on the trigger word too — the player's own word
                    // gets the same center-of-impact treatment as the primed words.
                    if (RadialBurst.Instance != null)
                        RadialBurst.Instance.Play(twCenter, _grid.CellSize * 3.0f, burstTint);

                    if (SparkleSpray.Instance != null)
                    {
                        float intensity = Mathf.Clamp01((twWordLen - 3) / 4f + 0.4f);
                        SparkleSpray.Instance.Play(twCenter, intensity);
                    }

                    if (GameParticles.Instance != null)
                    {
                        Vector3 lineStart, lineEnd;
                        if (twVertical)
                        {
                            lineStart = new Vector3(twCenter.x, twCenter.y - halfH * 1.1f, twCenter.z);
                            lineEnd   = new Vector3(twCenter.x, twCenter.y + halfH * 1.1f, twCenter.z);
                        }
                        else
                        {
                            lineStart = new Vector3(twCenter.x - halfW * 1.1f, twCenter.y, twCenter.z);
                            lineEnd   = new Vector3(twCenter.x + halfW * 1.1f, twCenter.y, twCenter.z);
                        }
                        int twSparkleCount = Mathf.Clamp(14 + twWordLen * 2, 14, 26);
                        GameParticles.Instance.PlaySparkleLine(lineStart, lineEnd, twSparkleCount);
                    }
                }
            }

            // Consume the cache — next Exploding phase won't double-fire stale triggers.
            _pendingBurstTriggers     = null;
            _pendingBurstTriggerWords = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Wild Tiles Phase C — reward injection
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Per-frame check for wild expiry. Clears the wild to a vowel when either
        /// the 3-drop counter (PlayerHand._wildDropsElapsed) or the 20s playable-time
        /// timer elapses. No-op when no wild is in hand.
        /// </summary>
        private void TickWildExpiry()
        {
            // Wild tiles no longer time out — player design decision (2026-04-17).
            // Rationale: wilds are already scarce (chain-reward only). Opportunity
            // cost of using a wild is its natural balance; artificial timeout
            // punishes careful planning. Supporting machinery below is preserved
            // in case we want to re-enable soft-timeout later.
            return;
        }

        /// <summary>
        /// Queue a wild injection as a chain-reward. Gated by the per-resolution cap
        /// and by PlayerHand's max-one-wild invariant. Fires popup + SFX on success.
        /// </summary>
        private void TryInjectWildReward(Vector3 popupWorldPos)
        {
            // Phase 5 mechanic gate. Single choke point for wild Phase C injection.
            // Callers at 3840, 3884 flow through here, so one gate covers all paths.
            if (!LevelController.IsMechanicAllowed("wild")) return;

            if (_wildInjectedThisResolution) return;
            if (MatchController.Instance == null) return;
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            if (!hand.InjectWildFromChainReward()) return;

            _wildInjectedThisResolution = true;
            // Do NOT anchor _wildInjectedAt here — the wild is only QUEUED in pending
            // state at this point, not visible in hand. TickWildExpiry anchors the
            // clock to the first frame HasWild becomes true (after DrawSlot fills
            // the pending wild into a slot), which gives the player a fair 20s from
            // when they can actually see and plan around the wild.

            if (BonusPopup.Instance != null)
                BonusPopup.Instance.Show("WILD!", WILD_CARD_COLOR, popupWorldPos, 1.4f);
            GameAudio.Instance?.PlayScorePowerup();
        }

        private IEnumerator RewritePulseCoroutine(Tile tile)
        {
            if (tile == null) { _rewritePulseCoroutine = null; yield break; }

            SpriteRenderer sr = tile.GetComponent<SpriteRenderer>();
            float speed = 2.2f;

            while (_rewriteModeActive && tile != null && sr != null)
            {
                float t = (Mathf.Sin(Time.time * speed) + 1f) * 0.5f;
                sr.color = Color.Lerp(REWRITE_LOW, REWRITE_HIGH, t);
                yield return null;
            }

            // Restore normal color when done
            if (sr != null) sr.color = Color.white;
            _rewritePulseCoroutine = null;
        }

        /// <summary>
        /// Executes the rewrite: replaces the board tile at the stored target
        /// with the card from the given hand slot.
        /// </summary>
        private void TryExecuteRewrite(int col, int row, int handSlot)
        {
            if (MatchController.Instance == null || RulesEngine.Instance == null)
            {
                CancelRewriteMode();
                return;
            }

            // Read letter from MatchController's authoritative hand, not local cache
            PlayerHand authHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            char letter = authHand != null ? authHand.GetSlot(handSlot) : _hand[handSlot];

            if (letter == '\0')
            {
//                 Debug.Log("[HandManager] Rewrite: hand slot is empty — cancelling.");
                CancelRewriteMode();
                return;
            }

            // Phase C: wild slots cannot be used as rewrite source. Wilds have no
            // committed letter to stamp onto the target cell, and we don't want to
            // burn a rewrite charge on a no-op. Cancel cleanly instead.
            if (authHand != null && authHand.IsWildSlot(handSlot))
            {
                CancelRewriteMode();
                return;
            }

            // Use MatchController to validate and consume swap charge
            bool success = MatchController.Instance.UseRewrite(handSlot, col, row);
            if (!success)
            {
//                 Debug.Log("[HandManager] Rewrite: MatchController.UseRewrite rejected.");
                CancelRewriteMode();
                return;
            }

            var cell = RulesEngine.Instance.GetCell(col, row);
            char oldLetter = cell != null ? cell.Letter : '?';

            _rewriteMatchRewriteCount++;
//             Debug.Log($"[HandManager] Rewrite ACCEPTED: '{letter}' → ({col},{row}) " +
                      // $"replacing '{oldLetter}' | rewrite #{_rewriteMatchRewriteCount} this match");

            // Stop pulse and clear highlight
            if (_rewritePulseCoroutine != null) { StopCoroutine(_rewritePulseCoroutine); _rewritePulseCoroutine = null; }
            Tile targetTile = _grid.GetTile(col, row);
            if (targetTile != null) { targetTile.Highlight(false); targetTile.ResetVisuals(); }

            _rewriteModeActive = false;
            _rewriteTargetCol = -1;
            _rewriteTargetRow = -1;

            // Disable input and start the turn sequence
            IsInteractable = false;
            _selectedIndex = -1;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            StartCoroutine(RewriteTurnSequence(col, row, letter, handSlot));
        }

        private IEnumerator RewriteTurnSequence(int col, int row, char letter, int handSlot)
        {
            if (JamHint.Instance != null) JamHint.Instance.ClearHint();
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            // Reset the wild-injection gate so stale state from a prior drop can't
            // suppress a rewrite-triggered wild-refill reward (Codex MEDIUM #1).
            _wildInjectedThisResolution = false;

            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            var mc    = MatchController.Instance;

            if (rules == null || grid == null || mc == null)
            {
                IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            int playerIdx = MatchController.PLAYER_HUMAN;

            // Rewrite refund tracking
            bool rewriteScoredWord = false;
            bool rewriteTriggeredPrimed = false;

            // Hide the hand card
            if (handSlot >= 0 && handSlot < HAND_SIZE && _cardObjects[handSlot] != null)
                _cardObjects[handSlot].SetActive(false);

            // Animate the tile edit — shake then letter change then squish
            Tile boardTile = grid.GetTile(col, row);
            if (boardTile != null)
            {
                Vector3 restPos = boardTile.transform.position;
                float cellSize = grid.CellSize;
                float posJitter = cellSize * 0.08f;
                float rotJitter = 10f;

                GameAudio.Instance?.PlayShuffle();

                // Phase 1: shake/jitter
                float shakeDur = 0.2f;
                float elapsed = 0f;
                while (elapsed < shakeDur)
                {
                    elapsed += Time.deltaTime;
                    float ox = Random.Range(-posJitter, posJitter);
                    float oy = Random.Range(-posJitter, posJitter) * 0.5f;
                    boardTile.transform.position = restPos + new Vector3(ox, oy, 0f);
                    float rz = Random.Range(-rotJitter, rotJitter);
                    boardTile.transform.localRotation = Quaternion.Euler(0f, 0f, rz);
                    yield return null;
                }

                // Phase 2: swap the letter visually
                boardTile.SetLetter(letter);
                boardTile.transform.position = restPos;
                boardTile.transform.localRotation = Quaternion.identity;

                // Phase 3: settle pop
                boardTile.PlayLandingSquish();
            }
            else
            {
                grid.CreateSingleTile(col, row, letter);
            }

            // Detonation Replay: snapshot board before rewrite resolution
            if (DetonationRecorder.Instance != null)
                DetonationRecorder.Instance.SnapshotBoard();

            // Run RulesEngine resolution
            var beginResult = rules.BeginRewrite(col, row, letter, playerIdx);
            if (beginResult == null)
            {
                Debug.LogError("[HandManager] RewriteTurnSequence: BeginRewrite returned null.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                IsInteractable = true;
                yield break;
            }

            // Bonus Mode: enter ONLY after BeginRewrite accepts the rewrite.
            // If rejection happened above we yield-break before CompleteDropBookkeeping,
            // which would leave bonus stuck IsActive=true forever.
            if (BonusMode.Instance != null && BonusMode.Instance.Armed)
                BonusMode.Instance.EnterOnDrop();

            // Detonation Replay: record the rewritten tile
            if (DetonationRecorder.Instance != null)
                DetonationRecorder.Instance.RecordRewrite(letter, col, row);

            yield return WaitCache.Get(0.15f);

            // Step-by-step resolution loop (mirrors GameVisualBridge phases)
            bool resolving = true;
            int totalScore = 0;
            int wordIndex = 0;

            // Deferred scoring state for rewrite path (same pattern as FullTurnSequence)
            List<WordScoredEvent> _rwDeferredScoredWords = null;

            while (resolving)
            {
                RulesEngine.StepResult step = rules.NextStep();
                if (step == null) { resolving = false; break; }

                // Detonation Replay: record every step
                if (DetonationRecorder.Instance != null)
                    DetonationRecorder.Instance.RecordStep(step);

                switch (step.Phase)
                {
                    case RulesEngine.ResolutionPhase.WordsDetected:
                    {
                        int wc = step.NewWords != null ? step.NewWords.Count : 0;
//                         Debug.Log($"[Rewrite] WordsDetected: {wc} word(s)");
                        break;
                    }
                    case RulesEngine.ResolutionPhase.WordsScored:
                    {
                        bool rwDetonationComing = (step.ScoredWords != null && step.ScoredWords.Count > 0)
                            && rules.PeekHasTriggers();

                        if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                        {
                            rewriteScoredWord = true;

                            if (rwDetonationComing)
                                _rwDeferredScoredWords = new List<WordScoredEvent>(step.ScoredWords);

                            for (int w = 0; w < step.ScoredWords.Count; w++)
                            {
                                var sw = step.ScoredWords[w];
//                                 Debug.Log($"[Rewrite] Word scored: '{sw.Word}' +{sw.FinalScore}");
                                totalScore += sw.FinalScore;

                                // Survival rewrite meter: rewrites that score words count too
                                if (MatchController.Instance != null)
                                    MatchController.Instance.SurvivalWordScored();

                                // Track first word this turn for LastWordDisplay AND
                                // fire ShowWord NOW so player sees their word immediately
                                // instead of waiting for CompleteDropBookkeeping at end of
                                // turn (the "LIE but display still says SEE +4" lag).
                                // CompleteDropBookkeeping re-fires with turn total later
                                // ONLY if total > already-shown (chain/detonation added more).
                                if (MatchController.Instance != null && string.IsNullOrEmpty(MatchController.Instance.LastTurnWord))
                                {
                                    MatchController.Instance.LastTurnWord = sw.Word;
                                    MatchController.Instance.LastTurnShownScore = sw.FinalScore;
                                    if (LastWordDisplay.Instance != null && sw.PlayerIndex == MatchController.PLAYER_HUMAN)
                                        LastWordDisplay.Instance.ShowWord(sw.Word, sw.FinalScore, true);
                                }

                                // Flash tiles
                                var tiles = new System.Collections.Generic.List<Tile>();
                                if (sw.Cells != null)
                                {
                                    foreach (var c in sw.Cells)
                                    {
                                        Tile t = grid.GetTile(c.x, c.y);
                                        if (t != null) tiles.Add(t);
                                    }
                                }
                                Color hlColor = new Color(0.9f, 0.2f, 0.8f, 1f);
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayWordScored(tiles, hlColor, wordIndex);

                                GameAudio.Instance?.PlayTilePrimed();
                                HapticsManager.Light();

                                if (!rwDetonationComing && BonusPopup.Instance != null && tiles.Count > 0)
                                {
                                    Vector3 wc = Vector3.zero;
                                    for (int st = 0; st < tiles.Count; st++)
                                        if (tiles[st] != null) wc += tiles[st].transform.position;
                                    wc /= Mathf.Max(1, tiles.Count);
                                    BonusPopup.Instance.ShowWordScore(sw.Word, sw.FinalScore, wc);
                                }

                                wordIndex++;
                            }
                        }
                        yield return WaitCache.Get(rwDetonationComing ? 0f : 0.25f);
                        break;
                    }
                    case RulesEngine.ResolutionPhase.TriggersFound:
                    {
                        rewriteTriggeredPrimed = true;
//                         Debug.Log("[Rewrite] Primed word triggered!");

                        // Multi-trigger callout (rewrite path)
                        if (step.Triggers != null && step.Triggers.Count >= 2 && step.ChainDepth == 0)
                        {
                            string multiLabel = step.Triggers.Count == 2 ? "DOUBLE!" : "TRIPLE!";
                            Color multiColor = new Color(1f, 0.6f, 0.15f, 1f);
                            Vector3 multiCenter = Vector3.zero;
                            int multiCount = 0;
                            foreach (var trig in step.Triggers)
                            {
                                if (trig.TriggeredCells == null) continue;
                                foreach (var c in trig.TriggeredCells)
                                {
                                    Tile mt = grid.GetTile(c.x, c.y);
                                    if (mt != null) { multiCenter += mt.transform.position; multiCount++; }
                                }
                            }
                            if (multiCount > 0) multiCenter /= multiCount;
                            if (BonusPopup.Instance != null)
                                BonusPopup.Instance.Show(multiLabel, multiColor, multiCenter + Vector3.up * 0.5f, 1.3f);
                            GameAudio.Instance?.PlayScorePowerup();
//                             Debug.Log($"[Rewrite] Multi-trigger: {multiLabel} ({step.Triggers.Count} primed words)");
                        }

                        // Play detonation trigger visual + sound (same as normal drop path)
                        if (step.Triggers != null && WordDropFX.Instance != null)
                        {
                            foreach (var trig in step.Triggers)
                            {
                                if (trig.TriggeredCells == null) continue;
                                var trigTiles = new System.Collections.Generic.List<Tile>();
                                foreach (var c in trig.TriggeredCells)
                                {
                                    Tile t = grid.GetTile(c.x, c.y);
                                    if (t != null) trigTiles.Add(t);
                                }
                            }
                        }

                        CacheBurstTriggers(step);
                        yield return WaitCache.Get(0.05f); // Micro-anticipation
                        break;
                    }
                    case RulesEngine.ResolutionPhase.Exploding:
                    {
                        if (ChainCounter.Instance != null)
                            ChainCounter.Instance.OnDetonation(step.ChainDepth);

                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        {
                            var dyingTiles = new System.Collections.Generic.List<Tile>();
                            foreach (var c in step.ExplodedCells)
                            {
                                Tile t = grid.GetTile(c.x, c.y);
                                if (t != null) dyingTiles.Add(t);
                            }

                            // Compute explosion center for popups
                            Vector3 rwCenter = Vector3.zero;
                            for (int d = 0; d < dyingTiles.Count; d++)
                                if (dyingTiles[d] != null) rwCenter += dyingTiles[d].transform.position;
                            rwCenter /= Mathf.Max(1, dyingTiles.Count);

                            // Show deferred word+score from the skipped ScoringDisplay
                            if (_rwDeferredScoredWords != null && BonusPopup.Instance != null)
                            {
                                float yOff = 0.5f;
                                foreach (var sw in _rwDeferredScoredWords)
                                {
                                    BonusPopup.Instance.ShowWordScore(sw.Word, sw.FinalScore, rwCenter + Vector3.up * yOff);
                                    yOff += 0.35f;

                                }
                                _rwDeferredScoredWords = null;
                            }

                            // Haptic feedback — strong impact on detonation explosion (rewrite path)
                            HapticsManager.Strong();

                            // Named Meltdown — build-up + stamp BEFORE explosion (rewrite path)
                            bool rwMeltdownActive = false;
                            if (MeltdownManager.Instance != null)
                            {
                                int rwPlayer = MatchController.Instance != null ? MatchController.Instance.CurrentPlayer : 0;
                                bool rwLastTurn = MatchController.Instance != null
                                    && MatchController.Instance.GetPlayerTurns(rwPlayer) >= MatchController.Instance.EffectiveMaxTurns - 1;
                                Coroutine rwMeltdownIntro = MeltdownManager.Instance.TryMeltdownIntro(
                                    step.ChainDepth, step.ChainTriggeredCount, step.DetonationBonus, rwLastTurn);
                                if (rwMeltdownIntro != null)
                                {
                                    yield return rwMeltdownIntro;
                                    rwMeltdownActive = true;
                                }
                            }

                            // Hitstop — only if meltdown didn't already freeze
                            if (!rwMeltdownActive && dyingTiles.Count > 0)
                            {
                                float hitStopDur = step.ChainDepth >= 2 ? 0.08f : 0.05f;
                                yield return StartCoroutine(WordDropFX.HitStop(hitStopDur));
                            }

                            // Flash after hitstop so timeScale=0 doesn't freeze its tween.
                            FirePerWordBurst();
                            FireTileFlashBoxes(dyingTiles);
                            if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                            {
                                int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dyingTiles.Count;
                                yield return WordDropFX.Instance.PlayExplosion(dyingTiles, step.ChainDepth, wLen);
                            }
                            grid.RemoveTiles(step.ExplodedCells);
//                             Debug.Log($"[Rewrite] Exploded {step.ExplodedCells.Count} tiles");

                            // Notify post-clear boost system
                            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                                && step.ExplodedCells != null)
                                SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                            // Meltdown outro — fade stamp after chain played out (rewrite path)
                            if (rwMeltdownActive && MeltdownManager.Instance != null)
                            {
                                Coroutine rwOutro = MeltdownManager.Instance.TryMeltdownOutro();
                                if (rwOutro != null)
                                    yield return rwOutro;
                            }
                        }
                        break;
                    }
                    case RulesEngine.ResolutionPhase.GravityApplied:
                    {
                        yield return StartCoroutine(grid.ApplyGravity());
                        yield return WaitCache.Get(0.1f);
                        break;
                    }
                    case RulesEngine.ResolutionPhase.Complete:
                    {
                        if (ChainCounter.Instance != null)
                            ChainCounter.Instance.OnChainComplete();
                        resolving = false;

                        int finalScore = step.TotalScore;

//                         Debug.Log($"[Rewrite] Resolution complete. Score={finalScore} " +
                                  // $"chainContinues={step.ChainContinues}");

                        rules.FinalizeDrop();

                        // Detonation Replay: finalize chain recording
                        if (DetonationRecorder.Instance != null)
                            DetonationRecorder.Instance.FinalizeChain();

                        try { grid.SyncToRulesState(rules); }
                        catch (System.Exception ex) { Debug.LogError($"[Rewrite] SyncToRulesState: {ex}"); }

                        // Bookkeeping — consumes turn, switches player, refills hand slot.
                        // isRewrite=true so the refill doesn't tick the wild-expiry
                        // drops counter (rewrites shouldn't shorten an untouched wild).
                        mc.CompleteDropBookkeeping(playerIdx, finalScore, handSlot, isRewrite: true);

                        // Rewrite Refund: refund on detonation/chain trigger
                        // Survival: ONLY refund on detonation (not basic word scoring)
                        // Classic: refund on any score or detonation
                        bool shouldRefund = SurvivalManager.IsSurvivalMode
                            ? rewriteTriggeredPrimed
                            : (rewriteScoredWord || rewriteTriggeredPrimed);

                        if (shouldRefund)
                        {
                            mc.RefundRewriteCharge(playerIdx);
//                             Debug.Log($"[RewriteRefund] Rewrite at ({col},{row}) " +
                                      // $"scored={rewriteScoredWord} triggered={rewriteTriggeredPrimed} refund=true");
                            if (BonusPopup.Instance != null && grid != null)
                                BonusPopup.Instance.ShowRefund(grid.CellToWorld(col, row));
                        }
                        else
                        {
//                             Debug.Log($"[RewriteRefund] Rewrite at ({col},{row}) no detonation refund=false");
                        }

                        break;
                    }
                }
            }

            // Apply primed glow to all primed tiles (rewrite path was missing this)
            PrimedWordRegistry rwRegistry = rules.PrimedRegistry;
            int rwCurrentTurn = rules.GlobalTurn;
            if (rwRegistry != null)
            {
                for (int p = 0; p < rwRegistry.Count; p++)
                {
                    var pw = rwRegistry.GetByIndex(p);
                    if (pw == null) continue;
                    int survived = Mathf.Max(0, rwCurrentTurn - pw.PrimedOnTurn);
                    int heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                    bool justPrimed = (pw.PrimedOnTurn == rwCurrentTurn - 1 || pw.PrimedOnTurn == rwCurrentTurn);
                    for (int c = 0; c < pw.Cells.Count; c++)
                    {
                        Tile t = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                        int fuse = Mathf.Max(0, pw.ExpiresOnTurn - rwCurrentTurn);
                        Color glowColor = pw.IsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                        if (t != null) t.SetPrimedGlow(glowColor, playFlash: justPrimed, heatLevel: heatLevel, fuseRemaining: fuse, isGold: pw.IsGold);
                    }
                }
            }

            // Refresh hand and visuals — reactivate all cards (rewrite hides the used card)
            for (int i = 0; i < HAND_SIZE; i++)
                if (_cardObjects[i] != null) _cardObjects[i].SetActive(true);
            RefreshHandFromMatchController();
            RefreshAllCardVisuals();

            // Check if match ended during rewrite resolution
            if (mc == null || !mc.IsMatchActive || mc.IsGameOver)
            {
//                 Debug.Log("[HandManager] RewriteTurnSequence: match ended during rewrite.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // Blitz mode: skip AI, check timer, re-enable immediately
            if (BlitzManager.IsBlitzMode)
            {
                if (BlitzManager.Instance != null && BlitzManager.Instance.CheckBlitzTimeUp())
                {
//                     Debug.Log("[HandManager] RewriteTurnSequence: Blitz time expired.");
                    if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                    yield break;
                }
                IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // Survival mode: no AI, re-enable input immediately
            if (SurvivalManager.IsSurvivalMode)
            {
                IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // AI turn — do NOT re-enable input until AI is done
            yield return WaitCache.Get(0.3f);
            if (mc.IsMatchActive && mc.CurrentPlayer == MatchController.PLAYER_AI)
            {
                if (GameVisualBridge.Instance != null)
                    yield return StartCoroutine(GameVisualBridge.Instance.ExecuteAITurnCoroutine());
            }

            // Check again if match ended after AI turn
            if (mc == null || !mc.IsMatchActive || mc.IsGameOver)
            {
//                 Debug.Log("[HandManager] RewriteTurnSequence: match ended after AI turn.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // If human has no turns left, force game over
            if (mc.IsPlayerDone(MatchController.PLAYER_HUMAN))
            {
//                 Debug.Log("[HandManager] RewriteTurnSequence: human has no turns — forcing game over.");
                IsInteractable = false;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                mc.ForceGameOver();
                yield break;
            }

            IsInteractable = true;
            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
        }

        /// <summary>Animate all cards except the one being dragged to their correct positions.</summary>
        private void UpdateCardPositionsExcept(int exceptIndex)
        {
            StartCoroutine(AnimateCardPositionsExcept(exceptIndex));
        }

        private IEnumerator AnimateCardPositionsExcept(int exceptIndex)
        {
            float elapsed = 0f;
            float duration = 0.1f;
            float baseY = GetCardRowY();

            Vector3[] startPos = new Vector3[HAND_SIZE];
            Vector3[] targetPos = new Vector3[HAND_SIZE];

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null || i == exceptIndex) continue;
                startPos[i] = _cardObjects[i].transform.position;
                // During drag, all non-dragged cards stay at base Y (no raising)
                targetPos[i] = new Vector3(GetCardX(i), baseY, -1f);
            }

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null || i == exceptIndex) continue;
                    _cardObjects[i].transform.position = Vector3.Lerp(startPos[i], targetPos[i], t);
                }
                yield return null;
            }
        }

        // ── Deal animation (Balatro style) ──────────────────────────────────

        private IEnumerator DealCardsAnimation(System.Action onComplete)
        {
            float baseY = GetCardRowY();
            float offScreenX = _grid != null ? _grid.GridRight + _grid.CellSize * 3f : 8f;

            // Hide all cards and shadows off-screen
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = new Vector3(offScreenX, baseY, -1f);
                if (i < HAND_SIZE && _cardShadows[i] != null)
                {
                    _cardShadows[i].color = Color.clear;
                    _cardShadows[i].transform.position = new Vector3(offScreenX, baseY, 0f);
                }
            }

            // Deal all cards with DOTween — staggered slide-in with overshoot
            // Play the drop-in-hand sound once for the whole deal
            GameAudio.Instance?.PlayCardDropHand();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].SetActive(true); // re-activate (was hidden before deal)
                Vector3 startPos = new Vector3(offScreenX, baseY - 0.3f, -1f);
                Vector3 endPos = new Vector3(GetCardX(i), baseY, -1f);
                _cardObjects[i].transform.position = startPos;
                _cardObjects[i].transform.localScale = GetCardBaseScale() * 0.6f; // start small

                float delay = i * 0.05f;
                int cardIdx = i;
                _cardObjects[i].transform.DOMove(endPos, 0.25f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack, 2.5f)
                    .OnComplete(() =>
                    {
                        // Show shadow when card lands
                        if (cardIdx < HAND_SIZE && _cardShadows[cardIdx] != null)
                        {
                            _cardShadows[cardIdx].color = new Color(0f, 0f, 0f, 0.15f);
                            _cardShadows[cardIdx].transform.position = new Vector3(
                                GetCardX(cardIdx), GetCardRowY() - _cardSize * 0.03f, 0f);
                        }
                    });
                _cardObjects[i].transform.DOScale(GetCardBaseScale(), 0.3f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack);
            }

            // Wait for all cards to land
            yield return WaitCache.Get(0.3f + HAND_SIZE * 0.05f + 0.05f);
            onComplete?.Invoke();
        }

        private const float CARD_SELECT_RAISE = 0.2f; // was 0.4 — halved
        private const float CARD_ANIM_SPEED = 12f;  // Lerp speed for smooth movement

        private void SelectCard(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_hand[index] == '\0') return;

            // Tap same card → deselect
            if (_selectedIndex == index)
            {
                GameAudio.Instance?.PlayLightTick();
                // Reset scale and sort order on deselect
                RestoreAllCardSortOrder();
                if (_cardObjects[index] != null)
                    _cardObjects[index].transform.localScale = GetCardBaseScale();
                HideAllCardShadows();
                _selectedIndex = -1;
                RefreshAllCardVisuals();
                UpdateCardPositions();
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
//                 Debug.Log($"[HandManager] Card {index} deselected");
                return;
            }

            // Reset scale on previously selected card
            if (_selectedIndex >= 0 && _selectedIndex < HAND_SIZE && _cardObjects[_selectedIndex] != null)
                _cardObjects[_selectedIndex].transform.localScale = GetCardBaseScale();

            // Tap different card → deselect old, select new
            _selectedIndex = index;
            GameAudio.Instance?.PlayTileSelect();
            RefreshAllCardVisuals();
            UpdateCardPositions();

            // Hide tutorial card highlight when player selects a card
            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
                TutorialManager.Instance.HideCardHighlightPublic();

            // Scale up the newly selected card + show shadow + boost sort order
            RestoreAllCardSortOrder();
            if (_cardObjects[index] != null)
                _cardObjects[index].transform.localScale = GetCardBaseScale() * 1.12f;
            BoostCardSortOrder(index);
            HideAllCardShadows(true); // animate old shadow dropping
            ShowCardShadow(index);

//             Debug.Log($"[HandManager] Card {index} selected: '{_hand[index]}'");

            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);
        }

        /// <summary>Swap two cards in the hand array and their visual objects.</summary>
        private void SwapCardPositions(int a, int b)
        {
            if (a < 0 || a >= HAND_SIZE || b < 0 || b >= HAND_SIZE) return;

            // Swap in local hand array
            char tempChar = _hand[a];
            _hand[a] = _hand[b];
            _hand[b] = tempChar;

            // Swap in MatchController's hand too — use wild-aware swap so the
            // ★ flag travels with its letter (otherwise a moved wild card shows
            // as normal and a moved normal card shows as wild).
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null) hand.SwapSlotsWithFlags(a, b);
            }

            // Swap visual references
            GameObject tempGO = _cardObjects[a];
            _cardObjects[a] = _cardObjects[b];
            _cardObjects[b] = tempGO;

            SpriteRenderer tempSR = _cardSRs[a];
            _cardSRs[a] = _cardSRs[b];
            _cardSRs[b] = tempSR;

            var tempTM = _cardTexts[a];
            _cardTexts[a] = _cardTexts[b];
            _cardTexts[b] = tempTM;

            // Swap point-value TMP refs too (was missing — caused stale point value
            // to persist on the swapped card, e.g. wild card showing "4" from prior letter).
            var tempPtsTM = _cardPtsTexts[a];
            _cardPtsTexts[a] = _cardPtsTexts[b];
            _cardPtsTexts[b] = tempPtsTM;

            // Swap shadows too
            SpriteRenderer tempShadow = _cardShadows[a];
            _cardShadows[a] = _cardShadows[b];
            _cardShadows[b] = tempShadow;

            // Swap halo child references — without this, the halo lights up
            // behind the wrong slot after a drag-reorder (the halo GO is a child
            // of the card, but _cardHalos indexes by slot, so the array index
            // has to travel with the card).
            GameObject tempHalo = _cardHalos[a];
            _cardHalos[a] = _cardHalos[b];
            _cardHalos[b] = tempHalo;

            SpriteRenderer tempHaloSR = _cardHaloSRs[a];
            _cardHaloSRs[a] = _cardHaloSRs[b];
            _cardHaloSRs[b] = tempHaloSR;

//             Debug.Log($"[HandManager] Swapped slots {a}↔{b}: '{_hand[a]}' '{_hand[b]}'");
        }

        /// <summary>Smoothly move cards to their target positions. Selected card pops up.</summary>
        private void UpdateCardPositions()
        {
            StartCoroutine(AnimateCardPositions());
        }

        private IEnumerator AnimateCardPositions()
        {
            float elapsed = 0f;
            float duration = 0.08f;

            // Capture start positions
            Vector3[] startPos = new Vector3[HAND_SIZE];
            Vector3[] targetPos = new Vector3[HAND_SIZE];
            float baseY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                startPos[i] = _cardObjects[i].transform.position;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                targetPos[i] = new Vector3(GetCardX(i), targetY, -1f);
            }

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Smooth ease out
                float eased = 1f - (1f - t) * (1f - t);

                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    _cardObjects[i].transform.position = Vector3.Lerp(startPos[i], targetPos[i], eased);
                }
                yield return null;
            }

            // Snap to final position and reset scale
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), targetY, -1f);
                // Reset scale for non-selected cards
                if (i != _selectedIndex)
                    _cardObjects[i].transform.localScale = GetCardBaseScale();
            }
        }

        // ── Column drop ───────────────────────────────────────────────────────────

        private void TryDropInColumn(Vector3 worldPos)
        {
            if (GameManager.Instance == null ||
                GameManager.Instance.CurrentState != GameState.Playing) return;

            // Check if tap is in grid tap zone
            float tapZoneBottom = _grid.GridBottom;
            float tapZoneTop    = _grid.GridTop + _grid.CellSize;

            if (worldPos.y < tapZoneBottom || worldPos.y > tapZoneTop) return;

            int col = _grid.WorldXToColumn(worldPos.x);
            if (col < 0) return;

            // Tutorial column restriction
            if (TutorialManager.AllowedColumn >= 0 && col != TutorialManager.AllowedColumn)
                return;

            // Tutorial card restriction (defense in depth — also checked in DropSelectedLetterInColumn)
            if (TutorialManager.AllowedCardIndex >= 0 && _selectedIndex != TutorialManager.AllowedCardIndex)
                return;

            // Hide tutorial arrows when player drops
            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
            {
                TutorialManager.Instance.HideArrowsOnDrop();
            }

            DropSelectedLetterInColumn(col);
        }

        /// <summary>
        /// Drops the currently selected letter into the given column.
        /// Routes through GameVisualBridge for visual sequencing.
        /// After drop+AI turn completes, re-enables input.
        /// </summary>
        public void DropSelectedLetterInColumn(int col)
        {
            if (!IsInteractable) return;
            if (_grid == null) return;
            if (MatchController.Instance == null) return;
            if (!MatchController.Instance.IsMatchActive) return;

            // Check if it's the human player's turn
            if (MatchController.Instance.CurrentPlayer != MatchController.PLAYER_HUMAN)
            {
//                 Debug.Log("[HandManager] Not player's turn — ignoring drop.");
                return;
            }

            // Full columns are allowed — dropping replaces the top tile

            // Check if human player still has turns
            if (MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN))
            {
//                 Debug.Log("[HandManager] Human player has no turns remaining.");
                IsInteractable = false;
                // Force game over if match should be done
                if (MatchController.Instance.IsPlayerDone(MatchController.PLAYER_AI) ||
                    MatchController.Instance.TotalTurnsUsed >= MatchController.Instance.TotalMaxTurns)
                {
                    MatchController.Instance.ForceGameOver();
                }
                return;
            }

            char letter = GetSelectedLetter();
            if (letter == '\0')
            {
//                 Debug.Log("[HandManager] No letter selected — ignoring drop.");
                return;
            }

            // Tutorial card restriction — only the highlighted card can be played
            if (TutorialManager.AllowedCardIndex >= 0 && _selectedIndex != TutorialManager.AllowedCardIndex)
                return;

            // Clear preview before committing the drop
            if (DropPreview.Instance != null)
                DropPreview.Instance.ClearPreview();

//             Debug.Log($"[HandManager] Player dropping '{letter}' into column {col} " +
                      // $"(slot {_selectedIndex})");

            // Disable input during animation
            IsInteractable = false;
            RestoreAllCardSortOrder();
            // Clear ALL shadows, then fully hide the dropped card's
            HideAllCardShadows();
            if (_selectedIndex >= 0 && _selectedIndex < HAND_SIZE && _cardShadows[_selectedIndex] != null)
                _cardShadows[_selectedIndex].color = Color.clear;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Deactivate swap mode on drop
            _swapModeActive = false;

            int handSlot = _selectedIndex;

            // Phase C: pull wild flag from authoritative PlayerHand state, then
            // clear it immediately so HasWild becomes false before the resolution
            // runs. Without this, a chain-depth or wild-refill reward earned on the
            // SAME turn the wild is played would be silently dropped (Codex HIGH #2).
            bool dropIsWild = false;
            if (MatchController.Instance != null)
            {
                PlayerHand pHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (pHand != null)
                {
                    dropIsWild = pHand.IsWildSlot(handSlot);
                    // Defensive: if the slot's LETTER is the wild sentinel ('*') but
                    // the wild flag is somehow false (swap/refresh desync), promote
                    // to wild anyway. Never let '*' land as a literal letter on board.
                    if (!dropIsWild && letter == TileBag.WILD_CHAR)
                        dropIsWild = true;
                    if (dropIsWild) pHand.ConsumeWildSlot(handSlot);
                }
            }

            // Start the full turn sequence coroutine
            StartCoroutine(FullTurnSequence(col, letter, handSlot, dropIsWild));
        }

        // ── Full turn sequence (player + AI) ──────────────────────────────────────

        private IEnumerator FullTurnSequence(int col, char letter, int handSlot)
        {
            return FullTurnSequence(col, letter, handSlot, isWild: false);
        }

        private IEnumerator FullTurnSequence(int col, char letter, int handSlot, bool isWild)
        {
            // Mark processing so BlitzManager defers game-over until resolution completes
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            // Phase C: reset per-resolution wild-injection gate. Local `maxChainDepth`
            // (tracked in the step loop below) is used for the chain-depth reward check.
            _wildInjectedThisResolution = false;

            // --- Job 4: Log state BEFORE the drop ---
            int playerIndexBeforeDrop = MatchController.Instance != null
                ? MatchController.Instance.CurrentPlayer : -1;
            bool matchActiveBeforeDrop = MatchController.Instance != null
                ? MatchController.Instance.IsMatchActive : false;

            // Clear jam hint on any player action
            if (JamHint.Instance != null) JamHint.Instance.ClearHint();

            // Phase 11d — a non-edit drop breaks the edit-refund chain. Any cells
            // recorded by a prior rewrite/board-swap no longer qualify for refund
            // once a regular tile-drop has intervened.
            if (MatchController.Instance != null)
                MatchController.Instance.ClearLastEditCells();

//             Debug.Log($"[HandManager] FullTurnSequence BEGIN: " +
                      // $"CurrentPlayer={playerIndexBeforeDrop} " +
                      // $"IsMatchActive={matchActiveBeforeDrop} " +
                      // $"col={col} letter='{letter}' handSlot={handSlot}");

            // 1. Use step-by-step RulesEngine directly from HandManager
            //    (bypasses GameVisualBridge entirely — no timeout issues)
            RulesEngine rules = RulesEngine.Instance;
            GridManager grid = GridManager.Instance;
            int playerIdx = MatchController.PLAYER_HUMAN;

            if (rules == null || grid == null)
            {
                Debug.LogError("[HandManager] Missing RulesEngine or GridManager");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                IsInteractable = true;
                yield break;
            }

            // ── Detonation Replay: snapshot board before resolution ──
            if (DetonationRecorder.Instance != null)
                DetonationRecorder.Instance.SnapshotBoard();

            // ── STEP 1: Animate hand tile flying to column, then drop into grid ──
            RulesEngine.StepResult beginResult = rules.BeginDrop(col, letter, playerIdx, isWild);

            // ── Bonus Mode: enter ONLY if BeginDrop accepted the drop ──
            // If beginResult is null (rejected letter / invalid col) the coroutine
            // yield-breaks below without CompleteDropBookkeeping, which would leave
            // bonus stuck IsActive=true forever. Enter after validation, before the
            // NextStep loop so DoScoreAndPrime still sees IsActive=true.
            if (beginResult != null && beginResult.Row >= 0
                && BonusMode.Instance != null && BonusMode.Instance.Armed)
                BonusMode.Instance.EnterOnDrop();
            if (beginResult == null || beginResult.Row < 0)
            {
                Debug.LogWarning("[HandManager] BeginDrop failed");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                IsInteractable = true;
                yield break;
            }

            int targetRow = beginResult.Row;

            // If we replaced the top tile in a full column, destroy the old tile visually
            if (beginResult.ReplacedTopTile)
            {
                Tile oldTile = grid.GetTile(col, targetRow);
                if (oldTile != null)
                {
                    // Quick dissolve animation on the replaced tile
                    oldTile.Dissolve(0.15f);
                    yield return WaitCache.Get(0.12f);
                    grid.RemoveTiles(new System.Collections.Generic.List<Vector2Int> { new Vector2Int(col, targetRow) });
                }
            }

            // Detonation Replay: record the dropped tile
            if (DetonationRecorder.Instance != null)
                DetonationRecorder.Instance.RecordDrop(letter, col, targetRow);

            // ── Card launch animation: anticipation squash → stretch upward → hide ──
            // If card was dragged near the board, skip the launch anim and just hide it
            GameObject handCard = (handSlot >= 0 && handSlot < HAND_SIZE) ? _cardObjects[handSlot] : null;
            if (handCard != null)
            {
                Transform ct = handCard.transform;
                Vector3 cardScale = GetCardBaseScale();
                float cardY = GetCardRowY();
                bool wasDragged = ct.position.y > cardY + _grid.CellSize * 0.5f;

                ct.DOComplete();

                if (wasDragged)
                {
                    // Card was dragged up — just hide immediately, no squash animation
                    handCard.SetActive(false);
                    ct.localScale = cardScale;
                }
                else
                {
                    // Card was tapped — play the launch animation from the hand row
                    ct.DOScale(new Vector3(cardScale.x * 1.15f, cardScale.y * 0.85f, 1f), 0.06f)
                        .SetEase(DG.Tweening.Ease.InQuad);
                    yield return WaitCache.Get(0.06f);

                    ct.DOScale(new Vector3(cardScale.x * 0.8f, cardScale.y * 1.25f, 1f), 0.08f)
                        .SetEase(DG.Tweening.Ease.OutQuad);
                    ct.DOMove(ct.position + Vector3.up * _grid.CellSize * 0.5f, 0.08f)
                        .SetEase(DG.Tweening.Ease.OutQuad);
                    yield return WaitCache.Get(0.06f);

                    handCard.SetActive(false);
                    ct.localScale = cardScale;
                }
            }

            // Create grid tile at the top of the column and drop it straight down
            Tile droppedTile = grid.CreateSingleTile(col, targetRow, letter, isWild);
            if (droppedTile != null)
            {
                Vector3 targetPos = droppedTile.transform.position;
                float spawnY = grid.GridTop + grid.CellSize * 1.5f;
                droppedTile.transform.position = new Vector3(targetPos.x, spawnY, targetPos.z);

                // Swish sound during fall
                GameAudio.Instance?.PlayTileFall();

                // Enable fake 3D tilt for the drop
                float tiltX = Random.Range(8f, 15f);
                float tiltY = Random.Range(-12f, 12f);
                droppedTile.SetFake3D(tiltX, tiltY);

                float elapsed = 0f;
                float duration = (spawnY - targetPos.y) / 54f;
                while (elapsed < duration && droppedTile != null)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    droppedTile.transform.position = new Vector3(targetPos.x, Mathf.Lerp(spawnY, targetPos.y, t * t), targetPos.z);

                    float fade = 1f - (t * t);
                    droppedTile.SetFake3D(tiltX * fade, tiltY * fade);
                    droppedTile.UpdateFake3DPosition();

                    yield return null;
                }
                if (droppedTile != null)
                {
                    droppedTile.ClearFake3D();
                    droppedTile.transform.position = targetPos;
                    droppedTile.PlayLandingSquish();
                    GameAudio.Instance?.PlayTileDrop();
                }
            }

            // ── STEP 2: Loop NextStep with animations ──
            bool resolving = true;
            int totalScore = 0;
            int baseScoreAccum = 0;
            int chainBonusAccum = 0;
            int detonationBonusAccum = 0;
            int wordIdx = 0;
            int maxChainDepth = 0;

            // Reset scoring display chain counter for this resolution
            if (ScoringDisplay.Instance != null)
                ScoringDisplay.Instance.ResetChain();
            Color playerColor = new Color(0.9f, 0.2f, 0.8f);  // magenta
            Color aiColor = new Color(0.96f, 0.57f, 0.18f);  // warm orange #F5922E

            // Deferred scoring state — when detonation is coming, we skip the
            // full ScoringDisplay and instead show the word+score during the
            // Exploding phase as a BonusPopup rising from the blast zone.
            List<WordScoredEvent> _deferredScoredWords = null;

            while (resolving)
            {
                RulesEngine.StepResult step = rules.NextStep();
                if (step == null) { Debug.LogError("[HandManager] NextStep null"); break; }

                // Track max chain depth for Survival stats
                if (step.ChainDepth > maxChainDepth)
                    maxChainDepth = step.ChainDepth;

                // Detonation Replay: record every step
                if (DetonationRecorder.Instance != null)
                    DetonationRecorder.Instance.RecordStep(step);

                switch (step.Phase)
                {
                    case RulesEngine.ResolutionPhase.WordsDetected:
                        // Just continue to score
                        break;

                    case RulesEngine.ResolutionPhase.WordsScored:
                        if (step.ScoredWords != null)
                        {
                            // Check if a detonation is coming — if so, skip the slow
                            // Balatro-style scoring and show a quick flash instead.
                            bool detonationComing = rules.PeekHasTriggers();

                            if (detonationComing)
                            {
                                // Defer the word+score info for display during Exploding phase
                                _deferredScoredWords = new List<WordScoredEvent>(step.ScoredWords);
                            }

                            foreach (var sw in step.ScoredWords)
                            {
                                // Track first word this turn for LastWordDisplay AND
                                // fire ShowWord NOW so player sees their word immediately
                                // instead of waiting for CompleteDropBookkeeping at end of
                                // turn (the "LIE but display still says SEE +4" lag).
                                // CompleteDropBookkeeping re-fires with turn total later
                                // ONLY if total > already-shown (chain/detonation added more).
                                if (MatchController.Instance != null && string.IsNullOrEmpty(MatchController.Instance.LastTurnWord))
                                {
                                    MatchController.Instance.LastTurnWord = sw.Word;
                                    MatchController.Instance.LastTurnShownScore = sw.FinalScore;
                                    if (LastWordDisplay.Instance != null && sw.PlayerIndex == MatchController.PLAYER_HUMAN)
                                        LastWordDisplay.Instance.ShowWord(sw.Word, sw.FinalScore, true);
                                }

                                // Collect tiles for FX
                                List<Tile> scoredTiles = new List<Tile>();
                                if (sw.Cells != null)
                                    foreach (var cell in sw.Cells)
                                    {
                                        Tile t = grid.GetTile(cell.x, cell.y);
                                        if (t != null) scoredTiles.Add(t);
                                    }

                                // Procedural staggered highlight + scale pop (always plays)
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayWordScored(scoredTiles, playerColor, wordIdx);

                                GameAudio.Instance?.PlayTilePrimed();
                                HapticsManager.Light();

                                if (detonationComing)
                                {
                                    yield return WaitCache.Get(0.15f);
                                }
                                else
                                {
                                    // Normal path — show word+score popup from the tiles on the board
                                    if (BonusPopup.Instance != null && scoredTiles.Count > 0)
                                    {
                                        Vector3 wordCenter = Vector3.zero;
                                        for (int st = 0; st < scoredTiles.Count; st++)
                                            if (scoredTiles[st] != null) wordCenter += scoredTiles[st].transform.position;
                                        wordCenter /= Mathf.Max(1, scoredTiles.Count);
                                        BonusPopup.Instance.ShowWordScore(sw.Word, sw.FinalScore, wordCenter);
                                    }

                                    yield return WaitCache.Get(0.35f);
                                }
                                wordIdx++;
                                totalScore += sw.FinalScore;
                                baseScoreAccum += sw.BaseScore;
                                chainBonusAccum += (sw.FinalScore - sw.BaseScore);

                                // Survival rewrite meter: 2 words → +1 rewrite
                                if (MatchController.Instance != null)
                                    MatchController.Instance.SurvivalWordScored();
                            }
                        }
                        break;

                    case RulesEngine.ResolutionPhase.TriggersFound:
                        // Fuse Trace for player path
                        if (step.Triggers != null && WordDropFX.Instance != null)
                            WordDropFX.Instance.PlayFuseTrace(step.Triggers, grid);

                        // Multi-trigger callout — "DOUBLE DETONATE" / "TRIPLE DETONATE"
                        if (step.Triggers != null && step.Triggers.Count >= 2 && step.ChainDepth == 0)
                        {
                            string multiLabel = step.Triggers.Count == 2 ? "DOUBLE!" : "TRIPLE!";
                            Color multiColor = new Color(1f, 0.6f, 0.15f, 1f); // orange
                            Vector3 multiCenter = Vector3.zero;
                            int multiCount = 0;
                            foreach (var trig in step.Triggers)
                            {
                                if (trig.TriggeredCells == null) continue;
                                foreach (var cell in trig.TriggeredCells)
                                {
                                    Tile mt = grid.GetTile(cell.x, cell.y);
                                    if (mt != null) { multiCenter += mt.transform.position; multiCount++; }
                                }
                            }
                            if (multiCount > 0) multiCenter /= multiCount;
                            if (BonusPopup.Instance != null)
                                BonusPopup.Instance.Show(multiLabel, multiColor, multiCenter + Vector3.up * 0.5f, 1.3f);
                            GameAudio.Instance?.PlayScorePowerup();
//                             Debug.Log($"[HandManager] Multi-trigger: {multiLabel} ({step.Triggers.Count} primed words)");
                        }

                        // Big burst flash — rises during the micro-anticipation below,
                        // peaks as the explosion begins. Helper is shared across all
                        // four resolution paths so every detonation fires.
                        CacheBurstTriggers(step);

                        // Micro-anticipation — just enough for the squeeze to register
                        yield return WaitCache.Get(0.05f);
                        if (step.Triggers != null)
                        {
                            foreach (var trig in step.Triggers)
                            {
                                // Collect triggered tiles for FX
                                List<Tile> trigTiles = new List<Tile>();
                                if (trig.TriggeredCells != null)
                                    foreach (var cell in trig.TriggeredCells)
                                    {
                                        Tile t = grid.GetTile(cell.x, cell.y);
                                        if (t != null) trigTiles.Add(t);
                                    }

                                // Haptic feedback — medium pulse on primed tile trigger
                                HapticsManager.Medium();
                            }
                        }
                        break;

                    case RulesEngine.ResolutionPhase.Exploding:
                        // Track detonation bonus (difference between step total and what we've counted)
                        detonationBonusAccum += step.TotalScore - (baseScoreAccum + chainBonusAccum + detonationBonusAccum);
                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        {
                            List<Tile> dying = new List<Tile>();
                            int stoneDying = 0;
                            foreach (var cell in step.ExplodedCells)
                            {
                                Tile t = grid.GetTile(cell.x, cell.y);
                                if (t != null)
                                {
                                    dying.Add(t);
                                    if (t.IsStone) stoneDying++;
                                }
                            }
                            // if (stoneDying > 0)
//                                 Debug.Log($"[HandManager] Exploding: {stoneDying} stone tile(s) in blast zone");

                            // Chain counter — persistent on-screen combo display
                            if (ChainCounter.Instance != null)
                                ChainCounter.Instance.OnDetonation(step.ChainDepth);

                            // Compute explosion center for popups
                            Vector3 center = Vector3.zero;
                            for (int d = 0; d < dying.Count; d++)
                                if (dying[d] != null) center += dying[d].transform.position;
                            center /= Mathf.Max(1, dying.Count);

                            // Haptic feedback — strong impact on detonation explosion
                            HapticsManager.Strong();

                            // Named Meltdown — build-up + stamp BEFORE explosion
                            bool meltdownActive = false;
                            if (MeltdownManager.Instance != null)
                            {
                                int mPlayer = MatchController.Instance != null ? MatchController.Instance.CurrentPlayer : 0;
                                bool mLastTurn = MatchController.Instance != null
                                    && MatchController.Instance.GetPlayerTurns(mPlayer) >= MatchController.Instance.EffectiveMaxTurns - 1;
                                Coroutine meltdownIntro = MeltdownManager.Instance.TryMeltdownIntro(
                                    step.ChainDepth, step.ChainTriggeredCount, step.DetonationBonus, mLastTurn);
                                if (meltdownIntro != null)
                                {
                                    yield return meltdownIntro;
                                    meltdownActive = true;
                                }
                            }

                            // Hitstop — only if meltdown didn't already freeze
                            if (!meltdownActive && dying.Count > 0)
                            {
                                float hitStopDur = step.ChainDepth >= 2 ? 0.08f : 0.05f;
                                yield return StartCoroutine(WordDropFX.HitStop(hitStopDur));
                            }

                            // Flash fires AFTER hitstop so timeScale=0 doesn't freeze the
                            // tween during the pause. Plays in sync with PlayExplosion
                            // below, matching Candy Crush's "bang + wash" sequence.
                            FirePerWordBurst();
                            FireTileFlashBoxes(dying);

                            // Tiered explosion (handles sound + visuals)
                            if (WordDropFX.Instance != null)
                            {
                                int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dying.Count;
                                yield return WordDropFX.Instance.PlayExplosion(dying, step.ChainDepth, wLen);
                            }

                            grid.RemoveTiles(step.ExplodedCells);

                            // Notify post-clear boost system
                            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                                && step.ExplodedCells != null)
                                SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                            // Score popups AFTER the explosion — rising from the blast zone
                            if (BonusPopup.Instance != null && step.DetonationBonus > 0)
                            {
                                int baseBonus = step.DetonationBonus - step.DetonationHeat;
                                BonusPopup.Instance.ShowDetonation("", baseBonus, center, step.ChainDepth);
                                if (step.DetonationHeat > 0)
                                    BonusPopup.Instance.ShowHeatBonus(step.DetonationHeat, center);
                            }

                            if (_deferredScoredWords != null && BonusPopup.Instance != null)
                            {
                                float yOffset = 0.5f;
                                foreach (var sw in _deferredScoredWords)
                                {
                                    BonusPopup.Instance.ShowWordScore(sw.Word, sw.FinalScore, center + Vector3.up * yOffset);
                                    yOffset += 0.4f;
                                }
                                _deferredScoredWords = null;
                            }

                            yield return WaitCache.Get(0.08f);

                            // Collect refills from detonated special tiles
                            if (SurvivalManager.IsSurvivalMode && MatchController.Instance != null)
                            {
                                float popY = 0.8f;
                                if (step.SwapRefillCount > 0)
                                {
                                    for (int sr = 0; sr < step.SwapRefillCount; sr++)
                                        MatchController.Instance.RefundSwapResource();
                                    if (BonusPopup.Instance != null)
                                        BonusPopup.Instance.Show($"SWAP +{step.SwapRefillCount}", new Color(0.85f, 0.60f, 0.10f, 1f), center + Vector3.up * popY, 1.2f);
                                    GameAudio.Instance?.PlayScorePowerup();
                                    popY += 0.5f;
                                }
                                if (step.EditRefillCount > 0)
                                {
                                    for (int er = 0; er < step.EditRefillCount; er++)
                                        MatchController.Instance.RefundSwapCharge(MatchController.PLAYER_HUMAN);
                                    if (BonusPopup.Instance != null)
                                        BonusPopup.Instance.Show($"EDIT +{step.EditRefillCount}", new Color(0.0f, 0.85f, 0.9f, 1f), center + Vector3.up * popY, 1.2f);
                                    GameAudio.Instance?.PlayScorePowerup();
                                    popY += 0.5f;
                                }
                                // Stone clear popup
                                if (stoneDying > 0 && BonusPopup.Instance != null)
                                {
                                    BonusPopup.Instance.Show($"STONE x{stoneDying}", new Color(0.6f, 0.55f, 0.65f, 1f), center + Vector3.up * popY, 1.1f);
                                    popY += 0.5f;
                                }

                                if (step.WildRefillCount > 0)
                                {
                                    // Phase C: inject one wild into the player's hand via the
                                    // pending-wild queue. The next DrawSlot fills that slot as a
                                    // wild. Gated by per-resolution cap and max-1-in-hand invariant.
                                    TryInjectWildReward(center + Vector3.up * popY);
                                }
                            }

                            // Meltdown outro — fade stamp after chain played out
                            if (meltdownActive && MeltdownManager.Instance != null)
                            {
                                Coroutine meltdownOutro = MeltdownManager.Instance.TryMeltdownOutro();
                                if (meltdownOutro != null)
                                    yield return meltdownOutro;
                            }
                        }
                        break;

                    case RulesEngine.ResolutionPhase.GravityApplied:
                        // Animate gravity fall — tiles move smoothly to final positions
                        yield return StartCoroutine(grid.ApplyGravity());
                        // No RebuildFromRulesEngine here — it destroys/recreates all tiles
                        // causing a visual glitch. Final rebuild happens after FinalizeDrop.
                        yield return WaitCache.Get(0.08f);
                        break;

                    case RulesEngine.ResolutionPhase.Complete:
                        totalScore = step.TotalScore;
                        if (ChainCounter.Instance != null)
                            ChainCounter.Instance.OnChainComplete();
                        resolving = false;
                        break;

                    default:
                        resolving = false;
                        break;
                }
            }

            // Phase C chain-depth reward: a big chain (>= WILD_CHAIN_DEPTH_REQ) earns
            // a wild injection in Survival. Per-resolution cap suppresses duplicate
            // awards if a wild-refill tile also detonated this turn.
            if (SurvivalManager.IsSurvivalMode
                && maxChainDepth >= SurvivalManager.WILD_CHAIN_DEPTH_REQ)
            {
                Vector3 rewardAt = _grid != null
                    ? new Vector3(0f, _grid.GridBottom + _grid.CellSize * 2f, 0f)
                    : Vector3.zero;
                TryInjectWildReward(rewardAt);
            }

            // ── STEP 3: Finalize ──
            rules.FinalizeDrop();

            // Detonation Replay: finalize chain recording
            if (DetonationRecorder.Instance != null)
                DetonationRecorder.Instance.FinalizeChain();

            // Sync without destroying existing tiles — avoids visual pop
            grid.SyncToRulesState(rules);

            // Reset all tile visuals: kill tweens, stop flash coroutines, restore everything
            for (int gc = 0; gc < RulesEngine.COLS; gc++)
                for (int gr = 0; gr < RulesEngine.ROWS; gr++)
                {
                    Tile t = grid.GetTile(gc, gr);
                    if (t == null) continue;
                    // Complete any running tweens then force correct scale
                    t.transform.DOComplete();
                    SpriteRenderer tSR = t.GetComponent<SpriteRenderer>();
                    float ns = (tSR != null && tSR.sprite != null) ? tSR.sprite.bounds.size.x
                        : Mathf.Clamp(Mathf.RoundToInt(grid.CellSize * 200f), 64, 512) / 100f;
                    float correctScale = (grid.CellSize * 0.93f) / ns;
                    t.transform.localScale = new Vector3(correctScale, correctScale, 1f);
                    t.ResetVisuals();
                    t.ClearPrimedGlow();
                }

            PrimedWordRegistry registry = rules.PrimedRegistry;
            int currentTurn = rules.GlobalTurn;
            if (registry != null)
            {
                for (int p = 0; p < registry.Count; p++)
                {
                    var pw = registry.GetByIndex(p);
                    if (pw == null) continue;
                    int survived = Mathf.Max(0, currentTurn - pw.PrimedOnTurn);
                    int heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                    bool justPrimed = (pw.PrimedOnTurn == currentTurn - 1 || pw.PrimedOnTurn == currentTurn);
                    for (int c = 0; c < pw.Cells.Count; c++)
                    {
                        Tile t = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                        int fuse = Mathf.Max(0, pw.ExpiresOnTurn - currentTurn);
                        Color glowColor = pw.IsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                        if (t != null) t.SetPrimedGlow(glowColor, playFlash: justPrimed, heatLevel: heatLevel, fuseRemaining: fuse, isGold: pw.IsGold);
                    }
                }
            }

            // Record chain depth for Survival stats
            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null && maxChainDepth > 0)
                SurvivalManager.Instance.RecordChainDepth(maxChainDepth);

            // ── STEP 4: Bookkeeping (refills hand slot with new letter) ──
            MatchController.Instance.CompleteDropBookkeeping(playerIdx, totalScore, handSlot,
                baseScoreAccum, chainBonusAccum, detonationBonusAccum);

            // Survival/Level: re-enable input IMMEDIATELY — no waiting for primed flash or card deal.
            // But skip the re-enable when LevelController has locked input (the winning drop that
            // just resolved called FireComplete mid-coroutine; modal is up, hand must stay inert).
            // EndProcessing still fires so MatchController isn't stuck in a processing state.
            if ((SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode)
                && MatchController.Instance != null && MatchController.Instance.IsMatchActive
                && !MatchController.Instance.IsGameOver)
            {
                bool levelTerminal = LevelController.Instance != null && LevelController.Instance.IsInputLocked;
                if (!levelTerminal)
                    IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
            }

            // Invalid-drop feedback (Level-mode universal). Drop formed no word
            // AND didn't prime a new one → subtle horizontal shake on the tile
            // plus a soft wood-tick SFX. Non-punitive: tile stays on the board
            // (WordDrop's tile-persistence mechanic), no move refund. Reads as
            // "noted, but nothing scored" so players don't stare at a silent
            // board thinking the game bugged out. Matches Candy Crush / Royal
            // Match invalid-tap convention. Gated on HasPermanentGlow so drops
            // that legitimately PRIMED a word (valid setup play, scoreDelta=0
            // until detonation) don't trigger the shake.
            if (GameManager.IsLevelMode
                && totalScore == 0
                && droppedTile != null
                && droppedTile.transform != null
                && !droppedTile.HasPermanentGlow)
            {
                var gridRef = _grid != null ? _grid : GridManager.Instance;
                float shakeMag = (gridRef != null ? gridRef.CellSize : 1f) * 0.20f;
                droppedTile.transform.DOShakePosition(
                    duration: 0.28f,
                    strength: new Vector3(shakeMag, 0f, 0f),
                    vibrato: 6,
                    randomness: 0f,
                    snapping: false,
                    fadeOut: true);
                GameAudio.Instance?.PlayInvalidDrop();
            }

            // Let the primed flash animation play (non-solo-mode waits here).
            // In Survival/Level, skip the wait — deal card immediately so player can act faster.
            if (!SurvivalManager.IsSurvivalMode && !GameManager.IsLevelMode)
                yield return WaitCache.Get(0.4f);

            if (HUDManager.Instance != null && ScoreManager.Instance != null)
            {
                HUDManager.Instance.SetPlayerScore(ScoreManager.Instance.PlayerScore);
                HUDManager.Instance.SetAIScore(ScoreManager.Instance.AIScore);
            }

            // ── STEP 5: Deal new tile into the empty slot ──
            _selectedIndex = -1; // clear selection so new card appears as normal, not selected
            PlayerHand updatedHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (updatedHand != null)
                SetHand(updatedHand.GetAllSlots());
            RefreshAllCardVisuals();

            if (handSlot >= 0 && handSlot < HAND_SIZE && _cardObjects[handSlot] != null)
            {
                float baseY = GetCardRowY();
                Vector3 cardPos = new Vector3(GetCardX(handSlot), baseY, -1f);

                _cardObjects[handSlot].transform.DOKill();
                _cardObjects[handSlot].SetActive(true);
                _cardObjects[handSlot].transform.position = cardPos;

                // Start slightly small, pop to full size
                Vector3 baseScale = GetCardBaseScale();
                _cardObjects[handSlot].transform.localScale = baseScale * 0.5f;
                _cardObjects[handSlot].transform.DOScale(baseScale, 0.15f)
                    .SetEase(DG.Tweening.Ease.OutBack);

                if (handSlot < HAND_SIZE && _cardShadows[handSlot] != null)
                {
                    _cardShadows[handSlot].color = new Color(0f, 0f, 0f, 0.25f);
                    _cardShadows[handSlot].transform.position = cardPos + new Vector3(0.03f, -0.03f, 0.5f);
                }
                if (_cardObjects[handSlot] != null)
                    _cardObjects[handSlot].transform.position = cardPos;

                // Show shadow now that card has landed
                if (handSlot < HAND_SIZE && _cardShadows[handSlot] != null)
                {
                    _cardShadows[handSlot].color = new Color(0f, 0f, 0f, 0.15f);
                    _cardShadows[handSlot].transform.position = new Vector3(cardPos.x, cardPos.y - _cardSize * 0.03f, 0f);
                }
            }

            if (!SurvivalManager.IsSurvivalMode)
                _selectedIndex = -1; // Deselect after placing (skip in Survival — player may have selected a new card)

            yield return WaitCache.Get(0.5f);

            // --- Job 4: Log state AFTER the wait loop ---
            int playerIndexAfterDrop = MatchController.Instance != null
                ? MatchController.Instance.CurrentPlayer : -1;
            bool matchActiveAfterDrop = MatchController.Instance != null
                ? MatchController.Instance.IsMatchActive : false;
            bool humanDone = MatchController.Instance != null
                ? MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN) : true;
            bool aiDone = MatchController.Instance != null
                ? MatchController.Instance.IsPlayerDone(MatchController.PLAYER_AI) : true;
            int humanTurnsUsed = MatchController.Instance != null
                ? MatchController.Instance.GetPlayerTurns(MatchController.PLAYER_HUMAN) : -1;
            int aiTurnsUsed = MatchController.Instance != null
                ? MatchController.Instance.GetPlayerTurns(MatchController.PLAYER_AI) : -1;

//             Debug.Log($"[HandManager] FullTurnSequence AFTER WAIT: " +
                      // $"CurrentPlayer={playerIndexAfterDrop} " +
                      // $"(was {playerIndexBeforeDrop} before drop) " +
                      // $"IsMatchActive={matchActiveAfterDrop} " +
                      // $"HumanTurns={humanTurnsUsed} AiTurns={aiTurnsUsed} " +
                      // $"HumanDone={humanDone} AiDone={aiDone}");

            // Check if match ended during player's drop
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive)
            {
//                 Debug.Log("[HandManager] FullTurnSequence: Match ended after player drop — skipping AI turn.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // 2. Update hand display after player's turn
            RefreshHandFromMatchController();

            // Blitz mode: check if time expired during resolution, skip AI entirely
            if (BlitzManager.IsBlitzMode)
            {
                if (BlitzManager.Instance != null && BlitzManager.Instance.CheckBlitzTimeUp())
                {
//                     Debug.Log("[HandManager] FullTurnSequence: Blitz time expired after resolution.");
                    if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                    yield break;
                }

                // In blitz, re-enable input immediately — no AI turn
                if (MatchController.Instance != null && MatchController.Instance.IsMatchActive
                    && !MatchController.Instance.IsGameOver)
                {
                    IsInteractable = true;
                    if (ColumnArrowManager.Instance != null)
                        ColumnArrowManager.Instance.ShowArrows(true);
//                     Debug.Log("[HandManager] FullTurnSequence END (blitz): Player input re-enabled.");
                }
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // Survival/Level mode: input was already re-enabled after bookkeeping.
            // Skip the AI-turn check (solo mode) and the FALLBACK warnings (AI turns stuck at 0).
            //
            // Level mode still needs to trigger the turn-based RisingRowManager when the
            // level has "rising_rows" as a hazard — Survival skips this path because it
            // drives its own move-based rising rows via SurvivalManager internally.
            if (SurvivalManager.IsSurvivalMode)
            {
                yield break;
            }
            if (GameManager.IsLevelMode)
            {
                if (RisingRowManager.Instance != null && RisingRowManager.Enabled
                    && MatchController.Instance != null && MatchController.Instance.IsMatchActive)
                {
                    int globalTurn = RulesEngine.Instance != null ? RulesEngine.Instance.GlobalTurn : 0;
                    if (RisingRowManager.Instance.ShouldRiseThisTurn(globalTurn))
                    {
                        bool overflowed = false;
                        yield return StartCoroutine(RisingRowManager.Instance.RiseRow((o) =>
                        {
                            overflowed = o;
                        }));
                        // Two Level-mode fail paths from a rising-row tick:
                        //   (a) overflow=true — shift aborted because the top row was
                        //       already fully filled pre-shift.
                        //   (b) shift succeeded but now no column can accept a drop —
                        //       every column's stack reaches the top (stone tiles make
                        //       this easy to hit because they anchor mid-column).
                        //       Without this, the player's next drop attempt is silently
                        //       rejected by DropLetter and the game just freezes.
                        bool fail = overflowed;
                        if (!fail && RulesEngine.Instance != null)
                        {
                            bool anyOpen = false;
                            for (int c = 0; c < GridManager.COLS; c++)
                            {
                                if (RulesEngine.Instance.IsColumnAvailable(c)) { anyOpen = true; break; }
                            }
                            fail = !anyOpen;
                        }
                        if (fail && LevelController.Instance != null
                            && LevelController.Instance.IsActive)
                        {
                            Debug.Log($"[Level/RisingRow] fail triggered — overflow={overflowed} boardJammed={!overflowed}");
                            LevelController.Instance.ForceFail();
                        }
                    }
                }
                yield break;
            }

            // 3. Determine if AI should take a turn
            bool currentPlayerIsAI   = (playerIndexAfterDrop == MatchController.PLAYER_AI);
            bool aiHasTurnsRemaining = !aiDone;
            bool matchStillActive    = matchActiveAfterDrop;

            bool tutorialActive = (TutorialManager.Instance != null && TutorialManager.Instance.IsActive);
            bool aiShouldAct = currentPlayerIsAI && aiHasTurnsRemaining && matchStillActive && !tutorialActive;

            // --- Job 4: Detailed logging of aiShouldAct determination ---
//             Debug.Log($"[HandManager] FullTurnSequence AI TURN CHECK: " +
                      // $"aiShouldAct={aiShouldAct} " +
                      // $"| currentPlayerIsAI={currentPlayerIsAI} (CurrentPlayer={playerIndexAfterDrop}, PLAYER_AI={MatchController.PLAYER_AI}) " +
                      // $"| aiHasTurnsRemaining={aiHasTurnsRemaining} (AiTurns={aiTurnsUsed}/{MatchController.MAX_TURNS}) " +
                      // $"| matchStillActive={matchStillActive}");

            if (aiShouldAct)
            {
//                 Debug.Log("[HandManager] AI turn should trigger: true — starting AI turn coroutine.");
            }
            else
            {
//                 Debug.Log($"[HandManager] AI turn should trigger: false — reason: " +
                          // $"{(matchStillActive ? "" : "match not active, ")} " +
                          // $"{(currentPlayerIsAI ? "" : $"current player is {playerIndexAfterDrop} not AI, ")} " +
                          // $"{(aiHasTurnsRemaining ? "" : "AI has no turns remaining")}");
            }

            // --- Job 4: Fallback — if aiShouldAct is false but AI still has turns, investigate ---
            if (!aiShouldAct && matchStillActive && !aiDone && !tutorialActive
                && GameVisualBridge.Instance != null && !GameVisualBridge.Instance.IsPlayingBack)
            {
                // Player just dropped: if player switched happened but current player ended up
                // NOT being AI despite AI having turns, something went wrong. Force it.
                // This can happen if the player was already done before this turn pushed them over
                // and the controller skipped to AI but then matched ended for some reason.
                Debug.LogWarning($"[HandManager] FullTurnSequence FALLBACK: " +
                                 $"aiShouldAct was false but AI has {MatchController.MAX_TURNS - aiTurnsUsed} turns remaining " +
                                 $"and match is still active. Checking if AI was skipped...");

                // Recheck: is the AI truly the next player now?
                // Re-read fresh values
                int freshCurrentPlayer = MatchController.Instance.CurrentPlayer;
                bool freshAiDone = MatchController.Instance.IsPlayerDone(MatchController.PLAYER_AI);
                bool freshMatchActive = MatchController.Instance.IsMatchActive;

//                 Debug.Log($"[HandManager] FullTurnSequence FALLBACK recheck: " +
                          // $"freshCurrentPlayer={freshCurrentPlayer} " +
                          // $"freshAiDone={freshAiDone} " +
                          // $"freshMatchActive={freshMatchActive}");

                if (freshCurrentPlayer == MatchController.PLAYER_AI && !freshAiDone && freshMatchActive)
                {
//                     Debug.Log("[HandManager] FullTurnSequence FALLBACK: AI turn detected on recheck — triggering.");
                    aiShouldAct = true;
                }
                else if (!freshAiDone && freshMatchActive)
                {
                    // AI has turns but isn't current player — this means it's still the human's turn
                    // OR something is wrong with player switching. Log it but don't force.
                    Debug.LogWarning($"[HandManager] FullTurnSequence FALLBACK: " +
                                     $"AI has turns remaining but current player is {freshCurrentPlayer}. " +
                                     $"Not forcing AI turn — may be correct if human has more turns.");
                }
            }

            if (aiShouldAct && GameVisualBridge.Instance != null)
            {
//                 Debug.Log("[HandManager] Triggering AI turn...");
                yield return StartCoroutine(GameVisualBridge.Instance.ExecuteAITurnCoroutine());
//                 Debug.Log("[HandManager] AI turn coroutine completed.");
            }

            // 4. Check match state again after AI turn
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive
                || MatchController.Instance.IsGameOver)
            {
//                 Debug.Log("[HandManager] FullTurnSequence: Match ended after AI turn — forcing GameOver transition.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.GameOver);
                yield break;
            }

            // 4b. Rising Row mechanic — after both players have gone
            if (RisingRowManager.Instance != null && RisingRowManager.Enabled
                && !BlitzManager.IsBlitzMode
                && MatchController.Instance != null && MatchController.Instance.IsMatchActive)
            {
                int globalTurn = RulesEngine.Instance != null ? RulesEngine.Instance.GlobalTurn : 0;
                if (RisingRowManager.Instance.ShouldRiseThisTurn(globalTurn))
                {
//                     Debug.Log($"[HandManager] Rising row triggered at globalTurn={globalTurn}");
                    bool overflowed = false;
                    yield return StartCoroutine(RisingRowManager.Instance.RiseRow((overflow) =>
                    {
                        overflowed = overflow;
                    }));

                    if (overflowed)
                    {
                        // Level mode: rising-row overflow ends the attempt via
                        // LevelController.ForceFail → OutOfMovesModal. Classic
                        // "Last Word" and the 1v1 GameOver flow don't apply here.
                        if (GameManager.IsLevelMode && LevelController.Instance != null
                            && LevelController.Instance.IsActive)
                        {
                            Debug.Log("[HandManager] Rising row overflow in Level mode → LevelController.ForceFail.");
                            LevelController.Instance.ForceFail();
                            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                            yield break;
                        }

                        if (MatchController.Instance != null && !MatchController.Instance.IsLastWord)
                        {
//                             Debug.Log("[HandManager] Rising row overflow → triggering LAST WORD phase!");
                            MatchController.Instance.TriggerLastWord();
                            // Continue — let players have their final turns
                        }
                        else
                        {
//                             Debug.Log("[HandManager] Rising row overflow during Last Word — game over.");
                            if (MatchController.Instance != null)
                            {
                                MatchController.Instance.ForceGameOver();
                                MatchController.Instance.EndProcessing();
                            }
                            if (GameManager.Instance != null)
                                GameManager.Instance.TransitionTo(GameState.GameOver);
                            yield break;
                        }
                    }
                }
            }

            // 5. Update hand display again (in case hand changed)
            RefreshHandFromMatchController();

            // 6. Check if match ended after player's turn (human was last)
            if (!MatchController.Instance.IsMatchActive || MatchController.Instance.IsGameOver)
            {
//                 Debug.Log("[HandManager] FullTurnSequence: Match ended (human last turn) — forcing GameOver.");
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.GameOver);
                yield break;
            }

            // 7. Re-enable player input if it's still their turn
            bool humanStillHasTurns = !MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN);
            bool matchIsActive = MatchController.Instance.IsMatchActive;

            if (matchIsActive && humanStillHasTurns)
            {
                IsInteractable = true;
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(true);

                // Refresh next tile preview — may not have been set during hand refresh
                UpdateNextTilePreview();

//                 Debug.Log("[HandManager] FullTurnSequence END: Player input re-enabled.");
            }
            else
            {
//                 Debug.Log($"[HandManager] FullTurnSequence END: No more turns. Forcing GameOver.");
                if (GameManager.Instance != null && !MatchController.Instance.IsGameOver)
                    MatchController.Instance.ForceGameOver();
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.GameOver);
            }

            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
        }

        // ── Hand display refresh ──────────────────────────────────────────────────

        /// <summary>
        /// Pulls the current hand from MatchController and refreshes card visuals.
        /// </summary>
        private void RefreshHandFromMatchController()
        {
            if (MatchController.Instance == null) return;

            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand != null)
            {
                // Ensure next tile cache is populated before refreshing visuals
                if (MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN)
                    hand.EnsureCachedNextLetter(MatchController.Instance.Bag);

                char[] slots = hand.GetAllSlots();
                for (int i = 0; i < HAND_SIZE && i < slots.Length; i++)
                    _hand[i] = slots[i];

                RefreshAllCardVisuals();
//                 Debug.Log($"[HandManager] Hand refreshed: {new string(_hand)}");
            }
        }

        // ── Visual construction ───────────────────────────────────────────────────

        /// <summary>
        /// Creates a rounded-rect tray behind the rack + action area.
        /// Visually groups cards, shuffle, and next into one control cluster.
        /// </summary>
        private void BuildControlTray()
        {
            if (_grid == null) return;

            float cardY = GetCardRowY();
            float trayTop = cardY + _grid.CellSize * 0.45f;
            float trayBottom = (cardY - _grid.CellSize * 1.0f) - _grid.CellSize * 0.18f;
            float trayH = trayTop - trayBottom;
            float trayW = (_grid.GridRight - _grid.GridLeft) + _grid.CellSize * 0.3f;

            if (SurvivalManager.IsSurvivalMode)
            {
                float actionRowY = GetActionRowY();
                trayTop = cardY + _cardSize * 0.60f;
                trayBottom = actionRowY - _cardSize * 0.48f;
                trayH = trayTop - trayBottom;
                trayW = (_grid.GridRight - _grid.GridLeft) + _grid.CellSize * 0.18f;
            }

            int texW = Mathf.Clamp(Mathf.RoundToInt(trayW * 150f), 64, 1024);
            int texH = Mathf.Clamp(Mathf.RoundToInt(trayH * 150f), 64, 1024);
            int radius = Mathf.Min(texW, texH) / 8;

            // Same material family as board — slightly lighter than board outer
            Color trayColor = new Color(0.085f, 0.105f, 0.260f, 0.60f); // darker, desaturated — distinct from purple bg
            Sprite traySprite = TileRenderer.CreateSolidRoundedRect(texW, texH, radius, trayColor);

            _controlTray = new GameObject("ControlTray");
            _controlTray.transform.SetParent(transform, false);
            _controlTray.transform.position = new Vector3(0f, (trayTop + trayBottom) / 2f, 0.5f);

            SpriteRenderer sr = _controlTray.AddComponent<SpriteRenderer>();
            sr.sprite = traySprite;
            sr.sortingOrder = -1; // behind cards

            float nativeW = texW / 100f;
            float nativeH = texH / 100f;
            _controlTray.transform.localScale = new Vector3(trayW / nativeW, trayH / nativeH, 1f);
        }

        private void BuildCardSprites()
        {
            if (_grid == null) return;
            _cardSize = _grid.CellSize * CARD_SIZE_FRACTION;

            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
            int radius  = texSize / 7;
            int border  = Mathf.Max(3, texSize / 16);

            // Try loading hand-drawn sprites
            Sprite loadedNormal   = Resources.Load<Sprite>("Tiles/test_tile");
            Sprite loadedSelected = Resources.Load<Sprite>("Tiles/selected_test@2x");
            Sprite loadedSwap     = Resources.Load<Sprite>("Tiles/swap_tile");

            if (loadedNormal != null)
            {
                _spriteNormal       = loadedNormal;
                _spriteSelected     = loadedSelected ?? loadedNormal;
                _spriteSwap         = loadedSwap ?? loadedNormal;
                _spriteSwapSelected = loadedSwap ?? loadedNormal; // swap+selected uses swap sprite
//                 Debug.Log("[HandManager] Loaded hand-drawn card sprites from Resources/Tiles.");
            }

            // Wild halo — loaded once, reused across all hand slots.
            Texture2D haloTex = Resources.Load<Texture2D>("Particles/wild_halo");
            if (haloTex != null)
            {
                _spriteWildHalo = Sprite.Create(
                    haloTex, new Rect(0, 0, haloTex.width, haloTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
                if (addShader == null) addShader = Shader.Find("Sprites/Default");
                _wildHaloMaterial = new Material(addShader);
            }
            else
            {
                // Fallback to procedural
                _spriteNormal      = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_NORMAL, border);
                _spriteSelected    = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SELECT, border + 2);
                _spriteSwap        = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SWAP, border + 1);
                _spriteSwapSelected= TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SWAP_SEL, border + 2);
//                 Debug.Log("[HandManager] Fallback: procedural card sprites.");
            }
        }

        private void BuildCardObjects()
        {
            if (_grid == null || _spriteNormal == null) return;

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) Destroy(_cardObjects[i]);
                if (_cardHalos[i] != null) Destroy(_cardHalos[i]);
                _cardHalos[i] = null;
                _cardHaloSRs[i] = null;
            }

            float cardY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                float cardX = GetCardX(i);

                GameObject cardGO = new GameObject($"HandCard_{i}");
                cardGO.transform.SetParent(transform, false);
                cardGO.transform.position = new Vector3(cardX, cardY, -1f);

                SpriteRenderer sr = cardGO.AddComponent<SpriteRenderer>();
                sr.sprite       = _spriteNormal;
                sr.sortingOrder = 10;
                // Apply lit material for URP 2D lighting
                Material litMat = Resources.Load<Material>("SpriteLit2D");
                if (litMat != null) sr.sharedMaterial = litMat;

                // Use actual sprite bounds for sizing
                float nativeSize = (sr.sprite != null && sr.sprite.bounds.size.x > 0)
                    ? sr.sprite.bounds.size.x
                    : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
                float scale      = _cardSize / nativeSize;
                cardGO.transform.localScale = new Vector3(scale, scale, 1f);

                float invScale = 1f / Mathf.Max(scale, 0.01f);

                // Letter text — TMP, matches board tiles exactly
                GameObject textGO = new GameObject("CardLetter");
                textGO.transform.SetParent(cardGO.transform, false);
                textGO.transform.localPosition = new Vector3(0f, nativeSize * 0.04f, -0.1f);

                var tm = textGO.AddComponent<TMPro.TextMeshPro>();
                TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
                if (tileFont != null) tm.font = tileFont;
                tm.text          = "?";
                tm.fontSize      = 5.5f;
                tm.fontStyle     = TMPro.FontStyles.Bold;
                tm.color         = CARD_TEXT_COLOR;
                tm.alignment     = TMPro.TextAlignmentOptions.Center;
                tm.sortingOrder  = 11;
                tm.rectTransform.sizeDelta = new Vector2(2f, 2f);
                tm.enableWordWrapping = false;
                tm.overflowMode  = TMPro.TextOverflowModes.Overflow;
                textGO.transform.localScale = new Vector3(invScale, invScale, 1f);

                // Point value — TMP, matches board tiles exactly
                GameObject ptsGO = new GameObject("CardPoints");
                ptsGO.transform.SetParent(cardGO.transform, false);
                ptsGO.transform.localPosition = new Vector3(nativeSize * 0.22f, -nativeSize * 0.22f, -0.1f);

                var ptsTm = ptsGO.AddComponent<TMPro.TextMeshPro>();
                if (tileFont != null) ptsTm.font = tileFont;
                ptsTm.text          = "";
                ptsTm.fontSize      = 2.8f;
                ptsTm.fontStyle     = TMPro.FontStyles.Bold;
                ptsTm.color         = CARD_PTS_COLOR;
                ptsTm.alignment     = TMPro.TextAlignmentOptions.Center;
                ptsTm.sortingOrder  = 11;
                ptsTm.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
                ptsTm.enableWordWrapping = false;
                ptsTm.overflowMode  = TMPro.TextOverflowModes.Overflow;
                ptsGO.transform.localScale = new Vector3(invScale, invScale, 1f);

                MeshRenderer ptsMr = ptsGO.GetComponent<MeshRenderer>();
                if (ptsMr != null) ptsMr.sortingOrder = 11;

                // Hand card shadows disabled — caused too many z-order/timing issues
                _cardShadows[i] = null;

                _cardObjects[i]  = cardGO;
                _cardSRs[i]      = sr;
                _cardTexts[i]    = tm;
                _cardPtsTexts[i] = ptsTm;

                // Wild halo child — sits BEHIND the card (lower sortingOrder), scaled
                // ~1.8× wider than the card so the glow spills past the card edges.
                // Starts disabled; RefreshCardVisual enables it when the slot is wild.
                if (_spriteWildHalo != null)
                {
                    GameObject haloGO = new GameObject("HandCardHalo");
                    haloGO.transform.SetParent(cardGO.transform, false);
                    haloGO.transform.localPosition = new Vector3(0f, 0f, 0.3f); // slightly behind card
                    var haloSR = haloGO.AddComponent<SpriteRenderer>();
                    haloSR.sprite = _spriteWildHalo;
                    if (_wildHaloMaterial != null) haloSR.sharedMaterial = _wildHaloMaterial;
                    haloSR.sortingOrder = 9; // card is 10, halo renders just behind
                    // Halo occupies roughly 1.25× card footprint — tight enough to
                    // read as "this card glows" without dominating the hand row.
                    float haloNativeSize = (haloSR.sprite != null && haloSR.sprite.bounds.size.x > 0)
                        ? haloSR.sprite.bounds.size.x : 1f;
                    float haloScale = (_cardSize * 1.25f) / (haloNativeSize * scale);
                    haloGO.transform.localScale = new Vector3(haloScale, haloScale, 1f);
                    // Animator — rotates + pulses so the halo reads as alive, not a sticker.
                    haloGO.AddComponent<WildHaloAnimator>();
                    haloGO.SetActive(false);
                    _cardHalos[i]   = haloGO;
                    _cardHaloSRs[i] = haloSR;
                }
            }

//             Debug.Log($"[HandManager] Built {HAND_SIZE} card objects at Y={cardY:F2}");
        }

        // ── Card shadow helpers ───────────────────────────────────────────────────

        private Coroutine _shadowAnimCoroutine;

        /// <summary>Boost sorting order on a card so it renders above all others.</summary>
        private void BoostCardSortOrder(int index)
        {
            if (index < 0 || index >= HAND_SIZE || _cardObjects[index] == null) return;
            var sr = _cardObjects[index].GetComponent<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = 20;
            // Boost all child renderers (text, points)
            foreach (var child in _cardObjects[index].GetComponentsInChildren<MeshRenderer>())
                child.sortingOrder = 21;
            foreach (var child in _cardObjects[index].GetComponentsInChildren<TMPro.TextMeshPro>())
                child.sortingOrder = 21;
        }

        /// <summary>Restore sorting order on all cards to default.</summary>
        private void RestoreAllCardSortOrder()
        {
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                var sr = _cardObjects[i].GetComponent<SpriteRenderer>();
                if (sr != null) sr.sortingOrder = 10;
                foreach (var child in _cardObjects[i].GetComponentsInChildren<MeshRenderer>())
                    child.sortingOrder = 11;
                foreach (var child in _cardObjects[i].GetComponentsInChildren<TMPro.TextMeshPro>())
                    child.sortingOrder = 11;
            }
        }

        private void ShowCardShadow(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_cardShadows[index] == null) return;

            float cardX = GetCardX(index);
            float restY = GetCardRowY();

            float centerX = 0f;
            float maxHOffset = _cardSize * 0.1f;
            float hOffset = -Mathf.Sign(cardX - centerX) * Mathf.Clamp01(Mathf.Abs(cardX - centerX) / 3f) * maxHOffset;

            _cardShadows[index].transform.position = new Vector3(cardX + hOffset, restY - _cardSize * 0.03f, 0f);

            // Shadow starts subtle, gets slightly bigger and darker as card lifts
            Vector3 cardScale = GetCardBaseScale();
            float shadowScaleMult = 1.08f; // smaller spread than before
            _cardShadows[index].transform.localScale = cardScale * shadowScaleMult * 0.95f;
            _cardShadows[index].color = new Color(0f, 0f, 0f, 0.15f); // start from rest opacity

            if (_shadowAnimCoroutine != null) StopCoroutine(_shadowAnimCoroutine);
            _shadowAnimCoroutine = StartCoroutine(AnimateShadowLift(
                _cardShadows[index], cardScale * shadowScaleMult));
        }

        private IEnumerator AnimateShadowLift(SpriteRenderer shadow, Vector3 targetScale)
        {
            if (shadow == null) yield break;

            float duration = 0.12f;
            float elapsed = 0f;
            Vector3 startScale = targetScale * 0.92f;
            float targetAlpha = 0.35f; // semi-transparent shadow

            while (elapsed < duration && shadow != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);

                shadow.transform.localScale = Vector3.Lerp(startScale, targetScale, eased);
                shadow.color = new Color(0f, 0f, 0f, Mathf.Lerp(0f, targetAlpha, eased));

                yield return null;
            }

            if (shadow != null)
            {
                shadow.transform.localScale = targetScale;
                shadow.color = new Color(0f, 0f, 0f, targetAlpha);
            }
        }

        private void HideAllCardShadows(bool animate = false)
        {
            if (_shadowAnimCoroutine != null) { StopCoroutine(_shadowAnimCoroutine); _shadowAnimCoroutine = null; }
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardShadows[i] == null) continue;
                if (animate && _cardShadows[i].color.a > 0.01f)
                {
                    // Fire-and-forget drop animation
                    StartCoroutine(AnimateShadowDrop(_cardShadows[i], GetCardBaseScale()));
                }
                else
                {
                    // Only show rest shadow if card is active and visible
                    bool cardActive = _cardObjects[i] != null && _cardObjects[i].activeSelf;
                    if (cardActive)
                    {
                        _cardShadows[i].color = new Color(0f, 0f, 0f, 0.15f);
                        _cardShadows[i].transform.localScale = GetCardBaseScale();
                        _cardShadows[i].transform.position = new Vector3(
                            GetCardX(i), GetCardRowY() - _cardSize * 0.03f, 0f);
                    }
                    else
                    {
                        _cardShadows[i].color = Color.clear;
                    }
                }
            }
        }

        private IEnumerator AnimateShadowDrop(SpriteRenderer shadow, Vector3 fullScale)
        {
            if (shadow == null) yield break;

            float duration = 0.1f;
            float elapsed = 0f;
            Vector3 startScale = shadow.transform.localScale;
            float startAlpha = shadow.color.a;

            while (elapsed < duration && shadow != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t;

                // Scale back up to full + fade to full opacity briefly, then disappear
                shadow.transform.localScale = Vector3.Lerp(startScale, fullScale, eased);
                shadow.color = new Color(0f, 0f, 0.02f, Mathf.Lerp(startAlpha, 0f, eased));

                yield return null;
            }

            if (shadow != null)
            {
                shadow.color = Color.clear;
                shadow.transform.localScale = fullScale;
            }
        }

        /// <summary>
        /// Called every frame — dynamically repositions the selected card's shadow
        /// based on the card's current world position relative to screen center.
        /// </summary>
        private void UpdateSelectedCardShadow()
        {
            if (_selectedIndex < 0 || _selectedIndex >= HAND_SIZE) return;
            if (_cardShadows[_selectedIndex] == null) return;
            if (_cardShadows[_selectedIndex].color.a < 0.01f) return; // not visible

            // Get the card's current world X (may be different from rest if dragging)
            float cardX = _cardObjects[_selectedIndex] != null
                ? _cardObjects[_selectedIndex].transform.position.x
                : GetCardX(_selectedIndex);

            float restY = GetCardRowY();
            float centerX = 0f;
            float maxHOffset = _cardSize * 0.1f;
            float hOffset = -Mathf.Sign(cardX - centerX) * Mathf.Clamp01(Mathf.Abs(cardX - centerX) / 3f) * maxHOffset;

            _cardShadows[_selectedIndex].transform.position = new Vector3(
                cardX + hOffset, restY - _cardSize * 0.05f, 0f);
        }

        // ── Visual update helpers ─────────────────────────────────────────────────

        private void RefreshAllCardVisuals()
        {
            for (int i = 0; i < HAND_SIZE; i++)
                RefreshCardVisual(i);
            // Only reset shadows if not dragging — drag handles its own shadow
            if (!_isDragging)
            {
                HideAllCardShadows();
                if (_selectedIndex >= 0)
                    ShowCardShadow(_selectedIndex);
            }
            UpdateNextTilePreview();
        }

        private void RefreshCardVisual(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_cardSRs[index] == null || _cardTexts[index] == null) return;

            bool isSelected = (index == _selectedIndex);
            char letter     = _hand[index];
            bool isEmpty    = (letter == '\0');

            // Phase C: wild flag lives on PlayerHand. Cheap lookup each refresh.
            bool isWild = false;
            if (MatchController.Instance != null)
            {
                PlayerHand pHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (pHand != null) isWild = pHand.IsWildSlot(index);
            }

            // Toggle the wild halo child on this card — glows out from behind so
            // the wild card reads as "special" even at a glance.
            if (_cardHalos[index] != null)
                _cardHalos[index].SetActive(isWild);

            // Choose sprite based on mode and selection
            if (_swapModeActive)
            {
                _cardSRs[index].sprite = isSelected ? _spriteSwapSelected : _spriteSwap;
            }
            else
            {
                _cardSRs[index].sprite = isSelected ? _spriteSelected : _spriteNormal;
            }

            // Update letter text
            if (isEmpty)
            {
                _cardTexts[index].text  = "";
                _cardTexts[index].color = CARD_TEXT_COLOR;
            }
            else if (isWild)
            {
                // Wild slot: ★ glyph in the wild color. No letter visible until it
                // resolves on the board; hand-side is always "uncommitted".
                _cardTexts[index].text  = WILD_CARD_GLYPH;
                _cardTexts[index].color = WILD_CARD_COLOR;
            }
            else if (_swapModeActive)
            {
                _cardTexts[index].text  = letter.ToString().ToUpper();
                _cardTexts[index].color = CARD_TEXT_SWAP;
            }
            else
            {
                _cardTexts[index].text  = letter.ToString().ToUpper();
                _cardTexts[index].color = CARD_TEXT_COLOR;
            }

            // Update point value text
            if (_cardPtsTexts[index] != null)
            {
                if (isEmpty || letter == '\0')
                {
                    _cardPtsTexts[index].text = "";
                }
                else if (isWild)
                {
                    // Suppress point value on wild cards — it has no fixed letter
                    _cardPtsTexts[index].text = "";
                }
                else
                {
                    int pts = LetterData.GetPoints(letter);
                    _cardPtsTexts[index].text = pts > 0 ? pts.ToString() : "";
                }
            }
        }

        // "?" reads as "blank/unknown tile" (classic Scrabble wild convention) and
        // is in every font atlas — ★ (U+2605) isn't in AvenirNext SDF so it rendered
        // as a missing-glyph square box. Player reads "?" as "any letter" immediately.
        private const string WILD_CARD_GLYPH = "?";
        private static readonly Color WILD_CARD_COLOR = new Color(0.75f, 0.40f, 1.00f, 1f); // purple — matches Tile.WILD_LETTER_COLOR

        // ── Layout helpers ────────────────────────────────────────────────────────

        // ── Shuffle button ───────────────────────────────────────────────────

        private GameObject _shuffleButton;
        private float _shuffleButtonY;
        private float _shuffleButtonX;
        private float _shuffleButtonSize;

        private void BuildShuffleButton()
        {
            if (_grid == null) return;

            float cardY = GetCardRowY();
            _shuffleButtonY = SurvivalManager.IsSurvivalMode
                ? GetActionRowY()
                : cardY - _cardSize * 1.0f;
            _shuffleButtonX = -_cardSize * 1.0f;   // left side
            _shuffleButtonSize = _cardSize * 0.7f;

            _shuffleButton = new GameObject("ShuffleButton");
            _shuffleButton.transform.position = new Vector3(_shuffleButtonX, _shuffleButtonY, -1f);

            // Background rounded rect sprite
            int bgW = 480;
            int bgH = 160;
            int bgRadius = 40;
            Color shuffleBgColor = new Color(0.961f, 0.761f, 0.294f, 1f); // gold #F5C24B
            Sprite bgSprite = TileRenderer.CreateSolidRoundedRect(bgW, bgH, bgRadius, shuffleBgColor);

            SpriteRenderer bgSR = _shuffleButton.AddComponent<SpriteRenderer>();
            bgSR.sprite = bgSprite;
            bgSR.sortingOrder = 11;

            // Scale the bg to appropriate world size
            float bgWorldW = _cardSize * 1.8f; // wider + taller presence
            float bgNativeW = bgW / 100f;
            float bgScale = bgWorldW / bgNativeW;
            _shuffleButton.transform.localScale = new Vector3(bgScale, bgScale, 1f);

            float invBgScale = 1f / Mathf.Max(bgScale, 0.01f);

            // Text label on top of background
            GameObject textGO = new GameObject("ShuffleText");
            textGO.transform.SetParent(_shuffleButton.transform, false);
            textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset btnFont = GameFont.GetUITMP();
            if (btnFont != null) tm.font = btnFont;
            tm.text = "SHUFFLE";
            tm.fontSize = 2.8f;
            tm.fontStyle = TMPro.FontStyles.Bold;
            tm.color = new Color(0.145f, 0.153f, 0.200f, 0.95f);
            tm.alignment = TMPro.TextAlignmentOptions.Center;
            tm.sortingOrder = 12;
            tm.rectTransform.sizeDelta = new Vector2(2f, 0.8f);
            tm.enableWordWrapping = false;
            tm.overflowMode = TMPro.TextOverflowModes.Overflow;
            tm.outlineWidth = 0f;
            // Force clean material — no underlay, no outline, no effects
            if (tm.fontSharedMaterial != null)
            {
                Material mat = new Material(tm.fontSharedMaterial);
                tm.fontMaterial = mat;
                mat.DisableKeyword("UNDERLAY_ON");
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetX, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetY, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayDilate, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlaySoftness, 0f);
                mat.SetColor(TMPro.ShaderUtilities.ID_UnderlayColor, Color.clear);
                mat.SetFloat(TMPro.ShaderUtilities.ID_OutlineWidth, 0f);
            }
            textGO.transform.localScale = new Vector3(invBgScale, invBgScale, 1f);

            MeshRenderer mr = textGO.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 12;
        }

        private SpriteRenderer _shuffleFillSR;
        private Coroutine _shuffleFillCoroutine;
        private bool _shuffleFilling = false;

        private bool TryHandleShuffleButton(Vector3 worldPos)
        {
            if (_shuffleButton == null) return false;
            if (TutorialManager.BlockShuffleAndSwap) return false;
            // No blocking — multiple rapid taps just re-trigger shuffle

            float halfSize = _shuffleButtonSize;
            bool inX = worldPos.x >= _shuffleButtonX - halfSize && worldPos.x <= _shuffleButtonX + halfSize;
            bool inY = worldPos.y >= _shuffleButtonY - halfSize * 0.5f && worldPos.y <= _shuffleButtonY + halfSize * 0.5f;

            if (inX && inY)
            {
                // Flash button and shuffle — SpriteRenderer.color is a TINT on the gold sprite
                SpriteRenderer bgSR = _shuffleButton.GetComponent<SpriteRenderer>();
                if (bgSR != null) bgSR.color = new Color(1.2f, 1.1f, 0.9f, 1f); // bright white-ish tint
                Invoke(nameof(ResetShuffleButtonColor), 0.3f);
                ShuffleHand();
                return true;
            }
            return false;
        }

        private void ResetShuffleButtonColor()
        {
            if (_shuffleFillSR != null) { Destroy(_shuffleFillSR.gameObject); _shuffleFillSR = null; }
            if (_shuffleButton == null) return;
            SpriteRenderer bgSR = _shuffleButton.GetComponent<SpriteRenderer>();
            if (bgSR != null) bgSR.color = Color.white; // white tint = show sprite's natural gold color
            _shuffleFilling = false;
        }

        private void ShuffleHand()
        {
            GameAudio.Instance?.PlayShuffle();
            StartCoroutine(ShuffleHandAnimated());
        }

        private IEnumerator ShuffleHandAnimated()
        {
            if (MatchController.Instance == null) yield break;
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) yield break;

            float baseY = GetCardRowY();

            // Clear shadows and selection
            HideAllCardShadows();
            _selectedIndex = -1;

            // Capture rest positions
            Vector3[] restPositions = new Vector3[HAND_SIZE];
            for (int i = 0; i < HAND_SIZE; i++)
                restPositions[i] = new Vector3(GetCardX(i), baseY, -1f);

            // ── Phase 1: SHAKE in place — jitter each card around its own position ──
            float shakeDur = 0.25f;
            float shakeElapsed = 0f;
            float posJitter = _cardSize * 0.10f;
            float rotJitter = 8f;

            while (shakeElapsed < shakeDur)
            {
                shakeElapsed += Time.deltaTime;

                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    Transform t = _cardObjects[i].transform;

                    float ox = Random.Range(-posJitter, posJitter);
                    float oy = Random.Range(-posJitter, posJitter) * 0.5f;
                    t.position = restPositions[i] + new Vector3(ox, oy, 0f);

                    float rz = Random.Range(-rotJitter, rotJitter);
                    t.localRotation = Quaternion.Euler(0f, 0f, rz);
                }
                yield return null;
            }

            // ── Phase 2: SHUFFLE DATA ──
            // Permute letters AND wild flags together so a shuffled wild lands in
            // the correct new slot instead of stranding its flag at the old index.
            char[] letters = hand.GetAllSlots();
            bool[] wilds   = hand.GetAllWildFlags();
            int activeSize = HAND_SIZE;
            for (int i = activeSize - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                char tempC = letters[i]; letters[i] = letters[j]; letters[j] = tempC;
                bool tempW = wilds[i];   wilds[i]   = wilds[j];   wilds[j]   = tempW;
            }
            hand.ReorderSlotsWithFlags(letters, wilds);

            _hand = (char[])letters.Clone();
            RefreshAllCardVisuals();

            // ── Phase 3: SETTLE — snap back to rest with overshoot ──
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                Transform t = _cardObjects[i].transform;
                t.DOKill();
                t.DOMove(restPositions[i], 0.15f)
                    .SetEase(DG.Tweening.Ease.OutBack, 2f);
                t.DORotate(Vector3.zero, 0.10f)
                    .SetEase(DG.Tweening.Ease.OutQuad);
            }

            yield return WaitCache.Get(0.18f);

            // Snap clean
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = restPositions[i];
                _cardObjects[i].transform.localRotation = Quaternion.identity;
            }

            _selectedIndex = -1;
//             Debug.Log($"[HandManager] Hand shuffled: {new string(letters)}");
        }

        private static Sprite _softShadowSprite;
        private static bool _softShadowBuilt; // force rebuild on code change

        private Sprite GetSoftShadowSprite()
        {
            if (_softShadowBuilt && _softShadowSprite != null) return _softShadowSprite;
            _softShadowBuilt = true;

            // Generate a blurred rounded-rect shadow texture
            int size = 128;
            int padding = 20; // soft edge region
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] px = new Color[size * size];

            float innerRadius = (size * 0.5f) - padding;
            float center = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from center, shaped as rounded rect
                    float dx = Mathf.Abs(x - center);
                    float dy = Mathf.Abs(y - center);
                    float cornerDist = Mathf.Sqrt(
                        Mathf.Max(0, dx - innerRadius * 0.7f) * Mathf.Max(0, dx - innerRadius * 0.7f) +
                        Mathf.Max(0, dy - innerRadius * 0.7f) * Mathf.Max(0, dy - innerRadius * 0.7f));
                    float edgeDist = Mathf.Max(dx, dy);
                    float dist = Mathf.Max(cornerDist, edgeDist - innerRadius);

                    // Smooth falloff
                    float alpha = 1f - Mathf.Clamp01(dist / padding);
                    alpha = alpha * alpha; // quadratic falloff for soft edge
                    alpha *= 1f; // full opacity in texture — runtime controls visibility

                    px[y * size + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            tex.SetPixels(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;

            _softShadowSprite = Sprite.Create(tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);

            return _softShadowSprite;
        }

        private Vector3 GetCardBaseScale()
        {
            // Use actual sprite bounds if hand-drawn sprites are loaded
            float nativeSize;
            if (_spriteNormal != null && _spriteNormal.bounds.size.x > 0)
                nativeSize = _spriteNormal.bounds.size.x;
            else
            {
                int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
                nativeSize = texSize / 100f;
            }
            float scale = _cardSize / nativeSize;
            return new Vector3(scale, scale, 1f);
        }

        private float GetCardRowY()
        {
            if (_grid == null) return -8f;
            if (!SurvivalManager.IsSurvivalMode)
                return _grid.GridBottom - _grid.CellSize * 0.9f;

            float boardGapY = _grid.GridBottom - _grid.CellSize * 0.55f;
            float actionAnchoredY = GetActionRowY() + _cardSize * 1.15f;
            return Mathf.Min(boardGapY, actionAnchoredY);
        }

        private float GetActionRowY()
        {
            if (_grid == null) return -9f;
            if (!SurvivalManager.IsSurvivalMode)
                return GetCardRowY() - _cardSize * 1.0f;

            float halfH = _cam != null ? _cam.orthographicSize : Camera.main.orthographicSize;
            // Account for iPhone safe area (home indicator) + padding
            float safeBottom = 0f;
            if (Screen.safeArea.y > 0)
                safeBottom = (Screen.safeArea.y / Screen.height) * halfH * 2f;
            float bottomInset = _cardSize * 0.50f + safeBottom;
            return -halfH + bottomInset;
        }

        private float GetCardX(int index)
        {
            if (_grid == null) return (index - 2f) * 1.5f;

            float gridWidth = _grid.GridRight - _grid.GridLeft;
            float handWidth = gridWidth * 0.82f; // wider spread, fills more of board width
            float step      = handWidth / HAND_SIZE;
            float startX    = -handWidth / 2f + step * 0.5f;
            return startX + index * step;
        }

        // ── Next tile preview ───────────────────────────────────────────────────

        private void BuildNextTilePreview()
        {
            if (_grid == null) return;

            // Layout: action row sits below hand cards
            // SHUFFLE is on the left, NEXT tile on the right
            // Both centered vertically on the same row
            float actionRowY = SurvivalManager.IsSurvivalMode ? GetActionRowY() : _shuffleButtonY;
            float nextX = _shuffleButtonX + _cardSize * 2.5f;
            float previewSize = _cardSize * 0.65f;

            // No separate floating NEXT label — it goes on the tile itself (see below)

            // -- Socket/holder behind the next tile --
            _nextTileSocket = new GameObject("NextSocket");
            _nextTileSocket.transform.SetParent(transform, false);
            _nextTileSocket.transform.position = new Vector3(nextX, actionRowY, -0.5f);
            SpriteRenderer socketSR = _nextTileSocket.AddComponent<SpriteRenderer>();
            socketSR.sprite = _spriteNormal;
            socketSR.color = new Color(0.06f, 0.08f, 0.20f, 0.50f); // deep inset — matches board family
            socketSR.sortingOrder = 9;
            float socketNative = (_spriteNormal != null && _spriteNormal.bounds.size.x > 0)
                ? _spriteNormal.bounds.size.x
                : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
            float socketScale = (previewSize * 1.15f) / socketNative;
            _nextTileSocket.transform.localScale = new Vector3(socketScale, socketScale, 1f);

            // -- Preview tile --
            _nextTilePreview = new GameObject("NextTilePreview");
            _nextTilePreview.transform.SetParent(transform, false);
            _nextTilePreview.transform.position = new Vector3(nextX, actionRowY, -1f);

            SpriteRenderer sr = _nextTilePreview.AddComponent<SpriteRenderer>();
            sr.sprite = _spriteNormal;
            sr.color = new Color(0.85f, 0.83f, 0.80f, 0.55f); // soft warm, docked not boxed
            sr.sortingOrder = 10;
            _nextTileSR = sr;

            // Scale using actual sprite bounds
            float nativeSize = (sr.sprite != null && sr.sprite.bounds.size.x > 0)
                ? sr.sprite.bounds.size.x
                : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
            float scale = previewSize / nativeSize;
            _nextTilePreview.transform.localScale = new Vector3(scale, scale, 1f);

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // Letter text (child of tile)
            GameObject textGO = new GameObject("NextLetter");
            textGO.transform.SetParent(_nextTilePreview.transform, false);
            textGO.transform.localPosition = new Vector3(0f, nativeSize * 0.04f, -0.2f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) tm.font = tileFont;
            tm.text          = "";
            tm.fontSize      = 5.5f;
            tm.fontStyle     = TMPro.FontStyles.Normal;
            tm.color         = new Color(0.25f, 0.25f, 0.30f, 1f);
            tm.alignment     = TMPro.TextAlignmentOptions.Center;
            tm.sortingOrder  = 15;
            tm.rectTransform.sizeDelta = new Vector2(2f, 2f);
            tm.enableWordWrapping = false;
            tm.overflowMode  = TMPro.TextOverflowModes.Overflow;
            textGO.transform.localScale = new Vector3(invScale, invScale, 1f);
            _nextTileLetter = tm;

            MeshRenderer mr = textGO.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 15;

            // "NEXT" label — to the left of the tile
            float labelX = nextX - previewSize * 0.5f - _cardSize * 0.55f;
            GameObject labelGO = new GameObject("NextLabel");
            labelGO.transform.position = new Vector3(labelX, actionRowY, -1f);

            var labelTmp = labelGO.AddComponent<TMPro.TextMeshPro>();
            var uiFont = GameFont.GetUITMP();
            if (uiFont != null) labelTmp.font = uiFont;
            labelTmp.text = "NEXT";
            labelTmp.fontSize = 2.5f;
            labelTmp.fontStyle = TMPro.FontStyles.Bold;
            labelTmp.color = new Color(0.78f, 0.78f, 0.88f, 0.95f);
            labelTmp.alignment = TMPro.TextAlignmentOptions.Center;
            labelTmp.sortingOrder = 15;
            labelTmp.rectTransform.sizeDelta = new Vector2(2f, 1f);
            labelTmp.enableWordWrapping = false;
            labelTmp.overflowMode = TMPro.TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(labelTmp, labelTmp.color, TMPHelper.TextTier.HUD);
            _nextTileLabel = labelTmp;
        }

        // ── Tile Bag button ─────────────────────────────────────────────────

        private GameObject _tileBagButton;
        private float _tileBagX;
        private float _tileBagY;
        private float _tileBagSize;

        private void BuildTileBagButton()
        {
            if (_grid == null) return;

            float actionRowY = SurvivalManager.IsSurvivalMode ? GetActionRowY() : _shuffleButtonY;
            float nextX = _shuffleButtonX + _cardSize * 2.5f;
            float previewSize = _cardSize * 0.65f;
            _tileBagX = nextX + previewSize * 0.5f + _cardSize * 0.85f;
            _tileBagY = actionRowY;
            _tileBagSize = _cardSize * 0.45f;

            _tileBagButton = new GameObject("TileBagButton");
            _tileBagButton.transform.SetParent(transform, false);
            _tileBagButton.transform.position = new Vector3(_tileBagX, _tileBagY, -1f);

            // Draw a procedural bag silhouette
            int texW = 64, texH = 80;
            Texture2D bagTex = new Texture2D(texW, texH, TextureFormat.ARGB32, false);
            Color clear = Color.clear;
            Color fill = new Color(0.85f, 0.80f, 0.70f, 0.9f); // warm tan to match tiles

            // Clear
            Color[] pixels = new Color[texW * texH];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            // Draw bag body (rounded trapezoid)
            for (int y = 0; y < texH; y++)
            {
                for (int x = 0; x < texW; x++)
                {
                    float nx = (x - texW * 0.5f) / (texW * 0.5f); // -1 to 1
                    float ny = (float)y / texH; // 0 (bottom) to 1 (top)

                    // Bag body: wider at bottom, narrow at top
                    float bodyBottom = 0.0f;
                    float bodyTop = 0.7f;
                    float neckBottom = 0.7f;
                    float neckTop = 0.85f;
                    float knotBottom = 0.82f;
                    float knotTop = 1.0f;

                    bool inside = false;

                    if (ny >= bodyBottom && ny < bodyTop)
                    {
                        // Body: tapers from wide at bottom to narrow at top
                        float t = (ny - bodyBottom) / (bodyTop - bodyBottom);
                        float halfWidth = Mathf.Lerp(0.85f, 0.55f, t);
                        inside = Mathf.Abs(nx) < halfWidth;
                    }
                    else if (ny >= neckBottom && ny < neckTop)
                    {
                        // Neck: narrow
                        inside = Mathf.Abs(nx) < 0.3f;
                    }
                    else if (ny >= knotBottom && ny <= knotTop)
                    {
                        // Knot: wider bump
                        float t = (ny - knotBottom) / (knotTop - knotBottom);
                        float bulge = 0.35f + 0.15f * Mathf.Sin(t * Mathf.PI);
                        inside = Mathf.Abs(nx) < bulge;
                    }

                    if (inside)
                        pixels[y * texW + x] = fill;
                }
            }

            bagTex.SetPixels(pixels);
            bagTex.Apply();
            bagTex.filterMode = FilterMode.Bilinear;

            Sprite bagSprite = Sprite.Create(bagTex,
                new Rect(0, 0, texW, texH),
                new Vector2(0.5f, 0.5f), 100f);

            SpriteRenderer sr = _tileBagButton.AddComponent<SpriteRenderer>();
            sr.sprite = bagSprite;
            sr.sortingOrder = 12;

            // Scale to desired size
            float nativeH = texH / 100f;
            float bagScale = _tileBagSize / nativeH;
            _tileBagButton.transform.localScale = new Vector3(bagScale, bagScale, 1f);

            // No label — icon only
        }

        private bool TryHandleTileBagButton(Vector3 worldPos)
        {
            if (_tileBagButton == null) return false;
            float halfSize = _tileBagSize * 0.6f;
            bool inX = Mathf.Abs(worldPos.x - _tileBagX) < halfSize;
            bool inY = Mathf.Abs(worldPos.y - _tileBagY) < halfSize;
            return inX && inY;
        }

        private void ShowSwapTilePopup()
        {
            DismissSwapTilePopup();
            _swapTileConfirmActive = true;

            float popupX = _tileBagX;
            float popupY = _tileBagY + _tileBagSize * 0.9f;

            // Background
            _swapTilePopup = new GameObject("SwapTilePopup");
            _swapTilePopup.transform.position = new Vector3(popupX, popupY + _cardSize * 0.15f, -2f);

            TextMesh titleTm = _swapTilePopup.AddComponent<TextMesh>();
            titleTm.text = "Swap Tile?";
            titleTm.anchor = TextAnchor.MiddleCenter;
            titleTm.alignment = TextAlignment.Center;
            titleTm.fontSize = 36;
            titleTm.characterSize = 0.06f;
            titleTm.fontStyle = FontStyle.Bold;
            titleTm.color = Color.white;
            GameFont.ApplyUI(titleTm);
            MeshRenderer titleMr = _swapTilePopup.GetComponent<MeshRenderer>();
            if (titleMr != null) titleMr.sortingOrder = 30;

            // Yes
            _swapTileYesLabel = new GameObject("SwapTileYes");
            _swapTileYesLabel.transform.position = new Vector3(popupX - _cardSize * 0.4f, popupY - _cardSize * 0.15f, -2f);
            TextMesh yesTm = _swapTileYesLabel.AddComponent<TextMesh>();
            yesTm.text = "YES";
            yesTm.anchor = TextAnchor.MiddleCenter;
            yesTm.alignment = TextAlignment.Center;
            yesTm.fontSize = 36;
            yesTm.characterSize = 0.06f;
            yesTm.fontStyle = FontStyle.Bold;
            yesTm.color = new Color(0.2f, 0.9f, 0.4f, 1f); // green
            GameFont.ApplyUI(yesTm);
            MeshRenderer yesMr = _swapTileYesLabel.GetComponent<MeshRenderer>();
            if (yesMr != null) yesMr.sortingOrder = 30;

            // No
            _swapTileNoLabel = new GameObject("SwapTileNo");
            _swapTileNoLabel.transform.position = new Vector3(popupX + _cardSize * 0.4f, popupY - _cardSize * 0.15f, -2f);
            TextMesh noTm = _swapTileNoLabel.AddComponent<TextMesh>();
            noTm.text = "NO";
            noTm.anchor = TextAnchor.MiddleCenter;
            noTm.alignment = TextAlignment.Center;
            noTm.fontSize = 36;
            noTm.characterSize = 0.06f;
            noTm.fontStyle = FontStyle.Bold;
            noTm.color = new Color(1f, 0.4f, 0.4f, 1f); // red
            GameFont.ApplyUI(noTm);
            MeshRenderer noMr = _swapTileNoLabel.GetComponent<MeshRenderer>();
            if (noMr != null) noMr.sortingOrder = 30;

//             Debug.Log("[HandManager] Showing Swap Tile? popup");
        }

        private void DismissSwapTilePopup()
        {
            _swapTileConfirmActive = false;
            if (_swapTilePopup != null) { Destroy(_swapTilePopup); _swapTilePopup = null; }
            if (_swapTileYesLabel != null) { Destroy(_swapTileYesLabel); _swapTileYesLabel = null; }
            if (_swapTileNoLabel != null) { Destroy(_swapTileNoLabel); _swapTileNoLabel = null; }
        }

        private bool IsWorldPosNearObject(GameObject obj, Vector3 worldPos, float radius)
        {
            if (obj == null) return false;
            Vector3 p = obj.transform.position;
            return Mathf.Abs(worldPos.x - p.x) < radius && Mathf.Abs(worldPos.y - p.y) < radius;
        }

        private void UpdateNextTilePreview()
        {
            if (_nextTileLetter == null) return;

            if (MatchController.Instance == null || MatchController.Instance.Bag == null)
            {
                _nextTileLetter.text = "";
                return;
            }

            // Only show next tile when it's the human player's turn
            bool isPlayerTurn = MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN;
            if (!isPlayerTurn)
            {
                _nextTileLetter.text = "";
                return;
            }

            // The preview is a hard contract with the player — whatever letter is
            // cached WILL be dealt next. DrawSlot no longer re-validates the cache,
            // so the displayed letter is always truthful.
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) { _nextTileLetter.text = ""; return; }

            char next = hand.CachedNextLetter;
            _nextTileLetter.text = (next != '\0') ? next.ToString() : "";
        }
    }

}
