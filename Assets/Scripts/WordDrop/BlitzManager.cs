using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Manages the Blitz Mode — a 90-second timed solo score attack.
    /// No AI opponent. Player drops words as fast as possible.
    /// Timer uses Time.deltaTime so hitstop pauses the timer (fair to player).
    /// </summary>
    public class BlitzManager : MonoBehaviour
    {
        public static BlitzManager Instance { get; private set; }

        // ── Constants ─────────────────────────────────────────────────────────────
        public const float BLITZ_DURATION = 90f;
        public const float WARN_THRESHOLD = 10f; // last 10 seconds pulse red

        // ── State ─────────────────────────────────────────────────────────────────
        private static bool _isBlitzMode = false;
        public static bool IsBlitzMode
        {
            get => _isBlitzMode;
            set => _isBlitzMode = value;
        }

        private float _timeRemaining;
        private bool  _isTimerRunning;
        private bool  _pendingGameOver; // set when timer hits 0, wait for resolution

        public float TimeRemaining => _timeRemaining;
        public bool  IsTimerRunning => _isTimerRunning;
        public bool  IsTimeUp => _timeRemaining <= 0f && _isBlitzMode;

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
//             Debug.Log("[BlitzManager] Awake");
        }

        private void Update()
        {
            if (!_isBlitzMode || !_isTimerRunning) return;

            _timeRemaining -= Time.deltaTime;

            // Update HUD timer display every frame
            UpdateHUDTimer();

            if (_timeRemaining <= 0f)
            {
                _timeRemaining = 0f;
                _isTimerRunning = false;
                _pendingGameOver = true;
                UpdateHUDTimer(); // show 0:00
//                 Debug.Log("[BlitzManager] Time's up!");
                OnTimerExpired();
            }
        }

        // ── Public API ────────────────────────────────────────────────────────────

        public void StartTimer()
        {
            _timeRemaining = BLITZ_DURATION;
            _isTimerRunning = true;
            _pendingGameOver = false;
//             Debug.Log($"[BlitzManager] Timer started: {BLITZ_DURATION}s");
            UpdateHUDTimer();
        }

        public void StopTimer()
        {
            _isTimerRunning = false;
//             Debug.Log($"[BlitzManager] Timer stopped at {_timeRemaining:F1}s remaining");
        }

        public static void Reset()
        {
            _isBlitzMode = false;
            if (Instance != null)
            {
                Instance._isTimerRunning = false;
                Instance._pendingGameOver = false;
                Instance._timeRemaining = 0f;
            }
        }

        // ── Formatting ────────────────────────────────────────────────────────────

        /// <summary>Formats time as "M:SS" (e.g. "1:23").</summary>
        public string GetFormattedTime()
        {
            float t = Mathf.Max(0f, _timeRemaining);
            int minutes = Mathf.FloorToInt(t / 60f);
            int seconds = Mathf.FloorToInt(t % 60f);
            return $"{minutes}:{seconds:D2}";
        }

        /// <summary>Returns a color for the timer — white normally, pulsing red in last 10s.</summary>
        public Color GetTimerColor()
        {
            if (_timeRemaining > WARN_THRESHOLD)
                return new Color(0.95f, 0.95f, 1f, 1f); // white

            // Pulse between red and bright red in the last 10 seconds
            float pulse = Mathf.Abs(Mathf.Sin(Time.time * 3f)); // ~3 Hz pulse
            return Color.Lerp(
                new Color(1f, 0.25f, 0.20f, 1f),   // red
                new Color(1f, 0.55f, 0.45f, 1f),    // bright red/orange
                pulse);
        }

        // ── Internal ──────────────────────────────────────────────────────────────

        private void UpdateHUDTimer()
        {
            if (HUDManager.Instance == null) return;
            HUDManager.Instance.SetTurnCountdownText(GetFormattedTime(), GetTimerColor());
        }

        private void OnTimerExpired()
        {
            // If not mid-resolution, end immediately.
            // If mid-resolution, FullTurnSequence will check IsTimeUp after resolution.
            if (MatchController.Instance != null && !MatchController.Instance.IsProcessing)
            {
                EndBlitzMatch();
            }
            else
            {
                // Resolution in progress — _pendingGameOver flag is set.
                // HandManager's FullTurnSequence will call CheckBlitzTimeUp() after resolution.
//                 Debug.Log("[BlitzManager] Mid-resolution — will end after current resolution completes.");
            }
        }

        /// <summary>
        /// Called by HandManager after resolution completes to check if blitz time expired
        /// during the resolution.
        /// </summary>
        public bool CheckBlitzTimeUp()
        {
            if (!_isBlitzMode || !_pendingGameOver) return false;
            EndBlitzMatch();
            return true;
        }

        private void EndBlitzMatch()
        {
            _pendingGameOver = false;
            _isTimerRunning = false;

//             Debug.Log("[BlitzManager] Ending blitz match.");

            if (MatchController.Instance != null)
                MatchController.Instance.ForceGameOver();
        }
    }
}
