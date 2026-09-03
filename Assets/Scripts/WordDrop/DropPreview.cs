using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Mobile-friendly pre-drop preview. Shows what would happen if the selected
    /// hand tile were dropped into the targeted column:
    ///   - Ghost tile at landing position
    ///   - Word highlight on formed words
    ///   - Gold highlight on triggered primed words
    ///   - Chain indication on connected primed groups
    ///
    /// Non-destructive: uses RulesEngine.SimulateDropWithTriggerCheck (read-only).
    /// Only recalculates when selected tile or target column changes.
    /// </summary>
    public class DropPreview : MonoBehaviour
    {
        public static DropPreview Instance { get; private set; }

        // Tutorial spotlight lifts the ghost above the dim scrim so the "green letter" preview shows. 2026-07-08.
        public static int OrderBoost = 0;

        // ── Preview state ───────────────────────────────────────────────────────
        private char _previewLetter = '\0';
        private int  _previewCol = -1;
        private int  _previewRow = -1;
        private bool _previewActive = false;
        private bool _isWild = false;
        private char _resolvedWildLetter = '\0'; // letter a wild would resolve to here (0 = no word)

        // ── Cached simulation results ───────────────────────────────────────────
        private List<RulesWordMatch> _previewWords;
        private bool _wouldTriggerPrimed = false;
        private List<PrimedWordRegistry.PrimedWord> _triggeredPrimedWords;
        private List<PrimedWordRegistry.PrimedWord> _chainPrimedWords;

        // ── Visual objects ──────────────────────────────────────────────────────
        private GameObject _ghostTile;
        private SpriteRenderer _ghostSR;
        private TMPro.TextMeshPro _ghostLetterTMP;
        private TMPro.TextMeshPro _ghostPointsTMP;

        private List<Tile> _highlightedWordTiles = new List<Tile>();
        private List<Tile> _highlightedPrimedTiles = new List<Tile>();

        private static readonly Color GHOST_COLOR      = new Color(1f, 1f, 1f, 0.35f);
        private static readonly Color GHOST_BORDER     = new Color(0.30f, 0.85f, 0.45f, 0.5f);
        private static readonly Color WORD_HIGHLIGHT   = new Color(0.30f, 0.85f, 0.45f, 0.35f);
        private static readonly Color TRIGGER_HIGHLIGHT = new Color(1f, 0.72f, 0.18f, 0.45f);
        private static readonly Color CHAIN_HIGHLIGHT   = new Color(1f, 0.55f, 0.15f, 0.35f);

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Update the preview for a given tile + column. Only recalculates if
        /// the letter or column actually changed.
        /// </summary>
        public void UpdatePreview(char letter, int col) => UpdatePreview(letter, col, false);

        public void UpdatePreview(char letter, int col, bool isWild)
        {
            if (letter == '\0' || col < 0)
            {
                ClearPreview();
                return;
            }
            _isWild = isWild;

            // Skip recalculation if nothing changed
            if (_previewActive && letter == _previewLetter && col == _previewCol)
                return;

            // Check column is valid
            var grid = GridManager.Instance;
            var rules = RulesEngine.Instance;
            if (grid == null || rules == null)
            {
                ClearPreview();
                return;
            }

            int targetRow = rules.GetLowestEmptyRow(col);
            if (targetRow < 0)
            {
                ClearPreview();
                return;
            }

            // Run simulation (non-destructive)
            _previewWords = rules.SimulateDropWithTriggerCheck(
                col, letter, MatchController.PLAYER_HUMAN, out _wouldTriggerPrimed);

            // Wild: which letter would it resolve to here? '\0' if it makes no word.
            // (Only runs on lane change — UpdatePreview is cached above.) 2026-06-03 Spencer.
            _resolvedWildLetter = _isWild
                ? rules.PreviewWildResolveLetter(col, MatchController.PLAYER_HUMAN)
                : '\0';

            // Find specific triggered primed words + chain group
            _triggeredPrimedWords = new List<PrimedWordRegistry.PrimedWord>();
            _chainPrimedWords = new List<PrimedWordRegistry.PrimedWord>();

            if (_wouldTriggerPrimed && _previewWords != null)
            {
                var registry = rules.PrimedRegistry;
                HashSet<int> triggerIds = new HashSet<int>();

                for (int w = 0; w < _previewWords.Count; w++)
                {
                    var match = _previewWords[w];
                    if (match.Cells == null) continue;
                    for (int c = 0; c < match.Cells.Count; c++)
                    {
                        // Direct overlap
                        var overlapping = registry.GetPrimedWordsContaining(match.Cells[c]);
                        for (int p = 0; p < overlapping.Count; p++)
                        {
                            if (triggerIds.Add(overlapping[p].Id))
                                _triggeredPrimedWords.Add(overlapping[p]);
                        }

                        // Adjacency trigger preview
                        if (RulesEngine.AdjacencyTriggerEnabled)
                        {
                            var adjacent = registry.GetPrimedWordsAdjacentTo(match.Cells[c]);
                            for (int p = 0; p < adjacent.Count; p++)
                            {
                                if (triggerIds.Add(adjacent[p].Id))
                                    _triggeredPrimedWords.Add(adjacent[p]);
                            }
                        }
                    }
                }

                // Find connected chain group
                if (_triggeredPrimedWords.Count > 0)
                {
                    var seedIds = new HashSet<int>();
                    for (int i = 0; i < _triggeredPrimedWords.Count; i++)
                        seedIds.Add(_triggeredPrimedWords[i].Id);

                    _chainPrimedWords = registry.FindConnectedGroup(seedIds, new HashSet<int>());
                }
            }

            // Update state
            _previewLetter = letter;
            _previewCol = col;
            _previewRow = targetRow;
            _previewActive = true;

            // Render
            ClearVisuals();
            RenderGhostTile(grid, col, targetRow, letter);
            RenderWordHighlights(grid);
            RenderPrimedHighlights(grid);

            // Log
            string wordStr = _previewWords != null && _previewWords.Count > 0
                ? string.Join(",", _previewWords.ConvertAll(w => w.Word))
                : "none";
            int chainSize = _chainPrimedWords != null ? _chainPrimedWords.Count : 0;
//             Debug.Log($"[Preview] Tile={letter} Col={col} Landing=({col},{targetRow}) " +
                      // $"Words={wordStr} Trigger={_wouldTriggerPrimed} ChainSize={chainSize}");
        }

        /// <summary>Clear all preview state and visuals.</summary>
        public void ClearPreview()
        {
            if (!_previewActive) return;

            _previewLetter = '\0';
            _previewCol = -1;
            _previewRow = -1;
            _previewActive = false;
            _previewWords = null;
            _wouldTriggerPrimed = false;
            _triggeredPrimedWords = null;
            _chainPrimedWords = null;

            ClearVisuals();
//             Debug.Log("[Preview] Cleared");
        }

        public bool IsActive => _previewActive;

        // ── Visual rendering ────────────────────────────────────────────────────

        private void RenderGhostTile(GridManager grid, int col, int row, char letter)
        {
            Vector3 pos = grid.CellToWorld(col, row);
            float cellSize = grid.CellSize;
            float displaySize = cellSize * Tile.DisplayRatio; // 2026-06-16 Spencer: match the board tile size (was hardcoded 0.88)

            if (_ghostTile == null)
            {
                _ghostTile = new GameObject("PreviewGhost");
                _ghostSR = _ghostTile.AddComponent<SpriteRenderer>();
                _ghostSR.sortingOrder = 4 + OrderBoost; // behind real tiles (5); boosted above the scrim in tutorial
            }

            _ghostTile.transform.position = pos;
            _ghostTile.SetActive(true);

            // Non-wild ghost: show the LIVE tile (test_tile) with a green TINT, instead of the
            // old separate green sprite — matches the new scored-green look. 2026-06-10 Spencer.
            // Wild still uses its own sprites.
            Sprite selectedSprite = _isWild
                ? Resources.Load<Sprite>(Tile.IridescentWild ? "Tiles/white5@2x" : "Tiles/wild2@2x")
                : Tile.NormalSprite;
            if (selectedSprite != null)
            {
                _ghostSR.sprite = selectedSprite;
                // green-tinted + semi-transparent for non-wild; plain semi-transparent for wild.
                _ghostSR.color = _isWild
                    ? new Color(1f, 1f, 1f, 0.5f)
                    : new Color(Tile.SCORED_TINT.r, Tile.SCORED_TINT.g, Tile.SCORED_TINT.b, 0.5f);
                float nativeSize = selectedSprite.bounds.size.x;
                float scale = displaySize / nativeSize;
                _ghostTile.transform.localScale = new Vector3(scale, scale, 1f);
            }
            else
            {
                int texSize = Mathf.Clamp(Mathf.RoundToInt(cellSize * 200f), 64, 512);
                int radius = texSize / 6;
                int border = Mathf.Max(3, texSize / 12);
                Texture2D tex = CreateGhostTexture(texSize, radius, border);
                _ghostSR.sprite = Sprite.Create(tex,
                    new Rect(0, 0, texSize, texSize),
                    new Vector2(0.5f, 0.5f), 100f);
                _ghostSR.color = Color.white;
                float nativeSize = texSize / 100f;
                float scale = displaySize / nativeSize;
                _ghostTile.transform.localScale = new Vector3(scale, scale, 1f);
            }

            // Letter — not bold, semi-transparent
            if (_ghostLetterTMP == null)
            {
                GameObject letterGO = new GameObject("GhostLetter");
                letterGO.transform.SetParent(_ghostTile.transform, false);
                _ghostLetterTMP = letterGO.AddComponent<TMPro.TextMeshPro>();
                _ghostLetterTMP.alignment = TMPro.TextAlignmentOptions.Midline; // match board/hand centering
                _ghostLetterTMP.sortingOrder = 6 + OrderBoost;
                _ghostLetterTMP.enableAutoSizing = false;

                var font = GameFont.GetTMP();
                if (font != null) _ghostLetterTMP.font = font;
                // 2026-06-03 Spencer: effects removed to match board/hand — no dilate.
                _ghostLetterTMP.fontMaterial.SetFloat("_FaceDilate", 0.05f); // very slight bolden, matches board/hand
                _ghostLetterTMP.fontMaterial.DisableKeyword("UNDERLAY_ON");
            }
            // Wild ghost: show the letter the wild would RESOLVE to here if it makes
            // a word, else "?". (Old wild2 sprite baked its own "?", so suppress text.)
            _ghostLetterTMP.text = _isWild
                ? (Tile.IridescentWild
                    ? (_resolvedWildLetter != '\0' ? _resolvedWildLetter.ToString() : "?")
                    : "")
                : letter.ToString();
            _ghostLetterTMP.fontSize = 7.6f; // Avenir: −6%, matches the board tile letter size
            _ghostLetterTMP.fontStyle = TMPro.FontStyles.Bold;
            _ghostLetterTMP.color = new Color(0.15f, 0.15f, 0.18f, 0.35f);
            _ghostLetterTMP.rectTransform.sizeDelta = new Vector2(2f, 2f);
            float sprNative = (_ghostSR.sprite != null) ? _ghostSR.sprite.bounds.size.x : cellSize;
            float invScale = 1f / Mathf.Max(_ghostTile.transform.localScale.x, 0.01f);
            // Centered on tile (no bevel offset — the ghost tile has no point
            // subscript so the letter sits at true center, matching the
            // centered treatment on board/hand tiles).
            _ghostLetterTMP.rectTransform.localPosition = new Vector3(0f, 0f, -0.1f);
            _ghostLetterTMP.rectTransform.localScale = new Vector3(invScale, invScale, 1f);
        }

        private void RenderWordHighlights(GridManager grid)
        {
            if (_previewWords == null) return;

            for (int w = 0; w < _previewWords.Count; w++)
            {
                var match = _previewWords[w];
                if (match.Cells == null) continue;
                for (int c = 0; c < match.Cells.Count; c++)
                {
                    Tile tile = grid.GetTile(match.Cells[c].x, match.Cells[c].y);
                    if (tile != null && !_highlightedWordTiles.Contains(tile))
                    {
                        tile.SetPreviewHighlight(WORD_HIGHLIGHT);
                        _highlightedWordTiles.Add(tile);
                    }
                }
            }
        }

        private void RenderPrimedHighlights(GridManager grid)
        {
            if (_triggeredPrimedWords == null) return;

            // Direct triggers — gold
            for (int i = 0; i < _triggeredPrimedWords.Count; i++)
            {
                var pw = _triggeredPrimedWords[i];
                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Tile tile = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                    if (tile != null && !_highlightedPrimedTiles.Contains(tile))
                    {
                        tile.SetPreviewHighlight(TRIGGER_HIGHLIGHT);
                        _highlightedPrimedTiles.Add(tile);
                    }
                }
            }

            // Chain-connected primed words — orange (skip already highlighted)
            if (_chainPrimedWords != null)
            {
                HashSet<int> directIds = new HashSet<int>();
                for (int i = 0; i < _triggeredPrimedWords.Count; i++)
                    directIds.Add(_triggeredPrimedWords[i].Id);

                for (int i = 0; i < _chainPrimedWords.Count; i++)
                {
                    if (directIds.Contains(_chainPrimedWords[i].Id)) continue;
                    var pw = _chainPrimedWords[i];
                    for (int c = 0; c < pw.Cells.Count; c++)
                    {
                        Tile tile = grid.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                        if (tile != null && !_highlightedPrimedTiles.Contains(tile))
                        {
                            tile.SetPreviewHighlight(CHAIN_HIGHLIGHT);
                            _highlightedPrimedTiles.Add(tile);
                        }
                    }
                }
            }
        }

        private void ClearVisuals()
        {
            // Ghost tile
            if (_ghostTile != null)
                _ghostTile.SetActive(false);

            // Word highlights
            for (int i = 0; i < _highlightedWordTiles.Count; i++)
            {
                if (_highlightedWordTiles[i] != null)
                    _highlightedWordTiles[i].ClearPreviewHighlight();
            }
            _highlightedWordTiles.Clear();

            // Primed highlights
            for (int i = 0; i < _highlightedPrimedTiles.Count; i++)
            {
                if (_highlightedPrimedTiles[i] != null)
                    _highlightedPrimedTiles[i].ClearPreviewHighlight();
            }
            _highlightedPrimedTiles.Clear();

            // Safety net (2026-06-16 Spencer): a preview-highlighted tile can slip out of the tracked
            // lists when the preview recomputes mid-drag ("finding paths"), leaving it stuck green.
            // Sweep the whole grid and clear any STRAY highlight. ClearPreviewHighlight is idempotent
            // (no-op unless the tile actually has a preview highlight) and keeps genuinely-scored tiles
            // green, so this is safe + cheap (~one small grid).
            var grid = GridManager.Instance;
            if (grid != null)
                for (int col = 0; col < GridManager.COLS; col++)
                    for (int row = 0; row < GridManager.ROWS; row++)
                    {
                        var t = grid.GetTile(col, row);
                        if (t != null) t.ClearPreviewHighlight();
                    }
        }

        private Texture2D CreateGhostTexture(int size, int radius, int border)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color fill = GHOST_COLOR;
            Color edge = GHOST_BORDER;
            Color clear = Color.clear;

            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y < size; y++)
                {
                    float dx = Mathf.Max(0, Mathf.Abs(x - size / 2f) - (size / 2f - radius));
                    float dy = Mathf.Max(0, Mathf.Abs(y - size / 2f) - (size / 2f - radius));
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist > radius)
                        tex.SetPixel(x, y, clear);
                    else if (dist > radius - border)
                        tex.SetPixel(x, y, edge);
                    else
                        tex.SetPixel(x, y, fill);
                }
            }
            tex.Apply();
            return tex;
        }

        private void OnDestroy()
        {
            if (_ghostTile != null) Destroy(_ghostTile);
            if (Instance == this) Instance = null;
        }
    }
}
