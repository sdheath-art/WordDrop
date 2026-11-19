using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// 2026-11-19 LOADING SCREEN (Spencer: "when you load into the game right now you can see the board building before it goes
    /// to the homescreen. can we put a loading screen over this to disguise it?"). His art (owl + WORD tiles on the balcony)
    /// covers the whole screen from the first frame and stays until the home screen is actually up, then fades out.
    ///
    /// The picture is Resources/UI/loading_screen.bytes - a JPEG kept as raw bytes and decoded here, on purpose: a new PNG/JPG
    /// dropped into the project goes through Unity's default import (resized to a power of two, capped at 2048, recompressed),
    /// which is how new art has been silently squashed before. Bytes are shown exactly as saved (1472 x 2872).
    /// It fills the screen by COVER (no bars, sides trimmed on tall phones), blocks touches while it is up, and frees itself.
    /// </summary>
    public static class LoadingScreen
    {
        public static bool  ON = true;
        public static float MIN_SECONDS = 1.0f;     // never a one-frame flash of the art
        public static float SETTLE_SECONDS = 0.35f; // after the home screen is fully opaque, let its layout finish
        public static float FADE_SECONDS = 0.35f;
        public static float MAX_SECONDS = 10f;      // safety: never trap the player behind it
        public static string LABEL = "Loading...";
        public static float LABEL_CAP = 0.054f, LABEL_DOWN = 0.885f;   // capital height / screen width; centre, as a fraction of the way down
        public static bool  Showing { get; private set; }
        /// <summary>True from launch until the picture starts to fade: GameAudio remembers music requests instead of playing them.</summary>
        public static bool  HoldingMusic { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Boot()
        {
            if (!ON) return;
            var bytes = Resources.Load<TextAsset>("UI/loading_screen");
            if (bytes == null) return;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            if (!tex.LoadImage(bytes.bytes, true)) { Object.Destroy(tex); return; }
            tex.wrapMode = TextureWrapMode.Clamp;

            var go = new GameObject("LoadingScreen", typeof(RectTransform), typeof(Canvas), typeof(CanvasGroup), typeof(GraphicRaycaster));
            Object.DontDestroyOnLoad(go);
            var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32000;   // over everything
            var back = new GameObject("Back", typeof(RectTransform), typeof(Image)); back.transform.SetParent(go.transform, false);
            var brt = (RectTransform)back.transform; brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = brt.offsetMax = Vector2.zero;
            back.GetComponent<Image>().color = new Color32(0x4A, 0xB4, 0xF4, 0xFF);   // the picture's sky, in case anything shows at an edge
            var pic = new GameObject("Art", typeof(RectTransform), typeof(RawImage), typeof(AspectRatioFitter)); pic.transform.SetParent(go.transform, false);
            var prt = (RectTransform)pic.transform; prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one; prt.offsetMin = prt.offsetMax = Vector2.zero;
            var raw = pic.GetComponent<RawImage>(); raw.texture = tex; raw.raycastTarget = true;   // swallows touches while loading
            var fit = pic.GetComponent<AspectRatioFitter>(); fit.aspectMode = AspectRatioFitter.AspectMode.EnvelopeParent; fit.aspectRatio = (float)tex.width / tex.height;

            // "Loading..." (11-19, Spencer picked mock A in WordSlop Button: "wordslop button is good"; no name or logo yet). The card
            // titles' own treatment - cream face, deep purple edge, hard drop - through the same helper, so RkText gives it the
            // measured stroke / offset like every other label. Capital height 5.4% of the screen width, 88.5% of the way down.
            TextMeshProUGUI label = null;
            try
            {
                var lg = new GameObject("Loading", typeof(RectTransform), typeof(TextMeshProUGUI)); lg.transform.SetParent(go.transform, false);
                var lrt = (RectTransform)lg.transform; lrt.anchorMin = lrt.anchorMax = new Vector2(0.5f, 1f - LABEL_DOWN); lrt.pivot = new Vector2(0.5f, 0.5f);
                float sw = Screen.width > 0 ? Screen.width : 1179f; lrt.sizeDelta = new Vector2(sw, sw * 0.2f);
                label = lg.GetComponent<TextMeshProUGUI>(); label.text = LABEL; label.raycastTarget = false;
                label.alignment = TextAlignmentOptions.Center; label.textWrappingMode = TextWrappingModes.NoWrap; label.overflowMode = TextOverflowModes.Overflow;
                label.fontSize = sw * LABEL_CAP / 0.594f / HUDManager.RECIPE_MAX_MUL;   // WordSlop Button: capital = 0.594 em; the helper grows it by RECIPE_MAX_MUL
                HUDManager.ApplyRecipeCount(label, LevelIntroModal.V2_TITLE_EDGE_RK, LevelIntroModal.V2_TITLE_STROKE_RK, LevelIntroModal.V2_TITLE_DROP_RK, Resources.Load<TMP_FontAsset>("WordSlop Button SDF"));
            }
            catch (System.Exception e) { Debug.LogException(e); }   // the picture still does its job without the word

            Showing = true; HoldingMusic = true;
            go.AddComponent<Runner>().Begin(go.GetComponent<CanvasGroup>(), tex, label);
        }

        private class Runner : MonoBehaviour
        {
            public void Begin(CanvasGroup group, Texture2D tex, TextMeshProUGUI label) { StartCoroutine(Run(group, tex, label)); }
            private IEnumerator Run(CanvasGroup group, Texture2D tex, TextMeshProUGUI label)
            {
                // the label was made before RkText started listening (it boots after the scene loads): redraw it once so it is styled
                yield return null; yield return null; if (label != null) label.SetAllDirty();
                // 11-19 (Movie_025: "you can still see the board populating for a split second"): the log had this screen "up for
                // 5.00s" on FRAME 3 - the first frames of a launch are seconds long (one was 11 s in the editor), so real-time
                // timers ran out at once and it faded while the board was still being built. Time here is counted in capped
                // steps (a long frame counts as 1/30 s), and "ready" now means the home screen has FINISHED fading in - it
                // reports itself showing while still transparent - and has then stayed up for SETTLE_SECONDS.
                float t = 0f, readyFor = 0f, real = 0f;
                while (real < MAX_SECONDS + 30f)
                {
                    float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 30f); t += dt; real += Time.unscaledDeltaTime;
                    bool home = LevelMapPanel.Instance != null && LevelMapPanel.Instance.IsFullyShown;
                    readyFor = home ? readyFor + dt : 0f;
                    if (t >= MIN_SECONDS && readyFor >= SETTLE_SECONDS) break;
                    if (t >= MAX_SECONDS) break;
                    yield return null;
                }
                WDLog.Log($"[LoadingScreen] up for {t:F2}s ({(readyFor >= SETTLE_SECONDS ? "home screen ready" : "TIMED OUT - the home screen never finished showing")})");
                HoldingMusic = false; GameAudio.Instance?.ReleaseHeldMusic();   // the home song comes in as the picture fades
                float f = 0f;
                while (f < FADE_SECONDS) { f += Mathf.Min(Time.unscaledDeltaTime, 1f / 30f); group.alpha = 1f - Mathf.Clamp01(f / FADE_SECONDS); yield return null; }
                Showing = false;
                Destroy(gameObject); Destroy(tex);
            }
        }
    }
}
