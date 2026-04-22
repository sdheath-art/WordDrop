using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Bonus Mode HUD — minimal functional layer (B3). Procedurally-built canvas
    /// with three elements:
    ///   1. Chain meter fill bar (bottom-anchored, shows ChainMeter.FillRatio)
    ///   2. "BONUS!" banner text (mid-screen, visible only while IsActive)
    ///   3. Running word counter (below banner, shows BonusMode.WordCount ×N)
    /// Feel pass (golden aura, slam-in, music crossfade, haptic, chime) is B4.
    /// </summary>
    public class BonusHUD : MonoBehaviour
    {
        public static BonusHUD Instance { get; private set; }

        private Canvas _canvas;
        private Image _meterFill;
        private Image _meterBg;
        private TextMeshProUGUI _bannerText;
        private TextMeshProUGUI _multiplierText;

        private static readonly Color METER_BG       = new Color(0.08f, 0.04f, 0.16f, 0.65f);
        private static readonly Color METER_FILL     = new Color(1.00f, 0.72f, 0.18f, 0.95f);  // amber gold
        private static readonly Color METER_FILL_MAX = new Color(1.00f, 0.48f, 0.10f, 1.00f);  // deep orange when full
        private static readonly Color BANNER_COLOR   = new Color(1.00f, 0.88f, 0.35f, 1f);
        private static readonly Color MULT_COLOR     = new Color(1.00f, 0.70f, 0.20f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            HideBonusUI();
        }

        private void OnEnable()
        {
            Subscribe();
        }

        private void Start()
        {
            Subscribe();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void Subscribe()
        {
            if (ChainMeter.Instance != null)
            {
                ChainMeter.Instance.OnFillChanged -= HandleFillChanged;
                ChainMeter.Instance.OnFillChanged += HandleFillChanged;
            }
            if (BonusMode.Instance != null)
            {
                BonusMode.Instance.OnBonusArmed     -= HandleBonusArmed;
                BonusMode.Instance.OnBonusArmed     += HandleBonusArmed;
                BonusMode.Instance.OnBonusEnter     -= HandleBonusEnter;
                BonusMode.Instance.OnBonusEnter     += HandleBonusEnter;
                BonusMode.Instance.OnBonusWordScored -= HandleBonusWordScored;
                BonusMode.Instance.OnBonusWordScored += HandleBonusWordScored;
                BonusMode.Instance.OnBonusExit      -= HandleBonusExit;
                BonusMode.Instance.OnBonusExit      += HandleBonusExit;
            }
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnLevelStarted -= HandleLevelStarted;
                LevelController.Instance.OnLevelStarted += HandleLevelStarted;
            }
        }

        private void Unsubscribe()
        {
            if (ChainMeter.Instance != null)
                ChainMeter.Instance.OnFillChanged -= HandleFillChanged;
            if (BonusMode.Instance != null)
            {
                BonusMode.Instance.OnBonusArmed      -= HandleBonusArmed;
                BonusMode.Instance.OnBonusEnter      -= HandleBonusEnter;
                BonusMode.Instance.OnBonusWordScored -= HandleBonusWordScored;
                BonusMode.Instance.OnBonusExit       -= HandleBonusExit;
            }
            if (LevelController.Instance != null)
                LevelController.Instance.OnLevelStarted -= HandleLevelStarted;
        }

        // Chain meter is hidden on Level-mode levels that don't opt in via
        // hudFlags.showChainMeter (design commit: visible from L26+ only so
        // Bonus Mode is introduced as a single conceptual unit). Abort/menu
        // paths (data == null) also stay hidden — the menu should never see
        // the meter flash during the slide transition. Survival/Classic are
        // kill-switched in production; if resurrected, they'll need a
        // separate show hook.
        private void HandleLevelStarted(LevelData data)
        {
            bool showMeter = data != null
                          && data.hudFlags != null
                          && data.hudFlags.showChainMeter;

            if (_meterBg != null)
                _meterBg.gameObject.SetActive(showMeter);
        }

        // ── UI build ──────────────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("BonusHUDCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 75; // above main HUD (50), below transitions (100)

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 1920);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // White 1px sprite — required for Image.Type.Filled to respect fillAmount.
            // Without a sprite, Unity renders Image as a solid color block ignoring
            // fill type. Allocated once, shared between bg + fill.
            Sprite whiteSprite = CreateWhiteSprite();

            // Meter background + fill, anchored just below the top HUD bar.
            // (Old placement at y=0.24 overlapped the board because bottomReserve=25%.)
            GameObject bgGO = new GameObject("MeterBg");
            bgGO.transform.SetParent(canvasGO.transform, false);
            _meterBg = bgGO.AddComponent<Image>();
            _meterBg.color  = METER_BG;
            _meterBg.sprite = whiteSprite;
            RectTransform bgRT = _meterBg.rectTransform;
            bgRT.anchorMin = new Vector2(0.12f, 0.88f);
            bgRT.anchorMax = new Vector2(0.88f, 0.88f);
            bgRT.pivot     = new Vector2(0.5f, 0.5f);
            bgRT.sizeDelta = new Vector2(0f, 22f);
            bgRT.anchoredPosition = Vector2.zero;

            // Default-inactive: only levels that explicitly set
            // hudFlags.showChainMeter activate the meter bar via
            // HandleLevelStarted. Prevents the meter rendering during
            // scene transitions (slide from menu) before the level's
            // OnLevelStarted event has fired.
            bgGO.SetActive(false);

            GameObject fillGO = new GameObject("MeterFill");
            fillGO.transform.SetParent(bgGO.transform, false);
            _meterFill = fillGO.AddComponent<Image>();
            _meterFill.color      = METER_FILL;
            _meterFill.sprite     = whiteSprite;
            _meterFill.type       = Image.Type.Filled;
            _meterFill.fillMethod = Image.FillMethod.Horizontal;
            _meterFill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _meterFill.fillAmount = 0f;
            RectTransform fillRT = _meterFill.rectTransform;
            fillRT.anchorMin = Vector2.zero;
            fillRT.anchorMax = Vector2.one;
            fillRT.offsetMin = new Vector2(2f, 2f);
            fillRT.offsetMax = new Vector2(-2f, -2f);

            // "CHAIN" label above the bar so it reads as UI, not decoration.
            GameObject labelGO = new GameObject("MeterLabel");
            labelGO.transform.SetParent(bgGO.transform, false);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            labelText.text = "CHAIN";
            labelText.color = new Color(1f, 0.88f, 0.50f, 0.80f);
            labelText.fontSize = 22;
            labelText.alignment = TextAlignmentOptions.Left;
            labelText.fontStyle = FontStyles.Bold;
            labelText.enableWordWrapping = false;
            RectTransform labelRT = labelText.rectTransform;
            labelRT.anchorMin = new Vector2(0f, 1f);
            labelRT.anchorMax = new Vector2(0f, 1f);
            labelRT.pivot     = new Vector2(0f, 0f);
            labelRT.sizeDelta = new Vector2(200f, 28f);
            labelRT.anchoredPosition = new Vector2(0f, 2f);

            // Banner text — "BONUS!" big gold, mid-screen.
            GameObject bannerGO = new GameObject("BonusBanner");
            bannerGO.transform.SetParent(canvasGO.transform, false);
            _bannerText = bannerGO.AddComponent<TextMeshProUGUI>();
            _bannerText.text = "BONUS!";
            _bannerText.color = BANNER_COLOR;
            _bannerText.fontSize = 160;
            _bannerText.alignment = TextAlignmentOptions.Center;
            _bannerText.fontStyle = FontStyles.Normal;
            _bannerText.enableWordWrapping = false;
            RectTransform bannerRT = _bannerText.rectTransform;
            bannerRT.anchorMin = new Vector2(0.5f, 0.70f);
            bannerRT.anchorMax = new Vector2(0.5f, 0.70f);
            bannerRT.pivot     = new Vector2(0.5f, 0.5f);
            bannerRT.sizeDelta = new Vector2(900f, 200f);
            bannerRT.anchoredPosition = Vector2.zero;

            // Running multiplier display — "×3" under banner.
            GameObject multGO = new GameObject("BonusMult");
            multGO.transform.SetParent(canvasGO.transform, false);
            _multiplierText = multGO.AddComponent<TextMeshProUGUI>();
            _multiplierText.text = "";
            _multiplierText.color = MULT_COLOR;
            _multiplierText.fontSize = 80;
            _multiplierText.alignment = TextAlignmentOptions.Center;
            _multiplierText.fontStyle = FontStyles.Normal;
            _multiplierText.enableWordWrapping = false;
            RectTransform multRT = _multiplierText.rectTransform;
            multRT.anchorMin = new Vector2(0.5f, 0.60f);
            multRT.anchorMax = new Vector2(0.5f, 0.60f);
            multRT.pivot     = new Vector2(0.5f, 0.5f);
            multRT.sizeDelta = new Vector2(600f, 160f);
            multRT.anchoredPosition = Vector2.zero;
        }

        private void HideBonusUI()
        {
            if (_bannerText != null) _bannerText.gameObject.SetActive(false);
            if (_multiplierText != null) _multiplierText.gameObject.SetActive(false);
        }

        private static Sprite _cachedWhiteSprite;
        private static Sprite CreateWhiteSprite()
        {
            if (_cachedWhiteSprite != null) return _cachedWhiteSprite;
            Texture2D tex = Texture2D.whiteTexture;
            _cachedWhiteSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
            return _cachedWhiteSprite;
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void HandleFillChanged(float ratio)
        {
            if (_meterFill == null) return;
            _meterFill.fillAmount = ratio;
            _meterFill.color = ratio >= 0.999f ? METER_FILL_MAX : METER_FILL;
        }

        private void HandleBonusArmed()
        {
            // Armed state visible only via the full meter — let the fill-color
            // swap in HandleFillChanged signal "ready". No banner yet (B4 slam-in).
        }

        private void HandleBonusEnter()
        {
            if (_bannerText != null) _bannerText.gameObject.SetActive(true);
            if (_multiplierText != null)
            {
                _multiplierText.gameObject.SetActive(true);
                int drops = BonusMode.Instance != null ? BonusMode.Instance.DropsRemaining : 5;
                _multiplierText.text = $"{drops} FREE MOVES";
            }
        }

        private void HandleBonusWordScored(int wordCount)
        {
            // Redesign 2026-04-18: bonus is now "5 free moves" not a multiplier mode.
            // Text updates via NotifyDropCompleted countdown — see Update-style refresh
            // below (we poll DropsRemaining each frame rather than subscribing, since
            // the drop-count is the live state we care about).
        }

        private void Update()
        {
            if (_multiplierText == null) return;
            if (BonusMode.Instance == null) return;
            if (!BonusMode.Instance.IsActive) return;
            int drops = BonusMode.Instance.DropsRemaining;
            _multiplierText.text = drops == 1 ? "1 FREE MOVE" : $"{drops} FREE MOVES";
        }

        private void HandleBonusExit(int banked, string reason)
        {
            HideBonusUI();
            if (_meterFill != null)
            {
                _meterFill.fillAmount = 0f;
                _meterFill.color = METER_FILL;
            }
        }
    }
}
