using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Full-grid "wall of light" flash overlay for big detonation moments.
    /// Candy-Crush-style horizontal sweep — scales out briefly, fades fast,
    /// additive-blended so the pre-rendered black background vanishes.
    /// Gated to big moments only (chain depth, long word, or 3+ word cluster).
    /// </summary>
    public class BigBurstFlash : MonoBehaviour
    {
        public static BigBurstFlash Instance { get; private set; }

        private const int   POOL_SIZE       = 6;  // handles 3-word clusters with headroom for overlapping decays

        // Candy-Crush-style "wall of light emerges from word center" — flash starts
        // as a narrow slice along the word's short axis (tiny X scale), then visibly
        // extends outward to full width over ~240ms. Short axis (Y) barely moves so
        // the blast reads as a beam extending sideways, not a growing cloud.
        private const float RISE_DURATION     = 0.24f; // scale ramp — visible outward extension
        private const float FADE_IN_DURATION  = 0.05f; // 0 → 1 alpha: fast so the growth is visible
        private const float HOLD_DURATION     = 0.04f; // brief peak at full extension
        private const float FADE_OUT_DURATION = 0.45f; // was 0.22 — Phase 11h: longer "after-light" tail like Candy Crush
        private const float START_SCALE_X   = 0.15f;   // narrow sliver — starts at word center
        private const float END_SCALE_X     = 1.15f;   // slight overshoot past the target length
        private const float START_SCALE_Y   = 0.80f;   // near-full thickness from the start
        private const float END_SCALE_Y     = 1.05f;   // minimal Y growth — stays a beam, not a cloud
        private const int   SORT_ORDER      = 150;

        private Sprite   _sprite;
        private Material _additiveMat;
        private readonly Stack<SpriteRenderer> _pool = new Stack<SpriteRenderer>(POOL_SIZE);

        // Phase 11h: radial-glow layer that sits over the existing horizontal
        // beam. Reads as a "light source appearing" at the burst center.
        private Sprite   _radialSprite;
        private readonly Stack<SpriteRenderer> _radialPool = new Stack<SpriteRenderer>(POOL_SIZE);
        private const float RADIAL_HDR_INTENSITY = 4.5f;
        private const float RADIAL_START_SCALE   = 0.2f;
        private const float RADIAL_END_SCALE     = 1.6f;
        private const float RADIAL_RISE_DURATION = 0.18f;
        private const float RADIAL_FADE_OUT      = 0.40f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("BigBurstFlash");
                go.AddComponent<BigBurstFlash>();
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
            Texture2D tex = Resources.Load<Texture2D>("Particles/big_burst_sweep");
            if (tex == null)
            {
                Debug.LogError("[BigBurstFlash] Particles/big_burst_sweep.png not found");
                return;
            }
            _sprite = Sprite.Create(
                tex,
                new Rect(0, 0, tex.width, tex.height),
                new Vector2(0.5f, 0.5f),
                100f);

            Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
            if (addShader == null) addShader = Shader.Find("Sprites/Default");
            _additiveMat = new Material(addShader);

            // Phase 11h radial glow uses Particles/circle as a soft white-to-
            // transparent disc. Falls back to "no radial layer" if missing.
            Texture2D radialTex = Resources.Load<Texture2D>("Particles/circle");
            if (radialTex == null)
            {
                Debug.LogWarning("[BigBurstFlash] Particles/circle.png not found — radial glow disabled");
            }
            else
            {
                _radialSprite = Sprite.Create(
                    radialTex,
                    new Rect(0, 0, radialTex.width, radialTex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
        }

        private void PrewarmPool()
        {
            for (int i = 0; i < POOL_SIZE; i++) _pool.Push(BuildRenderer());
            for (int i = 0; i < POOL_SIZE; i++) _radialPool.Push(BuildRadialRenderer());
        }

        private SpriteRenderer BuildRenderer()
        {
            var go = new GameObject("BigBurstFlash_Instance");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _sprite;
            sr.material = _additiveMat;
            sr.sortingOrder = SORT_ORDER;
            sr.enabled = false;
            return sr;
        }

        private SpriteRenderer Checkout()
        {
            if (_pool.Count > 0) return _pool.Pop();
            return BuildRenderer();
        }

        private SpriteRenderer BuildRadialRenderer()
        {
            var go = new GameObject("BigBurstFlash_Radial");
            go.transform.SetParent(transform, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _radialSprite;
            sr.material = _additiveMat;
            sr.sortingOrder = SORT_ORDER + 1; // sit above the beam
            sr.enabled = false;
            return sr;
        }

        private SpriteRenderer CheckoutRadial()
        {
            if (_radialPool.Count > 0) return _radialPool.Pop();
            return BuildRadialRenderer();
        }

        /// <summary>
        /// Play the flash centered at worldPos, oriented along the word's direction.
        ///   targetLengthUnits    = size along the word's long axis (e.g. screen width)
        ///   targetThicknessUnits = size across the word's short axis (e.g. ~1 cell)
        ///   vertical             = rotate 90° so the sweep runs top-to-bottom
        ///   tint                 = HDR color; defaults to white
        /// Scaling the two axes independently keeps the blast narrow (matching the
        /// word's row height) while still spanning edge-to-edge horizontally.
        /// </summary>
        public void Play(Vector3 worldPos, float targetLengthUnits, float targetThicknessUnits,
                         bool vertical = false, Color? tint = null)
        {
            if (_sprite == null) return;

            SpriteRenderer sr = Checkout();
            sr.gameObject.SetActive(true);
            sr.enabled = true;
            sr.transform.position = new Vector3(worldPos.x, worldPos.y, -0.5f);

            // Rotate 90° for vertical words so the sweep runs top-to-bottom instead
            // of left-to-right. Scale is still applied in sprite-local space (X = long
            // edge), so the rotation handles the orientation change cleanly.
            sr.transform.localRotation = vertical
                ? Quaternion.Euler(0f, 0f, 90f)
                : Quaternion.identity;

            float nativeWidth  = _sprite.bounds.size.x;
            float nativeHeight = _sprite.bounds.size.y;
            float scaleX = targetLengthUnits    / Mathf.Max(nativeWidth,  0.01f);
            float scaleY = targetThicknessUnits / Mathf.Max(nativeHeight, 0.01f);
            sr.transform.localScale = new Vector3(scaleX * START_SCALE_X, scaleY * START_SCALE_Y, 1f);

            // Start invisible — fade in over FADE_IN_DURATION, then fade out.
            // This avoids the "hard on / slow off" snap that reads as harsh.
            // HDR default (2.5× white) so bloom catches the flash even without a
            // caller-supplied tint. Caller tints already in SDR/HDR are respected.
            Color c = tint ?? new Color(4f, 4f, 4f, 1f); // was 2.5 — Phase 11h: stronger bloom drive
            c.a = 0f;
            sr.color = c;

            sr.transform.DOKill();
            sr.DOKill();
            // Scale axes independently — X ramps from narrow sliver → full width
            // so the "wall of light extending outward" reads clearly (matches the
            // Candy Crush reference). OutQuart eases hard at the start then slows
            // into the final width so the sweep feels like it's "arriving."
            sr.transform.DOScale(new Vector3(scaleX * END_SCALE_X, scaleY * END_SCALE_Y, 1f), RISE_DURATION)
                .SetEase(Ease.OutQuart);

            // Alpha envelope: fade in fast (so the growth is visible from first
            // frame) → hold at peak while scale completes → fade out.
            var seq = DOTween.Sequence();
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, 1f, FADE_IN_DURATION)
                .SetEase(Ease.OutQuad));
            seq.AppendInterval(HOLD_DURATION);
            seq.Append(DOTween.ToAlpha(() => sr.color, v => sr.color = v, 0f, FADE_OUT_DURATION)
                .SetEase(Ease.InQuad));

            float totalLife = FADE_IN_DURATION + HOLD_DURATION + FADE_OUT_DURATION + 0.02f;
            StartCoroutine(ReturnAfter(sr, totalLife));

            // Phase 11h: emit a radial glow on top of the beam. Reads as a
            // luminous light source appearing at the burst center, matching
            // the Candy Crush "lit" feel. Sits above the beam in sort order.
            if (_radialSprite != null)
            {
                SpriteRenderer rs = CheckoutRadial();
                rs.gameObject.SetActive(true);
                rs.enabled = true;
                rs.transform.position = new Vector3(worldPos.x, worldPos.y, -0.6f);
                rs.transform.localRotation = Quaternion.identity;

                float radialBase = Mathf.Max(targetThicknessUnits, targetLengthUnits * 0.35f);
                float startScale = radialBase * RADIAL_START_SCALE;
                float endScale   = radialBase * RADIAL_END_SCALE;
                rs.transform.localScale = new Vector3(startScale, startScale, 1f);

                Color rc = new Color(RADIAL_HDR_INTENSITY, RADIAL_HDR_INTENSITY, RADIAL_HDR_INTENSITY, 0f);
                rs.color = rc;

                rs.transform.DOKill();
                rs.DOKill();
                rs.transform.DOScale(new Vector3(endScale, endScale, 1f), RADIAL_RISE_DURATION)
                    .SetEase(Ease.OutCubic);

                var rseq = DOTween.Sequence();
                rseq.Append(DOTween.ToAlpha(() => rs.color, v => rs.color = v, 1f, 0.04f).SetEase(Ease.OutQuad));
                rseq.AppendInterval(0.06f);
                rseq.Append(DOTween.ToAlpha(() => rs.color, v => rs.color = v, 0f, RADIAL_FADE_OUT).SetEase(Ease.InQuad));

                StartCoroutine(ReturnRadialAfter(rs, 0.04f + 0.06f + RADIAL_FADE_OUT + 0.02f));
            }
        }

        private IEnumerator ReturnAfter(SpriteRenderer sr, float delay)
        {
            yield return WaitCache.Get(delay);
            if (sr == null) yield break;
            sr.transform.DOKill();
            sr.DOKill();
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.transform.localRotation = Quaternion.identity; // reset for next checkout
            sr.color = Color.white;
            _pool.Push(sr);
        }

        private IEnumerator ReturnRadialAfter(SpriteRenderer sr, float delay)
        {
            yield return WaitCache.Get(delay);
            if (sr == null) yield break;
            sr.transform.DOKill();
            sr.DOKill();
            sr.enabled = false;
            sr.gameObject.SetActive(false);
            sr.color = Color.white;
            _radialPool.Push(sr);
        }
    }
}
