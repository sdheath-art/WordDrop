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

        // Phase 11j-heat-overlay: a soft rounded-square aura sprite tinted
        // and pulsed during the meltdown wind-up so each tile reads as
        // "heating up before the bang." The aura is rendered through a
        // SpriteMask shaped like the tile so the soft falloff is clipped
        // cleanly to the tile silhouette (no halo bleed past tile edges).
        private Sprite _heatAuraSprite;
        private Sprite _tileMaskSprite;

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

            // Particle VFX pack's Square_aura — a rounded-square soft aura
            // texture. We tint + pulse it ourselves; no AllIn1 shader needed.
            Texture2D auraTex = Resources.Load<Texture2D>("Particles/square_aura");
            if (auraTex != null)
            {
                _heatAuraSprite = Sprite.Create(
                    auraTex,
                    new Rect(0, 0, auraTex.width, auraTex.height),
                    new Vector2(0.5f, 0.5f),
                    100f);
            }
            else
            {
                Debug.LogWarning("[FlipbookFX] Particles/square_aura missing — tile heat-up disabled");
            }

            // Tile sprite acts as the SpriteMask shape so the aura stays
            // clipped to the rounded-rect silhouette.
            _tileMaskSprite = Resources.Load<Sprite>("Tiles/test_tile");
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
            // Two independent layers — gated by the per-layer FX toggles in
            // WordDropFX so Spencer can A/B them separately.
            if (WordDropFX.FX_FlipbookFrames) StartCoroutine(PlayCoroutine(worldPos, tier));
            if (WordDropFX.FX_FlipbookGlow)   StartCoroutine(GlowCoroutine(worldPos, tier));
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
            // Plain spawn — no scale override, no startDelay rewrite, no
            // scalingMode flip. The prefab plays its authored sequence
            // (wind-up → blast → fade) at its natural pace. Destroy timer
            // matches the prefab's full lifecycle (~3-4s).
            GameObject inst = Instantiate(_meltdownPrefab, worldPos, Quaternion.identity);

            // Strip AllIn1 demo helper scripts that error in our scene
            // (no AllIn1Shaker singleton exists). We have our own shake
            // systems via WordDropFX so the demo helper is redundant anyway.
            var demoShakers = inst.GetComponentsInChildren<AllIn1VfxToolkit.Demo.Scripts.AllIn1DoShake>(true);
            for (int i = 0; i < demoShakers.Length; i++)
                if (demoShakers[i] != null) Destroy(demoShakers[i]);

            // Slight upsize so the FX reads as cluster-spanning. ParticleSystem
            // scalingMode must be Hierarchy (not the default Local) for the
            // transform scale to actually grow the emitted particles. Only
            // touches scaling-related fields — startDelays / playOnAwake /
            // particle counts left untouched so the prefab's authored
            // sequence (wind-up → blast → fade) runs naturally.
            const float MELTDOWN_SCALE = 1.7f;
            inst.transform.localScale = Vector3.one * MELTDOWN_SCALE;
            var systems = inst.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null) continue;
                var main = systems[i].main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            }

            // Force every Renderer in the spawned hierarchy in front of
            // the tile sprites. Tiles render at sortingOrder = 5; the prefab
            // defaults to 0 which puts the FX behind them. Sort 50 lands
            // above tiles + their letter text (5 / 6) and is in the same
            // band as FlipbookExplosion (30) — below the screen-spanning
            // BigBurstFlash beam (150) which we want to remain on top.
            const int MELTDOWN_SORT_ORDER = 50;
            var renderers = inst.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].sortingOrder = MELTDOWN_SORT_ORDER;

            Destroy(inst, 4f);
        }

        /// <summary>
        /// Spawn the AllIn1 charge-up swirl as an overlay on a tile during
        /// meltdown wind-up. Plays for `duration` seconds, fading alpha
        /// 0→1 so it ramps in then peaks right before the explosion fires.
        /// One overlay per tile; auto-destroys when the duration completes.
        /// </summary>
        public void PlayTileHeatOverlay(Vector3 worldPos, float cellSize, float duration)
        {
            if (_heatAuraSprite == null) return;
            StartCoroutine(TileHeatOverlayCoroutine(worldPos, cellSize, duration));
        }

        private IEnumerator TileHeatOverlayCoroutine(Vector3 worldPos, float cellSize, float duration)
        {
            // Parent overlay GO holds the SpriteMask + aura SpriteRenderer.
            GameObject overlay = new GameObject("TileHeatOverlay");
            overlay.transform.position = new Vector3(worldPos.x, worldPos.y, -0.4f);

            // Mask child — uses tile sprite, sized to one tile, clips the aura
            // to the rounded-rect silhouette so the soft falloff doesn't
            // bleed past tile edges.
            SpriteMask mask = null;
            if (_tileMaskSprite != null)
            {
                GameObject maskGO = new GameObject("TileHeatMask");
                maskGO.transform.SetParent(overlay.transform, false);
                mask = maskGO.AddComponent<SpriteMask>();
                mask.sprite = _tileMaskSprite;
                // Mask scaled to exactly one cell.
                float maskNative = _tileMaskSprite.bounds.size.x;
                float maskScale = cellSize / Mathf.Max(maskNative, 0.001f);
                maskGO.transform.localScale = new Vector3(maskScale, maskScale, 1f);
                mask.isolateMaskedSprites = true; // confine masking to children
            }

            // Aura sprite — tinted, pulsed, alpha-faded, masked by the
            // SpriteMask above so visible pixels are clipped to the tile shape.
            SpriteRenderer sr = overlay.AddComponent<SpriteRenderer>();
            sr.sprite = _heatAuraSprite;
            sr.sortingOrder = 10; // above tiles (5/6), below meltdown prefab (50)
            // VisibleInsideMask = render only where the mask sprite has alpha.
            sr.maskInteraction = SpriteMaskInteraction.VisibleInsideMask;

            // Magenta/pink aura tint with HDR > 1 so bloom catches it cleanly.
            Color baseTint = new Color(1.6f, 0.4f, 1.4f, 1f);

            Debug.Log($"[FX-Heat] Spawned tile heat aura at {worldPos} duration={duration:F2}s mask={(mask != null ? "ON" : "OFF")}");

            float elapsed = 0f;
            // Math: square_aura.png is 256x256 at ppu=100 → native world
            // size 2.56 units. Tile is cellSize units (~0.6). To make the
            // aura match a tile, scale = cellSize / 2.56. The +0.20 padding
            // factor at endScale lets it grow ~20% past tile edges for
            // a "swelling" feel without overlapping neighbours.
            float nativeSize = _heatAuraSprite.bounds.size.x;
            float baseScale = (cellSize * 0.85f) / nativeSize;
            float endScale  = (cellSize * 1.20f) / nativeSize;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // Alpha: ease-in-quad from 0 → 1 — subtle at start, building.
                float alpha = t * t;

                // Scale: gentle ramp 0.95× → 1.30× over the wind-up so the
                // aura "swells" as charge builds. Plus a fast 6Hz wobble
                // for subtle pulse jitter (keeps it feeling alive, not
                // a static grow).
                float wobble = 1f + Mathf.Sin(elapsed * 6f * Mathf.PI * 2f) * 0.04f;
                float s = Mathf.Lerp(baseScale, endScale, t) * wobble;
                overlay.transform.localScale = new Vector3(s, s, 1f);

                sr.color = new Color(baseTint.r, baseTint.g, baseTint.b, alpha);
                yield return null;
            }

            if (overlay != null) Destroy(overlay);
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
