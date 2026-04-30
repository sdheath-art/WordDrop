#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Editor / dev-build debug overlay for iterating on detonation FX without
    /// playing a full Survival session. Auto-spawns at scene load. Adds a
    /// top-right OnGUI panel with:
    ///   - Direct-fire buttons for each FX layer (one-shot, no real tiles needed)
    ///   - "Forced Meltdown" button that flips MeltdownManager.IsActive on,
    ///     fires a faked explosion stack at screen center, flips back off
    ///   - Toggle checkboxes for every WordDropFX.FX_* flag (no recompile)
    ///
    /// Stripped from release builds via #if UNITY_EDITOR || DEVELOPMENT_BUILD.
    /// </summary>
    public class FXTestMenu : MonoBehaviour
    {
        public static FXTestMenu Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("FXTestMenu");
            go.AddComponent<FXTestMenu>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ── UI state ──────────────────────────────────────────────────────────────
        private bool _open = true;
        private Vector2 _scroll;
        private GUIStyle _btnStyle;
        private GUIStyle _toggleStyle;
        private GUIStyle _headerStyle;

        private const int PANEL_W   = 460;
        private const int BTN_H     = 44;
        private const int GAP       = 6;
        private const int TOGGLE_H  = 32;
        private const int HEADER_H  = 34;
        private const int CONTENT_H = 1900;

        private void OnGUI()
        {
            EnsureStyles();

            int x = Screen.width - PANEL_W - 12;
            int y = 12;

            // Collapse / expand header
            if (GUI.Button(new Rect(x, y, PANEL_W, BTN_H),
                           _open ? "▼ FX Test Menu (click to hide)" : "▶ FX Test Menu", _btnStyle))
            {
                _open = !_open;
            }
            y += BTN_H + GAP;
            if (!_open) return;

            int panelH = Mathf.Min(Screen.height - y - 20, 1100);
            GUI.Box(new Rect(x - 4, y - 4, PANEL_W + 8, panelH + 8), GUIContent.none);

            _scroll = GUI.BeginScrollView(new Rect(x, y, PANEL_W, panelH),
                                          _scroll,
                                          new Rect(0, 0, PANEL_W - 20, CONTENT_H));

            int innerY = 0;

            // ── Direct-fire buttons ──────────────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Direct Fire ──", _headerStyle);
            innerY += HEADER_H + 4;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown Prefab @ center", _btnStyle))
                FireMeltdownPrefab();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flipbook Frames @ center", _btnStyle))
                FireFlipbookFrames();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Flipbook Glow (bubble@2x) @ center", _btnStyle))
                FireFlipbookGlow();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "BigBurstFlash horizontal", _btnStyle))
                FireBigBurst(vertical: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "BigBurstFlash vertical", _btnStyle))
                FireBigBurst(vertical: true);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Sparkle Spray", _btnStyle))
                FireSparkleSpray();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Sparkle Line (horizontal)", _btnStyle))
                FireSparkleLine();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Confetti / Meltdown particles", _btnStyle))
                FireConfetti();
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonation Audio (tier 2)", _btnStyle))
                GameAudio.Instance?.PlayDetonation(1);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Board Shake (tier 2)", _btnStyle))
                WordDropFX.Instance?.PlayBoardShake(1, 5);
            innerY += BTN_H + GAP;

            innerY += 14;

            // ── Forced full-stack detonations ────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Forced Detonation ──", _headerStyle);
            innerY += HEADER_H + 4;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 1 (3 tiles)", _btnStyle))
                FireFakeDetonation(chainStep: 0, fakeTileCount: 3, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 2 (6 tiles)", _btnStyle))
                FireFakeDetonation(chainStep: 1, fakeTileCount: 6, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 3 (10 tiles, chain=2)", _btnStyle))
                FireFakeDetonation(chainStep: 2, fakeTileCount: 10, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Tier 4 MELTDOWN (15 tiles)", _btnStyle))
                FireFakeDetonation(chainStep: 3, fakeTileCount: 15, forceMeltdown: true);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Meltdown Intro Only (text + flash)", _btnStyle))
                FireMeltdownIntroOnly();
            innerY += BTN_H + GAP;

            innerY += 8;

            // ── Real-tile detonations — fires FX on actual board tiles, then
            // reactivates them so game state stays clean. Useful for seeing
            // how the explosion reads against the populated grid (vs dummy
            // tiles in empty space).
            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 4 board tiles (Tier 2)", _btnStyle))
                DetonateRealBoardTiles(count: 4, chainStep: 1, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 8 board tiles (Tier 3)", _btnStyle))
                DetonateRealBoardTiles(count: 8, chainStep: 2, forceMeltdown: false);
            innerY += BTN_H + GAP;

            if (GUI.Button(new Rect(0, innerY, PANEL_W - 20, BTN_H), "Detonate 12 tiles MELTDOWN (Tier 4)", _btnStyle))
                DetonateRealBoardTiles(count: 12, chainStep: 3, forceMeltdown: true);
            innerY += BTN_H + GAP;

            innerY += 14;

            // ── Layer toggles ────────────────────────────────────────────────────
            GUI.Label(new Rect(0, innerY, PANEL_W - 20, HEADER_H), "── Layer Toggles ──", _headerStyle);
            innerY += HEADER_H + 4;

            innerY = ToggleRow(innerY, "Meltdown Prefab",        ref WordDropFX.FX_MeltdownPrefab);
            innerY = ToggleRow(innerY, "Meltdown Intro Flash",   ref WordDropFX.FX_MeltdownIntroFlash);
            innerY = ToggleRow(innerY, "Tile Heat Overlay",      ref WordDropFX.FX_TileHeatOverlay);
            innerY = ToggleRow(innerY, "Flipbook Frames",        ref WordDropFX.FX_FlipbookFrames);
            innerY = ToggleRow(innerY, "Flipbook Glow",          ref WordDropFX.FX_FlipbookGlow);
            innerY = ToggleRow(innerY, "Tile Flash",             ref WordDropFX.FX_TileFlash);
            innerY = ToggleRow(innerY, "Tile Fragments",         ref WordDropFX.FX_TileFragments);
            innerY = ToggleRow(innerY, "Tile Flash Box",         ref WordDropFX.FX_TileFlashBox);
            innerY = ToggleRow(innerY, "Sparkle Particles",      ref WordDropFX.FX_SparkleParticles);
            innerY = ToggleRow(innerY, "Sparkle Spray",          ref WordDropFX.FX_SparkleSpray);
            innerY = ToggleRow(innerY, "Sparkle Line",           ref WordDropFX.FX_SparkleLine);
            innerY = ToggleRow(innerY, "Big Burst Flash",        ref WordDropFX.FX_BigBurstFlash);
            innerY = ToggleRow(innerY, "Board Shake",            ref WordDropFX.FX_BoardShake);
            innerY = ToggleRow(innerY, "Confetti",               ref WordDropFX.FX_Confetti);
            innerY = ToggleRow(innerY, "Detonation Audio",       ref WordDropFX.FX_DetonationAudio);
            innerY = ToggleRow(innerY, "Haptics",                ref WordDropFX.FX_Haptics);

            innerY += 10;

            // ── Bulk ─────────────────────────────────────────────────────────────
            if (GUI.Button(new Rect(0, innerY, (PANEL_W - 24) / 2, BTN_H), "All ON", _btnStyle))
                SetAll(true);
            if (GUI.Button(new Rect((PANEL_W - 24) / 2 + 4, innerY, (PANEL_W - 24) / 2, BTN_H), "All OFF", _btnStyle))
                SetAll(false);
            innerY += BTN_H + GAP;

            GUI.EndScrollView();
        }

        private int ToggleRow(int innerY, string label, ref bool flag)
        {
            flag = GUI.Toggle(new Rect(0, innerY, PANEL_W - 20, TOGGLE_H), flag, " " + label, _toggleStyle);
            return innerY + TOGGLE_H;
        }

        private void SetAll(bool v)
        {
            WordDropFX.FX_MeltdownPrefab     = v;
            WordDropFX.FX_MeltdownIntroFlash = v;
            WordDropFX.FX_TileHeatOverlay    = v;
            WordDropFX.FX_FlipbookFrames     = v;
            WordDropFX.FX_FlipbookGlow       = v;
            WordDropFX.FX_TileFlash          = v;
            WordDropFX.FX_TileFragments      = v;
            WordDropFX.FX_TileFlashBox       = v;
            WordDropFX.FX_SparkleParticles   = v;
            WordDropFX.FX_SparkleSpray       = v;
            WordDropFX.FX_SparkleLine        = v;
            WordDropFX.FX_BigBurstFlash      = v;
            WordDropFX.FX_BoardShake         = v;
            WordDropFX.FX_Confetti           = v;
            WordDropFX.FX_DetonationAudio    = v;
            WordDropFX.FX_Haptics            = v;
        }

        // ── Direct-fire helpers ───────────────────────────────────────────────────

        private Vector3 ScreenCenterWorld()
        {
            Camera cam = Camera.main;
            if (cam == null) return Vector3.zero;
            return new Vector3(cam.transform.position.x, cam.transform.position.y, 0f);
        }

        private void FireMeltdownPrefab()
        {
            if (FlipbookExplosion.Instance == null) { Debug.LogWarning("[FXTest] FlipbookExplosion missing"); return; }
            FlipbookExplosion.Instance.PlayMeltdown(ScreenCenterWorld());
        }

        private void FireFlipbookFrames()
        {
            // Force-bypass the toggle so the button always shows its layer.
            bool savedFrames = WordDropFX.FX_FlipbookFrames;
            bool savedGlow   = WordDropFX.FX_FlipbookGlow;
            WordDropFX.FX_FlipbookFrames = true;
            WordDropFX.FX_FlipbookGlow   = false;
            FlipbookExplosion.Instance?.Play(ScreenCenterWorld(), tier: 3);
            WordDropFX.FX_FlipbookFrames = savedFrames;
            WordDropFX.FX_FlipbookGlow   = savedGlow;
        }

        private void FireFlipbookGlow()
        {
            bool savedFrames = WordDropFX.FX_FlipbookFrames;
            bool savedGlow   = WordDropFX.FX_FlipbookGlow;
            WordDropFX.FX_FlipbookFrames = false;
            WordDropFX.FX_FlipbookGlow   = true;
            FlipbookExplosion.Instance?.Play(ScreenCenterWorld(), tier: 3);
            WordDropFX.FX_FlipbookFrames = savedFrames;
            WordDropFX.FX_FlipbookGlow   = savedGlow;
        }

        private void FireBigBurst(bool vertical)
        {
            if (BigBurstFlash.Instance == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            float halfH = cam.orthographicSize;
            float halfW = halfH * cam.aspect;
            float length = (vertical ? halfH : halfW) * 2.4f;
            float thickness = 1.0f;
            BigBurstFlash.Instance.Play(ScreenCenterWorld(), length, thickness, vertical, null);
        }

        private void FireSparkleSpray()
        {
            if (SparkleSpray.Instance == null) return;
            SparkleSpray.Instance.Play(ScreenCenterWorld(), 1f);
        }

        private void FireSparkleLine()
        {
            if (GameParticles.Instance == null) return;
            Camera cam = Camera.main;
            if (cam == null) return;
            float halfW = cam.orthographicSize * cam.aspect;
            Vector3 c = ScreenCenterWorld();
            Vector3 a = new Vector3(c.x - halfW * 1.1f, c.y, c.z);
            Vector3 b = new Vector3(c.x + halfW * 1.1f, c.y, c.z);
            GameParticles.Instance.PlaySparkleLine(a, b, 20);
        }

        private void FireConfetti()
        {
            GameParticles.Instance?.PlayMeltdown(ScreenCenterWorld());
        }

        // ── Forced full-stack detonation ─────────────────────────────────────────

        private void FireFakeDetonation(int chainStep, int fakeTileCount, bool forceMeltdown)
        {
            if (WordDropFX.Instance == null) { Debug.LogWarning("[FXTest] WordDropFX missing"); return; }

            // Build a row of dummy GameObjects with positions only — enough for
            // PlayExplosion's per-tile loops to iterate. The Tile component IS
            // attached but not Initialise()d — FlashHighlight/Shatter check for
            // null sub-components and short-circuit cleanly.
            var fake = new List<Tile>();
            Vector3 c = ScreenCenterWorld();
            float spacing = 0.7f;
            for (int i = 0; i < fakeTileCount; i++)
            {
                var go = new GameObject($"FXTest_DummyTile_{i}");
                go.transform.position = new Vector3(c.x + (i - fakeTileCount * 0.5f + 0.5f) * spacing, c.y, 0f);
                var tile = go.AddComponent<Tile>();
                fake.Add(tile);
                Destroy(go, 5f); // safety cleanup
            }

            if (forceMeltdown)
                StartCoroutine(ForcedMeltdownDetonation(fake, chainStep, fakeTileCount));
            else
                WordDropFX.Instance.PlayExplosion(fake, chainStep, fakeTileCount);
        }

        private void FireMeltdownIntroOnly()
        {
            if (MeltdownManager.Instance == null) { Debug.LogWarning("[FXTest] MeltdownManager missing"); return; }
            StartCoroutine(MeltdownIntroOnlyCoroutine());
        }

        private IEnumerator MeltdownIntroOnlyCoroutine()
        {
            // Flip FX_MeltdownIntroFlash on for the duration so the intro
            // doesn't return null at its own gate, then restore.
            bool saved = WordDropFX.FX_MeltdownIntroFlash;
            WordDropFX.FX_MeltdownIntroFlash = true;

            // High thresholds so GetMeltdownTitle returns a non-null title
            // (chainDepth >= 3 + triggerCount >= 3 lands on the top tier).
            Coroutine intro = MeltdownManager.Instance.TryMeltdownIntro(
                chainDepth: 4, triggerCount: 4, detonationBonus: 200, isLastTurn: false);

            if (intro != null) yield return intro;

            // Hold the stamp briefly so Spencer can read the title clearly.
            yield return new WaitForSeconds(1.0f);

            // Cleanup — Outro fades the overlay + clears _isPlaying so
            // subsequent test fires aren't blocked by the IsActive guard.
            MeltdownManager.Instance.TryMeltdownOutro();

            WordDropFX.FX_MeltdownIntroFlash = saved;
        }

        // ── Real-tile detonation — picks live tiles from the grid, runs FX on
        // them, then reactivates them so RulesEngine/MatchController state
        // stays correct. Caller doesn't need a primed word, edits, or chain.
        private void DetonateRealBoardTiles(int count, int chainStep, bool forceMeltdown)
        {
            if (WordDropFX.Instance == null || GridManager.Instance == null)
            {
                Debug.LogWarning("[FXTest] WordDropFX or GridManager missing");
                return;
            }

            var tiles = new List<Tile>();
            for (int row = 0; row < RulesEngine.ROWS && tiles.Count < count; row++)
                for (int col = 0; col < RulesEngine.COLS && tiles.Count < count; col++)
                {
                    Tile t = GridManager.Instance.GetTile(col, row);
                    if (t != null && t.gameObject.activeSelf) tiles.Add(t);
                }

            if (tiles.Count == 0)
            {
                Debug.LogWarning("[FXTest] No live tiles on the board — drop a few first or run Tier-buttons instead");
                return;
            }

            StartCoroutine(RealDetonationCoroutine(tiles, chainStep, forceMeltdown));
        }

        private IEnumerator RealDetonationCoroutine(List<Tile> tiles, int chainStep, bool forceMeltdown)
        {
            FieldInfo isPlayingField = forceMeltdown
                ? typeof(MeltdownManager).GetField("_isPlaying", BindingFlags.NonPublic | BindingFlags.Instance)
                : null;
            if (forceMeltdown && isPlayingField != null && MeltdownManager.Instance != null)
                isPlayingField.SetValue(MeltdownManager.Instance, true);

            // Cache positions so we can re-place tiles after PlayExplosion's
            // SetActive(false) at end of its loop.
            var positions = new List<Vector3>(tiles.Count);
            for (int i = 0; i < tiles.Count; i++) positions.Add(tiles[i].transform.position);

            yield return WordDropFX.Instance.PlayExplosion(tiles, chainStep, tiles.Count);

            // Hold a beat for the prefab tail / fragments to settle.
            yield return new WaitForSeconds(0.6f);

            // Restore tiles — re-activate the GameObjects and snap positions
            // back (TileFragments shifts the visible tile while shattering
            // sometimes; resetting position guarantees visual integrity).
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i] == null) continue;
                tiles[i].gameObject.SetActive(true);
                tiles[i].transform.position = positions[i];
            }

            if (forceMeltdown && isPlayingField != null && MeltdownManager.Instance != null)
                isPlayingField.SetValue(MeltdownManager.Instance, false);
        }

        private IEnumerator ForcedMeltdownDetonation(List<Tile> fake, int chainStep, int wordLen)
        {
            // Flip MeltdownManager.IsActive for the duration of the explosion so
            // ExplosionCoroutine's meltdown gate fires the prefab. Use reflection
            // to write the private _isPlaying field; avoids adding a public test
            // setter to production code.
            FieldInfo f = typeof(MeltdownManager).GetField("_isPlaying", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null && MeltdownManager.Instance != null)
            {
                f.SetValue(MeltdownManager.Instance, true);
            }

            WordDropFX.Instance.PlayExplosion(fake, chainStep, wordLen);

            // Hold true for ~3s so the prefab's full lifecycle plays under the
            // active flag, then release.
            yield return new WaitForSeconds(3f);
            if (f != null && MeltdownManager.Instance != null)
            {
                f.SetValue(MeltdownManager.Instance, false);
            }
        }

        // ── Styles ────────────────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_btnStyle != null) return;
            _btnStyle    = new GUIStyle(GUI.skin.button) { fontSize = 18, alignment = TextAnchor.MiddleLeft, padding = new RectOffset(10, 10, 6, 6) };
            _toggleStyle = new GUIStyle(GUI.skin.toggle) { fontSize = 16, padding = new RectOffset(6, 6, 4, 4) };
            _headerStyle = new GUIStyle(GUI.skin.label)  { fontSize = 18, fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.7f, 0.9f, 1f) } };
        }
    }
}
#endif
