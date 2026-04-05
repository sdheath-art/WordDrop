using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Screen-center combo counter that appears during chain detonations.
    /// Shows "CHAIN x2", "CHAIN x3" etc. with escalating size and color.
    /// Stays on screen until the chain resolves, then fades out.
    /// </summary>
    public class ChainCounter : MonoBehaviour
    {
        public static ChainCounter Instance { get; private set; }

        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private TextMeshProUGUI _label;
        private RectTransform _labelRT;
        private int _currentDepth;
        private bool _visible;
        private Coroutine _fadeCoroutine;

        // Colors escalate with chain depth
        private static readonly Color CHAIN_2_COLOR = new Color(1.00f, 0.84f, 0.42f, 1f);  // gold
        private static readonly Color CHAIN_3_COLOR = new Color(1.00f, 0.56f, 0.67f, 1f);  // pink
        private static readonly Color CHAIN_4_COLOR = new Color(1.00f, 0.95f, 0.85f, 1f);  // bright warm white

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Build();
        }

        private void Build()
        {
            // Canvas — overlay, above HUD
            GameObject canvasGO = new GameObject("ChainCounterCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 60; // above HUD (50)

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            _canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // Label — centered on screen, slightly above middle
            GameObject labelGO = new GameObject("ChainLabel");
            labelGO.transform.SetParent(canvasGO.transform, false);

            _labelRT = labelGO.AddComponent<RectTransform>();
            _labelRT.anchorMin = new Vector2(0.5f, 0.55f);
            _labelRT.anchorMax = new Vector2(0.5f, 0.55f);
            _labelRT.pivot = new Vector2(0.5f, 0.5f);
            _labelRT.sizeDelta = new Vector2(400f, 100f);

            _label = labelGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = GameFont.GetUITMP();
            if (font != null) _label.font = font;
            _label.fontSize = 48f;
            _label.fontStyle = FontStyles.Bold;
            _label.alignment = TextAlignmentOptions.Center;
            _label.enableWordWrapping = false;
            _label.overflowMode = TextOverflowModes.Overflow;
            _label.outlineWidth = 0.2f;
            _label.outlineColor = new Color32(0, 0, 0, 200);

            _visible = false;
            _currentDepth = 0;
        }

        /// <summary>
        /// Call when a detonation fires at the given chain depth.
        /// Shows/updates the counter if depth >= 1.
        /// </summary>
        public void OnDetonation(int chainDepth)
        {
            if (chainDepth < 1) return;
            GameAudio.Instance?.PlayChainReaction();

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            _currentDepth = chainDepth;
            int multiplier = chainDepth + 1; // depth 1 = x2, depth 2 = x3, etc.
            _label.text = $"CHAIN x{multiplier}";

            // Escalating color
            if (chainDepth >= 3)
                _label.color = CHAIN_4_COLOR;
            else if (chainDepth >= 2)
                _label.color = CHAIN_3_COLOR;
            else
                _label.color = CHAIN_2_COLOR;

            // Escalating font size
            float targetSize = 48f + (chainDepth - 1) * 12f;
            _label.fontSize = Mathf.Min(targetSize, 84f);

            if (!_visible)
            {
                // First appearance — punch in
                _visible = true;
                _canvasGroup.alpha = 1f;
                _labelRT.localScale = Vector3.one * 1.3f;
                _labelRT.DOScale(Vector3.one, 0.15f).SetEase(Ease.OutBack);
            }
            else
            {
                // Upgrade — punch scale to show it changed
                _labelRT.DOKill();
                _labelRT.localScale = Vector3.one * 1.2f;
                _labelRT.DOScale(Vector3.one, 0.12f).SetEase(Ease.OutBack);

                // Haptic feedback — medium pulse on chain multiplier upgrade
                HapticsManager.Medium();
            }
        }

        /// <summary>
        /// Hard reset — call at match start to ensure no stale chain text.
        /// </summary>
        public void ResetForNewMatch()
        {
            if (_fadeCoroutine != null) { StopCoroutine(_fadeCoroutine); _fadeCoroutine = null; }
            _canvasGroup.alpha = 0f;
            _visible = false;
            _currentDepth = 0;
        }

        /// <summary>
        /// Call when the chain resolution completes. Fades out the counter.
        /// </summary>
        public void OnChainComplete()
        {
            if (!_visible) return;
            _fadeCoroutine = StartCoroutine(FadeOut());
        }

        private IEnumerator FadeOut()
        {
            // Brief hold so player can read the final chain level
            yield return new WaitForSecondsRealtime(0.35f);

            float dur = 0.2f;
            float elapsed = 0f;
            while (elapsed < dur)
            {
                elapsed += Time.unscaledDeltaTime;
                _canvasGroup.alpha = 1f - (elapsed / dur);
                yield return null;
            }

            _canvasGroup.alpha = 0f;
            _visible = false;
            _currentDepth = 0;
            _fadeCoroutine = null;
        }
    }
}
