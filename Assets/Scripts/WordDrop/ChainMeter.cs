using System;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Tracks a "chain meter" that fills from detonation scores. When full,
    /// arms BonusMode to enter on the next drop. Fill is skill-weighted:
    /// higher-scoring detonations (longer words, deeper chains, cluster bonuses)
    /// fill faster, rewarding the "broken build" fantasy (Balatro lesson #6)
    /// and Schüll's Perfect Contingency (player action directly drives outcome).
    ///
    /// Fill rate: meter += primedWord.Score * FILL_PER_SCORE_POINT.
    /// At 0.01 → 100pt detonation = 1 unit; 1000pt chain = 10 units.
    /// Capacity is static (not per-stage) because scores naturally escalate
    /// with stage progression — no extra tuning dimension needed for launch.
    /// Meter does NOT decay (don't punish slow, contemplative play).
    /// </summary>
    public class ChainMeter : MonoBehaviour
    {
        public static ChainMeter Instance { get; private set; }

        // ── Tuning ────────────────────────────────────────────────────────────────
        public const float CAPACITY          = 20f;
        // Fills from the FINAL detonation bonus (post chain-multiplier + cluster
        // + gold), not the raw primed word score. At 0.003: a 300pt detonation
        // fills ~5% (1 unit), a 3000pt big chain fills 45% (9 units), a 6000pt
        // legendary cluster near-fills meter (90%). Target: arms 1-2× per run
        // under moderate chain play.
        public const float FILL_PER_SCORE_PT = 0.003f;

        // ── State ─────────────────────────────────────────────────────────────────
        private float _fill;

        /// <summary>Current fill (0..CAPACITY).</summary>
        public float Fill => _fill;

        /// <summary>Fill ratio 0..1 for HUD progress bar.</summary>
        public float FillRatio => Mathf.Clamp01(_fill / CAPACITY);

        // ── Events ────────────────────────────────────────────────────────────────
        /// <summary>Fires whenever fill changes (HUD updates). float = new FillRatio.</summary>
        public event Action<float> OnFillChanged;

        /// <summary>Fires when meter reaches capacity (same frame BonusMode.Arm is called).</summary>
        public event Action OnMeterFilled;

        // ── Unity lifecycle ───────────────────────────────────────────────────────
        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnDetonationScored += HandleDetonationScored;
            }

            // BonusMode exit should drain the meter — prevents immediate re-arm.
            if (BonusMode.Instance != null)
                BonusMode.Instance.OnBonusExit += HandleBonusExit;
        }

        private void Start()
        {
            // Safety re-subscribe in case RulesEngine was created after our OnEnable.
            if (RulesEngine.Instance != null)
            {
                RulesEngine.Instance.OnDetonationScored -= HandleDetonationScored;
                RulesEngine.Instance.OnDetonationScored += HandleDetonationScored;
            }
            if (BonusMode.Instance != null)
            {
                BonusMode.Instance.OnBonusExit -= HandleBonusExit;
                BonusMode.Instance.OnBonusExit += HandleBonusExit;
            }
        }

        private void OnDisable()
        {
            if (RulesEngine.Instance != null)
                RulesEngine.Instance.OnDetonationScored -= HandleDetonationScored;
            if (BonusMode.Instance != null)
                BonusMode.Instance.OnBonusExit -= HandleBonusExit;
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void HandleDetonationScored(int finalBonus)
        {
            // Don't fill during bonus — bonus scoring shouldn't immediately re-arm
            // the next bonus. Meter resumes filling after bonus exits (HandleBonusExit
            // zeros it on the way out as a hard guarantee).
            if (BonusMode.Instance != null && BonusMode.Instance.IsActive) return;
            if (finalBonus <= 0) return;

            AddFill(finalBonus * FILL_PER_SCORE_PT);
        }

        private void HandleBonusExit(int banked, string reason)
        {
            // Drain on exit so the very next detonation doesn't re-arm instantly.
            // The meter has to be earned from scratch after each bonus.
            if (_fill > 0f)
            {
                _fill = 0f;
                OnFillChanged?.Invoke(0f);
            }
        }

        // ── Core ──────────────────────────────────────────────────────────────────

        private void AddFill(float amount)
        {
            if (amount <= 0f) return;
            if (BonusMode.Instance != null && (BonusMode.Instance.Armed || BonusMode.Instance.IsActive)) return;

            float prev = _fill;
            _fill = Mathf.Min(_fill + amount, CAPACITY);
            if (!Mathf.Approximately(prev, _fill))
                OnFillChanged?.Invoke(FillRatio);

            if (_fill >= CAPACITY && prev < CAPACITY)
            {
                Debug.Log($"[ChainMeter] FILLED (+{amount:F2}) — arming BonusMode.");
                OnMeterFilled?.Invoke();
                BonusMode.Instance?.Arm();
            }
            else
            {
                Debug.Log($"[ChainMeter] +{amount:F2} → {_fill:F2}/{CAPACITY} ({FillRatio:P0})");
            }
        }

        /// <summary>Called from SurvivalManager.StartSurvival to clean state.</summary>
        public void ResetForNewRun()
        {
            _fill = 0f;
            OnFillChanged?.Invoke(0f);
        }
    }
}
