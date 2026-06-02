using System.Collections;
using UnityEngine;
using TMPro;
using DG.Tweening;

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

        public static readonly Color PRIMED_GLOW       = new Color(1.8f, 0.5f, 1.3f, 1f);  // HDR magenta — bloom catches this

        /// <summary>Override for ResetVisuals's "restore default sortingOrder"
        /// step. When > 0, ResetVisuals will set the tile's sortingOrder to
        /// this value instead of the baseline 5. Used by BoosterHUDSlot during
        /// booster aim mode so tiles stay above the scrim even if a cleanup
        /// path (rewrite cancel, drop complete, etc.) fires mid-aim.
        /// Reset to 0 when aim mode ends.</summary>
        public static int AimModeTileOrder = 0;
        public static readonly Color PRIMED_GOLD_GLOW  = new Color(2.0f, 1.5f, 0.3f, 1f);  // HDR gold — bloom catches this
        // Final-turn warning: primed word has 1 drop left. Shifts to HDR red-orange
        // so the player gets a clear "USE THIS OR LOSE IT" signal on their last chance.
        public static readonly Color PRIMED_DANGER_GLOW = new Color(2.2f, 0.6f, 0.15f, 1f); // HDR red-orange
        public static readonly Color PLAYER_GLOW = PRIMED_GLOW;
        public static readonly Color AI_GLOW     = PRIMED_GLOW;

        private const float FALL_DURATION     = 0.22f;   // feel-pass 2026-05-16: 0.30 → 0.22 (RM-snappier)
        private const float BOUNCE_OVERSHOOT  = 0.12f;  // visible overshoot
        private const float BOUNCE_SETTLE_DUR = 0.06f;  // quick snap back

        // Tile visual size as a fraction of cell pitch. Numerator (150) =
        // tile size in PSD pixels. Denominator MUST match GridManager's
        // PSD_CELL_PITCH so tile visual size stays a constant 150 PSD
        // regardless of what pitch is set to.
        //
        // 2026-05-30: pitch bumped 165 → 168 for a hair more inter-tile gap
        // without shrinking tiles. Denominator updated in lockstep. If you
        // change PSD_CELL_PITCH in GridManager, update the denominator here
        // too. (Long-term: refactor so tile size is decoupled from pitch
        // entirely — for now, two-place coupled change.)
        private const float TILE_DISPLAY_RATIO = 150f / 168f;

        // ---------------------------------------------------------------------------
        // Runtime state
        // ---------------------------------------------------------------------------

        public char  Letter      { get; private set; }
        public int   Col         { get; private set; }
        public int   Row         { get; private set; }
        public bool  IsAnimating { get; private set; }

        // Phase C — real wild state. Uncommitted wilds (IsWild && Letter=='\0')
        // render a ★ glyph; committed wilds show the resolved letter in a wild
        // color so the player can read what it matched while still seeing it's a wild.
        public bool IsWild => _isWild;
        private bool _isWild = false;

        private static readonly Color WILD_LETTER_COLOR = new Color(0.75f, 0.40f, 1.00f, 1f); // purple
        // "?" reads as blank/wild (Scrabble convention). ★ wasn't in the font atlas.
        private const string WILD_GLYPH = "?";

        // Wild halo — shared sprite + additive material, loaded on first SetWild.
        // Parallels HandManager's card halo so the wild tile keeps its "special"
        // presence as it moves from hand → board.
        private static Sprite   s_wildHaloSprite;
        private static Material s_wildHaloMaterial;
        private GameObject      _wildHaloGO;
        private SpriteRenderer  _wildHaloSR;

        // Edit-selected halo — reuses the wild-halo radial texture, tinted cyan.
        // Option A "selected" treatment: tile keeps its own face; a cyan glow
        // ring breathes behind it + a select-pop reads as "picked".
        private GameObject      _editHaloGO;
        private SpriteRenderer  _editHaloSR;
        private static Sprite   s_editHaloSprite;   // Square_aura_invert — rounded-square rim glow
        private static Material s_editHaloMaterial;
        private Vector3         _editBaseScale = Vector3.one;
        private bool            _editSelected;
        private static readonly Color EDIT_HALO_CYAN = new Color(0.35f, 0.88f, 0.98f, 1f);
        private const float EDIT_POP_SCALE        = 1.09f; // springy lift on select
        private const float EDIT_BREATH_SCALE     = 1.05f; // stays slightly enlarged while breathing
        private const float EDIT_POP_DUR          = 0.26f;
        private const float EDIT_BREATH_DUR       = 0.85f;
        private const float EDIT_HALO_ALPHA_LOW   = 0.10f;
        private const float EDIT_HALO_ALPHA_HIGH  = 0.26f;
        private const float EDIT_HALO_SIZE        = 1.5f;  // halo footprint as ×cell (rim spills past tile edges)
        // ColorState removed (was for Wordle mode)

        private SpriteRenderer _spriteRenderer;
        private Material       _defaultMaterial; // saved on first init for pool reset
        private TextMeshPro    _letterTMP;
        private TextMeshPro    _pointTMP;
        private AudioSource    _audioSource;
        private float          _cellSize;

        private bool  _isHighlighted    = false;
        private bool  _isGoldBonus      = false;
        private bool  _isSwapRefill    = false;
        private bool  _isEditRefill    = false;
        private bool  _isWildRefill   = false;
        private bool  _isStone        = false;
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

        public int OwnerIndex { get; private set; } = -1;

        private bool _poolInitialized = false;

        public void Initialise(char letter, int col, int row, float cellSize, int ownerIndex = -1)
        {
            if (letter == '\0')
            {
                Debug.LogError($"[Tile] Initialise with NULL letter at ({col},{row}) — destroying");
                Destroy(gameObject);
                return;
            }

            Letter    = letter;
            Col       = col;
            Row       = row;
            _cellSize = cellSize;
            OwnerIndex = ownerIndex;

            BuildSpriteCache();
            BuildVisuals(cellSize);
            if (_spriteRenderer != null) _defaultMaterial = _spriteRenderer.sharedMaterial;
            SetupAudio();
            UpdateLetterDisplay(letter);
            _poolInitialized = true;
        }

        public void Reinitialise(char letter, int col, int row, float cellSize, int ownerIndex = -1)
        {
            if (!_poolInitialized) { Initialise(letter, col, row, cellSize, ownerIndex); return; }

            // Kill any in-flight edit-selected breath tween before reuse — a
            // recycled tile must not carry an oscillating DOScale into its next
            // life (would fight the localScale set below). No-op if not selected.
            SetEditSelected(false);

            // Pool reuse: wild flag does not survive, caller must re-SetWild if needed.
            _isWild    = false;
            // Clear sticky scored-word flags too — see ResetForPool comment.
            _isShowingScoredSprite = false;
            _wasInScoredWord       = false;
            // Reset localPosition — see ResetForPool comment.
            transform.localPosition = Vector3.zero;
            Letter     = letter;
            Col        = col;
            Row        = row;
            _cellSize  = cellSize;
            OwnerIndex = ownerIndex;

            gameObject.SetActive(true);
            gameObject.name = $"Tile_{letter}_{col}_{row}";

            if (_spriteRenderer != null)
            {
                _spriteRenderer.sprite = (ownerIndex == 1 && s_spriteAI != null) ? s_spriteAI : s_spriteNormal;
                _spriteRenderer.color = Color.white;
                _spriteRenderer.enabled = true;
            }

            _currentBorderColor = TILE_BORDER_NORMAL;
            ApplyBorderColor(TILE_BORDER_NORMAL);

            // 2026-05-28 (Path A, Phase 2): cell pitch = 165 PSD, visible
            // tile = 150 PSD (= 0.909 of pitch), gap = 15 PSD between tiles.
            // Margin inside the 1030 board = 20 PSD per side. Matches board
            // tile to hand card size; spreads cells across full board width.
            float displaySize = cellSize * TILE_DISPLAY_RATIO;
            float nativeSize = _spriteRenderer != null && _spriteRenderer.sprite != null
                ? _spriteRenderer.sprite.bounds.size.x : 1f;
            float scale = displaySize / nativeSize;
            transform.localScale = new Vector3(scale, scale, 1f);
            transform.localRotation = Quaternion.identity;

            UpdateLetterDisplay(letter);
            if (_letterTMP != null) { _letterTMP.gameObject.SetActive(true); _letterTMP.enabled = true; }
            if (_pointTMP != null) { _pointTMP.gameObject.SetActive(true); _pointTMP.enabled = true; }
        }

        public void ResetForPool()
        {
            StopAllCoroutines();
            _gravityCoroutine = null;
            _fallCoroutine = null;
            _flashCoroutine = null;
            _dissolveCoroutine = null;
            _rotateCoroutine = null;

            DOTween.Kill(transform);

            if (_hasPrimedGlow) ClearPrimedGlow();
            if (_hasFake3D) ClearFake3D();
            if (_isHighlighted) Highlight(false);
            if (_isGoldBonus) SetGoldBonus(false);
            if (_isStone) SetStoneVisual(false);
            if (_isSwapRefill) SetSwapRefillVisual(false);
            if (_isEditRefill) SetEditRefillVisual(false);
            if (_isWildRefill) SetWildRefillVisual(false);
            if (_isWild) SetWild(false);

            IsAnimating = false;
            IsDissolving = false;
            _hasPreviewHighlight = false;
            // Clear sticky scored-word flags so a recycled tile doesn't carry
            // a prior turn's "I was in a scored word" memory into its next life.
            // (WordDropFX.Tier1PopCoroutine reads WasInScoredWord to decide
            // whether to apply the green-scored sprite/tint on detonation —
            // without this clear, K and U from a prior turn light up green
            // when caught in collateral splash damage on a later word.)
            _isShowingScoredSprite = false;
            _wasInScoredWord = false;
            // Reset localPosition in case MeltdownShakeCoroutine was interrupted
            // mid-shake and never restored its baseLocalPos at line 1190.
            Debug.Log($"[Tile] ResetForPool localPos: {transform.localPosition} → Vector3.zero");
            transform.localPosition = Vector3.zero;
            if (_dissolveMatInstance != null)
            {
                Destroy(_dissolveMatInstance);
                _dissolveMatInstance = null;
            }
            // Restore default material and ensure sprite renderer is clean
            if (_spriteRenderer != null)
            {
                if (_defaultMaterial != null) _spriteRenderer.material = _defaultMaterial;
                _spriteRenderer.enabled = true;
                _spriteRenderer.color = Color.white;
            }
            // Reset TMP colors — dissolve fades these to alpha 0
            if (_letterTMP != null)
            {
                _letterTMP.color = new Color(0.145f, 0.153f, 0.200f, 1f);
                _letterTMP.gameObject.SetActive(true);
                _letterTMP.enabled = true;
            }
            if (_pointTMP != null)
            {
                _pointTMP.color = new Color(0.4f, 0.4f, 0.45f, 0.85f);
                _pointTMP.gameObject.SetActive(true);
                _pointTMP.enabled = true;
            }

            Letter = '\0';
            Col = -1;
            Row = -1;
            gameObject.SetActive(false);
        }

        private void BuildVisuals(float cellSize)
        {
            int texSize = Mathf.Clamp(Mathf.RoundToInt(cellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // chunkier rounded corners
            int border  = Mathf.Max(3, texSize / 12);  // thicker edge for tactile depth

            _currentBorderColor = TILE_BORDER_NORMAL;

            _spriteRenderer              = gameObject.AddComponent<SpriteRenderer>();
            _spriteRenderer.sprite       = (OwnerIndex == 1 && s_spriteAI != null) ? s_spriteAI : s_spriteNormal;
            _spriteRenderer.sortingOrder = 5;

            // Skip lit material for now — debug: does the tile render without it?
            // LightingSetup.Instance?.ApplyLitMaterial(_spriteRenderer);

            // 2026-05-28 (Path A, Phase 2): cell pitch = 165 PSD, visible
            // tile = 150 PSD (= 0.909 of pitch), gap = 15 PSD between tiles.
            // Margin inside the 1030 board = 20 PSD per side. Matches board
            // tile to hand card size; spreads cells across full board width.
            float displaySize = cellSize * TILE_DISPLAY_RATIO;
            // Use actual sprite bounds for sizing (works with both procedural and hand-drawn sprites)
            float nativeSize = _spriteRenderer.sprite != null
                ? _spriteRenderer.sprite.bounds.size.x
                : texSize / 100f;
            float scale       = displaySize / nativeSize;
            transform.localScale = new Vector3(scale, scale, 1f);

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // ── Main letter — TextMeshPro with subtle grounding shadow ──
            GameObject letterGO = new GameObject("TileLetter");
            letterGO.transform.SetParent(transform, false);
            // Centered on tile face (true center, no offset).
            letterGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);

            _letterTMP = letterGO.AddComponent<TextMeshPro>();
            // Load Fredoka Bold TMP font
            TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) _letterTMP.font = tileFont;
            _letterTMP.text           = "";
            _letterTMP.fontSize       = 6.0f;
            _letterTMP.fontStyle      = FontStyles.Bold;
            _letterTMP.color          = new Color(0.145f, 0.153f, 0.200f, 1f); // #252733
            _letterTMP.alignment      = TextAlignmentOptions.Center;
            _letterTMP.sortingOrder   = 6;
            _letterTMP.rectTransform.sizeDelta = new Vector2(2f, 2f);
            _letterTMP.enableWordWrapping = false;
            _letterTMP.overflowMode  = TextOverflowModes.Overflow;

            // No effects — tile sprite already has bevel/shadow baked in

            letterGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // ── Point value — quieter, no shadow ──
            GameObject pointGO = new GameObject("TilePoints");
            pointGO.transform.SetParent(transform, false);
            // Slight push toward the corner (was 0.22, then 0.28 was too aggressive
            // and made subscripts touch the tile edge). 0.25 gives breathing room
            // from the letter without crowding the tile boundary.
            pointGO.transform.localPosition = new Vector3(nativeSize * 0.25f, -nativeSize * 0.25f, -0.1f);

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

            // ── Static drop shadow — child of tile, moves with it, never changes ──
            // Shadow stays UNLIT (default sprite material) so it doesn't interfere with 2D lighting
            GameObject shadowGO = new GameObject("TileShadow");
            shadowGO.transform.SetParent(transform, false);
            float shadowOffset = nativeSize * 0.04f;
            shadowGO.transform.localPosition = new Vector3(shadowOffset * 0.6f, -shadowOffset, 0.1f);
            shadowGO.transform.localScale = Vector3.one;
            SpriteRenderer shadowSR = shadowGO.AddComponent<SpriteRenderer>();
            shadowSR.sprite = _spriteRenderer.sprite;
            shadowSR.color = new Color(0f, 0f, 0f, 0.15f);
            shadowSR.sortingOrder = 4;
            // Keep default unlit material — don't convert to SpriteLit2D
            shadowSR.gameObject.tag = "Untagged"; // mark so ConvertAllSprites skips it

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
            {
                if (_isWild)
                {
                    // Board wild uses wild2@2x sprite (blank). Uncommitted = no text;
                    // committed = chosen letter on top of blank wild sprite. No "?"
                    // glyph anymore (was on legacy white tile, now redundant).
                    bool uncommitted = (letter == '\0' || letter == TileBag.WILD_CHAR);
                    _letterTMP.text = uncommitted ? "" : letter.ToString().ToUpper();
                    _letterTMP.color = WILD_LETTER_COLOR;
                }
                else
                {
                    _letterTMP.text = letter.ToString().ToUpper();
                    _letterTMP.color = new Color(0.145f, 0.153f, 0.200f, 1f);
                }
                // Force visibility — Fake3D or stone may have disabled these
                if (!_isStone)
                {
                    _letterTMP.gameObject.SetActive(true);
                    _letterTMP.enabled = true;
                }
            }

            if (_pointTMP != null)
            {
                if (_isWild)
                {
                    // Corner ★ marker so committed wilds stay identifiable
                    _pointTMP.text = WILD_GLYPH;
                    _pointTMP.color = WILD_LETTER_COLOR;
                }
                else
                {
                    int pts = LetterData.GetPoints(letter);
                    _pointTMP.text = pts > 0 ? pts.ToString() : "";
                    _pointTMP.color = new Color(0.40f, 0.40f, 0.50f, 1f);
                }
                if (!_isStone)
                {
                    _pointTMP.gameObject.SetActive(true);
                    _pointTMP.enabled = true;
                }
            }
        }

        // ---------------------------------------------------------------------------
        // Wild state control
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Mark (or clear) this tile as a wild. Updates visuals to match. The
        /// RulesEngine owns the logical wild flag in RulesCellData.IsWild; this
        /// method keeps the view in sync. Safe to call on pooled/re-used tiles.
        /// </summary>
        public void SetWild(bool active)
        {
            if (_isWild == active) return;
            _isWild = active;
            UpdateLetterDisplay(Letter);
            // Swap sprite to wild2@2x (blank iridescent) on board when wild,
            // or back to normal (or other state-appropriate sprite) when not.
            if (_spriteRenderer != null)
            {
                if (active)
                {
                    _spriteRenderer.sprite = s_spriteWild ?? s_spriteNormal;
                }
                else
                {
                    // Restore to state-appropriate sprite (priority: primed > gold > normal)
                    if (_hasPrimedGlow)
                        _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                    else if (_isGoldBonus)
                        _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                    else
                        _spriteRenderer.sprite = s_spriteNormal;
                }
            }
            // Board-tile halo removed (per playtest) — hand card keeps the halo so
            // the wild reads as special before it's dropped, but once the wild is
            // on the board the purple "?" / resolved-letter color is enough. Hide
            // the halo unconditionally in case one was created previously.
            UpdateWildHalo(false);
        }

        /// <summary>
        /// Create-on-demand halo child that glows behind the wild tile. Additive
        /// blend so it lights up surrounding cells; lower sortingOrder than the
        /// tile so the letter stays readable on top.
        /// </summary>
        private void UpdateWildHalo(bool active)
        {
            if (!active)
            {
                if (_wildHaloGO != null) _wildHaloGO.SetActive(false);
                return;
            }

            if (_wildHaloGO == null)
            {
                // Lazy-load the shared sprite + material once per process
                if (s_wildHaloSprite == null)
                {
                    Texture2D tex = Resources.Load<Texture2D>("Particles/wild_halo");
                    if (tex != null)
                    {
                        s_wildHaloSprite = Sprite.Create(
                            tex, new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                        Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
                        if (addShader == null) addShader = Shader.Find("Sprites/Default");
                        s_wildHaloMaterial = new Material(addShader);
                    }
                }
                if (s_wildHaloSprite == null) return; // asset missing — skip silently

                _wildHaloGO = new GameObject("TileWildHalo");
                _wildHaloGO.transform.SetParent(transform, false);
                _wildHaloGO.transform.localPosition = new Vector3(0f, 0f, 0.3f);
                _wildHaloSR = _wildHaloGO.AddComponent<SpriteRenderer>();
                _wildHaloSR.sprite = s_wildHaloSprite;
                if (s_wildHaloMaterial != null) _wildHaloSR.sharedMaterial = s_wildHaloMaterial;
                _wildHaloSR.sortingOrder = 3; // tiles render at 5, halo renders behind
                // Scale halo to ~1.7× cell footprint for a glow that spills past the tile edges.
                float haloNative = (_wildHaloSR.sprite != null && _wildHaloSR.sprite.bounds.size.x > 0)
                    ? _wildHaloSR.sprite.bounds.size.x : 1f;
                float tileScale = transform.localScale.x;
                float haloScale = (_cellSize * 1.2f) / (haloNative * Mathf.Max(tileScale, 0.01f));
                _wildHaloGO.transform.localScale = new Vector3(haloScale, haloScale, 1f);
                // Same animator as hand cards — slow rotation + breathing + twinkle.
                _wildHaloGO.AddComponent<WildHaloAnimator>();
            }

            _wildHaloGO.SetActive(true);
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
            if (letter == '\0')
            {
                if (_isWild)
                {
                    Letter = '\0';
                    UpdateLetterDisplay(letter);
                    return;
                }
                Debug.LogWarning($"[Tile] SetLetter called with '\\0' at ({Col},{Row}) — rejected to prevent blank tile.");
                return;
            }
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

        private void OnDestroy()
        {
            // Clean up orphaned Fake3D baked renderer (not parented to this tile)
            if (_bakedRenderer != null)
            {
                Destroy(_bakedRenderer.gameObject);
                _bakedRenderer = null;
            }
        }

        /// <summary>
        /// Forces letter and point TMP to be visible. Call periodically to fix
        /// any tiles that lost their text due to Fake3D, stone, or other bugs.
        /// </summary>
        public void RepairLetterVisibility()
        {
            if (_isStone) return; // stones intentionally hide letters
            if (_hasFake3D)
            {
                // Fake3D should have been cleared by now. If the baked renderer
                // is gone but the flag is still set, the coroutine was interrupted.
                // Force recovery so the tile doesn't stay blank forever.
                if (_bakedRenderer == null) ClearFake3D();
                else return; // Fake3D is active and valid — let it manage visibility
            }

            if (_letterTMP != null)
            {
                if (!_letterTMP.gameObject.activeSelf) _letterTMP.gameObject.SetActive(true);
                if (!_letterTMP.enabled) _letterTMP.enabled = true;
                // If text is blank but Letter is valid, re-set it
                if (string.IsNullOrEmpty(_letterTMP.text) && Letter != '\0' && Letter != '#')
                    _letterTMP.text = Letter.ToString().ToUpper();
            }
            if (_pointTMP != null)
            {
                if (!_pointTMP.gameObject.activeSelf) _pointTMP.gameObject.SetActive(true);
                if (!_pointTMP.enabled) _pointTMP.enabled = true;
                if (string.IsNullOrEmpty(_pointTMP.text) && Letter != '\0' && Letter != '#')
                {
                    int pts = LetterData.GetPoints(Letter);
                    _pointTMP.text = pts > 0 ? pts.ToString() : "";
                }
            }
        }

        /// <summary>
        /// Animates this tile falling to targetWorldPos over the given duration.
        /// Used for gravity drops — smooth linear fall, no bounce.
        /// Preserves primed glow throughout the animation.
        /// </summary>
        public void AnimateGravityFall(Vector3 targetWorldPos, float duration, float startDelay = 0f, bool mechanical = false)
        {
            if (_gravityCoroutine != null)
            {
                StopCoroutine(_gravityCoroutine);
                _gravityCoroutine = null;
            }
            _gravityCoroutine = StartCoroutine(GravityFallCoroutine(targetWorldPos, duration, startDelay, mechanical));
        }

        private IEnumerator GravityFallCoroutine(Vector3 target, float duration, float startDelay, bool mechanical)
        {
            IsAnimating = true;

            // Per-column stagger delay — hold start pose until our turn to fall
            if (startDelay > 0f)
            {
                float waited = 0f;
                while (waited < startDelay)
                {
                    waited += Time.deltaTime;
                    yield return null;
                }
            }

            Vector3 start = transform.position;
            // Feel-pass 2026-05-16: floor lowered from 0.20s → 0.12s so short
            // single-cell falls feel snappier (RM/CC reference). Long cascades
            // still get their full duration from the caller.
            float dur = Mathf.Max(duration, 0.12f);
            float elapsed = 0f;

            // Mechanical: constant-speed linear (conveyor/piston feel)
            // Organic: quadratic ease-in (natural gravity)
            while (elapsed < dur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / dur);
                float easedT = mechanical ? t : (t * t);
                transform.position = Vector3.Lerp(start, target, easedT);
                yield return null;
            }

            transform.position = target;
            IsAnimating        = false;
            _gravityCoroutine  = null;

            // Organic lands get a soft squish; mechanical lands hard-stop (machine feel)
            if (!mechanical)
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
        private float _primedStartTime;

        /// <summary>Reset the primed timer so the pulse animation restarts from calm.</summary>
        public void ResetPrimedTimer(float newMaxAge = -1f)
        {
            _primedStartTime = Time.time;
            _heatLevel = 0;
            _fuseRemaining = -1;
            if (newMaxAge > 0f) _primedMaxAge = newMaxAge;
        }
        private float _primedMaxAge = 30f;

        private int _heatLevel = 0;
        private TextMeshPro _fuseTMP; // small countdown number on primed tiles
        private int _fuseRemaining = 0;

        // ── Gold Bonus tile ──────────────────────────────────────────────────

        public static readonly Color GOLD_LOW  = new Color(0.65f, 0.55f, 0.20f, 1f);  // dim gold
        public static readonly Color GOLD_HIGH = new Color(1.0f, 0.88f, 0.35f, 1f);  // bright gold — just under bloom

        public bool IsGoldBonus => _isGoldBonus;
        private Coroutine _goldPulseCoroutine;

        /// <summary>Make this tile a golden bonus tile that pulses and scores 2x.</summary>
        public void SetGoldBonus(bool gold)
        {
            if (_isGoldBonus == gold) return;
            _isGoldBonus = gold;

            if (gold && _spriteRenderer != null)
            {
                GameAudio.Instance?.PlayGoldSpawnNew();
                if (_letterTMP != null)
                    _letterTMP.color = new Color(0.15f, 0.1f, 0f, 1f);
                if (_pointTMP != null)
                    _pointTMP.color = new Color(0.3f, 0.2f, 0f, 0.8f);
                if (_goldPulseCoroutine != null)
                {
                    StopCoroutine(_goldPulseCoroutine);
                    _goldPulseCoroutine = null;
                }
                // Swap sprite to golden_tile@2x (natural gold artwork) instead of
                // tinting the white tile yellow. Color stays white so the sprite
                // renders at its own designed color.
                _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
            else if (!gold && _spriteRenderer != null)
            {
                if (_goldPulseCoroutine != null) { StopCoroutine(_goldPulseCoroutine); _goldPulseCoroutine = null; }
                // Restore normal sprite + default color/letter tints
                _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color = Color.white;
                if (_letterTMP != null)
                    _letterTMP.color = new Color(0.145f, 0.153f, 0.200f, 1f);
                if (_pointTMP != null)
                    _pointTMP.color = new Color(0.4f, 0.4f, 0.45f, 0.85f);
            }
        }

        // ── Swap refill visual — orange tint ──────────────────────────────────
        private static readonly Color SWAP_REFILL_TINT = new Color(1f, 0.7f, 0.2f, 1f);
        public bool IsSwapRefill => _isSwapRefill;

        public void SetSwapRefillVisual(bool active)
        {
            _isSwapRefill = active;
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.color = SWAP_REFILL_TINT;
                if (_letterTMP != null) _letterTMP.color = new Color(0.3f, 0.15f, 0f, 1f);
                if (_pointTMP != null) _pointTMP.color = new Color(0.4f, 0.2f, 0f, 0.8f);
            }
            else
            {
                _spriteRenderer.color = Color.white;
            }
        }

        // ── Edit refill visual — cyan tint ────────────────────────────────────
        private static readonly Color EDIT_REFILL_TINT = new Color(0.2f, 0.9f, 0.95f, 1f);
        public bool IsEditRefill => _isEditRefill;

        public void SetEditRefillVisual(bool active)
        {
            _isEditRefill = active;
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.color = EDIT_REFILL_TINT;
                if (_letterTMP != null) _letterTMP.color = new Color(0f, 0.15f, 0.2f, 1f);
                if (_pointTMP != null) _pointTMP.color = new Color(0f, 0.2f, 0.25f, 0.8f);
            }
            else
            {
                _spriteRenderer.color = Color.white;
            }
        }

        // ── Wild refill visual — purple/rainbow tint ──────────────────────────
        private static readonly Color WILD_REFILL_TINT = new Color(0.75f, 0.4f, 1f, 1f);
        public bool IsWildRefill => _isWildRefill;

        public void SetWildRefillVisual(bool active)
        {
            _isWildRefill = active;
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.color = WILD_REFILL_TINT;
                if (_letterTMP != null) _letterTMP.color = new Color(0.2f, 0.05f, 0.3f, 1f);
                if (_pointTMP != null) _pointTMP.color = new Color(0.3f, 0.1f, 0.4f, 0.8f);
            }
            else
            {
                _spriteRenderer.color = Color.white;
            }
        }

        // ── Stone tile visual — dark grey, no letter ──────────────────────────
        private static readonly Color STONE_TINT = new Color(0.25f, 0.23f, 0.28f, 1f); // much darker — clearly not a normal tile
        public bool IsStone => _isStone;

        public void SetStoneVisual(bool active)
        {
            _isStone = active;
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.color = STONE_TINT;
                // Hide letter entirely — the dark tint is enough to identify stones
                if (_letterTMP != null) _letterTMP.gameObject.SetActive(false);
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
            }
            else
            {
                _spriteRenderer.color = Color.white;
                if (_letterTMP != null)
                {
                    _letterTMP.gameObject.SetActive(true);
                    _letterTMP.color = new Color(0.145f, 0.153f, 0.200f, 1f);
                }
                if (_pointTMP != null)
                {
                    _pointTMP.gameObject.SetActive(true);
                    _pointTMP.color = new Color(0.4f, 0.4f, 0.45f, 0.85f);
                }
            }
        }

        private IEnumerator GoldPulseLoop()
        {
            Vector3 origScale = transform.localScale;
            Vector3 peakScale = origScale * 1.10f;  // same 10% scale bump as FlashHighlight
            float shimmerTimer = 0f;

            while (_isGoldBonus && _spriteRenderer != null)
            {
                // Phase 1: Flash UP (0.05s) — white to gold tint + scale up
                float elapsed = 0f;
                while (elapsed < 0.05f && _isGoldBonus && _spriteRenderer != null)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / 0.05f);
                    t = 1f - (1f - t) * (1f - t); // ease-out
                    _spriteRenderer.color = Color.Lerp(Color.white, GOLD_HIGH, t);
                    transform.localScale = Vector3.Lerp(origScale, peakScale, t);
                    yield return null;
                }

                // Phase 2: Hold at peak (0.10s)
                if (_spriteRenderer != null) _spriteRenderer.color = GOLD_HIGH;
                if (transform != null) transform.localScale = peakScale;
                yield return WaitCache.Get(0.10f);

                // Phase 3: Fade back (0.15s) — gold to white + scale down
                elapsed = 0f;
                while (elapsed < 0.15f && _isGoldBonus && _spriteRenderer != null)
                {
                    elapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(elapsed / 0.15f);
                    t = t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f; // ease-in-out
                    _spriteRenderer.color = Color.Lerp(GOLD_HIGH, Color.white, t);
                    transform.localScale = Vector3.Lerp(peakScale, origScale, t);
                    yield return null;
                }

                if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
                if (transform != null) transform.localScale = origScale;

                // Ambient shimmer — emit 1-2 tiny sparkles
                shimmerTimer += 0.30f; // each cycle is ~0.30s
                if (shimmerTimer >= 0.8f)
                {
                    shimmerTimer = 0f;
                    GameParticles.Instance?.PlayShimmer(transform.position, Random.Range(1, 3));
                }

                // Pause before next pulse
                yield return WaitCache.Get(0.6f);
            }
            _goldPulseCoroutine = null;
        }

        // ── Primed glow ─────────────────────────────────────────────────────

        public void SetPrimedGlow(Color color, bool playFlash = false, int heatLevel = 0, int fuseRemaining = -1, bool isGold = false, float maxAge = 30f)
        {
            bool wasAlreadyPrimed = _hasPrimedGlow;
            int oldFuse = _fuseRemaining;
            _hasPrimedGlow   = true;
            _heatLevel       = heatLevel;
            _fuseRemaining   = fuseRemaining;
            _primedStartTime = Time.time;
            _primedMaxAge    = maxAge;

            // DIAGNOSTIC — kept silent in normal play, uncomment to re-enable.
            // Debug.Log($"[PrimedTile] ({Col},{Row}) fuse {oldFuse}→{fuseRemaining} heat={heatLevel} isGold={isGold} wasAlreadyPrimed={wasAlreadyPrimed}");

            // Final-turn warning — if caller tells us this is the final displayed
            // turn (fuse 0 or 1), shift to HDR red-orange so the player sees
            // "last chance" clearly. Covers fuse=0 ("about to auto-expire") AND
            // fuse=1 ("one turn left") so the danger state is stable through
            // both final frames. Gold primed words stay gold (their color is
            // part of the reward identity).
            Color glowColor;
            if (fuseRemaining >= 0 && fuseRemaining <= 1 && !isGold)
                glowColor = PRIMED_DANGER_GLOW;
            else
                glowColor = isGold ? PRIMED_GOLD_GLOW : PRIMED_GLOW;
            _primedGlowColor = glowColor;
            _currentBorderColor = glowColor;

            if (!_isHighlighted)
                ApplyBorderColor(glowColor);

            // Force the primed sprite swap immediately, regardless of highlight
            // state or coroutine lifecycle. Without this, gold tiles entering
            // primed state didn't show their glow visual until a later frame
            // (Spencer reported 2026-05-19: had to drop another letter first).
            if (_spriteRenderer != null && s_spriteGold != null)
                _spriteRenderer.sprite = s_spriteGold;

            // Start subtle primed idle animation
            if (_primedPulseCoroutine == null)
                _primedPulseCoroutine = StartCoroutine(PrimedPulseLoop());

            // Flash + sparkles when first primed
            if (playFlash && !wasAlreadyPrimed)
            {
                FlashHighlight(glowColor);
                GameParticles.Instance?.PlayPrimed(transform.position);
            }

        }

        private System.Collections.IEnumerator DelayedShineSweep()
        {
            // Wait for squash + flash to fully finish before sweeping
            yield return WaitCache.Get(0.45f);
            yield return ShineSweep();
        }

        /// <summary>Shine sweep — white flash + scale pop to celebrate priming.</summary>
        private System.Collections.IEnumerator ShineSweep()
        {
            if (_spriteRenderer == null) yield break;

            Vector3 baseScale = transform.localScale;

            // Quick scale pop — tile pulses bigger then settles
            float duration = 0.25f;
            float elapsed = 0f;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);

                // White flash — peaks early, fades out
                float flash;
                if (t < 0.25f)
                    flash = (t / 0.25f) * 0.5f;
                else
                    flash = 0.5f * (1f - ((t - 0.25f) / 0.75f));

                // Read current color each frame so we don't fight other coroutines
                Color cur = _spriteRenderer.color;
                _spriteRenderer.color = Color.Lerp(cur, Color.white, flash);

                // Scale pulse — quick pop up then settle
                float scaleMult;
                if (t < 0.2f)
                    scaleMult = 1f + 0.12f * (t / 0.2f);
                else
                    scaleMult = 1.12f - 0.12f * ((t - 0.2f) / 0.8f);

                transform.localScale = baseScale * scaleMult;

                yield return null;
            }

            transform.localScale = baseScale;
            _spriteRenderer.color = Color.white; // tiles are normally white-tinted
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
            // Removed per-tile random stagger + per-tile baseTime. All primed
            // tiles now pulse from a shared clock (Time.time) so a primed word
            // pulses and transitions color as one unit — no more staggered
            // per-tile phase drift during fuse state changes.
            float shimmerTimer = Random.Range(0f, 0.5f); // particle emission can stay desynced

            // Colors: gold base, hot magenta peak
            Color goldTint   = new Color(1f, 0.90f, 0.70f, 1f);   // warm gold
            Color magentaTint = new Color(1f, 0.55f, 0.75f, 1f);   // hot magenta/pink

            // Cache base scale
            float spriteNativeSize = (_spriteRenderer != null && _spriteRenderer.sprite != null)
                ? _spriteRenderer.sprite.bounds.size.x : Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512) / 100f;
            float baseScale = (_cellSize * TILE_DISPLAY_RATIO) / spriteNativeSize;

            // Set the heat-appropriate border sprite immediately
            ApplyHeatSprite();

            while (_hasPrimedGlow && _spriteRenderer != null)
            {
                if (_flashCoroutine != null)
                {
                    yield return null;
                    continue;
                }

                // Use global Time.time as the pulse clock so all primed tiles
                // share the same phase — the word pulses as a unit.
                float baseTime = Time.time;

                // Pulse speed ramps with DROPS REMAINING on this primed word.
                // Fuse is move-based now (April 17) — no more age heat in the
                // visual state machine. Final turn (fuse=1) also gets a color
                // shift to HDR red-orange via SetPrimedGlow.
                //   fuse=1 → +3 (critical, red-orange, 0.18s pulse)
                //   fuse=2 → +2 (urgent)
                //   fuse=3 → +1 (brisk)
                //   fuse>=4 → +0 (calm)
                int dropsUrgency = GetDropsUrgency();
                int effectiveHeat = (_fuseRemaining >= 0)
                    ? dropsUrgency
                    : Mathf.Clamp(_heatLevel, 0, 4);

                // Heat 0: calm (1.2s), 1: brisk (0.8s), 2: fast (0.5s), 3: urgent (0.3s), 4: critical (0.18s)
                // Slowed ~20% per Spencer (was 1.2/0.8/0.5/0.3/0.18). Same urgency curve, just less frenetic.
                float[] periods = { 1.4f, 1.0f, 0.6f, 0.38f, 0.22f };
                float period = periods[Mathf.Clamp(effectiveHeat, 0, 4)];
                float cycle = Mathf.Repeat(baseTime, period) / period;

                // Asymmetric pulse: quick bright pop (30% of cycle), slower dim recovery (70%)
                // Feels like a heartbeat / fuse tick, not an alarm strobe
                float pulse;
                if (cycle < 0.3f)
                    pulse = Mathf.Sin((cycle / 0.3f) * Mathf.PI * 0.5f); // quick rise
                else
                    pulse = Mathf.Cos(((cycle - 0.3f) / 0.7f) * Mathf.PI * 0.5f); // slow fall

                pulse = Mathf.Clamp01(pulse);

                // Face tint — ramps with effective heat (turn heat + time urgency)
                float baseTint = 0.12f + effectiveHeat * 0.06f; // 12% → 36%
                float tintAmount = baseTint + pulse * (0.10f + effectiveHeat * 0.05f);
                tintAmount = Mathf.Max(tintAmount, 0.35f);
                _spriteRenderer.color = Color.Lerp(Color.white, _primedGlowColor, tintAmount);

                // Scale: visible breathe at all levels, punchier at high heat
                float scalePulse = 0.035f + effectiveHeat * 0.015f; // 3.5% → 9.5% scale change
                float scaleMult = 1f + scalePulse * pulse;

                // Tiny jitter at high urgency — nervous energy
                if (effectiveHeat >= 3 && pulse < 0.2f)
                {
                    float jx = Random.Range(-0.005f, 0.005f);
                    float jy = Random.Range(-0.005f, 0.005f);
                    scaleMult += jx; // slight asymmetric wobble
                }

                if (!IsAnimating && _flashCoroutine == null)
                    transform.localScale = new Vector3(baseScale * scaleMult, baseScale * scaleMult, 1f);

                // Shimmer particles — more frequent at higher urgency
                float shimmerInterval = 1.2f - effectiveHeat * 0.20f; // heat 0: 1.2s, heat 4: 0.4s
                shimmerTimer += Time.deltaTime;
                if (shimmerTimer >= shimmerInterval)
                {
                    shimmerTimer = 0f;
                    GameParticles.Instance?.PlayShimmer(transform.position, 1 + _heatLevel / 2);
                }

                // Re-check heat sprite in case _primedGlowColor changed between turns
                ApplyHeatSprite();

                yield return null;
            }

            // Clean up when primed state ends
            if (_spriteRenderer != null)
            {
                _spriteRenderer.color = Color.white;
                _spriteRenderer.sprite = s_spriteNormal;
            }
            _primedPulseCoroutine = null;
        }

        private int GetDropsUrgency()
        {
            // Escalation: calm → brisk → fast → urgent → critical. fuse=0 is
            // the "about to expire" state and hits the top tier (critical —
            // 0.18s period, largest pulse, max jitter). fuse=1 is one step
            // below (urgent — 0.3s) so the final two turns are visibly
            // different, not identical.
            if (_fuseRemaining == 0) return 4;  // critical — frantic, about to expire
            if (_fuseRemaining == 1) return 3;  // urgent — one turn left
            if (_fuseRemaining == 2) return 2;  // fast
            if (_fuseRemaining == 3) return 1;  // brisk
            return 0;                            // calm (fuse >= 4)
        }

        /// <summary>Swap to the correct border sprite based on heat level (thicker = hotter).</summary>
        private void ApplyHeatSprite()
        {
            if (_spriteRenderer == null) return;

            int effectiveHeat = (_fuseRemaining >= 0)
                ? GetDropsUrgency()
                : Mathf.Clamp(_heatLevel, 0, 4);
            switch (effectiveHeat)
            {
                case 0:  _spriteRenderer.sprite = s_spriteGoldThick; break;
                case 1:  _spriteRenderer.sprite = s_spriteHeat1 ?? s_spriteGoldThick; break;
                case 2:  _spriteRenderer.sprite = s_spriteHeat2 ?? s_spriteGoldThick; break;
                default: _spriteRenderer.sprite = s_spriteHeat3 ?? s_spriteGoldThick; break;
            }
        }

        /// <summary>Set sorting order on tile sprite + text layers.</summary>
        public void SetSortingOrder(int order)
        {
            if (_spriteRenderer != null) _spriteRenderer.sortingOrder = order;
            if (_letterTMP != null) _letterTMP.sortingOrder = order + 1;
            if (_pointTMP != null) _pointTMP.sortingOrder = order + 1;
        }

        /// <summary>Brief color flash to highlight a scored word tile.</summary>
        public void FlashHighlight(Color color)
        {
            if (_spriteRenderer == null) return;
            // Cancel any existing flash so they don't overlap
            if (_flashCoroutine != null) StopCoroutine(_flashCoroutine);
            // Start the flash from whatever color we're currently in — do NOT
            // snap to white first. Reverting to white causes a visible flicker
            // when a primed tile changes fuse state.
            _flashCoroutine = StartCoroutine(FlashBorderCoroutine(color));
        }

        private Coroutine _meltdownShakeRoutine;

        /// <summary>
        /// Violent Perlin-noise position jitter for the meltdown windup —
        /// builds tension before the explosion. Magnitude ramps from subtle
        /// at start to violent at the end, mimicking a charging-up effect.
        /// Restores original localPosition when complete.
        /// </summary>
        public void PlayMeltdownShake(float duration)
        {
            if (_meltdownShakeRoutine != null) StopCoroutine(_meltdownShakeRoutine);
            _meltdownShakeRoutine = StartCoroutine(MeltdownShakeCoroutine(duration));
        }

        private System.Collections.IEnumerator MeltdownShakeCoroutine(float duration)
        {
            Vector3 baseLocalPos = transform.localPosition;
            // Per-tile noise seed so tiles shake independently, not in sync.
            float seedX = Random.value * 100f;
            float seedY = Random.value * 100f + 50f;
            const float NOISE_SPEED = 35f;     // Hz of jitter — high for "violent"
            const float MAGNITUDE_START = 0.005f;
            const float MAGNITUDE_PEAK  = 0.07f; // ~10% of a tile width

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / duration;
                // Ease-in curve — magnitude builds tension into the explosion.
                float magnitude = Mathf.Lerp(MAGNITUDE_START, MAGNITUDE_PEAK, t * t);
                float nx = Mathf.PerlinNoise(seedX, elapsed * NOISE_SPEED) * 2f - 1f;
                float ny = Mathf.PerlinNoise(seedY, elapsed * NOISE_SPEED) * 2f - 1f;
                transform.localPosition = baseLocalPos + new Vector3(nx, ny, 0f) * magnitude;
                yield return null;
            }

            transform.localPosition = baseLocalPos;
            _meltdownShakeRoutine = null;
        }


        /// <summary>Force-reset visuals — call to unstick any interrupted flash or sorting boost.</summary>
        public void ResetVisuals()
        {
            if (_flashCoroutine != null) { StopCoroutine(_flashCoroutine); _flashCoroutine = null; }
            if (_spriteRenderer != null)
            {
                // Preserve special tile tints — they stay until cleared
                if (_isStone)
                    _spriteRenderer.color = STONE_TINT;
                else if (_isSwapRefill)
                    _spriteRenderer.color = SWAP_REFILL_TINT;
                else if (_isEditRefill)
                    _spriteRenderer.color = EDIT_REFILL_TINT;
                else if (_isWildRefill)
                    _spriteRenderer.color = WILD_REFILL_TINT;
                else if (!_isGoldBonus)
                    _spriteRenderer.color = Color.white;
            }
            // 2026-06-01: respect aim-mode sortingOrder boost. When a booster
            // is in aim mode, BoosterHUDSlot bumps every tile above the
            // scrim's order so they stay visible inside the cutout. If
            // ResetVisuals (called from various cleanup paths) hard-coded the
            // default 5, the tile would drop BELOW the scrim mid-aim and
            // disappear. AimModeTileOrder is set by BoosterHUDSlot.
            SetSortingOrder(AimModeTileOrder > 0 ? AimModeTileOrder : 5);
            Color border = _hasPrimedGlow ? _primedGlowColor : TILE_BORDER_NORMAL;
            ApplyBorderColor(border);
        }

        private System.Collections.IEnumerator FlashBorderCoroutine(Color color)
        {
            if (_spriteRenderer == null) yield break;

            Color glowColor = color;
            // Snapshot the color we're currently in so the flash lerps FROM
            // our current state, not from a hard-coded white. Fixes the
            // visible white-flicker that used to appear whenever a primed
            // tile's color state changed between fuse turns.
            Color startColor = _spriteRenderer.color;
            // Target settling color: if this tile is still primed, settle to
            // its current pulse state; otherwise back to white (normal tiles).
            Color settleColor = _hasPrimedGlow
                ? Color.Lerp(Color.white, _primedGlowColor, 0.35f)
                : Color.white;

            Vector3 origScale = transform.localScale;
            Vector3 glowScale = origScale * 1.10f;

            // Phase 1: Flash UP (0.05s) — from current color to peak glow
            float elapsed = 0f;
            while (elapsed < 0.05f && _spriteRenderer != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.05f);
                t = 1f - (1f - t) * (1f - t);
                _spriteRenderer.color = Color.Lerp(startColor, glowColor, t);
                transform.localScale = Vector3.Lerp(origScale, glowScale, t);
                yield return null;
            }

            // Phase 2: Hold at peak glow (0.10s)
            if (_spriteRenderer != null) _spriteRenderer.color = glowColor;
            if (transform != null) transform.localScale = glowScale;
            yield return WaitCache.Get(0.10f);

            // Phase 3: Fade back (0.15s) — toward settle color, not white
            elapsed = 0f;
            while (elapsed < 0.15f && _spriteRenderer != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / 0.15f);
                t = t < 0.5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) / 2f;
                _spriteRenderer.color = Color.Lerp(glowColor, settleColor, t);
                transform.localScale = Vector3.Lerp(glowScale, origScale, t);

                yield return null;
            }

            // Final settle — pulse loop (if still primed) will take over from here
            if (_spriteRenderer != null) _spriteRenderer.color = settleColor;
            if (transform != null) transform.localScale = origScale;
            _flashCoroutine = null;
        }

        /// <summary>
        /// Halt the gold-pulse and flash-highlight coroutines (NOT the primed
        /// pulse — use ClearPrimedGlow for that). Caller must write the
        /// desired color to _spriteRenderer.color afterward, otherwise the
        /// tile keeps whatever color it happened to be at when the pulse
        /// stopped. Used by WordDropFX.Tier1PopCoroutine to lock the tile's
        /// preserved color through the squeeze→punch→shatter sequence.
        /// </summary>
        public void StopVisualPulses()
        {
            if (_goldPulseCoroutine != null)
            {
                StopCoroutine(_goldPulseCoroutine);
                _goldPulseCoroutine = null;
            }
            if (_flashCoroutine != null)
            {
                StopCoroutine(_flashCoroutine);
                _flashCoroutine = null;
            }
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

            // Reset any pulse-induced tint/scale — preserve special tile tints
            if (_spriteRenderer != null)
            {
                if (_isStone)
                    _spriteRenderer.color = STONE_TINT;
                else if (_isSwapRefill)
                    _spriteRenderer.color = SWAP_REFILL_TINT;
                else if (_isEditRefill)
                    _spriteRenderer.color = EDIT_REFILL_TINT;
                else if (_isWildRefill)
                    _spriteRenderer.color = WILD_REFILL_TINT;
                else if (!_isGoldBonus)
                    _spriteRenderer.color = Color.white;
                // Restore appropriate sprite based on tile state.
                // Gold-bonus tiles return to their gold sprite, not white.
                _spriteRenderer.sprite = _isGoldBonus
                    ? (s_spriteGolden ?? s_spriteNormal)
                    : s_spriteNormal;
            }

            // Reset scale to correct base
            float sprNative = (_spriteRenderer != null && _spriteRenderer.sprite != null)
                ? _spriteRenderer.sprite.bounds.size.x : Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512) / 100f;
            float correctScale = (_cellSize * TILE_DISPLAY_RATIO) / sprNative;
            transform.localScale = new Vector3(correctScale, correctScale, 1f);

            _heatLevel = 0;
            _fuseRemaining = 0;
            if (_fuseTMP != null) _fuseTMP.gameObject.SetActive(false);
        }

        // ---------------------------------------------------------------------------
        // Public API — Preview Highlight (used by DropPreview)
        // ---------------------------------------------------------------------------

        private bool _hasPreviewHighlight = false;
        private Color _savedBorderBeforePreview;

        /// <summary>Lightweight tint overlay for drop preview. Does not affect primed glow.</summary>
        public void SetPreviewHighlight(Color color)
        {
            if (!_hasPreviewHighlight)
                _savedBorderBeforePreview = _currentBorderColor;
            _hasPreviewHighlight = true;

            // Swap sprite to scored look instead of tinting
            if (_spriteRenderer != null)
            {
                _spriteRenderer.sprite = s_spriteScored ?? s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
        }

        /// <summary>Restore tile to its state before preview highlight.</summary>
        public void ClearPreviewHighlight()
        {
            if (!_hasPreviewHighlight) return;
            _hasPreviewHighlight = false;

            // Swap back to appropriate sprite (canonical priority: scored > primed > gold > wild > normal)
            if (_spriteRenderer != null)
            {
                if (_isShowingScoredSprite)
                    _spriteRenderer.sprite = s_spriteScored ?? s_spriteNormal;
                else if (_hasPrimedGlow)
                    _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                else if (_isGoldBonus)
                    _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                else if (_isWild)
                    _spriteRenderer.sprite = s_spriteWild ?? s_spriteNormal;
                else
                    _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
            ApplyBorderColor(_savedBorderBeforePreview);
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
//                 Debug.Log($"[Tile] ({Col},{Row}) '{Letter}' already has primed glow — keeping.");
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

        /// <summary>
        /// HDR-boosted color sampled from the actual scored-tile texture
        /// (Resources/Tiles/selected_test@2x.png). The green is in the
        /// texture itself (sr.color stays white during the scored state),
        /// so consumers needing "the visible color of a scored tile" — e.g.
        /// WordDropFX.Tier1PopCoroutine for debris tint — should use this.
        /// Sampled once on first access and cached. Multiplied by HDR_BOOST
        /// so the green channel crosses the bloom threshold.
        /// </summary>
        /// <summary>
        /// Average color sampled from the primed-tile texture
        /// (Resources/Tiles/primed_test@2x.png), HDR-boosted so bloom catches
        /// debris tinted with this color. Used by WordDropFX.Tier1PopCoroutine
        /// for debris tint on primed tiles — debris should match the texture
        /// color the player saw before the explosion, not the lerped pulse-
        /// mid color used during the pop animation. Sampled once on first
        /// access and cached.
        /// </summary>
        public static Color PRIMED_TILE_TINT
        {
            get
            {
                if (!s_primedTintSampled)
                {
                    s_primedTintCached = SamplePrimedTileTint();
                    s_primedTintSampled = true;
                }
                return s_primedTintCached;
            }
        }
        private static bool s_primedTintSampled;
        private static Color s_primedTintCached = new Color(0.95f, 0.50f, 0.55f, 1f); // fallback

        private static Color SamplePrimedTileTint()
        {
            const float HDR_BOOST = 1.55f;
            Sprite sp = s_spriteGold ?? Resources.Load<Sprite>("Tiles/primed_test@2x");
            if (sp == null || sp.texture == null || !sp.texture.isReadable)
                return new Color(0.95f * HDR_BOOST, 0.50f * HDR_BOOST, 0.55f * HDR_BOOST, 1f);

            Texture2D tex = sp.texture;
            Rect rect = sp.textureRect;
            float rSum = 0f, gSum = 0f, bSum = 0f;
            int n = 0;
            const int GRID = 5;
            for (int gy = 0; gy < GRID; gy++)
            for (int gx = 0; gx < GRID; gx++)
            {
                float u = 0.2f + (gx / (float)(GRID - 1)) * 0.6f;
                float v = 0.2f + (gy / (float)(GRID - 1)) * 0.6f;
                int px = Mathf.RoundToInt(rect.x + rect.width  * u);
                int py = Mathf.RoundToInt(rect.y + rect.height * v);
                Color c = tex.GetPixel(px, py);
                if (c.a > 0.5f)
                {
                    rSum += c.r;
                    gSum += c.g;
                    bSum += c.b;
                    n++;
                }
            }
            if (n == 0) return new Color(0.95f * HDR_BOOST, 0.50f * HDR_BOOST, 0.55f * HDR_BOOST, 1f);
            return new Color(
                (rSum / n) * HDR_BOOST,
                (gSum / n) * HDR_BOOST,
                (bSum / n) * HDR_BOOST,
                1f);
        }

        /// <summary>
        /// Public accessor for the primed-tile sprite (Resources/Tiles/primed_test@2x.png).
        /// Used by WordDropFX.Tier1PopCoroutine to swap a cascade-formed word's
        /// sprite to the pink-coral primed texture during the cascade preamble
        /// (lit-up-as-primed beat before the explosion). Falls back to lazy
        /// Resources.Load if the sprite cache hasn't built yet.
        /// </summary>
        public static Sprite PrimedSprite =>
            s_spriteGold ?? Resources.Load<Sprite>("Tiles/primed_test@2x");

        /// <summary>
        /// Public accessor for the green "scored" tile sprite — used by
        /// WordDropFX to force the green texture under fragments so the
        /// debris matches the bright kelly-green the player saw.
        /// Falls back to Resources.Load if the sprite cache hasn't built yet.
        /// </summary>
        public static Sprite ScoredSprite =>
            s_spriteScored ?? Resources.Load<Sprite>("Tiles/green_tile2@2x");

        public static Color SCORED_TILE_TINT
        {
            get
            {
                if (!s_scoredTintSampled)
                {
                    s_scoredTintCached = SampleScoredTileTint();
                    s_scoredTintSampled = true;
                }
                return s_scoredTintCached;
            }
        }
        private static bool s_scoredTintSampled;
        private static Color s_scoredTintCached = new Color(0.65f, 1.55f, 0.80f, 1f); // fallback if sample fails

        private static Color SampleScoredTileTint()
        {
            const float HDR_BOOST = 1.55f;
            // s_spriteScored is normally populated by BuildSpriteCache (an
            // instance method called from Tile.Initialise). If it hasn't
            // fired yet — e.g. SCORED_TILE_TINT is queried before any tile
            // initialises — load directly from Resources so sampling can
            // proceed regardless of init order.
            Sprite sp = s_spriteScored ?? Resources.Load<Sprite>("Tiles/green_tile2@2x");
            if (sp == null || sp.texture == null || !sp.texture.isReadable)
                return new Color(0.65f, 1.55f, 0.80f, 1f);

            Texture2D tex = sp.texture;
            Rect rect = sp.textureRect;
            // Sample a 5×5 grid in the central 60% of the sprite, average
            // non-transparent pixels. Edges are skipped to avoid border/
            // anti-aliasing pixels biasing the average.
            float rSum = 0f, gSum = 0f, bSum = 0f;
            int n = 0;
            const int GRID = 5;
            for (int gy = 0; gy < GRID; gy++)
            for (int gx = 0; gx < GRID; gx++)
            {
                float u = 0.2f + (gx / (float)(GRID - 1)) * 0.6f;
                float v = 0.2f + (gy / (float)(GRID - 1)) * 0.6f;
                int px = Mathf.RoundToInt(rect.x + rect.width  * u);
                int py = Mathf.RoundToInt(rect.y + rect.height * v);
                Color c = tex.GetPixel(px, py);
                if (c.a > 0.5f)
                {
                    rSum += c.r;
                    gSum += c.g;
                    bSum += c.b;
                    n++;
                }
            }
            if (n == 0) return new Color(0.65f, 1.55f, 0.80f, 1f);
            return new Color(
                (rSum / n) * HDR_BOOST,
                (gSum / n) * HDR_BOOST,
                (bSum / n) * HDR_BOOST,
                1f);
        }

        private bool _isShowingScoredSprite;
        public bool IsShowingScoredSprite => _isShowingScoredSprite;

        // Sticky "was-scored" flag — set true when SetScoredSprite(true) fires
        // and NEVER cleared by SetScoredSprite(false). Lets WordDropFX.Tier1Pop
        // detect tiles that were part of a recently-scored word even if
        // PlayWordScored's staggered OnComplete already reverted the sprite
        // back to normal before detonation kicks in. Cleared on
        // gameObject.SetActive(false) so pooled tiles start fresh.
        private bool _wasInScoredWord;
        public bool WasInScoredWord => _wasInScoredWord;

        /// <summary>Swap sprite to scored look (tile_selected2) and back.</summary>
        public void SetScoredSprite(bool scored)
        {
            if (_spriteRenderer == null) return;
            _isShowingScoredSprite = scored;
            if (scored) _wasInScoredWord = true;
            if (scored)
            {
                _spriteRenderer.sprite = s_spriteScored ?? s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
            else
            {
                // Priority: primed > gold > normal
                if (_hasPrimedGlow)
                    _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                else if (_isGoldBonus)
                    _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                else
                    _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
        }

        /// <summary>Reset the sticky scored-word flag (called when tile is recycled).</summary>
        public void ClearScoredWordFlag() { _wasInScoredWord = false; }

        /// <summary>Swap sprite to cyan when this tile is the rewrite/edit target,
        /// restore normal sprite when cleared. The cyan color tint applied via
        /// Highlight() + RewritePulseCoroutine still modulates this sprite —
        /// pulse goes from subtle cyan-tile to saturated HDR cyan + bloom.</summary>
        public void SetRewriteTargetSprite(bool active)
        {
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.sprite = s_spriteCyan ?? s_spriteNormal;
            }
            else
            {
                // Restore based on current tile state.
                // Priority: scored > primed > gold > normal
                if (_isShowingScoredSprite)
                    _spriteRenderer.sprite = s_spriteScored ?? s_spriteNormal;
                else if (_hasPrimedGlow)
                    _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                else if (_isGoldBonus)
                    _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                else
                    _spriteRenderer.sprite = s_spriteNormal;
            }
        }

        /// <summary>
        /// Edit-selected visual (Option A). Keeps the tile's own sprite + letter
        /// and layers a cyan additive glow halo BEHIND it, plus a springy
        /// select-pop that settles into a gentle scale + halo breath. This is the
        /// "this tile is selected" treatment premium mobile puzzlers use — motion
        /// + an additive highlight — instead of repainting the whole tile cyan
        /// (which read as a glitch). Fully self-contained: start with true,
        /// clear with false.
        /// </summary>
        public void SetEditSelected(bool active)
        {
            if (active)
            {
                EnsureEditHalo();

                // Capture the tile's true rest scale ONCE per selection — tiles
                // rest at a cell-derived scale, not 1.0, so the pop/breath/exit
                // must all restore to this value.
                if (!_editSelected) _editBaseScale = transform.localScale;
                _editSelected = true;

                // Select-pop → continuous breath that stays slightly enlarged so
                // the tile keeps reading "lifted / selected" the whole time.
                transform.DOKill();
                transform.localScale = _editBaseScale;
                transform.DOScale(_editBaseScale * EDIT_POP_SCALE, EDIT_POP_DUR)
                    .SetEase(Ease.OutBack, 1.7f)
                    .OnComplete(() =>
                    {
                        transform.DOScale(_editBaseScale * EDIT_BREATH_SCALE, EDIT_BREATH_DUR)
                            .SetEase(Ease.InOutSine)
                            .SetLoops(-1, LoopType.Yoyo);
                    });

                // Swap to the authored cyan_tile sprite (bright candy cyan) so the
                // tile itself reads "selected". A multiply-tint on the white tile
                // could only DARKEN it (dull "shaded cyan"); the sprite is the real
                // vibrant cyan. Keep color white so the sprite shows true.
                SetRewriteTargetSprite(true);
                if (_spriteRenderer != null) _spriteRenderer.color = Color.white;

                if (_editHaloGO != null) _editHaloGO.SetActive(true);
                if (_editHaloSR != null)
                {
                    _editHaloSR.DOKill();
                    _editHaloSR.color = new Color(EDIT_HALO_CYAN.r, EDIT_HALO_CYAN.g, EDIT_HALO_CYAN.b, EDIT_HALO_ALPHA_LOW);
                    _editHaloSR.DOFade(EDIT_HALO_ALPHA_HIGH, EDIT_BREATH_DUR)
                        .SetEase(Ease.InOutSine)
                        .SetLoops(-1, LoopType.Yoyo);
                }
            }
            else
            {
                if (!_editSelected) return;
                _editSelected = false;
                transform.DOKill();
                transform.localScale = _editBaseScale;
                // Restore the original sprite + white tint (cleanup also calls
                // ResetVisuals, but keep this self-contained so nothing lingers).
                SetRewriteTargetSprite(false);
                if (_spriteRenderer != null) _spriteRenderer.color = Color.white;
                if (_editHaloSR != null) _editHaloSR.DOKill();
                if (_editHaloGO != null) _editHaloGO.SetActive(false);
            }
        }

        /// <summary>Lazily build the cyan edit halo child using the rounded-square
        /// rim-glow texture (Square_aura_invert — transparent center, glow hugging
        /// the tile silhouette). Behind the tile so the letter stays readable; the
        /// rim spills past the tile edges.</summary>
        private void EnsureEditHalo()
        {
            if (_editHaloGO != null) return;

            if (s_editHaloSprite == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Particles/Square_aura_invert");
                if (tex != null)
                {
                    s_editHaloSprite = Sprite.Create(
                        tex, new Rect(0, 0, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f), 100f);
                    Shader addShader = Shader.Find("WordDrop/AdditiveSprite");
                    if (addShader == null) addShader = Shader.Find("Sprites/Default");
                    s_editHaloMaterial = new Material(addShader);
                }
            }
            if (s_editHaloSprite == null) return; // asset missing — skip silently

            _editHaloGO = new GameObject("TileEditHalo");
            _editHaloGO.transform.SetParent(transform, false);
            _editHaloGO.transform.localPosition = new Vector3(0f, 0f, 0.3f);
            _editHaloSR = _editHaloGO.AddComponent<SpriteRenderer>();
            _editHaloSR.sprite = s_editHaloSprite;
            if (s_editHaloMaterial != null) _editHaloSR.sharedMaterial = s_editHaloMaterial;
            _editHaloSR.sortingOrder = 3; // tiles render at 5 — halo sits behind
            float haloNative = (_editHaloSR.sprite != null && _editHaloSR.sprite.bounds.size.x > 0)
                ? _editHaloSR.sprite.bounds.size.x : 1f;
            float tileScale = transform.localScale.x;
            float haloScale = (_cellSize * EDIT_HALO_SIZE) / (haloNative * Mathf.Max(tileScale, 0.01f));
            _editHaloGO.transform.localScale = new Vector3(haloScale, haloScale, 1f);
            _editHaloGO.SetActive(false);
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

        // SetColorState removed (was for Wordle mode)

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
        private static Sprite s_spriteHeat1;     // warm orange border
        private static Sprite s_spriteHeat2;     // hot orange border
        private static Sprite s_spriteHeat3;     // white-hot border
        private static Sprite s_spriteWhiteThick;
        private static Sprite s_spriteScored;     // tile_selected2 — used when a word is scored
        private static bool s_spriteCacheBuilt;

        // AI tile sprite — loaded from Resources alongside the others
        private static Sprite s_spriteAI;
        // Cyan tile sprite — shown when this tile is the rewrite target
        private static Sprite s_spriteCyan;
        // Golden tile sprite — shown when this tile is a 2x gold bonus tile
        private static Sprite s_spriteGolden;
        // Wild tile sprite — shown when this tile is a wild on the board (wild2@2x, blank)
        private static Sprite s_spriteWild;

        private void BuildSpriteCache()
        {
            if (s_spriteCacheBuilt) return; // already built by another tile
            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512);
            int radius  = texSize / 6;
            int border  = Mathf.Max(3, texSize / 12);

            // Try loading hand-drawn sprites from Resources/Tiles
            Sprite loadedNormal = Resources.Load<Sprite>("Tiles/white5@2x");
            Sprite loadedPrimed = Resources.Load<Sprite>("Tiles/pink_tile@2x");
            Sprite loadedAI     = Resources.Load<Sprite>("Tiles/ai_tile");

            Sprite loadedScored = Resources.Load<Sprite>("Tiles/green_tile2@2x");
            Sprite loadedCyan   = Resources.Load<Sprite>("Tiles/cyan_tile@2x");
            Sprite loadedGolden = Resources.Load<Sprite>("Tiles/golden_tile2@2x");
            Sprite loadedWild   = Resources.Load<Sprite>("Tiles/wild2@2x");
            Debug.Log($"[CyanDebug] cyan_tile@2x loaded? {loadedCyan != null} (name: {(loadedCyan != null ? loadedCyan.name : "NULL")})");

            if (loadedNormal != null)
            {
                // Use hand-drawn sprites
                s_spriteNormal    = loadedNormal;
                s_spriteThick     = loadedNormal;
                s_spriteAI        = loadedAI ?? loadedNormal;
                s_spriteScored    = loadedScored ?? loadedNormal;
                s_spriteCyan      = loadedCyan ?? loadedNormal;
                s_spriteGolden    = loadedGolden ?? loadedNormal;
                s_spriteWild      = loadedWild ?? loadedNormal;

                // Primed states all use the primed sprite (code handles flash/pulse)
                s_spriteGold      = loadedPrimed ?? loadedNormal;
                s_spriteGoldThick = loadedPrimed ?? loadedNormal;
                s_spriteHeat1     = loadedPrimed ?? loadedNormal;
                s_spriteHeat2     = loadedPrimed ?? loadedNormal;
                s_spriteHeat3     = loadedPrimed ?? loadedNormal;
                s_spriteWhiteThick= loadedNormal;

//                 Debug.Log("[Tile] Loaded hand-drawn tile sprites from Resources/Tiles.");
            }
            else
            {
                // Fallback to procedural sprites
                s_spriteNormal    = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_NORMAL, border);
                s_spriteThick     = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, TILE_BORDER_NORMAL, border + 3);
                s_spriteGold      = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, PRIMED_GLOW, border);
                s_spriteGoldThick = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, PRIMED_GLOW, border + 3);
                s_spriteHeat1 = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, PRIMED_GLOW, border + 2);
                s_spriteHeat2 = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, PRIMED_GLOW, border + 4);
                s_spriteHeat3 = TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, PRIMED_GLOW, border + 7);
                s_spriteWhiteThick= TileRenderer.CreateRoundedRect(texSize, texSize, radius, TILE_FILL_COLOR, Color.white, border + 3);
                s_spriteAI        = s_spriteNormal;
                s_spriteCyan      = s_spriteNormal;
                s_spriteGolden    = s_spriteNormal;
                s_spriteWild      = s_spriteNormal;
