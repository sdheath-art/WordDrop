using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Centered mini-modal shown when SurvivalManager fires OnStageCleared. Pauses
    /// gameplay timers via SurvivalManager.SetOverlayPaused, blocks hand input via
    /// HandManager.IsInteractable, displays the cleared-stage snapshot + refill
    /// summary, dismisses on Continue.
    ///
    /// Show fires immediately when the stage-clear event arrives. Residual
    /// cascade visuals may still be animating; the dim overlay + frozen
    /// Survival timers + blocked HandManager input keep the celebration
    /// readable. An earlier version waited for MatchController.IsProcessing,
    /// but that gate was sticking past the visual cascade and the modal felt
    /// like it waited for "next drop".
    ///
    /// Canvas sortingOrder = 160 so it overlays LevelCompletedModal (150),
    /// MenuUI (100), and GameOverUI. Built once in Start (after singletons exist),
    /// hidden until event fires.
    ///
    /// v1: summary-mode only (refill display + Continue button).
    /// v1.5: when StageClearContext.Offers is populated, modal will swap to
    /// choice-mode (3 modifier cards + confirm). Same modal frame, different content.
    /// </summary>
    public class StageClearModal : MonoBehaviour
    {
        public static StageClearModal Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _panel;
        private Image _overlayImage;          // dim backdrop — faded in separately from panel
        private Text _titleText;
        private Text _scoreLabelText;
        private Text _scoreValueText;
        private Text _refillText;
        private GameObject _btnContinue;
        private CanvasGroup _btnContinueGroup; // for fading the button in/out independently

        // Tweens we may need to kill mid-flight (Dismiss / OnDestroy) so a rapid
        // open→close sequence doesn't leave runaway tweens writing to dead refs.
        private Sequence _entranceSequence;
        private Tweener _scoreCountTween;

        // Deferred-show queue — events captured during a resolution batch are
        // queued and shown in sequence. Single-slot would lose stage 1 if stage 2
        // fires before stage 1's IsProcessing drops (rare but real per Codex review).
        private readonly Queue<SurvivalManager.StageClearContext> _pendingQueue
            = new Queue<SurvivalManager.StageClearContext>();

        // Tracks whether the modal is currently presenting a stage. Used so
        // OnDestroy can know whether to roll back pause/input state.
        private bool _isPresenting;
        // Tracked rotation Z for the title's toss-in tween. Lives on the
        // class so DOTween.To getter/setter can share state safely across
        // the 3-phase rotation Sequence.
        private float _titleZRot;

        // Note: we intentionally do NOT manage HandManager.IsInteractable.
        // The dim overlay's raycastTarget=true blocks all taps from reaching
        // the board/hand while the modal is up, which is sufficient. Touching
        // HandManager.IsInteractable was racing with HandManager's drop coroutine
        // which legitimately drives that state, causing tiles to freeze after
        // back-to-back stage-clear modals (see the cascade-during-multi-clear bug).

        // Guard against double-dismiss (Continue clicked twice, etc.).
        private bool _isDismissing;

        // Tracks whether we've successfully subscribed to OnStageCleared. The
        // late-bind path in Update() only runs subscription when this is false,
        // avoiding per-frame delegate churn for the lifetime of the modal.
        private bool _isSubscribed;

        private static readonly Color PANEL_BG = new Color(0.05f, 0.04f, 0.12f, 0.80f);
        private static readonly Color CARD_BG  = new Color(0.16f, 0.14f, 0.30f, 0.98f);
        private static readonly Color TITLE    = new Color(1.00f, 0.84f, 0.25f, 1f);
        private static readonly Color SUBTITLE = new Color(0.85f, 0.80f, 0.92f, 1f);
        private static readonly Color REFILL_GREEN = new Color(0.45f, 1.00f, 0.55f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            // Build UI + subscribe in Awake so we never miss an early stage-clear
            // event. SurvivalManager.Instance is assigned in its own Awake; if our
            // Awake runs before its (script execution order is undefined), the
            // null-guard means we'll try again on first HandleStageCleared call
            // via the late-bind path below.
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);

            TrySubscribe();
        }

        private void TrySubscribe()
        {
            var sm = SurvivalManager.Instance;
            if (sm == null) return;

            // Verify by inspecting the actual delegate's invocation list — the
            // _isSubscribed flag is a fast-path optimization but it can lie
            // after SurvivalManager.StopSurvival nulls OnStageCleared (which it
            // does to clear stale subscribers between runs). Without this
            // check, after the first top-out + Play Again, the modal would
            // never receive stage-clear events again because _isSubscribed
            // stayed true while the actual subscription was severed.
            var inv = sm.OnStageCleared?.GetInvocationList();
            if (inv != null)
            {
                for (int i = 0; i < inv.Length; i++)
                {
                    if (inv[i].Target == (object)this)
                    {
                        _isSubscribed = true;
                        return; // already subscribed for real
                    }
                }
            }

            // Not in the invocation list — subscribe (defensive -= first in
            // case there's a dead reference to our handler from a prior life).
            sm.OnStageCleared -= HandleStageCleared;
            sm.OnStageCleared += HandleStageCleared;
            _isSubscribed = true;
        }

        private void OnDestroy()
        {
            // Kill all entrance + count-up + punch tweens before the GameObject
            // dies, so DOTween doesn't try to write to destroyed components on
            // its next update pass. Covers fades (Text DOColor, CanvasGroup
            // DOFade), score count-up, and score-punch DOPunchScale.
            KillAllEntranceTweens();

            // If the modal is destroyed while visible (scene unload, app quit
            // mid-celebration), roll back pause + music state so the game
            // doesn't get stuck overlay-paused or with victory music looping
            // past the celebration. HandManager.IsInteractable is NOT touched
            // here — see field-declaration comment.
            if (_isPresenting)
            {
                if (SurvivalManager.Instance != null)
                    SurvivalManager.Instance.SetOverlayPaused(false);
                // Music restore only if the Survival run is still live AND
                // we're not tearing down (Application.isPlaying guards Editor
                // exit; !IsGameOver guards post-run modal-destroy during
                // game-over UI takeover). IsSurvivalMode alone wasn't enough
                // — it's a static mode flag that doesn't clear during teardown.
                if (GameAudio.Instance != null
                    && Application.isPlaying
                    && SurvivalManager.IsSurvivalMode
                    && SurvivalManager.Instance != null
                    && !SurvivalManager.Instance.IsGameOver)
                {
                    GameAudio.Instance.PlaySurvivalMusic();
                }
                _isPresenting = false;
            }

            if (_isSubscribed && SurvivalManager.Instance != null)
                SurvivalManager.Instance.OnStageCleared -= HandleStageCleared;
            _isSubscribed = false;
            if (Instance == this) Instance = null;
        }

        // ── Event flow ──────────────────────────────────────────────────────────

        private void HandleStageCleared(SurvivalManager.StageClearContext ctx)
        {
            // Queue the payload — multiple stage-clears in a single resolution
            // batch (rare but real) all get shown in sequence as the player
            // dismisses each.
            _pendingQueue.Enqueue(ctx);
        }

        private void Update()
        {
            // Verify subscription EVERY frame. TrySubscribe inspects the actual
            // delegate invocation list; if our handler is in there, it's a
            // ~3-instruction no-op. If our subscription was severed (e.g. by
            // SurvivalManager.StopSurvival's null-out between runs), this
            // restores it within one frame so the next stage-clear event fires
            // the modal correctly.
            TrySubscribe();

            if (_isPresenting) return;          // already showing — wait for Continue
            if (_pendingQueue.Count == 0) return;

            // Show immediately on event. We previously waited for
            // MatchController.IsProcessing == false to avoid popping over a
            // cascade, but IsProcessing was sticking past the visual cascade
            // in practice (modal felt like it waited for "next drop").
            // Trade-off accepted: cascade visuals may still be animating when
            // modal pops. The dim overlay backgrounds them, SetOverlayPaused
            // freezes Survival timers, HandManager input is blocked — the
            // residual cascade just finishes behind the celebration.
            var ctx = _pendingQueue.Dequeue();
            Show(ctx);
        }

        // ── Show / Dismiss ──────────────────────────────────────────────────────

        /// <summary>Dev/test entry — show a fake stage-clear modal without a
        /// real stage clear. <paramref name="boss"/> = true picks a boss
        /// stage (5) so the whoosh_big SFX + difficulty visuals trigger.</summary>
        public void ShowForDebug(bool boss = false)
        {
            if (_isPresenting) return;
            int stage = boss ? 5 : 3;
            var ctx = new SurvivalManager.StageClearContext
            {
                ClearedStage  = stage,
                TargetScore   = 1462,
                StageScore    = 1462,
                MovesUsed     = 12,
                MovesBudget   = 16,
                RisesFired    = 4,
                Occupancy     = 0.55f,
                CoinsEarned   = boss ? 90 : 60,
                Offers        = null,
            };
            Show(ctx);
        }

        /// <summary>Dev/test entry point — programmatic dismiss for FXTestMenu.</summary>
        public void DismissForDebug()
        {
            if (!_isPresenting) return;
            Dismiss();
        }

        private void Show(SurvivalManager.StageClearContext ctx)
        {
            if (_canvas == null) return;
            _isDismissing = false;
            _isPresenting = true;

            // Modal entry SFX — fires the instant the panel appears. Mirrors
            // mobile-game convention (Royal Match / Candy Crush play a sting
            // on level-clear). Regular stages get the lighter sparkle whoosh,
            // boss stages (5, 10, 15…) get the dedicated "entry" sting that
            // also reads as a milestone moment.
            if (SurvivalManager.IsBossStage(ctx.ClearedStage))
                GameAudio.Instance?.PlayEntry();
            else
                GameAudio.Instance?.PlaySparkleWhoosh();

            // Refill is idempotent (Mathf.Max). Calling here guarantees it ran
            // even if subscription order didn't fire MatchController first.
            // If MatchController's handler already ran, this is a no-op.
            MatchController.StageClearRefillSummary summary = default;
            var mc = MatchController.Instance;
            if (mc != null)
                summary = mc.RefillStageClearResources(MatchController.PLAYER_HUMAN);

            // Populate text fields. Score value displays "0" initially so the
            // count-up animation can tally up from zero. Final value populates
            // when the count-up completes.
            if (_titleText != null) _titleText.text = $"STAGE {ctx.ClearedStage} CLEARED!";
            if (_scoreLabelText != null) _scoreLabelText.text = "Stage Score";
            if (_scoreValueText != null) _scoreValueText.text = "0";
            if (_refillText != null)
            {
                string refillLine = $"Edits {summary.RewritesAfter}  •  Swaps {summary.SwapsAfter}";
                if (ctx.CoinsEarned > 0) refillLine += $"  •  +{ctx.CoinsEarned}¢";
                _refillText.text = refillLine;
            }

            // Freeze gameplay timers. We do NOT touch HandManager.IsInteractable —
            // the dim overlay's raycastTarget=true already blocks all input
            // from reaching the board/hand, and touching IsInteractable here
            // would race with HandManager's drop coroutine (which legitimately
            // drives that state during cascade resolution).
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(true);

            // Swap to stage-clear music. PlayStageClearMusic picks a random
            // track from the victory pool (avoiding immediate-repeat across
            // consecutive stages) and plays once.
            GameAudio.Instance?.PlayStageClearMusic();

            // Kill any tweens left over from a prior open→close cycle BEFORE
            // resetting state — otherwise a still-running fade-in could
            // immediately overwrite the alpha=0 we just set.
            KillAllEntranceTweens();
            ResetEntranceState();

            _canvas.gameObject.SetActive(true);

            // Kick off the choreographed entrance.
            AnimateEntrance(ctx);
        }

        /// <summary>
        /// Kills every tween the entrance choreography could have started.
        /// The entrance Sequence only kills its own callbacks; the tweens those
        /// callbacks SPAWN (DOColor on Text, DOFade on CanvasGroup, DOPunchScale
        /// on the score transform) are independent and must be killed explicitly,
        /// or they'll keep writing to components after Dismiss/OnDestroy.
        /// Called from Show (defensive), Dismiss, and OnDestroy.
        /// </summary>
        private void KillAllEntranceTweens()
        {
            _entranceSequence?.Kill();
            _entranceSequence = null;
            _scoreCountTween?.Kill();
            _scoreCountTween = null;

            // Each child Component tween is owned by the component reference,
            // so DOKill on the component cancels its outstanding tweens.
            if (_overlayImage != null) _overlayImage.DOKill();
            if (_titleText != null) _titleText.DOKill();
            if (_scoreLabelText != null) _scoreLabelText.DOKill();
            if (_scoreValueText != null)
            {
                _scoreValueText.DOKill();
                // Score punch lives on the transform (DOPunchScale), separate
                // tween instance from the Text's DOColor.
                _scoreValueText.transform.DOKill();
            }
            if (_refillText != null) _refillText.DOKill();
            if (_btnContinueGroup != null) _btnContinueGroup.DOKill();

            // Transform-level tweens — panel PopIn (DOScale) and button idle
            // pulse + press squash (also DOScale on _btnContinue.transform).
            // PopOut's subtree-kill covers these during Dismiss but NOT during
            // OnDestroy, so they need explicit kills here too.
            if (_panel != null) _panel.transform.DOKill();
            if (_btnContinue != null) _btnContinue.transform.DOKill();
        }

        /// <summary>Reset all animated entrance properties to their pre-Show state.
        /// Called once at Show start so a rapid re-show after dismiss starts clean.
        /// MUST be called AFTER KillAllEntranceTweens — otherwise a surviving fade-in
        /// tween could overwrite the alpha we just reset.</summary>
        private void ResetEntranceState()
        {
            if (_overlayImage != null)
            {
                Color c = _overlayImage.color;
                _overlayImage.color = new Color(c.r, c.g, c.b, 0f);
            }
            // 2026-05-29: panel starts at full scale, anchored at REST.
            // UIAnimations.DropInWithBounce reads rest position and offsets
            // the panel above; old scale-zero start replaced by position-
            // based drop-bounce to unify with TopOutPanel + future modals.
            if (_panel != null)
            {
                _panel.transform.localScale = Vector3.one;
                var rt = _panel.transform as RectTransform;
                if (rt != null) rt.anchoredPosition = Vector2.zero;
            }
            SetTextAlpha(_titleText, 0f);
            SetTextAlpha(_scoreLabelText, 0f);
            SetTextAlpha(_scoreValueText, 0f);
            SetTextAlpha(_refillText, 0f);
            if (_btnContinueGroup != null) _btnContinueGroup.alpha = 0f;
            if (_btnContinue != null) _btnContinue.transform.localScale = Vector3.one;
        }

        /// <summary>
        /// Choreographed entrance sequence based on Playrix's "0.1s stagger" rule and
        /// observed Royal Match / Candy Crush staging. Backdrop fades first, then the
        /// panel pops, then children fade in cascade, then score count-up + punch,
        /// then the button pulse begins. Total entrance ~1.0s.
        /// </summary>
        private void AnimateEntrance(SurvivalManager.StageClearContext ctx)
        {
            _entranceSequence?.Kill();
            Sequence seq = DOTween.Sequence();

            // Phase 1: Backdrop fades to dim alpha (120ms).
            if (_overlayImage != null)
                seq.Append(_overlayImage.DOFade(PANEL_BG.a, 0.12f).SetEase(Ease.OutQuad));
            else
                seq.AppendInterval(0.12f);

            // Phase 2: Panel pops in with explicit two-phase overshoot+settle.
            // A single OutBack tween bakes the overshoot into the easing curve,
            // but the peak isn't visually distinct. Splitting it into "fast
            // grow past the target" then "settle back with bounce" makes the
            // overshoot READ as a deliberate beat the player sees, rather
            // than a curve that smooths over the bounce. Tuning:
            //   Phase A: 0 → 1.18 in 0.16s (OutCubic — punchy growth)
            //   Phase B: 1.18 → 1.0 in 0.14s (OutBack 2.0 — gentle bounce-settle)
            // Total ~0.30s, similar to PopIn's 0.25s. The DOKill clears any
            // prior tween on the transform before we sequence the new one.
            // 2026-05-29: canonical drop-with-bounce at 1.5× speed — same
            // shape as TopOutPanel but snappier (stage clear should not
            // make the player wait as long as a game-over panel).
            const float DROP_SPEED = 1.5f;
            if (_panel != null)
            {
                seq.AppendCallback(() =>
                {
                    if (_panel == null) return;
                    var rt = _panel.transform as RectTransform;
                    if (rt != null) UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED);
                });
                seq.AppendInterval(UIAnimations.DROP_TOTAL_DUR / DROP_SPEED + 0.05f);
            }

            // Phase 3: Children fade in with 80ms stagger (Playrix-style).
            // Title gets the Candy-Crush-style "object toss" — scale-overshoot
            // + rotation wobble + alpha fade all in parallel, ~0.40s.
            seq.AppendCallback(() => TossInTitle());
            seq.AppendInterval(0.08f);
            seq.AppendCallback(() => FadeInText(_scoreLabelText, 0.18f));
            seq.AppendInterval(0.06f);
            // Score value: fade-in + count-up start simultaneously (count-up
            // runs longer than fade so the number is visibly tallying).
            seq.AppendCallback(() =>
            {
                FadeInText(_scoreValueText, 0.12f);
                StartScoreCountUp(ctx.StageScore, 0.55f);
            });
            seq.AppendInterval(0.10f);
            seq.AppendCallback(() => FadeInText(_refillText, 0.18f));
            seq.AppendInterval(0.08f);
            // Continue button — fade then start the idle pulse.
            seq.AppendCallback(() =>
            {
                if (_btnContinueGroup != null) _btnContinueGroup.DOFade(1f, 0.18f);
            });
            seq.AppendInterval(0.22f); // wait for button fade + brief settle
            seq.AppendCallback(StartContinuePulse);

            _entranceSequence = seq;
        }

        private static void SetTextAlpha(Text t, float a)
        {
            if (t == null) return;
            Color c = t.color;
            t.color = new Color(c.r, c.g, c.b, a);
        }

        private static void FadeInText(Text t, float duration)
        {
            if (t == null) return;
            Color c = t.color;
            t.DOColor(new Color(c.r, c.g, c.b, 1f), duration).SetEase(Ease.OutQuad);
        }

        /// <summary>
        /// Candy-Crush-style "object toss" entrance for the stage clear title.
        /// Three concurrent tweens (scale + rotation + alpha) so the text feels
        /// like a physical card flopping onto the screen, not a flat fade. The
        /// rotation wobble is what sells the "alive" feel — without it the
        /// scale-overshoot alone reads as mechanical.
        /// </summary>
        private void TossInTitle()
        {
            if (_titleText == null) return;
            Transform t = _titleText.transform;
            t.DOKill();

            // Pre-toss state: tiny, almost flat (just a hair of tilt so the
            // rotation isn't a step-change when it starts moving). The bulk
            // of the rotation wobble happens during scale REBOUND + SETTLE
            // when text is full-size and the swing is actually visible.
            t.localScale    = Vector3.one * 0.1f;
            t.localRotation = Quaternion.Euler(0f, 0f, 8f);
            SetTextAlpha(_titleText, 0f);

            // 2026-05-29: switched OutElastic (multi-oscillation, jittery
            // settle) for OutBack (single overshoot + smooth deceleration).
            // OutBack's curve: passes target → small overshoot past → eases
            // smoothly down to settle. No vibration at the end.
            // Overshoot factor 3.0 = ~30% past target, BIG punch.
            // Duration 0.28s — most of the growth happens in the first
            // ~0.15s for that punchy "snap into view" feel.
            const float TOSS_DURATION = 0.28f;
            const float OVERSHOOT     = 3.0f;

            t.DOScale(1.0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            // Rotation sweeps from +8° past 0° to -X° then smoothly back to 0°.
            DG.Tweening.Core.DOGetter<float> getZ = () => _titleZRot;
            DG.Tweening.Core.DOSetter<float> setZ = (float z) =>
            {
                _titleZRot = z;
                t.localRotation = Quaternion.Euler(0f, 0f, z);
            };
            _titleZRot = 8f;
            DOTween.To(getZ, setZ, 0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            // Alpha fades in quickly so the punch lands at full visibility.
            Color c = _titleText.color;
            _titleText.DOColor(new Color(c.r, c.g, c.b, 1f), 0.12f).SetEase(Ease.OutQuad);
        }

        /// <summary>
        /// Score count-up animation. Tallies from 0 → ctx.StageScore over the given
        /// duration with OutQuad easing (snappy start, gentle settle). On complete,
        /// punches the score value transform for a "look how much you earned" beat.
        /// </summary>
        private void StartScoreCountUp(int target, float duration)
        {
            if (_scoreValueText == null) return;
            _scoreCountTween?.Kill();
            int current = 0;
            _scoreCountTween = DOTween.To(() => current, v =>
            {
                current = v;
                if (_scoreValueText != null) _scoreValueText.text = v.ToString("N0");
            }, target, duration)
                .SetEase(Ease.OutQuad)
                .OnComplete(() =>
                {
                    if (_scoreValueText != null)
                    {
                        _scoreValueText.text = target.ToString("N0");
                        // Score punch — reward beat after tally completes.
                        // DOPunchScale: cumulative punch of 15% over 0.20s with
                        // 6 vibrato bounces. Read-as: "the number lands."
                        _scoreValueText.transform.DOPunchScale(
                            Vector3.one * 0.15f, 0.20f, 6, 0.6f);
                    }
                    _scoreCountTween = null;
                });
        }

        // ── Button animation (matches GameOverUI PLAY AGAIN feel-pass) ──────────

        private void StartContinuePulse()
        {
            if (_btnContinue == null) return;
            Transform t = _btnContinue.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            // 1.0 → 1.07 → 1.0 over 1.4s yoyo. Same numbers as
            // GameOverUI.StartPlayAgainPulse — keep button-feel consistent
            // across every "tap to continue" surface in the game.
            t.DOScale(1.07f, 0.7f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private void StopContinuePulse()
        {
            if (_btnContinue == null) return;
            _btnContinue.transform.DOKill();
            _btnContinue.transform.localScale = Vector3.one;
        }

        private void PlayContinuePressSquash()
        {
            if (_btnContinue == null) return;
            Transform t = _btnContinue.transform;
            t.DOKill();
            // Press-squash → pop → settle. Matches GameOverUI line 567-569
            // (Candy Crush cartoon-button feel-pass tuning).
            Sequence press = DOTween.Sequence();
            press.Append(t.DOScale(0.92f, 0.06f).SetEase(Ease.OutQuad));
            press.Append(t.DOScale(1.06f, 0.10f).SetEase(Ease.OutBack, 4f));
            press.Append(t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad));
        }

        private void OnContinuePressed()
        {
            // Stop the idle pulse before dismiss so it doesn't fight over scale.
            // The press-squash that used to run here was killed mid-flight by
            // PopOut's descendant DOKill — leaving the button at a frozen
            // intermediate scale during the parent shrink (read as a "frame drop"
            // even though it was just a held interpolation). Drop the press
            // squash on the dismiss path; modal is going away anyway.
            StopContinuePulse();
            // Release half of the split multi-pop — paired with the press
            // half fired from the EventTrigger.PointerDown listener wired
            // up in the Continue-button construction block.
            GameAudio.Instance?.PlayMultiPopRelease();
            Dismiss();
        }

        private void Dismiss()
        {
            if (_isDismissing) return;
            _isDismissing = true;

            // Kill any in-flight entrance choreography — if the player taps
            // Continue mid-entrance (e.g. impatient rapid-tap), abort the
            // fade-in cascade AND every tween its callbacks spawned so we
            // don't write to components during PopOut.
            KillAllEntranceTweens();

            // Pause + music restore are deferred to the PopOut callback so the
            // overlay-pause stays ON across the dismiss animation (no slip
            // window where Survival timers re-advance / HandManager input
            // could fire). The callback also checks whether the queue still
            // has another stage-clear pending — if so, it skips the restore
            // entirely and Show() of the next modal keeps the pause + swaps
            // music back to victory.
            if (_panel != null)
            {
                // 2026-05-29: dismiss flies the panel UP off-screen
                // (UIAnimations.ExitUp) — matches the way it came in from
                // above, so the modal "leaves the way it arrived." Was
                // ExitDown briefly; Spencer asked for it to retreat upward
                // instead.
                _panel.transform.DOKill();
                var rt = _panel.transform as RectTransform;
                if (rt != null)
                    UIAnimations.ExitUp(rt, FinalizeDismiss, speedMult: 1.5f);
                else
                    FinalizeDismiss();
            }
            else if (_canvas != null)
            {
                _canvas.gameObject.SetActive(false);
                FinalizeDismiss();
            }
            else
            {
                FinalizeDismiss();
            }
        }

        private void FinalizeDismiss()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _isPresenting = false;

            // If another stage-clear is queued, leave overlay pause + victory
            // music ON — the next Update tick will dequeue it and the new
            // Show() will replace the music + keep the pause active. This
            // closes the slip window where back-to-back modals could leak
            // a frame of unpaused gameplay.
            if (_pendingQueue.Count > 0) return;

            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(false);

            // Resume gameplay music. PlaySurvivalMusic picks a fresh random
            // track from the pool (won't restart the same victory clip).
            GameAudio.Instance?.PlaySurvivalMusic();
        }

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("StageClearCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 160; // above LevelCompletedModal (150)

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen dim overlay. Blocks raycasts so taps don't reach the board.
            // Alpha is animated separately from the panel scale (Playrix-style
            // backdrop-fade-before-panel-pop staging).
            GameObject overlay = new GameObject("Overlay");
            overlay.transform.SetParent(canvasGO.transform, false);
            RectTransform oRT = overlay.AddComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero;
            oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero;
            oRT.offsetMax = Vector2.zero;
            _overlayImage = overlay.AddComponent<Image>();
            _overlayImage.color = PANEL_BG;
            _overlayImage.raycastTarget = true;

            // Centered card — Candy Crush level-complete proportions
            // (~88% screen width, ~58% screen height). Larger than the previous
            // mini-modal sizing so the celebration has room to breathe and
            // there's space for v1.5 modifier-pick cards without a re-layout.
            _panel = new GameObject("Card");
            _panel.transform.SetParent(canvasGO.transform, false);
            RectTransform pRT = _panel.AddComponent<RectTransform>();
            pRT.anchorMin = new Vector2(0.06f, 0.21f);
            pRT.anchorMax = new Vector2(0.94f, 0.79f);
            pRT.offsetMin = Vector2.zero;
            pRT.offsetMax = Vector2.zero;
            Image pImg = _panel.AddComponent<Image>();
            pImg.color = CARD_BG;

            // Title — top section of the larger panel
            _titleText = CreateLabel(_panel.transform, "Title",
                new Vector2(0.04f, 0.78f), new Vector2(0.96f, 0.93f),
                "STAGE 1 CLEARED!", 40, TITLE);
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Score label
            _scoreLabelText = CreateLabel(_panel.transform, "ScoreLabel",
                new Vector2(0.10f, 0.62f), new Vector2(0.90f, 0.70f),
                "Stage Score", 20, SUBTITLE);

            // Score value — hero element, bigger font fills the new space
            _scoreValueText = CreateLabel(_panel.transform, "ScoreValue",
                new Vector2(0.10f, 0.44f), new Vector2(0.90f, 0.62f),
                "0", 56, Color.white);
            _scoreValueText.fontStyle = FontStyle.Bold;

            // Refill summary
            _refillText = CreateLabel(_panel.transform, "Refill",
                new Vector2(0.06f, 0.30f), new Vector2(0.94f, 0.40f),
                "Edits 3  •  Swaps 2", 22, REFILL_GREEN);

            // Continue button — bigger, sits in bottom 25% of panel
            int childCountBefore = _panel.transform.childCount;
            MenuUI.CreateButton(_panel.transform, "BtnContinue",
                new Vector2(0.18f, 0.08f), new Vector2(0.82f, 0.22f),
                "CONTINUE", new Color(0.25f, 0.75f, 0.40f, 1f), Color.white, 28,
                OnContinuePressed);
            if (_panel.transform.childCount > childCountBefore)
            {
                _btnContinue = _panel.transform.GetChild(_panel.transform.childCount - 1).gameObject;
                // CanvasGroup lets us fade the whole button (background + text)
                // in one shot during the staggered entrance.
                _btnContinueGroup = _btnContinue.GetComponent<CanvasGroup>();
                if (_btnContinueGroup == null)
                    _btnContinueGroup = _btnContinue.AddComponent<CanvasGroup>();

                // Two-stage click feel: the floraphonic multi-pop is split
                // into press + release halves. PointerDown plays the first
                // pop, the existing onClick → OnContinuePressed plays the
                // second. EventTrigger handles PointerDown alongside Button's
                // own onClick — both fire correctly because Button only
                // consumes the trigger to drive its onClick state.
                var trigger = _btnContinue.GetComponent<EventTrigger>();
                if (trigger == null) trigger = _btnContinue.AddComponent<EventTrigger>();
                var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
                trigger.triggers.Add(pdEntry);
            }
        }

        private static Text CreateLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            Text t = go.AddComponent<Text>();
            t.font = MenuUI.GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }
}
