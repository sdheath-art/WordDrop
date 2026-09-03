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
        // 3D ICON TUNING  (coin + heart)  — 2026-07-30
        // ═════════════════════════════════════════════════════════════════════
        //
        // The icons are BAKED renders, so these are runtime tints applied to the Image —
        // they MULTIPLY the art. That means they can darken and re-tint freely, but they
        // cannot make an icon brighter than its source render. If you need brighter than
        // 1.0, the render itself has to change (ask me and I'll re-render).
        //
        // Applied wherever the icon is built: level-map pills, in-game HUD counter,
        // vault REWARD panel, and the flying coins in both the cascade and chest bursts —
        // so a change here keeps every instance consistent.

        [Header("Coin — colour")]
        [Tooltip("Rotate the hue. -0.5..0.5 is a full turn either way. 0 = the render as-is.")]
        [Range(-0.5f, 0.5f)] public float coinHue = 0f;
        [Tooltip("1 = unchanged, 0 = greyscale, >1 = more vivid.")]
        [Range(0f, 2f)] public float coinSaturation = 1f;   // was 2f. At 2 the shader's saturate() clamped
                                            // virtually the whole coin to max saturation, flattening
                                            // the gradient into hard contour bands that read as "bends"
                                            // in the bevel. The contrast now lives in the render
                                            // instead, so the boost is no longer needed. 2026-07-30.
        [Tooltip("Multiply. Good for darkening; on already-bright gold it clamps and stops lifting.")]
        [Range(0f, 2f)] public float coinValue = 1.03f;
        [Tooltip("Washes toward white (+) or black (-). THIS is the one that can genuinely brighten.")]
        [Range(-1f, 1f)] public float coinLightness = 0f;

        [Header("Coin — size")]
        [Tooltip("ONE lever for every coin in the game: level-map pill, in-game HUD counter, " +
                 "vault REWARD panel, the flying cascade coins and the chest-burst coins.")]
        [Range(0.4f, 2.5f)] public float coinSizeScale = 1f;
        [Tooltip("Base size in the level-map pill (px), before the scale above.")]
        [Range(24f, 110f)] public float coinIconSize = 60f;
        [Tooltip("Base size in the in-game HUD counter (px).")]
        [Range(16f, 80f)] public float coinHudIconSize = 36f;
        [Tooltip("Base size in the vault REWARD panel (px).")]
        [Range(24f, 110f)] public float coinRewardIconSize = 60f;

        [Header("Heart — colour")]
        [Range(-0.5f, 0.5f)] public float heartHue = 0f;
        [Range(0f, 2f)] public float heartSaturation = 1.1f;
        [Range(0f, 2f)] public float heartValue = 1.33f;
        [Tooltip("Washes toward white (+) or black (-). THIS is the one that can genuinely brighten.")]
        [Range(-1f, 1f)] public float heartLightness = 0f;

        [Header("Heart — size")]
        [Range(0.4f, 2.5f)] public float heartSizeScale = 1f;
        [Tooltip("Base size in the level-map pill (px), before the scale above.")]
        [Range(24f, 110f)] public float heartIconSize = 60f;

        // Computed sizes — every call site reads these, so one scale moves them together.
        public static float CoinPillSize   => Instance != null ? Instance.coinIconSize       * Instance.coinSizeScale  : 60f;
        public static float CoinHudSize    => Instance != null ? Instance.coinHudIconSize    * Instance.coinSizeScale  : 36f;
        public static float CoinRewardSize => Instance != null ? Instance.coinRewardIconSize * Instance.coinSizeScale  : 60f;
        public static float HeartPillSize  => Instance != null ? Instance.heartIconSize      * Instance.heartSizeScale : 60f;

        // ── Live icon registry (Play-mode tuning) ───────────────────────────────
        // Icons are BUILT ONCE, so moving a slider mid-game would otherwise do nothing until the
        // screen was rebuilt. Every icon registers itself here; RefreshIcons() re-applies size,
        // material and shadow offset to all of them, and the custom UIConfig editor calls it on
        // every slider change. Dial values in live, then tell me the numbers to lock in. 2026-07-30.
        public enum IconSlot { CoinPill, CoinHud, CoinReward, HeartPill }

        private static readonly System.Collections.Generic.List<
            System.Collections.Generic.KeyValuePair<UnityEngine.UI.Graphic, IconSlot>> _liveIcons =
            new System.Collections.Generic.List<
                System.Collections.Generic.KeyValuePair<UnityEngine.UI.Graphic, IconSlot>>();

        public static float SizeFor(IconSlot slot)
        {
            switch (slot)
            {
                case IconSlot.CoinPill:   return CoinPillSize;
                case IconSlot.CoinHud:    return CoinHudSize;
                case IconSlot.CoinReward: return CoinRewardSize;
                default:                  return HeartPillSize;
            }
        }

        public static Material MaterialFor(IconSlot slot)
            => slot == IconSlot.HeartPill ? HeartIconMaterial : CoinIconMaterial;

        /// <summary>Call AFTER the drop shadow is added, so the shadow offset can track the size.</summary>
        public static void RegisterIcon(UnityEngine.UI.Graphic g, IconSlot slot)
        {
            if (g == null) return;
            _liveIcons.RemoveAll(e => e.Key == null);          // prune destroyed icons
            _liveIcons.Add(new System.Collections.Generic.KeyValuePair<UnityEngine.UI.Graphic, IconSlot>(g, slot));
            ApplyToIcon(g, slot);
        }

        private static void ApplyToIcon(UnityEngine.UI.Graphic g, IconSlot slot)
        {
            if (g == null) return;
            float size = SizeFor(slot);
            if (g.rectTransform != null) g.rectTransform.sizeDelta = new Vector2(size, size);
            // null is fine — Graphic falls back to the default UI material, which is exactly what
            // we want at neutral values (stock shader, zero risk).
            g.material = MaterialFor(slot);
            var sh = g.GetComponent<UnityEngine.UI.Shadow>();
            if (sh != null) sh.effectDistance = new Vector2(0f, -Mathf.Max(1f, size * 0.035f));
            g.SetMaterialDirty();
            g.SetVerticesDirty();
        }

        /// <summary>Re-apply every slider to every live icon. Safe to call any time.</summary>
        public static void RefreshIcons()
        {
            _liveIcons.RemoveAll(e => e.Key == null);
            foreach (var e in _liveIcons) ApplyToIcon(e.Key, e.Value);
        }

        // ── HSV materials ───────────────────────────────────────────────────────
        // Image.color can only MULTIPLY, so it can darken but never rotate hue or raise
        // saturation/brightness. These wrap the WordDrop/IconHSV shader, which does the
        // conversion per-pixel. One shared material per icon type, rebuilt when values change.
        private static Material _coinMat, _heartMat;
        private static Vector4  _coinMatKey = Vector4.one * -999f, _heartMatKey = Vector4.one * -999f;

        private static Material BuildMat(ref Material mat, ref Vector4 key, float h, float sat, float val, float lit)
        {
            var want = new Vector4(h, sat, val, lit);
            if (mat != null && key == want) return mat;
            var sh = Shader.Find("WordDrop/IconHSV");
            if (sh == null) return null;                       // shader missing -> callers leave material null
            if (mat == null) mat = new Material(sh) { hideFlags = HideFlags.DontSave };
            mat.SetFloat("_HueShift", h);
            mat.SetFloat("_Saturation", sat);
            mat.SetFloat("_Value", val);
            mat.SetFloat("_Lightness", lit);
            key = want;
            return mat;
        }

        /// <summary>Material for coin icons, or null to leave the default. Null when the values
        /// are neutral, so the default UI material is used and nothing changes.</summary>
        public static Material CoinIconMaterial
        {
            get
            {
                var c = Instance;
                if (c == null) return null;
                if (Mathf.Approximately(c.coinHue, 0f) && Mathf.Approximately(c.coinSaturation, 1f)
                    && Mathf.Approximately(c.coinValue, 1f) && Mathf.Approximately(c.coinLightness, 0f)) return null;
                return BuildMat(ref _coinMat, ref _coinMatKey, c.coinHue, c.coinSaturation, c.coinValue, c.coinLightness);
            }
        }

        /// <summary>Material for the heart icon, or null to leave the default.</summary>
        public static Material HeartIconMaterial
        {
            get
            {
                var c = Instance;
                if (c == null) return null;
                if (Mathf.Approximately(c.heartHue, 0f) && Mathf.Approximately(c.heartSaturation, 1f)
                    && Mathf.Approximately(c.heartValue, 1f) && Mathf.Approximately(c.heartLightness, 0f)) return null;
                return BuildMat(ref _heartMat, ref _heartMatKey, c.heartHue, c.heartSaturation, c.heartValue, c.heartLightness);
            }
        }

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
