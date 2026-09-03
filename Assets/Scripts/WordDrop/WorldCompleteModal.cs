using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// "AREA COMPLETED" celebration modal — shown after the trophy drops on a boss node (world-ending level).
    /// Drops in the SAME way as the level-clear modal (dim → panel drop-in with bounce), with a big TROPHY hero
    /// graphic that scales/rotates in like the hero star, and an "ADVANCE" button that carries the player into the
    /// next world. Self-contained: builds its own overlay canvas above the level map + play modal. 2026-07-13 Spencer.
    /// </summary>
    public class WorldCompleteModal : MonoBehaviour
    {
        public static WorldCompleteModal Instance { get; private set; }

        private static readonly Color OVERLAY_BG  = new Color(0.05f, 0.04f, 0.12f, 0.80f); // dim
        private static readonly Color PANEL_CREAM  = new Color(0.98f, 0.94f, 0.80f, 1f);
        private static readonly Color TITLE_PURPLE = new Color(0.56f, 0.31f, 0.78f, 1f);   // matches boss node / modal header
        private static readonly Color BTN_ORANGE   = new Color(0.87f, 0.45f, 0.13f, 1f);   // same as PLAY
        private static readonly Color TROPHY_GOLD  = Color.white;                          // trophy shows its own colour
        private static readonly Color GLOW_GOLD    = new Color(1.00f, 0.82f, 0.22f, 0.80f); // additive gold glow (same as StageClear)

        private Canvas _canvas;
        private Image _overlay;
        private RectTransform _panel;
        private RectTransform _trophyRT;
        private Image _trophyImg;
        private RectTransform _glowRT;
        private Image _glowImg;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _subText;
        private Button _advanceBtn;
        private Action _onAdvance;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
        }

        /// <summary>Show the modal for a completed world. <paramref name="worldNum"/> is the world just finished
        /// (1 after level 10, 2 after level 20 …). <paramref name="onAdvance"/> fires when ADVANCE is tapped.</summary>
        public void Show(int worldNum, Action onAdvance)
        {
            if (_canvas == null) return;
            _onAdvance = onAdvance;

            _canvas.gameObject.SetActive(true);
            if (_titleText != null) _titleText.text = "AREA COMPLETED";
            if (_subText   != null) _subText.text   = worldNum > 0 ? $"Area {worldNum} cleared!" : "";

            ResetEntranceState();
            AnimateEntrance();
        }

        private void ResetEntranceState()
        {
            if (_overlay != null)
            {
                var c = _overlay.color;
                _overlay.color = new Color(c.r, c.g, c.b, 0f);
            }
            if (_panel != null)
            {
                _panel.DOKill();
                _panel.anchoredPosition = new Vector2(0f, UIAnimations.DROP_OFFSCREEN_OFFSET); // parked above
                _panel.localScale = Vector3.one;
            }
            if (_trophyRT != null)
            {
                _trophyRT.DOKill();
                _trophyRT.localScale = Vector3.zero;
            }
            if (_trophyImg != null)
            {
                var tc = _trophyImg.color; _trophyImg.color = new Color(tc.r, tc.g, tc.b, 0f);
            }
            if (_glowRT != null) { _glowRT.DOKill(); _glowRT.localScale = Vector3.zero; }
            if (_glowImg != null) { _glowImg.DOKill(); _glowImg.enabled = false; _glowImg.color = GLOW_GOLD; }
        }

        private void AnimateEntrance()
        {
            // Backdrop fade → panel DROPS in with a bounce (mirrors StageClearModal / LevelIntroModal), then the
            // trophy hero drops in on the sequence's completion.
            var seq = DOTween.Sequence().SetUpdate(true);
            if (_overlay != null)
                seq.Append(_overlay.DOFade(OVERLAY_BG.a, 0.12f).SetEase(Ease.OutQuad));

            const float DROP_SPEED = 1.5f;
            if (_panel != null)
            {
                if (UIAnimations.ReducedMotion)
                    seq.AppendCallback(() => { _panel.anchoredPosition = Vector2.zero; });
                else
                    seq.AppendCallback(() => { _panel.anchoredPosition = Vector2.zero;
                                               UIAnimations.DropInWithBounce(_panel, speedMult: DROP_SPEED); });
                seq.AppendInterval(UIAnimations.DROP_PHASE_DROP_DUR / DROP_SPEED);
            }
            seq.OnComplete(AnimateTrophyDrop);
        }

        // Trophy hero drops in (same feel as the hero star: big + tilted + transparent → OutBounce settle).
        private void AnimateTrophyDrop()
        {
            if (_trophyRT == null) return;
            _trophyRT.DOKill();

            if (UIAnimations.ReducedMotion)
            {
                _trophyRT.localScale = Vector3.one;
                _trophyRT.localRotation = Quaternion.identity;
                if (_trophyImg != null) _trophyImg.color = TROPHY_GOLD;
                GameAudio.Instance?.PlayPersonalBest();
                return;
            }

            _trophyRT.localScale = Vector3.one * 2.4f;
            _trophyRT.localRotation = Quaternion.Euler(0f, 0f, -70f);
            if (_trophyImg != null) _trophyImg.color = new Color(TROPHY_GOLD.r, TROPHY_GOLD.g, TROPHY_GOLD.b, 0f);

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(_trophyRT.DOScale(1f, 0.60f).SetEase(Ease.OutBounce));
            seq.Join(_trophyRT.DORotate(Vector3.zero, 0.50f, RotateMode.Fast).SetEase(Ease.OutCubic));
            if (_trophyImg != null) seq.Join(_trophyImg.DOFade(1f, 0.18f).SetEase(Ease.OutCubic));
            seq.InsertCallback(0.10f, () => GameAudio.Instance?.PlayPersonalBest());
            seq.InsertCallback(0.30f, SpawnSparkles); // sparkles pop as it lands
            // Gold glow grows in behind the trophy once it's settled.
            if (_glowRT != null && _glowImg != null)
            {
                _glowRT.localScale = Vector3.zero;
                seq.AppendCallback(() => { if (_glowImg != null) _glowImg.enabled = true; });
                seq.Append(_glowRT.DOScale(1f, 0.55f).SetEase(Ease.OutCubic).SetUpdate(true));
            }
        }

        // Compact version of StageClearModal's SpawnStarSparkles — gold flares/twinkles fly out from the trophy.
        private void SpawnSparkles()
        {
            if (_panel == null || _trophyRT == null) return;
            Sprite flare = LoadFlareSprite();
            Sprite twinkle = LoadTwinkleSprite();
            if (flare == null && twinkle == null) return;
            const int COUNT = 14;
            int behind = _trophyRT.GetSiblingIndex(); // insert BEHIND the trophy
            Vector2 center = _trophyRT.anchoredPosition;
            var addMat = LoadAdditiveGlowMaterial();
            for (int i = 0; i < COUNT; i++)
            {
                bool isFlare = (i % 2 == 0);
                var sGO = new GameObject("Sparkle", typeof(RectTransform), typeof(Image));
                sGO.transform.SetParent(_panel.transform, false);
                var rt = (RectTransform)sGO.transform;
                rt.SetSiblingIndex(behind);
                rt.anchorMin = _trophyRT.anchorMin; rt.anchorMax = _trophyRT.anchorMax;
                rt.pivot = new Vector2(0.5f, 0.5f);
                float baseSize = isFlare ? (52f + (i % 3) * 14f) : (22f + (i % 3) * 8f);
                rt.sizeDelta = new Vector2(baseSize, baseSize);
                rt.anchoredPosition = center;
                var img = sGO.GetComponent<Image>();
                img.sprite = isFlare ? flare : twinkle;
                img.color = isFlare ? new Color(1f, 0.90f, 0.45f, 1f) : new Color(1f, 0.97f, 0.80f, 1f);
                img.raycastTarget = false; img.preserveAspect = true;
                if (addMat != null) img.material = addMat;
                float ang = (i / (float)COUNT) * Mathf.PI * 2f + (i % 2 == 0 ? 0.35f : -0.42f);
                float dist = 110f + (i % 4) * 34f;
                Vector2 target = center + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * dist;
                float spin = (i % 2 == 0 ? 1f : -1f) * (90f + (i % 3) * 60f);
                rt.localScale = Vector3.one * 0.15f;
                var sq = DOTween.Sequence().SetUpdate(true);
                sq.Append(rt.DOScale(1f, 0.16f).SetEase(Ease.OutBack, 2.2f));
                sq.Join(rt.DOAnchorPos(target, 0.55f).SetEase(Ease.OutCubic));
                sq.Join(rt.DOLocalRotate(new Vector3(0f, 0f, spin), 0.55f, RotateMode.LocalAxisAdd).SetEase(Ease.OutCubic));
                sq.Insert(0.20f, img.DOFade(0f, 0.40f).SetEase(Ease.InQuad));
                sq.OnComplete(() => { if (sGO != null) Destroy(sGO); });
            }
        }

        private void OnAdvance()
        {
            GameAudio.Instance?.PlayMultiPopRelease();
            Hide();
            var cb = _onAdvance; _onAdvance = null;
            cb?.Invoke();
        }

        /// <summary>Force the modal closed (debug jumps clean-slate everything).</summary>
        public void ForceHide() { _onAdvance = null; Hide(); }

        private void Hide()
        {
            if (_panel != null) _panel.DOKill();
            if (_trophyRT != null) _trophyRT.DOKill();
            if (_overlay != null) _overlay.DOKill();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        // ── UI construction ─────────────────────────────────────────────────────────
        private void BuildUI()
        {
            var canvasGO = new GameObject("WorldCompleteCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 176; // above the level MAP (170) and play modal (172)
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Dim backdrop (also eats taps so the map behind can't be touched).
            var overlayGO = new GameObject("Overlay", typeof(RectTransform), typeof(Image));
            overlayGO.transform.SetParent(canvasGO.transform, false);
            var oRT = overlayGO.GetComponent<RectTransform>();
            oRT.anchorMin = Vector2.zero; oRT.anchorMax = Vector2.one;
            oRT.offsetMin = Vector2.zero; oRT.offsetMax = Vector2.zero;
            _overlay = overlayGO.GetComponent<Image>();
            _overlay.color = OVERLAY_BG; _overlay.raycastTarget = true;

            // Panel (cream rounded card).
            var panelGO = new GameObject("Card", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO.GetComponent<RectTransform>();
            _panel.anchorMin = _panel.anchorMax = new Vector2(0.5f, 0.5f);
            _panel.pivot = new Vector2(0.5f, 0.5f);
            _panel.sizeDelta = new Vector2(475f, 560f); // ~matches the level-clear card (0.06–0.94 × 0.21–0.79 of 540×960)
            _panel.anchoredPosition = Vector2.zero;
            var pImg = panelGO.GetComponent<Image>();
            pImg.sprite = MenuUI.GetRoundedRectSprite(44); pImg.type = Image.Type.Sliced;
            pImg.color = PANEL_CREAM; pImg.raycastTarget = true;

            // Additive gold GLOW behind the trophy (fake bloom — grows in after the trophy lands). Created BEFORE the
            // trophy so it renders behind it.
            var glowGO = new GameObject("Glow", typeof(RectTransform), typeof(Image));
            glowGO.transform.SetParent(_panel.transform, false);
            _glowRT = glowGO.GetComponent<RectTransform>();
            _glowRT.anchorMin = _glowRT.anchorMax = new Vector2(0.5f, 0.5f);
            _glowRT.pivot = new Vector2(0.5f, 0.5f);
            _glowRT.sizeDelta = new Vector2(500f, 500f); // fits within the panel when centred → no spill off the top
            _glowRT.anchoredPosition = new Vector2(0f, 20f); // CENTRED (like the level-clear hero), same as the trophy
            _glowImg = glowGO.GetComponent<Image>();
            _glowImg.sprite = LoadGlowStarSprite();
            var glowMat = LoadAdditiveGlowMaterial();
            if (glowMat != null) _glowImg.material = glowMat;
            _glowImg.color = GLOW_GOLD;
            _glowImg.preserveAspect = true; _glowImg.raycastTarget = false;
            _glowImg.enabled = false;

            // Trophy hero — big, drops in.
            var trophyGO = new GameObject("Trophy", typeof(RectTransform), typeof(Image));
            trophyGO.transform.SetParent(_panel.transform, false);
            _trophyRT = trophyGO.GetComponent<RectTransform>();
            _trophyRT.anchorMin = _trophyRT.anchorMax = new Vector2(0.5f, 0.5f);
            _trophyRT.pivot = new Vector2(0.5f, 0.5f);
            _trophyRT.sizeDelta = new Vector2(190f, 190f); // same as the level-clear hero star
            _trophyRT.anchoredPosition = new Vector2(0f, 20f); // CENTRED in the card, like the hero star
            _trophyImg = trophyGO.GetComponent<Image>();
            _trophyImg.sprite = LoadTrophySprite();
            _trophyImg.preserveAspect = true; _trophyImg.raycastTarget = false;
            _trophyImg.color = TROPHY_GOLD;

            // Title — near the TOP of the card (like "WELL DONE!"), so the hero trophy sits centred below it.
            _titleText = MakeLabel(_panel, "Title", new Vector2(0f, 215f), new Vector2(440f, 70f), 44f, TITLE_PURPLE);
            _titleText.text = "AREA COMPLETED";
            _titleText.fontStyle = FontStyles.Bold;

            // Subtitle — just under the title.
            _subText = MakeLabel(_panel, "Sub", new Vector2(0f, 160f), new Vector2(420f, 44f), 26f,
                                 new Color(0.35f, 0.30f, 0.28f, 1f));

            // Advance button.
            var btnGO = new GameObject("AdvanceBtn", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(_panel.transform, false);
            var bRT = btnGO.GetComponent<RectTransform>();
            bRT.anchorMin = bRT.anchorMax = new Vector2(0.5f, 0.5f);
            bRT.pivot = new Vector2(0.5f, 0.5f);
            bRT.sizeDelta = new Vector2(320f, 104f);
            bRT.anchoredPosition = new Vector2(0f, -205f); // bottom of the card, like CONTINUE
            var bImg = btnGO.GetComponent<Image>();
            bImg.sprite = MenuUI.GetRoundedRectSprite(34); bImg.type = Image.Type.Sliced;
            bImg.color = BTN_ORANGE;
            _advanceBtn = btnGO.GetComponent<Button>();
            _advanceBtn.transition = Selectable.Transition.None;
            _advanceBtn.onClick.AddListener(OnAdvance);
            // Press/release SFX (same as the PLAY button).
            var trigger = btnGO.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var pd = new UnityEngine.EventSystems.EventTrigger.Entry { eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown };
            pd.callback.AddListener((_) => { GameAudio.Instance?.PlayMultiPopPress(); UIAnimations.ButtonPress(bRT); });
            trigger.triggers.Add(pd);

            var btnLbl = MakeLabel(bRT, "Label", Vector2.zero, new Vector2(320f, 104f), 40f, Color.white);
            btnLbl.text = "ADVANCE"; btnLbl.fontStyle = FontStyles.Bold; btnLbl.raycastTarget = false;

            _canvas.gameObject.SetActive(false);
        }

        private static TextMeshProUGUI MakeLabel(RectTransform parent, string name, Vector2 pos, Vector2 size,
                                                 float fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = size; rt.anchoredPosition = pos;
            var t = go.GetComponent<TextMeshProUGUI>();
            var f = GameFont.GetDisplayTMP(); if (f != null) t.font = f;
            t.fontSize = fontSize; t.alignment = TextAlignmentOptions.Center; t.color = color;
            t.enableWordWrapping = false; t.raycastTarget = false;
            return t;
        }

        private static Sprite _trophySprite; private static bool _trophyTried;
        private static Sprite LoadTrophySprite()
        {
            if (_trophyTried) return _trophySprite;
            _trophyTried = true;
            _trophySprite = Resources.Load<Sprite>("Tiles/Icon_ItemIcon_Trophy");
            if (_trophySprite == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/Icon_ItemIcon_Trophy");
                if (tex != null)
                    _trophySprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _trophySprite;
        }

        // Glow + sparkle assets — same sources StageClearModal uses (Particles/*), so both modals read identically.
        private static Sprite SpriteFrom(string path)
        {
            var s = Resources.Load<Sprite>(path);
            if (s != null) return s;
            var tex = Resources.Load<Texture2D>(path);
            return tex != null ? Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }

        private static Sprite _glowStar; private static bool _glowStarTried;
        private static Sprite LoadGlowStarSprite()
        {
            if (_glowStarTried) return _glowStar;
            _glowStarTried = true;
            _glowStar = SpriteFrom("Particles/Star02") ?? SpriteFrom("Tiles/Icon_ImageIcon_Star01_On");
            return _glowStar;
        }

        private static Sprite _flare; private static bool _flareTried;
        private static Sprite LoadFlareSprite()
        {
            if (_flareTried) return _flare;
            _flareTried = true;
            _flare = SpriteFrom("Particles/flare") ?? SpriteFrom("Particles/flare_star") ?? LoadGlowStarSprite();
            return _flare;
        }

        private static Sprite _twinkle; private static bool _twinkleTried;
        private static Sprite LoadTwinkleSprite()
        {
            if (_twinkleTried) return _twinkle;
            _twinkleTried = true;
            _twinkle = SpriteFrom("Particles/point1") ?? SpriteFrom("Particles/soft_circle") ?? LoadFlareSprite();
            return _twinkle;
        }

        private static Material _addMat; private static bool _addMatTried;
        private static Material LoadAdditiveGlowMaterial()
        {
            if (_addMatTried) return _addMat;
            _addMatTried = true;
            var s = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default");
            if (s != null) _addMat = new Material(s);
            return _addMat;
        }
    }
}
