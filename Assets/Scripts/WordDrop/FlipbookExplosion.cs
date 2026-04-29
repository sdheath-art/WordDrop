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
        // Phase 11h: A/B between circle.png and bubble@2x concluded — bubble
        // wins. circle is a hard ring (white-on-black with a black core) which
        // bloomed into shockwave globs; bubble@2x is a soft radial gradient
        // and reads as a clean halo. Loaded at ppu=200 because the source is
        // 512×512 — keeps world-space bounds identical to the old circle path
        // so the rest of the scale math doesn't need re-tuning.
        private Sprite _glowSpriteBubble;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);
        private readonly Stack<SpriteRenderer> _glowPool = new Stack<SpriteRenderer>(POOL_SIZE);

        // Phase 11j-meltdown: AllIn1 "Magic Explosive Spell" prefab is the
        // hero VFX for meltdown events. Inspector-assignable for easy swap;
        // auto-loaded from Resources/Prefabs/FX/ in Awake if not assigned.
        // Plugins/AllIn1VfxToolkit is gitignored so the prefab + its
        // dependencies (materials, scripts) only resolve on machines that
        // have the package imported.
        [SerializeField] private GameObject _meltdownPrefab;

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

            // Phase 11j-meltdown — auto-load if not Inspector-assigned.
            // FlipbookExplosion is auto-created at runtime so a scene-baked
            // Inspector reference isn't realistic; Resources.Load is the
            // canonical way for this singleton.
            if (_meltdownPrefab == null)
                _meltdownPrefab = Resources.Load<GameObject>("Prefabs/FX/Magic Explosive Spell");
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

            // Glow halo sprite — soft radial gradient. ppu=200 because the
            // source is 512×512 (vs the legacy 256 circle path).
            LoadGlowSprite();
        }

        private void LoadGlowSprite()
        {
            Texture2D bubbleTex = Resources.Load<Texture2D>("Particles/bubble@2x");
            if (bubbleTex != null)
                _glowSpriteBubble = Sprite.Create(
                    bubbleTex,
                    new Rect(0, 0, bubbleTex.width, bubbleTex.height),
                    new Vector2(0.5f, 0.5f), 200f);

            Debug.Log($"[FlipbookFX] glow load — bubble@2x: " +
                      $"{(bubbleTex != null ? $"{bubbleTex.width}x{bubbleTex.height}" : "MISSING")}");
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
            sr.sprite = _glowSpriteBubble;
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

        /// <summary>
        /// Spawn the meltdown hero VFX (AllIn1 Magic Explosive Spell) at
        /// worldPos. One-shot — caller must gate to once per meltdown burst.
        /// Auto-destroys 4s after spawn (prefab plays for ~2-3s, 1s margin).
        /// </summary>
        public void PlayMeltdown(Vector3 worldPos)
        {
            if (_meltdownPrefab == null)
            {
                Debug.LogWarning("[FlipbookFX] Meltdown prefab not assigned (Resources/Prefabs/FX/Magic Explosive Spell) — skipping");
                return;
            }
            // Plays the prefab from t=0 with its full wind-up intact.
            // WordDropFX.ExplosionCoroutine spawns this FIRST, then waits
            // MELTDOWN_WINDUP_DELAY before running the rest of the
            // explosion FX so the prefab's blast peak coincides with the
            // tile destruction (Candy Crush "telegraph then bang" pattern).
            GameObject inst = Instantiate(_meltdownPrefab, worldPos, Quaternion.identity);
            Destroy(inst, 4f);
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

            // A/B concluded — bubble@2x always wins. CreateGlowRenderer set
            // this on prewarm, but reassign defensively in case the sprite
            // loaded after some renderers were already created.
            if (_glowSpriteBubble != null) glow.sprite = _glowSpriteBubble;

            float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;

            // Bubble scales up from small to full size
            float startSize, endSize, duration;
            Color tint;

            // HDR glow halo tints so bloom catches the outer glow ring.
            // Sizes halved from prior values — bubble@2x's native sprite is
            // 2.56 world units (512px @ ppu=200), so the old endSize × cellSize
            // produced ~2-3 cell-wide glows; chains stacked into screen-fill.
            // New endSize multipliers target ~0.5/0.8/1.1/1.4 cells wide for
            // tiers 1/2/3/4 so a 5-way overlap stays bounded.
            switch (tier)
            {
                case 1:
                    startSize = cellSize * 0.04f;
                    endSize = cellSize * 0.20f;
                    tint = new Color(1.3f, 1.75f, 2.2f, 0.5f);
                    duration = 0.25f;
                    break;
                case 2:
                    startSize = cellSize * 0.05f;
                    endSize = cellSize * 0.30f;
                    tint = new Color(1.1f, 2.2f, 1.3f, 0.6f);
                    duration = 0.3f;
                    break;
                case 3:
                    startSize = cellSize * 0.08f;
                    endSize = cellSize * 0.42f;
                    tint = new Color(2.2f, 1.5f, 0.6f, 0.7f);
                    duration = 0.35f;
                    break;
                default:
                    startSize = cellSize * 0.10f;
                    endSize = cellSize * 0.55f;
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
