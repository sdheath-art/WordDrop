using System;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Owns the level's active Objective (2026-06-08): feeds it gameplay events, pushes the
    /// HUD readout, and fires OnObjectiveComplete on the rising edge of IsComplete. The win
    /// condition that used to be "score >= target" routes through here. Auto-creates on scene
    /// load. For now a debug objective auto-installs in Survival so we can FEEL-TEST one
    /// objective atom before wiring level JSON / the LevelController win-check.
    /// </summary>
    public class ObjectiveManager : MonoBehaviour
    {
        public static ObjectiveManager Instance { get; private set; }

        public Objective Active { get; private set; }
        // A retired objective is complete-and-consumed: still shown on the HUD (so the player
        // sees 3/3 behind the stage-clear modal) but no longer the live win condition, so the
        // stage-clear loop won't instantly re-clear. Reset to a fresh objective once the modal
        // closes. 2026-06-09.
        public bool HasObjective => Active != null && !_retired;

        /// <summary>Fires once, when the active objective transitions to complete.</summary>
        public event Action<Objective> OnObjectiveComplete;

        // FEEL-TEST: auto-install one objective in Survival so it's playable without level
        // JSON. Flip false / replace with per-level objectives once the framework is proven.
        private const bool DEBUG_AUTO_OBJECTIVE = true;

        // Tutorial counting: when true, the connecting/trigger word in a detonation ALSO counts toward
        // the active objective (read in RulesEngine.DoExplode). Set per-level in InstallLevel — OFF for
        // every normal level, so existing objective balance is unchanged. 2026-06-25 Spencer.
        public static bool CountConnectingWords = false;

        private RulesEngine _subscribedTo;
        private bool _firedComplete;
        private bool _retired;          // complete + consumed, holding on HUD until modal closes

        // HeroWord (chicken) win-lock: set the instant the LAST escort is collected (cell cleared),
        // even though IsComplete only flips when its fly-up animation lands. A rising row must not fire
        // during that fly-up window once the win is locked in. 2026-06-23 Spencer.
        private bool _escortWinPending;
        public bool EscortWinPending => _escortWinPending;
        private bool _modalWasShowing;  // edge-detect the stage-clear modal closing

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
                new GameObject("ObjectiveManager").AddComponent<ObjectiveManager>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>Install the level's objective (resets progress, refreshes the HUD).</summary>
        public void SetObjective(Objective obj)
        {
            Active = obj;
            _firedComplete = false;
            _retired = false;
            _escortWinPending = false;
            obj?.Reset();
            PushHud();
            Debug.Log($"[Objective] Set: {(obj != null ? obj.Title : "none")}");
        }

        public void ClearObjective()
        {
            Active = null;
            _retired = false;
            PushHud();
        }

        /// <summary>
        /// VALIDATION MVP (2026-06-15): install the level for a 1-based level index (=
        /// SurvivalManager.CurrentStageIndex) from the hardcoded LevelTable — the simple run
        /// sequencer that stands in for the full procedural pickLevel(n) generator (deferred).
        /// Reads LevelTable.Get(n), installs its mode via SetObjective, and applies that level's
        /// difficulty dials: length/detonation seeding toggles, board-assist frequency, rise
        /// cadence, and (vault levels) the move budget. Any randomness inside the modes routes
        /// through SurvivalRng already — this only flips deterministic per-level dials.
        /// </summary>
        public void InstallLevel(int levelIndex)
        {
            var e = LevelTable.Get(levelIndex);

            // ── Difficulty dials ──
            // Seeding toggles: OpportunitySeeding (length, anti-frustration, peel LAST) +
            // DetonationSeeding (the "easy mode", peel FIRST). Static — apply before the draw runs.
            DroughtAssist.OpportunitySeeding = e.LengthSeed;
            DroughtAssist.DetonationSeeding  = e.DetonSeed;
            // Board-assist frequency (0.5 high → 0.2 low).
            PlayerHand.SetBoardAssistBase(e.AssistFreq);
            // Rise cadence (turns per rise; bigger = slower). Vault levels run rises OFF regardless.
            SurvivalManager.SetRiseCadenceOverride(e.RiseCadence);
            // Vault move budget for THIS level (only read on a move-cap/vault level).
            SurvivalManager.Instance?.SetVaultMoveBudgetOverride(e.VaultMoves);
            // Authored/tutorial levels: rising rows OFF + a custom move budget. Force rises back ON for
            // normal non-Vault levels so a prior tutorial level's "off" can't leak forward; Vault keeps
            // its own rises-off handling. 2026-06-25 Spencer.
            if (e.RisesOff)                     RisingRowManager.Enabled = false;
            else if (e.Mode != LevelMode.Vault) RisingRowManager.Enabled = true;
            SurvivalManager.Instance?.SetStageMoveBudgetOverride(e.MoveBudget);
            // Tutorial: count the connecting word toward the goal on this level (OFF for normal levels).
            CountConnectingWords = e.CountConnecting;

            // ── Starting board ──
            // Tutorial / authored levels place an EXACT hand-built board (FixedBoard) verbatim and skip
            // the random reseed. Everything else keeps the existing behavior. 2026-06-25 Spencer.
            if (e.FixedBoard != null && e.FixedBoard.Length > 0)
            {
                var rules = RulesEngine.Instance;
                if (rules != null)
                {
                    rules.ClearBoard();
                    int h = e.FixedBoard.Length;
                    for (int i = 0; i < h; i++)
                    {
                        string row = e.FixedBoard[i];
                        if (string.IsNullOrEmpty(row)) continue;
                        int boardRow = (h - 1) - i;   // FixedBoard[0] = TOP row; last row → bottom (row 0)
                        for (int col = 0; col < row.Length && col < RulesEngine.COLS; col++)
                        {
                            char ch = row[col];
                            if (ch == '.' || ch == '_' || ch == ' ') continue;
                            rules.SetCell(col, boardRow, new RulesCellData
                            {
                                Letter = char.ToUpper(ch), Col = col, Row = boardRow, PlayerIndex = -1
                            });
                        }
                    }
                    MatchController.Instance?.ResetTurnCounter();
                    GridManager.Instance?.RebuildFromRulesEngine(rules);
                }
            }
            // ── Fresh, fuller starting board per level ──
            // Vault + Ice levels self-seed a full board (SeedVaultBoard in their objective Tick). The
            // other modes (LongWord / HeroWord) would otherwise inherit the PREVIOUS level's DEPLETED
            // board → a barren start with almost no word possibilities (Spencer saw an L3 LongWord with
            // ~3 tiles). Reseed a fuller board here for those modes. 2026-06-15 Spencer.
            else if (e.Mode != LevelMode.Vault && e.Mode != LevelMode.Ice)
            {
                var rules = RulesEngine.Instance;
                if (rules != null)
                {
                    // Per-mode STARTING DENSITY (Spencer 2026-06-15): Ice + Vault self-seed a NEARLY-FULL
                    // board (they need letters packed around the ice/chests). LongWord/HeroWord want a
                    // LESS-packed board — LongWord room to maneuver, HeroWord drop HEADROOM so the escort
                    // tiles can actually fall to the bottom. Starting values; tune by feel.
                    int fillRows; float density;
                    switch (e.Mode)
                    {
                        case LevelMode.HeroWord: fillRows = 6; density = 0.80f; break; // 2026-06-15 Spencer: FULLER board so the escort takes longer to reach the bottom
                        default:                 fillRows = 5; density = 0.65f; break; // LongWord: room to maneuver
                    }
                    rules.SeedVaultBoard(fillRows, density, 0, 0, 0, 0, 0, 0, 0); // 0 vaults = fill only
                    MatchController.Instance?.ResetTurnCounter();                 // ClearBoard zeroed GlobalTurn; reset the cached _currentTurn too (the trap)
                    GridManager.Instance?.RebuildFromRulesEngine(rules);          // full rebuild wipes stale per-tile visual state
                }
            }

            // ── Install the mode ──
            SetObjective(LevelTable.MakeObjective(in e));

            // ── Per-level music ── treasure-chest reward levels get their signature track (Glitter Blast);
            // every other mode uses the normal gameplay rotation. Both calls are idempotent, so this is a
            // safe no-op when the right music is already playing. 2026-06-17 Spencer.
            if (e.Mode == LevelMode.Vault)
                GameAudio.Instance?.PlayChestMusic();
            else
                GameAudio.Instance?.PlaySurvivalMusic();

            Debug.Log($"[LevelTable] Install L{levelIndex} → {e.Mode} ({e.Profile}) " +
                      $"len-seed:{e.LengthSeed} deton-seed:{e.DetonSeed} assist:{e.AssistFreq:0.00} " +
                      $"rise:{e.RiseCadence} | {e.Why}");

            // Pre-level "here's your goal" modal — pauses gameplay until the player taps PLAY so a
            // fresh tester always knows the objective before the board starts moving. 2026-06-15 Spencer.
            LevelIntroModal.Instance?.Show(Active, levelIndex);
        }

        /// <summary>Stage cleared via this objective: keep showing it (3/3) but stop it being the
        /// live win condition so the clear loop won't re-fire. It resets to a fresh objective when
        /// the stage-clear modal closes (see Update). 2026-06-09.</summary>
        public void RetireForStageClear()
        {
            if (Active != null) _retired = true;
        }

        private void Update()
        {
            // RulesEngine.Instance may not exist at Awake — (re)subscribe to OnWordScored
            // whenever it changes (mirrors how other managers verify their subscription).
            var re = RulesEngine.Instance;
            if (re != _subscribedTo)
            {
                if (_subscribedTo != null)
                {
                    _subscribedTo.OnWordScored        -= HandleWordScored;
                    _subscribedTo.OnTilesExploded     -= HandleTilesExploded;
                    _subscribedTo.OnResolutionComplete -= HandleResolutionComplete;
                }
                if (re != null)
                {
                    re.OnWordScored        += HandleWordScored;        // prime-time (make a word)
                    re.OnTilesExploded     += HandleTilesExploded;     // explode-time (tiles blow up)
                    re.OnResolutionComplete += HandleResolutionComplete; // collect drop-targets at the bottom
                }
                _subscribedTo = re;
            }

            // Hold a completed objective on the HUD through the stage-clear modal, then reset it
            // for the next stage once the modal closes (edge: was showing → now hidden).
            bool modalShowing = StageClearModal.Instance != null && StageClearModal.Instance.IsShowing;
            if (_retired && _modalWasShowing && !modalShowing)
                ClearObjective(); // → auto-install fires below for the fresh stage
            _modalWasShowing = modalShowing;

            // ── LEVEL-SEQUENCER INSTALL (validation MVP, 2026-06-15) ──
            // Once we're in a live Survival run with no objective set, install the level keyed off
            // the current stage index. The survival STAGE system already advances stage→stage; on
            // each new stage Active goes null (RetireForStageClear → ClearObjective on modal close),
            // so this fires once per level. LevelTable.Get(n) returns the mode + difficulty dials;
            // we install the mode AND apply that level's dials (seeding toggles, assist freq, rise
            // cadence, vault move budget). Replaces the old single hardcoded SetObjective(new IceObjective(4)).
            if (DEBUG_AUTO_OBJECTIVE && Active == null
                && SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                && !SurvivalManager.Instance.IsGameOver)
            {
                InstallLevel(SurvivalManager.Instance.CurrentStageIndex);
            }

            // Per-frame hook: HeroWord spawns its drop-targets, BreakRocks spawns + polls its rocks.
            // POLL-BASED objectives (BreakRocks) update their progress and reach IsComplete INSIDE
            // Tick — there's no event — so we must wrap Tick to refresh the HUD and fire the win on
            // the rising edge, or the objective would silently complete but never win. The
            // _firedComplete guard inside FireCompleteIfJust makes this safe for event-driven
            // objectives too (no double-fire). 2026-06-09.
            if (Active != null)
            {
                bool   wasComplete  = Active.IsComplete;
                string prevProgress = Active.ProgressText;
                Active.Tick();
                PushHud();
                // Poll-based progress changed this Tick (a vault cracked) → pop the counter. The
                // crack itself already plays the stone-splash FX; this is the HUD reaction.
                if (!wasComplete && Active.ProgressText != prevProgress)
                    HUDManager.Instance?.PulseObjective();
                FireCompleteIfJust(wasComplete);
            }

            // Backup poll: catch any drop-target sitting at row 0 before a rising row shoves it
            // back up. The timing-safe collect is on OnResolutionComplete; this mops up stragglers.
            CollectBottomDropTargets();

            // Self-heal: keep escort objects amber even if a resolution path momentarily re-stoned one
            // grey (Spencer caught a dark escort). Cheap sweep; no-op unless drop-targets exist. 2026-06-15.
            if (Active != null)
                GridManager.Instance?.EnsureDropTargetVisuals(RulesEngine.Instance);
        }

        private void HandleResolutionComplete(ResolutionCompleteEvent evt) => CollectBottomDropTargets();

        /// <summary>Collect any drop-target that reached the bottom row and credit the active
        /// objective (hero-word / drop-to-bottom). Idempotent — clears the cell so it can't
        /// double-count. Called on OnResolutionComplete AND polled each frame. 2026-06-09.</summary>
        private void CollectBottomDropTargets()
        {
            if (Active == null) return;
            var rules = RulesEngine.Instance;
            if (rules == null) return;
            var grid = GridManager.Instance;

            List<Vector2Int> collected = null;
            for (int c = 0; c < RulesEngine.COLS; c++)
            {
                var cell = rules.GetCell(c, 0);
                if (cell == null || !cell.IsDropTarget) continue;

                // Wait until the tile has visually LANDED (not mid-fall) so the player actually
                // sees it hit the bottom before it's collected.
                var tile = grid != null ? grid.GetTile(c, 0) : null;
                if (tile != null && tile.IsAnimating) continue;

                // Celebrate at the landing spot.
                if (grid != null)
                    GameParticles.Instance?.PlayShimmerBurst(grid.CellToWorld(c, 0), 12);

                rules.ClearCell(c, 0);
                (collected ??= new List<Vector2Int>()).Add(new Vector2Int(c, 0));
            }
            if (collected == null) return;

            grid?.RemoveTiles(collected);   // visuals for just these cells
            GameAudio.Instance?.PlayChickenCluck(); // rubber chicken hit the bottom row — first 1.5s cluck
            HapticsManager.Strong();         // solid impact buzz to match the chicken landing. 2026-06-24 Spencer

            // Each collected escort FLIES UP to the Target icon (same animation as the HiddenWord letters).
            // The decrement + completion check happen when it LANDS, so the counter ticks as each one
            // arrives. Staggered so multiples pop together then land one-by-one. 2026-06-17 Spencer.
            var obj = Active;
            for (int i = 0; i < collected.Count; i++)
            {
                Vector3 startWorld = grid != null ? grid.CellToWorld(collected[i].x, collected[i].y) : Vector3.zero;
                HUDManager.Instance?.FlyEscortToTarget(startWorld, () =>
                {
                    if (obj == null) return;
                    bool wasComplete = obj.IsComplete;
                    obj.OnDropTargetCollected(1);
                    PushHud();
                    HUDManager.Instance?.PulseObjective();   // counter reacts to each collect
                    FireCompleteIfJust(wasComplete);
                }, i * 0.25f);
            }

            // Win-lock: HeroWord spawns ALL escorts upfront, so once none remain on the board, every
            // chicken has been collected and the level WILL be won when the fly-ups land. Flag it now so
            // a rising row can't sneak in during the fly-up window (IsComplete hasn't flipped yet). 2026-06-23.
            if (!rules.HasAnyDropTarget()) _escortWinPending = true;
        }

        /// <summary>Notify the active objective of a word scored via a path that does NOT
        /// fire RulesEngine.OnWordScored — specifically the board-swap scoring path in
        /// HandManager (the "dual scoring paths" tech debt). Keeps objectives accurate no
        /// matter how the player formed the word. 2026-06-08.</summary>
        public void NotifyWordScored(string word, int playerIndex, int chainStep = 0)
        {
            if (string.IsNullOrEmpty(word)) return;
            HandleWordScored(new WordScoredEvent { Word = word, PlayerIndex = playerIndex, ChainStep = chainStep });
        }

        private void HandleWordScored(WordScoredEvent evt)
        {
            if (Active == null) return;
            bool wasComplete = Active.IsComplete;
            Active.OnWordScored(evt);
            PushHud();

            FireCompleteIfJust(wasComplete);
        }

        /// <summary>Notify the active objective that a primed word's tiles EXPLODED, called
        /// directly from the live resolution path (RulesEngine.DoExplode) — which removes tiles
        /// via StepResult and does NOT fire OnTilesExploded. This is the path that actually runs;
        /// the OnTilesExploded event only fires from the dead ProcessDrop path. 2026-06-09.</summary>
        public void NotifyWordExploded(string word, int ownerPlayerIndex)
        {
            if (Active == null || string.IsNullOrEmpty(word)) return;
            bool wasComplete = Active.IsComplete;
            Active.OnWordExploded(word, ownerPlayerIndex);
            PushHud();
            FireCompleteIfJust(wasComplete);
        }

        /// <summary>Tiles EXPLODED — feed every destroyed primed word to detonation-based
        /// objectives ("explode N words"). Covers trigger, triggered, connected-group, and
        /// splash-sweep words alike — the complete "what blew up" list.</summary>
        private void HandleTilesExploded(TilesExplodedEvent evt)
        {
            if (Active == null || evt?.ExplodedWords == null) return;
            bool wasComplete = Active.IsComplete;
            for (int i = 0; i < evt.ExplodedWords.Count; i++)
            {
                var w = evt.ExplodedWords[i];
                Active.OnWordExploded(w.Word, w.OwnerPlayerIndex);
            }
            PushHud();
            FireCompleteIfJust(wasComplete);
        }

        private void FireCompleteIfJust(bool wasComplete)
        {
            if (Active != null && !wasComplete && Active.IsComplete && !_firedComplete)
            {
                _firedComplete = true;
                Debug.Log($"[Objective] COMPLETE: {Active.Title}");
                OnObjectiveComplete?.Invoke(Active);
                GameAudio.Instance?.PlayBing();                 // objective-complete "bing"
                HUDManager.Instance?.FlashObjectiveComplete();
                // Drive the stage clear the instant the objective completes. POLL-BASED objectives
                // (BreakRocks) finish inside Tick with no score delta, so the normal CheckStageClear
                // callers (per-drop / per-score) would otherwise miss it until the next drop.
                // CheckStageClear is idempotent + guarded, so this is safe for every objective type.
                SurvivalManager.Instance?.CheckStageClear();
            }
        }

        private void PushHud()
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetObjective(Active);
        }

        /// <summary>Called by a HiddenWord fly-up when its letter LANDS in the Target panel: the slot has
        /// just been revealed, so refresh the HUD (rock→letter) and fire stage-clear if the word's now
        /// complete. `wasComplete` is the objective's IsComplete state captured BEFORE this reveal.
        /// 2026-06-17 Spencer.</summary>
        public void NotifyHiddenReveal(bool wasComplete)
        {
            PushHud();
            FireCompleteIfJust(wasComplete);
        }

        private void OnDestroy()
        {
            if (_subscribedTo != null)
            {
                _subscribedTo.OnWordScored        -= HandleWordScored;
                _subscribedTo.OnTilesExploded     -= HandleTilesExploded;
                _subscribedTo.OnResolutionComplete -= HandleResolutionComplete;
            }
        }
    }
}
