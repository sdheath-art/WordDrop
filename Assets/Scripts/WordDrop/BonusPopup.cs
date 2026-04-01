using System.Collections;
using UnityEngine;
using DG.Tweening;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Floating bonus popup — pops in, holds, floats up, fades out.
    /// Used to communicate every source of points to the player:
    /// word scores, detonation re-scores, heat bonuses, final push, comeback.
    /// </summary>
    public class BonusPopup : MonoBehaviour
    {
        public static BonusPopup Instance { get; private set; }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Show a score popup at a world position.</summary>
        public void Show(string text, Color color, Vector3 worldPos, float scale = 1f)
        {
            StartCoroutine(AnimatePopup(text, color, worldPos, scale));
        }

        /// <summary>Show a word score popup centered on tile positions.</summary>
        public void ShowWordScore(string word, int points, Vector3 worldPos)
        {
            Show($"{word} +{points}", Color.white, worldPos);
        }

        /// <summary>Show detonation re-score.</summary>
        public void ShowDetonation(string word, int points, Vector3 worldPos)
        {
            Show($"BOOM +{points}", Tile.PRIMED_GLOW, worldPos, 1.2f);
        }

        /// <summary>Show heat fuse bonus.</summary>
        public void ShowHeatBonus(int points, Vector3 worldPos)
        {
            if (points <= 0) return;
            Show($"HEAT +{points}", new Color(1f, 0.6f, 0.15f, 1f), worldPos + Vector3.up * 0.3f, 0.9f);
        }

        /// <summary>Show final push bonus.</summary>
        public void ShowFinalPush(int points, Vector3 worldPos)
        {
            if (points <= 0) return;
            Show($"FINAL PUSH +{points}", new Color(1f, 0.95f, 0.3f, 1f), worldPos + Vector3.up * 0.5f, 1.1f);
        }

        /// <summary>Show comeback bonus.</summary>
        public void ShowComeback(int points, Vector3 worldPos)
        {
            if (points <= 0) return;
            Show($"COMEBACK +{points}", new Color(0.3f, 0.9f, 0.4f, 1f), worldPos + Vector3.up * 0.5f, 1.1f);
        }

        /// <summary>Show rewrite refund.</summary>
        public void ShowRefund(Vector3 worldPos)
        {
            Show("CHARGE BACK!", new Color(0.3f, 0.85f, 0.9f, 1f), worldPos + Vector3.up * 0.3f, 0.9f);
        }

        // ── Animation ───────────────────────────────────────────────────────

        private IEnumerator AnimatePopup(string text, Color color, Vector3 worldPos, float scale)
        {
            // Create text object
            GameObject go = new GameObject("BonusPopup");
            go.transform.position = worldPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            var font = GameFont.GetTMP();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = 4f * scale;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 30;
            tmp.rectTransform.sizeDelta = new Vector2(8f, 2f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;

            // Add outline for readability on any background
            tmp.outlineWidth = 0.15f;
            tmp.outlineColor = new Color32(0, 0, 0, 180);

            Transform t = go.transform;

            // Phase 1: Pop in (scale from 0 → overshoot → settle)
            t.localScale = Vector3.zero;
            t.DOScale(Vector3.one * scale, 0.15f).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(0.15f);

            // Phase 2: Hold + gentle float up
            float holdDur = 0.5f;
            float elapsed = 0f;
            Vector3 startPos = t.position;
            while (elapsed < holdDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / holdDur;
                t.position = startPos + Vector3.up * (progress * 0.4f);
                yield return null;
            }

            // Phase 3: Fade out while continuing to float
            float fadeDur = 0.3f;
            elapsed = 0f;
            Color startColor = tmp.color;
            Color32 startOutline = tmp.outlineColor;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / fadeDur;
                t.position += Vector3.up * Time.deltaTime * 0.8f;

                float alpha = 1f - progress;
                tmp.color = new Color(startColor.r, startColor.g, startColor.b, alpha);
                tmp.outlineColor = new Color32(startOutline.r, startOutline.g, startOutline.b,
                    (byte)(startOutline.a * alpha));

                yield return null;
            }

            Destroy(go);
        }
    }
}
