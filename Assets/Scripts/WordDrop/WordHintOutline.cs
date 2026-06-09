using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Royal-Match / Candy-Crush-style "marching ants" hint outline — a dashed,
    /// animated rounded-rectangle traced around the suggested word's cells.
    /// Driven by HintManager: Show(cells) when a hint appears, Hide() on clear.
    /// 2026-06-03 Spencer.
    /// </summary>
    public class WordHintOutline : MonoBehaviour
    {
        // ── Tunables (×CellSize unless noted) ──────────────────────────────
        public static float Pad           = 0.0f;  // 0 = lines land on the cell boundary (centred in the inter-tile gap); negative = tighter
        public static float CornerRadius  = 0.22f; // rounded-corner radius
        public static int   CornerSegs    = 5;      // arc subdivisions per corner
        public static float LineWidth     = 0.055f; // outline thickness
        public static float DashesPerCell = 2.2f;   // dash density along the line
        public static float MarchSpeed    = 0.55f;  // texture scroll (UV units/sec)
        public static float Brightness    = 1.15f;  // HDR white → soft bloom glow
        public static int   SortOrder     = 40;     // safely above tiles + their anims

        private LineRenderer _lr;
        private Material     _mat;
        private bool         _active;

        private void Awake()
        {
            _lr = gameObject.AddComponent<LineRenderer>();
            _lr.loop              = true;
            _lr.useWorldSpace     = true;
            _lr.numCapVertices    = 2;
            _lr.numCornerVertices = 2;
            _lr.alignment         = LineAlignment.View;
            _lr.textureMode       = LineTextureMode.Tile; // tile the dash texture along the line
            _lr.sortingOrder      = SortOrder;
            _lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _lr.receiveShadows    = false;

            Shader sh = Shader.Find("WordDrop/HintDashLine")
                     ?? Shader.Find("WordDrop/AdditiveSprite")
                     ?? Shader.Find("Sprites/Default");
            _mat = new Material(sh) { mainTexture = BuildDashTexture() };
            _lr.material = _mat;
            _lr.enabled = false;
        }

        public void Show(List<Vector2Int> cells)
        {
            var grid = GridManager.Instance;
            if (_lr == null || grid == null || cells == null || cells.Count == 0) { Hide(); return; }
            float cs = grid.CellSize;

            // World-space bounds of all word cells (centre ± half cell), padded out.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var c in cells)
            {
                Vector3 p = grid.CellToWorld(c.x, c.y);
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            float outset = cs * (0.5f + Pad);
            minX -= outset; maxX += outset;
            minY -= outset; maxY += outset;

            float r = cs * CornerRadius;
            List<Vector3> pts = BuildRoundedRect(minX, minY, maxX, maxY, r, Mathf.Max(1, CornerSegs));
            _lr.positionCount = pts.Count;
            _lr.SetPositions(pts.ToArray());

            float w = cs * LineWidth;
            _lr.startWidth = w;
            _lr.endWidth   = w;

            // Tile mode repeats the texture once per world unit; scale it so we get
            // ~DashesPerCell dashes per cell length around the whole perimeter.
            float perimeter = Perimeter(pts);
            float repeats = Mathf.Max(1f, (perimeter / cs) * DashesPerCell);
            // mainTextureScale.x is in repeats-per-world-unit-of-line for Tile mode;
            // divide by perimeter so `repeats` ends up being total repeats.
            _mat.mainTextureScale = new Vector2(repeats / Mathf.Max(perimeter, 0.001f), 1f);

            _lr.enabled = true;
            _active = true;
        }

        public void Hide()
        {
            _active = false;
            if (_lr != null) _lr.enabled = false;
        }

        private void Update()
        {
            if (!_active || _mat == null || _lr == null) return;

            // March the dashes.
            Vector2 off = _mat.mainTextureOffset;
            off.x -= MarchSpeed * Time.deltaTime;
            if (off.x < -1f) off.x += 1f;
            _mat.mainTextureOffset = off;

            // Gentle brightness pulse.
            float pulse = Brightness * (0.85f + 0.15f * Mathf.Sin(Time.time * 4.2f));
            Color c = new Color(pulse, pulse, pulse, 1f);
            _lr.startColor = c;
            _lr.endColor   = c;
        }

        // Counter-clockwise rounded rectangle as a point loop (LineRenderer loop=true).
        private static List<Vector3> BuildRoundedRect(float minX, float minY, float maxX, float maxY, float r, int segs)
        {
            r = Mathf.Min(r, (maxX - minX) * 0.5f, (maxY - minY) * 0.5f);
            var pts = new List<Vector3>((segs + 1) * 4);
            AddArc(pts, maxX - r, minY + r, r, -90f,   0f, segs); // bottom-right
            AddArc(pts, maxX - r, maxY - r, r,   0f,  90f, segs); // top-right
            AddArc(pts, minX + r, maxY - r, r,  90f, 180f, segs); // top-left
            AddArc(pts, minX + r, minY + r, r, 180f, 270f, segs); // bottom-left
            return pts;
        }

        private static void AddArc(List<Vector3> pts, float cx, float cy, float r, float a0, float a1, int segs)
        {
            for (int i = 0; i <= segs; i++)
            {
                float a = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, i / (float)segs);
                pts.Add(new Vector3(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r, 0f));
            }
        }

        private static float Perimeter(List<Vector3> pts)
        {
            float total = 0f;
            for (int i = 0; i < pts.Count; i++)
                total += Vector3.Distance(pts[i], pts[(i + 1) % pts.Count]);
            return total;
        }

        // Horizontal dash texture: ~60% opaque white dash, 40% transparent gap,
        // with soft edges so the dashes don't alias. Tiled + scrolled by the line.
        private static Texture2D BuildDashTexture()
        {
            const int W = 32, H = 4;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            };
            var px = new Color32[W * H];
            for (int x = 0; x < W; x++)
            {
                float u = x / (float)(W - 1);   // 0..1 across the texture
                // Dash occupies the first ~58%; gap the rest. Two-pixel soft edge
                // (relies on bilinear filtering of the tiled texture to smooth).
                float dashEnd = 0.58f, edge = 0.04f;
                float rise = Mathf.Clamp01(u / edge);                 // 0→1 over the leading edge
                float fall = Mathf.Clamp01((dashEnd - u) / edge);     // 1→0 over the trailing edge
                float a = Mathf.Clamp01(Mathf.Min(rise, fall));
                byte alpha = (byte)(a * 255f);
                for (int y = 0; y < H; y++) px[y * W + x] = new Color32(255, 255, 255, alpha);
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
