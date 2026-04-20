using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Player-triggered hint system for Survival mode.
    /// A hint button appears when the board is jammed (2+ scoreless turns).
    /// Player taps it to reveal ONE valid word path.
    /// Hint disappears when the player acts.
    /// </summary>
    public class JamHint : MonoBehaviour
    {
        public static JamHint Instance { get; private set; }

        // ── Tuning ──────────────────────────────────────────────────────────────
        private const int   DROUGHT_THRESHOLD = 2;   // scoreless turns before button appears
        private const float HINT_PULSE_SPEED  = 3f;  // glow oscillation speed

        // ── State ───────────────────────────────────────────────────────────────
        private bool  _buttonVisible;
        private bool  _hintActive;
        private int   _hintCol = -1;
        private int   _hintRow = -1;
        private char  _hintLetter;
        private int   _hintHandSlot = -1;

        // ── UI ──────────────────────────────────────────────────────────────────
        private Canvas _canvas;
        private GameObject _hintButton;
        private TextMeshProUGUI _hintButtonLabel;
        private GameObject _hintGlow;
        private SpriteRenderer _hintGlowSR;

        public bool IsHintActive => _hintActive;
        public int  HintCol      => _hintCol;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildHintButton();
        }

        private void OnDestroy()
        {
            ClearHint();
        }

        private void Update()
        {
            // Hint button disabled — gets in the way of gameplay
            HideButton(); return;
            // if (!SurvivalManager.IsSurvivalMode) { HideButton(); return; }
            if (GameManager.Instance == null || GameManager.Instance.CurrentState != GameState.Playing) return;
            if (MatchController.Instance == null) return;

            // Animate hint glow when active
            if (_hintActive && _hintGlow != null)
            {
                float alpha = 0.3f + 0.25f * Mathf.Sin(Time.time * HINT_PULSE_SPEED);
                if (_hintGlowSR != null)
                    _hintGlowSR.color = new Color(0.2f, 0.9f, 0.5f, alpha);
            }

            // Show/hide button based on drought
            var hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            int drought = hand.DroughtTurns;

            if (drought >= DROUGHT_THRESHOLD && !_hintActive)
            {
                // Only show HINT if there's actually a valid move to suggest
                if (HasAnyValidMove(hand))
                    ShowButton();
            }
            else if (drought < DROUGHT_THRESHOLD)
            {
                HideButton();
                ClearHint();
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // BUTTON
        // ══════════════════════════════════════════════════════════════════════════

        private void BuildHintButton()
        {
            // Canvas
            GameObject canvasGO = new GameObject("HintCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 55; // above HUD

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Button — small, bottom-right area
            _hintButton = new GameObject("HintButton");
            _hintButton.transform.SetParent(canvasGO.transform, false);

            RectTransform rt = _hintButton.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.72f, 0.12f);
            rt.anchorMax = new Vector2(0.95f, 0.18f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            Image img = _hintButton.AddComponent<Image>();
            img.sprite = TileRenderer.CreateSolidRoundedRect(512, 128, 48, Color.white);
            img.type = Image.Type.Simple;
            img.color = new Color(0.15f, 0.55f, 0.35f, 0.85f); // dark green

            Button btn = _hintButton.AddComponent<Button>();
            ColorBlock cb = btn.colors;
            cb.normalColor = new Color(0.15f, 0.55f, 0.35f, 0.85f);
            cb.highlightedColor = new Color(0.2f, 0.65f, 0.4f, 0.9f);
            cb.pressedColor = new Color(0.1f, 0.4f, 0.25f, 0.9f);
            cb.fadeDuration = 0.08f;
            btn.colors = cb;
            btn.onClick.AddListener(OnHintClicked);

            // Label
            GameObject lblGO = new GameObject("Label");
            lblGO.transform.SetParent(_hintButton.transform, false);
            RectTransform lblRT = lblGO.AddComponent<RectTransform>();
            lblRT.anchorMin = Vector2.zero;
            lblRT.anchorMax = Vector2.one;
            lblRT.offsetMin = Vector2.zero;
            lblRT.offsetMax = Vector2.zero;

            _hintButtonLabel = lblGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = GameFont.GetUITMP();
            if (font != null) _hintButtonLabel.font = font;
            _hintButtonLabel.text = "HINT";
            _hintButtonLabel.fontSize = 18;
            _hintButtonLabel.fontStyle = FontStyles.Normal;
            _hintButtonLabel.color = new Color(0.85f, 1f, 0.9f, 1f);
            _hintButtonLabel.alignment = TextAlignmentOptions.Center;
            _hintButtonLabel.enableWordWrapping = false;

            _hintButton.SetActive(false);
            _buttonVisible = false;
        }

        private void ShowButton()
        {
            if (_buttonVisible) return;
            _buttonVisible = true;
            if (_hintButton != null) _hintButton.SetActive(true);
        }

        private void HideButton()
        {
            if (!_buttonVisible) return;
            _buttonVisible = false;
            if (_hintButton != null) _hintButton.SetActive(false);
        }

        private void OnHintClicked()
        {
            GameAudio.Instance?.PlayButtonClick();

            // Don't scan during resolution — board is mid-mutation
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;

            var hand = MatchController.Instance?.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;

            FindBestHint(hand);
            HideButton(); // one hint per jam — button goes away after use
        }

        /// <summary>Quick check: is there any valid word-forming drop?</summary>
        private bool HasAnyValidMove(PlayerHand hand)
        {
            var rules = RulesEngine.Instance;
            if (rules == null) return false;

            for (int slot = 0; slot < PlayerHand.HAND_SIZE; slot++)
            {
                char letter = hand.GetSlot(slot);
                if (letter == '\0') continue;

                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    int row = rules.GetLowestEmptyRow(col);
                    if (row < 0) continue;

                    var matches = rules.SimulateDrop(col, letter, MatchController.PLAYER_HUMAN);
                    if (matches != null && matches.Count > 0)
                        return true;
                }
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════════
        // HINT SCANNING
        // ══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans all hand letters × all columns for valid word-forming opportunities.
        /// Picks the highest-scoring one as the hint.
        /// </summary>
        private void FindBestHint(PlayerHand hand)
        {
            var rules = RulesEngine.Instance;
            if (rules == null) return;

            int bestScore = 0;
            int bestCol = -1;
            int bestRow = -1;
            char bestLetter = '\0';
            int bestSlot = -1;

            for (int slot = 0; slot < PlayerHand.HAND_SIZE; slot++)
            {
                char letter = hand.GetSlot(slot);
                if (letter == '\0') continue;

                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    int row = rules.GetLowestEmptyRow(col);
                    if (row < 0) continue;

                    var matches = rules.SimulateDrop(col, letter, MatchController.PLAYER_HUMAN);
                    if (matches == null || matches.Count == 0) continue;

                    int totalScore = 0;
                    for (int m = 0; m < matches.Count; m++)
                        totalScore += matches[m].Score;

                    if (totalScore > bestScore)
                    {
                        bestScore = totalScore;
                        bestCol = col;
                        bestRow = row;
                        bestLetter = letter;
                        bestSlot = slot;
                    }
                }
            }

            if (bestCol >= 0)
            {
                ShowHint(bestCol, bestRow, bestLetter, bestSlot);
            }
            else
            {
                // No valid play — shouldn't happen since we pre-check, but just hide
                HideButton();
//                 Debug.Log("[JamHint] No valid plays found on scan (edge case)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════════
        // HINT DISPLAY
        // ══════════════════════════════════════════════════════════════════════════

        private void ShowHint(int col, int row, char letter, int handSlot)
        {
            _hintActive = true;
            _hintCol = col;
            _hintRow = row;
            _hintLetter = letter;
            _hintHandSlot = handSlot;

            // Create a subtle glow at the hint position
            if (_hintGlow == null)
            {
                _hintGlow = new GameObject("JamHintGlow");
                var sr = _hintGlow.AddComponent<SpriteRenderer>();
                sr.sprite = TileRenderer.CreateSolidRoundedRect(128, 128, 24, Color.white);
                sr.sortingOrder = 3;
                _hintGlowSR = sr;
            }

            if (GridManager.Instance != null)
            {
                Vector3 worldPos = GridManager.Instance.CellToWorld(col, row);
                _hintGlow.transform.position = worldPos;
                float scale = GridManager.Instance.CellSize * 0.9f / 1.28f;
                _hintGlow.transform.localScale = new Vector3(scale, scale, 1f);
                _hintGlow.SetActive(true);
            }

            // Pulse the hand card
            if (HandManager.Instance != null)
            {
                var cards = HandManager.Instance.GetCardObjects();
                if (handSlot >= 0 && handSlot < cards.Length && cards[handSlot] != null)
                {
                    var sr = cards[handSlot].GetComponent<SpriteRenderer>();
                    if (sr != null)
                        sr.color = new Color(0.7f, 1f, 0.7f, 1f);
                }
            }

//             Debug.Log($"[JamHint] Hint: drop '{letter}' (slot {handSlot}) → col {col} row {row}");
        }

        /// <summary>Call when player drops a tile or scores — clears the hint.</summary>
        public void ClearHint()
        {
            if (!_hintActive) return;

            _hintActive = false;

            if (_hintGlow != null)
                _hintGlow.SetActive(false);

            // Restore hand card color
            if (HandManager.Instance != null && _hintHandSlot >= 0)
            {
                var cards = HandManager.Instance.GetCardObjects();
                if (_hintHandSlot < cards.Length && cards[_hintHandSlot] != null)
                {
                    var sr = cards[_hintHandSlot].GetComponent<SpriteRenderer>();
                    if (sr != null)
                        sr.color = Color.white;
                }
            }

            _hintCol = -1;
            _hintRow = -1;
            _hintHandSlot = -1;
        }
    }
}