//                 Debug.Log("[Tile] Fallback: procedural sprite cache built.");
            }

            s_spriteCacheBuilt = true;
        }

        private void ApplyBorderColor(Color borderColor)
        {
            if (_spriteRenderer == null) return;
            bool thick = _isHighlighted || _hasPrimedGlow;

            // Use cached sprite if color matches a known state.
            // Primed glow variants (magenta + danger red-orange) both use the
            // primed sprite family — missing PRIMED_DANGER_GLOW here was the
            // root cause of the white flash on fuse→1 / fuse→0 transitions.
            if (ColorsClose(borderColor, TILE_BORDER_NORMAL))
                _spriteRenderer.sprite = thick ? s_spriteThick : s_spriteNormal;
            else if (ColorsClose(borderColor, TILE_BORDER_GOLD)
                  || ColorsClose(borderColor, PRIMED_GLOW)
                  || ColorsClose(borderColor, PRIMED_DANGER_GLOW)
                  || ColorsClose(borderColor, PRIMED_GOLD_GLOW))
                _spriteRenderer.sprite = thick ? s_spriteGoldThick : s_spriteGold;
            else if (ColorsClose(borderColor, Color.white))
                _spriteRenderer.sprite = s_spriteWhiteThick;
            else if (_hasPrimedGlow)
            {
                // Any other color while primed — keep the primed sprite family,
                // don't fall through to normal sprite (which causes the white flash).
                _spriteRenderer.sprite = thick ? s_spriteGoldThick : s_spriteGold;
            }
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

            // Feel-pass 2026-05-16: cap per-frame dt to 1/30s so a cold-start
            // Time.deltaTime spike (typical after scene load / GC pause) can't
            // skip half the animation. Steady-state frames at ~16ms unaffected.
            const float MAX_DT = 1f / 30f;

            while (elapsed < FALL_DURATION)
            {
                elapsed += Mathf.Min(Time.deltaTime, MAX_DT);
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
                elapsed += Mathf.Min(Time.deltaTime, MAX_DT);
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
            GameAudio.Instance?.PlayTileDrop();
            HapticsManager.TileLand();
        }

        // ---------------------------------------------------------------------------
        // Public API — Landing squish (can be called by any drop path)
        // ---------------------------------------------------------------------------

        private Coroutine _squishCoroutine;

        /// <summary>
        /// Plays the squash-and-stretch landing effect. Call this after the tile
        /// reaches its final position, from any drop path (HandManager, GameVisualBridge, etc).
        /// <paramref name="playSound"/> defaults to true; pass false to do a
        /// silent squish (used by Jester Hat shuffle which squishes many tiles
        /// simultaneously and doesn't want the audio chord from N PlayTileDrop
        /// calls firing at once).
        /// </summary>
        public void PlayLandingSquish(bool playSound = true)
        {
            if (_squishCoroutine != null) StopCoroutine(_squishCoroutine);
            _squishCoroutine = StartCoroutine(LandingSquishCoroutine(playSound));
        }

        /// <summary>Coroutine version for yielding.</summary>
        public IEnumerator PlayLandingSquishAndWait()
        {
            if (_squishCoroutine != null) StopCoroutine(_squishCoroutine);
            _squishCoroutine = StartCoroutine(LandingSquishCoroutine(true));
            yield return _squishCoroutine;
        }

        private IEnumerator LandingSquishCoroutine(bool playSound = true)
        {
            IsAnimating = true;
            Vector3 baseScale = transform.localScale;
            if (playSound) PlayLandSound();

            float squishDur = 0.18f;
            float elapsed = 0f;
            while (elapsed < squishDur)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / squishDur);

                float sx, sy;
                if (t < 0.20f)
                {
                    // Phase 1: quick hard squash — fat and flat
                    float p = t / 0.20f;
                    float ease = p * p;
                    sx = Mathf.Lerp(1f, 1.22f, ease);
                    sy = Mathf.Lerp(1f, 0.82f, ease);
                }
                else if (t < 0.45f)
                {
                    // Phase 2: overshoot tall and narrow — spring back
                    float p = (t - 0.20f) / 0.25f;
                    float ease = Mathf.Sin(p * Mathf.PI * 0.5f);
                    sx = Mathf.Lerp(1.22f, 0.93f, ease);
                    sy = Mathf.Lerp(0.82f, 1.08f, ease);
                }
                else if (t < 0.70f)
                {
                    // Phase 3: small secondary bounce
                    float p = (t - 0.45f) / 0.25f;
                    float ease = Mathf.Sin(p * Mathf.PI * 0.5f);
                    sx = Mathf.Lerp(0.93f, 1.04f, ease);
                    sy = Mathf.Lerp(1.08f, 0.97f, ease);
                }
                else
                {
                    // Phase 4: settle to rest
                    float p = (t - 0.70f) / 0.30f;
                    float ease = 1f - (1f - p) * (1f - p);
                    sx = Mathf.Lerp(1.04f, 1f, ease);
                    sy = Mathf.Lerp(0.97f, 1f, ease);
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
            // 2026-05-28: landing dust puff removed — was too noisy with the
            // new rising-row pop animation. Squish-only landing still fires.
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
