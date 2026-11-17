using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;

namespace WordDrop
{
    /// <summary>
    /// Manages the player's hand of 5 letter cards (display + input).
    ///
    /// New system (Job 8):
    ///   - Works with PlayerHand + MatchController instead of old TileBag flow
    ///
    /// Job 3 changes:
    ///   - FullTurnSequence: after safety timeout fires, calls GameVisualBridge.ForceReset()
    ///     to guarantee _isPlayingBack is false before proceeding to AI turn check.
    ///
    /// Job 4 changes:
    ///   - Added explicit logging of CurrentPlayer, IsMatchActive, and aiShouldAct
    ///   - Log player index before and after the wait loop to verify player switched
    ///   - Added fallback: if aiShouldAct is false but both players have turns remaining,
    ///     force-trigger the AI turn anyway
    /// </summary>
    public class HandManager : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        private static int  HAND_SIZE            => PlayerHand.HAND_SIZE;
        // 2026-05-28 (Path A): reduced from 0.85 → 0.72 so hand tiles match
        // the board tile size (~150px PSD target). Frees vertical space for the
        // bottom booster row + NEXT to coexist without overlap. In Survival
        // mode this is overridden by exact PSD-derived sizing — see helpers
        // below and the survival branches of GetCardX/GetCardRowY/etc.
        private const float CARD_SIZE_FRACTION  = 0.72f;

        // ── PSD layout constants (canvas 1179×2556, iPhone 16/15 Pro) ─────────
        // Mirrors values used by BoosterHUDSlot.cs. Camera orthographicSize is
        // 10 (SceneBootstrap.cs:105), giving worldH=20. Conversion ratio is
        // therefore 20/2556 = 0.007825 world units per PSD pixel.
        //
        // Used by survival-mode hand/tray/NEXT placement to pin every world-
        // space element to exact PSD pixel anchors. Normalized values (e.g.
        // 169.67 step) are slight tidy-ups of Spencer's PSD measurements where
        // sub-pixel rounding produced uneven gaps.
        // ──────────────────────────────────────────────────────────────────────
        private const float PSD_CANVAS_W   = 1179f;
        private const float PSD_CANVAS_H   = 2556f;
        // Hand pill — rides 87 PSD below board bottom (drag-distance rule).
        // Board Y=500 in GridManager → bottom 1846 → pill Y=1933.
        // 2026-06-24 (Spencer): whole holder raised 45 PSD (pill/cards/NEXT shifted up
        // together) so the Edit + Bag tools can hang attached UNDER the panel's bottom
        // edge with clean room above the booster row. Still ~22px clear of the board panel.
        private const float PSD_PILL_X     = 137f;
        private const float PSD_PILL_Y     = 1888f;   // was 1933 (−45)
        private const float PSD_PILL_W     = 905f;
        private const float PSD_PILL_H     = 200f;
        // Hand cards (4 of them, normalized step)
        private const float PSD_CARD_X0    = 171f;
        private const float PSD_CARD_STEP  = 169.67f;
        private const float PSD_CARD_Y     = 1913f;   // was 1958 (−45)
        private const float PSD_CARD_W     = 149f;
        private const float PSD_CARD_H     = 151f;
        // NEXT preview
        private const float PSD_NEXT_X     = 898f;
        private const float PSD_NEXT_Y     = 1954f;   // was 1999 (−45)
        private const float PSD_NEXT_W     = 108f;
        private const float PSD_NEXT_H     = 109f;

        /// <summary>
        /// PSD pixel → world unit conversion. Depends on the live camera's
        /// orthographicSize; recomputed each call so a camera resize (rare)
        /// stays consistent.
        /// </summary>
        private float PsdToWorld(float psdPx)
        {
            float halfH = _cam != null ? _cam.orthographicSize : 10f;
            return psdPx * (2f * halfH / PSD_CANVAS_H);
        }

        /// <summary>PSD X coord → world X (canvas center → world 0).</summary>
        private float PsdXToWorld(float xPsd)
        {
            return PsdToWorld(xPsd - PSD_CANVAS_W * 0.5f);
        }

        /// <summary>PSD Y coord → world Y (canvas center → world 0, Y flipped).</summary>
        private float PsdYToWorld(float yPsd)
        {
            return PsdToWorld(PSD_CANVAS_H * 0.5f - yPsd);
        }

        // Card visual colors
        private static readonly Color CARD_FILL_NORMAL    = new Color(0.973f, 0.961f, 0.937f, 1f);    // warm cream #F8F5EF
        private static readonly Color CARD_BORDER_NORMAL  = new Color(0.800f, 0.745f, 0.640f, 1f);  // desaturated warm tan
        private static readonly Color CARD_BORDER_SELECT  = new Color(0.200f, 0.851f, 0.424f, 1f);  // player green #33D96C
        private static readonly Color CARD_TEXT_COLOR     = new Color(0.145f, 0.153f, 0.200f, 1f);   // text dark #252733
        private static readonly Color CARD_PTS_COLOR      = new Color(0.35f, 0.35f, 0.40f, 1f);
        private static readonly Color CARD_BORDER_SWAP    = new Color(0.85f, 0.60f, 0.10f, 1f);
        private static readonly Color CARD_BORDER_SWAP_SEL= new Color(1.00f, 0.80f, 0.20f, 1f);
        private static readonly Color CARD_TEXT_SWAP      = new Color(0.75f, 0.45f, 0.05f, 1f);
        // Bright cyan for the SELECTED swap card — matches the board tile's bloomed edit-glow appearance so the
        // two tiles being swapped read as the same cyan selection. 2026-07-10 Spencer.
        private static readonly Color SWAP_SELECTED_CYAN  = new Color(0.55f, 1.00f, 1.08f, 1f);

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static HandManager Instance { get; private set; }
        public GameObject[] GetCardObjects() => _cardObjects;

        /// <summary>World-space bounds of the control-tray pill (the rounded hand holder), for the
        /// tutorial spotlight to cut a matching rounded hole. Null if the tray isn't built. 2026-06-25.</summary>
        public Bounds? ControlTrayWorldBounds
        {
            get
            {
                if (_controlTray == null) return null;
                var sr = _controlTray.GetComponent<SpriteRenderer>();
                return sr != null ? sr.bounds : (Bounds?)null;
            }
        }

        // ── Runtime state ─────────────────────────────────────────────────────────

        private char[]           _hand          = new char[PlayerHand.MAX_HAND_SIZE];
        private int              _selectedIndex = -1;
        private bool             _swapModeActive = false;

        private GameObject[]     _cardObjects   = new GameObject[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[] _cardSRs       = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];
        private TMPro.TextMeshPro[] _cardTexts    = new TMPro.TextMeshPro[PlayerHand.MAX_HAND_SIZE];
        private TMPro.TextMeshPro[] _cardPtsTexts = new TMPro.TextMeshPro[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[]   _cardShadows  = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];
        // Additive HDR cyan bloom overlay for the SELECTED swap card — the SAME mechanism the board tile's edit
        // glow uses (WordDrop/AdditiveSprite + HDR _Color), so a swapped hand card glows with the identical cyan
        // halo as the board tile it's exchanging with. 2026-07-10 Spencer.
        private SpriteRenderer[]    _cardCyanGlowSRs = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];
        private static Material               s_cardBloomMat;
        private static MaterialPropertyBlock  s_cardBloomMPB;

        private Sprite           _spriteNormal;
        // 2026-06-04 Spencer: baked glassy tile + separate baked drop shadow for the
        // wild card (authored in PS). Built in code with a computed PPU so the 80%-fill
        // tile lands at the exact rack size. _spriteTileShadow renders as the contact shadow.
        private Sprite           _spriteGlossy;
        private Sprite           _spriteTileShadow;
        private Sprite           _spriteWildShadow; // 2026-06-04 Spencer: dedicated baked shadow for wild rack cards only
        private static Material  s_shadowMultiplyMat; // 2026-06-04 Spencer: MULTIPLY blend for the baked shadow (matches PS layer)

        // 2026-06-05 Spencer: in-editor A/B shadow compare. Flips the WHOLE board's card
        // shadow between variant A (current) and B (a new one) IN PLACE — toggle in the
        // Inspector or press the \ key in Play mode. Strengths are live. To test a new
        // shadow, drop its texture's bare name (e.g. "shadow_b@2x") into _shadowTexB.
        [Header("Shadow A/B Test ( \\ key flips in Play )")]
        [SerializeField] private bool   _shadowUseB = false; // 2026-06-08 Spencer: default back to A (test_shadow@2x)
        [SerializeField] private string _shadowTexA = "test_shadow@2x";
        [SerializeField] private string _shadowTexB = "menu psds/shadowy"; // 2026-06-05 Spencer: test PSD shadow
        [SerializeField, Range(0f, 2f)] private float _shadowStrengthA = 0.40f;
        [SerializeField, Range(0f, 2f)] private float _shadowStrengthB = 0.40f;
        private Sprite   _shadowSpriteA, _shadowSpriteB;
        private Material _shadowMatA, _shadowMatB;
        private Sprite           _spriteSelected;
        private Sprite           _spriteSwap;
        private Sprite           _spriteSwapSelected;
        private Sprite           _spriteWild;          // hand-card wild sprite (wild@2x — has ? baked in)
        // Wild halo — multicolor glow sprite placed behind the wild card so the
        // slot reads as "special" at a glance. One child GO per card, toggled
        // on/off via RefreshCardVisual based on IsWildSlot.
        private Sprite           _spriteWildHalo;
        private Material         _wildHaloMaterial;
        private GameObject[]     _cardHalos = new GameObject[PlayerHand.MAX_HAND_SIZE];
        private SpriteRenderer[] _cardHaloSRs = new SpriteRenderer[PlayerHand.MAX_HAND_SIZE];
        // 2026-06-04 Spencer: tight dark contact shadow hugging the wild tile edge,
        // layered between the aura and the card face so the tile stays crisp on the glow.
        private GameObject[]     _cardContactShadow = new GameObject[PlayerHand.MAX_HAND_SIZE];

        // 2026-06-04 Spencer: LIVE wild-shadow tuning. These are exposed in the Inspector
        // so the wild rack-card shadow can be dialed in during Play mode WITHOUT a
        // recompile — drag the sliders and it updates every frame (see Update()).
        [Header("Wild Shadow (tune live in Play mode)")]
        [SerializeField] private bool    _wildShadowEnabled  = true;
        [SerializeField, Range(0f, 2f)]    private float   _wildShadowStrength = 0.48f; // Multiply darkness
        [SerializeField] private Vector2 _wildShadowOffset   = Vector2.zero;             // local x/y nudge
        [SerializeField, Range(0.3f, 2.5f)] private float  _wildShadowScale    = 1f;     // size multiplier
        private Material _wildShadowMat; // dedicated instance so strength is independent of normal cards
        // 2026-06-03 Spencer: holographic overlay on the WILD hand card (matches the
        // board tile). Shown by RefreshCardVisual when the slot is wild + Tile.IridescentWild.
        private GameObject[]     _cardIrid = new GameObject[PlayerHand.MAX_HAND_SIZE];
        private static Material  s_iridCardMaterial;
        private static Sprite    s_cardWildGlow;   // 2026-06-03: soft VFX_Glow radial behind the card rays

        private Camera           _cam;
        private GridManager      _grid;
        private float            _cardSize;

        // ── Next tile preview ────────────────────────────────────────────────────
        private int              _lastCarryCol = -1; // track column for tick sound during carry
        private GameObject       _nextTilePreview;
        private SpriteRenderer   _nextTileSR;
        private TMPro.TextMeshPro _nextTileLetter;
        private bool             _suppressNextPreview; // blanks the NEXT preview between an early visual deal and turn-resolve
        private TMPro.TextMeshPro _nextTileLabel;
        private GameObject       _nextTileSocket;
        private GameObject       _nextDivider;     // thin line between hand + NEXT — slides with the holder on entry
        private GameObject       _controlTray;

        public bool IsInteractable { get; set; } = false;

        // ── Tap-to-swap mode (driven by BoosterHUDSlot's TileBag tap) ──────────
        /// <summary>True while the player is in tap-to-swap mode: scrim is up,
        /// hand cards are pulsing, the next card tap triggers a swap.</summary>
        public bool TapToSwapModeActive { get; private set; }
        /// <summary>Pulse tweens started in EnterTapToSwapMode so we can kill
        /// them on exit / on a successful swap.</summary>
        private readonly List<Tweener> _tapToSwapPulseTweens = new List<Tweener>();

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            BoardShuffle.Ensure();   // no-words -> shuffle watcher (2026-10-30)
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _intentSlopPx = Mathf.Max(14f, Screen.dpi * 0.08f);

            _cam  = Camera.main;
            _grid = GridManager.Instance;

            if (_grid == null)
            {
                Debug.LogError("[HandManager] GridManager not found in Awake!");
                return;
            }

            BuildCardSprites();
            BuildControlTray();
            BuildCardObjects();

            // Initialize hand to empty display
            for (int i = 0; i < HAND_SIZE; i++)
                _hand[i] = '\0';

            BuildShuffleButton();
            BuildNextTilePreview();
            BuildTileBagButton();
//             WDLog.Log("[HandManager] Awake complete — cards + shuffle + next-tile + tile-bag built, IsInteractable=false.");
        }

        private void Start()
        {
            // Subscribe to MatchController events for hand updates
            if (MatchController.Instance != null)
            {
                MatchController.Instance.OnHandRefilled += OnHandRefilled;
//                 WDLog.Log("[HandManager] Subscribed to MatchController.OnHandRefilled");
            }
            else
            {
                Debug.LogWarning("[HandManager] MatchController not found in Start — cannot subscribe.");
            }

        }

        private void OnDestroy()
        {
            if (MatchController.Instance != null)
                MatchController.Instance.OnHandRefilled -= OnHandRefilled;
        }

        // ── MatchController event handler ─────────────────────────────────────────

        private void OnHandRefilled(HandRefilledEvent evt)
        {
            // Only update visuals for the human player
            if (evt.PlayerIndex != MatchController.PLAYER_HUMAN) return;
            if (evt.Letters == null) return;

            SetHand(evt.Letters);
//             WDLog.Log($"[HandManager] Hand updated from OnHandRefilled: {new string(evt.Letters)}");
        }

        // ── Public API ────────────────────────────────────────────────────────────

        /// <summary>
        /// Repositions all hand UI elements (cards, shuffle, next, tile bag) for the current
        /// grid layout. Call after GridManager.RebuildGrid() changes grid dimensions.
        /// </summary>
        public void RebuildHandLayout()
        {
            _cam = Camera.main;
            _grid = GridManager.Instance;
            if (_grid == null) return;

            DestroyHandUI();
            BuildCardSprites();
            BuildControlTray();
            BuildCardObjects();
            BuildShuffleButton();
            BuildNextTilePreview();
            BuildTileBagButton();
            RefreshAllCardVisuals();

//             WDLog.Log($"[HandManager] RebuildHandLayout — cardRowY={GetCardRowY():F2} shuffleY={_shuffleButtonY:F2}");
        }

        private void DestroyHandUI()
        {
            CancelInvoke(nameof(ResetShuffleButtonColor));

            if (_shadowAnimCoroutine != null)
            {
                StopCoroutine(_shadowAnimCoroutine);
                _shadowAnimCoroutine = null;
            }

            if (_shuffleFillCoroutine != null)
            {
                StopCoroutine(_shuffleFillCoroutine);
                _shuffleFillCoroutine = null;
            }

            if (_controlTray != null)
            {
                Destroy(_controlTray);
                _controlTray = null;
            }

            if (_shuffleFillSR != null)
            {
                Destroy(_shuffleFillSR.gameObject);
                _shuffleFillSR = null;
            }

            if (_shuffleButton != null)
            {
                Destroy(_shuffleButton);
                _shuffleButton = null;
            }

            if (_nextTileSocket != null)
            {
                Destroy(_nextTileSocket);
                _nextTileSocket = null;
            }

            if (_nextTilePreview != null)
            {
                Destroy(_nextTilePreview);
                _nextTilePreview = null;
            }

            if (_nextTileLabel != null)
            {
                Destroy(_nextTileLabel.gameObject);
                _nextTileLabel = null;
            }

            if (_tileBagButton != null)
            {
                Destroy(_tileBagButton);
                _tileBagButton = null;
            }

            if (_swapLabel != null)
            {
                Destroy(_swapLabel);
                _swapLabel = null;
            }

            DismissSwapTilePopup();

            for (int i = 0; i < _cardObjects.Length; i++)
            {
                if (_cardObjects[i] != null)
                    Destroy(_cardObjects[i]);

                if (_cardShadows[i] != null)
                    Destroy(_cardShadows[i].gameObject);

                _cardObjects[i] = null;
                _cardSRs[i] = null;
                _cardTexts[i] = null;
                _cardPtsTexts[i] = null;
                _cardShadows[i] = null;
            }

            _nextTileSR = null;
            _nextTileLetter = null;
            _shuffleButtonY = 0f;
            _shuffleButtonX = 0f;
            _shuffleButtonSize = 0f;
            _tileBagX = 0f;
            _tileBagY = 0f;
            _tileBagSize = 0f;
            _shuffleFilling = false;
        }

        /// <summary>
        /// Updates the hand display from current MatchController PlayerHand data.
        /// Called at match start and after each turn via InitialiseHand().
        /// </summary>
        public void InitialiseHand()
        {
            if (_grid == null) _grid = GridManager.Instance;

            if (_spriteNormal == null)
            {
                _cam = Camera.main;
                BuildCardSprites();
                BuildCardObjects();
            }

            // Pull current hand from MatchController
            if (MatchController.Instance != null)
            {
                PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (hand != null)
                {
                    SetHand(hand.GetAllSlots());
                }
                else
                {
                    // Fallback: blank hand
                    char[] blank = new char[HAND_SIZE];
                    for (int i = 0; i < HAND_SIZE; i++) blank[i] = '?';
                    SetHand(blank);
                }
            }

            _selectedIndex  = -1; // Nothing selected at start
            _swapModeActive = false;
            _inputMode = InputMode.Idle;
            _touchCardIndex = -1;
            _dragIndex = -1;
            _isDragging = false;

            RefreshAllCardVisuals();

            // Snap cards to their final position AND full rest scale. (The old staggered deal-pop grew them from
            // scale 0; it was removed 2026-07-13, so they must land at rest scale here — otherwise they stay at
            // scale 0 and the tile holder looks EMPTY. The level-entry slide-in provides the motion now.)
            float baseY = GetCardRowY();
            Vector3 cardRestScale = GetCardBaseScale();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].SetActive(true);
                _cardObjects[i].transform.DOKill();
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), baseY, -1f);
                _cardObjects[i].transform.localScale = cardRestScale;
                if (_cardShadows[i] != null)
                {
                    _cardShadows[i].color = new Color(0f, 0f, 0f, 0.15f);
                    _cardShadows[i].transform.position = new Vector3(
                        GetCardX(i), baseY - _cardSize * 0.03f, 0f);
                }
            }

            // Player can interact immediately; the pop-in is purely cosmetic
            // and won't affect input pickup.
            IsInteractable = true;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Deal-pop removed 2026-07-13 (Spencer): the level-entry slide-in now animates the whole tile holder
            // in from the right, so the per-card deal pop was redundant (and hidden under the entry). Cards just
            // appear at rest; AnimateHandSlideIn provides the motion.
        }

        // Hand cards now run the canonical NewTilePop at speedMult=1.0 so
        // they look IDENTICAL to row-rise new tiles. With an elastic curve,
        // duration is part of the feel (trailing oscillations only read at
        // the full 0.85s) — speeding it up made the hand look like a different
        // animation. The total hand reveal is ~0.85s + (HAND_SIZE-1)*stagger.
        private const float HAND_POP_SPEED_MULT = 1.0f;

        /// <summary>
        /// Pop-in for each hand card. Curve identity (OutElastic sprout) is
        /// shared with row-rise new tiles via UIAnimations.NewTilePop —
        /// tuning the sprout feel in one place updates both call sites.
        /// Audio: 2026-06-01 — fires ONE PlayEntryPop at the start of the
        /// deal instead of four staggered PlayTileArrival pitches. The old
        /// 4-tone climb felt busy; one consolidated pop reads cleaner.
        /// Stagger 0.06s per card (visual only).
        /// </summary>
        private IEnumerator StaggeredHandPopIn()
        {
            Vector3 baseScale = GetCardBaseScale();
            GameAudio.Instance?.PlayEntryPop();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                UIAnimations.NewTilePop(
                    _cardObjects[i].transform,
                    baseScale,
                    speedMult: HAND_POP_SPEED_MULT);
                yield return WaitCache.Get(0.06f);
            }
        }

        // ── Group animation (Candy-Crush-style converge when a menu opens) ─────
        //
        // Mirrors BoosterHUDSlot.AnimateGroupOut/In — when the Settings modal
        // opens, the hand TILES (cards + NEXT preview) converge horizontally
        // to the hand center and scale out while the holder pill, divider,
        // NEXT label, and NEXT socket stay put. Tiles return on close.

        private const float HAND_GROUP_OUT_DUR   = 0.28f;
        private const float HAND_GROUP_IN_DUR    = 0.32f;
        private const float HAND_GROUP_OVERSHOOT = 1.7f;

        private bool       _handRestCached;
        private Vector3[]  _cardRestPositions;
        private Vector3[]  _cardRestScales;
        private Vector3[]  _cardShadowRestPositions;
        private Vector3[]  _cardShadowRestScales;
        private Vector3    _nextTileRestPosition;
        private Vector3    _nextTileRestScale;
        private float      _handCenterX;
        private float      _handTilesT = 1f;
        private Tween      _handTilesTween;

        private void CacheHandTileRestIfNeeded()
        {
            if (_handRestCached) return;
            _cardRestPositions        = new Vector3[HAND_SIZE];
            _cardRestScales           = new Vector3[HAND_SIZE];
            _cardShadowRestPositions  = new Vector3[HAND_SIZE];
            _cardShadowRestScales     = new Vector3[HAND_SIZE];
            // Group center X — midpoint of leftmost to rightmost ANIMATED
            // element (cards + NEXT preview). Including NEXT in the center
            // calc means NEXT and the leftmost card travel symmetric
            // distances, so the whole group reads as one unit.
            float leftX  = HAND_SIZE > 0 ? GetCardX(0) : 0f;
            float rightX = HAND_SIZE > 0 ? GetCardX(HAND_SIZE - 1) : 0f;
            if (_nextTilePreview != null)
            {
                float nx = _nextTilePreview.transform.position.x;
                if (nx < leftX)  leftX  = nx;
                if (nx > rightX) rightX = nx;
            }
            _handCenterX = (leftX + rightX) * 0.5f;
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null)
                {
                    _cardRestPositions[i] = _cardObjects[i].transform.position;
                    _cardRestScales[i]    = _cardObjects[i].transform.localScale;
                }
                if (_cardShadows[i] != null)
                {
                    _cardShadowRestPositions[i] = _cardShadows[i].transform.position;
                    _cardShadowRestScales[i]    = _cardShadows[i].transform.localScale;
                }
            }
            if (_nextTilePreview != null)
            {
                _nextTileRestPosition = _nextTilePreview.transform.position;
                _nextTileRestScale    = _nextTilePreview.transform.localScale;
            }
            _handRestCached = true;
        }

        /// <summary>
        /// True group-scale animation for hand tiles: a single float tweens
        /// 1→0, and every tile (cards + shadows + NEXT preview) lerps
        /// position toward the group center and scale toward 0 in perfect
        /// lockstep. Reads as one unified object zooming out to a point —
        /// stage-clear-toss-style cartoon perspective. The hand HOLDER pill,
        /// divider, NEXT label, and NEXT socket stay put.
        /// </summary>
        public void AnimateHandTilesOut(float speedMult = 1f)
        {
            CacheHandTileRestIfNeeded();
            float dur = HAND_GROUP_OUT_DUR / Mathf.Max(0.001f, speedMult);

            // Kill any in-flight per-tile tweens (residue from prior implementation).
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) _cardObjects[i].transform.DOKill();
                if (_cardShadows[i] != null) _cardShadows[i].transform.DOKill();
            }
            if (_nextTilePreview != null) _nextTilePreview.transform.DOKill();

            _handTilesTween?.Kill();
            _handTilesTween = DOTween.To(() => _handTilesT, v =>
            {
                _handTilesT = v;
                ApplyHandTilesGroupTransform(v);
            }, 0f, dur).SetEase(Ease.InBack, HAND_GROUP_OVERSHOOT);
        }

        /// <summary>
        /// Reverse of AnimateHandTilesOut — group scale tweens 0→1 with
        /// OutBack overshoot so the tiles pop back into place. No-op if
        /// AnimateHandTilesOut never ran.
        /// </summary>
        /// <summary>Level-entry: collapse the hand tiles instantly, then spring them back to rest with the same
        /// OutBack overshoot the settings-close uses — the bottom "pops in" on level start. 2026-07-13.</summary>
        public void PlayEntrySpring()
        {
            if (!_handRestCached) return;
            _handTilesTween?.Kill();
            _handTilesT = 0f;
            ApplyHandTilesGroupTransform(0f); // start collapsed (off), same frame it springs out
            AnimateHandTilesIn();
        }

        private readonly System.Collections.Generic.List<Transform> _handSlideTfs   = new System.Collections.Generic.List<Transform>();
        private readonly System.Collections.Generic.List<Vector3>   _handSlideRests = new System.Collections.Generic.List<Vector3>();

        private float _handSlideParkedOffX;
        private bool _handSlidePrepped; // true between PrepHandEntry (park) and AnimateHandSlideIn completion

        /// <summary>Snapshot the tile-holder REST positions (while at rest) + park the whole holder off-screen
        /// right, so the level can start blank behind the fading map. AnimateHandSlideIn then slides from here —
        /// it must NOT re-snapshot, or it would capture the parked position as "rest". Called before the map fade.
        /// 2026-07-13 Spencer.</summary>
        public void PrepHandEntry()
        {
            if (_handSlidePrepped) return; // already snapshotted + parked — don't re-snapshot the parked pos as "rest"
            _handSlidePrepped = true;
            _handSlideTfs.Clear(); _handSlideRests.Clear();
            void Add(Transform tr) { if (tr != null) { _handSlideTfs.Add(tr); _handSlideRests.Add(tr.position); } }
            if (_controlTray != null) Add(_controlTray.transform);
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) Add(_cardObjects[i].transform);
                if (_cardShadows[i] != null) Add(_cardShadows[i].transform);
            }
            if (_nextTileSocket != null)  Add(_nextTileSocket.transform);
            if (_nextTilePreview != null) Add(_nextTilePreview.transform);
            if (_nextDivider != null)     Add(_nextDivider.transform);   // the thin divider line
            if (_nextTileLabel != null)   Add(_nextTileLabel.transform);  // the "NEXT" text
            if (_handSlideTfs.Count == 0) { _handSlideParkedOffX = 0f; return; }

            float offX = 12f;
            var cam = Camera.main;
            // 2.6 half-widths clears the FULL tile holder off the right edge (at 1.35 it was still peeking in). 2026-07-13.
            if (cam != null) offX = cam.orthographicSize * cam.aspect * 2.6f;
            _handSlideParkedOffX = offX;
            DOTween.Kill("handSlide");
            ApplyHandSlideOffset(offX); // park off-screen right
        }

        /// <summary>Level-entry: the whole tile holder (tray background, cards + shadows, NEXT socket/preview/divider/
        /// label) SHOOTS IN from the right and settles — the SAME motion as the board (Beat 2). Slides from wherever
        /// PrepHandEntry parked it; preps itself if called standalone. 2026-07-13 Spencer.</summary>
        public void AnimateHandSlideIn()
        {
            if (_handSlideTfs.Count == 0) PrepHandEntry(); // standalone / non-map: snapshot + park now
            if (_handSlideTfs.Count == 0) return;
            float offX = _handSlideParkedOffX;
            DOTween.Kill("handSlide");
            ApplyHandSlideOffset(offX);
            DOTween.To(() => offX, ApplyHandSlideOffset, 0f, 0.52f)
                .SetEase(Ease.OutBack, 1.3f).SetId("handSlide")
                .OnComplete(() => { ApplyHandSlideOffset(0f); _handSlidePrepped = false; });
        }

        private void ApplyHandSlideOffset(float dx)
        {
            for (int i = 0; i < _handSlideTfs.Count; i++)
                if (_handSlideTfs[i] != null)
                    _handSlideTfs[i].position = _handSlideRests[i] + new Vector3(dx, 0f, 0f);
        }

        /// <summary>Level-entry UNIFIED slide: t=1 → parked off the right edge, t=0 → rest, t&lt;0 → overshoot left.
        /// Driven by the SAME shared tween as the board + SWAPS so the whole thing moves as one. 2026-07-13 Spencer.</summary>
        public void SetEntrySlideNorm(float t)
        {
            if (_handSlideTfs.Count == 0) PrepHandEntry(); // standalone: snapshot + park first
            ApplyHandSlideOffset(t * _handSlideParkedOffX); // _handSlideParkedOffX is the park distance
        }

        /// <summary>Snap the tile holder to exact rest + clear the prep guard. Called from the shared slide's OnComplete.</summary>
        public void EndEntrySlide()
        {
            ApplyHandSlideOffset(0f);
            _handSlidePrepped = false;
        }

        /// <summary>Level-EXIT prep: re-snapshot the CURRENT tile holder at rest WITHOUT parking, so the shared slide
        /// can drive it OUT to the right (norm 0→1) — the mirror of the entry. Re-snapshots because the cards changed
        /// during play. 2026-07-14 Spencer.</summary>
        public void PrepHandExit()
        {
            DOTween.Kill("handSlide");
            // Leave _handSlidePrepped FALSE — the next level's PrepHandEntry must run fully to re-snapshot the new
            // cards. SetEntrySlideNorm won't re-prep during the exit because the list below is non-empty.
            _handSlideTfs.Clear(); _handSlideRests.Clear();
            void Add(Transform tr) { if (tr != null) { _handSlideTfs.Add(tr); _handSlideRests.Add(tr.position); } }
            if (_controlTray != null) Add(_controlTray.transform);
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) Add(_cardObjects[i].transform);
                if (_cardShadows[i] != null) Add(_cardShadows[i].transform);
            }
            if (_nextTileSocket != null)  Add(_nextTileSocket.transform);
            if (_nextTilePreview != null) Add(_nextTilePreview.transform);
            if (_nextDivider != null)     Add(_nextDivider.transform);
            if (_nextTileLabel != null)   Add(_nextTileLabel.transform);
            _handSlideParkedOffX = 12f;
            var cam = Camera.main;
            if (cam != null) _handSlideParkedOffX = cam.orthographicSize * cam.aspect * 2.6f;
            // NOTE: no ApplyHandSlideOffset here — the holder stays at rest until the exit tween slides it out.
        }

        public void AnimateHandTilesIn(float speedMult = 1f)
        {
            if (!_handRestCached) return;
            float dur = HAND_GROUP_IN_DUR / Mathf.Max(0.001f, speedMult);

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) _cardObjects[i].transform.DOKill();
                if (_cardShadows[i] != null) _cardShadows[i].transform.DOKill();
            }
            if (_nextTilePreview != null) _nextTilePreview.transform.DOKill();

            _handTilesTween?.Kill();
            _handTilesTween = DOTween.To(() => _handTilesT, v =>
            {
                _handTilesT = v;
                ApplyHandTilesGroupTransform(v);
            }, 1f, dur).SetEase(Ease.OutBack, HAND_GROUP_OVERSHOOT);
        }

        private void ApplyHandTilesGroupTransform(float t)
        {
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null)
                {
                    var tr = _cardObjects[i].transform;
                    float deltaX = _cardRestPositions[i].x - _handCenterX;
                    tr.position = new Vector3(
                        _handCenterX + deltaX * t,
                        _cardRestPositions[i].y,
                        _cardRestPositions[i].z);
                    tr.localScale = _cardRestScales[i] * t;
                }
                if (_cardShadows[i] != null)
                {
                    var str = _cardShadows[i].transform;
                    float deltaX = _cardShadowRestPositions[i].x - _handCenterX;
                    str.position = new Vector3(
                        _handCenterX + deltaX * t,
                        _cardShadowRestPositions[i].y,
                        _cardShadowRestPositions[i].z);
                    str.localScale = _cardShadowRestScales[i] * t;
                }
            }
            if (_nextTilePreview != null)
            {
                var nt = _nextTilePreview.transform;
                float deltaX = _nextTileRestPosition.x - _handCenterX;
                nt.position = new Vector3(
                    _handCenterX + deltaX * t,
                    _nextTileRestPosition.y,
                    _nextTileRestPosition.z);
                nt.localScale = _nextTileRestScale * t;
            }
        }

        /// <summary>
        /// Updates all 5 card displays to show the given letters.
        /// Letters array should have length == HAND_SIZE.
        /// </summary>
        public void SetHand(char[] letters)
        {
            if (letters == null)
            {
                Debug.LogWarning("[HandManager] SetHand: letters array is null.");
                return;
            }

            int count = Mathf.Min(letters.Length, HAND_SIZE);
            for (int i = 0; i < count; i++)
                _hand[i] = letters[i];

            // Fill any remaining slots with '\0' if array is short
            for (int i = count; i < HAND_SIZE; i++)
                _hand[i] = '\0';

            RefreshAllCardVisuals();
        }

        /// <summary>
        /// Update a single card slot without rebuilding the entire hand.
        /// Used by the tutorial to avoid the "whole hand changed" visual glitch.
        /// </summary>
        public void UpdateSingleCard(int index, char letter)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            _hand[index] = letter;
            RefreshCardVisual(index);
        }

        /// <summary>
        /// Returns the letter on the currently selected card.
        /// Returns '\0' if no valid selection.
        /// </summary>
        public char GetSelectedLetter()
        {
            if (_selectedIndex < 0 || _selectedIndex >= HAND_SIZE)
                return '\0';
            return _hand[_selectedIndex];
        }

        public int GetSelectedIndex() => _selectedIndex;

        /// <summary>
        /// Enables or disables all card interaction.
        /// Also hides column arrows when disabled.
        /// </summary>
        public void SetInteractable(bool interactable)
        {
            IsInteractable = interactable;

            if (!interactable)
            {
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
            }
        }

        // ── Tutorial spotlight: raise the whole hand HOLDER (rack panel + cards + NEXT) above the dim scrim so
        //    it reads bright — but leave the bag button dimmed (not part of the beat). 2026-07-08 Spencer.
        private readonly System.Collections.Generic.Dictionary<Renderer, int> _handSpotlightSaved
            = new System.Collections.Generic.Dictionary<Renderer, int>();
        public void SetHandSpotlight(bool on)
        {
            if (on)
            {
                SetHandSpotlight(false); // clear any prior bump first
                var bagSet = new System.Collections.Generic.HashSet<Renderer>();
                if (_tileBagButton != null)
                    foreach (var br in _tileBagButton.GetComponentsInChildren<Renderer>(true)) bagSet.Add(br);
                foreach (var r in GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || bagSet.Contains(r)) continue; // bag stays under the dim
                    _handSpotlightSaved[r] = r.sortingOrder;
                    r.sortingOrder += 30; // above the scrim (15)
                }
            }
            else
            {
                foreach (var kv in _handSpotlightSaved) if (kv.Key != null) kv.Key.sortingOrder = kv.Value;
                _handSpotlightSaved.Clear();
            }
        }

        /// <summary>Get world X position of a hand card (for tutorial arrow).</summary>
        public float CardSize => _cardSize; // world-space card size, for the tutorial "dim unused cards" overlay
        public bool HasNextTile => _nextTilePreview != null;
        public Vector3 NextTileWorldPos => _nextTilePreview != null ? _nextTilePreview.transform.position : new Vector3(99999f, 99999f, 0f);

        // Tutorial: dim the WHOLE NEXT unit uniformly (socket + tile + label) by pushing it below the scrim, so it
        // reads as a properly-dimmed element instead of a bright tile on a dimmed socket. 2026-07-08 Spencer.
        private readonly System.Collections.Generic.Dictionary<Renderer, int> _nextDimSaved
            = new System.Collections.Generic.Dictionary<Renderer, int>();
        public void SetNextTileDimmed(bool dim)
        {
            if (dim)
            {
                SetNextTileDimmed(false); // clear any prior bump first
                BumpNextTree(_nextTileSocket);
                BumpNextTree(_nextTilePreview);
            }
            else
            {
                foreach (var kv in _nextDimSaved) if (kv.Key != null) kv.Key.sortingOrder = kv.Value;
                _nextDimSaved.Clear();
            }
        }
        private void BumpNextTree(GameObject root)
        {
            if (root == null) return;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || _nextDimSaved.ContainsKey(r)) continue;
                _nextDimSaved[r] = r.sortingOrder;
                r.sortingOrder -= 8; // below the tutorial scrim (9) — whole unit dims together
            }
        }
        public float GetCardWorldX(int index) => GetCardX(index);

        /// <summary>Get world Y position of the hand card row (for tutorial arrow).</summary>
        public float GetCardWorldY() => GetCardRowY();

        /// <summary>Tutorial: force-select a card by index.</summary>
        public void ForceSelectCard(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            _selectedIndex = index;
            RefreshAllCardVisuals();
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);
        }

        /// <summary>
        /// Activates swap mode. In swap mode, tapping a card swaps it
        /// (calls MatchController.UseSwap) instead of selecting it.
        /// </summary>
        public void ShowSwapButton(bool active)
        {
            _swapModeActive = active;
            RefreshAllCardVisuals();
//             WDLog.Log($"[HandManager] Swap mode: {active}");
        }

        // ── Input handling ────────────────────────────────────────────────────────

        // ── Drag state machine ──────────────────────────────────────────
        private enum InputMode { Idle, PressedCard, CarryToBoard, Reordering }
        private InputMode _inputMode = InputMode.Idle;
        private int   _touchCardIndex = -1;
        private Vector3 _touchStartWorld;
        private Vector2 _touchStartScreen;
        private float _intentSlopPx = 14f;
        private const float DROP_LOCK_RATIO = 1.1f;
        private const float REORDER_LOCK_RATIO = 1.25f;

        // Hand reordering is HARD-LOCKED on gated tutorial levels (e.g. L5 EDIT). CurrentLevelIsGated is timing-
        // independent — it covers the WHOLE level, including the entry-anim window BEFORE the coaching dim fires,
        // which BlockShuffleAndSwap (only set once the gate starts) does not. Gating the reorder ENTRY points here
        // means the rack can never even lock into reorder mode, so nothing shuffles visually either. 2026-07-13.
        private static bool HandReorderLocked =>
            TutorialManager.BlockShuffleAndSwap || TutorialManager.EditGateActive || TutorialManager.CurrentLevelIsGated;

        // ── Legacy drag compat ──
        private int  _dragIndex = -1;
        private bool _isDragging = false;
        private float _dragStartX;

        // ── Long-press swap state ──────────────────────────────────────────
        private const float LONG_PRESS_TIME = 0.5f;
        private float _holdTimer = 0f;
        private int   _holdIndex = -1;
        private bool  _holdTriggered = false;
        private int   _swapConfirmIndex = -1; // -1 = no confirmation pending
        private GameObject _swapLabel;        // "SWAP?" floating label
        // _rewriteLabel removed — rewrite is now triggered by board tap

        // ── Rewrite mode state ──────────────────────────────────────────
        private const float REWRITE_TIMEOUT = 5f; // auto-cancel after 5s to prevent pause abuse
        private bool  _rewriteModeActive = false;
        private int   _rewriteTargetCol = -1;
        private int   _rewriteTargetRow = -1;
        private float _rewriteTimeoutTimer = 0f;
        /// <summary>2026-09-03: the SELECTED tile itself, not just its coordinates.
        /// Rising rows shift every tile up a row, so the stored col/row goes stale almost
        /// immediately — CancelRewriteMode was then clearing the glow on whatever tile had
        /// moved INTO that cell (or nothing), leaving the real selection glowing forever
        /// and pointing the swap at the wrong cell.</summary>
        private Tile  _rewriteTargetTile;

        // ── INPUT BUFFER (2026-09-03) ────────────────────────────────────────────────
        // Rows now rise on nearly every move, and input is blocked for the whole rise
        // animation — so a tap landing during one was silently DROPPED and the player
        // had to tap again. Standard match-3 fix: remember a press that arrives while
        // blocked and replay it the moment input frees up (if still fresh).
        private Vector3 _bufferedPressWorld;
        private float   _bufferedPressAt = -1f;
        private const float INPUT_BUFFER_WINDOW = 0.45f;   // seconds a buffered tap stays valid

        /// <summary>
        /// Called after a rising row shifts the board up by 1.
        /// Updates the rewrite target so it follows the selected tile.
        /// </summary>
        public void OnBoardShiftedUp()
        {
            if (_rewriteModeActive && _rewriteTargetRow >= 0)
            {
                // Clear edit-selected visual on old tile
                Tile oldTile = _grid != null ? _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow) : null;
                if (oldTile != null) { oldTile.SetEditSelected(false); oldTile.ResetVisuals(); }

                _rewriteTargetRow += 1;

                // If shifted off the top, cancel rewrite
                if (_rewriteTargetRow >= RulesEngine.ROWS)
                {
//                     WDLog.Log("[HandManager] Rewrite target shifted off board — cancelling");
                    CancelRewriteMode();
                    return;
                }

                // Re-apply highlight to tile at new position
                Tile newTile = _grid != null ? _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow) : null;
                if (newTile != null)
                {
                    // Re-apply the edit-selected visual so the halo + breath
                    // follow the tile to its new row after the shift.
                    newTile.SetEditSelected(true);
                }
                else
                {
                    // Tile disappeared during shift — cancel
                    CancelRewriteMode();
                    return;
                }

//                 WDLog.Log($"[HandManager] Rewrite target shifted up → ({_rewriteTargetCol},{_rewriteTargetRow})");
            }
        }
        public  bool IsRewriteModeActive => _rewriteModeActive;
        private int  _rewriteMatchRewriteCount = 0; // debug: total rewrites this match

        // ── Wild Tiles Phase C — per-resolution injection cap ───────────────────────
        // Multiple injection triggers (wild-refill tile detonation + chain depth reward)
        // can fire during one resolution pass. Only the first one lands.
        private bool _wildInjectedThisResolution = false;

        // Wall-clock time (unscaled) the wild BECAME VISIBLE in hand. Anchored by
        // TickWildExpiry on the first frame HasWild is true (NOT when queued via
        // TryInjectWildReward — the chain resolution + popup + refill delay can
        // eat several seconds before the wild actually appears in a slot, which
        // used to expire the wild almost immediately after it showed up).
        private float _wildInjectedAt = -1f;
        private const float WILD_EXPIRY_SECONDS = 20f; // generous — player needs time to plan a wild drop
        private const int   WILD_EXPIRY_DROPS   = 3;
        private bool  _wildExpiryPaused = false;
        private float _wildExpiryPauseStarted = -1f;

        // ── Swap Tile confirmation popup ──
        private bool _swapTileConfirmActive = false;
        private GameObject _swapTilePopup;
        private GameObject _swapTileYesLabel;
        private GameObject _swapTileNoLabel;

        // (bag button triggers hand swap directly, board rewrite is a tap on a board tile)

        // 2026-06-04 Spencer: re-applies the Inspector wild-shadow values to every active
        // wild card shadow each frame so the look updates live in Play mode. A shadow
        // whose sprite == _spriteWildShadow is, by construction, a wild card slot
        // (RefreshCardVisual only assigns that sprite to wild cards), so it's safe to
        // drive enable/offset/scale from here without re-checking emptiness.
        private void ApplyWildShadowTuningLive()
        {
            if (_cardContactShadow == null || _spriteWildShadow == null) return;
            if (_wildShadowMat != null) _wildShadowMat.SetFloat("_Strength", _wildShadowStrength);
            for (int s = 0; s < _cardContactShadow.Length; s++)
            {
                var sgo = _cardContactShadow[s];
                if (sgo == null) continue;
                var ssr = sgo.GetComponent<SpriteRenderer>();
                if (ssr == null || ssr.sprite != _spriteWildShadow) continue; // wild shadows only
                if (!_wildShadowEnabled) { if (sgo.activeSelf) sgo.SetActive(false); continue; }
                if (!sgo.activeSelf) sgo.SetActive(true);
                var t = sgo.transform;
                t.localPosition = new Vector3(_wildShadowOffset.x, _wildShadowOffset.y, 0.04f);
                t.localScale    = Vector3.one * _wildShadowScale;
            }
        }

        /// <summary>2026-06-05 Spencer: builds a card-shadow sprite from a bare texture name
        /// in Resources/Tiles, full-frame at the given PPU (matches the main card shadow).</summary>
        private Sprite MakeShadowSprite(string texName, float ppu)
        {
            if (string.IsNullOrEmpty(texName)) return null;
            // Bare name → look in Tiles/; a name with a slash is treated as a full
            // Resources path (e.g. "menu psds/shadowy").
            string path = texName.Contains("/") ? texName : "Tiles/" + texName;
            Texture2D t = Resources.Load<Texture2D>(path);
            if (t == null) return null;
            return Sprite.Create(t, new Rect(0, 0, t.width, t.height), new Vector2(0.5f, 0.5f), ppu);
        }

        /// <summary>2026-06-05 Spencer: A/B shadow compare. Each frame, pushes the selected
        /// variant (A or B) onto every visible NON-wild card shadow, with its live strength,
        /// so flipping _shadowUseB (Inspector or the \ key) swaps the whole board in place.</summary>
        private void ApplyShadowABLive()
        {
            if (_cardContactShadow == null) return;
            if (_shadowMatA == null || _shadowMatB == null)
            {
                Shader msh = Shader.Find("WordDrop/MultiplySprite");
                if (msh == null) return;
                if (_shadowMatA == null) _shadowMatA = new Material(msh);
                if (_shadowMatB == null) _shadowMatB = new Material(msh);
            }
            _shadowMatA.SetFloat("_Strength", _shadowStrengthA);
            _shadowMatB.SetFloat("_Strength", _shadowStrengthB);

            Sprite   spr = _shadowUseB ? _shadowSpriteB : _shadowSpriteA;
            Material mat = _shadowUseB ? _shadowMatB    : _shadowMatA;
            if (spr == null) return;
            for (int i = 0; i < _cardContactShadow.Length; i++)
            {
                var go = _cardContactShadow[i];
                if (go == null || !go.activeSelf) continue; // wild/empty shadows are inactive → skipped
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr == null || sr.sprite == _spriteWildShadow) continue; // never touch a wild shadow
                sr.sprite         = spr;
                sr.sharedMaterial = mat;
            }
        }

        private void Update()
        {
            if (_grid == null) return;

            // 2026-06-04 Spencer: live wild-shadow tuning — push the Inspector values to
            // any active wild card shadow EVERY frame so dragging the sliders in Play mode
            // updates the look instantly, no recompile + no card refresh needed.
            ApplyWildShadowTuningLive();

            // 2026-06-05 Spencer: A/B shadow compare — the \ key flips A↔B in place.
            if (Input.GetKeyDown(KeyCode.Backslash)) _shadowUseB = !_shadowUseB;
            ApplyShadowABLive();

            // Phase C: wild expiry. Fires on 3-drop count OR 20s playable-time elapsed,
            // whichever comes first. Drop count is tracked in PlayerHand.DrawSlot.
            TickWildExpiry();

            Vector3 screenPos = Vector3.zero;
            bool mouseDown = WDInput.GetMouseButtonDown(0);
            bool mouseHeld = WDInput.GetMouseButton(0);
            bool mouseUp   = WDInput.GetMouseButtonUp(0);

            if (WDInput.touchCount > 0)
            {
                Touch touch = WDInput.GetTouch(0);
                screenPos = touch.position;
                mouseDown = (touch.phase == TouchPhase.Began);
                mouseHeld = (touch.phase == TouchPhase.Moved || touch.phase == TouchPhase.Stationary);
                mouseUp   = (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled);
            }
            else
            {
                screenPos = WDInput.mousePosition;
            }
            if (BoosterIntro.Running || TutorialBeats.Pending) mouseDown = false;   // booster lesson up / a lesson about to appear (11-17): no new board press starts, mouse or touch

            screenPos.z = Mathf.Abs(_cam.transform.position.z);
            Vector3 worldPos = _cam.ScreenToWorldPoint(screenPos);

            // Tap-to-swap mode intercept: if active, any card tap routes to
            // PerformTapToSwap and we short-circuit the rest of Update so the
            // normal drag/drop logic doesn't also fire. The X cancel button
            // and the scrim live on a UI canvas owned by BoosterHUDSlot, so
            // taps on them are absorbed there before reaching this loop.
            if (TapToSwapModeActive)
            {
                if (mouseDown)
                {
                    int tappedCard = GetCardIndexAtPosition(worldPos);
                    if (tappedCard >= 0)
                    {
                        PerformTapToSwap(tappedCard);
                    }
                }
                return;
            }

            UpdateSelectedCardShadow();

            // Block ALL input when not interactable or during processing (rising rows, chain resolution).
            // Also block when LevelController has locked input (Level Complete / Out of Moves modal
            // is up) — otherwise the FullTurnSequence re-enable at the end of the drop resolution
            // coroutine can unlock input AFTER FireComplete/FireFail fired, letting SHUFFLE/drag
            // still work while a terminal modal is on screen.
            bool levelLocked = LevelController.Instance != null && LevelController.Instance.IsInputLocked;
            // Overlay pause gate (StageClearModal etc.) — block input while any
            // full-screen overlay is freezing Survival timers. Independent of
            // IsInteractable so the modal doesn't have to race with the hand
            // coroutine's own state ownership.
            bool overlayPaused = SurvivalManager.Instance != null && SurvivalManager.Instance.IsOverlayPaused;
            // 2026-06-01: also block hand input while a booster is in aim
            // mode (Bloomburst, Comet, Jester Hat, Stone Splitter). Otherwise
            // the player could drag-drop a hand card to a column while
            // simultaneously trying to aim at a board tile, doubling-up the
            // turn. The scrim that BoosterHUDSlot puts up makes the hand row
            // visually dim, but the input gate is what actually disables it.
            bool aimModeActive = BoosterManager.Instance != null
                                 && BoosterManager.Instance.AimMode;
            // 2026-07-29: gated tutorial levels. Between scripted steps nothing is armed, and
            // the coroutines below re-enable IsInteractable when a drop finishes resolving —
            // so relying on the tutorial's SetInteractable(false) alone left a window where a
            // stray drop could go in and desync the script. Same reasoning as levelLocked:
            // this must be checked independently of IsInteractable.
            bool tutorialGateClosed = TutorialManager.GateClosed;
            // The board is parked off-screen or sliding in — nothing on screen is where it
            // will be, so a tap here is meaningless. 2026-09-09 Spencer.
            bool boardEntering = GridManager.Instance != null && GridManager.Instance.BoardEntryInProgress;
            // Belt and braces (2026-10-23): never take board input while the map/home screen covers it, whatever the pause
            // flag says — Settings > Quit level left the pause cleared under the home screen and the hidden board took moves.
            bool mapCovering = LevelMapPanel.Instance != null && LevelMapPanel.Instance.BlocksBoard;
            if (boardEntering || mapCovering ||
                !IsInteractable ||
                levelLocked ||
                overlayPaused ||
                aimModeActive ||
                tutorialGateClosed ||
                (MatchController.Instance != null && MatchController.Instance.IsProcessing))
            {
                // Buffer a press that arrives while input is blocked so it is not lost —
                // EXCEPT during board entry. The buffer exists so a tap during a rise or a
                // detonation still counts; a tap while the board is flying in is not a move
                // the player meant, and replaying it selected a tile the instant the board
                // landed. Drop it instead, and clear anything already held. 2026-09-09.
                if (boardEntering) { _bufferedPressAt = -1f; }
                else if (mouseDown)
                {
                    _bufferedPressWorld = worldPos;
                    _bufferedPressAt    = Time.unscaledTime;
                }
                if (DropPreview.Instance != null)
                    DropPreview.Instance.ClearPreview();
                if (_inputMode != InputMode.Idle)
                    CancelCurrentGesture();
                return;
            }

            // Input is free again — replay a recent buffered press as if it just happened.
            if (!mouseDown && _bufferedPressAt > 0f)
            {
                bool fresh = (Time.unscaledTime - _bufferedPressAt) <= INPUT_BUFFER_WINDOW;
                Vector3 bufWorld = _bufferedPressWorld;
                _bufferedPressAt = -1f;
                if (fresh)
                {
                    mouseDown = true;
                    worldPos  = bufWorld;
                }
            }

            bool risingRowActive = SurvivalManager.Instance != null && SurvivalManager.Instance.IsRisingRow;

            // ── ARMED-WEB: tap a primed tile → detonate its connected cluster ──────────
            // First thing after the input gate, before the drag/swap state machine, so a
            // press on a primed tile can never be consumed as the start of a swap.
            // A rise does not always trip the gate above, so buffer here too rather than
            // dropping the press — this was the "had to tap twice" case.
            if (mouseDown && risingRowActive)
            {
                _bufferedPressWorld = worldPos;
                _bufferedPressAt    = Time.unscaledTime;
            }

            if (RulesEngine.ArmedWebMode && mouseDown && !_rewriteModeActive && !risingRowActive)
            {
                Vector2Int tapCell = _grid.WorldToCell(worldPos);
                if (tapCell.x >= 0 && tapCell.y >= 0)
                {
                    int primedHere = RulesEngine.Instance != null
                        ? RulesEngine.Instance.GetPrimedWordsContainingPublic(tapCell) : -1;
                    if (TryTapDetonate(tapCell)) return;
                }
            }

            // Rewrite mode timeout — auto-cancel to prevent indefinite pause
            if (_rewriteModeActive && !TutorialManager.EditGateActive) // no auto-timeout during the L5 edit tutorial
            {
                _rewriteTimeoutTimer -= Time.deltaTime;
                if (_rewriteTimeoutTimer <= 0f)
                {
                    CancelRewriteMode();
                }
                // The selected tile can be destroyed mid-selection (detonation, top-out).
                // Without this the mode stays "on" with nothing selected.
                else if (_rewriteTargetTile == null)
                {
                    CancelRewriteMode();
                }
            }

            // ── STATE MACHINE ──────────────────────────────────────────

            switch (_inputMode)
            {
                // ── IDLE ────────────────────────────────────────────────
                case InputMode.Idle:
                {
                    if (!mouseDown) break;

                    // Modal states first
                    if (_rewriteModeActive)
                    {
                        // 2026-10-20 cleanup: the "tap a hand card → rewrite" branch was removed
                        // (no hand cards are ever built, so it could not fire). Block kept as-is.
                        {
                            // Check if tapped any board tile → Board Swap (any two regular tiles)
                            // Rising rows / gravity may have moved the SELECTED tile since it
                            // was picked, so re-read its cell before using the coords.
                            RefreshRewriteTarget();
                            Vector2Int boardTap = _grid.WorldToCell(worldPos);
                            // EDIT-gated tutorial: lock the board-swap to the script.
                            //  • Gated STAMP step (AllowedEditCol>=0, no swap target): board-swap is
                            //    FORBIDDEN — the player must stamp the scripted letter, not swap the
                            //    target tile with a neighbour (Spencer 2026-07-09: was able to swap an
                            //    N during the HAND step because AllowedSwapCol<0 read as "unrestricted").
                            //  • Gated SWAP step (AllowedSwapCol>=0): allow ONLY the scripted 2nd tile.
                            //  • Outside the tutorial (AllowedEditCol<0): swaps are unrestricted.
                            bool gatedEdit  = TutorialManager.AllowedEditCol >= 0;
                            bool swapGateOk = gatedEdit
                                ? (TutorialManager.AllowedSwapCol >= 0
                                   && boardTap.x == TutorialManager.AllowedSwapCol
                                   && boardTap.y == TutorialManager.AllowedSwapRow)
                                : true;
                            // Teal glow + button-push on the swap TARGET the instant it's tapped — the source tile
                            // already glowed when it was selected; now the 2nd tile matches. Persistent (not a
                            // quick flash) so BOTH tiles keep the magical teal glow through the exchange wobble,
                            // then fade to white at the end (FadeEditGlowOut in BoardSwapTurnSequence). If the swap
                            // is invalid, CancelRewriteMode's normal cleanup handles it. 2026-07-10 Spencer.
                            if (TAP_TO_SWAP && boardTap.x >= 0 && boardTap.y >= 0 && swapGateOk
                                && !(boardTap.x == _rewriteTargetCol && boardTap.y == _rewriteTargetRow)
                                && CanSelectForSwap(boardTap.x, boardTap.y)
                                && (!AdjacentSwapsOnly || IsAdjacent(_rewriteTargetCol, _rewriteTargetRow, boardTap.x, boardTap.y)))
                                _grid.GetTile(boardTap.x, boardTap.y)?.SetEditSelected(true);
                            if (TAP_TO_SWAP && boardTap.x >= 0 && boardTap.y >= 0
                                && !(boardTap.x == _rewriteTargetCol && boardTap.y == _rewriteTargetRow)
                                && swapGateOk
                                && (TryBoardSwap(_rewriteTargetCol, _rewriteTargetRow, boardTap.x, boardTap.y)
                                    || TryBoardMoveToEmpty(_rewriteTargetCol, _rewriteTargetRow, boardTap.x, boardTap.y)))
                            {
                                // Swap succeeded — rewrite mode exits inside TryBoardSwap
                            }
                            else
                            {
                                // DOUBLE-TAP A SPECIAL TO FIRE IT, for one move (2026-10-21 Spencer: "should we add something where
                                // you dont actually need to make another word with it to detonate? like a double tap"). Royal Match
                                // charges a move for tapping a booster piece too. Single tap still selects, so specials can be swapped.
                                if (InstantPopMode && boardTap.x == _rewriteTargetCol && boardTap.y == _rewriteTargetRow
                                    && Time.unscaledTime - _selectedAt <= DOUBLE_TAP_WINDOW && !SurvivalManager.AwaitingNextLevel && !BoosterManager.CascadeBusy
                                    && (SurvivalManager.Instance == null || !SurvivalManager.Instance.OutOfMovesLocked)   // 11-17 monkey 436301: double-tapped power-ups after the last move -> "moves used 12 past the budget 10"
                                    && TutorialBeats.AllowsCell(boardTap))   // 11-17: a lesson is up -> only its own tiles (monkey 98181: a Royal Orb double-tapped on the DIMMED board cleared letters and the swipe lesson's word PIKE fell apart)
                                {
                                    var sc = RulesEngine.Instance != null ? RulesEngine.Instance.GetCell(boardTap.x, boardTap.y) : null;
                                    // TNT is NOT double-tappable (2026-10-21 Spencer: "if i just double tap the dynamite ones they
                                    // instantly blow up"). Earned specials are a reward you spend; placed TNT is a target — it goes
                                    // off only when its letter is used in a word, or another blast reaches it.
                                    if (sc != null && sc.SpecialKind == RulesEngine.SPECIAL_TNT)
                                    {
                                        CancelRewriteMode();
                                        GridManager.Instance?.GetTile(boardTap.x, boardTap.y)?.ShakeNo();
                                        var tp = GridManager.Instance != null ? GridManager.Instance.CellToWorld(boardTap.x, boardTap.y) : Vector3.zero;
                                        BonusPopup.Instance?.Show("USE IT IN A WORD!", new Color(1f, 0.6f, 0.3f, 1f), tp + Vector3.up * 0.8f, 0.9f);
                                        WDLog.Log($"[Special] TNT double-tap refused at ({boardTap.x},{boardTap.y}) — needs a word or a blast");
                                        return;
                                    }
                                    if (sc != null && sc.SpecialKind != RulesEngine.SPECIAL_NONE)
                                    {
                                        CancelRewriteMode();
                                        if (FireSpecialAt(boardTap) && SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                                            && SurvivalManager.Instance.EditsCountAsMoves)
                                            SurvivalManager.Instance.NotifyDropCommitted();   // costs a move
                                        return;
                                    }
                                }
                                // Touching-only: a tap on a tile that isn't next to the selection
                                // means "I meant this one" — move the selection there.
                                bool reselect = AdjacentSwapsOnly && !gatedEdit
                                    && boardTap.x >= 0 && boardTap.y >= 0
                                    && !IsAdjacent(_rewriteTargetCol, _rewriteTargetRow, boardTap.x, boardTap.y)
                                    && !(boardTap.x == _rewriteTargetCol && boardTap.y == _rewriteTargetRow);
                                CancelRewriteMode();
                                if (reselect) TryEnterRewriteMode(boardTap.x, boardTap.y);
                            }
                        }
                        return;
                    }

                    if (_swapConfirmIndex >= 0)
                    {
                        int tappedForSwap = GetCardIndexAtPosition(worldPos);
                        if (tappedForSwap == _swapConfirmIndex)
                            ExecuteSwap(_swapConfirmIndex);
                        CancelSwapConfirmation();
                        return;
                    }

                    if (_swapTileConfirmActive)
                    {
                        if (_swapTileYesLabel != null && IsWorldPosNearObject(_swapTileYesLabel, worldPos, _cardSize * 0.5f))
                        {
                            int savedCard = _selectedIndex;
                            DismissSwapTilePopup();
                            if (savedCard >= 0) { ExecuteSwap(savedCard); _selectedIndex = -1; }
                            return;
                        }
                        DismissSwapTilePopup();
                        return;
                    }

                    // Action row buttons
                    if (TryHandleShuffleButton(worldPos)) return;
                    if (_selectedIndex >= 0 && TryHandleTileBagButton(worldPos))
                    {
                        ShowSwapTilePopup();
                        return;
                    }

                    // Tapping the board with a selected card no longer drops — drag only
                    // Clear selection if player taps the board
                    if (_selectedIndex >= 0 && worldPos.y >= _grid.GridBottom - _grid.CellSize * 0.5f)
                        _selectedIndex = -1;

                    // Tap on board → enter rewrite mode immediately (blocked during rising row)
                    Vector2Int boardCell = _grid.WorldToCell(worldPos);
                    if (boardCell.x >= 0 && boardCell.y >= 0 && !risingRowActive)
                    {
                        // NO TAP-TO-SELECT (2026-10-23 Spencer: "can we remove being able to click a tile and then another to swap
                        // and that click sound ... follow royal match and have the tile light up with a faint glow when you are
                        // pushing down on it instead"). Swaps are drag-only; the press glow lives in SwipeWordInput. A POWER-UP
                        // still takes the first tap so double-tap-to-fire works. TAP_TO_SWAP = true restores the old select.
                        var tc = RulesEngine.Instance != null ? RulesEngine.Instance.GetCell(boardCell.x, boardCell.y) : null;
                        if (TAP_TO_SWAP || (tc != null && tc.SpecialKind != RulesEngine.SPECIAL_NONE))
                            TryEnterRewriteMode(boardCell.x, boardCell.y);
                    }

                    break;
                }
            }
        }

        /// <summary>Cancel any in-progress gesture and reset to Idle.</summary>
        private void CancelCurrentGesture()
        {
            if (_inputMode == InputMode.CarryToBoard)
            {
                if (DropPreview.Instance != null)
                    DropPreview.Instance.ClearPreview();
                SnapCardBack(_touchCardIndex);
                _selectedIndex = -1; // Clear selection so tapping the board doesn't drop this card
            }
            else if (_inputMode == InputMode.Reordering)
            {
                HandleDragEnd();
            }

            _inputMode = InputMode.Idle;
            _touchCardIndex = -1;
            _dragIndex = -1;
            _isDragging = false;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);
        }

        /// <summary>Snap a card back to its resting position after a cancelled carry.</summary>
        private void SnapCardBack(int index)
        {
            if (index < 0 || index >= HAND_SIZE || _cardObjects[index] == null) return;
            RestoreAllCardSortOrder();
            float baseY = GetCardRowY();
            _cardObjects[index].transform.position = new Vector3(GetCardX(index), baseY, -1f);
            _cardObjects[index].transform.localScale = GetCardBaseScale();
            // Reset sprite back to rest state (wild slots return to wild sprite)
            SpriteRenderer csr = _cardObjects[index].GetComponent<SpriteRenderer>();
            if (csr != null)
            {
                csr.sprite = GetSlotRestSprite(index);
                csr.color  = Color.white; // clear the green drag tint (1132) so a cancelled drag doesn't leave the card green. 2026-06-10
            }
            // Reset shadow to rest state
            if (index < HAND_SIZE && _cardShadows[index] != null)
            {
                _cardShadows[index].color = new Color(0f, 0f, 0f, 0.15f);
                _cardShadows[index].transform.position = new Vector3(
                    GetCardX(index), baseY - _cardSize * 0.03f, 0f);
            }
        }

        private int GetCardIndexAtPosition(Vector3 worldPos)
        {
            float halfCard = _cardSize * 0.5f;
            float cardY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float cardX = _cardObjects[i].transform.position.x;
                float cardActualY = _cardObjects[i].transform.position.y;

                bool inX = worldPos.x >= cardX - halfCard && worldPos.x <= cardX + halfCard;
                bool inY = worldPos.y >= cardActualY - halfCard && worldPos.y <= cardActualY + halfCard;

                if (inX && inY) return i;
            }
            return -1;
        }

        private void HandleDragEnd()
        {
            bool wasDragging = _isDragging; // did user actually move past drag threshold?
            _isDragging = false;
            _dragIndex = -1;
            RestoreAllCardSortOrder();

            if (wasDragging)
            {
                // Actual drag happened — deselect and snap all cards to rest
                _selectedIndex = -1;
                HideAllCardShadows();
                float baseY = GetCardRowY();
                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    _cardObjects[i].transform.position = new Vector3(GetCardX(i), baseY, -1f);
                    _cardObjects[i].transform.localScale = GetCardBaseScale();
                }
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
            }
            // If not dragging, it was just a tap — keep the selection from SelectCard
        }

        // ── Swap confirmation ────────────────────────────────────────────────

        private void TriggerSwapConfirmation(int cardIndex)
        {
            if (MatchController.Instance == null) return;
            int swapsLeft = MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN);
            if (swapsLeft <= 0)
            {
//                 WDLog.Log("[HandManager] No swaps remaining.");
                return;
            }

            _swapConfirmIndex = cardIndex;

            // Raise the tile higher to indicate swap mode
            if (_cardObjects[cardIndex] != null)
            {
                float baseY = GetCardRowY();
                _cardObjects[cardIndex].transform.position = new Vector3(
                    GetCardX(cardIndex), baseY + CARD_SELECT_RAISE * 1.8f, -1f);
            }

            // Show "SWAP?" label above the raised card
            if (_swapLabel != null) Destroy(_swapLabel);
            _swapLabel = CreateConfirmLabel("SWAP?",
                GetCardX(cardIndex),
                GetCardRowY() + CARD_SELECT_RAISE * 1.8f + _cardSize * 0.7f,
                Color.white);

//             WDLog.Log($"[HandManager] Swap confirmation triggered for card {cardIndex} ({swapsLeft} swaps left)");
        }

        private GameObject CreateConfirmLabel(string text, float x, float y, Color color)
        {
            GameObject go = new GameObject(text.Replace("?", "Label"));
            go.transform.position = new Vector3(x, y, -2f);

            TextMesh tm = go.AddComponent<TextMesh>();
            tm.text = text;
            tm.anchor = TextAnchor.MiddleCenter;
            tm.alignment = TextAlignment.Center;
            tm.fontSize = 42;
            tm.characterSize = 0.07f;
            tm.fontStyle = FontStyle.Bold;
            tm.color = color;
            GameFont.ApplyUI(tm);

            MeshRenderer mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 25;

            return go;
        }

        // IsWorldPosNearLabel removed — no longer needed

        private void CancelSwapConfirmation()
        {
            _swapConfirmIndex = -1;
            if (_swapLabel != null) { Destroy(_swapLabel); _swapLabel = null; }

            // Snap card back down
            float baseY = GetCardRowY();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), targetY, -1f);
            }
        }

        private void ExecuteSwap(int cardIndex)
        {
            if (TutorialManager.BlockShuffleAndSwap) return;

            // One-time contextual hint for first swap
            if (PlayerPrefs.GetInt("hint_swap", 0) == 0)
            {
                PlayerPrefs.SetInt("hint_swap", 1);
                PlayerPrefs.Save();
                if (BonusPopup.Instance != null)
                {
                    float cardX = GetCardWorldX(cardIndex);
                    float cardY = GetCardWorldY();
                    BonusPopup.Instance.Show("TRADE A CARD", new Color(0.3f, 0.85f, 0.9f, 1f),
                        new Vector3(cardX, cardY + 0.5f, -5f), 1.1f);
                }
            }

            if (MatchController.Instance == null) return;
            bool success = MatchController.Instance.UseSwap(cardIndex);
            if (success)
            {
//                 WDLog.Log($"[HandManager] Swap executed on card {cardIndex}");
                RefreshHandFromMatchController();
                RefreshAllCardVisuals();
            }
        }

        // ── Rewrite Tile ─────────────────────────────────────────────────────

        /// <summary>
        /// Called when a board tile is tapped. Validates it's a valid
        /// rewrite target and enters rewrite mode if so.
        /// </summary>
        private void TryEnterRewriteMode(int col, int row)
        {
            if (TutorialLocks.EditLocked) return;   // edit is locked until it's taught (L2+)
            if (TutorialManager.BlockShuffleAndSwap) return;
            // EDIT-gated tutorial: ONLY the scripted target tile may enter rewrite mode. 2026-07-07 Spencer.
            if (TutorialManager.AllowedEditCol >= 0
                && (col != TutorialManager.AllowedEditCol || row != TutorialManager.AllowedEditRow)) return;
            if (MatchController.Instance == null || RulesEngine.Instance == null) return;

            // 2026-06-01: tile taps during booster aim mode are RESERVED for
            // the armed booster's target — they must not also enter rewrite
            // mode (which would turn the tile cyan and consume an edit charge).
            // Spencer caught this with the Jester Hat: tapping a tile to
            // confirm the shuffle was simultaneously starting a rewrite-target
            // selection. Bail early so the tap is exclusively the booster
            // target. BoosterHUDSlot's ResolveAim path handles the actual
            // booster resolution.
            if (BoosterManager.Instance != null && BoosterManager.Instance.AimMode)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: booster aim mode is active");
                return;
            }

            // The level is already won — see the note in SwipeWordInput. The board stays up
            // for a beat before the clear modal arrives; it must not be playable in that gap.
            if (SurvivalManager.AwaitingNextLevel)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: level already won — waiting for the next one");
                return;
            }

            int rewritesLeft = MatchController.Instance.GetRewritesRemaining(MatchController.PLAYER_HUMAN);
            if (rewritesLeft <= 0)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: NO EDIT CHARGES LEFT");
//                 WDLog.Log("[HandManager] Rewrite: no rewrites remaining.");
                return;
            }

            var cell = RulesEngine.Instance.GetCell(col, row);
            if (cell == null)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: empty cell — no data here");
                return;
            }

            // Gold and stone tiles cannot be rewritten
            Tile existingTile = _grid.GetTile(col, row);
            if (existingTile != null && existingTile.IsGoldBonus)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: gold bonus tile");
//                 WDLog.Log($"[HandManager] Rewrite: tile at ({col},{row}) is gold — cannot rewrite.");
                return;
            }
            if (cell.IsStone)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: stone");
//                 WDLog.Log($"[HandManager] Rewrite: tile at ({col},{row}) is stone — cannot rewrite.");
                return;
            }
            // Frozen (ice) tiles are LOCKED — can't rewrite their letter until the ice thaws.
            // Else you'd trivially edit your way around the clear-the-ice challenge. 2026-06-15 Spencer.
            if (cell.IsFrozen)
                return;

            // Special NON-LETTER tiles can't be edited — editing them would break their mechanic (or hand you a
            // free letter). Treasure chests / anchored rocks are IsAnchored (the chest's IsStone lives on the tile
            // visual, not the cell, so the cell.IsStone check above misses them); escort chickens are IsDropTarget.
            // 2026-07-10 Spencer.
            if (cell.IsAnchored || cell.IsDropTarget)
                return;
            if (existingTile != null && existingTile.IsVault) // belt-and-suspenders: any tile drawn as a chest
                return;

            var primed = RulesEngine.Instance.PrimedRegistry
                .GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primed != null && primed.Count > 0)
            {
                WDLog.Log($"[TapBlocked] ({col},{row}) refused: tile is in an ARMED word");
//                 WDLog.Log($"[HandManager] Rewrite: tile at ({col},{row}) is primed.");
                return;
            }

            // One-time contextual hint for first rewrite (only after validation passes)
            // Old EDIT-feature hint ("REPLACE A TILE") — wrong in the swap game, where selecting a tile starts a SWAP.
            if (!InstantPopMode && PlayerPrefs.GetInt("hint_rewrite", 0) == 0)
            {
                PlayerPrefs.SetInt("hint_rewrite", 1);
                PlayerPrefs.Save();
                if (BonusPopup.Instance != null && GridManager.Instance != null)
                {
                    Vector3 worldPos = GridManager.Instance.CellToWorld(col, row);
                    BonusPopup.Instance.Show("REPLACE A TILE", new Color(0.3f, 0.85f, 0.9f, 1f), worldPos, 1.1f);
                }
            }

            _rewriteModeActive = true;
            _rewriteTargetCol = col;
            _rewriteTargetRow = row;
            _rewriteTargetTile = _grid != null ? _grid.GetTile(col, row) : null;
            _rewriteTimeoutTimer = REWRITE_TIMEOUT;
            _selectedAt = Time.unscaledTime;   // double-tap-to-fire window starts here (2026-10-21)
            // 2026-05-29: pickup SFX silenced — only drop sound plays now.
            // GameAudio.Instance?.PlayTileSelect();
            HapticsManager.EditConfirm(); // 0.30/0.75 — crisp UI feel, much lighter than old Strong()

            // Pulse the target tile
            Tile targetTile = _grid.GetTile(col, row);
            if (targetTile != null)
            {
                // Option A "selected" treatment: keep the tile's own face and
                // layer a cyan glow halo + springy select-pop + gentle breath
                // behind it (see Tile.SetEditSelected). Earlier approaches — a
                // full cyan sprite swap, or a color-tint pulse on a white tile —
                // read as a glitch or a washed-out faint tint.
                targetTile.SetEditSelected(true);
            }

            // Deselect any selected card
            _selectedIndex = -1;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

//             WDLog.Log($"[HandManager] Entered REWRITE mode: target ({col},{row}) " +
                      // $"letter='{cell.Letter}' — tap a hand card to replace it");
        }

        /// <summary>Swaps only between tiles that TOUCH (up/down/left/right). 2026-10-20 Spencer, on
        /// instant pop being "too easy": any-tile swaps meant a word could be built from anywhere.
        /// Touching-only makes words come from spotting what is nearly there. false = any two tiles.</summary>
        public static bool AdjacentSwapsOnly = true;

        private static bool IsAdjacent(int c1, int r1, int c2, int r2)
        {
            int dx = Mathf.Abs(c1 - c2);
            int dy = Mathf.Abs(r1 - r2);
            return (dx + dy) == 1; // orthogonal only
        }

        /// <summary>
        /// Legal board swap: swap two adjacent non-primed tiles IF the result creates a valid word.
        /// Costs 1 rewrite charge. Returns true if swap executed.
        /// </summary>
        /// <summary>
        /// Board Swap: swap any two regular tiles on the board (no adjacency required,
        /// no word requirement). Costs 1 edit charge. Cannot swap primed, gold, or stone tiles.
        /// </summary>
        // Fired when a board-to-board tile SWAP completes. The EDIT-gated tutorial advances on this, since a
        // board-swap doesn't fire MatchController.OnRewriteUsed the way a hand-STAMP does. 2026-07-07 Spencer.
        public static event System.Action OnBoardSwapDone;

        /// <summary>Can this cell take part in a swap? Mirrors the guards the FIRST tap
        /// already applies (stone / ice / anchored / chest / escort / primed). 2026-09-03:
        /// the SECOND tap applied the teal selected-glow with NO validation, so rocks and
        /// primed tiles lit up as if selectable before the swap was rejected.</summary>
        private bool CanSelectForSwap(int col, int row)
        {
            var rules = RulesEngine.Instance;
            if (rules == null || _grid == null) return false;
            var cell = rules.GetCell(col, row);
            if (cell == null) return false;
            if (cell.IsStone || cell.IsFrozen) return false;
            if (cell.IsAnchored || cell.IsDropTarget) return false;
            if (GEMS_LOCKED && cell.HasGem) return false;   // gems can't be picked up (2026-10-22)
            var t = _grid.GetTile(col, row);
            if (t != null && (t.IsVault || t.IsGoldBonus)) return false;
            // HIGH-VALUE GOLD letters are not swappable. 2026-09-21 Spencer: "i shouldnt be able
            // to press a golden tile". This also makes the deselect bug unreachable for them —
            // selecting then cancelling was dropping the gold face back to the plain white one.
            if (Tile.GOLD_LETTERS_LOCKED && t != null && t.IsHighValueLetter) return false;
            var primed = rules.PrimedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primed != null && primed.Count > 0) return false;
            return true;
        }

        // ── MOVE A TILE INTO AN EMPTY SPACE (2026-09-09 Spencer) ────────────────────
        // Tap a tile, tap an empty cell: the tile relocates there, and the column it left
        // collapses. Deliberately UNCONDITIONAL — no cost, no charge, no limit — because
        // every condition is another rule to teach, and taught rules are where casual
        // players fall off. Difficulty comes from the board rising, which needs no explaining.
        //
        // This is the old DROP verb returning, but sourced from the board rather than a hand:
        // you no longer receive tiles, you relocate them.
        // 2026-09-09: OFF. Shipped without a visual path — the data move (SetCell) had no
        // corresponding tile handoff, so the display desynced from the board: letters drawn
        // on top of each other, stuck selection rims, wrong tints. The MECHANIC is sound and
        // the rules side works; what is missing is moving the TILE with the data. Do that
        // before turning this back on.
        /// <summary>How often the deep finale BOOM lands at the end of a big explosion.
        /// 1 = every time (the old behaviour), 0 = never. 2026-09-10 Spencer.</summary>
        public static float BIG_POP_FINALE_CHANCE = 0.25f;

        // 2026-10-20 cleanup: the move-to-empty implementation (gated by the always-false
        // AllowMoveToEmpty flag) was removed as dead code. Restore from tag
        // pre-cleanup-2026-10-20 if the mechanic comes back.
        private bool TryBoardMoveToEmpty(int fromCol, int fromRow, int toCol, int toRow) => false;

        private bool TryBoardSwap(int col1, int row1, int col2, int row2)
        {
            var rules = RulesEngine.Instance;
            var mc = MatchController.Instance;
            if (rules == null || mc == null) return false;

            // Both tiles must exist
            var cell1 = rules.GetCell(col1, row1);
            var cell2 = rules.GetCell(col2, row2);
            if (cell1 == null || cell2 == null) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: empty cell"); return false; }
            if (!TutorialBeats.AllowSwap(new Vector2Int(col1, row1), new Vector2Int(col2, row2))) return false;   // tutorial beat: only the taught swap
            if (BoosterManager.CascadeBusy) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: a booster is still resolving"); return false; }   // 11-17
            if (AdjacentSwapsOnly && !_freeSwap && !IsAdjacent(col1, row1, col2, row2)) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: not touching"); return false; }

            // Cannot swap special tiles
            if (cell1.IsStone || cell2.IsStone) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: stone"); return false; }
            if (cell1.IsFrozen || cell2.IsFrozen) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: ICE (frozen)"); return false; } // ice tiles locked until thawed (2026-06-15 Spencer)
            // GEMS LOCKED (2026-10-22 Spencer: "you can basically just pivot them until they finally make a word"). Walking a gem
            // swap by swap until its letter lands in a word beat the goal's point — FIND a word through it. Gems fall, can be
            // swiped through, and are collected by any word using them; they just can't be moved by hand.
            if (GEMS_LOCKED && (cell1.HasGem || cell2.HasGem)) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: GEM (locked)"); return false; }
            // POWER-UPS LOCKED (2026-11-03, Spencer: "i shouldnt be able to move the dynamite pieces"). A power-up can't be dragged into a
            // word any more; it fires by double-tap, by a word made through it, or caught in a blast. Two power-ups swapped TOGETHER
            // still make their combo. false = the 10-30 rule (a power-up swap that makes a word stays).
            // 11-03 correction (Spencer: "you should still be able to move rockets, bombs and disco balls around freely"): ONLY the
            // dynamite is locked. Two power-ups swapped together still combo.
            bool tnt1 = cell1.SpecialKind == RulesEngine.SPECIAL_TNT, tnt2 = cell2.SpecialKind == RulesEngine.SPECIAL_TNT;
            bool sp1 = cell1.SpecialKind != RulesEngine.SPECIAL_NONE, sp2 = cell2.SpecialKind != RulesEngine.SPECIAL_NONE;
            if (SPECIALS_LOCKED && (tnt1 || tnt2) && !(sp1 && sp2)) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: dynamite (locked)"); return false; }
            if (cell1.IsSwapRefill || cell1.IsEditRefill || cell1.IsWildRefill) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: refill tile A"); return false; }
            if (cell2.IsSwapRefill || cell2.IsEditRefill || cell2.IsWildRefill) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: refill tile B"); return false; }
            if (cell1.IsWild || cell2.IsWild) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: wild"); return false; }

            // Cannot swap gold tiles
            Tile t1 = _grid.GetTile(col1, row1);
            Tile t2 = _grid.GetTile(col2, row2);
            if (t1 != null && t1.IsGoldBonus) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: gold bonus A"); return false; }
            if (t2 != null && t2.IsGoldBonus) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: gold bonus B"); return false; }

            // Cannot swap primed tiles
            var reg = rules.PrimedRegistry;
            if (reg != null)
            {
                if (reg.GetPrimedWordsContaining(new Vector2Int(col1, row1)).Count > 0) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: ARMED word A"); return false; }
                if (reg.GetPrimedWordsContaining(new Vector2Int(col2, row2)).Count > 0) { WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: ARMED word B"); return false; }
            }

            // SWAP COMBO: two specials swapped together fire a combined blast (no word needed, costs one move).
            if (SPECIAL_COMBOS && InstantPopMode && !_tapDetonating
                && cell1.SpecialKind != RulesEngine.SPECIAL_NONE && cell2.SpecialKind != RulesEngine.SPECIAL_NONE)
            {
                _freeSwap = false;   // a combo pays its own move (Free Swap doesn't carry into it)
                CancelRewriteMode();
                StartCoroutine(SpecialComboSequence(new Vector2Int(col1, row1), new Vector2Int(col2, row2)));
                return true;
            }

            // Must have edit charges
            if (mc.GetRewritesRemaining(MatchController.PLAYER_HUMAN) <= 0)
            {
                WDLog.Log($"[SwapBlocked] ({col1},{row1})<->({col2},{row2}) refused: NO EDIT CHARGES LEFT");
                GameAudio.Instance?.PlayButtonClick();
                return false;
            }

            // Reject same-letter swaps — no-op waste of edit — UNLESS one of the swapped
            // cells already sits in a real word that isn't primed/scored yet. 2026-06-08
            // Spencer: a K↔K swap then "claims" that word (e.g. a PEAK a rising row formed),
            // priming it or triggering a connected explosion. CellHasUnscoredWord skips
            // already-scored words, so it can't re-score for free — just costs the edit.
            char letter1 = cell1.Letter;
            char letter2 = cell2.Letter;
            // 2026-10-22 Spencer: "if you have a bomb or dynamite tile that shares a letter with a tile it should still be able
            // to be swapped" — same letters, but the swap MOVES the special (and two specials make a combo), so it is a real move.
            // 2026-10-22 (2nd): gems too — "if there is an adjacent I and the jewel has an I you should be able to swap the place
            // of them". The rule is "the two TILES differ", not "the two letters differ": anything the swap carries counts.
            bool specialInvolved = cell1.SpecialKind != cell2.SpecialKind
                                   || cell1.HasGem != cell2.HasGem
                                   || cell1.IsRainGold != cell2.IsRainGold;
            if (char.ToUpper(letter1) == char.ToUpper(letter2)
                && !specialInvolved
                && !rules.CellHasUnscoredWord(col1, row1)
                && !rules.CellHasUnscoredWord(col2, row2))
            {
                GameAudio.Instance?.PlayButtonClick();
                return false;
            }

            // BOUNCE-BACK (2026-10-30 Spencer: "sure if you think this would make the game better lets do it", after the RM study:
            // a match-3 swap that makes no match slides over, slides back and costs nothing; ours made 54% of his swaps cost a
            // move for nothing). Judged BEFORE anything is committed - WordsIfSwapped tries the swap in place and restores it - so
            // the rules state is never touched and nothing can desync. Specials too (BOUNCE_SPECIALS) - two specials still combo above;
            // the Free Swap booster keeps its own rule. Dead boards are BoardShuffle's job.
            if (BOUNCE_NO_WORD && InstantPopMode && SurvivalManager.FullBoardActive && !_freeSwap && (BOUNCE_SPECIALS || !specialInvolved)
                && (s_bouncePre = rules.WordsIfSwapped(new Vector2Int(col1, row1), new Vector2Int(col2, row2))).Count == 0)
                // (10-30: dropped "&& !CellHasUnscoredWord(a/b)". It saw standing words the real scan won't count - claim-blocked or
                // too short - so those swaps skipped this bounce, went through, found nothing and hit the LATE BOUNCE: the letters
                // visibly flipped and snapped back (Spencer: "the letter will switch ... and then glitch back"). WordsIfSwapped already
                // covers the same-letter claim case it guarded - the swap leaves the board as it is, so a standing word shows there.)
            {
                WDLog.Log($"[Bounce] ({col1},{row1})<->({col2},{row2}) makes no word - bounced back, no move spent");
                NoWordStreak++;   // the fishing hint still notices repeated misses
                PlaythroughStats.Bounces++;
                CancelRewriteMode();
                OwlPortrait.Play("think");   // 11-01 owl: unimpressed by a swap that makes no word
                StartCoroutine(BounceNoWordSwap(new Vector2Int(col1, row1), new Vector2Int(col2, row2)));
                return true;      // handled: the caller must not treat it as refused (no second nudge)
            }

            // STRANDED-GOAL AIRPLANE (10-30): judged on the board BEFORE this swap - if no common word reached a goal, a special this
            // swap earns becomes an airplane (RulesEngine.DoExplode). Only on goal levels, only for a real (worded) swap.
            RulesEngine.GoalDeadAtSwap = SurvivalManager.FullBoardActive && LevelRules.Goals.Count > 0 && rules.GoalAreaDead();
            MarkPlayerWordComing();   // the next pop is the word THIS swap makes (QUICK_POP)
            TutorialBeats.OnSwapCommitted(new Vector2Int(col1, row1), new Vector2Int(col2, row2));
            // Consume edit charge
            mc.UseRewriteCharge(MatchController.PLAYER_HUMAN);

            // Phase 11d — both swapped cells count as edit targets. Either one
            // appearing in a 5+ directly-formed word earns the refund.
            mc.RecordEditCells(
                new Vector2Int(col1, row1),
                new Vector2Int(col2, row2));

            // RecordEditCells pins the tier-3 burst origin to cells[0] (col1,row1). That's right for a
            // single-cell rewrite, but a 2-cell SWAP has no single trigger cell — the word-forming letter
            // can land in EITHER swapped cell, so cells[0] often fires the burst at the wrong swapped corner
            // (Spencer 2026-06-17: swapped to make a word, glow erupted from the other corner, not the word).
            // Clear it so the burst falls back to the DETONATING WORD's center (WordDropFX:1459), i.e. it
            // erupts from the word you actually formed.
            WordDropFX.LastTriggerCell = null;

            // Swap the letters in data
            cell1.Letter = letter2;
            cell2.Letter = letter1;
            // A special tile's power travels WITH its letter (2026-10-21) — the tile objects stay put, so move the flag.
            int sk = cell1.SpecialKind; cell1.SpecialKind = cell2.SpecialKind; cell2.SpecialKind = sk;
            bool rg = cell1.IsRainGold; cell1.IsRainGold = cell2.IsRainGold; cell2.IsRainGold = rg;   // shower gold too
            bool gm = cell1.HasGem; cell1.HasGem = cell2.HasGem; cell2.HasGem = gm;                   // and gems
            RulesEngine.LastSwapCells = new[] { new Vector2Int(col1, row1), new Vector2Int(col2, row2) };
            RulesEngine.ShortWordCells = RulesEngine.LastSwapCells;   // full board: a 3-letter word must run through these

            // Purge scored keys for both cells so new words can be detected
            rules.PurgeScoredKeysForCells(new System.Collections.Generic.List<Vector2Int> {
                new Vector2Int(col1, row1), new Vector2Int(col2, row2)
            });

            CancelRewriteMode(keepGlow: true); // keep the source's teal glow alive through the swap; faded out at the end

//             WDLog.Log($"[HandManager] Board swap ({col1},{row1})↔({col2},{row2}): '{letter1}'↔'{letter2}'");

            // Disable input and run the SAME resolution pipeline as a regular edit
            IsInteractable = false;
            _selectedIndex = -1;
            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(false);

            // Use BoardSwapTurnSequence which animates both tiles then runs full resolution
            StartCoroutine(BoardSwapTurnSequence(col1, row1, letter2, col2, row2, letter1));
            OnBoardSwapDone?.Invoke();   // EDIT-gated tutorial advances the step on this (board-swap fires no OnRewriteUsed)
            return true;
        }

        /// <summary>
        /// Animates a board swap (dissolve both tiles, pop in with new letters)
        /// then runs the full rewrite resolution pipeline for scoring/priming/detonation.
        /// </summary>
        // ── ARMED-WEB: tap a primed tile to detonate its whole connected cluster ─────
        // Deliberately its OWN path. Routing this through BoardSwapTurnSequence meant
        // inheriting that method's scan/score/prime pass, its crossing rules and its
        // gates — every one of which fought the tap. This does exactly three things:
        // seed the engine from the tapped cell, walk the resolution, play the payoff.

        /// <summary>True while a tap detonation is resolving (blocks re-entry).</summary>
        private bool _tapDetonating;
        private float _tapAtTime;

        /// <summary>Player tapped a board cell. If it belongs to a primed word, detonate
        /// that word and every primed word connected to it. Returns true if it fired.</summary>
        // ── INSTANT POP (2026-10-20 Spencer) ─────────────────────────────────────────────
        // "rather than priming it for a payoff to just make it simple and if you prime a word
        // those letters just pop". Any word that arms goes off straight away, through the SAME
        // tap-detonation path (scoring, FX, gravity) — and words formed by the falling letters
        // pop too, one after another, so cascades happen on their own. Rising rows untouched.
        // Switch off = the armed-web game exactly as before.
        public static bool InstantPopMode = true;

        /// <summary>Instant pop: only GOLD tiles (gold high-value letters + gold bonus cells) glow and fly up
        /// to the HUD, on every blast — instead of every letter, and only when the leftover decoration
        /// objective happened to count the word. 2026-10-20 Spencer: "we used to have an animation where
        /// the letters glow and then fly up ... re-add that just for Gold letters". The gold flight carries
        /// the blast's points (each landing pays a share); blasts with no gold pay out as before.</summary>
        public static bool FLY_UP_GOLD_ONLY = true;
        // Not during the bonus blast (2026-10-21 Spencer: "during the bonus it shouldnt reward extra moves for golden tiles") —
        // the level is won, so a gold letter just explodes and pays its coin like any other tile, no "+1 MOVE" fly-up.
        public static bool GoldFlyUpActive => InstantPopMode && FLY_UP_GOLD_ONLY && !BonusBlastRunning && !LevelRules.BonusLevelActive;   // Gold Rush: gold = coins, not moves (10-30)

        /// <summary>Gold letters fly to the MOVES box and each one is worth +1 move (2026-10-20 Spencer: "what do you
        /// think about these letters awarding an extra move"). The blast's POINTS then pay straight away (no fly
        /// share) — the gold letters carry a move, not the score. Needs the move limit on.</summary>
        public static bool GOLD_GIVES_MOVE = true;
        public static bool GoldToMovesActive => GoldFlyUpActive && GOLD_GIVES_MOVE && SurvivalManager.MoveLimitMode;
        public static void OnGoldLetterLanded() => SurvivalManager.Instance?.GrantBonusMoves(1, "gold letter");
        public static bool IsGoldFlyTile(Tile t) => t != null && (t.IsGoldLetter || t.IsGoldBonus);

        /// <summary>Fire the next armed word, if any. Called at the end of a swap, a swipe and a
        /// detonation, in the same frame processing ends, so a queued rise cannot slip in between
        /// a word forming and it popping.</summary>
        /// <summary>How long a word shows in its green/gold before it pops, so the player can see
        /// WHICH word it was. 2026-10-20 Spencer: "its not really clear what words are popping off".</summary>
        public static bool  POPUP_AT_WORD_CENTRE = true;   // word pop-up rises from the middle of the word (false = the tapped/first cell)
        public static float INSTANT_POP_HOLD = 0.35f;
        // QUICK POP (2026-10-23 Spencer: "having the prime sound and that delayed pause and light up of the word is even
        // necessary anymore?"). The 0.35s hold applied to EVERY word, cascade words too — a 3-word cascade spent >1s just
        // holding. Now: the player's OWN word (the first pop after a swap/swipe) gets a short PLAYER_POP_HOLD to read it;
        // words the fall made pop at once (CASCADE_POP_HOLD). QUICK_POP = false -> the old 0.35s for everything.
        public static bool  QUICK_POP        = true;
        // TEMP DIAG 2026-10-23 (Spencer: "quite a delay after the explosion and for the letters to cascade and then finally pop" —
        // log: TNT at 37.51s, the fall word TART popped 40.88s). Stamps each detonation phase. Strip once the delay is fixed.
        public static bool POP_TIMING_DIAG = true;
        private static int s_ptFrame; private static float s_ptTime;
        private static void PopTiming(string what)
        {
            if (!POP_TIMING_DIAG) return;
            int frames = Time.frameCount - s_ptFrame; float secs = Time.unscaledTime - s_ptTime;
            string fps = s_ptFrame > 0 && frames > 0 && secs < 10f ? $"  [{frames} frames since last stamp, avg {secs / frames * 1000f:F0}ms/frame]" : "";
            WDLog.Log($"[PopTiming] t={Time.unscaledTime:F2} {what}{fps}");
            s_ptFrame = Time.frameCount; s_ptTime = Time.unscaledTime;
        }
        public static float PLAYER_POP_HOLD  = 0.12f;
        public static float CASCADE_POP_HOLD = 0f;
        private bool _nextPopIsPlayers;   // set by a swap / swipe, consumed by the first auto-pop after it
        public static void MarkPlayerWordComing() { RulesEngine.BeginPlayerMove(); if (Instance != null) Instance._nextPopIsPlayers = true; }   // also a new move for the cascade cap

        /// <summary>Only words this long pop BY THEMSELVES. Shorter ones light up and wait: tap to
        /// cash one in, or extend it and the longer word pops (taking the short one with it, both
        /// paid). 2026-10-20 Spencer: "because its autodetonate a 3 word is automatically going to
        /// explode blocking you from making it a longer word". 3 = everything auto-pops (old way).</summary>
        public static int INSTANT_POP_AUTO_MIN = 3;   // 2026-10-20: back to 3 — Spencer: "i dont like having to tap to explode the 3 letter words". Longer words now come from PLANTED near-words (RulesEngine.TryPlantNearWord) instead.
        private bool _autoPopPending;
        /// <summary>A pop chain is still resolving (tap detonation or a pending auto-pop). Full-board refill waits on it.</summary>
        /// <summary>2026-10-22 Spencer: "how do i switch off the staggered pops ... when you explode more than one word?"
        /// true = several words detonating together pop one word at a time (trigger first, finale last + biggest);
        /// false = they all pop at once through the single-detonation path. FX Test Menu: "MULTI-WORD POP".</summary>
        public static bool MULTI_WORD_STAGGER = false;   // 2026-10-22 Spencer: default ALL AT ONCE
        public bool IsResolvingChain => _tapDetonating || _autoPopPending;

        private void MaybeAutoPop()
        {
            if (!InstantPopMode || _tapDetonating || _autoPopPending || SurvivalManager.AwaitingNextLevel) return;
            var reg = RulesEngine.Instance != null ? RulesEngine.Instance.PrimedRegistry : null;
            if (reg == null) return;
            var all = reg.GetAllPrimedWords();
            for (int i = 0; i < all.Count; i++)
                if (all[i].Cells != null && all[i].Cells.Count > 0
                    && (all[i].Word == null || all[i].Word.Length >= INSTANT_POP_AUTO_MIN))   // short words wait for a tap
                {
                    WDLog.Log($"[InstantPop] '{all[i].Word}' pops ({all.Count} armed)");
                    PopTiming("auto-pop '" + all[i].Word + "'");
                    StartCoroutine(AutoPopAfterHold(all[i].Cells[0]));
                    return;
                }
        }

        /// <summary>Hold the lit word on screen, then fire it. Processing stays claimed for the
        /// whole hold so no rise, swap or second pop can act on the board in between; it is
        /// released and the detonation claims it again in the same frame.</summary>
        private System.Collections.IEnumerator AutoPopAfterHold(Vector2Int cell)
        {
            _autoPopPending = true;
            IsInteractable = false;
            MatchController.Instance?.BeginProcessing();
            float hold = INSTANT_POP_HOLD;
            if (QUICK_POP && SurvivalManager.FullBoardActive) hold = _nextPopIsPlayers ? PLAYER_POP_HOLD : CASCADE_POP_HOLD;
            _nextPopIsPlayers = false;
            float t = 0f;
            while (t < hold) { t += Time.deltaTime; yield return null; }
            MatchController.Instance?.EndProcessing();
            _autoPopPending = false;
            if (!TryTapDetonate(cell)) IsInteractable = true;   // word vanished meanwhile — hand control back
        }

        /// <summary>Fire the special tile at <paramref name="cell"/> through the REAL detonation pipeline (rules clear,
        /// FX, gravity, cascades): registers a one-letter armed word on the cell and taps it. Used by the FX Test Menu
        /// special-tile buttons (2026-10-21), and the basis for tap-to-fire later. Returns false if it could not start.</summary>
        // ── END-OF-LEVEL BONUS BLAST (2026-10-21, Royal Match style) ─────────────────────────────────────────────
        public static bool BonusBlastRunning { get; private set; }
        private static float s_bonusStartedAt;
        /// <summary>True while the blast is running AND younger than 25s — the Well Done modal waits on this, so a blast
        /// that ever broke mid-way can never soft-lock the level end.</summary>
        public static bool BonusBlastHoldsModal => BonusBlastRunning && Time.unscaledTime - s_bonusStartedAt < 25f;

        public void BeginBonusBlast(int rockets, int movesLeft, int baseCoins = 0, int alreadyShown = 0)
        {
            if (BonusBlastRunning) return;
            bool anySpecial = false;
            var rules = RulesEngine.Instance;
            if (rules != null)
                for (int x = 0; x < RulesEngine.COLS && !anySpecial; x++)
                    for (int y = 0; y < RulesEngine.ROWS && !anySpecial; y++)
                    { var d = rules.GetCell(x, y); if (d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE) anySpecial = true; }
            if (rockets <= 0 && !anySpecial)
            {   // Gold Rush with every special used: no finale, but the counter still gets the level-clear coins (10-30)
                if (LevelRules.BonusLevelActive) HUDManager.Instance?.EnterRewardMode(Mathf.Max(0, baseCoins - alreadyShown));
                WDLog.Log("[Bonus] nothing to blast (no moves left, no specials)"); return;
            }
            BonusBlastRunning = true;
            HUDManager.RewardFromPayouts = true;   // 10-31: the REWARD readout follows the real payout from here (see PayBonusCoins)
            // 10-31 v3 (Spencer: still "+10 at the very end", log: the whole finale is ONE chained tap -> ONE payout at the end): the
            // readout's target is now LIVE - base + every tile destroyed so far x COINS_PER_BONUS_TILE - so the chain/word extras
            // count in as the tiles explode, not in a lump after.
            // (v3 live target REMOVED: it ran ahead of the coins - tiles count before their coins launch)
            // 2026-10-22 Spencer: "can it cut the music during the bonus?" — the level music fades out so the rockets and
            // blasts carry the moment; the Well Done screen starts its own victory track after, the next level its song.
            if (BONUS_CUT_MUSIC) GameAudio.Instance?.StopMusic(BONUS_MUSIC_FADE);
            s_bonusStartedAt = Time.unscaledTime;
            s_bonusBaseCoins = baseCoins; s_bonusAlreadyShown = Mathf.Clamp(alreadyShown, 0, baseCoins);
            StartCoroutine(BonusBlastRoutine(rockets, movesLeft));
        }

        /// <summary>A tap that SKIPS the bonus — ignored for the first BONUS_SKIP_LOCKOUT seconds so frantic end-of-level
        /// clicking can't skip it by accident (2026-10-21).</summary>
        private static bool TapPressedThisFrame() =>
            Time.unscaledTime - s_bonusStartedAt >= BONUS_SKIP_LOCKOUT
            && (WDInput.GetMouseButtonDown(0) || (WDInput.touchCount > 0 && WDInput.GetTouch(0).phase == TouchPhase.Began));
        public static float BONUS_SKIP_LOCKOUT = 1.2f;

        /// <summary>Seconds the completed goal badges (checks) stay up before the bonus blast swaps in the coin counter.</summary>
        public static float BONUS_CHECK_HOLD = 0.4f;   // 11-06: 0.8 -> 0.4 (L1-20 recording: 1.75 s static board after the goal; RM goes to its banner in ~0.3 s)
        /// <summary>true = a tap during the bonus blast SKIPS it (estimate paid); false = a tap fast-forwards it.</summary>
        public static bool  BONUS_TAP_SKIPS = false;
        public static float BONUS_FAST_INTERVAL = 0.03f;   // rocket launch gap once the player taps
        public static bool  BONUS_COINS_ON_ARRIVAL = true;  // 11-07: bonus coins leave each tile as it breaks (false = all at launch)
        public static float BONUS_FAST_SCALE = 3.5f;       // 11-07: game speed for the rest of the bonus once the player taps
        private IEnumerator BonusBlastRoutine(int rockets, int movesLeft)
        {
            var rules = RulesEngine.Instance; var grid = GridManager.Instance;
            WDLog.Log($"[Bonus] start — {rockets} rocket(s) from {movesLeft} move(s) left");
            // TAP = FAST-FORWARD, not abort (2026-10-23 Spencer: "when you hit the bonus stage and click it just completely
            // interupts the bonus and freezes"). The old tap-to-skip cancelled the show before a rocket was placed, silently
            // wiped the specials and paid an estimate — then the board sat still until the modal. Now a tap ends the holds and
            // rushes the rockets; everything still fires and pays for real. BONUS_TAP_SKIPS = true restores the old skip.
            bool skip = false, fast = false;
            GameObject skipHint = null;   // 11-07 Spencer: "tap to skip for the rockets bonus" (RM shows "Tap to Skip")
            bool Tapped()
            {
                // 11-07 Spencer "i cant actually tap to skip": the tap only shortened the rocket launch gaps - the blasts still
                // played at full length (log: ~2.6 s from tap to done). Fast-forward now runs the rest of the show at
                // BONUS_FAST_SCALE; re-asserted every frame because a hitstop sets timeScale 0 -> 1. Restored at the end.
                if (fast && Time.timeScale > 0f && Time.timeScale < BONUS_FAST_SCALE) Time.timeScale = BONUS_FAST_SCALE;
                if (!TapPressedThisFrame()) return false;
                if (skipHint != null) { var g0 = skipHint; skipHint = null; HideBonusSkipHint(g0); }
                if (BONUS_TAP_SKIPS) return true;
                if (!fast) WDLog.Log("[Bonus] tap — fast-forward");
                fast = true; return false;
            }
            // Let the winning move finish (its pops, gravity, landing points) before the show starts.
            float waited = 0f;
            while (waited < 5f && ((MatchController.Instance != null && MatchController.Instance.IsProcessing) || _tapDetonating
                                   || WordDropFX.HasActiveExplosions))
            { Tapped(); waited += Time.unscaledDeltaTime; yield return null; }
            // LET THE GOAL CHECK SHOW (2026-10-23 Spencer: "when you complete the objective it isnt doing a checkmark in the badge
            // anymore"). The last gem / letter is still FLYING to its goal badge when the level clears (log: gem collected,
            // 0.15s later the bonus began), and EnterRewardMode swaps the goal pictures for the coin counter — so the badge never
            // reached 0 on screen. Wait for every goal flight to land, then hold the checks a beat before the coins take over.
            waited = 0f;
            while (waited < 1.5f && (HUDManager.CollectInFlight > 0 || LevelRules.CollectAwaitingFly > 0 || LevelRules.ThemeAwaitingFly > 0))
            { Tapped(); waited += Time.unscaledDeltaTime; yield return null; }
            WDLog.Log($"[Bonus] goal flights settled after {waited:F2}s (inFlight={HUDManager.CollectInFlight}, awaiting={LevelRules.CollectAwaitingFly}) — holding the checks");   // TEMP DIAG
            { float h = 0f; while (h < BONUS_CHECK_HOLD && !fast) { Tapped(); h += Time.unscaledDeltaTime; yield return null; } }

            int coins = 0;
            if (BONUS_SKIP_HINT && !fast && rockets > 1) skipHint = MakeBonusSkipHint();
            TMPro.TextMeshPro counter = null;
            if (grid != null && BONUS_BANNER)   // 2026-10-29 Spencer: "remove the cheezy bonus! with pill text" - off; the rockets + coin count carry it
            {
                var cgo = new GameObject("BonusCounter");
                counter = cgo.AddComponent<TMPro.TextMeshPro>();
                counter.alignment = TMPro.TextAlignmentOptions.Center;
                counter.fontSize = 7f; counter.sortingOrder = 120;
                counter.color = new Color(1f, 0.86f, 0.3f);
                counter.outlineWidth = 0.25f; counter.outlineColor = new Color32(70, 40, 0, 255);
                cgo.transform.position = grid.CellToWorld(RulesEngine.COLS / 2, RulesEngine.ROWS - 2) + new Vector3(0f, 0f, -1f);
                counter.text = "BONUS!";
                // READABLE ON A FULL BOARD (2026-10-21 Spencer: "the word bonus is overlayed on the board and you cant read
                // it"). Gold text straight over letter tiles had nothing behind it. Dark rounded plate + a soft drop shadow,
                // the way Royal Match sits its banners on a backing.
                counter.fontSize = 8.5f;
                counter.fontStyle = TMPro.FontStyles.Bold;
                var cm = counter.fontMaterial;
                cm.EnableKeyword("UNDERLAY_ON");
                cm.SetColor("_UnderlayColor", new Color(0f, 0f, 0f, 0.6f));
                cm.SetFloat("_UnderlayOffsetY", -0.6f); cm.SetFloat("_UnderlaySoftness", 0.3f);
                if (s_bonusPlate == null)
                    s_bonusPlate = TileRenderer.CreateRoundedRect(512, 160, 70, new Color(0.10f, 0.05f, 0.22f, 0.82f), new Color(1f, 0.78f, 0.25f, 0.95f), 6);
                if (s_bonusPlate != null)
                {
                    var pgo = new GameObject("BonusPlate");
                    pgo.transform.SetParent(cgo.transform, false);
                    pgo.transform.localPosition = new Vector3(0f, 0f, 0.05f);
                    var psr = pgo.AddComponent<SpriteRenderer>();
                    psr.sprite = s_bonusPlate; psr.sortingOrder = 119;
                    Vector2 want = new Vector2(grid.CellSize * 6.2f, grid.CellSize * 1.7f);
                    Vector3 b = s_bonusPlate.bounds.size;
                    pgo.transform.localScale = new Vector3(want.x / Mathf.Max(0.01f, b.x), want.y / Mathf.Max(0.01f, b.y), 1f);
                }
                cgo.transform.localScale = Vector3.one * 0.6f;
                cgo.transform.DOScale(1f, 0.3f).SetEase(Ease.OutBack, 2.5f).SetUpdate(true);
            }
            GameAudio.Instance?.PlayScorePowerup();
            HUDManager.Instance?.EnterRewardMode(s_bonusBaseCoins - s_bonusAlreadyShown);   // Gold Rush: the panel already counts its coins - add the base only   // SCORE panel -> REWARD, counting this level's coins
            // ANTICIPATION (2026-10-21 Spencer: "a bit of an anticipation beat ... it all just happens a bit too fast").
            { float h = 0f; while (h < BONUS_INTRO_HOLD && !skip && !fast) { if (Tapped()) skip = true; h += Time.unscaledDeltaTime; yield return null; } }

            // 1) Leftover moves -> rockets dropped on random tiles, the MOVES counter ticking down with each.
            int placed = 0; float lastLandAt = 0f;
            for (int i = 0; i < rockets && !skip; i++)
            {
                if (Tapped()) { skip = true; break; }
                var free = new List<Vector2Int>();
                if (rules != null)
                    for (int x = 0; x < RulesEngine.COLS; x++)
                        for (int y = 0; y < RulesEngine.ROWS; y++)
                        {
                            var d = rules.GetCell(x, y);
                            if (d != null && !d.IsFrozen && !d.IsDropTarget && !d.IsAnchored && d.SpecialKind == RulesEngine.SPECIAL_NONE)
                                free.Add(new Vector2Int(x, y));
                        }
                if (free.Count == 0) break;
                var c = free[Random.Range(0, free.Count)];
                int kind = Random.value < 0.5f ? RulesEngine.SPECIAL_ROCKET_H : RulesEngine.SPECIAL_ROCKET_V;
                rules.GetCell(c.x, c.y).SpecialKind = kind;   // claim the cell now so the next rocket picks another
                grid?.GetTile(c.x, c.y)?.HideLetterForBonus();   // now, not on landing: state sync can show the icon mid-flight (2026-10-26)
                placed++;
                // 10-31 (Spencer: the moves "stick on one number and then change immediately"): rockets are capped at
                // BONUS_MAX_ROCKETS, so with more moves left the counter stalled and then snapped to 0. Each rocket now takes its
                // share of the moves, so the last rocket brings the counter to 0.
                HUDManager.MovesDisplayOverride = Mathf.Max(0, movesLeft - Mathf.RoundToInt(placed * (float)movesLeft / Mathf.Max(1, rockets)));
                // OVERLAPPING LAUNCHES (2026-10-22 Spencer: "during the bonus can it dole out the rockets a little bit faster?").
                // Was strictly one-at-a-time: 0.32s flight + 0.12s gap = 0.44s a rocket, 4.4s for ten. Now the next rocket
                // leaves the MOVES counter every BONUS_ROCKET_INTERVAL while earlier ones are still flying; each lands on its
                // own tile when its flight ends (delayed call), and the loop waits for the last landing before the pre-fire hold.
                int li = i; var lc = c; int lk = kind;
                Vector3 lto = grid != null ? grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.6f) : Vector3.zero;
                System.Action land = () => { var lt = grid?.GetTile(lc.x, lc.y); lt?.SetSpecial(lk, pop: true); lt?.HideLetterForBonus(); GameAudio.Instance?.PlayScoreTick(1f + 0.06f * li); GameAudio.Instance?.PlayPowerupShimmer();   // 10-31: shimmer as each bonus rocket lands
                                             if (WordDropFX.BONUS_COMET) WordDropFX.Instance?.BonusLandSparkle(lto); };
                if (grid != null && HUDManager.Instance != null && WordDropFX.Instance != null)
                {
                    Vector3 to = lto;
                    // 2026-10-28: Royal Match's comet (white streak + sparkle dust, ~0.2s) instead of flying the rocket icon
                    float flight = WordDropFX.BONUS_COMET ? BONUS_COMET_FLIGHT : BONUS_ROCKET_FLIGHT;
                    if (WordDropFX.BONUS_COMET) WordDropFX.Instance.BonusComet(HUDManager.Instance.GetMovesWorldPos(to.z), to, flight);
                    else WordDropFX.Instance.FlyIcon(kind, HUDManager.Instance.GetMovesWorldPos(to.z), to, flight);
                    DOVirtual.DelayedCall(flight, () => land(), ignoreTimeScale: true);
                    lastLandAt = Time.unscaledTime + flight;
                }
                else land();
                float t = 0f; while (t < (fast ? BONUS_FAST_INTERVAL : BONUS_ROCKET_INTERVAL)) { if (Tapped()) { skip = true; break; } t += Time.unscaledDeltaTime; yield return null; }
            }
            // let the last rocket in the air land before the pre-fire beat
            while (Time.unscaledTime < lastLandAt) { if (Tapped()) skip = true; yield return null; }
            // Beat with every rocket on the board before the first one goes.
            { float h = 0f; while (h < BONUS_PREFIRE_HOLD && !skip && !fast) { if (Tapped()) skip = true; h += Time.unscaledDeltaTime; yield return null; } }

            // 2) Fire EVERY special at once (2026-10-21 Spencer: "i dont want a short pause between all the explosions. just a
            //    short pause before they all start"). One detonation seeded with a 1-letter armed word on each special; the FX
            //    ripple out BONUS_FIRE_STAGGER apart. The loop below then mops up anything left (normally nothing).
            if (!skip && rules != null)
            {
                int before0 = RulesEngine.ExplodedTilesTotal;
                Vector2Int first = new Vector2Int(-1, -1);
                for (int y = 0; y < RulesEngine.ROWS; y++)
                    for (int x = 0; x < RulesEngine.COLS; x++)
                    {
                        var d = rules.GetCell(x, y);
                        if (d == null || d.SpecialKind == RulesEngine.SPECIAL_NONE) continue;
                        rules.PrimedRegistry.AddPrimedWord(d.Letter.ToString(), new List<Vector2Int> { new Vector2Int(x, y) },
                            MatchController.PLAYER_HUMAN, rules.GlobalTurn, rules.GlobalTurn + 999);
                        if (first.x < 0) first = new Vector2Int(x, y);
                    }
                if (first.x >= 0)
                {
                    RulesEngine.SeedAllOnNextTap = true;
                    if (TryTapDetonate(first))
                    {
                        float w0 = 0f;
                        while (w0 < 8f && (_tapDetonating || (MatchController.Instance != null && MatchController.Instance.IsProcessing)))
                        { if (Tapped()) skip = true; w0 += Time.unscaledDeltaTime; yield return null; }
                        int got = Mathf.Max(0, RulesEngine.ExplodedTilesTotal - before0) * SurvivalManager.COINS_PER_BONUS_TILE;
                        if (got > 0) PayBonusCoins(got, ref coins, counter);
                    }
                    else RulesEngine.SeedAllOnNextTap = false;
                }
            }
            int guard = 0;
            while (!skip && guard++ < 40 && rules != null)
            {
                Vector2Int next = new Vector2Int(-1, -1);
                for (int y = 0; y < RulesEngine.ROWS && next.x < 0; y++)
                    for (int x = 0; x < RulesEngine.COLS && next.x < 0; x++)
                    { var d = rules.GetCell(x, y); if (d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE) next = new Vector2Int(x, y); }
                if (next.x < 0) break;
                int before = RulesEngine.ExplodedTilesTotal;
                if (!FireSpecialAt(next)) { yield return null; continue; }
                float w = 0f;
                while (w < 6f && (_tapDetonating || (MatchController.Instance != null && MatchController.Instance.IsProcessing)))
                { if (Tapped()) skip = true; w += Time.unscaledDeltaTime; yield return null; }
                int gained = Mathf.Max(0, RulesEngine.ExplodedTilesTotal - before) * SurvivalManager.COINS_PER_BONUS_TILE;
                if (gained > 0) PayBonusCoins(gained, ref coins, counter);
            }

            // 3) Skipped: pay a quick estimate for whatever had not gone off yet.
            if (skip && rules != null)
            {
                int left = rockets - placed;
                for (int x = 0; x < RulesEngine.COLS; x++)
                    for (int y = 0; y < RulesEngine.ROWS; y++)
                    { var d = rules.GetCell(x, y); if (d != null && d.SpecialKind != RulesEngine.SPECIAL_NONE) { left++; d.SpecialKind = RulesEngine.SPECIAL_NONE; grid?.GetTile(x, y)?.SetSpecial(RulesEngine.SPECIAL_NONE); } }
                int est = left * BONUS_SKIP_COINS_PER_SPECIAL;
                if (est > 0) PayBonusCoins(est, ref coins, counter);
                WDLog.Log($"[Bonus] skipped — paid an estimate of {est} for {left} special(s)");
            }

            HUDManager.MovesDisplayOverride = 0;
            { float h = 0f; float settle = fast ? BONUS_COINS_SETTLE / BONUS_FAST_SCALE : BONUS_COINS_SETTLE; while (h < settle) { Tapped(); h += Time.unscaledDeltaTime; yield return null; } }   // let the last coins land before the readout is snapped
            if (fast) Time.timeScale = 1f;   // 11-07: end of the fast-forward
            HUDManager.RewardFromPayouts = false;   // final: exact total (by now the coins have counted it up)
            HUDManager.Instance?.SetRewardTotal(s_bonusBaseCoins + coins);
            WDLog.Log($"[Bonus] done — {coins} coin(s) from the blast");
            try { AnalyticsManager.Log("bonus_blast", "rockets", rockets, "moves_left", movesLeft, "coins", coins, "skipped", skip ? 1 : 0); AnalyticsManager.FlushToDisk(); }
            catch (System.Exception) { }
            if (counter != null)
            {
                var cg = counter.gameObject;
                var plate = cg.GetComponentInChildren<SpriteRenderer>();
                if (plate != null) plate.DOFade(0f, 0.4f).SetDelay(0.7f).SetUpdate(true);
                counter.DOFade(0f, 0.4f).SetDelay(0.7f).SetUpdate(true).OnComplete(() => { if (cg != null) Destroy(cg); });
            }
            if (skipHint != null) { HideBonusSkipHint(skipHint); skipHint = null; }
            yield return new WaitForSecondsRealtime(BONUS_END_HOLD);
            if (fast && Time.timeScale > 1f) Time.timeScale = 1f; BonusBlastRunning = false;
        }

        /// <summary>11-07: "Tap to skip" at the bottom of the screen during the leftover-moves rockets (RM's "Tap to Skip"). A tap
        /// fast-forwards (every rocket still fires and pays) - the hint fades as soon as it's used, and at the end of the show.</summary>
        public static bool BONUS_SKIP_HINT = true;
        private GameObject MakeBonusSkipHint()
        {
            var go = new GameObject("BonusSkipHint", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
            var cv = go.GetComponent<Canvas>(); cv.renderMode = RenderMode.ScreenSpaceOverlay; cv.sortingOrder = 45;   // under the win screen
            var sc = go.GetComponent<UnityEngine.UI.CanvasScaler>(); sc.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            sc.referenceResolution = new Vector2(1179f, 2556f); sc.matchWidthOrHeight = 0f;
            var tg = new GameObject("Text", typeof(RectTransform), typeof(TMPro.TextMeshProUGUI)); tg.transform.SetParent(go.transform, false);
            var rt = (RectTransform)tg.transform; rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f); rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(0f, 2556f * BONUS_SKIP_Y); rt.sizeDelta = new Vector2(900f, 110f);
            var t = tg.GetComponent<TMPro.TextMeshProUGUI>(); t.text = "Tap to Skip"; t.fontSize = BONUS_SKIP_SIZE; t.alignment = TMPro.TextAlignmentOptions.Center; t.raycastTarget = false;
            // 11-16 (Spencer: "looks weird in comparison to the royal match version"). RM (L9 @538s): SOLID warm cream, soft dark
            // shadow, ~37% of the screen wide, steady. Ours was lavender-stroked, alpha pulsing .9 <-> .6 (read ghosted over the
            // debris) and ~24% wide. Now: solid cream, dark stroke + drop, bigger, a gentle size breathe instead of the fade.
            HUDManager.ApplyRecipeCount(t, BONUS_SKIP_LINE, HUDManager.D_STROKE, HUDManager.D_DROP * 1.5f);
            t.enableVertexGradient = false; t.color = BONUS_SKIP_CREAM; t.fontSize = BONUS_SKIP_SIZE;
            t.alpha = 0f; t.DOFade(1f, 0.25f).SetUpdate(true).SetLink(t.gameObject);
            tg.transform.DOScale(1.04f, 0.8f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true).SetLink(tg);
            return go;
        }
        public static float BONUS_SKIP_Y = 0.135f;  // 11-16 Movie_009: .17 sat ON the board's gold rim; .135 = centred in the gap above the boosters (was .20)
        public static float BONUS_SKIP_SIZE = 86f;  // 11-16: was 58 (~24% of the screen wide; RM ~37%)
        public static Color BONUS_SKIP_CREAM = new Color(1f, 0.96f, 0.84f, 1f), BONUS_SKIP_LINE = new Color(0.16f, 0.07f, 0.26f, 1f);   // height from the bottom (fraction of the screen) - above the booster row
        private static void HideBonusSkipHint(GameObject go)
        {
            if (go == null) return;
            var t = go.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (t == null) { Destroy(go); return; }
            t.DOKill(); t.DOFade(0f, 0.2f).SetUpdate(true).OnComplete(() => { if (go != null) Destroy(go); });
        }

        public static int BONUS_SKIP_COINS_PER_SPECIAL = 7;
        // 11-06 (L1-20 recording: 2.25 s static board between the last bonus blast and Well Done = 0.9 + 0.5 here + the modal's
        // 0.8 settle beat). Was 0.9 / 0.5. Rocket pacing itself is untouched (10-21 Spencer wanted the rockets slower).
        public static float BONUS_COINS_SETTLE = 0.5f, BONUS_END_HOLD = 0.2f;
        private static Sprite s_bonusPlate;
        // Bonus pacing (2026-10-21: slowed down — "it all just happens a bit too fast").
        public static bool  BONUS_CUT_MUSIC     = true;
        public static float BONUS_MUSIC_FADE    = 0.35f;   // seconds
        public static bool  BONUS_BANNER        = false;  // the "BONUS!" pill over the board at level end (10-29: removed)
        public static float BONUS_INTRO_HOLD    = 0.7f;   // "BONUS!" on screen before anything moves
        public static float BONUS_COMET_FLIGHT  = 0.22f;  // RM comet: ~0.15-0.2s counter -> tile (measured @1:19)
        public static float BONUS_ROCKET_FLIGHT = 0.32f;  // each rocket flying from the MOVES counter to its tile
        public static float BONUS_ROCKET_GAP    = 0.12f;  // (old one-at-a-time pacing; unused since 2026-10-22)
        public static float BONUS_ROCKET_INTERVAL = 0.2f; // 2026-10-22: a new rocket leaves the MOVES counter this often (flights overlap)
        public static float BONUS_PREFIRE_HOLD  = 0.5f;   // all rockets placed, before the first fires
        public static float BONUS_FIRE_STAGGER  = 0.07f;  // all specials fire together; their FX ripple out this far apart
        private static int s_bonusBaseCoins, s_bonusAlreadyShown;   // estimate when the blast is tapped through

        private static void PayBonusCoins(int n, ref int running, TMPro.TextMeshPro counter)
        {
            running += n;
            // 10-31 (Spencer: "right before it goes to the well done screen the rewards number changes to something else"): the
            // flying coins carry "tiles this special cleared" but the pay counts EVERY exploded tile (words + chains the rockets
            // set off), so the readout was corrected at the end. Now the readout's target IS the paid total; coins only punch it.
            HUDManager.Instance?.SetRewardTotal(s_bonusBaseCoins + running);
            CoinWallet.Add(n);
            SurvivalManager.PendingBonusCoins += n;
            LevelMapPanel.Instance?.AddPendingCoinReward(n);
            // (the running total now counts up in the HUD's REWARD panel as the coins land)
        }

        public static float DOUBLE_TAP_WINDOW = 0.4f;   // seconds between the two taps that fire a special
        private float _selectedAt = -10f;

        public static int   BIG_MOMENT_WORDS = 2;   // words in one blast that count as a big moment (boom + harder shake)
        public static bool  SPECIAL_COMBOS = true;
        /// <summary>FX Test Menu: run the swap-combo sequence on two cells that already hold specials.</summary>
        public void TestCombo(Vector2Int a, Vector2Int b) { if (!_tapDetonating) StartCoroutine(SpecialComboSequence(a, b)); }
        public static float COMBO_MERGE_DUR = 0.16f;
        private static int ComboPower(int k) => k == RulesEngine.SPECIAL_DISCO ? 4 : RulesEngine.IsBombLike(k) ? 3
                                              : k == RulesEngine.SPECIAL_AIRPLANE ? 2 : RulesEngine.IsRocket(k) ? 1 : 0;
        /// <summary>The two specials slide into each other (the second into the first), the merged tile pops, then the combo
        /// fires through the normal detonation path at the first cell.</summary>
        private System.Collections.IEnumerator SpecialComboSequence(Vector2Int a, Vector2Int b)
        {
            var rules = RulesEngine.Instance; if (rules == null || _grid == null) yield break;
            var da = rules.GetCell(a.x, a.y); var db = rules.GetCell(b.x, b.y);
            if (da == null || db == null) yield break;
            // FIRES AT THE DROP TARGET (11-01 Spencer: "it should start its animation at the tile you pushed it into"). a = the tile
            // the player dragged, b = the tile it was pushed into: a slides into b and the combo goes off from b. The STRONGER
            // special still decides what it becomes (10-31: rocket + dynamite -> the dynamite look) - only its position changed.
            {
                var tc = a; a = b; b = tc; var td = da; da = db; db = td;   // a = where it fires (the target), b = the one that moves
            }
            bool dStrong = ComboPower(da.SpecialKind) >= ComboPower(db.SpecialKind);
            int ka = dStrong ? da.SpecialKind : db.SpecialKind, kb = dStrong ? db.SpecialKind : da.SpecialKind;   // ka = stronger
            int combo = RulesEngine.ComboKindFor(ka, kb);
            RulesEngine.PendingComboPartner = ka == RulesEngine.SPECIAL_DISCO ? kb : ka;
            OwlPortrait.Play("starry");   // 11-01 owl: power-up combo
            WDLog.Log($"[Combo] {RulesEngine.SpecialName(db.SpecialKind)} pushed into {RulesEngine.SpecialName(da.SpecialKind)} -> {RulesEngine.SpecialName(combo)} at ({a.x},{a.y})");
            var ta = _grid.GetTile(a.x, a.y); var tb = _grid.GetTile(b.x, b.y);
            if (tb != null)
            {
                tb.MarkAnimating(COMBO_MERGE_DUR + 0.1f);
                tb.transform.DOKill();
                tb.transform.DOMove(_grid.CellToWorld(a.x, a.y) + new Vector3(_grid.BoardSlideOffsetX, 0f, -0.05f), COMBO_MERGE_DUR).SetEase(Ease.InBack, 1.3f);
            }
            if (ta != null) { ta.transform.DOKill(); ta.transform.DOPunchScale(Vector3.one * 0.25f, COMBO_MERGE_DUR + 0.12f, 6, 0.6f); }
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("combo_merge")) GameAudio.Instance.PlayKit("combo_merge"); else GameAudio.Instance?.PlayScorePowerup();   // 11-07 Spencer's pick: Pop Delay Rising B4
            yield return new WaitForSeconds(COMBO_MERGE_DUR);
            // b's special is consumed into the combo; a carries the combo kind and fires.
            db.SpecialKind = RulesEngine.SPECIAL_NONE; tb?.SetSpecial(RulesEngine.SPECIAL_NONE);
            if (tb != null) tb.transform.position = _grid.CellToWorld(b.x, b.y) + new Vector3(_grid.BoardSlideOffsetX, 0f, 0f);
            da.SpecialKind = combo;
            if (FireSpecialAt(a) && SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null && SurvivalManager.Instance.EditsCountAsMoves)
                SurvivalManager.Instance.NotifyDropCommitted();   // costs a move
        }

        /// <summary>True while a tapped / fired special is resolving (BoosterManager waits on it between queued fires).</summary>
        public bool IsTapDetonating => _tapDetonating;
        /// <summary>BOOSTERS (11-07): a booster's shelf/safe hits get the same books-fly / lamp / open FX a blast's do.</summary>
        public void HitShelvesFromBooster(List<KeyValuePair<Vector2Int, int>> hits)
        {
            if (hits == null || hits.Count == 0) return;
            HitShelfTiles(_grid != null ? _grid : GridManager.Instance, new RulesEngine.StepResult { ShelfHits = hits });
        }

        public bool FireSpecialAt(Vector2Int cell)
        {
            var rules = RulesEngine.Instance;
            if (rules == null || _tapDetonating || (MatchController.Instance != null && MatchController.Instance.IsProcessing)) return false;
            var data = rules.GetCell(cell.x, cell.y);
            if (data == null || data.SpecialKind == RulesEngine.SPECIAL_NONE) return false;
            int id = rules.PrimedRegistry.AddPrimedWord(data.Letter.ToString(), new List<Vector2Int> { cell },
                         MatchController.PLAYER_HUMAN, rules.GlobalTurn, rules.GlobalTurn + 999);
            if (id < 0) return false;
            WDLog.Log($"[Special] TEST fire {RulesEngine.SpecialName(data.SpecialKind)} at ({cell.x},{cell.y})");
            bool fired = TryTapDetonate(cell);
            if (fired) TutorialBeats.OnSpecialFired(cell);   // 11-17: the "double-tap the rocket" lesson ends here
            return fired;
        }

        private bool TryTapDetonate(Vector2Int cell)
        {
            if (_tapDetonating) return false;
            var rules = RulesEngine.Instance;
            if (rules == null || _grid == null) return false;
            if (rules.GetPrimedWordsContainingPublic(cell) == 0) return false;

            _tapDetonating = true;
            // ── SAME-FRAME ACKNOWLEDGEMENT (2026-09-08 Spencer) ────────────────────────
            // "there isn't an immediate reaction that i feel on a game like slay the spire."
            // Slay the Spire does not RESOLVE faster — it ANSWERS faster. It fires a sound and
            // moves the thing you clicked on the frame of the press, and once the player has
            // been told "heard you", the resolution that follows stops reading as lag.
            // This used to squish ONE tile, SILENTLY (playSound:false), plus a haptic that does
            // nothing on desktop — so on a five-tile word four fifths of what you clicked did
            // nothing, and there was no sound at all.
            // Now: sound on the press, and every tile of the word reacts together, which also
            // shows the player WHAT they are about to detonate.
            var tapped = _grid.GetTile(cell.x, cell.y);
            if (tapped != null) tapped.PlayLandingSquish(playSound: true);   // the one sound
            var webCells = rules.GetPrimedCellsContaining(cell);
            for (int i = 0; i < webCells.Count; i++)
            {
                if (webCells[i] == cell) continue;                           // squished above
                var wt = _grid.GetTile(webCells[i].x, webCells[i].y);
                if (wt != null) wt.PlayLandingSquish(playSound: false);      // one sound, not five
            }
            // Payout pop — fired HERE, at the tap, because the primed words still exist in the
            // registry at this point. One step later they are gone and the payout is
            // unrecoverable. Sourced from the same PreviewGroupPayout the badge shows, so the
            // number that leaps off the board is the number the player was promised.
            // 2026-09-10 Spencer.
            if (ClusterOutline.Instance != null && _grid != null)
            {
                var cl = rules.GetPrimedClusters();
                for (int i = 0; i < cl.Count; i++)
                {
                    if (cl[i].cells == null || !cl[i].cells.Contains(cell)) continue;
                    // POP UNDER THE FINGER. 2026-10-19 Spencer: "you should probably center it
                    // on wherever you press to pop the words". The anchor used to be the corner
                    // BADGE so the number could grow out of it — but the badge is off
                    // (SHOW_CORNER_BADGE = false), so it was launching from a corner with
                    // nothing there. `cell` IS the tapped cell, and one tap makes exactly one
                    // pop (note the break below), so there is nothing to collide with.
                    // The anchor is still read for font size / badge index / ink — only the
                    // POSITION comes from the tap.
                    if (ClusterOutline.Instance.TryGetBadgeAnchor(cell, out var anchor, out var fs, out var bi, out var bc))
                    {
                        Vector3 tapAt = _grid.CellToWorld(cell.x, cell.y);
                        // CENTRE OF THE WORD (2026-10-28 Spencer: "the pop ups should probably occur central to the word that is
                        // formed"). Words auto-pop now (InstantPop taps the word's FIRST cell for you), so "under the finger"
                        // (10-19) put the word at its first letter. Centre of the cluster's cells instead.
                        if (POPUP_AT_WORD_CENTRE && cl[i].cells.Count > 0)
                        {
                            Vector3 sum = Vector3.zero;
                            foreach (var c in cl[i].cells) sum += _grid.CellToWorld(c.x, c.y);
                            tapAt = sum / cl[i].cells.Count;
                        }
                        tapAt.z = anchor.z;      // keep the badge's depth so sorting is unchanged
                        // the word(s) in this cluster, for the word-instead-of-score pop (2026-10-23)
                        var words = new List<string>(); bool goalHit = false; int longest = 0;
                        foreach (var pw in rules.PrimedRegistry.GetAllPrimedWords())
                            if (pw?.Cells != null && pw.Word != null && pw.Word.Length > 1 && pw.Cells.Exists(c => cl[i].cells.Contains(c)) && !words.Contains(pw.Word.ToUpperInvariant()))
                            {
                                words.Add(pw.Word.ToUpperInvariant()); longest = Mathf.Max(longest, pw.Word.Length);
                                if (BonusPopup.GOAL_WORD_GOLD && LevelRules.Goals.Count > 0 && rules.WordCellsHitGoal(pw.Cells)) goalHit = true;
                            }
                        // 10-31 (Spencer OK'd the mock goal_word_pop.png): GOLD = this word made goal progress; LONG words pop bigger.
                        BonusPopup.NextPopGold = goalHit; BonusPopup.NextPopLength = longest;
                        OwlPortrait.Play(longest >= 7 ? "starry" : longest >= 6 ? "proud" : (goalHit && Random.value < 0.35f ? "wink" : "happy"));   // 11-01 owl (7+ letters = starry)
                        string label = words.Count == 0 ? null : words.Count <= BonusPopup.WORD_POPUP_MAX_WORDS ? string.Join("  ", words) : $"{words[0]}  +{words.Count - 1} MORE";
                        _tapPopWords.Clear(); foreach (var w in words) _tapPopWords.Add(w);   // these already got their pop-up here
                        bool firesSpecial = !WORD_POP_OVER_BLAST && cl[i].cells.Exists(c => { var sd = rules.GetCell(c.x, c.y); return sd != null && sd.SpecialKind != RulesEngine.SPECIAL_NONE; });
                        if (!firesSpecial) ClusterOutline.Instance.PopPayout(tapAt, cl[i].payout, cl[i].wordCount >= 2, fs, bi, bc, label);
                    }
                    break;
                }
            }

            HapticsManager.Light();
            _tapAtTime = Time.unscaledTime;
            StartCoroutine(TapDetonateSequence(cell));
            return true;
        }

        private IEnumerator TapDetonateSequence(Vector2Int cell)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (rules == null || grid == null) { _tapDetonating = false; yield break; }

            if (JamHint.Instance != null) JamHint.Instance.ClearHint();
            // Drop the outline of the web being blown up — but ONLY that one. Clearing every
            // outline made unrelated clusters blink out and back on each pop. 2026-09-09.
            ClusterOutline.Instance?.RefreshExcluding(cell);
            MatchController.Instance?.BeginProcessing();
            IsInteractable = false;

            // Size of the web being fired, read BEFORE the blast consumes it — decides whether the
            // board gets pushed down afterwards (SurvivalManager.WebDropRows). 2026-10-20.
            int webWords = 0;
            {
                var clusters = rules.GetPrimedClusters();
                for (int i = 0; i < clusters.Count; i++)
                    if (clusters[i].cells != null && clusters[i].cells.Contains(cell)) { webWords = clusters[i].wordCount; break; }
            }

            if (!rules.BeginTapDetonation(cell, MatchController.PLAYER_HUMAN))
            {
                IsInteractable = true;
                MatchController.Instance?.EndProcessing();
                _tapDetonating = false;
                yield break;
            }
            SurvivalManager.Instance?.NotifyBlastFired();   // blast-limit prototype: one web = one blast

            bool resolving = true;
            int  guard     = 0;
            while (resolving && guard++ < 200)
            {
                var step = rules.NextStep();
                if (step == null) break;
                WDLog.Log($"[ArmedWeb] {step.Phase} exploded={(step.ExplodedCells != null ? step.ExplodedCells.Count : 0)}");

                switch (step.Phase)
                {
                    // 2026-09-03: words formed by the CASCADE after a detonation get the
                    // same green scored sequence a dropped word used to get. Without this
                    // case they scored and primed completely silently — the tap loop
                    // only handled explode/gravity.
                    case RulesEngine.ResolutionPhase.WordsScored:
                    {
                        // Timing MIRRORS the old drop path exactly. Two mistakes made it
                        // read abrupt + clunky: (1) a per-word yield inside the loop, which
                        // double-staggered against PlayWordScored's own wordIndex-driven
                        // stagger; (2) no trailing settle, so it cut straight to the next
                        // phase. Fire all words together, then ONE wait — shorter if a
                        // detonation is about to follow.
                        bool detonationComing = rules.PeekHasTriggers();
                        int  _flashedWords    = 0;   // words we actually put on screen this step
                        if (step.ScoredWords != null)
                        {
                            // Was a hardcoded magenta that no longer matched the armed tile.
                            // Read the real constant so it can never drift again. 2026-09-09.
                            Color primedMagenta = Tile.PRIMED_GLOW;
                            int wIdx = 0;

                            // EXPERIMENT (2026-09-10 Spencer): flash EVERY word formed, not just
                            // the ones that scored. ScoredWords is filtered — a word already
                            // scored this run pays nothing and so never flashed. NewWords is
                            // everything the board detected. Flip FLASH_EVERY_WORD to false to
                            // go back to flashing only what pays.
                            // Carry the word + its score alongside the cells: a NewWord that
                            // scored nothing has no FinalScore, so the points popup is skipped
                            // for it rather than announcing a zero.
                            var flashCells = new System.Collections.Generic.List<(System.Collections.Generic.List<Vector2Int> cells, string word, int score)>();
                            if (WordDropFX.FLASH_EVERY_WORD && step.NewWords != null)
                            { foreach (var nw in step.NewWords) if (nw.Cells != null) flashCells.Add((nw.Cells, nw.Word, 0)); }
                            else
                            { foreach (var sw in step.ScoredWords) if (sw.Cells != null) flashCells.Add((sw.Cells, sw.Word, sw.FinalScore)); }

                            foreach (var fw in flashCells)
                            {
                                var scoredTiles = new System.Collections.Generic.List<Tile>();
                                foreach (var sc in fw.cells)
                                {
                                    Tile t = grid.GetTile(sc.x, sc.y);
                                    if (t != null) scoredTiles.Add(t);
                                }
                                if (scoredTiles.Count > 0 && WordDropFX.Instance != null)
                                { WordDropFX.Instance.PlayWordScored(scoredTiles, primedMagenta, wIdx); _flashedWords++; }

                                HapticsManager.Light();

                                if (!detonationComing && BonusPopup.Instance != null && scoredTiles.Count > 0)
                                {
                                    Vector3 wc = Vector3.zero;
                                    for (int st = 0; st < scoredTiles.Count; st++)
                                        if (scoredTiles[st] != null) wc += scoredTiles[st].transform.position;
                                    wc /= Mathf.Max(1, scoredTiles.Count);
                                    if (fw.score > 0) BonusPopup.Instance.ShowWordScore(fw.word, fw.score, wc);
                                }

                                wIdx++;
                            }
                        }
                        // ── THE TAP DELAY (2026-09-08 Spencer) ────────────────────────────
                        // "cascade words explode instantly on click but the regular words have
                        // a sort of delay." Two bugs stacked here.
                        //
                        // 1. This wait ran even when NOTHING was flashed. On the first step of a
                        //    tap there are no newly-scored words to show, so the game sat waiting
                        //    for an animation that was not playing. Dead time, every tap.
                        //
                        // 2. detonationComing comes from PeekHasTriggers(), which reads
                        //    _stepPendingWords — and BeginTapDetonation initialises that list
                        //    EMPTY. So it always answered "no detonation follows" and took the
                        //    0.3s branch, even though a tap ALWAYS ends in a detonation. Mid
                        //    cascade the list is populated and it correctly took 0.1s. That 3x
                        //    difference is exactly the one Spencer could feel.
                        //
                        // detonationComing is left alone above because it also gates the score
                        // popup; only the TIMING is corrected here.
                        bool detonationCertain = detonationComing || _tapDetonating;
                        if (_flashedWords > 0)
                            yield return WaitCache.Get(detonationCertain ? 0.1f : 0.3f);
                        break;
                    }

                    case RulesEngine.ResolutionPhase.TriggersFound:
                        CacheBurstTriggers(step);
                        // No anticipation beat on a TAP. That 0.05s pause was tuned for
                        // AUTO-triggered detonations, where the player did not ask for it.
                        // A tap is a direct command and must fire immediately.
                        break;

                    case RulesEngine.ResolutionPhase.Exploding:
                    {
                        DefrostThawedTiles(grid, step);
                        PopTiming("explode step start");
                        yield return PreBurstSpecials(grid, step);   // the specials swell + flash, THEN blow (2026-10-22)
                        PopTiming("pre-burst done");
                        ApplySpecialStep(grid, step);   // special tiles born / fired this step (2026-10-21)
                        // Drop the outline of EVERY cluster actually going up, not just the one
                        // the player tapped. When a resolution took out more than one cluster,
                        // the others' outline and payout badge hung over empty cells. Driving it
                        // from the exploded cells means the display cannot disagree with what the
                        // rules actually did. 2026-09-10 Spencer.
                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                            ClusterOutline.Instance?.RefreshExcludingCells(
                                new System.Collections.Generic.HashSet<Vector2Int>(step.ExplodedCells));
                        if (_tapAtTime > 0f)
                        {
                            _tapAtTime = 0f;
                        }
                        if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        {
                            var dying = new System.Collections.Generic.List<Tile>();
                            foreach (var c in step.ExplodedCells)
                            {
                                Tile t = grid.GetTile(c.x, c.y);
                                if (t != null) dying.Add(t);
                            }
                            // ── THE MISSING AWARD (2026-09-09 Spencer) ────────────────────
                            // "the score isnt updating when the letters fly up to the ui."
                            // It was never updating at all: the tap path computed the detonation
                            // score in RulesEngine and NOTHING handed it to ScoreManager. Only the
                            // old drop paths in MatchController ever did that, and those are dead.
                            // So every detonation was scored and thrown away.
                            // DEPOSIT, don't award — each letter that lands on the score
                            // withdraws its share, so the counter climbs on impact.
                            //
                            // ⚠️ MUST STAY ABOVE ExplodeWordsThenSplash (moved here 2026-10-03).
                            // It used to sit BELOW it. Both FX calls are `yield return`ed, so the
                            // whole explode + fly-up sequence ran to completion first — the letters
                            // had already LANDED and called AwardOneFlyShare against an EMPTY bank
                            // before this line ever deposited. debug_log proof:
                            //     [556] AWARD share (pending=0, remaining=3)   <- 3 landings, no money
                            //     [571] DEPOSIT 20 (pending was 0, remaining=0) <- 15 frames too late
                            // Net effect: the counter ran ONE DETONATION BEHIND (the next blast's
                            // landings paid the previous blast), and the LAST blast of a level had
                            // nobody left to pay it, so ScoreManager's 1.6s FLY_FLUSH_TIMEOUT did —
                            // 25 TIMEOUT-FLUSH lines in a single session. That late score is also
                            // what made the level-complete checkmark + ding feel delayed: the score
                            // crossed the target 1.6s late, and then the objective's deferToLand
                            // handoff burned its OWN 1.6s deadline waiting for landings that were
                            // already over.
                            // Braces added too — the `if` guarded only the log line, so the deposit
                            // was running unconditionally (harmless only because DepositFlyScore
                            // early-returns on <= 0).
                            if (step.TotalScore > 0)
                            {
                                WDLog.Log($"[ScoreLag] t={Time.unscaledTime:F2} step {step.Phase} TotalScore={step.TotalScore}");
                                ScoreManager.Instance?.DepositFlyScore(step.TotalScore);
                            }

                            // BIG MOMENT decided at the START of the blast (10-31, Spencer: the boom was "not firing at the right time" -
                            // it played after the ~0.75s explode+splash). The boom lands with the explosion; the shake below uses the same call.
                            int wordsHere = Mathf.Max(step.PoppedWords != null ? step.PoppedWords.Count : 0, step.TriggerWords != null ? step.TriggerWords.Count : 0,
                                                      step.ChainTriggeredCount);   // (not _lastBurstWords: it can still hold the previous blast's words)
                            wordsHere = Mathf.Max(wordsHere, RulesEngine.PendingSwapWordCount); RulesEngine.PendingSwapWordCount = 0;   // the swap's words, counted once
                            bool bigMoment = step.TotalScore > 0 && (wordsHere >= BIG_MOMENT_WORDS || (step.SpecialsFired != null && step.SpecialsFired.Count > 0));
                            WDLog.Log($"[BigMoment] words={wordsHere} specials={step.SpecialsFired?.Count ?? 0} depth={step.ChainDepth} -> {(bigMoment ? "BOOM" : "pop")}");   // TEMP DIAG 10-31
                            // 10-31 Spencer: "on places where a bomb goes off though it should just be the bomb sound" - specials keep their own sound
                            // ARM the boom (10-31 v2): it plays in place of the first explosion pop, as the tiles burst, and the blast's
                            // other pops stay silent. Specials keep their own sound; an EARNED plane doesn't fly here, so it doesn't block it.
                            bool boomHere = bigMoment && (step.SpecialsFired == null || step.SpecialsFired.Count == 0);
                            // v5 (10-31, Spencer: "the blast needs to line up with the screen shake" -> v4 boom at the late shake was "even
                            // more off"): the boom AND the big shake both fire at the FIRST burst (GameAudio.BoomInsteadOfPop, which also
                            // silences that blast's other pops); the end-of-blast shake stays the normal small one.
                            if (bigMoment) OwlPortrait.Play(wordsHere >= 3 ? "starry" : "wow");   // 11-01 owl: double word / big blast; 3+ words in one move = starry
                            if (boomHere) GameAudio.Instance?.ArmBoom(() => GridManager.Instance?.ShakeBoard(0.025f + 0.045f * 0.9f, 0.09f + 0.05f * 0.9f));

                            if (dying.Count > 0 && WordDropFX.Instance != null)
                            {
                                FirePerWordBurst();
                                FireTileFlashBoxes(dying);
                                int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dying.Count;
                                LaunchThemeFlights(dying, grid);   // animal-word letters fly to the goal instead of exploding (2026-10-23)
                                yield return WordDropFX.MaybeBigPopAndHold(dying, step.SpecialsFired != null && step.SpecialsFired.Count > 0);
                                PopTiming("big-pop hold done");
                                yield return ExplodeWordsThenSplash(step, dying, step.ChainDepth, wLen);
                                PopTiming("explode + splash done");
                            }
                            if (boomHere) GameAudio.Instance?.EndBoomBlast();

                            // ONE shake, HERE, at the blast — sized by what the blast was worth.
                            // A single-word detonation previously got no shake at all (only the
                            // multi-word crescendo and a vault case ever fired one), so the only
                            // punch the player felt was the score landing, a beat too late.
                            if (step.TotalScore > 0)
                            {
                                // Magnitude is in WORLD UNITS and the cell is ~0.8, so 0.18 moved the
                                // whole board a fifth of a tile — over a long duration that reads as
                                // a slow sweep, not an impact. The shipped shakes were 0.06-0.09.
                                // Short and small: an impact is a jolt, not a slide. 2026-09-09.
                                float sm = Mathf.Clamp01(step.TotalScore / 600f);
                                // BIG MOMENT = 2+ words in one blast (a double word) or a special going off: harder shake + the BOOM
                                // (10-31, Spencer: "the pop sound doesnt really do a bigger explosion justice"). The first wiring went
                                // into WordDropFX's tier path, which the swap game never runs - this is the live pop.
                                if (bigMoment && !boomHere) sm = Mathf.Max(sm, 0.8f);   // (a boom blast already got its big shake at the burst)
                                GridManager.Instance?.ShakeBoard(0.025f + 0.045f * sm, 0.09f + 0.05f * sm);
                            }

                            grid.RemoveTiles(step.ExplodedCells);

                            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                                SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                            Vector3 centre = Vector3.zero;
                            foreach (var t in dying) if (t != null) centre += t.transform.position;
                            centre /= Mathf.Max(1, dying.Count);
                            if (step.DetonationBonus > 0 && BonusPopup.Instance != null && dying.Count > 0)
                                BonusPopup.Instance.ShowDetonation(TakeBurstWords(), step.DetonationBonus, centre, step.ChainDepth);
                            ApplyDetonationRefillRewards(step, centre, 0);
                        }
                        break;
                    }

                    case RulesEngine.ResolutionPhase.GravityApplied:
                        PopTiming("gravity start");
                        yield return StartCoroutine(grid.ApplyGravity());
                        PopTiming("gravity done");
                        // Gravity moves tiles, so primed words end up on DIFFERENT tile
                        // objects with no glow — re-apply it. SILENTLY (flashNew:false):
                        // the tiles that just landed are about to get the GREEN scored
                        // flash on the next step, and firing the magenta arm flash here
                        // too made them double-animate and read as a glitch. The green
                        // flash is the celebration; magenta eases in behind it.
                        rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
                        // No pad here — ApplyGravity already yields until the tiles have
                        // landed, so any extra wait is dead air between the landing and
                        // the scored flash.
                        break;

case RulesEngine.ResolutionPhase.Complete:
                        // NOTE: no flush here. The resolution finishes long before the
                        // letters land — for a single word the sequence completes in a
                        // fraction of a second while the letters are airborne for ~0.7s —
                        // so flushing here paid the whole score out early and the counter
                        // ran up before anything hit. The safety flush is time-driven in
                        // ScoreManager instead. 2026-09-09 Spencer.
                        resolving = false;
                        rules.FinalizeDrop();
                        // SyncToRulesState reconciles tiles IN PLACE. (RebuildFromRulesEngine
                        // tears down and recreates GridRoot — which now also destroys and
                        // rebuilds the board panel, so the whole board blinked out for a
                        // frame after every detonation.)
                        grid.SyncToRulesState(rules); rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
                        break;

                    default:
                        break;
                }
            }

            // Big web -> push the board down. Inside the processing gate and awaited, so nothing
            // (rise, swap, another tap) can act on the board while it falls.
            int dropRows = SurvivalManager.WebDropRows(webWords);
            if (dropRows > 0 && SurvivalManager.Instance != null)
                yield return StartCoroutine(SurvivalManager.Instance.DropBoardRows(dropRows, webWords));

            IsInteractable = true;
            MatchController.Instance?.EndProcessing();
            _tapDetonating = false;
            SurvivalManager.Instance?.NotifyBlastResolved();   // blast-limit: refill + out-of-blasts check
            MaybeAutoPop();                                    // instant pop: words the fall made go next
            // Whole chain done (nothing else popping)? A big play earns ONE reward shower.
            if (_bigPlayThisChain && !_tapDetonating && !_autoPopPending)
            {
                _bigPlayThisChain = false;
                SurvivalManager.Instance?.QueueShower();
            }
        }

        /// <summary>Scores, primes and presents a set of freshly-detected words using the FULL
        /// pipeline — chain multiplier, echo bonus, gold multiplier, scored-key registration,
        /// primed glow, haptics, bonus popup, survival hooks and the global fuse reset.
        ///
        /// EXTRACTED 2026-10-19 from BoardSwapTurnSequence so the new SWIPE-TO-CLAIM path
        /// (SwipeWordInput) scores through the exact same code. Writing a second scoring path
        /// is the documented desync landmine: the comment inside this block records that an
        /// earlier parallel path called CalculateWordScore directly and thereby skipped the
        /// chain/echo/gold handling, leaving gold tiles reusable and echo streaks unrewarded.
        /// One pipeline, two callers.</summary>
        private System.Collections.Generic.HashSet<int> PrimeAndPresentWords(
            System.Collections.Generic.List<RulesWordMatch> allNew, RulesEngine rules, GridManager grid)
        {
            if (allNew == null || allNew.Count == 0 || rules == null || grid == null)
                return new System.Collections.Generic.HashSet<int>();
            // Score, prime, and show visuals for each word — same as RewriteTurnSequence
            var registry = rules.PrimedRegistry;
            int globalTurn = rules.GlobalTurn;
            int playerIdx = MatchController.PLAYER_HUMAN;
            var justPrimedIds = new HashSet<int>();

            // Mirror DoScoreAndPrime's cluster-bonus logic: when multiple
            // words score in one swap, each gets a chainStep boost equal
            // to (count - 1). Without this, swap multi-word scores
            // collapse to flat per-word values.
            int swapClusterChainStep = Mathf.Max(0, allNew.Count - 1);
            RulesEngine.NoteSwapWords(allNew);   // two words that don't cross = an airplane (2026-10-23)
            for (int i = 0; i < allNew.Count; i++)
            {
                var match = allNew[i];
                // Apply the same scoring pipeline drops/rewrites use —
                // chain multiplier, echo bonus, gold tile multiplier.
                // Previously: match.Score = RulesEngine.CalculateWordScore(...)
                // skipped all of this, leaving gold tiles reusable + echo
                // streaks unrewarded on swap-created words.
                int baseScore = RulesEngine.CalculateWordScore(match.Word, match.WildLetterIndices);
                float chainMult = (swapClusterChainStep > 0)
                    ? 1f + Mathf.Min(swapClusterChainStep, RulesEngine.CHAIN_DEPTH_SCALE_CAP) * 0.5f
                    : 1f;
                int chainBoosted = Mathf.RoundToInt(baseScore * chainMult);
                int echoBonus = rules.ConsumeEchoBonus(match.Word, playerIdx);
                bool isGoldWord = rules.HasGoldTile(match);
                int bonusMult = rules.ConsumeGoldAndGetMultiplier(match);
                match.Score = (chainBoosted + echoBonus) * bonusMult;
                rules.RegisterScoredKey(match.Word + "|" + match.CellKey);

                int fuse = rules.GetFuseLengthPublic(match.Word.Length);
                int primedId = registry.AddPrimedWord(match.Word, match.Cells, playerIdx, globalTurn, globalTurn + fuse, match.Score, isGoldWord,
                    RulesEngine.Instance != null && RulesEngine.Instance.HasHighValueLetter(match));
                justPrimedIds.Add(primedId);
                // This swap path primes directly (no RulesEngine.OnWordScored) — notify
                // the objective so swap/claim-made words still count. 2026-06-08.
                ObjectiveManager.Instance?.NotifyWordScored(match.Word, playerIdx);

                // Visual: primed glow + particles
                var scoredTiles = new System.Collections.Generic.List<Tile>();
                if (match.Cells != null)
                {
                    // BRANCH GATE: this primes from the word MATCH, not from the registry,
                    // so it painted words the rules had already refused ("LIDS" armed while
                    // the log said rejected). The registry is authoritative by this point —
                    // the word has either been accepted into it or it has not. 2026-09-09.
                    bool armedA = RulesEngine.Instance != null && match.Cells != null
                        && match.Cells.Count > 0
                        && RulesEngine.Instance.GetPrimedWordsContainingPublic(match.Cells[0]) > 0;
                    if (armedA)
                    for (int c = 0; c < match.Cells.Count; c++)
                    {
                        Tile t = grid.GetTile(match.Cells[c].x, match.Cells[c].y);
                        if (t != null)
                        {
                            t.SetPrimedGlow(RulesEngine.Instance != null && RulesEngine.Instance.HasHighValueLetter(match)
                                    ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW,
                                    playFlash: true, fuseRemaining: fuse, maxAge: match.Word.Length <= 3 ? 25f : match.Word.Length == 4 ? 30f : match.Word.Length == 5 ? 38f : 45f);
                            GameParticles.Instance?.PlayPrimed(t.transform.position);
                            scoredTiles.Add(t);
                        }
                    }
                }

                // 2026-05-29: removed duplicate PlayWordScored call —
                // GameVisualBridge.cs:560 is the canonical word-presentation
                // path (per the MatchController.cs:670 comment). When the
                // edit/rewrite swap flow ran through here AND through
                // GameVisualBridge, the pop SFX fired twice. Haptics still
                // fire here since GameVisualBridge doesn't handle haptics.
                HapticsManager.Light();

                if (BonusPopup.Instance != null && scoredTiles.Count > 0)
                {
                    Vector3 center = Vector3.zero;
                    for (int c = 0; c < scoredTiles.Count; c++)
                        if (scoredTiles[c] != null) center += scoredTiles[c].transform.position;
                    center /= Mathf.Max(1, scoredTiles.Count);
                    BonusPopup.Instance.ShowWordScore(match.Word, match.Score, center);
                }

                // Survival rewrite meter
                if (MatchController.Instance != null)
                    MatchController.Instance.SurvivalWordScored();

                // Survival long-word reward (5+/6+/7+) — was previously
                // only called from the AI bridge path with a PLAYER_HUMAN
                // gate that the AI bridge never satisfies, leaving 5+
                // letter rewards unreachable. Survival is solo-only so
                // SurvivalManager.IsSurvivalMode is the right gate.
                if (SurvivalManager.IsSurvivalMode
                    && GameVisualBridge.Instance != null
                    && !string.IsNullOrEmpty(match.Word))
                {
                    GameVisualBridge.Instance.TriggerSurvivalLongWordReward(
                        match.Word, scoredTiles, isPlayer: true);
                }
            }
            GameAudio.Instance?.PlayTilePrimed();

            // Global fuse reset: sync all existing primed words to the new
            // fuse timeline, same as BeginDrop/BeginRewrite paths do via
            // DoScoreAndPrime. Without this the board-swap path leaves
            // pre-existing primes on their original, stale timers.
            rules.ResetExistingPrimedWordsExternal(justPrimedIds);

            // Swaps intentionally do NOT count as a move (Balatro-discard
            // analog — no hand card consumed, pure board reshuffle).
            // They DO contribute score (CurrentStageScore auto-derives
            // from PlayerScore so swap points count automatically).
            // Trigger the clear check in case this swap crossed target.
            if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                SurvivalManager.Instance.CheckStageClear();
            return justPrimedIds;
        }

        /// <summary>Match-3 style slide for a board swap (see BoardSwapTurnSequence). false = the old
        /// shake-in-place. Duration/overshoot from the research: 0.18-0.25s, ease-out with a small
        /// overshoot. 2026-10-20.</summary>
        public static bool  MATCH3_SWAP_ANIM = true;
        public static float MAGIC_LIFT = 0.22f, MAGIC_CROSS = 0.42f, MAGIC_LAND = 0.14f;   // Free Swap booster's magic swap
        /// <summary>false (2026-10-23 Spencer) = no tap-a-tile-then-another swap and no select click; swaps are drag-only and a
        /// pressed tile glows (SwipeWordInput). Power-ups still take taps for double-tap-to-fire.</summary>
        public static bool  TAP_TO_SWAP = false;
        public static bool  SWAP_LAND_SFX = false;   // landing clack after a letter exchange (off 2026-10-22)
        public static bool  GEMS_LOCKED   = false;   // gem tiles can't be swapped when true (2026-10-22 pivot exploit) — 2026-10-23 Spencer: unlocked "for now"
        public static float SWAP_SLIDE_DUR   = 0.20f;
        public static float SWAP_OVERSHOOT   = 1.2f;   // 1.70158 is the textbook "back"; softer reads better on tiles

        private static float EaseOutBack(float k, float c1)
        {
            float c3 = c1 + 1f, x = k - 1f;
            return 1f + c3 * x * x * x + c1 * x * x;
        }

        /// <summary>A refused drag-swap leans toward the neighbour and springs back, so "not allowed"
        /// reads as a reaction instead of nothing. Pattern from match-3 convention.</summary>
        private IEnumerator NudgeRefusedSwap(Vector2Int a, Vector2Int b)
        {
            var grid = GridManager.Instance;
            Tile ta = grid != null ? grid.GetTile(a.x, a.y) : null;
            if (ta == null) yield break;
            // 11-16: a second refusal while the first nudge is still leaning read the LEANED position as home and left the tile
            // parked off its cell for good (mashing a refused swap). One nudge per tile at a time.
            if (!_nudging.Add(ta)) yield break;
            const float dur = 0.22f;
            ta.MarkAnimating(dur + 0.05f);
            ta.LiftForFx(dur + 0.05f);   // the bump toward the other tile draws OVER it (2026-10-23 Spencer) — self-restoring
            Vector3 from = ta.transform.position;
            Vector3 dir  = (grid.CellToWorld(b.x, b.y) - grid.CellToWorld(a.x, a.y)) * 0.28f;
            float t = 0f;
            while (t < dur)
            {
                t += Time.deltaTime;
                float k = Mathf.Clamp01(t / dur);
                float lean = Mathf.Sin(k * Mathf.PI);                                // out and back
                float shake = Mathf.Sin(k * Mathf.PI * 6f) * (1f - k) * 0.03f;       // small "no" on the way home
                ta.transform.position = from + dir * lean + new Vector3(shake * grid.CellSize, 0f, 0f);
                yield return null;
            }
            if (ta != null) ta.transform.position = from;
            _nudging.Remove(ta);
        }
        private readonly System.Collections.Generic.HashSet<Tile> _nudging = new System.Collections.Generic.HashSet<Tile>();

        private static System.Collections.Generic.List<RulesWordMatch> s_bouncePre;   // what the bounce pre-check saw (LATE BOUNCE diag)
        public static bool  BOUNCE_NO_WORD   = true;
        public static bool  SPECIALS_LOCKED  = true;    // 11-03 Spencer: the DYNAMITE can't be moved (rockets/bombs/discos can; two power-ups still combo)
        public static bool  BOUNCE_SPECIALS  = true;    // 10-30 Spencer: specials follow the same rule (no word -> bounce); double-tap still fires one for a move, two specials still combo    // false = the old rule: every swap stays and costs a move
        public static float BOUNCE_OUT = 0.13f, BOUNCE_HOLD = 0.05f, BOUNCE_BACK = 0.17f;
        /// <summary>No-word swap: the two tiles trade places, hold a beat, and slide home with a small "no" shake - the match-3
        /// bounce. Visual only (the rules never changed); input waits for it.</summary>
        private IEnumerator BounceNoWordSwap(Vector2Int a, Vector2Int b)
        {
            var grid = GridManager.Instance;
            Tile ta = grid != null ? grid.GetTile(a.x, a.y) : null, tb = grid != null ? grid.GetTile(b.x, b.y) : null;
            if (ta == null || tb == null) yield break;
            IsInteractable = false;
            float total = BOUNCE_OUT + BOUNCE_HOLD + BOUNCE_BACK + 0.05f;
            ta.MarkAnimating(total); tb.MarkAnimating(total); ta.LiftForFx(total);
            Vector3 pa = grid.CellToWorld(a.x, a.y), pb = grid.CellToWorld(b.x, b.y);
            pa.z = ta.transform.position.z; pb.z = tb.transform.position.z;
            GameAudio.Instance?.PlaySwapSound();
            float t = 0f;
            while (t < BOUNCE_OUT) { t += Time.deltaTime; float k = Mathf.SmoothStep(0f, 1f, t / BOUNCE_OUT);
                ta.transform.position = Vector3.Lerp(pa, pb, k); tb.transform.position = Vector3.Lerp(pb, pa, k); yield return null; }
            GameAudio.Instance?.PlaySwapReject();   // 11-07: own sound for a rejected swap
            HapticsManager.Light();
            yield return new WaitForSeconds(BOUNCE_HOLD);
            t = 0f;
            while (t < BOUNCE_BACK) { t += Time.deltaTime; float k = Mathf.Clamp01(t / BOUNCE_BACK); float e = Mathf.SmoothStep(0f, 1f, k);
                Vector3 shake = new Vector3(Mathf.Sin(k * Mathf.PI * 5f) * (1f - k) * 0.025f * grid.CellSize, 0f, 0f);
                ta.transform.position = Vector3.Lerp(pb, pa, e) + shake; tb.transform.position = Vector3.Lerp(pa, pb, e) - shake; yield return null; }
            if (ta != null) ta.transform.position = pa;
            if (tb != null) tb.transform.position = pb;
            IsInteractable = true;
        }

        private IEnumerator BoardSwapTurnSequence(int col1, int row1, char newLetter1, int col2, int row2, char newLetter2)
        {
            if (JamHint.Instance != null) JamHint.Instance.ClearHint();
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            var rules = RulesEngine.Instance;
            var grid = GridManager.Instance;

            if (rules == null || grid == null)
            {
                IsInteractable = true;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
                yield break;
            }

            // Animate both tiles — shake, swap letters, squish (same as edit)
            Tile tile1 = grid.GetTile(col1, row1);
            Tile tile2 = grid.GetTile(col2, row2);

            float shakeDur = 0.2f;
            float elapsed = 0f;
            float cellSize = grid.CellSize;
            float posJitter = cellSize * 0.08f;
            float rotJitter = 10f;
            Vector3 restPos1 = tile1 != null ? tile1.transform.position : Vector3.zero;
            Vector3 restPos2 = tile2 != null ? tile2.transform.position : Vector3.zero;
            if (MATCH3_SWAP_ANIM)
            {
                // Home = the CELL, not wherever the transform happens to be — a refused-swap nudge or
                // a landing squish can still be moving a tile when the next swap starts. 2026-10-20.
                Vector3 slide = new Vector3(grid.BoardSlideOffsetX, 0f, 0f);
                restPos1 = grid.CellToWorld(col1, row1) + slide;
                restPos2 = grid.CellToWorld(col2, row2) + slide;
            }

            if (GameAudio.SWAP_SHUFFLE_SFX) GameAudio.Instance?.PlayShuffle(); else GameAudio.Instance?.PlaySwapSound();   // 2026-10-22: tile_fall 1..6 in turn, not the shuffle

            // Hold both tiles at a steady bright teal through the shake+exchange (kills the selection breathe so the
            // dramatic wobble reads as "charged"). Re-asserted after SetLetter below, faded right after the squish
            // — BEFORE the pop — so the teal never lingers through the detonation. 2026-07-10 Spencer.
            if (tile1 != null) tile1.HoldEditGlow();
            if (tile2 != null) tile2.HoldEditGlow();

            if (MATCH3_SWAP_ANIM && tile1 != null && tile2 != null)
            {
                // ── MATCH-3 SLIDE (2026-10-20 Spencer: research what a match-3 swap looks like) ──
                // Industry pattern: the two pieces physically trade places in ~0.18-0.25s on an
                // ease-out with a slight overshoot, then land. The tile the player moved passes OVER
                // the other (lifted + swells), the other dips under. Replaces the old shake-in-place,
                // which was the EDIT animation and read as "a letter changed", not "two tiles moved".
                float dur = SWAP_SLIDE_DUR;
                bool magic = _freeSwap && BoosterFX.ON && BoosterFX.Instance != null;   // FREE SWAP booster: the MAGIC SWAP (2026-10-29)
                if (magic) dur = MAGIC_LIFT + MAGIC_CROSS + MAGIC_LAND;
                // Settle any tap-select LIFT first (Tile.SELECT_LIFT) — a breathing selected tile would fight
                // the slide's scale and its base scale would be captured mid-breath.
                tile1.SetEditSelected(false);
                tile2.SetEditSelected(false);
                tile1.MarkAnimating(dur + 0.1f);          // keep sync/invariant snappers off them mid-slide
                tile2.MarkAnimating(dur + 0.1f);
                tile1.LiftForFx(dur + 0.05f);             // the moved tile travels on top
                Vector3 baseScale1 = tile1.transform.localScale, baseScale2 = tile2.transform.localScale;
                float t = 0f;
                if (magic)
                {   // MAGIC SWAP (2026-10-29 Spencer: free swap "needs to look a bit juicy ... special rather than just swapping out letters").
                    // 1 LIFT: both tiles rise and swell as the glove swoops in. 2 CROSS: they trade places on crossing arcs (one over,
                    // one under) with a full spin each, trailing sparkles. 3 LAND: squash back down with a burst of stars on each cell.
                    tile2.LiftForFx(dur + 0.05f);
                    BoosterFX.Instance.Glove(restPos1, restPos2, MAGIC_LIFT + MAGIC_CROSS);
                    GameAudio.Instance?.PlayWhooshFast();
                    float c = grid.CellSize; Vector3 up = new Vector3(0f, c * 0.35f, 0f);
                    while (t < MAGIC_LIFT)
                    {
                        t += Time.deltaTime; float k = Mathf.Clamp01(t / MAGIC_LIFT); float e = EaseOutBack(k, 1.6f);
                        tile1.transform.position = restPos1 + up * e; tile2.transform.position = restPos2 + up * e;
                        tile1.transform.localScale = baseScale1 * (1f + 0.3f * e); tile2.transform.localScale = baseScale2 * (1f + 0.3f * e);
                        yield return null;
                    }
                    BoosterFX.Instance.SwapTrail(tile1.transform, MAGIC_CROSS); BoosterFX.Instance.SwapTrail(tile2.transform, MAGIC_CROSS);
                    Vector3 p1 = restPos1 + up, p2 = restPos2 + up, mid = (p1 + p2) * 0.5f;
                    Vector3 dir = (p2 - p1); Vector3 perp = new Vector3(-dir.y, dir.x, 0f).normalized; if (perp.y < 0f) perp = -perp;
                    float bow = Mathf.Max(c * 1.1f, dir.magnitude * 0.35f);
                    Vector3 cA = mid + perp * bow, cB = mid - perp * bow;
                    float t2 = 0f;
                    while (t2 < MAGIC_CROSS)
                    {
                        t2 += Time.deltaTime; float k = Mathf.Clamp01(t2 / MAGIC_CROSS); float e = k * k * (3f - 2f * k); float u = 1f - e;
                        tile1.transform.position = u * u * p1 + 2f * u * e * cA + e * e * p2;
                        tile2.transform.position = u * u * p2 + 2f * u * e * cB + e * e * p1;
                        tile1.transform.rotation = Quaternion.Euler(0f, 0f, 360f * e); tile2.transform.rotation = Quaternion.Euler(0f, 0f, -360f * e);
                        float sw = 1.3f + 0.12f * Mathf.Sin(k * Mathf.PI);
                        tile1.transform.localScale = baseScale1 * sw; tile2.transform.localScale = baseScale2 * sw;
                        yield return null;
                    }
                    tile1.transform.rotation = Quaternion.identity; tile2.transform.rotation = Quaternion.identity;
                    float t3 = 0f;
                    while (t3 < MAGIC_LAND)
                    {
                        t3 += Time.deltaTime; float k = Mathf.Clamp01(t3 / MAGIC_LAND);
                        tile1.transform.position = Vector3.Lerp(restPos2 + up, restPos2, k * k); tile2.transform.position = Vector3.Lerp(restPos1 + up, restPos1, k * k);
                        float sq = Mathf.Lerp(1.3f, 1f, k * k);
                        tile1.transform.localScale = new Vector3(baseScale1.x * sq * (1f + 0.1f * Mathf.Sin(k * Mathf.PI)), baseScale1.y * sq * (1f - 0.1f * Mathf.Sin(k * Mathf.PI)), baseScale1.z);
                        tile2.transform.localScale = new Vector3(baseScale2.x * sq * (1f + 0.1f * Mathf.Sin(k * Mathf.PI)), baseScale2.y * sq * (1f - 0.1f * Mathf.Sin(k * Mathf.PI)), baseScale2.z);
                        yield return null;
                    }
                    BoosterFX.Instance.SwapLand(restPos1); BoosterFX.Instance.SwapLand(restPos2);
                    GameAudio.Instance?.PlayScorePowerup();
                    t = dur;   // skip the normal slide loop
                }
                while (t < dur)
                {
                    t += Time.deltaTime;
                    float k = Mathf.Clamp01(t / dur);
                    float e = EaseOutBack(k, SWAP_OVERSHOOT);
                    float arc = Mathf.Sin(k * Mathf.PI);   // 0 -> 1 -> 0 across the move
                    tile1.transform.position   = Vector3.LerpUnclamped(restPos1, restPos2, e);
                    tile2.transform.position   = Vector3.LerpUnclamped(restPos2, restPos1, e);
                    tile1.transform.localScale = baseScale1 * (1f + 0.12f * arc);
                    tile2.transform.localScale = baseScale2 * (1f - 0.06f * arc);
                    yield return null;
                }
                // Hand the letters over and put each tile back on its OWN cell in the same frame. On
                // screen that is identical to the tiles having traded places (the face follows the
                // letter), but the grid keeps its tile<->cell ownership — moving tile objects between
                // cells is the desync landmine this codebase has paid for before.
                tile1.transform.localScale = baseScale1;
                tile2.transform.localScale = baseScale2;
                elapsed = shakeDur;                       // skip the legacy shake below
            }

            // Phase 1: shake both (legacy — only when MATCH3_SWAP_ANIM is off)
            while (elapsed < shakeDur)
            {
                elapsed += Time.deltaTime;
                if (tile1 != null)
                {
                    tile1.transform.position = restPos1 + new Vector3(
                        Random.Range(-posJitter, posJitter), Random.Range(-posJitter, posJitter) * 0.5f, 0f);
                    tile1.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-rotJitter, rotJitter));
                }
                if (tile2 != null)
                {
                    tile2.transform.position = restPos2 + new Vector3(
                        Random.Range(-posJitter, posJitter), Random.Range(-posJitter, posJitter) * 0.5f, 0f);
                    tile2.transform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(-rotJitter, rotJitter));
                }
                yield return null;
            }

            // Phase 2: swap letters — SetLetter resets the face tint to white, so re-assert the teal hold on each
            // tile so the newly-swapped letter stays cyan through the exchange too. 2026-07-10 Spencer.
            if (tile1 != null) { tile1.SetLetter(newLetter1); tile1.transform.position = restPos1; tile1.transform.localRotation = Quaternion.identity; tile1.HoldEditGlow(); }
            if (tile2 != null) { tile2.SetLetter(newLetter2); tile2.transform.position = restPos2; tile2.transform.localRotation = Quaternion.identity; tile2.HoldEditGlow(); }
            // Special markers move with their letters (data already swapped in TryBoardSwap). 2026-10-21.
            if (tile1 != null) tile1.SetSpecial(rules.GetCell(col1, row1)?.SpecialKind ?? 0);
            if (tile2 != null) tile2.SetSpecial(rules.GetCell(col2, row2)?.SpecialKind ?? 0);
            if (tile1 != null) tile1.SetRainGold(rules.GetCell(col1, row1)?.IsRainGold ?? false);
            if (tile2 != null) tile2.SetRainGold(rules.GetCell(col2, row2)?.IsRainGold ?? false);
            if (tile1 != null) tile1.SetGem(rules.GetCell(col1, row1)?.HasGem ?? false);
            if (tile2 != null) tile2.SetGem(rules.GetCell(col2, row2)?.HasGem ?? false);

            // Phase 3: squish
            // Silent landing (2026-10-22 Spencer: "it doesnt have to have the tile land sound after it is placed") — the swap
            // already has its own sound (PlaySwapSound, tile_fall 1..6 in turn) when it starts.
            if (tile1 != null) tile1.PlayLandingSquish(playSound: SWAP_LAND_SFX);
            if (tile2 != null) tile2.PlayLandingSquish(playSound: SWAP_LAND_SFX);

            // Now run the FULL rewrite resolution on the first cell
            // BeginRewrite won't re-swap since we already swapped in TryBoardSwap
            // Instead, use BeginSwapResolution with detected words from both positions.
            // 2026-06-08 Spencer: WILD-RESOLVING seed scan (not raw FindNewWords) so a swap
            // completing a word THROUGH an uncommitted wild (P-wild-D → PAD) is detected now,
            // not a turn later on the next drop. See SwapResolutionSequence for full rationale.
            var swapWords = rules.ScanSeedCellsPublic(new System.Collections.Generic.List<Vector2Int>
            {
                new Vector2Int(col1, row1),
                new Vector2Int(col2, row2),
            });
            var allNew = new System.Collections.Generic.List<RulesWordMatch>();

            if (swapWords != null)
                for (int i = 0; i < swapWords.Count; i++)
                {
                    string key = swapWords[i].Word + "|" + swapWords[i].CellKey;
                    if (!rules.IsClaimBlocked(key)) allNew.Add(swapWords[i]);
                }

            if (allNew.Count == 0)
            {   // TEMP DIAG (2026-10-23): the player followed the hint and nothing armed — say why
                var p1 = new Vector2Int(col1, row1); var p2 = new Vector2Int(col2, row2);
                if ((p1 == HintManager.LastHintA && p2 == HintManager.LastHintB) || (p1 == HintManager.LastHintB && p2 == HintManager.LastHintA))
                {
                    var sb = new System.Text.StringBuilder();
                    if (swapWords != null) foreach (var w in swapWords) sb.Append($"{w.Word}(blocked={rules.IsClaimBlocked(w.Word + "|" + w.CellKey)}) ");
                    WDLog.Log($"[Hint] MISMATCH: hint expected '{HintManager.LastHintWord}' at ({col1},{row1})<->({col2},{row2}); scan found [{sb}] shortCells={(RulesEngine.ShortWordCells == null ? "null" : RulesEngine.ShortWordCells.Length.ToString())}");
                }
            }
            // LATE BOUNCE (2026-10-30, Spencer: "i shouldnt have been able to move the gem piece objective"): the pre-check
            // (WordsIfSwapped) saw 'BITE' for a gem swap but the real scan found nothing, so the swap went through with NO word and
            // CHARGED a move - breaking the rule "no word = nothing happens". Whatever the two judges disagree on, the rule holds:
            // undo the data swap (letter + special + gold + gem), put the tiles back, refund the charge, no move.
            if (allNew.Count == 0 && BOUNCE_NO_WORD && InstantPopMode && SurvivalManager.FullBoardActive && !_freeSwap && rules != null && grid != null)
            {
                var c1 = rules.GetCell(col1, row1); var c2 = rules.GetCell(col2, row2);
                if (c1 != null && c2 != null)
                {
                    char lt = c1.Letter; c1.Letter = c2.Letter; c2.Letter = lt;
                    int sk = c1.SpecialKind; c1.SpecialKind = c2.SpecialKind; c2.SpecialKind = sk;
                    bool rg = c1.IsRainGold; c1.IsRainGold = c2.IsRainGold; c2.IsRainGold = rg;
                    bool gm = c1.HasGem; c1.HasGem = c2.HasGem; c2.HasGem = gm;
                    foreach (var (t, c) in new[] { (grid.GetTile(col1, row1), c1), (grid.GetTile(col2, row2), c2) })
                    {
                        if (t == null) continue;
                        t.SetLetter(c.Letter); t.SetSpecial(c.SpecialKind); t.SetRainGold(c.IsRainGold); t.SetGem(c.HasGem);
                        t.FadeEditGlowOut();
                    }
                    // SLIDE BACK, don't flip (10-30): each tile now wears its ORIGINAL letter, so start it on the OTHER cell (where
                    // that letter was just showing) and slide it home with the normal bounce's wobble - it reads as the same
                    // bounce-back instead of letters changing in place.
                    Tile t1 = grid.GetTile(col1, row1), t2 = grid.GetTile(col2, row2);
                    if (t1 != null && t2 != null)
                    {
                        Vector3 p1 = grid.CellToWorld(col1, row1), p2 = grid.CellToWorld(col2, row2);
                        p1.z = t1.transform.position.z; p2.z = t2.transform.position.z;
                        t1.MarkAnimating(BOUNCE_BACK + 0.05f); t2.MarkAnimating(BOUNCE_BACK + 0.05f); t1.LiftForFx(BOUNCE_BACK + 0.05f);
                        float bt = 0f;
                        while (bt < BOUNCE_BACK)
                        {
                            bt += Time.deltaTime; float k = Mathf.Clamp01(bt / BOUNCE_BACK); float e = Mathf.SmoothStep(0f, 1f, k);
                            Vector3 shake = new Vector3(Mathf.Sin(k * Mathf.PI * 5f) * (1f - k) * 0.025f * grid.CellSize, 0f, 0f);
                            if (t1 != null) t1.transform.position = Vector3.Lerp(p2, p1, e) + shake;
                            if (t2 != null) t2.transform.position = Vector3.Lerp(p1, p2, e) - shake;
                            yield return null;
                        }
                        if (t1 != null) t1.transform.position = p1;
                        if (t2 != null) t2.transform.position = p2;
                    }
                }
                var pre = new System.Text.StringBuilder(); if (s_bouncePre != null) foreach (var w in s_bouncePre) pre.Append(w.Word).Append(' ');
                WDLog.Log($"[Bounce] LATE ({col1},{row1})<->({col2},{row2}): pre-check saw [{pre}] but the real scan found none - swap undone, charge refunded, no move");
                GameAudio.Instance?.PlaySwapReject();   // 11-07: own sound for a rejected swap
                MatchController.Instance?.RefundRewriteCharge(MatchController.PLAYER_HUMAN);
                NoWordStreak++; PlaythroughStats.Bounces++;
                rules.FinalizeDrop();
                grid.SyncToRulesState(rules); rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
                IsInteractable = true;
                MatchController.Instance?.EndProcessing();
                yield break;
            }
            if (allNew.Count > 0)
            {
                var registry = rules.PrimedRegistry;
                var justPrimedIds = PrimeAndPresentWords(allNew, rules, grid);

                // Teal dies out NOW — right after the exchange/prime, BEFORE the 0.3s wait and the detonation —
                // so primed tiles hand straight to magenta and un-worded swapped tiles fade to white at the SAME
                // time, instead of one tile sitting cyan through the whole pop animation. 2026-07-10 Spencer.
                grid.GetTile(col1, row1)?.FadeEditGlowOut();
                grid.GetTile(col2, row2)?.FadeEditGlowOut();

                // ── THE TAP DELAY (2026-09-08 Spencer) ──────────────────────────────────
                // This 0.3s exists to give the DROP game a beat between "word primed" and
                // "the crossing auto-detonates it" — it is the wind-up before the chain fires.
                // In ARMED-WEB nothing auto-detonates: the trigger loop immediately below is
                // gated `&& !RulesEngine.ArmedWebMode`, so it does nothing. The wait therefore
                // leads nowhere — it just holds input locked for a third of a second while the
                // tile is ALREADY sitting there magenta and looking ready to tap.
                //
                // That is the whole delay. The tap path itself fires on the same frame as the
                // press (verified in debug_log: tap and Exploding share a frame number); the
                // lag was the game refusing the click, not the click being slow. And it only
                // affected words you had just MADE — tap a cascade word and the resolution has
                // already ended, which is exactly the asymmetry Spencer noticed.
                //
                // One frame instead of zero so the teal-out tween is running before input
                // reopens; the tween finishes on its own, it does not need to be waited on.
                if (RulesEngine.ArmedWebMode) yield return null;
                else                          yield return WaitCache.Get(0.3f);

                // Check for detonation triggers.
                // ARMED-WEB: crossing LINKS words into the web, it never fires them.
                // Only a player TAP detonates (see TapDetonateSequence).
                bool triggeredPrimed = false;
                for (int i = 0; i < allNew.Count && !triggeredPrimed && !RulesEngine.ArmedWebMode; i++)
                {
                    if (allNew[i].Cells == null) continue;
                    for (int c = 0; c < allNew[i].Cells.Count; c++)
                    {
                        var overlapping = registry.GetPrimedWordsContaining(allNew[i].Cells[c]);
                        if (overlapping != null)
                            for (int p = 0; p < overlapping.Count; p++)
                                if (!justPrimedIds.Contains(overlapping[p].Id))
                                { triggeredPrimed = true; break; }
                        if (triggeredPrimed) break;

                        if (RulesEngine.AdjacencyTriggerEnabled)
                        {
                            var adj = registry.GetPrimedWordsAdjacentTo(allNew[i].Cells[c]);
                            if (adj != null)
                                for (int p = 0; p < adj.Count; p++)
                                    if (!justPrimedIds.Contains(adj[p].Id))
                                    { triggeredPrimed = true; break; }
                        }
                        if (triggeredPrimed) break;
                    }
                }

//                 WDLog.Log($"[BoardSwap] triggeredPrimed={triggeredPrimed}, allNew={allNew.Count}, justPrimed={justPrimedIds.Count}, registry={registry.Count}");

                // NO SCORE FOR MERELY FORMING A WORD (Spencer 2026-10-20).
                // Arming used to pay allNew[i].Score here, on top of the detonation payout.
                // That meant an armed word was already BANKED, so a rise eating your web cost
                // you nothing you held — only something you might have had. Foregone gain is a
                // far weaker pull than loss, which is why the web never felt like it was at
                // risk. Payment now happens exclusively at detonation (PreviewGroupPayout /
                // DoExplode), so every armed word is money ON THE TABLE and "fire now or arm
                // one more" becomes the decision the turn is actually about.
                // The lost base is folded into DETONATION_SCORE_MULTIPLIER — see the note there.

                // If detonation triggered, run full step resolution
                if (triggeredPrimed)
                {
                    rules.BeginSwapResolution(allNew, MatchController.PLAYER_HUMAN, justPrimedIds);

                    bool resolving = true;
                    int swapScore = 0;

                    while (resolving)
                    {
                        var step = rules.NextStep();
                        if (step == null) { resolving = false; break; }

                        switch (step.Phase)
                        {
                            case RulesEngine.ResolutionPhase.WordsDetected:
                                break;
                            case RulesEngine.ResolutionPhase.WordsScored:
                                if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                                {
                                    HapticsManager.Light();
                                    GameAudio.Instance?.PlayTilePrimed();
                                }
                                break;

                            case RulesEngine.ResolutionPhase.TriggersFound:
                                CacheBurstTriggers(step);
                                yield return WaitCache.Get(0.05f);
                                break;

                            case RulesEngine.ResolutionPhase.Exploding:
                            {
                                DefrostThawedTiles(grid, step); // ICE: thaw frozen tiles caught in this detonation (survive + defrost)
                                if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                                {
                                    var dyingTiles = new System.Collections.Generic.List<Tile>();
                                    foreach (var c in step.ExplodedCells)
                                    {
                                        Tile t = grid.GetTile(c.x, c.y);
                                        if (t != null) dyingTiles.Add(t);
                                    }

                                    // Pre-explosion HapticsManager.Strong() removed — haptics
                                    // now owned by WordDropFX.PlayExplosion (single source).

                                    // Tiered explosion (handles sound + visuals)
                                    if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                    {
                                        // Hitstop removed for cascades (2026-05-15) — cascades
                                        // pop instantly on impact, no time-freeze pause.
                                        // Flash fires AFTER hitstop, immediately before
                                        // PlayExplosion, so timeScale=0 doesn't freeze the
                                        // tween during the pause and the flash plays in sync
                                        // with the actual tile-dissolve animation.
                                        FirePerWordBurst();
                                        FireTileFlashBoxes(dyingTiles);
                                        int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dyingTiles.Count;
                                        LaunchThemeFlights(dyingTiles, grid);   // animal-word letters fly to the goal instead of exploding (2026-10-23)
                                        yield return WordDropFX.MaybeBigPopAndHold(dyingTiles, step.SpecialsFired != null && step.SpecialsFired.Count > 0);
                                        yield return ExplodeWordsThenSplash(step, dyingTiles, step.ChainDepth, wLen);
                                    }
                                    grid.RemoveTiles(step.ExplodedCells);

                                    if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                                        SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                                    Vector3 swapCenter = Vector3.zero;
                                    if (dyingTiles.Count > 0)
                                    {
                                        foreach (var t in dyingTiles)
                                            if (t != null) swapCenter += t.transform.position;
                                        swapCenter /= Mathf.Max(1, dyingTiles.Count);
                                    }
                                    if (step.DetonationBonus > 0 && BonusPopup.Instance != null && dyingTiles.Count > 0)
                                    {
                                        BonusPopup.Instance.ShowDetonation(TakeBurstWords(), step.DetonationBonus, swapCenter, step.ChainDepth);
                                    }
                                    // Refill rewards (swap/edit/wild) — was missing on this path.
                                    ApplyDetonationRefillRewards(step, swapCenter, 0);
                                }
                                swapScore = step.TotalScore;
                                break;
                            }

                            case RulesEngine.ResolutionPhase.GravityApplied:
                                yield return StartCoroutine(grid.ApplyGravity());
                                rules.RefreshAllPrimedWordTiles(-1, flashNew: false);   // see note above
                                break;

                            case RulesEngine.ResolutionPhase.Complete:
                                resolving = false;
                                rules.FinalizeDrop();
                                grid.SyncToRulesState(rules); rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
                                break;

                            default:
                                resolving = false;
                                break;
                        }
                    }

                    if (swapScore > 0 && ScoreManager.Instance != null)
                    {
                        ScoreManager.Instance.AddScore(swapScore, MatchController.PLAYER_HUMAN);
                        // Phase 5.1 fix: same bypass as the base-score path above.
                        if (GameManager.IsLevelMode)
                            LevelController.Instance?.NotifyScore(swapScore);
                    }

                    if (MatchController.Instance != null)
                    {
                        MatchController.Instance.RefundRewriteCharge(MatchController.PLAYER_HUMAN);
//                         WDLog.Log("[BoardSwap] Detonation → edit refunded");
                    }
                }
            }

            // Finalize — even if no detonation, sync state
            if (rules != null)
            {
                rules.FinalizeDrop();
                if (grid != null) grid.SyncToRulesState(rules); rules.RefreshAllPrimedWordTiles(-1, flashNew: false);
            }

            // No-word swaps fade their teal HERE — right after the instant exchange (no pop happened). Word-forming
            // swaps already faded above, before the detonation, so skip them here. 2026-07-10 Spencer.
            if (grid != null && allNew.Count == 0)
            {
                grid.GetTile(col1, row1)?.FadeEditGlowOut();
                grid.GetTile(col2, row2)?.FadeEditGlowOut();
            }

            // A board-swap costs a MOVE. 2026-09-03: this used to live inside the
            // `if (allNew.Count > 0)` branch, so a swap only counted when it happened to
            // form a word — setup swaps (repositioning letters for a future word, which
            // is the whole point of the swap game) were FREE. Now it fires once per swap
            // regardless, which is what the comment always claimed.
            WDLog.Log($"[BoardSwapMove] swap resolved. formedWord={allNew.Count > 0} survival={SurvivalManager.IsSurvivalMode} editsCountAsMoves={(SurvivalManager.Instance != null ? SurvivalManager.Instance.EditsCountAsMoves.ToString() : "null")} → {((SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null && SurvivalManager.Instance.EditsCountAsMoves) ? "COUNTING" : "not counted")}");
            NoWordStreak = allNew.Count > 0 ? 0 : NoWordStreak + 1;   // fishing detector (HintManager)
            bool freeSwap = _freeSwap; _freeSwap = false;
            if (freeSwap) WDLog.Log("[FreeSwap] booster swap — no move spent");
            else if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                && SurvivalManager.Instance.EditsCountAsMoves)
                SurvivalManager.Instance.NotifyDropCommitted();

            IsInteractable = true;
            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
            MaybeAutoPop();   // instant pop
        }

        /// <summary>Re-reads the selected tile's CURRENT cell. Rising rows and gravity
        /// move it, so anything using _rewriteTargetCol/Row must refresh first.</summary>
        private void RefreshRewriteTarget()
        {
            if (_rewriteTargetTile == null) return;
            _rewriteTargetCol = _rewriteTargetTile.Col;
            _rewriteTargetRow = _rewriteTargetTile.Row;
        }

        /// <summary>Called by SwipeWordInput the moment a drag crosses a second cell, i.e. the
        /// gesture is a SWIPE and not a tap. Pressing down on the board pre-selects that tile
        /// for a tap-tap board swap, so without this the swiped-from tile stays lit and armed
        /// as a swap source after the swipe resolves. 2026-10-19.</summary>
        public void AbortPendingSwapSelection()
        {
            CancelRewriteMode();
        }

        // FREE SWAP booster (2026-10-23): the next TryBoardSwap may join any two tiles and costs no move. Set only for the
        // duration of CommitFreeSwap -> the swap sequence; cleared at the move count (or on refusal / combo).
        private bool _freeSwap;
        /// <summary>Swaps in a row that made no word (2026-10-23 "fishing"). HintManager shows the hint fast after FISHING_SWAPS.</summary>
        public static int NoWordStreak;
        public bool CommitFreeSwap(Vector2Int a, Vector2Int b)
        {
            if (_tapDetonating || (MatchController.Instance != null && MatchController.Instance.IsProcessing)) return false;
            CancelRewriteMode();
            _freeSwap = true;
            bool ok = TryBoardSwap(a.x, a.y, b.x, b.y);
            if (!ok) _freeSwap = false;
            return ok;
        }

        /// <summary>DRAG-TO-SWAP (2026-10-20 Spencer: "in candy crush you swipe"). A drag from one tile
        /// onto its neighbour swaps them — same rules, same sequence, same move cost as tap-tap,
        /// because it goes through TryBoardSwap. SwipeWordInput decides on RELEASE: exactly 2 cells
        /// = swap, 3+ = word swipe, so the two gestures never collide.</summary>
        public bool CommitDragSwap(Vector2Int a, Vector2Int b)
        {
            CancelRewriteMode();
            bool ok = TryBoardSwap(a.x, a.y, b.x, b.y);
            WDLog.Log($"[DragSwap] ({a.x},{a.y})->({b.x},{b.y}) {(ok ? "swapped" : "refused")}");
            if (!ok)
            {
                GameAudio.Instance?.PlayButtonClick();
                if (MATCH3_SWAP_ANIM && IsAdjacent(a.x, a.y, b.x, b.y)) StartCoroutine(NudgeRefusedSwap(a, b));
            }
            return ok;
        }

        /// <summary>Routes a player-SWIPED word through the exact pipeline a swap uses.
        /// Returns false if the word was refused by the registry's branch gate.
        ///
        /// 2026-10-19 — MUST run inside BeginProcessing/EndProcessing. The first version did
        /// not, and the symptom was a rise tearing through a just-primed word: the tiles under
        /// the glow did not move up with the rest of the board. Cause: SurvivalManager defers
        /// its rise via a wait-coroutine "until processing clears", and with IsProcessing
        /// false that wait was already satisfied — so NotifyDropCommitted fired a rise in the
        /// SAME FRAME the primed glow and particles were still being applied. Every other turn
        /// path in the game is wrapped this way; the swipe path was the only one that was not.
        /// </summary>
        public bool CommitSwipedWord(RulesWordMatch match)
        {
            var rules = RulesEngine.Instance;
            var grid  = GridManager.Instance;
            if (match == null || rules == null || grid == null) return false;
            StartCoroutine(SwipeCommitSequence(match, rules, grid));
            return true;
        }

        private System.Collections.IEnumerator SwipeCommitSequence(
            RulesWordMatch match, RulesEngine rules, GridManager grid)
        {
            RulesEngine.LastSwapCells = null;   // a swiped word has no swapped cell — its special is born mid-word
            IsInteractable = false;
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            var one = new System.Collections.Generic.List<RulesWordMatch> { match };
            var justPrimedIds = PrimeAndPresentWords(one, rules, grid);

            // AddPrimedWord returns -1 when the registry refuses, and the extracted block adds
            // the id unconditionally — so a non-empty set is NOT proof of success. Check for a
            // real id, or a refused word would score and cost a move without arming.
            bool armed = false;
            foreach (int id in justPrimedIds) if (id >= 0) { armed = true; break; }
            // FOUND-IT (10-31, Spencer OK'd): a dragged word is the purest word-spot - it makes its special one size earlier
            // (RulesEngine.DRAG_SPECIAL_BONUS) and gets a bigger pop + its own sound (ClusterOutline / here).
            if (armed && RulesEngine.DRAG_REWARD)
            {
                foreach (int id in justPrimedIds) if (id >= 0) RulesEngine.SwipedPrimedIds.Add(id);
                RulesEngine.SwipedWords.Add(match.Word.ToUpperInvariant());
                GameAudio.Instance?.PlayScorePowerup();
            }

            if (armed)
            {
                // Consume the rise-seeding exemption so the same word cannot be claimed twice.
                rules.ClearRiseSeededKey(match.Word + "|" + match.CellKey);

                // No score for arming — same rule as the swap path above. A swipe costs a
                // move and pays NOTHING until you detonate what it armed, which is the point:
                // claiming a word is now a commitment, not a withdrawal.

                // Let the prime glow + particles get a frame before anything moves the board.
                yield return null;

                // A SWIPE COSTS A MOVE (Spencer 2026-10-19). Gated identically to the
                // board-swap path so the two verbs cost the same thing.
                if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                    && SurvivalManager.Instance.EditsCountAsMoves)
                    SurvivalManager.Instance.NotifyDropCommitted();

                if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                    SurvivalManager.Instance.CheckStageClear();
            }
            else
            {
                WDLog.Log($"[Swipe] '{match.Word}' refused by the registry — no move charged");
            }

            if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
            IsInteractable = true;
            MaybeAutoPop();   // instant pop
        }

        private void CancelRewriteMode(bool keepGlow = false)
        {
            // Clear via the TILE REFERENCE — the stored cell may now hold a different tile.
            if (_rewriteTargetTile != null && !keepGlow)
            {
                _rewriteTargetTile.SetEditSelected(false, popOnExit: true);
                _rewriteTargetTile.ResetVisuals();
            }
            _rewriteTargetTile = null;

            if (_rewriteTargetCol >= 0 && _rewriteTargetRow >= 0)
            {
                Tile targetTile = _grid.GetTile(_rewriteTargetCol, _rewriteTargetRow);
                // keepGlow: a board SWAP wants the source tile to KEEP its teal glow through the exchange
                // (faded out at the end of BoardSwapTurnSequence), so don't clear it here. 2026-07-10 Spencer.
                if (targetTile != null && !keepGlow)
                {
                    targetTile.SetEditSelected(false, popOnExit: true);
                    targetTile.ResetVisuals();
                }
            }

            _rewriteModeActive = false;
            _rewriteTargetCol = -1;
            _rewriteTargetRow = -1;
//             WDLog.Log("[HandManager] Rewrite mode cancelled.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Big Burst Flash — per-word wall-of-light on detonation
        // ═══════════════════════════════════════════════════════════════════════════

        // step.Triggers is populated in TriggersFound and null'd by the time
        // Exploding fires — but we want the flash to land WITH the actual explosion,
        // not during the pre-anticipation. Cache here, then fire in Exploding.
        private List<PrimedTriggeredEvent>       _pendingBurstTriggers;
        // Un-consumed copy of the per-word triggers, kept ALIVE for the staggered-combo gate in
        // ExplodeWordsThenSplash. Needed because (a) step.Triggers is null on the Exploding-phase step
        // and (b) FirePerWordBurst nulls _pendingBurstTriggers before the explode runs. 2026-07-06 Spencer.
        private List<PrimedTriggeredEvent>       _stagerTriggers;
        // Un-consumed copy of the TRIGGER (connecting) words for the staggered combo — gives the trigger
        // word its OWN stage in the 4-part chain (e.g. YES). Mirrors _stagerTriggers. 2026-07-06 Spencer.
        private List<RulesWordMatch>             _stagerTriggerWords;
        private List<RulesWordMatch> _pendingBurstTriggerWords;
        private int  _pendingBurstChainDepth;
        private int  _pendingBurstLongestWord;
        // ScreenFlash should fire ONCE per burst pass, not once per primed word.
        // FirePerWordBurst iterates cluster members and we only want one screen tint.
        private bool _screenFlashFiredThisBurst;

        /// <summary>
        /// Phase 11i: returns the screen extent (in world units) along the
        /// word's axis with a small bleed past the edge — used so the
        /// BigBurstFlash beam spans the FULL screen width/height regardless
        /// of how many tiles the word covers (Candy Crush striped-candy feel).
        /// </summary>
        private float ScreenExtentAlongAxis(bool vertical)
        {
            Camera cam = _cam != null ? _cam : Camera.main;
            if (cam == null) return 20f;
            float halfHeight = cam.orthographicSize;
            float halfWidth  = halfHeight * cam.aspect;
            return vertical ? halfHeight * 2.2f : halfWidth * 2.2f;
        }

        /// <summary>
        /// Fires a TileFlashBox under each dying tile. Single coin flip PER
        /// DETONATION — either the whole cluster lights up, or none does. Keeps
        /// the effect as a moment-to-moment surprise rather than a uniform overlay.
        /// Tune TILE_FLASH_BOX_CHANCE to taste.
        /// </summary>
        // ICE: defrost any frozen tiles caught in this detonation step. Their IsFrozen was already
        // cleared in DoExplode/TryThawCell and they are NOT in ExplodedCells (they survive), so the
        // booster resolution sequences here (swap/edit/rewrite) must defrost + clear the overlay
        // WITHOUT routing them through the explode animation. Mirrors GameVisualBridge. 2026-06-12.
        /// <summary>Special tiles: a 5+ letter word's survivor becomes a special (drop its armed glow, show the marker,
        /// pop it in); specials that fired play their effect. The rules already did the clearing. 2026-10-21.</summary>
        private bool _bigPlayThisChain;   // a 5+ letter word or a special fired somewhere in this chain -> reward shower

        /// <summary>Cell -> seconds after the detonation starts when a special's blast reaches it (set per Exploding step).</summary>
        private readonly Dictionary<Vector2Int, float> _specialPopDelay = new Dictionary<Vector2Int, float>();
        public const float ROCKET_FLIGHT = 0.32f;   // (old sweep timing)
        public static float ROCKET_POP_PER_CELL = 0.012f;   // seconds per cell of ripple — ~0.1s end to end
        public static int BLAST_POP_GROUP = 4;   // < MELTDOWN_TILE_GATE (6) and the tier-2+ thresholds
        public static float TIMED_POP_BUCKET = 0.09f;   // 0.05 -> 0.09 (2026-10-22 frame drops): a near-instant rocket line = 1-2 explosion calls, not 4+   // pops within this window share one explosion call (fewer FX/audio stacks)

        /// <summary>Pull the special-cleared tiles out of <paramref name="tiles"/> and pop them on their arrival times.
        /// Returns the coroutine to wait on (null if none).</summary>
        private Coroutine StartTimedSpecialPops(List<Tile> tiles, HashSet<Vector2Int> exclude)
        {
            if (_specialPopDelay.Count == 0 || tiles == null) return null;
            var timed = new List<KeyValuePair<float, Tile>>();
            for (int i = tiles.Count - 1; i >= 0; i--)
            {
                var t = tiles[i]; if (t == null) continue;
                var cell = new Vector2Int(t.Col, t.Row);
                if (exclude != null && exclude.Contains(cell)) continue;
                if (!_specialPopDelay.TryGetValue(cell, out float d)) continue;
                timed.Add(new KeyValuePair<float, Tile>(d, t));
                tiles.RemoveAt(i);
            }
            if (timed.Count == 0) return null;
            timed.Sort((a, b) => a.Key.CompareTo(b.Key));
            return StartCoroutine(TimedSpecialPops(timed));
        }

        private System.Collections.IEnumerator TimedSpecialPops(List<KeyValuePair<float, Tile>> timed)
        {
            float t0 = Time.unscaledTime;
            var running = new List<Coroutine>();
            int i = 0;
            while (i < timed.Count)
            {
                float at = timed[i].Key;
                var bucket = new List<Tile>();
                while (i < timed.Count && timed[i].Key <= at + TIMED_POP_BUCKET) { if (timed[i].Value != null) bucket.Add(timed[i].Value); i++; }
                while (Time.unscaledTime - t0 < at) yield return null;
                // SMALL GROUPS (2026-10-22 Spencer: "for the dynamite explosion ... right now its still the old explosion"). A
                // bomb's 25-63 tiles now land in ONE bucket, and a big tile count made PlayExplosion add the old Tier-3 burst +
                // meltdown prefab on top of the new fireball. Groups of BLAST_POP_GROUP stay under every big-layer gate, so
                // blast-cleared tiles just pop + throw chunks and the special's own FX is the explosion.
                if (bucket.Count > 0 && WordDropFX.Instance != null)
                    for (int g = 0; g < bucket.Count; g += BLAST_POP_GROUP)
                        running.Add(WordDropFX.Instance.PlayExplosion(bucket.GetRange(g, Mathf.Min(BLAST_POP_GROUP, bucket.Count - g)), 0, 1, wordFlash: false, cascade: false));   // cascade:true added a CYAN glow that clashed with the orange blast
            }
            foreach (var c in running) if (c != null) yield return c;
        }

        public static bool  SPECIAL_MERGE_ANIM   = true;
        public static float SPECIAL_CONVERGE_DUR = 0.2f;   // letters slide into the new special
        private readonly Dictionary<Vector2Int, Vector2Int> _convergeTo = new Dictionary<Vector2Int, Vector2Int>();

        /// <summary>Slide the dying letters of a special-making word into the special's tile, then return so they pop there.</summary>
        private System.Collections.IEnumerator ConvergeIntoSpecials(List<Tile> dying)
        {
            if (_convergeTo.Count == 0 || dying == null || _grid == null) yield break;
            bool any = false;
            foreach (var t in dying)
            {
                if (t == null || !_convergeTo.TryGetValue(new Vector2Int(t.Col, t.Row), out var to)) continue;
                Vector3 target = _grid.CellToWorld(to.x, to.y) + new Vector3(_grid.BoardSlideOffsetX, 0f, 0f);
                target.z = t.transform.position.z - 0.05f;
                t.MarkAnimating(SPECIAL_CONVERGE_DUR + 0.1f);
                t.transform.DOKill();
                t.transform.DOMove(target, SPECIAL_CONVERGE_DUR).SetEase(Ease.InBack, 1.4f).SetUpdate(true);
                t.transform.DOScale(t.transform.localScale * 0.7f, SPECIAL_CONVERGE_DUR).SetEase(Ease.InQuad).SetUpdate(true);
                any = true;
            }
            _convergeTo.Clear();
            if (any) yield return new WaitForSecondsRealtime(SPECIAL_CONVERGE_DUR);
        }

        // ── PRE-BURST (2026-10-22 Spencer: "should the dynamite and bombs light up before they burst?" -> "yes please"). Every
        // special firing this step swells ~20% and flashes bright for PRE_BURST_SECONDS, then the fireball, the bang and the tile
        // pops all go together (ApplySpecialStep right after). They swell TOGETHER, so a chain of specials costs one beat, not one
        // each. Skipped in the bonus (rapid fire) and when nothing fires. Royal Match's TNT does the same quick swell. ──
        public static bool  PRE_BURST         = true;
        public static float PRE_BURST_SECONDS = 0.12f;
        public static float PRE_BURST_SWELL   = 1.2f;
        private IEnumerator PreBurstSpecials(GridManager grid, RulesEngine.StepResult step)
        {
            if (!PRE_BURST || BonusBlastRunning || grid == null || step == null || step.SpecialsFired == null || step.SpecialsFired.Count == 0) yield break;
            var swelling = new List<Tile>();
            foreach (var f in step.SpecialsFired)
            {
                var t = grid.GetTile(f.Cell.x, f.Cell.y);
                if (t == null || swelling.Contains(t)) continue;
                swelling.Add(t);
                t.MarkAnimating(PRE_BURST_SECONDS + 0.1f);
                t.transform.DOKill();
                t.transform.DOScale(t.CanonicalRestScalePublic * PRE_BURST_SWELL, PRE_BURST_SECONDS).SetEase(Ease.OutQuad).SetUpdate(true);
                var sr = t.GetComponent<SpriteRenderer>();
                if (sr != null) { sr.DOKill(); sr.color = Color.white; sr.DOColor(new Color(1.7f, 1.6f, 1.4f, 1f), PRE_BURST_SECONDS).SetUpdate(true); }
            }
            if (swelling.Count == 0) yield break;
            yield return new WaitForSecondsRealtime(PRE_BURST_SECONDS);
        }

        private void ApplySpecialStep(GridManager grid, RulesEngine.StepResult step)
        {
            if (grid == null || step == null) return;
            if (!BonusBlastRunning && (step.LongestWordLength >= 5 || step.SpecialsFired != null)) _bigPlayThisChain = true;
            _convergeTo.Clear();
            if (step.GemsCollectedOnSpecials != null)
                foreach (var gc in step.GemsCollectedOnSpecials)
                {
                    var gt = grid.GetTile(gc.x, gc.y);
                    if (gt == null) continue;
                    HUDManager.Instance?.FlyCollectLetter(gt.transform.position, gt.Letter);   // gem flies to the goal from the special's cell
                    gt.SetGem(false);
                }
            if (step.SpecialsCreated != null)
                foreach (var kv in step.SpecialsCreated)
                {
                    var t = grid.GetTile(kv.Key.x, kv.Key.y);
                    if (t == null) continue;
                    t.ClearPrimedGlow();
                    // MERGE (2026-10-21 Spencer: "where the tiles slide and converge into the tile" — Candy Crush / Royal
                    // Match). The word's other letters slide into the birth tile (ExplodeWordsThenSplash), and the special
                    // pops in as they arrive.
                    bool merge = SPECIAL_MERGE_ANIM && step.SpecialSources != null && step.SpecialSources.TryGetValue(kv.Key, out var src);
                    if (merge)
                        foreach (var c in step.SpecialSources[kv.Key]) if (c != kv.Key) _convergeTo[c] = kv.Key;
                    if (merge)
                    {
                        var tt = t; int kind = kv.Value;
                        tt.transform.DOPunchScale(Vector3.one * 0.06f, SPECIAL_CONVERGE_DUR, 6, 0.5f);   // braces as the letters come in
                        DOVirtual.DelayedCall(SPECIAL_CONVERGE_DUR, () => { if (tt != null) tt.SetSpecial(kind, pop: true); }, ignoreTimeScale: true);
                    }
                    else t.SetSpecial(kv.Value, pop: true);
                }
            _specialPopDelay.Clear();
            if (step.SpecialsFired != null && grid != null)
            {
                // POP WHEN THE BLAST ARRIVES (2026-10-21 Spencer: "shouldnt the rockets be blowing up the tiles in their path
                // as they travel into them?"). Tiles a special clears were "splash" and all popped together at the END of the
                // detonation, so the rocket art flew over letters that were still standing. Each cleared cell now gets the time
                // the FX reaches it; ExplodeWordsThenSplash pops it then. Rocket head = DOMove over ROCKET_FLIGHT, InQuad.
                int fj = 0;
                float windup = StepComboWindup(step);   // 2026-10-28: a combo winds up first; everything in the step waits for its burst
                foreach (var f in step.SpecialsFired)
                {
                    float fire = (BonusBlastRunning ? fj * BONUS_FIRE_STAGGER : 0f) + windup;
                    fj++;
                    if (f.Cleared == null) continue;
                    bool rocket = f.Kind == RulesEngine.SPECIAL_ROCKET_H || f.Kind == RulesEngine.SPECIAL_ROCKET_V;
                    bool comboLine = f.Kind == RulesEngine.SPECIAL_COMBO_CROSS || f.Kind == RulesEngine.SPECIAL_COMBO_BIGCROSS;
                    bool comboBlast = f.Kind == RulesEngine.SPECIAL_COMBO_MEGA || f.Kind == RulesEngine.SPECIAL_COMBO_DISCO_DISCO || f.Kind == RulesEngine.SPECIAL_COMBO_DISCO;
                    for (int k = 0; k < f.Cleared.Count; k++)
                    {
                        var c = f.Cleared[k];
                        float t;
                        if (comboLine)
                        {   // instant line clear, slight ripple outward (like a rocket)
                            int ddx = Mathf.Abs(c.x - f.Cell.x), ddy = Mathf.Abs(c.y - f.Cell.y);
                            t = fire + (WordDropFX.ROCKET_TRAVEL ? WordDropFX.RocketArriveAt(ddy <= 1 ? ddx : ddy, ddy <= 1 ? RulesEngine.COLS : RulesEngine.ROWS)
                                                                 : ROCKET_POP_PER_CELL * (ddy <= 1 ? ddx : ddy));
                        }
                        else if (comboBlast)
                            t = fire + 0.008f * Mathf.Max(Mathf.Abs(c.x - f.Cell.x), Mathf.Abs(c.y - f.Cell.y));
                        else if (rocket)
                        {
                            bool alongRow = c.y == f.Cell.y && (f.Kind == RulesEngine.SPECIAL_ROCKET_H || c.x != f.Cell.x);
                            float d = alongRow ? Mathf.Abs(c.x - f.Cell.x) : Mathf.Abs(c.y - f.Cell.y);
                            // Royal Match (recording @1:21): the whole line goes at once — just a hint of ripple outward.
                            // 2026-10-28 (RK clips): the heads TRAVEL (~25-30 tiles/s) and each tile breaks just after its head passes.
                            t = fire + (WordDropFX.ROCKET_TRAVEL ? WordDropFX.RocketArriveAt(d, alongRow ? RulesEngine.COLS : RulesEngine.ROWS)
                                                                 : ROCKET_POP_PER_CELL * d);
                        }
                        else if (f.Kind == RulesEngine.SPECIAL_AIRPLANE)
                            t = fire + f.Delay + (c == f.Target && c != f.Cell ? WordDropFX.AIRPLANE_TAKEOFF + WordDropFX.AIRPLANE_FLIGHT : 0.02f);
                        else if (f.Kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB)
                        {   // home plus at once; the carried bomb's square when the plane lands (rippling out from the target)
                            int dT = Mathf.Max(Mathf.Abs(c.x - f.Target.x), Mathf.Abs(c.y - f.Target.y));
                            bool atTarget = f.Target != f.Cell && dT <= RulesEngine.BOMB_RADIUS_POWERED
                                            && Mathf.Abs(c.x - f.Cell.x) + Mathf.Abs(c.y - f.Cell.y) > 1;
                            t = fire + (atTarget ? WordDropFX.AIRPLANE_TAKEOFF + WordDropFX.AIRPLANE_FLIGHT + 0.008f * dT : 0.02f);
                        }   // home plus at once; the target when the plane lands
                        else if (RulesEngine.IsBombLike(f.Kind))
                            t = fire + 0.008f * Mathf.Max(Mathf.Abs(c.x - f.Cell.x), Mathf.Abs(c.y - f.Cell.y));   // gone in the first frames — tiles must not stand inside the fireball (2026-10-22 capture)
                        else if (f.Kind == RulesEngine.SPECIAL_DISCO)
                            t = fire + WordDropFX.DiscoPopAt(f.Cleared.Count);   // RM light ball: beams one by one, then EVERYTHING pops at the flash (2026-10-23)
                        else
                            t = fire + 0.08f + 0.035f * k;
                        if (!_specialPopDelay.TryGetValue(c, out float was) || t < was) _specialPopDelay[c] = t;
                    }
                }
                // 11-04 (Spencer: "the bookshelf and any objective needs to wait to blow up until the flying propeller hits it"):
                // a propeller lands on ONE cell of a 2x2 shelf/safe, but the shelf's last hit clears all four - the other three
                // popped at takeoff. Every cell of a shelf the plane is flying to now pops when it lands.
                if (step.ShelfHits != null)
                    foreach (var kv in step.ShelfHits)
                    {
                        float land = 0f; var a = kv.Key;
                        for (int dx = 0; dx < 2; dx++) for (int dy = 0; dy < 2; dy++) land = Mathf.Max(land, PlaneLandDelay(step, new Vector2Int(a.x + dx, a.y + dy)));
                        if (land <= 0f) continue;
                        for (int dx = 0; dx < 2; dx++) for (int dy = 0; dy < 2; dy++) _specialPopDelay[new Vector2Int(a.x + dx, a.y + dy)] = land;
                    }
            }
            if (step.SpecialsFired != null && WordDropFX.Instance != null)
            {
                int fi = 0;
                float windup = StepComboWindup(step);
                if (windup > 0f)
                    foreach (var f in step.SpecialsFired)
                        if (WordDropFX.ComboWindup(f.Kind) > 0f) WordDropFX.Instance.PlayComboWindup(f, windup);
                foreach (var f in step.SpecialsFired)
                {
                    var ff = f;
                    float delay = (BonusBlastRunning ? fi * BONUS_FIRE_STAGGER : 0f) + windup + ff.Delay;   // bonus: one quick ripple, no pauses
                    if (delay <= 0f) WordDropFX.Instance.PlaySpecialFire(ff);
                    else DOVirtual.DelayedCall(delay, () => { if (WordDropFX.Instance != null) WordDropFX.Instance.PlaySpecialFire(ff); }, ignoreTimeScale: true);
                    fi++;
                }
            }
            // Bonus blast: every destroyed tile spits a coin into the REWARD panel, streaming in sweep order.
            if (BonusBlastRunning && step.SpecialsFired != null && HUDManager.Instance != null)
                foreach (var f in step.SpecialsFired)
                {
                    if (f.Cleared == null || f.Cleared.Count == 0) continue;
                    if (BONUS_COINS_ON_ARRIVAL)
                    {   // 11-07 (Movie_007): every coin burst out at LAUNCH, before the rockets reached the tiles. Each coin now waits for
                        // its tile to break (_specialPopDelay, filled above). ONE call per special, so the stream (cap 14, stagger) and the
                        // coin-landing sound keep their rhythm (Spencer 11-07: per-bucket calls had changed the landing sound).
                        var pts = new List<Vector3>(); var waits = new List<float>();
                        foreach (var c in f.Cleared)
                        {
                            pts.Add(grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f));
                            waits.Add(_specialPopDelay.TryGetValue(c, out float d) ? d : 0f);
                        }
                        HUDManager.Instance.SpawnBonusCoins(pts, f.Cleared.Count * SurvivalManager.COINS_PER_BONUS_TILE, waits);
                        continue;
                    }
                    var starts = new List<Vector3>();
                    foreach (var c in f.Cleared) starts.Add(grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f));
                    HUDManager.Instance.SpawnBonusCoins(starts, f.Cleared.Count * SurvivalManager.COINS_PER_BONUS_TILE);
                }
            // 10-31 (the REWARD readout jumped ~+10 at the end): the finale also destroys tiles NO special cleared (words the
            // cascade spells, chain pops) and pays for them, but no coin flew for them. They get their own coins now, so the coins
            // that land add up to what is paid.
            if (BonusBlastRunning && step.ExplodedCells != null && step.ExplodedCells.Count > 0 && HUDManager.Instance != null)
            {
                var byFire = new HashSet<Vector2Int>();
                if (step.SpecialsFired != null) foreach (var f in step.SpecialsFired) if (f.Cleared != null) foreach (var c in f.Cleared) byFire.Add(c);
                var extra = new List<Vector3>();
                foreach (var c in step.ExplodedCells) if (!byFire.Contains(c)) extra.Add(grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f));
                if (extra.Count > 0) HUDManager.Instance.SpawnBonusCoins(extra, extra.Count * SurvivalManager.COINS_PER_BONUS_TILE);
            }
            // GOLD RUSH (2026-10-30, Spencer: "there really is no reward for making and firing off the powerups"): on the bonus
            // level every tile a special clears pays coins too, times the climbing Gold Rush multiplier. (The finale blast above
            // pays through its own counter - not here, or it would pay twice.)
            if (!BonusBlastRunning && LevelRules.BonusLevelActive && !RulesEngine.Simulating && step.SpecialsFired != null && HUDManager.Instance != null)
                foreach (var f in step.SpecialsFired)
                {
                    LevelRules.GoldRushSpecialsFired++;   // the climb: every GOLD_RUSH_SPECIALS_PER_STEP specials fired = one step up
                    int mult = LevelRules.GoldRushMult();
                    // v4 (RM L20 "coins everywhere"): a blast pays for the GOLD COIN tiles it takes, times the multiplier
                    if (f.Gold == null || f.Gold.Count == 0) continue;
                    int pay = Mathf.Max(1, f.Gold.Count / Mathf.Max(1, LevelRules.GOLD_RUSH_TILES_PER_COIN)) * LevelRules.GOLD_RUSH_COIN_TILE * mult;
                    CoinWallet.Add(pay); SurvivalManager.PendingBonusCoins += pay; LevelMapPanel.Instance?.AddPendingCoinReward(pay);
                    var starts = new List<Vector3>();
                    foreach (var c in f.Gold) starts.Add(grid.CellToWorld(c.x, c.y) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f));
                    HUDManager.Instance.SpawnBonusCoins(starts, pay);
                }
        }

        /// <summary>The longest combo wind-up among this step's specials (0 in the bonus blast, which stays one quick ripple).</summary>
        /// <summary>Seconds until a propeller (or plane+bomb combo) fired this step lands on <paramref name="cell"/>; 0 if none is
        /// flying there. Same timing as the target's pop in the special-pop schedule (windup + the plane's own delay + takeoff + flight).</summary>
        private static float PlaneLandDelay(RulesEngine.StepResult step, Vector2Int cell)
        {
            if (step?.SpecialsFired == null) return 0f;
            float best = 0f;
            foreach (var f in step.SpecialsFired)
                if (f.Target != f.Cell && ((f.Kind == RulesEngine.SPECIAL_AIRPLANE && f.Target == cell)
                    || (f.Kind == RulesEngine.SPECIAL_COMBO_PLANE_BOMB && Mathf.Max(Mathf.Abs(cell.x - f.Target.x), Mathf.Abs(cell.y - f.Target.y)) <= RulesEngine.BOMB_RADIUS_POWERED)))   // the carried bomb's square too
                    best = Mathf.Max(best, StepComboWindup(step) + f.Delay + WordDropFX.AIRPLANE_TAKEOFF + WordDropFX.AIRPLANE_FLIGHT);
            return best;
        }

        private static float StepComboWindup(RulesEngine.StepResult step)
        {
            if (BonusBlastRunning || step == null || step.SpecialsFired == null) return 0f;
            float w = 0f;
            foreach (var f in step.SpecialsFired) w = Mathf.Max(w, WordDropFX.ComboWindup(f.Kind));
            return w;
        }

        /// <summary>MULTI-HIT CRATE (2026-11-03): each crate that lost a layer this step - wood-chip debris, a knock, then the next stage
        /// (X -> one plank -> slats).</summary>
        private void HitCrateTiles(GridManager grid, RulesEngine.StepResult step)
        {
            if (grid == null || step == null || step.CrateHitCells == null) return;
            var rules = RulesEngine.Instance;
            foreach (var c in step.CrateHitCells)
            {
                Tile t = null; try { t = grid.GetTile(c.x, c.y); } catch { }
                if (t == null) continue;
                var d = rules != null ? rules.GetCell(c.x, c.y) : null;
                int after = d != null ? d.CrateHP : Mathf.Max(1, t.CrateHP - 1);
                int before = Mathf.Max(t.CrateHP, after + 1);
                void Knock()
                {
                    if (t == null) return;
                    WordDropFX.Instance?.PlayCrateDebris(t.transform.position);
                    WordDropFX.Instance?.PlayPlankOff(t, before);   // 11-03: the plank that came off tumbles away
                    if (!t.IsAnimating) t.transform.DOPunchScale(Vector3.one * 0.12f, 0.22f, 6, 0.6f);   // knock (skipped mid-move so it can't fight a fall tween)
                    t.SetCrateHP(after);
                    GameAudio.Instance?.PlayCrateKnock();   // 11-07 sound pass: a layer coming off was silent
                }
                // 11-04 (Spencer: "the planks and animations of the crate being hit need to wait until the propeller book flies and
                // hits it. right now it fires as soon as you use the book"): a crate the propeller is flying to knocks when it LANDS.
                float land = PlaneLandDelay(step, c);
                if (land > 0f) DOVirtual.DelayedCall(land, Knock, ignoreTimeScale: true);
                else if (CRATE_KNOCK_ON_ARRIVAL && step.SpecialsFired != null && step.SpecialsFired.Count > 0) StartCoroutine(KnockWhenBlastArrives(c, Knock));
                else Knock();
            }
        }

        /// <summary>11-07 (Spencer: "when a rocket goes through a crate it also needs to show the destruction ... or a bomb goes off"):
        /// a crate that LOSES a layer to a special knocked the moment the special fired - the plank flew while the rocket was still
        /// at its start and the tiles round the crate popped later, as the head / blast wave reached them. It now knocks when the
        /// blast reaches it: the pop time of its own or a side neighbour's cell (ApplySpecialStep fills _specialPopDelay right
        /// after this step's crate hits, hence the one-frame wait).</summary>
        public static bool CRATE_KNOCK_ON_ARRIVAL = true;
        private IEnumerator KnockWhenBlastArrives(Vector2Int c, System.Action knock)
        {
            float t0 = Time.unscaledTime;
            yield return null;
            float best = -1f;
            var around = new[] { c, c + Vector2Int.left, c + Vector2Int.right, c + Vector2Int.up, c + Vector2Int.down };
            foreach (var q in around)
                if (_specialPopDelay.TryGetValue(q, out float d) && (best < 0f || d < best)) best = d;
            while (best > 0f && Time.unscaledTime - t0 < best) yield return null;
            knock();
        }

        /// <summary>BOOKSHELF (2026-11-03): each book knocked off this step - its slot empties on the shelf, a copy flies to the Books
        /// goal, and the shelf takes a knock. Positions come from the grid (not the tile), so the last book still flies when the
        /// shelf's tiles are cleared in the same step.</summary>
        private void HitShelfTiles(GridManager grid, RulesEngine.StepResult step)
        {
            if (grid == null || step == null || step.ShelfHits == null) return;
            foreach (var kv in step.ShelfHits)
            {
                var a = kv.Key; int slot = kv.Value;
                Tile t = null; try { t = grid.GetTile(a.x, a.y); } catch { }
                bool safe = t != null && t.ShelfKind == 1;
                int style = t != null ? t.ShelfStyle : 0;
                void Hit()
                {
                    if (safe) { HitSafe(grid, t, a, slot); return; }   // SAFE (11-03): lamps, not books
                    Vector3 at = Tile.ShelfBookWorld(grid.CellToWorld(a.x, a.y), grid.CellSize, slot, style);
                    if (t != null && t.ShelfPart == 1)
                    {
                        t.SetShelf(1, slot);   // hide the slot that just emptied
                        WordDropFX.Instance?.PlayCrateDebris(at);
                        if (!t.IsAnimating) t.transform.DOPunchScale(Vector3.one * 0.06f, 0.22f, 6, 0.6f);
                    }
                    HUDManager.Instance?.FlyBook(at, Tile.ShelfBookFlySprite(slot));
                    HapticsManager.ObstacleTick();   // 11-17 haptics pass
                    if (GameAudio.Instance != null && GameAudio.Instance.HasKit("tome_land")) GameAudio.Instance.PlayKit("tome_land", GameAudio.TOME_LAND_VOL);   // 11-07 pick: a book knocked off the shelf (Book Drop Low A2)
                }
                // 11-04 (Spencer "start away" on the list: shelf/safe like the crates) - a propeller flying to the shelf/safe hits it on LANDING
                float land = 0f;
                for (int dx = 0; dx < 2; dx++) for (int dy = 0; dy < 2; dy++) land = Mathf.Max(land, PlaneLandDelay(step, new Vector2Int(a.x + dx, a.y + dy)));
                if (land > 0f) DOVirtual.DelayedCall(land, Hit, ignoreTimeScale: true); else Hit();
            }
        }

        /// <summary>SAFE (2026-11-03): a hit lights the next lamp; the last hit swings the door open on a pile of coins (Spencer's open
        /// art over the board for a beat), pays RulesEngine.SAFE_COINS the way an opened treasure does, and the coins fly to the HUD.
        /// The safe's four tiles clear in the same step (crate debris is skipped for safes - WordDropFX.MaybeCrateDebris).</summary>
        private void HitSafe(GridManager grid, Tile t, Vector2Int a, int left)
        {
            Vector3 anchor = grid.CellToWorld(a.x, a.y);
            if (left > 0)
            {
                int lit = Tile.SafeLampsLit(left) - 1;   // the newest lamp (Gold Rush lights two per hit)
                t.SetShelf(1, left);
                SafeHitImpact(t, Tile.SafeLampWorld(anchor, grid.CellSize, lit), lit, grid.CellSize);
                if (lit > 0 && Tile.SafeLampsLit(left) - Tile.SafeLampsLit(left + 1) > 1) t.PopSafeLamp(lit - 1);
                if (LevelRules.BonusLevelActive && LevelRules.GOLD_RUSH_SAFES && !RulesEngine.Simulating)
                {   // GOLD RUSH v6: every lamp hit pays a little, straight into the REWARD counter
                    int pay = LevelRules.GOLD_RUSH_LAMP_COINS;
                    CoinWallet.Add(pay); SurvivalManager.PendingBonusCoins += pay; LevelMapPanel.Instance?.AddPendingCoinReward(pay);
                    HUDManager.Instance?.SpawnBonusCoins(new List<Vector3> { Tile.SafeLampWorld(anchor, grid.CellSize, lit) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f) }, pay);
                }
                return;
            }
            bool rush = LevelRules.BonusLevelActive && LevelRules.GOLD_RUSH_SAFES;
            if (RulesEngine.SAFE_STAYS_OPEN && t != null) t.SetShelf(1, 0);   // 11-04: the tile now draws the open, empty vault (stays on the board); the blast FX plays over it
            int coins = rush ? LevelRules.GOLD_RUSH_JACKPOT : RulesEngine.SAFE_COINS;   // Gold Rush: the jackpot
            StartCoroutine(SafeOpenFX(Tile.ShelfCentreWorld(anchor, grid.CellSize) + new Vector3(grid.BoardSlideOffsetX, 0f, -0.4f), Tile.ShelfWorldSize(grid.CellSize), coins));
            CoinWallet.Add(coins);
            SurvivalManager.PendingBonusCoins += coins;
            LevelMapPanel.Instance?.AddPendingCoinReward(coins);
            WDLog.Log($"[Safe] opened at ({a.x},{a.y}) -> +{coins} coins");
        }
        // ── SAFE BLOW-OFF (2026-11-03, Spencer: "the door gets blown off its hinges ... make the inside empty and then we can fill it
        // with our own art of our coins in stacks"). Flash + shake; the door (Blockers/safe_door) blasts off toward the camera,
        // spinning, and falls away under gravity; the empty vault (safe_empty) stays; the coin stacks (safe_coins) pop up inside;
        // then the coins fly to the HUD and the vault fades. Falls back to the old open-door pop if the new art is missing. ──
        // IMPACT (11-03 Spencer: a hit "should show some kind of force of impact"): the safe squashes and rebounds, the new lamp pops
        // big with a burst of gold sparks, the board gives a small knock.
        public static float SAFE_HIT_SHAKE = 0.045f;
        public static int   SAFE_HIT_SPARKS = 12, SAFE_OPEN_SPARKS = 30;
        public static Color SAFE_SPARK_HDR = new Color(2.4f, 1.7f, 0.5f, 1f);
        private void SafeHitImpact(Tile t, Vector3 lampAt, int lamp, float cell)
        {
            t.PunchShelf(1f);
            t.PopSafeLamp(lamp);
            SafeSparks(lampAt, cell, SAFE_HIT_SPARKS, 1f);
            GridManager.Instance?.ShakeBoard(SAFE_HIT_SHAKE, 0.12f);
            HapticsManager.Medium();   // 11-17 haptics pass
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("safe_lamp")) GameAudio.Instance.PlayKit("safe_lamp"); else { GameAudio.Instance?.PlayCrateBreak(); GameAudio.Instance?.PlayCoinLand(); }   // 11-07 pick: Metal Latch B3
        }
        private static void SafeSparks(Vector3 at, float cell, int n, float reach)
        {
            var fx = WordDropFX.Instance; if (fx == null) return;
            for (int i = 0; i < n; i++)
            {
                float ang = Random.Range(0f, Mathf.PI * 2f), sp = Random.Range(2.5f, 5.5f) * cell * reach;
                fx.SpawnSpark(at + new Vector3(0f, 0f, -0.5f), new Vector3(Mathf.Cos(ang), Mathf.Sin(ang) + 0.35f, 0f) * sp, cell * Random.Range(0.10f, 0.18f), Random.Range(0.35f, 0.6f), SAFE_SPARK_HDR);
            }
        }
        /// <summary>FX TEST (11-03, Spencer: "work on the safe that is on the board"): the full blow-off ON the first safe on the board -
        /// visual only: the safe hides while it plays and comes back after (the data is untouched, so it can be replayed).
        /// No safe on the board = plays at the board centre.</summary>
        public void TestSafeBlowOff()
        {
            var g = GridManager.Instance; if (g == null) return;
            for (int c = 0; c < RulesEngine.COLS; c++) for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                Tile t = null; try { t = g.GetTile(c, r); } catch { }
                if (t == null || t.ShelfPart != 1 || t.ShelfKind != 1) continue;
                StartCoroutine(TestBlowOffOn(t, g.CellToWorld(c, r), g.CellSize));
                return;
            }
            var anchor = g.CellToWorld(RulesEngine.COLS / 2 - 1, RulesEngine.ROWS / 2 - 1);
            StartCoroutine(SafeOpenFX(Tile.ShelfCentreWorld(anchor, g.CellSize) + new Vector3(g.BoardSlideOffsetX, 0f, -0.4f), Tile.ShelfWorldSize(g.CellSize)));
        }
        private System.Collections.IEnumerator TestBlowOffOn(Tile t, Vector3 anchor, float cell)
        {
            var g = GridManager.Instance;
            t.SetShelf(1, 0);   // all lamps lit, as on the real last hit
            yield return new WaitForSeconds(0.15f);
            t.SetShelfVisible(false);
            yield return SafeOpenFX(Tile.ShelfCentreWorld(anchor, cell) + new Vector3(g != null ? g.BoardSlideOffsetX : 0f, 0f, -0.4f), Tile.ShelfWorldSize(cell));
            yield return new WaitForSeconds(0.4f);
            if (t != null) { t.SetShelf(1, RulesEngine.SafeHitsNow); t.SetShelfVisible(true); }   // back to a fresh closed safe for another go
        }
        /// <summary>FX TEST (11-03): one hit on the first safe on the board (visual only - lights the next lamp, no data change).</summary>
        public bool TestSafeHit()
        {
            var g = GridManager.Instance; if (g == null) return false;
            for (int c = 0; c < RulesEngine.COLS; c++) for (int r = 0; r < RulesEngine.ROWS; r++)
            {
                Tile t = null; try { t = g.GetTile(c, r); } catch { }
                if (t == null || t.ShelfPart != 1 || t.ShelfKind != 1) continue;
                int left = t.ShelfBooks > 1 ? t.ShelfBooks - 1 : RulesEngine.SafeHitsNow - 1;   // loops back to the first hit
                int lit = Tile.SafeLampsLit(left) - 1;
                t.SetShelf(1, left);
                SafeHitImpact(t, Tile.SafeLampWorld(g.CellToWorld(c, r), g.CellSize, lit), lit, g.CellSize);
                return true;
            }
            return false;
        }
        public static float SAFE_OPEN_HOLD = 0.9f;
        public static int   SAFE_VAULT_ORDER = 44;  // 11-04 ABOVE the tiles (row bands 5..~34) and below FX (46+): the letters fall in BEHIND the opening safe and show as the vault fades (Spencer: "the letters fell over the safe before its animation was done" - his 11-03 ask was "cascade down behind it"; 3 = under the tiles, the old misread)   // 11-03: longer look at the loot before it fades (was .55)
        public static float SAFE_DOOR_FIT = 0.96f;                     // door width / safe width (its gold frame lines up with the closed safe's)
        public static float SAFE_DOOR_DUR = 0.95f, SAFE_DOOR_GRAVITY = 16f, SAFE_DOOR_SPIN = 260f, SAFE_DOOR_GROW = 1.6f;
        public static Vector3 SAFE_COINS_AT = new Vector3(0.4976f, 0.5191f, 0.5981f);   // coin stacks: centre x / y DOWN, width (0..1 of the safe)
        private System.Collections.IEnumerator SafeOpenFX(Vector3 at, float size, int coinsPaid = -1)
        {
            if (coinsPaid < 0) coinsPaid = RulesEngine.SAFE_COINS;
            var empty = Tile.SafeEmptySprite; var door = Tile.SafeDoorSprite; var coinsSp = Tile.SafeCoinsSprite;
            if (empty == null || door == null) { yield return SafeOpenSwingFX(at, size); yield break; }
            SpriteRenderer Make(string n, Sprite sp, Vector3 pos, float width, int order)
            {
                var g = new GameObject(n); g.transform.position = pos;
                var r = g.AddComponent<SpriteRenderer>(); r.sprite = sp; r.sortingOrder = order;
                g.transform.localScale = Vector3.one * (width / Mathf.Max(0.0001f, sp.bounds.size.x));
                return r;
            }
            // 11-03 (Spencer: "can the letters still cascade down behind it"): the letters keep falling into the cleared space at their
            // normal pace, BEHIND the vault + coins (SAFE_VAULT_ORDER 44 / 45 > tiles), and show as the vault fades. The door and its
            // flash stay on top (64 / 65).
            var vault = Make("SafeVault", empty, at, size, SAFE_VAULT_ORDER);
            var drSR = Make("SafeDoor", door, at + new Vector3(0f, 0f, -0.05f), size * SAFE_DOOR_FIT, 64);
            GridManager.Instance?.ShakeBoard(0.24f, 0.32f);   // 11-03: the blast hits harder than a lamp
            HapticsManager.MegaImpact();   // 11-17 haptics pass
            if (GameAudio.Instance != null && GameAudio.Instance.HasKit("safe_open")) GameAudio.Instance.PlayKit("safe_open"); else GameAudio.Instance?.PlayCrateBreak();   // 11-07 pick: Sack Drop On Table Metal Loose B6
            WordDropFX.Instance?.PlayScreenFlash(2);
            WordDropFX.Instance?.PlayChestCoinBurst(at, size);   // gold glow + bubble pop behind the vault
            SafeSparks(at, size * 0.5f, SAFE_OPEN_SPARKS, 1.4f);
            vault.transform.DOPunchScale(Vector3.one * vault.transform.localScale.x * 0.12f, 0.35f, 6, 0.6f);
            // flash: a white copy of the door that fades in a blink
            var fl = Make("SafeFlash", door, drSR.transform.position + new Vector3(0f, 0f, -0.01f), size * SAFE_DOOR_FIT, 65);
            fl.color = new Color(1f, 1f, 1f, 0.9f); fl.material = new Material(Shader.Find("GUI/Text Shader"));   // solid white silhouette
            fl.DOFade(0f, 0.18f).OnComplete(() => { if (fl != null) Destroy(fl.gameObject); });
            // 11-03 (Spencer: "when the door blows off should the coins already be in there"): the stacks are IN the vault from the
            // first frame, behind the door, so the blast reveals them - no pop-in
            SpriteRenderer coins = null;
            if (coinsSp != null)
            {
                var cpos = at + new Vector3((SAFE_COINS_AT.x - 0.5f) * size, (0.5f - SAFE_COINS_AT.y) * size, -0.02f);
                coins = Make("SafeCoins", coinsSp, cpos, size * SAFE_COINS_AT.z, SAFE_VAULT_ORDER + 1);
                if (SAFE_COIN_SPARKLE) StartCoroutine(SafeCoinSparkle(coins, 0.16f + 0.3f + SAFE_OPEN_HOLD));   // 11-17: the reveal glitters until the coins fade
            }
            StartCoroutine(SafeDoorFly(drSR, size));
            yield return new WaitForSeconds(0.16f);
            GameAudio.Instance?.PlayCoinDisperse();
            yield return new WaitForSeconds(0.3f);
            if (LevelRules.BonusLevelActive && HUDManager.Instance != null)
            {   // Gold Rush: the REWARD counter only counts the bonus coin path - stream them off the stacks
                var starts = new List<Vector3>();
                for (int i = 0; i < 8; i++) starts.Add(at + new Vector3(Random.Range(-0.28f, 0.28f) * size, Random.Range(-0.2f, 0.15f) * size, -0.1f));
                HUDManager.Instance.SpawnBonusCoins(starts, coinsPaid);
            }
            else HUDManager.Instance?.SpawnTreasureCoins(at, coinsPaid);   // the bonus coin flight, into the HUD
            yield return new WaitForSeconds(SAFE_OPEN_HOLD);
            if (coins != null) { coins.DOFade(0f, 0.3f); coins.transform.DOScale(coins.transform.localScale * 0.85f, 0.3f); }
            if (vault != null) vault.DOFade(0f, 0.3f);
            yield return new WaitForSeconds(0.32f);
            if (coins != null) Destroy(coins.gameObject);
            if (vault != null) Destroy(vault.gameObject);
        }
        // ── COIN SPARKLE (2026-11-17, Spencer: "when the safe opens could we have some kind of particle effect sparkle on the coins
        // inside the safe for the reveal?"). Four-point glints wink on and off over the coin stacks from the moment the door blows
        // off until the coins fade: a quick cluster for the reveal, then a steady twinkle. Drawn just above the coins and under the
        // flying door. Gold-white HDR through the FX material's _Color, so it blooms on device. SAFE_COIN_SPARKLE = false: none. ──
        public static bool  SAFE_COIN_SPARKLE = true;
        public static int   SAFE_SPARKLE_BURST = 6;                 // glints in the first moment of the reveal
        public static float SAFE_SPARKLE_EVERY = 0.085f;            // then one this often
        public static Vector2 SAFE_SPARKLE_SIZE = new Vector2(0.16f, 0.34f);   // glint size, x the coin pile's width
        public static Vector2 SAFE_SPARKLE_LIFE = new Vector2(0.32f, 0.55f);
        public static Color SAFE_SPARKLE_HDR = new Color(2.6f, 2.3f, 1.5f, 1f);
        private System.Collections.IEnumerator SafeCoinSparkle(SpriteRenderer coins, float duration)
        {
            var fx = WordDropFX.Instance; var star = WordDropFX.TwinkleSprite;
            if (fx == null || star == null || coins == null) yield break;
            yield return new WaitForSeconds(0.06f);   // the door has just left
            float t = 0f, next = 0f; int made = 0;
            while (t < duration && coins != null)
            {
                while (coins != null && (made < SAFE_SPARKLE_BURST || t >= next))
                {
                    var b = coins.bounds;
                    // over the stacks, not the empty corners: the upper two thirds of the pile, pulled in from the sides
                    var at = new Vector3(Mathf.Lerp(b.min.x, b.max.x, Random.Range(0.10f, 0.90f)), Mathf.Lerp(b.min.y, b.max.y, Random.Range(0.30f, 0.98f)), coins.transform.position.z - 0.02f);
                    float sz = b.size.x * Random.Range(SAFE_SPARKLE_SIZE.x, SAFE_SPARKLE_SIZE.y);
                    float delay = made < SAFE_SPARKLE_BURST ? made * 0.035f : 0f;
                    StartCoroutine(SafeGlint(fx, star, at, sz, Random.Range(SAFE_SPARKLE_LIFE.x, SAFE_SPARKLE_LIFE.y), delay));
                    made++;
                    if (made >= SAFE_SPARKLE_BURST) next = t + SAFE_SPARKLE_EVERY;
                    if (made > 80) yield break;   // never runaway
                }
                t += Time.deltaTime;
                yield return null;
            }
        }
        private System.Collections.IEnumerator SafeGlint(WordDropFX fx, Sprite star, Vector3 at, float size, float life, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            var sr = fx.MakeFxSprite("SafeGlint", star, SAFE_SPARKLE_HDR, SAFE_VAULT_ORDER + 2);
            if (sr == null) yield break;
            sr.transform.position = at;
            float k0 = size / Mathf.Max(0.0001f, star.bounds.size.x);
            float t = 0f, rot0 = Random.Range(-20f, 20f), spin = Random.Range(-70f, 70f);
            while (t < life && sr != null)
            {
                t += Time.deltaTime; float k = t / life;
                float s = k < 0.3f ? k / 0.3f : 1f - (k - 0.3f) / 0.7f;        // snaps open, eases shut
                sr.transform.localScale = Vector3.one * k0 * Mathf.Max(0f, s);
                sr.transform.rotation = Quaternion.Euler(0f, 0f, rot0 + spin * t);
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }
        private System.Collections.IEnumerator SafeDoorFly(SpriteRenderer sr, float size)
        {
            if (sr == null) yield break;
            var tr = sr.transform; float side = Random.value < 0.5f ? -1f : 1f;
            var vel = new Vector3(side * Random.Range(2.2f, 3.0f), Random.Range(4.0f, 5.0f), 0f) * size * 0.5f;
            float spin = side * -SAFE_DOOR_SPIN * Random.Range(0.85f, 1.15f), t = 0f; Vector3 s0 = tr.localScale;
            while (t < SAFE_DOOR_DUR && sr != null)
            {
                float dt = Time.deltaTime; t += dt;
                vel.y -= SAFE_DOOR_GRAVITY * size * 0.5f * dt;
                tr.position += vel * dt;
                tr.Rotate(0f, 0f, spin * dt);
                tr.localScale = s0 * Mathf.Lerp(1f, SAFE_DOOR_GROW, Mathf.Clamp01(t / (SAFE_DOOR_DUR * 0.6f)));   // flies at the camera
                float f = Mathf.Clamp01((t - SAFE_DOOR_DUR * 0.65f) / (SAFE_DOOR_DUR * 0.35f));
                sr.color = new Color(1f, 1f, 1f, 1f - f);
                yield return null;
            }
            if (sr != null) Destroy(sr.gameObject);
        }
        private System.Collections.IEnumerator SafeOpenSwingFX(Vector3 at, float size)
        {
            var sp = Tile.SafeOpenSprite; if (sp == null) yield break;
            var go = new GameObject("SafeOpen"); go.transform.position = at;
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = sp; sr.sortingOrder = 60;
            float s0 = size / Mathf.Max(0.0001f, sp.bounds.size.x);
            go.transform.localScale = Vector3.one * s0 * 0.94f;
            var seq = DOTween.Sequence().SetTarget(go);
            seq.Append(go.transform.DOScale(s0 * 1.12f, 0.12f).SetEase(Ease.OutQuad));
            seq.Append(go.transform.DOScale(s0, 0.22f).SetEase(Ease.OutBack, 2f));
            GameAudio.Instance?.PlayCoinDisperse();
            yield return new WaitForSeconds(0.18f);
            HUDManager.Instance?.SpawnTreasureCoins(at, RulesEngine.SAFE_COINS);
            yield return new WaitForSeconds(SAFE_OPEN_HOLD);
            if (go == null) yield break;
            sr.DOFade(0f, 0.3f).OnComplete(() => { if (go != null) Destroy(go); });
            go.transform.DOScale(s0 * 0.9f, 0.3f).SetEase(Ease.InQuad);
        }

        /// <summary>TREASURE (2026-10-27): each treasure that lost a band this step - repaint to its new state + the band-off FX.</summary>
        private void HitTreasureTiles(GridManager grid, RulesEngine.StepResult step)
        {
            if (grid == null || step == null || step.TreasureHitCells == null) return;
            var rules = RulesEngine.Instance;
            foreach (var c in step.TreasureHitCells)
            {
                Tile t = null; try { t = grid.GetTile(c.x, c.y); } catch { }
                if (t == null) continue;
                var d = rules != null ? rules.GetCell(c.x, c.y) : null;
                int before = t.TreasureHits, after = d != null ? d.TreasureHits : Mathf.Max(1, before - 1);
                if (before <= after) before = after + 1;
                WDLog.Log($"[Treasure] hit at ({c.x},{c.y}) {before}->{after} band={(Tile.TreasureBandSprite(before) != null)}");
                TreasureFX.Hit(t, before);
                t.SetTreasureHits(after);
            }
        }

        // WORD POP-UPS FOR EVERY WORD (2026-10-28 Spencer: "words made on a cascade need word pop ups ... i think this only
        // happens during combos"). The pop-up fired only at the TAP, for the one cluster under it; any other word that went in
        // the same detonation (caught in a blast, chained, or several armed words going at once) got none. Each Exploding
        // step now lists its PoppedWords; any not already shown at the tap gets its own pop-up at its centre.
        private readonly HashSet<string> _tapPopWords = new HashSet<string>();
        public static bool POPUP_EVERY_WORD = true;
        /// <summary>11-07 (Spencer: "some of the explosions seem confusing cause theres just so much going on"): a word that sets off a
        /// special no longer pops its text - it landed right in the middle of the blast ("ALLY YOGI" over a TNT, L9). The explosion is
        /// that word's payoff (Royal Match puts no text over a blast). The owl still reacts. true = the old text over the blast.</summary>
        public static bool WORD_POP_OVER_BLAST = false;
        private void PopUpOtherWords(GridManager grid, RulesEngine.StepResult step)
        {
            if (!POPUP_EVERY_WORD || grid == null || step?.PoppedWords == null || ClusterOutline.Instance == null) { _tapPopWords.Clear(); return; }
            var shown = new HashSet<string>(_tapPopWords);
            _tapPopWords.Clear();
            foreach (var kv in step.PoppedWords)
            {
                string w = kv.Key?.ToUpperInvariant();
                if (string.IsNullOrEmpty(w) || w.Length < 2 || kv.Value == null || kv.Value.Count == 0 || !shown.Add(w)) continue;
                if (!WORD_POP_OVER_BLAST && step.SpecialsFired != null && step.SpecialsFired.Exists(f => kv.Value.Contains(f.Cell))) continue;   // 11-07: no text over its own blast
                Vector3 sum = Vector3.zero;
                foreach (var c in kv.Value) sum += grid.CellToWorld(c.x, c.y);
                Vector3 at = sum / kv.Value.Count; at.z = -0.5f;
                ClusterOutline.Instance.PopPayout(at, 0, false, 0f, -1, null, w);
            }
        }

        private void DefrostThawedTiles(GridManager grid, RulesEngine.StepResult step)
        {
            PopUpOtherWords(grid, step);   // pop-ups for words the tap didn't cover (cascade / combo words)
            HitTreasureTiles(grid, step);   // TREASURE: same moment as the thaw (survivors of this detonation)
            HitCrateTiles(grid, step);      // 11-03 MULTI-HIT CRATE: crates that lost a layer (same moment)
            HitShelfTiles(grid, step);      // 11-03 BOOKSHELF: books knocked off fly to the goal
            if (grid == null || step == null || step.ThawedCells == null || step.ThawedCells.Count == 0) return;
            bool anyThawed = false;
            for (int i = 0; i < step.ThawedCells.Count; i++)
            {
                Tile thawed = null;
                try { thawed = grid.GetTile(step.ThawedCells[i].x, step.ThawedCells[i].y); }
                catch { /* ignore */ }
                if (thawed != null)
                {
                    thawed.ClearPrimedGlow(); // thawed tile becomes a PLAIN letter — no lingering magenta (obscured the melt). 2026-07-07 Spencer.
                    float land = PlaneLandDelay(step, step.ThawedCells[i]);   // 11-04: ice a propeller is flying to breaks when it LANDS
                    var tt = thawed;
                    if (land > 0f) DOVirtual.DelayedCall(land, () => { if (tt != null) { tt.PlayDefrost(); GameAudio.Instance?.PlayIceMelt(); } }, ignoreTimeScale: true);
                    else { thawed.PlayDefrost(); anyThawed = true; }
                }
            }
            // ONE melt sound per batch. The clip is a ~0.94s ascending run of pops, so playing it
            // per tile would overlap into mush when a detonation thaws several at once. 2026-07-30.
            if (anyThawed) GameAudio.Instance?.PlayIceMelt();
        }

        private const float TILE_FLASH_BOX_CHANCE = 0.6f; // 60% of detonations show boxes
        private void FireTileFlashBoxes(IList<Tile> dying)
        {
            if (!WordDropFX.FX_TileFlashBox) { WDLog.Log("[FX] TileFlashBox: SKIPPED"); return; }
            if (TileFlashBox.Instance == null || _grid == null || dying == null) return;
            if (dying.Count == 0) return;
            WDLog.Log("[FX] TileFlashBox: FIRED");

            // One roll for the entire detonation — all-or-nothing variety.
            if (Random.value >= TILE_FLASH_BOX_CHANCE) return;

            float cellSize = _grid.CellSize;
            for (int i = 0; i < dying.Count; i++)
            {
                Tile t = dying[i];
                if (t == null) continue;
                TileFlashBox.Instance.Play(t.transform.position, cellSize);
            }
        }

        /// <summary>Tiles of theme words queued by LevelRules.OnWordPopped this step: hide them, take them OUT of the explosion
        /// list, and fly each letter to the theme goal picture (staggered; the last one ticks the badge). 2026-10-23.</summary>
        public static float THEME_FLY_STAGGER = 0.06f;
        private void LaunchThemeFlights(List<Tile> dying, GridManager grid)
        {
            if (LevelRules.PendingThemeWords.Count == 0 || grid == null || dying == null) return;
            foreach (var cells in LevelRules.PendingThemeWords)
            {
                for (int i = 0; i < cells.Count; i++)
                {
                    var c = cells[i];
                    var t = grid.GetTile(c.x, c.y);
                    bool last = i == cells.Count - 1;
                    if (t == null) { if (last) HUDManager.Instance?.FlyThemeLetter(Vector3.zero, ' ', true, 0f); continue; }
                    if (t.SpecialKind != RulesEngine.SPECIAL_NONE) { if (last) HUDManager.Instance?.FlyThemeLetter(t.transform.position, t.Letter, true, i * THEME_FLY_STAGGER); continue; }   // a special in the word still blows (its own FX)
                    dying.Remove(t);
                    t.HideForFlight();
                    HUDManager.Instance?.FlyThemeLetter(t.transform.position, t.Letter, last, i * THEME_FLY_STAGGER);
                }
            }
            WDLog.Log($"[Theme] {LevelRules.PendingThemeWords.Count} word(s) flying to the goal");
            LevelRules.PendingThemeWords.Clear();
        }

        private List<string> _lastBurstWords;
        private string TakeBurstWords()
        {
            if (_lastBurstWords == null || _lastBurstWords.Count == 0) { WDLog.Log("[WordPopup] take: NO cached words"); return ""; }
            string w = string.Join(" ", _lastBurstWords); _lastBurstWords = null; WDLog.Log($"[WordPopup] take: '{w}'"); return w;
        }
        private void CacheBurstTriggers(RulesEngine.StepResult step)
        {
            if (step == null || step.Triggers == null || step.Triggers.Count == 0)
            {
                _pendingBurstTriggers     = null;
                _pendingBurstTriggerWords = null;
                _stagerTriggers           = null;
                _stagerTriggerWords       = null;
                return;
            }
            _pendingBurstTriggers     = new List<PrimedTriggeredEvent>(step.Triggers);
            // Words going off this step, kept for the score pop-up (WORD instead of "+N", 2026-10-22). 1-letter "words" are the
            // bonus round's seeded specials — not words.
            _lastBurstWords = new List<string>();
            foreach (var tr in step.Triggers)
                if (tr != null && !string.IsNullOrEmpty(tr.TriggeredWord) && tr.TriggeredWord.Length > 1 && !_lastBurstWords.Contains(tr.TriggeredWord.ToUpperInvariant()))
                    _lastBurstWords.Add(tr.TriggeredWord.ToUpperInvariant());
            WDLog.Log($"[WordPopup] cached {_lastBurstWords.Count} word(s): {string.Join(",", _lastBurstWords)}");
            // Live copy for the staggered-combo gate (see field decl) — FirePerWordBurst consumes
            // _pendingBurstTriggers before ExplodeWordsThenSplash reads it. 2026-07-06 Spencer.
            _stagerTriggers           = new List<PrimedTriggeredEvent>(step.Triggers);
            _stagerTriggerWords       = step.TriggerWords != null ? new List<RulesWordMatch>(step.TriggerWords) : null;
            _pendingBurstTriggerWords = step.TriggerWords != null
                ? new List<RulesWordMatch>(step.TriggerWords) : null;
            _pendingBurstChainDepth  = step.ChainDepth;
            _pendingBurstLongestWord = step.LongestWordLength;

            // Mirror to WordDropFX side-channel so the meltdown windup in
            // WordDropFX.PlayExplosion can filter per-tile FX (heat overlay,
            // primed glow orb, perlin shake, magic explosive prefab) to ONLY
            // word tiles — junk/collateral splash tiles stay still during
            // the windup and only explode at the impact moment with everyone
            // else. Each entry is one word.
            if (WordDropFX._pendingCascadeWords == null)
                WordDropFX._pendingCascadeWords = new List<List<Tile>>();
            for (int t = 0; t < step.Triggers.Count; t++)
            {
                var trig = step.Triggers[t];
                if (trig.TriggeredCells == null || trig.TriggeredCells.Count == 0) continue;
                var primedTiles = new List<Tile>();
                for (int c = 0; c < trig.TriggeredCells.Count; c++)
                {
                    Tile tile = null;
                    try { tile = _grid.GetTile(trig.TriggeredCells[c].x, trig.TriggeredCells[c].y); }
                    catch { /* ignore */ }
                    if (tile != null) primedTiles.Add(tile);
                }
                if (primedTiles.Count > 0)
                    WordDropFX._pendingCascadeWords.Add(primedTiles);
            }
            if (step.TriggerWords != null)
            {
                for (int w = 0; w < step.TriggerWords.Count; w++)
                {
                    var tw = step.TriggerWords[w];
                    if (tw.Cells == null || tw.Cells.Count == 0) continue;
                    var twTiles = new List<Tile>();
                    for (int c = 0; c < tw.Cells.Count; c++)
                    {
                        Tile tile = null;
                        try { tile = _grid.GetTile(tw.Cells[c].x, tw.Cells[c].y); }
                        catch { /* ignore */ }
                        if (tile != null) twTiles.Add(tile);
                    }
                    if (twTiles.Count > 0)
                        WordDropFX._pendingCascadeWords.Add(twTiles);
                }
            }
        }

        /// <summary>
        /// Fires one BigBurstFlash per triggered primed word, using the cached
        /// triggers captured during TriggersFound. Called from every Exploding
        /// phase handler so the flash lands in sync with the actual explosion.
        ///
        /// Gated to BIG moments so it stays a rare "whoa" beat:
        ///   - chain depth >= 2 (cascading detonation)
        ///   - OR longest primed word >= 6 letters
        ///   - OR 3+ primed words in a single cluster
        /// All other (small solo) detonations fall back to the existing flipbook +
        /// particle stack without the screen-wide flash.
        /// </summary>
        private void FirePerWordBurst()
        {
            _screenFlashFiredThisBurst = false; // reset per burst pass
            if (BigBurstFlash.Instance == null) { _pendingBurstTriggers = null; _pendingBurstTriggerWords = null; return; }
            if (_grid == null) { _pendingBurstTriggers = null; _pendingBurstTriggerWords = null; return; }
            if (_pendingBurstTriggers == null || _pendingBurstTriggers.Count == 0) return;

            // 2026-05-15 (v2): BigBurst beam fires ONLY on the initial detonation
            // step (chainDepth == 0), never on cascade steps. Before this fix,
            // deep cascades reaching chainDepth 4+ would fire the beam alone
            // while the cascade visual stayed simple Tier1Pop — visual mismatch.
            // Within the initial step, the beam only fires for impressive events
            // (long word or many triggers), keeping it rare and meaningful.
            bool bigMoment = _pendingBurstChainDepth == 0
                          && (_pendingBurstLongestWord >= 7
                              || _pendingBurstTriggers.Count >= 4);
            WDLog.Log($"[BigBurst] bigMoment={bigMoment} — chainDepth={_pendingBurstChainDepth} " +
                      $"longestWord={_pendingBurstLongestWord} triggers={_pendingBurstTriggers.Count}");
            if (!bigMoment)
            {
                // Small detonation — no screen-wide flash. Clear cache and bail so
                // we don't leak stale triggers into the next big moment.
                _pendingBurstTriggers     = null;
                _pendingBurstTriggerWords = null;
                return;
            }

            // Phase 11g — duck music briefly when a big moment lands. Per-spec
            // gate: chainDepth >= 2 OR longestWord >= 6. Avoids ducking on
            // every plain detonation (which would pump the music). MeltdownManager
            // owns the longer 0.30s duck for full meltdowns separately.
            if (_pendingBurstChainDepth >= 2 || _pendingBurstLongestWord >= 6)
                GameAudio.Instance?.DuckMusicBriefly(0.25f);

            Color burstTint = (_pendingBurstChainDepth >= 2 && _pendingBurstLongestWord >= 6)
                ? new Color(1.8f, 1.4f, 0.7f, 1f)   // HDR warm gold — big + skilled
                : new Color(1.6f, 1.6f, 1.6f, 1f);  // HDR white

            foreach (var trig in _pendingBurstTriggers)
            {
                if (trig.TriggeredCells == null || trig.TriggeredCells.Count == 0) continue;

                int minCol = int.MaxValue, maxCol = int.MinValue;
                int minRow = int.MaxValue, maxRow = int.MinValue;
                Vector3 wordCenter = Vector3.zero;
                int tileCount = 0;
                foreach (var cell in trig.TriggeredCells)
                {
                    if (cell.x < minCol) minCol = cell.x;
                    if (cell.x > maxCol) maxCol = cell.x;
                    if (cell.y < minRow) minRow = cell.y;
                    if (cell.y > maxRow) maxRow = cell.y;
                    Tile wt = _grid.GetTile(cell.x, cell.y);
                    if (wt != null) { wordCenter += wt.transform.position; tileCount++; }
                }
                if (tileCount == 0) continue;
                wordCenter /= tileCount;

                bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                int wordLen = vertical ? (maxRow - minRow + 1) : (maxCol - minCol + 1);

                // Flash spans the full viewport along the word's axis — extends past
                // the screen edges so the impact feels unbounded.
                // Horizontal word → flash covers the full screen WIDTH.
                // Vertical word   → flash covers the full screen HEIGHT.
                Camera cam = _cam != null ? _cam : Camera.main;
                float halfH = cam != null ? cam.orthographicSize : 10f;
                float halfW = halfH * ((float)Screen.width / Screen.height);

                // Thickness spans just a bit more than one tile row (cell + bleed) so
                // the blast reads as a narrow beam through the word, not a fat slab.
                float thickness = _grid.CellSize * 1.4f;

                // Phase 11i regression fix — per-word beam direction restored.
                // Each detonating word fires its own screen-spanning beam along
                // its own axis; vertical primed words show a vertical sweep,
                // horizontal show a horizontal sweep. The earlier per-burst cap
                // collapsed every word in a chain to the FIRST word's direction,
                // which read wrong on mixed-direction cascades.
                if (WordDropFX.FX_BigBurstFlash)
                {
                    WDLog.Log("[FX] BigBurstFlash: FIRED (primed word)");
                    float screenLength = ScreenExtentAlongAxis(vertical);
                    BigBurstFlash.Instance.Play(wordCenter, screenLength, thickness, vertical, burstTint);
                }
                else { WDLog.Log("[FX] BigBurstFlash: SKIPPED (primed word)"); }

                // HDR sparkle spray — 8-16 tiny stars flying radially outward.
                if (WordDropFX.FX_SparkleSpray && SparkleSpray.Instance != null)
                {
                    WDLog.Log("[FX] SparkleSpray: FIRED (primed word)");
                    float intensity = Mathf.Clamp01((wordLen - 3) / 4f + 0.4f);
                    SparkleSpray.Instance.Play(wordCenter, intensity);
                }
                else { WDLog.Log("[FX] SparkleSpray: SKIPPED (primed word)"); }

                // Phase 11i — ScreenFlash now gated to MELTDOWN-grade events
                // only. Used to fire on every "bigMoment" which fired on
                // chainDepth >= 1, washing the whole screen on routine chains.
                // Fallback signal (no preexisting gate in this scope): chainDepth
                // >= 3 OR longestPrimedWord >= 7 — same threshold MeltdownManager's
                // GetMeltdownTitle uses for its top tier names.
                bool isMeltdown = _pendingBurstChainDepth >= 3 || _pendingBurstLongestWord >= 7;
                if (ScreenFlash.Instance != null && !_screenFlashFiredThisBurst && isMeltdown)
                {
                    float intensity = Mathf.Clamp01((wordLen - 3) / 4f + 0.5f);
                    ScreenFlash.Instance.Play(burstTint, intensity);
                    _screenFlashFiredThisBurst = true;
                }

                // Scatter flare_star sparkles along the full blast line.
                if (WordDropFX.FX_SparkleLine && GameParticles.Instance != null)
                {
                    WDLog.Log("[FX] SparkleLine: FIRED (primed word)");
                    Vector3 lineStart, lineEnd;
                    if (vertical)
                    {
                        lineStart = new Vector3(wordCenter.x, wordCenter.y - halfH * 1.1f, wordCenter.z);
                        lineEnd   = new Vector3(wordCenter.x, wordCenter.y + halfH * 1.1f, wordCenter.z);
                    }
                    else
                    {
                        lineStart = new Vector3(wordCenter.x - halfW * 1.1f, wordCenter.y, wordCenter.z);
                        lineEnd   = new Vector3(wordCenter.x + halfW * 1.1f, wordCenter.y, wordCenter.z);
                    }
                    int sparkleCount = Mathf.Clamp(14 + wordLen * 2, 14, 26);
                    GameParticles.Instance.PlaySparkleLine(lineStart, lineEnd, sparkleCount);
                }
                else { WDLog.Log("[FX] SparkleLine: SKIPPED (primed word)"); }
            }
            // Second pass: the TRIGGER words themselves (the new words the player
            // just formed that ignited the primed cluster). Each gets its own blast
            // so the player's own word lights up too, not only the primed casualties.
            if (_pendingBurstTriggerWords != null)
            {
                foreach (var tw in _pendingBurstTriggerWords)
                {
                    if (tw == null || tw.Cells == null || tw.Cells.Count == 0) continue;

                    int minCol = int.MaxValue, maxCol = int.MinValue;
                    int minRow = int.MaxValue, maxRow = int.MinValue;
                    Vector3 twCenter = Vector3.zero;
                    int twTileCount = 0;
                    foreach (var cell in tw.Cells)
                    {
                        if (cell.x < minCol) minCol = cell.x;
                        if (cell.x > maxCol) maxCol = cell.x;
                        if (cell.y < minRow) minRow = cell.y;
                        if (cell.y > maxRow) maxRow = cell.y;
                        Tile wtTile = _grid.GetTile(cell.x, cell.y);
                        if (wtTile != null) { twCenter += wtTile.transform.position; twTileCount++; }
                    }
                    if (twTileCount == 0) continue;
                    twCenter /= twTileCount;

                    bool twVertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                    int  twWordLen  = twVertical ? (maxRow - minRow + 1) : (maxCol - minCol + 1);

                    Camera cam = _cam != null ? _cam : Camera.main;
                    float halfH = cam != null ? cam.orthographicSize : 10f;
                    float halfW = halfH * ((float)Screen.width / Screen.height);
                    float thickness = _grid.CellSize * 1.4f;

                    if (WordDropFX.FX_BigBurstFlash)
                    {
                        WDLog.Log("[FX] BigBurstFlash: FIRED (trigger word)");
                        float screenLength = ScreenExtentAlongAxis(twVertical);
                        BigBurstFlash.Instance.Play(twCenter, screenLength, thickness, twVertical, burstTint);
                    }
                    else { WDLog.Log("[FX] BigBurstFlash: SKIPPED (trigger word)"); }

                    if (WordDropFX.FX_SparkleSpray && SparkleSpray.Instance != null)
                    {
                        WDLog.Log("[FX] SparkleSpray: FIRED (trigger word)");
                        float intensity = Mathf.Clamp01((twWordLen - 3) / 4f + 0.4f);
                        SparkleSpray.Instance.Play(twCenter, intensity);
                    }
                    else { WDLog.Log("[FX] SparkleSpray: SKIPPED (trigger word)"); }

                    if (WordDropFX.FX_SparkleLine && GameParticles.Instance != null)
                    {
                        WDLog.Log("[FX] SparkleLine: FIRED (trigger word)");
                        Vector3 lineStart, lineEnd;
                        if (twVertical)
                        {
                            lineStart = new Vector3(twCenter.x, twCenter.y - halfH * 1.1f, twCenter.z);
                            lineEnd   = new Vector3(twCenter.x, twCenter.y + halfH * 1.1f, twCenter.z);
                        }
                        else
                        {
                            lineStart = new Vector3(twCenter.x - halfW * 1.1f, twCenter.y, twCenter.z);
                            lineEnd   = new Vector3(twCenter.x + halfW * 1.1f, twCenter.y, twCenter.z);
                        }
                        int twSparkleCount = Mathf.Clamp(14 + twWordLen * 2, 14, 26);
                        GameParticles.Instance.PlaySparkleLine(lineStart, lineEnd, twSparkleCount);
                    }
                    else { WDLog.Log("[FX] SparkleLine: SKIPPED (trigger word)"); }
                }
            }

            // Consume the cache — next Exploding phase won't double-fire stale triggers.
            _pendingBurstTriggers     = null;
            _pendingBurstTriggerWords = null;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // Wild Tiles Phase C — reward injection
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Per-frame check for wild expiry. Clears the wild to a vowel when either
        /// the 3-drop counter (PlayerHand._wildDropsElapsed) or the 20s playable-time
        /// timer elapses. No-op when no wild is in hand.
        /// </summary>
        private void TickWildExpiry()
        {
            // Wild tiles no longer time out — player design decision (2026-04-17).
            // Rationale: wilds are already scarce (chain-reward only). Opportunity
            // cost of using a wild is its natural balance; artificial timeout
            // punishes careful planning. Supporting machinery below is preserved
            // in case we want to re-enable soft-timeout later.
            return;
        }

        /// <summary>
        /// Apply detonation refill rewards (Survival-only): swap-refill,
        /// edit-refill, wild-refill counts from RulesEngine.DoExplode, plus
        /// stone-clear popup. Extracted so primary, swap, and rewrite
        /// detonation paths all use the same logic — previously the rewrite
        /// and both swap detonations dropped these counts on the floor,
        /// meaning blowing up refill tiles in those paths gave no refund
        /// and no wild injection.
        /// </summary>
        private void ApplyDetonationRefillRewards(RulesEngine.StepResult step, Vector3 center, int stoneDying)
        {
            if (!SurvivalManager.IsSurvivalMode || MatchController.Instance == null || step == null) return;
            float popY = 0.8f;
            if (step.SwapRefillCount > 0)
            {
                for (int sr = 0; sr < step.SwapRefillCount; sr++)
                    MatchController.Instance.RefundSwapResource();
                if (BonusPopup.Instance != null)
                    BonusPopup.Instance.Show($"SWAP +{step.SwapRefillCount}", new Color(0.85f, 0.60f, 0.10f, 1f), center + Vector3.up * popY, 1.2f);
                GameAudio.Instance?.PlayScorePowerup();
                popY += 0.5f;
            }
            if (step.EditRefillCount > 0)
            {
                for (int er = 0; er < step.EditRefillCount; er++)
                    MatchController.Instance.RefundSwapCharge(MatchController.PLAYER_HUMAN);
                if (BonusPopup.Instance != null)
                    BonusPopup.Instance.Show($"EDIT +{step.EditRefillCount}", new Color(0.0f, 0.85f, 0.9f, 1f), center + Vector3.up * popY, 1.2f);
                GameAudio.Instance?.PlayScorePowerup();
                popY += 0.5f;
            }
            // Stone clear popup
            if (stoneDying > 0 && BonusPopup.Instance != null)
            {
                BonusPopup.Instance.Show($"STONE x{stoneDying}", new Color(0.6f, 0.55f, 0.65f, 1f), center + Vector3.up * popY, 1.1f);
                popY += 0.5f;
            }
            if (step.WildRefillCount > 0)
            {
                // Phase C: inject one wild into the player's hand via the
                // pending-wild queue. The next DrawSlot fills that slot as a
                // wild. Gated by per-resolution cap and max-1-in-hand invariant.
                TryInjectWildReward(center + Vector3.up * popY);
            }
        }

        /// <summary>
        /// Queue a wild injection as a chain-reward. Gated by the per-resolution cap
        /// and by PlayerHand's max-one-wild invariant. Fires popup + SFX on success.
        /// </summary>
        private void TryInjectWildReward(Vector3 popupWorldPos)
        {
            // Wilds are introduced at the combo/chain level (L3) — suppress the chain-reward wild on the
            // earlier tutorial levels so a learner isn't handed a mechanic they haven't been taught. 2026-06-25.
            if (TutorialLocks.WildRewardLocked) return;
            // Phase 5 mechanic gate. Single choke point for wild Phase C injection.
            // Callers at 3840, 3884 flow through here, so one gate covers all paths.
            if (!LevelController.IsMechanicAllowed("wild")) return;

            if (_wildInjectedThisResolution) return;
            if (MatchController.Instance == null) return;

            // FIRST wild ever earned (unlocks at L7) → play the one-time UNLOCK celebration: "Wild!" modal →
            // on Claim inject the wild → placement hint. Teaches the mechanic once so a fresh player isn't handed
            // an unexplained tile. Persisted, so every wild after this is the normal silent reward below.
            // 2026-07-10 Spencer.
            if (PlayerPrefs.GetInt("wild_unlock_taught", 0) == 0)
            {
                PlayerPrefs.SetInt("wild_unlock_taught", 1);
                PlayerPrefs.Save();
                _wildInjectedThisResolution = true;
                TriggerWildUnlockFlow(); // modal → inject wild on Claim → contextual placement hint
                return;
            }

            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            if (!hand.InjectWildFromChainReward()) return;

            _wildInjectedThisResolution = true;
            // Do NOT anchor _wildInjectedAt here — the wild is only QUEUED in pending
            // state at this point, not visible in hand. TickWildExpiry anchors the
            // clock to the first frame HasWild becomes true (after DrawSlot fills
            // the pending wild into a slot), which gives the player a fair 20s from
            // when they can actually see and plan around the wild.

            if (BonusPopup.Instance != null)
                BonusPopup.Instance.Show("WILD!", WILD_CARD_COLOR, popupWorldPos, 1.4f);
            GameAudio.Instance?.PlayScorePowerup();
        }

        // ── Deal animation (Balatro style) ──────────────────────────────────

        private IEnumerator DealCardsAnimation(System.Action onComplete)
        {
            float baseY = GetCardRowY();
            float offScreenX = _grid != null ? _grid.GridRight + _grid.CellSize * 3f : 8f;

            // Hide all cards and shadows off-screen
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = new Vector3(offScreenX, baseY, -1f);
                if (i < HAND_SIZE && _cardShadows[i] != null)
                {
                    _cardShadows[i].color = Color.clear;
                    _cardShadows[i].transform.position = new Vector3(offScreenX, baseY, 0f);
                }
            }

            // Deal all cards with DOTween — staggered slide-in with overshoot
            // Play the drop-in-hand sound once for the whole deal
            GameAudio.Instance?.PlayCardDropHand();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].SetActive(true); // re-activate (was hidden before deal)
                Vector3 startPos = new Vector3(offScreenX, baseY - 0.3f, -1f);
                Vector3 endPos = new Vector3(GetCardX(i), baseY, -1f);
                _cardObjects[i].transform.position = startPos;
                _cardObjects[i].transform.localScale = GetCardBaseScale() * 0.6f; // start small

                float delay = i * 0.05f;
                int cardIdx = i;
                _cardObjects[i].transform.DOMove(endPos, 0.25f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack, 2.5f)
                    .OnComplete(() =>
                    {
                        // Show shadow when card lands
                        if (cardIdx < HAND_SIZE && _cardShadows[cardIdx] != null)
                        {
                            _cardShadows[cardIdx].color = new Color(0f, 0f, 0f, 0.15f);
                            _cardShadows[cardIdx].transform.position = new Vector3(
                                GetCardX(cardIdx), GetCardRowY() - _cardSize * 0.03f, 0f);
                        }
                    });
                _cardObjects[i].transform.DOScale(GetCardBaseScale(), 0.3f)
                    .SetDelay(delay)
                    .SetEase(DG.Tweening.Ease.OutBack);
            }

            // Wait for all cards to land
            yield return WaitCache.Get(0.3f + HAND_SIZE * 0.05f + 0.05f);
            onComplete?.Invoke();
        }

        private const float CARD_SELECT_RAISE = 0.2f; // was 0.4 — halved
        private const float CARD_ANIM_SPEED = 12f;  // Lerp speed for smooth movement

        /// <summary>Toggle the additive HDR cyan bloom overlay on a hand card — mirrors the board tile's edit
        /// glow (same shader + HDR _Color) so a selected swap card glows with the identical cyan halo. Lazily
        /// builds the overlay child the first time a card needs it. 2026-07-10 Spencer.</summary>
        private void SetCardCyanGlow(int index, bool on)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            var existing = _cardCyanGlowSRs[index];
            var faceSR   = _cardSRs[index];
            int faceOrder = (faceSR != null ? faceSR.sortingOrder : 10);
            if (!on)
            {
                if (existing != null) existing.enabled = false;
                // Restore the letter/points to their normal order (they get bumped above the glow when it's on).
                if (_cardTexts[index]    != null) _cardTexts[index].sortingOrder    = faceOrder + 1;
                if (_cardPtsTexts[index] != null) _cardPtsTexts[index].sortingOrder = faceOrder + 1;
                return;
            }

            GameObject card = _cardObjects[index];
            if (card == null) return;

            // (Re)build if missing or orphaned by a card rebuild (Unity-null or reparented).
            if (existing == null || existing.transform.parent != card.transform)
            {
                if (s_cardBloomMat == null)
                {
                    Shader sh = Shader.Find("WordDrop/AdditiveSprite")
                             ?? Resources.Load<Shader>("Shaders/AdditiveSprite");
                    if (sh != null) s_cardBloomMat = new Material(sh) { name = "CardBloomGlowShared" };
                }
                if (s_cardBloomMPB == null) s_cardBloomMPB = new MaterialPropertyBlock();

                var go = new GameObject("CardCyanGlow");
                go.transform.SetParent(card.transform, false);
                go.transform.localPosition = new Vector3(0f, 0f, -0.04f); // in front of the face, behind the letter
                go.transform.localScale    = Vector3.one;                 // same shape/scale as the card face
                existing = go.AddComponent<SpriteRenderer>();
                existing.sprite = _spriteNormal; // neutral card shape (not glossy) so the additive hue stays clean
                if (s_cardBloomMat != null) existing.sharedMaterial = s_cardBloomMat;
                existing.color = Color.white; // HDR comes from _Color (MPB), not vertex color
                _cardCyanGlowSRs[index] = existing;
            }

            // Glow sits ONE ABOVE the face (like the tile bloom) so the opaque face can't hide it; bump the
            // letter + points TWO above so they stay readable on top of the glow. Match the face's sorting layer.
            if (faceSR != null) existing.sortingLayerID = faceSR.sortingLayerID;
            existing.sortingOrder = faceOrder + 1;
            if (_cardTexts[index]    != null) _cardTexts[index].sortingOrder    = faceOrder + 2;
            if (_cardPtsTexts[index] != null) _cardPtsTexts[index].sortingOrder = faceOrder + 2;

            if (s_cardBloomMPB == null) s_cardBloomMPB = new MaterialPropertyBlock();
            existing.GetPropertyBlock(s_cardBloomMPB);
            // Same HDR cyan the tile edit glow uses (EDIT_GLOW_HDR) so the bloom color matches exactly.
            s_cardBloomMPB.SetColor("_Color", new Color(0.28f, 1.18f, 1.42f, 1f));
            existing.SetPropertyBlock(s_cardBloomMPB);
            existing.enabled = true;
        }

        private void SelectCard(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_hand[index] == '\0') return;

            // Tap same card → deselect
            if (_selectedIndex == index)
            {
                GameAudio.Instance?.PlayLightTick();
                // Reset scale and sort order on deselect
                RestoreAllCardSortOrder();
                if (_cardObjects[index] != null)
                    _cardObjects[index].transform.localScale = GetCardBaseScale();
                HideAllCardShadows();
                _selectedIndex = -1;
                RefreshAllCardVisuals();
                UpdateCardPositions();
                if (ColumnArrowManager.Instance != null)
                    ColumnArrowManager.Instance.ShowArrows(false);
//                 WDLog.Log($"[HandManager] Card {index} deselected");
                return;
            }

            // Reset scale on previously selected card
            if (_selectedIndex >= 0 && _selectedIndex < HAND_SIZE && _cardObjects[_selectedIndex] != null)
                _cardObjects[_selectedIndex].transform.localScale = GetCardBaseScale();

            // Tap different card → deselect old, select new
            _selectedIndex = index;
            // 2026-05-29: pickup SFX silenced — only drop sound plays now.
            // GameAudio.Instance?.PlayTileSelect();
            RefreshAllCardVisuals();
            UpdateCardPositions();

            // Hide tutorial card highlight when player selects a card
            if (TutorialManager.Instance != null && TutorialManager.Instance.IsActive)
                TutorialManager.Instance.HideCardHighlightPublic();

            // Scale up the newly selected card + show shadow + boost sort order
            RestoreAllCardSortOrder();
            if (_cardObjects[index] != null)
                _cardObjects[index].transform.localScale = GetCardBaseScale() * 1.12f;
            BoostCardSortOrder(index);
            HideAllCardShadows(true); // animate old shadow dropping
            ShowCardShadow(index);

//             WDLog.Log($"[HandManager] Card {index} selected: '{_hand[index]}'");

            if (ColumnArrowManager.Instance != null)
                ColumnArrowManager.Instance.ShowArrows(true);
        }

        /// <summary>Smoothly move cards to their target positions. Selected card pops up.</summary>
        private void UpdateCardPositions()
        {
            StartCoroutine(AnimateCardPositions());
        }

        private IEnumerator AnimateCardPositions()
        {
            float elapsed = 0f;
            float duration = 0.08f;

            // Capture start positions
            Vector3[] startPos = new Vector3[HAND_SIZE];
            Vector3[] targetPos = new Vector3[HAND_SIZE];
            float baseY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                startPos[i] = _cardObjects[i].transform.position;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                targetPos[i] = new Vector3(GetCardX(i), targetY, -1f);
            }

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                // Smooth ease out
                float eased = 1f - (1f - t) * (1f - t);

                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    _cardObjects[i].transform.position = Vector3.Lerp(startPos[i], targetPos[i], eased);
                }
                yield return null;
            }

            // Snap to final position and reset scale
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                float targetY = (i == _selectedIndex) ? baseY + CARD_SELECT_RAISE : baseY;
                _cardObjects[i].transform.position = new Vector3(GetCardX(i), targetY, -1f);
                // Reset scale for non-selected cards
                if (i != _selectedIndex)
                    _cardObjects[i].transform.localScale = GetCardBaseScale();
            }
        }

        /// <summary>Explodes a detonation step's tiles, splitting WORD tiles (which flash green) from
        /// big-moment SPLASH collateral (which stays white). Word membership comes from the step's
        /// scored-word + triggered-word cells — reliable even for cascade/rise words that detonate
        /// UNPRIMED (so it can't be done by primed state). 2026-06-23 Spencer.</summary>
        private System.Collections.IEnumerator ExplodeWordsThenSplash(
            RulesEngine.StepResult step, List<Tile> dying, int chainDepth, int wLen)
        {
            if (WordDropFX.Instance == null || dying == null || dying.Count == 0) yield break;
            yield return ConvergeIntoSpecials(dying);   // special-making words merge into their new special first

            var wordCells = new HashSet<Vector2Int>();
            if (step.FormedWordCells != null)
                foreach (var c in step.FormedWordCells)
                    wordCells.Add(c);

            // SINGLE explosion (fixes the double-pop / fake-cascade regression: the old version called
            // PlayExplosion TWICE — once for word tiles, once for splash — which read as two pops).
            // Pre-green ONLY the formed-word tiles with the flat scored tint (no bloom/glow/decay — reverted
            // per Spencer 2026-06-23), then detonate everything in ONE call with wordFlash:false. Splash
            // tiles are left untouched → they pop white.
            if (wordCells.Count > 0)
            {
                for (int i = 0; i < dying.Count; i++)
                {
                    var t = dying[i];
                    if (t == null) continue;
                    if (wordCells.Contains(new Vector2Int(t.Col, t.Row)) && !t.IsShowingScoredSprite)
                        t.SetScoredSprite(true); // pre-green the word tile; splash tiles are left white
                }
            }

            // ── STAGGERED COMBO CRESCENDO ──────────────────────────────────────────
            // When ONE move sets off MULTIPLE connected primed words (the "stack a web of
            // words, then trigger the whole thing" combo), pop them ONE-AT-A-TIME with a
            // growing screen-shake and a climactic finale — the ChainPrototype "PULL
            // TRIGGER" feel — instead of one flat simultaneous blast. The small per-word
            // pops stay under the 8-tile meltdown gate, so only the FINALE (last word +
            // splash collateral) keeps the big meltdown payoff. Single-word detonations
            // fall through to the original one-shot path. FX-ONLY — logical tile removal is
            // unchanged (the caller still does grid.RemoveTiles afterward). 2026-06-25 Spencer.
            // Read the CACHED per-word triggers, not step.Triggers (null on the Exploding step) — see
            // _stagerTriggers. Consume immediately so a wave with no triggers can't reuse a stale list.
            var triggers = _stagerTriggers;
            _stagerTriggers = null;
            int wordCount = 0;
            if (triggers != null)
                for (int i = 0; i < triggers.Count; i++)
                    if (triggers[i].TriggeredCells != null && triggers[i].TriggeredCells.Count > 0)
                        wordCount++;

            if (MULTI_WORD_STAGGER && wordCount >= 2)
            {
                // ── Build the STAGES of the chain (order A — the blast RADIATES FROM THE TRIGGER) ──
                // Stage 0 = the TRIGGER word (e.g. YES): it fired the combo, so it goes FIRST. Then the
                // charged words in trigger order (direct-hit → chain-connected), finale last + biggest.
                // Each stage keeps its FULL word cells (for the FLY-UP, so a SHARED letter like the E in
                // FINE+YES flies in BOTH stages) AND a DISJOINT pop group (so each tile DETONATES once). 2026-07-06.
                var stageCells = new List<List<Vector2Int>>();
                if (_stagerTriggerWords != null)
                    for (int w = 0; w < _stagerTriggerWords.Count; w++)
                        if (_stagerTriggerWords[w]?.Cells != null && _stagerTriggerWords[w].Cells.Count > 0)
                            stageCells.Add(_stagerTriggerWords[w].Cells);
                for (int ti = 0; ti < triggers.Count; ti++)
                    if (triggers[ti].TriggeredCells != null && triggers[ti].TriggeredCells.Count > 0)
                        stageCells.Add(triggers[ti].TriggeredCells);

                var dyingByCell = new Dictionary<Vector2Int, Tile>();
                for (int i = 0; i < dying.Count; i++)
                {
                    var t = dying[i];
                    if (t != null) dyingByCell[new Vector2Int(t.Col, t.Row)] = t;
                }

                // A tile SHARED between two words (e.g. the A/N/T shared by ANT and ANTS) used to fly UP once
                // per word it belongs to → two overlapping sets travelling to the target. Fly each physical
                // tile only in its FRONTMOST (highest-stage, ahead-in-sort) group; earlier groups skip it so no
                // duplicate letters float up behind the last explosion. lastStageForCell records the MAX stage
                // index that owns each cell (later s overwrites). 2026-07-27 Spencer.
                var lastStageForCell = new Dictionary<Vector2Int, int>();
                for (int s = 0; s < stageCells.Count; s++)
                    foreach (var cell in stageCells[s])
                        lastStageForCell[cell] = s;

                var claimed = new HashSet<Vector2Int>();
                var pops = new List<List<Tile>>();      // DISJOINT — each tile detonates once
                var flyGroups = new List<List<Tile>>(); // per WORD — each shared letter flies ONCE, in its frontmost word
                for (int s = 0; s < stageCells.Count; s++)
                {
                    var cells = stageCells[s];
                    var popTiles = new List<Tile>();
                    var flyTiles = new List<Tile>();
                    for (int c = 0; c < cells.Count; c++)
                    {
                        if (!dyingByCell.TryGetValue(cells[c], out var t) || t == null) continue;
                        // FLY only in the frontmost stage that owns this cell — dedupes shared letters.
                        if ((t.WasInScoredWord || t.HasPermanentGlow) && char.IsLetter(t.Letter)
                            && lastStageForCell.TryGetValue(cells[c], out int lastS) && lastS == s)
                            flyTiles.Add(t);
                        if (!claimed.Contains(cells[c])) { claimed.Add(cells[c]); popTiles.Add(t); } // POP: disjoint
                    }
                    pops.Add(popTiles);
                    flyGroups.Add(flyTiles);
                }

                var splash = new List<Tile>();
                for (int i = 0; i < dying.Count; i++)
                {
                    var t = dying[i];
                    if (t != null && !claimed.Contains(new Vector2Int(t.Col, t.Row))) splash.Add(t);
                }
                Coroutine timedPops = StartTimedSpecialPops(splash, claimed);   // special-cleared tiles pop as the blast reaches them
                if (pops.Count > 0) pops[pops.Count - 1].AddRange(splash);
                // GOLD LETTERS CAUGHT IN A BLAST fly too (2026-10-21 Spencer: "if theres gold letter in it those gold letters
                // should fly up and count as added moves"). Splash tiles (a special's row/3x3/disco) belong to no word, so
                // they were never in a fly group; add the gold ones to the finale's group.
                if (GoldFlyUpActive && flyGroups.Count > 0)
                    foreach (var st in splash) if (IsGoldFlyTile(st) && !flyGroups[flyGroups.Count - 1].Contains(st)) flyGroups[flyGroups.Count - 1].Add(st);

                WDLog.Log($"[ChainFX] STAGGER combo(4-part): stages={pops.Count} dying={dying.Count} chain={chainDepth}");

                var flyObj = ObjectiveManager.Instance != null ? ObjectiveManager.Instance.Active : null;
                bool goldFly   = GoldFlyUpActive;
                bool flyUpGoal = HUDManager.Instance != null
                    && (goldFly
                        || ((flyObj is LongWordObjective || flyObj is ComboObjective)
                            && (ObjectiveManager.Instance == null || ObjectiveManager.Instance.CurrentExplosionCounts))); // no fly-up if it won't count (drop on a swap-only level)
                int flyMinLen = goldFly ? 0 : (flyObj is LongWordObjective lwFly ? lwFly.MinLen : 0);

                int popCount = pops.Count;
                for (int g = 0; g < popCount; g++)
                {
                    bool finale = (g == popCount - 1);
                    var groupTiles = pops[g];
                    float t01 = popCount <= 1 ? 1f : (float)g / (popCount - 1); // 0→1 across the chain

                    // Climax only: a crisp anticipation freeze BEFORE the boom (prototype).
                    if (finale)
                        yield return StartCoroutine(WordDropFX.HitStop(0.05f));

                    // POP (detonation visual) — fire-and-forget for the overlapping cascade feel. Fly-up is
                    // SUPPRESSED here; we drive it per WORD just below so a shared letter flies in two stages.
                    Coroutine boom = (groupTiles != null && groupTiles.Count > 0)
                        ? WordDropFX.Instance.PlayExplosion(groupTiles, g, groupTiles.Count, wordFlash: false, cascade: false, suppressFlyUp: true, comboStagger: true)
                        : null;

                    // FLY-UP — this WORD's full letters travel up to the target, ramping per word (finale
                    // biggest). Shared letters (E in FINE+YES) fly in each stage they belong to. 2026-07-06.
                    if (flyUpGoal && g < flyGroups.Count && stageCells[g].Count >= flyMinLen
                        && (!goldFly || flyGroups[g].Exists(IsGoldFlyTile)))
                    {
                        var flyTiles = goldFly ? flyGroups[g].FindAll(IsGoldFlyTile) : flyGroups[g];   // instant pop: gold only
                        // Crescendo: an ACCELERATING ramp (t^1.6 across the chain) so the build still reads, but
                        // GENTLER — the letters don't balloon during chains. ~1.05× (first) → ~1.35× (last).
                        // Toned down from 1.2→1.9 per Spencer 2026-07-27 (was ballooning on combos).
                        float popPeak = 1.05f + 0.30f * Mathf.Pow(t01, 1.6f);
                        // Tell the bank how many landings to split this burst across, and pay a
                        // share per landing — the FX path has always done this, but the COMBO
                        // path passed a null callback and set no count, so nothing paid out and
                        // the score sat in the bank until the 1.6s FLY_FLUSH_TIMEOUT backstop
                        // swept it up. That backstop had quietly become the delivery mechanism
                        // for every combo, which is the delay before the score moved.
                        // 2026-09-10 Spencer.
                        bool goldToMoves = goldFly && GoldToMovesActive;
                        if (!goldToMoves) ScoreManager.Instance?.SetFlyLetterCount(flyTiles.Count);
                        for (int i = 0; i < flyTiles.Count; i++)
                        {
                            bool lastLetter = (i == flyTiles.Count - 1);
                            // Pop the objective icon ONCE for the whole combo — on the FINALE stage's last
                            // letter only. Popping per stage stacks overlapping WildCardPop punch-rotations that
                            // interrupt each other, freezing the target icon slanted. 2026-07-07 Spencer.
                            bool popIcon = finale && lastLetter;
                            // Each flying tile gets its OWN 4-slot sorting band: +4 per LETTER (so a letter
                            // never bleeds onto a neighbour's tile) and +40 per STAGE (later stages fully in
                            // front → CAT covers SOFT covers FINE). 2026-07-06 Spencer.
                            // Sorting reads LEFT-TO-RIGHT within a word: the leftmost letter sits in FRONT
                            // (highest band), each letter behind the one to its left. (count-1-i) reverses the
                            // old right-to-left stacking. +g*40 still keeps later STAGES in front of earlier. 2026-07-27.
                            int sortBase = 200 + g * 40 + (flyTiles.Count - 1 - i) * 4;
                            // The TRIGGER word (stage 0, e.g. YES) keeps the ORIGINAL magenta; only the chained
                            // words (comboStep >= 1) ramp in hue via s_comboFlyRamp. 2026-07-27.
                            HUDManager.Instance.FlyLetterToTarget(flyTiles[i].transform.position, flyTiles[i].Letter,
                                popIcon && !goldToMoves,
                                goldToMoves ? (System.Action)OnGoldLetterLanded : () => ScoreManager.Instance?.AwardOneFlyShare(),
                                i * 0.07f, false, popPeak, sortBase, comboStep: g, armedColor: flyTiles[i].DetonationColor,
                                toMoves: goldToMoves);
                        }
                    }

                    if (finale)
                    {
                        // The climax BOOM — moved here from the pre-windup so the deep boom lands ON the last
                        // pop and the audio BUILDS instead of firing front-loaded. 2026-07-06 Spencer.
                        //
                        // 2026-09-10 Spencer: OCCASIONAL now. Hearing it on the end of every big
                        // explosion made it read as an error tone rather than a reward — a
                        // flourish stops being a flourish when it is guaranteed. The rising
                        // pitched pops carry the climax on their own; this lands on top of them
                        // now and then. Raise BIG_POP_FINALE_CHANCE to bring it back more often.
                        if (UnityEngine.Random.value < BIG_POP_FINALE_CHANCE)
                            GameAudio.Instance?.PlayBigPop();
                        if (groupTiles != null && groupTiles.Count > 0)
                        {
                            // Center the finale FX on the LAST WORD to explode (this stage's word tiles), NOT the
                            // merged pop group — that has scattered splash collateral folded in, which pulls the
                            // center off the actual last word. Fall back to the group if empty. 2026-07-07 Spencer.
                            var centerTiles = (g < flyGroups.Count && flyGroups[g] != null && flyGroups[g].Count > 0)
                                ? flyGroups[g] : groupTiles;
                            Vector3 fc = Vector3.zero; int fn = 0;
                            foreach (var ft in centerTiles) if (ft != null) { fc += ft.transform.position; fn++; }
                            if (fn > 0)
                            {
                                Vector3 center = fc / fn;
                                if (BigBurstFlash.Instance != null) BigBurstFlash.Instance.Play(center, 7f, 1.1f, false, null);
                                // Expanding white light ring blooming out from the finale — the "boom" shockwave
                                // payoff on the final pop of the chain. 2026-07-07 Spencer.
                                float cell = GridManager.Instance != null ? GridManager.Instance.CellSize : 0.8f;
                                ShockwaveRing.Instance?.Play(center, cell * 1.6f, null); // contained boom ring
                            }
                        }
                    }

                    // ── SHAKE REMOVED HERE (2026-09-09 Spencer) ──────────────────────
                    // Measured from the live log: this fired four times per combo, and the
                    // finale was mag 0.300 over 0.28s — 37% of a CELL, running a quarter of a
                    // second. It starts during the explosion but OUTLASTS it, so the tail was
                    // still going once the board had settled: "jiggling at times when nothing
                    // is happening". The three smaller ones ahead of it were being dropped by
                    // the overlap guard anyway, so the "growing punch" had already collapsed
                    // to one huge late hit.
                    // The detonation now owns the shake, ONCE, at the blast, scaled by score
                    // (TapDetonateSequence). Two owners on one event is what made it unfixable
                    // by tuning. Audio/scale escalation for the combo is untouched.

                    // Inter-pop beat — TIGHTENED so the whole clear finishes fast and gravity follows sooner
                    // (shrinks the "letters hang suspended" gap without touching removal/gravity). 2026-07-07 Spencer.
                    float beat = 0.09f;

                    if (finale) { if (boom != null) yield return boom; else yield return new WaitForSecondsRealtime(beat); }
                    else yield return new WaitForSecondsRealtime(beat);
                }
                if (timedPops != null) yield return timedPops;   // every tile gone before the caller removes them
                yield break;
            }

            // ── Single detonation (original one-shot path) ──
            var rest = new List<Tile>(dying);
            Coroutine timed1 = StartTimedSpecialPops(rest, wordCells);
            if (rest.Count > 0) yield return WordDropFX.Instance.PlayExplosion(rest, chainDepth, wLen, wordFlash: false, cascade: false);
            if (timed1 != null) yield return timed1;
        }

        // ── Hand display refresh ──────────────────────────────────────────────────

        /// <summary>
        /// Pulls the current hand from MatchController and refreshes card visuals.
        /// </summary>
        public void RefreshHandFromMatchController()
        {
            if (MatchController.Instance == null) return;

            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand != null)
            {
                // HiddenWord completability safety net: rig a still-needed letter into the next draw if
                // it's gone fully unavailable (board + hand + next, wilds count as backup). Runs BEFORE
                // EnsureCachedNextLetter so the rig lands on the upcoming tile. 2026-06-17 Spencer.
                MaybeRigHiddenLetter();

                // Ensure next tile cache is populated before refreshing visuals
                if (MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN)
                    hand.EnsureCachedNextLetter(MatchController.Instance.Bag);

                char[] slots = hand.GetAllSlots();
                for (int i = 0; i < HAND_SIZE && i < slots.Length; i++)
                    _hand[i] = slots[i];

                RefreshAllCardVisuals();
//                 WDLog.Log($"[HandManager] Hand refreshed: {new string(_hand)}");
            }
        }

        /// <summary>HiddenWord anti-frustration: if a still-needed mystery letter has ZERO availability
        /// anywhere (no copies on the board, none in hand, not the next tile, and no wild to cover it),
        /// force it into the next draw so the word can always be finished. Safety-net only — does nothing
        /// while you still have a path. 2026-06-17 Spencer.</summary>
        private void MaybeRigHiddenLetter()
        {
            var mc = MatchController.Instance;
            if (mc == null || mc.Bag == null || mc.Bag.HasRiggedNext) return; // don't stack rigs
            if (!(ObjectiveManager.Instance?.Active is HiddenWordObjective obj)) return;
            var needed = obj.NeededLetters();
            if (needed.Count == 0) return;

            var hand = mc.GetHand(MatchController.PLAYER_HUMAN);
            char nextCached = hand != null ? hand.CachedNextLetter : '\0';

            // NOTE 2026-06-24 Spencer: we deliberately do NOT bail just because a wild exists.
            // A wild is only a usable substitute if you can also FORM a valid word that places it
            // as the needed letter — on a near-empty board that's often impossible, which softlocked
            // a HiddenWord (the missing letters had zero real copies AND zero way to deploy the wild,
            // so the level couldn't be finished). The per-letter check below only rigs a letter that
            // has zero REAL copies anywhere, so rigging one in is always harmless; the wild stays a
            // bonus on top. (Old code returned early whenever any wild was present.)
            foreach (char L in needed)
            {
                char up = char.ToUpperInvariant(L);
                bool avail = char.ToUpperInvariant(nextCached) == up;
                if (!avail && hand != null)
                    foreach (char hc in hand.GetAllSlots()) if (char.ToUpperInvariant(hc) == up) { avail = true; break; }
                if (!avail && GridManager.Instance != null)
                    for (int c = 0; c < GridManager.COLS && !avail; c++)
                        for (int r = 0; r < GridManager.ROWS && !avail; r++)
                        { var t = GridManager.Instance.GetTile(c, r); if (t != null && !t.IsWild && char.ToUpperInvariant(t.Letter) == up) avail = true; }

                if (!avail)
                {
                    mc.Bag.ForceNextDraw(up);
                    WDLog.Log($"[HiddenSafetyNet] '{up}' unavailable everywhere — rigged into the next draw.");
                    return; // one at a time
                }
            }
        }

        /// <summary>DEBUG: force a wild ('*') into the hand immediately (first non-empty
        /// slot, or slot 0) and refresh so it's visible right away. Bypasses the mechanic
        /// gate / pending-queue. 2026-06-03 Spencer — to test the iridescent wild tile.</summary>
        /// <summary>The full first-wild UNLOCK payoff, decoupled from the trigger: "Unlocked! Wild" modal →
        /// on Claim, inject a wild into hand (juicy entry) → after it lands, pin a CONTEXTUAL placement hint on
        /// the live board. Callable from the FX test button now; wired to the real first-cascade trigger later.
        /// 2026-07-07 Spencer.</summary>
        public void TriggerWildUnlockFlow()
        {
            _wildUnlockFlowActive = true; // hold the level-clear modal until the wild is explained + dealt
            if (UnlockModal.Instance == null) { StartCoroutine(WildRevealAfterClaim()); return; }
            Sprite icon = Resources.Load<Sprite>("Tiles/wild@2x"); // null-safe: modal just shows no icon if missing
            UnlockModal.Instance.Show(
                "Wild",
                "It becomes any letter you need — drop it to complete a word!",
                icon,
                onClaimed: () => StartCoroutine(WildRevealAfterClaim()));
        }

        private System.Collections.IEnumerator WildRevealAfterClaim()
        {
            // Inject the wild with its entry pop, then let the player decide WHERE to use it — no immediate forced
            // hint (that read as the game playing for them). The wild-aware IDLE hint is the fallback if they
            // stall, and the "?" tile's glow already signals it's special. 2026-07-08 Spencer.
            DebugForceWildIntoHand();
            // Keep the unlock flow "active" (so the level-clear modal keeps holding) until the wild's entry pop has
            // finished + a short beat — the player actually sees it dealt before "Well Done!". 2026-07-14 Spencer.
            while (IsWildEntryAnimating) yield return null;
            yield return new WaitForSecondsRealtime(0.5f);
            _wildUnlockFlowActive = false;
        }

        public void DebugForceWildIntoHand()
        {
            if (MatchController.Instance == null) return;
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) return;
            int target = 0;
            char[] slots = hand.GetAllSlots();
            for (int i = 0; i < slots.Length; i++) { if (slots[i] != '\0') { target = i; break; } }
            hand.SetSlot(target, TileBag.WILD_CHAR);
            RefreshHandFromMatchController();
            // Play the juicy wild-entry so the test button shows the real animation.
            PlayWildCardEntry(target);
            WDLog.Log($"[Debug] Forced WILD ('*') into hand slot {target} — drop it to see the wild tile.");
        }

        /// <summary>Juicy entry for an awarded WILD card at <paramref name="slot"/>:
        /// big-overshoot pop + hold (UIAnimations.WildCardPop) + gold-spawn chime +
        /// sparkle. Used by the chain-reward refill and the Force-WILD test button.
        /// 2026-06-04 Spencer.</summary>
        // 2026-06-04 Spencer: true while a wild's ARRIVAL pop is playing, so overlays
        // (StageClearModal) can hold until it finishes. Auto-expiring timestamp — can
        // never get stuck even if the card is destroyed mid-animation.
        private float _wildEntryEndsAt = -1f;
        public bool IsWildEntryAnimating => Time.unscaledTime < _wildEntryEndsAt;

        // True for the WHOLE one-time first-wild UNLOCK flow: from the "Wild!" modal opening, through Claim, until the
        // wild has been dealt into the hand and its entry pop finished. The level-clear (StageClear) modal holds on
        // this so — when the wild unlocks on the SAME move that clears the level — the player actually SEES the wild
        // explained + dealt before "Well Done!" pops. 2026-07-14 Spencer.
        private bool _wildUnlockFlowActive;
        public bool IsWildUnlockFlowActive => _wildUnlockFlowActive;

        private void PlayWildCardEntry(int slot)
        {
            if (slot < 0 || slot >= HAND_SIZE) return;
            GameObject card = _cardObjects[slot];
            if (card == null) return;

            // WildCardPop runs ~1.15s (grow + hold + settle); mark the arrival window.
            _wildEntryEndsAt = Time.unscaledTime + 1.25f;

            // Lift the WHOLE wild card group (face, letter, points, aura, glow) above
            // its neighbours for the duration of the oversized pop — otherwise the
            // 1.75× scale-up clips behind adjacent cards. +20 preserves the card's
            // internal layering; we restore the exact original orders on completion.
            var renderers = card.GetComponentsInChildren<Renderer>(true);
            int[] saved = new int[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                saved[i] = renderers[i].sortingOrder;
                renderers[i].sortingOrder += 20;
            }

            UIAnimations.WildCardPop(card.transform, GetCardBaseScale(), onComplete: () =>
            {
                for (int i = 0; i < renderers.Length; i++)
                    if (renderers[i] != null) renderers[i].sortingOrder = saved[i];
            });
            GameAudio.Instance?.PlayLine();
            // Dark contrast halo UNDER the bright glow/sparks — carves a pocket of
            // darkness out of the bright board so the rainbow actually pops.
            PlayWildDarkHalo(card.transform.position, sortingOrder: 12);
            // Spark burst behind the wild card (VFX_Sparks_2 sheet, sliced into 4).
            PlayWildSparkBurst(card.transform.position, sortingOrder: 13);
        }

        private static Sprite s_wildDarkHaloSprite;

        /// <summary>Soft DARK radial behind the wild's bright glow/sparks — a localized
        /// "contrast halo" that darkens the bright board so the colour reads. Fades in
        /// with the pop, lifts as the card settles. Default (alpha-blend) material so it
        /// darkens rather than adds. 2026-06-04 Spencer.</summary>
        private void PlayWildDarkHalo(Vector3 center, int sortingOrder)
        {
            if (UIAnimations.ReducedMotion) return;
            if (s_wildDarkHaloSprite == null)
                s_wildDarkHaloSprite = MakeSoftRadialSprite();
            if (s_wildDarkHaloSprite == null) return;

            var go = new GameObject("WildDarkHalo");
            go.transform.position = center;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = s_wildDarkHaloSprite; // default alpha-blend material → darkens
            sr.sortingOrder = sortingOrder;
            sr.color = new Color(0.02f, 0.02f, 0.06f, 0f); // cool near-black, alpha 0 (fades in)

            float native = (s_wildDarkHaloSprite.bounds.size.x > 0.0001f) ? s_wildDarkHaloSprite.bounds.size.x : 1f;
            go.transform.localScale = Vector3.one * ((_cardSize * 3.6f) / native); // bigger than the glow so it surrounds it

            GameObject capture = go;
            var seq = DOTween.Sequence();
            seq.Append(sr.DOFade(0.68f, 0.20f).SetEase(Ease.OutQuad)); // darken in with the grow
            seq.AppendInterval(0.35f);                                 // hold through the beat
            seq.Append(sr.DOFade(0f, 0.55f).SetEase(Ease.InQuad));     // lift as the card settles
            seq.OnComplete(() => { if (capture != null) Destroy(capture); });
        }

        /// <summary>Procedural soft radial sprite (white, alpha 1 at center → 0 at edge),
        /// generated once. Used as a tintable soft glow/vignette — no art dependency,
        /// guaranteed circular + soft. 2026-06-04 Spencer.</summary>
        private static Sprite MakeSoftRadialSprite()
        {
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            float c = (size - 1) * 0.5f;
            float maxR = c;
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / maxR;
                    // Full alpha through the core, soft feather only near the outer
                    // edge — a strong, readable vignette that still fades out cleanly.
                    float a = 1f - Mathf.SmoothStep(0.5f, 1f, d);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite[] s_wildSparkSprites;
        private static Material s_wildSparkMat;

        /// <summary>Small additive spark burst (the 4 sparkles from VFX_Sparks_2,
        /// sliced from the 2×2 sheet) behind the awarded wild card. 2026-06-04.</summary>
        private void PlayWildSparkBurst(Vector3 center, int sortingOrder)
        {
            if (UIAnimations.ReducedMotion) { WDLog.Log("[WildSpark] skipped — ReducedMotion"); return; }
            if (s_wildSparkSprites == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Particles/vfx_sparks_2");
                if (tex == null)
                {
                    // PNG imported as a Sprite (default) — grab its underlying texture.
                    Sprite sheet = Resources.Load<Sprite>("Particles/vfx_sparks_2");
                    if (sheet != null) tex = sheet.texture;
                }
                if (tex == null) return;
                int hw = tex.width / 2, hh = tex.height / 2;
                s_wildSparkSprites = new Sprite[4]
                {
                    Sprite.Create(tex, new Rect(0,  hh, hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(hw, hh, hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(0,  0,  hw, hh), new Vector2(0.5f, 0.5f), 100f),
                    Sprite.Create(tex, new Rect(hw, 0,  hw, hh), new Vector2(0.5f, 0.5f), 100f),
                };
            }
            if (s_wildSparkMat == null)
            {
                Shader sh = Shader.Find("WordDrop/AdditiveSprite");
                if (sh == null) sh = Shader.Find("Sprites/Default");
                s_wildSparkMat = new Material(sh);
            }

            const int count = 7;
            for (int i = 0; i < count; i++)
            {
                Sprite spr = s_wildSparkSprites[Random.Range(0, s_wildSparkSprites.Length)];
                var go = new GameObject("WildSpark");
                // Spawn on a ring BEYOND the (enlarged) card edges so the sparks
                // radiate around the tile rather than being swallowed by it.
                float ang = Random.Range(0f, Mathf.PI * 2f);
                Vector3 dirOut = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                float radius = _cardSize * Random.Range(0.85f, 1.35f);
                go.transform.position = center + dirOut * radius;
                go.transform.rotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = spr;
                sr.sharedMaterial = s_wildSparkMat;
                sr.sortingOrder = sortingOrder;
                // Rainbow hue keyed to the spark's angle around the tile, so the burst
                // matches the wild's iridescent rainbow aura sweeping around it.
                Color hue = Color.HSVToRGB(ang / (Mathf.PI * 2f), 0.70f, 1f);
                sr.color = new Color(hue.r, hue.g, hue.b, 0f); // alpha 0 → fades in

                float native = (spr.bounds.size.x > 0.0001f) ? spr.bounds.size.x : 1f;
                float peak = (_cardSize * Random.Range(0.30f, 0.55f)) / native;
                go.transform.localScale = Vector3.one * peak * 0.25f;

                Vector3 drift = dirOut * (_cardSize * 0.35f); // continue radiating outward

                GameObject capture = go;
                Vector3 startPos = go.transform.position;
                var seq = DOTween.Sequence();
                // Quick pop-in, drifting outward...
                seq.Append(go.transform.DOScale(Vector3.one * peak, 0.14f).SetEase(Ease.OutBack, 2f));
                seq.Join(sr.DOFade(1f, 0.08f));
                seq.Join(go.transform.DOMove(startPos + drift, 0.40f).SetEase(Ease.OutCubic));
                seq.Join(go.transform.DORotate(new Vector3(0f, 0f, Random.Range(-40f, 40f)), 0.40f, RotateMode.LocalAxisAdd));
                // ...then immediately shrink + fade so it never hangs suspended.
                seq.Insert(0.15f, sr.DOFade(0f, 0.25f));
                seq.Insert(0.15f, go.transform.DOScale(Vector3.one * peak * 0.5f, 0.25f).SetEase(Ease.InQuad));
                seq.OnComplete(() => { if (capture != null) Destroy(capture); });
            }
        }

        // ── Visual construction ───────────────────────────────────────────────────

        /// <summary>
        /// Creates a rounded-rect tray behind the rack + action area.
        /// Visually groups cards, shuffle, and next into one control cluster.
        /// </summary>
        /// <summary>2026-09-03 swap-pivot: the DROP mechanic's letter hand is being
        /// removed in favour of board swaps. Gate rather than delete so the old game
        /// is one bool away. Suppresses the tray pill AND the letter cards.</summary>
        public static bool HandEnabled = false;

        private void BuildControlTray()
        {
            if (!HandEnabled) return;
            if (_grid == null) return;

            float cardY = GetCardRowY();
            float trayTop = cardY + _grid.CellSize * 0.45f;
            float trayBottom = (cardY - _grid.CellSize * 1.0f) - _grid.CellSize * 0.18f;
            float trayH = trayTop - trayBottom;
            float trayW = (_grid.GridRight - _grid.GridLeft) + _grid.CellSize * 0.3f;

            if (SurvivalManager.IsSurvivalMode)
            {
                // 2026-05-28 (Path A, Phase 2): tray locked to exact PSD pill
                // spec (W=905, H=200, X=137, Y=1966 in canvas 1179×2556).
                // Center the tray on the canvas-derived position; size to
                // match the PSD pill dimensions.
                trayTop    = PsdYToWorld(PSD_PILL_Y);
                trayBottom = PsdYToWorld(PSD_PILL_Y + PSD_PILL_H);
                trayH      = trayTop - trayBottom;
                trayW      = PsdToWorld(PSD_PILL_W);
            }

            int texW = Mathf.Clamp(Mathf.RoundToInt(trayW * 150f), 64, 1024);
            int texH = Mathf.Clamp(Mathf.RoundToInt(trayH * 150f), 64, 1024);
            int radius = Mathf.Min(texW, texH) / 8;

            // Same material family as board — slightly lighter than board outer
            Color trayColor = new Color(0.10f, 0.27f, 0.50f, 1.0f); // deep ocean blue — matches the HUD bar / bench; candy-palette chrome.
            Sprite traySprite = TileRenderer.CreateSolidRoundedRect(texW, texH, radius, trayColor);

            _controlTray = new GameObject("ControlTray");
            _controlTray.transform.SetParent(transform, false);
            _controlTray.transform.position = new Vector3(0f, (trayTop + trayBottom) / 2f, 0.5f);

            SpriteRenderer sr = _controlTray.AddComponent<SpriteRenderer>();
            sr.sprite = traySprite;
            sr.sortingOrder = -1; // behind cards

            float nativeW = texW / 100f;
            float nativeH = texH / 100f;
            _controlTray.transform.localScale = new Vector3(trayW / nativeW, trayH / nativeH, 1f);
        }

        private void BuildCardSprites()
        {
            if (_grid == null) return;
            // 2026-05-28 (Path A, Phase 2): in Survival, card size is locked
            // to PSD card width (149 px) via the world-unit conversion. In
            // other modes (legacy 1v1, level), keep the cell-fraction sizing.
            _cardSize = SurvivalManager.IsSurvivalMode
                ? PsdToWorld(PSD_CARD_W)
                : _grid.CellSize * CARD_SIZE_FRACTION;

            int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
            int radius  = texSize / 7;
            int border  = Mathf.Max(3, texSize / 16);

            // Try loading hand-drawn sprites — the regular card is the baked glossy tile
            // (white_glossy), chosen over the tester PSD in the A/B compare. whiteRef2 is the bounds
            // reference; _spriteNormal is set to the glossy sprite in the build block below. 2026-06-10.
            Sprite whiteRef2 = Resources.Load<Sprite>("Tiles/white5@2x");
            float refBounds2 = (whiteRef2 != null && whiteRef2.bounds.size.x > 0.0001f) ? whiteRef2.bounds.size.x : 1f;
            Sprite loadedNormal = whiteRef2;
            Sprite loadedSelected = Resources.Load<Sprite>("Tiles/green_tile2@2x");
            // 2026-06-04 Spencer: new glossy green (greeny) — trimmed to the tile (100%),
            // full rect at a PPU matched to white5 so it's a true drop-in / same size.
            Texture2D greenyTex2 = Resources.Load<Texture2D>("Tiles/greeny@2x");
            if (greenyTex2 != null && loadedNormal != null && loadedNormal.bounds.size.x > 0.0001f)
            {
                float gppu = greenyTex2.width / loadedNormal.bounds.size.x;
                loadedSelected = Sprite.Create(greenyTex2, new Rect(0, 0, greenyTex2.width, greenyTex2.height),
                                               new Vector2(0.5f, 0.5f), gppu);
            }
            Sprite loadedSwap     = Resources.Load<Sprite>("Tiles/swap_tile");
            Sprite loadedWild     = Resources.Load<Sprite>("Tiles/wild@2x");

            if (loadedNormal != null)
            {
                _spriteNormal       = loadedNormal;
                _spriteSelected     = loadedSelected ?? loadedNormal;
                _spriteSwap         = loadedSwap ?? loadedNormal;
                _spriteSwapSelected = loadedSwap ?? loadedNormal; // swap+selected uses swap sprite
                _spriteWild         = loadedWild ?? loadedNormal;
//                 WDLog.Log("[HandManager] Loaded hand-drawn card sprites from Resources/Tiles.");

                // Baked glassy wild tile + its separate baked drop shadow. They import
                // as plain 1024px textures filling 80% / 91% of frame; we Sprite.Create
                // them with a PPU that puts the 80%-fill TILE at the same world size as
                // white5 (which fills 100%), so the wild matches the rack. Same PPU on
                // the shadow keeps it aligned (rendered at child scale 1.0).
                const float GLOSSY_FILL = 0.80f; // measured: tile fills 80% of the glossy frame
                float whiteBounds = _spriteNormal.bounds.size.x; // white5 fills 100% → this is the target tile size
                Texture2D glossyTex = Resources.Load<Texture2D>("Tiles/white_glossy@2x");
                if (glossyTex != null && whiteBounds > 0.0001f)
                {
                    float glossyPPU = glossyTex.width / (whiteBounds / GLOSSY_FILL);
                    // Build from the TILE region only (the 80% content) so the sprite's
                    // bounds == the tile → true drop-in (bounds-based sizing matches white5).
                    float gm = (1f - GLOSSY_FILL) * 0.5f * glossyTex.width;
                    float gcw = GLOSSY_FILL * glossyTex.width;
                    // Regular card = the baked glossy tile. Assign to _spriteNormal so cards, the next
                    // tile, AND the selected/drag green tint all render on the glossy tile. 2026-06-10.
                    _spriteNormal = Sprite.Create(glossyTex, new Rect(gm, gm, gcw, gcw),
                                                  new Vector2(0.5f, 0.5f), glossyPPU);
                    // Shadow uses the FULL frame at the same PPU → bounds = 1/FILL × tile
                    // (~1.25× the tile), so at child scale 1.0 it feathers just past the edge.
                    Texture2D shadowTex = Resources.Load<Texture2D>("Tiles/test_shadow@2x");
                    if (shadowTex != null)
                        _spriteTileShadow = Sprite.Create(shadowTex, new Rect(0, 0, shadowTex.width, shadowTex.height),
                                                          new Vector2(0.5f, 0.5f), glossyPPU);
                    // A/B shadow variants — same full-frame build at the same PPU so both
                    // line up identically; ApplyShadowABLive (Update) swaps the active one.
                    _shadowSpriteA = MakeShadowSprite(_shadowTexA, glossyPPU);
                    _shadowSpriteB = MakeShadowSprite(_shadowTexB, glossyPPU);
                    WDLog.Log($"[ShadowAB] A='{_shadowTexA}' ({(_shadowSpriteA != null ? "LOADED" : "NULL")}), " +
                              $"B='{_shadowTexB}' ({(_shadowSpriteB != null ? "LOADED" : "NULL")}), useB={_shadowUseB}");
                    // 2026-06-04 Spencer: dedicated wild shadow — same full-frame build at
                    // the same PPU so it aligns exactly like the normal card shadow.
                    Texture2D wildShadowTex = Resources.Load<Texture2D>("Tiles/wild_shadow2@2x");
                    if (wildShadowTex != null)
                        _spriteWildShadow = Sprite.Create(wildShadowTex, new Rect(0, 0, wildShadowTex.width, wildShadowTex.height),
                                                          new Vector2(0.5f, 0.5f), glossyPPU);
                    // 2026-06-04 Spencer: switch the rack WILD card to the baked glossy
                    // wild_swap sprite — same 80%-content build as the glossy white so it
                    // drops in at the exact rack tile size.
                    Texture2D wildSwapTex = Resources.Load<Texture2D>("Tiles/wild_one@2x");
                    if (wildSwapTex != null)
                        _spriteWild = Sprite.Create(wildSwapTex, new Rect(gm, gm, gcw, gcw),
                                                    new Vector2(0.5f, 0.5f), glossyPPU);
                }
                _spriteGlossy = null; // cards + next read _spriteNormal (the glossy tile) directly
            }

            // Wild halo — loaded once, reused across all hand slots.
            // 2026-06-04 Spencer: trying the denser/softer VFX_Rays (was vfx_rays_sharp,
            // which read spiky/sticker-ish). Swap to "vfx_rays_2" or back to
            // "vfx_rays_sharp" here to compare.
            Texture2D haloTex = Resources.Load<Texture2D>("Particles/vfx_rays");
            if (haloTex == null) haloTex = Resources.Load<Texture2D>("Particles/vfx_rays_sharp"); // fallback
            if (haloTex != null)
            {
                _spriteWildHalo = Sprite.Create(
                    haloTex, new Rect(0, 0, haloTex.width, haloTex.height),
                    new Vector2(0.5f, 0.5f), 100f);
                // Rainbow aura material (2026-06-03 Spencer) — matches the board wild.
                Shader addShader = Shader.Find("WordDrop/IridescentAura")
                                ?? Shader.Find("WordDrop/AdditiveSprite")
                                ?? Shader.Find("Sprites/Default");
                _wildHaloMaterial = new Material(addShader);
            }
            else
            {
                // Fallback to procedural
                _spriteNormal      = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_NORMAL, border);
                _spriteSelected    = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SELECT, border + 2);
                _spriteSwap        = TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SWAP, border + 1);
                _spriteSwapSelected= TileRenderer.CreateRoundedRect(texSize, texSize, radius,
                                        CARD_FILL_NORMAL, CARD_BORDER_SWAP_SEL, border + 2);
//                 WDLog.Log("[HandManager] Fallback: procedural card sprites.");
            }
        }

        private void BuildCardObjects()
        {
            if (!HandEnabled) return;
            BuildCardObjectsInner();
        }

        private void BuildCardObjectsInner()
        {
            if (_grid == null || _spriteNormal == null) return;

            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] != null) Destroy(_cardObjects[i]);
                if (_cardHalos[i] != null) Destroy(_cardHalos[i]);
                _cardHalos[i] = null;
                if (_cardIrid[i] != null) Destroy(_cardIrid[i]);
                _cardIrid[i] = null;
                _cardHaloSRs[i] = null;
            }

            float cardY = GetCardRowY();

            for (int i = 0; i < HAND_SIZE; i++)
            {
                float cardX = GetCardX(i);

                GameObject cardGO = new GameObject($"HandCard_{i}");
                cardGO.transform.SetParent(transform, false);
                cardGO.transform.position = new Vector3(cardX, cardY, -1f);

                SpriteRenderer sr = cardGO.AddComponent<SpriteRenderer>();
                sr.sprite       = _spriteNormal;
                sr.sortingOrder = 10;
                // Apply lit material for URP 2D lighting
                Material litMat = Resources.Load<Material>("SpriteLit2D");
                if (litMat != null) sr.sharedMaterial = litMat;

                // Use actual sprite bounds for sizing
                float nativeSize = (sr.sprite != null && sr.sprite.bounds.size.x > 0)
                    ? sr.sprite.bounds.size.x
                    : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
                float scale      = _cardSize / nativeSize;
                cardGO.transform.localScale = new Vector3(scale, scale, 1f);

                float invScale = 1f / Mathf.Max(scale, 0.01f);

                // Letter text — TMP, matches board tiles exactly (true center)
                GameObject textGO = new GameObject("CardLetter");
                textGO.transform.SetParent(cardGO.transform, false);
                textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f); // 2026-06-04 Spencer: nudge to re-center Avenir

                var tm = textGO.AddComponent<TMPro.TextMeshPro>();
                TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
                if (tileFont != null) tm.font = tileFont;
                tm.text          = "?";
                tm.fontSize      = 6.3f; // 2026-06-05 Spencer: −10% (was 7.0)
                tm.fontStyle     = TMPro.FontStyles.Bold;
                tm.color         = CARD_TEXT_COLOR;
                tm.alignment     = TMPro.TextAlignmentOptions.Midline; // Midline so a single capital sits visually centered, not high
                tm.sortingOrder  = 11;
                tm.rectTransform.sizeDelta = new Vector2(2f, 2f);
                tm.enableWordWrapping = false;
                tm.overflowMode  = TMPro.TextOverflowModes.Overflow;
                // 2026-06-03 Spencer: effects REMOVED to match the board letters —
                // raw Clarity, no underlay shadow, no face dilate.
                var cardLetterMat = tm.fontMaterial;
                cardLetterMat.DisableKeyword("UNDERLAY_ON");
                cardLetterMat.SetFloat("_FaceDilate", 0.05f); // very slight bolden (no shadow); old was 0.27
                tm.UpdateMeshPadding();
                textGO.transform.localScale = new Vector3(invScale, invScale, 1f);

                // Point value — TMP, matches board tiles exactly
                GameObject ptsGO = new GameObject("CardPoints");
                ptsGO.transform.SetParent(cardGO.transform, false);
                ptsGO.transform.localPosition = new Vector3(nativeSize * 0.25f, -nativeSize * 0.25f, -0.1f);

                var ptsTm = ptsGO.AddComponent<TMPro.TextMeshPro>();
                if (tileFont != null) ptsTm.font = tileFont;
                ptsTm.text          = "";
                ptsTm.fontSize      = 2.8f;
                ptsTm.fontStyle     = TMPro.FontStyles.Bold;
                ptsTm.color         = CARD_PTS_COLOR;
                ptsTm.alignment     = TMPro.TextAlignmentOptions.Center;
                ptsTm.sortingOrder  = 11;
                ptsTm.rectTransform.sizeDelta = new Vector2(0.8f, 0.6f);
                ptsTm.enableWordWrapping = false;
                ptsTm.overflowMode  = TMPro.TextOverflowModes.Overflow;
                ptsGO.transform.localScale = new Vector3(invScale, invScale, 1f);

                MeshRenderer ptsMr = ptsGO.GetComponent<MeshRenderer>();
                if (ptsMr != null) ptsMr.sortingOrder = 11;

                // Hand card shadows disabled — caused too many z-order/timing issues
                _cardShadows[i] = null;

                _cardObjects[i]  = cardGO;
                _cardSRs[i]      = sr;
                _cardTexts[i]    = tm;
                _cardPtsTexts[i] = ptsTm;

                // Wild halo child — sits BEHIND the card (lower sortingOrder), scaled
                // ~1.8× wider than the card so the glow spills past the card edges.
                // Starts disabled; RefreshCardVisual enables it when the slot is wild.
                if (_spriteWildHalo != null)
                {
                    GameObject haloGO = new GameObject("HandCardHalo");
                    haloGO.transform.SetParent(cardGO.transform, false);
                    haloGO.transform.localPosition = new Vector3(0f, 0f, 0.3f); // slightly behind card
                    var haloSR = haloGO.AddComponent<SpriteRenderer>();
                    haloSR.sprite = _spriteWildHalo;
                    if (_wildHaloMaterial != null) haloSR.sharedMaterial = _wildHaloMaterial;
                    haloSR.sortingOrder = 7; // lowered to make room for contact shadow (8); card is 10
                    // Halo occupies roughly 1.25× card footprint — tight enough to
                    // read as "this card glows" without dominating the hand row.
                    float haloNativeSize = (haloSR.sprite != null && haloSR.sprite.bounds.size.x > 0)
                        ? haloSR.sprite.bounds.size.x : 1f;
                    float haloScale = (_cardSize * 1.85f) / (haloNativeSize * scale); // 2026-06-04: rays bumped up so they spill past the glow
                    haloGO.transform.localScale = new Vector3(haloScale, haloScale, 1f);
                    // Animator — rotates + pulses so the halo reads as alive, not a sticker.
                    haloGO.AddComponent<WildHaloAnimator>();

                    // Second aura layer — soft VFX_Glow radial behind the rays for a
                    // fuller, rounder glow. Child of the halo so it toggles + cleans
                    // up with it. 2026-06-03 Spencer.
                    if (s_cardWildGlow == null)
                    {
                        Texture2D gtex = Resources.Load<Texture2D>("Particles/vfx_glow");
                        if (gtex != null)
                            s_cardWildGlow = Sprite.Create(gtex, new Rect(0, 0, gtex.width, gtex.height),
                                                           new Vector2(0.5f, 0.5f), 100f);
                    }
                    if (s_cardWildGlow != null)
                    {
                        var glowGO = new GameObject("HandCardGlow");
                        glowGO.transform.SetParent(haloGO.transform, false);
                        glowGO.transform.localPosition = new Vector3(0f, 0f, 0.05f);
                        var glowSR = glowGO.AddComponent<SpriteRenderer>();
                        glowSR.sprite = s_cardWildGlow;
                        if (_wildHaloMaterial != null) glowSR.sharedMaterial = _wildHaloMaterial;
                        glowSR.sortingOrder = 6; // behind the rays (7)
                        glowSR.color = new Color(1f, 1f, 1f, 1.0f);
                        float glowNative = (glowSR.sprite != null && glowSR.sprite.bounds.size.x > 0)
                            ? glowSR.sprite.bounds.size.x : 1f;
                        // Bigger glow so it leads over the rays (matches the board look).
                        glowGO.transform.localScale = Vector3.one * ((haloNativeSize / glowNative) * 0.97f); // 2026-06-04: glow back up a bit, still under the rays so the tips peek out
                    }

                    // Dark contrast backing BEHIND the rainbow aura so it pops against
                    // the tray + neighbours even at rest (bright-on-bright reads muddy;
                    // a dark pocket fixes it). Soft radial, normal alpha-blend (darkens,
                    // doesn't add). Child of the halo → toggles/swaps/cleans up with it.
                    if (s_wildDarkHaloSprite == null) s_wildDarkHaloSprite = MakeSoftRadialSprite();
                    if (s_wildDarkHaloSprite != null)
                    {
                        var darkGO = new GameObject("HandCardDarkBacking");
                        darkGO.transform.SetParent(haloGO.transform, false);
                        darkGO.transform.localPosition = new Vector3(0f, 0f, 0.12f); // behind glow + rays
                        var darkSR = darkGO.AddComponent<SpriteRenderer>();
                        darkSR.sprite = s_wildDarkHaloSprite;
                        darkSR.sortingOrder = 5; // behind glow (6) + rays (7)
                        darkSR.color = new Color(0.02f, 0.02f, 0.06f, 0.45f); // cool near-black
                        float darkNative = (darkSR.sprite.bounds.size.x > 0.0001f) ? darkSR.sprite.bounds.size.x : 1f;
                        // World size ~2.2× card (halo is 1.40×): scale = (2.2/1.40) · halo/dark native.
                        darkGO.transform.localScale = Vector3.one * ((2.2f / 1.40f) * (haloNativeSize / darkNative));
                    }

                    haloGO.SetActive(false);
                    _cardHalos[i]   = haloGO;
                    _cardHaloSRs[i] = haloSR;
                }

                // Contact / separation shadow — a dark copy of the tile silhouette,
                // slightly larger, layered between the aura (≤7) and the card face (10)
                // so the tile edge stays crisp against the bright glow (the #118 look).
                // 2026-06-04 Spencer.
                if (_spriteNormal != null)
                {
                    var csGO = new GameObject("HandCardContactShadow");
                    csGO.transform.SetParent(cardGO.transform, false);
                    csGO.transform.localPosition = new Vector3(0f, 0f, 0.04f); // just behind the face
                    var csSR = csGO.AddComponent<SpriteRenderer>();
                    csSR.sortingOrder = 8;                             // above aura (≤7), below card face (10)
                    if (_spriteTileShadow != null)
                    {
                        // Baked PS drop shadow — same PPU as the glossy tile, so child
                        // scale 1.0 lines it up exactly; darkness + feather are baked in.
                        csSR.sprite = _spriteTileShadow;
                        csSR.color = Color.white;
                        // MULTIPLY blend so it darkens the tray like the PS Multiply layer.
                        if (s_shadowMultiplyMat == null)
                        {
                            Shader msh = Shader.Find("WordDrop/MultiplySprite");
                            if (msh != null)
                            {
                                s_shadowMultiplyMat = new Material(msh);
                                s_shadowMultiplyMat.SetFloat("_Strength", 0.40f); // 2026-06-05 Spencer: lighter shadow (was 0.48)
                            }
                        }
                        if (s_shadowMultiplyMat != null) csSR.sharedMaterial = s_shadowMultiplyMat;
                        csGO.transform.localScale = Vector3.one;
                    }
                    else
                    {
                        // Fallback: procedural soft shadow.
                        Sprite softSpr = GetSoftShadowSprite();
                        csSR.sprite = softSpr;
                        csSR.color = new Color(0.02f, 0.02f, 0.06f, 0.60f);
                        float cardNative = _spriteNormal.bounds.size.x > 0.0001f ? _spriteNormal.bounds.size.x : 1f;
                        float softNative = (softSpr != null && softSpr.bounds.size.x > 0.0001f) ? softSpr.bounds.size.x : 1f;
                        csGO.transform.localScale = Vector3.one * (1.30f * (cardNative / softNative));
                    }
                    csGO.SetActive(false);
                    _cardContactShadow[i] = csGO;
                }

                // Wild iridescent overlay — fills the card with the holographic
                // shader when the slot is wild (matches the board tile). White card
                // shape as the mask. Starts disabled; RefreshCardVisual toggles it.
                {
                    if (s_iridCardMaterial == null)
                    {
                        Shader ish = Shader.Find("WordDrop/IridescentTile")
                                  ?? Shader.Find("Sprites/Default");
                        s_iridCardMaterial = new Material(ish);
                    }
                    GameObject iridGO = new GameObject("HandCardIrid");
                    iridGO.transform.SetParent(cardGO.transform, false);
                    iridGO.transform.localPosition = new Vector3(0f, 0f, -0.05f); // in front of the card face
                    var iridSR = iridGO.AddComponent<SpriteRenderer>();
                    iridSR.sprite = _spriteNormal;                 // white card shape = the mask
                    if (s_iridCardMaterial != null) iridSR.sharedMaterial = s_iridCardMaterial;
                    iridSR.sortingOrder = 10;                      // over the card face (10), under text (11)
                    iridGO.transform.localScale = Vector3.one;     // matches the card face
                    iridGO.SetActive(false);
                    _cardIrid[i] = iridGO;
                }

                // 2026-05-29: cards spawn INACTIVE so they don't flash at
                // full size between BuildCardObjects and InitialiseHand's
                // pop-in. InitialiseHand calls SetActive(true) right before
                // scaling to 0 and running StaggeredHandPopIn. Without this,
                // the cards were visible at rest scale for the brief gap
                // between rebuild and the deal animation.
                cardGO.SetActive(false);
            }

//             WDLog.Log($"[HandManager] Built {HAND_SIZE} card objects at Y={cardY:F2}");
        }

        // ── Card shadow helpers ───────────────────────────────────────────────────

        private Coroutine _shadowAnimCoroutine;

        // ═════════════════════════════════════════════════════════════════════════
        // Tap-to-swap mode — entered via BoosterHUDSlot's TileBag tap. Dim scrim
        // sits on a separate Canvas (owned by BoosterHUDSlot); HandManager's job
        // here is just to (a) start a subtle pulse on the hand cards and (b)
        // intercept the next card tap to route it through MatchController.UseSwap.
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>Start the swap-selection mode. Called by BoosterHUDSlot
        /// after it shows the scrim + X. Pulses each hand card so it's clear
        /// they're tappable. Update()'s top-of-frame check intercepts the next
        /// card tap and routes it to PerformTapToSwap.</summary>
        public void EnterTapToSwapMode()
        {
            if (TapToSwapModeActive) return;
            TapToSwapModeActive = true;

            // Kill any prior pulse tweens just in case.
            for (int i = 0; i < _tapToSwapPulseTweens.Count; i++)
                _tapToSwapPulseTweens[i]?.Kill();
            _tapToSwapPulseTweens.Clear();

            // Per-card subtle scale pulse (1.0 ⇄ 1.06 @ 0.55s, ping-pong).
            // Tween stored so we can kill it cleanly on exit. DOKill first so
            // a stale tween (snap-back, hover) doesn't fight the pulse — cards
            // should be in their resting state when this mode enters.
            Vector3 baseScale = GetCardBaseScale();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                var card = _cardObjects[i];
                if (card == null) continue;
                card.transform.DOKill();
                card.transform.localScale = baseScale;
                var pulse = card.transform
                    .DOScale(baseScale * 1.06f, 0.55f)
                    .SetEase(Ease.InOutSine)
                    .SetLoops(-1, LoopType.Yoyo);
                _tapToSwapPulseTweens.Add(pulse);
            }
        }

        /// <summary>Exit tap-to-swap mode. Kills pulse tweens and restores
        /// the card scale. Called by BoosterHUDSlot on cancel (X) or by
        /// PerformTapToSwap after a successful swap.</summary>
        public void ExitTapToSwapMode()
        {
            if (!TapToSwapModeActive) return;
            TapToSwapModeActive = false;

            // Kill ONLY the pulse tweens we created — using stored refs so we
            // don't trample the punch-scale tween PerformTapToSwap may have
            // started on the same transform.
            for (int i = 0; i < _tapToSwapPulseTweens.Count; i++)
                _tapToSwapPulseTweens[i]?.Kill();
            _tapToSwapPulseTweens.Clear();

            // Restore card scales to base — the ping-pong tween may have left
            // them mid-stride. The punch tween, if active, will overwrite this
            // immediately on its next frame so the punch isn't lost.
            Vector3 baseScale = GetCardBaseScale();
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.localScale = baseScale;
            }
        }

        /// <summary>Execute the swap on the tapped card and exit the mode.
        /// Dissolves the card in place (matches SwapViaBagDrop's in-place
        /// dissolve), executes the data swap, then pops in the replacement.</summary>
        private void PerformTapToSwap(int cardIndex)
        {
            if (cardIndex < 0 || cardIndex >= HAND_SIZE) return;
            if (MatchController.Instance == null) return;
            if (MatchController.Instance.GetSwapsRemaining(MatchController.PLAYER_HUMAN) <= 0)
            {
                BoosterHUDSlot.Instance?.ExitTileBagSwapMode();
                return;
            }
            if (_hand[cardIndex] == '\0') return;
            StartCoroutine(TapToSwapSequence(cardIndex));
        }

        private IEnumerator TapToSwapSequence(int cardIndex)
        {
            // Stop the pulse on THIS card before dissolving so the scale tween
            // doesn't fight the dissolve shrink.
            GameObject cardGO = _cardObjects[cardIndex];
            if (cardGO != null) cardGO.transform.DOKill();

            SpriteRenderer cardSR = cardGO != null ? cardGO.GetComponent<SpriteRenderer>() : null;
            yield return StartCoroutine(DissolveCardInPlace(cardGO, cardSR));

            bool success = MatchController.Instance != null
                && MatchController.Instance.UseSwap(cardIndex);

            // EXIT MODE FIRST so ExitTapToSwapMode's "restore all card scales
            // to baseScale" pass runs BEFORE we kick off the NewTilePop on the
            // replacement card. Otherwise the exit-cleanup snaps the new card
            // to baseScale immediately and the pop animation never plays.
            BoosterHUDSlot.Instance?.ExitTileBagSwapMode();

            if (success)
            {
                RestoreAllCardSortOrder();
                _selectedIndex = -1;
                RefreshHandFromMatchController();

                // Pop the replacement in. Same NewTilePop curve as SwapViaBagDrop
                // so the tap-to-swap path feels identical to a fresh single-tile
                // deal (or the row-rise tile arrival).
                if (_cardObjects[cardIndex] != null)
                {
                    _cardObjects[cardIndex].transform.localScale = Vector3.zero;
                    if (cardSR != null) cardSR.color = Color.white;
                    Vector3 restPos = new Vector3(GetCardX(cardIndex), GetCardRowY(), -1f);
                    _cardObjects[cardIndex].transform.position = restPos;
                    UIAnimations.NewTilePop(
                        _cardObjects[cardIndex].transform,
                        GetCardBaseScale(),
                        speedMult: HAND_POP_SPEED_MULT);
                    GameAudio.Instance?.PlayTileArrival();
                }
                RefreshAllCardVisuals();
            }
        }

        /// <summary>Shared dissolve animation: particles at the card's current
        /// world position, poof sound, light haptic, then shrink-and-fade in
        /// place. Used by both SwapViaBagDrop (drag-onto-bag) and
        /// TapToSwapSequence (tap-to-swap mode) so the visual is consistent.</summary>
        private IEnumerator DissolveCardInPlace(GameObject cardGO, SpriteRenderer cardSR)
        {
            if (cardGO == null) yield break;

            Vector3 cardPos = cardGO.transform.position;
            GameParticles.Instance?.PlayDetonation(cardPos, 0);
            GameAudio.Instance?.PlayPoofExplosion();
            HapticsManager.Light();

            cardGO.transform.DOScale(Vector3.zero, 0.15f).SetEase(DG.Tweening.Ease.InBack);
            if (cardSR != null)
            {
                Color startCol = cardSR.color;
                float fadeElapsed = 0f;
                while (fadeElapsed < 0.15f)
                {
                    fadeElapsed += Time.deltaTime;
                    float t = Mathf.Clamp01(fadeElapsed / 0.15f);
                    cardSR.color = new Color(startCol.r, startCol.g, startCol.b, 1f - t);
                    yield return null;
                }
            }
            else
            {
                yield return WaitCache.Get(0.15f);
            }
        }

        /// <summary>Boost sorting order on a card so it renders above all others.</summary>
        private void BoostCardSortOrder(int index)
        {
            if (index < 0 || index >= HAND_SIZE || _cardObjects[index] == null) return;
            // 45/46 (was 20/21): a dragged card must sort ABOVE the tutorial hand-spotlight bump (base+30 ≈ 40),
            // or on tutorial levels the OTHER spotlighted cards render in front of the one you're carrying (it
            // looked "dimmed"). Still below the HUD (50). 2026-07-13 Spencer.
            var sr = _cardObjects[index].GetComponent<SpriteRenderer>();
            if (sr != null) sr.sortingOrder = 45;
            // Boost all child renderers (text, points)
            foreach (var child in _cardObjects[index].GetComponentsInChildren<MeshRenderer>())
                child.sortingOrder = 46;
            foreach (var child in _cardObjects[index].GetComponentsInChildren<TMPro.TextMeshPro>())
                child.sortingOrder = 46;
        }

        /// <summary>Restore sorting order on all cards to default.</summary>
        private void RestoreAllCardSortOrder()
        {
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                var sr = _cardObjects[i].GetComponent<SpriteRenderer>();
                if (sr != null) sr.sortingOrder = 10;
                foreach (var child in _cardObjects[i].GetComponentsInChildren<MeshRenderer>())
                    child.sortingOrder = 11;
                foreach (var child in _cardObjects[i].GetComponentsInChildren<TMPro.TextMeshPro>())
                    child.sortingOrder = 11;
            }
        }

        private void ShowCardShadow(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_cardShadows[index] == null) return;

            float cardX = GetCardX(index);
            float restY = GetCardRowY();

            float centerX = 0f;
            float maxHOffset = _cardSize * 0.1f;
            float hOffset = -Mathf.Sign(cardX - centerX) * Mathf.Clamp01(Mathf.Abs(cardX - centerX) / 3f) * maxHOffset;

            _cardShadows[index].transform.position = new Vector3(cardX + hOffset, restY - _cardSize * 0.03f, 0f);

            // Shadow starts subtle, gets slightly bigger and darker as card lifts
            Vector3 cardScale = GetCardBaseScale();
            float shadowScaleMult = 1.08f; // smaller spread than before
            _cardShadows[index].transform.localScale = cardScale * shadowScaleMult * 0.95f;
            _cardShadows[index].color = new Color(0f, 0f, 0f, 0.15f); // start from rest opacity

            if (_shadowAnimCoroutine != null) StopCoroutine(_shadowAnimCoroutine);
            _shadowAnimCoroutine = StartCoroutine(AnimateShadowLift(
                _cardShadows[index], cardScale * shadowScaleMult));
        }

        private IEnumerator AnimateShadowLift(SpriteRenderer shadow, Vector3 targetScale)
        {
            if (shadow == null) yield break;

            float duration = 0.12f;
            float elapsed = 0f;
            Vector3 startScale = targetScale * 0.92f;
            float targetAlpha = 0.35f; // semi-transparent shadow

            while (elapsed < duration && shadow != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t * (3f - 2f * t);

                shadow.transform.localScale = Vector3.Lerp(startScale, targetScale, eased);
                shadow.color = new Color(0f, 0f, 0f, Mathf.Lerp(0f, targetAlpha, eased));

                yield return null;
            }

            if (shadow != null)
            {
                shadow.transform.localScale = targetScale;
                shadow.color = new Color(0f, 0f, 0f, targetAlpha);
            }
        }

        private void HideAllCardShadows(bool animate = false)
        {
            if (_shadowAnimCoroutine != null) { StopCoroutine(_shadowAnimCoroutine); _shadowAnimCoroutine = null; }
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardShadows[i] == null) continue;
                if (animate && _cardShadows[i].color.a > 0.01f)
                {
                    // Fire-and-forget drop animation
                    StartCoroutine(AnimateShadowDrop(_cardShadows[i], GetCardBaseScale()));
                }
                else
                {
                    // Only show rest shadow if card is active and visible
                    bool cardActive = _cardObjects[i] != null && _cardObjects[i].activeSelf;
                    if (cardActive)
                    {
                        _cardShadows[i].color = new Color(0f, 0f, 0f, 0.15f);
                        _cardShadows[i].transform.localScale = GetCardBaseScale();
                        _cardShadows[i].transform.position = new Vector3(
                            GetCardX(i), GetCardRowY() - _cardSize * 0.03f, 0f);
                    }
                    else
                    {
                        _cardShadows[i].color = Color.clear;
                    }
                }
            }
        }

        private IEnumerator AnimateShadowDrop(SpriteRenderer shadow, Vector3 fullScale)
        {
            if (shadow == null) yield break;

            float duration = 0.1f;
            float elapsed = 0f;
            Vector3 startScale = shadow.transform.localScale;
            float startAlpha = shadow.color.a;

            while (elapsed < duration && shadow != null)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t;

                // Scale back up to full + fade to full opacity briefly, then disappear
                shadow.transform.localScale = Vector3.Lerp(startScale, fullScale, eased);
                shadow.color = new Color(0f, 0f, 0.02f, Mathf.Lerp(startAlpha, 0f, eased));

                yield return null;
            }

            if (shadow != null)
            {
                shadow.color = Color.clear;
                shadow.transform.localScale = fullScale;
            }
        }

        /// <summary>
        /// Called every frame — dynamically repositions the selected card's shadow
        /// based on the card's current world position relative to screen center.
        /// </summary>
        private void UpdateSelectedCardShadow()
        {
            if (_selectedIndex < 0 || _selectedIndex >= HAND_SIZE) return;
            if (_cardShadows[_selectedIndex] == null) return;
            if (_cardShadows[_selectedIndex].color.a < 0.01f) return; // not visible

            // Get the card's current world X (may be different from rest if dragging)
            float cardX = _cardObjects[_selectedIndex] != null
                ? _cardObjects[_selectedIndex].transform.position.x
                : GetCardX(_selectedIndex);

            float restY = GetCardRowY();
            float centerX = 0f;
            float maxHOffset = _cardSize * 0.1f;
            float hOffset = -Mathf.Sign(cardX - centerX) * Mathf.Clamp01(Mathf.Abs(cardX - centerX) / 3f) * maxHOffset;

            _cardShadows[_selectedIndex].transform.position = new Vector3(
                cardX + hOffset, restY - _cardSize * 0.05f, 0f);
        }

        // ── Visual update helpers ─────────────────────────────────────────────────

        private void RefreshAllCardVisuals()
        {
            for (int i = 0; i < HAND_SIZE; i++)
                RefreshCardVisual(i);
            // Only reset shadows if not dragging — drag handles its own shadow
            if (!_isDragging)
            {
                HideAllCardShadows();
                if (_selectedIndex >= 0)
                    ShowCardShadow(_selectedIndex);
            }
            UpdateNextTilePreview();
        }

        /// <summary>
        /// Returns the sprite this slot should show when at rest (no drag).
        /// Wild slots use the wild sprite; everything else falls back to normal.
        /// </summary>
        private Sprite GetSlotRestSprite(int index)
        {
            // Iridescent wild rests on the WHITE base (overlay/"?"/aura are children).
            if (IsWildSlotChecked(index))
                return Tile.IridescentWild ? _spriteNormal : (_spriteWild ?? _spriteNormal);
            return _spriteNormal;
        }

        private bool IsWildSlotChecked(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return false;
            if (MatchController.Instance == null) return false;
            var pHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            return pHand != null && pHand.IsWildSlot(index);
        }

        private void RefreshCardVisual(int index)
        {
            if (index < 0 || index >= HAND_SIZE) return;
            if (_cardSRs[index] == null || _cardTexts[index] == null) return;

            bool isSelected = (index == _selectedIndex);
            char letter     = _hand[index];
            bool isEmpty    = (letter == '\0');

            // Phase C: wild flag lives on PlayerHand. Cheap lookup each refresh.
            bool isWild = false;
            if (MatchController.Instance != null)
            {
                PlayerHand pHand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
                if (pHand != null) isWild = pHand.IsWildSlot(index);
            }

            // Toggle the wild halo child on this card — glows out from behind so
            // the wild card reads as "special" even at a glance.
            if (_cardHalos[index] != null)
                _cardHalos[index].SetActive(isWild);
            // 2026-06-04 Spencer: baked drop shadow under each card. Wild cards use the
            // dedicated wild_shadow sprite + a dedicated material (so its strength is
            // tunable in the Inspector independent of normal cards). See Update() for
            // the live re-apply of offset/scale/strength.
            if (_cardContactShadow[index] != null)
            {
                var csSR = _cardContactShadow[index].GetComponent<SpriteRenderer>();
                var cst  = _cardContactShadow[index].transform;
                if (isWild)
                {
                    // 2026-06-04 Spencer: wild card has NO separate shadow — a glowing tile
                    // wouldn't cast one, and the edge separation is being baked into the
                    // wild_swap sprite itself. (Wild-shadow tuning fields kept for later.)
                    _cardContactShadow[index].SetActive(false);
                }
                else
                {
                    if (csSR != null) { csSR.sprite = _spriteTileShadow; csSR.sharedMaterial = s_shadowMultiplyMat; }
                    cst.localPosition = new Vector3(0f, 0f, 0.04f);
                    cst.localScale    = Vector3.one;
                    _cardContactShadow[index].SetActive(!isEmpty);
                }
            }

            // Choose sprite based on mode and selection.
            // Wild slots use the wild@2x sprite (has "?" baked in), so the
            // letter text overlay is suppressed below — the sprite itself is
            // the visual.
            bool iridWild = isWild && Tile.IridescentWild;
            // Crystal tint gated separately so we can show white "?" + aura only.
            if (_cardIrid[index] != null) _cardIrid[index].SetActive(iridWild && Tile.IridescentTileTint);
            if (isWild)
            {
                // 2026-06-04 Spencer: rack wild uses the SAME normal white glossy tile that
                // every other card uses (the wild identity reads from the aura + "?" overlay).
                _cardSRs[index].sprite = _spriteGlossy ?? _spriteNormal;
                _cardSRs[index].color  = Color.white; // wild is NEVER green — clear any stray scored tint. 2026-06-10
            }
            else if (_swapModeActive)
            {
                if (isSelected)
                {
                    // Match the board tile's cyan edit glow instead of the flat blue swap sprite, so the swapped
                    // hand card and the board tile it's exchanging with read as the SAME cyan selection. The board
                    // tile's brightness comes from an additive bloom the card can't cheaply replicate, so we tint
                    // the glossy sprite a bright cyan that matches its bloomed appearance. 2026-07-10 Spencer.
                    _cardSRs[index].sprite = _spriteGlossy ?? _spriteNormal;
                    _cardSRs[index].color  = SWAP_SELECTED_CYAN;
                }
                else
                {
                    _cardSRs[index].sprite = _spriteSwap;
                    _cardSRs[index].color  = Color.white;
                }
            }
            else
            {
                // Selected card: show the LIVE tile (test_tile) with a green TINT instead of the
                // old separate greeny sprite — matches the new scored-green look. 2026-06-10 Spencer.
                if (isSelected)
                {
                    _cardSRs[index].sprite = _spriteNormal;
                    _cardSRs[index].color  = Tile.SCORED_TINT;
                }
                else
                {
                    _cardSRs[index].sprite = _spriteGlossy ?? _spriteNormal;
                    _cardSRs[index].color  = Color.white;
                }
            }

            // Cyan bloom halo ON only for the selected swap card (the dormant tile-bag swap mode), so it glows
            // like the board tile; OFF for every other state. 2026-07-10 Spencer.
            SetCardCyanGlow(index, _swapModeActive && isSelected && !isWild && !isEmpty);

            // Update letter text. Default to a flat color + the tile font; the wild
            // "?" turns on a holographic gradient + the Geometos font below (and we
            // reset both for normal letters). Font setter is a no-op if unchanged.
            _cardTexts[index].enableVertexGradient = false;
            _cardTexts[index].font = GameFont.GetTMP();
            if (isEmpty)
            {
                _cardTexts[index].text  = "";
                _cardTexts[index].color = CARD_TEXT_COLOR;
            }
            else if (isWild)
            {
                // Iridescent wild: the white base has no baked "?", so render one.
                // Hand-drawn wild@2x already bakes the "?" — suppress text there.
                _cardTexts[index].text  = iridWild ? "?" : "";
                if (iridWild)
                {
                    // 2026-06-04 Spencer: holographic "?" — uses the SAME tile font
                    // (Avenir, inherited from the default set above) + a violet→magenta
                    // vertical gradient. Gradient multiplies the base color, keep white.
                    _cardTexts[index].color = Color.white;
                    _cardTexts[index].enableVertexGradient = true;
                    Color top = new Color(0.30f, 0.22f, 0.80f, 1f); // blue-violet
                    Color bot = new Color(0.82f, 0.14f, 0.55f, 1f); // magenta-pink
                    _cardTexts[index].colorGradient = new TMPro.VertexGradient(top, top, bot, bot);
                }
                else
                {
                    _cardTexts[index].color = WILD_CARD_COLOR;
                }
            }
            else if (_swapModeActive)
            {
                _cardTexts[index].text  = letter.ToString().ToUpper();
                // Selected swap card now shows the cyan edit glow, so its letter goes dark like a board tile's
                // (not the swap orange) to match the tile it's being exchanged with. 2026-07-10 Spencer.
                _cardTexts[index].color = isSelected ? CARD_TEXT_COLOR : CARD_TEXT_SWAP;
            }
            else
            {
                _cardTexts[index].text  = letter.ToString().ToUpper();
                _cardTexts[index].color = CARD_TEXT_COLOR;
            }

            // Update point value text
            if (_cardPtsTexts[index] != null)
            {
                if (isEmpty || letter == '\0')
                {
                    _cardPtsTexts[index].text = "";
                }
                else if (isWild)
                {
                    // Suppress point value on wild cards — it has no fixed letter
                    _cardPtsTexts[index].text = "";
                }
                else
                {
                    // Point values removed — cleaner RM/CC cards; score still tallies under the hood
                    _cardPtsTexts[index].text = "";
                }
            }
        }

        // "?" reads as "blank/unknown tile" (classic Scrabble wild convention) and
        // is in every font atlas — ★ (U+2605) isn't in AvenirNext SDF so it rendered
        // as a missing-glyph square box. Player reads "?" as "any letter" immediately.
        private const string WILD_CARD_GLYPH = "?";
        private static readonly Color WILD_CARD_COLOR = new Color(0.75f, 0.40f, 1.00f, 1f); // purple — matches Tile.WILD_LETTER_COLOR

        // ── Layout helpers ────────────────────────────────────────────────────────

        // ── Shuffle button ───────────────────────────────────────────────────

        private GameObject _shuffleButton;
        private float _shuffleButtonY;
        private float _shuffleButtonX;
        private float _shuffleButtonSize;

        private void BuildShuffleButton()
        {
            if (_grid == null) return;

            // 2026-05-28 (Path A): SHUFFLE button removed from MVP UI. The
            // shuffle mechanic is now exposed as the "Jester Hat" booster
            // (WispwhirlSingleRow, untargeted, full-board shuffle preserving
            // primed tiles). Body kept dormant in case we ever bring back a
            // dedicated hand-only shuffle for tutorial/onboarding.
            return;
#pragma warning disable CS0162 // unreachable code — intentional, see comment above
            float cardY = GetCardRowY();
            _shuffleButtonY = SurvivalManager.IsSurvivalMode
                ? GetActionRowY()
                : cardY - _cardSize * 1.0f;
            _shuffleButtonX = -_cardSize * 1.0f;   // left side
            _shuffleButtonSize = _cardSize * 0.7f;

            _shuffleButton = new GameObject("ShuffleButton");
            _shuffleButton.transform.position = new Vector3(_shuffleButtonX, _shuffleButtonY, -1f);

            // Background rounded rect sprite
            int bgW = 480;
            int bgH = 160;
            int bgRadius = 40;
            Color shuffleBgColor = new Color(0.961f, 0.761f, 0.294f, 1f); // gold #F5C24B
            Sprite bgSprite = TileRenderer.CreateSolidRoundedRect(bgW, bgH, bgRadius, shuffleBgColor);

            SpriteRenderer bgSR = _shuffleButton.AddComponent<SpriteRenderer>();
            bgSR.sprite = bgSprite;
            bgSR.sortingOrder = 11;

            // Scale the bg to appropriate world size
            float bgWorldW = _cardSize * 1.8f; // wider + taller presence
            float bgNativeW = bgW / 100f;
            float bgScale = bgWorldW / bgNativeW;
            _shuffleButton.transform.localScale = new Vector3(bgScale, bgScale, 1f);

            float invBgScale = 1f / Mathf.Max(bgScale, 0.01f);

            // Text label on top of background
            GameObject textGO = new GameObject("ShuffleText");
            textGO.transform.SetParent(_shuffleButton.transform, false);
            textGO.transform.localPosition = new Vector3(0f, 0f, -0.1f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset btnFont = GameFont.GetUITMP();
            if (btnFont != null) tm.font = btnFont;
            tm.text = "SHUFFLE";
            tm.fontSize = 2.8f;
            tm.fontStyle = TMPro.FontStyles.Bold;
            tm.color = new Color(0.145f, 0.153f, 0.200f, 0.95f);
            tm.alignment = TMPro.TextAlignmentOptions.Center;
            tm.sortingOrder = 12;
            tm.rectTransform.sizeDelta = new Vector2(2f, 0.8f);
            tm.enableWordWrapping = false;
            tm.overflowMode = TMPro.TextOverflowModes.Overflow;
            tm.outlineWidth = 0f;
            // Force clean material — no underlay, no outline, no effects
            if (tm.fontSharedMaterial != null)
            {
                Material mat = new Material(tm.fontSharedMaterial);
                tm.fontMaterial = mat;
                mat.DisableKeyword("UNDERLAY_ON");
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetX, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayOffsetY, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlayDilate, 0f);
                mat.SetFloat(TMPro.ShaderUtilities.ID_UnderlaySoftness, 0f);
                mat.SetColor(TMPro.ShaderUtilities.ID_UnderlayColor, Color.clear);
                mat.SetFloat(TMPro.ShaderUtilities.ID_OutlineWidth, 0f);
            }
            textGO.transform.localScale = new Vector3(invBgScale, invBgScale, 1f);

            MeshRenderer mr = textGO.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 12;
#pragma warning restore CS0162
        }

        private SpriteRenderer _shuffleFillSR;
        private Coroutine _shuffleFillCoroutine;
        private bool _shuffleFilling = false;

        private bool TryHandleShuffleButton(Vector3 worldPos)
        {
            if (_shuffleButton == null) return false;
            if (TutorialManager.BlockShuffleAndSwap) return false;
            // No blocking — multiple rapid taps just re-trigger shuffle

            float halfSize = _shuffleButtonSize;
            bool inX = worldPos.x >= _shuffleButtonX - halfSize && worldPos.x <= _shuffleButtonX + halfSize;
            bool inY = worldPos.y >= _shuffleButtonY - halfSize * 0.5f && worldPos.y <= _shuffleButtonY + halfSize * 0.5f;

            if (inX && inY)
            {
                // Flash button and shuffle — SpriteRenderer.color is a TINT on the gold sprite
                SpriteRenderer bgSR = _shuffleButton.GetComponent<SpriteRenderer>();
                if (bgSR != null) bgSR.color = new Color(1.2f, 1.1f, 0.9f, 1f); // bright white-ish tint
                Invoke(nameof(ResetShuffleButtonColor), 0.3f);
                ShuffleHand();
                return true;
            }
            return false;
        }

        private void ResetShuffleButtonColor()
        {
            if (_shuffleFillSR != null) { Destroy(_shuffleFillSR.gameObject); _shuffleFillSR = null; }
            if (_shuffleButton == null) return;
            SpriteRenderer bgSR = _shuffleButton.GetComponent<SpriteRenderer>();
            if (bgSR != null) bgSR.color = Color.white; // white tint = show sprite's natural gold color
            _shuffleFilling = false;
        }

        private void ShuffleHand()
        {
            GameAudio.Instance?.PlayShuffle();
            StartCoroutine(ShuffleHandAnimated());
        }

        private IEnumerator ShuffleHandAnimated()
        {
            if (MatchController.Instance == null) yield break;
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) yield break;

            float baseY = GetCardRowY();

            // Clear shadows and selection
            HideAllCardShadows();
            _selectedIndex = -1;

            // Capture rest positions
            Vector3[] restPositions = new Vector3[HAND_SIZE];
            for (int i = 0; i < HAND_SIZE; i++)
                restPositions[i] = new Vector3(GetCardX(i), baseY, -1f);

            // ── Phase 1: SHAKE in place — jitter each card around its own position ──
            float shakeDur = 0.25f;
            float shakeElapsed = 0f;
            float posJitter = _cardSize * 0.10f;
            float rotJitter = 8f;

            while (shakeElapsed < shakeDur)
            {
                shakeElapsed += Time.deltaTime;

                for (int i = 0; i < HAND_SIZE; i++)
                {
                    if (_cardObjects[i] == null) continue;
                    Transform t = _cardObjects[i].transform;

                    float ox = Random.Range(-posJitter, posJitter);
                    float oy = Random.Range(-posJitter, posJitter) * 0.5f;
                    t.position = restPositions[i] + new Vector3(ox, oy, 0f);

                    float rz = Random.Range(-rotJitter, rotJitter);
                    t.localRotation = Quaternion.Euler(0f, 0f, rz);
                }
                yield return null;
            }

            // ── Phase 2: SHUFFLE DATA ──
            // Permute letters AND wild flags together so a shuffled wild lands in
            // the correct new slot instead of stranding its flag at the old index.
            char[] letters = hand.GetAllSlots();
            bool[] wilds   = hand.GetAllWildFlags();
            int activeSize = HAND_SIZE;
            // MVP P3.5: SurvivalRng — hand shuffle is gameplay-affecting (same seed = same shuffled order).
            for (int i = activeSize - 1; i > 0; i--)
            {
                int j = SurvivalRng.Range(0, i + 1);
                char tempC = letters[i]; letters[i] = letters[j]; letters[j] = tempC;
                bool tempW = wilds[i];   wilds[i]   = wilds[j];   wilds[j]   = tempW;
            }
            hand.ReorderSlotsWithFlags(letters, wilds);

            _hand = (char[])letters.Clone();
            RefreshAllCardVisuals();

            // ── Phase 3: SETTLE — snap back to rest with overshoot ──
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                Transform t = _cardObjects[i].transform;
                t.DOKill();
                t.DOMove(restPositions[i], 0.15f)
                    .SetEase(DG.Tweening.Ease.OutBack, 2f);
                t.DORotate(Vector3.zero, 0.10f)
                    .SetEase(DG.Tweening.Ease.OutQuad);
            }

            yield return WaitCache.Get(0.18f);

            // Snap clean
            for (int i = 0; i < HAND_SIZE; i++)
            {
                if (_cardObjects[i] == null) continue;
                _cardObjects[i].transform.position = restPositions[i];
                _cardObjects[i].transform.localRotation = Quaternion.identity;
            }

            _selectedIndex = -1;
//             WDLog.Log($"[HandManager] Hand shuffled: {new string(letters)}");
        }

        private static Sprite _softShadowSprite;
        private static bool _softShadowBuilt; // force rebuild on code change

        private Sprite GetSoftShadowSprite()
        {
            if (_softShadowBuilt && _softShadowSprite != null) return _softShadowSprite;
            _softShadowBuilt = true;

            // Generate a blurred rounded-rect shadow texture
            int size = 128;
            int padding = 20; // soft edge region
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] px = new Color[size * size];

            float innerRadius = (size * 0.5f) - padding;
            float center = size * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance from center, shaped as rounded rect
                    float dx = Mathf.Abs(x - center);
                    float dy = Mathf.Abs(y - center);
                    float cornerDist = Mathf.Sqrt(
                        Mathf.Max(0, dx - innerRadius * 0.7f) * Mathf.Max(0, dx - innerRadius * 0.7f) +
                        Mathf.Max(0, dy - innerRadius * 0.7f) * Mathf.Max(0, dy - innerRadius * 0.7f));
                    float edgeDist = Mathf.Max(dx, dy);
                    float dist = Mathf.Max(cornerDist, edgeDist - innerRadius);

                    // Smooth falloff
                    float alpha = 1f - Mathf.Clamp01(dist / padding);
                    alpha = alpha * alpha; // quadratic falloff for soft edge
                    alpha *= 1f; // full opacity in texture — runtime controls visibility

                    px[y * size + x] = new Color(0f, 0f, 0f, alpha);
                }
            }

            tex.SetPixels(px);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;

            _softShadowSprite = Sprite.Create(tex,
                new Rect(0, 0, size, size),
                new Vector2(0.5f, 0.5f), 100f);

            return _softShadowSprite;
        }

        private Vector3 GetCardBaseScale()
        {
            // Use actual sprite bounds if hand-drawn sprites are loaded
            float nativeSize;
            if (_spriteNormal != null && _spriteNormal.bounds.size.x > 0)
                nativeSize = _spriteNormal.bounds.size.x;
            else
            {
                int texSize = Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512);
                nativeSize = texSize / 100f;
            }
            float scale = _cardSize / nativeSize;
            return new Vector3(scale, scale, 1f);
        }

        private float GetCardRowY()
        {
            // 2026-05-28 (Path A, Phase 2): Survival cards live at exact
            // PSD-pixel Y. Card center Y (PSD) = PSD_CARD_Y + PSD_CARD_H/2.
            if (SurvivalManager.IsSurvivalMode)
                return PsdYToWorld(PSD_CARD_Y + PSD_CARD_H * 0.5f);

            if (_grid == null) return -8f;
            return _grid.GridBottom - _grid.CellSize * 0.9f;
        }

        private float GetActionRowY()
        {
            if (_grid == null) return -9f;
            if (!SurvivalManager.IsSurvivalMode)
                return GetCardRowY() - _cardSize * 1.0f;

            float halfH = _cam != null ? _cam.orthographicSize : Camera.main.orthographicSize;
            // Account for iPhone safe area (home indicator) + padding
            float safeBottom = 0f;
            if (Screen.safeArea.y > 0)
                safeBottom = (Screen.safeArea.y / Screen.height) * halfH * 2f;
            // 2026-05-28 (Path A): hand row is no longer anchored from screen
            // bottom — GetCardRowY now uses GridBottom directly. This value
            // (GetActionRowY) is only consulted for the inset's safe-area math.
            // Keep small/sane: clamps where the bottom of the world-space UI
            // strip would START if it existed, but it doesn't in Path A.
            float bottomInset = _cardSize * 0.50f + safeBottom;
            return -halfH + bottomInset;
        }

        private float GetCardX(int index)
        {
            // 2026-05-28 (Path A, Phase 2): Survival cards live at exact
            // PSD-pixel positions inside the hand pill. Card N center X (PSD) =
            // PSD_CARD_X0 + N * PSD_CARD_STEP + PSD_CARD_W/2.
            if (SurvivalManager.IsSurvivalMode)
            {
                float xPsdCenter = PSD_CARD_X0 + index * PSD_CARD_STEP + PSD_CARD_W * 0.5f;
                return PsdXToWorld(xPsdCenter);
            }

            if (_grid == null) return (index - 2f) * 1.5f;

            float gridWidth = _grid.GridRight - _grid.GridLeft;
            float handWidth = gridWidth * 0.82f;
            float step      = handWidth / HAND_SIZE;
            float startX    = -handWidth / 2f + step * 0.5f;
            return startX + index * step;
        }

        // ── Next tile preview ───────────────────────────────────────────────────

        /// <summary>2026-09-03 swap-pivot: the NEXT preview belongs to the DROP mechanic
        /// (it previews the tile you are about to drop). With the hand gone it has no
        /// meaning. Gated with the hand — see HandEnabled.</summary>
        private void BuildNextTilePreview()
        {
            if (!HandEnabled) return;
            BuildNextTilePreviewInner();
        }

        private void BuildNextTilePreviewInner()
        {
            if (_grid == null) return;

            // 2026-05-28 (Path A): NEXT lives INSIDE the control-tray pill, on
            // the right side, separated from the hand cards by a thin vertical
            // divider. "NEXT" label sits ABOVE the small preview tile (not to
            // the left as before). Matches Spencer's locked mockup.
            bool survival = SurvivalManager.IsSurvivalMode;
            float nextRowY = survival
                ? GetCardRowY()
                : (_shuffleButtonY != 0f ? _shuffleButtonY : GetCardRowY());

            float nextX;
            float nextY;
            float previewSize;
            if (survival)
            {
                // 2026-05-28 (Path A, Phase 2): NEXT locked to exact PSD spec
                // (X=898, Y=2032, W=108, H=109). Slightly lower than card
                // center (Y=1991) per Spencer's PSD — leaves room for the
                // "NEXT" label above.
                nextX       = PsdXToWorld(PSD_NEXT_X + PSD_NEXT_W * 0.5f);
                nextY       = PsdYToWorld(PSD_NEXT_Y + PSD_NEXT_H * 0.5f);
                previewSize = PsdToWorld(PSD_NEXT_W);
            }
            else
            {
                nextX       = GetCardX(HAND_SIZE - 1) + _cardSize * 1.25f;
                nextY       = nextRowY;
                previewSize = _cardSize * 0.65f;
            }

            // No separate floating NEXT label — it goes on the tile itself (see below)

            // -- Socket/holder behind the next tile --
            _nextTileSocket = new GameObject("NextSocket");
            _nextTileSocket.transform.SetParent(transform, false);
            _nextTileSocket.transform.position = new Vector3(nextX, nextY, -0.5f);
            SpriteRenderer socketSR = _nextTileSocket.AddComponent<SpriteRenderer>();
            socketSR.sprite = _spriteNormal;
            socketSR.color = new Color(0.06f, 0.08f, 0.20f, 0.50f); // deep inset — matches board family
            socketSR.sortingOrder = 9;
            float socketNative = (_spriteNormal != null && _spriteNormal.bounds.size.x > 0)
                ? _spriteNormal.bounds.size.x
                : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
            float socketScale = (previewSize * 1.15f) / socketNative;
            _nextTileSocket.transform.localScale = new Vector3(socketScale, socketScale, 1f);

            // -- Preview tile --
            _nextTilePreview = new GameObject("NextTilePreview");
            _nextTilePreview.transform.SetParent(transform, false);
            _nextTilePreview.transform.position = new Vector3(nextX, nextY, -1f);

            SpriteRenderer sr = _nextTilePreview.AddComponent<SpriteRenderer>();
            sr.sprite = _spriteNormal;
            sr.color = new Color(0.85f, 0.83f, 0.80f, 0.55f); // soft warm, docked not boxed
            sr.sortingOrder = 10;
            _nextTileSR = sr;

            // Scale using actual sprite bounds
            float nativeSize = (sr.sprite != null && sr.sprite.bounds.size.x > 0)
                ? sr.sprite.bounds.size.x
                : Mathf.Clamp(Mathf.RoundToInt(_cardSize * 200f), 64, 512) / 100f;
            float scale = previewSize / nativeSize;
            _nextTilePreview.transform.localScale = new Vector3(scale, scale, 1f);

            float invScale = 1f / Mathf.Max(scale, 0.01f);

            // Letter text (child of tile) — true center
            GameObject textGO = new GameObject("NextLetter");
            textGO.transform.SetParent(_nextTilePreview.transform, false);
            textGO.transform.localPosition = new Vector3(0f, 0f, -0.2f);

            var tm = textGO.AddComponent<TMPro.TextMeshPro>();
            TMPro.TMP_FontAsset tileFont = GameFont.GetTMP();
            if (tileFont != null) tm.font = tileFont;
            tm.text          = "";
            tm.fontSize      = 6.3f; // 2026-06-05 Spencer: −10% (was 7.0)
            tm.fontStyle     = TMPro.FontStyles.Bold; // match board/hand letters
            tm.color         = new Color(0.25f, 0.25f, 0.30f, 1f);
            tm.alignment     = TMPro.TextAlignmentOptions.Midline; // match board/hand — centers a single capital
            tm.sortingOrder  = 15;
            tm.rectTransform.sizeDelta = new Vector2(2f, 2f);
            tm.enableWordWrapping = false;
            tm.overflowMode  = TMPro.TextOverflowModes.Overflow;
            // Same letter effects as board/hand/ghost: slight dilate, no underlay.
            var nextLetterMat = tm.fontMaterial;
            nextLetterMat.DisableKeyword("UNDERLAY_ON");
            nextLetterMat.SetFloat("_FaceDilate", 0.05f);
            tm.UpdateMeshPadding();
            textGO.transform.localScale = new Vector3(invScale, invScale, 1f);
            _nextTileLetter = tm;

            MeshRenderer mr = textGO.GetComponent<MeshRenderer>();
            if (mr != null) mr.sortingOrder = 15;

            // 2026-05-28 (Path A): "NEXT" label sits ABOVE the preview tile.
            // Anchored relative to the NEXT slot's actual Y (which in survival
            // is PSD-derived and below the card row).
            float labelY = nextY + previewSize * 0.5f + _cardSize * 0.20f;
            GameObject labelGO = new GameObject("NextLabel");
            labelGO.transform.position = new Vector3(nextX, labelY, -1f);

            var labelTmp = labelGO.AddComponent<TMPro.TextMeshPro>();
            var uiFont = GameFont.GetUITMP();
            if (uiFont != null) labelTmp.font = uiFont;
            labelTmp.text = "NEXT";
            labelTmp.fontSize = 2.2f;
            labelTmp.fontStyle = TMPro.FontStyles.Bold;
            labelTmp.color = new Color(0.78f, 0.78f, 0.88f, 0.95f);
            labelTmp.alignment = TMPro.TextAlignmentOptions.Center;
            labelTmp.sortingOrder = 15;
            labelTmp.rectTransform.sizeDelta = new Vector2(2f, 1f);
            labelTmp.enableWordWrapping = false;
            labelTmp.overflowMode = TMPro.TextOverflowModes.Overflow;
            TMPHelper.ApplyEffects(labelTmp, labelTmp.color, TMPHelper.TextTier.HUD);
            _nextTileLabel = labelTmp;

            // 2026-05-28 (Path A): Vertical divider line between the hand
            // cards and the NEXT slot — Spencer's mockup explicitly shows
            // this. Survival mode only (multiplayer/level mode keeps the
            // looser old layout).
            if (survival)
            {
                // Divider centered between last card right edge and NEXT slot
                // left edge (PSD: card 4 right=829, NEXT left=898, mid=863.5).
                float lastCardRight = GetCardX(HAND_SIZE - 1) + _cardSize * 0.5f;
                float dividerX      = (lastCardRight + nextX - previewSize * 0.5f) * 0.5f;
                float dividerH      = PsdToWorld(PSD_CARD_H);
                float dividerW      = PsdToWorld(6f);
                int   dTexW         = Mathf.Max(4, Mathf.RoundToInt(dividerW * 100f));
                int   dTexH         = Mathf.Max(8, Mathf.RoundToInt(dividerH * 100f));
                Sprite dividerSprite = TileRenderer.CreateSolidRoundedRect(
                    dTexW, dTexH, Mathf.Min(dTexW, dTexH) / 2,
                    new Color(0.78f, 0.78f, 0.92f, 0.45f));

                // Divider Y matches card row, not the offset NEXT slot.
                float dividerY = GetCardRowY();
                GameObject dividerGO = new GameObject("NextDivider");
                _nextDivider = dividerGO; // stored so the level-entry slide can carry it with the holder
                dividerGO.transform.SetParent(transform, false);
                dividerGO.transform.position = new Vector3(dividerX, dividerY, -0.4f);
                var divSR = dividerGO.AddComponent<SpriteRenderer>();
                divSR.sprite = dividerSprite;
                divSR.sortingOrder = 8;
            }
        }

        // ── Tile Bag button ─────────────────────────────────────────────────

        private GameObject _tileBagButton;
        private float _tileBagX;
        private float _tileBagY;
        private float _tileBagSize;

        private void BuildTileBagButton()
        {
            // 2026-05-28 (Path A): legacy world-space tile bag button retired.
            // BoosterHUDSlot owns the new TileBag tool in screen-space and uses
            // BuildTileBagSpriteForUI() below for the icon. Body kept as dormant
            // code (early-return) so any callers still wiring through this don't
            // break. Will fully remove in Commit 3 cleanup.
            return;
#pragma warning disable CS0162 // unreachable code — intentional, see comment above
            if (_grid == null) return;

            float actionRowY = SurvivalManager.IsSurvivalMode ? GetActionRowY() : _shuffleButtonY;
            float nextX = _shuffleButtonX + _cardSize * 2.5f;
            float previewSize = _cardSize * 0.65f;
            _tileBagX = nextX + previewSize * 0.5f + _cardSize * 0.85f;
            _tileBagY = actionRowY;
            _tileBagSize = _cardSize * 0.45f;

            _tileBagButton = new GameObject("TileBagButton");
            _tileBagButton.transform.SetParent(transform, false);
            _tileBagButton.transform.position = new Vector3(_tileBagX, _tileBagY, -1f);

            // Draw a procedural bag silhouette
            int texW = 64, texH = 80;
            Texture2D bagTex = new Texture2D(texW, texH, TextureFormat.ARGB32, false);
            Color clear = Color.clear;
            Color fill = new Color(0.85f, 0.80f, 0.70f, 0.9f); // warm tan to match tiles

            // Clear
            Color[] pixels = new Color[texW * texH];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            // Draw bag body (rounded trapezoid)
            for (int y = 0; y < texH; y++)
            {
                for (int x = 0; x < texW; x++)
                {
                    float nx = (x - texW * 0.5f) / (texW * 0.5f); // -1 to 1
                    float ny = (float)y / texH; // 0 (bottom) to 1 (top)

                    // Bag body: wider at bottom, narrow at top
                    float bodyBottom = 0.0f;
                    float bodyTop = 0.7f;
                    float neckBottom = 0.7f;
                    float neckTop = 0.85f;
                    float knotBottom = 0.82f;
                    float knotTop = 1.0f;

                    bool inside = false;

                    if (ny >= bodyBottom && ny < bodyTop)
                    {
                        // Body: tapers from wide at bottom to narrow at top
                        float t = (ny - bodyBottom) / (bodyTop - bodyBottom);
                        float halfWidth = Mathf.Lerp(0.85f, 0.55f, t);
                        inside = Mathf.Abs(nx) < halfWidth;
                    }
                    else if (ny >= neckBottom && ny < neckTop)
                    {
                        // Neck: narrow
                        inside = Mathf.Abs(nx) < 0.3f;
                    }
                    else if (ny >= knotBottom && ny <= knotTop)
                    {
                        // Knot: wider bump
                        float t = (ny - knotBottom) / (knotTop - knotBottom);
                        float bulge = 0.35f + 0.15f * Mathf.Sin(t * Mathf.PI);
                        inside = Mathf.Abs(nx) < bulge;
                    }

                    if (inside)
                        pixels[y * texW + x] = fill;
                }
            }

            bagTex.SetPixels(pixels);
            bagTex.Apply();
            bagTex.filterMode = FilterMode.Bilinear;

            Sprite bagSprite = Sprite.Create(bagTex,
                new Rect(0, 0, texW, texH),
                new Vector2(0.5f, 0.5f), 100f);

            SpriteRenderer sr = _tileBagButton.AddComponent<SpriteRenderer>();
            sr.sprite = bagSprite;
            sr.sortingOrder = 12;

            // Scale to desired size
            float nativeH = texH / 100f;
            float bagScale = _tileBagSize / nativeH;
            _tileBagButton.transform.localScale = new Vector3(bagScale, bagScale, 1f);

            // No label — icon only
#pragma warning restore CS0162
        }

        /// <summary>
        /// Public sprite factory for the tile-bag icon. Reused by the new
        /// screen-space BoosterHUDSlot tools row so the bag art stays consistent
        /// across the codebase. Generates a fresh sprite each call (cheap —
        /// 64×80 procedural texture); callers may cache the result.
        /// </summary>
        public static Sprite BuildTileBagSpriteForUI()
        {
            int texW = 64, texH = 80;
            var tex = new Texture2D(texW, texH, TextureFormat.ARGB32, false);
            Color clear = Color.clear;
            Color fill  = new Color(0.85f, 0.80f, 0.70f, 0.95f);

            var pixels = new Color[texW * texH];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = clear;

            for (int y = 0; y < texH; y++)
            {
                for (int x = 0; x < texW; x++)
                {
                    float nx = (x - texW * 0.5f) / (texW * 0.5f);
                    float ny = (float)y / texH;
                    bool inside = false;

                    if (ny < 0.7f)
                    {
                        float t = ny / 0.7f;
                        float halfWidth = Mathf.Lerp(0.85f, 0.55f, t);
                        inside = Mathf.Abs(nx) < halfWidth;
                    }
                    else if (ny < 0.85f)
                    {
                        inside = Mathf.Abs(nx) < 0.30f;
                    }
                    else
                    {
                        float t = (ny - 0.82f) / 0.18f;
                        float bulge = 0.35f + 0.15f * Mathf.Sin(t * Mathf.PI);
                        inside = Mathf.Abs(nx) < bulge;
                    }
                    if (inside) pixels[y * texW + x] = fill;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();
            tex.filterMode = FilterMode.Bilinear;
            return Sprite.Create(tex, new Rect(0, 0, texW, texH),
                new Vector2(0.5f, 0.5f), 100f);
        }

        private bool TryHandleTileBagButton(Vector3 worldPos)
        {
            if (_tileBagButton == null) return false;
            float halfSize = _tileBagSize * 0.6f;
            bool inX = Mathf.Abs(worldPos.x - _tileBagX) < halfSize;
            bool inY = Mathf.Abs(worldPos.y - _tileBagY) < halfSize;
            return inX && inY;
        }

        private void ShowSwapTilePopup()
        {
            DismissSwapTilePopup();
            _swapTileConfirmActive = true;

            float popupX = _tileBagX;
            float popupY = _tileBagY + _tileBagSize * 0.9f;

            // Background
            _swapTilePopup = new GameObject("SwapTilePopup");
            _swapTilePopup.transform.position = new Vector3(popupX, popupY + _cardSize * 0.15f, -2f);

            TextMesh titleTm = _swapTilePopup.AddComponent<TextMesh>();
            titleTm.text = "Swap Tile?";
            titleTm.anchor = TextAnchor.MiddleCenter;
            titleTm.alignment = TextAlignment.Center;
            titleTm.fontSize = 36;
            titleTm.characterSize = 0.06f;
            titleTm.fontStyle = FontStyle.Bold;
            titleTm.color = Color.white;
            GameFont.ApplyUI(titleTm);
            MeshRenderer titleMr = _swapTilePopup.GetComponent<MeshRenderer>();
            if (titleMr != null) titleMr.sortingOrder = 30;

            // Yes
            _swapTileYesLabel = new GameObject("SwapTileYes");
            _swapTileYesLabel.transform.position = new Vector3(popupX - _cardSize * 0.4f, popupY - _cardSize * 0.15f, -2f);
            TextMesh yesTm = _swapTileYesLabel.AddComponent<TextMesh>();
            yesTm.text = "YES";
            yesTm.anchor = TextAnchor.MiddleCenter;
            yesTm.alignment = TextAlignment.Center;
            yesTm.fontSize = 36;
            yesTm.characterSize = 0.06f;
            yesTm.fontStyle = FontStyle.Bold;
            yesTm.color = new Color(0.2f, 0.9f, 0.4f, 1f); // green
            GameFont.ApplyUI(yesTm);
            MeshRenderer yesMr = _swapTileYesLabel.GetComponent<MeshRenderer>();
            if (yesMr != null) yesMr.sortingOrder = 30;

            // No
            _swapTileNoLabel = new GameObject("SwapTileNo");
            _swapTileNoLabel.transform.position = new Vector3(popupX + _cardSize * 0.4f, popupY - _cardSize * 0.15f, -2f);
            TextMesh noTm = _swapTileNoLabel.AddComponent<TextMesh>();
            noTm.text = "NO";
            noTm.anchor = TextAnchor.MiddleCenter;
            noTm.alignment = TextAlignment.Center;
            noTm.fontSize = 36;
            noTm.characterSize = 0.06f;
            noTm.fontStyle = FontStyle.Bold;
            noTm.color = new Color(1f, 0.4f, 0.4f, 1f); // red
            GameFont.ApplyUI(noTm);
            MeshRenderer noMr = _swapTileNoLabel.GetComponent<MeshRenderer>();
            if (noMr != null) noMr.sortingOrder = 30;

//             WDLog.Log("[HandManager] Showing Swap Tile? popup");
        }

        private void DismissSwapTilePopup()
        {
            _swapTileConfirmActive = false;
            if (_swapTilePopup != null) { Destroy(_swapTilePopup); _swapTilePopup = null; }
            if (_swapTileYesLabel != null) { Destroy(_swapTileYesLabel); _swapTileYesLabel = null; }
            if (_swapTileNoLabel != null) { Destroy(_swapTileNoLabel); _swapTileNoLabel = null; }
        }

        private bool IsWorldPosNearObject(GameObject obj, Vector3 worldPos, float radius)
        {
            if (obj == null) return false;
            Vector3 p = obj.transform.position;
            return Mathf.Abs(worldPos.x - p.x) < radius && Mathf.Abs(worldPos.y - p.y) < radius;
        }

        public void UpdateNextTilePreview()
        {
            if (_nextTileLetter == null) return;

            // Suppressed while a turn is resolving after an EARLY visual deal: the cached "next" letter is the one we
            // already dealt into the hand (the real draw + re-cache doesn't run until the turn resolves), so showing it
            // would repeat the just-dealt letter. Blank it until STEP 5 restores the true next. 2026-07-14 Spencer.
            if (_suppressNextPreview)
            {
                _nextTileLetter.text = "";
                return;
            }

            if (MatchController.Instance == null || MatchController.Instance.Bag == null)
            {
                _nextTileLetter.text = "";
                return;
            }

            // Only show next tile when it's the human player's turn
            bool isPlayerTurn = MatchController.Instance.CurrentPlayer == MatchController.PLAYER_HUMAN;
            if (!isPlayerTurn)
            {
                _nextTileLetter.text = "";
                return;
            }

            // The preview is a hard contract with the player — whatever letter is
            // cached WILL be dealt next. DrawSlot no longer re-validates the cache,
            // so the displayed letter is always truthful.
            PlayerHand hand = MatchController.Instance.GetHand(MatchController.PLAYER_HUMAN);
            if (hand == null) { _nextTileLetter.text = ""; return; }

            char next = hand.CachedNextLetter;
            _nextTileLetter.text = (next != '\0') ? next.ToString() : "";
        }

        // ═════════════════════════════════════════════════════════════════════════
        // BOOSTER CASCADE — drive the post-booster NextStep loop with full FX
        // ═════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called by BoosterManager after a booster has cleared tiles AND
        /// RulesEngine.BeginCascadeAfterBoosterClear has set up gravity +
        /// seed cells. Drives the same NextStep resolution loop as a player
        /// drop, so post-booster gravity can form / prime / score / detonate
        /// words and cascade chains.
        ///
        /// Models the swap-resolution loop pattern (HandManager.cs ~2322).
        /// Sets IsInteractable=false + BeginProcessing for the duration so
        /// hand input cannot corrupt the state machine. Does NOT consume a
        /// turn or switch player — boosters are turn-neutral.
        ///
        /// playerIdx defaults to PLAYER_HUMAN since today's boosters are
        /// human-only (no AI booster path). Threaded as a parameter so future
        /// AI booster support is one-line wire-up.
        /// </summary>
        public IEnumerator RunBoosterCascadeChain(int playerIdx)
        {
            RulesEngine rules = RulesEngine.Instance;
            GridManager grid  = GridManager.Instance;
            if (rules == null || grid == null) yield break;

            // Input gate — mirrors player drop / rewrite / swap paths.
            bool previousInteractable = IsInteractable;
            IsInteractable = false;
            if (MatchController.Instance != null) MatchController.Instance.BeginProcessing();

            bool resolving = true;
            int totalScore = 0;
            int wordIndex  = 0;
            int maxChainDepth = 0;

            // Reset scoring display chain counter so cascade words use fresh
            // chain step numbers like a regular drop does.
            if (ScoringDisplay.Instance != null)
                ScoringDisplay.Instance.ResetChain();
            Color playerColor = new Color(0.9f, 0.2f, 0.8f);  // magenta — same as drop path

            try
            {
                while (resolving)
                {
                    RulesEngine.StepResult step = rules.NextStep();
                    if (step == null) { resolving = false; break; }

                    if (step.ChainDepth > maxChainDepth)
                        maxChainDepth = step.ChainDepth;

                    switch (step.Phase)
                    {
                        case RulesEngine.ResolutionPhase.WordsDetected:
                            break;

                        case RulesEngine.ResolutionPhase.WordsScored:
                        {
                            bool detonationComing = rules.PeekHasTriggers();

                            if (step.ScoredWords != null)
                            {
                                foreach (var sw in step.ScoredWords)
                                {
                                    List<Tile> scoredTiles = new List<Tile>();
                                    if (sw.Cells != null)
                                        foreach (var cell in sw.Cells)
                                        {
                                            Tile t = grid.GetTile(cell.x, cell.y);
                                            if (t != null) scoredTiles.Add(t);
                                        }

                                    if (WordDropFX.Instance != null)
                                        WordDropFX.Instance.PlayWordScored(scoredTiles, playerColor, wordIndex);

                                    HapticsManager.Light();

                                    if (!detonationComing && BonusPopup.Instance != null && scoredTiles.Count > 0)
                                    {
                                        Vector3 wc = Vector3.zero;
                                        for (int st = 0; st < scoredTiles.Count; st++)
                                            if (scoredTiles[st] != null) wc += scoredTiles[st].transform.position;
                                        wc /= Mathf.Max(1, scoredTiles.Count);
                                        BonusPopup.Instance.ShowWordScore(sw.Word, sw.FinalScore, wc);
                                    }

                                    // Survival long-word reward parity with drop path
                                    if (SurvivalManager.IsSurvivalMode
                                        && GameVisualBridge.Instance != null
                                        && !string.IsNullOrEmpty(sw.Word))
                                    {
                                        GameVisualBridge.Instance.TriggerSurvivalLongWordReward(
                                            sw.Word, scoredTiles, isPlayer: true);
                                    }

                                    wordIndex++;
                                }
                            }
                            yield return WaitCache.Get(detonationComing ? 0.1f : 0.3f);
                            break;
                        }

                        case RulesEngine.ResolutionPhase.TriggersFound:
                        {
                            // Set primed glow on triggered tiles (parity with swap-resolution)
                            if (step.Triggers != null)
                            {
                                foreach (var trig in step.Triggers)
                                {
                                    var pw = rules.PrimedRegistry != null
                                        ? rules.PrimedRegistry.GetById(trig.PrimedWordId)
                                        : null;
                                    int currentTurn = rules.GlobalTurn;
                                    int heatLevel = pw != null
                                        ? Mathf.Min(Mathf.Max(0, currentTurn - pw.PrimedOnTurn),
                                                    RulesEngine.HEAT_FUSE_MAX_BONUS)
                                        : 0;
                                    int fuse = pw != null ? Mathf.Max(0, pw.ExpiresOnTurn - currentTurn) : 0;
                                    bool isGold = pw != null && pw.IsGold;
                                    Color glowColor = isGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                                    if (trig.TriggeredCells != null)
                                    {
                                        foreach (var c in trig.TriggeredCells)
                                        {
                                            Tile t = grid.GetTile(c.x, c.y);
                                            if (t != null)
                                                t.SetPrimedGlow(glowColor, playFlash: true,
                                                    heatLevel: heatLevel, fuseRemaining: fuse, isGold: isGold);
                                        }
                                    }
                                }
                            }

                            CacheBurstTriggers(step);
                            yield return WaitCache.Get(0.05f);
                            break;
                        }

                        case RulesEngine.ResolutionPhase.Exploding:
                        {
                            if (ChainCounter.Instance != null)
                                ChainCounter.Instance.OnDetonation(step.ChainDepth);

                            DefrostThawedTiles(grid, step); // ICE: thaw frozen tiles caught in this detonation (survive + defrost)
                            if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                            {
                                var dyingTiles = new List<Tile>();
                                foreach (var c in step.ExplodedCells)
                                {
                                    Tile t = grid.GetTile(c.x, c.y);
                                    if (t != null) dyingTiles.Add(t);
                                }

                                Vector3 center = Vector3.zero;
                                for (int d = 0; d < dyingTiles.Count; d++)
                                    if (dyingTiles[d] != null) center += dyingTiles[d].transform.position;
                                center /= Mathf.Max(1, dyingTiles.Count);

                                // Hitstop on initial detonation only — cascades pop instantly.
                                if (dyingTiles.Count > 0 && step.ChainDepth == 0)
                                {
                                    yield return StartCoroutine(WordDropFX.HitStop(0.05f));
                                }

                                FirePerWordBurst();
                                FireTileFlashBoxes(dyingTiles);

                                if (dyingTiles.Count > 0 && WordDropFX.Instance != null)
                                {
                                    int wLen = step.LongestWordLength > 0 ? step.LongestWordLength : dyingTiles.Count;
                                    LaunchThemeFlights(dyingTiles, grid);   // animal-word letters fly to the goal instead of exploding (2026-10-23)
                                    yield return WordDropFX.MaybeBigPopAndHold(dyingTiles, step.SpecialsFired != null && step.SpecialsFired.Count > 0);
                                    yield return ExplodeWordsThenSplash(step, dyingTiles, step.ChainDepth, wLen);
                                }

                                grid.RemoveTiles(step.ExplodedCells);

                                if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                                    SurvivalManager.Instance.NotifyDetonation(step.ExplodedCells.Count, step.ChainDepth);

                                if (step.DetonationBonus > 0 && BonusPopup.Instance != null)
                                {
                                    int baseBonus = step.DetonationBonus - step.DetonationHeat;
                                    BonusPopup.Instance.ShowDetonation(TakeBurstWords(), baseBonus, center, step.ChainDepth);
                                    if (step.DetonationHeat > 0)
                                        BonusPopup.Instance.ShowHeatBonus(step.DetonationHeat, center);
                                }

                                ApplyDetonationRefillRewards(step, center, 0);
                            }
                            break;
                        }

                        case RulesEngine.ResolutionPhase.GravityApplied:
                        {
                            yield return StartCoroutine(grid.ApplyGravity());
                            yield return WaitCache.Get(0.08f);
                            break;
                        }

                        case RulesEngine.ResolutionPhase.Complete:
                        {
                            if (ChainCounter.Instance != null)
                                ChainCounter.Instance.OnChainComplete();
                            totalScore = step.TotalScore;
                            resolving = false;
                            break;
                        }

                        default:
                            resolving = false;
                            break;
                    }
                }

                // Finalize engine state + visual sync. Uses the booster-specific
                // finalize (does NOT tick _globalTurn / expire primed words) so
                // booster use stays turn-neutral.
                rules.FinalizeBoosterCascade();
                try { grid.SyncToRulesState(rules); rules.RefreshAllPrimedWordTiles(-1, flashNew: false); }
                catch (System.Exception ex) { Debug.LogError($"[BoosterCascade] SyncToRulesState: {ex}"); }

                // Apply persistent primed glow to every cell of every primed word.
                // Without this, words primed during the cascade flash green via
                // PlayWordScored but the glow tint never lands, so the tile
                // visually reverts to white as the flash finishes — leaving the
                // player with no indication that the word is primed/scored.
                // Mirrors the rewrite path at HandManager.cs:3434.
                PrimedWordRegistry registry = rules.PrimedRegistry;
                int finalTurn = rules.GlobalTurn;
                if (registry != null)
                {
                    for (int p = 0; p < registry.Count; p++)
                    {
                        var pw = registry.GetByIndex(p);
                        if (pw == null) continue;
                        int survived = Mathf.Max(0, finalTurn - pw.PrimedOnTurn);
                        int heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                        bool justPrimed = (pw.PrimedOnTurn == finalTurn - 1 || pw.PrimedOnTurn == finalTurn);
                        for (int c = 0; c < pw.Cells.Count; c++)
                        {
                            Tile t = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                            int fuse = Mathf.Max(0, pw.ExpiresOnTurn - finalTurn);
                            Color glowColor = pw.IsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;
                            if (t != null)
                                t.SetPrimedGlow(glowColor, playFlash: justPrimed,
                                    heatLevel: heatLevel, fuseRemaining: fuse, isGold: pw.IsGold);
                        }
                    }
                }

                // Score path — booster cascades count toward the player score
                // without consuming a turn or switching player. Survival's score
                // delta also routes through LevelController in Level mode.
                if (totalScore > 0)
                {
                    if (ScoreManager.Instance != null)
                        ScoreManager.Instance.AddScore(totalScore, playerIdx);
                    LevelController.Instance?.NotifyScore(totalScore);
                    if (SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null)
                        SurvivalManager.Instance.NotifyScoreDelta(totalScore);
                }

                WDLog.Log($"[BoosterCascade] Complete — totalScore={totalScore}, maxChainDepth={maxChainDepth}");
            }
            finally
            {
                IsInteractable = previousInteractable;
                if (MatchController.Instance != null) MatchController.Instance.EndProcessing();
            }
            // 2026-10-23 Spencer: "during a cascade it lit up two words like they were primed and didnt explode". A duck landing
            // (and boosters) refill through THIS path; words its fall spelled were armed and lit, but only the player-move path
            // ever called the instant pop — they sat there until the next fall broke them ("no longer a straight run").
            if (InstantPopMode && SurvivalManager.FullBoardActive) MaybeAutoPop();
        }
    }

}
