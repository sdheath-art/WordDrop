using System.Collections;
using UnityEngine;
using TMPro;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Persistent display between HUD and board showing the last word played.
    /// Updates with a wave animation where each character pops in sequentially.
    /// Shows "YOU: DOG +5" or "AI: QUARTZ +18".
    /// </summary>
    public class LastWordDisplay : MonoBehaviour
    {
        public static LastWordDisplay Instance { get; private set; }

        private TextMeshPro _text;
        private Coroutine _waveCoroutine;
        private Coroutine _fadeCoroutine;

        // Styling
        private static readonly Color PLAYER_COLOR = new Color(1f, 1f, 1f, 1f); // clean white
        private static readonly Color AI_COLOR     = new Color(1.00f, 0.40f, 0.55f, 1f); // strong pink
        private const float FONT_SIZE = 7.5f;
        private const float WAVE_CHAR_DELAY = 0.04f; // delay between each character appearing
        private const float CHAR_POP_DUR = 0.15f;    // each character's pop-in duration

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Build();
        }

        private void Build()
        {
            var grid = GridManager.Instance;
            if (grid == null) return;

            // Position in the open space above the grid, below the HUD
            float halfH = Camera.main != null ? Camera.main.orthographicSize : 5f;
            float hudBottom = halfH * 0.85f; // just below the top reserve
            float y = (grid.GridTop + hudBottom) / 2f; // midpoint of empty zone

            GameObject go = new GameObject("LastWordText");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(0f, y, -5f);

            _text = go.AddComponent<TextMeshPro>();

            var cfg = UIConfig.Instance;
            TMP_FontAsset font = GameFont.GetDisplayTMP();
            if (font != null) _text.font = font;

            _text.text = "";
            _text.fontSize = Application.isMobilePlatform ? 5.5f : FONT_SIZE;
            _text.fontStyle = FontStyles.Normal;
            _text.color = new Color(1f, 1f, 1f, 0f); // start invisible
            _text.alignment = TextAlignmentOptions.Center;
            _text.sortingOrder = 25;
            _text.rectTransform.sizeDelta = new Vector2(10f, 2f);
            _text.enableWordWrapping = false;
            _text.overflowMode = TextOverflowModes.Overflow;

            // Outline applied per-word in ShowWord via TMPHelper (color-matched)
        }

        /// <summary>
        /// Show the last word played with a wave animation.
        /// </summary>
        public void ShowWord(string word, int score, bool isPlayer)
        {
            if (_text == null) return;

            // Reposition to current grid layout (grid changes between modes)
            var grid = GridManager.Instance;
            if (grid != null)
            {
                float halfH = Camera.main != null ? Camera.main.orthographicSize : 5f;
                float hudBottom = halfH * 0.85f;
                float y = (grid.GridTop + hudBottom) / 2f;
                _text.transform.position = new Vector3(0f, y, -5f);
            }

            GameAudio.Instance?.PlayWordPopup();

            string rivalName = RivalSystem.Instance != null ? RivalSystem.Instance.CurrentRival.Name : "AI";
            string fullText;
            if (SurvivalManager.IsSurvivalMode)
                fullText = $"{word} +{score}";  // solo — no prefix needed
            else
                fullText = $"{(isPlayer ? "YOU" : rivalName.ToUpper())}: {word} +{score}";
            Color color = isPlayer ? PLAYER_COLOR : AI_COLOR;

            // Apply color-matched outline + effects
            TMPHelper.ApplyEffects(_text, color, TMPHelper.TextTier.Popup);

            if (_waveCoroutine != null)
                StopCoroutine(_waveCoroutine);

            _waveCoroutine = StartCoroutine(PunchIn(fullText, color));

            // On mobile, auto-fade the overlay so it doesn't obscure the grid
            if (Application.isMobilePlatform)
            {
                if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = StartCoroutine(AutoFade(1.5f, 0.4f));
            }
        }

        private System.Collections.IEnumerator AutoFade(float showDur, float fadeDur)
        {
            yield return WaitCache.Get(showDur);
            float elapsed = 0f;
            Color startColor = _text.color;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float a = Mathf.Lerp(startColor.a, 0f, elapsed / fadeDur);
                _text.color = new Color(startColor.r, startColor.g, startColor.b, a);
                yield return null;
            }
            _text.color = new Color(startColor.r, startColor.g, startColor.b, 0f);
        }

        /// <summary>Clear the display (e.g., on match start).</summary>
        public void Clear()
        {
            if (_waveCoroutine != null)
            {
                StopCoroutine(_waveCoroutine);
                _waveCoroutine = null;
            }
            if (_text != null)
            {
                _text.text = "";
                _text.color = new Color(1f, 1f, 1f, 0f);
            }
        }

        private IEnumerator PunchIn(string fullText, Color color)
        {
            _text.text = fullText;
            _text.color = new Color(color.r, color.g, color.b, 0f);

            Transform t = _text.transform;
            t.DOKill();

            // Start big and transparent, punch down to normal with fade in
            t.localScale = Vector3.one * 1.5f;
            t.DOScale(1f, 0.3f).SetEase(Ease.OutBack, 2.5f);

            // Fade in
            float elapsed = 0f;
            float fadeDur = 0.15f;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float a = Mathf.Clamp01(elapsed / fadeDur);
                _text.color = new Color(color.r, color.g, color.b, a);
                yield return null;
            }
            _text.color = color;

            _waveCoroutine = null;
        }

        private void SetCharAlpha(TMP_TextInfo textInfo, int charIndex, byte alpha)
        {
            int matIndex = textInfo.characterInfo[charIndex].materialReferenceIndex;
            int vertIndex = textInfo.characterInfo[charIndex].vertexIndex;
            Color32[] colors = textInfo.meshInfo[matIndex].colors32;

            colors[vertIndex + 0].a = alpha;
            colors[vertIndex + 1].a = alpha;
            colors[vertIndex + 2].a = alpha;
            colors[vertIndex + 3].a = alpha;
        }

        private void SetCharScale(TMP_TextInfo textInfo, int charIndex, float scale)
        {
            int matIndex = textInfo.characterInfo[charIndex].materialReferenceIndex;
            int vertIndex = textInfo.characterInfo[charIndex].vertexIndex;
            Vector3[] verts = textInfo.meshInfo[matIndex].vertices;

            // Calculate center of the character
            Vector3 center = (verts[vertIndex] + verts[vertIndex + 1] +
                              verts[vertIndex + 2] + verts[vertIndex + 3]) / 4f;

            // Scale vertices around center
            // We need the original positions — store them on first call
            // For simplicity, recalculate from the mesh source
            _text.ForceMeshUpdate();
            TMP_TextInfo fresh = _text.textInfo;
            Vector3[] srcVerts = fresh.meshInfo[matIndex].vertices;

            Vector3 srcCenter = (srcVerts[vertIndex] + srcVerts[vertIndex + 1] +
                                  srcVerts[vertIndex + 2] + srcVerts[vertIndex + 3]) / 4f;

            for (int v = 0; v < 4; v++)
            {
                Vector3 offset = srcVerts[vertIndex + v] - srcCenter;
                verts[vertIndex + v] = srcCenter + offset * scale;
            }
        }
    }
}
