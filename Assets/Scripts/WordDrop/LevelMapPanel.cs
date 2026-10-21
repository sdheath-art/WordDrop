using System.Collections.Generic;
using DG.Tweening;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace WordDrop
{
    /// <summary>
    /// Between-level progression map (2026-07-10 Spencer). A full-screen panel (covers the board — this
    /// IS the "you left the level" transition) showing a vertical node strip with an avatar that HOPS from
    /// the last level's node up to the new one, then a tap enters the next level (the "you arrived" beat).
    ///
    /// MVP + intentionally un-themed: clean circles in the game palette. It's the SKELETON we later theme
    /// into "Word Garden" (path winding through a garden, node = a flower that blooms) with no rework —
    /// same node strip + avatar-hop + enter hook. Built standalone + testable via the FX Test Menu; wired
    /// into StageClearModal.FinalizeDismiss once it feels right.
    /// </summary>
    public class LevelMapPanel : MonoBehaviour
    {
        public static LevelMapPanel Instance { get; private set; }

        // ── Layout (reference res 540×960, matches the other modals) ──
        private const int   VISIBLE_NODES  = 10;     // the first 10 levels — all shown at once (fixed map, no scroll)
        private const float NODE_SPACING   = 74f;    // vertical px between nodes (tightened so all 10 fit between HUD panels)
        private const float NODE_SIZE      = 52f;
        private const float ZIGZAG_X       = 70f;    // gentle winding-path horizontal offset
        private const float STRIP_Y_OFFSET = 8f;     // vertical centering of the strip in the gap between top/bottom HUD

        // Top/bottom HUD panels (Candy-Crush-map layout: hearts + coins + settings up top; bottom bar for later).
        private const float TOP_HUD_H    = 92f;
        private const float BOTTOM_HUD_H = 78f;
        private static readonly Color HUD_PANEL   = new Color(0.93f, 0.45f, 0.62f, 1f);  // candy pink (game HUD frame)
        private static readonly Color HUD_PILL    = new Color(0.98f, 0.94f, 0.88f, 1f);  // warm cream pill
        private static readonly Color HUD_INK     = new Color(0.16f, 0.20f, 0.34f, 1f);  // dark navy text
        private static readonly Color HUD_BOTTOM  = new Color(0.10f, 0.27f, 0.50f, 1f);  // navy panel (game bench tone)
        private static readonly Color HEART_RED   = new Color(0.94f, 0.30f, 0.42f, 1f);
        private TextMeshProUGUI _heartsText;
        private TextMeshProUGUI _coinsText;
        private RectTransform   _coinsIconRT;      // the coin icon inside the coins pill — the cascade's TARGET
        private int             _displayedCoins;   // the number the pill is SHOWING (ticks up as coins land)
        private int             _pendingCoinReward;// coins earned on the just-cleared level, waiting to fly on the map
        private int             _pendingCascadeLevel = -1; // the node the star landed on = the cascade's SOURCE
        private int             _coinCascadeLanded, _coinCascadeTotal;
        private System.Action   _coinCascadeOnComplete;

        // ── Worlds (Mario-style) ────────────────────────────────────────────────────
        // Each batch of VISIBLE_NODES levels is a "world" with its OWN look. World 1 (levels 1-10) is the
        // tutorial; the run proper starts at World 2. Themes CYCLE once past the last, so a long run keeps
        // changing scenery. Palette-only for now (cheap + distinct); real per-world art is a later lift.
        // 2026-07-13 Spencer.
        private struct WorldTheme
        {
            public string Name;
            public Color BgTop, BgBottom, NodeDone, NodeCurrent, NodeLocked, Path, Avatar;
        }
        private static readonly Color NODE_RIM = new Color(1f, 1f, 1f, 1f); // white rim — universal across worlds
        private static readonly WorldTheme[] s_worlds =
        {
            new WorldTheme { Name = "Tutorial",    BgTop = new Color(0.40f,0.82f,0.96f), BgBottom = new Color(0.50f,0.90f,0.84f), NodeDone = new Color(0.30f,0.80f,0.45f), NodeCurrent = new Color(1.00f,0.80f,0.30f), NodeLocked = new Color(0.13f,0.29f,0.52f), Path = new Color(1f,1f,1f,0.70f), Avatar = new Color(0.93f,0.45f,0.62f) },
            new WorldTheme { Name = "Green Hills",  BgTop = new Color(0.46f,0.80f,0.44f), BgBottom = new Color(0.74f,0.92f,0.55f), NodeDone = new Color(0.20f,0.62f,0.42f), NodeCurrent = new Color(1.00f,0.82f,0.28f), NodeLocked = new Color(0.20f,0.34f,0.24f), Path = new Color(1f,1f,1f,0.72f), Avatar = new Color(0.95f,0.40f,0.55f) },
            new WorldTheme { Name = "Desert",       BgTop = new Color(0.98f,0.72f,0.38f), BgBottom = new Color(0.99f,0.88f,0.62f), NodeDone = new Color(0.88f,0.48f,0.20f), NodeCurrent = new Color(0.92f,0.28f,0.30f), NodeLocked = new Color(0.45f,0.32f,0.22f), Path = new Color(1f,1f,1f,0.72f), Avatar = new Color(0.30f,0.52f,0.95f) },
            new WorldTheme { Name = "Ocean",        BgTop = new Color(0.30f,0.68f,0.94f), BgBottom = new Color(0.58f,0.88f,0.95f), NodeDone = new Color(0.98f,0.55f,0.45f), NodeCurrent = new Color(1.00f,0.85f,0.35f), NodeLocked = new Color(0.12f,0.28f,0.48f), Path = new Color(1f,1f,1f,0.72f), Avatar = new Color(1.00f,0.55f,0.30f) },
            new WorldTheme { Name = "Tundra",       BgTop = new Color(0.68f,0.84f,0.98f), BgBottom = new Color(0.90f,0.95f,1.00f), NodeDone = new Color(0.35f,0.62f,0.90f), NodeCurrent = new Color(0.18f,0.78f,0.90f), NodeLocked = new Color(0.45f,0.55f,0.68f), Path = new Color(0.30f,0.45f,0.65f,0.60f), Avatar = new Color(0.95f,0.40f,0.55f) },
            new WorldTheme { Name = "Volcano",      BgTop = new Color(0.82f,0.28f,0.24f), BgBottom = new Color(0.98f,0.58f,0.30f), NodeDone = new Color(1.00f,0.80f,0.30f), NodeCurrent = new Color(1.00f,0.92f,0.45f), NodeLocked = new Color(0.35f,0.14f,0.14f), Path = new Color(1f,1f,1f,0.68f), Avatar = new Color(0.40f,0.72f,0.98f) },
            new WorldTheme { Name = "Candy",        BgTop = new Color(0.96f,0.55f,0.76f), BgBottom = new Color(0.99f,0.82f,0.90f), NodeDone = new Color(0.62f,0.40f,0.88f), NodeCurrent = new Color(0.28f,0.82f,0.70f), NodeLocked = new Color(0.72f,0.35f,0.55f), Path = new Color(1f,1f,1f,0.75f), Avatar = new Color(0.30f,0.55f,0.95f) },
            new WorldTheme { Name = "Night Sky",    BgTop = new Color(0.16f,0.16f,0.42f), BgBottom = new Color(0.34f,0.28f,0.58f), NodeDone = new Color(0.35f,0.80f,0.85f), NodeCurrent = new Color(1.00f,0.82f,0.35f), NodeLocked = new Color(0.24f,0.24f,0.42f), Path = new Color(1f,1f,1f,0.60f), Avatar = new Color(0.98f,0.50f,0.62f) },
        };
        // The LAST node of every world (levels 10, 20, …) is the BOSS / hard level — always this purple, across
        // all worlds, to telegraph the difficulty spike. 2026-07-13 Spencer.
        private static readonly Color BOSS_COLOR = new Color(0.56f, 0.31f, 0.78f, 1f);
        private WorldTheme _theme = s_worlds[0];
        private static WorldTheme ThemeForLevel(int level)
        {
            int world = (BatchStart(level) - 1) / VISIBLE_NODES; // 0-based world index
            return s_worlds[world % s_worlds.Length];
        }

        private Canvas _canvas;
        private CanvasGroup _group;
        private RectTransform _nodesRoot;
        private RectTransform _avatar;
        private TextMeshProUGUI _tapPrompt;
        private Button _tapButton;
        private Button _currentNodeButton; // the current level's node — tap to (re)open the play modal on a bare map
        private Image _bgTopImg;      // recolored per world
        private bool    _hasMapArt;   // true once the painted map is in — suppresses the per-world tint
        private Vector2 _mapArtSize;  // the cover-fitted size of the map art, in canvas units
        private Image _bgBottomImg;   // recolored per world
        private Image _avatarFill;    // recolored per world
        private TextMeshProUGUI _worldTitle; // "WORLD n · NAME"

        private static Sprite s_circle;   // shared white circle (rounded-rect r = size/2)
        private static Sprite s_ring;     // shared thin ring for node rims (reuse circle, scaled child)

        private System.Action _onEntered;
        private System.Action _preHop;    // fires after fade-in, BEFORE the avatar hops (unlock reward); calls ContinueToHop when done
        private int  _hopTargetLevel;     // level the avatar hops to once any pre-hop reward is dismissed
        private int  _animateStarLevel = -1; // the just-cleared level whose star DROPS IN on this show (others are static); -1 = none
        // World-complete flow: after clearing a boss level, the map shows that world, drops a TROPHY on the boss node,
        // then shows the World Completed modal; ADVANCE pages to the next world + play modal.
        private bool _worldCompleteFlow;
        private int  _worldCompleteBoss;
        private int  _worldCompleteNext;
        private System.Action _worldCompleteIntro;
        private bool _busy;

        // ── HOME SCREEN (2026-10-20 Spencer) ────────────────────────────────────────
        // "build a homescreen to take place of the level map ... in the center a play
        // button ... level button should update the level number that you are about to
        // enter and then the pop up menu for the level should be the next step."
        //
        // This is a MODE on the map panel, not a new class, on purpose. The panel already
        // owns the background cover-fit, the hearts/coins pills, the settings gear, the
        // overlay-pause handshake and the hand-off into LevelIntroModal. Re-implementing
        // that next door would duplicate five systems that each took a session to get
        // right. In home mode the node/avatar/hop machinery is simply SKIPPED — none of it
        // is deleted, so flipping HomeMode off brings the walking map straight back.
        public static bool HomeMode = true;
        private RectTransform   _playBtnRT;
        private TextMeshProUGUI _playBtnLabel;
        private TextMeshProUGUI _playBtnShadow;
        private RectTransform   _playFaceRT, _playRimRT, _playPanelRT;
        private int             _homeLevel = 1;
        private bool _holdForIntro;       // true in the map-flow: keep the map UP while the play modal drops over it
        private int  _windowStart;        // level of node index 0

        // Phase 1 (2026-07-13): when true, the level-intro seam routes through the map (Candy-Crush loop:
        // completed → MAP → tap → play modal drops OVER the map → PLAY). PERSISTED so it survives a play-session
        // restart (lets us test boot → map). FX Test Menu toggles it.
        // KEY BUMPED TO _v2 ON 2026-10-19. Changing the DEFAULT was not enough: a default only
        // applies when the key has never been written, and "map_flow_enabled" already held 0
        // from an earlier debug toggle, so the map stayed off no matter what the default said.
        // A new key has no stored value, so the new default (ON) actually takes effect, and the
        // FX Test Menu toggle keeps working and persisting from here.
        private const string MAP_FLOW_KEY = "map_flow_enabled_v2";
        public static bool MapFlowEnabled
        {
            // DEFAULT ON since 2026-10-19. It shipped defaulting to 0, so the map between
            // levels simply never appeared in normal play — the clear flow went straight to
            // the level-intro modal, and the map was reachable only via the FX Test Menu's
            // "Map-in-Loop" toggle. That is why putting art on the map changed nothing on
            // screen. Spencer: "why is it not even going to the level map now".
            //
            // NOTE: this is the DEFAULT, used only when the pref has never been written. If
            // the FX Test Menu toggle has ever been touched, the stored value wins — flip it
            // there once to clear a stale OFF.
            get => PlayerPrefs.GetInt(MAP_FLOW_KEY, 1) == 1;
            set { PlayerPrefs.SetInt(MAP_FLOW_KEY, value ? 1 : 0); PlayerPrefs.Save(); }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        /// <summary>Level-flow entry: show the map for <paramref name="level"/> (hop from the previous level), and
        /// when the player taps the node fire <paramref name="onIntro"/> (the level's play modal, which drops OVER
        /// the map). Holds gameplay PAUSED the whole time so the frozen board behind can't be touched, and the pause
        /// carries through the intro until its PLAY releases it. 2026-07-13 Spencer.</summary>
        public void PresentThenIntro(int level, System.Action onIntro, System.Action preHop = null)
        {
            SurvivalManager.Instance?.SetOverlayPaused(true);
            _animateStarLevel = level - 1; // the level just cleared → its star DROPS IN after the first hop (set before
                                           // Show, which lays out the nodes + the persistent stars for the rest)
            Show(level - 1, level, onIntro);
            _holdForIntro = true; // set AFTER Show (which clears it) so the map stays up under the play modal
            _preHop = preHop;     // fires after the fade-in, BEFORE the avatar hops (e.g. the unlock reward). The
                                  // pre-hop action must call ContinueToHop() when it's done. 2026-07-13 Spencer.
        }

        /// <summary>World-complete entry: the player just cleared <paramref name="bossLevel"/> (a world-ending level).
        /// Show that boss's world, hop the avatar onto the boss node, DROP A TROPHY on it, then show the World
        /// Completed modal; ADVANCE pages to <paramref name="nextLevel"/>'s world and fires <paramref name="onIntro"/>
        /// (its play modal). 2026-07-13 Spencer.</summary>
        public void PresentWorldComplete(int bossLevel, int nextLevel, System.Action onIntro)
        {
            // HOME MODE: there is no map to walk, so the world-complete flow (which waits for the avatar
            // to land on the boss node) can never finish — Show(..., null) left the home button with
            // nothing to open, labelled with the BOSS level, and the game paused for good. Found by the
            // 2026-10-20 audit; it would have soft-locked every run at LEVEL 10/20/30. A boss clear now
            // takes the normal route: home -> LEVEL N -> card -> play.
            if (HomeMode)
            {
                WDLog.Log($"[LevelMap] boss level {RunLevel(bossLevel)} cleared — home mode, normal route to {RunLevel(nextLevel)}");
                PresentThenIntro(nextLevel, onIntro);
                return;
            }
            SurvivalManager.Instance?.SetOverlayPaused(true);
            _worldCompleteFlow  = true;
            _worldCompleteBoss  = bossLevel;
            _worldCompleteNext  = nextLevel;
            _worldCompleteIntro = onIntro;
            _animateStarLevel   = -1;           // the trophy is dropped explicitly on landing, not via the star path
            Show(bossLevel - 1, bossLevel, null); // boss's own world; avatar hops the prior node → boss node
            _holdForIntro = false;               // we show the World Completed modal on landing, not the play intro
        }

        // Avatar landed on the boss node → drop the trophy, then (after a beat) show the World Completed modal.
        private void OnWorldCompleteLanded()
        {
            // TODO: fly the reward coins from the trophy too. For now snap the pill to the real balance so a pending
            // reward from the boss level doesn't leave the coins under-counted during the Area Completed moment.
            if (_pendingCoinReward != 0) { _pendingCoinReward = 0; RefreshHud(); }

            int idx = _worldCompleteBoss - _windowStart;
            if (idx >= 0 && idx < VISIBLE_NODES)
            {
                BuildNodeMarker(NodeAnchoredPos(idx), _worldCompleteBoss, animate: true, isTrophy: true);
                // The avatar landed ON the boss node; raise the trophy ABOVE it so the celebration reads clearly.
                var marker = _nodesRoot.Find($"NodeMarker{_worldCompleteBoss}");
                if (marker != null) marker.SetAsLastSibling();
            }

            int worldNum = RunLevel(_worldCompleteBoss) / VISIBLE_NODES; // internal 20 → Area 1, 30 → Area 2 …
            DOVirtual.DelayedCall(0.75f, () =>
            {
                if (WorldCompleteModal.Instance == null)
                    new GameObject("WorldCompleteModalRoot").AddComponent<WorldCompleteModal>();
                WorldCompleteModal.Instance?.Show(worldNum, onAdvance: OnWorldCompleteAdvance);
            }).SetUpdate(true);
        }

        // ADVANCE tapped → swap the map to the next world IN PLACE. The map stays fully OPAQUE the whole time (no
        // fade through alpha 0), so the game board behind it never flashes. We re-theme + re-lay-out to the new
        // world, drop the avatar on its first node with a little bounce, then the play modal auto-pops on landing.
        // 2026-07-13 Spencer.
        private void OnWorldCompleteAdvance()
        {
            _worldCompleteFlow = false;
            int next = _worldCompleteNext;
            var intro = _worldCompleteIntro; _worldCompleteIntro = null;

            SurvivalManager.Instance?.SetOverlayPaused(true);
            _onEntered        = intro;
            _holdForIntro     = true;
            _preHop           = null;
            _animateStarLevel = -1;

            _theme = ThemeForLevel(next);
            ApplyWorldTheme(next);
            LayoutNodes(next);
            RefreshHud();

            _group.DOKill();
            _group.alpha = 1f;            // stays opaque — never reveals the board behind
            _group.blocksRaycasts = true;
            _busy = true;
            if (_tapPrompt != null) _tapPrompt.alpha = 0f;
            SetTapEnabled(false);

            PlaceAvatarAtLevel(next);
            HopTo(next);                  // little bounce onto the new world's first node → OnHopLanded → play modal
        }

        /// <summary>Force the map back to a clean, hidden state. Debug level-jumps call this so a jump made mid-flow
        /// (e.g. while the World Completed modal / a hop is up) doesn't leave the map _busy or stuck in the
        /// world-complete flow, which would make Show() silently swallow the next jump. 2026-07-13 Spencer.</summary>
        public void HardReset()
        {
            if (_group != null) _group.DOKill();
            if (_canvas != null) _canvas.gameObject.SetActive(false);
            _busy = false;
            _holdForIntro = false;
            _worldCompleteFlow = false;
            _worldCompleteIntro = null;
            _preHop = null;
            _animateStarLevel = -1;
        }

        /// <summary>Change what a node re-tap (after a cancel) re-opens. Used by the unlock level: once the reward
        /// is claimed, re-taps should go straight to the play modal, not re-show the unlock. 2026-07-13 Spencer.</summary>
        public void SetReopenAction(System.Action action) => _onEntered = action;

        /// <summary>Show the map, hop the avatar from <paramref name="fromLevel"/> → <paramref name="toLevel"/>,
        /// then invoke <paramref name="onEntered"/> when the player taps to enter the next level.</summary>
        public void Show(int fromLevel, int toLevel, System.Action onEntered)
        {
            if (_busy) { onEntered?.Invoke(); return; }
            _busy = true;
            _holdForIntro = false; // standalone Show (FX test) fades out on tap; PresentThenIntro re-sets this true
            _preHop = null;        // cleared here; PresentThenIntro re-sets it AFTER this call when there's a reward
            _onEntered = onEntered;

            EnsureUI();
            _theme = ThemeForLevel(toLevel); // pick this batch's WORLD look before building the nodes
            ApplyWorldTheme(toLevel);        // recolor background + avatar + world title

            // HOME SCREEN: no nodes, no avatar, no hop. Background + HUD + one big button
            // carrying the level you are about to play. Pressing it opens the SAME
            // LevelIntroModal a node tap used to, so the flow is home -> level card -> play.
            if (HomeMode)
            {
                ShowHome(toLevel);
                return;
            }

            LayoutNodes(toLevel);
            RefreshHud(); // hearts/coins current for this showing
            GameAudio.Instance?.PlayLevelMapMusic(); // Skybound_Victory while the map is up
            _canvas.gameObject.SetActive(true);
            _group.alpha = 0f;
            _group.blocksRaycasts = true;
            SetTapEnabled(false);
            if (_tapPrompt != null) _tapPrompt.alpha = 0f;

            PlaceAvatarAtLevel(Mathf.Clamp(fromLevel, _windowStart, _windowStart + VISIBLE_NODES - 1));

            // Fade the panel in (unscaled — gameplay may be overlay-paused), then either fire the pre-hop reward
            // (unlock modal) or hop straight away.
            // 2026-07-29: hold a beat AFTER the fade completes before anything animates. The
            // fade is only 0.30s, and firing the hop the instant it ended read as the sequence
            // starting before the player had even registered the map. The pause covers the
            // pre-hop reward path too, since it's applied to AfterFadeIn as a whole.
            _group.DOKill();
            _group.DOFade(1f, 0.30f).SetUpdate(true)
                  .OnComplete(() => DOVirtual.DelayedCall(MAP_SETTLE_BEAT, () => AfterFadeIn(toLevel)).SetUpdate(true));
        }

        // After the map has faded in: if a pre-hop action is queued (e.g. the Swap unlock reward), fire it FIRST and
        // wait — the avatar stays on the previous node until the reward is claimed, which calls ContinueToHop(). No
        // pre-hop → hop immediately. So the order reads: cleared → MAP → UNLOCKED → hop → play. 2026-07-13 Spencer.
        private void AfterFadeIn(int toLevel)
        {
            _hopTargetLevel = toLevel;
            if (_preHop != null)
            {
                var ph = _preHop;
                _preHop = null; // one-shot
                ph.Invoke();
            }
            else HopTo(toLevel);
        }

        /// <summary>Resume the flow after a pre-hop reward is dismissed: hop the avatar to the target node, then the
        /// play modal auto-pops on landing. 2026-07-13 Spencer.</summary>
        public void ContinueToHop() => HopTo(_hopTargetLevel);

        // ── Avatar hop ────────────────────────────────────────────────────────────
        private void HopTo(int toLevel)
        {
            int clampedTo = Mathf.Clamp(toLevel, _windowStart, _windowStart + VISIBLE_NODES - 1);
            Vector2 target = NodeAnchoredPos(clampedTo - _windowStart);
            if (_avatar == null) { OnHopLanded(); return; }

            Vector2 start = _avatar.anchoredPosition;
            _avatar.DOKill();

            System.Action onLand = () =>
            {
                if (_avatar != null)
                {
                    _avatar.DOPunchScale(Vector3.one * 0.18f, 0.22f, 6, 0.6f).SetUpdate(true);
                    GameAudio.Instance?.PlayMapNodeLand(); // tile_land_3
                }
                OnHopLanded();
            };

            // MAP ENTRY (same node — boot / run start / arriving in a new Area): not travelling anywhere, so instead
            // of a hop, DROP the avatar onto the node with the EXACT drop-in the stars use — big + tilted +
            // transparent → OutBounce settle + rotate upright + fade in. 2026-07-14 Spencer.
            if (Vector2.Distance(start, target) < 5f)
            {
                // MAP ENTRY (no journey): DROP the avatar in — big → OutBounce settle, like the stars — instead of a
                // hop. SAME completion path as the hop (single tween → onLand) so the intro handoff can't stall.
                // 2026-07-14 Spencer.
                _avatar.anchoredPosition = target;
                _avatar.localScale = Vector3.one * 2.2f;
                _avatar.DOScale(1f, 0.55f).SetEase(Ease.OutBounce).SetUpdate(true).OnComplete(() => onLand());
                return;
            }

            // THREE hops to the destination: thirds of the way, landing between each — reads as a little bunny-hop
            // journey up the path, not one big leap. 2026-07-15 Spencer.
            Vector2 p1 = Vector2.Lerp(start, target, 1f / 3f);
            Vector2 p2 = Vector2.Lerp(start, target, 2f / 3f);

            // The three-hop journey, with the star dropping in DURING it (never before the avatar
            // is visibly moving, so the star always lands on a vacated node).
            System.Action journey = () =>
            {
                if (_avatar == null) { onLand(); return; }
                var seq = DOTween.Sequence().SetUpdate(true);
                seq.Append(_avatar.DOJumpAnchorPos(p1, jumpPower: 70f, numJumps: 1, duration: 0.30f).SetEase(Ease.OutQuad));
                seq.AppendCallback(() => { if (_avatar != null) GameAudio.Instance?.PlayMapNodeLand(); });
                seq.Append(_avatar.DOJumpAnchorPos(p2, jumpPower: 70f, numJumps: 1, duration: 0.30f).SetEase(Ease.OutQuad));
                seq.AppendCallback(() => { if (_avatar != null) GameAudio.Instance?.PlayMapNodeLand(); });
                seq.Append(_avatar.DOJumpAnchorPos(target, jumpPower: 70f, numJumps: 1, duration: 0.30f).SetEase(Ease.OutQuad));
                seq.InsertCallback(STAR_DROP_DELAY, DropClearedStar);
                seq.OnComplete(() => onLand());
            };

            // 2026-07-29 order:
            //   1. coins burst from the cleared NODE and fly to the pill
            //   2. once they've all landed, the avatar sets off
            //   3. the star drops in shortly after it starts moving
            // The coins come from the node, NOT from the star — the star doesn't exist yet at
            // that point. RunPendingCoinCascade already falls back to NodeWorldPos when there's
            // no marker to fly from, which is exactly this case.
            // COINS_BEFORE_HOP = false restores the original (journey first, coins on landing).
            if (COINS_BEFORE_HOP)
            {
                // Point the cascade at the cleared node WITHOUT consuming _animateStarLevel —
                // DropClearedStar still needs it later to build the marker.
                _pendingCascadeLevel = _animateStarLevel;
                RunPendingCoinCascade(journey, COINS_FIRST_BEAT);
                return;
            }
            journey();
        }

        /// <summary>Drop the just-cleared level's star onto its node (celebration animation, level
        /// number over it) and record that node as the origin for the reward coins.</summary>
        private void DropClearedStar()
        {
            if (_animateStarLevel >= _windowStart && _animateStarLevel < _windowStart + VISIBLE_NODES)
            {
                BuildNodeMarker(NodeAnchoredPos(_animateStarLevel - _windowStart), _animateStarLevel, animate: true,
                                isTrophy: IsBossLevel(_animateStarLevel));
                _pendingCascadeLevel = _animateStarLevel; // this node is where the reward coins pop from
            }
            _animateStarLevel = -1; // consumed
        }

        /// <summary>Fire the pending reward cascade from the just-cleared node's star, then
        /// <paramref name="onDone"/>. No-ops straight through to onDone when there's no reward
        /// or no star node to fly from. Shared by both orderings (before-hop and on-landing)
        /// so the two paths can't drift apart. 2026-07-29.</summary>
        /// <param name="delay">Beat before the coins burst. The hop path schedules its own
        /// timing on the journey timeline and passes 0; OnHopLanded (no journey running) uses
        /// the default so the star still gets time to settle.</param>
        private void RunPendingCoinCascade(System.Action onDone, float delay = STAR_TO_COIN_BEAT)
        {
            int cascadeLevel = _pendingCascadeLevel;
            if (_pendingCoinReward > 0 && cascadeLevel >= _windowStart && cascadeLevel < _windowStart + VISIBLE_NODES)
            {
                _pendingCascadeLevel = -1;
                int reward = _pendingCoinReward; _pendingCoinReward = 0;
                var marker = _nodesRoot != null ? _nodesRoot.Find($"NodeMarker{cascadeLevel}") : null;
                Vector3 src = marker != null ? marker.position : NodeWorldPos(cascadeLevel);
                // Beat so the star has SETTLED before the coins burst out of it. The star's
                // drop is a 0.60s OutBounce (BuildNodeMarker), so anything under that fires
                // the coins mid-bounce. The old 0.30 worked only because two more hops ran
                // between the star drop and the cascade; in the new order nothing else fills
                // that gap.
                if (delay <= 0f) { SpawnCoinCascade(src, reward, onDone); return; }
                DOVirtual.DelayedCall(delay, () => SpawnCoinCascade(src, reward, onDone)).SetUpdate(true);
                return;
            }
            onDone();
        }

        // Avatar reached the node. Map-flow: auto-pop the play modal over the map. Standalone (FX test): wait for a tap.
        private void OnHopLanded()
        {
            int cascadeLevel = _pendingCascadeLevel; _pendingCascadeLevel = -1;
            _animateStarLevel = -1; // safety: the two-hop path already consumed it; this clears the same-node/no-hop paths
            if (_worldCompleteFlow) { OnWorldCompleteLanded(); return; } // boss node → trophy + Area Completed modal

            System.Action showNext = () => { if (_holdForIntro) ShowIntroOverMap(); else ReadyForTap(); };

            // Reward coins to fly? POP them from the just-cleared node's star into the coins pill, and only THEN
            // show the play modal (Spencer 2026-07-14: the goal modal waits for the coins to land). Small beat first
            // so the star has settled before the coins burst.
            // With COINS_BEFORE_HOP the cascade has already run and _pendingCoinReward is 0, so
            // this falls straight through. Kept live for the false path and for the no-hop /
            // same-node routes, which never pass through HopTo's split sequence.
            _pendingCascadeLevel = cascadeLevel;
            if (_pendingCoinReward > 0 && cascadeLevel >= _windowStart && cascadeLevel < _windowStart + VISIBLE_NODES)
            {
                RunPendingCoinCascade(showNext);
                return;
            }

            // No cascade (no reward, or no star node to fly from) — snap the pill to the real balance, then continue.
            if (_pendingCoinReward != 0) { _pendingCoinReward = 0; RefreshHud(); }
            showNext();
        }

        // ── Node star (level cleared) ───────────────────────────────────────────────
        private static readonly Color MAP_STAR_GOLD = new Color(1.00f, 0.84f, 0.25f, 1f);
        private static Sprite _mapStarSprite; private static bool _mapStarTried;
        private static Sprite LoadMapStarSprite()
        {
            if (_mapStarTried) return _mapStarSprite;
            _mapStarTried = true;
            _mapStarSprite = Resources.Load<Sprite>("Tiles/Icon_ImageIcon_Star01_On")
                          ?? Resources.Load<Sprite>("Particles/Star01");
            if (_mapStarSprite == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Tiles/Icon_ImageIcon_Star01_On")
                             ?? Resources.Load<Texture2D>("Particles/Star01");
                if (tex != null)
                    _mapStarSprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                   new Vector2(0.5f, 0.5f), 100f);
            }
            return _mapStarSprite;
        }

        private static Sprite _trophySprite; private static bool _trophyTried;
        private static Sprite LoadTrophySprite()
        {
            if (_trophyTried) return _trophySprite;
            _trophyTried = true;
            _trophySprite = Resources.Load<Sprite>("Tiles/Icon_ItemIcon_Trophy");
            if (_trophySprite == null)
            {
                Texture2D tex = Resources.Load<Texture2D>("Tiles/Icon_ItemIcon_Trophy");
                if (tex != null)
                    _trophySprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                                  new Vector2(0.5f, 0.5f), 100f);
            }
            return _trophySprite;
        }

        // ── Coin cascade (Royal Match / Candy Crush style) ──────────────────────────
        // Coins POP out of the just-cleared node's star, FAN out tightly, then STREAM (staggered) into the coins
        // pill — each arrival ticks the total up, punches the pill, and pings PlayCoinLand. When ALL coins have
        // landed, onComplete fires (the play modal waits on this). UI-space, unscaled time (the map runs while
        // gameplay is overlay-paused). 2026-07-14 Spencer.
        private static Sprite[] _coinFrames; private static bool _coinFramesTried;
        private static Sprite[] LoadCoinFrames()
        {
            if (_coinFramesTried) return _coinFrames;
            _coinFramesTried = true;

            // 2026-07-29: prefer the 3D crown coin — 8×4 / 32 frames of a full 360°
            // turn about the vertical axis, twice the frame count of the old sheet so
            // the spin reads smoothly at the ~25fps this plays back at
            // (SPIN_FPS 18 × fpsMul 1.4).
            // TO REVERT: delete Resources/Coins/coin3d_spin.png — this falls straight
            // back to the original 4×4 VFX_Coin_rotation with no code change.
            int COLS = 8, ROWS = 4;
            var tex = Resources.Load<Texture2D>("Coins/coin3d_spin");
            if (tex == null)
            {
                COLS = 4; ROWS = 4;
                tex = Resources.Load<Texture2D>("Coins/VFX_Coin_rotation");
            }
            if (tex == null) return null;
            int fw = tex.width / COLS, fh = tex.height / ROWS;
            _coinFrames = new Sprite[COLS * ROWS];
            int idx = 0;
            for (int r = 0; r < ROWS; r++)
                for (int c = 0; c < COLS; c++)
                {
                    int px = c * fw, py = (ROWS - 1 - r) * fh; // texture origin bottom-left; r=0 = top row
                    _coinFrames[idx++] = Sprite.Create(tex, new Rect(px, py, fw, fh), new Vector2(0.5f, 0.5f), 100f);
                }
            return _coinFrames;
        }

        /// <summary>Fly <paramref name="coins"/> reward coins from <paramref name="sourceWorld"/> (screen/world pos)
        /// into the coins pill, ticking the total up as they land, then fire <paramref name="onComplete"/> once the
        /// LAST coin arrives. Snaps + fires immediately under ReducedMotion / no target. 2026-07-14 Spencer.</summary>
        public void SpawnCoinCascade(Vector3 sourceWorld, int coins, System.Action onComplete)
        {
            if (coins <= 0 || _canvas == null || _coinsIconRT == null || UIAnimations.ReducedMotion)
            {
                _displayedCoins = CoinWallet.Balance;
                if (_coinsText != null) _coinsText.text = _displayedCoins.ToString();
                onComplete?.Invoke();
                return;
            }

            int N = Mathf.Clamp(14, 1, coins); // ~14 sprites = a rich stream (value split across them)
            _displayedCoins = Mathf.Max(0, CoinWallet.Balance - coins); // show PRE-reward total; ticks up to balance
            if (_coinsText != null) _coinsText.text = _displayedCoins.ToString();

            // Coin scatter on the BURST — fires with the fan-out, not on landing. 2026-07-29.
            GameAudio.Instance?.PlayCoinDisperse();

            _coinCascadeLanded = 0;
            _coinCascadeTotal  = N;
            _coinCascadeOnComplete = onComplete;

            int per = coins / N, remainder = coins - per * N;
            for (int i = 0; i < N; i++)
            {
                int share = per + (i == N - 1 ? remainder : 0);
                StartCoroutine(CoinCascadeCoroutine(sourceWorld, share, i));
            }

            // Spawn the floating "+N" AFTER the coins so it's the last sibling → renders ABOVE the coins. 2026-07-14.
            SpawnCoinRewardPopup(sourceWorld, coins);

            // Safety: if a coin coroutine dies (scene teardown), still fire onComplete so the play modal isn't lost.
            DOVirtual.DelayedCall(3.2f, () => { if (_coinCascadeOnComplete != null) FinishCoinCascade(); }).SetUpdate(true);
        }

        // ── Coin landing tunables (2026-07-29) ──────────────────────────────────
        /// <summary>End scale as the coin reaches the counter. Was 0.7 — visibly still a coin
        /// when it was destroyed. Revert to 0.7f with no fade for the old behaviour.</summary>
        /// <summary>Size of a flying coin in the cascade. Was hardcoded 46 in three places
        /// (container, shadow, coin). The spin sheet has 128px frames, so this can go to
        /// ~120 before it starts upscaling.</summary>
        /// <summary>true = reward coins bank between hop 1 and hops 2–3, so they land before the
        /// player travels on. false = the original order (complete the journey, then coins on
        /// landing). It cannot run earlier than hop 1: the coins fly from the cleared node's
        /// star, which only appears once the avatar hops off that node.</summary>
        private const bool  COINS_BEFORE_HOP = true;
        /// <summary>Gap between the star landing and the coins bursting from it. The star's
        /// drop-in is a 0.60s OutBounce, so keep this above that or the coins fire mid-bounce.</summary>
        private const float STAR_TO_COIN_BEAT = 0.70f;
        /// <summary>How long after the avatar starts hopping before the star begins dropping in.
        /// Must be > 0 so the star never appears while the avatar is still on the node.</summary>
        private const float STAR_DROP_DELAY = 0.12f;
        /// <summary>Pause between the map settling and the coins bursting out of the node — the
        /// first thing that happens in the reward sequence.</summary>
        private const float COINS_FIRST_BEAT = 0.20f;
        /// <summary>Pause after the map has finished fading in, before the hop/star/coin
        /// sequence starts. Gives the player a beat to register the screen first.</summary>
        private const float MAP_SETTLE_BEAT = 0.55f;
        /// <summary>Flying coin size, DERIVED from the pill icon so the two can't drift apart.
        /// Was a hardcoded 64 against a 50px icon — ~30% too big, which is why the coins read as
        /// noticeably larger in flight than the icon they land on. The 0.985 corrects for the
        /// spin sheet framing its coin at 84% of the frame vs the icon PNG's 83%. 2026-07-30.</summary>
        private const float COIN_FLY_SIZE_RATIO = 0.985f;  // spin sheet frames its coin at 84% vs the icon's 83%
        /// <summary>Pill icon sizes come from UIConfig so they're tunable in the Inspector; the
        /// consts below are the fallbacks if the asset is missing.</summary>
        private static float CoinPillIcon  => UIConfig.CoinPillSize;
        private static float HeartPillIcon => UIConfig.HeartPillSize;
        private static float CoinFlySize   => CoinPillIcon * COIN_FLY_SIZE_RATIO;
        /// <summary>Coin icon in the level-map HUD pill. Pill is 58 tall, so this is the
        /// practical ceiling before it crowds the pill. Hearts pill keeps the original 40.</summary>
        private const float COIN_PILL_ICON = 60f;
        /// <summary>Larger than COIN_PILL_ICON on purpose. preserveAspect fits the whole square
        /// frame into the box, and the heart's art fills only 74% of its frame vertically against
        /// the coin's 83% — so at an equal iconSize the heart draws 12% shorter and reads smaller.
        /// 56 equalises their drawn HEIGHT; the heart ends up slightly wider, which is correct for
        /// a heart. 2026-07-30.</summary>
        private const float HEART_PILL_ICON = 60f;
        // ── Coin burst: REAL ballistics ─────────────────────────────────────────
        // Every previous version lerped each coin to a random offset point. That can look
        // "spread out" but never reads as an explosion, because nothing accelerates — there's no
        // velocity and no gravity, so no arc. These are launch velocities and a gravity constant,
        // integrated per frame. All values are fractions of SCREEN HEIGHT per second, so the
        // burst is identical on any device.
        /// <summary>Upward launch speed (screen-heights/sec).</summary>
        private const float COIN_LAUNCH_UP_MIN = 0.60f;
        private const float COIN_LAUNCH_UP_MAX = 1.00f;
        /// <summary>Sideways launch speed, +/- (screen-heights/sec). Drives how wide it fans.</summary>
        private const float COIN_LAUNCH_SIDE   = 0.25f;
        /// <summary>Downward acceleration (screen-heights/sec^2). Higher = snappier, heavier arc.</summary>
        private const float COIN_GRAVITY       = 2.9f;
        /// <summary>How long the coin is under gravity before the counter pulls it in. Long enough
        /// that it passes its apex and is visibly FALLING when it gets collected.</summary>
        private const float COIN_BALLISTIC_DUR = 0.34f;
        /// <summary>Extra airtime per coin index, which staggers the returns into a stream. Applied
        /// as flight time, NOT as a pause — a paused coin reads as hanging midair.</summary>
        private const float COIN_STAGGER = 0.018f;
        /// <summary>Flipbook speed multipliers. 18fps x 3.0 = 54fps playback = ~1.7 revolutions/sec
        /// on the 32-frame sheet. Do not exceed ~3.0 or the spin aliases against the 60fps display
        /// and reads as juddering/reversing rather than fast.</summary>
        private const float COIN_SPIN_AIR  = 3.0f;
        private const float COIN_SPIN_HOME = 2.7f;
        private const float COIN_END_SCALE = 0.15f;
        /// <summary>Flight fraction at which the coin starts SHRINKING. Separate from the fade
        /// so the coin can visibly converge on the counter while still fully opaque.</summary>
        private const float COIN_SCALE_FROM = 0.78f;
        /// <summary>Flight fraction at which the coin starts fading. Deliberately very late —
        /// at 0.72 the coin was a visible ghost well before it reached the counter. These are
        /// UI RectTransforms on the map canvas, not world sprites, so there's no overlay to
        /// clip against and nothing forcing an early fade.</summary>
        private const float COIN_FADE_FROM = 0.93f;

        private System.Collections.IEnumerator CoinCascadeCoroutine(Vector3 sourceWorld, int share, int index)
            => CoinFlyRoutine(_canvas != null ? _canvas.transform : null, sourceWorld, _coinsIconRT, share, index, OnCoinCascadeLanded);

        /// <summary>THE home-screen coin flight (burst + gravity arc + shadow + 3D spin + magnet swoop + shrink-in), reusable on
        /// any overlay canvas with any target and landing callback. 2026-10-21 Spencer: the in-level bonus coins should use
        /// "the same animation as the homescreen when the coins fly up" — so both callers share this one routine. Run it with
        /// the CALLER's StartCoroutine; it only reads this panel's coin art and tuning.</summary>
        public System.Collections.IEnumerator CoinFlyRoutine(Transform parent, Vector3 sourceWorld, RectTransform target, int share, int index,
                                                             System.Action<int, Vector3> landed)
        {
            if (parent == null) { landed?.Invoke(share, sourceWorld); yield break; }
            // Container we animate (position + scale). A dark drop-SHADOW child sits behind an offset, and the COIN
            // child rides in front — so the shadow reads under the coin the whole flight.
            var go = new GameObject("CoinFly", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.transform.SetAsLastSibling(); // above the map + nodes
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(CoinFlySize, CoinFlySize);
            rt.position = sourceWorld;

            var frames = LoadCoinFrames();
            bool flip = frames != null && frames.Length > 0;
            Sprite frame0 = flip ? frames[0] : CoinIcon();

            var shadowGO = new GameObject("Shadow", typeof(RectTransform), typeof(Image));
            shadowGO.transform.SetParent(go.transform, false);
            var shRT = shadowGO.GetComponent<RectTransform>();
            shRT.anchorMin = shRT.anchorMax = new Vector2(0.5f, 0.5f);
            shRT.sizeDelta = new Vector2(CoinFlySize, CoinFlySize);
            shRT.anchoredPosition = new Vector2(3f, -6f) * (CoinFlySize / 46f); // soft offset down-right, scaled with the coin
            var shImg = shadowGO.GetComponent<Image>();
            shImg.sprite = frame0; shImg.color = new Color(0f, 0f, 0f, 0.32f);
            shImg.raycastTarget = false; shImg.preserveAspect = true;

            var coinGO = new GameObject("Coin", typeof(RectTransform), typeof(Image));
            coinGO.transform.SetParent(go.transform, false);
            var coinRT = coinGO.GetComponent<RectTransform>();
            coinRT.anchorMin = coinRT.anchorMax = new Vector2(0.5f, 0.5f);
            coinRT.sizeDelta = new Vector2(CoinFlySize, CoinFlySize);
            var img = coinGO.GetComponent<Image>();
            img.sprite = frame0; img.raycastTarget = false; img.preserveAspect = true;
            var coinHsv = UIConfig.CoinIconMaterial;   // tunable in UIConfig
            if (coinHsv != null) img.material = coinHsv;

            // Flipbook playback rate. CEILING: the sheet is 32 frames and the display runs at
            // 60fps, so past ~55fps playback the coin skips frames and strobes (it can even read
            // as rotating slowly backwards). SPIN_FPS x the largest multiplier below must stay
            // under that. 2026-07-30.
            const float SPIN_FPS = 18f; float spinT = 0f;
            void Spin(float fpsMul)
            {
                spinT += Time.unscaledDeltaTime;
                if (flip)
                {
                    var f = frames[Mathf.FloorToInt(spinT * SPIN_FPS * fpsMul) % frames.Length];
                    img.sprite = f; shImg.sprite = f;
                }
                else coinRT.Rotate(0f, 0f, -560f * fpsMul * Time.unscaledDeltaTime); // shadow stays put
            }

            // Phase 1: BALLISTIC BURST. Launch each coin with its own velocity and let gravity
            // integrate it — shoots out, arcs over, and is visibly FALLING by the time the counter
            // takes over. No hang needed: the apex IS the beat.
            float sh = Screen.height;
            Vector2 vel = new Vector2(UnityEngine.Random.Range(-COIN_LAUNCH_SIDE, COIN_LAUNCH_SIDE),
                                      UnityEngine.Random.Range(COIN_LAUNCH_UP_MIN, COIN_LAUNCH_UP_MAX)) * sh;
            float grav = COIN_GRAVITY * sh;
            Vector3 pos = sourceWorld;
            const float POP_DUR = 0.12f;
            // Stagger is folded INTO the flight time instead of being a wait afterwards, so a
            // coin keeps arcing under gravity until its turn comes rather than freezing in place.
            // The old `WaitForSecondsRealtime(index * 0.035f)` after this loop is what read as a
            // long midair pause — the last coin hung motionless for ~0.46s. 2026-07-30.
            float flightDur = COIN_BALLISTIC_DUR + index * COIN_STAGGER;
            float t = 0f;
            while (t < flightDur && go != null)
            {
                float dt = Time.unscaledDeltaTime;
                t += dt;
                vel.y -= grav * dt;                 // gravity
                pos += (Vector3)(vel * dt);         // integrate
                rt.position = pos;
                float sc = Mathf.Clamp01(t / POP_DUR);
                float ob = 1f + 2.7f * Mathf.Pow(sc - 1f, 3f) + 1.7f * Mathf.Pow(sc - 1f, 2f); // OutBack pop
                rt.localScale = Vector3.one * ob;
                Spin(COIN_SPIN_AIR);                 // fast tumble through the air
                yield return null;
            }
            if (go != null) rt.localScale = Vector3.one;

            // No wait here — the stagger already happened as extra airtime above.

            // Phase 2: HOME to the counter — bezier arc, ease-IN "magnet suck".
            Vector3 from = go != null ? rt.position : sourceWorld;
            Vector3 tgt  = target != null ? target.position : from;
            Vector3 mid  = Vector3.Lerp(from, tgt, 0.5f);
            Vector3 pathDir = (tgt - from).sqrMagnitude > 0.001f ? (tgt - from).normalized : Vector3.up;
            Vector3 perp = new Vector3(-pathDir.y, pathDir.x, 0f);
            Vector3 control = mid + perp * (Screen.width * UnityEngine.Random.Range(-0.05f, 0.05f)) + Vector3.up * (Screen.height * 0.03f);
            float e = 0f, homeDur = UnityEngine.Random.Range(0.62f, 0.78f); // slower travel to the counter (Spencer 2026-07-14)
            while (e < homeDur && go != null)
            {
                e += Time.unscaledDeltaTime;
                float p = Mathf.Clamp01(e / homeDur);
                float ec = p * p * p, u = 1f - ec; // ease-in cubic
                rt.position = (u * u) * from + (2f * u * ec) * control + (ec * ec) * tgt;
                // Coins hold FULL SIZE for the whole flight and only collapse inside the landing
                // window — the same window the fade uses — so they shrink INTO the counter
                // instead of dwindling the entire way there.
                // (Was Lerp(1, COIN_END_SCALE, p), i.e. shrinking across the full flight.)
                float shrink = Mathf.InverseLerp(COIN_SCALE_FROM, 1f, p);  // converge on the counter
                rt.localScale = Vector3.one * Mathf.Lerp(1f, COIN_END_SCALE, shrink);
                float land = Mathf.InverseLerp(COIN_FADE_FROM, 1f, p);     // wink out, right at the end
                if (land > 0f)
                {
                    float a = 1f - land;
                    var ic = img.color;   img.color   = new Color(ic.r, ic.g, ic.b, a);
                    var sc = shImg.color; shImg.color = new Color(sc.r, sc.g, sc.b, 0.32f * a);
                }
                Spin(COIN_SPIN_HOME);
                yield return null;
            }
            Vector3 landPos = go != null ? rt.position : tgt;
            if (go != null) Destroy(go);
            landed?.Invoke(share, landPos);
        }

        private void OnCoinCascadeLanded(int share, Vector3 landPos)
        {
            _displayedCoins += share;
            if (_coinsText != null) _coinsText.text = _displayedCoins.ToString();
            if (_coinsIconRT != null)
            {
                _coinsIconRT.DOKill(); _coinsIconRT.localScale = Vector3.one;
                _coinsIconRT.DOPunchScale(Vector3.one * 0.35f, 0.18f, 7, 0.7f).SetUpdate(true); // pill bumps on each hit
            }
            SpawnCoinHitFX(_coinsIconRT != null ? _coinsIconRT.position : landPos); // additive glow + sparkle burst
            GameAudio.Instance?.PlayCoinLand(); // rate-capped rising-pitch ting
            _coinCascadeLanded++;
            if (_coinCascadeLanded >= _coinCascadeTotal) FinishCoinCascade();
        }

        // A gold additive-glow flash + a few sparkles at the coin counter each time a coin lands (Royal-Match "the
        // counter sparkles as coins pour in"). 2026-07-14 Spencer.
        private void SpawnCoinHitFX(Vector3 worldPos) => SpawnCoinHitFXOn(_canvas != null ? _canvas.transform : null, worldPos);

        /// <summary>The home-screen coin landing glow + sparkle, on any overlay canvas (bonus REWARD panel too).</summary>
        public void SpawnCoinHitFXOn(Transform parent, Vector3 worldPos)
        {
            if (parent == null) return;
            var mat = LoadMapAdditiveMat();

            var glowGO = new GameObject("CoinHitGlow", typeof(RectTransform), typeof(Image));
            glowGO.transform.SetParent(parent, false);
            glowGO.transform.SetAsLastSibling();
            var grt = glowGO.GetComponent<RectTransform>();
            grt.sizeDelta = new Vector2(88f, 88f); grt.position = worldPos;
            var gimg = glowGO.GetComponent<Image>();
            gimg.sprite = LoadMapGlowSprite(); if (mat != null) gimg.material = mat;
            gimg.color = new Color(1f, 0.9f, 0.45f, 0.9f); gimg.raycastTarget = false; gimg.preserveAspect = true;
            grt.localScale = Vector3.one * 0.4f;
            var gseq = DOTween.Sequence().SetUpdate(true);
            gseq.Append(grt.DOScale(1.1f, 0.12f).SetEase(Ease.OutCubic));
            gseq.Join(gimg.DOFade(0f, 0.28f).SetEase(Ease.InQuad));
            gseq.OnComplete(() => { if (glowGO != null) Destroy(glowGO); });

            var flare = LoadMapFlareSprite();
            for (int i = 0; i < 3; i++)
            {
                var sGO = new GameObject("CoinSpark", typeof(RectTransform), typeof(Image));
                sGO.transform.SetParent(parent, false);
                sGO.transform.SetAsLastSibling();
                var srt = sGO.GetComponent<RectTransform>();
                srt.sizeDelta = new Vector2(26f, 26f); srt.position = worldPos;
                var simg = sGO.GetComponent<Image>();
                simg.sprite = flare; if (mat != null) simg.material = mat;
                simg.color = new Color(1f, 0.95f, 0.6f, 1f); simg.raycastTarget = false; simg.preserveAspect = true;
                float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                float dist = Screen.height * UnityEngine.Random.Range(0.015f, 0.035f);
                Vector3 dst = worldPos + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f) * dist;
                srt.localScale = Vector3.one * 0.2f;
                var sq = DOTween.Sequence().SetUpdate(true);
                sq.Append(srt.DOScale(1f, 0.12f).SetEase(Ease.OutBack, 2f));
                sq.Join(sGO.transform.DOMove(dst, 0.34f).SetEase(Ease.OutCubic));
                sq.Insert(0.12f, simg.DOFade(0f, 0.24f).SetEase(Ease.InQuad));
                sq.OnComplete(() => { if (sGO != null) Destroy(sGO); });
            }
        }

        // Floating "+N" coin popup (a.k.a. floating combat text / score popup) — pops in over the star, rises a
        // little, and fades out, showing the coins earned. Mirrors the in-game BonusPopup but in UI space for the
        // map. 2026-07-14 Spencer.
        private void SpawnCoinRewardPopup(Vector3 worldPos, int amount)
        {
            if (_canvas == null || amount <= 0 || UIAnimations.ReducedMotion) return;
            var go = new GameObject("CoinRewardPopup", typeof(RectTransform), typeof(CanvasGroup));
            go.transform.SetParent(_canvas.transform, false);
            go.transform.SetAsLastSibling();
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(240f, 80f);
            rt.position = worldPos + Vector3.up * (Screen.height * 0.025f); // just above the star
            var cg = go.GetComponent<CanvasGroup>();

            string txt = $"+{amount}";

            // Use the SAME painted digit sprites as the in-game score pops. These were built
            // from Spencer's Photoshop text stack (Assets/Resources/Digits), and the map's
            // coin popups were plain TMP text — two different number styles in one game.
            // Falls back to the TMP pair if the digit art is missing. 2026-10-19 Spencer.
            // MATCH THE IN-GAME POPS. 2026-10-19 Spencer: "about the same size on the level
            // map". The board pops are sized in WORLD units (ClusterOutline.DIGIT_CELL_WORLD)
            // and these are in CANVAS px, so derive one from the other instead of hardcoding a
            // number that drifts every time he tunes the other. The game camera is orthographic,
            // so 1 world unit = canvasHeight / (2 * orthographicSize) px. At the shipped
            // orthographicSize 10 on a 2556-tall canvas that is 127.8 px per unit, making the
            // current 0.40 come out at ~51px — the hardcoded 64 had been reading noticeably
            // LARGER than the in-game number it was supposed to match.
            float coinDigitPx = 64f;                                  // fallback
            var gameCam = Camera.main;
            if (gameCam != null && gameCam.orthographic && gameCam.orthographicSize > 0.01f)
            {
                float canvasH = ((RectTransform)_canvas.transform).rect.height;
                // × POP_GROW: the in-game pop lays its digits out at DIGIT_CELL_WORLD and then
                // DOScale()s to POP_GROW, so what the player SEES is the product. The map pop
                // scales 0 -> 1 and stops, so without this it ended up 1.8x too small — which
                // is exactly how much too small it looked. 2026-10-19 Spencer.
                coinDigitPx = ClusterOutline.DIGIT_CELL_WORLD * ClusterOutline.POP_GROW
                              * (canvasH / (2f * gameCam.orthographicSize));
            }
            float COIN_DIGIT_PX = coinDigitPx;
            var shadowRT = new GameObject("Shadow", typeof(RectTransform)).GetComponent<RectTransform>();
            shadowRT.SetParent(rt, false);
            shadowRT.anchorMin = shadowRT.anchorMax = new Vector2(0.5f, 0.5f);
            shadowRT.anchoredPosition = new Vector2(3f, -4f);
            bool sprites = ClusterOutline.BuildSpriteNumberUI(shadowRT, txt, COIN_DIGIT_PX,
                                                              new Color(0.15f, 0.09f, 0.02f, 0.55f));
            if (sprites)
            {
                var mainRT = new GameObject("Digits", typeof(RectTransform)).GetComponent<RectTransform>();
                mainRT.SetParent(rt, false);
                mainRT.anchorMin = mainRT.anchorMax = new Vector2(0.5f, 0.5f);
                mainRT.anchoredPosition = Vector2.zero;
                ClusterOutline.BuildSpriteNumberUI(mainRT, txt, COIN_DIGIT_PX, Color.white);
            }
            else
            {
                if (shadowRT != null) Destroy(shadowRT.gameObject);
                MakePopupLabel(rt, txt, new Vector2(3f, -4f), new Color(0.15f, 0.09f, 0.02f, 1f)); // dark shadow
                MakePopupLabel(rt, txt, Vector2.zero,          Color.white);                        // white main
            }

            rt.localScale = Vector3.zero;
            float riseY = rt.position.y + Screen.height * 0.075f;                     // travels up more, then fades
            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Append(rt.DOScale(1f, 0.18f).SetEase(Ease.OutBack, 2.2f));            // pop in
            seq.Join(rt.DOMoveY(riseY, 1.4f).SetEase(Ease.OutCubic));                 // rise (slow)
            seq.Insert(0.7f, cg.DOFade(0f, 0.6f).SetEase(Ease.InQuad));               // fade out
            seq.OnComplete(() => { if (go != null) Destroy(go); });
        }

        private static void MakePopupLabel(RectTransform parent, string text, Vector2 offset, Color color)
        {
            var go = new GameObject("Lbl", typeof(RectTransform), typeof(TextMeshProUGUI));
            go.transform.SetParent(parent, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = offset; rt.offsetMax = offset;
            var tmp = go.GetComponent<TextMeshProUGUI>();
            var f = GameFont.GetDisplayTMP(); if (f != null) tmp.font = f;
            tmp.text = text; tmp.fontSize = 50f; tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = color; tmp.fontStyle = FontStyles.Bold; tmp.raycastTarget = false; tmp.enableWordWrapping = false;
        }

        // ── Coin-FX asset loaders (same sources StageClearModal / WorldCompleteModal use) ──
        private static Sprite MapSpriteFrom(string path)
        {
            var s = Resources.Load<Sprite>(path);
            if (s != null) return s;
            var tex = Resources.Load<Texture2D>(path);
            return tex != null ? Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f) : null;
        }
        private static Sprite _mapGlow; private static bool _mapGlowTried;
        private static Sprite LoadMapGlowSprite()
        { if (!_mapGlowTried) { _mapGlowTried = true; _mapGlow = MapSpriteFrom("Particles/Star02") ?? MapSpriteFrom("Particles/soft_circle"); } return _mapGlow; }
        private static Sprite _mapFlare; private static bool _mapFlareTried;
        private static Sprite LoadMapFlareSprite()
        { if (!_mapFlareTried) { _mapFlareTried = true; _mapFlare = MapSpriteFrom("Particles/flare") ?? MapSpriteFrom("Particles/point1") ?? LoadMapGlowSprite(); } return _mapFlare; }
        private static Material _mapAddMat; private static bool _mapAddMatTried;
        private static Material LoadMapAdditiveMat()
        { if (!_mapAddMatTried) { _mapAddMatTried = true; var sh = Shader.Find("WordDrop/AdditiveSprite") ?? Shader.Find("Sprites/Default"); if (sh != null) _mapAddMat = new Material(sh); } return _mapAddMat; }

        private void FinishCoinCascade()
        {
            if (_coinCascadeOnComplete == null) return;
            var cb = _coinCascadeOnComplete; _coinCascadeOnComplete = null;
            _displayedCoins = CoinWallet.Balance;                       // snap to exact balance
            if (_coinsText != null) _coinsText.text = _displayedCoins.ToString();
            cb.Invoke();
        }

        /// <summary>The reward earned on the just-cleared level — flies into the coins pill when the star lands on
        /// the map. Set by SurvivalManager at level clear; consumed once by the cascade. 2026-07-14 Spencer.</summary>
        public void SetPendingCoinReward(int coins) => _pendingCoinReward = Mathf.Max(0, coins);
        public void AddPendingCoinReward(int coins) => _pendingCoinReward = Mathf.Max(0, _pendingCoinReward + coins);   // bonus blast

        // ── Tutorial vs Areas (2026-07-14 Spencer) ──────────────────────────────────
        // Levels 1..TUTORIAL_LEVELS are the ONE-TIME tutorial — NOT a numbered Area. The run proper begins at the
        // level after it, which the player sees as "AREA 1, level 1". Internal stage indices never move (LevelTable /
        // difficulty stay put); everything player-facing goes through these transforms.
        public const int TUTORIAL_LEVELS = VISIBLE_NODES;                       // 1..10 = tutorial
        public static bool IsTutorialLevel(int level) => level > 0 && level <= TUTORIAL_LEVELS;
        public static int  RunLevel(int level) => level - TUTORIAL_LEVELS;      // internal → run/leaderboard level (tutorial ≤ 0)
        public static int  DisplayNum(int level) => IsTutorialLevel(level) ? level : RunLevel(level); // node/trophy number

        // Boss = last node of an AREA (run level 10, 20, 30 … = internal 20, 30, 40 …) → TROPHY marker + Area Completed
        // flow. The tutorial's last level (internal 10) is NOT a boss. 2026-07-14 Spencer.
        public static bool IsBossLevel(int level) => level > TUTORIAL_LEVELS && level % VISIBLE_NODES == 0;

        private const float NODE_STAR_SIZE = NODE_SIZE * 1.6f; // ~1.6× a node; the level number sits centered over it
        private const float STAR_ART_NUDGE = NODE_STAR_SIZE * 0.10f; // the star sprite's visual centre sits below its
                                                                     // bbox centre → lift the art up so it reads centred
                                                                     // on the node (number is pushed back down to centre)

        /// <summary>Build a golden marker (~1.6× node size) centred on the node at <paramref name="anchoredPos"/> with
        /// the level number <paramref name="levelNum"/> over it (SAME font + size as a node number). A STAR for normal
        /// levels, a TROPHY for boss (world-ending) levels. When <paramref name="animate"/> is true it plays the Well
        /// Done! modal's hero drop-in (big + tilted + transparent → scales down with an OutBounce settle, rotates
        /// upright, fades in); otherwise it appears instantly at rest — a PERSISTENT marker on a cleared level.
        /// 2026-07-13 Spencer.</summary>
        private void BuildNodeMarker(Vector2 anchoredPos, int levelNum, bool animate, bool isTrophy)
        {
            var sprite = isTrophy ? LoadTrophySprite() : LoadMapStarSprite();
            if (sprite == null || _nodesRoot == null) return;
            float nudge = isTrophy ? 0f : STAR_ART_NUDGE; // trophy sits centred in its texture; the star needs a lift

            var starGO = new GameObject($"NodeMarker{levelNum}", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
            starGO.transform.SetParent(_nodesRoot, false);
            var rt = starGO.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = anchoredPos + new Vector2(0f, nudge); // lift so the art reads centred on the node
            rt.sizeDelta = new Vector2(NODE_STAR_SIZE, NODE_STAR_SIZE);
            rt.SetAsLastSibling();

            var img = starGO.GetComponent<Image>();
            img.sprite = sprite;
            img.preserveAspect = true;
            img.raycastTarget = false;
            img.color = isTrophy ? Color.white : MAP_STAR_GOLD; // trophy shows its own gold; star is tinted

            // Level number OVER the marker (child → scales/rotates/fades WITH it via the CanvasGroup). The art is
            // lifted by nudge, so push the number back DOWN by the same amount → it lands at the node centre.
            var numGO = new GameObject("MarkerNum", typeof(RectTransform), typeof(TextMeshProUGUI));
            numGO.transform.SetParent(starGO.transform, false);
            var nrt = numGO.GetComponent<RectTransform>();
            nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
            nrt.offsetMin = Vector2.zero; nrt.offsetMax = Vector2.zero;
            nrt.anchoredPosition = new Vector2(0f, -nudge);
            var ntmp = numGO.GetComponent<TextMeshProUGUI>();
            var nfont = GameFont.GetUITMP(); if (nfont != null) ntmp.font = nfont; // SAME font as the node number
            ntmp.text = DisplayNum(levelNum).ToString(); // run level in Areas (tutorial shows 1..10)
            ntmp.fontSize = 27f;                     // SAME size as the node number
            ntmp.alignment = TextAlignmentOptions.Center;
            ntmp.color = HUD_INK;                    // SAME dark ink the node uses on a bright fill
            ntmp.raycastTarget = false;

            var cg = starGO.GetComponent<CanvasGroup>();

            // Static (persistent star) OR reduced-motion → appear at rest, no drop animation.
            if (!animate || UIAnimations.ReducedMotion)
            {
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;
                cg.alpha = 1f;
                if (_avatar != null) _avatar.SetAsLastSibling();
                if (animate) GameAudio.Instance?.PlayPersonalBest(); // reduced-motion just-cleared star still gets the sting
                return;
            }

            // Starts BIG (as if close to the camera) + tilted + transparent, then scales DOWN with a bounce and
            // rotates upright — identical feel to the modal hero star. CanvasGroup fades star + number together.
            rt.localScale = Vector3.one * 2.4f;
            rt.localRotation = Quaternion.Euler(0f, 0f, -70f);
            cg.alpha = 0f;

            if (_avatar != null) _avatar.SetAsLastSibling(); // avatar stays on top

            var seq = DOTween.Sequence().SetUpdate(true);
            seq.Join(rt.DOScale(1f, 0.60f).SetEase(Ease.OutBounce));
            seq.Join(rt.DORotate(Vector3.zero, 0.50f, RotateMode.Fast).SetEase(Ease.OutCubic));
            seq.Join(cg.DOFade(1f, 0.18f).SetEase(Ease.OutCubic));
            seq.InsertCallback(0.10f, () => GameAudio.Instance?.PlayPersonalBest());
        }


        // ── HOME SCREEN ────────────────────────────────────────────────────────────

        private void ShowHome(int toLevel)
        {
            _homeLevel = toLevel;
            if (_nodesRoot != null) _nodesRoot.gameObject.SetActive(false);
            if (_avatar    != null) _avatar.gameObject.SetActive(false);
            if (_tapPrompt != null) _tapPrompt.alpha = 0f;
            if (_worldTitle != null) _worldTitle.gameObject.SetActive(false);

            EnsurePlayButton();
            LayoutPlayButton();
            // SECOND PASS NEXT FRAME. The anchors make the width right whenever the layout
            // resolves, but the height is derived from that resolved width — so it has to be
            // read after the layout system has run at least once.
            StartCoroutine(LayoutPlayButtonDeferred());
            string lvlText = $"LEVEL {DisplayNum(toLevel)}";
            if (_playBtnLabel  != null) _playBtnLabel.text  = lvlText;
            if (_playBtnShadow != null) _playBtnShadow.text = lvlText;   // twin must track it

            RefreshHud();
            GameAudio.Instance?.PlayLevelMapMusic();
            _canvas.gameObject.SetActive(true);
            _group.alpha = 0f;
            _group.blocksRaycasts = true;

            _group.DOKill();
            _group.DOFade(1f, 0.30f).SetUpdate(true).OnComplete(() =>
            {
                // The button is the only thing to press, so it must not be live until the
                // screen has actually arrived — a tap landing during the fade reads as the
                // press being eaten. Same reason the map gated its node taps behind the fade.
                //
                // DO NOT CLEAR _busy HERE. 2026-10-20: it looked like a harmless "we're done
                // presenting" flag, but IsShowing => _busy, and HUDManager.PlayLevelEntry uses
                // IsShowing to decide whether to run the board entry itself or leave it to the
                // panel's fade-out. Clearing it early made that guard miss, so the entrance
                // played immediately AND again after the fade — Spencer saw the board slide in
                // twice. _busy means "this panel owns the screen" and is released by FadeMapOut.
                if (_playBtnRT != null)
                {
                    var b = _playBtnRT.GetComponent<Button>();
                    if (b != null) b.interactable = true;
                    PulsePlayButton();
                }
                RunHomeCoinCascade();
            });
        }

        /// <summary>The big centre button. Layered to the reference Spencer supplied, MEASURED
        /// off it rather than eyeballed (the plate there is 512x183 inside a 24px navy border,
        /// i.e. 47.5% of screen width at ~2.4:1, corner radius ~17% of height):
        ///   navy outer plate  ->  green face with a vertical gradient  ->  cream label.
        /// The gradient is a real 1xN texture, not a flat fill: the reference runs #ADD761 at
        /// the top to #478226 at the bottom with darker lips top and bottom, and a flat green
        /// is the single thing that makes a button like this read as programmer art.</summary>
        private void EnsurePlayButton()
        {
            if (_playBtnRT != null) return;

            var go = new GameObject("PlayButton", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(_canvas.transform, false);
            _playBtnRT = go.GetComponent<RectTransform>();
            _playBtnRT.anchorMin = _playBtnRT.anchorMax = new Vector2(0.5f, 0.5f);
            _playBtnRT.pivot     = new Vector2(0.5f, 0.5f);

            LayoutPlayButton();

            float h = PlayButtonSize().y;   // provisional; LayoutPlayButton re-applies on every show
            var navy = go.GetComponent<Image>();
            navy.sprite = MenuUI.GetRoundedRectSprite(Mathf.RoundToInt(h * NAVY_RADIUS));
            navy.type   = Image.Type.Sliced;
            navy.color  = new Color32(0x2F, 0x3C, 0x96, 0xFF);   // measured navy border

            // Green face, inset by the measured border (24px on a 231px-tall button = 10.4%).
            float border = h * BORDER_FRAC;
            var faceGO = new GameObject("Face", typeof(RectTransform), typeof(Image));
            faceGO.transform.SetParent(go.transform, false);
            var fRT = _playFaceRT = faceGO.GetComponent<RectTransform>();
            fRT.anchorMin = Vector2.zero; fRT.anchorMax = Vector2.one;
            fRT.offsetMin = new Vector2(border, border);
            fRT.offsetMax = new Vector2(-border, -border);
            var fImg = faceGO.GetComponent<Image>();
            int faceH = Mathf.Max(8, Mathf.RoundToInt(h - border * 2f));
            fImg.sprite = BuildGreenSprite(faceH, RING_TOP, RING_MID, RING_BOT, lip: true, radiusFrac: RING_RADIUS);
            fImg.type   = Image.Type.Sliced;
            fImg.raycastTarget = false;

            // INNER PANEL. Measured across the reference: a DARKER ring about 30px wide on a
            // 512px plate (5.9% of its width), then a 2-3px bright rim, then a BRIGHTER inner
            // panel. I had this inverted on the first pass from a slice that happened to land in
            // the ring — the ring runs #87BD45 down to #39731E, the panel #BEE870 down to
            // #478824, so the panel is the light one. Two plates, not one gradient: that split
            // is what makes it read as a moulded button instead of a coloured rectangle.
            float inset = faceH * RING_INSET;
            var rimGO = new GameObject("Rim", typeof(RectTransform), typeof(Image));
            rimGO.transform.SetParent(faceGO.transform, false);
            var rRT = _playRimRT = rimGO.GetComponent<RectTransform>();
            rRT.anchorMin = Vector2.zero; rRT.anchorMax = Vector2.one;
            rRT.offsetMin = new Vector2(inset - faceH * 0.016f, inset - faceH * 0.016f);
            rRT.offsetMax = new Vector2(-(inset - faceH * 0.016f), -(inset - faceH * 0.016f));
            var rImg = rimGO.GetComponent<Image>();
            rImg.sprite = MenuUI.GetRoundedRectSprite(Mathf.RoundToInt(faceH * RING_RADIUS));
            rImg.type = Image.Type.Sliced;
            rImg.color = new Color32(0xB7, 0xE4, 0x6D, 0xFF);   // the measured rim highlight
            rImg.raycastTarget = false;

            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(faceGO.transform, false);
            var pRT2 = _playPanelRT = panelGO.GetComponent<RectTransform>();
            pRT2.anchorMin = Vector2.zero; pRT2.anchorMax = Vector2.one;
            pRT2.offsetMin = new Vector2(inset, inset);
            pRT2.offsetMax = new Vector2(-inset, -inset);
            var pImg = panelGO.GetComponent<Image>();
            pImg.sprite = BuildGreenSprite(Mathf.Max(8, Mathf.RoundToInt(faceH - inset * 2f)),
                                           PANEL_TOP, PANEL_MID, PANEL_BOT, lip: false,
                                           radiusFrac: PANEL_RADIUS * faceH / Mathf.Max(1f, faceH - inset * 2f));
            pImg.type = Image.Type.Sliced;
            pImg.raycastTarget = false;

            // DARK TWIN UNDERNEATH, NOT A TMP OUTLINE. An outline writes to the label's
            // fontMaterial, and that is precisely what threw a NullReference earlier today and
            // stopped the level map opening at all — reordering it after the font assignment was
            // not enough and it had to come out. A second label offset down a couple of pixels
            // gets the same separation from the light top of the gradient and touches no
            // material. 2026-10-20.
            var shadow = BuildLabel(panelGO.transform, "LEVEL 1",
                                    new Color32(0x2C, 0x4A, 0x12, 0xFF), h * 0.34f);
            shadow.name = "LabelShadow";
            var sRT = shadow.rectTransform;
            sRT.anchorMin = Vector2.zero; sRT.anchorMax = Vector2.one;
            sRT.offsetMin = new Vector2(0f, -h * 0.035f);
            sRT.offsetMax = new Vector2(0f, -h * 0.035f);
            _playBtnShadow = shadow;

            _playBtnLabel = BuildLabel(panelGO.transform, "LEVEL 1",
                                       new Color32(0xF7, 0xFA, 0xCF, 0xFF), h * 0.34f);
            _playBtnLabel.alignment = TextAlignmentOptions.Center;
            var lRT = _playBtnLabel.rectTransform;
            lRT.anchorMin = Vector2.zero; lRT.anchorMax = Vector2.one;
            lRT.offsetMin = Vector2.zero; lRT.offsetMax = Vector2.zero;

            var btn = go.GetComponent<Button>();
            btn.targetGraphic = navy;
            btn.interactable  = false;      // armed once the screen has faded in
            btn.onClick.AddListener(OnPlayPressed);
        }

        // ── BUTTON GEOMETRY, SOLVED AGAINST THE REFERENCE ──────────────────────────
        // 2026-10-20. The first pass picked these by eye and the result read wrong: the inner
        // panel looked like a rounded blob floating inside a blue frame. Measuring the
        // reference gave the ring width at the straight edge (26px) and across the corner
        // diagonal (40px) on a 231px button; a short search over candidate radii reproduces
        // both to within 0.0007. The panel radius was the whole problem — eyeballed at 0.20 of
        // the face height when it is really 0.026, nearly 8x too round, which is exactly what
        // balloons the ring at the corners and detaches the panel from its frame.
        const float NAVY_RADIUS  = 0.208f;   // of BUTTON height (was 0.170 by eye)
        const float BORDER_FRAC  = 0.104f;   // of BUTTON height — this one was already right
        const float RING_RADIUS  = 0.170f;   // of FACE height (was 0.200)
        const float RING_INSET   = 0.145f;   // of FACE height (was 0.165)
        // 0.026 is the strict nested solution (face radius minus the inset), which keeps the
        // ring perfectly even but leaves the panel corners almost square. The reference's panel
        // is visibly softer than that, so this is nudged up — the ring thickens a little at the
        // corners, which is what the reference does too (26px of ring on the straight edge
        // against 40px across the diagonal).
        const float PANEL_RADIUS = 0.075f;   // of FACE height (was 0.200 by eye — the bug)

        // MEASURED off the reference button, two separate vertical gradients.
        static readonly Color32 RING_TOP  = new Color32(0x87, 0xBD, 0x45, 0xFF);
        static readonly Color32 RING_MID  = new Color32(0x56, 0x95, 0x2A, 0xFF);
        static readonly Color32 RING_BOT  = new Color32(0x39, 0x73, 0x1E, 0xFF);
        static readonly Color32 PANEL_TOP = new Color32(0xBE, 0xE8, 0x70, 0xFF);
        static readonly Color32 PANEL_MID = new Color32(0x90, 0xCB, 0x3F, 0xFF);
        static readonly Color32 PANEL_BOT = new Color32(0x47, 0x88, 0x24, 0xFF);

        /// <summary>Vertical gradient as a 1xN sliced sprite. Nine-slice borders keep the rounded
        /// corners from stretching while the gradient spans the full height at any button size.
        /// A flat fill is the single thing that makes a button like this read as programmer art,
        /// so both plates get a real ramp.</summary>
        private static Sprite BuildGreenSprite(int height, Color32 top, Color32 mid, Color32 bot, bool lip, float radiusFrac)
        {
            int r = Mathf.Max(1, Mathf.RoundToInt(height * radiusFrac));
            var tex = new Texture2D(1, height, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < height; y++)
            {
                float t = 1f - (y / (float)(height - 1));   // texture y=0 is the BOTTOM
                Color c = t < 0.5f ? Color.Lerp(bot, mid, t / 0.5f)
                                   : Color.Lerp(mid, top, (t - 0.5f) / 0.5f);
                if (lip && y >= height - 2) c = new Color32(0x36, 0x5A, 0x1A, 0xFF); // dark top lip
                if (lip && y <= 1)          c = new Color32(0x33, 0x6C, 0x1B, 0xFF); // dark bottom lip
                tex.SetPixel(0, y, c);
            }
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, 1, height), new Vector2(0.5f, 0.5f), 100f, 0,
                                 SpriteMeshType.FullRect, new Vector4(0, r, 0, r));
        }


        /// <summary>The button's size, as a fraction of the CANVAS — never of Screen.*.
        ///
        /// 2026-10-20: the first version read ((RectTransform)_canvas.transform).rect at BUILD
        /// time. The canvas is ScaleWithScreenSize against a 540x960 reference, and on the frame
        /// the panel is created the scaler has not resolved yet, so that rect still reported raw
        /// screen pixels (1179). 47.5% of 1179 = 560 UNITS in a ~443-unit-wide canvas — the
        /// button came out wider than the screen, which is why it read as a full-width blue bar
        /// with a green strip in it. This is the same first-frame trap documented for modals:
        /// anything measured in the frame the UI is built is measured against an unresolved
        /// layout. Re-applied on every ShowHome so it is correct even if the first read is not.</summary>
        private const float PLAY_W_FRAC = 0.475f;   // of canvas width  — measured off the reference
        private const float PLAY_ASPECT = 2.42f;    // w:h              — measured off the reference
        private const float PLAY_Y_FRAC = 0.26f;    // below centre, as a fraction of canvas height

        private Vector2 PlayButtonSize()
        {
            float w = _playBtnRT != null && _playBtnRT.rect.width > 1f
                ? _playBtnRT.rect.width                                   // resolved by the anchors
                : ((RectTransform)_canvas.transform).rect.width * PLAY_W_FRAC;
            if (w <= 1f) w = 540f * PLAY_W_FRAC;                          // nothing resolved yet
            return new Vector2(w, w / PLAY_ASPECT);
        }

        /// <summary>WIDTH COMES FROM ANCHORS, NOT FROM A MEASUREMENT. 2026-10-20, second attempt:
        /// reading the canvas rect and deferring was still wrong, because ShowHome runs in the
        /// same frame EnsureUI creates the canvas and the scaler has not resolved even by the
        /// end of it — the button came out wider than the screen and sat off the bottom as a
        /// navy band. Anchors are evaluated by the layout system whenever it resolves, so
        /// pinning anchorMin/Max to 0.2625..0.7375 makes the width correct by construction at
        /// any resolution, with no timing to get wrong. Only the HEIGHT needs a real number
        /// (it is derived from the resolved width), and that is re-applied next frame.</summary>
        private void LayoutPlayButton()
        {
            if (_playBtnRT == null) return;
            float half = PLAY_W_FRAC * 0.5f;
            _playBtnRT.anchorMin = new Vector2(0.5f - half, 0.5f);
            _playBtnRT.anchorMax = new Vector2(0.5f + half, 0.5f);
            _playBtnRT.pivot     = new Vector2(0.5f, 0.5f);

            Rect cr = ((RectTransform)_canvas.transform).rect;
            Vector2 size = PlayButtonSize();
            _playBtnRT.sizeDelta = new Vector2(0f, size.y);    // x driven by the anchors
            // CLAMP. If the canvas rect is still reporting raw screen pixels on this pass,
            // -0.26 of 2556 is -664 units in a ~960-unit canvas and the button lands off the
            // bottom edge — which is exactly what "now there is no button" looked like. The
            // deferred pass corrects the number; this makes the WRONG number harmless rather
            // than invisible, so a timing slip degrades to slightly-misplaced, never gone.
            float ch   = cr.height > 1f ? cr.height : 960f;
            float maxY = Mathf.Max(0f, ch * 0.5f - size.y * 0.75f);
            _playBtnRT.anchoredPosition = new Vector2(0f, Mathf.Clamp(-ch * PLAY_Y_FRAC, -maxY, maxY));

            // The children are inset by PIXEL offsets derived from the button's height, so a
            // size change has to re-apply them — stretching the root alone would leave a border
            // and inner panel sized for whatever height was guessed when the hierarchy was
            // built. Fractions are the measured ones: 10.4% border, 16.5% ring.
            float h = size.y;
            float border = h * BORDER_FRAC;
            float faceH  = Mathf.Max(8f, h - border * 2f);
            float inset  = faceH * RING_INSET;

            if (_playFaceRT != null)
            {
                _playFaceRT.offsetMin = new Vector2(border, border);
                _playFaceRT.offsetMax = new Vector2(-border, -border);
            }
            if (_playRimRT != null)
            {
                _playRimRT.offsetMin = new Vector2(inset - faceH * 0.016f, inset - faceH * 0.016f);
                _playRimRT.offsetMax = new Vector2(-(inset - faceH * 0.016f), -(inset - faceH * 0.016f));
            }
            if (_playPanelRT != null)
            {
                _playPanelRT.offsetMin = new Vector2(inset, inset);
                _playPanelRT.offsetMax = new Vector2(-inset, -inset);
            }
            if (_playBtnLabel  != null) _playBtnLabel.fontSize  = h * 0.34f;
            if (_playBtnShadow != null)
            {
                _playBtnShadow.fontSize = h * 0.34f;
                _playBtnShadow.rectTransform.offsetMin = new Vector2(0f, -h * 0.035f);
                _playBtnShadow.rectTransform.offsetMax = new Vector2(0f, -h * 0.035f);
            }
        }

        /// <summary>Park the button at rest. 2026-10-20 Spencer: "just make the level button
        /// stationary" — it used to breathe on a looping yoyo scale. It is the only control on
        /// the screen, so it does not need motion to be found, and a permanent pulse under a
        /// static background reads as the screen never settling.
        /// Still kills any tween and resets the scale, so a press squash cannot strand it.</summary>

        /// <summary>The level-clear coin payout, on the home screen. The map used to fly these
        /// out of the star that had just dropped onto the cleared node; with the nodes gone that
        /// source went with them, and the reward was being silently snapped into the balance.
        ///
        /// Source is BEHIND AND BELOW the Level button, not its face. By the time this screen
        /// appears the button already reads the NEXT level, and coins pouring out of the forward
        /// control reads as a preview of what you are about to win rather than a payout for what
        /// you just did. Bursting from behind it keeps the eye where it already is — the button
        /// is the only thing on screen — without the button looking like the source.
        ///
        /// The landing half already works: SpawnCoinCascade ticks the pill up per arrival,
        /// punches it, and plays the land ping. RefreshHud shows `balance - pending` until then,
        /// so the counter genuinely climbs instead of arriving at a number already correct.
        /// 2026-10-20 Spencer: "i still want the animation of coins flying up to the top when
        /// you finish a level."</summary>
        private void RunHomeCoinCascade()
        {
            if (_pendingCoinReward <= 0 || _playBtnRT == null) return;
            int reward = _pendingCoinReward;
            _pendingCoinReward = 0;
            _pendingCascadeLevel = -1;

            Vector3 src = _playBtnRT.position + new Vector3(0f, -_playBtnRT.rect.height * 0.35f, 0f);
            // Let the screen settle before the burst — firing it on the same frame the fade
            // ends reads as the coins arriving before the home screen does.
            DOVirtual.DelayedCall(COINS_FIRST_BEAT, () => SpawnCoinCascade(src, reward, null))
                     .SetUpdate(true);
        }


        private System.Collections.IEnumerator LayoutPlayButtonDeferred()
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            LayoutPlayButton();
        }

        private void PulsePlayButton()
        {
            if (_playBtnRT == null) return;
            _playBtnRT.DOKill();
            _playBtnRT.localScale = Vector3.one;
        }

        private void OnPlayPressed()
        {
            if (_playBtnRT != null)
            {
                var b = _playBtnRT.GetComponent<Button>();
                if (b != null) b.interactable = false;   // one press only; re-armed on cancel
                _playBtnRT.DOKill();
                _playBtnRT.localScale = Vector3.one;
                UIAnimations.ButtonPress(_playBtnRT);
            }
            GameAudio.Instance?.PlayMultiPopRelease();
            ShowIntroOverMap();   // the SAME hand-off a node tap used: home -> level card -> play
        }

        // Show the level's play modal OVER the map (auto on landing, and on a node re-tap after cancel). Keeps the
        // map up behind it; fades the map on PLAY, or returns to the bare map on cancel. 2026-07-13 Spencer.
        private void ShowIntroOverMap()
        {
            if (_onEntered == null) return;
            _group.blocksRaycasts = false;                       // intro (higher canvas) takes input; map is backdrop
            if (_currentNodeButton != null) _currentNodeButton.interactable = false;
            LevelIntroModal.OnPlayStarted -= HandleIntroPlayed;    LevelIntroModal.OnPlayStarted += HandleIntroPlayed;
            LevelIntroModal.OnCancelled   -= HandleIntroCancelled; LevelIntroModal.OnCancelled   += HandleIntroCancelled;
            _onEntered.Invoke(); // shows the LevelIntroModal (NOT nulled → the node can re-open it after a cancel)
        }

        private void HandleIntroPlayed()
        {
            LevelIntroModal.OnPlayStarted -= HandleIntroPlayed;
            LevelIntroModal.OnCancelled   -= HandleIntroCancelled;
            FadeMapOut();
        }

        /// <summary>Back out of the level card -> the home screen is live again, so the button
        /// has to become pressable. Without this the X on the level card left a dead screen.</summary>
        private void RearmPlayButton()
        {
            if (_playBtnRT == null) return;
            var b = _playBtnRT.GetComponent<Button>();
            if (b != null) b.interactable = true;
            PulsePlayButton();
        }

        private void HandleIntroCancelled()
        {
            LevelIntroModal.OnPlayStarted -= HandleIntroPlayed;
            LevelIntroModal.OnCancelled   -= HandleIntroCancelled;
            if (HomeMode) { _group.blocksRaycasts = true; RearmPlayButton(); return; }
            GoBareMap();
        }

        // Intro X-cancelled → the map sits BARE (still paused). Tapping the current node re-opens the play modal.
        private void GoBareMap()
        {
            _group.blocksRaycasts = true;
            if (_currentNodeButton != null) _currentNodeButton.interactable = true;
        }

        private void ReadyForTap()
        {
            SetTapEnabled(true);
            if (_tapPrompt != null)
            {
                _tapPrompt.DOKill();
                _tapPrompt.alpha = 0f;
                DOTween.To(() => _tapPrompt.alpha, a => _tapPrompt.alpha = a, 1f, 0.25f).SetUpdate(true);
                _tapPrompt.transform.localScale = Vector3.one;
                _tapPrompt.transform.DOScale(1.10f, 0.7f).SetEase(Ease.InOutSine).SetLoops(-1, LoopType.Yoyo).SetUpdate(true);
            }
        }

        private void OnTapEnter()
        {
            if (!_busy) return;
            SetTapEnabled(false);
            GameAudio.Instance?.PlayButtonClick();

            // Standalone/FX-test only: fade out then invoke the callback. (The real map-flow auto-pops the intro
            // on landing via OnHopLanded and never enables this full-screen tap button.) 2026-07-13 Spencer.
            _group.DOKill();
            _group.DOFade(0f, 0.28f).SetUpdate(true).OnComplete(() =>
            {
                _canvas.gameObject.SetActive(false);
                _busy = false;
                var cb = _onEntered; _onEntered = null;
                cb?.Invoke();
            });
        }

        /// <summary>True while the map is on screen (fading in, hopping, or holding under the intro). HUDManager
        /// uses this to defer the level-entry spring-in until the map has actually faded off the board.</summary>
        public bool IsShowing => _busy;

        // Fade the map away (used after PLAY, once the intro has released the pause and gameplay is starting).
        private void FadeMapOut()
        {
            // Park the board + HUDs off-screen NOW (before the fade) so the map dissolves into a BLANK screen rather
            // than the board/HUDs sitting at rest behind it. Prep is idempotent (OnPlayStarted may have parked
            // already). Once the map is fully gone, play the two-beat entry. 2026-07-13 Spencer.
            HUDManager.Instance?.PrepLevelEntry();
            _group.DOKill();
            _group.DOFade(0f, 0.30f).SetUpdate(true).OnComplete(() =>
            {
                if (_canvas != null) _canvas.gameObject.SetActive(false);
                _busy = false;
                HUDManager.Instance?.AnimateLevelEntryIn();
            });
        }

        private void SetTapEnabled(bool on)
        {
            if (_tapButton != null) _tapButton.interactable = on;
        }

        // First level shown in the batch that contains <paramref name="level"/> (1-10 → 1, 11-20 → 11, …).
        private static int BatchStart(int level) => ((Mathf.Max(1, level) - 1) / VISIBLE_NODES) * VISIBLE_NODES + 1;

        // ── Node layout ───────────────────────────────────────────────────────────
        private void LayoutNodes(int centerLevel)
        {
            // The map pages in BATCHES of VISIBLE_NODES: reach the top (e.g. level 10) and the next showing simply
            // starts over at the next batch (11-20), numbers and all. centerLevel picks the batch + node coloring.
            // 2026-07-13 Spencer.
            _windowStart = BatchStart(centerLevel);
            _currentNodeButton = null; // nodes are destroyed + rebuilt below; BuildNode re-assigns for the current one

            // Match the nodes container to the cover-fitted artwork. Its default is a fixed
            // 540x960 box, which is fine for the zigzag layout but unrelated to where the
            // painting's pixels land — and nodes placed on the art can sit outside it.
            // Same centre, same scale, so node coordinates are unaffected; this just stops
            // anything depending on children living inside their parent's rect. 2026-10-19.
            if (_hasMapArt && _nodesRoot != null && _mapArtSize.x > 0f)
                _nodesRoot.sizeDelta = _mapArtSize;

            // Clear old nodes/paths — but NOT the avatar (it lives under nodesRoot too).
            for (int i = _nodesRoot.childCount - 1; i >= 0; i--)
            {
                var child = _nodesRoot.GetChild(i);
                if (_avatar != null && child == _avatar) continue;
                Destroy(child.gameObject);
            }

            // Connector paths first (behind nodes).
            // The painted map already HAS a path — drawing connector segments over it would
            // cut straight across the artwork's curves. Only draw them on the fallback
            // zigzag layout, which has no path of its own. 2026-10-19.
            if (!_hasMapArt)
                for (int i = 0; i < VISIBLE_NODES - 1; i++)
                    BuildPath(NodeAnchoredPos(i), NodeAnchoredPos(i + 1));

            // Nodes.
            for (int i = 0; i < VISIBLE_NODES; i++)
            {
                int level = _windowStart + i;
                bool isCurrent = (level == centerLevel);
                bool isLocked  = (level > centerLevel); // not reached yet → padlock instead of a number
                bool isBoss    = IsBossLevel(level); // last node of an AREA (internal 20, 30…) = boss; tutorial's 10 is NOT
                Color c = isBoss
                    ? BOSS_COLOR
                    : (level < centerLevel ? _theme.NodeDone : (isCurrent ? _theme.NodeCurrent : _theme.NodeLocked));
                BuildNode(NodeAnchoredPos(i), level, c, isCurrent, isLocked);
            }

            // Persistent MARKERS on every level already CLEARED (level < centerLevel), so a cleared level stays marked
            // whenever the map is shown — a TROPHY on boss nodes, a STAR on the rest. The level whose marker is about
            // to DROP IN this show (_animateStarLevel) is skipped here — its drop is triggered separately. 2026-07-13.
            for (int i = 0; i < VISIBLE_NODES; i++)
            {
                int level = _windowStart + i;
                if (level < centerLevel && level != _animateStarLevel)
                    BuildNodeMarker(NodeAnchoredPos(i), level, animate: false, isTrophy: IsBossLevel(level));
            }

            // Keep the avatar rendered above the nodes.
            if (_avatar != null) _avatar.SetAsLastSibling();
        }

        // node index (0 = bottom / lowest level) → anchored position (bottom-to-top climb + gentle zigzag)
        /// <summary>Where each node sits ON THE PAINTED MAP, as a fraction of the artwork
        /// (0,0 = top-left). MEASURED off levelmap@2x.png by detecting the purple path discs:
        /// nine flat ellipses (width/height 1.6-2.0, area >9000) running up the path, ordered
        /// bottom-first so index 0 is level 1. The tenth is the LIBRARY DOORWAY at the top —
        /// the arch with the bookshelves, found as the large warm-bright region inside the
        /// castle band. 2026-10-19 Spencer: "place all the nodes above the purple nodes...
        /// and then the final node on the library door".
        ///
        /// Stored normalised rather than in pixels so it survives the import downscale
        /// (2296x4194 -> 2242x4096 at maxTextureSize 4096) and any future re-export at a
        /// different resolution, as long as the composition does not move.</summary>
        private static readonly Vector2[] MAP_NODE_UV =
        {
            // THIRD map export, 2026-10-20 (1704x3694). This artwork paints the nodes IN — ten
            // numbered blue-and-gold ovals climbing a stone staircase to the observatory. So
            // these are MEASURED off the painted ovals themselves (deep blue, ratio ~1.35,
            // fill ~0.72; their pixel size recedes 199px -> 115px up the stairs, which is how
            // sky and water were told apart from them), not off path markers as before.
            //
            // ⚠️ THE ARTWORK IS MISSING NODE 3. The painted numbering runs 1,2,4,5,6,7,8,9,10 —
            // verified by cropping and reading each oval, and by sweeping the whole band between
            // 2 and 4, which contains only open plaza. Slot 3 below is therefore NOT measured: it
            // is the widest clear run of path between its neighbours (363px of bare paving), so
            // the avatar still walks a sensible line. Re-measure it the moment the art has a
            // real disc there.
            new Vector2(0.5008f, 0.8639f),  // 1
            new Vector2(0.5343f, 0.7529f),  // 2
            new Vector2(0.5850f, 0.7270f),  // 3  ← INTERPOLATED, no disc in the art
            new Vector2(0.6157f, 0.6963f),  // 4
            new Vector2(0.5036f, 0.6477f),  // 5
            new Vector2(0.6257f, 0.5946f),  // 6
            new Vector2(0.5079f, 0.5486f),  // 7
            new Vector2(0.5678f, 0.4897f),  // 8
            new Vector2(0.4721f, 0.4497f),  // 9
            new Vector2(0.5475f, 0.4170f),  // 10 top of the stairs, at the observatory doors
        };

        private System.Collections.IEnumerator VerifyMapFit()
        {
            yield return null;
            yield return new WaitForEndOfFrame();
            if (_bgBottomImg == null || _canvas == null) yield break;

            Rect canvasRect = ((RectTransform)_canvas.transform).rect;
            var   bgRT      = _bgBottomImg.rectTransform;
            var   sp        = _bgBottomImg.sprite;
            if (sp == null) yield break;

            float artAspect = sp.rect.width / sp.rect.height;
            float scrAspect = canvasRect.width / canvasRect.height;
            Vector2 cover = artAspect > scrAspect
                ? new Vector2(canvasRect.height * artAspect, canvasRect.height)
                : new Vector2(canvasRect.width, canvasRect.width / artAspect);

            bgRT.anchorMin = bgRT.anchorMax = new Vector2(0.5f, 0.5f);
            bgRT.pivot = new Vector2(0.5f, 0.5f);
            bgRT.anchoredPosition = Vector2.zero;
            bgRT.sizeDelta = cover;
            _mapArtSize = cover;
            if (_nodesRoot != null) _nodesRoot.sizeDelta = cover;

            WDLog.Log($"[LevelMap] AFTER LAYOUT — canvas {canvasRect.width:F0}x{canvasRect.height:F0}, " +
                      $"scale {_canvas.scaleFactor:F3}, bg rect {bgRT.rect.width:F0}x{bgRT.rect.height:F0}, " +
                      $"sizeDelta {bgRT.sizeDelta.x:F0}x{bgRT.sizeDelta.y:F0}, " +
                      $"lossyScale {bgRT.lossyScale.x:F3}");
            LayoutNodes(_windowStart);
        }

        private Vector2 NodeAnchoredPos(int idx)
        {
            // On the painted map, follow the artwork. The background is ENVELOPE-fitted, so it
            // is larger than the screen — a node's screen position has to be computed against
            // the BACKGROUND's rect, not the canvas, or every node drifts toward centre by the
            // amount of the overflow.
            if (_hasMapArt && _bgBottomImg != null && idx >= 0 && idx < MAP_NODE_UV.Length)
            {
                // Use the ART's cover-fitted size, NOT _nodesRoot's rect — that is a fixed
                // 540x960 box (see its construction) and has nothing to do with where the
                // painting's pixels land. Both it and the background are centre-anchored and
                // unscaled, so a canvas-space offset from centre IS a nodesRoot-local offset.
                Vector2 uv = MAP_NODE_UV[idx];
                return new Vector2((uv.x - 0.5f) * _mapArtSize.x,
                                   (0.5f - uv.y) * _mapArtSize.y);   // uGUI +y is UP; uv +y is DOWN
            }

            float y = (idx - (VISIBLE_NODES - 1) * 0.5f) * NODE_SPACING + STRIP_Y_OFFSET;
            float x = (idx % 2 == 0) ? -ZIGZAG_X : ZIGZAG_X;
            return new Vector2(x, y);
        }

        // World/screen position of a level's node (fallback source for the coin cascade if the star marker is gone).
        private Vector3 NodeWorldPos(int level)
        {
            var node = _nodesRoot != null ? _nodesRoot.Find($"Node{level}") : null;
            if (node != null) return node.position;
            if (_nodesRoot != null) return _nodesRoot.TransformPoint(NodeAnchoredPos(level - _windowStart));
            return Vector3.zero;
        }

        private void PlaceAvatarAtLevel(int level)
        {
            if (_avatar == null) return;
            _avatar.anchoredPosition = NodeAnchoredPos(level - _windowStart);
        }

        // ── UI construction ───────────────────────────────────────────────────────
        private void EnsureUI()
        {
            if (_canvas != null) return;
            EnsureSprites();

            var canvasGO = new GameObject("LevelMapCanvas");
            canvasGO.transform.SetParent(transform, false);
            _canvas = canvasGO.AddComponent<Canvas>();
            _canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 170; // above StageClearModal (160), below nothing that matters here
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 0.5f;
            canvasGO.AddComponent<GraphicRaycaster>();

            _group = canvasGO.AddComponent<CanvasGroup>();

            // Full-screen background (covers the board). Two stacked images = cheap vertical gradient feel.
            // Colors are set per-world in ApplyWorldTheme; build with the current theme as a starting point.
            _bgBottomImg = BuildFullScreen("BGBottom", _theme.BgBottom);
            _bgTopImg    = BuildFullScreen("BGTop", new Color(_theme.BgTop.r, _theme.BgTop.g, _theme.BgTop.b, 0.85f)); // soft top wash

            // ── PAINTED MAP ART (2026-10-19 Spencer) ─────────────────────────────────
            // Spencer's level-map painting replaces the two flat colour washes. 2296x4194,
            // aspect 0.547 — WIDER than the 1179x2556 phone (0.461), so it must be
            // COVER-fitted or the path and castle squash. AspectRatioFitter in
            // EnvelopeParent mode is the uGUI way to do that: it keeps the sprite's aspect
            // and overflows the narrower axis rather than distorting.
            //
            // The top wash is switched off and the recolour in ApplyWorldTheme is skipped
            // (see _hasMapArt) — those exist to tint a flat background per world, and they
            // would tint the painting instead.
            var mapArt = Resources.Load<Sprite>("UI/levelmap@2x");
            if (mapArt != null && _bgBottomImg != null)
            {
                _hasMapArt = true;
                _bgBottomImg.sprite         = mapArt;
                _bgBottomImg.color          = Color.white;
                _bgBottomImg.preserveAspect = false;
                if (_bgTopImg != null) _bgTopImg.gameObject.SetActive(false);

                // COVER-FIT BY HAND, NOT WITH AspectRatioFitter. 2026-10-19 — the fitter was
                // doing NOTHING: BuildFullScreen calls Stretch(), which sets anchorMin 0 /
                // anchorMax 1, and AspectRatioFitter cannot drive a RectTransform with
                // stretched anchors. The art was simply being stretched to the canvas, which
                // squashed it (source 0.547 vs phone 0.461) and left a gap at the bottom.
                // Centre-anchor it and size it explicitly so the maths is visible and the
                // node placement below has a rect it can trust.
                var bgRT = _bgBottomImg.rectTransform;
                bgRT.anchorMin = bgRT.anchorMax = new Vector2(0.5f, 0.5f);
                bgRT.pivot = new Vector2(0.5f, 0.5f);
                bgRT.anchoredPosition = Vector2.zero;

                Rect canvasRect = ((RectTransform)_canvas.transform).rect;
                float artAspect = mapArt.rect.width / mapArt.rect.height;
                float scrAspect = canvasRect.width / canvasRect.height;
                Vector2 cover = artAspect > scrAspect
                    ? new Vector2(canvasRect.height * artAspect, canvasRect.height) // match height, overflow width
                    : new Vector2(canvasRect.width, canvasRect.width / artAspect);  // match width, overflow height
                bgRT.sizeDelta = cover;
                _mapArtSize = cover;

                WDLog.Log($"[LevelMap] map art {mapArt.rect.width}x{mapArt.rect.height} " +
                          $"(aspect {artAspect:F3}) cover-fitted to {cover.x:F0}x{cover.y:F0} " +
                          $"in a {canvasRect.width:F0}x{canvasRect.height:F0} canvas");
                // Re-assert and REPORT after layout settles. A Canvas resolves its rect over
                // the first frames, and anything that re-runs Stretch() on this image would
                // silently undo the sizing above. Logging the rect we actually END UP with
                // turns "it looks zoomed" into a number. 2026-10-19.
                StartCoroutine(VerifyMapFit());
            }

            // Full-screen tap-to-enter button (transparent). Enabled only after the hop.
            var btnGO = new GameObject("TapToEnter");
            btnGO.transform.SetParent(canvasGO.transform, false);
            var btnRT = btnGO.AddComponent<RectTransform>();
            Stretch(btnRT);
            var btnImg = btnGO.AddComponent<Image>();
            btnImg.color = new Color(0f, 0f, 0f, 0f); // invisible, but raycastable
            _tapButton = btnGO.AddComponent<Button>();
            _tapButton.transition = Selectable.Transition.None;
            _tapButton.onClick.AddListener(OnTapEnter);

            // Nodes root (centered).
            var nrGO = new GameObject("NodesRoot");
            nrGO.transform.SetParent(canvasGO.transform, false);
            _nodesRoot = nrGO.AddComponent<RectTransform>();
            _nodesRoot.anchorMin = _nodesRoot.anchorMax = new Vector2(0.5f, 0.5f);
            _nodesRoot.anchoredPosition = Vector2.zero;
            _nodesRoot.sizeDelta = new Vector2(540f, 960f);

            // Avatar (bright marker) — lives under nodesRoot so it shares the node coordinate space. Container +
            // layered rim/fill children (same fix as the nodes — no image on the container). 2026-07-10 Spencer.
            var avGO = new GameObject("Avatar");
            avGO.transform.SetParent(_nodesRoot, false);
            _avatar = avGO.AddComponent<RectTransform>();
            _avatar.sizeDelta = new Vector2(NODE_SIZE * 0.72f, NODE_SIZE * 0.72f);
            BuildCircle(avGO.transform, NODE_SIZE * 0.72f + 8f, NODE_RIM);            // white rim (behind)
            _avatarFill = BuildCircle(avGO.transform, NODE_SIZE * 0.72f, _theme.Avatar); // fill (recolored per world)

            // Top + bottom HUD panels (Candy-Crush-map style).
            BuildTopHud(canvasGO.transform);
            BuildBottomHud(canvasGO.transform);

            // World title — sits just under the top HUD, recolored per world in ApplyWorldTheme.
            var wtGO = new GameObject("WorldTitle");
            wtGO.transform.SetParent(canvasGO.transform, false);
            var wtRT = wtGO.AddComponent<RectTransform>();
            wtRT.anchorMin = wtRT.anchorMax = new Vector2(0.5f, 1f);
            wtRT.pivot = new Vector2(0.5f, 1f);
            wtRT.sizeDelta = new Vector2(460f, 46f);
            wtRT.anchoredPosition = new Vector2(0f, -(TOP_HUD_H + 14f));
            _worldTitle = wtGO.AddComponent<TextMeshProUGUI>();
            _worldTitle.fontSize = 30f;
            _worldTitle.fontStyle = FontStyles.Bold;
            _worldTitle.alignment = TextAlignmentOptions.Center;
            _worldTitle.raycastTarget = false;
            _worldTitle.enableWordWrapping = false;
            var wtf = GameFont.GetUITMP(); if (wtf != null) _worldTitle.font = wtf;
            // Hidden on the painted map — "AREA 1 · GREEN HILLS" was laid over the castle and
            // the top of the path. The artwork names the place on its own. Still built (and
            // still recoloured by ApplyWorldTheme) so nothing downstream hits a null; it is
            // just not shown. 2026-10-19 Spencer.
            if (_hasMapArt) wtGO.SetActive(false);

            // (No "Tap to continue" text — the whole screen is still the tap-to-enter button. 2026-07-10 Spencer.)

            _canvas.gameObject.SetActive(false);
        }

        private Image BuildFullScreen(string name, Color c)
        {
            var go = new GameObject(name);
            go.transform.SetParent(_canvas.transform, false);
            var rt = go.AddComponent<RectTransform>();
            Stretch(rt);
            var img = go.AddComponent<Image>();
            img.color = c;
            img.raycastTarget = false;
            return img;
        }

        // Recolor the world-dependent chrome (background, avatar, title) for the batch containing <paramref name="level"/>.
        private void ApplyWorldTheme(int level)
        {
            int world = (BatchStart(level) - 1) / VISIBLE_NODES; // 0-based
            // Skip the per-world tint when the painted map is in — those colours exist to
            // shade a FLAT background and would wash the artwork. 2026-10-19.
            if (!_hasMapArt)
            {
                if (_bgBottomImg != null) _bgBottomImg.color = _theme.BgBottom;
                if (_bgTopImg != null)    _bgTopImg.color    = new Color(_theme.BgTop.r, _theme.BgTop.g, _theme.BgTop.b, 0.85f);
            }
            if (_avatarFill != null)  _avatarFill.color  = _theme.Avatar;
            if (_worldTitle != null)
            {
                // world 0 = the one-time tutorial (not a numbered Area); worlds 1+ are AREA 1, 2, 3 …
                _worldTitle.text = world == 0
                    ? "TUTORIAL"
                    : $"AREA {world}   ·   {_theme.Name.ToUpper()}";
                // Contrast against the background: dark title on light worlds, white on dark ones.
                float lum = _theme.BgTop.r * 0.3f + _theme.BgTop.g * 0.59f + _theme.BgTop.b * 0.11f;
                _worldTitle.color = lum > 0.55f ? new Color(0.16f, 0.20f, 0.34f, 0.92f) : new Color(1f, 1f, 1f, 0.92f);
            }
        }

        private void BuildNode(Vector2 pos, int level, Color fill, bool isCurrent, bool isLocked)
        {
            // Container holds NO image — layer children explicitly: rim (behind) → colored fill → number (on top).
            // (A child ALWAYS renders in front of its parent's own graphic in uGUI, so the old parent-fill + child-rim
            // put the white rim OVER the fill, hiding both the color and the number. 2026-07-10 Spencer.)
            var go = new GameObject($"Node{level}");
            go.transform.SetParent(_nodesRoot, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(NODE_SIZE, NODE_SIZE);
            rt.anchoredPosition = pos;

            // The painted map already DRAWS the discs, so a rim + fill would sit a flat UI
            // circle on top of rendered artwork. Everything carrying INFORMATION — number,
            // padlock, star/trophy, invisible tap target — stays; only decoration goes.
            // 2026-10-19 Spencer.
            if (!_hasMapArt)
            {
                BuildCircle(go.transform, NODE_SIZE + 10f, NODE_RIM); // white rim (behind)
                BuildCircle(go.transform, NODE_SIZE, fill);           // colored fill (on top of rim)
            }

            if (isLocked)
            {
                // Not reached yet → the booster padlock icon instead of a number (white on the navy fill).
                var lockGO = new GameObject("Lock");
                lockGO.transform.SetParent(go.transform, false);
                var krt = lockGO.AddComponent<RectTransform>();
                krt.anchorMin = krt.anchorMax = new Vector2(0.5f, 0.5f);
                krt.sizeDelta = new Vector2(NODE_SIZE * 0.52f, NODE_SIZE * 0.52f);
                krt.anchoredPosition = Vector2.zero;
                var kImg = lockGO.AddComponent<Image>();
                kImg.sprite = LockIcon();
                kImg.color = Color.white;
                kImg.preserveAspect = true;
                kImg.raycastTarget = false;
            }
            else
            {
                // Level number — on top of everything. Auto-pick text color for contrast (dark on bright, white on navy).
                float lum = fill.r * 0.3f + fill.g * 0.59f + fill.b * 0.11f;
                var lblGO = new GameObject("Num");
                lblGO.transform.SetParent(go.transform, false);
                var lrt = lblGO.AddComponent<RectTransform>();
                Stretch(lrt);
                var lbl = lblGO.AddComponent<TextMeshProUGUI>();
                lbl.text = DisplayNum(level).ToString(); // run level in Areas (tutorial shows 1..10)
                lbl.fontSize = 27f;
                lbl.fontStyle = FontStyles.Bold;
                lbl.alignment = TextAlignmentOptions.Center;
                // No fill behind it any more, so the fill-luminance rule does not apply.
                // White reads on both the purple disc faces and the gold rims.
                lbl.color = _hasMapArt ? Color.white : (lum > 0.5f ? HUD_INK : Color.white);
                if (_hasMapArt) lbl.fontSize = 30f;
                lbl.raycastTarget = false;
                var uf = GameFont.GetUITMP(); if (uf != null) lbl.font = uf;

                // ⚠️ NO TMP OUTLINE HERE. 2026-10-19 — outlineWidth goes through
                // SetOutlineThickness, which writes to the font MATERIAL. On a TMP component
                // created this same frame the material is not ready, and it throws
                // NullReferenceException — which aborted the whole map build, so the level map
                // never appeared at all. Reordering it after `lbl.font = uf` was NOT enough:
                // a non-null font does not mean a ready material.
                //
                // The number is legible without it: white, bold, 30pt sits on a saturated
                // purple disc. If it ever needs more separation, add a second TMP behind it
                // as a shadow rather than touching the material.
            }

            // The CURRENT level's node is a tap target: on a bare map (after cancelling the play modal) tapping it
            // re-opens the modal. A transparent raycast graphic over the node catches the tap. Interactable is
            // enabled only in the bare-map state (GoBareMap). 2026-07-13 Spencer.
            if (isCurrent)
            {
                var hitGO = new GameObject("NodeHit");
                hitGO.transform.SetParent(go.transform, false);
                var hrt = hitGO.AddComponent<RectTransform>();
                hrt.anchorMin = hrt.anchorMax = new Vector2(0.5f, 0.5f);
                hrt.sizeDelta = new Vector2(NODE_SIZE + 18f, NODE_SIZE + 18f); // a touch bigger for an easy tap
                hrt.anchoredPosition = Vector2.zero;
                var hitImg = hitGO.AddComponent<Image>();
                hitImg.color = new Color(0f, 0f, 0f, 0f); // invisible but raycastable
                _currentNodeButton = hitGO.AddComponent<Button>();
                _currentNodeButton.transition = Selectable.Transition.None;
                _currentNodeButton.interactable = false; // enabled only on a bare map
                _currentNodeButton.onClick.AddListener(() => { GameAudio.Instance?.PlayButtonClick(); ShowIntroOverMap(); });
            }
        }

        // A single circle Image child of the given size + color (used for node rim + fill, correctly layered).
        private Image BuildCircle(Transform parent, float size, Color color)
        {
            var go = new GameObject("Circle");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(size, size);
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.sprite = s_circle;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        // ── HUD panels (Candy-Crush-map layout: hearts + coins + settings up top; bottom bar reserved) ──
        private const float HUD_OVER = 60f; // how far the bars bleed PAST each screen edge (outer corners off-screen)

        private void BuildTopHud(Transform parent)
        {
            // The pink bar is skipped on the painted map so the artwork runs to the top of the
            // screen. The PILLS below are separate children and are unaffected — hearts, coins
            // and settings still sit in the same places, now floating on the art. TOP_HUD_H is
            // still used for their vertical placement, so nothing moves. 2026-10-19 Spencer.
            if (!_hasMapArt)
                BuildFullBleedBar(parent, "TopHud", HUD_PANEL, top: true, visibleH: TOP_HUD_H, radius: 30);

            float bandY = -TOP_HUD_H * 0.5f; // vertical centre of the visible top band (below the screen top)

            // Hearts pill (left) — soulgem heart icon + count.
            _heartsText = BuildHudPill(parent, new Vector2(0f, 1f), leftX: 14f, centerY: bandY, width: 132f, iconSprite: HeartIcon(), iconSize: HeartPillIcon, isHeart: true);
            // Coins pill (next to hearts) — coin icon + balance. Wider so a big coin total fits on one line.
            _coinsText  = BuildHudPill(parent, new Vector2(0f, 1f), leftX: 14f + 132f + 10f, centerY: bandY, width: 186f, iconSprite: CoinIcon(), iconSize: CoinPillIcon);
            _coinsIconRT = _coinsText != null ? _coinsText.transform.parent.Find("Icon") as RectTransform : null; // cascade target

            // Green "+" STORE badge on the coins pill (Royal-Match style) → opens the coin store (built later).
            var coinPill = _coinsText != null ? _coinsText.transform.parent as RectTransform : null;
            if (coinPill != null)
            {
                var badgeGO = new GameObject("CoinStorePlus", typeof(RectTransform));
                badgeGO.transform.SetParent(coinPill, false);
                var brt = badgeGO.GetComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = new Vector2(1f, 0.5f); // right edge of the pill
                brt.pivot = new Vector2(0.5f, 0.5f);
                brt.sizeDelta = new Vector2(52f, 52f);
                // 2026-10-20 Spencer: "move the plus sign down so that it isnt fully
                // obstructing the amount". At -14 the badge sat across the middle of the pill's
                // right end and swallowed the last digit of a five-figure balance. Dropped to
                // the bottom-right CORNER and nudged out, so it overlaps the pill's rounded
                // edge rather than its text.
                brt.anchoredPosition = new Vector2(8f, -26f);
                var hit = badgeGO.AddComponent<Image>();               // transparent raycast target for the button
                hit.color = new Color(0f, 0f, 0f, 0f); hit.raycastTarget = true;
                var badgeBtn = badgeGO.AddComponent<Button>();
                badgeBtn.transition = Selectable.Transition.None;
                badgeBtn.onClick.AddListener(OpenCoinStore);
                BuildCircle(badgeGO.transform, 52f, Color.white);                       // white rim
                BuildCircle(badgeGO.transform, 44f, new Color(0.27f, 0.77f, 0.36f, 1f)); // candy-green fill
                // "+" drawn as two rounded white bars — perfectly centred (a TMP "+" glyph reads off-centre).
                void PlusBar(Vector2 size)
                {
                    var barGO = new GameObject("PlusBar", typeof(RectTransform), typeof(Image));
                    barGO.transform.SetParent(badgeGO.transform, false);
                    var brt2 = barGO.GetComponent<RectTransform>();
                    brt2.anchorMin = brt2.anchorMax = new Vector2(0.5f, 0.5f);
                    brt2.pivot = new Vector2(0.5f, 0.5f);
                    brt2.sizeDelta = size; brt2.anchoredPosition = Vector2.zero;
                    var bimg2 = barGO.GetComponent<Image>();
                    bimg2.sprite = MenuUI.GetRoundedRectSprite(3); bimg2.type = Image.Type.Sliced;
                    bimg2.color = Color.white; bimg2.raycastTarget = false;
                }
                PlusBar(new Vector2(24f, 7f)); // horizontal
                PlusBar(new Vector2(7f, 24f)); // vertical
            }

            // Settings gear (right, anchored to the screen corner) — opens the same SettingsModal the in-game HUD does.
            var gearGO = new GameObject("SettingsBtn");
            gearGO.transform.SetParent(parent, false);
            var grt = gearGO.AddComponent<RectTransform>();
            grt.anchorMin = grt.anchorMax = new Vector2(1f, 1f);
            grt.pivot = new Vector2(1f, 0.5f);
            grt.sizeDelta = new Vector2(60f, 60f);
            grt.anchoredPosition = new Vector2(-14f, bandY);
            var gearImg = gearGO.AddComponent<Image>();
            gearImg.sprite = MenuUI.GetRoundedRectSprite(30); gearImg.type = Image.Type.Sliced;
            gearImg.color = new Color(1f, 0.82f, 0.32f, 1f); gearImg.raycastTarget = true;
            var gearBtn = gearGO.AddComponent<Button>();
            gearBtn.transition = Selectable.Transition.None;
            gearBtn.onClick.AddListener(OpenSettings);
            // Gear ICON sprite, not a glyph — the Cartoon/UI fonts have no ⚙ character (it rendered as a box □).
            // Dark tint reads on the gold button. 2026-07-13 Spencer.
            var gearIconGO = new GameObject("GearIcon");
            gearIconGO.transform.SetParent(gearGO.transform, false);
            var girt = gearIconGO.AddComponent<RectTransform>();
            girt.anchorMin = girt.anchorMax = new Vector2(0.5f, 0.5f);
            girt.sizeDelta = new Vector2(38f, 38f);
            girt.anchoredPosition = Vector2.zero;
            var gearIconImg = gearIconGO.AddComponent<Image>();
            gearIconImg.sprite = SettingsIcon();
            gearIconImg.color = HUD_INK;
            gearIconImg.preserveAspect = true;
            gearIconImg.raycastTarget = false;

            RefreshHud();
        }

        private void BuildBottomHud(Transform parent)
        {
            // Reserved bar — we fill this later (garden/world shortcuts, daily, shop, etc.).
            BuildFullBleedBar(parent, "BottomHud", HUD_BOTTOM, top: false, visibleH: BOTTOM_HUD_H, radius: 30);
        }

        // Full-bleed rounded bar: stretches past BOTH side edges AND past its outer (top/bottom) edge so the outer
        // rounded corners sit off-screen — only the inner, map-facing edge shows rounding, no background gaps.
        private Image BuildFullBleedBar(Transform parent, string name, Color color, bool top, float visibleH, int radius)
        {
            var img = new GameObject(name).AddComponent<Image>();
            img.transform.SetParent(parent, false);
            var rt = img.rectTransform;
            if (top)
            {
                rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f);
                rt.offsetMin = new Vector2(-HUD_OVER, -visibleH);   // bottom (inner, rounded) edge — visibleH below the top
                rt.offsetMax = new Vector2(HUD_OVER, HUD_OVER);     // top edge bleeds HUD_OVER above the screen
            }
            else
            {
                rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 0f);
                rt.offsetMin = new Vector2(-HUD_OVER, -HUD_OVER);   // bottom edge bleeds below the screen
                rt.offsetMax = new Vector2(HUD_OVER, visibleH);     // top (inner, rounded) edge — visibleH above the bottom
            }
            img.sprite = MenuUI.GetRoundedRectSprite(radius); img.type = Image.Type.Sliced;
            img.color = color; img.raycastTarget = true;
            return img;
        }

        // A cream pill in the top HUD, anchored to the SCREEN (left pivot at leftX). Returns the value text.
        /// <summary><paramref name="iconSize"/> defaults to the original 40 so the hearts pill
        /// is unchanged; the coins pill passes a larger value for the 3D coin.</summary>
        private TextMeshProUGUI BuildHudPill(Transform parent, Vector2 anchor, float leftX, float centerY, float width, Sprite iconSprite, float iconSize = 40f, bool isHeart = false)
        {
            var go = new GameObject("Pill");
            go.transform.SetParent(parent, false);
            var pill = go.AddComponent<RectTransform>();
            pill.anchorMin = pill.anchorMax = anchor;
            pill.pivot = new Vector2(0f, 0.5f); // left pivot → leftX is the pill's left edge
            pill.sizeDelta = new Vector2(width, 58f);
            pill.anchoredPosition = new Vector2(leftX, centerY);
            var pimg = go.AddComponent<Image>();
            pimg.sprite = MenuUI.GetRoundedRectSprite(26); pimg.type = Image.Type.Sliced;
            pimg.color = HUD_PILL; pimg.raycastTarget = false;

            if (iconSprite != null)
            {
                var ic = new GameObject("Icon");
                ic.transform.SetParent(pill.transform, false);
                var irt = ic.AddComponent<RectTransform>();
                irt.anchorMin = irt.anchorMax = new Vector2(0f, 0.5f);
                irt.sizeDelta = new Vector2(iconSize, iconSize);
                irt.anchoredPosition = new Vector2(28f, 0f);
                var iimg = ic.AddComponent<Image>();
                iimg.sprite = iconSprite;
                // Tunable in Resources/UIConfig (coin/heart brightness + tint). 2026-07-30.
                iimg.color = Color.white;
                MenuUI.AddIconDropShadow(iimg, iconSize);   // hearts and coins both get it
                // Applies size + hue/sat/value now, and re-applies whenever a UIConfig slider
                // moves in Play mode. Image.color can only multiply, so colour is a shader job.
                UIConfig.RegisterIcon(iimg, isHeart ? UIConfig.IconSlot.HeartPill : UIConfig.IconSlot.CoinPill);
                iimg.preserveAspect = true; iimg.raycastTarget = false;
            }

            var t = BuildLabel(pill.transform, "", HUD_INK, 28f);
            var trt = (RectTransform)t.transform;
            trt.anchorMin = new Vector2(0f, 0f); trt.anchorMax = new Vector2(1f, 1f);
            // keep the same visual gap between icon and text as the icon grows
            trt.offsetMin = new Vector2(iconSprite != null ? 28f + iconSize * 0.5f + 18f : 16f, 0f);
            trt.offsetMax = new Vector2(-12f, 0f);
            t.alignment = TextAlignmentOptions.Left;
            return t;
        }

        private TextMeshProUGUI BuildLabel(Transform parent, string text, Color color, float size)
        {
            var go = new GameObject("Label");
            go.transform.SetParent(parent, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(200f, 50f);
            rt.anchoredPosition = Vector2.zero;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.color = color; t.fontSize = size; t.fontStyle = FontStyles.Bold;
            t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false; t.richText = true;
            t.enableWordWrapping = false; t.overflowMode = TextOverflowModes.Overflow; // never wrap the count mid-number
            var uf = GameFont.GetUITMP(); if (uf != null) t.font = uf;
            return t;
        }

        private void RefreshHud()
        {
            if (_heartsText != null)
            {
                // Hearts = CONTINUES remaining this run (not the menu's HeartsManager lives). Reflects
                // SurvivalManager.MAX_CONTINUES_PER_RUN so it auto-tracks if we change the cap. 2026-07-13 Spencer.
                int max  = SurvivalManager.MAX_CONTINUES_PER_RUN;
                int used = SurvivalManager.Instance != null ? SurvivalManager.Instance.ContinuesUsedThisRun : 0;
                int left = Mathf.Max(0, max - used);
                _heartsText.text = $"{left}/{max}"; // heart is now the soulgem icon in the pill, not a glyph
            }
            if (_coinsText != null)
            {
                // Show the PRE-reward total while a coin cascade is queued — the flying coins tick it up to the
                // real balance as they land. No pending reward → just the balance. 2026-07-14 Spencer.
                _displayedCoins = Mathf.Max(0, CoinWallet.Balance - _pendingCoinReward);
                _coinsText.text = _displayedCoins.ToString();
            }
        }

        private void OpenSettings()
        {
            GameAudio.Instance?.PlaySettingsPress();
            if (SettingsModal.Instance == null)
            {
                var modalGO = new GameObject("SettingsModalRoot");
                modalGO.AddComponent<SettingsModal>();
            }
            SettingsModal.Instance?.Show();
        }

        // Coin-store entry point (the green "+" on the coins pill). Store UI is built later — placeholder for now.
        private void OpenCoinStore()
        {
            GameAudio.Instance?.PlayButtonClick();
            WDLog.Log("[LevelMapPanel] Coin store tapped — store not built yet (placeholder). 2026-07-14 Spencer.");
            // TODO: open the coin purchase store when it exists.
        }

        private static Sprite s_lockIcon; private static bool s_lockTried;
        private static Sprite LockIcon()
        {
            if (s_lockTried) return s_lockIcon;
            s_lockTried = true;
            // Same padlock the boosters use. Imported as textureType Default → Texture2D fallback.
            s_lockIcon = Resources.Load<Sprite>("Tiles/icon_lock_02");
            if (s_lockIcon == null)
            {
                var tex = Resources.Load<Texture2D>("Tiles/icon_lock_02");
                if (tex != null)
                    s_lockIcon = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            }
            return s_lockIcon;
        }

        private static Sprite s_settingsIcon; private static bool s_settingsTried;
        private static Sprite SettingsIcon()
        {
            if (s_settingsTried) return s_settingsIcon;
            s_settingsTried = true;
            // The Icon_Settings_Gear.png asset is basically empty (renders as a screwdriver-ish blob), so DRAW a real
            // cog procedurally: a rounded-tooth gear with a centre hole. 2026-07-14 Spencer.
            s_settingsIcon = BuildGearSprite();
            return s_settingsIcon;
        }

        private static Sprite BuildGearSprite()
        {
            const int S = 96;
            const int TEETH = 8;
            float c = (S - 1) * 0.5f;
            float outerR = S * 0.46f, valleyR = S * 0.35f, holeR = S * 0.17f;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[S * S];
            for (int y = 0; y < S; y++)
                for (int x = 0; x < S; x++)
                {
                    float dx = x - c, dy = y - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(dy, dx);
                    float toothT = (Mathf.Cos(ang * TEETH) + 1f) * 0.5f;      // 0..1, peaks TEETH times → rounded teeth
                    float edgeR = Mathf.Lerp(valleyR, outerR, toothT);
                    float a = Mathf.Min(Mathf.Clamp01(edgeR - r + 0.5f),      // ~1px anti-aliased outer edge
                                        Mathf.Clamp01(r - holeR + 0.5f));     // ~1px anti-aliased centre hole
                    px[y * S + x] = new Color32(255, 255, 255, (byte)(a * 255f));
                }
            tex.SetPixels32(px); tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f);
        }

        private static Sprite s_coinIcon; private static bool s_coinTried;
        private static Sprite CoinIcon()
        {
            if (s_coinTried) return s_coinIcon;
            s_coinTried = true;
            // The coin PNG imports as textureType Default (not Sprite), so Resources.Load<Sprite> returns null —
            // load the Texture2D and build a Sprite from it. Same fallback the energy/booster icons use. 2026-07-10.
            // 2026-07-29: the 3D crown coin takes priority — 640px vs the old icon's 59px,
            // and it matches the coin used in the cascade spin sheet so the pill and the
            // flying coins are the same object.
            // TO REVERT: delete Resources/Tiles/coin3d_icon.png — falls straight back to
            // Icon_ImageIcon_Coin with no code change.
            foreach (string path in new[] { "Tiles/coin3d_icon", "Tiles/Icon_ImageIcon_Coin" })
            {
                s_coinIcon = Resources.Load<Sprite>(path);
                if (s_coinIcon != null) break;
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null)
                {
                    s_coinIcon = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    break;
                }
            }
            return s_coinIcon;
        }

        private static Sprite s_heartIcon; private static bool s_heartTried;
        private static Sprite HeartIcon()
        {
            if (s_heartTried) return s_heartIcon;
            s_heartTried = true;
            // 2026-07-30: 3D heart takes priority — 640px vs the old soulgem's 62x53.
            // TO REVERT: delete Resources/Tiles/heart3d_icon.png and this falls straight back.
            foreach (string path in new[] { "Tiles/heart3d_icon", "Tiles/Icon_ImageIcon_Soulgem" })
            {
                s_heartIcon = Resources.Load<Sprite>(path);
                if (s_heartIcon != null) break;
                var tex = Resources.Load<Texture2D>(path);
                if (tex != null)
                {
                    s_heartIcon = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                    break;
                }
            }
            return s_heartIcon;
        }

        private void BuildPath(Vector2 a, Vector2 b)
        {
            var go = new GameObject("Path");
            go.transform.SetParent(_nodesRoot, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            Vector2 mid = (a + b) * 0.5f;
            Vector2 dir = b - a;
            float len = dir.magnitude;
            rt.sizeDelta = new Vector2(14f, len);
            rt.anchoredPosition = mid;
            rt.localRotation = Quaternion.Euler(0, 0, -Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg);
            var img = go.AddComponent<Image>();
            img.sprite = s_circle; // stretched circle → a rounded-ended bar
            img.color = _theme.Path;
            img.raycastTarget = false;
        }

        private static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        private static void EnsureSprites()
        {
            if (s_circle == null) s_circle = TileRenderer.CreateSolidRoundedRect(96, 96, 48, Color.white);
            s_ring = s_circle;
        }

        // ── Dev/test entry (FX Test Menu) ──
        private static int s_debugWorld; // cycles the previewed world on each FX-menu press
        /// <summary>Debug: fire the coin cascade on its own, for tuning COIN_FLY_SIZE /
        /// COIN_END_SCALE / the spin sheet without replaying a level.
        /// The cascade parents to the map canvas and needs the coins pill to exist, so this
        /// shows the map first if it isn't already up, then waits a frame for the UI to build.
        /// 2026-07-29.</summary>
        public void CoinCascadeForDebug(int coins = 250)
        {
            bool needsShow = _canvas == null || !_canvas.gameObject.activeSelf;
            if (needsShow) ShowForDebug();
            StartCoroutine(CoinCascadeDebugRoutine(coins, needsShow));
        }

        private System.Collections.IEnumerator CoinCascadeDebugRoutine(int coins, bool waitForShow)
        {
            // one frame minimum so BuildUI has run; longer if the map is animating in
            yield return null;
            if (waitForShow) yield return new WaitForSecondsRealtime(0.85f);
            if (_canvas == null || _coinsIconRT == null)
            {
                Debug.LogWarning("[LevelMap] CoinCascadeForDebug: map canvas/coins pill not ready.");
                yield break;
            }
            // launch from the lower-middle of the screen, roughly where a level-complete
            // reward would originate
            Vector3 src = new Vector3(Screen.width * 0.5f, Screen.height * 0.34f, 0f);
            CoinWallet.Add(coins);   // so the counter has something real to tick up to
            SpawnCoinCascade(src, coins, () => WDLog.Log($"[LevelMap] debug coin cascade done (+{coins})."));
        }

        public void ShowForDebug()
        {
            _busy = false;
            // Each press previews the NEXT world (mid-batch so you see done/current/locked nodes) — lets you
            // eyeball every theme without playing to level 80. Wraps around. 2026-07-13 Spencer.
            int w = s_debugWorld;
            s_debugWorld = (s_debugWorld + 1) % s_worlds.Length;
            int mid = w * VISIBLE_NODES + 5; // middle-ish level of that world's batch
            Show(mid - 1, mid, () => WDLog.Log($"[LevelMap] debug preview → World {w + 1} ({s_worlds[w].Name})."));
        }
    }
}
