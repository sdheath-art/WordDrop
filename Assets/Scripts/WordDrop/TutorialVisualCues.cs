using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Phase 6 visual cue manager — replaces verbose text coaching with subtle
    /// on-tile signals per the Royal Match / Wordscapes pattern.
    ///
    /// Reads LevelData.visualCues at level start and drives:
    ///   • Subtle glow (breathing pulse, ~1.05 scale, yellow tint) on specified cells
    ///   • Prominent pulse (1.12 scale, brighter) on cells that need spectacle setup
    ///   • Ghost-play demo — animates a translucent letter falling into a target cell
    ///     after N seconds of player inactivity. Fires once per level instance.
    ///   • AmpedPrimingPulse flag — exposes a static for Tile.cs's pulse coroutine to
    ///     crank the intensity (Tile reads LevelController.AmpedPrimingPulse).
    ///
    /// Clears all effects on level end or next StartLevel call.
    /// </summary>
    public class TutorialVisualCues : MonoBehaviour
    {
        public static TutorialVisualCues Instance { get; private set; }

        /// <summary>Set by Apply() when a level requests it; consumed by Tile.GoldPulseLoop / PrimedGlow paths if they opt in.</summary>
        public static bool AmpedPrimingPulse { get; private set; }

        private readonly List<Tween> _activeTweens = new List<Tween>();
        private readonly List<GameObject> _activeGOs = new List<GameObject>();
        private Coroutine _ghostDemoCoroutine;
        private GameObject _ghostGO;

        // Dedicated reference to the drop-arrow GameObject so the first_word
        // handler can target it specifically. Other cues (subtle pulse,
        // prominent pulse, ghost demo) have different teaching-beat lifecycles
        // and retire only at level end via ClearAll — this one retires when
        // the player has formed their first word (its teaching purpose met).
        private GameObject _dropArrowGO;

        // Subtle pulse (cyan/teal) = UI/interact vocabulary. Visually distinct
        // from the warm/pink primed-word vocabulary so tutorial cues don't blur
        // with gameplay state. Blend saturated (0.85) so the tint actually reads
        // on a white tile — 0.55 was imperceptible per L1 playtest.
        private static readonly Color SUBTLE_GLOW_COLOR     = new Color(0.25f, 0.85f, 1.00f, 1f);
        private static readonly Color PROMINENT_PULSE_COLOR = new Color(1.00f, 0.78f, 0.20f, 1f);

        // Drop-arrow color matches the cyan UI/interact vocabulary + LEVEL title
        // accent. Keeps tutorial cues in one visual family regardless of cue type.
        private static readonly Color ARROW_COLOR = new Color(0.25f, 0.85f, 1.00f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void Start()
        {
            // Subscribe after SceneBootstrap has created LevelController (Awake
            // ordering isn't guaranteed; Start runs after all Awakes complete).
            if (LevelController.Instance != null)
            {
                LevelController.Instance.OnFirstWord -= HandleFirstWord;
                LevelController.Instance.OnFirstWord += HandleFirstWord;
            }
        }

        private void OnDestroy()
        {
            if (LevelController.Instance != null)
                LevelController.Instance.OnFirstWord -= HandleFirstWord;
            ClearAll();
        }

        // Retire the drop arrow once its teaching purpose is met — player has
        // formed their first word. Other cues stay until level end.
        private void HandleFirstWord()
        {
            FadeOutDropArrow();
        }

        // ── Public entry point ──────────────────────────────────────────────────

        /// <summary>
        /// Called by LevelController.StartLevel after tiles are placed. Idempotent:
        /// clears any prior cues before applying new ones. Safe to call with null data.
        /// </summary>
        public void Apply(LevelData data)
        {
            ClearAll();
            if (data == null || data.visualCues == null) return;
            var cues = data.visualCues;

            AmpedPrimingPulse = cues.ampedPrimingPulse;

            // Subtle pulse uses saturated 0.85 blend so cyan is clearly visible;
            // prominent stays at the legacy 0.55 so L3's CAT+POT emphasis doesn't
            // over-saturate onto the primed pink.
            if (cues.subtleGlowCells != null)
                foreach (var c in cues.subtleGlowCells)
                    AddBreathingPulse(c, amplitude: 1.09f, colorTint: SUBTLE_GLOW_COLOR, period: 1.4f, colorBlend: 0.85f);

            if (cues.prominentPulseCells != null)
                foreach (var c in cues.prominentPulseCells)
                    AddBreathingPulse(c, amplitude: 1.12f, colorTint: PROMINENT_PULSE_COLOR, period: 0.9f, colorBlend: 0.55f);

            if (cues.dropArrowCol >= 0)
                AddDropArrow(cues.dropArrowCol);

            if (cues.ghostDemoIdleSeconds > 0
                && cues.ghostDemoCell != null
                && !string.IsNullOrEmpty(cues.ghostDemoLetter))
            {
                _ghostDemoCoroutine = StartCoroutine(GhostDemoAfterIdle(
                    cues.ghostDemoIdleSeconds,
                    char.ToUpperInvariant(cues.ghostDemoLetter[0]),
                    cues.ghostDemoCell));
            }
        }

        /// <summary>Clears pulses, ghosts, arrows, amp flag. Called on level end and on Apply() for a new level.</summary>
        public void ClearAll()
        {
            AmpedPrimingPulse = false;
            for (int i = 0; i < _activeTweens.Count; i++)
                _activeTweens[i]?.Kill();
            _activeTweens.Clear();

            for (int i = 0; i < _activeGOs.Count; i++)
                if (_activeGOs[i] != null) Destroy(_activeGOs[i]);
            _activeGOs.Clear();
            _dropArrowGO = null;

            if (_ghostDemoCoroutine != null) { StopCoroutine(_ghostDemoCoroutine); _ghostDemoCoroutine = null; }
            if (_ghostGO != null) { Destroy(_ghostGO); _ghostGO = null; }
        }

        // ── Arrow retirement on first_word ──────────────────────────────────────
        // Fade the drop arrow out when the player has formed their first word.
        // 0.3s alpha fade → destroy GameObject → null the reference. Matches the
        // "reactive prompt" vocabulary (first_word tutorial prompt also retires
        // here — arrow and prompt exit together). Other cues stay until level end.
        private void FadeOutDropArrow()
        {
            if (_dropArrowGO == null) return;
            GameObject arrowGO = _dropArrowGO;
            _dropArrowGO = null;

            var sr = arrowGO.GetComponent<SpriteRenderer>();
            if (sr == null)
            {
                Destroy(arrowGO);
                return;
            }

            // Kill in-flight arrow tweens (alpha breathe + y-bob) so the fade
            // animates cleanly without yoyo-ing back to full opacity.
            arrowGO.transform.DOKill();
            DOTween.Kill(sr);

            Color start = sr.color;
            Color end = new Color(start.r, start.g, start.b, 0f);
            DOTween.To(() => sr.color, v => sr.color = v, end, 0.3f)
                .SetEase(Ease.InCubic)
                .OnComplete(() => { if (arrowGO != null) Destroy(arrowGO); });
        }

        // ── Pulse implementation ────────────────────────────────────────────────

        private void AddBreathingPulse(CellCoord cell, float amplitude, Color colorTint, float period, float colorBlend = 0.55f)
        {
            if (cell == null) return;
            var grid = GridManager.Instance;
            if (grid == null) return;

            Tile tile = grid.GetTile(cell.x, cell.y);
            if (tile == null) return;
            Transform t = tile.transform;
            Vector3 baseScale = t.localScale;

            // Scale breathe
            Tween scaleTween = t.DOScale(baseScale * amplitude, period)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
            _activeTweens.Add(scaleTween);

            // Color breathe (uses SpriteRenderer if present). Tile primes can overwrite
            // this color — that's fine; the cue is a start-of-level hint, not permanent.
            SpriteRenderer sr = tile.GetComponentInChildren<SpriteRenderer>();
            if (sr != null)
            {
                Color baseColor = sr.color;
                Tween colorTween = DOTween.To(
                    () => sr.color,
                    v => sr.color = v,
                    Color.Lerp(baseColor, colorTint, colorBlend),
                    period)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo);
                _activeTweens.Add(colorTween);
            }
        }

        // ── Drop arrow ──────────────────────────────────────────────────────────
        // Column-targeted cue for "drop a letter here" tutorials (L1, L2, and
        // later mechanic intros that want column-drop guidance). Sits above the
        // board via GridManager.GetColumnSpawnPosition — the same anchor existing
        // tile-drop animations use, so there's no collision with grid cells or
        // primed glows at the top row.

        private void AddDropArrow(int col)
        {
            var grid = GridManager.Instance;
            if (grid == null) return;
            if (col < 0 || col >= GridManager.COLS) return;

            Vector3 anchorPos = grid.GetColumnSpawnPosition(col);

            GameObject arrowGO = new GameObject("TutorialDropArrow");
            var sr = arrowGO.AddComponent<SpriteRenderer>();
            sr.sprite = CreateDownArrowSprite();
            sr.color = ARROW_COLOR;
            sr.sortingOrder = 7;
            arrowGO.transform.position = anchorPos;
            float cs = grid.CellSize;
            arrowGO.transform.localScale = new Vector3(cs * 0.55f, cs * 0.55f, 1f);
            _activeGOs.Add(arrowGO);
            _dropArrowGO = arrowGO;

            // Alpha breathe: 1.0 ↔ 0.45 over 0.6s half-cycle → 1.2s full period.
            Tween alphaTween = DOTween.To(
                () => sr.color,
                v => sr.color = v,
                new Color(ARROW_COLOR.r, ARROW_COLOR.g, ARROW_COLOR.b, 0.45f),
                0.6f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
            _activeTweens.Add(alphaTween);

            // Gentle y-bob (±15% of cellSize, ~0.8s half-cycle) for "come here" motion.
            float bobAmount = cs * 0.15f;
            Tween bobTween = arrowGO.transform.DOMoveY(anchorPos.y - bobAmount, 0.8f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
            _activeTweens.Add(bobTween);
        }

        private static Sprite _downArrowSprite;
        private static Sprite CreateDownArrowSprite()
        {
            if (_downArrowSprite != null) return _downArrowSprite;

            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color32 clear = new Color32(0, 0, 0, 0);
            Color32 white = new Color32(255, 255, 255, 255);
            Color32[] px = new Color32[size * size];
            for (int i = 0; i < px.Length; i++) px[i] = clear;

            // Filled downward-pointing triangle. Apex at y=0 (bottom of texture),
            // base at y=size-1 (top). Unity sprite Y grows upward, so with default
            // rotation the apex reads as "down" in world space.
            for (int y = 0; y < size; y++)
            {
                float t = (float)y / (size - 1); // 0 at apex, 1 at base
                int halfWidth = Mathf.RoundToInt((size / 2f) * t);
                int centerX = size / 2;
                int xMin = Mathf.Max(0, centerX - halfWidth);
                int xMax = Mathf.Min(size - 1, centerX + halfWidth);
                for (int x = xMin; x <= xMax; x++)
                    px[y * size + x] = white;
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            tex.filterMode = FilterMode.Bilinear;
            _downArrowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _downArrowSprite;
        }

        // ── Ghost demo ──────────────────────────────────────────────────────────

        private IEnumerator GhostDemoAfterIdle(int idleSeconds, char letter, CellCoord target)
        {
            // Simple stopwatch. Skip the demo if the level ended OR the player has
            // already scored a word (CurrentScore > 0 is a cheap proxy for activity).
            float elapsed = 0f;
            while (elapsed < idleSeconds)
            {
                elapsed += Time.deltaTime;
                var lc = LevelController.Instance;
                if (lc == null || !lc.IsActive) yield break;
                if (lc.CurrentScore > 0) yield break;
                yield return null;
            }

            yield return ShowGhostDrop(letter, target);
        }

        private IEnumerator ShowGhostDrop(char letter, CellCoord target)
        {
            var grid = GridManager.Instance;
            if (grid == null) yield break;

            Vector3 endPos = grid.CellToWorld(target.x, target.y);
            Vector3 startPos = new Vector3(endPos.x, endPos.y + grid.CellSize * 5f, endPos.z);

            _ghostGO = new GameObject("TutorialGhostLetter");
            var sr = _ghostGO.AddComponent<SpriteRenderer>();
            sr.sprite = TileRenderer.CreateSolidRoundedRect(128, 128, 20, Color.white);
            sr.color = new Color(1f, 1f, 1f, 0.55f);
            sr.sortingOrder = 6;
            _ghostGO.transform.position = startPos;
            float cs = grid.CellSize;
            _ghostGO.transform.localScale = new Vector3(cs * 0.9f / 1.28f, cs * 0.9f / 1.28f, 1f);

            // Fade in
            var fadeIn = DOTween.To(() => sr.color, v => sr.color = v, new Color(1f, 1f, 1f, 0.8f), 0.4f);
            yield return fadeIn.WaitForCompletion();

            // Drop
            var drop = _ghostGO.transform.DOMove(endPos, 0.5f).SetEase(Ease.InQuad);
            yield return drop.WaitForCompletion();

            // Hold briefly
            yield return new WaitForSeconds(1.2f);

            // Fade out
            var fadeOut = DOTween.To(() => sr.color, v => sr.color = v, new Color(1f, 1f, 1f, 0f), 0.5f);
            yield return fadeOut.WaitForCompletion();

            if (_ghostGO != null) { Destroy(_ghostGO); _ghostGO = null; }
            _ghostDemoCoroutine = null;
        }
    }
}
