using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// A single "height-scale sweep" for a UI Image. A crest of vertical stretch rolls across the sprite ONE time and
    /// settles flat — each column briefly scales taller (around the image's centre line) as the crest passes, so the
    /// baked title does a rolling stretch without ever moving letters out of position. Call PlaySweep() to trigger it.
    /// 2026-07-17 Spencer (replaced the position-displacement ripple that was warping the lettering).
    /// </summary>
    [RequireComponent(typeof(Graphic))]
    [DisallowMultipleComponent]
    public class UIWaveMesh : BaseMeshEffect
    {
        [Tooltip("Peak extra height at the crest (0.30 = +30% taller where the wave is).")]
        public float heightScale = 0.30f;
        [Tooltip("Crest width as a fraction of image width — wider = gentler, more of the word rises together.")]
        public float bumpWidthFrac = 0.30f;
        [Tooltip("Seconds for the crest to travel fully across.")]
        public float sweepDuration = 0.7f;
        [Tooltip("Horizontal subdivisions.")]
        public int columns = 28;

        private bool _playing;
        private float _t;

        protected override void OnEnable() { base.OnEnable(); if (graphic != null) graphic.SetVerticesDirty(); }

        /// <summary>Kick a single height-scale wave that rolls across the image once, then rests flat.</summary>
        public void PlaySweep()
        {
            _t = 0f; _playing = true;
            if (graphic != null) graphic.SetVerticesDirty();
        }

        private void Update()
        {
            if (!_playing) return;
            _t += Time.unscaledDeltaTime;
            if (graphic != null) graphic.SetVerticesDirty();
            if (_t >= sweepDuration) _playing = false; // the SetVerticesDirty above renders the final flat state
        }

        public override void ModifyMesh(VertexHelper vh)
        {
            if (!IsActive() || vh.currentVertCount < 4) return;
            if (!_playing && _t <= 0f) return; // never swept → leave the plain quad untouched

            var src = new List<UIVertex>();
            vh.GetUIVertexStream(src);
            float cx = 0f, cy = 0f, xmin = float.MaxValue, xmax = float.MinValue;
            for (int i = 0; i < src.Count; i++)
            {
                var p = src[i].position;
                cx += p.x; cy += p.y;
                if (p.x < xmin) xmin = p.x;
                if (p.x > xmax) xmax = p.x;
            }
            cx /= src.Count; cy /= src.Count;

            UIVertex bl = src[0], tl = src[0], tr = src[0], br = src[0];
            bool hbl = false, htl = false, htr = false, hbr = false;
            for (int i = 0; i < src.Count; i++)
            {
                var v = src[i];
                bool left = v.position.x < cx, bottom = v.position.y < cy;
                if (left && bottom) { bl = v; hbl = true; }
                else if (left && !bottom) { tl = v; htl = true; }
                else if (!left && bottom) { br = v; hbr = true; }
                else { tr = v; htr = true; }
            }
            if (!(hbl && htl && htr && hbr)) return;

            float width = Mathf.Max(1f, xmax - xmin);
            float sigma = Mathf.Max(1f, bumpWidthFrac * width);
            float prog = Mathf.Clamp01(sweepDuration > 0.001f ? _t / sweepDuration : 1f);
            float bumpX = Mathf.Lerp(xmin - sigma, xmax + sigma, prog); // crest travels left → right, once

            vh.Clear();
            int cols = Mathf.Max(1, columns);
            for (int gy = 0; gy <= 1; gy++) // just bottom + top rows — enough for a vertical scale
            {
                float v = gy;
                for (int gx = 0; gx <= cols; gx++)
                {
                    float u = (float)gx / cols;
                    UIVertex vert = Bilerp(bl, br, tl, tr, u, v);
                    float d = vert.position.x - bumpX;
                    float infl = Mathf.Exp(-(d * d) / (2f * sigma * sigma));
                    float sY = 1f + heightScale * infl;
                    vert.position.y = cy + (vert.position.y - cy) * sY; // scale HEIGHT around the centre line only
                    vh.AddVert(vert);
                }
            }
            int stride = cols + 1;
            for (int gx = 0; gx < cols; gx++)
            {
                int i0 = gx, i1 = gx + 1, i2 = gx + stride, i3 = gx + 1 + stride;
                vh.AddTriangle(i0, i2, i1);
                vh.AddTriangle(i1, i2, i3);
            }
        }

        private static UIVertex Bilerp(UIVertex bl, UIVertex br, UIVertex tl, UIVertex tr, float u, float v)
        {
            UIVertex o = UIVertex.simpleVert;
            float w00 = (1f - u) * (1f - v), w10 = u * (1f - v), w01 = (1f - u) * v, w11 = u * v;
            o.position = bl.position * w00 + br.position * w10 + tl.position * w01 + tr.position * w11;
            o.uv0 = bl.uv0 * w00 + br.uv0 * w10 + tl.uv0 * w01 + tr.uv0 * w11;
            o.color = bl.color;
            return o;
        }
    }
}
