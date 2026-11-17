using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Pre-level "here's your goal" modal (Candy-Crush / Royal-Match style), shown BEFORE each level
    /// in a Survival run — the FIRST thing after the player taps PLAY, over the DIMMED game board
    /// (semi-transparent backdrop, board still visible behind). Displays "LEVEL N", a picture of WHAT
    /// the player needs (objective icon + count badge), a plain-words description, and a PLAY button
    /// that starts the level.
    ///
    /// Uses the EXACT same entrance/exit choreography as StageClearModal (the post-level modal):
    /// backdrop fade-in → UIAnimations.DropInWithBounce → staggered title toss + child fades, and
    /// UIAnimations.ExitUp on dismiss (pause held through the exit, released after). Built once in
    /// Awake, hidden until Show(); pauses gameplay via SurvivalManager.SetOverlayPaused while up.
    /// Triggered from ObjectiveManager.InstallLevel (once per level). 2026-06-15 Spencer.
    /// </summary>
    public class LevelIntroModal : MonoBehaviour
    {
        public static LevelIntroModal Instance { get; private set; }
        private RectTransform _boosterRow; private int _levelNum;

        public bool IsShowing => _canvas != null && _canvas.gameObject.activeSelf;

        private Canvas      _canvas;
        private GameObject  _panel;
        private Image       _overlay;
        private Text        _titleText;
        private Text        _goalText;
        private Text        _descText;
        private GameObject  _iconHolder;   // cleared + rebuilt per Show()
        private GameObject  _goalRow;      // full-board briefing: one picture per goal, with its count (2026-10-22)
        private CanvasGroup _iconGroup;
        public static bool USE_ART_PLAY = true;   // 2026-10-27: green pill Play art button (false = the old orange MenuUI button)
        private GameObject  _btnPlay;
        private CanvasGroup _btnGroup;
        private GameObject  _closeBtn;     // X (map-flow only): cancel → bare map

        private Sequence _entranceSeq;
        private float    _titleZRot;       // shared state for the title toss rotation tween
        private bool     _isPresenting;
        private bool     _isDismissing;

        private static readonly Color OVERLAY_BG = new Color(0.05f, 0.04f, 0.12f, 0.80f); // dim — board shows through
        private static readonly Color CARD_BG    = new Color(0.99f, 0.95f, 0.86f, 1f);    // warm cream (CC card)
        private static readonly Color HEADER_BG  = new Color(0.56f, 0.31f, 0.78f, 1f);    // boss-node purple header (matches LevelMapPanel.BOSS_COLOR) — reads as "a bit more difficulty". 2026-07-13 Spencer
        private static readonly Color TITLE_COL  = Color.white;
        private static readonly Color DESC_COL   = new Color(0.32f, 0.24f, 0.30f, 1f);

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void OnDestroy()
        {
            KillTweens();
            if (_isPresenting && SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(false);
            if (Instance == this) Instance = null;
        }

        // ── Show / dismiss ────────────────────────────────────────────────────────

        /// <summary>Present the goal for the level about to start. Pauses gameplay until PLAY.</summary>
        // ── Deferred show ──────────────────────────────────────────────────────
        // When an UNLOCK reward modal sits between the clear celebration and this level, the intro is STORED
        // here (not shown) at install time and displayed later by UnlockModal.OnClaim, so the order reads
        // cleared → UNLOCKED → objective intro. 2026-07-06 Spencer.
        private Objective _deferredObj;
        private int _deferredLevel = -1;
        public bool HasDeferred => _deferredObj != null;
        public void SetDeferred(Objective obj, int levelNum) { _deferredObj = obj; _deferredLevel = levelNum; }
        public void ShowDeferred()
        {
            if (_deferredObj == null) return;
            var obj = _deferredObj; int lvl = _deferredLevel;
            _deferredObj = null; _deferredLevel = -1;
            Show(obj, lvl);
        }

        public static int NO_CARD_THROUGH = 1;    // 11-01 Spencer + RM 1-20 recording: NO card on L1 only, a card from L2 (was 15 - misread; L16 is when boosters unlock). 0 = always show
        public void Show(Objective obj, int levelNum)
        {
            if (_canvas == null || obj == null) return;
            if (_isPresenting) return;
            _isPresenting = true;
            _isDismissing = false;

            // NO BOARD BEHIND THE CARD (2026-10-23 Spencer: "a menu pops down giving you the goals and there is a board built
            // behind it already. you click play and then that board dissapears and then another board pops in"). The entry
            // animation on PLAY parks the board off-screen then slides it in — so park it NOW, before the card shows: the card
            // sits over the empty background and PLAY brings the board in once (Royal Match). Prep is idempotent.
            if (SurvivalManager.FullBoardActive) HUDManager.Instance?.PrepLevelEntry();

            // NO CARD ON LEVELS 1-15 (2026-10-30 Spencer: "just follow exactly what they do"). Royal Match (his L1-20 recording):
            // L1-15 the board just starts - the goal lives in the HUD and new pieces are taught on the board (TutorialBeats);
            // the card arrives at L16 with "Select Boosters" (PreBoosters.FROM_LEVEL 16) and stays on every level after.
            // Same hand-off as pressing PLAY: shimmer, music, OnPlayStarted (the map fades, the board slides in, tutorials start).
            // MECHANIC SPLASH (11-01): the first level that teaches a power-up shows the splash INSTEAD of the card (RM L4/L5);
            // closing it is the same hand-off as PLAY.
            string splash = MechanicSplash.KeyFor(levelNum);
            if (splash != null)
            {
                _levelNum = levelNum;
                if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
                MechanicSplash.Show(splash, () => { GameAudio.Instance?.PlayLevelLoadShimmer(); Hide(); });
                return;
            }

            if (NO_CARD_THROUGH > 0 && SurvivalManager.FullBoardActive && LevelRules.OwnsBoardSeed(levelNum)
                && !LevelMapPanel.IsTutorialLevel(levelNum) && LevelMapPanel.RunLevel(levelNum) <= NO_CARD_THROUGH)
            {
                WDLog.Log($"[LevelCard] level {LevelMapPanel.DisplayNum(levelNum)}: no card (RM: cards from {NO_CARD_THROUGH + 1}) - straight in");
                _levelNum = levelNum;
                GameAudio.Instance?.PlayLevelLoadShimmer();
                Hide();
                return;
            }

            if (_titleText != null) _titleText.text = $"LEVEL {LevelMapPanel.DisplayNum(levelNum)}"; // run level (tutorial 1..10)

            // 2026-10-20 Spencer: on a scored run the card shows the POINTS TARGET and the rules
            // of this level, not an objective icon and the level's internal name. The name told a
            // new player nothing — "Warm Up" is a label, not an instruction. Tutorial levels keep
            // their hand-authored objective text, which is genuinely instructive and gated to
            // specific actions. Same ownership gate as the board seed, so the two cannot disagree
            // about who is driving a level.
            bool scoreBrief = LevelRules.OwnsBoardSeed(levelNum);
            if (_descText != null)
            {
                _descText.text = scoreBrief
                    ? LevelRules.BriefingFor(levelNum,
                        SurvivalManager.Instance != null ? SurvivalManager.Instance.CurrentStageTarget : 0)
                    : obj.IntroDescription; // verbose, instructive (not the terse HUD Title)
            }

            // Layout per objective type: HiddenWord centers the row of blanks across the top of the card
            // with the description CENTERED underneath; every other mode keeps icon-left / text-right.
            bool hidden = obj.Icon == Objective.HudIcon.HiddenWord;
            ApplyContentLayout(hidden, scoreBrief);
            BuildGoalRow(scoreBrief && SurvivalManager.FullBoardActive ? LevelRules.GoalIconsFor(levelNum) : null,
                         scoreBrief && SurvivalManager.FullBoardActive);
            // PRE-LEVEL BOOSTERS (2026-10-29): "Select Boosters" row between the briefing and PLAY; the briefing gives up its lower part.
            _levelNum = levelNum;
            bool boost = scoreBrief && PreBoosters.OfferedOn(levelNum);
            if (_boosterRow == null && _panel != null)
            {
                var go = new GameObject("PreBoosterRow", typeof(RectTransform)); go.transform.SetParent(_panel.transform, false);
                _boosterRow = (RectTransform)go.transform;
                _boosterRow.anchorMin = new Vector2(0.10f, 0.275f); _boosterRow.anchorMax = new Vector2(0.90f, 0.475f);
                _boosterRow.offsetMin = _boosterRow.offsetMax = Vector2.zero;
            }
            if (_boosterRow != null)
            {
                _boosterRow.gameObject.SetActive(boost);
                if (boost)
                {
                    PreBoosters.BuildRow(_boosterRow);
                    if (_descText != null) { var dRT = _descText.rectTransform; dRT.anchorMin = new Vector2(dRT.anchorMin.x, 0.48f); dRT.offsetMin = new Vector2(dRT.offsetMin.x, 0f); }
                }
            }

            // Rebuild the objective icon fresh each time. The score briefing has no icon — it is
            // all text, filling the card — so skip the build entirely rather than building one
            // into a hidden holder.
            if (_iconHolder != null)
            {
                for (int i = _iconHolder.transform.childCount - 1; i >= 0; i--)
                    Destroy(_iconHolder.transform.GetChild(i).gameObject);
                _iconHolder.SetActive(!scoreBrief);
                if (!scoreBrief)
                {
                    float iconSize = hidden ? 84f : 78f;
                    ObjectiveIconBuilder.Build(obj.Icon, _iconHolder.transform, iconSize, obj.RemainingCount, obj.IconWord);
                }
            }

            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            if (_closeBtn != null) _closeBtn.SetActive(LevelMapPanel.MapFlowEnabled); // X only where there's a map to cancel back to
            GameAudio.Instance?.PlayWhooshFast(); // 2026-06-15 Spencer: level-intro entry uses whoosh_fast

            if (CARD_V2 && _v2Root != null) ApplyV2(scoreBrief && SurvivalManager.FullBoardActive ? LevelRules.GoalIconsFor(levelNum) : null, boost, levelNum);

            KillTweens();
            ResetEntranceState();
            _canvas.gameObject.SetActive(true);
            AnimateEntrance();
        }

        /// <summary>EXACT mirror of StageClearModal.AnimateEntrance: backdrop fade → panel
        /// drop-in-with-bounce → staggered title toss + child fades → button pulse.</summary>
        private void AnimateEntrance()
        {
            if (CARD_V2 && CARD_V2_ANIM && _v2Root != null) { AnimateEntranceV2(); return; }
            _entranceSeq?.Kill();
            Sequence seq = DOTween.Sequence();

            // Phase 1: backdrop fades to dim alpha over the (still-visible) board.
            if (_overlay != null)
                seq.Append(_overlay.DOFade(CARD_V2 ? V2_OVERLAY.a : OVERLAY_BG.a, 0.12f).SetEase(Ease.OutQuad));
            else
                seq.AppendInterval(0.12f);

            // Phase 2: card drops in from above with the canonical bounce-settle (1.5× speed).
            const float DROP_SPEED = 1.5f;
            if (_panel != null)
            {
                seq.AppendCallback(() =>
                {
                    if (_panel == null) return;
                    var rt = _panel.transform as RectTransform;
                    // Restore rest (0,0) so DropInWithBounce reads the correct rest, then it re-parks above and drops
                    // in — all this frame, so the card was never seen sitting at rest during the backdrop fade.
                    if (rt != null) { rt.anchoredPosition = Vector2.zero; UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED); }
                });
                // Start the content DURING the drop's overshoot — after just the drop phase, while the card is still
                // rebounding/settling — instead of waiting for the full bounce to finish. Everything lands on screen
                // ~0.6s sooner and the children ride the bounce. 2026-07-13 Spencer.
                seq.AppendInterval(UIAnimations.DROP_PHASE_DROP_DUR / DROP_SPEED);
            }

            // Phase 3: children fade/toss in with a Playrix-style stagger.
            seq.AppendCallback(TossInTitle);
            seq.AppendInterval(0.08f);
            seq.AppendCallback(() => { if (_iconGroup != null) _iconGroup.DOFade(1f, 0.18f).SetEase(Ease.OutQuad); });
            seq.AppendInterval(0.06f);
            seq.AppendCallback(() => { FadeInText(_goalText, 0.18f); FadeInText(_descText, 0.18f); });
            seq.AppendInterval(0.10f);
            // Button row fades in, and PLAY punches out on top of that fade — the pop hands off
            // to the idle pulse itself, so StartPlayPulse is no longer called separately here
            // (two things driving the same transform would fight).
            seq.AppendCallback(() =>
            {
                // Alpha SNAPS to full — no fade. A 0.18s fade ran concurrently with the pop, so
                // the button was semi-transparent through the exact frames where the punch reads,
                // which washed it out. The reveal is carried entirely by scale. 2026-07-29.
                if (_btnGroup != null) { _btnGroup.DOKill(); _btnGroup.alpha = 1f; }
                PopInPlayButton();
            });

            _entranceSeq = seq;
        }

        /// <summary>Positions the icon holder + description for the current mode. HiddenWord: blanks row
        /// centered across the top, description centered underneath. Otherwise: icon left, text right
        /// (the original layout). 2026-06-17 Spencer.</summary>
        // ── GOAL PICTURES (2026-10-22 Spencer: "if it involves some sort of an icon or tile it would be helpful to have a
        // visual representation of that as well on the goals explanation"). Full-board briefing: GOAL heading, then a row
        // with the real art for each goal (crate, ice tile, gem, duck, book, blue letter tile) and a count badge, then text. ──
        // Layout (card fractions, bottom=0): PLAY button tops out at 0.26, header starts ~0.80.
        //   GOAL heading 0.735-0.795 · picture row 0.615-0.735 · text 0.285-0.605
        // Everything is ANCHOR-sized, not pixel-sized: the card's rect is not resolved on the first frame
        // (reference_wordrop_first_frame_modal_animation), so a size computed from it came out too big — the crate
        // overflowed its row onto the text (2026-10-22 screenshot).
        private void BuildGoalRow(System.Collections.Generic.List<LevelRules.GoalIconInfo> goals, bool fullBrief)
        {
            if (_goalRow != null) { Destroy(_goalRow); _goalRow = null; }
            if (_descText != null && !fullBrief) _descText.verticalOverflow = VerticalWrapMode.Overflow;
            if (!fullBrief || _panel == null) return;
            bool any = goals != null && goals.Count > 0;
            if (_goalText != null)
            {
                _goalText.gameObject.SetActive(true);
                var gRT = _goalText.rectTransform;
                gRT.anchorMin = new Vector2(0.07f, 0.735f); gRT.anchorMax = new Vector2(0.93f, 0.795f);
                gRT.offsetMin = gRT.offsetMax = Vector2.zero;
                _goalText.alignment = TextAnchor.MiddleCenter;
            }
            if (_descText != null)
            {
                var dRT = _descText.rectTransform;
                dRT.anchorMin = new Vector2(0.08f, 0.285f); dRT.anchorMax = new Vector2(0.92f, any ? 0.605f : 0.725f);
                dRT.offsetMin = dRT.offsetMax = Vector2.zero;
                // Best-fit only works with TRUNCATE — with Overflow the text ran on behind the PLAY button.
                _descText.verticalOverflow = VerticalWrapMode.Truncate;
                _descText.resizeTextForBestFit = true; _descText.resizeTextMinSize = 12; _descText.resizeTextMaxSize = 22;
                _descText.lineSpacing = 1.1f;
            }
            if (!any) return;
            _goalRow = new GameObject("GoalRow", typeof(RectTransform));
            _goalRow.transform.SetParent(_panel.transform, false);
            var rt = (RectTransform)_goalRow.transform;
            rt.anchorMin = new Vector2(0.08f, 0.615f); rt.anchorMax = new Vector2(0.92f, 0.735f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            int n = goals.Count;
            for (int i = 0; i < n; i++)
            {
                var g = goals[i];
                // Each goal gets an equal slot across the row: picture on the left half, "x11" on the right half.
                float x0 = (float)i / n, x1 = (float)(i + 1) / n;
                var slot = new GameObject("Goal_" + g.Type, typeof(RectTransform));
                slot.transform.SetParent(_goalRow.transform, false);
                var srt = (RectTransform)slot.transform;
                srt.anchorMin = new Vector2(x0, 0f); srt.anchorMax = new Vector2(x1, 1f);
                srt.offsetMin = srt.offsetMax = Vector2.zero;

                // picture: square, height of the row, right edge at the slot's centre
                var pic = new GameObject("Pic", typeof(RectTransform));
                pic.transform.SetParent(slot.transform, false);
                var prt = (RectTransform)pic.transform;
                prt.anchorMin = new Vector2(0.5f, 0.04f); prt.anchorMax = new Vector2(0.5f, 0.96f);
                prt.pivot = new Vector2(1f, 0.5f);
                prt.offsetMin = prt.offsetMax = Vector2.zero;
                var fit = pic.AddComponent<UnityEngine.UI.AspectRatioFitter>();
                fit.aspectMode = UnityEngine.UI.AspectRatioFitter.AspectMode.HeightControlsWidth; fit.aspectRatio = 1f;
                var icon = ObjectiveIconBuilder.BuildGoalIcon(g, pic.transform, 64f);
                if (icon != null)
                {
                    var irt = (RectTransform)icon.transform;
                    irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = irt.offsetMax = Vector2.zero;
                }

                // count: to the right of the picture, never on top of anything
                var cnt = CreateLabel(slot.transform, "Count", new Vector2(0.52f, 0.1f), new Vector2(1f, 0.9f), "x" + g.Count, 34, HEADER_BG);
                cnt.fontStyle = FontStyle.Bold;
                cnt.alignment = TextAnchor.MiddleLeft;
                cnt.resizeTextForBestFit = true; cnt.resizeTextMinSize = 14; cnt.resizeTextMaxSize = 40;
            }
        }

        private void ApplyContentLayout(bool hidden, bool scoreBrief = false)
        {
            if (_descText != null) _descText.resizeTextForBestFit = false;   // only the full-board brief best-fits (BuildGoalRow)
            if (_iconHolder != null && _iconHolder.transform is RectTransform iRT)
            {
                if (hidden) { iRT.anchorMin = new Vector2(0.05f, 0.56f); iRT.anchorMax = new Vector2(0.95f, 0.80f); }
                else        { iRT.anchorMin = new Vector2(0.10f, 0.34f); iRT.anchorMax = new Vector2(0.37f, 0.74f); }
                iRT.offsetMin = Vector2.zero; iRT.offsetMax = Vector2.zero;
            }
            if (_descText != null)
            {
                var dRT = _descText.rectTransform;
                if (hidden)
                {
                    dRT.anchorMin = new Vector2(0.07f, 0.30f); dRT.anchorMax = new Vector2(0.93f, 0.485f);
                    _descText.alignment = TextAnchor.UpperCenter;
                }
                else if (scoreBrief)
                {
                    // No icon and no GOAL label, so the briefing gets the whole card body.
                    // Top-anchored, not middle: the text is several lines on early levels and
                    // one line later, and growing DOWNWARD from a fixed top keeps the target
                    // line in the same place on every level instead of sliding about.
                    dRT.anchorMin = new Vector2(0.09f, 0.30f); dRT.anchorMax = new Vector2(0.91f, 0.80f);
                    _descText.alignment = TextAnchor.UpperCenter;
                }
                else
                {
                    dRT.anchorMin = new Vector2(0.46f, 0.32f); dRT.anchorMax = new Vector2(0.95f, 0.665f);
                    _descText.alignment = TextAnchor.MiddleLeft;
                }
                dRT.offsetMin = Vector2.zero; dRT.offsetMax = Vector2.zero;
                // Smaller on the briefing: it carries real sentences, where 30pt wrapped to a
                // wall of text. The objective layouts keep their original size.
                _descText.fontSize = scoreBrief ? 23 : 30;
                // The briefing body must NOT be bold throughout, or the <b> emphasis on the
                // target and on each rule keyword has nothing to stand out against. The
                // objective layouts keep their all-bold styling.
                _descText.fontStyle       = scoreBrief ? FontStyle.Normal : FontStyle.Bold;
                _descText.supportRichText = true;
                _descText.lineSpacing     = scoreBrief ? 1.15f : 1f;
            }
            // GOAL subtitle sits just above the description in each layout — and is GONE on the
            // score briefing, which states the target in words instead. 2026-10-20 Spencer.
            if (_goalText != null) _goalText.gameObject.SetActive(!scoreBrief);
            if (_goalText != null && !scoreBrief)
            {
                var gRT = _goalText.rectTransform;
                if (hidden)
                {
                    gRT.anchorMin = new Vector2(0.07f, 0.49f); gRT.anchorMax = new Vector2(0.93f, 0.545f);
                    _goalText.alignment = TextAnchor.LowerCenter;
                }
                else
                {
                    gRT.anchorMin = new Vector2(0.46f, 0.67f); gRT.anchorMax = new Vector2(0.95f, 0.75f);
                    _goalText.alignment = TextAnchor.LowerLeft;
                }
                gRT.offsetMin = Vector2.zero; gRT.offsetMax = Vector2.zero;
            }
        }

        /// <summary>Candy-Crush "object toss" for the LEVEL title — scale-overshoot + rotation
        /// wobble + alpha, identical to StageClearModal.TossInTitle.</summary>
        private void TossInTitle()
        {
            if (CARD_V2 && _v2Title != null)
            {
                var t2 = _v2Title.transform; t2.DOKill();
                t2.localScale = Vector3.one * TITLE_START_SCALE;
                t2.DOScale(1f, 0.28f).SetEase(Ease.OutBack, 5.2f);
            }
            if (_titleText == null) return;
            Transform t = _titleText.transform;
            t.DOKill();

            t.localScale    = Vector3.one * TITLE_START_SCALE;
            t.localRotation = Quaternion.Euler(0f, 0f, 8f);
            // FULLY OPAQUE from frame one. The old 0.12s alpha fade ran concurrently with the
            // scale, so the title was translucent through exactly the frames where it grows —
            // you never clearly saw it appear from small. Scale alone carries the entrance now.
            SetTextAlpha(_titleText, 1f);

            const float TOSS_DURATION = 0.28f;
            const float OVERSHOOT     = 5.2f;   // peak ~1.54x (was 3.0 -> ~1.25x)

            t.DOScale(1.0f, TOSS_DURATION).SetEase(Ease.OutBack, OVERSHOOT);

            DG.Tweening.Core.DOGetter<float> getZ = () => _titleZRot;
            DG.Tweening.Core.DOSetter<float> setZ = (float z) =>
            {
                _titleZRot = z;
                t.localRotation = Quaternion.Euler(0f, 0f, z);
            };
            _titleZRot = 8f;
            DOTween.To(getZ, setZ, 0f, TOSS_DURATION).SetEase(Ease.OutBack, OVERSHOOT);

            // Plop fires on the POP-OUT — the same frame the toss starts — not on the landing.
            // (The delayed land hit was removed 2026-07-30.)
            GameAudio.Instance?.PlayTitleDrop(1f);
        }

        // ── PLAY button reveal ──────────────────────────────────────────────────
        // Rebuilt 2026-07-29 from research on mobile-casual UI motion. The previous version was
        // 420ms with a ~1.94x uniform overshoot — big and slow reads FLOATY, not snappy. Crisp
        // menu pops land around 120ms out + ~90ms settle, with a MODEST overshoot, and get their
        // energy from squash-and-stretch (scaleX/scaleY moving in OPPOSITION, conserving area)
        // rather than from raw scale. Uniform scaling just reads as a zoom.
        /// <summary>Scale the title is visible at before it punches out. At full alpha this is
        /// legible for a frame or two, which is what makes the growth read.</summary>
        private const float TITLE_START_SCALE = 0.08f;

        // Idle breath extremes. Kept mild so that if the loop is killed mid-cycle the button is
        // never far off its authored size.
        private static readonly Vector3 PLAY_IDLE_SQUASH  = new Vector3(0.992f, 1.010f, 1f); // narrow + tall
        private static readonly Vector3 PLAY_IDLE_STRETCH = new Vector3(1.058f, 1.008f, 1f); // WIDE + barely taller
        private const float PLAY_IDLE_DUR = 0.44f;

        private static readonly Vector3 PLAY_POP_STRETCH = new Vector3(1.20f, 0.86f, 1f); // punch out: wide + flat
        private static readonly Vector3 PLAY_POP_SQUASH  = new Vector3(0.93f, 1.08f, 1f); // counter-swing: narrow + tall
        private const float PLAY_POP_OUT_DUR     = 0.11f;  // 0 -> stretched
        private const float PLAY_POP_COUNTER_DUR = 0.07f;  // stretched -> squashed
        private const float PLAY_POP_SETTLE_DUR  = 0.09f;  // squashed -> rest
        private const float PLAY_POP_TILT        = 12f;    // degrees of rotational kick
        /// <summary>Scale the button is visible at before it fires. Not 0: at full alpha a tiny
        /// button is legible for a frame or two, which is what sells "it grew from nothing".</summary>
        private static readonly Vector3 PLAY_POP_START = new Vector3(0.10f, 0.10f, 1f);

        /// <summary>Candy-Crush style PLAY reveal: punches out past full size with a rotational
        /// kick, then bounces back down elastically before handing off to the idle pulse.
        /// Runs on scaled time to match the rest of the entrance sequence. 2026-07-29.</summary>

        // ── v2 ENTRANCE (2026-11-01, Spencer: "is it too fast? anything you could improve?"). Old: drop at 1.5x with a rebound
        // AND a second dip in ~0.5s (jittery), title tossed from 8% to ~1.5x with an 8 deg tilt (burst out of the new plate),
        // PLAY from 10% with a 12 deg tilt, and goals / boosters / X just appeared. Now ~0.9s, one beat at a time:
        // backdrop -> ONE soft drop (single overshoot) -> calm title -> goals pop in order (count punches after its icon) ->
        // boosters pop in order -> softer PLAY pop -> idle pulse -> the X last. CARD_V2_ANIM = false -> the old entrance.
        public static bool  CARD_V2_ANIM = true;
        public static bool  V2_CONTENT_POPS = false;   // 11-01: content arrives ON the card (true = the staggered pops)
        public static float V2_DROP_DUR = 0.30f, V2_ITEM_STAGGER = 0.06f;   // 11-01 Spencer: .42 drop 'too slow'
        private void AnimateEntranceV2()
        {
            _entranceSeq?.Kill();
            var items = new System.Collections.Generic.List<Transform>();
            var counts = new System.Collections.Generic.List<Transform>();
            if (_v2Goals != null) foreach (Transform c in _v2Goals) { (c.name == "Count" ? counts : items).Add(c); }
            var boosts = new System.Collections.Generic.List<Transform>();
            if (_v2Boosts != null && _v2BoostBox != null && _v2BoostBox.gameObject.activeSelf) foreach (Transform c in _v2Boosts) boosts.Add(c);
            if (!V2_CONTENT_POPS)
            {   // 11-01 Spencer: "instead of everything popping up ... just make it all be on the card when it swoops down".
                // Everything rests at full size from the first frame; the card carries it all in one drop, then PLAY breathes.
                foreach (var t in items) { t.DOKill(); t.localScale = Vector3.one; }
                foreach (var t in counts) { t.DOKill(); t.localScale = Vector3.one; }
                foreach (var t in boosts) { t.DOKill(); t.localScale = Vector3.one; }
                if (_v2Title != null) { _v2Title.transform.DOKill(); _v2Title.transform.localScale = Vector3.one; }
                if (_closeBtn != null) { _closeBtn.transform.DOKill(); _closeBtn.transform.localScale = Vector3.one; }
                if (_btnPlay != null) { _btnPlay.transform.DOKill(); _btnPlay.transform.localScale = Vector3.one; _btnPlay.transform.localRotation = Quaternion.identity; }
                if (_btnGroup != null) { _btnGroup.DOKill(); _btnGroup.alpha = 1f; }
                var sq = DOTween.Sequence();
                if (_overlay != null) sq.Append(_overlay.DOFade(V2_OVERLAY.a, 0.15f).SetEase(Ease.OutQuad));
                sq.InsertCallback(0.04f, () =>
                {
                    if (_panel == null) return;
                    var rt = (RectTransform)_panel.transform; rt.DOKill();
                    rt.anchoredPosition = new Vector2(0f, UIAnimations.DROP_OFFSCREEN_OFFSET);
                    rt.DOAnchorPos(Vector2.zero, V2_DROP_DUR).SetEase(Ease.OutBack, 1.15f);
                });
                sq.InsertCallback(0.04f + V2_DROP_DUR, StartPlayPulse);
                _entranceSeq = sq;
                return;
            }
            foreach (var t in items) { t.DOKill(); t.localScale = Vector3.zero; }
            foreach (var t in counts) { t.DOKill(); t.localScale = Vector3.zero; }
            foreach (var t in boosts) { t.DOKill(); t.localScale = Vector3.zero; }
            if (_v2Title != null) { _v2Title.transform.DOKill(); _v2Title.transform.localScale = Vector3.zero; }
            if (_closeBtn != null) { _closeBtn.transform.DOKill(); _closeBtn.transform.localScale = Vector3.zero; }
            if (_btnPlay != null) { _btnPlay.transform.DOKill(); _btnPlay.transform.localScale = Vector3.zero; }

            var seq = DOTween.Sequence();
            if (_overlay != null) seq.Append(_overlay.DOFade(V2_OVERLAY.a, 0.15f).SetEase(Ease.OutQuad));
            seq.AppendInterval(0f);   // the card drops WHILE the backdrop fades (was after it)
            // one soft drop: from parked off-screen to rest with a single overshoot
            seq.InsertCallback(0.04f, () =>
            {
                if (_panel == null) return;
                var rt = (RectTransform)_panel.transform; rt.DOKill();
                rt.anchoredPosition = new Vector2(0f, UIAnimations.DROP_OFFSCREEN_OFFSET);
                rt.DOAnchorPos(Vector2.zero, V2_DROP_DUR).SetEase(Ease.OutBack, 1.15f);
            });
            seq.AppendInterval(Mathf.Max(0f, 0.04f + V2_DROP_DUR * 0.6f - 0.15f));   // content starts as the card settles, not after
            // calm title: 60% -> ~1.1 -> 1, no tilt (stays inside the arched plate)
            seq.AppendCallback(() =>
            {
                if (_v2Title == null) return;
                var t = _v2Title.transform; t.localScale = Vector3.one * 0.6f;
                t.DOScale(1f, 0.32f).SetEase(Ease.OutBack, 2.0f);
            });
            seq.AppendInterval(0.12f);
            // goals in order: icon pops, its count punches in just after
            for (int i = 0; i < items.Count; i++)
            {
                var icon = items[i]; var cnt = i < counts.Count ? counts[i] : null;
                seq.AppendCallback(() =>
                {
                    if (icon != null) icon.DOScale(1f, 0.24f).SetEase(Ease.OutBack, 2.4f);
                    if (cnt != null) cnt.DOScale(1f, 0.2f).SetEase(Ease.OutBack, 3f).SetDelay(0.08f);
                });
                seq.AppendInterval(V2_ITEM_STAGGER);
            }
            if (boosts.Count > 0)
            {
                seq.AppendInterval(0.06f);
                foreach (var b in boosts)
                {
                    var bt = b;
                    seq.AppendCallback(() => { if (bt != null) bt.DOScale(1f, 0.22f).SetEase(Ease.OutBack, 2.2f); });
                    seq.AppendInterval(V2_ITEM_STAGGER);
                }
            }
            seq.AppendInterval(0.08f);
            seq.AppendCallback(() => { if (_btnGroup != null) { _btnGroup.DOKill(); _btnGroup.alpha = 1f; } PopInPlayV2(); });
            seq.AppendInterval(0.2f);
            seq.AppendCallback(() => { if (_closeBtn != null) _closeBtn.transform.DOScale(1f, 0.18f).SetEase(Ease.OutBack, 2.2f); });
            _entranceSeq = seq;
        }

        /// <summary>Softer PLAY pop for the v2 card: from 40%, a lighter stretch / counter-swing, 4 deg tilt, then the idle pulse.</summary>
        private void PopInPlayV2()
        {
            if (_btnPlay == null) return;
            var t = _btnPlay.transform; t.DOKill(); t.localRotation = Quaternion.identity;
            t.localScale = new Vector3(0.4f, 0.4f, 1f);
            var pop = DOTween.Sequence();
            pop.Append(t.DOScale(new Vector3(1.10f, 0.93f, 1f), 0.13f).SetEase(Ease.InQuad));
            pop.Join(t.DOLocalRotate(new Vector3(0f, 0f, 4f), 0.13f));
            pop.Append(t.DOScale(new Vector3(0.97f, 1.03f, 1f), 0.08f).SetEase(Ease.OutQuad));
            pop.Join(t.DOLocalRotate(Vector3.zero, 0.08f));
            pop.Append(t.DOScale(Vector3.one, 0.1f).SetEase(Ease.OutBack, 1.5f));
            pop.OnComplete(StartPlayPulse);
        }

        private void PopInPlayButton()
        {
            if (_btnPlay == null) return;
            var t = _btnPlay.transform;
            t.DOKill();
            t.localRotation = Quaternion.identity;   // never punch from a leftover tilt
            t.localScale = PLAY_POP_START;

            // Squash-and-stretch, three short beats totalling ~270ms:
            //   0 -> WIDE+FLAT   (the punch out; area-conserving stretch sells the force)
            //     -> NARROW+TALL (counter-swing, smaller — a decaying spring)
            //     -> rest        (tiny OutBack so it doesn't land dead)
            // The direction REVERSES at each seam, so momentary zero velocity there is correct —
            // that's the turnaround. The earlier "stop at the top" was different: OutBack's long
            // deceleration tail made it LINGER at the peak while still travelling the same way.
            Sequence pop = DOTween.Sequence();
            // InQuad, NOT OutQuad. OutQuad is front-loaded — it covers most of the distance in
            // the first frames, so the button never visibly reads as small. InQuad accelerates
            // INTO the peak: it stays tiny for a beat, then rockets out and slams into the
            // stretch at max velocity, which is what the counter-swing then reverses.
            pop.Append(t.DOScale(PLAY_POP_STRETCH, PLAY_POP_OUT_DUR).SetEase(Ease.InQuad));
            pop.Append(t.DOScale(PLAY_POP_SQUASH,  PLAY_POP_COUNTER_DUR).SetEase(Ease.InOutQuad));
            pop.Append(t.DOScale(Vector3.one,      PLAY_POP_SETTLE_DUR).SetEase(Ease.OutBack, 2.2f));
            // Rotation across the whole pop so the wobble decays with the scale.
            pop.Join(t.DOPunchRotation(new Vector3(0f, 0f, PLAY_POP_TILT),
                                       PLAY_POP_OUT_DUR + PLAY_POP_COUNTER_DUR + PLAY_POP_SETTLE_DUR,
                                       7, 0.9f));
            // Cartoon plop on the IMPACT — the instant it reaches max stretch and the counter-swing
            // reverses. (Replaced the tile_land hit, removed 2026-07-30.)
            pop.InsertCallback(PLAY_POP_OUT_DUR, () => GameAudio.Instance?.PlayPlayButtonPop());
            pop.OnComplete(StartPlayPulse);          // idle pulse only AFTER the pop finishes
        }

        /// <summary>11-03: false = PLAY sits still at rest (no idle breathing). The tap press feedback is unchanged.</summary>
        public static bool PLAY_IDLE_PULSE = false;
        private void StartPlayPulse()
        {
            if (_btnPlay == null) return;
            var t = _btnPlay.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            t.localRotation = Quaternion.identity;   // clear any tilt left by an interrupted pop
            if (!PLAY_IDLE_PULSE) return;            // 11-03 Spencer: "make the play button on the level card not have an animation"
            // Cartoon breath, not a zoom: X grows much more than Y, so the button INFLATES wide
            // as it swells and narrows as it settles. A uniform 1.07 scale reads as a camera
            // push-in; opposing the axes is what makes it feel like a squashy object.
            t.localScale = PLAY_IDLE_SQUASH;
            t.DOScale(PLAY_IDLE_STRETCH, PLAY_IDLE_DUR)
             .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
        }

        private void OnPlay()
        {
            if (_isDismissing) return;
            _isDismissing = true;
            if (_preview) { EndPreview(); return; }
            if ((_boosterRow != null && _boosterRow.gameObject.activeSelf)
                || (CARD_V2 && _v2BoostBox != null && _v2BoostBox.gameObject.activeSelf)) PreBoosters.CommitSelection(_levelNum);   // spent on PLAY (v2 row too - 11-01)

            KillTweens();
            // (press/release SFX are wired on the button's PointerDown + onClick — no extra SFX here)

            // Shimmer fires on the SAME frame as the tap, layered over the button's press/release
            // SFX rather than following them. 2026-07-29.
            GameAudio.Instance?.PlayLevelLoadShimmer();

            // Pause is held THROUGH the exit (board frozen while the card flies up), released in Hide —
            // same as StageClearModal so the board doesn't lurch while the panel exits.
            if (_panel != null)
            {
                var rt = _panel.transform as RectTransform;
                if (rt != null) UIAnimations.ExitUp(rt, Hide, speedMult: 1.5f);
                else Hide();
            }
            else Hide();
        }

        // X / cancel (map-flow only): fly the card out like PLAY, but DON'T start the level — return to the bare
        // map instead. Gameplay stays paused (the map holds the pause); tapping the node re-opens this. 2026-07-13.
        private void OnCancel()
        {
            if (_preview && !_isDismissing) { _isDismissing = true; EndPreview(); return; }
            if (_isDismissing) return;
            _isDismissing = true;
            KillTweens();
            if (_panel != null)
            {
                var rt = _panel.transform as RectTransform;
                if (rt != null) UIAnimations.ExitUp(rt, HideCancelled, speedMult: 1.5f);
                else HideCancelled();
            }
            else HideCancelled();
        }

        private void HideCancelled()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _isPresenting = false;
            OnCancelled?.Invoke(); // map goes bare (no pause release, no OnPlayStarted → level doesn't start)
        }

        /// <summary>Fires when the goal modal is dismissed → gameplay begins. The tutorial gating layer
        /// uses this to start its coaching at the moment the board becomes interactive. 2026-06-25.</summary>
        public static event System.Action OnPlayStarted;
        /// <summary>Fires when the modal is X-cancelled (map-flow) → return to the bare map without starting.</summary>
        public static event System.Action OnCancelled;

        public static float DIM_OUT_SECONDS = 0.30f;   // = LevelMapPanel.FadeMapOut
        private void Hide()
        {
            bool wasPresenting = _isPresenting;
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
            // Map-flow (Phase 1): the map was playing Skybound and StageClearModal.FinalizeDismiss no longer starts
            // the gameplay track (it hands off to the map), so START the level's music HERE on PLAY. Each level gets
            // a fresh survival track — same as the non-map flow did on stage-clear. 2026-07-13 Spencer.
            if (LevelMapPanel.MapFlowEnabled && wasPresenting)
                GameAudio.Instance?.PlaySurvivalMusic();
            // 11-06 (L1-20 recording 02:09.83): switching the canvas off dropped the dim in ONE frame, so the undimmed home
            // flashed before LevelMapPanel's 0.30 s fade. Fade the dim over the same 0.30 s, then switch off - unless the card
            // was re-shown meanwhile (KillTweens kills this tween on any new show).
            if (_canvas != null)
            {
                var cv = _canvas;
                if (_overlay != null && _overlay.color.a > 0.01f)
                {
                    _overlay.DOKill();
                    _overlay.DOFade(0f, DIM_OUT_SECONDS).SetUpdate(true)
                        .OnComplete(() => { if (cv != null && !_isPresenting) cv.gameObject.SetActive(false); });
                }
                else cv.gameObject.SetActive(false);
            }
            _isPresenting = false;
            if (wasPresenting) OnPlayStarted?.Invoke();
        }

        // ── Entrance state helpers (mirror StageClearModal) ───────────────────────

        private void ResetEntranceState()
        {
            if (_overlay != null)
            {
                Color c = _overlay.color;
                _overlay.color = new Color(c.r, c.g, c.b, 0f);
            }
            if (_panel != null)
            {
                _panel.transform.localScale = Vector3.one;
                // Park the card OFF-SCREEN (not at rest) so it stays hidden during the backdrop fade — otherwise the
                // cream card sits visible at center for 0.12s, THEN DropInWithBounce snaps it up and drops it, which
                // reads as the entry animation playing twice. AnimateEntrance restores rest right before the drop.
                // Mirrors StageClearModal. 2026-07-13 Spencer.
                if (_panel.transform is RectTransform rt) rt.anchoredPosition = new Vector2(0f, UIAnimations.DROP_OFFSCREEN_OFFSET);
            }
            SetTextAlpha(_titleText, 0f);
            SetTextAlpha(_goalText, 0f);
            SetTextAlpha(_descText, 0f);
            if (_iconGroup != null) _iconGroup.alpha = 0f;
            if (_btnGroup != null) _btnGroup.alpha = 0f;
            if (_btnPlay != null) _btnPlay.transform.localScale = Vector3.one;
        }

        private void KillTweens()
        {
            _entranceSeq?.Kill();
            _entranceSeq = null;
            if (_overlay != null) _overlay.DOKill();
            if (_titleText != null) { _titleText.DOKill(); _titleText.transform.DOKill(); }
            if (_descText != null) _descText.DOKill();
            if (_iconGroup != null) _iconGroup.DOKill();
            if (_btnGroup != null) _btnGroup.DOKill();
            if (_panel != null) _panel.transform.DOKill();
            if (_btnPlay != null) _btnPlay.transform.DOKill();
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

        // ── UI construction ─────────────────────────────────────────────────────

        private void BuildUI()
        {
            var canvasGO = new GameObject("LevelIntroCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 172; // above the level MAP (170) so the play modal drops OVER it (Candy-Crush).
                                        // Doesn't coexist with StageClearModal (sequential), so being above it is fine.

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight  = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Dim, tap-blocking backdrop — semi-transparent so the game board shows through.
            var overlayGO = new GameObject("Overlay", typeof(RectTransform), typeof(Image));
            overlayGO.transform.SetParent(canvasGO.transform, false);
            var oRT = overlayGO.GetComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero; oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero; oRT.offsetMax = Vector2.zero;
            _overlay = overlayGO.GetComponent<Image>();
            _overlay.color = OVERLAY_BG;
            _overlay.raycastTarget = true;

            // Card.
            _panel = new GameObject("Card", typeof(RectTransform), typeof(Image));
            _panel.transform.SetParent(canvasGO.transform, false);
            var pRT = _panel.GetComponent<RectTransform>();
            // Sizing from Spencer's PSD mockup (canvas 1179×2556; modal X43 Y577 W1090 H1130). 2026-06-15.
            pRT.anchorMin = new Vector2(0.037f, 0.332f);
            pRT.anchorMax = new Vector2(0.961f, 0.774f);
            pRT.offsetMin = Vector2.zero; pRT.offsetMax = Vector2.zero;
            var pImg = _panel.GetComponent<Image>();
            pImg.color = CARD_BG;
            // Cartoonish rounded corners (9-sliced). Card rounds all 4 (the bottom shows cream; the top
            // is covered by the header, which rounds its own top to match). 2026-06-23 Spencer.
            pImg.sprite = MenuUI.GetRoundedRectSprite(44);
            pImg.type = Image.Type.Sliced;

            // Header strip with the level number.
            var headerGO = new GameObject("Header", typeof(RectTransform), typeof(Image));
            headerGO.transform.SetParent(_panel.transform, false);
            var hRT = headerGO.GetComponent<RectTransform>();
            hRT.anchorMin = new Vector2(0f, 0.80f);
            hRT.anchorMax = new Vector2(1f, 1f);
            hRT.offsetMin = Vector2.zero; hRT.offsetMax = Vector2.zero;
            var hImg = headerGO.GetComponent<Image>();
            hImg.color = HEADER_BG;
            // Round ONLY the top corners (match the card); bottom stays square where it meets the body.
            hImg.sprite = MenuUI.GetRoundedRectSprite(44, roundTop: true, roundBottom: false);
            hImg.type = Image.Type.Sliced;

            _titleText = CreateLabel(headerGO.transform, "Title",
                new Vector2(0.04f, 0f), new Vector2(0.96f, 1f), "LEVEL 1", 38, TITLE_COL);
            _titleText.fontStyle = FontStyle.Bold;

            // Icon holder (left of the body) + CanvasGroup so the whole icon fades in as one.
            _iconHolder = new GameObject("IconHolder", typeof(RectTransform));
            _iconHolder.transform.SetParent(_panel.transform, false);
            var iRT = _iconHolder.GetComponent<RectTransform>();
            iRT.anchorMin = new Vector2(0.10f, 0.34f);
            iRT.anchorMax = new Vector2(0.37f, 0.74f);
            iRT.offsetMin = Vector2.zero; iRT.offsetMax = Vector2.zero;
            _iconGroup = _iconHolder.AddComponent<CanvasGroup>();

            // "GOAL" subtitle — sits above the objective text in every Level modal. 2026-06-23 Spencer.
            _goalText = CreateLabel(_panel.transform, "Goal",
                new Vector2(0.46f, 0.70f), new Vector2(0.95f, 0.78f), "GOAL", 24, HEADER_BG);
            _goalText.fontStyle = FontStyle.Bold;

            // Description (right of the icon).
            _descText = CreateLabel(_panel.transform, "Desc",
                new Vector2(0.46f, 0.32f), new Vector2(0.95f, 0.76f), "", 30, DESC_COL);
            _descText.alignment = TextAnchor.MiddleLeft;
            _descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _descText.fontStyle = FontStyle.Bold;

            // PLAY button + CanvasGroup for the staggered fade-in.
            // 2026-10-27 Spencer: the green PILL Play art button (v3 style) replaces the flat orange one. ArtButton brings
            // its own press squeeze and the two-stage press/release SFX, so none are added here. Falls back to the old
            // button if the art is missing.
            ArtButton artPlay = USE_ART_PLAY ? ArtButton.Create(_panel.transform, "pill_play", "Play", lilita: false, titleMedium: false) : null;   // 11-01 Spencer: WordSlop, same as the home Level button
            if (artPlay != null)
            {
                artPlay.name = "BtnPlay";
                var prt = (RectTransform)artPlay.transform;
                prt.anchorMin = new Vector2(0.26f, 0.05f); prt.anchorMax = new Vector2(0.74f, 0.26f);   // same slot as before;
                prt.offsetMin = prt.offsetMax = Vector2.zero; prt.pivot = new Vector2(0.5f, 0.5f);          // the art keeps its aspect inside it
                artPlay.Refit();
                artPlay.Button.onClick.AddListener(OnPlay);
                _btnPlay = artPlay.gameObject;
                _btnGroup = _btnPlay.AddComponent<CanvasGroup>();
            }
            int before = artPlay != null ? int.MaxValue : _panel.transform.childCount;
            if (artPlay == null) MenuUI.CreateButton(_panel.transform, "BtnPlay",
                new Vector2(0.26f, 0.05f), new Vector2(0.74f, 0.26f),
                "PLAY", new Color(0.96f, 0.63f, 0.16f, 1f), Color.white, 30, OnPlay); // 2026-06-24: warm orange CTA
            if (_panel.transform.childCount > before)
            {
                _btnPlay = _panel.transform.GetChild(_panel.transform.childCount - 1).gameObject;
                _btnGroup = _btnPlay.GetComponent<CanvasGroup>();
                if (_btnGroup == null) _btnGroup = _btnPlay.AddComponent<CanvasGroup>();

                // PLAY uses the SAME two-stage press/release SFX as the level-completed CONTINUE button:
                // PlayMultiPopPress on pointer-DOWN, PlayMultiPopRelease on release. 2026-06-15 Spencer.
                var playBtn = _btnPlay.GetComponent<Button>();
                if (playBtn != null)
                {
                    playBtn.onClick.RemoveAllListeners();
                    var bt = _btnPlay.transform;
                    playBtn.onClick.AddListener(() => UIAnimations.ButtonPress(bt));
                    playBtn.onClick.AddListener(() => GameAudio.Instance?.PlayMultiPopRelease()); // release half
                    playBtn.onClick.AddListener(OnPlay);
                }
                var trigger = _btnPlay.GetComponent<EventTrigger>();
                if (trigger == null) trigger = _btnPlay.AddComponent<EventTrigger>();
                var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
                pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress()); // press half
                trigger.triggers.Add(pdEntry);
            }

            // X / cancel button — top-right of the card, on the header. Only meaningful in the map-flow (returns
            // to the bare map); shown/hidden per MapFlowEnabled in Show. 2026-07-13 Spencer.
            _closeBtn = new GameObject("CloseBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            _closeBtn.transform.SetParent(_panel.transform, false);
            var xrt = _closeBtn.GetComponent<RectTransform>();
            xrt.anchorMin = xrt.anchorMax = new Vector2(1f, 1f);
            xrt.pivot = new Vector2(0.5f, 0.5f);
            xrt.sizeDelta = new Vector2(64f, 64f);
            xrt.anchoredPosition = new Vector2(-40f, -34f);
            var ximg = _closeBtn.GetComponent<Image>();
            ximg.sprite = MenuUI.GetRoundedRectSprite(32); // radius = half size → a circle chip
            ximg.type = Image.Type.Sliced;
            ximg.color = new Color(0.28f, 0.18f, 0.26f, 1f); // dark chip on the pink header
            var xlblGO = new GameObject("X", typeof(RectTransform), typeof(TextMeshProUGUI));
            xlblGO.transform.SetParent(_closeBtn.transform, false);
            var xlrt = xlblGO.GetComponent<RectTransform>();
            xlrt.anchorMin = Vector2.zero; xlrt.anchorMax = Vector2.one; xlrt.offsetMin = Vector2.zero; xlrt.offsetMax = Vector2.zero;
            var xtmp = xlblGO.GetComponent<TextMeshProUGUI>();
            var xfont = GameFont.GetDisplayTMP(); if (xfont != null) xtmp.font = xfont;
            xtmp.text = "✕"; xtmp.fontSize = 38; xtmp.alignment = TextAlignmentOptions.Center; xtmp.color = Color.white; xtmp.raycastTarget = false;
            // Corner cancel button uses the SETTINGS button's push-down + release SFX: PlaySettingsPress on
            // PointerDown, PlaySettingsRelease on release/click. 2026-07-13 Spencer.
            var closeBtnComp = _closeBtn.GetComponent<Button>();
            closeBtnComp.onClick.AddListener(() => GameAudio.Instance?.PlaySettingsRelease()); // release half
            closeBtnComp.onClick.AddListener(OnCancel);
            var xTrigger = _closeBtn.GetComponent<EventTrigger>();
            if (xTrigger == null) xTrigger = _closeBtn.AddComponent<EventTrigger>();
            var xPdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            xPdEntry.callback.AddListener((_) => GameAudio.Instance?.PlaySettingsPress()); // press half
            xTrigger.triggers.Add(xPdEntry);
            _closeBtn.SetActive(false); // toggled on per map-flow in Show
            if (CARD_V2) BuildV2();
        }


        // ── LEVEL CARD v2 (2026-11-01, Spencer approved panels/ref_1101/panel_card_mock_v32.png: "that looks great. start building").
        // Our own bevelled menu panel, built from the RK Level-58 card's measured structure but ours (purple body, arched header,
        // soft gold frames, sky-blue booster plates, red-badge X). Art = Resources/UI/Panel/* exported by panel_export.py from the
        // mock code; layout numbers are the mock's own pixels (card 1000 wide on a 1179-wide screen) inside a root scaled to the
        // canvas, so the card keeps the mock's proportions on every screen. The old card's animation, PLAY and X objects are
        // REUSED (re-parented), so entrance, press SFX and cancel behave as before. CARD_V2 = false -> the old card. ──
        public static bool CARD_V2 = true;
        private const float V2_SCREEN_W = 1179f, V2_CW = 1000f;
        private const float V2_GOAL_Y0 = 210f, V2_GOAL_H = 310f, V2_GAP = 50f, V2_BOOST_H = 330f, V2_PLAY_W = 600f, V2_PLAY_H = 204f, V2_SIDE = 70f;
        public static float V2_COUNT_CAP = 70f;
        // 11-06 Spencer "composition a bit better": measured in game the visible Play button sat ~30 px above the purple body's
        // bottom vs 60 below the goal box (the rect's 60 included the button's own margin), so it read crammed. 60 -> 90.
        public static float V2_PLAY_BOTTOM = 90f;
        public static float PLAY_SCALE = 1.25f;
        public static float V2_TITLE_TRACK = -5.5f;   // TMP 1/100 em   // goal count cap height, mock px
        public static Color V2_LABEL_INK = new Color32(0x3A, 0x1A, 0x7A, 0xFF), V2_TITLE_LINE = new Color32(0x1E, 0x0A, 0x52, 0xFF);
        public static bool  V2_TITLE_RK = true;
        public static Color V2_TITLE_EDGE_RK = new Color32(0x2B, 0x0B, 0x6E, 0xFF);
        public static float V2_TITLE_STROKE_RK = 0.07f, V2_TITLE_DROP_RK = -1.0f;
        public static Color V2_OVERLAY => UISeparation.MODAL_DIM;   // 11-02 shared dim (was #1A0B2E .86)   // measured in game 15:04: walls 109 vs mock 87 at .80 (linear blend) -> .86
        private RectTransform _v2Root, _v2GoalBox, _v2BoostBox, _v2Goals, _v2Boosts;
        /// <summary>11-17 (Spencer: "does settings seem centered to you?" -> "yes lift the others to match"). MEASURED on the Settings
        /// and Try Again? cards (720 px): at y = 10 the title's capitals had 50 px of plate above them and 28 below (11 px low).
        /// Every card header (Level, Continue? / Try Again?, Chapter Complete, the Settings cards) now sits this many card px
        /// higher (~8 px on a 720 screen; not the full 11 - the plate's top is arched). 0 = the old position.</summary>
        public static float V2_TITLE_LIFT = 13f;
        private TextMeshProUGUI _v2Title, _v2GoalLabel, _v2BoostLabel;

        private class V2Scale : MonoBehaviour
        {   // the root works in mock pixels: scale = canvas units per mock pixel (screen width / 1179)
            public RectTransform canvasRT;
            private void LateUpdate()
            {
                if (canvasRT == null) return;
                float k = canvasRT.rect.width / V2_SCREEN_W; transform.localScale = new Vector3(k, k, 1f);
                // the mock centres the WHOLE card (header included) at 47% down; the card's body centre lands at 48.4% down.
                // _panel's centre is at 44.7% down, so drop the body by the difference (in canvas units).
                ((RectTransform)transform).anchoredPosition = new Vector2(0f, -(0.4836f - 0.447f) * canvasRT.rect.height);
            }
        }

        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_v2Sprites = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite V2Sprite(string name, Vector4 border = default)
        {
            string key = name + border;
            if (s_v2Sprites.TryGetValue(key, out var sp) && sp != null) return sp;
            var tex = Resources.Load<Texture2D>("UI/Panel/" + name);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border) : null;
            s_v2Sprites[key] = sp; return sp;
        }

        /// <summary>Place <paramref name="rt"/> centred at (x, y) mock px from the root's TOP-LEFT (y down).</summary>
        private static void V2Place(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
            rt.localScale = Vector3.one; rt.localRotation = Quaternion.identity;
        }
        private static RectTransform V2Node(string name, Transform parent, params System.Type[] comps)
        {
            var list = new System.Collections.Generic.List<System.Type> { typeof(RectTransform) }; list.AddRange(comps);
            var go = new GameObject(name, list.ToArray()); go.transform.SetParent(parent, false); return (RectTransform)go.transform;
        }
        private static Image V2Image(string name, Transform parent, Sprite sp, bool sliced = false)
        {
            var im = V2Node(name, parent, typeof(Image)).GetComponent<Image>(); im.sprite = sp; im.raycastTarget = false;
            if (sliced) im.type = Image.Type.Sliced; return im;
        }
        private static TextMeshProUGUI V2Text(string name, Transform parent)
        {
            var t = V2Node(name, parent, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            t.alignment = TextAlignmentOptions.Center; t.textWrappingMode = TextWrappingModes.NoWrap; t.raycastTarget = false;
            t.overflowMode = TextOverflowModes.Overflow; return t;
        }
        // WordSlop Button: cap height = 0.594 em; the recipe helpers grow the size by RECIPE_MAX_MUL, so divide it back out
        private static float V2FontFor(float capPx) => capPx / 0.594f / HUDManager.RECIPE_MAX_MUL;

        private void BuildV2()
        {
            var canvasRT = (RectTransform)_canvas.transform;
            if (_overlay != null) _overlay.color = V2_OVERLAY;
            var pImg = _panel.GetComponent<Image>(); if (pImg != null) { pImg.color = Color.clear; pImg.raycastTarget = false; }
            foreach (Transform ch in _panel.transform) if (ch.name == "Header") ch.gameObject.SetActive(false);

            _v2Root = V2Node("CardV2", _panel.transform);
            _v2Root.anchorMin = _v2Root.anchorMax = new Vector2(0.5f, 0.5f); _v2Root.pivot = new Vector2(0.5f, 0.5f);
            _v2Root.gameObject.AddComponent<V2Scale>().canvasRT = canvasRT;
            _v2Root.localScale = Vector3.one * (canvasRT.rect.width > 0f ? canvasRT.rect.width / V2_SCREEN_W : 0.46f);

            // panel: 9-sliced; the sprite's body sits 90 in from left/top/right and 110 from the bottom (lip + drop shadow)
            var frame = V2Image("Frame", _v2Root, V2Sprite("panel", new Vector4(205, 225, 205, 205)), sliced: true);
            var frt = frame.rectTransform; frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one;
            frt.offsetMin = new Vector2(-90f, -110f); frt.offsetMax = new Vector2(90f, 90f);

            _v2GoalBox = V2Image("GoalBox", _v2Root, V2Sprite("cream", new Vector4(52, 52, 52, 52)), sliced: true).rectTransform;
            _v2BoostBox = V2Image("BoostBox", _v2Root, V2Sprite("cream", new Vector4(52, 52, 52, 52)), sliced: true).rectTransform;
            _v2GoalLabel = V2Text("GoalsLabel", _v2GoalBox); _v2BoostLabel = V2Text("BoostLabel", _v2BoostBox);
            foreach (var l in new[] { _v2GoalLabel, _v2BoostLabel })
            {
                l.fontSize = V2FontFor(41f); HUDManager.ApplyRecipeFlat(l); l.color = V2_LABEL_INK;
            }
            _v2GoalLabel.text = "Goals"; _v2BoostLabel.text = "Select Boosters";
            V2Place(_v2GoalLabel.rectTransform, (V2_CW - 2 * V2_SIDE) / 2f, 52f, 700f, 80f);
            V2Place(_v2BoostLabel.rectTransform, (V2_CW - 2 * V2_SIDE) / 2f, 52f, 700f, 80f);
            _v2Goals = V2Node("Goals", _v2GoalBox); _v2Boosts = V2Node("Boosters", _v2BoostBox);

            // header: arched plate (+ its lip and the shadow it casts) - plate occupies sprite x 20..800, y 20..298
            var header = V2Image("HeaderPlate", _v2Root, V2Sprite("header"));
            V2Place(header.rectTransform, 110f - 20f + 410f, 130f - 298f + 179f, 820f, 358f);
            _v2Title = V2Text("Title", _v2Root);
            _v2Title.fontSize = V2FontFor(102f);
            // 11-16 Spencer ("the text treatment on the Level 2 header ... a lot of shading"; picked B of ~/Desktop/header_text_mock.png): RK
            // style - a THIN edge round the letters and the depth only in the down-right drop, in a deep plate purple instead of near-black
            // navy (the thick .19 navy ring + drop made a muddy dark halo). V2_TITLE_RK = false -> the old .19 / -0.9 navy treatment.
            if (V2_TITLE_RK) HUDManager.ApplyRecipeCount(_v2Title, V2_TITLE_EDGE_RK, V2_TITLE_STROKE_RK, V2_TITLE_DROP_RK, Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF"));
            else HUDManager.ApplyRecipeCount(_v2Title, V2_TITLE_LINE, 0.19f, -0.9f, Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF"));   // 11-06 Spencer: tried bold "WordSlop Title SDF", now the MEDIUM cut (same metrics as Button; missing -> Button)
            _v2Title.characterSpacing = 0f;   // 11-06 Spencer: normal kerning (was V2_TITLE_TRACK -5.5; ChapterEndCard still uses it). 11-01 Spencer: match the mock's kerning. -3 (the mock's 0.03 em) MEASURED looser in game: gaps 7-8 px vs 4-5 at a 105 px cap -> -5.5
            V2Place(_v2Title.rectTransform, V2_CW / 2f, 10f - V2_TITLE_LIFT, 760f, 170f);

            // PLAY (the existing art button, re-parented) and the X (re-skinned to the gold ring + red badge)
            if (_btnPlay != null)
            {
                _btnPlay.transform.SetParent(_v2Root, false);
                var ab = _btnPlay.GetComponent<ArtButton>(); if (ab != null) ab.Refit();
            }
            if (_closeBtn != null)
            {
                _closeBtn.transform.SetParent(_v2Root, false);
                var xi = _closeBtn.GetComponent<Image>(); xi.sprite = V2Sprite("close"); xi.type = Image.Type.Simple; xi.color = Color.white;
                foreach (Transform ch in _closeBtn.transform) ch.gameObject.SetActive(false);   // the old text X
                // sprite centre of the ring = (84, 84) of 168x180; the ring sits on the panel's top-right corner like the mock
                V2Place((RectTransform)_closeBtn.transform, V2_CW - 0.62f * 168f + 84f, -0.62f * 168f + 84f + 6f, 168f, 180f);
            }
        }

        // FX menu preview (11-01 Spencer: "a button in fx test to bring it down"): drops the v2 card in for the current level
        // without touching the board, the pause or the map; PLAY / X just lift it away.
        private bool _preview;
        public void PreviewV2(bool withBoosters)
        {
            if (_canvas == null || !CARD_V2 || _v2Root == null || _isPresenting) return;
            int lvl = SurvivalManager.Instance != null ? SurvivalManager.Instance.CurrentStageIndex : 1;
            _preview = true; _isPresenting = true; _isDismissing = false; _levelNum = lvl;
            ApplyV2(LevelRules.GoalIconsFor(lvl), withBoosters, lvl);
            if (_closeBtn != null) _closeBtn.SetActive(true);
            GameAudio.Instance?.PlayWhooshFast();
            KillTweens(); ResetEntranceState(); _canvas.gameObject.SetActive(true); AnimateEntrance();
        }
        private void EndPreview()
        {
            KillTweens();
            var rt = _panel != null ? _panel.transform as RectTransform : null;
            System.Action done = () => { if (_canvas != null) _canvas.gameObject.SetActive(false); _isPresenting = false; _isDismissing = false; _preview = false; };
            if (rt != null) UIAnimations.ExitUp(rt, () => done(), speedMult: 1.5f); else done();
        }

        private void ApplyV2(System.Collections.Generic.List<LevelRules.GoalIconInfo> goals, bool boost, int levelNum)
        {
            // the old card's text/pictures stand down (BuildGoalRow re-activates some of them every Show)
            if (_goalText != null) _goalText.gameObject.SetActive(false);
            if (_descText != null) _descText.gameObject.SetActive(false);
            if (_iconHolder != null) _iconHolder.SetActive(false);
            if (_goalRow != null) _goalRow.SetActive(false);
            if (_boosterRow != null) _boosterRow.gameObject.SetActive(false);

            float contentBottom = V2_GOAL_Y0 + V2_GOAL_H + (boost ? V2_GAP + V2_BOOST_H : 0f);
            float ch = contentBottom + 60f + V2_PLAY_H + V2_PLAY_BOTTOM;
            _v2Root.sizeDelta = new Vector2(V2_CW, ch);

            _v2Title.text = $"Level {LevelMapPanel.DisplayNum(levelNum)}";

            float boxW = V2_CW - 2f * V2_SIDE;
            V2Place(_v2GoalBox, V2_CW / 2f, V2_GOAL_Y0 + V2_GOAL_H / 2f, boxW, V2_GOAL_H);
            _v2BoostBox.gameObject.SetActive(boost);
            if (boost) V2Place(_v2BoostBox, V2_CW / 2f, V2_GOAL_Y0 + V2_GOAL_H + V2_GAP + V2_BOOST_H / 2f, boxW, V2_BOOST_H);
            if (_btnPlay != null)
            {
                // MEASURED in game (11-01): a 600x204 rect drew a 534-wide button (Refit fits the frame inside with margin); the
                // 800x400 attempt drew ~690 and sat on the rim. 600/534 = 1.124 -> the mock's 600-wide button, bottom 60 above the body edge.
                const float PLAY_FIT = 560f / 534f;   // 11-01 booster-card compare: the mock's visible button is ~560 wide (600 drew 606)
                // 11-16 Spencer ("its not really standing out in its composition", picked B of ~/Desktop/play_button_mock.png): PLAY_SCALE 1.25,
                // grown about the same centre - it out-weighs the Goals box now. No pulse/shine (he didn't like them).
                V2Place((RectTransform)_btnPlay.transform, V2_CW / 2f, ch - V2_PLAY_BOTTOM - V2_PLAY_H / 2f, V2_PLAY_W * PLAY_FIT * PLAY_SCALE, V2_PLAY_H * PLAY_FIT * PLAY_SCALE);
                var ab = _btnPlay.GetComponent<ArtButton>(); if (ab != null) ab.Refit();
            }

            // goals: one picture per goal with a recipe-D count; a lone goal is drawn bigger (mock: 180 vs 150)
            for (int i = _v2Goals.childCount - 1; i >= 0; i--) Destroy(_v2Goals.GetChild(i).gameObject);
            V2Place(_v2Goals, boxW / 2f, V2_GOAL_H / 2f, boxW, V2_GOAL_H);
            int n = goals != null ? goals.Count : 0;
            // GOLD RUSH (11-01 Spencer: L20's card showed an empty Goals box - "should it say something about the gold rush?"). The
            // bonus level has no goals: the box says what it is instead - "Gold Rush!", the HUD coin and one line.
            bool bonus = LevelRules.For(LevelMapPanel.RunLevel(levelNum)).Bonus;
            _v2GoalLabel.text = bonus ? "Gold Rush!" : "Goals";
            if (bonus)
            {
                n = 0;
                var coinTex = Resources.Load<Texture2D>("UI/HudBar/hud_coin");
                if (coinTex != null)
                {
                    var coin = V2Node("Coin", _v2Goals, typeof(Image)).GetComponent<Image>();
                    coin.sprite = Sprite.Create(coinTex, new Rect(0, 0, coinTex.width, coinTex.height), new Vector2(0.5f, 0.5f), 100f);
                    coin.preserveAspect = true; coin.raycastTarget = false;
                    V2Place(coin.rectTransform, boxW / 2f, 150f, 120f, 120f);
                }
                var line = V2Text("BonusLine", _v2Goals); line.text = "Every word pays coins!";
                line.fontSize = V2FontFor(36f); HUDManager.ApplyRecipeFlat(line); line.color = V2_LABEL_INK;
                V2Place(line.rectTransform, boxW / 2f, 252f, boxW - 60f, 70f);
            }
            for (int i = 0; i < n; i++)
            {
                float gsz = 150f, k = 1f;   // 11-01 Spencer: the lone gem at 180 read 'much bigger' than the mock -> 150 like the L16 mock
                float gx = boxW / n * (i + 0.5f), gy = 175f;
                var pic = V2Node("Pic_" + goals[i].Type, _v2Goals); V2Place(pic, gx, gy, gsz, gsz);
                var icon = ObjectiveIconBuilder.BuildGoalIcon(goals[i], pic, gsz);
                if (icon != null) { var irt = (RectTransform)icon.transform; irt.anchorMin = Vector2.zero; irt.anchorMax = Vector2.one; irt.offsetMin = irt.offsetMax = Vector2.zero; }
                var cnt = V2Text("Count", _v2Goals); cnt.text = goals[i].Count.ToString();
                // mock caps were in 1080-units x K (1179/1080): 78 -> 85 px real (11-01 Spencer: count read small/thin, 'wonky');
                // and the mock's heavier band (stroke ~5% cap, drop ~7%) rather than the HUD's thin D
                cnt.fontSize = V2FontFor(V2_COUNT_CAP * k);   // 11-01 Spencer: 85 'too big' -> 70
                HUDManager.ApplyRecipeCount(cnt, HUDManager.HUD_INK, HUDManager.D_STROKE, HUDManager.D_DROP);   // 11-01: .22/-.95 fattened the glyph (face dilate = stroke/2) - 'font doesnt match the mock'; D = mock's 5% stroke / 7% drop
                V2Place(cnt.rectTransform, gx + 60f * k, gy + 38f * k, 160f * k, 130f * k);
            }

            if (boost)
            {
                V2Place(_v2Boosts, boxW / 2f, V2_BOOST_H / 2f, boxW, V2_BOOST_H);
                PreBoosters.BuildRowV2(_v2Boosts, V2Sprite("booster_plate"), V2Sprite("badge_red"));
            }
        }

        private static Text CreateLabel(Transform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax, string text, int fontSize, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            var t = go.AddComponent<Text>();
            t.font = MenuUI.GetFont();
            t.text = text;
            t.fontSize = fontSize;
            t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }

    /// <summary>
    /// Builds the little objective ICON used by the level-intro modal (and the in-game Target panel).
    /// Each kind maps to a recognisable picture of WHAT the player needs, with an optional count badge.
    /// All art is procedural (rounded-tile sprites + tint) so no asset imports are needed — upgradeable
    /// to real sprites later, same as the escort-tile placeholder. 2026-06-15 Spencer.
    /// </summary>
    public static class ObjectiveIconBuilder
    {
        private static readonly Color AMBER   = new Color(1f, 0.48f, 0f, 1f);  // escort tile — bright saturated orange (Spencer 2026-06-15)
        private static readonly Color ICE     = new Color(0.62f, 0.85f, 1f, 1f);  // frosted tile
        private static readonly Color GOLD    = new Color(1f, 0.84f, 0.30f, 1f);  // vault
        private static readonly Color MAGENTA = new Color(0.93f, 0.26f, 0.82f, 1f); // primed "WORD" tile
        private static readonly Color LETTER  = new Color(0.15f, 0.15f, 0.20f, 1f);
        private static readonly Color BADGE   = new Color(0.20f, 0.45f, 0.85f, 1f); // blue count badge
        private static readonly Color ROCK    = new Color(0.13f, 0.13f, 0.16f, 1f); // hidden-word blank — black rock

        private static Material s_addMat;
        /// <summary>Additive material so the glow ADDS light (reads as a glow) instead of alpha-blending a
        /// magenta haze over the cream panel (which looked smudged). 2026-06-17 Spencer.</summary>
        private static Material AdditiveMat()
        {
            if (s_addMat != null) return s_addMat;
            var sh = Shader.Find("Legacy Shaders/Particles/Additive")
                  ?? Shader.Find("Mobile/Particles/Additive")
                  ?? Shader.Find("Particles/Additive")
                  ?? Shader.Find("Sprites/Default");
            s_addMat = new Material(sh);
            return s_addMat;
        }

        private static Sprite s_glowSprite;
        /// <summary>Procedural soft radial glow sprite (overlay UI can't bloom, so the glow IS a sprite).</summary>
        private static Sprite GlowSprite()
        {
            if (s_glowSprite != null) return s_glowSprite;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float r = size * 0.5f; var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - r) / r, dy = (y + 0.5f - r) / r;
                    float d = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                    float a = 1f - d; a = a * a * (3f - 2f * a);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px); tex.Apply();
            s_glowSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return s_glowSprite;
        }

        /// <summary>Build the icon for an objective into <paramref name="parent"/>, sized to fit a
        /// <paramref name="size"/>×<paramref name="size"/> box. badgeCount &gt; 0 adds a count badge.</summary>
        public static GameObject Build(Objective.HudIcon kind, Transform parent, float size, int badgeCount, string word = null)
        {
            switch (kind)
            {
                case Objective.HudIcon.Word:       return BuildWordCluster(parent, size, badgeCount, word);
                case Objective.HudIcon.HiddenWord: return BuildHiddenRow(parent, size, word); // word = masked; one rock per blank, single row, no badge
                case Objective.HudIcon.DropTarget: return BuildSpriteIcon(parent, size, "Tiles/common_icon_chicken", AMBER, badgeCount); // chicken placeholder
                case Objective.HudIcon.Ice:        return BuildTile(parent, size, ICE,   null, badgeCount);
                case Objective.HudIcon.Vault:      return BuildSpriteIcon(parent, size, "Tiles/Icon_ItemIcon_Treasure", GOLD, badgeCount);
                default:                           return null;
            }
        }

        /// <summary>One goal picture for the full-board briefing, using the art the player will see on the board.</summary>
        private static Sprite s_grassIcon;
        private static Sprite GrassIcon()
        {
            if (s_grassIcon != null) return s_grassIcon;
            var tex = Resources.Load<Texture2D>("Boosters/2leaf");
            if (tex != null) s_grassIcon = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            return s_grassIcon;
        }
        public static GameObject BuildGoalIcon(LevelRules.GoalIconInfo g, Transform parent, float size)
        {
            g.Count = 0;   // no badge: the card shows the count as its own label beside the picture
            switch (g.Type)
            {
                case LevelRules.GoalType.Crates:  return BuildSprite(parent, size, Tile.CrateSprite, Color.white, null, g.Count, AMBER);
                case LevelRules.GoalType.Ice:     return Tile.ICE_ART && Tile.IceArtSprite != null   // 11-03 (Spencer: "the goal for the ice level should look like the ice tiles")
                                                    ? BuildSprite(parent, size, Tile.IceArtSprite, Color.white, null, g.Count, ICE)
                                                    : BuildSprite(parent, size, Tile.NormalSprite, ICE, null, g.Count, ICE);
                case LevelRules.GoalType.Gems:    return BuildSprite(parent, size, Tile.GemSprite, Color.white, null, g.Count, GOLD);
                case LevelRules.GoalType.Safes:   return BuildSprite(parent, size, Tile.SafeSprite, Color.white, null, g.Count, AMBER);   // SAFE (11-03)
                case LevelRules.GoalType.Books:   return BuildSprite(parent, size, Tile.ShelfBookFlySprite(0), Color.white, null, g.Count, AMBER);   // BOOKSHELF (11-03): one book
                case LevelRules.GoalType.Ducks:   return BuildSprite(parent, size, Tile.DuckSprite, Color.white, null, g.Count, GOLD);
                case LevelRules.GoalType.Theme:   return BuildSprite(parent, size, LoadIconSprite(LevelRules.ThemeIconPath(g.Theme)), Color.white, null, g.Count, MAGENTA);
                case LevelRules.GoalType.Grass:   return BuildSprite(parent, size, GrassIcon(), Color.white, null, g.Count, new Color(0.36f, 0.72f, 0.24f));
                case LevelRules.GoalType.Collect: return BuildSprite(parent, size, Tile.CollectFaceSprite, Tile.CollectFaceTint, g.Letter.ToString(), g.Count, BADGE);
                default: return null;
            }
        }
        private static GameObject BuildSprite(Transform parent, float size, Sprite sprite, Color tint, string letter, int badgeCount, Color fallbackTint)
        {
            var go = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>();
            img.raycastTarget = false;
            if (sprite != null) { img.sprite = sprite; img.preserveAspect = true; img.color = tint; }
            else { img.sprite = TileRenderer.CreateSolidRoundedRect(80, 80, 18, Color.white); img.color = fallbackTint; }
            // GOAL ICON EDGE (2026-10-29, Spencer: the teal goal gem washed out on the cream panel): an edged COPY of the board
            // sprite (IconEdge, edge "B"), tint baked in; scaled by its padding so the art keeps its size (works with the HUD's
            // stretch anchors too).
            if (sprite != null)
            {
                var edged = IconEdge.Get(sprite, tint, out float emul);
                if (edged != null) { img.sprite = edged; img.color = Color.white; go.transform.localScale = Vector3.one * emul; }
            }
            if (!string.IsNullOrEmpty(letter)) AddLetter(go.transform, letter, size);
            if (badgeCount > 0) AddBadge(go.transform, badgeCount, size);
            return go;
        }

        /// <summary>Icon backed by an actual game sprite (e.g. the treasure chest). Falls back to a
        /// tinted rounded tile if the sprite can't be loaded. 2026-06-15 Spencer.</summary>
        private static GameObject BuildSpriteIcon(Transform parent, float size, string resourcePath, Color fallbackTint, int badgeCount)
        {
            var go = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>();
            var sprite = LoadIconSprite(resourcePath);
            if (sprite != null) { img.sprite = sprite; img.preserveAspect = true; img.color = Color.white; }
            else { img.sprite = TileRenderer.CreateSolidRoundedRect(80, 80, 18, Color.white); img.color = fallbackTint; }
            if (badgeCount > 0) AddBadge(go.transform, badgeCount, size);
            return go;
        }

        /// <summary>HUD-only reward icon: the coin. The intro modal keeps the treasure chest (its copy
        /// says "feed treasure chests"); the in-game Target panel shows the coin + reads "REWARD".
        /// 2026-06-18 Spencer.</summary>
        public static GameObject BuildRewardCoinIcon(Transform parent, float size)
        {
            // 2026-07-29: use the 3D crown coin. LoadIconSprite caches, so the probe is free, and
            // it falls back to the original flat icon if the new art isn't present.
            string path = LoadIconSprite("Tiles/coin3d_icon") != null
                        ? "Tiles/coin3d_icon" : "Tiles/Icon_ImageIcon_Coin";
            var go = BuildSpriteIcon(parent, size, path, GOLD, 0);
            if (go != null)
            {
                var img2 = go.GetComponent<Image>();
                MenuUI.AddIconDropShadow(img2, size);
                UIConfig.RegisterIcon(img2, UIConfig.IconSlot.CoinReward);   // live-tunable
            }
            return go;
        }

        // Some icon assets (Coin, Treasure) are imported as plain Textures, so Resources.Load<Sprite>
        // returns null and the icon used to fall back to a coloured blob. Build a Sprite from the
        // Texture2D when needed. Cached so repeated builds don't leak sprites. 2026-06-18 Spencer.
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_iconSpriteCache
            = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite LoadIconSprite(string path)
        {
            if (s_iconSpriteCache.TryGetValue(path, out var cached)) return cached;
            var sprite = Resources.Load<Sprite>(path);
            if (sprite == null)
            {
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null)
                    sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            s_iconSpriteCache[path] = sprite;
            return sprite;
        }

        private static GameObject BuildTile(Transform parent, float size, Color tint, string letter, int badgeCount)
        {
            var go = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>();
            img.sprite = TileRenderer.CreateSolidRoundedRect(80, 80, 18, Color.white);
            img.color  = tint;
            if (!string.IsNullOrEmpty(letter)) AddLetter(go.transform, letter, size);
            if (badgeCount > 0) AddBadge(go.transform, badgeCount, size);
            return go;
        }

        private static GameObject BuildWordCluster(Transform parent, float size, int badgeCount, string word)
        {
            if (string.IsNullOrEmpty(word)) word = "WORD";
            word = word.ToUpperInvariant();
            int n = word.Length;
            int cols = (n <= 4) ? 2 : 3;            // 4 letters → 2×2, 5 letters → 3 top + 2 bottom
            int rows = Mathf.CeilToInt(n / (float)cols);

            var holder = new GameObject("WordIcon", typeof(RectTransform));
            holder.transform.SetParent(parent, false);
            var hrt = holder.GetComponent<RectTransform>();
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(size, size);

            // mini-tile sized so the whole cols×rows grid fits inside `size`, with a small gap.
            float mini  = size * 0.92f / Mathf.Max(cols, rows);
            float pitch = mini * 1.06f;

            int idx = 0;
            for (int r = 0; r < rows; r++)
            {
                int tilesInRow = Mathf.Min(cols, n - r * cols);
                float rowW   = (tilesInRow - 1) * pitch;
                float startX = -rowW * 0.5f;                       // center each row
                float y      = (rows - 1) * pitch * 0.5f - r * pitch; // top row highest
                for (int c = 0; c < tilesInRow; c++)
                {
                    var t = new GameObject($"T{idx}", typeof(RectTransform), typeof(Image));
                    t.transform.SetParent(holder.transform, false);
                    var trt = t.GetComponent<RectTransform>();
                    trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
                    trt.sizeDelta = new Vector2(mini, mini);
                    trt.anchoredPosition = new Vector2(startX + c * pitch, y);
                    var img = t.GetComponent<Image>();
                    img.sprite = TileRenderer.CreateSolidRoundedRect(60, 60, 12, Color.white);
                    img.color  = MAGENTA;
                    AddLetter(t.transform, word[idx].ToString(), mini);
                    idx++;
                }
            }
            if (badgeCount > 0) AddBadge(holder.transform, badgeCount, size);
            return holder;
        }

        // Hidden-word target: a single horizontal row of slots — "_ _ _ _". '_' is a black rock (still
        // hidden); any other char is a revealed letter (magenta tile, letter shown). No count badge — the
        // row itself IS the progress. The row overflows the small icon holder into the wider Target panel.
        // 2026-06-17 Spencer.
        private static GameObject BuildHiddenRow(Transform parent, float size, string word)
        {
            if (string.IsNullOrEmpty(word)) word = "____";
            word = word.ToUpperInvariant();
            int n = word.Length;

            var holder = new GameObject("HiddenIcon", typeof(RectTransform));
            holder.transform.SetParent(parent, false);
            var hrt = holder.GetComponent<RectTransform>();
            hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
            hrt.sizeDelta = new Vector2(size, size);

            float mini   = size * 0.82f;
            float pitch  = mini * 1.2f;               // ~20% gap between rocks
            float startX = -(n - 1) * pitch * 0.5f;   // center the row on the holder

            for (int i = 0; i < n; i++)
            {
                char ch = word[i];
                bool isBlank = ch == '_';
                float x = startX + i * pitch;

                // Persistent magenta GLOW halo behind a revealed letter — overlay UI can't bloom (it's drawn
                // after post-process), so the glow must be an actual sprite to match the flying tile's glow.
                if (!isBlank)
                {
                    var g = new GameObject($"G{i}", typeof(RectTransform), typeof(Image));
                    g.transform.SetParent(holder.transform, false);
                    var grt = g.GetComponent<RectTransform>();
                    grt.anchorMin = grt.anchorMax = new Vector2(0.5f, 0.5f);
                    grt.pivot = new Vector2(0.5f, 0.5f);
                    grt.sizeDelta = new Vector2(mini * 2.0f, mini * 2.0f);
                    grt.anchoredPosition = new Vector2(x, 0f);
                    var gimg = g.GetComponent<Image>();
                    gimg.sprite   = GlowSprite();
                    gimg.material = AdditiveMat();                  // ADD light → glow, not a smudge
                    gimg.color    = new Color(0.95f, 0.35f, 0.85f, 0.7f); // magenta
                    gimg.raycastTarget = false;
                    g.transform.SetAsFirstSibling(); // render behind the slot tiles
                }

                var t = new GameObject($"R{i}", typeof(RectTransform), typeof(Image));
                t.transform.SetParent(holder.transform, false);
                var trt = t.GetComponent<RectTransform>();
                trt.anchorMin = trt.anchorMax = new Vector2(0.5f, 0.5f);
                trt.sizeDelta = new Vector2(mini, mini);
                trt.anchoredPosition = new Vector2(x, 0f);
                var img = t.GetComponent<Image>();
                img.sprite = TileRenderer.CreateSolidRoundedRect(60, 60, 12, Color.white);
                img.color  = isBlank ? ROCK : MAGENTA;
                if (!isBlank) AddLetter(t.transform, ch.ToString(), mini);
            }
            return holder;
        }

        private static void AddLetter(Transform parent, string letter, float size)
        {
            var go = new GameObject("L", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            // 2026-06-15 Spencer: WORD-icon letters use the SAME font as the in-game letter tiles
            // (GameFont.GetTMP = AvenirNext), not the Cartoon UI font — so the icon reads like the board.
            var txt = go.AddComponent<TextMeshProUGUI>();
            var tileFont = GameFont.GetTMP();
            if (tileFont != null) txt.font = tileFont;
            txt.text = letter;
            txt.color = LETTER;
            txt.alignment = TextAlignmentOptions.Center;
            txt.enableWordWrapping = false;
            txt.enableAutoSizing = true;
            txt.fontSizeMin = 6f;
            txt.fontSizeMax = size * 0.7f;
        }

        // Bare outlined number at the icon's bottom-right corner — matches the HUD Target badge
        // (no circle). White, dark-navy outline. 2026-06-17 Spencer.
        private static void AddBadge(Transform parent, int count, float size)
        {
            float b = size * 0.5f;
            var go = new GameObject("Badge", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f); // bottom-right of the icon box
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(b, b);
            rt.anchoredPosition = Vector2.zero;

            var txt = go.AddComponent<Text>();
            txt.font = MenuUI.GetFont();
            txt.text = count.ToString();
            txt.color = Color.white;
            txt.fontStyle = FontStyle.Bold;
            txt.alignment = TextAnchor.MiddleCenter;
            txt.resizeTextForBestFit = true;
            txt.resizeTextMinSize = 6;
            txt.resizeTextMaxSize = Mathf.RoundToInt(b);

            var ol = go.AddComponent<Outline>();
            ol.effectColor = new Color32(20, 28, 55, 255); // deep navy, same as the HUD badge outline
            ol.effectDistance = new Vector2(1.8f, 1.8f);
        }
    }
}
