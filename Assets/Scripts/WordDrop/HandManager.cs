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

        private const int   HAND_SIZE           = PlayerHand.HAND_SIZE; // 5
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

        private char[]           _hand          = new char[HAND_SIZE];
        private int              _selectedIndex = -1;
        private bool             _swapModeActive = false;

        private GameObject[]     _cardObjects   = new GameObject[HAND_SIZE];
        private SpriteRenderer[] _cardSRs       = new SpriteRenderer[HAND_SIZE];
        private TMPro.TextMeshPro[] _cardTexts    = new TMPro.TextMeshPro[HAND_SIZE];
        private TMPro.TextMeshPro[] _cardPtsTexts = new TMPro.TextMeshPro[HAND_SIZE];
        private SpriteRenderer[]   _cardShadows  = new SpriteRenderer[HAND_SIZE];

        private Sprite           _spriteNormal;
        private Sprite           _spriteSelected;
        private Sprite           _spriteSwap;
        private Sprite           _spriteSwapSelected;

        private Camera           _cam;
        private GridManager      _grid;
        private float            _cardSize;

        // ── Next tile preview ────────────────────────────────────────────────────
        private GameObject       _nextTilePreview;
        private SpriteRenderer   _nextTileSR;
        private TMPro.TextMeshPro _nextTileLetter;
        private TextMesh         _nextTileLabel;

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
            Debug.Log("[HandManager] Awake complete — cards + shuffle + next-tile + tile-bag built, IsInteractable=false.");
        }

        private void Start()
        {
            // Subscribe to MatchController events for hand updates
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnHandRefilled += OnHandRefilled;
                Debug.Log("[HandManager] Subscribed to MatchController.OnHandRefilled");
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
            Debug.Log($"[HandManager] Hand updated from OnHandRefilled: {new string(evt.Letters)}");
        }

        // ── Public API ────────────────────────────────────────────────────────────

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

            // Deal cards in one at a time (Balatro style)
            StartCoroutine(DealCardsAnimation(() =>
            {
                IsInteractable = true;
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false); // No arrows until card selected
                Debug.Log($"[HandManager] InitialiseHand complete — hand: {new string(_hand)}");
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
            Debug.Log($"[HandManager] Swap mode: {active}");
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
        private bool _rewriteModeActive = false;
        private int  _rewriteTargetCol = -1;
        private int  _rewriteTargetRow = -1;
        private int  _rewriteMatchRewriteCount = 0; // debug: total rewrites this match
        private static readonly Color REWRITE_HIGHLIGHT_COLOR = new Color(0.3f, 0.95f, 0.9f, 1f); // teal/cyan

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

            // Block ALL input when not interactable
            if (!IsInteractable)
            {
                if (DropPreview.Instance != null)
                    DropPreview.Instance.ClearPreview();
                if (_inputMode != InputMode.Idle)
                    CancelCurrentGesture();
                return;
            }

            // Board long-press for rewrite (runs in Idle only)
            if (_boardHoldCell.x >= 0 && mouseHeld && !_boardHoldTriggered && !_rewriteModeActive
                && _inputMode == InputMode.Idle)
            {
                _boardHoldTimer += Time.deltaTime;
                if (_boardHoldTimer >= LONG_PRESS_TIME)
                {
                    _boardHoldTriggered = true;
                    TryEnterRewriteMode(_boardHoldCell.x, _boardHoldCell.y);
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
                            TryExecuteRewrite(_rewriteTargetCol, _rewriteTargetRow, rewriteTapped);
                        else
                            CancelRewriteMode();
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

                    // Board area tapped — either drop (if card selected) or start rewrite hold
                    if (_selectedIndex >= 0 && worldPos.y >= _grid.GridBottom - _grid.CellSize * 0.5f)
                    {
                        // Tap-to-drop fallback: card already selected, tap board column to drop
                        int tapCol = _grid.WorldXToColumn(worldPos.x);
                        if (tapCol >= 0 && _grid.IsColumnAvailable(tapCol))
                        {
                            DropSelectedLetterInColumn(tapCol);
                            break;
                        }
                    }

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
                        // Quick tap — select/deselect the card
                        SelectCard(_touchCardIndex);
                        _inputMode = InputMode.Idle;
                        _touchCardIndex = -1;
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

                                if (_cardObjects[_touchCardIndex] != null)
                                {
                                    _cardObjects[_touchCardIndex].transform.position = new Vector3(
                                        worldPos.x, worldPos.y, -3f);
                                    _cardObjects[_touchCardIndex].transform.localScale = GetCardBaseScale() * 1.1f;
                                }

                                Debug.Log($"[Input] CarryToBoard: card={_touchCardIndex} letter={_hand[_touchCardIndex]}");
                            }
                            else if (dx > dy * REORDER_LOCK_RATIO)
                            {
                                // Lock into REORDERING
                                _inputMode = InputMode.Reordering;
                                _dragIndex = _touchCardIndex;
                                _isDragging = true;
                                _dragStartX = worldPos.x;

                                Debug.Log($"[Input] Reordering: card={_touchCardIndex}");
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

                                float shadowDrop = _cardSize * 0.12f; // how far below the card
                                _cardShadows[_touchCardIndex].transform.position = new Vector3(
                                    worldPos.x + hOffset, worldPos.y - shadowDrop, 0f);
                                _cardShadows[_touchCardIndex].color = new Color(0f, 0f, 0f, 0.5f);
                                _cardShadows[_touchCardIndex].transform.localScale = GetCardBaseScale() * 1.12f;
                            }
                        }

                        // Update preview based on column under finger
                        char letter = (_touchCardIndex >= 0 && _touchCardIndex < HAND_SIZE)
                            ? _hand[_touchCardIndex] : '\0';
                        int col = _grid.WorldXToColumn(worldPos.x);

                        if (letter != '\0' && col >= 0 && worldPos.y >= _grid.GridBottom - _grid.CellSize)
                        {
                            if (DropPreview.Instance != null)
                                DropPreview.Instance.UpdatePreview(letter, col);

                            if (ColumnArrowManager.Instance != null)
                                ColumnArrowManager.Instance.ShowArrows(true);
                        }
                        else
                        {
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

                        if (dropCol >= 0 && overBoard && _touchCardIndex >= 0
                            && _grid.IsColumnAvailable(dropCol)
                            && MatchController.Instance != null
                            && MatchController.Instance.IsMatchActive
                            && MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN
                            && !MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN))
                        {
                            _selectedIndex = _touchCardIndex;
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
            float baseY = GetCardRowY();
            _cardObjects[index].transform.position = new Vector3(GetCardX(index), baseY, -1f);
            _cardObjects[index].transform.localScale = GetCardBaseScale();
            // Clear the carry shadow
            if (index < HAND_SIZE && _cardShadows[index] != null)
                _cardShadows[index].color = Color.clear;
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
                _cardShadows[_dragIndex].transform.position = new Vector3(worldPos.x + hOffset, baseY - _cardSize * 0.05f, 0f);
                _cardShadows[_dragIndex].color = new Color(0f, 0f, 0f, 0.7f);
                _cardShadows[_dragIndex].transform.localScale = GetCardBaseScale() * 1.15f;
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
                Debug.Log("[HandManager] No swaps remaining.");
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

            Debug.Log($"[HandManager] Swap confirmation triggered for card {cardIndex} ({swapsLeft} swaps left)");
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

        private void ExecuteSwap(int cardIndex)
        {
            if (TutorialManager.BlockShuffleAndSwap) return;
            if (MatchController.Instance == null) return;
            bool success = MatchController.Instance.UseSwap(cardIndex);
            if (success)
            {
                Debug.Log($"[HandManager] Swap executed on card {cardIndex}");
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
            if (MatchController.Instance == null || RulesEngine.Instance == null) return;

            int swapsLeft = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
            if (swapsLeft <= 0)
            {
                Debug.Log("[HandManager] Rewrite: no swaps remaining.");
                return;
            }

            var cell = RulesEngine.Instance.GetCell(col, row);
            if (cell == null) return;

            var primed = RulesEngine.Instance.PrimedRegistry
                .GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primed != null && primed.Count > 0)
            {
                Debug.Log($"[HandManager] Rewrite: tile at ({col},{row}) is primed.");
                return;
            }

            _rewriteModeActive = true;
            _rewriteTargetCol = col;
            _rewriteTargetRow = row;

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

            Debug.Log($"[HandManager] Entered REWRITE mode: target ({col},{row}) " +
                      $"letter='{cell.Letter}' — tap a hand card to replace it");
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
            Debug.Log("[HandManager] Rewrite mode cancelled.");
        }

        private IEnumerator RewritePulseCoroutine(Tile tile)
        {
            while (_rewriteModeActive && tile != null)
            {
                tile.FlashHighlight(REWRITE_HIGHLIGHT_COLOR);
                yield return new WaitForSeconds(0.6f);
            }
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

            char letter = _hand[handSlot];

            // Use MatchController to validate and consume swap charge
            bool success = MatchController.Instance.UseRewrite(handSlot, col, row);
            if (!success)
            {
                Debug.Log("[HandManager] Rewrite: MatchController.UseRewrite rejected.");
                CancelRewriteMode();
                return;
            }

            var cell = RulesEngine.Instance.GetCell(col, row);
            char oldLetter = cell != null ? cell.Letter : '?';

            _rewriteMatchRewriteCount++;
            Debug.Log($"[HandManager] Rewrite ACCEPTED: '{letter}' → ({col},{row}) " +
                      $"replacing '{oldLetter}' | rewrite #{_rewriteMatchRewriteCount} this match");

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
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            var mc    = MatchController.Instance;

            if (rules == null || grid == null || mc == null)
            {
                IsInteractable = true;
                yield break;
            }

            int playerIdx = MatchController.PLAYER_HUMAN;

            // Rewrite refund tracking
            bool rewriteScoredWord = false;
            bool rewriteTriggeredPrimed = false;

            // Hide the hand card
            if (handSlot >= 0 && handSlot < HAND_SIZE && _cardObjects[handSlot] != null)
                _cardObjects[handSlot].SetActive(false);

            // Animate the tile swap — shuffle jitter then letter change
            Tile boardTile = grid.GetTile(col, row);
            if (boardTile != null)
            {
                Vector3 restPos = boardTile.transform.position;
                float cellSize = grid.CellSize;
                float posJitter = cellSize * 0.08f;
                float rotJitter = 10f;

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
                // Fallback — create tile if somehow missing
                grid.CreateSingleTile(col, row, letter);
            }

            // Run RulesEngine resolution
            var beginResult = rules.BeginRewrite(col, row, letter, playerIdx);
            if (beginResult == null)
            {
                Debug.LogError("[HandManager] RewriteTurnSequence: BeginRewrite returned null.");
                yield break;
            }

            yield return new WaitForSeconds(0.15f);

            // Step-by-step resolution loop (mirrors GameVisualBridge phases)
            bool resolving = true;
            int totalScore = 0;
            int wordIndex = 0;

            while (resolving)
            {
                RulesEngine.StepResult step = rules.NextStep();
                if (step == null) { resolving = false; break; }

                switch (step.Phase)
                {
                    case RulesEngine.ResolutionPhase.WordsDetected:
                    {
                        int wc = step.NewWords != null ? step.NewWords.Count : 0;
                        Debug.Log($"[Rewrite] WordsDetected: {wc} word(s)");
                        break;
                    }
                    case RulesEngine.ResolutionPhase.WordsScored:
                    {
                        if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                        {
                            rewriteScoredWord = true;
                            for (int w = 0; w < step.ScoredWords.Count; w++)
                            {
                                var sw = step.ScoredWords[w];
                                Debug.Log($"[Rewrite] Word scored: '{sw.Word}' +{sw.FinalScore}");
                                totalScore += sw.FinalScore;

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
                                Color hlColor = new Color(0.2f, 0.9f, 0.4f, 1f);
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayWordScored(tiles, hlColor, wordIndex);

                                if (ScoringDisplay.Instance != null)
                                    ScoringDisplay.Instance.ShowWordScore(sw.Word, sw.FinalScore, true);

                                wordIndex++;
                            }
                        }
                        yield return new WaitForSeconds(0.25f);
                        break;
                    }
                    case RulesEngine.ResolutionPhase.TriggersFound:
                    {
                        rewriteTriggeredPrimed = true;
                        Debug.Log("[Rewrite] Primed word triggered!");
                        yield return new WaitForSeconds(0.15f);
                        break;
                    }
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
                            if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                yield return WordDropFX.Instance.PlayExplosion(dyingTiles, wordIndex);
                            grid.RemoveTiles(step.ExplodedCells);
                            Debug.Log($"[Rewrite] Exploded {step.ExplodedCells.Count} tiles");
                        }
                        break;
                    }
                    case RulesEngine.ResolutionPhase.GravityApplied:
                    {
                        yield return StartCoroutine(grid.ApplyGravity());
                        yield return new WaitForSeconds(0.1f);
                        break;
                    }
                    case RulesEngine.ResolutionPhase.Complete:
                    {
                        resolving = false;

                        int finalScore = step.TotalScore;

                        Debug.Log($"[Rewrite] Resolution complete. Score={finalScore} " +
                                  $"chainContinues={step.ChainContinues}");

                        rules.FinalizeDrop();

                        try { grid.SyncToRulesState(rules); }
                        catch (System.Exception ex) { Debug.LogError($"[Rewrite] SyncToRulesState: {ex}"); }

                        // Bookkeeping — consumes turn, switches player, refills hand slot
                        mc.CompleteDropBookkeeping(playerIdx, finalScore, handSlot);

                        // Rewrite Refund: if the rewrite scored or triggered, refund 1 swap charge
                        if (rewriteScoredWord || rewriteTriggeredPrimed)
                        {
                            mc.RefundSwapCharge(playerIdx);
                            Debug.Log($"[RewriteRefund] Rewrite at ({col},{row}) " +
                                      $"scored={rewriteScoredWord} triggered={rewriteTriggeredPrimed} refund=true");
                        }
                        else
                        {
                            Debug.Log($"[RewriteRefund] Rewrite at ({col},{row}) no score/no detonation refund=false");
                        }

                        break;
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
                Debug.Log("[HandManager] RewriteTurnSequence: match ended during rewrite.");
                yield break;
            }

            // AI turn — do NOT re-enable input until AI is done
            yield return new WaitForSeconds(0.3f);
            if (mc.IsMatchActive && mc.CurrentPlayer == MatchController.PLAYER_AI)
            {
                if (GameVisualBridge.Instance != null)
                    yield return StartCoroutine(GameVisualBridge.Instance.ExecuteAITurnCoroutine());
            }

            // Check again if match ended after AI turn
            if (mc == null || !mc.IsMatchActive || mc.IsGameOver)
            {
                Debug.Log("[HandManager] RewriteTurnSequence: match ended after AI turn.");
                yield break;
            }

            // If human has no turns left, force game over
            if (mc.IsPlayerDone(MatchController.PLAYER_HUMAN))
            {
                Debug.Log("[HandManager] RewriteTurnSequence: human has no turns — forcing game over.");
                IsInteractable = false;
                mc.ForceGameOver();
                yield break;
            }

            IsInteractable = true;
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

            // Hide all cards off-screen to the right
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = new Vector3(offScreenX, baseY, -1f);
            }

            // Deal all cards with DOTween — staggered slide-in with overshoot
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                Vector3 startPos = new Vector3(offScreenX, baseY - 0.3f, -1f);
                Vector3 endPos = new Vector3(GetCardX(i), baseY, -1f);
                _cardObjects[i].transform.position = startPos;
                _cardObjects[i].transform.localScale = GetCardBaseScale() * 0.6f; // start small

                float delay = i * 0.05f; // slightly more stagger for readability
                // Position overshoots past target then settles back
                _cardObjects[i].transform.DOMove(endPos, 0.25f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack, 2.5f);
                // Scale pops up with elastic bounce
                _cardObjects[i].transform.DOScale(GetCardBaseScale(), 0.3f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutElastic, 0.6f, 0.3f);
            }

            // Wait for all cards to land
            yield return new WaitForSeconds(0.3f + HAND_SIZE * 0.05f + 0.05f);
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
                // Reset scale on deselect
                if (_cardObjects[index] != null)
                    _cardObjects[index].transform.localScale = GetCardBaseScale();
                HideAllCardShadows();
                _selectedIndex = -1;
                RefreshAllCardVisuals();
                UpdateCardPositions();
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
                Debug.Log($"[HandManager] Card {index} deselected");
                return;
            }

            // Reset scale on previously selected card
            if (_selectedIndex >= 0 && _selectedIndex < HAND_SIZE && _cardObjects[_selectedIndex] != null)
                _cardObjects[_selectedIndex].transform.localScale = GetCardBaseScale();

            // Tap different card → deselect old, select new
            _selectedIndex = index;
            RefreshAllCardVisuals();
            UpdateCardPositions();

            // Hide tutorial card highlight when player selects a card
            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
                TutorialManager.Instance.HideCardHighlightPublic();

            // Scale up the newly selected card + show shadow
            if (_cardObjects[index] != null)
                _cardObjects[index].transform.localScale = GetCardBaseScale() * 1.08f;
            HideAllCardShadows(true); // animate old shadow dropping
            ShowCardShadow(index);

            Debug.Log($"[HandManager] Card {index} selected: '{_hand[index]}'");

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

            // Swap in MatchController's hand too
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                {
                    char ca = hand.GetSlot(a);
                    char cb = hand.GetSlot(b);
                    hand.SetSlot(a, cb);
                    hand.SetSlot(b, ca);
                }
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

            Debug.Log($"[HandManager] Swapped slots {a}↔{b}: '{_hand[a]}' '{_hand[b]}'");
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

            // Snap to final
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), targetY, -1f);
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
                Debug.Log("[HandManager] Not player's turn — ignoring drop.");
                return;
            }

            if (!_grid.IsColumnAvailable(col))
            {
                Debug.Log($"[HandManager] Column {col} is full — ignoring.");
                return;
            }

            // Check if human player still has turns
            if (MatchController.Instance.IsPlayerDone(MatchController.PLAYER_HUMAN))
            {
                Debug.Log("[HandManager] Human player has no turns remaining.");
                IsInteractable = false;
                // Force game over if match should be done
                if (MatchController.Instance.IsPlayerDone(MatchController.PLAYER_AI) ||
                    MatchController.Instance.TotalTurnsUsed >= MatchController.MAX_TURNS * 2)
                {
                    MatchController.Instance.ForceGameOver();
                }
                return;
            }

            char letter = GetSelectedLetter();
            if (letter == '\0')
            {
                Debug.Log("[HandManager] No letter selected — ignoring drop.");
                return;
            }

            // Clear preview before committing the drop
            if (DropPreview.Instance != null)
                DropPreview.Instance.ClearPreview();

            Debug.Log($"[HandManager] Player dropping '{letter}' into column {col} " +
                      $"(slot {_selectedIndex})");

            // Disable input during animation
            IsInteractable = false;
            HideAllCardShadows();
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Deactivate swap mode on drop
            _swapModeActive = false;

            int handSlot = _selectedIndex;

            // Start the full turn sequence coroutine
            StartCoroutine(FullTurnSequence(col, letter, handSlot));
        }

        // ── Full turn sequence (player + AI) ──────────────────────────────────────

        private IEnumerator FullTurnSequence(int col, char letter, int handSlot)
        {
            // --- Job 4: Log state BEFORE the drop ---
            int playerIndexBeforeDrop = MatchController.Instance != null
                ? MatchController.Instance.CurrentPlayer : -1;
            bool matchActiveBeforeDrop = MatchController.Instance != null
                ? MatchController.Instance.IsMatchActive : false;

            Debug.Log($"[HandManager] FullTurnSequence BEGIN: " +
                      $"CurrentPlayer={playerIndexBeforeDrop} " +
                      $"IsMatchActive={matchActiveBeforeDrop} " +
                      $"col={col} letter='{letter}' handSlot={handSlot}");

            // 1. Use step-by-step RulesEngine directly from HandManager
            //    (bypasses GameVisualBridge entirely — no timeout issues)
            RulesEngine rules = RulesEngine.Instance;
            GridManager grid = GridManager.Instance;
            int playerIdx = MatchController.PLAYER_HUMAN;

            if (rules == null || grid == null)
            {
                Debug.LogError("[HandManager] Missing RulesEngine or GridManager");
                IsInteractable = true;
                yield break;
            }

            // ── STEP 1: Animate hand tile flying to column, then drop into grid ──
            RulesEngine.StepResult beginResult = rules.BeginDrop(col, letter, playerIdx);
            if (beginResult == null || beginResult.Row < 0)
            {
                Debug.LogWarning("[HandManager] BeginDrop failed");
                IsInteractable = true;
                yield break;
            }

            int targetRow = beginResult.Row;

            // Hide the hand card immediately — it's been placed
            GameObject handCard = (handSlot >= 0 && handSlot < HAND_SIZE) ? _cardObjects[handSlot] : null;
            if (handCard != null)
                handCard.SetActive(false);

            // Create grid tile at the top of the column and drop it straight down
            Tile droppedTile = grid.CreateSingleTile(col, targetRow, letter);
            if (droppedTile != null)
            {
                Vector3 targetPos = droppedTile.transform.position;
                float spawnY = grid.GridTop + grid.CellSize * 1.5f;
                droppedTile.transform.position = new Vector3(targetPos.x, spawnY, targetPos.z);

                // Enable fake 3D tilt for the drop
                float tiltX = Random.Range(8f, 15f);
                float tiltY = Random.Range(-12f, 12f);
                droppedTile.SetFake3D(tiltX, tiltY);

                float elapsed = 0f;
                float duration = (spawnY - targetPos.y) / 38f; // was 45
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
                }
            }

            // ── STEP 2: Loop NextStep with animations ──
            bool resolving = true;
            int totalScore = 0;
            int baseScoreAccum = 0;
            int chainBonusAccum = 0;
            int detonationBonusAccum = 0;
            int wordIdx = 0;

            // Reset scoring display chain counter for this resolution
            if (ScoringDisplay.Instance != null)
                ScoringDisplay.Instance.ResetChain();
            Color playerColor = new Color(0.29f, 0.87f, 0.31f);  // bright lime green #4ADE50
            Color aiColor = new Color(0.96f, 0.57f, 0.18f);  // warm orange #F5922E

            while (resolving)
            {
                RulesEngine.StepResult step = rules.NextStep();
                if (step == null) { Debug.LogError("[HandManager] NextStep null"); break; }

                switch (step.Phase)
                {
                    case RulesEngine.ResolutionPhase.WordsDetected:
                        // Just continue to score
                        break;

                    case RulesEngine.ResolutionPhase.WordsScored:
                        if (step.ScoredWords != null)
                        {
                            foreach (var sw in step.ScoredWords)
                            {
                                // Collect tiles for FX
                                List<Tile> scoredTiles = new List<Tile>();
                                if (sw.Cells != null)
                                    foreach (var cell in sw.Cells)
                                    {
                                        Tile t = grid.GetTile(cell.x, cell.y);
                                        if (t != null) scoredTiles.Add(t);
                                    }

                                // Procedural staggered highlight + scale pop
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayWordScored(scoredTiles, playerColor, wordIdx);

                                // Balatro-style scoring display
                                if (ScoringDisplay.Instance != null)
                                    ScoringDisplay.Instance.ShowWordScore(sw.Word, sw.FinalScore, true);

                                // Beat timing — full countdown for first word, quick for chains
                                float scoringDur = (wordIdx == 0)
                                    ? ScoringDisplay.GetDuration(sw.Word.Length)
                                    : ScoringDisplay.GetQuickDuration();
                                float beat = Mathf.Max(
                                    WordDropFX.GetBeatDuration(0.40f, wordIdx),
                                    scoringDur);
                                yield return new WaitForSeconds(beat);
                                wordIdx++;
                                totalScore += sw.FinalScore;
                                baseScoreAccum += sw.BaseScore;
                                chainBonusAccum += (sw.FinalScore - sw.BaseScore);
                            }
                        }
                        break;

                    case RulesEngine.ResolutionPhase.TriggersFound:
                        // Fuse Trace for player path
                        if (step.Triggers != null && WordDropFX.Instance != null)
                            WordDropFX.Instance.PlayFuseTrace(step.Triggers, grid);
                        yield return new WaitForSeconds(0.15f);
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

                                // Procedural detonation sequence (squeeze → flash → pop → shake)
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayDetonation(trigTiles, wordIdx);

                                if (ScoringDisplay.Instance != null)
                                    ScoringDisplay.Instance.ShowWordScore(
                                        trig.TriggeredWord, RulesEngine.BREAKER_BONUS, true);

                                yield return new WaitForSeconds(WordDropFX.DETONATE_TOTAL_DUR);
                            }
                        }
                        break;

                    case RulesEngine.ResolutionPhase.Exploding:
                        // Track detonation bonus (difference between step total and what we've counted)
                        detonationBonusAccum += step.TotalScore - (baseScoreAccum + chainBonusAccum + detonationBonusAccum);
                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        {
                            List<Tile> dying = new List<Tile>();
                            foreach (var cell in step.ExplodedCells)
                            {
                                Tile t = grid.GetTile(cell.x, cell.y);
                                if (t != null) dying.Add(t);
                            }

                            // Procedural staggered explosion with escalating shake
                            if (WordDropFX.Instance != null)
                                yield return WordDropFX.Instance.PlayExplosion(dying, wordIdx);

                            grid.RemoveTiles(step.ExplodedCells);
                            yield return new WaitForSeconds(0.08f);
                        }
                        break;

                    case RulesEngine.ResolutionPhase.GravityApplied:
                        // Animate gravity fall — tiles move smoothly to final positions
                        yield return StartCoroutine(grid.ApplyGravity());
                        // No RebuildFromRulesEngine here — it destroys/recreates all tiles
                        // causing a visual glitch. Final rebuild happens after FinalizeDrop.
                        yield return new WaitForSeconds(0.08f);
                        break;

                    case RulesEngine.ResolutionPhase.Complete:
                        totalScore = step.TotalScore;
                        resolving = false;
                        break;

                    default:
                        resolving = false;
                        break;
                }
            }

            // ── STEP 3: Finalize ──
            rules.FinalizeDrop();
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
                    int ts = Mathf.Clamp(Mathf.RoundToInt(grid.CellSize * 200f), 64, 512);
                    float ns = ts / 100f;
                    float correctScale = (grid.CellSize * 0.88f) / ns;
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
                        if (t != null) t.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: justPrimed, heatLevel: heatLevel, fuseRemaining: fuse);
                    }
                }
            }

            // Let the primed flash animation play before anything else happens
            yield return new WaitForSeconds(0.4f);

            // ── STEP 4: Bookkeeping (refills hand slot with new letter) ──
            MatchController.Instance.CompleteDropBookkeeping(playerIdx, totalScore, handSlot,
                baseScoreAccum, chainBonusAccum, detonationBonusAccum);

            if (HUDManager.Instance != null && ScoreManager.Instance != null)
            {
                HUDManager.Instance.SetPlayerScore(ScoreManager.Instance.PlayerScore);
                HUDManager.Instance.SetAIScore(ScoreManager.Instance.AIScore);
            }

            // ── STEP 5: Animate new tile dealing into the empty slot ──
            // Refresh hand data from MatchController
            PlayerHand updatedHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (updatedHand != null)
                SetHand(updatedHand.GetAllSlots());
            RefreshAllCardVisuals();

            // Re-activate the hand card object (it was hidden when placed)
            if (handSlot >= 0 && handSlot < HAND_SIZE && _cardObjects[handSlot] != null)
            {
                float baseY = GetCardRowY();
                float offScreenX = grid.GridRight + grid.CellSize * 3f;

                _cardObjects[handSlot].SetActive(true);
                _cardObjects[handSlot].transform.position = new Vector3(offScreenX, baseY - 0.2f, -1f);

                // Slide in from right with bounce
                Vector3 from = _cardObjects[handSlot].transform.position;
                Vector3 to = new Vector3(GetCardX(handSlot), baseY, -1f);
                float dealElapsed = 0f;
                float dealDuration = 0.2f;

                while (dealElapsed < dealDuration)
                {
                    dealElapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(dealElapsed / dealDuration);
                    float overshoot = 1.2f;
                    float eased = 1f + (overshoot + 1f) * Mathf.Pow(t - 1f, 3f) + overshoot * Mathf.Pow(t - 1f, 2f);
                    if (_cardObjects[handSlot] != null)
                        _cardObjects[handSlot].transform.position = Vector3.LerpUnclamped(from, to, eased);
                    yield return null;
                }
                if (_cardObjects[handSlot] != null)
                    _cardObjects[handSlot].transform.position = to;
            }

            _selectedIndex = -1; // Deselect after placing

            yield return new WaitForSeconds(0.5f);

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

            Debug.Log($"[HandManager] FullTurnSequence AFTER WAIT: " +
                      $"CurrentPlayer={playerIndexAfterDrop} " +
                      $"(was {playerIndexBeforeDrop} before drop) " +
                      $"IsMatchActive={matchActiveAfterDrop} " +
                      $"HumanTurns={humanTurnsUsed} AiTurns={aiTurnsUsed} " +
                      $"HumanDone={humanDone} AiDone={aiDone}");

            // Check if match ended during player's drop
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive)
            {
                Debug.Log("[HandManager] FullTurnSequence: Match ended after player drop — skipping AI turn.");
                yield break;
            }

            // 2. Update hand display after player's turn
            RefreshHandFromMatchController();

            // 3. Determine if AI should take a turn
            bool currentPlayerIsAI   = (playerIndexAfterDrop == MatchController.PLAYER_AI);
            bool aiHasTurnsRemaining = !aiDone;
            bool matchStillActive    = matchActiveAfterDrop;

            bool tutorialActive = (TutorialManager.Instance != null && TutorialManager.Instance.IsActive);
            bool aiShouldAct = currentPlayerIsAI && aiHasTurnsRemaining && matchStillActive && !tutorialActive;

            // --- Job 4: Detailed logging of aiShouldAct determination ---
            Debug.Log($"[HandManager] FullTurnSequence AI TURN CHECK: " +
                      $"aiShouldAct={aiShouldAct} " +
                      $"| currentPlayerIsAI={currentPlayerIsAI} (CurrentPlayer={playerIndexAfterDrop}, PLAYER_AI={MatchController.PLAYER_AI}) " +
                      $"| aiHasTurnsRemaining={aiHasTurnsRemaining} (AiTurns={aiTurnsUsed}/{MatchController.MAX_TURNS}) " +
                      $"| matchStillActive={matchStillActive}");

            if (aiShouldAct)
            {
                Debug.Log("[HandManager] AI turn should trigger: true — starting AI turn coroutine.");
            }
            else
            {
                Debug.Log($"[HandManager] AI turn should trigger: false — reason: " +
                          $"{(matchStillActive ? "" : "match not active, ")} " +
                          $"{(currentPlayerIsAI ? "" : $"current player is {playerIndexAfterDrop} not AI, ")} " +
                          $"{(aiHasTurnsRemaining ? "" : "AI has no turns remaining")}");
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

                Debug.Log($"[HandManager] FullTurnSequence FALLBACK recheck: " +
                          $"freshCurrentPlayer={freshCurrentPlayer} " +
                          $"freshAiDone={freshAiDone} " +
                          $"freshMatchActive={freshMatchActive}");

                if (freshCurrentPlayer == MatchController.PLAYER_AI && !freshAiDone && freshMatchActive)
                {
                    Debug.Log("[HandManager] FullTurnSequence FALLBACK: AI turn detected on recheck — triggering.");
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
                Debug.Log("[HandManager] Triggering AI turn...");
                yield return StartCoroutine(GameVisualBridge.Instance.ExecuteAITurnCoroutine());
                Debug.Log("[HandManager] AI turn coroutine completed.");
            }

            // 4. Check match state again after AI turn
            if (MatchController.Instance == null || !MatchController.Instance.IsMatchActive
                || MatchController.Instance.IsGameOver)
            {
                Debug.Log("[HandManager] FullTurnSequence: Match ended after AI turn — forcing GameOver transition.");
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.GameOver);
                yield break;
            }

            // 5. Update hand display again (in case hand changed)
            RefreshHandFromMatchController();

            // 6. Check if match ended after player's turn (human was last)
            if (!MatchController.Instance.IsMatchActive || MatchController.Instance.IsGameOver)
            {
                Debug.Log("[HandManager] FullTurnSequence: Match ended (human last turn) — forcing GameOver.");
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

                Debug.Log("[HandManager] FullTurnSequence END: Player input re-enabled.");
            }
            else
            {
                Debug.Log($"[HandManager] FullTurnSequence END: No more turns. Forcing GameOver.");
                if (GameManager.Instance != null && !MatchController.Instance.IsGameOver)
                    MatchController.Instance.ForceGameOver();
                if (GameManager.Instance != null)
                    GameManager.Instance.TransitionTo(GameState.GameOver);
            }
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
                char[] slots = hand.GetAllSlots();
                for (int i = 0; i < HAND_SIZE && i < slots.Length; i++)
                    _hand[i] = slots[i];

                RefreshAllCardVisuals();
                Debug.Log($"[HandManager] Hand refreshed: {new string(_hand)}");
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
            float shuffleY = cardY - _grid.CellSize * 1.0f;
            float trayBottom = shuffleY - _grid.CellSize * 0.18f;
            float trayH = trayTop - trayBottom;
            float trayW = (_grid.GridRight - _grid.GridLeft) + _grid.CellSize * 0.3f;

            int texW = Mathf.Clamp(Mathf.RoundToInt(trayW * 150f), 64, 1024);
            int texH = Mathf.Clamp(Mathf.RoundToInt(trayH * 150f), 64, 1024);
            int radius = Mathf.Min(texW, texH) / 8;

            // Same material family as board — slightly lighter than board outer
            Color trayColor = new Color(0.085f, 0.105f, 0.260f, 0.60f); // darker, desaturated — distinct from purple bg
            Sprite traySprite = TileRenderer.CreateSolidRoundedRect(texW, texH, radius, trayColor);

            GameObject trayGO = new GameObject("ControlTray");
            trayGO.transform.SetParent(transform, false);
            trayGO.transform.position = new Vector3(0f, (trayTop + trayBottom) / 2f, 0.5f);

            SpriteRenderer sr = trayGO.AddComponent<SpriteRenderer>();
            sr.sprite = traySprite;
            sr.sortingOrder = -1; // behind cards

            float nativeW = texW / 100f;
            float nativeH = texH / 100f;
            trayGO.transform.localScale = new Vector3(trayW / nativeW, trayH / nativeH, 1f);
        }

        private void BuildCardSprites()
        {
            if (_grid == null) return;
            _cardSize = _grid.CellSize * CARD_SIZE_FRACTION;

            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
            int radius  = texSize / 7;
            int border  = Mathf.Max(3, texSize / 16);

            _spriteNormal      = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                    CARD_FILL_NORMAL, CARD_BORDER_NORMAL, border);
            _spriteSelected    = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                    CARD_FILL_NORMAL, CARD_BORDER_SELECT, border + 2);
            _spriteSwap        = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                    CARD_FILL_NORMAL, CARD_BORDER_SWAP, border + 1);
            _spriteSwapSelected= TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                    CARD_FILL_NORMAL, CARD_BORDER_SWAP_SEL, border + 2);
        }

        private void BuildCardObjects()
        {
            if (_grid == null || _spriteNormal == null) return;

            for (int i = 0; i < HAND_SIZE; i++)
                if (_cardObjects[i] != null) Destroy(_cardObjects[i]);

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

                int   texSize    = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
                float nativeSize = texSize / 100f;
                float scale      = _cardSize / nativeSize;
                cardGO.transform.localScale = new Vector3(scale, scale, 1f);

                float invScale = 1f / Mathf.Max(scale, 0.01f);

                // Letter text — TMP, matches board tiles exactly
                GameObject textGO = new GameObject("CardLetter");
                textGO.transform.SetParent(cardGO.transform, false);
                textGO.transform.localPosition = new Vector3(0f, nativeSize * 0.02f, -0.1f);

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
                ptsGO.transform.localPosition = new Vector3(nativeSize * 0.28f, -nativeSize * 0.32f, -0.1f);

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

                // Shadow sprite — independent object (NOT a child of card)
                // Stays at rest position while card lifts above it
                GameObject shadowGO = new GameObject($"CardShadow_{i}");
                shadowGO.transform.SetParent(transform, false); // parent to HandManager, not the card
                shadowGO.transform.position = new Vector3(cardX, cardY, 0f);
                shadowGO.transform.localScale = new Vector3(scale, scale, 1f); // same scale as card

                SpriteRenderer shadowSR = shadowGO.AddComponent<SpriteRenderer>();
                shadowSR.sprite = GetSoftShadowSprite();
                shadowSR.color = new Color(0f, 0f, 0f, 0f); // invisible by default
                shadowSR.sortingOrder = 9;

                _cardShadows[i] = shadowSR;

                _cardObjects[i]  = cardGO;
                _cardSRs[i]      = sr;
                _cardTexts[i]    = tm;
                _cardPtsTexts[i] = ptsTm;
            }

            Debug.Log($"[HandManager] Built {HAND_SIZE} card objects at Y={cardY:F2}");
        }

        // ── Card shadow helpers ───────────────────────────────────────────────────

        private Coroutine _shadowAnimCoroutine;

        private void ShowCardShadow(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_cardShadows[index] == null) return;

            float cardX = GetCardX(index);
            float restY = GetCardRowY();

            float centerX = 0f;
            float maxHOffset = _cardSize * 0.1f;
            float hOffset = -Mathf.Sign(cardX - centerX) * Mathf.Clamp01(Mathf.Abs(cardX - centerX) / 3f) * maxHOffset;

            _cardShadows[index].transform.position = new Vector3(cardX + hOffset, restY - _cardSize * 0.05f, 0f);

            // Shadow starts invisible, fades in + grows slightly as card lifts
            Vector3 cardScale = GetCardBaseScale();
            float shadowScaleMult = 1.15f; // 15% larger than card — soft edge covers extra
            _cardShadows[index].transform.localScale = cardScale * shadowScaleMult * 0.9f;
            _cardShadows[index].color = Color.clear;

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
            float targetAlpha = 0.7f; // strong shadow

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
                    _cardShadows[i].color = new Color(0f, 0f, 0f, 0f);
                    _cardShadows[i].transform.localScale = GetCardBaseScale();
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
                else
                {
                    int pts = LetterData.GetPoints(letter);
                    _cardPtsTexts[index].text = pts > 0 ? pts.ToString() : "";
                }
            }
        }

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
            _shuffleButtonY = cardY - _cardSize * 1.0f;
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
            TMPro.TMP_FontAsset heavyFont = Resources.Load<TMPro.TMP_FontAsset>("NunitoExtraBold SDF");
            if (heavyFont != null) tm.font = heavyFont;
            tm.text = "SHUFFLE";
            tm.fontSize = 2.8f;
            tm.fontStyle = TMPro.FontStyles.Normal;
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
            float rotJitter = 12f;

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
            char[] letters = hand.GetAllSlots();
            for (int i = letters.Length - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                char temp = letters[i];
                letters[i] = letters[j];
                letters[j] = temp;
            }
            for (int i = 0; i < letters.Length; i++)
                hand.SetSlot(i, letters[i]);

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

            yield return new WaitForSeconds(0.18f);

            // Snap clean
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = restPositions[i];
                _cardObjects[i].transform.localRotation = Quaternion.identity;
            }

            _selectedIndex = -1;
            Debug.Log($"[HandManager] Hand shuffled: {new string(letters)}");
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
            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
            float nativeSize = texSize / 100f;
            float scale = _cardSize / nativeSize;
            return new Vector3(scale, scale, 1f);
        }

        private float GetCardRowY()
        {
            if (_grid == null) return -8f;
            return _grid.GridBottom - _grid.CellSize * 0.9f;  // clear gap below board
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
            float actionRowY = _shuffleButtonY;  // same Y as shuffle button
            float nextX = _shuffleButtonX + _cardSize * 2.5f;
            float previewSize = _cardSize * 0.65f;

            // No separate floating NEXT label — it goes on the tile itself (see below)

            // -- Socket/holder behind the next tile --
            GameObject socketGO = new GameObject("NextSocket");
            socketGO.transform.SetParent(transform, false);
            socketGO.transform.position = new Vector3(nextX, actionRowY, -0.5f);
            SpriteRenderer socketSR = socketGO.AddComponent<SpriteRenderer>();
            socketSR.sprite = _spriteNormal;
            socketSR.color = new Color(0.06f, 0.08f, 0.20f, 0.50f); // deep inset — matches board family
            socketSR.sortingOrder = 9;
            float socketScale = (previewSize * 1.15f) / (Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f);
            socketGO.transform.localScale = new Vector3(socketScale, socketScale, 1f);

            // -- Preview tile --
            _nextTilePreview = new GameObject("NextTilePreview");
            _nextTilePreview.transform.SetParent(transform, false);
            _nextTilePreview.transform.position = new Vector3(nextX, actionRowY, -1f);

            SpriteRenderer sr = _nextTilePreview.AddComponent<SpriteRenderer>();
            sr.sprite = _spriteNormal;
            sr.color = new Color(0.85f, 0.83f, 0.80f, 0.55f); // soft warm, docked not boxed
            sr.sortingOrder = 10;
            _nextTileSR = sr;

            // Scale relative to the card sprite (same texture), just smaller
            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
            float nativeSize = texSize / 100f;
            float scale = previewSize / nativeSize;
            _nextTilePreview.transform.localScale = new Vector3(scale, scale, 1f);

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // Letter text (child of tile)
            GameObject textGO = new GameObject("NextLetter");
            textGO.transform.SetParent(_nextTilePreview.transform, false);
            textGO.transform.localPosition = new Vector3(0f, nativeSize * 0.02f, -0.2f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) tm.font = tileFont;
            tm.text          = "";
            tm.fontSize      = 5.5f;
            tm.fontStyle     = TMPro.FontStyles.Bold;
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
            float labelX = nextX - previewSize * 0.5f - _cardSize * 0.35f;
            GameObject labelGO = new GameObject("NextLabel");
            labelGO.transform.position = new Vector3(labelX, actionRowY, -1f);

            TextMesh labelTm = labelGO.AddComponent<TextMesh>();
            labelTm.anchor = TextAnchor.MiddleRight;
            labelTm.alignment = TextAlignment.Right;
            labelTm.fontSize = 38;
            labelTm.characterSize = 0.055f;
            labelTm.fontStyle = FontStyle.Bold;
            labelTm.color = new Color(0.78f, 0.78f, 0.88f, 0.95f);  // brighter, more readable
            labelTm.text = "NEXT";
            GameFont.ApplyBody(labelTm);
            _nextTileLabel = labelTm;

            MeshRenderer labelMr = labelGO.GetComponent<MeshRenderer>();
            if (labelMr != null) labelMr.sortingOrder = 15;
        }

        // ── Tile Bag button ─────────────────────────────────────────────────

        private GameObject _tileBagButton;
        private float _tileBagX;
        private float _tileBagY;
        private float _tileBagSize;

        private void BuildTileBagButton()
        {
            if (_grid == null) return;

            float actionRowY = _shuffleButtonY;
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

            Debug.Log("[HandManager] Showing Swap Tile? popup");
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

            // Use the pre-cached letter from PlayerHand — this is the actual letter
            // the player will receive, accounting for drought assist and hand protection.
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            char next = (hand != null) ? hand.CachedNextLetter : '\0';
            _nextTileLetter.text = (next != '\0') ? next.ToString() : "";
        }
    }

}
