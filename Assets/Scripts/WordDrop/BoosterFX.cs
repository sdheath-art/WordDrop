using System;
using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>BOOSTER USE ANIMATIONS (2026-10-29 Spencer: design the booster animations from his Royal Kingdom GIFs - "the
    /// animations need to have a bit more of a wow factor", esp. Free Swap and Spyglass). Breakdown + timings in memory
    /// (project_wordrop_board_art_plan 10-29). No camera shake anywhere: impact = hit-stop + flash + debris, like RK.
    ///  - MALLET: hammer arcs from its slot PAST the tile to a 2-cell wind-up, 0.1s swing, 0.17s hit-stop on a starburst, recoil.
    ///  - COMET (row clear): a comet streaks in from the left edge at row height, each tile bursts as the head passes
    ///    (~45ms/cell), slams the far wall in a flash and fizzles into sparks.
    ///  - SPYGLASS: spyglass flies to centre, a spell dim, its lens sweeps the board as a searchlight, locks on the best word.
    ///  - FREE SWAP: see HandManager (MAGIC SWAP) - sparkle trail + landing burst helpers live here.
    /// ON = false -> every booster resolves instantly like before.</summary>
    public class BoosterFX : MonoBehaviour
    {
        public static bool ON = true;
        public static BoosterFX Instance { get; private set; }
        public static readonly Color FLASH = new Color(2.4f, 2.2f, 1.6f, 1f), GOLD = new Color(2.2f, 1.7f, 0.6f, 1f);
        public static readonly Color COMET_HEAD = new Color(3.0f, 2.6f, 1.9f, 1f), COMET_TAIL = new Color(2.6f, 1.2f, 0.3f, 1f);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            var go = new GameObject("BoosterFX"); DontDestroyOnLoad(go); Instance = go.AddComponent<BoosterFX>();
        }

        private static float Cell => GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;
        private static WordDropFX FX => WordDropFX.Instance;
        private static SpriteRenderer Art(string name, Sprite sp, float size, int order)
        {
            if (sp == null) return null;
            var go = new GameObject(name); var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = sp; sr.sortingOrder = order;
            go.transform.localScale = Vector3.one * size / Mathf.Max(0.001f, sp.bounds.size.x);
            return sr;
        }
        private static void SetSize(SpriteRenderer sr, float size) { if (sr != null) sr.transform.localScale = Vector3.one * size / Mathf.Max(0.001f, sr.sprite.bounds.size.x); }
        private static float EaseOut(float k) => 1f - (1f - k) * (1f - k);
        private static float EaseIn(float k) => k * k;
        private static void Lock(bool on)
        {
            var mc = MatchController.Instance; var hand = HandManager.Instance;
            if (on) { mc?.BeginProcessing(); if (hand != null) hand.IsInteractable = false; }
            else { mc?.EndProcessing(); if (hand != null) hand.IsInteractable = true; }
        }

        /// <summary>Starburst: flare star + soft glow, full on frame 1, fades out. size in world units.</summary>
        public void Starburst(Vector3 at, float size, float life = 0.35f)
        {
            if (FX == null) return;
            var g = FX.MakeFxSprite("BoostFlash", WordDropFX.SoftDot(), FLASH, 62); g.transform.position = at;
            var st = WordDropFX.TwinkleSprite != null ? FX.MakeFxSprite("BoostStar", WordDropFX.TwinkleSprite, FLASH, 63) : null;
            if (st != null) st.transform.position = at;
            StartCoroutine(FlashRoutine(g, st, size, life));
            for (int i = 0; i < 10; i++)
            {
                Vector2 d = UnityEngine.Random.insideUnitCircle.normalized;
                FX.SpawnSpark(at, (Vector3)(d * Cell * UnityEngine.Random.Range(3f, 6f)), Cell * UnityEngine.Random.Range(0.10f, 0.18f), UnityEngine.Random.Range(0.25f, 0.45f), GOLD);
            }
        }
        private IEnumerator FlashRoutine(SpriteRenderer g, SpriteRenderer st, float size, float life)
        {
            float t = 0f;
            while (t < life)
            {
                t += Time.unscaledDeltaTime; float k = t / life;
                if (g != null) { g.transform.localScale = Vector3.one * size * (0.8f + 0.5f * k); g.color = new Color(1, 1, 1, 1f - k * k); }
                if (st != null) { st.transform.localScale = Vector3.one * size * 1.3f * (k < 0.25f ? 0.6f + 1.6f * k : 1f - 0.6f * (k - 0.25f)); st.transform.rotation = Quaternion.Euler(0, 0, 25f * k); st.color = new Color(1, 1, 1, 1f - k); }
                yield return null;
            }
            if (g != null) Destroy(g.gameObject); if (st != null) Destroy(st.gameObject);
        }

        /// <summary>Shatter a tile's visual now (debris in its colours) and hide it - data is cleared by the caller.</summary>
        public static void BurstTile(Tile t)
        {
            if (t == null) return;
            WordDropFX.Instance?.BoosterBreak(t);   // 11-07: crate chunks / treasure / goal flight, like an explosion (was: the crate just vanished)
            TileFragments.Instance?.Shatter(t, 1.1f, collateral: true);   // 11-07: a booster's tiles throw cream chunks, not word green
            t.transform.DOKill(); t.transform.localScale = Vector3.zero;
        }

        // ── FREE SWAP PICK GLOW (2026-10-29, Spencer: "when you are picking the tiles to swap with the glove could we add the same
        // glow from the booster on the tile you select") ─────────────────────────────────────────────────────────────────────
        // Resources/Tiles/swap_pick_glow.png = the armed booster's 3-tier gold glow baked around the pillow tile's silhouette,
        // interior cut out (a RING), canvas 1.8968x the tile sprite. Parented to the tile so it rides its select lift/breathe;
        // sorted above the tiles so the ring isn't hidden under neighbours. Pulses like the armed button.
        public static float PICK_GLOW_RATIO = 1.8968f;
        private SpriteRenderer _pickGlow; private Coroutine _pickPulse;
        private static Sprite s_pickGlow;
        public void ShowPickGlow(Tile t)
        {
            HidePickGlow();
            if (!ON || t == null) return;
            if (s_pickGlow == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/swap_pick_glow");
                if (tex == null) return;
                s_pickGlow = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            var tsr = t.GetComponent<SpriteRenderer>(); if (tsr == null || tsr.sprite == null) return;
            var go = new GameObject("SwapPickGlow"); go.transform.SetParent(t.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.01f);
            _pickGlow = go.AddComponent<SpriteRenderer>(); _pickGlow.sprite = s_pickGlow;
            _pickGlow.sortingOrder = tsr.sortingOrder + 6;
            float baseScale = tsr.sprite.bounds.size.x * PICK_GLOW_RATIO / s_pickGlow.bounds.size.x;
            _pickPulse = StartCoroutine(PickPulse(go.transform, _pickGlow, baseScale));
        }
        public void HidePickGlow()
        {
            if (_pickPulse != null) { StopCoroutine(_pickPulse); _pickPulse = null; }
            if (_pickGlow != null) Destroy(_pickGlow.gameObject);
            _pickGlow = null;
        }
        private IEnumerator PickPulse(Transform tr, SpriteRenderer sr, float baseScale)
        {
            float t = 0f;
            while (tr != null)
            {
                t += Time.unscaledDeltaTime;
                float pop = Mathf.Clamp01(t / 0.14f);                          // pops in from 80%
                float breathe = 0.5f + 0.5f * Mathf.Sin(t * Mathf.PI * 2f * 1.1f);
                tr.localScale = Vector3.one * baseScale * Mathf.Lerp(0.8f, 1f, 1f - (1f - pop) * (1f - pop)) * (1f + 0.04f * breathe);
                sr.color = new Color(1f, 1f, 1f, pop * (0.72f + 0.28f * breathe));
                yield return null;
            }
        }

        // ── MALLET ──────────────────────────────────────────────────────────────────────────────────────────────────
        public void Mallet(Vector3 target, Action onImpact, Action onDone)
        {
            if (!ON) { onImpact?.Invoke(); onDone?.Invoke(); return; }
            StartCoroutine(MalletRoutine(target, onImpact, onDone));
        }
        // Hammer geometry, measured on Resources/Boosters/1hammer (256px): head centre (115,160), handle grip end (190,40) in image
        // px (y down). So at rotation 0 the grip->head direction points 238 deg; rotating the sprite by (theta - 238) aims the head
        // along theta. The swing pivots on the GRIP like a real hammer (2026-10-29 Spencer: "the way the hammer is rotated is wrong").
        // 11-02 ROYAL MALLET (boosters_royal_1102): head centre (122,65) = midway between the two cream caps, grip = the purple pommel
        // (70,238); the head axis is square to the handle (86.6 deg). A real mallet strikes with an END CAP, not with the handle
        // pointing at the tile like the old block hammer - so at the hit the handle runs nearly level (177) with the hand up-right and
        // the left cap face-down, and the head centre sits HAM_CAP above the tile so the cap lands on it.
        // Old block hammer: HAM_HEAD (115,160), HAM_GRIP (190,40), REST 238, wind 70 / hit 228 / recoil 190, no cap offset.
        private static readonly Vector2 HAM_HEAD = new Vector2(122f - 128f, 128f - 65f) / 256f, HAM_GRIP = new Vector2(70f - 128f, 128f - 238f) / 256f;
        private const float HAM_REST_DIR = 73.3f, HAM_CAP = 0.30f;   // cap face below the head centre, fraction of the sprite size
        public static float MALLET_WIND_DIR = 20f, MALLET_HIT_DIR = 177f, MALLET_RECOIL_DIR = 150f;   // hit 228 = near the art's own pose: head block flat, handle up-right   // grip->head directions (deg)
        private static void PlaceHammer(SpriteRenderer h, Vector3 grip, float dirDeg, float size)
        {
            float rot = dirDeg - HAM_REST_DIR;
            SetSize(h, size);
            h.transform.rotation = Quaternion.Euler(0, 0, rot);
            Vector3 gripLocal = (Vector3)(HAM_GRIP * size);
            h.transform.position = grip - h.transform.rotation * gripLocal;   // keep the grip where the hand is
        }
        private static float HandleLen(float size) => (HAM_GRIP - HAM_HEAD).magnitude * size;
        private IEnumerator MalletRoutine(Vector3 target, Action onImpact, Action onDone)
        {
            Lock(true);
            float c = Cell;
            Vector3 from = BoosterHUDSlot.Instance != null ? BoosterHUDSlot.Instance.SlotWorldPos(BoosterManager.ID_MALLET, target.z) : target + Vector3.down * 4f;
            var h = Art("Hammer", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_MALLET), c * 0.5f, 64);
            if (h == null) { onImpact?.Invoke(); Lock(false); onDone?.Invoke(); yield break; }
            float hitSize = c * 1.25f, bigSize = c * 2.0f;
            // the grip (hand) sits up-right of the tile so the head lands with its block face flat on it at the hit
            float hr = MALLET_HIT_DIR * Mathf.Deg2Rad;
            Vector3 grip = target - new Vector3(Mathf.Cos(hr), Mathf.Sin(hr), 0f) * HandleLen(hitSize) + new Vector3(0f, HAM_CAP * hitSize, -0.5f);
            float windDir = MALLET_WIND_DIR, hitDir = MALLET_HIT_DIR;
            // 1 FLY UP from the slot to the wind-up: head raised above the hand, cocked back
            Vector3 windGrip = grip + new Vector3(c * 0.35f, c * 0.25f, 0f);
            Vector3 mid = Vector3.Lerp(from, windGrip, 0.5f) + new Vector3(c * 0.8f, c * 1.0f, 0f);
            float t = 0f, dur = 0.48f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseOut(Mathf.Clamp01(t / dur)); float u = 1f - k;
                Vector3 g = u * u * from + 2f * u * k * mid + k * k * windGrip;
                PlaceHammer(h, g, Mathf.Lerp(windDir - 150f, windDir, k), Mathf.Lerp(c * 0.5f, bigSize, k));   // spins up into the cocked pose (~150 deg flourish, as before)
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.07f);   // hang at the peak
            // 2 SWING: pivot on the grip, head comes over and down onto the tile
            t = 0f; dur = 0.11f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseIn(Mathf.Clamp01(t / dur));
                PlaceHammer(h, Vector3.Lerp(windGrip, grip, k), Mathf.Lerp(windDir, hitDir, k), Mathf.Lerp(bigSize, hitSize, k));
                yield return null;
            }
            PlaceHammer(h, grip, hitDir, hitSize);
            onImpact?.Invoke();
            Starburst(target, c * 2.5f, 0.38f);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("mallet_bonk")) GameAudio.Instance.PlayKit("mallet_bonk"); else GameAudio.Instance?.PlayBigExplosion();   // 11-07 pick: Rubber Cup Hit Hand C5
            yield return new WaitForSecondsRealtime(0.17f);   // HIT-STOP: the hammer holds flat on the tile
            // 3 RECOIL: bounces back up off the tile, then drops away and fades
            t = 0f; dur = 0.32f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur);
                Vector3 g = grip + new Vector3(c * 0.5f * k, c * 0.9f * EaseOut(Mathf.Min(1f, k * 1.6f)) - c * 2.4f * k * k, 0f);
                PlaceHammer(h, g, Mathf.Lerp(hitDir, MALLET_RECOIL_DIR, EaseOut(Mathf.Min(1f, k * 1.5f))), Mathf.Lerp(hitSize, c * 1.5f, k));
                h.color = new Color(1, 1, 1, 1f - Mathf.Clamp01((k - 0.45f) / 0.55f));
                yield return null;
            }
            Destroy(h.gameObject);
            Lock(false);
            onDone?.Invoke();
        }

        // ── COMET (row clear) ───────────────────────────────────────────────────────────────────────────────────────
        public static float COMET_PER_CELL = 0.045f;
        /// <summary>cells = the row's cells left to right; onCell(i) when the head reaches cell i; onDone after the fizzle.</summary>
        public void Comet(int row, Action<int> onCell, Action onDone)
        {
            if (!ON) { for (int i = 0; i < RulesEngine.COLS; i++) onCell?.Invoke(i); onDone?.Invoke(); return; }
            StartCoroutine(CometRoutine(row, onCell, onDone));
        }
        private IEnumerator CometRoutine(int row, Action<int> onCell, Action onDone)
        {
            Lock(true);
            var grid = GridManager.Instance; float c = Cell;
            Vector3 slide = new Vector3(grid != null ? grid.BoardSlideOffsetX : 0f, 0f, -0.7f);
            Vector3 x0 = grid.CellToWorld(0, row) + slide, x1 = grid.CellToWorld(RulesEngine.COLS - 1, row) + slide;
            Vector3 start = x0 + Vector3.left * c * 2.2f, wall = x1 + Vector3.right * c * 0.55f;
            var tail = FX.MakeFxSprite("CometTail", WordDropFX.SoftBeam(), COMET_TAIL, 61);
            var head = FX.MakeFxSprite("CometHead", WordDropFX.SoftDot(), COMET_HEAD, 63);
            var art = Art("CometArt", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_BRAMBLE_SWEEP), c * 0.9f, 64);
            GameAudio.Instance?.PlayRocketFire();
            float total = Vector3.Distance(start, wall), speed = c / COMET_PER_CELL, travelled = 0f; int next = 0; float nextDust = 0f, tt = 0f;
            while (travelled < total)
            {
                float dt = Time.unscaledDeltaTime; tt += dt; travelled = Mathf.Min(total, travelled + speed * dt);
                Vector3 p = start + (wall - start).normalized * travelled;
                head.transform.position = p; head.transform.localScale = Vector3.one * c * (0.9f + 0.15f * Mathf.Sin(tt * 40f));
                float tl = Mathf.Min(travelled, c * 3.5f);
                tail.transform.position = p + Vector3.left * tl * 0.5f; tail.transform.localScale = new Vector3(tl, c * 0.75f, 1f);
                if (art != null) { art.transform.position = p + new Vector3(0, 0, -0.1f); art.transform.rotation = Quaternion.Euler(0, 0, -20f + 8f * Mathf.Sin(tt * 30f)); }
                while (next < RulesEngine.COLS && p.x >= grid.CellToWorld(next, row).x + slide.x - c * 0.1f) { onCell?.Invoke(next); next++; }
                if (tt >= nextDust && WordDropFX.TwinkleSprite != null)
                {
                    nextDust = tt + 0.02f;
                    FX.SpawnSpark(p + (Vector3)(UnityEngine.Random.insideUnitCircle * c * 0.3f), new Vector3(-UnityEngine.Random.Range(1f, 3f), UnityEngine.Random.Range(-1.5f, 1.5f), 0f) * c,
                                  c * UnityEngine.Random.Range(0.10f, 0.2f), UnityEngine.Random.Range(0.25f, 0.45f), UnityEngine.Random.value < 0.5f ? COMET_TAIL : COMET_HEAD);
                }
                yield return null;
            }
            while (next < RulesEngine.COLS) { onCell?.Invoke(next); next++; }
            // slam the far wall
            Starburst(wall, c * 2.2f, 0.35f);
            GameAudio.Instance?.PlayBigExplosion();
            float f = 0f;
            while (f < 0.22f)
            {
                f += Time.unscaledDeltaTime; float k = f / 0.22f;
                head.transform.localScale = Vector3.one * c * (1f - k); head.color = new Color(1, 1, 1, 1f - k);
                tail.transform.localScale = new Vector3(Mathf.Max(0.01f, c * 3.5f * (1f - k)), c * 0.75f * (1f - k), 1f); tail.transform.position = wall + Vector3.left * c * 1.75f * (1f - k);
                if (art != null) { art.transform.position = wall + new Vector3(-c * 0.6f * k, c * 0.8f * k - c * 2f * k * k, -0.1f); art.transform.Rotate(0, 0, 700f * Time.unscaledDeltaTime); art.color = new Color(1, 1, 1, 1f - k); }
                yield return null;
            }
            Destroy(head.gameObject); Destroy(tail.gameObject); if (art != null) Destroy(art.gameObject);
            Lock(false);
            onDone?.Invoke();
        }

        // ── LIGHTNING (the row-clear booster, 2026-10-29 Spencer: "the lightning bolt animation is a little lack luster ... some kind
        // of lightning blast across the screen that clears the row"). Built from the project's electricity VFX: the AllIn1Vfx bolt
        // strips (Particles/bolt_a|b|c), the Particle VFX zap flipbook (electro_sheet, via WordDropFX.TileShocks), soft glow + stars.
        //  1 SUMMON (.30s): the lightning icon flies from its slot to just outside the row's left edge, growing, crackling.
        //  2 CHARGE (.22s): it glows hot, mini-arcs flicker toward the row, the board dims a little.
        //  3 STRIKE: a screen-wide bolt (white-hot core + blue fringe + wide glow), re-rolled every ~35ms, slams across the row;
        //    a white flash band; tiles burst in a fast ripple (LIGHTNING_PER_CELL) with zaps on each; short side forks crackle off.
        //  4 AFTERGLOW (.3s): the bolt thins and flickers out, sparks drift, the dim lifts.
        public static bool  LIGHTNING = true;
        public static float LIGHTNING_PER_CELL = 0.018f;
        public static float LIGHTNING_CHARGE = 0.45f;   // charge step length with the charge-up sound (was 0.22)
        public static readonly Color BOLT_CORE = new Color(2.6f, 2.8f, 3.2f, 1f), BOLT_GLOW = new Color(0.9f, 1.4f, 3.0f, 1f), BOLT_FRINGE = new Color(0.25f, 0.5f, 1f, 0.9f);
        public void LightningRow(int row, Action<int> onCell, Action onDone)
        {
            if (!ON || !LIGHTNING || WordDropFX.BoltSprites == null) { Comet(row, onCell, onDone); return; }
            StartCoroutine(LightningRoutine(row, onCell, onDone));
        }
        private SpriteRenderer BoltLayer(string n, Color c, int order, bool additive)
        {
            var bolts = WordDropFX.BoltSprites;
            SpriteRenderer sr;
            if (additive) sr = FX.MakeFxSprite(n, bolts[0], c, order);
            else { sr = new GameObject(n).AddComponent<SpriteRenderer>(); sr.sprite = bolts[0]; sr.sortingOrder = order; sr.color = c; }
            return sr;
        }
        private static void Stretch(SpriteRenderer sr, Vector3 a, Vector3 b, float thick, int shape, bool flip)
        {
            var bolts = WordDropFX.BoltSprites; sr.sprite = bolts[shape % bolts.Length];
            Vector3 d = b - a; float len = d.magnitude;
            sr.transform.position = (a + b) * 0.5f;
            sr.transform.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
            float h = thick / Mathf.Max(0.001f, sr.sprite.bounds.size.y);
            sr.transform.localScale = new Vector3(len / Mathf.Max(0.001f, sr.sprite.bounds.size.x), h * (flip ? -1f : 1f), 1f);
        }
        private IEnumerator LightningRoutine(int row, Action<int> onCell, Action onDone)
        {
            Lock(true);
            var grid = GridManager.Instance; float c = Cell;
            Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, -0.7f);
            Vector3 left = grid.CellToWorld(0, row) + slide, right = grid.CellToWorld(RulesEngine.COLS - 1, row) + slide;
            var cam = Camera.main;
            float screenL = cam != null ? cam.ViewportToWorldPoint(new Vector3(0f, 0.5f, 10f)).x : left.x - c * 2f;
            float screenR = cam != null ? cam.ViewportToWorldPoint(new Vector3(1f, 0.5f, 10f)).x : right.x + c * 2f;
            Vector3 perch = new Vector3(Mathf.Max(screenL + c * 0.45f, left.x - c * 0.9f), left.y, left.z - 0.2f);
            Vector3 from = BoosterHUDSlot.Instance != null ? BoosterHUDSlot.Instance.SlotWorldPos(BoosterManager.ID_BRAMBLE_SWEEP, perch.z) : perch + Vector3.down * 4f;
            var icon = Art("BoltIcon", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_BRAMBLE_SWEEP), c * 0.6f, 66);
            var halo = FX.MakeFxSprite("BoltHalo", WordDropFX.SoftDot(), BOLT_GLOW, 65); halo.color = new Color(1, 1, 1, 0f);
            // 1 SUMMON
            GameAudio.Instance?.PlayWhooshFast();
            float t = 0f, dur = 0.30f; Vector3 mid = Vector3.Lerp(from, perch, 0.5f) + new Vector3(-c * 0.8f, c * 1.4f, 0);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseOut(Mathf.Clamp01(t / dur)); float u = 1f - k;
                Vector3 p = u * u * from + 2f * u * k * mid + k * k * perch;
                if (icon != null) { icon.transform.position = p; SetSize(icon, Mathf.Lerp(c * 0.6f, c * 1.5f, k)); icon.transform.rotation = Quaternion.Euler(0, 0, 360f * k); }
                halo.transform.position = p + new Vector3(0, 0, 0.05f); halo.transform.localScale = Vector3.one * c * 2f * k; halo.color = new Color(1, 1, 1, 0.6f * k);
                yield return null;
            }
            if (icon != null) icon.transform.rotation = Quaternion.identity;
            // 2 CHARGE (11-07 Spencer: "the lightning booster should have a charging sound before it fires" - his pick Particles
            // Filter Modulation Zappy C2, trimmed to 0.5 s; the charge stretched 0.22 -> LIGHTNING_CHARGE so it builds into the strike)
            FX?.BoardDim(0.35f, 0.12f);
            var arc = BoltLayer("ChargeArc", BOLT_CORE, 64, true);
            GameAudio.Instance?.PlayKit("lightning_charge");
            t = 0f; dur = GameAudio.Instance != null && GameAudio.Instance.HasKit("lightning_charge") ? LIGHTNING_CHARGE : 0.22f; float reroll = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = t / dur;
                if (icon != null) icon.transform.localScale = Vector3.one * (c * 1.5f / Mathf.Max(0.001f, icon.sprite.bounds.size.x)) * (1f + 0.12f * Mathf.Sin(t * 70f));
                halo.transform.localScale = Vector3.one * c * (2f + 1.5f * k); halo.color = new Color(1, 1, 1, 0.6f + 0.4f * k);
                if (t >= reroll)
                {
                    reroll = t + 0.04f;
                    Vector3 tip = perch + new Vector3(c * UnityEngine.Random.Range(0.8f, 1.8f), c * UnityEngine.Random.Range(-0.6f, 0.6f), 0);
                    Stretch(arc, perch, tip, c * 0.45f, UnityEngine.Random.Range(0, 3), UnityEngine.Random.value < 0.5f);
                    arc.color = new Color(1, 1, 1, UnityEngine.Random.value < 0.25f ? 0f : 1f);
                }
                yield return null;
            }
            Destroy(arc.gameObject);
            // 3 STRIKE
            Vector3 a = perch + new Vector3(c * 0.3f, 0, 0), b = new Vector3(screenR + c * 0.5f, left.y, left.z);
            var glow = FX.MakeFxSprite("BoltGlow", WordDropFX.SoftBeam(), BOLT_GLOW, 61);
            var fringe = BoltLayer("BoltFringe", BOLT_FRINGE, 62, false);
            var core = BoltLayer("BoltCore", BOLT_CORE, 63, true);
            var core2 = BoltLayer("BoltCore2", BOLT_CORE, 63, true);
            var flash = FX.MakeFxSprite("RowFlash", WordDropFX.SoftBeam(), new Color(2.2f, 2.3f, 2.6f, 1f), 60);
            flash.transform.position = (a + b) * 0.5f; flash.transform.localScale = new Vector3((b - a).magnitude * 1.1f, c * 2.6f, 1f);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("lightning_zap")) GameAudio.Instance.PlayKit("lightning_zap"); else { GameAudio.Instance?.PlayBigExplosion(); GameAudio.Instance?.PlayRocketFire(); }   // 11-07 pick: Wide Zap C6
            Starburst(a, c * 2.2f, 0.3f);
            var forks = new List<SpriteRenderer>();
            for (int i = 0; i < 4; i++) { var f = BoltLayer("Fork" + i, BOLT_CORE, 63, true); forks.Add(f); }
            int next = 0; t = 0f; reroll = 0f; float strike = RulesEngine.COLS * LIGHTNING_PER_CELL, life = strike + 0.42f;
            while (t < life)
            {
                t += Time.unscaledDeltaTime;
                // reach: the bolt's head races across the row in `strike` seconds, then the full bolt holds and fades
                float reachK = Mathf.Clamp01(t / Mathf.Max(0.01f, strike));
                Vector3 head = Vector3.Lerp(a, b, reachK * reachK * (3f - 2f * reachK));
                while (next < RulesEngine.COLS && head.x >= grid.CellToWorld(next, row).x + slide.x - c * 0.2f)
                {
                    onCell?.Invoke(next);
                    Vector3 cp = grid.CellToWorld(next, row) + slide;
                    if (FX != null) StartCoroutine(FX.TileShocks(cp, c, 0.35f));
                    for (int s2 = 0; s2 < 3; s2++)
                    { Vector2 dd = UnityEngine.Random.insideUnitCircle.normalized; FX?.SpawnSpark(cp, (Vector3)(dd * c * UnityEngine.Random.Range(2f, 5f)), c * UnityEngine.Random.Range(0.1f, 0.18f), 0.35f, BOLT_CORE); }
                    next++;
                }
                float fade = t < strike + 0.12f ? 1f : 1f - (t - strike - 0.12f) / 0.30f;
                float thick = c * (t < strike + 0.06f ? 1.0f : Mathf.Lerp(1.0f, 0.35f, Mathf.Clamp01((t - strike) / 0.36f)));
                if (t >= reroll)
                {
                    reroll = t + 0.035f;
                    Stretch(core, a, head, thick, UnityEngine.Random.Range(0, 3), UnityEngine.Random.value < 0.5f);
                    Stretch(core2, a, head, thick * 0.7f, UnityEngine.Random.Range(0, 3), UnityEngine.Random.value < 0.5f);
                    Stretch(fringe, a, head, thick * 1.35f, UnityEngine.Random.Range(0, 3), UnityEngine.Random.value < 0.5f);
                    foreach (var f in forks)
                    {   // short side forks crackling off random points along the lit part of the bolt
                        Vector3 root = Vector3.Lerp(a, head, UnityEngine.Random.Range(0.1f, 0.95f));
                        Vector3 tip = root + new Vector3(c * UnityEngine.Random.Range(-0.4f, 1.2f), c * UnityEngine.Random.Range(0.8f, 1.7f) * (UnityEngine.Random.value < 0.5f ? 1f : -1f), 0);
                        Stretch(f, root, tip, c * 0.35f, UnityEngine.Random.Range(0, 3), UnityEngine.Random.value < 0.5f);
                        f.color = new Color(1, 1, 1, (UnityEngine.Random.value < 0.4f ? 0f : 1f) * fade);
                    }
                }
                glow.transform.position = (a + head) * 0.5f; glow.transform.localScale = new Vector3(Mathf.Max(0.01f, (head - a).magnitude), c * 2.0f * (0.85f + 0.3f * Mathf.PerlinNoise(t * 30f, 0f)), 1f);
                float flick = 0.75f + 0.25f * Mathf.PerlinNoise(t * 45f, 3f);
                core.color = new Color(1, 1, 1, fade * flick); core2.color = new Color(1, 1, 1, fade * 0.8f);
                fringe.color = new Color(BOLT_FRINGE.r, BOLT_FRINGE.g, BOLT_FRINGE.b, BOLT_FRINGE.a * fade);
                glow.color = new Color(1, 1, 1, 0.8f * fade);
                flash.color = new Color(1, 1, 1, Mathf.Clamp01(1f - t / 0.28f) * 0.9f);
                if (icon != null) icon.color = new Color(1, 1, 1, Mathf.Clamp01(1f - t / 0.2f));
                halo.color = new Color(1, 1, 1, Mathf.Clamp01(1f - t / 0.25f));
                yield return null;
            }
            while (next < RulesEngine.COLS) { onCell?.Invoke(next); next++; }
            foreach (var g in new SpriteRenderer[] { glow, fringe, core, core2, flash, halo }) if (g != null) Destroy(g.gameObject);
            foreach (var f in forks) if (f != null) Destroy(f.gameObject);
            if (icon != null) Destroy(icon.gameObject);
            FX?.BoardDimLift(0.2f);
            Lock(false);
            onDone?.Invoke();
        }

        // ── AXE (column clear, 2026-10-29) ───────────────────────────────────────────────────────────────────────────
        // The axe flies above the column, cocks back, then plunges blade-first down the whole column: every tile bursts as the
        // blade passes, a white-gold slash trail follows it, it bites into the bottom with a hit-stop + starburst, wobbles, lifts out.
        // Axe geometry measured on Resources/Boosters/1axe (256px): the blade's leading point is ~(23,44) px (y down); rotating the
        // sprite +110 deg turns the curved blade edge DOWN with the handle up-right (checked in a pose mock).
        // 2026-11-02 royal axe (purple blade, gold edge, short red grip): leading point re-measured (lowest pixel after +110 deg) at
        // ~(23,72); the old grey axe was (23,44) - its art is in WordDrop_Art/specials_backup_1102/boosters_axe_v1/.
        private static readonly Vector2 AXE_BLADE = new Vector2(23f - 128f, 128f - 72f) / 256f;
        public static float AXE_CHOP_ROT = 110f, AXE_WIND_ROT = 200f, AXE_PER_ROW = 0.028f;
        public static readonly Color AXE_TRAIL = new Color(2.4f, 2.1f, 1.5f, 1f);
        public void AxeColumn(int col, Action<int> onCell, Action onDone)
        {
            if (!ON || GridManager.Instance == null) { for (int r = 0; r < RulesEngine.ROWS; r++) onCell?.Invoke(r); onDone?.Invoke(); return; }
            StartCoroutine(AxeRoutine(col, onCell, onDone));
        }
        private static void PlaceAxe(SpriteRenderer a, Vector3 blade, float rot, float size)
        {
            SetSize(a, size);
            a.transform.rotation = Quaternion.Euler(0, 0, rot);
            a.transform.position = blade - a.transform.rotation * (Vector3)(AXE_BLADE * size);   // pinned at the blade point
        }
        private IEnumerator AxeRoutine(int col, Action<int> onCell, Action onDone)
        {
            Lock(true);
            var grid = GridManager.Instance; float c = Cell;
            Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, -0.7f);
            int topRow = RulesEngine.TopRealRow(col); if (topRow < 0) topRow = RulesEngine.ROWS - 1;
            int botRow = 0; while (botRow < topRow && RulesEngine.IsVoid(col, botRow)) botRow++;
            Vector3 top = grid.CellToWorld(col, topRow) + slide, bottom = grid.CellToWorld(col, botRow) + slide;
            var cam = Camera.main;
            float screenTop = cam != null ? cam.ViewportToWorldPoint(new Vector3(0.5f, 1f, 10f)).y : top.y + c * 3f;
            Vector3 perch = new Vector3(top.x, Mathf.Min(top.y + c * 1.5f, screenTop - c * 0.9f), top.z - 0.2f);
            Vector3 from = BoosterHUDSlot.Instance != null ? BoosterHUDSlot.Instance.SlotWorldPos(BoosterManager.ID_AXE, perch.z) : perch + Vector3.down * 6f;
            var axe = Art("Axe", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_AXE), c * 0.6f, 66);
            if (axe == null) { for (int r = botRow; r <= topRow; r++) onCell?.Invoke(r); Lock(false); onDone?.Invoke(); yield break; }
            float big = c * 1.7f;
            // 1 SUMMON: spins up out of its slot to above the column
            GameAudio.Instance?.PlayWhooshFast();
            float t = 0f, dur = 0.32f; Vector3 mid = Vector3.Lerp(from, perch, 0.5f) + new Vector3(c * 1.2f, c * 0.6f, 0f);
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseOut(Mathf.Clamp01(t / dur)); float u = 1f - k;
                PlaceAxe(axe, u * u * from + 2f * u * k * mid + k * k * perch, Mathf.Lerp(AXE_WIND_ROT - 360f, AXE_WIND_ROT, k), Mathf.Lerp(c * 0.6f, big, k));
                yield return null;
            }
            // 2 WIND-UP: lifts and cocks back, a glint runs along the blade, the board dims
            FX?.BoardDim(0.3f, 0.12f);
            if (FX != null && WordDropFX.TwinkleSprite != null) StartCoroutine(FX.Twinkle(perch + new Vector3(0, c * 0.4f, -0.1f), c * 0.8f, 0.3f, 0f, Vector3.zero, 67));
            t = 0f; dur = 0.16f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseOut(Mathf.Clamp01(t / dur));
                PlaceAxe(axe, perch + new Vector3(0f, c * 0.35f * k, 0f), AXE_WIND_ROT + 15f * k, big);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.05f);
            // 3 CHOP: snaps blade-down and plunges the column; each tile bursts as the blade passes
            Vector3 start = perch + new Vector3(0f, c * 0.35f, 0f), end = bottom + new Vector3(0f, -c * 0.32f, 0f);
            var trail = FX.MakeFxSprite("AxeTrail", WordDropFX.SoftBeam(), AXE_TRAIL, 61);
            var core = FX.MakeFxSprite("AxeTrailCore", WordDropFX.SoftBeam(), new Color(3f, 3f, 3f, 1f), 62);
            trail.transform.rotation = core.transform.rotation = Quaternion.Euler(0, 0, 90f);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("axe_swing")) GameAudio.Instance.PlayKit("axe_swing"); else GameAudio.Instance?.PlayRocketFire();   // 11-07 pick: Swipe Fast Short Air C
            int next = topRow; float plunge = Mathf.Max(0.12f, (topRow - botRow + 1) * AXE_PER_ROW);
            t = 0f;
            while (t < plunge)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / plunge); float e = k * k;   // accelerates down
                Vector3 blade = Vector3.Lerp(start, end, e);
                float rot = Mathf.Lerp(AXE_WIND_ROT + 15f, AXE_CHOP_ROT, Mathf.Clamp01(k * 3f)) + 4f * Mathf.Sin(t * 60f) * (1f - k);
                PlaceAxe(axe, blade, rot, Mathf.Lerp(big, c * 1.45f, k));
                while (next >= botRow && blade.y <= grid.CellToWorld(col, next).y + slide.y + c * 0.25f)
                {
                    onCell?.Invoke(next);
                    Vector3 cp = grid.CellToWorld(col, next) + slide;
                    for (int s2 = 0; s2 < 4; s2++)
                    {   // chips fly out sideways off the blade
                        float side = s2 % 2 == 0 ? 1f : -1f;
                        FX?.SpawnSpark(cp, new Vector3(side * c * UnityEngine.Random.Range(2.5f, 5f), c * UnityEngine.Random.Range(0.5f, 2.5f), 0f), c * UnityEngine.Random.Range(0.1f, 0.18f), 0.35f, GOLD);
                    }
                    next--;
                }
                float len = Mathf.Max(0.01f, start.y - blade.y);
                trail.transform.position = new Vector3(start.x, blade.y + len * 0.5f, start.z + 0.05f); trail.transform.localScale = new Vector3(len, c * 1.1f, 1f);
                core.transform.position = trail.transform.position; core.transform.localScale = new Vector3(len, c * 0.35f, 1f);
                trail.color = new Color(1, 1, 1, 0.85f); core.color = new Color(1, 1, 1, 0.9f);
                yield return null;
            }
            while (next >= botRow) { onCell?.Invoke(next); next--; }
            // 4 BITE: hit-stop in the bottom tile, starburst, the trail flares and fades, the axe wobbles in place
            PlaceAxe(axe, end, AXE_CHOP_ROT, c * 1.45f);
            Starburst(bottom, c * 2.4f, 0.36f);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("axe_hit")) GameAudio.Instance.PlayKit("axe_hit"); else GameAudio.Instance?.PlayBigExplosion();   // 11-07 pick: Log Fall On Log C3 (the chop lands)
            float bt = 0f, bdur = 0.42f;
            // 11-17 (Spencer: the empty column sat a beat too long before anything fell - "a little bit" bothersome). The tiles used
            // to wait for the whole bite + lift-out (0.66 s). Now the board is released and the fall starts as soon as the hit-stop
            // ends; the axe wobbles, lifts out and fades OVER the falling tiles. Same order as before (unlock, then onDone), earlier.
            bool released = false;
            while (bt < bdur)
            {
                bt += Time.unscaledDeltaTime; float k = Mathf.Clamp01(bt / bdur);
                if (!released && bt >= AXE_FALL_AFTER) { released = true; Lock(false); onDone?.Invoke(); }
                float hold = bt < 0.14f ? 0f : (bt - 0.14f) / (bdur - 0.14f);   // first 0.14s = hit-stop
                PlaceAxe(axe, end, AXE_CHOP_ROT + 7f * Mathf.Sin(hold * 28f) * (1f - hold), c * 1.45f);
                float f = 1f - k;
                trail.color = new Color(1, 1, 1, 0.85f * f); core.color = new Color(1, 1, 1, 0.9f * f * f);
                trail.transform.localScale = new Vector3(trail.transform.localScale.x, c * 1.1f * (1f + 0.4f * k), 1f);
                yield return null;
            }
            Destroy(trail.gameObject); Destroy(core.gameObject);
            // 5 LIFT OUT: pops up out of the board and fades
            t = 0f; dur = 0.24f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur);
                PlaceAxe(axe, end + new Vector3(c * 0.4f * k, c * 1.2f * EaseOut(k), 0f), AXE_CHOP_ROT + 30f * k, c * 1.45f * (1f + 0.15f * k));
                axe.color = new Color(1, 1, 1, 1f - k);
                yield return null;
            }
            Destroy(axe.gameObject);
            FX?.BoardDimLift(0.2f);
            if (!released) { Lock(false); onDone?.Invoke(); }
        }
        public static float AXE_FALL_AFTER = 0.16f;   // seconds into the bite when the column starts to refill (0.14 = the hit-stop); 9 = the old wait

        // ── SPYGLASS (search) ───────────────────────────────────────────────────────────────────────────────────────
        // 11-02 royal spyglass: its lens sits UPPER-LEFT of the art (old icon: lower-left, tilted 35) - the glass hovers down-right of
        // the searchlight, so at 0 the lens already faces its beam
        public static float SPYGLASS_TILT = 0f;
        public void Spyglass(Action onDone)
        {
            var rules = RulesEngine.Instance;
            List<Vector2Int> word = null; Vector2Int a = default, b = default;
            bool found = rules != null && rules.FindBestSwapHint(out a, out b, out word) && word != null && word.Count > 0;
            if (!ON || !found) { onDone?.Invoke(); return; }
            StartCoroutine(SpyglassRoutine(a, b, word, onDone));
        }
        private IEnumerator SpyglassRoutine(Vector2Int a, Vector2Int b, List<Vector2Int> word, Action onDone)
        {
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("spyglass_extend")) GameAudio.Instance.PlayKit("spyglass_extend");   // 11-07 pick: Measure Tape Latch A5
            Lock(true);
            var grid = GridManager.Instance; float c = Cell;
            Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, -0.8f);
            Vector3 centre = grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS / 2) + slide;
            Vector3 from = BoosterHUDSlot.Instance != null ? BoosterHUDSlot.Instance.SlotWorldPos(BoosterManager.ID_SPYGLASS, centre.z) : centre + Vector3.down * 5f;
            FX?.BoardDim(0.55f, 0.2f);
            var glass = Art("Spyglass", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_SPYGLASS), c * 0.6f, 66);
            var beam = FX.MakeFxSprite("SpyLight", WordDropFX.SoftDot(), new Color(1.8f, 1.7f, 1.3f, 1f), 45);   // over the dim (44), under tiles' FX
            beam.color = new Color(1, 1, 1, 0f);
            // fly in, grow
            float t = 0f, dur = 0.35f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = EaseOut(Mathf.Clamp01(t / dur));
                glass.transform.position = Vector3.Lerp(from, centre + Vector3.up * c * 1.5f, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * c;
                SetSize(glass, Mathf.Lerp(c * 0.6f, c * 2.4f, k)); glass.transform.rotation = Quaternion.Euler(0, 0, Mathf.Lerp(-30f, 10f, k));
                yield return null;
            }
            GameAudio.Instance?.PlayWhooshFast();
            // SEARCH: the lens light sweeps a figure-eight over the board, glinting letters as it passes
            Vector3 target = Vector3.zero; foreach (var p in word) target += grid.CellToWorld(p.x, p.y) + slide; target /= word.Count;
            float w = RulesEngine.COLS * c * 0.36f, hgt = RulesEngine.ROWS * grid.RowPitch * 0.33f;
            t = 0f; dur = 1.1f; float nextGlint = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur);
                float ang = k * Mathf.PI * 2f;
                Vector3 sweep = centre + new Vector3(Mathf.Sin(ang) * w, Mathf.Sin(ang * 2f) * hgt * 0.8f + Mathf.Cos(ang) * hgt * 0.3f, 0f);
                Vector3 lp = k < 0.75f ? sweep : Vector3.Lerp(sweep, target, EaseOut((k - 0.75f) / 0.25f));
                beam.transform.position = lp + new Vector3(0, 0, 0.05f); beam.transform.localScale = Vector3.one * c * 2.6f;
                beam.color = new Color(1, 1, 1, Mathf.Min(1f, t / 0.15f) * 0.55f);
                glass.transform.position = lp + new Vector3(c * 0.9f, -c * 0.9f, -0.1f);
                glass.transform.rotation = Quaternion.Euler(0, 0, SPYGLASS_TILT + 10f * Mathf.Sin(t * 9f));
                SetSize(glass, c * 2.0f);
                if (t >= nextGlint && WordDropFX.TwinkleSprite != null)
                {
                    nextGlint = t + 0.07f;
                    StartCoroutine(FX.Twinkle(lp + (Vector3)(UnityEngine.Random.insideUnitCircle * c * 0.9f), c * 0.35f, 0.3f, 0f, Vector3.zero, 63));
                }
                yield return null;
            }
            // LOCK ON: zoom + letters light gold one by one
            Starburst(target, c * 2.2f, 0.35f);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("spyglass_reveal")) GameAudio.Instance.PlayKit("spyglass_reveal"); else GameAudio.Instance?.PlayScorePowerup();   // 11-07 pick: Positive Highlight B2
            float zt = 0f;
            while (zt < 0.25f) { zt += Time.unscaledDeltaTime; float k = zt / 0.25f; beam.transform.localScale = Vector3.one * c * Mathf.Lerp(2.6f, 1.6f + 0.6f * word.Count * 0.3f, k); yield return null; }
            for (int i = 0; i < word.Count; i++)
            {
                var tile = grid.GetTile(word[i].x, word[i].y);
                if (tile != null) { tile.LiftForFx(1.2f); tile.transform.DOKill(true); tile.transform.DOPunchScale(Vector3.one * 0.18f, 0.25f, 5, 0.6f).SetUpdate(true); }
                StartCoroutine(FX.Twinkle(grid.CellToWorld(word[i].x, word[i].y) + slide, c * 0.7f, 0.4f, 0f, Vector3.zero, 63));
                yield return new WaitForSecondsRealtime(0.07f);
            }
            // spyglass shrinks away with a glint; dim lifts; the strong hint takes over (outline + the move)
            t = 0f; dur = 0.3f; Vector3 gp = glass.transform.position;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime; float k = Mathf.Clamp01(t / dur);
                glass.transform.position = gp + Vector3.up * c * 1.5f * k; SetSize(glass, Mathf.Lerp(c * 2.0f, 0.01f, EaseIn(k)));
                beam.color = new Color(1, 1, 1, 0.55f * (1f - k));
                yield return null;
            }
            if (WordDropFX.TwinkleSprite != null) StartCoroutine(FX.Twinkle(glass.transform.position, c * 1.2f, 0.35f, 0f, Vector3.zero, 66));
            Destroy(glass.gameObject); Destroy(beam.gameObject);
            FX?.BoardDimLift(0.25f);
            Lock(false);
            onDone?.Invoke();
        }

        // ── FREE SWAP helpers (MAGIC SWAP in HandManager.BoardSwapTurnSequence) ──────────────────────────────────────
        public void SwapTrail(Transform t, float life)
        {
            if (FX == null || t == null) return;
            StartCoroutine(TrailRoutine(t, life));
        }
        private IEnumerator TrailRoutine(Transform tr, float life)
        {
            float t = 0f, next = 0f, c = Cell;
            while (t < life && tr != null)
            {
                t += Time.unscaledDeltaTime;
                if (t >= next && WordDropFX.TwinkleSprite != null)
                {
                    next = t + 0.03f;
                    StartCoroutine(FX.Twinkle(tr.position + (Vector3)(UnityEngine.Random.insideUnitCircle * c * 0.25f) + new Vector3(0, 0, -0.3f), c * UnityEngine.Random.Range(0.22f, 0.38f), 0.35f, 0f, Vector3.zero, 63));
                }
                yield return null;
            }
        }
        public void SwapLand(Vector3 at)
        {
            if (FX == null) return;
            Starburst(at, Cell * 1.6f, 0.3f);
            FX.BonusLandSparkle(at);
        }
        /// <summary>The glove: swoops from its slot between the two tiles, 'grabs' (squash), and leaves.</summary>
        public void Glove(Vector3 a, Vector3 b, float dur)
        {
            var g = Art("Glove", BoosterHUDSlot.BoosterIcon(BoosterManager.ID_FREE_SWAP), Cell * 0.6f, 66);
            if (g == null) return;
            Vector3 from = BoosterHUDSlot.Instance != null ? BoosterHUDSlot.Instance.SlotWorldPos(BoosterManager.ID_FREE_SWAP, a.z - 0.8f) : (a + b) * 0.5f + Vector3.down * 4f;
            Vector3 mid = (a + b) * 0.5f + new Vector3(0, Cell * 1.1f, -0.8f);
            var seq = DOTween.Sequence().SetUpdate(true).SetTarget(g);
            g.transform.position = from;
            seq.Append(g.transform.DOMove(mid, dur * 0.45f).SetEase(Ease.OutCubic));
            seq.Join(g.transform.DOScale(g.transform.localScale * 2.4f, dur * 0.45f).SetEase(Ease.OutBack));
            seq.Append(g.transform.DOPunchScale(Vector3.one * 0.3f * g.transform.localScale.x, dur * 0.25f, 4, 0.5f));
            seq.Append(g.transform.DOMove(mid + Vector3.up * Cell * 2.5f, dur * 0.3f).SetEase(Ease.InQuad));
            seq.Join(g.DOFade(0f, dur * 0.3f));
            seq.OnComplete(() => { if (g != null) Destroy(g.gameObject); });
        }
    }
}
