using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Shatters a tile into 4 sprite fragments that fly outward with gravity.
    /// Pooled — no Instantiate/Destroy during gameplay.
    /// </summary>
    public class TileFragments : MonoBehaviour
    {
        public static TileFragments Instance { get; private set; }

        private const int FRAGMENTS_PER_TILE = 4;
        private const int POOL_SIZE = 32; // enough for 8 tiles exploding at once
        private const float FRAGMENT_LIFETIME = 0.4f;
        private const float GRAVITY = 12f;
        private const float SPIN_SPEED = 360f;

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
            PrewarmPool();
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
            go.SetActive(false);
            return sr;
        }

        private SpriteRenderer Checkout()
        {
            SpriteRenderer sr = _pool.Count > 0 ? _pool.Pop() : CreateFragment();
            sr.gameObject.SetActive(true);
            return sr;
        }

        private void Return(SpriteRenderer sr)
        {
            sr.gameObject.SetActive(false);
            _pool.Push(sr);
        }

        /// <summary>
        /// Shatter a tile into 4 fragments that fly outward.
        /// Call BEFORE hiding/destroying the tile.
        /// </summary>
        public void Shatter(Tile tile)
        {
            if (tile == null) return;

            Sprite tileSprite = tile.GetComponent<SpriteRenderer>()?.sprite;
            if (tileSprite == null) return;

            Texture2D tex = tileSprite.texture;
            if (tex == null) return;

            // Check if texture is readable
            bool texReadable = tex.isReadable;

            Vector3 tilePos = tile.transform.position;
            Vector3 tileScale = tile.transform.localScale;
            Rect spriteRect = tileSprite.textureRect;

            // Slice into 4 quarters
            float halfW = spriteRect.width / 2f;
            float halfH = spriteRect.height / 2f;
            float ppu = tileSprite.pixelsPerUnit;

            Rect[] quadrants = new Rect[]
            {
                new Rect(spriteRect.x, spriteRect.y + halfH, halfW, halfH),           // top-left
                new Rect(spriteRect.x + halfW, spriteRect.y + halfH, halfW, halfH),   // top-right
                new Rect(spriteRect.x, spriteRect.y, halfW, halfH),                   // bottom-left
                new Rect(spriteRect.x + halfW, spriteRect.y, halfW, halfH),           // bottom-right
            };

            // Offset directions for each quarter
            Vector2[] dirs = new Vector2[]
            {
                new Vector2(-1f, 1f).normalized,   // top-left
                new Vector2(1f, 1f).normalized,    // top-right
                new Vector2(-1f, -0.5f).normalized, // bottom-left
                new Vector2(1f, -0.5f).normalized,  // bottom-right
            };

            Debug.Log($"[TileFragments] tex={tex.name} readable={texReadable} rect={spriteRect} halfW={halfW} halfH={halfH} ppu={ppu}");

            for (int i = 0; i < FRAGMENTS_PER_TILE; i++)
            {
                SpriteRenderer sr = Checkout();
                Sprite fragSprite = null;
                if (texReadable)
                {
                    fragSprite = Sprite.Create(tex, quadrants[i], new Vector2(0.5f, 0.5f), ppu);
                    sr.sprite = fragSprite;
                    sr.color = Color.white;
                }
                else
                {
                    Debug.LogWarning("[TileFragments] Texture not readable — using full tile fallback");
                    sr.sprite = tileSprite;
                    sr.color = new Color(1f, 1f, 1f, 0.8f);
                }
                sr.transform.position = tilePos;
                sr.transform.localScale = tileScale;
                sr.transform.rotation = Quaternion.identity;

                float speed = Random.Range(3f, 6f);
                float spin = Random.Range(-SPIN_SPEED, SPIN_SPEED);
                Vector2 vel = dirs[i] * speed + new Vector2(Random.Range(-1f, 1f), Random.Range(-0.5f, 0.5f));

                StartCoroutine(FragmentCoroutine(sr, fragSprite, vel, spin));
            }
        }

        private IEnumerator FragmentCoroutine(SpriteRenderer sr, Sprite fragSprite, Vector2 velocity, float spin)
        {
            float elapsed = 0f;
            Vector3 pos = sr.transform.position;

            while (elapsed < FRAGMENT_LIFETIME)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / FRAGMENT_LIFETIME;

                // Move with velocity + gravity
                velocity.y -= GRAVITY * Time.deltaTime;
                pos.x += velocity.x * Time.deltaTime;
                pos.y += velocity.y * Time.deltaTime;
                sr.transform.position = pos;

                // Spin
                sr.transform.Rotate(0f, 0f, spin * Time.deltaTime);

                // Shrink + fade
                float scale = Mathf.Lerp(1f, 0.3f, t);
                Vector3 baseScale = sr.transform.localScale;
                sr.transform.localScale = new Vector3(
                    Mathf.Sign(baseScale.x) * Mathf.Abs(baseScale.x) * scale / Mathf.Max(scale, 0.01f),
                    Mathf.Sign(baseScale.y) * Mathf.Abs(baseScale.y) * scale / Mathf.Max(scale, 0.01f),
                    1f);
                sr.color = new Color(1f, 1f, 1f, 1f - t);

                yield return null;
            }

            // Cleanup
            Destroy(fragSprite); // destroy the dynamically created sprite
            Return(sr);
        }
    }
}
