using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Path A (2026-05-28) — always-available booster inventory.
    ///
    /// Player has 4 main boosters from run start, each with persistent charges
    /// that carry across stages (NO per-stage refill — the prior roguelite
    /// "1 charge per stage" model is gone). Starting inventory: 3 charges of
    /// each booster.
    ///
    /// Inventory roster (locked):
    ///   - Bloomburst    → display "Bloom Bomb"    (3x3 area destroy, targeted)
    ///   - BrambleSweep  → display "Comet"         (row/column clear, targeted)
    ///   - WispwhirlSingleRow → display "Jester Hat" (full-board shuffle of non-primed, untargeted)
    ///   - RockCrusher   → display "Stone Splitter" (crushes connected rock cluster, targeted)
    ///
    /// Aim-mode: when player taps a NeedsTarget booster's HUD button, that
    /// booster becomes the ArmedBooster and AimMode flips true. Next board tap
    /// resolves it via ResolveAim(col, row). Cancel via CancelAim() (no charge
    /// consumed).
    ///
    /// Backward-compat shims preserved (ActiveBooster, RefillForStage,
    /// GrantBooster, AddExtraCharge, no-arg TryActivate) so dormant roguelite
    /// code (BoosterChoiceModal) and existing SurvivalManager hooks compile
    /// without changes. See project_wordrop_level_pivot_2026_05_28.md.
    /// </summary>
    public class BoosterManager : MonoBehaviour
    {
        public static BoosterManager Instance { get; private set; }

        // ── Inventory configuration ─────────────────────────────────────────────

        public const int STARTING_CHARGES = 3;

        // Booster IDs — must match Booster.Id values on the concrete subclasses.
        // Display names are owned by each Booster subclass via DisplayName override.
        public const string ID_BLOOMBURST    = "bloomburst";
        public const string ID_BRAMBLE_SWEEP = "bramble_sweep";
        public const string ID_WISPWHIRL     = "wispwhirl_row";   // existing Booster.Id
        public const string ID_ROCK_CRUSHER  = "stone_splitter";

        // ── State ────────────────────────────────────────────────────────────────

        private readonly List<Booster> _inventory = new List<Booster>();
        private readonly Dictionary<string, int> _charges = new Dictionary<string, int>();

        /// <summary>The booster the player just tapped that's waiting for a target
        /// tap (aim mode). Null when not aiming.</summary>
        public Booster ArmedBooster { get; private set; }

        /// <summary>True between TryActivate (NeedsTarget) and ResolveAim/CancelAim.</summary>
        public bool AimMode { get; private set; }

        /// <summary>Fires on any inventory/charge/aim-mode change. HUD subscribes
        /// once and refreshes its full 4-slot display on each invoke.</summary>
        public System.Action OnStateChanged;

        // ── Public accessors ────────────────────────────────────────────────────

        public IReadOnlyList<Booster> Inventory => _inventory;

        public int GetCharges(string boosterId)
            => _charges.TryGetValue(boosterId, out int c) ? c : 0;

        public Booster GetBoosterById(string boosterId)
        {
            for (int i = 0; i < _inventory.Count; i++)
                if (_inventory[i].Id == boosterId) return _inventory[i];
            return null;
        }

        // ── Unity lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ── Run lifecycle ───────────────────────────────────────────────────────

        /// <summary>Called by SurvivalManager.StartSurvival. Populates the four
        /// always-available boosters and gives each STARTING_CHARGES.</summary>
        public void StartRun()
        {
            _inventory.Clear();
            _charges.Clear();
            ArmedBooster = null;
            AimMode = false;

            // Instantiate the four MVP boosters. Each is a plain object — the
            // Booster base is not a MonoBehaviour, so direct `new` is fine.
            _inventory.Add(new Bloomburst());
            _inventory.Add(new BrambleSweep());
            _inventory.Add(new WispwhirlSingleRow());
            _inventory.Add(new RockCrusher());

            foreach (var b in _inventory)
                _charges[b.Id] = STARTING_CHARGES;

            Debug.Log($"[Booster] StartRun: 4 boosters granted, {STARTING_CHARGES} charges each");
            OnStateChanged?.Invoke();
        }

        /// <summary>Called by SurvivalManager.StopSurvival. Wipes state.</summary>
        public void EndRun()
        {
            _inventory.Clear();
            _charges.Clear();
            ArmedBooster = null;
            AimMode = false;
            OnStateChanged?.Invoke();
        }

        // ── Activation (new multi-booster API) ──────────────────────────────────

        /// <summary>Player tapped the HUD button for the booster with this ID.
        /// Untargeted boosters resolve immediately; targeted boosters arm aim mode.</summary>
        public void TryActivate(string boosterId)
        {
            if (AimMode) return;
            // Block booster activation during an active resolution loop —
            // player drops, rewrites, swaps and prior boosters all set
            // MatchController.IsProcessing while their NextStep loop runs.
            // Without this gate a racing UI tap could corrupt the engine
            // step-state mid-cascade.
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;
            var booster = GetBoosterById(boosterId);
            if (booster == null) return;
            if (GetCharges(boosterId) <= 0) return;

            try
            {
                AnalyticsManager.Log("booster_activate",
                    "booster", booster.Id,
                    "charges_before", GetCharges(boosterId));
            }
            catch (System.Exception ex) { Debug.LogError($"[Booster] Analytics threw: {ex.Message}"); }

            if (booster.NeedsTarget)
            {
                ArmedBooster = booster;
                AimMode = true;
                Debug.Log($"[Booster] {booster.DisplayName} → aim mode active");
                OnStateChanged?.Invoke();
            }
            else
            {
                _charges[boosterId] = GetCharges(boosterId) - 1;
                Debug.Log($"[Booster] {booster.DisplayName} resolved (untargeted), charges→{_charges[boosterId]}");
                OnStateChanged?.Invoke();
                // Snapshot occupied cells BEFORE the booster mutates _board so
                // we can derive the set of cleared cells once it completes.
                HashSet<Vector2Int> occupiedBefore =
                    booster.TriggersGravity ? SnapshotOccupiedCells() : null;
                booster.Activate(() =>
                {
                    if (booster.TriggersGravity)
                        StartCoroutine(RunGravityAndCascadeCoroutine(occupiedBefore));
                });
            }
        }

        /// <summary>Player tapped a tile in aim mode. Consumes a charge from the
        /// armed booster and resolves it with the target cell.</summary>
        public void ResolveAim(int col, int row)
        {
            if (!AimMode || ArmedBooster == null) return;
            // Block resolution during an active resolution loop (mirrors
            // TryActivate's gate). Aim mode should never be entered while
            // a drop is resolving, but if input races, hold the booster.
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;
            string id = ArmedBooster.Id;
            if (GetCharges(id) <= 0) { AimMode = false; ArmedBooster = null; OnStateChanged?.Invoke(); return; }

            var booster = ArmedBooster;
            _charges[id] = GetCharges(id) - 1;
            AimMode = false;
            ArmedBooster = null;
            Debug.Log($"[Booster] {booster.DisplayName} resolved at ({col},{row}), charges→{_charges[id]}");
            OnStateChanged?.Invoke();
            // Snapshot occupied cells BEFORE the booster mutates _board so we
            // can derive the set of cleared cells once it completes.
            HashSet<Vector2Int> occupiedBefore =
                booster.TriggersGravity ? SnapshotOccupiedCells() : null;
            booster.ResolveWithTarget(col, row, () =>
            {
                if (booster.TriggersGravity)
                    StartCoroutine(RunGravityAndCascadeCoroutine(occupiedBefore));
            });
        }

        /// <summary>Player tapped outside the board or hit cancel — no charge consumed.</summary>
        public void CancelAim()
        {
            if (!AimMode) return;
            AimMode = false;
            ArmedBooster = null;
            Debug.Log($"[Booster] Aim cancelled (no charge consumed)");
            OnStateChanged?.Invoke();
        }

        /// <summary>Add charges to a specific booster (e.g., rewarded ad reward,
        /// coin purchase, milestone gift). Stacks freely — no cap in MVP.</summary>
        public void AddCharges(string boosterId, int amount)
        {
            if (amount <= 0) return;
            if (!_charges.ContainsKey(boosterId)) return; // not in inventory
            _charges[boosterId] += amount;
            OnStateChanged?.Invoke();
        }

        // ── Gravity + cascade ───────────────────────────────────────────────────

        /// <summary>Snapshot of which cells currently contain tiles. Used to
        /// derive the set of cells a booster cleared by diffing pre- vs
        /// post-Activate board state, without modifying every Booster subclass.</summary>
        private HashSet<Vector2Int> SnapshotOccupiedCells()
        {
            var snap = new HashSet<Vector2Int>();
            var rules = RulesEngine.Instance;
            if (rules == null) return snap;
            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    if (rules.GetCell(c, r) != null)
                        snap.Add(new Vector2Int(c, r));
                }
            }
            return snap;
        }

        /// <summary>Run gravity AND the post-clear cascade after a destructive
        /// booster. Drives the same NextStep resolution loop a player drop uses,
        /// so falling tiles can form / prime / score / detonate words.
        ///
        /// Flow:
        ///   1. Diff pre-booster snapshot vs current board → cleared cells
        ///   2. RulesEngine.BeginCascadeAfterBoosterClear → applies data-layer
        ///      gravity with full bookkeeping (primed registry updates, scored
        ///      key purges, seed cells populated, RemoveInvalidPrimedWords).
        ///      Returns the moves dict.
        ///   3. GridManager.ApplyGravityFromEvents(moves) — animates falls.
        ///   4. HandManager.RunBoosterCascadeChain — drives NextStep with FX
        ///      until the chain finishes; calls FinalizeBoosterCascade.
        ///   5. If nothing moved, finalize directly so engine state returns to Idle.</summary>
        private IEnumerator RunGravityAndCascadeCoroutine(HashSet<Vector2Int> occupiedBefore)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) yield break;

            // 1. Derive cleared cells by diffing the snapshot against current state.
            var clearedCells = new List<Vector2Int>();
            if (occupiedBefore != null)
            {
                foreach (var cell in occupiedBefore)
                {
                    if (rules.GetCell(cell.x, cell.y) == null)
                        clearedCells.Add(cell);
                }
            }
            Debug.Log($"[BoosterDbg] Cascade snapshotBefore={occupiedBefore?.Count ?? 0} " +
                      $"clearedDiff={clearedCells.Count}");

            // 2. Engine entry: applies gravity + full bookkeeping, returns moves.
            var moves = rules.BeginCascadeAfterBoosterClear(clearedCells, MatchController.PLAYER_HUMAN);

            // 3. Animate gravity if anything fell.
            if (moves != null && moves.Count > 0)
                yield return StartCoroutine(grid.ApplyGravityFromEvents(moves));

            // BoosterDbg: post-gravity audit. Walks every board cell and
            // compares the data (_board) vs visual (_tiles) layer. Logs the
            // exact (col,row) of any desync so we can pin down which cell is
            // showing the "fell into another letter" bug.
            int desyncs = AuditBoardVisualSync(rules, grid);
            if (desyncs > 0)
                Debug.LogError($"[BoosterDbg] Post-gravity desync count: {desyncs} cell(s)");

            // 4. Drive cascade chain via HandManager if anything fell. The chain
            //    coroutine handles WordsScored / Triggers / Exploding / chain
            //    gravity / Complete and calls FinalizeBoosterCascade at the end.
            if (moves != null && moves.Count > 0 && HandManager.Instance != null)
            {
                yield return HandManager.Instance.StartCoroutine(
                    HandManager.Instance.RunBoosterCascadeChain(MatchController.PLAYER_HUMAN));
            }
            else
            {
                // No tiles moved → no cascade possible. Engine is still in
                // GravityApplied phase from BeginCascadeAfterBoosterClear, so
                // finalize manually to return to Idle.
                rules.FinalizeBoosterCascade();
            }

            // Post-cascade audit — after SyncToRulesState ran in the cascade
            // chain (or the no-cascade finalize path), the visual should match
            // the data. Any remaining desync is a bug we haven't accounted for.
            int finalDesyncs = AuditBoardVisualSync(rules, grid);
            if (finalDesyncs > 0)
                Debug.LogError($"[BoosterDbg] Post-cascade FINAL desync count: {finalDesyncs} cell(s) " +
                               "(visual layer drifted from data after SyncToRulesState)");
        }

        /// <summary>Diagnostic: walk every board cell and log any (col,row)
        /// where the rules engine data and the visual tile layer disagree.
        /// Returns the number of mismatches detected.</summary>
        private int AuditBoardVisualSync(RulesEngine rules, GridManager grid)
        {
            int mismatches = 0;
            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                for (int r = 0; r < RulesEngine.ROWS; r++)
                {
                    var rulesCell = rules.GetCell(c, r);
                    var visualTile = grid.GetTile(c, r);

                    bool dataHas   = rulesCell != null;
                    bool visualHas = visualTile != null;

                    if (dataHas != visualHas)
                    {
                        Debug.LogWarning($"[BoosterDbg] DESYNC ({c},{r}): " +
                                         $"data={(dataHas ? rulesCell.Letter.ToString() : "null")}, " +
                                         $"visual={(visualHas ? visualTile.Letter.ToString() : "null")}");
                        mismatches++;
                    }
                    else if (dataHas && visualHas && rulesCell.Letter != visualTile.Letter
                             && rulesCell.Letter != '\0')
                    {
                        Debug.LogWarning($"[BoosterDbg] LETTER DESYNC ({c},{r}): " +
                                         $"data='{rulesCell.Letter}', visual='{visualTile.Letter}'");
                        mismatches++;
                    }
                }
            }
            return mismatches;
        }

        // ── Backward-compat shims (legacy API — kept dormant for unused paths) ──

        /// <summary>Legacy alias: returns the currently armed booster (aim mode)
        /// or null. Used to be the "single picked booster" in the old roguelite
        /// model. Kept for compile-compat with dormant BoosterChoiceModal code.</summary>
        public Booster ActiveBooster => ArmedBooster;

        /// <summary>Legacy: total charges of the currently armed booster, or 0.</summary>
        public int ChargesRemaining => ArmedBooster != null ? GetCharges(ArmedBooster.Id) : 0;

        /// <summary>Legacy constant from the old per-stage refill model. Path A
        /// uses persistent charges, no per-stage refill — this is kept only so
        /// dormant code referencing it compiles.</summary>
        public const int CHARGES_PER_STAGE = 1;

        /// <summary>Legacy from the roguelite choice modal. No-op in Path A —
        /// boosters are always-available and granted at StartRun().</summary>
        public void GrantBooster(Booster booster) { /* dormant: Path A grants all 4 at StartRun */ }

        /// <summary>Legacy: per-stage charge refill from the old model. Path A
        /// charges persist across stages (no auto-refill at stage start). This
        /// is now a no-op. To add charges via rewarded ad / coin purchase, use
        /// AddCharges(boosterId, amount) instead.</summary>
        public void RefillForStage() { /* dormant: Path A has persistent charges */ }

        /// <summary>Legacy: paid extra charge for the armed booster. Path A
        /// callers should use AddCharges(boosterId, 1) explicitly. Kept for
        /// compile-compat.</summary>
        public void AddExtraCharge()
        {
            if (ArmedBooster == null) return;
            AddCharges(ArmedBooster.Id, 1);
        }

        /// <summary>Legacy no-arg activator. Path A requires booster ID. Kept
        /// for compile-compat with the existing single-slot BoosterHUDSlot
        /// until Commit 2b refactors the HUD to multi-slot. If something is
        /// armed in aim mode, it does nothing (aim mode owns the next tap).</summary>
        public void TryActivate() { /* dormant: multi-slot HUD will call TryActivate(id) per slot */ }
    }
}
