#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// QUARANTINED DESIGN PROBE (2026-06-24 Spencer) — "authored chain" prototype.
    ///
    /// Tests one question and nothing else: is BUILDING a connected web of armed
    /// words and then PULLING THE TRIGGER yourself more fun than the current
    /// auto-fuse (prime → it blows up on its own a few moves later)?
    ///
    /// It is fully self-contained: it spawns its own fake Tiles via Tile.Initialise
    /// (no GridManager / RulesEngine / MatchController dependency), draws its own
    /// left-side OnGUI panel, and is stripped from release builds. Deleting this one
    /// file removes the entire probe. It NEVER touches the live game loop.
    ///
    /// HOW TO USE (in the editor):
    ///   1. Press "Arm word (+1 link)" a few times — watch gold armed words stack
    ///      into one connected web, with an anticipation tick as it grows.
    ///   2. Press "PULL TRIGGER" — the whole web detonates as one escalating chain:
    ///      rising pitch, ramping shake, a hard hitstop + big burst on the climax,
    ///      and a combo count. THIS is the "you authored the cascade" payoff.
    ///   3. Press "Auto-fuse (old way)" — spawns one word and blows it up flat, with
    ///      no build-up, so you can A/B the feel back to back.
    /// </summary>
    public class ChainPrototype : MonoBehaviour
    {
        public static ChainPrototype Instance { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            // Chain-prototype debug menu RETIRED 2026-07-09 (Spencer) — the chain design is validated + shipped,
            // so stop spawning the OnGUI panel. Class kept for reference; restore the three lines below to re-enable.
            // if (Instance != null) return;
            // var go = new GameObject("ChainPrototype");
            // go.AddComponent<ChainPrototype>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        // ── Words used to build the web (left-aligned rows → vertically adjacent → "connected") ──
        private static readonly string[] WEB_WORDS =
            { "WORD", "PLAY", "STAR", "GEM", "BLOOM", "CANDY", "CHAIN", "POP" };
        private const int   MAX_LINKS = 7;
        private const float SPACING   = 1.0f;   // world units between tile centers
        private const float CELL_SIZE = 1.0f;   // Tile.Initialise scale

        // Each entry is one armed word = one "link" in the chain. Tiles stay on the
        // board (armed gold) until the player pulls the trigger.
        private readonly List<List<Tile>> _links = new List<List<Tile>>();
        private bool _busy;                       // true while a detonation sequence runs

        // Combo flash (drawn in OnGUI for a beat after a trigger).
        private string _comboText;
        private float  _comboUntil;

        // ── UI ──────────────────────────────────────────────────────────────────
        private bool _open = true;
        private GUIStyle _btn, _header, _info, _combo;
        private const int PANEL_X = 16;
        private const int PANEL_W = 300;
        private const int BTN_H   = 50;

        private void EnsureStyles()
        {
            if (_btn != null) return;
            _btn = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            _header = new GUIStyle(GUI.skin.label)
                { fontSize = 16, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.85f, 0.3f) } };
            _info = new GUIStyle(GUI.skin.label)
                { fontSize = 13, wordWrap = true, normal = { textColor = Color.white } };
            _combo = new GUIStyle(GUI.skin.label)
                { fontSize = 64, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter,
                  normal = { textColor = new Color(1f, 0.9f, 0.35f) } };
        }

        private void OnGUI()
        {
            EnsureStyles();

            // Big centered combo flash after a trigger.
            if (Time.realtimeSinceStartup < _comboUntil && !string.IsNullOrEmpty(_comboText))
            {
                var prev = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.55f);
                GUI.Label(new Rect(Screen.width * 0.5f - 398, Screen.height * 0.32f + 4, 800, 100), _comboText, _combo);
                GUI.color = prev;
                GUI.Label(new Rect(Screen.width * 0.5f - 400, Screen.height * 0.32f, 800, 100), _comboText, _combo);
            }

            float y = 80;
            if (GUI.Button(new Rect(PANEL_X, 40, PANEL_W, 30), _open ? "▼ Chain Prototype" : "▶ Chain Prototype", _btn))
                _open = !_open;
            if (!_open) return;

            GUI.Box(new Rect(PANEL_X, y, PANEL_W, 440), GUIContent.none);
            y += 8;

            GUI.Label(new Rect(PANEL_X + 10, y, PANEL_W - 20, 24), "⚡ AUTHORED CHAIN PROBE", _header); y += 28;
            GUI.Label(new Rect(PANEL_X + 10, y, PANEL_W - 20, 56),
                "Arm words to build a connected web, then PULL THE TRIGGER to set the whole thing off at once.", _info);
            y += 62;

            GUI.Label(new Rect(PANEL_X + 10, y, PANEL_W - 20, 22),
                $"Armed links: {_links.Count}   tiles: {TotalTiles()}", _info); y += 26;

            if (GUI.Button(new Rect(PANEL_X + 10, y, PANEL_W - 20, BTN_H), "➕  Arm word (+1 link)", _btn))
                ArmWord();
            y += BTN_H + 6;

            GUI.enabled = _links.Count > 0 && !_busy;
            if (GUI.Button(new Rect(PANEL_X + 10, y, PANEL_W - 20, BTN_H), "💥  PULL TRIGGER", _btn))
                StartCoroutine(PullTrigger());
            GUI.enabled = true;
            y += BTN_H + 6;

            if (GUI.Button(new Rect(PANEL_X + 10, y, PANEL_W - 20, BTN_H), "Auto-fuse (old way)", _btn))
                StartCoroutine(AutoFuse());
            y += BTN_H + 6;

            if (GUI.Button(new Rect(PANEL_X + 10, y, PANEL_W - 20, 34), "Reset board", _btn))
                ResetBoard();
        }

        // ── Building the web ──────────────────────────────────────────────────────

        private int TotalTiles()
        {
            int n = 0;
            for (int i = 0; i < _links.Count; i++) n += _links[i].Count;
            return n;
        }

        private Vector3 CamCenter()
        {
            Camera cam = Camera.main;
            return cam != null ? new Vector3(cam.transform.position.x, cam.transform.position.y, 0f) : Vector3.zero;
        }

        /// <summary>Arms one more word as a new left-aligned row directly beneath the last,
        /// so the rows are vertically adjacent and read as one connected web. Each tile gets
        /// the real game "armed" look (PRIMED_GOLD_GLOW) and stays put until the trigger.</summary>
        private void ArmWord()
        {
            if (_busy) return;
            if (_links.Count >= MAX_LINKS) { Debug.Log("[ChainProto] Max links reached — pull the trigger or reset."); return; }

            string word = WEB_WORDS[_links.Count % WEB_WORDS.Length];
            Vector3 c = CamCenter();
            // Anchor the block near upper-center; each new word is one row lower.
            float originX = c.x - 2f * SPACING;
            float originY = c.y + 2.4f * SPACING - _links.Count * SPACING;

            var row = new List<Tile>(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                var go = new GameObject($"ChainProto_{_links.Count}_{i}");
                var tile = go.AddComponent<Tile>();
                tile.Initialise(word[i], i, _links.Count, CELL_SIZE);
                go.transform.position = new Vector3(originX + i * SPACING, originY, 0f);
                // Real armed-tile look: HDR gold glow that blooms on desktop via sr.color.
                tile.SetPrimedGlow(Tile.PRIMED_GOLD_GLOW, playFlash: true, heatLevel: 0,
                                   fuseRemaining: 999, isGold: true, maxAge: 600f);
                row.Add(tile);
            }
            _links.Add(row);

            // Anticipation: each added link rings a touch higher (the web getting "hotter").
            GameAudio.Instance?.PlayTilePrimed();
            GameAudio.Instance?.PlayMatchLine(_links.Count - 1);
            Debug.Log($"[ChainProto] Armed '{word}' — web now {_links.Count} link(s), {TotalTiles()} tiles.");
        }

        // ── The payoff ────────────────────────────────────────────────────────────

        /// <summary>Detonates the whole connected web as one escalating chain: each link pops
        /// in sequence with rising pitch, growing shake, and a longer hitstop, climaxing on the
        /// last link with a hard freeze + big burst. Bigger web = bigger finale.</summary>
        private IEnumerator PullTrigger()
        {
            if (_busy || _links.Count == 0) yield break;
            _busy = true;

            int totalLinks = _links.Count;
            int totalTiles = TotalTiles();
            Vector3 camBase = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            int comboTiles = 0;

            Debug.Log($"[ChainProto] PULL TRIGGER — {totalLinks} links / {totalTiles} tiles.");

            for (int i = 0; i < _links.Count; i++)
            {
                var link = _links[i];
                bool finale = (i == _links.Count - 1);
                float t = totalLinks <= 1 ? 1f : (float)i / (totalLinks - 1); // 0→1 across the chain

                // Rising audio: matchline pitch climbs per link; layered boom deepens with depth.
                GameAudio.Instance?.PlayMatchLine(i);
                GameAudio.Instance?.PlayDetonationLayered(i);
                // Climax only: a crisp anticipation freeze BEFORE the boom, then release into the
                // explosion at full speed. We freeze the WIND-UP, never the explosion itself — freezing
                // the in-flight explosion (timeScale=0 mid-animation) was the "stall". Non-finale links
                // don't freeze at all, so the cascade keeps flowing.
                if (finale)
                {
                    GameAudio.Instance?.PlayBigPop(0.25f);
                    yield return WordDropFX.HitStop(0.05f);
                }

                // The detonation FX on this link's tiles (flash, fragments, sparkles) — runs UNFROZEN.
                WordDropFX.Instance?.PlayExplosion(link, chainStep: i, wordLength: link.Count);

                if (finale && BigBurstFlash.Instance != null)
                    BigBurstFlash.Instance.Play(LinkCenter(link), 7f, 1.1f, false, null);

                // Screen punch — light early, bigger on the climax (realtime, never frozen).
                float mag = finale ? 0.20f : 0.06f + 0.10f * t;
                float dur = finale ? 0.26f : 0.16f;
                GridManager.Instance?.ShakeBoard(mag, dur);

                comboTiles += link.Count;
                int chainScore = ChainScore(i + 1, comboTiles);
                ShowCombo(i + 1, chainScore, finale);

                yield return ShakeRealtime(camBase, mag, dur); // doubles as the inter-link stagger
            }

            if (Camera.main != null) Camera.main.transform.position = camBase;

            // Clean up the spent tiles after the FX has played out.
            CollectAndDestroy(2.0f);
            _links.Clear();
            _busy = false;
        }

        /// <summary>The current flat single-pop, for an honest A/B: one word, immediate blow-up,
        /// no build, no escalation, no combo.</summary>
        private IEnumerator AutoFuse()
        {
            if (_busy) yield break;
            _busy = true;

            string word = WEB_WORDS[0];
            Vector3 c = CamCenter();
            float originX = c.x - (word.Length - 1) * 0.5f * SPACING;
            var row = new List<Tile>(word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                var go = new GameObject($"ChainProto_autofuse_{i}");
                var tile = go.AddComponent<Tile>();
                tile.Initialise(word[i], i, 0, CELL_SIZE);
                go.transform.position = new Vector3(originX + i * SPACING, c.y, 0f);
                tile.SetPrimedGlow(Tile.PRIMED_GOLD_GLOW, playFlash: true, heatLevel: 0,
                                   fuseRemaining: 999, isGold: true, maxAge: 600f);
                row.Add(tile);
            }

            yield return new WaitForSecondsRealtime(0.5f);     // a beat to read the armed word
            GameAudio.Instance?.PlayDetonationLayered(0);
            WordDropFX.Instance?.PlayExplosion(row, chainStep: 0, wordLength: row.Count);
            yield return new WaitForSecondsRealtime(0.05f);
            GridManager.Instance?.ShakeBoard(0.08f, 0.16f);    // the modest current shake

            yield return new WaitForSecondsRealtime(1.5f);
            foreach (var t in row) if (t != null) Destroy(t.gameObject);
            _busy = false;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        private static int ChainScore(int links, int tiles)
        {
            // Exponential in chain length so a longer chain feels worth chasing:
            // ~ tiles * 10 * links^1.6.
            return Mathf.RoundToInt(tiles * 10f * Mathf.Pow(links, 1.6f));
        }

        private void ShowCombo(int links, int score, bool finale)
        {
            _comboText = finale
                ? $"MEGA CHAIN  x{links}\n+{score}"
                : $"CHAIN  x{links}";
            _comboUntil = Time.realtimeSinceStartup + (finale ? 2.2f : 0.6f);
        }

        private Vector3 LinkCenter(List<Tile> link)
        {
            if (link == null || link.Count == 0) return CamCenter();
            Vector3 sum = Vector3.zero; int n = 0;
            foreach (var t in link) if (t != null) { sum += t.transform.position; n++; }
            return n > 0 ? sum / n : CamCenter();
        }

        private IEnumerator ShakeRealtime(Vector3 basePos, float mag, float dur)
        {
            Camera cam = Camera.main;
            if (cam == null) { yield return new WaitForSecondsRealtime(dur); yield break; }
            float t = 0f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                float falloff = Mathf.Clamp01(1f - t / dur);
                Vector2 o = Random.insideUnitCircle * mag * falloff;
                cam.transform.position = new Vector3(basePos.x + o.x, basePos.y + o.y, basePos.z);
                yield return null;
            }
            cam.transform.position = basePos;
        }

        private void CollectAndDestroy(float delay)
        {
            for (int i = 0; i < _links.Count; i++)
                foreach (var t in _links[i])
                    if (t != null) Destroy(t.gameObject, delay);
        }

        private void ResetBoard()
        {
            for (int i = 0; i < _links.Count; i++)
                foreach (var t in _links[i])
                    if (t != null) Destroy(t.gameObject);
            _links.Clear();
            _comboText = null;
            _busy = false;
        }
    }
}
#endif
