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

        // ═══════════════════════════════════════════════════════════════════════════
        //  TILE PALETTE — ONE MEANING PER HUE (2026-09-08 Spencer)
        //
        //  The rule: HUE = WHAT IS HAPPENING, not what the tile is.
        //  Before this there were 24 tile colours and hues meant several things at once —
        //  orange was "fuse expiring" AND "escort target" AND "swap refill" AND "gold
        //  border"; cyan was "selected" AND "edit refill" AND "wild". That is why the
        //  board read as arbitrary. Most of those belonged to DROP-game mechanics that
        //  the swap pivot removed, so this is mostly deletion, not reassignment.
        //
        //    tan      resting tile
        //    gold     high-value letter (V K J X Q Z) — at rest AND armed (hotter)
        //    magenta  ARMED
        //    green    SCORING right now
        //    cyan     SELECTED for a swap
        //    red      TOP-OUT DANGER  (and nothing else — see PRIMED_DANGER_GLOW below)
        //    grey     blocker: stone
        //    pale blue blocker: ice
        //
        //  Anything below marked LEGACY-DROP belongs to a removed mechanic. It is kept
        //  only because call sites still compile against it; those tiles no longer spawn.
        // ═══════════════════════════════════════════════════════════════════════════

        private static readonly Color TILE_FILL_COLOR    = new Color(0.973f, 0.961f, 0.937f, 1f);     // warm cream #F8F5EF
        // 2026-06-24 Spencer: per-letter value-accent tinting was REMOVED (it read as confusing — color the
        // player has to decode). Tiles use a single uniform resting colour again. The _baseTint /
        // ApplyRestingColor plumbing is kept (it just restores the uniform colour) so the state reverts
        // stay centralised in one place.
        private Color _baseTint = Color.white; // uniform resting tile colour
        private static readonly Color TILE_BORDER_NORMAL = new Color(0.800f, 0.745f, 0.640f, 1f);  // desaturated warm tan
        private static readonly Color TILE_BORDER_GOLD   = new Color(1.000f, 0.720f, 0.180f, 1f);  // bright rich gold-orange #FFB82E

        // Kept as compile stub for any callers that reference it
        // PALETTE 2026-09-08: was CYAN, which collided with "selected for swap". A wild's
        // letter is already purple (WILD_LETTER_COLOR), so the border matches it — wild
        // now reads as one idea instead of borrowing the selection hue.
        public static readonly Color TILE_BORDER_WILD = new Color(0.75f, 0.40f, 1.00f, 1f);

        // 2026-09-09 Spencer: hue 323 -> 335 to match the ROSE armed tile. The scored flash
        // fires in this colour and then settles onto the tile, so a mismatch between the two
        // reads as a hue shift at the moment a word arms — "really hot magenta to the new
        // pink". Same saturation (72%) and magnitude, hue only. This also drives the fly-up
        // and detonation colours, so they follow the tile automatically.
        public static readonly Color PRIMED_GLOW       = new Color(1.8f, 0.50f, 1.04f, 1f);  // HDR rose — bloom catches this

        // ── ARMED GLOW (2026-09-08, Spencer) ────────────────────────────────────────
        // The armed pulse used to push the face into HDR (cap 1.55) AND run an additive
        // bloom overlay. Against the LIGHTENED tile set that reads as blown out — the
        // magenta loses its form. PRIMED_BLOOM=false keeps the pulse (tint + scale
        // breathe) but caps the face at LDR and mutes the overlay, so nothing glows
        // until the word actually detonates. The explosion FX are a separate path and
        // are untouched. Flip back to true to restore the old look.
        public const  bool  PRIMED_BLOOM   = false;

        // ── ARMED TILES ARE STILL (2026-09-08, Spencer) ──────────────────────────────
        // "we are just leaving words primed now so no escalation or speed and timing
        // should even be in the game for this version."
        // The heartbeat, the scale breathe, the jitter and the heat-tier sprite swaps all
        // existed to communicate a FUSE — how many turns until this word expires. Words no
        // longer expire (LevelTable L11 fuseOff:true), so all of it was animating a rule
        // that is gone, and twelve linked tiles each ran their own clock.
        // An armed tile now simply IS magenta (or gold). Flip true to restore the pulse.
        public const  bool  PRIMED_ANIMATION = false;

        // 2026-09-09 Spencer: dark ink on the armed tile was the weakest text on the board —
        // near-black on a mid-tone face, while every other tile is near-black on a pale one.
        // White also makes ARMED read as a different STATE, not just a different colour.
        // Remembers the outgoing colour and restores it, rather than assuming ink: twelve
        // places set the letter colour (wild purple, stone, frozen...), so writing a
        // hardcoded value back would stomp them.
        public const  bool  ARMED_LETTER_WHITE = true;
        private Color _preArmedLetterColor;
        private bool  _letterWhitened;
        public const  float PRIMED_LDR_CAP = 1.0f;   // 1.0 = no bloom at all; 1.28 sits just under the bloom line

        /// <summary>Override for ResetVisuals's "restore default sortingOrder"
        /// step. When > 0, ResetVisuals will set the tile's sortingOrder to
        /// this value instead of the baseline 5. Used by BoosterHUDSlot during
        /// booster aim mode so tiles stay above the scrim even if a cleanup
        /// path (rewrite cancel, drop complete, etc.) fires mid-aim.
        /// Reset to 0 when aim mode ends.</summary>
        public static int AimModeTileOrder = 0;
        private int _spotlightOrder = -1; // tutorial per-tile spotlight: >=0 raises THIS tile above the dim scrim
        public static bool SpotlightActive = false; // true while a tutorial spotlight/scrim is up (guards FX re-bumps)
        // 2026-09-09 Spencer: "when i explode it its turning orange. just make it gold."
        // The old (2.0, 1.5, 0.3) has a ratio of 1 : 0.75 : 0.15, and once brought into
        // displayable range that reads ORANGE. The gold TILE is 235:181:76 = 1 : 0.77 : 0.32 —
        // roughly double the blue. Matched to the tile so the detonation, the fly-up and the
        // flash all read as the same gold the player has been looking at. Same fix as the rose
        // glow: a glow whose hue does not match its tile shows up as a colour shift.
        /// <summary>Resting tint for a GOLD-armed tile — THE ORIGINAL VALUE, recovered.
        ///
        /// This is what the code computed before PRIMED_ANIMATION was turned off: the branch
        /// below did Lerp(white, PRIMED_GOLD_GLOW, 0.58) then scaled into LDR, giving
        /// (1.00, 0.831, 0.501). Disabling the armed pulse took that whole branch out of play
        /// and the method returned plain WHITE instead — which is when the bright gold Spencer
        /// remembers vanished. It was never deliberately changed; it was collateral.
        ///
        /// Earlier on 2026-09-10 I derived (1.00, 0.77, 0.32) from the scored FLASH instead.
        /// That is the flash's peak multiply, which is a momentary hit with bloom behind it —
        /// not the colour a tile settles at. Blue 0.32 vs 0.50 is the whole difference between
        /// a dark amber and a bright saturated gold.</summary>
        /// <summary>Read-only state for BoardInvariants — these let the game check itself
        /// instead of relying on someone spotting a fault on screen. 2026-09-10 Spencer.</summary>
        public bool HasPrimedGlow    => _hasPrimedGlow;
        public bool LetterIsWhitened => _letterWhitened;
        public int  SortingOrderNow  => _spriteRenderer != null ? _spriteRenderer.sortingOrder : 0;
        /// <summary>THE answer to "is this tile gold-primed", for every path that needs it.
        ///
        /// Three sources could disagree: what the caller passed, this tile's own letter, and
        /// what the registry says about the cell. The scored-flash path asked only the first
        /// two, so a gold word could still put ROSE art on a tile — and gold tinted over rose
        /// art is the peach Spencer kept seeing. Gold wins if ANY source says gold.
        /// 2026-09-10 Spencer.</summary>
        public bool ResolveGoldPrimed(bool callerSaysGold)
        {
            // STICKY. Once this tile is known to be gold-primed it stays gold until it stops
            // being primed at all. Some paths paint a tile BEFORE its word reaches the registry,
            // and at that instant every source honestly answers "not gold": the caller does not
            // know yet, the letter itself may be ordinary (the E and T of JET), and the registry
            // has no word. So the tile went rose, then flipped gold a moment later when the
            // registry caught up — the rose-during-the-flash Spencer kept seeing.
            // Gold wins means gold does not un-win. 2026-09-10 Spencer.
            if (_isGoldPrimed && _hasPrimedGlow) return true;
            if (callerSaysGold || IsGoldLetter || IsGem) return true;   // a gem in the word = the gem (gold) armed look
            var c = RulesEngine.PrimedGlowForCell(new Vector2Int(Col, Row));
            return ColorsClose(c, PRIMED_GOLD_GLOW);
        }

        public bool IsGoldPrimedState    => _isGoldPrimed;
        /// <summary>Which armed art is on this tile right now — for diagnostics that need to
        /// name what they are looking at, not just say "wrong". 2026-09-10 Spencer.</summary>
        public string ArmedArtName
        {
            get
            {
                var sp = _spriteRenderer != null ? _spriteRenderer.sprite : null;
                if (sp == null) return "none";
                if (sp == s_spritePrimedGold) return "GOLD-primed";
                if (sp == s_spriteScored)     return "SCORED-green";
                if (sp == s_spriteGoldThick)  return "ROSE-armed";
                if (sp == s_spriteGold)       return "ROSE-primed";
                if (sp == s_spriteHighValue)  return "gold-resting";
                if (sp == s_spriteNormal)     return "plain";
                return sp.name;
            }
        }

        public bool ShowingGoldPrimedArt => _spriteRenderer != null && s_spritePrimedGold != null
                                            && _spriteRenderer.sprite == s_spritePrimedGold;

        public static bool GOLD_FLICKER_DIAG = false;   // temp: trace gold→rose→gold repaints

        // ── WARM RESTING TILE (2026-09-10) ─────────────────────────────────────────
        // MEASURED: the resting face is #E2E5CB — hue 67 deg, saturation 33%. 67 deg is
        // yellow-GREEN, which is why Spencer read it as "too tanned"; the code above already
        // calls it "the tan resting face". Every warm game piece in his five reference games
        // sits at hue 46-48 with far more saturation (Toon Blast cube 48/81, Royal Match gold
        // 47/47). This rotates the resting tile texture to ~46 deg and lifts saturation.
        //
        // Done as a PER-PIXEL HUE ROTATION on the texture at cache-build, NOT as an
        // sr.color tint. A multiply tint was tried on the rose tiles and reverted because it
        // FLATTENS THE BEVEL (it scales every channel down, crushing the highlight). Rotating
        // hue while leaving V alone keeps the bevel and the gloss exactly intact.
        //
        // It also avoids touching the dozen sites that set _spriteRenderer.color = white for
        // the normal face — the art itself changes, so every path picks it up for free.
        // Costs one blit + readback + per-pixel pass ONCE at cache build.
        // ⚠️ DEFAULT OFF — THE PREMISE WAS WRONG. Corrected 2026-09-10 by measuring the actual
        // tile FACE instead of a quantized region average:
        //     real tile face  #F4E0B8  hue 40, sat 73, L 88   <- already a warm golden cream
        //     what I "measured" #E2E5CB hue 67, sat 33         <- faces BLENDED with the dark
        //                                                        gaps and shadows between them
        // Royal Match gold is hue 47 — so the resting tile was ALREADY in range and needed
        // nothing. Turning this on shifted it to hue 20 (SALMON) and cost 10 points of
        // brightness. Exactly the trap in [[feedback-measure-the-source-not-the-pixels]]:
        // measure the ASSET, not composited screen pixels.
        // Spencer did say he "liked the look better of the gold tiles" with it on — so if he
        // wants the peach back it is one flag, but it is a taste choice, NOT a correction.
        // ── BAKED CONTACT EDGE / AO (2026-09-10) ───────────────────────────────────
        // Spencer: "notice the ambient occlusion between the tiles in toon blast. they create
        // real separation between the blocks." He is right, and it is the fix for the oldest
        // complaint in this pass — a settled row reads as ONE SLAB of cream rather than nine
        // movable pieces, which fights the swap verb.
        //
        // MEASURED on toonblast.png: their separation is three things stacked — a thin dark gap
        // showing the board, each cube shading DOWN toward its own perimeter, and a top-lit
        // gradient so the dark bottom of one cube meets the bright top of the next.
        //
        // The existing TILE_DROP_SHADOW was disabled 2026-09-03 ("just muddies the seams") —
        // correctly, because it is a SOFT OFFSET DROP shadow, which smears on an overlapping
        // board. What is wanted is a TIGHT CONTACT EDGE inside the tile's own silhouette.
        //
        // Method: box-blur the alpha channel to get an "inside-ness" mask, then darken where
        // that mask is below 1 — i.e. a soft dark rim hugging the edge. Baked into the texture
        // once at cache build, so it costs ZERO extra renderers (unlike the 99-renderer cell
        // liner that had to be switched off for frame drops).
        // 2026-09-10: started at 0.34 / 0.055 / 1.7 — Spencer: "looks better but it should
        // probably be even more". Pushed up, and the reference agrees: TB's seams measure ~55%
        // darker than the cube face, so 0.34 was well under.
        // ── LETTERPRESS / DEBOSSED LETTERS (2026-09-10) ────────────────────────────
        // Spencer, off the Scrabble GO tile: "a light coloured inner shadow or drop shadow
        // underneath the black letters that makes it look almost like it's grooved into the
        // tile." That is a LETTERPRESS (deboss), and the physics is why it works: a groove lit
        // from above has its TOP inner wall in shadow and its BOTTOM wall catching light. The
        // glyph is already dark, so the cue is that lit lower lip — i.e. a LIGHT underlay,
        // offset DOWNWARD.
        //
        // NOTE the June note further down: TMP underlay was disabled 2026-06-03 as a *dark*
        // drop shadow. Re-enabling it with those old values gives a drop shadow, NOT this.
        // The colour is the whole point — light, not dark.
        //
        // Could NOT be confirmed on the reference: at ~80px per tile in a compressed
        // screenshot, pixels 3px below a letter edge average L=83.2 vs 84.4 above, which is
        // noise. Spencer called it "very faint"; it is below what that image can resolve.
        // So these numbers are a sensible starting point, not measured truth — tune by eye.
        /// <summary>Letter colour. 2026-09-11: was #252733, a blue-grey. The Monopoly dice dots
        /// measure #0A0A0A — near-black and fully neutral. One constant so every state uses it;
        /// it was previously repeated as a literal at ~8 call sites.</summary>
        /// <summary>Letter ink = THE BOARD'S OWN HUE, dark and saturated — not neutral black.
        /// 2026-09-15, from the reference board Spencer sent.
        ///
        /// MEASURED off that reference, and it is the whole trick: its letter ink is hue **220.3**
        /// and its board is hue **220.5** — the SAME hue. The letters are literally the board
        /// colour at a lower value. Its entire screen is two hues (~220 blue, ~35 orange) with
        /// everything else a value/saturation variation of one of them.
        ///
        /// Ours was `#0A0A0A` — neutral black, **saturation 0, no hue at all** — which made the
        /// ink the one element on the board belonging to NEITHER family. Now hue 200 (our teal
        /// board), sat 0.70, value 0.26 = `#133242`. Same principle as ROCK_WORLD_TINT, applied
        /// to type instead of blockers.
        ///
        /// If the board is ever pushed toward a true navy, move this with it — `#162A54`
        /// (hue 220) is the reference's exact ink. The two must stay on the same hue.</summary>
        public static Color LETTER_INK = new Color(0.0078f, 0.1490f, 0.3451f, 1f);   // #022658 — 2026-09-21 Spencer

        public static bool  LETTER_EMBOSS          = true;
        public static float LETTER_EMBOSS_ALPHA    = 0.55f;   // strength of the lit lip
        public static float LETTER_EMBOSS_OFFSET   = -0.35f;  // NEGATIVE = below the glyph
        public static float LETTER_EMBOSS_SOFTNESS = 0.08f;
        public static Color LETTER_EMBOSS_COLOR    = new Color(1f, 0.98f, 0.90f, 1f); // warm near-white

        // ── HIGH-VALUE TILE RECOLOUR (2026-09-10) ──────────────────────────────────
        // MEASURED, and this is the whole problem: white_glossy@2x is hue 43 / HSV sat 0.233
        // and highvalue_tile@2x is hue 42 / HSV sat 0.667 — the SAME HUE. The only thing that
        // ever marked a high-value letter was SATURATION. So when the resting tile was pushed
        // to Scrabble GO's gold (sat 0.61) the two collided and J/Q/X/Z stopped reading as
        // special — Spencer: "good but you changed all the tiles lol".
        // Fix: move high-value to a genuinely HOTTER hue, so "warmer = worth more" still reads
        // at a glance even though both are now gold-family.
        // 2026-09-10 — the gold Spencer approved came from taking white_glossy (HSV sat 0.233)
        // and MULTIPLYING saturation x2.62 with a +10 deg hue shift. A multiply keeps the art's
        // RELATIVE structure: highlights stay pale, shadows go rich, and it reads as GOLD.
        // Forcing the high-value art to an ABSOLUTE hue instead made every pixel the same hue —
        // zero variation — which is exactly what "flat yellow" looks like. Spencer: "its reading
        // as yellow not as gold... you had it exactly right before when you coloured all the
        // tiles gold. i just need that gold with the cream tiles."
        // So: build the GOLD face from the SAME source and the SAME transform, and leave the
        // resting face untouched.
        public static float GOLD_HUE_SHIFT = 10f;
        public static float GOLD_SAT_MUL   = 2.62f;

        /// <summary>Use the Cube World `gold_block` render as the gold-resting face instead of
        /// the hue-shifted white tile. 2026-09-11, Spencer: "change over our current gold tiles
        /// to gold_block". Set false to go back to the GOLD_HUE_SHIFT / GOLD_SAT_MUL recolour —
        /// those knobs are untouched and still live, so this is a straight A/B.
        /// NOTE this is the GOLD-RESTING face (high-value letters, `s_spriteHighValue`), NOT
        /// `s_spriteGolden` (the gold BONUS cell) and NOT `s_spriteGold` (which, despite the
        /// name, is the PRIMED face used by every primed tile — see the trap note below).</summary>
        // 2026-09-11: TRIED AND REVERTED. Spencer: "lets just make it easy and go back to the
        // old golden tiles". The art/size problems were all solved (see the session log) but the
        // cube's 26%-tall TOP FACE band sits where a letter tile has a ~5% bevel, so the letter
        // reads as badly centred inside the visible gold face. That is a GEOMETRY mismatch, not
        // something a colour or size tweak fixes. Flip to true to look at it again — everything
        // it needs is still in place (gold_block_flat@2x, the knob below, the fallback chain).
        public static bool  GOLD_BLOCK_FACE    = false;

        /// <summary>Gamma lift on the gold_block art (v^(1/g)), NOT a multiply — a multiply clips
        /// the highlights and flattens the facets. 1.0 = the pack's native colour, which is what
        /// Spencer chose for the rocks. MEASURED 2026-09-11: raw gold_block is L 153 vs
        /// white_glossy 212 and golden_tile2 183, so **1.54 matches golden_tile2's brightness**
        /// if it reads too dark on the board.</summary>
        public static float GOLD_BLOCK_LIGHTEN = 1.0f;

        public static bool  HIVAL_RECOLOR = true;
        // 2026-09-10: the Scrabble GO gold goes HERE, on the high-value tile only — not on the
        // resting face. MEASURED: Scrabble tile #F8DC58 = hue 50, HSV sat 0.645.
        // highvalue_tile@2x is already hue 42 / HSV sat 0.667, so this is a small nudge:
        // +8 deg of hue and saturation essentially unchanged.
        public static float HIVAL_HUE     = 50f;    // Scrabble GO gold
        public static float HIVAL_SAT_MUL = 0.97f;  // 0.667 -> ~0.645

        /// <summary>Master switch for `AoFace`/`ContactEdgeCopy` on every letter face.
        /// Turned off then straight back ON 2026-09-15 at Spencer's request.
        ///
        /// 🔑 NOTE THE STATE THIS LEFT BEHIND, AND IT IS BETTER THAN BEFORE: the AO that my
        /// palette transfer had BAKED into the front-on PNGs was stripped during the off pass and
        /// has deliberately NOT been put back. So AO is now applied exactly ONCE, at runtime,
        /// instead of baked-in AO *plus* this pass — which is what the front-on art briefly had.
        /// Do not re-bake AO into the PNGs; this switch is the only place it belongs.</summary>
        public static bool  TILE_AO          = false;  // OFF 2026-09-21 — ContactEdgeCopy paints a
        // dark rim round the silhouette. On Spencer's hand-painted peach tile that renders as a
        // RUSTY BROWN ring ("the colours are wrong") and muddies the edge ("not clean sprites").
        // His art already carries its own edge shading, so this is double-shading authored work.
        // 2026-09-11: 0.52 left the sides almost white. The reason is that the blurred alpha only
        // reaches occl ~0.5 at the edge (not 1), so the sides were landing at 1 - 0.5*0.52 = 0.74
        // — just 26% darker than the face, which is nothing on a WHITE tile. TILE_AO_FLOOR never
        // even engaged there; it only bit at the bottom edge where the 2.2x boost applies.
        // STRENGTH is the lever for "how dark", FLOOR only caps the extreme.
        public static float TILE_AO_STRENGTH = 0.72f;   // eased 2026-09-11 — the rim was eating into the face
        // 0.080 reached ~32% in from EVERY edge, so on a 249px tile the "rim" was covering most
        // of the face and dimming it. 0.060 keeps it a rim. 2026-09-11.
        public static float TILE_AO_WIDTH    = 0.060f;
        public static float TILE_AO_BOTTOM   = 2.2f;    // extra darkening on the BOTTOM edge (top-lit)
        // Shadow COLOUR, per [[reference-icon-stroke-shadow-recipe]]: a shadow is made of the
        // occluded SURFACE, not of the object casting it — so it is not a neutral darkening.
        // The recipe there is hue -18 deg toward violet, saturation x1.05, value down. Shifting
        // hue and LIFTING saturation is what stops a shadow reading muddy-grey.
        // TILE_AO_TINTED=false reverts to the plain neutral multiply.
        // 2026-09-11: OFF to match the dice. Its shadow planes measure sat 0.01-0.03 — neutral
        // grey, no violet. The tinted-shadow recipe is right for a COLOURED surface; a white
        // die shades to grey.
        public static bool  TILE_AO_TINTED   = false;
        public static float TILE_AO_HUE_ROT  = -18f;   // degrees, toward violet
        public static float TILE_AO_SAT_MUL  = 1.05f;  // shadows are slightly MORE saturated

        /// <summary>Darkest the AO rim may go, as a fraction of the face value. MEASURED on the
        /// Monopoly dice: its deepest shadow plane is 0.58x the lit face — it never approaches
        /// black. Without this floor the rim clamps to 0 and reads as a drawn outline.</summary>
        // 0.58 = the dice's deepest plane, but that is a whole FACE lit at an angle; a tile RIM
        // reads lighter than a face plane at the same value, so it needs to go further.
        // Spencer 2026-09-11: "the shading on the white tiles needs to be a bit darker".
        public static float TILE_AO_FLOOR    = 0.44f;

        // ── BAKED SPECULAR GLINT (2026-09-10) ──────────────────────────────────────
        // Spencer: "its not there yet. im guessing its probably cause the tiles are missing
        // any kind of a highlight glint." Correct — and it is a side effect of the cream:
        // the gloss IS baked into white_glossy@2x, but a baked gloss reads as a PALER, LESS
        // SATURATED patch on a more saturated face. Dropping the face saturation to ~0.06 made
        // the face as pale as the highlight, so the glint collapsed into it.
        // The old separate gloss layer (GlossEnabled, below) is left OFF on purpose — it is one
        // SpriteRenderer per tile, the same 99-renderer cost that got the cell liner disabled.
        // Baked into the texture instead: zero extra draws.
        // 2026-09-11: TMP was applying FontStyles.Bold ON TOP of the tile font. That made sense
        // when the font was AvenirNext SDF (a REGULAR weight that needed the boost) — but with an
        // already-bold face like Fredoka-Bold it is SYNTHETIC DOUBLE-BOLD: strokes fatten, the
        // counters in G/B/O close up, and it reads "blobby" (Spencer's word). Off by default now
        // that the tile font is a bold cut. Set true if you switch back to a regular-weight font.
        public static bool  TILE_FONT_FAUX_BOLD = false;

        // 2026-09-11: OFF. Replaced by the BAKED SPECULAR below, which is computed from the real
        // mesh normals in Blender instead of being a gaussian blob guessed from the silhouette.
        // Two highlight systems would fight; this one was always in the wrong place.
        public static bool  TILE_GLINT          = false;
        public static float TILE_GLINT_STRENGTH = 0.30f;  // 0 = off; how far toward white
        public static float TILE_GLINT_Y        = 0.74f;  // 0 bottom .. 1 top
        public static float TILE_GLINT_SPREAD   = 0.22f;  // vertical softness
        public static float TILE_GLINT_WIDTH    = 0.40f;  // horizontal softness (streak width)

        // ── BAKED SPECULAR HIGHLIGHT (2026-09-11) ──────────────────────────────────
        // Spencer: "should you maybe try in blender adding a rimlight on the model to see if you
        // can get the correct look on the geometry?" — correct, and it is why the 2D attempts
        // kept landing wrong. A highlight guessed from a SILHOUETTE cannot know which way the
        // surface points; this is rendered from the actual mesh normals.
        //
        // WHAT FAILED FIRST, so nobody repeats it:
        //  • 2D gaussian blob on the face  — no relationship to the geometry, reads as a smudge.
        //  • Fresnel RIM pass in Blender   — peaks where the surface grazes the CAMERA. With this
        //    near-front-on camera that is the top CAP and the outer silhouette, NEVER the
        //    cap/face divide. Measured: cap 0.66, divide 0.10. Wrong by construction.
        //  • BLINN-PHONG SPECULAR          — ✅ peaks where the normal bisects light and view, so
        //    with the light above and slightly forward it lands exactly on the divide and follows
        //    the corner curvature for free. `PASS=spec` in cutebox.py (SHIN 40, SPZ 1.0).
        //
        // Baked to Tiles/tile_spec@2x.png, aligned to the tile art's silhouette. Composited in
        // the same texture pass as the AO, so ZERO extra renderers/draws.
        // Applied to ALL face states. NOTE the history: when this map was a SHARP specular band
        // it had to be restricted to white_glossy-derived faces, because the arts have different
        // cap heights (white_glossy 13%, highvalue 18%, vowel 18%, greeny 23%, primed different
        // again) and a hard edge landed visibly wrong — Spencer: "the glint looks odd on the
        // primed tiles". Now that it is a SOFT DIFFUSE gradient there is no edge to misplace, so
        // the restriction is unnecessary and costs the primed tiles their lighting.
        // ⚠️ If this ever goes back to a sharp highlight, restore the restriction.
        public static bool  TILE_SPEC          = true;
        /// <summary>Highlight opacity. 2026-09-11: 1.0 -> 0.78 at Spencer's request. Thickness is
        /// NOT set here — it is the Blender shininess (SHIN) the map was rendered at; currently 75
        /// (was 40). Re-render via cutebox.py PASS=spec to change it.</summary>
        // 2026-09-11: the map is now a DIFFUSE top-light, not a sharp specular band — see the
        // note above. Lower strength because it covers most of the face rather than a thin line.
        // 2026-09-11: cut 0.22 -> 0.07. MEASURED: the ndl map is FLAT at 0.616 across the lower
        // 60% of the face, so over most of the tile this was a CONSTANT multiplier — uniform
        // dimming with no gradient to show for it. The gradient is TILE_FACE_GRAD's job now; this
        // is left only for the variation near the curved edges.
        /// <summary>OFF 2026-09-15 — Spencer: "please remove the highlight glint as well for now
        /// / on the tiles". This is the BAKED specular band (via `tile_spec@2x` in
        /// ContactEdgeCopy(applySpec:true)) and was the last glint still live: TILE_GLINT and
        /// GlossEnabled were already false. Restore with 0.07f. It was tuned on the TILTED art,
        /// so re-measure rather than trusting 0.07 if it ever comes back on front-on tiles.</summary>
        public static float TILE_SPEC_STRENGTH = 0f;
        /// <summary>Nudge the band UP the tile, as a fraction of tile WIDTH. Spencer picked 16px
        /// on the 249px-wide art = 0.064. NOTE this is a straight VERTICAL shift — moving the
        /// light's elevation slides the band ALONG the surface and reshapes it, and offsetting
        /// along the divide's normal pushes it SIDEWAYS on the tile's sides. Neither is "up".</summary>
        // No nudge needed now: a diffuse gradient covers the face, so there is no band to place.
        public static float TILE_SPEC_UP       = 0.0f;

        // ── FACE FALLOFF (2026-09-11) ──────────────────────────────────────────────
        // Spencer: "something about the top of our tiles looks weird". MEASURED, and the cause is
        // geometric: WordDrop's tile front face is FLAT, so one normal covers the whole face and
        // diffuse lighting can only give it ONE value. The profile down a tile was a dark cap and
        // then L=140.3 for the entire rest — literally no gradient. The ndl map confirms it:
        // it varies over the top 40% (the curved edge) then sits flat at 0.616 all the way down.
        //
        // Toon Blast's cubes are slightly DOMED, so their front normal varies and you get a smooth
        // falloff. Measured on their cube: peak at t=0.33 from the top of the face, falling to
        // ~0.62 of peak by t=0.83. cutebox.py has BULGE for this but the header records
        // "BULGE=0 no bulge, no taper (decided)" — so this fakes the same read in post instead of
        // changing locked geometry.
        public static bool  TILE_FACE_GRAD          = true;
        public static float TILE_FACE_GRAD_STRENGTH = 0.11f;  // eased back so the lower face stays bright
        public static float TILE_FACE_GRAD_PEAK     = 0.33f;  // t from the TOP of the sprite

        // ── TOP-FACE (CAP) DARKENING (2026-09-11) ──────────────────────────────────
        // Spencer: "the shading is a lot darker than the face in the toon blast".
        // MEASURED on his Toon Blast cube:  cap L=139.6, upper face L=189.3 -> ratio 0.74.
        // WordDrop was at 0.82, i.e. the cap was too LIGHT relative to the face. 0.90 here takes
        // 0.82 -> ~0.74 to match. Only the cap is touched; the face keeps its own brightness.
        // ── ROCK / STONE TILE (2026-09-11) ─────────────────────────────────────────
        // Stone used to be a grey TINT on the letter tile (STONE_TINT) — same art, different
        // colour, so it never read as a different KIND of object. Now a real sprite: Bitgem
        // Cube World `block_stone1_1`, rendered through the tile camera + key light (elev 52 /
        // az 42) so it is lit identically, keeping the PACK'S OWN materials and colours
        // (Spencer picked the native render over a re-ramped one — the hard facets read as
        // crafted rather than rendered). Baked to Tiles/rock_tile@2x.png at 249x295.
        // MEASURED mean luminance 39 vs the cream tile's 88 — clearly separated at a glance.
        public static bool  ROCK_SPRITE     = true;
        /// <summary>Run the rock through the same contact-AO pass as the letter tiles so it sits
        /// on the board rather than floating. OFF = the raw baked art, untouched.</summary>
        /// <summary>OFF 2026-09-15 — Spencer: "remove the ao on all the renders of the tiles that
        /// are not ordinary tiles as well such as the wooden blanks, metal and rocks". The AO rim
        /// made the blockers read as recessed and grubby beside the clean letter faces.
        ///
        /// NOTE this is now DELIBERATELY ASYMMETRIC with `TILE_AO`, which stays TRUE for the
        /// letter tiles. Blockers keep only the shading baked into their own Blender render (real
        /// form, from the mesh) and get no contact-edge pass on top. `ROCK_AO_BOOST` is left as
        /// tuned so restoring is a single bool.</summary>
        public static bool  ROCK_USE_AO     = false;
        /// <summary>Lighten the rock. A GAMMA lift (v^(1/g)), not a multiply — a multiply would
        /// push the highlights into the ceiling and flatten the facets, which is the trap
        /// TILE_VAL_MUL fell into (it clipped 62% of the letter tile to white). Gamma leaves 0 at
        /// 0 and 1 at 1 and lifts everything between, so the facet structure survives.
        /// Scale: 1.0 -> L 39 (raw) · 1.45 -> L 52 · 2.0 -> L 62 · 2.6 -> L 68 · 3.5 -> L 74.
        /// The cream letter tile is L 88. Matching it exactly would wash the rock out and it would
        /// stop reading as an obstacle, so 2.0 keeps a clear gap while sitting in the same family.
        /// NOTE the rock already goes through the SAME ContactEdgeCopy pass as every letter tile
        /// (cap darkening, face gradient, contact AO) and is lit by the same key — brightness was
        /// the only thing left that made it look like a different kind of asset.</summary>
        /// <summary>FALLBACK ONLY — ROCK_VARIANT_LIGHTEN drives each variant. Was 1.35, which
        /// was compensating for the fbx_tile.py Metallic=1 bug; with that fixed the re-baked art
        /// lands at target on its own, so this is 1.0 and keeping 1.35 would double-correct.</summary>
        public static float ROCK_LIGHTEN    = 1.0f;  // mild — the atlas textures carry their own value now

        /// <summary>SURFACE CONTRAST — expands the value range around the rock's own mean, so the
        /// facets and the crack actually read at tile size.
        ///
        /// 2026-09-11: measured on screen the rock was #879687, L 57, **saturation 6.7%** — the
        /// gamma lift raised the value but flattened the sage out of it, leaving a plain mid-grey
        /// block with no visible surface. Spencer: does it "read like a stone block"? It did not.
        /// BRIGHTNESS IS THE WRONG LEVER: what makes something read as a MATERIAL is surface
        /// contrast, not lightness. 1.0 = off.</summary>
        /// <summary>1.25 -> 1.15: the re-baked art carries ~10% more contrast on its own
        /// (measured sd 30-39 before the rig fixes, 32-43 after), so the knob gives that back.</summary>
        public static float ROCK_CONTRAST   = 1.15f;
        /// <summary>Saturation restore. The lift desaturates; this puts the stone's own colour
        /// back so it reads as rock rather than as grey card.</summary>
        /// <summary>1.0 = the pack's own colour. ⚠️ HISTORY: this was 1.6 then 0.40, and
        /// ROCK_HUE_ROT was -100, ALL of it compensating for a BUG — the FBX import was not
        /// linking the pack's shared colour ATLAS, so blocks rendered in Blender's fallback
        /// material (the coal came out MAGENTA). With the atlas wired in fbx_tile.py the real
        /// colours are correct and no compensation is needed. Don't reintroduce it.</summary>
        public static float ROCK_SAT        = 1.0f;

        /// <summary>Rotate the rock's hue toward warm stone. MEASURED: the Cube World block is
        /// sage GREEN (hue 132) and boosting its saturation made that worse — Spencer: "a bit of
        /// a green tinge instead of rock coloured". -95 deg lands it near 37, a warm grey-brown.
        /// A ROTATION, not an absolute hue set: setting one hue for every pixel flattens the art
        /// (that is what turned the gold tile into flat yellow).</summary>
        public static float ROCK_HUE_ROT    = 0f;

        /// <summary>WORLD TINT — pulls every blocker's hue toward one anchor so the set reads as
        /// ONE universe. 2026-09-11, Spencer: "is there anyway we can tie everything together to
        /// make it look like its from the same universe?".
        ///
        /// MEASURED, and this is why it exists: VALUE was already unified (every variant lands
        /// L 110-119 via ROCK_VARIANT_LIGHTEN) but HUE was scattered across 167 degrees —
        /// wood 33-40, stone 107-118, steel 196-200. Three families ~90 apart on the wheel is
        /// what makes purchased art look imported. Hue, not brightness, was the tell.
        ///
        /// 0 = the pack's native colours (what Spencer originally chose for the rocks).
        /// 1 = every blocker collapsed onto the anchor, which DESTROYS material identity —
        /// steel stops looking like steel. 0.45 was picked off a side-by-side sheet: steel
        /// leaves blue and joins the family while stone/wood/steel stay tellable apart.
        ///
        /// Anchor 42 deg is the game's own warm axis — `white_glossy@2x` measures hue 35 and the
        /// board frame is gold. Blockers converge toward the letters rather than away.</summary>
        public static float ROCK_WORLD_TINT   = 0.50f;

        /// <summary>How hard a FAR-from-anchor hue is desaturated on its way in. This is the knob
        /// that keeps steel neutral instead of green — at 0 the tint degenerates back into a hue
        /// rotation and the green transit returns. 0.85 leaves a trace of the original.</summary>
        public static float ROCK_WORLD_DESAT  = 0.85f;
        public static float ROCK_WORLD_HUE    = 42f;     // none — see ROCK_SAT's note

        /// <summary>Extra contact-AO for rocks. They read flatter than the letter tiles because
        /// the same AO settings land on a darker, lower-contrast surface — Spencer: "the shading
        /// and ao doesn't really match the board". Multiplies TILE_AO_STRENGTH for rocks only.</summary>
        public static float ROCK_AO_BOOST   = 1.45f;
        /// <summary>Rock VARIANTS. A board of identical rocks reads as tiled wallpaper, so each
        /// cell picks one deterministically from its own coordinates — same cell always gets the
        /// same rock (no flicker on refresh), but neighbours differ. 2026-09-11.</summary>
        private static Sprite[] s_rockVariants;
        private static readonly string[] ROCK_VARIANT_FILES =
        {
            "Tiles/rock_tile@2x",      // block_stone1_1      — one fissure
            "Tiles/rock_tile_b@2x",    // block_stone_large4  — properly fractured
            "Tiles/rock_tile_c@2x",    // block_stone1_8      — recessed square
            "Tiles/rock_tile_d@2x",    // block_stone_large2  — concentric
            "Tiles/rock_tile_e@2x",    // block_stone_large5  — segmented
            // REMOVED 2026-09-11 — coal_ore_block_2. Its dark inclusions read as muddy blobs at
            // tile size, not ore. The PNG is still in Resources if it is ever wanted back.
            // "Tiles/rock_tile_f@2x",
            "Tiles/rock_tile_g@2x",    // tavern_floor_block  — WOOD planks (ROTX 90)
            "Tiles/rock_tile_h@2x",    // wood_deco_top_1     — WOOD board (ROTX 90)
            // Added 2026-09-11 on Spencer's ask ("add the metal_cube designs to the rocks for
            // now"). Riveted metal crates — they only read as metal since the Metallic=0 fix;
            // before it they rendered as near-black slabs. Baked WITH that fix + the Standard
            // view transform, unlike the stone set above, which predates both.
            "Tiles/rock_tile_i@2x",    // metal_cube_1        — riveted panel
            "Tiles/rock_tile_j@2x",    // metal_cube_2        — recessed hatch
            "Tiles/rock_tile_k@2x",    // metal_cube_3        — studded plate
            // 2026-09-11 Spencer: "i want all the steel blocks not just the one you added".
            // metal_cube_4 was missed because the candidate scan capped each family at 3.
            "Tiles/rock_tile_l@2x",    // metal_cube_4        — riveted, 4th of the family
            "Tiles/rock_tile_m@2x",    // metal_block         — plain plate (ROTX 90, gloss to face)
            "Tiles/rock_tile_n@2x",    // metal_bar_block     — stacked bars (ROTX 90)
            // 2026-09-15 — the two wooden forms Spencer asked for.
            "Tiles/rock_tile_o@2x",    // wood_cube_1         — plain wooden cube
            "Tiles/rock_tile_p@2x",    // wooden_box_2        — crate, diagonal brace (ROTX -90)
        };

        /// <summary>PER-VARIANT gamma lift, parallel to ROCK_VARIANT_FILES — MUST stay the same
        /// length and order. 2026-09-11: one global ROCK_LIGHTEN could not work, because the
        /// pack's own materials differ by 2x (re-baked stone lands at raw L 114-120, the metal
        /// cubes at 45-50). Raising a single global enough to rescue the metal blew out the
        /// stone. Each value solves 255*(raw/255)^(1/g) = 112, so every variant arrives at the
        /// same brightness and the board reads as ONE set.
        ///
        /// To add a variant: measure its raw mean L over alpha>200 and add the matching gamma.
        /// A value of 1.0 means the art already sits at target and must NOT be touched — a
        /// multiply-style "lighten" on art that is already correct is what clipped the tiles
        /// before (the TILE_VAL_MUL trap).</summary>
        private static readonly float[] ROCK_VARIANT_LIGHTEN =
        {
            // RE-MEASURED 2026-09-15 for the FRONT-ON (TILT 0) bakes. The previous values were
            // measured on the tilted renders and do NOT transfer — removing the tilt removes the
            // lit top face, so every variant's mean luminance moved.
            1.15f,   // rock_tile@2x    raw L  99
            1.12f,   // rock_tile_b@2x  raw L 102
            1.14f,   // rock_tile_c@2x  raw L 100
            1.19f,   // rock_tile_d@2x  raw L  96
            1.15f,   // rock_tile_e@2x  raw L  99
            1.21f,   // rock_tile_g@2x  raw L  94   tavern planks
            1.54f,   // rock_tile_h@2x  raw L  72   wood board
            2.29f,   // rock_tile_i@2x  raw L  39   metal
            2.19f,   // rock_tile_j@2x  raw L  42   metal
            2.30f,   // rock_tile_k@2x  raw L  38   metal
            2.32f,   // rock_tile_l@2x  raw L  38   metal
            0.66f,   // rock_tile_m@2x  raw L 149   metal_block — DARKENED
            0.89f,   // rock_tile_n@2x  raw L 123   metal_bar_block — DARKENED
            // 1.00 = THE PACK'S OWN COLOUR, untouched. 2026-09-15, Spencer: "i want them
            // coloured the way they are in the pack". The equalising values (1.20 / 1.84) lifted
            // these to the common L 112 target but washed the saturation out of the crate —
            // matching the SET and keeping the MATERIAL are mutually exclusive here.
            1.00f,   // rock_tile_o@2x  raw L  95   wood_cube_1  — pack colour
            1.00f,   // rock_tile_p@2x  raw L  56   wooden_box_2 — pack colour
        };

        /// <summary>OFF 2026-09-15 — the tile art is now FRONT-ON (cutebox TILT 0) and has no
        /// top face. Darkening the top of a flat face just puts a band back on it, which is the
        /// "dusty tops" problem all over again. TILE_CAP_TINTED and its hue/sat knobs are kept
        /// but inert while this is false; both come back together if the tilted art returns.</summary>
        public static bool  TILE_CAP_DARKEN_ON  = false;
        // 0.90 -> 0.85: with gamma down to 1.15 the cap starts lighter (0.846^1.15 = 0.826 vs
        // 0.757 before), so it needs a touch more here to still land on Toon Blast's 0.74 ratio.
        public static float TILE_CAP_DARKEN     = 0.80f;   // cap holds its separation now gamma is linear
        /// <summary>Where the cap darkening fades out, as t from the TOP of the sprite.
        /// MEASURED: the art's cap/face boundary sits at t=0.177 and bows by only 2px across the
        /// whole tile (TOPD=0.05 in cutebox.py — a real but almost invisible dome).
        /// The darkening is keyed to POSITION, not VALUE: a value-threshold mask traced that 2px
        /// bow as a contour and turned it into a visible arc — Spencer: "what is that semicircle
        /// above the line between the face of the tile and the darkened top?"</summary>
        /// <summary>Make the cap a COLOURED shadow instead of a neutral darkening.
        /// 2026-09-15, Spencer: "the tops of the white tiles look more like they are dusty than
        /// it being shadows".
        ///
        /// MEASURED — and the art is NOT at fault. `white_glossy@2x` authors its cap correctly,
        /// as a genuine shadow: cap zone sat **38.8%** / hue 26 against the face's 25.0% / hue 39.
        /// The runtime then flattens it. `TILE_SAT_MUL = 0.08` ("effectively white") takes the
        /// cap to ~3% saturation, and `TILE_CAP_DARKEN`'s neutral `c.rgb *= k` only scales VALUE
        /// — so what survives is a pale NEUTRAL GREY band over white. Neutral grey on white reads
        /// as a film of DUST sitting on the surface, not as light being blocked.
        ///
        /// A shadow keeps colour commitment: it darkens AND shifts hue toward the ambient while
        /// holding saturation. That is the same principle `TILE_AO_TINTED` already applies to the
        /// contact AO ("a shadowed version of the surface rather than a grey wash over it") — the
        /// cap simply never got it. Matches reference_icon_stroke_shadow_recipe (hue toward
        /// violet, saturation UP) and reference_stylized_render_settings ("saturated shadows").
        ///
        /// Set TILE_CAP_TINTED=false for the old neutral multiply.</summary>
        public static bool  TILE_CAP_TINTED     = true;
        /// <summary>-18 is the documented shadow recipe (reference_icon_stroke_shadow_recipe:
        /// hue toward violet, saturation up). 2026-09-15: I first set -60 and it turned the caps
        /// PINK in game — the cap sits near hue 44, so -60 lands on ~344 = magenta. Verified
        /// against a mock that now reproduces the in-game pink, so these numbers are trustworthy.
        /// -18 keeps the cap in the tile's own WARM family; **+156 instead** shifts it to the
        /// board's teal (the ambient) for a cool shadow — also correct, different look.</summary>
        public static float TILE_CAP_HUE_SHIFT  = -18f;
        public static float TILE_CAP_SAT_ADD    = 0.12f;   // absolute, added at full cap strength

        public static float TILE_CAP_FADE_START = 0.13f;   // full strength above this
        public static float TILE_CAP_FADE_END   = 0.25f;   // nothing below this

        /// <summary>Which Resources texture is the resting tile face. Swap tiles by changing
        /// THIS ONE STRING. Candidates present: "Tiles/white_tile@2x", "Tiles/peach_tile@2x",
        /// "Tiles/white_glossy@2x" (the original Blender render). 2026-09-21.</summary>
        public static string RESTING_FACE_ASSET = "Tiles/white_tile_pillow@2x";   // 2026-10-28 PILLOW FAMILY (Spencer: "yes the bottom ones look great"). Hand-painted original: "Tiles/white_tile_2@2x"

        /// <summary>Hand-painted GOLD face (the high-value letter tile — `s_spriteHighValue`,
        /// labelled "gold-resting" at line 175). Empty string falls back to the old
        /// GOLD_HUE_SHIFT / GOLD_SAT_MUL recolour of the white tile. 2026-09-21.
        /// NOTE the gold BONUS cell (`golden_tile2@2x`) and the gold PRIMED face
        /// (`primed_gold_tile@2x`) are SEPARATE sprites and are not affected by this.</summary>
        public static string GOLD_FACE_ASSET = "Tiles/gold_tile_pillow@2x";   // 2026-10-28 pillow family; original "Tiles/gold_tile2@2x"   // 2026-10-22: back to the gold tile — the yellow gem now marks GEM tiles (collect goal)

        /// <summary>The PRIMED / armed face — the tile a word turns into once it is armed.
        /// "Tiles/primed_tile@2x" is the original magenta. 2026-09-21 trying green_primed@2x.
        /// NOTE the gold-primed variant (`primed_gold_tile@2x`) is a SEPARATE sprite, chosen
        /// per-tile by `_isGoldPrimed`, and is not affected by this.</summary>
        public static string PRIMED_FACE_ASSET = "Tiles/green_primed_pillow@2x";   // 2026-10-28 pillow family; original "Tiles/green_primed@2x"

        /// <summary>When the primed face is AUTHORED art, make its glow/pulse a NEUTRAL luminance
        /// lift instead of the magenta PRIMED_GLOW tint. Set false to restore the old behaviour
        /// (correct only when the primed sprite is itself magenta). 2026-09-21.</summary>
        public static bool PRIMED_TINT_NEUTRAL = true;

        /// <summary>The GOLD-primed face — a gold word that is armed. 2026-09-21 Spencer: "when
        /// the gold tiles prime a word gold cant it just do a sprite swap to the golden tiles
        /// like how priming regular tiles swaps to the green tiles". Pointed at the authored gold
        /// art so it is a straight SWAP, not the old render under an amber tint.
        /// Empty string falls back to "Tiles/primed_gold_tile@2x".</summary>
        public static string GOLD_PRIMED_FACE_ASSET = "Tiles/gem_yellow";   // 2026-10-22 Spencer: a word made WITH a gem turns every tile of it into a gem while it's armed — the old gold-word armed look, now gem art

        /// <summary>Neutralise the gold-primed tint too, so the authored gold art LIGHTS UP
        /// rather than being multiplied by PRIMED_GOLD_GLOW (2.0, 1.54, 0.64) — which on gold
        /// art pushes it orange.</summary>
        public static bool GOLD_PRIMED_TINT_NEUTRAL = true;

        /// <summary>PAYOUT TIER COLOURS for armed clusters. A cluster worth >= PRIMED_TIER_CUTS[i]
        /// wears PRIMED_TIER_ASSETS[i+1]. 2026-09-21 Spencer: show that a big web is worth more.
        ///
        /// Cuts are LOGARITHMIC, not linear — measured payouts are long-tailed (36/46/58/94/184/
        /// 1036 in one session, median 76). Linear cuts would leave the top tier almost unused.
        /// 100 is the first total a SINGLE word cannot reach, so the change means "you connected
        /// something" rather than just "big number". 400 is ~p85. Empty asset = no tier face.</summary>
        /// <summary>When a cluster crosses into a higher tier, EVERY tile in it glows — not just
        /// the word that was played. 2026-09-21 Spencer: "I want the whole cluster to glow when it
        /// changes". The tier belongs to the cluster, so the feedback should be cluster-wide.
        /// Uses the ADDITIVE bloom overlay, not sr.color, so it cannot fight the primed pulse or
        /// re-tint authored art — pure added light.</summary>
        public static bool  TIER_UP_FLASH       = false;   // 2026-09-21: Spencer asked to revert — set true to bring the cluster-wide glow back
        public static float TIER_UP_FLASH_DUR   = 0.42f;
        public static float TIER_UP_FLASH_PEAK  = 0.85f;   // overlay alpha at the peak

        public static int[]    PRIMED_TIER_CUTS   = { 100, 400 };
        public static string[] PRIMED_TIER_ASSETS = { "", "Tiles/primed_tier1_pillow@2x", "Tiles/primed_tier2_pillow@2x" };   // 2026-10-28 pillow family; originals "Tiles/primed_tier1@2x" / "Tiles/primed_tier2@2x"
        private static Sprite[] s_spritePrimedTier;
        private int _primedTier;

        public static int TierForPayout(int payout)
        {
            int t = 0;
            if (PRIMED_TIER_CUTS != null)
                for (int i = 0; i < PRIMED_TIER_CUTS.Length; i++)
                    if (payout >= PRIMED_TIER_CUTS[i]) t = i + 1;
            return t;
        }

        /// <summary>Tell this tile which payout tier its cluster is in. Repaints if armed.</summary>
        public void SetPrimedTier(int tier)
        {
            // 🚨 DO NOT early-out when the tier is unchanged. 2026-09-21: it did, and that made
            // the tier face UNRECOVERABLE — once any other writer repainted the armed face, the
            // tier was already correct so this returned before repainting and the tile stayed
            // green. Spencer hit it by extending a cluster during a row rise. The cheap guard is
            // the sprite comparison below, which no-ops when the face is already right.
            int prev = _primedTier;
            bool seenBefore = _tierSeen;
            _primedTier = tier;
            _tierSeen = true;
            if (_hasPrimedGlow && _spriteRenderer != null && !_isShowingScoredSprite)
            {
                var f = ArmedFace();
                if (f != null && _spriteRenderer.sprite != f) FaceSprite = f;
                // Only on an UPGRADE, and only if this tile was already sitting at a tier —
                // otherwise a tile arming for the first time would double up with its own
                // scored flash.
                if (TIER_UP_FLASH && seenBefore && tier > prev && isActiveAndEnabled)
                {
                    if (_tierFlashCo != null) StopCoroutine(_tierFlashCo);
                    _tierFlashCo = StartCoroutine(TierUpFlashRoutine());
                }
            }
        }

        private bool _tierSeen;
        private Coroutine _tierFlashCo;

        /// <summary>Additive glow swell. Deliberately NOT sr.color: that property already has
        /// several writers (primed pulse, danger pulse, scored flash) and adding another caused
        /// exactly the kind of fight that produced today's face bugs. The overlay is its own
        /// renderer, so it layers instead of competing.</summary>
        private System.Collections.IEnumerator TierUpFlashRoutine()
        {
            float t = 0f;
            Color glow = Color.white;   // neutral: adds light, never recolours the authored art
            while (t < TIER_UP_FLASH_DUR)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / TIER_UP_FLASH_DUR);
                float env = (k < 0.25f) ? (k / 0.25f) : (1f - (k - 0.25f) / 0.75f);
                SetBloomGlow(glow, TIER_UP_FLASH_PEAK * env, forceDesktop: true);
                yield return null;
            }
            ClearBloomGlow();
            _tierFlashCo = null;
        }

        /// <summary>🔑 THE ONE PLACE that decides which ARMED face this tile wears.
        /// Three bugs came from ~50 scattered writers to _spriteRenderer.sprite, some consulting
        /// _isGoldPrimed and some not. Precedence lives here: GOLD beats tier, tier beats plain.</summary>
        private Sprite ArmedFace()
        {
            if (_isGoldPrimed && s_spritePrimedGold != null) return s_spritePrimedGold;
            if (s_spritePrimedTier != null && _primedTier > 0
                && _primedTier < s_spritePrimedTier.Length
                && s_spritePrimedTier[_primedTier] != null)
                return s_spritePrimedTier[_primedTier];
            return s_spriteGold ?? s_spriteNormal;
        }

        /// <summary>TEMP 2026-09-21 — logs every armed-face swap so the gold-word-flashes-green
        /// path can be identified rather than guessed at. Strip before build.</summary>
        public static bool ARMED_FACE_DIAG = false;  // diagnostic, off


        public static bool  WARM_TILES     = false;   // OFF 2026-09-21 — the resting face is now
        // Spencer's HAND-PAINTED peach_tile, and WarmedCopy recolours it: TILE_HUE_SHIFT rotates
        // the hue and TILE_SAT_MUL 0.48 halves the saturation (his 38.6% -> 18.5%), which is why
        // it rendered as pale cream instead of peach. Authored art must reach the screen as drawn.
        // Turn back on ONLY if the face returns to a neutral render that needs tinting.
        // 2026-09-10: retargeted at the SCRABBLE GO tile Spencer sent. MEASURED there
        // #F8DC58 = hue 50, HLS sat 92, L 85. WordDrop's face is #F4E0B8 = hue 40, HLS sat 73,
        // L 88. So: +10 deg of hue and a big saturation lift, brightness ~unchanged.
        // NOTE the multiplier is in HSV (what Color.RGBToHSV returns), NOT the HLS numbers
        // above — that mismatch is why the earlier -21/x1.45 guess missed. In HSV the face is
        // 0.246 and the target 0.645, so the multiplier is 2.62.
        // 2026-09-10: retargeted at the cream Spencer picked (Screenshot 6.59.52 PM).
        // MEASURED reference #F6F3E4 = hue 50, HSV sat 0.073, V 0.965, L 95.
        // white_glossy@2x is    #F0E0B8 = hue 43, HSV sat 0.233, V 0.941, L 88.
        // So: +10 deg of hue, and saturation pulled DOWN to ~0.30x — a paler, warmer cream.
        // Same hue as the gold face (they share a family); only the saturation differs.
        /// <summary>10 -> 2. The source face is hue 40.3 and the reference is 41.4, so a small
        /// nudge lands on it; +10 pushed us to 50 (more yellow). 2026-09-15.</summary>
        public static float TILE_HUE_SHIFT = 2f;
        // 0.30 hit the reference swatch EXACTLY (HSV 0.233 -> 0.070) and still read "too white"
        // in game — a big flat swatch and a small bevelled tile do not look the same at the same
        // value, because the gloss highlight and the dark AO rim push the midtone toward white.
        // Matching the number is not matching the look. Halfway back: HSV 0.233 -> ~0.135.
        // 2026-09-11: retargeted at the MONOPOLY GO DICE Spencer picked. MEASURED off it:
        //   lit face        #FDFDFD  L 99  sat 0.00   <- pure WHITE, no hue at all
        //   mid shadow      #C0C3BE  L 76  sat 0.03
        //   deep shadow     #8E908F  L 57  sat 0.01
        //   dots            #0A0A0A  L  4  sat 0.00
        // So the die is fully NEUTRAL. Face saturation pulled right down to match.
        /// <summary>0.08 -> 0.48. 2026-09-15: MEASURED off the reference board Spencer sent —
        /// its tile face is `#FCF3DF`, hue 41.4, **saturation 11.5%**. Ours was being knocked from
        /// the art's 24.1% down to ~1.9% ("effectively white"), which rendered grey rather than
        /// cream. 11.5 / 24.1 = 0.48. Only affects the RESTING face (WarmedCopy has one call
        /// site); gold uses GOLD_SAT_MUL and is untouched.</summary>
        public static float TILE_SAT_MUL   = 0.48f;
        /// <summary>Brightness lift on the face. The source art tops out at V 0.941; the dice
        /// face is 0.99, so a small lift is needed to actually reach white.</summary>
        // 2026-09-11: was 1.05 and it was CLIPPING — MEASURED, 62.3% of the tile's opaque pixels
        // sat above V 0.952 and so crushed to pure white. That flattened the top cap against the
        // face and killed the 3D read (Spencer: "the top end isn't really visible with the depth
        // cause it's so light"). Brightness lift is the wrong tool on art that is already near
        // white. 1.0 = no lift, nothing clipped.
        public static float TILE_VAL_MUL   = 1.0f;

        /// <summary>Shading CONTRAST on the face, as a power curve on V (v^gamma). >1 deepens the
        /// shaded areas while leaving white at white, so it adds depth without clipping — which a
        /// multiply cannot do. MEASURED source art: top cap V 0.846 vs face V 0.952, a gap of
        /// only 0.106. At gamma 1.6 that becomes 0.757 vs 0.925 — a gap of 0.168, so the cap
        /// actually reads as a separate plane. 1.0 = off.</summary>
        // 2026-09-11: 1.6 -> 1.15. Gamma darkens EVERYTHING, and it was set high purely to get
        // the cap to separate from the face. TILE_CAP_DARKEN now does that job precisely, so the
        // blunt instrument can relax and the FACE comes back up — which is what should draw the
        // eye, since it carries the letter.
        public static float TILE_SHADE_GAMMA = 1.0f;    // linear — face sits at the source art's own brightness

        public static Color ARMED_GOLD_REST = new Color(1.00f, 0.831f, 0.501f, 1f);

        /// <summary>Resting tint for a ROSE-armed tile — the gold treatment carried over, but
        /// SOFTENED. The literal transfer is PRIMED_GLOW scaled into LDR = (1.00, 0.278, 0.578),
        /// and rose's green channel is half gold's, so that lands on rgb(252,35,104) over the
        /// sprite — a deep crimson, DARKER than the tan-rose it already was. Spencer's complaint
        /// was that primed rose already reads dark, so the full multiply makes it worse.
        /// TRIED AND REVERTED (2026-09-10): pulled 55% from white, i.e. (1.00, 0.603, 0.768).
        /// Came out hot pink with the bevel washed out. WHY, so nobody retries it: a multiply
        /// scales the sprite's light AND dark pixels by the same factor, so the gap between them
        /// shrinks in whichever channels are pulled down — and the rose bevel lives in green and
        /// blue, exactly where rose's glow is lowest (0.50). Saturation climbs while relief
        /// flattens. Gold survives the same treatment only because its green stays high (1.54).
        /// White leaves the authored sprite alone, which is what matches Spencer's render.</summary>
        public static Color ARMED_ROSE_REST = Color.white;   // REVERTED 2026-09-10 — see below

        public static readonly Color PRIMED_GOLD_GLOW  = new Color(2.0f, 1.54f, 0.64f, 1f);  // HDR gold — matches the gold tile
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
        /// <summary>2026-09-03: the tile is FORCED to this width:height on screen so
        /// the vertical stacking overlap is deterministic. Width == one cell; height ==
        /// cell / TILE_TARGET_ASPECT (= 1.2 cells). The extra 20% is the shaded top
        /// face, which the tile ABOVE draws over — that is the whole 3D-stack effect.
        /// Forced non-uniformly because several paths swap the sprite after Initialise
        /// and a square sprite would silently kill the overlap.</summary>
        public  const float TILE_TARGET_ASPECT = 0.833f;

        /// <summary>Scales the letter's downward face-nudge. 0 = dead centre (front-on art),
        /// 1 = the old tilted-art offset. 2026-09-15: front-on tiles have no cap, so 0.</summary>
        public static float LETTER_FACE_NUDGE_MUL = 0f;

        /// <summary>TRUE once the tile art is rendered FRONT-ON (cutebox/fbx_tile TILT 0), so the
        /// sprite is the same height as its cell and there is NO exposed top cap. Anything that
        /// compensated for the old ~20% overhang must read this instead of TILE_TARGET_ASPECT —
        /// see ClusterOutline, where the outline used to rise over any tile with nothing above it
        /// to enclose its cap, which now reads as a snaking/battlement edge. 2026-09-15.</summary>
        public static bool TILE_ART_FRONT_ON = true;

        /// <summary>Tile width/height of the art ACTUALLY in use, in TILE_TARGET_ASPECT's units.
        /// Front-on it defers to GridManager.TILE_ASPECT (1.0418 = 249/239) so the grid and every
        /// overlay share ONE number; otherwise the legacy tilted 0.833. 2026-09-15: was a
        /// placeholder 1f, which was ~4% out and would have made overlays slightly too tall.</summary>
        public static float ArtAspect => TILE_ART_FRONT_ON ? GridManager.TILE_ASPECT : TILE_TARGET_ASPECT;
        /// <summary>2026-09-03: cascading tiles land with the DROP squish (chunkier)
        /// rather than the quieter gravity one. False restores the old feel.</summary>
        public  const bool  GRAVITY_USES_LANDING_SQUISH = true;
        /// <summary>Royal-Match-measured fall (cells/s, cells/s²). false = the old distance-scaled ease-in.</summary>
        public static bool  RM_FALL       = true;
        public static float RM_FALL_V0    = 12f;
        public static float RM_FALL_ACCEL = 18f;
        public static float RM_FALL_VMAX  = 20f;
        /// <summary>MASTER SQUASH SWITCH (2026-10-22 Spencer: "is it possible to remove the distortion of the tiles ... i would
        /// like to see what it looks like without this effect"). false = no fall stretch, no landing squish, no gravity
        /// squish — tiles keep their exact shape and simply land. true = the old squash-and-stretch feel.</summary>
        public static bool TILE_SQUASH_STRETCH = false;
        public  const bool  TILE_DROP_SHADOW   = false; // 2026-09-03: per-tile drop shadow off for the stacked/overlap board
        // 2026-10-21 Spencer: "more padding in between letters, it just looks too cluttered" — Royal Match measured: 84 px
        // pitch, ~73 px icons = 87% of the cell. Now a tunable (FX Test Menu cycles it; remembered in PlayerPrefs). The
        // cell pitch does NOT change — only the tile inside it shrinks, so the board stays the same size.
        public static readonly float[] TILE_RATIO_STEPS = { 1.0f, 0.92f, 0.91f, 0.88f, 0.84f };
        public static float TILE_DISPLAY_RATIO_DEFAULT = 0.91f;   // 2026-10-28 Spencer: "scaled up just a hair" (.88 -> .91, +3.4%)
        private static float s_tileRatio = -1f;
        public static float TILE_DISPLAY_RATIO
        {
            get { if (s_tileRatio < 0f) s_tileRatio = PlayerPrefs.GetFloat("WD_TileRatio2", TILE_DISPLAY_RATIO_DEFAULT)   /* key bumped 10-28 so the new default wins over a saved .88 */; return s_tileRatio; }
            set { s_tileRatio = value; PlayerPrefs.SetFloat("WD_TileRatio2", value); PlayerPrefs.Save(); }
        }
        /// <summary>Re-apply the rest scale after TILE_DISPLAY_RATIO changes (FX Test Menu).</summary>
        public void RefitRestScale() { if (_spriteRenderer != null && _spriteRenderer.sprite != null) RefitToSprite(_spriteRenderer.sprite); }
        /// <summary>Tile display size as a fraction of the cell pitch — so other systems (e.g. the
        /// drop-preview ghost) can size their tiles to MATCH the board tiles. 2026-06-16 Spencer.</summary>
        public static float DisplayRatio => TILE_DISPLAY_RATIO;

        // ---------------------------------------------------------------------------
        // Runtime state
        // ---------------------------------------------------------------------------

        public char  Letter      { get; private set; }
        public int   Col         { get; private set; }
        public int   Row         { get; private set; }
        /// <summary>True while this tile's position is owned by an animation. Everything that
        /// snaps tiles to their cells (BoardInvariants' MISPLACED repair, GridManager's sync
        /// drift fix) must respect it, or it teleports a tile that is legitimately in flight.
        ///
        /// Two channels, because the flag has two kinds of owner:
        ///  • the FLAG — set/cleared by Tile's own animation methods, which always finish,
        ///  • the DEADLINE — for callers that drive this tile's transform from OUTSIDE Tile
        ///    (GridManager.AnimateRiseRow tweens transform.DOMove directly). A deadline rather
        ///    than an OnComplete on purpose: those tweens are routinely DOKill'd by gravity, by
        ///    SnapAllTilesToCells and by a level change, and a flag cleared in OnComplete would
        ///    be stranded TRUE forever — blinding every integrity check on this tile for the
        ///    rest of the run. A deadline cannot be stranded. (Same rule as the stage-clear
        ///    pause: never hang state restoration on a callback that can be cancelled.)
        /// 2026-10-20.</summary>
        public bool IsAnimating
        {
            get => _isAnimatingFlag || Time.unscaledTime < _animatingUntil;
            private set => _isAnimatingFlag = value;
        }
        private bool  _isAnimatingFlag;
        private float _animatingUntil;

        /// <summary>Claim this tile's position for <paramref name="seconds"/> on behalf of an
        /// externally-driven tween. Extends an existing claim, never shortens one.</summary>
        public void MarkAnimating(float seconds)
            => _animatingUntil = Mathf.Max(_animatingUntil, Time.unscaledTime + seconds);
        /// <summary>Drop an external MarkAnimating claim early (the effect that claimed it ended). 2026-10-23: the hint's 4.5s claim
        /// outlived the hint and held every gravity wait it fell into — the "pop, long delay, then the next word" stall.</summary>
        public void ReleaseAnimatingClaim() => _animatingUntil = 0f;
        /// <summary>A gravity / fall coroutine is moving this tile (what a gravity wait should wait for — not FX claims).</summary>
        public bool IsFalling => _gravityCoroutine != null || _fallCoroutine != null;

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
        // 2026-09-09 Spencer: "the glow is blowing out all definition on the tile."
        // This is a MULTIPLY on the face, and g/b above 1.0 push those channels past white
        // across most of an already-light cyan tile — everything clips to the same value and
        // the shading disappears. Pulled to ~1.0 so the face keeps its form and the cyan
        // comes from the hue, not from over-driving the channels.
        // ── SELECTION = A RIM, NOT A WASH (2026-09-09 Spencer) ──────────────────────
        // The glow was blowing out the tile: a >1.0 face multiply clipped the shading flat,
        // and a full-strength additive overlay on top compressed what was left. A rim solves
        // it structurally — the tile keeps its own colour, shading and letter, and nothing
        // can wash out because nothing is painted over the face.
        // NOT a solid cyan FACE: cyan is already the frozen tile, and a solid face colour
        // reads as a tile TYPE rather than a momentary state.
        // Set false to go back to the glow.
        public const  bool  EDIT_SELECT_RIM = true;
        /// <summary>Selected tile LIFTS (scale up + gentle breathe) instead of drawing a rim/glow. 2026-10-20.</summary>
        public static bool  SELECT_LIFT       = true;
        public static float SELECT_LIFT_SCALE = 1.08f;   // tap-select (one tile); was 1.12 — neighbours collided
        public static float SWIPE_LIFT_SCALE  = 1.04f;   // each letter in a swipe trace (neighbours, so smaller)
        /// <summary>Face tint while a board swap is ANIMATING. Every channel stays at or below
        /// 1.0, so this MULTIPLIES against the tile sprite and keeps the bevel and contour — the
        /// exact detail the old HDR glow (EDIT_GLINT_HIGH + bloom) was washing out. Solid cyan,
        /// not a glow. 2026-09-10 Spencer.</summary>
        public static readonly Color EDIT_SWAP_FACE = new Color(0.42f, 0.88f, 1.00f, 1f);

        public static readonly Color EDIT_RIM_CYAN = new Color(0.24f, 0.92f, 1.00f, 1f);

        private static readonly Color EDIT_GLINT_HIGH = new Color(0.30f, 1.00f, 1.10f, 1f); // peak — cyan without clipping the face
        private static readonly Color EDIT_GLINT_LOW  = new Color(0.42f, 0.98f, 1.18f, 1f); // breathing trough — still cyan-lit
        private const float EDIT_ACCESS_DUR = 0.13f; // fast snap-to-peak on click
        // 2026-07-09 Spencer: the sr.color glint above is CLAMPED on mobile (linear) so it
        // can't actually bloom — the selected tile read as a flat cyan. Layer a REAL pulsing
        // HDR cyan bloom through the additive _Color overlay (the coin-trail/primed path that
        // DOES bloom on device) so the swap/edit selection obviously breathes a glow. Forced on
        // desktop too so it's unmistakable wherever it's viewed. Well above the 1.30 threshold.
        private static readonly Color EDIT_GLOW_HDR = new Color(0.28f, 1.18f, 1.42f, 1f); // peak bloom — brought down again; sits right around the 1.30 bloom threshold for a soft glow, full-off trough carries the pulse
        private const float EDIT_GLOW_ALPHA_LOW  = 0.0f;  // trough — glow fades fully OFF so it blinks on↔off (not just dims)
        // The bigger culprit: an ADDITIVE overlay at full strength adds a constant amount of
        // light everywhere, which compresses the difference between the tile's lit and shaded
        // areas — that is definition loss by definition. Halved so it reads as a glow sitting
        // ON the tile rather than replacing it. 2026-09-09 Spencer.
        private const float EDIT_GLOW_ALPHA_HIGH = 0.52f; // peak — glow, not a wash
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
            WDLog.Log($"[BoardShadow] FLIP → active={(s_useBoardShadowB ? "B=" + BoardShadowTexB : "A=" + BoardShadowTexA)} (updated {n} tiles)");
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
            if (LetterData.GetPoints(letter) >= HIGH_VALUE_POINTS)
                WDLog.Log($"[GoldSpawn] Initialise '{letter}' at ({col},{row}) -> sprite={(_spriteRenderer!=null&&_spriteRenderer.sprite!=null?_spriteRenderer.sprite.name:"null")} (want highValue) cacheBuilt={s_spriteCacheBuilt} hv={(s_spriteHighValue!=null?s_spriteHighValue.name:"NULL")}");
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
                FaceSprite = (ownerIndex == 1 && s_spriteAI != null) ? s_spriteAI : RestingFaceFor(letter);
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

            // 2026-09-03: sorting is per-ROW (ROW_ORDER_STRIDE). Tiles come from a POOL,
            // and Reinitialise set Row WITHOUT re-applying the order — so a recycled tile
            // kept its previous row's band and stopped being obscured by the tile above.
            SetSortingOrder(BaseSortingOrder);

            UpdateLetterDisplay(letter);

            // 2026-09-10 Spencer: SET THE RESTING FACE. Reinitialise is the POOL REUSE path —
            // how the board actually populates — and it never assigned a sprite at all, so a
            // recycled tile kept whatever face its previous letter had. A gold letter therefore
            // arrived tan and stayed tan until some later board sync ran RefreshRestingFace,
            // which corrected the WHOLE board at once: "no gold tiles, then they all turn gold
            // the moment I make a word." Initialise did this on its own; the recycled path is
            // the one the game actually runs. Same class of bug as the sorting order note above.
            if (_spriteRenderer != null && !IsWild && !IsStone && !IsFrozen)
                FaceSprite = (ownerIndex == 1 && s_spriteAI != null)
                                       ? s_spriteAI : RestingFaceFor(letter);
            if (_letterTMP != null) { _letterTMP.gameObject.SetActive(true); _letterTMP.enabled = true; }
            if (_pointTMP != null) { _pointTMP.gameObject.SetActive(true); _pointTMP.enabled = true; }
        }

        public void ResetForPool()
        {
            _bonusLetterHidden = false;
            TreasureHits = 0;                       // a recycled tile must not stay a treasure
            SetSpecial(RulesEngine.SPECIAL_NONE);   // a recycled tile must not keep a special marker
            IsRainGold = false;                     // nor a shower-gold flag
            IsGem = false;                          // nor a gem
            // nor the refill CLIP (2026-10-22 blank tiles): a tile blown up mid-fall came back with its sprites masked and its
            // letter's stencil set, so on reuse the letter drew only inside the mask — or not at all.
            foreach (var csr in GetComponentsInChildren<SpriteRenderer>(true)) csr.maskInteraction = SpriteMaskInteraction.None;
            if (_letterTMP != null)
            {
                var lm = _letterTMP.fontMaterial;
                if (lm != null && lm.HasProperty("_StencilComp")) { lm.SetFloat("_Stencil", 0f); lm.SetFloat("_StencilComp", (float)UnityEngine.Rendering.CompareFunction.Always); }
                _letterTMP.alpha = 1f;
            }
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
            _primedTier = 0; _tierSeen = false;   // pooled reuse: no stale tier, no spurious flash
            if (_isGoldBonus) SetGoldBonus(false);
            if (_isVault) SetVaultVisual(false);      // restores chest → normal sprite + cell-fit
            else if (_isDropTargetVisual) { SetDropTargetVisual(false); _isStone = false; } // clear escort-object look
            else if (_isStone) SetStoneVisual(false);
            _isDropTargetVisual = false; // clear escort-object flag on pool reuse
            _isAnchored = false; // clear Break-Rocks anchor on pool reuse
            _vaultRequiredLen = 0; // clear chest-tier tint on pool reuse
            // ICE: clear frost overlay on pool reuse. Drop the defrost claim FIRST — a tile
            // being recycled has no business still animating a melt, and while that claim stood
            // SetFrozenVisual(false) returned early and handed the next user of this tile a
            // stale _isFrozen with a half-melted overlay. That was the "ice disappears on a row
            // rise" bug's way into the pool. 2026-10-20.
            _defrostingUntil = 0f;
            if (_defrostRoutine != null) { StopCoroutine(_defrostRoutine); _defrostRoutine = null; }
            if (_isFrozen) SetFrozenVisual(false);
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
            _staticPrimedApplied = false; // recycled tile must repaint, not trust a previous life's paint
            _letterWhitened = false;      // ditto for the armed letter colour
            if (_selectRimSR != null) _selectRimSR.enabled = false;
            // Reset localPosition in case MeltdownShakeCoroutine was interrupted
            // mid-shake and never restored its baseLocalPos at line 1190.
            WDLog.Log($"[Tile] ResetForPool localPos: {transform.localPosition} → Vector3.zero");
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
                _letterTMP.color = LETTER_INK;
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
            Generation++;   // anything still holding this tile from before the recycle can tell it's a different tile now
            gameObject.SetActive(false);
        }

        /// <summary>Bumped every time the tile goes back to the pool. A delayed FX step (a pop that hides the tile ~0.3s
        /// later) captures it and does nothing if it changed — the tile was recycled into a NEW letter meanwhile.
        /// 2026-10-28: a late pop hid five freshly recycled refill tiles, so they never fell and gravity sat on its
        /// 600-frame safety (~10s pause, empty cells at the top).</summary>
        public int Generation { get; private set; }

        /// <summary>Tracks whether BuildVisuals has already run on this instance, so it can
        /// never stack a second set of child objects. 2026-10-19.</summary>
        private bool _visualsBuilt;

        private void BuildVisuals(float cellSize)
        {
            // ── NEVER BUILD TWICE ────────────────────────────────────────────────────
            // 2026-10-19 Spencer: "sometimes when i enter the game the board populates in a
            // weird glitchy way" — a tile rendered with SEVERAL letters composited on top of
            // each other. Cause: this method creates five child objects (TileLetter,
            // TilePoints, TileShadow, TileGloss, TileInnerShadow) with bare `new GameObject`
            // and no guard. Tiles are POOLED, and while the pool path checks _poolInitialized
            // before calling Initialise, Initialise itself is public and unguarded — so a
            // second Initialise on a live tile ran BuildVisuals again and stacked a whole new
            // set of children over the old ones. The old TextMeshPro kept rendering its old
            // glyph underneath the new one.
            //
            // Destroying the stale children rather than early-returning, because a rebuild may
            // legitimately be needed at a different cellSize.
            if (_visualsBuilt)
            {
                for (int i = transform.childCount - 1; i >= 0; i--)
                {
                    var ch = transform.GetChild(i);
                    switch (ch.name)
                    {
                        case "TileLetter":
                        case "TilePoints":
                        case "TileShadow":
                        case "TileGloss":
                        case "TileInnerShadow":
                            Destroy(ch.gameObject);
                            break;
                    }
                }
                _letterTMP = null; _glossGO = null; _innerShadowGO = null;
                WDLog.Log($"[Tile] BuildVisuals re-run on '{Letter}' at ({Col},{Row}) — stale visuals cleared");
            }
            _visualsBuilt = true;
            int texSize = Mathf.Clamp(Mathf.RoundToInt(cellSize * 200f), 64, 512);
            int radius  = texSize / 6;   // chunkier rounded corners
            int border  = Mathf.Max(3, texSize / 12);  // thicker edge for tactile depth

            _currentBorderColor = TILE_BORDER_NORMAL;

            _spriteRenderer              = gameObject.AddComponent<SpriteRenderer>();
            _spriteRenderer.sprite       = (OwnerIndex == 1 && s_spriteAI != null) ? s_spriteAI : RestingFaceFor(Letter);
            _spriteRenderer.sortingOrder = BaseSortingOrder;

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
            if (!s_loggedTileMetrics)
            {
                s_loggedTileMetrics = true;
                var sp = _spriteRenderer.sprite;
                WDLog.Log($"[TileMetrics] cell={cellSize:F4} ratio={TILE_DISPLAY_RATIO:F3} " +
                          $"spriteBounds={(sp!=null?sp.bounds.size.x:0):F4}x{(sp!=null?sp.bounds.size.y:0):F4} " +
                          $"scale={scale:F4} -> onScreen {(sp!=null?sp.bounds.size.x*scale:0):F4}x{(sp!=null?sp.bounds.size.y*scale:0):F4} " +
                          $"(want width==cell, height==1.2*cell)  spriteName={(sp!=null?sp.name:"NULL")}");
            }

            // Letters/points shrink WITH the tile (2026-10-21 Spencer: "the font needs to be just as much smaller too").
            // Was 1/scale, which pinned the letter to a fixed world size whatever the tile size. Measured against the
            // 100% tile, so a ratio change (and the live FX-menu refit) scales the letter by the same amount.
            float invScale = TILE_DISPLAY_RATIO / Mathf.Max(scale, 0.01f);

            // ── Main letter — TextMeshPro with subtle grounding shadow ──
            GameObject letterGO = new GameObject("TileLetter");
            letterGO.transform.SetParent(transform, false);
            // Centered on tile face (true center, no offset).
            // 2026-09-03: the tile sprite is 1/TILE_TARGET_ASPECT cells TALL, but only the
            // bottom ~83% of it is the visible FACE (the top is the shaded face that the
            // tile above covers). A letter centred on the whole sprite therefore sits high
            // in the block, leaving dead space under it that reads as "stretched".
            // Push it down by half the overshoot so it centres on the FACE.
            // localPosition is in the tile's LOCAL space, which is scaled by `scale`,
            // so the world offset (overshoot/2 * cellSize) divides back out to
            // (1/aspect - 1)/2 * nativeSize.
            // FRONT-ON TILES: nudge OFF. 2026-09-15 — the tile art is now rendered at TILT 0
            // (cutebox.py), so there is no top cap pushing the visible face down and the glyph
            // belongs dead centre. This nudge existed ONLY to compensate for the 20-degree tilt:
            // it shifts the letter DOWN by 10% of the tile width. Set LETTER_FACE_NUDGE_MUL = 1
            // to restore it if the tilted art ever comes back.
            float faceNudge = ((1f / TILE_TARGET_ASPECT) - 1f) * 0.5f * nativeSize * LETTER_FACE_NUDGE_MUL;
            letterGO.transform.localPosition = new Vector3(0f, -faceNudge, -0.1f); // 2026-06-04 Spencer: nudge to re-center Avenir

            _letterTMP = letterGO.AddComponent<TextMeshPro>();
            // Load Fredoka Bold TMP font
            TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) _letterTMP.font = tileFont;
            _letterTMP.text           = "";
            _letterTMP.fontSize       = 6.84f * LetterSizeFactor; // 2026-06-05 Spencer: −10% (was 7.6); 2026-10-23 scales with the board (see LetterSizeFactor)
            _letterTMP.fontStyle      = TILE_FONT_FAUX_BOLD ? FontStyles.Bold : FontStyles.Normal;
            _letterTMP.color          = LETTER_INK; // #252733
            _letterTMP.alignment      = TextAlignmentOptions.Midline; // Midline (not Center) so a single capital sits visually centered, not high
            _letterTMP.sortingOrder   = BaseSortingOrder + 2; // above the gloss sheen (+1) so the letter stays crisp
            _letterTMP.rectTransform.sizeDelta = new Vector2(2f, 2f);
            _letterTMP.enableWordWrapping = false;
            _letterTMP.overflowMode  = TextOverflowModes.Overflow;

            // 2026-06-03 Spencer: letter effects REMOVED for now — raw Clarity font,
            // no underlay drop-shadow, no face dilate. Re-enable the block below
            // (UNDERLAY_ON + offsets/softness + _FaceDilate 0.27) to restore them.
            var letterMat = _letterTMP.fontMaterial; // auto-instances from the shared SDF material
            if (LETTER_EMBOSS)
            {
                // LIGHT underlay pushed DOWN = the lit lower lip of a groove. See the
                // LETTER_EMBOSS block at the top of this file for why it is light, not dark.
                Color u = LETTER_EMBOSS_COLOR; u.a = Mathf.Clamp01(LETTER_EMBOSS_ALPHA);
                letterMat.EnableKeyword("UNDERLAY_ON");
                letterMat.SetColor("_UnderlayColor",    u);
                letterMat.SetFloat("_UnderlayOffsetX",  0f);
                letterMat.SetFloat("_UnderlayOffsetY",  LETTER_EMBOSS_OFFSET);
                letterMat.SetFloat("_UnderlayDilate",   0.05f);
                letterMat.SetFloat("_UnderlaySoftness", LETTER_EMBOSS_SOFTNESS);
            }
            else
            {
                letterMat.DisableKeyword("UNDERLAY_ON");
            }
            letterMat.SetFloat("_FaceDilate", 0.05f); // very slight bolden (no shadow); old was 0.27
            _letterTMP.UpdateMeshPadding();          // MUST follow — makes room for the underlay

            letterGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // ── Point value — quieter, no shadow ──
            GameObject pointGO = new GameObject("TilePoints");
            pointGO.transform.SetParent(transform, false);
            // same face-centring nudge as the letter (applied to its localPosition below)
            // Slight push toward the corner (was 0.22, then 0.28 was too aggressive
            // and made subscripts touch the tile edge). 0.25 gives breathing room
            // from the letter without crowding the tile boundary.
            pointGO.transform.localPosition = new Vector3(nativeSize * 0.25f, -nativeSize * 0.25f - faceNudge, -0.1f);

            _pointTMP = pointGO.AddComponent<TextMeshPro>();
            if (tileFont != null) _pointTMP.font = tileFont;
            _pointTMP.text           = "";
            _pointTMP.fontSize       = 2.8f * LetterSizeFactor;
            _pointTMP.fontStyle      = FontStyles.Normal;  // Fredoka Bold already bold
            _pointTMP.color          = new Color(0.40f, 0.40f, 0.50f, 1f);
            _pointTMP.alignment      = TextAlignmentOptions.Center;
            _pointTMP.sortingOrder   = BaseSortingOrder + 2; // above the gloss sheen (+1)
            _pointTMP.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
            _pointTMP.enableWordWrapping = false;
            _pointTMP.overflowMode  = TextOverflowModes.Overflow;
            // No shadow on point values — keep them quiet

            pointGO.transform.localScale = new Vector3(invScale, invScale, 1f);

            // ── Static drop shadow — ONLY the baked tile_shadowbig. No procedural
            // fallback (2026-06-04 Spencer): if the baked sprite didn't load we render
            // NO shadow rather than the old black silhouette. Stays UNLIT.
            // 2026-09-03: OFF for the overlap board — tiles now butt together and
            // stack, so a per-tile drop shadow just muddies the seams. The stacking
            // itself is what reads as depth.
            if (TILE_DROP_SHADOW && s_dropShadowSprite != null)
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
                    _glossSR.sortingOrder = BaseSortingOrder + 1; // above tile face; SetSortingOrder keeps it at order+1
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
                _innerShadowSR.sortingOrder = BaseSortingOrder + 1; // with the gloss (face+1), under the letter (face+2)
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
                    _letterTMP.color = LETTER_INK;
                }
                else
                {
                    _letterTMP.text = sentinel ? "" : letter.ToString().ToUpper();
                    _letterTMP.color = LETTER_INK;
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
                        FaceSprite = RestingFaceFor(Letter);
                        _spriteRenderer.color  = Color.white;
                        UpdateIridescent(IridescentTileTint);
                    }
                    else
                    {
                        FaceSprite = s_spriteWild ?? s_spriteNormal;
                        UpdateIridescent(false);
                    }
                }
                else
                {
                    UpdateIridescent(false);
                    // Restore to state-appropriate sprite (priority: primed > gold > normal)
                    if (_hasPrimedGlow)
                        FaceSprite = s_spriteGold ?? s_spriteNormal;
                    else if (_isGoldBonus)
                        FaceSprite = s_spriteGolden ?? s_spriteNormal;
                    else
                        FaceSprite = RestingFaceFor(Letter);
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
                _wildHaloSR.sortingOrder = BaseSortingOrder - 2; // halo renders behind its own tile face
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
                _iridSR.sortingOrder = BaseSortingOrder + 1; // over the face, under the letter (+2)
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
            // 2026-09-03: the resting FACE is per-letter now (vowel/high-value/normal), so a
            // swap that changes the letter must repaint it — but only when the tile is in its
            // plain resting state, or this would stomp primed/scored/gold/ice.
            // NOTE: deliberately NOT gated on _editSelected. A board SWAP sets the letter
            // while the tile is still selected (teal glow), and that glow is a HALO which
            // does not touch the sprite — gating on it meant a swapped tile kept its OLD
            // face, so a high-value letter swapped in stayed tan (and gold letters swapped
            // out stayed gold). 2026-09-03 Spencer.
            if (LETTER_CATEGORY_TINTS && _spriteRenderer != null
                && !_hasPrimedGlow && !_isShowingScoredSprite && !_isGoldBonus
                && !IsWild && !IsStone && !IsFrozen)
                FaceSprite = RestingFaceFor(letter);
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
            // 2026-09-03: sorting is per-ROW (see ROW_ORDER_STRIDE) so a tile that
            // FALLS must take its new row's band, or the overlap sorts wrong after
            // every gravity step.
            SetSortingOrder(BaseSortingOrder);
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

            if (_letterTMP != null && !_bonusLetterHidden)
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
            // Backstop for the pool race above: a live grid tile that something hid can't run a coroutine, and gravity
            // would wait ~10s on a fall that never starts. Show it and let it fall.
            if (!gameObject.activeSelf && Letter != '\0')
            {
                Debug.LogWarning($"[PoolRace] '{Letter}' ({Col},{Row}) was hidden when told to fall - re-shown");
                gameObject.SetActive(true);
            }
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
            // ROYAL MATCH FALL (2026-10-22, MEASURED from Spencer's 60fps RM recording: pieces start already moving
            // (~6-15 cells/s), speed up gently (~15-20 cells/s^2) and top out ~18-20 cells/s). Ours started from rest and
            // slammed in at 29 cells/s, with a different "gravity" per distance (43-213 cells/s^2) so columns stretched.
            // One rule for every tile now: v0, accel, vmax — a column falls as one block.
            if (RM_FALL && !mechanical)
            {
                float cellW = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.96f;
                float total = Vector3.Distance(start, target);
                float v = RM_FALL_V0 * cellW, travelled = 0f;
                while (travelled < total)
                {
                    float dt = Mathf.Min(Time.deltaTime, 1f / 30f);
                    v = Mathf.Min(v + RM_FALL_ACCEL * cellW * dt, RM_FALL_VMAX * cellW);
                    travelled = Mathf.Min(total, travelled + v * dt);
                    transform.position = Vector3.Lerp(start, target, total > 0.0001f ? travelled / total : 1f);
                    yield return null;
                }
            }
            float dur = RM_FALL && !mechanical ? 0f : Mathf.Max(duration, 0.12f);
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
            // Re-assert the row band on landing. Cheap, and it makes a wrong order
            // self-healing rather than something that can persist. 2026-09-10 Spencer.
            ReassertSortingOrder();

            // Organic lands get a squish; mechanical lands hard-stop (machine feel).
            // 2026-09-03 swap-pivot: cascading tiles now use the DROP landing squish —
            // the chunkier one a dropped letter used to play — because with the drop
            // mechanic gone, gravity IS how every tile arrives. Sound is off: one dropped
            // letter could play it, a whole cascade landing would be a cacophony (the
            // prime flash + primed SFX carry the "it landed and armed" moment instead).
            if (!mechanical)
            {
                if (GRAVITY_USES_LANDING_SQUISH) PlayLandingSquish(playSound: false);
                else                             PlayGravitySquish();
            }
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
                FaceSprite = s_spriteGolden ?? s_spriteNormal;
                _spriteRenderer.color = Color.white;
            }
            else if (!gold && _spriteRenderer != null)
            {
                if (_goldPulseCoroutine != null) { StopCoroutine(_goldPulseCoroutine); _goldPulseCoroutine = null; }
                // Restore normal sprite + default color/letter tints
                FaceSprite = RestingFaceFor(Letter);
                ApplyRestingColor();
                if (_letterTMP != null)
                    _letterTMP.color = LETTER_INK;
                if (_pointTMP != null)
                    _pointTMP.color = new Color(0.4f, 0.4f, 0.45f, 0.85f);
            }
        }

        // ── Swap refill visual — orange tint ──────────────────────────────────
        private static readonly Color SWAP_REFILL_TINT = new Color(1f, 0.7f, 0.2f, 1f);  // LEGACY-DROP: swap-refill tile — no longer spawns
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
        private static readonly Color EDIT_REFILL_TINT = new Color(0.2f, 0.9f, 0.95f, 1f);  // LEGACY-DROP: edit-refill tile — no longer spawns
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
        private static readonly Color WILD_REFILL_TINT = new Color(0.75f, 0.4f, 1f, 1f);  // LEGACY-DROP: wild-refill tile — no longer spawns
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
        private static Color BaseTintForLetter(char letter)   // value-accent tinting removed 2026-06-24 (kept as the single resting colour)
            => (CollectLetter != '\0' && char.ToUpperInvariant(letter) == CollectLetter && !CollectUsesBlueTile) ? COLLECT_TINT : Color.white;
        /// <summary>2026-10-22 Spencer: "for the collect the Es could we make them the blue coloured tile we had?" The collect
        /// letter wears the authored blue tile (vowel_tile@2x — the old vowel accent, off since 2026-09-03). false = violet tint.</summary>
        public static bool COLLECT_BLUE_TILE = true;
        // GEM TILES (2026-10-22 Spencer: "put the gems as letter tiles on the collect letters levels ... the point of the level
        // is to collect all the gems"). A gem is a property of the CELL (RulesCellData.HasGem), not of a letter: any ordinary
        // letter can carry one. The tile wears the yellow gem face with its letter on top; destroying it collects it.
        public static string GEM_TILE_ASSET = "Tiles/gem_teal";   // 2026-10-28: teal Blender gem (was "Tiles/gem_yellow"; the ARMED gold look keeps gem_yellow)
        // CARVED GEMS (2026-10-28 Spencer chose the Blender teal gem with the letter CARVED into it). One render per letter
        // (Resources/Tiles/GemLetters/gem_teal_A..Z, from WordDrop_icon_gen/gem_blender/gem_carved.py + add_gloss.py): a gem tile
        // wears its letter's carved face and the TMP letter is hidden while it does (LateUpdate). Armed gem words keep the gold
        // armed face + TMP letter. CARVED_GEMS = false -> blank teal gem with the TMP letter on top.
        public static bool   CARVED_GEMS = true;
        public static string CARVED_GEM_PREFIX = "Tiles/GemLetters/gem_teal_";
        private static readonly Sprite[] s_gemLetters = new Sprite[26];
        private static Sprite GemFaceFor(char letter)
        {
            if (CARVED_GEMS) { int i = char.ToUpperInvariant(letter) - 'A'; if (i >= 0 && i < 26 && s_gemLetters[i] != null) return s_gemLetters[i]; }
            return s_spriteGem;
        }
        public static bool IsCarvedGemFace(Sprite s)
        {
            if (s == null) return false;
            for (int i = 0; i < 26; i++) if (s_gemLetters[i] == s) return true;
            return false;
        }
        public static bool IsGemFace(Sprite s) => s != null && (s == s_spriteGem || IsCarvedGemFace(s));
        private bool _carvedHidLetter;
        private static Sprite s_spriteGem;
        public static Sprite GemSprite => s_spriteGem;
        public static readonly Color GEM_ACCENT = new Color(0.25f, 0.85f, 0.92f, 1f);   // teal (was gold 1,.82,.15 for the yellow gem)
        public bool IsGem { get; private set; }
        /// <summary>Re-apply the plain resting face + white tint (no-op while primed / scored). Refill uses it so a recycled tile
        /// never arrives wearing a previous word's gold/gem face (2026-10-23 "letters turned into gems").</summary>
        private float _bornAt;
        /// <summary>Stamp when this tile entered the board (checkout from the pool). Used by the [GemBorn] diag.</summary>
        public void MarkBorn() => _bornAt = Time.unscaledTime;

        /// <summary>BONUS BLAST rockets (2026-10-23 Spencer: "during the bonus it probably shouldnt have letters on the rockets cause it
        /// doesnt matter"): hide the letter + its plate. The tile is destroyed when the rocket fires, so nothing restores it.</summary>
        // FACE TRACE (TEMP DIAG 2026-10-26, Spencer: "letters flipping back and forth between regular tiles and gems ... this NEEDS
        // to be fixed" — 8 reports, every snapshot diag came back clean because the flip happens between the dump and the render).
        // EVERY write to the face goes through here; a plain letter getting a gem/gold face, or a real gem losing its gem face,
        // logs the caller chain. Rate-limited per tile.
        private float _faceTraceNext, _faceGuardLogNext;
        public static bool FACE_GUARD = true;
        private Sprite FaceSprite
        {
            get => _spriteRenderer != null ? _spriteRenderer.sprite : null;
            set
            {
                if (_spriteRenderer == null) return;
                var old = _spriteRenderer.sprite;
                _spriteRenderer.sprite = value;
                if (old == value || Time.unscaledTime < _faceTraceNext) return;
                bool goldish = value != null && (IsGemFace(value) || value == s_spritePrimedGold || value == s_spriteGolden
                               || (value == s_spriteHighValue && LetterData.GetPoints(Letter) < HIGH_VALUE_POINTS && !IsRainGold));
                bool wrongGold = goldish && !IsGem && !_hasPrimedGlow && !_iconIsTile;
                bool lostGem = IsGem && IsGemFace(old) && !IsGemFace(value) && !_hasPrimedGlow && !_isShowingScoredSprite;
                if (!wrongGold && !lostGem) return;
                _faceTraceNext = Time.unscaledTime + 1f;
                var st = new System.Diagnostics.StackTrace(1, false); var who = new System.Text.StringBuilder();
                for (int i = 0; i < Mathf.Min(6, st.FrameCount); i++) { var m = st.GetFrame(i).GetMethod(); if (m != null) who.Append(m.DeclaringType?.Name).Append('.').Append(m.Name).Append(i < 5 ? " < " : ""); }
                WDLog.Log($"[FaceTrace] ({Col},{Row}) '{Letter}' {(wrongGold ? "PLAIN LETTER got " + FaceName(value) : "GEM lost its face -> " + FaceName(value))} " +
                          $"(was {FaceName(old)}) by {who} | {DebugFaceState()}");
            }
        }
        private bool _bonusLetterHidden;   // 2026-10-26: the [BlankTile] safety net was switching the hidden letter back on
        public void HideLetterForBonus()
        {
            _bonusLetterHidden = true;
            if (!_iconIsTile) return;   // claimed but not a rocket yet: LateUpdate hides it the frame it becomes one
            // Hide by RENDERER only (2026-10-23 fix): deactivating the plate GameObject broke ExitIconTile, which only moves the
            // letter home while the plate is active — recycled bonus tiles came back with tiny corner letters (Spencer's screenshot).
            if (_letterTMP != null) _letterTMP.enabled = false;
            var psr = _letterPlateGO != null ? _letterPlateGO.GetComponent<SpriteRenderer>() : null;
            if (psr != null) psr.enabled = false;
        }

        public void ForceRestingFace()
        {
            if (_spriteRenderer != null && !_hasPrimedGlow && !_isShowingScoredSprite) { FaceSprite = RestingFaceFor(Letter); _spriteRenderer.color = Color.white; }
        }

        public void SetGem(bool on)
        {
            if (on == IsGem) return;
            // TEMP DIAG 2026-10-23 (Spencer: "the N gem just appeared out of nowhere ... flipping into a gem from a normal tile"):
            // every tile that BECOMES a gem says where, how long it had existed, and who called.
            if (on)
            {
                var st = new System.Diagnostics.StackTrace(1, false);
                var who = new System.Text.StringBuilder();
                for (int i = 0; i < Mathf.Min(3, st.FrameCount); i++) { var m = st.GetFrame(i).GetMethod(); if (m != null) who.Append(m.DeclaringType?.Name).Append('.').Append(m.Name).Append(i < 2 ? " < " : ""); }
                WDLog.Log($"[GemBorn] ({Col},{Row}) '{Letter}' became a gem — tile age {Time.unscaledTime - _bornAt:F2}s — by {who}");
            }
            IsGem = on;
            if (_spriteRenderer != null && !_hasPrimedGlow && !_isShowingScoredSprite) { FaceSprite = RestingFaceFor(Letter); _spriteRenderer.color = Color.white; }
        }
        public static string COLLECT_TILE_ASSET = "Tiles/blue_tile_pillow@2x";   // 2026-10-28 pillow family; original "Tiles/blue_tile@2x"
        private static Sprite s_spriteCollectBlue;
        private static Sprite CollectBlueFace => s_spriteCollectBlue != null ? s_spriteCollectBlue : s_spriteVowel;
        public static bool CollectUsesBlueTile => COLLECT_BLUE_TILE && CollectBlueFace != null;
        public static Sprite CollectFaceSprite => CollectUsesBlueTile ? CollectBlueFace : s_spriteNormal;
        /// <summary>The collect letter's colour for HUD/fly/debris: the blue tile's blue, or the violet tint.</summary>
        public static Color CollectAccent => CollectUsesBlueTile ? new Color(0.12f, 0.42f, 1.0f, 1f) : COLLECT_TINT;   // the ramp tile's deeper blue
        public static Color CollectFaceTint => CollectUsesBlueTile ? Color.white : COLLECT_TINT;
        /// <summary>COLLECT GOAL (2026-10-21 Spencer: "the letters that need to be collected should be a different colour on the
        /// board so they stand out"). Set by LevelRules.Apply; every tile of this letter rests violet.</summary>
        public static char CollectLetter = '\0';
        public static Color COLLECT_TINT = new Color(0.78f, 0.64f, 1f, 1f);   // violet — clear of gold (K/Q), ice blue and special pink

        // CONVERGE, DON'T WRITE-ONCE (2026-10-21 Spencer: "the E's lose their colour when you move them or interact with
        // them"). ~20 paths reset the face to plain white after a select / swap / flash / fade. Rather than patch each one,
        // every frame a RESTING tile settles to the colour it should have: violet if it's the collect letter, white if it
        // just stopped being one. Only acts when the face is exactly white or exactly violet, so it never fights a flash,
        // primed glow, gold or stone tint mid-effect.
        private void LateUpdate()
        {
            // BONUS ROCKETS: no badge, no letter (Spencer 2026-10-26: "i just dont want any badge or lettering showing during the
            // bonus"). Several paths re-show them (state sync re-entering the icon, letter-repair nets, priming), so this
            // converges every frame instead of trusting a one-time hide.
            // FACE GUARD (2026-10-26, Spencer: "certain tiles dont know if they are gems or not"). Last word before the frame is
            // drawn: a plain letter never wears the gem/gold-armed face unless a word is ARMING it, and a real gem always wears
            // the gem face when it's resting. Logs [FaceGuard] so the cause is still visible ([FaceTrace] names the writer).
            // GEM FLAG vs DATA (2026-10-26: an O that was a gem in the data showed plain and the airplane "collected" it). The data is
            // the truth; a settled tile whose gem flag disagrees is corrected here every frame, and logged.
            if (FACE_GUARD && !IsAnimating && Col >= 0 && Row >= 0 && RulesEngine.Instance != null && GridManager.Instance != null
                && GridManager.Instance.GetTile(Col, Row) == this)
            {
                var cd = RulesEngine.Instance.GetCell(Col, Row);
                if (cd != null && cd.Letter == Letter && cd.HasGem != IsGem)
                {
                    WDLog.Log($"[FaceGuard] ({Col},{Row}) '{Letter}' gem flag {IsGem} but data says {cd.HasGem} -> following the data | {DebugFaceState()}");
                    SetGem(cd.HasGem);
                }
            }
            if (FACE_GUARD && _spriteRenderer != null && !_iconIsTile && !_isShowingScoredSprite && !_hasPrimedGlow && !_swipePreview && !_editSelected
                && !_isStone && !IsFrozen && !IsWild && !IsDissolving)
            {
                var cur = _spriteRenderer.sprite;
                bool gemFace = cur != null && (IsGemFace(cur) || cur == s_spritePrimedGold);
                if (gemFace != IsGem || (IsGem && cur != GemFaceFor(Letter)))
                {
                    var reg = RulesEngine.Instance != null ? RulesEngine.Instance.PrimedRegistry : null;
                    bool armed = reg != null && Col >= 0 && Row >= 0 && reg.GetPrimedWordsContaining(new Vector2Int(Col, Row)).Count > 0;
                    if (!armed)
                    {
                        var restFace = RestingFaceFor(Letter);
                        if (restFace != null && restFace != cur)
                        {
                            if (Time.unscaledTime >= _faceGuardLogNext)
                            {
                                _faceGuardLogNext = Time.unscaledTime + 1f;
                                WDLog.Log($"[FaceGuard] ({Col},{Row}) '{Letter}' was {FaceName(cur)} -> {FaceName(restFace)} (gem={IsGem}) | {DebugFaceState()}");
                            }
                            FaceSprite = restFace; _spriteRenderer.color = Color.white;
                        }
                    }
                }
            }
            // CARVED GEM: the letter is in the gem art, so the TMP letter hides while the carved face shows (and comes back the
            // moment the face changes - armed gold, swipe preview, crate...). Only re-enables what THIS hid.
            {
                bool carved = _spriteRenderer != null && !_iconIsTile && IsCarvedGemFace(_spriteRenderer.sprite);
                if (carved) { if (_letterTMP != null && _letterTMP.enabled) _letterTMP.enabled = false; _carvedHidLetter = true; }
                else if (_carvedHidLetter) { _carvedHidLetter = false; if (_letterTMP != null && !_bonusLetterHidden) _letterTMP.enabled = true; }
            }
            if (_bonusLetterHidden && _iconIsTile)   // only once it IS a rocket: the plain tile keeps its letter until the rocket lands
            {
                if (_letterTMP != null && _letterTMP.enabled) _letterTMP.enabled = false;
                if (_letterPlateGO != null) { var bpsr = _letterPlateGO.GetComponent<SpriteRenderer>(); if (bpsr != null && bpsr.enabled) bpsr.enabled = false; }
                if (_pointTMP != null && _pointTMP.enabled) _pointTMP.enabled = false;
            }
            // Crate art carries its own colour — never let a tint (stone grey, flashes) darken it.
            if (_isCrateArt && _spriteRenderer != null && _spriteRenderer.color != Color.white && _flashCoroutine == null)
                _spriteRenderer.color = Color.white;
            // Icon-as-tile special: its face stays white (the art carries the colour) and its letter stays white-on-outline,
            // whatever a flash or reset wrote meanwhile.
            if (_iconIsTile)
            {
                // THE ICON STAYS (2026-10-22 Spencer: "when you make a word that has one of the icons like a dynamite or bomb the
                // tiles flip to green and that tile stays blank while the badge in the corner has the letter in it"). The word's
                // armed/scored face swap hit the special too and replaced its art with the plain green face. A special keeps its
                // own art through every state — the flash / glow TINT still lights it up, so it reads as part of the lit word.
                if (_spriteRenderer != null && SpecialKind != RulesEngine.SPECIAL_NONE)
                {
                    var iconFace = SpecialTileSprite(SpecialKind);
                    if (iconFace != null && _spriteRenderer.sprite != iconFace) FaceSprite = iconFace;
                }
                if (_letterPlateGO != null && _letterPlateGO.activeSelf && _spriteRenderer != null)
                {   // keep the token between the icon and the letter as the tile changes row band / lifts
                    var psr = _letterPlateGO.GetComponent<SpriteRenderer>();
                    if (psr != null && psr.sortingOrder != _spriteRenderer.sortingOrder + 1) psr.sortingOrder = _spriteRenderer.sortingOrder + 1;
                }
                // The letter must stay ON its token: flash / punch animations restore it to the tile centre when they end
                // (2026-10-22: a bomb born from WILE showed a blank token, its letter lost in the middle of the bomb art).
                if (_letterPlateGO != null && _letterPlateGO.activeSelf && _letterTMP != null)
                {
                    var lt = _letterTMP.transform;
                    if ((lt.localPosition - _letterPlatePos).sqrMagnitude > 1e-6f) lt.localPosition = _letterPlatePos;
                    if (!DOTween.IsTweening(lt) && (lt.localScale - _letterPlateScale).sqrMagnitude > 1e-6f) lt.localScale = _letterPlateScale;
                    if (!_letterTMP.gameObject.activeSelf) _letterTMP.gameObject.SetActive(true);
                    if (_letterTMP.alpha < 0.99f) _letterTMP.alpha = 1f;   // armed or not — the token letter never fades
                }
                Color inkWant = SPECIAL_LETTER_PLATE ? LETTER_INK : Color.white;
                // ON THE TOKEN the letter is ALWAYS dark ink — even while its word is armed. Armed words whiten their letters,
                // and white on the cream token vanished (2026-10-22 Spencer: "the letter in the badge disappearing looks weird").
                bool onToken = SPECIAL_LETTER_PLATE && _letterPlateGO != null && _letterPlateGO.activeSelf;
                if (_letterTMP != null && (onToken || !_hasPrimedGlow) && _letterTMP.color != inkWant) _letterTMP.color = inkWant;
                return;
            }
            if (_spriteRenderer == null || _hasPrimedGlow || _isShowingScoredSprite || _isStone || IsFrozen || IsRainGold) return;
            if (_flashCoroutine != null) return;
            bool want = CollectLetter != '\0' && char.ToUpperInvariant(Letter) == CollectLetter;
            if (CollectUsesBlueTile)
            {
                var sp = _spriteRenderer.sprite;
                if (want && sp == s_spriteNormal && _spriteRenderer.color == Color.white) FaceSprite = CollectBlueFace;
                else if (!want && sp == CollectBlueFace) { FaceSprite = RestingFaceFor(Letter); }
                if (_spriteRenderer.color == COLLECT_TINT) { _baseTint = Color.white; _spriteRenderer.color = Color.white; }
                return;
            }
            var c = _spriteRenderer.color;
            if (want && c == Color.white) { _baseTint = COLLECT_TINT; _spriteRenderer.color = COLLECT_TINT; }
            else if (!want && c == COLLECT_TINT) { _baseTint = Color.white; _spriteRenderer.color = Color.white; }
        }
        /// <summary>Re-tint after the collect letter changes (tiles already on the board).</summary>
        public void RefreshCollectTint()
        {
            RefreshBaseTint();
            if (!_hasPrimedGlow && !_isShowingScoredSprite && !_isStone && !IsFrozen) ApplyRestingColor();
        }
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

        /// <summary>Build the 13 blocker sprites. **WARMED AT LOAD — do not let this run during
        /// play.** 2026-09-15, Spencer: "during the first row up of every game the animation
        /// doesnt happen and there is a frame drop... its only the first time".
        ///
        /// MEASURED from debug_log.txt: the whole build landed on ONE frame (474), the exact frame
        /// of `[SFX] PlayRisingRow` and the first `'#'` tile — because it was lazy, behind
        /// `if (s_rockVariants == null)` inside SetStoneVisual, and the first stone arrives with
        /// the first rising row. Each of the 13 variants runs FOUR full-texture passes
        /// (LiftCopy / WorldTintCopy / SurfaceCopy / ContactEdgeCopy), every one a RenderTexture
        /// blit plus GetPixels+SetPixels with per-pixel HSV — ~3.1M pixel ops and 52 GPU
        /// round-trips in a single frame. The row-rise tween was starved, so the animation
        /// appeared not to happen at all.
        ///
        /// Idempotent: returns immediately once built.</summary>
        public static void BuildRockVariants()
        {
            if (s_rockVariants != null) return;
                        var list = new System.Collections.Generic.List<Sprite>();
                        for (int vi = 0; vi < ROCK_VARIANT_FILES.Length; vi++)
                        {
                        var path = ROCK_VARIANT_FILES[vi];
                        var rockTex = Resources.Load<Texture2D>(path);
                        if (rockTex != null)
                        {
                            // per-variant first, global ROCK_LIGHTEN only as the fallback
                            float lift = (vi < ROCK_VARIANT_LIGHTEN.Length) ? ROCK_VARIANT_LIGHTEN[vi] : ROCK_LIGHTEN;
                            // BOTH DIRECTIONS. LiftCopy is v^(1/g): g>1 brightens, g<1 DARKENS.
                            // The old `> 1.001f` test silently skipped every value below 1, so a
                            // variant brighter than target could not be brought down — metal_block
                            // arrives at raw L 146 against a 112 target. 2026-09-11.
                            if (Mathf.Abs(lift - 1f) > 0.001f) { var l = LiftCopy(rockTex, lift); if (l != null) rockTex = l; }
                            if (ROCK_WORLD_TINT > 0.001f)
                            { var t = WorldTintCopy(rockTex, ROCK_WORLD_HUE, ROCK_WORLD_TINT, ROCK_WORLD_DESAT); if (t != null) rockTex = t; }
                            if (ROCK_CONTRAST > 1.001f || ROCK_SAT > 1.001f)
                            { var k = SurfaceCopy(rockTex, ROCK_CONTRAST, ROCK_SAT); if (k != null) rockTex = k; }
                            if (ROCK_USE_AO)
                            {
                                float savedAO = TILE_AO_STRENGTH;
                                TILE_AO_STRENGTH = Mathf.Clamp01(savedAO * ROCK_AO_BOOST);
                                var t = ContactEdgeCopy(rockTex, false);
                                TILE_AO_STRENGTH = savedAO;
                                if (t != null) rockTex = t;
                            }
                            float refB = (s_spriteNormal != null && s_spriteNormal.bounds.size.x > 0.0001f)
                                ? s_spriteNormal.bounds.size.x : 1f;
                            list.Add(Sprite.Create(rockTex,
                                new Rect(0, 0, rockTex.width, rockTex.height),
                                new Vector2(0.5f, 0.5f), rockTex.width / refB));
                        }
                        }
                        s_rockVariants = list.ToArray();
                        WDLog.Log($"[Rock] {s_rockVariants.Length} variant(s) built (ao={ROCK_USE_AO})");
        }

        public void SetStoneVisual(bool active)
        {
            if (active && _isVault) { SetVaultVisual(true); return; } // vaults render as the chest, never grey
            if (active && _isDropTargetVisual) { SetDropTargetVisual(true); return; } // escort objects are NOT grey rocks
            _isStone = active;
            if (_spriteRenderer == null) return;
            // FULL BOARD CRATE (2026-10-21): crates are plain (falling) stones drawn with Spencer's crate art.
            // TREASURE (2026-10-27): a stone with TreasureHits > 0 draws its locked-treasure state instead (3 / 2 / 1 bands).
            var blockSprite = TreasureHits > 0 ? TreasureSprite(TreasureHits) : null;
            if (blockSprite == null && SurvivalManager.FullBoardActive) blockSprite = GetCrateSprite();
            if (active && blockSprite != null && (SurvivalManager.FullBoardActive || TreasureHits > 0))
            {
                FaceSprite = blockSprite; _spriteRenderer.color = Color.white;
                RefitToSprite(blockSprite); _isCrateArt = true;
                if (_letterTMP != null) _letterTMP.gameObject.SetActive(false);
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
                if (_glossGO != null) _glossGO.SetActive(false);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
                return;
            }
            if (!active && _isCrateArt)
            {
                _isCrateArt = false;
                var face = RestingFaceFor(Letter);
                if (face != null) { FaceSprite = face; RefitToSprite(face); }
            }
            if (active)
            {
                if (ROCK_SPRITE)
                {
                    BuildRockVariants();   // no-op once built; warmed at load, see below
                    if (s_rockVariants != null && s_rockVariants.Length > 0)
                    {
                        // deterministic per cell — same rock every time for a given cell
                        int hash = (Col * 73856093) ^ (Row * 19349663);
                        int idx = (hash & 0x7fffffff) % s_rockVariants.Length;
                        FaceSprite = s_rockVariants[idx];
                        _spriteRenderer.color  = Color.white;   // the art carries its own colour
                    }
                    else _spriteRenderer.color = STONE_TINT;    // fallback: the old grey tint
                }
                else _spriteRenderer.color = STONE_TINT;
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
                    _letterTMP.color = LETTER_INK;
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
        private static readonly Color DROP_TARGET_TINT = new Color(1f, 0.48f, 0f, 1f);  // LEGACY-DROP: escort target — no longer spawns // bright saturated orange (Spencer 2026-06-15)
        private bool _isDropTargetVisual;
        public bool  IsDropTargetVisual => _isDropTargetVisual;
        // PLACEHOLDER: the escort drop-target renders the chicken sprite instead of an orange tile.
        // 2026-06-19 Spencer.
        public static Sprite ChickenSprite => GetChickenSprite(); // exposed for the escort fly-up
        private static Sprite s_chickenSprite; private static bool s_chickenTried;
        // ── DUCK GOAL (2026-10-22 Spencer: "yes please" to the bring-it-down duck). The old escort-chicken drop-target now
        // wears Spencer's rubber duck. One unit wide, like the tile faces, so RefitToSprite and the HUD flight size it right. ──
        public static string DUCK_ASSET = "Goals/duck";
        private static Sprite s_duckSprite; private static bool s_duckTried;
        public static Sprite DuckSprite
        {
            get
            {
                if (s_duckTried) return s_duckSprite;
                s_duckTried = true;
                var tex = Resources.Load<Texture2D>(DUCK_ASSET);
                if (tex != null) s_duckSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
                WDLog.Log($"[Duck] {DUCK_ASSET} -> {(tex != null ? tex.width + "x" + tex.height : "MISSING")}");
                return s_duckSprite;
            }
        }
        private static Sprite GetChickenSprite()
        {
            if (DuckSprite != null) return DuckSprite;
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
                    if (_spriteRenderer.sprite != chick) { FaceSprite = chick; RefitToSprite(chick); } // swap+fit once
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
                if (normal != null && _spriteRenderer.sprite != normal) { FaceSprite = normal; RefitToSprite(normal); }
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
        private static readonly Color VAULT_MID_TINT = new Color(0.80f, 0.86f, 0.95f, 1f);  // LEGACY-DROP: vault chest — no longer spawns // cool silver
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

        /// <summary>Is the frost actually VISIBLE, as opposed to merely flagged? A tile can claim
        /// IsFrozen while showing no ice at all — the overlay deactivated, scaled to nothing by an
        /// interrupted melt, or faded to zero alpha.
        ///
        /// 2026-10-20: ReconcileSpecialVisuals compared tile.IsFrozen against cell.IsFrozen, and
        /// when both said TRUE it concluded all was well and moved on — while the player looked at
        /// a tile with no ice on it that still refused to swap. The same blind spot that hid the
        /// floating-tile bug: every check asked about BOOKKEEPING, and the fault was in PIXELS.
        /// Anything reconciling frost must ask this too, not just compare the flags.</summary>
        public bool FrostVisualHealthy
        {
            get
            {
                if (!_isFrozen) return true;                       // nothing to show
                if (_defrosting) return true;                      // the melt legitimately owns it
                if (_frostGO == null || !_frostGO.activeSelf) return false;
                if (_frostSR == null || _frostSR.sprite == null) return false;
                if (_frostSR.color.a < 0.05f) return false;        // faded out by an interrupted melt
                if (_spriteRenderer != null && _frostSR.sortingOrder < _spriteRenderer.sortingOrder + 2)
                    return false;                                  // drawn UNDER its own tile face (2026-10-20)
                var ls = _frostGO.transform.localScale;
                return ls.x > 0.01f && ls.y > 0.01f;               // shrunk to nothing by one
            }
        }

        /// <summary>Re-assert the frost from scratch — overlay active, at rest scale/position, at
        /// full tint. Safe to call on a tile that already looks right.</summary>
        public void RepairFrostVisual()
        {
            if (!_isFrozen) return;
            _defrostingUntil = 0f;      // an interrupted melt must not block the repair
            SetFrozenVisual(true);
        }
        private static readonly Color FROST_TINT = new Color(0.62f, 0.84f, 0.97f, 0.62f); // icy light-blue, translucent

        private static readonly Vector3 FROST_REST_LOCAL_POS = new Vector3(0f, 0f, -0.13f);

        /// <summary>The overlay's correct local scale, derived from the sprite exactly as
        /// EnsureFrostOverlay derives it — so the restore in SetFrozenVisual and the build
        /// cannot disagree about what "rest" means.</summary>
        private Vector3 FrostRestScale()
        {
            float displaySize = _cellSize * TILE_DISPLAY_RATIO;
            float restX   = Mathf.Max(0.01f, _restScale.x);
            float fNative = (_frostSR != null && _frostSR.sprite != null && _frostSR.sprite.bounds.size.x > 0f)
                ? _frostSR.sprite.bounds.size.x : 1f;
            float fs = (displaySize * 0.92f) / (fNative * restX);
            return new Vector3(fs, fs, 1f);
        }

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
            SyncFrostOrder();   // rides the tile's own row band — see SyncFrostOrder
            _frostGO.tag = "Untagged"; // skip 2D-light conversion (stays unlit)

            // Size the sheet to roughly cover the tile face (same counter-scale formula as the gloss).
            _frostGO.transform.localScale = FrostRestScale();   // one formula, shared with the re-freeze restore

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
        /// <summary>True only while the defrost animation genuinely owns the frost overlay.
        ///
        /// 2026-10-20 — THIS IS THE "ICE DISAPPEARS ON A ROW RISE" BUG. It used to be a plain
        /// bool set at the top of DefrostCoroutine and cleared at the very bottom. Unity does
        /// NOT unwind a coroutine that is stopped, pooled or disabled, so any rise that recycled
        /// a mid-thaw tile stranded it TRUE — permanently. From then on:
        ///   • SetFrozenVisual(false) hit its `if (!active && _defrosting) return` guard and
        ///     left _isFrozen TRUE, even from ResetForPool,
        ///   • the frost overlay stayed parked at the END of the melt — scale ~0, alpha 0,
        ///   • and ReconcileSpecialVisuals compares BOOLS, so tile(frozen) == cell(frozen),
        ///     everything "agreed", and nothing ever repaired the invisible overlay.
        /// Result: a tile that is still frozen for gameplay (refuses swaps) with no ice on it.
        ///
        /// A DEADLINE instead of a flag, for the same reason Tile.IsAnimating uses one: it is
        /// the only form that cannot be stranded by a cancelled coroutine.</summary>
        private bool _defrosting => Time.unscaledTime < _defrostingUntil;
        private float _defrostingUntil;   // guards against a sync clearing the frost mid-defrost
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
                if (_frostGO != null)
                {
                    _frostGO.SetActive(true);
                    SyncFrostOrder();   // re-freeze on a tile that has changed rows since — see SyncFrostOrder
                    // RESTORE THE REST TRANSFORM. 2026-10-20: a tile recycled part-way through
                    // the melt kept the overlay's shrunken scale and dropped position, so the
                    // next freeze re-activated a frost that was scaled to nothing — active,
                    // flagged frozen, and invisible. Setting the colour alone was not enough:
                    // the melt animates scale and position too, and whatever it last wrote is
                    // still there.
                    _frostGO.transform.localScale    = FrostRestScale();
                    _frostGO.transform.localPosition = FROST_REST_LOCAL_POS;
                    if (_frostSR != null) _frostSR.color = FROST_TINT;
                }
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
            // Claim the overlay for the length of the animation + a small margin. Self-expiring.
            _defrostingUntil = Time.unscaledTime + (0.10f + 0.42f) * 1.25f;
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
            _defrostingUntil = 0f;      // release the claim before the clear, or the guard eats it
            SetFrozenVisual(false);
            _defrostRoutine = null;
        }
        private static Sprite s_vaultSprite;
        private static bool   s_vaultSpriteTried;
        private Coroutine     _vaultIdleRoutine;   // anticipation jiggle while rendered as a chest

        // ── TREASURE TILE (2026-10-27) ── art: Resources/Blockers/treasure_{3,2,1}.png (+ treasure_band_{3,2}.png = the band that
        // comes off on that hit, cut as the difference between states). Hits left mirror RulesCellData.TreasureHits.
        public int TreasureHits { get; private set; }
        private static readonly Sprite[] s_treasure = new Sprite[4], s_treasureBand = new Sprite[4]; private static bool s_treasureTried;
        private static void LoadTreasure()
        {
            if (s_treasureTried) return; s_treasureTried = true;
            for (int i = 1; i <= 3; i++)
            {
                var t = Resources.Load<Texture2D>($"Blockers/treasure_{i}");
                if (t != null) s_treasure[i] = Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), t.width);
                var b = Resources.Load<Texture2D>($"Blockers/treasure_band_{i}");
                if (b != null) s_treasureBand[i] = Sprite.Create(b, new Rect(0, 0, b.width, b.height), new Vector2(0.5f, 0.5f), b.width);
            }
        }
        public static Sprite TreasureSprite(int hits) { LoadTreasure(); return s_treasure[Mathf.Clamp(hits, 1, 3)]; }
        public static Sprite TreasureBandSprite(int hits) { LoadTreasure(); return hits >= 2 && hits <= 3 ? s_treasureBand[hits] : null; }
        /// <summary>Set hits left and repaint (only when the tile is drawn as a block). Damage FX is TreasureFX.Hit.</summary>
        public void SetTreasureHits(int hits)
        {
            if (TreasureHits == hits) return;
            TreasureHits = hits;
            if (_isStone) SetStoneVisual(true);
        }

        // CRATE (2026-10-21 Spencer's placeholder, Resources/Blockers/crate.png): on the full board an anchored stone is a
        // Royal-Match-style crate, not the old treasure chest.
        private static Sprite s_crateSprite; private static bool s_crateTried;
        private bool _isCrateArt;
        public static Sprite CrateSprite => GetCrateSprite();
        private static Sprite GetCrateSprite()
        {
            if (s_crateTried) return s_crateSprite;
            s_crateTried = true;
            var tex = Resources.Load<Texture2D>("Blockers/crate");
            if (tex != null) s_crateSprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
            return s_crateSprite;
        }
        private static Sprite GetVaultSprite()
        {
            if (SurvivalManager.FullBoardActive && GetCrateSprite() != null) return s_crateSprite;
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
                    FaceSprite = vault;
                    RefitToSprite(vault); // chest bounds ≠ tile bounds — re-scale to the cell
                }
                _spriteRenderer.color = VaultTint; // tier telegraph: white / silver / gold
                ApplyVaultBadge(); // "4+"/"5+" requirement on mid/high chests; nothing on regular
                if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
                if (_glossGO != null) _glossGO.SetActive(false);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
                // Treasure-chest "shaking in anticipation" idle (Vampire-Survivors feel).
                if (_vaultIdleRoutine == null && gameObject.activeInHierarchy && !SurvivalManager.FullBoardActive)   // crates sit still
                    _vaultIdleRoutine = StartCoroutine(VaultIdleShakeLoop());
                if (_letterTMP != null && SurvivalManager.FullBoardActive) _letterTMP.gameObject.SetActive(false);   // a crate has no letter
            }
            else
            {
                if (_vaultIdleRoutine != null) { StopCoroutine(_vaultIdleRoutine); _vaultIdleRoutine = null; }
                transform.localRotation = Quaternion.identity; // undo any in-progress jiggle
                HideVaultBadge();
                Sprite normal = (OwnerIndex == 1 && s_spriteAI != null) ? s_spriteAI : s_spriteNormal;
                if (normal != null)
                {
                    FaceSprite = normal;
                    RefitToSprite(normal);
                }
                _spriteRenderer.color = Color.white;
                if (_glossGO != null) _glossGO.SetActive(true);
                if (_innerShadowGO != null) _innerShadowGO.SetActive(true);
                if (_letterTMP != null)
                {
                    _letterTMP.gameObject.SetActive(true);
                    _letterTMP.color = LETTER_INK;
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
            // ── DERIVE GOLD FROM THE COLOUR (2026-09-09 Spencer) ────────────────────────
            // "when a gold word is primed its still flashing magenta and then back to gold."
            // Fourteen call sites reach this method and most pass a gold GLOW COLOUR while
            // omitting the isGold flag, so the tint went gold while the FACE stayed magenta —
            // then a later path that did pass the flag corrected it. That correction is the
            // flash. The colour already carries the answer, so stop asking callers to repeat
            // it: any of them can now be sloppy without producing a visible wrong state.
            if (!isGold && ColorsClose(color, PRIMED_GOLD_GLOW)) isGold = true;
            if (ARMED_LETTER_WHITE && _letterTMP != null && !_letterWhitened && !_isWild)
            {
                _preArmedLetterColor = _letterTMP.color;
                _letterTMP.color     = Color.white;
                _letterWhitened      = true;
            }
            _staticPrimedApplied = false;   // force one repaint — colour/gold state may have changed
            bool wasAlreadyPrimed = _hasPrimedGlow;
            // Remember whether THIS tile was armed gold, so the pulse can pick the right
            // primed face instead of every primed tile sharing one.
            // 2026-09-10 Spencer, REVERSING this morning's per-letter face: a word that CONTAINS
            // a gold letter reads gold in full — "the gold word should take priority in colour".
            // KIN with a gold K and magenta I/N read as a mistake rather than as one word. The
            // resting (unprimed) face stays per-letter, which is what makes a lone gold tile
            // stand out on the board in the first place.
            if (GOLD_FLICKER_DIAG)   // 2026-09-21: was gated on IsGoldLetter, so the OTHER tiles in a gold word never logged — and those are the ones going green
                WDLog.Log($"[GoldFlicker] t={Time.unscaledTime:F2} ({Col},{Row}) '{Letter}' pts={LetterData.GetPoints(Letter)} isGold={isGold} colour={(ColorsClose(color, PRIMED_GOLD_GLOW) ? "GOLD" : "ROSE")} resolved={_isGoldPrimed} reg={RulesEngine.PrimedWordRegisteredForCell(new Vector2Int(Col, Row))} ← {new System.Diagnostics.StackTrace().GetFrame(1)?.GetMethod()?.Name}");
            _isGoldPrimed = ResolveGoldPrimed(isGold || ColorsClose(color, PRIMED_GOLD_GLOW));
            // APPLY THE FACE NOW, in the same call that sets the colour. The sprite swap used to
            // be deferred to the pulse loop, so for a frame or more a GOLD colour sat on top of
            // the ROSE armed art — and gold tint over rose art is that peach/orange Spencer kept
            // seeing. Colour and face are one decision; they must land together. 2026-09-10.
            if (_spriteRenderer != null && !IsWild && !IsStone && !IsFrozen)
            {
                BuildSpriteCache();
                Sprite want = ArmedFace();   // single chokepoint
                if (want != null && _spriteRenderer.sprite != want) FaceSprite = want;
            }
            // GEM WORD GLOW (2026-10-22 Spencer: "when you make a word add a glow to these"): an armed gem word pulses a warm
            // HDR glow (real bloom) on every tile until it pops / un-arms (ClearPrimedGlow kills it).
            if (_isGoldPrimed && GEM_WORD_GLOW && _gemGlowPulse == null && !IsWild && !IsStone)
            {
                _gemGlowAlpha = GEM_GLOW_LOW;
                SetBloomGlow(GEM_GLOW_HDR, _gemGlowAlpha, forceDesktop: true);
                _gemGlowPulse = DOTween.To(() => _gemGlowAlpha, a => { _gemGlowAlpha = a; SetBloomGlow(GEM_GLOW_HDR, a, forceDesktop: true); },
                                           GEM_GLOW_HIGH, 0.22f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
            }
            int oldFuse = _fuseRemaining;
            _pendingDiffusePop = false; // re-primed → cancel any pending diffuse-pop revert
            _hasPrimedGlow   = true;
            // If this tile is priming straight out of the teal edit/swap glow, kill that glow's bloom + pulse now
            // so the cyan doesn't linger a frame over the magenta primed look. No-op for normal drop-primed tiles
            // (no edit glow active). 2026-07-10 Spencer.
            if (_editGlowPulse != null) { _editGlowPulse.Kill(); _editGlowPulse = null; }
            if (SELECT_LIFT && _editSelected) ReassertSortingOrder();   // undo the tap-select draw-on-top (see FadeEditGlowOut)
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
            // PALETTE: red is reserved for TOP-OUT DANGER. The expiring-fuse red is a
            // drop-game signal — in armed-web nothing expires, so it must never fire or
            // red would mean two different things. 2026-09-08.
            if (!RulesEngine.ArmedWebMode && fuseRemaining >= 0 && fuseRemaining <= 1 && !isGold)
                glowColor = PRIMED_DANGER_GLOW;
            else
                glowColor = isGold ? PRIMED_GOLD_GLOW : PRIMED_GLOW;

            // AUTHORED PRIMED ART: light it up, do not RECOLOUR it. 2026-09-21, Spencer:
            // "the colour of the tiles actually shifts a dark green instead of looking like the
            // tile lights up". Sprite colour MULTIPLIES the texture, and PRIMED_GLOW is an HDR
            // magenta (1.8, 0.50, 1.04) authored for the magenta face — its 0.50 green channel
            // HALVES a green tile and its red/blue lift leaves the magenta fringe. Swapping in a
            // neutral tint of the same overall strength turns the pulse into pure LUMINANCE, so
            // any authored colour brightens instead of shifting hue.
            glowColor = NeutralisePrimedTint(glowColor, isGold);
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
            // 🚨 THE gold->green->gold FLASH. 2026-09-21. This assigned s_spriteGold (the rose/
            // green armed face) UNCONDITIONALLY, one line after the gold face had been correctly
            // applied — so a gold word painted gold, was stomped green here, then corrected back
            // by the pulse loop a frame later. Every other writer had been taught to ask
            // _isGoldPrimed; this one was missed because s_spriteGold and s_spriteGoldThick are
            // THE SAME OBJECT (both = loadedPrimed), so it logged under the other name.
            if (_spriteRenderer != null && s_spriteGold != null && Time.time >= _scoredFlashUntil && !_isShowingScoredSprite)
                FaceSprite = ArmedFace();   // single chokepoint

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
                    if (!WordDropFX.SCORED_FLASH_USES_PRIMED_COLOR
                        && s_spriteNormal != null && _spriteRenderer.sprite != s_spriteNormal)
                        FaceSprite = RestingFaceFor(Letter);
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
                    Sprite primedFace = ArmedFace();   // single chokepoint
                    // Hold rather than paint a guess — see GoldAnswerPending.
                    if (primedFace != null && _spriteRenderer.sprite != primedFace && !GoldAnswerPending)
                        FaceSprite = primedFace;
                }
                else
                {
                    if (!WordDropFX.SCORED_FLASH_USES_PRIMED_COLOR
                        && s_spriteNormal != null && _spriteRenderer.sprite != s_spriteNormal)
                        FaceSprite = RestingFaceFor(Letter);
                }

                // ── NOTHING IS MOVING (2026-09-08) ──────────────────────────────────
                // With PRIMED_ANIMATION off everything below recomputes an IDENTICAL colour
                // and scale every frame — once per armed tile, so a twelve-tile web does it
                // twelve times a frame to reach the same answer. Worse, it re-writes
                // sr.color, which FIGHTS GridManager's top-out danger pulse: both drive the
                // same property and the last writer in the frame wins, so armed tiles could
                // never flash red. Paint once, then stand down until something changes.
                if (!PRIMED_ANIMATION && pastMagentaFadeIn)
                {
                    // Danger owns sr.color while it is running — do not touch it.
                    if (_inDanger) { yield return null; continue; }
                    // ...and the gold state must match too. This cached "already painted" on
                    // COLOUR alone, so a tile that primed before its word was registered got the
                    // ROSE face, then _isGoldPrimed flipped to true when the registry caught up —
                    // and this early-out skipped the repaint forever. The colour said gold, the
                    // art stayed rose, and gold tint over rose art is the peach. 2026-09-10.
                    if (_staticPrimedApplied && _staticPrimedGold == _isGoldPrimed
                        && _spriteRenderer.color == _staticPrimedColor)
                    {
                        yield return null;
                        continue;
                    }
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
                int effectiveHeat = 0;
                if (PRIMED_ANIMATION)
                {
                    int dropsUrgency = GetDropsUrgency();
                    effectiveHeat = (_fuseRemaining >= 0)
                        ? dropsUrgency
                        : Mathf.Clamp(_heatLevel, 0, 4);
                }

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

                pulse = PRIMED_ANIMATION ? Mathf.Clamp01(pulse) : 0f;

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
                // Use the ONE definition so the loop and the scored-flash settle cannot drift
                // apart — a mismatch shows as a visible step at the hand-off.
                Color pc = PRIMED_ANIMATION
                    ? Color.Lerp(Color.white, _primedGlowColor, tintAmount)
                    : ArmedRestColor(_primedGlowColor);
                float pmax = Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b));
                // 2026-06-04 Spencer: the new glossy pink primed sprite is darker than
                // the old flat one, so the multiply rarely crossed the 1.30 bloom line —
                // the pulse stopped reading as a GLOW. Raise the cap so it blooms again.
                float primedCap = PRIMED_BLOOM ? Mathf.Max(WordDropFX.PrimedGlowCap, 1.55f)
                                                : PRIMED_LDR_CAP;
                if (pmax > primedCap) { float k = primedCap / pmax; pc.r *= k; pc.g *= k; pc.b *= k; }
                // Ease the magenta IN from white over the first PRIMED_MAGENTA_FADE_IN seconds after the
                // scored-flash hold expires (smooth green→white→magenta, no snap). After the window —
                // or when there was no recent scored flash — fadeIn is 1 and this is a no-op. 2026-06-23.
                if (!pastMagentaFadeIn && !WordDropFX.SCORED_FLASH_USES_PRIMED_COLOR)
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
                if (!PRIMED_ANIMATION) { _staticPrimedApplied = true; _staticPrimedColor = pc; _staticPrimedGold = _isGoldPrimed; }

                // TEMP diagnostic (2026-07-27): confirm the pulse is actually painting the detonation tint.
                if (_hasDetonationColor && !_detoProbeLogged)
                {
                    _detoProbeLogged = true;
                }

                // MOBILE bloom: pc above gets clamped to 1.0 on iOS/Metal (sr.color
                // → Color32), so on the phone the primed pulse never crosses the 1.30
                // bloom line. Feed the additive overlay the RAW HDR primed color with a
                // soft pulsing alpha so it glows on device. No-ops on desktop (the pc
                // path already blooms there). Floor near calm, brighter with heat.
                SetBloomGlow(_primedGlowColor,
                    PRIMED_BLOOM ? Mathf.Clamp01(0.26f + pulse * 0.18f + effectiveHeat * 0.03f) : 0f);

                // Scale: visible breathe at all levels, punchier at high heat
                float scalePulse = PRIMED_ANIMATION ? 0.035f + effectiveHeat * 0.015f : 0f;
                float scaleMult = 1f + scalePulse * pulse;

                // Tiny jitter at high urgency — nervous energy
                if (PRIMED_ANIMATION && effectiveHeat >= 3 && pulse < 0.2f)
                {
                    float jx = Random.Range(-0.005f, 0.005f);
                    float jy = Random.Range(-0.005f, 0.005f);
                    scaleMult += jx; // slight asymmetric wobble
                }

                if (!IsAnimating && !_externalScaleControl && _flashCoroutine == null)
                    transform.localScale = new Vector3(baseScale * scaleMult, baseScale * scaleMult, 1f);

                // Shimmer particles — more frequent at higher urgency
                // 2026-09-08 Spencer: "its barely even noticible as it is". Measured first —
                // the shimmer is POOLED (one shared ParticleSystem, EmitAt, no allocation), so
                // there was no performance case for cutting it. It goes because it earns nothing
                // on screen, and its rate was another fuse-driven escalation. Part of the same
                // "armed tiles are still" decision, so it rides the same flag.
                float shimmerInterval = PRIMED_ANIMATION ? 1.2f - effectiveHeat * 0.20f : float.MaxValue;
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
                FaceSprite = RestingFaceFor(Letter);
            }
            if (_letterWhitened && _letterTMP != null)
            {
                _letterTMP.color = _preArmedLetterColor;
                _letterWhitened  = false;
            }
            ClearBloomGlow();
            _staticPrimedApplied = false;
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

            if (!PRIMED_ANIMATION)
            {
                // ONE armed face. This method used to overwrite the sprite every frame
                // without consulting _isGoldPrimed, which is why gold-armed words kept
                // reverting to the magenta face no matter where we set it. 2026-09-08.
                Sprite armed = ArmedFace();   // single chokepoint
                if (armed != null && _spriteRenderer.sprite != armed) FaceSprite = armed;
                return;
            }

            int effectiveHeat = (_fuseRemaining >= 0)
                ? GetDropsUrgency()
                : Mathf.Clamp(_heatLevel, 0, 4);
            // 🚨 GOLD BEATS HEAT-0. 2026-09-21: this switch overwrote the sprite EVERY FRAME with
            // s_spriteGoldThick (the rose/green armed face) without consulting _isGoldPrimed — the
            // identical fault the !PRIMED_ANIMATION branch above was fixed for on 2026-09-08, but
            // only that branch got the fix. With PRIMED_ANIMATION on, a gold word painted GOLD,
            // then this repainted it green a frame later, then gold again: Spencer's
            // "gold to green to gold". Heat 1+ still wins — that is the fuse warning, which must
            // be able to override the reward colour.
            if (_isGoldPrimed && s_spritePrimedGold != null && effectiveHeat == 0)
            {
                if (_spriteRenderer.sprite != s_spritePrimedGold)
                    FaceSprite = s_spritePrimedGold;
                return;
            }
            switch (effectiveHeat)
            {
                case 0:  FaceSprite = s_spriteGoldThick; break;
                case 1:  FaceSprite = s_spriteHeat1 ?? s_spriteGoldThick; break;
                case 2:  FaceSprite = s_spriteHeat2 ?? s_spriteGoldThick; break;
                default: FaceSprite = s_spriteHeat3 ?? s_spriteGoldThick; break;
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

        // ── TOP-OUT DANGER (2026-09-03) ──────────────────────────────────────────────
        // The additive bloom overlay could not carry this: it ADDS light to an already
        // bright cream tile, so red barely shifted it. Danger has to change the FACE.
        // Tile owns this so it can restore whatever colour it had when the danger passes.
        private bool  _isGoldPrimed;   // armed with a high-value letter
        private bool  _staticPrimedApplied;   // static armed face is painted and current
        private bool  _staticPrimedGold;      // gold state the cached paint was made for
        private Color _staticPrimedColor;     // what we painted, so we can tell if anything moved it
        private bool  _inDanger;
        private Color _preDangerColor;
        private static readonly Color DANGER_FACE = new Color(1.0f, 0.28f, 0.22f, 1f);

        /// <summary>Pulse this tile red for top-out danger. t = 0..1 blend; 0 clears and
        /// restores the colour the tile had before the danger started.</summary>
        public void SetDangerPulse(float t)
        {
            if (_spriteRenderer == null) return;
            if (t <= 0.001f)
            {
                if (_inDanger)
                {
                    _inDanger = false;
                    _spriteRenderer.color = _preDangerColor;
                }
                return;
            }
            if (!_inDanger)
            {
                _inDanger = true;
                _preDangerColor = _spriteRenderer.color;   // remember whatever state it was in
            }
            _spriteRenderer.color = Color.Lerp(_preDangerColor, DANGER_FACE, Mathf.Clamp01(t));
        }

        /// <summary>Hide the bloom-glow overlay (mobile). Safe to call on desktop.</summary>
        public void ClearBloomGlow()
        {
            if (_bloomGlowSR != null) _bloomGlowSR.enabled = false;
        }

        /// <summary>Set sorting order on tile sprite + text layers.</summary>
        /// <summary>A lifted order that KEEPS the per-row band.
        ///
        /// The spotlight and aim-mode lifts used to assign every tile one flat order so the
        /// whole board cleared the dim scrim. That erased the row bands, and inside a stack
        /// the draw order then fell back to arbitrary — which is why a spotlighted tile could
        /// render behind the tile below it. Lifting by a base and KEEPING Row*STRIDE clears
        /// the scrim and preserves who occludes whom. Row maxes at 10, so a band is 30 wide.
        /// 2026-09-10 Spencer.</summary>
        /// <summary>Put this tile back at its authored rest scale.
        ///
        /// The rise pops new tiles with an OutElastic scale bounce lasting 0.85s while input
        /// resumes after 0.18s — so a following rise or gravity step can DOKill that tween
        /// part-way and leave the tile stranded at whatever scale the bounce had reached. A
        /// tile stranded small does not meet its neighbours, which is the purple gap between
        /// letters after a multi-row rise. 2026-09-10 Spencer.</summary>
        /// <summary>Is THIS tile's letter a high-value (gold) one?
        ///
        /// Gold is a property of the LETTER, not of the word it happens to be in. The word-level
        /// decision is correct for the GLOW COLOUR — deciding that per tile made gold words
        /// flicker between magenta and gold — but it was also driving the FACE, so every letter
        /// in a word containing one gold tile rendered gold. That is why a 2-point A came out
        /// gold. Colour is a word decision; the face is a letter decision. 2026-09-10 Spencer.</summary>
        public bool IsGoldLetter => LetterData.GetPoints(Letter) >= HIGH_VALUE_POINTS || IsRainGold;

        /// <summary>Shower reward letter (2026-10-21) — gold face + gold behaviour for an ordinary letter. Data lives on
        /// RulesCellData.IsRainGold; this is the visual mirror.</summary>
        public bool IsRainGold { get; private set; }
        public void SetRainGold(bool on)
        {
            if (on == IsRainGold) return;
            IsRainGold = on;
            if (_spriteRenderer != null && !_hasPrimedGlow) { FaceSprite = RestingFaceFor(Letter); ApplyRestingColor(); }
        }

        public void SnapRestScale()
        {
            if (_restScaleSet && !_externalScaleControl) transform.localScale = _restScale;
        }

        public int LiftedOrder(int lift) => lift + Mathf.Max(Row, 0) * ROW_ORDER_STRIDE;

        /// <summary>THE one place that resolves what order this tile should have right now:
        /// per-tile spotlight, else the aim/spotlight lift, else its row band. Anything that
        /// changes a tile's row or finishes moving it should call this rather than deciding
        /// for itself. 2026-09-10 Spencer.</summary>
        /// <summary>Raise this tile above the board for an effect, for a bounded time.
        ///
        /// The lift used to be undone by a tween callback, so any killed tween stranded the tile
        /// above everything and its shaded top cap drew over the row above it. A DEADLINE cannot
        /// be stranded: once it passes, the next ReassertSortingOrder puts the tile back whether
        /// or not any callback ever ran. 2026-09-10 Spencer.</summary>
        public void LiftForFx(float seconds)
        {
            float dur = Mathf.Max(0.05f, seconds);
            _fxOrderUntil = Time.unscaledTime + dur;
            SetSortingOrder(FX_TILE_ORDER);
            // Restore ITSELF. The deadline alone was not enough: it is only consulted when
            // something calls ReassertSortingOrder, and a tile nobody touches again just stayed
            // at 60 — the invariant checker caught exactly that. DOVirtual.DelayedCall is not
            // attached to this transform, so tile.transform.DOKill() cannot cancel it the way it
            // cancelled the punch tween's OnComplete. 2026-09-10 Spencer.
            DG.Tweening.DOVirtual.DelayedCall(dur + 0.02f, () =>
            {
                if (this == null) return;
                // A LATER lift may have extended the deadline (the hint re-lifts every refresh) — an older timer must not drop
                // it early. 2026-10-23.
                if (Time.unscaledTime + 0.01f < _fxOrderUntil) return;
                _fxOrderUntil = 0f;
                ReassertSortingOrder();
            }, ignoreTimeScale: true);
        }

        private float _fxOrderUntil;
        /// <summary>Drop an FX lift NOW (the hint clears the moment the player touches). The self-restore timer from
        /// LiftForFx still fires later and simply re-asserts the same normal order — harmless.</summary>
        /// <summary>The tile's letter is flying to the HUD (theme word): vanish in place. Scale 0 is the state an exploded tile
        /// ends in anyway, so the pool's Reinitialise already restores it. 2026-10-23.</summary>
        public void HideForFlight()
        {
            transform.DOKill();
            MarkAnimating(1f);
            transform.localScale = Vector3.zero;
        }

        public void EndFxLift()
        {
            if (_fxOrderUntil <= 0f) return;
            _fxOrderUntil = 0f;
            ReassertSortingOrder();
        }

        /// <summary>Put the CORRECT armed art on, now. Same idea as ReassertSortingOrder: the
        /// face is derived from current state rather than trusted to whoever set it last.
        ///
        /// The face used to be applied in the same call that computed _isGoldPrimed — fine, but
        /// if that call happened BEFORE the word reached the registry the answer was "rose", and
        /// nothing came back to correct it. The invariant checker caught whole words sitting in
        /// ROSE art with every gold flag set. Called from the board sync, so it converges.
        /// 2026-09-10 Spencer.</summary>
        public void RefreshArmedFace()
        {
            if (!_hasPrimedGlow || _spriteRenderer == null) return;
            if (IsWild || IsStone || IsFrozen) return;
            if (_isShowingScoredSprite) return;          // the detonation owns the face
            BuildSpriteCache();
            bool gold = ResolveGoldPrimed(false);        // sticky + letter + registry
            _isGoldPrimed = gold;
            Sprite want = ArmedFace();   // single chokepoint
            if (want != null && _spriteRenderer.sprite != want)
            {
                FaceSprite = want;
                _staticPrimedApplied = false;            // force the pulse loop to repaint colour too
            }
        }

        public void ReassertSortingOrder()
        {
            if (_fxOrderUntil > 0f)
            {
                if (Time.unscaledTime < _fxOrderUntil) { SetSortingOrder(FX_TILE_ORDER); return; }
                _fxOrderUntil = 0f;   // expired — fall through and restore the row band
            }
            SetSortingOrder(_spotlightOrder >= 0 ? LiftedOrder(_spotlightOrder)
                          : (AimModeTileOrder > 0 ? LiftedOrder(AimModeTileOrder) : BaseSortingOrder));
        }

        public void SetSortingOrder(int order)
        {
            // While a tutorial spotlight is DIMMING this tile, block external FX bumps (WordDropFX charge/detonation
            // raises tiles to 15) from lifting it above the scrim — charged tiles were bleeding bright. 2026-07-08 Spencer.
            if (SpotlightActive && _spotlightOrder < 0) order = BaseSortingOrder;
            if (_spriteRenderer != null) _spriteRenderer.sortingOrder = order;
            if (_iridSR != null) _iridSR.sortingOrder = order + 1;            // holographic fill, above the face
            if (_glossSR != null) _glossSR.sortingOrder = order + 1;          // top sheen, above the face
            if (_innerShadowSR != null) _innerShadowSR.sortingOrder = order + 1; // bottom shadow, above the face
            if (_bloomGlowSR != null) _bloomGlowSR.sortingOrder = order + 1;  // additive glow, above the face (below the letter)
            if (_letterTMP != null) _letterTMP.sortingOrder = order + 2; // letter crisp ABOVE both
            if (_pointTMP != null) _pointTMP.sortingOrder = order + 2;
            SyncFrostOrder();
            SyncSpecialOrder();
        }

        /// <summary>ICE MUST RIDE ITS TILE'S SORTING BAND. 2026-10-20 Spencer: "ice on tiles is
        /// dissapearing when a row rises sometimes". The frost was created at a FIXED order 10 — right
        /// when every tile sat at 5, wrong since 2026-09-03 gave each row its own band
        /// (5 + row*3). From row 2 up the tile's own face (11+) drew OVER its ice: the tile stayed
        /// frozen (still refused swaps) but the ice was invisible. A rise pushing an ice tile from
        /// row 1 to row 2 is exactly "disappears on a rise". Frost sits at the letter's order
        /// (face+2) and wins the tie on Z (-0.13, in front of the letter), and stays below the next
        /// row's face (face+3), so the overlap between rows is still right.</summary>
        // ── SPECIAL TILE VISUAL (2026-10-21, PLACEHOLDER until Spencer paints rocket / bomb / disco art) ───────────
        // A pulsing coloured ADDITIVE halo behind the tile (world-space -> real bloom) + a small corner pip showing the
        // kind: horizontal bar = row rocket, vertical bar = column rocket, dot = bomb, ring+dot = disco.
        public int SpecialKind { get; private set; }
        private GameObject _specialHaloGO, _specialPipGO, _specialPip2GO;
        private SpriteRenderer _specialHaloSR, _specialPipSR, _specialPip2SR;
        private Tween _specialPulse;
        private static Material s_specialHaloMat;
        private static Sprite s_pipBarH, s_pipBarV, s_pipDot, s_pipRing;

        // Spencer's placeholder icons (2026-10-21) — Resources/Specials/special_{rocket,bomb,disco}.png. Loaded as Texture2D
        // and wrapped in a Sprite here (1 world unit wide) so the import settings / odd sizes (bomb is 515px) don't matter.
        private static readonly System.Collections.Generic.Dictionary<string, Sprite> s_specialIcons = new System.Collections.Generic.Dictionary<string, Sprite>();
        public static Sprite SpecialIcon(int kind)
        {
            string key = kind == RulesEngine.SPECIAL_BOMB ? "special_bomb" : kind == RulesEngine.SPECIAL_TNT ? "special_tnt" : kind == RulesEngine.SPECIAL_DISCO ? "special_disco" : kind == RulesEngine.SPECIAL_AIRPLANE ? "special_airplane"
                       : (kind == RulesEngine.SPECIAL_ROCKET_H || kind == RulesEngine.SPECIAL_ROCKET_V) ? "special_rocket" : null;
            if (key == null) return null;
            if (s_specialIcons.TryGetValue(key, out var sp)) return sp;
            var tex = Resources.Load<Texture2D>("Specials/" + key);
            sp = tex != null ? Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width) : null;
            s_specialIcons[key] = sp;
            if (sp == null) WDLog.Log($"[Special] icon Specials/{key} MISSING — using placeholder pip");
            return sp;
        }
        /// <summary>The rocket art's nose points up-right (45°); rotate it to point along its row or column.</summary>
        public static float SpecialIconAngle(int kind) =>
            kind == RulesEngine.SPECIAL_ROCKET_H ? -45f : kind == RulesEngine.SPECIAL_ROCKET_V ? 45f : 0f;
        public static float SPECIAL_BADGE_SCALE = 0.55f;
        public static bool  SPECIAL_ICON_AS_TILE = true;
        public static float SPECIAL_TILE_ICON_FILL = 1.08f;   // icon art width vs the tile face (the art has transparent margins)
        // ROCKET CLIPPED (2026-10-23 Spencer: "the rockets nub ... is being secluded ... was it smaller before?" — no, same size
        // since 10-21). The art is diagonal; turning it upright inside the SAME 512 square sliced ~3% off the nose and the
        // flame (measured: rotated bbox touched both edges). RotateTex now rotates into a canvas big enough for the whole art,
        // and rotated icons are sized by their opaque nose-to-tail span: 1.0 = exactly the tile face's height.
        public static float SPECIAL_ROTATED_ICON_LEN = 1.0f;
        private bool _iconIsTile;
        private static readonly System.Collections.Generic.Dictionary<int, Sprite> s_specialTileSprites = new System.Collections.Generic.Dictionary<int, Sprite>();

        /// <summary>The special's art as a FULL TILE face: same world size as the normal face, so every path that swaps the
        /// face (RestingFaceFor) can use it without a refit. Rockets are pre-rotated to point along their row / column so the
        /// tile transform (and its letter) never rotates.</summary>
        public static Sprite SpecialTileSprite(int kind)
        {
            if (s_specialTileSprites.TryGetValue(kind, out var sp)) return sp;
            var icon = SpecialIcon(kind);
            if (icon == null || s_spriteNormal == null) { s_specialTileSprites[kind] = null; return null; }
            Texture2D src = icon.texture;
            float ang = SpecialIconAngle(kind);
            bool rotated = Mathf.Abs(ang) > 0.01f;
            Texture2D tex = rotated ? RotateTex(src, ang) : src;
            float faceW = s_spriteNormal.bounds.size.x;
            float ppu = tex.width / (faceW * SPECIAL_TILE_ICON_FILL);
            if (rotated) ppu = OpaqueSpan(tex) / (faceW * SPECIAL_ROTATED_ICON_LEN);   // size the rocket by its real nose-to-tail length
            sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), ppu);
            s_specialTileSprites[kind] = sp;
            return sp;
        }

        /// <summary>Rotate a texture about its centre (bilinear, transparent outside). Needs a readable source; falls back to
        /// a GPU blit copy when it isn't.</summary>
        private static Texture2D RotateTex(Texture2D src, float degrees)
        {
            Texture2D readable = src;
            if (!src.isReadable)
            {
                var rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(src, rt);
                var prev = RenderTexture.active; RenderTexture.active = rt;
                readable = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0); readable.Apply();
                RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            }
            int w = readable.width, h = readable.height;
            // output canvas fits the WHOLE rotated art (the old same-size canvas cropped the rocket's tips)
            float rr = degrees * Mathf.Deg2Rad, ac = Mathf.Abs(Mathf.Cos(rr)), asn = Mathf.Abs(Mathf.Sin(rr));
            int W = Mathf.CeilToInt(w * ac + h * asn), H = Mathf.CeilToInt(w * asn + h * ac);
            var inPx = readable.GetPixels(); var outPx = new Color[W * H];
            float rad = -degrees * Mathf.Deg2Rad, cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
            float cx = (w - 1) * 0.5f, cy = (h - 1) * 0.5f, ocx = (W - 1) * 0.5f, ocy = (H - 1) * 0.5f;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    float dx = x - ocx, dy = y - ocy;
                    float sx = cs * dx - sn * dy + cx, sy = sn * dx + cs * dy + cy;
                    if (sx < 0 || sy < 0 || sx > w - 1 || sy > h - 1) { outPx[y * W + x] = new Color(0, 0, 0, 0); continue; }
                    int x0 = (int)sx, y0 = (int)sy, x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
                    float fx = sx - x0, fy = sy - y0;
                    Color a = Color.Lerp(inPx[y0 * w + x0], inPx[y0 * w + x1], fx);
                    Color b = Color.Lerp(inPx[y1 * w + x0], inPx[y1 * w + x1], fx);
                    outPx[y * W + x] = Color.Lerp(a, b, fy);
                }
            var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            t.SetPixels(outPx); t.Apply();
            return t;
        }

        /// <summary>The larger of the opaque bounding box's width and height, in pixels (alpha &gt; 0.08).</summary>
        private static float OpaqueSpan(Texture2D t)
        {
            var px = t.GetPixels(); int w = t.width, h = t.height;
            int x0 = w, x1 = -1, y0 = h, y1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a > 0.08f) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
            return x1 < 0 ? w : Mathf.Max(x1 - x0 + 1, y1 - y0 + 1);
        }

        // LETTER PLATE (2026-10-22 Spencer: "there is definitely an issue of readability" — white outlined L lost on the
        // dynamite's red/white stripes). The letter moves onto a small cream token in the bottom-right corner, in the normal
        // dark ink, so it reads like every other tile while the icon stays recognisable around it. false = outlined letter.
        public static bool  SPECIAL_LETTER_PLATE = true;
        public static float SPECIAL_PLATE_SIZE   = 0.62f;   // plate diameter as a fraction of the face width (0.56 -> 0.62, 2026-10-22 "a hair bigger")
        public static float SPECIAL_PLATE_OFFSET = 0.25f;   // centre offset toward the corner, fraction of the face width
        // CENTRED TOKEN (2026-10-22 Spencer: "position the letter badge for the dynamite and so on in the middle of the icon
        // rather than the corner?"). The token sits exactly where every normal tile's letter sits, so a special's letter
        // lines up with the word around it; the icon reads as a ring around the token. false = the corner token above.
        public static bool  SPECIAL_PLATE_CENTRED      = false;   // 2026-10-22 Spencer: back to the corner
        public static float SPECIAL_PLATE_SIZE_CENTRED = 0.58f;   // token diameter, fraction of the face width
        public static float SPECIAL_LETTER_SCALE_CENTRED = 0.78f; // letter size vs a normal tile's letter
        private GameObject _letterPlateGO;
        private Vector3 _letterHomePos, _letterHomeScale, _letterPlatePos, _letterPlateScale;
        private static Sprite s_plateSprite;

        private void EnterIconTile(int kind)
        {
            _iconIsTile = true;
            if (_spriteRenderer != null) { FaceSprite = SpecialTileSprite(kind); _spriteRenderer.color = Color.white; }
            if (_glossGO != null) _glossGO.SetActive(false);
            if (_innerShadowGO != null) _innerShadowGO.SetActive(false);
            if (_pointTMP != null) _pointTMP.gameObject.SetActive(false);
            if (_letterTMP == null) return;
            if (!SPECIAL_LETTER_PLATE)
            {
                _letterTMP.color = Color.white;
                _letterTMP.outlineWidth = 0.28f;
                _letterTMP.outlineColor = new Color32(20, 22, 40, 255);
                return;
            }
            float faceW = s_spriteNormal != null ? s_spriteNormal.bounds.size.x : 1.7f;
            // Capture the letter's normal home ONCE (re-entering must not treat the token position as home).
            bool onPlateAlready = _letterPlateGO != null && _letterPlateGO.activeSelf;
            Vector3 home = onPlateAlready ? _letterHomePos : _letterTMP.transform.localPosition;
            float plateSize = SPECIAL_PLATE_CENTRED ? SPECIAL_PLATE_SIZE_CENTRED : SPECIAL_PLATE_SIZE;
            Vector3 c = SPECIAL_PLATE_CENTRED
                ? new Vector3(home.x, home.y, -0.09f)
                : new Vector3(faceW * SPECIAL_PLATE_OFFSET, -faceW * SPECIAL_PLATE_OFFSET, -0.09f);
            if (s_plateSprite == null)
            {
                var px = TileRenderer.CreateRoundedRect(128, 128, 64, TILE_FILL_COLOR, new Color(0.08f, 0.12f, 0.30f, 1f), 7);   // cream token, navy rim
                s_plateSprite = px;
            }
            if (_letterPlateGO == null)
            {
                _letterPlateGO = new GameObject("LetterPlate");
                _letterPlateGO.transform.SetParent(transform, false);
                _letterPlateGO.AddComponent<SpriteRenderer>();
            }
            var psr = _letterPlateGO.GetComponent<SpriteRenderer>();
            psr.sprite = s_plateSprite;
            psr.sortingOrder = (_spriteRenderer != null ? _spriteRenderer.sortingOrder : BaseSortingOrder) + 1;   // over the icon, under the letter (+2)
            float plateNative = s_plateSprite != null ? s_plateSprite.bounds.size.x : 1.28f;
            _letterPlateGO.transform.localPosition = c;
            _letterPlateGO.transform.localScale = Vector3.one * (faceW * plateSize / plateNative);
            if (!onPlateAlready) { _letterHomePos = _letterTMP.transform.localPosition; _letterHomeScale = _letterTMP.transform.localScale; }
            _letterPlateGO.SetActive(true);
            _letterPlatePos = new Vector3(c.x, c.y, -0.1f);
            _letterPlateScale = _letterHomeScale * (SPECIAL_PLATE_CENTRED ? SPECIAL_LETTER_SCALE_CENTRED : SPECIAL_PLATE_SIZE * 1.05f);
            _letterTMP.transform.localPosition = _letterPlatePos;
            _letterTMP.transform.localScale = _letterPlateScale;
            _letterTMP.gameObject.SetActive(true);
            _letterTMP.color = LETTER_INK;
            _letterTMP.outlineWidth = 0f;
        }

        private void ExitIconTile()
        {
            // Letter home WHENEVER a home was recorded — not only while the plate is active (a deactivated plate stranded the letter
            // small in the corner on recycled tiles, 2026-10-23).
            if (_letterTMP != null && _letterHomeScale.sqrMagnitude > 0f)
            {
                _letterTMP.transform.localPosition = _letterHomePos;
                _letterTMP.transform.localScale = _letterHomeScale;
            }
            _bonusLetterHidden = false;
            if (_letterTMP != null) _letterTMP.enabled = true;
            if (_letterPlateGO != null) { var psr = _letterPlateGO.GetComponent<SpriteRenderer>(); if (psr != null) psr.enabled = true; _letterPlateGO.SetActive(false); }
            _iconIsTile = false;
            if (_spriteRenderer != null) { FaceSprite = RestingFaceFor(Letter); ApplyRestingColor(); }
            if (_glossGO != null) _glossGO.SetActive(true);
            if (_innerShadowGO != null) _innerShadowGO.SetActive(true);
            if (_pointTMP != null) _pointTMP.gameObject.SetActive(true);
            if (_letterTMP != null) { _letterTMP.outlineWidth = 0f; _letterTMP.color = LETTER_INK; }
        }
        public static float SPECIAL_HALO_SCALE  = 1.16f;   // was 1.32
        public static float SPECIAL_HALO_MAX    = 1.1f;    // halo colour peak — under the bloom threshold (1.3) so it can't wash the face   // icon width as a fraction of the tile face

        public static Color SpecialColor(int kind) =>
              RulesEngine.IsBombLike(kind)       ? new Color(2.4f, 0.55f, 0.35f, 1f)   // hot red-orange
            : kind == RulesEngine.SPECIAL_DISCO ? new Color(1.7f, 1.1f, 2.4f, 1f)    // violet-white
            : kind == RulesEngine.SPECIAL_AIRPLANE ? new Color(0.6f, 1.6f, 2.4f, 1f) // sky blue
            :                                     new Color(2.2f, 1.25f, 0.35f, 1f);  // rocket: orange-gold

        public void SetSpecial(int kind, bool pop = false)
        {
            if (kind == SpecialKind && (kind == RulesEngine.SPECIAL_NONE || _specialHaloGO != null || _iconIsTile)) return;
            SpecialKind = kind;
            _specialPulse?.Kill(); _specialPulse = null;
            if (kind == RulesEngine.SPECIAL_NONE)
            {
                if (_specialHaloGO != null) _specialHaloGO.SetActive(false);
                if (_specialPipGO  != null) _specialPipGO.SetActive(false);
                if (_specialPip2GO != null) _specialPip2GO.SetActive(false);
                if (_iconIsTile) ExitIconTile();
                return;
            }
            // ICON AS THE TILE (2026-10-21 Spencer: "instead of ... the icon as a badge is it possible to just make the icon the
            // tile and i would like to see how the letters overlay overtop of the icons"). The art IS the tile face; the
            // letter sits on top in white with a dark outline. SPECIAL_ICON_AS_TILE = false -> the corner-badge look.
            if (SPECIAL_ICON_AS_TILE && SpecialTileSprite(kind) != null)
            {
                if (_specialHaloGO != null) _specialHaloGO.SetActive(false);
                if (_specialPipGO  != null) _specialPipGO.SetActive(false);
                if (_specialPip2GO != null) _specialPip2GO.SetActive(false);
                EnterIconTile(kind);
                if (pop) { transform.DOKill(); Vector3 r0 = CanonicalRestScale(); transform.localScale = r0 * 0.6f; transform.DOScale(r0, 0.32f).SetEase(Ease.OutBack, 2.4f); }
                return;
            }
            if (s_pipBarH == null)
            {
                s_pipBarH = TileRenderer.CreateSolidRoundedRect(96, 34, 17, Color.white);
                s_pipBarV = TileRenderer.CreateSolidRoundedRect(34, 96, 17, Color.white);
                s_pipDot  = TileRenderer.CreateSolidRoundedRect(56, 56, 28, Color.white);
                s_pipRing = TileRenderer.CreateRoundedRect(80, 80, 40, new Color(1f, 1f, 1f, 0f), Color.white, 9);
            }
            if (s_specialHaloMat == null)
            {
                Shader sh = Shader.Find("WordDrop/AdditiveSprite") ?? Resources.Load<Shader>("Shaders/AdditiveSprite");
                if (sh != null) s_specialHaloMat = new Material(sh) { name = "SpecialHaloAdditive" };
            }
            Color col = SpecialColor(kind);
            if (_specialHaloGO == null)
            {
                _specialHaloGO = new GameObject("SpecialHalo");
                _specialHaloGO.transform.SetParent(transform, false);
                _specialHaloGO.transform.localPosition = new Vector3(0f, 0f, 0.02f);
                _specialHaloSR = _specialHaloGO.AddComponent<SpriteRenderer>();
                _specialHaloSR.sprite = s_spriteNormal;
                if (s_specialHaloMat != null) _specialHaloSR.sharedMaterial = s_specialHaloMat;
                _specialPipGO = new GameObject("SpecialPip");
                _specialPipGO.transform.SetParent(transform, false);
                _specialPipSR = _specialPipGO.AddComponent<SpriteRenderer>();
                _specialPip2GO = new GameObject("SpecialPip2");
                _specialPip2GO.transform.SetParent(transform, false);
                _specialPip2SR = _specialPip2GO.AddComponent<SpriteRenderer>();
            }
            _specialHaloGO.SetActive(true);
            // TONED DOWN (2026-10-21 Spencer: "what the pulsating golden thing is supposed to be. is it just scaled wrong?").
            // The halo is behind the face, but its HDR colour (up to 2.4) tripped URP bloom, and the bloom blurred FORWARD
            // over the tile — the whole face read as a big pulsing gold tile. Now: a smaller rim, colour capped under the
            // bloom threshold (1.3), dimmer pulse. The sticker carries "this is special"; the halo is a warm edge.
            _specialHaloGO.transform.localScale = Vector3.one * SPECIAL_HALO_SCALE;
            float peak = Mathf.Max(col.r, Mathf.Max(col.g, col.b));
            Color haloCol = peak > SPECIAL_HALO_MAX ? new Color(col.r / peak * SPECIAL_HALO_MAX, col.g / peak * SPECIAL_HALO_MAX, col.b / peak * SPECIAL_HALO_MAX, 1f) : col;
            var mpb = new MaterialPropertyBlock();
            _specialHaloSR.GetPropertyBlock(mpb);
            mpb.SetColor("_Color", haloCol);
            _specialHaloSR.SetPropertyBlock(mpb);
            _specialHaloSR.color = new Color(1f, 1f, 1f, 0.35f);
            _specialPulse = _specialHaloSR.DOFade(0.7f, 0.6f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);

            // Corner pip (top-right of the face), normal blend, LDR version of the colour so it reads on white.
            Color pipCol = new Color(Mathf.Clamp01(col.r * 0.5f), Mathf.Clamp01(col.g * 0.5f), Mathf.Clamp01(col.b * 0.5f), 1f);
            float fx = (_spriteRenderer != null && _spriteRenderer.sprite != null) ? _spriteRenderer.sprite.bounds.extents.x : 0.5f;
            Vector3 corner = new Vector3(fx * 0.62f, fx * 0.62f, -0.11f);
            _specialPipGO.SetActive(true);
            _specialPipGO.transform.localPosition = corner;
            _specialPipGO.transform.localScale = Vector3.one * 0.55f;
            _specialPipSR.color = pipCol;
            _specialPipSR.sprite = kind == RulesEngine.SPECIAL_ROCKET_H ? s_pipBarH
                                 : kind == RulesEngine.SPECIAL_ROCKET_V ? s_pipBarV : s_pipDot;
            _specialPipGO.transform.localRotation = Quaternion.identity;
            var icon = SpecialIcon(kind);
            if (icon != null)
            {
                // Spencer's art as a corner STICKER: half the tile wide, overlapping the top-right edge, letter stays readable.
                _specialPipSR.sprite = icon;
                _specialPipSR.color  = Color.white;
                _specialPipGO.transform.localPosition = new Vector3(fx * 0.58f, fx * 0.58f, -0.11f);
                _specialPipGO.transform.localScale    = Vector3.one * (fx * 2f * SPECIAL_BADGE_SCALE);
                _specialPipGO.transform.localRotation = Quaternion.Euler(0f, 0f, SpecialIconAngle(kind));
                _specialPip2GO.SetActive(false);
                SyncSpecialOrder();
                if (pop) { transform.DOKill(); Vector3 r0 = CanonicalRestScale(); transform.localScale = r0 * 0.6f; transform.DOScale(r0, 0.32f).SetEase(Ease.OutBack, 2.4f); }
                _specialPipGO.transform.DOKill();
                _specialPipGO.transform.DOPunchRotation(new Vector3(0f, 0f, 14f), 0.5f, 6, 0.6f);
                return;
            }
            bool disco = kind == RulesEngine.SPECIAL_DISCO;
            _specialPip2GO.SetActive(disco);
            if (disco)
            {
                _specialPip2GO.transform.localPosition = corner;
                _specialPip2GO.transform.localScale = Vector3.one * 0.62f;
                _specialPip2SR.sprite = s_pipRing; _specialPip2SR.color = pipCol;
            }
            SyncSpecialOrder();
            if (pop)
            {
                transform.DOKill();
                Vector3 rest = CanonicalRestScale();
                transform.localScale = rest * 0.6f;
                transform.DOScale(rest, 0.32f).SetEase(Ease.OutBack, 2.4f);
            }
        }

        private void SyncSpecialOrder()
        {
            if (_specialHaloSR == null) return;
            int face = _spriteRenderer != null ? _spriteRenderer.sortingOrder : BaseSortingOrder;
            _specialHaloSR.sortingOrder = face - 2;   // behind its own face, above the row below's face
            if (_specialPipSR  != null) _specialPipSR.sortingOrder  = face + 2;
            if (_specialPip2SR != null) _specialPip2SR.sortingOrder = face + 2;
        }

        private void SyncFrostOrder()
        {
            if (_frostSR == null) return;
            int face = _spriteRenderer != null ? _spriteRenderer.sortingOrder : BaseSortingOrder;
            _frostSR.sortingOrder = face + 2;
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
                    var v = GetVaultSprite(); if (v != null) FaceSprite = v;
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
                {
                    _spriteRenderer.color = Color.white;
                    // 2026-09-04: ALSO restore the per-letter resting FACE. ResetVisuals is
                    // the "back to normal" path (deselecting a swap calls it), and it only
                    // reset the COLOUR — so a high-value tile that had been given the gold
                    // face came back on the plain tan sprite and looked like it had lost
                    // its gold.
                    if (LETTER_CATEGORY_TINTS && !_hasPrimedGlow && !_isShowingScoredSprite
                        && !IsWild && !IsStone && !IsFrozen)
                        FaceSprite = RestingFaceFor(Letter);
                }
            }
            // 2026-06-01: respect aim-mode sortingOrder boost. When a booster
            // is in aim mode, BoosterHUDSlot bumps every tile above the
            // scrim's order so they stay visible inside the cutout. If
            // ResetVisuals (called from various cleanup paths) hard-coded the
            // default 5, the tile would drop BELOW the scrim mid-aim and
            // disappear. AimModeTileOrder is set by BoosterHUDSlot.
            ReassertSortingOrder();
            Color border = _hasPrimedGlow ? _primedGlowColor : TILE_BORDER_NORMAL;
            ApplyBorderColor(border);
        }

        /// <summary>Tutorial spotlight: raise THIS tile above the dim scrim (order >= 0) or clear it (-1).
        /// Persists through repaints, unlike a bare SetSortingOrder. 2026-07-08 Spencer.</summary>
        public void SetSpotlight(int order)
        {
            // `order` is a LIFT BASE, not a final order. Assigning it verbatim gave every
            // spotlighted tile the SAME sorting order, so stacked tiles sorted arbitrarily —
            // the top-row tiles drawing behind each other during the top-out warning. Third
            // code path with this same bug; the other two were the tutorial board bump and
            // booster aim mode. 2026-09-10 Spencer.
            _spotlightOrder = order;
            ReassertSortingOrder();
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

        public static bool  GEM_WORD_GLOW = true;
        public static Color GEM_GLOW_HDR  = new Color(2.4f, 1.8f, 0.45f, 1f);   // warm gold
        public static float GEM_GLOW_LOW  = 0.35f, GEM_GLOW_HIGH = 0.85f;
        private Tween _gemGlowPulse; private float _gemGlowAlpha;

        public void ClearPrimedGlow()
        {
            if (_gemGlowPulse != null) { _gemGlowPulse.Kill(); _gemGlowPulse = null; ClearBloomGlow(); }
            // Gold-primed is STICKY while primed (see ResolveGoldPrimed) — so it has to be
            // released here, or a recycled tile carries a previous word's gold into its next
            // life. Nothing cleared this flag at all before. 2026-09-10 Spencer.
            _isGoldPrimed = false;

            // PUT THE LETTER BACK. ARMED_LETTER_WHITE whitens the glyph when a tile arms; the
            // only place that restored it lived in SetDetonationColor, which never runs when a
            // word simply falls out of being primed. So an unprimed tile kept a white letter on
            // a tan face. 2026-09-10 Spencer.
            if (_letterWhitened && _letterTMP != null)
            {
                _letterTMP.color = _preArmedLetterColor;
                _letterWhitened  = false;
            }

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
                FaceSprite = _isVault
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
                FaceSprite = _diffuseSprite;
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
                FaceSprite = RestingFaceFor(Letter);
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
                WDLog.Log($"[PreviewClear] ({Col},{Row}) scored={_isShowingScoredSprite} gold={_isGoldBonus} primed={_hasPrimedGlow} wild={_isWild} → restoring {(_isShowingScoredSprite ? "SCORED(green)" : _hasPrimedGlow ? "PRIMED" : _isGoldBonus ? "GOLD" : _isWild ? "WILD" : "NORMAL")}");

            // Swap back to appropriate sprite (canonical priority: scored > primed > gold > wild > normal)
            if (_spriteRenderer != null)
            {
                if (_isShowingScoredSprite)
                {
                    // scored = green tint on the live tile (not a sprite swap). Keep the tint;
                    // do NOT fall through to the color=white reset below. 2026-06-10.
                    FaceSprite = RestingFaceFor(Letter);
                    _spriteRenderer.color  = SCORED_TINT;
                }
                else
                {
                    if (_hasPrimedGlow)
                        FaceSprite = s_spriteGold ?? s_spriteNormal;
                    else if (_isGoldBonus)
                        FaceSprite = s_spriteGolden ?? s_spriteNormal;
                    else if (_isWild)
                        FaceSprite = s_spriteWild ?? s_spriteNormal;
                    else
                        FaceSprite = RestingFaceFor(Letter);
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
//                 WDLog.Log($"[Tile] ({Col},{Row}) '{Letter}' already has primed glow — keeping.");
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

        /// <summary>The authored GOLD tile face (gold_tile2) — what a gold word wears when armed. Used by the
        /// gold letter fly-up so the flying tile is gold, not the green armed face. 2026-10-20.</summary>
        public static Sprite GoldPrimedSprite => s_spritePrimedGold ?? s_spriteHighValue ?? PrimedSprite;

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
        /// <summary>LETTERS SCALE WITH THE TILE (2026-10-23 Spencer, 7x9 board: "what do you think about the size of the letters in
        /// relation to the tiles?"). The glyph is sized in world units and un-scaled against the tile, so on the 7-wide board the
        /// tiles grew 29% and the letters didn't: measured cap height 53px on a 102px tile (52%) at 9 wide -> 53px on 132px (40%)
        /// at 7 wide. Factor = board-width ratio x LETTER_SIZE_MUL; 1.06 puts the cap at ~55% of the tile on either board.</summary>
        public static float LETTER_SIZE_MUL = 0.96f;   // 1.06 (55%) read "too bold and in your face" on the 7x9 tiles — 0.96 ≈ 50% (2026-10-23)
        public static float LetterSizeFactor => (9f / Mathf.Max(1, RulesEngine.COLS)) * LETTER_SIZE_MUL;
        public bool IsShowingScoredSprite => _isShowingScoredSprite;
        /// <summary>STALE-FACE CHECK (2026-10-23 "letters turned into gems" x3): the face sprite isn't the resting face for this
        /// tile's state while nothing (primed glow / scored flash / special / stone / selection) explains it. Returns the
        /// sprite name for the log.</summary>
        /// <summary>Period-key diag: the face's readable name, the resting face it SHOULD show, and the flags that could explain a
        /// difference (2026-10-23 — the gold rows read as gems but could be the swipe-preview or armed-gold faces).</summary>
        public string DebugFaceReport(out bool odd)
        {
            var cur = _spriteRenderer != null ? _spriteRenderer.sprite : null; var rest = RestingFaceFor(Letter);
            odd = cur != rest;
            return $"face={FaceName(cur)} resting={FaceName(rest)} swipePreview={_swipePreview} editSel={_editSelected} primedGlow={_hasPrimedGlow} scoredSprite={_isShowingScoredSprite}";
        }

        public bool FaceIsStale(out string showing)
        {
            showing = _spriteRenderer != null && _spriteRenderer.sprite != null ? _spriteRenderer.sprite.name : "null";
            if (_spriteRenderer == null || _hasPrimedGlow || _isShowingScoredSprite || _isStone || _editSelected || _iconIsTile) return false;
            return _spriteRenderer.sprite != RestingFaceFor(Letter);
        }

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
                FaceSprite = RestingFaceFor(Letter);
                _spriteRenderer.color  = SCORED_TINT;
            }
            else
            {
                // Priority: primed > gold > normal
                if (_hasPrimedGlow)
                    FaceSprite = s_spriteGold ?? s_spriteNormal;
                else if (_isGoldBonus)
                    FaceSprite = s_spriteGolden ?? s_spriteNormal;
                else
                    FaceSprite = RestingFaceFor(Letter);
                _spriteRenderer.color = Color.white;
            }
        }

        /// <summary>THE single "put this tile back how it should look" routine — sprite AND tint,
        /// by state priority: scored &gt; primed &gt; gold &gt; normal.
        ///
        /// 2026-09-11. Written because selecting a GOLD tile and deselecting it turned it WHITE
        /// (Spencer). Two causes, both from assuming "resting == white":
        ///   • SetEditSelected(true) in rim mode set sr.color = white, despite its own comment
        ///     saying "Rim only. Face, shading and letter are left completely alone." A gold tile
        ///     carries its gold as a TINT, so that wiped it on SELECT.
        ///   • SetEditSelected(false) restored a hard-coded white instead of the tile's state.
        /// SetRewriteTargetSprite(false) already had the correct priority logic; it just was not
        /// shared. Anything that ends a temporary visual must call THIS, never assume white.</summary>
        // ── SWIPE FEEDBACK (2026-10-20, Spencer: "whatever you think is best") ──────────────────────
        // While a swipe traces a REAL word, its tiles wear the armed (about-to-pop) face — the "let go now"
        // signal from word games. A swipe released on a non-word shakes "no" instead of just clicking.
        private bool _swipePreview;
        private Color _swipePrevLetterColor;
        public void SetSwipeValidPreview(bool on)
        {
            if (_spriteRenderer == null) return;
            if (on)
            {
                if (_hasPrimedGlow) return;   // already lit — leave it alone
                _swipePreview = true;
                bool gold = IsGoldLetter && s_spritePrimedGold != null;
                FaceSprite = gold ? s_spritePrimedGold : (s_spriteGold ?? s_spriteNormal);
                _spriteRenderer.color  = NeutralisePrimedTint(gold ? ARMED_GOLD_REST : ARMED_ROSE_REST, gold);
                // White letters, same as an armed word (ARMED_LETTER_WHITE). 2026-10-20 Spencer: "when you swipe to
                // create a word can the text also turn white?" Restored on release — BEFORE the word arms, so the
                // armed path saves the real ink, not this preview white.
                if (_letterTMP != null && !_isWild) { _swipePrevLetterColor = _letterTMP.color; _letterTMP.color = Color.white; }
            }
            else if (_swipePreview)
            {
                _swipePreview = false;
                RestoreRestingFaceAndTint();
                if (_letterTMP != null && !_isWild) _letterTMP.color = _swipePrevLetterColor;
            }
        }

        public void ShakeNo()
        {
            MarkAnimating(0.32f);   // keep the position checkers off it while it shakes
            transform.DOPunchPosition(new Vector3(0.07f, 0f, 0f), 0.28f, 14, 0.6f);
        }

        private void RestoreRestingFaceAndTint()
        {
            if (_spriteRenderer == null) return;
            if (_isShowingScoredSprite)
            {
                FaceSprite = RestingFaceFor(Letter);   // scored = green TINT, not a swap
                _spriteRenderer.color  = SCORED_TINT;
                return;
            }
            if (_hasPrimedGlow)
            {
                // Gold-primed and rose-primed use different sprites AND different rest colours.
                // _isGoldPrimed is the sticky resolved flag (see ResolveGoldPrimed).
                bool goldPrimed = _isGoldPrimed;
                FaceSprite = (goldPrimed && s_spritePrimedGold != null)
                    ? s_spritePrimedGold : (s_spriteGold ?? s_spriteNormal);
                _spriteRenderer.color  = NeutralisePrimedTint(goldPrimed ? ARMED_GOLD_REST : ARMED_ROSE_REST, goldPrimed);
                return;
            }
            if (_isGoldBonus)
            {
                FaceSprite = s_spriteGolden ?? s_spriteNormal;
                _spriteRenderer.color  = GOLD_HIGH;                // gold is a TINT here — do not white it
                return;
            }
            if (_isStone)   { _spriteRenderer.color = STONE_TINT; return; }
            FaceSprite = RestingFaceFor(Letter);
            _spriteRenderer.color  = _baseTint;
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
                FaceSprite = s_spriteCyan ?? s_spriteNormal;
                _spriteRenderer.color  = Color.white; // clear any scored green tint while showing cyan
            }
            else
            {
                RestoreRestingFaceAndTint();   // single owner — see RestoreRestingFaceAndTint
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
        public void SetEditSelected(bool active, bool popOnExit = false, float liftScale = -1f)
        {
            // Crates / stones are never interactive (2026-10-22 Spencer: "when i click on a wooden box it tints it dark").
            // A press started a 1-tile swipe trace that selected it; the release restored the old grey STONE tint over the
            // crate art. Refusing here covers every path that selects a tile.
            if (active && _isStone) return;
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

                // MATCH-3 SELECT (2026-10-20 Spencer: "we want to be as similar to a match 3 game as possible").
                // The piece itself reacts — it lifts and gently breathes — with NO line round it and no glow.
                // Candy Crush / Royal Match style; the outline frame is the older Bejeweled look.
                if (SELECT_LIFT)
                {
                    // 2026-10-20 Spencer: "when it scales up there isnt enough padding between letters". Tiles sit
                    // close together, so a 12% lift on neighbours (a swiped word) made them collide. Swipe traces
                    // pass a small liftScale; a single tap-selected tile lifts a bit more AND draws above its
                    // neighbours so any overlap reads as "on top", not as a pile-up.
                    float s = liftScale > 0f ? liftScale : SELECT_LIFT_SCALE;
                    transform.DOKill();
                    if (liftScale <= 0f) SetSortingOrder(BaseSortingOrder + ROW_ORDER_STRIDE * 2);   // over the row above too
                    Vector3 lifted = _editBaseScale * s;
                    float breathe = Mathf.Min(0.03f, (s - 1f) * 0.5f);
                    transform.DOScale(lifted, 0.12f).SetEase(Ease.OutBack, 2.2f).OnComplete(() =>
                    {
                        if (this == null || !_editSelected || breathe <= 0.001f) return;
                        transform.DOScale(_editBaseScale * (s - breathe), 0.55f)
                            .SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo);
                    });
                    return;
                }

                // 2026-06-03 Spencer: selection reads as an HDR cyan glow on the tile
                // itself (glow-only, like the hint) — NO cyan sprite swap, NO halo.
                // Snap quickly to the peak saturated/glowing cyan ("accessing" feel),
                // THEN breathe between that peak and a dimmer cyan so it stays lit.
                if (EDIT_SELECT_RIM)
                {
                    // Rim only. Face, shading and letter are left completely alone.
                    // 2026-09-11: do NOT reset the tint here. The comment above says the face is
                    // left alone, and a gold/scored tile carries its state AS a tint — whiting it
                    // was what turned gold tiles white on select.
                    if (_spriteRenderer != null) _spriteRenderer.DOKill();
                    ShowSelectRim(true);
                }
                else if (_spriteRenderer != null)
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
                if (EDIT_SELECT_RIM) return;   // rim carries it — no overlay to blow out
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
                if (SELECT_LIFT && wasSelected) ReassertSortingOrder();   // undo the tap-select draw-on-top
                // Kill the glint pulse + restore white only when we were actually
                // selected, so we don't stomp a special-tile tint on a stray cleanup
                // call. (ResetVisuals also covers this, but keep it self-contained.)
                if (wasSelected && _spriteRenderer != null)
                {
                    _spriteRenderer.DOKill();
                    RestoreRestingFaceAndTint();   // NOT white — see RestoreRestingFaceAndTint
                }
                // Kill the pulsing bloom overlay and hide it. Always (even if we thought
                // we were already deselected) so an interrupted toggle can't strand a lit
                // glow. 2026-07-09 Spencer.
                if (EDIT_SELECT_RIM && wasSelected) ShowSelectRim(false);
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
            // Committed swaps clear the selection HERE, never through SetEditSelected(false) — so the tap-select draw-on-top
            // (+2 row bands) was never undone. BoardInvariants logged ~300 "a lift outlived its effect", mostly right after
            // a swap that formed no word. 2026-10-21.
            if (SELECT_LIFT && _editSelected) ReassertSortingOrder();
            _editSelected = false;
            // The rim is NOT part of the glow fade — it is a separate overlay, so it has to be
            // switched off explicitly here. This is the path a committed SWAP takes (the swap
            // never calls SetEditSelected(false)), which is why outlines were staying on both
            // exchanged tiles. Clearing it here rather than relying on the board sync, because
            // not every swap branch reaches a sync. 2026-09-09 Spencer.
            ShowSelectRim(false);
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
            // Match-3 select (SELECT_LIFT): swapping pieces carry no outline/tint — Candy Crush style.
            // 2026-10-20 Spencer: "i think we follow the way candy crush is for this".
            if (SELECT_LIFT) return;
            if (EDIT_SELECT_RIM)
            {
                // Rim mode: hold the outline through the exchange instead of washing the face.
                // Note this does NOT set _editSelected — the swap is committing, not selecting —
                // so FadeEditGlowOut below is what ends it. 2026-09-09.
                // 2026-09-10 Spencer: face tint tried and REMOVED — he did not like the cyan
                // wash during an exchange. The rim alone marks the two tiles. Set the colour to
                // EDIT_SWAP_FACE here to bring it back.
                if (_spriteRenderer != null) { _spriteRenderer.DOKill(); _spriteRenderer.color = Color.white; }
                ShowSelectRim(true);
                return;
            }
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
                FaceSprite = RestingFaceFor(Letter);
                _spriteRenderer.color  = Color.white;
            }
        }

        /// <summary>2026-06-04 Spencer: the tile's canonical cell-derived rest scale.
        /// Prefers the value captured at setup; falls back to a fresh cell computation
        /// if setup hasn't run yet. NEVER reads the live transform (which may be mid
        /// animation) so edit/diffuse pops can't compound the tile bigger.</summary>
        public Vector3 CanonicalRestScalePublic => CanonicalRestScale();   // HandManager pre-burst swell (2026-10-22)
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
        private static bool s_loggedTileMetrics;

        /// <summary>2026-09-03: tiles OVERLAP vertically, so an upper tile must cover a
        /// lower tile's top face — including that tile's GLOW and letter. Unity's
        /// sortingOrder ALWAYS beats Z depth, so a lower tile's glow at order+1 was
        /// drawing over an upper tile's face at order+0. Give each ROW its own band of
        /// 3 orders (face, sub-layers, letter) so the whole stack sorts by row.
        /// Bands run 5 .. 5+3*(rows-1); FX/aim orders sit above that (see FX_TILE_ORDER).</summary>
        public const int ROW_ORDER_STRIDE = 3;
        public const int ROW_ORDER_BASE   = 5;
        /// <summary>Order for a tile lifted by FX/detonation — above every row band.</summary>
        public const int FX_TILE_ORDER    = 60;
        public int BaseSortingOrder => ROW_ORDER_BASE + Mathf.Max(Row, 0) * ROW_ORDER_STRIDE;


        /// <summary>2026-09-03: the sprite cache lives in statics behind
        /// s_spriteCacheBuilt. With Editor domain reload off those statics survive
        /// between play sessions, so a sprite built from an OLDER version of the
        /// texture kept being reused (and the tile came out the wrong shape). Reset
        /// on every play so the cache always matches the art on disk.</summary>
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSpriteCache()
        {
            s_spriteCacheBuilt = false;
            s_loggedTileMetrics = false;
        }

        // ── LETTER-CATEGORY RESTING FACES (2026-09-03 Spencer) ───────────────────────
        // THREE resting categories, not 26. Per-LETTER value tinting was removed on
        // 2026-06-24 because it was "colour the player has to decode"; three categories
        // are learnable at a glance and carry real information now the game is
        // score-based: vowels are what you scan for, high-value letters are opportunity.
        // Transient STATE colours (primed magenta / scored green / selected cyan) still
        // override the resting face — this only changes what a tile looks like at rest.
        /// <summary>Gold (high-value) letters cannot be picked up for a swap. 2026-09-21 Spencer.
        /// They read as a reward to spend, not a piece to shuffle — and allowing selection also
        /// exposed a bug where cancelling the selection restored the plain face.</summary>
        public static bool GOLD_LETTERS_LOCKED = true;

        /// <summary>True when this tile is showing the high-value GOLD resting face.
        /// Mirrors the RestingFaceForRaw rule so the two cannot drift apart.</summary>
        public bool IsHighValueLetter =>
            LETTER_CATEGORY_TINTS && s_spriteHighValue != null
            && LetterData.GetPoints(Letter) >= HIGH_VALUE_POINTS
            && !_isStone && !_isGoldBonus && !_isVault;

        public static bool LETTER_CATEGORY_TINTS = true;
        public static bool VOWEL_TINT            = false;  // OFF — blue vowels read confusing
        // 2026-09-03: was 4 — but this game's values are NOT Scrabble's. At 4+ the set is
        // C F M U W G P Y B V K J X Q Z = 15 of 26 letters, so most of the board went gold
        // and nearly every word primed gold. At 7+ it is V K J X Q Z — genuinely rare, so
        // gold reads as an opportunity instead of as noise.
        public static int  HIGH_VALUE_POINTS     = 99;  // 2026-10-22 Spencer: K/J/V/X/Q/Z back to NORMAL default tiles — no letter reaches 99, so none is gold, locked or gives a move. Was 7 (>= this many points reads as "high value").

        private static Sprite s_spritePrimedGold; // ARMED + high-value: the hot gold face
        private static Sprite s_spriteVowel;      // blue
        private static Sprite s_spriteHighValue;  // gold

        private static bool IsVowelLetter(char c)
        {
            char u = char.ToUpper(c);
            return u=='A' || u=='E' || u=='I' || u=='O' || u=='U';
        }

        /// <summary>The tile's RESTING face for its letter: vowel / high-value / normal.</summary>
        /// <summary>Re-derive the resting face from the CURRENT letter. Safe to call any time —
        /// it is a no-op unless the tile is in its plain resting state, so it can never stomp a
        /// primed, scored, gold-bonus, wild, stone or frozen face.
        ///
        /// Exists because SetLetter only repaints the face while the tile is already resting. A
        /// letter that arrives while any of those flags is up (mid-cascade, mid-flash) never gets
        /// its face corrected when the flag clears, which is how a high-value letter ends up
        /// sitting on the board as a plain tan tile. 2026-09-08 Spencer.</summary>
        private float _faceWrongSince = -1f;
        /// <summary>RefreshRestingFace, but only once the face has been wrong for <paramref name="persist"/> seconds in a row.</summary>
        public void RefreshRestingFaceIfStuck(float persist)
        {
            if (_spriteRenderer == null) return;
            // ARMED IN THE RULES = leave it (2026-10-23 Spencer: "a word fell and made another green word but there was a delay ...
            // something just looked off" — READ lit green, FaceFix flipped it to white, THEN it popped). A word armed by a fall can
            // carry the armed face without the glow flag; the registry is the truth, so never "fix" a tile that's in an armed word.
            var reg = RulesEngine.Instance != null ? RulesEngine.Instance.PrimedRegistry : null;
            bool armedInRules = reg != null && Col >= 0 && Row >= 0 && reg.GetPrimedWordsContaining(new Vector2Int(Col, Row)).Count > 0;
            Sprite want = (_hasPrimedGlow || _isShowingScoredSprite || IsStone || IsFrozen || IsWild || armedInRules) ? null : RestingFaceFor(Letter);
            // COLOUR counts too (2026-10-22: the top rows went gold after a cascade with the RIGHT face — a leftover warm
            // armed tint (1.3,1.25,1.1) on a plain tile; the face-only check never saw it).
            bool colourWrong = want != null && _spriteRenderer.sprite == want && !_editSelected && !_swipePreview && _flashCoroutine == null
                               && !_iconIsTile && !_isCrateArt && !ColorsClose(_spriteRenderer.color, _baseTint);
            if (want == null || (_spriteRenderer.sprite == want && !colourWrong)) { _faceWrongSince = -1f; return; }
            if (_faceWrongSince < 0f) { _faceWrongSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - _faceWrongSince < persist) return;
            _faceWrongSince = -1f;
            if (colourWrong)
            {
                WDLog.Log($"[FaceFix] ({Col},{Row}) '{Letter}' stuck COLOUR {_spriteRenderer.color} (face {FaceName(_spriteRenderer.sprite)}) -> {_baseTint}");
                ApplyRestingColor();
                return;
            }
            RefreshRestingFace();
        }

        public void RefreshRestingFace()
        {
            if (!LETTER_CATEGORY_TINTS || _spriteRenderer == null) return;
            BuildSpriteCache();   // no-op once built; guarantees s_spriteHighValue exists
            if (_hasPrimedGlow || _isShowingScoredSprite || _isGoldBonus
                || IsWild || IsStone || IsFrozen) return;
            if (OwnerIndex == 1 && s_spriteAI != null) return;
            Sprite want = RestingFaceFor(Letter);
            if (want != null && _spriteRenderer.sprite != want)
            {
                WDLog.Log($"[FaceFix] ({Col},{Row}) '{Letter}' was showing {FaceName(_spriteRenderer.sprite)} -> {FaceName(want)} " +
                          $"(color {_spriteRenderer.color}, special {SpecialKind}, rainGold {IsRainGold}, selected {_editSelected})");
                FaceSprite = want;
                ApplyRestingColor();
            }
        }
        /// <summary>Is this tile wearing a GOLD face (high-value or gold-primed) without being a gold letter?</summary>
        public bool ShowsGoldFaceWrongly =>
            _spriteRenderer != null && LetterData.GetPoints(Letter) < HIGH_VALUE_POINTS && !_iconIsTile   // includes shower-gold (capped at 2 — 3+ is itself a bug)
            && _spriteRenderer.sprite != s_spriteNormal   // some gold slots fall back to the NORMAL face — that is not gold (2026-10-22 false alarm: 96 tiles)
            && (_spriteRenderer.sprite == s_spriteHighValue || _spriteRenderer.sprite == GoldPrimedSprite || _spriteRenderer.sprite == s_spriteGolden);
        /// <summary>Everything that can change a tile's face, in one line — for the [GoldRows] tripwire.</summary>
        public string DebugFaceState() =>
            $"({Col},{Row}) '{Letter}' face={FaceName(_spriteRenderer != null ? _spriteRenderer.sprite : null)} col={(_spriteRenderer != null ? _spriteRenderer.color.ToString() : "-")} " +
            $"primedGlow={_hasPrimedGlow} goldPrimed={_isGoldPrimed} scored={_isShowingScoredSprite} swipePrev={_swipePreview} selected={_editSelected} " +
            $"anim={IsAnimating} flash={(_flashCoroutine != null)} goldBonus={_isGoldBonus} rainGold={IsRainGold} special={SpecialKind}";

        /// <summary>BLANK TILE CHECK (2026-10-22 Spencer: "certain letter tiles are showing up with no letters on them as blanks,
        /// usually during cascades and explosions"). Why a settled letter tile would show no letter, or null if it's fine.</summary>
        public string LetterProblem()
        {
            if (_letterTMP == null || _isStone || _isVault || IsWild || _bonusLetterHidden) return null;   // bonus rockets: hidden on purpose
            if (!_letterTMP.gameObject.activeSelf) return "letter object inactive";
            if (!_letterTMP.enabled) return "letter component disabled";
            if (string.IsNullOrEmpty(_letterTMP.text)) return "letter text empty";
            if (_letterTMP.alpha < 0.5f) return $"letter alpha {_letterTMP.alpha:F2}";
            if (_letterTMP.color.a < 0.5f) return $"letter colour alpha {_letterTMP.color.a:F2}";
            var m = _letterTMP.fontMaterial;
            if (m != null && m.HasProperty("_StencilComp") && Mathf.RoundToInt(m.GetFloat("_StencilComp")) != (int)UnityEngine.Rendering.CompareFunction.Always
                && m.GetFloat("_StencilComp") != 0f) return $"letter stuck CLIPPED (stencil comp {m.GetFloat("_StencilComp")})";
            if (_letterTMP.transform.localScale.x < 0.01f) return "letter scale ~0";
            return null;
        }
        /// <summary>Put the letter back: active, enabled, text, opaque, unclipped.</summary>
        public void FixLetter()
        {
            if (_letterTMP == null || _bonusLetterHidden) return;
            _letterTMP.gameObject.SetActive(true); _letterTMP.enabled = true;
            if (string.IsNullOrEmpty(_letterTMP.text)) UpdateLetterDisplay(Letter);
            _letterTMP.alpha = 1f; var c = _letterTMP.color; c.a = 1f; _letterTMP.color = c;
            var m = _letterTMP.fontMaterial;
            if (m != null && m.HasProperty("_StencilComp")) { m.SetFloat("_Stencil", 0f); m.SetFloat("_StencilComp", (float)UnityEngine.Rendering.CompareFunction.Always); }
            if (_letterTMP.transform.localScale.x < 0.01f) _letterTMP.transform.localScale = _letterPlateGO != null && _letterPlateGO.activeSelf ? _letterPlateScale : _letterHomeScale.sqrMagnitude > 0f ? _letterHomeScale : Vector3.one;
        }

        /// <summary>Readable name for the procedural face sprites (they have no asset names), for the [FaceFix] log.</summary>
        public static string FaceName(Sprite s)
        {
            if (s == null) return "null";
            if (s == s_spriteNormal) return "NORMAL";
            if (IsCarvedGemFace(s)) return "GEM-CARVED";
            if (s == s_spriteHighValue) return "GOLD(high-value)";
            if (s == GoldPrimedSprite) return "GOLD-PRIMED";
            if (s == s_spriteGolden) return "GOLDEN-BONUS";
            if (s == s_spriteVowel) return "VOWEL";
            if (s == PrimedSprite) return "PRIMED";
            return string.IsNullOrEmpty(s.name) ? "unnamed" : s.name;
        }

        /// <summary>The colour an ARMED tile settles at. Single definition so the scored
        /// flash can fade exactly onto it — any mismatch shows up as a visible step at the
        /// hand-off. Mirrors the pulse loop's maths with pulse = 0. 2026-09-08.</summary>
        /// <summary>Strip the hue out of a primed tint, keeping its strength — so an AUTHORED
        /// primed face LIGHTS UP instead of being recoloured. Sprite colour MULTIPLIES the
        /// texture, so PRIMED_GLOW's magenta (1.8, 0.50, 1.04) halves the green channel of a
        /// green tile and leaves a magenta fringe. Gold is exempt: its colour IS the reward cue.
        ///
        /// Call this from EVERY path that tints an armed tile. 2026-09-21 — the first fix only
        /// covered the steady glow, so the CASCADE scored-flash (WordDropFX) still went dark
        /// green: Spencer, "it is still glowing dark green ... during the scored flash".</summary>
        public static Color NeutralisePrimedTint(Color c, bool isGold)
        {
            if (isGold)
            {
                // Gold used to be exempt because its colour WAS the reward cue. With authored
                // gold-primed art the sprite carries that cue, so the tint must go neutral too
                // or PRIMED_GOLD_GLOW multiplies the gold art toward orange. 2026-09-21.
                if (!GOLD_PRIMED_TINT_NEUTRAL || string.IsNullOrEmpty(GOLD_PRIMED_FACE_ASSET)) return c;
            }
            else if (!PRIMED_TINT_NEUTRAL || string.IsNullOrEmpty(PRIMED_FACE_ASSET)) return c;
            float lum = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            return new Color(lum, lum, lum, c.a);
        }

        public static Color ArmedRestColor(Color hdrGlow)
        {
            // 2026-09-09 Spencer: "its not really matching the colours of your preview."
            // The armed SPRITE (primed_tile@2x) already carries the authored colour, and this
            // tint was multiplying a magenta over it — rose sprite x magenta tint = a hotter,
            // more saturated pink than the art. The tint dates from when the sprite was too
            // weak to read as primed on its own. With the static armed face it is redundant,
            // so leave the sprite alone and the game matches the render exactly.
            if (!PRIMED_ANIMATION)
                // 2026-09-10 Spencer: the scored flash on a gold tile mid-cascade is the colour
                // he wants gold-armed tiles to REST at. That flash sets sr.color to
                // PRIMED_GOLD_GLOW (2.0, 1.54, 0.64) over this same sprite; at rest the colour
                // was plain white, so the sprite showed untinted. Same multiply, scaled into
                // LDR so it survives the Color32 clamp on device.
                // NOT reproduced: the flash also fires an additive bloom overlay. That is the
                // armed glow he had removed until detonation, so this matches the hue and
                // richness but not the glow.
            {
                if (ColorsClose(hdrGlow, PRIMED_GOLD_GLOW)) return ARMED_GOLD_REST;
                return ARMED_ROSE_REST;   // same treatment for the rose tiles
            }
            Color pc = Color.Lerp(Color.white, hdrGlow, 0.58f);
            float m  = Mathf.Max(pc.r, Mathf.Max(pc.g, pc.b));
            if (m > PRIMED_LDR_CAP) { float k = PRIMED_LDR_CAP / m; pc.r *= k; pc.g *= k; pc.b *= k; }
            pc.a = 1f;
            return pc;
        }

        /// <summary>Put the ARMED face on right now, before the word has formally primed, so the
        /// scored flash can light straight up in magenta/gold instead of passing through the tan
        /// resting face. 2026-09-08 Spencer.</summary>
        /// <summary>TRUE while we cannot yet know whether this tile's word is gold — the word
        /// has not reached the primed registry and the tile's own letter is not high-value.
        ///
        /// 🚨 Painting during this window is what made a gold word flash GREEN then snap to gold
        /// (Spencer 2026-09-21, "on a regular swap when i prime a gold word it is turning green
        /// and then gold"). For JAR the J answers gold from its own letter, but A and R answer
        /// GREEN — they have no way to know about the J until the word is registered. Holding the
        /// current face for those few frames removes the flash; the registry answers immediately
        /// after and the correct armed face goes on once.</summary>
        private bool GoldAnswerPending =>
            !_isGoldPrimed && !IsGoldLetter
            && !RulesEngine.PrimedWordRegisteredForCell(new Vector2Int(Col, Row));

        public void ApplyArmedFaceNow(bool isGold)
        {
            if (_spriteRenderer == null) return;
            BuildSpriteCache();
            // The WORD decides, so every tile in it wears the same face. See SetPrimedGlow.
            _isGoldPrimed = ResolveGoldPrimed(isGold);
            // Do not paint a guess — see GoldAnswerPending.
            if (!isGold && GoldAnswerPending)
            {
                if (ARMED_FACE_DIAG) WDLog.Log($"[ArmedFace] '{Letter}' ({Col},{Row}) HOLD — gold answer pending");
                return;
            }
            Sprite armed = ArmedFace();   // single chokepoint
            if (ARMED_FACE_DIAG)
                WDLog.Log($"[ArmedFace] '{Letter}' ({Col},{Row}) pts={LetterData.GetPoints(Letter)} " +
                          $"isGold(arg)={isGold} _isGoldPrimed={_isGoldPrimed} " +
                          $"registered={RulesEngine.PrimedWordRegisteredForCell(new Vector2Int(Col, Row))} " +
                          $"-> {(armed == s_spritePrimedGold ? "GOLD" : armed == s_spriteGoldThick ? "GREEN(primed)" : "other")}");
            if (armed != null) FaceSprite = armed;
        }

        /// <summary>Choose this tile's resting face, GUARANTEEING the sprite cache exists first.
        ///
        /// The chooser below is static, so it cannot build the cache (BuildSpriteCache is an
        /// instance method — it sizes textures from the tile's own _cellSize). Any of the ~20
        /// call sites that ran before some other tile happened to build it got the plain face
        /// back for a gold letter, silently. That is why J/V/X started tan and only turned gold
        /// later, when a board sync ran RefreshRestingFace and corrected them.
        ///
        /// Making the instance overload the thing every site calls means the cache is built on
        /// demand at all of them, instead of each one having to remember. 2026-09-10 Spencer.</summary>
        private Sprite RestingFaceFor(char letter)
        {
            BuildSpriteCache();   // no-op once built
            if (_iconIsTile && SpecialKind != RulesEngine.SPECIAL_NONE) { var st = SpecialTileSprite(SpecialKind); if (st != null) return st; }
            if (IsGem && !_isStone) { var gf = GemFaceFor(letter); if (gf != null) return gf; }   // gem tile (carved per letter)
            if (CollectUsesBlueTile && CollectLetter != '\0' && char.ToUpperInvariant(letter) == CollectLetter && !_isStone && !IsRainGold
                && LetterData.GetPoints(letter) < HIGH_VALUE_POINTS) return CollectBlueFace;   // collect letter = blue ramp tile
            if (IsRainGold && LETTER_CATEGORY_TINTS && s_spriteHighValue != null && !_isStone && !_isGoldBonus && !_isVault)
                return s_spriteHighValue;   // shower gold letter wears the K/Q gold face
            return RestingFaceForRaw(letter);
        }

        private static Sprite RestingFaceForRaw(char letter)
        {
            // NOTE: cannot build the sprite cache from here — BuildSpriteCache() is an
            // INSTANCE method (it sizes the textures from the tile's own _cellSize). So this
            // can still answer from an unbuilt cache and silently return the plain face.
            // RefreshRestingFace() below covers that: it builds the cache first and runs on
            // every board sync. 2026-09-08 Spencer.
            if (!LETTER_CATEGORY_TINTS) return s_spriteNormal;
            // 2026-09-03 Spencer: VOWEL tinting is OFF — blue vowels read as confusing
            // (too close to the ice tile, and vowels are common enough that half the
            // board turned blue). High-value gold stays: it is rare, so it reads as an
            // opportunity rather than as noise. Flip VOWEL_TINT back on to retry.
            if (VOWEL_TINT && IsVowelLetter(letter) && s_spriteVowel != null) return s_spriteVowel;
            if (LetterData.GetPoints(letter) >= HIGH_VALUE_POINTS && s_spriteHighValue != null)
                return s_spriteHighValue;
            return s_spriteNormal;
        }

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

        /// <summary>Return a hue-rotated, saturation-lifted copy of a texture, luminance
        /// untouched. Goes via a RenderTexture blit because Resources textures are not
        /// readable — same pattern TileSpriteAtlas already uses. 2026-09-10.</summary>
        private static Texture2D WarmedCopy(Texture2D src) => HueShiftCopy(src, TILE_HUE_SHIFT, TILE_SAT_MUL);

        /// <summary>Hue-ROTATE and saturation-MULTIPLY a face texture, leaving V alone. The
        /// multiply is the point — it preserves the art's relative saturation structure, which
        /// is what makes a recolour read as a material rather than a flat fill.</summary>
        /// <summary>Pull a pixel toward the world palette WITHOUT transiting the colour wheel.
        ///
        /// 🚨 WHY NOT JUST ROTATE THE HUE: the first version did, and Spencer immediately caught
        /// it — "the steel containers are looking green". It is arithmetic, not taste. Steel sits
        /// at hue 196 and the anchor is 42, so the SHORT path between them runs straight through
        /// GREEN at 120. At strength 0.45 the steel landed on ~127 — greener than the stone it
        /// was supposed to match. Any partial rotation from blue toward warm MUST pass through
        /// green. Rotating hue is simply the wrong tool for a near-neutral material.
        ///
        /// INSTEAD: build the pixel's target — same VALUE, hue AT the anchor, and saturation cut
        /// in proportion to how far round the wheel it had to come (`dist`) — then LERP the
        /// ORIGINAL toward that target in RGB. A blue lerping to a warm grey passes through
        /// GREY, never through green. Distant hues (steel) arrive desaturated and read as
        /// neutral metal with a warm cast; hues already near the anchor (wood) keep their colour,
        /// because their `dist` is small. Strength stays a smooth 0..1 dial.</summary>
        private static Texture2D WorldTintCopy(Texture2D src, float anchorDeg, float strength, float desat)
        {
            if (src == null || strength <= 0.001f) return src;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            float anchor = Mathf.Repeat(anchorDeg, 360f) / 360f;
            float k = Mathf.Clamp01(strength);
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                if (sv <= 0.02f) continue;                      // true greys need no help
                float d    = Mathf.Repeat(anchor - h + 0.5f, 1f) - 0.5f;
                float dist = Mathf.Clamp01(Mathf.Abs(d) * 2f);   // 0 at the anchor, 1 opposite
                Color tgt  = Color.HSVToRGB(anchor, Mathf.Clamp01(sv * (1f - dist * desat)), v);
                px[i] = new Color(Mathf.Lerp(c.r, tgt.r, k),
                                  Mathf.Lerp(c.g, tgt.g, k),
                                  Mathf.Lerp(c.b, tgt.b, k), c.a);
            }
            tex.SetPixels(px);
            tex.Apply(false);
            return tex;
        }

        private static Texture2D HueShiftCopy(Texture2D src, float shiftDeg, float satMul)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            var px = tex.GetPixels();
            float shift = shiftDeg / 360f;
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                if (sv <= 0.02f) continue;                 // leave true greys alone
                h = Mathf.Repeat(h + shift, 1f);
                sv = Mathf.Clamp01(sv * satMul);
                // Contrast curve FIRST (deepens shading, white stays white), then any lift.
                float vv2 = Mathf.Pow(Mathf.Clamp01(v), Mathf.Max(0.05f, TILE_SHADE_GAMMA));
                Color o = Color.HSVToRGB(h, sv, Mathf.Clamp01(vv2 * TILE_VAL_MUL));
                o.a = c.a;
                px[i] = o;
            }
            tex.SetPixels(px);
            tex.Apply(false);
            tex.filterMode = src.filterMode;
            tex.wrapMode   = TextureWrapMode.Clamp;
            WDLog.Log($"[HueShift] face hue {shiftDeg:+0;-0} deg, sat x{satMul:0.00}");
            return tex;
        }

        /// <summary>Expand value contrast around the texture's own mean and restore saturation.
        /// Contrast around the MEAN (not around 0.5) so it works whatever the lift left behind.</summary>
        private static Texture2D SurfaceCopy(Texture2D src, float contrast, float satMul)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);

            var px = tex.GetPixels();
            double sum = 0; int n = 0;
            for (int i = 0; i < px.Length; i++)
                if (px[i].a > 0.004f) { sum += Mathf.Max(px[i].r, Mathf.Max(px[i].g, px[i].b)); n++; }
            float mean = n > 0 ? (float)(sum / n) : 0.5f;

            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                v  = Mathf.Clamp01(mean + (v - mean) * contrast);
                sv = Mathf.Clamp01(sv * satMul);
                h  = Mathf.Repeat(h + ROCK_HUE_ROT / 360f, 1f);   // sage -> warm stone
                Color o = Color.HSVToRGB(h, sv, v);
                o.a = c.a; px[i] = o;
            }
            tex.SetPixels(px); tex.Apply(false);
            tex.filterMode = src.filterMode; tex.wrapMode = TextureWrapMode.Clamp;
            WDLog.Log($"[Rock] surface pass — mean V {mean:0.00}, contrast x{contrast:0.00}, sat x{satMul:0.00}");
            return tex;
        }

        /// <summary>Gamma-lift a texture's value, leaving hue and saturation alone. v^(1/g):
        /// 0 stays 0, 1 stays 1, everything between rises — so nothing clips. 2026-09-11.</summary>
        private static Texture2D LiftCopy(Texture2D src, float gamma)
        {
            if (src == null || gamma <= 1.001f) return src;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            var px = tex.GetPixels();
            float inv = 1f / gamma;
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                Color o = Color.HSVToRGB(h, sv, Mathf.Pow(Mathf.Clamp01(v), inv));
                o.a = c.a; px[i] = o;
            }
            tex.SetPixels(px); tex.Apply(false);
            tex.filterMode = src.filterMode; tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

        /// <summary>Bake a soft dark contact rim just inside the sprite's silhouette, so adjacent
        /// tiles separate instead of fusing into one slab. Blurs the ALPHA channel to get an
        /// inside-ness mask, then multiplies the colour down where that mask drops below 1.
        /// The bottom edge is darkened harder (TILE_AO_BOTTOM) because the board is lit from
        /// above — that is what puts a value STEP at every horizontal seam. 2026-09-10.</summary>
        /// <summary>Bake the contact edge into any tile FACE texture. Every face state needs it —
        /// if only the resting tile had AO, the separation would POP OFF the instant a word armed
        /// and the tile swapped to the primed/gold art. 2026-09-10 Spencer spotted this.</summary>
        private static Texture2D AoFace(Texture2D t, bool applySpec = false)
        {
            if (!TILE_AO || t == null) return t;
            var ao = ContactEdgeCopy(t, applySpec);
            return ao != null ? ao : t;
        }

        /// <summary>Set a face texture to an ABSOLUTE hue (not a shift), scaling saturation and
        /// leaving V alone so the bevel survives. Absolute because a relative shift depends on
        /// the source art — which is exactly how the earlier warm-tile guess went wrong.</summary>
        private static Texture2D HiValFace(Texture2D src)
        {
            if (!HIVAL_RECOLOR || src == null) return src;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            float th = Mathf.Repeat(HIVAL_HUE, 360f) / 360f;
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                if (sv <= 0.02f) continue;                  // leave true greys
                Color o = Color.HSVToRGB(th, Mathf.Clamp01(sv * HIVAL_SAT_MUL), v);
                o.a = c.a;
                px[i] = o;
            }
            tex.SetPixels(px); tex.Apply(false);
            tex.filterMode = src.filterMode; tex.wrapMode = TextureWrapMode.Clamp;
            WDLog.Log($"[HiVal] high-value face -> hue {HIVAL_HUE:0} sat x{HIVAL_SAT_MUL:0.00}");
            return tex;
        }

        // ── ARMED-GREEN LIFT (2026-10-22 Spencer: "the green of the tiles might be a little too dark ...
        // brighten them up just a touch"). Lifts VALUE toward white by this fraction (HSV, hue kept) and trims
        // saturation by a third of it, so the bevel's light→dark spread stays and nothing turns minty.
        // green_primed bottom V 0.68 → ~0.75 at 0.22; the top (V 0.92) barely moves. 0 = the art as authored.
        public static float PRIMED_FACE_LIFT = 0f;   // 2026-10-28: 0 - the green is now on the RK clover ramp; the .22 lift (10-22) washed it toward lime
        private static Texture2D LiftedFace(Texture2D src, float lift)
        {
            if (src == null || lift <= 0.001f) return src;
            var tex = ReadableCopy(src);
            if (tex == null) return src;
            var px = tex.GetPixels();
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.a <= 0.004f) continue;
                float h, sv, v;
                Color.RGBToHSV(c, out h, out sv, out v);
                Color o = Color.HSVToRGB(h, Mathf.Clamp01(sv * (1f - lift / 3f)), v + (1f - v) * lift);
                o.a = c.a;
                px[i] = o;
            }
            tex.SetPixels(px); tex.Apply(false);
            tex.filterMode = src.filterMode; tex.wrapMode = TextureWrapMode.Clamp;
            WDLog.Log($"[FaceLift] {src.name} lift {lift:0.00}");
            return tex;
        }

        private static Texture2D s_specMap;

        /// <summary>Resources textures are not CPU-readable; go via a RenderTexture blit.</summary>
        private static Texture2D ReadableCopy(Texture2D src)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active; RenderTexture.active = rt;
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0); t.Apply(false);
            RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
            return t;
        }

        private static bool s_aoLogged;

        private static Texture2D ContactEdgeCopy(Texture2D src, bool applySpec = false)
        {
            if (src == null) return null;
            RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32);
            Graphics.Blit(src, rt);
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);

            int w = tex.width, h = tex.height;
            var px = tex.GetPixels();

            // alpha -> float, then two separable box blurs (cheap approximation of a gaussian)
            int r = Mathf.Max(1, Mathf.RoundToInt(w * Mathf.Clamp01(TILE_AO_WIDTH)));
            var a = new float[w * h];
            for (int i = 0; i < px.Length; i++) a[i] = px[i].a;
            var tmp = new float[w * h];
            BoxBlurH(a, tmp, w, h, r);
            BoxBlurV(tmp, a, w, h, r);

            float strength = Mathf.Clamp01(TILE_AO_STRENGTH);
            for (int y = 0; y < h; y++)
            {
                // 0 at the bottom of the sprite, 1 at the top
                float up = h > 1 ? (float)y / (h - 1) : 1f;
                float bottomBoost = Mathf.Lerp(Mathf.Max(1f, TILE_AO_BOTTOM), 1f, up);
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color c = px[i];
                    if (c.a <= 0.004f) continue;
                    float occl = Mathf.Clamp01(1f - a[i]);          // 0 inside, ->1 at the edge
                    if (occl <= 0.001f) continue;
                    float k = Mathf.Clamp(1f - occl * strength * bottomBoost, Mathf.Clamp01(TILE_AO_FLOOR), 1f);
                    if (TILE_AO_TINTED)
                    {
                        // Darken by VALUE, and rotate hue / lift saturation in proportion to how
                        // occluded the pixel is, so the rim is a shadowed version of the surface
                        // rather than a grey wash over it.
                        float hh, ss, vv;
                        Color.RGBToHSV(c, out hh, out ss, out vv);
                        float t = occl;                               // 0 at the face, 1 at the rim
                        hh = Mathf.Repeat(hh + (TILE_AO_HUE_ROT / 360f) * t, 1f);
                        ss = Mathf.Clamp01(ss * Mathf.Lerp(1f, TILE_AO_SAT_MUL, t));
                        Color o = Color.HSVToRGB(hh, ss, vv * k);
                        o.a = c.a;
                        px[i] = o;
                    }
                    else
                    {
                        c.r *= k; c.g *= k; c.b *= k;                // alpha untouched
                        px[i] = c;
                    }
                }
            }

            // ── top-face (cap) darkening — identify the cap from the UNMODIFIED value ──
            if (TILE_CAP_DARKEN_ON && TILE_CAP_DARKEN < 0.999f)
            {
                for (int y = 0; y < h; y++)
                {
                    float tTop = 1f - (h > 1 ? (float)y / (h - 1) : 0f);
                    // Purely VERTICAL ramp — nothing keyed to value, so no contour to trace.
                    float zw = 1f - Mathf.SmoothStep(0f, 1f,
                                   Mathf.InverseLerp(TILE_CAP_FADE_START, TILE_CAP_FADE_END, tTop));
                    if (zw <= 0.001f) continue;
                    float k = Mathf.Lerp(1f, TILE_CAP_DARKEN, zw);
                    if (k >= 0.999f) continue;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        Color c = px[i];
                        if (c.a <= 0.004f) continue;
                        if (TILE_CAP_TINTED)
                        {
                            // darken by VALUE, and commit to a hue — see TILE_CAP_TINTED
                            // 🚨 SCALE BY zw, NOT (1-k). `k` only ranges 1.0 -> TILE_CAP_DARKEN
                            // (0.80), so `1-k` maxes out at 0.20 — the tint was being applied at
                            // a FIFTH of the configured strength and was invisible. Spencer:
                            // "nothing has changed". `zw` is the cap COVERAGE ramp: 0 at the fade
                            // edge, a true 1.0 at the top. 2026-09-15.
                            float amt = zw;
                            float hh, ss, vv;
                            Color.RGBToHSV(c, out hh, out ss, out vv);
                            hh = Mathf.Repeat(hh + (TILE_CAP_HUE_SHIFT / 360f) * amt, 1f);
                            ss = Mathf.Clamp01(ss + TILE_CAP_SAT_ADD * amt);
                            Color o = Color.HSVToRGB(hh, ss, vv * k);
                            px[i] = new Color(o.r, o.g, o.b, c.a);
                        }
                        else
                        {
                            c.r *= k; c.g *= k; c.b *= k;
                            px[i] = c;
                        }
                    }
                }
            }

            // ── face falloff: the vertical gradient a flat face cannot produce on its own ──
            if (TILE_FACE_GRAD && TILE_FACE_GRAD_STRENGTH > 0.001f)
            {
                float peak = Mathf.Clamp01(TILE_FACE_GRAD_PEAK);
                for (int y = 0; y < h; y++)
                {
                    float tTop = 1f - (h > 1 ? (float)y / (h - 1) : 0f);   // 0 at sprite top
                    // Rise gently to the peak, then fall away below it — Toon Blast's shape.
                    float g = (tTop <= peak)
                        ? Mathf.Lerp(0.97f, 1f, peak > 0.0001f ? tTop / peak : 1f)   // barely dip above the peak
                        : Mathf.Lerp(1f, 1f - TILE_FACE_GRAD_STRENGTH,
                                     Mathf.Clamp01((tTop - peak) / Mathf.Max(0.0001f, 1f - peak)));
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        Color c = px[i];
                        if (c.a <= 0.004f) continue;
                        c.r *= g; c.g *= g; c.b *= g;   // multiply: hue and saturation preserved
                        px[i] = c;
                    }
                }
            }

            // ── baked specular from Blender, nudged up, added AFTER the AO ──
            if (applySpec && TILE_SPEC && TILE_SPEC_STRENGTH > 0.001f)
            {
                if (s_specMap == null) s_specMap = Resources.Load<Texture2D>("Tiles/tile_spec@2x");
                if (s_specMap != null)
                {
                    Texture2D sm = ReadableCopy(s_specMap);
                    if (sm != null)
                    {
                        var sp = sm.GetPixels();
                        int sw = sm.width, sh = sm.height;
                        int upPx = Mathf.RoundToInt(w * TILE_SPEC_UP);
                        for (int y = 0; y < h; y++)
                        {
                            // Unity Texture2D row 0 is the BOTTOM. dst[y] = src[y - up] pulls
                            // content from LOWER in the source and places it HIGHER, i.e. moves
                            // the band UP the tile. (y + up) moves it DOWN — that was the bug.
                            int sy = Mathf.Clamp(Mathf.RoundToInt((y - upPx) * (sh / (float)h)), 0, sh - 1);
                            for (int x = 0; x < w; x++)
                            {
                                int i = y * w + x;
                                Color c = px[i];
                                if (c.a <= 0.004f) continue;
                                int sx = Mathf.Clamp(Mathf.RoundToInt(x * (sw / (float)w)), 0, sw - 1);
                                float v = sp[sy * sw + sx].r;
                                if (v <= 0.002f) continue;
                                // MULTIPLY DOWN where unlit, rather than adding white where lit.
                                // 2026-09-11: screening toward white across the whole face
                                // DESATURATED the gold badly ("the gold has now changed its hue
                                // considerably") — adding white always kills chroma, and it only
                                // got away with it before because the band was 14px. A uniform
                                // RGB multiply preserves hue AND saturation exactly; the gradient
                                // comes from darkening the unlit part instead.
                                float lit = Mathf.Clamp01(v);
                                float km  = Mathf.Lerp(1f - TILE_SPEC_STRENGTH, 1f, lit);
                                c.r *= km; c.g *= km; c.b *= km;
                                px[i] = c;
                            }
                        }
                        Destroy(sm);
                    }
                }
            }

            // ── legacy centre-blob glint (TILE_GLINT, now off by default) ──
            if (TILE_GLINT && TILE_GLINT_STRENGTH > 0.001f)
            {
                float sy = Mathf.Max(0.01f, TILE_GLINT_SPREAD);
                float sx = Mathf.Max(0.01f, TILE_GLINT_WIDTH);
                for (int y = 0; y < h; y++)
                {
                    float ny = h > 1 ? (float)y / (h - 1) : 0.5f;
                    float dy = (ny - TILE_GLINT_Y) / sy;
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        Color c = px[i];
                        if (c.a <= 0.004f) continue;
                        float nx = w > 1 ? (float)x / (w - 1) : 0.5f;
                        float dx = (nx - 0.5f) / sx;
                        float g = Mathf.Exp(-(dy * dy + dx * dx));
                        g *= a[i];                                  // stay inside the silhouette
                        if (g < 0.002f) continue;
                        float amt = Mathf.Clamp01(g * TILE_GLINT_STRENGTH);
                        c.r += (1f - c.r) * amt;                    // screen toward white
                        c.g += (1f - c.g) * amt;
                        c.b += (1f - c.b) * amt;
                        px[i] = c;
                    }
                }
            }

            tex.SetPixels(px);
            tex.Apply(false);
            tex.filterMode = src.filterMode;
            tex.wrapMode   = TextureWrapMode.Clamp;
            if (!s_aoLogged)
            {
                s_aoLogged = true;
                WDLog.Log($"[TileAO] contact edge baked on every face — strength {TILE_AO_STRENGTH:0.00}, width {TILE_AO_WIDTH:0.000} ({r}px), bottom x{TILE_AO_BOTTOM:0.0}, tinted={TILE_AO_TINTED} (hue {TILE_AO_HUE_ROT:+0}, sat x{TILE_AO_SAT_MUL:0.00})");
            }
            return tex;
        }

        private static void BoxBlurH(float[] src, float[] dst, int w, int h, int r)
        {
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                float sum = 0f; int n = 0;
                for (int x = -r; x <= r; x++) { int xi = Mathf.Clamp(x, 0, w - 1); sum += src[row + xi]; n++; }
                for (int x = 0; x < w; x++)
                {
                    dst[row + x] = sum / n;
                    int add = Mathf.Clamp(x + r + 1, 0, w - 1);
                    int rem = Mathf.Clamp(x - r,     0, w - 1);
                    sum += src[row + add] - src[row + rem];
                }
            }
        }

        private static void BoxBlurV(float[] src, float[] dst, int w, int h, int r)
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0f; int n = 0;
                for (int y = -r; y <= r; y++) { int yi = Mathf.Clamp(y, 0, h - 1); sum += src[yi * w + x]; n++; }
                for (int y = 0; y < h; y++)
                {
                    dst[y * w + x] = sum / n;
                    int add = Mathf.Clamp(y + r + 1, 0, h - 1);
                    int rem = Mathf.Clamp(y - r,     0, h - 1);
                    sum += src[add * w + x] - src[rem * w + x];
                }
            }
        }

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
            // RESTING FACE — change RESTING_FACE_ASSET to swap it; no other edit needed.
            // 2026-09-21: Spencer is iterating on hand-painted tiles, so the asset name is a
            // constant rather than a literal buried here. Falls back to the old render.
            Texture2D glossyTex = Resources.Load<Texture2D>(RESTING_FACE_ASSET)
                               ?? Resources.Load<Texture2D>("Tiles/white_glossy@2x");
            // TEMP DIAG 2026-09-21 — Spencer: "it didnt load". Report what Unity ACTUALLY
            // handed back, so a stale import can be told apart from a code path that ignores it.
            WDLog.Log($"[TileFace] {RESTING_FACE_ASSET} -> {(glossyTex != null ? glossyTex.width + "x" + glossyTex.height : "NULL")}  " +
                      $"white5 refBounds={refBounds:F4}  WARM_TILES={WARM_TILES} TILE_AO={TILE_AO} " +
                      $"SAT_MUL={TILE_SAT_MUL} HUE_SHIFT={TILE_HUE_SHIFT}");
            if (glossyTex != null && refBounds > 0.0001f)
            {
                // 2026-09-03: the tile art is now TIGHT-CROPPED and TALLER than wide
                // (0.782). The old centred SQUARE crop threw away that shape and left
                // the tile at ~70% of the cell width. Use the full texture, and set PPU
                // off WIDTH so sprite width == one cell; the extra height becomes the
                // vertical overlap that hides the tile-top behind the tile above.
                float ppu = glossyTex.width / refBounds;
                if (WARM_TILES)
                {
                    Texture2D warmed = WarmedCopy(glossyTex);
                    if (warmed != null) glossyTex = warmed;
                }
                if (TILE_AO)
                {
                    // spec ON: this IS the cutebox geometry the spec map was rendered from
                    Texture2D ao = ContactEdgeCopy(glossyTex, true);
                    if (ao != null) glossyTex = ao;
                }
                loadedNormal = Sprite.Create(glossyTex,
                    new Rect(0f, 0f, glossyTex.width, glossyTex.height),
                    new Vector2(0.5f, 0.5f), ppu);
                s_dropShadowSpriteA = MakeBoardShadow(BoardShadowTexA, ppu);
                s_dropShadowSpriteB = MakeBoardShadow(BoardShadowTexB, ppu);
                s_dropShadowSprite  = s_useBoardShadowB ? s_dropShadowSpriteB : s_dropShadowSpriteA;
            }

            Sprite loadedPrimed = Resources.Load<Sprite>("Tiles/pink_tile@2x");
            // 2026-06-04 Spencer: new glossy primed (pink) tile — same content-rect/PPU
            // drop-in treatment as the white tile (1024px, 80% fill). Replaces loadedPrimed
            // so all primed/heat states below pick it up.
            // PRIMED (armed) face. PRIMED_FACE_ASSET lets it be swapped by name — 2026-09-21
            // Spencer is trying green_primed@2x in place of the magenta.
            Texture2D primedTex = AoFace(Resources.Load<Texture2D>(PRIMED_FACE_ASSET)
                                      ?? Resources.Load<Texture2D>("Tiles/primed_tile@2x"), true);
            primedTex = LiftedFace(primedTex, PRIMED_FACE_LIFT);   // 2026-10-22 brighten the green a touch
            WDLog.Log($"[PrimedFace] {PRIMED_FACE_ASSET} -> {(primedTex != null ? primedTex.width + "x" + primedTex.height : "NULL")}");
            if (PRIMED_TIER_ASSETS != null)
            {
                s_spritePrimedTier = new Sprite[PRIMED_TIER_ASSETS.Length];
                for (int ti = 1; ti < PRIMED_TIER_ASSETS.Length; ti++)
                {
                    if (string.IsNullOrEmpty(PRIMED_TIER_ASSETS[ti])) continue;
                    var tt = AoFace(Resources.Load<Texture2D>(PRIMED_TIER_ASSETS[ti]), true);
                    if (tt == null) { WDLog.Log($"[PrimedTier] {ti}: {PRIMED_TIER_ASSETS[ti]} MISSING"); continue; }
                    float tref = (loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f) ? loadedNormal.bounds.size.x : 1f;
                    s_spritePrimedTier[ti] = Sprite.Create(tt, new Rect(0,0,tt.width,tt.height), new Vector2(0.5f,0.5f), tt.width / tref);
                    WDLog.Log($"[PrimedTier] {ti}: {PRIMED_TIER_ASSETS[ti]} -> {tt.width}x{tt.height}");
                }
            }
            if (primedTex != null && loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
            {
                // 2026-09-03: the primed tile art is now TIGHT-CROPPED and TALLER than
                // wide, like the white tile. The old centred SQUARE crop cut the rounded
                // top and bottom off and kept only the flat middle — which rendered the
                // primed state as a plain magenta RECTANGLE with no tile form. Use the
                // full texture at a PPU that matches the white tile's bounds.
                float targetBounds = loadedNormal.bounds.size.x; // = the (glossy) tile size
                float pppu = primedTex.width / targetBounds;
                loadedPrimed = Sprite.Create(primedTex,
                    new Rect(0f, 0f, primedTex.width, primedTex.height),
                    new Vector2(0.5f, 0.5f), pppu);
            }
            // Category faces — same tight-cropped 249x299 art as the normal tile.
            {
                Texture2D vTex = AoFace(Resources.Load<Texture2D>("Tiles/vowel_tile@2x"), true);
                // GOLD face built from the SAME art as the resting tile, hue-shifted and
                // saturation-multiplied — see GOLD_HUE_SHIFT. Falls back to the authored
                // highvalue art if the recolour is switched off.
                Texture2D hTex = null;
                // AUTHORED GOLD FACE takes priority — Spencer's hand-painted gold_tile@2x
                // (2026-09-21). Same pattern as RESTING_FACE_ASSET: swap the string, nothing else.
                if (!string.IsNullOrEmpty(GOLD_FACE_ASSET))
                {
                    hTex = Resources.Load<Texture2D>(GOLD_FACE_ASSET);
                    WDLog.Log($"[GoldFace] {GOLD_FACE_ASSET} -> {(hTex != null ? hTex.width + "x" + hTex.height : "NULL")}");
                }
                if (hTex == null && GOLD_BLOCK_FACE)
                {
                    // gold_block_flat@2x, NOT gold_block@2x. MEASURED 2026-09-11: the raw cube
                    // render has NO flat plateau — L falls continuously 176 (30% up) -> 117
                    // (bottom), and its BRIGHTEST point is darker than white_glossy's DARKEST
                    // plateau value (216). In the cell that read as a dull olive blob floating
                    // inside its own cell: Spencer, "they arent the right size or colour". The
                    // sprite was always the correct size — only the bright REGION was small.
                    // The _flat bake reimposes white_glossy's per-row luminance envelope on the
                    // cube, keeping its hue, facets and gloss streaks. Rebuild it by re-running
                    // the envelope-match step, not by hand-tuning a lighten.
                    Texture2D gbTex = Resources.Load<Texture2D>("Tiles/gold_block_flat@2x")
                                   ?? Resources.Load<Texture2D>("Tiles/gold_block@2x");
                    if (gbTex != null)
                    {
                        if (GOLD_BLOCK_LIGHTEN > 1.001f)
                        {
                            var lifted = LiftCopy(gbTex, GOLD_BLOCK_LIGHTEN);
                            if (lifted != null) gbTex = lifted;
                        }
                        hTex = AoFace(gbTex, true);
                    }
                    WDLog.Log($"[GoldBlock] tex={(gbTex != null ? gbTex.name + " " + gbTex.width + "x" + gbTex.height : "NULL")} lighten={GOLD_BLOCK_LIGHTEN}");
                }
                if (hTex == null)
                    hTex = HIVAL_RECOLOR
                        ? AoFace(HueShiftCopy(Resources.Load<Texture2D>("Tiles/white_glossy@2x"), GOLD_HUE_SHIFT, GOLD_SAT_MUL), true)
                        : AoFace(Resources.Load<Texture2D>("Tiles/highvalue_tile@2x"));
                float refB = (loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
                    ? loadedNormal.bounds.size.x : 1f;
                if (vTex != null)
                    s_spriteVowel = Sprite.Create(vTex, new Rect(0,0,vTex.width,vTex.height),
                        new Vector2(0.5f,0.5f), vTex.width / refB);
                if (hTex != null)
                    s_spriteHighValue = Sprite.Create(hTex, new Rect(0,0,hTex.width,hTex.height),
                        new Vector2(0.5f,0.5f), hTex.width / refB);
                WDLog.Log($"[LetterTint] vowel={(s_spriteVowel!=null)} highValue={(s_spriteHighValue!=null)}");
                // COLLECT TILE = Spencer's ramp blue tile (Documents/blue_tile@2x, 2026-09-21) — NOT the flat vowel_tile fill
                // (2026-10-22: "i meant the blue tiles that i created with the ramp ... i just have a flat blue box").
                var gTex = Resources.Load<Texture2D>(GEM_TILE_ASSET);
                if (gTex != null)
                    s_spriteGem = Sprite.Create(gTex, new Rect(0, 0, gTex.width, gTex.height), new Vector2(0.5f, 0.5f), gTex.width / refB);
                int carvedN = 0;
                for (int li = 0; li < 26; li++)
                {
                    var lt = Resources.Load<Texture2D>(CARVED_GEM_PREFIX + (char)('A' + li));
                    if (lt == null) continue;
                    s_gemLetters[li] = Sprite.Create(lt, new Rect(0, 0, lt.width, lt.height), new Vector2(0.5f, 0.5f), lt.width / refB);
                    carvedN++;
                }
                WDLog.Log($"[GemTile] {GEM_TILE_ASSET} {(gTex != null ? "ok" : "MISSING")}, carved letters {carvedN}/26");
                var cTex = Resources.Load<Texture2D>(COLLECT_TILE_ASSET);
                if (cTex != null)
                    s_spriteCollectBlue = Sprite.Create(cTex, new Rect(0, 0, cTex.width, cTex.height), new Vector2(0.5f, 0.5f), cTex.width / refB);
                WDLog.Log($"[CollectTile] {COLLECT_TILE_ASSET} -> {(cTex != null ? cTex.width + "x" + cTex.height : "NULL — falling back to the flat vowel tile")}");
            }

            // Gold PRIMED face — full texture at the white tile's bounds, like the rest.
            Sprite loadedGoldPrimed = null;
            {
                Texture2D gpTex = AoFace(
                    (string.IsNullOrEmpty(GOLD_PRIMED_FACE_ASSET)
                        ? null : Resources.Load<Texture2D>(GOLD_PRIMED_FACE_ASSET))
                    ?? Resources.Load<Texture2D>("Tiles/primed_gold_tile@2x"), true);
                WDLog.Log($"[GoldPrimedFace] {GOLD_PRIMED_FACE_ASSET} -> {(gpTex != null ? gpTex.width + "x" + gpTex.height : "NULL")}");
                float refB2 = (loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
                    ? loadedNormal.bounds.size.x : 1f;
                if (gpTex != null)
                    loadedGoldPrimed = Sprite.Create(gpTex, new Rect(0,0,gpTex.width,gpTex.height),
                        new Vector2(0.5f,0.5f), gpTex.width / refB2);
                WDLog.Log($"[GoldPrimed] sprite loaded = {loadedGoldPrimed != null}");
            }

            Sprite loadedAI     = Resources.Load<Sprite>("Tiles/ai_tile");

            Sprite loadedScored = Resources.Load<Sprite>("Tiles/green_tile2@2x");
            // 2026-06-04 Spencer: new glossy green tile (greeny). It's trimmed to the
            // tile (100% fill), so use the FULL rect at a PPU that matches the white
            // tile's bounds — true drop-in, same size as the rest.
            Texture2D greenyTex = AoFace(Resources.Load<Texture2D>("Tiles/greeny_pillow@2x") ?? Resources.Load<Texture2D>("Tiles/greeny@2x"), true);   // 2026-10-28 pillow family (scored flash)
            if (greenyTex != null && loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
            {
                float gppu = greenyTex.width / loadedNormal.bounds.size.x;
                loadedScored = Sprite.Create(greenyTex, new Rect(0, 0, greenyTex.width, greenyTex.height),
                                             new Vector2(0.5f, 0.5f), gppu);
            }
            Sprite loadedCyan   = Resources.Load<Sprite>("Tiles/cyan_tile@2x");
            Sprite loadedGolden = Resources.Load<Sprite>("Tiles/golden_tile2@2x");
            Sprite loadedWild   = Resources.Load<Sprite>("Tiles/wild2@2x");
            WDLog.Log($"[CyanDebug] cyan_tile@2x loaded? {loadedCyan != null} (name: {(loadedCyan != null ? loadedCyan.name : "NULL")})");

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
                // NAMING TRAP: s_spriteGold is NOT "the gold sprite" — it is the PRIMED
                // face, used by EVERY primed tile. Pointing it at gold art (2026-09-03)
                // made every primed tile gold-arted, so magenta words rendered as gold
                // art under a magenta tint = PINK. It stays the magenta primed face.
                s_spriteGold      = loadedPrimed ?? loadedNormal;
                s_spriteGoldThick = loadedPrimed ?? loadedNormal;
                // The genuinely gold PRIMED face lives separately and is chosen per-tile.
                s_spritePrimedGold = loadedGoldPrimed ?? loadedPrimed ?? loadedNormal;
                s_spriteHeat1     = loadedPrimed ?? loadedNormal;
                s_spriteHeat2     = loadedPrimed ?? loadedNormal;
                s_spriteHeat3     = loadedPrimed ?? loadedNormal;
                s_spriteWhiteThick= loadedNormal;

//                 WDLog.Log("[Tile] Loaded hand-drawn tile sprites from Resources/Tiles.");
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
//                 WDLog.Log("[Tile] Fallback: procedural sprite cache built.");
            }

            s_spriteCacheBuilt = true;

            // WARM THE BLOCKER SPRITES HERE, while the board is still loading. Left lazy, this
            // whole build landed on the frame of the FIRST rising row — the frame that also
            // starts the row-rise tween — and starved it. It must come AFTER s_spriteNormal is
            // assigned: the sprites are scaled by s_spriteNormal.bounds.size.x. 2026-09-15.
            BuildRockVariants();
        }

        private GameObject      _selectRimGO;
        private SpriteRenderer  _selectRimSR;
        private static Sprite   s_selectRimSprite;      // face only (a tile sits above)
        private static Sprite   s_selectRimTallSprite;  // face + exposed top cap

        /// <summary>Cyan selection RIM. A separate overlay drawn slightly larger than the
        /// tile, so the face, its shading and the letter are untouched — nothing is painted
        /// over the tile, so nothing can wash out.
        /// Built here rather than through ApplyBorderColor because that method does not tint
        /// a border: it picks from a fixed set of sprite VARIANTS, and an unknown colour
        /// falls through to the plain sprite with no visible change. 2026-09-09.</summary>
        private void ShowSelectRim(bool on)
        {
            if (!on)
            {
                if (_selectRimSR != null) _selectRimSR.enabled = false;
                return;
            }
            if (_selectRimGO == null)
            {
                {
                    // TWO variants, same rule the cluster outline uses (2026-09-10 Spencer):
                    //  - SQUARE for a tile with something on top of it. Its dark top cap is
                    //    hidden behind that tile, so wrapping it would put the rim on a buried
                    //    surface.
                    //  - TALL for a tile at the top of its stack, where the cap IS visible and
                    //    the rim should go round it.
                    // Same corner radius and stroke in PIXELS for both, so they read as one
                    // shape — only the height differs.
                    int w = Mathf.Clamp(Mathf.RoundToInt(_cellSize * 200f), 64, 512);
                    // FRONT-ON: there is no cap, so the "face" sprite takes the tile's real
                    // aspect and the tall variant is never chosen. 2026-09-15.
                    int h  = Mathf.RoundToInt(w / TILE_TARGET_ASPECT);   // legacy: face + cap
                    int hf = TILE_ART_FRONT_ON ? Mathf.RoundToInt(w / ArtAspect) : w;
                    int r = w / 6, stroke = Mathf.Max(5, w / 16);
                    if (s_selectRimSprite == null)
                        s_selectRimSprite = TileRenderer.CreateRoundedRect(
                            w, hf, r, new Color(0f, 0f, 0f, 0f), Color.white, stroke);
                    if (s_selectRimTallSprite == null)
                        s_selectRimTallSprite = TileRenderer.CreateRoundedRect(
                            w, h, r, new Color(0f, 0f, 0f, 0f), Color.white, stroke);
                }
                _selectRimGO = new GameObject("SelectRim");
                _selectRimGO.transform.SetParent(transform, false);
                _selectRimGO.transform.localPosition = new Vector3(0f, 0f, -0.06f);
                _selectRimSR = _selectRimGO.AddComponent<SpriteRenderer>();
                _selectRimSR.sprite = s_selectRimSprite;
                _selectRimGO.tag = "Untagged";
            }
            _selectRimSR.color = EDIT_RIM_CYAN;
            _selectRimSR.sortingOrder = BaseSortingOrder + 2;   // over the face, under the letter
            // Size from the TILE's own sprite bounds, not from cell size — the rim is a
            // CHILD, so matching bounds in local space makes it track the tile exactly once
            // the parent's scale is inherited, whatever that scale happens to be.
            var ts = (_spriteRenderer != null && _spriteRenderer.sprite != null)
                ? _spriteRenderer.sprite.bounds.size : Vector3.one;
            // Is this tile's dark top cap actually VISIBLE? Only if nothing sits on it — the
            // same one-lookup rule the cluster outline uses. 2026-09-10 Spencer.
            var gm = GridManager.Instance;
            // Front-on art has no exposed cap to fence, so it always uses the face variant.
            bool capExposed = !TILE_ART_FRONT_ON && (gm == null || gm.GetTile(Col, Row + 1) == null);
            _selectRimSR.sprite = capExposed && s_selectRimTallSprite != null
                                ? s_selectRimTallSprite : s_selectRimSprite;

            var rs = _selectRimSR.sprite != null ? _selectRimSR.sprite.bounds.size : Vector3.one;
            // Width-matched either way. The tall variant is centred on the WHOLE sprite (the
            // cap is part of what it fences); the square one is dropped by half the overhang so
            // it sits on the face.
            float k = rs.x > 0.0001f ? (ts.x * 1.04f) / rs.x : 1f;
            _selectRimGO.transform.localScale = new Vector3(k, k, 1f);
            // The drop existed to move the square rim off the cap and onto the face. Front-on
            // the rim already matches the sprite, so it stays centred.
            float rimY = (capExposed || TILE_ART_FRONT_ON) ? 0f : -(ts.y - ts.x) * 0.5f;
            _selectRimGO.transform.localPosition = new Vector3(0f, rimY, -0.06f);
            _selectRimSR.enabled = true;
        }

        /// <summary>Force the rim to agree with _editSelected. Called from the board sync,
        /// which visits every tile after every board change.
        /// Needed because the rim is a CHILD of a POOLED tile: three paths end a selection
        /// (deselect, swap-commit, explosion) and any one of them missing the cleanup leaves
        /// a lit rim that comes back on whatever tile reuses the object — which is how
        /// outlines appeared on tiles Spencer never selected. Enforcing the invariant in one
        /// place beats trusting every exit path. 2026-09-09.</summary>
        public void SyncSelectRim()
        {
            if (_selectRimSR == null) return;
            bool want = _editSelected && EDIT_SELECT_RIM;
            // Re-run the builder while selected: a rise or a gravity step can put a tile on top
            // of (or clear one off) the selected tile, which changes which rim variant is right.
            // 2026-09-10 Spencer.
            if (want) { ShowSelectRim(true); return; }
            if (_selectRimSR.enabled != want) _selectRimSR.enabled = want;
        }

        /// <summary>The "no special border" face for THIS tile — category-aware.
        ///
        /// 🚨 ROOT CAUSE of "select a gold tile, deselect it, it turns white" (reported
        /// 2026-09-10, still live 2026-09-21). ApplyBorderColor used a bare
        /// `thick ? s_spriteThick : s_spriteNormal`, which is the PLAIN face regardless of
        /// letter category — so Highlight(false) -> ApplyBorderColor(TILE_BORDER_NORMAL)
        /// repainted a high-value GOLD letter white. RestoreRestingFaceAndTint had already put
        /// the gold face back; ApplyBorderColor ran after it and won. Two writers, one property.
        ///
        /// The thick variant only applies when there is no category face to protect — with the
        /// hand-drawn sprite set s_spriteThick IS s_spriteNormal anyway (see BuildSpriteCache).</summary>
        private Sprite PlainFaceForBorder(bool thick)
        {
            Sprite face = RestingFaceFor(Letter);
            if (face == s_spriteNormal && thick && s_spriteThick != null) return s_spriteThick;
            return face;
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
                FaceSprite = PlainFaceForBorder(thick);   // was s_spriteNormal — ate the gold face
            else if (ColorsClose(borderColor, TILE_BORDER_GOLD)
                  || ColorsClose(borderColor, PRIMED_GLOW)
                  || ColorsClose(borderColor, PRIMED_DANGER_GLOW)
                  || ColorsClose(borderColor, PRIMED_GOLD_GLOW))
                FaceSprite = (_isGoldPrimed && _hasPrimedGlow && s_spritePrimedGold != null)
                    ? s_spritePrimedGold
                    : (thick ? s_spriteGoldThick : s_spriteGold);
            else if (ColorsClose(borderColor, Color.white))
                FaceSprite = s_spriteWhiteThick;
            else if (_hasPrimedGlow)
            {
                // Any other color while primed — keep the primed sprite family,
                // don't fall through to normal sprite (which causes the white flash).
                // 🚨 ASK THE FLAG, NOT THE COLOUR. 2026-09-21: the branches above identify the
                // armed face by ColorsClose(borderColor, PRIMED_GOLD_GLOW). Once the tint is
                // NEUTRALISED for authored art that test can never match, so a GOLD word fell
                // through to here and got the rose/green primed face — Spencer: "JAR did not
                // light up gold, it lit up green". _isGoldPrimed is the resolved, sticky answer.
                FaceSprite = (_isGoldPrimed && s_spritePrimedGold != null)
                    ? s_spritePrimedGold
                    : (thick ? s_spriteGoldThick : s_spriteGold);
            }
            else
            {
                // Unknown color (player green / AI orange flash) — use SpriteRenderer tint instead of regenerating
                FaceSprite = PlainFaceForBorder(thick);   // category-aware, see above
                // Border color is faked via sprite tint — good enough for a brief flash
            }
        }

        private void ApplyBorderColorDirect(Color borderColor)
        {
            if (_spriteRenderer == null) return;
            if (ColorsClose(borderColor, Color.white))
                FaceSprite = s_spriteWhiteThick;
            else
                FaceSprite = s_spriteThick;
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
                float stretch = TILE_SQUASH_STRETCH ? 1f + easedT * 0.10f : 1f;
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
            if (!TILE_SQUASH_STRETCH)
            {   // no distortion: snap to the exact rest scale, keep the landing sound
                if (_squishCoroutine != null) { StopCoroutine(_squishCoroutine); _squishCoroutine = null; }
                transform.localScale = CanonicalRestScale();
                if (playSound) PlayLandSound();   // same landing sound the squish played
                return;
            }
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
            if (!TILE_SQUASH_STRETCH) { transform.localScale = CanonicalRestScale(); return; }   // see TILE_SQUASH_STRETCH
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
