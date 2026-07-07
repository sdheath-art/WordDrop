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

        /// <summary>True while the modal canvas is currently active on screen.
        /// Subscribers (e.g. HintManager) use this to suppress background
        /// animations that would otherwise compete with the modal.</summary>
        public bool IsShowing => _canvas != null && _canvas.gameObject.activeSelf;

        private Canvas _canvas;
        private GameObject _panel;
        private Image _overlayImage;          // dim backdrop — faded in separately from panel
        private Text _titleText;
        private Text _scoreLabelText;
        private Text _scoreValueText;
        private Text _refillText; // legacy — retained for compile-compat in animation refs; chip layout below supersedes it
        private GameObject _refillRow;
        private CanvasGroup _refillRowGroup;
        private GameObject _editChip;
        private Image _editIcon;
        private Text _editLabel;
        private GameObject _swapChip;
        private Image _swapIcon;
        private Text _swapLabel;
        private GameObject _coinChip;
        private Text _coinLabel;
        private GameObject _btnContinue;
        private CanvasGroup _btnContinueGroup; // for fading the button in/out independently

        // Royal-Match-style celebration: a single big golden star drops into the center where the
        // score used to be, over a golden glow, with golden particles + the personal_best SFX. 2026-06-23.
        private Image _heroStarImage;
        private RectTransform _heroStarRect;
        private Image _heroGlowImage;
        private RectTransform _heroGlowRect;
        private Coroutine _starDropCoroutine;
        private static readonly Color HERO_STAR_GOLD = new Color(1.00f, 0.84f, 0.25f, 1f);
        private static readonly Color HERO_GLOW_GOLD = new Color(1.00f, 0.82f, 0.22f, 0.80f);

        // Tweens we may need to kill mid-flight (Dismiss / OnDestroy) so a rapid
        // open→close sequence doesn't leave runaway tweens writing to dead refs.
        private Sequence _entranceSequence;
        private Tweener _scoreCountTween;
        private Tweener _coinCountTween;
        private int _coinCountTarget; // captured at entrance time so the count-up tween knows its end value

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

        // 2026-05-30: post-explosion beat. Track the unscaled time when
        // explosions last cleared so we can wait a moment before presenting
        // — gives the player a beat to see the last explosion's tail before
        // the modal flies in.
        private float _explosionsClearedAt = -1f;
        private const float POST_EXPLOSION_BEAT = 0.45f;

        // Note: we intentionally do NOT manage HandManager.IsInteractable.
        // The dim overlay's raycastTarget=true blocks all taps from reaching
        // the board/hand while the modal is up, which is sufficient. Touching
        // HandManager.IsInteractable was racing with HandManager's drop coroutine
        // which legitimately drives that state, causing tiles to freeze after
        // back-to-back stage-clear modals (see the cascade-during-multi-clear bug).

        // Guard against double-dismiss (Continue clicked twice, etc.).
        private bool _isDismissing;
        private int _clearedStage; // the stage this modal is celebrating — used to trigger the unlock reward

        // Tracks whether we've successfully subscribed to OnStageCleared. The
        // late-bind path in Update() only runs subscription when this is false,
        // avoiding per-frame delegate churn for the lifetime of the modal.
        private bool _isSubscribed;

        private static readonly Color PANEL_BG = new Color(0.05f, 0.04f, 0.12f, 0.80f);
        private static readonly Color CARD_BG  = new Color(0.99f, 0.95f, 0.86f, 0.98f); // TEMP candy-unification
        private static readonly Color TITLE    = new Color(0.82f, 0.28f, 0.46f, 1f);
        private static readonly Color SUBTITLE = new Color(0.32f, 0.24f, 0.30f, 1f);
        private static readonly Color REFILL_GREEN = new Color(0.20f, 0.65f, 0.35f, 1f);

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

        // Set the instant the swap-unlock level clears. HandleStageCleared fires on OnStageCleared — BEFORE
        // the next-level InstallLevel runs (the modal itself shows later, after explosions settle), so this is
        // the reliable signal for deferring the L5 objective intro behind the Unlock modal. Cleared by
        // UnlockModal.OnClaim. 2026-07-06 Spencer.
        public static bool UnlockRewardPending;

        private void HandleStageCleared(SurvivalManager.StageClearContext ctx)
        {
            // Queue the payload — multiple stage-clears in a single resolution
            // batch (rare but real) all get shown in sequence as the player
            // dismisses each.
            _pendingQueue.Enqueue(ctx);
            if (ctx.ClearedStage == TutorialLocks.EDIT_UNLOCK_LEVEL - 1)
                UnlockRewardPending = true;
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

            // 2026-05-30: wait for active explosion FX to finish + a short beat.
            // 2026-06-01: ALSO gate on MatchController.IsProcessing so the timer
            // doesn't start ticking during the brief gap between cascade
            // explosions (HasActiveExplosions drops to 0 between explosion N
            // ending and explosion N+1 starting while gravity animates the
            // chain step). Spencer hit: modal flew in over a cascade explosion
            // because the gap fell within POST_EXPLOSION_BEAT.
            //
            // IsProcessing stays TRUE for the entire turn-resolution loop
            // (player drop → score → trigger → explode → gravity → detect-words
            // → repeat → final FinalizeDrop), so it correctly says "more
            // explosions may still come." Once it drops to false AND
            // HasActiveExplosions is also 0, the chain is truly done.
            //
            // HasActiveExplosions is kept as a secondary gate so the BEAT
            // covers the last explosion's visual tail even after IsProcessing
            // already cleared.
            bool stillResolving = MatchController.Instance != null
                                  && MatchController.Instance.IsProcessing;
            if (stillResolving || WordDropFX.HasActiveExplosions)
            {
                _explosionsClearedAt = -1f; // reset; will start timer when explosions truly settle
                return;
            }
            // 2026-06-04 Spencer: also hold for an in-flight wild ARRIVAL pop so the
            // modal doesn't cut off the wild's entry animation.
            if (HandManager.Instance != null && HandManager.Instance.IsWildEntryAnimating)
            {
                _explosionsClearedAt = -1f; // restart the settle beat after the wild lands
                return;
            }
            if (_explosionsClearedAt < 0f)
            {
                _explosionsClearedAt = Time.unscaledTime; // first frame with everything settled
                return; // wait until next frame to start checking the beat
            }
            if (Time.unscaledTime - _explosionsClearedAt < POST_EXPLOSION_BEAT) return;

            var ctx = _pendingQueue.Dequeue();
            _explosionsClearedAt = -1f; // reset for the NEXT stage's wait
            // [StageModalTrace] TEMP — pair with "[Stage] CLEARED stage N". If a stage N
            // shows here twice, the event was enqueued twice (SurvivalManager); if it shows
            // once per "[Stage] CLEARED" the modal isn't the doubler. queueRemaining helps
            // spot a stale leftover enqueue.
            Debug.Log($"[StageModalTrace] SHOW stage {ctx.ClearedStage} (queueRemaining={_pendingQueue.Count})");
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

            // 2026-05-30: kill any active background animations so the modal
            // is the focused element. Currently this means the hint-manager
            // tile-hop sequences (CC-style "play this word" hops). Other
            // pulses (primed glow) stay — they're behind the dim overlay and
            // SetOverlayPaused freezes their gameplay impact anyway.
            HintManager.Instance?.ClearVisuals();

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
            _clearedStage = ctx.ClearedStage;
            if (_titleText != null) _titleText.text = $"LEVEL {ctx.ClearedStage} CLEARED!";
            if (_scoreLabelText != null) _scoreLabelText.text = "Level Score";
            if (_scoreValueText != null) _scoreValueText.text = "0";
            // Populate refill row chips with this stage's deltas — how many
            // edits/swaps were actually added back by the refill (i.e. the
            // gap between Before and the refill cap). If the player was
            // already at or above the cap, the refill was a no-op and the
            // chip is hidden. Coins always shows the amount earned this stage.
            // Per Spencer 2026-06-01: "if it is refilling it... tell you how
            // much it has added back."
            int editsDelta = Mathf.Max(0, summary.RewritesAfter - summary.RewritesBefore);
            int swapsDelta = Mathf.Max(0, summary.SwapsAfter   - summary.SwapsBefore);
            int coinsDelta = Mathf.Max(0, ctx.CoinsEarned);

            if (_editChip != null) _editChip.SetActive(editsDelta > 0);
            if (_swapChip != null) _swapChip.SetActive(swapsDelta > 0);
            if (_coinChip != null) _coinChip.SetActive(coinsDelta > 0);

            if (_editLabel != null) _editLabel.text = $"+{editsDelta}";
            if (_swapLabel != null) _swapLabel.text = $"+{swapsDelta}";
            // Coin label starts at 0 — the count-up tween in the entrance
            // sequence tallies it up to coinsDelta in sync with the chip
            // fade-in (StartCoinCountUp below).
            if (_coinLabel != null) _coinLabel.text = $"● +0";
            _coinCountTarget = coinsDelta;

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
            _coinCountTween?.Kill();
            _coinCountTween = null;

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
            if (_refillRowGroup != null) _refillRowGroup.DOKill();
            if (_btnContinueGroup != null) _btnContinueGroup.DOKill();

            // Transform-level tweens — panel PopIn (DOScale) and button idle
            // pulse + press squash (also DOScale on _btnContinue.transform).
            // PopOut's subtree-kill covers these during Dismiss but NOT during
            // OnDestroy, so they need explicit kills here too.
            if (_panel != null) _panel.transform.DOKill();
            if (_btnContinue != null) _btnContinue.transform.DOKill();

            // Hero star/glow tweens — the glow has an ENDLESS pulse, so it must be killed or it leaks.
            if (_starDropCoroutine != null) { StopCoroutine(_starDropCoroutine); _starDropCoroutine = null; }
            if (_heroStarRect != null) _heroStarRect.DOKill();
            if (_heroGlowRect != null) _heroGlowRect.DOKill();
            if (_heroGlowImage != null) _heroGlowImage.DOKill();
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
                // 2026-06-23: park the panel OFF-SCREEN (above) so the card is NOT visible at rest
                // during the 0.12s backdrop fade. The drop callback in AnimateEntrance resets it to
                // rest (0,0) right before DropInWithBounce runs, so the modal only appears via its
                // drop-in animation — never as a static card before it. (Spencer: "the modal should
                // not be showing before the modal show animation happens.")
                if (rt != null) rt.anchoredPosition = new Vector2(0f, UIAnimations.DROP_OFFSCREEN_OFFSET + 400f);
            }
            SetTextAlpha(_titleText, 0f);
            SetTextAlpha(_scoreLabelText, 0f);
            SetTextAlpha(_scoreValueText, 0f);
            SetTextAlpha(_refillText, 0f);
            if (_refillRowGroup != null) _refillRowGroup.alpha = 0f;
            if (_btnContinueGroup != null) _btnContinueGroup.alpha = 0f;
            if (_btnContinue != null) _btnContinue.transform.localScale = Vector3.one;
            // 2026-06-23: the hero star + glow must NOT be attached/visible on the modal during the
            // entrance — they appear only via AnimateStarDrop AFTER the panel has settled. BuildUI
            // disables them initially, but AnimateStarDrop re-enables the star, so on a re-show we
            // must hide them again here (this runs at every Show). Otherwise the star sits on the
            // card at rest from the previous clear.
            if (_starDropCoroutine != null) { StopCoroutine(_starDropCoroutine); _starDropCoroutine = null; }
            if (_heroStarRect != null) { _heroStarRect.DOKill(); _heroStarRect.localScale = Vector3.one; _heroStarRect.anchoredPosition = Vector2.zero; }
            if (_heroGlowRect != null) _heroGlowRect.DOKill();
            if (_heroGlowImage != null) { _heroGlowImage.DOKill(); _heroGlowImage.enabled = false; }
            if (_heroStarImage != null) _heroStarImage.enabled = false;
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
                    // Panel was parked off-screen in ResetEntranceState so it stayed hidden during the
                    // backdrop fade. Restore rest (0,0) so DropInWithBounce reads the correct rest, then
                    // it snaps the panel above and drops it in — all on this same frame (no rest flash).
                    if (rt != null) { rt.anchoredPosition = Vector2.zero; UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED); }
                });
                // 2026-06-01: extended post-drop buffer 0.05 → 0.25s per Spencer.
                // The title toss-in was firing while the panel's settle phase was
                // still finishing, so the two animations stacked visually. Now
                // the panel fully settles, holds for ~200ms, THEN the letters pop —
                // cleaner sequence of beats (modal arrives → pause → letters punch).
                seq.AppendInterval(UIAnimations.DROP_TOTAL_DUR / DROP_SPEED + 0.25f);
            }

            // Phase 3: Children fade in with 80ms stagger (Playrix-style).
            // Title gets the Candy-Crush-style "object toss" — scale-overshoot
            // + rotation wobble + alpha fade all in parallel, ~0.40s.
            seq.AppendCallback(() => TossInTitle());
            // Hold so the modal + title are fully settled and still BEFORE the star arrives —
            // the star should read as a separate beat dropping ONTO a shown modal, not part of
            // the entrance. 2026-06-23 Spencer.
            seq.AppendInterval(0.32f);
            // Celebration star drops into the center (replaces the old score-tally reveal): golden star
            // lowers with an overshoot+bounce settle and a little rotation, golden glow scales in behind,
            // golden particles burst, and the personal_best SFX plays on landing. 2026-06-23 Spencer.
            seq.AppendCallback(() =>
            {
                if (_starDropCoroutine != null) StopCoroutine(_starDropCoroutine);
                _starDropCoroutine = StartCoroutine(AnimateStarDrop());
            });
            seq.AppendInterval(0.10f);
            // Refill chips (edit/swap) still fade in if this clear granted any. Coin count-up removed
            // (no gold count on the modal anymore).
            seq.AppendCallback(() =>
            {
                FadeInText(_refillText, 0.18f);
                if (_refillRowGroup != null)
                    _refillRowGroup.DOFade(1f, 0.18f).SetEase(Ease.OutQuad);
            });
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
        /// <summary>Loads a soft radial glow sprite (reuses the same one the chest/tier-3 FX use).</summary>
        private static Sprite _glowSpriteCache; private static bool _glowSpriteTried;
        private Sprite LoadGlowSprite()
        {
            if (_glowSpriteTried) return _glowSpriteCache;
            _glowSpriteTried = true;
            _glowSpriteCache = Resources.Load<Sprite>("Particles/glow")
                            ?? Resources.Load<Sprite>("Particles/soft_circle")
                            ?? Resources.Load<Sprite>("Particles/vfx_glow")
                            ?? Resources.Load<Sprite>("Particles/glowfree1");
            if (_glowSpriteCache != null) return _glowSpriteCache;
            // The glow textures are imported as Default (not Sprite), so Resources.Load<Sprite>
            // returns null — load the raw Texture2D and wrap it in a runtime Sprite instead.
            Texture2D tex = Resources.Load<Texture2D>("Particles/glow")
                         ?? Resources.Load<Texture2D>("Particles/soft_circle")
                         ?? Resources.Load<Texture2D>("Particles/vfx_glow")
                         ?? Resources.Load<Texture2D>("Particles/glowfree1");
            if (tex != null)
                _glowSpriteCache = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                 new Vector2(0.5f, 0.5f), 100f);
            return _glowSpriteCache;
        }

        /// <summary>Additive material for the hero glow — makes it ADD light to what's behind (fake bloom,
        /// since Overlay UI can't receive real post-process bloom). Falls back to the default sprite shader.</summary>
        private static Material _addGlowMat;
        private Material LoadAdditiveGlowMaterial()
        {
            if (_addGlowMat != null) return _addGlowMat;
            Shader s = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            if (s != null) _addGlowMat = new Material(s);
            return _addGlowMat;
        }

        /// <summary>The glow behind the hero star is Particles/Star02 (Spencer's pick), scaled up in
        /// gold. Imported as Default, so wrap the raw Texture2D in a runtime Sprite like the others.</summary>
        private static Sprite _glowStarCache; private static bool _glowStarTried;
        private Sprite LoadGlowStarSprite()
        {
            if (_glowStarTried) return _glowStarCache;
            _glowStarTried = true;
            _glowStarCache = Resources.Load<Sprite>("Particles/Star02");
            if (_glowStarCache != null) return _glowStarCache;
            Texture2D tex = Resources.Load<Texture2D>("Particles/Star02");
            if (tex != null)
                _glowStarCache = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                               new Vector2(0.5f, 0.5f), 100f);
            return _glowStarCache;
        }

        /// <summary>Loads the gold star sprite for the hero celebration. The Star01 textures are
        /// imported as Default (not Sprite), so Resources.Load&lt;Sprite&gt; returns null — we fall back
        /// to loading the raw Texture2D and wrapping it in a runtime Sprite, which works regardless of
        /// import type. (This is why the star was invisible: the sound fired but the Image had no sprite.)</summary>
        private static Sprite _starSpriteCache; private static bool _starSpriteTried;
        private Sprite LoadStarSprite()
        {
            if (_starSpriteTried) return _starSpriteCache;
            _starSpriteTried = true;
            _starSpriteCache = Resources.Load<Sprite>("Tiles/Icon_ImageIcon_Star01_On")
                            ?? Resources.Load<Sprite>("Particles/Star01");
            if (_starSpriteCache != null) return _starSpriteCache;
            Texture2D tex = Resources.Load<Texture2D>("Tiles/Icon_ImageIcon_Star01_On")
                         ?? Resources.Load<Texture2D>("Particles/Star01");
            if (tex != null)
                _starSpriteCache = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                 new Vector2(0.5f, 0.5f), 100f);
            return _starSpriteCache;
        }

        /// <summary>Royal-Match-style celebration: the golden star DROPS into the center with an
        /// overshoot+bounce settle and a little rotation, a golden glow scales/pulses in behind it,
        /// a burst of golden UI sparkles flies out, and the personal_best SFX plays on landing.
        /// 2026-06-23 Spencer.</summary>
        private IEnumerator AnimateStarDrop()
        {
            if (_heroStarRect == null) yield break;
            if (_heroStarImage != null) _heroStarImage.enabled = _heroStarImage.sprite != null;
            // Glow / layered effects disabled for now — building this piece by piece. Star only.
            if (_heroGlowImage != null) _heroGlowImage.enabled = false;

            _heroStarRect.DOKill();
            if (_heroGlowRect != null) _heroGlowRect.DOKill();
            if (_heroGlowImage != null) _heroGlowImage.DOKill();

            // ReducedMotion: snap to rest, sound, done.
            if (UIAnimations.ReducedMotion)
            {
                _heroStarRect.anchoredPosition = Vector2.zero;
                _heroStarRect.localScale = Vector3.one;
                _heroStarRect.localRotation = Quaternion.identity;
                if (_heroGlowRect != null) { _heroGlowRect.localScale = Vector3.one; _heroGlowRect.localRotation = Quaternion.identity; }
                if (_heroGlowImage != null) { _heroGlowImage.enabled = true; _heroGlowImage.color = HERO_GLOW_GOLD; }
                GameAudio.Instance?.PlayPersonalBest();
                yield break;
            }

            // Perspective settle: star starts BIG (as if close to the camera) at center and
            // scales DOWN to its rest size — no vertical drop. Quick fade so it doesn't hard-pop.
            // It also starts slightly TILTED and rotates UPRIGHT as it lands (a bit of spin-in).
            _heroStarRect.anchoredPosition = Vector2.zero;
            _heroStarRect.localRotation = Quaternion.Euler(0f, 0f, -70f);
            _heroStarRect.localScale = Vector3.one * 2.4f;
            if (_heroStarImage != null)
                _heroStarImage.color = new Color(HERO_STAR_GOLD.r, HERO_STAR_GOLD.g, HERO_STAR_GOLD.b, 0f);

            // Glow (Particles/Star01) starts tiny + transparent behind the star; it scales up in gold
            // only AFTER the star has landed (appended below). Keep it hidden during the star's scale-in.
            if (_heroGlowRect != null)
            {
                _heroGlowRect.localRotation = Quaternion.identity;
                _heroGlowRect.localScale = Vector3.zero; // start from NOTHING and grow
            }
            if (_heroGlowImage != null)
            {
                _heroGlowImage.enabled = false;
                _heroGlowImage.color = HERO_GLOW_GOLD; // starts visible, fades out as it scales up
            }

            Sequence seq = DOTween.Sequence();
            // Big -> small with a multi-bounce settle, like it drops onto a surface (eagle-eye view)
            // and jiggles to rest. OutBounce gives the diminishing-bounce feel.
            seq.Join(_heroStarRect.DOScale(1f, 0.60f).SetEase(Ease.OutBounce));
            // ...and a bit of rotation that settles UPRIGHT (lands straight at the resting spot).
            seq.Join(_heroStarRect.DORotate(Vector3.zero, 0.50f, RotateMode.Fast).SetEase(Ease.OutCubic));
            if (_heroStarImage != null)
                seq.Join(_heroStarImage.DOFade(HERO_STAR_GOLD.a, 0.18f).SetEase(Ease.OutCubic));
            seq.InsertCallback(0.10f, () => GameAudio.Instance?.PlayPersonalBest());
            // Sparkles pop from behind the star right as it lands.
            seq.InsertCallback(0.30f, () => SpawnStarSparkles());

            // After the star has settled, the gold glow-star scales up behind it (then a gentle pulse).
            if (_heroGlowRect != null && _heroGlowImage != null)
            {
                seq.AppendCallback(() => { if (_heroGlowImage != null) _heroGlowImage.enabled = true; });
                // Grow from nothing to full size at full brightness and STAY (no fade-out). It's hidden /
                // killed on dismiss + re-show by ResetEntranceState, so it won't persist past the modal.
                seq.Append(_heroGlowRect.DOScale(1f, 0.55f).SetEase(Ease.OutCubic));
            }
        }

        /// <summary>Sparkle sprites — same ones the in-game pop FX (SparkleSpray) use: a 4-pointed
        /// flare star + a small soft dot ("twinkle"). Loaded as Texture2D + Sprite.Create (Default import).</summary>
        private static Sprite _flareSprite; private static bool _flareTried;
        private static Sprite _twinkleSprite; private static bool _twinkleTried;
        private Sprite LoadFlareSprite()
        {
            if (_flareTried) return _flareSprite;
            _flareTried = true;
            Texture2D tex = Resources.Load<Texture2D>("Particles/flare")
                         ?? Resources.Load<Texture2D>("Particles/flare_star");
            if (tex != null)
                _flareSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _flareSprite ?? LoadGlowSprite();
        }
        private Sprite LoadTwinkleSprite()
        {
            if (_twinkleTried) return _twinkleSprite;
            _twinkleTried = true;
            Texture2D tex = Resources.Load<Texture2D>("Particles/point1")
                         ?? Resources.Load<Texture2D>("Particles/soft_circle");
            if (tex != null)
                _twinkleSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _twinkleSprite ?? LoadFlareSprite();
        }

        /// <summary>A burst of golden sparkles popping out from BEHIND the hero star — same flare/twinkle
        /// sprites as the in-game pop FX. UI-space (children of the card) so they render over the overlay,
        /// inserted BEHIND the star so they appear to emerge from behind it, then fly out + spin + fade.
        /// 2026-06-23 Spencer.</summary>
        private void SpawnStarSparkles()
        {
            if (_panel == null || _heroStarRect == null) return;
            Sprite flare = LoadFlareSprite();
            Sprite twinkle = LoadTwinkleSprite();
            if (flare == null && twinkle == null) return;
            const int COUNT = 14;
            int behindStar = _heroStarRect.GetSiblingIndex(); // insert sparkles here → render BEHIND the star
            Vector2 center = _heroStarRect.anchoredPosition;
            for (int i = 0; i < COUNT; i++)
            {
                bool isFlare = (i % 2 == 0); // alternate big 4-point flares and small twinkles
                var sGO = new GameObject("StarSparkle", typeof(RectTransform), typeof(Image));
                sGO.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)sGO.transform;
                rt.SetSiblingIndex(behindStar);
                rt.anchorMin = _heroStarRect.anchorMin; rt.anchorMax = _heroStarRect.anchorMax;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                float baseSize = isFlare ? (52f + (i % 3) * 14f) : (22f + (i % 3) * 8f);
                rt.sizeDelta = new Vector2(baseSize, baseSize);
                rt.anchoredPosition = center;
                var img = sGO.GetComponent<Image>();
                img.sprite = isFlare ? flare : twinkle;
                img.color = isFlare ? new Color(1f, 0.90f, 0.45f, 1f) : new Color(1f, 0.97f, 0.80f, 1f);
                img.raycastTarget = false;
                img.preserveAspect = true;
                var addMat = LoadAdditiveGlowMaterial();
                if (addMat != null) img.material = addMat; // additive → reads brighter, like the pop sparkles
                // Radial spread (varied so it's not perfectly even), then fly out + spin + fade.
                float ang = (i / (float)COUNT) * Mathf.PI * 2f + (i % 2 == 0 ? 0.35f : -0.42f);
                float dist = 110f + (i % 4) * 34f;
                Vector2 target = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                float spin = (i % 2 == 0 ? 1f : -1f) * (90f + (i % 3) * 60f);
                rt.localScale = Vector3.one * 0.15f;
                var sq = DOTween.Sequence();
                sq.Append(rt.DOScale(1f, 0.16f).SetEase(Ease.OutBack, 2.2f));     // pop
                sq.Join(rt.DOAnchorPos(target, 0.55f).SetEase(Ease.OutCubic));    // fly out
                sq.Join(rt.DOLocalRotate(new Vector3(0f, 0f, spin), 0.55f, RotateMode.LocalAxisAdd).SetEase(Ease.OutCubic)); // spin
                sq.Insert(0.20f, img.DOFade(0f, 0.40f).SetEase(Ease.InQuad));     // fade out
                sq.OnComplete(() => { if (sGO != null) Destroy(sGO); });
            }
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

        /// <summary>
        /// Tally up the coin chip label from 0 to target over duration, matching
        /// the StageScore count-up rhythm. Lands with a small punch-scale so the
        /// final number has the same "look how much you earned" beat as the score.
        /// </summary>
        private void StartCoinCountUp(int target, float duration)
        {
            if (_coinLabel == null) return;
            _coinCountTween?.Kill();
            int current = 0;
            _coinCountTween = DOTween.To(() => current, v =>
            {
                current = v;
                if (_coinLabel != null) _coinLabel.text = $"● +{v}";
            }, target, duration)
                .SetEase(Ease.OutQuad)
                .OnComplete(() =>
                {
                    if (_coinLabel != null)
                    {
                        _coinLabel.text = $"● +{target}";
                        // Smaller punch than the score (0.10 vs 0.15) — the
                        // coin chip is a secondary reward beat, not the hero.
                        _coinLabel.transform.DOPunchScale(
                            Vector3.one * 0.10f, 0.18f, 5, 0.6f);
                    }
                    _coinCountTween = null;
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

            // After the tutorial level that UNLOCKS Swap (the level just before SWAP_UNLOCK_LEVEL), hand off to
            // the Unlock reward modal INSTEAD of resuming — it keeps the overlay paused and resumes/advances on
            // Claim. Royal-Match cadence: cleared celebration FIRST, then the unlock reward. 2026-07-06 Spencer.
            if (_clearedStage == TutorialLocks.EDIT_UNLOCK_LEVEL - 1 && UnlockModal.Instance != null)
            {
                UnlockModal.Instance.Show("Edit", "Change any tile on the board into the letter you need!",
                    Resources.Load<Sprite>("Tiles/swap_tile"));
                return;
            }

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
            // Cartoonish rounded corners on the card (9-sliced, bigger radius than buttons).
            pImg.sprite = MenuUI.GetRoundedRectSprite(44);
            pImg.type = Image.Type.Sliced;

            // Title — top section of the larger panel
            _titleText = CreateLabel(_panel.transform, "Title",
                new Vector2(0.04f, 0.78f), new Vector2(0.96f, 0.93f),
                "LEVEL 1 CLEARED!", 40, TITLE);
            _titleText.fontStyle = FontStyle.Bold;
            _titleText.horizontalOverflow = HorizontalWrapMode.Overflow;

            // CELEBRATION STAR (replaces the old "Level Score" + number tally). A golden radial glow
            // behind a single big golden star, centered where the score used to be. Both start hidden/
            // small and are animated in by AnimateStarDrop. 2026-06-23 Spencer.
            Sprite starSprite = LoadStarSprite();
            Vector2 heroAnchor = new Vector2(0.5f, 0.60f); // upper-middle of the card, where the score was

            // Glow behind = a BIGGER gold star01 (same sprite) that scales up behind the hero star —
            // a golden star-shaped halo, per Spencer 2026-06-23 (instead of a soft radial glow).
            GameObject glowGO = new GameObject("HeroGlow");
            glowGO.transform.SetParent(_panel.transform, false);
            _heroGlowRect = glowGO.AddComponent<RectTransform>();
            _heroGlowRect.anchorMin = heroAnchor;
            _heroGlowRect.anchorMax = heroAnchor;
            _heroGlowRect.pivot = new Vector2(0.5f, 0.5f);
            _heroGlowRect.sizeDelta = new Vector2(640f, 640f); // big gold Star02 glow well past the 190 star
            _heroGlowRect.anchoredPosition = Vector2.zero;
            _heroGlowImage = glowGO.AddComponent<Image>();
            _heroGlowImage.sprite = LoadGlowStarSprite() ?? starSprite; // Particles/Star02, scaled up in gold behind
            _heroGlowImage.color = HERO_GLOW_GOLD;
            // Fake-bloom: additive blend so the glow ADDS light to what's behind it (Overlay UI can't
            // receive real post-process bloom). 2026-06-23 Spencer.
            Material addMat = LoadAdditiveGlowMaterial();
            if (addMat != null) _heroGlowImage.material = addMat;
            _heroGlowImage.preserveAspect = true;
            _heroGlowImage.raycastTarget = false;

            // Hero star — in front, centered, drops in.
            GameObject starGO = new GameObject("HeroStar");
            starGO.transform.SetParent(_panel.transform, false);
            _heroStarRect = starGO.AddComponent<RectTransform>();
            _heroStarRect.anchorMin = heroAnchor;
            _heroStarRect.anchorMax = heroAnchor;
            _heroStarRect.pivot = new Vector2(0.5f, 0.5f);
            _heroStarRect.sizeDelta = new Vector2(190f, 190f);
            _heroStarRect.anchoredPosition = Vector2.zero;
            _heroStarImage = starGO.AddComponent<Image>();
            _heroStarImage.sprite = starSprite;
            _heroStarImage.color = HERO_STAR_GOLD;
            _heroStarImage.preserveAspect = true;
            _heroStarImage.raycastTarget = false;
            _heroStarImage.enabled = false; // hidden until AnimateStarDrop reveals it mid-animation

            // Refill summary — horizontal row of 3 icon+amount chips
            // 2026-06-01: replaced the single text line ("Edits 3 • Swaps 2 • +26¢")
            // with three icon chips. Edit uses the cyan_tile sprite, Swap uses the
            // swap_tile sprite, Coin uses the same ● Unicode glyph as HUDManager
            // (HUDManager.cs:460) for visual consistency with the HUD coin counter.
            // Each chip shows the delta added by this stage clear ("+3"), not the
            // absolute total.
            _refillRow = new GameObject("RefillRow");
            _refillRow.transform.SetParent(_panel.transform, false);
            RectTransform rowRT = _refillRow.AddComponent<RectTransform>();
            rowRT.anchorMin = new Vector2(0.06f, 0.30f);
            rowRT.anchorMax = new Vector2(0.94f, 0.40f);
            rowRT.offsetMin = Vector2.zero;
            rowRT.offsetMax = Vector2.zero;
            _refillRowGroup = _refillRow.AddComponent<CanvasGroup>();
            HorizontalLayoutGroup rowHLG = _refillRow.AddComponent<HorizontalLayoutGroup>();
            rowHLG.childAlignment = TextAnchor.MiddleCenter;
            rowHLG.spacing = 30;
            rowHLG.childForceExpandWidth = false;
            rowHLG.childForceExpandHeight = false;

            Sprite editSprite = Resources.Load<Sprite>("Tiles/cyan_tile@2x");
            Sprite swapSprite = Resources.Load<Sprite>("Tiles/swap_tile");

            _editChip  = CreateRefillChip(_refillRow.transform, "EditChip",  editSprite, REFILL_GREEN, out _editIcon, out _editLabel);
            _swapChip  = CreateRefillChip(_refillRow.transform, "SwapChip",  swapSprite, REFILL_GREEN, out _swapIcon, out _swapLabel);
            // Coin/gold count chip removed per Spencer 2026-06-23 (clean Royal-Match celebration — no
            // numbers). _coinChip/_coinLabel stay null; coins are still AWARDED elsewhere, just not shown
            // here, and the count-up code is null-guarded so it no-ops.

            // Continue button — bigger, sits in bottom 25% of panel
            int childCountBefore = _panel.transform.childCount;
            MenuUI.CreateButton(_panel.transform, "BtnContinue",
                new Vector2(0.18f, 0.08f), new Vector2(0.82f, 0.22f),
                "CONTINUE", new Color(0.96f, 0.63f, 0.16f, 1f), Color.white, 28, // 2026-06-24: warm orange CTA (was green)
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

            // 2026-06-23: the "LEVEL N CLEARED!" title is created BEFORE the hero glow, so the big glow
            // (a later sibling) was rendering on top of the title text. Promote the title to the top of
            // the sibling order so it always sits ABOVE the glow. (It doesn't overlap the button/chips,
            // so being topmost is harmless.)
            if (_titleText != null) _titleText.transform.SetAsLastSibling();
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

        /// <summary>Refill chip: Image (icon) + Text (delta) horizontally
        /// packed via HorizontalLayoutGroup. Returns the chip GameObject so the
        /// caller can hide it when the delta is zero.</summary>
        private static GameObject CreateRefillChip(Transform parent, string name,
            Sprite iconSprite, Color labelColor, out Image iconOut, out Text labelOut)
        {
            GameObject chip = new GameObject(name);
            chip.transform.SetParent(parent, false);
            chip.AddComponent<RectTransform>();
            HorizontalLayoutGroup hlg = chip.AddComponent<HorizontalLayoutGroup>();
            hlg.childAlignment = TextAnchor.MiddleCenter;
            hlg.spacing = 4;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = false;

            GameObject iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(chip.transform, false);
            iconOut = iconGO.AddComponent<Image>();
            iconOut.sprite = iconSprite;
            iconOut.preserveAspect = true;
            iconOut.color = Color.white;
            LayoutElement iconLE = iconGO.AddComponent<LayoutElement>();
            iconLE.preferredWidth = 30;
            iconLE.preferredHeight = 30;

            GameObject labelGO = new GameObject("Label");
            labelGO.transform.SetParent(chip.transform, false);
            labelOut = labelGO.AddComponent<Text>();
            labelOut.font = MenuUI.GetFont();
            labelOut.text = "+0";
            labelOut.fontSize = 22;
            labelOut.color = labelColor;
            labelOut.alignment = TextAnchor.MiddleLeft;
            LayoutElement labelLE = labelGO.AddComponent<LayoutElement>();
            labelLE.preferredWidth = 50;
            labelLE.preferredHeight = 30;

            return chip;
        }

        /// <summary>Coin chip uses the same ● Unicode glyph as HUDManager's
        /// coin counter for visual consistency — no separate sprite needed.</summary>
        private static GameObject CreateCoinChip(Transform parent, string name,
            Color tint, out Text labelOut)
        {
            GameObject chip = new GameObject(name);
            chip.transform.SetParent(parent, false);
            chip.AddComponent<RectTransform>();
            labelOut = chip.AddComponent<Text>();
            labelOut.font = MenuUI.GetFont();
            labelOut.text = "● +0";
            labelOut.fontSize = 22;
            labelOut.color = tint;
            labelOut.alignment = TextAnchor.MiddleCenter;
            LayoutElement le = chip.AddComponent<LayoutElement>();
            le.preferredWidth = 80;
            le.preferredHeight = 30;
            return chip;
        }
    }
}
