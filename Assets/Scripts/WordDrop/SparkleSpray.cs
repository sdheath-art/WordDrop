using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Radial spray of small HDR "stars" on big detonations. Fills the Candy-Crush
    /// gap where primed tiles glow (HDR) but the detonation itself had no
    /// radiating particles — so the peak moment had less sparkle than the setup.
    ///
    /// Each sparkle flies outward on a random radial arc with gravity pull + spin
    /// + fade. HDR white tint means bloom catches them as tiny blown-out stars.
    /// Big-moment gated (chain depth ≥ 2 OR cluster size ≥ 3).
    /// </summary>
    public class SparkleSpray : MonoBehaviour
    {
        public static SparkleSpray Instance { get; private set; }

        private const int   POOL_SIZE         = 48;
        private const int   MIN_SPARKLES      = 8;
        private const int   MAX_SPARKLES      = 16;
        private const float MIN_SPEED         = 1.0f;   // world units / sec — tighter blast radius
        private const float MAX_SPEED         = 2.8f;
        private const float GRAVITY           = -3.5f;  // softer pull so shorter throw doesn't crash to floor
        private const float LIFE_DUR          = 0.75f;  // bumped from 0.60 — sparkles linger a hair longer before fading out
        // localScale on a 2.56-unit native flare sprite. Range tuned to
        // visually match SparkleLine's ~0.5-1.0 world-unit particle size.
        // Flare = big 4-pointed star; size matches SparkleLine.
        private const float FLARE_SIZE_MIN  = 0.20f;
        private const float FLARE_SIZE_MAX  = 0.45f;
        // Point = small soft dot. Native texture is 32×32 — these values render
        // the dot at ~0.06-0.16 world units (tiny twinkles, well under flare size).
        private const float POINT_SIZE_MIN  = 0.20f;
        private const float POINT_SIZE_MAX  = 0.50f;
        // Per-explosion counts — explosion emits 4 flares + 7 dots.
        private const int   FLARE_COUNT     = 4;
        private const int   POINT_COUNT     = 7;
        private const int   SORT_ORDER      = 155;

        private Sprite _flareSprite;
        private Sprite _pointSprite;
        private Sprite _sprite; // legacy field — kept so prewarm/Build still works
        private Material _additiveMat;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("SparkleSpray");
                go.AddComponent<SparkleSpray>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LoadSprite();
            PrewarmPool();
        }

        private void LoadSprite()
        {
            // Particle VFX 4-pointed star — preferred over generic radial blobs
            // because the spike pattern reads as "sparkle" instead of "blur".
            // The texture is 256×256 with 3 sparkles laid out across it; we
            // crop to one star (bottom-left quadrant) so each particle renders
            // a single sparkle, not three.
            // Hovl flare (256×256) — big 4-pointed star.
            Texture2D flareTex = Resources.Load<Texture2D>("Particles/flare");
            if (flareTex != null)
            {
                _flareSprite = Sprite.Create(flareTex,
                    new Rect(0, 0, flareTex.width, flareTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                _sprite = _flareSprite; // for prewarm pool default
                Debug.Log($"[SparkleSpray] Loaded flare.png {flareTex.width}×{flareTex.height}");
            }

            // Hovl Point1 (32×32) — small soft dot, supplemental "twinkle" particle.
            Texture2D pointTex = Resources.Load<Texture2D>("Particles/point1");
            if (pointTex != null)
            {
                _pointSprite = Sprite.Create(pointTex,
                    new Rect(0, 0, pointTex.width, pointTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                Debug.Log($"[SparkleSpray] Loaded point1.png {pointTex.width}×{pointTex.height}");
            }

            // Fallback if neither loaded — keep the system functional with a generic blob.
            if (_flareSprite == null && _pointSprite == null)
            {
                Debug.LogWarning("[SparkleSpray] flare.png + point1.png both missing — falling back to soft_circle/radial");
                Texture2D tex = Resources.Load<Texture2D>("Particles/soft_circle")
                             ?? Resources.Load<Texture2D>("Particles/flashfree2")
                             ?? Resources.Load<Texture2D>("Particles/radial_burst");
                if (tex == null)
                {
                    Debug.LogWarning("[SparkleSpray] No particle sprite found — spray disabled.");
                    return;
                }
                _sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                _flareSprite = _sprite;
            }
            else if (_sprite == null) _sprite = _pointSprite; // ensure non-null for prewarm

            // Additive blend — black/transparent regions of the sparkle textures
            // contribute nothing instead of rendering as opaque black squares.
            // Bloom is now tame enough (threshold 1.30, intensity 0.20) that
            // additive doesn't blow these into white blobs anymore.
            Shader addShader = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            _additiveMat = new Material(addShader);
        }

        private void PrewarmPool()
        {
            for (int i = 0; i < POOL_SIZE; i++) _pool.Push(BuildRenderer());
        }

        private SpriteRenderer BuildRenderer()
        {
            var go = new GameObject("Sparkle");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sprite;
            sr.material = _additiveMat;
            sr.sortingOrder = SORT_ORDER;
            sr.enabled = false;
            go.SetActive(false);
            return sr;
        }

        private SpriteRenderer Checkout()
        {
            if (_pool.Count > 0) return _pool.Pop();
            return BuildRenderer();
        }

        /// <summary>
        /// Spray sparkles from worldPos. intensity 0-1 scales sparkle count + speed.
        /// Optional tint overrides default HDR white.
        /// </summary>
        public void Play(Vector3 worldPos, float intensity = 1f, Color? tint = null)
        {
            if (_flareSprite == null && _pointSprite == null) return;

            intensity = Mathf.Clamp01(intensity);
            float speedScale = Mathf.Lerp(0.85f, 1.15f, intensity);

            // Below bloom threshold (1.30) — sparkles render at their actual
            // painted brightness without bloom-blur destroying the star shape.
            Color baseColor = tint ?? new Color(1.10f, 1.10f, 1.15f, 1f);

            // 4 big flares + 7 small point dots per explosion. Counts scale
            // gently with intensity so smaller events still emit fewer.
            int flares = Mathf.RoundToInt(FLARE_COUNT * Mathf.Lerp(0.6f, 1f, intensity));
            int points = Mathf.RoundToInt(POINT_COUNT * Mathf.Lerp(0.6f, 1f, intensity));

            for (int i = 0; i < flares; i++)
                SparkleOne(worldPos, baseColor, speedScale, _flareSprite, FLARE_SIZE_MIN, FLARE_SIZE_MAX);

            for (int i = 0; i < points; i++)
                SparkleOne(worldPos, baseColor, speedScale, _pointSprite ?? _flareSprite, POINT_SIZE_MIN, POINT_SIZE_MAX);
        }

        private void SparkleOne(Vector3 origin, Color tint, float speedScale, Sprite sprite, float sizeMin, float sizeMax)
        {
            SpriteRenderer sr = Checkout();
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            if (sprite != null) sr.sprite = sprite;
            sr.transform.position = new Vector3(origin.x, origin.y, -0.6f);
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            float size = Random.Range(sizeMin, sizeMax);
            sr.transform.localScale = new Vector3(size, size, 1f);

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(MIN_SPEED, MAX_SPEED) * speedScale;
            Vector2 velocity = new Vector2(Mathf.Cos(angle) * speed, Mathf.Sin(angle) * speed + 0.5f);
            float spin = Random.Range(-540f, 540f);
            float life = LIFE_DUR * Random.Range(0.85f, 1.15f);

            Color c = tint;
            c.a = 1f;
            sr.color = c;

            StartCoroutine(FlyAndFade(sr, velocity, spin, life));
        }

        private IEnumerator FlyAndFade(SpriteRenderer sr, Vector2 velocity, float spin, float life)
        {
            float elapsed = 0f;
            Transform t = sr.transform;
            Vector3 pos = t.position;
            Vector2 vel = velocity;

            // Cache initial scale so the shrink envelope can lerp from it to a
            // smaller end size in lockstep with the alpha fade.
            Vector3 startScale = t.localScale;
            const float SHRINK_END = 0.30f; // sparkle ends at 30% of its launch size

            while (elapsed < life && sr != null)
            {
                float dt = Time.deltaTime;
                elapsed += dt;

                vel.y += GRAVITY * dt;
                pos.x += vel.x * dt;
                pos.y += vel.y * dt;
                t.position = pos;

                t.Rotate(0f, 0f, spin * dt);

                // Alpha profile: snap to 1 then ease out to 0 over last 85% of life
                float tn = elapsed / life;
                float a = tn < 0.15f ? 1f : Mathf.SmoothStep(1f, 0f, (tn - 0.15f) / 0.85f);
                Color c = sr.color;
                c.a = a;
                sr.color = c;

                // Scale shrinks alongside the fade — starts shrinking at the
                // same t=0.15 mark, ends at SHRINK_END × startScale.
                float scaleK = tn < 0.15f
                    ? 1f
                    : Mathf.Lerp(1f, SHRINK_END, Mathf.SmoothStep(0f, 1f, (tn - 0.15f) / 0.85f));
                t.localScale = startScale * scaleK;

                yield return null;
            }

            if (sr == null) yield break;
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.transform.localRotation = Quaternion.identity;
            sr.color = Color.white;
            _pool.Push(sr);
        }
    }
}
