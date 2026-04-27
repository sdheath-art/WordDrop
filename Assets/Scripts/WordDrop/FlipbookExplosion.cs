using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Plays a sprite sheet flipbook animation for tile explosions.
    /// Includes a soft glow layer behind the flipbook for extra brightness.
    /// Pools all instances to avoid Instantiate/Destroy overhead.
    /// </summary>
    public class FlipbookExplosion : MonoBehaviour
    {
        public static FlipbookExplosion Instance { get; private set; }

        private const int COLS = 4;
        private const int ROWS = 4;
        private const int TOTAL_FRAMES = 16;
        private const int POOL_SIZE = 12;

        private Sprite[] _frames;
        private Material _additiveMat;
        // Two glow sprite variants for the explosion halo. A/B-tested 50/50 per
        // explosion so Spencer can compare the look in playtest. bubble@2x is
        // 512x512 (twice circle's 256), so it's loaded at ppu=200 to keep the
        // world-space size identical — only the texture detail differs.
        private Sprite _glowSpriteCircle;
        private Sprite _glowSpriteBubble;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);
        private readonly Stack<SpriteRenderer> _glowPool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("FlipbookExplosion");
                go.AddComponent<FlipbookExplosion>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            SliceSpriteSheet();
            PrewarmPool();
        }

        private void SliceSpriteSheet()
        {
            Texture2D tex = Resources.Load<Texture2D>("Particles/explosion_flipbook");
            if (tex == null)
            {
                Debug.LogError("[FlipbookExplosion] explosion_flipbook.png not found in Resources/Particles/");
                return;
            }

            _frames = new Sprite[TOTAL_FRAMES];
            float frameW = tex.width / (float)COLS;
            float frameH = tex.height / (float)ROWS;

            for (int row = 0; row < ROWS; row++)
            {
                for (int col = 0; col < COLS; col++)
                {
                    int index = row * COLS + col;
                    float x = col * frameW;
                    float y = (ROWS - 1 - row) * frameH;
                    Rect rect = new Rect(x, y, frameW, frameH);
                    _frames[index] = Sprite.Create(tex, rect, new Vector2(0.5f, 0.5f), 100f);
                }
            }

            // Additive blend — black becomes invisible, white glows bright
            Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
            if (addShader == null) addShader = Shader.Find("Sprites/Default");
            _additiveMat = new Material(addShader);

            // Glow halo sprites — A/B variants
            LoadGlowSprites();
        }

        private void LoadGlowSprites()
        {
            Texture2D circleTex = Resources.Load<Texture2D>("Particles/circle");
            if (circleTex != null)
                _glowSpriteCircle = Sprite.Create(
                    circleTex,
                    new Rect(0, 0, circleTex.width, circleTex.height),
                    new Vector2(0.5f, 0.5f), 100f);

            // bubble@2x is 512x512 — twice circle's 256. Set pixelsPerUnit=200
            // so its world-space bounds match circle's at the same transform
            // scale, isolating texture-look as the single A/B variable.
            Texture2D bubbleTex = Resources.Load<Texture2D>("Particles/bubble@2x");
            if (bubbleTex != null)
                _glowSpriteBubble = Sprite.Create(
                    bubbleTex,
                    new Rect(0, 0, bubbleTex.width, bubbleTex.height),
                    new Vector2(0.5f, 0.5f), 200f);
        }

        private void PrewarmPool()
        {
            for (int i = 0; i < POOL_SIZE; i++)
            {
                _pool.Push(CreateRenderer(30));
                _glowPool.Push(CreateGlowRenderer());
            }
        }

        private SpriteRenderer CreateRenderer(int sortOrder)
        {
            GameObject go = new GameObject("FlipbookFX");
            go.transform.SetParent(transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.material = _additiveMat;
            sr.sortingOrder = sortOrder;
            go.SetActive(false);
            return sr;
        }

        private SpriteRenderer CreateGlowRenderer()
        {
            GameObject go = new GameObject("GlowFX");
            go.transform.SetParent(transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.material = _additiveMat;
            // Sprite is assigned per-explosion in GlowCoroutine for the A/B
            // variant; default to circle here so a renderer that's somehow
            // checked out before GlowCoroutine sets the sprite still draws.
            sr.sprite = _glowSpriteCircle;
            sr.sortingOrder = 29;
            go.SetActive(false);
            return sr;
        }

        private SpriteRenderer Checkout()
        {
            SpriteRenderer sr = _pool.Count > 0 ? _pool.Pop() : CreateRenderer(30);
            sr.gameObject.SetActive(true);
            return sr;
        }

        private void Return(SpriteRenderer sr)
        {
            sr.gameObject.SetActive(false);
            _pool.Push(sr);
        }

        private SpriteRenderer CheckoutGlow()
        {
            SpriteRenderer sr = _glowPool.Count > 0 ? _glowPool.Pop() : CreateGlowRenderer();
            sr.gameObject.SetActive(true);
            return sr;
        }

        private void ReturnGlow(SpriteRenderer sr)
        {
            sr.gameObject.SetActive(false);
            _glowPool.Push(sr);
        }

        /// <summary>
        /// Play flipbook explosion + glow at the given world position.
        /// </summary>
        public void Play(Vector3 worldPos, int tier = 1)
        {
            if (_frames == null || _frames.Length == 0) return;
            StartCoroutine(PlayCoroutine(worldPos, tier));
            StartCoroutine(GlowCoroutine(worldPos, tier));
        }

        private IEnumerator PlayCoroutine(Vector3 worldPos, int tier)
        {
            SpriteRenderer sr = Checkout();
            sr.transform.position = new Vector3(worldPos.x, worldPos.y, -2f);

            float baseSize;
            float duration;
            Color tint;
            // HDR tints (r/g/b > 1.0) so URP bloom catches the flipbook. Without
            // this the primed-tile glow (HDR 1.8-2.2) stops blooming the moment
            // the word detonates — making the payoff LESS bright than the setup.
            // Multiplying RGB by ~2.0-2.2 gets the same visual read post-bloom.
            switch (tier)
            {
                case 1:
                    baseSize = 0.4f;
                    duration = 0.25f;
                    tint = new Color(1.1f, 1.85f, 2.2f, 0.8f); // cool blue pop HDR
                    break;
                case 2:
                    baseSize = 0.55f;
                    duration = 0.3f;
                    tint = new Color(0.7f, 2.2f, 1.1f, 0.9f); // green burst HDR
                    break;
                case 3:
                    baseSize = 0.7f;
                    duration = 0.35f;
                    tint = new Color(2.2f, 1.3f, 0.2f, 1f); // hot orange blast HDR
                    break;
                default:
                    baseSize = 0.85f;
                    duration = 0.4f;
                    tint = new Color(2.2f, 0.7f, 0.9f, 1f); // red/pink chain bomb HDR
                    break;
            }

            float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
            float scale = cellSize * baseSize;
            sr.transform.localScale = new Vector3(scale, scale, 1f);
            sr.color = tint;

            float elapsed = 0f;
            float frameTime = duration / TOTAL_FRAMES;

            while (elapsed < duration)
            {
                int frameIndex = Mathf.Min((int)(elapsed / frameTime), TOTAL_FRAMES - 1);
                sr.sprite = _frames[frameIndex];

                float t = elapsed / duration;
                if (t > 0.7f)
                {
                    float fade = 1f - ((t - 0.7f) / 0.3f);
                    sr.color = new Color(tint.r, tint.g, tint.b, tint.a * fade);
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            Return(sr);
        }

        private IEnumerator GlowCoroutine(Vector3 worldPos, int tier)
        {
            SpriteRenderer glow = CheckoutGlow();
            glow.transform.position = new Vector3(worldPos.x, worldPos.y, -1.5f);

            // 50/50 A/B between circle and bubble@2x glow textures. ppu was
            // tuned at load so both render at the same world size for the same
            // transform scale — only the texture look differs.
            bool useBubble = _glowSpriteBubble != null && Random.value < 0.5f;
            glow.sprite = useBubble ? _glowSpriteBubble : _glowSpriteCircle;

            float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;

            // Bubble scales up from small to full size
            float startSize, endSize, duration;
            Color tint;

            // HDR glow halo tints so bloom catches the outer glow ring.
            switch (tier)
            {
                case 1:
                    startSize = cellSize * 0.08f;
                    endSize = cellSize * 0.4f;
                    tint = new Color(1.3f, 1.75f, 2.2f, 0.5f);
                    duration = 0.25f;
                    break;
                case 2:
                    startSize = cellSize * 0.1f;
                    endSize = cellSize * 0.6f;
                    tint = new Color(1.1f, 2.2f, 1.3f, 0.6f);
                    duration = 0.3f;
                    break;
                case 3:
                    startSize = cellSize * 0.15f;
                    endSize = cellSize * 0.85f;
                    tint = new Color(2.2f, 1.5f, 0.6f, 0.7f);
                    duration = 0.35f;
                    break;
                default:
                    startSize = cellSize * 0.2f;
                    endSize = cellSize * 1.1f;
                    tint = new Color(2.2f, 1.1f, 0.9f, 0.8f);
                    duration = 0.4f;
                    break;
            }

            glow.transform.localScale = new Vector3(startSize, startSize, 1f);
            glow.color = tint;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;

                // Scale up fast then slow (ease out)
                float eased = 1f - (1f - t) * (1f - t);
                float size = Mathf.Lerp(startSize, endSize, eased);
                glow.transform.localScale = new Vector3(size, size, 1f);

                // Fade out in second half
                float alpha = t < 0.4f ? tint.a : tint.a * (1f - ((t - 0.4f) / 0.6f));
                glow.color = new Color(tint.r, tint.g, tint.b, alpha);

                yield return null;
            }

            ReturnGlow(glow);
        }
    }
}
