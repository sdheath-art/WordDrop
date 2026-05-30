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
                booster.Activate(() =>
                {
                    if (booster.TriggersGravity) RunGravity();
                });
            }
        }

        /// <summary>Player tapped a tile in aim mode. Consumes a charge from the
        /// armed booster and resolves it with the target cell.</summary>
        public void ResolveAim(int col, int row)
        {
            if (!AimMode || ArmedBooster == null) return;
            string id = ArmedBooster.Id;
            if (GetCharges(id) <= 0) { AimMode = false; ArmedBooster = null; OnStateChanged?.Invoke(); return; }

            var booster = ArmedBooster;
            _charges[id] = GetCharges(id) - 1;
            AimMode = false;
            ArmedBooster = null;
            Debug.Log($"[Booster] {booster.DisplayName} resolved at ({col},{row}), charges→{_charges[id]}");
            OnStateChanged?.Invoke();
            booster.ResolveWithTarget(col, row, () =>
            {
                if (booster.TriggersGravity) RunGravity();
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

        // ── Gravity helper ──────────────────────────────────────────────────────

        /// <summary>Run gravity after a destructive booster so floating tiles
        /// fall into the gaps it created.
        ///
        /// Canonical pattern (matches the scoring path):
        ///   1. RulesEngine.ApplyGravityInDataPublic() — compacts the data layer
        ///      _board[] AND returns a moves dict mapping old → new positions
        ///   2. GridManager.ApplyGravityFromEvents(moves) — animates the visual
        ///      tile GameObjects using the moves dict
        ///
        /// Calling only GridManager.ApplyGravity (the older standalone version)
        /// would leave RulesEngine._board out of sync and break the drop preview.</summary>
        private void RunGravity()
        {
            if (RulesEngine.Instance == null || GridManager.Instance == null) return;
            var moves = RulesEngine.Instance.ApplyGravityInDataPublic();
            if (moves != null && moves.Count > 0)
                StartCoroutine(GridManager.Instance.ApplyGravityFromEvents(moves));
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
