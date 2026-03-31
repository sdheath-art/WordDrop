using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// First-time tutorial that teaches the two core mechanics:
    ///   1. Form a word (CAT) by dropping a letter
    ///   2. Detonate a primed word (POT overlaps primed CAT)
    ///
    /// Flow:
    ///   - Intercepts at match start when PlayerPrefs "tutorial_complete" == 0
    ///   - Sets up a deterministic board + hand for each step
    ///   - Shows instruction text + pulsing arrow on the target column
    ///   - Restricts input to only the target column via AllowedColumn
    ///   - After detonation, ends tutorial, sets PlayerPrefs, starts normal game
    ///
    /// STEP 1 — "MAKE A WORD"
    ///   Board: A at (1,0), T at (2,0)
    ///   Hand:  C, X, R, E, L  (C at slot 0 is the target)
    ///   Arrow: column 0
    ///   Result: C drops to (0,0) → CAT at (0,0)(1,0)(2,0) → primes
    ///
    /// STEP 2 — "BLOW IT UP"
    ///   Board: CAT primed + O placed at (2,1) above T
    ///   Hand:  P, X, R, E, L  (P at slot 0 is the target)
    ///   Arrow: column 2
    ///   Result: P drops to (2,2) → POT vertically (2,2)(2,1)(2,0) → overlaps T(2,0) → DETONATION
    /// </summary>
    public class TutorialManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────────

        public static TutorialManager Instance { get; private set; }

        // ── Tutorial column restriction ───────────────────────────────────────────
        // When >= 0, HandManager should only allow drops into this column.
        // Set to -1 to allow all columns (normal play).
        public static int AllowedColumn { get; private set; } = -1;

        // When true, HandManager should block shuffle and swap interactions.
        public static bool BlockShuffleAndSwap { get; private set; } = false;

        // ── State ─────────────────────────────────────────────────────────────────

        private enum TutorialStep { Inactive, WaitForWord, WaitForDetonation, ShowBoom, Done }
        private TutorialStep _step = TutorialStep.Inactive;
        private static bool _skipForSession = false; // set when player skips or completes tutorial

        public bool IsActive => _step != TutorialStep.Inactive && _step != TutorialStep.Done;

        // ── Visual elements ───────────────────────────────────────────────────────

        private GameObject _instructionGO;
        private TMPro.TextMeshPro _instructionText;
        private GameObject _arrowGO;
        private TextMesh   _arrowText;
        private Tweener    _arrowBobTween;
        private Tweener    _arrowPulseTween;
        private GameObject _skipButtonCanvas;
        private GameObject _cardHighlightGO;
        private Tweener    _cardHighlightPulse;

        // ── Colors ────────────────────────────────────────────────────────────────

        private static readonly Color TEXT_COLOR       = new Color(1f, 1f, 1f, 1f);
        private static readonly Color TEXT_SHADOW      = new Color(0f, 0f, 0f, 0.3f);
        private static readonly Color ARROW_COLOR      = new Color(0.2f, 0.85f, 0.4f, 1f);  // green
        private static readonly Color BOOM_COLOR       = new Color(1f, 0.6f, 0.1f, 1f);     // orange

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
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            UnsubscribeEvents();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — called by MatchController/GameManager at match start
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if the tutorial should run (first game ever).
        /// Call this before normal match setup to decide whether to intercept.
        /// </summary>
        public static bool ShouldRunTutorial()
        {
            if (_skipForSession) return false;
            return true; // always run — player can skip
        }

        /// <summary>
        /// Begins the tutorial sequence. Call AFTER MatchController.StartMatch()
        /// has been called (so the board, hands, and managers are initialized).
        /// The tutorial will override the board and hand state.
        /// </summary>
        public void BeginTutorial()
        {
            if (_step != TutorialStep.Inactive && _step != TutorialStep.Done)
            {
                Debug.LogWarning("[TutorialManager] Tutorial already running.");
                return;
            }

            Debug.Log("[TutorialManager] === TUTORIAL BEGIN ===");

            _step = TutorialStep.WaitForWord;
            AllowedColumn = -1;
            BlockShuffleAndSwap = true;

            // Subscribe to RulesEngine events to detect scoring / detonation
            SubscribeEvents();

            // Show skip button
            ShowSkipButton();

            // Set up the board for step 1
            StartCoroutine(SetupStep1());
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 1 — "MAKE A WORD"
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep1()
        {
            // Wait a frame for everything to settle after StartMatch
            yield return null;

            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null)
            {
                Debug.LogError("[TutorialManager] RulesEngine or GridManager missing — aborting tutorial.");
                EndTutorial();
                yield break;
            }

            // Clear the board (StartMatch may have placed opening seed letters)
            rules.ClearBoard();
            grid.ClearAllCells();

            // Place A at (1,0) and T at (2,0)
            rules.SetCell(1, 0, new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = -1 });
            rules.SetCell(2, 0, new RulesCellData { Letter = 'T', Col = 2, Row = 0, PlayerIndex = -1 });

            // Sync visuals
            grid.RebuildFromRulesEngine(rules);

            // Override the player hand to: C, X, R, E, L
            SetPlayerHand(new char[] { 'C', 'X', 'R', 'E', 'L' });

            // Restrict drops to column 0 only
            AllowedColumn = 0;

            // Show instruction text
            ShowInstruction("MAKE A WORD", TEXT_COLOR);

            // Show pulsing arrow above column 0 + above the C card
            ShowArrow(0);
            ShowCardHighlight(0);

            // Dim shuffle so it doesn't compete with the guided action
            DimShuffle(true);

            // Enable input
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            Debug.Log("[TutorialManager] Step 1 ready — waiting for CAT at column 0.");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 2 — "BLOW IT UP"
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep2()
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null)
            {
                EndTutorial();
                yield break;
            }

            // Disable input briefly while we reconfigure
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            HideArrow();
            HideCardHighlight();
            HideInstruction();

            // Wait for the word scoring animation to play out
            yield return new WaitForSeconds(1.5f);

            // Apply primed glow to CAT tiles before the O drops
            grid.SyncToRulesState(rules);
            PrimedWordRegistry registry = rules.PrimedRegistry;
            if (registry != null)
            {
                for (int p = 0; p < registry.Count; p++)
                {
                    var pw = registry.GetByIndex(p);
                    if (pw == null) continue;
                    for (int c = 0; c < pw.Cells.Count; c++)
                    {
                        Tile t = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                        if (t != null) t.SetPrimedGlow(Tile.PRIMED_GLOW);
                    }
                }
            }

            yield return new WaitForSeconds(0.5f);

            // Place O in rules data at (2,1)
            rules.SetCell(2, 1, new RulesCellData { Letter = 'O', Col = 2, Row = 1, PlayerIndex = 1 });

            // Animate the O dropping in like an AI move
            grid.DropTile(2, 'O', TileOwner.AI);

            // Wait for drop animation to finish
            yield return new WaitForSeconds(0.4f);

            // Override hand to: P, X, R, E, L
            SetPlayerHand(new char[] { 'P', 'X', 'R', 'E', 'L' });

            // Restrict to column 2
            AllowedColumn = 2;

            // Show instruction
            ShowInstruction("NICE. BLOW IT UP.", TEXT_COLOR);

            // Show pulsing arrow above column 2 + above the P card
            ShowArrow(2);
            ShowCardHighlight(0);

            // Force current player back to human (bookkeeping may have switched to AI)
            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            // Re-enable input
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            Debug.Log("[TutorialManager] Step 2 ready — waiting for POT detonation at column 2.");
        }

        // ══════════════════════════════════════════════════════════════════════════
        // EVENT HANDLERS — detect word scoring and detonation
        // ══════════════════════════════════════════════════════════════════════════

        private void SubscribeEvents()
        {
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnWordScored     += OnWordScored;
                RulesEngine.Instance.OnPrimedTriggered += OnPrimedTriggered;
            }
        }

        private void UnsubscribeEvents()
        {
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnWordScored     -= OnWordScored;
                RulesEngine.Instance.OnPrimedTriggered -= OnPrimedTriggered;
            }
        }

        private void OnWordScored(WordScoredEvent evt)
        {
            if (_step != TutorialStep.WaitForWord) return;

            Debug.Log($"[TutorialManager] Word scored during tutorial: '{evt.Word}'");

            // Move to step 2 — wait for detonation
            _step = TutorialStep.WaitForDetonation;
            StartCoroutine(SetupStep2());
        }

        private void OnPrimedTriggered(PrimedTriggeredEvent evt)
        {
            if (_step != TutorialStep.WaitForDetonation) return;

            Debug.Log($"[TutorialManager] DETONATION during tutorial! " +
                      $"Triggered='{evt.TriggeredWord}' by '{evt.TriggerWord}'");

            _step = TutorialStep.ShowBoom;
            StartCoroutine(ShowBoomAndEnd());
        }

        // ══════════════════════════════════════════════════════════════════════════
        // BOOM + END
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator ShowBoomAndEnd()
        {
            HideArrow();
            HideCardHighlight();
            HideInstruction();

            // Disable input while explosion plays out
            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            // Let the explosion animation speak for itself
            yield return new WaitForSeconds(2.0f);

            // Mark tutorial complete for this session and persistently
            _skipForSession = true;
            PlayerPrefs.SetInt("tutorial_complete", 1);
            PlayerPrefs.Save();
            Debug.Log("[TutorialManager] Tutorial complete — PlayerPrefs saved.");

            EndTutorial();

            // Start a fresh normal game
            yield return new WaitForSeconds(0.3f);
            StartNormalGame();
        }

        private void EndTutorial()
        {
            _step = TutorialStep.Done;
            AllowedColumn = -1;
            BlockShuffleAndSwap = false;

            UnsubscribeEvents();
            HideArrow();
            HideCardHighlight();
            HideInstruction();
            HideSkipButton();
            DimShuffle(false);

            Debug.Log("[TutorialManager] === TUTORIAL END ===");
        }

        private void StartNormalGame()
        {
            Debug.Log("[TutorialManager] Starting normal game after tutorial.");

            // Re-enter Playing state which will call StartMatch fresh
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // HAND MANIPULATION
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Overwrites the player's hand data and visual display.
        /// </summary>
        private void SetPlayerHand(char[] letters)
        {
            // Set data in MatchController's PlayerHand
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                {
                    for (int i = 0; i < letters.Length && i < PlayerHand.HAND_SIZE; i++)
                        hand.SetSlot(i, letters[i]);
                }
            }

            // Update visual display
            if (HandManager.Instance != null)
                HandManager.Instance.SetHand(letters);
        }

        // ══════════════════════════════════════════════════════════════════════════
        // DIM SHUFFLE — de-emphasize during tutorial
        // ══════════════════════════════════════════════════════════════════════════

        private void DimShuffle(bool dim)
        {
            if (HandManager.Instance == null) return;
            // Access the shuffle button's SpriteRenderer through the HandManager
            Transform shuffleT = HandManager.Instance.transform.Find("ShuffleButton");
            if (shuffleT == null) return;
            SpriteRenderer sr = shuffleT.GetComponent<SpriteRenderer>();
            if (sr != null)
                sr.color = dim ? new Color(1f, 1f, 1f, 0.3f) : Color.white;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // INSTRUCTION TEXT
        // ══════════════════════════════════════════════════════════════════════════

        private void ShowInstruction(string text, Color color, bool largeScale = false)
        {
            HideInstruction();

            var grid = GridManager.Instance;
            if (grid == null) return;

            _instructionGO = new GameObject("TutorialInstruction");

            // Position between HUD and grid top
            float y = grid.GridTop + grid.CellSize * 1.8f;
            _instructionGO.transform.position = new Vector3(0f, y, -5f);

            _instructionText = _instructionGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset heavyFont = Resources.Load<TMPro.TMP_FontAsset>("NunitoExtraBold SDF");
            if (heavyFont != null) _instructionText.font = heavyFont;
            _instructionText.text           = text;
            _instructionText.fontSize       = largeScale ? 12f : 8f;
            _instructionText.color          = color;
            _instructionText.alignment      = TMPro.TextAlignmentOptions.Center;
            _instructionText.sortingOrder   = 50;
            _instructionText.rectTransform.sizeDelta = new Vector2(10f, 3f);
            _instructionText.enableWordWrapping = false;
            _instructionText.overflowMode   = TMPro.TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(_instructionText, color);

            // Scale-in with OutBack ease
            _instructionGO.transform.localScale = Vector3.zero;
            _instructionGO.transform.DOScale(Vector3.one, 0.4f).SetEase(Ease.OutBack);
        }

        private void HideInstruction()
        {
            if (_instructionGO != null)
            {
                // Kill any active tweens on this object before destroying
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

            // Skip button — bottom center, subtle rounded pill
            GameObject btnGO = new GameObject("SkipButton");
            btnGO.transform.SetParent(canvasGO.transform, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.5f, 0f);
            rt.anchorMax = new Vector2(0.5f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 25f);
            rt.sizeDelta = new Vector2(120f, 38f);  // slightly smaller, balanced

            Image img = btnGO.AddComponent<Image>();
            img.sprite = TileRenderer.CreateSolidRoundedRect(512, 160, 60, Color.white);
            img.type = Image.Type.Simple;
            img.color = new Color(0.15f, 0.15f, 0.25f, 0.50f);

            Button btn = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = new Color(0.15f, 0.15f, 0.25f, 0.50f);
            cb.highlightedColor = new Color(0.20f, 0.20f, 0.30f, 0.65f);
            cb.pressedColor = new Color(0.10f, 0.10f, 0.20f, 0.70f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(OnSkipClicked);

            // Label
            GameObject lblGO = new GameObject("Label");
            lblGO.transform.SetParent(btnGO.transform, false);
            RectTransform lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;

            Text t = lblGO.AddComponent<Text>();
            t.font = GameFont.GetUI();
            t.text = "SKIP ▸";
            t.fontSize = 18;
            t.fontStyle = FontStyle.Normal;
            t.color = new Color(0.75f, 0.75f, 0.85f, 0.9f);
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
            Debug.Log("[TutorialManager] Tutorial skipped by player.");
            _skipForSession = true;
            StopAllCoroutines();
            EndTutorial();
            StartNormalGame();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // CARD HIGHLIGHT — pulsing arrow above the target hand card
        // ══════════════════════════════════════════════════════════════════════════

        private void ShowCardHighlight(int cardIndex)
        {
            HideCardHighlight();

            if (HandManager.Instance == null || GridManager.Instance == null) return;

            // Get the card's world position
            float cardX = HandManager.Instance.GetCardWorldX(cardIndex);
            float cardY = HandManager.Instance.GetCardWorldY();
            float cellSize = GridManager.Instance.CellSize;

            _cardHighlightGO = new GameObject("TutorialCardHighlight");
            _cardHighlightGO.transform.position = new Vector3(cardX, cardY + cellSize * 0.65f, -5f);

            float arrowSize = cellSize * 0.20f;  // smaller, supportive
            SpriteRenderer sr = _cardHighlightGO.AddComponent<SpriteRenderer>();
            sr.sprite = GetArrowSprite();
            sr.color = ARROW_COLOR;
            sr.sortingOrder = 50;

            float nativeSize = 128f / 100f;
            float arrowScale = arrowSize / nativeSize;
            _cardHighlightGO.transform.localScale = new Vector3(arrowScale, arrowScale, 1f);

            // Pulse
            float pulseScale = arrowScale * 1.08f;
            _cardHighlightPulse = _cardHighlightGO.transform
                .DOScale(new Vector3(pulseScale, pulseScale, 1f), 0.7f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);

            // Bob
            _cardHighlightGO.transform
                .DOMoveY(cardY + cellSize * 0.75f, 0.35f)
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
        // PULSING ARROW
        // ══════════════════════════════════════════════════════════════════════════

        private static Sprite _arrowSprite;

        private static Sprite GetArrowSprite()
        {
            if (_arrowSprite != null) return _arrowSprite;

            // Create a high-res chunky downward triangle with soft edges
            int size = 256;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Chunky triangle: wider base, blunted point
                    float ny = (float)y / (size - 1); // 0 at bottom (point), 1 at top (wide)
                    float halfWidth = Mathf.Lerp(0.04f, 0.48f, ny); // blunted point (not razor sharp)
                    float cx = (float)x / (size - 1) - 0.5f;

                    float dist = Mathf.Abs(cx) - halfWidth;
                    if (dist < 0f)
                        pixels[y * size + x] = Color.white;
                    else if (dist < 0.015f) // soft AA edge
                        pixels[y * size + x] = new Color(1f, 1f, 1f, 1f - dist / 0.015f);
                    else
                        pixels[y * size + x] = Color.clear;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _arrowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _arrowSprite;
        }

        private void ShowArrow(int col)
        {
            HideArrow();

            var grid = GridManager.Instance;
            if (grid == null) return;

            float x = grid.GetColumnCenterX(col);
            float y = grid.GridTop + grid.CellSize * 0.5f;
            float arrowSize = grid.CellSize * 0.28f;

            _arrowGO = new GameObject("TutorialArrow");
            _arrowGO.transform.position = new Vector3(x, y, -5f);

            SpriteRenderer sr = _arrowGO.AddComponent<SpriteRenderer>();
            sr.sprite = GetArrowSprite();
            sr.color = ARROW_COLOR;
            sr.sortingOrder = 50;

            float nativeSize = 128f / 100f;
            float scale = arrowSize / nativeSize;
            _arrowGO.transform.localScale = new Vector3(scale, scale, 1f);

            // Pulsing scale
            _arrowPulseTween = _arrowGO.transform
                .DOScale(new Vector3(scale * 1.15f, scale * 1.15f, 1f), 0.6f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);

            // Bob up and down
            float bobY = y + grid.CellSize * 0.15f;
            _arrowBobTween = _arrowGO.transform
                .DOMoveY(bobY, 0.4f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private void HideArrow()
        {
            if (_arrowBobTween != null)  { _arrowBobTween.Kill();   _arrowBobTween = null; }
            if (_arrowPulseTween != null) { _arrowPulseTween.Kill(); _arrowPulseTween = null; }

            if (_arrowGO != null)
            {
                _arrowGO.transform.DOKill();
                Object.Destroy(_arrowGO);
                _arrowGO = null;
                _arrowText = null;
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // DEBUG — skip tutorial via PlayerPrefs
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>Resets the tutorial flag so it will run again on next game.</summary>
        public static void ResetTutorial()
        {
            PlayerPrefs.SetInt("tutorial_complete", 0);
            PlayerPrefs.Save();
            Debug.Log("[TutorialManager] Tutorial reset — will run on next game.");
        }
    }
}

// ══════════════════════════════════════════════════════════════════════════════════
// REQUIRED CHANGES TO OTHER FILES (DO NOT EDIT — notes for manual integration)
// ══════════════════════════════════════════════════════════════════════════════════
//
// ── 1. HandManager.cs ─────────────────────────────────────────────────────────
//
// A) In TryDropInColumn(), after the column is determined from worldPos,
//    add a check for the tutorial column restriction:
//
//      int col = _grid.WorldXToColumn(worldPos.x);
//      if (col < 0) return;
//
//      // Tutorial column restriction
//      if (TutorialManager.AllowedColumn >= 0 && col != TutorialManager.AllowedColumn)
//          return;
//
//      DropSelectedLetterInColumn(col);
//
// B) In the shuffle button handler (TryHandleShuffleButton or similar),
//    add a guard at the top:
//
//      if (TutorialManager.BlockShuffleAndSwap) return false;
//
// C) In TriggerSwapConfirmation(), add a guard at the top:
//
//      if (TutorialManager.BlockShuffleAndSwap) return;
//
// D) Add a public method for the tutorial to force-select a card:
//
//      public void ForceSelectCard(int index)
//      {
//          if (index < 0 || index >= HAND_SIZE) return;
//          _selectedIndex = index;
//          RefreshAllCardVisuals();
//          if (ColumnArrowManager.Instance != null)
//              ColumnArrowManager.Instance.ShowArrows(true);
//      }
//
// ── 2. GameManager.cs ─────────────────────────────────────────────────────────
//
// In OnStateEntered(GameState.Playing), after MatchController.StartMatch() is
// called but BEFORE HandManager.InitialiseHand(), add the tutorial check:
//
//      MatchController.Instance.StartMatch();
//
//      // Check for first-time tutorial
//      if (TutorialManager.ShouldRunTutorial() && TutorialManager.Instance != null)
//      {
//          // Tutorial will override board/hand and manage input itself
//          TutorialManager.Instance.BeginTutorial();
//          return; // Skip normal hand init and input enable
//      }
//
//      // ... normal flow continues: InitialiseHand, SetInteractable, ShowArrows ...
//
// ── 3. MatchController.cs (optional) ──────────────────────────────────────────
//
// No changes strictly required. The tutorial manipulates RulesEngine and
// PlayerHand directly. However, during the tutorial the FullTurnSequence
// in HandManager will still run normally — it will call ProcessDrop,
// CompleteDropBookkeeping, etc. The tutorial hooks into RulesEngine events
// (OnWordScored, OnPrimedTriggered) to detect when each step completes.
//
// The AI turn in FullTurnSequence will fire after the tutorial drop.
// This is fine — the tutorial overrides the board before the AI can act,
// and ends the tutorial + starts a fresh game after detonation.
// If you want to suppress the AI turn during tutorial, add:
//
//      // In FullTurnSequence, before the AI turn section:
//      if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
//      {
//          IsInteractable = true;
//          yield break;  // Skip AI turn during tutorial
//      }
