using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Path A (2026-05-28) — bottom action row, pixel-perfect port of
    /// Spencer's PSD design (canvas 1179×2556, iPhone 16/15 Pro reference).
    ///
    /// Display order (left to right):
    ///   1. Stone Splitter  — RockCrusher  (BoosterManager)
    ///   2. Comet           — BrambleSweep (BoosterManager)
    ///   3. Bloom Bomb      — Bloomburst   (BoosterManager)
    ///   4. Jester Hat      — Wispwhirl    (BoosterManager)
    ///   5. Settings        — opens settings modal (TODO wiring)
    ///
    /// Behind the buttons sits a "bench" panel (W=1222 px, bleeds past
    /// canvas edges) — a single backdrop that visually groups all five
    /// buttons into one bottom-of-screen module.
    ///
    /// Edit + Tile Bag are NOT in this row per Spencer's final design —
    /// edit charges show in the top HUD, Tile Bag is shelved for v1.
    ///
    /// Slot canvas (sortingOrder 50) — bench + buttons.
    /// Aim canvas (sortingOrder 60) — overlay when any targeted booster is armed.
    /// </summary>
    public class BoosterHUDSlot : MonoBehaviour
    {
        public static BoosterHUDSlot Instance { get; private set; }

        private const int SLOT_CANVAS_ORDER = 50;
        private const int AIM_CANVAS_ORDER  = 60;

        // ── PSD-derived layout constants (canvas 1179×2556) ─────────────────────
        // All values in PSD pixels; CanvasScaler with matching reference scales
        // them to whatever device the game is running on.
        //
        // PSD coords are top-left origin. Unity Canvas uses bottom-left origin
        // with anchor at (0.5, 0) so X is from center, Y is from bottom.
        //   anchoredX = (X_psd + W/2) - (CANVAS_W / 2)
        //   anchoredY = CANVAS_H - (Y_psd + H/2)   for pivot (0.5, 0.5)
        //   anchoredY = CANVAS_H - (Y_psd + H)     for pivot (0.5, 0)
        // ───────────────────────────────────────────────────────────────────────
        private const float CANVAS_W = 1179f;
        private const float CANVAS_H = 2556f;

        // 2026-05-28: shrunk 204.16 → 135 PSD so boosters are subordinate to
        // the 150 PSD board tiles (ratio 0.90×, AAA-typical Royal Match /
        // Candy Crush proportions). Row expanded from 5 slots → 7 (Edit + Bag
        // moved into the bottom bar now that boosters are smaller).
        //   7 × 135 + 6 × 26.34 = 1103.04 row span
        //   (1179 - 1103.04) / 2 = 37.98 left margin
        // 2026-09-11: 135 -> 198. MEASURED off Royal Match's bottom HUD (Spencer's reference):
        // its booster buttons are 16.8% of screen width with 2.9% gaps, i.e. a 19.7% pitch.
        // On this 1179px design width that is a 198px button and a 34px gap — WordDrop's were
        // 135px, about 47% too small, which is why the icons read tiny.
        private const float SLOT_SIZE   = 198f;
        private const float MK_BOOSTER_SIZE = 176f, MK_SETTINGS_SIZE = 150f;
        private const float MK_BOOSTER_X0 = 370f, MK_BOOSTER_STEP = 227.7f;
        // 2026-10-29 (Spencer: "the boosters need to be resized at the bottom so that it can fit" - the Axe made 5): the boosters
        // fill the span between the Settings button and the right margin, sized by how many there are. 4 boosters keep the
        // mockup's 176 size and 51.7 gap ratio; 5 shrink to fit (~147). Right edge = the mockup's (1053 + 88).
        private const float MK_ROW_LEFT = 232f, MK_ROW_RIGHT = 1141f, MK_GAP_RATIO = 51.7f / 176f;
        private static int MkBoosterCount() { int n = 0; foreach (var sp in DISPLAY_ORDER) if (sp.Type == SlotType.Booster) n++; return Mathf.Max(1, n); }
        // RK PROPORTIONS (2026-10-29, Spencer: "should we make the booster buttons a bit bigger?" - measured on his Royal Kingdom
        // level GIF, fractions of screen width): booster 13.75%, settings 10.3% (smaller = secondary), ~1% gaps, whole row 83%
        // centred. Ours were 12.5% / 12.7% / 3.7% / 94%. Used with the round art; Settings stays on the LEFT (Spencer's mockup).
        public static float RK_ROW_W = 0.83f, RK_BOOSTER = 0.1375f, RK_SETTINGS = 0.103f, RK_ROW_Y = 2390f;
        private static bool RkRow => UseRoundArt;
        // RK ROW v2 (2026-11-02, Spencer + his Royal Kingdom level screenshots: "our boosters should be as big as these ... and layed out
        // the same way"). MEASURED on ref_rk_boosters.jpg (587 px wide screen), fractions of screen width: booster 15.5% (ours 13.8%),
        // pitch 16.95% (ours 14.5%), first booster centre 9.1%, SETTINGS on the RIGHT at 92.5%, 12.5% across. Row ~97% of the width.
        // Vertical unchanged (RK_ROW_Y; both sit 93% down). false = the v1 RK row above (settings left, 83% row).
        public static bool RK2_LAYOUT = true;
        public static float RK2_BOOSTER = 0.155f, RK2_PITCH = 0.1695f, RK2_FIRST_X = 0.091f, RK2_SETTINGS = 0.125f, RK2_SETTINGS_X = 0.925f;
        private static float RkLeft => (CANVAS_W - RK_ROW_W * CANVAS_W) * 0.5f;
        private static float RkGap { get { int n = MkBoosterCount(); return Mathf.Max(0f, (RK_ROW_W * CANVAS_W - RK_SETTINGS * CANVAS_W - n * RK_BOOSTER * CANVAS_W) / n); } }
        private static float MkBoosterSize() { if (RkRow && RK2_LAYOUT) return RK2_BOOSTER * CANVAS_W; if (RkRow) return RK_BOOSTER * CANVAS_W; int n = MkBoosterCount(); return Mathf.Min(MK_BOOSTER_SIZE, (MK_ROW_RIGHT - MK_ROW_LEFT) / (n + (n - 1) * MK_GAP_RATIO)); }
        private static float MkBoosterCentre(int idx)
        {
            if (RkRow && RK2_LAYOUT) return (RK2_FIRST_X + idx * RK2_PITCH) * CANVAS_W;
            if (RkRow) { float b = RK_BOOSTER * CANVAS_W; return RkLeft + RK_SETTINGS * CANVAS_W + RkGap + idx * (b + RkGap) + b * 0.5f; }
            int n = MkBoosterCount(); float size = MkBoosterSize(), step = size * (1f + MK_GAP_RATIO);
            if (n <= 4) return MK_BOOSTER_X0 + idx * MK_BOOSTER_STEP;           // the original mockup positions
            return MK_ROW_RIGHT - size * 0.5f - (n - 1 - idx) * step;            // right-aligned, packed to fit
        }
        private const float SLOT_STEP   = 135f + 26.34f;   // 161.34
        private const float SLOT_LEFT_X = 37.98f;
        private const float SLOT_TOP_Y  = 2270.31f;       // unchanged — keeps the "pops above bench" silhouette

        // Bench panel: W=1222, H=246, X=-16, Y=2322 (bleeds past canvas edges).
        private const float BENCH_W = 1222f;
        private const float BENCH_H = 246f;
        private const float BENCH_X = -16f;
        private const float BENCH_Y = 2322f;

        // Charge badges sized + positioned proportionally to the smaller
        // button. PSD originals were 77.49 size / (+86.36, -63.36) offset
        // tuned to 204.16-px buttons; scale factor = 135/204.16 ≈ 0.6612.
        private const float BADGE_SIZE     = 77.49f * (135f / 204.16f); // ≈ 51.24
        private const float BADGE_OFFSET_X = 86.36f * (135f / 204.16f); // ≈ 57.11
        private const float BADGE_OFFSET_Y = -63.36f * (135f / 204.16f); // ≈ -41.89

        // Procedural sprites built once, reused across all slots/badges.
        private static Sprite _circleSpriteBig;   // for slot buttons
        private static Sprite _circleSpriteSmall; // for charge badges
        private static Sprite _benchSprite;       // for the bottom bench

        private static Sprite GetCircleSpriteBig()
        {
            if (_circleSpriteBig == null)
                _circleSpriteBig = TileRenderer.CreateSolidRoundedRect(256, 256, 128, Color.white);
            return _circleSpriteBig;
        }

        private static Sprite GetCircleSpriteSmall()
        {
            if (_circleSpriteSmall == null)
                _circleSpriteSmall = TileRenderer.CreateSolidRoundedRect(64, 64, 32, Color.white);
            return _circleSpriteSmall;
        }

        private static Sprite GetBenchSprite()
        {
            if (_benchSprite == null)
            {
                // Placeholder rounded-rect — real garden-themed art will replace
                // this later. Radius ~ height/4 for a soft pill look.
                int texW = 1024;
                int texH = Mathf.RoundToInt(texW * (BENCH_H / BENCH_W));
                _benchSprite = TileRenderer.CreateSolidRoundedRect(
                    texW, texH, 0, Color.white); // 2026-06-24 Spencer: square corners on the bottom bench (was rounded)
            }
            return _benchSprite;
        }

        // Soft radial glow + additive material for the EDITS pip restore burst (fake bloom over the
        // dark navy panel). Glow textures import as Default → load Texture2D + Sprite.Create. 2026-06-24.
        private static Sprite _pipGlowSprite; private static bool _pipGlowTried;
        private Sprite LoadGlowSprite()
        {
            if (_pipGlowTried) return _pipGlowSprite;
            _pipGlowTried = true;
            _pipGlowSprite = Resources.Load<Sprite>("Particles/glow")
                          ?? Resources.Load<Sprite>("Particles/soft_circle");
            if (_pipGlowSprite != null) return _pipGlowSprite;
            Texture2D tex = Resources.Load<Texture2D>("Particles/glow")
                         ?? Resources.Load<Texture2D>("Particles/soft_circle")
                         ?? Resources.Load<Texture2D>("Particles/vfx_glow");
            if (tex != null)
                _pipGlowSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _pipGlowSprite;
        }

        private static Material _pipAddMat;
        private Material LoadAdditiveGlowMaterial()
        {
            if (_pipAddMat != null) return _pipAddMat;
            Shader s = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            if (s != null) _pipAddMat = new Material(s);
            return _pipAddMat;
        }

        private static Sprite _lockSprite;
        private static Sprite GetLockSprite()
        {
            if (_lockSprite == null)
            {
                _lockSprite = Resources.Load<Sprite>("Tiles/icon_lock_02");
                if (_lockSprite == null) // PNG imported as a plain Texture (default) → build a Sprite
                {
                    var tex = Resources.Load<Texture2D>("Tiles/icon_lock_02");
                    if (tex != null)
                        _lockSprite = Sprite.Create(
                            tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
            }
            return _lockSprite;
        }

        private enum SlotType { Booster, Edit, TileBag, Settings }

        /// <summary>Onboarding lock: true while this tool is gated for early levels. Settings is
        /// never locked. 2026-06-09.</summary>
        private static bool IsSlotLocked(SlotSpec spec)
        {
            switch (spec.Type)
            {
                case SlotType.Edit:    return TutorialLocks.EditLocked;
                case SlotType.TileBag: return TutorialLocks.BagLocked;
                case SlotType.Booster: return BoosterOrdinal(spec.BoosterId) >= TutorialLocks.BoosterSlotsOpen();   // one slot at a time (2026-10-23)
                default:               return false; // Settings always available
            }
        }

        // ── ROUND BLUE SLOT ART (2026-10-29, Spencer picked round blue: "the top panel is going to be purple") ──
        // Photoshop v3 recipe as a circle (~/Documents/WordDrop_Art/vector_buttons/photoshop/build_booster.sh + booster/
        // make_slot_sprites.py). Every sprite shares one padded canvas: ART_W x ART_H, the gold rim's centre at (ART_CX, ART_CY)
        // and ART_D across (sprite px, y down). The slot rect = the rim; the art child is placed so the rim fills it.
        // States: ready / armed (brighter + baked gold glow, scale 1.06) / locked (desaturated). Badge = the round GREEN build.
        // false = the old squircle + flat badge.
        public static bool ROUND_ART = true;
        public static bool SETTINGS_RIMLESS = true; public static float SETTINGS_GEAR_FILL = 0.66f;   /* 11-16 ivory gear (flat, fills its canvas more than the old tilted one): .95 filled the disc edge to edge (Spencer "too big") */   // 0.66 x 640/512: the white gear sprite carries its recipe shadow on a padded 640 canvas (10-29)   // 2026-10-29 rimless purple disc + cream 3D gear
        private const float ART_W = 498f, ART_H = 534f, ART_CX = 248.97f, ART_CY = 247.15f, ART_D = 294f, ART_FACE_CY = 244.2f;
        public static float ART_LOCK_FILL = 0.58f;
        public static float ART_LOCK_CORNER = 0.46f;   // locked v2: padlock as a corner badge (10-29)
        // 11-16 (Spencer, RM screenshot: "the lock is centered on the button until the booster is unlocked"): a locked slot shows ONLY a
        // big gold padlock in the middle of the face - no faded booster icon. The icon appears for the first time at the unlock.
        // false = locked v2 (ghost icon + corner badge).
        public static bool  LOCK_CENTRED = true;
        public static float LOCK_CENTRED_SIZE = 0.60f;   // padlock size vs the slot
        // 11-02 (Spencer: "the lock badge should be positioned in the same place as well as the unlock"): the padlock - locked slot AND the
        // unlock moment, both read these - sits on the count badge's spot. Was (0.20, -0.18).
        public static float ART_LOCK_CX => ART_BADGE_POS_X;
        public static float ART_LOCK_CY => -ART_BADGE_POS_Y;
        /// <summary>Padlock art: the edged copy (Boosters/lock_*_gold_edge.png, 288 canvas = 256 + 16 pad, edge "B", 2026-10-29
        /// Spencer: "should we put this recipe on the locks too?") when present - mul = its box vs the 256 art.</summary>
        public static bool LOCK_SEPARATION = true;
        private static Sprite LockArt(bool open, out float mul)
        {
            string n = open ? "lock_open_gold" : "lock_closed_gold";
            // 10-29 Spencer: "separation between the gold lock and the gold rim so you can tell its above the rim" - the badge copy
            // adds a dark separation line + a contact shadow cast on the rim (composition/make_lock_badge.py), 352 canvas
            var bd = LOCK_SEPARATION ? LoadBoostersSprite(n + "_badge") : null;
            if (bd != null) { mul = 352f / 256f; return bd; }
            var e = LoadBoostersSprite(n + "_edge");
            mul = e != null ? 288f / 256f : 1f;
            return e != null ? e : LoadBoostersSprite(n);
        }
        private const float LOCK_ART_LOW = (133.5f - 128f) / 256f;   // the closed padlock's outline sits 5.5/256 low in its image; the open one shares the body position
        public static float ART_ICON_FILL = 0.70f, ART_BADGE_FRAC = 0.40f, ART_BADGE_POS = 0.31f, ART_ARMED_SCALE = 1.06f;
        // BADGE CLEAR OF THE ART (2026-11-02, Spencer: "the bottom badge it cutting off the art"). MEASURED on his RK screenshots: badge
        // centre +0.34 right / +0.40 down of the button (it hangs ~1/3 below it), and RK icons sit up-left, off that corner. Was .31/.31,
        // icons centred. Fractions of the slot size; the icon's shadow moves with it.
        public static float ART_BADGE_POS_X = 0.34f, ART_BADGE_POS_Y = 0.40f;
        // 11-02 v2 (Spencer: "some of the icons are off center now"): the uniform up-left shift pushed the mallet/axe off centre. MEASURED
        // per icon (badge-circle overlap of its alpha at the new badge spot): glove/mallet/bolt 0% at no shift; axe + spyglass 2% -> a
        // small straight-UP shift clears them (<0.5%). Fractions of the slot size, (x right, y UP).
        private static readonly Dictionary<string, Vector2> ART_ICON_SHIFT = new Dictionary<string, Vector2>
        {
            { "1axe", new Vector2(0f, 0.030f) },
            { "2spyglass", new Vector2(0f, 0.025f) },
        };
        private static Vector2 IconShift(string boosterId) { var f = BoosterIconFile(boosterId); return f != null && ART_ICON_SHIFT.TryGetValue(f, out var v) ? v : Vector2.zero; }
        public static float BADGE_OUTLINE = 0.12f, BADGE_TEXT_MARGIN = 0.17f;   // 11-02: was outline 0.25 (closed the 9's counter), no margin
        private static Sprite ButtonArt(string n) => Resources.Load<Sprite>("UI/Buttons/" + n);
        /// <summary>11-17: the settings gear's slot (SettingsV2 stacks its flyout above it). Null before the row is built.</summary>
        public RectTransform SettingsSlotRT
        {
            get
            {
                if (_slotButtons == null) return null;
                for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
                    if (DISPLAY_ORDER[i].Type == SlotType.Settings && _slotButtons[i] != null) return _slotButtons[i].transform as RectTransform;
                return null;
            }
        }
        // 11-16 LOOK PASS (Spencer, back after a week: "doesnt seem bright and inviting"; picked mock B): locked slots wore a dark
        // grey face - three of the darkest shapes on screen. Now they keep the blue READY face (RM: locked boosters stay in colour
        // with a gold padlock) and the ghost icon is tinted blue. false = the grey locked art.
        public static bool  LOCKED_IN_COLOUR = true;
        public static Color LOCKED_GHOST_TINT = new Color(0.62f, 0.78f, 1f, 1f);
        private static Sprite LockedSlotArt() => ButtonArt(LOCKED_IN_COLOUR ? "booster_slot_ready" : "booster_slot_locked");
        private static bool UseRoundArt => ROUND_ART && HUDManager.MOCKUP_HUD && ButtonArt("booster_slot_ready") != null;
        private Image[] _slotArt;
        /// <summary>Places a padded-canvas art image so its rim fills a rect of size d centred on the parent.</summary>
        private static void PlaceArt(RectTransform rt, float d)
        {
            float s = d / ART_D;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(ART_W * s, ART_H * s);
            rt.anchoredPosition = new Vector2((ART_W * 0.5f - ART_CX) * s, -(ART_H * 0.5f - ART_CY) * s);
        }

        // ── BOOSTER ICONS + UNLOCK MOMENT (2026-10-23) ──
        public static float BOOSTER_ICON_FILL = 0.72f;
        private static readonly Dictionary<string, Sprite> s_boosterIcons = new Dictionary<string, Sprite>();
        private static string BoosterIconFile(string id) =>
            id == BoosterManager.ID_FREE_SWAP ? "2glove" : id == BoosterManager.ID_MALLET ? "1hammer"
          : id == BoosterManager.ID_BRAMBLE_SWEEP ? "1goldlightning" : id == BoosterManager.ID_SPYGLASS ? "2spyglass" : id == BoosterManager.ID_AXE ? "1axe" : null;
        private static Sprite LoadBoostersSprite(string file)
        {
            if (file == null) return null;
            if (s_boosterIcons.TryGetValue(file, out var sp)) return sp;
            var tex = Resources.Load<Texture2D>("Boosters/" + file);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
            s_boosterIcons[file] = sp;
            return sp;
        }
        public static Sprite BoosterIcon(string id) => LoadBoostersSprite(BoosterIconFile(id));
        /// <summary>The booster slot's RectTransform (null if the row isn't built / the booster isn't in it).</summary>
        public RectTransform SlotRect(string boosterId)
        {
            if (_slotButtons == null) return null;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
                if (DISPLAY_ORDER[i].Type == SlotType.Booster && DISPLAY_ORDER[i].BoosterId == boosterId && _slotButtons[i] != null)
                    return (RectTransform)_slotButtons[i].transform;
            return null;
        }
        /// <summary>World position (game camera) of a booster slot, at depth z - where booster art launches from.</summary>
        public Vector3 SlotWorldPos(string boosterId, float z = 0f)
        {
            var rt = SlotRect(boosterId); var cam = Camera.main;
            if (rt == null || cam == null) return cam != null ? cam.ViewportToWorldPoint(new Vector3(0.5f, 0.08f, 10f)) : Vector3.zero;
            var c = rt.GetComponentInParent<Canvas>();
            Camera uiCam = c != null && c.renderMode != RenderMode.ScreenSpaceOverlay ? c.worldCamera : null;
            Vector2 sp = RectTransformUtility.WorldToScreenPoint(uiCam, rt.position);
            var w = cam.ScreenToWorldPoint(new Vector3(sp.x, sp.y, Mathf.Abs(cam.transform.position.z - z)));
            w.z = z; return w;
        }

        /// <summary>Slots opened since the player last saw the row: the closed padlock swaps to the OPEN one (same pack,
        /// 1lock_03), pops and fades, and the icon scales in. Seen count persists (PlayerPrefs "booster_slots_seen").</summary>
        private const string SLOTS_SEEN_PREF = "booster_slots_seen";
        public static void ResetSlotsSeen() { PlayerPrefs.DeleteKey(SLOTS_SEEN_PREF); }
        /// <summary>FX menu: replay the unlock moment on this booster's slot (2026-10-29).</summary>
        public void TestUnlock(string boosterId)
        {
            if (_slotButtons == null || _slotArt == null) return;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
                if (DISPLAY_ORDER[i].Type == SlotType.Booster && DISPLAY_ORDER[i].BoosterId == boosterId && _slotArt[i] != null && _slotButtons[i].activeInHierarchy)
                    StartCoroutine(ArtUnlockRoutine(i, 0f));
        }
        /// <summary>UNLOCK MOMENT for the round art (2026-10-29, Spencer: "should we have some kind of unlock animation seeing as we
        /// have a locked image and an unlock image?"). Locked slot + closed gold padlock -> padlock rattles -> CLICK: swaps to the
        /// open padlock with a gold flash, the slot cross-fades from locked to ready with a punch -> padlock floats up and fades ->
        /// icon (+ shadow) springs in -> badge pops.</summary>
        private System.Collections.IEnumerator ArtUnlockRoutine(int i, float delay)
        {
            var slot = _slotButtons[i].transform; var art = _slotArt[i];
            float d = ((RectTransform)slot).sizeDelta.x;
            var icon = slot.Find("BoosterIcon"); var sh = slot.Find("IconShadow");
            var badge = _chargeBadgeContainers[i] != null ? _chargeBadgeContainers[i].transform : null;
            foreach (var t in new[] { sh, badge }) if (t != null) { t.DOKill(); t.localScale = Vector3.zero; }
            art.sprite = LockedSlotArt();
            Image iconImg = icon != null ? icon.GetComponent<Image>() : null; Sprite fullIcon = iconImg != null ? iconImg.sprite : null;
            var ghost = LoadBoostersSprite("slot_" + BoosterIconFile(DISPLAY_ORDER[i].BoosterId) + "_ghost");
            if (icon != null) { icon.DOKill(); icon.localScale = LOCK_CENTRED ? Vector3.zero : Vector3.one; icon.gameObject.SetActive(true); if (ghost != null && iconImg != null) { if (fullIcon == ghost) fullIcon = LoadBoostersSprite("slot_" + BoosterIconFile(DISPLAY_ORDER[i].BoosterId)); iconImg.sprite = ghost; } }   // 11-16 centred lock: the icon is hidden until it springs in
            var lockGO = new GameObject("UnlockFx", typeof(RectTransform), typeof(Image));
            lockGO.transform.SetParent(slot, false);
            var lrt = (RectTransform)lockGO.transform;
            lrt.anchorMin = lrt.anchorMax = lrt.pivot = new Vector2(0.5f, 0.5f);
            var closedLock = LockArt(false, out float lmul);
            lrt.sizeDelta = Vector2.one * d * (LOCK_CENTRED ? LOCK_CENTRED_SIZE : ART_LOCK_CORNER) * lmul;
            lrt.anchoredPosition = LOCK_CENTRED ? new Vector2(0f, (ART_CY - ART_FACE_CY) * d / ART_D) : new Vector2(ART_LOCK_CX, ART_LOCK_CY) * d;
            var limg = lockGO.GetComponent<Image>(); limg.sprite = closedLock; limg.preserveAspect = true; limg.raycastTarget = false;
            if (delay > 0f) yield return new WaitForSecondsRealtime(delay);
            if (lockGO == null || slot == null) yield break;   // 11-16: the row was rebuilt mid-unlock (MissingReferenceException x4 at launch on L19)
            // 1 RATTLE: the padlock shakes against its shackle
            lrt.DOScale(1.1f, 0.12f).SetUpdate(true);
            yield return lrt.DOPunchRotation(new Vector3(0, 0, 14f), 0.38f, 9, 0.6f).SetUpdate(true).WaitForCompletion();
            // 2 CLICK: open padlock + gold flash; the slot lights up from locked to ready
            if (lockGO == null || slot == null || limg == null) yield break;   // row rebuilt during the rattle
            limg.sprite = LockArt(true, out _);
            lrt.localScale = Vector3.one * 1.3f; lrt.DOScale(1f, 0.22f).SetEase(Ease.OutBack).SetUpdate(true);
            var flashGO = new GameObject("UnlockFlash", typeof(RectTransform), typeof(Image));
            flashGO.transform.SetParent(slot, false); flashGO.transform.SetSiblingIndex(lockGO.transform.GetSiblingIndex());
            var frt = (RectTransform)flashGO.transform; frt.anchorMin = frt.anchorMax = frt.pivot = new Vector2(0.5f, 0.5f); frt.sizeDelta = Vector2.one * d;
            var fimg = flashGO.GetComponent<Image>(); fimg.sprite = GetCircleSpriteBig(); fimg.raycastTarget = false; fimg.color = new Color(1f, 0.92f, 0.45f, 0.85f);
            frt.localScale = Vector3.one * 0.5f; frt.DOScale(1.5f, 0.4f).SetEase(Ease.OutQuad).SetUpdate(true);
            fimg.DOFade(0f, 0.4f).SetUpdate(true).OnComplete(() => { if (flashGO != null) Destroy(flashGO); });
            var readyGO = new GameObject("ReadyFade", typeof(RectTransform), typeof(Image));
            readyGO.transform.SetParent(art.transform, false);
            var rrt = (RectTransform)readyGO.transform; rrt.anchorMin = Vector2.zero; rrt.anchorMax = Vector2.one; rrt.offsetMin = rrt.offsetMax = Vector2.zero;
            var rimg = readyGO.GetComponent<Image>(); rimg.sprite = ButtonArt("booster_slot_ready"); rimg.raycastTarget = false; rimg.color = new Color(1, 1, 1, 0);
            rimg.DOFade(1f, 0.25f).SetUpdate(true);
            art.transform.DOKill(true); art.transform.DOPunchScale(Vector3.one * 0.12f, 0.35f, 6, 0.6f).SetUpdate(true);
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("slot_unlock")) GameAudio.Instance.PlayKit("slot_unlock"); else GameAudio.Instance?.PlayScorePowerup();   // 11-07 pick: Suitcase Latch Snap B1
            yield return new WaitForSecondsRealtime(0.25f);
            art.sprite = ButtonArt("booster_slot_ready"); if (readyGO != null) Destroy(readyGO);
            // 3 the padlock floats up and fades; the icon springs in
            lrt.DOAnchorPosY(lrt.anchoredPosition.y + d * 0.45f, 0.4f).SetEase(Ease.InQuad).SetUpdate(true);
            limg.DOFade(0f, 0.4f).SetUpdate(true).OnComplete(() => { if (lockGO != null) Destroy(lockGO); });
            yield return new WaitForSecondsRealtime(0.08f);
            if (sh != null) sh.DOScale(1f, 0.45f).SetEase(Ease.OutBack, 2.2f).SetUpdate(true);
            if (icon != null)
            {   // the ghost turns into the full-colour icon with a pop (locked v2, 10-29)
                if (iconImg != null && fullIcon != null) { iconImg.sprite = fullIcon; iconImg.color = Color.white; }   // 11-16: drop the locked blue tint
                icon.localScale = Vector3.one * (LOCK_CENTRED ? 0.3f : 0.7f); icon.DOScale(1f, 0.45f).SetEase(Ease.OutBack, 2.2f).SetUpdate(true);
            }
            yield return new WaitForSecondsRealtime(0.3f);
            if (badge != null) badge.DOScale(1f, 0.3f).SetEase(Ease.OutBack, 2.5f).SetUpdate(true);
        }

        private void PlayUnlocksIfAny()
        {
            if (!SurvivalManager.FullBoardActive || _slotButtons == null) return;
            int open = TutorialLocks.BoosterSlotsOpen(), seen = PlayerPrefs.GetInt(SLOTS_SEEN_PREF, 0);
            if (open <= seen) { if (open < seen) PlayerPrefs.SetInt(SLOTS_SEEN_PREF, open); return; }
            PlayerPrefs.SetInt(SLOTS_SEEN_PREF, open); PlayerPrefs.Save();
            var openLock = LoadBoostersSprite("1lock_03");
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
            {
                var spec = DISPLAY_ORDER[i];
                if (spec.Type != SlotType.Booster) continue;
                int ord = BoosterOrdinal(spec.BoosterId);
                if (ord < seen || ord >= open || _slotButtons[i] == null) continue;
                WDLog.Log($"[Booster] slot {ord} UNLOCKED ({spec.BoosterId})");
                if (_slotArt[i] != null) { StartCoroutine(ArtUnlockRoutine(i, 0.35f)); continue; }   // round art: the full unlock moment
                var slot = _slotButtons[i].transform;
                var icon = slot.Find("BoosterIcon");
                if (icon != null) { icon.localScale = Vector3.zero; icon.DOScale(1f, 0.45f).SetEase(Ease.OutBack, 2.2f).SetDelay(0.55f).SetUpdate(true); }
                var iconSh = slot.Find("IconShadow");
                if (iconSh != null) { iconSh.localScale = Vector3.zero; iconSh.DOScale(1f, 0.45f).SetEase(Ease.OutBack, 2.2f).SetDelay(0.55f).SetUpdate(true); }
                if (_slotArt[i] != null) _slotArt[i].sprite = ButtonArt("booster_slot_ready");
                if (openLock == null) continue;
                var lockGO = new GameObject("UnlockFx", typeof(RectTransform), typeof(Image));
                lockGO.transform.SetParent(slot, false);
                var rt = lockGO.GetComponent<RectTransform>();
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                float slotW = ((RectTransform)slot).sizeDelta.x; rt.sizeDelta = Vector2.one * (HUDManager.MOCKUP_HUD ? slotW * 0.70f : SLOT_SIZE * 0.62f);
                var img = lockGO.GetComponent<Image>(); img.sprite = openLock; img.preserveAspect = true; img.raycastTarget = false;
                var seq = DOTween.Sequence().SetUpdate(true);
                seq.Append(rt.DOScale(1.25f, 0.18f).SetEase(Ease.OutQuad));
                seq.Append(rt.DOScale(1f, 0.15f));
                seq.AppendInterval(0.2f);
                seq.Append(rt.DOAnchorPosY(SLOT_SIZE * 0.35f, 0.35f).SetEase(Ease.InQuad));
                seq.Join(img.DOFade(0f, 0.35f));
                seq.OnComplete(() => { if (lockGO != null) Destroy(lockGO); });
                if (GameAudio.Instance != null && GameAudio.Instance.HasKit("slot_unlock")) GameAudio.Instance.PlayKit("slot_unlock"); else GameAudio.Instance?.PlayScorePowerup();   // 11-07 pick: Suitcase Latch Snap B1
            }
        }

        /// <summary>BOOSTER NUDGE (2026-10-23): pulse the first unlocked booster that has charges (stuck on the last goal pieces).</summary>
        public void NudgeBooster()
        {
            var bm = BoosterManager.Instance; if (bm == null || _slotButtons == null || bm.AimMode) return;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
            {
                var spec = DISPLAY_ORDER[i];
                if (spec.Type != SlotType.Booster || IsSlotLocked(spec) || _slotButtons[i] == null) continue;
                if (bm.GetCharges(spec.BoosterId) <= 0) continue;
                var t = _slotButtons[i].transform;
                t.DOKill(true);
                t.DOPunchScale(Vector3.one * 0.18f, 0.6f, 4, 0.5f).SetLoops(3).SetUpdate(true);
                WDLog.Log($"[Booster] NUDGE — stuck on the last goal pieces, pulsing {spec.BoosterId}");
                return;
            }
            WDLog.Log("[Booster] NUDGE — stuck, but no unlocked booster has charges");
        }

        /// <summary>Position of this booster among the row's BOOSTER slots (0 = leftmost) — its unlock order.</summary>
        private static int BoosterOrdinal(string id)
        {
            int n = 0;
            foreach (var sp in DISPLAY_ORDER) { if (sp.Type != SlotType.Booster) continue; if (sp.BoosterId == id) return n; n++; }
            return 99;
        }

        private struct SlotSpec
        {
            public SlotType Type;
            public string   BoosterId;
            public string   PlaceholderChar;
        }

        // 2026-06-24 layout (Spencer): Edit + Bag moved OUT of the bench row and up to
        // FLANK the tile holder (Edit left, Bag right). The bench row is now the 5
        // boosters + Settings, evenly spaced full-width. Mallet is the new 5th booster.
        // Edit/Bag are still listed here (so they keep their slot wiring/charges/locks);
        // their positions are computed specially in BuildSlotCanvas — see FLANK_* below.
        private static readonly SlotSpec[] DISPLAY_ORDER = new[]
        {
            new SlotSpec { Type = SlotType.Edit,                                                  PlaceholderChar = "E" },
            // 2026-06-24 (Spencer): Tile Bag swap CUT for launch — rarely used, non-obvious, and the
            // Mallet booster covers "ditch a bad tile." Swap code left dormant (IsScreenPointOverTileBag
            // returns false with no bag slot, so drag-onto-bag simply never triggers). Re-add this
            // SlotSpec line to bring it back.
            // 2026-10-23: FREE SWAP takes Stone Splitter's slot (Splitter clears every blocker = an instant-win button on crate
            // levels; it moves to the shop / pre-level later). Swap these two lines back to restore it.
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_FREE_SWAP,     PlaceholderChar = "F" },
            // 2026-10-23: slots unlock left to right at L8 / L14 / L17 / L19 — FREE SWAP first (stale boards are the #1 fun
            // problem in Spencer's run), Mallet second.
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_MALLET,        PlaceholderChar = "M" }, // 2026-06-24: Mallet first
            // new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_ROCK_CRUSHER,  PlaceholderChar = "S" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BRAMBLE_SWEEP, PlaceholderChar = "C" },
            // 2026-10-22 Spencer: "the paid booster can be the spyglass one in my mock up" — his mockup's 4th booster is a
            // spyglass. Bloomburst moves out of the row (code intact; swap this line back to restore it).
            // 2026-10-30 Spencer: axe and spyglass swapped places - AXE 4th (opens L19, next to the lightning row-clear it pairs
            // with), SPYGLASS last (opens L22). Slots unlock left to right, so the unlock levels follow the position.
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_AXE,           PlaceholderChar = "A" },
            new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_SPYGLASS,      PlaceholderChar = "?" },
            // new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_BLOOMBURST,    PlaceholderChar = "B" },
            // 2026-09-11 (Spencer): row cut to FOUR boosters + Settings. Wispwhirl parked as the
            // 5th — it is the last-listed, nothing else depends on the count. Swap which one is
            // commented out to change the four; BENCH_SLOT_COUNT below must match the row length.
            // new SlotSpec { Type = SlotType.Booster,  BoosterId = BoosterManager.ID_WISPWHIRL,     PlaceholderChar = "J" },
            new SlotSpec { Type = SlotType.Settings,                                              PlaceholderChar = "" },   // 2026-10-22: "⚙" isn't in the font (drew an empty box) — a drawn gear sits there instead (GearSprite)
        };

        // ── 2026-06-24 layout constants ─────────────────────────────────────────
        // Bench row now holds 6 slots (5 boosters + Settings) evenly spaced across the
        // SAME usable span the old 7-slot row used (left margin 37.98, span 1103.04),
        // so the bench backdrop still frames them.
        private const int   BENCH_SLOT_COUNT = 6;   // 2026-10-29: 5 boosters (+ Axe) + Settings. 2026-09-11 was 4 + Settings
        // Margins tightened to match Royal Match too — its row spans 95% of the screen width
        // (~2.5% either side) where WordDrop used 3.2%. 1179 - 2*29 = 1121.
        private const float BENCH_ROW_SPAN   = 1121f;
        private const float SLOT_STEP_BENCH  = (BENCH_ROW_SPAN - BENCH_SLOT_COUNT * SLOT_SIZE) / (BENCH_SLOT_COUNT - 1) + SLOT_SIZE; // ≈193.61
        // 2026-06-24 (Spencer): Edit is a rounded-SQUARE TOOL attached UNDER the tile holder
        // panel's bottom edge, CENTRED (Bag cut for launch), distinct from the round consumable
        // boosters. Holder raised 45 PSD → pill now spans Y 1888..2088; the tab tucks up ~12px
        // under the bottom edge so it reads as attached and hangs into the gap above the boosters.
        // FLANK_Y_PSD is the slot's PSD top edge. BAG_X_PSD kept for the dormant swap-X fallback.
        private const float FLANK_SIZE  = 120f;
        private const float FLANK_Y_PSD = 2114f;   // 2026-06-24: lowered ~38 PSD (was 2076, tucked under the pill) — drops the SWAPS panel into the gap
        private const float EDIT_X_PSD  = 529.5f;  // (legacy) single-square Edit left edge — superseded by the panel
        private const float BAG_X_PSD   = 889f;    // (dormant) swap-X fallback only — Bag slot removed
        // "EDITS" pip panel (replaces the Edit square): wide rounded panel, label + 3 gold pips.
        private const float EDIT_PANEL_W = 340f;
        private const float EDIT_PANEL_H = 100f;   // condensed height (was 150 → 120 → 100)
        private const int   EDIT_PIP_COUNT = 3;    // matches the survival rewrite cap of 3
        private static readonly Color PIP_FULL  = new Color(0.52f, 0.92f, 1.00f, 1f);   // 2026-07-10: light candy CYAN — matches the bright bloomed swap tile (was too deep/blue at 0.30,0.82,0.96)
        private static readonly Color PIP_EMPTY = new Color(0.30f, 0.36f, 0.46f, 0.85f); // dim = used
        private Image[]         _editPips;
        private RectTransform[] _editPipRects;
        /// <summary>2026-09-03: swaps are unlimited, so the SWAPS pip panel is hidden.</summary>
        public static bool ShowSwapsPanel = false;
        private GameObject      _editPanelSlot;
        private CanvasGroup     _editPanelGroup; // dims the whole panel when out of edits
        private TextMeshProUGUI _editAdHint;     // "▶ FREE +3" shown when out + a rewarded ad is offered
        private Tween           _editPulseTween; // gentle pulse inviting the ad tap
        private bool            _swapAdInFlight; // guards double-tap while a rewarded ad is showing
        private int             _lastPipCount = -1;

        private GameObject[]      _slotButtons;
        private Image[]           _slotImages;

        // ── Tap-to-swap mode (full-screen dim with X over bag) ──
        private Canvas     _swapModeCanvas;       // overlay scrim + X
        private GameObject _swapModeScrim;        // full-screen dim Image
        private GameObject _swapModeXButton;      // X positioned over the bag slot
        public  bool       TileBagSwapModeActive { get; private set; }

        // ── Booster aim-mode scrim ──
        // 2026-06-01: cutout scrim — two Image rectangles covering the area
        // ABOVE the board (HUD + gap where "TAP A TILE" sits) and BELOW the
        // board (hand row + booster bench). The board area is left UNCOVERED
        // so tiles and the board panel render at their natural sortingOrder
        // without needing per-tile bumps (which fought Tile.cs's internal
        // SetSortingOrder(5) calls during animations).
        private Canvas     _aimScrimCanvas;
        private GameObject _aimScrimTop;     // above the board
        private GameObject _aimScrimBottom;  // below the board
        private GameObject _aimScrimLeft;    // left of the board panel
        private GameObject _aimScrimRight;   // right of the board panel
        private bool       _wasAim;
        private const int  AIM_SCRIM_SORT_ORDER = 15;  // above hand cards (10) and bench (8), below dragged card (20)
        private const int  ARMED_SLOT_SORT_ORDER = 20; // armed booster slot Canvas.overrideSorting target — above scrim

        // Board region in normalized canvas anchor coords (bottom-left origin).
        // Calibrated from PSD spec: board X=44.5 W=1110 (so right=1154.5),
        // Y=446 H=1420 (so bottom=1866) on a 1179×2556 canvas.
        //   left   X PSD 44.5   / 1179 = 0.0377
        //   right  X PSD 1154.5 / 1179 = 0.979
        //   top    Y → 1 - (446/2556)  = 0.826
        //   bottom Y → 1 - (1866/2556) = 0.270
        private const float BOARD_LEFT_ANCHOR_X   = 0.0377f;
        private const float BOARD_RIGHT_ANCHOR_X  = 0.979f;
        private const float BOARD_TOP_ANCHOR_Y    = 0.826f;
        private const float BOARD_BOTTOM_ANCHOR_Y = 0.270f;

        /// <summary>Hit-test a screen-space point against the TileBag slot's
        /// rect. Used by HandManager's drag-release path to detect when the
        /// player drops a hand card onto the bag for a tile-swap. The legacy
        /// world-space bag (HandManager._tileBagButton) was retired in the
        /// Path A HUD rework, so drag-release now has to query this
        /// screen-space slot instead.</summary>
        public bool IsScreenPointOverTileBag(Vector2 screenPoint)
        {
            if (_slotButtons == null) return false;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
            {
                if (DISPLAY_ORDER[i].Type != SlotType.TileBag) continue;
                if (_slotButtons[i] == null) return false;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                if (rt == null) return false;
                // 2026-06-01: pass the canvas's render camera now that the
                // canvas is in ScreenSpaceCamera mode (was Overlay → null was
                // correct previously). RectangleContainsScreenPoint needs the
                // camera for Camera/WorldSpace canvases to map screen point to
                // local rect correctly.
                Camera cam = _slotCanvas != null ? _slotCanvas.worldCamera : null;
                return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, cam);
            }
            return false;
        }
        private TextMeshProUGUI[] _slotLabels;
        private TextMeshProUGUI[] _chargeBadges;
        private GameObject[]      _chargeBadgeContainers;

        private Canvas _slotCanvas;
        private Canvas _aimCanvas;
        private TextMeshProUGUI _aimBanner;
        /// <summary>The June "TAP A TILE" aim banner. Off (11-07): the rebuilt HUD is taller, so it sat over the Goals panel, and the
        /// booster lessons already teach each booster (RM-style: cut the text).</summary>
        public static bool AIM_BANNER = false;
        private Button _aimCancelButton;

        // Bench panel + group-animation rest state for the
        // AnimateGroupOut / AnimateGroupIn Candy-Crush-style converge/expand.
        private GameObject _benchGO;
        private bool       _restPositionsCached;
        private Vector2[]  _slotRestPositions;
        private Vector3[]  _slotRestScales;
        private Vector2    _benchCenterAnchored;

        // ── Unity lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildSlotCanvas();
            BuildAimCanvas();
            RefreshDisplay();
        }

        private void OnEnable()
        {
            if (BoosterManager.Instance != null)
                BoosterManager.Instance.OnStateChanged += RefreshDisplay;
            if (MatchController.Instance != null)
                MatchController.Instance.OnSwapUsed += OnSwapUsed_RefreshBadge;
        }

        private void OnDisable()
        {
            if (BoosterManager.Instance != null)
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
            if (MatchController.Instance != null)
                MatchController.Instance.OnSwapUsed -= OnSwapUsed_RefreshBadge;
        }

        private static bool s_settingsPrewarmed;

        /// <summary>Statics survive a play-mode exit when Reload Domain is off, so without this
        /// the prewarm runs on the FIRST play of an editor session and never again — and the
        /// hitch would look "fixed" once then come back. Same guard CaptureRig uses.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetPrewarmFlag() { s_settingsPrewarmed = false; }

        private System.Collections.IEnumerator PrewarmSettingsModal()
        {
            if (s_settingsPrewarmed) yield break;
            s_settingsPrewarmed = true;
            // Unscaled: the board entry may be running on a paused/slowed timescale.
            yield return new WaitForSecondsRealtime(2.0f);
            if (SettingsModal.Instance == null)
            {
                var modalGO = new GameObject("SettingsModalRoot");
                modalGO.AddComponent<SettingsModal>();
                yield return null;              // let Awake run and set Instance
            }
            SettingsModal.Prewarm();
        }

        private void Start()
        {
            // Build the settings modal AHEAD of the first press. It costs ~33 GameObjects, a
            // Canvas + CanvasScaler and a batch of TMP text, and paying for that on the frame the
            // player taps is the "sticks the first time, fine afterwards" hitch. Deferred a couple
            // of seconds so it lands after the board entry has settled rather than during it.
            // 2026-09-28 Spencer.
            StartCoroutine(PrewarmSettingsModal());

            if (BoosterManager.Instance != null)
            {
                BoosterManager.Instance.OnStateChanged -= RefreshDisplay;
                BoosterManager.Instance.OnStateChanged += RefreshDisplay;
            }
            // MatchController may not have existed at OnEnable time (it's
            // created on demand). Re-bind here defensively.
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnSwapUsed -= OnSwapUsed_RefreshBadge;
                MatchController.Instance.OnSwapUsed += OnSwapUsed_RefreshBadge;
            }
            RefreshDisplay();
        }

        /// <summary>Fires when MatchController.UseSwap decrements the swap
        /// counter. Forces the TileBag chip badge to re-read the live value.</summary>
        private void OnSwapUsed_RefreshBadge(SwapUsedEvent evt)
        {
            RefreshDisplay();
        }

        private int _lastEditCount = -1;

        private void Update()
        {
            // Edit-counter polling — refresh the badge when MatchController's
            // GetRewritesRemaining changes (no event to subscribe to).
            int editCount = MatchController.Instance != null
                ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN)
                : 0;
            if (editCount != _lastEditCount)
            {
                _lastEditCount = editCount;
                RefreshDisplay();
                RefreshEditPips(editCount); // update + animate the EDITS pips (glow burst on gain)
            }

            // Onboarding locks can flip between levels — rebuild the slots when they change so the
            // padlock overlays appear/disappear correctly (the overlay is built in BuildSlotCanvas). 2026-06-25.
            int lockHash = (TutorialLocks.EditLocked ? 1 : 0)
                         | (TutorialLocks.BagLocked ? 2 : 0)
                         | (TutorialLocks.BoostersLocked ? 4 : 0)
                         | (TutorialLocks.BoosterSlotsOpen() << 3);
            if (lockHash != _lastLockHash)
            {
                _lastLockHash = lockHash;
                RebuildSlots();
                PlayUnlocksIfAny();
            }

            // Aim-mode board-tap polling.
            if (BoosterManager.Instance == null || !BoosterManager.Instance.AimMode) return;
            // armed when the level was won -> put it away, no charge used (11-16)
            if (SurvivalManager.AwaitingNextLevel) { BoosterManager.Instance.CancelAim(); return; }
            { var own = SurvivalManager.Instance; if (own != null && (own.IsOverlayPaused || own.OutOfMovesLocked)) { BoosterManager.Instance.CancelAim(); return; } }
            // 11-18 (Spencer, phone: the glove's pick click "sounds like a grinding gear like it is playing a bunch of times"): this used
            // to pass on EVERY frame a finger was on the glass (touchCount > 0), so one tap picked, un-picked, picked ... the tile
            // 60 times a second - a click each time, and the pick glow restarted each time. The mouse path was already one-shot,
            // which is why the editor never showed it. A touch now counts once, on the frame it lands.
            bool touchBegan = WDInput.touchCount > 0 && WDInput.GetTouch(0).phase == TouchPhase.Began;
            if (!WDInput.GetMouseButtonDown(0) && !touchBegan) return;
            if (Camera.main == null) return;

            Vector3 screenPos = WDInput.touchCount > 0
                ? (Vector3)WDInput.GetTouch(0).position
                : WDInput.mousePosition;

            if (_aimCancelButton != null)
            {
                var rt = _aimCancelButton.GetComponent<RectTransform>();
                if (RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null))
                    return;
            }

            Vector3 worldPos = Camera.main.ScreenToWorldPoint(
                new Vector3(screenPos.x, screenPos.y, -Camera.main.transform.position.z));
            worldPos.z = 0f;

            if (GridManager.Instance != null
                && GridManager.Instance.WorldToCell(worldPos, out int col, out int row))
            {
                WDLog.Log($"[BoosterDbg] AimTap screen=({screenPos.x:0},{screenPos.y:0}) " +
                          $"world=({worldPos.x:0.00},{worldPos.y:0.00}) → cell=({col},{row})");
                BoosterManager.Instance.ResolveAim(col, row);
            }
            else
            {
                WDLog.Log($"[BoosterDbg] AimTap screen=({screenPos.x:0},{screenPos.y:0}) " +
                          $"world=({worldPos.x:0.00},{worldPos.y:0.00}) → WorldToCell FAILED");
            }
        }

        // ── PSD coord → Unity anchored position helpers ─────────────────────────

        private static Sprite s_gear;
        /// <summary>Antialiased white gear (8 teeth, centre hole), 4x4 supersampled. Placeholder settings icon.</summary>
        private static Sprite GearSprite()
        {
            if (s_gear != null) return s_gear;
            const int N = 128; var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[N * N]; float c = N * 0.5f;
            float R = N * 0.47f, root = N * 0.35f, hole = N * 0.15f; const int TEETH = 8;
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                int hit = 0;
                for (int sy = 0; sy < 4; sy++) for (int sx = 0; sx < 4; sx++)
                {
                    float dx = x + (sx + 0.5f) / 4f - c, dy = y + (sy + 0.5f) / 4f - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float t = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI) * TEETH + 0.25f, 1f);   // 0..1 across one tooth period
                    float edge = (t > 0.15f && t < 0.55f) ? R : root;                                    // tooth top ~40% of the period
                    if (r <= edge && r >= hole) hit++;
                }
                px[y * N + x] = new Color(1f, 1f, 1f, hit / 16f);
            }
            tex.SetPixels(px); tex.Apply();
            return s_gear = Sprite.Create(tex, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Vector2 PsdAnchoredCenter(float xPsd, float yPsd, float wPsd, float hPsd)
        {
            // Caller anchors with (0.5, 0) (bottom-center) and pivot (0.5, 0.5).
            float ax = (xPsd + wPsd * 0.5f) - (CANVAS_W * 0.5f);
            float ay = CANVAS_H - (yPsd + hPsd * 0.5f);
            return new Vector2(ax, ay);
        }

        // ── Slot canvas (5 buttons + bench panel) ───────────────────────────────

        private int _lastLockHash = -1; // tracks TutorialLocks state so a change triggers a rebuild

        /// <summary>Tear down + rebuild the slot canvas so the padlock overlays match the current
        /// TutorialLocks state (the overlay is created in BuildSlotCanvas). Called on a lock change.</summary>
        public void RebuildSlots()
        {
            if (_slotCanvas != null) Destroy(_slotCanvas.gameObject);
            _slotCanvas = null;
            BuildSlotCanvas();
            RefreshDisplay();
        }

        private void BuildSlotCanvas()
        {
            var canvasGO = new GameObject("BoosterHUDSlotCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _slotCanvas = canvasGO.GetComponent<Canvas>();
            // 2026-06-01: switched from ScreenSpaceOverlay to ScreenSpaceCamera
            // so the canvas's sortingOrder can compete with world-space
            // SpriteRenderers. Overlay always renders above the scene
            // regardless of order, which left the dragged hand card hidden
            // behind the bag slot during a drag-to-swap. Camera mode + a low
            // sortingOrder lets the card (sortingOrder=20 while dragged via
            // BoostCardSortOrder) draw above the bag while still keeping the
            // bag above the board tiles (sortingOrder=5-6).
            _slotCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _slotCanvas.worldCamera = Camera.main;
            _slotCanvas.planeDistance = 2f;
            _slotCanvas.sortingOrder = 8;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            // 1.0 = match by height — preferred for portrait phones where the
            // design width-fits the device. PSD canvas IS the iPhone canvas, so
            // any matchWidthOrHeight choice should produce identical layout on
            // iPhone; we pick height-match to be tolerant of taller devices.
            scaler.matchWidthOrHeight = 1.0f;

            // ── Bench panel (drawn first → sits behind buttons) ─────────────────
            var benchGO = new GameObject("BenchPanel",
                typeof(RectTransform), typeof(Image));
            benchGO.transform.SetParent(canvasGO.transform, false);
            _benchGO = benchGO;
            var benchRT = benchGO.GetComponent<RectTransform>();
            benchRT.anchorMin = new Vector2(0.5f, 0f);
            benchRT.anchorMax = new Vector2(0.5f, 0f);
            benchRT.pivot     = new Vector2(0.5f, 0.5f);
            benchRT.sizeDelta = new Vector2(BENCH_W, BENCH_H);
            benchRT.anchoredPosition = PsdAnchoredCenter(BENCH_X, BENCH_Y, BENCH_W, BENCH_H);
            var benchImg = benchGO.GetComponent<Image>();
            benchImg.sprite = GetBenchSprite();
            // 2026-09-10 palette pass: was deep ocean blue (0.10,0.27,0.50) — a THIRD hue
            // family alongside the cyan background and the plum board, so the bench read as a
            // strip from another app. Deep teal keeps it clearly a shelf while belonging to the
            // background's family. Revert = restore the line below.
            benchImg.color  = new Color(0.08f, 0.38f, 0.48f, 0.93f); // deep teal — background family
            benchImg.raycastTarget = false;

            // Blue EXTENSION strip BELOW the bench (child → slides with it). When the bench pops UP and overshoots
            // on level-entry, this keeps the area below it filled with the bench colour instead of the cyan
            // background — reads as the panel extending off the bottom, no gap. Mirrors the top bar's strip.
            // 2026-07-13 Spencer.
            {
                var extGO = new GameObject("BenchBottomExtend", typeof(RectTransform), typeof(Image));
                extGO.transform.SetParent(benchGO.transform, false);
                extGO.transform.SetAsFirstSibling();
                var extRT = extGO.GetComponent<RectTransform>();
                extRT.anchorMin = new Vector2(0f, 0f);
                extRT.anchorMax = new Vector2(1f, 0f);
                extRT.pivot     = new Vector2(0.5f, 1f);  // hangs DOWN from the bench bottom
                extRT.sizeDelta = new Vector2(0f, 400f);
                extRT.anchoredPosition = Vector2.zero;
                var extImg = extGO.GetComponent<Image>();
                extImg.color = benchImg.color;
                extImg.raycastTarget = false;
            }

            if (HUDManager.MOCKUP_HUD)
            {
                // the mockup has no bench strip — the buttons sit on the background
                benchImg.color = new Color(0f, 0f, 0f, 0f);
                var benchExt = benchGO.transform.Find("BenchBottomExtend"); if (benchExt != null) benchExt.gameObject.SetActive(false);
            }

            int slotCount = DISPLAY_ORDER.Length;
            _slotButtons           = new GameObject[slotCount];
            _slotImages            = new Image[slotCount];
            _slotLabels            = new TextMeshProUGUI[slotCount];
            _chargeBadges          = new TextMeshProUGUI[slotCount];
            _chargeBadgeContainers = new GameObject[slotCount];
            _slotArt               = new Image[slotCount];

            var displayFont = GameFont.GetDisplayTMP();

            int benchIdx = 0; // counts only the bench-row slots (boosters + Settings)
            for (int i = 0; i < slotCount; i++)
            {
                int slotIndex = i;
                var spec = DISPLAY_ORDER[i];

                // Edit is the wide "EDITS" pip panel centred under the holder; everything else
                // sits in the evenly spaced bench row along the bottom (full size).
                Vector2 anchored;
                float slotSize;     // square size for round slots
                Vector2 slotDim;    // actual RectTransform sizeDelta
                if (spec.Type == SlotType.Edit)
                {
                    slotSize = FLANK_SIZE;
                    slotDim  = new Vector2(EDIT_PANEL_W, EDIT_PANEL_H);
                    anchored = PsdAnchoredCenter(CANVAS_W * 0.5f - EDIT_PANEL_W * 0.5f, FLANK_Y_PSD, EDIT_PANEL_W, EDIT_PANEL_H);
                }
                else if (spec.Type == SlotType.TileBag)
                {
                    slotSize = FLANK_SIZE;
                    slotDim  = new Vector2(FLANK_SIZE, FLANK_SIZE);
                    anchored = PsdAnchoredCenter(BAG_X_PSD, FLANK_Y_PSD, FLANK_SIZE, FLANK_SIZE);
                }
                else if (HUDManager.MOCKUP_HUD)
                {
                    // 2026-10-22 Spencer's layout mockup (newboard@2x.png, /2 = design units): Settings bottom-LEFT, centre
                    // (114,2405) ~146 across; the four boosters to its right, centres x 370/598/826/1053 at y 2390, ~162 green
                    // body (+ outline). Placeholder slot art until his buttons land.
                    if (spec.Type == SlotType.Settings)
                    {
                        slotSize = MK_SETTINGS_SIZE; slotDim = new Vector2(slotSize, slotSize);
                        anchored = PsdAnchoredCenter(114f - slotSize * 0.5f, 2405f - slotSize * 0.5f, slotSize, slotSize);
                        if (RkRow)
                        {   // RK proportions: smaller than a booster, first in the centred row, centres level with the boosters
                            slotSize = RK_SETTINGS * CANVAS_W; slotDim = new Vector2(slotSize, slotSize);
                            anchored = PsdAnchoredCenter(RkLeft, RK_ROW_Y - slotSize * 0.5f, slotSize, slotSize);
                            if (RK2_LAYOUT)
                            {   // v2: Settings at the RIGHT end of the row (RK)
                                slotSize = RK2_SETTINGS * CANVAS_W; slotDim = new Vector2(slotSize, slotSize);
                                anchored = PsdAnchoredCenter(RK2_SETTINGS_X * CANVAS_W - slotSize * 0.5f, RK_ROW_Y - slotSize * 0.5f, slotSize, slotSize);
                            }
                        }
                    }
                    else
                    {
                        slotSize = MkBoosterSize(); slotDim = new Vector2(slotSize, slotSize);
                        float cx = MkBoosterCentre(benchIdx);
                        anchored = PsdAnchoredCenter(cx - slotSize * 0.5f, (RkRow ? RK_ROW_Y : 2390f) - slotSize * 0.5f, slotSize, slotSize);
                        benchIdx++;
                    }
                }
                else
                {
                    slotSize = SLOT_SIZE;
                    slotDim  = new Vector2(SLOT_SIZE, SLOT_SIZE);
                    float xPsd = SLOT_LEFT_X + benchIdx * SLOT_STEP_BENCH;
                    anchored = PsdAnchoredCenter(xPsd, SLOT_TOP_Y, SLOT_SIZE, SLOT_SIZE);
                    benchIdx++;
                }

                var slot = new GameObject($"Slot_{i}_{spec.Type}",
                    typeof(RectTransform), typeof(Image), typeof(Button));
                slot.transform.SetParent(canvasGO.transform, false);
                _slotButtons[i] = slot;

                var rt = slot.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2(0.5f, 0f);
                rt.anchorMax = new Vector2(0.5f, 0f);
                rt.pivot     = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = slotDim;
                rt.anchoredPosition = anchored;

                _slotImages[i] = slot.GetComponent<Image>();
                // Edit + Bag are rounded-SQUARE tools; boosters/Settings stay round.
                if (spec.Type == SlotType.Edit || spec.Type == SlotType.TileBag)
                {
                    _slotImages[i].sprite = MenuUI.GetRoundedRectSprite(34);
                    _slotImages[i].type   = Image.Type.Sliced;
                }
                else if (spec.Type == SlotType.Settings)
                {
                    // Settings stays a CIRCLE so it reads as a different KIND of control, not a
                    // fifth booster. Matches Spencer's concept. 2026-09-11.
                    var c = Resources.Load<Sprite>("UI/booster_circle");
                    _slotImages[i].sprite = c != null ? c : GetCircleSpriteBig();
                    // placeholder gear until Spencer's settings button art lands
                    var gearGO = new GameObject("Gear", typeof(RectTransform), typeof(Image));
                    gearGO.transform.SetParent(slot.transform, false);
                    var grt = (RectTransform)gearGO.transform;
                    grt.anchorMin = new Vector2(0.22f, 0.22f); grt.anchorMax = new Vector2(0.78f, 0.78f); grt.offsetMin = grt.offsetMax = Vector2.zero;
                    var gimg = gearGO.GetComponent<Image>(); gimg.sprite = GearSprite(); gimg.preserveAspect = true; gimg.raycastTarget = false;
                    gimg.color = new Color32(0xFB, 0xF2, 0xE2, 0xFF);
                    if (UseRoundArt && ButtonArt("settings_round") != null)
                    {   // 2026-10-29 Spencer: "the settings gear should probably be a purple button" - round purple v3 art, same family.
                        // Later: RIMLESS ("lets go rimless") - the purple disc without the gold, so it reads secondary to the boosters,
                        // and the AssetsCrafters 3D gear gradient-mapped to cream (the drawn gear "looks pretty cheap").
                        _slotImages[i].sprite = null; _slotImages[i].color = new Color(1f, 1f, 1f, 0f);
                        var artGO = new GameObject("SlotArt", typeof(RectTransform), typeof(Image));
                        artGO.transform.SetParent(slot.transform, false); artGO.transform.SetAsFirstSibling();
                        PlaceArt((RectTransform)artGO.transform, slotSize);
                        var rimless = SETTINGS_RIMLESS ? ButtonArt("settings_rimless") : null;
                        _slotArt[i] = artGO.GetComponent<Image>(); _slotArt[i].sprite = rimless != null ? rimless : ButtonArt("settings_round"); _slotArt[i].raycastTarget = false;
                        var gearArt = ButtonArt("settings_gear");
                        if (gearArt != null) { gimg.sprite = gearArt; gimg.color = Color.white; }
                        grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f); grt.sizeDelta = Vector2.one * slotSize * (rimless != null ? SETTINGS_GEAR_FILL : 0.52f);
                        grt.anchoredPosition = new Vector2(0f, (ART_CY - ART_FACE_CY) * slotSize / ART_D);
                    }
                }
                else
                {
                    // Boosters are neon SQUIRCLES (Layer Lab GUI-Neon2, copied into Resources/UI).
                    // Placeholder shapes — Spencer is making the real button art. 2026-09-11.
                    var sq = Resources.Load<Sprite>("UI/booster_squircle");
                    _slotImages[i].sprite = sq != null ? sq : GetCircleSpriteBig();
                }
                if (_slotArt[i] == null) _slotImages[i].color  = SlotBaseColor(spec.Type);   // round-art slots keep their alpha-0 hit area
                if (spec.Type == SlotType.Booster && UseRoundArt)
                {   // the slot's own Image stays as an invisible hit area (alpha 0 still takes taps); the art is a child
                    _slotImages[i].sprite = null; _slotImages[i].color = new Color(1f, 1f, 1f, 0f);
                    var artGO = new GameObject("SlotArt", typeof(RectTransform), typeof(Image));
                    artGO.transform.SetParent(slot.transform, false);
                    PlaceArt((RectTransform)artGO.transform, slotSize);
                    _slotArt[i] = artGO.GetComponent<Image>(); _slotArt[i].sprite = ButtonArt("booster_slot_ready"); _slotArt[i].raycastTarget = false;
                }

                var slotBtn = slot.GetComponent<Button>();
                slotBtn.onClick.AddListener(() => OnSlotTapped(slotIndex));

                // 2026-06-01: override Unity's default disabled-state tint
                // (which dims the button to ~50% alpha) so the Edit slot —
                // which we set interactable=false on per Spencer — doesn't
                // look "see through" relative to the active booster slots.
                // disabledColor == normalColor means there's no visual
                // difference between enabled and disabled states.
                var cb = slotBtn.colors;
                cb.normalColor      = Color.white;
                cb.highlightedColor = Color.white;
                cb.pressedColor     = new Color(0.85f, 0.85f, 0.85f, 1f);
                cb.selectedColor    = Color.white;
                cb.disabledColor    = Color.white;
                cb.colorMultiplier  = 1f;
                slotBtn.colors = cb;
                if (_slotArt[i] != null) slotBtn.targetGraphic = _slotArt[i];   // press tint shows on the art

                // Settings slot gets a two-stage click feel: PointerDown
                // fires the press half (otherpop_press), release fires the
                // release half (handled in OnSlotTapped below). Other slots
                // keep the standard single-click PlayUIClick.
                if (spec.Type == SlotType.Settings)
                {
                    var trigger = slot.GetComponent<EventTrigger>();
                    if (trigger == null) trigger = slot.AddComponent<EventTrigger>();
                    var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                    pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlaySettingsPress());
                    trigger.triggers.Add(pdEntry);
                }
                else if (spec.Type == SlotType.Booster)
                {
                    // Booster slots get a PointerDown handler that fires the
                    // multi-pop press half ONLY when this slot is currently
                    // styled as the aim-cancel button (red ✕). Otherwise the
                    // PointerDown is a no-op and the slot's onClick handler
                    // plays PlaySettingsRelease via OnSlotTapped's top branch.
                    var trigger = slot.GetComponent<EventTrigger>();
                    if (trigger == null) trigger = slot.AddComponent<EventTrigger>();
                    int capturedIndex = slotIndex;
                    var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                    pdEntry.callback.AddListener((_) =>
                    {
                        var bm = BoosterManager.Instance;
                        if (bm == null || !bm.AimMode) return;
                        if (capturedIndex < 0 || capturedIndex >= DISPLAY_ORDER.Length) return;
                        var s = DISPLAY_ORDER[capturedIndex];
                        if (s.Type != SlotType.Booster) return;
                        if (bm.ArmedBooster == null || s.BoosterId != bm.ArmedBooster.Id) return;
                        GameAudio.Instance?.PlayMultiPopPress();
                    });
                    trigger.triggers.Add(pdEntry);
                }

                // TileBag slot uses the procedural bag sprite as its icon
                // instead of a letter placeholder. All other slots get a
                // text label until real icon art lands.
                if (spec.Type == SlotType.TileBag)
                {
                    var iconGO = new GameObject("BagIcon",
                        typeof(RectTransform), typeof(Image));
                    iconGO.transform.SetParent(slot.transform, false);
                    var iconImg = iconGO.GetComponent<Image>();
                    iconImg.sprite = HandManager.BuildTileBagSpriteForUI();
                    iconImg.color = new Color(1f, 0.95f, 0.78f, 1f);
                    iconImg.raycastTarget = false;
                    iconImg.preserveAspect = true;
                    var iconRT = iconGO.GetComponent<RectTransform>();
                    iconRT.anchorMin = new Vector2(0.5f, 0.5f);
                    iconRT.anchorMax = new Vector2(0.5f, 0.5f);
                    iconRT.pivot     = new Vector2(0.5f, 0.5f);
                    iconRT.sizeDelta = new Vector2(slotSize * 0.70f, slotSize * 0.78f);
                    iconRT.anchoredPosition = Vector2.zero;
                    _slotLabels[i] = null;
                }
                else if (spec.Type == SlotType.Edit)
                {
                    // EDITS pip panel — "EDITS" label + 3 gold pips (charge readout). No letter, no badge.
                    BuildEditsPanel(slot, displayFont);
                    _slotLabels[i] = null;
                }
                else if (spec.Type == SlotType.Booster && BoosterIcon(spec.BoosterId) != null)
                {
                    // ICON ART (2026-10-23): the row only ever drew a placeholder letter. Icons from Spencer's licensed pack
                    // (Assets/AssetsCrafters/... 256x256, copied to Resources/Boosters). No icon file = the letter, as before.
                    bool art = _slotArt[i] != null;
                    float iconSz = slotSize * (art ? ART_ICON_FILL : BOOSTER_ICON_FILL);
                    Vector2 iconAt = art ? new Vector2(0f, (ART_CY - ART_FACE_CY) * slotSize / ART_D) + IconShift(spec.BoosterId) * slotSize : Vector2.zero;   // face centre (a hair high) + per-icon badge clearance (11-02)
                    var shSprite = art ? LoadBoostersSprite("shadow_" + BoosterIconFile(spec.BoosterId)) : null;
                    if (shSprite != null)
                    {   // two-layer recipe shadow (contact + ambient), pre-baked by make_slot_sprites.py: icon + 2L padding each side
                        var shGO = new GameObject("IconShadow", typeof(RectTransform), typeof(Image));
                        shGO.transform.SetParent(slot.transform, false);
                        var shImg = shGO.GetComponent<Image>(); shImg.sprite = shSprite; shImg.raycastTarget = false;
                        var shRT = (RectTransform)shGO.transform;
                        shRT.anchorMin = shRT.anchorMax = shRT.pivot = new Vector2(0.5f, 0.5f);
                        shRT.sizeDelta = Vector2.one * iconSz * (1f + 4f * 14f / 91f);
                        shRT.anchoredPosition = iconAt;   // the recipe's drop distance is baked into the sprite (make_slot_sprites.py, 10-29)
                    }
                    var iconGO = new GameObject("BoosterIcon", typeof(RectTransform), typeof(Image));
                    iconGO.transform.SetParent(slot.transform, false);
                    var iconImg = iconGO.GetComponent<Image>();
                    iconImg.sprite = BoosterIcon(spec.BoosterId); iconImg.preserveAspect = true; iconImg.raycastTarget = false;
                    // EDGED HUD ICON (2026-10-29, Spencer picked "B"): Boosters/slot_<icon>.png = the icon with a thin dark
                    // icon-coloured core + short halo (vector_buttons/edge/icon_edge.py), on a 288 canvas (256 + 16 each side) so the
                    // art keeps its size and centre. HUD only - the booster animations use the original (measured) icons.
                    float iconBox = iconSz;
                    var edged = art ? LoadBoostersSprite("slot_" + BoosterIconFile(spec.BoosterId)) : null;
                    if (edged != null) { iconImg.sprite = edged; iconBox = iconSz * 288f / 256f; }
                    var iconRT = iconGO.GetComponent<RectTransform>();
                    iconRT.anchorMin = iconRT.anchorMax = iconRT.pivot = new Vector2(0.5f, 0.5f);
                    iconRT.sizeDelta = new Vector2(iconBox, iconBox);
                    iconRT.anchoredPosition = iconAt;
                    _slotLabels[i] = null;
                }
                else
                {
                    var labelGO = new GameObject("Label", typeof(RectTransform));
                    labelGO.transform.SetParent(slot.transform, false);
                    _slotLabels[i] = labelGO.AddComponent<TextMeshProUGUI>();
                    if (displayFont != null) _slotLabels[i].font = displayFont;
                    _slotLabels[i].text = spec.PlaceholderChar;
                    _slotLabels[i].fontSize = 52;
                    _slotLabels[i].alignment = TextAlignmentOptions.Center;
                    _slotLabels[i].color = new Color(1f, 0.84f, 0.42f, 1f);
                    _slotLabels[i].raycastTarget = false;
                    var labelRT = labelGO.GetComponent<RectTransform>();
                    labelRT.anchorMin = Vector2.zero;
                    labelRT.anchorMax = Vector2.one;
                    labelRT.offsetMin = Vector2.zero;
                    labelRT.offsetMax = Vector2.zero;
                }

                // Charge badge — boosters and TileBag have one. Edit now uses the EDITS pip
                // panel instead of a numeric badge; Settings has none.
                bool needsBadge = spec.Type == SlotType.Booster
                              || spec.Type == SlotType.TileBag;
                if (needsBadge)
                {
                    var badgeGO = new GameObject("Badge",
                        typeof(RectTransform), typeof(Image));
                    badgeGO.transform.SetParent(slot.transform, false);
                    _chargeBadgeContainers[i] = badgeGO;
                    var badgeRT = badgeGO.GetComponent<RectTransform>();
                    badgeRT.anchorMin = new Vector2(0.5f, 0.5f);
                    badgeRT.anchorMax = new Vector2(0.5f, 0.5f);
                    badgeRT.pivot     = new Vector2(0.5f, 0.5f);
                    badgeRT.sizeDelta = new Vector2(BADGE_SIZE, BADGE_SIZE);
                    badgeRT.anchoredPosition = new Vector2(BADGE_OFFSET_X, BADGE_OFFSET_Y) * (HUDManager.MOCKUP_HUD ? slotSize / MK_BOOSTER_SIZE : 1f);   // 2026-10-29: tracks the slot size
                    var badgeImg = badgeGO.GetComponent<Image>();
                    badgeImg.sprite = GetCircleSpriteSmall();
                    badgeImg.color  = new Color(0.20f, 0.58f, 0.14f, 1f); // 2026-06-24 Spencer: darker green than SCORED_TINT so the white number reads (bright green washed it out)
                    badgeImg.raycastTarget = false;
                    bool badgeArt = _slotArt[i] != null && ButtonArt("booster_badge") != null;
                    if (badgeArt)
                    {   // round green v3 badge on the rim's lower right (2026-10-29)
                        float bd = slotSize * ART_BADGE_FRAC;
                        badgeRT.sizeDelta = new Vector2(bd, bd);
                        badgeRT.anchoredPosition = new Vector2(ART_BADGE_POS_X, -ART_BADGE_POS_Y) * slotSize;
                        badgeImg.color = new Color(1f, 1f, 1f, 0f);
                        var baGO = new GameObject("BadgeArt", typeof(RectTransform), typeof(Image));
                        baGO.transform.SetParent(badgeGO.transform, false);
                        PlaceArt((RectTransform)baGO.transform, bd);
                        var baImg = baGO.GetComponent<Image>(); baImg.sprite = ButtonArt("booster_badge"); baImg.raycastTarget = false;
                    }

                    var chargeGO = new GameObject("Charge", typeof(RectTransform));
                    chargeGO.transform.SetParent(badgeGO.transform, false);
                    _chargeBadges[i] = chargeGO.AddComponent<TextMeshProUGUI>();
                    if (displayFont != null) _chargeBadges[i].font = displayFont;
                    _chargeBadges[i].text = "0";
                    _chargeBadges[i].fontSize = 29; // was 44 — scaled with the smaller badge
                    _chargeBadges[i].alignment = TextAlignmentOptions.Center;
                    _chargeBadges[i].color = Color.white;
                    _chargeBadges[i].raycastTarget = false;
                    if (badgeArt)
                    {   // cream number with a dark-green ink ring, like the button labels
                        float bd = slotSize * ART_BADGE_FRAC;
                        _chargeBadges[i].fontSize = bd * 0.62f;
                        // 11-02 (Spencer: "somethings going on with the numbers in the badge"): the 0.25 ink ring grew INTO the heavy
                        // glyphs and closed the counters (the 9's hole became a dot) -> BADGE_OUTLINE; and two digits (97) crowded the
                        // rim -> auto-size inside a side margin, so one digit keeps the full size and two digits shrink to fit.
                        _chargeBadges[i].enableAutoSizing = true; _chargeBadges[i].fontSizeMax = bd * 0.62f; _chargeBadges[i].fontSizeMin = bd * 0.30f;
                        _chargeBadges[i].margin = new Vector4(bd * BADGE_TEXT_MARGIN, 0f, bd * BADGE_TEXT_MARGIN, 0f);
                        _chargeBadges[i].color = new Color32(0xFF, 0xF8, 0xD3, 0xFF);
                        _chargeBadges[i].outlineWidth = BADGE_OUTLINE;
                        _chargeBadges[i].outlineColor = new Color32(0x46, 0x04, 0x0C, 0xFF);   // dark crimson ink on the red badge (10-29, Spencer: red)
                        RkText.Firm(_chargeBadges[i]);   // 11-18: the firmer small-text edge (his pick from badge_numbers_mock.png)
                    }
                    var chargeRT = chargeGO.GetComponent<RectTransform>();
                    chargeRT.anchorMin = Vector2.zero;
                    chargeRT.anchorMax = Vector2.one;
                    chargeRT.offsetMin = Vector2.zero;
                    chargeRT.offsetMax = Vector2.zero;
                }

                // ── Onboarding lock overlay ──────────────────────────────────────
                // Locked tools (Edit/Bag/boosters in early levels) show a padlock and
                // ignore taps. Added last so it renders above the icon/badge. 2026-06-09.
                if (IsSlotLocked(spec))
                {
                    slotBtn.interactable = spec.Type == SlotType.Booster;   // 10-29: a locked BOOSTER is tappable -> 'Unlocks at Level N'
                    // Locked tools show ONLY the lock — hide the icon/letter/badge beneath it.
                    for (int ch = slot.transform.childCount - 1; ch >= 0; ch--)
                        if (slot.transform.GetChild(ch).name != "SlotArt") slot.transform.GetChild(ch).gameObject.SetActive(false);   // the round art stays (locked sprite)
                    if (_slotArt[i] != null) _slotArt[i].sprite = LockedSlotArt();
                    // LOCKED v2 (2026-10-29, Spencer OK'd composition mock): a greyscale GHOST of the booster's icon stays on the light
                    // grey-lavender face ("coming soon"), the padlock shrinks to a corner badge
                    var ghostIcon = slot.transform.Find("BoosterIcon");
                    var ghostSp = spec.Type == SlotType.Booster && _slotArt[i] != null ? LoadBoostersSprite("slot_" + BoosterIconFile(spec.BoosterId) + "_ghost") : null;
                    if (ghostIcon != null && ghostSp != null && !LOCK_CENTRED) { ghostIcon.gameObject.SetActive(true); var gi = ghostIcon.GetComponent<Image>(); gi.sprite = ghostSp; if (LOCKED_IN_COLOUR) gi.color = LOCKED_GHOST_TINT; }

                    var lockGO = new GameObject("LockOverlay", typeof(RectTransform), typeof(Image));
                    lockGO.transform.SetParent(slot.transform, false);
                    var lockRT = lockGO.GetComponent<RectTransform>();
                    lockRT.anchorMin = lockRT.anchorMax = new Vector2(0.5f, 0.5f);
                    lockRT.pivot     = new Vector2(0.5f, 0.5f);
                    float lockSz = HUDManager.MOCKUP_HUD ? slotSize * 0.70f : SLOT_SIZE * 0.62f;   // 2026-10-29: scales with the (now count-sized) slot
                    lockRT.sizeDelta = new Vector2(lockSz, lockSz);
                    lockRT.anchoredPosition = Vector2.zero;
                    var lockImg = lockGO.GetComponent<Image>();
                    lockImg.sprite         = GetLockSprite();
                    if (_slotArt[i] != null && LoadBoostersSprite("lock_closed_gold") != null)
                    {   // round art: the GOLD padlock pair (closed 1lock / open 1lock_01) reads on the blue face; centred by its outline
                        lockImg.sprite = LockArt(false, out float lockMul);
                        lockRT.sizeDelta = Vector2.one * slotSize * (LOCK_CENTRED ? LOCK_CENTRED_SIZE : ART_LOCK_CORNER) * lockMul;
                        lockRT.anchoredPosition = LOCK_CENTRED ? new Vector2(0f, (ART_CY - ART_FACE_CY) * slotSize / ART_D) : new Vector2(ART_LOCK_CX, ART_LOCK_CY) * slotSize;
                    }
                    lockImg.preserveAspect = true;
                    lockImg.raycastTarget  = false;
                }
            }
        }

        private static Color SlotBaseColor(SlotType type)
        {
            switch (type)
            {
                case SlotType.Edit:     return new Color(0.08f, 0.38f, 0.48f, 0.95f); // teal panel (matches bench)
                case SlotType.TileBag:  return new Color(0.18f, 0.25f, 0.20f, 0.92f);
                case SlotType.Settings: return new Color(0.20f, 0.18f, 0.22f, 0.92f);
                default:                return new Color(0.18f, 0.12f, 0.28f, 0.92f);
            }
        }

        /// <summary>Builds the "EDITS" pip panel inside the Edit slot: a title label + a row of
        /// EDIT_PIP_COUNT gold pips (charge readout). Pips fill/empty with the rewrite count and
        /// pop with an additive glow when an edit is RESTORED. 2026-06-24 Spencer.</summary>
        private void BuildEditsPanel(GameObject slot, TMP_FontAsset font)
        {
            // 2026-09-03 swap-pivot: swaps are UNLIMITED now, so the pip readout has
            // nothing to report. Gated rather than deleted — flip ShowSwapsPanel back
            // on if a swap budget ever returns.
            if (!ShowSwapsPanel) { if (slot != null) slot.SetActive(false); return; }
            _editPanelSlot = slot;
            _lastPipCount  = -1; // first RefreshEditPips just sets state, no animation
            _editPanelGroup = slot.GetComponent<CanvasGroup>() ?? slot.AddComponent<CanvasGroup>();

            // "EDITS" title (upper portion).
            var lblGO = new GameObject("EditsLabel", typeof(RectTransform));
            lblGO.transform.SetParent(slot.transform, false);
            var lbl = lblGO.AddComponent<TextMeshProUGUI>();
            var uiFont = GameFont.GetUITMP();   // 2026-06-24: match the HUD font (Montserrat-Black), not the Cartoon display font
            if (uiFont != null) lbl.font = uiFont;
            lbl.text = "SWAPS"; // 2026-06-24: edits renamed "swaps" for players (tile-bag swap was cut)
            lbl.fontSize = 24;
            lbl.fontStyle = TMPro.FontStyles.Bold;                       // match the NEXT label (bold)
            lbl.alignment = TextAlignmentOptions.Center;
            lbl.color = new Color(0.78f, 0.78f, 0.88f, 0.95f);          // match the NEXT label colour
            lbl.raycastTarget = false;
            var lrt = lbl.rectTransform;
            lrt.anchorMin = new Vector2(0.05f, 0.50f); lrt.anchorMax = new Vector2(0.95f, 0.94f);
            lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;

            // Pip row (lower portion).
            _editPips     = new Image[EDIT_PIP_COUNT];
            _editPipRects = new RectTransform[EDIT_PIP_COUNT];
            const float pipW = 0.26f, gap = 0.06f;
            float totalW = EDIT_PIP_COUNT * pipW + (EDIT_PIP_COUNT - 1) * gap;
            float startX = (1f - totalW) * 0.5f;
            for (int p = 0; p < EDIT_PIP_COUNT; p++)
            {
                var pipGO = new GameObject($"Pip{p}", typeof(RectTransform), typeof(Image));
                pipGO.transform.SetParent(slot.transform, false);
                var prt = pipGO.GetComponent<RectTransform>();
                float x0 = startX + p * (pipW + gap);
                prt.anchorMin = new Vector2(x0, 0.12f);
                prt.anchorMax = new Vector2(x0 + pipW, 0.42f);
                prt.offsetMin = Vector2.zero; prt.offsetMax = Vector2.zero;
                var pimg = pipGO.GetComponent<Image>();
                pimg.sprite = MenuUI.GetRoundedRectSprite(16);
                pimg.type   = Image.Type.Sliced;
                pimg.color  = PIP_EMPTY;
                pimg.raycastTarget = false;
                _editPips[p]     = pimg;
                _editPipRects[p] = prt;
            }

            // "watch ad for swaps" hint — overlays the pip row, shown only when out of swaps
            // AND a rewarded ad is on offer (see RefreshEditPips). Green = free/positive.
            var hintGO = new GameObject("AdHint", typeof(RectTransform));
            hintGO.transform.SetParent(slot.transform, false);
            _editAdHint = hintGO.AddComponent<TextMeshProUGUI>();
            if (font != null) _editAdHint.font = font;
            _editAdHint.text = "▶ FREE +3";
            _editAdHint.fontSize = 28;
            _editAdHint.alignment = TextAlignmentOptions.Center;
            _editAdHint.color = new Color(0.45f, 1f, 0.45f, 1f);
            _editAdHint.raycastTarget = false;
            var hrt = _editAdHint.rectTransform;
            hrt.anchorMin = new Vector2(0.04f, 0.08f); hrt.anchorMax = new Vector2(0.96f, 0.46f);
            hrt.offsetMin = Vector2.zero; hrt.offsetMax = Vector2.zero;
            hintGO.SetActive(false);
        }

        /// <summary>Refreshes the EDITS pips to reflect the current rewrite count. Newly-FILLED pips
        /// (an edit was restored) pop with an additive gold glow burst.</summary>
        // Tutorial (swap level): lift the SWAPS/EDITS pip panel above the dim scrim so it stays lit + readable
        // while the player learns swaps. A sub-canvas with overrideSorting raises just this panel. 2026-07-08 Spencer.
        private Canvas _editPanelSpotlightCanvas;
        public void SetEditPanelBright(bool bright)
        {
            if (_editPanelSlot == null) return;
            if (bright)
            {
                if (_editPanelSpotlightCanvas == null)
                {
                    _editPanelSpotlightCanvas = _editPanelSlot.GetComponent<Canvas>() ?? _editPanelSlot.AddComponent<Canvas>();
                    if (_editPanelSlot.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null)
                        _editPanelSlot.AddComponent<UnityEngine.UI.GraphicRaycaster>();
                }
                _editPanelSpotlightCanvas.overrideSorting = true;
                _editPanelSpotlightCanvas.sortingOrder = 45; // above the tutorial scrim (40 since the 2026-10-20 re-base)
                _editPanelSpotlightCanvas.enabled = true;
            }
            else if (_editPanelSpotlightCanvas != null)
            {
                _editPanelSpotlightCanvas.overrideSorting = false;
            }
        }

        private void RefreshEditPips(int count)
        {
            if (_editPips == null) return;

            // Locked (early tutorial levels) → show ONLY the padlock. Hide the pips + ad hint and bail:
            // RefreshEditPips runs every frame and would otherwise re-activate the pips, leaving the pink
            // charges visible ON TOP of the lock (inconsistent with how L1–L3 look). 2026-07-06 Spencer.
            if (TutorialLocks.EditLocked)
            {
                for (int p = 0; p < _editPips.Length; p++)
                    if (_editPips[p] != null) _editPips[p].gameObject.SetActive(false);
                if (_editAdHint != null) _editAdHint.gameObject.SetActive(false);
                return;
            }

            // Out of swaps AND a rewarded ad is on offer → show the "watch ad" affordance instead of dim.
            bool adOffer = count <= 0 && IsSwapAdEligible();

            for (int p = 0; p < _editPips.Length; p++)
            {
                bool filled    = p < count;
                bool wasFilled = p < _lastPipCount;
                if (_editPips[p] != null)
                {
                    _editPips[p].gameObject.SetActive(!adOffer); // hide pips behind the ad hint
                    _editPips[p].color = filled ? PIP_FULL : PIP_EMPTY;
                }
                if (filled && !wasFilled && _lastPipCount >= 0)
                    AnimateEditPipGain(p); // restored this pip → celebrate (cyan glow)
            }
            _lastPipCount = count;

            if (_editAdHint != null) _editAdHint.gameObject.SetActive(adOffer);

            // Panel alpha: bright when you have swaps OR when the ad offer is live (inviting);
            // dimmed only when you're out AND no ad is available.
            if (_editPanelGroup != null)
            {
                _editPanelGroup.DOKill();
                _editPanelGroup.DOFade(count > 0 || adOffer ? 1f : 0.45f, 0.2f);
            }

            // Gentle invite pulse while the ad offer is live; resting otherwise.
            if (_editPanelSlot != null)
            {
                _editPulseTween?.Kill();
                var t = _editPanelSlot.transform;
                if (adOffer)
                {
                    t.localScale = Vector3.one;
                    _editPulseTween = t.DOScale(1.06f, 0.6f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
                }
                else t.localScale = Vector3.one;
            }
        }

        /// <summary>True when the player is out of swaps, swaps are unlocked (taught), and a rewarded
        /// ad is available — i.e. we can offer "watch an ad to refill swaps". 2026-06-24 Spencer.</summary>
        private bool IsSwapAdEligible()
        {
            if (TutorialLocks.EditLocked) return false;     // not during onboarding (before swaps are taught)
            if (AdManager.Instance == null) return false;   // no ad system available
            int swaps = MatchController.Instance != null
                ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN) : 0;
            return swaps <= 0;
        }

        /// <summary>Opt-in rewarded ad → refill swaps to the survival cap. Self-limits: refilling takes
        /// you off 0, so the offer only reappears after you've spent them again. 2026-06-24 Spencer.</summary>
        private void OfferSwapAd()
        {
            if (_swapAdInFlight || !IsSwapAdEligible()) return;
            if (AdManager.Instance == null || MatchController.Instance == null) return;
            _swapAdInFlight = true;
            AdManager.Instance.ShowRewardedAd(onRewardGranted: () =>
            {
                // RefundRewriteCharge self-caps at the survival cap (3); call to-cap.
                for (int k = 0; k < EDIT_PIP_COUNT; k++)
                    MatchController.Instance.RefundRewriteCharge(MatchController.PLAYER_HUMAN);
                GameAudio.Instance?.PlayPersonalBest();
                _swapAdInFlight = false; // a real async SDK should also reset on ad-failed
            });
        }

        /// <summary>Pop + additive-glow burst on a pip that just got restored.</summary>
        private void AnimateEditPipGain(int p)
        {
            if (_editPipRects == null || p < 0 || p >= _editPipRects.Length) return;
            var rt = _editPipRects[p];
            if (rt == null) return;
            rt.DOKill();
            rt.localScale = Vector3.one * 0.45f;
            rt.DOScale(1f, 0.34f).SetEase(Ease.OutBack, 3.2f);
            SpawnEditPipGlow(rt);
        }

        /// <summary>Fake-bloom: an additive cyan glow behind the pip that expands and fades out.
        /// Over the dark navy panel the additive blend reads as a real glow. Self-destroys.</summary>
        private void SpawnEditPipGlow(RectTransform pip)
        {
            if (_editPanelSlot == null || pip == null) return;
            Sprite glow = LoadGlowSprite();
            if (glow == null) return;
            var gGO = new GameObject("PipGlow", typeof(RectTransform), typeof(Image));
            gGO.transform.SetParent(_editPanelSlot.transform, false);
            gGO.transform.SetSiblingIndex(0); // behind the pips
            var grt = (RectTransform)gGO.transform;
            grt.anchorMin = pip.anchorMin; grt.anchorMax = pip.anchorMax;
            grt.offsetMin = new Vector2(-34f, -34f); grt.offsetMax = new Vector2(34f, 34f); // spill past the pip
            var gimg = gGO.GetComponent<Image>();
            gimg.sprite = glow;
            gimg.color  = new Color(0.35f, 0.98f, 1.12f, 0.95f); // cyan = "swap charge restored" — matches the swap edit-glow
            gimg.raycastTarget = false;
            var addMat = LoadAdditiveGlowMaterial();
            if (addMat != null) gimg.material = addMat;
            grt.localScale = Vector3.one * 0.5f;
            var sq = DOTween.Sequence();
            sq.Append(grt.DOScale(1.7f, 0.48f).SetEase(Ease.OutCubic));
            sq.Join(gimg.DOFade(0f, 0.48f).SetEase(Ease.InQuad));
            sq.OnComplete(() => { if (gGO != null) Destroy(gGO); });
        }

        // ── Aim-mode canvas ─────────────────────────────────────────────────────

        private void BuildAimCanvas()
        {
            var canvasGO = new GameObject("BoosterAimCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _aimCanvas = canvasGO.GetComponent<Canvas>();
            _aimCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _aimCanvas.sortingOrder = AIM_CANVAS_ORDER;
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1.0f;

            // 2026-06-01: dark blue background panel removed per Spencer.
            // Banner rect stays as the layout container (so the centered text
            // child still positions correctly), but its Image is transparent
            // and the anchoredPosition is pushed down so the text sits near
            // the same Y as where the +WORDS / scored-word popup renders —
            // roughly the gap between the HUD bar and the top of the board.
            var bannerGO = new GameObject("Banner",
                typeof(RectTransform), typeof(Image));
            bannerGO.transform.SetParent(canvasGO.transform, false);
            var bannerRT = bannerGO.GetComponent<RectTransform>();
            bannerRT.anchorMin = new Vector2(0f, 1f);
            bannerRT.anchorMax = new Vector2(1f, 1f);
            bannerRT.pivot     = new Vector2(0.5f, 1f);
            bannerRT.sizeDelta = new Vector2(0f, 220f);
            bannerRT.anchoredPosition = new Vector2(0f, -200f); // centers the text in the gap between the HUD bar (~y=180 PSD) and the top of the board panel (~y=446 PSD) — Spencer 2026-06-01
            bannerGO.GetComponent<Image>().color = Color.clear;
            bannerGO.GetComponent<Image>().raycastTarget = false;

            var labelGO = new GameObject("Text", typeof(RectTransform));
            labelGO.transform.SetParent(bannerGO.transform, false);
            _aimBanner = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _aimBanner.font = displayFont;
            _aimBanner.text = "TAP A TILE";
            _aimBanner.fontSize = 82;
            _aimBanner.alignment = TextAlignmentOptions.Center;
            _aimBanner.color = new Color(1f, 0.84f, 0.42f, 1f);
            _aimBanner.raycastTarget = false;
            if (!AIM_BANNER) labelGO.SetActive(false);   // 11-07 Spencer "whats this tap a tile thing?" - June banner now lands on the Goals panel
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            // 2026-06-01: banner cancel button removed per Spencer. Cancel now
            // happens by tapping the armed booster's slot directly (which
            // RefreshDisplay re-styles as a red ✕ during aim mode), matching
            // the tile-bag swap-mode UX. _aimCancelButton stays null —
            // _aimTapHandler's rectangle-contains check guards against null.

            _aimCanvas.gameObject.SetActive(false);
        }

        // ── Tap-to-swap overlay (scrim + X over bag) ─────────────────────────────

        /// <summary>Build the swap-mode canvas lazily. ScreenSpaceCamera mode
        /// at sortingOrder=9 sits ABOVE the booster bench (sortingOrder=8) but
        /// BELOW the hand cards (sortingOrder=10) so the cards stay bright
        /// against the dim scrim. HUD/modal canvases stay above the scrim
        /// because they're ScreenSpaceOverlay (always renders over scene).</summary>
        private void BuildSwapModeCanvas()
        {
            if (_swapModeCanvas != null) return;

            var canvasGO = new GameObject("TileBagSwapModeCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _swapModeCanvas = canvasGO.GetComponent<Canvas>();
            _swapModeCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _swapModeCanvas.worldCamera = Camera.main;
            _swapModeCanvas.planeDistance = 2f;
            _swapModeCanvas.sortingOrder = 9;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1f;

            // Full-screen scrim image. raycastTarget=true so taps on the dim
            // area are absorbed (don't accidentally reach the board behind).
            _swapModeScrim = new GameObject("Scrim", typeof(RectTransform), typeof(Image));
            _swapModeScrim.transform.SetParent(canvasGO.transform, false);
            var scrimRT = _swapModeScrim.GetComponent<RectTransform>();
            scrimRT.anchorMin = Vector2.zero;
            scrimRT.anchorMax = Vector2.one;
            scrimRT.offsetMin = Vector2.zero;
            scrimRT.offsetMax = Vector2.zero;
            var scrimImg = _swapModeScrim.GetComponent<Image>();
            scrimImg.color = new Color(0f, 0f, 0f, 0.72f);
            scrimImg.raycastTarget = true;

            // X button positioned over the TileBag slot. Anchored from the
            // bottom of the screen to match the bag slot's anchor scheme so
            // it sits exactly where the bag is.
            _swapModeXButton = new GameObject("CancelX",
                typeof(RectTransform), typeof(Image), typeof(Button));
            _swapModeXButton.transform.SetParent(canvasGO.transform, false);
            var xRT = _swapModeXButton.GetComponent<RectTransform>();
            xRT.anchorMin = new Vector2(0.5f, 0f);
            xRT.anchorMax = new Vector2(0.5f, 0f);
            xRT.pivot     = new Vector2(0.5f, 0.5f);
            xRT.sizeDelta = new Vector2(FLANK_SIZE, FLANK_SIZE); // matches the (smaller) bag slot it overlays
            // Line the X up over the TileBag slot's ACTUAL position (the bag now flanks
            // the tile holder rather than sitting in the bench row). Read the built slot's
            // anchoredPosition directly; fall back to the computed flank position.
            int bagIndex = 0;
            for (int i = 0; i < DISPLAY_ORDER.Length; i++)
                if (DISPLAY_ORDER[i].Type == SlotType.TileBag) { bagIndex = i; break; }
            Vector2 bagAnchored = (_slotButtons != null && bagIndex < _slotButtons.Length && _slotButtons[bagIndex] != null)
                ? _slotButtons[bagIndex].GetComponent<RectTransform>().anchoredPosition
                : PsdAnchoredCenter(BAG_X_PSD, FLANK_Y_PSD, SLOT_SIZE, SLOT_SIZE);
            xRT.anchoredPosition = bagAnchored;

            var xImg = _swapModeXButton.GetComponent<Image>();
            xImg.sprite = GetCircleSpriteBig();
            xImg.color  = new Color(0.50f, 0.18f, 0.18f, 0.95f); // muted red

            // X glyph on top of the button
            var xGlyphGO = new GameObject("Glyph", typeof(RectTransform));
            xGlyphGO.transform.SetParent(_swapModeXButton.transform, false);
            var xGlyphRT = xGlyphGO.GetComponent<RectTransform>();
            xGlyphRT.anchorMin = Vector2.zero;
            xGlyphRT.anchorMax = Vector2.one;
            xGlyphRT.offsetMin = Vector2.zero;
            xGlyphRT.offsetMax = Vector2.zero;
            var xText = xGlyphGO.AddComponent<TextMeshProUGUI>();
            xText.text = "✕";
            xText.alignment = TextAlignmentOptions.Center;
            xText.fontSize = 56;
            xText.color = Color.white;
            xText.raycastTarget = false;
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) xText.font = displayFont;

            // 2026-06-01: same split multi-pop SFX as the SettingsModal close
            // button (SettingsModal.BuildCloseButton). PointerDown plays the
            // press half, onClick plays the release half. Spencer asked for
            // the swap-mode X to "have the exact sound" as the settings X.
            var xTrigger = _swapModeXButton.GetComponent<EventTrigger>();
            if (xTrigger == null) xTrigger = _swapModeXButton.AddComponent<EventTrigger>();
            var xDownEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            xDownEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            xTrigger.triggers.Add(xDownEntry);

            _swapModeXButton.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                ExitTileBagSwapMode();
            });

            _swapModeCanvas.gameObject.SetActive(false);
        }

        // ── Booster aim-mode scrim helpers ──────────────────────────────────────

        /// <summary>Build the aim-mode scrim canvas lazily. Two Image
        /// rectangles cover the area ABOVE the board and BELOW the board,
        /// leaving a CUTOUT for the board itself. The board panel + tiles
        /// stay at their natural sortingOrder and are simply not covered by
        /// the scrim — which avoids fighting Tile.cs's internal SetSortingOrder
        /// resets during animations. raycastTarget=false on both rects so
        /// taps pass through to BoosterHUDSlot.Update's aim-tap handler.</summary>
        private void BuildAimScrimCanvas()
        {
            if (_aimScrimCanvas != null) return;

            var canvasGO = new GameObject("BoosterAimScrimCanvas",
                typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _aimScrimCanvas = canvasGO.GetComponent<Canvas>();
            _aimScrimCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _aimScrimCanvas.worldCamera = Camera.main;
            _aimScrimCanvas.planeDistance = 2f;
            _aimScrimCanvas.sortingOrder = AIM_SCRIM_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(CANVAS_W, CANVAS_H);
            scaler.matchWidthOrHeight = 1f;

            Color scrimColor = new Color(0f, 0f, 0f, 0.72f);

            // Four-rectangle frame around the board. Anchors are placeholders
            // at build time — the actual cutout is computed from the board
            // background SR's world bounds in UpdateScrimGeometry, called on
            // every aim-mode entry. This way the cutout always matches the
            // ACTUAL rendered board panel, regardless of PSD vs runtime drift.
            _aimScrimTop    = MakeScrimRect(canvasGO.transform, "ScrimTop",    Vector2.zero, Vector2.one, scrimColor);
            _aimScrimBottom = MakeScrimRect(canvasGO.transform, "ScrimBottom", Vector2.zero, Vector2.one, scrimColor);
            _aimScrimLeft   = MakeScrimRect(canvasGO.transform, "ScrimLeft",   Vector2.zero, Vector2.one, scrimColor);
            _aimScrimRight  = MakeScrimRect(canvasGO.transform, "ScrimRight",  Vector2.zero, Vector2.one, scrimColor);

            _aimScrimCanvas.gameObject.SetActive(false);
        }

        /// <summary>Snap the 4 scrim rects to frame the board's actual world
        /// bounds. Called when aim mode activates so the cutout precisely
        /// matches whatever the lavender board panel renders at — width,
        /// height, position — without hardcoded PSD constants.</summary>
        private void UpdateScrimGeometry()
        {
            var grid = GridManager.Instance;
            var cam  = Camera.main;
            if (grid == null || cam == null) return;

            Bounds? b = grid.BoardBackgroundWorldBounds;
            if (!b.HasValue) return;

            // World-space corners → viewport (0-1) coords. For a ScreenSpaceCamera
            // canvas the viewport coords map directly to anchor coords (0,0 =
            // bottom-left of viewport, 1,1 = top-right). Orthographic camera
            // means the conversion is a clean affine map.
            Vector3 minVP = cam.WorldToViewportPoint(b.Value.min);
            Vector3 maxVP = cam.WorldToViewportPoint(b.Value.max);

            float left   = Mathf.Clamp01(minVP.x);
            float right  = Mathf.Clamp01(maxVP.x);
            float bottom = Mathf.Clamp01(minVP.y);
            float top    = Mathf.Clamp01(maxVP.y);

            SetRectAnchors(_aimScrimTop,    new Vector2(0f,    top),    new Vector2(1f,    1f));
            SetRectAnchors(_aimScrimBottom, new Vector2(0f,    0f),     new Vector2(1f,    bottom));
            SetRectAnchors(_aimScrimLeft,   new Vector2(0f,    bottom), new Vector2(left,  top));
            SetRectAnchors(_aimScrimRight,  new Vector2(right, bottom), new Vector2(1f,    top));
        }

        private static void SetRectAnchors(GameObject go, Vector2 anchorMin, Vector2 anchorMax)
        {
            if (go == null) return;
            var rt = go.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static GameObject MakeScrimRect(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // taps pass through to the aim handler
            return go;
        }

        /// <summary>Toggle the cutout scrim + armed-slot sort override.
        /// Called from RefreshDisplay when the cached _wasAim flag disagrees
        /// with the live state.
        ///
        /// Bright during aim (uncovered by scrim): the entire board area —
        /// background panel, tiles, board background sprites. No per-element
        /// sortingOrder manipulation required (which previously fought Tile.cs's
        /// internal SetSortingOrder resets during tile animations).
        /// Dim during aim (covered by scrim): HUD area, the "TAP A TILE"
        /// banner gap, hand row, NEXT preview, bench panel + un-armed slots.
        /// Armed booster slot uses Canvas.overrideSorting to stay bright on
        /// top of the bottom scrim so the red ✕ cancel reads.</summary>
        private void SetAimModeVisuals(bool active)
        {
            BuildAimScrimCanvas();
            // Full-screen scrim now; corners-of-board are handled by bumping the
            // board panel's rounded sprite ABOVE the scrim's sortingOrder, so
            // the panel itself naturally clips out only its rounded shape
            // (slivers stay dim with the scrim, panel area is bright).
            if (active) StretchScrimsFullScreen();
            if (_aimScrimCanvas != null) _aimScrimCanvas.gameObject.SetActive(active);

            // Bump board background + all tiles above the scrim so they
            // render bright inside the panel cutout. The board bg's rounded
            // sprite shape provides the perfect cutout for free — the corner
            // slivers (where the bg has 0 alpha) stay dim with the scrim.
            var grid = GridManager.Instance;
            if (grid != null)
            {
                grid.SetBoardBackgroundSortingOrder(active ? AIM_BOARD_BG_ORDER : 0);
                Tile.AimModeTileOrder = active ? AIM_TILE_ORDER : 0;
                // Same fix as the tutorial spotlight: lift, but keep the per-row bands so
                // stacked tiles still occlude correctly in aim mode. 2026-09-10 Spencer.
                for (int c = 0; c < RulesEngine.COLS; c++)
                {
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                    {
                        var tile = grid.GetTile(c, r);
                        if (tile != null) tile.ReassertSortingOrder();
                    }
                }
            }

            ApplyArmedSlotSortOverride(active);

            // The "TAP A TILE" banner sits in the same slot as the last-word
            // score readout ("TOE +7"); shown together they overlap and read as
            // jumbled text. Clear the score readout while aim mode is up — it
            // repopulates on its own the next time a word scores after aim exits.
            if (active) LastWordDisplay.Instance?.Clear();
        }

        private const int AIM_BOARD_BG_ORDER = 17; // above scrim (15), below tiles
        private const int AIM_TILE_ORDER     = 70; // 2026-09-03: above the per-row tile bands (was 25)

        /// <summary>Set up the scrim as a single full-screen dim layer.
        /// Uses _aimScrimTop as the sole active rect; the other 3 (Bottom /
        /// Left / Right) are hidden so the 72% alpha doesn't stack 4×
        /// (which would compound to ~99% opacity and read as pure black).
        /// The cutout for the board area comes from bumping the board panel
        /// + tiles above the scrim's sortingOrder, not from anchor geometry.</summary>
        private void StretchScrimsFullScreen()
        {
            SetRectAnchors(_aimScrimTop, Vector2.zero, Vector2.one);
            if (_aimScrimBottom != null) _aimScrimBottom.SetActive(false);
            if (_aimScrimLeft   != null) _aimScrimLeft.SetActive(false);
            if (_aimScrimRight  != null) _aimScrimRight.SetActive(false);
        }

        /// <summary>Use Canvas.overrideSorting on the armed booster's slot
        /// GameObject so it renders above the aim scrim. On exit, disable
        /// the override and the slot reverts to the bench canvas's order.</summary>
        private void ApplyArmedSlotSortOverride(bool active)
        {
            if (_slotButtons == null) return;
            for (int i = 0; i < _slotButtons.Length && i < DISPLAY_ORDER.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var spec = DISPLAY_ORDER[i];
                if (spec.Type != SlotType.Booster) continue;

                var bm = BoosterManager.Instance;
                bool isArmed = active && bm != null && bm.ArmedBooster != null
                                       && spec.BoosterId == bm.ArmedBooster.Id;

                var subCanvas = _slotButtons[i].GetComponent<Canvas>();
                if (isArmed)
                {
                    if (subCanvas == null) subCanvas = _slotButtons[i].AddComponent<Canvas>();
                    subCanvas.overrideSorting = true;
                    subCanvas.sortingOrder = ARMED_SLOT_SORT_ORDER; // above the scrim's sortingOrder
                    // GraphicRaycaster needed so the Button still receives
                    // taps under the new sub-canvas. Idempotent: AddComponent
                    // returns the existing one if already present.
                    if (_slotButtons[i].GetComponent<GraphicRaycaster>() == null)
                        _slotButtons[i].AddComponent<GraphicRaycaster>();
                }
                else if (subCanvas != null)
                {
                    subCanvas.overrideSorting = false;
                }
            }
        }

        /// <summary>Enter tap-to-swap mode. Caller must verify swaps remaining > 0.
        /// Shows the dim scrim + X over the bag, and tells HandManager to
        /// start the card-pulse + listen-for-card-tap behaviour.</summary>
        public void EnterTileBagSwapMode()
        {
            if (TileBagSwapModeActive) return;
            BuildSwapModeCanvas();
            if (_swapModeCanvas != null) _swapModeCanvas.gameObject.SetActive(true);
            TileBagSwapModeActive = true;
            // 2026-06-01: PlayUIClick removed — the slot tap that invoked
            // this already fired PlaySettingsRelease in OnSlotTapped.
            HandManager.Instance?.EnterTapToSwapMode();
        }

        /// <summary>Exit tap-to-swap mode. Called by the X button, by
        /// HandManager after a successful swap, or by any external code path
        /// that wants to bail (e.g. modal-open guards).</summary>
        public void ExitTileBagSwapMode()
        {
            if (!TileBagSwapModeActive) return;
            TileBagSwapModeActive = false;
            if (_swapModeCanvas != null) _swapModeCanvas.gameObject.SetActive(false);
            HandManager.Instance?.ExitTapToSwapMode();
        }

        // ── Refresh ─────────────────────────────────────────────────────────────

        // GROWING BOOSTER ROW (2026-11-01, Spencer picked option 1: "five grey padlocked slots ... dead heavy shapes"). Only unlocked
        // boosters + the NEXT locked one (a teaser) show; the visible group (Settings + boosters) is re-centred, so the row grows as
        // slots open (Royal Match). ROW_GROWS = false -> every slot, locked or not, as before.
        public static bool ROW_GROWS = false;   // 11-02 Spencer: "go back to all the spots please" (seeing the new HUD) - every slot shows, locked ones padlocked
        private int _rowVisibleCount = -1; private float[] _slotBaseX;
        private void ApplyGrowingRow()
        {
            if (_slotButtons == null || _slotRestPositions == null || !_restPositionsCached) return;
            int open = TutorialLocks.BoosterSlotsOpen();
            int visible = 0;
            for (int i = 0; i < DISPLAY_ORDER.Length && i < _slotButtons.Length; i++)
            {
                var spec = DISPLAY_ORDER[i]; if (_slotButtons[i] == null || spec.Type != SlotType.Booster) continue;
                bool show = !ROW_GROWS || BoosterOrdinal(spec.BoosterId) <= open;   // unlocked + the next one
                if (_slotButtons[i].activeSelf != show) _slotButtons[i].SetActive(show);
                if (show) visible++;
            }
            if (visible == _rowVisibleCount) return;
            _rowVisibleCount = visible;
            if (_slotBaseX == null || _slotBaseX.Length != _slotRestPositions.Length)
            { _slotBaseX = new float[_slotRestPositions.Length]; for (int i = 0; i < _slotBaseX.Length; i++) _slotBaseX[i] = _slotRestPositions[i].x; }
            // centre the visible group (Settings + shown boosters) on the screen
            float minX = float.MaxValue, maxX = float.MinValue, allMin = float.MaxValue, allMax = float.MinValue;
            for (int i = 0; i < _slotButtons.Length && i < _slotBaseX.Length; i++)
            {
                if (_slotButtons[i] == null || _slotButtons[i] == _editPanelSlot) continue;
                float x = _slotBaseX[i]; allMin = Mathf.Min(allMin, x); allMax = Mathf.Max(allMax, x);
                if (!_slotButtons[i].activeSelf) continue; minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            }
            if (minX > maxX) return;
            float dx = ROW_GROWS ? ((allMin + allMax) * 0.5f - (minX + maxX) * 0.5f) : 0f;
            for (int i = 0; i < _slotButtons.Length && i < _slotBaseX.Length; i++)
            {
                if (_slotButtons[i] == null || _slotButtons[i] == _editPanelSlot) continue;
                _slotRestPositions[i].x = _slotBaseX[i] + dx;
                var rt = _slotButtons[i].GetComponent<RectTransform>(); if (rt != null) rt.anchoredPosition = new Vector2(_slotRestPositions[i].x, rt.anchoredPosition.y);
            }
        }

        private void RefreshDisplay()
        {
            var bm = BoosterManager.Instance;
            bool isAim = bm != null && bm.AimMode;
            ApplyGrowingRow();

            for (int i = 0; i < DISPLAY_ORDER.Length; i++)
            {
                var spec = DISPLAY_ORDER[i];

                int charges = 0;
                bool slotActive = true;

                switch (spec.Type)
                {
                    case SlotType.Booster:
                    {
                        Booster booster = bm != null ? bm.GetBoosterById(spec.BoosterId) : null;
                        slotActive = booster != null;
                        charges = bm != null ? bm.GetCharges(spec.BoosterId) : 0;
                        if (slotActive && _slotLabels[i] != null && booster != null)
                            _slotLabels[i].text = booster.DisplayName.Substring(0, 1);
                        break;
                    }
                    case SlotType.Edit:
                        // 2026-09-03 swap-pivot: swaps are unlimited, so the EDITS/SWAPS
                        // pill has nothing to report. This refresh pass re-activates every
                        // slot each tick, so hiding it at BUILD time was not enough — it
                        // has to be suppressed HERE too.
                        if (!ShowSwapsPanel) { slotActive = false; break; }
                        charges = MatchController.Instance != null
                            ? MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN)
                            : 0;
                        break;
                    case SlotType.TileBag:
                        // 2026-06-01: source from MatchController's live swap
                        // counter so the badge actually decrements when a tile
                        // is swapped via drag-onto-bag. Was hardcoded to 2.
                        charges = MatchController.Instance != null
                            ? MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN)
                            : 0;
                        break;
                    case SlotType.Settings:
                        break;
                }

                if (_slotButtons[i] != null)
                    if (ROW_GROWS && spec.Type == SlotType.Booster && BoosterOrdinal(spec.BoosterId) > TutorialLocks.BoosterSlotsOpen()) slotActive = false;   // growing row: hidden past the teaser
                    _slotButtons[i].SetActive(slotActive);
                if (!slotActive) continue;

                // Charge badge — Royal-Match-style: number when charges > 0,
                // "+" when 0 (acts as a buy-more indicator). Settings has none.
                if (_chargeBadges[i] != null)
                    _chargeBadges[i].text = charges > 0 ? charges.ToString() : "+";
                // round art: RED count badge, DARK GREEN "+" (out of charges -> shop) badge (2026-10-29 Spencer)
                if (_chargeBadgeContainers[i] != null && _chargeBadges[i] != null)
                {
                    var ba = _chargeBadgeContainers[i].transform.Find("BadgeArt");
                    if (ba != null)
                    {
                        bool plus = charges <= 0;
                        var img = ba.GetComponent<Image>();
                        var want = ButtonArt(plus ? "booster_badge_plus" : "booster_badge");
                        if (img != null && want != null && img.sprite != want) img.sprite = want;
                        // 11-18: only when the colour really changes - the setter swaps the text back to its own material, which
                        // RkText then has to restyle (one frame of the plain digit on every refresh otherwise)
                        Color32 ink = plus ? new Color32(0x08, 0x22, 0x06, 0xFF) : new Color32(0x46, 0x04, 0x0C, 0xFF);
                        var badgeMat = _chargeBadges[i].fontSharedMaterial; bool recolour = true;
                        if (badgeMat != null && badgeMat.HasProperty("_OutlineColor"))
                        { Color32 cur = badgeMat.GetColor("_OutlineColor"); recolour = cur.r != ink.r || cur.g != ink.g || cur.b != ink.b; }
                        if (recolour) _chargeBadges[i].outlineColor = ink;
                    }
                }
                if (_chargeBadgeContainers[i] != null)
                {
                    bool showBadge = (spec.Type == SlotType.Booster
                                  || spec.Type == SlotType.Edit
                                  || spec.Type == SlotType.TileBag)
                                  && !IsSlotLocked(spec); // locked tools show no charge badge
                    _chargeBadgeContainers[i].SetActive(showBadge);
                }

                var btn = _slotButtons[i].GetComponent<Button>();

                // 2026-06-01: Edit icon is non-interactable for now (Commit 3
                // wiring still pending — tapping it does nothing useful, so
                // Spencer wants it un-clickable to avoid implying functionality).
                bool isEditSlot   = spec.Type == SlotType.Edit;
                bool isArmedSlot  = isAim && spec.Type == SlotType.Booster
                                          && bm.ArmedBooster != null
                                          && spec.BoosterId == bm.ArmedBooster.Id;

                if (btn != null)
                {
                    if (IsSlotLocked(spec))         btn.interactable = spec.Type == SlotType.Booster && !isAim; // onboarding lock (boosters: tappable for the unlock tooltip)
                    else if (isEditSlot)            btn.interactable = IsSwapAdEligible(); // tappable only to offer the swap-refill ad
                    else if (isAim && !isArmedSlot) btn.interactable = false;
                    else                            btn.interactable = true;
                }

                // Visual: armed slot during aim mode becomes the cancel button —
                // red bg + ✕ glyph (same look as the tile-bag swap-mode X).
                // Every other booster slot dims to its base color and is
                // non-interactable.
                if (spec.Type == SlotType.Booster && _slotArt != null && i < _slotArt.Length && _slotArt[i] != null)
                {   // round art: the STATE is the sprite (armed = brighter + gold glow, a touch bigger), not a tint
                    _slotArt[i].sprite = IsSlotLocked(spec) ? LockedSlotArt() : ButtonArt(isArmedSlot ? "booster_slot_armed" : "booster_slot_ready");
                    _slotArt[i].transform.localScale = Vector3.one * (isArmedSlot ? ART_ARMED_SCALE : 1f);
                }
                else if (_slotImages[i] != null && (_slotArt == null || i >= _slotArt.Length || _slotArt[i] == null))
                {
                    _slotImages[i].color = isArmedSlot
                        ? new Color(0.55f, 0.18f, 0.18f, 1f)
                        : SlotBaseColor(spec.Type);
                }
                if (_slotLabels[i] != null && spec.Type == SlotType.Booster)
                {
                    if (isArmedSlot)
                        _slotLabels[i].text = "✕";
                    else if (slotActive)
                    {
                        var booster = bm != null ? bm.GetBoosterById(spec.BoosterId) : null;
                        if (booster != null) _slotLabels[i].text = booster.DisplayName.Substring(0, 1);
                    }
                }
                // Hide the charge badge on the armed slot so the ✕ reads cleanly.
                if (_chargeBadgeContainers[i] != null && isArmedSlot)
                    _chargeBadgeContainers[i].SetActive(false);
            }

            if (_aimCanvas != null) _aimCanvas.gameObject.SetActive(isAim);

            // Scrim + board-tile sort boost — toggled on aim-state transitions.
            // Tracked separately from per-slot state so the scrim's tile-sorting
            // sweep only fires on the actual enter/exit edge, not on every
            // OnStateChanged callback (which can fire mid-aim too, e.g. when
            // charges change).
            if (isAim != _wasAim)
            {
                SetAimModeVisuals(isAim);
                _wasAim = isAim;
            }
            // Safety net (2026-06-03 Spencer: the dim scrim stayed stuck after using
            // Stone Splitter). The edge-gate above can miss the exit edge if _wasAim
            // desyncs during a booster resolve+cascade, leaving the screen dark with
            // no way back. Force a full re-sync whenever the scrim's ACTUAL state
            // disagrees with the aim state — the dim can never get stuck on.
            else if (_aimScrimCanvas != null && _aimScrimCanvas.gameObject.activeSelf != isAim)
            {
                SetAimModeVisuals(isAim);
                _wasAim = isAim;
            }
        }

        // ── Group animation (Candy-Crush-style converge/expand when a menu opens) ────
        //
        // When the Settings modal (or any blocking menu) opens, the booster
        // bench scales toward the bench center and disappears. When the modal
        // closes, the reverse animation plays. Bench panel scales in-place;
        // each slot converges its anchoredPosition to the bench center while
        // scaling to zero.

        private const float GROUP_OUT_DUR   = 0.28f;
        private const float GROUP_IN_DUR    = 0.32f;
        private const float GROUP_OVERSHOOT = 1.7f;

        // Group-scale state. A single float drives position + scale on all
        // children in lockstep, simulating "scale a parent transform" without
        // restructuring the hierarchy. groupT = 1 → rest; groupT = 0 →
        // collapsed at the group center.
        private float _groupT = 1f;
        private Tween _groupTween;

        private void CacheRestPositionsIfNeeded()
        {
            if (_restPositionsCached) return;
            if (_slotButtons == null) return;
            // Group center = midpoint of leftmost-to-rightmost slot anchored X.
            // Pinning to slot row (not bench center) keeps the whole group on
            // its own horizontal baseline — bench center sits lower and would
            // pull the row downward.
            float leftX  = float.MaxValue;
            float rightX = float.MinValue;
            _slotRestPositions = new Vector2[_slotButtons.Length];
            _slotRestScales    = new Vector3[_slotButtons.Length];
            for (int i = 0; i < _slotButtons.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                _slotRestPositions[i] = rt.anchoredPosition;
                _slotRestScales[i]    = rt.localScale;
                if (_slotRestPositions[i].x < leftX)  leftX  = _slotRestPositions[i].x;
                if (_slotRestPositions[i].x > rightX) rightX = _slotRestPositions[i].x;
            }
            _benchCenterAnchored = new Vector2((leftX + rightX) * 0.5f, 0f);
            _restPositionsCached = true;
        }

        /// <summary>
        /// True group-scale animation: a single float tweens 1→0, and every
        /// slot's position lerps proportionally to its distance from the
        /// group center, scale lerps proportionally to its rest scale. Looks
        /// like the whole row is one object zooming out into a point —
        /// stage-clear-toss-style cartoon perspective. Bench panel stays put.
        /// </summary>
        public void AnimateGroupOut(float speedMult = 1f)
        {
            CacheRestPositionsIfNeeded();
            float dur = GROUP_OUT_DUR / Mathf.Max(0.001f, speedMult);

            if (_benchGO != null)
            {
                _benchGO.transform.DOKill();
                _benchGO.transform.localScale = Vector3.one;
            }

            if (_slotButtons == null) return;

            // Kill any in-flight per-slot tweens (residue from prior implementation).
            for (int i = 0; i < _slotButtons.Length; i++)
                if (_slotButtons[i] != null) _slotButtons[i].transform.DOKill();

            _groupTween?.Kill();
            _groupTween = DOTween.To(() => _groupT, v =>
            {
                _groupT = v;
                ApplyGroupTransform(v);
            }, 0f, dur).SetEase(Ease.InBack, GROUP_OVERSHOOT);
        }

        /// <summary>
        /// Reverse of AnimateGroupOut — group scale tweens 0→1 with OutBack
        /// cartoon overshoot, so the row pops back into existence at its
        /// rest position. No-op if AnimateGroupOut never ran.
        /// </summary>
        private Vector2 _benchRest; private bool _benchRestCaptured;
        private float _bottomHudOffY;
        private const float BENCH_ENTRY_DROP = 340f; // how far below the bottom edge the bottom HUD parks

        /// <summary>Offset the WHOLE bottom HUD — bench panel + booster row + settings — vertically by <paramref
        /// name="dy"/> from rest, in lockstep. The boosters/settings are canvas children (not bench children), so
        /// we move them alongside the panel by hand; the SWAPS/Edit slot is EXCLUDED (it slides in from the right
        /// on its own). dy=0 → rest. 2026-07-13 Spencer.</summary>
        private void ApplyBottomHudOffset(float dy)
        {
            if (_benchGO != null)
            {
                var brt = _benchGO.transform as RectTransform;
                if (brt != null) brt.anchoredPosition = _benchRest + new Vector2(0f, dy);
            }
            if (_slotButtons != null && _slotRestPositions != null)
            {
                for (int i = 0; i < _slotButtons.Length && i < _slotRestPositions.Length; i++)
                {
                    if (_slotButtons[i] == null) continue;
                    if (_slotButtons[i] == _editPanelSlot) continue; // SWAPS panel slides from the right separately
                    var rt = _slotButtons[i].GetComponent<RectTransform>();
                    if (rt != null) rt.anchoredPosition = _slotRestPositions[i] + new Vector2(0f, dy);
                }
            }
            _bottomHudOffY = dy;
        }

        /// <summary>Snapshot the bottom-HUD rests + park it BELOW the bottom edge, so the map can fade into a blank
        /// screen before the pop-up plays. Called before the map fade. 2026-07-13 Spencer.</summary>
        public void PrepBenchEntry()
        {
            if (_benchGO == null) return;
            var rt = _benchGO.transform as RectTransform;
            if (rt == null) return;
            if (!_benchRestCaptured) { _benchRest = rt.anchoredPosition; _benchRestCaptured = true; }
            CacheRestPositionsIfNeeded(); // captures slot rests into _slotRestPositions
            rt.DOKill(); rt.localScale = Vector3.one; // no warp
            ApplyBottomHudOffset(-BENCH_ENTRY_DROP); // park below
        }

        /// <summary>Level-entry: the whole bottom HUD (bench panel + boosters + settings) POPS UP from below the
        /// bottom edge and overshoots (position, no scale/warp) — the mirror of the top bar dropping down. The
        /// boosters/settings ride with the panel in lockstep. BenchBottomExtend fills the overshoot gap so no cyan
        /// shows. Slides from wherever PrepBenchEntry parked it (prepping itself if called standalone). 2026-07-13.</summary>
        public void AnimateBenchIn()
        {
            if (_benchGO == null) return;
            var rt = _benchGO.transform as RectTransform;
            if (rt == null) return;
            if (!_benchRestCaptured) { _benchRest = rt.anchoredPosition; _benchRestCaptured = true; }
            CacheRestPositionsIfNeeded();
            rt.DOKill(); rt.localScale = Vector3.one; // no warp
            DOTween.Kill("benchEntry");
            ApplyBottomHudOffset(-BENCH_ENTRY_DROP); // ensure parked below, then pop up
            DOTween.To(() => _bottomHudOffY, ApplyBottomHudOffset, 0f, 0.24f)
                .SetEase(Ease.OutBack, 1.4f).SetId("benchEntry")
                .OnComplete(() => ApplyBottomHudOffset(0f));
        }

        /// <summary>Level-EXIT: the whole bottom HUD (bench panel + boosters + settings) slides back DOWN off the
        /// bottom edge — the mirror of AnimateBenchIn. 2026-07-14 Spencer.</summary>
        public void AnimateBenchOut()
        {
            if (_benchGO == null) return;
            var rt = _benchGO.transform as RectTransform;
            if (rt == null) return;
            if (!_benchRestCaptured) { _benchRest = rt.anchoredPosition; _benchRestCaptured = true; }
            CacheRestPositionsIfNeeded();
            rt.DOKill();
            DOTween.Kill("benchEntry");
            DOTween.To(() => _bottomHudOffY, ApplyBottomHudOffset, -BENCH_ENTRY_DROP, 0.34f)
                .SetEase(Ease.InBack, 1.2f).SetId("benchEntry").SetUpdate(true);
        }

        private Vector2 _swapPanelRest; private bool _swapPanelRestCaptured;
        // Park distance in UI units for ONE screen-width × 1.3 — matches the board/hand's 1.3-screen-width world park,
        // so the shared normalized slide overshoots the exact same on-screen distance for all three. Canvas reference
        // width (CANVAS_W) == one screen width. 2026-07-13 Spencer.
        private const float SWAP_PARK_DIST = CANVAS_W * 1.3f;

        /// <summary>Park the SWAPS panel off-screen right (before the map fade), so the level starts blank.
        /// 2026-07-13 Spencer.</summary>
        public void PrepSwapPanel()
        {
            if (_editPanelSlot == null) return;
            var rt = _editPanelSlot.transform as RectTransform;
            if (rt == null) return;
            if (!_swapPanelRestCaptured) { _swapPanelRest = rt.anchoredPosition; _swapPanelRestCaptured = true; }
            rt.DOKill();
            rt.anchoredPosition = _swapPanelRest + new Vector2(SWAP_PARK_DIST, 0f);
        }

        /// <summary>Level-entry UNIFIED slide: t=1 → parked off the right edge, t=0 → rest, t&lt;0 → overshoot left.
        /// Driven by the SAME shared tween as the board + tile holder so all three move as one. 2026-07-13 Spencer.</summary>
        public void SetSwapSlideNorm(float t)
        {
            if (_editPanelSlot == null) return;
            var rt = _editPanelSlot.transform as RectTransform;
            if (rt == null) return;
            if (!_swapPanelRestCaptured) { _swapPanelRest = rt.anchoredPosition; _swapPanelRestCaptured = true; }
            rt.anchoredPosition = _swapPanelRest + new Vector2(t * SWAP_PARK_DIST, 0f);
        }

        /// <summary>Snap the SWAPS panel to exact rest. Called from the shared slide's OnComplete.</summary>
        public void EndSwapSlide()
        {
            if (_editPanelSlot == null) return;
            var rt = _editPanelSlot.transform as RectTransform;
            if (rt != null) rt.anchoredPosition = _swapPanelRest;
        }

        public void AnimateGroupIn(float speedMult = 1f)
        {
            if (!_restPositionsCached) return;
            float dur = GROUP_IN_DUR / Mathf.Max(0.001f, speedMult);

            if (_benchGO != null)
            {
                _benchGO.transform.DOKill();
                _benchGO.transform.localScale = Vector3.one;
            }

            if (_slotButtons == null) return;
            for (int i = 0; i < _slotButtons.Length; i++)
                if (_slotButtons[i] != null) _slotButtons[i].transform.DOKill();

            _groupTween?.Kill();
            _groupTween = DOTween.To(() => _groupT, v =>
            {
                _groupT = v;
                ApplyGroupTransform(v);
            }, 1f, dur).SetEase(Ease.OutBack, GROUP_OVERSHOOT);
        }

        private void ApplyGroupTransform(float groupT)
        {
            // groupT clamped to a sensible visual range — InBack/OutBack
            // overshoot can push it slightly below 0 or above 1, which is
            // fine for scale (overshoot = momentary punch) but we apply it
            // uniformly so position + scale always read as one motion.
            for (int i = 0; i < _slotButtons.Length; i++)
            {
                if (_slotButtons[i] == null) continue;
                var rt = _slotButtons[i].GetComponent<RectTransform>();
                float deltaX = _slotRestPositions[i].x - _benchCenterAnchored.x;
                rt.anchoredPosition = new Vector2(
                    _benchCenterAnchored.x + deltaX * groupT,
                    _slotRestPositions[i].y); // Y stays — only X converges
                rt.localScale = _slotRestScales[i] * groupT;
            }
        }

        // ── Input handlers ──────────────────────────────────────────────────────

        // ── UNLOCK TOOLTIP (2026-10-29, Spencer + his Royal Kingdom screenshot: "add an unlocks at level number panel when you tap
        // on a locked booster"). A cream speech bubble above the row with a tail pointing at the tapped slot: "Unlocks at Level N"
        // (dark purple ink, "Level N" in our red accent), WordSlop. Pops in, holds UNLOCK_TIP_HOLD s (or until another tap), pops out.
        public static float UNLOCK_TIP_HOLD = 2.2f;
        private GameObject _unlockTip; private Sequence _unlockTipSeq;
        /// <summary>The run level a booster slot opens on (full-board: TutorialLocks.BOOSTER_SLOT_UNLOCKS by row order).</summary>
        private static int UnlockLevelFor(SlotSpec spec)
        {
            int ord = BoosterOrdinal(spec.BoosterId);
            if (SurvivalManager.FullBoardActive && ord >= 0 && ord < TutorialLocks.BOOSTER_SLOT_UNLOCKS.Length) return TutorialLocks.BOOSTER_SLOT_UNLOCKS[ord];
            return TutorialLocks.BOOSTER_UNLOCK_LEVEL;
        }
        /// <summary>11-16 Spencer: "make the unlock at whatever level bubbles match the tutorial ones". The tutorial's gold-rimmed
        /// cream bubble (UI/Panel/tut_bubble), WordSlop Button text at the lessons' size and ink, "Level N" in the lessons' blue.
        /// One line, as wide as its text; no tail (the lesson bubbles have none) - it sits over the tapped slot. False = the old
        /// cream bubble with the purple outline and tail.</summary>
        public static bool UNLOCK_TIP_GOLD = true;
        private bool ShowGoldUnlockTip(int slotIndex, RectTransform slotRT, RectTransform canvasRT, int lvl)
        {
            var btex = Resources.Load<Texture2D>("UI/Panel/tut_bubble");
            var font = Resources.Load<TMP_FontAsset>("WordSlop Button SDF");
            if (btex == null || font == null) return false;   // art or font missing -> the old bubble
            var rootCanvas = canvasRT.GetComponentInParent<Canvas>(); if (rootCanvas != null) rootCanvas = rootCanvas.rootCanvas;
            float scale = rootCanvas != null && rootCanvas.scaleFactor > 0.001f ? rootCanvas.scaleFactor : 1f;
            float W = Screen.width / scale;                                   // screen width in this canvas's units
            float pxPerUnit = (Camera.main != null ? Screen.height / (2f * Camera.main.orthographicSize) : 100f) / scale;   // canvas units per world unit

            var tip = new GameObject("UnlockTip", typeof(RectTransform), typeof(Image)); tip.transform.SetParent(canvasRT, false); tip.transform.SetAsLastSibling();
            _unlockTip = tip;
            var trt = (RectTransform)tip.transform;
            trt.anchorMin = trt.anchorMax = slotRT.anchorMin; trt.pivot = new Vector2(0.5f, 0f);
            var img = tip.GetComponent<Image>(); img.raycastTarget = false; img.type = Image.Type.Sliced;
            float bb = TutorialOverlay.GOLD_BORDER;
            img.sprite = Sprite.Create(btex, new Rect(0, 0, btex.width, btex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(bb, bb, bb, bb));
            img.pixelsPerUnitMultiplier = TutorialOverlay.GOLD_PPU / Mathf.Max(1f, pxPerUnit);   // rim as thick as the lesson bubble's

            var lgo = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); lgo.transform.SetParent(tip.transform, false);
            var tmp = lgo.GetComponent<TextMeshProUGUI>();
            tmp.font = font; tmp.fontSharedMaterial = font.material; tmp.raycastTarget = false; tmp.richText = true;
            tmp.alignment = TextAlignmentOptions.Center; tmp.textWrappingMode = TextWrappingModes.NoWrap; tmp.enableAutoSizing = false;
            tmp.color = LevelIntroModal.V2_LABEL_INK;
            float capEm = font.faceInfo.capLine / Mathf.Max(1f, font.faceInfo.pointSize);
            tmp.fontSize = TutorialOverlay.RM_CAP_FRAC * W / Mathf.Max(0.1f, capEm);
            tmp.text = $"Unlocks at <color=#{ColorUtility.ToHtmlStringRGB(TutorialOverlay.WORD_COLOR)}>Level {lvl}</color>";
            float textW = tmp.GetPreferredValues(tmp.text).x;
            float lh = font.faceInfo.lineHeight / Mathf.Max(1f, font.faceInfo.pointSize);
            float pitch = tmp.fontSize * (lh + TutorialOverlay.LESSON_LINE_SPACING * 0.01f);
            // the lessons' padding: top/bottom RM_PAD_FRAC each; sides = (bubble 76% - text 64%) / 2 of the screen width
            float sidePad = (TutorialOverlay.RM_BUBBLE_W_FRAC - TutorialOverlay.RM_TEXT_W_FRAC) * 0.5f * W;
            float pw = Mathf.Min(textW + 2f * sidePad, TutorialOverlay.RM_BUBBLE_W_FRAC * W);
            float ph = Mathf.Max(pitch + 2f * TutorialOverlay.RM_PAD_FRAC * W, 2f * bb / TutorialOverlay.GOLD_PPU * pxPerUnit + 4f);
            trt.sizeDelta = new Vector2(pw, ph);
            var lrt = (RectTransform)lgo.transform; lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f); lrt.pivot = new Vector2(0.5f, 0.5f);
            lrt.sizeDelta = new Vector2(pw, ph);
            lrt.anchoredPosition = new Vector2(0f, TutorialOverlay.TEXT_RIM_LIFT / TutorialOverlay.GOLD_PPU * pxPerUnit);   // centred on the cream, not the bevel

            // over the tapped slot, kept on screen
            float canvasW = canvasRT.rect.width > 1f ? canvasRT.rect.width : W;
            float minX = -canvasW * 0.5f + pw * 0.5f + 20f, maxX = canvasW * 0.5f - pw * 0.5f - 20f;
            trt.anchoredPosition = new Vector2(Mathf.Clamp(slotRT.anchoredPosition.x, minX, maxX), slotRT.anchoredPosition.y + slotRT.sizeDelta.y * 0.5f + 18f);

            trt.localScale = Vector3.one * 0.4f;
            _unlockTipSeq = DOTween.Sequence().SetUpdate(true);
            _unlockTipSeq.Append(trt.DOScale(1.06f, 0.16f).SetEase(Ease.OutQuad)).Append(trt.DOScale(1f, 0.1f))
                .AppendInterval(UNLOCK_TIP_HOLD).Append(trt.DOScale(0f, 0.16f).SetEase(Ease.InBack))
                .OnComplete(() => { if (tip != null) Destroy(tip); if (_unlockTip == tip) _unlockTip = null; });
            WDLog.Log($"[Booster] locked tap -> 'Unlocks at Level {lvl}' gold bubble ({DISPLAY_ORDER[slotIndex].BoosterId}) {pw:F0}x{ph:F0} font {tmp.fontSize:F0}");
            return true;
        }
        private void ShowUnlockTip(int slotIndex)
        {
            if (_slotButtons == null || slotIndex < 0 || slotIndex >= _slotButtons.Length || _slotButtons[slotIndex] == null) return;
            var slotRT = (RectTransform)_slotButtons[slotIndex].transform;
            var canvasRT = slotRT.parent as RectTransform; if (canvasRT == null) return;
            if (_unlockTip != null) { _unlockTipSeq?.Kill(); Destroy(_unlockTip); }
            int lvl = UnlockLevelFor(DISPLAY_ORDER[slotIndex]);
            if (UNLOCK_TIP_GOLD && ShowGoldUnlockTip(slotIndex, slotRT, canvasRT, lvl)) return;

            var tip = new GameObject("UnlockTip", typeof(RectTransform)); tip.transform.SetParent(canvasRT, false); tip.transform.SetAsLastSibling();
            _unlockTip = tip;
            var trt = (RectTransform)tip.transform;
            trt.anchorMin = trt.anchorMax = slotRT.anchorMin; trt.pivot = new Vector2(0.5f, 0f);
            float slotH = slotRT.sizeDelta.y, W = 520f, H = 132f;
            // keep the bubble on screen: centred over the slot, clamped to the canvas width; the tail stays on the slot
            float canvasW = canvasRT.rect.width > 1f ? canvasRT.rect.width : 1179f;
            float wantX = slotRT.anchoredPosition.x;
            float minX = -canvasW * 0.5f + W * 0.5f + 20f, maxX = canvasW * 0.5f - W * 0.5f - 20f;
            float bx = Mathf.Clamp(wantX, minX, maxX);
            trt.anchoredPosition = new Vector2(bx, slotRT.anchoredPosition.y + slotH * 0.5f + 34f);
            trt.sizeDelta = new Vector2(W, H);
            // body: dark purple outline -> cream face (rounded), soft drop below
            Image Rect(string n, Transform parent, Color c, float inset, float r)
            {
                var go = new GameObject(n, typeof(RectTransform), typeof(Image)); go.transform.SetParent(parent, false);
                var rt = (RectTransform)go.transform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
                var im = go.GetComponent<Image>(); im.sprite = MenuUI.GetRoundedRectSprite(Mathf.RoundToInt(r)); im.type = Image.Type.Sliced; im.color = c; im.raycastTarget = false; return im;
            }
            var shadow = Rect("Shadow", tip.transform, new Color32(0x3A, 0x1E, 0x12, 0x55), 0f, 40f); ((RectTransform)shadow.transform).anchoredPosition = new Vector2(0f, -8f);
            var outline = Rect("Outline", tip.transform, new Color32(0x38, 0x0B, 0x6A, 0xFF), 0f, 40f);
            Rect("Face", tip.transform, new Color32(0xFF, 0xF4, 0xDE, 0xFF), 7f, 34f);
            // tail: a rotated square in the outline colour + a smaller cream one on top, pointing at the slot
            float tailX = Mathf.Clamp(wantX - bx, -W * 0.5f + 50f, W * 0.5f - 50f);
            foreach (var (n, c, sz, dy) in new[] { ("TailOut", (Color)new Color32(0x38, 0x0B, 0x6A, 0xFF), 46f, -12f), ("TailIn", (Color)new Color32(0xFF, 0xF4, 0xDE, 0xFF), 32f, -6f) })
            {
                var go = new GameObject(n, typeof(RectTransform), typeof(Image)); go.transform.SetParent(tip.transform, false);
                var rt = (RectTransform)go.transform; rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.sizeDelta = new Vector2(sz, sz);
                rt.anchoredPosition = new Vector2(tailX, dy); rt.localRotation = Quaternion.Euler(0, 0, 45f);
                var im = go.GetComponent<Image>(); im.color = c; im.raycastTarget = false;
            }
            outline.transform.SetSiblingIndex(1);
            // label: "Unlocks at <Level N>"
            var lgo = new GameObject("Label", typeof(RectTransform)); lgo.transform.SetParent(tip.transform, false);
            var lrt = (RectTransform)lgo.transform; lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(24f, 14f); lrt.offsetMax = new Vector2(-24f, -14f);
            var tmp = lgo.AddComponent<TextMeshProUGUI>();
            var f = Resources.Load<TMP_FontAsset>(GameFont.WORDSLOP_FONT); if (f != null) tmp.font = f;
            tmp.text = $"Unlocks at <color=#C2132F>Level {lvl}</color>";
            tmp.color = new Color32(0x2B, 0x0A, 0x5A, 0xFF);
            tmp.alignment = TextAlignmentOptions.Center; tmp.enableAutoSizing = true; tmp.fontSizeMin = 20f; tmp.fontSizeMax = 54f; tmp.raycastTarget = false;
            // pop in, hold, pop out
            trt.localScale = Vector3.one * 0.4f;
            _unlockTipSeq = DOTween.Sequence().SetUpdate(true);
            _unlockTipSeq.Append(trt.DOScale(1.06f, 0.16f).SetEase(Ease.OutQuad)).Append(trt.DOScale(1f, 0.1f))
                .AppendInterval(UNLOCK_TIP_HOLD).Append(trt.DOScale(0f, 0.16f).SetEase(Ease.InBack))
                .OnComplete(() => { if (tip != null) Destroy(tip); if (_unlockTip == tip) _unlockTip = null; });
            WDLog.Log($"[Booster] locked tap -> 'Unlocks at Level {lvl}' ({DISPLAY_ORDER[slotIndex].BoosterId})");
        }

        private void OnSlotTapped(int slotIndex)
        {
            if (TutorialBeats.BlocksInput) return;   // tutorial beat up: boosters wait
            if (BoosterManager.CascadeBusy) return;   // 11-17: the last booster is still resolving
            if (slotIndex < 0 || slotIndex >= DISPLAY_ORDER.Length) return;
            HapticsManager.ButtonTap();   // 11-17 haptics pass: the booster buttons and the gear had a sound but no tap
            var spec = DISPLAY_ORDER[slotIndex];

            // Locked during early onboarding — ignore the tap (defensive; the button is also
            // interactable=false). 2026-06-09.
            if (IsSlotLocked(spec))
            {
                if (spec.Type == SlotType.Booster) { ShowUnlockTip(slotIndex); GameAudio.Instance?.PlayUIClick(); }
                else GameAudio.Instance?.PlayInvalidDrop();
                return;
            }

            // Aim-mode cancel: tapping the armed booster's slot (now styled
            // as a red ✕) cancels aim. Same multi-pop release sound as the
            // tile-bag swap-mode X (press half fires on PointerDown — see
            // EventTrigger wired in BuildSlotCanvas).
            var bm = BoosterManager.Instance;
            if (bm != null && bm.AimMode && spec.Type == SlotType.Booster
                && bm.ArmedBooster != null && spec.BoosterId == bm.ArmedBooster.Id)
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                bm.CancelAim();
                return;
            }

            // THE LEVEL IS WON - the row is closed until the next level (11-16, found by the monkey test: during the end-of-level
            // rocket bonus a booster could still be armed, and Settings > Quit would have cost a life on a level already won).
            if (SurvivalManager.AwaitingNextLevel) return;
            // 11-16 (play-badly monkey): a card or the Out of Moves banner owns the screen, or the moves are gone - no boosters and
            // no Settings gear. Settings opened over the banner used to un-pause the board under the Continue card when it closed.
            { var own = SurvivalManager.Instance; if (own != null && (own.IsOverlayPaused || own.OutOfMovesLocked)) return; }

            // 2026-06-01: every non-Settings slot click plays the same
            // settings-tab release sound per Spencer ("the sound played in the
            // settings menu when you click between buttons"). Settings has its
            // own paired press+release (a fuller 2-stage feel) wired below.
            if (spec.Type != SlotType.Settings)
                GameAudio.Instance?.PlaySettingsRelease();

            bool suppressClick = false;

            switch (spec.Type)
            {
                case SlotType.Booster:
                {
                    int charges = BoosterManager.Instance != null
                        ? BoosterManager.Instance.GetCharges(spec.BoosterId)
                        : 0;
                    if (charges > 0)
                    {
                        BoosterManager.Instance?.TryActivate(spec.BoosterId);
                        suppressClick = true;
                    }
                    else
                    {
                        OnBuyMoreTapped(spec);
                    }
                    break;
                }
                case SlotType.Edit:
                {
                    // The SWAPS panel is a readout (editing is via tapping a board tile). The only tap
                    // it accepts is the out-of-swaps rewarded-ad refill offer.
                    if (IsSwapAdEligible())
                        OfferSwapAd();
                    break;
                }
                case SlotType.TileBag:
                {
                    // Tap-to-swap mode: dim everything except the hand cards,
                    // replace the bag with an X. Tapping a card swaps it;
                    // tapping the X cancels without consuming a swap.
                    int swapsLeft = MatchController.Instance != null
                        ? MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN)
                        : 0;
                    if (swapsLeft <= 0)
                    {
                        OnBuyMoreTapped(spec);
                        break;
                    }
                    EnterTileBagSwapMode();
                    suppressClick = true; // EnterTileBagSwapMode handles its own SFX
                    break;
                }
                case SlotType.Settings:
                    // 11-17 settings revamp: in a level the gear fans out round buttons (exit / music / sound / vibration), no card.
                    if (SettingsV2.ENABLED && SettingsV2.ShowFlyout()) { GameAudio.Instance?.PlaySettingsRelease(); suppressClick = true; break; }
                    if (SettingsModal.Instance == null)
                    {
                        var modalGO = new GameObject("SettingsModalRoot");
                        modalGO.AddComponent<SettingsModal>();
                    }
                    SettingsModal.Instance?.Show();
                    // Release half of the split otherpop — press half fired
                    // from the EventTrigger.PointerDown listener on the slot.
                    GameAudio.Instance?.PlaySettingsRelease();
                    suppressClick = true; // skip the generic UI click below
                    break;
            }

            // 2026-06-01: dropped the bottom PlayUIClick fallback — the
            // settings-tab release sound is now fired at the top of this
            // method for every non-Settings slot, so there's no need for a
            // second click sound here. suppressClick is left in place in case
            // future case branches need to opt out of the top sound.
            _ = suppressClick;
        }

        private void OnCancelAimTapped()
        {
            BoosterManager.Instance?.CancelAim();
            GameAudio.Instance?.PlayUIClick();
        }

        private void OnBuyMoreTapped(SlotSpec spec)
        {
            string slotName = spec.Type == SlotType.Booster
                ? (BoosterManager.Instance?.GetBoosterById(spec.BoosterId)?.DisplayName ?? spec.BoosterId)
                : spec.Type.ToString();
            WDLog.Log($"[BoosterHUD] '+' tapped on {slotName} — buy flow pending");
        }
    }
}
