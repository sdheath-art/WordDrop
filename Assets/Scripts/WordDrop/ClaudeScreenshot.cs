using System.Collections;
using System.IO;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// ONE-TAP SCREENSHOT FOR CLAUDE (2026-10-23 Spencer: "build a function where i can just tap a button inside unity and it
    /// will take a screenshot of the game for you"). Press F8 in Play mode (or the FX Test Menu button) and the Game view is
    /// saved to UnityProject/Screenshots/claude_latest.png — a FIXED path Claude reads directly, no Desktop round-trip — plus a
    /// timestamped copy so a sequence can be compared. The FX Test Menu hides itself for the capture frame.
    /// Editor/dev tool only — nothing calls it in normal play.
    /// </summary>
    public class ClaudeScreenshot : MonoBehaviour
    {
        public static ClaudeScreenshot Instance { get; private set; }
        public static KeyCode HOTKEY = KeyCode.F8;   // F9/F10 belong to CaptureRig (frame-burst capture). On a Mac this needs fn+F8
        public static KeyCode HOTKEY_ALT = KeyCode.BackQuote;   // ` (under Esc) — no fn needed; F8 alone is the Mac play/pause media key
        /// <summary>True for the frame(s) a capture is being taken — overlays (the FX Test Menu) skip drawing.</summary>
        public static bool Capturing { get; private set; }
        public static int KEEP_TIMESTAMPED = 30;   // older timestamped shots are deleted so the folder doesn't balloon

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("ClaudeScreenshot");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<ClaudeScreenshot>();
            WDLog.Log($"[ClaudeShot] ready — press ` (backtick) or fn+F8, or the FX Test Menu button -> {Folder}/claude_latest.png");
        }

        public static string Folder => Path.Combine(Path.GetDirectoryName(Application.dataPath), "Screenshots");

        private void Update()
        {
            if (Input.GetKeyDown(HOTKEY) || Input.GetKeyDown(HOTKEY_ALT)) Take();
            if (Input.GetKeyDown(NOTE_KEY)) Mark();
        }

        // PLAY NOTES (2026-10-23 Spencer: "can i tell you my thoughts while i play and you check the logs"). . (period) drops a findable
        // marker into the log at this exact moment — level, moves, goal pieces left, board idle or not — so a note sent later
        // can be matched to the board. Editor + dev builds only (the whole component is).
        public static KeyCode NOTE_KEY = KeyCode.Period;   // M = music mute, N = no-assist toggle — both taken
        private static int s_noteNum;
        public static void Mark()
        {
            var sm = SurvivalManager.Instance;
            int lvl = sm != null ? LevelMapPanel.RunLevel(sm.CurrentStageIndex) : -1;
            string moves = sm != null ? $"{sm.CurrentStageMovesRemaining} moves left" : "";
            int pieces = RulesEngine.GoalPiecesLeft();
            s_noteNum++;
            WDLog.Log($"[NOTE] #{s_noteNum} ===== LEVEL {lvl}, {moves}, {pieces} goal piece(s) left, streak {WinStreak.Count}, " +
                      $"no-word streak {HandManager.NoWordStreak} =====");
            GameAudio.Instance?.PlayUIClick();
            DumpOddTiles();
            var hm = HintManager.Instance;
            WDLog.Log($"[NOTE-HINT] hint showing={(hm != null && hm.IsShowingHint)} cells=({HintManager.LastHintA.x},{HintManager.LastHintA.y})<->({HintManager.LastHintB.x},{HintManager.LastHintB.y}) word='{HintManager.LastHintWord}'");
        }

        // TEMP DIAG 2026-10-23 ("letters turned into gems" x4, faces + flags both read clean): every tile whose face isn't white /
        // resting, or that has any extra SpriteRenderer switched on, with all of them listed — names the layer drawing the gold.
        private static void DumpOddTiles()
        {
            var grid = GridManager.Instance; var rules = RulesEngine.Instance; if (grid == null) return;
            int n = 0;
            for (int x = 0; x < RulesEngine.COLS; x++)
                for (int y = 0; y < RulesEngine.ROWS; y++)
                {
                    var t = grid.GetTile(x, y); if (t == null) continue;
                    var main = t.GetComponent<SpriteRenderer>();
                    var sb = new System.Text.StringBuilder();
                    foreach (var sr in t.GetComponentsInChildren<SpriteRenderer>(false))
                    {
                        if (sr == main || !sr.enabled || sr.sprite == null) continue;
                        if (sr.color.a < 0.05f || sr.gameObject.name == "TileInnerShadow" || sr.gameObject.name == "LetterPlate") continue;   // always on — noise
                        sb.Append($" +{sr.gameObject.name}:{sr.sprite.name}({sr.color.r:F1},{sr.color.g:F1},{sr.color.b:F1},{sr.color.a:F2})");
                    }
                    bool gemFace = main != null && main.sprite != null && main.sprite == Tile.GemSprite;   // procedural sprites have no names — compare the reference
                    string faceRep = t.DebugFaceReport(out bool faceOdd);
                    bool oddMain = main != null && (main.color != Color.white || gemFace || t.IsGem || t.HasPrimedGlow || faceOdd);
                    if (!oddMain && sb.Length == 0) continue;
                    var c = rules != null ? rules.GetCell(x, y) : null;
                    WDLog.Log($"[NOTE-TILE] ({x},{y}) '{t.Letter}' GEMFACE={gemFace} tint={(main != null ? main.color.ToString() : "-")} " +
                              $"dataGem={(c != null && c.HasGem)} tileGem={t.IsGem} special={t.SpecialKind} {faceRep}{sb}");
                    if (++n >= 40) return;
                }
            if (n == 0) WDLog.Log("[NOTE-TILE] no odd tiles");
        }

        public void Take()
        {
            if (!Capturing) StartCoroutine(CaptureRoutine());
        }

        private IEnumerator CaptureRoutine()
        {
            Capturing = true;
            yield return null;                         // one frame with overlays hidden
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            Capturing = false;
            if (tex == null) { Debug.LogWarning("[ClaudeShot] capture failed"); yield break; }
            byte[] png = tex.EncodeToPNG();
            Destroy(tex);
            try
            {
                Directory.CreateDirectory(Folder);
                string latest = Path.Combine(Folder, "claude_latest.png");
                File.WriteAllBytes(latest, png);
                string stamped = Path.Combine(Folder, $"claude_{System.DateTime.Now:yyyyMMdd_HHmmss}.png");
                File.WriteAllBytes(stamped, png);
                var old = Directory.GetFiles(Folder, "claude_2*.png");
                System.Array.Sort(old);
                for (int i = 0; i < old.Length - KEEP_TIMESTAMPED; i++) File.Delete(old[i]);
                WDLog.Log($"[ClaudeShot] saved {latest} ({png.Length / 1024} KB) + {Path.GetFileName(stamped)}");
            }
            catch (System.Exception ex) { Debug.LogError($"[ClaudeShot] write failed: {ex.Message}"); }
        }
    }
}
