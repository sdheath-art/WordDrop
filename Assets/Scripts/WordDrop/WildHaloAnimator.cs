using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Lightweight idle animation for wild halo sprites — slow rotation, scale
    /// breathing, and alpha twinkle so the halo reads as alive magic, not a
    /// sticker. Attach to the halo GameObject (hand cards + board tiles both).
    ///
    /// Tuning constants at top; tweak freely without touching callers.
    /// </summary>
    [RequireComponent(typeof(SpriteRenderer))]
    public class WildHaloAnimator : MonoBehaviour
    {
        // Rotation: very slow — just enough to avoid sticker feel, not enough to
        // draw eye away from the card itself.
        private const float ROTATION_SPEED_DEG_PER_SEC = 8f;

        // Scale: subtle breathing around the base scale.
        private const float SCALE_PULSE_AMPLITUDE = 0.06f;  // ±6% of base size
        private const float SCALE_PULSE_HZ        = 0.45f;  // slow heartbeat

        // Alpha: gentle twinkle — stays mostly bright so the halo reads as "present"
        // not "blinking." Previous 0.55-1.0 range felt too flashy.
        private const float ALPHA_MIN      = 0.75f;
        private const float ALPHA_MAX      = 1.0f;
        private const float ALPHA_PULSE_HZ = 0.6f;

        // Random phase offset per instance so multiple halos don't pulse in lock-step.
        private float _phase;
        // Captured on first enable so we can modulate around the caller's scale.
        private Vector3 _baseScale;
        private bool    _baseScaleCaptured;
        private SpriteRenderer _sr;
        private Color _baseColor;

        private void Awake()
        {
            _sr = GetComponent<SpriteRenderer>();
            _phase = Random.Range(0f, Mathf.PI * 2f);
        }

        private void OnEnable()
        {
            // Re-capture base scale every enable — the halo may be re-created with
            // a different scale when the card/tile is rebuilt (e.g., orientation change).
            _baseScale = transform.localScale;
            _baseScaleCaptured = true;
            if (_sr != null) _baseColor = _sr.color;
        }

        private void Update()
        {
            if (!_baseScaleCaptured) return;

            float t = Time.time;

            // Rotation — continuous around Z.
            transform.Rotate(0f, 0f, ROTATION_SPEED_DEG_PER_SEC * Time.deltaTime, Space.Self);

            // Scale breathing.
            float scalePulse = 1f + SCALE_PULSE_AMPLITUDE * Mathf.Sin(t * SCALE_PULSE_HZ * Mathf.PI * 2f + _phase);
            transform.localScale = _baseScale * scalePulse;

            // Alpha twinkle — uses a SIN offset from the scale phase so the two
            // pulses are slightly out of sync (feels more organic than a single beat).
            if (_sr != null)
            {
                float raw = 0.5f + 0.5f * Mathf.Sin(t * ALPHA_PULSE_HZ * Mathf.PI * 2f + _phase + 1.1f);
                float a = Mathf.Lerp(ALPHA_MIN, ALPHA_MAX, raw);
                Color c = _baseColor;
                c.a = a;
                _sr.color = c;
            }
        }
    }
}
