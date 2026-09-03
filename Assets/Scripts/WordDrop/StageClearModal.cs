using System.Collections;
using System.Collections.Generic;
using DG.Tweening;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using TMPro;

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
        // True from the moment Continue is tapped until the canvas is dropped. The auto-installer uses this to
        // present the world map WHILE this (still-opaque) beige screen is up, so the map fades in over it — the board
        // never shows during the transition. 2026-07-14 Spencer.
        public bool IsDismissing => _isDismissing;

        private Canvas _canvas;
        private GameObject _panel;
        private CanvasGroup _panelGroup;      // fades the whole content out on dismiss (fade-to-map, no fly-off)
        private Image _overlayImage;          // dim backdrop — faded in separately from panel
        private Image _dimOutImage;           // black full-screen tint that fades IN on dismiss (darkens the exit transition)
        private Image _vignetteImage;         // radial darkening at the edges (over the gradient, behind the content)
        private Image _illumImage;            // big soft warm-white bloom lighting up the center (additive)
        private Image _raysImage; private RectTransform _raysRect; // slow-rotating gold god-rays sunburst behind the star
        private Image _starShadeImage;        // soft dark backing directly behind the star (separation from the FX)
        private Image _heroStarShadowImage;   // soft dark DROP SHADOW offset below the star (grounds it, Candy-Crush style)
        private Image _flashImage;            // full-screen white impact flash on the star landing
        private Text _titleText;
        private RectTransform   _titleContainer;  // "Well Done!" single-word 3-layer echo container (holds the CanvasGroup)
        private TextMeshProUGUI _titleFace;       // white face layer
        private TextMeshProUGUI _titleRim;        // rim layer
        private TextMeshProUGUI _titleShadow;     // shadow layer (legacy TMP path — null now that the title is a baked image)
        private Coroutine       _wellDoneWaveCo;  // legacy per-character wave coroutine (unused with the image title)
        private Image           _titleImage;      // baked "Well Done!" sprite (Resources/Tiles/welldone)
        private UIWaveMesh      _titleWave;        // traveling ripple applied to the baked title
        private GameObject _titleRow;
        private TextMeshProUGUI _levelHeaderTMP;     // "Level N" white FACE (front copy)
        private TextMeshProUGUI _levelHeaderOutline; // fat purple RIM copy = true outside stroke
        private TextMeshProUGUI _levelHeaderShadow;  // dark offset copy = drop shadow (behind the rim)
        private RectTransform   _levelHeaderContainer; // empty parent of all three copies; holds the CanvasGroup + is the animated node
        private Image           _levelRibbonImage;     // 9-sliced ribbon banner behind the "Level N" text
        private RectTransform   _levelRibbonRect;      // its rect — width resized to the level text
        private RectTransform   _levelSheenTL;         // top-left corner highlight (cut from Title_Ribbon02_White_Light)
        private RectTransform   _levelSheenBR;         // bottom-right corner highlight (cut from the same sprite)
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

        // ── 3D hero star tunables (2026-07-28) ──────────────────────────────────
        /// <summary>Soft dark disc behind the star, separating it from the glow core.
        /// false = the pre-2026-07-28 look (no backing).</summary>
        private const bool  STAR_BACK_SHADE       = true;
        /// <summary>Was 0.16 ("felt not seen") when the shade was last live — too low to
        /// separate against the bright glow. Drop it back toward 0.16 if it reads heavy.</summary>
        private const float STAR_BACK_SHADE_ALPHA = 0.42f;
        /// <summary>Full 360° rotating idle instead of the breathing squash/stretch.
        /// false = the original 2026-07-16 breathing idle.</summary>
        /// <summary>Hero star display size. Was a hardcoded 190. The backing shade and drop
        /// shadow derive from this so they stay in proportion — bump this one value only.
        /// Sharpness note: the spin frames carry ~176px of actual star, so past roughly
        /// 300 you're upscaling them (the static sprite is 512 and stays crisp either way).</summary>
        private const float HERO_STAR_SIZE        = 260f;
        private const bool  STAR_IDLE_SPIN        = true;
        private const int   SPIN_FRAMES           = 96;
        /// <summary>One complete revolution. NOTE: playback fps = SPIN_FRAMES / SPIN_SECONDS.
        /// The first pass used 32 frames over 4.5s = 7fps, which read as an animated GIF.
        /// Keep this ratio at 24fps or above — at 96 frames that means SPIN_SECONDS &lt;= 4.0.</summary>
        private const float SPIN_SECONDS          = 3.2f;   // 96/3.2 = 30fps
        private static readonly Color HERO_GLOW_GOLD = new Color(1.00f, 0.80f, 0.20f, 0.62f); // lower alpha → reads golden, not a blown-out white core

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
        private float _flyUpWaitStartedAt = -1f;         // when we started holding for fly-ups-to-target
        private const float FLY_UP_MAX_WAIT = 3f;        // safety cap so a dropped fly-up callback can't hang the modal

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
        // Level-complete backdrop: vertical blue → lavender → pink gradient (Candy-Crush-style, warmer/happier than
        // flat blue). 2026-07-14 Spencer.
        private static readonly Color SKY_TOP    = new Color(0.34f, 0.63f, 0.94f, 1f); // sky blue
        private static readonly Color SKY_MID    = new Color(0.64f, 0.56f, 0.87f, 1f); // lavender/purple
        private static readonly Color SKY_BOTTOM = new Color(0.92f, 0.68f, 0.86f, 1f); // pink
        private static readonly Color WELL_DONE_COL = new Color(1f, 0.83f, 0.28f, 1f); // warm gold — pops celebratory on the blue
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

#if UNITY_EDITOR
            // DEV: press K while the Level-Complete modal is up to dump the three title layers' exact tuned values to
            // the log (debug_log.txt), so Spencer can dial the look in live and hand me precise numbers. 2026-07-15.
            // (L was taken by a level debug menu.)
            if (Input.GetKeyDown(KeyCode.K) && _levelHeaderTMP != null) DumpLevelHeaderValues();
            if (Input.GetKeyDown(KeyCode.V) && _titleFace != null) DumpWellDoneValues();
            if (Input.GetKeyDown(KeyCode.C) && !_isPresenting) ShowForDebug(); // spawn the Level-Complete modal for testing
