using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// 11-23 WORD POP-UP LETTERING (Spencer: the pop-ups "look amateurish ... need them to look professional with innershadow
    /// treatment"; picked mock A in WordSlop Bold). The live text could only draw one even outline and one drop, so the look is
    /// BAKED: every letter exists as five pre-drawn layers (soft shadow, lip, lit rim, dark line, face with its inner shadow)
    /// in Resources/UI/PopLetters, made by WordDrop_Art/typography/pop_letters/bake_pop_letters.py. A word is drawn layer by
    /// layer - every lip, then every rim, then every line, then every face - so neighbouring letters fuse into one slab,
    /// exactly as in the mock. Letters A-Z and spaces only; anything else (e.g. "+2 MORE") returns null and the caller falls
    /// back to the old text. ON = false: old text everywhere.
    /// </summary>
    public static class PopLetters
    {
        public static bool ON = true;
        // 11-23 Spencer: "mock up what it would look like without the beveled backstop" -> picked mock B = no coloured rim, no lip
        // behind the letters: face + dark line + soft shadow only. true/true = the full stack (rim + lip) as first built.
        public static bool RIM_ON = false, LIP_ON = false;
        // 11-23 Spencer: "the backstop is too dark" -> mock A: the part showing UNDER the letters is a mid colour of the word's own
        // family, not the same near-black as the outline (it was LINE_GOLD / LINE_BLUE).
        public static Color DROP_GOLD = new Color32(0xC0, 0x60, 0x0C, 0xFF), DROP_BLUE = new Color32(0x3A, 0x56, 0xC8, 0xFF);
        public static float LINE_DROP = 0.07f;   // 11-23 Spencer after B in game: "lets try c". Of cap height; 0 = mock B (no drop)
        public static float MIP_BIAS = -0.7f;   // negative = sharper (uses a larger copy of the art than the screen size asks for)
        // 11-23 (Spencer: "the text pop ups are way to big now"; 09:08 screenshot: caps 1.1 tiles tall). The old text was sized by its
        // TMP bounds, which are taller than the capitals, so its real caps were ~0.8 of a tile - the size the mock was drawn at.
        public static float CAP_CELLS = 0.50f /* 11-23: 0.80 -> 0.65 -> 0.50 ("still too big"), Spencer after the recording: "it needs to be smaller" */;   // cap height of a 4-letter word, in tiles, after the pop's swell
        public static Color SHADOW = new Color32(0x1A, 0x0B, 0x2E, 0x80);
        // 11-23 Spencer: goal words in HOT ORANGE "instead of our current gold one" (rim + face are baked; these two are tints).
        // "GOLD" in the names = the goal-word colourway. Gold values were lip 6E2A05, line 4A1604.
        public static Color LIP_GOLD = new Color32(0x6A, 0x15, 0x05, 0xFF), LINE_GOLD = new Color32(0x4A, 0x0E, 0x04, 0xFF);   // 11-23: tried one step lighter (6D240B / 381D71) in game - Spencer: "it was better before". Keep the dark ones.
        public static Color LIP_BLUE = new Color32(0x1B, 0x3C, 0x86, 0xFF), LINE_BLUE = new Color32(0x24, 0x10, 0x4E, 0xFF);

        private static readonly Dictionary<string, Texture2D> s_tex = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, Sprite> s_sprites = new Dictionary<string, Sprite>();

        private static Sprite Glyph(string atlas, int i)
        {
            string key = atlas + i;
            if (s_sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            if (!s_tex.TryGetValue(atlas, out var tex) || tex == null) { tex = Resources.Load<Texture2D>("UI/PopLetters/pop" + atlas); s_tex[atlas] = tex; }
            if (tex == null) return null;
            // 11-23 (Spencer: the lettering "doesnt cut"): the art is drawn ~2-3x bigger than it shows, and the default sampling
            // picked a blurrier copy than needed. Blend between copies and lean toward the sharper one.
            if (tex.filterMode != FilterMode.Trilinear) { tex.filterMode = FilterMode.Trilinear; tex.mipMapBias = MIP_BIAS; }
            float k = tex.width / (float)PopLetterMetrics.ATLAS_W;   // the importer may have halved the atlas
            int col = i % PopLetterMetrics.COLS, row = i / PopLetterMetrics.COLS;
            var rect = new Rect(col * PopLetterMetrics.CELL_W * k, tex.height - (row + 1) * PopLetterMetrics.CELL_H * k,
                                PopLetterMetrics.CELL_W * k, PopLetterMetrics.CELL_H * k);
            // pivot = the glyph's pen origin on the baseline; 1 unit = one cap height
            var pivot = new Vector2(PopLetterMetrics.PAD_L / PopLetterMetrics.CELL_W,
                                    1f - (PopLetterMetrics.PAD_T + PopLetterMetrics.CAP_PX) / PopLetterMetrics.CELL_H);
            sp = Sprite.Create(tex, rect, pivot, PopLetterMetrics.CAP_PX * k, 0, SpriteMeshType.FullRect);
            s_sprites[key] = sp; return sp;
        }

        public static bool CanDraw(string label)
        {
            if (!ON || string.IsNullOrEmpty(label)) return false;
            bool any = false;
            foreach (char ch in label)
            {
                if (ch == ' ') continue;
                if (PopLetterMetrics.GLYPHS.IndexOf(char.ToUpperInvariant(ch)) < 0) return false;
                any = true;
            }
            return any && Glyph("_face_gold", 0) != null && Glyph("_line", 0) != null;
        }

        /// <summary>Builds <paramref name="label"/> under <paramref name="parent"/>, centred on it, caps <paramref name="capWorld"/>
        /// world units tall. <paramref name="order"/> = the face layer's sorting order (the other four sit just below it).
        /// Returns null if the label cannot be drawn. halfW / halfH = the word's half extents in world units.</summary>
        public static GameObject Build(Transform parent, string label, bool gold, float capWorld, int order,
                                       out float halfW, out float halfH, out System.Action<float> setAlpha)
        {
            halfW = halfH = 0f; setAlpha = null;
            if (!CanDraw(label)) return null;
            string pal = gold ? "gold" : "blue";
            var root = new GameObject("PopLetters");
            root.transform.SetParent(parent, false);
            root.transform.localScale = Vector3.one * capWorld;

            float total = 0f;
            foreach (char ch in label) total += ch == ' ' ? PopLetterMetrics.SPACE : PopLetterMetrics.ADVANCE[PopLetterMetrics.GLYPHS.IndexOf(char.ToUpperInvariant(ch))];

            var all = new List<SpriteRenderer>(); var baseA = new List<float>();
            // back to front: shadow, lip, rim, line, face
            var ll = new List<(string atlas, Color tint, float dy)>();
            ll.Add(("_shadow", SHADOW, 0f));
            if (RIM_ON && LIP_ON) ll.Add(("_grow", gold ? LIP_GOLD : LIP_BLUE, -PopLetterMetrics.LIP_DY));
            if (RIM_ON) ll.Add(("_rim_" + pal, Color.white, 0f));
            if (!RIM_ON && LINE_DROP > 0f) ll.Add(("_line", gold ? DROP_GOLD : DROP_BLUE, -LINE_DROP));   // mock C: the dark line again, a little lower = a hint of thickness
            ll.Add(("_line", gold ? LINE_GOLD : LINE_BLUE, 0f));
            ll.Add(("_face_" + pal, Color.white, 0f));
            var layers = ll.ToArray();
            for (int l = 0; l < layers.Length; l++)
            {
                float x = -total * 0.5f;
                foreach (char ch in label)
                {
                    if (ch == ' ') { x += PopLetterMetrics.SPACE; continue; }
                    int i = PopLetterMetrics.GLYPHS.IndexOf(char.ToUpperInvariant(ch));
                    var g = new GameObject(layers[l].atlas);
                    g.transform.SetParent(root.transform, false);
                    g.transform.localPosition = new Vector3(x, -0.5f + layers[l].dy, 0f);   // baseline half a cap below the centre
                    var sr = g.AddComponent<SpriteRenderer>();
                    sr.sprite = Glyph(layers[l].atlas, i); sr.color = layers[l].tint; sr.sortingOrder = order - (layers.Length - 1 - l);
                    all.Add(sr); baseA.Add(layers[l].tint.a);
                    x += PopLetterMetrics.ADVANCE[i];
                }
            }
            halfW = (total * 0.5f + (RIM_ON ? 0.13f : 0.09f)) * capWorld;
            halfH = 0.72f * capWorld;
            // 11-23 (Movie_026: the fade went muddy - with every layer half see-through the dark line, lip and shadow showed THROUGH
            // the faces). The back layers fade out over the first half, the faces over the second: the word thins to clean
            // letters, then goes.
            int perLayer = all.Count / layers.Length, faceFrom = perLayer * (layers.Length - 1);
            setAlpha = v =>
            {
                float back = Mathf.Clamp01(v * 2f - 1f), face = Mathf.Clamp01(v * 2f);
                for (int n = 0; n < all.Count; n++) if (all[n] != null) { var c = all[n].color; c.a = baseA[n] * (n >= faceFrom ? face : back); all[n].color = c; }
            };
            return root;
        }
    }
}
