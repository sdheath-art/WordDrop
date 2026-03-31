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

        // ── Turn / Swap counters ──────────────────────────────────────────────────

        private TextMeshProUGUI _turnCounterText;
        private TextMeshProUGUI _swapCounterText;

        // ── Word-found overlay ────────────────────────────────────────────────────

        private GameObject _wordFoundOverlay;
        private TextMeshProUGUI _wordFoundText;
        private Coroutine  _wordFoundCoroutine;

        // ── Colors ────────────────────────────────────────────────────────────────

        private static readonly Color PLAYER_COLOR    = new Color(0.200f, 0.851f, 0.424f, 1f);  // player green #33D96C
        private static readonly Color AI_COLOR        = new Color(1.000f, 0.604f, 0.239f, 1f); // AI orange #FF9A3D
        private static readonly Color TURN_COLOR      = new Color(0.80f, 0.80f, 0.86f, 1f);
        private static readonly Color TURN_WARN       = new Color(1.00f, 0.75f, 0.20f, 1f);
        private static readonly Color TURN_DANGER     = new Color(1.00f, 0.32f, 0.28f, 1f);
        private static readonly Color SWAP_COLOR      = new Color(0.60f, 0.60f, 0.66f, 1f);
        private static readonly Color WORD_POPUP_P1   = new Color(0.200f, 0.851f, 0.424f, 1f);  // player green #33D96C
        private static readonly Color WORD_POPUP_AI   = new Color(1.000f, 0.604f, 0.239f, 1f); // AI orange
        private static readonly Color BAR_BG          = new Color(0.070f, 0.090f, 0.220f, 0.94f);  // deep indigo — matches board family
        private static readonly Color RESET_NORMAL    = new Color(0.18f, 0.18f, 0.23f, 1f);
        private static readonly Color RESET_HIGHLIGHT = new Color(0.32f, 0.32f, 0.40f, 1f);
        private static readonly Color RESET_PRESSED   = new Color(0.10f, 0.10f, 0.14f, 1f);

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildHUD();
            Debug.Log("[HUDManager] Awake — HUD built");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BUILD
        // ═══════════════════════════════════════════════════════════════════════════

        private void BuildHUD()
        {
            // ── Canvas ────────────────────────────────────────────────────────────
            GameObject canvasGO = new GameObject("HUDCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 50;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // ── HUD Bar — solid flat strip across the top ─────────────────────────
            GameObject barGO = new GameObject("HUDBar");
            barGO.transform.SetParent(canvasGO.transform, false);

            RectTransform barRT = barGO.AddComponent<RectTransform>();
            barRT.anchorMin = new Vector2(0f, 1f);
            barRT.anchorMax = new Vector2(1f, 1f);
            barRT.pivot     = new Vector2(0.5f, 1f);
            barRT.offsetMin = new Vector2(0f, -78f);  // edge to edge
            barRT.offsetMax = new Vector2(0f,   0f);   // flush with top

            Image barImg = barGO.AddComponent<Image>();
            barImg.color = BAR_BG;

            // ── Reset button — integrated into plate, far left ───────────────────
            BuildResetButton(barGO.transform);

            // ── P1 Score — left, larger ──────────────────────────────────────────
            // P1 label (static)
            _playerScoreText = MakeLabel(barGO.transform, "PlayerLabel",
                anchorMin: new Vector2(0.10f, 0.35f),
                anchorMax: new Vector2(0.20f, 0.98f),
                pivot:     new Vector2(0f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "P1:",
                size:      22,
                style:     FontStyle.Bold,
                color:     PLAYER_COLOR,
                align:     TextAnchor.MiddleLeft);

            // P1 number (animated) — Nunito ExtraBold
            _playerScoreNum = MakeLabel(barGO.transform, "PlayerScoreNum",
                anchorMin: new Vector2(0.20f, 0.35f),
                anchorMax: new Vector2(0.35f, 0.98f),
                pivot:     new Vector2(0f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "0",
                size:      34,
                style:     FontStyle.Bold,
                color:     PLAYER_COLOR,
                align:     TextAnchor.MiddleLeft);
            TMP_FontAsset heavyFont = Resources.Load<TMP_FontAsset>("NunitoExtraBold SDF");
            if (heavyFont != null) _playerScoreNum.font = heavyFont;

            // ── Swaps — small, tucked under P1 ─────────────────────────────────
            _swapCounterText = MakeLabel(barGO.transform, "SwapCounter",
                anchorMin: new Vector2(0.10f, 0.02f),
                anchorMax: new Vector2(0.35f, 0.35f),
                pivot:     new Vector2(0f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "Swaps: 3",
                size:      12,
                style:     FontStyle.Normal,
                color:     new Color(0.68f, 0.68f, 0.75f, 1f),  // slightly brighter
                align:     TextAnchor.MiddleLeft);

            // ── Turns remaining — center headline ────────────────────────────────
            _turnCounterText = MakeLabel(barGO.transform, "TurnCounter",
                anchorMin: new Vector2(0.35f, 0.08f),
                anchorMax: new Vector2(0.65f, 0.92f),
                pivot:     new Vector2(0.5f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "",
                size:      28,
                style:     FontStyle.Bold,
                color:     new Color(1f, 1f, 1f, 1f),
                align:     TextAnchor.MiddleCenter);

            // AI number (animated) — Nunito ExtraBold
            _aiScoreNum = MakeLabel(barGO.transform, "AIScoreNum",
                anchorMin: new Vector2(0.80f, 0.08f),
                anchorMax: new Vector2(0.95f, 0.92f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "0",
                size:      34,
                style:     FontStyle.Bold,
                color:     AI_COLOR,
                align:     TextAnchor.MiddleRight);
            if (heavyFont != null) _aiScoreNum.font = heavyFont;

            // AI label (static)
            _aiScoreText = MakeLabel(barGO.transform, "AILabel",
                anchorMin: new Vector2(0.68f, 0.08f),
                anchorMax: new Vector2(0.80f, 0.92f),
                pivot:     new Vector2(1f, 0.5f),
                offMin:    new Vector2(0f, 0f),
                offMax:    new Vector2(0f, 0f),
                text:      "AI:",
                size:      22,
                style:     FontStyle.Bold,
                color:     AI_COLOR,
                align:     TextAnchor.MiddleRight);

            // ── Word-found overlay ────────────────────────────────────────────────
            BuildWordFoundOverlay(canvasGO.transform);
        }

        private void BuildResetButton(Transform plateTransform)
        {
            GameObject btnGO = new GameObject("ResetButton");
            btnGO.transform.SetParent(plateTransform, false);

            RectTransform rt = btnGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0.10f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(4f, 4f);
            rt.offsetMax = new Vector2(-2f, -4f);

            Image img = btnGO.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.08f); // subtle, integrated

            Button btn    = btnGO.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor      = new Color(1f, 1f, 1f, 0.08f);
            cb.highlightedColor = new Color(1f, 1f, 1f, 0.15f);
            cb.pressedColor     = new Color(1f, 1f, 1f, 0.04f);
            cb.fadeDuration     = 0.08f;
            btn.colors          = cb;
            btn.onClick.AddListener(OnResetClicked);

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(btnGO.transform, false);

            RectTransform lrt = labelGO.AddComponent<RectTransform>();
            lrt.anchorMin = new Vector2(0.15f, 0.15f);
            lrt.anchorMax = new Vector2(0.85f, 0.85f);
            lrt.offsetMin = Vector2.zero;
            lrt.offsetMax = Vector2.zero;

            // Procedural reset icon — circular arrow
            Image iconImg = labelGO.AddComponent<Image>();
            iconImg.sprite = CreateResetIconSprite();
            iconImg.color = new Color(0.85f, 0.85f, 0.92f, 0.8f);
            iconImg.preserveAspect = true;
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
            bg.color = new Color(0.04f, 0.04f, 0.06f, 0.75f);

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
            _wordFoundText.fontSize  = 30;
            _wordFoundText.fontStyle = FontStyles.Normal;
            _wordFoundText.color     = WORD_POPUP_P1;
            _wordFoundText.alignment = TextAlignmentOptions.Center;
            _wordFoundText.enableWordWrapping = false;
            _wordFoundText.overflowMode = TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(_wordFoundText, WORD_POPUP_P1);

            _wordFoundOverlay.SetActive(false);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Score
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Updates the P1 (human player) score label.</summary>
        public void SetPlayerScore(int pts)
        {
            if (_playerScoreNum != null)
                _playerScoreNum.text = pts.ToString();
        }

        /// <summary>Updates the AI score number.</summary>
        public void SetAIScore(int pts)
        {
            if (_aiScoreNum != null)
                _aiScoreNum.text = pts.ToString();
        }

        // ── Visual score tick (used by ScoringDisplay to count up per-tile) ─────
        private int _displayPlayerScore = 0;
        private int _displayAIScore = 0;

        /// <summary>
        /// Visually tick the score up by delta points. Does not touch ScoreManager.
        /// The real score is applied later by CompleteDropBookkeeping.
        /// </summary>
        public void TickScore(bool isPlayer, int delta)
        {
            if (isPlayer)
            {
                _displayPlayerScore += delta;
                if (_playerScoreNum != null)
                {
                    _playerScoreNum.text = _displayPlayerScore.ToString();
                    AnimateScorePop(_playerScoreNum.transform);
                }
            }
            else
            {
                _displayAIScore += delta;
                if (_aiScoreNum != null)
                {
                    _aiScoreNum.text = _displayAIScore.ToString();
                    AnimateScorePop(_aiScoreNum.transform);
                }
            }
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
            // Use the known original color — don't read from current state (may be mid-flash)
            Color origColor = (tmp == _playerScoreNum) ? PLAYER_COLOR : AI_COLOR;
            if (tmp != null) tmp.color = Color.white;

            // Scale pop up to 1.65x (0.21s, OutBack 2f)
            t.DOScale(1.65f, 0.21f).SetEase(Ease.OutBack, 2f);

            // Jitter rotation per-frame during the pop (0.55s total)
            float jitterElapsed = 0f;
            float jitterDur = 0.55f;
            float scaleUpDur = 0.21f;
            float settleDur = 0.275f;
            bool settleStarted = false;

            while (jitterElapsed < jitterDur && t != null)
            {
                jitterElapsed += Time.deltaTime;
                float decay = 1f - (jitterElapsed / jitterDur);

                // Random rotation each frame (±10° decaying)
                float rot = Random.Range(-10f, 10f) * decay;
                t.localRotation = Quaternion.Euler(0f, 0f, rot);

                // Trigger settle scale after scale-up completes
                if (!settleStarted && jitterElapsed >= scaleUpDur)
                {
                    settleStarted = true;
                    t.DOScale(1f, settleDur).SetEase(Ease.OutQuad);
                }

                yield return null;
            }

            if (t == null) yield break;

            // Clean rotation
            t.localRotation = Quaternion.identity;

            // Settle beat: 1.0 → 1.2 → 1.0
            t.DOScale(1.2f, 0.165f * 0.4f).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(0.165f * 0.4f);
            if (t != null) t.DOScale(1f, 0.165f * 0.6f).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(0.165f * 0.6f);

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

        /// <summary>Sync display scores to actual ScoreManager values (call after bookkeeping).</summary>
        public void SyncDisplayScores()
        {
            if (ScoreManager.Instance != null)
            {
                _displayPlayerScore = ScoreManager.Instance.PlayerScore;
                _displayAIScore = ScoreManager.Instance.AIScore;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Turn Countdown (used by TurnCountdown.cs)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Set the countdown text and color directly from TurnCountdown.</summary>
        public void SetTurnCountdownText(string text, Color color)
        {
            if (_turnCounterText == null) return;
            _turnCounterText.text = text;
            _turnCounterText.color = color;
            _turnCounterText.fontSize = 28;
            // Use Nunito ExtraBold for heavy weight on turns
            TMP_FontAsset heavyFont = Resources.Load<TMP_FontAsset>("NunitoExtraBold SDF");
            if (heavyFont != null) _turnCounterText.font = heavyFont;
        }

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

            _swapCounterText.text = $"Swaps: {remaining}";

            // Dim the text when no swaps remain
            _swapCounterText.color = remaining > 0
                ? SWAP_COLOR
                : new Color(0.38f, 0.38f, 0.42f, 0.60f);
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

            _wordFoundOverlay.SetActive(true);
            _wordFoundCoroutine = StartCoroutine(FadeOutWordFound(1.4f));
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

            TMPHelper.ApplyEffects(t, color);
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
