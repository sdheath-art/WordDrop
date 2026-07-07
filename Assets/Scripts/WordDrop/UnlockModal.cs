using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Royal-Match-style "Unlocked!" reward modal — announces a newly-unlocked tool (e.g. Swap) after a
    /// tutorial level clears. Styled to MATCH StageClearModal (cream rounded card, pink title, pop-in). Shown
    /// AFTER the level-cleared modal for the unlock stage (StageClearModal.FinalizeDismiss hands off to it);
    /// "Claim" resumes gameplay → advances to the next level. Purely presentational — the tool actually
    /// unlocks via the level gate (TutorialLocks). Placeholder icon for now. 2026-07-06 Spencer.
    /// </summary>
    public class UnlockModal : MonoBehaviour
    {
        public static UnlockModal Instance { get; private set; }

        private Canvas _canvas;
        private GameObject _panel;
        private Text  _subtitleText;
        private Text  _descText;
        private Image _iconImage;
        private bool  _isShowing;

        // Match StageClearModal's palette.
        private static readonly Color PANEL_BG    = new Color(0.05f, 0.04f, 0.12f, 0.80f); // dim backdrop
        private static readonly Color CARD_BG     = new Color(0.99f, 0.95f, 0.86f, 0.98f); // cream card
        private static readonly Color TITLE       = new Color(0.82f, 0.28f, 0.46f, 1f);    // candy pink
        private static readonly Color SUBTITLE    = new Color(0.32f, 0.24f, 0.30f, 1f);    // muted body
        private static readonly Color CLAIM_GREEN = new Color(0.42f, 0.72f, 0.30f, 1f);    // Royal-Match green CTA

        public bool IsShowing => _isShowing;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("UnlockModal");
                go.AddComponent<UnlockModal>();
                DontDestroyOnLoad(go);
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        /// <summary>Present the unlock reward. subtitle/description/icon are passed so this can announce any tool.</summary>
        public void Show(string subtitle, string description, Sprite icon)
        {
            if (_isShowing || _canvas == null) return;
            _isShowing = true;

            if (_subtitleText != null) _subtitleText.text = subtitle;
            if (_descText != null) _descText.text = description;
            if (_iconImage != null && icon != null) _iconImage.sprite = icon;

            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(true);
            _canvas.gameObject.SetActive(true);

            // Park the panel small, then kick the scale-in ONE FRAME LATER (RevealPanelNextFrame). The
            // freshly-activated canvas builds its geometry + the CanvasScaler computes its scale factor on the
            // FIRST frame; starting the pop that same frame makes its opening step advance through that hitch
            // and "teleport" to a bigger size. Waiting one frame lets it settle so the pop is smooth. It's a
            // scale-in (distinct from the cleared modal's drop-in) on scaled time. 2026-07-06 Spencer.
            if (_panel != null)
            {
                _panel.transform.DOKill();
                _panel.transform.localScale = Vector3.zero; // hidden until the pop fires after the beat
            }
            StartCoroutine(RevealPanelAfterBeat());

            GameAudio.Instance?.PlayStageClearMusic();
        }

        private System.Collections.IEnumerator RevealPanelAfterBeat()
        {
            // Hold the panel hidden for a short beat so any show-time hitch (audio track load, StageClearModal
            // teardown, first canvas build) passes BEFORE the pop — otherwise the tween skips through it and
            // looks like it jumps to a bigger size. Then use the TESTED PopIn helper (proven smooth across the
            // game) with a gentler overshoot than the old OutBack 2.2, whose big overshoot peak itself read as
            // a "jump to bigger then settle." Same backdrop-first staging as StageClearModal. 2026-07-06 Spencer.
            yield return new WaitForSecondsRealtime(0.12f);
            if (_panel != null && _isShowing)
            {
                UIAnimations.PopIn(_panel.transform, UIAnimations.Overshoot.Heavy);
                GameAudio.Instance?.PlaySparkleWhoosh();
            }
        }

        private void OnClaim()
        {
            if (!_isShowing) return;
            _isShowing = false;
            GameAudio.Instance?.PlayMultiPopRelease();
            StopAllCoroutines();

            // Pop OUT (scale-down, mirror of the scale-in) so the modal LEAVES with a beat instead of vanishing.
            // The handoff (hide + advance) waits for the pop-out to finish. 2026-07-06 Spencer.
            if (_panel != null)
            {
                _panel.transform.DOKill();
                UIAnimations.PopOut(_panel.transform, FinalizeClaim);
            }
            else FinalizeClaim();
        }

        private void FinalizeClaim()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            StageClearModal.UnlockRewardPending = false;

            // Hand off to the DEFERRED next-level objective intro (it keeps the overlay paused; its own PLAY
            // resumes gameplay). Order becomes cleared → unlocked → objective. If there's no deferred intro
            // (edge case / direct jump), resume gameplay directly. 2026-07-06 Spencer.
            if (LevelIntroModal.Instance != null && LevelIntroModal.Instance.HasDeferred)
            {
                LevelIntroModal.Instance.ShowDeferred();
            }
            else
            {
                if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
                GameAudio.Instance?.PlaySurvivalMusic();
            }
        }

        // ── Dev/test entry (FX test menu) — show/hide in isolation, no overlay pause or level advance ──
        public void ShowForDebug()
        {
            if (_canvas == null) return;
            _isShowing = true;
            if (_subtitleText != null) _subtitleText.text = "Swap";
            if (_descText != null) _descText.text = "Swap any tile on the board for a new one!";
            if (_iconImage != null) _iconImage.sprite = Resources.Load<Sprite>("Tiles/swap_tile");
            _canvas.gameObject.SetActive(true);
            if (_panel != null)
            {
                _panel.transform.DOKill();
                _panel.transform.localScale = Vector3.zero;
            }
            StopAllCoroutines();
            StartCoroutine(RevealPanelAfterBeat());
            GameAudio.Instance?.PlayStageClearMusic();
        }

        public void HideForDebug()
        {
            _isShowing = false;
            StopAllCoroutines();
            if (_panel != null)
            {
                _panel.transform.DOKill();
                UIAnimations.PopOut(_panel.transform, () => { if (_canvas != null) _canvas.gameObject.SetActive(false); });
            }
            else if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        // ── UI construction (mirrors StageClearModal.BuildUI) ──────────────────────
        private void BuildUI()
        {
            var canvasGO = new GameObject("UnlockCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 165; // above StageClearModal (160) so it sits on top

            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen dim overlay (blocks taps to the board).
            var overlay = new GameObject("Overlay");
            overlay.transform.SetParent(canvasGO.transform, false);
            var oRT = overlay.AddComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero; oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero; oRT.offsetMax = Vector2.zero;
            var oImg = overlay.AddComponent<Image>();
            oImg.color = PANEL_BG; oImg.raycastTarget = true;

            // Centered cream card, rounded corners.
            _panel = new GameObject("Card");
            _panel.transform.SetParent(canvasGO.transform, false);
            var pRT = _panel.AddComponent<RectTransform>();
            pRT.anchorMin = new Vector2(0.08f, 0.24f);
            pRT.anchorMax = new Vector2(0.92f, 0.76f);
            pRT.offsetMin = Vector2.zero; pRT.offsetMax = Vector2.zero;
            var pImg = _panel.AddComponent<Image>();
            pImg.color = CARD_BG;
            pImg.sprite = MenuUI.GetRoundedRectSprite(44);
            pImg.type = Image.Type.Sliced;

            // Title — "Unlocked!"
            var title = CreateLabel(_panel.transform, "Title",
                new Vector2(0.04f, 0.80f), new Vector2(0.96f, 0.95f), "Unlocked!", 44, TITLE);
            title.fontStyle = FontStyle.Bold;
            title.horizontalOverflow = HorizontalWrapMode.Overflow;

            // Subtitle — the tool name (set per-show).
            _subtitleText = CreateLabel(_panel.transform, "Subtitle",
                new Vector2(0.06f, 0.66f), new Vector2(0.94f, 0.78f), "Swap", 32, TITLE);
            _subtitleText.fontStyle = FontStyle.Bold;

            // Icon — placeholder swap sprite, centered.
            var iconGO = new GameObject("Icon");
            iconGO.transform.SetParent(_panel.transform, false);
            var iRT = iconGO.AddComponent<RectTransform>();
            iRT.anchorMin = new Vector2(0.5f, 0.52f);
            iRT.anchorMax = new Vector2(0.5f, 0.52f);
            iRT.pivot = new Vector2(0.5f, 0.5f);
            iRT.sizeDelta = new Vector2(130f, 130f);
            iRT.anchoredPosition = Vector2.zero;
            _iconImage = iconGO.AddComponent<Image>();
            _iconImage.sprite = Resources.Load<Sprite>("Tiles/swap_tile"); // placeholder — swap for real art later
            _iconImage.preserveAspect = true;
            _iconImage.raycastTarget = false;

            // "x3" reward-count badge — bottom-right of the icon, Royal-Match style. Overhangs slightly so it
            // reads as a count chip on the corner. Placeholder count for now. 2026-07-06 Spencer.
            var countBadge = CreateLabel(iconGO.transform, "CountBadge",
                new Vector2(0.55f, -0.08f), new Vector2(1.28f, 0.42f), "x3", 40, TITLE);
            countBadge.fontStyle = FontStyle.Bold;
            countBadge.alignment = TextAnchor.LowerRight;
            countBadge.horizontalOverflow = HorizontalWrapMode.Overflow;
            countBadge.verticalOverflow = VerticalWrapMode.Overflow;
            countBadge.raycastTarget = false;

            // Description — brief "what it does".
            _descText = CreateLabel(_panel.transform, "Desc",
                new Vector2(0.08f, 0.23f), new Vector2(0.92f, 0.35f),
                "Swap any tile on the board for a new one!", 22, SUBTITLE);
            _descText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _descText.verticalOverflow = VerticalWrapMode.Overflow;

            // Claim button — green CTA, bottom of the card.
            MenuUI.CreateButton(_panel.transform, "BtnClaim",
                new Vector2(0.16f, 0.06f), new Vector2(0.84f, 0.20f),
                "Claim", CLAIM_GREEN, Color.white, 30, OnClaim);
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
            t.text = text; t.fontSize = fontSize; t.color = color;
            t.alignment = TextAnchor.MiddleCenter;
            return t;
        }
    }
}
