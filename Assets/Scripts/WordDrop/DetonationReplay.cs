using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using TMPro;

namespace WordDrop
{
    /// <summary>
    /// Plays back a recorded detonation chain in slow motion on the game board.
    /// Kill-cam style replay that fires before the game over panel appears.
    ///
    /// Flow:
    ///   1. GameOverUI calls PlayReplay() before showing scores
    ///   2. Board is cleared and rebuilt from the snapshot
    ///   3. Each step plays in slow motion (1.5-2x slower than normal)
    ///   4. "BEST CHAIN" label shown during playback
    ///   5. Final score popup, then board cleared, then GameOverUI continues
    /// </summary>
    public class DetonationReplay : MonoBehaviour
    {
        public static DetonationReplay Instance { get; private set; }

        // ── Timing (slower than normal for dramatic effect) ─────────────────────

        private const float REPLAY_SPEED_MULT    = 1.8f;  // everything takes 1.8x longer
        private const float PRE_REPLAY_PAUSE     = 0.5f;  // pause before replay starts
        private const float STEP_PAUSE            = 0.15f; // pause between steps
        private const float WORD_HIGHLIGHT_DUR    = 0.4f;  // how long word highlight shows
        private const float TRIGGER_BEAT          = 0.35f; // pause after triggers shown
        private const float EXPLOSION_EXTRA_PAUSE = 0.25f; // extra time after explosion
        private const float GRAVITY_PAUSE         = 0.2f;  // pause after gravity
        private const float POST_REPLAY_PAUSE     = 1.0f;  // pause after replay ends
        private const float LABEL_FADE_DUR        = 0.3f;  // label fade in/out

        // ── Colors ──────────────────────────────────────────────────────────────

        private static readonly Color REPLAY_LABEL_COLOR = new Color(0.961f, 0.761f, 0.294f, 1f); // gold
        private static readonly Color PLAYER_COLOR       = new Color(0.29f, 0.87f, 0.31f, 1f);

        // ── State ───────────────────────────────────────────────────────────────

        private bool _isPlaying;
        public bool IsPlaying => _isPlaying;

        // ── UI refs ─────────────────────────────────────────────────────────────

        private Canvas _replayCanvas;
        private GameObject _labelGO;
        private TextMeshProUGUI _labelText;
        private CanvasGroup _labelCG;

        // ── Unity lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildLabel();
//             Debug.Log("[DetonationReplay] Awake");
        }

        // ── Label setup ─────────────────────────────────────────────────────────

