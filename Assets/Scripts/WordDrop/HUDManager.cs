using System.Collections;
using System.Collections.Generic;
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
        // 2026-05-28: progress bar replaces text readout in survival HUD.
        private TextMeshProUGUI _stageLabelText;       // "S1", "S2", ...
        private UnityEngine.UI.Image _stageProgressFill;
        private RectTransform _stageProgressFillRT;
        private GameObject     _stageProgressBG;        // bar background (toggled off in objective mode; bar is endless-only)
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

        // ── Coin counter (MVP P1) ────────────────────────────────────────────────
        // Reads from CoinWallet.Balance. Lives in upper-right slot shared with
        // AI/MOVES counters — Survival's HideAIScore() makes room for it.
        private TextMeshProUGUI _coinCounterText;

        // ── Top-out danger indicator (Survival) ───────────────────────────────────
        // Countdown of MOVES until the board tops out (rises × cadence). A white
        // bubble loops behind it (same "pop" language as tier-1 explosions) only when
        // imminent. Created in BuildHUD, refreshed each frame in Update().
        private TextMeshProUGUI _topOutNumText;
        private Image           _topOutBubble;
        private Coroutine       _topOutBubbleLoop;
        private int             _topOutLastShown = int.MinValue;
        private Sprite          _bubbleSpriteCache;
        private int             _topOutDisplay   = int.MaxValue; // monotonic-clamped value actually shown
        private int             _topOutLastStrict = -1;          // last headroom; an increase = a clear → allow the count to rise
        private const int       TOPOUT_DANGER_THRESHOLD = 4; // moves; bubble fires at/under this

        // ── Objective readout (the level's goal + progress) ───────────────────────
        private TextMeshProUGUI _objectiveText;       // fallback for objectives with no icon (Icon==None)
        private Image           _objectiveCheck;     // green check, pops in on objective complete

        // ── In-game TARGET: objective icon + COUNT-DOWN badge (replaces the "Explode words 0/3"
        // text). Icon rebuilt only when the objective TYPE changes; badge number = RemainingCount,
        // ticking down as the player progresses; on completion the check drops onto the badge and
        // replaces the number. 2026-06-15 Spencer. ──
        private GameObject        _objectivePanel;      // "TARGET" framed box (left)
        private GameObject        _objectiveIconHolder;
        private GameObject        _objectiveIconGO;     // the built icon (rebuilt on type change)
        private TextMeshProUGUI   _objectivePanelLabel; // "TARGET" (→ "REWARD" on vault levels)
        private int               _displayedReward;     // badge number; ticks UP toward VaultObjective.RewardCoins as coins land
        private GameObject        _objectiveBadge;      // circle behind the count / check
        private TextMeshProUGUI   _objectiveBadgeText;  // the remaining-count number
        private Objective.HudIcon _shownObjectiveIcon = (Objective.HudIcon)(-1); // sentinel → first set always builds
        private string _shownIconWord; // Word icons share HudIcon.Word but differ by spelled word (WORD/FOUR/WORDS) — track so a length change rebuilds
        private GameObject        _movesPanel;          // "MOVES" framed box (right) — hosts the top-out counter
        private GameObject        _levelPanel;          // "LEVEL" framed box (center) — current level = the run goal
        private TextMeshProUGUI   _levelNumText;        // current level number
        private Sprite          _checkSpriteCache;

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
        private static readonly Color BAR_BG          = new Color(0.10f, 0.27f, 0.50f, 1.0f); // 2026-06-02: deep ocean blue (was #391D78 purple) — candy-palette chrome, cohesive with cyan bg + blue board
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
            CoinWallet.OnBalanceChanged -= HandleCoinBalanceChanged;
            CoinWallet.OnBalanceChanged += HandleCoinBalanceChanged;
        }

        private void HandleCoinBalanceChanged(int newBalance)
        {
            SetCoins(newBalance);
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
            CoinWallet.OnBalanceChanged -= HandleCoinBalanceChanged;
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

            // 2026-06-24 Spencer: fill the notch / status-bar strip ABOVE the safe area with the HUD bar
            // colour, so the top reads as ONE continuous bar to the screen edge instead of "bar, then teal
            // background showing above it." Lives on the canvas (outside the safe area) so it can reach the
            // true top; overlaps a few px into the bar to avoid a hairline seam. Zero height on notch-less
            // devices (anchorMax.y == 1), so it's harmless there.
            {
                var topFillGO = new GameObject("HUDTopFill", typeof(RectTransform), typeof(Image));
                topFillGO.transform.SetParent(canvasGO.transform, false);
                topFillGO.transform.SetAsFirstSibling(); // behind the safe-area content
                var topFillRT = topFillGO.GetComponent<RectTransform>();
                topFillRT.anchorMin = new Vector2(0f, anchorMax.y); // safe-area top
                topFillRT.anchorMax = new Vector2(1f, 1f);          // screen top
                topFillRT.offsetMin = new Vector2(0f, -6f);         // overlap into the bar — no seam
                topFillRT.offsetMax = Vector2.zero;
                var topFillImg = topFillGO.GetComponent<Image>();
                topFillImg.color = cfg != null ? cfg.hudBarBgColor : BAR_BG;
                topFillImg.raycastTarget = false;
            }

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
            // Force default UI material — guards against a stray Inspector
            // drag-drop (e.g. the FeelSnake particle material wandering in)
            // from changing the bar's look at runtime.
            barImg.material = null;

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

            // 2026-06-01: hide the YOU score + label per Spencer — Survival HUD
            // uses the Stage Score chip + coin counter; the "0" leftover from
            // the older HUD layout is no longer needed.
            if (_playerScoreText != null) _playerScoreText.gameObject.SetActive(false);
            if (_playerScoreNum  != null) _playerScoreNum.gameObject.SetActive(false);

            // ── Top-out danger indicator (Survival) — moves-to-top-out + white pulse ──
            // Sits in the freed top-left slot. Bubble created FIRST so it renders behind
            // the number. Both start hidden; Update() shows them in Survival mode.
            {
                var bubbleGO = new GameObject("TopOutDangerBubble", typeof(RectTransform), typeof(Image));
                bubbleGO.transform.SetParent(barGO.transform, false);
                var bubRT = bubbleGO.GetComponent<RectTransform>();
                bubRT.anchorMin = new Vector2(0.085f, 0.5f);
                bubRT.anchorMax = new Vector2(0.085f, 0.5f);
                bubRT.pivot     = new Vector2(0.5f, 0.5f);
                bubRT.sizeDelta = new Vector2(70f, 70f);
                _topOutBubble = bubbleGO.GetComponent<Image>();
                // Soft WHITE circle (Circle04 from Hyper Casual FX — Spencer 2026-06-15). NOT the grey
                // bubble@2x (tint multiplies, so a grey sprite can't go white).
                _topOutBubble.sprite        = LoadBubbleSprite();
                _topOutBubble.raycastTarget = false;
                // White circle; the pulse loop animates alpha (peak ~0.85) so it reads on the cream panel.
                _topOutBubble.color         = new Color(1f, 1f, 1f, 0f);
                bubbleGO.SetActive(false);

                _topOutNumText = MakeLabel(barGO.transform, "TopOutNum",
                    anchorMin: new Vector2(0.02f, 0.05f),
                    anchorMax: new Vector2(0.15f, 0.95f),
                    pivot:     new Vector2(0.5f, 0.5f),
                    offMin:    Vector2.zero, offMax: Vector2.zero,
                    text:      "",
                    size:      28,
                    style:     FontStyle.Bold,
                    color:     Color.white,
                    align:     TextAnchor.MiddleCenter);
                _topOutNumText.gameObject.SetActive(false);
            }

            // ── Objective readout — the level goal + progress. 2026-06-15: PROMOTED into
            // the center HUD slot (where the score progress bar used to live). Royal Match
            // has no progress bar — the objective/target IS the progress. The bar now only
            // shows in endless (no-objective) mode; SetObjective() toggles between them.
            // Band ENDS at 0.66 (well before the coin counter at 0.72+) and STARTS at 0.21 (after the
            // score at ~0.20). Auto-sizing (12–18) shrinks long titles to fit instead of bleeding into
            // the coins — so "Drop to the bottom 0/2" can never overlap the gold. 2026-06-15 Spencer.
            _objectiveText = MakeLabel(barGO.transform, "ObjectiveText",
                anchorMin: new Vector2(0.21f, 0.08f),
                anchorMax: new Vector2(0.66f, 0.92f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "",
                size:      18,
                style:     FontStyle.Bold,
                color:     Color.white,
                align:     TextAnchor.MiddleCenter);
            _objectiveText.enableAutoSizing = true;
            _objectiveText.fontSizeMin = 12f;
            _objectiveText.fontSizeMax = 18f;
            _objectiveText.enableWordWrapping = false;
            _objectiveText.overflowMode = TMPro.TextOverflowModes.Ellipsis;
            _objectiveText.gameObject.SetActive(false);

            // ── TARGET / LEVEL / MOVES panels (Royal Match layout). 2026-06-15 Spencer.
            //    No score on the HUD (goal = highest LEVEL reached per run). Target = objective icon +
            //    count badge; Level = the number you're climbing; Moves = moves-to-top-out (the existing
            //    counter, reparented). Panels stay WITHIN the bar so the rising board never overlaps them.
            var INK = new Color(0.16f, 0.16f, 0.28f, 1f);

            // TARGET (left) — objective icon + count badge.
            // 2026-06-24 Spencer: TARGET widened (0.33 → 0.63) into the space freed by removing the LEVEL
            // box — Royal-Match-style big target field, room for bigger words AND a future stacked-icon grid.
            _objectivePanel = BuildHudPanel(barGO.transform, "TargetPanel", 0.02f, 0.63f, "TARGET", out var tgtInner);
            _objectivePanelLabel = _objectivePanel.transform.Find("TargetPanelLabel")?.GetComponent<TextMeshProUGUI>();
            {
                var holderGO = new GameObject("ObjectiveIconHolder", typeof(RectTransform));
                holderGO.transform.SetParent(tgtInner, false);
                var holdRT = holderGO.GetComponent<RectTransform>();
                holdRT.anchorMin = holdRT.anchorMax = new Vector2(0.5f, 0.52f);
                holdRT.pivot = new Vector2(0.5f, 0.5f);
                holdRT.sizeDelta = new Vector2(34f, 34f);
                _objectiveIconHolder = holderGO;

                var badgeGO = new GameObject("ObjectiveBadge", typeof(RectTransform), typeof(Image));
                badgeGO.transform.SetParent(holderGO.transform, false);
                var bRT = badgeGO.GetComponent<RectTransform>();
                bRT.anchorMin = bRT.anchorMax = new Vector2(1f, 0f); // icon's bottom-right corner
                bRT.pivot = new Vector2(0.5f, 0.5f);
                bRT.sizeDelta = new Vector2(30f, 30f);
                bRT.anchoredPosition = new Vector2(3f, -2f);          // overhang the corner, Royal Match style
                var bImg = badgeGO.GetComponent<Image>();
                bImg.color = new Color(0f, 0f, 0f, 0f);               // NO circle — bare outlined number like RM (Spencer 2026-06-15)
                bImg.raycastTarget = false;
                _objectiveBadge = badgeGO;

                _objectiveBadgeText = MakeLabel(badgeGO.transform, "BadgeNum",
                    anchorMin: Vector2.zero, anchorMax: Vector2.one,
                    pivot: new Vector2(0.5f, 0.5f), offMin: Vector2.zero, offMax: Vector2.zero,
                    text: "", size: 20, style: FontStyle.Bold, color: Color.white, align: TextAnchor.MiddleCenter);
                _objectiveBadgeText.enableAutoSizing = true; _objectiveBadgeText.fontSizeMin = 10f; _objectiveBadgeText.fontSizeMax = 26f;
                if (heavyFont != null) _objectiveBadgeText.font = heavyFont; // 2026-06-24: same HUD number font as MOVES/score/coins
                _objectiveBadgeText.fontStyle    = FontStyles.Bold;                  // RM numbers are bold (Montserrat atlas is clean → safe)
                _objectiveBadgeText.outlineWidth = 0.22f;                            // a little thicker than the old 0.2 — 0.32 melted the small glyph (SDF spread overflows at this size). Spencer 2026-06-24
                _objectiveBadgeText.outlineColor = new Color32(20, 28, 55, 255);

                var checkGO = new GameObject("ObjectiveCheck", typeof(RectTransform), typeof(Image));
                checkGO.transform.SetParent(badgeGO.transform, false);
                var ckRT = checkGO.GetComponent<RectTransform>();
                ckRT.anchorMin = Vector2.zero; ckRT.anchorMax = Vector2.one;
                ckRT.offsetMin = new Vector2(2f, 2f); ckRT.offsetMax = new Vector2(-2f, -2f);
                _objectiveCheck = checkGO.GetComponent<Image>();
                _objectiveCheck.sprite = LoadCheckSprite();
                _objectiveCheck.color = new Color(0.4f, 1f, 0.45f);
                _objectiveCheck.preserveAspect = true;
                _objectiveCheck.raycastTarget = false;
                checkGO.SetActive(false);
            }
            _objectivePanel.SetActive(false);

            // LEVEL (center) — the run goal: current level number.
            _levelPanel = BuildHudPanel(barGO.transform, "LevelPanel", 0.385f, 0.615f, "LEVEL", out var lvlInner);
            _levelNumText = MakeLabel(lvlInner, "LevelNum",
                anchorMin: Vector2.zero, anchorMax: Vector2.one,
                pivot: new Vector2(0.5f, 0.5f), offMin: Vector2.zero, offMax: Vector2.zero,
                text: "1", size: 26, style: FontStyle.Bold, color: INK, align: TextAnchor.MiddleCenter);
            _levelNumText.enableAutoSizing = true; _levelNumText.fontSizeMin = 10f; _levelNumText.fontSizeMax = 30f;
            if (heavyFont != null) _levelNumText.font = heavyFont;
            _levelPanel.SetActive(false);

            // MOVES (right) — reparent the existing moves-to-top-out counter into the panel.
            _movesPanel = BuildHudPanel(barGO.transform, "MovesPanel", 0.67f, 0.98f, "MOVES", out var mvInner);
            if (_topOutNumText != null)
            {
                var trt = _topOutNumText.rectTransform;
                trt.SetParent(mvInner, false);
                trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
                trt.offsetMin = Vector2.zero; trt.offsetMax = Vector2.zero;
                _topOutNumText.alignment = TMPro.TextAlignmentOptions.Center;
                _topOutNumText.color = Color.white;                       // 2026-06-24: match the count badge — white fill...
                _topOutNumText.outlineWidth = 0.30f;                      // ...with a chunkier dark stroke. The big MOVES glyph can take more than the tiny badge (0.22) without melting. Spencer 2026-06-24
                _topOutNumText.outlineColor = new Color32(20, 28, 55, 255);
                if (heavyFont != null) _topOutNumText.font = heavyFont; // 2026-06-24: match every other HUD number's font (was the default UI font → looked off vs TARGET/score/coins)
                _topOutNumText.enableAutoSizing = true; _topOutNumText.fontSizeMin = 10f; _topOutNumText.fontSizeMax = 30f;
            }
            if (_topOutBubble != null)
            {
                var brt = _topOutBubble.rectTransform;
                brt.SetParent(mvInner, false);
                brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(66f, 66f);
                brt.anchoredPosition = Vector2.zero;
                brt.SetAsFirstSibling(); // render BEHIND the Moves number
            }
            _movesPanel.SetActive(false);

            // ── Center: Stage label + progress bar ───────────────────────────────
            // 2026-05-28: replaced "S1 204/400" text readout with a horizontal
            // progress bar that fills as the player closes on the stage target.
            // More visceral than numbers — players read fill % at a glance.
            // The _turnCounterText is kept (zero-size, invisible) so legacy
            // callers that set its .text don't NPE; the bar derives its fill
            // from CurrentStageScore / CurrentStageTarget instead.
            _turnCounterText = MakeLabel(barGO.transform, "TurnCounter",
                anchorMin: new Vector2(0f, 0f),
                anchorMax: new Vector2(0f, 0f),
                pivot:     new Vector2(0f, 0f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "",
                size:      1,
                style:     FontStyle.Bold,
                color:     new Color(0f, 0f, 0f, 0f),
                align:     TextAnchor.MiddleCenter);
            _turnCounterText.gameObject.SetActive(false);

            // 2026-05-28 v3: bar widened so progress is visible at low fill.
            //   Score:  0.08 → 0.22  (existing left)
            //   S1:     0.23 → 0.29  (compact, just before bar)
            //   Bar:    0.30 → 0.70  (centered on 0.50, 40% wide — was 24%)
            //   Coins:  0.72+ (existing right)
            _stageLabelText = MakeLabel(barGO.transform, "StageLabel",
                anchorMin: new Vector2(0.23f, 0.10f),
                anchorMax: new Vector2(0.29f, 0.90f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "S1",
                size:      20,
                style:     FontStyle.Bold,
                color:     new Color(0.65f, 0.92f, 0.98f, 1f),
                align:     TextAnchor.MiddleCenter);
            if (heavyFont != null) _stageLabelText.font = heavyFont;

            // Bar background: pill-shaped, narrow + thin. Both BG and fill
            // use the SAME rounded-pill sprite (radius = h/2). Fill is a
            // child of BG sized via its right anchor (anchorMax.x =
            // stageScore/stageTarget). Both ends of the fill stay curved
            // because the sprite itself is a pill — looks like a smaller
            // pill growing inside the bigger one. No Mask needed.
            Sprite pillSprite = TileRenderer.CreateSolidRoundedRect(200, 60, 30, Color.white);

            var bgGO = new GameObject("StageProgressBG",
                typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(barGO.transform, false);
            var bgRT = bgGO.GetComponent<RectTransform>();
            bgRT.anchorMin = new Vector2(0.30f, 0.41f);
            bgRT.anchorMax = new Vector2(0.70f, 0.59f);
            bgRT.offsetMin = Vector2.zero;
            bgRT.offsetMax = Vector2.zero;
            var bgImg = bgGO.GetComponent<Image>();
            bgImg.sprite = pillSprite;
            bgImg.color = new Color(0.10f, 0.12f, 0.22f, 0.92f);
            bgImg.raycastTarget = false;
            _stageProgressBG = bgGO;

            // Fill: child of BG, anchored to BG's left edge. anchorMax.x is
            // driven by the score ratio so the fill grows from left to right.
            // Starts at 0 (empty pill).
            var fillGO = new GameObject("StageProgressFill",
                typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(bgGO.transform, false);
            var fillRT = fillGO.GetComponent<RectTransform>();
            fillRT.anchorMin = new Vector2(0f, 0f);
            fillRT.anchorMax = new Vector2(0f, 1f); // width = 0 at start (empty)
            fillRT.pivot     = new Vector2(0f, 0.5f);
            fillRT.offsetMin = Vector2.zero;
            fillRT.offsetMax = Vector2.zero;
            _stageProgressFill = fillGO.GetComponent<Image>();
            _stageProgressFill.sprite = pillSprite;
            _stageProgressFill.color = new Color(0.40f, 0.90f, 0.95f, 1f);
            _stageProgressFill.raycastTarget = false;
            _stageProgressFillRT = fillRT;

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

            // ── Coin counter (MVP P1) — upper-right, gold ────────────────────────
            // Uses the same right-side slot as AI/MOVES. Visible by default;
            // hidden in modes that explicitly need the AI score visible.
            _coinCounterText = MakeLabel(barGO.transform, "CoinCounter",
                anchorMin: new Vector2(0.72f, 0.05f),
                anchorMax: new Vector2(0.92f, 0.65f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    Vector2.zero, offMax: Vector2.zero,
                text:      "● 0",
                size:      20,
                style:     FontStyle.Bold,
                color:     PLAYER_COLOR,
                align:     TextAnchor.MiddleRight);
            if (heavyFont != null) _coinCounterText.font = heavyFont;
            // Sync initial value from wallet so HUD reflects persistent balance on boot.
            _coinCounterText.text = $"● {CoinWallet.Balance}";

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
            // 2026-06-01: menu button suppressed per Spencer's screenshot —
            // settings access moved to the BoosterHUDSlot cog (bottom row).
            // Kept as a no-op so any other call sites that bootstrap the HUD
            // don't blow up on a missing build.
            return;
#pragma warning disable CS0162 // unreachable code — intentional, see comment above
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
#pragma warning restore CS0162
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

        /// <summary>MVP P1: update coin counter display with a small pop animation.
        /// Wired automatically via CoinWallet.OnBalanceChanged.
        ///
        /// Uses DOPunchScale (NOT the player/AI score's coroutine-based punch) so
        /// the animation is interruption-safe — DOPunchScale auto-restores scale
        /// to its start value on Kill, and SetUpdate(true) runs on unscaled time
        /// so a modal opening / time-scale shenanigans can't leave the counter
        /// stuck mid-punch.</summary>
        public void SetCoins(int balance)
        {
            if (_coinCounterText == null) return;
            _coinCounterText.text = $"● {balance}";
            var rt = _coinCounterText.rectTransform;
            rt.DOKill();
            rt.localScale = Vector3.one;
            rt.localRotation = Quaternion.identity;
            rt.DOPunchScale(Vector3.one * 0.25f, 0.30f, 1, 0.5f).SetUpdate(true);
        }

        /// <summary>Hide coin counter (e.g. modes that need the AI slot back).</summary>
        public void HideCoins()
        {
            if (_coinCounterText != null) _coinCounterText.gameObject.SetActive(false);
        }

        /// <summary>Show coin counter (Survival default).</summary>
        public void ShowCoins()
        {
            if (_coinCounterText != null) _coinCounterText.gameObject.SetActive(true);
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

        // ── HiddenWord fly-up ────────────────────────────────────────────────────
        /// <summary>Screen-space position of the i-th blank slot in the Target panel's hidden-word row.
        /// The row's slots ("R0".."Rn") are the direct children of the objective icon GO. Falls back to
        /// the player-score corner. 2026-06-17 Spencer.</summary>
        public Vector3 GetHiddenSlotScreenPos(int slotIndex)
        {
            if (_objectiveIconGO != null)
            {
                // Find by NAME (the row also has glow children that shift indices). 2026-06-17 Spencer.
                var slot = _objectiveIconGO.transform.Find($"R{slotIndex}");
                if (slot is RectTransform rt) return rt.position; // overlay → RectTransform.position is screen px
            }
            return GetPlayerScoreWorldPos();
        }

        /// <summary>HiddenWord polish: a matched letter pops (wild-style), then arcs up to its blank slot
        /// in the Target panel trailing sparkles and lands with a pop. Cosmetic — the rock→letter reveal is
        /// driven separately by the objective. Modeled on BonusPopup's score-fly. 2026-06-17 Spencer.</summary>
        /// <summary>Screen-space centre of the objective ICON (for escort tiles flying up to it).</summary>
        public Vector3 GetObjectiveIconScreenPos()
        {
            if (_objectiveIconHolder != null) return _objectiveIconHolder.transform.position;
            return GetPlayerScoreWorldPos();
        }

        // HiddenWord: a matched letter flies up and lands in its blank slot (reveal + pop on arrival).
        public void FlyHiddenLetterToSlot(Vector3 startWorld, char letter, int slotIndex, System.Action onLand = null, float startDelay = 0f)
        {
            if (!isActiveAndEnabled || UIAnimations.ReducedMotion || Camera.main == null) { onLand?.Invoke(); return; }
            Vector3 target = GetHiddenSlotScreenPos(slotIndex);
            System.Action onArrive = () =>
            {
                onLand?.Invoke();
                GameAudio.Instance?.PlayLine2();
                var slotT = _objectiveIconGO != null ? _objectiveIconGO.transform.Find($"R{slotIndex}") : null;
                if (slotT != null)
                {
                    BringTargetChainToFront();
                    slotT.SetAsLastSibling();
                    UIAnimations.WildCardPop(slotT, Vector3.one);
                    if (slotT is RectTransform srt && _objectiveIconGO.transform is RectTransform holderRT)
                    {
                        SpawnSlotGlow(holderRT, srt.anchoredPosition, srt.sizeDelta.x);
                        SpawnSlotSparkleBurst(holderRT, srt.anchoredPosition);
                    }
                }
            };
            StartCoroutine(FlyTileCoroutine(startWorld, Tile.PrimedSprite, new Color(1.7f, 0.7f, 1.5f, 1f),
                new Color(1.9f, 0.45f, 1.6f, 1f), letter, target, onArrive, startDelay));
        }

        // HeroWord: a collected escort tile flies up and lands ON the Target icon (same animation as the
        // HiddenWord letters — pops the icon + sparkle burst on arrival). 2026-06-17 Spencer.
        public void FlyEscortToTarget(Vector3 startWorld, System.Action onLand = null, float startDelay = 0f)
        {
            if (!isActiveAndEnabled || UIAnimations.ReducedMotion || Camera.main == null) { onLand?.Invoke(); return; }
            Vector3 target = GetObjectiveIconScreenPos();
            System.Action onArrive = () =>
            {
                onLand?.Invoke();
                GameAudio.Instance?.PlayLine2();
                if (_objectiveIconGO != null)
                {
                    BringTargetChainToFront();
                    _objectiveIconGO.transform.SetAsLastSibling();
                    UIAnimations.WildCardPop(_objectiveIconGO.transform, Vector3.one);
                    if (_objectiveIconHolder != null && _objectiveIconHolder.transform is RectTransform hrt)
                        SpawnSlotSparkleBurst(hrt, Vector2.zero); // icon sits at the holder centre
                }
            };
            // Fly the CHICKEN sprite up (white tint) with a GOLD glow (not orange). 2026-06-19 Spencer.
            StartCoroutine(FlyTileCoroutine(startWorld, Tile.ChickenSprite ?? Tile.NormalSprite, Color.white,
                new Color(1.7f, 1.3f, 0.3f, 1f), '\0', target, onArrive, startDelay));
        }

        /// <summary>UI renders by hierarchy order, so bring the whole Target chain to the front before a
        /// landing pop, or it draws under the panel frame / neighbouring panels.</summary>
        private void BringTargetChainToFront()
        {
            if (_objectivePanel != null) _objectivePanel.transform.SetAsLastSibling();
            if (_objectiveIconHolder != null)
            {
                if (_objectiveIconHolder.transform.parent != null) _objectiveIconHolder.transform.parent.SetAsLastSibling();
                _objectiveIconHolder.transform.SetAsLastSibling();
            }
            if (_objectiveIconGO != null) _objectiveIconGO.transform.SetAsLastSibling();
            // Badge (the count number) LAST so it draws on TOP of the icon — bringing the icon to front
            // above was hiding the number behind the coin. 2026-06-18 Spencer.
            if (_objectiveBadge != null) _objectiveBadge.transform.SetAsLastSibling();
        }

        // ── REWARD COIN COLLECT (vault levels) — Royal Match style ────────────────────────────────
        // A cracked chest spits a handful of coins that SCATTER ballistically, then GATHER up to the
        // REWARD counter with a sparkle trail; the displayed total ticks up as each coin lands. The
        // currency is already banked (VaultObjective.RewardCoins) — these coins drive _displayedReward.
        // 2026-06-18 Spencer.
        private Sprite _coinSpriteCache; private bool _coinSpriteTried;
        private Sprite GetCoinSprite()
        {
            if (_coinSpriteTried) return _coinSpriteCache;
            _coinSpriteTried = true;
            _coinSpriteCache = Resources.Load<Sprite>("Tiles/Icon_ImageIcon_Coin");
            if (_coinSpriteCache == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/Icon_ImageIcon_Coin");
                if (tex != null) _coinSpriteCache = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _coinSpriteCache;
        }

        // VFX_Coin_rotation is a 4×4 flipbook of a gold coin doing a full 3D spin. Slice it into 16
        // ordered frames (PPU 200 so a 128px frame ≈ the old 64px coin size, preserving COIN_SCALE).
        // Cached. Returns null if the sheet can't be loaded → caller falls back to a rotated flat sprite.
        private Sprite[] _coinFramesCache; private bool _coinFramesTried;
        private Sprite[] GetCoinFrames()
        {
            if (_coinFramesTried) return _coinFramesCache;
            _coinFramesTried = true;
            var tex = Resources.Load<Texture2D>("Coins/VFX_Coin_rotation");
            if (tex == null) return null;
            const int COLS = 4, ROWS = 4;
            int fw = tex.width / COLS, fh = tex.height / ROWS;
            var frames = new Sprite[COLS * ROWS];
            int idx = 0;
            for (int r = 0; r < ROWS; r++)           // r=0 = TOP row → reading order = spin sequence
                for (int c = 0; c < COLS; c++)
                {
                    int px = c * fw, py = (ROWS - 1 - r) * fh; // texture origin is bottom-left
                    frames[idx++] = Sprite.Create(tex, new Rect(px, py, fw, fh), new Vector2(0.5f, 0.5f), 200f);
                }
            _coinFramesCache = frames;
            return frames;
        }

        // ONE shared trail material for ALL reward coins — building `new Material()` per coin was a real
        // GC/perf hit (and leaked until GC). Built once from the sprite shader. 2026-06-18 Spencer.
        private Material _coinTrailMat;
        private Material GetCoinTrailMat(Material spriteMat)
        {
            if (_coinTrailMat == null && spriteMat != null)
                _coinTrailMat = new Material(spriteMat.shader) { mainTexture = GetGlowSprite().texture };
            return _coinTrailMat;
        }

        // Additive + HDR gold material for the per-coin glow halo (so it blooms — "glow coming off the coins").
        private Material _coinGlowMat;
        private Material GetCoinGlowMat()
        {
            if (_coinGlowMat == null)
            {
                var sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
                _coinGlowMat = new Material(sh);
                _coinGlowMat.SetColor("_Color", new Color(1.1f, 0.85f, 0.3f)); // gentle gold (toned way down)
            }
            return _coinGlowMat;
        }

        // Coin-trail material that BLOOMS: ADDITIVE shader + an HDR _Color tint (a TrailRenderer's gradient
        // bakes to clamped [0,1] vertex colours, so the only way past the bloom threshold is the material
        // tint). Coin-specific so the HiddenWord/escort trails (GetCoinTrailMat) are untouched. 2026-06-18.
        private Material _coinTrailMatHDR;
        private Material GetCoinTrailMatHDR()
        {
            if (_coinTrailMatHDR == null)
            {
                var sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
                _coinTrailMatHDR = new Material(sh);
                var trailTex = Resources.Load<Texture2D>("Particles/VFX_Trail_1"); // tapered comet streak
                _coinTrailMatHDR.mainTexture = trailTex != null ? trailTex : GetGlowSprite().texture;
                _coinTrailMatHDR.SetColor("_Color", new Color(2.8f, 2.2f, 0.9f)); // HDR gold → blooms hard
            }
            return _coinTrailMatHDR;
        }

        /// <summary>Spit a handful of reward coins out of a cracked chest at <paramref name="worldStart"/>.
        /// They scatter then fly to the REWARD counter, ticking <paramref name="coinValue"/> onto the
        /// displayed total as they land (split across the visual coins). tier scales the coin count.</summary>
        public void SpawnRewardCoinBurst(Vector3 worldStart, int coinValue, int tier)
        {
            if (coinValue <= 0) return;
            if (!isActiveAndEnabled || UIAnimations.ReducedMotion || Camera.main == null)
            {
                _displayedReward += coinValue; RefreshObjectiveBadgeNumber(); // no anim → snap the display
                return;
            }
            int coins = tier >= 5 ? 9 : (tier > 0 ? 7 : 5);       // a handful, NOT the full value (trimmed for perf)
            coins = Mathf.Clamp(coins, 1, coinValue);
            int per = coinValue / coins, remainder = coinValue - per * coins;
            for (int i = 0; i < coins; i++)
            {
                int share = per + (i == coins - 1 ? remainder : 0); // last coin carries the remainder
                StartCoroutine(RewardCoinCoroutine(worldStart, share, i));
            }
        }

        private System.Collections.IEnumerator RewardCoinCoroutine(Vector3 worldStart, int share, int index)
        {
            var cam = Camera.main; if (cam == null) { OnRewardCoinLanded(share); yield break; }
            var go = new GameObject("RewardCoin");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 205;
            // Prefer the VFX_Coin_rotation FLIPBOOK (a real 3D coin spin) over a flat sprite we rotate.
            Sprite[] frames = GetCoinFrames();
            bool flip = frames != null && frames.Length > 0;
            if (flip) sr.sprite = frames[0];
            else { var sp = GetCoinSprite(); sr.sprite = sp; if (sp == null) sr.color = new Color(1f, 0.82f, 0.2f); }
            const float COIN_SCALE = 1.7f; // bigger coins (Spencer 2026-06-19)
            const float SPIN_FPS   = 16f;   // flipbook playback rate
            float spinT = 0f;
            go.transform.position = worldStart;
            go.transform.localScale = Vector3.one * (COIN_SCALE * 0.5f); // start at HALF size → pops to full as it shoots out
            const float POP_DUR = 0.14f; // how fast the coin grows to full size on launch

            SpriteRenderer coinGlowSR = null; // per-coin glow halo removed (was too much) 2026-06-18 Spencer

            // ── Phase 1: explosion DEBRIS — burst outward (some up, some out) and FALL under gravity,
            //    so the coins read as part of the blast before they gather up. ──
            Vector2 dir = UnityEngine.Random.insideUnitCircle;
            if (dir.sqrMagnitude < 0.02f) dir = Vector2.up;
            dir.Normalize();
            float speed = UnityEngine.Random.Range(2.2f, 4.2f);
            // Burst OUT with only a small upward pop, so gravity quickly wins and the coins FREE-FALL
            // downward with the debris for a beat before the suck-up. 2026-06-18 Spencer.
            Vector3 vel = new Vector3(dir.x * speed, dir.y * speed * 0.7f + UnityEngine.Random.Range(0.4f, 1.8f), 0f);
            const float GRAV = -16f;        // gravity for a readable downward fall (not so heavy it snaps back)
            float scatterDur = UnityEngine.Random.Range(0.45f, 0.62f), st = 0f; // FREE-FALL BEAT before the gather
            Vector3 pos = worldStart;
            while (st < scatterDur && go != null)
            {
                st += Time.deltaTime;
                vel.y += GRAV * Time.deltaTime;
                pos += vel * Time.deltaTime;
                go.transform.position = pos;
                // POP OUT: scale tiny → full with a slight overshoot, like shooting out of the chest.
                if (st < POP_DUR + 0.05f)
                {
                    float pk = Mathf.Clamp01(st / POP_DUR);
                    float ob = 1f + 2.7f * Mathf.Pow(pk - 1f, 3f) + 1.7f * Mathf.Pow(pk - 1f, 2f); // OutBack overshoot
                    go.transform.localScale = Vector3.one * (COIN_SCALE * Mathf.Lerp(0.5f, 1f, ob));
                }
                spinT += Time.deltaTime;
                if (flip) sr.sprite = frames[Mathf.FloorToInt(spinT * SPIN_FPS) % frames.Length];
                else go.transform.Rotate(0f, 0f, 420f * Time.deltaTime);
                yield return null;
            }
            yield return WaitCache.Get(index * 0.02f); // slight gather stagger

            // ── Phase 2: GATHER — curved arc UP to the reward counter, accelerating in ──
            Vector2 targetScreen = GetObjectiveIconScreenPos();
            float camDist = Mathf.Abs(cam.transform.position.z);
            Vector3 from = go != null ? go.transform.position : worldStart;
            Vector3 targetWorld = cam.ScreenToWorldPoint(new Vector3(targetScreen.x, targetScreen.y, camDist));
            targetWorld.z = from.z;
            Vector3 control = Vector3.Lerp(from, targetWorld, 0.5f) + new Vector3(UnityEngine.Random.Range(-0.8f, 0.8f), 1.0f, 0f);

            // Trail on its OWN child object (not the coin) so at the end it can be DETACHED and left to
            // fade out, instead of being destroyed with the coin (which chopped it into a hard edge).
            GameObject trailGO = null; TrailRenderer trail = null;
            if (go != null)
            {
                trailGO = new GameObject("CoinTrail");
                trailGO.transform.SetParent(go.transform, false);
                trail = trailGO.AddComponent<TrailRenderer>();
                trail.time = 0.16f; trail.startWidth = COIN_SCALE * 0.5f; trail.endWidth = 0f;
                trail.minVertexDistance = 0.02f; trail.numCapVertices = 4; trail.autodestruct = false;
                trail.sortingOrder = 204; trail.emitting = true;
                var tm = GetCoinTrailMatHDR(); // additive + HDR gold → the trail glows/blooms
                if (tm != null) trail.sharedMaterial = tm;
                var tg = new Gradient();
                tg.SetKeys(new[] { new GradientColorKey(new Color(1f, 0.95f, 0.6f), 0f), new GradientColorKey(new Color(1f, 0.8f, 0.3f), 1f) },
                           new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
                trail.colorGradient = tg;
            }

            float flyDur = UnityEngine.Random.Range(0.50f, 0.62f), e = 0f, trailT = 0f; // gather, slower upward travel (Spencer 2026-06-19)
            while (e < flyDur && go != null)
            {
                e += Time.deltaTime;
                float p = Mathf.Clamp01(e / flyDur);
                float ec = p * p * p, u = 1f - ec;           // ease-IN cubic = magnet "suck"
                Vector3 cp = (u * u) * from + (2f * u * ec) * control + (ec * ec) * targetWorld;
                go.transform.position = cp;
                spinT += Time.deltaTime;
                if (flip) sr.sprite = frames[Mathf.FloorToInt(spinT * SPIN_FPS * 1.4f) % frames.Length]; // spin faster while gathering
                else go.transform.Rotate(0f, 0f, 560f * Time.deltaTime);

                float marginPx = 0.05f * Screen.height; // small → trail runs up near the counter (last bit is hidden
                                                        // by the HUD bar), not cut off way down at the board top
                float distBelow = targetScreen.y - cam.WorldToScreenPoint(cp).y;
                if (distBelow < marginPx) // fade the COIN before it ducks under the overlay HUD (trail keeps
                {                          // emitting — the HUD bar occludes its top; it fades out on detach)
                    float fade = Mathf.Clamp01(distBelow / marginPx);
                    var c = sr.color; sr.color = new Color(c.r, c.g, c.b, fade);
                    if (coinGlowSR != null) coinGlowSR.color = new Color(1f, 1f, 1f, 0.4f * fade);
                }
                else
                {
                    trailT += Time.deltaTime;
                    if (trailT >= 0.06f) { trailT = 0f; SpawnTrailSpark(cp, GetFlareStarSprite(), 0.11f, 0.24f, 0.14f); } // bigger flare sparkles
                }
                yield return null;
            }
            // Detach the trail and let it fade out on its own (autodestruct after its points expire) — NOT
            // destroyed with the coin, which left a hard chopped edge. 2026-06-18 Spencer.
            if (trailGO != null && trail != null)
            {
                trailGO.transform.SetParent(null, true); // keep its world-space points
                trail.emitting = false;
                trail.autodestruct = true;               // self-destroys once empty (~trail.time)
            }
            if (go != null) Destroy(go);
            OnRewardCoinLanded(share);
        }

        private void OnRewardCoinLanded(int share)
        {
            var vault = ObjectiveManager.Instance?.Active as VaultObjective;
            int cap = vault != null ? vault.RewardCoins : int.MaxValue;
            _displayedReward = Mathf.Min(_displayedReward + share, cap);
            RefreshObjectiveBadgeNumber();
            if (_objectiveIconGO != null) { BringTargetChainToFront(); UIAnimations.WildCardPop(_objectiveIconGO.transform, Vector3.one); }
            if (_objectiveIconHolder != null && _objectiveIconHolder.transform is RectTransform hrt)
                SpawnSlotSparkleBurst(hrt, Vector2.zero);
            GameAudio.Instance?.PlayCoinLand(); // pitch-ramp + round-robin Coins2/3/4 + throttle
        }

        private void RefreshObjectiveBadgeNumber()
        {
            if (_objectiveBadgeText == null) return;
            if (!_objectiveBadgeText.gameObject.activeSelf) _objectiveBadgeText.gameObject.SetActive(true);
            _objectiveBadgeText.text = _displayedReward.ToString();
        }

        /// <summary>Generic "tile flies up to a HUD target" flight — wild pop + glow + sparkle trail, curved
        /// ease-in flight, dissolves at the HUD, then onArrive() does the landing (reveal/pop/collect).
        /// Shared by the HiddenWord letters and the HeroWord escorts. 2026-06-17 Spencer.</summary>
        private System.Collections.IEnumerator FlyTileCoroutine(Vector3 startWorld, Sprite sprite, Color tileColor,
            Color glowColor, char letter, Vector3 targetScreen, System.Action onArrive, float startDelay)
        {
            var cam = Camera.main;
            if (cam == null) { onArrive?.Invoke(); yield break; }

            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
            float baseScale = cell * (154f / 172f); // 2026-06-24: synced with TILE_DISPLAY_RATIO (158→154)

            var go = new GameObject("TileFly");
            go.transform.position = startWorld;
            go.transform.localScale = Vector3.one * baseScale * 0.2f;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite != null ? sprite : Tile.NormalSprite;
            sr.color = tileColor; // HDR so it blooms
            sr.sortingOrder = 202; // well above the board so the flight is never occluded

            var glowSR = new GameObject("Glow").AddComponent<SpriteRenderer>();
            glowSR.transform.SetParent(go.transform, false);
            glowSR.sprite = GetGlowSprite();
            glowSR.color = new Color(glowColor.r, glowColor.g, glowColor.b, 0.75f);
            glowSR.sortingOrder = 201;
            glowSR.transform.localScale = Vector3.one * 3.3f;

            var trail = go.AddComponent<TrailRenderer>();
            trail.time = 0.35f;
            trail.startWidth = baseScale * 0.95f;
            trail.endWidth = 0f;
            trail.numCapVertices = 4;
            trail.minVertexDistance = 0.02f;
            trail.autodestruct = false;
            trail.emitting = false;
            trail.sortingOrder = 200;
            var ftMat = GetCoinTrailMat(sr.sharedMaterial); // shared trail material — was new Material() per flight
            if (ftMat != null) trail.sharedMaterial = ftMat;
            var tgrad = new Gradient();
            tgrad.SetKeys( // neutral white→light streak so it suits any tile colour
                new[] { new GradientColorKey(new Color(1f, 0.92f, 1f), 0f), new GradientColorKey(new Color(0.7f, 0.85f, 1f), 1f) },
                new[] { new GradientAlphaKey(0.95f, 0f), new GradientAlphaKey(0f, 1f) });
            trail.colorGradient = tgrad;

            TextMeshPro tmp = null;
            if (letter != '\0')
            {
                var letterGO = new GameObject("L");
                letterGO.transform.SetParent(go.transform, false);
                letterGO.transform.localPosition = new Vector3(0f, 0f, -0.01f);
                tmp = letterGO.AddComponent<TextMeshPro>();
                var f = GameFont.GetTMP(); if (f != null) tmp.font = f;
                tmp.text = char.ToUpperInvariant(letter).ToString();
                tmp.fontSize = 7f;
                tmp.enableWordWrapping = false;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.color = Color.white;
                var tmpR = tmp.GetComponent<MeshRenderer>(); if (tmpR != null) tmpR.sortingOrder = 203;
            }

            // ── Phase 1: WILD POP — overshoot + glow flash + wobble, elastic settle ──
            GameParticles.Instance?.PlayShimmerBurst(startWorld, 10);
            var pop = DOTween.Sequence();
            pop.Append(go.transform.DOScale(baseScale * 1.28f, 0.17f).SetEase(Ease.OutBack, 4f));
            pop.Join(glowSR.DOFade(0.95f, 0.13f));
            pop.Join(go.transform.DOPunchRotation(new Vector3(0f, 0f, 13f), 0.34f, 6, 0.7f));
            pop.Append(go.transform.DOScale(baseScale, 0.16f).SetEase(Ease.OutElastic, 0.6f, 0.4f));
            pop.Join(glowSR.DOFade(0.35f, 0.22f));
            yield return pop.WaitForCompletion();
            yield return WaitCache.Get(0.05f);
            if (startDelay > 0f) yield return WaitCache.Get(startDelay); // stagger AFTER the pop → group pops together, lands one-by-one

            // ── Phase 2: curved flight UP, ACCELERATING into the target (coin-collect "suck-in") ──
            float camDist = Mathf.Abs(cam.transform.position.z);
            Vector3 targetWorld = cam.ScreenToWorldPoint(new Vector3(targetScreen.x, targetScreen.y, camDist));
            targetWorld.z = go.transform.position.z;

            Vector3 from = go.transform.position;
            Vector3 control = Vector3.Lerp(from, targetWorld, 0.5f)
                              + new Vector3(UnityEngine.Random.Range(-1.3f, 1.3f), 1.2f, 0f);

            if (glowSR != null) glowSR.DOFade(0.55f, 0.12f);
            if (trail != null) { trail.Clear(); trail.emitting = true; }
            float flyDur = 0.34f, elapsed = 0f, trailT = 0f; // was 0.5 — snappier float-up (letters + escorts). 2026-06-18 Spencer.
            while (elapsed < flyDur && go != null)
            {
                elapsed += Time.deltaTime;
                float p = Mathf.Clamp01(elapsed / flyDur);
                float e = p * p * p;
                float u = 1f - e;
                Vector3 pos = (u * u) * from + (2f * u * e) * control + (e * e) * targetWorld;
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * Mathf.Lerp(baseScale, baseScale * 0.5f, e);

                // Dissolve + kill the trail before it reaches the overlay HUD (world can't render over it).
                float marginPx = 0.14f * Screen.height;
                float distBelowTargetPx = targetScreen.y - cam.WorldToScreenPoint(pos).y;
                bool nearHud = distBelowTargetPx < marginPx;
                if (nearHud)
                {
                    float fade = Mathf.Clamp01(distBelowTargetPx / marginPx);
                    var cc = sr.color; sr.color = new Color(cc.r, cc.g, cc.b, fade);
                    if (tmp != null) tmp.color = new Color(1f, 1f, 1f, fade);
                    if (glowSR != null) { var gc = glowSR.color; glowSR.color = new Color(gc.r, gc.g, gc.b, 0.55f * fade); }
                    if (trail != null) trail.emitting = false;
                }

                trailT += Time.deltaTime;
                if (trailT >= 0.012f && !nearHud)
                {
                    trailT = 0f;
                    SpawnTrailSpark(pos, GetFlareStarSprite(), 0.10f, 0.22f, 0.16f);
                    SpawnTrailSpark(pos, GetFlareStarSprite(), 0.05f, 0.11f, 0.22f);
                    SpawnTrailSpark(pos, GetFlareStarSprite(), 0.03f, 0.08f, 0.26f);
                    SpawnTrailSpark(pos, GetPoint1Sprite(),    0.04f, 0.13f, 0.22f);
                }
                yield return null;
            }

            if (go != null) { GameParticles.Instance?.PlayShimmerBurst(targetWorld, 12); Destroy(go); }
            onArrive?.Invoke();
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

        /// <summary>
        /// Phase 11d — brief scale + color pulse on the edit counter when a
        /// refund lands. Separate from the text update so the number changes
        /// first (done by ShowRewriteCount) and the animation draws the eye to
        /// the increment. Killed + restarted on repeat calls so back-to-back
        /// refunds each register visually.
        /// </summary>
        public void PulseRewriteCounter()
        {
            if (_rewriteCounterText == null) return;
            var tr = _rewriteCounterText.rectTransform;
            tr.DOKill();
            tr.localScale = Vector3.one;
            Sequence seq = DOTween.Sequence();
            seq.Append(tr.DOScale(1.35f, 0.12f).SetEase(Ease.OutBack));
            seq.Append(tr.DOScale(1.00f, 0.18f).SetEase(Ease.InOutQuad));
            seq.SetTarget(tr);
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
        // TOP-OUT DANGER INDICATOR (Survival)
        // ═══════════════════════════════════════════════════════════════════════════

        private void Update()
        {
            RefreshTopOutDanger();

            // Royal Match panels (2026-06-15 Spencer): Target (driven by SetObjective) + Level + Moves
            // show during a Survival run. Level number = the run goal (highest level reached). Coins are
            // force-hidden in survival — they'd sit on top of the Moves panel.
            bool survival = SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null;
            // 2026-06-24 Spencer: LEVEL box REMOVED (Royal-Match style — no in-play level box). The level
            // still shows on the intro modal ("LEVEL N") and the stage-clear modal ("LEVEL N CLEARED").
            if (_levelPanel != null && _levelPanel.activeSelf) _levelPanel.SetActive(false);
            if (_movesPanel != null && _movesPanel.activeSelf != survival) _movesPanel.SetActive(survival);
            if (survival)
            {
                if (_levelNumText != null)
                {
                    string lv = SurvivalManager.Instance.CurrentStageIndex.ToString();
                    if (_levelNumText.text != lv) _levelNumText.text = lv;
                }
                if (_coinCounterText != null && _coinCounterText.gameObject.activeSelf)
                    _coinCounterText.gameObject.SetActive(false);
            }
        }

        /// <summary>Kill any in-flight WildCardPop and snap the counter back to rest scale/rotation.
        /// The pop (scale + DOPunchRotation) leaves the transform mid-state if interrupted by a
        /// level transition; without this the counter starts the next level stuck big + rotated.
        /// 2026-06-09 Spencer.</summary>
        private void ResetTopOutNumTransform()
        {
            if (_topOutNumText == null) return;
            _topOutNumText.transform.DOKill();
            _topOutNumText.transform.localScale    = Vector3.one;
            _topOutNumText.transform.localRotation = Quaternion.identity;
        }

        private void RefreshTopOutDanger()
        {
            if (_topOutNumText == null) return;

            bool survival = SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null;
            if (!survival)
            {
                if (_topOutNumText.gameObject.activeSelf) _topOutNumText.gameObject.SetActive(false);
                ResetTopOutNumTransform();
                _topOutDisplay = int.MaxValue; _topOutLastStrict = -1; // re-seed clamp for next run
                HideTopOutBubble();
                return;
            }

            var sm = SurvivalManager.Instance;
            // Topped out (CONTINUE modal up, or forced game over) → you're out of turns:
            // FORCE "0" instead of letting the post-overflow turnsSinceRise=0 reset
            // recompute it to "2" (Spencer's repro). The CONTINUE modal is the real
            // top-out signal — IsGameOver only fires after the continue cap.
            bool toppedOut = sm.IsGameOver
                || (ContinueModal.Instance != null && ContinueModal.Instance.IsShowing);
            if (toppedOut)
            {
                ResetTopOutNumTransform(); // clear any mid-flight pop so "0" isn't stuck big/rotated
                _topOutNumText.text = "0";
                _topOutLastShown = 0;
                _topOutDisplay = int.MaxValue; _topOutLastStrict = -1; // reset clamp for the next run
                if (!_topOutNumText.gameObject.activeSelf) _topOutNumText.gameObject.SetActive(true);
                HideTopOutBubble();
                return;
            }
            // Non-death overlay (stage-clear transition) → hide so it doesn't flash a
            // mid-shift value; reappears when play resumes.
            if (sm.IsOverlayPaused)
            {
                if (_topOutNumText.gameObject.activeSelf) _topOutNumText.gameObject.SetActive(false);
                ResetTopOutNumTransform();
                _topOutDisplay = int.MaxValue; _topOutLastStrict = -1; // re-seed on resume
                HideTopOutBubble();
                return;
            }

            // Vault levels: rises are OFF — the counter shows MOVES LEFT (the move cap) instead of
            // the rise countdown. Same widget, repurposed. 2026-06-09.
            if (sm.IsMoveCapLevel)
            {
                // Monotonic DOWN: never let it jump back UP. When the level ends out-of-moves,
                // _currentStageMovesUsed resets to 0 → VaultMovesRemaining momentarily reads the full cap;
                // clamping stops that "flash back to 8" so it stays at 0 until the overlay hides it.
                // _topOutDisplay is re-seeded to int.MaxValue on overlay-pause / top-out, so a FRESH vault
                // level still shows the full cap. 2026-06-19 Spencer.
                _topOutDisplay = Mathf.Min(_topOutDisplay, sm.VaultMovesRemaining);
                int left = _topOutDisplay;
                if (!_topOutNumText.gameObject.activeSelf) _topOutNumText.gameObject.SetActive(true);
                if (left != _topOutLastShown)
                {
                    _topOutLastShown = left;
                    _topOutNumText.text = left.ToString();
                }
                if (left <= TOPOUT_DANGER_THRESHOLD) ShowTopOutBubble();
                else HideTopOutBubble();
                return;
            }

            // Monotonic clamp: the raw value can jump UP mid-cadence (the rise-schedule
            // resets the turn counter before the board actually changes). The player should
            // only ever see it tick DOWN — UNLESS they genuinely clear space, which raises
            // the headroom (strictRises). So allow an increase only when strictRises grows.
            int raw    = sm.GetMovesUntilTopOut();
            int strict = (RulesEngine.Instance != null) ? RulesEngine.Instance.GetRisesUntilTopOut() + 1 : raw;
            if (strict > _topOutLastStrict) _topOutDisplay = raw;             // cleared / fresh board → allow up
            else                            _topOutDisplay = Mathf.Min(_topOutDisplay, raw); // else monotonic down
            _topOutLastStrict = strict;
            int moves = _topOutDisplay;

            if (!_topOutNumText.gameObject.activeSelf) _topOutNumText.gameObject.SetActive(true);
            if (moves != _topOutLastShown)
            {
                // 2026-06-08 Spencer: when the count ticks UP (you cleared space → bought
                // turns), celebrate it with the wild card's scale-up + wiggle pop.
                bool wentUp = _topOutLastShown != int.MinValue && moves > _topOutLastShown;
                _topOutLastShown = moves;
                _topOutNumText.text = moves.ToString();
                if (wentUp) UIAnimations.WildCardPop(_topOutNumText.transform, Vector3.one);
            }

            if (moves <= TOPOUT_DANGER_THRESHOLD) ShowTopOutBubble();
            else HideTopOutBubble();
        }

        private void ShowTopOutBubble()
        {
            if (_topOutBubble == null) return;
            if (!_topOutBubble.gameObject.activeSelf) _topOutBubble.gameObject.SetActive(true);
            if (_topOutBubbleLoop == null) _topOutBubbleLoop = StartCoroutine(TopOutBubbleLoop());
        }

        private void HideTopOutBubble()
        {
            if (_topOutBubbleLoop != null) { StopCoroutine(_topOutBubbleLoop); _topOutBubbleLoop = null; }
            if (_topOutBubble != null && _topOutBubble.gameObject.activeSelf)
                _topOutBubble.gameObject.SetActive(false);
        }

        /// <summary>Mirrors the tier-1 pop bubble: a white bubble expanding small→big
        /// while fading, on a continuous loop. Period tightens as danger rises (fewer
        /// moves → faster pulse). Unscaled time so it animates through pauses.</summary>
        private IEnumerator TopOutBubbleLoop()
        {
            var rt = _topOutBubble.rectTransform;
            while (true)
            {
                int moves = (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                    ? (SurvivalManager.Instance.IsMoveCapLevel
                        ? SurvivalManager.Instance.VaultMovesRemaining
                        : SurvivalManager.Instance.GetMovesUntilTopOut())
                    : TOPOUT_DANGER_THRESHOLD;
                float period = moves <= 1 ? 0.55f : (moves <= 2 ? 0.75f : 0.95f);
                float t = 0f;
                while (t < period)
                {
                    t += Time.unscaledDeltaTime;
                    float n = Mathf.Clamp01(t / period);
                    float s = Mathf.Lerp(0.45f, 1.2f, n);
                    rt.localScale = new Vector3(s, s, 1f);
                    var c = _topOutBubble.color;
                    c.a = Mathf.Lerp(0.85f, 0f, n); // peak bumped for white-on-cream visibility
                    _topOutBubble.color = c;
                    yield return null;
                }
            }
        }

        private Sprite LoadBubbleSprite()
        {
            if (_bubbleSpriteCache != null) return _bubbleSpriteCache;
            // Circle04 (soft white circle) — copied into Resources/Particles. Try as Sprite first, then
            // as a raw Texture2D, then fall back to a generated white circle. 2026-06-15 Spencer.
            _bubbleSpriteCache = Resources.Load<Sprite>("Particles/Circle04");
            if (_bubbleSpriteCache == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Particles/Circle04");
                if (tex != null)
                    _bubbleSpriteCache = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (_bubbleSpriteCache == null)
                _bubbleSpriteCache = TileRenderer.CreateSolidRoundedRect(64, 64, 32, Color.white);
            return _bubbleSpriteCache;
        }

        private Material _glowAddMat;
        /// <summary>Additive material so UI glows ADD light (read as a glow) instead of smudging a magenta
        /// haze over the panel. 2026-06-17 Spencer.</summary>
        private Material GlowAdditiveMat()
        {
            if (_glowAddMat != null) return _glowAddMat;
            var sh = Shader.Find("Legacy Shaders/Particles/Additive")
                  ?? Shader.Find("Mobile/Particles/Additive")
                  ?? Shader.Find("Particles/Additive")
                  ?? Shader.Find("Sprites/Default");
            _glowAddMat = new Material(sh);
            return _glowAddMat;
        }

        private Sprite _glowSpriteCache;
        /// <summary>A SOFT radial glow sprite, generated procedurally (no soft-glow asset exists in
        /// Resources — only hard circles). White with a smooth alpha falloff so a tint + bloom reads as a
        /// real glow, not a hard disc. 2026-06-17 Spencer.</summary>
        private Sprite GetGlowSprite()
        {
            if (_glowSpriteCache != null) return _glowSpriteCache;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = 1f - d; a = a * a * (3f - 2f * a); // smoothstep → soft but WIDE glow
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px); tex.Apply();
            _glowSpriteCache = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _glowSpriteCache;
        }

        private Sprite _flareStarCache, _point1Cache;
        private Sprite LoadParticleSprite(string resPath, ref Sprite cache)
        {
            if (cache != null) return cache;
            cache = Resources.Load<Sprite>(resPath);
            if (cache == null)
            {
                var tex = Resources.Load<Texture2D>(resPath);
                if (tex != null) cache = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (cache == null) cache = GetGlowSprite(); // fallback
            return cache;
        }
        private Sprite GetFlareStarSprite() => LoadParticleSprite("Particles/flare_star", ref _flareStarCache);
        private Sprite GetPoint1Sprite()    => LoadParticleSprite("Particles/point1",     ref _point1Cache);

        // POOLED trail sparks. The fly-up emits ~4 sparks every 0.012s, so creating+Destroying a fresh
        // GameObject per spark churned ~150+ objects per letter flight → GC spikes / frame hitches during
        // reveals. The pool reuses a small set of spark renderers (grows once to the peak concurrent count,
        // then never instantiates or destroys again). Same visual density, no churn. 2026-06-18 Spencer.
        private readonly System.Collections.Generic.Queue<SpriteRenderer> _sparkPool = new System.Collections.Generic.Queue<SpriteRenderer>();
        private Transform _sparkPoolRoot;

        private SpriteRenderer GetPooledSpark()
        {
            if (_sparkPool.Count > 0)
            {
                var r = _sparkPool.Dequeue();
                if (r != null) { r.gameObject.SetActive(true); return r; }
            }
            if (_sparkPoolRoot == null) _sparkPoolRoot = new GameObject("TrailSparkPool").transform;
            var go = new GameObject("TrailSpark");
            go.transform.SetParent(_sparkPoolRoot, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 201;
            return sr;
        }

        private void ReturnSpark(SpriteRenderer sr)
        {
            if (sr == null) return;
            sr.DOKill();
            sr.transform.DOKill();
            sr.gameObject.SetActive(false);
            _sparkPool.Enqueue(sr);
        }

        /// <summary>Spawns one WHITE spark near <paramref name="pos"/> that twinkles, drifts a little, and
        /// fades — emitted along the fly-up path for a Candy-Crush-style sparkle trail. Pooled. 2026-06-17 Spencer.</summary>
        private void SpawnTrailSpark(Vector3 pos, Sprite sprite, float minSz, float maxSz, float spread)
        {
            var sr = GetPooledSpark();
            var t = sr.transform;
            sr.DOKill(); t.DOKill();
            Vector2 off = UnityEngine.Random.insideUnitCircle * spread;
            t.position = pos + new Vector3(off.x, off.y, 0f);
            t.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
            sr.sprite = sprite;
            sr.color = Color.white; // white sparks
            float sz = UnityEngine.Random.Range(minSz, maxSz);
            t.localScale = Vector3.one * sz;
            float life = UnityEngine.Random.Range(0.35f, 0.6f);
            sr.DOFade(0f, life).SetEase(Ease.InQuad);
            t.DOScale(sz * 0.2f, life).SetEase(Ease.InQuad);
            t.DOMove(t.position + new Vector3(off.x * 0.5f, -0.15f, 0f), life).SetEase(Ease.OutQuad) // slight fall
              .OnComplete(() => ReturnSpark(sr));
        }

        /// <summary>UI version of the "word primed" sparkle burst (GameParticles.PlayPrimed), fired ON the
        /// Target slot when a flown letter lands. Built as UI star Images so it renders OVER the overlay HUD
        /// (world particles would be hidden behind it). Gold-white stars bursting outward + fading.
        /// 2026-06-17 Spencer.</summary>
        private void SpawnSlotSparkleBurst(RectTransform parent, Vector2 center)
        {
            if (parent == null) return;
            StartCoroutine(SlotSparkleBurstCoroutine(parent, center));
        }

        private System.Collections.IEnumerator SlotSparkleBurstCoroutine(RectTransform parent, Vector2 center)
        {
            const int n = 9;
            var rts = new RectTransform[n]; var imgs = new Image[n];
            var dirs = new Vector2[n]; var dists = new float[n]; var lifes = new float[n]; var spins = new float[n];
            for (int i = 0; i < n; i++)
            {
                if (parent == null) yield break;
                var go = new GameObject("SlotSpark", typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)go.transform;
                rt.SetParent(parent, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                float sz = UnityEngine.Random.Range(14f, 32f); // bigger
                rt.sizeDelta = new Vector2(sz, sz);
                rt.anchoredPosition = center;
                rt.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                var img = go.GetComponent<Image>();
                img.sprite = GetFlareStarSprite();
                img.color = new Color(1f, 0.95f, 0.7f, 1f); // warm gold-white, like the prime sparkle
                img.raycastTarget = false;
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                rts[i] = rt; imgs[i] = img;
                dirs[i] = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                dists[i] = UnityEngine.Random.Range(26f, 58f);
                lifes[i] = UnityEngine.Random.Range(0.35f, 0.55f);
                spins[i] = UnityEngine.Random.Range(-120f, 120f);
            }
            float t = 0f;
            while (t < 0.6f)
            {
                t += Time.deltaTime;
                for (int i = 0; i < n; i++)
                {
                    if (rts[i] == null) continue;
                    float p = Mathf.Clamp01(t / lifes[i]);
                    float ease = 1f - (1f - p) * (1f - p);
                    rts[i].anchoredPosition = center + dirs[i] * dists[i] * ease;
                    rts[i].localScale = Vector3.one * Mathf.Lerp(1f, 0.15f, p);
                    rts[i].localRotation = Quaternion.Euler(0f, 0f, rts[i].localRotation.eulerAngles.z + spins[i] * Time.deltaTime);
                    var c = imgs[i].color; c.a = 1f - p; imgs[i].color = c;
                    if (p >= 1f) { Destroy(rts[i].gameObject); rts[i] = null; }
                }
                yield return null;
            }
            for (int i = 0; i < n; i++) if (rts[i] != null) Destroy(rts[i].gameObject);
        }

        /// <summary>A soft magenta glow that blooms behind the slot for the brief landing pop, then fades —
        /// so the tile lights up like a freshly-primed tile when its letter arrives. 2026-06-17 Spencer.</summary>
        private void SpawnSlotGlow(RectTransform parent, Vector2 center, float slotSize)
        {
            if (parent == null) return;
            var go = new GameObject("SlotGlow", typeof(RectTransform), typeof(Image));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.SetAsFirstSibling(); // behind the slot tiles
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = center;
            rt.sizeDelta = Vector2.one * slotSize * 2.5f;
            var img = go.GetComponent<Image>();
            img.sprite = GetGlowSprite();
            img.material = GlowAdditiveMat();             // ADD light → glow, not a smudge
            img.color = new Color(0.95f, 0.4f, 0.85f, 0f); // magenta, fades in/out over the pop
            img.raycastTarget = false;
            StartCoroutine(SlotGlowCoroutine(go, img, rt));
        }

        private System.Collections.IEnumerator SlotGlowCoroutine(GameObject go, Image img, RectTransform rt)
        {
            float t = 0f; const float dur = 0.5f;
            while (t < dur && go != null)
            {
                t += Time.deltaTime;
                float p = Mathf.Clamp01(t / dur);
                float a = p < 0.22f ? Mathf.Lerp(0f, 0.95f, p / 0.22f) : Mathf.Lerp(0.95f, 0f, (p - 0.22f) / 0.78f);
                var c = img.color; c.a = a; img.color = c;
                rt.localScale = Vector3.one * Mathf.Lerp(0.85f, 1.2f, p);
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // OBJECTIVE READOUT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Builds one Royal-Match-style HUD panel: a navy rounded frame with a gold header
        /// label on top and a cream inner content box below (the caller fills <paramref name="inner"/>).
        /// Stays within the bar height so the rising board never overlaps it. 2026-06-15 Spencer.</summary>
        private GameObject BuildHudPanel(Transform parent, string name, float xMin, float xMax, string label, out RectTransform inner)
        {
            var frameGO = new GameObject(name, typeof(RectTransform), typeof(Image));
            frameGO.transform.SetParent(parent, false);
            var frRT = frameGO.GetComponent<RectTransform>();
            frRT.anchorMin = new Vector2(xMin, 0.05f);
            frRT.anchorMax = new Vector2(xMax, 0.97f);
            frRT.offsetMin = Vector2.zero; frRT.offsetMax = Vector2.zero;
            var frImg = frameGO.GetComponent<Image>();
            frImg.sprite = TileRenderer.CreateSolidRoundedRect(120, 120, 20, Color.white);
            frImg.type   = Image.Type.Sliced; // 9-slice → crisp, un-stretched corners at any panel size
            frImg.color  = new Color(0.93f, 0.45f, 0.62f, 1f); // 2026-06-24: candy PINK frame/header — matches the modal headers (LevelIntro HEADER_BG), was navy
            frImg.raycastTarget = false;

            var lbl = MakeLabel(frameGO.transform, name + "Label",
                anchorMin: new Vector2(0.04f, 0.58f), anchorMax: new Vector2(0.96f, 0.96f),
                pivot: new Vector2(0.5f, 0.5f), offMin: Vector2.zero, offMax: Vector2.zero,
                text: label, size: 12, style: FontStyle.Bold,
                color: Color.white, align: TextAnchor.MiddleCenter); // 2026-06-24: white label on the pink header (was gold)
            lbl.enableAutoSizing = true; lbl.fontSizeMin = 7f; lbl.fontSizeMax = 13f;

            var innerGO = new GameObject(name + "Inner", typeof(RectTransform), typeof(Image));
            innerGO.transform.SetParent(frameGO.transform, false);
            inner = innerGO.GetComponent<RectTransform>();
            inner.anchorMin = new Vector2(0.06f, 0.05f); inner.anchorMax = new Vector2(0.94f, 0.55f);
            inner.offsetMin = Vector2.zero; inner.offsetMax = Vector2.zero;
            var inImg = innerGO.GetComponent<Image>();
            inImg.sprite = TileRenderer.CreateSolidRoundedRect(100, 100, 16, Color.white);
            inImg.type   = Image.Type.Sliced; // 9-slice → crisp corners
            inImg.color  = new Color(0.99f, 0.95f, 0.86f, 1f); // cream interior — matches the modal cards (CARD_BG)
            inImg.raycastTarget = false;

            return frameGO;
        }

        /// <summary>Toggle the endless-mode score progress bar (BG+fill+stage label). Hidden in
        /// objective mode — the objective readout owns that center slot. 2026-06-15.</summary>
        private void SetStageBarVisible(bool visible)
        {
            // Fill is a child of the BG, so toggling the BG hides/shows it too.
            if (_stageProgressBG != null && _stageProgressBG.activeSelf != visible)
                _stageProgressBG.SetActive(visible);
            if (_stageLabelText != null && _stageLabelText.gameObject.activeSelf != visible)
                _stageLabelText.gameObject.SetActive(visible);
        }

        /// <summary>Show/refresh the level objective (title + progress). Null hides it.
        /// Called by ObjectiveManager whenever the active objective changes or progresses.</summary>
        public void SetObjective(Objective obj)
        {
            if (obj == null)
            {
                if (_objectiveText != null && _objectiveText.gameObject.activeSelf) _objectiveText.gameObject.SetActive(false);
                if (_objectivePanel != null) _objectivePanel.SetActive(false);
                ResetObjectiveTextTransform();
                HideObjectiveCheck();
                SetStageBarVisible(true); // endless / no-objective: restore the score progress bar
                return;
            }
            SetStageBarVisible(false);

            if (obj.Icon != Objective.HudIcon.None)
            {
                // Icon + COUNT-DOWN badge (Royal Match Target panel). Text fallback hidden.
                if (_objectiveText != null && _objectiveText.gameObject.activeSelf) _objectiveText.gameObject.SetActive(false);
                if (_objectivePanel != null && !_objectivePanel.activeSelf) _objectivePanel.SetActive(true);
                // Vault (reward) levels retitle the panel "REWARD"; everything else stays "TARGET".
                if (_objectivePanelLabel != null)
                {
                    string want = obj.Icon == Objective.HudIcon.Vault ? "REWARD" : "TARGET";
                    if (_objectivePanelLabel.text != want) _objectivePanelLabel.text = want;
                }
                EnsureObjectiveIcon(obj);
                UpdateObjectiveBadge(obj);
            }
            else
            {
                // Text fallback for an objective with no icon.
                if (_objectivePanel != null) _objectivePanel.SetActive(false);
                if (_objectiveText != null)
                {
                    if (!_objectiveText.gameObject.activeSelf) _objectiveText.gameObject.SetActive(true);
                    _objectiveText.text  = $"{obj.Title}   {obj.ProgressText}";
                    _objectiveText.color = obj.IsComplete ? new Color(0.4f, 1f, 0.45f) : Color.white;
                    if (!obj.IsComplete) { ResetObjectiveTextTransform(); HideObjectiveCheck(); }
                }
            }
        }

        /// <summary>Rebuild the target icon only when its TYPE changes (cheap; no per-frame churn).</summary>
        private void EnsureObjectiveIcon(Objective obj)
        {
            if (_objectiveIconHolder == null || (obj.Icon == _shownObjectiveIcon && obj.IconWord == _shownIconWord)) return;
            if (_objectiveIconGO != null) Destroy(_objectiveIconGO);
            // Build WITHOUT the builder's own badge — the HUD owns the badge so it can tick the count
            // down and host the completion check.
            // Vault levels show the COIN (reward) in the HUD Target panel — the intro modal keeps the
            // treasure chest. 2026-06-18 Spencer.
            _objectiveIconGO = obj.Icon == Objective.HudIcon.Vault
                ? ObjectiveIconBuilder.BuildRewardCoinIcon(_objectiveIconHolder.transform, 32f)
                : ObjectiveIconBuilder.Build(obj.Icon, _objectiveIconHolder.transform, 32f, 0, obj.IconWord);
            if (_objectiveBadge != null)
            {
                _objectiveBadge.transform.SetAsLastSibling(); // keep badge/check on top of the icon
                var bRT = (RectTransform)_objectiveBadge.transform;
                // HiddenWord's row overflows the small icon holder, so the badge/check (the completion
                // check rides the badge) would sit under the row's CENTRE. Park it on the LAST slot's
                // bottom-right corner instead — where a normal icon's badge sits. 2026-06-17 Spencer.
                if (obj.Icon == Objective.HudIcon.HiddenWord && !string.IsNullOrEmpty(obj.IconWord)
                    && _objectiveIconGO.transform.Find($"R{obj.IconWord.Length - 1}") is RectTransform lastSlot)
                {
                    float half = lastSlot.sizeDelta.x * 0.5f;
                    _objectiveBadge.transform.position = lastSlot.position + new Vector3(half + 3f, -(half + 2f), 0f);
                }
                else
                {
                    // Default: the icon holder's bottom-right corner.
                    bRT.anchorMin = bRT.anchorMax = new Vector2(1f, 0f);
                    bRT.anchoredPosition = new Vector2(3f, -2f);
                }
            }
            _shownObjectiveIcon = obj.Icon;
            _shownIconWord = obj.IconWord;
        }

        /// <summary>Badge shows RemainingCount, ticking DOWN as the player progresses. On completion
        /// the number hides and the check drops onto the badge in its place. 2026-06-15 Spencer.</summary>
        private void UpdateObjectiveBadge(Objective obj)
        {
            if (_objectiveBadgeText == null) return;
            // HiddenWord shows its progress IN the rock row (rocks → letters), so it needs no count badge.
            if (obj.Icon == Objective.HudIcon.HiddenWord)
            {
                if (_objectiveBadgeText.gameObject.activeSelf) _objectiveBadgeText.gameObject.SetActive(false);
                return;
            }
            // Vault (REWARD) levels: the badge is a COIN TOTAL that ticks up as coins LAND. The true
            // total (vault.RewardCoins) is banked immediately; _displayedReward animates toward it.
            if (obj is VaultObjective vault)
            {
                HideObjectiveCheck();
                if (!_objectiveBadgeText.gameObject.activeSelf) _objectiveBadgeText.gameObject.SetActive(true);
                if (_displayedReward > vault.RewardCoins) _displayedReward = vault.RewardCoins; // new level → reset
                string cs = _displayedReward.ToString();
                if (_objectiveBadgeText.text != cs) _objectiveBadgeText.text = cs;
                return;
            }
            if (obj.IsComplete)
            {
                if (_objectiveBadgeText.gameObject.activeSelf) _objectiveBadgeText.gameObject.SetActive(false);
            }
            else
            {
                HideObjectiveCheck();
                if (!_objectiveBadgeText.gameObject.activeSelf) _objectiveBadgeText.gameObject.SetActive(true);
                string s = obj.RemainingCount.ToString();
                if (_objectiveBadgeText.text != s) _objectiveBadgeText.text = s;
            }
        }

        private void ResetObjectiveTextTransform()
        {
            if (_objectiveText == null) return;
            _objectiveText.transform.DOKill();
            _objectiveText.transform.localScale    = Vector3.one;
            _objectiveText.transform.localRotation = Quaternion.identity;
        }

        /// <summary>Celebrate completion — pop the target icon, then drop the check onto the badge,
        /// replacing the number ("lowers on the badge and replaces the number"). 2026-06-15 Spencer.</summary>
        public void FlashObjectiveComplete()
        {
            if (_objectiveIconHolder != null && _objectiveIconHolder.activeInHierarchy)
                UIAnimations.WildCardPop(_objectiveIconHolder.transform, Vector3.one);
            if (_objectiveBadgeText != null) _objectiveBadgeText.gameObject.SetActive(false);
            ShowObjectiveCheck();
            PlayObjectiveCelebration();
            if (_objectiveText != null && _objectiveText.gameObject.activeSelf)
            {
                _objectiveText.color = new Color(0.4f, 1f, 0.45f);
                UIAnimations.WildCardPop(_objectiveText.transform, Vector3.one);
            }
        }

        // ── Objective-complete CELEBRATION ─────────────────────────────────────────────────────────
        // The SAME spark burst the wild tile plays on spawn (HandManager.PlayWildSparkBurst): vfx_sparks_2,
        // rainbow hue by angle, pop → drift OUTWARD → fade. NO gravity (that "sucked them back in" — the
        // confetti's bug). Small + UI-space (children of the HUD) so it renders over the overlay. 2026-06-15.
        private static Sprite[] _objSparkSprites;

        private void PlayObjectiveCelebration()
        {
            if (_objectiveIconHolder == null || UIAnimations.ReducedMotion) return;
            SpawnStarFlash(); // extra layer: a big star scaling up behind the icon

            if (_objSparkSprites == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Particles/vfx_sparks_2");
                if (tex == null) { var sheet = Resources.Load<Sprite>("Particles/vfx_sparks_2"); if (sheet != null) tex = sheet.texture; }
                if (tex == null) return;
                int hw = tex.width / 2, hh = tex.height / 2;
                _objSparkSprites = new Sprite[4]
                {
                    Sprite.Create(tex, new Rect(0,  hh, hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(hw, hh, hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(0,  0,  hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(hw, 0,  hw, hh), new Vector2(0.5f, 0.5f), 100f),
                };
            }

            const int count = 7;
            for (int i = 0; i < count; i++)
            {
                var spr = _objSparkSprites[UnityEngine.Random.Range(0, _objSparkSprites.Length)];
                var go = new GameObject("ObjSpark", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_objectiveIconHolder.transform, false);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);

                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                Vector2 dirOut = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
                Vector2 startPos = dirOut * UnityEngine.Random.Range(5f, 12f); // small ring around the icon
                rt.anchoredPosition = startPos;
                rt.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));

                var img = go.GetComponent<Image>();
                img.sprite = spr;
                img.preserveAspect = true;
                img.raycastTarget = false;
                img.color = new Color(1f, 1f, 1f, 0f); // white (Spencer), fade in

                float peak = UnityEngine.Random.Range(7f, 12f); // SMALL (was too big)
                rt.sizeDelta = new Vector2(peak, peak);
                rt.localScale = Vector3.one * 0.25f;

                Vector2 drift = dirOut * UnityEngine.Random.Range(8f, 14f); // drift purely OUTWARD, decelerating — never back
                var capture = go;
                var seq = DOTween.Sequence();
                seq.Append(rt.DOScale(1f, 0.14f).SetEase(Ease.OutBack, 2f));             // pop in
                seq.Join(img.DOFade(1f, 0.08f));                                          // fade in
                seq.Join(rt.DOAnchorPos(startPos + drift, 0.40f).SetEase(Ease.OutCubic)); // drift out, no overshoot
                seq.Join(rt.DOLocalRotate(new Vector3(0f, 0f, UnityEngine.Random.Range(-40f, 40f)), 0.40f, RotateMode.LocalAxisAdd));
                seq.Insert(0.15f, img.DOFade(0f, 0.25f));                                 // fade out
                seq.Insert(0.15f, rt.DOScale(0.5f, 0.25f).SetEase(Ease.InQuad));          // shrink as it fades
                seq.OnComplete(() => { if (capture != null) Destroy(capture); });
            }
        }

        /// <summary>A single big star (Star01) scaling up + fading BEHIND the Target icon — an extra
        /// "pop" layer under the confetti. 2026-06-15 Spencer.</summary>
        private void SpawnStarFlash()
        {
            if (_objectiveIconHolder == null) return;
            Sprite star = LoadStarSprite();
            if (star == null) return;

            var go = new GameObject("ObjStarFlash", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_objectiveIconHolder.transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(40f, 40f);
            rt.localScale = Vector3.one * 0.6f;              // start near icon-size so it's not fully hidden
            rt.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(-12f, 12f));
            var img = go.GetComponent<Image>();
            img.sprite = star;
            img.preserveAspect = true;
            img.raycastTarget = false;
            img.color = new Color(1f, 1f, 1f, 1f);           // bright white — pops on the navy frame behind/around the icon
            rt.SetAsFirstSibling();                          // render BEHIND the icon

            var seq = DOTween.Sequence();
            seq.Append(rt.DOScale(3.0f, 0.55f).SetEase(Ease.OutCubic)); // grow well past the icon onto the navy
            seq.Join(img.DOFade(0f, 0.55f).SetEase(Ease.OutQuad));
            seq.Join(rt.DOLocalRotate(new Vector3(0f, 0f, 30f), 0.55f, RotateMode.LocalAxisAdd));
            seq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        private Sprite _starSpriteCache;
        private Sprite LoadStarSprite()
        {
            if (_starSpriteCache != null) return _starSpriteCache;
            _starSpriteCache = Resources.Load<Sprite>("Particles/Star01");
            if (_starSpriteCache == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Particles/Star01");
                if (tex != null)
                    _starSpriteCache = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (_starSpriteCache == null)
            {
                // Star01 not imported yet → fall back to the soft circle so SOMETHING flashes behind the
                // icon (better than nothing). 2026-06-15 Spencer.
                Debug.LogWarning("[HUD] Star01 sprite failed to load (Resources/Particles/Star01) — using a circle fallback for the objective star flash.");
                _starSpriteCache = LoadBubbleSprite();
            }
            return _starSpriteCache;
        }

        /// <summary>Quick pop on the target — fired each time progress ticks so the counter reacts.</summary>
        public void PulseObjective()
        {
            Transform t = (_objectiveIconHolder != null && _objectiveIconHolder.activeInHierarchy)
                ? _objectiveIconHolder.transform
                : (_objectiveText != null ? _objectiveText.transform : null);
            if (t == null) return;
            t.DOKill();
            t.localScale = Vector3.one;
            t.DOPunchScale(Vector3.one * 0.28f, 0.30f, 6, 0.8f);
        }

        /// <summary>Drop the green check onto the badge (replacing the number). Check is a child of
        /// the badge (fixed position), so this is just the same big→small pop as always.</summary>
        private void ShowObjectiveCheck()
        {
            if (_objectiveCheck == null) return;
            _objectiveCheck.gameObject.SetActive(true);
            _objectiveCheck.color = new Color(0.4f, 1f, 0.45f); // green
            var t = _objectiveCheck.transform;
            t.DOKill();
            t.localScale = Vector3.one * 2.2f;                  // start BIG
            t.DOScale(Vector3.one, 0.45f).SetEase(Ease.OutBack); // settle to small with a pop
        }

        private void HideObjectiveCheck()
        {
            if (_objectiveCheck == null) return;
            _objectiveCheck.transform.DOKill();
            if (_objectiveCheck.gameObject.activeSelf) _objectiveCheck.gameObject.SetActive(false);
        }

        private Sprite LoadCheckSprite()
        {
            if (_checkSpriteCache != null) return _checkSpriteCache;
            _checkSpriteCache = Resources.Load<Sprite>("Tiles/icon_check");
            if (_checkSpriteCache == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Tiles/icon_check");
                if (tex != null)
                    _checkSpriteCache = Sprite.Create(
                        tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _checkSpriteCache;
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
            // NOTE: do NOT set t.outlineWidth here — doing so spawns a per-text material INSTANCE that
            // ignores the shared-material cleanup in GameFont.GetUITMP (the _WeightBold=0 faux-bold fix).
            // Leaving it unset means every label shares the one cleaned material. 2026-06-15 Spencer.
            t.text     = text;
            t.fontSize = size;
            // 2026-06-15 Spencer: NEVER faux-bold — Cartoon's _WeightBold=0.75 over-dilates the already
            // heavy glyphs into a melty/garbled mess at HUD sizes. The font is bold by design; render
            // its native weight. (Was: Bold when style==Bold.)
            t.fontStyle = FontStyles.Normal;
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
            float riseSecondsLeft = sm.RisingRowSecondsRemaining;

            // 2026-05-28: stage progress is now a fill bar (more visceral than
            // numbers). Fill driven by changing anchorMax.x on the fill RT —
            // no Image.fillAmount/Mask voodoo. anchorMax.x=0 → empty pill;
            // =1 → full pill. Both ends stay rounded since fill uses the same
            // pill sprite as the BG.
            if (_stageLabelText != null) _stageLabelText.text = $"L{stage}";
            if (_stageProgressFillRT != null)
            {
                float fill = stageTarget > 0 ? (float)stageScore / stageTarget : 0f;
                fill = Mathf.Clamp01(fill);
                Vector2 am = _stageProgressFillRT.anchorMax;
                am.x = fill;
                _stageProgressFillRT.anchorMax = am;
            }

            // Color logic — purely rise-timer driven now that move-budget
            // stage-fail has been removed. Pressure comes from rising rows.
            //   cleared this stage          → green (safe, victory lap)
            //   rise imminent (≤2s)         → red
            //   rise soon (≤5s)             → amber
            //   otherwise                   → survival cyan
            // Color drives both the progress bar fill and the stage label.
            Color stageColor;
            bool cleared = sm.IsCurrentStageCleared;
            if (cleared)            stageColor = new Color(0.4f, 1f, 0.5f, 1f);    // green
            else if (riseSecondsLeft <= 2f) stageColor = TURN_DANGER;               // red
            else if (riseSecondsLeft <= 5f) stageColor = TURN_WARN;                 // amber
            else                    stageColor = new Color(0.40f, 0.90f, 0.95f, 1f); // cyan

            if (_stageLabelText    != null) _stageLabelText.color    = stageColor;
            if (_stageProgressFill != null) _stageProgressFill.color = stageColor;
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
