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

        private RulesEngine _subscribedTo;
        private bool _firedComplete;
        private bool _retired;          // complete + consumed, holding on HUD until modal closes
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

            // Feel-test auto-install: once we're in a live Survival run with no objective set,
            // drop in a long-word goal so it shows on the HUD and tracks.
            if (DEBUG_AUTO_OBJECTIVE && Active == null
                && SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                && !SurvivalManager.Instance.IsGameOver)
            {
                // Feel-test objective. Swap the line below to try each type:
                //   new LongWordObjective(minLen, goal)  — explode `goal` words of `minLen`+ letters
                //   new ChainObjective(k)                — land a k-chain cascade
                //   new HeroWordObjective(n)             — escort n drop-targets to the bottom (FLAGSHIP)
                SetObjective(new HeroWordObjective(3));
            }

            // Per-frame hook (e.g. HeroWordObjective spawns its drop-targets once the board's ready).
            Active?.Tick();

            // Backup poll: catch any drop-target sitting at row 0 before a rising row shoves it
            // back up. The timing-safe collect is on OnResolutionComplete; this mops up stragglers.
            CollectBottomDropTargets();
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
            GameAudio.Instance?.PlayScorePowerup();
            bool wasComplete = Active.IsComplete;
            Active.OnDropTargetCollected(collected.Count);
            PushHud();
            HUDManager.Instance?.PulseObjective();   // counter reacts to each collect
            FireCompleteIfJust(wasComplete);
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
            // [ObjectiveTrace] TEMP — confirm what OnWordScored delivers vs the objective filter.
            Debug.Log($"[ObjectiveTrace] OnWordScored '{evt?.Word}' len={(evt?.Word != null ? evt.Word.Length : -1)} " +
                      $"player={evt?.PlayerIndex} (need player={MatchController.PLAYER_HUMAN}, len>=5) active={(Active != null ? Active.Title : "none")}");
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
            // [ObjectiveTrace] TEMP — confirm exploded words reach the objective.
            Debug.Log($"[ObjectiveTrace] NotifyWordExploded '{word}' len={word.Length} " +
                      $"owner={ownerPlayerIndex} (need owner={MatchController.PLAYER_HUMAN}) active={Active.Title}");
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
                // [ObjectiveTrace] TEMP — confirm every exploded word reaches the objective.
                Debug.Log($"[ObjectiveTrace] OnWordExploded '{w.Word}' len={(w.Word != null ? w.Word.Length : -1)} " +
                          $"owner={w.OwnerPlayerIndex} (need owner={MatchController.PLAYER_HUMAN}) active={Active.Title}");
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
            }
        }

        private void PushHud()
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetObjective(Active);
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
