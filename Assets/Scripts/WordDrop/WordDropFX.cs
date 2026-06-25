using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using MoreMountains.Tools;

namespace WordDrop
{
    /// <summary>
    /// Centralized procedural effects system — Balatro-style juice via DOTween.
    ///
    /// Every visual effect goes through here. Called by HandManager and
    /// GameVisualBridge during resolution phases.
    ///
    /// Design: DOTween for all transforms. No manual coroutine lerps.
    /// Every tween uses easing curves for snappy, weighted, alive feel.
    /// </summary>
    public class WordDropFX : MonoBehaviour
    {
        public static WordDropFX Instance { get; private set; }

        // ── Tuning: Word scored ─────────────────────────────────────────────────
        public const float SCORE_POP_STRENGTH   = 0.12f;  // subtler pop
        public const float SCORE_POP_DURATION   = 0.14f;
        public const float TILE_STAGGER_DELAY   = 0.03f;

        // ── Tuning: Detonation ──────────────────────────────────────────────────
        public const float DETONATE_SQUEEZE     = 0.85f;
        public const float DETONATE_SQUEEZE_DUR = 0.06f;
        public const float DETONATE_POP         = 0.35f;
        public const float DETONATE_POP_DUR     = 0.15f;
        public const float DETONATE_TOTAL_DUR   = 0.25f;

        // ── Tuning: Board shake ─────────────────────────────────────────────────
        public const float SHAKE_BASE           = 0.08f;
        public const float SHAKE_PER_CHAIN      = 0.04f;
        public const float SHAKE_MAX            = 0.25f;
        public const float SHAKE_DURATION       = 0.25f;

        // ── Tuning: Explosion ───────────────────────────────────────────────────
        public const float EXPLODE_DURATION     = 0.15f;
        public const float EXPLODE_STAGGER      = 0.02f;
        public const float EXPLODE_PUNCH_ROT    = 20f;

        // ── Tuning: Tile drop landing ───────────────────────────────────────────
        public const float LAND_SQUASH_AMOUNT   = 0.20f;
        public const float LAND_SQUASH_DUR      = 0.15f;

        // ── Tuning: Chain escalation ────────────────────────────────────────────
        public const float CHAIN_SPEED_MULT     = 0.80f;
        public const float CHAIN_MIN_BEAT       = 0.10f;

        // ── Tuning: Card animations ─────────────────────────────────────────────
        public const float CARD_DEAL_DUR        = 0.12f;
        public const float CARD_DEAL_STAGGER    = 0.03f;
        public const float CARD_SELECT_DUR      = 0.10f;
        public const float CARD_SHUFFLE_DUR     = 0.15f;

        // ── Tuning: Detonation particles ──────────────────────────────────────
        public const int   PARTICLE_BURST_COUNT  = 12;
        public const float PARTICLE_LIFETIME_MIN = 0.15f;
        public const float PARTICLE_LIFETIME_MAX = 0.30f;
        public const float PARTICLE_SPEED        = 3.5f;
        public const float PARTICLE_SIZE          = 0.12f;

        // ── State ───────────────────────────────────────────────────────────────
        private Transform _gridRoot;
        private ParticleSystem _detonationParticles;
        private Material _cachedSpriteMat;

        // ── Lifecycle ───────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("WordDropFX");
                go.AddComponent<WordDropFX>();
//                 Debug.Log("[WordDropFX] Auto-created with DOTween.");
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DOTween.Init(false, true, LogBehaviour.ErrorsOnly);
            DOTween.defaultEaseType = DG.Tweening.Ease.OutQuad;
        }

        private void Start()
        {
            if (GridManager.Instance != null)
                _gridRoot = GridManager.Instance.transform;

            CreateDetonationParticleSystem();
        }

