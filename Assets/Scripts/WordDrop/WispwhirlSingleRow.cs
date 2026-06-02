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
            => "Tap a tile — rearranges all non-primed letters on the board.";
        // 2026-06-01: NeedsTarget = true per Spencer. Requires a tile tap to
        // confirm so the player can't lose a charge by an accidental tap on
        // the slot. The tap location is ignored — the shuffle still operates
        // board-wide. Same UX as the other boosters: tap arms aim mode, board
        // tap confirms, armed-slot ✕ cancels.
        public override bool NeedsTarget => true;

        // Animation timings mirror HandManager.ShuffleHandAnimated.
        private const float SHAKE_DURATION  = 0.25f;
        private const float SETTLE_DURATION = 0.18f;

        public override void ResolveWithTarget(int col, int row, System.Action onComplete)
        {
            // Tap target is ignored — the shuffle is board-wide. The tap is
            // purely a "confirm" gesture. Delegates to the legacy Activate
            // body below.
            Activate(onComplete);
        }

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

            // ── Phase 2: SHUFFLE LETTERS IN PLACE ────────────────────────────
            // 2026-05-30: tiles no longer TRAVEL between cells. Each tile stays
            // in its original cell position; the LETTER and underlying rules
            // data swap to match the permutation. Same approach as the Edit
            // booster's per-tile animation (HandManager.cs:3048-3080) extended
            // to N tiles. Fisher-Yates permutation; SurvivalRng so daily seeds
            // remain stable.
            int[] perm = new int[n];
            for (int i = 0; i < n; i++) perm[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                int tmp = perm[i]; perm[i] = perm[j]; perm[j] = tmp;
            }

            // Update rules engine: each position gets the SOURCE's data.
            // Tile objects don't move between cells — grid's tile references
            // stay pinned. Only the letter + rules data swap.
            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                var pos = positions[i];
                rules.SetCell(pos.x, pos.y, datas[src]);
            }

            // Update each tile's VISIBLE letter to match its new data, snap
            // back to rest position + identity rotation (kills the jitter).
            for (int i = 0; i < n; i++)
            {
                int src = perm[i];
                Tile t = tiles[i];
                if (t == null) continue;
                t.transform.DOKill();
                t.transform.position = restPositions[i];
                t.transform.localRotation = Quaternion.identity;
                t.SetLetter(datas[src].Letter);
            }

            // ── Phase 3: LANDING SQUISH ─────────────────────────────────────
            // Same settle pop the Edit booster uses on each tile — in-place
            // squash-and-stretch with no positional travel. Sound suppressed
            // because firing PlayTileDrop on N tiles simultaneously produces
            // a loud audio chord — PlayShuffle at the start of the shuffle
            // is enough to carry the audio for the whole sequence.
            for (int i = 0; i < n; i++)
            {
                Tile t = tiles[i];
                if (t == null) continue;
                t.PlayLandingSquish(playSound: false);
            }

            yield return new WaitForSeconds(0.30f);

            Debug.Log($"[JesterHat] Shuffled {n} tiles board-wide (primed/wild/stone preserved)");
            onComplete?.Invoke();
        }
    }
}
