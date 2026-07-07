using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Explosion shockwave on the FINAL pop of a chain — a bright core FLASH (soft filled circle) plus a
    /// RING edge that races outward past it. Explosion FEEL, not a grow:
    ///   • FULL BRIGHTNESS on frame 1 (an explosion is brightest at t=0), then fades — no fade-in, no hold.
    ///   • OUTEXPO scale — extremely fast attack then hard deceleration, so it "bursts" instead of "grows"
    ///     (a linear / ease-out grow is what killed the impact).
    ///   • Two layers at different speeds — the core flashes and dies fast; the ring races out and lingers.
    /// Additive-blended (bloom catches it, dark bg vanishes), pooled per layer. 2026-07-07 Spencer.
    /// </summary>
    public class ShockwaveRing : MonoBehaviour
    {
        public static ShockwaveRing Instance { get; private set; }

        private const int   POOL_SIZE  = 4;
        private const int   SORT_FILL  = 151;   // core flash, above BigBurstFlash (150)
        private const int   SORT_RING  = 152;   // ring edge, above the fill
        private const float START_SCALE_FRAC = 0.18f; // bursts out from near a point
        private const float RING_END_BOOST   = 1.25f;  // ring races past the fill — the blast edge leads

        private Sprite   _fillSprite, _ringSprite;
        private Material _additiveMat;
        private readonly Stack<SpriteRenderer> _fillPool = new Stack<SpriteRenderer>(POOL_SIZE);
        private readonly Stack<SpriteRenderer> _ringPool = new Stack<SpriteRenderer>(POOL_SIZE);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("ShockwaveRing");
                go.AddComponent<ShockwaveRing>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            LoadSprites();
            for (int i = 0; i < POOL_SIZE; i++) _fillPool.Push(Build(_fillSprite, SORT_FILL, "ShockwaveFill"));
            for (int i = 0; i < POOL_SIZE; i++) _ringPool.Push(Build(_ringSprite, SORT_RING, "ShockwaveRingEdge"));
        }

        private void LoadSprites()
        {
            Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
            if (addShader == null) addShader = Shader.Find("Sprites/Default");
            _additiveMat = new Material(addShader);
            _fillSprite = LoadTex("Particles/soft_circle");
            _ringSprite = LoadTex("Particles/VFX_Circle_3"); // bright ring outline
        }

        private static Sprite LoadTex(string path)
        {
            var tex = Resources.Load<Texture2D>(path);
            if (tex == null) { Debug.LogWarning($"[ShockwaveRing] {path}.png not found"); return null; }
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }

        private SpriteRenderer Build(Sprite sprite, int sortOrder, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.material = _additiveMat;
            sr.sortingOrder = sortOrder;
            sr.enabled = false;
            return sr;
        }

        /// <summary>Fire the two-layer explosion shockwave outward from worldPos. radiusUnits ≈ half-diameter
        /// the ring reaches; the core flash is smaller. tint defaults to HDR white so bloom catches it.</summary>
        public void Play(Vector3 worldPos, float radiusUnits, Color? tint = null)
        {
            // CORE FLASH — bursts to a modest size and dies FAST. The instant white flash IS the impact.
            PlayLayer(_fillSprite, _fillPool, SORT_FILL, worldPos, radiusUnits * 0.70f, tint, 4.5f, 0.20f, 0.15f);
            // RING EDGE — races outward past the flash and lingers slightly as it fades.
            PlayLayer(_ringSprite, _ringPool, SORT_RING, worldPos, radiusUnits * RING_END_BOOST, tint, 6.5f, 0.32f, 0.30f);
        }

        private void PlayLayer(Sprite sprite, Stack<SpriteRenderer> pool, int sortOrder, Vector3 worldPos,
                               float radiusUnits, Color? tint, float hdr, float expandDur, float fadeDur)
        {
            if (sprite == null) return;
            var sr = pool.Count > 0 ? pool.Pop() : Build(sprite, sortOrder, "Shockwave");
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            sr.transform.position = new Vector3(worldPos.x, worldPos.y, -0.7f);
            sr.transform.localRotation = Quaternion.identity;

            float native = Mathf.Max(sprite.bounds.size.x, 0.01f);
            float endScale   = (radiusUnits * 2f) / native; // radiusUnits = half-diameter
            float startScale = endScale * START_SCALE_FRAC;
            sr.transform.localScale = new Vector3(startScale, startScale, 1f);

            // FULL brightness immediately — an explosion is brightest the instant it detonates.
            Color c = tint ?? new Color(hdr, hdr, hdr, 1f);
            c.a = 1f;
            sr.color = c;

            sr.transform.DOKill();
            sr.DOKill();
            // OUTEXPO = savage fast attack then hard decel → reads as a BURST, not a linear grow.
            sr.transform.DOScale(new Vector3(endScale, endScale, 1f), expandDur).SetEase(Ease.OutExpo);
            // Fade out as it expands (front-loaded so it's brightest at the burst, gone by the time it's wide).
            DOTween.ToAlpha(() => sr.color, v => sr.color = v, 0f, fadeDur).SetEase(Ease.OutQuad);

            StartCoroutine(ReturnAfter(sr, pool, Mathf.Max(expandDur, fadeDur) + 0.02f));
        }

        private IEnumerator ReturnAfter(SpriteRenderer sr, Stack<SpriteRenderer> pool, float delay)
        {
            yield return WaitCache.Get(delay);
            if (sr == null) yield break;
            sr.transform.DOKill();
            sr.DOKill();
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.color = Color.white;
            pool.Push(sr);
        }
    }
}
