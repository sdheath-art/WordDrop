using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Path A (2026-05-28) — "Jester Hat" full-board shuffle. Player taps the
    /// booster button (NO target — untargeted). All non-primed, non-wild,
    /// non-stone tiles across the entire board are rearranged into new positions.
    ///
    /// Preserves in place:
    ///   - Primed tiles (any cell of any primed word — registry-checked)
    ///   - Wild tiles (the ? sentinel)
    ///   - Stone tiles (rocks — immovable terrain)
    ///   - Empty cells (no source to move)
    ///
    /// Animation matches the hand-shuffle pattern: shake-in-place → swap data
    /// → DOMove to destination with OutBack overshoot + brief rotation. The
    /// per-tile shake is what makes the booster feel like a JESTER tossing
    /// things around (vs the previous flat DOMove which read as a slide).
    ///
    /// Filename and Id are kept as "WispwhirlSingleRow" / "wispwhirl_row" for
    /// analytics + memory continuity. Display name + mechanic both changed.
    /// The single-row variant is gone — full-board is the only mode now.
    ///
    /// Uses SurvivalRng so shuffle is deterministic in Daily mode.
    /// </summary>
    public class WispwhirlSingleRow : Booster
    {
        public override string Id               => "wispwhirl_row";
        public override string DisplayName      => "Jester Hat";
        public override string ShortDescription
            => "Rearranges all non-primed letters on the board.";
        public override bool NeedsTarget => false;

        // Animation timings mirror HandManager.ShuffleHandAnimated.
        private const float SHAKE_DURATION  = 0.25f;
        private const float SETTLE_DURATION = 0.18f;

        public override void Activate(System.Action onComplete)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { onComplete?.Invoke(); return; }

            // 1. Build the set of cells that must STAY PUT — every cell that's
            //    part of any primed word. Belt-and-suspenders: even if per-cell
            //    GetPrimedWordsContaining missed something (registry edge case),
            //    iterating GetAllPrimedWords directly catches it.
            var preservedCells = new HashSet<Vector2Int>();
            if (rules.PrimedRegistry != null)
            {
                var allPrimed = rules.PrimedRegistry.GetAllPrimedWords();
                if (allPrimed != null)
                {
                    foreach (var pw in allPrimed)
                    {
                        if (pw == null || pw.Cells == null) continue;
                        foreach (var cell in pw.Cells)
                            preservedCells.Add(cell);
                    }
                }
            }

            // 2. Collect all shuffle-eligible cells board-wide.
            //    Skip: empty cells, stone tiles, wild tiles, any primed cell.
            var positions = new List<Vector2Int>();
            var datas     = new List<RulesCellData>();
            var tiles     = new List<Tile>();
            var gridCells = new List<CellData>();

            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    var rulesData = rules.GetCell(c, r);
                    if (rulesData == null) continue;
                    if (rulesData.IsStone) continue;
                    if (rulesData.IsWild)  continue;
                    if (preservedCells.Contains(new Vector2Int(c, r))) continue;

                    positions.Add(new Vector2Int(c, r));
                    datas.Add(rulesData);
                    tiles.Add(grid.GetTile(c, r));
                    gridCells.Add(grid.GetCell(c, r));
                }
            }

            int n = positions.Count;
            if (n < 2)
            {
                Debug.Log($"[JesterHat] Skipped — only {n} eligible tile(s) to shuffle");
                GameAudio.Instance?.PlayUIClick();
                onComplete?.Invoke();
                return;
            }

            // 3. Hand off to a coroutine so we can shake-over-time before the
            //    final settle move. Hosted on GridManager (MonoBehaviour).
            //    Fire shuffle SFX at the START — matches HandManager hand
            //    shuffle, which plays PlayShuffle() before the shake begins.
            GameAudio.Instance?.PlayShuffle();
            grid.StartCoroutine(AnimateJesterShuffle(
                positions, datas, tiles, gridCells, rules, grid, onComplete));
        }

        private IEnumerator AnimateJesterShuffle(
            List<Vector2Int> positions,
            List<RulesCellData> datas,
            List<Tile> tiles,
            List<CellData> gridCells,
            RulesEngine rules,
            GridManager grid,
            System.Action onComplete)
        {
            int n = positions.Count;

            // Capture rest positions (world space) before any movement.
            Vector3[] restPositions = new Vector3[n];
            for (int i = 0; i < n; i++)
                restPositions[i] = grid.CellToWorld(positions[i].x, positions[i].y);

            // ── Phase 1: SHAKE ─────────────────────────────────────────────
            // Each tile jitters around its current cell. Matches hand-shuffle
            // jitter amplitudes scaled to board cell size.
            float cellSize  = grid.CellSize;
            float posJitter = cellSize * 0.10f;
            float rotJitter = 8f;
            float shakeElapsed = 0f;
            while (shakeElapsed < SHAKE_DURATION)
            {
                shakeElapsed += Time.deltaTime;
                for (int i = 0; i < n; i++)
                {
                    if (tiles[i] == null) continue;
                    Transform t = tiles[i].transform;
                    float ox = Random.Range(-posJitter, posJitter);
                    float oy = Random.Range(-posJitter, posJitter) * 0.5f;
                    t.position = restPositions[i] + new Vector3(ox, oy, 0f);
                    float rz = Random.Range(-rotJitter, rotJitter);
                    t.localRotation = Quaternion.Euler(0f, 0f, rz);
                }
                yield return null;
            }

            // ── Phase 2: SHUFFLE DATA ───────────────────────────────────────
            // Fisher-Yates permutation; SurvivalRng so daily seeds are stable.
            int[] perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                int tmp = perm[i]; perm[i] = perm[j]; perm[j] = tmp;
            }

            // Clear all sources first to avoid mid-place overwrites.
            for (int i = 0; i < n; i++)
            {
                rules.ClearCell(positions[i].x, positions[i].y);
                grid.ClearTileRefAt(positions[i].x, positions[i].y);
            }

            // Place permuted tiles at destinations.
            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                var pos = positions[i];
                rules.SetCell(pos.x, pos.y, datas[src]);
                grid.PlaceTileAt(pos.x, pos.y, tiles[src], gridCells[src]);
            }

            // ── Phase 3: SETTLE ─────────────────────────────────────────────
            // From wherever each tile currently sits (jittered position) to its
            // NEW destination cell. OutBack overshoot mirrors the hand
            // shuffle's snap-into-place feel.
            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                Tile movedTile = tiles[src];
                if (movedTile == null) continue;
                Vector3 destWorld = restPositions[i];
                Transform t = movedTile.transform;
                t.DOKill();
                // OutBack overshoot is proportional to move distance. The hand
                // shuffle used 2f because cards never moved more than ~one card
                // width. Board tiles can move across 5+ cells — 2f exaggerates
                // the overshoot into a glitchy bounce. 0.8f keeps the snap-in
                // feel without the wild fly-past.
                t.DOMove(destWorld, SETTLE_DURATION)
                    .SetEase(Ease.OutBack, 0.8f);
                t.DORotate(Vector3.zero, 0.10f)
                    .SetEase(Ease.OutQuad);
            }

            yield return new WaitForSeconds(SETTLE_DURATION + 0.05f);

            // Snap clean — kill any micro-jitter from overshoot residue.
            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                Tile movedTile = tiles[src];
                if (movedTile == null) continue;
                movedTile.transform.position = restPositions[i];
                movedTile.transform.localRotation = Quaternion.identity;
            }

            Debug.Log($"[JesterHat] Shuffled {n} tiles board-wide (primed/wild/stone preserved)");
            onComplete?.Invoke();
        }
    }
}
