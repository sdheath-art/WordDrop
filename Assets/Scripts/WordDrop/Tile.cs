using System.Collections;
using UnityEngine;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Represents a single letter tile on the board.
    /// Owns its SpriteRenderer and TextMesh children.
    ///
    /// Job 6 changes:
    ///   - Removed wild tile pulse animation and IsWild/SetLetter wild-resolve logic
    ///   - Removed SetColorState / TileColorState Wordle coloring
    ///   - Removed TILE_BORDER_WILD constant
    ///   - Added SetPrimedGlow(Color c) and ClearPrimedGlow() for the priming system
    ///   - SetPermanentGlow repurposed as the backing for primed glow
    ///   - Kept: AnimateFall, AnimateGravityFall, UpdateGridPosition, Highlight
    ///
    /// Job 11 additions:
    ///   - FlashWhite(float duration) public coroutine: scale pulse + white border flash
    ///
    /// Gravity:
    ///   UpdateGridPosition(col, row) — updates Col/Row for correct positions.
    ///   AnimateGravityFall(target, duration) — smooth fall, no bounce.
    ///   Primed glow is preserved through gravity falls.
    /// </summary>
    public class Tile : MonoBehaviour
    {
        // ---------------------------------------------------------------------------
        // Visual constants
        // ---------------------------------------------------------------------------

        private static readonly Color TILE_FILL_COLOR    = new Color(0.973f, 0.961f, 0.937f, 1f);     // warm cream #F8F5EF
        private static readonly Color TILE_BORDER_NORMAL = new Color(0.800f, 0.745f, 0.640f, 1f);  // desaturated warm tan
        private static readonly Color TILE_BORDER_GOLD   = new Color(1.000f, 0.720f, 0.180f, 1f);  // bright rich gold-orange #FFB82E

        // Kept as compile stub for any callers that reference it
        public static readonly Color TILE_BORDER_WILD = new Color(0.20f, 0.90f, 1.00f, 1f);

        public static readonly Color PRIMED_GLOW  = new Color(1.000f, 0.720f, 0.180f, 1f);  // bright rich gold-orange #FFB82E
        public static readonly Color PLAYER_GLOW = PRIMED_GLOW;
        public static readonly Color AI_GLOW     = PRIMED_GLOW;

        private const float FALL_DURATION     = 0.12f;   // snappy drop
        private const float BOUNCE_OVERSHOOT  = 0.12f;  // visible overshoot
        private const float BOUNCE_SETTLE_DUR = 0.06f;  // quick snap back

        // ---------------------------------------------------------------------------
        // Runtime state
        // ---------------------------------------------------------------------------

        public char  Letter      { get; private set; }
        public int   Col         { get; private set; }
        public int   Row         { get; private set; }
        public bool  IsAnimating { get; private set; }

        // Stub properties kept for compile compatibility
        public bool IsWild => false;
        public TileColorState ColorState { get; private set; } = TileColorState.None;

        private SpriteRenderer _spriteRenderer;
        private TextMeshPro    _letterTMP;
        private TextMeshPro    _pointTMP;
        private AudioSource    _audioSource;
        private float          _cellSize;

        private bool  _isHighlighted    = false;
        private bool  _hasPrimedGlow    = false;
        private Color _primedGlowColor  = new Color(0.812f, 0.812f, 0.863f, 1f);
        private Color _currentBorderColor = new Color(0.812f, 0.812f, 0.863f, 1f);

        private Coroutine _gravityCoroutine;
        private Coroutine _fallCoroutine;
        private Coroutine _flashCoroutine;
        private Coroutine _dissolveCoroutine;

        // Dissolve shader
        private static Material s_dissolveMaterial;
        private Material _dissolveMatInstance;
        public bool IsDissolving { get; private set; }

        // Fake 3D system (baked atlas sprite + shader)
        private static Material s_fake3DMaterial;
        private Material _fake3DMatInstance;
        private bool _hasFake3D;
        private Coroutine _rotateCoroutine;
        private SpriteRenderer _bakedRenderer;
        private Vector3 _fake3DBaseScale;

        // ---------------------------------------------------------------------------
        // Public read-only glow properties (backward compat with HandManager)
        // ---------------------------------------------------------------------------

        public bool  HasPermanentGlow => _hasPrimedGlow;
        public Color GlowColor        => _hasPrimedGlow ? _primedGlowColor : Color.clear;

        // ---------------------------------------------------------------------------
        // Initialisation
        // ---------------------------------------------------------------------------

        public void Initialise(char letter, int col, int row, float cellSize)
        {
            Letter    = letter;
            Col       = col;
            Row       = row;
            _cellSize = cellSize;

            BuildSpriteCache();
            BuildVisuals(cellSize);
            SetupAudio();
            UpdateLetterDisplay(letter);
        }

        private void BuildVisuals(float cellSize)
        {
            int texSize = Mathf.Clamp(Mathf.RoundToInt(cellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // chunkier rounded corners
            int border  = Mathf.Max(3, texSize / 12);  // thicker edge for tactile depth

            _currentBorderColor = TILE_BORDER_NORMAL;

            _spriteRenderer              = gameObject.AddComponent<SpriteRenderer>();
            _spriteRenderer.sprite       = s_spriteNormal;  // use cached sprite
            _spriteRenderer.sortingOrder = 5;

            float displaySize = cellSize * 0.88f;
            float nativeSize  = texSize / 100f;
            float scale       = displaySize / nativeSize;
            transform.localScale = new Vector3(scale, scale, 1f);

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // ── Main letter — TextMeshPro with subtle grounding shadow ──
            GameObject letterGO = new GameObject("TileLetter");
            letterGO.transform.SetParent(transform, false);
            letterGO.transform.localPosition = new Vector3(0f, nativeSize * 0.02f, -0.1f);

            _letterTMP = letterGO.AddComponent<TextMeshPro>();
            // Load Fredoka Bold TMP font
            TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) _letterTMP.font = tileFont;
            _letterTMP.text           = "";
            _letterTMP.fontSize       = 5.5f;
            _letterTMP.fontStyle      = FontStyles.Bold;
            _letterTMP.color          = new Color(0.145f, 0.153f, 0.200f, 1f); // #252733
            _letterTMP.alignment      = TextAlignmentOptions.Center;
            _letterTMP.sortingOrder   = 6;
            _letterTMP.rectTransform.sizeDelta = new Vector2(2f, 2f);
            _letterTMP.enableWordWrapping = false;
            _letterTMP.overflowMode  = TextOverflowModes.Overflow;

            // No effects — clean flat text

            letterGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // ── Point value — quieter, no shadow ──
            GameObject pointGO = new GameObject("TilePoints");
            pointGO.transform.SetParent(transform, false);
            pointGO.transform.localPosition = new Vector3(nativeSize * 0.28f, -nativeSize * 0.26f, -0.1f);

            _pointTMP = pointGO.AddComponent<TextMeshPro>();
            if (tileFont != null) _pointTMP.font = tileFont;
            _pointTMP.text           = "";
            _pointTMP.fontSize       = 2.8f;
            _pointTMP.fontStyle      = FontStyles.Normal;  // Fredoka Bold already bold
            _pointTMP.color          = new Color(0.40f, 0.40f, 0.50f, 1f);
            _pointTMP.alignment      = TextAlignmentOptions.Center;
            _pointTMP.sortingOrder   = 6;
            _pointTMP.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
            _pointTMP.enableWordWrapping = false;
            _pointTMP.overflowMode  = TextOverflowModes.Overflow;
            // No shadow on point values — keep them quiet

            pointGO.transform.localScale = new Vector3(invScale, invScale, 1f);

        }

        private void SetupAudio()
        {
            _audioSource              = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake  = false;
            _audioSource.volume       = 0.6f;
            _audioSource.spatialBlend = 0f;
        }

        // ---------------------------------------------------------------------------
        // Letter display (simplified — no wild resolve)
        // ---------------------------------------------------------------------------

        private void UpdateLetterDisplay(char letter)
        {
            if (_letterTMP != null)
                _letterTMP.text = letter.ToString().ToUpper();

            if (_pointTMP != null)
            {
                int pts = LetterData.GetPoints(letter);
                _pointTMP.text = pts > 0 ? pts.ToString() : "";
            }
        }

        // ---------------------------------------------------------------------------
        // Public API — SetLetter (simplified stub, no wild pulse)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Updates the displayed letter. Simplified version — no wild resolve animation.
        /// Kept for compile compatibility with HandManager and AIAgent.
        /// </summary>
        public void SetLetter(char letter)
        {
            Letter = letter;
            UpdateLetterDisplay(letter);

            // Update border if no primed glow active
            if (!_hasPrimedGlow && !_isHighlighted)
            {
                _currentBorderColor = TILE_BORDER_NORMAL;
                ApplyBorderColor(TILE_BORDER_NORMAL);
            }
        }

        // ---------------------------------------------------------------------------
        // Public API — grid position update (gravity)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Updates the tile's Col/Row tracking after gravity repositioning.
        /// Does NOT move the GameObject — AnimateGravityFall handles that.
        /// </summary>
        public void UpdateGridPosition(int col, int row)
        {
            Col = col;
            Row = row;
        }

        // OnDestroy not needed — no persistent shadow objects

        /// <summary>
        /// Animates this tile falling to targetWorldPos over the given duration.
        /// Used for gravity drops — smooth linear fall, no bounce.
        /// Preserves primed glow throughout the animation.
        /// </summary>
        public void AnimateGravityFall(Vector3 targetWorldPos, float duration)
        {
            if (_gravityCoroutine != null)
            {
                StopCoroutine(_gravityCoroutine);
                _gravityCoroutine = null;
            }
            _gravityCoroutine = StartCoroutine(GravityFallCoroutine(targetWorldPos, duration));
        }

        private IEnumerator GravityFallCoroutine(Vector3 target, float duration)
        {
            IsAnimating = true;

            Vector3 start = transform.position;
            float dur = Mathf.Max(duration, 0.04f);
            float elapsed = 0f;

            // Accelerating fall
            while (elapsed < dur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / dur);
                float easedT = t * t * t; // cubic ease-in — faster acceleration
                transform.position = Vector3.Lerp(start, target, easedT);
                yield return null;
            }

            transform.position = target;
            IsAnimating        = false;
            _gravityCoroutine  = null;

            // Fire-and-forget soft squish — less pronounced than initial drop
            PlayGravitySquish();
        }

        // ---------------------------------------------------------------------------
        // Public API — FlashWhite (Job 11 NEW)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Flashes the tile white with a scale pulse over the given duration.
        /// Used by GameVisualBridge when a primed word is triggered.
        /// First half: scale up to 1.25× and border turns white.
        /// Second half: scale back down to 1×, border stays white.
        /// Caller is responsible for removing the tile after this completes.
        /// </summary>
        public IEnumerator FlashWhite(float duration)
        {
            // Stop any existing flash
            if (_flashCoroutine != null)
            {
                StopCoroutine(_flashCoroutine);
                _flashCoroutine = null;
            }

            _flashCoroutine = StartCoroutine(FlashWhiteCoroutine(duration));
            yield return _flashCoroutine;
        }

        private IEnumerator FlashWhiteCoroutine(float duration)
        {
            Vector3 originalScale = transform.localScale;
            float   halfDuration  = duration * 0.5f;

            // ── Phase 1: Scale up + turn white ───────────────────────────────────
            float elapsed = 0f;
            while (elapsed < halfDuration)
            {
                elapsed += Time.deltaTime;
                float t         = Mathf.Clamp01(elapsed / halfDuration);
                float scaleMult = Mathf.Lerp(1f, 1.25f, t);
                transform.localScale = originalScale * scaleMult;

                // Apply white border immediately
                ApplyBorderColorDirect(Color.white);
                yield return null;
            }

            // Ensure we hit peak scale
            transform.localScale = originalScale * 1.25f;
            ApplyBorderColorDirect(Color.white);

            // ── Phase 2: Scale back down, stay white ──────────────────────────────
            elapsed = 0f;
            while (elapsed < halfDuration)
            {
                elapsed += Time.deltaTime;
                float t         = Mathf.Clamp01(elapsed / halfDuration);
                float scaleMult = Mathf.Lerp(1.25f, 1f, t);
                transform.localScale = originalScale * scaleMult;
                yield return null;
            }

            // Settle back to original scale, white border stays for explosion
            transform.localScale = originalScale;
            ApplyBorderColorDirect(Color.white);

            _flashCoroutine = null;
        }

        // ---------------------------------------------------------------------------
        // Public API — Primed glow (NEW in Job 6)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Sets a colored primed glow on this tile's border.
        /// Used by GameVisualBridge when a word becomes primed.
        /// P1 color = green, AI color = orange.
        /// </summary>
        private Coroutine _primedPulseCoroutine;

        public void SetPrimedGlow(Color color)
        {
            _hasPrimedGlow   = true;
            _primedGlowColor = color;
            _currentBorderColor = color;

            if (!_isHighlighted)
                ApplyBorderColor(color);

            // Start subtle primed idle animation
            if (_primedPulseCoroutine == null)
                _primedPulseCoroutine = StartCoroutine(PrimedPulseLoop());
        }

        /// <summary>
        /// Clears the primed glow and restores the tile to its normal border color.
        /// Called when a primed word expires or is triggered.
        /// </summary>
        /// <summary>
        /// Subtle idle animation for primed tiles — contained dangerous energy.
        /// Gentle golden inner glow pulse + micro scale instability.
        /// </summary>
        private System.Collections.IEnumerator PrimedPulseLoop()
        {
            yield return new WaitForSeconds(Random.Range(0f, 1.0f));

            float baseTime = 0f;

            // Colors: gold base, hot magenta peak
            Color goldTint   = new Color(1f, 0.90f, 0.70f, 1f);   // warm gold
            Color magentaTint = new Color(1f, 0.55f, 0.75f, 1f);   // hot magenta/pink

            // Cache base scale
            int ts = Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512);
            float baseScale = (_cellSize * 0.88f) / (ts / 100f);

            while (_hasPrimedGlow && _spriteRenderer != null)
            {
                if (_flashCoroutine != null)
                {
                    yield return null;
                    continue;
                }

                baseTime += Time.deltaTime;

                // Pulse cycle: ~1.8s period
                // Mostly gold (80% of cycle), brief swell to magenta (20%)
                float cycle = Mathf.Repeat(baseTime, 1.8f) / 1.8f; // 0-1 over 1.8s
                float pulse;
                if (cycle < 0.7f)
                {
                    // Resting in gold — gentle breathing
                    pulse = 0f;
                }
                else
                {
                    // Swell to magenta and back (0.7 to 1.0)
                    float t = (cycle - 0.7f) / 0.3f; // 0-1 over the swell
                    pulse = Mathf.Sin(t * Mathf.PI); // smooth up and down
                }

                // Tint: gold base with magenta swell
                Color tint = Color.Lerp(goldTint, magentaTint, pulse * 0.45f);
                float tintAmount = 0.06f + pulse * 0.12f; // 6% at rest, up to 18% at peak
                _spriteRenderer.color = Color.Lerp(Color.white, tint, tintAmount);

                // Micro scale: tiny swell during magenta pulse
                float scaleMult = 1f + pulse * 0.02f; // 1.00 at rest, 1.02 at peak

                if (!IsAnimating && _flashCoroutine == null)
                    transform.localScale = new Vector3(baseScale * scaleMult, baseScale * scaleMult, 1f);

                yield return null;
            }

            // Clean up when primed state ends
            if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
            _primedPulseCoroutine = null;
        }

        /// <summary>Brief color flash to highlight a scored word tile.</summary>
        public void FlashHighlight(Color color)
        {
            if (_spriteRenderer == null) return;
            // Cancel any existing flash so they don't overlap
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            // Force-reset before starting new flash
            _spriteRenderer.color = Color.white;
            _flashCoroutine = StartCoroutine(FlashBorderCoroutine(color));
        }

        /// <summary>Force-reset visuals — call to unstick any interrupted flash.</summary>
        public void ResetVisuals()
        {
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }
            if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
            Color border = _hasPrimedGlow ? _primedGlowColor : TILE_BORDER_NORMAL;
            ApplyBorderColor(border);
        }

        private System.Collections.IEnumerator FlashBorderCoroutine(Color color)
        {
            if (_spriteRenderer == null) yield break;

            Color glowColor = color;

            Vector3 origScale = transform.localScale;
            Vector3 glowScale = origScale * 1.06f;

            // Phase 1: Flash UP (0.05s)
            float elapsed = 0f;
            while (elapsed < 0.05f && _spriteRenderer != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.05f);
                t = 1f - (1f - t) * (1f - t);
                _spriteRenderer.color = Color.Lerp(Color.white, glowColor, t);
                transform.localScale = Vector3.Lerp(origScale, glowScale, t);
                yield return null;
            }

            // Phase 2: Hold at peak glow (0.10s)
            if (_spriteRenderer != null) _spriteRenderer.color = glowColor;
            if (transform != null) transform.localScale = glowScale;
            yield return new WaitForSeconds(0.10f);

            // Phase 3: Fade back (0.15s)
            elapsed = 0f;
            while (elapsed < 0.15f && _spriteRenderer != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.15f);
                t = t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
                _spriteRenderer.color = Color.Lerp(glowColor, Color.white, t);
                transform.localScale = Vector3.Lerp(glowScale, origScale, t);

                yield return null;
            }

            if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
            if (transform != null) transform.localScale = origScale;
            _flashCoroutine = null;
        }

        public void ClearPrimedGlow()
        {
            _hasPrimedGlow      = false;
            _currentBorderColor = TILE_BORDER_NORMAL;

            // Stop primed pulse
            if (_primedPulseCoroutine != null)
            {
                StopCoroutine(_primedPulseCoroutine);
                _primedPulseCoroutine = null;
            }

            if (!_isHighlighted)
                ApplyBorderColor(TILE_BORDER_NORMAL);

            // Reset any pulse-induced tint/scale
            if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
        }

        // ---------------------------------------------------------------------------
        // Public API — SetPermanentGlow (backward compat alias for SetPrimedGlow)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Backward-compat alias for SetPrimedGlow. HandManager calls this.
        /// </summary>
        public void SetPermanentGlow(Color color)
        {
            if (_hasPrimedGlow)
            {
                Debug.Log($"[Tile] ({Col},{Row}) '{Letter}' already has primed glow — keeping.");
                return;
            }
            SetPrimedGlow(color);
        }

        // ---------------------------------------------------------------------------
        // Public API — temporary highlight
        // ---------------------------------------------------------------------------

        public void Highlight(bool on)
        {
            Highlight(on, TILE_BORDER_GOLD);
        }

        public void Highlight(bool on, Color color)
        {
            if (_isHighlighted == on && on == false) return;
            _isHighlighted = on;

            if (on)
            {
                ApplyBorderColor(color);
            }
            else
            {
                // Restore to primed glow if set, otherwise restore current border
                Color restoreColor = _hasPrimedGlow ? _primedGlowColor : _currentBorderColor;
                ApplyBorderColor(restoreColor);
            }
        }

        // ---------------------------------------------------------------------------
        // Public API — Wordle coloring stubs (no-op, kept for compile compat)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// No-op stub kept for compile compatibility with WordleEvaluator.
        /// Wordle coloring has been removed in Job 6.
        /// </summary>
        public void SetColorState(TileColorState state)
        {
            ColorState = state;
            // No-op: Wordle coloring removed. State tracked for compatibility only.
        }

        /// <summary>
        /// Sets the border color directly. Respects primed glow priority.
        /// If a primed glow is active, this call is ignored unless force=true.
        /// </summary>
        public void SetBorderColor(Color borderColor)
        {
            if (_hasPrimedGlow) return;
            _currentBorderColor = borderColor;
            if (!_isHighlighted)
                ApplyBorderColor(borderColor);
        }

        // ---------------------------------------------------------------------------
        // Public API — Scrabble style (kept for legacy callers)
        // ---------------------------------------------------------------------------

        public void SetScrabbleStyle(char letter, int pointValue)
        {
            Letter = letter;
            if (_letterTMP != null)
                _letterTMP.text = letter.ToString().ToUpper();
            if (_pointTMP != null)
                _pointTMP.text = pointValue > 0 ? pointValue.ToString() : "";
        }

        // ---------------------------------------------------------------------------
        // Internal border application
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Rebuilds the sprite with the given border color. Respects highlight/glow state.
        /// </summary>
        // Static cached sprites — shared by ALL tiles, generated once
        private static Sprite s_spriteNormal;
        private static Sprite s_spriteThick;
        private static Sprite s_spriteGold;
        private static Sprite s_spriteGoldThick;
        private static Sprite s_spriteWhiteThick;
        private static bool s_spriteCacheBuilt;

        private void BuildSpriteCache()
        {
            if (s_spriteCacheBuilt) return; // already built by another tile
            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512);
            int radius  = texSize / 6;
            int border  = Mathf.Max(3, texSize / 12);

            s_spriteNormal    = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_NORMAL, border);
            s_spriteThick     = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_NORMAL, border + 3);
            s_spriteGold      = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_GOLD, border);
            s_spriteGoldThick = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_GOLD, border + 3);
            s_spriteWhiteThick= TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, Color.white, border + 3);
            s_spriteCacheBuilt = true;
            Debug.Log("[Tile] Static sprite cache built (5 sprites).");
        }

        private void ApplyBorderColor(Color borderColor)
        {
            if (_spriteRenderer == null) return;
            bool thick = _isHighlighted || _hasPrimedGlow;

            // Use cached sprite if color matches a known state
            if (ColorsClose(borderColor, TILE_BORDER_NORMAL))
                _spriteRenderer.sprite = thick ? s_spriteThick : s_spriteNormal;
            else if (ColorsClose(borderColor, TILE_BORDER_GOLD) || ColorsClose(borderColor, PRIMED_GLOW))
                _spriteRenderer.sprite = thick ? s_spriteGoldThick : s_spriteGold;
            else if (ColorsClose(borderColor, Color.white))
                _spriteRenderer.sprite = s_spriteWhiteThick;
            else
            {
                // Unknown color (player green / AI orange flash) — use SpriteRenderer tint instead of regenerating
                _spriteRenderer.sprite = thick ? s_spriteThick : s_spriteNormal;
                // Border color is faked via sprite tint — good enough for a brief flash
            }
        }

        private void ApplyBorderColorDirect(Color borderColor)
        {
            if (_spriteRenderer == null) return;
            if (ColorsClose(borderColor, Color.white))
                _spriteRenderer.sprite = s_spriteWhiteThick;
            else
                _spriteRenderer.sprite = s_spriteThick;
        }

        private static bool ColorsClose(Color a, Color b)
        {
            return Mathf.Abs(a.r - b.r) < 0.02f &&
                   Mathf.Abs(a.g - b.g) < 0.02f &&
                   Mathf.Abs(a.b - b.b) < 0.02f;
        }

        // ---------------------------------------------------------------------------
        // Fall animation with bounce (initial drop from column top)
        // ---------------------------------------------------------------------------

        public void AnimateFall(Vector3 startWorldPos, Vector3 targetWorldPos)
        {
            transform.position = startWorldPos;
            if (_fallCoroutine != null)
            {
                StopCoroutine(_fallCoroutine);
                _fallCoroutine = null;
            }
            _fallCoroutine = StartCoroutine(FallCoroutine(startWorldPos, targetWorldPos));
        }

        private IEnumerator FallCoroutine(Vector3 start, Vector3 target)
        {
            IsAnimating = true;

            // Cache the base scale for squish calculations
            Vector3 baseScale = transform.localScale;

            Vector3 overshootTarget = target + Vector3.down * (_cellSize * BOUNCE_OVERSHOOT);
            float   elapsed         = 0f;

            while (elapsed < FALL_DURATION)
            {
                elapsed += Time.deltaTime;
                float t      = Mathf.Clamp01(elapsed / FALL_DURATION);
                float easedT = t * t;
                transform.position = Vector3.LerpUnclamped(start, overshootTarget, easedT);

                // Stretch vertically as it falls (anticipation)
                float stretch = 1f + easedT * 0.10f;
                float squash  = 1f / stretch; // preserve volume
                transform.localScale = new Vector3(baseScale.x * squash, baseScale.y * stretch, 1f);

                yield return null;
            }

            transform.position = overshootTarget;

            // Settle position to target quickly
            float settleDur = 0.04f;
            elapsed = 0f;
            while (elapsed < settleDur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / settleDur);
                transform.position = Vector3.Lerp(overshootTarget, target, t);
                yield return null;
            }
            transform.position = target;
            transform.localScale = baseScale;

            // Reuse the same landing squish as every other drop path
            _fallCoroutine = null;
            yield return LandingSquishCoroutine();
        }

        private void PlayLandSound()
        {
            if (_audioSource != null && _audioSource.clip != null)
                _audioSource.PlayOneShot(_audioSource.clip);
        }

        // ---------------------------------------------------------------------------
        // Public API — Landing squish (can be called by any drop path)
        // ---------------------------------------------------------------------------

        private Coroutine _squishCoroutine;

        /// <summary>
        /// Plays the squash-and-stretch landing effect. Call this after the tile
        /// reaches its final position, from any drop path (HandManager, GameVisualBridge, etc).
        /// </summary>
        public void PlayLandingSquish()
        {
            if (_squishCoroutine != null) StopCoroutine(_squishCoroutine);
            _squishCoroutine = StartCoroutine(LandingSquishCoroutine());
        }

        /// <summary>Coroutine version for yielding.</summary>
        public IEnumerator PlayLandingSquishAndWait()
        {
            if (_squishCoroutine != null) StopCoroutine(_squishCoroutine);
            _squishCoroutine = StartCoroutine(LandingSquishCoroutine());
            yield return _squishCoroutine;
        }

        private IEnumerator LandingSquishCoroutine()
        {
            IsAnimating = true;
            Vector3 baseScale = transform.localScale;
            PlayLandSound();

            float squishDur = 0.18f;
            float elapsed = 0f;
            while (elapsed < squishDur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / squishDur);

                float sx, sy;
                if (t < 0.25f)
                {
                    // Phase 1: quick squash
                    float p = t / 0.25f;
                    float ease = p * p;
                    sx = Mathf.Lerp(1f, 1.10f, ease);   // was 1.15
                    sy = Mathf.Lerp(1f, 0.91f, ease);   // was 0.87
                }
                else if (t < 0.55f)
                {
                    // Phase 2: slight overshoot tall
                    float p = (t - 0.25f) / 0.30f;
                    float ease = Mathf.Sin(p * Mathf.PI * 0.5f);
                    sx = Mathf.Lerp(1.10f, 0.97f, ease);  // was 1.15/0.96
                    sy = Mathf.Lerp(0.91f, 1.03f, ease);  // was 0.87/1.04
                }
                else
                {
                    // Phase 3: settle
                    float p = (t - 0.55f) / 0.45f;
                    float ease = 1f - (1f - p) * (1f - p);
                    sx = Mathf.Lerp(0.97f, 1f, ease);
                    sy = Mathf.Lerp(1.03f, 1f, ease);
                }

                transform.localScale = new Vector3(baseScale.x * sx, baseScale.y * sy, 1f);
                yield return null;
            }

            transform.localScale = baseScale;
            IsAnimating = false;
            _squishCoroutine = null;
        }

        /// <summary>
        /// Lighter squish for gravity landings — about half the intensity of the drop squish.
        /// Fire-and-forget, doesn't set IsAnimating.
        /// </summary>
        public void PlayGravitySquish()
        {
            StartCoroutine(GravitySquishCoroutine());
        }

        private IEnumerator GravitySquishCoroutine()
        {
            Vector3 baseScale = transform.localScale;

            float squishDur = 0.14f;
            float elapsed = 0f;
            while (elapsed < squishDur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / squishDur);

                float sx, sy;
                if (t < 0.3f)
                {
                    float p = t / 0.3f;
                    sx = Mathf.Lerp(1f, 1.10f, p * p);
                    sy = Mathf.Lerp(1f, 0.90f, p * p);
                }
                else
                {
                    float p = (t - 0.3f) / 0.7f;
                    float ease = 1f - (1f - p) * (1f - p);
                    sx = Mathf.Lerp(1.10f, 1f, ease);
                    sy = Mathf.Lerp(0.90f, 1f, ease);
                }

                transform.localScale = new Vector3(baseScale.x * sx, baseScale.y * sy, 1f);
                yield return null;
            }

            transform.localScale = baseScale;
        }

        // ---------------------------------------------------------------------------
        // Public API — Fake 3D rotation (baked atlas + shader)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Enables fake 3D on this tile. Swaps to a pre-baked single-image sprite
        /// (letter + points rendered in) and applies the fake 3D perspective shader.
        /// </summary>
        public void SetFake3D(float rotX, float rotY)
        {
            if (!_hasFake3D)
                EnableFake3D();

            if (_fake3DMatInstance != null)
            {
                _fake3DMatInstance.SetFloat("_RotX", rotX);
                _fake3DMatInstance.SetFloat("_RotY", rotY);
            }
        }

        /// <summary>Animates 3D rotation from one angle to another.</summary>
        public void AnimateFake3D(float toRotX, float toRotY, float duration, float fromRotX = 0f, float fromRotY = 0f)
        {
            if (_rotateCoroutine != null) StopCoroutine(_rotateCoroutine);
            _rotateCoroutine = StartCoroutine(Fake3DCoroutine(fromRotX, fromRotY, toRotX, toRotY, duration));
        }

        /// <summary>Coroutine version for yielding.</summary>
        public IEnumerator AnimateFake3DAndWait(float toRotX, float toRotY, float duration, float fromRotX = 0f, float fromRotY = 0f)
        {
            if (_rotateCoroutine != null) StopCoroutine(_rotateCoroutine);
            _rotateCoroutine = StartCoroutine(Fake3DCoroutine(fromRotX, fromRotY, toRotX, toRotY, duration));
            yield return _rotateCoroutine;
        }

        /// <summary>Removes fake 3D, restores original sprite + text children.</summary>
        public void ClearFake3D()
        {
            if (_rotateCoroutine != null) { StopCoroutine(_rotateCoroutine); _rotateCoroutine = null; }

            if (_hasFake3D)
            {
                // Destroy the baked overlay
                if (_bakedRenderer != null)
                {
                    Destroy(_bakedRenderer.gameObject);
                    _bakedRenderer = null;
                }

                // Restore originals
                if (_spriteRenderer != null) _spriteRenderer.enabled = true;
                if (_letterTMP != null) _letterTMP.enabled = true;
                if (_pointTMP != null) _pointTMP.enabled = true;

                _hasFake3D = false;
            }
        }

        private void EnableFake3D()
        {
            if (_spriteRenderer == null) return;

            // Get the pre-baked sprite from the atlas
            Sprite bakedSprite = TileSpriteAtlas.Get(Letter, _cellSize);
            if (bakedSprite == null)
            {
                Debug.LogWarning($"[Tile] Failed to get baked sprite for '{Letter}'");
                return;
            }

            // Hide original children
            _spriteRenderer.enabled = false;
            if (_letterTMP != null) _letterTMP.enabled = false;
            if (_pointTMP != null) _pointTMP.enabled = false;

            // Create as root-level object — NO parenting, no scale inheritance
            GameObject bakedGO = new GameObject($"Fake3D_{Letter}");
            bakedGO.transform.position = transform.position;
            bakedGO.transform.rotation = Quaternion.identity;
            bakedGO.transform.localScale = Vector3.one;

            _bakedRenderer = bakedGO.AddComponent<SpriteRenderer>();
            _bakedRenderer.sprite = bakedSprite;
            _bakedRenderer.sortingOrder = _spriteRenderer.sortingOrder + 1;

            // Load shader
            if (s_fake3DMaterial == null)
            {
                Shader shader = Shader.Find("WordDrop/Fake3D");
                if (shader != null)
                    s_fake3DMaterial = new Material(shader);
            }

            if (s_fake3DMaterial != null)
            {
                _fake3DMatInstance = new Material(s_fake3DMaterial);
                _fake3DMatInstance.SetTexture("_MainTex", bakedSprite.texture);
                _bakedRenderer.material = _fake3DMatInstance;
            }

            _hasFake3D = true;
        }

        /// <summary>
        /// Must be called each frame while fake 3D is active to keep
        /// the baked sprite following the tile's position.
        /// </summary>
        public void UpdateFake3DPosition()
        {
            if (_hasFake3D && _bakedRenderer != null)
                _bakedRenderer.transform.position = transform.position;
        }

        private IEnumerator Fake3DCoroutine(float fromX, float fromY, float toX, float toY, float duration)
        {
            if (!_hasFake3D) EnableFake3D();

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);

                float rx = Mathf.Lerp(fromX, toX, eased);
                float ry = Mathf.Lerp(fromY, toY, eased);

                if (_fake3DMatInstance != null)
                {
                    _fake3DMatInstance.SetFloat("_RotX", rx);
                    _fake3DMatInstance.SetFloat("_RotY", ry);
                }

                yield return null;
            }

            if (_fake3DMatInstance != null)
            {
                _fake3DMatInstance.SetFloat("_RotX", toX);
                _fake3DMatInstance.SetFloat("_RotY", toY);
            }

            _rotateCoroutine = null;
        }

        // ---------------------------------------------------------------------------
        // Public API — Dissolve effect (detonation)
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Dissolves this tile over the given duration using a noise-based burn shader.
        /// The tile burns away from edges inward with a hot orange/yellow border.
        /// Call this instead of Destroy for detonation visuals.
        /// </summary>
        public void Dissolve(float duration = 0.4f)
        {
            if (IsDissolving) return;
            if (_dissolveCoroutine != null) StopCoroutine(_dissolveCoroutine);
            _dissolveCoroutine = StartCoroutine(DissolveCoroutine(duration));
        }

        /// <summary>
        /// Coroutine version that can be yielded on (waits for dissolve to finish).
        /// </summary>
        public IEnumerator DissolveAndWait(float duration = 0.4f)
        {
            if (IsDissolving) yield break;
            if (_dissolveCoroutine != null) StopCoroutine(_dissolveCoroutine);
            _dissolveCoroutine = StartCoroutine(DissolveCoroutine(duration));
            yield return _dissolveCoroutine;
        }

        private IEnumerator DissolveCoroutine(float duration)
        {
            IsDissolving = true;

            // Stop any other visual effects
            if (_primedPulseCoroutine != null) { StopCoroutine(_primedPulseCoroutine); _primedPulseCoroutine = null; }
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }

            // Load dissolve shader once
            if (s_dissolveMaterial == null)
            {
                Shader dissolveShader = Shader.Find("WordDrop/TileDissolve");
                if (dissolveShader == null)
                {
                    Debug.LogWarning("[Tile] Dissolve shader not found — falling back to destroy.");
                    IsDissolving = false;
                    yield break;
                }
                s_dissolveMaterial = new Material(dissolveShader);
            }

            // Create instance material from the dissolve shader
            _dissolveMatInstance = new Material(s_dissolveMaterial);

            // Copy current sprite texture into the dissolve material
            if (_spriteRenderer != null && _spriteRenderer.sprite != null)
            {
                _dissolveMatInstance.SetTexture("_MainTex", _spriteRenderer.sprite.texture);
                _dissolveMatInstance.SetColor("_Color", _spriteRenderer.color);
                _spriteRenderer.material = _dissolveMatInstance;
            }

            // Animate _DissolveAmount from 0 to 1, fade text in sync
            Color letterStartColor = _letterTMP != null ? _letterTMP.color : Color.clear;
            Color pointStartColor  = _pointTMP != null ? _pointTMP.color : Color.clear;

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Ease in slightly so the burn accelerates
                float eased = t * t * (3f - 2f * t); // smoothstep
                _dissolveMatInstance.SetFloat("_DissolveAmount", eased);

                // Fade text out faster than the tile dissolves
                float textAlpha = Mathf.Clamp01(1f - t * 2f);
                if (_letterTMP != null)
                    _letterTMP.color = new Color(letterStartColor.r, letterStartColor.g, letterStartColor.b, letterStartColor.a * textAlpha);
                if (_pointTMP != null)
                    _pointTMP.color = new Color(pointStartColor.r, pointStartColor.g, pointStartColor.b, pointStartColor.a * textAlpha);

                yield return null;
            }

            _dissolveMatInstance.SetFloat("_DissolveAmount", 1f);
            IsDissolving = false;
            _dissolveCoroutine = null;
        }
    }
}
