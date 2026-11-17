using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>GEM TWINKLE (2026-11-02, Spencer picked B of composition/gem_glint/gem_glint_mock.html: "the star twinkle without
    /// the sweep"). Every few seconds ONE random resting goal gem gets a small four-point star that pops on its top-left corner,
    /// spins a quarter turn and shrinks away (0.6 s). Draws the eye to the goals; no shine sweep. Art: Resources/FX/gem_twinkle.png.
    /// The star is parented to the tile, so it moves / dies with it. ENABLED = false turns it off.</summary>
    public class GemTwinkle : MonoBehaviour
    {
        public static bool  ENABLED = true;
        public static float PERIOD = 3.0f, JITTER = 0.8f;    // seconds between twinkles, +/- jitter
        public static float DUR = 0.8f, PEAK_AT = 0.30f, HOLD = 0.12f;   // total time; fraction spent growing; seconds held at full size (11-02: was .6 / .35 / 0)
        public static float SIZE = 0.72f;                    // star size as a fraction of the visible tile (11-02 Spencer 'very faint': was .46)
        public static float GLOW_SIZE = 1.5f;                // additive bloom copy behind the star, x the star
        public static Color GLOW_HDR = Tile.GEM_MAGENTA ? new Color(2.8f, 1.6f, 2.8f, 1f) : Tile.GEM_RUBY ? new Color(3.0f, 1.5f, 1.7f, 1f) : new Color(1.4f, 2.6f, 3.0f, 1f);   // 11-16 ruby: pink-white glint   // HDR cyan-white: blooms (world-space sprite, linear project)
        public static Vector2 SPOT = new Vector2(-0.36f, 0.38f);   // star centre from the tile centre, in visible-tile units

        private static GemTwinkle s_inst;
        private static Sprite s_star;
        private static Material s_mat, s_glowMat;
        private float _next;
        private Tile _last;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (s_inst != null) return;
            var go = new GameObject("GemTwinkle"); DontDestroyOnLoad(go);
            s_inst = go.AddComponent<GemTwinkle>();
        }

        private void Update()
        {
            if (!ENABLED || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + PERIOD + Random.Range(-JITTER, JITTER);
            var g = GridManager.Instance;
            bool locked = LevelController.Instance != null && LevelController.Instance.IsInputLocked;
            if (g == null || Time.timeScale <= 0f || SurvivalManager.AwaitingNextLevel || locked) return;

            Tile pick = null; int seen = 0;
            for (int c = 0; c < RulesEngine.COLS; c++)
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    var t = g.GetTile(c, r);
                    if (t == null || !t.ShowingGemFace || t.IsAnimating || !t.gameObject.activeInHierarchy) continue;
                    seen++;
                    if (Random.Range(0, seen) == 0 && (t != _last || seen == 1)) pick = t;   // reservoir pick, avoid the same gem twice running
                }
            if (pick == null) return;
            _last = pick;
            Spawn(pick, g.CellSize * Tile.TILE_DISPLAY_RATIO);
        }

        private static void Spawn(Tile t, float tileWorld)
        {
            if (s_star == null)
            {
                var tex = Resources.Load<Texture2D>("FX/gem_twinkle");
                if (tex == null) { ENABLED = false; WDLog.Log("[GemTwinkle] FX/gem_twinkle missing - off"); return; }
                s_star = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);   // 1 world unit wide
            }
            var go = new GameObject("GemTwinkle_star");
            go.transform.SetParent(t.transform, false);
            Vector3 ls = t.transform.lossyScale;
            float sx = Mathf.Abs(ls.x) > 1e-5f ? ls.x : 1f, sy = Mathf.Abs(ls.y) > 1e-5f ? ls.y : 1f;
            go.transform.localPosition = new Vector3(SPOT.x * tileWorld / sx, SPOT.y * tileWorld / sy, -0.2f);
            Vector3 full = new Vector3(SIZE * tileWorld / sx, SIZE * tileWorld / sy, 1f);
            go.transform.localScale = Vector3.zero;
            var sr = go.AddComponent<SpriteRenderer>();
            // 11-02 FIX: a fresh SpriteRenderer's default material drew NOTHING here (log showed stars spawning at the right place /
            // size / order, Spencer saw none). Every working FX sprite sets Sprites/Default explicitly (WordDropFX) - do the same.
            if (s_mat == null) s_mat = new Material(Shader.Find("Sprites/Default"));
            sr.sharedMaterial = s_mat;
            sr.sprite = s_star; sr.sortingOrder = t.FaceSortingOrder + 4;   // above this face + letter (+2); the row above starts at +3, so it also clears that face
            sr.sortingLayerID = t.FaceSortingLayerID;
            // 11-02 Spencer: "very faint and unnoticible" - a white star on a light teal gem has little contrast. Add an ADDITIVE HDR copy
            // behind it so it actually emits (bloom), plus a bigger star and a short hold at full size.
            if (s_glowMat == null)
            {
                var sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Legacy Shaders/Particles/Additive");
                if (sh != null) s_glowMat = new Material(sh);
            }
            if (s_glowMat != null)
            {
                var gGO = new GameObject("glow"); gGO.transform.SetParent(go.transform, false);
                gGO.transform.localScale = Vector3.one * GLOW_SIZE; gGO.transform.localPosition = new Vector3(0f, 0f, 0.01f);
                var gsr = gGO.AddComponent<SpriteRenderer>();
                gsr.sharedMaterial = s_glowMat; gsr.sprite = s_star; gsr.color = GLOW_HDR;
                gsr.sortingLayerID = sr.sortingLayerID; gsr.sortingOrder = sr.sortingOrder - 1;
            }
            float up = DUR * PEAK_AT, down = Mathf.Max(0.05f, DUR - up - HOLD);
            DOTween.Sequence().SetLink(go)
                .Append(go.transform.DOScale(full, up).SetEase(Ease.OutBack))
                .AppendInterval(HOLD)
                .Append(go.transform.DOScale(Vector3.zero, down).SetEase(Ease.InQuad))
                .Insert(0f, go.transform.DOLocalRotate(new Vector3(0f, 0f, -90f), DUR, RotateMode.FastBeyond360).SetEase(Ease.Linear))
                .OnComplete(() => { if (go != null) Destroy(go); });
        }
    }
}
