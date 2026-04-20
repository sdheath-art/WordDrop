using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Balatro/RTT-style scoring presentation.
    ///
    /// When a word scores:
    ///   1. Tile cards spawn at scoring row spelling the word
    ///   2. Each tile pops in with OutBack overshoot, one at a time
    ///   3. As each tile counts, it does the RTT celebration shake:
    ///      squash → BIG springy pop → overshoot squash → settle with elastic
    ///   4. Jittery "+N" floats from each tile (RTT PointPopup style:
    ///      pop to 2.2x, random position jitter ±12px, rotation jitter ±30°)
    ///   5. Total punches in at the end
    ///   6. Cards slide off
    /// </summary>
    public class ScoringDisplay : MonoBehaviour
    {
        public static ScoringDisplay Instance { get; private set; }

        // ── Layout ──────────────────────────────────────────────────────────────
        private const float CARD_SPACING      = 1.00f;
        private const float CARD_POP_HEIGHT   = 0.30f;

        // ── Timing — snappy, 0.2-0.3s feel ──────────────────────────────────────
        private const float CARD_APPEAR_DUR   = 0.10f;
        private const float CARD_STAGGER      = 0.12f;
        private const float CELEBRATE_DUR     = 0.25f;
        private const float HOLD_AFTER        = 0.15f;
        private const float EXIT_DUR          = 0.12f;

        // ── Point popup — contained energy, not chaos ────────────────────────
        private const float POP_SPAWN_SCALE   = 0.5f;   // start bigger (less extreme pop)
        private const float POP_PEAK_SCALE    = 1.8f;   // pop to 180% — punchier
        private const float POP_SCALE_UP_DUR  = 0.05f;
        private const float POP_JITTER_AMOUNT = 0.02f;   // tight — barely moves off center
        private const float POP_JITTER_ROT    = 10f;     // visible wobble
        private const float POP_JITTER_DUR    = 0.15f;   // quick vibration
        private const float POP_SETTLE_SCALE  = 1.06f;
        private const float POP_SETTLE_DUR    = 0.04f;
        private const float POP_FADE_DUR      = 0.20f;

        // ── State ───────────────────────────────────────────────────────────────
        private List<GameObject> _activeObjects = new List<GameObject>();
        private Coroutine _activeSequence;

        private float CardSize
        {
            get
            {
                if (GridManager.Instance != null)
                    return GridManager.Instance.CellSize;
                return 0.90f;
            }
        }

        private float CardYBase
        {
            get
            {
                if (GridManager.Instance != null && Camera.main != null)
                {
                    // Must be below the HUD bar (ScreenSpaceOverlay renders on top of world-space).
                    // HUD bar = 82/960 of screen from top. Convert to world Y and add margin.
                    float camTop = Camera.main.transform.position.y + Camera.main.orthographicSize;
                    float screenH = Camera.main.orthographicSize * 2f;
                    float hudBottomWorld = camTop - screenH * (90f / 960f); // 90px with margin
                    float gridTop = GridManager.Instance.GridTop;
                    // Midpoint of the gap, but clamp so we never go above HUD bottom
                    float y = (hudBottomWorld + gridTop) / 2f;
                    return Mathf.Min(y, hudBottomWorld - CardSize * 0.5f);
                }
                return 6f;
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance == null)
            {
                var go = new GameObject("ScoringDisplay");
                go.AddComponent<ScoringDisplay>();
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═══════════════════════════════════════════════════════════════════════════

        private int _chainIndex = 0; // tracks which word in the chain we're on

        /// <summary>
        /// Show scoring for a word. First word in a chain gets tile-by-tile counting.
        /// Subsequent chain words show all tiles at once with just the total.
        /// Call ResetChain() at the start of each resolution.
        /// </summary>
        private struct QueuedWord
        {
            public string Word;
            public int Points;
            public bool IsPlayer;
        }
        private List<QueuedWord> _wordQueue = new List<QueuedWord>();
        private bool _isProcessingQueue = false;

        public void ShowWordScore(string word, int totalPoints, bool isPlayer)
        {
            // Scoring tally disabled — scores now fly to HUD via BonusPopup.
            // Keep method signature so callers compile.
            _chainIndex++;
        }

        private IEnumerator ProcessWordQueue()
        {
            _isProcessingQueue = true;

            while (_wordQueue.Count > 0)
            {
                var entry = _wordQueue[0];
                _wordQueue.RemoveAt(0);

                // Play the wave animation and wait for it to finish
                yield return StartCoroutine(QuickScoringSequence(entry.Word, entry.Points, entry.IsPlayer));
            }

            _isProcessingQueue = false;
        }

        /// <summary>Reset chain counter. Call at the start of each drop resolution.</summary>
        public void ResetChain()
        {
            _chainIndex = 0;
        }

        public static float GetDuration(int wordLength)
        {
            // Appear (0.10) + stagger (wordLength * 0.12) + celebrate (0.25)
            // + lower (0.25) + hold (0.45) + exit (0.20)
            // The step loop should move on once the word is readable — after celebrate.
            // The exit animation can overlap with the next phase.
            float appearTime = CARD_APPEAR_DUR + wordLength * CARD_STAGGER;
            return appearTime + CELEBRATE_DUR + 0.25f;
        }

        /// <summary>Same as GetDuration — all words use wave now.</summary>
        public static float GetQuickDuration()
        {
            return GetDuration(4); // estimate for average word length
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORING SEQUENCE
        // ═══════════════════════════════════════════════════════════════════════════

        // Rainbow glow colors for the jitter effect
        private static readonly Color[] GLOW_COLORS = {
            new Color(1f, 0.3f, 0.3f, 1f),   // red
            new Color(1f, 0.6f, 0.1f, 1f),   // orange
            new Color(1f, 1f, 0.2f, 1f),     // yellow
            new Color(0.3f, 1f, 0.4f, 1f),   // green
            new Color(0.2f, 0.8f, 1f, 1f),   // cyan
            new Color(0.5f, 0.3f, 1f, 1f),   // purple
            new Color(1f, 0.4f, 0.8f, 1f),   // pink
        };

        private const float RAISE_HEIGHT = 0.4f;   // how far tile raises (like hand select)
        private const float RAISE_DUR    = 0.08f;
        private const float LOWER_DUR    = 0.10f;

        private IEnumerator ScoringSequence(string word, int totalPoints, bool isPlayer)
        {
            Cleanup();

            // Pick a random RTT points clip for this whole sequence
            if (ScoringAudio.Instance != null)
                ScoringAudio.Instance.StartSequence();

            int len = word.Length;
            float tileSize = CardSize * 0.8f; // slightly smaller than board tiles
            float spacing = tileSize * 1.15f;
            float totalWidth = (len - 1) * spacing;
            float startX = -totalWidth / 2f;
            float yBase = CardYBase;

            Color scoreColor = isPlayer
                ? new Color(0.25f, 0.95f, 0.45f, 1f)
                : new Color(1.00f, 0.55f, 0.18f, 1f);

            // ── Phase 1: Spawn ALL tiles as BLANKS (letter hidden) ──
            List<GameObject> cards = new List<GameObject>();
            List<Vector3> targetScales = new List<Vector3>();
            List<TMPro.TextMeshPro> letterTMPs = new List<TMPro.TextMeshPro>();
            List<TMPro.TextMeshPro> ptsTMPs = new List<TMPro.TextMeshPro>();

            for (int i = 0; i < len; i++)
            {
                float x = startX + i * spacing;
                GameObject card = CreateScoringTile(word[i], x, yBase, tileSize);
                targetScales.Add(card.transform.localScale);
                card.transform.localScale = Vector3.zero;
                cards.Add(card);
                _activeObjects.Add(card);

                // Hide the letter and points — will reveal on flip
                var letterTMP = card.transform.Find("Letter")?.GetComponent<TMPro.TextMeshPro>();
                var ptsTMP = card.transform.Find("Pts")?.GetComponent<TMPro.TextMeshPro>();
                letterTMPs.Add(letterTMP);
                ptsTMPs.Add(ptsTMP);
                if (letterTMP != null) letterTMP.color = Color.clear;
                if (ptsTMP != null) ptsTMP.color = Color.clear;
            }

            // ── Shadow objects disabled — black blobs during board animations ──
            List<SpriteRenderer> shadows = new List<SpriteRenderer>();
            // Shadows removed: populate with nulls so downstream code doesn't break
            for (int i = 0; i < cards.Count; i++)
                shadows.Add(null);

            // ── Phase 1: Staggered pop-in of BLANK tiles ──
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.localScale = Vector3.zero;
                cards[i].transform.DOScale(targetScales[i], 0.25f)
                    .SetEase(Ease.OutBack)
                    .SetDelay(i * 0.04f);
            }
            yield return new WaitForSeconds(0.2f + cards.Count * 0.04f);

            float raiseY = tileSize * 0.15f;

            // ── Phase 2: ALL blank tiles raise together ──
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.DOMoveY(yBase + raiseY, 0.15f).SetEase(Ease.OutBack, 4f);
                if (i < shadows.Count && shadows[i] != null)
                {
                    shadows[i].color = new Color(0f, 0f, 0.02f, 0.22f);
                    StartCoroutine(AnimateScoredShadowLift(shadows[i], targetScales[i]));
                }
            }
            yield return new WaitForSeconds(0.18f);

            // ── Phase 3: Flip reveal each letter — exact RTT feel ──
            // RTT settings: flipDuration=0.1, scaleBounce=1.15, jiggle=3°, vibrato=12, thickness=3.5
            Color letterColor = new Color(0.10f, 0.10f, 0.12f, 1f);
            Color ptsColor = new Color(0.35f, 0.35f, 0.40f, 1f);

            // Punchier than RTT defaults
            float flipDur = 0.10f;
            float halfDur = flipDur * 0.5f;
            float scaleBounce = 1.25f;  // was 1.15 — bigger pop
            float thickness = 3.5f;
            float jiggleAngle = 10f;    // was 6 — more visible wobble
            int jiggleVibrato = 20;     // was 16 — more oscillations settling

            for (int i = 0; i < len; i++)
            {
                GameObject card = cards[i];
                if (card == null) continue;

                Transform t = card.transform;
                Vector3 fullScale = targetScales[i];
                int pts = LetterData.GetPoints(word[i]);

                // Always horizontal flip (side to side)
                bool flipHorizontal = true;
                float rotAngle = Random.Range(88f, 92f);
                float thisDur = halfDur * Random.Range(0.92f, 1.08f);
                float thisScaleBounce = scaleBounce * Random.Range(0.9f, 1.1f);

                t.DOKill();
                t.localRotation = Quaternion.identity;

                // Use coroutine for precise control — no competing sequences
                int idx = i; // capture for closure
                SpriteRenderer tileShadow = (idx < shadows.Count) ? shadows[idx] : null;
                yield return StartCoroutine(FlipRevealTile(t, fullScale, flipHorizontal, rotAngle, thisDur,
                    thisScaleBounce, thickness, jiggleAngle, jiggleVibrato,
                    idx < letterTMPs.Count ? letterTMPs[idx] : null,
                    idx < ptsTMPs.Count ? ptsTMPs[idx] : null,
                    letterColor, ptsColor, tileShadow));

                // Sound on flip
                if (ScoringAudio.Instance != null)
                    ScoringAudio.Instance.PlayTileCount(i);

                // Tick HUD score
                if (HUDManager.Instance != null)
                    HUDManager.Instance.TickScore(isPlayer, pts);

                // Brief gap before next letter
                yield return new WaitForSeconds(0.03f);
            }

            // ── Phase 4: ALL tiles lower together with bounce landing ──
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.DOMoveY(yBase, 0.12f).SetEase(Ease.OutQuad);
                if (i < shadows.Count && shadows[i] != null)
                    shadows[i].color = Color.clear;
            }
            yield return new WaitForSeconds(0.15f);

            yield return new WaitForSeconds(HOLD_AFTER);

            // ── Phase 5: Exit — staggered shrink down + fade ──
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.DOKill();
                cards[i].transform.localScale = targetScales[i]; // reset to clean base
                cards[i].transform.DOScale(Vector3.zero, 0.12f)
                    .SetEase(Ease.InQuart)
                    .SetDelay(i * 0.02f);
                SpriteRenderer sr = cards[i].GetComponent<SpriteRenderer>();
                if (sr != null)
                    sr.DOFade(0f, 0.10f).SetDelay(i * 0.02f);
            }

            yield return new WaitForSeconds(EXIT_DUR + 0.05f);
            Cleanup();
            _activeSequence = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // COLOR GLOW JITTER — wild rainbow border flash during celebration
        // ═══════════════════════════════════════════════════════════════════════════

        private IEnumerator ColorGlowJitter(GameObject card, float duration)
        {
            SpriteRenderer sr = card != null ? card.GetComponent<SpriteRenderer>() : null;
            if (sr == null) yield break;

            Color origColor = sr.color;
            float elapsed = 0f;
            int colorIdx = Random.Range(0, GLOW_COLORS.Length);
            float colorChangeRate = 0.09f; // cycle colors every 90ms (slower, less frantic)
            float nextChange = 0f;

            while (elapsed < duration && card != null)
            {
                elapsed += Time.deltaTime;
                float decay = 1f - (elapsed / duration); // fade intensity toward end

                if (elapsed >= nextChange)
                {
                    colorIdx = (colorIdx + 1) % GLOW_COLORS.Length; // cycle, don't random jump
                    nextChange = elapsed + colorChangeRate;
                }

                Color glow = GLOW_COLORS[colorIdx];
                sr.color = Color.Lerp(origColor, glow, 0.18f * decay); // subtle tint, fades out
                yield return null;
            }

            // Restore
            if (card != null && sr != null)
                sr.color = origColor;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // QUICK SCORING — chain words, all tiles at once, total only
        // ═══════════════════════════════════════════════════════════════════════════

        private IEnumerator QuickScoringSequence(string word, int totalPoints, bool isPlayer)
        {
            // Track this word's own tiles separately for cleanup
            var myObjects = new List<GameObject>();

            int len = word.Length;
            float tileSize = CardSize * 0.8f;
            float spacing = tileSize * 1.15f;
            float totalWidth = (len - 1) * spacing;
            float startX = -totalWidth / 2f;
            float yBase = CardYBase;

            // Spawn all tiles as blanks (letters hidden)
            List<GameObject> cards = new List<GameObject>();
            List<Vector3> targetScales = new List<Vector3>();
            List<TMPro.TextMeshPro> letterTMPs = new List<TMPro.TextMeshPro>();
            List<TMPro.TextMeshPro> ptsTMPs = new List<TMPro.TextMeshPro>();

            Color letterColor = new Color(0.10f, 0.10f, 0.12f, 1f);
            Color ptsColor = new Color(0.35f, 0.35f, 0.40f, 1f);

            for (int i = 0; i < len; i++)
            {
                float x = startX + i * spacing;
                GameObject card = CreateScoringTile(word[i], x, yBase, tileSize);
                targetScales.Add(card.transform.localScale);
                card.transform.localScale = Vector3.zero;
                cards.Add(card);
                myObjects.Add(card);

                var letterTMP = card.transform.Find("Letter")?.GetComponent<TMPro.TextMeshPro>();
                var ptsTMP = card.transform.Find("Pts")?.GetComponent<TMPro.TextMeshPro>();
                letterTMPs.Add(letterTMP);
                ptsTMPs.Add(ptsTMP);
                if (letterTMP != null) letterTMP.color = Color.clear;
                if (ptsTMP != null) ptsTMP.color = Color.clear;
                // Tint the tile grey — back of the card
                SpriteRenderer cardSR = card.GetComponent<SpriteRenderer>();
                if (cardSR != null) cardSR.color = new Color(0.65f, 0.65f, 0.68f, 1f);
            }

            // Pop in all blanks at once
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.localScale = targetScales[i];
                cards[i].transform.DOPunchScale(targetScales[i] * 0.15f, 0.2f, 2, 0.5f)
                    .SetEase(Ease.OutBack);
            }
            yield return new WaitForSeconds(0.10f);

            // ── Dealer wave flip — hand sweeping across, each tile flips in sequence ──
            float waveDelay = 0.05f; // slightly slower sweep
            float flipHalf = 0.06f;

            for (int i = 0; i < len; i++)
            {
                if (cards[i] == null) continue;
                int idx = i;
                StartCoroutine(WaveFlipTile(cards[i].transform, targetScales[i],
                    idx < letterTMPs.Count ? letterTMPs[idx] : null,
                    idx < ptsTMPs.Count ? ptsTMPs[idx] : null,
                    letterColor, ptsColor, flipHalf));
                yield return new WaitForSeconds(waveDelay);
            }

            // Wait for last tile's flip + jiggle to finish
            yield return new WaitForSeconds(flipHalf * 2f + 0.08f);

            // Tick HUD — all points at once
            if (HUDManager.Instance != null)
                HUDManager.Instance.TickScore(isPlayer, totalPoints);

            yield return new WaitForSeconds(0.2f); // hold for reading

            // Exit — shrink down + fade
            for (int i = 0; i < cards.Count; i++)
            {
                if (cards[i] == null) continue;
                cards[i].transform.DOKill();
                cards[i].transform.DOScale(Vector3.zero, 0.10f)
                    .SetEase(Ease.InQuart)
                    .SetDelay(i * 0.02f);
                SpriteRenderer sr = cards[i].GetComponent<SpriteRenderer>();
                if (sr != null)
                    sr.DOFade(0f, 0.08f).SetDelay(i * 0.02f);
            }

            yield return new WaitForSeconds(0.15f);
            // Only destroy this word's tiles, not all active objects
            foreach (var obj in myObjects)
                if (obj != null) { obj.transform.DOKill(); Destroy(obj); }
        }

        /// <summary>
        /// Dealer wave flip — tile goes flat then pops back up, revealing the letter.
        /// Like running your hand across a row of face-down cards.
        /// </summary>
        private IEnumerator WaveFlipTile(Transform t, Vector3 fullScale,
            TMPro.TextMeshPro letterTMP, TMPro.TextMeshPro ptsTMP,
            Color letterColor, Color ptsColor, float halfDur)
        {
            if (t == null) yield break;

            // Slam flat sideways (scale X to 0) — card flipping edge-on
            t.DOScaleX(0f, halfDur * 0.6f).SetEase(Ease.InQuad);
            yield return new WaitForSeconds(halfDur * 0.6f);

            // Reveal letter at flat point + restore tile to white
            if (letterTMP != null) letterTMP.color = letterColor;
            if (ptsTMP != null) ptsTMP.color = ptsColor;
            SpriteRenderer sr = t.GetComponent<SpriteRenderer>();
            if (sr != null) sr.color = Color.white;

            // Pop back open with overshoot — card flipping face-up
            t.DOScaleX(fullScale.x * 1.1f, halfDur * 0.4f).SetEase(Ease.OutBack, 4f);
            yield return new WaitForSeconds(halfDur * 0.4f);

            // Settle to normal
            t.DOScaleX(fullScale.x, 0.08f).SetEase(Ease.OutQuad);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // TILE JITTER — RTT-style frame-by-frame random shake with decay
        // The secret sauce: random position + rotation every frame, not tweens
        // ═══════════════════════════════════════════════════════════════════════════

        private IEnumerator TileJitter(Transform t, Vector3 fullScale, float tileSize, float duration,
            SpriteRenderer shadow = null)
        {
            if (t == null) yield break;

            Vector3 basePos = t.position;
            Vector3 shadowBasePos = shadow != null ? shadow.transform.position : Vector3.zero;
            float shadowDampen = 0.4f;

            // Spring physics parameters (Balatro style)
            float springFreq = 22f;    // oscillation speed (rad/s) — faster wobble
            float damping = 10f;       // settles quicker
            float initialRotVelocity = 500f; // stronger initial kick
            float initialPosVelocity = tileSize * 3f;

            float rotAngle = 0f;
            float rotVelocity = initialRotVelocity * (Random.value > 0.5f ? 1f : -1f);
            float posOffset = 0f;
            float posVelocity = initialPosVelocity * (Random.value > 0.5f ? 1f : -1f);

            float elapsed = 0f;
            while (elapsed < duration && t != null)
            {
                elapsed += Time.deltaTime;
                float dt = Time.deltaTime;

                // Damped spring: acceleration = -spring*x - damp*v
                float rotAccel = -springFreq * springFreq * rotAngle - 2f * damping * rotVelocity;
                rotVelocity += rotAccel * dt;
                rotAngle += rotVelocity * dt;

                float posAccel = -springFreq * springFreq * posOffset - 2f * damping * posVelocity;
                posVelocity += posAccel * dt;
                posOffset += posVelocity * dt;

                t.position = basePos + new Vector3(posOffset, 0f, 0f);
                t.localRotation = Quaternion.Euler(0f, 0f, rotAngle);

                // Shadow follows with dampened spring
                if (shadow != null)
                    shadow.transform.position = shadowBasePos + new Vector3(posOffset * shadowDampen, 0f, 0f);

                // Scale settles back from pop
                float scaleT = Mathf.Clamp01(elapsed / (duration * 0.5f));
                t.localScale = Vector3.Lerp(fullScale * 1.25f, fullScale, scaleT);

                yield return null;
            }

            // Clean reset
            if (t != null)
            {
                t.position = basePos;
                t.localRotation = Quaternion.identity;
                t.localScale = fullScale;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CELEBRATE TILE — for quick chain words (all tiles at once)
        // ═══════════════════════════════════════════════════════════════════════════

        private void CelebrateTile(Transform t, Vector3 origScale)
        {
            // Snap pop + jitter coroutine
            t.DOScale(origScale * 1.2f, 0.05f).SetEase(Ease.OutBack, 3f);
            StartCoroutine(TileJitter(t, origScale, CardSize * 0.8f, 0.15f));
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RTT-STYLE POINT POPUP — pop to 2.2x, jitter, settle beat, fade
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Balatro-style point text: starts tiny, pumps up with OutBack,
        /// holds for a beat, then floats up and fades out. Clean and punchy.
        /// </summary>
        /// <summary>
        /// Manual jiggle rotation — oscillates Z rotation back and forth with decay.
        /// More reliable than DOPunchRotation on world-space sprites.
        /// </summary>
        private IEnumerator JiggleRotation(Transform t, float maxAngle, int oscillations, float duration)
        {
            if (t == null) yield break;

            float elapsed = 0f;
            float frequency = oscillations / duration * Mathf.PI * 2f;

//             Debug.Log($"[ScoringDisplay] JiggleRotation START: angle={maxAngle} osc={oscillations} dur={duration}");

            while (elapsed < duration)
            {
                if (t == null) yield break;
                elapsed += Time.deltaTime;
                float decay = 1f - (elapsed / duration);
                float angle = Mathf.Sin(elapsed * frequency) * maxAngle * decay;
                t.localRotation = Quaternion.Euler(0f, 0f, angle);
                yield return null;
            }

            if (t != null)
                t.localRotation = Quaternion.identity;

//             Debug.Log("[ScoringDisplay] JiggleRotation END");
        }

        /// <summary>
        /// Balatro lesson #3: big scores must VISUALLY feel big. Flying score
        /// text font scales with log-magnitude so a 5000pt chain fills the
        /// screen while a 20pt safe word stays small. Prevents "AIR +8" and
        /// "AIR +4800" rendering identical (flagged 2026-04-18 playtest).
        /// </summary>
        public static float GetMagnitudeScale(int points)
        {
            if (points < 50)   return 1.00f;
            if (points < 200)  return 1.25f;
            if (points < 500)  return 1.55f;
            if (points < 1500) return 1.90f;
            if (points < 5000) return 2.30f;
            return 2.80f;
        }

        private void SpawnBalatroPointText(float x, float y, int points, Color color)
        {
            GameObject go = new GameObject("BalatroPoints");
            go.transform.position = new Vector3(x, y, -5f);
            go.transform.localScale = Vector3.zero; // start invisible

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = $"+{points}";
            tmp.fontSize = 10f * GetMagnitudeScale(points);
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.sortingOrder = 60;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 2f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            TMPHelper.ApplyEffects(tmp, color);

            _activeObjects.Add(go);

            // CLEAN PUMP — scales up smoothly, holds, drifts up and fades
            Sequence seq = DOTween.Sequence();

            // Scale up from 0 to full — smooth, no bounce
            seq.Append(go.transform.DOScale(1.0f, 0.15f).SetEase(Ease.OutQuad));

            // Hold at full size — the readable moment
            seq.AppendInterval(0.25f);

            // Drift up and fade — gentle exit
            seq.Append(go.transform.DOMoveY(y + 0.4f, 0.35f).SetEase(Ease.InQuad));
            seq.Join(DOTween.To(() => tmp.alpha, a => tmp.alpha = a, 0f, 0.35f));

            // Cleanup
            seq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        private void SpawnPointPopup(float x, float y, int points, Color color)
        {
            GameObject go = new GameObject("PointPopup");
            go.transform.position = new Vector3(x, y, -5f);
            go.transform.localScale = Vector3.one * POP_SPAWN_SCALE;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = $"+{points}";
            tmp.fontSize = 8f * GetMagnitudeScale(points);
            tmp.fontStyle = FontStyles.Bold;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color;
            tmp.sortingOrder = 60;
            tmp.rectTransform.sizeDelta = new Vector2(4f, 2f);
            tmp.textWrappingMode = TextWrappingModes.NoWrap;
            TMPHelper.ApplyEffects(tmp, color);

            _activeObjects.Add(go);
            StartCoroutine(PointPopupAnimation(go, tmp));
        }

        private IEnumerator PointPopupAnimation(GameObject go, TMP_Text tm)
        {
            Transform t = go.transform;
            Vector3 basePos = t.position;
            Color startColor = tm.color;

            // POP IN — scale from 0.3 to 2.2 with OutBack (strong overshoot)
            t.DOScale(POP_PEAK_SCALE, POP_SCALE_UP_DUR).SetEase(Ease.OutBack, 3f);
            yield return new WaitForSeconds(POP_SCALE_UP_DUR);

            // JITTER — random position + rotation every frame (RTT style)
            float jitterElapsed = 0f;
            while (jitterElapsed < POP_JITTER_DUR && go != null)
            {
                jitterElapsed += Time.deltaTime;
                float decay = 1f - (jitterElapsed / POP_JITTER_DUR);

                float ox = Random.Range(-POP_JITTER_AMOUNT, POP_JITTER_AMOUNT) * decay;
                float oy = Random.Range(-POP_JITTER_AMOUNT, POP_JITTER_AMOUNT) * decay;
                t.position = basePos + new Vector3(ox, oy, 0f);

                float rz = Random.Range(-POP_JITTER_ROT, POP_JITTER_ROT) * decay;
                t.rotation = Quaternion.Euler(0f, 0f, rz);

                yield return null;
            }

            if (go == null) yield break;

            // SETTLE to 1.0
            t.position = basePos;
            t.rotation = Quaternion.identity;
            t.DOScale(1f, POP_SETTLE_DUR).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(POP_SETTLE_DUR);

            if (go == null) yield break;

            // SETTLE BEAT — quick bounce to 1.1x then back
            t.DOScale(POP_SETTLE_SCALE, POP_SETTLE_DUR * 0.4f).SetEase(Ease.OutQuad);
            yield return new WaitForSeconds(POP_SETTLE_DUR * 0.4f);
            if (go != null) t.DOScale(1f, POP_SETTLE_DUR * 0.6f).SetEase(Ease.OutBack);
            yield return new WaitForSeconds(POP_SETTLE_DUR * 0.6f);

            // FADE OUT
            if (go == null) yield break;
            float fadeElapsed = 0f;
            while (fadeElapsed < POP_FADE_DUR && go != null && tm != null)
            {
                fadeElapsed += Time.deltaTime;
                float fade = 1f - (fadeElapsed / POP_FADE_DUR);
                Color c = startColor;
                c.a = fade;
                tm.color = c;

                // Drift up slightly during fade
                t.position += new Vector3(0f, Time.deltaTime * 0.5f, 0f);
                yield return null;
            }

            if (go != null) Destroy(go);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // TOTAL POPUP
        // ═══════════════════════════════════════════════════════════════════════════

        private GameObject SpawnTotalPopup(float x, float y, int total, Color color)
        {
            GameObject go = new GameObject("TotalScore");
            go.transform.position = new Vector3(x, y, -5f);
            go.transform.localScale = Vector3.zero;

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = $"+{total}";
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = 64;
            tm.characterSize = 0.09f;
            tm.fontStyle = FontStyle.Bold;
            tm.color = color;
            GameFont.Apply(tm);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 62;

            // Pop in with strong OutBack
            go.transform.DOScale(1f, 0.15f).SetEase(Ease.OutBack, 3f);

            // Settle beat
            DOVirtual.DelayedCall(0.15f, () =>
            {
                if (go != null)
                {
                    go.transform.DOScale(1.15f, 0.04f).SetEase(Ease.OutQuad).OnComplete(() =>
                    {
                        if (go != null)
                            go.transform.DOScale(1f, 0.06f).SetEase(Ease.OutBack);
                    });
                }
            });

            return go;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // TILE CREATION
        // ═══════════════════════════════════════════════════════════════════════════

        private static Sprite _cachedScoringTileSprite;

        private GameObject CreateScoringTile(char letter, float x, float y, float size = -1f)
        {
            if (size < 0f) size = CardSize;

            // Cache the sprite — same for all scoring tiles
            if (_cachedScoringTileSprite == null)
            {
                int texSize = 256;
                int radius  = texSize / 8;
                int border  = Mathf.Max(2, texSize / 18);
                _cachedScoringTileSprite = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                    new Color(0.95f, 0.95f, 0.97f, 1f),
                    new Color(0.55f, 0.55f, 0.60f, 1f),
                    border);
            }
            Sprite sprite = _cachedScoringTileSprite;

            GameObject go = new GameObject($"ScoreTile_{letter}");
            go.transform.position = new Vector3(x, y, -3f);

            SpriteRenderer sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.sortingOrder = 50;

            float nativeSize = 256f / 100f; // matches cached texSize
            float scale = size / nativeSize;
            go.transform.localScale = new Vector3(scale, scale, 1f);
            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // Letter — TMP, matches board tiles exactly
            GameObject textGO = new GameObject("Letter");
            textGO.transform.SetParent(go.transform, false);
            textGO.transform.localPosition = new Vector3(0f, nativeSize * 0.02f, -0.1f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) tm.font = tileFont;
            tm.text          = letter.ToString();
            tm.fontSize      = 5.5f;
            tm.fontStyle     = TMPro.FontStyles.Bold;
            tm.color         = new Color(0.10f, 0.10f, 0.12f, 1f);
            tm.alignment     = TMPro.TextAlignmentOptions.Center;
            tm.sortingOrder  = 51;
            tm.rectTransform.sizeDelta = new Vector2(2f, 2f);
            tm.enableWordWrapping = false;
            tm.overflowMode  = TMPro.TextOverflowModes.Overflow;
            textGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // Point value — TMP, matches board tiles
            GameObject ptsGO = new GameObject("Pts");
            ptsGO.transform.SetParent(go.transform, false);
            ptsGO.transform.localPosition = new Vector3(nativeSize * 0.28f, -nativeSize * 0.26f, -0.1f);

            var ptsTm = ptsGO.AddComponent<TMPro.TextMeshPro>();
            if (tileFont != null) ptsTm.font = tileFont;
            ptsTm.text          = LetterData.GetPoints(letter).ToString();
            ptsTm.fontSize      = 2.8f;
            ptsTm.fontStyle     = TMPro.FontStyles.Bold;
            ptsTm.color         = new Color(0.35f, 0.35f, 0.40f, 1f);
            ptsTm.alignment     = TMPro.TextAlignmentOptions.Center;
            ptsTm.sortingOrder  = 51;
            ptsTm.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
            ptsTm.enableWordWrapping = false;
            ptsTm.overflowMode  = TMPro.TextOverflowModes.Overflow;
            ptsGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            return go;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CLEANUP
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Cycles each letter's text color through a rainbow with a wave offset per letter.
        /// Runs continuously until stopped.
        /// </summary>
        private IEnumerator RainbowWaveCoroutine(List<TMPro.TextMeshPro> tmps)
        {
            if (tmps == null || tmps.Count == 0) yield break;

            float speed = 3f;       // how fast the rainbow cycles
            float waveOffset = 0.4f; // phase offset between adjacent letters

            float time = 0f;
            while (true)
            {
                time += Time.deltaTime * speed;

                for (int i = 0; i < tmps.Count; i++)
                {
                    if (tmps[i] == null) continue;

                    float hue = Mathf.Repeat(time + i * waveOffset, 1f);
                    Color rainbow = Color.HSVToRGB(hue, 0.7f, 0.95f);
                    tmps[i].color = rainbow;
                }

                yield return null;
            }
        }

        /// <summary>
        /// Exact RTT tile flip animation: rotate/scale to edge-on, reveal letter at midpoint,
        /// rotate/scale back with OutBack, then jiggle settle.
        /// </summary>
        private IEnumerator FlipRevealTile(Transform t, Vector3 fullScale, bool horizontal,
            float rotAngle, float halfDur, float scaleBounce, float thickness,
            float jiggleAng, int jiggleVib,
            TMPro.TextMeshPro letterTMP, TMPro.TextMeshPro ptsTMP,
            Color letterColor, Color ptsColor, SpriteRenderer shadow = null)
        {
            if (t == null) yield break;

            Vector3 shadowScale = shadow != null ? shadow.transform.localScale : Vector3.one;

            // ── Phase 1: Collapse to edge-on ──
            if (horizontal)
            {
                t.DOScaleX(0f, halfDur).SetEase(Ease.InOutQuad);
                t.DOScaleY(fullScale.y * scaleBounce, halfDur).SetEase(Ease.OutQuad);
                if (shadow != null)
                    shadow.transform.DOScaleX(0f, halfDur).SetEase(Ease.InOutQuad);
            }
            else
            {
                t.DOScaleY(0f, halfDur).SetEase(Ease.InOutQuad);
                t.DOScaleX(fullScale.x * thickness, halfDur).SetEase(Ease.OutQuad);
                if (shadow != null)
                    shadow.transform.DOScaleY(0f, halfDur).SetEase(Ease.InOutQuad);
            }

            yield return new WaitForSeconds(halfDur);

            // ── Midpoint: Reveal letter ──
            if (letterTMP != null) letterTMP.color = letterColor;
            if (ptsTMP != null) ptsTMP.color = ptsColor;

            // ── Phase 2: Expand back with OutBack ──
            if (horizontal)
            {
                t.DOScaleX(fullScale.x * 1.1f, halfDur * 0.6f).SetEase(Ease.OutBack, 3f);
                t.DOScaleY(fullScale.y, halfDur * 0.6f).SetEase(Ease.OutBack, 2f);
                if (shadow != null)
                    shadow.transform.DOScaleX(shadowScale.x * 1.1f, halfDur * 0.6f).SetEase(Ease.OutBack, 3f);
                yield return new WaitForSeconds(halfDur * 0.6f);

                t.DOScaleX(fullScale.x, halfDur * 0.4f).SetEase(Ease.OutQuad);
                if (shadow != null)
                    shadow.transform.DOScaleX(shadowScale.x, halfDur * 0.4f).SetEase(Ease.OutQuad);
            }
            else
            {
                t.DOScaleY(fullScale.y, halfDur).SetEase(Ease.OutBack);
                t.DOScaleX(fullScale.x, halfDur).SetEase(Ease.OutBack);
                if (shadow != null)
                    shadow.transform.DOScale(shadowScale, halfDur).SetEase(Ease.OutBack);
            }

            yield return new WaitForSeconds(halfDur);

            // ── Phase 3: Jiggle settle (RTT exact: PunchRotation Z, 6°, 0.2s, vibrato 16, elasticity 1) ──
            t.localScale = fullScale;
            float jiggleMult = Random.Range(0.7f, 1.3f);
            int thisVib = Mathf.Max(5, Mathf.RoundToInt(jiggleVib * Random.Range(0.7f, 1f)));

            t.DOPunchRotation(
                Vector3.forward * (jiggleAng * jiggleMult),
                0.35f,   // was 0.2 — longer wobble settle
                thisVib,
                1f
            ).SetEase(Ease.OutQuad);

            // Wait for jiggle to play out
            yield return new WaitForSeconds(0.3f);

            // Clean
            if (t != null)
            {
                t.localScale = fullScale;
                t.localRotation = Quaternion.identity;
            }
        }

        private IEnumerator AnimateScoredShadowLift(SpriteRenderer shadow, Vector3 fullScale)
        {
            if (shadow == null) yield break;

            // Same values as hand card shadow lift
            float duration = 0.10f;
            float elapsed = 0f;
            float targetScaleMult = 0.92f;
            float targetAlpha = 0.15f;

            while (elapsed < duration && shadow != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);

                float scaleMult = Mathf.Lerp(1f, targetScaleMult, eased);
                shadow.transform.localScale = fullScale * scaleMult;

                float alpha = Mathf.Lerp(0.22f, targetAlpha, eased);
                shadow.color = new Color(0f, 0f, 0.02f, alpha);

                yield return null;
            }
        }

        private IEnumerator DestroyAfterDelay(List<GameObject> objects, float delay)
        {
            yield return new WaitForSeconds(delay);
            foreach (var obj in objects)
                if (obj != null) Destroy(obj);
        }

        private void Cleanup()
        {
            foreach (var obj in _activeObjects)
                if (obj != null) { obj.transform.DOKill(); Destroy(obj); }
            _activeObjects.Clear();
        }
    }
}
