using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Named Meltdown Moments — the signature dramatic sequence when a massive
    /// chain detonation fires (chain depth 3+).
    ///
    /// Full sequence (Spencer-approved):
    ///   Phase 1 — BUILD-UP (0.3-0.4s before explosion):
    ///     Escalating Perlin camera shake, board tile jitter, vignette dim, rising SFX
    ///   Phase 2 — IMPACT (at explosion moment):
    ///     Hitstop freeze (150-200ms) + white screen flash + "MELTDOWN" stamp
    ///     slams in with OutBack overshoot
    ///   Phase 3 — HOLD (while chain explosions play out):
    ///     Stamp stays visible, overlay stays dimmed
    ///   Phase 4 — FADE (after chain completes):
    ///     Stamp + overlay fade out
    ///
    /// Two-call API:
    ///   yield return TryMeltdownIntro(...)   // before explosion — build-up + stamp
    ///   ... explosion code runs ...
    ///   yield return TryMeltdownOutro()      // after explosion — fade
    ///
    /// Thresholds (priority order):
    ///   Last-turn chain depth 1+: "LAST-TURN THEFT"
    ///   Chain depth 3+: "AFTERSHOCK"
    ///   Chain depth 2+: "MELTDOWN"
    ///   Chain depth 1 with 2+ triggers: "CHAIN REACTION"
    /// </summary>
    public class MeltdownManager : MonoBehaviour
    {
        public static MeltdownManager Instance { get; private set; }

        // ── Thresholds (Inspector) ──────────────────────────────────────────────
        [Header("Thresholds")]
        [SerializeField, Tooltip("Minimum chain depth for any meltdown moment (1 = chains only, 0 = every detonation)")]
        public int minChainDepthForMeltdown = 2;
        [SerializeField, Tooltip("Minimum trigger count at chain depth 0")]
        public int minTriggersForChainReaction = 1;

        // ── Build-up timing ─────────────────────────────────────────────────────
        [Header("Build-Up Phase")]
        [SerializeField] private float buildUpDuration       = 0.35f;
        [SerializeField] private float shakeStartMagnitude   = 0.02f;
        [SerializeField] private float shakeEndMagnitude     = 0.18f;
        [SerializeField] private float shakeFrequency        = 25f;
        [SerializeField] private float vignetteTargetAlpha   = 0f; // disabled — was causing dark blob over the board

        // ── Impact timing ───────────────────────────────────────────────────────
        [Header("Impact Phase")]
        [SerializeField] private float hitStopDuration       = 0.18f;
        [SerializeField] private float flashPeakAlpha        = 0.55f;
        [SerializeField] private float flashFadeDuration     = 0.15f;
        [SerializeField] private float stampStartScale       = 2.2f;
        [SerializeField] private float stampPunchDuration     = 0.20f;
        [SerializeField] private float stampOvershoot        = 2.5f;

        // ── Fade timing ─────────────────────────────────────────────────────────
        [Header("Fade Phase")]
        [SerializeField] private float fadeOutDuration        = 0.30f;

        // ── Visuals ─────────────────────────────────────────────────────────────
        [Header("Visuals")]
        [SerializeField] private float titleFontSize          = 80f;

        // ── Internal ────────────────────────────────────────────────────────────
        private Canvas _canvas;
        private CanvasGroup _canvasGroup;
        private Image _overlay;         // dark vignette
        private Image _flashOverlay;    // white flash
        private TextMeshProUGUI _titleLabel;
        private RectTransform _titleRT;
        private bool _isPlaying;
        private bool _outroRunning;

        /// <summary>True between Intro and Outro — tells callers the stamp is visible.</summary>
        public bool IsActive => _isPlaying;

        // Title colors per type
        private static readonly Color COLOR_CHAIN_REACTION = new Color(1f, 0.75f, 0.2f, 1f);
        private static readonly Color COLOR_MELTDOWN       = new Color(1f, 0.35f, 0.15f, 1f);
        private static readonly Color COLOR_AFTERSHOCK     = new Color(1f, 0.95f, 0.4f, 1f);
        private static readonly Color COLOR_LAST_TURN      = new Color(0.9f, 0.2f, 0.9f, 1f);

        private static readonly Vector3 CAMERA_HOME = new Vector3(0f, 0f, -5f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Build();
        }

        private void Build()
        {
            // Canvas — overlay, above ChainCounter (sortingOrder 60)
            GameObject canvasGO = new GameObject("MeltdownCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 65;

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            _canvasGroup = canvasGO.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;
            _canvasGroup.blocksRaycasts = false;
            _canvasGroup.interactable = false;

            // Dark overlay — full screen vignette
            GameObject overlayGO = new GameObject("DarkenOverlay");
            overlayGO.transform.SetParent(canvasGO.transform, false);

            RectTransform overlayRT = overlayGO.AddComponent<RectTransform>();
            overlayRT.anchorMin = Vector2.zero;
            overlayRT.anchorMax = Vector2.one;
            overlayRT.offsetMin = Vector2.zero;
            overlayRT.offsetMax = Vector2.zero;

            _overlay = overlayGO.AddComponent<Image>();
            _overlay.color = new Color(0f, 0f, 0f, 0f);
            _overlay.raycastTarget = false;

            // White flash overlay — full screen
            GameObject flashGO = new GameObject("FlashOverlay");
            flashGO.transform.SetParent(canvasGO.transform, false);

            RectTransform flashRT = flashGO.AddComponent<RectTransform>();
            flashRT.anchorMin = Vector2.zero;
            flashRT.anchorMax = Vector2.one;
            flashRT.offsetMin = Vector2.zero;
            flashRT.offsetMax = Vector2.zero;

            _flashOverlay = flashGO.AddComponent<Image>();
            _flashOverlay.color = new Color(1f, 1f, 1f, 0f);
            _flashOverlay.raycastTarget = false;

            // Title label — centered
            GameObject labelGO = new GameObject("MeltdownTitle");
            labelGO.transform.SetParent(canvasGO.transform, false);

            _titleRT = labelGO.AddComponent<RectTransform>();
            _titleRT.anchorMin = new Vector2(0.05f, 0.4f);
            _titleRT.anchorMax = new Vector2(0.95f, 0.6f);
            _titleRT.pivot = new Vector2(0.5f, 0.5f);
            _titleRT.offsetMin = Vector2.zero;
            _titleRT.offsetMax = Vector2.zero;

            _titleLabel = labelGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset font = GameFont.GetDisplayTMP();
            if (font != null) _titleLabel.font = font;
            _titleLabel.fontSize = titleFontSize;
            _titleLabel.fontStyle = FontStyles.Normal;
            _titleLabel.alignment = TextAlignmentOptions.Center;
            _titleLabel.enableWordWrapping = false;
            _titleLabel.overflowMode = TextOverflowModes.Overflow;
            _titleLabel.enableAutoSizing = true;
            _titleLabel.fontSizeMin = 30f;
            _titleLabel.fontSizeMax = titleFontSize;
            _titleLabel.outlineWidth = 0f;
            _titleLabel.outlineColor = Color.clear;
            _titleLabel.color = Color.white;

            // Start hidden
            _titleRT.localScale = Vector3.zero;
        }

        // ═════════════════════════════════════════════════════════════════════════
        // PUBLIC API — Two-phase: Intro (before explosion) + Outro (after)
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check if a meltdown qualifies. If so, play the build-up + stamp intro.
        /// Returns Coroutine (yield it) or null if no meltdown.
        /// After this returns, the stamp is visible — run your explosion, then call TryMeltdownOutro().
        /// </summary>
        public Coroutine TryMeltdownIntro(int chainDepth, int triggerCount, int detonationBonus, bool isLastTurn)
        {
            // Phase 5 mechanic gate. Callers (HandManager x2, GameVisualBridge x1) all
            // funnel through here, so one gate covers the feature. TryTriggerMeltdown
            // (legacy) gated separately below since it has its own flow.
            if (!LevelController.IsMechanicAllowed("meltdown")) return null;

            if (_isPlaying) return null;

            string title = GetMeltdownTitle(chainDepth, triggerCount, detonationBonus, isLastTurn);
            if (title == null) return null;

            // Phase 11j tuning — entire intro (build-up shake + vignette dim
            // + shimmer particles + meltdown SFX + white flash + MELTDOWN
            // title stamp + hitstop) gated on a single toggle so Spencer can
            // A/B with the rest of the explosion stack off.
            if (!WordDropFX.FX_MeltdownIntroFlash)
            {
                Debug.Log("[FX] MeltdownIntroFlash: SKIPPED");
                return null;
            }
            Debug.Log("[FX] MeltdownIntroFlash: FIRED");

            // Phase 11g — duck the music for the meltdown so the explosion
            // payoff isn't fighting the BGM at full volume.
            GameAudio.Instance?.DuckMusicBriefly(0.30f);

            // Phase 11j-meltdown — the Magic Explosive Spell prefab now spawns
            // PER TILE during the actual explosion (WordDropFX.PlayExplosion
            // gates on MeltdownManager.IsActive). One central spawn here
            // looked like an isolated central blob next to the cluster; per-
            // tile reads as the whole cluster getting consumed.

            Color titleColor = GetTitleColor(title);
//             Debug.Log($"[Meltdown] Intro: \"{title}\" (depth={chainDepth}, triggers={triggerCount}, detBonus={detonationBonus}, lastTurn={isLastTurn})");
            return StartCoroutine(IntroCoroutine(title, titleColor, chainDepth));
        }

        /// <summary>
        /// Fade out the stamp + overlay after the chain has played out.
        /// Returns Coroutine or null if nothing is playing.
        /// </summary>
        public Coroutine TryMeltdownOutro()
        {
            if (!_isPlaying || _outroRunning) return null;
//             Debug.Log("[Meltdown] Outro: fading out.");
            return StartCoroutine(OutroCoroutine());
        }

        /// <summary>
        /// Legacy single-call API — plays full sequence (intro + hold + outro).
        /// Use TryMeltdownIntro/TryMeltdownOutro for the split flow instead.
        /// Gate applied here too for completeness.
        /// </summary>
        public Coroutine TryTriggerMeltdown(int chainDepth, int triggerCount, int detonationBonus, bool isLastTurn)
        {
            if (!LevelController.IsMechanicAllowed("meltdown")) return null;

            if (_isPlaying) return null;

            string title = GetMeltdownTitle(chainDepth, triggerCount, detonationBonus, isLastTurn);
            if (title == null) return null;

            Color titleColor = GetTitleColor(title);
//             Debug.Log($"[Meltdown] Full: \"{title}\" (depth={chainDepth}, triggers={triggerCount})");
            return StartCoroutine(FullSequenceCoroutine(title, titleColor, chainDepth));
        }

        // ═════════════════════════════════════════════════════════════════════════
        // TITLE SELECTION
        // ═════════════════════════════════════════════════════════════════════════

        private string GetMeltdownTitle(int chainDepth, int triggerCount, int detonationBonus, bool isLastTurn)
        {
            // Only trigger on chains — basic detonations play normal FX without the stamp
            if (chainDepth < minChainDepthForMeltdown)
                return null;

            // Priority order — most dramatic first
            if (chainDepth >= 6)
                return "AFTERSHOCK";
            if (chainDepth >= 4)
                return "MELTDOWN";
            if (chainDepth >= 2)
                return "CHAIN REACTION";

            return null; // depth 0-1 = normal detonation, no celebration
        }

        private Color GetTitleColor(string title)
        {
            switch (title)
            {
                case "DETONATION":      return new Color(1f, 0.85f, 0.3f, 1f);
                case "CHAIN REACTION":  return COLOR_CHAIN_REACTION;
                case "MELTDOWN":        return COLOR_MELTDOWN;
                case "AFTERSHOCK":      return COLOR_AFTERSHOCK;
                case "LAST-TURN THEFT": return COLOR_LAST_TURN;
                default:                return Color.white;
            }
        }

        // ═════════════════════════════════════════════════════════════════════════
        // PHASE 1 — INTRO: Build-up + Impact + Stamp
        // ═════════════════════════════════════════════════════════════════════════

        private IEnumerator IntroCoroutine(string title, Color titleColor, int chainDepth)
        {
            _isPlaying = true;
            _canvasGroup.alpha = 1f;

            // ── Build-up: escalating Perlin shake + vignette dim + rising SFX ───
            GameAudio.Instance?.PlayEventRising();
            HapticsManager.Medium();

            Camera cam = Camera.main;
            float elapsed = 0f;
            float seed = Random.value * 100f;

            float shimmerTimer = 0f;
            while (elapsed < buildUpDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float progress = Mathf.Clamp01(elapsed / buildUpDuration);
                float ramp = progress * progress; // quadratic — slow start, fast end

                // Escalating camera shake via Perlin noise
                if (cam != null)
                {
                    float mag = Mathf.Lerp(shakeStartMagnitude, shakeEndMagnitude, ramp);
                    float x = (Mathf.PerlinNoise(seed + elapsed * shakeFrequency, 0f) - 0.5f) * 2f * mag;
                    float y = (Mathf.PerlinNoise(0f, seed + elapsed * shakeFrequency) - 0.5f) * 2f * mag;
                    cam.transform.position = CAMERA_HOME + new Vector3(x, y, 0f);
                }

                // Vignette overlay dims in
                _overlay.color = new Color(0f, 0f, 0f, vignetteTargetAlpha * ramp);

                // Escalating shimmer — more particles as build-up intensifies
                shimmerTimer += Time.unscaledDeltaTime;
                float shimmerInterval = Mathf.Lerp(0.12f, 0.04f, ramp);
                if (shimmerTimer >= shimmerInterval)
                {
                    shimmerTimer = 0f;
                    int count = Mathf.RoundToInt(Mathf.Lerp(2f, 6f, ramp));
                    Vector3 shimmerPos = CAMERA_HOME + new Vector3(
                        Random.Range(-2f, 2f), Random.Range(-3f, 3f), 5f);
                    GameParticles.Instance?.PlayShimmerBurst(shimmerPos, count);
                }

                yield return null;
            }

            // Snap camera back before hitstop
            if (cam != null) cam.transform.position = CAMERA_HOME;

            // ── Impact: hitstop + white flash + stamp ───────────────────────────

            // Haptic — strong hit
            HapticsManager.Strong();

            // Meltdown SFX + haptic (the big hit)
            GameAudio.Instance?.PlayMeltdown();
            // Confetti gated by the same FX_Confetti toggle as WordDropFX so a
            // single switch covers both spawn paths.
            if (WordDropFX.FX_Confetti)
                GameParticles.Instance?.PlayMeltdown(Vector3.zero);
            HapticsManager.Meltdown();

            // White screen flash — instant to peak
            _flashOverlay.color = new Color(1f, 1f, 1f, flashPeakAlpha);

            // Stamp slam — starts at large scale, punches to 1x with OutBack
            _titleLabel.text = title;
            _titleLabel.color = titleColor;
            _titleLabel.outlineWidth = 0f;
            _titleLabel.outlineColor = Color.clear;
            _titleRT.localScale = Vector3.one * stampStartScale;

            // Hitstop — freeze time for dramatic weight
            float hitDur = hitStopDuration + Mathf.Min(chainDepth - 2, 3) * 0.03f;
            Time.timeScale = 0f;

            // Animate stamp + flash fade DURING hitstop (using unscaled time)
            float stampElapsed = 0f;
            float flashElapsed = 0f;
            bool stampDone = false;
            bool flashDone = false;

            while (!stampDone || !flashDone || stampElapsed < hitDur)
            {
                float dt = Time.unscaledDeltaTime;
                stampElapsed += dt;
                flashElapsed += dt;

                // Stamp scale animation
                if (!stampDone)
                {
                    float st = Mathf.Clamp01(stampElapsed / stampPunchDuration);
                    float eased = EaseOutBack(st, stampOvershoot);
                    float scale = Mathf.LerpUnclamped(stampStartScale, 1f, eased);
                    _titleRT.localScale = Vector3.one * scale;
                    if (st >= 1f) stampDone = true;
                }

                // Flash fade out
                if (!flashDone)
                {
                    float ft = Mathf.Clamp01(flashElapsed / flashFadeDuration);
                    _flashOverlay.color = new Color(1f, 1f, 1f, flashPeakAlpha * (1f - ft));
                    if (ft >= 1f) flashDone = true;
                }

                yield return null;
            }

            // Restore time
            Time.timeScale = 1f;

            // Ensure final state
            _titleRT.localScale = Vector3.one;
            _flashOverlay.color = new Color(1f, 1f, 1f, 0f);

            // ── Impact screen shake — quick punchy shake that decays ──
            if (cam != null)
            {
                float impactMag = 0.15f + Mathf.Min(chainDepth, 5) * 0.04f; // bigger chains = bigger shake
                float impactDur = 0.25f;
                float impactElapsed = 0f;
                float impactSeed = Random.Range(0f, 100f);

                while (impactElapsed < impactDur)
                {
                    impactElapsed += Time.deltaTime;
                    float decay = 1f - (impactElapsed / impactDur);
                    decay = decay * decay; // quadratic decay — fast falloff
                    float mag = impactMag * decay;
                    float ix = (Mathf.PerlinNoise(impactSeed + impactElapsed * 40f, 0f) - 0.5f) * 2f * mag;
                    float iy = (Mathf.PerlinNoise(0f, impactSeed + impactElapsed * 40f) - 0.5f) * 2f * mag;
                    cam.transform.position = CAMERA_HOME + new Vector3(ix, iy, 0f);
                    yield return null;
                }
                cam.transform.position = CAMERA_HOME;
            }

            // Stamp is now visible — caller runs explosion, then calls TryMeltdownOutro()
        }

        // ═════════════════════════════════════════════════════════════════════════
        // PHASE 2 — OUTRO: Fade stamp + overlay
        // ═════════════════════════════════════════════════════════════════════════

        private IEnumerator OutroCoroutine()
        {
            _outroRunning = true;
            float elapsed = 0f;
            float startOverlayAlpha = _overlay.color.a;

            while (elapsed < fadeOutDuration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / fadeOutDuration);

                _canvasGroup.alpha = 1f - t;
                _overlay.color = new Color(0f, 0f, 0f, startOverlayAlpha * (1f - t));

                yield return null;
            }

            // Reset everything
            _canvasGroup.alpha = 0f;
            _overlay.color = new Color(0f, 0f, 0f, 0f);
            _flashOverlay.color = new Color(1f, 1f, 1f, 0f);
            _titleRT.localScale = Vector3.zero;
            _isPlaying = false;
            _outroRunning = false;
        }

        // ═════════════════════════════════════════════════════════════════════════
        // LEGACY — Full sequence (for GameVisualBridge AI path)
        // ═════════════════════════════════════════════════════════════════════════

        private IEnumerator FullSequenceCoroutine(string title, Color titleColor, int chainDepth)
        {
            yield return StartCoroutine(IntroCoroutine(title, titleColor, chainDepth));
            // Brief hold
            yield return new WaitForSecondsRealtime(0.25f);
            yield return StartCoroutine(OutroCoroutine());
        }

        // ═════════════════════════════════════════════════════════════════════════
        // MATH
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>OutBack easing with configurable overshoot.</summary>
        private static float EaseOutBack(float t, float overshoot = 1.70158f)
        {
            float c3 = overshoot + 1f;
            return 1f + c3 * Mathf.Pow(t - 1f, 3f) + overshoot * Mathf.Pow(t - 1f, 2f);
        }
    }
}
