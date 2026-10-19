using System.Collections.Generic;
using TMPro;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Scrabble-GO-style cluster display: an outline traced around each connected group of
    /// primed words, with a badge showing what tapping it would pay RIGHT NOW.
    ///
    /// WHY (2026-09-09 Spencer). Connecting words has always been worth roughly ten times as
    /// much as detonating them separately — the triangular multiplier has been in the code the
    /// whole time. But nothing on screen showed which words were linked, and nothing showed
    /// what a tap was worth, so there was no way to learn that connecting mattered. We tried
    /// forcing the behaviour with a priming rule; showing the reward is the better answer,
    /// because a player who can watch 30 become 400 needs no rule explained to them.
    ///
    /// Deliberately an OUTLINE, not a tint: it leaves the tile art completely alone, so it
    /// cannot fight the armed rose, the gold face or the cyan selection rim — the mistake that
    /// made every previous "show some state" attempt wash the tiles out.
    ///
    /// Single words get a THIN, quiet outline; two or more get a bold one with a bigger badge.
    /// The baseline matters: you have to see "30" on one word for "400" on five to land.
    /// </summary>
    public class ClusterOutline : MonoBehaviour
    {
        public static ClusterOutline Instance { get; private set; }
        // Set false to remove the whole feature instantly — outlines and badges both. The
        // game reverts to exactly how it played before this was added; nothing else depends
        // on it. Kept as a one-line switch because this display touches every board event and
        // is the most likely thing to be misbehaving. 2026-09-09 Spencer.
        public static bool Enabled = true;

        private const int   HALO_ORDER   = 128;    // IN FRONT of every tile, per the reference
        private const int   BADGE_ORDER  = 130;    // above tiles
        private const float THIN         = 0.035f; // single-word outline thickness (world units)
        private const float BOLD         = 0.075f; // 2+ words
        // CellToWorld returns where the tile SPRITE is centred, and the sprite is ~20% taller
        // than the cell (the extra height is the top face). So a square outline centred there
        // rides high by half the overhang. Drop it by that much to sit on the front face.
        // 2026-09-09 Spencer.
        private const float Y_NUDGE = -0.10f;   // x CellSize

        private static readonly Color COL_SINGLE = new Color(1f, 1f, 1f, 0.34f);
        private static readonly Color COL_GROUP  = new Color(1f, 0.93f, 0.42f, 0.95f);

        private readonly List<GameObject> _halo = new List<GameObject>();
        private readonly List<TextMeshPro>    _badges = new List<TextMeshPro>();
        private readonly List<SpriteRenderer> _badgeBg = new List<SpriteRenderer>();
        private static Sprite s_haloSprite, s_pillSprite;
        private int _haloUsed;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Instance = null; }

        /// <summary>Rebuild every cluster outline. Called from the board sync, so it can never
        /// show a cluster that no longer exists.</summary>
        public void Refresh()
        {
            _anchors.Clear(); _anchorInk.Clear();
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            _haloUsed = 0;
            if (!Enabled || rules == null || grid == null) { HideAll(); return; }

            var clusters = rules.GetPrimedClusters();
            for (int i = 0; i < clusters.Count; i++)
                Draw(i, clusters[i].cells, clusters[i].payout, clusters[i].wordCount, grid);

            for (int i = _haloUsed; i < _halo.Count; i++)
            {
                var mr = _halo[i] != null ? _halo[i].GetComponent<MeshRenderer>() : null;
                if (mr != null && mr.enabled) mr.enabled = false;
            }
            for (int i = clusters.Count; i < _badges.Count; i++) HideBadge(i);
        }

        /// <summary>Clear every outline immediately. Called the instant a detonation starts —
        /// the words are about to stop existing, and the outline is rebuilt from the board
        /// sync which does not run until the FX have finished, so without this the line hangs
        /// over the explosion. 2026-09-09 Spencer.</summary>
        public void Clear() { HideAll(); _haloUsed = 0; }

        /// <summary>Redraw everything EXCEPT the cluster containing this cell. Used when a
        /// detonation starts: the tapped web is about to stop existing, but every OTHER
        /// cluster on the board is untouched and should not blink. Clear() wiped them all,
        /// which is why unrelated outlines vanished and came back. 2026-09-09 Spencer.</summary>
        public void RefreshExcluding(Vector2Int cell)
        {
            _anchors.Clear(); _anchorInk.Clear();
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            _haloUsed = 0;
            if (!Enabled || rules == null || grid == null) { HideAll(); return; }

            var clusters = rules.GetPrimedClusters();
            int drawn = 0;
            for (int i = 0; i < clusters.Count; i++)
            {
                if (clusters[i].cells != null && clusters[i].cells.Contains(cell)) continue;
                Draw(drawn, clusters[i].cells, clusters[i].payout, clusters[i].wordCount, grid);
                drawn++;
            }
            for (int i = _haloUsed; i < _halo.Count; i++)
            {
                var mr = _halo[i] != null ? _halo[i].GetComponent<MeshRenderer>() : null;
                if (mr != null && mr.enabled) mr.enabled = false;
            }
            for (int i = drawn; i < _badges.Count; i++) HideBadge(i);
        }

        /// <summary>Redraw, dropping every cluster that contains ANY of these cells.
        ///
        /// RefreshExcluding() drops only the cluster the player TAPPED. When a resolution takes
        /// out more than one cluster, the others kept their outline and payout badge hanging
        /// over cells whose tiles no longer existed. Driving this from the cells that ACTUALLY
        /// exploded means the display cannot disagree with what happened, whatever the rules
        /// decide to include. 2026-09-10 Spencer.</summary>
        public void RefreshExcludingCells(System.Collections.Generic.ICollection<Vector2Int> gone)
        {
            _anchors.Clear(); _anchorInk.Clear();
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            _haloUsed = 0;
            if (!Enabled || rules == null || grid == null) { HideAll(); return; }

            var clusters = rules.GetPrimedClusters();
            int drawn = 0;
            for (int i = 0; i < clusters.Count; i++)
            {
                var cells = clusters[i].cells;
                bool hit = false;
                if (cells != null && gone != null)
                    for (int c = 0; c < cells.Count && !hit; c++) if (gone.Contains(cells[c])) hit = true;
                // NOTE: the pop is NOT fired from here. By the time a resolution reaches the
                // Exploding phase the rules have already removed those words from the registry,
                // so this list no longer contains the cluster that is going up and nothing would
                // ever match. It is fired at the TAP instead, while the cluster still exists.
                // 2026-09-10 Spencer.
                if (hit) continue;
                Draw(drawn, cells, clusters[i].payout, clusters[i].wordCount, grid);
                drawn++;
            }
            for (int i = _haloUsed; i < _halo.Count; i++)
            {
                var mr = _halo[i] != null ? _halo[i].GetComponent<MeshRenderer>() : null;
                if (mr != null && mr.enabled) mr.enabled = false;
            }
            for (int i = drawn; i < _badges.Count; i++) HideBadge(i);
        }

        /// <summary>Ride a rising row. Every outline is a child of this one transform and the
        /// whole board moves up by exactly one cell, so translating the root tracks the tiles
        /// perfectly — no per-frame rebuild, and nothing can drift out of step with them.
        /// Rebuilding each frame would NOT work: the outline reads cell coordinates, and those
        /// do not change until the rise completes, so it would sit still and snap regardless.
        /// 2026-09-09 Spencer.</summary>
        public void RiseBy(float distance, float duration)
        {
            // MUST use the tiles' own easing. They rise with Ease.OutBack (overshoot 0.15),
            // which accelerates hard and travels PAST the target before settling. A linear
            // move falls behind for most of the slide and catches up at the end — which reads
            // exactly as "lagging, then snapping". Same curve, same overshoot, same duration.
            transform.DOKill();
            transform.position = Vector3.zero;
            transform.DOMoveY(distance, duration)
                     .SetEase(DG.Tweening.Ease.OutBack, 0.15f)
                     .OnComplete(() =>
                     {
                         // Land at rest and rebuild from the NEW cell coordinates, or the
                         // offset gets baked in twice.
                         transform.position = Vector3.zero;
                         Refresh();
                     });
        }



        private void HideAll()
        {
            for (int i = 0; i < _halo.Count; i++)
            {
                var mr = _halo[i] != null ? _halo[i].GetComponent<MeshRenderer>() : null;
                if (mr != null) mr.enabled = false;
            }
            for (int i = 0; i < _badges.Count; i++) HideBadge(i);
        }
        private void HideBadge(int i)
        {
            if (i < _badges.Count && _badges[i] != null && _badges[i].gameObject.activeSelf)
                _badges[i].gameObject.SetActive(false);
            if (i < _badgeBg.Count && _badgeBg[i] != null && _badgeBg[i].enabled)
                _badgeBg[i].enabled = false;
        }

        private void EnsureSprites(GridManager grid)
        {
            if (s_haloSprite == null)
            {
                // FILLED rounded shape at the tile's aspect. Filled, not an outline: adjacent
                // cells then MERGE into one continuous shape instead of drawing internal
                // borders, and the tiles on top hide everything except the rim. That is what
                // makes it hug the tiles' rounded corners — the previous mesh version traced
                // hard rectilinear edges and looked crude at every bend.
                int w = Mathf.Clamp(Mathf.RoundToInt(grid.CellSize * 200f), 64, 512);
                int h = Mathf.RoundToInt(w / Tile.ArtAspect);
                s_haloSprite = TileRenderer.CreateRoundedRect(w, h, w / 5, Color.white, Color.white, 0);
            }
            if (s_pillSprite == null)
            {
                int p = 128;
                s_pillSprite = TileRenderer.CreateRoundedRect(p, p, p / 2, Color.white, Color.white, 0);
            }
        }

        private GameObject NextHalo()
        {
            while (_halo.Count <= _haloUsed)
            {
                var go = new GameObject($"Outline{_halo.Count}");
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>();
                var mr = go.AddComponent<MeshRenderer>();
                if (_mat == null) _mat = new Material(Shader.Find("Sprites/Default")) { name = "ClusterLineMat" };
                mr.sharedMaterial = _mat;
                mr.sortingOrder = HALO_ORDER;
                // NOTE: no SpriteRenderer here. A GameObject can only carry ONE renderer, so
                // AddComponent<SpriteRenderer> on an object that already has a MeshRenderer
                // returns NULL — and the null deref took the whole Draw down, which is why the
                // outline vanished and a half-built badge was left on screen as a white blob.
                _halo.Add(go);
            }
            var g = _halo[_haloUsed++];
            var mr2 = g.GetComponent<MeshRenderer>(); if (mr2 != null) mr2.enabled = true;
            return g;
        }
        private Material _mat;

        private static void BuildMesh(GameObject go, List<Vector3> v, List<int> t, Color c)
        {
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) return;
            var mesh = mf.sharedMesh != null ? mf.sharedMesh : new Mesh { name = "ClusterLine" };
            mesh.Clear();
            mesh.SetVertices(v); mesh.SetTriangles(t, 0);
            var cols = new Color[v.Count];
            for (int i = 0; i < cols.Length; i++) cols[i] = c;
            mesh.colors = cols; mesh.RecalculateBounds();
            mf.sharedMesh = mesh;
        }

        private static void Seg(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, float w)
        {
            Vector3 d = (b - a); if (d.sqrMagnitude < 1e-8f) return;
            Vector3 n = new Vector3(-d.y, d.x, 0f).normalized * (w * 0.5f);
            int i0 = v.Count;
            v.Add(a - n); v.Add(a + n); v.Add(b + n); v.Add(b - n);
            t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
            t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
        }

        private static void Arc(List<Vector3> v, List<int> t, Vector3 centre, float r, float a0, float a1, float w)
        {
            const int STEPS = 6;
            float inner = r - w * 0.5f, outer = r + w * 0.5f;
            for (int i = 0; i < STEPS; i++)
            {
                float t0 = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, i / (float)STEPS);
                float t1 = Mathf.Deg2Rad * Mathf.Lerp(a0, a1, (i + 1) / (float)STEPS);
                Vector3 d0 = new Vector3(Mathf.Cos(t0), Mathf.Sin(t0), 0f);
                Vector3 d1 = new Vector3(Mathf.Cos(t1), Mathf.Sin(t1), 0f);
                int i0 = v.Count;
                v.Add(centre + d0 * inner); v.Add(centre + d0 * outer);
                v.Add(centre + d1 * outer); v.Add(centre + d1 * inner);
                t.Add(i0); t.Add(i0 + 1); t.Add(i0 + 2);
                t.Add(i0); t.Add(i0 + 2); t.Add(i0 + 3);
            }
        }

        /// <summary>Royal-Match style payout pop: the cluster's number leaps off the board as it
        /// detonates instead of simply vanishing with the outline. Throwaway object — the pooled
        /// badges get recycled the moment the outline redraws, so animating one of those would
        /// be fighting the redraw. 2026-09-10 Spencer.</summary>

        // ── SPRITE DIGITS (2026-10-09 Spencer) ───────────────────────────────────
        // The payout pop is drawn from Spencer's authored digit art instead of TMP text.
        //
        // WHY NOT TMP: his Photoshop treatment is SIX effects across two text layers — stroke,
        // inner glow, gradient overlay and drop shadow on one, plus a second layer carrying its
        // own drop shadow. TMP's SDF shader gives one outline, one underlay and a face gradient.
        // Reproducing that in-shader would be an approximation; the sprites are the artwork.
        //
        // Numerals are a 12-glyph set, and Nunito's figures are TABULAR (measured: 0% width
        // variation), so every digit shares one advance and the layout needs no kerning table.
        //
        // Sprites live in Resources/Digits/ as digit_0 … digit_9, digit_comma, digit_plus.
        // Cells are a uniform 215x308 cut from one text layer, so baselines align by construction.
        public static bool  PAYOUT_SPRITE_DIGITS = true;
        /// <summary>Glyph advance as a fraction of the sprite cell's HEIGHT.
        ///
        /// MEASURED off the strip: ink centres sit 238 px apart, of which ~78 is the space he
        /// typed between them, leaving a ~160 px advance. Cells are 215 x 308.
        ///
        /// ⚠️ IT IS OVER THE HEIGHT, NOT THE WIDTH, because BuildSpriteNumber scales each sprite
        /// by its height (cell / nativeH). Expressing the advance as 160/215 while scaling by
        /// height made every gap 43% too wide — the cell is taller than it is broad, so the two
        /// denominators are not interchangeable. Scale by one dimension, measure by the same one.</summary>
        public static float DIGIT_ADVANCE_FRAC = 172f / 308f;   // 160 -> 172: Spencer wanted a hair more air
        /// <summary>World height of one digit cell at the pop's base size. The pop then scales
        /// up, so this is the resting size, not the peak.</summary>
        public static float DIGIT_CELL_WORLD   = 0.40f;   // 1.30 -> 0.65 -> 0.325 -> 0.36 -> 0.40
        // 2026-10-19 Spencer: "just a touch bigger". +11%, matching the size of his last
        // step (0.325 -> 0.36) so the nudges stay on the same scale. The advance is a
        // fraction of this, so the letter-spacing he tuned stays proportional and does not
        // need re-tuning. In-game pops only — the level map's coin popups size themselves
        // in canvas px (COIN_DIGIT_PX) and are unaffected.

        private static Dictionary<char, Sprite> s_digitSprites;

        private static Sprite DigitSprite(char c)
        {
            if (s_digitSprites == null) s_digitSprites = new Dictionary<char, Sprite>();
            Sprite sp;
            if (s_digitSprites.TryGetValue(c, out sp)) return sp;
            string nm = c == ',' ? "comma" : c == '+' ? "plus" : c.ToString();
            sp = Resources.Load<Sprite>("Digits/digit_" + nm);
            s_digitSprites[c] = sp;
            if (sp == null) WDLog.Log($"[Payout] missing digit sprite 'Digits/digit_{nm}'");
            return sp;
        }

        /// <summary>Lay the number out as sprite children under `parent`, centred on its origin.
        /// Returns false if the art is missing, so the caller can fall back to TMP.</summary>
        /// <summary>uGUI twin of BuildSpriteNumber — lays the SAME payout digit sprites into a
        /// Canvas instead of the world. Lives here so DIGIT_ADVANCE_FRAC and DigitSprite stay
        /// the single source of truth; the level map's coin popups were plain TMP text and
        /// looked nothing like the in-game score pops. 2026-10-19 Spencer.
        ///
        /// `cellPx` is the digit HEIGHT in canvas units (the world version calls this
        /// DIGIT_CELL_WORLD). Advance is a fraction of the HEIGHT, not the width — the cells
        /// are 215x308, and measuring the advance against the width once made the gaps 43%
        /// too wide. Returns false if the art is missing so the caller can fall back to text.</summary>
        public static bool BuildSpriteNumberUI(RectTransform parent, string text, float cellPx, Color tint)
        {
            if (!PAYOUT_SPRITE_DIGITS || parent == null || string.IsNullOrEmpty(text)) return false;
            if (DigitSprite('0') == null) return false;           // art not imported yet

            float adv   = cellPx * DIGIT_ADVANCE_FRAC;
            float total = adv * text.Length;
            float x     = -total * 0.5f + adv * 0.5f;             // centre the run on the parent

            for (int i = 0; i < text.Length; i++)
            {
                var sp = DigitSprite(text[i]);
                if (sp == null) { x += adv; continue; }

                var g = new GameObject("d" + text[i], typeof(RectTransform), typeof(Image));
                var rt = (RectTransform)g.transform;
                rt.SetParent(parent, false);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(x, 0f);

                // Keep the sprite's own aspect; only the HEIGHT is pinned to cellPx.
                float aspect = sp.rect.height > 0.01f ? sp.rect.width / sp.rect.height : 1f;
                rt.sizeDelta = new Vector2(cellPx * aspect, cellPx);

                var img = g.GetComponent<Image>();
                img.sprite = sp;
                img.color = tint;
                img.raycastTarget = false;
                x += adv;
            }
            return true;
        }

        private static bool BuildSpriteNumber(Transform parent, string text, int sortingOrder)
        {
            if (!PAYOUT_SPRITE_DIGITS) return false;
            if (DigitSprite('0') == null) return false;           // art not imported yet

            float cell = DIGIT_CELL_WORLD;
            float adv  = cell * DIGIT_ADVANCE_FRAC;
            float total = adv * text.Length;
            float x = -total * 0.5f + adv * 0.5f;                 // centre the run on the origin

            for (int i = 0; i < text.Length; i++)
            {
                var sp = DigitSprite(text[i]);
                if (sp == null) { x += adv; continue; }
                var g = new GameObject("d" + text[i]);
                g.transform.SetParent(parent, false);
                g.transform.localPosition = new Vector3(x, 0f, 0f);
                var sr = g.AddComponent<SpriteRenderer>();
                sr.sprite = sp;
                sr.sortingOrder = sortingOrder;
                // Scale the cell to DIGIT_CELL_WORLD tall, preserving the sprite's aspect.
                float nativeH = sp.bounds.size.y;
                float k = nativeH > 0.0001f ? cell / nativeH : 1f;
                g.transform.localScale = new Vector3(k, k, 1f);
                x += adv;
            }
            return true;
        }


        /// <summary>The pop's flight — swell, rise, fade — shared by the sprite-digit and TMP
        /// paths. `halfW`/`halfH` are the number's half-extents at scale 1, used to keep the
        /// whole flight inside the camera; the TMP path measures them off textBounds, the
        /// sprite path off the laid-out children.</summary>
        private void AnimatePopTransform(GameObject go, Vector3 at, float halfW, float halfH,
                                         System.Action<float> setAlpha)
        {
            const float GROW = 1.8f;
            float rise = _lastCellSize * 0.95f;
            halfW *= GROW; halfH *= GROW;

            var cam = Camera.main;
            if (cam != null && cam.orthographic)
            {
                float camH = cam.orthographicSize, camW = camH * cam.aspect;
                Vector3 cc = cam.transform.position;
                float pad = _lastCellSize * 0.06f;
                float minX = cc.x - camW + halfW + pad, maxX = cc.x + camW - halfW - pad;
                float minY = cc.y - camH + halfH + pad, maxY = cc.y + camH - halfH - pad;
                if (minX <= maxX) at.x = Mathf.Clamp(at.x, minX, maxX);
                if (minY <= maxY)
                {
                    at.y = Mathf.Clamp(at.y, minY, maxY);
                    rise = Mathf.Max(0f, Mathf.Min(rise, maxY - at.y));
                }
                go.transform.position = at;
            }

            go.transform.localScale = Vector3.one;
            go.transform.DOScale(GROW, 0.30f).SetEase(DG.Tweening.Ease.OutBack, 1.6f);
            if (rise > 0.001f)
                go.transform.DOMoveY(at.y + rise, 0.85f).SetEase(DG.Tweening.Ease.OutCubic);
            // DOKill BEFORE Destroy — see the note on the TMP path; the rise outlives the fade.
            float alpha = 1f;
            DOTween.To(() => alpha, v => { alpha = v; setAlpha(v); }, 0f, 0.34f)
                   .SetDelay(0.50f)
                   .OnComplete(() => { if (go != null) { go.transform.DOKill(); Destroy(go); } });
        }

        public void PopPayout(Vector3 at, int payout, bool group, float fromFontSize = 0f, int badgeIdx = -1, Color? textCol = null)
        {
            if (payout <= 0 || UIAnimations.ReducedMotion) return;
            // HIDE THE REAL BADGE THIS INSTANT. It otherwise stays up until the next outline
            // rebuild, so the player saw the static badge AND a growing copy at the same time —
            // two numbers, no continuity, which is why the swell did not read as coming out of
            // the corner number. The pop now takes its place on the same frame.
            if (badgeIdx >= 0) HideBadge(badgeIdx);

            // Inherit the badge's ink so the plate, the stroke and the number that flies out of
            // them are one object — resolved BEFORE HideBadge's slot could be reused.
            Color popInk = (badgeIdx >= 0 && badgeIdx < _anchorInk.Count)
                         ? _anchorInk[badgeIdx] : (Color)PAYOUT_STROKE_COLOR;

            var go = new GameObject("PayoutPop");
            go.transform.position = at;

            // Carry the PILL as well. The badge is a number on a dark plate; a bare number is a
            // visually different object, so it read as something new appearing rather than that
            // number growing. Same plate, scaled by the same parent. 2026-09-10 Spencer.
            // The plate STAYS PUT and fades where the badge was — only the number travels.
            // Parenting it to the moving object dragged the pill up the screen with the digits,
            // which read as the whole badge flying off rather than the score lifting out of it.
            // 2026-09-10 Spencer.
            if (BADGE_PLATE && s_pillSprite != null)
            {
                var pill = new GameObject("PayoutPopPill");
                pill.transform.position = at + new Vector3(0f, 0f, 0.02f);
                var psr = pill.AddComponent<SpriteRenderer>();
                psr.sprite = s_pillSprite;
                psr.color = PlateColour(popInk, group);
                psr.sortingOrder = BADGE_ORDER + 39;
                pill.transform.localScale = PlateScale(_lastCellSize, group,
                                                       payout.ToString("N0").Length,
                                                       psr.sprite.bounds.size.x);
                // Fades quickly and in place — it is the launch pad, not the payload.
                var pillGO = pill;
                DOTween.To(() => psr.color.a, v => { var cc2 = psr.color; cc2.a = v; psr.color = cc2; }, 0f, 0.26f)
                       .SetDelay(0.10f)
                       .OnComplete(() => { if (pillGO != null) Destroy(pillGO); });
            }

            var textGO = new GameObject("PayoutPopText");
            textGO.transform.SetParent(go.transform, false);
            textGO.transform.localPosition = Vector3.zero;

            // Authored sprite digits if the art is present; TMP otherwise. The pop's scale /
            // drift / fade tweens all act on `go`, so they drive either version unchanged.
            if (BuildSpriteNumber(textGO.transform, "+" + payout.ToString("N0"), BADGE_ORDER + 40))
            {
                var srs = textGO.GetComponentsInChildren<SpriteRenderer>();
                var b = new Bounds(textGO.transform.position, Vector3.zero);
                for (int i = 0; i < srs.Length; i++) b.Encapsulate(srs[i].bounds);
                AnimatePopTransform(go, at, b.extents.x, b.extents.y, v =>
                {
                    for (int i = 0; i < srs.Length; i++)
                    {
                        var c = srs[i].color; c.a = v; srs[i].color = c;
                    }
                });
                return;
            }

            var tmp = textGO.AddComponent<TextMeshPro>();
            var f = GameFont.GetTMP(); if (f != null) tmp.font = f;
            tmp.text = "+" + payout.ToString("N0");   // "+148" — this is score being GAINED
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;
            // Starts in the badge's green — so frame one still matches the number it replaces —
            // and turns WHITE as it swells, which is what it should be once it is airborne.
            // Snapping straight to white would break the "that number is growing" read we just
            // spent several passes getting right. 2026-09-10 Spencer.
            var startCol = textCol ?? Color.white;
            tmp.color = startCol;
            DOTween.To(() => tmp.color, v => tmp.color = v, Color.white, 0.22f)
                   .SetEase(DG.Tweening.Ease.OutQuad);
            // Heavy dark outline is what makes the number readable over ANY tile colour — the
            // legibility problem Spencer raised about the badge itself. Same trick RM uses.
            // Match the badge's own text size, then SCALE the object up — so it reads as that
            // very number swelling, not a second number fading in on top of it.
            float startFont = fromFontSize > 0.01f ? fromFontSize : (group ? PAYOUT_FONT_GROUP : PAYOUT_FONT_SINGLE);
            tmp.fontSize = startFont;
            tmp.fontStyle = FontStyles.Bold;
            // Tighten the tracking so the digits read as one chunky unit rather than loose
            // characters — the packed look on Royal Match's "+10". Negative values pull the
            // glyphs together; roughly -12 is snug, -20 starts to collide on wide digits.
            // 2026-09-10 Spencer.
            tmp.characterSpacing = PAYOUT_POP_TRACKING;
            // The pop grows OUT OF the badge, so it must wear the badge's ink or the number
            // changes colour mid-flight.
            StylePayout(tmp, popInk);
            var mr = tmp.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = BADGE_ORDER + 40;   // above the FX tiles (60)

            // Punch out, drift up, fade — shared with the sprite path via AnimatePopTransform.
            tmp.ForceMeshUpdate();
            Vector3 ext = tmp.textBounds.extents;
            AnimatePopTransform(go, at, Mathf.Max(ext.x, 0.05f), Mathf.Max(ext.y, 0.05f),
                                v => { if (tmp != null) tmp.alpha = v; });
        }

        /// <summary>Letter-spacing on the payout number — corner badge AND pop, in TMP units.
        /// Negative = tighter.
        /// 0 is the font's default, -12 is snug, past about -20 wide digits start to touch.</summary>
        // ── Cluster outline colour (2026-09-10 Spencer) ──────────────────────────────
        // Sampled off the Royal Match green "+" button: a saturated mid grass-green, lighter
        // and slightly yellow at the top of the ramp, deeper and cooler at the bottom.

        // Plate geometry, shared by the corner badge and the pop that grows out of it — these
        // were duplicated literals and drifted apart once already. Scaled up 2026-09-22 with the
        // font; the plate does NOT track fontSize on its own, so the two move together or the
        // digits overflow their own background.
        public static float PLATE_W_GROUP = 0.37f, PLATE_H_GROUP = 0.42f;
        public static float PLATE_W_SINGLE = 0.29f, PLATE_H_SINGLE = 0.33f;
        /// <summary>Opaque enough that white tiles cannot wash the plate out — the old 0.80 on a
        /// lone word let the tile through and put the number back on near-white.</summary>
        public static float PLATE_ALPHA_GROUP = 0.97f, PLATE_ALPHA_SINGLE = 0.93f;

        private static Vector3 PlateScale(float cellSize, bool group, int digits, float spriteW)
        {
            float pn = spriteW > 0.0001f ? spriteW : 1f;
            float d  = Mathf.Max(2, digits);
            float bw = cellSize * (group ? PLATE_W_GROUP : PLATE_W_SINGLE) * (0.62f + 0.24f * d);
            float bh = cellSize * (group ? PLATE_H_GROUP : PLATE_H_SINGLE);
            return new Vector3(bw / pn, bh / pn, 1f);
        }

        private static Color PlateColour(Color ink, bool group)
        {
            ink.a = group ? PLATE_ALPHA_GROUP : PLATE_ALPHA_SINGLE;
            return ink;
        }

        /// <summary>Plate behind the payout number, in the CLUSTER'S OWN INK.
        ///
        /// BACK ON 2026-09-22. It was switched off on 2026-09-10 when the number wore the
        /// outline's green; once the number went WHITE, nothing was behind it. MEASURED on
        /// Spencer's screenshot: digit fill #FEFEFE on tiles at #F9FAFD — a contrast ratio of
        /// 1.03:1. The whole readout was being carried by a ~2px stroke.
        ///
        /// 🔑 THE BADGE DOES NOT SIT ON ITS OWN CLUSTER. It hangs off the cluster's bottom-right
        /// CORNER, so what is behind it is almost always ordinary WHITE tiles and the board — not
        /// the coloured cluster. That is why tuning the stroke against the cluster colour could
        /// never fix this: a stroke gives the glyph's EDGE contrast, and the problem was that its
        /// BODY had none. A plate is the only thing that guarantees the background.
        ///
        /// Old note (kept — this is what the 09-10 removal reasoned from, and it was true then):
        /// with the number wearing the outline's green and carrying its own dark stroke, the plate
        /// was just a blob sitting on the tiles. The stroke is what keeps it legible.</summary>
        /// <summary>Draw the number in the cluster's corner at all. 2026-09-22 Spencer: trying
        /// the board WITHOUT it, keeping only the score that flies up when the word pops.
        ///
        /// It is hidden, NOT skipped. The badge's anchor — position, font size and ink — is what
        /// the detonation pop grows OUT of (PopPayout takes fromFontSize/badgeIdx, and the ink
        /// comes from _anchorInk). Short-circuiting before the bookkeeping would leave the pop
        /// with no launch point and no colour, so everything is still computed and registered;
        /// only the renderers are switched off.</summary>
        public static bool  SHOW_CORNER_BADGE    = false;

        public static bool  BADGE_PLATE          = true;

        /// <summary>The dark stroke on the payout number — corner badge AND detonation pop use
        /// exactly this, so the number that flies off looks like the one that was sitting there.
        /// With no plate behind it, this stroke is the only thing keeping it legible.</summary>
        /// <summary>Payout number size. The pop inherits whichever applies, so these move both.</summary>
        public static float  PAYOUT_FONT_SINGLE  = 4.6f;   // lone word  (2.6 -> 3.8 -> 4.6)
        public static float  PAYOUT_FONT_GROUP   = 6.4f;   // multi-word (3.6 -> 5.2 -> 6.4)


        /// <summary>Make the payout number legible on ANY tile colour — stroke + drop shadow.
        /// 2026-09-21, Spencer: "the lack of a drop shadow and any stroke on it make the letters
        /// look like they are a part of the E in SIRE".
        ///
        /// 🔑 WHY THE EXISTING STROKE DID NOTHING: `tmp.outlineWidth` / `tmp.outlineColor` write
        /// to the SHARED font material and are ignored unless the shader's OUTLINE keyword is on.
        /// Going through `fontMaterial` instances the material and setting the keyword explicitly
        /// is what actually renders it — the same route the letter emboss in Tile.cs uses.
        /// The UNDERLAY is the drop shadow; `UpdateMeshPadding()` MUST follow it or the glyph
        /// bounds clip the shadow off.</summary>
        private static void StylePayout(TMP_Text t) { StylePayout(t, PAYOUT_STROKE_COLOR); }

        private static void StylePayout(TMP_Text t, Color ink)
        {
            if (t == null) return;
            t.outlineWidth = PAYOUT_STROKE_WIDTH;
            t.outlineColor = ink;
            var m = t.fontMaterial;                       // instances it — shared would be ignored
            if (m == null) return;
            m.EnableKeyword("OUTLINE_ON");
            // 🔑 DILATE THE FACE FIRST. TMP's outline eats INWARD — it consumes the SDF band
            // between the glyph edge and the face, so a wide outline THINS the fill instead of
            // growing outside it. The flying number survives that because its transform is scaled
            // up ~3x, leaving plenty of stem; the corner badge renders the same proportional bite
            // at a third the pixels, so its digits went thin and hollow — Spencer 2026-09-21,
            // "an odd inside stroke". Dilating gives the stem back and pushes the stroke outward.
            m.SetFloat("_FaceDilate", PAYOUT_FACE_DILATE);
            m.SetFloat("_OutlineWidth", PAYOUT_STROKE_WIDTH);
            m.SetColor("_OutlineColor", ink);
            m.EnableKeyword("UNDERLAY_ON");
            // The underlay takes the SAME ink. They were both #022658; shifting only the stroke
            // leaves the badge wearing two unrelated darks.
            Color sh = ink; sh.a = PAYOUT_SHADOW_ALPHA;
            m.SetColor("_UnderlayColor",    sh);
            m.SetFloat("_UnderlayOffsetX",  PAYOUT_SHADOW_OFFSET_X);
            m.SetFloat("_UnderlayOffsetY",  PAYOUT_SHADOW_OFFSET_Y);
            m.SetFloat("_UnderlayDilate",   PAYOUT_SHADOW_DILATE);
            m.SetFloat("_UnderlaySoftness", PAYOUT_SHADOW_SOFTNESS);
            // FILL GRADIENT — flat white reads as UI text; a shaded fill reads as an object.
            // Cooler/duller at the top, bright at the bottom, mirroring the reference.
            if (PAYOUT_GRADIENT)
            {
                t.enableVertexGradient = true;
                t.colorGradient = new VertexGradient(
                    PAYOUT_FILL_TOP, PAYOUT_FILL_TOP, PAYOUT_FILL_BOTTOM, PAYOUT_FILL_BOTTOM);
            }
            t.UpdateMeshPadding();                        // or the shadow is clipped away
        }

        /// <summary>Stroke. 2026-09-21: was near-black (30,18,46) at 0.42 — a hard uniform black
        /// keyline, which is the clip-art tell. Royal Match's edge is a dark SATURATED relative of
        /// what is behind it, never black. #022658 is the board/letter navy already in the palette,
        /// so the number belongs to the same colour world as everything else.</summary>
        /// <summary>Face dilate — compensates for TMP's inward-eating outline. Raise together
        /// with PAYOUT_STROKE_WIDTH; drop to 0 for the old thin look.</summary>
        public static float  PAYOUT_FACE_DILATE  = 0.14f;
        public static float  PAYOUT_STROKE_WIDTH = 0.30f;
        public static Color  PAYOUT_SHADOW_COLOR    = new Color(0.008f, 0.149f, 0.345f, 1f); // #022658
        public static float  PAYOUT_SHADOW_ALPHA    = 0.85f;
        public static float  PAYOUT_SHADOW_OFFSET_X = 0.18f;
        public static float  PAYOUT_SHADOW_OFFSET_Y = -0.35f;
        public static float  PAYOUT_SHADOW_DILATE   = 0.12f;
        public static float  PAYOUT_SHADOW_SOFTNESS = 0.22f;
        /// <summary>Badge corner offset, in half-cells. 0.76/0.74 straddled the tile edge so the
        /// number collided with the tile border and the board frame, reading as part of the letter.
        /// Pushed out so it clears the tile. 2026-09-21.</summary>
        public static float  BADGE_OFFSET_X     = 0.98f;
        public static float  BADGE_OFFSET_Y     = 0.96f;
        /// <summary>Log every cluster payout so the tier cut-offs can be set from real data
        /// instead of a 6-sample guess. Leave ON while playing, then grep [PayoutTier].</summary>
        public static bool   PAYOUT_TIER_LOG    = true;
        public static bool   PAYOUT_GRADIENT     = true;
        public static Color  PAYOUT_FILL_TOP     = new Color(0.847f, 0.890f, 0.961f, 1f); // cool off-white
        public static Color  PAYOUT_FILL_BOTTOM  = new Color(1.000f, 1.000f, 1.000f, 1f); // bright
        public static Color32 PAYOUT_STROKE_COLOR = new Color32(2, 38, 88, 255);   // #022658


        // ── OUTLINE COLOUR DERIVED FROM THE TILE (2026-09-22 Spencer) ────────────────
        // "change the outline to the same colour of whatever the cluster is, but match the
        // difference in hue of the green line from the green tiles."
        //
        // MEASURED off the shipping green pair — tile face #08B514 (H124.2 S95.6 V71.0) against
        // OUTLINE_GREEN_*. The outline is the tile's own colour LIT: rotated toward yellow,
        // desaturated about a quarter, brightened by a third. Feeding green back through this
        // reproduces the three constants exactly, which is what validates the rule.
        //
        // 🔑 IT IS "TOWARD YELLOW", NOT "MINUS 21 DEGREES". The green case only looks like a
        // subtraction by accident. Applied as a flat -21 to the orange tier (H22.8) you land on
        // H1.9 — RED, not a lit orange. Yellow is the brightest hue, so rotating toward it along
        // the SHORTER arc is what "lit" actually means, and it holds on every hue. Clamped so it
        // cannot overshoot: gold sits at H57.3, already 2.7 deg from yellow, and without the
        // clamp it would sail past into chartreuse.
        // (Mirror of the shadow recipe, which rotates toward violet, the darkest hue.)
        public static bool  OUTLINE_FROM_TILE = true;
        private const float HILIGHT_HUE = 60f;              // yellow
        //                              hue deg, sat mul, val mul, alpha
        private static readonly float[] OUT_SINGLE = { 20.9f, 0.726f, 1.371f, 0.96f };
        private static readonly float[] OUT_LO     = { 19.6f, 0.727f, 1.338f, 0.97f };
        private static readonly float[] OUT_HI     = { 13.8f, 0.791f, 1.155f, 1.00f };

        private static readonly Dictionary<string, Color[]> s_outlineByAsset
            = new Dictionary<string, Color[]>();

        /// <summary>Rotate a hue toward yellow along the shorter arc, never past it.</summary>
        private static float TowardHighlight(float hueDeg, float amount)
        {
            float d = Mathf.Repeat(HILIGHT_HUE - hueDeg + 180f, 360f) - 180f;   // signed shorter arc
            float step = Mathf.Min(amount, Mathf.Abs(d)) * Mathf.Sign(d);
            return Mathf.Repeat(hueDeg + step, 360f);
        }

        private static Color LitFrom(Color tile, float[] k)
        {
            float h, sv, v; Color.RGBToHSV(tile, out h, out sv, out v);
            var c = Color.HSVToRGB(TowardHighlight(h * 360f, k[0]) / 360f,
                                   Mathf.Clamp01(sv * k[1]), Mathf.Clamp01(v * k[2]));
            c.a = k[3];
            return c;
        }

        /// <summary>The three outline colours (single / lo / hi) for a face sprite, derived from
        /// its own flat face colour. Cached — the sample costs a RenderTexture blit.</summary>
        private static Color[] OutlineSetFor(string asset)
        {
            if (string.IsNullOrEmpty(asset)) return null;
            Color[] set;
            if (s_outlineByAsset.TryGetValue(asset, out set)) return set;

            Color face;
            if (!TryFaceColour(asset, out face)) { s_outlineByAsset[asset] = null; return null; }
            set = new[] { LitFrom(face, OUT_SINGLE), LitFrom(face, OUT_LO), LitFrom(face, OUT_HI) };
            s_outlineByAsset[asset] = set;
            WDLog.Log($"[Outline] {asset} face #{ColorUtility.ToHtmlStringRGB(face)} -> "
                    + $"single #{ColorUtility.ToHtmlStringRGB(set[0])} "
                    + $"lo #{ColorUtility.ToHtmlStringRGB(set[1])} "
                    + $"hi #{ColorUtility.ToHtmlStringRGB(set[2])}");
            return set;
        }

        /// <summary>Modal colour of the sprite's flat centre. Goes through a RenderTexture blit
        /// because Resources textures are not readable — the pattern Tile.WorldTintCopy uses.</summary>
        private static bool TryFaceColour(string asset, out Color face)
        {
            face = Color.white;
            var src = Resources.Load<Texture2D>(asset);
            if (src == null) return false;

            var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            var prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0); tex.Apply(false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);

            // The face is flat, so the MODE of the central 40% is the colour — a mean would be
            // dragged off by the bevel and the shaded bottom lip.
            int x0 = Mathf.RoundToInt(src.width * 0.30f), x1 = Mathf.RoundToInt(src.width * 0.70f);
            int y0 = Mathf.RoundToInt(src.height * 0.30f), y1 = Mathf.RoundToInt(src.height * 0.70f);
            var hist = new Dictionary<int, int>();
            var px = tex.GetPixels(x0, y0, x1 - x0, y1 - y0);
            int bestKey = -1, bestN = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].a <= 0.5f) continue;
                int key = (Mathf.RoundToInt(px[i].r * 255f) << 16)
                        | (Mathf.RoundToInt(px[i].g * 255f) << 8)
                        |  Mathf.RoundToInt(px[i].b * 255f);
                int n; hist.TryGetValue(key, out n); n++; hist[key] = n;
                if (n > bestN) { bestN = n; bestKey = key; }
            }
            Object.Destroy(tex);
            if (bestKey < 0) return false;
            face = new Color(((bestKey >> 16) & 255) / 255f, ((bestKey >> 8) & 255) / 255f, (bestKey & 255) / 255f, 1f);
            return true;
        }

        /// <summary>Which face sprite this cluster is wearing — gold beats the payout tier.</summary>
        private static string FaceAssetFor(GridManager grid, List<Vector2Int> cells, int tier)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                var t = grid.GetTile(cells[i].x, cells[i].y);
                if (t != null && t.IsGoldPrimedState) return Tile.GOLD_PRIMED_FACE_ASSET;
            }
            if (tier > 0 && Tile.PRIMED_TIER_ASSETS != null && tier < Tile.PRIMED_TIER_ASSETS.Length
                && !string.IsNullOrEmpty(Tile.PRIMED_TIER_ASSETS[tier]))
                return Tile.PRIMED_TIER_ASSETS[tier];
            return Tile.PRIMED_FACE_ASSET;
        }


        /// <summary>The tile's colour SHADOWED — the exact mirror of LitFrom. Rotate 18 deg
        /// toward VIOLET (the darkest hue) along the shorter arc, saturation UP slightly, value
        /// to 45%. Darkening alone desaturates and goes muddy; the saturation bump is what keeps
        /// it a shade of the colour rather than a grey version of it.
        ///
        /// WHY THIS BEATS THE FLAT NAVY IT REPLACES: #022658 had 2.95:1 contrast against a blue
        /// tile and 10.47:1 against gold — a stroke that nearly vanished on one cluster and read
        /// as a black bar on another, because navy IS dark blue. Derived per tile the range is
        /// 3.29-5.91, and the WORST case (blue) improves. Measured 2026-09-22.</summary>
        private const float SHADOW_HUE = 275f;              // violet
        private static readonly float[] INK_K = { 18f, 1.05f, 0.45f };

        private static Color ShadeFrom(Color tile)
        {
            float h, sv, v; Color.RGBToHSV(tile, out h, out sv, out v);
            float deg = h * 360f;
            float d = Mathf.Repeat(SHADOW_HUE - deg + 180f, 360f) - 180f;
            deg = Mathf.Repeat(deg + Mathf.Min(INK_K[0], Mathf.Abs(d)) * Mathf.Sign(d), 360f);
            var c = Color.HSVToRGB(deg / 360f, Mathf.Clamp01(sv * INK_K[1]), Mathf.Clamp01(v * INK_K[2]));
            c.a = 1f;
            return c;
        }

        /// <summary>Stroke + underlay colour for a cluster wearing this face sprite. Cached with
        /// the outline set, so both directions come off one sample of the same sprite.</summary>
        private static readonly Dictionary<string, Color> s_inkByAsset = new Dictionary<string, Color>();
        private static Color InkFor(string asset)
        {
            if (!PAYOUT_INK_FROM_TILE || string.IsNullOrEmpty(asset)) return PAYOUT_STROKE_COLOR;
            Color ink;
            if (s_inkByAsset.TryGetValue(asset, out ink)) return ink;
            Color face;
            ink = TryFaceColour(asset, out face) ? ShadeFrom(face) : (Color)PAYOUT_STROKE_COLOR;
            s_inkByAsset[asset] = ink;
            WDLog.Log($"[Outline] {asset} payout ink #{ColorUtility.ToHtmlStringRGB(ink)}");
            return ink;
        }

        /// <summary>Derive the badge ink per cluster. false = the flat #022658 everywhere.</summary>
        public static bool PAYOUT_INK_FROM_TILE = true;

        public static bool  OUTLINE_GREEN_FLAT   = false;                                   // true = one green, no payout ramp
        // 2026-09-10: brightened to the SCRABBLE GO outline Spencer sent — MEASURED #7CF84C,
        // hue 103, sat 92, L 82. The old values were matched to Royal Match's "+" button
        // (#4FA138, sat 48) and are much duller. Ramp direction kept (LO -> HI with payout).
        //   was: SINGLE (0.31,0.63,0.22) · LO (0.29,0.64,0.19) · HI (0.09,0.44,0.12)
        public static Color OUTLINE_GREEN_SINGLE = new Color(0.486f, 0.973f, 0.298f, 0.96f); // lone word — #7CF84C
        public static Color OUTLINE_GREEN_LO     = new Color(0.460f, 0.950f, 0.290f, 0.97f); // small group
        public static Color OUTLINE_GREEN_HI     = new Color(0.300f, 0.820f, 0.200f, 1.00f); // big payout

        /// <summary>Digit tracking. ⚠️ MOVES WITH PAYOUT_FACE_DILATE — the dilate grows every
        /// glyph outward, so tracking tuned for the undilated width collides. -12 was right at
        /// dilate 0; with dilate 0.14 the digits overlapped (Spencer 2026-09-21, "the kerning is
        /// a bit too close"). Raise the dilate and this must come up with it.</summary>
        public static float PAYOUT_POP_TRACKING = -2f;

        /// <summary>Light the outline up when a word primes or joins a cluster. Marks the cell
        /// so Draw can brighten whichever cluster contains it, and drives a redraw for the
        /// duration. 2026-09-10 Spencer.</summary>
        public void FlashCluster(Vector2Int cell)
        {
            if (!Enabled) return;
            _flashCell  = cell;
            _flashStart = Time.unscaledTime;
            if (!RootIsOffset) Refresh();
        }

        /// <summary>True while RiseBy has the whole outline root translated for a rising row.
        ///
        /// NEVER Refresh() in this state. Refresh redraws the mesh from the tiles' LIVE
        /// positions, and the root translation is then applied ON TOP — so the outline lands a
        /// full row above the letters. The rise is the one case where the mesh is deliberately
        /// stale and the root is doing the moving. 2026-09-10 Spencer.</summary>
        private bool RootIsOffset => transform.position.sqrMagnitude > 0.000001f;

        private Vector2Int _flashCell = new Vector2Int(-99, -99);
        private float      _flashStart = -99f;
        private const float FLASH_DUR = 0.42f;

        private void Update()
        {
            if (_flashStart < 0f) return;
            float t = Time.unscaledTime - _flashStart;
            if (RootIsOffset) return;   // mid-rise: the root is moving, the mesh must stay put
            if (t > FLASH_DUR) { _flashStart = -99f; _flashCell = new Vector2Int(-99, -99); Refresh(); return; }
            Refresh();   // cheap: a handful of small meshes, and only for ~0.4s
        }

        private float _lastCellSize = 1f;

        /// <summary>Where each currently-drawn cluster's payout badge sits, and how big its text
        /// is. The detonation pop starts from exactly here so the number the player is looking at
        /// is the one that grows and lifts away. 2026-09-10 Spencer.</summary>
        private readonly List<Color> _anchorInk = new List<Color>();
        private readonly List<(List<Vector2Int> cells, Vector3 at, float fontSize, int idx, Color col)> _anchors
            = new List<(List<Vector2Int>, Vector3, float, int, Color)>();

        public bool TryGetBadgeAnchor(Vector2Int cell, out Vector3 at, out float fontSize, out int idx, out Color col)
        {
            for (int i = 0; i < _anchors.Count; i++)
                if (_anchors[i].cells != null && _anchors[i].cells.Contains(cell))
                { at = _anchors[i].at; fontSize = _anchors[i].fontSize; idx = _anchors[i].idx; col = _anchors[i].col; return true; }
            at = Vector3.zero; fontSize = 0f; idx = -1; col = Color.white; return false;
        }

        private void EnsureBadge(int i)
        {
            while (_badges.Count <= i)
            {
                var bg = new GameObject($"BadgeBg{_badgeBg.Count}");
                bg.transform.SetParent(transform, false);
                var bsr = bg.AddComponent<SpriteRenderer>();
                bsr.sprite = s_pillSprite;
                bsr.sortingOrder = BADGE_ORDER;
                bsr.enabled = false;          // stays hidden until Draw places and sizes it
                bg.transform.localScale = Vector3.zero;
                _badgeBg.Add(bsr);

                var go = new GameObject($"Badge{_badges.Count}");
                go.transform.SetParent(transform, false);
                var tmp = go.AddComponent<TextMeshPro>();
                var f = GameFont.GetTMP(); if (f != null) tmp.font = f;
                tmp.alignment = TextAlignmentOptions.Center;
                tmp.enableWordWrapping = false;
                tmp.color = Color.white;
                var mr = tmp.GetComponent<MeshRenderer>();
                if (mr != null) mr.sortingOrder = BADGE_ORDER + 1;
                _badges.Add(tmp);
            }
        }

        /// <summary>Where cell (cx,cy) actually IS on screen right now.
        ///
        /// Cell coordinates update the INSTANT gravity runs, but the tiles then spend up to
        /// half a second animating into them. Tracing from cell coords therefore drew the
        /// outline at the destination while the letters were still in the air — the line
        /// hung in empty space and the tiles caught up to it. 2026-09-09 Spencer.
        ///
        /// Reading the tile's live transform instead makes the outline track the letters
        /// exactly, including the per-column stagger that makes a horizontal word shear
        /// slightly as it falls. Replicating the fall maths could never match that: each
        /// column starts 45ms after the one before it.
        ///
        /// Falls back to the cell centre when no tile is present, which is what an empty
        /// cell means and what every caller wants.</summary>
        private Vector3 CellPos(GridManager grid, int cx, int cy)
        {
            var t = grid.GetTile(cx, cy);
            return t != null ? t.transform.position : grid.CellToWorld(cx, cy);
        }

        private void Draw(int i, List<Vector2Int> cells, int payout, int wordCount, GridManager grid)
        {
            EnsureSprites(grid);
            EnsureBadge(i);
            _lastCellSize = grid.CellSize;
            bool group = wordCount >= 2;
            // PAYOUT TIER -> every tile in this cluster. Draw() already knows the cluster's
            // payout and its cells, so this is the natural place. 2026-09-21.
            int tier = Tile.TierForPayout(payout);
            if (PAYOUT_TIER_LOG && payout > 0)
                WDLog.Log($"[PayoutTier] payout={payout} words={wordCount} cells={cells.Count} -> tier {tier}");
            for (int ci = 0; ci < cells.Count; ci++)
            {
                var ct = grid.GetTile(cells[ci].x, cells[ci].y);
                if (ct != null) ct.SetPrimedTier(tier);
            }
            // Colour ramps with the PAYOUT, not just the word count — a quiet white for a
            // lone word, warming through gold to hot rose as the pile grows. The size of the
            // prize should be readable at a glance without reading the number.
            float mag = Mathf.Clamp01(payout / 900f);
            // GREEN (2026-09-10 Spencer, matching the RM "+" button green). Still ramps with
            // the payout — he liked that the colour tracks the size of the prize — but within
            // green now: a fresh light green for small webs deepening to a rich one for big.
            // Set OUTLINE_GREEN_FLAT to skip the ramp and use one colour everywhere.
            Color cSingle = OUTLINE_GREEN_SINGLE, cLo = OUTLINE_GREEN_LO, cHi = OUTLINE_GREEN_HI;
            if (OUTLINE_FROM_TILE)
            {
                var lit = OutlineSetFor(FaceAssetFor(grid, cells, tier));
                if (lit != null) { cSingle = lit[0]; cLo = lit[1]; cHi = lit[2]; }
            }
            Color c = group
                ? (OUTLINE_GREEN_FLAT ? cHi : Color.Lerp(cLo, cHi, mag))
                : cSingle;

            // FLASH: a word just primed into this cluster — punch the line bright and let it
            // fall back, so the board acknowledges the web growing. 2026-09-10 Spencer.
            if (_flashStart > 0f && cells.Contains(_flashCell))
            {
                float ft = Mathf.Clamp01((Time.unscaledTime - _flashStart) / FLASH_DUR);
                float k  = 1f - ft;               // 1 at the strike, 0 at rest
                k = k * k;                        // fast fall-off — a strike, not a glow
                c = Color.Lerp(c, Color.white, k * 0.85f);
            }
            // How far the shape pokes past the tile IS the line weight. 1.11 read as a fat
            // halo; a border wants to be thin and crisp while still following the tile's
            // rounded corners, which this construction gives for free. Payout is carried by
            // COLOUR, not by thickness — a thickening line just becomes a glow again.
            float grow = group ? 1.045f : 1.028f;

            // ── STROKE, IN FRONT (2026-09-09 Spencer, per the Scrabble GO reference) ──
            // A constant-weight line drawn OVER the tiles, tracing only the edges where the
            // cluster meets open space, with ARCS at the outer corners matching the tile's
            // corner radius. Interior edges are skipped, so it reads as one shape rather than
            // a box round every tile — and an edge whose perpendicular neighbour IS in the
            // cluster runs the full length so it joins the next cell's edge seamlessly.
            // ── FOLLOW THE LETTER FACE (2026-09-09 Spencer) ──────────────────────────
            // The border traces the SQUARE face of each tile, not the full sprite. The sprite
            // is ~20% taller because of the shaded top, and that top is hidden behind whatever
            // tile sits above — so tracing it meant special-casing every tile by whether it had
            // a neighbour above, which is where the seam and corner logic kept going wrong.
            // Every cell is now the same square: no seams, no exceptions, no special cases.
            var set = new HashSet<Vector2Int>(cells);
            float hw = grid.CellSize * 0.5f;
            float hh = grid.CellSize * 0.5f;
            // FRONT-ON ART 2026-09-15: sprite centre IS the face centre, so no drop.
            float faceDrop = Tile.TILE_ART_FRONT_ON ? 0f : -0.10f * grid.CellSize;
            float lw = grid.CellSize * (group ? 0.055f : 0.038f);   // line weight
            // 0.20 was too large for tight shapes. An inner arc reaches hw + rad = 0.70 of a
            // cell toward the next centre, so on a cross — four inner corners in one small
            // area — arcs from adjacent cells collide and the outline pinches. 0.11 keeps the
            // corners visibly soft while staying clear of its neighbours. 2026-09-09 Spencer.
            // Height of the shaded top strip: the sprite is CellSize/TILE_TARGET_ASPECT tall
            // while the face is CellSize, and the face centre sits faceDrop below the sprite
            // centre — so the cap stands this far above the face top. Works out at 0.20 cells.
            // FRONT-ON ART 2026-09-15: no exposed top cap, so nothing to rise over. This was
            // what made the outline snake — it stepped up 0.20 of a cell over any tile with
            // nothing above it, and stayed low where a tile covered the cap. Spencer: "the
            // outline no longer needs to snake around".
            float capH = Tile.TILE_ART_FRONT_ON
                       ? 0f
                       : (-faceDrop + (grid.CellSize / Tile.ArtAspect) * 0.5f) - hh;
            float rad = grid.CellSize * 0.18f;   // outer corners only — can be generous again

            var verts = new List<Vector3>();
            var tris  = new List<int>();
            for (int k = 0; k < cells.Count; k++)
            {
                Vector2Int cl = cells[k];
                Vector3 C = CellPos(grid, cl.x, cl.y) + new Vector3(0f, faceDrop, 0f);
                C.z -= 0.35f;
                // ── THE EXPOSED TOP CAP (2026-09-10 Spencer) ────────────────────────────
                // The tile sprite is 20% taller than its face; that extra strip is the shaded
                // top. It is only VISIBLE when nothing sits on the tile — otherwise the tile
                // above covers it. Tracing the face for every cell therefore cut across the
                // top row's caps and left them outside the line.
                //
                // The rule is simply "is this cell the top of its stack", which is one board
                // lookup — NOT the per-tile seam special-casing that went wrong before. A cell
                // with any tile above it (in the cluster or not) keeps the plain face height,
                // because its cap is hidden behind that tile.
                float topY = (grid.GetTile(cl.x, cl.y + 1) == null) ? hh + capH : hh;
                bool up = !set.Contains(new Vector2Int(cl.x, cl.y + 1));
                bool dn = !set.Contains(new Vector2Int(cl.x, cl.y - 1));
                bool lf = !set.Contains(new Vector2Int(cl.x - 1, cl.y));
                bool rt = !set.Contains(new Vector2Int(cl.x + 1, cl.y));

                // INNER corners: where the outline steps UP around a taller part of the
                // cluster. The bend curves the opposite way to an outer corner — its centre
                // sits OUTSIDE the shape — and the straight run has to overshoot to meet it,
                // rather than pulling back the way it does at an outer corner. Without this
                // the outline meets itself at a hard 90 and stops looking form-fitting.
                // INNER corners are left SQUARE. I implemented only the TOP two cases, so a
                // cross folded on its top half and met square on the bottom — visibly lopsided.
                // Symmetric and correct in every configuration beats rounded in half of them,
                // and the eye goes to the OUTER corners, which are still arced. 2026-09-09.
                float xL = lf ? -hw + rad : -hw;
                float xR = rt ?  hw - rad :  hw;
                if (up) Seg(verts, tris, C + new Vector3(xL, topY, 0), C + new Vector3(xR, topY, 0), lw);
                if (dn) Seg(verts, tris, C + new Vector3(lf ? -hw + rad : -hw, -hh, 0), C + new Vector3(rt ?  hw - rad :  hw, -hh, 0), lw);
                // A side edge whose cell has a cluster tile BELOW-outward is the vertical run
                // that meets an inner corner from above — start it past the arc.
                // The vertical run that meets an inner corner from ABOVE is drawn by the cell
                // whose side edge EXISTS (lf/rt true) and which has a cluster tile diagonally
                // below it. I had these inverted — testing for the edge NOT being drawn — which
                // is the case where there is no line to shorten, so it also broke the ordinary
                // edges. 2026-09-09.
                float yLo = dn ? -hh + rad : -hh;
                float yHi = up ? topY - rad : topY;

                // A side edge must stop where the horizontal run it meets actually IS. When the
                // cell diagonally below is in the cluster AND its cap is exposed, that cell's
                // top edge sits capH higher than this cell's floor — so running the side edge
                // all the way down to the floor left a stub hanging below the horizontal line,
                // which is the outline appearing to dig down between two letters. Raise the
                // start to meet it. 2026-09-10 Spencer.
                float yLoL = yLo, yLoR = yLo;
                if (lf && set.Contains(new Vector2Int(cl.x - 1, cl.y - 1))
                       && grid.GetTile(cl.x - 1, cl.y) == null)
                    yLoL = Mathf.Max(yLoL, -hh + capH);
                if (rt && set.Contains(new Vector2Int(cl.x + 1, cl.y - 1))
                       && grid.GetTile(cl.x + 1, cl.y) == null)
                    yLoR = Mathf.Max(yLoR, -hh + capH);

                if (lf) Seg(verts, tris, C + new Vector3(-hw, yLoL, 0), C + new Vector3(-hw, yHi, 0), lw);
                if (rt) Seg(verts, tris, C + new Vector3( hw, yLoR, 0), C + new Vector3( hw, yHi, 0), lw);

                // Where a capped cell sits beside a cluster neighbour whose cap is hidden, the
                // two tops no longer line up. Close that step with a short vertical, drawn by
                // the TALLER cell only so it is never drawn twice. 2026-09-10 Spencer.
                // ...but NOT when the cell diagonally above is itself in the cluster. That cell
                // already draws its own side edge along this exact x, starting at this exact
                // point, so the connector laid a second line over the bottom of it — which read
                // as the outline digging down into the gap between two letters. Skip it and the
                // two runs simply meet. 2026-09-10 Spencer.
                if (!lf && !set.Contains(new Vector2Int(cl.x - 1, cl.y + 1)))
                {
                    float nT = (grid.GetTile(cl.x - 1, cl.y + 1) == null) ? hh + capH : hh;
                    if (nT < topY - 0.0001f)
                        Seg(verts, tris, C + new Vector3(-hw, nT, 0), C + new Vector3(-hw, topY, 0), lw);
                }
                if (!rt && !set.Contains(new Vector2Int(cl.x + 1, cl.y + 1)))
                {
                    float nT = (grid.GetTile(cl.x + 1, cl.y + 1) == null) ? hh + capH : hh;
                    if (nT < topY - 0.0001f)
                        Seg(verts, tris, C + new Vector3( hw, nT, 0), C + new Vector3( hw, topY, 0), lw);
                }

                if (up && lf) Arc(verts, tris, C + new Vector3(-hw + rad, topY - rad, 0), rad, 90f,  180f, lw);
                if (up && rt) Arc(verts, tris, C + new Vector3( hw - rad, topY - rad, 0), rad,  0f,   90f, lw);
                if (dn && lf) Arc(verts, tris, C + new Vector3(-hw + rad, -hh + rad, 0), rad, 180f, 270f, lw);
                if (dn && rt) Arc(verts, tris, C + new Vector3( hw - rad, -hh + rad, 0), rad, 270f, 360f, lw);
            }

            BuildMesh(NextHalo(), verts, tris, c);

            // Badge: solid pill behind the number so it reads on ANY tile colour. White text
            // on a busy board was disappearing — a backing plate is how Royal Match and
            // Scrabble GO both keep small numbers legible.
            Vector2Int corner = cells[0];
            for (int k = 1; k < cells.Count; k++)
                if (cells[k].y < corner.y || (cells[k].y == corner.y && cells[k].x > corner.x)) corner = cells[k];
            // Corner is measured off the TILE's real extents: half a cell across, but the tile
            // is ~20% taller than a cell, so vertical uses the tile half-height not half-width.
            float halfW = grid.CellSize * 0.5f;
            float halfH = (grid.CellSize / Tile.ArtAspect) * 0.5f;
            Vector3 at = CellPos(grid, corner.x, corner.y)
                       + new Vector3(halfW * BADGE_OFFSET_X, -halfH * BADGE_OFFSET_Y, -0.5f);

            var b = _badges[i];
            b.text = payout.ToString("N0");
            // 2026-09-10 Spencer: "still illegible". Up ~45%. At the old size the dark stroke
            // was heavier than the glyph's own strokes, so the digits filled in and read as a
            // blob — bigger fixes that faster than any amount of stroke tuning, because it
            // restores the ratio between the two.
            b.fontSize = group ? PAYOUT_FONT_GROUP : PAYOUT_FONT_SINGLE;
            // Remember the badge's anchor so the detonation pop can grow OUT OF this number
            // rather than appearing somewhere else at full size. 2026-09-10 Spencer.
            Color badgeInk = InkFor(FaceAssetFor(grid, cells, tier));
            while (_anchorInk.Count < _anchors.Count) _anchorInk.Add(PAYOUT_STROKE_COLOR);
            // POP FROM THE CENTRE OF THE CLUSTER, not its corner. 2026-10-19 Spencer.
            // `at` above is the bottom-right-most cell plus a badge offset, chosen so the pop
            // could grow OUT of the corner badge. That badge is switched off
            // (SHOW_CORNER_BADGE = false), so the number was flying up from the corner of the
            // blast for no visible reason. Every other match-3 puts the score at the middle of
            // what just exploded, which is also where the eye already is.
            //
            // The BADGE keeps using `at`, so re-enabling it still anchors it in the corner.
            Vector3 popAt = Vector3.zero;
            for (int k = 0; k < cells.Count; k++) popAt += CellPos(grid, cells[k].x, cells[k].y);
            popAt /= Mathf.Max(1, cells.Count);
            popAt.z = at.z;                      // keep the badge's depth so sorting is unchanged

            _anchors.Add((new List<Vector2Int>(cells), popAt, b.fontSize, i, b.color));
            _anchorInk.Add(badgeInk);
            // The number sits over a busy board of light tiles, so it gets BOTH a solid dark
            // plate behind it and its own dark outline. White-on-anything was disappearing.
            // WHITE with the SAME heavy stroke the detonation pop uses (2026-09-10 Spencer),
            // so the corner number and the number that flies off it are the identical object —
            // and the stroke, not a plate, is what keeps it legible on any tile colour.
            b.color = Color.white;
            b.characterSpacing = PAYOUT_POP_TRACKING;   // same tracking as the pop
            StylePayout(b, badgeInk);
            // KEEP IT ON SCREEN. The badge hangs off the cluster's bottom-right corner, so a
            // word ending at the right edge of the board pushed its number past the screen and
            // "290" showed as "29". Measure the real glyph bounds and slide it inside the
            // camera. Same treatment the detonation pop already had. 2026-09-10 Spencer.
            b.ForceMeshUpdate();
            var bcam = Camera.main;
            if (bcam != null && bcam.orthographic)
            {
                Vector3 bext = b.textBounds.extents;
                float bhw = Mathf.Max(bext.x, 0.05f), bhh = Mathf.Max(bext.y, 0.05f);
                float ch = bcam.orthographicSize, cw = ch * bcam.aspect;
                Vector3 cc2 = bcam.transform.position;
                float bpad = grid.CellSize * 0.04f;
                float lo = cc2.x - cw + bhw + bpad, hi = cc2.x + cw - bhw - bpad;
                if (lo <= hi) at.x = Mathf.Clamp(at.x, lo, hi);
                float blo = cc2.y - ch + bhh + bpad, bhi = cc2.y + ch - bhh - bpad;
                if (blo <= bhi) at.y = Mathf.Clamp(at.y, blo, bhi);
            }

            b.transform.position = at;
            b.gameObject.SetActive(SHOW_CORNER_BADGE);

            var bbg = _badgeBg[i];
            if (!SHOW_CORNER_BADGE || !BADGE_PLATE) { bbg.enabled = false; return; }
            bbg.sprite = s_pillSprite;
            bbg.color  = PlateColour(badgeInk, group);      // the cluster's own dark shade
            bbg.transform.position = at + new Vector3(0f, 0f, 0.02f);
            bbg.transform.localScale = PlateScale(grid.CellSize, group, b.text.Length,
                                                  bbg.sprite.bounds.size.x);
            bbg.sortingOrder = BADGE_ORDER;
            bbg.enabled = true;
        }

    }
}