        private void CreateDetonationParticleSystem()
        {
            var go = new GameObject("DetonationParticles");
            go.transform.SetParent(transform);

            _detonationParticles = go.AddComponent<ParticleSystem>();

            // Stop auto-play
            var emission = _detonationParticles.emission;
            emission.enabled = false;

            var main = _detonationParticles.main;
            main.playOnAwake = false;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(PARTICLE_LIFETIME_MIN, PARTICLE_LIFETIME_MAX);
            main.startSpeed = new ParticleSystem.MinMaxCurve(PARTICLE_SPEED * 0.5f, PARTICLE_SPEED);
            main.startSize = new ParticleSystem.MinMaxCurve(PARTICLE_SIZE * 0.5f, PARTICLE_SIZE);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 2.5f;
            main.maxParticles = 200;

            // Color: hot orange → yellow with fade out
            var colorOverLife = _detonationParticles.colorOverLifetime;
            colorOverLife.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 0.55f, 0.1f), 0f),    // hot orange
                    new GradientColorKey(new Color(1f, 0.9f, 0.3f), 0.3f),   // bright yellow
                    new GradientColorKey(new Color(1f, 0.3f, 0.1f), 1f)      // dim ember
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.9f, 0.4f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLife.color = grad;

            // Size: shrink over lifetime
            var sizeOverLife = _detonationParticles.sizeOverLifetime;
            sizeOverLife.enabled = true;
            sizeOverLife.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

            // Shape: small sphere burst
            var shape = _detonationParticles.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.05f;

            // Use default sprite renderer (white particle)
            var renderer = go.GetComponent<ParticleSystemRenderer>();
            renderer.sortingOrder = 20; // above tiles
            if (_cachedSpriteMat == null) _cachedSpriteMat = new Material(Shader.Find("Sprites/Default"));
            renderer.sharedMaterial = _cachedSpriteMat;
        }

        /// <summary>
        /// Fires a burst of ember particles at the given world position.
        /// Called per-tile during detonation dissolve.
        /// </summary>
        public void EmitDetonationBurst(Vector3 worldPos, int count = -1)
        {
            if (_detonationParticles == null) return;
            if (count < 0) count = PARTICLE_BURST_COUNT;

            _detonationParticles.transform.position = worldPos;

            var emitParams = new ParticleSystem.EmitParams();
            emitParams.position = worldPos;
            _detonationParticles.Emit(emitParams, count);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // WORD SCORED — staggered highlight with punch scale
        // ═══════════════════════════════════════════════════════════════════════════

        public void PlayWordScored(List<Tile> tiles, Color color, int chainStep)
        {
            if (tiles == null || tiles.Count == 0) return;
            Debug.Log($"[ScoredFlash] PlayWordScored fired: {tiles.Count} tile(s) arg3={chainStep} first='{(tiles[0] != null ? tiles[0].Letter.ToString() : "?")}'"); // TEMP cascade diagnostic 2026-06-11

            // Feel-pass 2026-05-16: cap punch at 0.18 (1.18× peak).
            // Was uncapped — chain 6 reached 0.42 (1.42×), bigger than the
            // detonation pop (1.30×) and broke hierarchy. Word-scored is
            // the smaller celebration; detonation is the big one. Chain 0-1
            // values are unchanged; chain 2+ gets clamped to 1.18×.
            float punch = Mathf.Min(SCORE_POP_STRENGTH + chainStep * 0.05f, 0.18f);

            // Staggered fuse-lit flash across tiles — each tile pops in sequence
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;

                // 2026-06-03 Spencer: sync a committed wild's resolved letter onto the
                // visual tile NOW so the GREEN flash shows the real letter (e.g. 'D'),
                // not a blank "?" until it primes magenta. ResolveWilds has already
                // committed the rules letter by this point; this covers EVERY
                // PlayWordScored caller (the per-path sync was missing the live path).
                if (tile.IsWild && RulesEngine.Instance != null)
                {
                    var rc = RulesEngine.Instance.GetCell(tile.Col, tile.Row);
                    if (rc != null && rc.Letter != '\0' && rc.Letter != TileBag.WILD_CHAR
                        && tile.Letter != rc.Letter)
                        tile.SetLetter(rc.Letter);
                }

                tile.transform.DOComplete();
                tile.SetScoredSprite(true);
                tile.SetSortingOrder(15);

                // Stagger: each tile flashes white slightly after the previous
                // Feel-pass 2026-05-16: 0.06 → 0.03 (matches TILE_STAGGER_DELAY
                // constant). On 5-letter words this halves total stagger from
                // 300ms → 150ms, so the last tile pops while the first is
                // still mid-animation — feels like a wave, not a queue.
                float delay = i * TILE_STAGGER_DELAY;
                int idx = i;
                SpriteRenderer sr = tile.GetComponent<SpriteRenderer>();

                // Flash white → fade to normal over staggered timing
                if (sr != null)
                {
                    sr.color = Color.white; // start white; the green comes from the flash peak below
                    // 2026-06-10 Spencer: base tile is now the LIGHT test_tile (not the green
                    // sprite), so the old R/B of 1.12 washed the flash toward white-green.
                    // Drop R/B so the peak reads as a vivid green bloom on a light base; green
                    // channel still floored at 1.62 for the glow.
                    Color flashGreen = new Color(0.30f, Mathf.Max(ScoredFlashGreen, 1.45f), 0.30f, 1f);
                    // 2026-06-04 Spencer: snap to bright green, HOLD at peak so the bloom
                    // actually reads, THEN settle. Was a 0.15s fade with no hold → far too
                    // quick to see, and the tile primes magenta the same frame which used
                    // to stomp it entirely. HoldPrimedVisual below defers that takeover.
                    const float SCORE_FLASH_HOLD = 0.12f;
                    const float SCORE_FLASH_FADE = 0.22f;
                    Tile flashTile = tile; // capture for the overlay callbacks
                    DOTween.Sequence()
                        .AppendInterval(delay)
                        .AppendCallback(() => {
                            if (sr != null) sr.color = flashGreen; // green-biased flash; green peak is the bloom lever (FX Bloom Tuning slider)
                            // MOBILE bloom: sr.color clamps to 1.0 on iOS/Metal, so the green
                            // never crosses the 1.30 bloom line on device. Drive the additive
                            // overlay with the same HDR green so it glows on the phone. No-op on
                            // desktop (sr.color HDR already blooms there).
                            if (flashTile != null) flashTile.SetBloomGlow(flashGreen, 0.90f);
                        })
                        .AppendInterval(SCORE_FLASH_HOLD)
                        .Append(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; },
                            Color.white, SCORE_FLASH_FADE).SetEase(DG.Tweening.Ease.OutQuad)) // settle to WHITE so the magenta primed-sprite handoff isn't muddied dark by a lingering green tint
                        .Join(DG.Tweening.DOVirtual.Float(0.90f, 0f, SCORE_FLASH_FADE,
                            a => { if (flashTile != null) flashTile.SetBloomGlow(flashGreen, a); })); // fade the mobile overlay out with the flash
                    // Hold the magenta primed takeover (sprite swap + per-frame pulse) until
                    // this tile's green flash has played through, so it doesn't get stomped.
                    tile.HoldPrimedVisual(delay + SCORE_FLASH_HOLD + SCORE_FLASH_FADE + 0.02f);
                }

                tile.transform
                    .DOPunchScale(Vector3.one * punch, SCORE_POP_DURATION, 1, 0.5f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack)
                    .OnComplete(() => {
                        if (tile != null) {
                            tile.SetSortingOrder(5);
                            // 2026-06-03 Spencer: keep the green scored sprite — do NOT
                            // revert to white here. The tile stays green after the flash
                            // (no white flicker), holding green straight through until
                            // detonation. (Detonation/reset clears the scored state.)
                            // tile.SetScoredSprite(false);
                        }
                    });

                // Temporary shadow under the tile during the pop
                // Shadow disabled — cleaner without floating shadows during animations
                // StartCoroutine(TileScoredShadow(tile));
            }

            // Sound + particles + screen shake + neighbor ripple on word scored
            // Pass chainStep so the chime pitches up alongside the pop — both
            // sounds rise together in the chord climb. Bug fix 2026-05-16.
            GameAudio.Instance?.PlayWordScored(chainStep);
            if (tiles.Count > 0 && tiles[0] != null)
            {
                Vector3 center = Vector3.zero;
                foreach (var tile in tiles) if (tile != null) center += tile.transform.position;
                center /= Mathf.Max(1, tiles.Count);
                GameParticles.Instance?.PlayWordScored(center, tiles.Count * 3);
            }
            // Feel-pass 2026-05-16: gate board shake to bigger payoffs only
            // (spec: reserve visible shake for top ~20% of events). Routine
            // 3-4 letter matches on chainStep 0 no longer shake the board.
            if (tiles.Count >= 5 || chainStep >= 1)
                PlayBoardShake(-1);
            PlayNeighborRipple(tiles, chainStep);
        }

        private IEnumerator TileScoredShadow(Tile tile)
        {
            if (tile == null) yield break;

            SpriteRenderer tileSR = tile.GetComponent<SpriteRenderer>();
            if (tileSR == null || tileSR.sprite == null) yield break;

            // Create a temporary shadow sprite — slightly larger, offset, behind tile
            GameObject shadowGO = new GameObject("ScoreShadow");
            float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.5f;
            float offset = cellSize * 0.08f;
            shadowGO.transform.position = tile.transform.position + new Vector3(offset, -offset * 1.5f, 0f);
            shadowGO.transform.localScale = tile.transform.localScale * 1.08f; // slightly larger than tile

            SpriteRenderer sr = shadowGO.AddComponent<SpriteRenderer>();
            sr.sprite = tileSR.sprite;
            sr.sortingOrder = 4;
            sr.color = new Color(0.02f, 0.01f, 0.08f, 0.6f);

            // Hold while flash is active
            yield return WaitCache.Get(0.18f);

            // Fade out
            float fadeDur = 0.12f;
            float elapsed = 0f;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / fadeDur);
                sr.color = new Color(0.02f, 0.01f, 0.08f, 0.6f * (1f - t));
                yield return null;
            }

            Destroy(shadowGO);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // DETONATION — squeeze → pop → shake
        // ═══════════════════════════════════════════════════════════════════════════
        // FUSE TRACE — visual line connecting direct triggers to chain triggers
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Draw shimmer lines from direct-triggered primed tiles to chain-triggered tiles.
        /// Lines fade out over 0.3s. Uses simple LineRenderer per connection.
        /// </summary>
        public void PlayFuseTrace(List<PrimedTriggeredEvent> triggers, GridManager grid)
        {
            // Defensive early-return: belt + suspenders so even if a path
            // forgets to check FX_FuseTrace, no lines are drawn while disabled.
            if (!FX_FuseTrace) return;
            if (triggers == null || grid == null) return;

            // Collect direct trigger tile positions and chain trigger tile positions
            List<Vector3> directPositions = new List<Vector3>();
            List<Vector3> chainPositions = new List<Vector3>();

            for (int i = 0; i < triggers.Count; i++)
            {
                var trig = triggers[i];
                if (trig.TriggeredCells == null || trig.TriggeredCells.Count == 0) continue;

                // Use center of triggered word
                Vector3 center = Vector3.zero;
                int validCount = 0;
                for (int c = 0; c < trig.TriggeredCells.Count; c++)
                {
                    Tile t = grid.GetTile(trig.TriggeredCells[c].x, trig.TriggeredCells[c].y);
                    if (t != null) { center += t.transform.position; validCount++; }
                }
                if (validCount == 0) continue;
                center /= validCount;

                if (trig.IsChainTrigger)
                    chainPositions.Add(center);
                else
                    directPositions.Add(center);
            }

            if (directPositions.Count == 0 || chainPositions.Count == 0) return;

            // Draw a line from each direct trigger to each chain trigger
            Color fuseColor = new Color(1f, 0.75f, 0.2f, 0.8f);
            for (int d = 0; d < directPositions.Count; d++)
            {
                for (int c = 0; c < chainPositions.Count; c++)
                {
                    StartCoroutine(AnimateFuseLine(directPositions[d], chainPositions[c], fuseColor));
                }
            }

//             Debug.Log($"[FuseTrace] Drew {directPositions.Count * chainPositions.Count} trace(s) " +
                      // $"from {directPositions.Count} direct → {chainPositions.Count} chain");
        }

        private IEnumerator AnimateFuseLine(Vector3 from, Vector3 to, Color color)
        {
            GameObject lineGO = new GameObject("FuseTrace");
            LineRenderer lr = lineGO.AddComponent<LineRenderer>();
            lr.positionCount = 2;
            lr.SetPosition(0, from);
            lr.SetPosition(1, to);
            lr.startWidth = 0.04f;
            lr.endWidth = 0.04f;
            if (_cachedSpriteMat == null) _cachedSpriteMat = new Material(Shader.Find("Sprites/Default"));
            lr.sharedMaterial = _cachedSpriteMat;
            lr.startColor = color;
            lr.endColor = color;
            lr.sortingOrder = 20;
            lr.useWorldSpace = true;

            // Fade out over 0.3s
            float dur = 0.3f;
            float elapsed = 0f;
            while (elapsed < dur)
            {
                elapsed += Time.deltaTime;
                float alpha = Mathf.Lerp(0.8f, 0f, elapsed / dur);
                Color c = new Color(color.r, color.g, color.b, alpha);
                lr.startColor = c;
                lr.endColor = c;
                yield return null;
            }

            Destroy(lineGO);
        }

        // ═══════════════════════════════════════════════════════════════════════════

        public void PlayDetonation(List<Tile> tiles, int chainStep, bool suppressAudio = false)
        {
            if (tiles == null || tiles.Count == 0) return;
            // Audio is suppressed during meltdown so the bang lands at the
            // tile-disappear moment instead of at the start of the squeeze.
            if (!suppressAudio) GameAudio.Instance?.PlayDetonation(chainStep);

            // Cluster-center orange burst (GameParticles.PlayDetonation) removed
            // 2026-04-30 — fired _burstSystem + _chainSystem orange flares that
            // were visible during meltdowns as orange leafy petals at the
            // cluster center. Per-tile shatter + sparkle spray + the prefab's
            // own halo carry the impact now.

            foreach (var tile in tiles)
            {
                if (tile == null) continue;
                Transform t = tile.transform;
                Vector3 orig = t.localScale;

                Sequence seq = DOTween.Sequence();
                seq.Append(t.DOScale(orig * DETONATE_SQUEEZE, DETONATE_SQUEEZE_DUR)
                    .SetEase(DG.Tweening.Ease.InBack, 2f));
                seq.AppendCallback(() =>
                {
                    if (tile != null) tile.FlashHighlight(Color.white);
                });
                seq.Append(t.DOScale(orig * 1.3f, DETONATE_POP_DUR * 0.4f)
                    .SetEase(DG.Tweening.Ease.OutBack, 4f));
                // Feel-pass 2026-05-16: OutElastic → OutQuad on return.
                // Cartoony 1.30× peak preserved; the elastic wobble after the
                // peak was reading as "rubber UI" instead of "solid magical
                // snap." OutQuad lands the tile clean, no oscillation.
                seq.Append(t.DOScale(orig, DETONATE_POP_DUR * 0.8f)
                    .SetEase(DG.Tweening.Ease.OutQuad));
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPLOSION — staggered shrink + rotation punch
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Boost a color's HSV saturation by a multiplier. Used to make
        /// fragment tints pop — texture-averaged samples (PRIMED_TILE_TINT,
        /// SCORED_TILE_TINT) come out muted because averaging dilutes the
        /// brightest saturation peaks; this boost pushes them back toward
        /// the bright color the player perceives on the tile.
        /// </summary>
        private static Color BoostSaturation(Color c, float satMultiplier)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            s = Mathf.Clamp01(s * satMultiplier);
            // Also bump V toward 1 if it's dim (so dark-mid colors brighten).
            v = Mathf.Clamp01(Mathf.Max(v, 0.85f));
            Color result = Color.HSVToRGB(h, s, v);
            result.a = c.a;
            return result;
        }

        // 2026-05-30: counter tracks how many ExplosionCoroutines are
        // currently running. Used by StageClearModal to gate its presentation
        // until the explosion animations complete — prevents the stage-clear
        // modal from flying in mid-explosion when the score crosses target
        // partway through a multi-word cascade.
        public static int ActiveExplosions { get; private set; }
        public static bool HasActiveExplosions => ActiveExplosions > 0;

        /// <summary>
        /// big_pop accent — fired ONCE per detonation STEP. Spencer's rule:
        /// "big_pop on any explosion of 8+ tiles that is NOT a gravity cascade."
        /// The cascade test MUST use the true chain depth (chainDepth == 0 ==
        /// the player's first explosion; >= 1 == a gravity-formed follow-up) and
        /// the FULL step tile count — neither is reliably available inside
        /// PlayExplosion (the chainStep param is overloaded across resolver
        /// paths, and multi-word steps are split into small per-word groups).
        /// So every resolver calls this at the step level instead. PlayBigPop's
        /// own 0.30s cooldown dedupes if two paths fire for the same step.
        /// </summary>
        // ── Big Pop sync — LIVE-EDITABLE IN PLAY MODE ──────────────────────────
        // Select the WordDropFX GameObject in the hierarchy while Playing and
        // drag these in the Inspector; changes apply on the next detonation, no
        // recompile. _bigPopHold is the anticipation window (snaps to ~16ms
        // frames — coarse). _bigPopNudge shifts the big_pop SOUND continuously
        // (+ = later/toward the pop, − = earlier) for sub-frame fine-tuning.
        [Header("Big Pop Sync (drag during Play)")]
        [Tooltip("Seconds big_pop plays before the explosion fires. Coarse — snaps to ~16ms frames.")]
        [Range(0f, 0.6f)] public float _bigPopHold = 0.152f; // Spencer-tuned by ear 2026-06-02
        [Tooltip("Fine shift of the big_pop sound. + = later (toward the pop), − = earlier. Continuous.")]
        [Range(-0.12f, 0.12f)] public float _bigPopNudge = 0f;
        [Tooltip("Visual 'pucker': the cluster scales to this during the hold, then bursts. 1 = off (no suck-in).")]
        [Range(0.6f, 1f)] public float _bigPopSuckScale = 0.86f;
        [Tooltip("Ease for the suck-in. InBack = pucker overshoot (lips), InCubic = smooth breath-in, InQuad = gentle.")]
        public Ease _bigPopSuckEase = Ease.InBack;
        [Tooltip("Big hero bubble at the cluster centre: inflates over the hold then pops. Value = diameter vs cluster span (1.3 = 30% bigger). 0 = off.")]
        [Range(0f, 2.5f)] public float _bigBubbleScale = 1.0f; // tier-2 signature bubble (gated to 8-11 tiles in MaybeBigPopAndHold)
        [Header("Tier-3 Energy Burst (Candy-Crush style)")]
        [Tooltip("Brightness/scale of the tier-3 burst (white-hot core + light rays + board flash + sparkles). 0 = off.")]
        [Range(0f, 2.5f)] public float _tier3BurstScale = 1.0f;

        // ── FX Bloom Tuning — LIVE IN PLAY. The 1.30 bloom threshold is the line:
        //    a value UNDER it brightens with NO bloom; OVER it glows/blows. Lower
        //    any of these if that effect is washing out. ──────────────────────
        [Header("FX Bloom Tuning (drag during Play — lower = less blow-out)")]
        [Tooltip("Pre-pop tile light-up glint cap. <1.30 = no bloom, >1.30 = glows.")]
        [Range(1.0f, 1.8f)] public float _glintCap = 1.0f; // 1.0 = glint OFF (skip). Spencer-tuned "on" value was 1.14
        [Tooltip("Scored-word flash GREEN peak. ~1.30 = soft glow, higher blows.")]
        [Range(1.0f, 1.8f)] public float _scoredFlashGreen = 1.3f;
        [Tooltip("Primed-glow (magenta tile) pulse cap. Higher = brighter magenta.")]
        [Range(1.0f, 1.8f)] public float _primedGlowCap = 1.35f;
        [Tooltip("Bubble-pop glow-behind HDR (×debris colour). Higher = brighter glow.")]
        [Range(0f, 6f)] public float _bubbleGlowHDR = 2.5f; // 0 = bubble glow-behind OFF
        [Tooltip("Pop aura (square highlight) HDR (×tile colour).")]
        [Range(0f, 6f)] public float _popAuraHDR = 0f; // 0 = square glow OFF (toggle); previous on-value was 3.0
        [Tooltip("Sparkle star brightness (additive — overlaps sum, so keep low).")]
        [Range(0f, 1.4f)] public float _sparkleBright = 0.85f; // 0 = sparkles OFF (black additive = invisible)

        public static float BigPopLeadSeconds => Instance != null ? Instance._bigPopHold : 0.152f;
        public static float BigPopNudge       => Instance != null ? Instance._bigPopNudge : 0f;
        public static float BigPopSuckScale   => Instance != null ? Instance._bigPopSuckScale : 1f;
        public static Ease  BigPopSuckEase    => Instance != null ? Instance._bigPopSuckEase : Ease.InBack;
        public static float BigBubbleScale    => Instance != null ? Instance._bigBubbleScale : 0f;
        public static float Tier3BurstScale   => Instance != null ? Instance._tier3BurstScale : 0f;

        /// <summary>Grid cell (col,row) of the letter the player last dropped or
        /// edited — the move that triggered the word/explosion. The tier-3 burst
        /// centers here so it erupts from your play instead of the cluster centroid.
        /// Null → fall back to the detonating cluster's center.</summary>
        public static Vector2Int? LastTriggerCell;

        public static float GlintCap          => Instance != null ? Instance._glintCap : 1.0f;
        public static float ScoredFlashGreen  => Instance != null ? Instance._scoredFlashGreen : 1.3f;
        public static float PrimedGlowCap     => Instance != null ? Instance._primedGlowCap : 1.35f;
        public static float BubbleGlowHDR     => Instance != null ? Instance._bubbleGlowHDR : 2.5f;
        public static float PopAuraHDR        => Instance != null ? Instance._popAuraHDR : 3.0f;
        public static float SparkleBright     => Instance != null ? Instance._sparkleBright : 0.85f;

        /// <summary>2026-06-03 Spencer: queue a "diffuse pop" on tiles whose primed
        /// word just EXPIRED (extinguished without detonating). Deferred on purpose —
        /// a rising row repaints the board ~immediately after and would DOComplete the
        /// pop away the instant it starts, so we wait until the move + any rise has
        /// fully settled, then pop the tiles at their final positions.</summary>
        public void QueueDiffusePops(List<Tile> tiles)
        {
            if (tiles == null || tiles.Count == 0) return;
            // Mark NOW (before the rebuild's ClearPrimedGlow runs) so each tile keeps its primed look
            // through the deferral instead of reverting early — the pop reverts it at its tail.
            for (int i = 0; i < tiles.Count; i++) tiles[i]?.MarkPendingDiffusePop();
            StartCoroutine(DiffusePopsDeferred(tiles));
        }

        private IEnumerator DiffusePopsDeferred(List<Tile> tiles)
        {
            // Let the resolution + any rising-row shift finish first.
            yield return WaitCache.Get(0.10f);
            float guard = 0f;
            while (guard < 2f &&
                   ((SurvivalManager.Instance != null && SurvivalManager.Instance.IsRisingRow) ||
                    (MatchController.Instance != null && MatchController.Instance.IsProcessing)))
            {
                guard += Time.deltaTime;
                yield return null;
            }
            yield return WaitCache.Get(0.05f);

            int popped = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile t = tiles[i];
                // Pop only if the tile still exists, is live, and actually reverted
                // (not re-primed / detonated / replaced in the meantime).
                if (t != null && t.gameObject.activeSelf && !t.HasPermanentGlow)
                {
                    t.PlayDiffusePop();
                    popped++;
                }
            }
            Debug.Log($"[DiffusePop] deferred fired: requested={tiles.Count} popped={popped}");
        }

        /// <summary>
        /// Tier-3 "Candy-Crush" energy burst — composes the bright additive layers
        /// the look is built from: a white-hot core + radiating light rays + a
        /// Light2D pulse that lights the surrounding candies + a sparkle burst.
        /// All HDR so URP Bloom catches them and they read as luminous energy.
        /// </summary>
        public void PlayTier3Burst(Vector3 center, float spanUnits)
        {
            float b = Tier3BurstScale;
            if (b <= 0.01f) return;
            Debug.Log($"[Tier3Burst] FIRE center={center} span={spanUnits:F2} scale={b:F2} (fb={(FlipbookExplosion.Instance!=null)} light={(LightingSetup.Instance!=null)} parts={(GameParticles.Instance!=null)})");

            // Blue-dominant palette w/ a green accent wisp (HDR so bloom glows it).
            // Sized TIGHT to the cluster so it reads as a burst, not a full-screen
            // wash (rays barely past the cluster, core a bit smaller).
            // HDR pushed above the 1.30 bloom threshold so these bloom hard — but
            // ONLY the dominant channel goes high; the other two stay low so the
            // core stays COLOURED instead of clipping to white. Board untouched.
            Color rayBlue   = new Color(0.25f, 0.9f, 5.5f); // blue streaks (blue-dominant)
            Color coreBlue  = new Color(0.5f, 1.3f, 5.8f);  // blue-white centre
            Color accGreen  = new Color(0.4f, 5.5f, 1.0f);  // green wisp accent

            var fb = FlipbookExplosion.Instance;
            if (fb != null)
            {
                fb.PlayRaysBurst (center, spanUnits * 1.1f, 0.70f, b, rayBlue);  // soft glow — lingers longer
                fb.PlayCoreGlow  (center, spanUnits * 0.6f, 0.24f, b, coreBlue); // tight bright centre
                fb.PlayAccentGlow(center, spanUnits * 0.45f, 0.20f, b, accGreen); // green wisp
            }
            // Light2D pulse (cool blue) — the surrounding candies light up.
            LightingSetup.Instance?.SpawnFlashLight(
                center, new Color(0.55f, 0.8f, 1f), 2.6f * b, spanUnits * 1.1f, 0.28f);
            // Sparkle burst — the signature twinkle stars.
            GameParticles.Instance?.PlayShimmerBurst(center, 16);
        }

        /// <summary>Gold "tier-3 style" energy burst centered on a cracked CHEST — same composition as
        /// PlayTier3Burst (core + rays + flash light + sparkles) but tinted YELLOW/GOLD to match the coin
        /// trail (no green accent). 2026-06-18 Spencer.</summary>
        public void PlayChestCoinBurst(Vector3 center, float spanUnits)
        {
            // Self-contained per-chest gold glow (a FRESH renderer each time) — NOT FlipbookExplosion's
            // shared _tier3Core (which overwrites itself when several chests crack at once → only one glow),
            // and NOT GameParticles.PlayShimmerBurst (those systems are green/cyan). Full colour control:
            // white glow sprite × HDR gold → blooms yellow. 2026-06-18 Spencer.
            EnsureChestGlowAssets();
            if (_chestGlowSprite != null) StartCoroutine(ChestGlowCoroutine(center, spanUnits));
            if (_chestBubbleSprite != null) StartCoroutine(ChestBubblePop(center, spanUnits)); // bubble@2x, tier-1 style
            LightingSetup.Instance?.SpawnFlashLight(center, new Color(1f, 0.8f, 0.3f), 2.6f, spanUnits * 1.6f, 0.28f);
        }

        /// <summary>bubble@2x rendered with the TIER-1 pop feel: a fast OutQuart scale-up + a snap-on then
        /// InCubic fade-out. Additive + HDR gold so it blooms gold. 2026-06-18 Spencer.</summary>
        private IEnumerator ChestBubblePop(Vector3 center, float span)
        {
            var go = new GameObject("ChestBubble");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _chestBubbleSprite;                 // bubble@2x
            if (_chestGlowMat != null) sr.sharedMaterial = _chestGlowMat; // additive → blooms
            sr.sortingOrder = 57;
            go.transform.position = new Vector3(center.x, center.y, -1.6f);
            float spriteWorld = _chestBubbleSprite.bounds.size.x;
            float endScale   = (span * 1.6f) / Mathf.Max(0.01f, spriteWorld);
            float startScale = endScale * 0.30f;
            const float DUR = 0.22f;
            Color gold = new Color(2.6f, 2.0f, 0.6f);       // HDR gold (additive) → blooms

            go.transform.localScale = Vector3.one * startScale;
            sr.color = new Color(gold.r, gold.g, gold.b, 0f);
            // Scale: OutQuart — fast front-loaded expansion (like tier-1's "fast pop").
            go.transform.DOScale(Vector3.one * endScale, DUR).SetEase(DG.Tweening.Ease.OutQuart);
            // Alpha: snap on (~6%) then InCubic fade-out across the rest.
            var seq = DOTween.Sequence();
            seq.Append(DOTween.ToAlpha(() => sr.color, c => sr.color = c, 0.85f, DUR * 0.06f).SetEase(DG.Tweening.Ease.OutQuad));
            seq.Append(DOTween.ToAlpha(() => sr.color, c => sr.color = c, 0f, DUR * 0.94f).SetEase(DG.Tweening.Ease.InCubic));

            yield return new WaitForSeconds(DUR);
            if (go != null) { go.transform.DOKill(); if (sr != null) sr.DOKill(); Destroy(go); }
        }

        private Sprite _chestGlowSprite; private Sprite _chestBubbleSprite; private Material _chestGlowMat; private bool _chestGlowTried;
        private void EnsureChestGlowAssets()
        {
            if (_chestGlowTried) return;
            _chestGlowTried = true;
            // Use the project's CLEAN glow asset (Particles/vfx_glow) — a white soft radial gradient
            // (bright centre, soft falloff, no organic shape). It's white so it tints cleanly to gold.
            // NOT bubble@2x — that's a soap bubble (hard rim + iridescent green edge); the tier-3 only
            // gets away with it at ppu 200 + a tiny scale, but blown up it reads as a bubble. 2026-06-18.
            var tex = Resources.Load<Texture2D>("Particles/glow")        // the glow the tier-3 burst uses
                   ?? Resources.Load<Texture2D>("Particles/vfx_glow")
                   ?? Resources.Load<Texture2D>("Particles/glowfree1");
            if (tex != null)
                _chestGlowSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            var btex = Resources.Load<Texture2D>("Particles/bubble@2x");
            if (btex != null)
                _chestBubbleSprite = Sprite.Create(btex, new Rect(0, 0, btex.width, btex.height), new Vector2(0.5f, 0.5f), 100f);
            var sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Sprites/Default");
            if (sh != null) _chestGlowMat = new Material(sh);
        }

        private IEnumerator ChestGlowCoroutine(Vector3 center, float span)
        {
            var go = new GameObject("ChestGlow");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = _chestGlowSprite;
            if (_chestGlowMat != null) sr.sharedMaterial = _chestGlowMat;
            sr.sortingOrder = 56;
            go.transform.position = new Vector3(center.x, center.y, -1.6f);
            float spriteWorld = _chestGlowSprite.bounds.size.x;
            float maxScale = (span * 2.6f) / Mathf.Max(0.01f, spriteWorld);
            Color gold = new Color(7.5f, 4.2f, 0.1f); // HDR warm gold (red highest, blue ~0) → blooms yellow
            float dur = 0.45f, t = 0f; // lingers longer (Spencer 2026-06-18)
            while (t < dur && go != null)
            {
                t += Time.unscaledDeltaTime;
                float k  = Mathf.Clamp01(t / dur);
                float se = 1f - (1f - k) * (1f - k);                 // ease-out scale
                float s  = Mathf.Lerp(maxScale * 0.35f, maxScale, se);
                go.transform.localScale = new Vector3(s, s, 1f);
                float a = k < 0.22f ? 1f : 1f - ((k - 0.22f) / 0.78f); // hold bright, then fade
                sr.color = new Color(gold.r, gold.g, gold.b, a);
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        /// <summary>
        /// Coroutine form (Option B): on any big explosion (8+ tiles), fire
        /// big_pop and HOLD for BigPopLeadSeconds so the clip's swell plays as
        /// anticipation and its transient lands on the pop. Gate is SIZE-ONLY:
        /// chainDepth is NOT used because the player's deliberate primed-word
        /// detonations chain to depth 2+ yet are exactly the big moments Spencer
        /// wants the boom on. The small gravity follow-ups are naturally excluded
        /// because they're under 8 tiles. PlayBigPop's cooldown keeps a multi-wave
        /// chain from machine-gunning the boom. Callers `yield return` this just
        /// before PlayExplosion.
        /// </summary>
        public static IEnumerator MaybeBigPopAndHold(List<Tile> tiles)
        {
            int totalTiles = tiles != null ? tiles.Count : 0;
            bool fire = totalTiles >= 8;
            Debug.Log($"[BigPop] gate: tiles={totalTiles} → {(fire ? "FIRE+hold" : "skip")}");
            if (!fire) yield break;

            GameAudio.Instance?.PlayBigPop(BigPopNudge);

            float hold = BigPopLeadSeconds;
            float suck = BigPopSuckScale;
            // Visual 'pucker' — suck the cluster inward over the hold so the
            // picture anticipates in lockstep with the audio swell. SetUpdate(true)
            // = unscaled, so it animates even if a hitstop has frozen timeScale.
            // We CAPTURE each tile's rest scale and RESTORE it after the hold —
            // tiles get pooled/reused, so a leftover shrink would compound on
            // every detonation (smaller and smaller each time).
            Vector3[] restScales = null;
            if (suck < 0.999f)
            {
                Ease ease = BigPopSuckEase;
                restScales = new Vector3[tiles.Count];
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    Transform t = tiles[i].transform;
                    restScales[i] = t.localScale;
                    t.DOKill();
                    t.DOScale(t.localScale * suck, hold).SetEase(ease).SetUpdate(true);
                }
            }

            // Big hero bubble at the cluster centre — inflates over the hold and
            // pops on the explosion. TIER 2 ONLY (8-11 tiles): it's tier 2's
            // signature. Tier 3+ (12+ tiles) gets the energy burst instead, so the
            // bubble doesn't leak onto it.
            float bubble = BigBubbleScale;
            if (bubble > 0.01f && totalTiles < 12 && FlipbookExplosion.Instance != null)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                int n = 0;
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    Vector3 p = tiles[i].transform.position;
                    if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                    if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                    n++;
                }
                if (n > 0)
                {
                    Vector3 center = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
                    float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                    float span = Mathf.Max(maxX - minX, maxY - minY) + cell;
                    FlipbookExplosion.Instance.PlayBigBubble(center, span * bubble, hold);
                }
            }

            // Realtime so a concurrent hitstop (timeScale=0) doesn't freeze
            // the swell — the board holds visually while the audio builds.
            yield return new WaitForSecondsRealtime(hold);

            // Restore rest scale so the suck-in NEVER persists. The explosion
            // bursts these tiles right after; any that survive (test reactivation,
            // pooling) come back at full size instead of stuck shrunk.
            if (restScales != null)
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    tiles[i].transform.DOKill();
                    tiles[i].transform.localScale = restScales[i];
                }
            }
        }

        // wordFlash: true for tiles that form a detonating WORD (they get the green cascade-flash);
        // false for pure splash/collateral tiles (they stay white per design). 2026-06-23.
        public Coroutine PlayExplosion(List<Tile> tiles, int chainStep = 0, int wordLength = 3, bool wordFlash = true)
        {
            if (tiles == null || tiles.Count == 0) return null;

            // HiddenWord polish: WHEN A WORD EXPLODES, every detonating letter that matches a still-hidden
            // slot pops + flies up to its blank in the Target panel with a sparkle trail ("escapes the
            // blast"). Fired here (the explode chokepoint) so it triggers on detonation, not on prime.
            // Claiming is reveal-order-independent so each slot flies exactly once. 2026-06-17 Spencer.
            if (ObjectiveManager.Instance != null
                && ObjectiveManager.Instance.Active is HiddenWordObjective hiddenObj
                && HUDManager.Instance != null)
            {
                int flyOrder = 0; // stagger so multiple matched letters launch + land one-by-one (sequentially)
                for (int i = 0; i < tiles.Count; i++)
                {
                    var ft = tiles[i];
                    if (ft == null) continue;
                    int slot = hiddenObj.ClaimSlotForLetter(ft.Letter);
                    if (slot < 0) continue;
                    int capturedSlot = slot;
                    var capturedObj = hiddenObj;
                    // The fly-up REVEALS its slot when it lands (keeps fly + reveal in lockstep, and the
                    // slot fills exactly when the letter arrives). NotifyHiddenReveal refreshes the HUD +
                    // fires stage-clear if the word's now complete.
                    HUDManager.Instance.FlyHiddenLetterToSlot(ft.transform.position, ft.Letter, slot, () =>
                    {
                        bool wasComplete = capturedObj.IsComplete;
                        capturedObj.RevealSlot(capturedSlot);
                        ObjectiveManager.Instance?.NotifyHiddenReveal(wasComplete);
                    }, flyOrder * 0.35f);
                    flyOrder++;
                }
            }

            // Vault REWARD coins: when a CHEST detonates, bank its tier coins and spit a coin burst that
            // scatters then gathers to the REWARD counter. SAME chokepoint as the HiddenWord fly-up —
            // player detonations route through PlayExplosion, NOT the live GameVisualBridge Exploding
            // phase. 2026-06-18 Spencer.
            if (ObjectiveManager.Instance != null
                && ObjectiveManager.Instance.Active is VaultObjective vaultObj
                && HUDManager.Instance != null)
            {
                int vaultsHit = 0;
                float span = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                for (int i = 0; i < tiles.Count; i++)
                {
                    var ft = tiles[i];
                    if (ft == null || !ft.IsVault) continue;
                    vaultsHit++;
                    int coins = VaultObjective.CoinsForTier(ft.VaultRequiredLen);
                    vaultObj.AddRewardCoins(coins);                                  // bank now (data truth)
                    HUDManager.Instance.SpawnRewardCoinBurst(ft.transform.position, coins, ft.VaultRequiredLen);
                    // Tier-2 hero bubble + a layered GOLD GLOW behind it. Coins unchanged. 2026-06-18 Spencer.
                    float t2scale = Mathf.Max(0.5f, BigBubbleScale);
                    FlipbookExplosion.Instance?.PlayBigBubble(ft.transform.position, span * 3f * t2scale, 0.20f);
                    EnsureChestGlowAssets();
                    if (_chestGlowSprite != null) StartCoroutine(ChestGlowCoroutine(ft.transform.position, span * 1.4f)); // gold glow
                }
                if (vaultsHit > 0)
                {
                    GameAudio.Instance?.PlayBigPop();          // tier-2 boom
                    GameAudio.Instance?.PlayCoinExplodeBlip(); // 0.232s jackpot coin "ding"
                    GridManager.Instance?.ShakeBoard(0.09f, 0.18f); // subtle board-tile shake (not the hand rack)
                }
            }

            return StartCoroutine(ExplosionCoroutineTracked(tiles, chainStep, wordLength, wordFlash));
        }

        // Quick green pulse for cascade words — short, simultaneous (no stagger), so it
        // reads as a snappy "scored!" beat right before the boom, not the slower full
        // PlayWordScored flash the player's dropped word gets.
        private const float CASCADE_FLASH_BEAT = 0.08f; // how long the green shows before the explosion (kept short to keep chains fast)

        /// <summary>Flat scored-green on cascade/detonating word tiles (the plain SetScoredSprite tint —
        /// pool-reset-safe, no bloom/glow/decay). Reverted from the brighter glow-flash version per Spencer
        /// 2026-06-23. Splash tiles are never passed here, so green only ever marks a formed word.</summary>
        private void FlashCascadeGreen(List<Tile> tiles)
        {
            for (int i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                if (tile == null) continue;
                if (!tile.IsShowingScoredSprite) tile.SetScoredSprite(true);
                // Saturated ADDITIVE green glow on top of the flat scored tint so the cascade flash
                // actually reads (the flat green alone was too subtle). forceDesktop → shows in the
                // editor too; blooms on device. Cleared on pool via ResetForPool→ClearBloomGlow.
                tile.SetBloomGlow(Tile.SCORED_GLOW_HDR, 0.9f, forceDesktop: true);
            }
        }

        private IEnumerator ExplosionCoroutineTracked(List<Tile> tiles, int chainStep, int wordLength, bool wordFlash = true)
        {
            // CASCADE GREEN (consistency fix 2026-06-23): the player's word greens via WordsScored→
            // PlayWordScored; gravity-formed CASCADE/rise words don't, so they used to pop white. This is
            // the single explode chokepoint, so greening WORD calls here makes every word read the same.
            // wordFlash is the caller's word-vs-splash verdict (GVB sends word tiles with wordFlash:true and
            // splash with wordFlash:false), so splash never greens. Skip tiles already scored (no double-set).
            if (wordFlash)
            {
                List<Tile> cascadeFlash = null;
                for (int i = 0; i < tiles.Count; i++)
                {
                    var t = tiles[i];
                    // Flash word tiles. Word-vs-splash is decided by the CALLER via wordFlash (callers split
                    // dying tiles into a word call [wordFlash:true] and a splash call [wordFlash:false] using
                    // the step's word-cell set) — NOT by primed state, because cascade words detonate unprimed.
                    // Skip the player's own word (already scored-green) so it isn't double-set. 2026-06-23.
                    if (t != null && !t.IsShowingScoredSprite)
                        (cascadeFlash ??= new List<Tile>()).Add(t);
                }
                if (cascadeFlash != null && cascadeFlash.Count > 0)
                {
                    FlashCascadeGreen(cascadeFlash);
                    yield return WaitCache.Get(CASCADE_FLASH_BEAT); // let the green read before the boom
                }
            }

            ActiveExplosions++;
            // Wrap in try/finally semantics via two yield phases — Unity
            // doesn't support try/finally with yield, so we structure as
            // "delegate then always decrement." If the inner coroutine throws,
            // Unity logs and stops it; the outer never gets to decrement.
            // For belt-and-suspenders, a max-age safety would reset the count,
            // but for now this matches the lifetime of ExplosionCoroutine.
            yield return StartCoroutine(ExplosionCoroutine(tiles, chainStep, wordLength));
            ActiveExplosions = Mathf.Max(0, ActiveExplosions - 1);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // TIER-1 CANDY-CRUSH-STYLE POP — single-tile orchestrated burst
        // (squeeze → bubble + tile fade → fragments → sparkle → cleanup).
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Side-channel for the just-scored words — set by upstream WordsScored
        /// handlers (GameVisualBridge / HandManager). Each list is ONE word's
        /// tiles (e.g. one entry for "GAL", another for "DIM", etc.).
        /// ExplosionCoroutine reads this in the cascade branch so:
        ///   - The pink-primed preamble only applies to trigger word tiles
        ///     (not collateral row/column tiles caught in the blast).
        ///   - BigBurstFlash fires ONCE PER WORD, each centered on that
        ///     specific word's tile cluster — not one mega-burst at the
        ///     centroid of all detonating tiles.
        /// Cleared after consumption.
        /// </summary>
        public static List<List<Tile>> _pendingCascadeWords;

        /// <summary>
        /// Plays the new Candy-Crush-style "tier 1 pop" orchestrated sequence
        /// on a single tile. Each step null-checks its prerequisite and
        /// continues on missing instances. Hides the tile at the end —
        /// caller (test menu / future gameplay wire-in) is responsible for
        /// restoring or destroying it.
        /// </summary>
        /// <summary>
        /// Per-word haptic for tier-1 detonations. Fires at t=120ms+offset
        /// (synced to fragment-fire / shatter peak in Tier1PopCoroutine).
        /// Offset accounts for cascade preamble (~0.20s) so the haptic still
        /// lands at the shatter, not 200ms before it. Escalates by chainStep:
        ///   chainStep 0 → Light
        ///   chainStep 1+ → Medium
        /// </summary>
        private IEnumerator Tier1PopHaptic(int chainStep, float offset = 0f)
        {
            yield return WaitCache.Get(0.12f + offset);
            // Initial word pop = WordScored; cascade pop = CascadePop. Both
            // currently map to the same Emphasis values (0.45/0.55) since
            // audio pitch escalation carries the chain feel, not the haptic.
            if (chainStep >= 1) HapticsManager.CascadePop();
            else                HapticsManager.WordScored();
        }

        /// <summary>
        /// Fires the matchline pop SFX after a delay. Used by the cascade
        /// path so the audio syncs with the squeeze (which starts after the
        /// pink-color preamble), not with the start of the preamble.
        /// </summary>
        private IEnumerator DelayedMatchLine(float delay)
        {
            yield return WaitCache.Get(delay);
            GameAudio.Instance?.PlayMatchLine();
        }

        /// <summary>
        /// Fires ONE BigBurstFlash sweep beam PER WORD in the cascade — each
        /// centered on its own tile cluster and oriented by that word's
        /// bounding box (vertical if y-range > x-range). When 3-4 words
        /// detonate together (e.g. cascade chain), 3-4 beams fire in sync,
        /// each correctly aligned with its own word. Replaces the previous
        /// single-beam-at-cluster-centroid logic which mis-aligned beams
        /// across multi-word detonations.
        /// </summary>
        private IEnumerator DelayedCascadeBurstPerWord(List<List<Tile>> cascadeWords, int chainStep, float delay)
        {
            yield return WaitCache.Get(delay);
            if (BigBurstFlash.Instance == null || cascadeWords == null) yield break;

            Camera cam = Camera.main;
            float halfH = cam != null ? cam.orthographicSize : 5f;
            float halfW = cam != null ? halfH * cam.aspect : 9f;
            float thickness = 1.0f;
            Color tint = chainStep >= 3
                ? new Color(1.4f, 0.7f, 0.2f, 1f)
                : new Color(1.2f, 0.95f, 0.5f, 1f);

            for (int wi = 0; wi < cascadeWords.Count; wi++)
            {
                var wordTiles = cascadeWords[wi];
                if (wordTiles == null || wordTiles.Count == 0) continue;

                Vector3 minPos = Vector3.zero;
                Vector3 maxPos = Vector3.zero;
                Vector3 sumPos = Vector3.zero;
                int liveCount = 0;
                for (int ti = 0; ti < wordTiles.Count; ti++)
                {
                    if (wordTiles[ti] == null) continue;
                    Vector3 p = wordTiles[ti].transform.position;
                    if (liveCount == 0) { minPos = p; maxPos = p; }
                    else
                    {
                        if (p.x < minPos.x) minPos.x = p.x;
                        if (p.y < minPos.y) minPos.y = p.y;
                        if (p.x > maxPos.x) maxPos.x = p.x;
                        if (p.y > maxPos.y) maxPos.y = p.y;
                    }
                    sumPos += p;
                    liveCount++;
                }
                if (liveCount == 0) continue;

                Vector3 center = sumPos / liveCount;
                bool vertical = (maxPos.y - minPos.y) > (maxPos.x - minPos.x);
                float length = vertical ? halfH * 2.2f : halfW * 2.2f;
                BigBurstFlash.Instance.Play(center, length, thickness, vertical, tint);
            }
        }

        /// <summary>
        /// Fires the detonation boom SFX after a delay. Used by the cascade
        /// pop path so the bigger boom syncs with the shatter peak instead
        /// of leading the visual.
        /// </summary>
        private IEnumerator DelayedDetonationAudio(int chainDepth, float delay)
        {
            yield return WaitCache.Get(delay);
            GameAudio.Instance?.PlayDetonation(chainDepth);
        }

        public Coroutine PlayTier1Pop(Tile tile) => PlayTier1Pop(tile, suppressAudio: false, isCascade: false, startDelay: 0f);

        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio) => PlayTier1Pop(tile, suppressAudio, isCascade: false, startDelay: 0f);

        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio, bool isCascade) => PlayTier1Pop(tile, suppressAudio, isCascade, startDelay: 0f);

        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio, bool isCascade, float startDelay)
            => PlayTier1Pop(tile, suppressAudio, isCascade, startDelay, instantPop: false);

        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio, bool isCascade, float startDelay, bool instantPop)
        {
            if (tile == null) return null;
            // Route through revert toggle. Legacy = 2026-05-03 squeeze→fade.
            // New = 2026-05-04 CC-reference shrink-to-nothing.
            if (FX_UseLegacyTier1Pop)
                return StartCoroutine(Tier1PopCoroutine_Legacy20260504(tile, suppressAudio, isCascade, startDelay));
            return StartCoroutine(Tier1PopCoroutine(tile, suppressAudio, isCascade, startDelay, instantPop));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // LEGACY Tier1Pop (2026-05-03) — squeeze 0.85× then alpha fade.
        // Kept verbatim behind FX_UseLegacyTier1Pop for one-flip revert. Do NOT
        // edit this method — it's the rollback target. If both implementations
        // need parallel changes, edit only Tier1PopCoroutine and accept the
        // legacy frozen behavior on revert.
        // ═══════════════════════════════════════════════════════════════════════════
        private IEnumerator Tier1PopCoroutine_Legacy20260504(Tile tile, bool suppressAudio, bool isCascade, float startDelay)
        {
            if (tile == null) yield break;
            // Start delay — used by cascade detonations on the non-trigger
            // tiles ("collateral" tiles in the same row/column as the trigger
            // word) so they shatter synced with the trigger word's pop after
            // its preamble, instead of exploding 200ms early.
            if (startDelay > 0f)
            {
                yield return WaitCache.Get(startDelay);
                if (tile == null) yield break;
            }

            Transform xform = tile.transform;
            Vector3 origScale = xform.localScale;
            Vector3 tilePos = xform.position;
            SpriteRenderer tileSR = tile.GetComponent<SpriteRenderer>();
            Color tileColor = tileSR != null ? tileSR.color : Color.white;
            // Capture the tile's CURRENT sprite before any state clears
            // (ClearPrimedGlow swaps the sprite to s_spriteNormal at
            // Tile.cs:1294). Restored right before TileFragments.Shatter so
            // each shrapnel fragment renders the original primed-pink-coral
            // / scored-green / etc. texture, not the cream fallback.
            Sprite originalSprite = tileSR != null ? tileSR.sprite : null;

            // ANIMATION RHYTHM — Candy-Crush squeeze→punch→shatter cycle:
            //   t=0–50ms     squeeze (anticipation):  origScale → 0.85× (OutQuad)
            //   t=50–120ms   punch (release):         0.85× → 1.15× (OutBack overshoot)
            //   t=120ms      shatter peak:            fragments fire from punched-up tile
            //   t=120–200ms  collapse:                tile DOFade with InCubic ease
            //
            // OutBack on the pop-up is the key gesture — anticipation → release
            // with overshoot is what gives CC its "snap." All layers use easing,
            // no linear lerps anywhere in the chain.

            // t=0 — squeeze + matchline SFX + overlay square halo. Tile scale
            // sequence runs as a DOTween chain so the punch-up follows the
            // squeeze with no gap.
            if (!suppressAudio) GameAudio.Instance?.PlayMatchLine();
            // ── Lock the tile's color for the pop sequence ──
            // FOUR things can actively write sr.color/scale every frame:
            //   - Primed pulse coroutine (Tile.cs:1006) — color + scale
            //   - Gold pulse coroutine (Tile.cs:829) — color (Lerp to GOLD_HIGH)
            //   - Flash coroutine (Tile.cs:1131) — color toward settleColor
            //   - DOTween tweens on the SpriteRenderer (e.g. PlayWordScored
            //     at WordDropFX.cs:201-205 starts a 0.15s color tween that's
            //     still in flight when the explosion fires shortly after)
            // Without halting all four, our preserved-color write gets
            // overwritten on the next frame (primed pink → gold pulse takes
            // over → reads as orange; player-flash green → settle to white).
            //
            // Order:
            //   1. Capture preserved color. Primed tiles → tile.GlowColor
            //      (saturated _primedGlowColor, not the lerped snapshot).
            //   2. Stop primed pulse via ClearPrimedGlow (also resets state).
            //      Stop gold pulse + flash coroutine via StopVisualPulses.
            //      Kill any DOTween tweens on transform AND sr.
            //   3. Lock sr.color to preservedColor. From here only DOFade
            //      (alpha-only) modifies the color until shatter.
            // Two distinct colors needed for primed tiles:
            //   - preservedColor (tile display during squeeze→pop): pulse-mid
            //     lerp matches what the player saw mid-pulse on the tile.
            //   - debrisColor (TileFragments tint at t=120ms): texture color
            //     sampled from primed_test@2x.png. Per Spencer, debris should
            //     match the natural pre-pop tile color, NOT the lighter
            //     scaled-down color visible during the pop animation.
            Color preservedColor = tileColor;
            Color debrisColor    = tileColor;
            if (isCascade)
            {
                // Gravity-formed cascade word — force the primed pink visual
                // (sprite + tint) regardless of any other tile state. The
                // preamble below will pulse through pink → flash → squeeze.
                preservedColor = Tile.PRIMED_TILE_TINT;
                debrisColor    = Tile.PRIMED_TILE_TINT;
                if (tileSR != null && Tile.PrimedSprite != null)
                {
                    tileSR.sprite = Tile.PrimedSprite;
                    originalSprite = Tile.PrimedSprite;
                }
            }
            else if (tile.HasPermanentGlow)
            {
                Color glow = tile.GlowColor;
                glow.a = 1f;
                preservedColor = Color.Lerp(Color.white, glow, 0.40f);
                debrisColor    = Tile.PRIMED_TILE_TINT;
            }
            else if (tile.WasInScoredWord)
            {
                // Sticky WasInScoredWord catches early-stagger tiles whose
                // PlayWordScored OnComplete already reverted the sprite back
                // to normal before detonation kicked in. Re-apply the scored
                // sprite so the green texture is under the debris tint.
                preservedColor = Tile.SCORED_TILE_TINT; // squeeze visual = mid-green
                // 2026-05-30: debris tint = white (was SCORED_TILE_TINT). The
                // green sprite already has bright kelly green baked in; the
                // averaged SCORED_TILE_TINT was < 1 on all channels and
                // multiplying with the sprite DIMMED the green. White tint
                // lets the sprite's natural saturated color show through.
                debrisColor    = Color.white;
                if (!tile.IsShowingScoredSprite) tile.SetScoredSprite(true);
                // Force originalSprite to the scored sprite so the pre-shatter
                // restore (line 757) re-applies the green texture even if some
                // OnComplete callback reverted it to white_tile2 mid-animation.
                originalSprite = Tile.ScoredSprite;
            }
            if (tile.HasPermanentGlow) tile.ClearPrimedGlow();
            tile.StopVisualPulses();
            xform.DOKill();
            if (tileSR != null) tileSR.DOKill();
            if (tileSR != null) tileSR.color = preservedColor;
            tileColor = debrisColor; // downstream Shatter tints debris to debrisColor (texture color for primed, HDR green for scored)

            // ── Cascade preamble ──────────────────────────────────────────────
            // For gravity-formed cascade words: hold on the primed pink color
            // for ~120ms, then a brief brightened-pink flash (NOT pure white,
            // which blows the pink out via bloom), then back to base pink for
            // the squeeze. Visual-only — no SetPrimedGlow / engine state.
            if (isCascade && tileSR != null)
            {
                yield return WaitCache.Get(0.12f);
                if (tile == null) yield break;
                // Brighter pink — multiply each channel by 1.25 so bloom catches
                // the pulse without washing the color out to white.
                tileSR.color = new Color(
                    Mathf.Min(preservedColor.r * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.g * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.b * 1.2f, GlintCap),
                    1f); // de-blown 2026-06-03: clamp 1.5→1.28 (under the bloom line) so the cascade pre-pop glint doesn't white-out
                yield return WaitCache.Get(0.08f);
                if (tile == null) yield break;
                tileSR.color = preservedColor; // back to base pink for the squeeze
            }

            // Squeeze ONLY — tile shrinks to 0.85× and holds there until the
            // fragments fire at t=120ms. No scale-up / overshoot. Mimics CC's
            // pattern where the candy shrinks, then explodes.
            xform.DOScale(origScale * 0.85f, 0.05f).SetEase(Ease.OutQuad);
            if (FlipbookExplosion.Instance != null)
            {
                float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                Color squareTint = new Color(
                    tileColor.r * PopAuraHDR,   // faint — soft highlight tint, only just touches bloom; was 6.0
                    tileColor.g * PopAuraHDR,
                    tileColor.b * PopAuraHDR,
                    1f);
                FlipbookExplosion.Instance.PlayPopOverlaySquare(tilePos, cellSize, 0.20f, squareTint);
            }

            // t=50ms — bubble fires at the punch start so its overshoot
            // syncs with the tile pop-up.
            yield return WaitCache.Get(0.05f);
            if (tile == null) yield break;
            if (FlipbookExplosion.Instance != null)
                FlipbookExplosion.Instance.PlayBubble(tilePos, tileColor, 1.0f, 0.12f);

            // t=120ms — punch peak: fragments fire from the punched-up tile,
            // tile fades with InCubic so it lingers visible during the shatter
            // then accelerates to zero (no flat linear fade).
            yield return WaitCache.Get(0.07f); // 50 + 70 = 120
            if (tile == null) yield break;
            // Lock sr.color AND sr.sprite to their pre-pop captured values
            // before TileFragments.Shatter samples them. Without these writes:
            //   - FlashBorderCoroutine has had ~120ms to drift sr.color back
            //     toward white → debris tints white.
            //   - ClearPrimedGlow / SetScoredSprite(false) callbacks may have
            //     swapped sr.sprite back to the cream s_spriteNormal → Path-B
            //     fragments render the cream texture instead of the primed
            //     pink-coral / scored green that the player saw.
            if (tileSR != null)
            {
                tileSR.color = tileColor;
                if (originalSprite != null) tileSR.sprite = originalSprite;
            }
            if (TileFragments.Instance != null)
                TileFragments.Instance.Shatter(tile, 1.2f);
            if (tileSR != null)
                tileSR.DOFade(0f, 0.08f).SetEase(Ease.InCubic);

            // t=140ms — sparkle spray on top of the chunks
            yield return WaitCache.Get(0.02f); // 120 + 20 = 140
            if (tile == null) yield break;
            if (SparkleSpray.Instance != null)
                SparkleSpray.Instance.Play(tile.transform.position, intensity: 0.25f); // fewer sparkles per pop tile (was 0.4) — less overlap blow-out

            // t=200ms — tile fade complete; hide it BEFORE the caller's
            // grid.RemoveTiles + gravity runs (caller picks up at t=250ms).
            // Otherwise newly-dropped tiles render on top of the still-active
            // popping tile. Bubble/fragments/sparkles continue independently
            // — the visual carries from those layers, not from the tile.
            yield return WaitCache.Get(0.06f); // 140 + 60 = 200
            if (tile == null) yield break;
            tile.gameObject.SetActive(false);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // NEW Tier1Pop (2026-05-04) — CC-reference "shrink to nothing" gesture
        // ═══════════════════════════════════════════════════════════════════════════
        // Reference: Spencer's CC capture frames showed:
        //   1. Tile lights up (bright glint) BEFORE any movement
        //   2. Tile scales down continuously to ~10% (not held at 0.85×)
        //   3. Bubble appears and scales up while tile is in late shrink
        //   4. Shrapnel + sparkles fire INSIDE the bubble at peak
        //
        // Timeline (~280ms total, non-cascade):
        //   t=0      capture color/sprite, brighten color × 1.5 (light-up flash)
        //   t=80     PlayMatchLine, color reverts, scale tween starts
        //   t=80-260 scale 1.0 → 0.10 over 180ms (Ease.InCubic)
        //   t=140    bubble@2x spawns (OutBack, peak alpha 0.45)
        //   t=260    tile.SetActive(false), TileFragments.Shatter, SparkleSpray
        //
        // Cascade-mode prepends the existing 200ms pink-pulse preamble before
        // the light-up phase. All color/sprite/HasPermanentGlow/WasInScoredWord
        // preservation is identical to the legacy coroutine.
        // ═══════════════════════════════════════════════════════════════════════════
        private IEnumerator Tier1PopCoroutine(Tile tile, bool suppressAudio, bool isCascade, float startDelay, bool instantPop = false)
        {
            if (tile == null) yield break;
            if (startDelay > 0f)
            {
                yield return WaitCache.Get(startDelay);
                if (tile == null) yield break;
            }

            Transform xform = tile.transform;
            Vector3 origScale = xform.localScale;
            Vector3 tilePos = xform.position;
            SpriteRenderer tileSR = tile.GetComponent<SpriteRenderer>();
            Color tileColor = tileSR != null ? tileSR.color : Color.white;
            Sprite originalSprite = tileSR != null ? tileSR.sprite : null;
            // 2026-06-03 Spencer's fix: a PRIMED tile already runs a pulse coroutine
            // that drives its EXACT colour every frame. The old code cleared that
            // pulse and tried to repaint the colour — which wiped the tile to white
            // and never matched. Instead, LEAVE THE PULSE RUNNING through the shrink
            // (colour stays perfect, no repaint) and only suspend its scale write so
            // our DOScale shrink wins. Scored/normal tiles have no pulse → old path.
            bool keepPulse = tile.HasPermanentGlow;

            // ── Color/sprite preservation (identical to legacy) ──
            Color preservedColor = tileColor;
            Color debrisColor    = tileColor;
            if (isCascade && tile.WasInScoredWord)
            {
                // 2026-06-08 Spencer: a cascade word IS a scored word — show the GREEN
                // scored flash, THEN pop, instead of forcing the magenta primed tint
                // (the long-standing "cascade pops don't show green" bug). Mirrors the
                // non-cascade scored branch below. The primed pulse already holds off
                // while Time.time < _scoredFlashUntil (PrimedPulseLoop), so we extend
                // that hold across the pop window and the kept pulse won't stomp green.
                preservedColor = Tile.SCORED_TILE_TINT;
                debrisColor    = Color.white;
                if (!tile.IsShowingScoredSprite) tile.SetScoredSprite(true);
                originalSprite = Tile.ScoredSprite;
                tile.HoldPrimedVisual(0.30f);
            }
            else if (isCascade)
            {
                // Collateral cascade tile (in the trigger row/col but not the scored
                // word itself) → magenta primed pop, as before.
                preservedColor = Tile.PRIMED_TILE_TINT;
                debrisColor    = Tile.PRIMED_TILE_TINT;
                if (tileSR != null && Tile.PrimedSprite != null)
                {
                    tileSR.sprite = Tile.PrimedSprite;
                    originalSprite = Tile.PrimedSprite;
                }
            }
            else if (tile.HasPermanentGlow)
            {
                Color glow = tile.GlowColor;
                glow.a = 1f;
                preservedColor = Color.Lerp(Color.white, glow, 0.40f);
                debrisColor    = Tile.PRIMED_TILE_TINT;
            }
            else if (tile.WasInScoredWord)
            {
                preservedColor = Tile.SCORED_TILE_TINT;
                debrisColor    = Color.white; // 2026-05-30: was SCORED_TILE_TINT — see line ~688 rationale.
                if (!tile.IsShowingScoredSprite) tile.SetScoredSprite(true);
                originalSprite = Tile.ScoredSprite;
            }
            // De-bloom the WHOLE pop at the source: cap preservedColor (the colour
            // the tile holds through the pop) just under the 1.30 bloom line, so a
            // primed/scored tile keeps its hue but NEVER glows during a tier-1 pop.
            // Covers the light-up set (line ~1063) AND the shrink set in one place.
            {
                float mxp = Mathf.Max(preservedColor.r, Mathf.Max(preservedColor.g, preservedColor.b));
                if (mxp > 1.29f) { float k = 1.29f / mxp; preservedColor.r *= k; preservedColor.g *= k; preservedColor.b *= k; }
            }
            if (keepPulse)
            {
                // Leave the primed pulse running (keeps the colour exact); just
                // suspend its scale write so the DOScale shrink below isn't fought.
                // ClearPrimedGlow is deferred to the very end (after shatter).
                tile.SetExternalScaleControl(true);
            }
            else
            {
                // Scored / normal tile — no primed pulse to ride. Old behaviour:
                // stop pulses and repaint the held colour.
                tile.StopVisualPulses();
                if (tileSR != null) tileSR.color = preservedColor;
            }
            xform.DOKill();
            if (tileSR != null) tileSR.DOKill();
            tileColor = debrisColor;

            // ── Cascade preamble (unchanged from legacy) ──
            // Pink pulse before the light-up + shrink kicks in. Cascade words
            // get a longer pre-pop window so the player sees them prime up.
            if (isCascade && tileSR != null)
            {
                // 2026-06-08 Spencer: pre-pop pause HALVED (0.12+0.08 → 0.06+0.04) for
                // snappier cascades while still showing a brief light-up beat.
                yield return WaitCache.Get(0.06f);
                if (tile == null) yield break;
                tileSR.color = new Color(
                    Mathf.Min(preservedColor.r * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.g * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.b * 1.2f, GlintCap),
                    1f); // de-blown 2026-06-03: clamp 1.5→1.28 (under the bloom line) so the cascade pre-pop glint doesn't white-out
                yield return WaitCache.Get(0.04f);
                if (tile == null) yield break;
                tileSR.color = preservedColor;
            }

            // ── t=0–80ms: LIGHT-UP brighten (no scale change) ──
            // Brighten the tile's tint to read as a "glint" before the shrink.
            // Cascade preamble already brightened so this layer is visually
            // redundant during cascades; non-cascade pops get the full glint.
            // instantPop (2026-05-15): cascade tiles skip the light-up entirely
            // so they pop IMMEDIATELY on impact with no held moment.
            // ── A/B TEST (all-off) glint — OFF ──
            // if (!isCascade && !instantPop && tileSR != null && GlintCap > 1.0f)
            // {
            //     tileSR.color = new Color(
            //         Mathf.Min(preservedColor.r * 1.2f, GlintCap),
            //         Mathf.Min(preservedColor.g * 1.2f, GlintCap),
            //         Mathf.Min(preservedColor.b * 1.2f, GlintCap),
            //         1f);
            // }

            // Subtle overlay aura during the light-up phase — same as legacy
            // but at a slightly lower alpha so the tile glint reads first.
            // 2026-06-03 Spencer: dropped the `!instantPop` gate so the pop-aura
            // glow layer ALSO fires on cascade pops (it was skipping them).
            // PopAuraHDR == 0 fully disables it (toggle off — doesn't even spawn).
            if (PopAuraHDR > 0.01f && FlipbookExplosion.Instance != null)
            {
                float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                Color squareTint = new Color(
                    tileColor.r * PopAuraHDR,   // faint — soft highlight tint, only just touches bloom; was 6.0
                    tileColor.g * PopAuraHDR,
                    tileColor.b * PopAuraHDR,
                    1f);
                FlipbookExplosion.Instance.PlayPopOverlaySquare(tilePos, cellSize, 0.26f, squareTint);
            }

            // Light-up phase wait — gated on instantPop so cascades pop
            // immediately without the 80ms "held moment" feel.
            if (!instantPop)
            {
                yield return WaitCache.Get(0.08f);
                if (tile == null) yield break;
            }

            // ── t=80ms: pop sound + revert color + start continuous shrink ──
            // OutQuart for the shrink — fast at start, settles slow at end.
            // Math: at 60ms into the 180ms tween (when bubble fires), OutQuart
            // puts the tile at ~28% scale, which matches the bubble's
            // startScale (0.30×cellSize) so the bubble appears at the same
            // size as the now-shrunk tile and expands from there.
            // InCubic was wrong here — it kept the tile at ~97% scale at the
            // bubble entry frame, so the bubble appeared underneath a still-
            // full-sized tile (Spencer's Image #75 reference).
            if (!suppressAudio) GameAudio.Instance?.PlayMatchLine();
            if (!keepPulse && tileSR != null)
            {
                // Scored/normal: hold the colour as it shrinks, capped just under the
                // 1.30 bloom line so it doesn't glow. Primed tiles SKIP this — their
                // pulse is still driving the colour every frame.
                Color pc = preservedColor;
                float mx = Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b));
                if (mx > 1.29f) { float k = 1.29f / mx; pc.r *= k; pc.g *= k; pc.b *= k; }
                tileSR.color = pc;
            }
            xform.DOScale(origScale * 0.10f, 0.18f).SetEase(Ease.OutQuart);

            // ── t=140ms: tile is at ~28% scale (smallest readable point) ──
            // This is the "explode" moment. Tile hides, bubble appears at the
            // same small size, debris fires, sparks fire — all at once. From
            // here on, bubble expands + debris flies out + sparks twinkle in
            // parallel. Matches CC where the candy "becomes" the bubble while
            // shrapnel and sparkles burst out simultaneously.
            yield return WaitCache.Get(0.06f);
            if (tile == null) yield break;

            // Lock sr.color + sr.sprite + scale + position before Shatter samples.
            // Drift from background coroutines or a stale/pooled transform could
            // otherwise tint debris white, revert sprite to cream, undersize
            // fragments (since tile is currently at ~28% scale), or misposition
            // them. Tile hides on the next line so these writes are invisible.
            if (tileSR != null)
            {
                // Lock the debris colour for scored/normal tiles. Primed tiles SKIP
                // this — the running pulse already holds the right colour, so the
                // shatter samples the live primed colour (not the HDR debris tint).
                if (!keepPulse) tileSR.color = tileColor;
                if (originalSprite != null) tileSR.sprite = originalSprite;
            }
            xform.DOKill();
            xform.localScale = origScale;
            xform.position = tilePos;

            if (FlipbookExplosion.Instance != null)
                FlipbookExplosion.Instance.PlayBubble(tilePos, tileColor, 1.0f, 0.12f);
            if (TileFragments.Instance != null)
                TileFragments.Instance.Shatter(tile, 1.2f);
            if (SparkleSpray.Instance != null)
                SparkleSpray.Instance.Play(tilePos, intensity: 0.25f); // fewer sparkles per pop tile (was 0.4) — less overlap blow-out

            if (keepPulse)
            {
                // Tile detonated — NOW end the primed state (stops the pulse) and
                // release external scale control. The tile hides on the next line so
                // ClearPrimedGlow's white-reset (if any) is invisible. Shatter above
                // already sampled the live primed colour.
                tile.SetExternalScaleControl(false);
                tile.ClearPrimedGlow();
            }
            tile.gameObject.SetActive(false);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FX LAYER TOGGLES (Phase 11j tuning — flip to A/B individual layers)
        // ═══════════════════════════════════════════════════════════════════════════
        // BASELINE: all OFF. Flip ONE to true at a time, recompile (Unity hot-
        // reloads in 2-3s), playtest, observe. If something fires while every
        // toggle is false, that's an ungated leak — grep debug_log.txt for
        // [FX] entries to find which layer fired anyway. Each gate logs FIRED
        // or SKIPPED so the trace is in the log even when visuals are absent.
        public static bool FX_MeltdownPrefab    = true;  // AllIn1 Magic Explosive Spell on each tile (meltdown only)
        public static bool FX_TileFlash         = false; // white/yellow flash on each tile pre-dissolve
        public static bool FX_DetonationAudio   = false; // tiered detonation SFX (PlayDetonation)
        public static bool FX_FlipbookGlow      = false; // bubble@2x scale-up halo per tile
        public static bool FX_MeltdownBubble    = false; // TEMP off — meltdown per-tile purple bubbles, so VFX_Glow shows clean (was on)
        public static bool FX_TileFragments     = false; // shattered tile pieces per tile
        public static bool FX_SparkleParticles  = false; // PlayPrimed + PlayWordScored sparkles (tier 2+)
        public static bool FX_BoardShake        = false; // camera shake + hand-card shake + neighbor ripple
        public static bool FX_Haptics           = false; // phone vibration
        public static bool FX_BigBurstFlash     = false; // 2026-05-16: globally disabled per Spencer. Detonations are pop-only now. Flip true to restore sweep beam.
        public static bool FX_BigBurstFlashCascadeTest = false; // test override — force-fires the meltdown beam during a cascade simulation, set by FXTestMenu
        public static bool FX_FuseTrace         = false; // 2026-05-19: globally disabled per Spencer. Orange chain-connection lines were added by a past session without his consent. Flip true to restore.
        public static bool FX_TileFlashBox      = false; // bright box overlay per tile (HandManager.FireTileFlashBoxes)
        public static bool FX_SparkleSpray      = false; // HDR sparkle stars from FirePerWordBurst (HandManager)
        public static bool FX_SparkleLine       = false; // GameParticles.PlaySparkleLine — flare_star sparkles along blast line
        public static bool FX_MeltdownIntroFlash = true;  // MeltdownManager IntroCoroutine white screen flash + title slam visuals

        // Tier1Pop revert toggle (2026-05-04). New default uses the CC-reference
        // shrink-to-nothing gesture: light-up brighten → continuous scale 1.0→0.10
        // (InCubic) → bubble overlap on the late shrink → shatter inside bubble.
        // Flip to TRUE to fall back to the legacy 2026-05-03 coroutine
        // (squeeze 0.85× then alpha fade) if the new behavior breaks something.
        public static bool FX_UseLegacyTier1Pop = false;
        public static bool FX_TileHeatOverlay   = false; // AllIn1 charge-up swirl overlay on each detonating tile during meltdown wind-up
        public static bool FX_PrimedGlowOrb     = false; // soft-circle round halo overlay on each detonating tile during meltdown wind-up (Candy-Crush color-bomb style)
        public static bool FX_MeltdownTilePunch = true;  // tile shrink-to-nothing during meltdown wind-up — tiles flash white then scale to 10% so the spell prefab carries the visual
        public static bool FX_MeltdownWindupShake = true;  // continuous camera shake ramping subtle → assertive over the meltdown windup, paired with earthquake SFX

        private IEnumerator ExplosionCoroutine(List<Tile> tiles, int chainStep, int wordLength)
        {
            bool mobile = Application.isMobilePlatform;
            int tileCount = tiles.Count;

            // 2026-05-30: cache each tile's INTENDED sprite + debris color
            // BEFORE any meltdown windup FX mutates them. Mirrors tier 1's
            // PlayTier1Pop logic so primed/scored tiles get the right
            // saturated colors. Both are restored to the SpriteRenderer
            // right before Shatter samples (see per-tile shatter loop).
            //   - HasPermanentGlow primed tile → PrimedSprite + PRIMED_TILE_TINT
            //     (the HDR-bright magenta tint amplifies the pink sprite —
            //     same path tier 1 uses for "magenta looks great")
            //   - WasInScoredWord scored tile  → ScoredSprite + Color.white
            //     (white tint lets the green sprite's natural kelly green
            //     show through without the dimming-multiply problem)
            //   - Otherwise                    → live sprite + live color
            var meltdownOriginalColors  = new Color[tileCount];
            var meltdownOriginalSprites = new Sprite[tileCount];
            for (int i = 0; i < tileCount; i++)
            {
                Tile mt = tiles[i];
                if (mt == null)
                {
                    meltdownOriginalColors[i] = Color.white;
                    meltdownOriginalSprites[i] = null;
                    continue;
                }

                if (mt.HasPermanentGlow)
                {
                    meltdownOriginalColors[i]  = Tile.PRIMED_TILE_TINT;
                    meltdownOriginalSprites[i] = Tile.PrimedSprite;
                }
                else if (mt.WasInScoredWord)
                {
                    meltdownOriginalColors[i]  = Color.white;
                    meltdownOriginalSprites[i] = Tile.ScoredSprite;
                }
                else
                {
                    SpriteRenderer originalSR = mt.GetComponent<SpriteRenderer>();
                    meltdownOriginalColors[i]  = originalSR != null ? originalSR.color  : Color.white;
                    meltdownOriginalSprites[i] = originalSR != null ? originalSR.sprite : null;
                }
            }

            // Phase 11j-meltdown — Candy-Crush-style telegraph: spawn the
            // AllIn1 Magic Explosive Spell prefab on each tile FIRST so it
            // starts its wind-up animation, then defer the rest of the
            // explosion FX (flash, dissolve, fragments, audio, flipbook) so
            // they peak at the prefab's blast moment. Without this defer,
            // tiles destruct first and the prefab's late-arriving blast
            // hits empty cells (Spencer's screenshot).
            // Tier inline preview log — uses the actual formula at line ~995.
            // Was misleading (old inline used >=5/>=9 thresholds vs actual
            // >=8/>=12). Reflects what tier ACTUALLY gets assigned below.
            Debug.Log($"[FX-Detonation] tier={(chainStep >= 3 || tileCount >= 15 ? 4 : chainStep >= 2 || tileCount >= 12 ? 3 : tileCount >= 8 ? 2 : 1)} chain={chainStep} tiles={tileCount}");

            // 2026-05-30: meltdown windup phase SKIPPED per Spencer. The
            // prefab itself fast-forwards past its windup inside
            // FlipbookExplosion.PlayMeltdownSized (ParticleSystem.Simulate),
            // so the blast is visible the moment it spawns. Tile destruct +
            // pops should fire on the same frame as the blast, not 1.7s
            // later — so WINDUP_DELAY shrinks to ~0 here. Other usages of
            // the constant (tile-shake durations, orb fade times) collapse
            // proportionally — windup-phase FX vanish, just-the-blast remains.
            // Was: FlipbookExplosion.MELTDOWN_BLAST_PEAK_AT_REAL_SPEED / MELTDOWN_PREFAB_SPEED.
            const float MELTDOWN_WINDUP_DELAY = 0.05f;

            // Tier formula — hoisted up from its prior location below so we
            // can include "tier 2+" in the meltdownActive gate.
            // 2026-05-30: tier 2 threshold reverted back to 8 — the lowered
            // 5+ threshold made every 5-letter word fire meltdown VFX which
            // felt like the game was constantly exploding. 8+ keeps the
            // meltdown moment for genuinely big detonations only.
            // TEMP (Spencer 2026-06-03): tier 4 collapsed into tier 3 so the
            // tier-3 burst actually fires on big explosions in real play (big
            // cascades normally jump straight to tier 4 and skip the burst).
            // Revert by restoring the `tier = 4` branch below.
            const bool COLLAPSE_TIER4_INTO_TIER3 = true;
            int tier;
            if (!COLLAPSE_TIER4_INTO_TIER3 && (chainStep >= 3 || tileCount >= 15)) tier = 4;
            else if (chainStep >= 2 || tileCount >= 12) tier = 3;
            else if (tileCount >= 8) tier = 2;
            else tier = 1;

            // Tier-3 Candy-Crush energy burst (proof) — bright additive layers at
            // the cluster centre. Layers OVER the existing FX for now; if it lands,
            // we narrow the meltdown gate so tier 3 owns this look outright.
            // 2026-06-03 Spencer: NEVER fire on cascade steps (chainStep >= 2) — the
            // burst is the player's MOVE landing (from the drop/edit cell), not the
            // chain reactions it sets off. Without this it fired on every cascade,
            // all stacked at the original drop cell in mid-board.
            if (tier == 3 && chainStep < 2 && Tier3BurstScale > 0.01f)
            {
                float bMinX = float.MaxValue, bMaxX = float.MinValue;
                float bMinY = float.MaxValue, bMaxY = float.MinValue;
                int bN = 0;
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    Vector3 bp = tiles[i].transform.position;
                    if (bp.x < bMinX) bMinX = bp.x; if (bp.x > bMaxX) bMaxX = bp.x;
                    if (bp.y < bMinY) bMinY = bp.y; if (bp.y > bMaxY) bMaxY = bp.y;
                    bN++;
                }
                if (bN > 0)
                {
                    Vector3 bCenter = new Vector3((bMinX + bMaxX) * 0.5f, (bMinY + bMaxY) * 0.5f, 0f);
                    float bCell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                    float bSpan = Mathf.Max(bMaxX - bMinX, bMaxY - bMinY) + bCell;

                    // Erupt from the letter the player dropped/edited to make the word
                    // (the trigger) rather than the cluster centroid — feels like the
                    // burst comes from your move. Falls back to the cluster center.
                    if (LastTriggerCell.HasValue && GridManager.Instance != null)
                        bCenter = GridManager.Instance.CellToWorld(LastTriggerCell.Value.x, LastTriggerCell.Value.y);

                    PlayTier3Burst(bCenter, bSpan);
                }
            }

            // 2026-05-30: meltdown VFX now also fires on tier 2+ INITIAL
            // drops (player drops 8+ tiles in one detonation, or 12+ tiles)
            // — per Spencer's request that big detonations get the magical-
            // spell+bubble treatment from the test menu's "Detonate 12 tiles
            // MELTDOWN" button. Cascades (chainStep >= 2) are excluded so
            // they keep the Tier1Pop cascade-pop visual identity instead of
            // firing the heavier meltdown stack on every chain step.
            // 2026-05-30: meltdown VFX fires on any detonation with 8+ tiles,
            // regardless of chainStep. Earlier version had `chainStep < 2` to
            // exclude cascades from the gate — but big cascades (13+ tiles in
            // one chain step) are exactly the "big magical moment" players
            // expect to see the meltdown VFX on. Counting tiles is the right
            // proxy for "is this detonation big enough to deserve the spell."
            bool _mmActive_dbg     = MeltdownManager.Instance != null && MeltdownManager.Instance.IsActive;
            bool _tilesGate_dbg    = tileCount >= 8;
            bool meltdownActive    = _mmActive_dbg || _tilesGate_dbg;
            Debug.Log($"[Meltdown] gate={meltdownActive} (mmActive={_mmActive_dbg}, tilesGate={_tilesGate_dbg}) — tier={tier} chain={chainStep} tiles={tileCount}");

            // Junk filter for meltdown windup — only WORD tiles (scored
            // trigger words + primed words being detonated, populated
            // upstream into _pendingCascadeWords by HandManager.CacheBurstTriggers
            // and GameVisualBridge.WordsScored/TriggersFound) get the per-
            // tile windup FX (magic explosive prefab, heat overlay, primed
            // glow orb, tile punch, perlin shake). Junk/collateral splash
            // tiles stay still until the impact moment, then shatter with
            // everyone else in the per-tile shatter loop further down.
            // If no per-word info is available (test menu, edge cases),
            // fall back to applying FX to every tile (legacy behaviour).
            HashSet<Tile> meltdownWordTiles = null;
            if (meltdownActive && _pendingCascadeWords != null && _pendingCascadeWords.Count > 0)
            {
                meltdownWordTiles = new HashSet<Tile>();
                for (int wi = 0; wi < _pendingCascadeWords.Count; wi++)
                {
                    var w = _pendingCascadeWords[wi];
                    if (w == null) continue;
                    for (int ti = 0; ti < w.Count; ti++)
                        if (w[ti] != null) meltdownWordTiles.Add(w[ti]);
                }
            }

            if (FX_MeltdownPrefab && meltdownActive && FlipbookExplosion.Instance != null)
            {
                Debug.Log($"[FX] MeltdownPrefab: FIRED (wordTilesOnly={meltdownWordTiles != null}, wordCount={(meltdownWordTiles == null ? -1 : meltdownWordTiles.Count)})");

                // Earthquake rumble at the very start of the meltdown —
                // plays on a dedicated AudioSource so we can cut it off
                // cleanly when the detonation bang fires at tile-disappear.
                GameAudio.Instance?.PlayEarthquake(1f);

                // 2026-05-30: ONE magical-spell prefab per WORD (stretched
                // to cover the word's bounding box), not one per tile —
                // reads as a single magical event consuming each word
                // instead of N independent spell-pop overlaps. Per-tile
                // pop animations (fragments, flash, shatter) still fire
                // individually in the per-tile loop further down.
                //
                // If _pendingCascadeWords is unavailable (test menu fakes,
                // edge cases) we fall back to a single prefab at the
                // tiles' centroid sized to span them all.
                float meltdownCellSize = (GridManager.Instance != null) ? GridManager.Instance.CellSize : 1f;

                if (_pendingCascadeWords != null && _pendingCascadeWords.Count > 0)
                {
                    for (int wi = 0; wi < _pendingCascadeWords.Count; wi++)
                    {
                        var w = _pendingCascadeWords[wi];
                        if (w == null || w.Count == 0) continue;

                        // Bounding box of this word's tiles.
                        float minX = float.MaxValue, maxX = float.MinValue;
                        float minY = float.MaxValue, maxY = float.MinValue;
                        int counted = 0;
                        for (int ti = 0; ti < w.Count; ti++)
                        {
                            if (w[ti] == null) continue;
                            Vector3 p = w[ti].transform.position;
                            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                            if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                            counted++;
                        }
                        if (counted == 0) continue;
                        Vector3 wordCentroid = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
                        // Scale = longest bbox dimension + 1 cell of padding so
                        // the spell extends past the word's edges. Uniform
                        // scale (no stretching) keeps particles circular.
                        float wordSpan = Mathf.Max(maxX - minX, maxY - minY) + meltdownCellSize;
                        FlipbookExplosion.Instance.PlayMeltdownSized(wordCentroid, wordSpan);
                    }
                }
                else
                {
                    // Fallback — no word info available. Single big prefab at
                    // the all-tiles centroid sized to span the cluster.
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minY = float.MaxValue, maxY = float.MinValue;
                    int counted = 0;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] == null) continue;
                        Vector3 p = tiles[i].transform.position;
                        if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
                        if (p.y < minY) minY = p.y; if (p.y > maxY) maxY = p.y;
                        counted++;
                    }
                    if (counted > 0)
                    {
                        Vector3 centroid = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, 0f);
                        float span = Mathf.Max(maxX - minX, maxY - minY) + meltdownCellSize;
                        FlipbookExplosion.Instance.PlayMeltdownSized(centroid, span);
                    }
                }

                // Per-tile bubble — fires on the SAME frame as the magical
                // spell prefab above so spell + bubbles read as one event.
                // Tinted to match the spell's lavender-violet hue; tile's
                // own sprite color is overridden so the bubble cluster reads
                // as part of the magical event instead of mixed tile colors.
                // 2026-05-30: meltdownWordTiles filter REMOVED — splash-
                // damage / collateral tiles also get the bubble so every
                // exploding tile reads as part of the event (otherwise the
                // bubble cluster has gaps where collateral tiles popped).
                Color meltdownBubbleTint = new Color(0.85f, 0.60f, 1.00f, 1f);
                if (FX_MeltdownBubble)
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    FlipbookExplosion.Instance.PlayBubble(
                        tiles[i].transform.position, meltdownBubbleTint, 1.0f, 0.12f);
                }

                // Tile punch — squeeze → pop → settle scale sequence per
                // tile, telegraphing that the tiles are being "grabbed" by
                // the magic during the wind-up. Sequence runs ~250ms so it
                // completes well before the blast peak at WINDUP_DELAY.
                if (FX_MeltdownTilePunch)
                {
                    Debug.Log("[FX] MeltdownTilePunch: FIRED");
                    // Junk filter — punch only the word tiles, not collateral.
                    List<Tile> punchTiles = tiles;
                    if (meltdownWordTiles != null)
                    {
                        punchTiles = new List<Tile>();
                        for (int i = 0; i < tiles.Count; i++)
                            if (tiles[i] != null && meltdownWordTiles.Contains(tiles[i]))
                                punchTiles.Add(tiles[i]);
                    }
                    // Tile shrink-to-nothing during windup — replaces the old
                    // squeeze→pop→settle pulse. Tiles flash white at start,
                    // then scale rapidly to ~10% (OutQuart, 200ms) and HOLD
                    // there through the rest of the windup so the orb +
                    // heat overlay + perlin shake carry the visual while the
                    // tiles themselves visibly disappear into the meltdown.
                    // Matches Tier1Pop's "shrink progressively to nothing"
                    // gesture per Spencer 2026-05-05.
                    //
                    // IMPORTANT: do NOT call Tile.FlashHighlight here — its
                    // FlashBorderCoroutine writes transform.localScale every
                    // frame and hard-resets to origScale at the end, which
                    // clobbers the shrink. Manual color flash via DOColor on
                    // the sprite renderer instead (same pattern Tier1Pop uses).
                    foreach (var pt in punchTiles)
                    {
                        if (pt == null) continue;
                        Transform pxform = pt.transform;
                        SpriteRenderer pSR = pt.GetComponent<SpriteRenderer>();

                        // Stop every scale writer on this tile BEFORE starting
                        // the shrink. PrimedPulseLoop is the one that broke
                        // the original shrink — it writes transform.localScale
                        // every frame on primed tiles and only yields when
                        // _flashCoroutine != null, so the DOScale below got
                        // continuously overwritten. ClearPrimedGlow stops the
                        // primed-pulse coroutine and resets scale to base.
                        if (pt.HasPermanentGlow) pt.ClearPrimedGlow();
                        pt.StopVisualPulses();
                        pxform.DOKill();
                        if (pSR != null) pSR.DOKill();

                        Vector3 origPunchScale = pxform.localScale;

                        if (pSR != null)
                        {
                            Color startColor = pSR.color;
                            pSR.color = Color.white;
                            pSR.DOColor(startColor, 0.25f).SetEase(DG.Tweening.Ease.OutQuad);
                        }

                        pxform.DOScale(Vector3.zero, 0.20f)
                            .SetEase(DG.Tweening.Ease.OutQuart);
                    }
                }
                else { Debug.Log("[FX] MeltdownTilePunch: SKIPPED"); }

                // Tile heat-up overlay — runs in parallel with the prefab
                // wind-up so each tile visibly charges before the bang.
                if (FX_TileHeatOverlay)
                {
                    Debug.Log("[FX] TileHeatOverlay: FIRED");
                    float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] == null) continue;
                        if (meltdownWordTiles != null && !meltdownWordTiles.Contains(tiles[i])) continue;
                        FlipbookExplosion.Instance.PlayTileHeatOverlay(
                            tiles[i].transform.position, cellSize, MELTDOWN_WINDUP_DELAY);
                    }
                }
                else { Debug.Log("[FX] TileHeatOverlay: SKIPPED"); }

                // Candy-Crush-color-bomb-style primed glow orb per tile —
                // soft-circle additive halo on top of each tile that overlays
                // through the ENTIRE animation (windup + explosion + dissolve)
                // so the glow stays visible front-and-center the whole time.
                if (FX_PrimedGlowOrb)
                {
                    Debug.Log("[FX] PrimedGlowOrb: FIRED");
                    float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                    // Long max duration as a safety cap; the orb will exit
                    // earlier when its tile becomes inactive (shatter hides it).
                    float orbDuration = MELTDOWN_WINDUP_DELAY + 1.0f;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] == null) continue;
                        if (meltdownWordTiles != null && !meltdownWordTiles.Contains(tiles[i])) continue;
                        FlipbookExplosion.Instance.PlayPrimedGlowOrb(
                            tiles[i].transform.position, cellSize, orbDuration, tiles[i].transform);
                    }
                }
                else { Debug.Log("[FX] PrimedGlowOrb: SKIPPED"); }

                // Violent Perlin-noise shake on each tile for the entire
                // windup — magnitude ramps subtle→violent so tension builds
                // into the explosion. Each tile shakes with its own seed so
                // they jitter independently, not in sync.
                Debug.Log("[FX] MeltdownShake: FIRED");
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    if (meltdownWordTiles != null && !meltdownWordTiles.Contains(tiles[i])) continue;
                    tiles[i].PlayMeltdownShake(MELTDOWN_WINDUP_DELAY);
                }

                // Continuous rumble haptic during the windup, with amplitude
                // ramping from very soft → full target. Builds tension into
                // the explosion exactly like the visual Perlin shake does.
                StartCoroutine(MeltdownRumbleBuildup(
                    duration: MELTDOWN_WINDUP_DELAY,
                    startLevel: 0.10f,
                    endLevel: 0.40f,
                    frequency: 0.20f));

                // Visual counterpart to the rumble haptic — camera shake that
                // ramps subtle → assertive over the windup, syncing with the
                // earthquake SFX so the screen physically trembles into the
                // explosion. Snaps the camera home on completion so the
                // impact PlayBoardShake takes over cleanly.
                if (FX_MeltdownWindupShake)
                {
                    Debug.Log("[FX] MeltdownWindupShake: FIRED");
                    StartCoroutine(MeltdownWindupShake(
                        duration: MELTDOWN_WINDUP_DELAY,
                        startMag: 0.015f,
                        endMag: 0.10f));
                }
                else { Debug.Log("[FX] MeltdownWindupShake: SKIPPED"); }

                yield return WaitCache.Get(MELTDOWN_WINDUP_DELAY);
            }
            else { Debug.Log($"[FX] MeltdownPrefab: SKIPPED (toggle={FX_MeltdownPrefab}, meltdownActive={meltdownActive})"); }

            // Determine tier by tiles exploded + chain depth.
            // Thresholds widened 2026-05-01: most typical word matches (3-7
            // tile clusters with no cascade) now hit tier 1 (CC-style pop).
            // Tier 2 reserved for medium clusters (8-11 tiles), tier 3 for
            // large clusters or 2nd cascades, tier 4 for meltdown territory.
            // (Declaration hoisted up to where meltdownActive is computed so
            // the gate can include tier >= 2. Tier value re-used here.)
            Debug.Log($"[VFX] Explosion tier={tier} tiles={tileCount} chain={chainStep}");

            // Tiered haptic feedback
            if (FX_Haptics) { Debug.Log("[FX] Haptics: FIRED"); HapticsManager.Explosion(tier); }
            else { Debug.Log("[FX] Haptics: SKIPPED"); }

            // ── Tier-1 Candy-Crush-style pop branch ───────────────────────────
            // Replaces the generic stack for tier-1 detonations only. Each tile
            // gets a per-tile orchestrated pop (squeeze → bubble + tile fade →
            // fragments → sparkle). Audio fires once for the whole word
            // (suppressAudio:true on per-tile pops, then PlayMatchLine here).
            //   - Skipped during meltdown (meltdown's tier-4 stack handles it)
            //   - Yields ~250ms — long enough for the bubble/fragments to read
            //     before the caller (HandManager / GameVisualBridge) advances
            //     to grid.RemoveTiles. The PlayTier1Pop coroutines continue
            //     running independently and finish hiding tiles at t=350ms;
            //     null checks inside Tier1PopCoroutine make them bail safely
            //     if the gameplay path destroys the tile sooner.
            // Gravity-formed cascade detonations (chainStep >= 2) ALWAYS use the
            // tier-1 pop path — Spencer wants one cascade animation regardless
            // of cluster size, with a "primed pink → flash → squeeze" preamble.
            // This overrides the tier 2/3 generic stack that would normally
            // fire for big cascade clusters.
            bool isCascadePop = chainStep >= 2 && !meltdownActive;

            // ── Single consolidated per-explosion diagnostic ─────────────────────
            // One greppable line summarizing the tier AND which layer branches will
            // actually run for THIS explosion — so a "layer didn't show" can be
            // traced to which gate excluded it. Grep: [ExplosionTier]
            {
                bool dbgTier3Burst   = tier == 3 && Tier3BurstScale > 0.01f;
                bool dbgTier1Pop     = (tier == 1 && !meltdownActive) || isCascadePop;
                bool dbgGenericStack = !dbgTier1Pop && !meltdownActive; // tier 2/3 generic
                Debug.Log($"[ExplosionTier] tier={tier} tiles={tileCount} chain={chainStep} " +
                          $"(why: {(chainStep >= 3 ? "chain>=3" : tileCount >= 15 ? "tiles>=15" : chainStep >= 2 ? "chain>=2" : tileCount >= 12 ? "tiles>=12" : tileCount >= 8 ? "tiles>=8" : "default")}) " +
                          $"→ LAYERS: tier3Burst={dbgTier3Burst} meltdown={meltdownActive} tier1Pop={dbgTier1Pop}(cascade={isCascadePop}) genericStack={dbgGenericStack}");
            }

            if ((tier == 1 && !meltdownActive) || isCascadePop)
            {
                Debug.Log($"[FX] Tier1Pop: FIRED (cascade={isCascadePop}, chain={chainStep}, tiles={tileCount})");
                // Snapshot + clear the trigger-word side channel up-front so
                // it can't leak into any later explosion this resolution step.
                List<List<Tile>> cascadeWords = _pendingCascadeWords;
                _pendingCascadeWords = null;

                // Always pass chainStep so pitch escalation works at EVERY
                // chain depth, not just chainStep >= 2. Bug fix 2026-05-16:
                // the else branch was calling PlayMatchLine() with no args,
                // so chain depth 1 played at base pitch instead of +1 semitone.
                // chainStep 0 → pitch 1.0 (base) is correct (Mathf.Pow handles it).
                GameAudio.Instance?.PlayMatchLine(chainStep);
                // NOTE: big_pop is NOT fired here — this Tier1Pop branch handles
                // tier-1 detonations AND cascades. big_pop fires only from the
                // tier-2/3 INITIAL-detonation path (the `tier >= 2` call below).
                // (Previous code distinguished cascade-trigger-word tiles from
                // collateral tiles to apply a pink primed preamble only to the
                // former. That distinction is no longer needed since cascades
                // now use the same simple Tier 1 pop as initial word pops.)
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    // Cascades use instantPop=true (no preamble) — initial pops
                    // keep the 80ms light-up flash because it provides visual
                    // feedback during the 50ms hitstop window, masking the
                    // pause and making the initial detonation feel instant.
                    bool tileInstantPop = isCascadePop;
                    PlayTier1Pop(tiles[i], suppressAudio: true, isCascade: false, startDelay: 0f, instantPop: tileInstantPop);
                }
                // Per-word haptic — sync to the visual pop moment:
                //   - Initial pops have an 80ms light-up preamble, so the
                //     visual peak (bubble spawn) is at ~t=140ms → haptic at
                //     t=120ms is well-synced.
                //   - Cascade pops use instantPop=true (no preamble), so the
                //     visual peak shifts to ~t=60ms → haptic needs -80ms
                //     offset to land at t=40ms, in sync with the visual.
                // Without this offset, cascade haptics felt 60ms late.
                // Detonation haptic for the tier-1 / cascade pop path — IMMEDIATE (not the old
                // delayed +120ms hit). Debounced in ExplosionImpact, so when this blast ALSO went
                // through GameVisualBridge's Strong() it stays ONE buzz; when it only goes through
                // here (the case that was silent after removing Tier1PopHaptic) it still fires. 2026-06-11.
                HapticsManager.ExplosionImpact();
                yield return WaitCache.Get(0.25f);
                yield break;
            }

            // All tiers: flash → dissolve → particles → shake
            // NO DOScale or DORotate — dissolve handles the visual death.
            // Tiers differ by: flash color, particle count, shake strength,
            // dissolve speed, and whether screen flash fires.

            // ── Tier-specific parameters ──
            float dissolveDur;
            int particlesPerTile;
            Color flashColor;
            bool screenFlash;
            bool boardShake;

            switch (tier)
            {
                case 1: // Pop — quick and clean
                    dissolveDur = 0.15f;
                    particlesPerTile = mobile ? 4 : 6;
                    flashColor = Color.white;
                    screenFlash = false;
                    boardShake = false;
                    break;
                case 2: // Burst — screen flash + shake
                    dissolveDur = 0.22f;
                    particlesPerTile = mobile ? 8 : 14;
                    flashColor = new Color(1f, 0.95f, 0.7f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    break;
                case 3: // Blast — heavy shake + more particles
                    dissolveDur = 0.45f;
                    particlesPerTile = mobile ? 12 : 20;
                    flashColor = new Color(1f, 0.85f, 0.3f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    break;
                default: // Chain Bomb — everything
                    dissolveDur = 0.60f;
                    particlesPerTile = mobile ? 14 : 24;
                    flashColor = new Color(1f, 0.7f, 0.15f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    break;
            }

            // ── Cascade detection ──
            // chainStep >= 1 && !meltdownActive == real-game cascade detonation
            // (a word formed via gravity-falling letters after a prior detonation).
            // Cascades fire the SAME FX stack as the test menu's
            // "Cascade Word + BigBurst" buttons — TileFlash, TileFragments,
            // SparkleSpray, SparkleParticles, FlipbookGlow, BoardShake,
            // BigBurstFlash, plus the meltdown-grade bang + mega-impact haptic.
            bool isCascade = chainStep >= 1 && !meltdownActive;
            // chainStep == 0 && !meltdownActive == initial player-triggered
            // detonation. Gets the BARE MINIMUM (tier-scaled audio, fragments,
            // light shake) — no TileFlash, no FlipbookGlow halos, no
            // SparkleParticles starbursts, no MegaImpact haptic. Those layers
            // stack into visual noise when fired on every tier 2/3 word.
            bool isInitial = chainStep == 0 && !meltdownActive;

            // ── Flash all tiles ──
            // Fires unconditionally during meltdown OR cascade (matches the
            // TileFragments pattern) so each tile flashes right before the
            // explosion regardless of the FX_TileFlash toggle.
            if (FX_TileFlash || meltdownActive || isCascade)
            {
                Debug.Log($"[FX] TileFlash: FIRED (toggle={FX_TileFlash}, meltdown={meltdownActive}, cascade={isCascade})");
                for (int i = 0; i < tiles.Count; i++)
                {
                    if (tiles[i] == null) continue;
                    tiles[i].transform.DOComplete(); // kill any in-progress tweens cleanly
                    tiles[i].FlashHighlight(flashColor);
                }
            }

            // BigBurst per-word is handled by HandManager.FlushPendingTriggerEffects
            // (fires per primed word AND per trigger word, gated by
            // FX_BigBurstFlash which now defaults to true). The duplicate
            // single-beam logic that used to live here was removed to avoid
            // double-firing.
            // _pendingCascadeWords side channel is no longer consumed here —
            // clear it so it doesn't leak between resolution steps.
            _pendingCascadeWords = null;

            // ── Screen flash DISABLED 2026-04-29 — colored prefabs now provide impact reading.
            //    Re-enable only if Spencer wants white-wash back for hero events. Gate to
            //    meltdown-grade chains, not tier-2+. ──
            // if (screenFlash) PlayScreenFlash(tier - 1);
            // DetonationAudio is now fired inside the per-tile shatter loop
            // (right at SetActive(false)) so the bang lands EXACTLY when the
            // tiles disappear — instead of ~50ms early like it was here.
            // See the per-tile loop further down.

            // ── Flipbook explosion per tile ──
            // (Meltdown prefab — if any — was spawned at the top of the
            // coroutine before the wind-up yield.) Play() now fires only the
            // bubble@2x glow halo — the orange shrapnel sprite sheet was retired.
            // SUPPRESSED during meltdown — the prefab + orb halo carry the
            // visual; the FX_FlipbookGlow-gated Play() reads as redundant.
            if ((FX_FlipbookGlow || isCascade) && !meltdownActive && FlipbookExplosion.Instance != null)
            {
                Debug.Log("[FX] FlipbookGlow: FIRED");
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i] != null)
                        FlipbookExplosion.Instance.Play(tiles[i].transform.position, tier);
            }
            else { Debug.Log("[FX] FlipbookGlow: SKIPPED"); }

            // Per-tile meltdown bubble fires UP in the meltdown prefab block
            // now (so it coincides with the spell prefab spawn on the same
            // frame). See the bubble loop right after PlayMeltdownSized.

            // ── Shatter + particles + sparkles ──
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                Vector3 pos = tiles[i].transform.position;

                // Tile fragments — also fires unconditionally during meltdown,
                // so the tiles visibly shatter at the blast peak regardless
                // of the global FX_TileFragments toggle (which still gates
                // non-meltdown detonations).
                bool fragsThisShot = (FX_TileFragments || meltdownActive || isCascade || isInitial) && TileFragments.Instance != null;
                if (fragsThisShot)
                {
                    if (i == 0) Debug.Log($"[FX] TileFragments: FIRED (toggle={FX_TileFragments}, meltdown={meltdownActive})");
                    // Restore scale to Vector3.one before Shatter samples it.
                    // During meltdown the windup shrinks tiles to 0.10×; without
                    // this restore, fragments would inherit the shrunken scale
                    // and come out tiny. DOKill cancels the shrink tween so the
                    // assignment sticks.
                    tiles[i].transform.DOKill();
                    tiles[i].transform.localScale = Vector3.one;
                    // Restore tile to the cached sprite + color before Shatter
                    // samples them (only during meltdown — tier 1 does its own
                    // restore at line ~757 in PlayTier1Pop). This is the
                    // bright-fragments fix: forces the colored sprite
                    // (pink_tile / green_tile2) under the right tint so the
                    // fragments match the visible tile color.
                    if (meltdownActive)
                    {
                        SpriteRenderer shatterSR = tiles[i].GetComponent<SpriteRenderer>();
                        if (shatterSR != null)
                        {
                            shatterSR.DOKill();
                            shatterSR.color = meltdownOriginalColors[i];
                            if (meltdownOriginalSprites[i] != null)
                                shatterSR.sprite = meltdownOriginalSprites[i];
                        }
                    }
                    TileFragments.Instance.Shatter(tiles[i]);
                }
                else if (i == 0) { Debug.Log("[FX] TileFragments: SKIPPED"); }

                // Per-tile sparkle spray layer — sits over the chunks so each
                // shatter point also emits sparkle stars. Always fires during
                // meltdown (matches TileFragments pattern); otherwise gated by
                // FX_SparkleSpray toggle.
                bool sparkleSprayThisShot = (FX_SparkleSpray || meltdownActive || isCascade) && SparkleSpray.Instance != null;
                if (sparkleSprayThisShot)
                {
                    if (i == 0) Debug.Log($"[FX] SparkleSpray (per-tile): FIRED (toggle={FX_SparkleSpray}, meltdown={meltdownActive})");
                    SparkleSpray.Instance.Play(pos, intensity: 0.55f);
                }
                else if (i == 0) { Debug.Log("[FX] SparkleSpray (per-tile): SKIPPED"); }

                // Sparkle stars + glow (tier 2+)
                if ((FX_SparkleParticles || isCascade) && tier >= 2 && GameParticles.Instance != null)
                {
                    if (i == 0) Debug.Log("[FX] SparkleParticles: FIRED");
                    GameParticles.Instance.PlayPrimed(pos);
                    if (tier >= 3)
                        GameParticles.Instance.PlayWordScored(pos, tier * 3);
                }
                else if (i == 0) { Debug.Log($"[FX] SparkleParticles: SKIPPED (toggle={FX_SparkleParticles}, tier={tier})"); }

                // Detonation audio fires once (on the first tile) at the
                // exact frame the tile disappears, so the bang is synced to
                // the visual shatter rather than firing ~50ms early. Fires
                // unconditionally during meltdown (matches TileFragments
                // pattern); otherwise gated by FX_DetonationAudio.
                if (i == 0 && (FX_DetonationAudio || meltdownActive || isCascade || isInitial))
                {
                    Debug.Log($"[FX] DetonationAudio: FIRED at tile-disappear (toggle={FX_DetonationAudio}, meltdown={meltdownActive}, cascade={isCascade}, initial={isInitial})");
                    if (meltdownActive)
                    {
                        // Cut the earthquake rumble exactly when the bang fires
                        // so the meltdown's audio sequence reads as: rumble →
                        // hard cut → POP (no muddy overlap).
                        // 2026-05-30: detonation BOOM swapped for PlayMatchLine
                        // (cascade pop family) per Spencer — the candy-bright
                        // direction wants a punchy pop here, not a heavy boom.
                        GameAudio.Instance?.StopEarthquake();
                        GameAudio.Instance?.PlayMatchLine(chainStep);
                        HapticsManager.RumbleStop();
                        HapticsManager.MeltdownHit(); // hero moment — 0.85/0.50
                    }
                    else
                    {
                        // 2026-05-30: tier 2/3 initial-drop detonations now
                        // fire PlayMatchLine (the cascade pop) instead of
                        // PlayDetonation's heavy boom — matches the candy-
                        // bright direction and the cascade/meltdown audio
                        // identity. Applies to BOTH initial player-drop
                        // detonations AND primed-word-triggered detonations
                        // (chainStep == 1). True cascades (chainStep >= 2)
                        // already take the Tier1Pop path earlier.
                        GameAudio.Instance?.PlayMatchLine(chainStep);
                        // NOTE: big_pop is NOT fired here. The chainStep param is
                        // unreliable for the cascade test — different resolver paths
                        // pass 0 (hardcoded), step.ChainDepth (true depth), or
                        // wordIndex (a word counter), and multi-word detonations are
                        // split into small per-word groups so tileCount here isn't
                        // the real blast size. big_pop is fired at the STEP level in
                        // the resolvers, gated on (step.ChainDepth == 0 &&
                        // dyingTiles.Count >= 8) — the only place both the true
                        // cascade depth and the full tile count are known.
                        HapticsManager.Explosion(tier);
                    }
                }

                // Reset color BEFORE hiding — kills the in-flight FlashHighlight
                // coroutine and restores the baseline tint (preserving special
                // tints like gold/stone). Otherwise the coroutine stops on
                // SetActive(false) before reaching its cleanup, and the tile
                // re-activates stuck on the orange flash color.
                tiles[i].ResetVisuals();

                // Hide tile — flipbook + fragments cover the visual
                tiles[i].gameObject.SetActive(false);
            }

            // ── Shake (tier 2+) ──
            if ((FX_BoardShake || meltdownActive || isCascade || isInitial) && boardShake)
            {
                Debug.Log($"[FX] BoardShake: FIRED (toggle={FX_BoardShake}, meltdown={meltdownActive}, cascade={isCascade}, initial={isInitial})");
                PlayBoardShake(tier - 1, tileCount);
                if (tier >= 3) ShakeHandCards(tier - 1, tileCount);
                if (tier >= 4) PlayNeighborRipple(tiles, chainStep);
            }
            else { Debug.Log($"[FX] BoardShake: SKIPPED (toggle={FX_BoardShake}, boardShake={boardShake})"); }

            // ── Confetti (tier 4) — REMOVED 2026-04-30 ──
            // The orange-ball confetti+burst (GameParticles.PlayDetonation +
            // PlayMeltdown) was firing on tier-4 meltdowns and reading as
            // visual noise on top of the chunks + sparkle spray + meltdown
            // prefab. Per-tile TileFragments + SparkleSpray now carry the
            // particle layer, so this center-cluster blast is gone.

            yield return WaitCache.Get(dissolveDur + 0.03f);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCREEN FLASH — white overlay on detonation impact
        // ═══════════════════════════════════════════════════════════════════════════

        private const float FLASH_BASE_ALPHA = 0.35f;
        private const float FLASH_PER_CHAIN  = 0.10f;
        private const float FLASH_MAX_ALPHA  = 0.65f;
        private const float FLASH_FADE_DUR   = 0.15f;

        /// <summary>Quick white screen flash scaled by chain depth.</summary>
        public void PlayScreenFlash(int chainStep)
        {
            float alpha = Mathf.Min(FLASH_BASE_ALPHA + chainStep * FLASH_PER_CHAIN, FLASH_MAX_ALPHA);
            StartCoroutine(ScreenFlashCoroutine(alpha));
        }

        /// <summary>
        /// Continuous rumble with amplitude ramp from startLevel → endLevel
        /// over the duration. Uses an ease-in curve (t²) so the rumble is
        /// nearly imperceptible at first and grows into peak intensity at
        /// the explosion frame.
        /// </summary>
        /// <summary>
        /// Continuous Perlin-noise camera shake with magnitude ramping
        /// startMag → endMag over the duration via t² ease-in. Pairs with
        /// MeltdownRumbleBuildup so the screen visually trembles into the
        /// explosion alongside the earthquake SFX and haptic rumble. Snaps
        /// camera back to CAMERA_HOME on completion so the impact
        /// PlayBoardShake can take over without fighting this coroutine.
        /// </summary>
        private IEnumerator MeltdownWindupShake(float duration, float startMag, float endMag)
        {
            Camera cam = Camera.main;
            if (cam == null) yield break;
            Transform t = cam.transform;

            t.DOKill();
            Vector3 home = CAMERA_HOME;
            float seedX = Random.Range(0f, 1000f);
            float seedY = Random.Range(0f, 1000f);
            const float noiseSpeed = 28f;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float p = Mathf.Clamp01(elapsed / duration);
                float mag = Mathf.Lerp(startMag, endMag, p * p);

                float n = Time.time * noiseSpeed;
                float nx = (Mathf.PerlinNoise(seedX + n, 0f) - 0.5f) * 2f;
                float ny = (Mathf.PerlinNoise(0f, seedY + n) - 0.5f) * 2f;

                t.position = home + new Vector3(nx * mag, ny * mag, 0f);
                yield return null;
            }

            t.position = home;
        }

        private IEnumerator MeltdownRumbleBuildup(float duration, float startLevel, float endLevel, float frequency)
        {
            // Start the underlying rumble at full base amplitude — clipLevel
            // does the actual scaling. PlayConstant resets clipLevel to 1.0
            // so we must set our starting level immediately after.
            HapticsManager.Rumble(1f, frequency, duration);
            HapticsManager.SetRumbleLevel(startLevel);

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float level = Mathf.Lerp(startLevel, endLevel, t * t); // ease-in
                HapticsManager.SetRumbleLevel(level);
                yield return null;
            }
        }

        /// <summary>
        /// (Legacy — superseded by MeltdownRumbleBuildup.)
        /// Haptic pulses during the meltdown windup — frequency and intensity
        /// ramp up so the player feels tension build, mirroring the visual
        /// shake which also builds magnitude over time.
        /// </summary>
        private IEnumerator MeltdownWindupHaptics(float duration)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                // Interval shrinks from 0.30s at start → 0.05s at end (faster pulses).
                float interval = Mathf.Lerp(0.30f, 0.05f, t * t);

                // Intensity ramps up: Light → Medium → Strong over the windup.
                if (t < 0.40f)        HapticsManager.Light();
                else if (t < 0.75f)   HapticsManager.Medium();
                else                  HapticsManager.Strong();

                yield return WaitCache.Get(interval);
                elapsed += interval;
            }
        }

        private IEnumerator ScreenFlashCoroutine(float peakAlpha)
        {
            // Create a temporary full-screen white overlay via UI canvas
            GameObject canvasGO = new GameObject("FlashCanvas");
            Canvas canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 55; // above game, below meltdown

            GameObject imgGO = new GameObject("FlashImage");
            imgGO.transform.SetParent(canvasGO.transform, false);

            RectTransform rt = imgGO.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            UnityEngine.UI.Image img = imgGO.AddComponent<UnityEngine.UI.Image>();
            img.color = new Color(1f, 1f, 1f, peakAlpha);
            img.raycastTarget = false;

            // Fade out
            float elapsed = 0f;
            while (elapsed < FLASH_FADE_DUR)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / FLASH_FADE_DUR);
                img.color = new Color(1f, 1f, 1f, peakAlpha * (1f - t * t)); // quadratic fade
                yield return null;
            }

            Destroy(canvasGO);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // BOARD SHAKE
        // ═══════════════════════════════════════════════════════════════════════════

        private static readonly Vector3 CAMERA_HOME = new Vector3(0f, 0f, -5f);

        /// <summary>
        /// Screen shake via camera. chainStep=-1 for light word shake, 0+ for explosions.
        /// tileCount scales the intensity — more tiles = bigger shake.
        /// </summary>
        public void PlayBoardShake(int chainStep, int tileCount = 1)
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            Transform t = cam.transform;

            t.DOKill();
            t.position = CAMERA_HOME;

            float mag;
            float dur;
            int vibrato;
            if (chainStep < 0)
            {
                mag = 0.06f;
                dur = 0.10f;
                vibrato = 8;
            }
            else
            {
                // Scale with both chain depth and tile count. Caps raised 2026-04-18
                // so 20+ tile cluster detonations produce visibly bigger shake —
                // previously capped at 0.70 which made a 3-tile word and a 25-tile
                // cluster shake identically. Now: 25-tile cluster ≈ 1.15× vs 3-tile
                // solo ≈ 0.29×.
                float tileMult = 1f + (tileCount - 1) * 0.18f; // each extra tile adds 18%
                mag = Mathf.Min((0.22f + chainStep * 0.14f) * tileMult, 1.20f);
                // Feel-pass 2026-05-16: duration formula tightened — was
                // 0.18 + 0.05*chain + 0.018*tiles capped at 0.55s (3x over
                // RM/CC spec of 120-180ms even for big shakes). Now 0.11 +
                // 0.025*chain + 0.006*tiles capped at 0.20s. Magnitude
                // unchanged so big payoffs still feel big — just shorter.
                dur = Mathf.Min(0.11f + chainStep * 0.025f + tileCount * 0.006f, 0.20f);
                vibrato = Mathf.Min(14 + chainStep * 5 + tileCount * 2, 48);
            }

            t.DOShakePosition(dur, mag, vibrato, 90f, false, true)
                .SetEase(DG.Tweening.Ease.OutQuad)
                .OnComplete(() => { if (t != null) t.position = CAMERA_HOME; });
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // HAND CARD SHAKE — aftershock from explosions
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Shakes the hand cards as if absorbing the impact of an explosion.
        /// Dampened relative to the board shake — feels like aftershock.
        /// </summary>
        public void ShakeHandCards(int chainStep, int tileCount = 1)
        {
            if (HandManager.Instance == null) return;

            float tileMult = 1f + (tileCount - 1) * 0.12f;
            float intensity = Mathf.Min((0.08f + chainStep * 0.03f) * tileMult, 0.20f);
            float dur = Mathf.Min(0.25f + chainStep * 0.04f + tileCount * 0.02f, 0.25f);

            StartCoroutine(HandCardShakeCoroutine(intensity, dur));
        }

        private IEnumerator HandCardShakeCoroutine(float intensity, float duration)
        {
            var handMgr = HandManager.Instance;
            if (handMgr == null) yield break;

            // Get card objects
            GameObject[] cards = handMgr.GetCardObjects();
            if (cards == null) yield break;

            // Store rest positions
            Vector3[] restPos = new Vector3[cards.Length];
            for (int i = 0; i < cards.Length; i++)
                restPos[i] = cards[i] != null ? cards[i].transform.position : Vector3.zero;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float decay = 1f - (elapsed / duration);
                decay = decay * decay; // quadratic decay — fast falloff

                for (int i = 0; i < cards.Length; i++)
                {
                    if (cards[i] == null) continue;
                    float ox = Random.Range(-intensity, intensity) * decay;
                    float oy = Random.Range(-intensity, intensity) * 0.5f * decay;
                    float rz = Random.Range(-3f, 3f) * decay;

                    cards[i].transform.position = restPos[i] + new Vector3(ox, oy, 0f);
                    cards[i].transform.localRotation = Quaternion.Euler(0f, 0f, rz);
                }

                yield return null;
            }

            // Snap back clean
            for (int i = 0; i < cards.Length; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.position = restPos[i];
                cards[i].transform.localRotation = Quaternion.identity;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // NEIGHBOR RIPPLE BOUNCE — adjacent tiles react to scored/detonated words
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Makes tiles adjacent to a scored/detonated word bounce slightly.
        /// Creates a ripple effect radiating outward from the impact center.
        /// Intensity is ~30-50% of the primary animation (Disney secondary motion).
        /// </summary>
        public void PlayNeighborRipple(List<Tile> scoredTiles, int chainStep = 0)
        {
            if (scoredTiles == null || scoredTiles.Count == 0) return;
            if (GridManager.Instance == null) return;

            var grid = GridManager.Instance;

            // Collect scored tile positions for distance calculation
            HashSet<Vector2Int> scoredPositions = new HashSet<Vector2Int>();
            Vector3 center = Vector3.zero;
            int centerCount = 0;
            foreach (var tile in scoredTiles)
            {
                if (tile == null) continue;
                scoredPositions.Add(new Vector2Int(tile.Col, tile.Row));
                center += tile.transform.position;
                centerCount++;
            }
            if (centerCount == 0) return;
            center /= centerCount;

            // Intensity scales with chain depth — bigger chains, bigger ripple
            float baseIntensity = 3f + chainStep * 1.5f;
            float maxIntensity = 8f;

            // Find and bounce all adjacent tiles not in the scored set
            for (int col = 0; col < GridManager.COLS; col++)
            {
                for (int row = 0; row < GridManager.ROWS; row++)
                {
                    if (scoredPositions.Contains(new Vector2Int(col, row))) continue;

                    Tile neighbor = grid.GetTile(col, row);
                    if (neighbor == null) continue;
                    // Escort drop-targets (chickens) and vaults must NOT bounce/pop from a neighbor's word —
                    // the DOPunchPosition + DOComplete also stomps their gravity tween. 2026-06-19 Spencer.
                    if (neighbor.IsDropTargetVisual || neighbor.IsVault) continue;

                    // Check if adjacent to any scored tile (8-directional)
                    bool isAdjacent = false;
                    foreach (var sp in scoredPositions)
                    {
                        if (Mathf.Abs(col - sp.x) <= 1 && Mathf.Abs(row - sp.y) <= 1)
                        {
                            isAdjacent = true;
                            break;
                        }
                    }
                    if (!isAdjacent) continue;

                    // Distance-based delay — closer tiles react first
                    float dist = Vector3.Distance(neighbor.transform.position, center);
                    float delay = dist * 0.03f; // 30ms per unit of distance

                    // Bounce direction: away from center
                    Vector3 dir = (neighbor.transform.position - center).normalized;
                    float intensity = Mathf.Min(baseIntensity, maxIntensity);
                    Vector3 punch = dir * intensity * 0.01f; // small positional punch

                    neighbor.transform.DOComplete();
                    neighbor.transform
                        .DOPunchPosition(punch, 0.15f, 4, 0.5f)
                        .SetDelay(delay)
                        .SetEase(DG.Tweening.Ease.OutQuad);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // HITSTOP — freeze frame on big moments
        // ═══════════════════════════════════════════════════════════════════════════

        // Tuning constants — expose as SerializeField when moving to UIConfig
        private const float HITSTOP_LIGHT_DUR    = 0.04f;  // word scored
        private const float HITSTOP_MEDIUM_DUR   = 0.07f;  // chain depth 1
        private const float HITSTOP_HEAVY_DUR    = 0.12f;  // chain depth 2+
        private const float HITSTOP_ULTRA_DUR    = 0.18f;  // meltdown
        private const float HITSTOP_SAFETY_MAX   = 0.5f;   // max freeze ever

        private Coroutine _activeHitStop;
        private float _hitStopSafetyTimer;

        private void Update()
        {
            // Safety net: if timeScale is stuck at 0, force restore
            if (Time.timeScale < 0.01f)
            {
                _hitStopSafetyTimer += Time.unscaledDeltaTime;
                if (_hitStopSafetyTimer > HITSTOP_SAFETY_MAX)
                {
                    Time.timeScale = 1f;
                    _hitStopSafetyTimer = 0f;
                    Debug.LogWarning("[WordDropFX] HitStop safety net — forced timeScale restore.");
                }
            }
            else
            {
                _hitStopSafetyTimer = 0f;
            }
        }

        /// <summary>
        /// Freezes time briefly for dramatic impact.
        /// chainStep: -1=none, 0=light, 1=medium, 2+=heavy, 3+=ultra (meltdown).
        /// Safe: runs on this MonoBehaviour (DontDestroyOnLoad not needed — WordDropFX persists),
        /// has safety net timer in Update().
        /// </summary>
        public void PlayHitStop(int chainStep)
        {
            if (chainStep < 0) return; // no hitstop for regular word scores

            float duration;
            if (chainStep >= 3) duration = HITSTOP_ULTRA_DUR;
            else if (chainStep >= 2) duration = HITSTOP_HEAVY_DUR;
            else if (chainStep >= 1) duration = HITSTOP_MEDIUM_DUR;
            else duration = HITSTOP_LIGHT_DUR;

            if (_activeHitStop != null) StopCoroutine(_activeHitStop);
            _activeHitStop = StartCoroutine(HitStopCoroutine(duration));
        }

        private IEnumerator HitStopCoroutine(float duration)
        {
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(duration);
            Time.timeScale = 1f;
            _activeHitStop = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // TILE LANDING — squash and stretch on impact
        // ═══════════════════════════════════════════════════════════════════════════

        public void PlayLandingBounce(Transform t)
        {
            if (t == null) return;
            Vector3 orig = t.localScale;

            // Squash (wide + short)
            Sequence seq = DOTween.Sequence();
            seq.Append(t.DOScale(new Vector3(orig.x * 1.2f, orig.y * 0.8f, 1f), LAND_SQUASH_DUR * 0.3f)
                .SetEase(DG.Tweening.Ease.InQuad));
            // Settle back with overshoot
            seq.Append(t.DOScale(orig, LAND_SQUASH_DUR * 0.7f)
                .SetEase(DG.Tweening.Ease.OutBack));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CARD ANIMATIONS — Balatro-style hand management
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Slide a card in from off-screen right with overshoot.</summary>
        public static void CardSlideIn(Transform card, Vector3 target, float delay = 0f)
        {
            if (card == null) return;
            card.DOMove(target, CARD_DEAL_DUR)
                .SetDelay(delay)
                .SetEase(DG.Tweening.Ease.OutBack);
        }

        /// <summary>Pop a card when selected (raise + punch).</summary>
        public static void CardSelect(Transform card, float raiseY)
        {
            if (card == null) return;
            Vector3 pos = card.position;
            card.DOMove(new Vector3(pos.x, raiseY, pos.z), CARD_SELECT_DUR)
                .SetEase(DG.Tweening.Ease.OutBack);
            card.DOPunchScale(Vector3.one * 0.08f, CARD_SELECT_DUR, 1, 0.5f);
        }

        /// <summary>Drop a card back down when deselected.</summary>
        public static void CardDeselect(Transform card, float baseY)
        {
            if (card == null) return;
            Vector3 pos = card.position;
            card.DOMove(new Vector3(pos.x, baseY, pos.z), CARD_SELECT_DUR)
                .SetEase(DG.Tweening.Ease.OutQuad);
        }

        /// <summary>Animate card to new X position (shuffle/reorder).</summary>
        public static void CardMoveTo(Transform card, Vector3 target, float duration = -1f)
        {
            if (card == null) return;
            float dur = duration > 0 ? duration : CARD_SHUFFLE_DUR;
            card.DOMove(target, dur)
                .SetEase(DG.Tweening.Ease.OutBack);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORE NUMBER — punch up animation for HUD
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Punch a transform for score display emphasis.</summary>
        public static void ScorePunch(Transform t, float strength = 0.3f)
        {
            if (t == null) return;
            t.DOPunchScale(Vector3.one * strength, 0.3f, 2, 0.5f)
                .SetEase(DG.Tweening.Ease.OutElastic);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // UTILITY
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>Returns chain-accelerated beat duration.</summary>
        public static float GetBeatDuration(float baseBeat, int chainStep)
        {
            float beat = baseBeat * Mathf.Pow(CHAIN_SPEED_MULT, chainStep);
            return Mathf.Max(beat, CHAIN_MIN_BEAT);
        }

        /// <summary>
        /// Brief time-freeze on detonation impact (hitstop).
        /// Runs on WordDropFX's own MonoBehaviour so it can't be killed by
        /// StopAllCoroutines on HandManager. Always restores timeScale.
        /// </summary>
        public static IEnumerator HitStop(float duration = 0.05f)
        {
            // Run on WordDropFX instance so it survives HandManager coroutine stops
            if (Instance != null)
            {
                Instance.StartCoroutine(HitStopInternal(duration));
                yield return new WaitForSecondsRealtime(duration);
            }
        }

        private static IEnumerator HitStopInternal(float duration)
        {
            Time.timeScale = 0f;
            yield return new WaitForSecondsRealtime(duration);
            Time.timeScale = 1f;
        }

        /// <summary>Safety: ensure timeScale is restored. Call on game state transitions.</summary>
        public static void EnsureTimeScaleRestored()
        {
            Time.timeScale = 1f;
        }

        /// <summary>Kill all tweens on a transform.</summary>
        public static void Kill(Transform t)
        {
            if (t != null) t.DOKill();
        }
    }
}
