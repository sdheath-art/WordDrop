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
        private const float MIN_SPEED         = 2.5f;   // world units / sec
        private const float MAX_SPEED         = 6.0f;
        private const float GRAVITY           = -5.5f;
        private const float LIFE_DUR          = 0.55f;
        private const float SPARKLE_SIZE_MIN  = 0.08f;  // world units
        private const float SPARKLE_SIZE_MAX  = 0.18f;
        private const int   SORT_ORDER        = 155;

        private Sprite _sprite;
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
            // Reuse an existing small bright sprite — flashfree2 / soft_circle work fine
            // for "tiny star" duty when HDR-tinted. Real star asset can slot in here later.
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

            Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
            if (addShader == null) addShader = Shader.Find("Sprites/Default");
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
            if (_sprite == null) return;

            intensity = Mathf.Clamp01(intensity);
            int count = Mathf.RoundToInt(Mathf.Lerp(MIN_SPARKLES, MAX_SPARKLES, intensity));
            float speedScale = Mathf.Lerp(0.85f, 1.15f, intensity);

            // HDR white default — bloom catches it as tiny blown stars.
            Color baseColor = tint ?? new Color(2.4f, 2.4f, 2.5f, 1f);

            for (int i = 0; i < count; i++)
            {
                SparkleOne(worldPos, baseColor, speedScale);
            }
        }

        private void SparkleOne(Vector3 origin, Color tint, float speedScale)
        {
            SpriteRenderer sr = Checkout();
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            sr.transform.position = new Vector3(origin.x, origin.y, -0.6f);
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            float size = Random.Range(SPARKLE_SIZE_MIN, SPARKLE_SIZE_MAX);
            sr.transform.localScale = new Vector3(size, size, 1f);

            float angle = Random.Range(0f, Mathf.PI * 2f);
            float speed = Random.Range(MIN_SPEED, MAX_SPEED) * speedScale;
            Vector2 velocity = new Vector2(Mathf.Cos(angle) * speed, Mathf.Sin(angle) * speed + 1.5f);
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

            while (elapsed < life && sr != null)
            {
                float dt = Time.deltaTime;
                elapsed += dt;

                vel.y += GRAVITY * dt;
                pos.x += vel.x * dt;
                pos.y += vel.y * dt;
                t.position = pos;

                t.Rotate(0f, 0f, spin * dt);

                // Alpha profile: snap to 1 then ease out to 0 over last 60% of life
                float tn = elapsed / life;
                float a = tn < 0.15f ? 1f : Mathf.SmoothStep(1f, 0f, (tn - 0.15f) / 0.85f);
                Color c = sr.color;
                c.a = a;
                sr.color = c;

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