#endif

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
            // 2026-07-14 Spencer: also hold for the WHOLE first-wild UNLOCK flow (modal → Claim → deal), so when the
            // wild unlocks on the same move that clears the level, the reveal plays out BEFORE "Well Done!".
            if (HandManager.Instance != null && HandManager.Instance.IsWildUnlockFlowActive)
            {
                _explosionsClearedAt = -1f;
                return;
            }
            // 2026-07-14 Spencer: also hold for letters/words still FLYING UP to the objective target, so the "Well
            // Done!" celebration doesn't fire before they land. Safety-timeout the wait so a dropped fly-up callback
            // (e.g. HUD torn down mid-flight) can never softlock the modal.
            if (HUDManager.HasFlyingToTarget)
            {
                if (_flyUpWaitStartedAt < 0f) _flyUpWaitStartedAt = Time.unscaledTime;
                if (Time.unscaledTime - _flyUpWaitStartedAt < FLY_UP_MAX_WAIT)
                {
                    _explosionsClearedAt = -1f; // keep the settle beat fresh until they've all landed
                    return;
                }
                // timed out — proceed rather than hang forever
            }
            _flyUpWaitStartedAt = -1f;
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
            if (_titleText != null) _titleText.text = "Well Done!";
            string levelLabel = $"Level {LevelMapPanel.DisplayNum(ctx.ClearedStage)}"; // run level (tutorial 1..10)
            if (_levelHeaderTMP != null)     _levelHeaderTMP.text = levelLabel;     // white face
            if (_levelHeaderOutline != null) _levelHeaderOutline.text = levelLabel; // rim — must mirror the face
            if (_levelHeaderShadow != null)  _levelHeaderShadow.text = levelLabel;  // shadow — must mirror the face
            RefreshLevelRibbon(); // size the ribbon banner to the (now-known) level label
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
            if (_titleContainer != null) _titleContainer.DOKill();
            if (_wellDoneWaveCo != null) { StopCoroutine(_wellDoneWaveCo); _wellDoneWaveCo = null; } // stop a running wave on re-show
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
            // The spin idle loops forever — stop it or it keeps swapping sprites on a hidden modal.
            if (_spinCoroutine != null) { StopCoroutine(_spinCoroutine); _spinCoroutine = null; }
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
                // Re-enable taps: the map-flow Dismiss disables the overlay's raycast so it can't re-fire mid-transition,
                // and it was never turned back on — so the SECOND clear's modal couldn't be tapped. 2026-07-14 Spencer.
                _overlayImage.raycastTarget = true;
            }
            if (_vignetteImage != null)
                _vignetteImage.color = new Color(1f, 1f, 1f, 0f); // hidden until AnimateEntrance fades it in
            if (_illumImage != null)
            {
                var ic = _illumImage.color; _illumImage.color = new Color(ic.r, ic.g, ic.b, 0f);
            }
            if (_raysImage != null)
            {
                if (_raysRect != null) { _raysRect.DOKill(); _raysRect.localRotation = Quaternion.identity; _raysRect.localScale = Vector3.zero; }
                _raysImage.DOKill();
                var rc = _raysImage.color; _raysImage.color = new Color(rc.r, rc.g, rc.b, 0f);
            }
            if (_starShadeImage != null)
            {
                var sc = _starShadeImage.color; _starShadeImage.color = new Color(sc.r, sc.g, sc.b, 0f);
            }
            if (_heroStarShadowImage != null) // reset the drop shadow here too, so it doesn't linger visible on a re-show
            {
                _heroStarShadowImage.DOKill();
                var dc = _heroStarShadowImage.color; _heroStarShadowImage.color = new Color(dc.r, dc.g, dc.b, 0f);
            }
            if (_flashImage != null) { _flashImage.DOKill(); _flashImage.color = new Color(1f, 1f, 1f, 0f); }
            if (_dimOutImage != null) { _dimOutImage.DOKill(); _dimOutImage.color = new Color(0f, 0f, 0f, 0f); }
            if (_panel != null) _panel.transform.DOKill(); // clear any lingering impact-shake before resetting rest pos
            // 2026-05-29: panel starts at full scale, anchored at REST.
            // UIAnimations.DropInWithBounce reads rest position and offsets
            // the panel above; old scale-zero start replaced by position-
            // based drop-bounce to unify with TopOutPanel + future modals.
            if (_panel != null)
            {
                _panel.transform.localScale = Vector3.one;
                var rt = _panel.transform as RectTransform;
                if (rt != null) rt.anchoredPosition = Vector2.zero; // at REST — no drop-in anymore (content self-animates)
            }
            if (_panelGroup != null) _panelGroup.alpha = 1f; // reset from a prior exit fade; content manages its own alpha
            HideTMP(_levelHeaderContainer); // "Level N" hidden until its toss-in (container carries all three copies)
            SetTextAlpha(_titleText, 0f);
            HideTMP(_titleContainer); // "Well Done!" echo hidden until its toss-in
            SetTextAlpha(_scoreLabelText, 0f);
            SetTextAlpha(_scoreValueText, 0f);
            SetTextAlpha(_refillText, 0f);
            if (_refillRowGroup != null) _refillRowGroup.alpha = 0f;
            if (_btnContinueGroup != null) _btnContinueGroup.alpha = 0f;
            // Also DEACTIVATE the prompt GO — a CanvasGroup alpha set while the canvas is still inactive (it's
            // SetActive(true)'d just after this) can fail to apply on the first frame, flashing the prompt at its old
            // alpha. An inactive GameObject can't render, so this guarantees it's hidden until its reveal. 2026-07-14.
            if (_btnContinue != null) { _btnContinue.transform.localScale = Vector3.one; _btnContinue.SetActive(false); }
            // 2026-06-23: the hero star + glow must NOT be attached/visible on the modal during the
            // entrance — they appear only via AnimateStarDrop AFTER the panel has settled. BuildUI
            // disables them initially, but AnimateStarDrop re-enables the star, so on a re-show we
            // must hide them again here (this runs at every Show). Otherwise the star sits on the
            // card at rest from the previous clear.
            if (_starDropCoroutine != null) { StopCoroutine(_starDropCoroutine); _starDropCoroutine = null; }
            if (_spinCoroutine != null) { StopCoroutine(_spinCoroutine); _spinCoroutine = null; }
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

            // Phase 1: the board + HUDs tween OFF (mirror of the level-entry slide) WHILE the beige washes in over
            // the top — so the level "leaves" the same way it arrived, then the screen settles to beige. Only in the
            // map flow (the legacy non-map path fades the beige back to reveal the board). 2026-07-14 Spencer.
            if (LevelMapPanel.MapFlowEnabled)
            {
                seq.AppendCallback(() => HUDManager.Instance?.AnimateLevelExitOut());
                seq.AppendInterval(0.12f); // let the board/HUDs start leaving BEFORE the beige climbs over them
            }
            if (_overlayImage != null)
                seq.Append(_overlayImage.DOFade(1f, 0.30f).SetEase(Ease.InQuad)); // beige washes in as the board slides off
            else
                seq.AppendInterval(0.30f);
            // Vignette disabled (2026-07-15 Spencer) — re-enable by restoring this fade.
            // if (_vignetteImage != null)
            //     seq.Join(_vignetteImage.DOFade(1f, 0.30f).SetEase(Ease.InQuad));
            if (_illumImage != null)
                seq.Join(_illumImage.DOFade(0.14f, 0.34f).SetEase(Ease.InQuad)); // faint warm BASE only — the rays now carry the gold (was 0.32; it was washing everything out)
            // (God-rays are NOT shown here — they BURST out from nothing on the star's impact; see AnimateStarDrop.)
            if (_starShadeImage != null)
                seq.Join(_starShadeImage.DOFade(STAR_BACK_SHADE_ALPHA, 0.34f).SetEase(Ease.InQuad)); // dark tint behind the star — separation from the glow core

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
            // Phase 2: NO panel drop-in anymore (Spencer 2026-07-14) — the whole screen is beige and the content
            // appears via each element's own animation. The LEVEL NUMBER tosses in first (Candy-Crush object toss),
            // then the "Well Done!" title a beat later — staggered.
            seq.AppendCallback(() => TossInTMP(_levelHeaderContainer));
            seq.AppendInterval(0.18f);
            // "Well Done!" letters pop in AND the star + big celebration fire at the SAME TIME (2026-07-15 Spencer) —
            // the whole hit lands together with the title animating, instead of a separate beat afterwards.
            seq.AppendCallback(() =>
            {
                PlayWellDoneWave(); // "Well Done!" letters pop in one at a time (per-character wave, keeps perfect spacing)
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
            // Continue button — hold a clear beat so the star + glow (a PARALLEL coroutine, ~1.15s) fully land FIRST,
            // THEN fade the prompt in + start the pulse. The old 0.55 elapsed mid-celebration, so it read as no beat.
            seq.AppendInterval(1.25f); // 2026-07-14 Spencer: "tap to continue" waits a real beat before appearing
            seq.AppendCallback(() =>
            {
                if (_btnContinue != null) _btnContinue.SetActive(true); // re-activate for its reveal
                if (_btnContinueGroup != null) { _btnContinueGroup.alpha = 0f; _btnContinueGroup.DOFade(1f, 0.18f); }
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

        /// <summary>Hides a toss-in label before its animation: via its CanvasGroup if it has one (so a drop shadow is
        /// hidden too), else via the text colour alpha. 2026-07-14 Spencer.</summary>
        private static void HideLabelForToss(Component label)
        {
            if (label == null) return;
            var cg = label.GetComponent<CanvasGroup>();
            if (cg != null) { cg.DOKill(); cg.alpha = 0f; }
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
        private void TossInTitle() => TossInText(_titleText);

        // Candy-Crush "object toss": tiny + tilted + transparent → OutBack scale/rotation overshoot + quick fade.
        // Used for BOTH the level-number header and the "Well Done!" title (staggered). 2026-07-14 Spencer.
        private void TossInText(Component label, float duration = 0.28f, float overshoot = 3.0f, float tilt = 8f, float startScale = 0.1f)
        {
            if (label == null) return;
            Transform t = label.transform;
            t.DOKill();
            t.localScale    = Vector3.one * startScale;
            t.localRotation = Quaternion.Euler(0f, 0f, tilt);
            // Fade via a CanvasGroup (works for uGUI Text or TMP). Ensure one exists so the pop is identical either way.
            var cg = label.GetComponent<CanvasGroup>();
            if (cg == null) cg = label.gameObject.AddComponent<CanvasGroup>();
            cg.DOKill(); cg.alpha = 0f;

            t.DOScale(1.0f, duration).SetEase(Ease.OutBack, overshoot);
            t.DOLocalRotate(Vector3.zero, duration).SetEase(Ease.OutBack, overshoot); // tilt → past 0 → settle
            float fadeDur = Mathf.Min(0.12f, duration * 0.5f); // snap the fade in fast for a punchy pop
            cg.DOFade(1f, fadeDur).SetEase(Ease.OutQuad);
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

        /// <summary>The soft radial glow used behind the hero star / illumination / sparkle halos — now
        /// Particles/VFX_Glow (Spencer's pick 2026-07-15), a soft additive aura. Falls back to soft_circle.
        /// Imported as Default, so wrap the raw Texture2D in a runtime Sprite.</summary>
        private static Sprite _glowStarCache; private static bool _glowStarTried;
        private Sprite LoadGlowStarSprite()
        {
            if (_glowStarTried) return _glowStarCache;
            _glowStarTried = true;
            _glowStarCache = Resources.Load<Sprite>("Particles/VFX_Glow") ?? Resources.Load<Sprite>("Particles/soft_circle");
            if (_glowStarCache != null) return _glowStarCache;
            Texture2D tex = Resources.Load<Texture2D>("Particles/VFX_Glow") ?? Resources.Load<Texture2D>("Particles/soft_circle");
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
        /// <summary>True when the rendered star3d art loaded. That art is already
        /// coloured, so it must NOT be tinted by HERO_STAR_GOLD — same reasoning as
        /// heroIsTrophy. Tinting it multiplies the gold in twice and crushes blue.</summary>
        private static bool _starIs3D;
        private Sprite LoadStarSprite()
        {
            if (_starSpriteTried) return _starSpriteCache;
            _starSpriteTried = true;
            // 2026-07-28: star3d_gold (rendered Candy-Crush-style star) takes priority.
            // TO REVERT: delete Resources/Tiles/star3d_gold.png and this falls straight
            // back to the original authored Icon_ImageIcon_Star01_On — no code change.
            _starSpriteCache = Resources.Load<Sprite>("Tiles/star3d_gold");
            _starIs3D = _starSpriteCache != null;
            if (_starSpriteCache == null)
                _starSpriteCache = Resources.Load<Sprite>("Tiles/Icon_ImageIcon_Star01_On")
                                ?? Resources.Load<Sprite>("Particles/Star01");
            if (_starSpriteCache != null) return _starSpriteCache;
            Texture2D tex = Resources.Load<Texture2D>("Tiles/star3d_gold");
            _starIs3D = tex != null;
            if (tex == null)
                tex = Resources.Load<Texture2D>("Tiles/Icon_ImageIcon_Star01_On")
                   ?? Resources.Load<Texture2D>("Particles/Star01");
            if (tex != null)
                _starSpriteCache = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                 new Vector2(0.5f, 0.5f), 100f);
            return _starSpriteCache;
        }

        private static Sprite _trophySpriteCache; private static bool _trophySpriteTried;
        private Sprite LoadTrophySprite()
        {
            if (_trophySpriteTried) return _trophySpriteCache;
            _trophySpriteTried = true;
            _trophySpriteCache = Resources.Load<Sprite>("Tiles/Icon_ItemIcon_Trophy");
            if (_trophySpriteCache != null) return _trophySpriteCache;
            Texture2D tex = Resources.Load<Texture2D>("Tiles/Icon_ItemIcon_Trophy");
            if (tex != null)
                _trophySpriteCache = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                   new Vector2(0.5f, 0.5f), 100f);
            return _trophySpriteCache;
        }

        /// <summary>Royal-Match-style celebration: the golden star DROPS into the center with an
        /// overshoot+bounce settle and a little rotation, a golden glow scales/pulses in behind it,
        /// a burst of golden UI sparkles flies out, and the personal_best SFX plays on landing.
        /// 2026-06-23 Spencer.</summary>
        private IEnumerator AnimateStarDrop()
        {
            if (_heroStarRect == null) yield break;
            // Boss (world-ending) levels celebrate with a TROPHY instead of a star — the gold glow behind + the
            // sparkles stay exactly the same. 2026-07-13 Spencer.
            bool  heroIsTrophy = LevelMapPanel.IsBossLevel(_clearedStage);
            Color heroCol      = (heroIsTrophy || _starIs3D) ? Color.white : HERO_STAR_GOLD; // trophy AND the rendered 3D star show their own colour; only the flat vector star is tinted
            if (_heroStarImage != null) _heroStarImage.sprite = heroIsTrophy ? LoadTrophySprite() : LoadStarSprite();
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
                if (_heroStarImage != null) _heroStarImage.color = heroCol;
                if (_heroGlowRect != null) { _heroGlowRect.localScale = Vector3.one; _heroGlowRect.localRotation = Quaternion.identity; }
                if (_heroGlowImage != null) { _heroGlowImage.enabled = true; _heroGlowImage.color = HERO_GLOW_GOLD; }
                if (_heroStarShadowImage != null)
                { var shc = _heroStarShadowImage.color; _heroStarShadowImage.color = new Color(shc.r, shc.g, shc.b, 0.5f); }
                GameAudio.Instance?.PlayPersonalBest();
                yield break;
            }

            // Star DROPS IN from the "sky" — starts BIG (close to camera) + up high + tumbling, then falls down and scales
            // DOWN to rest into the hit, then squash-and-stretch (Royal-Match-style entrance). 2026-07-16 Spencer.
            _heroStarRect.anchoredPosition = new Vector2(0f, 420f); // start UP in the sky
            _heroStarRect.localRotation = Quaternion.Euler(0f, 0f, -320f); // starts tilted; spins ~320° to upright as it drops
            _heroStarRect.localScale = Vector3.one * 2.7f; // start BIG (close to camera) → falls + scales DOWN to rest
            if (_heroStarImage != null)
                _heroStarImage.color = heroCol; // OPAQUE from the start — scale up from nothing (no fade) so the squash is fully visible
            if (_heroStarShadowImage != null) // drop shadow hidden until the star lands (faded in on impact below)
            { var shc0 = _heroStarShadowImage.color; _heroStarShadowImage.color = new Color(shc0.r, shc0.g, shc0.b, 0f); }

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
            seq.timeScale = 0.85f; // 2026-07-15 Spencer — slow the star + its synced FX "a touch" (15% slower, all aligned)
            // Cartoon SQUASH & STRETCH landing (Feel-style): the star SCALES IN small→1 into the hit, SQUASHES flat on
            // impact, rebounds TALL, then settles round with a pop. This is the "hits with force" cartoon move. 2026-07-15.
            seq.Insert(0f,    _heroStarRect.DOScale(Vector3.one, 0.20f).SetEase(Ease.InQuad));                        // scale DOWN from big into the hit
            seq.Insert(0f,    _heroStarRect.DOAnchorPos(Vector2.zero, 0.20f).SetEase(Ease.InQuad));                    // DROP down from the sky (accelerating)
            seq.Insert(0.20f, _heroStarRect.DOScale(new Vector3(2.10f, 0.40f, 1f), 0.08f).SetEase(Ease.OutQuad));     // SQUASH WIDE (cartoon pull, 2026-07-16 — slower so it reads)
            // (0.28→0.34 the star HOLDS flat — an impact hold = the "hitstop" weight before it springs back.)
            seq.Insert(0.34f, _heroStarRect.DOScale(new Vector3(0.70f, 1.40f, 1f), 0.10f).SetEase(Ease.OutQuad));     // rebound TALL (bigger stretch)
            seq.Insert(0.44f, _heroStarRect.DOScale(Vector3.one, 0.22f).SetEase(Ease.OutBack, 2.4f)                  // settle round + pop
                .OnComplete(StartStarIdle));                                                                        // → cute floaty idle once it rests
            // SPIN upright at the START (Insert 0f, NOT Join) so it lands upright by the impact. No fade — the star is
            // opaque and scales up from nothing (0.02), so the whole squash is visible. 2026-07-16 Spencer.
            seq.Insert(0f, _heroStarRect.DORotate(Vector3.zero, 0.20f, RotateMode.FastBeyond360).SetEase(Ease.OutCubic)); // spin ~320° to upright as it drops
            // Reveal whoosh fires at t=0 with the star's fall. Its swell is 0.22s, so the peak
            // lands on the impact at 0.20 and the tail rings out through the squash/settle. 2026-07-29.
            seq.InsertCallback(0f, () => GameAudio.Instance?.PlayStarRevealWhoosh());
            seq.InsertCallback(0.16f, () => GameAudio.Instance?.PlayPersonalBest());
            // ALL impact FX fire on the star's FIRST HIT — the OutBounce drop first reaches full size ~0.22s in, THEN
            // bounces back up. Fire everything on that first contact, not during/after the bounce. 2026-07-14 Spencer.
            // The EXPLOSION punch on the first hit — bubble + shockwave + glare + the rays burst (+ glow, below).
            seq.InsertCallback(0.20f, () =>
            {
                SpawnBubbleBurst(); SpawnShockwave(); SpawnGlarePop(); SpawnGoldStars(); SpawnBlickBurst();
                HapticsManager.MegaImpact(); // big celebratory buzz on the star's landing/explosion. 2026-07-15 Spencer.
                GameAudio.Instance?.PlayLine(); // SFX/line on the star landing (replaced chain_reaction). 2026-07-16 Spencer.
                // Drop shadow fades in as the star lands — grounds it. 2026-07-15 Spencer.
                if (_heroStarShadowImage != null)
                    _heroStarShadowImage.DOFade(0.5f, 0.30f).SetEase(Ease.OutQuad).SetUpdate(true);
                // JUICE — a white impact FLASH + a quick SCREEN SHAKE of the celebration content, so the landing hits
                // with cartoon force (Feel's Flash + PositionShake, done in code). 2026-07-15 Spencer.
                if (_flashImage != null)
                {
                    _flashImage.transform.SetAsLastSibling();
                    _flashImage.DOKill();
                    _flashImage.color = new Color(1f, 1f, 1f, 0.5f);
                    _flashImage.DOFade(0f, 0.16f).SetEase(Ease.OutQuad).SetUpdate(true);
                }
                if (_panel != null && _panel.transform is RectTransform panelRT)
                    panelRT.DOShakeAnchorPos(0.34f, 20f, 22, 90f, false, true).SetUpdate(true);
                // God-rays BURST from nothing on the impact: scale 0→full + fade in, then spin slowly. 2026-07-14.
                if (_raysRect != null && _raysImage != null)
                {
                    _raysRect.DOKill();
                    _raysImage.DOKill();
                    _raysRect.localScale = Vector3.zero;
                    _raysRect.localRotation = Quaternion.identity;
                    var rc = _raysImage.color; _raysImage.color = new Color(rc.r, rc.g, rc.b, 0f);
                    _raysRect.DOScale(1f, 0.45f).SetEase(Ease.OutCubic).SetUpdate(true);         // burst to full size
                    // Fade in to a peak, then PULSE the intensity (alpha yoyo) so the rays "radiate" — throb brighter/
                    // dimmer continuously. 2026-07-15 Spencer.
                    _raysImage.DOFade(0.44f, 0.35f).SetEase(Ease.OutQuad).SetUpdate(true)
                        .OnComplete(() =>
                        {
                            if (_raysImage != null)
                                _raysImage.DOFade(0.16f, 0.85f).SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine).SetUpdate(true);
                        });
                    _raysRect.DORotate(new Vector3(0f, 0f, 360f), 26f, RotateMode.FastBeyond360)  // slow continuous spin
                        .SetLoops(-1, LoopType.Restart).SetEase(Ease.Linear).SetUpdate(true);
                }
            });
            // Sparkles + confetti TRAIL the punch by a beat, so they read as a separate scatter — not one gold pile.
            seq.InsertCallback(0.26f, () => SpawnStarSparkles());
            seq.InsertCallback(0.32f, () => SpawnConfettiColorful()); // code-drawn palette flakes (Confetti FX/UIParticle route reverted)
            seq.InsertCallback(0.60f, () => GameAudio.Instance?.PlayYay()); // "yay!" cheer — a clear beat AFTER the explosion (2026-07-16 Spencer)

            // Glow EXPLOSION on that same first hit: the gold glow bursts out PAST full size, then springs back and
            // stays — reads as the glow detonating behind the star (not a gentle grow). 2026-07-14 Spencer.
            if (_heroGlowRect != null && _heroGlowImage != null)
            {
                seq.InsertCallback(0.20f, () =>
                {
                    if (_heroGlowImage != null) _heroGlowImage.enabled = true;
                    if (_heroGlowRect == null) return;
                    _heroGlowRect.DOKill();
                    _heroGlowRect.localScale = Vector3.zero;
                    DOTween.Sequence()
                        .Append(_heroGlowRect.DOScale(1.45f, 0.20f).SetEase(Ease.OutQuad))   // blast out
                        .Append(_heroGlowRect.DOScale(1f, 0.34f).SetEase(Ease.OutBack, 1.6f)) // spring back + settle
                        .SetUpdate(true);
                });
            }
        }

        /// <summary>Cute cartoon IDLE for the hero star once it's landed: STAYS PUT, just a soft breathing
        /// squash/stretch loop. Unscaled (matches the rest of the celebration). Killed by _heroStarRect.DOKill()
        /// on dismiss/re-show. 2026-07-16 Spencer.</summary>
        private void StartStarIdle()
        {
            if (_heroStarRect == null) return;

            // Full 360° turn: the star rotates to show it's a real 3D object. Falls back to
            // the original breathing squash if the frames are missing or the flag is off.
            if (STAR_IDLE_SPIN && _starIs3D && LoadSpinFrames() != null)
            {
                if (_spinCoroutine != null) StopCoroutine(_spinCoroutine);
                _spinCoroutine = StartCoroutine(SpinIdleLoop());
                return;
            }

            _heroStarRect.DOScale(new Vector3(1.05f, 0.95f, 1f), 1.05f)             // breathing squash/stretch, in place
                .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine).SetUpdate(true);
        }

        // ── 360° spin idle ──────────────────────────────────────────────────────
        // A 10x10 sheet of 96 renders at even 3.75° steps about the screen-vertical axis.
        // Even spacing (not the sine distribution a sway would use) because a continuous
        // revolution should run at constant speed.
        //
        // 96 frames, not 32: smoothness is frames-per-SECOND, not frames-per-revolution.
        // 32 frames over a 4.5s turn is 7fps and reads as an animated GIF. 96/3.2s = 30fps.
        //
        // The star is a CLOSED double-cone — one outline ring with an apex in front and a
        // mirrored apex behind — so 180° looks identical to 0°, and edge-on at 90°/270°
        // shows a solid lens profile rather than the thin sliver the old shell gave.
        //
        // Frame 31 wraps straight to frame 0, so the loop is seamless with no ping-pong.
        private Coroutine _spinCoroutine;
        private static Sprite[] _spinFrames; private static bool _spinTried;

        private static Sprite[] LoadSpinFrames()
        {
            if (_spinTried) return _spinFrames;
            _spinTried = true;
            Texture2D sheet = Resources.Load<Texture2D>("Tiles/star3d_spin_sheet");
            if (sheet == null) return null;

            const int COLS = 10, ROWS = 10;
            int fw = sheet.width / COLS, fh = sheet.height / ROWS;
            var frames = new Sprite[SPIN_FRAMES];
            for (int i = 0; i < SPIN_FRAMES; i++)
            {
                int cx = i % COLS, cy = i / COLS;
                // Unity texture space is bottom-up; the sheet was packed top-down.
                var rect = new Rect(cx * fw, sheet.height - (cy + 1) * fh, fw, fh);
                frames[i] = Sprite.Create(sheet, rect, new Vector2(0.5f, 0.5f), 100f);
            }
            _spinFrames = frames;
            return _spinFrames;
        }

        private System.Collections.IEnumerator SpinIdleLoop()
        {
            Sprite[] frames = LoadSpinFrames();
            if (frames == null || _heroStarImage == null) yield break;

            // Sample the frame from ELAPSED TIME rather than sleeping a fixed interval per
            // frame. WaitForSecondsRealtime(0.033) rounds unevenly against the display
            // refresh — some frames hold for one refresh, some for two — which reads as
            // judder even at 30fps. Time-based indexing is self-correcting and never drifts.
            float t0 = Time.unscaledTime;
            int shown = -1;
            while (true)
            {
                if (_heroStarImage == null) yield break;
                float phase = ((Time.unscaledTime - t0) / SPIN_SECONDS) % 1f;
                int i = (int)(phase * SPIN_FRAMES) % SPIN_FRAMES;
                if (i != shown) { _heroStarImage.sprite = frames[i]; shown = i; }
                yield return null;   // unscaled, like the rest of the celebration
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
            Sprite halo = LoadGlowStarSprite(); // soft_circle — the soft glow BEHIND each glint (Candy-Crush 2-layer sparkle)
            if (flare == null && twinkle == null) { Debug.LogWarning("[StageClear] SpawnStarSparkles: no flare/twinkle sprite — no sparkles."); return; }
            Debug.Log($"[StageClear] SpawnStarSparkles firing (flare={(flare!=null)} twinkle={(twinkle!=null)} halo={(LoadGlowStarSprite()!=null)})");
            Material addMat = LoadAdditiveGlowMaterial();
            const int COUNT = 24;
            int behindStar = _heroStarRect.GetSiblingIndex(); // insert sparkles here → render BEHIND the star
            Vector2 center = _heroStarRect.anchoredPosition;
            for (int i = 0; i < COUNT; i++)
            {
                bool isFlare = (i % 2 == 0); // alternate big 4-point flares and small twinkles
                // Container: holds a soft halo + a crisp glint, moves/scales/fades AS ONE (a CanvasGroup fades both).
                var sGO = new GameObject("StarSparkle", typeof(RectTransform));
                sGO.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)sGO.transform;
                rt.SetSiblingIndex(behindStar);
                rt.anchorMin = _heroStarRect.anchorMin; rt.anchorMax = _heroStarRect.anchorMax;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                float baseSize = isFlare ? Random.Range(44f, 92f) : Random.Range(15f, 40f); // wide size range like CC
                rt.sizeDelta = new Vector2(baseSize, baseSize);
                rt.anchoredPosition = center;
                var cg = sGO.AddComponent<CanvasGroup>();

                // Soft glow halo behind the glint — HALVED (was 2.1×) + dimmer, so it reads as a subtle glow, not a blob.
                // WHITE glow behind the glints (additive) — the hot centre reads white and the falloff lets whatever's
                // BEHIND it bleed through the edges (so it picks up the local background colour, like the reference).
                // 2026-07-15 Spencer.
                if (halo != null)
                    AddSparkleLayer(rt, halo, baseSize * 1.3f, new Color(1f, 1f, 1f, 0.30f), addMat);
                // Crisp glint on top (4-point flare or small twinkle) — kept so it can TWINKLE while floating.
                Image glintImg = AddSparkleLayer(rt, isFlare ? flare : twinkle, baseSize,
                    isFlare ? new Color(1f, 1f, 1f, 1f) : new Color(0.95f, 0.98f, 1f, 1f), addMat);

                // ── BLAST-then-FLOAT (2026-07-15 Spencer) ───────────────────────────────────────────────────────────
                // Horizontal EXPLOSION finishes in a fixed ~0.35s (decoupled from lifetime, so a long float doesn't
                // slow the blast), then holds. Vertical pops up with the blast, SUSPENDS, then FLOATS DOWN slowly
                // (SmoothStep descent = gentle hang → drift → settle). Long lifetime = slow, floaty.
                float ang = Random.Range(0f, Mathf.PI * 2f);                        // RADIAL launch direction
                float dist = Random.Range(240f, 560f);                              // radial reach (tighter than the confetti, esp. upward)
                float dirX = Mathf.Cos(ang), dirY = Mathf.Sin(ang);
                float floatDown = Random.Range(300f, 560f);                         // slow gravity drift after the blast
                float lifetime = Random.Range(1.3f, 2.0f);                          // floaty but disappears sooner
                const float BURST_T = 0.22f;                                        // blast finishes FAST → explosive dispersion
                float finalScale = Random.Range(0.85f, 1.25f);
                float spinDeg = (Random.value < 0.5f ? 1f : -1f) * Random.Range(70f, 240f); // gentle drift-spin over the life
                rt.localScale = Vector3.one * 0.1f;
                Vector2 c0 = center;

                var sq = DOTween.Sequence();
                sq.Append(DOTween.To(() => 0f, u =>
                {
                    if (rt == null) return;
                    float bp = Mathf.Clamp01(u * lifetime / BURST_T);               // blast progress (real-time based)
                    float burst = dist * (1f - Mathf.Pow(1f - bp, 6f));            // radial explosive burst FROM THE CENTRE
                    float down = floatDown * Mathf.SmoothStep(0f, 1f, u);          // slow gravity drift down
                    rt.anchoredPosition = c0 + new Vector2(dirX * burst, dirY * burst - down);
                }, 1f, lifetime).SetEase(Ease.Linear));
                sq.Insert(0f, rt.DOScale(finalScale, 0.18f).SetEase(Ease.OutBack, 2.2f)); // springy pop-in (fixed, snappy)
                sq.Insert(lifetime * 0.55f, rt.DOScale(finalScale * 0.15f, lifetime * 0.45f).SetEase(Ease.InQuad)); // shrink tail
                sq.Insert(0f, rt.DOLocalRotate(new Vector3(0f, 0f, spinDeg), lifetime, RotateMode.LocalAxisAdd).SetEase(Ease.Linear)); // gentle spin
                sq.Insert(lifetime * 0.48f, cg.DOFade(0f, lifetime * 0.52f).SetEase(Ease.InQuad)); // start fading sooner
                sq.SetUpdate(true);
                sq.OnComplete(() => { if (sGO != null) Destroy(sGO); });

                // TWINKLE while floating: the glint's own alpha flickers (looping yoyo, random speed so they're out of
                // sync). Multiplies under the container fade, so it still fades out at the tail. Linked to the GO so the
                // loop is killed when the sparkle is destroyed. 2026-07-15 Spencer.
                if (glintImg != null)
                    glintImg.DOFade(Random.Range(0.28f, 0.5f), Random.Range(0.16f, 0.38f))
                        .SetLoops(-1, LoopType.Yoyo).SetEase(Ease.InOutSine).SetUpdate(true).SetLink(sGO);
            }
        }

        /// <summary>Adds one centered Image child (a glow halo or a glint) to a sparkle container; returns the Image
        /// (so callers can e.g. twinkle the glint). 2026-07-14.</summary>
        private static Image AddSparkleLayer(RectTransform parent, Sprite sprite, float size, Color color, Material mat)
        {
            if (sprite == null) return null;
            var go = new GameObject("SparkleLayer", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = new Vector2(0.5f, 0.5f); rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            img.preserveAspect = true;
            if (mat != null) img.material = mat; // additive → reads as light
            return img;
        }

        private static Sprite _bubbleSprite; private static bool _bubbleTried;
        private Sprite LoadBubbleSprite()
        {
            if (_bubbleTried) return _bubbleSprite;
            _bubbleTried = true;
            _bubbleSprite = Resources.Load<Sprite>("Particles/VFX_Circle_out");
            if (_bubbleSprite != null) return _bubbleSprite;
            var tex = Resources.Load<Texture2D>("Particles/VFX_Circle_out");
            if (tex != null) _bubbleSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _bubbleSprite;
        }

        private static Sprite _ringSprite; private static bool _ringTried;
        private Sprite LoadRingSprite()
        {
            if (_ringTried) return _ringSprite;
            _ringTried = true;
            _ringSprite = Resources.Load<Sprite>("Particles/VFX_Circle_3");
            if (_ringSprite != null) return _ringSprite;
            var tex = Resources.Load<Texture2D>("Particles/VFX_Circle_3");
            if (tex != null) _ringSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _ringSprite;
        }

        private static Sprite _raysSprite; private static bool _raysTried;
        private Sprite LoadRaysSprite()
        {
            if (_raysTried) return _raysSprite;
            _raysTried = true;
            _raysSprite = Resources.Load<Sprite>("Particles/VFX_Rays");
            if (_raysSprite != null) return _raysSprite;
            var tex = Resources.Load<Texture2D>("Particles/VFX_Rays");
            if (tex != null) _raysSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _raysSprite;
        }

        private static Sprite _glareSprite; private static bool _glareTried;
        private Sprite LoadGlareSprite()
        {
            if (_glareTried) return _glareSprite;
            _glareTried = true;
            _glareSprite = Resources.Load<Sprite>("Particles/VFX_GlareGold");
            if (_glareSprite != null) return _glareSprite;
            var tex = Resources.Load<Texture2D>("Particles/VFX_GlareGold");
            if (tex != null) _glareSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return _glareSprite;
        }

        /// <summary>Sharp gold GLARE that pops + twinkles at the star on landing (VFX_GlareGold, additive). 2026-07-14.</summary>
        private void SpawnGlarePop()
        {
            if (_panel == null || _heroStarRect == null) return;
            Sprite glare = LoadGlareSprite();
            if (glare == null) { Debug.LogWarning("[StageClear] SpawnGlarePop: VFX_GlareGold not loaded."); return; }
            var go = new GameObject("StarGlare", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)go.transform;
            rt.SetSiblingIndex(_heroStarRect.GetSiblingIndex()); // behind the star
            rt.anchorMin = rt.anchorMax = _heroStarRect.anchorMin;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(520f, 520f);
            rt.anchoredPosition = new Vector2(0f, -16f);
            rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-12f, 12f));
            var img = go.GetComponent<Image>();
            img.sprite = glare; img.preserveAspect = true; img.raycastTarget = false;
            var mat = LoadAdditiveGlowMaterial();
            if (mat != null) img.material = mat;
            img.color = new Color(1f, 0.92f, 0.55f, 0.9f);
            rt.localScale = Vector3.one * 0.3f;
            var sq = DOTween.Sequence();
            sq.Append(rt.DOScale(1.15f, 0.22f).SetEase(Ease.OutQuad));   // snap out (twinkle)
            sq.Append(rt.DOScale(0.9f, 0.45f).SetEase(Ease.InQuad));     // ease down
            sq.Insert(0.12f, img.DOFade(0f, 0.55f).SetEase(Ease.InQuad));
            sq.SetUpdate(true);
            sq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        /// <summary>Glassy BUBBLE that pops + expands + fades behind the star. The VFX_Circle_out texture is a FILLED
        /// sphere, so alone it reads as a solid disc — so we build a real bubble: a very faint see-through interior
        /// (NORMAL blend) plus a bright additive RIM (the ring texture) for the glassy edge. 2026-07-14 Spencer.</summary>
        private void SpawnBubbleBurst()
        {
            if (_panel == null || _heroStarRect == null) return;
            Sprite fill = LoadBubbleSprite(); // VFX_Circle_out — soft filled sphere
            Sprite rim  = LoadRingSprite();   // VFX_Circle_3  — thin ring (the glassy edge)
            if (fill == null && rim == null) { Debug.LogWarning("[StageClear] SpawnBubbleBurst: bubble sprites not loaded."); return; }
            var go = new GameObject("StarBubble", typeof(RectTransform));
            go.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)go.transform;
            rt.SetSiblingIndex(_heroStarRect.GetSiblingIndex()); // behind the star
            rt.anchorMin = rt.anchorMax = _heroStarRect.anchorMin;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = Vector2.zero;
            rt.anchoredPosition = new Vector2(0f, -16f); // match the glow/star visual centre
            var cg = go.AddComponent<CanvasGroup>();
            cg.alpha = 0.62f; // whole bubble a bit dimmer (2026-07-15 Spencer) — fades from here to 0
            var mat = LoadAdditiveGlowMaterial();
            // Faint TRANSLUCENT interior — normal blend, very low alpha, so it's see-through (not a solid disc). Gold.
            if (fill != null) AddSparkleLayer(rt, fill, 320f, new Color(1f, 0.84f, 0.40f, 0.14f), null);
            // Glassy RIM — additive ring, brighter, so the bubble reads as an edge/sheen, not a fill. Gold.
            if (rim != null)  AddSparkleLayer(rt, rim, 340f, new Color(1f, 0.82f, 0.34f, 0.65f), mat);
            rt.localScale = Vector3.one * 0.45f;
            var sq = DOTween.Sequence();
            sq.Append(rt.DOScale(1.30f, 0.55f).SetEase(Ease.OutCubic)); // expand outward
            sq.Join(cg.DOFade(0f, 0.55f).SetEase(Ease.InQuad));          // fade the whole bubble as it grows
            sq.SetUpdate(true);
            sq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        /// <summary>Faint layered gold SHOCKWAVE rings expanding way off-screen (VFX_Circle_3, additive). 2026-07-14.</summary>
        private void SpawnShockwave()
        {
            if (_panel == null || _heroStarRect == null) return;
            Sprite ring = LoadRingSprite();
            if (ring == null) { Debug.LogWarning("[StageClear] SpawnShockwave: VFX_Circle_3 not loaded."); return; }
            var mat = LoadAdditiveGlowMaterial();
            var go = new GameObject("Shockwave", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)go.transform;
            rt.SetSiblingIndex(_heroStarRect.GetSiblingIndex()); // behind the star
            rt.anchorMin = rt.anchorMax = _heroStarRect.anchorMin;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(200f, 200f);
            rt.anchoredPosition = new Vector2(0f, -16f);
            var img = go.GetComponent<Image>();
            img.sprite = ring; img.preserveAspect = true; img.raycastTarget = false;
            if (mat != null) img.material = mat;
            img.color = new Color(1f, 0.85f, 0.30f, 0.45f); // gold, faint
            rt.localScale = Vector3.one * 0.2f;
            var sq = DOTween.Sequence();
            sq.Append(rt.DOScale(9f, 0.7f).SetEase(Ease.OutCubic));             // expand ~200px → ~1800px (off screen)
            sq.Join(img.DOFade(0f, 0.7f).SetEase(Ease.OutQuad));                // fade as it expands
            sq.SetUpdate(true);
            sq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        private static Sprite[] _goldStarSprites; private static bool _goldStarTried;
        private Sprite[] LoadGoldStarSprites()
        {
            if (_goldStarTried) return _goldStarSprites;
            _goldStarTried = true;
            var list = new System.Collections.Generic.List<Sprite>();
            foreach (var n in new[] { "Particles/hcStar01", "Particles/hcStar02" })
            {
                var s = Resources.Load<Sprite>(n);
                if (s == null)
                {
                    var tex = Resources.Load<Texture2D>(n);
                    if (tex != null) s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                }
                if (s != null) list.Add(s);
            }
            _goldStarSprites = list.Count > 0 ? list.ToArray() : null;
            return _goldStarSprites;
        }

        /// <summary>Small GOLD additive star puffs (hcStar01) that blast out with the sparkles and fade FAST — quicker
        /// than the other particles. 2026-07-15 Spencer.</summary>
        private void SpawnGoldStars()
        {
            if (_panel == null || _heroStarRect == null) return;
            Sprite[] stars = LoadGoldStarSprites();
            if (stars == null) { Debug.LogWarning("[StageClear] SpawnGoldStars: hcStar sprites not loaded."); return; }
            Material addMat = LoadAdditiveGlowMaterial();
            const int COUNT = 24;
            int behindStar = _heroStarRect.GetSiblingIndex();
            Vector2 center = _heroStarRect.anchoredPosition;
            float TWO_PI = Mathf.PI * 2f;
            for (int i = 0; i < COUNT; i++)
            {
                var go = new GameObject("GoldStar", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)go.transform;
                rt.SetSiblingIndex(behindStar);
                rt.anchorMin = rt.anchorMax = _heroStarRect.anchorMin;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                float sz = Random.Range(8f, 42f); // VERY small → varied
                rt.sizeDelta = new Vector2(sz, sz);
                rt.anchoredPosition = center;
                rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                var img = go.GetComponent<Image>();
                img.sprite = stars[Random.Range(0, stars.Length)]; img.raycastTarget = false; img.preserveAspect = true;
                if (addMat != null) img.material = addMat;      // ADDITIVE
                img.color = new Color(1f, 0.80f, 0.28f, 1f);    // GOLDEN

                float ang = Random.Range(0f, TWO_PI);                               // RADIAL launch direction
                float dist = Random.Range(320f, 760f);                              // radial reach — equal in every direction
                float dirX = Mathf.Cos(ang), dirY = Mathf.Sin(ang);
                float fallDepth = Random.Range(320f, 640f);
                float lifetime = Random.Range(0.55f, 0.95f);                        // SHORT — fades quick
                float finalScale = Random.Range(0.7f, 1.1f);
                float spinDeg = (Random.value < 0.5f ? 1f : -1f) * Random.Range(120f, 480f);
                const float B_T = 0.20f;
                rt.localScale = Vector3.one * 0.1f;
                Vector2 c0 = center;

                var sq = DOTween.Sequence();
                sq.Append(DOTween.To(() => 0f, u =>
                {
                    if (rt == null) return;
                    float bp = Mathf.Clamp01(u * lifetime / B_T);
                    float burst = dist * (1f - Mathf.Pow(1f - bp, 6f));             // radial explosive burst FROM THE CENTRE
                    float down = fallDepth * Mathf.SmoothStep(0f, 1f, u);
                    rt.anchoredPosition = c0 + new Vector2(dirX * burst, dirY * burst - down);
                }, 1f, lifetime).SetEase(Ease.Linear));
                sq.Insert(0f, rt.DOScale(finalScale, 0.14f).SetEase(Ease.OutBack, 2.2f));
                sq.Insert(0f, rt.DOLocalRotate(new Vector3(0f, 0f, spinDeg), lifetime, RotateMode.LocalAxisAdd).SetEase(Ease.Linear));
                sq.Insert(lifetime * 0.30f, img.DOFade(0f, lifetime * 0.70f).SetEase(Ease.InQuad)); // fade EARLY + quick
                sq.SetUpdate(true);
                sq.OnComplete(() => { if (go != null) Destroy(go); });
            }
        }

        private static Sprite _blickSprite; private static bool _blickTried;
        private Sprite LoadBlickSprite()
        {
            if (_blickTried) return _blickSprite;
            _blickTried = true;
            _blickSprite = Resources.Load<Sprite>("Particles/VFX_Blick_1");
            if (_blickSprite == null)
            {
                var tex = Resources.Load<Texture2D>("Particles/VFX_Blick_1");
                if (tex != null) _blickSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _blickSprite;
        }

        /// <summary>Radial burst of 4-point sparkles (VFX_Blick_1) that explode from the star centre on impact, tinted to
        /// the CONFETTI palette (normal blend = true colour) so they match the confetti. 2026-07-16 Spencer.</summary>
        private void SpawnBlickBurst()
        {
            if (_panel == null || _heroStarRect == null) return;
            var sprite = LoadBlickSprite();
            if (sprite == null) { Debug.LogWarning("[StageClear] SpawnBlickBurst: VFX_Blick_1 not loaded."); return; }
            const int COUNT = 20;
            int behindStar = _heroStarRect.GetSiblingIndex();
            Vector2 center = _heroStarRect.anchoredPosition;
            float TWO_PI = Mathf.PI * 2f;
            for (int i = 0; i < COUNT; i++)
            {
                var go = new GameObject("Blick", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)go.transform;
                rt.SetSiblingIndex(behindStar);
                rt.anchorMin = rt.anchorMax = _heroStarRect.anchorMin;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                float sz = Random.Range(18f, 54f);
                rt.sizeDelta = new Vector2(sz, sz);
                rt.anchoredPosition = center;
                rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                var img = go.GetComponent<Image>();
                img.sprite = sprite; img.raycastTarget = false; img.preserveAspect = true;
                img.color = CONFETTI_PALETTE[Random.Range(0, CONFETTI_PALETTE.Length)]; // MATCH the confetti colours

                float ang = Random.Range(0f, TWO_PI);                                // RADIAL launch direction
                float dist = Random.Range(150f, 400f);                              // radial reach — pulled in (2026-07-16)
                float dirX = Mathf.Cos(ang), dirY = Mathf.Sin(ang);
                float lifetime = Random.Range(0.55f, 0.9f);
                float finalScale = Random.Range(0.7f, 1.15f);
                float spinDeg = (Random.value < 0.5f ? 1f : -1f) * Random.Range(90f, 360f);
                const float B_T = 0.18f;
                rt.localScale = Vector3.one * 0.1f;
                Vector2 c0 = center;

                var sq = DOTween.Sequence();
                sq.Append(DOTween.To(() => 0f, u =>
                {
                    if (rt == null) return;
                    float bp = Mathf.Clamp01(u * lifetime / B_T);
                    float burst = dist * (1f - Mathf.Pow(1f - bp, 6f));             // PURE radial explosion outward — NO gravity
                    rt.anchoredPosition = c0 + new Vector2(dirX * burst, dirY * burst);
                }, 1f, lifetime).SetEase(Ease.Linear));
                sq.Insert(0f, rt.DOScale(finalScale, 0.16f).SetEase(Ease.OutBack, 2.2f));
                sq.Insert(0f, rt.DOLocalRotate(new Vector3(0f, 0f, spinDeg), lifetime, RotateMode.LocalAxisAdd).SetEase(Ease.Linear));
                sq.Insert(lifetime * 0.35f, img.DOFade(0f, lifetime * 0.65f).SetEase(Ease.InQuad));
                sq.SetUpdate(true);
                sq.OnComplete(() => { if (go != null) Destroy(go); });
            }
        }

        /// <summary>Confetti burst — colored ribbon curls (confetti_large sheet, 4 colours) pop up + out from the star,
        /// then tumble down past the bottom with gravity, fading out. The celebratory layer CC has and we were missing.
        /// 2026-07-14 Spencer.</summary>
        // Screen-palette confetti tints (blue → lavender → pink + gold + white + a mint accent). 2026-07-15 Spencer.
        private static readonly Color[] CONFETTI_PALETTE =
        {
            new Color(1.00f, 0.90f, 0.12f), // vivid yellow
            new Color(0.28f, 0.92f, 0.28f), // vivid green
            new Color(0.15f, 0.88f, 0.98f), // cyan
            new Color(0.98f, 0.22f, 0.85f), // magenta
            new Color(1.00f, 0.45f, 0.12f), // orange
            new Color(0.58f, 0.32f, 0.98f), // purple
        }; // 2026-07-16 Spencer — bright & saturated (was pastel) to match the reference confetti

        private void SpawnConfetti()
        {
            Sprite[] sprites = LoadConfettiSprites();
            if (sprites == null) { Debug.LogWarning("[StageClear] SpawnConfetti: confetti_large not loaded."); return; }
            SpawnConfettiPieces(sprites, null, 16); // the ribbon curls (their own baked colours)
        }

        /// <summary>Second confetti layer — white paper flakes (VFX_Confetti_2) TINTED to the screen's palette. 2026-07-15.</summary>
        private void SpawnConfettiColorful()
        {
            Sprite[] sprites = LoadConfetti2Sprites();
            if (sprites == null) { Debug.LogWarning("[StageClear] SpawnConfettiColorful: VFX_Confetti_2 not loaded."); return; }
            SpawnConfettiPieces(sprites, CONFETTI_PALETTE, 32);
        }

        /// <summary>Confetti spawner: <paramref name="palette"/> null = use the sprites' baked colours; otherwise tint
        /// each piece to a random palette colour. Same violent-burst → gravity-arc → tumble motion. 2026-07-15.</summary>
        private void SpawnConfettiPieces(Sprite[] sprites, Color[] palette, int count)
        {
            if (_panel == null || _heroStarRect == null || sprites == null) return;
            Vector2 center = _heroStarRect.anchoredPosition;
            for (int i = 0; i < count; i++)
            {
                Sprite sp = sprites[Random.Range(0, sprites.Length)];
                if (sp == null) continue;
                var cGO = new GameObject("Confetti", typeof(RectTransform), typeof(Image));
                cGO.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)cGO.transform;
                // ~1/3 of the flakes render IN FRONT of the star (sibling AFTER it) so confetti overlaps it for depth;
                // the rest stay behind. Read the star's index fresh each piece (earlier inserts shift it). 2026-07-15.
                int starIdx = _heroStarRect.GetSiblingIndex();
                bool inFront = Random.value < 0.35f;
                rt.SetSiblingIndex(inFront ? starIdx + 1 : starIdx);
                rt.anchorMin = _heroStarRect.anchorMin; rt.anchorMax = _heroStarRect.anchorMax;
                rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
                float sz = Random.Range(24f, 46f);
                // Boxy confetti: always RECTANGULAR (height 35–60% of width) — no squares. 2026-07-16 Spencer.
                rt.sizeDelta = new Vector2(sz, sz * Random.Range(0.35f, 0.6f));
                rt.anchoredPosition = center;
                rt.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
                var img = cGO.GetComponent<Image>();
                img.sprite = sp; img.raycastTarget = false; img.preserveAspect = true; // NORMAL blend — keep colours true
                if (palette != null && palette.Length > 0) img.color = palette[Random.Range(0, palette.Length)];

                // ── REAL CONFETTI FLUTTER (2026-07-15 Spencer) ──────────────────────────────────────────────────────
                // Confetti FLOATS: a small outward pop, then a SLOW drift down with a side-to-side flutter (sway), a
                // flat-paper edge-flip, and a gentle tumble. Emulates a particle system's gravity + drag + noise.
                float ang = Random.Range(0f, Mathf.PI * 2f);
                float spread = Mathf.Cos(ang) * Random.Range(120f, 420f);           // outward fan as it shoots up
                float peakRise = Random.Range(430f, 720f);                          // how high the FAST burst carries it (some near the top)
                float fallDist = Random.Range(950f, 1350f);                         // how far below it then floats down to
                float lifetime = Random.Range(2.8f, 4.6f);                          // SLOW float DOWN after the fast burst
                float swayAmp = Random.Range(28f, 80f);                             // side-to-side flutter width (wider)
                float swayFreq = Random.Range(1.4f, 3.4f);                          // flutter cycles (back to original sway rate, 2026-07-16)
                float swayPhase = Random.Range(0f, Mathf.PI * 2f);
                float flipFreq = Random.Range(3.6f, 8.5f);                          // flat-paper edge-flip cycles — MORE flipping (2026-07-16)
                float flipPhase = Random.Range(0f, Mathf.PI * 2f);
                float tumble = Random.Range(120f, 520f) * (Random.value < 0.5f ? 1f : -1f); // gentle spin
                const float POP_T = 0.28f;                                          // the fast burst (out + UP) finishes this fast
                Vector2 c0 = center;
                float TWO_PI = Mathf.PI * 2f;

                var sq = DOTween.Sequence();
                sq.Append(DOTween.To(() => 0f, u =>
                {
                    if (rt == null) return;
                    float pp = Mathf.Clamp01(u * lifetime / POP_T);                 // pop progress (real-time)
                    float x = spread * (1f - Mathf.Pow(1f - pp, 2f))                // gentle outward pop
                            + swayAmp * Mathf.Sin(u * swayFreq * TWO_PI + swayPhase); // FLUTTER sway
                    float y = peakRise * (1f - Mathf.Pow(1f - pp, 3f))             // FAST burst UP to the peak (in POP_T)...
                            - fallDist * u;                                        // ...then SLOW linear float DOWN over the life
                    rt.anchoredPosition = c0 + new Vector2(x, y);
                    // flat-paper edge-flip: the width narrows to edge-on then opens back out.
                    float flip = 0.30f + 0.70f * Mathf.Abs(Mathf.Cos(u * flipFreq * TWO_PI + flipPhase));
                    rt.localScale = new Vector3(flip, 1f, 1f);
                }, 1f, lifetime).SetEase(Ease.Linear));
                sq.Insert(0f, rt.DOLocalRotate(new Vector3(0f, 0f, tumble), lifetime, RotateMode.LocalAxisAdd).SetEase(Ease.Linear));
                sq.Insert(lifetime - 0.7f, img.DOFade(0f, 0.7f).SetEase(Ease.InQuad));
                sq.SetUpdate(true);
                sq.OnComplete(() => { if (cGO != null) Destroy(cGO); });
            }
        }

        /// <summary>Slices confetti_large (a 2×2 sheet of green/blue/red/yellow ribbon curls) into 4 tintable sprites.
        /// Texture origin is bottom-left, so the top row is the upper half of the rect. 2026-07-14 Spencer.</summary>
        private static Sprite[] _confettiSprites; private static bool _confettiTried;
        private Sprite[] LoadConfettiSprites()
        {
            if (_confettiTried) return _confettiSprites;
            _confettiTried = true;
            Texture2D tex = Resources.Load<Texture2D>("Particles/confetti_large");
            if (tex == null) return _confettiSprites;
            float hw = tex.width * 0.5f, hh = tex.height * 0.5f;
            var piv = new Vector2(0.5f, 0.5f);
            _confettiSprites = new Sprite[]
            {
                Sprite.Create(tex, new Rect(0f,  hh, hw, hh), piv, 100f), // TL green
                Sprite.Create(tex, new Rect(hw, hh, hw, hh), piv, 100f),  // TR blue
                Sprite.Create(tex, new Rect(0f,  0f, hw, hh), piv, 100f), // BL red
                Sprite.Create(tex, new Rect(hw, 0f, hw, hh), piv, 100f),  // BR yellow
            };
            return _confettiSprites;
        }

        /// <summary>Slices VFX_Confetti_2 (a 4×4 sheet of WHITE paper flakes) into 16 tintable sprites. 2026-07-15.</summary>
        private static Sprite[] _confetti2Sprites; private static bool _confetti2Tried;
        private Sprite[] LoadConfetti2Sprites()
        {
            if (_confetti2Tried) return _confetti2Sprites;
            _confetti2Tried = true;
            // 2026-07-16 Spencer: BOXY confetti only — a single SOLID WHITE square sprite, tinted per-piece to the vivid
            // palette. (The confetti4x4/VFX sheets had curly/irregular shapes; the RectTransform gives the square/rect shape.)
            var t = Texture2D.whiteTexture;
            _confetti2Sprites = new Sprite[]
            {
                Sprite.Create(t, new Rect(0f, 0f, t.width, t.height), new Vector2(0.5f, 0.5f), 100f)
            };
            return _confetti2Sprites;
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

            // Leave the star GLOW visible — it's a child of _panel, so the panel's CanvasGroup fade now carries it out
            // smoothly with everything else (no more instant pop-off). Just stop its tweens so it holds steady while it
            // fades. 2026-07-14 Spencer.
            if (_heroGlowRect != null) _heroGlowRect.DOKill();
            if (_heroGlowImage != null) _heroGlowImage.DOKill();

            // MAP FLOW: IsDismissing is now true → the auto-installer (ObjectiveManager) presents the world map, which
            // fades in BELOW this (still-opaque) beige — covering the board. After it's covered, FADE the beige out to
            // reveal the map: a clean fade to the level map with NO board flash. 2026-07-14 Spencer.
            if (LevelMapPanel.MapFlowEnabled)
            {
                if (_overlayImage != null) _overlayImage.raycastTarget = false;
                DOVirtual.DelayedCall(0.38f, () => // wait for the map to finish fading in behind the beige (~0.30s)
                {
                    if (_overlayImage != null) _overlayImage.DOKill();
                    if (_panelGroup != null) _panelGroup.DOKill();
                    const float FADE = 0.30f;
                    if (_dimOutImage != null) { _dimOutImage.transform.SetAsLastSibling(); _dimOutImage.DOKill(); _dimOutImage.DOFade(0.5f, FADE).SetEase(Ease.InQuad).SetUpdate(true); }
                    Tween ft = null;
                    if (_overlayImage != null) ft = _overlayImage.DOFade(0f, FADE).SetEase(Ease.InQuad).SetUpdate(true);
                    if (_panelGroup != null) { var pt = _panelGroup.DOFade(0f, FADE).SetEase(Ease.InQuad).SetUpdate(true); if (ft == null) ft = pt; }
                    if (ft != null) ft.OnComplete(FinalizeDismiss); else FinalizeDismiss();
                }).SetUpdate(true);
                return;
            }

            // Pause + music restore are deferred to the PopOut callback so the
            // overlay-pause stays ON across the dismiss animation (no slip
            // window where Survival timers re-advance / HandManager input
            // could fire). The callback also checks whether the queue still
            // has another stage-clear pending — if so, it skips the restore
            // entirely and Show() of the next modal keeps the pause + swaps
            // music back to victory.
            if (_panel != null)
            {
                // 2026-07-14 Spencer: dismiss FADES the whole beige screen + content out (a clean transition to the
                // level map), instead of flying the panel off-screen.
                _panel.transform.DOKill();
                if (_overlayImage != null) _overlayImage.DOKill();
                const float FADE = 0.28f;
                if (_dimOutImage != null) { _dimOutImage.transform.SetAsLastSibling(); _dimOutImage.DOKill(); _dimOutImage.DOFade(0.5f, FADE).SetEase(Ease.InQuad); }
                Tween t = null;
                if (_overlayImage != null) t = _overlayImage.DOFade(0f, FADE).SetEase(Ease.InQuad);
                if (_panelGroup != null) { var pt = _panelGroup.DOFade(0f, FADE).SetEase(Ease.InQuad); if (t == null) t = pt; }
                if (t != null) t.OnComplete(FinalizeDismiss);
                else FinalizeDismiss();
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

            // Map-in-loop: the between-level MAP takes over from here — it holds the board paused, swaps to map
            // music, and (on the unlock level) shows the unlock reward ON the map before the play modal. So DON'T
            // resume, start the survival track, OR show the unlock over the board here — the map owns all of it.
            // Order becomes: cleared → MAP → unlocked → play. This check MUST come before the unlock branch below.
            // 2026-07-13 Spencer.
            if (LevelMapPanel.MapFlowEnabled)
                return;

            // After the tutorial level that UNLOCKS Swap (the level just before SWAP_UNLOCK_LEVEL), hand off to
            // the Unlock reward modal INSTEAD of resuming — it keeps the overlay paused and resumes/advances on
            // Claim. Royal-Match cadence: cleared celebration FIRST, then the unlock reward. 2026-07-06 Spencer.
            if (_clearedStage == TutorialLocks.EDIT_UNLOCK_LEVEL - 1 && UnlockModal.Instance != null)
            {
                UnlockModal.Instance.Show("Swap", "Swap tiles on the board to make new words!",
                    LoadIconSprite("Tiles/Icon_ItemIcon_Energy"));   // 2026-07-10 Spencer: energy icon for the SWAP unlock
                return;
            }

            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(false);

            // Resume gameplay music. PlaySurvivalMusic picks a fresh random
            // track from the pool (won't restart the same victory clip).
            GameAudio.Instance?.PlaySurvivalMusic();
        }

        // Some Tiles/*.Png icons (Coin, Treasure, the ItemIcon set) are imported as plain Textures rather than
        // Sprites, so Resources.Load<Sprite> returns null — fall back to building a Sprite from the Texture so the
        // icon still shows. 2026-07-10 Spencer.
        private static Sprite LoadIconSprite(string path)
        {
            var s = Resources.Load<Sprite>(path);
            if (s == null)
            {
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null)
                    s = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return s;
        }

        // ── UI construction ─────────────────────────────────────────────────────

        /// <summary>Builds a 1×N vertical gradient sprite (row 0 = bottom colour → top row = top colour). Bilinear +
        /// clamp so it stretches smoothly to full screen. 2026-07-14 Spencer.</summary>
        private static Sprite MakeVerticalGradientSprite(Color top, Color mid, Color bottom, int height = 256)
        {
            var tex = new Texture2D(1, height, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            for (int y = 0; y < height; y++)
            {
                float t = y / (float)(height - 1);                        // 0 bottom → 1 top
                Color c = t < 0.5f ? Color.Lerp(bottom, mid, t * 2f)      // bottom half: pink → lavender
                                   : Color.Lerp(mid, top, (t - 0.5f) * 2f); // top half:    lavender → blue
                tex.SetPixel(0, y, c);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, 1, height), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Radial vignette sprite: transparent centre → black at the corners. Stretched full-screen it
        /// becomes an ellipse that darkens the edges, focusing the eye on the star. 2026-07-14 Spencer.</summary>
        private static Sprite MakeVignetteSprite(float maxAlpha = 0.5f, float inner = 0.40f, float outer = 1.0f, int size = 256)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            float c = (size - 1) * 0.5f;
            float maxDist = Mathf.Sqrt(c * c + c * c); // centre → corner
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / maxDist; // 0 centre → 1 corner
                    float a = Mathf.SmoothStep(inner, outer, d) * maxAlpha;
                    tex.SetPixel(x, y, new Color(0f, 0f, 0f, a));
                }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        /// <summary>Soft radial sprite with the falloff in the ALPHA (opaque centre → transparent edge) — unlike
        /// Particles/soft_circle whose radial is in RGB with an opaque-square alpha (renders as a box on normal blend).
        /// White RGB so a colour tint controls the look. 2026-07-14 Spencer.</summary>
        private static Sprite _softRadialCache;
        private static Sprite MakeSoftRadialSprite(int size = 128)
        {
            if (_softRadialCache != null) return _softRadialCache;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = x - c, dy = y - c;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) / c); // 0 centre → 1 edge
                    float a = 1f - Mathf.SmoothStep(0f, 1f, d);                  // smooth opaque→transparent
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            tex.Apply();
            _softRadialCache = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _softRadialCache;
        }

        private void BuildUI()
        {
            GameObject canvasGO = new GameObject("StageClearCanvas");
            canvasGO.transform.SetParent(transform, false);

            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 180; // ABOVE the level map (170) + play/unlock/area modals, so on dismiss the map
                                        // can fade in BELOW this beige and the beige fades out to reveal it (no board
                                        // flash). 2026-07-14 Spencer.

            CanvasScaler scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGO.AddComponent<GraphicRaycaster>();

            // (gradient sprite helper: MakeVerticalGradientSprite, below)
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
            // Whole screen washes to a vertical BLUE gradient (lighter at top → deeper at bottom) — no card. The sprite
            // carries the gradient; the Image tint stays white so DOFade on the alpha still fades the whole thing in/out.
            // 2026-07-14 Spencer.
            _overlayImage.sprite = MakeVerticalGradientSprite(SKY_TOP, SKY_MID, SKY_BOTTOM);
            _overlayImage.type = Image.Type.Simple;
            _overlayImage.color = new Color(1f, 1f, 1f, 1f);
            _overlayImage.raycastTarget = true;
            // No card + no button anymore: the whole beige screen is the tap-to-continue target. 2026-07-14 Spencer.
            var overlayTapBtn = overlay.AddComponent<Button>();
            overlayTapBtn.transition = Selectable.Transition.None;
            overlayTapBtn.onClick.AddListener(OnContinuePressed);

            // Impact FLASH — a full-screen white pop on the star's landing (Feel-style "Flash"). Brought to the top
            // sibling at flash time so it covers everything for a frame. 2026-07-15 Spencer.
            var flashGO = new GameObject("ImpactFlash", typeof(RectTransform), typeof(Image));
            flashGO.transform.SetParent(canvasGO.transform, false);
            var fRT = (RectTransform)flashGO.transform;
            fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one; fRT.offsetMin = Vector2.zero; fRT.offsetMax = Vector2.zero;
            _flashImage = flashGO.GetComponent<Image>();
            _flashImage.color = new Color(1f, 1f, 1f, 0f);
            _flashImage.raycastTarget = false;

            // EXIT DIM — a full-screen BLACK tint that fades IN on dismiss, so tapping-to-continue DARKENS the screen as
            // it transitions out. Kept at alpha 0 normally; brought topmost + faded in by Dismiss(). 2026-07-15 Spencer.
            var dimGO = new GameObject("ExitDim", typeof(RectTransform), typeof(Image));
            dimGO.transform.SetParent(canvasGO.transform, false);
            var dRT = (RectTransform)dimGO.transform;
            dRT.anchorMin = Vector2.zero; dRT.anchorMax = Vector2.one; dRT.offsetMin = Vector2.zero; dRT.offsetMax = Vector2.zero;
            _dimOutImage = dimGO.GetComponent<Image>();
            _dimOutImage.color = new Color(0f, 0f, 0f, 0f);
            _dimOutImage.raycastTarget = false;

            // Centered card — Candy Crush level-complete proportions
            // (~88% screen width, ~58% screen height). Larger than the previous
            // mini-modal sizing so the celebration has room to breathe and
            // there's space for v1.5 modifier-pick cards without a re-layout.
            _panel = new GameObject("Card");
            _panel.transform.SetParent(canvasGO.transform, false);
            RectTransform pRT = _panel.AddComponent<RectTransform>();
            // FULL-SCREEN container (2026-07-14 Spencer): so each element can be placed at an ABSOLUTE screen fraction
            // matching the Candy-Crush "Level completed" layout (title near top, star mid-upper, tap near bottom).
            pRT.anchorMin = new Vector2(0f, 0f);
            pRT.anchorMax = new Vector2(1f, 1f);
            pRT.offsetMin = Vector2.zero;
            pRT.offsetMax = Vector2.zero;
            Image pImg = _panel.AddComponent<Image>();
            pImg.color = new Color(0f, 0f, 0f, 0f); // TRANSPARENT — no card; content sits directly on the beige screen
            pImg.raycastTarget = false;             // taps fall through to the overlay (tap anywhere to continue)
            _panelGroup = _panel.AddComponent<CanvasGroup>(); // fades all the content together on the exit

            // Vignette — darkens the screen edges over the blue gradient, focusing the eye on the star. First panel
            // child = BEHIND all the content (text/star/glow) but above the gradient overlay. Fades with _panelGroup on
            // dismiss; its own alpha is driven in with the backdrop by AnimateEntrance. 2026-07-14 Spencer.
            var vignetteGO = new GameObject("Vignette");
            vignetteGO.transform.SetParent(_panel.transform, false);
            var vRT = vignetteGO.AddComponent<RectTransform>();
            vRT.anchorMin = Vector2.zero; vRT.anchorMax = Vector2.one;
            vRT.offsetMin = Vector2.zero; vRT.offsetMax = Vector2.zero;
            _vignetteImage = vignetteGO.AddComponent<Image>();
            _vignetteImage.sprite = MakeVignetteSprite(0.34f, 0.62f, 1.0f); // lighter; bigger clear centre; darkening concentrated toward corners
            _vignetteImage.type = Image.Type.Simple;
            _vignetteImage.preserveAspect = false; // stretch to the screen (elliptical vignette)
            _vignetteImage.raycastTarget = false;
            _vignetteImage.color = new Color(1f, 1f, 1f, 0f); // faded in by AnimateEntrance

            // Central ILLUMINATION — a big soft warm-white bloom that lights up the middle of the screen (the "why does
            // theirs look so lit up" effect). Additive so it ADDS light to the gradient. Sits above the vignette, below
            // all the celebration content. 2026-07-14 Spencer.
            var illumGO = new GameObject("Illumination");
            illumGO.transform.SetParent(_panel.transform, false);
            var iRT = illumGO.AddComponent<RectTransform>();
            iRT.anchorMin = iRT.anchorMax = new Vector2(0.5f, 0.52f); // centered ON the star (heroAnchor)
            iRT.pivot = new Vector2(0.5f, 0.5f);
            iRT.sizeDelta = new Vector2(900f, 900f);
            iRT.anchoredPosition = new Vector2(0f, -16f); // matches the hero glow nudge — stays centered on the star
            _illumImage = illumGO.AddComponent<Image>();
            _illumImage.sprite = LoadGlowStarSprite(); // soft_circle radial
            _illumImage.preserveAspect = true;
            _illumImage.raycastTarget = false;
            var illumMat = LoadAdditiveGlowMaterial();
            if (illumMat != null) _illumImage.material = illumMat;
            _illumImage.color = new Color(1f, 0.86f, 0.42f, 0f); // GOLDEN glow (was warm white), faded in by AnimateEntrance

            // God-rays SUNBURST — a big gold VFX_Rays that fades in with the backdrop and SLOWLY ROTATES behind the
            // star, giving that radiant "lit up" texture (the diagonal streaks in CC). Additive. 2026-07-14 Spencer.
            var raysGO = new GameObject("GodRays");
            raysGO.transform.SetParent(_panel.transform, false);
            _raysRect = raysGO.AddComponent<RectTransform>();
            _raysRect.anchorMin = _raysRect.anchorMax = new Vector2(0.5f, 0.52f);
            _raysRect.pivot = new Vector2(0.5f, 0.5f);
            _raysRect.sizeDelta = new Vector2(600f, 600f); // scaled down a bit (was 760)
            _raysRect.anchoredPosition = new Vector2(0f, -24f); // nudged down onto the star's VISUAL centre (rays read high at rect-centre — star is bottom-heavy)
            _raysImage = raysGO.AddComponent<Image>();
            _raysImage.sprite = LoadRaysSprite();
            _raysImage.preserveAspect = true;
            _raysImage.raycastTarget = false;
            if (illumMat != null) _raysImage.material = illumMat;
            _raysImage.color = new Color(1f, 0.84f, 0.36f, 0f); // gold, faded in by AnimateEntrance
            if (_raysImage.sprite == null) { Destroy(raysGO); _raysImage = null; _raysRect = null; }

            // "Level N" — echo title: three stacked copies inside an empty animated CONTAINER, drawn back→front as
            // SHADOW (dark, offset down) → RIM (fat purple, the true outside stroke) → FACE (clean white). TMP's own
            // Outline only strokes INWARD and its soft Underlay shadow blotches once dilated, so we fake both with
            // real offset/fattened copies. The container holds the CanvasGroup + transform all three ride. 2026-07-15.
            var lvlFont = Resources.Load<TMP_FontAsset>("WendyOne SDF")     // preferred (bake @ pt90/pad15 into Resources)
                       ?? Resources.Load<TMP_FontAsset>("Cartoon SDF(done)") // fallback until Wendy One is baked
                       ?? Resources.Load<TMP_FontAsset>("Fredoka-Bold SDF")
                       ?? Resources.Load<TMP_FontAsset>("NunitoExtraBold SDF");
            var lvlFace   = new Color(255f / 255f, 252f / 255f, 251f / 255f, 1f); // Spencer-tuned near-white face (#FFFCFB)
            var lvlRim    = new Color(44f / 255f, 60f / 255f, 120f / 255f, 1f);   // Spencer-tuned rim blue (#2C3C78)
            var lvlShadow = new Color(24f / 255f, 38f / 255f, 82f / 255f, 1f);    // Spencer-tuned navy shadow (#182652)

            // CONTAINER (empty) — the animated node; CanvasGroup fades all three copies together.
            var lvlGO = new GameObject("LevelHeaderTMP", typeof(RectTransform), typeof(CanvasGroup));
            lvlGO.transform.SetParent(_panel.transform, false);
            var lvlRT = (RectTransform)lvlGO.transform;
            lvlRT.anchorMin = new Vector2(0.04f, 0.83f); lvlRT.anchorMax = new Vector2(0.96f, 0.915f);
            lvlRT.offsetMin = Vector2.zero; lvlRT.offsetMax = Vector2.zero;
            _levelHeaderContainer = lvlRT;
            lvlGO.GetComponent<CanvasGroup>().alpha = 0f;

            // RIBBON backing behind "Level N" — 9-sliced sprite (Tiles/Title_Ribbon02_White_Bg). First child of the
            // container so it renders BEHIND the three text layers and rides the same CanvasGroup fade. Width is fit to
            // the level text in RefreshLevelRibbon() once the number is known. 2026-07-16 Spencer.
            var ribbonSprite = Resources.Load<Sprite>("Tiles/Title_Ribbon02_White_Bg");
            if (ribbonSprite != null)
            {
                var ribGO = new GameObject("LevelRibbon", typeof(RectTransform), typeof(Image));
                ribGO.transform.SetParent(lvlRT, false);
                _levelRibbonRect = (RectTransform)ribGO.transform;
                _levelRibbonRect.anchorMin = _levelRibbonRect.anchorMax = new Vector2(0.5f, 0.5f);
                _levelRibbonRect.pivot = new Vector2(0.5f, 0.5f);
                _levelRibbonRect.anchoredPosition = Vector2.zero;
                _levelRibbonRect.sizeDelta = new Vector2(320f, 140f); // placeholder; RefreshLevelRibbon fits width to text
                _levelRibbonImage = ribGO.GetComponent<Image>();
                _levelRibbonImage.sprite = ribbonSprite;
                _levelRibbonImage.type = Image.Type.Sliced;
                _levelRibbonImage.raycastTarget = false;
                _levelRibbonImage.color = CONFETTI_PALETTE[3]; // tint to a confetti colour (magenta) — swap the index for another
                // MASK — the ribbon clips its children (the corner sheens) to its own shape via stencil, so a sheen can
                // never spill past the ribbon body/tails. showMaskGraphic keeps the ribbon itself visible. 2026-07-17.
                var ribMask = ribGO.AddComponent<Mask>();
                ribMask.showMaskGraphic = true;
                _levelRibbonRect.SetAsFirstSibling(); // behind every text layer (set again below after the light too)

                // SHEEN highlights — the pack's Title_Ribbon02_White_Light holds two small semi-transparent white
                // triangles: one top-left, one bottom-right. CUT each into its own sprite and PIN it flush to the
                // matching body corner at native size (NO stretch). Parented UNDER the ribbon so the Mask clips them to
                // the ribbon shape; positioned in RefreshLevelRibbon (coords relative to the ribbon = same as before).
                var lightSprite = Resources.Load<Sprite>("Tiles/Title_Ribbon02_White_Light");
                if (lightSprite != null && lightSprite.texture != null)
                {
                    var tex = lightSprite.texture; // exact triangle bounds (bottom-left origin): TL (0,39,22,19), BR (33,0,38,21)
                    var tlSprite = Sprite.Create(tex, new Rect(0f, 39f, 22f, 19f), new Vector2(0.5f, 0.5f), 100f);
                    var brSprite = Sprite.Create(tex, new Rect(33f, 0f, 38f, 21f), new Vector2(0.5f, 0.5f), 100f);
                    _levelSheenTL = MakeSheenPiece(_levelRibbonRect, tlSprite, new Vector2(0f, 1f), "LevelSheenTL"); // pivot top-left
                    _levelSheenBR = MakeSheenPiece(_levelRibbonRect, brSprite, new Vector2(1f, 0f), "LevelSheenBR"); // pivot bottom-right
                }

                _levelRibbonRect.SetAsFirstSibling(); // behind every text layer
            }

            // Children in hierarchy order = render order (back→front).
            _levelHeaderShadow  = MakeLevelCopy(lvlRT, lvlFont, "LevelHeaderShadow",  Vector2.zero, "Level 1", 90f); // offsets set below
            _levelHeaderOutline = MakeLevelCopy(lvlRT, lvlFont, "LevelHeaderOutline", Vector2.zero, "Level 1", 90f); // the rim
            _levelHeaderTMP     = MakeLevelCopy(lvlRT, lvlFont, "LevelHeaderFace",    Vector2.zero, "Level 1", 90f); // white face

            // SHADOW — Spencer's Inspector 2026-07-16: rect Left 0 / Top 0 / Right -2 / Bottom -6; font 60, tracking -5,
            // gradient OFF, white vertex. Material (LevelShadow SDF): periwinkle face #23367D, dilate 0.08, underlay off.
            var shRT = (RectTransform)_levelHeaderShadow.transform;
            shRT.offsetMin = new Vector2(0f, -6f); shRT.offsetMax = new Vector2(3f, 0f);
            _levelHeaderShadow.fontSize = 60f;
            _levelHeaderShadow.characterSpacing = -5f;
            _levelHeaderShadow.enableVertexGradient = false;
            _levelHeaderShadow.color = Color.white;

            // RIM offset — blue #2C3C78, dilate 0.59, softness 0, offset (Right -3 / Bottom -3).
            var rimRT = (RectTransform)_levelHeaderOutline.transform;
            rimRT.offsetMin = new Vector2(0f, -3f); rimRT.offsetMax = new Vector2(3f, 0f);

            // FACE-ONLY look — Spencer 2026-07-16: Four Corners gradient (top #FFFFFF, bottom-left #E7E7E7,
            // bottom-right #ECECEC). Outline + shadow layers turned OFF for now (below).
            _levelHeaderTMP.enableVertexGradient = true;
            // Four distinct corner colors below = a Four Corners gradient inherently (TMP_Text has no colorMode setter).
            _levelHeaderTMP.colorGradient = new VertexGradient(
                Color.white, Color.white,                                    // top-left, top-right  #FFFFFF
                new Color(231f / 255f, 231f / 255f, 231f / 255f, 1f),        // bottom-left  #E7E7E7
                new Color(236f / 255f, 236f / 255f, 236f / 255f, 1f));       // bottom-right #ECECEC

            // MATERIAL LOOK — prefer a real material asset in Resources (tune it live in the Inspector; it persists and
            // needs NO code change). Falls back to Spencer's dumped values until the asset exists. 2026-07-15.
            if (!TryAssignSharedMaterial(_levelHeaderShadow, "LevelShadow SDF"))
                ApplyShadowMaterial(_levelHeaderShadow, lvlShadow, 0.54f, 0.827f); // navy, fat, soft
            if (!TryAssignSharedMaterial(_levelHeaderOutline, "LevelRim SDF"))
                ApplyOutlineBackMaterial(_levelHeaderOutline, lvlRim, 0.59f);      // blue rim
            if (!TryAssignSharedMaterial(_levelHeaderTMP, "LevelFace SDF"))
                ApplyFaceMaterial(_levelHeaderTMP, lvlFace);                       // near-white face

            // Face TMP settings per Spencer's Inspector (2026-07-16): font 60, tracking -5.
            _levelHeaderTMP.fontSize = 60f;
            _levelHeaderTMP.characterSpacing = -5f;

            // Outline layer OFF for now (Spencer 2026-07-16); shadow is back ON with its own settings (above).
            _levelHeaderOutline.gameObject.SetActive(false);

            // Title — baked "Well Done!" image (Resources/Tiles/welldone) fitted in a wide band, preserving aspect.
            // Band is taller than the old TMP band so the sprite displays large. 2026-07-17 Spencer.
            BuildTitleLetters(_panel.transform,
                new Vector2(0.05f, 0.66f), new Vector2(0.95f, 0.815f), // top 0.815 stays below the Level ribbon (0.83)
                "Well Done!", 85, WELL_DONE_COL);

            // CELEBRATION STAR (replaces the old "Level Score" + number tally). A golden radial glow
            // behind a single big golden star, centered where the score used to be. Both start hidden/
            // small and are animated in by AnimateStarDrop. 2026-06-23 Spencer.
            Sprite starSprite = LoadStarSprite();
            Vector2 heroAnchor = new Vector2(0.5f, 0.52f); // star + all its FX, nudged down ~15% from 0.665 (2026-07-14 Spencer)

            // Glow behind = a BIGGER gold star01 (same sprite) that scales up behind the hero star —
            // a golden star-shaped halo, per Spencer 2026-06-23 (instead of a soft radial glow).
            GameObject glowGO = new GameObject("HeroGlow");
            glowGO.transform.SetParent(_panel.transform, false);
            _heroGlowRect = glowGO.AddComponent<RectTransform>();
            _heroGlowRect.anchorMin = heroAnchor;
            _heroGlowRect.anchorMax = heroAnchor;
            _heroGlowRect.pivot = new Vector2(0.5f, 0.5f);
            _heroGlowRect.sizeDelta = new Vector2(340f, 340f); // HALVED (was 640) — tighter gold aura around the 190 star (2026-07-14)
            _heroGlowRect.anchoredPosition = new Vector2(0f, -16f); // nudged slightly DOWN onto the star's visual centre
            _heroGlowImage = glowGO.AddComponent<Image>();
            _heroGlowImage.sprite = LoadGlowStarSprite() ?? starSprite; // Particles/soft_circle — soft gold radial aura
            _heroGlowImage.color = HERO_GLOW_GOLD;
            // Fake-bloom: additive blend so the glow ADDS light to what's behind it (Overlay UI can't
            // receive real post-process bloom). 2026-06-23 Spencer.
            Material addMat = LoadAdditiveGlowMaterial();
            if (addMat != null) _heroGlowImage.material = addMat;
            _heroGlowImage.preserveAspect = true;
            _heroGlowImage.raycastTarget = false;

            // Dark TINT behind the star — a soft dark radial at VERY low alpha, "felt not seen": it just barely
            // deepens the area right behind the star so it separates from the bright rays/glow. Uses a proper
            // alpha-radial sprite (soft_circle's radial is in RGB → renders as a box on normal blend). 2026-07-14.
            var shadeGO = new GameObject("StarBackShade");
            shadeGO.transform.SetParent(_panel.transform, false);
            var shadeRT = shadeGO.AddComponent<RectTransform>();
            shadeRT.anchorMin = shadeRT.anchorMax = heroAnchor;
            shadeRT.pivot = new Vector2(0.5f, 0.5f);
            shadeRT.sizeDelta = Vector2.one * (HERO_STAR_SIZE * 1.58f);  // was 300 against a 190 star
            shadeRT.anchoredPosition = new Vector2(0f, -16f);
            _starShadeImage = shadeGO.AddComponent<Image>();
            _starShadeImage.sprite = MakeSoftRadialSprite(); // radial in the ALPHA → soft dark disc, no square
            _starShadeImage.raycastTarget = false;
            _starShadeImage.color = new Color(0.04f, 0.02f, 0.10f, 0f); // deep near-black, faded in low by AnimateEntrance
            // 2026-07-28: re-enabled ONLY because the star is now the rendered 3D art, whose
            // gold mid-tones sit at nearly the same VALUE as the near-white glow core — the
            // silhouette was dissolving into the FX. The dark disc locally darkens the glow so
            // gold never meets white, which buys separation without a painted outline.
            // You turned this off on 2026-07-16 (with the old flat vector star, which didn't
            // have the problem). Set STAR_BACK_SHADE = false to put it straight back.
            _starShadeImage.enabled = STAR_BACK_SHADE && _starIs3D;

            // DROP SHADOW — a soft dark ellipse offset DOWN behind the star, so the star reads as GROUNDED / lifted off
            // the FX instead of floating (the Candy-Crush trophy look). Soft-edged (alpha radial), fades in as the star
            // lands (AnimateStarDrop). Rendered here (before HeroStar) so it sits BEHIND the star. 2026-07-15 Spencer.
            var starShadowGO = new GameObject("HeroStarShadow");
            starShadowGO.transform.SetParent(_panel.transform, false);
            var starShadowRT = starShadowGO.AddComponent<RectTransform>();
            starShadowRT.anchorMin = starShadowRT.anchorMax = heroAnchor;
            starShadowRT.pivot = new Vector2(0.5f, 0.5f);
            starShadowRT.sizeDelta = new Vector2(HERO_STAR_SIZE * 1.10f, HERO_STAR_SIZE * 0.74f); // wider-than-tall = a grounded contact shadow (was 210x140 against a 190 star)
            starShadowRT.anchoredPosition = new Vector2(0f, 0f);        // Spencer-tuned: centered directly behind the star
            _heroStarShadowImage = starShadowGO.AddComponent<Image>();
            _heroStarShadowImage.sprite = MakeSoftRadialSprite();       // soft alpha disc = soft-edged shadow
            _heroStarShadowImage.raycastTarget = false;
            _heroStarShadowImage.preserveAspect = false;               // let it stretch into the wide ellipse
            _heroStarShadowImage.color = new Color(0.05f, 0.03f, 0.12f, 0f); // dark navy-black, faded in by AnimateStarDrop

            // Hero star — in front, centered, drops in.
            GameObject starGO = new GameObject("HeroStar");
            starGO.transform.SetParent(_panel.transform, false);
            _heroStarRect = starGO.AddComponent<RectTransform>();
            _heroStarRect.anchorMin = heroAnchor;
            _heroStarRect.anchorMax = heroAnchor;
            _heroStarRect.pivot = new Vector2(0.5f, 0.5f);
            _heroStarRect.sizeDelta = new Vector2(HERO_STAR_SIZE, HERO_STAR_SIZE);
            _heroStarRect.anchoredPosition = Vector2.zero;
            _heroStarImage = starGO.AddComponent<Image>();
            _heroStarImage.sprite = starSprite;
            _heroStarImage.color = _starIs3D ? Color.white : HERO_STAR_GOLD;
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
            rowRT.anchorMin = new Vector2(0.06f, 0.42f);
            rowRT.anchorMax = new Vector2(0.94f, 0.50f);
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

            // "Tap To Continue" prompt — no button; the whole beige screen is the tap target. Reuses the _btnContinue
            // fields so the existing staggered fade-in + idle breathe-pulse drive the prompt. Same "tap to continue"
            // feel as the tutorial row-rise stops. 2026-07-14 Spencer.
            // Built as TMP (not legacy Text) so it can use the WendyOne SDF font, matching the Level title. The fade/
            // pulse animations only drive the transform + CanvasGroup, so the component swap is safe. 2026-07-15 Spencer.
            var tapGO = new GameObject("TapPrompt", typeof(RectTransform));
            tapGO.transform.SetParent(_panel.transform, false);
            var tapRT = (RectTransform)tapGO.transform;
            tapRT.anchorMin = new Vector2(0.10f, 0.045f); tapRT.anchorMax = new Vector2(0.90f, 0.125f);
            tapRT.offsetMin = Vector2.zero; tapRT.offsetMax = Vector2.zero;
            var tapTMP = tapGO.AddComponent<TextMeshProUGUI>();
            var tapFont = Resources.Load<TMP_FontAsset>("WendyOne SDF")
                       ?? Resources.Load<TMP_FontAsset>("Cartoon SDF(done)");
            if (tapFont != null) tapTMP.font = tapFont;
            tapTMP.text = "Tap to Continue";
            tapTMP.fontSize = 34f;
            tapTMP.alignment = TextAlignmentOptions.Center;
            tapTMP.enableWordWrapping = false;
            tapTMP.overflowMode = TextOverflowModes.Overflow;
            tapTMP.color = Color.white; // white, low on the screen like Candy-Crush "Tap to skip"
            tapTMP.raycastTarget = false; // taps fall through to the overlay
            // Prefer a tunable material asset (tune it live like the Level layers); fall back to a CRISP white (softness
            // pinned to 0) so it never inherits the WendyOne material's baked softness/glow. 2026-07-15 Spencer.
            if (!TryAssignSharedMaterial(tapTMP, "TapPrompt SDF"))
                ApplyFaceMaterial(tapTMP, Color.white);
            _btnContinue = tapGO;
            _btnContinueGroup = tapGO.AddComponent<CanvasGroup>();

            // 2026-06-23: the "LEVEL N CLEARED!" title is created BEFORE the hero glow, so the big glow
            // (a later sibling) was rendering on top of the title text. Promote the title to the top of
            // the sibling order so it always sits ABOVE the glow. (It doesn't overlap the button/chips,
            // so being topmost is harmless.)
            if (_titleText != null) _titleText.transform.SetAsLastSibling();
            if (_titleRow != null) _titleRow.transform.SetAsLastSibling(); // keep the letter row above the glow
            // Star (+ its shadow) go ABOVE the "Well Done!" lettering so the big drop-in renders in front of it. 2026-07-16.
            if (_heroStarShadowImage != null) _heroStarShadowImage.transform.SetAsLastSibling();
            if (_heroStarRect != null) _heroStarRect.SetAsLastSibling();
        }

        /// <summary>Builds "Well Done!" as a SINGLE-WORD 3-layer echo (shadow / rim / face) — a normal TMP word, so the
        /// spacing is perfect (font engine), with the SAME materials/font/settings as the Level title. FLAT (no curve).
        /// Animated as one word via TossInTMP. 2026-07-16 Spencer.</summary>
        private void BuildTitleLetters(Transform parent, Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            _titleRow = new GameObject("TitleRow", typeof(RectTransform), typeof(CanvasGroup));
            _titleRow.transform.SetParent(parent, false);
            _titleContainer = (RectTransform)_titleRow.transform;
            _titleContainer.anchorMin = anchorMin; _titleContainer.anchorMax = anchorMax;
            _titleContainer.offsetMin = Vector2.zero; _titleContainer.offsetMax = Vector2.zero;
            _titleRow.GetComponent<CanvasGroup>().alpha = 0f;

            // "Well Done!" is now a BAKED image (Resources/Tiles/welldone) — art-directed in Photoshop (face + outline +
            // shadow all flattened), replacing the old 3-layer TMP echo. It fills the container preserving aspect, and a
            // UIWaveMesh gives it a traveling ripple so the letters still "wave". 2026-07-17 Spencer.
            var titleSprite = Resources.Load<Sprite>("Tiles/welldone");
            var imgGO = new GameObject("WellDoneImage", typeof(RectTransform), typeof(Image), typeof(UIWaveMesh));
            imgGO.transform.SetParent(_titleContainer, false);
            var imgRT = (RectTransform)imgGO.transform;
            imgRT.anchorMin = Vector2.zero; imgRT.anchorMax = Vector2.one;
            imgRT.offsetMin = Vector2.zero; imgRT.offsetMax = Vector2.zero;
            _titleImage = imgGO.GetComponent<Image>();
            _titleImage.sprite = titleSprite;
            _titleImage.type = Image.Type.Simple;
            _titleImage.preserveAspect = true; // never distort the baked title; it fits within the container band
            _titleImage.raycastTarget = false;
            _titleImage.enabled = titleSprite != null;
            _titleWave = imgGO.GetComponent<UIWaveMesh>();

            // Legacy TMP layers are gone — null them so any old references no-op.
            _titleFace = null; _titleRim = null; _titleShadow = null;
        }

        /// <summary>Entrance for the baked "Well Done!" title: a punchy scale/rotation slam-in (TossInTMP) plus a wave
        /// sweep that eases down to a gentle ongoing ripple. 2026-07-17 Spencer (replaced the per-character TMP wave).</summary>
        private void PlayWellDoneWave()
        {
            // Reveal (fade) + ONE height-scale sweep across the baked title — no position warp. 2026-07-17 Spencer.
            if (_titleContainer != null)
            {
                _titleContainer.DOKill();
                _titleContainer.localScale = Vector3.one;
                _titleContainer.localRotation = Quaternion.identity;
                var cg = _titleContainer.GetComponent<CanvasGroup>();
                if (cg != null) { cg.DOKill(); cg.alpha = 0f; cg.DOFade(1f, 0.14f).SetEase(Ease.OutQuad).SetUpdate(true); }
            }
            if (_titleWave != null) _titleWave.PlaySweep();
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

#if UNITY_EDITOR
        /// <summary>DEV: logs the exact live values of all three "Level N" title layers in a copy-paste-ready block, so
        /// Spencer can tune the look in the Inspector and hand back precise numbers instead of screenshots. 2026-07-15.</summary>
        /// <summary>DEV: dumps the FIRST "Well Done!" letter's tunable TMP values (face color, size) so Spencer can tune
        /// in the Inspector and hand back exact numbers to bake into every letter. 2026-07-16.</summary>
        private void DumpWellDoneValues()
        {
            var t = _titleFace;
            if (t == null) { Debug.Log("[WellDone] no letters to dump."); return; }
            string Hex(Color c) => $"#{Mathf.RoundToInt(c.r * 255):X2}{Mathf.RoundToInt(c.g * 255):X2}{Mathf.RoundToInt(c.b * 255):X2}{Mathf.RoundToInt(c.a * 255):X2}";
            Debug.Log("===== WELL DONE DUMP (press V) =====");
            Debug.Log($"FACE | color={Hex(t.color)} fontSize={t.fontSize}");
            Debug.Log("===== END WELL DONE DUMP =====");
        }

        private void DumpLevelHeaderValues()
        {
            Debug.Log("===== LEVEL HEADER DUMP (press K) =====");
            DumpLevelLayer("FACE  ", _levelHeaderTMP);
            DumpLevelLayer("RIM   ", _levelHeaderOutline);
            DumpLevelLayer("SHADOW", _levelHeaderShadow);
            Debug.Log("===== END LEVEL HEADER DUMP =====");
        }

        private static void DumpLevelLayer(string tag, TextMeshProUGUI tmp)
        {
            if (tmp == null) { Debug.Log($"{tag} | <null>"); return; }
            var m = tmp.fontMaterial;
            string Hex(Color c) => $"#{Mathf.RoundToInt(c.r * 255):X2}{Mathf.RoundToInt(c.g * 255):X2}{Mathf.RoundToInt(c.b * 255):X2}{Mathf.RoundToInt(c.a * 255):X2}";
            float GF(int id) => m.HasProperty(id) ? m.GetFloat(id) : 0f;
            string faceC = m.HasProperty(ShaderUtilities.ID_FaceColor)    ? Hex(m.GetColor(ShaderUtilities.ID_FaceColor))    : "-";
            string olC   = m.HasProperty(ShaderUtilities.ID_OutlineColor) ? Hex(m.GetColor(ShaderUtilities.ID_OutlineColor)) : "-";
            var rt = (RectTransform)tmp.transform;
            string grad = tmp.enableVertexGradient
                ? $"gradient ON top={Hex(tmp.colorGradient.topLeft)} bottom={Hex(tmp.colorGradient.bottomLeft)}"
                : "gradient off";
            Debug.Log($"{tag} | fontSize={tmp.fontSize:F1} | FaceColor={faceC} FaceDilate={GF(ShaderUtilities.ID_FaceDilate):F3} Softness={GF(ShaderUtilities.ID_OutlineSoftness):F3} | OutlineColor={olC} OutlineWidth={GF(ShaderUtilities.ID_OutlineWidth):F3} | offsetMin={rt.offsetMin} offsetMax={rt.offsetMax} | {grad}");
            if (m.IsKeywordEnabled("UNDERLAY_ON"))
                Debug.Log($"{tag} |   underlay ON: color={Hex(m.GetColor("_UnderlayColor"))} offX={m.GetFloat("_UnderlayOffsetX"):F2} offY={m.GetFloat("_UnderlayOffsetY"):F2} dilate={m.GetFloat("_UnderlayDilate"):F3} softness={m.GetFloat("_UnderlaySoftness"):F3}");
        }
#endif

        /// <summary>Builds one corner-sheen Image (a cut triangle from the ribbon's Light sprite) under the level
        /// container, above the ribbon base but below the text. Pivot sets which corner it pins to. 2026-07-16.</summary>
        private static Material _sheenAdditiveMat; private static bool _sheenAdditiveTried;
        /// <summary>Shared ADDITIVE UI material (Resources/UIAdditive.shader) for the ribbon corner sheens. Cached; null
        /// if the shader is missing (sheens then fall back to normal alpha). 2026-07-17.</summary>
        private static Material GetSheenAdditiveMaterial()
        {
            if (_sheenAdditiveTried) return _sheenAdditiveMat;
            _sheenAdditiveTried = true;
            var sh = Resources.Load<Shader>("UIAdditive") ?? Shader.Find("UI/Additive");
            if (sh != null) _sheenAdditiveMat = new Material(sh) { name = "UIAdditive (sheen)" };
            return _sheenAdditiveMat;
        }

        private RectTransform MakeSheenPiece(RectTransform parent, Sprite sprite, Vector2 pivot, string name)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = pivot;
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.type = Image.Type.Simple;
            img.raycastTarget = false;
            img.color = new Color(1f, 1f, 1f, 0.2f); // faint white gloss (additive)
            var addMat = GetSheenAdditiveMaterial();       // ADDITIVE blend → the white adds light (glossy), not a flat patch
            if (addMat != null) img.material = addMat;
            return rt;
        }

        /// <summary>Sizes the ribbon banner to hug the current "Level N" text — width = text width + tail padding,
        /// fixed height. Called whenever the level label changes. No-op if the ribbon sprite wasn't found. 2026-07-16.</summary>
        private void RefreshLevelRibbon()
        {
            if (_levelRibbonRect == null || _levelHeaderTMP == null) return;
            _levelHeaderTMP.ForceMeshUpdate();
            float textW = _levelHeaderTMP.preferredWidth;
            float w = Mathf.Max(260f, textW + 240f); // wider — text width + generous room past the fishtails
            float h = 62f;                            // banner height (short, stretched proportion)
            _levelRibbonRect.sizeDelta = new Vector2(w, h);
            // Corner sheens: Spencer hand-placed them for "Level 3" (ribbon width 411.24) → TL 30.2px in from the LEFT
            // edge, BR 35.1px in from the RIGHT edge (slightly asymmetric because the two cut triangles differ in size).
            // Pin each that fixed distance from its ribbon edge — the fishtail is a fixed rendered size, so this stays
            // correct as the banner grows for wider level numbers. 2026-07-17 Spencer-measured.
            const float TL_INSET = 30.2f; // from left edge
            const float BR_INSET = 35.1f; // from right edge
            float halfW = w * 0.5f;
            float s = h / 58f;            // banner vs native body height
            if (_levelSheenTL != null)
            {
                _levelSheenTL.sizeDelta = new Vector2(22f * s, 19f * s);
                _levelSheenTL.anchoredPosition = new Vector2(-(halfW - TL_INSET), h * 0.5f); // top-left corner
            }
            if (_levelSheenBR != null)
            {
                _levelSheenBR.sizeDelta = new Vector2(38f * s, 21f * s);
                _levelSheenBR.anchoredPosition = new Vector2(halfW - BR_INSET, -h * 0.5f);   // bottom-right corner
            }
        }

        /// <summary>Applies Spencer's vertical vertex gradient to a title layer: top #FFFFFF → bottom #CCCCCC.
        /// Multiplies the material face color, so every layer picks up a subtle top-lit shading. 2026-07-16.</summary>
        private static void ApplyTitleGradient(TextMeshProUGUI tmp)
        {
            if (tmp == null) return;
            tmp.enableVertexGradient = true;
            Color top = Color.white;                                             // #FFFFFF
            Color bot = new Color(204f / 255f, 204f / 255f, 204f / 255f, 1f);    // #CCCCCC
            tmp.colorGradient = new VertexGradient(top, top, bot, bot);
        }

        /// <summary>If a material asset named `resourceName` exists in Resources, assigns it as the layer's SHARED
        /// material (the real project asset — tuning it in the Inspector persists and shows live on the modal). Returns
        /// true if assigned. Lets Spencer own the material look entirely, with no code round-trips. 2026-07-15.</summary>
        private static bool TryAssignSharedMaterial(TextMeshProUGUI tmp, string resourceName)
        {
            if (tmp == null) return false;
            var m = Resources.Load<Material>(resourceName);
            if (m == null) return false;
            tmp.fontSharedMaterial = m;
            return true;
        }

        /// <summary>Spawns one full-rect TMP copy of the "Level N" title as a child of the container. `offsetPx` shifts
        /// both rect corners for a pure translation (used to offset the shadow copy). All copies share text/font/size so
        /// their glyphs align exactly; the caller applies the per-layer material. 2026-07-15.</summary>
        private static TextMeshProUGUI MakeLevelCopy(RectTransform parent, TMP_FontAsset font, string name, Vector2 offsetPx, string text, float fontSize)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = offsetPx; rt.offsetMax = offsetPx; // both corners equal → translate only (no resize)
            var t = go.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = text;
            t.fontSize = fontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.overflowMode = TextOverflowModes.Overflow;
            t.raycastTarget = false;
            t.color = Color.white;
            return t;
        }

        /// <summary>SHADOW copy of the echo title: dark semi-transparent silhouette, FATTENED to `dilate` (matching the
        /// rim) so it reads as a solid drop shadow. Offset is applied via its RectTransform, not a soft Underlay (which
        /// blotches once dilated). Sits behind the rim. 2026-07-15.</summary>
        private static void ApplyShadowMaterial(TextMeshProUGUI tmp, Color shadow, float dilate, float softness)
        {
            if (tmp == null) return;
            var mat = tmp.fontMaterial; // per-object instance — safe to modify
            if (mat == null) return;
            if (mat.HasProperty(ShaderUtilities.ID_FaceColor))    mat.SetColor(ShaderUtilities.ID_FaceColor, shadow);
            if (mat.HasProperty(ShaderUtilities.ID_FaceDilate))   mat.SetFloat(ShaderUtilities.ID_FaceDilate, dilate);
            if (mat.HasProperty(ShaderUtilities.ID_OutlineWidth)) mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f);
            if (mat.HasProperty(ShaderUtilities.ID_OutlineSoftness)) mat.SetFloat(ShaderUtilities.ID_OutlineSoftness, softness); // Spencer-tuned soft drop
            if (mat.HasProperty("_UnderlayColor")) mat.DisableKeyword("UNDERLAY_ON");
        }

        /// <summary>FRONT copy of the echo title: a clean white FACE with NO stroke (the outside rim is the fat back
        /// copy behind it). Renders on top of the purple back so the white sits inside the rim. 2026-07-15 Spencer.</summary>
        private static void ApplyFaceMaterial(TextMeshProUGUI tmp, Color face)
        {
            if (tmp == null) return;
            var mat = tmp.fontMaterial; // per-object instance — safe to modify
            if (mat == null) return;
            if (mat.HasProperty(ShaderUtilities.ID_FaceColor))   mat.SetColor(ShaderUtilities.ID_FaceColor, face);
            if (mat.HasProperty(ShaderUtilities.ID_FaceDilate))  mat.SetFloat(ShaderUtilities.ID_FaceDilate, -0.07f); // Spencer-tuned
            if (mat.HasProperty(ShaderUtilities.ID_OutlineWidth)) mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f); // rim comes from the back copy
            if (mat.HasProperty(ShaderUtilities.ID_OutlineSoftness)) mat.SetFloat(ShaderUtilities.ID_OutlineSoftness, 0f); // crisp — no baked softness
            if (mat.HasProperty("_UnderlayColor")) mat.DisableKeyword("UNDERLAY_ON"); // shadow lives on the back copy
        }

        /// <summary>BACK copy of the echo title: solid deep-purple, FATTENED by `dilate` so it pokes out past the white
        /// face on every side = a TRUE outside rim (TMP's own Outline only strokes inward). Also carries the soft drop
        /// shadow (Underlay) so it sits beneath the whole title. `dilate` IS the rim thickness — tune it. 2026-07-15.</summary>
        private static void ApplyOutlineBackMaterial(TextMeshProUGUI tmp, Color deep, float dilate)
        {
            if (tmp == null) return;
            var mat = tmp.fontMaterial; // per-object instance — safe to modify
            if (mat == null) return;
            // FACE = the rim colour, fattened outward. This fattened purple is what shows around the white face.
            if (mat.HasProperty(ShaderUtilities.ID_FaceColor))   mat.SetColor(ShaderUtilities.ID_FaceColor, deep);
            if (mat.HasProperty(ShaderUtilities.ID_FaceDilate))  mat.SetFloat(ShaderUtilities.ID_FaceDilate, dilate);
            if (mat.HasProperty(ShaderUtilities.ID_OutlineWidth)) mat.SetFloat(ShaderUtilities.ID_OutlineWidth, 0f);
            if (mat.HasProperty(ShaderUtilities.ID_OutlineSoftness)) mat.SetFloat(ShaderUtilities.ID_OutlineSoftness, 0f); // crisp — no baked softness
            // UNDERLAY OFF — a soft shadow on top of the already-fattened rim overruns the SDF spread and breaks into
            // blotchy purple clouds. The rim alone reads clean. (Add a crisp offset shadow via a 3rd copy if wanted.)
            if (mat.HasProperty("_UnderlayColor")) mat.DisableKeyword("UNDERLAY_ON");
        }

        /// <summary>PUNCHY object-slam entrance for a label/container: fast overshoot past full size → recoil under →
        /// bounce to rest (squash-pop), plus a rotational wobble and a snappy fade. Accepts any Component so it works on
        /// a TMP or an empty container's RectTransform. SetUpdate(true) = unscaled, safe during any hitstop. 2026-07-15.</summary>
        private void TossInTMP(Component tmp)
        {
            if (tmp == null) return;
            Transform t = tmp.transform;
            t.DOKill();
            t.localScale = Vector3.one * 0.05f;                 // start VERY small so the scale-in is dramatic
            t.localRotation = Quaternion.Euler(0f, 0f, 16f);    // tilted a bit more, snaps upright
            var cg = tmp.GetComponent<CanvasGroup>();
            if (cg != null) { cg.DOKill(); cg.alpha = 0f; }

            // Scale: SLAM in — overshoot to 1.32, recoil to 0.90, then bounce to 1.0. Reads as a punchy impact.
            Sequence seq = DOTween.Sequence().SetUpdate(true);
            seq.Append(t.DOScale(1.32f, 0.15f).SetEase(Ease.OutCubic));    // snap up, blow past full size
            seq.Append(t.DOScale(0.90f, 0.09f).SetEase(Ease.InOutQuad));   // recoil under
            seq.Append(t.DOScale(1.00f, 0.16f).SetEase(Ease.OutBack, 3.5f)); // spring to rest
            // Rotation wobble settles with overshoot; fade snaps in fast.
            t.DOLocalRotate(Vector3.zero, 0.32f).SetEase(Ease.OutBack, 4.5f).SetUpdate(true);
            if (cg != null) cg.DOFade(1f, 0.09f).SetEase(Ease.OutQuad).SetUpdate(true);
        }

        private static void HideTMP(Component tmp)
        {
            if (tmp == null) return;
            var cg = tmp.GetComponent<CanvasGroup>();
            if (cg != null) { cg.DOKill(); cg.alpha = 0f; }
            tmp.transform.DOKill();
            tmp.transform.localScale = Vector3.one;
            tmp.transform.localRotation = Quaternion.identity;
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
