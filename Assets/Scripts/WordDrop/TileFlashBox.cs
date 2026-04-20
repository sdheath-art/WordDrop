using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Per-cell bright square glow that lights up BEHIND each exploding tile.
    /// Layered under the tile (sortingOrder below the tile's) so the dissolving
    /// letter still reads on top while the cell brightens. Candy-Crush-style
    /// "cell flash" that makes every clear feel satisfying, not just big chains.
    /// Fires on every detonated cell — no gate.
    /// </summary>
    public class TileFlashBox : MonoBehaviour
    {
        public static TileFlashBox Instance { get; private set; }

        private const int   POOL_SIZE        = 24;   // handles a big cluster detonation
        private const float FADE_IN_DURATION  = 0.05f;
        private const float HOLD_DURATION     = 0.06f;
        private const float FADE_OUT_DURATION = 0.30f;
        private const float START_SCALE       = 0.9f;
        private const float END_SCALE         = 1.18f;
        private const int   SORT_ORDER        = 3;    // tiles sit at 5, so this renders behind them

        // Two sprite variants for A/B testing — 50/50 pick per Play call so
        // you can feel both in actual gameplay before committing. Drop either
        // one by setting _variantB to null if you decide.
        private Sprite   _sprite;      // primary: tile_flash_box.png
        private Sprite   _variantB;    // alternate: Square01.png
        private Material _additiveMat;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("TileFlashBox");
                go.AddComponent<TileFlashBox>();
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
            Texture2D tex = Resources.Load<Texture2D>("Particles/tile_flash_box");
            if (tex == null)
            {
                Debug.LogWarning("[TileFlashBox] Particles/tile_flash_box.png not found — " +
                                 "falling back to soft_circle. Flash will render as a round blob " +
                                 "until the real asset is dropped in.");
                tex = Resources.Load<Texture2D>("Particles/soft_circle");
            }
            if (tex == null) { Debug.LogError("[TileFlashBox] No fallback sprite — flashbox disabled."); return; }

            _sprite = Sprite.Create(
                tex, new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f), 100f);

            // A/B variant — load Square01 if present; null-safe fallback means
            // absence quietly collapses back to primary-only.
            Texture2D texB = Resources.Load<Texture2D>("Particles/Square01");
            if (texB != null)
            {
                _variantB = Sprite.Create(
                    texB, new Rect(0, 0, texB.width, texB.height),
                    new Vector2(0.5f, 0.5f), 100f);
            }

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
            var go = new GameObject("TileFlashBox_Instance");
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
        /// Play a per-cell flash box centered at worldPos, sized to cellSizeUnits.
        /// tint defaults to HDR white so bloom picks it up.
        /// </summary>
        public void Play(Vector3 worldPos, float cellSizeUnits, Color? tint = null)
        {
            if (_sprite == null) return;

            SpriteRenderer sr = Checkout();
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            // Slightly behind tile plane so the tile renders on top while dissolving.
            sr.transform.position = new Vector3(worldPos.x, worldPos.y, 0.05f);
            sr.transform.localRotation = Quaternion.identity;

            // A/B test: pick primary or variant 50/50. If _variantB is null (asset
            // missing), this collapses to primary-only.
            bool pickedVariantB = (_variantB != null && Random.value < 0.5f);
            Sprite chosen = pickedVariantB ? _variantB : _sprite;
            sr.sprite = chosen;

            // Variant B (Square01) has a smaller visible area inside a transparent
            // canvas, so it needs an upscale to actually fill the tile. Also renders
            // at lower alpha so it reads as a brightness wash over the tile rather
            // than a solid white block sitting on top of it.
            float variantMultiplier = pickedVariantB ? 2.15f : 1.0f;
            float nativeSize = chosen.bounds.size.x;
            float scale = (cellSizeUnits * variantMultiplier) / Mathf.Max(nativeSize, 0.01f);
            sr.transform.localScale = new Vector3(scale * START_SCALE, scale * START_SCALE, 1f);

            Color c;
            float peakAlpha;
            if (tint.HasValue)
            {
                c = tint.Value;
                peakAlpha = c.a > 0f ? c.a : 1f;
            }
            else if (pickedVariantB)
            {
                // Brightness overlay — subtle lift, not a glow core.
                c = new Color(1.2f, 1.2f, 1.2f, 1f);
                peakAlpha = 0.55f;
            }
            else
            {
                c = new Color(1.5f, 1.5f, 1.5f, 1f); // HDR white (primary)
                peakAlpha = 1f;
            }
            c.a = 0f; // fade-in starts from 0
            sr.color = c;

            sr.transform.DOKill();
            sr.DOKill();
            // Scale eases out slightly past cell bounds for a bloom halo bleed.
            sr.transform.DOScale(new Vector3(scale * END_SCALE, scale * END_SCALE, 1f),
                                 FADE_IN_DURATION + HOLD_DURATION + FADE_OUT_DURATION)
                .SetEase(Ease.OutCubic);

            // Alpha envelope: fast fade in → brief hold at peak → slower fade out.
            // peakAlpha controls the max opacity so variants can render as subtler
            // brightness washes (Square01 @ 0.55) rather than solid glow cores.
            var seq = DOTween.Sequence();
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, peakAlpha, FADE_IN_DURATION)
                .SetEase(Ease.OutQuad));
            seq.AppendInterval(HOLD_DURATION);
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, 0f, FADE_OUT_DURATION)
                .SetEase(Ease.InQuad));

            StartCoroutine(ReturnAfter(sr, FADE_IN_DURATION + HOLD_DURATION + FADE_OUT_DURATION + 0.02f));
        }

        private IEnumerator ReturnAfter(SpriteRenderer sr, float delay)
        {
            yield return WaitCache.Get(delay);
            if (sr == null) yield break;
            sr.transform.DOKill();
            sr.DOKill();
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.color = Color.white;
            _pool.Push(sr);
        }
    }
}