        private void BuildLabel()
        {
            // Create a small overlay canvas for the "BEST CHAIN" label
            GameObject canvasGO = new GameObject("ReplayCanvas");
            canvasGO.transform.SetParent(transform, false);

            _replayCanvas = canvasGO.AddComponent<Canvas>();
            _replayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _replayCanvas.sortingOrder = 9500; // below GameOverUI (10000) but above everything else

            var scaler = canvasGO.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(540f, 960f);
            scaler.matchWidthOrHeight = 1f;

            // Label
            _labelGO = new GameObject("ReplayLabel");
            _labelGO.transform.SetParent(canvasGO.transform, false);

            RectTransform rt = _labelGO.AddComponent<RectTransform>();
            rt.anchorMin = new Vector2(0.05f, 0.88f);
            rt.anchorMax = new Vector2(0.95f, 0.96f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            _labelText = _labelGO.AddComponent<TextMeshProUGUI>();
            TMP_FontAsset uiFont = GameFont.GetDisplayTMP();
            if (uiFont != null) _labelText.font = uiFont;
            _labelText.text = "BEST CHAIN";
            _labelText.fontSize = 42;
            _labelText.fontStyle = FontStyles.Normal;
            _labelText.color = REPLAY_LABEL_COLOR;
            _labelText.alignment = TextAlignmentOptions.Center;
            _labelText.enableWordWrapping = false;
            TMPHelper.ApplyEffects(_labelText, REPLAY_LABEL_COLOR, TMPHelper.TextTier.Announcement);

            _labelCG = _labelGO.AddComponent<CanvasGroup>();
            _labelCG.alpha = 0f;

            _labelGO.SetActive(false);
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Starts the replay coroutine. Returns the Coroutine so the caller can yield on it.
        /// Returns null if there's nothing to replay.
        /// </summary>
        public Coroutine PlayReplay(DetonationRecorder.RecordedChain chain)
        {
            if (chain == null || chain.Steps.Count == 0) return null;
            if (_isPlaying) return null;

            return StartCoroutine(ReplayCoroutine(chain));
        }

        // ── Core replay coroutine ───────────────────────────────────────────────

        private IEnumerator ReplayCoroutine(DetonationRecorder.RecordedChain chain)
        {
            _isPlaying = true;

            GridManager grid = GridManager.Instance;
            if (grid == null)
            {
                Debug.LogError("[DetonationReplay] GridManager is null — cannot replay.");
                _isPlaying = false;
                yield break;
            }

//             Debug.Log($"[DetonationReplay] Starting replay: {chain.Steps.Count} steps, " +
                      // $"depth={chain.MaxChainDepth}, bonus={chain.TotalDetonationBonus}");

            // 1. Clear the board and rebuild from snapshot
            grid.ClearAllCells();
            yield return null; // frame for cleanup

            RebuildBoardFromSnapshot(grid, chain.BoardSnapshot);
            yield return null; // frame for tile creation

            // 2. Show "BEST CHAIN" label
            ShowLabel();
            yield return new WaitForSeconds(PRE_REPLAY_PAUSE);

            // 2.5. Show the triggering action — drop or rewrite
            if (chain.DroppedLetter != '\0')
            {
                if (chain.IsRewrite)
                {
                    // Rewrite: swap the letter on the existing tile with a shake animation
                    Tile rewriteTile = grid.GetTile(chain.DroppedCol, chain.DroppedRow);
                    if (rewriteTile != null)
                    {
                        Vector3 restPos = rewriteTile.transform.position;
                        float cellSize = grid.CellSize;
                        float posJitter = cellSize * 0.08f;
                        float rotJitter = 10f;

                        // Shake
                        float shakeDur = 0.2f * REPLAY_SPEED_MULT;
                        float elapsed = 0f;
                        while (elapsed < shakeDur)
                        {
                            elapsed += Time.deltaTime;
                            float ox = Random.Range(-posJitter, posJitter);
                            float oy = Random.Range(-posJitter, posJitter) * 0.5f;
                            rewriteTile.transform.position = restPos + new Vector3(ox, oy, 0f);
                            float rz = Random.Range(-rotJitter, rotJitter);
                            rewriteTile.transform.localRotation = Quaternion.Euler(0f, 0f, rz);
                            yield return null;
                        }

                        // Swap letter
                        rewriteTile.SetLetter(chain.DroppedLetter);
                        rewriteTile.transform.position = restPos;
                        rewriteTile.transform.localRotation = Quaternion.identity;
                        rewriteTile.PlayLandingSquish();
                    }
                    yield return new WaitForSeconds(0.3f * REPLAY_SPEED_MULT);
                }
                else
                {
                    // Normal drop
                    grid.DropTile(chain.DroppedCol, chain.DroppedLetter, TileOwner.Player);
                    yield return new WaitForSeconds(0.4f * REPLAY_SPEED_MULT);
                }
            }

            // 3. Play through each step in slow motion
            for (int i = 0; i < chain.Steps.Count; i++)
            {
                var step = chain.Steps[i];
                yield return StartCoroutine(PlayStep(grid, step));
                yield return new WaitForSeconds(STEP_PAUSE * REPLAY_SPEED_MULT);
            }

            // 4. Final score popup
            if (BonusPopup.Instance != null && chain.TotalDetonationBonus > 0)
            {
                Vector3 center = new Vector3(
                    (grid.GridLeft + grid.GridRight) / 2f,
                    (grid.GridBottom + grid.GridTop) / 2f,
                    0f);
                BonusPopup.Instance.ShowDetonation(
                    "TOTAL", chain.TotalDetonationBonus, center, chain.MaxChainDepth);
            }

            yield return new WaitForSeconds(POST_REPLAY_PAUSE);

            // 5. Hide label and clean up
            yield return StartCoroutine(HideLabel());

            // 6. Clear the board for game over screen
            grid.ClearAllCells();
            yield return null;

            _isPlaying = false;
//             Debug.Log("[DetonationReplay] Replay complete.");
        }

        // ── Step playback ───────────────────────────────────────────────────────

        private IEnumerator PlayStep(GridManager grid, DetonationRecorder.RecordedStep step)
        {
            switch (step.Phase)
            {
                case RulesEngine.ResolutionPhase.WordsScored:
                    yield return StartCoroutine(PlayWordsScoredStep(grid, step));
                    break;

                case RulesEngine.ResolutionPhase.TriggersFound:
                    yield return StartCoroutine(PlayTriggersStep(grid, step));
                    break;

                case RulesEngine.ResolutionPhase.Exploding:
                    yield return StartCoroutine(PlayExplosionStep(grid, step));
                    break;

                case RulesEngine.ResolutionPhase.GravityApplied:
                    yield return StartCoroutine(PlayGravityStep(grid, step));
                    break;
            }
        }

        private IEnumerator PlayWordsScoredStep(GridManager grid, DetonationRecorder.RecordedStep step)
        {
            if (step.ScoredWords == null) yield break;

            foreach (var sw in step.ScoredWords)
            {
                if (sw.Cells == null) continue;

                List<Tile> tiles = new List<Tile>();
                foreach (var cell in sw.Cells)
                {
                    Tile t = grid.GetTile(cell.x, cell.y);
                    if (t != null) tiles.Add(t);
                }

                if (tiles.Count > 0 && WordDropFX.Instance != null)
                    WordDropFX.Instance.PlayWordScored(tiles, PLAYER_COLOR, 0);

                yield return new WaitForSeconds(WORD_HIGHLIGHT_DUR * REPLAY_SPEED_MULT);
            }
        }

        private IEnumerator PlayTriggersStep(GridManager grid, DetonationRecorder.RecordedStep step)
        {
            if (step.Triggers == null) yield break;

            // Fuse trace (gated by FX_FuseTrace, default off per Spencer 2026-05-19)
            if (WordDropFX.FX_FuseTrace && WordDropFX.Instance != null)
                WordDropFX.Instance.PlayFuseTrace(step.Triggers, grid);

            yield return new WaitForSeconds(0.08f * REPLAY_SPEED_MULT);

            foreach (var trig in step.Triggers)
            {
                if (trig.TriggeredCells == null) continue;

                List<Tile> trigTiles = new List<Tile>();
                foreach (var cell in trig.TriggeredCells)
                {
                    Tile t = grid.GetTile(cell.x, cell.y);
                    if (t != null) trigTiles.Add(t);
                }

                if (trigTiles.Count > 0 && WordDropFX.Instance != null)
                    WordDropFX.Instance.PlayDetonation(trigTiles, step.ChainDepth);

                // Replay: no score popups — just the visual explosion

                yield return new WaitForSeconds(TRIGGER_BEAT * REPLAY_SPEED_MULT);
            }
        }

        private IEnumerator PlayExplosionStep(GridManager grid, DetonationRecorder.RecordedStep step)
        {
            if (step.ExplodedCells == null || step.ExplodedCells.Count == 0) yield break;

            List<Tile> dying = new List<Tile>();
            foreach (var cell in step.ExplodedCells)
            {
                Tile t = grid.GetTile(cell.x, cell.y);
                if (t != null) dying.Add(t);
            }

            // Chain counter
            if (ChainCounter.Instance != null)
                ChainCounter.Instance.OnDetonation(step.ChainDepth);

            // Compute center for bonus popup
            Vector3 center = Vector3.zero;
            int validCount = 0;
            foreach (var t in dying)
            {
                if (t != null) { center += t.transform.position; validCount++; }
            }
            if (validCount > 0) center /= validCount;

            // Hitstop — brief freeze
            if (dying.Count > 0)
            {
                float hitStopDur = step.ChainDepth >= 2 ? 0.10f : 0.06f;
                yield return StartCoroutine(WordDropFX.HitStop(hitStopDur));
            }

            // Big-moment burst during replay — same gate as in-game play.
            // Recorded steps only carry chain depth + explode cells, so we infer
            // orientation from the cell spread and fire a single centered sweep.
            bool replayBigMoment = step.ChainDepth >= 2 || dying.Count >= 6;
            if (replayBigMoment && BigBurstFlash.Instance != null && step.ExplodedCells != null && step.ExplodedCells.Count > 0)
            {
                int minCol = int.MaxValue, maxCol = int.MinValue;
                int minRow = int.MaxValue, maxRow = int.MinValue;
                foreach (var cell in step.ExplodedCells)
                {
                    if (cell.x < minCol) minCol = cell.x; if (cell.x > maxCol) maxCol = cell.x;
                    if (cell.y < minRow) minRow = cell.y; if (cell.y > maxRow) maxRow = cell.y;
                }
                bool vertical = (maxCol - minCol) < (maxRow - minRow);
                Camera cam = Camera.main;
                float halfH = cam != null ? cam.orthographicSize : 10f;
                float halfW = halfH * ((float)Screen.width / Screen.height);
                float length = (vertical ? halfH : halfW) * 2.4f;
                float thickness = grid.CellSize * 1.4f;
                Color tint = new Color(1.8f, 1.4f, 0.7f, 1f); // warm gold — it's a "best chain" highlight
                BigBurstFlash.Instance.Play(center, length, thickness, vertical, tint);

                // Phase 11i — RadialBurst removed (was stacking with BigBurstFlash's
                // own radial layer + ScreenFlash to compound into screen-wide
                // white-out). BigBurstFlash now emits its own radial halo.

                if (GameParticles.Instance != null)
                {
                    Vector3 lineStart, lineEnd;
                    if (vertical)
                    {
                        lineStart = new Vector3(center.x, center.y - halfH * 1.1f, center.z);
                        lineEnd   = new Vector3(center.x, center.y + halfH * 1.1f, center.z);
                    }
                    else
                    {
                        lineStart = new Vector3(center.x - halfW * 1.1f, center.y, center.z);
                        lineEnd   = new Vector3(center.x + halfW * 1.1f, center.y, center.z);
                    }
                    GameParticles.Instance.PlaySparkleLine(lineStart, lineEnd, 20);
                }
            }

            // Explosion FX — pass tile count so the tier matches the actual blast size
            if (WordDropFX.Instance != null && dying.Count > 0)
                yield return WordDropFX.Instance.PlayExplosion(dying, step.ChainDepth, dying.Count);

            // Remove tiles from grid
            grid.RemoveTiles(step.ExplodedCells);

            // Replay: no bonus popups — pure visual replay

            yield return new WaitForSeconds(EXPLOSION_EXTRA_PAUSE * REPLAY_SPEED_MULT);
        }

        private IEnumerator PlayGravityStep(GridManager grid, DetonationRecorder.RecordedStep step)
        {
            if (step.GravityMoves == null || step.GravityMoves.Count == 0) yield break;

            yield return StartCoroutine(grid.ApplyGravityFromEvents(step.GravityMoves));
            yield return new WaitForSeconds(GRAVITY_PAUSE * REPLAY_SPEED_MULT);
        }

        // ── Board rebuild ───────────────────────────────────────────────────────

        private void RebuildBoardFromSnapshot(GridManager grid, DetonationRecorder.CellSnapshot[,] snapshot)
        {
            if (snapshot == null) return;

            int created = 0;
            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                for (int row = 0; row < RulesEngine.ROWS; row++)
                {
                    var snap = snapshot[col, row];
                    if (snap == null) continue;

                    // Skip stones in replay — don't show '#' as a letter
                    if (snap.IsStone || snap.Letter == '#')
                    {
                        Tile stoneTile = grid.CreateSingleTile(col, row, 'X'); // placeholder letter
                        if (stoneTile != null) stoneTile.SetStoneVisual(true);
                        created++;
                        continue;
                    }

                    Tile tile = grid.CreateSingleTile(col, row, snap.Letter);
                    if (tile != null)
                    {
                        // Apply primed glow if the cell was primed during the original game
                        if (snap.IsPrimed)
                        {
                            tile.SetPrimedGlow(Tile.PRIMED_GLOW, playFlash: false,
                                heatLevel: snap.HeatLevel, fuseRemaining: snap.FuseRemaining);
                        }
                        created++;
                    }
                }
            }

            // Debug: log all letters in snapshot
            var sb = new System.Text.StringBuilder();
            sb.Append("[DetonationReplay] Snapshot letters (bottom to top):\n");
            for (int row = 0; row < RulesEngine.ROWS; row++)
            {
                sb.Append($"  Row {row}: ");
                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    var s = snapshot[col, row];
                    sb.Append(s != null ? s.Letter.ToString() : ".");
                }
                sb.Append("\n");
            }
//             Debug.Log(sb.ToString());
//             Debug.Log($"[DetonationReplay] Rebuilt board from snapshot: {created} tiles");
        }

        // ── Label animation ─────────────────────────────────────────────────────

        private void ShowLabel()
        {
            if (_labelGO == null) return;
            _labelGO.SetActive(true);
            _labelCG.alpha = 0f;

            // Fade in + scale pop
            DOTween.To(() => _labelCG.alpha, a => _labelCG.alpha = a, 1f, LABEL_FADE_DUR)
                .SetEase(Ease.OutQuad);

            _labelGO.transform.localScale = Vector3.one * 0.7f;
            _labelGO.transform.DOScale(Vector3.one, LABEL_FADE_DUR)
                .SetEase(Ease.OutBack, 2f);

            // Gentle pulse
            _labelGO.transform.DOPunchScale(Vector3.one * 0.08f, 0.3f, 6, 0.5f)
                .SetDelay(LABEL_FADE_DUR);
        }

        private IEnumerator HideLabel()
        {
            if (_labelGO == null) yield break;

            DOTween.To(() => _labelCG.alpha, a => _labelCG.alpha = a, 0f, LABEL_FADE_DUR)
                .SetEase(Ease.InQuad);

            yield return new WaitForSeconds(LABEL_FADE_DUR);

            _labelGO.SetActive(false);
        }
    }
}
