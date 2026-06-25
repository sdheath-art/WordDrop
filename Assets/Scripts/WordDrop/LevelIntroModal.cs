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

        private Sequence _entranceSeq;
        private float    _titleZRot;       // shared state for the title toss rotation tween
        private bool     _isPresenting;
        private bool     _isDismissing;

        private static readonly Color OVERLAY_BG = new Color(0.05f, 0.04f, 0.12f, 0.80f); // dim — board shows through
        private static readonly Color CARD_BG    = new Color(0.99f, 0.95f, 0.86f, 1f);    // warm cream (CC card)
        private static readonly Color HEADER_BG  = new Color(0.93f, 0.45f, 0.62f, 1f);    // candy pink header
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
        public void Show(Objective obj, int levelNum)
        {
            if (_canvas == null || obj == null) return;
            if (_isPresenting) return;
            _isPresenting = true;
            _isDismissing = false;

            if (_titleText != null) _titleText.text = $"LEVEL {levelNum}";
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
                    if (rt != null) UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED);
                });
                seq.AppendInterval(UIAnimations.DROP_TOTAL_DUR / DROP_SPEED + 0.25f);
            }

            // Phase 3: children fade/toss in with a Playrix-style stagger.
            seq.AppendCallback(TossInTitle);
            seq.AppendInterval(0.08f);
            seq.AppendCallback(() => { if (_iconGroup != null) _iconGroup.DOFade(1f, 0.18f).SetEase(Ease.OutQuad); });
            seq.AppendInterval(0.06f);
            seq.AppendCallback(() => { FadeInText(_goalText, 0.18f); FadeInText(_descText, 0.18f); });
            seq.AppendInterval(0.10f);
            seq.AppendCallback(() => { if (_btnGroup != null) _btnGroup.DOFade(1f, 0.18f); });
            seq.AppendInterval(0.22f);
            seq.AppendCallback(StartPlayPulse);

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

            t.localScale    = Vector3.one * 0.1f;
            t.localRotation = Quaternion.Euler(0f, 0f, 8f);
            SetTextAlpha(_titleText, 0f);

            const float TOSS_DURATION = 0.28f;
            const float OVERSHOOT     = 3.0f;

            t.DOScale(1.0f, TOSS_DURATION).SetEase(Ease.OutBack, OVERSHOOT);

            DG.Tweening.Core.DOGetter<float> getZ = () => _titleZRot;
            DG.Tweening.Core.DOSetter<float> setZ = (float z) =>
            {
                _titleZRot = z;
                t.localRotation = Quaternion.Euler(0f, 0f, z);
            };
            _titleZRot = 8f;
            DOTween.To(getZ, setZ, 0f, TOSS_DURATION).SetEase(Ease.OutBack, OVERSHOOT);

            Color c = _titleText.color;
            _titleText.DOColor(new Color(c.r, c.g, c.b, 1f), 0.12f).SetEase(Ease.OutQuad);
        }

        private void StartPlayPulse()
        {
            if (_btnPlay == null) return;
            var t = _btnPlay.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            t.DOScale(1.07f, 0.7f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
        }

        private void OnPlay()
        {
            if (_isDismissing) return;
            _isDismissing = true;

            KillTweens();
            // (press/release SFX are wired on the button's PointerDown + onClick — no extra SFX here)

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

        /// <summary>Fires when the goal modal is dismissed → gameplay begins. The tutorial gating layer
        /// uses this to start its coaching at the moment the board becomes interactive. 2026-06-25.</summary>
        public static event System.Action OnPlayStarted;

        private void Hide()
        {
            bool wasPresenting = _isPresenting;
            if (SurvivalManager.Instance != null) SurvivalManager.Instance.SetOverlayPaused(false);
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
                if (_panel.transform is RectTransform rt) rt.anchoredPosition = Vector2.zero;
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
            _canvas.sortingOrder = 158; // above HUD (50), below StageClearModal (160)

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
            => BuildSpriteIcon(parent, size, "Tiles/Icon_ImageIcon_Coin", GOLD, 0);

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
