using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Tracks player and AI scores separately across the match.
    /// TotalScore is an alias for PlayerScore for backward compatibility.
    ///
    /// Combo system:
    ///   ComboCount tracks consecutive player turns where at least one word was scored.
    ///   TurnsUntilComboExpires counts down each player turn; reset to 2 when a word is scored.
    ///   GetComboMultiplier() returns 1x / 1.5x / 2x based on combo count.
    ///
    ///   Timeline example:
    ///     Turn 1: AdvancePlayerTurn() → expires stays 0 (no-op)
    ///             RecordWordScored()  → ComboCount=1, expires=2
    ///     Turn 2: AdvancePlayerTurn() → expires=1
    ///             RecordWordScored()  → ComboCount=2 (×1.5!), expires=2
    ///     Turn 3: AdvancePlayerTurn() → expires=1
    ///             RecordWordScored()  → ComboCount=3 (×2.0!), expires=2
    ///     Turn 4: AdvancePlayerTurn() → expires=1
    ///             No word scored      → (expires stays 1)
    ///     Turn 5: AdvancePlayerTurn() → expires=0 → ComboCount reset to 0
    /// </summary>
    public class ScoreManager : MonoBehaviour
    {
        public static ScoreManager Instance { get; private set; }

        // ── Score state ───────────────────────────────────────────────────────────
        public int PlayerScore { get; private set; } = 0;
        public int AIScore     { get; private set; } = 0;

        /// <summary>Backward-compat alias for PlayerScore.</summary>
        public int TotalScore => PlayerScore;

        // ── Combo state ───────────────────────────────────────────────────────────

        /// <summary>
        /// How many consecutive player turns the player has scored at least one word.
        ///   0 = no combo
        ///   1 = scored last turn (no bonus yet, multiplier still ×1.0)
        ///   2 = scored 2 turns in a row → ×1.5
        ///   3+ = scored 3+ turns in a row → ×2.0
        /// </summary>
        public int ComboCount { get; private set; } = 0;

        /// <summary>
        /// How many more player turns remain before the combo expires.
        /// Set to 2 whenever a word is scored on the player's turn.
        /// Decremented by AdvancePlayerTurn() at the start of each player turn.
        /// When it hits 0, ComboCount resets to 0.
        /// </summary>
        public int TurnsUntilComboExpires { get; private set; } = 0;

        /// <summary>True if a combo bonus is currently active (ComboCount >= 2).</summary>
        public bool IsComboActive => ComboCount >= 2;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
//             WDLog.Log("[ScoreManager] Awake");
        }

        // ── Combo API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Called at the START of each player turn (before the tile is dropped).
        /// Ticks down TurnsUntilComboExpires. If it reaches 0, the combo resets.
        /// AI turns do NOT call this — combo is player-only.
        /// </summary>
        public void AdvancePlayerTurn()
        {
            if (TurnsUntilComboExpires <= 0)
            {
                // No active combo window — nothing to tick
                return;
            }

            TurnsUntilComboExpires--;
//             WDLog.Log($"[ScoreManager] AdvancePlayerTurn — " +
                      // $"TurnsUntilComboExpires={TurnsUntilComboExpires} ComboCount={ComboCount}");

            if (TurnsUntilComboExpires == 0 && ComboCount > 0)
            {
                int oldCombo = ComboCount;
                ComboCount = 0;
//                 WDLog.Log($"[ScoreManager] Combo EXPIRED — was {oldCombo} → reset to 0. " +
                          // $"No word scored for 2 consecutive turns.");
                NotifyHUDCombo();
            }
        }

        /// <summary>
        /// Called after a new word is scored on the board.
        ///   isPlayer = true  → advances player combo counter, resets expiry window.
        ///   isPlayer = false → AI scored; player combo is NOT affected.
        ///
        /// Combo progression:
        ///   First word scored:  ComboCount 0→1  (no bonus yet, ×1.0)
        ///   Second consecutive: ComboCount 1→2  (×1.5 bonus active)
        ///   Third consecutive:  ComboCount 2→3  (×2.0 bonus active)
        ///   Subsequent:         ComboCount stays at 3+ (×2.0 cap)
        ///
        /// Each call resets TurnsUntilComboExpires to 2, giving the player
        /// a 2-turn window to score again before the combo breaks.
        /// </summary>
        public void RecordWordScored(bool isPlayer)
        {
            if (!isPlayer)
            {
//                 WDLog.Log("[ScoreManager] RecordWordScored(AI) — player combo unchanged.");
                return;
            }

            // Increment combo count (no cap — GetComboMultiplier handles the ceiling)
            ComboCount++;

            // Reset the expiry window to 2 turns from now
            TurnsUntilComboExpires = 2;

            float mult = GetComboMultiplier();
//             WDLog.Log($"[ScoreManager] RecordWordScored(Player) — " +
                      // $"ComboCount={ComboCount}  multiplier=×{mult:F1}  " +
                      // $"expiresIn={TurnsUntilComboExpires} turns");

            NotifyHUDCombo();
        }

        /// <summary>
        /// Returns the score multiplier based on the current combo count.
        ///   ComboCount 0-1 → ×1.0 (no bonus)
        ///   ComboCount 2   → ×1.5
        ///   ComboCount 3+  → ×2.0
        /// </summary>
        public float GetComboMultiplier()
        {
            if (ComboCount >= 3) return 2.0f;
            if (ComboCount == 2) return 1.5f;
            return 1.0f;
        }

        /// <summary>Resets the combo state. Call on match reset.</summary>
        private void ResetCombo()
        {
            ComboCount             = 0;
            TurnsUntilComboExpires = 0;
            NotifyHUDCombo();
        }

        private void NotifyHUDCombo()
        {
            if (HUDManager.Instance != null)
                HUDManager.Instance.SetCombo(ComboCount, GetComboMultiplier());
        }

        // ── Score API ─────────────────────────────────────────────────────────────

        // ── FLY-UP SCORE BANK (2026-09-09 Spencer) ──────────────────────────────────
        // "the score should be running up when the words hit the number total right?"
        // Yes — the counter has to jump on IMPACT, not run on its own clock while letters
        // happen to be in the air. The blast DEPOSITS its points here instead of awarding
        // them; each letter that lands WITHDRAWS an equal share, so the number climbs in
        // step with the hits.
        // Flush() exists because the letters do not always fly — reduced motion, no
        // objective, suppressed fly-ups — and the player must never silently lose points
        // because an animation was skipped. Anything undelivered is paid out at the end.
        private int _flyPending;
        /// <summary>Points deposited by a blast that have not landed on the counter yet. The
        /// blast-limit lose check waits on this, or the last blast could lose a level its own
        /// points were about to win. 2026-10-20.</summary>
        public bool FlyScorePending => _flyPending > 0;
        private int _flyRemaining;

        private int _flyBurstTotal;   // whole burst, for sizing the ONE finale
        private float _flyActivityAt;  // last deposit or landing, unscaled

        // Safety net ONLY. Points must never depend on an animation having played — the
        // letters do not always fly (reduced motion, suppressed fly-ups, no objective).
        // Time-driven rather than tied to the resolution ending, because the resolution
        // finishes while the letters are still in the air. 2026-09-09 Spencer.
        private const float FLY_FLUSH_TIMEOUT = 1.6f;

        /// <summary>A blast whose points have NO letters flying to deliver them pays after this short beat
        /// instead of waiting out FLY_FLUSH_TIMEOUT. 2026-10-20 Spencer: "the ding and checkmark for the
        /// passing of the level ... should happen immediately when the last word you needed to make pops".
        /// Log: last word popped, nothing flew (gold-only fly-ups), points sat in the bank 1.6s, a row ROSE
        /// in that gap, then the level registered as won. The beat only lets a fly-up that is about to
        /// register (same resolution, a few frames later) claim the points first.</summary>
        private const float FLY_NO_LETTERS_GRACE = 0.15f;

        private void Update()
        {
            if (_flyPending > 0 && _flyRemaining <= 0 && Time.unscaledTime - _flyActivityAt > FLY_NO_LETTERS_GRACE)
            {
                int pts = _flyPending, burst = _flyBurstTotal;
                WDLog.Log($"[ScoreLag] t={Time.unscaledTime:F2} NO-FLY PAY {pts} — no letters in flight, paid now");
                _flyPending = 0; _flyBurstTotal = 0;
                AddPlayerScore(pts);
                HUDManager.Instance?.PlayScoreSlam(burst > 0 ? burst : pts);
                return;
            }
            if (_flyPending > 0 && Time.unscaledTime - _flyActivityAt > FLY_FLUSH_TIMEOUT)
                FlushFlyScore();
        }

        public void DepositFlyScore(int pts)
        {
            if (pts <= 0) return;
            WDLog.Log($"[ScoreLag] t={Time.unscaledTime:F2} DEPOSIT {pts} (pending was {_flyPending}, remaining={_flyRemaining})");
            _flyPending    += pts;
            _flyBurstTotal += pts;
            _flyActivityAt  = Time.unscaledTime;
        }

        /// <summary>Called once by the fly-up when it knows how many letters will land.</summary>
        public void SetFlyLetterCount(int n)
        {
            // ACCUMULATE. A chain fires several fly-ups and the later ones register while
            // earlier letters are still in the air; overwriting the count made the shares
            // divide by the wrong number. 2026-09-09 Spencer.
            _flyRemaining = Mathf.Max(0, _flyRemaining) + Mathf.Max(0, n);
        }

        /// <summary>One letter landed — pay its share of the bank.</summary>
        public void AwardOneFlyShare()
        {
            WDLog.Log($"[ScoreLag] t={Time.unscaledTime:F2} AWARD share (pending={_flyPending}, remaining={_flyRemaining})");
            if (_flyPending <= 0) { _flyRemaining = Mathf.Max(0, _flyRemaining - 1); return; }
            int share = _flyRemaining > 1 ? Mathf.CeilToInt(_flyPending / (float)_flyRemaining) : _flyPending;
            share = Mathf.Min(share, _flyPending);
            _flyPending   -= share;
            _flyRemaining  = Mathf.Max(0, _flyRemaining - 1);
            _flyActivityAt = Time.unscaledTime;
            AddPlayerScore(share);
            // Last letter of the burst — fire the finale ONCE, sized by the whole burst
            // rather than by this one share.
            if (_flyPending <= 0 || _flyRemaining <= 0)
            {
                HUDManager.Instance?.PlayScoreSlam(_flyBurstTotal);
                _flyBurstTotal = 0;
            }
        }

        /// <summary>Pay out anything the landings did not deliver. Safe to call every turn.</summary>
        public void FlushFlyScore()
        {
            if (_flyPending > 0) WDLog.Log($"[ScoreLag] t={Time.unscaledTime:F2} TIMEOUT-FLUSH {_flyPending} — the 1.6s backstop paid this, not the landings");
            if (_flyPending > 0) { int r = _flyPending; _flyPending = 0; AddPlayerScore(r); }
            _flyRemaining = 0; _flyBurstTotal = 0;
        }

        /// <summary>Adds points to the player's running total.</summary>
        public void AddPlayerScore(int pts)
        {
            PlayerScore += pts;
//             WDLog.Log($"[ScoreManager] AddPlayerScore({pts}) → PlayerScore={PlayerScore}");

            if (HUDManager.Instance != null)
                HUDManager.Instance.SetScore(PlayerScore);

            // Survival stage-clear must fire on ANY PlayerScore change, not
            // only the player-drop path (CompleteDropBookkeeping → NotifyScoreDelta).
            // Bug repro (2026-05-21): player at 586/600 used board-swap that scored
            // +25 → PlayerScore=611, crossing target. CheckStageClear was never
            // called because board-swap scoring (HandManager.cs:1716/1824/2218)
            // and Survival clear-bonus (SurvivalManager.cs:575/587) call AddScore
            // directly without firing NotifyScoreDelta. Modal "waited for next
            // drop" because NotifyDropCommitted on the next player drop was the
            // first thing to re-evaluate the target. Hooking the check here
            // covers EVERY score-add path, including future ones.
            if (pts > 0
                && (SurvivalManager.IsSurvivalMode || SurvivalManager.ScoreRunMode)
                && SurvivalManager.Instance != null)
            {
                SurvivalManager.Instance.CheckStageClear();
            }
        }

        /// <summary>Adds points to the AI's running total.</summary>
        public void AddAIScore(int pts)
        {
            AIScore += pts;
//             WDLog.Log($"[ScoreManager] AddAIScore({pts}) → AIScore={AIScore}");
        }

        /// <summary>
        /// Legacy method kept for backward compatibility with RoundManager.
        /// Routes the round score to PlayerScore.
        /// </summary>
        public void AddScore(int roundScore, int movesRemaining)
        {
            AddPlayerScore(roundScore);
        }

        /// <summary>Resets both scores and the combo state to zero.</summary>
        public void ResetScore()
        {
            PlayerScore = 0;
            AIScore     = 0;
            ResetCombo();

            if (HUDManager.Instance != null)
                HUDManager.Instance.SetScore(0);

//             WDLog.Log("[ScoreManager] Scores reset — PlayerScore=0, AIScore=0, Combo=0");
        }

        // ── Legacy per-round API stubs ────────────────────────────────────────────

        public int GetRoundScore(int roundNumber)          => 0;
        public int GetRoundMovesRemaining(int roundNumber) => 0;
    }
}
