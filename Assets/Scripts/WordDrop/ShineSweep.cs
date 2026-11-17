using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// SHINE SWEEP (2026-10-29, Spencer's Royal Kingdom crown GIF: "is it possible to add this effect to the panels when you tap on
    /// them on the home screen?"). A soft diagonal band of light glides across the object, clipped to the object's OWN silhouette,
    /// while the object briefly brightens - the glint you see on the RK crown/dragon/turtle icons. Additive (WordDrop/AdditiveSprite).
    ///   ShineSweep.Play(image)            - clipped to that Image's sprite (icons: heart, coin, nav icons, portrait)
    ///   ShineSweep.PlayRect(rt, radius)   - clipped to a rounded rect (the cream panels baked into the bar art)
    /// </summary>
    public static class ShineSweep
    {
        public static bool ON = true;
        // V2 = the dodge shine (11-01). false -> the old white additive band.
        public static bool  V2 = false;
        // V3 = the crown GIF's light (11-01). Default ON; false -> V2 / the old white band.
        public static bool  V3 = false;
        // V4 = the researched shine (UIEffect Shiny recipe). DEFAULT. false -> the old white band.
        public static bool  V4 = true;
        public static float V4_HALF_WIDTH = 0.22f, V4_STRENGTH = 0.8f, V4_ANGLE = 20f, V4_DURATION = 1.0f;
        private static Sprite s_smooth;
        /// <summary>p = smoothstep(0,1, 1-|d|): a soft triangle with zero slope at the centre and the edges.</summary>
        private static Sprite SmoothBand()
        {
            if (s_smooth != null) return s_smooth;
            const int W = 64, H = 8; var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[W * H];
            for (int x = 0; x < W; x++)
            {
                float d = Mathf.Abs((x + 0.5f) / W * 2f - 1f), q = Mathf.Clamp01(1f - d), a = q * q * (3f - 2f * q);
                for (int y = 0; y < H; y++) px[y * W + x] = new Color(1f, 1f, 1f, a);
            }
            t.SetPixels(px); t.Apply();
            return s_smooth = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }   // 11-01 Spencer: soft light + wash both "look bad" - old white band back as default
        public static float V3_WIDTH = 1.3f, V3_EDGE = 0.3f, V3_STRENGTH = 0.6f, V3_ANGLE = 20f, V3_DURATION = 1.4f;   // width x object; edge = soft fraction each side
        public static Color V3_TINT = new Color(1f, 0.95f, 0.78f, 1f);
        private static Material s_screen;
        private static Material ScreenMat()
        {
            if (s_screen != null) return s_screen;
            var sh = Shader.Find("WordDrop/UIScreen"); if (sh == null) sh = Resources.Load<Shader>("Shaders/UIScreen");
            if (sh != null) s_screen = new Material(sh) { name = "ShineScreen" };
            return s_screen;
        }   // 11-01 Spencer tried it in game: "nah that looks worse" - kept as an option
        public static float V2_WIDTH = 0.32f, V2_DODGE = 1.0f, V2_SLIVER = 0.6f, V2_CORE = 0.55f, V2_DURATION = 0.42f;
        private static Material s_dodge;
        private static Material DodgeMat()
        {
            if (s_dodge != null) return s_dodge;
            var sh = Shader.Find("WordDrop/UIDodge"); if (sh == null) sh = Resources.Load<Shader>("Shaders/UIDodge");
            if (sh != null) s_dodge = new Material(sh) { name = "ShineDodge" };
            return s_dodge;
        }
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> s_washes = new System.Collections.Generic.Dictionary<int, Sprite>();
        /// <summary>A lit WASH: full across the middle, smoothstep-soft over <paramref name="edge"/> of the width at each side.</summary>
        private static Sprite Wash(float edge)
        {
            int key = Mathf.RoundToInt(edge * 100f); if (s_washes.TryGetValue(key, out var sp) && sp != null) return sp;
            const int W = 128, H = 8; var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[W * H];
            for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W, e = Mathf.Max(0.01f, edge);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(u / e)) * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((1f - u) / e));
                for (int y = 0; y < H; y++) px[y * W + x] = new Color(1f, 1f, 1f, a);
            }
            t.SetPixels(px); t.Apply();
            return s_washes[key] = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> s_bands = new System.Collections.Generic.Dictionary<int, Sprite>();
        /// <summary>A soft vertical band, gaussian across with sharpness <paramref name="sharp"/>, fading at both ends.</summary>
        private static Sprite BandShaped(float sharp)
        {
            int key = Mathf.RoundToInt(sharp * 10f); if (s_bands.TryGetValue(key, out var sp) && sp != null) return sp;
            const int W = 64, H = 128; var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                float a = Mathf.Exp(-u * u * sharp) * Mathf.Clamp01((1f - Mathf.Abs(v)) * 3f);
                px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
            t.SetPixels(px); t.Apply();
            return s_bands[key] = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }
        public static float DURATION = 0.32f, BAND_ANGLE = 24f, BAND_WIDTH = 0.34f, STRENGTH = 0.25f, FLASH = 0.05f;   // 10-29 Spencer: "brightens too much" (was .85 band, .28 flash)
        private static Sprite s_band; private static Material s_add;

        public static void Play(Image target) { if (target != null && target.sprite != null) Run(target.rectTransform, target.sprite, false, target); }
        /// <summary>Sweep over a panel using its EXACT shape cut from the art (Resources/UI/Shine/<name>.png + shine_masks.json,
        /// made by hud/ramp_match/make_shine_masks.py). <paramref name="artRoot"/> = the RectTransform the art image fills
        /// (the nav bar, the home HUD bar); the mask is placed over it at the panel's measured box. 2026-10-29 Spencer: "masked to
        /// the panel and run across the whole shape of the panel".</summary>
        public static void PlayMask(RectTransform artRoot, string name)
        {
            if (artRoot == null) return;
            var sp = Resources.Load<Sprite>("UI/Shine/" + name);
            if (sp == null) { var tx = Resources.Load<Texture2D>("UI/Shine/" + name); if (tx != null) sp = Sprite.Create(tx, new Rect(0, 0, tx.width, tx.height), new Vector2(0.5f, 0.5f), 100f); }
            var box = MaskBox(name); if (sp == null || box == null) return;
            var holder = artRoot.Find("Shine_" + name) as RectTransform;
            if (holder == null) { var go = new GameObject("Shine_" + name, typeof(RectTransform)); go.transform.SetParent(artRoot, false); holder = (RectTransform)go.transform; }
            holder.SetAsFirstSibling();   // 10-29 Spencer: run across the PANEL, not the icon - draw just above the art, UNDER icons + text
            holder.anchorMin = new Vector2(box[0], 1f - box[3]); holder.anchorMax = new Vector2(box[2], 1f - box[1]);
            holder.offsetMin = holder.offsetMax = Vector2.zero;
            Run(holder, sp, false, null);
        }
        [System.Serializable] private class MaskRow { public string name; public float[] box; }
        private static System.Collections.Generic.Dictionary<string, float[]> s_boxes;
        private static float[] MaskBox(string name)
        {
            if (s_boxes == null)
            {
                s_boxes = new System.Collections.Generic.Dictionary<string, float[]>();
                var ta = Resources.Load<TextAsset>("UI/Shine/shine_masks"); if (ta == null) return null;
                // tiny parser for {"name": [a,b,c,d], ...}
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(ta.text, "\"([^\"]+)\"\\s*:\\s*\\[([^\\]]+)\\]"))
                {
                    var parts = m.Groups[2].Value.Split(','); var f = new float[4];
                    for (int i = 0; i < 4 && i < parts.Length; i++) float.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f[i]);
                    s_boxes[m.Groups[1].Value] = f;
                }
            }
            return s_boxes.TryGetValue(name, out var b) ? b : null;
        }
        public static void PlayRect(RectTransform rt, int radius = 40)
        {
            if (rt == null) return;
            // 10-29 fix (Spencer: "not masking to the panel but a circle"): the shared rounded-rect sprite has no 9-slice border, so
            // 'Sliced' stretched it into an ellipse. Build a rounded rect at THIS rect's own aspect (radius in the rect's units).
            var size = rt.rect.size; if (size.x < 1f || size.y < 1f) return;
            Run(rt, RoundRect(size.x, size.y, radius), false, null);
        }
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_rr = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite RoundRect(float w, float h, float r)
        {
            float k = Mathf.Min(1f, 256f / Mathf.Max(w, h));                    // texture <= 256px on the long side
            int W = Mathf.Max(8, Mathf.RoundToInt(w * k)), H = Mathf.Max(8, Mathf.RoundToInt(h * k)); float R = Mathf.Min(r * k, Mathf.Min(W, H) * 0.5f);
            string key = W + "x" + H + "r" + Mathf.RoundToInt(R);
            if (s_rr.TryGetValue(key, out var sp) && sp != null) return sp;
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp }; var px = new Color[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float qx = Mathf.Max(Mathf.Abs(x + 0.5f - W * 0.5f) - (W * 0.5f - R), 0f), qy = Mathf.Max(Mathf.Abs(y + 0.5f - H * 0.5f) - (H * 0.5f - R), 0f);
                float d = Mathf.Sqrt(qx * qx + qy * qy) - R;
                px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(0.5f - d));
            }
            t.SetPixels(px); t.Apply();
            return s_rr[key] = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }

        private static void Run(RectTransform target, Sprite shape, bool sliced, Image flashOf)
        {
            if (!ON || target == null || !target.gameObject.activeInHierarchy) return;
            var old = target.Find("ShineSweep"); if (old != null) Object.Destroy(old.gameObject);
            // the mask: the target's own shape, invisible
            var mGO = new GameObject("ShineSweep", typeof(RectTransform), typeof(Image), typeof(Mask));
            mGO.transform.SetParent(target, false); mGO.transform.SetAsLastSibling();
            var mrt = (RectTransform)mGO.transform; mrt.anchorMin = Vector2.zero; mrt.anchorMax = Vector2.one; mrt.offsetMin = mrt.offsetMax = Vector2.zero;
            var mimg = mGO.GetComponent<Image>(); mimg.sprite = shape; mimg.raycastTarget = false;
            // ALPHA-CLIPPED mask (10-29 fix): a plain Mask writes the stencil for the sprite's whole QUAD, so the glint filled the
            // icon's rectangle. UI/Default with UNITY_UI_ALPHACLIP discards transparent pixels -> the stencil = the real silhouette.
            var mm = MaskMat(); if (mm != null) mimg.material = mm;
            mimg.preserveAspect = flashOf != null && flashOf.preserveAspect;
            mGO.GetComponent<Mask>().showMaskGraphic = false;
            // a soft whole-object flash (the RK icon brightens as the glint passes) + the travelling band
            var size = target.rect.size; float diag = size.magnitude;
            var flash = NewImg("Flash", mGO.transform, SoftWhite(), new Color(1f, 0.97f, 0.85f, 0f));
            var frt = flash.rectTransform; frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = frt.offsetMax = Vector2.zero;
            Image band; RectTransform brt; float travel;
            var seq = DOTween.Sequence().SetUpdate(true);
            if (V4 && DodgeMat() != null)
            {
                // V4 (11-01, from research - mob-sakai UIEffect "Shiny", gloss = 1): ONE band, profile smoothstep(1 - |d|/hw) (zero slope
                // at centre AND edges = no line anywhere), hw = 0.22 x object width (half-max ~0.20), gain in the pixel's OWN colour
                // (UIDodge: dst x (1 + S x p)) at S = 0.8 (~+46/255 on mid-tones, like the RK crown), 20 deg, 1.0 s InOutSine.
                // No core, no sliver, no flash.
                float hw = size.x * V4_HALF_WIDTH;
                band = NewImg("Shine", mGO.transform, SmoothBand(), new Color(1f, 1f, 1f, V4_STRENGTH));
                band.material = DodgeMat();
                brt = band.rectTransform; brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(hw * 2f, diag * 1.4f);
                brt.localRotation = Quaternion.Euler(0, 0, -V4_ANGLE);
                travel = size.x * 0.5f + hw + size.y * Mathf.Tan(V4_ANGLE * Mathf.Deg2Rad) * 0.5f;
                brt.anchoredPosition = new Vector2(-travel, 0f);
                seq.Append(brt.DOAnchorPosX(travel, V4_DURATION).SetEase(Ease.InOutSine));
            }
            else if (V3 && ScreenMat() != null)
            {
                // V3 (11-01, MEASURED off Spencer's crown GIF "a blended light rather than ... a white line"): ONE wide soft gaussian
                // (half-max width ~28% of the object), no hard core, warm pale tint, SCREEN blend (lifts darks more than lights - the
                // GIF's gain was 43 on darks vs 34 on lights), ~20 deg, and SLOW: ~1.1 s to cross (the old 0.32 s read as a flicked line).
                // v3b (11-01, Spencer: "doesnt look right still"; the GIF's light rendered on its own = a WIPE, not a band): a lit
                // WASH wider than the object with soft leading/trailing edges - it enters left, the WHOLE object is lit for a beat,
                // then it exits right.
                band = NewImg("Light", mGO.transform, Wash(V3_EDGE), new Color(V3_TINT.r, V3_TINT.g, V3_TINT.b, V3_STRENGTH));
                band.material = ScreenMat();
                brt = band.rectTransform; brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(size.x * V3_WIDTH + size.y * Mathf.Tan(V3_ANGLE * Mathf.Deg2Rad), diag * 1.4f);
                brt.localRotation = Quaternion.Euler(0, 0, -V3_ANGLE);
                travel = size.x * 0.5f + brt.sizeDelta.x * 0.5f + size.y * Mathf.Tan(V3_ANGLE * Mathf.Deg2Rad) * 0.5f;
                brt.anchoredPosition = new Vector2(-travel, 0f);
                seq.Append(brt.DOAnchorPosX(travel, V3_DURATION).SetEase(Ease.InOutSine));
            }
            else if (V2 && DodgeMat() != null)
            {
                // V2 (11-01, Spencer: "a basic white line" -> "more of a linear dodge"; mock composition/library_home/shine_compare.png):
                // a wide DODGE band (brightens the surface in its own colour) + a crisp additive hot core + a thin trailing dodge
                // sliver (the double glint). All three ride one parent so they travel together.
                var rig = new GameObject("BandRig", typeof(RectTransform)); rig.transform.SetParent(mGO.transform, false);
                brt = (RectTransform)rig.transform; brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                float bw = Mathf.Max(size.x, size.y) * V2_WIDTH;
                brt.sizeDelta = new Vector2(bw, diag * 1.3f); brt.localRotation = Quaternion.Euler(0, 0, -BAND_ANGLE);
                Image Piece(string n, Sprite sp, Material m, Color c, float wMul, float xOff)
                {
                    var im = NewImg(n, rig.transform, sp, c); if (m != null) im.material = m;
                    var r = im.rectTransform; r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f); r.sizeDelta = new Vector2(bw * wMul, diag * 1.3f); r.anchoredPosition = new Vector2(xOff, 0f);
                    return im;
                }
                Piece("Dodge", BandShaped(4f), DodgeMat(), new Color(1f, 1f, 1f, V2_DODGE), 1f, 0f);
                Piece("Sliver", BandShaped(6f), DodgeMat(), new Color(1f, 1f, 1f, V2_SLIVER), 0.25f, -bw * 0.9f);
                band = Piece("Core", BandShaped(60f), null, new Color(1f, 0.97f, 0.85f, V2_CORE), 1f, 0f);   // additive (NewImg default)
                travel = size.x * 0.5f + bw * 1.6f + size.y * Mathf.Tan(BAND_ANGLE * Mathf.Deg2Rad) * 0.5f;
                brt.anchoredPosition = new Vector2(-travel, 0f);
                seq.Append(brt.DOAnchorPosX(travel, V2_DURATION).SetEase(Ease.InOutSine));
            }
            else
            {
                band = NewImg("Band", mGO.transform, Band(), new Color(1f, 0.98f, 0.9f, STRENGTH));
                brt = band.rectTransform; brt.anchorMin = brt.anchorMax = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(Mathf.Max(size.x, size.y) * BAND_WIDTH, diag * 1.3f);
                brt.localRotation = Quaternion.Euler(0, 0, -BAND_ANGLE);
                travel = size.x * 0.5f + brt.sizeDelta.x + size.y * Mathf.Tan(BAND_ANGLE * Mathf.Deg2Rad) * 0.5f;
                brt.anchoredPosition = new Vector2(-travel, 0f);
                seq.Append(brt.DOAnchorPosX(travel, DURATION).SetEase(Ease.InOutSine));
            }
            float dur = V4 && s_dodge != null ? V4_DURATION : V3 && s_screen != null ? V3_DURATION : V2 && s_dodge != null ? V2_DURATION : DURATION;
            float flashA = V4 && s_dodge != null ? 0f : FLASH;   // V4: no whole-object flash
            seq.Insert(0f, flash.DOFade(flashA, dur * 0.45f).SetEase(Ease.OutQuad));
            seq.Insert(dur * 0.45f, flash.DOFade(0f, dur * 0.55f).SetEase(Ease.InQuad));
            seq.OnComplete(() => { if (mGO != null) Object.Destroy(mGO); });
        }

        private static Image NewImg(string n, Transform parent, Sprite s, Color c)
        {
            var go = new GameObject(n, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.sprite = s; im.color = c; im.raycastTarget = false;
            var m = AddMat(); if (m != null) im.material = m;
            return im;
        }
        private static Material AddMat()
        {
            if (s_add != null) return s_add;
            // WordDrop/UIAdditive = additive WITH the UI stencil block (WordDrop/AdditiveSprite ignores masks - the glint spilled out)
            var sh = Shader.Find("WordDrop/UIAdditive"); if (sh == null) sh = Resources.Load<Shader>("Shaders/UIAdditive");
            if (sh != null) s_add = new Material(sh) { name = "ShineAdditive" };
            return s_add;
        }
        private static Material s_mask;
        private static Material MaskMat()
        {
            if (s_mask != null) return s_mask;
            var sh = Shader.Find("UI/Default"); if (sh == null) return null;
            s_mask = new Material(sh) { name = "ShineMaskAlphaClip" }; s_mask.EnableKeyword("UNITY_UI_ALPHACLIP");
            return s_mask;
        }
        /// <summary>A vertical soft band: bright core, gaussian falloff across, fading at both ends.</summary>
        private static Sprite Band()
        {
            if (s_band != null) return s_band;
            const int W = 64, H = 128; var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[W * H];
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float u = (x + 0.5f) / W * 2f - 1f, v = (y + 0.5f) / H * 2f - 1f;
                float a = Mathf.Exp(-u * u * 6f) * 0.75f + Mathf.Exp(-u * u * 40f) * 0.5f;   // soft wide glow + a crisp hot core
                a *= Mathf.Clamp01((1f - Mathf.Abs(v)) * 3f);
                px[y * W + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(a));
            }
            t.SetPixels(px); t.Apply();
            return s_band = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        }
        private static Sprite s_white;
        private static Sprite SoftWhite()
        {
            if (s_white != null) return s_white;
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, false); var px = new Color[16]; for (int i = 0; i < 16; i++) px[i] = Color.white; t.SetPixels(px); t.Apply();
            return s_white = Sprite.Create(t, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
