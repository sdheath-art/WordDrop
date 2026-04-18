using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// First-time tutorial that teaches the game one concept at a time:
    ///
    ///   STEP 1 — "DRAG UP TO DROP"
    ///     Board: empty. Hand: O, X, R, E, L
    ///     Player drags O up to column 3 (center). Letter falls. No word formed.
    ///     Teaches the core input: drag card up → letter falls into column.
    ///
    ///   STEP 2 — "NOW MAKE A WORD"
    ///     Board: O at (3,0), G at (4,0) placed by setup. Hand: D, X, R, E, L
    ///     Player drags D to column 2. D(2,0) O(3,0) G(4,0) = DOG. Tiles prime (glow).
    ///
    ///   STEP 3 — "THOSE TILES ARE LIVE" (no action — just a beat)
    ///     Pause to let the player see the glowing primed tiles. Name the state.
    ///
    ///   STEP 4 — "ONE MORE"
    ///     Board: DOG primed + I placed at (4,1) above G. Hand: P, X, R, E, L
    ///     Player drags P to column 4. P(4,2) I(4,1) G(4,0) = PIG vertically.
    ///     Overlaps G at (4,0) → DETONATION. Tutorial complete.
    /// </summary>
    public class TutorialManager : MonoBehaviour
    {
        // ── Singleton ─────────────────────────────────────────────────────────────

        public static TutorialManager Instance { get; private set; }

        // ── Tutorial restrictions ──────────────────────────────────────────────────
        public static int AllowedColumn { get; private set; } = -1;
        public static int AllowedCardIndex { get; private set; } = -1;
        public static bool BlockShuffleAndSwap { get; private set; } = false;

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

        public bool IsActive => _step != TutorialStep.Inactive && _step != TutorialStep.Done;

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
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            UnsubscribeEvents();
        }

        // ══════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ══════════════════════════════════════════════════════════════════════════

        public static bool ShouldRunTutorial()
        {
            // Tutorial disabled for testing — always skip
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

            _step = TutorialStep.WaitForDrop;
            AllowedColumn = -1;
            AllowedCardIndex = -1;
            BlockShuffleAndSwap = true;

            SubscribeEvents();
            ShowSkipButton();
            StartCoroutine(SetupStep1());
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 1 — "DRAG UP TO DROP"
        // Learn the core input: drag a card up, letter falls into a column.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep1()
        {
            yield return null; // wait a frame for everything to settle

            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            // Clear the board completely
            rules.ClearBoard();
            grid.ClearAllCells();

            // Empty board — just learning to drop
            grid.RebuildFromRulesEngine(rules);

            // Hand: O, X, R, E, L
            SetPlayerHand(new char[] { 'O', 'X', 'R', 'E', 'L' });

            // Restrict to column 3 (center), card 0 only
            AllowedColumn = 3;
            AllowedCardIndex = 0;

            // Rig the next card deal so slot 0 refills with D after the O drop
            RigNextDraw('D');
            NextPreviewLetter = 'D';

            ShowInstruction("DRAG UP TO DROP");
            ShowArrow(3);
            ShowCardHighlight(0);
            DimShuffle(true);

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

//             Debug.Log("[TutorialManager] Step 1 ready — waiting for O drop at column 3.");
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
        // STEP 3 — "THOSE TILES ARE LIVE" (no action — naming the glow)
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep3()
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            HideArrow();
            HideCardHighlight();
            HideInstruction();

            // Wait for word scoring animation
            yield return new WaitForSeconds(1.2f);

            // Sync primed glow visuals
            grid.SyncToRulesState(rules);
            ApplyPrimedGlow(rules, grid);

            // BUG-07: Verify DOG is actually primed before proceeding
            if (rules.PrimedRegistry == null || rules.PrimedRegistry.Count == 0)
            {
                Debug.LogError("[TutorialManager] Step 3: DOG not primed — aborting tutorial.");
                _transitioning = false;
                EndTutorial();
                StartNormalGame();
                yield break;
            }

            yield return new WaitForSeconds(0.3f);

            // Name the state — no action required
            ShowInstruction("THE FUSE IS LIT");

            // Hold for a beat so the player sees the glow + reads the text
            yield return new WaitForSeconds(2.0f);

            HideInstruction();
            yield return new WaitForSeconds(0.3f);

            // Move to step 4
            _step = TutorialStep.WaitForDetonation;
            StartCoroutine(SetupStep4());
        }

        // ══════════════════════════════════════════════════════════════════════════
        // STEP 4 — "ONE MORE"
        // Board: DOG primed + I at (4,1). Player drops P at column 4 → PIG → BOOM.
        // ══════════════════════════════════════════════════════════════════════════

        private IEnumerator SetupStep4()
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { EndTutorial(); yield break; }

            // Place I at (4,1) above G — animate it dropping in like an AI move
            rules.SetCell(4, 1, new RulesCellData { Letter = 'I', Col = 4, Row = 1, PlayerIndex = -1 });
            grid.DropTile(4, 'I', TileOwner.AI);

            yield return new WaitForSeconds(0.5f);

            // Restrict to column 4, card 0 only
            AllowedColumn = 4;
            AllowedCardIndex = 0;

            ShowInstruction("YOUR MOVE");
            ShowArrow(4);
            ShowCardHighlight(0);

            // Force current player back to human
            if (MatchController.Instance != null)
                MatchController.Instance.CurrentPlayer = MatchController.PLAYER_HUMAN;

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(true);

            _transitioning = false;
//             Debug.Log("[TutorialManager] Step 4 ready — waiting for P drop at column 4 to form PIG → detonation.");
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

//             Debug.Log($"[TutorialManager] Word scored during step 2: '{evt.Word}'");

            // Step 2 complete — word formed, now show primed state
            _step = TutorialStep.ShowPrimed;
            _transitioning = true;
            StartCoroutine(SetupStep3());
        }

        private void OnPrimedTriggered(PrimedTriggeredEvent evt)
        {
            if (_step != TutorialStep.WaitForDetonation) return;
            if (_transitioning) return;

//             Debug.Log($"[TutorialManager] DETONATION during step 4! " +
                      // $"Triggered='{evt.TriggeredWord}' by '{evt.TriggerWord}'");

            _step = TutorialStep.ShowBoom;
            _transitioning = true;
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

            if (HandManager.Instance != null)
                HandManager.Instance.SetInteractable(false);

            // Let the explosion speak for itself
            yield return new WaitForSeconds(1.5f);

            // Brief celebration text
            ShowInstruction("NICE.");
            yield return new WaitForSeconds(1.2f);

            HideInstruction();

            _skipForSession = true;
            PlayerPrefs.SetInt("tutorial_complete", 1);
            PlayerPrefs.Save();
//             Debug.Log("[TutorialManager] Tutorial complete — PlayerPrefs saved.");

            EndTutorial();

            yield return new WaitForSeconds(0.3f);
            StartNormalGame();
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

//             Debug.Log("[TutorialManager] === TUTORIAL END ===");
        }

        private void StartNormalGame()
        {
//             Debug.Log("[TutorialManager] Starting normal game after tutorial.");
            if (GameManager.Instance != null)
                GameManager.Instance.TransitionTo(GameState.Playing);
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

        private void ShowInstruction(string text)
        {
            HideInstruction();

            var grid = GridManager.Instance;
            if (grid == null) return;

            _instructionGO = new GameObject("TutorialInstruction");

            float y = grid.GridTop + grid.CellSize * 1.8f;
            _instructionGO.transform.position = new Vector3(0f, y, -5f);

            _instructionText = _instructionGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset uiFont = GameFont.GetUITMP();
            if (uiFont != null) _instructionText.font = uiFont;
            _instructionText.text           = text;
            _instructionText.fontSize       = 7f;
            _instructionText.color          = TEXT_COLOR;
            _instructionText.alignment      = TMPro.TextAlignmentOptions.Center;
            _instructionText.sortingOrder   = 50;
            _instructionText.rectTransform.sizeDelta = new Vector2(10f, 3f);
            _instructionText.enableWordWrapping = false;
            _instructionText.overflowMode   = TMPro.TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(_instructionText, TEXT_COLOR, TMPHelper.TextTier.HUD);

            // Scale-in
            _instructionGO.transform.localScale = Vector3.zero;
            _instructionGO.transform.DOScale(Vector3.one, 0.35f).SetEase(Ease.OutBack);
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

        private void ShowCardHighlight(int cardIndex)
        {
            HideCardHighlight();

            if (HandManager.Instance == null || GridManager.Instance == null) return;

            float cardX = HandManager.Instance.GetCardWorldX(cardIndex);
            float cardY = HandManager.Instance.GetCardWorldY();
            float cellSize = GridManager.Instance.CellSize;

            _cardHighlightGO = new GameObject("TutorialCardHighlight");
            _cardHighlightGO.transform.position = new Vector3(cardX, cardY + cellSize * 0.65f, -5f);

            float arrowSize = cellSize * 0.15f;
            SpriteRenderer sr = _cardHighlightGO.AddComponent<SpriteRenderer>();
            sr.sprite = GetChevronSprite();
            sr.color = ARROW_COLOR;
            sr.sortingOrder = 50;

            float nativeSize = 128f / 100f;
            float arrowScale = arrowSize / nativeSize;
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
