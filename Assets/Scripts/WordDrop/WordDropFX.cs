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
        public const float SCORE_POP_DURATION   = 0.20f;
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
        public const float PARTICLE_LIFETIME_MIN = 0.25f;
        public const float PARTICLE_LIFETIME_MAX = 0.5f;
        public const float PARTICLE_SPEED        = 3.5f;
        public const float PARTICLE_SIZE          = 0.08f;

        // ── State ───────────────────────────────────────────────────────────────
        private Transform _gridRoot;
        private ParticleSystem _detonationParticles;

        // ── Lifecycle ───────────────────────────────────────────────────────────

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("WordDropFX");
                go.AddComponent<WordDropFX>();
                Debug.Log("[WordDropFX] Auto-created with DOTween.");
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
            renderer.material = new Material(Shader.Find("Sprites/Default"));
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

            // All tiles flash simultaneously
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;

                tile.transform.DOComplete();
                tile.FlashHighlight(color);
                tile.transform
                    .DOPunchScale(Vector3.one * punch, SCORE_POP_DURATION, 1, 0.5f)
                    .SetEase(DG.Tweening.Ease.OutBack);

                // Temporary shadow under the tile during the pop
                StartCoroutine(TileScoredShadow(tile));
            }

            // Light screen shake on word scored — subtle bump
            PlayBoardShake(-1); // -1 = lighter than base detonation shake
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
            yield return new WaitForSeconds(0.18f);

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

        public void PlayDetonation(List<Tile> tiles, int chainStep)
        {
            if (tiles == null || tiles.Count == 0) return;

            foreach (var tile in tiles)
            {
                if (tile == null) continue;
                Transform t = tile.transform;
                Vector3 orig = t.localScale;

                Sequence seq = DOTween.Sequence();

                // Squeeze in tight (anticipation — builds tension)
                seq.Append(t.DOScale(orig * DETONATE_SQUEEZE, DETONATE_SQUEEZE_DUR)
                    .SetEase(DG.Tweening.Ease.InBack, 2f));

                // Pop out big with elastic overshoot + flash
                seq.AppendCallback(() =>
                {
                    if (tile != null) tile.FlashHighlight(Color.white);
                });
                seq.Append(t.DOScale(orig * 1.3f, DETONATE_POP_DUR * 0.4f)
                    .SetEase(DG.Tweening.Ease.OutBack, 4f));

                // Elastic settle back to original
                seq.Append(t.DOScale(orig, DETONATE_POP_DUR * 0.8f)
                    .SetEase(DG.Tweening.Ease.OutElastic, 0.5f, 0.3f));
            }

            // No shake here — explosion shake handles it
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPLOSION — staggered shrink + rotation punch
        // ═══════════════════════════════════════════════════════════════════════════

        public Coroutine PlayExplosion(List<Tile> tiles, int chainStep = 0)
        {
            if (tiles == null || tiles.Count == 0) return null;
            return StartCoroutine(ExplosionCoroutine(tiles, chainStep));
        }

        private IEnumerator ExplosionCoroutine(List<Tile> tiles, int chainStep)
        {
            float dissolveDur = 0.28f + chainStep * 0.02f; // was 0.35 — snappier

            // Phase 1: Quick scale pop on all tiles (anticipation before dissolve)
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;

                tile.transform.DOComplete();
                tile.transform.DOPunchScale(Vector3.one * 0.2f, 0.08f, 1, 0.5f)
                    .SetEase(DG.Tweening.Ease.OutQuad);
            }
            yield return new WaitForSeconds(0.06f);

            // Phase 2: Dissolve + particles + rotation
            for (int i = 0; i < tiles.Count; i++)
            {
                Tile tile = tiles[i];
                if (tile == null) continue;

                float randomRot = Random.Range(-EXPLODE_PUNCH_ROT, EXPLODE_PUNCH_ROT);
                tile.transform.DORotate(new Vector3(0, 0, randomRot), dissolveDur)
                    .SetEase(DG.Tweening.Ease.InQuad);

                // More particles — bigger burst
                int burstCount = PARTICLE_BURST_COUNT + 6 + chainStep * 4;
                EmitDetonationBurst(tile.transform.position, burstCount);

                tile.Dissolve(dissolveDur);
            }

            PlayBoardShake(chainStep, tiles.Count);
            ShakeHandCards(chainStep, tiles.Count);

            yield return new WaitForSeconds(dissolveDur + 0.03f);

            foreach (var tile in tiles)
                if (tile != null) tile.transform.rotation = Quaternion.identity;
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
                mag = 0.18f;
                dur = 0.18f;
                vibrato = 12;
            }
            else
            {
                // Scale with both chain depth and tile count
                float tileMult = 1f + (tileCount - 1) * 0.15f; // each extra tile adds 15%
                mag = Mathf.Min((0.30f + chainStep * 0.18f) * tileMult, 1.3f);
                dur = Mathf.Min(0.28f + chainStep * 0.06f + tileCount * 0.02f, 0.6f);
                vibrato = Mathf.Min(18 + chainStep * 4 + tileCount * 2, 50);
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
            float intensity = Mathf.Min((0.12f + chainStep * 0.04f) * tileMult, 0.35f);
            float dur = Mathf.Min(0.25f + chainStep * 0.04f + tileCount * 0.02f, 0.5f);

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

        /// <summary>Kill all tweens on a transform.</summary>
        public static void Kill(Transform t)
        {
            if (t != null) t.DOKill();
        }
    }
}
