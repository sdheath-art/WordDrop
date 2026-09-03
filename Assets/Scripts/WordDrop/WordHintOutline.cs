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
        public static float LineWidth     = 0.035f; // outline thickness
        public static float DashesPerCell = 2.2f;   // dash density along the line
        public static float MarchSpeed    = 0.55f;  // texture scroll (UV units/sec)
        public static float Brightness    = 1.15f;  // HDR white → soft bloom glow
        public static Color Tint          = new Color(1.0f, 0.92f, 0.62f, 1f); // warm pale-gold (line + sparkles)
        public static int   SortOrder     = 40;     // safely above tiles + their anims

        // ── Sparkle layer — Royal-Match "line of twinkles" scattered along the outline. 2026-07-08 Spencer.
        public static int   SparkleCount  = 12;     // twinkles alive along the line at once
        public static float SparkleSize   = 0.11f;  // ×CellSize (peak)
        public static float SparkleLife   = 0.75f;  // seconds per twinkle pop→fade cycle

        private LineRenderer _lr;
        private Material     _mat;
        private bool         _active;

        private List<Vector3>    _pts;              // current outline perimeter (for sparkle placement)
        private Transform[]      _sparkles;
        private SpriteRenderer[] _sparkleSR;
        private float[]          _sparklePhase;
        private static Sprite    _sparkleSprite;

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

            // 2026-09-03: tiles OVERLAP vertically — CellToWorld returns the TILE centre,
            // which is biased UP by half the tile's overshoot so the bottom row sits flush
            // with the board floor. The visible FACE sits that much lower, so drop the
            // outline to hug the faces instead of floating above them.
            float faceDrop = ((1f / GridManager.TILE_ASPECT) - 1f) * 0.5f * cs;
            minY -= faceDrop; maxY -= faceDrop;

            float r = cs * CornerRadius;
            List<Vector3> pts = BuildRoundedRect(minX, minY, maxX, maxY, r, Mathf.Max(1, CornerSegs));
            _lr.positionCount = pts.Count;
            _lr.SetPositions(pts.ToArray());
            _pts = pts;               // stash for the sparkle layer
            EnsureSparkles();
            // Re-seed sparkles onto the NEW perimeter (staggered, starting invisible) so they don't linger at
            // the previous word's outline and snap across. 2026-07-08 Spencer.
            if (_sparkles != null)
                for (int i = 0; i < _sparkles.Length; i++)
                {
                    _sparklePhase[i]      = i / (float)_sparkles.Length;
                    _sparkles[i].position = RandomPerimeterPoint();
                    if (_sparkleSR[i] != null) _sparkleSR[i].color = Color.clear;
                }

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
            if (_sparkleSR != null)
                for (int i = 0; i < _sparkleSR.Length; i++)
                    if (_sparkleSR[i] != null) _sparkleSR[i].color = Color.clear;
        }

        private void Update()
        {
            if (!_active || _mat == null || _lr == null) return;

            // March the dashes.
            Vector2 off = _mat.mainTextureOffset;
            off.x += MarchSpeed * Time.deltaTime; // clockwise march (2026-07-08 Spencer)
            if (off.x > 1f) off.x -= 1f;
            _mat.mainTextureOffset = off;

            // Gentle brightness pulse.
            float pulse = Brightness * (0.85f + 0.15f * Mathf.Sin(Time.time * 4.2f));
            Color c = new Color(Tint.r * pulse, Tint.g * pulse, Tint.b * pulse, 1f);
            _lr.startColor = c;
            _lr.endColor   = c;

            UpdateSparkles(Time.deltaTime);
        }

        // ── Sparkle layer ──────────────────────────────────────────────────
        private void EnsureSparkles()
        {
            if (_sparkles != null) return;
            _sparkles     = new Transform[SparkleCount];
            _sparkleSR    = new SpriteRenderer[SparkleCount];
            _sparklePhase = new float[SparkleCount];
            Shader sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            Sprite spr = GetSparkleSprite();
            for (int i = 0; i < SparkleCount; i++)
            {
                var go = new GameObject("HintSparkle" + i);
                go.transform.SetParent(transform, false);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite      = spr;
                sr.material     = new Material(sh);
                sr.sortingOrder = SortOrder + 1; // just above the line
                sr.color        = Color.clear;
                _sparkles[i]     = go.transform;
                _sparkleSR[i]    = sr;
                _sparklePhase[i] = i / (float)SparkleCount; // stagger so they don't all twinkle in unison
            }
        }

        private void UpdateSparkles(float dt)
        {
            if (_sparkles == null || _pts == null || _pts.Count < 2) return;
            float cs = GridManager.Instance != null ? GridManager.Instance.CellSize : 1f;
            for (int i = 0; i < _sparkles.Length; i++)
            {
                _sparklePhase[i] += dt / Mathf.Max(0.05f, SparkleLife);
                if (_sparklePhase[i] >= 1f)
                {
                    _sparklePhase[i] -= 1f;
                    _sparkles[i].position = RandomPerimeterPoint(); // respawn at a new spot on the line
                }
                float p  = _sparklePhase[i];
                float tw = Mathf.Sin(p * Mathf.PI);                 // 0 → 1 → 0 pop-and-fade
                float sc = cs * SparkleSize * (0.35f + 0.65f * tw);
                _sparkles[i].localScale    = new Vector3(sc, sc, 1f);
                _sparkles[i].localRotation = Quaternion.Euler(0f, 0f, p * 120f);
                float b = Mathf.Clamp01(tw) * Brightness;
                _sparkleSR[i].color = new Color(Tint.r * b, Tint.g * b, Tint.b * b, Mathf.Clamp01(tw));
            }
        }

        private Vector3 RandomPerimeterPoint()
        {
            // Sample by ARC LENGTH so sparkles spread evenly along the whole line — sampling by segment index
            // over-picked the corner arcs (many tiny segments) and starved the long top/bottom edges. 2026-07-08.
            float total = Perimeter(_pts);
            float target = UnityEngine.Random.value * total;
            float acc = 0f;
            for (int i = 0; i < _pts.Count; i++)
            {
                int j = (i + 1) % _pts.Count;
                float seg = Vector3.Distance(_pts[i], _pts[j]);
                if (acc + seg >= target)
                {
                    float t = seg > 0.0001f ? (target - acc) / seg : 0f;
                    return Vector3.Lerp(_pts[i], _pts[j], t);
                }
                acc += seg;
            }
            return _pts[0];
        }

        private static Sprite GetSparkleSprite()
        {
            if (_sparkleSprite != null) return _sparkleSprite;
            _sparkleSprite = Resources.Load<Sprite>("Particles/star_hc");
            if (_sparkleSprite == null)
            {
                var tex = Resources.Load<Texture2D>("Particles/star_hc");
                if (tex != null)
                    _sparkleSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _sparkleSprite;
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
