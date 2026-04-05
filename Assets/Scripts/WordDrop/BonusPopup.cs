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

        // ── Score Accumulator — merges overlapping scores into one popup ────

        private int _playerAccum = 0;
        private int _aiAccum = 0;
        private Vector3 _playerAccumPos;
        private Vector3 _aiAccumPos;
        private Coroutine _playerFlushCo;
        private Coroutine _aiFlushCo;
        private const float ACCUM_WINDOW = 0.15f; // merge scores within this window

        private void AccumulateScore(int points, Vector3 worldPos, bool isPlayer)
        {
            if (isPlayer)
            {
                _playerAccum += points;
                _playerAccumPos = (_playerAccum == points) ? worldPos : Vector3.Lerp(_playerAccumPos, worldPos, 0.5f);
                if (_playerFlushCo != null) StopCoroutine(_playerFlushCo);
                _playerFlushCo = StartCoroutine(FlushAccum(true));
            }
            else
            {
                _aiAccum += points;
                _aiAccumPos = (_aiAccum == points) ? worldPos : Vector3.Lerp(_aiAccumPos, worldPos, 0.5f);
                if (_aiFlushCo != null) StopCoroutine(_aiFlushCo);
                _aiFlushCo = StartCoroutine(FlushAccum(false));
            }
        }

        private IEnumerator FlushAccum(bool isPlayer)
        {
            yield return new WaitForSeconds(ACCUM_WINDOW);

            int total = isPlayer ? _playerAccum : _aiAccum;
            Vector3 pos = isPlayer ? _playerAccumPos : _aiAccumPos;

            if (isPlayer) { _playerAccum = 0; _playerFlushCo = null; }
            else { _aiAccum = 0; _aiFlushCo = null; }

            if (total <= 0) yield break;

            // Color and scale based on total points
            Color color;
            float fontSize;
            if (total >= 25)
            {
                color = new Color(1f, 0.3f, 0.15f, 1f); // hot red — massive
                fontSize = 9f;
            }
            else if (total >= 16)
            {
                color = new Color(1f, 0.65f, 0.1f, 1f); // orange — big
                fontSize = 8f;
            }
            else if (total >= 8)
            {
                color = new Color(1f, 0.85f, 0.2f, 1f); // gold — solid
                fontSize = 7f;
            }
            else
            {
                color = Color.white; // standard
                fontSize = 6f;
            }

            StartCoroutine(FlyScoreToHUD($"+{total}", color, pos, isPlayer, total, fontSize));
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Show a score popup at a world position.</summary>
        public void Show(string text, Color color, Vector3 worldPos, float scale = 1f)
        {
            StartCoroutine(AnimatePopup(text, color, worldPos, scale));
        }

        /// <summary>Show a word score popup — accumulated and merged.</summary>
        public void ShowWordScore(string word, int points, Vector3 worldPos, bool isPlayer = true)
        {
            AccumulateScore(points, worldPos, isPlayer);
        }

        /// <summary>Show detonation re-score — accumulated.</summary>
        public void ShowDetonation(string word, int points, Vector3 worldPos, int chainDepth = 0, bool isPlayer = true)
        {
            AccumulateScore(points, worldPos, isPlayer);
        }

        /// <summary>Show heat fuse bonus — accumulated.</summary>
        public void ShowHeatBonus(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (points <= 0) return;
            AccumulateScore(points, worldPos, isPlayer);
        }

        /// <summary>Show final push bonus — flies directly (special label).</summary>
        public void ShowFinalPush(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (points <= 0) return;
            Color c = new Color(1f, 0.95f, 0.3f, 1f);
            StartCoroutine(FlyScoreToHUD($"FINAL +{points}", c, worldPos + Vector3.up * 0.5f, isPlayer, points));
        }

        /// <summary>Show comeback bonus — flies directly (special label).</summary>
        public void ShowComeback(int points, Vector3 worldPos, bool isPlayer = true)
        {
            if (points <= 0) return;
            Color c = new Color(0.3f, 0.9f, 0.4f, 1f);
            StartCoroutine(FlyScoreToHUD($"COMEBACK +{points}", c, worldPos + Vector3.up * 0.5f, isPlayer, points));
        }

        /// <summary>Show rewrite refund.</summary>
        public void ShowRefund(Vector3 worldPos)
        {
            GameAudio.Instance?.PlayChargeBack();
            var cfg = UIConfig.Instance;
            Color c = cfg != null ? cfg.popupRefundColor : new Color(0.3f, 0.85f, 0.9f, 1f);
            float s = cfg != null ? cfg.popupRefundScale : 0.9f;
            Show("CHARGE BACK!", c, worldPos + Vector3.up * 0.3f, s);
        }

        // ── Flying Score Animation ──────────────────────────────────────────

        private IEnumerator FlyScoreToHUD(string text, Color color, Vector3 startPos, bool isPlayer, int points = 0, float fontSize = 6f)
        {
            GameAudio.Instance?.PlayBonusPopup();

            GameObject go = new GameObject("FlyingScore");
            go.transform.position = startPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            TMPro.TMP_FontAsset font = GameFont.GetTMP();
            if (font != null) tmp.font = font;
            tmp.text = text;
            tmp.fontSize = fontSize;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 100;
            tmp.rectTransform.sizeDelta = new Vector2(5f, 2f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;
            tmp.outlineWidth = 0.2f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);
            TMPHelper.ApplyEffects(tmp, color);

            Transform t = go.transform;

            // Pop in with overshoot
            t.localScale = Vector3.zero;
            t.DOScale(1.3f, 0.12f).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(0.12f);
            t.DOScale(1f, 0.08f).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(0.15f); // brief hold so player reads it

            // Get target position (HUD score counter)
            // HUD is Screen Space Overlay — position is in screen pixels. Convert to world.
            Camera cam = Camera.main;
            Vector3 targetWorld;
            if (cam != null && HUDManager.Instance != null)
            {
                Vector3 screenPos = isPlayer
                    ? HUDManager.Instance.GetPlayerScoreWorldPos()
                    : HUDManager.Instance.GetAIScoreWorldPos();
                // Overlay canvas position IS screen position — convert directly
                float camDist = Mathf.Abs(cam.transform.position.z);
                targetWorld = cam.ScreenToWorldPoint(new Vector3(screenPos.x, screenPos.y, camDist));
            }
            else
            {
                targetWorld = startPos + Vector3.up * 3f;
            }

            // Fly to HUD with arc + shimmer trail
            float flyDur = 0.4f;
            float elapsed = 0f;
            Vector3 flyStart = t.position;
            float arcHeight = 1.5f;
            float trailTimer = 0f;

            while (elapsed < flyDur && t != null)
            {
                elapsed += Time.deltaTime;
                float progress = Mathf.Clamp01(elapsed / flyDur);
                float eased = 1f - (1f - progress) * (1f - progress); // ease out quad

                // Parabolic arc
                Vector3 pos = Vector3.Lerp(flyStart, targetWorld, eased);
                float arc = arcHeight * 4f * progress * (1f - progress);
                pos.y += arc;
                t.position = pos;

                // Shrink as it approaches
                float scale = Mathf.Lerp(1f, 0.4f, eased);
                t.localScale = Vector3.one * scale;

                // Fade slightly
                float alpha = Mathf.Lerp(1f, 0.6f, eased);
                tmp.color = new Color(color.r, color.g, color.b, alpha);

                // Gold shimmer trail — emit particles along the arc
                trailTimer += Time.deltaTime;
                if (trailTimer >= 0.03f)
                {
                    trailTimer = 0f;
                    GameParticles.Instance?.PlayShimmer(pos, 2);
                }

                yield return null;
            }

            // Arrived — punch the HUD counter, tick up score, play impact
            GameAudio.Instance?.PlayScoreImpact(points);
            if (isPlayer)
                HUDManager.Instance?.PunchPlayerScore(points);
            else
                HUDManager.Instance?.PunchAIScore(points);

            Destroy(go);
        }

        // ── Standard Popup Animation ────────────────────────────────────────

        private IEnumerator AnimatePopup(string text, Color color, Vector3 worldPos, float scale)
        {
            GameAudio.Instance?.PlayBonusPopup();
            // Create text object
            GameObject go = new GameObject("BonusPopup");
            go.transform.position = worldPos;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            var cfg = UIConfig.Instance;

            // Font — use config font if set, otherwise fall back to GameFont
            TMPro.TMP_FontAsset popupFont = (cfg != null && cfg.popupFont != null) ? cfg.popupFont : GameFont.GetTMP();
            if (popupFont != null) tmp.font = popupFont;

            tmp.text = text;
            float baseFontSize = cfg != null ? cfg.popupBaseFontSize : 5f;
            tmp.fontSize = baseFontSize * scale;
            tmp.fontStyle = FontStyles.Bold;
            tmp.color = color;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.sortingOrder = 30;
            tmp.rectTransform.sizeDelta = new Vector2(8f, 2f);
            tmp.enableWordWrapping = false;
            tmp.overflowMode = TextOverflowModes.Overflow;

            // Outline + drop shadow — configurable
            tmp.outlineWidth = cfg != null ? cfg.popupOutlineWidth : 0.2f;
            Color32 outlineCol = cfg != null ? (Color32)cfg.popupOutlineColor : new Color32(0, 0, 0, 220);
            tmp.outlineColor = outlineCol;
            TMPHelper.ApplyEffects(tmp, color);

            Transform t = go.transform;

            // Phase 1: Pop in (scale from 0 → overshoot → settle)
            float popInDur = cfg != null ? cfg.popupPopInDuration : 0.12f;
            t.localScale = Vector3.zero;
            t.DOScale(Vector3.one * scale, popInDur).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(popInDur);

            // Phase 2: Hold + gentle float up (research: 800-1000ms total visibility)
            float holdDur = cfg != null ? cfg.popupHoldDuration : 0.5f;
            float elapsed = 0f;
            Vector3 startPos = t.position;
            while (elapsed < holdDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / holdDur;
                float floatDist = cfg != null ? cfg.popupFloatDistance : 0.4f;
                t.position = startPos + Vector3.up * (progress * floatDist);
                yield return null;
            }

            // Phase 3: Fade out while continuing to float
            float fadeDur = cfg != null ? cfg.popupFadeDuration : 0.3f;
            elapsed = 0f;
            Color startColor = tmp.color;
            Color32 startOutline = tmp.outlineColor;
            while (elapsed < fadeDur)
            {
                elapsed += Time.deltaTime;
                float progress = elapsed / fadeDur;
                float floatSpd = cfg != null ? cfg.popupFloatSpeed : 0.8f;
                t.position += Vector3.up * Time.deltaTime * floatSpd;

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
