using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Full-screen color wash for big detonation moments. Briefly tints the
    /// entire screen (180ms) to punch home that "something big just happened" —
    /// the Candy Crush color-bomb-activation screen tint. Stacks on top of
    /// BigBurstFlash (beam) + RadialBurst (center) + SparkleSpray (stars).
    /// Big-moment gated by callers.
    /// </summary>
    public class ScreenFlash : MonoBehaviour
    {
        public static ScreenFlash Instance { get; private set; }

        private const float FADE_IN_DUR  = 0.04f;
        private const float HOLD_DUR     = 0.03f;
        private const float FADE_OUT_DUR = 0.15f;
        private const float PEAK_ALPHA   = 0.35f;

        private Canvas _canvas;
        private Image _wash;
        private Coroutine _active;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("ScreenFlash");
                go.AddComponent<ScreenFlash>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("ScreenFlashCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 80; // above BonusHUD (75), below modal transitions

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);

            // Full-screen wash image.
            GameObject washGO = new GameObject("Wash");
            washGO.transform.SetParent(canvasGO.transform, false);
            _wash = washGO.AddComponent<Image>();
            _wash.sprite = CreateWhiteSprite();
            _wash.raycastTarget = false;
            _wash.color = new Color(1f, 1f, 1f, 0f);
            RectTransform rt = _wash.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// Flash the screen briefly. tint supplies the color (alpha is managed
        /// internally). intensity 0-1 scales peak alpha.
        /// </summary>
        public void Play(Color tint, float intensity = 1f)
        {
            if (_wash == null) return;
            intensity = Mathf.Clamp01(intensity);
            float peak = PEAK_ALPHA * intensity;

            if (_active != null) StopCoroutine(_active);
            _active = StartCoroutine(FlashCoroutine(tint, peak));
        }

        private IEnumerator FlashCoroutine(Color tint, float peakAlpha)
        {
            Color c = tint;
            c.a = 0f;
            _wash.color = c;

            // Fade in
            float elapsed = 0f;
            while (elapsed < FADE_IN_DUR)
            {
                elapsed += Time.unscaledDeltaTime;
                float a = Mathf.Lerp(0f, peakAlpha, elapsed / FADE_IN_DUR);
                c.a = a;
                _wash.color = c;
                yield return null;
            }

            // Hold
            c.a = peakAlpha;
            _wash.color = c;
            yield return new WaitForSecondsRealtime(HOLD_DUR);

            // Fade out
            elapsed = 0f;
            while (elapsed < FADE_OUT_DUR)
            {
                elapsed += Time.unscaledDeltaTime;
                float a = Mathf.Lerp(peakAlpha, 0f, elapsed / FADE_OUT_DUR);
                c.a = a;
                _wash.color = c;
                yield return null;
            }

            c.a = 0f;
            _wash.color = c;
            _active = null;
        }

        private static Sprite _cachedWhiteSprite;
        private static Sprite CreateWhiteSprite()
        {
            if (_cachedWhiteSprite != null) return _cachedWhiteSprite;
            Texture2D tex = Texture2D.whiteTexture;
            _cachedWhiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return _cachedWhiteSprite;
        }
    }
}
