using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// 11-24 (Spencer picked the nest marker; "why dont you just place the owls each in their own columns"): on an owl
    /// (drop-to-bottom) level a NEST sits just under the board below each owl home column (RulesEngine.DUCK_COLUMNS), so the
    /// player can see where the owls are going. Visual only - nothing here touches the rules. World-space sprites: the home
    /// screen and cards cover them like they cover the board. ON = false removes them.
    /// </summary>
    public class OwlNests : MonoBehaviour
    {
        public static bool   ON = true;
        public static bool   STAY_IN_NEST = true;   // a landed owl sits in its nest for the rest of the level (false: it flies to the goal box)
        public static string ASSET = "Goals/nest";
        public static float  WIDTH_CELLS = 0.95f;   // nest width, in board cells
        public static float  DROP_CELLS  = 0.80f;   // nest centre this far under the board's bottom edge, in cells
        public static float  OWL_CELLS   = 0.92f;   // the sitting owl's size, in cells. 11-24 Spencer: "the size of the piece is shrinking when it falls off the board" - .58 -> .92 = the same size as on the board
        public static float  OWL_UP_CELLS = 0.32f;  // the sitting owl's centre above the nest's centre
        public static float  LAND_SECS   = 0.28f;
        public static int    ORDER = 3;             // board panel 0 < grid 1 < owl 3 < NEST 4 < tiles 5

        // landing keyframes: time (s) -> owl width, owl height, height above the seat (cells), nest height. [1] = the moment it hits.
        private static readonly float[] K_T    = { 0f,    0.20f, 0.27f, 0.41f, 0.53f, 0.59f, 0.72f };
        private static readonly float[] K_SX   = { 0.94f, 0.88f, 1.28f, 0.92f, 1.00f, 1.13f, 1.00f };
        private static readonly float[] K_SY   = { 1.06f, 1.14f, 0.68f, 1.12f, 1.00f, 0.88f, 1.00f };
        private static readonly float[] K_UP   = { 0f,    0f,    0f,    0.20f, 0f,    0f,    0f    };
        private static readonly float[] K_NEST = { 1f,    1f,    0.72f, 1.10f, 1f,    0.90f, 1f    };
        public static float FALL_SPEED  = 17f;     // board falls run Tile.RM_FALL_V0 12 -> RM_FALL_VMAX 20 cells/s; 17 sits between a short and a long drop
        public static int   LAND_PUFF   = 10;      // sparkle burst when it hits (0 = none)
        public static float IDLE_SPEED  = 2.6f, IDLE_AMOUNT = 0.02f;   // the sitting owl breathes
        private static float Key(float t, float[] ts, float[] vs)
        {
            if (t <= ts[0]) return vs[0];
            for (int k = 1; k < ts.Length; k++)
                if (t <= ts[k]) { float f = (t - ts[k - 1]) / (ts[k] - ts[k - 1]); f = f * f * (3f - 2f * f); return Mathf.Lerp(vs[k - 1], vs[k], f); }
            return vs[vs.Length - 1];
        }

        // BLINK (11-24, Spencer's closed-eyes frame): every owl - on the board and in a nest - blinks on its own clock.
        public static bool   BLINK = true;
        public static string BLINK_ASSET = "Goals/owl_blink";
        public static float  BLINK_EVERY_MIN = 2.6f, BLINK_EVERY_MAX = 5.2f, BLINK_SECS = 0.14f;
        private static Sprite s_blink; private static bool s_blinkTried;
        private static Sprite BlinkSprite
        {
            get
            {
                if (s_blinkTried) return s_blink;
                s_blinkTried = true;
                var tex = Resources.Load<Texture2D>(BLINK_ASSET);
                if (tex != null) s_blink = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
                WDLog.Log($"[Nest] {BLINK_ASSET} -> {(tex != null ? tex.width + "x" + tex.height : "MISSING")}");
                return s_blink;
            }
        }
        // CHEER (11-24, Spencer's wings-up frame): an owl cheers when it lands in its nest; when EVERY nest is full they all cheer and
        // hop in a wave, and keep going through the bonus. The cheer art is on a canvas twice as wide (the wings reach past the
        // neutral frame), same height and placement, so it swaps in at the same scale.
        public static bool   CHEER = true;
        public static string CHEER_ASSET = "Goals/owl_cheer";
        public static float  CHEER_FROM = 0.14f, CHEER_SECS = 1.1f;      // after the impact: wings go up on the bounce, held this long
        public static float  PARTY_BEAT = 0.75f, PARTY_UP = 0.6f, PARTY_HOP = 0.10f, PARTY_WAVE = 0.11f;   // seconds per hop, share of it with wings up, hop height (cells), delay per column
        private static Sprite s_cheer; private static bool s_cheerTried;
        private static Sprite CheerSprite
        {
            get
            {
                if (s_cheerTried) return s_cheer;
                s_cheerTried = true;
                var tex = Resources.Load<Texture2D>(CHEER_ASSET);
                if (tex != null) s_cheer = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.height);   // 1 unit TALL, like the neutral frame
                WDLog.Log($"[Nest] {CHEER_ASSET} -> {(tex != null ? tex.width + "x" + tex.height : "MISSING")}");
                return s_cheer;
            }
        }
        /// <summary>Is the owl with this id mid-blink right now? Each id has its own period and offset, so they never blink in step.</summary>
        private static bool Blinking(int id)
        {
            float h = Mathf.Abs(Mathf.Sin(id * 12.9898f) * 43758.547f); h -= Mathf.Floor(h);
            float period = Mathf.Lerp(BLINK_EVERY_MIN, BLINK_EVERY_MAX, h);
            return Mathf.Repeat(Time.unscaledTime + h * 7f, period) < BLINK_SECS;
        }
        /// <summary>The picture an owl shows this frame.</summary>
        private static Sprite OwlFrame(int id) => BLINK && BlinkSprite != null && Blinking(id) ? BlinkSprite : Tile.DuckSprite;
        private void BlinkBoardOwls(GridManager grid)
        {
            if (!BLINK || BlinkSprite == null || Tile.DuckSprite == null) return;
            for (int c = 0; c < RulesEngine.COLS; c++) for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                var t = grid.GetTile(c, r); if (t == null || !t.IsDropTargetVisual) continue;
                var sr = t.GetComponent<SpriteRenderer>(); if (sr == null || !sr.enabled) continue;
                var want = OwlFrame(100 + c);   // keyed by column: an owl keeps its rhythm all the way down and into its nest
                if (sr.sprite == Tile.DuckSprite || sr.sprite == BlinkSprite) { if (sr.sprite != want) sr.sprite = want; }
            }
        }

        private static Sprite s_sprite; private static bool s_tried;
        private class Slot { public SpriteRenderer Nest, Owl; public int Col = -1; public float LandAt = -1f; public Vector3 From; public bool Puffed; }
        private readonly List<Slot> _slots = new List<Slot>();
        private static readonly Dictionary<int, Vector3> s_landFrom = new Dictionary<int, Vector3>();

        /// <summary>An owl reached the bottom row of <paramref name="col"/>: it drops from there into the nest and stays.</summary>
        public static void Land(int col, Vector3 fromWorld)
        {
            if (!ON || !STAY_IN_NEST) return;
            if (RulesEngine.DuckColsHome.Add(col)) { s_landFrom[col] = fromWorld; WDLog.Log($"[Nest] owl home in column {col} ({RulesEngine.DuckColsHome.Count} home)"); }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            var go = new GameObject("OwlNests"); DontDestroyOnLoad(go); go.AddComponent<OwlNests>();
        }

        private static Sprite NestSprite
        {
            get
            {
                if (s_tried) return s_sprite;
                s_tried = true;
                var tex = Resources.Load<Texture2D>(ASSET);
                if (tex != null) s_sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
                WDLog.Log($"[Nest] {ASSET} -> {(tex != null ? tex.width + "x" + tex.height : "MISSING")}");
                return s_sprite;
            }
        }

        private void LateUpdate()
        {
            var grid = GridManager.Instance; var sm = SurvivalManager.Instance;
            // 11-24 Spencer: "when you complete the level and the bonus shows i think they should stay there while the bonus is running" - the
            // stage counter moves on at the win, so this no longer asks GoalsActiveFor: the nests stay until the NEXT level installs its goals.
            bool show = ON && grid != null && sm != null && LevelRules.HasDuckGoal && NestSprite != null;
            if (show) BlinkBoardOwls(grid);
            List<int> cols = show ? RulesEngine.DuckHomeColumns(LevelRules.DuckGoalTarget) : null;
            int want = show ? cols.Count : 0;
            while (_slots.Count < want)
            {
                var s = new Slot();
                var og = new GameObject("NestOwl"); og.transform.SetParent(transform, false);
                s.Owl = og.AddComponent<SpriteRenderer>(); s.Owl.sortingOrder = ORDER; s.Owl.enabled = false;
                var go = new GameObject("Nest"); go.transform.SetParent(transform, false);
                s.Nest = go.AddComponent<SpriteRenderer>(); s.Nest.sprite = NestSprite; s.Nest.sortingOrder = ORDER + 1;
                _slots.Add(s);
            }
            for (int i = 0; i < _slots.Count; i++)
            {
                var s = _slots[i]; bool on = i < want;
                if (s.Nest.enabled != on) s.Nest.enabled = on;
                if (!on) { s.Owl.enabled = false; s.Col = -1; s.LandAt = -1f; continue; }
                int col = cols[i];
                if (s.Col != col) { s.Col = col; s.LandAt = -1f; }
                Vector3 c = grid.CellToWorld(col, 0);
                float bottom = c.y - grid.RowPitch * 0.5f, cell = grid.CellSize;
                Vector3 nestPos = new Vector3(c.x + grid.BoardSlideOffsetX, bottom - cell * DROP_CELLS, c.z);
                s.Nest.transform.position = nestPos; s.Nest.transform.localScale = new Vector3(cell * WIDTH_CELLS, cell * WIDTH_CELLS, 1f);
                bool home = STAY_IN_NEST && RulesEngine.DuckColsHome.Contains(col) && Tile.DuckSprite != null;
                if (!home) { s.Owl.enabled = false; s.LandAt = -1f; continue; }
                if (s.LandAt < 0f) { s.LandAt = Time.unscaledTime; s.Puffed = false; s.From = s_landFrom.TryGetValue(col, out var f) ? f : nestPos; }
                Vector3 sit = nestPos + new Vector3(0f, cell * OWL_UP_CELLS, 0f);
                // LANDING (11-24 Spencer: "some kind of landing cartoonish animation when the owl drops on the nest"): it falls stretched,
                // SQUASHES flat on the nest (the nest gives under it), bounces up once, lands again softly and settles. Then it breathes.
                // 11-24 Spencer: "it also really slows when it hits the bottom of the board, it should fall at the same rate" - the drop
                // into the nest runs at the BOARD'S fall speed (GridManager GRAVITY_FALL_SPEED), straight on, no ease; the keyframes
                // start at the impact.
                float raw = Time.unscaledTime - s.LandAt, size = cell * OWL_CELLS;
                float fallDur = Mathf.Max(0.02f, Vector3.Distance(s.From, sit) / Mathf.Max(0.01f, FALL_SPEED * cell));
                float t = raw < fallDur ? K_T[1] * 0.999f : K_T[1] + (raw - fallDur);
                float sx = Key(t, K_T, K_SX), sy = Key(t, K_T, K_SY), up = Key(t, K_T, K_UP) * cell;
                Vector3 pos;
                if (raw < fallDur) pos = Vector3.Lerp(s.From, sit, raw / fallDur);
                else pos = sit + new Vector3(0f, up, 0f);
                if (raw >= fallDur && !s.Puffed)
                {
                    s.Puffed = true;
                    if (LAND_PUFF > 0) GameParticles.Instance?.PlayShimmerBurst(nestPos + new Vector3(0f, cell * 0.1f, 0f), LAND_PUFF);
                }
                if (t > K_T[K_T.Length - 1]) { float b = Mathf.Sin((t - K_T[K_T.Length - 1]) * IDLE_SPEED + i * 1.7f) * IDLE_AMOUNT; sy = 1f + b; sx = 1f - b * 0.6f; }
                float since = raw - fallDur;   // seconds since it hit the nest
                bool cheer = CHEER && CheerSprite != null && since >= CHEER_FROM && since < CHEER_FROM + CHEER_SECS;
                bool party = CHEER && CheerSprite != null && want > 0 && RulesEngine.DuckColsHome.Count >= want && since > K_T[K_T.Length - 1] - K_T[1];
                if (party)
                {   // everyone is home: hop with wings up, one column after the other
                    float ph = Mathf.Repeat((Time.unscaledTime - i * PARTY_WAVE) / PARTY_BEAT, 1f);
                    if (ph < PARTY_UP) { cheer = true; pos.y += Mathf.Sin(ph / PARTY_UP * Mathf.PI) * cell * PARTY_HOP; }
                }
                pos.y -= (1f - sy) * size * 0.5f;   // squash from the feet, not the middle
                s.Owl.sprite = cheer ? CheerSprite : OwlFrame(100 + col); s.Owl.enabled = true;
                s.Owl.transform.position = pos;
                s.Owl.transform.localScale = new Vector3(size * sx, size * sy, 1f);
                float nsy = Key(t, K_T, K_NEST), nw = cell * WIDTH_CELLS;
                s.Nest.transform.localScale = new Vector3(nw * (2f - nsy), nw * nsy, 1f);
                s.Nest.transform.position = nestPos - new Vector3(0f, (1f - nsy) * nw * 0.18f, 0f);
            }
        }
    }
}
