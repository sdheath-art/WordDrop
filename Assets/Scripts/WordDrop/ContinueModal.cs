using System;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// MVP P3 continue offer. Shown after a Survival top-out (and after the
    /// TopOutPanel announcement plays). Two actions:
    ///   - CONTINUE: spend CONTINUE_COIN_COST coins → clear top 3 rows + refill
    ///     resources → resume play (no heart consumed)
    ///   - GIVE UP: heart consumed → finalize game over
    ///
    /// Canvas sortingOrder = 170 (above TopOutPanel @ 165, below GameOverUI @ 10000).
    /// </summary>
    public class ContinueModal : MonoBehaviour
    {
        public static ContinueModal Instance { get; private set; }

        /// <summary>True while the modal canvas is currently active on screen.
        /// HintManager + other ambient animation systems gate on this to stop
        /// background tile-hops/pulses while the modal is up — matches
        /// StageClearModal.IsShowing.</summary>
        public bool IsShowing => _canvas != null && _canvas.gameObject.activeSelf;

        private const int CANVAS_SORT_ORDER = 170;

        private Canvas _canvas;
        private GameObject _panel;
        private CanvasGroup _panelCanvasGroup; // alpha gate so the panel can be hidden across the SetActive→DropInWithBounce gap
        private Image _backdrop;
        private TextMeshProUGUI _titleText;
        private TextMeshProUGUI _bodyText;
        private GameObject  _objectiveIconHolder; // holds the remaining-objective ICON below the body text
        private CanvasGroup _objectiveIconCG;
        private GameObject  _objectiveIconGO;      // the built icon (rebuilt each show; destroyed first)
        private TextMeshProUGUI _continueLabel;
        // ── COIN IN THE LABEL (2026-10-22 Spencer: "add the coin icon where the bullet is on the play on button"). The label
        // leaves COIN_GAP where the ● was; a real coin Image (the HUD's coin3d_icon) is parked on the MIDDLE space of that gap,
        // measured from TMP's own layout, so it tracks the text whatever the cost or the auto-size. ──
        private const string COIN_GAP = "     ";
        private Image _coinInLabel; private string _coinPlacedFor; private float _coinPlacedSize = -1f;
        private static Sprite s_labelCoin; private static bool s_labelCoinTried;
        private static Sprite LabelCoinSprite()
        {
            if (s_labelCoinTried) return s_labelCoin;
            s_labelCoinTried = true;
            foreach (string path in new[] { "Tiles/coin3d_icon", "Tiles/Icon_ImageIcon_Coin" })
            {
                s_labelCoin = Resources.Load<Sprite>(path);
                if (s_labelCoin != null) break;
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null) { s_labelCoin = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f); break; }
            }
            return s_labelCoin;
        }
        private void LateUpdate()
        {
            if (CARD_V2 && _cardV2 != null && _canvas != null)
            {   // the card works in mock px (1179-wide screen), like the Level card
                float k = ((RectTransform)_canvas.transform).rect.width / 1179f; _cardV2.localScale = new Vector3(k, k, 1f);
            }
            if (_playArt != null && _adArt != null) EqualiseLabels();
            if (_failedState && _retryArt != null) PlaceIconInGap(TopLabel(_retryArt), ref _retryCoin, ref _retryCoinFor, ref _retryCoinSize, "Tiles/coin3d_icon", false);
            if (_adLabelTop != null) PlaceIconInGap(_adLabelTop, ref _adIcon, ref _adIconFor, ref _adIconSize, "UI/icon_clapper_label", false);   // 11-16: the coin's rim + soft shadow treatment
            if (CARD_V2 && _cardV2 != null) { SeatLabel(_playArt, null); SeatLabel(_adArt, _adIcon); if (_failedState) SeatLabel(_retryArt, null); }
            var l = _continueLabel;
            if (l == null || !l.gameObject.activeInHierarchy) return;
            if (_coinPlacedFor == l.text && Mathf.Approximately(_coinPlacedSize, l.fontSize)) return;
            int gap = l.text.IndexOf(COIN_GAP, System.StringComparison.Ordinal);
            if (_coinInLabel == null)
            {
                var sp = LabelCoinSprite(); if (sp == null) return;
                var go = new GameObject("CoinInLabel", typeof(RectTransform), typeof(Image));
                go.transform.SetParent(l.transform, false);
                _coinInLabel = go.GetComponent<Image>(); _coinInLabel.sprite = sp; _coinInLabel.preserveAspect = true; _coinInLabel.raycastTarget = false;
                var crt = (RectTransform)go.transform; crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            }
            l.ForceMeshUpdate();
            _coinPlacedFor = l.text; _coinPlacedSize = l.fontSize;
            int mid = gap + COIN_GAP.Length / 2;
            if (gap < 0 || mid >= l.textInfo.characterCount) { _coinInLabel.gameObject.SetActive(false); return; }
            var ci = l.textInfo.characterInfo[mid];
            var line = l.textInfo.lineInfo[ci.lineNumber];
            float x = (ci.origin + ci.xAdvance) * 0.5f;
            float y = (line.ascender + line.descender) * 0.5f;
            float h = (line.ascender - line.descender) * (CARD_V2 ? CV_ICON_K : 0.85f);   // 11-16 v2: 0.85 of the WordSlop line box overlapped "On"/"50"
            var rt = _coinInLabel.rectTransform;
            rt.localPosition = new Vector3(x, y, 0f);
            rt.sizeDelta = new Vector2(h, h);
            _coinInLabel.gameObject.SetActive(true);
        }
        private TextMeshProUGUI _adLabel;
        // ── CARD V2 (2026-11-16, Spencer: "yes to the continue screen" - ~/Desktop/continue_screen_mock3.png + watch_ad_hud_purple_mock.png).
        // The Level card's own sprites (UI/Panel panel / header / cream / close), header "Continue?", the goals still needed in the cream
        // box, "Get +5 moves to finish!", the pill_play Play On (coin in the label) and the same button with the HUD-ramp purple face
        // (pill_play_face_purple) for the ad, then "You will lose a life!". The old panel's logic is untouched; only its look is replaced.
        // false = the old cream panel.
        public static bool CARD_V2 = true;
        private RectTransform _cardV2; private ArtButton _playArt, _adArt; private TextMeshProUGUI _adLabelTop;
        private Image _adIcon; private string _adIconFor; private float _adIconSize = -1f;
        // 11-16 spacing pass (Spencer: "cluttered ... the spacing is wrong"; measured: buttons 87% of the card width, gaps 25-40px
        // and uneven). Smaller, narrower buttons; even ~40-60 card-px gaps between box / line / buttons / life line.
        private const float CV_W = 1000f, CV_SIDE = 70f, CV_BOX_Y0 = 210f, CV_BOX_H = 280f, CV_LINE_Y = 545f, CV_PLAY_Y = 705f, CV_AD_Y = 928f, CV_LIFE_Y = 1100f, CV_H = 1180f;
        private const float CV_BTN_W = 640f, CV_BTN_H = 190f;
        public static float CV_ICON_K = 0.56f;
        // 11-16 hierarchy pass (measured his shot: title cap 60px, buttons 45, body ~20, goal count 12): buttons x0.8 (-> ~36),
        // body text x1.2 (-> ~24), goal count bold ~30. Title 60 -> buttons 36 -> body 24 steps down evenly.
        public static float CV_LABEL_K = 1.0f, CV_BODY_K = 1.2f;   // label 0.8 overshot once the buttons shrank (shot 13:04: 28px vs title 55)
        public static float CV_BTN_EXTRA = 330f;   // art px the pill stretches by (9-slice), Spencer 11-16 (150 -> 330 later that day: "Play On 300" filled 87% of the pill; 330 = pill 746 of the card's 1000, text ~72%)   // icon height vs the label's line box
        private static readonly Color CV_INK = new Color32(0x3A, 0x1A, 0x7A, 0xFF);
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_cvSprites = new System.Collections.Generic.Dictionary<string, Sprite>();
        private static Sprite CvSprite(string name, Vector4 border = default)
        {
            string key = name + border;
            if (s_cvSprites.TryGetValue(key, out var sp) && sp != null) return sp;
            var tex = Resources.Load<Texture2D>("UI/Panel/" + name);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border) : null;
            s_cvSprites[key] = sp; return sp;
        }
        /// <summary>Centre <paramref name="rt"/> at (x, y) mock px from the card's TOP-LEFT (y down).</summary>
        private static void CvPlace(RectTransform rt, float x, float y, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h); rt.localRotation = Quaternion.identity;
        }
        private static RectTransform CvNode(string name, Transform parent, params System.Type[] comps)
        {
            var list = new System.Collections.Generic.List<System.Type> { typeof(RectTransform) }; list.AddRange(comps);
            var go = new GameObject(name, list.ToArray()); go.transform.SetParent(parent, false); return (RectTransform)go.transform;
        }
        private static float CvFont(float capPx) => capPx / 0.594f / HUDManager.RECIPE_MAX_MUL;
        private static TextMeshProUGUI TopLabel(ArtButton ab)
        {
            var ts = ab != null ? ab.GetComponentsInChildren<TextMeshProUGUI>(true) : null;
            return ts != null && ts.Length > 0 ? ts[ts.Length - 1] : null;
        }

        private void BuildCardV2()
        {
            var panelRT = (RectTransform)_panel.transform;
            var pImg = _panel.GetComponent<Image>(); if (pImg != null) pImg.color = Color.clear;
            if (_coinDisplay != null) _coinDisplay.SetActive(false);   // the HUD bar already shows the coin balance

            _cardV2 = CvNode("CardV2", panelRT);
            _cardV2.anchorMin = _cardV2.anchorMax = new Vector2(0.5f, 0.5f); _cardV2.pivot = new Vector2(0.5f, 0.5f);
            _cardV2.sizeDelta = new Vector2(CV_W, CV_H); _cardV2.anchoredPosition = Vector2.zero;
            var frame = CvNode("Frame", _cardV2, typeof(Image)).GetComponent<Image>();
            frame.sprite = CvSprite("panel", new Vector4(205, 225, 205, 205)); frame.type = Image.Type.Sliced; frame.raycastTarget = false;
            var frt = frame.rectTransform; frt.anchorMin = Vector2.zero; frt.anchorMax = Vector2.one; frt.offsetMin = new Vector2(-90f, -110f); frt.offsetMax = new Vector2(90f, 90f);

            float boxW = CV_W - 2f * CV_SIDE;
            var box = CvNode("GoalBox", _cardV2, typeof(Image)).GetComponent<Image>();
            box.sprite = CvSprite("cream", new Vector4(52, 52, 52, 52)); box.type = Image.Type.Sliced; box.raycastTarget = false;
            CvPlace(box.rectTransform, CV_W / 2f, CV_BOX_Y0 + CV_BOX_H / 2f, boxW, CV_BOX_H);

            var header = CvNode("HeaderPlate", _cardV2, typeof(Image)).GetComponent<Image>(); header.sprite = CvSprite("header"); header.raycastTarget = false;
            CvPlace(header.rectTransform, 110f - 20f + 410f, 130f - 298f + 179f, 820f, 358f);

            if (_titleText != null)
            {
                _titleText.transform.SetParent(_cardV2, false); _titleText.text = "Continue?";
                _titleText.fontSize = CvFont(LevelIntroModal.V2_TITLE_CAP);
                HUDManager.ApplyRecipeCount(_titleText, LevelIntroModal.V2_TITLE_EDGE_RK, LevelIntroModal.V2_TITLE_STROKE_RK, LevelIntroModal.V2_TITLE_DROP_RK, Resources.Load<TMP_FontAsset>("WordSlop Title Medium SDF"));
                _titleText.characterSpacing = 0f; _titleText.enableAutoSizing = false; _titleText.alignment = TextAlignmentOptions.Center;
                CvPlace(_titleText.rectTransform, CV_W / 2f, 10f - LevelIntroModal.V2_TITLE_LIFT, 760f, 170f);   // 11-17 lifted with every card header
            }
            if (_bodyText != null)
            {
                _bodyText.transform.SetParent(box.transform, false);
                _bodyText.fontSize = CvFont(LevelIntroModal.V2_BODY_CAP * CV_BODY_K); HUDManager.ApplyRecipeFlat(_bodyText); _bodyText.color = CV_INK; _bodyText.enableAutoSizing = false;
                _bodyText.alignment = TextAlignmentOptions.Center;
                CvPlace(_bodyText.rectTransform, boxW / 2f, 52f, boxW - 40f, 80f);
            }
            if (_objectiveIconHolder != null)
            {
                _objectiveIconHolder.transform.SetParent(box.transform, false);
                var irt = (RectTransform)_objectiveIconHolder.transform; CvPlace(irt, boxW / 2f, 175f, 10f, 10f);
            }
            GOALS_LEFT_ICON = 130f;   // card px (the old panel used 80)
            _v2ContinueOnly.Add(box.gameObject);

            var line = CvNode("GetMoves", _cardV2, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            line.text = "Get +5 moves to finish!"; line.alignment = TextAlignmentOptions.Center; line.raycastTarget = false;
            line.fontSize = CvFont(38f * CV_BODY_K); HUDManager.ApplyRecipeCount(line, LevelIntroModal.V2_TITLE_EDGE_RK, 0.12f, -0.6f);
            CvPlace(line.rectTransform, CV_W / 2f, CV_LINE_Y, CV_W - 80f, 70f);
            _v2ContinueOnly.Add(line.gameObject);

            // buttons: the Level card's Play art; clicks go to the old handlers (the old buttons are hidden, not removed)
            _playArt = ArtButton.Create(_cardV2, "pill_play", "Play On" + COIN_GAP + "50", lilita: false, titleMedium: false);
            _adArt = ArtButton.Create(_cardV2, "pill_play", COIN_GAP + "Watch Ad", lilita: false, titleMedium: false);   // icon FIRST: trailing spaces don't count in centring (the clapper overflowed)
            if (_playArt != null && _adArt != null)
            {
                var purple = Resources.Load<Sprite>("UI/Buttons/pill_play_face_purple");
                var faceArt = _adArt.Face != null ? _adArt.Face.Find("FaceArt") : null;
                if (purple != null && faceArt != null) faceArt.GetComponent<Image>().sprite = purple;
                // 11-16: 9-slice WIDER (Spencer) so "Play On (coin) 50" / "(clapper) Watch Ad" fit at full size; root = the art's new aspect
                _playArt.Widen(CV_BTN_EXTRA); _adArt.Widen(CV_BTN_EXTRA);
                var fs = _playArt.FrameSize; float bw = CV_BTN_H * fs.x / fs.y;
                CvPlace((RectTransform)_playArt.transform, CV_W / 2f, CV_PLAY_Y, bw, CV_BTN_H); _playArt.Refit();
                CvPlace((RectTransform)_adArt.transform, CV_W / 2f, CV_AD_Y, bw, CV_BTN_H); _adArt.Refit();
                _playArt.PlaySounds = false; _adArt.PlaySounds = false;   // the old handlers play the press/release halves
                _v2ContinueOnly.Add(_playArt.gameObject); _v2ContinueOnly.Add(_adArt.gameObject);
                if (_continueButtonGO != null)
                {
                    _continueButtonGO.SetActive(false);
                    _playArt.Button.onClick.AddListener(OnContinueClicked);
                    AddPressSound(_playArt.gameObject);
                    _continueButtonGO = _playArt.gameObject; _continueButton = _playArt.Button;
                    _continueButtonCG = _playArt.gameObject.AddComponent<CanvasGroup>();
                    _continueLabel = TopLabel(_playArt);
                }
                if (_adButtonGO != null)
                {
                    _adButtonGO.SetActive(false);
                    _adArt.Button.onClick.AddListener(OnAdClicked);
                    AddPressSound(_adArt.gameObject);
                    _adButtonGO = _adArt.gameObject; _adButton = _adArt.Button;
                    _adButtonCG = _adArt.gameObject.AddComponent<CanvasGroup>();
                    _adLabelTop = TopLabel(_adArt); _adLabel = _adLabelTop;
                }
            }

            // "You will lose a life!" - broken heart + one line
            var life = CvNode("LoseLife", _cardV2);
            _v2ContinueOnly.Add(life.gameObject);
            CvPlace(life, CV_W / 2f, CV_LIFE_Y, 700f, 80f);
            var heartSp = LoadHeart("heart_broken");
            var lifeTxt = CvNode("Text", life, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            lifeTxt.text = "You will lose a life!"; lifeTxt.raycastTarget = false; lifeTxt.alignment = TextAlignmentOptions.Center;
            lifeTxt.fontSize = CvFont(34f * CV_BODY_K); HUDManager.ApplyRecipeCount(lifeTxt, new Color32(0x3C, 0x0A, 0x28, 0xFF), 0.12f, -0.6f);
            lifeTxt.color = new Color32(0xFF, 0xD6, 0xD6, 0xFF); lifeTxt.enableVertexGradient = false;
            var lrt = lifeTxt.rectTransform; lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 0.5f); lrt.sizeDelta = new Vector2(600f, 80f); lrt.anchoredPosition = new Vector2(40f, 0f);
            if (heartSp != null)
            {
                var hi = MakeHeartImage(life, "Heart", heartSp, 72f);
                lifeTxt.ForceMeshUpdate(); float tw = lifeTxt.preferredWidth;
                hi.rectTransform.anchoredPosition = new Vector2(40f - tw / 2f - 50f, 0f);
            }

            // the close X: the Level card's gold ring + red badge on the panel's top-right corner
            if (_giveUpButtonGO != null)
            {
                _giveUpButtonGO.transform.SetParent(_cardV2, false);
                var xi = _giveUpButtonGO.GetComponent<Image>(); xi.sprite = CvSprite("close"); xi.type = Image.Type.Simple; xi.color = Color.white;
                foreach (Transform ch in _giveUpButtonGO.transform) ch.gameObject.SetActive(false);
                CvPlace((RectTransform)_giveUpButtonGO.transform, CV_W - 0.62f * 168f + 84f, -0.62f * 168f + 84f + 6f, 168f, 180f);
            }
        }
        // ── 11-16 "TRY AGAIN?" (Spencer: "just build it how you think is best"): RM's fail flow. X on Continue doesn't end the run
        // any more: you lose a life and the SAME card turns into Try Again? (broken heart, lives row, green Try Again). Try Again ->
        // the same level's card (fresh deal, same seed); X -> home on the same level. The old path sent you to GameOverUI and its
        // Play Again restarted the RUN at level 1 - there is no saved level progress, so a loss on L15 meant replaying L1-14.
        private bool _failedState;
        private Image _retryCoin; private string _retryCoinFor; private float _retryCoinSize = -1f;
        private RectTransform _failGroup; private ArtButton _retryArt; private TextMeshProUGUI _failText;
        private readonly System.Collections.Generic.List<GameObject> _v2ContinueOnly = new System.Collections.Generic.List<GameObject>();
        private Image[] _lifeRow;
        public static bool HEART_SOCKETS = true; private bool _hasSockets;
        private const float FAIL_H = 940f, FAIL_HEARTS_Y = 570f, FAIL_BTN_Y = 755f;
        /// <summary>11-16: the home Play button with 0 lives - this card in its "No lives left!" state (no life taken). done(true) =
        /// refilled (open the level), done(false) = closed.</summary>
        private System.Action<bool> _homeLivesDone;
        public void ShowOutOfLives(System.Action<bool> done)
        {
            Show(SurvivalManager.Instance);
            _homeLivesDone = done;
            ShowFailedState(consume: false);
        }
        private bool FinishHomeLives(bool refilled)
        {
            if (_homeLivesDone == null) return false;
            var d = _homeLivesDone; _homeLivesDone = null;
            _afterHide = () => d(refilled); Hide();
            return true;
        }
        private void ShowFailedState(bool consume = true)
        {
            _failedState = true;
            if (consume) HeartsManager.Consume();
            try { AnalyticsManager.Log("level_failed", "stage", _survival != null ? _survival.CurrentStageIndex : -1, "lives_left", HeartsManager.Current); }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }
            if (_failGroup == null) BuildFailGroup();
            foreach (var go in _v2ContinueOnly) if (go != null) go.SetActive(false);
            if (_bodyText != null) _bodyText.gameObject.SetActive(false);
            if (_objectiveIconHolder != null) _objectiveIconHolder.SetActive(false);
            _failGroup.gameObject.SetActive(true);
            if (_titleText != null) _titleText.text = "Try Again?";
            _cardV2.sizeDelta = new Vector2(CV_W, FAIL_H);
            int lives = HeartsManager.Current;
            _refilling = false;   // a refill animation cut short by closing the card must not block the next one
            for (int i = 0; i < _lifeRow.Length; i++) { _lifeRow[i].DOKill(); _lifeRow[i].rectTransform.DOKill(); _lifeRow[i].rectTransform.localScale = Vector3.one; _lifeRow[i].color = i < lives ? Color.white : _hasSockets ? new Color(1f, 1f, 1f, 0f) : new Color(0.32f, 0.22f, 0.5f, 0.55f); }
            bool none = lives <= 0;
            // 11-16 (shot: the 2-line 0-lives text spilled over the box and under the heart): one line in the box; the price goes on
            // the button with the coin, like Play On
            if (_failText != null) _failText.text = none ? "No lives left!" : "You lost a life";
            if (_retryArt != null) _retryArt.Label = none ? "Refill" + COIN_GAP + HeartWaitModal.REFILL_COIN_COST : "Try Again";
            _cardV2.DOKill(); _cardV2.localScale = Vector3.one * 0.92f;
            _cardV2.DOScale(1f, 0.28f).SetEase(Ease.OutBack).SetUpdate(true);
        }
        private void LeaveFailedState()
        {
            if (!_failedState) return;
            _failedState = false;
            foreach (var go in _v2ContinueOnly) if (go != null) go.SetActive(true);
            if (_bodyText != null) _bodyText.gameObject.SetActive(true);
            if (_failGroup != null) _failGroup.gameObject.SetActive(false);
            if (_titleText != null) _titleText.text = "Continue?";
            if (_cardV2 != null) _cardV2.sizeDelta = new Vector2(CV_W, CV_H);
        }
        private void BuildFailGroup()
        {
            _failGroup = CvNode("TryAgain", _cardV2);
            _failGroup.anchorMin = Vector2.zero; _failGroup.anchorMax = Vector2.one; _failGroup.offsetMin = _failGroup.offsetMax = Vector2.zero;
            float boxW = CV_W - 2f * CV_SIDE;
            var box = CvNode("FailBox", _failGroup, typeof(Image)).GetComponent<Image>();
            box.sprite = CvSprite("cream", new Vector4(52, 52, 52, 52)); box.type = Image.Type.Sliced; box.raycastTarget = false;
            CvPlace(box.rectTransform, CV_W / 2f, CV_BOX_Y0 + CV_BOX_H / 2f, boxW, CV_BOX_H);
            _failText = CvNode("Text", box.transform, typeof(TextMeshProUGUI)).GetComponent<TextMeshProUGUI>();
            _failText.fontSize = CvFont(LevelIntroModal.V2_BODY_CAP * CV_BODY_K); HUDManager.ApplyRecipeFlat(_failText); _failText.color = CV_INK;
            _failText.alignment = TextAlignmentOptions.Center; _failText.raycastTarget = false;
            _failText.textWrappingMode = TextWrappingModes.NoWrap; _failText.enableAutoSizing = true;
            _failText.fontSizeMax = _failText.fontSize; _failText.fontSizeMin = _failText.fontSize * 0.6f;
            CvPlace(_failText.rectTransform, boxW / 2f, 62f, boxW - 40f, 100f);
            var broken = LoadHeart("heart_broken");
            if (broken != null) { var hb = MakeHeartImage(box.transform, "BrokenHeart", broken, 150f); CvPlace(hb.rectTransform, boxW / 2f, 195f, 150f, 150f); }
            var whole = LoadHeart("heart_whole");
            _lifeRow = new Image[HeartsManager.MAX_HEARTS];
            var socket = HEART_SOCKETS ? LoadHeart("heart_socket") : null; _hasSockets = socket != null;
            for (int i = 0; i < _lifeRow.Length; i++)
            {
                // 11-18 (Spencer: "could the hearts spaces ... have a stroke around them or something ... the look like theres an inset
                // divot for the hearts to go"): a heart-shaped SOCKET under every life (Hearts/heart_socket = heart_whole's own
                // silhouette: darker recess, shadow under its top edge, a light lip along the bottom). A lost life shows the empty
                // socket; before, it was the heart itself tinted dark. Art: ~/Documents/WordDrop_Art/heart_socket_1118/make_socket.py
                if (HEART_SOCKETS && socket != null)
                {
                    var sk = MakeHeartImage(_failGroup, "LifeSocket" + i, socket, 84f);
                    CvPlace(sk.rectTransform, CV_W / 2f + (i - (_lifeRow.Length - 1) / 2f) * 104f, FAIL_HEARTS_Y, 84f, 84f);
                }
                _lifeRow[i] = MakeHeartImage(_failGroup, "Life" + i, whole, 84f);
                CvPlace(_lifeRow[i].rectTransform, CV_W / 2f + (i - (_lifeRow.Length - 1) / 2f) * 104f, FAIL_HEARTS_Y, 84f, 84f);
            }
            _retryArt = ArtButton.Create(_failGroup, "pill_play", "Try Again", lilita: false, titleMedium: false);
            if (_retryArt != null)
            {
                _retryArt.Widen(CV_BTN_EXTRA);
                var fs = _retryArt.FrameSize; float bw = CV_BTN_H * fs.x / fs.y;
                CvPlace((RectTransform)_retryArt.transform, CV_W / 2f, FAIL_BTN_Y, bw, CV_BTN_H); _retryArt.Refit();
                _retryArt.Button.onClick.AddListener(OnRetryClicked);
            }
            _failGroup.gameObject.SetActive(false);
        }
        /// <summary>11-16 Spencer: "when they refill their lives can you show some kind of animation that shows the hearts refilling
        /// so the player knows this has happened". The card's five grey hearts fill left to right (pop + rising note each), the
        /// line reads "Lives refilled!", a short beat, then the card carries on as before. False = no animation.</summary>
        public static bool REFILL_ANIM = true;
        public static float REFILL_STEP = 0.13f, REFILL_HOLD = 0.45f;
        private bool _refilling;
        private System.Collections.IEnumerator RefillHeartsThen()
        {
            _refilling = true;
            if (_failText != null) _failText.text = "Lives refilled!";
            for (int i = 0; i < _lifeRow.Length; i++)
            {
                var h = _lifeRow[i]; if (h == null) continue;
                h.DOKill(); h.rectTransform.DOKill();
                h.DOColor(Color.white, 0.10f).SetUpdate(true);
                h.rectTransform.localScale = Vector3.one * 0.6f;
                DOTween.Sequence().SetUpdate(true).SetTarget(h.rectTransform)
                    .Append(h.rectTransform.DOScale(1.3f, 0.10f).SetEase(Ease.OutQuad))
                    .Append(h.rectTransform.DOScale(1f, 0.12f).SetEase(Ease.OutBack));
                GameAudio.Instance?.PlayKit("bubble_pop", 0.7f, 1f + i * 0.09f);
                yield return new WaitForSecondsRealtime(REFILL_STEP);
            }
            yield return new WaitForSecondsRealtime(REFILL_HOLD);
            _refilling = false;
            if (!FinishHomeLives(true)) HideThenRetry(true);
        }
        private void OnRetryClicked()
        {
            if (HeartsManager.Current <= 0)
            {
                if (_refilling) return;   // already paid - the hearts are filling
                if (!CoinWallet.Spend(HeartWaitModal.REFILL_COIN_COST)) { if (!FinishHomeLives(false)) HideThenRetry(false); return; }
                HeartsManager.GrantFull();
                if (REFILL_ANIM && _lifeRow != null && isActiveAndEnabled) { StartCoroutine(RefillHeartsThen()); return; }
                if (FinishHomeLives(true)) return;
            }
            if (_refilling) return;
            HideThenRetry(true);
        }
        /// <summary>The retry runs AFTER the card's exit: FinalizeHide un-pauses gameplay, which would otherwise land on top of the
        /// home screen's pause and let the board run under it.</summary>
        private System.Action _afterHide;
        private void HideThenRetry(bool openCard)
        {
            if (_isDismissing) return;
            var s = _survival;
            _afterHide = () => s?.RetryCurrentLevel(openCard);
            Hide();
        }

        private string _eqFor;
        /// <summary>11-16 (Spencer's shot: "Watch Ad" drew bigger than "Play On" - each label auto-sizes to its own width): both buttons
        /// use the smaller of the two auto sizes.</summary>
        private void EqualiseLabels()
        {
            string key = _playArt.Label + "|" + _adArt.Label; if (_eqFor == key) return;
            var a = _playArt.GetComponentsInChildren<TextMeshProUGUI>(true); var b = _adArt.GetComponentsInChildren<TextMeshProUGUI>(true);
            if (a.Length == 0 || b.Length == 0) return;
            foreach (var t in a) { t.enableAutoSizing = true; t.ForceMeshUpdate(); }
            foreach (var t in b) { t.enableAutoSizing = true; t.ForceMeshUpdate(); }
            float size = Mathf.Min(a[a.Length - 1].fontSize, b[b.Length - 1].fontSize) * CV_LABEL_K;
            foreach (var t in a) { t.enableAutoSizing = false; t.fontSize = size; }
            foreach (var t in b) { t.enableAutoSizing = false; t.fontSize = size; }
            _eqFor = key; _coinPlacedFor = null; _adIconFor = null;   // re-place the coin + clapper at the new size
        }
        private static void AddPressSound(GameObject go)
        {
            var trig = go.GetComponent<EventTrigger>() ?? go.AddComponent<EventTrigger>();
            var e = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            e.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            trig.triggers.Add(e);
        }
        // 11-16 (Spencer: "make sure the lettering is centered on the buttons"), MEASURED on a 720-wide game shot:
        //  - "Watch Ad": the WORDS sat dead centre (x 360.0) with the clapper hanging off their left, so clapper + words read
        //    ~25 px left of centre. The label now shifts right by half the clapper's overhang: the pair is what gets centred.
        //  - every label on this card sat 4-5 px LOW (letters' centre 754 / 890.5 vs face centre ~749.5 / 886.5): these labels use
        //    WordSlop Button, the art's text box was tuned for the Level card's PLAY font. Lifted CV_LABEL_LIFT card px (1179-wide).
        public static float CV_LABEL_LIFT = 7.4f;
        private readonly System.Collections.Generic.Dictionary<ArtButton, Vector2> _labelHome = new System.Collections.Generic.Dictionary<ArtButton, Vector2>();
        private void SeatLabel(ArtButton ab, Image leadIcon)
        {
            if (ab == null || ab.Face == null || _cardV2 == null) return;
            var root = ab.Face.Find("Label") as RectTransform; if (root == null) return;
            if (!_labelHome.TryGetValue(ab, out var home)) { home = root.anchoredPosition; _labelHome[ab] = home; }
            float cardS = _cardV2.lossyScale.x, parentS = root.parent.lossyScale.x;
            if (cardS <= 0f || parentS <= 0f) return;
            float dx = 0f, dyTail = 0f;
            var l = TopLabel(ab);
            // 11-16 re-measure ("Play On" shot 17:02): the label is centred by its INK, tails included, so the y's tail pushed the
            // capitals 4.5 px above the face centre (caps 727-764 vs face 750) while "Watch Ad" / "Refill" (no tails) sat right.
            // Drop the label by half the tail's depth: the CAPITALS are what gets centred, whatever the word.
            if (l != null && l.textInfo != null)
            {
                var ti = l.textInfo; float tail = 0f, capH = 0f;
                for (int i = 0; i < ti.characterCount; i++)
                {
                    var ci = ti.characterInfo[i]; if (!ci.isVisible) continue;
                    // 11-18 (Spencer's screenshot: "the words Play On for button are not centered" - capitals 8 px low; they were
                    // 0.5 px off on 11-16): bottomLeft / topRight are the letter's QUAD, which includes the material's padding, so
                    // the "tail" grew when the text recipe changed the padding - and words with NO tail ("Watch Ad") started
                    // counting padding as one. Measure the tail from the GLYPH itself: independent of any outline or shadow.
                    var g = ci.textElement != null ? ci.textElement.glyph : null;
                    if (g != null)
                    {
                        float below = (g.metrics.height - g.metrics.horizontalBearingY) * ci.scale, above = g.metrics.horizontalBearingY * ci.scale;
                        tail = Mathf.Max(tail, below); capH = Mathf.Max(capH, above);
                    }
                    else { tail = Mathf.Max(tail, ci.baseLine - ci.bottomLeft.y); capH = Mathf.Max(capH, ci.topRight.y - ci.baseLine); }
                }
                if (capH > 0f && tail > capH * 0.1f) dyTail = -tail * 0.5f * l.transform.lossyScale.y / parentS;
            }
            if (leadIcon != null && leadIcon.gameObject.activeInHierarchy && l != null && l.textInfo != null)
            {
                float left = float.MaxValue, right = float.MinValue; var ti = l.textInfo;
                for (int i = 0; i < ti.characterCount; i++) { var ci = ti.characterInfo[i]; if (!ci.isVisible) continue; left = Mathf.Min(left, ci.bottomLeft.x); right = Mathf.Max(right, ci.topRight.x); }
                float il = leadIcon.rectTransform.localPosition.x - leadIcon.rectTransform.sizeDelta.x * 0.5f;
                if (right > left && il < left) dx = (left - il) * 0.5f * l.transform.lossyScale.x / parentS;
            }
            root.anchoredPosition = home + new Vector2(dx, CV_LABEL_LIFT * cardS / parentS + dyTail);
        }

        /// <summary>11-16: an icon parked in the COIN_GAP of a label (the ad's clapper), measured from TMP's layout like the coin.</summary>
        private static void PlaceIconInGap(TextMeshProUGUI l, ref Image icon, ref string placedFor, ref float placedSize, string sprite, bool lastGap)
        {
            if (l == null || !l.gameObject.activeInHierarchy) return;
            if (placedFor == l.text && Mathf.Approximately(placedSize, l.fontSize)) return;
            int gap = lastGap ? l.text.LastIndexOf(COIN_GAP, System.StringComparison.Ordinal) : l.text.IndexOf(COIN_GAP, System.StringComparison.Ordinal);
            if (icon == null)
            {
                var tex = Resources.Load<Texture2D>(sprite); if (tex == null) return;
                var go = new GameObject("IconInLabel", typeof(RectTransform), typeof(Image)); go.transform.SetParent(l.transform, false);
                icon = go.GetComponent<Image>(); icon.sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                icon.preserveAspect = true; icon.raycastTarget = false;
                var crt = (RectTransform)go.transform; crt.anchorMin = crt.anchorMax = crt.pivot = new Vector2(0.5f, 0.5f);
            }
            l.ForceMeshUpdate(); placedFor = l.text; placedSize = l.fontSize;
            int mid = gap + COIN_GAP.Length / 2;
            if (gap < 0 || mid >= l.textInfo.characterCount) { icon.gameObject.SetActive(false); return; }
            var ci = l.textInfo.characterInfo[mid]; var line = l.textInfo.lineInfo[ci.lineNumber];
            float h = (line.ascender - line.descender) * CV_ICON_K;
            icon.rectTransform.localPosition = new Vector3((ci.origin + ci.xAdvance) * 0.5f, (line.ascender + line.descender) * 0.5f, 0f);
            icon.rectTransform.sizeDelta = new Vector2(h, h); icon.gameObject.SetActive(true);
        }
        private Button _continueButton;
        private GameObject _continueButtonGO;
        private CanvasGroup _continueButtonCG;
        private Button _adButton;
        private GameObject _adButtonGO;
        private CanvasGroup _adButtonCG;
        private GameObject _giveUpButtonGO;
        private CanvasGroup _giveUpButtonCG;
        private GameObject _coinDisplay;
        private CanvasGroup _coinDisplayCG;
        private TextMeshProUGUI _coinAmountText;
        private SurvivalManager _survival;
        private int _currentCost;
        private bool _adInFlight;
        private bool _isPresenting;
        private bool _isDismissing;
        private Sequence _entranceSequence;
        private float _titleZRot; // animated z-rotation for the toss-in
        private Vector2 _panelRestAnchoredPos; // cached at build so AnimateEntrance can restore after ExitUp's SetRelative drift

        // Animation tuning — mirrors StageClearModal but snappier per Spencer's
        // 2026-06-01 ask ("should appear a lot faster"). The end-game continue
        // moment is high-anxiety; the player wants to act, not watch animations.
        private const float DROP_SPEED   = 2.5f;        // panel drop multiplier (StageClearModal uses 1.5)
        private const float BACKDROP_FADE_DUR = 0.10f;  // was 0.18
        private const float SETTLE_BEAT       = 0.05f;  // post-drop hold before title pops (was 0.25)
        private const float STAGGER           = 0.04f;  // child reveal stagger (was 0.06-0.10)
        private const float CHILD_FADE_DUR    = 0.12f;  // each child fade-in (was 0.18)
        private static Color BACKDROP_DIM => UISeparation.MODAL_DIM;   // 11-02 shared dim (was black .55)

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildUI();
            if (CARD_V2) BuildCardV2();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("ContinueModalCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.GetComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = CANVAS_SORT_ORDER;

            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            // 2026-06-01: switched reference resolution to the PSD canvas
            // (1179×2556 — iPhone 16/15 Pro) so the panel can use PSD coords
            // directly without translation math.
            scaler.referenceResolution = new Vector2(1179f, 2556f);
            scaler.matchWidthOrHeight = 0.5f;

            // Backdrop dim (full screen, tap-blocking). Stored as field so
            // Show/Hide can fade it independently of the panel drop animation.
            var dimGO = new GameObject("Dim", typeof(RectTransform), typeof(Image));
            dimGO.transform.SetParent(canvasGO.transform, false);
            var dimRT = dimGO.GetComponent<RectTransform>();
            dimRT.anchorMin = Vector2.zero;
            dimRT.anchorMax = Vector2.one;
            dimRT.offsetMin = Vector2.zero;
            dimRT.offsetMax = Vector2.zero;
            _backdrop = dimGO.GetComponent<Image>();
            _backdrop.color = BACKDROP_DIM;

            // Panel card — sized + positioned per Spencer's PSD spec
            // (W=1180, H=1230 at PSD X=0, Y=500). PSD origin is top-left; Unity
            // anchors are bottom-left. Conversion:
            //   anchorMin.y = (2556 - 500 - 1230) / 2556 ≈ 0.323
            //   anchorMax.y = (2556 - 500)        / 2556 ≈ 0.805
            // Width spans the full canvas. Pivot stays centered so the
            // DropInWithBounce tween animates around the rect's centre.
            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            _panel = panelGO;
            var panelRT = panelGO.GetComponent<RectTransform>();
            panelRT.anchorMin = new Vector2(0f,    0.323f);
            panelRT.anchorMax = new Vector2(1f,    0.805f);
            panelRT.pivot     = new Vector2(0.5f,  0.5f);
            panelRT.offsetMin = Vector2.zero;
            panelRT.offsetMax = Vector2.zero;
            panelGO.GetComponent<Image>().color = new Color(0.99f, 0.95f, 0.86f, 0.98f); // TEMP candy-unification
            // CanvasGroup lets us hide the panel across the SetActive→
            // DropInWithBounce gap. Without this, the panel renders briefly at
            // rest position before DropInWithBounce snaps it offscreen, which
            // reads as a "double pop in" (Spencer 2026-06-01).
            _panelCanvasGroup = panelGO.AddComponent<CanvasGroup>();

            // Cache the rest anchoredPosition NOW so we can restore it on
            // every Show. ExitUp uses SetRelative(true) which leaves the
            // panel high above the canvas after a hide — if we don't reset,
            // DropInWithBounce reads the drifted position as the new rest
            // and the panel ratchets further off-screen each cycle.
            _panelRestAnchoredPos = panelRT.anchoredPosition;

            // Title
            var titleGO = new GameObject("Title", typeof(RectTransform));
            titleGO.transform.SetParent(panelRT, false);
            _titleText = titleGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _titleText.font = displayFont;
            _titleText.text = "CONTINUE?";
            _titleText.fontSize = 150; // Spencer 2026-06-01 — bigger title for the CONTINUE? hero moment
            _titleText.alignment = TextAlignmentOptions.Center;
            _titleText.color = new Color(0.82f, 0.28f, 0.46f, 1f);
            var titleRT = titleGO.GetComponent<RectTransform>();
            titleRT.anchorMin = new Vector2(0.05f, 0.75f);  // 2026-06-24: nudged down so the big title fits inside the panel
            titleRT.anchorMax = new Vector2(0.95f, 0.92f);
            titleRT.offsetMin = Vector2.zero;
            titleRT.offsetMax = Vector2.zero;

            // Body — describes what continue does
            var bodyGO = new GameObject("Body", typeof(RectTransform));
            bodyGO.transform.SetParent(panelRT, false);
            _bodyText = bodyGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) _bodyText.font = displayFont;
            _bodyText.text = "Clear the top rows\nand refill resources";
            _bodyText.fontSize = 28;
            _bodyText.alignment = TextAlignmentOptions.Center;
            _bodyText.color = new Color(0.32f, 0.24f, 0.30f, 1f);
            var bodyRT = bodyGO.GetComponent<RectTransform>();
            bodyRT.anchorMin = new Vector2(0.05f, 0.70f);   // raised to make room for the bigger objective icon below it
            bodyRT.anchorMax = new Vector2(0.95f, 0.745f);
            bodyRT.offsetMin = Vector2.zero;
            bodyRT.offsetMax = Vector2.zero;
            _bodyText.alignment = TextAlignmentOptions.Bottom;

            // Remaining-objective ICON holder — point-anchored just below the "So close" line.
            _panelRTForHeart = panelRT;
            var iconHolderGO = new GameObject("ObjectiveIconHolder", typeof(RectTransform));
            iconHolderGO.transform.SetParent(panelRT, false);
            _objectiveIconHolder = iconHolderGO;
            _objectiveIconCG = iconHolderGO.AddComponent<CanvasGroup>();
            var iconHolderRT = iconHolderGO.GetComponent<RectTransform>();
            iconHolderRT.anchorMin = iconHolderRT.anchorMax = new Vector2(0.5f, 0.625f);
            iconHolderRT.pivot     = new Vector2(0.5f, 0.5f);
            iconHolderRT.sizeDelta = new Vector2(10f, 10f);

            // Continue (PLAY ON) button (paid path) — primary CTA. Cost is
            // dynamic per SurvivalManager.CurrentContinueCost (50→100 ladder).
            // 2026-06-01: label "CONTINUE" → "PLAY ON" per Spencer; coin glyph
            // preserved. Idle pulse + press-squash + split multi-pop SFX added
            // to mirror StageClearModal's continue button feel.
            var continueBtnGO = MakeButton(panelRT, "ContinueBtn",
                new Vector2(0.10f, 0.42f), new Vector2(0.90f, 0.56f),
                "PLAY ON" + COIN_GAP + "50",
                new Color(0.96f, 0.63f, 0.16f, 1f), // 2026-06-24: warm orange CTA (was green)
                OnContinueClicked);
            PinButtonSize(continueBtnGO, 0.45f);  // 2026-06-24: nudged down to open room above for the objective icon
            _continueButtonGO = continueBtnGO;
            _continueButton = continueBtnGO.GetComponent<Button>();
            _continueLabel = continueBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            _continueButtonCG = continueBtnGO.AddComponent<CanvasGroup>();

            // Split multi-pop SFX: PointerDown = press half, onClick = release
            // half. Same pattern wired on StageClearModal's CONTINUE button.
            var pressTrigger = continueBtnGO.GetComponent<UnityEngine.EventSystems.EventTrigger>();
            if (pressTrigger == null)
                pressTrigger = continueBtnGO.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var pdEntry = new UnityEngine.EventSystems.EventTrigger.Entry
            {
                eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown,
            };
            pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            pressTrigger.triggers.Add(pdEntry);

            // Watch Ad button — Codex rule: visually secondary but clearly available.
            // Muted blue. Same rescue path as paid continue.
            var adBtnGO = MakeButton(panelRT, "AdBtn",
                new Vector2(0.15f, 0.26f), new Vector2(0.85f, 0.39f),
                "WATCH AD",
                new Color(0.25f, 0.42f, 0.62f, 1f),
                OnAdClicked);
            PinButtonSize(adBtnGO, 0.285f);  // 2026-06-24: down with PLAY ON, keeps the gap
            _adButtonGO = adBtnGO;
            _adButton = adBtnGO.GetComponent<Button>();
            _adLabel = adBtnGO.GetComponentInChildren<TextMeshProUGUI>();
            _adButtonCG = adBtnGO.AddComponent<CanvasGroup>();
            MenuUI.AddButtonIcon(adBtnGO, "UI/icon_clapper");   // 2026-10-22 Spencer's clapper
            // 2026-06-01: smaller-text override removed — Spencer wants all
            // three buttons at the 86px hero size set by MakeButton.

            // Same press-half / release-half SFX pattern as the PLAY ON button.
            var adPressTrigger = adBtnGO.GetComponent<UnityEngine.EventSystems.EventTrigger>();
            if (adPressTrigger == null)
                adPressTrigger = adBtnGO.AddComponent<UnityEngine.EventSystems.EventTrigger>();
            var adPdEntry = new UnityEngine.EventSystems.EventTrigger.Entry
            {
                eventID = UnityEngine.EventSystems.EventTriggerType.PointerDown,
            };
            adPdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            adPressTrigger.triggers.Add(adPdEntry);

            // 2026-06-01: GIVE UP button replaced with a top-right Cancel X
            // (mirrors SettingsModal.BuildCloseButton). Tapping the X is the
            // give-up action — calls OnGiveUpClicked which forwards to
            // SurvivalManager.DeclineContinue. Same multi-pop press/release
            // SFX as the Settings X.
            BuildCancelX(panelRT);

            // Coin total + buy-more "+" button (Candy-Crush-style pill in the
            // top-left corner). Tapping "+" is a placeholder for the future
            // IAP / coin store flow.
            BuildCoinDisplay(panelRT);
        }

        private void BuildCoinDisplay(RectTransform panelRT)
        {
            // Pill container — top-left of panel
            var containerGO = new GameObject("CoinDisplay",
                typeof(RectTransform), typeof(Image));
            containerGO.transform.SetParent(panelRT, false);
            var rt = containerGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot     = new Vector2(0f, 0.5f); // anchored from left edge
            rt.sizeDelta = new Vector2(320f, 110f);
            rt.anchoredPosition = new Vector2(40f, -68f); // 2026-06-24: lowered so the whole pill sits inside the panel (was clipping the top)
            containerGO.GetComponent<Image>().color = new Color(0.96f, 0.89f, 0.79f, 0.92f);
            containerGO.GetComponent<Image>().raycastTarget = false;

            // Coin amount label ("● 1234"). Yellow, matches HUD coin counter.
            var labelGO = new GameObject("Amount", typeof(RectTransform));
            labelGO.transform.SetParent(containerGO.transform, false);
            _coinAmountText = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) _coinAmountText.font = displayFont;
            _coinAmountText.text = "● 0";
            _coinAmountText.fontSize = 56;
            _coinAmountText.alignment = TextAlignmentOptions.MidlineRight;
            _coinAmountText.color = new Color(0.80f, 0.52f, 0.10f, 1f);
            _coinAmountText.raycastTarget = false;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = new Vector2(95f, 0f); // leave room for + button on the left
            labelRT.offsetMax = new Vector2(-20f, 0f);

            // "+" button — green circle on the left
            var plusGO = new GameObject("Plus",
                typeof(RectTransform), typeof(Image), typeof(Button));
            plusGO.transform.SetParent(containerGO.transform, false);
            var plusRT = plusGO.GetComponent<RectTransform>();
            plusRT.anchorMin = new Vector2(0f, 0.5f);
            plusRT.anchorMax = new Vector2(0f, 0.5f);
            plusRT.pivot     = new Vector2(0.5f, 0.5f);
            plusRT.sizeDelta = new Vector2(80f, 80f);
            plusRT.anchoredPosition = new Vector2(50f, 0f);
            plusGO.GetComponent<Image>().color = new Color(0.18f, 0.65f, 0.38f, 1f); // CTA green

            var plusGlyphGO = new GameObject("Glyph", typeof(RectTransform));
            plusGlyphGO.transform.SetParent(plusGO.transform, false);
            var plusTMP = plusGlyphGO.AddComponent<TextMeshProUGUI>();
            if (displayFont != null) plusTMP.font = displayFont;
            plusTMP.text = "+";
            plusTMP.fontSize = 64;
            plusTMP.alignment = TextAlignmentOptions.Center;
            plusTMP.color = Color.white;
            plusTMP.raycastTarget = false;
            var plusGlyphRT = plusGlyphGO.GetComponent<RectTransform>();
            plusGlyphRT.anchorMin = Vector2.zero;
            plusGlyphRT.anchorMax = Vector2.one;
            plusGlyphRT.offsetMin = Vector2.zero;
            plusGlyphRT.offsetMax = Vector2.zero;

            plusGO.GetComponent<Button>().onClick.AddListener(OnBuyCoinsTapped);

            _coinDisplay = containerGO;
            _coinDisplayCG = containerGO.AddComponent<CanvasGroup>();
        }

        private void OnBuyCoinsTapped()
        {
            // Placeholder — wire to IAP / coin store flow when that lands.
            GameAudio.Instance?.PlayUIClick();
            WDLog.Log("[ContinueModal] Buy coins tapped — TODO: open coin store / IAP flow");
        }

        private void BuildCancelX(RectTransform panelRT)
        {
            var btnGO = new GameObject("CancelX",
                typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(panelRT, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(110f, 110f);
            rt.anchoredPosition = new Vector2(-40f, -40f);

            var img = btnGO.GetComponent<Image>();
            img.color = new Color(0.55f, 0.18f, 0.18f, 1f); // muted red, matches SettingsModal close

            var glyphGO = new GameObject("X", typeof(RectTransform));
            glyphGO.transform.SetParent(btnGO.transform, false);
            var glyphTMP = glyphGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) glyphTMP.font = displayFont;
            glyphTMP.text = "X";   // 11-16 (Movie_013 log): "✕" (U+2715) is not in WordSlop SDF -> it drew an empty box
            glyphTMP.fontSize = 64;
            glyphTMP.alignment = TextAlignmentOptions.Center;
            glyphTMP.color = Color.white;
            glyphTMP.raycastTarget = false;
            var glyphRT = glyphGO.GetComponent<RectTransform>();
            glyphRT.anchorMin = Vector2.zero;
            glyphRT.anchorMax = Vector2.one;
            glyphRT.offsetMin = Vector2.zero;
            glyphRT.offsetMax = Vector2.zero;

            // Split multi-pop SFX, same as SettingsModal's close X.
            var trigger = btnGO.GetComponent<EventTrigger>();
            if (trigger == null) trigger = btnGO.AddComponent<EventTrigger>();
            var pdEntry = new EventTrigger.Entry { eventID = EventTriggerType.PointerDown };
            pdEntry.callback.AddListener((_) => GameAudio.Instance?.PlayMultiPopPress());
            trigger.triggers.Add(pdEntry);

            btnGO.GetComponent<Button>().onClick.AddListener(() =>
            {
                GameAudio.Instance?.PlayMultiPopRelease();
                // Same path as the old GIVE UP button — finalizes the game-over.
                OnGiveUpClicked();
            });

            // Re-use the give-up-button CanvasGroup slot for the staggered
            // fade-in. AnimateEntrance sets this to alpha=0 pre-show and
            // fades it back in alongside the other content.
            _giveUpButtonGO = btnGO;
            _giveUpButtonCG = btnGO.AddComponent<CanvasGroup>();
        }

        /// <summary>Pin a button to a fixed 550×120 PSD-pixel rect, centered
        /// horizontally on the panel at the given vertical anchor. Overrides
        /// whatever stretched anchorMin/anchorMax MakeButton applied.</summary>
        private static void PinButtonSize(GameObject btnGO, float verticalAnchorY)
        {
            if (btnGO == null) return;
            var rt = btnGO.GetComponent<RectTransform>();
            if (rt == null) return;
            rt.anchorMin        = new Vector2(0.5f, verticalAnchorY);
            rt.anchorMax        = new Vector2(0.5f, verticalAnchorY);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(550f, 120f);
            rt.anchoredPosition = Vector2.zero;
        }

        private GameObject MakeButton(RectTransform parent, string name,
            Vector2 anchorMin, Vector2 anchorMax,
            string label, Color bgColor, UnityEngine.Events.UnityAction onClick)
        {
            var btnGO = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            btnGO.transform.SetParent(parent, false);
            var rt = btnGO.GetComponent<RectTransform>();
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var btnImg = btnGO.GetComponent<Image>();
            btnImg.color  = bgColor;
            btnImg.sprite = MenuUI.GetRoundedRectSprite(28); // 2026-06-24: rounded corners (match the rest of the buttons)
            btnImg.type   = Image.Type.Sliced;
            btnGO.GetComponent<Button>().onClick.AddListener(onClick);

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(btnGO.transform, false);
            var labelText = labelGO.AddComponent<TextMeshProUGUI>();
            var displayFont = GameFont.GetDisplayTMP();
            if (displayFont != null) labelText.font = displayFont;
            labelText.text = label;
            labelText.fontSize = 72; // Spencer 2026-06-01 — bumped from 36; 72 ≈ 60% of the 120px button height for hero scale without clipping
            labelText.alignment = TextAlignmentOptions.Center;
            labelText.color = Color.white;
            var labelRT = labelGO.GetComponent<RectTransform>();
            labelRT.anchorMin = Vector2.zero;
            labelRT.anchorMax = Vector2.one;
            labelRT.offsetMin = Vector2.zero;
            labelRT.offsetMax = Vector2.zero;

            return btnGO;
        }

        public void Show(SurvivalManager survival)
        {
            _survival = survival;
            _homeLivesDone = null;
            LeaveFailedState();
            _adInFlight = false;

            // Dynamic cost from SurvivalManager (50 → 100 ladder).
            _currentCost = survival != null ? survival.CurrentContinueCost : 50;
            bool canAfford = CoinWallet.Balance >= _currentCost;

            // Refresh coin total in the top-left pill.
            if (_coinAmountText != null)
                _coinAmountText.text = $"● {CoinWallet.Balance}";

            // "So close!" — surface the remaining objective so the continue feels like one more push
            // at a goal in sight, not a generic "clear the top rows". 2026-06-15 Spencer.
            {
                var obj = ObjectiveManager.Instance != null ? ObjectiveManager.Instance.Active : null;

                // Rebuild the remaining-objective icon (chicken / vault / hidden-row / etc.) fresh each show.
                if (_objectiveIconGO != null) { Destroy(_objectiveIconGO); _objectiveIconGO = null; }

                bool showIcon = obj != null && !obj.IsComplete && obj.Icon != Objective.HudIcon.None;
                if (SurvivalManager.MoveLimitMode) showIcon = false;   // move-limit runs: the heart break plays here instead
                if (showIcon && _objectiveIconHolder != null)
                {
                    _objectiveIconGO = ObjectiveIconBuilder.Build(
                        obj.Icon, _objectiveIconHolder.transform, 76f, obj.RemainingCount, obj.IconWord);
                    if (_bodyText != null) _bodyText.text = "So close! You only have";
                }
                else if (_bodyText != null)
                {
                    _bodyText.text = (obj != null && !obj.IsComplete)
                        ? $"So close! You only have\n{obj.RemainingText}"
                        : "Clear the top rows\nand refill resources";
                }
                if (_objectiveIconHolder != null) _objectiveIconHolder.SetActive(showIcon);
                if (SurvivalManager.MoveLimitMode)
                {
                    // GOAL LEVELS (2026-10-30, RM study quick win #1): the card said "Only N points to go" on goal levels, where
                    // points mean nothing. Show the goal pictures still needed, with their counts (Royal Match's out-of-moves card).
                    // 10-30 Spencer: "id still like the heart break" -> both, side by side: [broken heart] [goals still needed]
                    if (SurvivalManager.FullBoardActive && LevelRules.Goals.Count > 0 && BuildGoalsLeft(out float goalsW))
                    {
                        if (_bodyText != null) _bodyText.text = CARD_V2 ? "You still need" : "So close! You still need";
                        if (!CARD_V2)
                        {
                            float heartW = HEART_SIZE * GOALS_HEART_SCALE, total = heartW + GOALS_HEART_GAP + goalsW;
                            ((RectTransform)_objectiveIconGO.transform).anchoredPosition = new Vector2(total * 0.5f - goalsW * 0.5f, 0f);
                            PlayHeartBreak(-total * 0.5f + heartW * 0.5f, GOALS_HEART_SCALE);
                        }
                    }
                    else
                    {
                        var sm = SurvivalManager.Instance;
                        int shortBy = sm != null ? Mathf.Max(0, sm.CurrentStageTarget - sm.CurrentStageScore) : 0;
                        if (_bodyText != null)
                            _bodyText.text = CARD_V2 ? (shortBy > 0 ? $"Only {shortBy:N0} points to go!" : "Out of moves!")
                                                     : (shortBy > 0 ? $"So close! Only {shortBy:N0} points to go" : "Out of moves!");
                        if (!CARD_V2) PlayHeartBreak();   // 11-16 v2: the "You will lose a life!" line carries the heart
                    }
                }
            }

            if (CARD_V2 && _bodyText != null)
            {   // one line, shrink to fit; centred in the box when there are no goal pictures under it
                _bodyText.textWrappingMode = TextWrappingModes.NoWrap; _bodyText.enableAutoSizing = true;
                _bodyText.fontSizeMax = CvFont(LevelIntroModal.V2_BODY_CAP * CV_BODY_K); _bodyText.fontSizeMin = CvFont(24f);
                bool pics = _objectiveIconHolder != null && _objectiveIconHolder.activeSelf && _objectiveIconGO != null;
                CvPlace(_bodyText.rectTransform, (CV_W - 2f * CV_SIDE) / 2f, pics ? 52f : CV_BOX_H / 2f, CV_W - 2f * CV_SIDE - 60f, 80f);
            }
            string playText = canAfford ? (CARD_V2 ? $"Play On{COIN_GAP}{_currentCost}" : $"PLAY ON{COIN_GAP}{_currentCost}")
                                        : (CARD_V2 ? $"Need{COIN_GAP}{_currentCost - CoinWallet.Balance} more" : $"NEED{COIN_GAP}{_currentCost - CoinWallet.Balance} MORE");
            if (_playArt != null) _playArt.Label = playText;   // 11-16 card v2: every layer of the art label
            else if (_continueLabel != null) _continueLabel.text = playText;
            if (_continueButton != null)
                _continueButton.interactable = canAfford;

            // Ad button always available (no cooldown per Codex). Disabled only
            // if AdManager is missing or an ad request is in-flight.
            bool adAvailable = AdManager.Instance != null;
            if (_adButton != null) _adButton.interactable = adAvailable;
            string adText = adAvailable ? (CARD_V2 ? COIN_GAP + "Watch Ad" : "WATCH AD") : (CARD_V2 ? "No ad right now" : "AD UNAVAILABLE");
            if (_adArt != null) _adArt.Label = adText; else if (_adLabel != null) _adLabel.text = adText;

            try
            {
                AnalyticsManager.Log("continue_offered",
                    "stage", survival != null ? survival.CurrentStageIndex : -1,
                    "continue_number", survival != null ? survival.ContinuesUsedThisRun + 1 : -1,
                    "cost", _currentCost,
                    "coin_balance", CoinWallet.Balance,
                    "can_afford", canAfford ? 1 : 0,
                    "ad_available", adAvailable ? 1 : 0);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            AnimateEntrance();
        }

        public static float GOALS_LEFT_ICON = 80f, GOALS_LEFT_GAP = 22f, GOALS_HEART_SCALE = 0.8f, GOALS_HEART_GAP = 34f;
        /// <summary>Row of the goals still unfinished (picture + count under it) in the objective-icon spot. False if every goal
        /// is already met (then the caller falls back to the plain out-of-moves text).</summary>
        private bool BuildGoalsLeft(out float width)
        {
            width = 0f;
            if (_objectiveIconHolder == null) return false;
            var left = new System.Collections.Generic.List<(LevelRules.LevelGoal g, int n)>();
            foreach (var g in LevelRules.Goals) { int n = Mathf.Max(0, g.Target - LevelRules.GoalProgress(g)); if (n > 0) left.Add((g, n)); }
            if (left.Count == 0) return false;
            if (_objectiveIconGO != null) Destroy(_objectiveIconGO);
            _objectiveIconGO = new GameObject("GoalsLeft", typeof(RectTransform));
            _objectiveIconGO.transform.SetParent(_objectiveIconHolder.transform, false);
            float step = GOALS_LEFT_ICON + GOALS_LEFT_GAP, x0 = -(left.Count - 1) * step * 0.5f;
            width = left.Count * GOALS_LEFT_ICON + (left.Count - 1) * GOALS_LEFT_GAP;
            for (int i = 0; i < left.Count; i++)
            {
                var cell = new GameObject("Goal_" + left[i].g.Type, typeof(RectTransform));
                cell.transform.SetParent(_objectiveIconGO.transform, false);
                ((RectTransform)cell.transform).anchoredPosition = new Vector2(x0 + i * step, 12f);
                var info = new LevelRules.GoalIconInfo { Type = left[i].g.Type, Count = 0, Letter = left[i].g.Letter, Theme = left[i].g.Theme };
                ObjectiveIconBuilder.BuildGoalIcon(info, cell.transform, GOALS_LEFT_ICON);
                var numGO = new GameObject("Count", typeof(RectTransform));
                numGO.transform.SetParent(cell.transform, false);
                var num = numGO.AddComponent<TextMeshProUGUI>();
                if (_bodyText != null) { num.font = _bodyText.font; num.color = _bodyText.color; }
                num.text = left[i].n.ToString(); num.fontSize = 40f; num.alignment = TextAlignmentOptions.Center; num.raycastTarget = false;
                var nrt = num.rectTransform; nrt.sizeDelta = new Vector2(step, 50f); nrt.anchoredPosition = new Vector2(0f, -GOALS_LEFT_ICON * 0.5f - 22f);
                if (CARD_V2)
                {   // 11-16: the count was the smallest text on the card (12px) - it's the number that matters. Cream with the title's edge.
                    num.fontSize = CvFont(HUDManager.GoalCountCap * GOALS_LEFT_ICON); HUDManager.ApplyRecipeCount(num, LevelIntroModal.V2_TITLE_EDGE_RK, 0.16f, -0.8f); RkText.Firm(num);   // 11-18: the HUD's size rule (was 52)
                    nrt.sizeDelta = new Vector2(step + 40f, 80f); nrt.anchoredPosition = new Vector2(GOALS_LEFT_ICON * 0.38f, -GOALS_LEFT_ICON * 0.38f);
                    num.alignment = TextAlignmentOptions.Center;
                }
            }
            _objectiveIconHolder.SetActive(true);
            return true;
        }

        /// <summary>Choreographed entrance — backdrop fades in first, then the
        /// panel drops from above with a damped bounce settle. Mirrors
        /// StageClearModal so both modals share the same "feel" identity.</summary>
        private void AnimateEntrance()
        {
            if (_canvas == null) return;
            _isPresenting = true;
            _isDismissing = false;

            _entranceSequence?.Kill();

            // Reset state: backdrop transparent, panel hidden (alpha 0). Panel
            // alpha is restored to 1 INSIDE the Phase 2 callback AFTER
            // DropInWithBounce has already snapped the panel offscreen — so it
            // never renders at rest position before the drop animation starts.
            if (_backdrop != null)
                _backdrop.color = new Color(BACKDROP_DIM.r, BACKDROP_DIM.g, BACKDROP_DIM.b, 0f);
            if (_panelCanvasGroup != null) _panelCanvasGroup.alpha = 0f;
            if (_panel != null)
            {
                _panel.transform.localScale = Vector3.one;
                // Restore the panel to its original rest anchoredPosition.
                // ExitUp left it ~1200 units higher; without this reset
                // DropInWithBounce treats THAT as the new rest and the panel
                // drifts further off-screen every Show.
                var prt = _panel.transform as RectTransform;
                if (prt != null) prt.anchoredPosition = _panelRestAnchoredPos;
            }

            // Hide ALL panel content pre-show so the children fade in
            // individually AFTER the panel lands (StageClearModal pattern).
            // Without this they ride the drop animation visible, defeating
            // the purpose of the staggered reveal.
            SetTextAlpha(_titleText, 0f);
            SetTextAlpha(_bodyText,  0f);
            if (_objectiveIconCG != null) _objectiveIconCG.alpha = 0f;
            if (_continueButtonCG != null) _continueButtonCG.alpha = 0f;
            if (_adButtonCG       != null) _adButtonCG.alpha       = 0f;
            if (_giveUpButtonCG   != null) _giveUpButtonCG.alpha   = 0f;
            if (_coinDisplayCG    != null) _coinDisplayCG.alpha    = 0f;

            _canvas.gameObject.SetActive(true);
            // Continue-modal entry sting per Spencer 2026-06-01. Distinct
            // from PlayUIClick (which read as "tap registered" rather than
            // "moment of arrival") and PlayEntry (reserved for StageClear).
            GameAudio.Instance?.PlayVictory1();

            // Lock the background the same way StageClearModal does so the
            // hint-manager tile-hops, rising-row timers, auto-drops, and any
            // other ambient gameplay animation freezes for the duration of
            // the modal. HintManager.GameplayActive() reads IsShowing (above)
            // and ResetIdleTimer-clears any in-flight hint sequences.
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(true);
            HintManager.Instance?.ClearVisuals();

            Sequence seq = DOTween.Sequence();

            // Phase 1 — backdrop fade-in (matches StageClearModal Phase 1).
            if (_backdrop != null)
                seq.Append(_backdrop.DOFade(BACKDROP_DIM.a, BACKDROP_FADE_DUR).SetEase(Ease.OutQuad));
            else
                seq.AppendInterval(BACKDROP_FADE_DUR);

            // Phase 2 — panel drops from above with damped bounce (StageClearModal Phase 2).
            if (_panel != null)
            {
                seq.AppendCallback(() =>
                {
                    if (_panel == null) return;
                    var rt = _panel.transform as RectTransform;
                    if (rt != null)
                    {
                        // ORDER MATTERS: DropInWithBounce snaps the panel
                        // offscreen first, THEN we make it visible. If we
                        // showed it before DropInWithBounce, the player would
                        // see it pop in at rest position before the drop.
                        UIAnimations.DropInWithBounce(rt, speedMult: DROP_SPEED);
                        if (_panelCanvasGroup != null) _panelCanvasGroup.alpha = 1f;

                        // Hide the title immediately on reveal so TossInTitle
                        // (Phase 3, after the panel lands) is what actually
                        // brings it onto screen. Otherwise the title rides
                        // the drop visible, which makes the toss-in feel like
                        // a redundant second reveal.
                        if (_titleText != null)
                        {
                            Color c = _titleText.color;
                            _titleText.color = new Color(c.r, c.g, c.b, 0f);
                        }
                    }
                });
                // Short settle beat so the panel reads as "landed" before
                // the children pop in. Kept tiny — Spencer wants the modal
                // available fast.
                seq.AppendInterval(UIAnimations.DROP_TOTAL_DUR / DROP_SPEED + SETTLE_BEAT);
            }

            // Phase 3 — snappy staggered reveal of panel children. Reveals
            // come quick and close together but still cascade (not all at
            // once) so the panel reads as "alive" rather than a screenshot.
            seq.AppendCallback(() => TossInTitle());
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInText(_bodyText, CHILD_FADE_DUR);
                if (_objectiveIconCG != null) _objectiveIconCG.DOFade(1f, CHILD_FADE_DUR);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInCanvasGroup(_continueButtonCG, CHILD_FADE_DUR);
                StartButtonPulse(_continueButtonGO);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() =>
            {
                FadeInCanvasGroup(_adButtonCG, CHILD_FADE_DUR);
                StartButtonPulse(_adButtonGO);
            });
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() => FadeInCanvasGroup(_giveUpButtonCG, CHILD_FADE_DUR));
            seq.AppendInterval(STAGGER);
            seq.AppendCallback(() => FadeInCanvasGroup(_coinDisplayCG, CHILD_FADE_DUR));

            _entranceSequence = seq;
        }

        private static void SetTextAlpha(TextMeshProUGUI t, float a)
        {
            if (t == null) return;
            Color c = t.color;
            t.color = new Color(c.r, c.g, c.b, a);
        }

        private static void FadeInText(TextMeshProUGUI t, float duration)
        {
            if (t == null) return;
            Color c = t.color;
            t.DOColor(new Color(c.r, c.g, c.b, 1f), duration).SetEase(Ease.OutQuad);
        }

        private static void FadeInCanvasGroup(CanvasGroup cg, float duration)
        {
            if (cg == null) return;
            cg.DOFade(1f, duration).SetEase(Ease.OutQuad);
        }

        /// <summary>Title scale + rotation + alpha toss-in. Mirrors
        /// StageClearModal.TossInTitle but adapted to TextMeshProUGUI
        /// (uses TMP's color tween for alpha — same DOColor API as UI Text).</summary>
        private void TossInTitle()
        {
            if (_titleText == null) return;
            Transform t = _titleText.transform;
            t.DOKill();

            // Pre-toss state: tiny, slight tilt so the rotation has somewhere
            // to swing from.
            t.localScale    = Vector3.one * 0.1f;
            t.localRotation = Quaternion.Euler(0f, 0f, 8f);
            Color c0 = _titleText.color;
            _titleText.color = new Color(c0.r, c0.g, c0.b, 0f);

            const float TOSS_DURATION = 0.28f;
            const float OVERSHOOT     = 3.0f;

            t.DOScale(1.0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            DG.Tweening.Core.DOGetter<float> getZ = () => _titleZRot;
            DG.Tweening.Core.DOSetter<float> setZ = (float z) =>
            {
                _titleZRot = z;
                t.localRotation = Quaternion.Euler(0f, 0f, z);
            };
            _titleZRot = 8f;
            DOTween.To(getZ, setZ, 0f, TOSS_DURATION)
                .SetEase(Ease.OutBack, OVERSHOOT);

            Color c = _titleText.color;
            _titleText.DOColor(new Color(c.r, c.g, c.b, 1f), 0.12f).SetEase(Ease.OutQuad);
        }

        public static bool BUTTON_PULSE = false;   // 11-16 Spencer: "dont make the buttons pulsate please"
        private static void StartButtonPulse(GameObject btnGO)
        {
            if (btnGO == null || !BUTTON_PULSE) return;
            Transform t = btnGO.transform;
            t.DOKill();
            t.localScale = Vector3.one;
            t.DOScale(1.07f, 0.7f)
                .SetEase(Ease.InOutSine)
                .SetLoops(-1, LoopType.Yoyo);
        }

        private static void StopButtonPulse(GameObject btnGO)
        {
            if (btnGO == null) return;
            btnGO.transform.DOKill();
            btnGO.transform.localScale = Vector3.one;
        }

        private static void PlayButtonPressSquash(GameObject btnGO)
        {
            if (btnGO == null) return;
            Transform t = btnGO.transform;
            t.DOKill();
            // Press-squash → pop → settle. Same numbers as
            // StageClearModal.PlayContinuePressSquash for a unified feel.
            Sequence press = DOTween.Sequence();
            press.Append(t.DOScale(0.92f, 0.06f).SetEase(Ease.OutQuad));
            press.Append(t.DOScale(1.06f, 0.10f).SetEase(Ease.OutBack, 4f));
            press.Append(t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad));
        }

        private void Hide()
        {
            if (_isDismissing) return;
            _isDismissing = true;

            _entranceSequence?.Kill();
            _entranceSequence = null;

            // Kill button pulses so the scale tween doesn't fight ExitUp.
            StopButtonPulse(_continueButtonGO);
            StopButtonPulse(_adButtonGO);

            if (_panel != null)
            {
                _panel.transform.DOKill();
                var rt = _panel.transform as RectTransform;
                if (rt != null)
                {
                    UIAnimations.ExitUp(rt, FinalizeHide, speedMult: 1.5f);
                }
                else
                {
                    FinalizeHide();
                }
            }
            else
            {
                FinalizeHide();
            }

            // Fade the backdrop out in parallel with the panel's exit so the
            // dim doesn't linger after the card has flown off-screen.
            if (_backdrop != null)
            {
                _backdrop.DOKill();
                _backdrop.DOFade(0f, BACKDROP_FADE_DUR).SetEase(Ease.InQuad);
            }
        }

        private void FinalizeHide()
        {
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _isPresenting = false;
            _isDismissing = false;

            // Resume background gameplay timers — rising row, auto-drop,
            // hint loop, etc. Paired with the SetOverlayPaused(true) in
            // AnimateEntrance.
            if (SurvivalManager.Instance != null)
                SurvivalManager.Instance.SetOverlayPaused(false);
            var after = _afterHide; _afterHide = null; after?.Invoke();
        }

        private void OnContinueClicked()
        {
            // 11-16 (play-badly monkey): a double tap on Play On bought TWO continues - 600 coins twice, "+5 moves -> 10 left".
            // The card is already leaving after the first tap; nothing on it works again until it is shown anew.
            if (_isDismissing) return;
            if (_survival == null || _adInFlight) { Hide(); return; }
            if (!CoinWallet.Spend(_currentCost))
            {
                Debug.LogWarning($"[ContinueModal] Continue tapped without sufficient coins for cost={_currentCost} — should have been gated.");
                return;
            }

            // Release half of the split multi-pop SFX (press half fired on
            // PointerDown). Press-squash dropped on the dismiss path —
            // ExitUp's transform tween starts immediately and the press
            // squash would fight it. Same compromise StageClearModal made.
            GameAudio.Instance?.PlayMultiPopRelease();
            Hide();
            _survival.ApplyContinueRescue();
            _survival.ResumeFromContinue();
        }

        private void OnAdClicked()
        {
            if (_isDismissing) return;
            if (_survival == null || _adInFlight) return;
            if (AdManager.Instance == null)
            {
                Debug.LogWarning("[ContinueModal] AdManager missing — ad continue path unavailable.");
                return;
            }

            // Release half of split multi-pop (press half from PointerDown).
            GameAudio.Instance?.PlayMultiPopRelease();
            _adInFlight = true;
            if (_adButton != null) _adButton.interactable = false;
            if (_continueButton != null) _continueButton.interactable = false;

            try
            {
                AnalyticsManager.Log("continue_ad_started",
                    "stage", _survival.CurrentStageIndex,
                    "continue_number", _survival.ContinuesUsedThisRun + 1);
            }
            catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

            // Same rescue path as paid continue. AdManager.ShowRewardedAd fires
            // the callback only on successful ad completion.
            AdManager.Instance.ShowRewardedAd(onRewardGranted: () =>
            {
                try
                {
                    AnalyticsManager.Log("continue_ad_completed",
                        "stage", _survival.CurrentStageIndex,
                        "continue_number", _survival.ContinuesUsedThisRun + 1);
                }
                catch (Exception ex) { Debug.LogError($"[ContinueModal] Analytics threw: {ex.Message}"); }

                Hide();
                _adInFlight = false;
                _survival.ApplyContinueRescue();
                _survival.ResumeFromContinue();
            });
        }

        private void OnGiveUpClicked()
        {
            if (_adInFlight || _isDismissing) return;
            if (CARD_V2 && _cardV2 != null)
            {
                GameAudio.Instance?.PlayUIClick();
                if (!_failedState) { ShowFailedState(); return; }
                if (FinishHomeLives(false)) return;
                HideThenRetry(false);                      // X on "Try Again?" -> home, same level
                return;
            }
            // GIVE UP is the de-emphasized button (no pulse, no press squash);
            // keep the plain UI click here so it reads as the lesser action.
            GameAudio.Instance?.PlayUIClick();
            Hide();
            if (_survival != null) _survival.DeclineContinue();
        }
    
        // ── HEART BREAK (2026-10-21 Spencer: "when you hit the Continue? screen ... animate the heart breaking") ──────────
        // Spencer's art: Resources/Hearts/heart_whole + heart_broken, plus the broken heart SPLIT ALONG ITS CRACK into
        // heart_broken_left/right (made offline from his PNG) so the halves can genuinely fall apart.
        private RectTransform _panelRTForHeart;
        private GameObject _heartRoot;
        private static Sprite LoadHeart(string name)
        {
            var tex = Resources.Load<Texture2D>("Hearts/" + name);
            return tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }
        private Image MakeHeartImage(Transform parent, string name, Sprite sp, float size)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            var img = go.GetComponent<Image>(); img.sprite = sp; img.preserveAspect = true; img.raycastTarget = false;
            return img;
        }
        public static float HEART_SIZE = 150f;
        private void PlayHeartBreak(float offsetX = 0f, float scale = 1f)
        {
            if (_panelRTForHeart == null) return;
            if (_heartRoot != null) Destroy(_heartRoot);
            Sprite whole = LoadHeart("heart_whole"), broken = LoadHeart("heart_broken"),
                   left = LoadHeart("heart_broken_left"), right = LoadHeart("heart_broken_right");
            if (whole == null) return;
            _heartRoot = new GameObject("HeartBreak", typeof(RectTransform));
            _heartRoot.transform.SetParent(_panelRTForHeart, false);
            var root = (RectTransform)_heartRoot.transform;
            root.anchorMin = root.anchorMax = new Vector2(0.5f, 0.625f); root.sizeDelta = Vector2.zero;
            root.anchoredPosition = new Vector2(offsetX, 0f); root.localScale = Vector3.one * scale;
            var wImg = MakeHeartImage(root, "Whole", whole, HEART_SIZE);
            var bImg = broken != null ? MakeHeartImage(root, "Broken", broken, HEART_SIZE) : null;
            var lImg = left   != null ? MakeHeartImage(root, "Left",   left,   HEART_SIZE) : null;
            var rImg = right  != null ? MakeHeartImage(root, "Right",  right,  HEART_SIZE) : null;
            if (bImg != null) bImg.gameObject.SetActive(false);
            if (lImg != null) lImg.gameObject.SetActive(false);
            if (rImg != null) rImg.gameObject.SetActive(false);

            var w = wImg.rectTransform;
            w.localScale = Vector3.zero;
            var seq = DOTween.Sequence().SetUpdate(true).SetTarget(_heartRoot);
            seq.AppendInterval(0.2f);
            seq.Append(w.DOScale(1.1f, 0.22f).SetEase(Ease.OutBack, 2.2f));            // pop in
            seq.Append(w.DOScale(1f, 0.1f));
            for (int i = 0; i < 2; i++)                                                  // two heartbeats
            {
                seq.Append(w.DOScale(1.12f, 0.11f).SetEase(Ease.OutQuad));
                seq.Append(w.DOScale(1f, 0.16f).SetEase(Ease.InQuad));
            }
            seq.AppendInterval(0.1f);
            seq.Append(w.DOShakeAnchorPos(0.32f, 9f, 28, 90f, false, true));            // strain
            seq.AppendCallback(() =>                                                    // CRACK
            {
                GameAudio.Instance?.PlayDeepImpact();
                HapticsManager.Strong();
                wImg.gameObject.SetActive(false);
                if (bImg != null) { bImg.gameObject.SetActive(true); bImg.rectTransform.localScale = Vector3.one * 1.12f; bImg.rectTransform.DOScale(1f, 0.14f).SetUpdate(true); }
            });
            seq.AppendInterval(0.32f);
            seq.AppendCallback(() =>                                                    // FALL APART
            {
                if (lImg == null || rImg == null) return;
                if (bImg != null) bImg.gameObject.SetActive(false);
                lImg.gameObject.SetActive(true); rImg.gameObject.SetActive(true);
                var l = lImg.rectTransform; var r = rImg.rectTransform;
                l.DOAnchorPos(new Vector2(-HEART_SIZE * 0.12f, -HEART_SIZE * 0.08f), 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                r.DOAnchorPos(new Vector2( HEART_SIZE * 0.12f, -HEART_SIZE * 0.08f), 0.5f).SetEase(Ease.OutCubic).SetUpdate(true);
                l.DOLocalRotate(new Vector3(0f, 0f,  13f), 0.5f).SetEase(Ease.OutBack, 1.6f).SetUpdate(true);
                r.DOLocalRotate(new Vector3(0f, 0f, -13f), 0.5f).SetEase(Ease.OutBack, 1.6f).SetUpdate(true);
            });
        }
}
}
