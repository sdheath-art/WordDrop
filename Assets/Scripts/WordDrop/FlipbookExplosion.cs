using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

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

        // Speed multiplier applied to every ParticleSystem in the spawned
        // meltdown prefab. >1 plays faster, <1 plays slower. WordDropFX
        // reads this constant to scale MELTDOWN_WINDUP_DELAY automatically
        // so tile destruction stays aligned with the blast peak.
        //   2.0 = very snappy but prefab finishes before tile dissolve completes
        //   1.5 = compromise: faster wind-up, still visible through tile dissolve
        //   1.0 = original authored timing (Spencer 2026-05-01)
        public const float MELTDOWN_PREFAB_SPEED = 1.0f;
        // Authored blast peak in the prefab is at startDelay 1.7s in real
        // time; at simulationSpeed=N the peak moves to 1.7 / N.
        public const float MELTDOWN_BLAST_PEAK_AT_REAL_SPEED = 1.70f;

        // Phase 11j-heat-overlay: a soft rounded-square aura sprite tinted
        // and pulsed during the meltdown wind-up so each tile reads as
        // "heating up before the bang." The aura is rendered through a
        // SpriteMask shaped like the tile so the soft falloff is clipped
        // cleanly to the tile silhouette (no halo bleed past tile edges).
        private Sprite _heatAuraSprite;
        private Sprite _tileMaskSprite;
        private Sprite _glowOrbSprite;
        private Material _glowOrbMat;

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

            // Soft-circle sprite for the round primed-glow orb overlay
            // (Candy-Crush-color-bomb style — a pulsing radial halo on top
            // of each tile during the meltdown windup, NOT clipped to tile
            // silhouette so the glow extends past edges).
            // Load priority: vfx_glow (clean radial gradient — bright center,
            // soft falloff, no organic shape) → previous fallbacks. Switched
            // from water_puddle 2026-04-30: Spencer wanted a simple clean
            // glow that scales up, not a distorting cloud.
            Texture2D orbTex = Resources.Load<Texture2D>("Particles/vfx_glow")
                            ?? Resources.Load<Texture2D>("Particles/water_puddle")
                            ?? Resources.Load<Texture2D>("Particles/acid_puddle")
                            ?? Resources.Load<Texture2D>("Particles/glow")
                            ?? Resources.Load<Texture2D>("Particles/glowfree1")
                            ?? Resources.Load<Texture2D>("Particles/soft_circle")
                            ?? Resources.Load<Texture2D>("Particles/circle");
            if (orbTex != null)
            {
                _glowOrbSprite = Sprite.Create(orbTex,
                    new Rect(0, 0, orbTex.width, orbTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                // Photoshop "Screen" blend (no UV warp) — clean static glow
                // that brightens the underlying tile without blowing out.
                // Movement now comes solely from the scale ramp-up, not from
                // any shader-side distortion.
                Shader screenShader = Shader.Find("WordDrop/ScreenSprite")
                                  ?? Shader.Find("Sprites/Default");
                _glowOrbMat = new Material(screenShader);
            }
            else
            {
                Debug.LogWarning("[FlipbookFX] No soft_circle/glow/circle in Resources/Particles — primed glow orb disabled");
            }
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
            // Orange shrapnel sprite-sheet retired 2026-04-30 — colored prefab + tile shards
            // carry the impact now. Glow halo layer kept (still gated by FX_FlipbookGlow).
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
            // Bumped to 2.7× per Spencer 2026-05-01 — bloom is now tamer
            // (threshold 1.30, intensity 0.20) so the prefab can be larger
            // without blowing out into a screen-spanning white blob.
            const float MELTDOWN_SCALE = 2.7f;
            // Brightness multiplier on each PS's startColor — 0.75 softens
            // additive stacking enough to avoid white-out without sapping
            // the prefab's life.
            const float MELTDOWN_BRIGHTNESS = 0.75f;
            inst.transform.localScale = Vector3.one * MELTDOWN_SCALE;
            var systems = inst.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null) continue;
                var main = systems[i].main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.simulationSpeed = MELTDOWN_PREFAB_SPEED;

                // Dim startColor in-place so the prefab's additive layers
                // stack without blowing out into white. Constant-mode and
                // gradient-mode both supported.
                var sc = main.startColor;
                if (sc.mode == ParticleSystemGradientMode.Color)
                {
                    Color c = sc.color;
                    sc.color = new Color(c.r * MELTDOWN_BRIGHTNESS, c.g * MELTDOWN_BRIGHTNESS, c.b * MELTDOWN_BRIGHTNESS, c.a);
                }
                else if (sc.mode == ParticleSystemGradientMode.TwoColors)
                {
                    Color a = sc.colorMin;
                    Color b = sc.colorMax;
                    sc.colorMin = new Color(a.r * MELTDOWN_BRIGHTNESS, a.g * MELTDOWN_BRIGHTNESS, a.b * MELTDOWN_BRIGHTNESS, a.a);
                    sc.colorMax = new Color(b.r * MELTDOWN_BRIGHTNESS, b.g * MELTDOWN_BRIGHTNESS, b.b * MELTDOWN_BRIGHTNESS, b.a);
                }
                main.startColor = sc;

                // Strip the prefab's authored projectile/shrapnel layers — our
                // own TileFragments handles per-tile chunk shatter, so we don't
                // want competing prefab-level pieces flying around.
                string n = systems[i].name;
                if (n == "AbosrbMagicPieces" || n == "AbosrbMagicPiecesExplosion")
                {
                    var emission = systems[i].emission;
                    emission.enabled = false;
                    systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
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

            // Destroy timer scales with the simulation speed so we don't
            // either truncate the tail (timer too short) or leak invisible
            // GOs (timer too long).
            Destroy(inst, 4f / MELTDOWN_PREFAB_SPEED);
        }

        /// <summary>
        /// Variant of PlayMeltdown that takes an explicit uniform scale.
        /// Use this when spawning a single prefab to span an entire WORD
        /// instead of one prefab per tile — the caller computes the word's
        /// bounding-box size and passes a scale that covers it. Particles
        /// stay circular (uniform scale, no stretching). 2026-05-30.
        /// </summary>
        public void PlayMeltdownSized(Vector3 worldPos, float scale)
        {
            if (_meltdownPrefab == null)
            {
                Debug.LogWarning("[FlipbookFX] Meltdown prefab not assigned (Resources/Prefabs/FX/Magic Explosive Spell) — skipping");
                return;
            }
            GameObject inst = Instantiate(_meltdownPrefab, worldPos, Quaternion.identity);

            var demoShakers = inst.GetComponentsInChildren<AllIn1VfxToolkit.Demo.Scripts.AllIn1DoShake>(true);
            for (int i = 0; i < demoShakers.Length; i++)
                if (demoShakers[i] != null) Destroy(demoShakers[i]);

            // Floor at MELTDOWN_SCALE so a single-tile-word still reads as
            // "cluster-spanning". Cap at 6.0 to avoid frame-eating fill-rate
            // spikes on big clusters — the prefab's particles scale with the
            // transform, so unbounded scale → unbounded overdraw.
            const float MELTDOWN_BRIGHTNESS = 0.75f;
            const float MELTDOWN_SCALE_MAX  = 6.0f;
            float finalScale = Mathf.Clamp(scale, 2.7f, MELTDOWN_SCALE_MAX);
            inst.transform.localScale = Vector3.one * finalScale;
            // 2026-05-30: fast-forward each ParticleSystem past the windup
            // phase so the prefab spawns VISIBLE at its blast peak — skips
            // the ~1.7s lead-up entirely per Spencer's preference. The
            // ExplosionCoroutine also skips its corresponding WINDUP_DELAY
            // wait so the tile destruct + pops land at the same blast frame.
            float fastForwardTime = MELTDOWN_BLAST_PEAK_AT_REAL_SPEED / MELTDOWN_PREFAB_SPEED;
            var systems = inst.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < systems.Length; i++)
            {
                if (systems[i] == null) continue;
                var main = systems[i].main;
                main.scalingMode = ParticleSystemScalingMode.Hierarchy;
                main.simulationSpeed = MELTDOWN_PREFAB_SPEED;

                var sc = main.startColor;
                if (sc.mode == ParticleSystemGradientMode.Color)
                {
                    Color c = sc.color;
                    sc.color = new Color(c.r * MELTDOWN_BRIGHTNESS, c.g * MELTDOWN_BRIGHTNESS, c.b * MELTDOWN_BRIGHTNESS, c.a);
                }
                else if (sc.mode == ParticleSystemGradientMode.TwoColors)
                {
                    Color a = sc.colorMin;
                    Color b = sc.colorMax;
                    sc.colorMin = new Color(a.r * MELTDOWN_BRIGHTNESS, a.g * MELTDOWN_BRIGHTNESS, a.b * MELTDOWN_BRIGHTNESS, a.a);
                    sc.colorMax = new Color(b.r * MELTDOWN_BRIGHTNESS, b.g * MELTDOWN_BRIGHTNESS, b.b * MELTDOWN_BRIGHTNESS, b.a);
                }
                main.startColor = sc;

                string n = systems[i].name;
                if (n == "AbosrbMagicPieces" || n == "AbosrbMagicPiecesExplosion")
                {
                    var emission = systems[i].emission;
                    emission.enabled = false;
                    systems[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }

                // Fast-forward THIS particle system past the windup so it
                // becomes visible at its blast peak. withChildren=false
                // since we're already iterating GetComponentsInChildren above.
                systems[i].Simulate(fastForwardTime, false, true);
                systems[i].Play(false);
            }

            const int MELTDOWN_SORT_ORDER = 50;
            var renderers = inst.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
                if (renderers[i] != null) renderers[i].sortingOrder = MELTDOWN_SORT_ORDER;

            Destroy(inst, 4f / MELTDOWN_PREFAB_SPEED);
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

        /// <summary>
        /// Tier-1 Candy-Crush-style overlay square — bright frame around the
        /// tile, similar to the heat overlay but UNCLIPPED (extends past tile
        /// edges) and faster. Used by PlayTier1Pop as the bright square that
        /// pulses in CC frame 1 of the pop reference.
        /// </summary>
        public void PlayPopOverlaySquare(Vector3 worldPos, float cellSize, float duration, Color tint)
        {
            if (_heatAuraSprite == null) return;
            StartCoroutine(PopOverlaySquareCoroutine(worldPos, cellSize, duration, tint));
        }

        private IEnumerator PopOverlaySquareCoroutine(Vector3 worldPos, float cellSize, float duration, Color tint)
        {
            GameObject overlay = new GameObject("Tier1PopOverlaySquare");
            overlay.transform.position = new Vector3(worldPos.x, worldPos.y, -0.45f);

            SpriteRenderer sr = overlay.AddComponent<SpriteRenderer>();
            sr.sprite = _heatAuraSprite;
            sr.material = _additiveMat;
            sr.sortingOrder = 28; // above tiles (5/6), below bubble (29) and fragments (31)

            // square_aura native size = 2.56 world units (256px @ ppu=100).
            // FIXED scale — does not animate. Sized close to one tile width so
            // the soft falloff extends just slightly past tile edges.
            float nativeSize = _heatAuraSprite.bounds.size.x;
            float fixedScale = (cellSize * 0.85f) / Mathf.Max(nativeSize, 0.001f);
            overlay.transform.localScale = new Vector3(fixedScale, fixedScale, 1f);

            // Constant low alpha — subtle frame, not a peaked flash. Brief
            // fade-in at start + fade-out at end so it doesn't pop in/out.
            const float HOLD_ALPHA = 0.20f;
            const float FADE_IN  = 0.04f; // first 40ms ramp up
            const float FADE_OUT = 0.06f; // last 60ms ramp down

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;

                float alpha;
                if (elapsed < FADE_IN)
                    alpha = Mathf.Lerp(0f, HOLD_ALPHA, elapsed / FADE_IN);
                else if (elapsed > duration - FADE_OUT)
                    alpha = Mathf.Lerp(HOLD_ALPHA, 0f, (elapsed - (duration - FADE_OUT)) / FADE_OUT);
                else
                    alpha = HOLD_ALPHA;

                sr.color = new Color(tint.r, tint.g, tint.b, tint.a * alpha);
                yield return null;
            }

            if (overlay != null) Destroy(overlay);
        }

        /// <summary>
        /// Tier-1 Candy-Crush-style "pop" bubble halo. Spawns the bubble@2x
        /// sprite at worldPos, scales 0.5×→1.6× cellSize×sizeMultiplier,
        /// fades alpha 0 → peak (0.85) at midpoint → 0, with a caller-
        /// provided tint (replaces the hardcoded HDR cyan in GlowCoroutine
        /// for this NEW path only — existing GlowCoroutine is untouched).
        /// Cleans up the spawned GameObject when finished.
        /// </summary>
        public void PlayBubble(Vector3 pos, Color tint, float sizeMultiplier, float duration)
        {
            if (_glowSpriteBubble == null)
            {
                LoadGlowSprite();
                if (_glowSpriteBubble == null) return;
            }
            StartCoroutine(BubbleCoroutine(pos, tint, sizeMultiplier, duration));
        }

        private IEnumerator BubbleCoroutine(Vector3 pos, Color tint, float sizeMultiplier, float duration)
        {
            GameObject bubble = new GameObject("Tier1PopBubble");
            bubble.transform.position = new Vector3(pos.x, pos.y, -1.5f);

            SpriteRenderer sr = bubble.AddComponent<SpriteRenderer>();
            sr.sprite = _glowSpriteBubble;
            // Alpha-blended overlay (NOT additive). CC bubbles are translucent
            // — you see through them to the candy. Additive on a white-radial
            // texture would clip to white at the center (bloom amplifier rule).
            // Leaving sr.material unset → SpriteRenderer uses the default
            // Sprites/Default material (Blend SrcAlpha OneMinusSrcAlpha).
            sr.sortingOrder = 29;

            float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
            // bubble@2x is loaded at ppu=200 → native sprite bounds ≈ 2.56 world
            // units. Shrunk 15% from prior values per Spencer 2026-05-01.
            // startScale bumped 2026-05-04 from 0.051 to 0.30 — with the
            // continuous-expansion + simultaneous-fade pattern, a 0.051 start
            // meant the bubble was a dot at peak alpha and only grew large
            // after most of the alpha had already faded. Reading from a
            // meaningful size ensures the bubble is visibly present.
            float startScale = 0.30f * cellSize * sizeMultiplier;
            float endScale   = 0.574f * cellSize * sizeMultiplier;

            // Translucent overlay alpha — see-through, like a soap bubble.
            const float PEAK_ALPHA = 0.45f;

            // Initial state — small, invisible.
            bubble.transform.localScale = new Vector3(startScale, startScale, 1f);
            sr.color = new Color(tint.r, tint.g, tint.b, 0f);

            // Scale: continuous expansion across the FULL duration. No pop,
            // no hold, no shrink. OutQuart for an aggressive front-loaded
            // expansion — bubble has gained 75% of its size in the first half
            // of the (now 120ms) duration, decelerating into the final 25%.
            // Reads as "fast pop" rather than "slow grow."
            bubble.transform.DOScale(new Vector3(endScale, endScale, 1f), duration)
                .SetEase(DG.Tweening.Ease.OutQuart);

            // Alpha: instant snap-on (5% of duration ≈ 6ms), then fade-out
            // across the remaining 95% with InCubic. InCubic fades slowly at
            // first and accelerates at the end — bubble stays visible through
            // most of its expansion, then disappears quickly at the end.
            // (OutQuart was wrong here — front-loaded the fade so the bubble
            // was at ~6% alpha by the halfway point.)
            DG.Tweening.Sequence alphaSeq = DOTween.Sequence();
            alphaSeq.Append(DOTween.ToAlpha(() => sr.color, c => sr.color = c, PEAK_ALPHA, duration * 0.05f)
                .SetEase(DG.Tweening.Ease.OutQuad));
            alphaSeq.Append(DOTween.ToAlpha(() => sr.color, c => sr.color = c, 0f, duration * 0.95f)
                .SetEase(DG.Tweening.Ease.InCubic));

            yield return new WaitForSeconds(duration);

            if (bubble != null)
            {
                bubble.transform.DOKill();
                if (sr != null) sr.DOKill();
                Destroy(bubble);
            }
        }

        /// <summary>
        /// Candy-Crush-color-bomb-style primed glow orb: a round soft-circle
        /// sprite layered over the tile. Pulsing diffused light overlay
        /// using Photoshop "Screen" blend mode. If `targetTile` is provided,
        /// the orb dies the instant the tile becomes inactive (shatter hides
        /// it) so the overlay shuts off cleanly when the tile disappears.
        /// </summary>
        public void PlayPrimedGlowOrb(Vector3 worldPos, float cellSize, float duration, Transform targetTile = null)
        {
            if (_glowOrbSprite == null) return;
            StartCoroutine(PrimedGlowOrbCoroutine(worldPos, cellSize, duration, targetTile));
        }

        private IEnumerator PrimedGlowOrbCoroutine(Vector3 worldPos, float cellSize, float duration, Transform targetTile)
        {
            GameObject orb = new GameObject("PrimedGlowOrb");
            orb.transform.position = new Vector3(worldPos.x, worldPos.y, -0.5f);

            SpriteRenderer sr = orb.AddComponent<SpriteRenderer>();
            sr.sprite = _glowOrbSprite;
            sr.material = _glowOrbMat;
            // 3 = BEHIND tiles (5/6) — the orb halo extends past the tile
            // edges so you see it as a glow surrounding the tile, while the
            // tile itself stays clean and readable on top.
            sr.sortingOrder = 3;

            // Saturated cool blue — R/G dropped, B pushed further above the
            // bloom threshold so bloom catches a stronger blue rim.
            Color baseTint = new Color(0.25f, 0.55f, 1.20f, 1f);

            float nativeSize = _glowOrbSprite.bounds.size.x;
            // Orb sized 1.8× cell — halo extends past tile edges but tighter
            // than the previous 2.2 (tile renders on top at sortingOrder 5/6).
            float orbScale = (cellSize * 1.80f) / Mathf.Max(nativeSize, 0.001f);
            const float PEAK_ALPHA = 0.38f; // translucent so candy/tile shows through

            // Three-phase alpha envelope so the orb stays bright through the
            // windup AND the explosion, only fading at the tail:
            //   ramp up (0 → 0.3s):  0 → 1.0
            //   hold (0.3s → end-0.4s): 1.0
            //   ramp down (last 0.4s): 1.0 → 0
            const float RAMP_UP   = 0.30f;
            const float RAMP_DOWN = 0.40f;
            float holdEnd = Mathf.Max(RAMP_UP, duration - RAMP_DOWN);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                // Tile shattered → orb dies immediately. Skips the slow tail
                // fade so the overlay shuts off the instant the tile vanishes.
                if (targetTile != null && !targetTile.gameObject.activeInHierarchy)
                    break;

                elapsed += Time.deltaTime;

                float alpha;
                if (elapsed < RAMP_UP)
                    alpha = Mathf.SmoothStep(0f, PEAK_ALPHA, elapsed / RAMP_UP);
                else if (elapsed < holdEnd)
                    alpha = PEAK_ALPHA;
                else
                    alpha = Mathf.SmoothStep(PEAK_ALPHA, 0f, (elapsed - holdEnd) / Mathf.Max(0.01f, duration - holdEnd));

                // Scale envelope — start at 70% so the halo is visible
                // immediately when the animation starts, then grow to 100%
                // over the RAMP_UP window. (No ongoing warp; just grow + hold.)
                float scaleK = elapsed < RAMP_UP
                    ? Mathf.SmoothStep(0.70f, 1.00f, elapsed / RAMP_UP)
                    : 1.00f;
                float s = orbScale * scaleK;
                orb.transform.localScale = new Vector3(s, s, 1f);

                sr.color = new Color(baseTint.r, baseTint.g, baseTint.b, alpha);
                yield return null;
            }

            if (orb != null) Destroy(orb);
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
                // Span the mask's sorting-order range across the aura's
                // sortingOrder (10). Default is 0–0, which would mean the
                // mask doesn't affect our aura at sortingOrder 10 → aura
                // gets VisibleInsideMask but never sees a mask hit → renders
                // invisible. Wide range = mask catches anything in this
                // sortingLayer.
                mask.frontSortingOrder = 32767;
                mask.backSortingOrder  = -32768;
                // SpriteMask only affects sprites whose maskInteraction is
                // set to VisibleInsideMask / VisibleOutsideMask. Tile
                // SpriteRenderers default to None, so they're untouched.
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
