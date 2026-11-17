using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// ICON EDGE at runtime (2026-10-29, Spencer: goal icons should get the edge too - "check screenshot", a pale teal gem
    /// washing out on the cream goal panel). Same look as edge "B" (vector_buttons/edge/icon_edge.py, Spencer "sure looks
    /// great"): a thin SOLID dark core right against the icon + a short soft halo, in the icon's own colour run through our
    /// stroke recipe (hue -18 deg toward violet, sat x1.05, value x V). The goal pictures reuse the BOARD sprites (gem, crate,
    /// ice, duck, letter tile), so the edge can't be baked into those files - this makes an edged COPY once and caches it.
    /// One edge colour per icon (the average of its outer band) - fine at goal-icon size.
    /// The copy is padded by the edge, so the caller scales the Image by <c>mul</c> to keep the art the same size.
    /// </summary>
    public static class IconEdge
    {
        public static bool ON = true;
        public static float CORE = 0.008f, HALO = 0.02f, V = 0.25f, HALO_POW = 1.6f;

        private static readonly Dictionary<(Sprite, Color), (Sprite, float)> s_cache = new Dictionary<(Sprite, Color), (Sprite, float)>();

        public static Sprite Get(Sprite src, Color tint, out float mul)
        {
            mul = 1f;
            if (!ON || src == null || src.texture == null) return null;
            if (s_cache.TryGetValue((src, tint), out var hit)) { mul = hit.Item2; return hit.Item1; }
            Sprite made = null;
            try { made = Make(src, tint, out mul); }
            catch (System.Exception e) { WDLog.Log($"[IconEdge] {src.name}: {e.Message}"); made = null; mul = 1f; }
            s_cache[(src, tint)] = (made, mul);
            return made;
        }

        private static Sprite Make(Sprite src, Color tint, out float mul)
        {
            mul = 1f;
            // readable copy of the sprite's pixels (works for non-readable textures; sRGB round trip for the linear project)
            Rect r = src.textureRect;
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            var tex = src.texture;
            var rtx = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rtx);
            var prev = RenderTexture.active; RenderTexture.active = rtx;
            var read = new Texture2D(w, h, TextureFormat.RGBA32, false);
            read.ReadPixels(new Rect(r.x, r.y, w, h), 0, 0); read.Apply();
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rtx);
            var px = read.GetPixels32(); Object.Destroy(read);

            // content size (alpha bbox)
            int x0 = w, y0 = h, x1 = -1, y1 = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                if (px[y * w + x].a > 127) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            if (x1 < 0) return null;
            float content = Mathf.Max(x1 - x0 + 1, y1 - y0 + 1);
            float OW = Mathf.Max(1.5f, HALO * content), CW = Mathf.Max(1f, CORE * content);
            int P = Mathf.CeilToInt(OW) + 2;
            int W = w + 2 * P, H = h + 2 * P;

            // distance OUTSIDE the icon (chamfer 3-4, /3 = px)
            var inside = new bool[W * H];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) inside[(y + P) * W + x + P] = px[y * w + x].a > 127;
            var dOut = Chamfer(inside, W, H, false);
            var dIn = Chamfer(inside, W, H, true);

            // edge colour: average of the icon's outer band (inside, within ~3% of content), tinted, through the recipe
            float band = Mathf.Max(2f, content * 0.03f);
            double sr = 0, sg = 0, sb = 0, n = 0;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                int i = (y + P) * W + x + P; if (!inside[i] || dIn[i] > band) continue;
                var c = px[y * w + x]; sr += c.r / 255.0 * tint.r; sg += c.g / 255.0 * tint.g; sb += c.b / 255.0 * tint.b; n++;
            }
            if (n < 1) return null;
            Color.RGBToHSV(new Color((float)(sr / n), (float)(sg / n), (float)(sb / n)), out float hh, out float ss, out float vv);
            float hd = hh * 360f, toward = Mathf.Repeat(275f - hd + 180f, 360f) - 180f;
            hd += Mathf.Sign(toward) * Mathf.Min(18f, Mathf.Abs(toward));
            Color edge = Color.HSVToRGB(Mathf.Repeat(hd, 360f) / 360f, Mathf.Clamp01(ss * 1.05f), vv * V);

            var outPx = new Color32[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int i = y * W + x;
                Color icon = new Color(0, 0, 0, 0);
                int sx = x - P, sy = y - P;
                if (sx >= 0 && sy >= 0 && sx < w && sy < h)
                {
                    var c = px[sy * w + sx];
                    icon = new Color(c.r / 255f * tint.r, c.g / 255f * tint.g, c.b / 255f * tint.b, c.a / 255f * tint.a);
                }
                float d = inside[i] ? 0f : dOut[i];
                float ring = 0f;
                if (d > 0f)
                {
                    ring = Mathf.Pow(Mathf.Clamp01(1f - d / OW), HALO_POW);
                    ring = Mathf.Max(ring, Mathf.Clamp01(CW + 0.5f - d));
                }
                // icon OVER the edge (edge sits under the anti-aliased rim too)
                float a = icon.a + ring * (1f - icon.a);
                Color rgb = a > 0f ? (icon * icon.a + edge * ring * (1f - icon.a)) / a : Color.clear;
                outPx[i] = new Color32((byte)(Mathf.Clamp01(rgb.r) * 255), (byte)(Mathf.Clamp01(rgb.g) * 255), (byte)(Mathf.Clamp01(rgb.b) * 255), (byte)(Mathf.Clamp01(a) * 255));
            }
            var outTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            outTex.wrapMode = TextureWrapMode.Clamp; outTex.filterMode = FilterMode.Bilinear;
            outTex.SetPixels32(outPx); outTex.Apply(false, true);
            mul = (float)W / w;
            return Sprite.Create(outTex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), src.pixelsPerUnit);
        }

        private static readonly Dictionary<Sprite, Color> s_avg = new Dictionary<Sprite, Color>();
        /// <summary>Average colour of the middle 40% of a sprite (opaque pixels), cached. Used for the tile-letter lip tint.</summary>
        public static Color CentreColour(Sprite src)
        {
            if (src == null || src.texture == null) return Color.white;
            if (s_avg.TryGetValue(src, out var c)) return c;
            c = Color.white;
            try
            {
                Rect r = src.textureRect; int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
                var rtx = RenderTexture.GetTemporary(src.texture.width, src.texture.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(src.texture, rtx); var prev = RenderTexture.active; RenderTexture.active = rtx;
                int x0 = (int)(r.x + w * 0.3f), y0 = (int)(r.y + h * 0.3f), cw = Mathf.Max(1, (int)(w * 0.4f)), ch = Mathf.Max(1, (int)(h * 0.4f));
                var read = new Texture2D(cw, ch, TextureFormat.RGBA32, false); read.ReadPixels(new Rect(x0, y0, cw, ch), 0, 0); read.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rtx);
                var px = read.GetPixels32(); Object.Destroy(read);
                double rr = 0, gg = 0, bb = 0, n = 0;
                foreach (var p in px) { if (p.a < 200) continue; rr += p.r; gg += p.g; bb += p.b; n++; }
                if (n > 0) c = new Color((float)(rr / n / 255), (float)(gg / n / 255), (float)(bb / n / 255), 1f);
            }
            catch (System.Exception e) { WDLog.Log($"[IconEdge] CentreColour {src.name}: {e.Message}"); }
            s_avg[src] = c; return c;
        }

        /// <summary>Two-pass 3-4 chamfer distance (px). outside=false: distance from the icon for pixels outside it;
        /// inverse=true: distance from the outside for pixels inside it.</summary>
        private static float[] Chamfer(bool[] inside, int W, int H, bool inverse)
        {
            const float BIG = 1e6f; var d = new float[W * H];
            for (int i = 0; i < d.Length; i++) d[i] = (inside[i] != inverse) ? 0f : BIG;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                int i = y * W + x; float v = d[i]; if (v == 0f) continue;
                if (x > 0) v = Mathf.Min(v, d[i - 1] + 3);
                if (y > 0) { v = Mathf.Min(v, d[i - W] + 3); if (x > 0) v = Mathf.Min(v, d[i - W - 1] + 4); if (x < W - 1) v = Mathf.Min(v, d[i - W + 1] + 4); }
                d[i] = v;
            }
            for (int y = H - 1; y >= 0; y--) for (int x = W - 1; x >= 0; x--)
            {
                int i = y * W + x; float v = d[i]; if (v == 0f) continue;
                if (x < W - 1) v = Mathf.Min(v, d[i + 1] + 3);
                if (y < H - 1) { v = Mathf.Min(v, d[i + W] + 3); if (x < W - 1) v = Mathf.Min(v, d[i + W + 1] + 4); if (x > 0) v = Mathf.Min(v, d[i + W - 1] + 4); }
                d[i] = v;
            }
            for (int i = 0; i < d.Length; i++) d[i] /= 3f;
            return d;
        }
    }
}
