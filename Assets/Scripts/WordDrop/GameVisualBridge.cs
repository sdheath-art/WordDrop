using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// SOLE WORD-PRESENTATION PATH — STEP-BY-STEP VISUAL DRIVER
    /// ──────────────────────────────────────────────────────────
    /// Drives visual playback for AI drops via ExecuteAITurnCoroutine.
    /// Player drops are driven directly by HandManager.FullTurnSequence
    /// using RulesEngine.BeginDrop/NextStep/FinalizeDrop (bypasses this class).
    ///
    /// Also owns: ForceReset, IsPlayingBack, MatchController event handlers,
    /// ScalePop, ApplyAllPrimedGlows.
    /// </summary>
    public class GameVisualBridge : MonoBehaviour
    {
        public static GameVisualBridge Instance { get; private set; }

        // -- Timing constants (tunable) -----------------------------------------------

        // ── Visual pacing — everything 0.2-0.3s, snappy and punchy ────────────
        private const float TILE_DROP_PAUSE        = 0.0f;
        private const float WORD_LOCKIN_BEAT       = 0.25f;
        private const float CHAIN_LOCKIN_BEAT      = 0.20f;
        private const float CHAIN_FAST_BEAT        = 0.15f;
        private const float PRIMED_GLOW_PAUSE      = 0.08f;
        private const float TRIGGER_FLASH_DURATION = 0.20f;
        private const float POST_RESOLUTION_PAUSE  = 0.20f;
        private const float EXPLOSION_DURATION     = 0.15f;
        private const float POST_EXPLOSION_PAUSE   = 0.08f;
        private const float GRAVITY_SETTLE_PAUSE   = 0.10f;
        private const float TILE_FALL_SPEED        = 38f; // was 45

        // Safety timeout constant (shared between all wait loops)
        private const int SAFETY_FRAME_LIMIT = 600; // ~10 seconds at 60fps

        // -- Step-by-step resolution state -------------------------------------------

        private bool _isPlayingBack = false;

        // -- Player colors ------------------------------------------------------------

        private static readonly Color PLAYER_COLOR = new Color(0.20f, 0.82f, 0.38f, 1f);
        private static readonly Color AI_COLOR     = new Color(1.00f, 0.55f, 0.15f, 1f);

        // -- Unity lifecycle ----------------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Debug.Log("[GameVisualBridge] Awake");
        }

        private void Start()
        {
            SubscribeToRulesEngineEvents();
            SubscribeToMatchControllerEvents();
            Debug.Log("[GameVisualBridge] Subscribed to all events");
        }

        private void OnDestroy()
        {
            UnsubscribeFromRulesEngineEvents();
            UnsubscribeFromMatchControllerEvents();
        }

        // -- Debug state dump --------------------------------------------------------

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.T))
            {
                Debug.Log($"[GameVisualBridge] STATE DUMP: " +
                          $"_isPlayingBack={_isPlayingBack} | " +
                          $"CurrentPlayer={MatchController.Instance?.CurrentPlayer} | " +
                          $"TotalTurnsUsed={MatchController.Instance?.TotalTurnsUsed} | " +
                          $"IsMatchActive={MatchController.Instance?.IsMatchActive}");
            }
        }

        // =============================================================================
        // EVENT SUBSCRIPTIONS
        // =============================================================================

        private void SubscribeToRulesEngineEvents()
        {
            // Step-by-step API: no RulesEngine event subscriptions needed.
        }

        private void UnsubscribeFromRulesEngineEvents()
        {
            // No-op
        }

        private void SubscribeToMatchControllerEvents()
        {
            if (MatchController.Instance == null)
            {
                Debug.LogWarning("[GameVisualBridge] MatchController not found -- cannot subscribe.");
                return;
            }

            MatchController.Instance.OnTurnEnd      += HandleTurnEnd;
            MatchController.Instance.OnHandRefilled += HandleHandRefilled;
            MatchController.Instance.OnMatchEnd     += HandleMatchEnd;
            MatchController.Instance.OnSwapUsed     += HandleSwapUsed;
        }

        private void UnsubscribeFromMatchControllerEvents()
        {
            if (MatchController.Instance == null) return;

            MatchController.Instance.OnTurnEnd      -= HandleTurnEnd;
            MatchController.Instance.OnHandRefilled -= HandleHandRefilled;
            MatchController.Instance.OnMatchEnd     -= HandleMatchEnd;
            MatchController.Instance.OnSwapUsed     -= HandleSwapUsed;
        }

        // =============================================================================
        // MatchController EVENT HANDLERS
        // =============================================================================

        private void HandleTurnEnd(TurnEndEvent evt)
        {
            Debug.Log($"[GameVisualBridge] TurnEnd: player={evt.PlayerIndex} " +
                      $"turn={evt.PlayerTurnNumber} global={evt.GlobalTurnIndex}");

            if (HUDManager.Instance != null && MatchController.Instance != null)
            {
                int totalRemaining = MatchController.Instance.TotalMaxTurns
                                   - MatchController.Instance.TotalTurnsUsed;
                HUDManager.Instance.SetTurnsRemaining(
                    totalRemaining, MatchController.Instance.TotalMaxTurns);

                // Always show human player's swap count (AI swaps aren't player-visible)
                int swaps = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
                HUDManager.Instance.ShowSwapCount(swaps);
            }
        }

        private void HandleHandRefilled(HandRefilledEvent evt)
        {
            Debug.Log($"[GameVisualBridge] HandRefilled: player={evt.PlayerIndex} " +
                      $"letters={new string(evt.Letters)}");
        }

        private void HandleMatchEnd(MatchEndEvent evt)
        {
            Debug.Log($"[GameVisualBridge] MatchEnd: winner={evt.WinnerPlayerIndex} " +
                      $"P={evt.PlayerScore} AI={evt.AIScore}");
        }

        private void HandleSwapUsed(SwapUsedEvent evt)
        {
            Debug.Log($"[GameVisualBridge] SwapUsed: player={evt.PlayerIndex} " +
                      $"slot={evt.HandSlot} '{evt.OldLetter}'->'{evt.NewLetter}' " +
                      $"swapsRemaining={evt.SwapsRemaining}");

            if (HUDManager.Instance != null)
                HUDManager.Instance.ShowSwapCount(evt.SwapsRemaining);
        }

        // =============================================================================
        // PUBLIC API
        // =============================================================================

        /// <summary>True if visual playback is currently in progress.</summary>
        public bool IsPlayingBack => _isPlayingBack;

        /// <summary>
        /// Force-resets the playback flag. Used as a safety escape hatch if
        /// the resolution gets stuck and the safety timeout fires.
        /// </summary>
        public void ForceReset()
        {
            if (_isPlayingBack)
            {
                Debug.LogWarning("[GameVisualBridge] ForceReset() called — forcing _isPlayingBack = false. " +
                                 "This indicates a resolution coroutine got stuck.");
                _isPlayingBack = false;

                // Note: we intentionally do NOT call FinalizeDrop() here.
                // ForceReset is an escape hatch for stuck playback — it should only
                // clear the visual lock. FinalizeDrop advances game state (increments
                // _globalTurn, expires primed words) which is wrong if resolution
                // never actually completed. The safe wrapper handles finalization.
            }
            else
            {
                Debug.Log("[GameVisualBridge] ForceReset() called but _isPlayingBack was already false — no-op.");
            }
        }

        // =============================================================================
        // SAFE WRAPPER — ensures _isPlayingBack and FinalizeDrop are always cleaned up
        // =============================================================================

        private IEnumerator RunStepByStepResolutionSafe(
            int col, char letter, int playerIndex,
            System.Action<int> onComplete,
            System.Action onFinished)
        {
            bool caughtException = false;
            System.Exception capturedException = null;
            int totalScore = 0;

            yield return StartCoroutine(RunStepByStepResolutionInner(
                col, letter, playerIndex,
                (score) => { totalScore = score; },
                (ex) => { caughtException = true; capturedException = ex; }));

            try
            {
                if (caughtException && capturedException != null)
                {
                    Debug.LogError($"[GameVisualBridge] Resolution exception caught in safe wrapper: {capturedException}");
                }

                // Safety net: FinalizeDrop is called here in case the Complete phase
                // didn't run (e.g. exception). FinalizeDrop is now idempotent —
                // if already Idle, it's a no-op.
                if (RulesEngine.Instance != null)
                {
                    try { RulesEngine.Instance.FinalizeDrop(); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[GameVisualBridge] FinalizeDrop in safe wrapper threw: {ex.Message}");
                    }
                }

                Debug.Log($"[GameVisualBridge] RunStepByStepResolutionSafe: invoking onComplete(score={totalScore})");
                onComplete?.Invoke(totalScore);
                onFinished?.Invoke();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameVisualBridge] RunStepByStepResolutionSafe cleanup error: {ex}");
                onComplete?.Invoke(0);
                onFinished?.Invoke();
            }
        }

        /// <summary>
        /// Inner resolution coroutine with full phase-by-phase logging (Job 2)
        /// and null-safe phase handlers (Job 5).
        /// </summary>
        private IEnumerator RunStepByStepResolutionInner(
            int col, char letter, int playerIndex,
            System.Action<int> onComplete,
            System.Action<System.Exception> onException)
        {
            RulesEngine rules = RulesEngine.Instance;
            GridManager grid  = GridManager.Instance;

            if (rules == null || grid == null)
            {
                Debug.LogError("[GameVisualBridge] RunStepByStepResolutionInner: missing RulesEngine or GridManager. Aborting.");
                onComplete?.Invoke(0);
                yield break;
            }

            bool isPlayer = (playerIndex == MatchController.PLAYER_HUMAN);
            int wordIndex = 0;

            if (ScoringDisplay.Instance != null)
                ScoringDisplay.Instance.ResetChain();

            // ── Step 1: BeginDrop ────────────────────────────────────────────────────

            Debug.Log($"[GameVisualBridge] >>> BeginDrop: calling rules.BeginDrop(col={col}, letter='{letter}', player={playerIndex})");

            RulesEngine.StepResult beginResult = null;
            try
            {
                beginResult = rules.BeginDrop(col, letter, playerIndex);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameVisualBridge] BeginDrop threw exception: {ex}");
                onException?.Invoke(ex);
                onComplete?.Invoke(0);
                yield break;
            }

            if (beginResult == null)
            {
                Debug.LogError($"[GameVisualBridge] BeginDrop returned NULL for col={col} letter='{letter}' — column may be full or RulesEngine refused the drop. Aborting resolution.");
                onComplete?.Invoke(0);
                yield break;
            }

            int targetRow = beginResult.Row;
            Debug.Log($"[GameVisualBridge] <<< BeginDrop returned: Phase={beginResult.Phase} Row={targetRow}");

            // Animate tile falling
            Tile droppedTile = null;
            try
            {
                droppedTile = grid.CreateSingleTile(col, targetRow, letter);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameVisualBridge] CreateSingleTile threw: {ex}");
                onException?.Invoke(ex);
                onComplete?.Invoke(0);
                yield break;
            }

            if (droppedTile != null)
            {
                Vector3 targetPos = droppedTile.transform.position;
                float spawnY = grid.GridTop + grid.CellSize * 1.5f;
                droppedTile.transform.position = new Vector3(targetPos.x, spawnY, targetPos.z);

                // Enable fake 3D tilt for the drop
                float tiltX = Random.Range(8f, 15f);
                float tiltY = Random.Range(-12f, 12f);
                droppedTile.SetFake3D(tiltX, tiltY);

                float distance = spawnY - targetPos.y;
                float duration = distance / TILE_FALL_SPEED;
                float elapsed = 0f;

                while (elapsed < duration)
                {
                    if (droppedTile == null) break;
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / duration);
                    float eased = t * t;
                    droppedTile.transform.position = new Vector3(
                        targetPos.x,
                        Mathf.Lerp(spawnY, targetPos.y, eased),
                        targetPos.z);

                    // Ease tilt toward flat as it lands
                    float fade = 1f - eased;
                    droppedTile.SetFake3D(tiltX * fade, tiltY * fade);
                    droppedTile.UpdateFake3DPosition();

                    yield return null;
                }

                if (droppedTile != null)
                {
                    droppedTile.ClearFake3D();
                    droppedTile.transform.position = targetPos;
                    droppedTile.PlayLandingSquish();
                }
            }
            else
            {
                Debug.LogWarning($"[GameVisualBridge] CreateSingleTile returned null for col={col} row={targetRow} — no visual tile created, but continuing resolution.");
            }

            // TILE_DROP_PAUSE is 0 — no delay needed after tile creation

            // ── Step 2: Loop NextStep until Complete ─────────────────────────────────

            Debug.Log($"[GameVisualBridge] Starting NextStep loop for col={col} letter='{letter}' player={playerIndex}");

            bool resolving = true;
            int safetyStepCount = 0;
            bool loopExitedCleanly = false;
            const int MAX_STEPS = 200;

            while (resolving && safetyStepCount < MAX_STEPS)
            {
                safetyStepCount++;

                // ── Fetch next step ──────────────────────────────────────────────────
                RulesEngine.StepResult step = null;
                try
                {
                    step = rules.NextStep();
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[GameVisualBridge] NextStep threw exception at iteration {safetyStepCount}: {ex}");
                    onException?.Invoke(ex);
                    resolving = false;
                    break;
                }

                // ── Null check for NextStep result ──────────────────────────────────
                if (step == null)
                {
                    Debug.LogError($"[GameVisualBridge] NextStep returned NULL unexpectedly at iteration {safetyStepCount}. " +
                                   $"RulesEngine.CurrentPhase={rules.CurrentPhase}. " +
                                   $"Treating as Complete to avoid hang.");
                    onComplete?.Invoke(0);
                    resolving = false;
                    break;
                }

                Debug.Log($"[GameVisualBridge] NextStep iteration={safetyStepCount} Phase={step.Phase}");

                switch (step.Phase)
                {
                    // ── TileDropped ──────────────────────────────────────────────────
                    case RulesEngine.ResolutionPhase.TileDropped:
                    {
                        Debug.Log($"[GameVisualBridge] Phase=TileDropped (row={step.Row}) — tile placed in rules engine data.");
                        // No null-safety needed here — no complex data to check
                        break;
                    }

                    // ── WordsDetected ────────────────────────────────────────────────
                    // Job 5: If step.NewWords is null, treat as empty list and continue.
                    case RulesEngine.ResolutionPhase.WordsDetected:
                    {
                        // Null-safe: treat null NewWords as empty list
                        int wordCount = (step.NewWords != null) ? step.NewWords.Count : 0;
                        Debug.Log($"[GameVisualBridge] Phase=WordsDetected: {wordCount} new word(s) found. " +
                                  $"Words: [{(step.NewWords != null ? string.Join(", ", step.NewWords.ConvertAll(w => w.Word)) : "none")}]");
                        // No additional action needed — visual bridge just waits for the
                        // next step (WordsScored or Complete) to handle the words.
                        break;
                    }

                    // ── WordsScored ──────────────────────────────────────────────────
                    // Job 5: If step.ScoredWords is null, skip the inner loop entirely.
                    case RulesEngine.ResolutionPhase.WordsScored:
                    {
                        // Null-safe: if ScoredWords is null, treat as empty
                        int scoredCount = (step.ScoredWords != null) ? step.ScoredWords.Count : 0;
                        Debug.Log($"[GameVisualBridge] Phase=WordsScored: {scoredCount} word(s) to animate.");

                        if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                        {
                            for (int w = 0; w < step.ScoredWords.Count; w++)
                            {
                                var sw = step.ScoredWords[w];

                                Debug.Log($"[GameVisualBridge]   Scored word [{w}]: '{sw.Word}' " +
                                          $"baseScore={sw.BaseScore} finalScore={sw.FinalScore} " +
                                          $"chainStep={sw.ChainStep} player={sw.PlayerIndex}");

                                // Collect tiles for FX
                                List<Tile> scoredTilesForFX = new List<Tile>();
                                if (sw.Cells != null)
                                {
                                    for (int c = 0; c < sw.Cells.Count; c++)
                                    {
                                        Tile tile = null;
                                        try { tile = grid.GetTile(sw.Cells[c].x, sw.Cells[c].y); }
                                        catch { /* ignore */ }

                                        if (tile != null)
                                            scoredTilesForFX.Add(tile);
                                        else
                                            Debug.LogWarning($"[GameVisualBridge]   No tile found at ({sw.Cells[c].x},{sw.Cells[c].y}) for word '{sw.Word}'");
                                    }
                                }

                                // Procedural staggered highlight + scale pop
                                Color hlColor = isPlayer ? PLAYER_COLOR : AI_COLOR;
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayWordScored(scoredTilesForFX, hlColor, wordIndex);

                                // Show popup via HUD
                                // Balatro-style scoring display
                                try
                                {
                                    if (ScoringDisplay.Instance != null)
                                        ScoringDisplay.Instance.ShowWordScore(sw.Word, sw.FinalScore, isPlayer);
                                }
                                catch (System.Exception ex)
                                {
                                    Debug.LogWarning($"[GameVisualBridge] ScoringDisplay error: {ex.Message}");
                                }

                                // Beat timing — full countdown for first word, quick for chains
                                float scoringDur = (wordIndex == 0)
                                    ? ScoringDisplay.GetDuration(sw.Word.Length)
                                    : ScoringDisplay.GetQuickDuration();
                                float beat = Mathf.Max(
                                    WordDropFX.GetBeatDuration(WORD_LOCKIN_BEAT, wordIndex),
                                    scoringDur);
                                Debug.Log($"[GameVisualBridge]   Waiting {beat:F2}s beat for word '{sw.Word}'");
                                yield return new WaitForSeconds(beat);
                                wordIndex++;
                            }
                        }
                        else
                        {
                            // Job 5: null or empty ScoredWords — log and continue without waiting
                            Debug.Log("[GameVisualBridge] Phase=WordsScored: ScoredWords is null or empty — skipping word animation (no words to show).");
                        }
                        break;
                    }

                    // ── TriggersFound ────────────────────────────────────────────────
                    // Job 5: If step.Triggers is null or empty, skip trigger animation.
                    case RulesEngine.ResolutionPhase.TriggersFound:
                    {
                        // Detonation fires immediately — scoring display exit overlaps
                        // Null-safe: treat null Triggers as empty list
                        int triggerCount = (step.Triggers != null) ? step.Triggers.Count : 0;
                        Debug.Log($"[GameVisualBridge] Phase=TriggersFound: {triggerCount} trigger(s) found.");

                        if (step.Triggers != null && step.Triggers.Count > 0)
                        {
                            bool hasChainTriggers = false;

                            // Pass 1: Direct triggers
                            for (int tr = 0; tr < step.Triggers.Count; tr++)
                            {
                                var trig = step.Triggers[tr];
                                if (trig.IsChainTrigger) { hasChainTriggers = true; continue; }

                                Debug.Log($"[GameVisualBridge]   DirectTrigger [{tr}]: '{trig.TriggerWord}' triggers primed '{trig.TriggeredWord}'");

                                List<Tile> trigTiles = new List<Tile>();
                                if (trig.TriggeredCells != null)
                                {
                                    for (int c = 0; c < trig.TriggeredCells.Count; c++)
                                    {
                                        try
                                        {
                                            Tile tile = grid.GetTile(trig.TriggeredCells[c].x, trig.TriggeredCells[c].y);
                                            if (tile != null) trigTiles.Add(tile);
                                        }
                                        catch { /* ignore */ }
                                    }
                                }

                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayDetonation(trigTiles, wordIndex);
                            }

                            // If there are chain triggers, brief pause then ignite them
                            if (hasChainTriggers)
                            {
                                // Fuse Trace: draw shimmer lines from direct triggers to chain triggers
                                if (WordDropFX.Instance != null)
                                    WordDropFX.Instance.PlayFuseTrace(step.Triggers, grid);

                                yield return new WaitForSeconds(0.12f); // staged ignition gap

                                for (int tr = 0; tr < step.Triggers.Count; tr++)
                                {
                                    var trig = step.Triggers[tr];
                                    if (!trig.IsChainTrigger) continue;

                                    Debug.Log($"[GameVisualBridge]   ChainTrigger [{tr}]: chain-connected '{trig.TriggeredWord}'");

                                    List<Tile> trigTiles = new List<Tile>();
                                    if (trig.TriggeredCells != null)
                                    {
                                        for (int c = 0; c < trig.TriggeredCells.Count; c++)
                                        {
                                            try
                                            {
                                                Tile tile = grid.GetTile(trig.TriggeredCells[c].x, trig.TriggeredCells[c].y);
                                                if (tile != null) trigTiles.Add(tile);
                                            }
                                            catch { /* ignore */ }
                                        }
                                    }

                                    if (WordDropFX.Instance != null)
                                        WordDropFX.Instance.PlayDetonation(trigTiles, wordIndex);
                                }
                            }

                            float trigWait = WordDropFX.DETONATE_TOTAL_DUR;
                            Debug.Log($"[GameVisualBridge]   Waiting {trigWait:F2}s for trigger FX. " +
                                      $"(chainTriggered={step.ChainTriggeredCount})");
                            yield return new WaitForSeconds(trigWait);
                        }
                        else
                        {
                            // Job 5: null or empty triggers — skip animation entirely, no wait
                            Debug.Log("[GameVisualBridge] Phase=TriggersFound: no triggers (null or empty) — skipping flash animation.");
                        }
                        break;
                    }

                    // ── Exploding ────────────────────────────────────────────────────
                    // Job 5: If step.ExplodedCells is null or empty, skip animation
                    //        but STILL call grid.RemoveTiles (with empty list is safe).
                    case RulesEngine.ResolutionPhase.Exploding:
                    {
                        // Null-safe: treat null ExplodedCells as empty list
                        int explodeCount = (step.ExplodedCells != null) ? step.ExplodedCells.Count : 0;
                        Debug.Log($"[GameVisualBridge] Phase=Exploding: {explodeCount} cell(s) to remove.");

                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        {
                            List<Tile> dyingTiles = new List<Tile>(step.ExplodedCells.Count);
                            for (int i = 0; i < step.ExplodedCells.Count; i++)
                            {
                                try
                                {
                                    Tile tile = grid.GetTile(step.ExplodedCells[i].x, step.ExplodedCells[i].y);
                                    if (tile != null) dyingTiles.Add(tile);
                                }
                                catch { /* ignore */ }
                            }

                            // Procedural staggered explosion with escalating shake
                            if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                yield return WordDropFX.Instance.PlayExplosion(dyingTiles, wordIndex);

                            // Show detonation bonus popup
                            if (step.DetonationBonus > 0 && BonusPopup.Instance != null && dyingTiles.Count > 0)
                            {
                                Vector3 center = Vector3.zero;
                                for (int d = 0; d < dyingTiles.Count; d++)
                                    if (dyingTiles[d] != null) center += dyingTiles[d].transform.position;
                                center /= Mathf.Max(1, dyingTiles.Count);

                                int baseBonus = step.DetonationBonus - step.DetonationHeat;
                                BonusPopup.Instance.ShowDetonation("", baseBonus, center);
                                if (step.DetonationHeat > 0)
                                    BonusPopup.Instance.ShowHeatBonus(step.DetonationHeat, center);
                            }

                            try
                            {
                                grid.RemoveTiles(step.ExplodedCells);
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"[GameVisualBridge] RemoveTiles error: {ex.Message}");
                            }

                            yield return new WaitForSeconds(POST_EXPLOSION_PAUSE);
                        }
                        else
                        {
                            // Job 5: null or empty ExplodedCells — skip animation
                            // but still call RemoveTiles with empty list (safe no-op in GridManager)
                            Debug.Log("[GameVisualBridge] Phase=Exploding: no cells to explode (null or empty) — skipping explosion animation.");
                            try
                            {
                                // Pass empty list to RemoveTiles — this is safe (GridManager checks for empty)
                                grid.RemoveTiles(new List<UnityEngine.Vector2Int>());
                                Debug.Log("[GameVisualBridge]   RemoveTiles called with empty list (no-op).");
                            }
                            catch (System.Exception ex)
                            {
                                Debug.LogWarning($"[GameVisualBridge] RemoveTiles (empty) error: {ex.Message}");
                            }
                        }
                        break;
                    }

                    // ── GravityApplied ───────────────────────────────────────────────
                    // Job 5: ApplyGravity() is ALWAYS yielded, regardless of prior state.
                    case RulesEngine.ResolutionPhase.GravityApplied:
                    {
                        Debug.Log("[GameVisualBridge] Phase=GravityApplied: running visual gravity (always executes).");

                        // Job 5 guarantee: ApplyGravity is always awaited — never skipped
                        yield return StartCoroutine(grid.ApplyGravity());
                        Debug.Log("[GameVisualBridge]   Visual gravity complete.");

                        // No mid-chain RebuildFromRulesEngine — it destroys/recreates all tiles
                        // causing a visual glitch. Final rebuild happens in Complete phase.

                        Debug.Log($"[GameVisualBridge]   Waiting {GRAVITY_SETTLE_PAUSE:F2}s gravity settle pause.");
                        yield return new WaitForSeconds(GRAVITY_SETTLE_PAUSE);
                        break;
                    }

                    // ── Complete ──────────────────────────────────────────────────────
                    // Job 5: FinalizeDrop() is ALWAYS called, even if TotalScore == 0.
                    case RulesEngine.ResolutionPhase.Complete:
                    {
                        int completedScore = step.TotalScore; // May be 0 — that's fine
                        Debug.Log($"[GameVisualBridge] Phase=Complete: TotalScore={completedScore}. Resolution finished cleanly.");

                        // Job 5 guarantee: FinalizeDrop is ALWAYS called in Complete phase,
                        // regardless of TotalScore value (including 0)
                        try
                        {
                            rules.FinalizeDrop();
                            Debug.Log("[GameVisualBridge]   FinalizeDrop called successfully in Complete phase (score may be 0 — that's OK).");
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[GameVisualBridge] FinalizeDrop in Complete phase threw: {ex.Message}");
                            // Continue even if FinalizeDrop throws — the safe wrapper will also call it
                        }

                        try
                        {
                            // Sync without destroying tiles — avoids visual pop
                            grid.SyncToRulesState(rules);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[GameVisualBridge] SyncToRulesState error: {ex.Message}");
                        }

                        try { ApplyAllPrimedGlows(grid, rules); }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[GameVisualBridge] ApplyAllPrimedGlows error: {ex.Message}");
                        }

                        // Sync scores to HUD — null-safe
                        try
                        {
                            if (HUDManager.Instance != null && ScoreManager.Instance != null)
                            {
                                HUDManager.Instance.SetPlayerScore(ScoreManager.Instance.PlayerScore);
                                HUDManager.Instance.SetAIScore(ScoreManager.Instance.AIScore);
                                Debug.Log($"[GameVisualBridge]   HUD scores synced: P1={ScoreManager.Instance.PlayerScore} AI={ScoreManager.Instance.AIScore}");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.LogWarning($"[GameVisualBridge] HUD sync error: {ex.Message}");
                        }

                        Debug.Log($"[GameVisualBridge]   Waiting {POST_RESOLUTION_PAUSE:F2}s post-resolution pause.");
                        yield return new WaitForSeconds(POST_RESOLUTION_PAUSE);

                        Debug.Log($"[GameVisualBridge]   Invoking onComplete(score={completedScore})");
                        onComplete?.Invoke(completedScore);
                        resolving = false;
                        loopExitedCleanly = true;
                        break;
                    }

                    // ── Default / Unexpected phase ────────────────────────────────────
                    // Job 5: Log unexpected phase and stop resolution to avoid infinite loop.
                    default:
                    {
                        Debug.LogWarning($"[GameVisualBridge] UNEXPECTED/UNHANDLED phase: '{step.Phase}' " +
                                         $"(int value={(int)step.Phase}) at iteration {safetyStepCount}. " +
                                         $"Stopping resolution to prevent infinite loop. " +
                                         $"Last known RulesEngine phase: {rules.CurrentPhase}. " +
                                         $"This likely indicates a new phase was added to RulesEngine without " +
                                         $"a corresponding case in GameVisualBridge.");
                        resolving = false;
                        // Don't call onComplete here — the safe wrapper will handle it
                        break;
                    }
                }
            }

            // ── Loop exit logging ─────────────────────────────────────────────────────

            if (safetyStepCount >= MAX_STEPS)
            {
                Debug.LogError($"[GameVisualBridge] NextStep loop hit safety cap ({MAX_STEPS} iterations) — forced stop. " +
                               $"Last known RulesEngine phase: {rules.CurrentPhase}. " +
                               $"This indicates an infinite loop in the resolution state machine.");
                onComplete?.Invoke(0);
            }
            else if (loopExitedCleanly)
            {
                Debug.Log($"[GameVisualBridge] NextStep loop exited cleanly after {safetyStepCount} iteration(s).");
            }
            else
            {
                Debug.LogWarning($"[GameVisualBridge] NextStep loop exited via break/default after {safetyStepCount} iteration(s) " +
                                 $"(non-clean exit — phase was {rules.CurrentPhase}). " +
                                 $"onComplete may not have been called from Complete phase — safe wrapper will handle.");
            }
        }

        private void ApplyAllPrimedGlows(GridManager grid, RulesEngine rules)
        {
            if (grid == null || rules == null) return;

            // Reset all tile visuals: kill tweens, stop flash coroutines, restore everything
            for (int col = 0; col < RulesEngine.COLS; col++)
                for (int row = 0; row < RulesEngine.ROWS; row++)
                {
                    Tile t = grid.GetTile(col, row);
                    if (t == null) continue;
                    t.transform.DOComplete();
                    int ts = Mathf.Clamp(Mathf.RoundToInt(grid.CellSize * 200f), 64, 512);
                    float ns = ts / 100f;
                    float correctScale = (grid.CellSize * 0.88f) / ns;
                    t.transform.localScale = new Vector3(correctScale, correctScale, 1f);
                    t.ResetVisuals();
                    t.ClearPrimedGlow();
                }

            // Reapply only active primed words
            PrimedWordRegistry registry = rules.PrimedRegistry;
            if (registry == null) return;

            int currentTurn = rules.GlobalTurn;

            for (int p = 0; p < registry.Count; p++)
            {
                var pw = registry.GetByIndex(p);
                if (pw == null || pw.Cells == null) continue;

                // Heat Fuse: glow shifts gold → orange → white-hot based on survived turns
                int survived = Mathf.Max(0, currentTurn - pw.PrimedOnTurn);
                int heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                bool justPrimed = (pw.PrimedOnTurn == currentTurn - 1 || pw.PrimedOnTurn == currentTurn);

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Tile tile = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                    if (tile != null)
                    {
                        int fuse = Mathf.Max(0, pw.ExpiresOnTurn - currentTurn);
                        try { tile.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: justPrimed, heatLevel: heatLevel, fuseRemaining: fuse); }
                        catch { /* ignore */ }
                    }
                }
            }
        }

        // =============================================================================
        // AI TURN
        // =============================================================================

        /// <summary>
        /// Executes the AI's turn with full visual sequencing.
        /// Job 3: Safety timeout calls ForceReset() before continuing.
        /// </summary>
        public IEnumerator ExecuteAITurnCoroutine()
        {
            Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: starting AI turn.");

            if (MatchController.Instance == null)
            {
                Debug.LogWarning("[GameVisualBridge] ExecuteAITurnCoroutine: MatchController is null — skipping.");
                yield break;
            }
            if (!MatchController.Instance.IsMatchActive)
            {
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: match not active — skipping.");
                yield break;
            }
            if (MatchController.Instance.IsPlayerDone(MatchController.PLAYER_AI))
            {
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: AI has no turns remaining — skipping.");
                yield break;
            }

            // Wait for any in-progress visual playback to finish, with safety timeout
            int safety = 0;
            while (_isPlayingBack && safety < SAFETY_FRAME_LIMIT)
            {
                safety++;
                yield return null;
            }

            // Job 3: If safety limit hit, call ForceReset() to guarantee clean state
            if (safety >= SAFETY_FRAME_LIMIT)
            {
                Debug.LogWarning($"[GameVisualBridge] ExecuteAITurnCoroutine: safety timeout after " +
                                 $"{SAFETY_FRAME_LIMIT} frames waiting for previous playback to finish. " +
                                 $"Calling ForceReset() to recover and continue with AI turn.");
                ForceReset();
            }
            else if (safety > 0)
            {
                Debug.Log($"[GameVisualBridge] ExecuteAITurnCoroutine: waited {safety} frame(s) for " +
                          $"previous playback to clear.");
            }

            // AI thinking delay
            float aiDelay = (AIAgent.Instance != null) ? AIAgent.Instance.Delay : 0.8f;
            Debug.Log($"[GameVisualBridge] ExecuteAITurnCoroutine: AI thinking delay {aiDelay:F2}s");
            yield return new WaitForSeconds(aiDelay);

            if (!MatchController.Instance.IsMatchActive)
            {
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: match ended during AI delay — aborting.");
                yield break;
            }

            // Delegate to AIAgent for move evaluation
            int  bestCol    = -1;
            int  bestSlot   = -1;
            char bestLetter = '\0';

            bool foundMove = false;
            try
            {
                if (AIAgent.Instance != null)
                {
                    foundMove = AIAgent.Instance.EvaluateBestMove(
                        out bestCol, out bestSlot, out bestLetter);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameVisualBridge] AIAgent.EvaluateBestMove threw: {ex}");
                foundMove = false;
            }

            Debug.Log($"[GameVisualBridge] ExecuteAITurnCoroutine: AI evaluated move — " +
                      $"foundMove={foundMove} bestCol={bestCol} bestSlot={bestSlot} bestLetter='{bestLetter}'");

            // Fallback if AIAgent not available or failed
            if (!foundMove)
            {
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: AIAgent gave no move — using random fallback.");
                PlayerHand aiHand = MatchController.Instance.GetHand(MatchController.PLAYER_AI);
                if (aiHand == null)
                {
                    Debug.LogWarning("[GameVisualBridge] AI hand is null -- skipping AI turn.");
                    yield break;
                }

                List<int> availCols = new List<int>();
                for (int c = 0; c < GridManager.COLS; c++)
                {
                    if (RulesEngine.Instance != null && RulesEngine.Instance.IsColumnAvailable(c))
                        availCols.Add(c);
                }

                if (availCols.Count == 0)
                {
                    Debug.Log("[GameVisualBridge] AI: no columns available -- skipping.");
                    yield break;
                }

                bestCol = availCols[UnityEngine.Random.Range(0, availCols.Count)];

                for (int s = 0; s < PlayerHand.HAND_SIZE; s++)
                {
                    if (aiHand.GetSlot(s) != '\0')
                    {
                        bestSlot   = s;
                        bestLetter = aiHand.GetSlot(s);
                        break;
                    }
                }
            }

            if (bestCol < 0 || bestLetter == '\0')
            {
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: no valid move found after fallback — skipping AI turn.");
                yield break;
            }

            Debug.Log($"[GameVisualBridge] ExecuteAITurnCoroutine: AI dropping '{bestLetter}' into col {bestCol} (slot={bestSlot})");

            // Execute the drop with step-by-step visual resolution
            _isPlayingBack = true;

            int totalScore = 0;

            yield return StartCoroutine(RunStepByStepResolutionSafe(
                bestCol, bestLetter, MatchController.PLAYER_AI,
                (score) => { totalScore = score; },
                null));

            // Bookkeeping
            try
            {
                if (MatchController.Instance != null)
                {
                    Debug.Log($"[GameVisualBridge] ExecuteAITurnCoroutine: calling CompleteDropBookkeeping " +
                              $"player={MatchController.PLAYER_AI} score={totalScore} slot={bestSlot}");
                    MatchController.Instance.CompleteDropBookkeeping(
                        MatchController.PLAYER_AI, totalScore, bestSlot);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[GameVisualBridge] AI turn bookkeeping error: {ex}");
            }
            finally
            {
                _isPlayingBack = false;
                Debug.Log("[GameVisualBridge] ExecuteAITurnCoroutine: AI turn complete. _isPlayingBack = false");
            }
        }

        // =============================================================================
        // VISUAL EFFECT HELPERS
        // =============================================================================

        /// <summary>Scale pop effect — punch up to peakScale then back to original.</summary>
        private IEnumerator ScalePop(Transform t, float peakScale, float duration)
        {
            if (t == null) yield break;
            Vector3 original = t.localScale;
            Vector3 peak = original * peakScale;
            float half = duration * 0.5f;

            float elapsed = 0f;
            while (elapsed < half)
            {
                elapsed += Time.deltaTime;
                float p = Mathf.Clamp01(elapsed / half);
                if (t != null) t.localScale = Vector3.Lerp(original, peak, p);
                yield return null;
            }

            elapsed = 0f;
            while (elapsed < half)
            {
                elapsed += Time.deltaTime;
                float p = Mathf.Clamp01(elapsed / half);
                if (t != null) t.localScale = Vector3.Lerp(peak, original, p);
                yield return null;
            }

            if (t != null) t.localScale = original;
        }

    }
}
