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
        private Coroutine _ghostDemoCoroutine;
        private GameObject _ghostGO;

        private static readonly Color SUBTLE_GLOW_COLOR    = new Color(1.00f, 0.92f, 0.45f, 1f);
        private static readonly Color PROMINENT_PULSE_COLOR = new Color(1.00f, 0.78f, 0.20f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            ClearAll();
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

            if (cues.subtleGlowCells != null)
                foreach (var c in cues.subtleGlowCells)
                    AddBreathingPulse(c, amplitude: 1.05f, colorTint: SUBTLE_GLOW_COLOR, period: 1.4f);

            if (cues.prominentPulseCells != null)
                foreach (var c in cues.prominentPulseCells)
                    AddBreathingPulse(c, amplitude: 1.12f, colorTint: PROMINENT_PULSE_COLOR, period: 0.9f);

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

        /// <summary>Clears pulses, ghosts, amp flag. Called on level end and on Apply() for a new level.</summary>
        public void ClearAll()
        {
            AmpedPrimingPulse = false;
            for (int i = 0; i < _activeTweens.Count; i++)
                _activeTweens[i]?.Kill();
            _activeTweens.Clear();

            if (_ghostDemoCoroutine != null) { StopCoroutine(_ghostDemoCoroutine); _ghostDemoCoroutine = null; }
            if (_ghostGO != null) { Destroy(_ghostGO); _ghostGO = null; }
        }

        // ── Pulse implementation ────────────────────────────────────────────────

        private void AddBreathingPulse(CellCoord cell, float amplitude, Color colorTint, float period)
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
                    Color.Lerp(baseColor, colorTint, 0.55f),
                    period)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo);
                _activeTweens.Add(colorTween);
            }
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
