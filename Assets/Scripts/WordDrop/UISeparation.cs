using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// HOME SEPARATION (2026-10-31, Spencer on the library-hall home: "should there be more separation of the HUD and button
    /// from the background?"). Mocked in WordDrop_Art/composition/library_home/separation_compare.png:
    ///   1. SCRIM: the painting darkens toward the top (behind the HUD bar) and slightly at the bottom and edges, in the
    ///      plate hue #1A0B2E, so the busy library recedes and the UI sits on a quieter field (Royal Match does the same).
    ///   2. SOFT SHADOW: a blurred rounded-rect shadow straight down under the HUD bar and the Level button, so they lift off
    ///      the painting instead of lying flat on it.
    /// Both are generated (no art); the shadow follows its target's world rect every frame, so it tracks HudBar.Place and
    /// button punches without either knowing about it.
    /// </summary>
    public static class UISeparation
    {
        public static bool  SCRIM_ON = true, SHADOWS_ON = true;
        public static Color PLATE = new Color32(0x1A, 0x0B, 0x2E, 0xFF);
        /// <summary>11-02 Spencer: "dark tint should match the mock up, that goes for all dark tint in the game ... as dark as you have
        /// it in the mock up". ONE dim for every full-screen modal / spotlight. The mocks composite #1A0B2E @ ~0.78 in sRGB; the
        /// project is LINEAR, where the same alpha reads much lighter (measured 10-31: .80 gave walls 109 vs mock 87), so the
        /// in-game alpha is the linear equivalent ~0.91 (solved for mid and light backgrounds).</summary>
        public static Color MODAL_DIM = new Color32(0x1A, 0x0B, 0x2E, 232);
        public static float TOP_ALPHA = 0.55f, TOP_EXTENT = 0.17f;       // fraction of screen height the top scrim fades over
        public static float BOT_ALPHA = 0.30f, BOT_START  = 0.86f;
        public static float VIG_ALPHA = 0.38f;

        private static Sprite s_scrim;

        /// <summary>Full-screen scrim placed directly above <paramref name="background"/> (same parent).</summary>
        public static Image AddScrim(Image background)
        {
            if (!SCRIM_ON || background == null) return null;
            var parent = background.transform.parent;
            var go = new GameObject("HomeScrim", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.transform.SetSiblingIndex(background.transform.GetSiblingIndex() + 1);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>(); img.sprite = Scrim(); img.color = Color.white; img.raycastTarget = false;
            return img;
        }

        /// <summary>A soft shadow just behind <paramref name="target"/> (same parent, previous sibling). <paramref name="inset"/>
        /// trims transparent margins off the target rect as fractions (left, bottom, right, top); <paramref name="blur"/> and
        /// <paramref name="drop"/> are fractions of the trimmed height; <paramref name="corner"/> is the target's corner radius
        /// as a fraction of its trimmed height (0.5 = a full pill), so the shadow has the target's own shape.</summary>
        public static Image AddShadow(RectTransform target, Vector4 inset, float blur = 0.22f, float drop = 0.10f, float alpha = 0.55f, float corner = 0.3f)
        {
            if (!SHADOWS_ON || target == null || target.parent == null) return null;
            var go = new GameObject(target.name + "_Shadow", typeof(RectTransform), typeof(Image), typeof(Follower));
            go.transform.SetParent(target.parent, false);
            go.transform.SetSiblingIndex(target.GetSiblingIndex());          // pushes the target one up: shadow draws first
            var img = go.GetComponent<Image>(); img.type = Image.Type.Simple; img.raycastTarget = false;
            img.color = new Color(PLATE.r, PLATE.g, PLATE.b, alpha);
            var f = go.GetComponent<Follower>(); f.target = target; f.inset = inset; f.blur = blur; f.drop = drop; f.corner = corner;
            f.LateUpdate();
            return img;
        }

        private class Follower : MonoBehaviour
        {
            public RectTransform target; public Vector4 inset; public float blur, drop, corner;
            private readonly Vector3[] _c = new Vector3[4];
            private CanvasGroup _group; private float _baseA = -1f, _aspect = -1f;
            public void LateUpdate()
            {
                if (target == null) { Destroy(gameObject); return; }
                // never SetActive(false) on ourselves: LateUpdate would stop and we'd never come back. Toggle the Image instead,
                // and borrow the target's CanvasGroup alpha so fades take the shadow with them.
                var img = GetComponent<Image>();
                img.enabled = target.gameObject.activeInHierarchy;
                if (_group == null) _group = target.GetComponent<CanvasGroup>();
                if (_baseA < 0f) _baseA = img.color.a;
                var col = img.color; col.a = _baseA * (_group != null ? _group.alpha : 1f); img.color = col;
                var parent = (RectTransform)transform.parent; var rt = (RectTransform)transform;
                target.GetWorldCorners(_c);                                   // 0 bottom-left, 2 top-right
                Vector2 a = parent.InverseTransformPoint(_c[0]), b = parent.InverseTransformPoint(_c[2]);
                float w = b.x - a.x, h = b.y - a.y;
                a += new Vector2(w * inset.x, h * inset.y); b -= new Vector2(w * inset.z, h * inset.w);
                float tw = b.x - a.x, th = b.y - a.y, pad = th * blur;
                if (tw < 1f || th < 1f) return;
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = (a + b) * 0.5f - parent.rect.center + new Vector2(0f, -th * drop);
                rt.sizeDelta = new Vector2(tw + pad * 2f, th + pad * 2f);
                // the texture is built for this shape; rebuild only if the aspect really changes (punches scale both axes)
                float asp = tw / th;
                if (img.sprite == null || Mathf.Abs(asp - _aspect) > 0.05f * _aspect) { _aspect = asp; img.sprite = ShapeShadow(asp, blur, corner); }
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_shadows = new System.Collections.Generic.Dictionary<string, Sprite>();

        /// <summary>A rounded rect of aspect <paramref name="aspect"/> (w/h) and corner radius <paramref name="corner"/>*h, with a
        /// Gaussian falloff <paramref name="blur"/>*h wide around it, baked at the shadow's full size so nothing gets sliced.</summary>
        private static Sprite ShapeShadow(float aspect, float blur, float corner)
        {
            string key = $"{aspect:F2}|{blur:F2}|{corner:F2}";
            if (s_shadows.TryGetValue(key, out var cached) && cached != null) return cached;
            const float HPX = 96f;                                            // core height in texels
            float pad = HPX * blur, cw = HPX * aspect;
            int W = Mathf.Clamp(Mathf.CeilToInt(cw + pad * 2f), 8, 1024), H = Mathf.CeilToInt(HPX + pad * 2f);
            float hx = cw * 0.5f, hy = HPX * 0.5f, r = Mathf.Min(corner * HPX, hy), sigma = Mathf.Max(1f, pad / 2.4f);
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                // signed distance to the rounded rect centred in the texture
                float qx = Mathf.Abs(x + 0.5f - W * 0.5f) - (hx - r), qy = Mathf.Abs(y + 0.5f - H * 0.5f) - (hy - r);
                float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
                float d = outside + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                // inside: fade in over half a sigma so the core edge isn't hard; outside: Gaussian
                float al = d <= 0f ? 1f : Mathf.Exp(-(d * d) / (2f * sigma * sigma));
                px[y * W + x] = new Color(1, 1, 1, al);
            }
            t.SetPixels(px); t.Apply();
            var sp = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
            s_shadows[key] = sp; return sp;
        }

        /// <summary>Top scrim + bottom scrim + edge vignette, matching the mock (mock_library_home.py, sep=True).</summary>
        private static Sprite Scrim() => s_scrim != null ? s_scrim : (s_scrim = ScrimSprite(1f, TOP_ALPHA, VIG_ALPHA, BOT_ALPHA));

        // GAMEPLAY (2026-10-31, reading-room background): the chandelier sits right behind the Goals/Moves bar, so the same
        // top darkening goes on the world-space ScreenBackground (SceneBootstrap). Vignette gentler: the board covers the middle.
        public static bool  GAME_SCRIM_ON = true;
        public static float GAME_TOP_ALPHA = 0.55f, GAME_VIG_ALPHA = 0.22f, GAME_BOT_ALPHA = 0f;

        /// <summary>Scrim texture laid out for a quad <paramref name="overscan"/>x the screen: the fades are placed in SCREEN
        /// terms and the overscan margin keeps the edge value, so camera shake never shows an undarkened strip.</summary>
        public static Sprite ScrimSprite(float overscan, float topA, float vigA, float botA)
        {
            const int W = 96, H = 208;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[W * H];
            float m = (1f - 1f / overscan) * 0.5f;                          // margin on each side, in texture units
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float u = Mathf.Clamp01(((x + 0.5f) / W - m) * overscan);
                float v = Mathf.Clamp01(((1f - (y + 0.5f) / H) - m) * overscan);   // v = 0 at the TOP of the screen
                float d = Mathf.Sqrt(Sq((u - 0.5f) / 0.62f) + Sq((v - 0.55f) / 0.62f));
                float vig = Mathf.Pow(Mathf.Clamp01((d - 0.55f) / 0.6f), 1.4f) * vigA;
                float top = Mathf.Pow(Mathf.Clamp01(1f - v / TOP_EXTENT), 1.6f) * topA;
                float bot = Mathf.Pow(Mathf.Clamp01((v - BOT_START) / (1f - BOT_START)), 1.6f) * botA;
                px[y * W + x] = new Color(PLATE.r, PLATE.g, PLATE.b, Mathf.Min(0.75f, vig + top + bot));
            }
            t.SetPixels(px); t.Apply();
            return Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }
        private static float Sq(float f) => f * f;
    }
}
