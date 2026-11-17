using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// OWL MASCOT in the HUD portrait (2026-11-01, PLACEHOLDER art - Spencer: "put this into the game as a placeholder").
    /// A cut-out puppet ported 1:1 from the approved mock WordDrop_Art/character/owl/owl_hud_mock.html: body + wings stay inside the
    /// portrait disc (masked), the whole HEAD breaks out over the gold ring and casts a soft shadow onto it. Idle breathe / bob /
    /// random blink; moods are played from game moments (OwlPortrait.Play). Art: Resources/Owl/*.png + owl_layout.json, exported
    /// from the mock rig (all positions in "head px": the mock's coordinate space; the circle shows view_w head px across).
    /// ON = false -> the old profile avatar.
    /// </summary>
    public class OwlPortrait : MonoBehaviour
    {
        public static bool ON = true;
        /// <summary>11-02 3D OWL, STEP 1 (Spencer: final owl = his Meshy model, see memory project_wordrop_owl_3d_direction): parts rendered
        /// in Blender (WordDrop_Art/character/owl/meshy/render_parts.py + export_parts.py) -> Resources/Owl3D. Step 1 = head + body only
        /// (the face is baked into the model, wings stay on the body): moods still bob / tilt / squash, but no eye, brow, beak or wing
        /// changes yet. false = the 2D placeholder rig in Resources/Owl.</summary>
        public static bool OWL_3D = true;
        public static float OWL_SCALE = 1.10f;
        public static float OWL_SHIFT_Y = 0.0855f;   // 11-03 Spencer: the WHOLE owl up (~26 of a 304 px portrait) so the unmasked head's ear tufts break out over the ring and cast their shadow on it   // 11-03 Spencer: owl 10% bigger in the portrait (ear tufts measured ~60 layout px inside the circle)
        public static float OWL_SHIFT_X = 0.034f;   // fraction of the portrait width (~10 screen px on a 1179 phone), owl inside the frame (11-02)
        private static string Dir => OWL_3D ? "Owl3D/" : "Owl/";
        public static float IDLE_SLEEP_SECS = 20f;
        private static OwlPortrait I;

        [System.Serializable] private class Layout { public float head_cx, head_cy, view_w; }
        private Layout _lay; private readonly Dictionary<string, float[]> _parts = new Dictionary<string, float[]>();
        private readonly Dictionary<string, Sprite> _spr = new Dictionary<string, Sprite>();

        private Image _photo; private RectTransform _hudRoot;
        private RectTransform _bodyScale, _headHolder, _headScale;          // scale roots (k = canvas units per head px)
        private RectTransform _body, _head, _glasses, _wingL, _wingR;       // animated groups
        private RectTransform _bodyOut, _wingLOut, _wingROut; private Image _wingLOutImg, _wingROutImg, _wingLImg, _wingRImg;   // raised wings break out of the frame
        private Image _eyeL, _eyeR, _browL, _browR, _beak;
        private Image _whiteL, _whiteR, _irisL, _irisR; private Vector2 _irisLBase, _irisRBase; private bool _pupils; private string _eyeState = "open";   // 11-01: separate whites + irises -> real eye tracking
        private Vector2 _browLBase, _browRBase, _glBase, _headBase, _eyeLBase, _eyeRBase;

        private string _mood; private float _moodT, _t, _nextBlink = 1.5f, _blinkUntil, _lastActivity;
        private bool _sleepyClosed; private float _nextZ;
        private Image _headImg; private string _browState = "neutral"; private bool _snoreOn; private Vector2 _bodyBase; private bool _lookDown3D; private float _talkUntil; private bool _talkClosed;   // 11-02: 3D mode swaps whole-head mood sprites (Owl3D/head_<mood>)
        private int _lastMoves = -1, _lastStage = -1;
        // 11-06 Spencer: expressions hard-cut. A mood change now BLINKS THROUGH (head_blink for BLINK_THRU s hides the swap) with a
        // SQUASH-AND-POP on the head (dips during the blink, overshoots after, settles by POP_SECS). false = the old hard cut.
        public static bool  MOOD_TRANSITION = true;
        public static float BLINK_THRU = 0.07f, POP_SECS = 0.30f, POP_AMT = 0.07f;
        private bool _moodChange; private Sprite _pendingHead; private float _swapAt = -1f, _popT = -1f;
        private float _lookUntil, _nextGlance = 5f, _lookAmt, _sideLook, _sideUntil;   // 11-01 'look down at the board': eased 0..1

        // ── public API ───────────────────────────────────────────────────────────────────────────────────────────────
        public static void Play(string mood) { if (I != null && ON) I.Begin(mood); }
        public static readonly string[] MOODS = { "happy", "proud", "wink", "wow", "starry", "think", "nervous", "worried", "sad", "teary", "cheer", "sleepy" };

        /// <summary>Called by HUDManager once the art HUD's photo well exists. Adds the puppet; hides the profile avatar.</summary>
        public static void Attach(RectTransform hudRoot, Image photo)
        {
            if (!ON || hudRoot == null || photo == null || I != null) return;
            var go = new GameObject("OwlPortrait", typeof(RectTransform)); go.transform.SetParent(hudRoot, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = rt.anchorMax = Vector2.zero; rt.sizeDelta = Vector2.zero;
            I = go.AddComponent<OwlPortrait>(); I.Build(hudRoot, photo);
        }

        // ── build ────────────────────────────────────────────────────────────────────────────────────────────────────
        private void Build(RectTransform hudRoot, Image photo)
        {
            _hudRoot = hudRoot; _photo = photo;
            var json = Resources.Load<TextAsset>(Dir + "owl_layout");
            if (json == null) { WDLog.Log("[Owl] Owl/owl_layout missing - owl off"); enabled = false; return; }
            _lay = JsonUtility.FromJson<Layout>(json.text);
            ParseParts(json.text);

            // the photo well becomes a mask with our own deep-blue ground; the profile avatar stands down
            var pb = photo.GetComponent<ProfileBinding>(); if (pb != null) Destroy(pb);
            foreach (Transform ch in photo.transform) ch.gameObject.SetActive(false);
            var mask = photo.GetComponent<Mask>(); if (mask == null) mask = photo.gameObject.AddComponent<Mask>();
            mask.showMaskGraphic = false;
            var bg = NewImage("OwlGround", photo.transform, Ground(), Color.white); Stretch(bg.rectTransform);
            // 11-02 Spencer: "the jaggies are mostly that orange line around the ring" - a uGUI Mask is a stencil, so its circle edge
            // is hard pixel steps. An anti-aliased dark line (the ring's own inner outline colour) drawn just above the masked owl,
            // straddling the mask edge, hides the steps. Sibling of the photo (children would be clipped by the same stencil).
            _edge = NewImage("OwlMaskEdge", photo.transform.parent, EdgeRing(), new Color32(32, 10, 84, 255)); _edge.raycastTarget = false;
            _edge.transform.SetSiblingIndex(photo.transform.GetSiblingIndex() + 1);

            // BODY (masked): wings + body
            _bodyScale = NewRT("OwlBodyScale", photo.transform);
            _body = Group("Body", _bodyScale, 612, 1900);
            if (OWL_3D) Part("head_glow", _body, 612, 1900);   // 11-02 backlight: light behind him, masked by the circle, moves with him
            _wingL = Part("wingL", _body, 612, 1900, new Vector2(0.82f, 0.9f));
            _wingR = Part("wingR", _body, 612, 1900, new Vector2(0.18f, 0.9f));
            Part("body", _body, 612, 1900);
            // 11-02 Spencer (reference portrait): the frame casts a soft shadow INTO the portrait instead of a clean stroke.
            // Measured on the reference: darkens ~45% at the frame, gone ~13% of the width in, tinted toward violet. Masked with
            // the body (sibling after it) so it shades the ground and his body; the head (unmasked, on top) stays lit.
            if (OWL_3D) { var inner = NewImage("OwlInnerShadow", photo.transform, InnerShadow(), new Color32(0x1E, 0x0E, 0x50, 0xFF)); Stretch(inner.rectTransform); }

            // HEAD (unmasked, over the ring, under the level star): sibling of the photo in the HUD root
            _headHolder = NewRT("OwlHead", hudRoot);
            _headScale = NewRT("Scale", _headHolder);
            // WINGS OUT (11-01 Spencer: "you cant see his wings when you win"): the masked wings are clipped by the circle once raised.
            // A second pair lives here, unmasked and behind the head; it fades in as the wings lift and the masked pair fades out.
            _bodyOut = Group("BodyOut", _headScale, 612, 1900);
            _wingLOut = Part("wingL", _bodyOut, 612, 1900, new Vector2(0.82f, 0.9f));
            _wingROut = Part("wingR", _bodyOut, 612, 1900, new Vector2(0.18f, 0.9f));
            _wingLOutImg = _wingLOut.GetComponent<Image>(); _wingROutImg = _wingROut.GetComponent<Image>();
            _wingLImg = _wingL.GetComponent<Image>(); _wingRImg = _wingR.GetComponent<Image>();
            _head = Group("Head", _headScale, 612, 900);
            Part("head_shadow", _head, 612, 900);
            if (OWL_3D) Part("head_chin", _head, 612, 900);   // 11-02: contact shadow under his chin onto the vest (behind the head)
            _headImg = Part("head", _head, 612, 900).GetComponent<Image>();
            _pupils = _parts.ContainsKey("eye_whiteL") && Spr("eye_whiteL") != null && Spr("eye_irisL") != null;
            if (_pupils)
            {
                _whiteL = Part("eye_whiteL", _head, 612, 900).GetComponent<Image>(); _whiteR = Part("eye_whiteR", _head, 612, 900).GetComponent<Image>();
                _irisL = Part("eye_irisL", _head, 612, 900).GetComponent<Image>(); _irisR = Part("eye_irisR", _head, 612, 900).GetComponent<Image>();
                _irisLBase = _irisL.rectTransform.anchoredPosition; _irisRBase = _irisR.rectTransform.anchoredPosition;
            }
            _eyeL = Part("eye_openL", _head, 612, 900).GetComponent<Image>();
            _eyeR = Part("eye_openR", _head, 612, 900).GetComponent<Image>();
            _browL = Part("brow_neutralL", _head, 612, 900).GetComponent<Image>();
            _browR = Part("brow_neutralR", _head, 612, 900).GetComponent<Image>();
            _glasses = Group("Glasses", _head, 612, 420, 612, 900);
            Part("glasses", _glasses, 612, 420);
            _beak = Part("beak_closed", _head, 612, 900).GetComponent<Image>();
            _headBase = _head.anchoredPosition; _glBase = _glasses.anchoredPosition; _bodyBase = _body.anchoredPosition;
            _browLBase = _browL.rectTransform.anchoredPosition; _browRBase = _browR.rectTransform.anchoredPosition;
            _eyeLBase = _eyeL.rectTransform.anchoredPosition; _eyeRBase = _eyeR.rectTransform.anchoredPosition;

            HandManager.OnBoardSwapDone += Touch;
            Neutral();
            if (OWL_3D)   // parts the 3D set doesn't have (face, glasses, wings) would draw as white boxes - switch them off
                foreach (var root in new Transform[] { _bodyScale, _headHolder })
                    foreach (var img in root.GetComponentsInChildren<Image>(true)) if (img.sprite == null) img.enabled = false;
            WDLog.Log("[Owl] attached to the HUD portrait");
        }
        private void OnDestroy() { HandManager.OnBoardSwapDone -= Touch; if (I == this) I = null; }
        private void Touch() { _lastActivity = _t; LookAtBoard(1.4f); }
        /// <summary>Glance down at the board for <paramref name="secs"/> (eyes drop in the face, head dips, glasses slide).</summary>
        public static void LookDown(float secs = 1.2f) { if (I != null) I.LookAtBoard(secs); }
        /// <summary>11-02: talk for <paramref name="secs"/> (3D heads: beak flaps between the open-smile neutral and head_talk).</summary>
        public static void Talk(float secs = 1.6f) { if (I != null) I._talkUntil = I._t + secs; }
        /// <summary>Tutorial lesson on screen: he looks down at the highlighted tiles the whole time and never dozes off.</summary>
        public static void TutorialFocus(bool on) { if (I == null) return; I._tutorial = on; if (on) { I._lastActivity = I._t; if (I._mood == "sleepy") I.End(); } else I._lookUntil = I._t + 0.2f; }
        private bool _tutorial;
        private void LookAtBoard(float secs) { _lookUntil = Mathf.Max(_lookUntil, _t + secs); }

        private void ParseParts(string text)
        {
            int i = text.IndexOf("\"parts\""); if (i < 0) return;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text.Substring(i),
                         "\"([A-Za-z_]+)\"\\s*:\\s*\\[\\s*([-0-9.eE]+)\\s*,\\s*([-0-9.eE]+)\\s*,\\s*([-0-9.eE]+)\\s*,\\s*([-0-9.eE]+)\\s*\\]"))
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                _parts[m.Groups[1].Value] = new[] { float.Parse(m.Groups[2].Value, ci), float.Parse(m.Groups[3].Value, ci),
                                                    float.Parse(m.Groups[4].Value, ci), float.Parse(m.Groups[5].Value, ci) };
            }
        }

        private Sprite Spr(string name)
        {
            if (_spr.TryGetValue(name, out var s) && s != null) return s;
            var tex = Resources.Load<Texture2D>(Dir + name);
            s = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
            _spr[name] = s; return s;
        }

        /// <summary>A group whose pivot sits at head-px (px, py); positioned inside a parent whose own pivot is (ppx, ppy)
        /// (defaults to the view centre for the scale roots).</summary>
        private RectTransform Group(string name, RectTransform parent, float px, float py, float ppx = float.NaN, float ppy = float.NaN)
        {
            if (float.IsNaN(ppx)) { ppx = _lay.head_cx; ppy = _lay.head_cy; }
            var rt = NewRT(name, parent); rt.anchoredPosition = new Vector2(px - ppx, -(py - ppy)); return rt;
        }
        /// <summary>Image for part <paramref name="key"/> inside a group whose pivot is head-px (gx, gy).</summary>
        private RectTransform Part(string key, RectTransform group, float gx, float gy, Vector2? pivot = null)
        {
            var img = NewImage(key, group, Spr(key), Color.white); var rt = img.rectTransform;
            PlaceKey(rt, key, gx, gy, pivot ?? new Vector2(0.5f, 0.5f));
            return rt;
        }
        private void PlaceKey(RectTransform rt, string key, float gx, float gy, Vector2 pivot)
        {
            if (!_parts.TryGetValue(key, out var p)) return;
            rt.pivot = pivot; rt.sizeDelta = new Vector2(p[2], p[3]);
            float px = p[0] + p[2] * pivot.x, py = p[1] + p[3] * (1f - pivot.y);
            rt.anchoredPosition = new Vector2(px - gx, -(py - gy));
        }
        private void Swap(Image img, string key, float gx = 612, float gy = 900)
        {
            var s = Spr(key); if (s == null || img == null) return;
            img.sprite = s; PlaceKey(img.rectTransform, key, gx, gy, new Vector2(0.5f, 0.5f));
        }

        // ── expressions ──────────────────────────────────────────────────────────────────────────────────────────────
        private void Face(string eyes, string brows, string beak)
        {
            if (eyes != null) _eyeState = eyes;
            if (eyes != null) { Swap(_eyeL, "eye_" + eyes + "L"); Swap(_eyeR, "eye_" + eyes + "R");
                                _eyeLBase = _eyeL.rectTransform.anchoredPosition; _eyeRBase = _eyeR.rectTransform.anchoredPosition; }
            if (brows != null) { Swap(_browL, "brow_" + brows + "L"); Swap(_browR, "brow_" + brows + "R");
                                 _browLBase = _browL.rectTransform.anchoredPosition; _browRBase = _browR.rectTransform.anchoredPosition; }
            if (beak != null) Swap(_beak, "beak_" + beak);
            if (brows != null) _browState = brows;
            if (OWL_3D) Head3D();
        }
        /// <summary>11-02: the 3D owl has no face parts - each mood is a whole head (ChatGPT edits of the neutral render, fitted
        /// by meshy/moods/mood_export.py to the same crop box as head.png). Missing sprites fall back to the neutral head.</summary>
        private void Head3D()
        {
            if (_headImg == null) return;
            string key = "head";
            bool change = _moodChange; _moodChange = false;
            if (_mood == "cheer" && Spr("head_cheer") != null) { SetHead(Spr("head_cheer"), change); return; }   // 11-02: level won
            if (_eyeState == "closed") key = _mood == "sleepy" && Spr("head_sleep") != null ? (_snoreOn && Spr("head_snore") != null ? "head_snore" : "head_sleep") : "head_blink";
            else if (_eyeState == "happy" || _eyeState == "wink") key = "head_happy";
            else if (_browState == "worried") key = "head_worried";
            else if (_mood == "wow" || _mood == "starry") key = "head_surprised";
            else if (_mood == "think" && Spr("head_think") != null) key = "head_think";   // 11-02: hmm (a swap that made no word)
            else if (_mood == null && _talkClosed && Spr("head_talk") != null) key = "head_talk";
            else if (_lookDown3D && Spr("head_look") != null) key = "head_look";   // 11-02: eyes down at the board
            var s = Spr(key); SetHead(s != null ? s : Spr("head"), change);
        }
        private void SetHead(Sprite s, bool moodChange)
        {
            if (_swapAt > 0f) { _pendingHead = s; return; }   // mid blink-through: the swap lands on the newest face
            var blink = Spr("head_blink");
            if (moodChange && MOOD_TRANSITION && blink != null && s != _headImg.sprite && s != blink && _headImg.sprite != blink)
            { _headImg.sprite = blink; _pendingHead = s; _swapAt = _t + BLINK_THRU; _popT = _t; return; }
            _headImg.sprite = s;
        }
        private void Neutral() { Face("open", "neutral", "closed"); }
        /// <summary>The level is won (the bonus blast runs after the win): the owl celebrates and nothing else interrupts him.</summary>
        private static bool Winning => SurvivalManager.AwaitingNextLevel || HandManager.BonusBlastRunning;
        private void Begin(string m)
        {
            // 11-01 Spencer: "shouldnt the owl be celebrating when you finish the level?" - the win fires at the START of the bonus
            // blast, whose word pops / blasts then replaced the cheer with happy / wow. While winning, everything becomes the cheer.
            if (Winning) { if (_mood == "cheer") return; m = "cheer"; }
            _moodChange = true; _mood = m; _moodT = _t; _blinkUntil = 0f; _sleepyClosed = false; _lastActivity = _t; _nextZ = 0f;
            switch (m)
            {
                case "happy":   Face("happy", "raised", "open"); break;
                case "proud":   Face("happy", "raised", "open"); break;
                case "wink":    Face("wink", "raised", "open"); break;
                case "wow":     Face("open", "raised", "open"); break;
                case "starry":  Face("starry", "raised", "open"); break;
                case "think":   Face("open", "determined", "closed"); break;
                case "nervous": Face("open", "worried", "closed"); break;
                case "worried": Face("open", "worried", "closed"); break;
                case "sad":     Face("sad", "worried", "closed"); break;
                case "teary":   Face("teary", "worried", "closed"); break;
                case "cheer":   Face("happy", "raised", "open"); break;
                case "sleepy":  Face("open", "neutral", "closed"); break;
                default: _mood = null; Neutral(); break;
            }
        }
        private void End() { _moodChange = true; _mood = null; Neutral(); }

        // ── win twinkle (11-02, Spencer "thats great" to the mock): a glint flares on the star in each eye of head_cheer,
        // alternating eyes, 0.35 s each, every 0.7 s. Gem-twinkle sprite; positions measured in head_cheer.png (414x283).
        private static readonly Vector2[] TWINKLE_AT = { new Vector2(139f / 414f, 143f / 283f), new Vector2(234f / 414f, 133f / 283f) };
        private Image[] _tw; private int[] _twCycle = { -1, -1 }; private Vector2[] _twOff = new Vector2[2];
        private const float TWINKLE_SPREAD = 22f / 414f;   // 11-02 Spencer: "twinkle in random spots around the pupils" - radius around each pupil (head_cheer px / width)
        private Image _edge; private const float EDGE_PAD = 1.04f;
        /// <summary>Anti-aliased ring for OwlMaskEdge: centred on the mask's circle edge (rect padded by EDGE_PAD), ~1.2% of the diameter wide.</summary>
        private static Sprite EdgeRing()
        {
            const int N = 512; var tex = new Texture2D(N, N, TextureFormat.RGBA32, false); tex.wrapMode = TextureWrapMode.Clamp;
            float rEdge = 0.5f / EDGE_PAD * N, half = 0.006f * N, c = (N - 1) * 0.5f; var px = new Color32[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float d = Mathf.Abs(Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) - rEdge);
                px[y * N + x] = new Color32(255, 255, 255, (byte)(255f * Mathf.Clamp01(half + 0.5f - d)));
            }
            tex.SetPixels32(px); tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }
        private void EyeTwinkle()
        {
            bool on = _headImg != null && _mood == "cheer" && _headImg.sprite != null && _headImg.sprite == Spr("head_cheer");
            if (_tw == null)
            {
                if (!on) return;
                var tex = Resources.Load<Texture2D>("FX/gem_twinkle"); if (tex == null || !_parts.TryGetValue("head", out var hp)) return;
                var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                _tw = new Image[4];
                for (int i = 0; i < 4; i++)   // 0,1 = glint, 2,3 = brighter copy on top (the mock's extra glow)
                {
                    var img = NewImage("Twinkle", _head, spr, Color.white); img.raycastTarget = false;
                    var v = TWINKLE_AT[i % 2]; float sz = 46f / 414f * hp[2];
                    img.rectTransform.sizeDelta = new Vector2(sz, sz);
                    img.rectTransform.anchoredPosition = new Vector2(hp[0] + v.x * hp[2] - 612f, -(hp[1] + v.y * hp[3] - 900f));
                    _tw[i] = img;
                }
            }
            _parts.TryGetValue("head", out var hp2);
            for (int e = 0; e < 2; e++)   // a new random spot around the pupil each flare
            {
                int cyc = Mathf.FloorToInt((_t - e * 0.35f) / 0.7f);
                if (cyc != _twCycle[e])
                {
                    _twCycle[e] = cyc; var r = Random.insideUnitCircle * TWINKLE_SPREAD;
                    _twOff[e] = new Vector2(r.x * hp2[2], r.y * hp2[2]);
                    var v = TWINKLE_AT[e]; var p0 = new Vector2(hp2[0] + v.x * hp2[2] - 612f, -(hp2[1] + v.y * hp2[3] - 900f));
                    _tw[e].rectTransform.anchoredPosition = _tw[e + 2].rectTransform.anchoredPosition = p0 + _twOff[e];
                }
            }
            for (int i = 0; i < 4; i++)
            {
                float u = (((_t - (i % 2) * 0.35f) % 0.7f) + 0.7f) % 0.7f / 0.35f;
                float k = on && u <= 1f ? Mathf.Sin(u * Mathf.PI) : 0f;
                _tw[i].enabled = k > 0.02f;
                _tw[i].rectTransform.localScale = Vector3.one * k;
                _tw[i].rectTransform.localRotation = Quaternion.Euler(0f, 0f, -30f * u);
                _tw[i].color = new Color(1f, 1f, 1f, i < 2 ? 1f : 0.6f * k);
            }
        }

        // ── sleep Zs: WordSlop recipe letters (cream, ink stroke) rising from the top-right of his head, drifting, growing, fading ──
        private int _zIndex;
        private void SpawnZ()
        {
            if (_headScale == null) return;
            var go = new GameObject("Z", typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(_headScale, false);
            var t = go.GetComponent<TextMeshProUGUI>(); t.text = "Z"; t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            t.fontSize = 150f; HUDManager.ApplyRecipeCount(t, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP);
            var rt = (RectTransform)go.transform; rt.sizeDelta = new Vector2(300f, 300f);
            float sx = 940f - _lay.head_cx + (_zIndex % 2) * 40f, sy = -(160f - _lay.head_cy); _zIndex++;
            rt.anchoredPosition = new Vector2(sx, sy); rt.localScale = Vector3.one * 0.5f; rt.localRotation = Quaternion.Euler(0f, 0f, 12f); t.alpha = 0f;
            StartCoroutine(FloatZ(rt, t, new Vector2(sx, sy)));
        }
        private System.Collections.IEnumerator FloatZ(RectTransform rt, TextMeshProUGUI t, Vector2 p0)
        {
            const float DUR = 2.2f; float u = 0f; float _zFade = 1f;
            while (u < DUR && rt != null)
            {
                u += Time.unscaledDeltaTime; float k = u / DUR;
                rt.anchoredPosition = p0 + new Vector2(70f * k + 18f * Mathf.Sin(k * Mathf.PI * 2f), 230f * k);
                rt.localScale = Vector3.one * Mathf.Lerp(0.5f, 1.15f, k);
                float a = k < 0.2f ? k / 0.2f : Mathf.Clamp01((1f - k) / 0.45f);
                // 11-06 recording: a Z kept floating over the HUD owl into the L1 lesson - once he wakes, live Zs fade out fast
                if (_mood != "sleepy") { _zFade = Mathf.Min(_zFade, t.alpha) - Time.unscaledDeltaTime * 5f; a = Mathf.Min(a, _zFade); if (a <= 0f) break; }
                t.alpha = a;
                yield return null;
            }
            if (rt != null) Destroy(rt.gameObject);
        }

        // ── per frame ────────────────────────────────────────────────────────────────────────────────────────────────
        private void LateUpdate()
        {
            if (_photo == null || _headHolder == null) return;
            bool show = _photo.gameObject.activeInHierarchy;
            if (_headHolder.gameObject.activeSelf != show) _headHolder.gameObject.SetActive(show);
            if (!show) return;

            // the head holder mirrors the photo well's rect (same parent), so both scale roots share one k
            var prt = _photo.rectTransform;
            _headHolder.anchorMin = prt.anchorMin; _headHolder.anchorMax = prt.anchorMax; _headHolder.pivot = prt.pivot;
            _headHolder.offsetMin = prt.offsetMin; _headHolder.offsetMax = prt.offsetMax;
            if (_edge != null)
            {
                var ert = _edge.rectTransform; ert.anchorMin = prt.anchorMin; ert.anchorMax = prt.anchorMax; ert.pivot = prt.pivot;
                var pad = new Vector2(prt.rect.width, prt.rect.height) * (EDGE_PAD - 1f) * 0.5f;
                ert.offsetMin = prt.offsetMin - pad; ert.offsetMax = prt.offsetMax + pad;
                if (_edge.gameObject.activeSelf != show) _edge.gameObject.SetActive(show);
            }
            float k = prt.rect.width / Mathf.Max(1f, _lay.view_w) * OWL_SCALE;
            _bodyScale.localScale = _headScale.localScale = new Vector3(k, k, 1f);
            // 11-02 Spencer ("scoot the owl over a little right ... inside the frame"): his face sat ~20 HUD px left of the frame
            // centre. v2: 12 canvas units came out ~21 screen px ("too much") - now a fraction of the portrait width, ~10 px.
            // head_shadow.png is clipped around this shifted centre (OWL_SHIFT_X x view_w layout px).
            _bodyScale.anchoredPosition = _headScale.anchoredPosition = new Vector2(OWL_SHIFT_X * prt.rect.width, OWL_SHIFT_Y * prt.rect.width);

            float dt = Time.unscaledDeltaTime; _t += dt;
            WatchGame();

            float bodyS = 1f + 0.018f * Mathf.Sin(_t * 2.6f), headY = 4f * Mathf.Sin(_t * 2.6f + 0.6f), headR = 1.2f * Mathf.Sin(_t * 1.3f), headS = 1f;
            float glY = 0f, browY = 0f, wing = 0f, shake = 0f, eyeS = 1f, look = 0f, lookY = 0f;
            if (_mood == null && _t > _nextGlance)
            {   // idle: mostly glance down at the board, sometimes a side glance (pupils only)
                _nextGlance = _t + 4f + Random.value * 4f;
                if (_pupils && Random.value < 0.35f) { _sideLook = (Random.value < 0.5f ? -1f : 1f) * 14f; _sideUntil = _t + 0.9f + Random.value * 0.6f; }
                else LookAtBoard(1.1f + Random.value * 0.6f);
            }
            if (_mood == null && _t < _sideUntil) look = _sideLook;
            if (_tutorial) { _lookUntil = _t + 0.3f; _lastActivity = _t; }
            bool looking = _t < _lookUntil && (_mood == null || _mood == "happy" || _mood == "think");
            _lookAmt = Mathf.MoveTowards(_lookAmt, looking ? 1f : 0f, Time.unscaledDeltaTime * 5f);
            float la = Mathf.SmoothStep(0f, 1f, _lookAmt);
            if (OWL_3D) { bool ld = _lookAmt > 0.5f && _eyeState == "open" && (_mood == null || _mood == "think"); if (ld != _lookDown3D) { _lookDown3D = ld; Head3D(); } }
            {   // talking: the beak flaps ~6x a second while _talkUntil runs (no mood, eyes open)
                bool tc = _mood == null && _t < _talkUntil && Mathf.Sin(_t * 38f) > 0f;
                if (tc != _talkClosed) { _talkClosed = tc; if (OWL_3D) Head3D(); }
            }
            if (_mood == null)
            {
                if (_t > _nextBlink) { _blinkUntil = _t + 0.13f; _nextBlink = _t + 2.2f + Random.value * 3.2f; Face("closed", null, null); }
                if (_blinkUntil > 0f && _t > _blinkUntil) { _blinkUntil = 0f; Face("open", null, null); }
            }
            else
            {
                float u = _t - _moodT;
                switch (_mood)
                {
                    case "happy": headY += -38f * Mathf.Sin(Mathf.Min(u / 0.45f, 1f) * Mathf.PI); headS = 1f + 0.05f * Mathf.Sin(Mathf.Min(u / 0.3f, 1f) * Mathf.PI);
                        browY = -14f * Mathf.Max(0f, 1f - u / 0.9f); if (u > 1.3f) End(); break;
                    case "wow": { float pop = Mathf.Min(u / 0.12f, 1f); eyeS = 1f + 0.07f * pop * Mathf.Max(0f, 1f - (u - 0.9f) / 0.4f);
                        headY += -20f * Mathf.Exp(-u * 4f) * Mathf.Cos(u * 16f); browY = -34f * pop; glY = -46f * Mathf.Exp(-u * 5f) * Mathf.Cos(u * 18f);
                        wing = 60f * Mathf.Exp(-u * 3f) * Mathf.Abs(Mathf.Sin(u * 14f)); if (u > 1.6f) End(); break; }
                    case "worried": shake = 3f * Mathf.Sin(u * 40f) * Mathf.Max(0f, 1f - u / 1.2f); headY += 6f; browY = -6f; if (u > 2.0f) End(); break;
                    case "sad": { float d = Mathf.Min(u / 0.6f, 1f); headY += 34f * d; headR += -7f * d; bodyS = 1f - 0.02f * d; browY = 8f * d; if (u > 2.6f) End(); break; }
                    case "cheer": { float up = Mathf.Min(u / 0.25f, 1f); wing = 140f * up + 12f * Mathf.Sin(u * 14f);
                        float b = Mathf.Abs(Mathf.Sin(u * Mathf.PI * 2.4f)); headY += -30f * b; headS = 1f + 0.03f * b; browY = -12f; if (u > 2.4f && !Winning) End(); break; }   // keeps cheering through the bonus
                    case "starry": { float pop = Mathf.Min(u / 0.15f, 1f); headY += -26f * Mathf.Exp(-u * 3f) * Mathf.Abs(Mathf.Cos(u * 10f));
                        headS = 1f + 0.05f * pop * Mathf.Max(0f, 1f - (u - 1.4f) / 0.6f); browY = -18f * pop; wing = 90f * pop + 10f * Mathf.Sin(u * 18f); if (u > 2.4f) End(); break; }
                    case "teary": { float d = Mathf.Min(u / 0.7f, 1f); headY += 30f * d + 2f * Mathf.Sin(u * 9f); headR += -5f * d; bodyS = 1f - 0.02f * d; browY = 6f * d; if (u > 3.0f) End(); break; }
                    case "wink": headR += 8f * Mathf.Sin(Mathf.Min(u / 0.25f, 1f) * Mathf.PI / 2f) * Mathf.Max(0f, 1f - (u - 0.9f) / 0.3f);
                        headY += -8f * Mathf.Sin(Mathf.Min(u / 0.3f, 1f) * Mathf.PI); if (u > 1.3f) End(); break;
                    case "nervous": shake = 2.5f * Mathf.Sin(u * 45f); headY += 8f; browY = -4f; look = Mathf.Sin(u * 7f) > 0f ? -12f : 12f; if (u > 2.2f) End(); break;
                    case "sleepy": { float d = Mathf.Min(u / 1.2f, 1f); headY += 26f * d + 6f * Mathf.Sin(u * 1.6f); headR += -6f * d;
                        if (u > 0.6f && !_sleepyClosed) { _sleepyClosed = true; Face("closed", null, "closed"); }
                        // 11-01 Spencer: "ZZZ above his head" - asleep until someone plays (a swap / any other moment wakes him)
                        if (u > 0.9f && _t > _nextZ) { _nextZ = _t + 0.85f; SpawnZ(); }
                        // 11-02 Spencer: "one of him more snoring" - 3D heads breathe between asleep and snoring (beak open)
                        { bool sn = _sleepyClosed && Mathf.Sin(u * 2.4f) > 0.25f; if (sn != _snoreOn) { _snoreOn = sn; if (OWL_3D) Head3D(); } }
                        if (_lastActivity > _moodT + 0.01f) End(); break; }
                    case "proud": { float d = Mathf.Min(u / 0.4f, 1f); bodyS = 1f + 0.05f * d;
                        headY += -10f * d + 6f * Mathf.Sin(Mathf.Max(0f, u - 0.4f) * 5f) * Mathf.Max(0f, 1f - (u - 0.4f) / 1.2f); browY = -10f; if (u > 2.0f) End(); break; }
                    case "think": headR += -6f * Mathf.Sin(Mathf.Min(u / 0.4f, 1f) * Mathf.PI / 2f); browY = 4f; look = u < 1.0f ? 0f : (u < 1.8f ? -14f : 14f); if (u > 2.8f) End(); break;
                }
            }

            // looking down at the board: eyes drop in the face, head dips + leans forward (slight squash), glasses slide down the beak
            if (_pupils) { lookY += 26f * la; headY += 6f * la; glY += 4f * la; browY += 3f * la; }
            else         { lookY += 16f * la; headY += 12f * la; glY += 8f * la; browY += 5f * la; }
            // 11-02 Spencer: "when he laughs you can see the cuts on the slice" - the 3D head is a slice of one render, so big
            // cartoon hops/tilts tear it off the body. Keep the 2D rig's timing, at a fraction of the travel.
            // 11-02 (2nd pass, "a separate head moving behind it"): the 3D owl moves as ONE piece - head and body get the same
            // translation, no tilt/squash (they pivot from different points, so any rotation or scale splits them apart).
            if (OWL_3D) { headY *= 0.6f; headR = 0f; headS = 1f; bodyS = 1f; }
            // mock (CSS: +y down, +deg clockwise) -> uGUI (+y up, +z counter-clockwise)
            _body.localScale = new Vector3(1f / bodyS + (bodyS - 1f) * 0.3f, bodyS, 1f);
            _head.anchoredPosition = _headBase + new Vector2(shake, -headY);
            _head.localRotation = Quaternion.Euler(0f, 0f, -headR);
            _head.localScale = new Vector3(headS * (1f + 0.012f * la), headS * (1f - 0.03f * la), 1f);
            if (OWL_3D) EyeTwinkle();
            if (_swapAt > 0f && _t >= _swapAt) { _swapAt = -1f; if (_headImg != null && _pendingHead != null) _headImg.sprite = _pendingHead; }
            float moodPop = 0f;   // squash (<0) during the blink, overshoot (>0) after, 0 by POP_SECS
            if (_popT >= 0f) { float u = (_t - _popT) / POP_SECS; if (u >= 1f) _popT = -1f; else moodPop = -POP_AMT * Mathf.Sin(u * Mathf.PI * 2f) * (1f - u); }
            if (OWL_3D) { _head.localScale = new Vector3(1f - 0.6f * moodPop, 1f + moodPop, 1f); _body.anchoredPosition = _bodyBase + new Vector2(shake, -headY); }
            _glasses.anchoredPosition = _glBase + new Vector2(0f, -glY);
            _browL.rectTransform.anchoredPosition = _browLBase + new Vector2(0f, -browY);
            _browR.rectTransform.anchoredPosition = _browRBase + new Vector2(0f, -browY);
            // 11-01 Spencer: the white+pupil eyes look "a bit creepy" at rest -> REST = the original painted eyes; the white+pupil
            // build only swaps in while he is actually looking somewhere (board glance, side glance, think / nervous darts).
            bool usePupils = _pupils && _eyeState == "open" && (la > 0.02f || Mathf.Abs(look) > 0.5f);
            if (_pupils)
            {
                _whiteL.enabled = _whiteR.enabled = _irisL.enabled = _irisR.enabled = usePupils;
                _eyeL.enabled = _eyeR.enabled = !usePupils;
            }
            if (usePupils)
            {   // pupils glance inside the whites (clamped so the iris never leaves the eye)
                var off = Vector2.ClampMagnitude(new Vector2(look * 1.6f, -lookY), 34f);
                _irisL.rectTransform.anchoredPosition = _irisLBase + off; _irisR.rectTransform.anchoredPosition = _irisRBase + off;
                _whiteL.rectTransform.localScale = _whiteR.rectTransform.localScale = _irisL.rectTransform.localScale = _irisR.rectTransform.localScale = new Vector3(eyeS, eyeS, 1f);
                _eyeL.rectTransform.anchoredPosition = _eyeLBase; _eyeR.rectTransform.anchoredPosition = _eyeRBase;
            }
            else
            {
                _eyeL.rectTransform.anchoredPosition = _eyeLBase + new Vector2(look, -lookY);
                _eyeR.rectTransform.anchoredPosition = _eyeRBase + new Vector2(look, -lookY);
            }
            _eyeL.rectTransform.localScale = _eyeR.rectTransform.localScale = new Vector3(eyeS, eyeS, 1f);
            _wingL.localRotation = _wingLOut.localRotation = Quaternion.Euler(0f, 0f, -wing);
            _wingR.localRotation = _wingROut.localRotation = Quaternion.Euler(0f, 0f, wing);
            _bodyOut.localScale = _body.localScale;
            float outA = Mathf.Clamp01((wing - 25f) / 35f);   // raised -> drawn over the frame; resting -> the clipped pair
            var cOut = new Color(1f, 1f, 1f, outA); var cIn = new Color(1f, 1f, 1f, 1f - outA);
            _wingLOutImg.color = _wingROutImg.color = cOut; _wingLImg.color = _wingRImg.color = cIn;
        }

        /// <summary>Moments the owl reads himself: moves running low, and dozing off when nobody plays.</summary>
        private void WatchGame()
        {
            var sm = SurvivalManager.Instance;
            if (sm == null || !SurvivalManager.IsSurvivalMode) return;
            int stage = sm.CurrentStageIndex, moves = sm.CurrentStageMovesRemaining;
            if (stage != _lastStage) { _lastStage = stage; _lastMoves = moves; _lastActivity = _t; return; }
            if (moves != _lastMoves)
            {
                if (moves < _lastMoves && !SurvivalManager.AwaitingNextLevel)
                {
                    if (moves == 3 || moves == 1) Begin("nervous");
                    else if (moves == 0) Begin("sad");
                }
                _lastMoves = moves; _lastActivity = _t;
            }
            if (_mood == null && _t - _lastActivity > IDLE_SLEEP_SECS && !SurvivalManager.AwaitingNextLevel) Begin("sleepy");
        }

        // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────
        private static RectTransform NewRT(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform)); go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform; rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f); rt.sizeDelta = Vector2.zero; return rt;
        }
        private static Image NewImage(string name, Transform parent, Sprite s, Color c)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
            var im = go.GetComponent<Image>(); im.sprite = s; im.color = c; im.raycastTarget = false; return im;
        }
        private static void Stretch(RectTransform rt) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }
        private static Sprite s_ground, s_inner;
        private const float INNER_SHADOW_A = 0.90f, INNER_SHADOW_W = 0.22f;   // 11-03 Spencer: RK portrait darkens all the way to the rim's inner stroke (was .45 / .20 - left a pale teal band inside the ring)   // 11-02 v2: Spencer "dont like the thick stroke"   // alpha at the frame, width as a share of the diameter
        private static Sprite InnerShadow()
        {
            if (s_inner != null) return s_inner;
            const int N = 256; var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N]; float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float r = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / N;   // 0.5 = the circle edge
                float u = Mathf.Clamp01((0.5f - r) / INNER_SHADOW_W);               // 0 at the frame -> 1 inside
                // v2: the reference is a straight LINEAR ramp from the frame (R 52->86, G 40->115 over ~15% of the width) - no flat dark band.
                // The old 1-smoothstep held near full strength for the first few px, which read as a thick stroke.
                float a = INNER_SHADOW_A * Mathf.Pow(1f - u, 1.15f) * (r <= 0.5f + 1f / N ? 1f : 0f);
                px[y * N + x] = new Color32(255, 255, 255, (byte)(255f * a));
            }
            t.SetPixels32(px); t.Apply(false, true);
            return s_inner = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }
        /// <summary>The portrait's deep-blue ground (mock: radial #6b8ee0 -> #4a63c0 -> #36389a).</summary>
        private static Sprite Ground()
        {
            if (s_ground != null) return s_ground;
            const int N = 128; var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            // 11-01 Spencer picked "soft cream" (FFF6DE -> EED9B0 -> B99B72). 11-02 3D owl: teal backlit gradient (moods/backlight_mock.png
            // option 3 - mint behind his head, deep teal at the rim; the warm owl is the complement)
            Color a = new Color32(0xFF, 0xF6, 0xDE, 0xFF), b = new Color32(0xEE, 0xD9, 0xB0, 0xFF), c = new Color32(0xB9, 0x9B, 0x72, 0xFF);
            if (OWL_3D) { a = new Color32(0xC8, 0xFF, 0xF6, 0xFF); b = new Color32(0x4F, 0xC0, 0xC4, 0xFF); c = new Color32(0x1A, 0x5E, 0x7A, 0xFF); }
            var px = new Color[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float dx = (x + 0.5f) / N - 0.5f, dy = (y + 0.5f) / N - (OWL_3D ? 0.675f : 0.65f), d = Mathf.Sqrt(dx * dx + dy * dy) / (OWL_3D ? 0.60f : 0.62f);
                float s1 = OWL_3D ? 0.55f : 0.6f;
                px[y * N + x] = d < s1 ? Color.Lerp(a, b, d / s1) : Color.Lerp(b, c, Mathf.Clamp01((d - s1) / (1f - s1)));
            }
            t.SetPixels(px); t.Apply();
            return s_ground = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
