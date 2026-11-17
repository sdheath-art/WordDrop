using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// SOLE WORD-PRESENTATION PATH — STEP-BY-STEP VISUAL DRIVER
    /// ──────────────────────────────────────────────────────────
    /// 2026-10-20 cleanup: the AI-turn / step-by-step drop playback
    /// (ExecuteAITurnCoroutine, RunStepByStepResolution*, ApplyAllPrimedGlows) was
    /// removed as dead code — restore from tag pre-cleanup-2026-10-20 if needed.
    ///
    /// Still owns: ForceReset, IsPlayingBack, MatchController event handlers,
    /// TriggerSurvivalLongWordReward, ScalePop.
    /// </summary>
    public class GameVisualBridge : MonoBehaviour
    {
        public static GameVisualBridge Instance { get; private set; }

        // -- Step-by-step resolution state -------------------------------------------

        private bool _isPlayingBack = false;

        // -- Unity lifecycle ----------------------------------------------------------

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
//             WDLog.Log("[GameVisualBridge] Awake");
        }

        private void Start()
        {
            SubscribeToRulesEngineEvents();
            SubscribeToMatchControllerEvents();
//             WDLog.Log("[GameVisualBridge] Subscribed to all events");
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
//                 WDLog.Log($"[GameVisualBridge] STATE DUMP: " +
                          // $"_isPlayingBack={_isPlayingBack} | " +
                          // $"CurrentPlayer={MatchController.Instance?.CurrentPlayer} | " +
                          // $"TotalTurnsUsed={MatchController.Instance?.TotalTurnsUsed} | " +
                          // $"IsMatchActive={MatchController.Instance?.IsMatchActive}");
            }
        }

        // =============================================================================
        // EVENT SUBSCRIPTIONS
        // =============================================================================

        private void SubscribeToRulesEngineEvents() { }
        private void UnsubscribeFromRulesEngineEvents() { }

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
//             WDLog.Log($"[GameVisualBridge] TurnEnd: player={evt.PlayerIndex} " +
                      // $"turn={evt.PlayerTurnNumber} global={evt.GlobalTurnIndex}");

            if (HUDManager.Instance != null && MatchController.Instance != null)
            {
                // Survival/Level: don't overwrite the solo-mode label with turn count
                if (!SurvivalManager.IsSurvivalMode && !GameManager.IsLevelMode)
                {
                    int totalRemaining = MatchController.Instance.TotalMaxTurns
                                       - MatchController.Instance.TotalTurnsUsed;
                    HUDManager.Instance.SetTurnsRemaining(
                        totalRemaining, MatchController.Instance.TotalMaxTurns);
                }

                // Always show human player's swap and rewrite counts
                int swaps = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
                HUDManager.Instance.ShowSwapCount(swaps);
                int rewrites = MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN);
                HUDManager.Instance.ShowRewriteCount(rewrites);
            }
        }

        private void HandleHandRefilled(HandRefilledEvent evt)
        {
//             WDLog.Log($"[GameVisualBridge] HandRefilled: player={evt.PlayerIndex} " +
                      // $"letters={new string(evt.Letters)}");
        }

        private void HandleMatchEnd(MatchEndEvent evt)
        {
//             WDLog.Log($"[GameVisualBridge] MatchEnd: winner={evt.WinnerPlayerIndex} " +
                      // $"P={evt.PlayerScore} AI={evt.AIScore}");
        }

        private void HandleSwapUsed(SwapUsedEvent evt)
        {
//             WDLog.Log($"[GameVisualBridge] SwapUsed: player={evt.PlayerIndex} " +
                      // $"slot={evt.HandSlot} '{evt.OldLetter}'->'{evt.NewLetter}' " +
                      // $"swapsRemaining={evt.SwapsRemaining}");

            GameAudio.Instance?.PlaySwap();
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
//                 WDLog.Log("[GameVisualBridge] ForceReset() called but _isPlayingBack was already false — no-op.");
            }
        }

        /// <summary>
        /// Phase 11+ Survival triangularity: reward every scored word ≥ 5
        /// letters with a chunkier payoff than short words. Tracks the
        /// session-longest word for the game-over debrief, freezes the
        /// rising-row timer, refills edit charges, and escalates visual +
        /// audio feedback per length tier. Called from the WordsScored
        /// phase; gated on SurvivalManager.IsSurvivalMode and the human
        /// player. Cascades are fine to trigger this — PM said exclude
        /// cascade-specific amplification, but a long word is still a long
        /// word even if gravity assembled it.
        /// </summary>
        public void TriggerSurvivalLongWordReward(
            string word, List<Tile> scoredTiles, bool isPlayer)
        {
            // Always track the session-longest, even for short 3/4 words
            // so the game-over "Best Word" stat reflects the real max.
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.UpdateLongestWord(word);

            int len = word.Length;
            if (len < 5) return;

            // Rise-timer freeze, edit refill, popup tier per length.
            float freezeSeconds;
            int   editRefills;
            string tierLabel;
            float popupOffsetY;
            switch (len)
            {
                case 5:
                    freezeSeconds = 2f;
                    editRefills   = 1;
                    tierLabel     = "NICE!";
                    popupOffsetY  = 2.0f;
                    break;
                case 6:
                    freezeSeconds = 4f;
                    editRefills   = 2;
                    tierLabel     = "EXCELLENT!";
                    popupOffsetY  = 2.5f;
                    break;
                default:  // 7+
                    freezeSeconds = 6f;
                    editRefills   = 3;
                    tierLabel     = "MASTERFUL!";
                    popupOffsetY  = 3.0f;
                    break;
            }

            // 1. Freeze the rising-row timer.
            SurvivalManager.Instance?.ApplyRiseFreeze(freezeSeconds);

            // 2. Refill edit charges (each call caps at the Survival cap of 3).
            if (MatchController.Instance != null)
            {
                for (int i = 0; i < editRefills; i++)
                    MatchController.Instance.RefundRewriteCharge(
                        MatchController.PLAYER_HUMAN);
            }

            // 3. Escalating text popup above the scored tiles.
            if (BonusPopup.Instance != null && scoredTiles != null && scoredTiles.Count > 0)
            {
                Vector3 wc = Vector3.zero;
                int n = 0;
                for (int st = 0; st < scoredTiles.Count; st++)
                    if (scoredTiles[st] != null) { wc += scoredTiles[st].transform.position; n++; }
                if (n > 0) wc /= n;
                Color tierColor = len >= 7
                    ? new Color(1.00f, 0.85f, 0.25f, 1f)   // gold — masterful
                    : (len == 6
                        ? new Color(1.00f, 0.55f, 0.90f, 1f)   // pink-magenta
                        : new Color(0.45f, 0.95f, 0.55f, 1f));  // green — nice
                BonusPopup.Instance.Show(tierLabel, tierColor,
                    wc + Vector3.up * popupOffsetY, 1.25f);
            }

            // 4. Escalating audio + haptics. Existing clips used at tier:
            //    5+ → primed chord (already fires above) + ScorePowerup overlay
            //    6+ → StageUp overlay
            //    7+ → reserved "signature" (using StageUp for now — future
            //         task: record a dedicated 7-letter jingle)
            if (len >= 5)
            {
                if (GameAudio.Instance != null && GameAudio.Instance.HasKit("powerup_created")) GameAudio.Instance.PlayKit("powerup_created"); else GameAudio.Instance?.PlayScorePowerup();   // 11-07 pick: Short Highlight Sparkle Layer Impact A6
                HapticsManager.Medium();
            }
            if (len >= 6)
            {
                GameAudio.Instance?.PlayStageUp();
                HapticsManager.Strong();
            }
            if (len >= 7)
            {
                // Signature beat — double StageUp stack for punch until a
                // dedicated clip ships. Phase 11+ audio polish task.
                GameAudio.Instance?.PlayStageUp();
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
