using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Royal-Match-style tutorial hand pointer (2026-06-09). A cartoon hand that demonstrates a
    /// drag (press → move → release → loop) or a tap-in-place, rendered above everything (incl.
    /// the tutorial spotlight dim). Driven by screen-space points, so callers pass the rack-tile
    /// and target-column screen positions. Art: Resources/Tiles/cursor_hand.
    ///
    /// Debug: in a running scene, press H to demo a drag, J to hide.
    /// </summary>
    public class TutorialHand : MonoBehaviour
    {
        public static TutorialHand Instance { get; private set; }

        // Where the "touch point" sits inside the sprite (normalized, 0,0 = bottom-left). The
        // cursor_hand points up, so the fingertip is near the top. We pin THIS point on the target
        // so the fingertip lands ON the tile and the hand hangs below it. Higher y → hand sits
        // lower (fingertip lower on the tile); lower y → hand rides higher. Tune to the art.
        public static Vector2 FingertipPivot = new Vector2(0.5f, 0.90f);

        // Look/feel tunables.
        private const float HAND_HEIGHT = 240f;  // px at the 1080×1920 reference resolution
        private const float PRESS_SCALE = 0.82f;
        private const float FADE_DUR    = 0.15f;
        private const float PRESS_DUR   = 0.12f;
        private const float DRAG_DUR    = 0.80f;
        private const float HOLD_DUR    = 0.45f;
        private const float GAP_DUR     = 0.25f;

        private const bool DEBUG_HAND_KEY = true;

        private RectTransform _canvasRT;
        private RectTransform _handRT;
        private RectTransform _debugDot;
        private CanvasGroup   _cg;
        private Image         _img;
        private Sequence      _seq;
        private static Sprite _handSprite;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
                new GameObject("TutorialHand").AddComponent<TutorialHand>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Build();
        }

        private void Build()
        {
            var canvasGO = new GameObject("TutorialHandCanvas", typeof(Canvas), typeof(CanvasScaler));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.GetComponent<Canvas>();
            canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000; // above the spotlight dim + HUD
            var scaler = canvasGO.GetComponent<CanvasScaler>();
            scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight  = 0.5f;
            _canvasRT = canvasGO.GetComponent<RectTransform>();

            var handGO = new GameObject("Hand", typeof(RectTransform), typeof(CanvasGroup), typeof(Image));
            handGO.transform.SetParent(canvasGO.transform, false);
            _handRT = handGO.GetComponent<RectTransform>();
            _handRT.pivot = FingertipPivot;
            _cg = handGO.GetComponent<CanvasGroup>();
            _cg.blocksRaycasts = false;
            _cg.interactable   = false;
            _cg.alpha          = 0f;
            _img = handGO.GetComponent<Image>();
            _img.raycastTarget  = false;
            _img.type           = Image.Type.Simple;
            _img.preserveAspect = true;   // render at true proportions — never stretch
            _img.sprite         = LoadHandSprite();

            // Square box; preserveAspect fits the hand inside it at its real aspect, so there's
            // no way to get a non-uniform stretch from an aspect mismatch.
            _handRT.sizeDelta = new Vector2(HAND_HEIGHT, HAND_HEIGHT);
            if (_img.sprite != null)
                Debug.Log($"[TutorialHand] hand_point sprite {_img.sprite.rect.width}x{_img.sprite.rect.height}");

            // Debug-only contact marker — a red dot at the exact point the fingertip should land,
            // so we can dial FingertipPivot precisely. Added after the hand → renders on top.
            var dotGO = new GameObject("DebugContactDot", typeof(RectTransform), typeof(Image));
            dotGO.transform.SetParent(canvasGO.transform, false);
            _debugDot = dotGO.GetComponent<RectTransform>();
            _debugDot.sizeDelta = new Vector2(30f, 30f);
            var dotImg = dotGO.GetComponent<Image>();
            dotImg.sprite        = TileRenderer.CreateSolidRoundedRect(36, 36, 18, new Color(1f, 0.15f, 0.2f, 0.95f));
            dotImg.raycastTarget = false;
            dotGO.SetActive(false);
        }

        private static Sprite LoadHandSprite()
        {
            if (_handSprite != null) return _handSprite;
            _handSprite = Resources.Load<Sprite>("Tiles/hand_point");
            if (_handSprite == null) // PNG imported as a plain Texture → build a Sprite
            {
                var tex = Resources.Load<Texture2D>("Tiles/hand_point");
                if (tex != null)
                    _handSprite = Sprite.Create(
                        tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return _handSprite;
        }

        /// <summary>Loop a press → drag → release demo from one screen point to another.</summary>
        public void ShowDrag(Vector2 fromScreen, Vector2 toScreen)
        {
            if (_handRT == null) return;
            KillSeq();
            Vector2 from = ToCanvas(fromScreen);
            Vector2 to   = ToCanvas(toScreen);

            _seq = DOTween.Sequence();
            _seq.AppendCallback(() => { _handRT.anchoredPosition = from; _handRT.localScale = Vector3.one; _cg.alpha = 0f; });
            _seq.Append(_cg.DOFade(1f, FADE_DUR));
            _seq.Append(_handRT.DOScale(PRESS_SCALE, PRESS_DUR));                  // press down
            _seq.Append(_handRT.DOAnchorPos(to, DRAG_DUR).SetEase(Ease.InOutSine)); // drag (held)
            _seq.Append(_handRT.DOScale(1f, PRESS_DUR));                            // release
            _seq.AppendInterval(HOLD_DUR);
            _seq.Append(_cg.DOFade(0f, FADE_DUR));                                  // fade out
            _seq.AppendInterval(GAP_DUR);
            _seq.SetLoops(-1, LoopType.Restart);
            _seq.SetUpdate(true); // keep looping even if the tutorial pauses gameplay time
        }

        /// <summary>Loop a tap-in-place demo at a screen point.</summary>
        public void ShowTap(Vector2 screenPos)
        {
            if (_handRT == null) return;
            KillSeq();
            Vector2 p = ToCanvas(screenPos);

            _seq = DOTween.Sequence();
            _seq.AppendCallback(() => { _handRT.anchoredPosition = p; _handRT.localScale = Vector3.one; _cg.alpha = 0f; });
            _seq.Append(_cg.DOFade(1f, FADE_DUR));
            _seq.Append(_handRT.DOScale(PRESS_SCALE, PRESS_DUR));
            _seq.Append(_handRT.DOScale(1f, PRESS_DUR));
            _seq.AppendInterval(HOLD_DUR);
            _seq.Append(_cg.DOFade(0f, FADE_DUR));
            _seq.AppendInterval(GAP_DUR);
            _seq.SetLoops(-1, LoopType.Restart);
            _seq.SetUpdate(true);
        }

        public void Hide()
        {
            KillSeq();
            if (_cg != null) _cg.alpha = 0f;
        }

        private void KillSeq()
        {
            if (_seq != null) { _seq.Kill(); _seq = null; }
            if (_handRT != null) _handRT.DOKill();
            if (_cg != null) _cg.DOKill();
        }

        private Vector2 ToCanvas(Vector2 screen)
        {
            RectTransformUtility.ScreenPointToLocalPointInRectangle(_canvasRT, screen, null, out Vector2 local);
            return local;
        }

        private void Update()
        {
            if (!DEBUG_HAND_KEY) return;
            if (Input.GetKeyDown(KeyCode.H))
            {
                Vector2 c = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
                ShowTap(c);                                  // resting hand — judge look + fingertip
                if (_debugDot != null) { _debugDot.gameObject.SetActive(true); _debugDot.anchoredPosition = ToCanvas(c); }
            }
            if (Input.GetKeyDown(KeyCode.G))
                ShowDrag(new Vector2(Screen.width * 0.5f, Screen.height * 0.30f),
                         new Vector2(Screen.width * 0.5f, Screen.height * 0.55f)); // drag demo
            if (Input.GetKeyDown(KeyCode.J))
            {
                Hide();
                if (_debugDot != null) _debugDot.gameObject.SetActive(false);
            }
            if (Input.GetKeyDown(KeyCode.O))   // toggle LENGTH seeding
            {
                DroughtAssist.OpportunitySeeding = !DroughtAssist.OpportunitySeeding;
                Debug.Log($"[Seeding] length {(DroughtAssist.OpportunitySeeding ? "ON" : "OFF")} | detonation {(DroughtAssist.DetonationSeeding ? "ON" : "OFF")}");
            }
            if (Input.GetKeyDown(KeyCode.P))   // toggle DETONATION seeding (the 'easy/flow' helper)
            {
                DroughtAssist.DetonationSeeding = !DroughtAssist.DetonationSeeding;
                Debug.Log($"[Seeding] length {(DroughtAssist.OpportunitySeeding ? "ON" : "OFF")} | detonation {(DroughtAssist.DetonationSeeding ? "ON" : "OFF")}");
            }
        }
    }
}
