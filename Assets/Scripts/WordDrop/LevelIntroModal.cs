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

        public bool IsShowing => _canvas != null && _canvas.gameObject.activeSelf;

        private Canvas      _canvas;
        private GameObject  _panel;
        private Image       _overlay;
        private Text        _titleText;
        private Text        _goalText;
        private Text        _descText;
        private GameObject  _iconHolder;   // cleared + rebuilt per Show()
        private CanvasGroup _iconGroup;
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

        public void Show(Objective obj, int levelNum)
        {
            if (_canvas == null || obj == null) return;
            if (_isPresenting) return;
            _isPresenting = true;
            _isDismissing = false;

            if (_titleText != null) _titleText.text = $"LEVEL {LevelMapPanel.DisplayNum(levelNum)}"; // run level (tutorial 1..10)
            if (_descText  != null) _descText.text  = obj.IntroDescription; // verbose, instructive (not the terse HUD Title)

            // Layout per objective type: HiddenWord centers the row of blanks across the top of the card
            // with the description CENTERED underneath; every other mode keeps icon-left / text-right.
            bool hidden = obj.Icon == Objective.HudIcon.HiddenWord;
            ApplyContentLayout(hidden);

            // Rebuild the objective icon fresh each time.
            if (_iconHolder != null)
            {
                for (int i = _iconHolder.transform.childCount - 1; i >= 0; i--)
                    Destroy(_iconHolder.transform.GetChild(i).gameObject);
                float iconSize = hidden ? 84f : 78f;
                ObjectiveIconBuilder.Build(obj.Icon, _iconHolder.transform, iconSize, obj.RemainingCount, obj.IconWord);
            }

            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            if (_closeBtn != null) _closeBtn.SetActive(LevelMapPanel.MapFlowEnabled); // X only where there's a map to cancel back to
            GameAudio.Instance?.PlayWhooshFast(); // 2026-06-15 Spencer: level-intro entry uses whoosh_fast

            KillTweens();
            ResetEntranceState();
            _canvas.gameObject.SetActive(true);
            AnimateEntrance();
        }

        /// <summary>EXACT mirror of StageClearModal.AnimateEntrance: backdrop fade → panel
        /// drop-in-with-bounce → staggered title toss + child fades → button pulse.</summary>
        private void AnimateEntrance()
        {
            _entranceSeq?.Kill();
            Sequence seq = DOTween.Sequence();

            // Phase 1: backdrop fades to dim alpha over the (still-visible) board.
            if (_overlay != null)
                seq.Append(_overlay.DOFade(OVERLAY_BG.a, 0.12f).SetEase(Ease.OutQuad));
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
        private void ApplyContentLayout(bool hidden)
        {
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
                else
                {
                    dRT.anchorMin = new Vector2(0.46f, 0.32f); dRT.anchorMax = new Vector2(0.95f, 0.665f);
                    _descText.alignment = TextAnchor.MiddleLeft;
                }
                dRT.offsetMin = Vector2.zero; dRT.offsetMax = Vector2.zero;
            }
            // GOAL subtitle sits just above the description in each layout.
            if (_goalText != null)
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

        private void StartPlayPulse()
        {
            if (_btnPlay == null) return;
            var t = _btnPlay.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            t.localRotation = Quaternion.identity;   // clear any tilt left by an interrupted pop
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

        private void Hide()
        {
            bool wasPresenting = _isPresenting;
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
            // Map-flow (Phase 1): the map was playing Skybound and StageClearModal.FinalizeDismiss no longer starts
            // the gameplay track (it hands off to the map), so START the level's music HERE on PLAY. Each level gets
            // a fresh survival track — same as the non-map flow did on stage-clear. 2026-07-13 Spencer.
            if (LevelMapPanel.MapFlowEnabled && wasPresenting)
                GameAudio.Instance?.PlaySurvivalMusic();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
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
            int before = _panel.transform.childCount;
            MenuUI.CreateButton(_panel.transform, "BtnPlay",
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
