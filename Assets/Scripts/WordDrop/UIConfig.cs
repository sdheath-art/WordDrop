using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Central ScriptableObject for all UI layout values.
    /// Create via Assets → Create → WordDrop → UIConfig.
    /// Place in Assets/Resources/UIConfig for auto-loading.
    /// </summary>
    [CreateAssetMenu(fileName = "UIConfig", menuName = "WordDrop/UIConfig")]
    public class UIConfig : ScriptableObject
    {
        // ═════════════════════════════════════════════════════════════════════
        // SINGLETON ACCESSOR
        // ═════════════════════════════════════════════════════════════════════

        private static UIConfig _instance;
        public static UIConfig Instance
        {
            get
            {
                if (_instance == null)
                    _instance = Resources.Load<UIConfig>("UIConfig");
                return _instance;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // GENERAL
        // ═════════════════════════════════════════════════════════════════════

        [Header("General")]
        public Vector2 referenceResolution = new Vector2(540f, 960f);
        public float canvasMatchWidthOrHeight = 0.5f;

        [Header("Common Colors")]
        public Color goldColor          = new Color(0.96f, 0.84f, 0.25f, 1f);
        public Color playerGreen        = new Color(0.200f, 0.851f, 0.424f, 1f);
        public Color aiOrange           = new Color(1.000f, 0.604f, 0.239f, 1f);
        public Color textWhite          = new Color(0.95f, 0.95f, 1f, 1f);
        public Color textDim            = new Color(0.65f, 0.65f, 0.75f, 1f);

        // ═════════════════════════════════════════════════════════════════════
        // MENU
        // ═════════════════════════════════════════════════════════════════════

        [Header("Menu — Panel")]
        public Color menuPanelBgColor = new Color(0.08f, 0.08f, 0.10f, 0.98f);

        [Header("Menu — Title")]
        public Vector2 menuTitleAnchorMin = new Vector2(0.05f, 0.88f);
        public Vector2 menuTitleAnchorMax = new Vector2(0.95f, 0.98f);
        public int menuTitleFontSize      = 64;
        public Color menuTitleColor       = new Color(0.96f, 0.84f, 0.25f, 1f);

        [Header("Menu — Best Score")]
        public Vector2 menuBestScoreAnchorMin = new Vector2(0.05f, 0.83f);
        public Vector2 menuBestScoreAnchorMax = new Vector2(0.95f, 0.88f);
        public int menuBestScoreFontSize      = 22;
        public Color menuBestScoreColor       = new Color(0.96f, 0.76f, 0.29f, 1f);

        [Header("Menu — Difficulty")]
        public Vector2 menuDiffLabelAnchorMin = new Vector2(0.05f, 0.74f);
        public Vector2 menuDiffLabelAnchorMax = new Vector2(0.95f, 0.82f);
        public int menuDiffLabelFontSize      = 22;
        public Color menuDiffLabelColor       = new Color(0.75f, 0.75f, 0.80f, 1f);
        public int menuDiffButtonFontSize     = 18;
        public float menuDiffButtonY0         = 0.67f;
        public float menuDiffButtonY1         = 0.74f;
        public Color menuDiffEasyColor        = new Color(0.25f, 0.65f, 0.35f, 1f);
        public Color menuDiffMediumColor      = new Color(0.80f, 0.65f, 0.15f, 1f);
        public Color menuDiffHardColor        = new Color(0.80f, 0.25f, 0.20f, 1f);

        [Header("Menu — AI Profile")]
        public Vector2 menuProfileLabelAnchorMin = new Vector2(0.05f, 0.59f);
        public Vector2 menuProfileLabelAnchorMax = new Vector2(0.95f, 0.66f);
        public int menuProfileLabelFontSize      = 20;
        public int menuProfileButtonFontSize     = 16;
        public float menuProfileButtonY0         = 0.52f;
        public float menuProfileButtonY1         = 0.59f;
        public Color menuProfileScorerColor      = new Color(0.30f, 0.55f, 0.75f, 1f);
        public Color menuProfileBlockerColor     = new Color(0.60f, 0.35f, 0.60f, 1f);
        public Color menuProfileHunterColor      = new Color(0.75f, 0.30f, 0.25f, 1f);

        [Header("Menu — Play Button")]
        public Vector2 menuPlayAnchorMin = new Vector2(0.10f, 0.42f);
        public Vector2 menuPlayAnchorMax = new Vector2(0.90f, 0.52f);
        public int menuPlayFontSize      = 40;
        public Color menuPlayBgColor     = new Color(0.96f, 0.63f, 0.16f, 1f); // 2026-06-24: warm orange CTA (was green)

        [Header("Menu — Daily Button")]
        public Vector2 menuDailyAnchorMin     = new Vector2(0.05f, 0.28f);
        public Vector2 menuDailyAnchorMax     = new Vector2(0.48f, 0.38f);
        public int menuDailyFontSize          = 26;
        public Color menuDailyBgColor         = new Color(0.20f, 0.45f, 0.80f, 1f);
        public Color menuDailyCompletedColor  = new Color(0.35f, 0.45f, 0.55f, 0.7f);
        public int menuDailyInfoFontSize      = 14;
        public Color menuDailyInfoColor       = new Color(0.60f, 0.75f, 0.90f, 1f);

        [Header("Menu — Blitz Button")]
        public Vector2 menuBlitzAnchorMin      = new Vector2(0.52f, 0.28f);
        public Vector2 menuBlitzAnchorMax      = new Vector2(0.95f, 0.38f);
        public int menuBlitzFontSize           = 24;
        public Color menuBlitzBgColor          = new Color(0.85f, 0.30f, 0.15f, 1f);
        public int menuBlitzBestScoreFontSize  = 14;
        public Color menuBlitzBestScoreColor   = new Color(0.85f, 0.50f, 0.25f, 1f);

        [Header("Menu — Debug Buttons")]
        public int menuDebugFontSize       = 13;
        public Color menuDebugBgColor      = new Color(0.3f, 0.3f, 0.4f, 0.5f);
        public Color menuDebugTextColor    = new Color(0.7f, 0.7f, 0.8f, 0.7f);

        // ═════════════════════════════════════════════════════════════════════
        // HUD
        // ═════════════════════════════════════════════════════════════════════

        [Header("HUD — Bar")]
        public float hudBarHeight       = 78f;
        // Spencer-picked purple (#391D78 / R 0.2228, G 0.1134, B 0.4716,
        // A 1.0). Eyedropper swatch from the Inspector Color picker —
        // saturated violet that sits on the hand-tray hue family but
        // reads more purple than navy.
        public Color hudBarBgColor      = new Color(0.2228f, 0.1134f, 0.4716f, 1.0f); // #391D78 — held at original while Spencer reworks HUD look in Photoshop

        [Header("HUD — Player Score")]
        public Vector2 hudPlayerLabelAnchorMin = new Vector2(0.10f, 0.35f);
        public Vector2 hudPlayerLabelAnchorMax = new Vector2(0.20f, 0.98f);
        public int hudPlayerLabelFontSize      = 22;
        public Vector2 hudPlayerNumAnchorMin   = new Vector2(0.20f, 0.35f);
        public Vector2 hudPlayerNumAnchorMax   = new Vector2(0.35f, 0.98f);
        public int hudPlayerNumFontSize        = 34;

        [Header("HUD — AI Score")]
        public Vector2 hudAILabelAnchorMin = new Vector2(0.68f, 0.08f);
        public Vector2 hudAILabelAnchorMax = new Vector2(0.80f, 0.92f);
        public int hudAILabelFontSize      = 22;
        public Vector2 hudAINumAnchorMin   = new Vector2(0.80f, 0.08f);
        public Vector2 hudAINumAnchorMax   = new Vector2(0.95f, 0.92f);
        public int hudAINumFontSize        = 34;

        [Header("HUD — Swap & Rewrite Counters")]
        public Vector2 hudSwapAnchorMin    = new Vector2(0.10f, 0.02f);
        public Vector2 hudSwapAnchorMax    = new Vector2(0.22f, 0.35f);
        public int hudSwapFontSize         = 12;
        public Color hudSwapColor          = new Color(0.68f, 0.68f, 0.75f, 1f);
        public Vector2 hudRewriteAnchorMin = new Vector2(0.22f, 0.02f);
        public Vector2 hudRewriteAnchorMax = new Vector2(0.35f, 0.35f);
        public int hudRewriteFontSize      = 12;
        public Color hudSwapDimColor       = new Color(0.38f, 0.38f, 0.42f, 0.60f);

        [Header("HUD — Turn Counter")]
        public Vector2 hudTurnAnchorMin = new Vector2(0.35f, 0.08f);
        public Vector2 hudTurnAnchorMax = new Vector2(0.65f, 0.92f);
        public int hudTurnFontSize      = 28;
        public Color hudTurnColor       = new Color(0.80f, 0.80f, 0.86f, 1f);
        public Color hudTurnWarnColor   = new Color(1.00f, 0.75f, 0.20f, 1f);
        public Color hudTurnDangerColor = new Color(1.00f, 0.32f, 0.28f, 1f);

        [Header("HUD — Word Found Overlay")]
        public int hudWordFoundFontSize    = 30;
        public Color hudWordFoundBgColor   = new Color(0.04f, 0.04f, 0.06f, 0.75f);
        public float hudWordFoundDuration  = 1.4f;

        // ═════════════════════════════════════════════════════════════════════
        // GAME OVER
        // ═════════════════════════════════════════════════════════════════════

        [Header("Game Over — Panel")]
        public Vector2 gameOverPanelSize = new Vector2(460f, 420f);
        public Color gameOverPanelBg     = new Color(0.118f, 0.173f, 0.412f, 0.98f);

        [Header("Game Over — Title")]
        public int gameOverTitleFontSize   = 48;
        public Color gameOverTitleColor    = new Color(0.961f, 0.761f, 0.294f, 1f);

        [Header("Game Over — Winner")]
        public int gameOverWinnerFontSize  = 34;
        public Color gameOverDefeatColor   = new Color(0.90f, 0.30f, 0.30f, 1f);

        [Header("Game Over — Scores")]
        public int gameOverScoreFontSize   = 30;
        public int gameOverTurnsFontSize   = 20;
        public int gameOverBestFontSize    = 22;

        [Header("Game Over — Buttons")]
        public int gameOverPlayAgainFontSize = 32;
        public Color gameOverButtonColor     = new Color(0.961f, 0.761f, 0.294f, 1f);
        public int gameOverShareFontSize     = 26;
        public Color gameOverShareColor      = new Color(0.40f, 0.65f, 0.90f, 1f);

        // ═════════════════════════════════════════════════════════════════════
        // BONUS POPUP
        // ═════════════════════════════════════════════════════════════════════

        [Header("Bonus Popup — Timing")]
        public float popupBaseFontSize     = 7f;
        public float popupHoldDuration     = 0.6f;
        public float popupFadeDuration     = 0.25f;
        public float popupPopInDuration    = 0.12f;

        [Header("Bonus Popup — Colors")]
        public Color popupDetonationColor  = new Color(1f, 0.85f, 0.2f, 1f);
        public Color popupHeatColor        = new Color(1f, 0.5f, 0.1f, 1f);
        public Color popupFinalPushColor   = new Color(1f, 0.95f, 0.3f, 1f);
        public Color popupComebackColor    = new Color(0.3f, 0.9f, 0.4f, 1f);
        public Color popupRefundColor      = new Color(0.3f, 0.85f, 0.9f, 1f);

        [Header("Bonus Popup — Scales")]
        public float popupWordScoreScale   = 0.9f;
        public float popupDetonationScale  = 1.5f;
        public float popupHeatScale        = 0.75f;
        public float popupFinalPushScale   = 1.1f;
        public float popupComebackScale    = 1.1f;
        public float popupRefundScale      = 0.9f;
        public float popupChainMaxScale    = 1.8f;

        [Header("Bonus Popup — Outline & Shadow")]
        public float popupOutlineWidth     = 0.2f;
        public Color popupOutlineColor     = new Color(0f, 0f, 0f, 0.86f);
        public float popupFloatDistance    = 0.4f;  // how far the popup floats up during hold
        public float popupFloatSpeed       = 0.8f;  // float speed during fade phase

        // ═════════════════════════════════════════════════════════════════════
        // FONTS
        // ═════════════════════════════════════════════════════════════════════

        [Header("Fonts — assign TMP font assets here")]
        [Tooltip("Main font for popups and world-space text (e.g. NunitoBlack SDF)")]
        public TMPro.TMP_FontAsset popupFont;

        [Tooltip("Heavy font for HUD scores and scoring display (e.g. NunitoExtraBold SDF)")]
        public TMPro.TMP_FontAsset hudScoreFont;

        [Tooltip("UI font for menus and buttons (legacy Unity Font)")]
        public Font uiFont;
    }
}
