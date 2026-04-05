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

        // Styling
        private static readonly Color PLAYER_COLOR = new Color(1.00f, 0.84f, 0.42f, 0.85f); // gold
        private static readonly Color AI_COLOR     = new Color(1.00f, 0.56f, 0.67f, 0.85f); // pink
        private const float FONT_SIZE = 6.5f;
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

            // Position above board with breathing room from column arrows
            float y = grid.GridTop + grid.CellSize * 0.9f;

            GameObject go = new GameObject("LastWordText");
            go.transform.SetParent(transform, false);
            go.transform.position = new Vector3(0f, y, -5f);

            _text = go.AddComponent<TextMeshPro>();

            var cfg = UIConfig.Instance;
            TMP_FontAsset font = GameFont.GetUITMP();
            if (font != null) _text.font = font;

            _text.text = "";
            _text.fontSize = FONT_SIZE;
            _text.fontStyle = FontStyles.Bold;
            _text.color = new Color(1f, 1f, 1f, 0f); // start invisible
            _text.alignment = TextAlignmentOptions.Center;
            _text.sortingOrder = 25;
            _text.rectTransform.sizeDelta = new Vector2(10f, 2f);
            _text.enableWordWrapping = false;
            _text.overflowMode = TextOverflowModes.Overflow;

            // Outline for readability
            _text.outlineWidth = 0.15f;
            _text.outlineColor = new Color32(0, 0, 0, 150);
        }

        /// <summary>
        /// Show the last word played with a wave animation.
        /// </summary>
        public void ShowWord(string word, int score, bool isPlayer)
        {
            if (_text == null) return;
            GameAudio.Instance?.PlayWordPopup();

            string prefix = isPlayer ? "YOU" : "AI";
            string fullText = $"{prefix}: {word} +{score}";
            Color color = isPlayer ? PLAYER_COLOR : AI_COLOR;

            if (_waveCoroutine != null)
                StopCoroutine(_waveCoroutine);

            _waveCoroutine = StartCoroutine(PunchIn(fullText, color));
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
