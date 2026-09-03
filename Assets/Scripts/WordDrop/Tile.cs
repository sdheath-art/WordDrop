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
        // 2026-06-24 Spencer: per-letter value-accent tinting was REMOVED (it read as confusing — color the
        // player has to decode). Tiles use a single uniform resting colour again. The _baseTint /
        // ApplyRestingColor plumbing is kept (it just restores the uniform colour) so the state reverts
        // stay centralised in one place.
        private Color _baseTint = Color.white; // uniform resting tile colour
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
        private int _spotlightOrder = -1; // tutorial per-tile spotlight: >=0 raises THIS tile above the dim scrim
        public static bool SpotlightActive = false; // true while a tutorial spotlight/scrim is up (guards FX re-bumps)
        public static readonly Color PRIMED_GOLD_GLOW  = new Color(2.0f, 1.5f, 0.3f, 1f);  // HDR gold — bloom catches this
        // Final-turn warning: primed word has 1 drop left. Shifts to HDR red-orange
        // so the player gets a clear "USE THIS OR LOSE IT" signal on their last chance.
        // 2026-06-03 Spencer: pulled back from (2.2, 0.6, 0.15). At 1.6 the pulse
        // breathes IN and OUT of the bloom (dim part of the cycle sits under the
        // 1.30 line, the peak just crosses it) instead of strobing red-hot the
        // whole time — softer danger flash, still reads as "last chance" at peak.
        public static readonly Color PRIMED_DANGER_GLOW = new Color(1.6f, 0.55f, 0.2f, 1f); // HDR red-orange (pulled back)
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
        private const float TILE_DISPLAY_RATIO = 154f / 172f; // 2026-06-24 Spencer: a hair more spacing (numerator 158→154 → gap 14→18 PSD; board margin scales off the same). Numerator MUST match the hardcoded 154/172 in GridManager (halfGapPad + tileRadiusWorld) and HUDManager baseScale; denom = GridManager.PSD_CELL_PITCH.
        /// <summary>Tile display size as a fraction of the cell pitch — so other systems (e.g. the
        /// drop-preview ghost) can size their tiles to MATCH the board tiles. 2026-06-16 Spencer.</summary>
        public static float DisplayRatio => TILE_DISPLAY_RATIO;

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
        private static Sprite   s_wildGlowSprite;   // 2026-06-03: soft VFX_Glow radial behind the rays
        private GameObject      _wildHaloGO;
        private SpriteRenderer  _wildHaloSR;

        // 2026-06-03 Spencer: procedural HOLOGRAPHIC wild tile — a white base +
        // animated iridescent shader overlay instead of a hand-drawn sprite.
        // Toggle IridescentWild to A/B against s_spriteWild.
        public static bool      IridescentWild = true;
        // 2026-06-03 Spencer: gate the crystal TILE-TINT overlay separately from the
        // aura. false = white "?" tile + rainbow aura only (no crystal shader on the face).
        public static bool      IridescentTileTint = false;
        private GameObject      _iridGO;
        private SpriteRenderer  _iridSR;
        private static Material s_iridMaterial;

        // Edit-selected halo — reuses the wild-halo radial texture, tinted cyan.
        // Option A "selected" treatment: tile keeps its own face; a cyan glow
        // ring breathes behind it + a select-pop reads as "picked".
        private GameObject      _editHaloGO;
        private SpriteRenderer  _editHaloSR;
        private static Sprite   s_editHaloSprite;   // Square_aura_invert — rounded-square rim glow
        private static Material s_editHaloMaterial;
        private Vector3         _editBaseScale = Vector3.one;
        // 2026-06-04 Spencer: the tile's canonical cell-derived rest scale, captured
        // at setup. Edit pop / diffuse pop / exit ALL snap back to this — never to the
        // live transform (which can be mid-animation) so rapid toggling can't compound
        // the tile bigger, and never inferred from the live sprite (which may be the
        // green edit sprite). _restScaleSet guards against a stale Vector3.one default.
        private Vector3         _restScale = Vector3.one;
        private bool            _restScaleSet;
        private bool            _editSelected;
        private static readonly Color EDIT_HALO_CYAN = new Color(0.35f, 0.88f, 0.98f, 1f);
        private const float EDIT_POP_SCALE        = 1.09f; // springy lift on select
        private const float EDIT_BREATH_SCALE     = 1.05f; // stays slightly enlarged while breathing
        private const float EDIT_POP_DUR          = 0.26f;
        private const float EDIT_BREATH_DUR       = 0.85f;
        private const float EDIT_HALO_ALPHA_LOW   = 0.10f;
        private const float EDIT_HALO_ALPHA_HIGH  = 0.26f;
        private const float EDIT_HALO_SIZE        = 1.5f;  // halo footprint as ×cell (rim spills past tile edges)
        // 2026-06-03 Spencer: edit-selected reads as an HDR cyan glow on the tile
        // itself (glow-only, like the hint) — no cyan sprite swap, no halo. On click
        // it SNAPS quickly to the most saturated/glowing cyan ("accessing" feel),
        // then breathes between that peak and a dimmer cyan so it stays lit. Red is
        // pulled way down for saturation; green/blue pushed well past the 1.30 bloom
        // threshold so the peak genuinely blooms. Tune here.
        private static readonly Color EDIT_GLINT_HIGH = new Color(0.24f, 1.06f, 1.26f, 1f); // peak — saturated cyan, blue just UNDER 1.30 bloom (minimal glow)
        private static readonly Color EDIT_GLINT_LOW  = new Color(0.42f, 0.98f, 1.18f, 1f); // breathing trough — still cyan-lit
        private const float EDIT_ACCESS_DUR = 0.13f; // fast snap-to-peak on click
        // 2026-07-09 Spencer: the sr.color glint above is CLAMPED on mobile (linear) so it
        // can't actually bloom — the selected tile read as a flat cyan. Layer a REAL pulsing
        // HDR cyan bloom through the additive _Color overlay (the coin-trail/primed path that
        // DOES bloom on device) so the swap/edit selection obviously breathes a glow. Forced on
        // desktop too so it's unmistakable wherever it's viewed. Well above the 1.30 threshold.
        private static readonly Color EDIT_GLOW_HDR = new Color(0.28f, 1.18f, 1.42f, 1f); // peak bloom — brought down again; sits right around the 1.30 bloom threshold for a soft glow, full-off trough carries the pulse
        private const float EDIT_GLOW_ALPHA_LOW  = 0.0f;  // trough — glow fades fully OFF so it blinks on↔off (not just dims)
        private const float EDIT_GLOW_ALPHA_HIGH = 1.0f;  // peak — full glow
        private const float EDIT_GLOW_DUR        = 0.6f;  // half-cycle of the glow blink (own timing so it's livelier than the face breath)
        private Tween _editGlowPulse;
        private float _editGlowAlpha;

        // ── Glassy sheen highlight (CC-style) — 2026-06-03 Spencer prototype ──
        // A soft white gloss layered OVER the upper portion of every tile (not
        // baked), Screen-blended so it brightens toward a wet highlight without
        // blowing out. Tunables are static so they can be dialed live.
        private GameObject     _glossGO;
        private SpriteRenderer _glossSR;
        private static Sprite   s_glossSprite;
        private static Sprite   s_dropShadowSprite; // 2026-06-04 Spencer: baked tile_shadow2 drop shadow
        private SpriteRenderer  _dropShadowSR;      // this tile's drop-shadow renderer (faded during the fake-3D drop tilt)
        private static Material s_shadowMultiplyMat; // 2026-06-04 Spencer: MULTIPLY blend so the baked shadow blends like its PS layer
        private static Material s_glossMaterial;
        public static bool  GlossEnabled = false; // 2026-06-04 Spencer: off — gloss is baked into the new tile sprite now
        public static float GlossAlpha   = 0.35f;  // Screen-blend white strength (0 = off)
        public static float GlossWidth   = 0.62f;  // sheen width  as fraction of tile
        public static float GlossHeight  = 0.30f;  // sheen height as fraction of tile (flatter = streak)
        public static float GlossY       = 0.20f;  // upward offset as fraction of tile (toward top)

        // ── Inner shadow (CC-style 3D volume) — soft dark gradient on the LOWER
        // tile so it reads as a rounded raised form (top gloss + bottom shadow).
        // Normal alpha-blended dark; sits under the letter, over the face.
        private GameObject     _innerShadowGO;
        private SpriteRenderer _innerShadowSR;
        private static Material s_shadowMaterial;
        private static Sprite   s_shadowSprite; // vertical gradient (dark bottom → clear top)
        public static bool   ShadowEnabled = true; // 2026-06-05 Spencer: ON to test the shadowy.psd board shadow (was off — bevel baked into tile sprite)
        // 2026-06-05 Spencer: BOARD shadow A/B — press \ (handled in GridManager) to flip
        // A↔B live on the board, with a [BoardShadow] log each press. A = old, B = new.
        public static string BoardShadowTexA = "Tiles/test_shadow@2x";
        public static string BoardShadowTexB = "menu psds/shadowy";
        private static bool   s_useBoardShadowB = false; // 2026-06-08 Spencer: default back to A (test_shadow@2x); \ flips to B
        private static Sprite s_dropShadowSpriteA, s_dropShadowSpriteB;
        private static readonly System.Collections.Generic.List<SpriteRenderer> s_boardShadowSRs = new System.Collections.Generic.List<SpriteRenderer>();

        private static Sprite MakeBoardShadow(string resourcePath, float ppu)
        {
            if (string.IsNullOrEmpty(resourcePath)) return null;
            Texture2D t = Resources.Load<Texture2D>(resourcePath);
            if (t == null) return null;
            return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), ppu);
        }

        /// <summary>2026-06-05 Spencer: flips the BOARD drop shadow between variant A (old)
        /// and B (new) live across every tile, and logs which is now active. Called from
        /// GridManager on the \ key.</summary>
        public static void FlipBoardShadow()
        {
            s_useBoardShadowB = !s_useBoardShadowB;
            s_dropShadowSprite = s_useBoardShadowB ? s_dropShadowSpriteB : s_dropShadowSpriteA;
            int n = 0;
            for (int i = 0; i < s_boardShadowSRs.Count; i++)
            {
                var sr = s_boardShadowSRs[i];
                if (sr != null) { sr.sprite = s_dropShadowSprite; n++; }
            }
            Debug.Log($"[BoardShadow] FLIP → active={(s_useBoardShadowB ? "B=" + BoardShadowTexB : "A=" + BoardShadowTexA)} (updated {n} tiles)");
        }

        /// <summary>2026-06-05 Spencer: live board-shadow darkness — driven by the
        /// GridManager Inspector slider each frame (the multiply material is static here).</summary>
        public static void SetBoardShadowStrength(float strength)
        {
            if (s_shadowMultiplyMat != null) s_shadowMultiplyMat.SetFloat("_Strength", strength);
        }
        public static float ShadowAlpha   = 0.30f;  // darkness (0 = off)
        public static float ShadowWidth   = 0.80f;  // shadow width  as fraction of tile (inset to clear rounded corners)
        public static float ShadowHeight  = 0.62f;  // shadow height as fraction of tile
        public static float ShadowY       = -0.16f; // downward offset (toward bottom)
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
        private bool  _isAnchored      = false; // Break-Rocks: fixed rock — resists gravity (visual side, mirrors RulesCellData.IsAnchored)
        private bool  _isVault         = false; // Vaults: anchored objective tile rendered as a treasure chest (distinct from grey stone)
        private int   _vaultRequiredLen = 0;    // chest-tier "key" length — telegraphed by tint (0=regular/white, mid=silver, high=gold)
        private bool  _isFrozen        = false; // ICE objective: tile covered in ice (still a normal matchable letter; thaws on detonation)
        private GameObject     _frostGO;        // frost overlay child (above the letter) — independent of sprite-color repaints
        private SpriteRenderer _frostSR;
        private static Sprite  s_frostSprite;   // shared rounded-rect for the frost overlay

        // -------------------------------------------------------------------
        // Bloom-glow overlay (MOBILE bloom fix, 2026-06-19)
        // On iOS/Metal, SpriteRenderer.color is baked to Color32 and clamps HDR
        // to [0,1], so the primed/scored glows (driven via sr.color) never cross
        // the 1.30 bloom threshold on device — they glow on desktop but not the
        // phone. This additive overlay carries the HDR glow through a material
        // _Color property instead (the same path the coin trail uses, which DOES
        // bloom on device). Mobile-only: desktop keeps its existing sr.color glow
        // untouched. _Color carries HDR rgb + the animated alpha (Blend SrcAlpha One).
        private GameObject     _bloomGlowGO;
        private SpriteRenderer _bloomGlowSR;
        private static Material s_bloomGlowMat;       // shared WordDrop/AdditiveSprite
        private static MaterialPropertyBlock s_bloomGlowMPB;
        private GameObject     _vaultBadgeGO;   // circular chip behind the requirement number (built lazily)
        private SpriteRenderer _vaultBadgeBg;
        private TextMeshPro    _vaultBadgeTMP;  // the "4+"/"5+" number
        private static Sprite  s_badgeCircle;
        private bool  _hasPrimedGlow    = false;
        private Color _primedGlowColor  = new Color(0.812f, 0.812f, 0.863f, 1f);
        private Color _currentBorderColor = new Color(0.812f, 0.812f, 0.863f, 1f);
        // Combo colour escalation (2026-07-27 Spencer): once WordDropFX.PlayExplosion tints this
        // tile by its blast order, the tint is LATCHED so per-turn re-glows (SetPrimedGlow) can't
        // repaint it back to magenta before it pops. Cleared on ClearPrimedGlow/ResetForPool.
        private bool  _hasDetonationColor = false;
        private bool  _detoProbeLogged    = false; // TEMP diagnostic: one pulse-frame log per detonation tint

        private Coroutine _gravityCoroutine;
        private Coroutine _fallCoroutine;
        private Coroutine _flashCoroutine;
        private Coroutine _dissolveCoroutine;

        // 2026-06-04 Spencer: a scored word ALSO primes in the same frame, and
        // SetPrimedGlow's sprite swap + per-frame magenta pulse used to stomp the green
        // scored flash within a frame or two ("flash fires very quickly"). While
        // Time.time < _scoredFlashUntil, the primed VISUAL takeover (sprite swap + pulse
        // color write) is held so the green scored flash plays through first.
        private float _scoredFlashUntil = 0f;
        // After the scored-flash hold expires, the magenta primed visual eases IN over this window
        // (white → magenta) instead of snapping — fixes the choppy scored→primed handoff. 2026-06-23.
        private const float PRIMED_MAGENTA_FADE_IN = 0.14f;
        public void HoldPrimedVisual(float seconds)
        {
            float until = Time.time + seconds;
            if (until > _scoredFlashUntil) _scoredFlashUntil = until;
        }

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
                _baseTint = BaseTintForLetter(letter);   // per-letter value accent
                _spriteRenderer.color = _baseTint;
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
            _restScale = new Vector3(scale, scale, 1f); _restScaleSet = true;
            transform.localScale = _restScale;
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

            _pendingDiffusePop = false; // recycled tile must not carry a pending diffuse-pop into reuse
            if (_hasPrimedGlow) ClearPrimedGlow();
            ClearBloomGlow(); // 2026-06-24: defensive — a non-primed bloom glow (cascade green flash) must NOT leak onto a reused tile
            if (_hasFake3D) ClearFake3D();
            if (_isHighlighted) Highlight(false);
            if (_isGoldBonus) SetGoldBonus(false);
            if (_isVault) SetVaultVisual(false);      // restores chest → normal sprite + cell-fit
            else if (_isDropTargetVisual) { SetDropTargetVisual(false); _isStone = false; } // clear escort-object look
            else if (_isStone) SetStoneVisual(false);
            _isDropTargetVisual = false; // clear escort-object flag on pool reuse
            _isAnchored = false; // clear Break-Rocks anchor on pool reuse
            _vaultRequiredLen = 0; // clear chest-tier tint on pool reuse
            if (_isFrozen) SetFrozenVisual(false); // ICE: clear frost overlay on pool reuse
            if (_isWild) SetWild(false); // also hides the iridescent overlay
            if (_iridGO != null) _iridGO.SetActive(false); // belt-and-suspenders for pool reuse
            if (_isSwapRefill) SetSwapRefillVisual(false);
            if (_isEditRefill) SetEditRefillVisual(false);
            if (_isWildRefill) SetWildRefillVisual(false);
            if (_isWild) SetWild(false);

            IsAnimating = false;
            _externalScaleControl = false;
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
            _hasDetonationColor = false; // combo escalation: recycled tile must not carry a stale blast tint
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
                _spriteRenderer.color = _baseTint;
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
            _restScale = new Vector3(scale, scale, 1f); _restScaleSet = true;
            transform.localScale = _restScale;

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // ── Main letter — TextMeshPro with subtle grounding shadow ──
            GameObject letterGO = new GameObject("TileLetter");
            letterGO.transform.SetParent(transform, false);
            // Centered on tile face (true center, no offset).
            letterGO.transform.localPosition = new Vector3(0f, 0f, -0.1f); // 2026-06-04 Spencer: nudge to re-center Avenir

            _letterTMP = letterGO.AddComponent<TextMeshPro>();
            // Load Fredoka Bold TMP font
            TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) _letterTMP.font = tileFont;
            _letterTMP.text           = "";
            _letterTMP.fontSize       = 6.84f; // 2026-06-05 Spencer: −10% (was 7.6)
            _letterTMP.fontStyle      = FontStyles.Bold;
            _letterTMP.color          = new Color(0.145f, 0.153f, 0.200f, 1f); // #252733
            _letterTMP.alignment      = TextAlignmentOptions.Midline; // Midline (not Center) so a single capital sits visually centered, not high
            _letterTMP.sortingOrder   = 7; // above the gloss sheen (6) so the letter stays crisp
            _letterTMP.rectTransform.sizeDelta = new Vector2(2f, 2f);
            _letterTMP.enableWordWrapping = false;
            _letterTMP.overflowMode  = TextOverflowModes.Overflow;

            // 2026-06-03 Spencer: letter effects REMOVED for now — raw Clarity font,
            // no underlay drop-shadow, no face dilate. Re-enable the block below
            // (UNDERLAY_ON + offsets/softness + _FaceDilate 0.27) to restore them.
            var letterMat = _letterTMP.fontMaterial; // auto-instances from the shared SDF material
            letterMat.DisableKeyword("UNDERLAY_ON");
            letterMat.SetFloat("_FaceDilate", 0.05f); // very slight bolden (no shadow); old was 0.27
            _letterTMP.UpdateMeshPadding();

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
            _pointTMP.sortingOrder   = 7; // above the gloss sheen (6)
            _pointTMP.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
            _pointTMP.enableWordWrapping = false;
            _pointTMP.overflowMode  = TextOverflowModes.Overflow;
            // No shadow on point values — keep them quiet

            pointGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // ── Static drop shadow — ONLY the baked tile_shadowbig. No procedural
            // fallback (2026-06-04 Spencer): if the baked sprite didn't load we render
            // NO shadow rather than the old black silhouette. Stays UNLIT.
            if (s_dropShadowSprite != null)
            {
                GameObject shadowGO = new GameObject("TileShadow");
                shadowGO.transform.SetParent(transform, false);
                shadowGO.transform.localPosition = new Vector3(0f, 0f, 0.1f);
                shadowGO.transform.localScale = Vector3.one;
                SpriteRenderer shadowSR = shadowGO.AddComponent<SpriteRenderer>();
                _dropShadowSR = shadowSR;
                shadowSR.sprite = s_dropShadowSprite; // baked art defines spread/darkness
                shadowSR.color = Color.white;
                // MULTIPLY blend so it darkens the board like the PS Multiply layer
                // (alpha carries the layer opacity). Default alpha-blend looked flat/gray.
                if (s_shadowMultiplyMat == null)
                {
                    Shader sh = Shader.Find("WordDrop/MultiplySprite");
                    if (sh != null)
                    {
                        s_shadowMultiplyMat = new Material(sh);
                        s_shadowMultiplyMat.SetFloat("_Strength", 0.48f); // dial back darkness (2026-06-04 Spencer)
                    }
                }
                if (s_shadowMultiplyMat != null) shadowSR.sharedMaterial = s_shadowMultiplyMat;
                shadowSR.sortingOrder = 4;
                shadowSR.gameObject.tag = "Untagged"; // skip 2D-light conversion
                s_boardShadowSRs.Add(shadowSR); // register for the \ A/B flip
            }

            // ── Glassy sheen highlight (CC-style) — soft white gloss over the
            // upper portion of the tile face. Screen-blended (brightens toward a
            // wet highlight, no harsh additive blow-out). Created once; rides the
            // tile through pooling. 2026-06-03 Spencer prototype.
            if (GlossEnabled)
            {
                if (s_glossSprite == null)
                {
                    Texture2D gtex = Resources.Load<Texture2D>("Particles/vfx_glow")
                                  ?? Resources.Load<Texture2D>("Particles/soft_circle")
                                  ?? Resources.Load<Texture2D>("Particles/glow");
                    if (gtex != null)
                        s_glossSprite = Sprite.Create(gtex, new Rect(0, 0, gtex.width, gtex.height), new Vector2(0.5f, 0.5f), 100f);
                    Shader scr = Shader.Find("WordDrop/ScreenSprite")
                              ?? Shader.Find("WordDrop/AdditiveSprite")
                              ?? Shader.Find("Sprites/Default");
                    s_glossMaterial = new Material(scr);
                }
                if (s_glossSprite != null)
                {
                    _glossGO = new GameObject("TileGloss");
                    _glossGO.transform.SetParent(transform, false);
                    // world y offset = nativeSize*GlossY*scale = displaySize*GlossY (upper).
                    // z = -0.05 sits in front of the face, behind the letter (-0.1).
                    _glossGO.transform.localPosition = new Vector3(0f, nativeSize * GlossY, -0.05f);
                    _glossSR = _glossGO.AddComponent<SpriteRenderer>();
                    _glossSR.sprite = s_glossSprite;
                    if (s_glossMaterial != null) _glossSR.sharedMaterial = s_glossMaterial;
                    _glossSR.color = new Color(1f, 1f, 1f, GlossAlpha);
                    _glossSR.sortingOrder = 6; // above tile face (5); SetSortingOrder keeps it at order+1
                    float gNative = (_glossSR.sprite != null && _glossSR.sprite.bounds.size.x > 0f)
                        ? _glossSR.sprite.bounds.size.x : 1f;
                    // Ellipse: same world-size formula as the edit halo — world size
                    // = displaySize × fraction, counter-scaled for the parent.
                    float gx = (displaySize * GlossWidth)  / (gNative * Mathf.Max(scale, 0.01f));
                    float gy = (displaySize * GlossHeight) / (gNative * Mathf.Max(scale, 0.01f));
                    _glossGO.transform.localScale = new Vector3(gx, gy, 1f);
                    _glossGO.tag = "Untagged"; // skip 2D-light conversion (stays unlit)
                }
            }

            // ── Inner shadow (bottom) — a VERTICAL GRADIENT (dark at the bottom
            // edge, fading clear toward the top) on the lower tile for rounded-
            // button volume. A gradient hugs the bottom edge instead of blobbing
            // at the centre like a radial did. Tinted black + alpha-blended,
            // sitting over the face and under the letter. 2026-06-03 Spencer.
            if (ShadowEnabled)
            {
                if (s_shadowSprite == null) s_shadowSprite = BuildVerticalGradientSprite();
                if (s_shadowMaterial == null)
                    s_shadowMaterial = new Material(Shader.Find("Sprites/Default"));
                _innerShadowGO = new GameObject("TileInnerShadow");
                _innerShadowGO.transform.SetParent(transform, false);
                // z = -0.04 sits just in front of the face, behind the letter (-0.1).
                _innerShadowGO.transform.localPosition = new Vector3(0f, nativeSize * ShadowY, -0.04f);
                _innerShadowSR = _innerShadowGO.AddComponent<SpriteRenderer>();
                _innerShadowSR.sprite = s_shadowSprite; // vertical gradient, tinted dark
                if (s_shadowMaterial != null) _innerShadowSR.sharedMaterial = s_shadowMaterial;
                _innerShadowSR.color = new Color(0f, 0f, 0f, ShadowAlpha);
                _innerShadowSR.sortingOrder = 6; // with the gloss (face+1), under the letter (face+2)
                float sNative = (_innerShadowSR.sprite != null && _innerShadowSR.sprite.bounds.size.x > 0f)
                    ? _innerShadowSR.sprite.bounds.size.x : 1f;
                float sx = (displaySize * ShadowWidth)  / (sNative * Mathf.Max(scale, 0.01f));
                float sy = (displaySize * ShadowHeight) / (sNative * Mathf.Max(scale, 0.01f));
                _innerShadowGO.transform.localScale = new Vector3(sx, sy, 1f);
                _innerShadowGO.tag = "Untagged"; // skip 2D-light conversion (stays unlit)
            }

        }

        /// <summary>Builds a 1×N vertical-gradient sprite: white RGB, alpha = full at
        /// the BOTTOM row fading to 0 at the top (squared falloff so the dark
        /// concentrates near the bottom edge). Tinted black + alpha-blended, it
        /// reads as a bottom inner-shadow band, not a centre blob. Built once.</summary>
        private static Sprite BuildVerticalGradientSprite()
        {
            const int W = 48, H = 128;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                float t = y / (float)(H - 1);   // 0 at bottom row, 1 at top (Unity tex y=0 is bottom)
                float av = 1f - t;              // dark at bottom, clear at top
                av = av * av;                   // square — push the darkness toward the very bottom
                for (int x = 0; x < W; x++)
                {
                    float u = x / (float)(W - 1);
                    // Horizontal feather — taper the outer ~18% so the band fades
                    // out at the sides instead of cutting off hard.
                    float hx = Mathf.SmoothStep(0f, 0.18f, u) * Mathf.SmoothStep(0f, 0.18f, 1f - u);
                    byte alpha = (byte)(Mathf.Clamp01(av * hx) * 255f);
                    px[y * W + x] = new Color32(255, 255, 255, alpha);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
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
                // 2026-06-04 Spencer: a wild that lands WITHOUT forming a word stays
                // unresolved — its cell letter is a sentinel ('*' WILD_CHAR, '\0', or
                // '?'). NEVER render a sentinel as a literal glyph: '*'/'?' printed
                // raw, and '\0' showed a missing-glyph box ("weird text"). Blank them
                // defensively on EVERY path (wild or not) so no garbage ever shows.
                bool sentinel = (letter == '\0' || letter == TileBag.WILD_CHAR || letter == '?');
                if (_isWild)
                {
                    // Board wild uses wild2@2x sprite (blank). Uncommitted = no text;
                    // committed = chosen letter on top of blank wild sprite. No "?"
                    // glyph anymore (was on legacy white tile, now redundant).
                    _letterTMP.text = sentinel ? "" : letter.ToString().ToUpper();
                    // 2026-06-03 Spencer: black letter (same as normal tiles) — the pink
                    // WILD_LETTER_COLOR was unreadable on the magenta primed tile.
                    _letterTMP.color = new Color(0.145f, 0.153f, 0.200f, 1f);
                }
                else
                {
                    _letterTMP.text = sentinel ? "" : letter.ToString().ToUpper();
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
                    // 2026-06-03 Spencer: no corner "?" marker (it cluttered the tile).
                    _pointTMP.text = "";
                    _pointTMP.color = WILD_LETTER_COLOR;
                }
                else
                {
                    int pts = LetterData.GetPoints(letter);
                    _pointTMP.text = ""; // point values removed — cleaner RM/CC tiles; score still tallies under the hood
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
                    if (IridescentWild)
                    {
                        // White base + (optional) crystal overlay. The rainbow aura is
                        // independent (UpdateWildHalo below). IridescentTileTint gates
                        // just the crystal tint on the face.
                        _spriteRenderer.sprite = s_spriteNormal;
                        _spriteRenderer.color  = Color.white;
                        UpdateIridescent(IridescentTileTint);
                    }
                    else
                    {
                        _spriteRenderer.sprite = s_spriteWild ?? s_spriteNormal;
                        UpdateIridescent(false);
                    }
                }
                else
                {
                    UpdateIridescent(false);
                    // Restore to state-appropriate sprite (priority: primed > gold > normal)
                    if (_hasPrimedGlow)
                        _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                    else if (_isGoldBonus)
                        _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                    else
                        _spriteRenderer.sprite = s_spriteNormal;
                }
            }
            // 2026-06-03 Spencer: NO rainbow aura on the board wild — the rays+glow
            // live on the HOLDER card only (HandManager). Once dropped, the board
            // tile is just the white "?" (its wild identity reads from the "?").
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
                    Texture2D tex = Resources.Load<Texture2D>("Particles/vfx_rays_sharp"); // 2026-06-03 Spencer: ray-burst aura
                    if (tex != null)
                    {
                        s_wildHaloSprite = Sprite.Create(
                            tex, new Rect(0, 0, tex.width, tex.height),
                            new Vector2(0.5f, 0.5f), 100f);
                        // Rainbow aura material (2026-06-03 Spencer). Falls back to
                        // plain additive if the aura shader is missing.
                        Shader addShader = Shader.Find("WordDrop/IridescentAura")
                                        ?? Shader.Find("WordDrop/AdditiveSprite")
                                        ?? Shader.Find("Sprites/Default");
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
                float haloScale = (_cellSize * 1.8f) / (haloNative * Mathf.Max(tileScale, 0.01f)); // rays spill past edges
                _wildHaloGO.transform.localScale = new Vector3(haloScale, haloScale, 1f);
                // Same animator as hand cards — slow rotation + breathing + twinkle.
                _wildHaloGO.AddComponent<WildHaloAnimator>();

                // Second aura layer — a soft VFX_Glow radial behind the rays for a
                // fuller, rounder glow. Child of the rays GO so it toggles + cleans
                // up with it (no extra wiring). 2026-06-03 Spencer.
                if (s_wildGlowSprite == null)
                {
                    Texture2D gtex = Resources.Load<Texture2D>("Particles/vfx_glow");
                    if (gtex != null)
                        s_wildGlowSprite = Sprite.Create(gtex, new Rect(0, 0, gtex.width, gtex.height),
                                                         new Vector2(0.5f, 0.5f), 100f);
                }
                if (s_wildGlowSprite != null)
                {
                    var glowGO = new GameObject("WildGlow");
                    glowGO.transform.SetParent(_wildHaloGO.transform, false);
                    glowGO.transform.localPosition = new Vector3(0f, 0f, 0.05f); // just behind the rays
                    var glowSR = glowGO.AddComponent<SpriteRenderer>();
                    glowSR.sprite = s_wildGlowSprite;
                    if (s_wildHaloMaterial != null) glowSR.sharedMaterial = s_wildHaloMaterial; // same rainbow additive
                    glowSR.sortingOrder = 2; // behind the rays (3)
                    glowSR.color = new Color(1f, 1f, 1f, 0.85f);
                    float glowNative = (glowSR.sprite != null && glowSR.sprite.bounds.size.x > 0)
                        ? glowSR.sprite.bounds.size.x : 1f;
                    // Counter the parent's scale so the glow is ~0.85× the rays footprint.
                    glowGO.transform.localScale = Vector3.one * ((haloNative / glowNative) * 0.85f);
                }
            }

            _wildHaloGO.SetActive(true);
        }

        /// <summary>Procedural holographic overlay for the wild tile — fills the tile
        /// face with the animated iridescent shader (masked to the white tile shape),
        /// sitting over the face and under the gloss/letter. 2026-06-03 Spencer.</summary>
        private void UpdateIridescent(bool active)
        {
            if (!active)
            {
                if (_iridGO != null) _iridGO.SetActive(false);
                return;
            }
            if (_iridGO == null)
            {
                if (s_iridMaterial == null)
                {
                    Shader ish = Shader.Find("WordDrop/IridescentTile")
                              ?? Shader.Find("WordDrop/IridescentBubble")
                              ?? Shader.Find("Sprites/Default");
                    s_iridMaterial = new Material(ish);
                }
                _iridGO = new GameObject("TileIridescent");
                _iridGO.transform.SetParent(transform, false);
                // z = -0.03 in front of the face (0), behind the gloss (-0.05) and letter (-0.1).
                _iridGO.transform.localPosition = new Vector3(0f, 0f, -0.03f);
                _iridSR = _iridGO.AddComponent<SpriteRenderer>();
                if (s_iridMaterial != null) _iridSR.sharedMaterial = s_iridMaterial;
                _iridSR.sortingOrder = 6; // over the white face (5), under the letter (7)
                _iridGO.transform.localScale = Vector3.one; // matches the tile face
                _iridGO.tag = "Untagged";
            }
            // Mask to the white rounded-rect tile shape (its alpha = the tile outline).
            _iridSR.sprite = s_spriteNormal;
            _iridGO.SetActive(true);
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
            // 2026-06-04 Spencer: treat ALL wild sentinels ('\0', '*' WILD_CHAR, '?')
            // the same — an unresolved wild used to slip '*'/'?' through here and print
            // the raw glyph on the tile. On a wild tile, keep it (display blanks it);
            // on a normal tile, reject so we never stamp a sentinel as a real letter.
            if (letter == '\0' || letter == TileBag.WILD_CHAR || letter == '?')
            {
                if (_isWild)
                {
                    Letter = letter;
                    UpdateLetterDisplay(letter);
                    return;
                }
                Debug.LogWarning($"[Tile] SetLetter called with sentinel '{(letter == '\0' ? "\\0" : letter.ToString())}' at ({Col},{Row}) on a non-wild tile — rejected to prevent garbage glyph.");
                return;
            }
            Letter = letter;
            UpdateLetterDisplay(letter);
            RefreshBaseTint(); // letter changed (rewrite/swap) → recompute the value tint
            if (!_isShowingScoredSprite && !_hasPrimedGlow && !_isGoldBonus && !_isStone
                && !_isFrozen && !_isWild && !_isSwapRefill && !_isEditRefill && !_isWildRefill)
                ApplyRestingColor(); // repaint only if the tile is in its plain resting state

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
            // Kill the edit/swap glow pulse — it's a virtual tween with no Unity target,
            // so DOTween safe-mode won't auto-null it; left running it would fire its
            // setter on this destroyed tile. 2026-07-09 Spencer.
            _editGlowPulse?.Kill();
            _editGlowPulse = null;

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
                // If text is blank but Letter is valid, re-set it. 2026-06-04 Spencer:
                // exclude wild sentinels ('*'/'?') too — this "re-set if blank" path was
                // re-stamping the asterisk back onto an unresolved wild right after the
                // display guard blanked it.
                if (string.IsNullOrEmpty(_letterTMP.text) && Letter != '\0' && Letter != '#'
                    && Letter != TileBag.WILD_CHAR && Letter != '?')
                    _letterTMP.text = Letter.ToString().ToUpper();
            }
            if (_pointTMP != null)
            {
                if (!_pointTMP.gameObject.activeSelf) _pointTMP.gameObject.SetActive(true);
                if (!_pointTMP.enabled) _pointTMP.enabled = true;
                if (string.IsNullOrEmpty(_pointTMP.text) && Letter != '\0' && Letter != '#')
                {
                    int pts = LetterData.GetPoints(Letter);
                    _pointTMP.text = ""; // point values removed — cleaner RM/CC tiles; score still tallies under the hood
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
        // 2026-06-03 Spencer: when true, the primed pulse keeps updating COLOUR but
        // stops writing transform.localScale, so an external animation (the tier-1
        // explosion shrink) can drive the scale while the tile holds its exact
        // primed colour all the way down. Reset to false on pool reuse.
        private bool _externalScaleControl;
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
        // Default -1 (= "no fuse yet" → CALM), NOT 0. 0 maps to GetDropsUrgency()==4 (critical),
        // so a fresh tile pulsed at max heat for the window before SetPrimedGlow set the real fuse,
        // then "diffused" to calm. -1 matches ResetPrimedTimer's no-fuse value. 2026-06-10.
        private int _fuseRemaining = -1;

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
                ApplyRestingColor();
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
                ApplyRestingColor();
            }
        }

        // ── Edit refill visual — cyan tint ────────────────────────────────────
        private static readonly Color EDIT_REFILL_TINT = new Color(0.2f, 0.9f, 0.95f, 1f);
        // ── Scored "word made" tint (2026-06-10 Spencer): green the CURRENT tile instead of
        // swapping to a separate green sprite, so the scored flash keeps the live tile's shape
        // (same approach as the edit/swap refill tints). Multiplies the light tile → kelly green.
        public static readonly Color SCORED_TINT = new Color(0.44f, 1.0f, 0.11f, 1f); // #57C515 hue at full brightness — bright/saturated/happy on the shaded tile (Spencer 2026-06-16; flat #57C515 read too dark once tinted)
        // Saturated HDR green for the CASCADE bloom flash — G pushed well past the 1.30 bloom line so the
        // additive overlay glows hard and the flash is unmissable. 2026-06-24 Spencer.
        public static readonly Color SCORED_GLOW_HDR = new Color(0.30f, 2.6f, 0.18f, 1f);
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
                ApplyRestingColor();
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
                ApplyRestingColor();
            }
        }

        // ── Value-accent resting colour (2026-06-24 Spencer) ──────────────────
        private static Color BaseTintForLetter(char letter) => Color.white; // value-accent tinting removed 2026-06-24 (kept as the single resting colour)
        /// <summary>Recompute the per-letter value tint (call when the letter is set/changed).</summary>
        private void RefreshBaseTint() => _baseTint = BaseTintForLetter(Letter);
        /// <summary>Paint the tile face its resting value tint. Used by every "return to normal" revert
        /// so the tint survives scored/primed/gold/refill states (replaces the old hard-coded white).</summary>
        private void ApplyRestingColor()
        {
            if (_spriteRenderer != null) _spriteRenderer.color = _baseTint;
        }

        // ── Stone tile visual — dark grey, no letter ──────────────────────────
        private static readonly Color STONE_TINT = new Color(0.46f, 0.46f, 0.49f, 1f); // neutral stone grey (was dark purple-grey)
        public bool IsStone => _isStone;

        // ── Break-Rocks: anchored flag (visual mirror of RulesCellData.IsAnchored). ──
        // An anchored tile resists gravity — GridManager.ApplyGravity() keeps it at its
        // found row and compacts other tiles around it (matches ApplyGravityInData).
        public bool IsAnchored => _isAnchored;
        public void SetAnchored(bool active) => _isAnchored = active;

        public void SetStoneVisual(bool active)
        {
            if (active && _isVault) { SetVaultVisual(true); return; } // vaults render as the chest, never grey
            if (active && _isDropTargetVisual) { SetDropTargetVisual(true); return; } // escort objects are NOT grey rocks
            _isStone = active;
            if (_spriteRenderer == null) return;
            if (active)
            {
                _spriteRenderer.color = STONE_TINT;
                // Hide letter entirely — the dark tint is enough to identify stones
                if (_letterTMP != null) _letterTMP.gameObject.SetActive(false);
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
                // Stones are matte obstacles — no glassy sheen / inner shadow.
                if (_glossGO != null) _glossGO.SetActive(false);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
            }
            else
            {
                _spriteRenderer.color = Color.white;
                // Restore the glass treatment when the tile leaves stone state
                // (also covers pool reuse via ResetForPool → SetStoneVisual(false)).
                if (_glossGO != null) _glossGO.SetActive(true);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(true);
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

        // ── HeroWord ESCORT-OBJECT visual — must NOT look like a grey rock. Distinct vivid AMBER
        // collectible tint (placeholder; drop a sprite at Resources/Tiles/Icon_DropTarget to upgrade
        // it like the vault chest and I'll wire it). Keeps stone DATA behavior (non-matchable,
        // survives detonation) but reads as the prize you're escorting down. 2026-06-15 Spencer.
        private static readonly Color DROP_TARGET_TINT = new Color(1f, 0.48f, 0f, 1f); // bright saturated orange (Spencer 2026-06-15)
        private bool _isDropTargetVisual;
        public bool  IsDropTargetVisual => _isDropTargetVisual;
        // PLACEHOLDER: the escort drop-target renders the chicken sprite instead of an orange tile.
        // 2026-06-19 Spencer.
        public static Sprite ChickenSprite => GetChickenSprite(); // exposed for the escort fly-up
        private static Sprite s_chickenSprite; private static bool s_chickenTried;
        private static Sprite GetChickenSprite()
        {
            if (s_chickenTried) return s_chickenSprite;
            s_chickenTried = true;
            s_chickenSprite = Resources.Load<Sprite>("Tiles/common_icon_chicken");
            if (s_chickenSprite == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/common_icon_chicken");
                if (tex != null) s_chickenSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return s_chickenSprite;
        }

        public void SetDropTargetVisual(bool active)
        {
            _isDropTargetVisual = active;
            _isStone = active; // keep stone data-guards (hidden letter, survives detonation, non-matchable)
            if (_spriteRenderer == null) return;
            if (active)
            {
                var chick = GetChickenSprite();
                if (chick != null)
                {
                    if (_spriteRenderer.sprite != chick) { _spriteRenderer.sprite = chick; RefitToSprite(chick); } // swap+fit once
                    _spriteRenderer.color = Color.white;
                }
                else _spriteRenderer.color = DROP_TARGET_TINT; // fallback orange if the chicken can't load
                if (_letterTMP != null) _letterTMP.gameObject.SetActive(false);
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
                if (_glossGO != null) _glossGO.SetActive(false);        // chicken placeholder — no tile sheen
                if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
                if (_dropShadowSR != null) _dropShadowSR.enabled = false; // no tile-shaped drop shadow behind the chicken
            }
            else
            {
                Sprite normal = (OwnerIndex == 1 && s_spriteAI != null) ? s_spriteAI : s_spriteNormal;
                if (normal != null && _spriteRenderer.sprite != normal) { _spriteRenderer.sprite = normal; RefitToSprite(normal); }
                _spriteRenderer.color = Color.white;
                if (_glossGO != null) _glossGO.SetActive(true);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(true);
                if (_dropShadowSR != null) _dropShadowSR.enabled = true;
            }
        }

        // ── Treasure Vault visual (Break-Rocks → Vaults) — chest sprite, no letter ──
        public bool IsVault => _isVault;
        public int  VaultRequiredLen => _vaultRequiredLen; // chest tier key (0=regular, mid≈4, high≥5) — for reward coins
        /// <summary>Telegraph the chest's tier by its required word-length key: regular (0) = normal
        /// chest, mid = silver, high (≥5) = gold (the jackpot). Set before SetVaultVisual. 2026-06-12.</summary>
        public void SetVaultRequirement(int requiredLen) => _vaultRequiredLen = requiredLen;
        private static readonly Color VAULT_MID_TINT = new Color(0.80f, 0.86f, 0.95f, 1f); // cool silver
        private Color VaultTint => _vaultRequiredLen <= 0 ? Color.white
                                 : _vaultRequiredLen >= 5 ? GOLD_HIGH        // high tier = gold jackpot
                                 : VAULT_MID_TINT;                            // mid tier = silver

        // Build the badge chip lazily: a dark circular chip + a small white number, both children
        // of the tile. Mirrors the booster count-chips. 2026-06-15.
        private void EnsureVaultBadge()
        {
            if (_vaultBadgeGO != null) return;
            if (s_badgeCircle == null)
                s_badgeCircle = TileRenderer.CreateSolidRoundedRect(64, 64, 32, Color.white); // filled circle

            _vaultBadgeGO = new GameObject("VaultBadge");
            _vaultBadgeGO.transform.SetParent(transform, false);
            _vaultBadgeGO.transform.localPosition = new Vector3(0f, 0f, -0.11f);
            _vaultBadgeBg = _vaultBadgeGO.AddComponent<SpriteRenderer>();
            _vaultBadgeBg.sprite = s_badgeCircle;
            _vaultBadgeBg.color = new Color(0.12f, 0.10f, 0.22f, 0.96f); // dark navy chip (matches HUD)
            _vaultBadgeBg.sortingOrder = 8; // above chest face(5)/gloss(6)/letter(7)
            _vaultBadgeGO.tag = "Untagged";

            // Number — a DIRECT child of the tile (not the scaled chip) so its size is predictable.
            var numGO = new GameObject("VaultBadgeNum");
            numGO.transform.SetParent(transform, false);
            numGO.transform.localPosition = new Vector3(0f, 0f, -0.12f);
            _vaultBadgeTMP = numGO.AddComponent<TextMeshPro>();
            var f = GameFont.GetTMP(); if (f != null) _vaultBadgeTMP.font = f;
            _vaultBadgeTMP.fontSize          = 3.1f; // small (Spencer)
            _vaultBadgeTMP.characterSpacing  = -10f; // pull the number + "+" together (close the glyph gap)
            _vaultBadgeTMP.fontStyle         = FontStyles.Bold;
            _vaultBadgeTMP.color             = Color.white;
            _vaultBadgeTMP.alignment         = TextAlignmentOptions.Center;
            _vaultBadgeTMP.enableWordWrapping = false;
            _vaultBadgeTMP.overflowMode      = TextOverflowModes.Overflow;
            _vaultBadgeTMP.rectTransform.sizeDelta = new Vector2(4f, 4f);
            _vaultBadgeTMP.sortingOrder      = 9;
        }

        // Telegraph the requirement as a small "4+"/"5+" number centered in a circular chip — mid/
        // high chests only; regular chests show nothing. The chest always hides its own letter. 2026-06-15.
        private void ApplyVaultBadge()
        {
            if (_letterTMP != null) _letterTMP.gameObject.SetActive(false);
            if (_vaultRequiredLen <= 0) { HideVaultBadge(); return; }

            EnsureVaultBadge();
            // Chip ≈ 0.52× the cell — gloss-style counter-scale so it's the same world size
            // regardless of the chest sprite's bounds.
            float displaySize = _cellSize * TILE_DISPLAY_RATIO;
            float restX = Mathf.Max(0.01f, _restScale.x);
            float cNative = (_vaultBadgeBg.sprite != null && _vaultBadgeBg.sprite.bounds.size.x > 0f)
                ? _vaultBadgeBg.sprite.bounds.size.x : 1f;
            float cs = (displaySize * 0.52f) / (cNative * restX);
            _vaultBadgeBg.transform.localScale = new Vector3(cs, cs, 1f);

            _vaultBadgeTMP.text = $"{_vaultRequiredLen}+";
            _vaultBadgeGO.SetActive(true);
            _vaultBadgeTMP.gameObject.SetActive(true);
        }

        private void HideVaultBadge()
        {
            if (_vaultBadgeGO != null)  _vaultBadgeGO.SetActive(false);
            if (_vaultBadgeTMP != null) _vaultBadgeTMP.gameObject.SetActive(false);
        }

        // ── ICE / frost overlay (clear-the-blocker objective, 2026-06-12) ───────────────
        // A frozen tile is a NORMAL, MATCHABLE letter with a translucent ice sheet drawn OVER it.
        // We render the frost as a separate child SpriteRenderer (like the gloss/badge) rather than
        // tinting the base sprite — so the letter stays visible AND the many places that repaint
        // _spriteRenderer.color (ResetVisuals, pulse cleanup, flashes) can't clobber the ice. The
        // overlay just sits on/off; nothing else has to know about it. The tile is otherwise a
        // normal letter tile (NOT a stone — it matches in words).
        public bool IsFrozen => _isFrozen;
        private static readonly Color FROST_TINT = new Color(0.62f, 0.84f, 0.97f, 0.62f); // icy light-blue, translucent

        private void EnsureFrostOverlay()
        {
            if (_frostGO != null) return;
            if (s_frostSprite == null)
                s_frostSprite = TileRenderer.CreateSolidRoundedRect(160, 160, 36, Color.white);

            _frostGO = new GameObject("TileFrost");
            _frostGO.transform.SetParent(transform, false);
            _frostGO.transform.localPosition = new Vector3(0f, 0f, -0.13f); // in front of letter (-0.1) + badge (-0.12)
            _frostSR = _frostGO.AddComponent<SpriteRenderer>();
            _frostSR.sprite = s_frostSprite;
            _frostSR.color  = FROST_TINT;
            _frostSR.sortingOrder = 10; // above face(5)/gloss(6)/letter(7)/badge(8-9)
            _frostGO.tag = "Untagged"; // skip 2D-light conversion (stays unlit)

            // Size the sheet to roughly cover the tile face (same counter-scale formula as the gloss).
            float displaySize = _cellSize * TILE_DISPLAY_RATIO;
            float restX = Mathf.Max(0.01f, _restScale.x);
            float fNative = (_frostSR.sprite != null && _frostSR.sprite.bounds.size.x > 0f)
                ? _frostSR.sprite.bounds.size.x : 1f;
            float fs = (displaySize * 0.92f) / (fNative * restX);
            _frostGO.transform.localScale = new Vector3(fs, fs, 1f);

            // Assign the diagonal specular-sweep material + a per-tile phase so a row of ice
            // doesn't sweep in lockstep. The shine animates on the GPU (_Time.y) — no per-frame C#.
            var sheen = GetFrostSheenMat();
            if (sheen != null)
            {
                _frostSR.sharedMaterial = sheen;
                _frostHasSheen = true;
                if (s_frostMPB == null) s_frostMPB = new MaterialPropertyBlock();
                _frostSR.GetPropertyBlock(s_frostMPB);
                s_frostMPB.SetFloat("_Phase", UnityEngine.Random.value);
                _frostSR.SetPropertyBlock(s_frostMPB);
            }
        }

        private Coroutine _frostShiverRoutine; // fallback CPU glint loop (only if the sheen shader is unavailable)
        private Coroutine _defrostRoutine;     // thaw flash → shatter
        private bool      _defrosting;         // guards against a sync clearing the frost mid-defrost
        private bool      _frostHasSheen;      // true once the GPU sheen material is assigned

        private static Material s_frostSheenMat;
        private static bool     s_frostSheenTried;
        private static MaterialPropertyBlock s_frostMPB;

        // The diagonal specular-sweep material (WordDrop/FrostSheen). Shared across all ice tiles;
        // per-tile _Phase desyncs them via a MaterialPropertyBlock. GPU-animated (_Time.y) → no
        // per-frame C#. Returns null only if the shader can't be found (→ CPU glint fallback).
        private static Material GetFrostSheenMat()
        {
            if (s_frostSheenTried) return s_frostSheenMat;
            s_frostSheenTried = true;
            // Prefer the editable material ASSET (Assets/Resources/FrostSheen.mat) so its sweep
            // values can be tuned live in the Inspector. Fall back to building one from the shader.
            s_frostSheenMat = Resources.Load<Material>("FrostSheen");
            if (s_frostSheenMat == null)
            {
                var sh = Shader.Find("WordDrop/FrostSheen") ?? Resources.Load<Shader>("Shaders/FrostSheen");
                if (sh != null) s_frostSheenMat = new Material(sh) { name = "FrostSheenShared" };
            }
            return s_frostSheenMat;
        }

        /// <summary>Show/hide the ice overlay. Frozen tiles stay normal matchable letters; this is
        /// pure visual. GridManager.SyncToRulesState drives it from RulesCellData.IsFrozen. The
        /// overlay is a separate child so sprite-color repaints never clobber it (reset-pattern).</summary>
        public void SetFrozenVisual(bool active)
        {
            // While a thaw wind-up crack is playing, ignore a safety-sync trying to clear the frost —
            // the defrost coroutine owns the final clear (so the crack isn't cut off mid-shiver).
            if (!active && _defrosting) return;

            _isFrozen = active;
            if (active)
            {
                EnsureFrostOverlay();
                if (_frostGO != null) { _frostGO.SetActive(true); _frostSR.color = FROST_TINT; }
                // The sheen shader self-animates on the GPU. Only if it's unavailable do we fall back
                // to a lightweight CPU glint (a gentle brightness pulse — NO movement; moving the frost
                // over a static letter read as the tile distorting).
                if (!_frostHasSheen && _frostShiverRoutine == null && _frostGO != null && isActiveAndEnabled)
                    _frostShiverRoutine = StartCoroutine(FrostGlintLoop());
            }
            else if (_frostGO != null)
            {
                if (_frostShiverRoutine != null) { StopCoroutine(_frostShiverRoutine); _frostShiverRoutine = null; }
                if (_frostSR != null) _frostSR.color = FROST_TINT;
                _frostGO.SetActive(false);
            }
        }

        /// <summary>FALLBACK ambient glint (only runs if the WordDrop/FrostSheen shader can't be
        /// loaded). A gentle periodic brightness pulse on the frost — NO movement (movement over a
        /// static letter read as distortion). The real effect is the GPU diagonal specular sweep in
        /// the sheen shader. 2026-06-18 Spencer.</summary>
        private System.Collections.IEnumerator FrostGlintLoop()
        {
            if (_frostGO == null) { _frostShiverRoutine = null; yield break; }

            const float PULSE_DUR = 0.5f;
            float t = 0f, pulseT = -1f;
            yield return WaitCache.Get(UnityEngine.Random.Range(0f, 1.5f)); // desync the start
            float nextPulse = UnityEngine.Random.Range(1.2f, 2.4f);

            while (_isFrozen && !_defrosting && _frostGO != null && _frostGO.activeInHierarchy)
            {
                float dt = Time.deltaTime;
                t += dt;

                float env = 0f;
                if (pulseT >= 0f)
                {
                    pulseT += dt;
                    float p = pulseT / PULSE_DUR;
                    if (p >= 1f) { pulseT = -1f; nextPulse = t + UnityEngine.Random.Range(1.2f, 2.4f); }
                    else env = Mathf.Sin(p * Mathf.PI); // rise + fall
                }
                else if (t >= nextPulse) pulseT = 0f;

                if (_frostSR != null)
                    _frostSR.color = new Color(
                        FROST_TINT.r + 0.22f * env, FROST_TINT.g + 0.16f * env,
                        FROST_TINT.b + 0.10f * env, Mathf.Min(1f, FROST_TINT.a + 0.20f * env));

                yield return null;
            }

            if (_frostGO != null && !_defrosting && _frostSR != null) _frostSR.color = FROST_TINT;
            _frostShiverRoutine = null;
        }

        /// <summary>Defrost / ice-shatter: a short sharp "crack" shiver that BUILDS (the ice straining)
        /// with the frost brightening, then the shatter particle burst fires and the overlay clears —
        /// leaving the (now normal) letter tile in place. Called by GameVisualBridge for cells in
        /// StepResult.ThawedCells. 2026-06-12 / wind-up added 2026-06-18 Spencer.</summary>
        public void PlayDefrost()
        {
            if (_defrosting) return;
            // No visible frost (edge: already cleared) → just the burst + ensure state cleared.
            if (_frostGO == null || !_frostGO.activeInHierarchy || !isActiveAndEnabled)
            {
                GameParticles.Instance?.PlayDefrost(transform.position);
                SetFrozenVisual(false);
                return;
            }
            if (_defrostRoutine != null) StopCoroutine(_defrostRoutine);
            _defrostRoutine = StartCoroutine(DefrostCoroutine());
        }

        private System.Collections.IEnumerator DefrostCoroutine()
        {
            _defrosting = true;
            if (_frostShiverRoutine != null) { StopCoroutine(_frostShiverRoutine); _frostShiverRoutine = null; }

            // 1) Quick bright "crack" flash — brighten the frost toward white-blue.
            const float CRACK = 0.10f;
            float e = 0f;
            while (e < CRACK && _frostGO != null)
            {
                e += Time.deltaTime;
                float p = Mathf.Clamp01(e / CRACK);
                if (_frostSR != null)
                    _frostSR.color = new Color(0.72f + 0.28f * p, 0.90f, 1f, Mathf.Min(1f, FROST_TINT.a + 0.55f * p));
                yield return null;
            }

            // 2) Ice shard / shimmer / droplet burst at the moment it cracks.
            GameParticles.Instance?.PlayDefrost(transform.position);

            // 3) MELT: the frost SLUMPS and fades away (Y-shrink + drip DOWN + fade) instead of popping off —
            //    the visible "it's actually melting" moment. 2026-07-07 Spencer.
            const float MELT = 0.42f;
            Vector3 s0 = _frostGO != null ? _frostGO.transform.localScale : Vector3.one;
            Vector3 p0 = _frostGO != null ? _frostGO.transform.localPosition : Vector3.zero;
            float a0 = _frostSR != null ? _frostSR.color.a : FROST_TINT.a;
            float m = 0f;
            while (m < MELT && _frostGO != null)
            {
                m += Time.deltaTime;
                float t = Mathf.Clamp01(m / MELT);
                float ease = 1f - (1f - t) * (1f - t); // OutQuad
                _frostGO.transform.localScale = new Vector3(s0.x * (1f - 0.55f * ease), s0.y * (1f - 0.92f * ease), 1f);
                _frostGO.transform.localPosition = p0 + Vector3.down * (0.16f * ease);
                if (_frostSR != null)
                {
                    var c = _frostSR.color;
                    _frostSR.color = new Color(c.r, c.g, c.b, a0 * (1f - ease));
                }
                yield return null;
            }

            // Restore the overlay's rest transform (so a pooled tile freezes clean next time) + clear.
            if (_frostGO != null) { _frostGO.transform.localScale = s0; _frostGO.transform.localPosition = p0; }
            _defrosting = false;
            SetFrozenVisual(false);
            _defrostRoutine = null;
        }
        private static Sprite s_vaultSprite;
        private static bool   s_vaultSpriteTried;
        private Coroutine     _vaultIdleRoutine;   // anticipation jiggle while rendered as a chest

        private static Sprite GetVaultSprite()
        {
            if (s_vaultSpriteTried) return s_vaultSprite;
            s_vaultSpriteTried = true;
            // Icon_ItemIcon_Treasure.Png is imported as a plain Texture (mistake #5) → the Sprite
            // load returns null; fall back to building a Sprite from the Texture2D.
            s_vaultSprite = Resources.Load<Sprite>("Tiles/Icon_ItemIcon_Treasure");
            if (s_vaultSprite == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/Icon_ItemIcon_Treasure");
                if (tex != null)
                    s_vaultSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            if (s_vaultSprite == null)
                Debug.LogWarning("[Vault] Icon_ItemIcon_Treasure failed to load (Sprite + Texture2D both null).");
            return s_vaultSprite;
        }

        /// <summary>Render this tile as a treasure VAULT — swaps to the chest sprite (re-fit to the
        /// cell since its PPU/bounds differ from tile sprites) and hides the letter/gloss like a
        /// stone. The vault stays IsStone in DATA so the adjacent-detonation crack still works, but
        /// it reads as the hunted chest, not grey junk. Restores the normal tile on false / pool
        /// reuse. 2026-06-09.</summary>
        public void SetVaultVisual(bool active)
        {
            _isVault = active;
            _isStone = active; // keep stone guards (hidden letter, crack mechanic) applying
            if (_spriteRenderer == null) return;
            if (active)
            {
                var vault = GetVaultSprite();
                if (vault != null)
                {
                    _spriteRenderer.sprite = vault;
                    RefitToSprite(vault); // chest bounds ≠ tile bounds — re-scale to the cell
                }
                _spriteRenderer.color = VaultTint; // tier telegraph: white / silver / gold
                ApplyVaultBadge(); // "4+"/"5+" requirement on mid/high chests; nothing on regular
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
                if (_glossGO != null) _glossGO.SetActive(false);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
                // Treasure-chest "shaking in anticipation" idle (Vampire-Survivors feel).
                if (_vaultIdleRoutine == null && gameObject.activeInHierarchy)
                    _vaultIdleRoutine = StartCoroutine(VaultIdleShakeLoop());
            }
            else
            {
                if (_vaultIdleRoutine != null) { StopCoroutine(_vaultIdleRoutine); _vaultIdleRoutine = null; }
                transform.localRotation = Quaternion.identity; // undo any in-progress jiggle
                HideVaultBadge();
                Sprite normal = (OwnerIndex == 1 && s_spriteAI != null) ? s_spriteAI : s_spriteNormal;
                if (normal != null)
                {
                    _spriteRenderer.sprite = normal;
                    RefitToSprite(normal);
                }
                _spriteRenderer.color = Color.white;
                if (_glossGO != null) _glossGO.SetActive(true);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(true);
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

        // Re-fit this tile's rest scale so `sprite` renders at the cell display size, mirroring the
        // bounds-based sizing in CheckoutTile (sprite PPU/native bounds vary — skill_007).
        private void RefitToSprite(Sprite sprite)
        {
            if (sprite == null) return;
            float displaySize = _cellSize * TILE_DISPLAY_RATIO;
            float nativeSize  = sprite.bounds.size.x > 0.0001f ? sprite.bounds.size.x : 1f;
            float scale = displaySize / nativeSize;
            _restScale = new Vector3(scale, scale, 1f); _restScaleSet = true;
            transform.localScale = _restScale;
        }

        /// <summary>Idle "shaking in anticipation" jiggle for treasure vaults — a few quick damped
        /// Z-rotation oscillations, then a rest gap, looped. Rotation-ONLY (leaves scale to the
        /// refit path and position to gravity, so it can't desync). Phase-desynced per chest so a
        /// board of vaults doesn't shake in lockstep. Runs while _isVault; SetVaultVisual(false) /
        /// ResetForPool stop it and restore upright rotation. 2026-06-09.</summary>
        private IEnumerator VaultIdleShakeLoop()
        {
            var t = transform;
            yield return WaitCache.Get(Random.Range(0f, 1.6f)); // stagger chests
            while (_isVault && _spriteRenderer != null)
            {
                // ── rest between shakes (the pause is what reads as "anticipation") ──
                float gap = Random.Range(1.5f, 2.4f), gt = 0f;
                while (gt < gap && _isVault) { gt += Time.deltaTime; yield return null; }
                if (!_isVault) break;

                // ── damped wobble ──
                const float dur = 0.42f, amp = 6f, freq = 3f; // peak ±6°, 3 oscillations, fading out
                float e = 0f;
                while (e < dur && _isVault)
                {
                    e += Time.deltaTime;
                    float p = e / dur;
                    float ang = Mathf.Sin(p * freq * Mathf.PI * 2f) * amp * (1f - p);
                    t.localRotation = Quaternion.Euler(0f, 0f, ang);
                    yield return null;
                }
                t.localRotation = Quaternion.identity;
            }
            t.localRotation = Quaternion.identity;
            _vaultIdleRoutine = null;
        }

        /// <summary>Pre-open "tell": a hard, intensifying shake to fire the instant before the chest
        /// pops (call from the open/crack sequence when it lands). Ramps amplitude up over ~0.5s so
        /// it builds — the idle jiggle's louder cousin. Stops the idle loop while it plays.</summary>
        public void PlayVaultAnticipation()
        {
            if (!_isVault || !gameObject.activeInHierarchy) return;
            if (_vaultIdleRoutine != null) { StopCoroutine(_vaultIdleRoutine); _vaultIdleRoutine = null; }
            StartCoroutine(VaultAnticipationBurst());
        }

        private IEnumerator VaultAnticipationBurst()
        {
            var t = transform;
            const float dur = 0.55f, maxAmp = 13f, freq = 9f; // builds to ±13°, fast
            float e = 0f;
            while (e < dur && _isVault)
            {
                e += Time.deltaTime;
                float p = e / dur;
                float ang = Mathf.Sin(p * freq * Mathf.PI * 2f) * maxAmp * p; // amplitude ramps UP
                t.localRotation = Quaternion.Euler(0f, 0f, ang);
                yield return null;
            }
            t.localRotation = Quaternion.identity;
            // hand back to the idle jiggle if the chest is still around
            if (_isVault && _vaultIdleRoutine == null && gameObject.activeInHierarchy)
                _vaultIdleRoutine = StartCoroutine(VaultIdleShakeLoop());
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
            _pendingDiffusePop = false; // re-primed → cancel any pending diffuse-pop revert
            _hasPrimedGlow   = true;
            // If this tile is priming straight out of the teal edit/swap glow, kill that glow's bloom + pulse now
            // so the cyan doesn't linger a frame over the magenta primed look. No-op for normal drop-primed tiles
            // (no edit glow active). 2026-07-10 Spencer.
            if (_editGlowPulse != null) { _editGlowPulse.Kill(); _editGlowPulse = null; }
            _editSelected = false;
            ClearBloomGlow();
            _heatLevel       = heatLevel;
            _fuseRemaining   = fuseRemaining;
            _primedStartTime = Time.time;
            _primedMaxAge    = maxAge;

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
            // Combo escalation: if this tile has already been handed its per-word detonation
            // hue (mid-blast), DON'T let a re-glow stomp it back to magenta before it pops.
            if (!_hasDetonationColor)
                _primedGlowColor = glowColor;
            _currentBorderColor = glowColor;

            if (!_isHighlighted)
                ApplyBorderColor(glowColor);

            // Teaching hook (L7 one-shot): a charged word showing its WARNING color (fuse ≤ 1, about to lose its
            // charge) tells the tutorial so it can freeze + spotlight it. Robust by design — NOT gated on a clean
            // oldFuse→newFuse transition, because several re-glow paths pass fuseRemaining:-1 and would poison an
            // oldFuse check; the one-shot flag inside MaybeShowPrimeDecay dedups the repeated warning re-glows.
            // Non-gold only. Cheap no-op unless L7 armed it + it hasn't shown. 2026-07-09 Spencer.
            if (!isGold && fuseRemaining >= 0 && fuseRemaining <= 1)
                TutorialManager.MaybeShowPrimeDecay(new Vector2Int(Col, Row));

            // Force the primed sprite swap immediately, regardless of highlight
            // state or coroutine lifecycle. Without this, gold tiles entering
            // primed state didn't show their glow visual until a later frame
            // (Spencer reported 2026-05-19: had to drop another letter first).
            // 2026-06-04 Spencer: but NOT during the green scored-flash window — let the
            // green flash play first; PrimedPulseLoop swaps to the magenta sprite when
            // the hold expires. ALSO skip while the tile is still showing its scored-green (the edit/
            // rewrite path) — PrimedPulseLoop swaps to the magenta sprite after the fade-in, once the
            // color already matches, so the swap blends instead of popping. 2026-06-23 Spencer.
            if (_spriteRenderer != null && s_spriteGold != null && Time.time >= _scoredFlashUntil && !_isShowingScoredSprite)
                _spriteRenderer.sprite = s_spriteGold;

            // Start subtle primed idle animation
            if (_primedPulseCoroutine == null)
                _primedPulseCoroutine = StartCoroutine(PrimedPulseLoop());

            // Flash + sparkles when first primed
            if (playFlash && !wasAlreadyPrimed)
            {
                // 2026-06-04 Spencer: during the green scored-flash window, SKIP the
                // magenta prime flash — it drives sr.color via _flashCoroutine and would
                // stomp the green flash. The green scored flash IS the celebration here;
                // the magenta pulse takes over cleanly once the hold expires.
                if (Time.time >= _scoredFlashUntil)
                {
                    if (_isShowingScoredSprite)
                    {
                        // EDIT/REWRITE path: the word is primed AFTER its green scored-flash window has
                        // already settled to white (unlike a normal drop, which primes mid-window). Snapping
                        // the magenta FlashHighlight here is the choppy white→magenta flip. Instead, restart
                        // the fade-in clock so PrimedPulseLoop eases white→magenta smoothly — same handoff a
                        // normal drop gets. 2026-06-23 Spencer.
                        _scoredFlashUntil = Time.time;
                    }
                    else
                    {
                        // Flash to a TAMED version of the glow — the raw HDR (PRIMED_GLOW
                        // 1.8/0.5/1.3) held at peak blooms too hard ("blows out"). Cap each
                        // channel so the flash glows gently. The STEADY primed glow
                        // (settleColor, from _primedGlowColor) is computed separately and
                        // unaffected, so the gameplay cue stays.
                        Color flashCol = new Color(
                            Mathf.Min(glowColor.r, 1.28f),
                            Mathf.Min(glowColor.g, 1.28f),
                            Mathf.Min(glowColor.b, 1.28f),
                            glowColor.a);
                        FlashHighlight(flashCol);
                    }
                }
                GameParticles.Instance?.PlayPrimed(transform.position);
            }

        }

        // COMBO COLOUR ESCALATION (2026-07-27 Spencer). WordDropFX.PlayExplosion calls this on each
        // chained word's tiles in the split-second before they detonate, tinting them by blast order
        // (pink→magenta→violet→blue-violet) so a combo reads as a rolling hue ramp. It writes the SAME
        // field the primed pulse already paints from (_primedGlowColor), so the running pulse simply
        // renders the new hue — and LATCHES _hasDetonationColor so a per-turn re-glow won't repaint it
        // back to magenta before the pop. The big-blast/meltdown path reads DetonationColor from its
        // cache instead (WordDropFX ~1658). Cleared on ClearPrimedGlow/ResetForPool.
        public void SetDetonationColor(Color hdrTint)
        {
            _hasDetonationColor = true;
            _primedGlowColor    = hdrTint;
            _currentBorderColor = hdrTint;
            if (!_isHighlighted) ApplyBorderColor(hdrTint);
        }
        public bool  HasDetonationColor => _hasDetonationColor;
        public Color DetonationColor    => _primedGlowColor;
        // Hue-preserving LDR version of the detonation tint. sr.color is clamped to [0,1] per channel
        // on mobile (Color32), which pins the brightest channel to 1.0 for EVERY chain word and
        // collapses the per-word hue ramp to one magenta. Normalising so the brightest channel = 1.0
        // keeps the R:G:B RATIO (= the hue) intact through that clamp, so each word's hue survives.
        // The additive bloom overlay still gets the raw HDR _primedGlowColor for the glow. 2026-07-27.
        public Color DetonationFaceColor
        {
            get
            {
                float m = Mathf.Max(_primedGlowColor.r, Mathf.Max(_primedGlowColor.g, _primedGlowColor.b));
                if (m <= 0.0001f) return _primedGlowColor;
                float s = 1f / m;
                return new Color(_primedGlowColor.r * s, _primedGlowColor.g * s, _primedGlowColor.b * s, 1f);
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

                // 2026-06-04 Spencer: hold off the magenta takeover until the green
                // scored flash has played through — don't write color or swap the
                // sprite yet (the green scored sprite + flash tween own sr.color).
                if (Time.time < _scoredFlashUntil)
                {
                    // 2026-06-17 Spencer: the scored flash MULTIPLIES a green tint over the sprite. While
                    // we're holding, the sprite may still be the PRIMED (magenta) sprite — and green×magenta
                    // = a muddy dark tile (seen when a primed word scores/explodes via a booster). Force the
                    // LIGHT normal sprite during the hold so the green reads bright, exactly like a non-primed
                    // scored tile. The block just below restores the magenta sprite once the hold expires.
                    if (s_spriteNormal != null && _spriteRenderer.sprite != s_spriteNormal)
                        _spriteRenderer.sprite = s_spriteNormal;
                    // NOTE: don't touch the bloom overlay here — during this hold the
                    // scored green-flash tween (WordDropFX.PlayWordScored) owns it. We
                    // resume driving the magenta overlay once the hold expires below.
                    yield return null;
                    continue;
                }
                // Smooth scored→primed handoff (2026-06-23 Spencer): instead of snapping from the
                // white scored-flash settle straight to the glossy magenta sprite (choppy), the magenta
                // EASES in via sr.color over PRIMED_MAGENTA_FADE_IN. Keep the LIGHT sprite during that
                // fade (the magenta rises as a tint on it), then swap to the glossy primed sprite once
                // the color already matches — so the sprite swap blends instead of popping.
                bool pastMagentaFadeIn = Time.time >= _scoredFlashUntil + PRIMED_MAGENTA_FADE_IN;
                if (pastMagentaFadeIn)
                {
                    if (s_spriteGold != null && _spriteRenderer.sprite != s_spriteGold)
                        _spriteRenderer.sprite = s_spriteGold;
                }
                else
                {
                    if (s_spriteNormal != null && _spriteRenderer.sprite != s_spriteNormal)
                        _spriteRenderer.sprite = s_spriteNormal;
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
                // 2026-06-04 Spencer: the new glossy primed sprite is ~0.95 brightness,
                // so the old 0.35 floor (pc.r≈1.28 → ×0.95≈1.22) sat UNDER the 1.30
                // bloom line at low heat — freshly-primed/calm tiles stopped glowing,
                // only about-to-expire ones did. Raise the floor so pc.r≈1.46 → rendered
                // ≈1.39, a soft glow that's present from the moment a word primes.
                tintAmount = Mathf.Max(tintAmount, 0.58f);
                // Cap the pulse so the primed glow blooms SOFTLY, not blown out.
                // Raw peak reached ~1.53 red on PRIMED_GLOW (1.8/0.5/1.3) → hard
                // bloom. Scale the colour down (preserving hue) if any channel
                // crosses 1.35 so it sits just over the 1.30 bloom line.
                Color pc = Color.Lerp(Color.white, _primedGlowColor, tintAmount);
                float pmax = Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b));
                // 2026-06-04 Spencer: the new glossy pink primed sprite is darker than
                // the old flat one, so the multiply rarely crossed the 1.30 bloom line —
                // the pulse stopped reading as a GLOW. Raise the cap so it blooms again.
                float primedCap = Mathf.Max(WordDropFX.PrimedGlowCap, 1.55f);
                if (pmax > primedCap) { float k = primedCap / pmax; pc.r *= k; pc.g *= k; pc.b *= k; }
                // Ease the magenta IN from white over the first PRIMED_MAGENTA_FADE_IN seconds after the
                // scored-flash hold expires (smooth green→white→magenta, no snap). After the window —
                // or when there was no recent scored flash — fadeIn is 1 and this is a no-op. 2026-06-23.
                if (!pastMagentaFadeIn)
                {
                    float fadeIn = Mathf.Clamp01((Time.time - _scoredFlashUntil) / PRIMED_MAGENTA_FADE_IN);
                    pc = Color.Lerp(Color.white, pc, fadeIn);
                }
                // COMBO ESCALATION: for detonation tiles, replace the HDR pc (which loses its hue to the
                // mobile Color32 clamp) with the hue-preserving LDR face tint so each chain word's colour
                // actually reads on screen. Bloom overlay below still gets raw HDR. 2026-07-27.
                if (_hasDetonationColor)
                    pc = DetonationFaceColor;
                _spriteRenderer.color = pc;

                // TEMP diagnostic (2026-07-27): confirm the pulse is actually painting the detonation tint.
                if (_hasDetonationColor && !_detoProbeLogged)
                {
                    _detoProbeLogged = true;
                    Debug.Log($"[ComboRamp/pulse] {Letter} PAINTING glow=({_primedGlowColor.r:F2},{_primedGlowColor.g:F2},{_primedGlowColor.b:F2}) → pc=({pc.r:F2},{pc.g:F2},{pc.b:F2}) tint={tintAmount:F2} sprite={( _spriteRenderer.sprite!=null?_spriteRenderer.sprite.name:"null")}");
                }

                // MOBILE bloom: pc above gets clamped to 1.0 on iOS/Metal (sr.color
                // → Color32), so on the phone the primed pulse never crosses the 1.30
                // bloom line. Feed the additive overlay the RAW HDR primed color with a
                // soft pulsing alpha so it glows on device. No-ops on desktop (the pc
                // path already blooms there). Floor near calm, brighter with heat.
                SetBloomGlow(_primedGlowColor,
                    Mathf.Clamp01(0.26f + pulse * 0.18f + effectiveHeat * 0.03f));

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

                if (!IsAnimating && !_externalScaleControl && _flashCoroutine == null)
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
            ClearBloomGlow();
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

        // -------------------------------------------------------------------
        // Bloom-glow overlay — additive HDR glow that blooms on MOBILE (where
        // sr.color HDR is clamped). See the field declarations for the why.
        // -------------------------------------------------------------------
        private void EnsureBloomGlowOverlay()
        {
            if (_bloomGlowSR != null) return;
            if (s_bloomGlowMat == null)
            {
                Shader sh = Shader.Find("WordDrop/AdditiveSprite")
                         ?? Resources.Load<Shader>("Shaders/AdditiveSprite");
                if (sh != null) s_bloomGlowMat = new Material(sh) { name = "TileBloomGlowShared" };
            }
            if (s_bloomGlowMPB == null) s_bloomGlowMPB = new MaterialPropertyBlock();

            _bloomGlowGO = new GameObject("TileBloomGlow");
            _bloomGlowGO.transform.SetParent(transform, false);
            _bloomGlowGO.transform.localPosition = new Vector3(0f, 0f, -0.04f); // in front of face, behind letter
            _bloomGlowGO.transform.localScale    = Vector3.one;                 // same transform/scale as the face
            _bloomGlowSR = _bloomGlowGO.AddComponent<SpriteRenderer>();
            // Use the NEUTRAL light tile shape as a fixed base (NOT the colored
            // primed/scored face sprite — that would tint the additive hue). Same
            // native size as the face, so localScale 1 covers the tile exactly and
            // the HDR _Color cleanly controls the glow color. Bloom adds the halo.
            _bloomGlowSR.sprite = s_spriteNormal;
            if (s_bloomGlowMat != null) _bloomGlowSR.sharedMaterial = s_bloomGlowMat;
            _bloomGlowSR.color = Color.white; // vertex color stays white; HDR comes from _Color (MPB)
            _bloomGlowSR.sortingOrder = (_spriteRenderer != null ? _spriteRenderer.sortingOrder : 5) + 1;
            _bloomGlowSR.enabled = false;
        }

        /// <summary>
        /// Drive the additive bloom-glow overlay. hdrColor carries the HDR rgb
        /// (e.g. PRIMED_GLOW 1.8/0.5/1.3 or scored green ~1.62); alpha animates
        /// the glow in/out. MOBILE-ONLY — on desktop the existing sr.color HDR
        /// path already blooms, so this no-ops to keep the desktop look identical.
        /// </summary>
        public void SetBloomGlow(Color hdrColor, float alpha, bool forceDesktop = false)
        {
            if (!Application.isMobilePlatform && !forceDesktop) return; // desktop unchanged unless forced (cascade green flash)
            EnsureBloomGlowOverlay();
            if (_bloomGlowSR == null) return;

            alpha = Mathf.Clamp01(alpha);
            if (alpha <= 0.001f) { _bloomGlowSR.enabled = false; return; }

            // HDR rgb + animated alpha go through the material _Color (unclamped),
            // NOT sr.color (which would clamp). Blend SrcAlpha One uses _Color.a.
            _bloomGlowSR.GetPropertyBlock(s_bloomGlowMPB);
            s_bloomGlowMPB.SetColor("_Color", new Color(hdrColor.r, hdrColor.g, hdrColor.b, alpha));
            _bloomGlowSR.SetPropertyBlock(s_bloomGlowMPB);
            _bloomGlowSR.enabled = true;
        }

        /// <summary>Hide the bloom-glow overlay (mobile). Safe to call on desktop.</summary>
        public void ClearBloomGlow()
        {
            if (_bloomGlowSR != null) _bloomGlowSR.enabled = false;
        }

        /// <summary>Set sorting order on tile sprite + text layers.</summary>
        public void SetSortingOrder(int order)
        {
            // While a tutorial spotlight is DIMMING this tile, block external FX bumps (WordDropFX charge/detonation
            // raises tiles to 15) from lifting it above the scrim — charged tiles were bleeding bright. 2026-07-08 Spencer.
            if (SpotlightActive && _spotlightOrder < 0) order = 5;
            if (_spriteRenderer != null) _spriteRenderer.sortingOrder = order;
            if (_iridSR != null) _iridSR.sortingOrder = order + 1;            // holographic fill, above the face
            if (_glossSR != null) _glossSR.sortingOrder = order + 1;          // top sheen, above the face
            if (_innerShadowSR != null) _innerShadowSR.sortingOrder = order + 1; // bottom shadow, above the face
            if (_bloomGlowSR != null) _bloomGlowSR.sortingOrder = order + 1;  // additive glow, above the face (below the letter)
            if (_letterTMP != null) _letterTMP.sortingOrder = order + 2; // letter crisp ABOVE both
            if (_pointTMP != null) _pointTMP.sortingOrder = order + 2;
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
                if (_isVault)
                {
                    // Vaults ALWAYS show the treasure chest — never the grey stone tint.
                    var v = GetVaultSprite(); if (v != null) _spriteRenderer.sprite = v;
                    _spriteRenderer.color = VaultTint; // tier telegraph (white / silver / gold)
                }
                else if (_isStone)
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
            SetSortingOrder(_spotlightOrder >= 0 ? _spotlightOrder : (AimModeTileOrder > 0 ? AimModeTileOrder : 5));
            Color border = _hasPrimedGlow ? _primedGlowColor : TILE_BORDER_NORMAL;
            ApplyBorderColor(border);
        }

        /// <summary>Tutorial spotlight: raise THIS tile above the dim scrim (order >= 0) or clear it (-1).
        /// Persists through repaints, unlike a bare SetSortingOrder. 2026-07-08 Spencer.</summary>
        public void SetSpotlight(int order)
        {
            _spotlightOrder = order;
            SetSortingOrder(order >= 0 ? order : (AimModeTileOrder > 0 ? AimModeTileOrder : 5));
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
        /// <summary>2026-06-03 Spencer: let the primed pulse keep driving COLOUR while
        /// an external animation (tier-1 explosion shrink) drives the transform scale.
        /// Suspends only the pulse's localScale write (its sole reader), so the tile
        /// holds its exact primed colour as it scales down — no clear/repaint needed.</summary>
        public void SetExternalScaleControl(bool on) { _externalScaleControl = on; }

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

        // Diffuse pop: when a primed word expires WITHOUT detonating, the springy "pop" is deferred
        // (so the rising row can't DOComplete it away). But the colour reverts immediately in the
        // rebuild — so the pop fired AFTER the tile already looked normal, which read backwards. These
        // let the tile KEEP its primed look through the deferral, so the pop fires first and the revert
        // to normal happens at the pop's tail (signifying the state change). 2026-06-18 Spencer.
        private bool   _pendingDiffusePop;
        private Sprite _diffuseSprite;
        private Color  _diffuseColor = Color.white;
        public void MarkPendingDiffusePop() { _pendingDiffusePop = true; }

        public void ClearPrimedGlow()
        {
            // Capture the primed look BEFORE we reset it — a pending diffuse pop keeps showing it.
            // (ResetVisuals runs just before this and whitens the colour, so use the primed glow's
            // settle-colour for fidelity rather than the live value.) The primed sprite is unchanged.
            if (_hasPrimedGlow && _spriteRenderer != null)
            {
                _diffuseSprite = _spriteRenderer.sprite;
                _diffuseColor  = Color.Lerp(Color.white, _primedGlowColor, 0.35f);
            }
            _hasPrimedGlow      = false;
            _hasDetonationColor = false; // combo escalation: drop the latched blast tint
            _detoProbeLogged    = false;
            _currentBorderColor = TILE_BORDER_NORMAL;

            // Stop primed pulse
            if (_primedPulseCoroutine != null)
            {
                StopCoroutine(_primedPulseCoroutine);
                _primedPulseCoroutine = null;
            }
            ClearBloomGlow(); // StopCoroutine skips the loop's own cleanup

            if (!_isHighlighted)
                ApplyBorderColor(TILE_BORDER_NORMAL);

            // Reset any pulse-induced tint/scale — preserve special tile tints
            if (_spriteRenderer != null)
            {
                if (_isVault)
                    _spriteRenderer.color = VaultTint; // vaults keep the chest; tier tint (white/silver/gold)
                else if (_isStone)
                    _spriteRenderer.color = STONE_TINT;
                else if (_isSwapRefill)
                    _spriteRenderer.color = SWAP_REFILL_TINT;
                else if (_isEditRefill)
                    _spriteRenderer.color = EDIT_REFILL_TINT;
                else if (_isWildRefill)
                    _spriteRenderer.color = WILD_REFILL_TINT;
                else if (!_isGoldBonus)
                    _spriteRenderer.color = Color.white;
                // Restore appropriate sprite based on tile state. Vaults re-assert the chest;
                // gold-bonus tiles return to their gold sprite, not white.
                _spriteRenderer.sprite = _isVault
                    ? (GetVaultSprite() ?? s_spriteNormal)
                    : _isGoldBonus
                        ? (s_spriteGolden ?? s_spriteNormal)
                        : s_spriteNormal;
            }

            // Reset scale to correct base
            float sprNative = (_spriteRenderer != null && _spriteRenderer.sprite != null)
                ? _spriteRenderer.sprite.bounds.size.x : Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512) / 100f;
            float correctScale = (_cellSize * TILE_DISPLAY_RATIO) / sprNative;
            transform.localScale = new Vector3(correctScale, correctScale, 1f);

            _heatLevel = 0;
            _fuseRemaining = -1; // -1 = no fuse → CALM. Was 0, which = critical heat, causing a
                                 // re-primed tile to flash max-heat for a frame before settling. 2026-06-10.
            if (_fuseTMP != null) _fuseTMP.gameObject.SetActive(false);

            // If this tile is queued for a diffuse pop, RE-ASSERT the primed look (plain letter tiles
            // only — special tiles keep the look set above). The deferred PlayDiffusePop reverts it to
            // normal at the end of the pop, so the pop fires first and the colour change lands after.
            if (_pendingDiffusePop && _diffuseSprite != null && _spriteRenderer != null
                && !_isVault && !_isStone && !_isGoldBonus && !_isWild
                && !_isSwapRefill && !_isEditRefill && !_isWildRefill && !_isShowingScoredSprite)
            {
                _spriteRenderer.sprite = _diffuseSprite;
                _spriteRenderer.color  = _diffuseColor;
            }
        }

        // ---------------------------------------------------------------------------
        // Public API — Preview Highlight (used by DropPreview)
        // ---------------------------------------------------------------------------

        private bool _hasPreviewHighlight = false;
        private Color _savedBorderBeforePreview;

        /// <summary>Lightweight tint overlay for drop preview. Does not affect primed glow.</summary>
        public void SetPreviewHighlight(Color color)
        {
            if (_isDropTargetVisual) return; // escort objects keep their amber — never preview-tint them (caused a flicker)
            if (!_hasPreviewHighlight)
                _savedBorderBeforePreview = _currentBorderColor;
            _hasPreviewHighlight = true;

            // Tint the live tile to the scored green (keeps test_tile's shape). 2026-06-10.
            if (_spriteRenderer != null)
            {
                _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color  = SCORED_TINT;
            }
        }

        /// <summary>Restore tile to its state before preview highlight.</summary>
        public void ClearPreviewHighlight()
        {
            if (_isDropTargetVisual) { _hasPreviewHighlight = false; return; } // never repaint an escort white (flicker)
            if (!_hasPreviewHighlight) return;
            _hasPreviewHighlight = false;

            // 2026-06-03 Spencer: diagnosing "2x tile stuck green after leaving the
            // preview path". Logs the tile's state + which sprite it restores to.
            if (_isGoldBonus || _isShowingScoredSprite)
                Debug.Log($"[PreviewClear] ({Col},{Row}) scored={_isShowingScoredSprite} gold={_isGoldBonus} primed={_hasPrimedGlow} wild={_isWild} → restoring {(_isShowingScoredSprite ? "SCORED(green)" : _hasPrimedGlow ? "PRIMED" : _isGoldBonus ? "GOLD" : _isWild ? "WILD" : "NORMAL")}");

            // Swap back to appropriate sprite (canonical priority: scored > primed > gold > wild > normal)
            if (_spriteRenderer != null)
            {
                if (_isShowingScoredSprite)
                {
                    // scored = green tint on the live tile (not a sprite swap). Keep the tint;
                    // do NOT fall through to the color=white reset below. 2026-06-10.
                    _spriteRenderer.sprite = s_spriteNormal;
                    _spriteRenderer.color  = SCORED_TINT;
                }
                else
                {
                    if (_hasPrimedGlow)
                        _spriteRenderer.sprite = s_spriteGold ?? s_spriteNormal;
                    else if (_isGoldBonus)
                        _spriteRenderer.sprite = s_spriteGolden ?? s_spriteNormal;
                    else if (_isWild)
                        _spriteRenderer.sprite = s_spriteWild ?? s_spriteNormal;
                    else
                        _spriteRenderer.sprite = s_spriteNormal;
                    _spriteRenderer.color = Color.white;
                }
            }
            ApplyBorderColor(_savedBorderBeforePreview);
        }

        // ── TEMP diagnostic 2026-06-17: "muddy green/dark primed tile" (booster cascade) ──
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

        /// <summary>Public accessor for the base/normal tile sprite (currently test_tile). Used by
        /// the drag ghost + selected-card so they can show the live tile shape + a green tint
        /// instead of swapping to the old separate green sprite. 2026-06-10.</summary>
        public static Sprite NormalSprite => s_spriteNormal;

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
            if (_isDropTargetVisual) return; // escort objects aren't matchable — never scored-tint them (flicker)
            _isShowingScoredSprite = scored;
            if (scored) _wasInScoredWord = true;
            if (scored)
            {
                // Green the LIVE tile via tint (keeps test_tile's shape) instead of swapping
                // to the separate green sprite. 2026-06-10 Spencer.
                _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color  = SCORED_TINT;
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
                _spriteRenderer.color  = Color.white; // clear any scored green tint while showing cyan
            }
            else
            {
                // Restore based on current tile state.
                // Priority: scored > primed > gold > normal
                if (_isShowingScoredSprite)
                {
                    _spriteRenderer.sprite = s_spriteNormal;       // scored = green tint, not sprite swap
                    _spriteRenderer.color  = SCORED_TINT;
                }
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
        public void SetEditSelected(bool active, bool popOnExit = false)
        {
            if (active)
            {
                // 2026-06-04 Spencer: base the pop on the CANONICAL rest scale captured
                // at setup — NOT the live transform (could be mid-pop → compounds bigger
                // each toggle) and NOT the live sprite bounds (could be the green edit
                // sprite). This keeps the springy pop exactly as before but makes the
                // rest size rock-solid no matter how fast you toggle on/off.
                _editBaseScale = CanonicalRestScale();
                _editSelected = true;

                // "Button push" tactile feedback on tap — a quick scale-DOWN press that springs back to rest
                // (was a scale-UP pop). 2026-07-10 Spencer.
                ButtonPushScale();

                // 2026-06-03 Spencer: selection reads as an HDR cyan glow on the tile
                // itself (glow-only, like the hint) — NO cyan sprite swap, NO halo.
                // Snap quickly to the peak saturated/glowing cyan ("accessing" feel),
                // THEN breathe between that peak and a dimmer cyan so it stays lit.
                if (_spriteRenderer != null)
                {
                    _spriteRenderer.DOKill();
                    _spriteRenderer.color = Color.white;
                    _spriteRenderer.DOColor(EDIT_GLINT_HIGH, EDIT_ACCESS_DUR)
                        .SetEase(Ease.OutQuad)
                        .OnComplete(() =>
                        {
                            if (_spriteRenderer != null)
                                _spriteRenderer.DOColor(EDIT_GLINT_LOW, EDIT_BREATH_DUR)
                                    .SetEase(Ease.InOutSine)
                                    .SetLoops(-1, LoopType.Yoyo);
                        });
                }

                // Pulsing HDR cyan bloom overlay — the part that actually glows on
                // mobile (sr.color above is clamped there). Alpha breathes between
                // LOW and HIGH so the selected tile obviously throbs. forceDesktop so
                // it's visible in the editor / on desktop too. Virtual tween (not bound
                // to sr), so the sr.DOKill above won't touch it — kill it explicitly on
                // deselect. 2026-07-09 Spencer.
                _editGlowPulse?.Kill();
                _editGlowAlpha = EDIT_GLOW_ALPHA_LOW;
                SetBloomGlow(EDIT_GLOW_HDR, _editGlowAlpha, forceDesktop: true);
                _editGlowPulse = DOTween.To(
                        () => _editGlowAlpha,
                        x => { _editGlowAlpha = x; SetBloomGlow(EDIT_GLOW_HDR, x, forceDesktop: true); },
                        EDIT_GLOW_ALPHA_HIGH, EDIT_GLOW_DUR)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo);
            }
            else
            {
                bool wasSelected = _editSelected;
                _editSelected = false;
                Vector3 rest = CanonicalRestScale();
                // ALWAYS snap scale back to canonical rest — even if we think we're
                // already deselected. A double SetEditSelected(false) or an interrupted
                // pop used to early-return here and leave the tile stuck inflated.
                transform.DOKill();
                transform.localScale = rest;
                // Kill the glint pulse + restore white only when we were actually
                // selected, so we don't stomp a special-tile tint on a stray cleanup
                // call. (ResetVisuals also covers this, but keep it self-contained.)
                if (wasSelected && _spriteRenderer != null)
                {
                    _spriteRenderer.DOKill();
                    _spriteRenderer.color = Color.white;
                }
                // Kill the pulsing bloom overlay and hide it. Always (even if we thought
                // we were already deselected) so an interrupted toggle can't strand a lit
                // glow. 2026-07-09 Spencer.
                _editGlowPulse?.Kill();
                _editGlowPulse = null;
                ClearBloomGlow();
                // 2026-06-03 Spencer: on toggle-off / expiry, fire the SAME springy
                // pop as on select so turning it off feels symmetric. (Not on commit
                // or board-shift reposition — those pass popOnExit=false.)
                if (popOnExit && wasSelected)
                {
                    transform.DOScale(rest * EDIT_POP_SCALE, EDIT_POP_DUR)
                        .SetEase(Ease.OutBack, 1.7f)
                        .OnComplete(() => { if (this != null) transform.localScale = rest; });
                }
            }
        }

        /// <summary>Quick teal "button selection" flash on TAP — used for the board-swap TARGET tile so the
        /// second tile you tap gives the same tap feedback the source got. Self-clears in ~0.22s (before the
        /// swap resolution could prime this tile), and bails if the tile is already edit-selected or primed, so
        /// it never fights those glows. 2026-07-10 Spencer.</summary>
        public void FlashEditTap()
        {
            if (_editSelected || _hasPrimedGlow || _spriteRenderer == null) return;
            // Snap to the teal glint + a real bloom, then fade both back over a short beat.
            _spriteRenderer.DOKill();
            _spriteRenderer.color = EDIT_GLINT_HIGH;
            _spriteRenderer.DOColor(Color.white, 0.22f).SetEase(Ease.OutQuad);
            SetBloomGlow(EDIT_GLOW_HDR, EDIT_GLOW_ALPHA_HIGH, forceDesktop: true);
            _editGlowPulse?.Kill();
            _editGlowAlpha = EDIT_GLOW_ALPHA_HIGH;
            _editGlowPulse = DOTween.To(() => _editGlowAlpha,
                    x => { _editGlowAlpha = x; SetBloomGlow(EDIT_GLOW_HDR, x, forceDesktop: true); },
                    0f, 0.22f)
                .SetEase(Ease.OutQuad)
                .OnComplete(() => { if (this != null && !_editSelected) ClearBloomGlow(); });
        }

        /// <summary>Quick "button push" on tap — a fast scale-DOWN press that springs back to rest (was a
        /// scale-UP pop). Used on the tiles you tap to edit/swap so the press feels tactile. 2026-07-10 Spencer.</summary>
        public void ButtonPushScale()
        {
            Vector3 rest = _editSelected ? _editBaseScale : CanonicalRestScale();
            transform.DOKill();
            transform.localScale = rest;
            transform.DOScale(rest * 0.90f, 0.08f).SetEase(Ease.OutQuad)
                .OnComplete(() =>
                {
                    if (this == null) return;
                    transform.DOScale(rest, 0.24f).SetEase(Ease.OutBack, 2.4f)
                        .OnComplete(() => { if (this != null) transform.localScale = rest; });
                });
        }

        /// <summary>After a swap/edit resolves, let the teal glow "die out to white" — fade the face tint + the
        /// additive teal bloom off over `dur`. If the tile ended up PRIMED (the swap formed a word), skip the
        /// white fade and just drop the edit bloom so the primed glow stands. Stops the pulse + clears the
        /// selected flag. 2026-07-10 Spencer.</summary>
        public void FadeEditGlowOut(float dur = 0.1f)
        {
            _editSelected = false;
            _editGlowPulse?.Kill(); _editGlowPulse = null;
            bool primed = _hasPrimedGlow;
            if (_spriteRenderer != null && !primed)
            {
                _spriteRenderer.DOKill();
                _spriteRenderer.DOColor(Color.white, dur).SetEase(Ease.OutQuad);
            }
            if (!primed)
            {
                _editGlowAlpha = Mathf.Max(_editGlowAlpha, EDIT_GLOW_ALPHA_HIGH * 0.5f); // start the fade from a lit value
                _editGlowPulse = DOTween.To(() => _editGlowAlpha,
                        x => { _editGlowAlpha = x; SetBloomGlow(EDIT_GLOW_HDR, x, forceDesktop: true); },
                        0f, dur)
                    .SetEase(Ease.OutQuad) // drop fast off the top so the glow diminishes quickly after the exchange
                    .OnComplete(() => { if (this != null) { ClearBloomGlow(); _editGlowPulse = null; } });
            }
            else ClearBloomGlow(); // primed → drop the edit bloom, let the primed glow take over
        }

        /// <summary>Steady (non-pulsing) bright teal hold used DURING a swap's shake/exchange so both tiles read
        /// as "charged" through the wobble. Re-applied right after SetLetter (which wipes the face tint back to
        /// white — the "swapped tile isn't still teal" bug). No scale pop, no breathe; FadeEditGlowOut ends it.
        /// 2026-07-10 Spencer.</summary>
        public void HoldEditGlow()
        {
            _editGlowPulse?.Kill(); _editGlowPulse = null;
            if (_spriteRenderer != null)
            {
                _spriteRenderer.DOKill();
                _spriteRenderer.color = EDIT_GLINT_HIGH;
            }
            _editGlowAlpha = EDIT_GLOW_ALPHA_HIGH;
            SetBloomGlow(EDIT_GLOW_HDR, EDIT_GLOW_ALPHA_HIGH, forceDesktop: true);
        }

        /// <summary>2026-06-03 Spencer: the same springy "pop" used on edit toggle-off,
        /// fired when a primed word DIFFUSES back to a normal tile (it didn't detonate
        /// — fuse expired or letters changed). Gives the player a visual cue that the
        /// tile reverted. Pops up from the current rest scale and settles back.</summary>
        public void PlayDiffusePop()
        {
            // 2026-06-04 Spencer: pop from the CANONICAL rest, not the live transform,
            // so a diffuse landing mid-animation can't leave the tile inflated.
            Vector3 baseScale = CanonicalRestScale();
            transform.DOKill();
            transform.localScale = baseScale;
            transform.DOScale(baseScale * EDIT_POP_SCALE, EDIT_POP_DUR)
                .SetEase(Ease.OutBack, 1.7f)
                .OnComplete(() =>
                {
                    if (this == null) return;
                    transform.localScale = baseScale;
                    RevertDiffuseLook(); // pop done → NOW revert primed→normal (signifies the state change)
                });
        }

        /// <summary>Revert the kept-primed look to a normal tile at the END of the diffuse pop, so the
        /// scale-up reads as the cue and the colour change lands after it. 2026-06-18 Spencer.</summary>
        private void RevertDiffuseLook()
        {
            if (!_pendingDiffusePop) return;
            _pendingDiffusePop = false;
            if (_spriteRenderer == null) return;
            if (!_isVault && !_isStone && !_isGoldBonus && !_isWild
                && !_isSwapRefill && !_isEditRefill && !_isWildRefill && !_isShowingScoredSprite && !_hasPrimedGlow)
            {
                _spriteRenderer.sprite = s_spriteNormal;
                _spriteRenderer.color  = Color.white;
            }
        }

        /// <summary>2026-06-04 Spencer: the tile's canonical cell-derived rest scale.
        /// Prefers the value captured at setup; falls back to a fresh cell computation
        /// if setup hasn't run yet. NEVER reads the live transform (which may be mid
        /// animation) so edit/diffuse pops can't compound the tile bigger.</summary>
        private Vector3 CanonicalRestScale()
        {
            if (_restScaleSet) return _restScale;
            float native = (_spriteRenderer != null && _spriteRenderer.sprite != null
                && _spriteRenderer.sprite.bounds.size.x > 0.0001f)
                ? _spriteRenderer.sprite.bounds.size.x : 1f;
            float s = (_cellSize * TILE_DISPLAY_RATIO) / native;
            return new Vector3(s, s, 1f);
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
            bool sentinel = (letter == '\0' || letter == TileBag.WILD_CHAR || letter == '?');
            if (_letterTMP != null)
                _letterTMP.text = sentinel ? "" : letter.ToString().ToUpper();
            if (_pointTMP != null)
                _pointTMP.text = ""; // point values removed — cleaner tiles; score still tallies under the hood
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

            // Regular white tile = the baked glossy tile (white_glossy, 80% crop matched to white5's
            // bounds → true drop-in). Chosen over the tester PSD in the A/B compare. 2026-06-10 Spencer.
            // This block also builds the board drop-shadows.
            Sprite whiteRef = Resources.Load<Sprite>("Tiles/white5@2x");
            float refBounds = (whiteRef != null && whiteRef.bounds.size.x > 0.0001f) ? whiteRef.bounds.size.x : 1f;
            Sprite loadedNormal = whiteRef;
            Texture2D glossyTex = Resources.Load<Texture2D>("Tiles/white_glossy@2x");
            if (glossyTex != null && refBounds > 0.0001f)
            {
                const float GLOSSY_FILL = 0.80f;
                float ppu = glossyTex.width / (refBounds / GLOSSY_FILL);
                float m = (1f - GLOSSY_FILL) * 0.5f * glossyTex.width;
                float cw = GLOSSY_FILL * glossyTex.width;
                loadedNormal = Sprite.Create(glossyTex, new Rect(m, m, cw, cw), new Vector2(0.5f, 0.5f), ppu);
                s_dropShadowSpriteA = MakeBoardShadow(BoardShadowTexA, ppu);
                s_dropShadowSpriteB = MakeBoardShadow(BoardShadowTexB, ppu);
                s_dropShadowSprite  = s_useBoardShadowB ? s_dropShadowSpriteB : s_dropShadowSpriteA;
            }

            Sprite loadedPrimed = Resources.Load<Sprite>("Tiles/pink_tile@2x");
            // 2026-06-04 Spencer: new glossy primed (pink) tile — same content-rect/PPU
            // drop-in treatment as the white tile (1024px, 80% fill). Replaces loadedPrimed
            // so all primed/heat states below pick it up.
            Texture2D primedTex = Resources.Load<Texture2D>("Tiles/primed_tile@2x");
            if (primedTex != null && loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
            {
                const float PRIMED_FILL = 0.80f;
                float targetBounds = loadedNormal.bounds.size.x; // = the (glossy) tile size
                float pppu = primedTex.width / (targetBounds / PRIMED_FILL);
                float pm = (1f - PRIMED_FILL) * 0.5f * primedTex.width;
                float pcw = PRIMED_FILL * primedTex.width;
                loadedPrimed = Sprite.Create(primedTex, new Rect(pm, pm, pcw, pcw), new Vector2(0.5f, 0.5f), pppu);
            }
            Sprite loadedAI     = Resources.Load<Sprite>("Tiles/ai_tile");

            Sprite loadedScored = Resources.Load<Sprite>("Tiles/green_tile2@2x");
            // 2026-06-04 Spencer: new glossy green tile (greeny). It's trimmed to the
            // tile (100% fill), so use the FULL rect at a PPU that matches the white
            // tile's bounds — true drop-in, same size as the rest.
            Texture2D greenyTex = Resources.Load<Texture2D>("Tiles/greeny@2x");
            if (greenyTex != null && loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
            {
                float gppu = greenyTex.width / loadedNormal.bounds.size.x;
                loadedScored = Sprite.Create(greenyTex, new Rect(0, 0, greenyTex.width, greenyTex.height),
                                             new Vector2(0.5f, 0.5f), gppu);
            }
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
            // 2026-06-05 Spencer: base the squish on the CANONICAL rest scale, NOT the live
            // transform. An edit committed mid-pop left the tile inflated, and capturing
            // that as baseScale baked the inflated size in permanently (tile no longer
            // matched its neighbours). Tiles always land at rest, so rest is the correct
            // base for every caller — and it makes the mid-pop inflation impossible.
            Vector3 baseScale = CanonicalRestScale();
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
            // 2026-06-05 Spencer: settle to the CANONICAL rest scale (same fix as the
            // landing squish) so a tile that squishes during gravity/cascade while
            // mid-pop can't bake an inflated size in as its new rest.
            Vector3 baseScale = CanonicalRestScale();

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

            // The fake-3D tilt warps the tile FACE in-shader, but the flat drop shadow (a separate sprite)
            // can't follow — it looked like the shadow peeled off. A tilted tile is lifted off the board, so
            // fade its ground shadow out with the tilt (full when flat, gone when tilted). 2026-06-17 Spencer.
            if (_dropShadowSR != null)
            {
                float a = Mathf.Clamp01(1f - (Mathf.Abs(rotX) + Mathf.Abs(rotY)) / 16f);
                var c = _dropShadowSR.color; c.a = a; _dropShadowSR.color = c;
                _dropShadowSR.enabled = !_isDropTargetVisual && a > 0.02f; // chicken escort has no tile shadow
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
                // Tilt's gone (tile flat again) — bring the ground shadow fully back (except chicken escorts).
                if (_dropShadowSR != null && !_isDropTargetVisual) { _dropShadowSR.enabled = true; var c = _dropShadowSR.color; c.a = 1f; _dropShadowSR.color = c; }

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
            _bakedRenderer.sortingOrder = _spriteRenderer.sortingOrder + 2; // above the gloss sheen (face+1)

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
