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
        public const string ID_MALLET        = "mallet";          // 2026-06-24: tap one tile, smash it
        public const string ID_FREE_SWAP     = "free_swap";       // 2026-10-23: swap any two tiles, no move
        public const string ID_AXE          = "axe_column";      // 2026-10-29: chop a whole column (AxeChop)
        public const string ID_SPYGLASS      = "lantern_trace";   // 2026-10-22: the PAID strong hint (LanternTrace, shown as "Spyglass")

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
            _inventory.Add(new Mallet());
            _inventory.Add(new LanternTrace());   // Spyglass — the strong hint (2026-10-22)
            _inventory.Add(new FreeSwap());       // 2026-10-23
            _inventory.Add(new AxeChop());        // 2026-10-29: column clear

            foreach (var b in _inventory)
                _charges[b.Id] = STARTING_CHARGES;

            WDLog.Log($"[Booster] StartRun: {_inventory.Count} boosters granted, {STARTING_CHARGES} charges each");
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
                WDLog.Log($"[Booster] {booster.DisplayName} → aim mode active");
                OnStateChanged?.Invoke();
            }
            else
            {
                _charges[boosterId] = GetCharges(boosterId) - 1;
                WDLog.Log($"[Booster] {booster.DisplayName} resolved (untargeted), charges→{_charges[boosterId]}");
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
        /// <summary>Booster unlock tutorial (BoosterIntro): when set, aim taps outside these cells are ignored.</summary>
        public static HashSet<Vector2Int> TutorialAllowed;
        public void ResolveAim(int col, int row)
        {
            if (!AimMode || ArmedBooster == null) return;
            if (TutorialAllowed != null && !TutorialAllowed.Contains(new Vector2Int(col, row))) { GameAudio.Instance?.PlayUIClick(); return; }
            // Block resolution during an active resolution loop (mirrors
            // TryActivate's gate). Aim mode should never be entered while
            // a drop is resolving, but if input races, hold the booster.
            if (MatchController.Instance != null && MatchController.Instance.IsProcessing) return;
            string id = ArmedBooster.Id;
            if (GetCharges(id) <= 0) { AimMode = false; ArmedBooster = null; OnStateChanged?.Invoke(); return; }

            // TWO-TAP boosters (Free Swap): first tap selects, same tile again deselects, second tile commits the pair.
            if (ArmedBooster.NeedsTwoTargets)
            {
                var p = new Vector2Int(col, row);
                if (!ArmedBooster.CanResolveAt(col, row))
                {
                    WDLog.Log($"[Booster] {ArmedBooster.DisplayName} — can't pick ({col},{row}), staying armed");
                    GameAudio.Instance?.PlayUIClick();
                    return;
                }
                if (FirstPick == null) { FirstPick = p; var pt = GridManager.Instance?.GetTile(col, row); pt?.SetEditSelected(true); BoosterFX.Instance?.ShowPickGlow(pt); GameAudio.Instance?.PlayUIClick(); OnStateChanged?.Invoke(); return; }   // gold pick glow (10-29)
                var a = FirstPick.Value;
                if (a == p) { ClearFirstPick(); GameAudio.Instance?.PlayUIClick(); OnStateChanged?.Invoke(); return; }
                var pairBooster = ArmedBooster;
                ClearFirstPick();
                if (!pairBooster.TryResolvePair(a, p))
                {
                    WDLog.Log($"[Booster] {pairBooster.DisplayName} pair refused — staying armed (no charge spent)");
                    GameAudio.Instance?.PlayButtonClick();
                    OnStateChanged?.Invoke();
                    return;
                }
                _charges[pairBooster.Id] = GetCharges(pairBooster.Id) - 1;
                AimMode = false; ArmedBooster = null;
                WDLog.Log($"[Booster] {pairBooster.DisplayName} resolved ({a.x},{a.y})+({col},{row}), charges→{_charges[pairBooster.Id]}");
                OnStateChanged?.Invoke();
                return;
            }

            // Invalid target (e.g. Stone Splitter tapped on a non-rock) — ignore the
            // tap and stay armed so the player can retry. NO charge consumed.
            if (!ArmedBooster.CanResolveAt(col, row))
            {
                WDLog.Log($"[Booster] {ArmedBooster.DisplayName} — invalid target ({col},{row}), staying armed (no charge spent)");
                GameAudio.Instance?.PlayUIClick();
                return;
            }

            var booster = ArmedBooster;
            _charges[id] = GetCharges(id) - 1;
            AimMode = false;
            ArmedBooster = null;
            WDLog.Log($"[Booster] {booster.DisplayName} resolved at ({col},{row}), charges→{_charges[id]}");
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
        /// <summary>Two-tap boosters: the selected first tile, or null.</summary>
        public Vector2Int? FirstPick { get; private set; }
        private void ClearFirstPick()
        {
            if (FirstPick == null) return;
            var t = GridManager.Instance?.GetTile(FirstPick.Value.x, FirstPick.Value.y);
            if (t != null) t.SetEditSelected(false);
            BoosterFX.Instance?.HidePickGlow();
            FirstPick = null;
        }

        public void CancelAim()
        {
            if (!AimMode) return;
            ClearFirstPick();
            AimMode = false;
            ArmedBooster = null;
            WDLog.Log($"[Booster] Aim cancelled (no charge consumed)");
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
        public HashSet<Vector2Int> SnapshotOccupiedPublic() => SnapshotOccupiedCells();
        /// <summary>Public entry (2026-10-22): the duck collector reuses this exact gravity + refill + cascade path.</summary>
        public IEnumerator RunGravityAndCascadePublic(HashSet<Vector2Int> occupiedBefore) => RunGravityAndCascadeCoroutine(occupiedBefore);
        // ── 11-17 CASCADE LOCK (monkey seed 718401, L19: a swap the player had queued was committed WHILE a mallet's fall was still
        // stepping through its waves, its word set off a TNT in the middle of the booster's cascade, and the board audit found 22
        // cells with a picture and no data; 12 s later the same thing with the axe left "orphan" tiles for a moment). A booster's
        // cascade is not "processing" (its own queued power-up fires need that off), so nothing held the board. Now a flag covers
        // the whole cascade: swaps wait in the swipe queue, double-taps and booster buttons are ignored, the integrity checker
        // waits. Self-clears after 10 s so a lost coroutine can never freeze the board. ──
        private static float s_cascadeSince = -1f;
        public static bool CascadeBusy => s_cascadeSince >= 0f && Time.unscaledTime - s_cascadeSince < 10f;
        private IEnumerator RunGravityAndCascadeCoroutine(HashSet<Vector2Int> occupiedBefore)
        {
            s_cascadeSince = Time.unscaledTime;
            yield return RunGravityAndCascadeInner(occupiedBefore);
            s_cascadeSince = -1f;
        }
        private IEnumerator RunGravityAndCascadeInner(HashSet<Vector2Int> occupiedBefore)
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
            WDLog.Log($"[BoosterDbg] Cascade snapshotBefore={occupiedBefore?.Count ?? 0} " +
                      $"clearedDiff={clearedCells.Count}");

            // 2. Engine entry: applies gravity + full bookkeeping, returns moves.
            var moves = rules.BeginCascadeAfterBoosterClear(clearedCells, MatchController.PLAYER_HUMAN);

            // 3. Animate gravity if anything fell. FULL-BOARD also refills: a booster that empties a WHOLE column (the Axe) has
            //    no falls, only refills, so moves==0 but new letters still need drawing (2026-10-29: Spencer "the board didnt
            //    repopulate after using the axe" - data refilled, visuals stayed empty). ApplyGravityFromEvents spawns refills
            //    in full-board even with no moves.
            bool refillOnly = (moves == null || moves.Count == 0) && SurvivalManager.FullBoardActive && clearedCells.Exists(c => rules.GetCell(c.x, c.y) != null);
            if ((moves != null && moves.Count > 0) || refillOnly)
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
            if (((moves != null && moves.Count > 0) || refillOnly) && HandManager.Instance != null)
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
            // 11-17: a word the cascade formed pops on its own right here (InstantPop) - its tiles are already cleared in the data
            // and not yet off the screen, which this audit reported as "FINAL desync" in every monkey run (a false alarm). Wait for
            // that pop to finish first; the cascade lock stays on meanwhile.
            { float pw = 0f; while (HandManager.Instance != null && HandManager.Instance.IsTapDetonating && pw < 6f) { pw += Time.unscaledDeltaTime; yield return null; } }
            int finalDesyncs = AuditBoardVisualSync(rules, grid);
            if (finalDesyncs > 0)
                Debug.LogError($"[BoosterDbg] Post-cascade FINAL desync count: {finalDesyncs} cell(s) " +
                               "(visual layer drifted from data after SyncToRulesState)");

            yield return FireQueuedSpecials();

            // 11-07 Spencer: "when you use a booster and it achieves the last objective it doesnt pass you on the level and waits
            // for you to make another word first". Boosters are turn-neutral (no NotifyDropCommitted), and the move is what ran
            // the clear check. Same fix as the duck collector (SurvivalManager runs CheckStageClear after this cascade).
            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null && !SurvivalManager.AwaitingNextLevel)
                SurvivalManager.Instance.CheckStageClear();
        }

        // ── Specials a booster hit (11-07) ──────────────────────────────────────
        // Spencer: "if the hammer hits a dynamite or any booster i think it should have the effect as if it was triggered" /
        // "same with all boosters really". A booster no longer smashes a special: Booster.TakeSpecials leaves it on the board and
        // queues its TILE here (it may fall in the cascade), and once the cascade settles each one still standing fires through
        // the double-tap path (HandManager.FireSpecialAt) - no move charged. One that a cascade blast already set off is skipped.
        private static readonly List<Tile> _specialsToFire = new List<Tile>();
        public static void QueueSpecialFire(Tile t) { if (t != null && !_specialsToFire.Contains(t)) _specialsToFire.Add(t); }
        private IEnumerator FireQueuedSpecials()
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance; var hand = HandManager.Instance;
            while (_specialsToFire.Count > 0)
            {
                var t = _specialsToFire[0]; _specialsToFire.RemoveAt(0);
                if (t == null || rules == null || grid == null || hand == null || SurvivalManager.AwaitingNextLevel) continue;
                float wait = 0f;
                while ((hand.IsTapDetonating || (MatchController.Instance != null && MatchController.Instance.IsProcessing)) && wait < 15f)
                { wait += Time.unscaledDeltaTime; yield return null; }
                if (t == null) continue;
                Vector2Int? at = null;
                for (int c = 0; c < RulesEngine.COLS && at == null; c++)
                    for (int r = 0; r < RulesEngine.ROWS; r++)
                        if (grid.GetTile(c, r) == t) { at = new Vector2Int(c, r); break; }
                if (at == null) continue;
                var d = rules.GetCell(at.Value.x, at.Value.y);
                if (d == null || d.SpecialKind == RulesEngine.SPECIAL_NONE) continue;
                WDLog.Log($"[Booster] hit a {RulesEngine.SpecialName(d.SpecialKind)} at ({at.Value.x},{at.Value.y}) -> fires");
                if (hand.FireSpecialAt(at.Value)) { yield return null; while (hand.IsTapDetonating) yield return null; }
            }
            _specialsToFire.Clear();
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
