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

            float punch = SCORE_POP_STRENGTH + chainStep * 0.05f;

            // Staggered fuse-lit flash across tiles — each tile pops in sequence
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;

                tile.transform.DOComplete();
                tile.SetScoredSprite(true);
                tile.SetSortingOrder(15);

                // Stagger: each tile flashes white slightly after the previous
                float delay = i * 0.06f;
                int idx = i;
                SpriteRenderer sr = tile.GetComponent<SpriteRenderer>();

                // Flash white → fade to normal over staggered timing
                if (sr != null)
                {
                    sr.color = Color.white;
                    DOTween.Sequence()
                        .AppendInterval(delay)
                        .AppendCallback(() => { if (sr != null) sr.color = new Color(2f, 2f, 2f, 1f); }) // HDR bright flash
                        .Append(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; },
                            Color.white, 0.15f).SetEase(DG.Tweening.Ease.OutQuad));
                }

                tile.transform
                    .DOPunchScale(Vector3.one * punch, SCORE_POP_DURATION, 1, 0.5f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack)
                    .OnComplete(() => {
                        if (tile != null) {
                            tile.SetSortingOrder(5);
                            tile.SetScoredSprite(false);
                        }
                    });

                // Temporary shadow under the tile during the pop
                // Shadow disabled — cleaner without floating shadows during animations
                // StartCoroutine(TileScoredShadow(tile));
            }

            // Sound + particles + screen shake + neighbor ripple on word scored
            GameAudio.Instance?.PlayWordScored();
            if (tiles.Count > 0 && tiles[0] != null)
            {
                Vector3 center = Vector3.zero;
                foreach (var tile in tiles) if (tile != null) center += tile.transform.position;
                center /= Mathf.Max(1, tiles.Count);
                GameParticles.Instance?.PlayWordScored(center, tiles.Count * 3);
            }
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

        public void PlayDetonation(List<Tile> tiles, int chainStep)
        {
            if (tiles == null || tiles.Count == 0) return;
            GameAudio.Instance?.PlayDetonation(chainStep);

            Vector3 center = Vector3.zero;
            foreach (var tile in tiles)
                if (tile != null) center += tile.transform.position;
            center /= Mathf.Max(1, tiles.Count);
            GameParticles.Instance?.PlayDetonation(center, chainStep);

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
                seq.Append(t.DOScale(orig, DETONATE_POP_DUR * 0.8f)
                    .SetEase(DG.Tweening.Ease.OutElastic, 0.5f, 0.3f));
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPLOSION — staggered shrink + rotation punch
        // ═══════════════════════════════════════════════════════════════════════════

        public Coroutine PlayExplosion(List<Tile> tiles, int chainStep = 0, int wordLength = 3)
        {
            if (tiles == null || tiles.Count == 0) return null;
            return StartCoroutine(ExplosionCoroutine(tiles, chainStep, wordLength));
        }

        private IEnumerator ExplosionCoroutine(List<Tile> tiles, int chainStep, int wordLength)
        {
            bool mobile = Application.isMobilePlatform;
            int tileCount = tiles.Count;

            // Determine tier by tiles exploded + chain depth
            int tier;
            if (chainStep >= 3 || tileCount >= 15) tier = 4;
            else if (chainStep >= 2 || tileCount >= 9) tier = 3;
            else if (tileCount >= 5) tier = 2;
            else tier = 1;

            Debug.Log($"[VFX] Explosion tier={tier} tiles={tileCount} chain={chainStep}");

            // Tiered haptic feedback
            HapticsManager.Explosion(tier);

            // All tiers: flash → dissolve → particles → shake
            // NO DOScale or DORotate — dissolve handles the visual death.
            // Tiers differ by: flash color, particle count, shake strength,
            // dissolve speed, and whether screen flash / confetti fires.

            // ── Tier-specific parameters ──
            float dissolveDur;
            int particlesPerTile;
            Color flashColor;
            bool screenFlash;
            bool boardShake;
            bool confetti;

            switch (tier)
            {
                case 1: // Pop — quick and clean
                    dissolveDur = 0.15f;
                    particlesPerTile = mobile ? 4 : 6;
                    flashColor = Color.white;
                    screenFlash = false;
                    boardShake = false;
                    confetti = false;
                    break;
                case 2: // Burst — screen flash + shake
                    dissolveDur = 0.22f;
                    particlesPerTile = mobile ? 8 : 14;
                    flashColor = new Color(1f, 0.95f, 0.7f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    confetti = false;
                    break;
                case 3: // Blast — heavy shake + more particles
                    // Phase 11+ — slower hold for big chain-reactions so the
                    // particles + shake + screen flash land as a discrete moment
                    // instead of blurring into the next cascade layer.
                    dissolveDur = 0.45f;
                    particlesPerTile = mobile ? 12 : 20;
                    flashColor = new Color(1f, 0.85f, 0.3f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    confetti = false;
                    break;
                default: // Chain Bomb — everything
                    // Phase 11+ — biggest tier slows down the most. Confetti +
                    // meltdown particles get a full visible beat before the
                    // chain proceeds.
                    dissolveDur = 0.60f;
                    particlesPerTile = mobile ? 14 : 24;
                    flashColor = new Color(1f, 0.7f, 0.15f, 1f);
                    screenFlash = true;
                    boardShake = true;
                    confetti = true;
                    break;
            }

            // ── Flash all tiles ──
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                tiles[i].transform.DOComplete(); // kill any in-progress tweens cleanly
                tiles[i].FlashHighlight(flashColor);
            }

            // ── Screen flash DISABLED 2026-04-29 — colored prefabs now provide impact reading.
            //    Re-enable only if Spencer wants white-wash back for hero events. Gate to
            //    meltdown-grade chains, not tier-2+. ──
            // if (screenFlash) PlayScreenFlash(tier - 1);
            Debug.Log($"[DetonationSFX] WordDropFX.PlayExplosion calling PlayDetonation(tier={tier}, arg={tier-1}). GameAudio.Instance null? {GameAudio.Instance == null}");
            GameAudio.Instance?.PlayDetonation(tier - 1);

            // ── Flipbook explosion per tile ──
            if (FlipbookExplosion.Instance != null)
            {
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i] != null)
                        FlipbookExplosion.Instance.Play(tiles[i].transform.position, tier);
            }

            // ── Shatter + particles + sparkles ──
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                Vector3 pos = tiles[i].transform.position;

                // Tile fragments
                if (TileFragments.Instance != null)
                    TileFragments.Instance.Shatter(tiles[i]);

                // Ember particles removed — flipbook + bubble + fragments is enough

                // Sparkle stars + glow (tier 2+)
                if (tier >= 2 && GameParticles.Instance != null)
                {
                    GameParticles.Instance.PlayPrimed(pos);
                    if (tier >= 3)
                        GameParticles.Instance.PlayWordScored(pos, tier * 3);
                }

                // Hide tile — flipbook + fragments cover the visual
                tiles[i].gameObject.SetActive(false);
            }

            // ── Shake (tier 2+) ──
            if (boardShake)
            {
                PlayBoardShake(tier - 1, tileCount);
                if (tier >= 3) ShakeHandCards(tier - 1, tileCount);
                if (tier >= 4) PlayNeighborRipple(tiles, chainStep);
            }

            // ── Confetti (tier 4) ──
            if (confetti)
            {
                Vector3 center = Vector3.zero;
                int count = 0;
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i] != null) { center += tiles[i].transform.position; count++; }
                if (count > 0)
                {
                    center /= count;
                    GameParticles.Instance?.PlayDetonation(center, chainStep);
                    GameParticles.Instance?.PlayMeltdown(center);
                }
            }

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
                dur = Mathf.Min(0.18f + chainStep * 0.05f + tileCount * 0.018f, 0.55f);
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
