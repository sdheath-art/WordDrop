using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Candy-Crush-style "Top Out!" announcement panel. Drops in from above with
    /// an OutBack bounce, dwells ~1.5s, exits downward with InBack anticipation.
    /// Auto-dismisses (no button interaction). Used as the transition moment
    /// between TriggerTopOut and the GameOver UI / best-combo replay.
    ///
    /// Implements the spec captured from Candy Crush gameplay at 60fps:
    ///   Drop in:  330ms OutBack 1.6
    ///   Dwell:    1.5s
    ///   Exit:     350ms InBack 1.4
    ///   Dim:      0.65 alpha black, fade in 250ms / fade out 350ms
    ///
    /// Canvas sortingOrder = 165 — above StageClearModal (160), below any future
    /// GameOver UI that might want to render on top.
    /// </summary>
    public class TopOutPanel : MonoBehaviour
    {
        public static TopOutPanel Instance { get; private set; }

        // Tuning knobs.
        //
        // Drop motion = SINGLE elastic curve from above-screen to the rest
        // position. OutElastic produces a continuous springy arc — the panel
        // falls past the rest position, bounces back, returns, and settles
        // smoothly. The damped oscillation IS the elastic feel.
        //
        // Tuning — explicit 3-phase damped bounce (Candy Crush "out of moves"
        // panel behavior). Switched 2026-05-29 from OutElastic because that
        // ease over-amplified when tweening a large distance (1200 PSD
        // start), producing a 400+ PSD plunge. The Sequence below has hand-
        // controlled dip/rebound magnitudes so the bounce reads as "panel
        // lands on a surface with momentum" not "panel falls off a cliff."
        //
        //   DIP_BELOW    = how far past center the panel dips on first land
        //   REBOUND_UP   = how far above center it rebounds before settling
        //   PHASE_*      = duration of each motion phase
        // 2026-05-29: bounce magnitudes dialed down further per Spencer.
        //   DIP_BELOW  60 → 40
        //   REBOUND_UP 22 → 14
        private const float DIP_BELOW         = 40f;
        private const float REBOUND_UP        = 14f;
        // 2026-05-29: phase durations cut ~33% so the whole bounce reads
        // snappier (total 0.65→0.43s). Same shape, just faster.
        private const float PHASE_DROP_DUR    = 0.20f;  // above → dip-below
        private const float PHASE_REBOUND_DUR = 0.12f;  // dip-below → rebound-up
        private const float PHASE_SETTLE_DUR  = 0.11f;  // rebound-up → center
        // 2026-05-29: settle position raised from canvas center (0) to +21
        // in 540×960 ref space. Spencer's PSD spec puts the panel TOP at
        // Y=1038 PSD on the 1179×2556 canvas, meaning panel center is 55
        // PSD ABOVE canvas center. 55 PSD × (960/2556) ≈ 21 ref px.
        private const float REST_Y            = 21f;

        private const float DWELL_SECONDS       = 1.5f;
        private const float EXIT_DURATION       = 0.35f;
        private const float EXIT_OVERSHOOT      = 1.4f;


        private Canvas _canvas;
        private RectTransform _panel;
        private TextMeshProUGUI _titleText;

        private Vector2 _settlePos;  // anchoredPosition at rest (visually centered)
        private Vector2 _abovePos;   // off-screen above
        private Vector2 _belowPos;   // off-screen below

        private Sequence _sequence;
        private bool _isShowing;
        private Action _pendingCompletion; // composed callback fired when the panel finishes

        // 2026-05-29: re-skinned for Glimbloom — mystical-forest purple +
        // warm gold accents. Mirrors SettingsModal / BoosterHUDSlot bench
        // colors. Was pink (Candy Crush style) which didn't fit the theme.
        private static readonly Color PANEL_BG      = new Color(0.20f, 0.13f, 0.32f, 0.98f);  // deep mystical purple
        private static readonly Color PANEL_STRIPE  = new Color(0.62f, 0.45f, 0.85f, 1f);    // brighter purple stripe
        private static readonly Color TITLE_COLOR   = new Color(1.00f, 0.84f, 0.42f, 1f);    // warm gold (header)
        private static readonly Color SUBTITLE_COL  = new Color(0.95f, 0.92f, 0.86f, 0.92f); // warm cream

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // Build UI in Awake (matches StageClearModal pattern) so a cold-call
            // to Show() during a fast top-out won't fall through to the
            // null-canvas escape path and skip the panel.
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            _sequence?.Kill();
            _sequence = null;
            if (_panel != null) _panel.DOKill();

            // Fire any pending callbacks BEFORE clearing so the deferred
            // GameOver transition still runs if the panel is destroyed
            // mid-sequence (scene unload, etc.). Without this, the
            // SurvivalManager's TriggerTopOut → FinalizeGameOver flow would
            // be lost and the game-over UI would never appear.
            Action toFire = _pendingCompletion;
            _pendingCompletion = null;
            try { toFire?.Invoke(); }
            catch (Exception e) { Debug.LogError($"[TopOutPanel] OnDestroy pending-completion threw: {e}"); }

            if (Instance == this) Instance = null;
        }

        /// <summary>
        /// Plays the drop-in → dwell → exit sequence. Fires onComplete when
        /// the panel has fully exited the screen. Caller is responsible for
        /// the post-panel transition (game over UI, best-combo replay, etc).
        /// </summary>
        public void Show(Action onComplete = null)
        {
            if (_canvas == null)
            {
                // UI not built yet (Awake hasn't run). Fall through so the
                // caller's flow still continues, even though the panel won't
                // be presented.
                onComplete?.Invoke();
                return;
            }
            if (_isShowing)
            {
                // Already in flight — chain this callback so it fires alongside
                // whatever was queued before. Sequence.OnComplete REPLACES the
                // handler, so we maintain our own composed delegate that all
                // pending callbacks get added to. The sequence's OnComplete
                // fires this composed delegate once when the panel finishes.
                Action existing = _pendingCompletion;
                _pendingCompletion = () =>
                {
                    try { existing?.Invoke(); } catch (Exception e) { Debug.LogError($"[TopOutPanel] chained-existing onComplete threw: {e}"); }
                    try { onComplete?.Invoke(); } catch (Exception e) { Debug.LogError($"[TopOutPanel] chained-new onComplete threw: {e}"); }
                };
                return;
            }
            _isShowing = true;
            _pendingCompletion = onComplete;

            // ORDER MATTERS: kill any lingering tweens FIRST, then reset all
            // transform state, THEN create the new sequence. Otherwise a stale
            // tween could overwrite the reset position between the assignment
            // and the new sequence's first frame, causing OutElastic to start
            // from an inconsistent position and visually overshoot by different
            // amounts each play (bug observed 2026-05-21).
            _panel.DOKill();
            _sequence?.Kill();

            // Reset all transform state to a known-clean baseline. Previous
            // tweens may have touched scale/rotation; force them back to
            // identity so the entrance starts pristine.
            _panel.localScale    = Vector3.one;
            _panel.localRotation = Quaternion.identity;

            // Use FIXED off-screen offsets (not parent.rect.height — that
            // value can return 0 or stale on the first frame after Play mode
            // starts, before canvas auto-layout has run). 1200 reference
            // units is comfortably above any phone screen height in the
            // 540×960 reference space.
            const float OFFSCREEN_OFFSET = 1200f;
            _settlePos = new Vector2(0f, REST_Y);
            _abovePos  = new Vector2(0f,  OFFSCREEN_OFFSET);
            _belowPos  = new Vector2(0f, -OFFSCREEN_OFFSET);

            // Start panel at rest so UIAnimations.DropInWithBounce can read
            // the rest position and offset above. Helper sets the actual
            // start (above) position synchronously when called.
            _panel.anchoredPosition = _settlePos;
            _canvas.gameObject.SetActive(true);

            _sequence = DOTween.Sequence();

            // Phase 1 — DROP / REBOUND / SETTLE: canonical drop-with-bounce
            // at 1.4× speed (drops ~0.30s, matching AAA spec — Royal Match /
            // Candy Crush / King-library standard 0.25-0.30s for modal entry).
            const float TOPOUT_SPEED = 1.4f;
            float dipDur = UIAnimations.DROP_PHASE_DROP_DUR / TOPOUT_SPEED;
            _sequence.Append(UIAnimations.DropInWithBounce(_panel, speedMult: TOPOUT_SPEED));
            _sequence.InsertCallback(dipDur, () => GameAudio.Instance?.PlayWood2());

            // Phase 2 — DWELL static at the rest position.
            _sequence.AppendInterval(DWELL_SECONDS);

            // Phase 3 — EXIT downward at 1.4× speed (~0.25s).
            _sequence.Append(UIAnimations.ExitDown(_panel, speedMult: TOPOUT_SPEED));

            _sequence.OnComplete(() =>
            {
                if (_canvas != null) _canvas.gameObject.SetActive(false);
                _isShowing = false;
                _sequence = null;
                // Fire the composed callback (includes the original onComplete
                // plus any chained ones from re-entrant Show() calls). Capture
                // + null the field first so a callback that re-invokes Show
                // can't re-trigger this same chain.
                Action toFire = _pendingCompletion;
                _pendingCompletion = null;
                try { toFire?.Invoke(); } catch (Exception e) { Debug.LogError($"[TopOutPanel] onComplete threw: {e}"); }
            });
        }

        /// <summary>
        /// Convenience for callers that don't need a completion callback.
        /// </summary>
        public void Show() => Show(null);

        /// <summary>Populate the title text. Call before Show.</summary>
        public void SetText(string title)
        {
            if (_titleText != null) _titleText.text = title;
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("TopOutCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 165; // above StageClearModal (160)

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Screen tinting / dim overlay removed per Spencer 2026-05-21 —
            // the board behind the panel stays at full brightness during
            // the top-out announcement.

            // 2026-05-29: panel extends edge-to-edge horizontally — the pink
            // banner bleeds off both left and right of the camera. Anchors
            // collapse vertically at canvas Y=0.5 so height is set by the
            // offsetMin/Max gap (139 ref px = ~370 PSD per Spencer spec).
            GameObject panelGO = new GameObject("Panel");
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO.AddComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0f, 0.5f);
            _panel.anchorMax = new Vector2(1f, 0.5f);
            _panel.pivot     = new Vector2(0.5f, 0.5f);
            // Height 143 ref px (= 380 PSD) centered on Y anchor; horizontal
            // stretch via the anchors (offsetMin/Max.x = 0).
            _panel.offsetMin = new Vector2(0f, -71.5f);
            _panel.offsetMax = new Vector2(0f,  71.5f);
            _panel.anchoredPosition = Vector2.zero;
            Image panelImg = panelGO.AddComponent<Image>();
            panelImg.color = PANEL_BG;
            panelImg.raycastTarget = true; // absorb taps so they don't reach the board behind

            // Top + bottom stripes (Candy-Crush striped header/footer aesthetic).
            CreateStripe(_panel, "TopStripe", 0.86f, 1.0f);
            CreateStripe(_panel, "BotStripe", 0.0f,  0.14f);

            // Title — Cartoon SDF (display font from GameFont.GetDisplayTMP).
            // Per feel-pass lessons, Cartoon font requires FontStyles.Normal —
            // using Bold on this thick display font causes dark SDF fringing
            // and bloated rendering (lesson learned 2026-04 — see
            // feedback_lessons_learned.md "Typography / TextMeshPro").
            _titleText = CreateTMPLabel(_panel, "Title",
                new Vector2(0.05f, 0.18f), new Vector2(0.95f, 0.82f),
                "TOP OUT!", 60, TITLE_COLOR);
            _titleText.fontStyle = FontStyles.Normal;
            _titleText.enableWordWrapping = false;
            _titleText.overflowMode = TextOverflowModes.Overflow;
        }

        private static void CreateStripe(RectTransform parent, string name, float yMin, float yMax)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, yMin);
            rt.anchorMax = new Vector2(1f, yMax);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Image img = go.AddComponent<Image>();
            img.color = PANEL_STRIPE;
            img.raycastTarget = false;
        }

        private static TextMeshProUGUI CreateTMPLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            TextMeshProUGUI t = go.AddComponent<TextMeshProUGUI>();
            t.font = GameFont.GetDisplayTMP(); // Cartoon SDF (display font)
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            return t;
        }
    }
}
