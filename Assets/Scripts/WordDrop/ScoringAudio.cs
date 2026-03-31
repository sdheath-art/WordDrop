using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Scoring sound effects using RTT's points1-7.wav clips with ascending pitch.
    /// Each tile in a scored word plays a clip at higher pitch, creating the
    /// RTT musical scale effect.
    /// </summary>
    public class ScoringAudio : MonoBehaviour
    {
        public static ScoringAudio Instance { get; private set; }

        // ── Tuning (matches RTT AudioManager) ──────────────────────────────────
        private const float BASE_PITCH       = 1.0f;
        private const float PITCH_INCREMENT  = 0.10f;  // 10% per tile, same as RTT
        private const float MAX_PITCH        = 2.0f;
        private const float COUNT_VOLUME     = 0.7f;
        private const float TOTAL_VOLUME     = 0.9f;

        // ── Audio ───────────────────────────────────────────────────────────────
        private AudioClip[] _pointsClips;   // points1-7.wav from RTT
        private AudioClip _totalClip;
        private AudioSource _source;
        private int _sequenceClipIndex;     // which clip to use for current sequence

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("ScoringAudio");
                go.AddComponent<ScoringAudio>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _source = gameObject.AddComponent<AudioSource>();
            _source.playOnAwake = false;
            _source.mute = true; // Disabled for now

            // Load RTT points clips (points1.wav through points7.wav)
            _pointsClips = new AudioClip[7];
            int loaded = 0;
            for (int i = 0; i < 7; i++)
            {
                _pointsClips[i] = Resources.Load<AudioClip>($"SFX/points{i + 1}");
                if (_pointsClips[i] != null) loaded++;
            }

            _totalClip = Resources.Load<AudioClip>("SFX/points_total");

            _sequenceClipIndex = Random.Range(0, 7);

            Debug.Log($"[ScoringAudio] Loaded: {loaded}/7 points clips, total={(_totalClip != null)}");
        }

        /// <summary>
        /// Call at the start of each scoring sequence to pick a random clip.
        /// Same clip plays for the whole sequence at ascending pitch (RTT style).
        /// </summary>
        public void StartSequence()
        {
            _sequenceClipIndex = Random.Range(0, _pointsClips.Length);
        }

        /// <summary>Play the tile count sound at ascending pitch for tile index in sequence.</summary>
        public void PlayTileCount(int tileIndex)
        {
            AudioClip clip = _pointsClips[_sequenceClipIndex];
            if (clip == null || _source == null) return;

            float pitch = Mathf.Min(BASE_PITCH + tileIndex * PITCH_INCREMENT, MAX_PITCH);
            _source.pitch = pitch;
            _source.PlayOneShot(clip, COUNT_VOLUME);
        }

        /// <summary>Play the total score reveal sound.</summary>
        public void PlayTotalReveal()
        {
            if (_totalClip == null || _source == null) return;
            _source.pitch = 1f;
            _source.PlayOneShot(_totalClip, TOTAL_VOLUME);
        }
    }
}
