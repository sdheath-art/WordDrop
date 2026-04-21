using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Canonical UI design tokens for WordDrop — sizing, spacing, color, typography.
    /// Mirrors <c>project_wordrop_ui_style_guide.md</c> (single source of truth).
    ///
    /// Every UI component must source its numbers/colors/sizes from here rather than
    /// hardcoding values. Prevents drift, enables global re-theming, and satisfies the
    /// Phase 11 audit checklist.
    /// </summary>
    public static class UILayout
    {
        // ═══════════════════════════════════════════════════════════════════════
        // SPACING — 8px scale, use these multiples only
        // ═══════════════════════════════════════════════════════════════════════

        public const float SpaceXXS = 4f;
        public const float SpaceXS  = 8f;
        public const float SpaceSM  = 12f;
        public const float SpaceMD  = 16f;
        public const float SpaceLG  = 24f;
        public const float SpaceXL  = 32f;
        public const float SpaceXXL = 48f;

        // ═══════════════════════════════════════════════════════════════════════
        // MODAL SIZING (fractions of screen)
        // ═══════════════════════════════════════════════════════════════════════

        public struct ModalSpec
        {
            public float WidthFraction;
            public float MaxHeightFraction;
            public float CornerRadius;
            public float Padding;
        }

        public static readonly ModalSpec ModalLevelCompleted =
            new ModalSpec { WidthFraction = 0.85f, MaxHeightFraction = 0.70f, CornerRadius = 24f, Padding = 32f };

        public static readonly ModalSpec ModalOutOfMoves =
            new ModalSpec { WidthFraction = 0.85f, MaxHeightFraction = 0.65f, CornerRadius = 24f, Padding = 32f };

        public static readonly ModalSpec ModalStarterPack =
            new ModalSpec { WidthFraction = 0.90f, MaxHeightFraction = 0.80f, CornerRadius = 28f, Padding = 24f };

        public static readonly ModalSpec ModalSmallDialog =
            new ModalSpec { WidthFraction = 0.75f, MaxHeightFraction = 0.40f, CornerRadius = 20f, Padding = 24f };

        public static readonly ModalSpec ModalToast =
            new ModalSpec { WidthFraction = 0.80f, MaxHeightFraction = 0f /*auto*/, CornerRadius = 16f, Padding = 16f };

        public static readonly ModalSpec ModalFullScreen =
            new ModalSpec { WidthFraction = 1.00f, MaxHeightFraction = 1.00f, CornerRadius = 0f, Padding = 40f };

        // ═══════════════════════════════════════════════════════════════════════
        // BUTTON SIZING (logical pixels at reference resolution)
        // ═══════════════════════════════════════════════════════════════════════

        public struct ButtonSpec
        {
            public float Height;
            public float MinWidth;
            public float CornerRadius;
            public float PaddingHorizontal;
        }

        public static readonly ButtonSpec ButtonPrimary =
            new ButtonSpec { Height = 64f, MinWidth = 160f, CornerRadius = 32f, PaddingHorizontal = 24f };

        public static readonly ButtonSpec ButtonSecondary =
            new ButtonSpec { Height = 56f, MinWidth = 120f, CornerRadius = 28f, PaddingHorizontal = 20f };

        public static readonly ButtonSpec ButtonTertiary =
            new ButtonSpec { Height = 48f, MinWidth = 80f, CornerRadius = 24f, PaddingHorizontal = 16f };

        public static readonly ButtonSpec ButtonIcon =
            new ButtonSpec { Height = 56f, MinWidth = 56f, CornerRadius = 28f, PaddingHorizontal = 0f };

        public static readonly ButtonSpec ButtonSmall =
            new ButtonSpec { Height = 40f, MinWidth = 80f, CornerRadius = 20f, PaddingHorizontal = 16f };

        /// <summary>iOS HIG tap target minimum.</summary>
        public const float MinTapTargetSize = 44f;

        // ═══════════════════════════════════════════════════════════════════════
        // SAFE AREA
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Home-indicator clearance on iPhone (bottom).</summary>
        public const float HomeIndicatorGap = 20f;

        /// <summary>Returns the device safe area rect (respects notch / Dynamic Island).</summary>
        public static Rect SafeArea => Screen.safeArea;

        // ═══════════════════════════════════════════════════════════════════════
        // COLOR PALETTE
        // Sourced from project_wordrop_ui_style_guide.md (2026-04-20 teal-board commit).
        // Use these constants rather than inlining Color literals.
        // ═══════════════════════════════════════════════════════════════════════

        // ── Primary (teal board direction) ──────────────────────────────────────
        public static readonly Color TealBoard = Hex(0x1F, 0x5F, 0x6C);
        public static readonly Color TealDeep  = Hex(0x16, 0x3E, 0x46);
        public static readonly Color TealLight = Hex(0x3B, 0x88, 0x96);
        public static readonly Color TealPale  = Hex(0x7D, 0xB5, 0xBE);

        // ── Tile variants ───────────────────────────────────────────────────────
        public static readonly Color TileNormal = Hex(0xF5, 0xE8, 0xC7);
        public static readonly Color TilePrimed = Hex(0xE6, 0x4B, 0xAF);
        public static readonly Color TileGold   = Hex(0xF5, 0xC5, 0x42);
        public static readonly Color TileStone  = Hex(0x6B, 0x6E, 0x73);
        public static readonly Color TileCyan   = Hex(0x3E, 0xCB, 0xCF);

        // ── UI accents ──────────────────────────────────────────────────────────
        public static readonly Color Gold           = Hex(0xF5, 0xC5, 0x42);
        public static readonly Color SuccessGreen   = Hex(0x3F, 0xC1, 0x6C);
        public static readonly Color DangerRed      = Hex(0xE3, 0x53, 0x3C);
        public static readonly Color WarningOrange  = Hex(0xF5, 0x9A, 0x42);

        // ── Text ────────────────────────────────────────────────────────────────
        public static readonly Color TextPrimary   = Color.white;
        public static readonly Color TextSecondary = Hex(0xB8, 0xC5, 0xC9);
        public static readonly Color TextDisabled  = Hex(0x6B, 0x7A, 0x7E);

        // ── Overlays ────────────────────────────────────────────────────────────
        public static readonly Color ModalBackdrop = new Color(0f, 0f, 0f, 0.80f);

        // ═══════════════════════════════════════════════════════════════════════
        // TYPOGRAPHY — point sizes at reference resolution
        // ═══════════════════════════════════════════════════════════════════════

        public const int FontHero    = 72;  // Level Complete banner, score celebration
        public const int FontDisplay = 48;  // Modal titles, main menu buttons
        public const int FontTitle   = 32;  // Section headers, level names
        public const int FontHeading = 24;  // Sub-headers, HUD stats
        public const int FontBodyLg  = 18;  // Body text in modals
        public const int FontBody    = 16;  // Standard UI, tutorial prompts
        public const int FontCaption = 14;  // Timestamps, subtitles, hints
        public const int FontMicro   = 12;  // Legal, metadata, tile numbers

        /// <summary>Outline width token (on text over busy backgrounds). 2–3px.</summary>
        public const float OutlineWidthPx = 2.5f;
        public static readonly Color OutlineColor = new Color(0f, 0f, 0f, 0.60f);

        // ═══════════════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Compact hex-byte → Color helper. Keeps the palette section readable.</summary>
        private static Color Hex(byte r, byte g, byte b, byte a = 255)
            => new Color(r / 255f, g / 255f, b / 255f, a / 255f);
    }
}
