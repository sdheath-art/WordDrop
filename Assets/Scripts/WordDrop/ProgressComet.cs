using System.Collections;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// PROGRESS COMET (2026-10-30, Spencer: "can we add that glow particle type of effect ... its the area button that glows like
    /// that"). Measured off his Royal Match L1-20 recording at 12 fps (t=330.6-333s, Area 1 3/6 -> 4/6):
    ///   1. a glowing GOLD comet with a glittering trail flies from the thing that progressed down into the button (~0.4s);
    ///   2. on impact the progress bar ticks up with white sparkles running along it;
    ///   3. a burst of gold glitter scatters around the whole button and fades (~0.5s).
    /// Built for the word-book widget (Milestone 2); TEST_ON_LEVEL_BUTTON plays it into the home Level button after each win so
    /// the feel can be judged now. Sparkle sprites are generated (no FX art in the project); additive via WordDrop/UIAdditive.
    /// </summary>
    public static class ProgressComet
    {
        public static bool ON = true, TEST_ON_LEVEL_BUTTON = true;
        public static float FLY = 0.40f, BURST = 0.55f, TRAIL_PER_SEC = 110f, TRAIL_LIFE = 0.38f, ARC = 0.35f;
        public static int   BURST_COUNT = 26, BAR_SPARKS = 10;
        public static Color GOLD = new Color(1f, 0.82f, 0.30f, 1f), PALE = new Color(1f, 0.97f, 0.82f, 1f);

        private static Runner s_runner; private static Sprite s_glow, s_star; private static Material s_add;
        private class Runner : MonoBehaviour { }

        /// <summary>Fly a comet from <paramref name="fromWorld"/> (overlay-canvas world = screen position) into <paramref name="target"/>.
        /// <paramref name="bar"/> (optional) gets sparkles along it on impact; <paramref name="onImpact"/> runs on the hit (tick the bar there).</summary>
        public static void Play(Vector3 fromWorld, RectTransform target, RectTransform bar = null, System.Action onImpact = null)
        {
            if (!ON || target == null || !target.gameObject.activeInHierarchy) { onImpact?.Invoke(); return; }
            var canvas = target.GetComponentInParent<Canvas>(); if (canvas == null) { onImpact?.Invoke(); return; }
            if (s_runner == null) { var go = new GameObject("ProgressComet"); Object.DontDestroyOnLoad(go); s_runner = go.AddComponent<Runner>(); }
            s_runner.StartCoroutine(Run(canvas.rootCanvas, fromWorld, target, bar, onImpact));
        }

        private static IEnumerator Run(Canvas root, Vector3 from, RectTransform target, RectTransform bar, System.Action onImpact)
        {
            // a full-screen layer on top of everything in that canvas
            var layerGO = new GameObject("CometLayer", typeof(RectTransform)); layerGO.transform.SetParent(root.transform, false);
            var layer = (RectTransform)layerGO.transform; layer.anchorMin = Vector2.zero; layer.anchorMax = Vector2.one; layer.offsetMin = layer.offsetMax = Vector2.zero;
            layer.SetAsLastSibling();
            float unit = target.rect.height;                       // sizes follow the button (canvas units)
            float scale = root.transform.lossyScale.x;              // canvas units -> world (screen) pixels

            var glow = NewImg(layer, Glow(), GOLD, unit * 1.10f);
            var core = NewImg(layer, Glow(), PALE, unit * 0.45f);
            Vector3 to = target.position;
            Vector3 side = Vector3.Cross((to - from).normalized, Vector3.forward);
            Vector3 ctrl = (from + to) * 0.5f + side * (to - from).magnitude * ARC + Vector3.up * unit * scale * 0.6f;
            GameAudio.Instance?.PlayLevelLoadShimmer();

            float t = 0f, spawnAcc = 0f;
            while (t < FLY)
            {
                if (layer == null) yield break;
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / FLY); float e = k * k * (3f - 2f * k);
                Vector3 p = (1 - e) * (1 - e) * from + 2 * (1 - e) * e * ctrl + e * e * to;
                glow.rectTransform.position = core.rectTransform.position = p;
                float pulse = 1f + Mathf.Sin(t * 40f) * 0.08f;
                glow.rectTransform.localScale = core.rectTransform.localScale = Vector3.one * pulse;
                spawnAcc += TRAIL_PER_SEC * Time.unscaledDeltaTime;
                while (spawnAcc >= 1f)
                {
                    spawnAcc -= 1f;
                    Vector3 j = (Vector3)(Random.insideUnitCircle * unit * scale * 0.18f);
                    Spark(layer, p + j, unit * Random.Range(0.14f, 0.30f), Random.value < 0.35f ? PALE : GOLD,
                          (Vector3)(Random.insideUnitCircle * unit * scale * 0.25f) + Vector3.down * unit * scale * 0.15f, TRAIL_LIFE * Random.Range(0.7f, 1.1f));
                }
                yield return null;
            }
            if (glow != null) Object.Destroy(glow.gameObject);
            if (core != null) Object.Destroy(core.gameObject);

            // IMPACT: the button punches + a light sweep, a soft flash, glitter all round it, sparkles along the bar
            onImpact?.Invoke();
            GameAudio.Instance?.PlayScorePowerup();
            HapticsManager.Light();
            if (target != null)
            {
                target.DOKill(true); target.DOPunchScale(Vector3.one * 0.12f, 0.35f, 6, 0.6f).SetUpdate(true);
                var img = target.GetComponent<Image>(); if (img != null) ShineSweep.Play(img); else ShineSweep.PlayRect(target);
            }
            var flash = NewImg(layer, Glow(), PALE, unit * 1.2f); flash.rectTransform.position = to;
            flash.rectTransform.DOScale(2.2f, BURST * 0.6f).SetEase(Ease.OutQuad).SetUpdate(true);
            flash.DOFade(0f, BURST * 0.6f).SetEase(Ease.OutQuad).SetUpdate(true);
            Vector2 half = target != null ? target.rect.size * 0.5f * scale : Vector2.one * unit * scale;
            for (int i = 0; i < BURST_COUNT; i++)
            {
                Vector2 edge = new Vector2(Random.Range(-1f, 1f) * half.x, Random.Range(-1f, 1f) * half.y);
                Vector3 p = to + (Vector3)edge;
                Vector3 vel = ((Vector3)edge.normalized * Random.Range(0.6f, 1.4f) + Vector3.up * 0.3f) * unit * scale;
                Spark(layer, p, unit * Random.Range(0.12f, 0.32f), Random.value < 0.4f ? PALE : GOLD, vel, BURST * Random.Range(0.7f, 1.15f));
            }
            if (bar != null)
            {
                float w = bar.rect.width * scale;
                for (int i = 0; i < BAR_SPARKS; i++)
                {
                    Vector3 p = bar.position + Vector3.right * (Random.Range(-0.5f, 0.5f) * w);
                    Spark(layer, p, unit * Random.Range(0.10f, 0.20f), PALE, Vector3.up * unit * scale * Random.Range(0.1f, 0.4f), BURST * Random.Range(0.5f, 0.9f), i * 0.025f);
                }
            }
            yield return new WaitForSecondsRealtime(BURST * 1.3f);
            if (layerGO != null) Object.Destroy(layerGO);
        }

        // ── shared FX pieces (10-31: the home coin pop's glow + emitted sparkles, Spencer's RM 50-100 ref ~22:20) ──
        public static Sprite GlowSprite => Glow();
        public static Sprite StarSprite => Star();
        /// <summary>An additive image (glow / sparkle sprite) under <paramref name="parent"/>, sized in canvas units.</summary>
        public static Image AddFx(RectTransform parent, Sprite s, Color c, float size) => NewImg(parent, s, c, size);
        /// <summary>One sparkle at world <paramref name="pos"/> drifting with <paramref name="vel"/> (world px/s) for <paramref name="life"/> s.</summary>
        public static void Sparkle(RectTransform layer, Vector3 pos, float size, Color c, Vector3 vel, float life, float delay = 0f) => Spark(layer, pos, size, c, vel, life, delay);

        /// <summary>One four-point sparkle: pops in, drifts with <paramref name="vel"/> (world px/s), spins, shrinks and fades.</summary>
        private static void Spark(RectTransform layer, Vector3 pos, float size, Color c, Vector3 vel, float life, float delay = 0f)
        {
            var im = NewImg(layer, Star(), c, size); var rt = im.rectTransform;
            rt.position = pos; rt.localScale = Vector3.zero; rt.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 90f));
            DOTween.Sequence().SetUpdate(true).SetDelay(delay).SetLink(im.gameObject)   // dies with its layer (11-01: splash closed mid-sparkle -> DOTween null warnings)
                .Append(rt.DOScale(1f, life * 0.25f).SetEase(Ease.OutBack))
                .Join(rt.DOMove(pos + vel * life, life).SetEase(Ease.OutQuad))
                .Join(rt.DOLocalRotate(new Vector3(0, 0, Random.Range(-180f, 180f)), life, RotateMode.LocalAxisAdd))
                .Insert(life * 0.45f, rt.DOScale(0f, life * 0.55f).SetEase(Ease.InQuad))
                .OnComplete(() => { if (im != null) Object.Destroy(im.gameObject); });
        }

        private static Image NewImg(RectTransform parent, Sprite s, Color c, float size)
        {
            var go = new GameObject("fx", typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.sprite = s; im.color = c; im.raycastTarget = false;
            if (s_add == null) { var sh = Shader.Find("WordDrop/UIAdditive") ?? Resources.Load<Shader>("Shaders/UIAdditive"); if (sh != null) s_add = new Material(sh) { name = "CometAdditive" }; }
            if (s_add != null) im.material = s_add;
            im.rectTransform.sizeDelta = Vector2.one * size;
            return im;
        }

        private static Sprite Glow()
        {
            if (s_glow != null) return s_glow;
            const int N = 64; var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(N / 2f, N / 2f)) / (N / 2f);
                float a = Mathf.Clamp01(1f - d); a = a * a * 0.85f + Mathf.Exp(-d * d * 30f) * 0.6f;
                px[y * N + x] = new Color(1, 1, 1, Mathf.Clamp01(a));
            }
            t.SetPixels(px); t.Apply();
            return s_glow = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Four-point twinkle: two thin crossed rays + a soft centre.</summary>
        private static Sprite Star()
        {
            if (s_star != null) return s_star;
            const int N = 64; var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float u = (x + 0.5f) / N * 2f - 1f, v = (y + 0.5f) / N * 2f - 1f;
                float ray = Mathf.Max(Mathf.Exp(-u * u * 260f) * Mathf.Clamp01(1f - Mathf.Abs(v)), Mathf.Exp(-v * v * 260f) * Mathf.Clamp01(1f - Mathf.Abs(u)));
                float centre = Mathf.Exp(-(u * u + v * v) * 18f);
                px[y * N + x] = new Color(1, 1, 1, Mathf.Clamp01(ray * 1.1f + centre * 0.9f));
            }
            t.SetPixels(px); t.Apply();
            return s_star = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
