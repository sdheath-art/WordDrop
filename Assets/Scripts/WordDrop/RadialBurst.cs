using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Centered radial starburst for big-moment detonations. Layers on TOP of
    /// BigBurstFlash to add punch at the impact point — think of it as the
    /// "star at the center of the explosion" that the wide sweep radiates from.
    /// Gated same as BigBurstFlash (chain depth, long word, or multi-cluster).
    /// </summary>
    public class RadialBurst : MonoBehaviour
    {
        public static RadialBurst Instance { get; private set; }

        private const int   POOL_SIZE        = 6;
        private const float RISE_DURATION    = 0.22f;  // scale growth
        private const float FADE_IN_DURATION  = 0.05f;
        private const float FADE_OUT_DURATION = 0.26f;
        private const float START_SCALE       = 0.30f;
        private const float END_SCALE         = 1.50f;
        private const int   SORT_ORDER        = 152;   // above BigBurstFlash (150), below popups

        private Sprite   _sprite;
        private Material _additiveMat;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("RadialBurst");
                go.AddComponent<RadialBurst>();
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
            Texture2D tex = Resources.Load<Texture2D>("Particles/radial_burst");
            if (tex == null)
            {
                Debug.LogWarning("[RadialBurst] Particles/radial_burst.png not found — " +
                                 "falling back to flashfree2. Burst will use generic flash sprite " +
                                 "until the real asset is dropped in.");
                tex = Resources.Load<Texture2D>("Particles/flashfree2")
                   ?? Resources.Load<Texture2D>("Particles/soft_circle");
            }
            if (tex == null) { Debug.LogError("[RadialBurst] No fallback sprite — radial burst disabled."); return; }

            _sprite = Sprite.Create(
                tex, new Rect(0, 0, tex.width, tex.height),
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
            var go = new GameObject("RadialBurst_Instance");
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
        /// Play a radial burst at worldPos, final size = sizeUnits wide.
        /// Rotation random so successive bursts don't look identical.
        /// </summary>
        public void Play(Vector3 worldPos, float sizeUnits, Color? tint = null)
        {
            if (_sprite == null) return;

            SpriteRenderer sr = Checkout();
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            sr.transform.position = new Vector3(worldPos.x, worldPos.y, -0.4f);
            sr.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            float nativeSize = _sprite.bounds.size.x;
            float scale = sizeUnits / Mathf.Max(nativeSize, 0.01f);
            sr.transform.localScale = new Vector3(scale * START_SCALE, scale * START_SCALE, 1f);

            Color c = tint ?? new Color(1.8f, 1.8f, 1.8f, 1f); // HDR white — bloom catches it
            c.a = 0f;
            sr.color = c;

            sr.transform.DOKill();
            sr.DOKill();
            sr.transform.DOScale(new Vector3(scale * END_SCALE, scale * END_SCALE, 1f), RISE_DURATION)
                .SetEase(Ease.OutCubic);

            var seq = DOTween.Sequence();
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, 1f, FADE_IN_DURATION)
                .SetEase(Ease.OutQuad));
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, 0f, FADE_OUT_DURATION)
                .SetEase(Ease.InQuad));

            StartCoroutine(ReturnAfter(sr, FADE_IN_DURATION + FADE_OUT_DURATION + 0.02f));
        }

        private IEnumerator ReturnAfter(SpriteRenderer sr, float delay)
        {
            yield return WaitCache.Get(delay);
            if (sr == null) yield break;
            sr.transform.DOKill();
            sr.DOKill();
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.transform.localRotation = Quaternion.identity;
            sr.color = Color.white;
            _pool.Push(sr);
        }
    }
}
