using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Candy-Crush-style confetti shatter: ~10 varied-size shards burst outward in 360°,
    /// pop bigger then ease smaller as they fall and fade. Pooled.
    /// </summary>
    public class TileFragments : MonoBehaviour
    {
        public static TileFragments Instance { get; private set; }

        // 4 quadrant chunks of the source tile sprite — varied scale curve, 360° spread, mixed spin.
        private const int FRAGMENTS_PER_TILE = 4;
        private const int POOL_SIZE = 32; // 4 frags x ~8 tiles exploding at once
        private const float FRAGMENT_LIFETIME_MIN = 0.65f;  // bumped from 0.50 — debris linger a hair longer before fading
        private const float FRAGMENT_LIFETIME_MAX = 0.90f;  // bumped from 0.70 — same reason
        private const float GRAVITY = 4f;          // lower pull so shards stay near origin
        private const float SPIN_MIN = 180f;
        private const float SPIN_MAX = 480f;

        // Per-fragment scale envelope (multiplied onto tile.localScale).
        // Dramatic spawn: tiny at emit, pop to peak, vanish small.
        private const float SCALE_START = 0.10f;   // tiny at emit
        private const float SCALE_PEAK = 0.75f;    // peak — smaller than natural quadrant so shards read as shrapnel
        private const float SCALE_END = 0.12f;     // shrink to almost nothing as they fade
        private const float SCALE_PEAK_T = 0.30f;  // when peak hits (0..1 of life)

        // Uniform per-fragment size multiplier (applied onto tile.localScale before envelope).
        // All 4 quadrants render at the same size — no jitter.
        private const float FRAGMENT_SIZE_MUL = 0.70f;

        // Outward burst velocity range. Pushed harder 2026-05-04 round 2 to
        // match the snappier bubble pop — debris should fly out fast at the
        // moment of pop, not drift outward.
        private const float BURST_SPEED_MIN = 2.5f;
        private const float BURST_SPEED_MAX = 5.5f;
        // Negative = downward bias on Y (shards go forward/down, never up).
        private const float DOWNWARD_BIAS = -0.30f;

        // Debris fragments use the VFX_Derbis sprite atlas (4 grayscale shard
        // shapes in a 2×2 layout). Each fragment picks a random shard for variety.
        // White tint (1,1,1) preserves the painted shading and keeps debris
        // below bloom threshold (no glow). Bump R/G/B above 1 if you want
        // bloom to catch them.
        private static readonly Color FRAGMENT_TINT = new Color(1.0f, 1.0f, 1.0f, 1f);
        private Sprite[] _debrisSprites;

        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("TileFragments");
                go.AddComponent<TileFragments>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LoadDebrisSprites();
            PrewarmPool();
        }

        private void LoadDebrisSprites()
        {
            Texture2D tex = Resources.Load<Texture2D>("Particles/debris");
            if (tex == null)
            {
                Debug.LogWarning("[TileFragments] Particles/debris.png not found in Resources — fragments disabled.");
                return;
            }
            int half = tex.width / 2;
            _debrisSprites = new Sprite[4]
            {
                Sprite.Create(tex, new Rect(0,    half, half, half), new Vector2(0.5f, 0.5f), 100f), // top-left
                Sprite.Create(tex, new Rect(half, half, half, half), new Vector2(0.5f, 0.5f), 100f), // top-right
                Sprite.Create(tex, new Rect(0,    0,    half, half), new Vector2(0.5f, 0.5f), 100f), // bottom-left
                Sprite.Create(tex, new Rect(half, 0,    half, half), new Vector2(0.5f, 0.5f), 100f), // bottom-right
            };
            Debug.Log($"[TileFragments] Loaded debris atlas {tex.width}×{tex.height} — 4 sub-sprites built");
        }

        private void PrewarmPool()
        {
            for (int i = 0; i < POOL_SIZE; i++)
                _pool.Push(CreateFragment());
        }

        private SpriteRenderer CreateFragment()
        {
            GameObject go = new GameObject("TileFrag");
            go.transform.SetParent(transform, false);
            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sortingOrder = 31; // above flipbook (30), above tiles (5)
            // Path B: render tile content visible inside debris-shard mask.
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

            // Mask child — debris shard shape carves a shrapnel piece out of
            // the tile texture set on the parent SpriteRenderer. Wide
            // sortingOrder range so the mask catches the parent SR (which
            // sits at sortingOrder 31).
            GameObject maskGO = new GameObject("TileFragMask");
            maskGO.transform.SetParent(go.transform, false);
            SpriteMask mask = maskGO.AddComponent<SpriteMask>();
            mask.frontSortingOrder = 32767;
            mask.backSortingOrder  = -32768;

            go.SetActive(false);
            return sr;
        }

        // Caller must set sr.transform.localScale before calling Activate(),
        // otherwise the pooled object renders one frame at its previous (often peak) scale.
        private SpriteRenderer Checkout()
        {
            SpriteRenderer sr = _pool.Count > 0 ? _pool.Pop() : CreateFragment();
            // Stay inactive — caller activates after configuring scale/sprite/position.
            return sr;
        }

        private void Return(SpriteRenderer sr)
        {
            sr.gameObject.SetActive(false);
            _pool.Push(sr);
        }

        /// <summary>
        /// Shatter a tile into debris shards (VFX_Derbis atlas).
        /// Call BEFORE hiding the tile.
        /// </summary>
        public void Shatter(Tile tile) => Shatter(tile, 1f);

        /// <summary>
        /// Shatter overload that scales the per-fragment base size by
        /// `sizeMultiplier` (1.0 = identical to the no-arg overload).
        /// Used by the Candy-Crush-style tier-1 pop sequence to spawn
        /// chunkier debris that reads as part of the visible "burst".
        /// </summary>
        public void Shatter(Tile tile, float sizeMultiplier)
        {
            if (tile == null || _debrisSprites == null || _debrisSprites.Length == 0) return;

            Vector3 tilePos = tile.transform.position;
            Vector3 tileScale = tile.transform.localScale;

            // Sample tile color + sprite so debris inherits the tile's
            // current state. Path B: each fragment renders the actual tile
            // sprite, masked by a debris shard so the visible shape is a
            // shrapnel piece of the tile texture rather than a generic
            // grayscale shard.
            Color  tileColor   = Color.white;
            Sprite tileContent = null;
            var tileSR = tile.GetComponent<SpriteRenderer>();
            if (tileSR != null)
            {
                tileColor   = tileSR.color;
                tileContent = tileSR.sprite;
            }
            Color shardTint = new Color(
                FRAGMENT_TINT.r * tileColor.r,
                FRAGMENT_TINT.g * tileColor.g,
                FRAGMENT_TINT.b * tileColor.b,
                1f);

            // Use mask mode if we have a tile sprite to render and the
            // debris atlas is available. Otherwise fall back to old shard-
            // only rendering (no mask) as a defensive path.
            bool useMaskMode = tileContent != null;
            // Mask local scale so the shard renders at the same world size
            // as the tile content. Without this the small shard sprite
            // (1.28 wu native) only carves a small central piece of the
            // larger tile sprite (~1.69 wu native).
            float maskLocalScale = 1f;
            if (useMaskMode)
            {
                float contentNative = tileContent.bounds.size.x;
                float shardNative   = _debrisSprites[0].bounds.size.x;
                if (shardNative > 0.001f)
                    maskLocalScale = contentNative / shardNative;
            }

            for (int i = 0; i < FRAGMENTS_PER_TILE; i++)
            {
                SpriteRenderer sr = Checkout();
                SpriteMask mask = sr.GetComponentInChildren<SpriteMask>(true);

                if (useMaskMode)
                {
                    // Content: the actual tile sprite. Masked by a unique
                    // shard per fragment for visual variety (i % 4 cycles
                    // through the 4 atlas shapes).
                    sr.sprite = tileContent;
                    sr.color  = shardTint;
                    sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;
                    if (mask != null)
                    {
                        mask.sprite = _debrisSprites[i % _debrisSprites.Length];
                        mask.transform.localScale = new Vector3(maskLocalScale, maskLocalScale, 1f);
                        mask.transform.localPosition = Vector3.zero;
                        mask.transform.localRotation = Quaternion.identity;
                        mask.enabled = true;
                    }
                }
                else
                {
                    // Fallback: old shard-only rendering.
                    sr.sprite = _debrisSprites[Random.Range(0, _debrisSprites.Length)];
                    sr.color  = shardTint;
                    sr.maskInteraction = SpriteMaskInteraction.None;
                    if (mask != null) mask.enabled = false;
                }

                // Slight per-fragment positional jitter so they don't all stack on the exact center.
                Vector3 jitter = new Vector3(Random.Range(-0.08f, 0.08f), Random.Range(-0.08f, 0.08f), 0f);
                sr.transform.position = tilePos + jitter;
                sr.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

                Vector3 finalBase = new Vector3(
                    tileScale.x * FRAGMENT_SIZE_MUL * sizeMultiplier,
                    tileScale.y * FRAGMENT_SIZE_MUL * sizeMultiplier,
                    1f);

                // Direction: full 360° with a downward bias so shards travel out + down,
                // never popping up off the tile.
                float angle = Random.Range(0f, Mathf.PI * 2f);
                Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle) + DOWNWARD_BIAS).normalized;
                float speed = Random.Range(BURST_SPEED_MIN, BURST_SPEED_MAX);
                Vector2 vel = dir * speed;

                float spin = Random.Range(SPIN_MIN, SPIN_MAX) * (Random.value < 0.5f ? -1f : 1f);
                float life = Random.Range(FRAGMENT_LIFETIME_MIN, FRAGMENT_LIFETIME_MAX);

                sr.transform.localScale = finalBase * SCALE_START;
                sr.gameObject.SetActive(true);

                StartCoroutine(FragmentCoroutine(sr, null, vel, spin, life, finalBase, shardTint));
            }
        }

        private IEnumerator FragmentCoroutine(SpriteRenderer sr, Sprite fragSprite, Vector2 velocity, float spin, float lifetime, Vector3 baseScale, Color tint)
        {
            float elapsed = 0f;
            Vector3 pos = sr.transform.position;

            // Initial scale = SCALE_START * baseScale (small at emit so we can grow into peak)
            sr.transform.localScale = baseScale * SCALE_START;

            while (elapsed < lifetime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / lifetime;

                // Move with velocity + gravity
                velocity.y -= GRAVITY * Time.deltaTime;
                pos.x += velocity.x * Time.deltaTime;
                pos.y += velocity.y * Time.deltaTime;
                sr.transform.position = pos;

                // Spin
                sr.transform.Rotate(0f, 0f, spin * Time.deltaTime);

                // Scale envelope: SCALE_START → SCALE_PEAK at SCALE_PEAK_T → SCALE_END at 1.0
                // Smoothstep on each leg so it feels poppy on the way up and settled on the way down.
                float scaleK;
                if (t < SCALE_PEAK_T)
                {
                    float u = Mathf.SmoothStep(0f, 1f, t / SCALE_PEAK_T);
                    scaleK = Mathf.Lerp(SCALE_START, SCALE_PEAK, u);
                }
                else
                {
                    float u = Mathf.SmoothStep(0f, 1f, (t - SCALE_PEAK_T) / (1f - SCALE_PEAK_T));
                    scaleK = Mathf.Lerp(SCALE_PEAK, SCALE_END, u);
                }
                sr.transform.localScale = baseScale * scaleK;

                // Alpha: hold ~full through the pop, fade only after t≈0.45.
                float alpha = t < 0.45f ? 1f : Mathf.SmoothStep(1f, 0f, (t - 0.45f) / 0.55f);
                sr.color = new Color(tint.r, tint.g, tint.b, alpha);

                yield return null;
            }

            Return(sr);
        }
    }
}
