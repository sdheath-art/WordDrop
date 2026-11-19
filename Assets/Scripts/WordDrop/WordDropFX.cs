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
        public static bool SINGLE_WORD_POP = false;   // 2026-10-28 Spencer: back to the 30ms-per-letter light-up ripple ("i think i like the ripple"); the pop is still one burst. true = whole word lights at once

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
//                 WDLog.Log("[WordDropFX] Auto-created with DOTween.");
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DOTween.Init(false, true, LogBehaviour.ErrorsOnly);
            // 2026-09-08 Spencer: the budget was never set, so it sat on DOTween's default of
            // 200 tweeners / 50 sequences. A 9x11 board plus a chain detonation goes past that,
            // and DOTween then REALLOCATES its pools mid-explosion — a stall exactly when the
            // game is busiest, which is a strong candidate for the frame drops on big pops.
            // Sized generously; the cost is a one-time allocation at startup, not per frame.
            DOTween.SetTweensCapacity(tweenersCapacity: 2500, sequencesCapacity: 300);   // 11-16: was 800/200 - a 10-rocket L1 finale (Movie_011) hit the cap and DOTween grew to 2000 MID-PLAY (alloc = possible hitch at the big moment)
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
            renderer.sortingOrder = 40; // above tiles  // was 20; re-based above the row bands (tiles reach 37) 2026-10-20
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

        /// <summary>2026-09-03 swap-pivot: flash a scored word in its PRIMED colour and
        /// leave it armed, rather than the green scored flash that settled to white before
        /// the magenta primed glow took over. In armed-web the word is being ARMED, not
        /// cleared, so green -> white -> magenta reads as a wrong-colour flicker.</summary>
        // ── SCORED FLASH = THE ARMED COLOUR (2026-09-08 Spencer) ────────────────────
        // "it should probably flash the colour that its changing, so magenta rather than the
        // green... it momentarily flips from green to tan then to magenta. it should be smooth
        // and seemless so that it just lights up to magenta and stays that colour."
        //
        // Tried once before and reverted as janky. TWO causes, both since removed:
        //   1. The per-frame primed PULSE wrote sr.color every frame and fought this tween for
        //      the same property. Armed tiles are static now (Tile.PRIMED_ANIMATION = false).
        //   2. The loop forced the RESTING (tan) face while magenta eased in as a tint — that
        //      is the tan flicker. Both of those writes are now gated on this flag.
        //
        // The flash lights straight up in the colour the word is BECOMING (gold for a
        // high-value word, via the one PrimedGlowForCell chokepoint) and settles exactly onto
        // Tile.ArmedRestColor — no green, no tan, no white gap.
        public const bool SCORED_FLASH_USES_PRIMED_COLOR = true;

        /// <summary>EXPERIMENT 2026-09-10 (Spencer): flash EVERY word the board forms, including
        /// ones that score nothing because they were already scored this run. Set false to
        /// restore "only words that pay get a flash".</summary>
        public static bool FLASH_EVERY_WORD = true;

        /// <summary>Flash peak for a GOLD word. Near-neutral on purpose — see wordFlashPeak.
        /// Slightly warm so it still reads gold, above 1.0 so desktop bloom catches it.</summary>
        public static Color GOLD_FLASH_PEAK = new Color(1.30f, 1.25f, 1.10f, 1f);

        /// <summary>forceGold: for the FX test menu only. This method decides gold from the
        /// TILES' letters and ignores the colour it is handed, so a test button could not make a
        /// non-gold word flash gold — every variant looked the same. 2026-09-10 Spencer.</summary>
        public void PlayWordScored(List<Tile> tiles, Color color, int chainStep, bool forceGold = false)
        {
            if (tiles == null || tiles.Count == 0) return;
            WDLog.Log($"[ScoredFlash] PlayWordScored fired: {tiles.Count} tile(s) arg3={chainStep} first='{(tiles[0] != null ? tiles[0].Letter.ToString() : "?")}'"); // TEMP cascade diagnostic 2026-06-11

            // Feel-pass 2026-05-16: cap punch at 0.18 (1.18× peak).
            // Was uncapped — chain 6 reached 0.42 (1.42×), bigger than the
            // detonation pop (1.30×) and broke hierarchy. Word-scored is
            // the smaller celebration; detonation is the big one. Chain 0-1
            // values are unchanged; chain 2+ gets clamped to 1.18×.
            float punch = Mathf.Min(SCORE_POP_STRENGTH + chainStep * 0.05f, 0.18f);

            // ── DECIDE GOLD ONCE, PER WORD (2026-09-09 Spencer) ──────────────────────
            // "when a gold word is primed it is briefly turning magenta and then back to gold."
            // PrimedGlowForCell is a PER-TILE question, and when the word is not yet in the
            // registry it falls back to "is THIS tile a high-value letter?". But goldness
            // belongs to the WORD — it is gold because it CONTAINS one high-value letter — so
            // every other tile in that word correctly answered "no" and flashed magenta, then
            // flipped once the word registered.
            // The whole word is right here, so answer it once for the set and give every tile
            // the same colour. No tile can disagree with its own word.
            // Branch priming means a scored word does NOT necessarily arm. Ask the rules,
            // using the same predicate the gate uses, or rejected words wear the armed colour.
            var wcells = new System.Collections.Generic.List<Vector2Int>();
            for (int ci = 0; ci < tiles.Count; ci++)
                if (tiles[ci] != null) wcells.Add(new Vector2Int(tiles[ci].Col, tiles[ci].Row));
            bool willArm = RulesEngine.Instance == null || RulesEngine.Instance.WillPrime(wcells);

            bool wordIsGold = forceGold;
            for (int gi = 0; gi < tiles.Count && !wordIsGold; gi++)
            {
                var gt = tiles[gi];
                if (gt == null || gt.IsWild) continue;
                if (LetterData.GetPoints(gt.Letter) >= Tile.HIGH_VALUE_POINTS) wordIsGold = true;
                else if (RulesEngine.PrimedGlowForCell(new Vector2Int(gt.Col, gt.Row)) == Tile.PRIMED_GOLD_GLOW)
                    wordIsGold = true;   // registry already knows
            }
            // NEUTRALISE for authored primed art — see Tile.NeutralisePrimedTint. Without this
            // the cascade scored-flash multiplies a magenta over a green tile and it goes DARK.
            Color wordArmedHdr = Tile.NeutralisePrimedTint(
                wordIsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW, wordIsGold);

            // FLASH PEAK for a GOLD word (2026-09-10 Spencer: "it ends up turning into this
            // orange colour that i don't like"). sr.color MULTIPLIES the sprite. A rose flash
            // works because it multiplies a TAN tile by rose. A gold flash multiplied an already
            // GOLD tile by gold: red clips at 1.0, blue is pulled to 0.64, and the hue slides to
            // salmon. Multiplying gold by gold cannot make brighter gold — only more saturated
            // orange. A near-NEUTRAL warm multiply raises brightness and leaves the hue alone,
            // so the tile lights up as itself.
            Color wordFlashPeak = wordIsGold ? GOLD_FLASH_PEAK : wordArmedHdr;   // already neutralised above

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
                if (!SCORED_FLASH_USES_PRIMED_COLOR) tile.SetScoredSprite(true);
                // NO LIFT (2026-09-10 Spencer: "the butt ends pop out again and then tuck back
                // in"). Raising the tile above every row band is what made its shaded top cap
                // visible — that cap is hidden ONLY because the tile above draws in front of it,
                // so lifting the tile necessarily exposes it, then hides it again when the lift
                // expires. A colour flash does not need to be above the board: the only part of
                // the tile a neighbour covers is the cap, which is meant to be covered.
                // Call tile.LiftForFx(1.20f) here to restore the old behaviour.

                // Stagger: each tile flashes white slightly after the previous
                // Feel-pass 2026-05-16: 0.06 → 0.03 (matches TILE_STAGGER_DELAY
                // constant). On 5-letter words this halves total stagger from
                // 300ms → 150ms, so the last tile pops while the first is
                // still mid-animation — feels like a wave, not a queue.
                // SINGLE POP (2026-10-22 Spencer: "instead of having staggered pops for each letter ... just have one pop?").
                // The whole word lights up together (the flash HOLD still gives ~0.12s to read which word it was), then pops as
                // one. Chains still step word-by-word and blasts still sweep — only the letters WITHIN a word lose the ripple.
                float delay = SINGLE_WORD_POP ? 0f : i * TILE_STAGGER_DELAY;
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
                    // Ask the ONE chokepoint what this cell arms to, so a high-value word
                    // lights up gold rather than everything flashing magenta. 2026-09-08.
                    Color armedHdr  = wordArmedHdr;   // decided once above, for the whole word
                    bool  armedGold = wordIsGold;
                    Color flashGreen = SCORED_FLASH_USES_PRIMED_COLOR
                        ? wordFlashPeak         // brighten, do not re-tint (see wordFlashPeak)
                        : new Color(0.30f, Mathf.Max(ScoredFlashGreen, 1.45f), 0.30f, 1f);
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
                            // Swap the armed face HERE, inside the staggered callback — not up
                            // front. Doing it before the tween put every tile's armed face on in
                            // frame 0, so the whole word turned magenta at once and the stagger
                            // vanished. The face and the flash have to land together, per tile,
                            // for the wave to read. 2026-09-08 Spencer.
                            if (SCORED_FLASH_USES_PRIMED_COLOR && willArm && flashTile != null)
                                flashTile.ApplyArmedFaceNow(armedGold);
                            if (sr != null) sr.color = flashGreen; // green-biased flash; green peak is the bloom lever (FX Bloom Tuning slider)
                            // MOBILE bloom: sr.color clamps to 1.0 on iOS/Metal, so the green
                            // never crosses the 1.30 bloom line on device. Drive the additive
                            // overlay with the same HDR green so it glows on the phone. No-op on
                            // desktop (sr.color HDR already blooms there).
                            if (flashTile != null) flashTile.SetBloomGlow(flashGreen, 0.90f);
                        })
                        .AppendInterval(SCORE_FLASH_HOLD)
                        .Append(DOTween.To(() => sr.color, c => { if (sr != null) sr.color = c; },
                            (SCORED_FLASH_USES_PRIMED_COLOR && willArm) ? Tile.ArmedRestColor(armedHdr) : Color.white,
                            SCORE_FLASH_FADE).SetEase(DG.Tweening.Ease.OutQuad)) // settle EXACTLY onto the armed resting colour (or WHITE on the legacy green path, so the magenta hand-off isn't muddied)
                        .Join(DG.Tweening.DOVirtual.Float(0.90f, 0f, SCORE_FLASH_FADE,
                            a => { if (flashTile != null) flashTile.SetBloomGlow(flashGreen, a); })); // fade the mobile overlay out with the flash
                    // Hold the magenta primed takeover (sprite swap + per-frame pulse) until
                    // this tile's green flash has played through, so it doesn't get stomped.
                    // The hold DEFERS the magenta primed takeover until the green flash
                    // has settled to white — which is exactly the white gap. When the
                    // flash is ALREADY the primed colour there is nothing to protect, so
                    // let the primed visual apply immediately and the tile just stays lit.
                    // Hold in BOTH modes: the tween owns sr.color for its whole run, and the
                    // primed loop repainting mid-tween is what made the old attempt stutter.
                    tile.HoldPrimedVisual(delay + SCORE_FLASH_HOLD + SCORE_FLASH_FADE + 0.02f);
                }

                tile.transform
                    .DOPunchScale(Vector3.one * punch, SCORE_POP_DURATION, 1, 0.5f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack)
                    .OnKill(() => {
                        // ON KILL, not just on complete. The lift to FX_TILE_ORDER (60) was
                        // undone in OnComplete — so any tween KILL left the tile stranded above
                        // the whole board, and its shaded top cap drew over the row sitting on
                        // top of it. Gravity, the landing squish and SnapAllTilesToCells all
                        // DOKill tile tweens, and a cascade word meets all three. OnKill fires
                        // on completion too, so this covers both. ReassertSortingOrder rather
                        // than BaseSortingOrder, so a spotlight or aim-mode lift still wins.
                        // 2026-09-10 Spencer.
                        if (tile != null) tile.ReassertSortingOrder();
                    })
                    .OnComplete(() => {
                        if (tile != null) {
                            tile.ReassertSortingOrder();
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
            // The primed chime belongs HERE, not at the call sites. Five different places call
            // PlayWordScored and only three of them played it, so the same event sounded
            // different depending on which path produced the word — "one makes a different
            // sound". One owner, one sound. 2026-09-10 Spencer.
            // QUICK POP (2026-10-23): no separate "prime" chime on the full board — every word pops on its own now, so it was a
            // second sound for one event. The pop (PlayMatchLine) carries it, pitched by word length.
            if (!(HandManager.QUICK_POP && SurvivalManager.FullBoardActive)) GameAudio.Instance?.PlayTilePrimed();
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

//             WDLog.Log($"[FuseTrace] Drew {directPositions.Count * chainPositions.Count} trace(s) " +
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
            lr.sortingOrder = 40;  // was 20; re-based above the row bands (tiles reach 37) 2026-10-20
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
            WDLog.Log($"[DiffusePop] deferred fired: requested={tiles.Count} popped={popped}");
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
            WDLog.Log($"[Tier3Burst] FIRE center={center} span={spanUnits:F2} scale={b:F2} (fb={(FlipbookExplosion.Instance!=null)} light={(LightingSetup.Instance!=null)} parts={(GameParticles.Instance!=null)})");

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
        /// <summary>Tiles needed for the big-pop hitstop. Keep it ABOVE a typical web
        /// detonation or the time-freeze fires constantly and reads as stutter.</summary>
        public const int BIG_POP_TILE_THRESHOLD = 20;
        public static bool SKIP_BIGPOP_ON_SPECIALS = true;

        public static IEnumerator MaybeBigPopAndHold(List<Tile> tiles, bool specialsFired = false)
        {
            int totalTiles = tiles != null ? tiles.Count : 0;
            // 2026-10-22 Spencer: "the SFX for the bomb is triggering before the actual explosion animation". A bomb's 5x5
            // usually crosses the 20-tile gate, so this wind-up (pre-boom + ~0.15s pucker + bubble) ran AFTER the bomb's
            // fireball and bang had already fired (ApplySpecialStep) and BEFORE the tiles popped — bang, pause, pop. When a
            // special fired this step, the blast IS the big moment: no wind-up, the tiles go with the bang.
            if (specialsFired && SKIP_BIGPOP_ON_SPECIALS) { WDLog.Log($"[BigPop] gate: tiles={totalTiles} → skip (special fired)"); yield break; }
            // 2026-09-03 swap-pivot: threshold was 8. In the DROP game a word popped 3-5
            // tiles, so 8+ was a rare celebration. With ARMED WEBS a tap routinely fires
            // 9-16 tiles (one measured 41), so the hitstop — which sets Time.timeScale=0 —
            // was freezing the game on MOST pops and reading as stutter/pausing.
            bool fire = totalTiles >= BIG_POP_TILE_THRESHOLD;
            WDLog.Log($"[BigPop] gate: tiles={totalTiles} → {(fire ? "FIRE+hold" : "skip")}");
            if (!fire) yield break;

            // Skip the pre-windup BOOM when a multi-word combo is about to STAGGER — otherwise the boom
            // front-loads BEFORE the rising chain pops, making the crescendo backwards (BOOM → little pops).
            // The stagger fires its own boom at the FINALE instead, so the audio BUILDS to the climax. (The
            // visual pucker/bubble below still plays.) 2026-07-06 Spencer.
            bool comboPending = _pendingCascadeWords != null && _pendingCascadeWords.Count >= 2;
            if (!comboPending)
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
            // BIG-CHAIN DIM (2026-10-28, RK/RM clips): the board darkens under the cluster while it puckers - the popping
            // tiles are lifted above the veil, so they read as a spotlight - and the veil lifts as they burst.
            if (BIG_POP_DIM && Instance != null)
            {
                for (int i = 0; i < tiles.Count; i++) if (tiles[i] != null) tiles[i].LiftForFx(hold + 0.15f);
                Instance.BoardDim(BOARD_DIM_ALPHA * 0.8f, hold);
            }
            yield return new WaitForSecondsRealtime(hold);
            if (BIG_POP_DIM && Instance != null) Instance.BoardDimLift();

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

        // COMBO COLOUR ESCALATION ramp (2026-07-27 Spencer). Each successive word in a blast lights up
        // a different shade in the split-second before it pops, so the chain reads as a rolling hue
        // escalation. Indexed by (chainStep - 1): chainStep 0 = the TRIGGER word, deliberately SKIPPED so
        // it keeps its green scored-flash. These are HDR TINTS that multiply the pink primed sprite
        // (primed_test@2x ≈ 246,135,233), so the on-screen hue is sprite×tint. Tune freely.
        /// <summary>Per-word hue escalation through a chain. OFF (2026-09-08) — it read as
        /// arbitrary colour flicker against the one-meaning-per-hue palette.</summary>
        public static bool COMBO_COLOUR_RAMP = false;

        // 2026-09-08 Spencer: even with the RAMP off, chain words 2+ were still being
        // repainted to s_comboDetonationRamp[0] (hot pink) — a DIFFERENT colour from the
        // trigger word's magenta, and it stomped gold words too. So the board still read as
        // "each successive word changes colour". COMBO_TINT_OVERRIDE=false stops the repaint
        // entirely: every word detonates in the colour it was ARMED in (magenta, or gold for
        // a high-value word). The chain still escalates through scale, shake, pitch, timing.
        public static bool COMBO_TINT_OVERRIDE = false;

        private static readonly Color[] s_comboDetonationRamp = new Color[]
        {
            new Color(2.20f, 0.50f, 0.95f, 1f), // step 1 — hot pink (trigger stays magenta; chain ramps above it)
            new Color(1.40f, 0.45f, 2.10f, 1f), // step 2 — violet
            new Color(1.10f, 0.50f, 2.40f, 1f), // step 3 — blue-violet
            new Color(0.90f, 0.50f, 2.60f, 1f), // step 4+ — deep blue-violet
        };

        // wordFlash: true for tiles that form a detonating WORD (they get the green cascade-flash);
        // false for pure splash/collateral tiles (they stay white per design). 2026-06-23.
        // comboStagger: true ONLY on the staggered multi-word combo paths, where chainStep is a clean
        // 0-based per-word order (0 = trigger, 1,2,3… = chained words). Gates the combo colour escalation
        // so a single-word detonation — whose chainStep is an unrelated counter — never mis-tints. 2026-07-27.
        // ── SPECIAL TILE FX (2026-10-21, placeholder art — motion first, Spencer's sprites later) ──────────────────
        private static Material s_specialFxMat;
        private static Sprite s_rocketSprite, s_ringSprite, s_beamSprite;
        public static float AIRPLANE_TAKEOFF = 0.16f;   // lift-off + wobble before it heads off (HandManager adds this to the target's pop time)
        public static float AIRPLANE_FLIGHT = 0.45f;   // 11-06 Spencer: the board froze ~1 s while the book flew (gravity waits for the landing) - 0.8 -> 0.45. LATER: let the board fall during the flight (RM)   // paper-plane glide is unhurried   // seconds the airplane takes to reach its target (HandManager delays that tile to match)
        private Material SpecialFxMat()
        {
            if (s_specialFxMat == null)
            {
                Shader sh = Shader.Find("WordDrop/AdditiveSprite") ?? Resources.Load<Shader>("Shaders/AdditiveSprite");
                if (sh != null) s_specialFxMat = new Material(sh) { name = "SpecialFxAdditive" };
            }
            return s_specialFxMat;
        }
        public static float ROCKET_HEAD_FLIGHT = 0.30f;
        /// <summary>TRAVELLING ROCKET (2026-10-28, Spencer's RK/RM clips, ~/Documents/animations/_findings): the heads fly at
        /// ~17-30 tiles/s, each tile breaks about a frame after its head passes, and the flame line grows WITH the heads then
        /// lingers ~0.35s. false = the 10-22 look (the whole line flashes at once, tiles go together).</summary>
        public static bool  ROCKET_TRAVEL = true;
        public static float ROCKET_LINGER = 0.35f;
        /// <summary>Seconds after the fire until a rocket head reaches a cell <paramref name="dist"/> cells away. The head is a
        /// DOMove over the line's full length (<paramref name="lineCells"/>) in ROCKET_HEAD_FLIGHT with Ease.InQuad, so
        /// progress p = (t/T)^2 and t = T*sqrt(dist/lineCells); +1 frame so the tile breaks just behind the head.</summary>
        public static float RocketArriveAt(float dist, int lineCells)
            => dist <= 0 ? 0f : ROCKET_V2 ? dist / ROCKET_SPEED + 0.017f
             : ROCKET_HEAD_FLIGHT * Mathf.Sqrt(Mathf.Clamp01(dist / (float)Mathf.Max(1, lineCells))) + 0.033f;

        // ── ROCKET V2 (2026-11-07, Spencer: "rebuild the rocket" - ours read as a thin white laser next to Royal Match's). Measured
        // RM (royalmatch(1-20).mov 56.69-57.25 s): the rocket tile SPLITS into two big halves that shoot apart (small white puff at
        // the centre); each half stays clearly visible, nose first, at ~25-30 cells/s from the first frame (no ease-in); a hot
        // white-yellow streak ~1/3 cell thick joins it back towards the start and burns away from the start end ~0.2 s behind
        // it, flecked with small sparks; the pieces break as the half reaches them. No full-line flash. ROCKET_V2 = false brings
        // back the 10-28 look (whole-line HDR beam + glow blob heads, InQuad). Tile pops follow RocketArriveAt either way.
        public static bool  ROCKET_V2 = true;
        public static float ROCKET_SPEED = 26f;          // cells per second, constant
        public static float ROCKET_TRAIL_DELAY = 0.20f;  // the streak's tail leaves the start this long after the head (~5 cells of streak)
        // 11-07 pass 2 (Movie_007): over the cream tiles the additive streak read as a thin WHITE line - core is now yellower and
        // thicker, the glow wider and stronger, so it reads warm yellow-white like RM's.
        public static float ROCKET_TRAIL_CORE_W = 0.30f, ROCKET_TRAIL_GLOW_W = 0.85f;   // thickness, fractions of a cell
        public static Color ROCKET_TRAIL_CORE = new Color(1.55f, 1.3f, 0.55f, 1f);   // yellow-white, under the bloom blow-out
        public static Color ROCKET_TRAIL_GLOW = new Color(1.45f, 0.72f, 0.12f, 1f);   // orange-gold glow around it
        public static Color ROCKET_EXHAUST = new Color(1.3f, 0.75f, 0.25f, 1f);       // was (2.0,1.3,0.45) - bloomed to a white blob
        public static float ROCKET_EXHAUST_SIZE = 0.34f;                              // x cell (was 0.5)
        public static float ROCKET_ART_SIZE = 1.05f;     // the flying half, x the board tile size (RM halves are ~a full piece long)

        private void PlayRocketV2(RulesEngine.SpecialFire f, Vector3 origin, float cell, bool horizontal)
        {
            var grid = GridManager.Instance; if (grid == null) return;
            GameAudio.Instance?.PlayRocketFire();
            // the split: a small white puff where the rocket sat
            var puff = MakeFxSprite("RocketPuff", SoftDot(), new Color(1.15f, 1.1f, 0.95f, 1f), 47);   // 11-07 pass 2: was 1.6 - bloomed white
            puff.transform.position = origin; puff.transform.localScale = Vector3.one * cell * 0.6f;
            puff.transform.DOScale(cell * 1.4f, 0.14f).SetEase(Ease.OutQuad);
            puff.DOFade(0f, 0.16f).SetEase(Ease.InQuad).OnComplete(() => { if (puff != null) Destroy(puff.gameObject); });
            for (int sgn = -1; sgn <= 1; sgn += 2)
            {
                Vector3 dir = horizontal ? Vector3.right * sgn : Vector3.up * sgn;
                int at = horizontal ? f.Cell.x : f.Cell.y, n = horizontal ? RulesEngine.COLS : RulesEngine.ROWS;
                float exitCells = (sgn > 0 ? (n - 1 - at) : at) + 1.2f;   // flies just past the board edge
                StartCoroutine(RocketHeadV2(origin, dir, exitCells, cell));
            }
            GridManager.Instance?.ShakeBoard(0.08f, 0.16f);
        }

        private System.Collections.IEnumerator RocketHeadV2(Vector3 origin, Vector3 dir, float exitCells, float cell)
        {
            float speed = ROCKET_SPEED * cell, travel = exitCells * cell, tHead = travel / speed;
            // the half: the board's own rocket sprite (crisp, not additive), nose along the travel direction
            SpriteRenderer art = null; float artUp = Tile.ROCKET_ART_ANGLE;
            var tsp = Tile.SpecialTileSprite(RulesEngine.SPECIAL_ROCKET_V);
            if (tsp != null && Tile.NormalSprite != null)
            {
                var ago = new GameObject("RocketHalf"); art = ago.AddComponent<SpriteRenderer>(); art.sprite = tsp; art.sortingOrder = 50;
                ago.transform.localScale = Vector3.one * (cell * Tile.TILE_DISPLAY_RATIO * ROCKET_ART_SIZE / Mathf.Max(0.0001f, Tile.NormalSprite.bounds.size.x));
                artUp = 90f;
            }
            else art = MakeIconSprite("RocketHalf", RulesEngine.SPECIAL_ROCKET_V, cell * ROCKET_ART_SIZE, 50);
            float dirDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            if (art != null) { art.transform.rotation = Quaternion.Euler(0f, 0f, dirDeg - artUp); art.transform.position = origin; }
            var exhaust = MakeFxSprite("RocketExhaust", SoftDot(), ROCKET_EXHAUST, 49);
            var beam = SoftBeam();
            var glow = MakeFxSprite("RocketTrailGlow", beam, ROCKET_TRAIL_GLOW, 46);
            var core = MakeFxSprite("RocketTrailCore", beam, ROCKET_TRAIL_CORE, 47);
            glow.transform.rotation = core.transform.rotation = Quaternion.Euler(0f, 0f, dirDeg);
            Vector3 perp = new Vector3(-dir.y, dir.x, 0f);
            float t = 0f, nextFleck = 0f, life = tHead + ROCKET_TRAIL_DELAY + 0.05f;
            while (t < life)
            {
                t += Time.deltaTime;
                float headD = Mathf.Min(travel, speed * t);
                float tailD = Mathf.Clamp(speed * (t - ROCKET_TRAIL_DELAY), 0f, headD);
                Vector3 head = origin + dir * headD;
                if (art != null)
                {
                    art.transform.position = head + new Vector3(0f, 0f, -0.02f);
                    if (t >= tHead) { Destroy(art.gameObject); art = null; }   // gone past the edge
                }
                if (exhaust != null)
                {
                    exhaust.transform.position = head - dir * cell * 0.42f;
                    exhaust.transform.localScale = Vector3.one * cell * (ROCKET_EXHAUST_SIZE + 0.08f * Mathf.PerlinNoise(t * 40f, dir.x + 3f));
                    if (t >= tHead) { Destroy(exhaust.gameObject); exhaust = null; }
                }
                // streak from the tail up to just behind the half; the ends taper (SoftBeam), it thins as it burns away
                float len = Mathf.Max(0.001f, headD - tailD - cell * 0.3f);
                Vector3 mid = origin + dir * (tailD + len * 0.5f);
                float burn = Mathf.Clamp01((t - tHead) / Mathf.Max(0.01f, ROCKET_TRAIL_DELAY));   // after the half has left
                float flick = 0.9f + 0.2f * Mathf.PerlinNoise(t * 30f, dir.y + 7f);
                core.transform.position = glow.transform.position = mid;
                core.transform.localScale = new Vector3(len, cell * ROCKET_TRAIL_CORE_W * 2f * (1f - 0.6f * burn), 1f);
                glow.transform.localScale = new Vector3(len, cell * ROCKET_TRAIL_GLOW_W * 2f * flick * (1f - 0.4f * burn), 1f);
                core.color = new Color(1f, 1f, 1f, 1f - burn * burn);
                glow.color = new Color(1f, 1f, 1f, 0.85f * (1f - burn));
                // flecks: small warm sparks + the odd little white star, shed just behind the half
                if (t < tHead && t >= nextFleck)
                {
                    nextFleck = t + 0.016f;
                    Vector3 sp = head - dir * cell * 0.5f + perp * Random.Range(-0.22f, 0.22f) * cell;
                    SpawnSpark(sp, (-dir * Random.Range(0.4f, 1.6f) + perp * Random.Range(-1.4f, 1.4f)) * cell,
                        cell * Random.Range(0.07f, 0.13f), Random.Range(0.18f, 0.32f), Random.value < 0.55f ? ROCKET_FLAME : new Color(2.4f, 2.2f, 1.6f, 1f));
                    if (Random.value < 0.25f && GameParticles.Instance != null)
                        GameParticles.Instance.EmitTrailStar(sp, cell * Random.Range(0.16f, 0.28f), (Vector3)Random.insideUnitCircle * cell * 0.5f);
                }
                yield return null;
            }
            if (art != null) Destroy(art.gameObject);
            if (exhaust != null) Destroy(exhaust.gameObject);
            Destroy(core.gameObject); Destroy(glow.gameObject);
        }
        public static float ROCKET_LINE_LIFE   = 0.42f;   // measured: the flame lingers ~0.4s
        public static Color ROCKET_CORE  = new Color(3.2f, 3.0f, 2.6f, 1f);   // white-hot
        public static Color ROCKET_FLAME = new Color(2.6f, 1.25f, 0.25f, 1f); // orange-yellow flame
        private static Sprite s_softBeam, s_softDot;

        /// <summary>Soft-edged strip: gaussian falloff across its thickness, soft ends — a glow line, not a hard bar.</summary>
        public static Sprite SoftBeam()
        {
            if (s_softBeam != null) return s_softBeam;
            int w = 64, h = 32; var t = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float v = (y + 0.5f) / h * 2f - 1f, u = (x + 0.5f) / w * 2f - 1f;
                float a = Mathf.Exp(-v * v * 5f) * Mathf.Clamp01((1f - Mathf.Abs(u)) * 6f);
                px[y * w + x] = new Color(1f, 1f, 1f, a);
            }
            t.SetPixels(px); t.Apply();
            s_softBeam = Sprite.Create(t, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 64f);   // 1 x 0.5 units
            return s_softBeam;
        }
        public static Sprite SoftDot()
        {
            if (s_softDot != null) return s_softDot;
            int n = 32; var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                px[y * n + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy)) );
            }
            t.SetPixels(px); t.Apply();
            s_softDot = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 32f);   // 1 unit
            return s_softDot;
        }

        private System.Collections.IEnumerator RocketBlastLine(Vector3 centre, Vector3 origin, bool horizontal, float length, float cell)
        {
            var beam = SoftBeam();
            var core  = MakeFxSprite("RocketCore",  beam, ROCKET_CORE,  48);
            var flame = MakeFxSprite("RocketFlame", beam, ROCKET_FLAME, 47);
            float rot = horizontal ? 0f : 90f;
            foreach (var s in new[] { core, flame }) { s.transform.position = centre; s.transform.rotation = Quaternion.Euler(0f, 0f, rot); }
            float coreW = cell * 0.55f, flameW = cell * 1.25f;
            // sparks shed along the line, drifting outward from the rocket
            int nSparks = Mathf.RoundToInt(length / cell * (HandManager.BonusBlastRunning ? 0.8f : 1.6f));   // fewer per rocket in the bonus
            for (int i = 0; i < nSparks; i++)
            {
                float along = Random.Range(-0.5f, 0.5f) * length;
                Vector3 axis = horizontal ? Vector3.right : Vector3.up, perp = horizontal ? Vector3.up : Vector3.right;
                Vector3 p = centre + axis * along + perp * Random.Range(-0.3f, 0.3f) * cell;
                Vector3 vel = (axis * Mathf.Sign(Vector3.Dot(p - origin, axis)) * Random.Range(1.5f, 4f) + perp * Random.Range(-1.2f, 1.2f)) * cell;
                SpawnSpark(p, vel, cell * Random.Range(0.12f, 0.22f), Random.Range(0.25f, 0.5f),
                    Random.value < 0.5f ? ROCKET_FLAME : new Color(3f, 2.4f, 0.8f, 1f));
            }
            float t = 0f;
            float life = ROCKET_TRAVEL ? ROCKET_HEAD_FLIGHT + ROCKET_LINGER : ROCKET_LINE_LIFE;
            Vector3 ax = horizontal ? Vector3.right : Vector3.up;
            float lo = -length * 0.5f, hi = length * 0.5f;                            // line extent along the axis, about centre
            float o = Vector3.Dot(origin - centre, ax);                                // where the rocket sits on it
            while (t < life)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / life);
                float flick = 0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 30f, origin.x * 7f);
                float len = length; Vector3 mid = centre;
                if (ROCKET_TRAVEL)
                {   // the flame stretches out WITH the two heads (same InQuad as their DOMove), then lingers and thins
                    float p = Mathf.Clamp01(t / ROCKET_HEAD_FLIGHT); p *= p;
                    float reach = length / 1.15f * p;
                    float a = Mathf.Max(lo, o - reach), b = Mathf.Min(hi, o + reach);
                    len = Mathf.Max(0.001f, b - a); mid = centre + ax * ((a + b) * 0.5f);
                    float kk = Mathf.Clamp01((t - ROCKET_HEAD_FLIGHT * 0.5f) / (life - ROCKET_HEAD_FLIGHT * 0.5f));   // thin after the heads are well out
                    k = kk;
                }
                else len = length * Mathf.Clamp01(t / 0.05f);                          // the line snaps across in 0.05s
                core.transform.position = flame.transform.position = mid;
                core.transform.localScale  = new Vector3(len, coreW * (1f - k) * 2f / 1f, 1f);
                flame.transform.localScale = new Vector3(len, flameW * (1f - k * 0.8f) * flick * 2f, 1f);
                core.color  = new Color(1f, 1f, 1f, 1f - Mathf.Clamp01(k * 1.8f));     // core burns out first
                flame.color = new Color(1f, 1f, 1f, 1f - k * k);                       // flame lingers, thinning
                yield return null;
            }
            Destroy(core.gameObject); Destroy(flame.gameObject);
        }

        public static float FIREBALL_LIFE = 0.40f;
        // 11-07 (Spencer: explosions "confusing cause theres just so much going on" - measured vs Royal Match's TNT, which is one hot
        // core bursting into chunks): the hollow shock ring is off, and tiles a blast CATCHES no longer each fire their own sparkle
        // spray (a 20-tile TNT = 20 sparkle bursts over the fireball). Word tiles still sparkle. true = the old look.
        public static bool BLAST_RING = false;
        public static bool BLAST_COLLATERAL_SPARKLES = false;
        /// <summary>Camera shake cap (world units). The view is ~9.2 wide; overscan 1.15 leaves ~0.69 per side. Was uncapped up
        /// to 1.2, which slid the background off-screen and showed white (2026-10-22).</summary>
        public static float CAMERA_SHAKE_MAX = 0.6f;
        public static Color FIREBALL_CORE  = new Color(1.6f, 1.4f, 1.05f, 1f);   // warm white, under the bloom wash-out
        public static Color FIREBALL_FLAME = new Color(1.9f, 0.85f, 0.18f, 1f);  // orange
        // SUN RAYS + GLINT (2026-10-22 Spencer: "on the explosion there is a sunrays effect ... i have a lot of sprites that
        // might help"). From his packs, already in Resources/Particles: vfx_rays_sharp (long thin rays) and flare (4-point star).
        public static bool  FIREBALL_SUNRAYS = true;
        // 2026-10-22 Spencer: the wide wedge rays (vfx_rays_sharp) looked weird — "there should be some thinner rays in my
        // project". VFX_Getting_Weapon_Rays (Particle VFX pack) = fine, wispy, soft-tapered rays.
        public static string SUNRAY_SPRITE = "vfx_thin_rays";   // was "vfx_rays_sharp"
        public static Color SUNRAY_COLOR = new Color(1.8f, 1.35f, 0.55f, 1f);   // warm gold, softer for thin rays
        public static float SUNRAY_LIFE  = 0.55f;
        private static Sprite s_sunrays, s_glint; private static bool s_raysTried;
        private static void LoadRaySprites()
        {
            if (s_raysTried) return; s_raysTried = true;
            var r = Resources.Load<Texture2D>("Particles/" + SUNRAY_SPRITE);
            if (r != null) s_sunrays = Sprite.Create(r, new Rect(0, 0, r.width, r.height), new Vector2(0.5f, 0.5f), r.width);   // 1 unit
            var g = Resources.Load<Texture2D>("Particles/flare");
            if (g != null) s_glint = Sprite.Create(g, new Rect(0, 0, g.width, g.height), new Vector2(0.5f, 0.5f), g.width);
        }
        private System.Collections.IEnumerator SunRays(Vector3 centre, float radius)
        {
            LoadRaySprites();
            SpriteRenderer rays = s_sunrays != null ? MakeFxSprite("SunRays", s_sunrays, SUNRAY_COLOR, 46) : null;
            SpriteRenderer glint = s_glint != null ? MakeFxSprite("Glint", s_glint, ROCKET_CORE, 49) : null;
            float spin = Random.value < 0.5f ? -40f : 40f, rot0 = Random.Range(0f, 360f);
            if (rays != null) rays.transform.position = centre + new Vector3(0f, 0f, 0.05f);
            if (glint != null) glint.transform.position = centre - new Vector3(0f, 0f, 0.05f);
            float t = 0f;
            while (t < SUNRAY_LIFE)
            {
                t += Time.deltaTime; float k = Mathf.Clamp01(t / SUNRAY_LIFE);
                if (rays != null)
                {
                    float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.18f), 3f);
                    rays.transform.localScale = Vector3.one * radius * (1.2f + 2.2f * grow);
                    rays.transform.rotation = Quaternion.Euler(0f, 0f, rot0 + spin * t);
                    rays.color = new Color(1f, 1f, 1f, k < 0.35f ? 1f : 1f - (k - 0.35f) / 0.65f);
                }
                if (glint != null)
                {   // bright 4-point star: pops big at the instant of detonation, shrinks away in ~0.2s
                    float g = Mathf.Clamp01(t / 0.2f);
                    glint.transform.localScale = Vector3.one * radius * 2.4f * (g < 0.25f ? g / 0.25f : 1f - (g - 0.25f) / 0.75f);
                    glint.transform.rotation = Quaternion.Euler(0f, 0f, rot0 * 0.25f);
                    glint.color = new Color(1f, 1f, 1f, 1f - g);
                }
                yield return null;
            }
            if (rays != null) Destroy(rays.gameObject);
            if (glint != null) Destroy(glint.gameObject);
        }
        /// <summary>Spherical blast in the rocket palette: core snaps out in ~0.07s and burns out first, flame sphere
        /// swells to the blast radius, flickers and thins; sparks fly out radially (pooled).</summary>
        // ── BLAST V2 (11-19, Spencer: bomb + dynamite to look "more like around 22:56-22:59" of his Royal Match recording, "the VFX
        // layer with the smoke and particle effects"; he picked the mock bomb_vfx_mock_v2.png, with a worry about "the big smoke
        // flume"). MEASURED there: a 1-cell white core flash -> a lobed cream cloud with a warm rim, full size in ~0.1 s and
        // bright ~0.3 s -> chunks thrown out, tumbling and falling -> the cloud breaks into small puffs and pale haze with
        // confetti, ~0.8 s in all. No dark smoke, no screen dim. Textures: Resources/FX/blast_*.png (bake script in
        // ~/Documents/WordDrop_Art/bomb_vfx_mock_1119). The cloud lands with the bang and the tile burst (BANG_ON_BURST).
        // BLAST_V2 = false -> the old rays + flame exactly as before. Every size / time below is a knob.
        public static bool  BLAST_V2 = true, BLAST_V2_RAYS = false;
        public static float BLAST_V2_FLAME = 0.55f;          // the old orange flame sphere, shrunk to a core flash
        public static float BLAST_CLOUD_SIZE = 0.80f;        // cloud width as a fraction of the cleared square's width
        public static float BLAST_CLOUD_ALPHA = 0.95f, BLAST_CLOUD_IN = 0.10f, BLAST_CLOUD_HOLD = 0.08f, BLAST_CLOUD_OUT = 0.20f;   // v3: gone before the tiles above start to fall (was hold .16 / out .24)
        public static int   BLAST_CLOUD_PUFFS = 9, BLAST_PUFFS = 5, BLAST_WISPS = 5, BLAST_CONFETTI = 22, BLAST_CHUNKS = 11;
        public static float BLAST_HAZE_ALPHA = 0.38f, BLAST_HAZE_LIFE = 0.55f;
        private static readonly Dictionary<string, Sprite> s_blastSprites = new Dictionary<string, Sprite>();
        private static Sprite s_blastSquare;
        private static Sprite BlastSprite(string name)
        {
            if (s_blastSprites.TryGetValue(name, out var sp)) return sp;
            var tex = Resources.Load<Texture2D>("FX/" + name);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width) : null;   // 1 world unit wide at scale 1
            return s_blastSprites[name] = sp;
        }
        private static SpriteRenderer BlastQuad(string name, Sprite sp, Vector3 pos, float size, int order, Color c)
        {
            var go = new GameObject(name); go.transform.position = pos; go.transform.localScale = Vector3.one * size;
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = sp; sr.sortingOrder = order; sr.color = c;
            return sr;
        }
        /// <summary>One flying / drifting piece: moves by vel (+ gravity), spins, scales s0 -> s1, fades out over the last part of its life.</summary>
        private System.Collections.IEnumerator BlastBit(SpriteRenderer sr, Vector3 vel, float gravity, float spin, float life, float fadeFrom, float s0, float s1, float alpha)
        {
            float t = 0f; var tr = sr.transform; Color c = sr.color;
            while (t < life && sr != null)
            {
                float dt = Time.deltaTime; t += dt; float k = Mathf.Clamp01(t / life);
                vel.y -= gravity * dt; tr.position += vel * dt; tr.Rotate(0f, 0f, spin * dt);
                tr.localScale = Vector3.one * Mathf.Lerp(s0, s1, 1f - (1f - k) * (1f - k));
                c.a = alpha * (k < fadeFrom ? 1f : 1f - (k - fadeFrom) / Mathf.Max(0.001f, 1f - fadeFrom)); sr.color = c;
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }
        private System.Collections.IEnumerator BlastV2(Vector3 centre, float radius, float cell)
        {
            bool bonus = HandManager.BonusBlastRunning;
            if (!bonus && BANG_ON_BURST > 0f) yield return new WaitForSecondsRealtime(BANG_ON_BURST);   // with the bang + the tile burst
            float dia = radius * 2f * BLAST_CLOUD_SIZE;
            // 1 CLOUD (v3, 11-19 after Movie_022 - Spencer: "isnt quite the same as royal match"; the single outlined cloud sprite read
            // as a flat sticker that scaled as one piece). Now: a white-hot flash + a warm glow that BLOOM (HDR, additive), and the
            // body is ~9 soft feathered puffs, each bursting outward on its own, so the edge boils and breaks up by itself.
            var dot = SoftDot();
            var flash = MakeFxSprite("BlastFlash", dot, new Color(3.2f, 3.0f, 2.4f, 1f), 50); flash.transform.position = centre;
            StartCoroutine(BlastBit(flash, Vector3.zero, 0f, 0f, 0.14f, 0.0f, dia * 0.5f, dia * 1.25f, 1f));
            var glow = MakeFxSprite("BlastGlow", dot, new Color(2.0f, 1.35f, 0.45f, 1f), 48); glow.transform.position = centre;
            StartCoroutine(BlastBit(glow, Vector3.zero, 0f, 0f, 0.34f, 0.15f, dia * 0.9f, dia * 1.9f, 0.85f));
            var soft = BlastSprite("blast_soft");
            if (soft != null)
            {
                float life = BLAST_CLOUD_IN + BLAST_CLOUD_HOLD + BLAST_CLOUD_OUT;
                for (int i = 0; i < BLAST_CLOUD_PUFFS; i++)
                {
                    bool mid = i == 0;
                    float ang = (i + Random.Range(-0.35f, 0.35f)) / (BLAST_CLOUD_PUFFS - 1) * Mathf.PI * 2f; Vector3 d = mid ? Vector3.zero : new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                    float off = dia * Random.Range(0.24f, 0.34f), sz = dia * (mid ? 0.78f : Random.Range(0.46f, 0.62f)), l = life * Random.Range(0.85f, 1.1f);
                    var sr = BlastQuad("BlastCloudPuff", soft, centre + d * off * 0.25f, sz * 0.3f, 49, new Color(1f, 1f, 1f, BLAST_CLOUD_ALPHA));
                    StartCoroutine(BlastBit(sr, d * off * 0.95f / l + Vector3.up * cell * 0.15f, 0f, 0f, l, (BLAST_CLOUD_IN + BLAST_CLOUD_HOLD) / life, sz * 0.3f, sz * 1.12f, BLAST_CLOUD_ALPHA));
                }
            }
            // 2 CHUNKS: big cream pieces thrown out, tumbling, falling past the board
            var ch = BlastSprite("blast_chunk");
            if (ch != null && !bonus)
                for (int i = 0; i < BLAST_CHUNKS; i++)
                {
                    Vector2 d = Random.insideUnitCircle.normalized; float sz = cell * Random.Range(0.42f, 0.70f);   // v3: bigger pieces
                    var sr = BlastQuad("BlastChunk", ch, centre + (Vector3)(d * radius * Random.Range(0.2f, 0.6f)), sz, 50, Color.white);
                    sr.transform.Rotate(0f, 0f, Random.Range(0f, 360f));
                    Vector3 v = new Vector3(d.x * Random.Range(3.5f, 7f), d.y * Random.Range(2.5f, 5f) + Random.Range(2f, 5f), 0f) * cell;
                    StartCoroutine(BlastBit(sr, v, 26f * cell, Random.Range(-540f, 540f), Random.Range(0.60f, 0.85f), 0.72f, sz, sz * 0.85f, 1f));
                }
            // 3 CONFETTI: small bits in the tile's own colours (cream, navy, gold)
            if (s_blastSquare == null) s_blastSquare = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, Texture2D.whiteTexture.width, Texture2D.whiteTexture.height), new Vector2(0.5f, 0.5f), Texture2D.whiteTexture.width);
            int nConf = bonus ? BLAST_CONFETTI / 3 : BLAST_CONFETTI;
            for (int i = 0; i < nConf; i++)
            {
                Vector2 d = Random.insideUnitCircle; float sz = cell * Random.Range(0.045f, 0.09f); float pick = Random.value;
                Color col = pick < 0.45f ? new Color(1f, 0.957f, 0.871f) : pick < 0.7f ? new Color(0.11f, 0.14f, 0.38f) : pick < 0.9f ? new Color(0.98f, 0.77f, 0.25f) : Color.white;
                var sr = BlastQuad("BlastConfetti", s_blastSquare, centre + (Vector3)(d * radius * 0.5f), sz, 50, col);
                Vector3 v = new Vector3(d.x * Random.Range(2f, 5f), d.y * Random.Range(2f, 4f) + Random.Range(1f, 3f), 0f) * cell;
                StartCoroutine(BlastBit(sr, v, 9f * cell, Random.Range(-720f, 720f), Random.Range(0.55f, 0.95f), 0.6f, sz, sz, 1f));
            }
            if (bonus) yield break;
            yield return new WaitForSeconds(BLAST_CLOUD_IN + BLAST_CLOUD_HOLD);
            // 4 a few small soft puffs keep drifting outward as it clears...
            if (soft != null)
                for (int i = 0; i < BLAST_PUFFS; i++)
                {
                    float ang = (i + Random.Range(-0.3f, 0.3f)) / BLAST_PUFFS * Mathf.PI * 2f; Vector3 d = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang) * 0.9f, 0f);
                    float sz = dia * Random.Range(0.20f, 0.30f);
                    var sr = BlastQuad("BlastPuff", soft, centre + d * dia * Random.Range(0.34f, 0.46f), sz, 49, new Color(1f, 1f, 1f, 0.6f));
                    StartCoroutine(BlastBit(sr, d * cell * Random.Range(0.9f, 1.8f) + Vector3.up * cell * 0.35f, 0f, 0f, Random.Range(0.26f, 0.36f), 0.1f, sz, sz * 1.5f, 0.6f));
                }
            // ...and pale HAZE over the cleared cells
            var ws = BlastSprite("blast_wisp");
            if (ws != null)
                for (int i = 0; i < BLAST_WISPS; i++)
                {
                    Vector3 p = centre + new Vector3(Random.Range(-0.7f, 0.7f), Random.Range(-0.6f, 0.6f), 0f) * radius; float sz = radius * Random.Range(1.1f, 1.7f);
                    var sr = BlastQuad("BlastWisp", ws, p, sz, 46, new Color(1f, 1f, 1f, BLAST_HAZE_ALPHA));
                    sr.transform.Rotate(0f, 0f, Random.Range(-25f, 25f));
                    StartCoroutine(BlastBit(sr, new Vector3(Random.Range(-0.3f, 0.3f), Random.Range(0.15f, 0.5f), 0f) * cell, 0f, Random.Range(-8f, 8f), BLAST_HAZE_LIFE * Random.Range(0.8f, 1.2f), 0.2f, sz * 0.8f, sz * 1.15f, BLAST_HAZE_ALPHA));
                }
        }
        public static float BANG_ON_BURST = 0.17f;   // seconds from the fireball's first frame to the bang (0 = with the first frame, as before)
        private System.Collections.IEnumerator Fireball(Vector3 centre, float radius, float cell, bool v2 = false)
        {
            v2 = v2 && BLAST_V2;   // 11-19 Spencer: "i only meant for the bombs and dynamite explosions" - only those two pass v2; combos keep the old fireball
            var dot = SoftDot();
            // 11-19 BANG ON THE BURST (Spencer: "the bomb is often off in timing with the sound"). MEASURED on Movie_021 + the log, 5
            // bombs: the bang fired with the first frame of the fireball's rays, but the thing the eye reads as the explosion -
            // the white flash and the word tiles + bomb bursting - came 11 frames (0.18 s) later, every time. The bang now waits
            // for that burst. Not in the end-of-level bonus (rapid fire, no build-up).
            if (BANG_ON_BURST > 0f && !HandManager.BonusBlastRunning)
                DOVirtual.DelayedCall(BANG_ON_BURST, () => GameAudio.Instance?.PlayBigExplosion(), ignoreTimeScale: true);
            else
            GameAudio.Instance?.PlayBigExplosion();   // bomb / TNT / combo fireball
            if (v2) StartCoroutine(BlastV2(centre, radius, cell));
            if (FIREBALL_SUNRAYS && !(v2 && !BLAST_V2_RAYS)) StartCoroutine(SunRays(centre, radius));
            // 2026-10-22 capture: the white-hot core bloomed into a flat white disc over the whole 5x5. Fireball core is now
            // dimmer (under the bloom wash-out) and smaller, the flame a touch softer — rays + glint carry the brightness.
            var flame = MakeFxSprite("FireballFlame", dot, FIREBALL_FLAME, 47);
            var core  = MakeFxSprite("FireballCore",  dot, FIREBALL_CORE,  48);
            flame.transform.position = core.transform.position = centre;
            int n = Mathf.RoundToInt(radius / cell * (HandManager.BonusBlastRunning ? 5f : 10f));
            for (int i = 0; i < n; i++)
            {
                Vector2 d = Random.insideUnitCircle.normalized;
                SpawnSpark(centre + (Vector3)(d * cell * 0.3f), (Vector3)(d * Random.Range(4f, 9f) * cell * (radius / (1.5f * cell))),
                    cell * Random.Range(0.12f, 0.24f), Random.Range(0.25f, 0.45f), Random.value < 0.5f ? ROCKET_FLAME : new Color(3f, 2.4f, 0.8f, 1f));
            }
            float t = 0f;
            while (t < FIREBALL_LIFE)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / FIREBALL_LIFE);
                float grow = 1f - Mathf.Pow(1f - Mathf.Clamp01(t / 0.12f), 3f);           // fast out-cubic swell
                float flick = 0.92f + 0.16f * Mathf.PerlinNoise(Time.time * 28f, centre.x * 5f);
                core.transform.localScale  = Vector3.one * radius * 1.1f * grow * (1f - 0.5f * k);
                flame.transform.localScale = Vector3.one * radius * 2.2f * grow * flick * (v2 ? BLAST_V2_FLAME : 1f);   // V2: a core flash, the cloud is the body
                core.color  = new Color(1f, 1f, 1f, 0.9f * (1f - Mathf.Clamp01(k * 3f)));   // core burns out fast
                flame.color = new Color(1f, 1f, 1f, 0.8f * (1f - k * k));                   // flame lingers, then thins
                yield return null;
            }
            Destroy(core.gameObject); Destroy(flame.gameObject);
        }

        /// <summary>11-04: gold star sparkles stream off each flying rocket head, on top of the embers. Seconds between stars;
        /// sizes are fractions of a cell. false = embers only.</summary>
        public static bool  ROCKET_STAR_TRAIL = true;
        public static float ROCKET_STAR_EVERY = 0.03f, ROCKET_STAR_MIN = 0.30f, ROCKET_STAR_MAX = 0.62f, ROCKET_STAR_HERO = 0.15f;
        private System.Collections.IEnumerator EmberTrail(Transform head, float life, float cell)
        {
            float t = 0f, nextStar = 0f;
            while (head != null && t < life)
            {
                t += Time.deltaTime;
                if (ROCKET_STAR_TRAIL && t >= nextStar && GameParticles.Instance != null)
                {   // left where the head is now, so the moving head draws a line of stars behind it; a few bigger "hero" stars
                    nextStar = t + ROCKET_STAR_EVERY;
                    float sz = cell * (Random.value < ROCKET_STAR_HERO ? Random.Range(ROCKET_STAR_MAX, ROCKET_STAR_MAX * 1.5f) : Random.Range(ROCKET_STAR_MIN, ROCKET_STAR_MAX));
                    GameParticles.Instance.EmitTrailStar(head.position + (Vector3)Random.insideUnitCircle * cell * 0.18f, sz, (Vector3)Random.insideUnitCircle * cell * 0.6f);
                }
                if ((Time.frameCount & 1) == 0)   // every other frame
                    SpawnSpark(head.position + (Vector3)Random.insideUnitCircle * cell * 0.12f,
                        (Vector3)Random.insideUnitCircle * cell * 0.8f, cell * Random.Range(0.10f, 0.18f), Random.Range(0.2f, 0.35f),
                        Random.value < 0.6f ? ROCKET_FLAME : ROCKET_CORE);
                yield return null;
            }
        }

        // ── POOLED SPARKS (2026-10-22 Spencer: "only issue is that im getting frame drops"). The first pass made a new
        // GameObject + coroutine per spark (~100 per rocket, 1000+ in a bonus). Now a fixed pool driven by ONE update loop:
        // nothing is created or destroyed mid-blast, and SPARK_CAP bounds the cost however big the chain. ──
        public static int SPARK_CAP = 160;
        private class SparkP { public Transform t; public SpriteRenderer sr; public Vector3 vel; public float size, life, age; public bool on; }
        private readonly System.Collections.Generic.List<SparkP> _sparks = new System.Collections.Generic.List<SparkP>();
        private MaterialPropertyBlock _sparkMpb;
        private int _sparkCursor;

        public void SpawnSpark(Vector3 pos, Vector3 vel, float size, float life, Color hdr)
        {
            SparkP sp = null;
            for (int n = 0; n < _sparks.Count; n++)
            {
                var c = _sparks[(_sparkCursor + n) % _sparks.Count];
                if (!c.on) { sp = c; _sparkCursor = (_sparkCursor + n + 1) % _sparks.Count; break; }
            }
            if (sp == null)
            {
                if (_sparks.Count >= SPARK_CAP) return;   // at the cap: skip, never stall
                var r = MakeFxSprite("Spark", SoftDot(), hdr, 48);
                r.transform.SetParent(transform, true);
                sp = new SparkP { t = r.transform, sr = r };
                _sparks.Add(sp);
            }
            if (_sparkMpb == null) _sparkMpb = new MaterialPropertyBlock();
            sp.sr.GetPropertyBlock(_sparkMpb); _sparkMpb.SetColor("_Color", hdr); sp.sr.SetPropertyBlock(_sparkMpb);
            sp.t.position = pos; sp.vel = vel; sp.size = size; sp.life = life; sp.age = 0f; sp.on = true;
            sp.t.localScale = Vector3.one * size; sp.sr.color = Color.white;
            if (!sp.t.gameObject.activeSelf) sp.t.gameObject.SetActive(true);
        }

        private void TickSparks()
        {
            float dt = Time.deltaTime;
            for (int i = 0; i < _sparks.Count; i++)
            {
                var sp = _sparks[i]; if (!sp.on) continue;
                sp.age += dt;
                if (sp.age >= sp.life || sp.t == null) { sp.on = false; if (sp.t != null) sp.t.gameObject.SetActive(false); continue; }
                sp.t.position += sp.vel * dt; sp.vel *= (1f - 3f * dt);
                float k = sp.age / sp.life;
                sp.sr.color = new Color(1f, 1f, 1f, 1f - k);
                sp.t.localScale = Vector3.one * sp.size * (1f - 0.5f * k);
            }
        }

        private void LateUpdate() { if (_sparks.Count > 0) TickSparks(); }

        public SpriteRenderer MakeFxSprite(string name, Sprite sprite, Color hdr, int order)
        {
            var go = new GameObject(name);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            var mat = SpecialFxMat();
            if (mat != null)
            {
                sr.sharedMaterial = mat;
                var mpb = new MaterialPropertyBlock(); sr.GetPropertyBlock(mpb); mpb.SetColor("_Color", hdr); sr.SetPropertyBlock(mpb);
                sr.color = Color.white;
            }
            else sr.color = new Color(Mathf.Clamp01(hdr.r), Mathf.Clamp01(hdr.g), Mathf.Clamp01(hdr.b), 1f);
            sr.sortingOrder = order;   // 48: above every tile row band (max 37), with the other board FX
            return sr;
        }

        /// <summary>Fly a special's icon from <paramref name="from"/> to <paramref name="to"/> on a little arc, shrinking into the
        /// tile — used by the bonus blast to launch rockets out of the MOVES counter. Destroys itself on arrival.</summary>
        public Tween FlyIcon(int kind, Vector3 from, Vector3 to, float dur)
        {
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
            var sr = MakeIconSprite("BonusRocketFly", kind, cell * 0.9f, 60);
            if (sr == null) return null;
            sr.transform.position = from;
            var go = sr.gameObject;
            Vector3 mid = Vector3.Lerp(from, to, 0.5f) + new Vector3(0f, cell * 1.2f, 0f);
            var seq = DOTween.Sequence();
            seq.Append(DOTween.To(() => 0f, t =>
            {
                if (go == null) return;
                float u = 1f - t;
                go.transform.position = u * u * from + 2f * u * t * mid + t * t * to;
            }, 1f, dur).SetEase(Ease.InOutQuad));
            seq.Join(go.transform.DOScale(cell * 0.6f, dur).SetEase(Ease.InQuad));
            seq.OnComplete(() => { if (go != null) Destroy(go); });
            return seq;
        }

        // ── BONUS COMET + LANDING STARS (2026-10-28 Spencer: Royal Match @1:19 - "a trailer starting at the moves left and then
        // they are placed on the board with a stars particle animation ... we have some star VFX sprites"). Measured from
        // royalmatch(1-20).mov at 30fps: a thick WHITE comet streaks straight from the Moves counter to its tile in ~0.15-0.2s,
        // shedding sparkle dust; on arrival a white flash (~0.6 cell, ~0.1s) and a ring of twinkling 4-point stars round the
        // tile while the rocket appears. Sprites: Particles/flare_star (4-point twinkle), Star02 (soft 5-point star).
        public static bool  BONUS_COMET = true;
        public static Color COMET_COLOR = new Color(2.4f, 2.35f, 2.2f, 1f);
        private static Sprite s_twinkle, s_star5; private static bool s_starsTried;
        public static Sprite TwinkleSprite { get { LoadStars(); return s_twinkle; } }
        public static Sprite Star5Sprite { get { LoadStars(); return s_star5; } }
        private static void LoadStars()
        {
            if (s_starsTried) return; s_starsTried = true;
            Sprite Mk(string n) { var t = Resources.Load<Texture2D>("Particles/" + n); return t != null ? Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width) : null; }
            s_twinkle = Mk("flare_star"); s_star5 = Mk("Star02");
        }
        public void BonusComet(Vector3 from, Vector3 to, float dur)
        {
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
            StartCoroutine(BonusCometRoutine(from, to, dur, cell));
        }
        private System.Collections.IEnumerator BonusCometRoutine(Vector3 from, Vector3 to, float dur, float cell)
        {
            LoadStars();
            var tail = MakeFxSprite("CometTail", SoftBeam(), COMET_COLOR, 61);
            var head = MakeFxSprite("CometHead", SoftDot(), COMET_COLOR, 62);
            Vector3 d = to - from; float len = d.magnitude; Vector3 dir = len > 0.001f ? d / len : Vector3.down;
            float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            tail.transform.rotation = Quaternion.Euler(0f, 0f, ang);
            float tailMax = Mathf.Min(len, cell * 3.2f), t = 0f, nextDust = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur); float e = k * k * (3f - 2f * k);
                Vector3 p = from + d * e;
                float tl = Mathf.Min(tailMax, len * e);
                head.transform.position = p; head.transform.localScale = Vector3.one * cell * 0.55f;
                tail.transform.position = p - dir * tl * 0.5f;
                tail.transform.localScale = new Vector3(Mathf.Max(0.01f, tl), cell * 0.5f, 1f);
                if (t >= nextDust && s_twinkle != null)
                {   // sparkle dust shed off the trail, drifting sideways and fading
                    nextDust = t + 0.025f;
                    Vector3 side = new Vector3(-dir.y, dir.x, 0f) * Random.Range(-0.35f, 0.35f) * cell;
                    StartCoroutine(Twinkle(p - dir * Random.Range(0f, tl) + side, cell * Random.Range(0.18f, 0.32f), Random.Range(0.25f, 0.4f), 0f,
                        side.normalized * cell * 0.6f + Vector3.down * cell * 0.3f, 60));
                }
                yield return null;
            }
            // tail drains into the tile
            float f = 0f; Vector3 end = to;
            while (f < 0.08f) { f += Time.unscaledDeltaTime; float q = 1f - f / 0.08f; tail.transform.localScale = new Vector3(Mathf.Max(0.01f, tailMax * q), cell * 0.5f * q, 1f); tail.transform.position = end - dir * tailMax * q * 0.5f; yield return null; }
            Destroy(tail.gameObject); Destroy(head.gameObject);
        }
        /// <summary>Flash + a ring of twinkling stars round the tile as the bonus rocket appears.</summary>
        public void BonusLandSparkle(Vector3 at)
        {
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
            LoadStars();
            var fl = MakeFxSprite("BonusLandFlash", SoftDot(), COMET_COLOR, 62);
            fl.transform.position = at; fl.transform.localScale = Vector3.one * cell * 0.3f;
            var f0 = fl;
            fl.transform.DOScale(cell * 1.1f, 0.10f).SetEase(Ease.OutQuad).SetUpdate(true);
            fl.DOFade(0f, 0.16f).SetDelay(0.06f).SetUpdate(true).OnComplete(() => { if (f0 != null) Destroy(f0.gameObject); });
            if (s_twinkle == null) return;
            int n = 7; float a0 = Random.Range(0f, 360f);
            for (int i = 0; i < n; i++)
            {
                float a = (a0 + i * 360f / n + Random.Range(-15f, 15f)) * Mathf.Deg2Rad;
                Vector3 p = at + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * cell * Random.Range(0.42f, 0.62f);
                StartCoroutine(Twinkle(p, cell * Random.Range(0.32f, 0.5f), Random.Range(0.32f, 0.45f), Random.Range(0f, 0.12f),
                    (p - at).normalized * cell * 0.5f, 63));
            }
            if (s_star5 != null)
                for (int i = 0; i < 3; i++)
                {
                    Vector2 r = Random.insideUnitCircle.normalized;
                    StartCoroutine(Twinkle(at + (Vector3)r * cell * 0.2f, cell * 0.22f, 0.45f, 0.03f * i, (Vector3)r * cell * 1.6f, 63, s_star5));
                }
        }
        /// <summary>One star: pops 0 -> size -> 0 over life (peak at 35%), slow spin, drifts by vel.</summary>
        public System.Collections.IEnumerator Twinkle(Vector3 at, float size, float life, float delay, Vector3 vel, int order, Sprite sp = null)
        {
            if (delay > 0f) { float w = 0f; while (w < delay) { w += Time.unscaledDeltaTime; yield return null; } }
            var sr = MakeFxSprite("Twinkle", sp ?? s_twinkle, COMET_COLOR, order);
            if (sr == null) yield break;
            float t = 0f, spin = Random.Range(-90f, 90f), rot0 = Random.Range(0f, 45f);
            while (t < life && sr != null)
            {
                t += Time.unscaledDeltaTime; float k = t / life;
                float s = k < 0.35f ? k / 0.35f : 1f - (k - 0.35f) / 0.65f;
                sr.transform.position = at + vel * t;
                sr.transform.localScale = Vector3.one * size * Mathf.Max(0f, s);
                sr.transform.rotation = Quaternion.Euler(0f, 0f, rot0 + spin * t);
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }

        private SpriteRenderer MakeIconSprite(string name, int kind, float worldSize, int order)
        {
            var icon = Tile.SpecialIcon(kind);
            if (icon == null) return null;
            var go = new GameObject(name);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = icon; sr.sortingOrder = order;
            go.transform.localScale = Vector3.one * worldSize;   // sprite is 1 unit wide
            go.transform.rotation = Quaternion.Euler(0f, 0f, Tile.SpecialIconAngle(kind));
            return sr;
        }

        // ── AIRPLANE FLIGHT (2026-10-23 Spencer: "the flying airplane animation is kinda lame. its just very stiff and boring").
        // Was: scale-up, then DOJump at one fixed angle. Now, Royal-Match-propeller style: LIFT-OFF (rises, swells, wobbles),
        // a SWOOPING curve (cubic bezier bowed to one side) with the nose following the path and a banking squash, a sparkle
        // TRAIL, then a DIVE onto the target (shrink + impact ring + sparks + shake). Timing: AIRPLANE_TAKEOFF + AIRPLANE_FLIGHT. ──

        /// <summary>11-03: the airplane power-up is now a PROPELLER BOOK (helicopter): upright flight, rotor spinning. false = the
        /// paper-plane flight (nose follows the path).</summary>
        public static bool AIRPLANE_COPTER = true; public static float COPTER_LEAN = 12f, COPTER_ROTOR_HZ = 6f;
        // the book's opaque height / canvas (430/512) and its centre's offset BELOW the canvas centre (27/512) in Specials/special_airplane
        public static float COPTER_ART_BOOK_H = 429f / 512f, COPTER_ART_CENTRE_DY = 0.5f / 512f,   /* 11-16 wing-emblem book, centred on its canvas (was 430/27) */ COPTER_FLIGHT_GROW = 1.12f;
        public static float COPTER_LIFT = 0.55f, COPTER_LIFT_SCALE = 1.3f;   // helicopter flight: rise (cells) + swell while airborne
        public static float COPTER_DUST_EVERY = 0.025f;   // magic trail: seconds between puffs (2 specks each)
        public static Color[] COPTER_DUST_COLORS = { new Color(1.9f, 1.5f, 0.5f, 1f), new Color(1.4f, 0.8f, 2.0f, 1f), new Color(0.8f, 1.6f, 2.0f, 1f), new Color(2.0f, 1.9f, 1.6f, 1f) };   // HDR gold / violet / cyan / white (blooms)
        private static Sprite s_flightRotor;
        private static void AddFlightRotor(SpriteRenderer plane)
        {
            if (Tile.WINGS_ON && Tile.WingSprite != null && plane != null)
            {   // 11-16 WINGED BOOK (see Tile.WINGS_ON): the wings pop out from board size to full and flap fast while it flies
                var sp = Tile.WingSprite; float full = Tile.WING_WIDTH / Mathf.Max(sp.bounds.size.x, 1e-4f), small = full * Tile.WING_BOARD_SCALE;
                for (int side = 1; side >= -1; side -= 2)
                {
                    var wgo = new GameObject(side > 0 ? "FlightWingR" : "FlightWingL"); wgo.transform.SetParent(plane.transform, false);
                    var wsr = wgo.AddComponent<SpriteRenderer>(); wsr.sprite = sp; wsr.flipX = side < 0; wsr.sortingOrder = plane.sortingOrder - 1;
                    float ux = side > 0 ? Tile.WING_MOUNT_UV.x : 1f - Tile.WING_MOUNT_UV.x;
                    wgo.transform.localPosition = new Vector3(ux - 0.5f, Tile.WING_MOUNT_UV.y - 0.5f, 0.01f);
                    wgo.transform.localScale = Vector3.one * small;
                    wgo.transform.DOScale(full, Tile.WING_GROW_DUR).SetEase(Ease.OutBack, 2.2f).SetTarget(wgo).SetLink(wgo);
                    int s = side;
                    DOTween.To(() => 0f, t => { if (wgo != null) wgo.transform.localRotation = Quaternion.Euler(0f, 0f, s * (Tile.WING_BASE_DEG + Tile.WING_FLIGHT_AMP * Mathf.Sin(t * 2f * Mathf.PI))); },
                               10000f, 10000f / Tile.WING_FLIGHT_HZ).SetEase(Ease.Linear).SetTarget(wgo).SetLink(wgo);
                }
                return;
            }
            if (Tile.ROTOR_FLIPBOOK && Tile.RotorFlipFrames != null && plane != null)
            {   // 11-16 flipbook propeller (see Tile.ROTOR_FLIPBOOK), flipped twice as fast in flight
                var fr = Tile.RotorFlipFrames;
                var fgo = new GameObject("FlightRotor"); fgo.transform.SetParent(plane.transform, false);
                var fsr = fgo.AddComponent<SpriteRenderer>(); fsr.sprite = fr[0]; fsr.sortingOrder = plane.sortingOrder + 1;
                fgo.transform.localPosition = new Vector3(Tile.ROTOR_HUB_UV.x - 0.5f, Tile.ROTOR_HUB_UV.y + Tile.ROTOR_FLIP_LIFT - 0.5f, -0.01f);
                float fw = Tile.ROTOR_FLIP_WIDTH / Mathf.Max(fr[0].bounds.size.x, 1e-4f);
                fgo.transform.localScale = new Vector3(fw, fw * Tile.ROTOR_FLIP_SQUASH, 1f);
                DOTween.To(() => 0f, t => { if (fsr != null) fsr.sprite = fr[((int)t) % fr.Length]; }, 100000f, 100000f / Tile.ROTOR_FLIP_FLIGHT_FPS)
                       .SetEase(Ease.Linear).SetTarget(fgo).SetLink(fgo);
                return;
            }
            if (s_flightRotor == null) { var t = Resources.Load<Texture2D>("Specials/airplane_rotor"); if (t != null) s_flightRotor = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width); }
            if (s_flightRotor == null || plane == null) return;
            var go = new GameObject("FlightRotor"); go.transform.SetParent(plane.transform, false);
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = s_flightRotor; sr.sortingOrder = plane.sortingOrder + 1;
            // plane sprite is 1 unit wide (ppu = texture width) in its own space; mast top = Tile.ROTOR_HUB_UV of that square
            go.transform.localPosition = new Vector3(Tile.ROTOR_HUB_UV.x - 0.5f, Tile.ROTOR_HUB_UV.y + Tile.ROTOR_LIFT - 0.5f, -0.01f);
            float w = Tile.ROTOR_WIDTH;
            go.transform.localScale = new Vector3(w, w, 1f);
            DOTween.To(() => 0f, t => { if (go != null) go.transform.localScale = new Vector3(w * Mathf.Max(Tile.ROTOR_MIN_SQUASH, Mathf.Abs(Mathf.Cos(t * Mathf.PI))), w, 1f); }, 200f, 200f / COPTER_ROTOR_HZ)
                   .SetEase(Ease.Linear).SetTarget(go).SetLink(go);
            var hubSp = Tile.RotorHubSprite;   // 11-03 the gold hub never squashes
            if (hubSp != null)
            {
                var hub = new GameObject("FlightRotorHub"); hub.transform.SetParent(plane.transform, false);
                var hsr = hub.AddComponent<SpriteRenderer>(); hsr.sprite = hubSp; hsr.sortingOrder = sr.sortingOrder + 1;
                hub.transform.localPosition = go.transform.localPosition + new Vector3(0f, 0f, -0.005f);
                hub.transform.localScale = new Vector3(w, w, 1f);
            }
        }

        private void PlayAirplaneFlight(RulesEngine.SpecialFire f, Vector3 origin, float cell, bool carryBomb = false)
        {
            // PAPER-PLANE GLIDE (2026-10-23 Spencer, mid-build: "it should probably have motion like a paper airplane might where
            // it glides"). TOSS up (quick), FLOAT over a high soft arc while it bobs and its nose rocks on the air (the wobble
            // dies away as it steadies), then tips the nose DOWN and glides in. Soft white wisps behind, not sparks.
            var grid = GridManager.Instance; if (grid == null) return;
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("tome_flight")) GameAudio.Instance.PlayKit("tome_flight"); else GameAudio.Instance?.PlayWhooshFast();   // 11-07 pick: Book Page Brush Fast A6 (was silent)
            var plane = MakeIconSprite("AirplaneArt", carryBomb ? RulesEngine.SPECIAL_AIRPLANE : f.Kind, cell * 1.0f, 55);
            if (plane == null) return;
            if (AIRPLANE_COPTER) AddFlightRotor(plane);   // 11-03 the propeller book's rotor spins (fast) in flight too
            // CARRIED BOMB (plane+bomb combo): hangs just under the plane, swinging a little, never rotating with the nose
            SpriteRenderer bomb = carryBomb ? MakeIconSprite("CarriedBomb", RulesEngine.SPECIAL_BOMB, cell * 0.85f, 54) : null;
            if (bomb != null) bomb.transform.position = origin + new Vector3(0f, -cell * 0.55f, 0.05f);   // hanging from the start (11-07: no first-frame pop)
            Vector3 to = grid.CellToWorld(f.Target.x, f.Target.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.3f);
            {   // BOOKSHELF (11-03): land on the middle of the 2x2 shelf, not on whichever of its cells was picked
                Tile tt = null; try { tt = grid.GetTile(f.Target.x, f.Target.y); } catch { }
                if (tt != null && tt.ShelfPart > 0)
                {
                    int p = tt.ShelfPart;
                    to += new Vector3((p == 2 || p == 4 ? -0.5f : 0.5f) * grid.CellSize, (p >= 3 ? -0.5f : 0.5f) * grid.RowPitch, 0f);
                }
            }
            Vector3 toss = origin + new Vector3(0f, cell * 0.7f, -0.4f);
            Vector3 d = to - toss; float len = Mathf.Max(0.01f, d.magnitude);
            float apex = Mathf.Clamp(len * 0.35f, cell * 1.4f, cell * 3.5f);            // high, lazy arc
            Vector3 mid = toss + d * 0.45f + new Vector3(0f, apex, 0f);
            System.Func<float, Vector3> P = t => { float u = 1f - t; return u * u * toss + 2f * u * t * mid + t * t * to; };
            var tr = plane.transform;
            tr.position = origin; tr.localScale = Vector3.one * cell * 0.9f;
            float baseScale = cell * 1.2f;
            if (AIRPLANE_COPTER && !carryBomb)
            {   // 11-03 (Spencer: "it changes smaller ... not a smooth transition"): start at EXACTLY the board tile's size and spot. The board
                // fits the book's opaque height to the cell (Tile FIT_CELL x SPECIAL_FILL_AIRPLANE); this sprite is the whole 512 canvas, so
                // scale by canvas/book and lift by the book's offset from the canvas centre. Then it only grows a little.
                float s0 = cell * Tile.SPECIAL_ICON_CELL_FILL * Tile.SPECIAL_FILL_AIRPLANE / COPTER_ART_BOOK_H;
                tr.localScale = Vector3.one * s0;
                tr.position = origin + new Vector3(0f, -COPTER_ART_CENTRE_DY * s0, 0f);
                baseScale = s0 * COPTER_FLIGHT_GROW;
            }
            var go = plane.gameObject;
            float dirSign = d.x >= 0f ? 1f : -1f;
            var seq = DOTween.Sequence().SetTarget(plane);
            bool copter = AIRPLANE_COPTER && !carryBomb;
            if (copter)
            {
                // 11-03 HELICOPTER FLIGHT (Spencer: "more of a helicopter kind of movement", his Royal-Match propeller GIF, read frame by
                // frame): LIFT straight up and swell (closer to the camera) while a soft SHADOW stays on the board and slides away
                // (that's the altitude), a short HOVER jitter, a FAST near-straight flight leaning into travel with the shadow tracking
                // the ground path, then DROP onto the target (shrinks back, shadow meets it) -> the same touch-down burst. Same total
                // time as the plane (AIRPLANE_TAKEOFF + AIRPLANE_FLIGHT) so the clear lands on cue.
                float s0 = tr.localScale.x, sUp = s0 * COPTER_LIFT_SCALE;
                Vector3 start = tr.position, liftV = new Vector3(0f, cell * COPTER_LIFT, -0.4f);
                Vector3 up = start + liftV, upTo = to + liftV;
                var shGO = new GameObject("CopterShadow"); var shadow = shGO.AddComponent<SpriteRenderer>();   // plain alpha sprite (the FX material is additive)
                shadow.sprite = plane.sprite; shadow.color = new Color(0.10f, 0.05f, 0.22f, 0f); shadow.sortingOrder = plane.sortingOrder - 1;
                var sh = shadow.transform; sh.position = start; sh.localScale = tr.localScale;
                Vector3 shOff = new Vector3(cell * 0.18f, -cell * 0.12f, 0.1f);
                float tUp = AIRPLANE_TAKEOFF * 0.75f, tHover = AIRPLANE_TAKEOFF * 0.25f, tFly = AIRPLANE_FLIGHT * 0.78f, tDrop = AIRPLANE_FLIGHT * 0.22f;
                // lift
                seq.Append(tr.DOMove(up, tUp).SetEase(Ease.OutCubic));
                seq.Join(tr.DOScale(sUp, tUp).SetEase(Ease.OutBack, 1.6f));
                seq.Join(sh.DOMove(start + shOff, tUp).SetEase(Ease.OutCubic));
                seq.Join(shadow.DOFade(0.32f, tUp));
                seq.Join(sh.DOScale(s0 * 0.9f, tUp));
                // hover jitter
                seq.Append(tr.DOShakePosition(tHover, new Vector3(cell * 0.05f, cell * 0.05f, 0f), 18, 90f, false, true));
                // fly: near-straight, quick, eased, leaning into travel
                Vector3 dir = upTo - up; float lean = Mathf.Clamp(-dir.x / Mathf.Max(1e-4f, dir.magnitude), -1f, 1f) * COPTER_LEAN;
                // 11-03 MAGIC TRAIL (Spencer: "the propeller books need some kind of magical trail around it while it moves"): fairy dust
                // spills off the book while it flies - gold, violet and soft cyan specks that drift down and fade behind it.
                float lastDust = -1f; Vector3 prevP = up;
                seq.Append(tr.DOMove(upTo, tFly).SetEase(Ease.InOutSine).OnUpdate(() =>
                {
                    if (go == null) return;
                    float now = Time.time; if (now - lastDust < COPTER_DUST_EVERY) return; lastDust = now;
                    Vector3 p = tr.position, vel = p - prevP; prevP = p;
                    for (int k = 0; k < 2; k++)
                    {
                        Color c = COPTER_DUST_COLORS[Random.Range(0, COPTER_DUST_COLORS.Length)];
                        Vector3 at = p + (Vector3)Random.insideUnitCircle * cell * 0.28f + new Vector3(0f, -cell * 0.1f, 0f);
                        Vector3 v = -vel.normalized * cell * Random.Range(0.4f, 1.1f) + new Vector3(0f, -cell * Random.Range(0.3f, 0.8f), 0f);
                        SpawnSpark(at, v, cell * Random.Range(0.10f, 0.22f), Random.Range(0.35f, 0.65f), c);
                    }
                }));
                seq.Join(sh.DOMove(to + shOff, tFly).SetEase(Ease.InOutSine));
                seq.Join(tr.DORotate(new Vector3(0f, 0f, lean), tFly * 0.3f).SetEase(Ease.OutQuad));
                seq.Insert(tUp + tHover + tFly * 0.7f, tr.DORotate(Vector3.zero, tFly * 0.3f).SetEase(Ease.InOutQuad));
                // drop onto the target
                seq.Append(tr.DOMove(to, tDrop).SetEase(Ease.InQuad));
                seq.Join(tr.DOScale(s0, tDrop).SetEase(Ease.InQuad));
                seq.Join(sh.DOMove(to, tDrop).SetEase(Ease.InQuad));
                seq.Join(shadow.DOFade(0f, tDrop));
                seq.AppendCallback(() => { if (shadow != null) Destroy(shadow.gameObject); });
            }
            else
            {
            // toss
            seq.Append(tr.DOMove(toss, AIRPLANE_TAKEOFF).SetEase(Ease.OutQuad));
            seq.Join(tr.DOScale(baseScale, AIRPLANE_TAKEOFF).SetEase(Ease.OutBack, 1.8f));
            seq.Join(tr.DORotate(new Vector3(0f, 0f, AIRPLANE_COPTER ? 0f : (dirSign > 0 ? -15f : 105f)), AIRPLANE_TAKEOFF).SetEase(Ease.OutQuad));
            // glide
            float lastWisp = 0f;
            seq.Append(DOTween.To(() => 0f, t =>
            {
                if (go == null) return;
                // floaty timing: drifts through the top of the arc, picks up speed on the way down
                float g = t < 0.6f ? Mathf.Lerp(0f, 0.5f, Mathf.Sin(t / 0.6f * Mathf.PI * 0.5f)) : Mathf.Lerp(0.5f, 1f, Mathf.Pow((t - 0.6f) / 0.4f, 1.6f));
                float calm = 1f - t;                                                      // wobble settles as it glides
                Vector3 pos = P(g) + new Vector3(0f, Mathf.Sin(t * Mathf.PI * 3f) * cell * 0.18f * calm, 0f);   // bobbing on the air
                Vector3 ahead = P(Mathf.Min(1f, g + 0.02f)) - P(g);
                tr.position = pos;
                if (AIRPLANE_COPTER)
                {   // 11-03 PROPELLER BOOK (Spencer: "it doesnt need to tilt the book towards the tile ... since its a helicopter"): stays
                    // upright, only a gentle lean into its travel direction and a little sway
                    float lean = Mathf.Clamp(-ahead.x / Mathf.Max(1e-4f, ahead.magnitude), -1f, 1f) * COPTER_LEAN + Mathf.Sin(t * Mathf.PI * 4f) * 4f * calm;
                    tr.rotation = Quaternion.Euler(0f, 0f, lean);
                }
                else
                {
                    float ang = Mathf.Atan2(ahead.y, ahead.x) * Mathf.Rad2Deg;
                    ang += Mathf.Sin(t * Mathf.PI * 4f) * 12f * calm;                        // nose rocks
                    tr.rotation = Quaternion.Euler(0f, 0f, ang - 45f);                         // art nose points up-right
                }
                float roll = Mathf.Sin(t * Mathf.PI * 2.5f) * 0.08f * calm;
                tr.localScale = new Vector3(baseScale * (1f - roll), baseScale * (1f + roll), 1f);
                if (t - lastWisp > 0.05f)
                {
                    lastWisp = t;
                    Vector3 back = -ahead.normalized * cell * 0.4f;
                    SpawnSpark(pos + back, back * 0.8f + (Vector3)Random.insideUnitCircle * cell * 0.2f,
                               cell * Random.Range(0.16f, 0.26f), Random.Range(0.3f, 0.5f), new Color(1.15f, 1.2f, 1.3f, 1f));
                }
            }, 1f, AIRPLANE_FLIGHT).SetEase(Ease.Linear));
            }
            // touch-down
            seq.AppendCallback(() =>
            {
                HapticsManager.ObstacleBreak();   // 11-17 haptics pass
                if (GameAudio.Instance != null && GameAudio.Instance.HasKit("tome_land")) GameAudio.Instance.PlayKit("tome_land", GameAudio.TOME_LAND_VOL);   // 11-07 pick: Book Drop Low A2 (the landing was silent)
                if (s_ringSprite != null)
                {
                    var ring = MakeFxSprite("PlaneImpact", s_ringSprite, new Color(1.5f, 1.6f, 2f, 1f), 54);
                    ring.transform.position = to; ring.transform.localScale = Vector3.one * cell * 0.4f;
                    var r0 = ring;
                    ring.transform.DOScale(cell * 1.8f, 0.25f).SetEase(Ease.OutQuad);
                    ring.DOFade(0f, 0.25f).SetDelay(0.03f).OnComplete(() => { if (r0 != null) Destroy(r0.gameObject); });
                }
                for (int k = 0; k < 8; k++)
                    SpawnSpark(to, (Vector3)(Random.insideUnitCircle.normalized * cell * Random.Range(3f, 6f)), cell * Random.Range(0.14f, 0.24f), Random.Range(0.25f, 0.45f), new Color(1.6f, 1.6f, 1.9f, 1f));
                GridManager.Instance?.ShakeBoard(0.12f, 0.18f);
                if (carryBomb)
                {   // the carried bomb goes off on the target: the normal powered-bomb blast (fireball, ring, shake, sound)
                    if (bomb != null) Destroy(bomb.gameObject);
                    PlaySpecialFire(new RulesEngine.SpecialFire { Cell = f.Target, Kind = RulesEngine.SPECIAL_BOMB, Letter = f.Letter, Word = "XX", Target = f.Target });
                }
            });
            seq.Append(plane.DOFade(0f, 0.06f));
            seq.OnComplete(() => { if (go != null) Destroy(go); if (bomb != null) Destroy(bomb.gameObject); });
            if (bomb != null) StartCoroutine(CarryBomb(bomb, tr, cell));
        }

        /// <summary>Plane+bomb combo: the bomb hangs under the flying plane (a little behind, swinging), upright.</summary>
        private System.Collections.IEnumerator CarryBomb(SpriteRenderer bomb, Transform plane, float cell)
        {
            float t = 0f;
            while (bomb != null && plane != null)
            {
                t += Time.deltaTime;
                float swing = Mathf.Sin(t * 9f) * 10f;
                bomb.transform.position = plane.position + new Vector3(0f, -cell * 0.55f, 0.05f);
                bomb.transform.rotation = Quaternion.Euler(0f, 0f, Tile.SpecialIconAngle(RulesEngine.SPECIAL_BOMB) + swing);
                yield return null;
            }
        }

        // ── LIGHT BALL / DISCO (2026-10-23 Spencer: "at about 26100 in the royalmatch(1-20) video it shows the disco ball
        // animation ... see if we can replicate the look"). Measured frames 26280-26365 (~60fps): CHARGE ~1.1s — spinning ball,
        // white halo + ring pulses, thin white beams to the targets ONE AT A TIME, each hit target keeps a frosty sheen; then
        // POP — a big white flash at the ball and EVERY target pops on the same frame (ring burst + shards); ~0.2s settle.
        // HandManager pops the tiles at DiscoPopAt(n) (all together); the beams land at DiscoBeamAt(k, n). ──
        public static float ORB_RISE_PEAK = 1.34f;   // seconds into Kit/orb_rise.wav where it peaks (measured)
        public static float DISCO_LEAD = 0.18f;          // ball wind-up before the first beam
        public static float DISCO_CHARGE = 1.0f;         // aim for this long a beam phase (RM ~1.1s)
        public static float DISCO_GAP_MIN = 0.025f, DISCO_GAP_MAX = 0.07f;
        public static float DISCO_POP_HOLD = 0.16f;      // last beam -> flash
        public static Color DISCO_FROST = new Color(1.3f, 1.5f, 1.9f, 1f);
        public static float DiscoGap(int n) => Mathf.Clamp(DISCO_CHARGE / Mathf.Max(1, n), DISCO_GAP_MIN, DISCO_GAP_MAX);
        public static float DiscoBeamAt(int k, int n) => DISCO_LEAD + k * DiscoGap(n);
        public static float DiscoPopAt(int n) => DISCO_LEAD + n * DiscoGap(n) + DISCO_POP_HOLD;
        private static Sprite s_glowSprite;
        public static Sprite GlowSprite()
        {
            if (s_glowSprite != null) return s_glowSprite;
            var tex = Resources.Load<Texture2D>("Particles/glow") ?? Resources.Load<Texture2D>("Particles/soft_circle");
            if (tex != null) s_glowSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
            return s_glowSprite ?? SoftDot();
        }

        // REAL LIGHTNING (2026-10-28 Spencer: "i have some real VFX sprites of these in my project"). Three bolt strips cut from
        // the AllIn1Vfx pack's TrailElectric 10 / 5 / 3 (Resources/Particles/bolt_a|b|c, white + alpha), stretched ball -> target.
        // The beam crackles: the shape swaps every ~2 frames and flips randomly, then fades. DISCO_BOLTS = false -> the old bar.
        public static bool  DISCO_BOLTS = true;
        public static Color DISCO_BOLT_COLOR = new Color(1.25f, 1.6f, 2.9f, 1f);   // blue-white, HDR so it catches the bloom
        public static float DISCO_BOLT_WIDTH = 0.72f;
        public static Color DISCO_BOLT_FRINGE = new Color(0.22f, 0.45f, 1f, 0.85f);   // normal-blend blue under the core                              // bolt amplitude, cells
        private static Sprite[] s_bolts; private static readonly float[] s_boltFill = { 1.3f, 1.79f, 1.08f };   // strip height / bolt height
        private static bool s_boltsTried;
        public static Sprite[] BoltSprites => LoadBolts() ? s_bolts : null;
        public static bool LoadBolts()
        {
            if (s_boltsTried) return s_bolts != null;
            s_boltsTried = true;
            var list = new List<Sprite>();
            foreach (var n in new[] { "bolt_a", "bolt_b", "bolt_c" })
            {
                var t = Resources.Load<Texture2D>("Particles/" + n);
                if (t == null) { WDLog.Log($"[Disco] {n} missing - plain beams"); return false; }
                list.Add(Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width));   // 1 unit wide
            }
            s_bolts = list.ToArray();
            return true;
        }
        private System.Collections.IEnumerator DiscoBolt(Vector3 from, Vector3 to, float at, float cell)
        {
            if (at > 0f) yield return new WaitForSeconds(at);
            Vector3 d = to - from; float len = d.magnitude; if (len < 0.01f) yield break;
            var sr = MakeFxSprite("DiscoBolt", s_bolts[0], DISCO_BOLT_COLOR, 50);
            sr.transform.position = from + d * 0.5f;
            sr.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            // BLUE FRINGE (2026-10-28 Spencer screenshots: the bolts vanished over the white letter tiles - additive light can't
            // show on white). A NORMAL-blended, wider copy in saturated blue sits under the hot core, so over a white tile you
            // still see a blue bolt with a white centre.
            var fringe = new GameObject("DiscoBoltFringe").AddComponent<SpriteRenderer>();
            fringe.sprite = s_bolts[0]; fringe.sortingOrder = 49; fringe.color = DISCO_BOLT_FRINGE;
            fringe.transform.SetParent(sr.transform, false);
            fringe.transform.localScale = new Vector3(1f, 1.35f, 1f);   // 1.7 -> 1.35 (overdraw)
            float life = 0.30f, t = 0f, nextSwap = 0f; int idx = Random.Range(0, s_bolts.Length);
            while (t < life && sr != null)
            {
                if (t >= nextSwap)
                {   // crackle: a new shape, flipped at random, every ~2 frames
                    idx = (idx + 1 + Random.Range(0, s_bolts.Length - 1)) % s_bolts.Length;
                    sr.sprite = s_bolts[idx]; fringe.sprite = s_bolts[idx];
                    // sprite is 1 unit wide and (H/W) tall: scale y so the BOLT itself spans DISCO_BOLT_WIDTH cells
                    float h = cell * DISCO_BOLT_WIDTH * s_boltFill[idx] / Mathf.Max(0.001f, s_bolts[idx].bounds.size.y);
                    sr.transform.localScale = new Vector3(len, h * (Random.value < 0.5f ? -1f : 1f), 1f);
                    nextSwap = t + 0.035f;
                }
                float k = t / life;
                float al = k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f;
                sr.color = new Color(1f, 1f, 1f, al);
                fringe.color = new Color(DISCO_BOLT_FRINGE.r, DISCO_BOLT_FRINGE.g, DISCO_BOLT_FRINGE.b, DISCO_BOLT_FRINGE.a * al);
                t += Time.deltaTime;
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }

        // CHARGED TILES (2026-10-28 Spencer: "little electro shocks floating around them ... like in the reference gif"). Every
        // tile a beam has hit crackles until the pop: small zaps from the Particle VFX pack's electricity flipbook
        // (Resources/Particles/electro_sheet = 4x2 frames) pop in around its edge, each at a new spot/angle every ~70ms.
        // Normal blend (not additive) so they show over the white tiles too. DISCO_TILE_SHOCKS = false -> off.
        public static bool  DISCO_TILE_SHOCKS = true;
        public static int   SHOCKS_PER_TILE   = 2;
        public static float SHOCK_SIZE        = 0.95f;   // flipbook frame size, cells (the zap itself is ~45% of the frame)
        private static Sprite[] s_shockFrames; private static bool s_shockTried;
        private static bool LoadShocks()
        {
            if (s_shockTried) return s_shockFrames != null;
            s_shockTried = true;
            var t = Resources.Load<Texture2D>("Particles/electro_sheet");
            if (t == null) { WDLog.Log("[Disco] electro_sheet missing - no tile shocks"); return false; }
            int fw = t.width / 4, fh = t.height / 2;
            s_shockFrames = new Sprite[8];
            for (int i = 0; i < 8; i++)
                s_shockFrames[i] = Sprite.Create(t, new Rect((i % 4) * fw, (1 - i / 4) * fh, fw, fh), new Vector2(0.5f, 0.5f), fw);   // 1 unit wide
            return true;
        }
        public System.Collections.IEnumerator TileShocks(Vector3 centre, float cell, float dur)
        {
            if (dur <= 0.02f || !LoadShocks()) yield break;
            var zaps = new SpriteRenderer[SHOCKS_PER_TILE];
            for (int i = 0; i < zaps.Length; i++)
            {
                zaps[i] = new GameObject("TileShock").AddComponent<SpriteRenderer>();
                zaps[i].sortingOrder = 51;
                zaps[i].enabled = false;
            }
            float t = 0f; var next = new float[zaps.Length];
            for (int i = 0; i < next.Length; i++) next[i] = i * 0.035f;   // staggered so they don't all jump together
            while (t < dur)
            {
                for (int i = 0; i < zaps.Length; i++)
                {
                    if (zaps[i] == null || t < next[i]) continue;
                    var z = zaps[i];
                    z.enabled = Random.value > 0.2f;   // the odd blank beat = flicker
                    z.sprite = s_shockFrames[Random.Range(0, s_shockFrames.Length)];
                    float ang = Random.Range(0f, 360f);
                    Vector2 off = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad)) * cell * Random.Range(0.28f, 0.5f);
                    z.transform.position = centre + (Vector3)off + new Vector3(0f, 0f, -0.4f);
                    z.transform.rotation = Quaternion.Euler(0f, 0f, ang + Random.Range(-40f, 40f));
                    float sc = cell * SHOCK_SIZE * Random.Range(0.8f, 1.15f);
                    z.transform.localScale = new Vector3(sc * (Random.value < 0.5f ? -1f : 1f), sc, 1f);
                    next[i] = t + Random.Range(0.055f, 0.09f);
                }
                t += Time.deltaTime;
                yield return null;
            }
            foreach (var z in zaps) if (z != null) Destroy(z.gameObject);
        }

        private void PlayDiscoCharge(RulesEngine.SpecialFire f, Vector3 origin, float cell)
        {
            var grid = GridManager.Instance; if (grid == null) return;
            var targets = new List<Vector2Int>();
            foreach (var c in f.Cleared) if (c != f.Cell) targets.Add(c);
            int n = targets.Count;
            float popAt = DiscoPopAt(f.Cleared.Count);
            Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
            // 11-07 Spencer's pick (Rising Pitch C6): a rising musical tone through the charge. The file is trimmed so its peak sits
            // ~1.34 s in = DISCO_LEAD + DISCO_CHARGE + DISCO_POP_HOLD, so the climax lands on the flash; started so it peaks at popAt.
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("orb_rise"))
            { float lead = Mathf.Max(0f, popAt - ORB_RISE_PEAK); if (lead > 0.01f) DOVirtual.DelayedCall(lead, () => GameAudio.Instance?.PlayKit("orb_rise")); else GameAudio.Instance.PlayKit("orb_rise"); }

            // the ball: spins the whole charge, swells a touch, gone in the flash
            var ball = MakeIconSprite("DiscoBall", f.Kind, cell * 1.1f, 52);
            if (ball != null)
            {
                ball.transform.position = origin + new Vector3(0f, 0f, -0.2f);
                var b0 = ball;
                ball.transform.DOScale(cell * 1.45f, 0.25f).SetEase(Ease.OutBack, 2f);
                ball.transform.DORotate(new Vector3(0f, 0f, 360f * 3f), popAt, RotateMode.FastBeyond360).SetEase(Ease.InQuad);
                ball.DOFade(0f, 0.08f).SetDelay(popAt).OnComplete(() => { if (b0 != null) Destroy(b0.gameObject); });
            }
            // halo + ring pulses
            var halo = MakeFxSprite("DiscoHalo", GlowSprite(), new Color(1.6f, 1.6f, 1.8f, 1f), 51);
            halo.transform.position = origin + new Vector3(0f, 0f, -0.15f);
            halo.transform.localScale = Vector3.one * cell * 0.5f;
            var h0 = halo;
            halo.transform.DOScale(cell * 2.2f, 0.3f).SetEase(Ease.OutQuad);
            halo.transform.DOScale(cell * 2.6f, 0.35f).SetDelay(0.3f).SetLoops(Mathf.Max(1, Mathf.CeilToInt((popAt - 0.3f) / 0.35f)), LoopType.Yoyo);
            DOVirtual.DelayedCall(popAt + 0.05f, () => { if (h0 != null) Destroy(h0.gameObject); });
            for (float t = 0.1f; t < popAt - 0.1f; t += 0.32f)
            {
                var ring = MakeFxSprite("DiscoPulse", s_ringSprite, new Color(1.2f, 1.3f, 1.6f, 1f), 50);
                ring.transform.position = origin; ring.transform.localScale = Vector3.one * cell * 0.9f; ring.color = new Color(1f, 1f, 1f, 0f);
                var r0 = ring;
                ring.DOFade(0.7f, 0.04f).SetDelay(t);
                ring.transform.DOScale(cell * 2.6f, 0.45f).SetDelay(t).SetEase(Ease.OutQuad);
                ring.DOFade(0f, 0.4f).SetDelay(t + 0.05f).OnComplete(() => { if (r0 != null) Destroy(r0.gameObject); });
            }

            // beams, one at a time; each hit target keeps a frosty glow until the pop
            for (int k = 0; k < n; k++)
            {
                var c = targets[k];
                Vector3 to = grid.CellToWorld(c.x, c.y) + slide + new Vector3(0f, 0f, -0.3f);
                Vector3 d = to - origin; float len = d.magnitude; if (len < 0.01f) continue;
                float at = DiscoBeamAt(k, f.Cleared.Count);
                // PERF (2026-10-28 probe: disco 17fps in the editor): with real bolts the old bar under each beam is skipped -
                // the blue fringe already gives the halo, and three stacked strips per beam piled up overdraw at the ball.
                if (DISCO_BOLTS && LoadBolts())
                {
                    StartCoroutine(DiscoBolt(origin, to, at, cell));
                    goto hitFx;
                }
                var beam = MakeFxSprite("DiscoBeam", s_beamSprite, new Color(1.6f, 1.7f, 2.0f, 1f), 49);
                beam.transform.position = origin + d * 0.5f;
                beam.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
                beam.transform.localScale = new Vector3(len / s_beamSprite.bounds.size.x, DISCO_BOLTS && LoadBolts() ? 0.30f : 0.35f, 1f);
                beam.color = new Color(1f, 1f, 1f, 0f);
                var bm = beam;
                float glowA = DISCO_BOLTS && LoadBolts() ? 0.7f : 1f;   // with real bolts the bar is just a soft glow under them
                beam.DOFade(glowA, 0.03f).SetDelay(at);
                beam.DOFade(0f, 0.18f).SetDelay(at + 0.12f).OnComplete(() => { if (bm != null) Destroy(bm.gameObject); });
                hitFx:
                var cc = c; var tt = to;
                DOVirtual.DelayedCall(at + 0.03f, () =>
                {
                    var tile = GridManager.Instance != null ? GridManager.Instance.GetTile(cc.x, cc.y) : null;
                    if (tile != null) tile.SetBloomGlow(DISCO_FROST, 0.85f, forceDesktop: true);
                    if (DISCO_TILE_SHOCKS) StartCoroutine(TileShocks(tt, cell, popAt - (at + 0.03f)));
                    for (int sIdx = 0; sIdx < 3; sIdx++)
                        SpawnSpark(tt, (Vector3)(Random.insideUnitCircle.normalized * cell * Random.Range(2f, 4f)), cell * 0.14f, 0.25f, new Color(1.5f, 1.6f, 2f, 1f));
                });
            }

            // the pop: white flash at the ball, a ring burst + shards on every target, same frame
            var targetsCopy = new List<Vector2Int>(targets);
            DOVirtual.DelayedCall(popAt, () =>
            {
                var g = GridManager.Instance; if (g == null) return;
                var flash = MakeFxSprite("DiscoFlash", GlowSprite(), new Color(2.2f, 2.2f, 2.3f, 1f), 53);
                flash.transform.position = origin + new Vector3(0f, 0f, -0.25f);
                flash.transform.localScale = Vector3.one * cell * 1.2f;
                var f0 = flash;
                flash.transform.DOScale(cell * 4.2f, 0.12f).SetEase(Ease.OutQuad);
                flash.DOFade(0f, 0.3f).SetDelay(0.1f).OnComplete(() => { if (f0 != null) Destroy(f0.gameObject); });
                foreach (var c in targetsCopy)
                {
                    Vector3 p = g.CellToWorld(c.x, c.y) + new Vector3(g.BoardSlideOffsetX, 0f, -0.35f);
                    var ring = MakeFxSprite("DiscoBurst", s_ringSprite, new Color(1.5f, 1.6f, 2f, 1f), 50);
                    ring.transform.position = p; ring.transform.localScale = Vector3.one * cell * 0.5f;
                    var r1 = ring;
                    ring.transform.DOScale(cell * 1.35f, 0.22f).SetEase(Ease.OutQuad);
                    ring.DOFade(0f, 0.22f).SetDelay(0.04f).OnComplete(() => { if (r1 != null) Destroy(r1.gameObject); });
                    for (int sIdx = 0; sIdx < 4; sIdx++)
                        SpawnSpark(p, (Vector3)(Random.insideUnitCircle.normalized * cell * Random.Range(3f, 6f)), cell * Random.Range(0.12f, 0.2f), Random.Range(0.25f, 0.4f), new Color(1.4f, 1.5f, 2f, 1f));
                }
                g.ShakeBoard(0.16f, 0.30f);
                if (GameAudio.Instance == null || !GameAudio.Instance.HasKit("orb_rise")) GameAudio.Instance?.PlayScorePowerup();   // the rise's peak is the pop now
            });
        }

        /// <summary>Plays the fire animation for one special. Rocket: two glowing heads streak out both ways along the
        /// row/column with a fading lane flash. Bomb: white flash + expanding shockwave ring. Disco: beams from the ball
        /// to every tile it took. Visual only — the rules already cleared the cells.</summary>
        // ── COMBO WIND-UP + BOARD DIM (2026-10-28, Spencer's RK/RM combo clips, ~/Documents/animations/_findings) ─────────────
        // Theirs never fire a combo on the swap frame: MERGE -> a WIND-UP sized to the payoff (rocket+rocket ~1-frame star pop;
        // TNT+rocket ~0.46s flip; TNT+TNT grow + spin + countdown; light ball + X ~0.3s shrink) -> a burst that is FULL WHITE on
        // its first frame, plateaus ~3 frames, fades ~0.3s. The biggest also DIM the board (~45% over ~0.43s) and lift it with
        // the flash. Ours fired everything instantly. Kept SHORT on purpose (Spencer 10-23 cut dead time): ~0.15-0.55s.
        // COMBO_WINDUP = false -> the old instant combos. HandManager adds ComboWindupFor(step) to every pop/fire in the step.
        public static bool  COMBO_WINDUP = true;
        public static float ComboWindup(int kind)
        {
            if (!COMBO_WINDUP) return 0f;
            switch (kind)
            {
                case RulesEngine.SPECIAL_COMBO_CROSS:       return 0.15f;
                case RulesEngine.SPECIAL_COMBO_BIGCROSS:    return 0.35f;
                case RulesEngine.SPECIAL_COMBO_DISCO:       return 0.30f;
                case RulesEngine.SPECIAL_COMBO_MEGA:        return 0.55f;
                case RulesEngine.SPECIAL_COMBO_DISCO_DISCO: return 0.55f;
                case RulesEngine.SPECIAL_COMBO_PLANES:      return 0.25f;
                case RulesEngine.SPECIAL_COMBO_PLANE_BOMB:  return 0.50f;   // the tome swoops in and picks the bomb up (11-07; was: swell + flash)
                default: return 0f;
            }
        }
        public static bool ComboDims(int kind) => kind == RulesEngine.SPECIAL_COMBO_MEGA || kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO;

        /// <summary>Wind-up art at the combo cell for <paramref name="dur"/> s; the real burst (PlaySpecialFire) follows it.</summary>
        public void PlayComboWindup(RulesEngine.SpecialFire f, float dur)
        {
            var grid = GridManager.Instance; if (grid == null || dur <= 0f) return;
            float cell = grid.CellSize;
            Vector3 origin = grid.CellToWorld(f.Cell.x, f.Cell.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.35f);
            if (ComboDims(f.Kind)) BoardDim(BOARD_DIM_ALPHA, dur);
            StartCoroutine(ComboWindupRoutine(f.Kind, origin, cell, dur));
        }

        // 11-07 (Spencer: "when you mix the bomb with the flying tome the bomb looks like its about to explode and then gets picked
        // up and flies away which doesnt really make sense"): the swell + white-hot blinks said "about to blow". PICK-UP instead:
        // the tome drops in over the bomb with its rotor spinning, the bomb hops up and hooks on, and the pair lifts to where the
        // flight starts (PlayAirplaneFlight gets origin + PLANE_BOMB_LIFT, so the handoff is seamless). false = the old swell.
        public static bool  PLANE_BOMB_PICKUP = true;
        public static float PLANE_BOMB_LIFT = 0.55f;   // cells: the plane's height above the bomb's cell when the flight takes over (CarryBomb hangs the bomb 0.55 under)
        private System.Collections.IEnumerator PlaneBombPickupRoutine(Vector3 origin, float cell, float dur)
        {
            var bomb = MakeIconSprite("ComboPickupBomb", RulesEngine.SPECIAL_BOMB, cell, 59);
            var plane = MakeIconSprite("ComboPickupTome", RulesEngine.SPECIAL_AIRPLANE, cell, 60);
            if (plane != null && AIRPLANE_COPTER) AddFlightRotor(plane);
            float bombRot = bomb != null ? bomb.transform.eulerAngles.z : 0f, planeRot = plane != null ? plane.transform.eulerAngles.z : 0f;
            Vector3 hover = origin + new Vector3(0f, cell * PLANE_BOMB_LIFT, -0.02f);
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime; float k = Mathf.Clamp01(t / dur);
                // tome: drops in from above (0-55%), then holds at the hover point with a small bob
                float drop = DOVirtual.EasedValue(0f, 1f, Mathf.Clamp01(k / 0.55f), Ease.OutBack);
                if (plane != null)
                {
                    plane.transform.position = Vector3.LerpUnclamped(hover + new Vector3(0f, cell * 0.9f, 0f), hover, drop) + new Vector3(0f, Mathf.Sin(k * 14f) * cell * 0.03f * (1f - k), 0f);
                    plane.transform.localScale = Vector3.one * cell * Mathf.Lerp(0.6f, 1f, Mathf.Clamp01(k / 0.3f));
                    plane.color = new Color(1f, 1f, 1f, Mathf.Clamp01(k / 0.15f));
                    plane.transform.rotation = Quaternion.Euler(0f, 0f, planeRot + Mathf.Sin(k * 22f) * 5f * (1f - k));
                }
                // bomb: a crouch as the tome arrives (40-60%), then it hops a touch and hooks on (swings under the tome, like the flight)
                if (bomb != null)
                {
                    float crouch = k < 0.4f ? 0f : k < 0.6f ? Mathf.Sin((k - 0.4f) / 0.2f * Mathf.PI) : 0f;
                    float hook = Mathf.Clamp01((k - 0.6f) / 0.4f);
                    float hop = Mathf.Sin(hook * Mathf.PI) * cell * 0.12f;
                    bomb.transform.position = origin + new Vector3(0f, hop, 0f);
                    bomb.transform.localScale = new Vector3(cell * 0.85f * (1f + 0.12f * crouch), cell * 0.85f * (1f - 0.16f * crouch), 1f);
                    bomb.transform.rotation = Quaternion.Euler(0f, 0f, bombRot + Mathf.Sin(hook * 9f) * 10f * hook);
                }
                yield return null;
            }
            if (plane != null) Destroy(plane.gameObject);
            if (bomb != null) Destroy(bomb.gameObject);
        }

        private System.Collections.IEnumerator ComboWindupRoutine(int kind, Vector3 origin, float cell, float dur)
        {
            if (kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB && PLANE_BOMB_PICKUP) { yield return PlaneBombPickupRoutine(origin, cell, dur); yield break; }
            int a = RulesEngine.SPECIAL_ROCKET_H, b = RulesEngine.SPECIAL_ROCKET_H;
            if (kind == RulesEngine.SPECIAL_COMBO_BIGCROSS) { a = RulesEngine.SPECIAL_TNT; b = RulesEngine.SPECIAL_ROCKET_H; }
            else if (kind == RulesEngine.SPECIAL_COMBO_MEGA) { a = b = RulesEngine.SPECIAL_TNT; }
            else if (kind == RulesEngine.SPECIAL_COMBO_DISCO || kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO) { a = b = RulesEngine.SPECIAL_DISCO; }
            else if (kind == RulesEngine.SPECIAL_COMBO_PLANES) { a = b = RulesEngine.SPECIAL_AIRPLANE; }
            else if (kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB) { a = b = RulesEngine.SPECIAL_BOMB; }
            var art = MakeIconSprite("ComboWindup", a, cell, 60);
            var glow = MakeFxSprite("ComboWindupGlow", SoftDot(), new Color(1.6f, 1.25f, 0.6f, 1f), 59);
            if (glow != null) glow.transform.position = origin + new Vector3(0f, 0f, 0.02f);
            if (art != null) art.transform.position = origin;
            float baseRot = art != null ? art.transform.eulerAngles.z : 0f;
            // peak size per kind (TNT+TNT grows biggest, rocket+rocket just pops)
            float peak = kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB ? 1.9f : kind == RulesEngine.SPECIAL_COMBO_MEGA ? 2.4f : kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO ? 2.0f
                       : kind == RulesEngine.SPECIAL_COMBO_CROSS ? 1.35f : 1.6f;
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime; float k = Mathf.Clamp01(t / dur);
                // grow with overshoot (out-back), then a last-instant pucker to 0.85 of peak right before the burst
                float g = DOVirtual.EasedValue(0f, 1f, Mathf.Clamp01(k / 0.7f), Ease.OutBack);
                float s = Mathf.Lerp(1f, peak, g) * (k > 0.85f ? Mathf.Lerp(1f, 0.85f, (k - 0.85f) / 0.15f) : 1f);
                if (art != null)
                {
                    art.transform.localScale = Vector3.one * cell * s;
                    if (kind == RulesEngine.SPECIAL_COMBO_BIGCROSS)
                    {   // TNT <-> rocket ALTERNATE in the flat (2026-10-28 Spencer: a 2D sprite shouldn't fake a 3D flip): ~3 swaps,
                        // each new one punches up as it takes the front, with a little tilt wobble
                        float ph = k * 3f; int n = (int)ph; float u = ph - n;
                        int kindNow = (n & 1) == 0 ? a : b;
                        art.sprite = Tile.SpecialIcon(kindNow) ?? art.sprite;
                        float punch = 1f + 0.22f * Mathf.Exp(-u * 7f) * Mathf.Cos(u * 18f);
                        art.transform.localScale = Vector3.one * cell * s * punch;
                        art.transform.rotation = Quaternion.Euler(0f, 0f, Tile.SpecialIconAngle(kindNow) + Mathf.Sin(ph * Mathf.PI * 2f) * 7f);
                    }
                    else if (kind == RulesEngine.SPECIAL_COMBO_MEGA || kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO)
                        art.transform.rotation = Quaternion.Euler(0f, 0f, baseRot + 360f * DOVirtual.EasedValue(0f, 1f, k, Ease.InOutQuad));
                    else art.transform.rotation = Quaternion.Euler(0f, 0f, baseRot + Mathf.Sin(k * 40f) * 8f * (1f - k));   // shiver
                    art.transform.position = origin + new Vector3(0f, cell * 0.25f * g, 0f);   // lifts off the board a little
                }
                if (glow != null)
                {
                    glow.transform.localScale = Vector3.one * cell * (1.2f + 1.4f * k) * s * 0.8f;
                    glow.color = new Color(1f, 1f, 1f, 0.35f + 0.5f * k);
                }
                if (kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB)
                {   // FLASHING (Spencer: "the bomb getting big and flashing"): 3 white-hot blinks, quickening, glow flaring with each
                    float blink = Mathf.Pow(Mathf.Abs(Mathf.Sin(k * k * Mathf.PI * 3f)), 3f);
                    if (art != null) art.color = Color.Lerp(Color.white, new Color(1f, 0.55f, 0.45f, 1f), blink);
                    if (glow != null) { glow.color = new Color(1f, 1f, 1f, Mathf.Clamp01(0.35f + 0.65f * blink)); glow.transform.localScale *= 1f + 0.5f * blink; }
                }
                yield return null;
            }
            if (art != null) Destroy(art.gameObject);
            if (glow != null) Destroy(glow.gameObject);
        }

        /// <summary>Burst flash: FULL white on its first frame, holds ~3 frames, then fades ~0.3s (RK: no ramp-up).</summary>
        private System.Collections.IEnumerator WhiteBurstFlash(Vector3 centre, float radius)
        {
            var f = MakeFxSprite("BurstFlash", SoftDot(), new Color(2.4f, 2.3f, 2.1f, 1f), 50);
            f.transform.position = centre - new Vector3(0f, 0f, 0.1f);
            float t = 0f, hold = 0.1f, fade = 0.3f;
            while (t < hold + fade)
            {
                t += Time.deltaTime;
                float k = t < hold ? 0f : (t - hold) / fade;
                f.transform.localScale = Vector3.one * radius * 2.4f * (1f + 0.25f * k);
                f.color = new Color(1f, 1f, 1f, 1f - k * k);
                yield return null;
            }
            Destroy(f.gameObject);
        }

        // ── BOARD DIM: a black veil over the board + background (order 44: above every tile, under the FX 46+; lifted
        // tiles at 60 stay bright = a spotlight). Rides the camera so a jolt can't uncover an edge. ──
        public static bool  BOARD_DIM = true;
        public static bool  BIG_POP_DIM = true;   // dim during the 20+ tile big-pop hold
        public static float BOARD_DIM_ALPHA = 0.5f;   // RK ball+ball: 127 -> 57 mean brightness (~55% darker)
        public static float BOARD_DIM_LIFT  = 0.3f;
        private SpriteRenderer _dimSR; private Tween _dimTw;
        public void BoardDim(float alpha, float dur)
        {
            if (!BOARD_DIM) return;
            var cam = Camera.main; if (cam == null) return;
            if (_dimSR == null)
            {
                var tex = new Texture2D(4, 4); var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white; tex.SetPixels(px); tex.Apply();
                var go = new GameObject("BoardDim"); _dimSR = go.AddComponent<SpriteRenderer>();
                _dimSR.sprite = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
                _dimSR.sortingOrder = 44; _dimSR.color = new Color(0.03f, 0.01f, 0.08f, 0f);
                go.transform.SetParent(cam.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, 4f);
            }
            float h = cam.orthographicSize * 2.6f;
            _dimSR.transform.localScale = new Vector3(h * cam.aspect * 1.3f, h, 1f);
            _dimSR.gameObject.SetActive(true);
            _dimTw?.Kill();
            _dimTw = _dimSR.DOFade(alpha, Mathf.Max(0.05f, dur)).SetEase(Ease.OutQuad).SetUpdate(true);
        }
        public void BoardDimLift(float dur = -1f)
        {
            if (_dimSR == null || !_dimSR.gameObject.activeSelf) return;
            _dimTw?.Kill();
            var sr = _dimSR;
            _dimTw = sr.DOFade(0f, dur > 0f ? dur : BOARD_DIM_LIFT).SetEase(Ease.InQuad).SetUpdate(true)
                .OnComplete(() => { if (sr != null) sr.gameObject.SetActive(false); });
        }

        public void PlaySpecialFire(RulesEngine.SpecialFire f)
        {
            var grid = GridManager.Instance;
            if (grid == null) return;
            if (s_rocketSprite == null)
            {
                s_rocketSprite = TileRenderer.CreateSolidRoundedRect(128, 44, 22, Color.white);
                s_ringSprite   = TileRenderer.CreateRoundedRect(128, 128, 64, new Color(1f, 1f, 1f, 0f), Color.white, 10);
                s_beamSprite   = TileRenderer.CreateSolidRoundedRect(64, 12, 6, Color.white);
            }
            float cell = grid.CellSize;
            Vector3 origin = grid.CellToWorld(f.Cell.x, f.Cell.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.3f);
            Color col = Tile.SpecialColor(f.Kind);
            bool powered = !string.IsNullOrEmpty(f.Word) && f.Word.Length > 1;   // fired by a word the player made
            // Powered rocket = a CROSS: play the streak along both axes (matches RulesEngine's row + column clear).
            if (powered && (f.Kind == RulesEngine.SPECIAL_ROCKET_H || f.Kind == RulesEngine.SPECIAL_ROCKET_V))
            {
                var a = f; a.Word = null;
                a.Kind = RulesEngine.SPECIAL_ROCKET_H; PlaySpecialFire(a);
                a.Kind = RulesEngine.SPECIAL_ROCKET_V; PlaySpecialFire(a);
                return;
            }
            float bombScale = (powered && RulesEngine.IsBombLike(f.Kind)) ? 1.7f : 1f;   // 5x5 vs 3x3
            // AIRPLANE COMBO: a burst at home (3x3); the airplanes it launches are their own SpecialFire entries.
            if (f.Kind == RulesEngine.SPECIAL_COMBO_PLANES)
            {
                StartCoroutine(Fireball(origin, 1.3f * cell, cell));
                if (COMBO_WINDUP) StartCoroutine(WhiteBurstFlash(origin, 1.3f * cell));
                GridManager.Instance?.ShakeBoard(0.12f, 0.2f);
                return;
            }
            // SWAP COMBOS — built from the same rocket lines + fireball, bigger.
            if (f.Kind >= RulesEngine.SPECIAL_COMBO_CROSS && f.Kind <= RulesEngine.SPECIAL_COMBO_DISCO_DISCO)
            {
                if (f.Kind == RulesEngine.SPECIAL_COMBO_CROSS || f.Kind == RulesEngine.SPECIAL_COMBO_BIGCROSS)
                {
                    int spread = f.Kind == RulesEngine.SPECIAL_COMBO_BIGCROSS ? 1 : 0;
                    StartCoroutine(Fireball(origin, (1.2f + spread) * cell, cell));
                    for (int d = -spread; d <= spread; d++)
                    {
                        var r = f; r.Word = null;
                        r.Kind = RulesEngine.SPECIAL_ROCKET_H; r.Cell = new Vector2Int(f.Cell.x, f.Cell.y + d); if (r.Cell.y >= 0 && r.Cell.y < RulesEngine.ROWS) PlaySpecialFire(r);
                        r.Kind = RulesEngine.SPECIAL_ROCKET_V; r.Cell = new Vector2Int(f.Cell.x + d, f.Cell.y); if (r.Cell.x >= 0 && r.Cell.x < RulesEngine.COLS) PlaySpecialFire(r);
                    }
                }
                else if (f.Kind == RulesEngine.SPECIAL_COMBO_MEGA) StartCoroutine(Fireball(origin, 4.5f * cell, cell));
                else if (f.Kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO)
                {
                    Vector3 mid = grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.3f);
                    StartCoroutine(Fireball(mid, 6f * cell, cell));
                }
                else StartCoroutine(Fireball(origin, 1.4f * cell, cell));   // disco + X: the partner specials fire their own FX
                if (COMBO_WINDUP)
                {
                    float fr = f.Kind == RulesEngine.SPECIAL_COMBO_MEGA ? 3f : f.Kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO ? 4.5f : 1.3f;
                    Vector3 fc = f.Kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO
                        ? grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.3f) : origin;
                    StartCoroutine(WhiteBurstFlash(fc, fr * cell));
                    if (ComboDims(f.Kind)) BoardDimLift();
                    if (ComboDims(f.Kind)) { var cam = Camera.main; if (cam != null) { cam.transform.DOKill(); PlayJolt(cam.transform, CAMERA_SHAKE_MAX); } }
                }
                GridManager.Instance?.ShakeBoard(0.28f, 0.35f);
                return;
            }

            if ((f.Kind == RulesEngine.SPECIAL_ROCKET_H || f.Kind == RulesEngine.SPECIAL_ROCKET_V) && ROCKET_V2)
            {
                PlayRocketV2(f, origin, cell, f.Kind == RulesEngine.SPECIAL_ROCKET_H);
            }
            else if (f.Kind == RulesEngine.SPECIAL_ROCKET_H || f.Kind == RulesEngine.SPECIAL_ROCKET_V)
            {
                bool h = f.Kind == RulesEngine.SPECIAL_ROCKET_H;
                Vector3 axis = h ? Vector3.right : Vector3.up;
                float reach = (h ? RulesEngine.COLS : RulesEngine.ROWS) * cell;
                // BLAST LINE (2026-10-22, from Spencer's Royal Match recording @1:21): the whole line flashes at once — a
                // white-hot core plus a flickering orange flame that thins out over ~0.4s — and the tiles along it go
                // (near) instantly; the rocket heads then ride out along the already-cleared line with ember trails.
                Vector3 lineCentre = h ? new Vector3(grid.CellToWorld(RulesEngine.COLS / 2, f.Cell.y).x + grid.BoardSlideOffsetX, origin.y, origin.z)
                                       : new Vector3(origin.x, grid.CellToWorld(f.Cell.x, RulesEngine.ROWS / 2).y, origin.z);
                StartCoroutine(RocketBlastLine(lineCentre, origin, h, reach * 1.15f, cell));
                GameAudio.Instance?.PlayRocketFire();
                // two heads, one each way
                for (int sgn = -1; sgn <= 1; sgn += 2)
                {
                    var head = MakeFxSprite("RocketGlow", s_rocketSprite, col * 1.4f, 48);
                    head.transform.position = origin;
                    head.transform.rotation = Quaternion.Euler(0f, 0f, h ? 0f : 90f);
                    head.transform.localScale = Vector3.one * (cell / s_rocketSprite.bounds.size.x) * 0.9f;
                    Vector3 to = origin + axis * sgn * reach;
                    var hs = head;
                    head.transform.DOMove(to, ROCKET_HEAD_FLIGHT).SetEase(Ease.InQuad);
                    head.DOFade(0f, 0.12f).SetDelay(ROCKET_HEAD_FLIGHT - 0.08f).OnComplete(() => { if (hs != null) Destroy(hs.gameObject); });
                    StartCoroutine(EmberTrail(head.transform, ROCKET_HEAD_FLIGHT, cell));
                    // Spencer's rocket art rides the glow: nose along the travel direction (art angle = Tile.ROCKET_ART_ANGLE).
                    // 11-03 (Spencer: flying rockets "smaller than on the board and angled off"): fly the SAME sprite the board shows (the
                    // vertical rocket's tile sprite: upright, sized so its visible length = one cell, centred on its body) at the tile's
                    // own scale, instead of the raw icon canvas (whose empty margins made it ~15-20% smaller).
                    SpriteRenderer art = null; float artUp = Tile.ROCKET_ART_ANGLE;
                    var tsp = Tile.SpecialTileSprite(RulesEngine.SPECIAL_ROCKET_V);
                    if (tsp != null && Tile.NormalSprite != null)
                    {
                        var ago = new GameObject("RocketArt"); art = ago.AddComponent<SpriteRenderer>(); art.sprite = tsp; art.sortingOrder = 49;
                        ago.transform.localScale = Vector3.one * (cell * Tile.TILE_DISPLAY_RATIO / Mathf.Max(0.0001f, Tile.NormalSprite.bounds.size.x));
                        artUp = 90f;   // the vertical rocket's tile sprite points straight up
                    }
                    else art = MakeIconSprite("RocketArt", f.Kind, cell * 0.95f, 49);
                    if (art != null)
                    {
                        float dirDeg = h ? (sgn > 0 ? 0f : 180f) : (sgn > 0 ? 90f : -90f);
                        art.transform.rotation = Quaternion.Euler(0f, 0f, dirDeg - artUp);   // nose along the travel direction
                        art.transform.position = origin;
                        var a = art;
                        art.transform.DOMove(to, ROCKET_HEAD_FLIGHT).SetEase(Ease.InQuad);
                        art.DOFade(0f, 0.1f).SetDelay(ROCKET_HEAD_FLIGHT - 0.06f).OnComplete(() => { if (a != null) Destroy(a.gameObject); });
                    }
                }
                GridManager.Instance?.ShakeBoard(0.10f, 0.18f);
            }
            else if (RulesEngine.IsBombLike(f.Kind))
            {
                var bart = MakeIconSprite("BombArt", f.Kind, cell * 0.9f, 49);
                if (bart != null)
                {
                    bart.transform.position = origin;
                    var b0 = bart;
                    bart.transform.DOScale(cell * 1.7f, 0.12f).SetEase(Ease.OutQuad);
                    bart.DOFade(0f, 0.1f).SetDelay(0.1f).OnComplete(() => { if (b0 != null) Destroy(b0.gameObject); });
                }
                // FIREBALL (2026-10-22 Spencer: match Royal Match's TNT blast — "more localized and more of a sphere
                // explosion but it matches in the colour and vibe of the rockets"). Same palette as the rocket line: a
                // white-hot core blooming out inside an orange flame sphere, a thin shock ring, and a radial spark burst.
                float radius = ((powered ? RulesEngine.BOMB_RADIUS_POWERED : RulesEngine.BOMB_RADIUS_BASIC) + 0.5f) * cell;   // fireball matches the cleared square
                StartCoroutine(Fireball(origin, radius, cell, v2: f.Kind == RulesEngine.SPECIAL_BOMB || f.Kind == RulesEngine.SPECIAL_TNT));
                if (BLAST_RING)
                {
                    var ring = MakeFxSprite("BombRing", s_ringSprite, ROCKET_FLAME * 0.8f, 47);
                    ring.transform.position = origin;
                    ring.transform.localScale = Vector3.one * (cell / s_ringSprite.bounds.size.x) * 0.6f;
                    ring.transform.DOScale(Vector3.one * (radius * 2.6f / s_ringSprite.bounds.size.x), 0.32f).SetEase(Ease.OutCubic);
                    ring.DOFade(0f, 0.32f).SetEase(Ease.InQuad).OnComplete(() => { if (ring != null) Destroy(ring.gameObject); });
                }
                GridManager.Instance?.ShakeBoard(0.20f * bombScale, 0.28f);
            }
            else if (f.Kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB)
            {
                GridManager.Instance?.ShakeBoard(0.10f, 0.18f);
                if (f.Target != f.Cell) PlayAirplaneFlight(f, PLANE_BOMB_PICKUP ? origin + new Vector3(0f, cell * PLANE_BOMB_LIFT, 0f) : origin, cell, carryBomb: true);   // starts where the pick-up left it
            }
            else if (f.Kind == RulesEngine.SPECIAL_AIRPLANE)
            {
                GridManager.Instance?.ShakeBoard(0.10f, 0.18f);
                bool hasTarget = f.Target != f.Cell && f.Cleared != null && f.Cleared.Contains(f.Target);
                // 11-03 (Spencer: "it didnt show the propeller one flying to the bookcase"): a target that SURVIVES the hit - a bookshelf
                // (loses a book) or a multi-hit crate (loses a layer) - isn't in Cleared, so the flight never played. A stone tile at the
                // target means the plane really went there.
                if (!hasTarget && f.Target != f.Cell && GridManager.Instance != null)
                { Tile tt = null; try { tt = GridManager.Instance.GetTile(f.Target.x, f.Target.y); } catch { } hasTarget = tt != null && tt.IsStone; }
                if (hasTarget) PlayAirplaneFlight(f, origin, cell);
            }
            else if (f.Kind == RulesEngine.SPECIAL_DISCO && f.Cleared != null)
            {
                PlayDiscoCharge(f, origin, cell);
            }
            // 11-07 sound pass: score_powerup played on EVERY power-up firing on top of its own sound (17x in one level) - dropped;
            // each power-up's own sound carries it now (rocket fireworks, bomb boom, orb charge, tome pages)
            HapticsManager.Strong();
        }

        // ── CRATE DEBRIS (2026-10-21 Spencer: "when the crates burst can they have a particle effect of debris falling off the
        // screen?"). Chunks cut from the crate art burst up and out, spin, and fall under gravity until they leave the bottom
        // of the screen. Fired at the moment the crate tile hides (all three hide sites in this file). ──
        public static int   CRATE_CHUNKS     = 7;
        public static float CRATE_GRAVITY    = 24f;   // in cells / s^2
        private static Sprite[] s_crateChunks;
        private static Sprite[] CrateChunks()
        {
            if (s_crateChunks != null) return s_crateChunks;
            var tex = Resources.Load<Texture2D>("Blockers/crate");
            if (tex == null) { s_crateChunks = new Sprite[0]; return s_crateChunks; }
            float w = tex.width, h = tex.height;
            // interior pieces (the art has transparent margins): planks, corners, a slat
            var rects = new[] {
                new Rect(w*0.12f, h*0.55f, w*0.30f, h*0.22f), new Rect(w*0.45f, h*0.60f, w*0.40f, h*0.20f),
                new Rect(w*0.15f, h*0.20f, w*0.25f, h*0.30f), new Rect(w*0.55f, h*0.18f, w*0.28f, h*0.28f),
                new Rect(w*0.35f, h*0.35f, w*0.22f, h*0.22f), new Rect(w*0.20f, h*0.75f, w*0.55f, h*0.12f) };
            s_crateChunks = new Sprite[rects.Length];
            for (int k = 0; k < rects.Length; k++) s_crateChunks[k] = Sprite.Create(tex, rects[k], new Vector2(0.5f, 0.5f), w);   // 1 unit = whole crate
            return s_crateChunks;
        }

        /// <summary>11-07 (Spencer: "when the boosters hit crates it should show the crates destruction ... if a mallet hits it just
        /// dissapears"): a booster-smashed tile gets what an explosion gives it - crate chunks + the break sound, a treasure opening,
        /// and a gem / goal letter FLYING to its badge (ClearCellCountingGoals already counted it and set CollectAwaitingFly).</summary>
        public void BoosterBreak(Tile t) => MaybeCrateDebris(t);
        private void MaybeCrateDebris(Tile t)
        {
            if (t == null || !SurvivalManager.FullBoardActive) return;
            // COLLECT: a target letter flies up to the HUD icon the moment its tile hides (every path: words, splash, specials).
            char cl = LevelRules.ActiveCollectLetter;
            // THE WINNING PIECE STILL FLIES (2026-10-23 Spencer: "the checkmark is still not showing up when achieving an
            // objective"). The last gem is counted -> the level is WON -> its tile hides a moment later, and this gate skipped its
            // flight, so CollectAwaitingFly stayed 1 and the badge never reached 0 (log: "goal flights settled ... awaiting=1").
            // After the win only pieces still owed a flight (awaiting > 0) fly; the bonus blast's gems aren't counted at all.
            bool flyOk = !SurvivalManager.AwaitingNextLevel || LevelRules.CollectAwaitingFly > 0;   // after the win nothing new is counted (RulesEngine), so awaiting > 0 = the winning pieces
            if (!t.IsStone && cl != '\0' && char.ToUpperInvariant(t.Letter) == cl && flyOk)
                HUDManager.Instance?.FlyCollectLetter(t.transform.position, cl);
            if (t.IsGem && LevelRules.HasGemGoal && flyOk)
                HUDManager.Instance?.FlyCollectLetter(t.transform.position, t.Letter);   // gem flies to the gem icon
            if (!t.IsStone || t.IsDropTargetVisual) return;   // ducks are stone-flagged too — they never "break"
            if (t.ShelfKind == 1) return;   // SAFE (11-03): its open burst is HandManager.HitSafe, not wood chunks
            if (t.TreasureHits > 0)
            {   // TREASURE OPENS (2026-10-27): pay + the coin fountain instead of crate chunks. Paid here because every explosion path
                // hides its tiles through these three sites; the bonus blast's treasures pay too (Spencer: extra rewards).
                int coins = RulesEngine.TREASURE_COINS;
                CoinWallet.Add(coins);
                SurvivalManager.PendingBonusCoins += coins;
                LevelMapPanel.Instance?.AddPendingCoinReward(coins);
                WDLog.Log($"[Treasure] opened at ({t.Col},{t.Row}) -> +{coins} coins");
                TreasureFX.Open(t.transform.position, coins);   // (TreasureHits resets in ResetForPool)
                return;
            }
            PlayCrateDebris(t.transform.position);
            GameAudio.Instance?.PlayCrateBreak();   // 2026-10-22 Spencer: crate SFX
        }

        // ── CRATE PLANK OFF (2026-11-03, Spencer: "show the boards falling off one by one in the debris explosion"). Losing a layer
        // pops THAT plank off the crate (Blockers/crate_plank_a = the X's back plank, lost 3 -> 2; crate_plank_b = the last diagonal,
        // lost 2 -> 1). Both are full 512 crate canvases, so drawn at the tile's own scale they start exactly where the plank sat. ──
        private static Sprite[] s_planks; private static bool s_planksTried;
        public static float PLANK_FLY_DUR = 0.75f, PLANK_GRAVITY = 14f, PLANK_SPIN = 420f;
        public void PlayPlankOff(Tile t, int hpBefore)
        {
            if (t == null || hpBefore < 2) return;
            if (!s_planksTried)
            {
                s_planksTried = true; s_planks = new Sprite[2];
                string[] names = { "Blockers/crate_plank_a", "Blockers/crate_plank_b" };
                for (int i = 0; i < 2; i++) { var tex = Resources.Load<Texture2D>(names[i]); if (tex != null) s_planks[i] = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width); }
            }
            var sp = s_planks[hpBefore >= 3 ? 0 : 1]; if (sp == null) return;
            var go = new GameObject("CratePlank");
            go.transform.position = t.transform.position + new Vector3(0f, 0f, -0.3f);
            go.transform.localScale = t.transform.lossyScale;
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = sp; sr.sortingOrder = t.FaceSortingOrder + 6;
            StartCoroutine(PlankFly(go, sr, hpBefore >= 3 ? -1f : 1f));
        }
        private System.Collections.IEnumerator PlankFly(GameObject go, SpriteRenderer sr, float side)
        {
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
            // pops UP and out to its side, spins as it tumbles, falls under gravity, fades at the end
            var vel = new Vector3(side * Random.Range(1.2f, 2.0f), Random.Range(3.2f, 4.2f), 0f) * cell;
            float spin = side * -PLANK_SPIN * Random.Range(0.8f, 1.2f), t0 = 0f;
            Vector3 s0 = go.transform.localScale;
            while (t0 < PLANK_FLY_DUR && go != null)
            {
                float dt = Time.deltaTime; t0 += dt;
                vel.y -= PLANK_GRAVITY * cell * dt;
                go.transform.position += vel * dt;
                go.transform.Rotate(0f, 0f, spin * dt);
                go.transform.localScale = s0 * Mathf.Lerp(1f, 1.15f, Mathf.Clamp01(t0 / 0.2f));   // lifts toward the camera as it pops
                float f = Mathf.Clamp01((t0 - PLANK_FLY_DUR * 0.6f) / (PLANK_FLY_DUR * 0.4f));
                sr.color = new Color(1f, 1f, 1f, 1f - f);
                yield return null;
            }
            if (go != null) Destroy(go);
        }

        public void PlayCrateDebris(Vector3 pos)
        {
            var chunks = CrateChunks();
            if (chunks.Length == 0) return;
            float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
            for (int k = 0; k < CRATE_CHUNKS; k++)
            {
                var go = new GameObject("CrateChunk");
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = chunks[k % chunks.Length];
                sr.sortingOrder = 46;   // above every tile row band (<= 37), under the special FX (47-49)
                go.transform.position = pos + new Vector3(Random.Range(-0.25f, 0.25f) * cell, Random.Range(-0.2f, 0.25f) * cell, -0.8f);
                go.transform.localScale = Vector3.one * cell * Random.Range(0.85f, 1.15f);
                go.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                Vector2 v = new Vector2(Random.Range(-2.6f, 2.6f), Random.Range(3.2f, 6.5f)) * cell;
                float spin = Random.Range(-620f, 620f);
                StartCoroutine(CrateChunkFall(go.transform, sr, v, spin, cell));
            }
        }

        private System.Collections.IEnumerator CrateChunkFall(Transform tr, SpriteRenderer sr, Vector2 v, float spin, float cell)
        {
            var cam = Camera.main;
            float floor = cam != null ? cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, 0f)).y - cell * 1.5f : -20f;
            float t = 0f;
            while (tr != null && t < 4f)
            {
                float dt = Time.deltaTime; t += dt;
                v.y -= CRATE_GRAVITY * cell * dt;
                tr.position += new Vector3(v.x, v.y, 0f) * dt;
                tr.Rotate(0f, 0f, spin * dt);
                if (tr.position.y < floor) break;
                yield return null;
            }
            if (tr != null) Destroy(tr.gameObject);
        }

        /// <summary>Tiles in one blast step needed for the meltdown stack (wind-up + jerky shake + punch). Was 8.</summary>
        // 2026-10-21: 5 -> 6. With 4+ letter words a plain 4-letter pop clears 5 tiles (word + 1 splash), so EVERY pop went
        // down the meltdown path, which skips the expanding bubble (Spencer: "the pops are missing ... the expanding bubble").
        // 6 = ordinary 4-letter pops get the bubble again; 5+ letter words and chains keep the meltdown + jerky shake.
        // (A brief revert to 5 + bubble-on-meltdown was undone the same hour — it changed the look Spencer liked.)
        public static int MELTDOWN_TILE_GATE = 6;
        public static bool MELTDOWN_POP_BUBBLE = false;

        public Coroutine PlayExplosion(List<Tile> tiles, int chainStep = 0, int wordLength = 3, bool wordFlash = true, bool cascade = false, bool suppressFlyUp = false, bool comboStagger = false)
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

            // LongWord ("explode N words") polish: when a word that COUNTS toward the goal detonates
            // (length >= the target min length), its letters fly UP to the objective icon — the same
            // escort travel the HeroWord chickens use — so a goal-satisfying explosion feels "collected."
            // Mirrors the HiddenWord/Vault fly-ups at this same detonation chokepoint. Deliberately UNGATED
            // by wordFlash: the live player-detonation path (HandManager) passes wordFlash:false, so gating
            // on it would never fire. FIRST CUT — flies every tile in this blast group, which on the single-
            // detonation path can include a little splash collateral; tighten to word-only if it reads busy.
            // 2026-07-06 Spencer.
            // Fire for LongWord ("explode N words") AND Combo ("blow up N at once") — both want the
            // detonated word's letters to fly up to the target. LongWord gates on its MinLen; Combo takes
            // any qualifying word. 2026-07-06 Spencer.
            var flyObj = ObjectiveManager.Instance != null ? ObjectiveManager.Instance.Active : null;
            bool goldOnly  = HandManager.GoldFlyUpActive;
            bool flyUpGoal = HUDManager.Instance != null && !suppressFlyUp
                && (goldOnly
                    || ((ObjectiveManager.Instance == null || ObjectiveManager.Instance.CurrentExplosionCounts) // no fly-up if it won't count (drop on a swap-only level)
                        && ((flyObj is LongWordObjective lwObj && wordLength >= lwObj.MinLen)
                            || flyObj is ComboObjective)));
            if (flyUpGoal)
            {
                // Collect valid tiles first so we can pop the icon EXACTLY ONCE (on the last landing) —
                // a per-letter pop stacks overlapping rotation tweens and freezes the icon (see
                // HUDManager.FlyLetterToTarget). Stagger so the letters launch together, land one-by-one.
                var flyTiles = new System.Collections.Generic.List<Tile>();
                var _flyDbg = new System.Text.StringBuilder();
                for (int i = 0; i < tiles.Count; i++)
                {
                    var ft = tiles[i];
                    if (ft == null) continue;
                    // Fly the WORD's letters, EXCLUDE splash collateral. A word tile is one that either was
                    // SCORED (WasInScoredWord, sticky green flag) OR is currently CHARGED/primed
                    // (HasPermanentGlow). The OR is the fix for words CHARGED ON AN EARLIER MOVE (e.g. SIP in a
                    // SIP/RUT/HUH cluster): those can lack the sticky scored flag but are still glowing-primed at
                    // detonation, so WasInScoredWord alone dropped them. Stones ('#') / swept junk are neither →
                    // stay filtered out. char.IsLetter guards stray '#'/'?'/'\0'. 2026-07-06 Spencer.
                    bool isWordTile = (goldOnly ? HandManager.IsGoldFlyTile(ft) : (ft.WasInScoredWord || ft.HasPermanentGlow)) && char.IsLetter(ft.Letter)   // gold mode: ANY gold tile in the blast flies (2026-10-21)
                                      && (!goldOnly || HandManager.IsGoldFlyTile(ft));   // instant pop: gold only
                    _flyDbg.Append($"{ft.Letter}(s{(ft.WasInScoredWord ? 1 : 0)}g{(ft.HasPermanentGlow ? 1 : 0)}{(isWordTile ? "→fly" : "")}) ");
                    if (isWordTile) flyTiles.Add(ft);
                }
                // Ramp the fly-up pop per WORD along a chain: deeper words punch out bigger toward the
                // camera, the finale biggest. Keyed on chainStep (the word's position in the stagger/cascade).
                // Tasteful range: 1.28× (first) → ~1.65× (chainStep 4+). Glow scales with it (child). 2026-07-06.
                float popPeak = 1.28f + Mathf.Min(chainStep * 0.10f, 0.37f);
                // Tell the bank how many landings to split the blast across, so each
                // impact pays an equal share and the counter climbs WITH the letters.
                bool goldToMoves = goldOnly && HandManager.GoldToMovesActive;
                if (flyTiles.Count > 0 && !goldToMoves) ScoreManager.Instance?.SetFlyLetterCount(flyTiles.Count);
                for (int i = 0; i < flyTiles.Count; i++)
                {
                    bool last = (i == flyTiles.Count - 1);
                    // The LAST letter to land releases any held objective-complete payoff (bing/checkmark/modal),
                    // so the win fires as a RESULT of the tiles hitting the target. 2026-07-10 Spencer.
                    // EVERY letter pays its share on impact; the LAST one also closes out
                    // the objective. Previously only the last letter had a callback at all,
                    // which is why nothing could tick per landing. 2026-09-09 Spencer.
                    bool isLast = last;
                    System.Action landCb = goldToMoves
                        ? (System.Action)HandManager.OnGoldLetterLanded      // gold letter = +1 move; points already paid
                        : () =>
                    {
                        ScoreManager.Instance?.AwardOneFlyShare();
                        if (isLast) ObjectiveManager.Instance?.NotifyObjectiveFlyUpsLanded();
                    };
                    // Own 4-slot sorting band per letter (+4) so a letter never bleeds onto a neighbour tile.
                    HUDManager.Instance.FlyLetterToTarget(flyTiles[i].transform.position, flyTiles[i].Letter, last, landCb, i * 0.07f, cascade, popPeak, 200 + (flyTiles.Count - 1 - i) * 4, comboStep: comboStagger ? chainStep : 0, armedColor: flyTiles[i].DetonationColor, toMoves: goldToMoves);
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

            // COMBO COLOUR ESCALATION (2026-07-27 Spencer). Tint each CHAINED word's tiles by its
            // detonation order so the blast ramps in hue right before each word pops (the wind-up feel).
            // chainStep 0 is the TRIGGER word — skipped, so it keeps its green scored-flash. Scoped to
            // WORD tiles via the SAME predicate as the fly-up above (WasInScoredWord || HasPermanentGlow)
            // so splash/junk collateral stays neutral. SetDetonationColor latches the tint on the tile so
            // the primed pulse (and any late re-glow) renders the new hue instead of magenta through the
            // pop. The big-blast/meltdown shatter path honours it via Tile.DetonationColor (~line 1660).
            // 2026-09-08 Spencer: COMBO COLOUR ESCALATION OFF. Each successive word in a
            // chain used to shift hue (hot pink -> violet -> blue-violet), which read as
            // arbitrary — the board flickered through colours that mean nothing in the
            // palette. Every word in a chain now detonates the SAME colour; the chain
            // still escalates through scale, shake, pitch and timing.
            // Set COMBO_COLOUR_RAMP true to bring the ramp back.
            if (COMBO_TINT_OVERRIDE && comboStagger && chainStep >= 1)
            {
                Color detoTint = COMBO_COLOUR_RAMP
                    ? s_comboDetonationRamp[Mathf.Min(chainStep - 1, s_comboDetonationRamp.Length - 1)]
                    : s_comboDetonationRamp[0];
                int _tinted = 0;
                var _dbg = new System.Text.StringBuilder();
                for (int i = 0; i < tiles.Count; i++)
                {
                    var ct = tiles[i];
                    if (ct == null) continue;
                    bool isWordTile = (ct.WasInScoredWord || ct.HasPermanentGlow) && char.IsLetter(ct.Letter);
                    _dbg.Append($"{ct.Letter}(s{(ct.WasInScoredWord?1:0)}g{(ct.HasPermanentGlow?1:0)}{(isWordTile?"→tint":"")}) ");
                    if (isWordTile) { ct.SetDetonationColor(detoTint); _tinted++; }
                }
            }
            else if (COMBO_TINT_OVERRIDE && chainStep >= 1)
            {
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
            yield return StartCoroutine(ExplosionCoroutine(tiles, chainStep, wordLength, wordFlash));
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

        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio, bool isCascade, float startDelay, bool instantPop) => PlayTier1Pop(tile, suppressAudio, isCascade, startDelay, instantPop, false);
        /// <param name="collateral">11-07: a tile a blast caught (not the word's) - cream chunks, no sparkle spray.</param>
        public Coroutine PlayTier1Pop(Tile tile, bool suppressAudio, bool isCascade, float startDelay, bool instantPop, bool collateral)
        {
            if (tile == null) return null;
            // Route through revert toggle. Legacy = 2026-05-03 squeeze→fade.
            // New = 2026-05-04 CC-reference shrink-to-nothing.
            if (FX_UseLegacyTier1Pop)
                return StartCoroutine(Tier1PopCoroutine_Legacy20260504(tile, suppressAudio, isCascade, startDelay));
            return StartCoroutine(Tier1PopCoroutine(tile, suppressAudio, isCascade, startDelay, instantPop, collateral));
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
            MaybeCrateDebris(tile);
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
        private IEnumerator Tier1PopCoroutine(Tile tile, bool suppressAudio, bool isCascade, float startDelay, bool instantPop = false, bool collateral = false)
        {
            if (tile == null) yield break;
            int gen = tile.Generation;   // recycled into a new letter while we waited? then it isn't ours to pop (2026-10-28)
            if (startDelay > 0f)
            {
                yield return WaitCache.Get(startDelay);
                if (tile == null || tile.Generation != gen) yield break;
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
                if (tile == null || tile.Generation != gen) yield break;
                tileSR.color = new Color(
                    Mathf.Min(preservedColor.r * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.g * 1.2f, GlintCap),
                    Mathf.Min(preservedColor.b * 1.2f, GlintCap),
                    1f); // de-blown 2026-06-03: clamp 1.5→1.28 (under the bloom line) so the cascade pre-pop glint doesn't white-out
                yield return WaitCache.Get(0.04f);
                if (tile == null || tile.Generation != gen) yield break;
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
                if (tile == null || tile.Generation != gen) yield break;
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
            if (tile == null || tile.Generation != gen) yield break;

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
                TileFragments.Instance.Shatter(tile, 1.2f, collateral);
            if (SparkleSpray.Instance != null && (!collateral || BLAST_COLLATERAL_SPARKLES))   // 11-07: blast-caught tiles don't sparkle
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
            MaybeCrateDebris(tile);
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

        private IEnumerator ExplosionCoroutine(List<Tile> tiles, int chainStep, int wordLength, bool wordFlash = true)
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
                    // Combo escalation: honour this tile's per-word detonation hue if it got one, else magenta.
                    // 2026-09-08: was falling back to hardcoded PRIMED_TILE_TINT, which repainted
                    // GOLD-armed words magenta in the shatter path. DetonationFaceColor is the
                    // tile's OWN primed colour normalised to LDR, so it is correct either way.
                    meltdownOriginalColors[i]  = mt.DetonationFaceColor;
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
            WDLog.Log($"[FX-Detonation] tier={(chainStep >= 3 || tileCount >= 15 ? 4 : chainStep >= 2 || tileCount >= 12 ? 3 : tileCount >= 8 ? 2 : 1)} chain={chainStep} tiles={tileCount}");

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
            // 2026-10-20 Spencer: "one of the last explosions had this cool jerky screen shake ... sprinkle that in more
            // often" — it was this meltdown stack (ABOARD, 9 tiles). At 8 tiles it fired 1 in 67 blasts in his run;
            // 5 = every 5+ letter word (incl. solved planted words) or a decent cascade, ~1 in 6. Doubles as the payoff
            // for the long words the scoring now pushes toward. 6 ≈ 4 per run if this is too often.
            bool _tilesGate_dbg    = tileCount >= MELTDOWN_TILE_GATE;
            bool meltdownActive    = _mmActive_dbg || _tilesGate_dbg;
            WDLog.Log($"[Meltdown] gate={meltdownActive} (mmActive={_mmActive_dbg}, tilesGate={_tilesGate_dbg}) — tier={tier} chain={chainStep} tiles={tileCount}");

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
                WDLog.Log($"[FX] MeltdownPrefab: FIRED (wordTilesOnly={meltdownWordTiles != null}, wordCount={(meltdownWordTiles == null ? -1 : meltdownWordTiles.Count)})");

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
                    WDLog.Log("[FX] MeltdownTilePunch: FIRED");
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
                else { WDLog.Log("[FX] MeltdownTilePunch: SKIPPED"); }

                // Tile heat-up overlay — runs in parallel with the prefab
                // wind-up so each tile visibly charges before the bang.
                if (FX_TileHeatOverlay)
                {
                    WDLog.Log("[FX] TileHeatOverlay: FIRED");
                    float cellSize = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                    for (int i = 0; i < tiles.Count; i++)
                    {
                        if (tiles[i] == null) continue;
                        if (meltdownWordTiles != null && !meltdownWordTiles.Contains(tiles[i])) continue;
                        FlipbookExplosion.Instance.PlayTileHeatOverlay(
                            tiles[i].transform.position, cellSize, MELTDOWN_WINDUP_DELAY);
                    }
                }
                else { WDLog.Log("[FX] TileHeatOverlay: SKIPPED"); }

                // Candy-Crush-color-bomb-style primed glow orb per tile —
                // soft-circle additive halo on top of each tile that overlays
                // through the ENTIRE animation (windup + explosion + dissolve)
                // so the glow stays visible front-and-center the whole time.
                if (FX_PrimedGlowOrb)
                {
                    WDLog.Log("[FX] PrimedGlowOrb: FIRED");
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
                else { WDLog.Log("[FX] PrimedGlowOrb: SKIPPED"); }

                // Violent Perlin-noise shake on each tile for the entire
                // windup — magnitude ramps subtle→violent so tension builds
                // into the explosion. Each tile shakes with its own seed so
                // they jitter independently, not in sync.
                WDLog.Log("[FX] MeltdownShake: FIRED");
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
                    WDLog.Log("[FX] MeltdownWindupShake: FIRED");
                    StartCoroutine(MeltdownWindupShake(
                        duration: MELTDOWN_WINDUP_DELAY,
                        startMag: 0.015f,
                        endMag: 0.10f));
                }
                else { WDLog.Log("[FX] MeltdownWindupShake: SKIPPED"); }

                yield return WaitCache.Get(MELTDOWN_WINDUP_DELAY);
            }
            else { WDLog.Log($"[FX] MeltdownPrefab: SKIPPED (toggle={FX_MeltdownPrefab}, meltdownActive={meltdownActive})"); }

            // Determine tier by tiles exploded + chain depth.
            // Thresholds widened 2026-05-01: most typical word matches (3-7
            // tile clusters with no cascade) now hit tier 1 (CC-style pop).
            // Tier 2 reserved for medium clusters (8-11 tiles), tier 3 for
            // large clusters or 2nd cascades, tier 4 for meltdown territory.
            // (Declaration hoisted up to where meltdownActive is computed so
            // the gate can include tier >= 2. Tier value re-used here.)
            WDLog.Log($"[VFX] Explosion tier={tier} tiles={tileCount} chain={chainStep}");

            // Tiered haptic feedback
            if (FX_Haptics) { WDLog.Log("[FX] Haptics: FIRED"); HapticsManager.Explosion(tier); }
            else { WDLog.Log("[FX] Haptics: SKIPPED"); }

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
                WDLog.Log($"[ExplosionTier] tier={tier} tiles={tileCount} chain={chainStep} " +
                          $"(why: {(chainStep >= 3 ? "chain>=3" : tileCount >= 15 ? "tiles>=15" : chainStep >= 2 ? "chain>=2" : tileCount >= 12 ? "tiles>=12" : tileCount >= 8 ? "tiles>=8" : "default")}) " +
                          $"→ LAYERS: tier3Burst={dbgTier3Burst} meltdown={meltdownActive} tier1Pop={dbgTier1Pop}(cascade={isCascadePop}) genericStack={dbgGenericStack}");
            }

            if ((tier == 1 && !meltdownActive) || isCascadePop)
            {
                WDLog.Log($"[FX] Tier1Pop: FIRED (cascade={isCascadePop}, chain={chainStep}, tiles={tileCount})");
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
                    PlayTier1Pop(tiles[i], suppressAudio: true, isCascade: false, startDelay: 0f, instantPop: tileInstantPop, collateral: !wordFlash);   // 11-07: blast tiles = cream chunks, no sparkles
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
                WDLog.Log($"[FX] TileFlash: FIRED (toggle={FX_TileFlash}, meltdown={meltdownActive}, cascade={isCascade})");
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
                WDLog.Log("[FX] FlipbookGlow: FIRED");
                for (int i = 0; i < tiles.Count; i++)
                    if (tiles[i] != null)
                        FlipbookExplosion.Instance.Play(tiles[i].transform.position, tier);
            }
            else { WDLog.Log("[FX] FlipbookGlow: SKIPPED"); }

            // Per-tile meltdown bubble fires UP in the meltdown prefab block
            // now (so it coincides with the spell prefab spawn on the same
            // frame). See the bubble loop right after PlayMeltdownSized.

            // ── Shatter + particles + sparkles ──
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                Vector3 pos = tiles[i].transform.position;
                // The Candy-Crush expanding bubble, layered on the meltdown pop (it used to be Tier1Pop-only).
                if (meltdownActive && MELTDOWN_POP_BUBBLE && FlipbookExplosion.Instance != null)
                {
                    var bsr = tiles[i].GetComponent<SpriteRenderer>();
                    FlipbookExplosion.Instance.PlayBubble(pos, bsr != null ? bsr.color : Color.white, 1.0f, 0.12f);
                }

                // Tile fragments — also fires unconditionally during meltdown,
                // so the tiles visibly shatter at the blast peak regardless
                // of the global FX_TileFragments toggle (which still gates
                // non-meltdown detonations).
                bool fragsThisShot = (FX_TileFragments || meltdownActive || isCascade || isInitial) && TileFragments.Instance != null;
                if (fragsThisShot)
                {
                    if (i == 0) WDLog.Log($"[FX] TileFragments: FIRED (toggle={FX_TileFragments}, meltdown={meltdownActive})");
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
                    TileFragments.Instance.Shatter(tiles[i], 1f, collateral: !wordFlash);   // 11-07: blast collateral = cream chunks, not word green
                }
                else if (i == 0) { WDLog.Log("[FX] TileFragments: SKIPPED"); }

                // Per-tile sparkle spray layer — sits over the chunks so each
                // shatter point also emits sparkle stars. Always fires during
                // meltdown (matches TileFragments pattern); otherwise gated by
                // FX_SparkleSpray toggle.
                bool sparkleSprayThisShot = (FX_SparkleSpray || meltdownActive || isCascade) && SparkleSpray.Instance != null
                                            && (wordFlash || BLAST_COLLATERAL_SPARKLES);   // 11-07: blast collateral doesn't sparkle
                if (sparkleSprayThisShot)
                {
                    if (i == 0) WDLog.Log($"[FX] SparkleSpray (per-tile): FIRED (toggle={FX_SparkleSpray}, meltdown={meltdownActive})");
                    SparkleSpray.Instance.Play(pos, intensity: 0.55f);
                }
                else if (i == 0) { WDLog.Log("[FX] SparkleSpray (per-tile): SKIPPED"); }

                // Sparkle stars + glow (tier 2+)
                if ((FX_SparkleParticles || isCascade) && tier >= 2 && GameParticles.Instance != null)
                {
                    if (i == 0) WDLog.Log("[FX] SparkleParticles: FIRED");
                    GameParticles.Instance.PlayPrimed(pos);
                    if (tier >= 3)
                        GameParticles.Instance.PlayWordScored(pos, tier * 3);
                }
                else if (i == 0) { WDLog.Log($"[FX] SparkleParticles: SKIPPED (toggle={FX_SparkleParticles}, tier={tier})"); }

                // Detonation audio fires once (on the first tile) at the
                // exact frame the tile disappears, so the bang is synced to
                // the visual shatter rather than firing ~50ms early. Fires
                // unconditionally during meltdown (matches TileFragments
                // pattern); otherwise gated by FX_DetonationAudio.
                if (i == 0 && (FX_DetonationAudio || meltdownActive || isCascade || isInitial))
                {
                    WDLog.Log($"[FX] DetonationAudio: FIRED at tile-disappear (toggle={FX_DetonationAudio}, meltdown={meltdownActive}, cascade={isCascade}, initial={isInitial})");
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
                        // QUICK POP (2026-10-23): longer words pop HIGHER — +1 step per letter past four — the job the prime
                        // chime half-did. Same ladder the chain climb uses, so it stays in the chord.
                        int lenStep = (HandManager.QUICK_POP && SurvivalManager.FullBoardActive) ? Mathf.Max(0, wordLength - 4) : 0;
                        GameAudio.Instance?.PlayMatchLine(chainStep + lenStep);
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
                MaybeCrateDebris(tiles[i]);   // crate: wood chunks fly off (before ResetVisuals)
                tiles[i].ResetVisuals();

                // Hide tile — flipbook + fragments cover the visual
                tiles[i].gameObject.SetActive(false);
            }

            // ── Shake (tier 2+) ──
            if ((FX_BoardShake || meltdownActive || isCascade || isInitial) && boardShake)
            {
                WDLog.Log($"[FX] BoardShake: FIRED (toggle={FX_BoardShake}, meltdown={meltdownActive}, cascade={isCascade}, initial={isInitial})");
                PlayBoardShake(tier - 1, tileCount);
                if (tier >= 3) ShakeHandCards(tier - 1, tileCount);
                if (tier >= 4) PlayNeighborRipple(tiles, chainStep);
            }
            else { WDLog.Log($"[FX] BoardShake: SKIPPED (toggle={FX_BoardShake}, boardShake={boardShake})"); }

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
        // ── JOLT SHAKE (2026-10-28, from Spencer's Royal Match/Kingdom clips, ~/Documents/animations/_findings). They never
        // buzz the camera: line clears and most combos don't shake at all; the area bomb gets ONE jolt of ~3% of the screen
        // width (2 frames out), a return, one smaller rebound (~1/3), settled in ~250ms. Ours was a 14-48 vibrato buzz up
        // to ~6.5% of the width on every big word. SHAKE_JOLT = false restores the old shake.
        public static bool  SHAKE_JOLT          = true;
        public static float JOLT_MIN_WEIGHT     = 0.30f;   // old shake magnitudes below this (routine words, small chains) don't shake
        public static float JOLT_SCREEN_FRAC    = 0.030f;  // the biggest jolt, as a fraction of the visible width (RK bomb ~3%)
        public static float JOLT_REBOUND        = 0.35f;   // the rebound, as a fraction of the jolt

        private void PlayJolt(Transform t, float weight)
        {
            if (weight < JOLT_MIN_WEIGHT) { t.position = CAMERA_HOME; return; }
            Camera cam = t.GetComponent<Camera>();
            float viewW = cam != null ? cam.orthographicSize * 2f * cam.aspect : 9.2f;
            // weight runs JOLT_MIN_WEIGHT .. CAMERA_SHAKE_MAX in the old units; map it onto 40%..100% of the full jolt
            float k = Mathf.InverseLerp(JOLT_MIN_WEIGHT, CAMERA_SHAKE_MAX, weight);
            float amp = viewW * JOLT_SCREEN_FRAC * Mathf.Lerp(0.4f, 1f, k);
            Vector2 d = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, -0.3f)).normalized;   // mostly down, like a thump
            Vector3 a = CAMERA_HOME + (Vector3)(d * amp), b = CAMERA_HOME - (Vector3)(d * amp * JOLT_REBOUND);
            DOTween.Sequence().SetTarget(t)
                .Append(t.DOMove(a, 0.033f).SetEase(Ease.OutQuad))
                .AppendInterval(0.033f)
                .Append(t.DOMove(b, 0.08f).SetEase(Ease.InOutQuad))
                .Append(t.DOMove(CAMERA_HOME, 0.10f).SetEase(Ease.OutQuad))
                .OnComplete(() => { if (t != null) t.position = CAMERA_HOME; })
                .OnKill(() => { if (t != null) t.position = CAMERA_HOME; });
        }

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

            mag = Mathf.Min(mag, CAMERA_SHAKE_MAX);   // never past the background's overscan (SceneBootstrap OVERSCAN 1.15)
            if (SHAKE_JOLT) { PlayJolt(t, mag); return; }
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
        private const float HITSTOP_SAFETY_MAX   = 0.5f;   // max freeze ever

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
