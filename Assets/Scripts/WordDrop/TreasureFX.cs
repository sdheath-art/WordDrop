using System.Collections;
using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>TREASURE TILE effects (2026-10-27, Spencer: "that old animation was terrible").
    ///   HIT  : the tile squashes and flashes, the gold band that came off (Blockers/treasure_band_N, cut as the difference between
    ///          states) pops off the front - up, sideways, spinning - and falls away; a ring of gold sparks; a metal clink.
    ///   OPEN : the box swells and glows, then bursts - wood + gold chunks fly, a gold ring flashes, and a FOUNTAIN of coins arcs
    ///          up out of it, hangs, then streams into the HUD (reward coin / score panel), punching it as each lands. A "+N" coin
    ///          label rises from the cell. Coins are PAID by HandManager the moment the treasure opens; this is presentation only.
    /// All world-space SpriteRenderers on the FX sorting band (46-49), unscaled time so a paused overlay can't freeze them.</summary>
    public class TreasureFX : MonoBehaviour
    {
        private static TreasureFX s_inst;
        private static TreasureFX Inst { get { if (s_inst == null) { var go = new GameObject("TreasureFX"); s_inst = go.AddComponent<TreasureFX>(); } return s_inst; } }

        public static int   OPEN_COINS_VISUAL = 8;
        public static float BAND_GRAVITY = 26f;          // cells / s^2
        private static readonly Color GOLD = new Color(1f, 0.82f, 0.25f, 1f);

        private static Sprite s_spark, s_chunkTex;
        private static Sprite[] s_chunks;
        private static Sprite Spark()
        {
            if (s_spark != null) return s_spark;
            var t = Resources.Load<Texture2D>("Particles/glow");   // has real alpha (soft_circle is white-on-black, additive-only: drew black squares)
            if (t != null) s_spark = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
            return s_spark;
        }
        private static Sprite[] Chunks()
        {
            if (s_chunks != null) return s_chunks;
            var tex = Resources.Load<Texture2D>("Blockers/treasure_1");
            if (tex == null) { s_chunks = new Sprite[0]; return s_chunks; }
            float w = tex.width, h = tex.height;
            // 2026-10-27: cut for the TREASURE CHEST art (domed lid on top, lock in the middle, corner caps at the bottom)
            var rects = new[] {
                new Rect(w*0.20f, h*0.66f, w*0.30f, h*0.18f), new Rect(w*0.50f, h*0.66f, w*0.30f, h*0.18f),   // lid planks
                new Rect(w*0.18f, h*0.70f, w*0.12f, h*0.22f), new Rect(w*0.72f, h*0.70f, w*0.12f, h*0.22f),   // lid straps
                new Rect(w*0.02f, h*0.12f, w*0.16f, h*0.16f), new Rect(w*0.82f, h*0.12f, w*0.16f, h*0.16f),   // corner caps
                new Rect(w*0.15f, h*0.28f, w*0.32f, h*0.16f), new Rect(w*0.53f, h*0.28f, w*0.32f, h*0.16f),   // front planks
                new Rect(w*0.39f, h*0.42f, w*0.22f, h*0.24f) };                                                // the lock
            s_chunks = new Sprite[rects.Length];
            for (int k = 0; k < rects.Length; k++) s_chunks[k] = Sprite.Create(tex, rects[k], new Vector2(0.5f, 0.5f), w);
            return s_chunks;
        }
        private static float Cell => GridManager.Instance != null ? GridManager.Instance.CellSize : 0.9f;

        private static SpriteRenderer Make(string name, Sprite sp, Vector3 pos, float scale, int order, Color c)
        {
            var go = new GameObject(name); var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sp; sr.sortingOrder = order; sr.color = c;
            go.transform.position = pos; go.transform.localScale = Vector3.one * scale; return sr;
        }

        // ── HIT ───────────────────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>A band comes off. <paramref name="hitsBefore"/> = hits left BEFORE this hit (3 or 2).</summary>
        public static void Hit(Tile tile, int hitsBefore)
        {
            if (tile == null) return;
            var pos = tile.transform.position + new Vector3(0f, 0f, -0.8f);
            float cell = Cell;
            // 1. the chest REACTS: a hard squash + a wobble (rotation only - the tile may be falling) + a hot flash
            var tr = tile.transform;            // no DOKill: the tile may be mid-move/fall; scale + rotation only
            var s0 = tr.localScale;
            DOTween.Sequence().SetUpdate(true)
                .Append(tr.DOScale(new Vector3(s0.x * 1.18f, s0.y * 0.8f, s0.z), 0.07f).SetEase(Ease.OutQuad))
                .Append(tr.DOScale(s0, 0.45f).SetEase(Ease.OutElastic, 1.2f, 0.3f));
            tr.DOPunchRotation(new Vector3(0f, 0f, 16f), 0.55f, 9, 0.6f).SetUpdate(true);
            var srT = tile.GetComponent<SpriteRenderer>();
            if (srT != null) { srT.color = new Color(1.9f, 1.75f, 1.3f, 1f); srT.DOColor(Color.white, 0.3f).SetUpdate(true); }
            // 2. the band that came off UNLATCHES (lifts toward you, flashes) then DROPS down off the board where you can follow it
            var band = Tile.TreasureBandSprite(hitsBefore);   // (old gold-band art; null for the chest)
            if (band != null) Inst.StartCoroutine(Inst.BandDrop(Make("TreasureBand", band, pos + new Vector3(0, 0, -0.05f), cell * 0.98f, 49, Color.white), cell));
            else Inst.Chips(pos, cell, hitsBefore);
            if (hitsBefore == 2)   // the hit that cracks the lid OPEN: the whole chest hops up a little and lands
                tr.DOPunchScale(new Vector3(-0.06f, 0.16f, 0f), 0.35f, 3, 0.5f).SetUpdate(true);   // scale only - a position punch would snap a falling tile back
            // 3. a dust puff + sparks off the hit
            Inst.Dust(pos, cell);
            Inst.Sparks(pos, cell, 12, 1.15f);
            GameAudio.Instance?.PlayCoinExplodeBlip();   // a bright metallic tick
            GameAudio.Instance?.PlayCrateBreak();
        }

        /// <summary>HIT band: holds + lifts (0.14s, scale 1 -> 1.12, flash), then falls DOWN with a small sideways kick and a slow
        /// tilt, staying readable ~1.3s until it leaves the screen. (Spencer: the hit "needs to show that it took damage".)</summary>
        private IEnumerator BandDrop(SpriteRenderer sr, float cell)
        {
            var tr = sr.transform; float t = 0f; Vector3 p0 = tr.position;
            sr.color = new Color(1.8f, 1.7f, 1.3f, 1f);
            while (tr != null && t < 0.14f)
            {
                t += Time.unscaledDeltaTime; float u = t / 0.14f;
                tr.position = p0 + new Vector3(0f, 0.12f * cell * Mathf.Sin(u * Mathf.PI * 0.5f), 0f);
                tr.localScale = Vector3.one * cell * 0.98f * (1f + 0.14f * u);
                sr.color = Color.Lerp(new Color(1.8f, 1.7f, 1.3f, 1f), Color.white, u);
                yield return null;
            }
            float side = Random.value < 0.5f ? -1f : 1f;
            Vector2 v = new Vector2(side * Random.Range(0.9f, 1.6f), Random.Range(1.2f, 2.0f)) * cell;
            float spin = side * Random.Range(-170f, -110f); t = 0f;
            var cam = Camera.main; float floor = cam != null ? cam.ViewportToWorldPoint(new Vector3(0.5f, 0f, 0f)).y - cell : -30f;
            while (tr != null && t < 2.5f && tr.position.y > floor)
            {
                float dt = Time.unscaledDeltaTime; t += dt;
                v.y -= BAND_GRAVITY * 0.55f * cell * dt;
                tr.position += new Vector3(v.x, v.y, 0f) * dt;
                tr.Rotate(0f, 0f, spin * dt);
                if (t > 1.1f) sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - (t - 1.1f) / 0.4f));
                yield return null;
            }
            if (tr != null) Destroy(tr.gameObject);
        }

        /// <summary>CHEST hit: wood chips (planks cut from the chest art) flick off the hit side and drop, plus one gold strap bit.</summary>
        private void Chips(Vector3 pos, float cell, int hitsBefore)
        {
            var ch = Chunks(); if (ch.Length == 0) return;
            int[] pick = hitsBefore >= 3 ? new[] { 6, 7, 0 } : new[] { 0, 1, 2, 3 };
            foreach (int k in pick)
            {
                var sr = Make("TreasureChip", ch[k % ch.Length], pos + new Vector3(Random.Range(-0.25f, 0.25f), Random.Range(-0.1f, 0.25f), -0.05f) * cell,
                              cell * Random.Range(0.55f, 0.75f), 49, Color.white);
                sr.transform.rotation = Quaternion.Euler(0, 0, Random.Range(-25f, 25f));
                StartCoroutine(BandDrop(sr, cell * 0.6f));
            }
        }

        private void Dust(Vector3 pos, float cell)
        {
            var sp = Spark(); if (sp == null) return;
            for (int i = 0; i < 6; i++)
            {
                float a = Random.Range(0f, Mathf.PI * 2f);
                var sr = Make("TreasureDust", sp, pos + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * cell * 0.25f, cell * 0.35f, 47, new Color(0.86f, 0.62f, 0.38f, 0.75f));
                sr.transform.DOMove(pos + new Vector3(Mathf.Cos(a), Mathf.Sin(a) * 0.6f + 0.25f, 0f) * cell * 0.9f, 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                sr.transform.DOScale(cell * 0.75f, 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                sr.DOFade(0f, 0.5f).SetEase(Ease.InQuad).SetUpdate(true).OnComplete(() => { if (sr != null) Destroy(sr.gameObject); });
            }
        }

        private IEnumerator BandOff(SpriteRenderer sr, float cell)
        {
            var tr = sr.transform;
            Vector2 v = new Vector2(Random.Range(-1f, 1f) < 0 ? Random.Range(-3.2f, -1.8f) : Random.Range(1.8f, 3.2f), Random.Range(5.5f, 7f)) * cell;
            float spin = Mathf.Sign(v.x) * Random.Range(-540f, -360f), t = 0f;
            // (the OPEN's box chunks) pop toward the camera first (scale up), then fall away
            while (tr != null && t < 1.4f)
            {
                float dt = Time.unscaledDeltaTime; t += dt;
                v.y -= BAND_GRAVITY * cell * dt;
                tr.position += new Vector3(v.x, v.y, 0f) * dt;
                tr.Rotate(0f, 0f, spin * dt);
                float k = t < 0.12f ? 1f + 0.25f * (t / 0.12f) : 1.25f - 0.35f * Mathf.Clamp01((t - 0.12f) / 1f);
                tr.localScale = Vector3.one * cell * 0.98f * k;
                if (t > 0.75f) sr.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1f - (t - 0.75f) / 0.5f));
                yield return null;
            }
            if (tr != null) Destroy(tr.gameObject);
        }

        private void Sparks(Vector3 pos, float cell, int n, float reach)
        {
            var sp = Spark(); if (sp == null) return;
            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f + Random.Range(-0.2f, 0.2f);
                var sr = Make("TreasureSpark", sp, pos, cell * Random.Range(0.12f, 0.2f), 49, new Color(1f, 0.92f, 0.45f, 1f));
                Vector3 to = pos + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * cell * reach * Random.Range(0.6f, 1f);
                sr.transform.DOMove(to, 0.32f).SetEase(Ease.OutCubic).SetUpdate(true);
                sr.transform.DOScale(0f, 0.32f).SetEase(Ease.InQuad).SetUpdate(true);
                sr.DOFade(0f, 0.32f).SetUpdate(true).OnComplete(() => { if (sr != null) Destroy(sr.gameObject); });
            }
        }

        // ── OPEN ──────────────────────────────────────────────────────────────────────────────────────────────────────────
        /// <summary>The treasure opens at <paramref name="pos"/> (its tile is hiding). <paramref name="coins"/> = coins paid.</summary>
        public static void Open(Vector3 pos, int coins)
        {
            pos.z = -0.8f;
            Inst.StartCoroutine(Inst.OpenRoutine(pos, coins));
        }

        private IEnumerator OpenRoutine(Vector3 pos, int coins)
        {
            float cell = Cell;
            // 1. glow swell (a gold disc grows under the bursting box)
            var sp = Spark();
            if (sp != null)
            {
                var glow = Make("TreasureGlow", sp, pos + new Vector3(0, 0, 0.05f), cell * 0.6f, 46, new Color(1f, 0.85f, 0.35f, 0.9f));
                glow.transform.DOScale(cell * 2.6f, 0.45f).SetEase(Ease.OutCubic).SetUpdate(true);
                glow.DOFade(0f, 0.45f).SetEase(Ease.InQuad).SetUpdate(true).OnComplete(() => { if (glow != null) Destroy(glow.gameObject); });
            }
            // 2. chunks of the box fly out
            var ch = Chunks();
            for (int k = 0; k < ch.Length; k++)
            {
                var sr = Make("TreasureChunk", ch[k], pos + new Vector3(Random.Range(-0.2f, 0.2f), Random.Range(-0.2f, 0.2f), 0) * cell, cell * Random.Range(0.85f, 1.05f), 47, Color.white);
                sr.transform.rotation = Quaternion.Euler(0, 0, Random.Range(-30f, 30f));
                StartCoroutine(BandOff(sr, cell * 0.85f));
            }
            Sparks(pos, cell, 14, 1.3f);
            GameAudio.Instance?.PlayCrateBreak();
            GameAudio.Instance?.PlayCoinDisperse();
            // 3. "+N" rises from the cell
            FloatLabel(pos, coins, cell);
            // 4. the BONUS coin flight (Spencer: copy it exactly) - spinning 3D coins pop out and stream into the HUD
            HUDManager.Instance?.SpawnTreasureCoins(pos, coins);
            yield break;
        }

        private IEnumerator CoinFlight(Vector3 pos, float cell, Sprite coin, int i, int n)
        {
            var sr = Make("TreasureCoin", coin, pos, cell * 0.42f, 49, Color.white);
            var tr = sr.transform;
            // fountain: a fan of arcs up and out
            float a = Mathf.Lerp(-55f, 55f, n > 1 ? i / (float)(n - 1) : 0.5f) * Mathf.Deg2Rad;
            Vector2 v = new Vector2(Mathf.Sin(a) * Random.Range(2.2f, 3.4f), Mathf.Cos(a) * Random.Range(6.5f, 8.2f)) * cell;
            float t = 0f, hang = Random.Range(0.42f, 0.55f), spin = Random.Range(-360f, 360f);
            while (tr != null && t < hang)
            {
                float dt = Time.unscaledDeltaTime; t += dt;
                v.y -= 22f * cell * dt; v *= 1f - 1.2f * dt;
                tr.position += new Vector3(v.x, v.y, 0f) * dt; tr.Rotate(0, 0, spin * dt);
                yield return null;
            }
            if (tr == null) yield break;
            // stream to the HUD on a curve
            var cam = Camera.main; var hud = HUDManager.Instance;
            if (cam == null || hud == null) { Destroy(tr.gameObject); yield break; }
            Vector2 scr = hud.TreasureCoinTargetScreen();
            Vector3 to = cam.ScreenToWorldPoint(new Vector3(scr.x, scr.y, Mathf.Abs(cam.transform.position.z - tr.position.z)));
            to.z = tr.position.z;
            Vector3 from = tr.position, ctrl = Vector3.Lerp(from, to, 0.35f) + new Vector3(Random.Range(-1.2f, 1.2f) * cell, 1.6f * cell, 0f);
            float dur = Random.Range(0.42f, 0.55f); t = 0f;
            while (tr != null && t < dur)
            {
                t += Time.unscaledDeltaTime; float u = Mathf.Clamp01(t / dur), e = u * u * (3f - 2f * u) * 0.35f + u * u * 0.65f;
                tr.position = (1 - e) * (1 - e) * from + 2 * (1 - e) * e * ctrl + e * e * to;
                tr.localScale = Vector3.one * cell * Mathf.Lerp(0.42f, 0.3f, e);
                tr.Rotate(0, 0, spin * Time.unscaledDeltaTime);
                yield return null;
            }
            if (tr != null) Destroy(tr.gameObject);
            hud.TreasureCoinLanded();
        }

        private void FloatLabel(Vector3 pos, int coins, float cell)
        {
            var go = new GameObject("TreasurePlus"); go.transform.position = pos + new Vector3(0, 0.2f * cell, -0.1f);
            var t = go.AddComponent<TMPro.TextMeshPro>();
            var f = Resources.Load<TMPro.TMP_FontAsset>("WordSlop Button SDF") ?? GameFont.GetUITMP(); if (f != null) t.font = f;
            t.text = $"+{coins}"; t.fontSize = 5f * cell; t.alignment = TMPro.TextAlignmentOptions.Center; t.color = new Color(1f, 0.93f, 0.55f, 1f);
            t.outlineWidth = 0.25f; t.outlineColor = new Color32(0x6A, 0x30, 0x00, 0xFF);
            t.sortingOrder = 50;
            go.transform.localScale = Vector3.one * 0.4f;
            DOTween.Sequence().SetUpdate(true)
                .Append(go.transform.DOScale(1f, 0.18f).SetEase(Ease.OutBack, 2.2f))
                .Join(go.transform.DOMoveY(pos.y + 1.1f * cell, 0.9f).SetEase(Ease.OutCubic))
                .Insert(0.55f, t.DOFade(0f, 0.35f))
                .OnComplete(() => { if (go != null) Destroy(go); });
        }
    }
}
