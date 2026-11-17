using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>PRE-LEVEL BOOSTERS (2026-10-29 Spencer: "make them have to select the boosters they want on the level pop up before
    /// starting the level and also place them in a sort of animation when the level begins", Royal Kingdom GIF; researched RM/CC).
    ///  - Three boosters = our specials: ROCKET, BOMB, DISCO. Inventory persists (PlayerPrefs).
    ///  - Unlock at run level FROM_LEVEL (RM: the booster pick appears at L16) with a starter gift of STARTER each.
    ///  - The level pop-up shows a "Select Boosters" row: count badge, tap toggles a green check (remembered for the next try).
    ///  - SPENT ON PLAY (RM/CC: lost if you fail). The board slides in, then each one drops from above onto a safe random tile
    ///    (comet + star sparkle) and becomes that special; the player gets control after the last one lands.
    ///  - Earning more for now: +1 random booster every REWARD_EVERY level wins after unlock (shop / chests later).
    /// ON = false -> no booster row, nothing placed.</summary>
    public class PreBoosters : MonoBehaviour
    {
        public static bool ON = true;
        public static int  FROM_LEVEL = 16, STARTER = 3, REWARD_EVERY = 3;
        public static float DROP_FLIGHT = 0.42f, DROP_GAP = 0.18f, MIN_GAP = 2;
        public static readonly int[] KINDS = { RulesEngine.SPECIAL_ROCKET_H, RulesEngine.SPECIAL_BOMB, RulesEngine.SPECIAL_DISCO };
        public static readonly string[] KEYS = { "rocket", "bomb", "disco" };

        private static PreBoosters s_inst;
        private static readonly List<int> s_pending = new List<int>();
        /// <summary>WIN-STREAK specials (10-31, Spencer: "could we have a drop animation for those at the start of the level?"):
        /// WinStreak queues them here instead of placing them silently at build; they drop in with the boosters. Never refunded.</summary>
        private static readonly List<int> s_streak = new List<int>();
        public static void QueueStreak(int kind) => s_streak.Add(kind);

        /// <summary>Power-ups are queued to drop in but the drop has not finished: the board takes no moves (11-16, play-badly
        /// monkey: a swap made in the first instant of a level ran INTO the drop-in - the two shared the busy flag, the drop
        /// released it mid-pop and a disco landed on a cell that was exploding). Gives up after 6 s so a drop that never starts
        /// (no level-entry event) can never leave the board dead.</summary>
        public static bool BlocksBoard
        {
            get
            {
                if (s_pending.Count == 0 && s_streak.Count == 0) { s_waitSince = -1f; return false; }
                if (s_waitSince < 0f) s_waitSince = Time.unscaledTime;
                return Time.unscaledTime - s_waitSince < 6f;
            }
        }
        private static float s_waitSince = -1f;
        public static void ClearStreak() => s_streak.Clear();   // a rebuilt board re-queues; never stack two builds' worth

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (s_inst != null) return;
            var go = new GameObject("PreBoosters"); DontDestroyOnLoad(go); s_inst = go.AddComponent<PreBoosters>();
            HUDManager.OnLevelEntryDone += () => { if (s_inst != null && (s_pending.Count > 0 || s_streak.Count > 0)) s_inst.StartCoroutine(s_inst.DropAll()); };
        }

        // ── inventory ──
        public static int Count(int i) => PlayerPrefs.GetInt("WD_PreBoost_" + KEYS[i], 0);
        public static void Add(int i, int n) { PlayerPrefs.SetInt("WD_PreBoost_" + KEYS[i], Mathf.Max(0, Count(i) + n)); PlayerPrefs.Save(); }
        public static bool Selected(int i) => PlayerPrefs.GetInt("WD_PreBoostSel_" + KEYS[i], 0) == 1 && Count(i) > 0;
        public static void SetSelected(int i, bool on) { PlayerPrefs.SetInt("WD_PreBoostSel_" + KEYS[i], on ? 1 : 0); PlayerPrefs.Save(); }

        /// <summary>Is the booster pick offered on this level (internal level index)? Grants the starter gift the first time.</summary>
        public static bool OfferedOn(int levelIndex)
        {
            if (!ON || !SurvivalManager.FullBoardActive) return false;
            int run = LevelMapPanel.DisplayNum(levelIndex);
            if (run < FROM_LEVEL || LevelRules.For(run).Bonus) return false;
            if (PlayerPrefs.GetInt("WD_PreBoostGift", 0) == 0)
            {
                PlayerPrefs.SetInt("WD_PreBoostGift", 1);
                for (int i = 0; i < KINDS.Length; i++) Add(i, STARTER);
                WDLog.Log($"[PreBoost] unlocked at L{run}: starter gift {STARTER} of each");
            }
            return true;
        }

        /// <summary>PLAY pressed: spend the selected boosters and queue them for the drop-in.</summary>
        public static void CommitSelection(int levelIndex)
        {
            s_pending.Clear();
            if (!OfferedOn(levelIndex)) return;
            for (int i = 0; i < KINDS.Length; i++)
                if (Selected(i)) { Add(i, -1); s_pending.Add(KINDS[i]); }
            if (s_pending.Count > 0) WDLog.Log($"[PreBoost] spent {s_pending.Count} booster(s) on L{LevelMapPanel.DisplayNum(levelIndex)}");
        }

        /// <summary>Level won (WinStreak.Win): every REWARD_EVERY wins after unlock, +1 random booster.</summary>
        public static void OnLevelWon()
        {
            if (!ON || PlayerPrefs.GetInt("WD_PreBoostGift", 0) == 0) return;
            int w = PlayerPrefs.GetInt("WD_PreBoostWins", 0) + 1; PlayerPrefs.SetInt("WD_PreBoostWins", w); PlayerPrefs.Save();
            if (w % REWARD_EVERY != 0) return;
            int i = Random.Range(0, KINDS.Length); Add(i, 1);
            WDLog.Log($"[PreBoost] win reward: +1 {KEYS[i]} (now {Count(i)})");
        }

        /// <summary>FX Test Menu: run the drop-in now with one of each (nothing spent).</summary>
        public static void TestDropIn()
        {
            if (s_inst == null) return;
            s_pending.Clear(); s_pending.AddRange(KINDS);
            s_inst.StartCoroutine(s_inst.DropAll());
        }

        // ── the drop-in ──
        private IEnumerator DropAll()
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance; var fx = WordDropFX.Instance;
            if (rules == null || grid == null) { s_pending.Clear(); s_streak.Clear(); yield break; }
            var hand = HandManager.Instance; var mc = MatchController.Instance;
            mc?.BeginProcessing(); if (hand != null) hand.IsInteractable = false;
            var placed = new List<Vector2Int>();
            float cell = grid.CellSize;
            var all = new List<(int kind, bool streak)>();
            foreach (int k in s_streak) all.Add((k, true));
            foreach (int k in s_pending) all.Add((k, false));
            s_streak.Clear();
            foreach (var (k0, streak) in all)
            {
                int kind = k0 == RulesEngine.SPECIAL_ROCKET_H && Random.value < 0.5f ? RulesEngine.SPECIAL_ROCKET_V : k0;
                var c = PickCell(rules, placed);
                if (c.x < 0)
                {
                    if (streak) { WDLog.Log("[Streak] no free cell for a streak special"); continue; }
                    WDLog.Log("[PreBoost] no free cell - booster refunded"); for (int i = 0; i < KINDS.Length; i++) if (KINDS[i] == k0) Add(i, 1); continue;
                }
                placed.Add(c);
                rules.GetCell(c.x, c.y).SpecialKind = kind;   // claim in data now (like the bonus rockets)
                Vector3 to = grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.6f);
                var cam = Camera.main;
                Vector3 from = cam != null ? new Vector3(to.x * 0.4f, cam.transform.position.y + cam.orthographicSize + cell, to.z) : to + Vector3.up * 8f;
                if (fx != null) { fx.BonusComet(from, to, DROP_FLIGHT); fx.FlyIcon(kind, from, to, DROP_FLIGHT); }
                GameAudio.Instance?.PlayWhooshFast();
                yield return new WaitForSecondsRealtime(DROP_FLIGHT);
                var t = grid.GetTile(c.x, c.y);
                t?.SetSpecial(kind, pop: true);
                fx?.BonusLandSparkle(to);
                GameAudio.Instance?.PlayScorePowerup();
                GameAudio.Instance?.PlayPowerupShimmer();   // 10-31 Spencer: the brass chime shimmer as the free power-up lands
                WDLog.Log($"[PreBoost] {RulesEngine.SpecialName(kind)} placed at ({c.x},{c.y})");
                yield return new WaitForSecondsRealtime(DROP_GAP);
            }
            s_pending.Clear();
            mc?.EndProcessing(); if (hand != null) hand.IsInteractable = true;
        }

        /// <summary>A plain letter cell: no blocker/goal piece/special, not the top row, away from the other drops.</summary>
        private static Vector2Int PickCell(RulesEngine rules, List<Vector2Int> taken)
        {
            var ok = new List<Vector2Int>();
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 1; y < RulesEngine.ROWS - 1; y++)
                {
                    var d = rules.GetCell(x, y);
                    if (d == null || d.IsStone || d.IsFrozen || d.HasGem || d.IsDropTarget || d.IsAnchored || d.TreasureHits > 0
                        || d.SpecialKind != RulesEngine.SPECIAL_NONE || d.Letter == '\0') continue;
                    bool near = false;
                    foreach (var p in taken) if (Mathf.Max(Mathf.Abs(p.x - x), Mathf.Abs(p.y - y)) < MIN_GAP) near = true;
                    for (int ox = -1; ox <= 1 && !near; ox++) for (int oy = -1; oy <= 1 && !near; oy++)
                    { var n = rules.GetCell(x + ox, y + oy); if (n != null && n.SpecialKind != RulesEngine.SPECIAL_NONE && (ox != 0 || oy != 0)) near = true; }
                    if (!near) ok.Add(new Vector2Int(x, y));
                }
            return ok.Count == 0 ? new Vector2Int(-1, -1) : ok[Random.Range(0, ok.Count)];
        }

        // ── the pop-up row ──
        private static readonly Color SLOT_BG = new Color32(0x3C, 0x8C, 0xF0, 0xFF), SLOT_ON = new Color32(0x4C, 0xC8, 0x2E, 0xFF);
        private static readonly Color BADGE_RED = new Color32(0xE8, 0x2A, 0x3A, 0xFF), BADGE_GREEN = new Color32(0x2F, 0xA8, 0x1E, 0xFF);

        /// <summary>Builds (or rebuilds) the "Select Boosters" row inside <paramref name="parent"/>, spanning its rect.</summary>
        public static void BuildRow(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--) Object.Destroy(parent.GetChild(i).gameObject);
            var label = NewUI("Label", parent, typeof(Text)); Anchor(label, 0f, 0.74f, 1f, 1f);
            var lt = label.GetComponent<Text>(); lt.font = MenuUI.GetFont(); lt.text = "Select Boosters:"; lt.fontSize = 22; lt.fontStyle = FontStyle.Bold;
            lt.alignment = TextAnchor.MiddleCenter; lt.color = new Color32(0x6A, 0x2A, 0x1A, 0xFF);
            for (int i = 0; i < KINDS.Length; i++)
            {
                float x0 = 0.5f + (i - 1) * 0.27f - 0.11f;
                var slot = NewUI("Booster_" + KEYS[i], parent, typeof(Image), typeof(Button)); Anchor(slot, x0, 0f, x0 + 0.22f, 0.72f);
                var arf = slot.gameObject.AddComponent<AspectRatioFitter>(); arf.aspectMode = AspectRatioFitter.AspectMode.FitInParent; arf.aspectRatio = 1f;
                var bg = slot.GetComponent<Image>(); bg.sprite = MenuUI.GetRoundedRectSprite(26); bg.type = Image.Type.Sliced;
                var icon = NewUI("Icon", slot, typeof(Image)); Anchor(icon, 0.12f, 0.12f, 0.88f, 0.88f);
                var ii = icon.GetComponent<Image>(); ii.sprite = Tile.SpecialIcon(KINDS[i]); ii.preserveAspect = true; ii.raycastTarget = false;
                icon.localRotation = Quaternion.Euler(0f, 0f, Tile.SpecialIconAngle(KINDS[i]));
                var badge = NewUI("Badge", slot, typeof(Image)); Anchor(badge, 0.62f, -0.08f, 1.06f, 0.36f);
                var bi = badge.GetComponent<Image>(); bi.sprite = MenuUI.GetRoundedRectSprite(40); bi.type = Image.Type.Sliced; bi.raycastTarget = false;
                var num = NewUI("Num", badge, typeof(Text)); Anchor(num, 0f, 0f, 1f, 1f);
                var nt = num.GetComponent<Text>(); nt.font = MenuUI.GetFont(); nt.fontSize = 20; nt.fontStyle = FontStyle.Bold; nt.alignment = TextAnchor.MiddleCenter; nt.color = Color.white; nt.raycastTarget = false;
                var chk = NewUI("Check", badge, typeof(Image)); Anchor(chk, 0.12f, 0.12f, 0.88f, 0.88f);
                var ci = chk.GetComponent<Image>(); ci.sprite = CheckSprite(); ci.preserveAspect = true; ci.raycastTarget = false;
                int idx = i; var sTf = slot;
                System.Action refresh = () =>
                {
                    bool sel = Selected(idx); int n = Count(idx);
                    bg.color = n <= 0 ? new Color(0.55f, 0.55f, 0.6f, 1f) : sel ? SLOT_ON : SLOT_BG;
                    ii.color = n <= 0 ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
                    bi.color = sel ? BADGE_GREEN : BADGE_RED;
                    nt.text = sel && ci.sprite != null ? "" : sel ? "OK" : n.ToString();
                    ci.enabled = sel && ci.sprite != null;
                };
                refresh();
                slot.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (Count(idx) <= 0) { sTf.DOKill(); sTf.DOShakeAnchorPos(0.25f, 6f, 20).SetUpdate(true); return; }
                    SetSelected(idx, !Selected(idx)); refresh();
                    sTf.DOKill(true); sTf.localScale = Vector3.one; sTf.DOPunchScale(Vector3.one * 0.14f, 0.22f, 6, 0.6f).SetUpdate(true);
                    GameAudio.Instance?.PlayUIClick();
                });
            }
        }
        /// <summary>LEVEL CARD v2 booster row (2026-11-01, approved mock panel_card_mock_v32.png): the three boosters on sky-blue plates
        /// (Resources/UI/Panel/booster_plate.png), each lifted a touch with a soft plate-blue shadow under it, the count on the red-rim
        /// badge. <paramref name="parent"/> is laid out in mock pixels (the card's cream box, 860 x 330). Same select/empty behaviour
        /// as BuildRow; the card draws its own "Select Boosters" label.</summary>
        public static Color V2_SHADOW = new Color32(0x3A, 0x6F, 0xB0, 0xFF);
        public static void BuildRowV2(RectTransform parent, Sprite plate, Sprite badge)
        {
            for (int i = parent.childCount - 1; i >= 0; i--) Object.Destroy(parent.GetChild(i).gameObject);
            float w = parent.rect.width > 1f ? parent.rect.width : parent.sizeDelta.x;
            for (int i = 0; i < KINDS.Length; i++)
            {
                float cx = w / 2f + (i - 1) * 270f, cy = 215f;
                var slot = NewUI("Booster_" + KEYS[i], parent, typeof(Image), typeof(Button));
                Place(slot, cx, cy, 200f, 200f);
                var bg = slot.GetComponent<Image>(); bg.sprite = plate; bg.color = Color.white;
                var shadow = NewUI("Shadow", slot, typeof(Image)); Place(shadow, 100f, 100f + 4f, 116f * 1.06f, 116f * 1.06f);
                var si = shadow.GetComponent<Image>(); si.sprite = Tile.SpecialIcon(KINDS[i]); si.preserveAspect = true; si.raycastTarget = false;
                si.color = new Color(V2_SHADOW.r, V2_SHADOW.g, V2_SHADOW.b, 0.38f);
                // upright, like the mock (11-01: the board's tile angle turned the rocket sideways)
                var icon = NewUI("Icon", slot, typeof(Image)); Place(icon, 100f, 100f - 10f, 116f, 116f);
                var ii = icon.GetComponent<Image>(); ii.sprite = Tile.SpecialIcon(KINDS[i]); ii.preserveAspect = true; ii.raycastTarget = false;
                // the badge art is padded (disc = 346 of its 498 px; circle centre 248,246.5): size the IMAGE so the DISC is 92
                const float BW = 92f * 498f / 346f, BH = BW * 534f / 498f;
                var bd = NewUI("Badge", slot, typeof(Image)); Place(bd, 100f + 66f + (249f - 248f) / 498f * BW, 100f + 62f + (267f - 246.5f) / 534f * BH, BW, BH);
                var bi = bd.GetComponent<Image>(); bi.sprite = badge; bi.preserveAspect = true; bi.raycastTarget = false;
                var num = NewUI("Num", bd, typeof(TMPro.TextMeshProUGUI)); Place(num, 248f / 498f * BW, 246.5f / 534f * BH, 92f, 80f);
                var nt = num.GetComponent<TMPro.TextMeshProUGUI>(); nt.alignment = TMPro.TextAlignmentOptions.Center; nt.raycastTarget = false;
                nt.fontSize = 35f / 0.594f / HUDManager.RECIPE_MAX_MUL;
                HUDManager.ApplyRecipeCount(nt, new Color32(0x46, 0x04, 0x0C, 0xFF), 0.2f, 0f);
                var chk = NewUI("Check", bd, typeof(Image)); Place(chk, 248f / 498f * BW, 246.5f / 534f * BH, 84f, 84f);   // alone on the plate: a touch bigger
                var ci = chk.GetComponent<Image>(); ci.sprite = CheckSprite(); ci.preserveAspect = true; ci.raycastTarget = false;
                int idx = i; var sTf = slot;
                System.Action refresh = () =>
                {
                    bool sel = Selected(idx); int n = Count(idx);
                    bg.color = n <= 0 ? new Color(0.72f, 0.74f, 0.78f, 1f) : sel ? new Color(0.78f, 1f, 0.78f, 1f) : Color.white;
                    ii.color = n <= 0 ? new Color(1f, 1f, 1f, 0.45f) : Color.white;
                    nt.text = sel && ci.sprite != null ? "" : n.ToString();
                    nt.transform.localScale = Vector3.one * (n >= 10 ? 0.78f : 1f);   // two digits fit the disc
                    ci.enabled = sel && ci.sprite != null;
                    bi.enabled = !(sel && ci.sprite != null);   // 11-01 Spencer: checked -> the red badge goes, the check stands alone
                };
                refresh();
                slot.GetComponent<Button>().onClick.AddListener(() =>
                {
                    if (Count(idx) <= 0) { sTf.DOKill(); sTf.DOShakeAnchorPos(0.25f, 6f, 20).SetUpdate(true); return; }
                    SetSelected(idx, !Selected(idx)); refresh();
                    sTf.DOKill(true); sTf.localScale = Vector3.one; sTf.DOPunchScale(Vector3.one * 0.14f, 0.22f, 6, 0.6f).SetUpdate(true);
                    GameAudio.Instance?.PlayUIClick();
                });
            }
        }
        /// <summary>Centre (x, y) in mock px from the parent's top-left, y down.</summary>
        private static void Place(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = new Vector2(0f, 1f); r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h);
        }
        private static Sprite s_check;
        private static Sprite CheckSprite()
        {
            if (s_check != null) return s_check;
            s_check = Resources.Load<Sprite>("UI/icon_check_3d");
            if (s_check == null) { var t = Resources.Load<Texture2D>("UI/icon_check_3d"); if (t != null) s_check = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), 100f); }
            return s_check;
        }
        private static RectTransform NewUI(string n, Transform p, params System.Type[] comps)
        {
            var list = new List<System.Type> { typeof(RectTransform) }; list.AddRange(comps);
            var go = new GameObject(n, list.ToArray()); go.transform.SetParent(p, false); return (RectTransform)go.transform;
        }
        private static void Anchor(RectTransform r, float x0, float y0, float x1, float y1)
        { r.anchorMin = new Vector2(x0, y0); r.anchorMax = new Vector2(x1, y1); r.offsetMin = r.offsetMax = Vector2.zero; }
    }
}
