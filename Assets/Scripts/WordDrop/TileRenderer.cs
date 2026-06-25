using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Procedural sprite generator for tiles, cells, and UI elements.
    /// Creates chunky beveled rounded rectangles with tactile depth.
    /// </summary>
    public static class TileRenderer
    {
        // ── Bevel tuning ────────────────────────────────────────────────────────
        // Bevel is expressed as fraction of texture height
        private const float BEVEL_TOP_FRAC    = 0.12f;  // bright top strip
        private const float BEVEL_BOTTOM_FRAC = 0.10f;  // dark bottom strip
        private const float BEVEL_TOP_BOOST   = 0.08f;  // brightness added at top edge
        private const float BEVEL_BOTTOM_DIM  = 0.06f;  // brightness removed at bottom edge

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Rounded rectangle with border and subtle bevel (bright top, dark bottom).
        /// </summary>
        public static Sprite CreateRoundedRect(
            int width, int height,
            int cornerRadius,
            Color fillColor,
            Color borderColor,
            int borderThickness = 3)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[width * height];

            int bevelTopPx = Mathf.Max(2, Mathf.RoundToInt(height * BEVEL_TOP_FRAC));
            int bevelBotPx = Mathf.Max(2, Mathf.RoundToInt(height * BEVEL_BOTTOM_FRAC));

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Anti-aliased outer silhouette (supersampled coverage) so the rounded corners don't
                    // read as jaggy stair-steps — especially when the sprite is stretched. 2026-06-15.
                    float cov = CoverageRoundedRect(x, y, width, height, cornerRadius);
                    if (cov <= 0f)
                    {
                        pixels[y * width + x] = Color.clear;
                        continue;
                    }

                    bool insideInner = IsInsideRoundedRect(
                        x, y, width, height,
                        Mathf.Max(0, cornerRadius - borderThickness),
                        borderThickness);

                    Color col = insideInner ? fillColor : borderColor;
                    col.a *= cov; // soften the outer edge
                    pixels[y * width + x] = col;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            // 9-slice border = cornerRadius so callers that set Image.type = Sliced keep crisp, correctly
            // proportioned corners at ANY display size (only the middle stretches). Ignored in Simple
            // mode, so existing callers are unaffected. 2026-06-15.
            int b = Mathf.Clamp(cornerRadius, 0, Mathf.Min(width, height) / 2);
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f,
                0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        /// <summary>Solid rounded rect with bevel (no separate border).</summary>
        public static Sprite CreateSolidRoundedRect(int width, int height, int cornerRadius, Color fillColor)
        {
            return CreateRoundedRect(width, height, cornerRadius, fillColor, fillColor, 0);
        }

        /// <summary>
        /// Board frame: darker outer shell with inner highlight edge.
        /// Creates a layered look — dark frame → thin bright edge → inner fill.
        /// </summary>
        public static Sprite CreateBoardFrame(
            int width, int height, int cornerRadius,
            Color frameColor, Color edgeHighlight, Color innerColor,
            int frameThickness, int edgeThickness)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            Color[] pixels = new Color[width * height];

            int innerRadius1 = Mathf.Max(0, cornerRadius - frameThickness);
            int innerRadius2 = Mathf.Max(0, cornerRadius - frameThickness - edgeThickness);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    bool inOuter = IsInsideRoundedRect(x, y, width, height, cornerRadius);
                    if (!inOuter) { pixels[y * width + x] = Color.clear; continue; }

                    bool inEdge = IsInsideRoundedRect(x, y, width, height, innerRadius1, frameThickness);
                    if (!inEdge) { pixels[y * width + x] = frameColor; continue; }

                    bool inInner = IsInsideRoundedRect(x, y, width, height, innerRadius2, frameThickness + edgeThickness);
                    if (!inInner) { pixels[y * width + x] = edgeHighlight; continue; }

                    pixels[y * width + x] = innerColor;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Vertical gradient rectangle (bottom color to top color).</summary>
        public static Sprite CreateGradientRect(int width, int height, Color bottomColor, Color topColor)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;
            Color[] pixels = new Color[width * height];
            for (int y = 0; y < height; y++)
            {
                float t = (float)y / (height - 1);
                Color rowColor = Color.Lerp(bottomColor, topColor, t);
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = rowColor;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Simple solid colored rectangle (no rounding).</summary>
        public static Sprite CreateRect(int width, int height, Color color)
        {
            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            tex.SetPixels(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100f);
        }

        // ── Color helpers ───────────────────────────────────────────────────────

        private static Color Brighten(Color c, float amount)
        {
            return new Color(
                Mathf.Clamp01(c.r + amount),
                Mathf.Clamp01(c.g + amount),
                Mathf.Clamp01(c.b + amount),
                c.a);
        }

        private static Color Dim(Color c, float amount)
        {
            return new Color(
                Mathf.Clamp01(c.r - amount),
                Mathf.Clamp01(c.g - amount),
                Mathf.Clamp01(c.b - amount),
                c.a);
        }

        // ── Geometry helpers ────────────────────────────────────────────────────

        /// <summary>Fractional coverage (0..1) of pixel (px,py) by the rounded-rect silhouette,
        /// supersampled 4×4 — this is the anti-aliasing that kills the jaggy corners. 2026-06-15.</summary>
        private static float CoverageRoundedRect(int px, int py, int w, int h, int radius)
        {
            const int S = 4;
            int inside = 0;
            for (int sy = 0; sy < S; sy++)
                for (int sx = 0; sx < S; sx++)
                {
                    float fx = px + (sx + 0.5f) / S;
                    float fy = py + (sy + 0.5f) / S;
                    if (InsideRoundedRectF(fx, fy, w, h, radius)) inside++;
                }
            return inside / (float)(S * S);
        }

        /// <summary>Float-precision point-in-rounded-rect test (for supersampled coverage).</summary>
        private static bool InsideRoundedRectF(float x, float y, float w, float h, float radius)
        {
            if (x < 0f || x >= w || y < 0f || y >= h) return false;
            radius = Mathf.Min(radius, w * 0.5f, h * 0.5f);
            if (radius <= 0f) return true;

            float cx, cy;
            if      (x < radius      && y < radius)      { cx = radius;      cy = radius; }
            else if (x >= w - radius && y < radius)      { cx = w - radius;  cy = radius; }
            else if (x < radius      && y >= h - radius) { cx = radius;      cy = h - radius; }
            else if (x >= w - radius && y >= h - radius) { cx = w - radius;  cy = h - radius; }
            else return true; // not in a corner quadrant → solid

            float dx = x - cx, dy = y - cy;
            return dx * dx + dy * dy <= radius * radius;
        }

        private static bool IsInsideRoundedRect(int px, int py, int w, int h, int radius, int inset = 0)
        {
            int x = px - inset;
            int y = py - inset;
            int rw = w - inset * 2;
            int rh = h - inset * 2;

            if (rw <= 0 || rh <= 0) return false;
            radius = Mathf.Min(radius, rw / 2, rh / 2);
            if (x < 0 || x >= rw || y < 0 || y >= rh) return false;

            if (x < radius && y < radius)
                return CircleContains(x, y, radius, radius, radius);
            if (x >= rw - radius && y < radius)
                return CircleContains(x, y, rw - 1 - radius, radius, radius);
            if (x < radius && y >= rh - radius)
                return CircleContains(x, y, radius, rh - 1 - radius, radius);
            if (x >= rw - radius && y >= rh - radius)
                return CircleContains(x, y, rw - 1 - radius, rh - 1 - radius, radius);

            return true;
        }

        private static bool CircleContains(int px, int py, int cx, int cy, int r)
        {
            int dx = px - cx;
            int dy = py - cy;
            return dx * dx + dy * dy <= r * r;
        }
    }
}
