using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Event-indexed tutorial prompt overlay for Level mode.
    ///
    /// Reads LevelData.tutorialPrompts and subscribes to LevelController events.
    /// Each prompt fires at the gameplay moment that its `on` trigger names:
    ///   "start"             → LevelController.StartLevel
    ///   "first_word"        → OnFirstWord (first scoring drop this level)
    ///   "first_detonation"  → OnFirstDetonation (first primed-word detonation)
    ///   "first_chain"       → OnFirstChain (first chain-bonus drop)
    ///   "first_rewrite"     → OnFirstRewrite (first committed rewrite — L4 intro)
    ///
    /// Prompts stay on screen until the next one displaces them, or until the
    /// level ends. Empty tutorialPrompts ⇒ overlay stays hidden (most levels).
    ///
    /// Canvas sortingOrder = 120 (above HUD=50-ish, below modals=150).
    /// </summary>
    public class LevelTutorialOverlay : MonoBehaviour
    {
        public static LevelTutorialOverlay Instance { get; private set; }

        private Canvas _canvas;
        private CanvasGroup _group;
        private Text _text;

        // Pending prompts keyed by their `on` trigger; one prompt per event.
        private readonly Dictionary<string, string> _promptsByTrigger = new Dictionary<string, string>();

        private DG.Tweening.Tween _fadeTween;
        private Coroutine _autoHideCoroutine;

        // Auto-fade tuning. 4s visible + 0.5s fade matches mobile puzzle patterns
        // (Candy Crush / Royal Match land in the 3–5s window). Prompt vanishes when
        // its teaching job is done so the HUD can own ongoing guidance.
        private const float AUTO_HIDE_SECONDS = 4f;
        private const float AUTO_HIDE_FADE_SECONDS = 0.5f;

        private static readonly Color PANEL_BG   = new Color(0.08f, 0.06f, 0.20f, 0.82f);
        private static readonly Color TEXT_COLOR = new Color(1f, 0.92f, 0.78f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            SetVisibleImmediate(false);
        }

        private void Start()
        {
            // Subscribe once LevelController exists (SceneBootstrap creates it before us).
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnFirstWord       += HandleFirstWord;
                LevelController.Instance.OnFirstDetonation += HandleFirstDetonation;
                LevelController.Instance.OnFirstChain      += HandleFirstChain;
                LevelController.Instance.OnFirstRewrite    += HandleFirstRewrite;
                LevelController.Instance.OnLevelComplete   += HandleLevelEnded;
                LevelController.Instance.OnLevelFail       += HandleLevelEnded;
            }
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnFirstWord       -= HandleFirstWord;
                LevelController.Instance.OnFirstDetonation -= HandleFirstDetonation;
                LevelController.Instance.OnFirstChain      -= HandleFirstChain;
                LevelController.Instance.OnFirstRewrite    -= HandleFirstRewrite;
                LevelController.Instance.OnLevelComplete   -= HandleLevelEnded;
                LevelController.Instance.OnLevelFail       -= HandleLevelEnded;
            }
        }

        // ── Public entry point (called by LevelController.StartLevel) ───────────

        /// <summary>
        /// Loads prompts from the current level into the trigger map and shows
        /// the "start" prompt if present. No-op if the level has no prompts.
        /// </summary>
        public void OnLevelStarted(LevelData data)
        {
            _promptsByTrigger.Clear();
            if (data == null || data.tutorialPrompts == null || data.tutorialPrompts.Length == 0)
            {
                SetVisible(false);
                return;
            }

            for (int i = 0; i < data.tutorialPrompts.Length; i++)
            {
                var p = data.tutorialPrompts[i];
                if (p == null || string.IsNullOrEmpty(p.on) || string.IsNullOrEmpty(p.text)) continue;
                _promptsByTrigger[p.on] = p.text;
            }

            if (_promptsByTrigger.TryGetValue("start", out string startText))
                ShowPrompt(startText, persist: true);
            else
                SetVisible(false);
        }

        // ── Event handlers ──────────────────────────────────────────────────────

        private void HandleFirstWord()
        {
            if (_promptsByTrigger.TryGetValue("first_word", out string t)) ShowPrompt(t, persist: false);
        }

        private void HandleFirstDetonation()
        {
            if (_promptsByTrigger.TryGetValue("first_detonation", out string t)) ShowPrompt(t, persist: false);
        }

        private void HandleFirstChain()
        {
            if (_promptsByTrigger.TryGetValue("first_chain", out string t)) ShowPrompt(t, persist: false);
        }

        private void HandleFirstRewrite()
        {
            if (_promptsByTrigger.TryGetValue("first_rewrite", out string t)) ShowPrompt(t, persist: false);
        }

        private void HandleLevelEnded(int score, int starsOrShortfall)
        {
            // Fade the overlay out when a level ends — the completion / fail modal
            // takes the screen at that point. Cancel any pending auto-hide.
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            SetVisible(false);
        }

        /// <summary>
        /// Immediate, un-animated hide. Use when an external path needs to
        /// guarantee the overlay is gone (e.g., MenuUI.SetVisible, AbortLevel)
        /// regardless of whether a fade happens to be mid-tween. Fallback for
        /// the case where HandleLevelEnded's event-driven fade either didn't
        /// fire or was interrupted — without this the "Drop letters…" prompt
        /// can leak onto the main menu / Survival because its canvas is still
        /// active from the prior daily.
        /// </summary>
        public void ForceHide()
        {
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }
            _fadeTween?.Kill();
            _fadeTween = null;
            _promptsByTrigger.Clear();
            if (_group != null) _group.alpha = 0f;
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        // ── UI ──────────────────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("LevelTutorialCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 120;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            GameObject panel = new GameObject("Panel", typeof(RectTransform));
            panel.transform.SetParent(canvasGO.transform, false);
            RectTransform pRT = (RectTransform)panel.transform;
            // Top strip above the board area
            pRT.anchorMin = new Vector2(0.05f, 0.68f);
            pRT.anchorMax = new Vector2(0.95f, 0.78f);
            pRT.offsetMin = Vector2.zero;
            pRT.offsetMax = Vector2.zero;

            Image panelImg = panel.AddComponent<Image>();
            panelImg.color = PANEL_BG;
            panelImg.raycastTarget = false;

            _group = panel.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;

            GameObject textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(panel.transform, false);
            RectTransform tRT = (RectTransform)textGO.transform;
            tRT.anchorMin = new Vector2(0.04f, 0.08f);
            tRT.anchorMax = new Vector2(0.96f, 0.92f);
            tRT.offsetMin = Vector2.zero;
            tRT.offsetMax = Vector2.zero;

            _text = textGO.AddComponent<Text>();
            _text.font = MenuUI.GetFont();
            _text.text = "";
            _text.fontSize = 24;
            _text.color = TEXT_COLOR;
            _text.alignment = TextAnchor.MiddleCenter;
            _text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _text.verticalOverflow = VerticalWrapMode.Overflow;
        }

        private void ShowPrompt(string text, bool persist = false)
        {
            if (_text == null) return;
            _text.text = text;
            SetVisible(true);

            // Cancel any in-flight auto-hide from a prior prompt.
            if (_autoHideCoroutine != null) { StopCoroutine(_autoHideCoroutine); _autoHideCoroutine = null; }

            // Instructive prompts (start) persist until the player triggers the next event,
            // which replaces them naturally. Reactive prompts (first_word, first_detonation,
            // first_chain) explain something that just happened and fade after 4s.
            if (!persist)
                _autoHideCoroutine = StartCoroutine(AutoHideAfter(AUTO_HIDE_SECONDS));
        }

        private IEnumerator AutoHideAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            SetVisible(false, AUTO_HIDE_FADE_SECONDS);
            _autoHideCoroutine = null;
        }

        private void SetVisible(bool visible, float fadeDurationOverride = -1f)
        {
            if (_group == null) return;
            _fadeTween?.Kill();
            if (visible)
            {
                _canvas.gameObject.SetActive(true);
                float dur = fadeDurationOverride > 0f ? fadeDurationOverride : 0.25f;
                _fadeTween = UIAnimations.FadeIn(_group, duration: dur);
            }
            else
            {
                float dur = fadeDurationOverride > 0f ? fadeDurationOverride : 0.2f;
                _fadeTween = UIAnimations.FadeOut(_group,
                    onComplete: () => { if (_canvas != null) _canvas.gameObject.SetActive(false); },
                    duration: dur);
            }
        }

        private void SetVisibleImmediate(bool visible)
        {
            if (_group == null || _canvas == null) return;
            _group.alpha = visible ? 1f : 0f;
            _canvas.gameObject.SetActive(visible);
        }
    }
}
