using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Canonical UI animations for WordDrop — every modal, button, score count-up,
    /// star burst, and shake routes through the 12 methods here rather than calling
    /// DOTween directly.
    ///
    /// Rules:
    ///   • All methods return the underlying Tween/Sequence so callers can chain, kill, or await.
    ///   • Every method exposes an optional <c>onComplete</c> callback for sequencing.
    ///   • Durations + easings come from the style-guide token table (never inlined).
    ///   • ReducedMotion accessibility: overshoot/bounce/shake animations degrade to plain
    ///     fades so players who enable the iOS/Android flag aren't blasted with motion.
    ///
    /// Mirrors <c>project_wordrop_ui_style_guide.md</c>. When adding a new animation,
    /// update both the style guide and this class.
    /// </summary>
    public static class UIAnimations
    {
        // ═══════════════════════════════════════════════════════════════════════
        // DURATION TOKENS (seconds)
        // ═══════════════════════════════════════════════════════════════════════

        public const float DurInstant  = 0.08f;
        public const float DurFast     = 0.15f;
        public const float DurMedium   = 0.25f;
        public const float DurSlow     = 0.40f;
        public const float DurStagger  = 0.20f;

        // ═══════════════════════════════════════════════════════════════════════
        // OVERSHOOT LEVELS — maps to Ease.OutBack amount
        // ═══════════════════════════════════════════════════════════════════════

        public enum Overshoot
        {
            Light,   // amount 1.08 — subtle pop-in
            Medium,  // amount 1.25 — modal appearance (default, bumped from 1.15 for punchier feel)
            Heavy,   // amount 1.40 — star pop, spectacle moments
        }

        private static float OvershootAmount(Overshoot level)
        {
            switch (level)
            {
                case Overshoot.Light:  return 1.08f;
                case Overshoot.Heavy:  return 1.40f;
                default:               return 1.25f;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // REDUCED MOTION ACCESSIBILITY FLAG
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// When true, overshoot/bounce/shake animations collapse to plain fades.
        /// Settings UI / iOS accessibility bridge can flip this. Defaults to OS query
        /// on first access (polled once per app session).
        /// </summary>
        public static bool ReducedMotion
        {
            get
            {
                if (!_reducedMotionPolled)
                {
                    _reducedMotionPolled = true;
                    _reducedMotion = QueryOsReducedMotion();
                }
                return _reducedMotion;
            }
            set { _reducedMotion = value; _reducedMotionPolled = true; }
        }
        private static bool _reducedMotion;
        private static bool _reducedMotionPolled;

        private static bool QueryOsReducedMotion()
        {
#if UNITY_IOS && !UNITY_EDITOR
            // iOS bridge lives in a later native plugin. Default false for Phase 6.5.
            return false;
#else
            return false;
#endif
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 1. PopIn — scale 0 → overshoot → 1.0
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pops a transform from scale 0 to 1.0 with an overshoot bounce.
        /// Use for modal appearance, panel reveals, celebratory elements.
        /// Reduced-motion: falls back to FadeIn of the transform's CanvasGroup (if any).
        /// </summary>
        /// <param name="target">Transform to animate (scale is driven).</param>
        /// <param name="level">Overshoot intensity token.</param>
        /// <param name="onComplete">Optional callback fired when the tween completes.</param>
        /// <returns>The running Tweener (or null if ReducedMotion took the fallback path without a CanvasGroup).</returns>
        public static Tweener PopIn(Transform target, Overshoot level = Overshoot.Medium, Action onComplete = null)
        {
            if (target == null) { onComplete?.Invoke(); return null; }

            // Kill any prior scale tween on this transform before starting a new
            // one. Without this, rapid show/hide/show sequences stack tweens —
            // both try to drive localScale every frame, producing the jitter
            // Spencer called "losing frames". DOTween convention: kill before new.
            target.DOKill(false);

            if (ReducedMotion)
            {
                target.localScale = Vector3.one;
                var cg = target.GetComponent<CanvasGroup>();
                if (cg != null) return FadeIn(cg, onComplete);
                onComplete?.Invoke();
                return null;
            }

            target.localScale = Vector3.zero;
            Tweener t = target.DOScale(1f, DurMedium)
                .SetEase(Ease.OutBack, OvershootAmount(level));
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 2. PopOut — scale 1.0 → 0 with optional fade
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Dismisses a transform by scaling to 0 and (if a CanvasGroup is present) fading alpha.
        /// Use for modal dismiss. Reduced-motion: skips scale, uses fade-only when possible.
        /// </summary>
        /// <param name="target">Transform to animate.</param>
        /// <param name="onComplete">Optional callback fired after dismiss.</param>
        /// <returns>The running Sequence.</returns>
        public static Sequence PopOut(Transform target, Action onComplete = null)
        {
            if (target == null) { onComplete?.Invoke(); return null; }

            // Kill every tween on the subtree — the parent's dismiss must be
            // the ONLY thing writing scale on this hierarchy during the 0.15s
            // PopOut. Otherwise child tweens from the "show" phase (star pop-ins,
            // button press bounces, coin count-ups still ticking) fight the
            // parent scale and produce multiplicative-scale jitter that reads as
            // frame loss. Belt+suspenders over the per-transform DOKill.
            var descendants = target.GetComponentsInChildren<Transform>(includeInactive: true);
            for (int i = 0; i < descendants.Length; i++)
                descendants[i].DOKill(false);

            Sequence seq = DOTween.Sequence();
            var cg = target.GetComponent<CanvasGroup>();

            if (ReducedMotion)
            {
                if (cg != null) seq.Join(cg.DOFade(0f, DurFast));
                else            seq.Join(target.DOScale(0f, DurFast));
            }
            else
            {
                // InBack gives a tiny anticipation bump before the collapse (a
                // Candy-Crush-style dismiss pulse) which reads as punch rather
                // than snap, and avoids the end-of-curve sprint InCubic causes.
                seq.Join(target.DOScale(0f, DurFast).SetEase(Ease.InBack, 1.2f));
                // Explicit ease on the alpha fade so it matches the scale feel
                // instead of inheriting DOTween's default OutQuad.
                if (cg != null) seq.Join(cg.DOFade(0f, DurFast).SetEase(Ease.InCubic));
            }
            if (onComplete != null) seq.OnComplete(() => onComplete());
            return seq;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 3. ButtonPress — scale 1.0 → 0.95 → 1.05 → 1.0
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Satisfying button tap feedback — press-down, overshoot on release, settle.
        /// Every interactive button should route through this.
        /// Reduced-motion: degrades to a quick fade-in-place (no scale).
        /// </summary>
        public static Sequence ButtonPress(Transform target, Action onComplete = null)
        {
            if (target == null) { onComplete?.Invoke(); return null; }

            Sequence seq = DOTween.Sequence();
            if (ReducedMotion)
            {
                // Tiny delay so the player sees something responded.
                seq.AppendInterval(DurInstant);
            }
            else
            {
                seq.Append(target.DOScale(0.95f, DurInstant).SetEase(Ease.OutCubic));
                seq.Append(target.DOScale(1.05f, DurInstant).SetEase(Ease.OutCubic));
                seq.Append(target.DOScale(1.00f, DurFast  ).SetEase(Ease.OutCubic));
            }
            if (onComplete != null) seq.OnComplete(() => onComplete());
            return seq;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 4. FadeIn — alpha 0 → 1
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Fades a CanvasGroup to alpha 1. Tweens from the group's *current* alpha,
        /// so a partial-fade-in continues smoothly if called mid-fade (e.g., rapid
        /// prompt swaps in the tutorial overlay). Callers that want an explicit
        /// alpha=0 start should set <c>group.alpha = 0f;</c> beforehand.
        /// </summary>
        public static Tweener FadeIn(CanvasGroup group, Action onComplete = null, float duration = -1f)
        {
            if (group == null) { onComplete?.Invoke(); return null; }
            float d = duration > 0f ? duration : 0.20f; // linear per style guide
            Tweener t = group.DOFade(1f, d).SetEase(Ease.Linear);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 5. FadeOut — alpha 1 → 0
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Fades a CanvasGroup from opaque to transparent.</summary>
        public static Tweener FadeOut(CanvasGroup group, Action onComplete = null, float duration = -1f)
        {
            if (group == null) { onComplete?.Invoke(); return null; }
            float d = duration > 0f ? duration : 0.20f;
            Tweener t = group.DOFade(0f, d).SetEase(Ease.Linear);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 6. SlideInFromBottom — anchored y +100 → 0
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Slides a RectTransform in from below its resting position.
        /// Use for toasts, bottom-sheet dialogs. Reduced-motion: plain fade-in.
        /// </summary>
        public static Tweener SlideInFromBottom(RectTransform rt, float offsetY = 100f, Action onComplete = null)
        {
            if (rt == null) { onComplete?.Invoke(); return null; }
            Vector2 rest = rt.anchoredPosition;
            if (ReducedMotion)
            {
                rt.anchoredPosition = rest;
                var cg = rt.GetComponent<CanvasGroup>();
                if (cg != null) return FadeIn(cg, onComplete);
                onComplete?.Invoke();
                return null;
            }
            rt.anchoredPosition = rest + new Vector2(0, -offsetY);
            Tweener t = rt.DOAnchorPosY(rest.y, 0.30f).SetEase(Ease.OutCubic);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 7. SlideOutToBottom — anchored y 0 → +offset
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>Slides a RectTransform downward off its resting position.</summary>
        public static Tweener SlideOutToBottom(RectTransform rt, float offsetY = 100f, Action onComplete = null)
        {
            if (rt == null) { onComplete?.Invoke(); return null; }
            float restY = rt.anchoredPosition.y;
            if (ReducedMotion)
            {
                var cg = rt.GetComponent<CanvasGroup>();
                if (cg != null) return FadeOut(cg, onComplete);
                onComplete?.Invoke();
                return null;
            }
            Tweener t = rt.DOAnchorPosY(restY - offsetY, 0.20f).SetEase(Ease.InCubic);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 8. StarPopIn — staggered scale 0 → 1.3 → 1.0 across multiple stars
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Pops each of the first <paramref name="starsLit"/> star transforms in sequence
        /// with a <c>DurStagger</c> gap between them. Remaining stars are left at scale 1
        /// (caller is expected to set them dim beforehand).
        /// </summary>
        /// <param name="starTransforms">Array of star transforms (order matters).</param>
        /// <param name="starsLit">How many stars to animate, 0..length.</param>
        /// <param name="onAllComplete">Fires after the last star has settled.</param>
        public static Sequence StarPopIn(Transform[] starTransforms, int starsLit, Action onAllComplete = null)
        {
            Sequence seq = DOTween.Sequence();
            if (starTransforms == null || starsLit <= 0)
            {
                if (onAllComplete != null) seq.OnComplete(() => onAllComplete());
                return seq;
            }

            // Pre-zero the stars that will animate.
            for (int i = 0; i < starsLit && i < starTransforms.Length; i++)
            {
                if (starTransforms[i] != null)
                    starTransforms[i].localScale = Vector3.zero;
            }

            if (ReducedMotion)
            {
                // Skip overshoot — just fade stars in one by one.
                for (int i = 0; i < starsLit && i < starTransforms.Length; i++)
                {
                    var tf = starTransforms[i];
                    if (tf == null) continue;
                    seq.AppendInterval(DurStagger);
                    seq.AppendCallback(() => tf.localScale = Vector3.one);
                }
                if (onAllComplete != null) seq.OnComplete(() => onAllComplete());
                return seq;
            }

            for (int i = 0; i < starsLit && i < starTransforms.Length; i++)
            {
                int idx = i; // closure capture
                seq.AppendInterval(i == 0 ? 0f : DurStagger);
                seq.AppendCallback(() =>
                {
                    Transform tf = starTransforms[idx];
                    if (tf == null) return;
                    tf.DOScale(1.3f, DurFast).SetEase(Ease.OutBack, OvershootAmount(Overshoot.Heavy))
                        .OnComplete(() =>
                        {
                            if (tf != null) tf.DOScale(1.0f, DurFast).SetEase(Ease.OutCubic);
                        });
                });
            }
            if (onAllComplete != null) seq.OnComplete(() => onAllComplete());
            return seq;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 9. NumberIncrement — text value interpolates start → end
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Animates a score/coin/points readout from <paramref name="from"/> to
        /// <paramref name="to"/> over ~0.5s with an OutCubic ramp. Writes to the
        /// TextMeshProUGUI component on every frame.
        /// Reduced-motion: snaps to the final value with no interpolation.
        /// </summary>
        public static Tweener NumberIncrement(TextMeshProUGUI target, int from, int to, Action onComplete = null, float duration = 0.5f)
        {
            if (target == null) { onComplete?.Invoke(); return null; }
            if (ReducedMotion)
            {
                target.text = to.ToString();
                onComplete?.Invoke();
                return null;
            }
            int current = from;
            target.text = current.ToString();
            Tweener t = DOTween.To(() => current, v =>
            {
                current = v;
                if (target != null) target.text = current.ToString();
            }, to, duration).SetEase(Ease.OutCubic);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 10. ShakeSuccess — ±8px, 3 oscillations, ~0.4s
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Small satisfying horizontal shake on a transform — used for positive feedback
        /// (word scored, tile landed). Reduced-motion: skipped entirely.
        /// </summary>
        public static Tweener ShakeSuccess(Transform target, Action onComplete = null)
        {
            if (target == null || ReducedMotion) { onComplete?.Invoke(); return null; }
            Tweener t = target.DOShakePosition(DurSlow, strength: new Vector3(8f, 0f, 0f), vibrato: 3, randomness: 0f, snapping: false, fadeOut: true);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 11. ShakeError — ±15px, 5 oscillations, ~0.5s
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Stronger negative-feedback shake — out of moves, invalid action, error.
        /// Reduced-motion: skipped entirely.
        /// </summary>
        public static Tweener ShakeError(Transform target, Action onComplete = null)
        {
            if (target == null || ReducedMotion) { onComplete?.Invoke(); return null; }
            Tweener t = target.DOShakePosition(0.5f, strength: new Vector3(15f, 0f, 0f), vibrato: 5, randomness: 0f, snapping: false, fadeOut: true);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // DropInWithBounce — panel drops from above, lands with damped bounce
        // ═══════════════════════════════════════════════════════════════════════
        //
        // Canonical "modal arrives" animation. Panel starts off-screen above its
        // rest position, falls down, overshoots PAST the rest by a small dip,
        // rebounds back up past rest by an even smaller amount, then settles at
        // rest. Same beat as the TopOutPanel — extracted 2026-05-29 so every
        // modal in the game uses the same landing feel.
        //
        // The panel must already be anchored such that its REST position
        // matches the current anchoredPosition — DropInWithBounce reads this
        // as the target and animates relative to it.

        // 2026-06-01: bounce amounts increased + a second small dip added per
        // Spencer ("more cartoonishness, more bounce, less damping"). The
        // sequence is now: drop past rest by 80 → rebound up 40 → dip back
        // down 15 → settle. The 4-phase shape gives a clear "bonk … boing …
        // bonk-settle" rhythm characteristic of Royal Match / Toon Blast
        // modals, vs the older 3-phase damped landing.
        public const float DROP_OFFSCREEN_OFFSET   = 1200f;
        public const float DROP_DIP_BELOW          = 80f;   // was 40 — bigger overshoot past rest
        public const float DROP_REBOUND_UP         = 40f;   // was 14 — more visible bounce-back
        public const float DROP_BOUNCE_DIP         = 15f;   // NEW — second smaller dip before final settle
        public const float DROP_PHASE_DROP_DUR     = 0.22f; // was 0.20 — slightly slower for more punch
        public const float DROP_PHASE_REBOUND_DUR  = 0.16f; // was 0.12 — slower so the rebound reads
        public const float DROP_PHASE_BOUNCE_DUR   = 0.14f; // duration of the second dip
        public const float DROP_PHASE_SETTLE_DUR   = 0.22f; // 2026-06-01 — was 0.10; longer + OutCubic so the bounce winds down gradually instead of cutting off abruptly
        public const float DROP_TOTAL_DUR          = DROP_PHASE_DROP_DUR
                                                   + DROP_PHASE_REBOUND_DUR
                                                   + DROP_PHASE_BOUNCE_DUR
                                                   + DROP_PHASE_SETTLE_DUR;

        /// <summary>
        /// Drops a RectTransform from above its current anchoredPosition and
        /// lands with a damped bounce (dip past rest, rebound up, settle).
        /// <paramref name="speedMult"/> = 1.0 uses canonical durations; > 1.0
        /// is FASTER (e.g. 1.5 = 50% faster, durations divided). Reduced-
        /// motion: falls back to PopIn (scale-up with overshoot).
        /// </summary>
        public static Sequence DropInWithBounce(RectTransform rt, Action onArrive = null, float speedMult = 1f)
        {
            if (rt == null) { onArrive?.Invoke(); return null; }
            rt.DOKill(false);

            if (ReducedMotion)
            {
                var cg = rt.GetComponent<CanvasGroup>();
                if (cg != null) FadeIn(cg, onArrive);
                else            PopIn(rt, Overshoot.Medium, onArrive);
                return null;
            }

            float dropDur    = DROP_PHASE_DROP_DUR    / speedMult;
            float reboundDur = DROP_PHASE_REBOUND_DUR / speedMult;
            float bounceDur  = DROP_PHASE_BOUNCE_DUR  / speedMult;
            float settleDur  = DROP_PHASE_SETTLE_DUR  / speedMult;

            float restY = rt.anchoredPosition.y;
            float aboveY = restY + DROP_OFFSCREEN_OFFSET;
            rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, aboveY);

            // Four-phase cartoony bounce: drop past rest → big rebound up →
            // smaller dip back down → settle. The middle two phases use
            // OutBack with mild overshoot so each direction-change "snaps"
            // rather than smoothly easing, giving the modal that bouncy
            // toy-landing feel Spencer asked for.
            Sequence seq = DOTween.Sequence();
            seq.Append(rt.DOAnchorPosY(restY - DROP_DIP_BELOW,  dropDur).SetEase(Ease.OutQuad));
            seq.Append(rt.DOAnchorPosY(restY + DROP_REBOUND_UP, reboundDur).SetEase(Ease.OutBack, 1.4f));
            seq.Append(rt.DOAnchorPosY(restY - DROP_BOUNCE_DIP, bounceDur).SetEase(Ease.OutBack, 1.2f));
            // 2026-06-01: final settle eased to OutCubic (was OutQuad). OutCubic
            // has a more pronounced "long tail" — velocity drops off much more
            // gradually as the panel approaches rest, so the bounce reads as
            // winding down rather than coming to a sudden stop.
            seq.Append(rt.DOAnchorPosY(restY,                   settleDur).SetEase(Ease.OutCubic));
            if (onArrive != null) seq.OnComplete(() => onArrive());
            return seq;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ExitDown — panel exits straight down off-screen with anticipation
        // ═══════════════════════════════════════════════════════════════════════
        //
        // Canonical "modal leaves" animation. Pairs with DropInWithBounce.
        // Uses InBack so the panel briefly anticipates UP before dropping
        // away — gives the dismiss a little punch instead of a soft slide.

        public const float EXIT_DOWN_DURATION  = 0.35f;
        public const float EXIT_DOWN_OVERSHOOT = 1.4f;

        /// <summary>
        /// Drops a RectTransform off-screen below its current anchoredPosition
        /// with an InBack anticipation. Reduced-motion: falls back to PopOut
        /// (scale-to-zero collapse).
        /// </summary>
        public static Tweener ExitDown(RectTransform rt, Action onComplete = null, float speedMult = 1f)
        {
            if (rt == null) { onComplete?.Invoke(); return null; }
            rt.DOKill(false);

            if (ReducedMotion)
            {
                PopOut(rt, onComplete);
                return null;
            }

            // SetRelative so the -DROP_OFFSCREEN_OFFSET offset is applied to
            // whatever Y position the panel has when the tween STARTS — not
            // when this method is called. Matters when the tween is nested
            // inside a Sequence and other tweens move the panel between
            // construction and tween-start.
            Tweener t = rt.DOAnchorPosY(-DROP_OFFSCREEN_OFFSET, EXIT_DOWN_DURATION / speedMult)
                .SetEase(Ease.InBack, EXIT_DOWN_OVERSHOOT)
                .SetRelative(true);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        /// <summary>
        /// Lifts a RectTransform off-screen ABOVE its current anchoredPosition
        /// with an InBack anticipation (briefly dips down before launching up).
        /// Mirror of ExitDown — for modals that "leave the way they came in"
        /// (stage clear, level complete, etc). Reduced-motion: falls back to
        /// PopOut.
        /// </summary>
        public static Tweener ExitUp(RectTransform rt, Action onComplete = null, float speedMult = 1f)
        {
            if (rt == null) { onComplete?.Invoke(); return null; }
            rt.DOKill(false);

            if (ReducedMotion)
            {
                PopOut(rt, onComplete);
                return null;
            }

            // SetRelative — see ExitDown for rationale.
            Tweener t = rt.DOAnchorPosY(DROP_OFFSCREEN_OFFSET, EXIT_DOWN_DURATION / speedMult)
                .SetEase(Ease.InBack, EXIT_DOWN_OVERSHOOT)
                .SetRelative(true);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // NewTilePop — scale curve shared by row-rise + hand-deal
        // ═══════════════════════════════════════════════════════════════════════
        //
        // Single "soft sprout" curve applied wherever a new tile/card arrives:
        // the row-rise new bottom row, the initial hand deal, the single-card
        // refill after a swap. Authoring the curve in one place means any
        // tuning of feel (amplitude, period, easing, baseline duration) propa-
        // gates to every arrival moment in the game.
        //
        // The CURVE IDENTITY is shared. The TEMPO is per-call-site via the
        // speedMult arg — speedMult=1.0 = canonical 0.85s baseline (row-rise),
        // higher values shorten the duration proportionally for snappier sites
        // (hand reveals).

        public const float NEW_TILE_POP_DURATION  = 0.85f;
        public const float NEW_TILE_POP_AMPLITUDE = 0.45f;
        public const float NEW_TILE_POP_PERIOD    = 0.30f;

        /// <summary>
        /// Pops a transform to <paramref name="targetScale"/> using the
        /// canonical soft OutElastic sprout. Caller MUST set the start scale
        /// (typically <c>Vector3.zero</c>) before calling.
        /// <paramref name="speedMult"/> &gt; 1 = faster (duration divided).
        /// Reduced motion: snaps to target with no animation.
        /// </summary>
        public static Tweener NewTilePop(Transform target, Vector3 targetScale, float speedMult = 1f, Action onComplete = null)
        {
            if (target == null) { onComplete?.Invoke(); return null; }

            if (ReducedMotion)
            {
                target.localScale = targetScale;
                onComplete?.Invoke();
                return null;
            }

            Tweener t = target.DOScale(targetScale, NEW_TILE_POP_DURATION / Mathf.Max(0.0001f, speedMult))
                .SetEase(Ease.OutElastic, NEW_TILE_POP_AMPLITUDE, NEW_TILE_POP_PERIOD);
            if (onComplete != null) t.OnComplete(() => onComplete());
            return t;
        }

        // ── WildCardPop — extra-juicy entry for an AWARDED WILD ──────────────────
        // Reads as a reward, not a normal deal: punches WAY past the rest size,
        // holds big for a beat so the player registers it, then elastic-settles to
        // the right size with a small celebratory wobble. 2026-06-04 Spencer.
        public const float WILD_POP_OVERSHOOT = 1.75f; // peak = 1.75× the rest scale
        public const float WILD_POP_GROW_DUR  = 0.30f; // anticipatory grow to peak
        public const float WILD_POP_HOLD      = 0.30f; // hold big — the "ta-da" beat
        public const float WILD_POP_SETTLE    = 0.55f; // settle back to rest size

        /// <summary>Big-overshoot → hold → elastic-settle pop for an awarded wild.
        /// Caller need not set start scale (we start it small). ReducedMotion snaps.</summary>
        public static Sequence WildCardPop(Transform target, Vector3 targetScale, Action onComplete = null)
        {
            if (target == null) { onComplete?.Invoke(); return null; }

            if (ReducedMotion)
            {
                target.localScale = targetScale;
                onComplete?.Invoke();
                return null;
            }

            target.DOKill();
            target.localScale = targetScale * 0.25f;
            Sequence seq = DOTween.Sequence();
            // Grow well past rest with an anticipatory back-ease.
            seq.Append(target.DOScale(targetScale * WILD_POP_OVERSHOOT, WILD_POP_GROW_DUR).SetEase(Ease.OutBack, 3f));
            // Small celebratory wobble while it's big (returns to rest rotation).
            seq.Join(target.DOPunchRotation(new Vector3(0f, 0f, 11f), WILD_POP_GROW_DUR + WILD_POP_HOLD, 6, 0.7f));
            // Hold big for a beat so the award reads.
            seq.AppendInterval(WILD_POP_HOLD);
            // Settle to the correct size with a soft elastic bounce.
            seq.Append(target.DOScale(targetScale, WILD_POP_SETTLE).SetEase(Ease.OutElastic, 0.6f, 0.45f));
            if (onComplete != null) seq.OnComplete(() => onComplete());
            return seq;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // 12. ConfettiBurst — particle pop, 1.5s envelope
        // ═══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Plays a confetti-style particle burst at a world-space position. Phase 6.5
        /// stub — routes to GameParticles.Instance if available, otherwise no-ops.
        /// Use on Level Completed celebration moments only.
        /// </summary>
        /// <param name="worldPosition">Where the burst originates.</param>
        /// <param name="onComplete">Fires ~1.5s after the burst is spawned.</param>
        public static void ConfettiBurst(Vector3 worldPosition, Action onComplete = null)
        {
            // Use existing sparkle/shimmer particle as confetti stand-in until art drops in.
            if (GameParticles.Instance != null)
                GameParticles.Instance.PlayShimmer(worldPosition, 12);

            if (onComplete != null)
            {
                DOVirtual.DelayedCall(1.5f, () => onComplete());
            }
        }
    }
}
