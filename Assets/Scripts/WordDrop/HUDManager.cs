using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// HUD Manager for the Scrabble-drop game (Job 10 rewrite).
    ///
    /// Layout (top bar):
    ///   LEFT:   P1 score  (green)
    ///   CENTER: Turn N/40 + Swaps: N below
    ///   RIGHT:  AI score  (orange)
    ///   FAR-LEFT edge: Reset button (small)
    ///
    /// Word-found overlay: brief popup in the middle of the screen.
    /// No combo pill. No target word blanks. No round label.
    ///
    /// Built entirely in Awake() so it's ready before SceneBootstrap.Start() runs.
    /// </summary>
    public class HUDManager : MonoBehaviour
    {
        public static HUDManager Instance { get; private set; }

        // ── Canvas / Panel ────────────────────────────────────────────────────────

        private Canvas _canvas;

        // ── Score labels ──────────────────────────────────────────────────────────

        private TextMeshProUGUI _playerScoreText;   // "P1:" label (static)
        private TextMeshProUGUI _playerScoreNum;    // number only (animated)
        private TextMeshProUGUI _aiScoreText;       // "AI:" label (static)
        private TextMeshProUGUI _aiScoreNum;        // number only (animated)

        // ── Turn / Swap / Rewrite counters ───────────────────────────────────────

        private TextMeshProUGUI _turnCounterText;
        private TextMeshProUGUI _swapCounterText;
        private TextMeshProUGUI _rewriteCounterText;

        // ── Level-mode HUD (Phase 9.8) ───────────────────────────────────────────
        // Level mode shows a target readout under the player score ("/50" muted
        // divider) and a MOVES counter in the right slot where the AI score used
        // to live. Solo-mode's HideAIScore() keeps the AI widgets inactive so
        // these overlay cleanly. Activated via HandleLevelStarted; deactivated
        // on null-data (abort / non-Level boot).
        private TextMeshProUGUI _levelTargetText;
        private TextMeshProUGUI _levelMovesLabelText;
        private TextMeshProUGUI _levelMovesNumText;

        // ── Word-found overlay ────────────────────────────────────────────────────

        private GameObject _wordFoundOverlay;
        private TextMeshProUGUI _wordFoundText;
        private Coroutine  _wordFoundCoroutine;

        // ── Colors ────────────────────────────────────────────────────────────────

        // Colors — cohesive purple/gold/pink palette matching the game's visual identity
        private static Color PLAYER_COLOR
        {
            get { return new Color(1.00f, 0.84f, 0.42f, 1f); } // bright gold #FFD66B
        }
        private static Color AI_COLOR
        {
            get { return new Color(1.00f, 0.56f, 0.67f, 1f); } // soft pink #FF8FAA
        }
        private static Color TURN_COLOR
        {
            get { return new Color(0.94f, 0.90f, 0.84f, 1f); } // warm white #F0E6D6
        }
        private static Color TURN_WARN
        {
            get { return new Color(1.00f, 0.80f, 0.35f, 1f); } // warm amber
        }
        private static Color TURN_DANGER
        {
            get { return new Color(1.00f, 0.40f, 0.40f, 1f); } // soft red
        }
        private static readonly Color SWAP_COLOR      = new Color(0.63f, 0.53f, 0.75f, 0.8f); // light purple #A088C0
        private static readonly Color WORD_POPUP_P1   = new Color(1.00f, 0.84f, 0.42f, 1f);   // gold (match player)
        private static readonly Color WORD_POPUP_AI   = new Color(1.00f, 0.56f, 0.67f, 1f);   // pink (match AI)
        private static readonly Color BAR_BG          = new Color(0.118f, 0.055f, 0.243f, 0.95f); // deep purple #1E0E3E
        private static readonly Color RESET_NORMAL    = new Color(0.18f, 0.18f, 0.23f, 1f);
        private static readonly Color RESET_HIGHLIGHT = new Color(0.32f, 0.32f, 0.40f, 1f);
        private static readonly Color RESET_PRESSED   = new Color(0.10f, 0.10f, 0.14f, 1f);

        // ── Blitz mode state ──────────────────────────────────────────────────────
        private bool _isBlitzMode = false;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildHUD();
//             Debug.Log("[HUDManager] Awake — HUD built");
        }

        private void OnEnable()
        {
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnLevelStarted -= HandleLevelStarted;
                LevelController.Instance.OnLevelStarted += HandleLevelStarted;
            }
        }

        private void Start()
        {
            // LevelController may not exist during Awake (SceneBootstrap ordering).
            // Re-subscribe in Start as a safety net — identical pattern to BonusHUD.
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnLevelStarted -= HandleLevelStarted;
                LevelController.Instance.OnLevelStarted += HandleLevelStarted;
            }
        }

        private void OnDisable()
        {
            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelStarted -= HandleLevelStarted;
        }

        // Per-level HUD element gating: SWAP / EDIT charge counters hide on
        // Level-mode levels that don't opt in via hudFlags (design commit:
        // editCharges visible from L4+, swapCharges visible from L5+). Any
        // non-Level mode (data == null — Survival / Daily / debug) keeps both
        // counters visible as the safe default.
        private void HandleLevelStarted(LevelData data)
        {
            // Swap / edit counters only activate when a level explicitly opts
            // in via hudFlags. null-data (AbortLevel on menu return) keeps
            // them hidden — otherwise the Playing→Menu slide briefly flashes
            // SWAP x2 / EDIT x0 over the menu. Survival mode (kill-switched
            // in production) will need a separate show hook in Phase 9.5.
            bool showSwap = data != null
                         && data.hudFlags != null
                         && data.hudFlags.showSwapCharges;
            bool showEdit = data != null
                         && data.hudFlags != null
                         && data.hudFlags.showEditCharges;

            if (_swapCounterText != null)
                _swapCounterText.gameObject.SetActive(showSwap);
            if (_rewriteCounterText != null)
                _rewriteCounterText.gameObject.SetActive(showEdit);

            // Level-mode readouts — target under score, MOVES on the right.
            // Null-data (AbortLevel / non-Level boot) hides them again.
            bool showLevelHud = (data != null);
            if (_levelTargetText != null)
                _levelTargetText.gameObject.SetActive(showLevelHud);
            if (_levelMovesLabelText != null)
                _levelMovesLabelText.gameObject.SetActive(showLevelHud);
            if (_levelMovesNumText != null)
                _levelMovesNumText.gameObject.SetActive(showLevelHud);

            if (showLevelHud)
            {
                SetLevelTarget(data.target);
                SetLevelMoves(data.moveBudget);
            }
        }

        /// <summary>Sets the "/{target}" divider shown under the player score in Level mode.</summary>
        public void SetLevelTarget(int target)
        {
            if (_levelTargetText == null) return;
            _levelTargetText.text = $"/ {target}";
        }

        /// <summary>Sets the MOVES number in the right HUD slot in Level mode.</summary>
        public void SetLevelMoves(int moves)
        {
            if (_levelMovesNumText == null) return;
            _levelMovesNumText.text = moves.ToString();
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD
        // ═══════════════════════════════════════════════════════════════════════════

        private void BuildHUD()
        {
            var cfg = UIConfig.Instance; // null-safe: checked per-field

            // ── Canvas ────────────────────────────────────────────────────────────
            GameObject canvasGO = new GameObject("HUDCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = cfg != null ? cfg.referenceResolution : new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = cfg != null ? cfg.canvasMatchWidthOrHeight : 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // ── Safe Area container — respects iPhone notch/dynamic island ────────
            GameObject safeAreaGO = new GameObject("SafeArea");
            safeAreaGO.transform.SetParent(canvasGO.transform, false);
            RectTransform safeRT = safeAreaGO.AddComponent<RectTransform>();
            safeRT.anchorMin = Vector2.zero;
            safeRT.anchorMax = Vector2.one;
            safeRT.offsetMin = Vector2.zero;
            safeRT.offsetMax = Vector2.zero;

            // Apply safe area insets
            Rect safeArea = Screen.safeArea;
            Vector2 anchorMin = safeArea.position;
            Vector2 anchorMax = safeArea.position + safeArea.size;
            anchorMin.x /= Screen.width;
            anchorMin.y /= Screen.height;
            anchorMax.x /= Screen.width;
            anchorMax.y /= Screen.height;
            safeRT.anchorMin = anchorMin;
            safeRT.anchorMax = anchorMax;

            // ── HUD Bar — solid flat strip across the top ─────────────────────────
            GameObject barGO = new GameObject("HUDBar");
            barGO.transform.SetParent(safeAreaGO.transform, false);

            RectTransform barRT = barGO.AddComponent<RectTransform>();
            barRT.anchorMin = new Vector2(0f, 1f);
            barRT.anchorMax = new Vector2(1f, 1f);
            barRT.pivot     = new Vector2(0.5f, 1f);
            float barH = cfg != null ? cfg.hudBarHeight : 78f;
            if (Application.isMobilePlatform) barH = Mathf.Min(barH, 60f);
            barRT.offsetMin = new Vector2(0f, -barH);
            barRT.offsetMax = new Vector2(0f,   0f);

            Image barImg = barGO.AddComponent<Image>();
            barImg.color = cfg != null ? cfg.hudBarBgColor : BAR_BG;

            TMP_FontAsset heavyFont = GameFont.GetUITMP();

            // ── Left: YOU score ──────────────────────────────────────────────────
            _playerScoreText = MakeLabel(barGO.transform, "PlayerLabel",
                anchorMin: new Vector2(0.08f, 0.60f),
                anchorMax: new Vector2(0.28f, 0.95f),
                pivot:     new Vector2(0f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "",
                size:      12,
                style:     FontStyle.Bold,
                color:     new Color(PLAYER_COLOR.r, PLAYER_COLOR.g, PLAYER_COLOR.b, 0.7f),
                align:     TextAnchor.MiddleLeft);

            _playerScoreNum = MakeLabel(barGO.transform, "PlayerScoreNum",
                anchorMin: new Vector2(0.08f, 0.05f),
                anchorMax: new Vector2(0.22f, 0.65f),   // was 0.28 — tighter right edge
                pivot:     new Vector2(0f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "0",
                size:      30,
                style:     FontStyle.Bold,
                color:     PLAYER_COLOR,
                align:     TextAnchor.MiddleLeft);
            if (heavyFont != null) _playerScoreNum.font = heavyFont;
            // Auto-shrink large scores so 4-5 digit values don't overflow into the
            // swap/edit counters. Min 16 stays readable; max 30 preserves the punch
            // for typical 2-3 digit scores.
            if (_playerScoreNum != null)
            {
                _playerScoreNum.enableAutoSizing = true;
                _playerScoreNum.fontSizeMin = 16;
                _playerScoreNum.fontSizeMax = 30;
                _playerScoreNum.overflowMode = TMPro.TextOverflowModes.Overflow;
            }

            // ── Center: Turn counter ─────────────────────────────────────────────
            _turnCounterText = MakeLabel(barGO.transform, "TurnCounter",
                anchorMin: new Vector2(0.24f, 0.10f),
                anchorMax: new Vector2(0.98f, 0.90f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "",
                size:      18,
                style:     FontStyle.Bold,
                color:     new Color(0.97f, 0.95f, 0.92f, 0.9f),
                align:     TextAnchor.MiddleCenter);
            if (heavyFont != null) _turnCounterText.font = heavyFont;

            // ── Right: AI score ──────────────────────────────────────────────────
            _aiScoreText = MakeLabel(barGO.transform, "AILabel",
                anchorMin: new Vector2(0.75f, 0.60f),
                anchorMax: new Vector2(0.92f, 0.95f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "AI",
                size:      14,
                style:     FontStyle.Bold,
                color:     new Color(AI_COLOR.r, AI_COLOR.g, AI_COLOR.b, 0.7f),
                align:     TextAnchor.MiddleRight);

            _aiScoreNum = MakeLabel(barGO.transform, "AIScoreNum",
                anchorMin: new Vector2(0.72f, 0.05f),
                anchorMax: new Vector2(0.92f, 0.65f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "0",
                size:      38,
                style:     FontStyle.Bold,
                color:     AI_COLOR,
                align:     TextAnchor.MiddleRight);
            if (heavyFont != null) _aiScoreNum.font = heavyFont;

            // ── Level-mode readouts (Phase 9.8) ──────────────────────────────────
            // TARGET — small muted label UNDER the player score ("/ 50"). Lives
            // under the player score anchor so it reads as "score / target".
            _levelTargetText = MakeLabel(barGO.transform, "LevelTargetText",
                anchorMin: new Vector2(0.22f, 0.05f),
                anchorMax: new Vector2(0.34f, 0.55f),
                pivot:     new Vector2(0f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "",
                size:      14,
                style:     FontStyle.Bold,
                color:     new Color(PLAYER_COLOR.r, PLAYER_COLOR.g, PLAYER_COLOR.b, 0.55f),
                align:     TextAnchor.MiddleLeft);

            // MOVES label — matches AI label slot (small, bold, muted).
            _levelMovesLabelText = MakeLabel(barGO.transform, "LevelMovesLabel",
                anchorMin: new Vector2(0.75f, 0.60f),
                anchorMax: new Vector2(0.92f, 0.95f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "MOVES",
                size:      12,
                style:     FontStyle.Bold,
                color:     new Color(TURN_COLOR.r, TURN_COLOR.g, TURN_COLOR.b, 0.65f),
                align:     TextAnchor.MiddleRight);

            // MOVES number — matches AI number slot (big, bold).
            _levelMovesNumText = MakeLabel(barGO.transform, "LevelMovesNum",
                anchorMin: new Vector2(0.72f, 0.05f),
                anchorMax: new Vector2(0.92f, 0.65f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "0",
                size:      30,
                style:     FontStyle.Bold,
                color:     TURN_COLOR,
                align:     TextAnchor.MiddleRight);
            if (heavyFont != null) _levelMovesNumText.font = heavyFont;

            // All three start hidden — HandleLevelStarted activates them on
            // Level-mode entry; non-Level modes keep them inactive.
            if (_levelTargetText != null) _levelTargetText.gameObject.SetActive(false);
            if (_levelMovesLabelText != null) _levelMovesLabelText.gameObject.SetActive(false);
            if (_levelMovesNumText != null) _levelMovesNumText.gameObject.SetActive(false);

            // ── Swaps + Rewrites — compact, under center ─────────────────────────
            Color swapCol = new Color(0.78f, 0.78f, 0.85f, 0.8f);

            // Swap/edit counters moved RIGHT so they don't sit under the growing
            // score label on the left. Was 0.24-0.44 / 0.44-0.64; now 0.30-0.48 /
            // 0.48-0.66 — the score's expanded anchor (up to 0.22) now clears
            // these by ~8% screen width buffer.
            _swapCounterText = MakeLabel(barGO.transform, "SwapCounter",
                anchorMin: new Vector2(0.30f, 0.02f),
                anchorMax: new Vector2(0.48f, 0.28f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "SWAP x2",
                size:      10,
                style:     FontStyle.Bold,
                color:     swapCol,
                align:     TextAnchor.MiddleCenter);

            _rewriteCounterText = MakeLabel(barGO.transform, "RewriteCounter",
                anchorMin: new Vector2(0.48f, 0.02f),
                anchorMax: new Vector2(0.66f, 0.28f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "EDIT x1",
                size:      10,
                style:     FontStyle.Bold,
                color:     swapCol,
                align:     TextAnchor.MiddleCenter);

            // Default-inactive: only levels that explicitly set
            // hudFlags.showSwapCharges / showEditCharges activate these via
            // HandleLevelStarted. Prevents scene-transition race windows
            // where the default-visible state was briefly rendered during
            // the first frames of a level load.
            if (_swapCounterText != null) _swapCounterText.gameObject.SetActive(false);
            if (_rewriteCounterText != null) _rewriteCounterText.gameObject.SetActive(false);

            // ── Menu button — small, far right edge ──────────────────────────────
            BuildMenuButton(barGO.transform);

            // ── Word-found overlay ────────────────────────────────────────────────
            BuildWordFoundOverlay(canvasGO.transform);
        }

        private void BuildResetButton(Transform plateTransform)
        {
            // Reset button removed — menu button handles returning to menu
        }

        private void BuildMenuButton(Transform plateTransform)
        {
            // Small pause/menu icon tucked into top-left corner (not overlapping scores)
            GameObject btnGO = new GameObject("MenuButton");
            btnGO.transform.SetParent(plateTransform, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.01f, 0.30f);
            rt.anchorMax = new Vector2(0.07f, 0.70f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(2f, 0f);
            rt.offsetMax = new Vector2(0f, 0f);

            Image img = btnGO.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.08f);

            Button btn    = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor      = new Color(1f, 1f, 1f, 0.08f);
            cb.highlightedColor = new Color(1f, 1f, 1f, 0.15f);
            cb.pressedColor     = new Color(1f, 1f, 1f, 0.04f);
            cb.fadeDuration     = 0.08f;
            btn.colors          = cb;
            btn.onClick.AddListener(OnMenuClicked);

            // Simple "X" label
            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);

            RectTransform lrt = labelGO.AddComponent<RectTransform>();
            lrt.anchorMin = Vector2.zero;
            lrt.anchorMax = Vector2.one;
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;

            Text t = labelGO.AddComponent<Text>();
            t.font = GameFont.GetUI();
            t.text = "≡";
            t.fontSize = 20;
            t.fontStyle = FontStyle.Bold;
            t.color = new Color(0.85f, 0.85f, 0.92f, 0.6f);
            t.alignment = TextAnchor.MiddleCenter;
        }

        private void BuildWordFoundOverlay(Transform canvasTransform)
        {
            // Small banner just below HUD bar — does NOT block the board
            _wordFoundOverlay = new GameObject("WordFoundOverlay");
            _wordFoundOverlay.transform.SetParent(canvasTransform, false);

            RectTransform rt = _wordFoundOverlay.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.1f, 0.88f);
            rt.anchorMax = new Vector2(0.9f, 0.95f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image bg = _wordFoundOverlay.AddComponent<Image>();
            var cfgBg = UIConfig.Instance;
            bg.color = cfgBg != null ? cfgBg.hudWordFoundBgColor : new Color(0.04f, 0.04f, 0.06f, 0.75f);

            GameObject textGO = new GameObject("WordFoundText");
            textGO.transform.SetParent(_wordFoundOverlay.transform, false);

            RectTransform trt = textGO.AddComponent<RectTransform>();
            trt.anchorMin = Vector2.zero;
            trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(8f,  2f);
            trt.offsetMax = new Vector2(-8f, -2f);

            _wordFoundText = textGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset uiFont = GameFont.GetUITMP();
            if (uiFont != null) _wordFoundText.font = uiFont;
            _wordFoundText.text      = "";
            var cfgWf = UIConfig.Instance;
            _wordFoundText.fontSize  = cfgWf != null ? cfgWf.hudWordFoundFontSize : 30;
            _wordFoundText.fontStyle = FontStyles.Normal;
            _wordFoundText.color     = WORD_POPUP_P1;
            _wordFoundText.alignment = TextAlignmentOptions.Center;
            _wordFoundText.enableWordWrapping = false;
            _wordFoundText.overflowMode = TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(_wordFoundText, WORD_POPUP_P1, TMPHelper.TextTier.HUD);

            _wordFoundOverlay.SetActive(false);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Score
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Updates the P1 score with animated count-up.</summary>
        public void SetPlayerScore(int pts)
        {
            if (_playerScoreNum == null) return;
            int current = 0;
            int.TryParse(_playerScoreNum.text, out current);
            if (pts != current && pts > current)
            {
                if (_playerCountUp != null) StopCoroutine(_playerCountUp);
                _playerCountUp = StartCoroutine(CountUpScore(_playerScoreNum, current, pts, true));
                AnimateScorePop(_playerScoreNum.rectTransform);
            }
            else
            {
                _playerScoreNum.text = pts.ToString();
            }
        }

        /// <summary>Updates the AI score with animated count-up.</summary>
        /// <summary>Update the AI label to show rival name + record.</summary>
        public void SetRivalName(string name, Color accentColor)
        {
            if (_aiScoreText == null) return;
            _aiScoreText.text = name.ToUpper();
            _aiScoreText.color = accentColor;
        }

        public void SetAIScore(int pts)
        {
            if (_aiScoreNum == null) return;
            int current = 0;
            int.TryParse(_aiScoreNum.text, out current);
            if (pts != current && pts > current)
            {
                if (_aiCountUp != null) StopCoroutine(_aiCountUp);
                _aiCountUp = StartCoroutine(CountUpScore(_aiScoreNum, current, pts, false));
                AnimateScorePop(_aiScoreNum.rectTransform);
            }
            else
            {
                _aiScoreNum.text = pts.ToString();
            }
        }

        /// <summary>Hide AI score display (Survival mode — no opponent).</summary>
        public void HideAIScore()
        {
            if (_aiScoreText != null) _aiScoreText.gameObject.SetActive(false);
            if (_aiScoreNum  != null) _aiScoreNum.gameObject.SetActive(false);
        }

        /// <summary>Show AI score display (returning from Survival to Classic).</summary>
        public void ShowAIScore()
        {
            if (_aiScoreText != null) _aiScoreText.gameObject.SetActive(true);
            if (_aiScoreNum  != null) _aiScoreNum.gameObject.SetActive(true);
        }

        /// <summary>Hide "YOU" label (Survival — solo, no need for player label).</summary>
        public void HidePlayerLabel()
        {
            if (_playerScoreText != null) _playerScoreText.gameObject.SetActive(false);
        }

        /// <summary>Show "YOU" label (returning from Survival to Classic).</summary>
        public void ShowPlayerLabel()
        {
            if (_playerScoreText != null) _playerScoreText.gameObject.SetActive(true);
        }

        // ── Visual score tick (used by ScoringDisplay to count up per-tile) ─────
        // Tick accumulator tracks points added during the current scoring animation.
        // Display value = ScoreManager (authoritative) + accumulator (visual preview).
        private int _tickAccumPlayer = 0;
        private int _tickAccumAI = 0;

        /// <summary>
        /// Visually tick the score up by delta points. Does not touch ScoreManager.
        /// The real score is applied later by CompleteDropBookkeeping.
        /// </summary>
        public void TickScore(bool isPlayer, int delta)
        {
            // No-op: tick counting removed to prevent score flicker.
            // HUD totals update immediately via SetPlayerScore/SetAIScore.
            // The ScoringDisplay popup shows the per-word breakdown instead.
        }

        private Coroutine _scorePopCoroutine;

        /// <summary>RTT-style score pop — exact RTT values with coroutine jitter.</summary>
        private void AnimateScorePop(Transform t)
        {
            if (t == null) return;
            if (_scorePopCoroutine != null) StopCoroutine(_scorePopCoroutine);
            _scorePopCoroutine = StartCoroutine(ScorePopCoroutine(t));
        }

        private IEnumerator ScorePopCoroutine(Transform t)
        {
            t.DOKill();
            t.localScale = Vector3.one;
            t.localRotation = Quaternion.identity;

            // Flash text color to bright white
            TextMeshProUGUI tmp = t.GetComponent<TextMeshProUGUI>();
            Color origColor = (tmp == _playerScoreNum) ? PLAYER_COLOR : AI_COLOR;
            if (tmp != null) tmp.color = Color.white;

            // Scale PUNCH — big overshoot then elastic settle
            t.DOScale(2.2f, 0.12f).SetEase(Ease.OutBack, 3f);

            // Jitter rotation per-frame during the pop (0.6s total)
            float jitterElapsed = 0f;
            float jitterDur = 0.6f;
            float scaleUpDur = 0.12f;
            float settleDur = 0.35f;
            bool settleStarted = false;

            while (jitterElapsed < jitterDur && t != null)
            {
                jitterElapsed += Time.deltaTime;
                float decay = 1f - (jitterElapsed / jitterDur);

                // Random rotation each frame (±15° decaying)
                float rot = Random.Range(-15f, 15f) * decay;
                t.localRotation = Quaternion.Euler(0f, 0f, rot);

                // Trigger settle scale after scale-up completes
                if (!settleStarted && jitterElapsed >= scaleUpDur)
                {
                    settleStarted = true;
                    t.DOScale(1f, settleDur).SetEase(Ease.OutElastic, 0.5f, 0.3f);
                }

                yield return null;
            }

            if (t == null) yield break;

            // Clean rotation
            t.localRotation = Quaternion.identity;

            // Final settle beat: 1.0 → 1.3 → 1.0
            t.DOScale(1.3f, 0.08f).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(0.08f);
            if (t != null) t.DOScale(1f, 0.15f).SetEase(Ease.OutBack, 2f);
            yield return new WaitForSeconds(0.15f);

            // Ensure clean state
            if (t != null)
            {
                t.localScale = Vector3.one;
                t.localRotation = Quaternion.identity;
            }

            // Fade color back (0.3s)
            if (tmp != null)
            {
                float fadeElapsed = 0f;
                Color fromColor = tmp.color;
                while (fadeElapsed < 0.3f && tmp != null)
                {
                    fadeElapsed += Time.deltaTime;
                    tmp.color = Color.Lerp(fromColor, origColor, Mathf.Clamp01(fadeElapsed / 0.3f));
                    yield return null;
                }
                if (tmp != null) tmp.color = origColor;
            }

            _scorePopCoroutine = null;
        }

        /// <summary>Sync display scores to actual ScoreManager values.</summary>
        public void SyncDisplayScores()
        {
            _tickAccumPlayer = 0;
            _tickAccumAI = 0;
            if (ScoreManager.Instance != null)
            {
                SetPlayerScore(ScoreManager.Instance.PlayerScore);
                SetAIScore(ScoreManager.Instance.AIScore);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Turn Countdown (used by TurnCountdown.cs)
        // ═══════════════════════════════════════════════════════════════════════════

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Turn Counter
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Updates turn display as "Turn used/max".
        /// Color shifts amber at ≤40% remaining, red at ≤20% remaining.
        /// </summary>
        public void SetTurnCounter(int current, int max)
        {
            if (_turnCounterText == null) return;

            _turnCounterText.text = $"Turn {current}/{max}";

            int remaining = max - current;
            float ratio   = max > 0 ? (float)remaining / max : 1f;

            if (ratio <= 0.20f)
                _turnCounterText.color = TURN_DANGER;
            else if (ratio <= 0.40f)
                _turnCounterText.color = TURN_WARN;
            else
                _turnCounterText.color = TURN_COLOR;
        }

        /// <summary>
        /// Alternative setter used by GameVisualBridge: remaining turns + max.
        /// Converts to "Turn used/max" format internally.
        /// </summary>
        public void SetTurnsRemaining(int remaining, int max)
        {
            // TurnCountdown manages the _turnCounterText label directly
            if (TurnCountdown.Instance != null)
                TurnCountdown.Instance.UpdateTurnsLeft(remaining, max);
        }

        /// <summary>
        /// Direct text/color setter for turn countdown label. Used by TurnCountdown.
        /// Punches scale on critical announcements (LAST TURN, SUDDEN DEATH).
        /// </summary>
        public void SetTurnCountdownText(string text, Color color)
        {
            if (_turnCounterText == null) return;
            _turnCounterText.text = text;
            _turnCounterText.color = color;

            // Punch on dramatic announcements
            if (text.Contains("LAST") || text.Contains("SUDDEN"))
            {
                var rt = _turnCounterText.rectTransform;
                rt.DOKill();
                rt.localScale = Vector3.one * 1.5f;
                rt.DOScale(1f, 0.35f).SetEase(Ease.OutBack, 2f);
            }
        }

        /// <summary>
        /// Returns the world-space position of the player score counter.
        /// Used by flying score text to know where to fly to.
        /// </summary>
        public Vector3 GetPlayerScoreWorldPos()
        {
            if (_playerScoreNum == null || _canvas == null) return Vector3.up * 4f;
            return _playerScoreNum.rectTransform.position;
        }

        public Vector3 GetAIScoreWorldPos()
        {
            if (_aiScoreNum == null || _canvas == null) return Vector3.up * 4f;
            return _aiScoreNum.rectTransform.position;
        }

        private Coroutine _playerCountUp;
        private Coroutine _aiCountUp;

        /// <summary>Punch the score counter (visual only — score update handled by SetPlayerScore).</summary>
        public void PunchPlayerScore(int addPoints = 0)
        {
            if (_playerScoreNum == null) return;
            _playerScoreNum.rectTransform.DOKill();
            _playerScoreNum.rectTransform.localScale = Vector3.one;
            _playerScoreNum.rectTransform.localRotation = Quaternion.identity;
            AnimateScorePop(_playerScoreNum.rectTransform);
        }

        public void PunchAIScore(int addPoints = 0)
        {
            if (_aiScoreNum == null) return;
            _aiScoreNum.rectTransform.DOKill();
            _aiScoreNum.rectTransform.localScale = Vector3.one;
            _aiScoreNum.rectTransform.localRotation = Quaternion.identity;
            AnimateScorePop(_aiScoreNum.rectTransform);
        }

        private IEnumerator CountUpScore(TextMeshProUGUI label, int from, int to, bool isPlayer)
        {
            int delta = to - from;
            float duration = Mathf.Clamp(delta * 0.03f, 0.15f, 0.5f);
            float elapsed = 0f;
            int lastDisplay = from;
            float tickInterval = 0.04f;
            float lastTickTime = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // OutExpo easing: fast start, slow end — feels like Balatro chip counting
                float eased = 1f - Mathf.Pow(2f, -10f * t);
                int display = Mathf.RoundToInt(Mathf.Lerp(from, to, eased));

                if (display != lastDisplay)
                {
                    label.text = display.ToString();

                    // Scale punch on each tick
                    label.transform.localScale = Vector3.one * 1.15f;

                    if (elapsed - lastTickTime >= tickInterval)
                    {
                        float pitch = Mathf.Lerp(0.9f, 1.4f, t);
                        GameAudio.Instance?.PlayScoreTick(pitch);
                        lastTickTime = elapsed;
                    }
                    lastDisplay = display;
                }
                else
                {
                    // Settle scale back toward 1
                    label.transform.localScale = Vector3.Lerp(label.transform.localScale, Vector3.one, Time.deltaTime * 12f);
                }
                yield return null;
            }
            label.text = to.ToString();
            label.transform.localScale = Vector3.one;

            if (isPlayer) _playerCountUp = null;
            else _aiCountUp = null;
        }

        /// <summary>
        /// Forces any running count-up animations to finish immediately.
        /// Call before showing game-over screen to prevent stale HUD values.
        /// </summary>
        public void ForceFinishCountUp()
        {
            if (_playerCountUp != null)
            {
                StopCoroutine(_playerCountUp);
                _playerCountUp = null;
            }
            if (_aiCountUp != null)
            {
                StopCoroutine(_aiCountUp);
                _aiCountUp = null;
            }
            // Snap both labels to authoritative ScoreManager values
            if (ScoreManager.Instance != null)
            {
                if (_playerScoreNum != null)
                    _playerScoreNum.text = ScoreManager.Instance.PlayerScore.ToString();
                if (_aiScoreNum != null)
                    _aiScoreNum.text = ScoreManager.Instance.AIScore.ToString();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Swap Counter
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows remaining swaps for the current active player.
        /// Called by GameVisualBridge.HandleTurnEnd() and HandleSwapUsed().
        /// </summary>
        public void ShowSwapCount(int remaining)
        {
            if (_swapCounterText == null) return;

            _swapCounterText.text = $"SWAP x{remaining}";

            // Dim the text when no swaps remain
            var cfgSw = UIConfig.Instance;
            _swapCounterText.color = remaining > 0
                ? (cfgSw != null ? cfgSw.hudSwapColor : SWAP_COLOR)
                : (cfgSw != null ? cfgSw.hudSwapDimColor : new Color(0.38f, 0.38f, 0.42f, 0.60f));
        }

        /// <summary>
        /// Sets swap count for a specific player index.
        /// Overload required by GameVisualBridge.HandleTurnEnd().
        /// Only updates display (doesn't filter by player — always shows current).
        /// </summary>
        public void SetSwapsRemaining(int player, int count)
        {
            ShowSwapCount(count);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Rewrite Counter
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shows remaining rewrites for the current active player.
        /// </summary>
        public void ShowRewriteCount(int remaining)
        {
            if (_rewriteCounterText == null) return;

            _rewriteCounterText.text = $"EDIT x{remaining}";

            // Dim the text when no rewrites remain
            var cfgRw = UIConfig.Instance;
            _rewriteCounterText.color = remaining > 0
                ? (cfgRw != null ? cfgRw.hudSwapColor : SWAP_COLOR)
                : (cfgRw != null ? cfgRw.hudSwapDimColor : new Color(0.38f, 0.38f, 0.42f, 0.60f));
        }

        /// <summary>
        /// Sets rewrite count for a specific player index.
        /// </summary>
        public void SetRewritesRemaining(int player, int count)
        {
            ShowRewriteCount(count);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Word Found Overlay
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Briefly shows a word-scored popup in the center of the screen.
        /// isPlayer=true → green text, isPlayer=false → orange text.
        /// Auto-fades after ~1.4 seconds.
        /// </summary>
        public void ShowWordFound(string word, int pts, bool isPlayer)
        {
            if (_wordFoundOverlay == null) return;

            if (_wordFoundCoroutine != null)
                StopCoroutine(_wordFoundCoroutine);

            _wordFoundText.text  = $"+{pts}  {word}!";
            _wordFoundText.color = isPlayer ? WORD_POPUP_P1 : WORD_POPUP_AI;

            Image bg = _wordFoundOverlay.GetComponent<Image>();
            if (bg != null) bg.color = new Color(0.04f, 0.04f, 0.06f, 0.84f);

            var cfgDur = UIConfig.Instance;
            _wordFoundOverlay.SetActive(true);
            _wordFoundCoroutine = StartCoroutine(FadeOutWordFound(cfgDur != null ? cfgDur.hudWordFoundDuration : 1.4f));
        }

        private IEnumerator FadeOutWordFound(float totalDuration)
        {
            float holdTime = totalDuration * 0.55f;
            float fadeTime = totalDuration * 0.45f;

            yield return new WaitForSeconds(holdTime);

            if (_wordFoundText == null || _wordFoundOverlay == null)
            {
                _wordFoundCoroutine = null;
                yield break;
            }

            Image bg        = _wordFoundOverlay.GetComponent<Image>();
            Color textStart = _wordFoundText.color;
            Color bgStart   = bg != null ? bg.color : Color.clear;

            float elapsed = 0f;
            while (elapsed < fadeTime)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fadeTime);

                Color tc = textStart;
                tc.a = Mathf.Lerp(1f, 0f, t);
                _wordFoundText.color = tc;

                if (bg != null)
                {
                    Color bc = bgStart;
                    bc.a = Mathf.Lerp(bgStart.a, 0f, t);
                    bg.color = bc;
                }

                yield return null;
            }

            _wordFoundOverlay.SetActive(false);
            _wordFoundCoroutine = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUTTON CALLBACKS
        // ═══════════════════════════════════════════════════════════════════════════

        private void OnResetClicked()
        {
            if (GameManager.Instance == null) return;
            if (GameManager.Instance.CurrentState != GameState.Playing) return;

            AnalyticsManager.ButtonTap("reset");
            GameManager.Instance.RequestReset();
        }

        private void OnMenuClicked()
        {
            if (GameManager.Instance == null) return;
            if (GameManager.Instance.CurrentState != GameState.Playing) return;

            // Safety: restore timeScale in case hitstop was active
            WordDropFX.EnsureTimeScaleRestored();

            // Stop any in-flight coroutines
            if (HandManager.Instance != null)
            {
                HandManager.Instance.StopAllCoroutines();
                HandManager.Instance.SetInteractable(false);
            }

            AnalyticsManager.ButtonTap("menu");
            GameManager.Instance.TransitionTo(GameState.Menu);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // LEGACY STUBS — kept so old callers compile without changes
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Legacy stub — round display removed.</summary>
        public void SetRound(int current, int max) { }

        /// <summary>Legacy stub — routes to SetPlayerScore for backward compat.</summary>
        public void SetScore(int score)
        {
            SetPlayerScore(score);
        }

        /// <summary>Legacy stub — target word blanks removed.</summary>
        public void ResetTargetDisplay(string targetWord) { }

        /// <summary>Legacy stub — target word blanks removed.</summary>
        public void UpdateTargetDisplay() { }

        /// <summary>Legacy stub — target word blanks removed.</summary>
        public void RevealAllTargetLetters() { }

        /// <summary>Legacy stub — combo pill removed.</summary>
        public void SetCombo(int combo, float multiplier) { }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Blitz Mode
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Toggle blitz mode display: hides AI score/label, uses timer instead of turn counter.
        /// </summary>
        public void SetBlitzMode(bool blitz)
        {
            _isBlitzMode = blitz;

            if (_aiScoreText != null)
                _aiScoreText.gameObject.SetActive(!blitz);
            if (_aiScoreNum != null)
                _aiScoreNum.gameObject.SetActive(!blitz);
        }

        /// <summary>
        /// Configures HUD for Daily Drop mode: hides AI score, shows "DAILY DROP" label.
        /// </summary>
        public void SetDailyMode(bool daily)
        {
            if (daily)
            {
                // Hide AI score number, repurpose AI label for "DAILY"
                if (_aiScoreNum != null)  _aiScoreNum.gameObject.SetActive(false);
                if (_aiScoreText != null)
                {
                    _aiScoreText.gameObject.SetActive(true);
                    _aiScoreText.text = "DAILY";
                }

                // Keep P1 label as-is so score displays correctly
                if (_playerScoreText != null) _playerScoreText.text = "P1:";
            }
            else if (!_isBlitzMode)
            {
                // Restore AI elements (unless blitz mode is active)
                if (_aiScoreText != null)
                {
                    _aiScoreText.gameObject.SetActive(true);
                    _aiScoreText.text = "AI";
                }
                if (_aiScoreNum != null) _aiScoreNum.gameObject.SetActive(true);
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // LIVE CONFIG — update existing elements without rebuilding
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by UIConfigEditor when values change during Play mode.
        /// Updates colors, font sizes, and text on existing UI elements.
        /// </summary>
        public void ApplyLiveConfig()
        {
            var cfg = UIConfig.Instance;
            if (cfg == null) return;

            // Bar background
            Transform barT = transform.GetComponentInChildren<Canvas>()?.transform.Find("HUDBar");
            if (barT != null)
            {
                Image barImg = barT.GetComponent<Image>();
                if (barImg != null) barImg.color = cfg.hudBarBgColor;
            }

            // Player score colors
            if (_playerScoreText != null)
            {
                _playerScoreText.color = PLAYER_COLOR;
                _playerScoreText.fontSize = cfg.hudPlayerLabelFontSize;
            }
            if (_playerScoreNum != null)
            {
                _playerScoreNum.color = PLAYER_COLOR;
                _playerScoreNum.fontSize = cfg.hudPlayerNumFontSize;
            }

            // AI score colors
            if (_aiScoreText != null)
            {
                _aiScoreText.color = AI_COLOR;
                _aiScoreText.fontSize = cfg.hudAILabelFontSize;
            }
            if (_aiScoreNum != null)
            {
                _aiScoreNum.color = AI_COLOR;
                _aiScoreNum.fontSize = cfg.hudAINumFontSize;
            }

            // Swap/rewrite counters
            if (_swapCounterText != null)
            {
                _swapCounterText.color = cfg.hudSwapColor;
                _swapCounterText.fontSize = cfg.hudSwapFontSize;
            }
            if (_rewriteCounterText != null)
            {
                _rewriteCounterText.color = cfg.hudSwapColor;
                _rewriteCounterText.fontSize = cfg.hudSwapFontSize;
            }

            // Turn counter
            if (_turnCounterText != null)
                _turnCounterText.fontSize = cfg.hudTurnFontSize;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════════════

        private TextMeshProUGUI MakeLabel(
            Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot,
            Vector2 offMin, Vector2 offMax,
            string text, int size, FontStyle style,
            Color color, TextAnchor align)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);

            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot     = pivot;
            rt.offsetMin = offMin;
            rt.offsetMax = offMax;

            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset uiFont = GameFont.GetUITMP();
            if (uiFont != null) t.font = uiFont;
            t.text     = text;
            t.fontSize = size;
            t.fontStyle = style == FontStyle.Bold ? FontStyles.Bold : FontStyles.Normal;
            t.color    = color;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;

            switch (align)
            {
                case TextAnchor.MiddleLeft:   t.alignment = TextAlignmentOptions.MidlineLeft; break;
                case TextAnchor.MiddleRight:  t.alignment = TextAlignmentOptions.MidlineRight; break;
                case TextAnchor.MiddleCenter: t.alignment = TextAlignmentOptions.Center; break;
                default:                      t.alignment = TextAlignmentOptions.Center; break;
            }

            TMPHelper.ApplyEffects(t, color, TMPHelper.TextTier.HUD);
            return t;
        }

        private static Sprite _resetIconSprite;

        private static Sprite CreateResetIconSprite()
        {
            if (_resetIconSprite != null) return _resetIconSprite;

            int size = 128;
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            Color[] pixels = new Color[size * size];

            float cx = size / 2f;
            float cy = size / 2f;
            float outerR = size * 0.42f;
            float innerR = size * 0.28f;
            float gapAngleStart = -0.4f; // radians — gap at top-right
            float gapAngleEnd = 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float angle = Mathf.Atan2(dy, dx);

                    // Ring shape with gap
                    bool inRing = dist >= innerR && dist <= outerR;
                    bool inGap = angle > gapAngleStart && angle < gapAngleEnd;

                    if (inRing && !inGap)
                        pixels[y * size + x] = Color.white;
                    else
                        pixels[y * size + x] = Color.clear;

                    // Arrow head at the gap start (pointing clockwise)
                    float arrowCx = cx + Mathf.Cos(gapAngleStart) * (innerR + outerR) * 0.5f;
                    float arrowCy = cy + Mathf.Sin(gapAngleStart) * (innerR + outerR) * 0.5f;
                    float adx = x - arrowCx;
                    float ady = y - arrowCy;

                    // Triangle arrow pointing downward-right
                    float rotAngle = gapAngleStart + 1.2f;
                    float rx = adx * Mathf.Cos(rotAngle) + ady * Mathf.Sin(rotAngle);
                    float ry = -adx * Mathf.Sin(rotAngle) + ady * Mathf.Cos(rotAngle);

                    float arrowSize = size * 0.15f;
                    float ny = ry / arrowSize;
                    float halfW = (1f - Mathf.Clamp01(ny)) * 0.5f;
                    float nxNorm = rx / arrowSize;

                    if (ny >= -0.2f && ny <= 1f && Mathf.Abs(nxNorm) < halfW)
                        pixels[y * size + x] = Color.white;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            _resetIconSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _resetIconSprite;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Survival Mode HUD
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called every frame by SurvivalManager.Update() to refresh the HUD.
        /// Shows elapsed time and next auto-drop countdown in the turn counter area.
        /// </summary>
        public void UpdateSurvivalHUD(SurvivalManager sm)
        {
            if (sm == null || _turnCounterText == null) return;

            int   stage          = sm.GetCurrentStage();
            int   stageScore     = sm.CurrentStageScore;
            int   stageTarget    = sm.CurrentStageTarget;
            int   stageMovesLeft = sm.CurrentStageMovesRemaining;
            float riseSecondsLeft = sm.RisingRowSecondsRemaining;

            // Charge counts — replacing the "N moves" readout per Phase 11b+
            // feedback. Moves-left is still available for danger-color logic
            // below (we want red when budget is near exhausted), but the
            // visible text now shows remaining edits/swaps because those are
            // the player-facing resources they actually manage.
            int edits = 0;
            int swaps = 0;
            if (MatchController.Instance != null)
            {
                edits = MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN);
                swaps = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
            }

            // Primary HUD: stage progress + charge counts + rise countdown.
            // Phase 11b — rise countdown is wall-clock seconds, not moves.
            // Ceiling the displayed number so "RISE in 1s" covers 0.0-1.0s and
            // the text never shows "0s" while the timer is still approaching.
            int riseCountdown = Mathf.CeilToInt(riseSecondsLeft);
            _turnCounterText.text =
                $"S{stage} {stageScore}/{stageTarget}  |  {edits} edits · {swaps} swaps  |  RISE in {riseCountdown}s";

            // Color logic:
            // - If stage already cleared this round → green (safe, bonus moves)
            // - If behind pace + running out of moves → red
            // - If behind pace but moves OK → amber
            // - Otherwise survival cyan
            bool cleared = sm.IsCurrentStageCleared;
            float requiredPerMove = stageMovesLeft > 0
                ? (float)(stageTarget - stageScore) / stageMovesLeft
                : 0f;

            if (cleared)
            {
                _turnCounterText.color = new Color(0.4f, 1f, 0.5f, 1f); // green — already cleared
            }
            else if (stageMovesLeft <= 3 && stageScore < stageTarget)
            {
                _turnCounterText.color = TURN_DANGER; // red — about to fail
            }
            else if (requiredPerMove > 100f)
            {
                _turnCounterText.color = TURN_WARN; // amber — behind pace
            }
            else if (riseSecondsLeft <= 2f)
            {
                _turnCounterText.color = TURN_DANGER; // red — rise imminent (≤2s)
            }
            else if (riseSecondsLeft <= 5f)
            {
                _turnCounterText.color = TURN_WARN; // amber — rise soon (≤5s)
            }
            else
            {
                _turnCounterText.color = new Color(0.1f, 0.85f, 0.9f, 1f); // survival cyan
            }
        }

        internal static Font GetFont()
        {
            Font f = GameFont.GetUI();
            if (f != null) return f;
            f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }
    }
}
