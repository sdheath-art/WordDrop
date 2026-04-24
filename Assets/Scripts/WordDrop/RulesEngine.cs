using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Authoritative board state owner for the Scrabble-drop game.
    /// Maintains a 7×6 cell array INDEPENDENT of GridManager's visual layer.
    ///
    /// Job 2: FindNewWords — horizontal + vertical word detection (no diagonals)
    /// Job 4: CalculateWordScore, ProcessDrop, ExplodePrimedWord, gravity, chain loop
    /// Job 9: Enhanced SimulateDrop — non-mutating evaluation for AI
    ///
    /// Event flow:
    ///   ProcessDrop() emits events via C# delegates:
    ///     OnTileDropped, OnWordScored, OnWordPrimed, OnPrimedTriggered,
    ///     OnTilesExploded, OnGravityCollapse, OnChainStep, OnResolutionComplete
    /// </summary>
    public class RulesEngine : MonoBehaviour
    {
        // ── Constants ─────────────────────────────────────────────────────────────

        // Board dimensions — COLS is fixed, ROWS is mode-dependent.
        // GridManager mirrors ROWS via RulesEngine.ROWS; keep in sync.
        // Classic/Blitz = 5 rows (legacy). Survival = 8 rows. Level = 8 rows
        // (Path B upgrade 2026-04-22: raised from 5 to match Survival's proven
        // cascade grid — cascades require vertical room above detonation
        // clusters for gravity-fed secondary triggers).
        //
        // LEVEL_ROWS is a compile-time constant so LevelValidator can reference
        // it directly without depending on the mode-dependent ROWS property.
        // The validator runs BEFORE GameMode.CurrentMode is set to Level
        // (LevelDebugMenu/MenuUI/LevelCompletedModal all Validate → then set
        // mode → then TransitionTo(Playing)), so at validation time the
        // dynamic ROWS property would still return 5. Using LEVEL_ROWS keeps
        // the validator consistent with Level's actual runtime row count.
        public const int COLS       = 6;
        public const int MAX_ROWS   = 9;
        public const int LEVEL_ROWS = 8;
        public static int ROWS => (SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode) ? LEVEL_ROWS : 5;

        /// <summary>
        /// Kill switch for adjacency-based detonation triggering.
        /// When true: a new word touching a primed word (orthogonal adjacency) triggers it.
        /// When false: only direct cell overlap triggers (original behavior).
        /// </summary>
        public static bool AdjacencyTriggerEnabled = false;

        /// <summary>
        /// Kill switch for junk splash damage on detonation.
        /// When true: detonations clear 1-2 nearby non-special tiles as collateral.
        /// Prevents junk towers from accumulating on the sides.
        /// </summary>
        public static bool JunkSplashEnabled = true;

        /// <summary>
        /// Kill switch for post-gravity fertility repair.
        /// When true: after gravity, if the board has too few near-words,
        /// silently nudge 1-2 tiles to create opportunities.
        /// </summary>
        public static bool PostGravityFertilityEnabled = true;

        private const int MIN_WORD_LENGTH   = 3;
        private const int MAX_WORD_LENGTH   = 7;

        /// <summary>
        /// Phase 11+ rare-letter premium set. Any word in Survival containing
        /// at least one of these letters gets a flat ×2 multiplier on top of
        /// the length-tier mult. Kept tight — these are the four letters
        /// every Scrabble player instinctively prices as "hard."
        /// </summary>
        private static readonly char[] RARE_LETTERS = { 'Q', 'Z', 'X', 'J' };
        private const int MAX_CHAIN_DEPTH   = 12;
        private const int CHAIN_BONUS       = 3;

        // Primed words expire after this many turns if not detonated.
        // 3 turns: balanced — enough time to set up detonations without board clutter.
        // 2 turns: too tight — words often expire before opponent can interact with them.
        // 4 turns = player primes on their turn, survives AI turn, survives player's
        // next turn, detonatable on the turn AFTER that. Generous enough to actually use.
        // Classic: flat 4-turn expiry. Survival: word length (3-6 placements).
        private const int PRIMED_EXPIRY_TURNS = 4;
        private const int SURVIVAL_FUSE_CAP   = 6;

        /// <summary>Returns the fuse length for a primed word. Survival: word length capped at 6. Classic: flat 4.</summary>
        private static int GetFuseLength(int wordLength)
        {
            if (SurvivalManager.IsSurvivalMode)
                return Mathf.Min(wordLength, SURVIVAL_FUSE_CAP);
            return PRIMED_EXPIRY_TURNS;
        }

        // ── Detonation scoring tuning ───────────────────────────────────────────
        // Controls how much score a detonation awards.
        //   DETONATION_SCORE_MULTIPLIER: fraction of primed word's stored score
        //     (0 = ignore stored score, 0.5 = half, 1.0 = full double-word)
        //   BREAKER_BONUS: flat points per detonation regardless of word value
        //
        // Formula: bonus = RoundToInt(pw.Score * MULTIPLIER) + BREAKER_BONUS
        //
        // Tuning presets:
        //   Flat only:    MULTIPLIER=0,   BONUS=2  (current — simple, predictable)
        //   Scaled:       MULTIPLIER=0.5, BONUS=1  (rewards big-word primes)
        //   Full double:  MULTIPLIER=1.0, BONUS=0  (maximum detonation reward)
        //   Hybrid:       MULTIPLIER=0.5, BONUS=2  (scaled + flat floor)
        public const float DETONATION_SCORE_MULTIPLIER = 1.5f; // 1.5x primed word's score on detonation
        public const int   BREAKER_BONUS               = 10;  // flat floor — short primed words still feel explosive

        // Chain-Depth Scaling: TRIANGULAR curve — each depth feels dramatically bigger.
        // Uses triangular formula: multiplier = 1 + (depth² + depth) / 2
        // depth 0: 1.0x, depth 1: 2.0x, depth 2: 4.0x, depth 3: 7.0x, depth 4+: capped at 7.0x
        // Detonation sites evaluate at (_stepChainDepth + 1) so the OPENING boom is 2x,
        // not a flat 1x — fixes the "first detonation feels identical to a safe word" gap.
        public const float CHAIN_DEPTH_SCALE_PER_DEPTH = 0.5f; // unused — kept for compat, see TriangularChainMultiplier
        public const int   CHAIN_DEPTH_SCALE_CAP       = 3;   // max chain depth for triangular scaling (tried 8 on 2026-04-18 + cluster boost in DoExplode — combined effect made every move a board-clearing mega-combo. Reverted to 3.)

        // Heat Fuse: primed words gain +N detonation bonus per survived turn, capped.
        // 2 pts/turn capped at 10 → 5 turns to max, matches Survival word-length fuse cap.
        // Patience pays — rewards defending primed words across turns.
        public const int   HEAT_FUSE_PER_TURN          = 2;
        public const int   HEAT_FUSE_MAX_BONUS         = 10;

        // Overlap Fuse Extension: existing primed words get +1 fuse when a new prime overlaps them
        public const int   OVERLAP_FUSE_EXTENSION      = 1;
        public const int   MAX_OVERLAP_FUSE_BONUS      = 2;
        private const int  MAX_GLOBAL_FUSE_RESETS      = 2;

        /// <summary>
        /// Triangular chain multiplier: each depth level adds MORE than the last.
        /// Formula: 1 + (d² + d) / 2 where d = min(chainDepth, cap)
        /// d=0: 1.0x, d=1: 2.0x, d=2: 4.0x, d=3: 7.0x, d=4: 11.0x
        /// Much punchier than the old linear 1.0/1.5/2.0/2.5 — chains should feel explosive.
        /// </summary>
        public static float TriangularChainMultiplier(int chainDepth)
        {
            int d = Mathf.Min(Mathf.Max(chainDepth, 0), CHAIN_DEPTH_SCALE_CAP);
            return 1f + (d * d + d) / 2f;
        }

        // TWO directions only — matches how humans read:
        // Horizontal: left to right
        // Vertical: top to bottom (high row to low row)
        // With 51k words, 4 directions creates too many false matches.
        // Players place words in reading order. ORE = O then R then E left-to-right.
        private static readonly int[][] _directions = new int[][]
        {
            new int[] { 1,  0 },   // horizontal: left → right
            new int[] { 0, -1 },   // vertical: top → bottom (row 5 down to row 0)
        };

        // ── Singleton ─────────────────────────────────────────────────────────────

        public static RulesEngine Instance { get; private set; }

        // ── Board state ───────────────────────────────────────────────────────────

        private RulesCellData[,] _board = new RulesCellData[COLS, MAX_ROWS];

        // ── Board Blessing: bonus cells that double word scores ──────────────────

        private bool[,] _bonusCells = new bool[COLS, MAX_ROWS];

        // ── Cyan tiles: edit-refund on detonation (Survival mode) ────────────────

        private bool[,] _cyanCells = new bool[COLS, MAX_ROWS];

        public bool IsCyanCell(int col, int row)
        {
            if (!InBounds(col, row)) return false;
            return _cyanCells[col, row];
        }

        public void SetCyanCell(int col, int row, bool val)
        {
            if (InBounds(col, row)) _cyanCells[col, row] = val;
        }

        public void ClearAllCyanCells()
        {
            System.Array.Clear(_cyanCells, 0, _cyanCells.Length);
        }

        /// <summary>Check if a cell is a bonus cell.</summary>
        public bool IsBonusCell(int col, int row)
        {
            if (!InBounds(col, row)) return false;
            return _bonusCells[col, row];
        }

        /// <summary>Set a cell as a bonus cell. Refuses wild cells — wilds never carry gold.</summary>
        public void SetBonusCell(int col, int row, bool isBonus)
        {
            if (!InBounds(col, row)) return;
            if (isBonus && _board[col, row] != null && _board[col, row].IsWild)
            {
//                 Debug.Log($"[RulesEngine] SetBonusCell refused: ({col},{row}) is a wild tile — no gold on wilds.");
                return;
            }
            _bonusCells[col, row] = isBonus;
        }

        /// <summary>Clear all bonus cells (called on match start).</summary>
        public void ClearAllBonusCells()
        {
            System.Array.Clear(_bonusCells, 0, _bonusCells.Length);
        }

        /// <summary>Shift bonus cells up by one row (called during rising rows).</summary>
        public void ShiftBonusCellsUp()
        {
            for (int col = 0; col < COLS; col++)
            {
                for (int row = ROWS - 1; row > 0; row--)
                    _bonusCells[col, row] = _bonusCells[col, row - 1];
                _bonusCells[col, 0] = false; // bottom row cleared for new bonus placement
            }
        }

        /// <summary>
        /// Place 1-2 random bonus cells scattered across the board at game start.
        /// Enforces minimum 2-cell spacing to prevent clumping.
        /// Returns the positions for visual overlay.
        /// </summary>
        public List<Vector2Int> PlaceInitialBonusCells()
        {
            var positions = new List<Vector2Int>();
            int count = Random.Range(1, 3); // 1 or 2 starting bonus cells (rarer = more exciting)
            var available = new List<Vector2Int>();

            // Scatter across the whole board — any cell can be gold
            // Tiles landing on these positions later will become gold via CreateSingleTile
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    available.Add(new Vector2Int(col, row));

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                int idx = Random.Range(0, available.Count);
                var pos = available[idx];
                available.RemoveAt(idx);
                _bonusCells[pos.x, pos.y] = true;
                positions.Add(pos);

                // Remove nearby cells to prevent clumping (min 2 cells apart)
                available.RemoveAll(p =>
                    Mathf.Abs(p.x - pos.x) <= 2 && Mathf.Abs(p.y - pos.y) <= 2);
            }

//             Debug.Log($"[RulesEngine] Initial Board Blessing: placed {positions.Count} bonus cell(s)");
            return positions;
        }

        /// <summary>
        /// Place 0-1 random bonus cells in the bottom row after a rising row.
        /// 40% chance per rising row — gold should feel like a surprise, not routine.
        /// Returns the columns that got bonus cells for visual overlay.
        /// </summary>
        public List<int> PlaceBonusCellsOnBottomRow()
        {
            var bonusCols = new List<int>();

            // 40% chance to place a gold tile on a rising row (was 100% with 1-2 tiles)
            if (Random.value > 0.40f)
            {
//                 Debug.Log("[RulesEngine] Board Blessing: no bonus cell this rising row (60% skip chance).");
                return bonusCols;
            }

            int count = 1; // at most 1 per rising row
            var available = new List<int>();
            for (int col = 0; col < COLS; col++)
            {
                // Don't place gold adjacent to existing gold in row 1 (the row that just shifted up)
                bool adjacentGold = false;
                if (col > 0 && _bonusCells[col - 1, 1]) adjacentGold = true;
                if (col < COLS - 1 && _bonusCells[col + 1, 1]) adjacentGold = true;
                if (_bonusCells[col, 1]) adjacentGold = true; // directly above
                // Never place gold on stone tiles
                bool isStone = _board[col, 0] != null && _board[col, 0].IsStone;
                if (!adjacentGold && !isStone) available.Add(col);
            }

            for (int i = 0; i < count && available.Count > 0; i++)
            {
                int idx = Random.Range(0, available.Count);
                int col = available[idx];
                available.RemoveAt(idx);
                _bonusCells[col, 0] = true;
                bonusCols.Add(col);
            }

//             Debug.Log($"[RulesEngine] Board Blessing: placed {bonusCols.Count} bonus cell(s) on bottom row at col(s): {string.Join(",", bonusCols)}");
            return bonusCols;
        }

        /// <summary>
        /// Check if any tile in a word match is a gold bonus tile.
        /// Returns the bonus multiplier (1 = no bonus, 2 = has gold tile).
        /// Consumes the gold status on used tiles (one-time use).
        /// </summary>
        /// <summary>Check if any tile in the word is gold. Does NOT consume.</summary>
        public bool HasGoldTile(RulesWordMatch match)
        {
            if (match.Cells == null || GridManager.Instance == null) return false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = GridManager.Instance.GetTile(match.Cells[i].x, match.Cells[i].y);
                if (tile != null && tile.IsGoldBonus) return true;
            }
            return false;
        }

        /// <summary>
        /// Consume gold tiles in a word and return the bonus multiplier.
        /// Call AFTER checking HasGoldTile for priming decisions.
        /// </summary>
        public int ConsumeGoldAndGetMultiplier(RulesWordMatch match)
        {
            if (match.Cells == null || GridManager.Instance == null) return 1;
            bool hitGold = false;
            for (int i = 0; i < match.Cells.Count; i++)
            {
                var cell = GetCell(match.Cells[i].x, match.Cells[i].y);
                if (cell != null && cell.IsWild) continue; // wilds never carry gold
                Tile tile = GridManager.Instance.GetTile(match.Cells[i].x, match.Cells[i].y);
                if (tile != null && tile.IsGoldBonus)
                {
                    hitGold = true;
                    tile.SetGoldBonus(false); // consumed!
                    _bonusCells[match.Cells[i].x, match.Cells[i].y] = false; // clear position too
//                     Debug.Log($"[RulesEngine] Gold tile consumed at ({match.Cells[i].x},{match.Cells[i].y})!");
                }
            }
            return hitGold ? 2 : 1;
        }

        // ── Word Echo: letter streak bonus ───────────────────────────────────────

        // Tracks how many words each player has scored starting with each letter.
        // Each subsequent word starting with the same letter gets +echoCount bonus.
        private Dictionary<char, int>[] _echoCounters = new Dictionary<char, int>[]
        {
            new Dictionary<char, int>(), // player 0 (human)
            new Dictionary<char, int>(), // player 1 (AI)
        };

        /// <summary>Get the echo bonus for a word scored by a player. Increments the counter.</summary>
        public int ConsumeEchoBonus(string word, int playerIndex)
        {
            if (string.IsNullOrEmpty(word) || playerIndex < 0 || playerIndex >= _echoCounters.Length)
                return 0;

            char startLetter = char.ToUpper(word[0]);
            var counters = _echoCounters[playerIndex];

            int echoCount = 0;
            if (counters.TryGetValue(startLetter, out echoCount) && echoCount > 0)
            {
//                 Debug.Log($"[RulesEngine] Word Echo: '{word}' starts with '{startLetter}' — echo #{echoCount} → +{echoCount} bonus");
            }

            // Increment for next time
            counters[startLetter] = echoCount + 1;
            return Mathf.Min(echoCount, 3); // capped at +3 to prevent runaway scoring
        }

        /// <summary>Clear echo counters (called on match start via ClearBoard).</summary>
        private void ClearEchoCounters()
        {
            for (int i = 0; i < _echoCounters.Length; i++)
                _echoCounters[i].Clear();
        }

        // ── Primed word registry ──────────────────────────────────────────────────

        private PrimedWordRegistry _primedRegistry = new PrimedWordRegistry();

        /// <summary>Public access to the primed word registry.</summary>
        public PrimedWordRegistry PrimedRegistry => _primedRegistry;

        // ── Global turn counter (incremented externally by MatchController) ───────

        private int _globalTurn = 0;

        /// <summary>Current global turn number. Increment after each player's full turn.</summary>
        public int GlobalTurn
        {
            get => _globalTurn;
            set => _globalTurn = value;
        }

        // ── Events (C# delegates) ────────────────────────────────────────────────

        public event RulesEventHandler<TileDroppedEvent>       OnTileDropped;
        public event RulesEventHandler<WordScoredEvent>        OnWordScored;
        public event RulesEventHandler<WordPrimedEvent>        OnWordPrimed;
        public event RulesEventHandler<PrimedTriggeredEvent>   OnPrimedTriggered;
        /// <summary>Fired per primed-word detonation AFTER DoExplode computes the
        /// final bonus (includes chain multiplier + cluster boost + gold). Use this
        /// for downstream systems that need the actual scoring impact, not the raw
        /// primed word's original score (ChainMeter fill, FX intensity scaling, etc).
        /// The int payload is the final bonus awarded for this detonation.</summary>
        public event System.Action<int>                        OnDetonationScored;
        public event RulesEventHandler<TilesExplodedEvent>     OnTilesExploded;
        public event RulesEventHandler<GravityCollapseEvent>   OnGravityCollapse;
        public event RulesEventHandler<ChainStepEvent>         OnChainStep;
        public event RulesEventHandler<ResolutionCompleteEvent> OnResolutionComplete;


        // ── Scored word tracking (prevents re-scoring same word at same cells) ────

        private HashSet<string> _scoredWordKeys = new HashSet<string>();

        /// <summary>Check if a word key has already been scored (for external validation).</summary>
        public bool IsScoredKey(string key) => _scoredWordKeys.Contains(key);

        /// <summary>Register a word key as scored (for swap resolution).</summary>
        public void RegisterScoredKey(string key) => _scoredWordKeys.Add(key);
        public void RemoveScoredKey(string key) => _scoredWordKeys.Remove(key);

        /// <summary>Public wrapper for GetFuseLength.</summary>
        public int GetFuseLengthPublic(int wordLength) => GetFuseLength(wordLength);

        /// <summary>Public wrapper for ScanEntireBoard (for legal swap validation).</summary>
        public List<RulesWordMatch> ScanEntireBoardPublic() => ScanEntireBoard();

        // ── Unity lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            ClearBoard();
//             Debug.Log("[RulesEngine] Awake — 7×6 board initialized. " +
                      // "Detecting horizontal + vertical words (3–7 letters). NO diagonals.");
        }

        // ── Board manipulation ────────────────────────────────────────────────────

        public void SetCell(int col, int row, RulesCellData data)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = data;
        }

        public RulesCellData GetCell(int col, int row)
        {
            if (!InBounds(col, row)) return null;
            return _board[col, row];
        }

        public void ClearCell(int col, int row)
        {
            if (!InBounds(col, row)) return;
            _board[col, row] = null;
            _bonusCells[col, row] = false; // gold consumed with the cell
        }

        public void ClearBoard()
        {
            // Clear entire array (MAX_ROWS) regardless of current mode
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < MAX_ROWS; row++)
                    _board[col, row] = null;

            _scoredWordKeys.Clear();
            _primedRegistry.Clear();
            _globalTurn = 0;
            ClearAllBonusCells();
            ClearAllCyanCells();
            ClearEchoCounters();

//             Debug.Log("[RulesEngine] Board cleared.");
        }

        public int GetLowestEmptyRow(int col)
        {
            if (col < 0 || col >= COLS) return -1;
            for (int row = 0; row < ROWS; row++)
                if (_board[col, row] == null) return row;
            return -1;
        }

        public bool IsColumnAvailable(int col)
            => GetLowestEmptyRow(col) >= 0;

        /// <summary>How many tiles are in this column (0 = empty, ROWS = full).</summary>
        public int GetColumnHeight(int col)
        {
            int emptyRow = GetLowestEmptyRow(col);
            return emptyRow < 0 ? ROWS : emptyRow;
        }

        public int CountOccupied()
        {
            int count = 0;
            for (int col = 0; col < COLS; col++)
                for (int row = 0; row < ROWS; row++)
                    if (_board[col, row] != null) count++;
            return count;
        }

        /// <summary>Returns board occupancy as a fraction (0.0 = empty, 1.0 = full).</summary>
        public float GetBoardOccupancy()
        {
            return (float)CountOccupied() / (COLS * ROWS);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RISING ROW SUPPORT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if ALL columns have a tile in the top row (board is full).
        /// Used by RisingRowManager to check for overflow before shifting.
        /// </summary>
        public bool HasTilesInTopRow()
        {
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
                if (_board[col, topRow] == null) return false;
            return true;
        }

        /// <summary>
        /// Returns true if ANY column has a tile in the top row.
        /// Used by Survival mode for top-out detection after auto-drops.
        /// </summary>
        public bool HasAnyTileInTopRow()
        {
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
                if (_board[col, topRow] != null) return true;
            return false;
        }

        /// <summary>
        /// Shifts all board data up one row. Row 0 becomes empty.
        /// Returns a move dictionary mapping old positions to new positions.
        /// IMPORTANT: Does NOT check for overflow — caller must check HasTilesInTopRow() first.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> ShiftBoardUp()
        {
            return ShiftBoardUp(out _);
        }

        /// <summary>
        /// Shifts all board data up one row. Row 0 becomes empty.
        /// Returns a move dictionary mapping old positions to new positions.
        /// Also outputs a list of top-row cells that were pushed off the board.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> ShiftBoardUp(out List<Vector2Int> crushedCells)
        {
            var moves = new Dictionary<Vector2Int, Vector2Int>();
            crushedCells = new List<Vector2Int>();

            // Collect tiles in top row that will be pushed off
            int topRow = ROWS - 1;
            for (int col = 0; col < COLS; col++)
            {
                if (_board[col, topRow] != null)
                {
                    crushedCells.Add(new Vector2Int(col, topRow));
                    _board[col, topRow] = null;
                }
            }

            // Shift from top to bottom to avoid overwriting
            for (int col = 0; col < COLS; col++)
            {
                for (int row = ROWS - 1; row >= 1; row--)
                {
                    _board[col, row] = _board[col, row - 1];
                    if (_board[col, row] != null)
                    {
                        var oldPos = new Vector2Int(col, row - 1);
                        var newPos = new Vector2Int(col, row);
                        moves[oldPos] = newPos;
                        _board[col, row].Row = row;
                    }
                }
                // Clear bottom row
                _board[col, 0] = null;
            }

//             Debug.Log($"[RulesEngine] ShiftBoardUp — moved {moves.Count} tiles up by 1 row.");
            return moves;
        }

        /// <summary>
        /// Fills the bottom row (row 0) with random neutral letters from the bag.
        /// Returns the array of letters placed (length = COLS).
        /// Neutral tiles have PlayerIndex = -1.
        /// </summary>
        private static readonly char[] FERTILE_VOWELS     = { 'A', 'E', 'I', 'O', 'U' };
        private static readonly char[] FERTILE_CONNECTORS = { 'R', 'S', 'T', 'N', 'L', 'D', 'H', 'C', 'M', 'P' };

        // Old FindColumnHelper removed — replaced by FindColumnHelperImproved + candidate scoring

        // ── Candidate row generation for fertile rising rows ─────────────────

        /// <summary>
        /// Generates a candidate bottom row with variety and basic balance.
        /// Not yet scored — that happens in ScoreRowPlayability.
        /// </summary>
        private char[] GenerateCandidateRow(TileBag bag, HashSet<int> stoneColumns)
        {
            char[] row = new char[COLS];
            var used = new HashSet<char>();
            int vowelCount = 0;

            // Place stones first
            foreach (int sc in stoneColumns)
                row[sc] = '#';

            // Tall-column relief: if an edge column is significantly taller than
            // average, occasionally skip it (leave a gap) so it doesn't grow further.
            var skipCols = new HashSet<int>();
            if (SurvivalManager.IsSurvivalMode)
            {
                int avgHeight = 0;
                for (int c = 0; c < COLS; c++) avgHeight += GetColumnHeight(c);
                avgHeight /= COLS;

                // Check edge columns (0 and COLS-1) and near-edge (1 and COLS-2)
                int[] edgeCols = { 0, COLS - 1, 1, COLS - 2 };
                for (int e = 0; e < edgeCols.Length; e++)
                {
                    int c = edgeCols[e];
                    if (stoneColumns.Contains(c)) continue;
                    int h = GetColumnHeight(c);
                    // If this column is 3+ rows taller than average, 50% chance to skip
                    if (h >= avgHeight + 3 && Random.value < 0.50f)
                    {
                        skipCols.Add(c);
                    }
                }
            }

            // Fill non-stone, non-skip columns
            int nonStone = COLS - stoneColumns.Count;
            int targetVowels = 2;

            var colOrder = new List<int>();
            for (int c = 0; c < COLS; c++)
                if (!stoneColumns.Contains(c) && !skipCols.Contains(c)) colOrder.Add(c);
            for (int i = colOrder.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int tmp = colOrder[i]; colOrder[i] = colOrder[j]; colOrder[j] = tmp;
            }

            // First pass: try to place board-aware helpers via column simulation
            int helpersPlaced = 0;
            foreach (int col in colOrder)
            {
                if (helpersPlaced >= 4) break;
                if (Random.value > 0.38f) continue; // EXPERIMENT: was 0.60 — revert if rows feel dead

                char helper = FindColumnHelperImproved(col);
                if (helper != '\0' && !used.Contains(helper))
                {
                    row[col] = helper;
                    used.Add(helper);
                    if (IsVowelChar(helper)) vowelCount++;
                    helpersPlaced++;
                }
            }

            // Second pass: fill remaining with balanced letters
            foreach (int col in colOrder)
            {
                if (row[col] != '\0') continue;

                char picked;
                if (vowelCount < targetVowels)
                {
                    picked = PickUnused(FERTILE_VOWELS, used);
                    vowelCount++;
                }
                else if (Random.value < 0.25f && vowelCount < 3)
                {
                    picked = PickUnused(FERTILE_VOWELS, used);
                    vowelCount++;
                }
                else
                {
                    picked = PickUnused(FERTILE_CONNECTORS, used);
                }

                row[col] = picked;
                used.Add(picked);
            }

            return row;
        }

        /// <summary>
        /// Scores a candidate row by simulating placement at row 0 (which becomes
        /// row 1 after the rise) and checking for ACTUAL word opportunities.
        ///
        /// Scoring:
        ///   +5 per immediate word formed with existing board tiles
        ///   +3 per one-letter-away near-word (drop one more letter to complete)
        ///   +2 per horizontal word opportunity within the new row itself
        ///   +1 per useful adjacency (new letter next to existing tile)
        ///   -3 per dead column (new letter has no tiles nearby)
        ///   -2 if row has 0 or 1 vowels
        ///   -1 per duplicate letter
        /// </summary>
        private int ScoreRowPlayability(char[] row, HashSet<int> stoneColumns)
        {
            int score = 0;

            // Simulate: after rise, current board shifts up by 1. New row sits at row 1.
            // But we're scoring BEFORE the shift, so the new row will be at row 0
            // and existing tiles are still at their current positions (they'll shift to +1).
            // For scoring purposes: new row at 0, existing board at rows 1+.

            // Count immediate word opportunities
            // Check horizontal words WITHIN the new row
            for (int startCol = 0; startCol <= COLS - 3; startCol++)
            {
                for (int len = 3; len <= Mathf.Min(7, COLS - startCol); len++)
                {
                    bool valid = true;
                    var sb = new System.Text.StringBuilder(len);
                    for (int c = startCol; c < startCol + len; c++)
                    {
                        if (row[c] == '\0' || row[c] == '#') { valid = false; break; }
                        sb.Append(row[c]);
                    }
                    if (valid && WordDictionary.IsValidWord(sb.ToString()))
                    {
                        // Words within the new row are mildly good (they'll get primed)
                        // But too many means the row is "too easy" — cap at +4
                        score += 2;
                    }
                }
            }

            // Check vertical opportunities: new row letter + existing tiles above
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;

                // After rise, new letter at (col, 0), existing tile at (col, 0) moves to (col, 1)
                // So new letter is adjacent to what WAS at (col, 0)
                var above = _board[col, 0]; // will become (col, 1) after rise
                if (above == null || above.IsStone) continue;

                // Two adjacent letters — check if any A-Z above or below completes a word
                char newLetter = row[col];
                char aboveLetter = char.ToUpper(above.Letter);

                // Check 3-letter vertical: [above's above] + above + new
                var above2 = _board[col, 1]; // will become (col, 2)
                if (above2 != null && !above2.IsStone)
                {
                    string vert3 = "" + char.ToUpper(above2.Letter) + aboveLetter + newLetter;
                    if (WordDictionary.IsValidWord(vert3))
                        score += 5; // immediate word!
                    // Also check reverse
                    string vert3r = "" + newLetter + aboveLetter + char.ToUpper(above2.Letter);
                    if (WordDictionary.IsValidWord(vert3r))
                        score += 5;
                }

                // Near-word: new + above, needs one more letter dropped on top
                // Check if any letter X makes (X + above + new) or (above + new + X) a word
                bool hasNearWord = false;
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    string try1 = "" + ch + aboveLetter + newLetter;
                    string try2 = "" + aboveLetter + newLetter + ch;
                    // Reverse direction
                    string try3 = "" + ch + newLetter + aboveLetter;
                    string try4 = "" + newLetter + aboveLetter + ch;
                    if (WordDictionary.IsValidWord(try1) || WordDictionary.IsValidWord(try2) ||
                        WordDictionary.IsValidWord(try3) || WordDictionary.IsValidWord(try4))
                    {
                        hasNearWord = true;
                        break;
                    }
                }
                if (hasNearWord)
                    score += 3; // good adjacency — one drop away from a word

                // Basic adjacency bonus
                score += 1;
            }

            // Check horizontal near-words: pairs in the new row with a gap
            for (int col = 0; col < COLS - 2; col++)
            {
                char left = row[col];
                char right = row[col + 2];
                if (left == '\0' || left == '#' || right == '\0' || right == '#') continue;
                if (row[col + 1] != '\0' && row[col + 1] != '#') continue; // no gap

                // Gap pattern: left _ right — check if filling creates a word
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    string fill = "" + left + ch + right;
                    if (WordDictionary.IsValidWord(fill))
                    {
                        score += 2;
                        break; // count once per gap
                    }
                }
            }

            // Penalties
            int vowels = 0;
            var letterCounts = new Dictionary<char, int>();
            for (int col = 0; col < COLS; col++)
            {
                char c = row[col];
                if (c == '\0' || c == '#') continue;
                if (IsVowelChar(c)) vowels++;
                if (!letterCounts.ContainsKey(c)) letterCounts[c] = 0;
                letterCounts[c]++;
            }

            if (vowels < 2) score -= 2;
            foreach (var kv in letterCounts)
                if (kv.Value > 1) score -= (kv.Value - 1);

            // Dead columns: new letter with no existing tiles nearby
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;
                bool hasNeighbor = false;
                for (int dc = -1; dc <= 1; dc++)
                {
                    int nc = col + dc;
                    if (nc < 0 || nc >= COLS) continue;
                    for (int r = 0; r < Mathf.Min(3, ROWS); r++)
                    {
                        if (_board[nc, r] != null && !_board[nc, r].IsStone)
                        { hasNeighbor = true; break; }
                    }
                    if (hasNeighbor) break;
                }
                if (!hasNeighbor) score -= 1;
            }

            // Tall column relief: reward rows that create word opportunities
            // in the tallest columns (edge junk tower prevention).
            // Bonus if the letter in a tall column forms a near-word with its neighbors.
            for (int col = 0; col < COLS; col++)
            {
                if (row[col] == '\0' || row[col] == '#') continue;
                int height = GetColumnHeight(col);
                if (height < 4) continue; // only care about tall columns

                // Bonus for creating word opportunities in tall columns
                char aboveChar = (_board[col, 0] != null && !_board[col, 0].IsStone)
                    ? char.ToUpper(_board[col, 0].Letter) : '\0';
                if (aboveChar != '\0')
                {
                    // Check if new letter + above letter could be part of a word
                    for (char ch = 'A'; ch <= 'Z'; ch++)
                    {
                        string t1 = "" + ch + aboveChar + row[col];
                        string t2 = "" + row[col] + aboveChar + ch;
                        if (WordDictionary.IsValidWord(t1) || WordDictionary.IsValidWord(t2))
                        {
                            score += 3; // tall column gets a word opportunity — great
                            break;
                        }
                    }
                }

                // Penalty if a tall edge column gets a junk letter with no opportunity
                bool isEdge = (col == 0 || col == COLS - 1);
                if (isEdge && aboveChar == '\0')
                    score -= 2; // adding to a tall edge with nothing above = more junk
            }

            return score;
        }

        /// <summary>
        /// Improved column helper: checks what letter at (col, 0) would create
        /// a word with existing tiles at rows 0-2 (which shift to 1-3 after rise).
        /// Also checks horizontal neighbors in the new row.
        /// </summary>
        private char FindColumnHelperImproved(int col)
        {
            var candidates = new List<char>(8);

            // What's directly above? (will be at row 1 after rise)
            var above = _board[col, 0];
            char aboveLetter = (above != null && !above.IsStone) ? char.ToUpper(above.Letter) : '\0';

            // What's two above? (will be at row 2 after rise)
            var above2 = _board[col, 1];
            char above2Letter = (above2 != null && !above2.IsStone) ? char.ToUpper(above2.Letter) : '\0';

            // Horizontal neighbors at row 0 (other new row letters aren't placed yet,
            // but existing tiles at row 0 will shift to row 1)
            // Check what letters in adjacent columns at rows 0-1 could form horizontal words

            for (char ch = 'A'; ch <= 'Z'; ch++)
            {
                if (candidates.Count >= 8) break;

                // Vertical check: ch + above + above2 (3-letter vertical word)
                if (aboveLetter != '\0' && above2Letter != '\0')
                {
                    string v1 = "" + above2Letter + aboveLetter + ch;
                    string v2 = "" + ch + aboveLetter + above2Letter;
                    if (WordDictionary.IsValidWord(v1) || WordDictionary.IsValidWord(v2))
                    { candidates.Add(ch); continue; }
                }

                // Near-word check: ch + above, any X makes a word
                if (aboveLetter != '\0')
                {
                    for (char x = 'A'; x <= 'Z'; x++)
                    {
                        string t1 = "" + x + aboveLetter + ch;
                        string t2 = "" + aboveLetter + ch + x;
                        string t3 = "" + x + ch + aboveLetter;
                        string t4 = "" + ch + aboveLetter + x;
                        if (WordDictionary.IsValidWord(t1) || WordDictionary.IsValidWord(t2) ||
                            WordDictionary.IsValidWord(t3) || WordDictionary.IsValidWord(t4))
                        { candidates.Add(ch); goto nextChar; }
                    }
                }

                // Horizontal check: what letters in adjacent columns make words?
                for (int dc = -2; dc <= 0; dc++)
                {
                    // Check if col+dc to col+dc+2 has tiles that form a word with ch
                    int startC = col + dc;
                    if (startC < 0 || startC + 2 >= COLS) continue;

                    // Read adjacent letters from the CURRENT board at row 0
                    // (these will shift up, but we're checking if letters nearby
                    //  would form words in the new row context)
                    char[] buf = new char[3];
                    bool allValid = true;
                    for (int i = 0; i < 3; i++)
                    {
                        int c = startC + i;
                        if (c == col)
                            buf[i] = ch;
                        else
                        {
                            var cell = _board[c, 0];
                            if (cell == null || cell.IsStone) { allValid = false; break; }
                            buf[i] = char.ToUpper(cell.Letter);
                        }
                    }
                    if (allValid && WordDictionary.IsValidWord(new string(buf)))
                    { candidates.Add(ch); goto nextChar; }
                }

                nextChar:;
            }

            if (candidates.Count == 0) return '\0';
            return candidates[Random.Range(0, candidates.Count)];
        }

        private static bool IsVowelChar(char c)
        {
            c = char.ToUpper(c);
            return c == 'A' || c == 'E' || c == 'I' || c == 'O' || c == 'U';
        }

        // ── Post-gravity fertility helpers ──────────────────────────────────────

        /// <summary>
        /// Counts how many "near-words" exist on the board — positions where
        /// dropping one letter would complete a 3+ letter word. This measures
        /// how alive the board feels.
        /// </summary>
        private int CountNearWords()
        {
            int count = 0;

            for (int col = 0; col < COLS; col++)
            {
                // Find the lowest empty row in this column
                int emptyRow = -1;
                for (int row = 0; row < ROWS; row++)
                {
                    if (_board[col, row] == null) { emptyRow = row; break; }
                }
                if (emptyRow < 0) continue; // column full

                // Try each letter A-Z as a hypothetical drop
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    if (CheckWordAt(col, emptyRow, ch))
                    {
                        count++;
                        break; // one per column
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Checks if placing 'letter' at (col, row) would create a valid word
        /// horizontally or vertically.
        /// </summary>
        private bool CheckWordAt(int col, int row, char letter)
        {
            // Horizontal check
            int left = col, right = col;
            while (left > 0 && _board[left - 1, row] != null && !_board[left - 1, row].IsStone) left--;
            while (right < COLS - 1 && _board[right + 1, row] != null && !_board[right + 1, row].IsStone) right++;

            if (right - left + 1 >= 3)
            {
                var sb = new System.Text.StringBuilder(right - left + 1);
                for (int c = left; c <= right; c++)
                    sb.Append(c == col ? letter : char.ToUpper(_board[c, row].Letter));

                string full = sb.ToString();
                int idx = col - left;
                for (int start = 0; start < full.Length; start++)
                    for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                        if (idx >= start && idx < start + len)
                            if (WordDictionary.IsValidWord(full.Substring(start, len)))
                                return true;
            }

            // Vertical check
            int bottom = row, top = row;
            while (bottom > 0 && _board[col, bottom - 1] != null && !_board[col, bottom - 1].IsStone) bottom--;
            while (top < ROWS - 1 && _board[col, top + 1] != null && !_board[col, top + 1].IsStone) top++;

            if (top - bottom + 1 >= 3)
            {
                var sb = new System.Text.StringBuilder(top - bottom + 1);
                for (int r = top; r >= bottom; r--)
                    sb.Append(r == row ? letter : char.ToUpper(_board[col, r].Letter));

                string full = sb.ToString();
                int idx = top - row;
                for (int start = 0; start < full.Length; start++)
                    for (int len = 3; len <= Mathf.Min(7, full.Length - start); len++)
                        if (idx >= start && idx < start + len)
                            if (WordDictionary.IsValidWord(full.Substring(start, len)))
                                return true;
            }

            return false;
        }

        /// <summary>
        /// Silently nudges up to 'maxNudge' tiles to create word opportunities.
        /// Picks isolated junk consonants and replaces them with letters that
        /// would create near-words with their neighbors.
        /// Returns the number of tiles nudged.
        /// </summary>
        private int NudgeTilesForFertility(int maxNudge)
        {
            int nudged = 0;

            // Collect nudge candidates: non-primed, non-special, non-gold tiles
            // Prefer tiles in the bottom half and on the edges (junk tower zones)
            var candidates = new List<(int col, int row, int priority)>();

            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var cell = _board[col, row];
                    if (cell == null || cell.IsStone) continue;
                    if (cell.IsSwapRefill || cell.IsEditRefill || cell.IsWildRefill) continue;
                    if (cell.IsWild) continue; // wilds are never nudged
                    if (_bonusCells[col, row]) continue;

                    // Check if part of a primed word — skip
                    var primed = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
                    if (primed != null && primed.Count > 0) continue;

                    // Priority: edge columns + lower rows = more likely junk
                    int edgeDist = Mathf.Min(col, COLS - 1 - col);
                    int priority = (3 - edgeDist) + (ROWS - row); // higher = more likely to nudge
                    candidates.Add((col, row, priority));
                }
            }

            // Sort by priority (highest first = edge/bottom tiles)
            candidates.Sort((a, b) => b.priority.CompareTo(a.priority));

            for (int i = 0; i < candidates.Count && nudged < maxNudge; i++)
            {
                int col = candidates[i].col;
                int row = candidates[i].row;

                // Try each letter A-Z: would replacing this tile create a near-word?
                // A "near-word" means: if someone dropped one more letter adjacent,
                // it would form a word. We approximate: does the NEW letter participate
                // in a 2-letter run that can be extended to 3?
                char bestLetter = '\0';
                for (char ch = 'A'; ch <= 'Z'; ch++)
                {
                    // Check if this letter creates a horizontal or vertical near-word
                    if (CheckWordAt(col, row, ch))
                    {
                        // Don't pick the same letter that's already there
                        if (char.ToUpper(_board[col, row].Letter) == ch) continue;
                        bestLetter = ch;
                        break;
                    }
                }

                if (bestLetter != '\0')
                {
                    char oldLetter = _board[col, row].Letter;
                    _board[col, row].Letter = bestLetter;

                    // Purge scored keys for this cell so new words can be detected
                    PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

                    nudged++;
//                     Debug.Log($"[PostGravityFertility] Nudged ({col},{row}): '{oldLetter}' → '{bestLetter}'");
                }
            }

            return nudged;
        }

        /// <summary>Pick a letter from the pool that hasn't been used yet (for variety).</summary>
        private static char PickUnused(char[] pool, HashSet<char> used)
        {
            // Try to find an unused letter
            for (int attempt = 0; attempt < 10; attempt++)
            {
                char c = pool[Random.Range(0, pool.Length)];
                if (!used.Contains(c)) return c;
            }
            // Fallback: just pick any
            return pool[Random.Range(0, pool.Length)];
        }

        public char[] FillBottomRow(TileBag bag)
        {
            char[] letters = new char[COLS];
            // Fertile row (8-16 candidates → pick best by ScoreRowPlayability)
            // is an assist: it serves word-friendly rows instead of raw random
            // draws. Gate behind NoAssistMode so hardest-difficulty runs really
            // get raw bag draws. Without this, "no assist" quietly still biased
            // rows toward playability.
            bool fertility = SurvivalManager.IsSurvivalMode && !SurvivalManager.NoAssistMode;

            // Stone tiles
            float stoneChance = SurvivalManager.IsSurvivalMode && SurvivalManager.Instance != null
                ? SurvivalManager.Instance.GetStoneChance() : 0f;
            var stoneColumns = new HashSet<int>();
            for (int col = 0; col < COLS; col++)
                if (stoneColumns.Count < 2 && Random.value < stoneChance)
                    stoneColumns.Add(col);

            if (fertility)
            {
                // CANDIDATE-GENERATION FERTILE ROW
                // Generate multiple candidate rows, simulate placement against the
                // current board, score by ACTUAL playability, pick the best one.
                // Goal: "dangerous but solvable — this row gave me real options"

                // Post-clear boost: generate more candidates for a better row
                bool boosted = SurvivalManager.Instance != null && SurvivalManager.Instance.IsPostClearBoosted;
                int numCandidates = boosted ? 16 : 8;

                char[] bestRow = null;
                int bestScore = -999;

                for (int attempt = 0; attempt < numCandidates; attempt++)
                {
                    char[] candidate = GenerateCandidateRow(bag, stoneColumns);
                    int score = ScoreRowPlayability(candidate, stoneColumns);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestRow = candidate;
                    }
                }

                if (bestRow != null)
                    System.Array.Copy(bestRow, letters, COLS);
                else
                {
                    // Fallback: random row with basic vowel balance
                    for (int col = 0; col < COLS; col++)
                    {
                        if (stoneColumns.Contains(col)) { letters[col] = '#'; continue; }
                        letters[col] = bag.DrawLetter();
                    }
                }

//                 Debug.Log($"[RulesEngine] FillBottomRow FERTILE — score={bestScore} row={new string(letters)}");
            }
            else
            {
                // Classic mode: plain random draw
                for (int col = 0; col < COLS; col++)
                    letters[col] = bag.DrawLetter();

//                 Debug.Log($"[RulesEngine] FillBottomRow — {new string(letters)}");
            }

            // Commit to board — skip empty columns (gaps from tall-column relief)
            for (int col = 0; col < COLS; col++)
            {
                if (letters[col] == '\0') continue; // intentional gap — no tile here

                bool isStone = letters[col] == '#';
                _board[col, 0] = new RulesCellData
                {
                    Letter = letters[col],
                    Col = col,
                    Row = 0,
                    PlayerIndex = -1,
                    IsStone = isStone,
                };
            }

            return letters;
        }

        /// <summary>
        /// <summary>
        /// After a rising row, detect any NEW words that weren't on the board before.
        /// These words are primed as neutral (playerIndex = -1) — free bonus for both players!
        /// Returns the list of newly primed words.
        /// </summary>
        /// <summary>
        /// Scans the board for words after a rising row and registers them as scored
        /// so they don't get double-counted, but does NOT prime them.
        /// The player must create words themselves — rising rows just provide material.
        /// </summary>
        // RegisterRiseRowWords REMOVED — was permanently blocking board words from
        // ever being scored/primed. Replaced by RegisterWordsAtRow which only
        // registers words touching the specified row (targeted, not whole-board).

        /// <summary>
        /// Registers all valid words touching a specific row as scored, so they
        /// don't auto-prime. Used after rising rows to suppress the new bottom
        /// row from immediately priming. Words become primeable when a player
        /// action purges their scored keys (drop/gravity at those cells).
        /// </summary>
        public void RegisterWordsAtRow(int row)
        {
            int registered = 0;
            for (int col = 0; col < COLS; col++)
            {
                if (_board[col, row] == null) continue;
                var words = FindNewWords(col, row);
                for (int i = 0; i < words.Count; i++)
                {
                    string key = words[i].Word + "|" + words[i].CellKey;
                    if (_scoredWordKeys.Add(key))
                        registered++;
                }
            }
            // if (registered > 0)
//                 Debug.Log($"[RulesEngine] RegisterWordsAtRow({row}): registered {registered} word(s)");
        }

        /// <summary>
        /// Detects and primes words passing through a specific cell (used by auto-drops).
        /// Only words physically containing (col, row) are primed.
        /// </summary>
        public List<RulesWordMatch> DetectAndPrimeAtCell(int col, int row)
        {
            var newWords = new List<RulesWordMatch>();
            var justPrimedIds = new HashSet<int>();
            var seedCells = new List<Vector2Int> { new Vector2Int(col, row) };
            var allWords = ScanSeedCellsOnly(seedCells);

            for (int i = 0; i < allWords.Count; i++)
            {
                string key = allWords[i].Word + "|" + allWords[i].CellKey;
                if (_scoredWordKeys.Contains(key)) continue;

                var match = allWords[i];
                int score = CalculateWordScore(match.Word, match.WildLetterIndices);
                match.Score = score;

                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId = _primedRegistry.AddPrimedWord(
                    match.Word, match.Cells, -1, _globalTurn, expiresOn, score);
                justPrimedIds.Add(primedId);

                _scoredWordKeys.Add(key);

                newWords.Add(match);
//                 Debug.Log($"[RulesEngine] Auto-drop created word '{match.Word}' ({score} pts) at ({col},{row}) — PRIMED!");
            }

            // if (newWords.Count > 0)
//                 Debug.Log($"[RulesEngine] Auto-drop primed {newWords.Count} new word(s)!");
            if (justPrimedIds.Count > 0)
                ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);

            return newWords;
        }

        /// <summary>
        /// After a rising row shift, remap _scoredWordKeys to new cell positions.
        /// Only words that were ALREADY scored keep their "scored" status at the
        /// new coordinates.  Words that coincidentally exist on the board but were
        /// never scored by a player are NOT registered — this prevents the
        /// ghost-registration bug where unscored words became un-detectable.
        /// </summary>
        public void RebuildScoredKeysAfterRise()
        {
            // Rising rows shift every tile up by 1 row.
            // Remap each existing scored key: y → y+1.
            // Drop any key whose cell would be at or above ROWS (tile was crushed).
            var oldKeys = new List<string>(_scoredWordKeys);
            _scoredWordKeys.Clear();

            int remapped = 0;
            int dropped  = 0;

            for (int k = 0; k < oldKeys.Count; k++)
            {
                string key = oldKeys[k];
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;

                string wordPart = key.Substring(0, pipeIdx);
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');

                bool valid = true;
                var newCoords = new System.Text.StringBuilder();

                for (int i = 0; i < coords.Length; i++)
                {
                    string[] xy = coords[i].Split(',');
                    if (xy.Length != 2) { valid = false; break; }

                    int x, y;
                    if (!int.TryParse(xy[0], out x) || !int.TryParse(xy[1], out y))
                    { valid = false; break; }

                    int newY = y + 1;
                    if (newY >= ROWS) { valid = false; break; } // crushed off top

                    if (i > 0) newCoords.Append(';');
                    newCoords.Append(x).Append(',').Append(newY);
                }

                if (valid)
                {
                    _scoredWordKeys.Add(wordPart + "|" + newCoords.ToString());
                    remapped++;
                }
                else
                {
                    dropped++;
                }
            }

//             Debug.Log($"[RulesEngine] RebuildScoredKeysAfterRise — remapped {remapped} keys, dropped {dropped} (crushed/invalid).");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORING (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Calculates the score for a word.
        /// Formula: sum of letter point values × length multiplier.
        ///   3 letters → ×1
        ///   4 letters → ×1.5 (rounded to int)
        ///   5+ letters → ×2
        /// </summary>
        public static int CalculateWordScore(string word)
        {
            return CalculateWordScore(word, null);
        }

        /// <summary>
        /// Wild-aware scoring. Positions listed in wildIndices contribute 0 letter
        /// points but still count toward the length multiplier — the wild "earned"
        /// the length, just not the tile value.
        /// </summary>
        public static int CalculateWordScore(string word, List<int> wildIndices)
        {
            if (string.IsNullOrEmpty(word)) return 0;

            int raw = 0;
            for (int i = 0; i < word.Length; i++)
            {
                if (wildIndices != null && wildIndices.Contains(i)) continue;
                raw += LetterData.GetPoints(word[i]);
            }

            // Triangular length multiplier — rewards longer words exponentially.
            // Survival uses a steeper 7-letter tier (Phase 11+ triangularity)
            // so SPARKLE-tier words feel like a marquee moment instead of just
            // "slightly more than a 6-letter." Other modes keep the legacy
            // curve (Level/Daily/Classic don't have the same time pressure so
            // the bump is Survival-specific).
            float multiplier;
            bool isSurvival = SurvivalManager.IsSurvivalMode;
            switch (word.Length)
            {
                case 3:  multiplier = 1.0f; break;
                case 4:  multiplier = 1.5f; break;
                case 5:  multiplier = 2.5f; break;
                case 6:  multiplier = 4.0f; break;
                default: multiplier = word.Length >= 7
                    ? (isSurvival ? 7.0f : 6.0f)
                    : 1.0f; break;
            }

            // Rare-letter premium (Survival-only, Phase 11+): words that
            // include at least one of Q/Z/X/J get a flat ×2 on top of the
            // length mult. Flat multiplier per word — QUIZ doesn't get
            // stacked bonuses for having Q and Z both. Deliberate play
            // pattern: "can I make a Q word?"
            if (isSurvival)
            {
                string upper = word.ToUpperInvariant();
                if (upper.IndexOfAny(RARE_LETTERS) >= 0)
                    multiplier *= 2.0f;
            }

            int score = Mathf.RoundToInt(raw * multiplier);

            if (wildIndices != null && wildIndices.Count > 0)
            {
//                 Debug.Log($"[RulesEngine] CalculateWordScore('{word}'): " +
                          // $"raw={raw} (wilds={wildIndices.Count} zeroed) × {multiplier:F1} = {score}");
            }
            else
            {
//                 Debug.Log($"[RulesEngine] CalculateWordScore('{word}'): " +
                          // $"raw={raw} × {multiplier:F1} = {score}");
            }

            return score;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // PROCESS DROP — Full resolution logic (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Drops a tile into the logical board, finds words, scores them, primes them,
        /// checks for primed word triggers, explodes triggered words, applies gravity,
        /// and chains until stable.
        ///
        /// Words primed in the CURRENT resolution step cannot be triggered in the
        /// same resolution — tracked via _justPrimedThisResolution.
        ///
        /// Returns a ResolutionResult summarizing everything that happened.
        /// </summary>
        public ResolutionResult ProcessDrop(int col, char letter, int playerIndex)
        {
            var result = new ResolutionResult
            {
                Col         = col,
                Letter      = letter,
                PlayerIndex = playerIndex,
            };

            // 1. Find target row
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0)
            {
//                 Debug.Log($"[RulesEngine] ProcessDrop: col {col} is full — cannot place.");
                return result;
            }

            result.Row = targetRow;

            // 2. Place tile in board data
            var cellData = new RulesCellData
            {
                Letter      = char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
            };
            _board[col, targetRow] = cellData;

//             Debug.Log($"[RulesEngine] ProcessDrop: placed '{letter}' at ({col},{targetRow}) " +
                      // $"player={playerIndex}");

            // 3. Emit TileDropped event
            OnTileDropped?.Invoke(new TileDroppedEvent
            {
                Col         = col,
                Row         = targetRow,
                Letter      = char.ToUpper(letter),
                PlayerIndex = playerIndex,
            });

            // 4. Expiry moved to AFTER resolution (see step 8 below).
            // This gives the player one last chance to detonate a primed word
            // on the turn it would expire — feels much fairer.

            // 5. Resolution chain loop
            // Track all primed word IDs created during THIS entire resolution
            // They cannot be triggered within the same resolution.
            HashSet<int> justPrimedThisResolution = new HashSet<int>();

            // Reset big-moment splash gate for this legacy resolution path too.
            _splashFiredThisResolution = false;

            int chainStep  = 0;
            int totalScore = 0;
            int baseScoreAccum = 0;
            int chainBonusAccum = 0;
            int detonationBonusAccum = 0;
            bool keepChaining = true;
            var localSeedCells = new List<Vector2Int> { new Vector2Int(col, targetRow) };

            while (keepChaining && chainStep < MAX_CHAIN_DEPTH)
            {
                keepChaining = false;

                // 5a. Seed-cell scan — only words containing the drop/gravity position
                List<RulesWordMatch> allWords = ScanSeedCellsOnly(localSeedCells);

                // 5b. Remove substring words — only keep longest per line.
                // If TAMER exists, remove TAM and TAME from same cells.
                allWords = FilterSubstringWords(allWords);

                // 5c. Filter to only NEW (unscored) words
                List<RulesWordMatch> newWords = new List<RulesWordMatch>();
                for (int i = 0; i < allWords.Count; i++)
                {
                    string key = allWords[i].Word + "|" + allWords[i].CellKey;
                    if (!_scoredWordKeys.Contains(key))
                        newWords.Add(allWords[i]);
                }

                if (newWords.Count == 0)
                {
                    break;
                }

                // Emit ChainStep event
                OnChainStep?.Invoke(new ChainStepEvent
                {
                    StepIndex     = chainStep,
                    NewWordsFound = newWords.Count,
                });

                // if (chainStep > 0)
//                     Debug.Log($"[RulesEngine] CHAIN step {chainStep}: {newWords.Count} new word(s)!");

                // 5c. Score, prime, and check triggers for each new word
                // Collect all cells that need to be exploded this step
                HashSet<int> primedIdsToExplode = new HashSet<int>();
                List<PrimedTriggeredEvent> triggeredEvents = new List<PrimedTriggeredEvent>();

                // Cluster bonus: when multiple primed words trigger together in one
                // step (connected-cluster detonation), boost each word's effective
                // chain depth by (count - 1). Fixes the signature-spectacle gap where
                // 4 primed words firing simultaneously would otherwise score at
                // chainStep=0 with no multiplier, making huge visual moments feel flat.
                int effectiveChainStep = chainStep + Mathf.Max(0, newWords.Count - 1);

                for (int w = 0; w < newWords.Count; w++)
                {
                    RulesWordMatch match = newWords[w];
                    string key = match.Word + "|" + match.CellKey;
                    _scoredWordKeys.Add(key);

                    // Calculate score with chain bonus + gold bonus + echo
                    //
                    // Chain bonus is MULTIPLICATIVE on the word's base score — a long
                    // word formed mid-chain should pay substantially more than the
                    // same word formed safely. Caps at depth 3 (2.5x) so absurd
                    // chains still feel bounded. Fixes the Schell-triangularity gap
                    // where risky chain plays barely beat safe long-word drops.
                    int baseScore  = CalculateWordScore(match.Word, match.WildLetterIndices);
                    float chainMult = (effectiveChainStep > 0)
                        ? 1f + Mathf.Min(effectiveChainStep, CHAIN_DEPTH_SCALE_CAP) * 0.5f
                        : 1f;
                    int chainBoosted = Mathf.RoundToInt(baseScore * chainMult);
                    int echoBonus  = ConsumeEchoBonus(match.Word, playerIndex);
                    bool isGoldWord = HasGoldTile(match); // check BEFORE consuming
                    int bonusMult  = ConsumeGoldAndGetMultiplier(match);
                    int finalScore = (chainBoosted + echoBonus) * bonusMult;
                    // Reporting fields expect a separate chainBonus number.
                    int chainBonus = chainBoosted - baseScore;
                    match.Score    = finalScore;
                    totalScore    += finalScore;
                    baseScoreAccum += baseScore;
                    chainBonusAccum += chainBonus;

                    Debug.Log($"[RulesEngine] Scored '{match.Word}': base={baseScore}" +
                              (chainBonus > 0 ? $" +chain({chainBonus}, effStep={effectiveChainStep})" : "") +
                              (echoBonus > 0 ? $" +echo({echoBonus})" : "") +
                              $" = {finalScore} pts  [rawStep={chainStep}, clusterSize={newWords.Count}]");

                    // Emit WordScored
                    var scoredEvt = new WordScoredEvent
                    {
                        Word        = match.Word,
                        Cells       = new List<Vector2Int>(match.Cells),
                        BaseScore   = baseScore,
                        FinalScore  = finalScore,
                        PlayerIndex = playerIndex,
                        ChainStep   = chainStep,
                    };
                    result.ScoredWords.Add(scoredEvt);
                    OnWordScored?.Invoke(scoredEvt);

                    // Prime the word — gold words get gold primed status
                    int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                    int primedId  = _primedRegistry.AddPrimedWord(
                        match.Word,
                        match.Cells,
                        playerIndex,
                        _globalTurn,
                        expiresOn,
                        finalScore,
                        isGoldWord);

                    justPrimedThisResolution.Add(primedId);

                    var primedEvt = new WordPrimedEvent
                    {
                        Word          = match.Word,
                        Cells         = new List<Vector2Int>(match.Cells),
                        PlayerIndex   = playerIndex,
                        PrimedOnTurn  = _globalTurn,
                        ExpiresOnTurn = expiresOn,
                        PrimedWordId  = primedId,
                    };
                    result.PrimedWords.Add(primedEvt);
                    OnWordPrimed?.Invoke(primedEvt);
                    GameAudio.Instance?.PlayTilePrimed();

                    // Check if any cell in this new word overlaps an EXISTING primed word
                    // (not one primed in THIS resolution)
                    for (int c = 0; c < match.Cells.Count; c++)
                    {
                        Vector2Int cell = match.Cells[c];
                        List<PrimedWordRegistry.PrimedWord> overlapping =
                            _primedRegistry.GetPrimedWordsContaining(cell);

                        for (int p = 0; p < overlapping.Count; p++)
                        {
                            PrimedWordRegistry.PrimedWord pw = overlapping[p];

                            // Skip if this primed word was created in THIS resolution
                            if (justPrimedThisResolution.Contains(pw.Id))
                                continue;

                            // Skip if already queued for explosion
                            if (primedIdsToExplode.Contains(pw.Id))
                                continue;

                            primedIdsToExplode.Add(pw.Id);

//                             Debug.Log($"[RulesEngine] TRIGGER! New word '{match.Word}' " +
                                      // $"at cell ({cell.x},{cell.y}) overlaps primed " +
                                      // $"'{pw.Word}' (id={pw.Id})");

                            var trigEvt = new PrimedTriggeredEvent
                            {
                                TriggeredWord    = pw.Word,
                                TriggeredCells   = new List<Vector2Int>(pw.Cells),
                                TriggerWord      = match.Word,
                                OverlapCell      = cell,
                                OwnerPlayerIndex = pw.OwnerPlayer,
                                PrimedWordId     = pw.Id,
                            };
                            triggeredEvents.Add(trigEvt);
                            result.TriggeredPrimedWords.Add(trigEvt);
                            OnPrimedTriggered?.Invoke(trigEvt);
                        }
                    }
                }

                // 5c-overlap. Overlap Fuse Extension for non-triggered existing primed words
                {
                    HashSet<int> alreadyExtended = new HashSet<int>();
                    foreach (int newId in justPrimedThisResolution)
                    {
                        var newPw = _primedRegistry.GetById(newId);
                        if (newPw == null || newPw.Cells == null) continue;
                        for (int p = 0; p < _primedRegistry.Count; p++)
                        {
                            var oldPw = _primedRegistry.GetByIndex(p);
                            if (oldPw == null) continue;
                            if (justPrimedThisResolution.Contains(oldPw.Id)) continue;
                            if (primedIdsToExplode.Contains(oldPw.Id)) continue; // already detonating
                            if (alreadyExtended.Contains(oldPw.Id)) continue;

                            bool overlaps = false;
                            for (int c = 0; c < newPw.Cells.Count && !overlaps; c++)
                                for (int d = 0; d < oldPw.Cells.Count && !overlaps; d++)
                                {
                                    int ddx = Mathf.Abs(newPw.Cells[c].x - oldPw.Cells[d].x);
                                    int ddy = Mathf.Abs(newPw.Cells[c].y - oldPw.Cells[d].y);
                                    if (ddx + ddy <= 1) overlaps = true;
                                }

                            if (overlaps)
                            {
                                oldPw.CreatedAtTime = Time.time;
                                oldPw.ExpiresOnTurn = _globalTurn + GetFuseLength(oldPw.Word.Length);
                                alreadyExtended.Add(oldPw.Id);
                                RefreshPrimedWordTiles(oldPw, _globalTurn);
//                                 Debug.Log($"[OverlapFuse] Legacy: NewPrimed={newPw.Word} overlapped Existing={oldPw.Word} " +
                                          // $"-> +{OVERLAP_FUSE_EXTENSION} fuse (expires={oldPw.ExpiresOnTurn}, " +
                                          // $"bonusGranted={oldPw.OverlapFuseBonusGranted})");
                            }
                        }
                    }
                }

                if (justPrimedThisResolution.Count > 0)
                    ResetExistingPrimedWordsForNewPrime(justPrimedThisResolution, _globalTurn);

                // 5c-b. Expand triggers to connected primed group
                if (primedIdsToExplode.Count > 0)
                {
                    var connectedGroup = _primedRegistry.FindConnectedGroup(
                        primedIdsToExplode, justPrimedThisResolution);
                    for (int g = 0; g < connectedGroup.Count; g++)
                    {
                        var pw = connectedGroup[g];
                        if (primedIdsToExplode.Contains(pw.Id)) continue;
                        primedIdsToExplode.Add(pw.Id);
//                         Debug.Log($"[PrimedChain] Legacy path: chain-connected '{pw.Word}' (id={pw.Id})");
                    }
                }

                // 5d. Explode all triggered primed words
                if (primedIdsToExplode.Count > 0)
                {
                    keepChaining = true; // gravity may create new words

                    // Collect all cells to remove
                    HashSet<Vector2Int> allCellsToRemove = new HashSet<Vector2Int>();

                    int detonationBonus = 0;
                    int longestPrimedWord = 0;
                    var primedSplashSources = new List<List<Vector2Int>>();

                    foreach (int pid in primedIdsToExplode)
                    {
                        PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                        if (pw == null) continue;

                        if (pw.Word.Length > longestPrimedWord)
                            longestPrimedWord = pw.Word.Length;
                        if (pw.Cells != null && pw.Cells.Count > 0)
                            primedSplashSources.Add(new List<Vector2Int>(pw.Cells));

                        for (int c = 0; c < pw.Cells.Count; c++)
                            allCellsToRemove.Add(pw.Cells[c]);

                        // Detonation bonus: base + heat fuse + chain-depth scaling
                        int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                        int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                        int rawBonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS + heatBonus;

                        // Evaluate multiplier at depth+1 so the opening detonation is 2x,
                        // not a flat 1x. The triangular cap still applies inside the helper.
                        float chainMultiplier = TriangularChainMultiplier(chainStep + 1);
                        float goldMultiplier = pw.IsGold ? 2f : 1f; // balanced: same 2x as normal gold scoring // gold primed words detonate for 3x
                        int bonus = Mathf.RoundToInt(rawBonus * chainMultiplier * goldMultiplier);

                        detonationBonus += bonus;
//                         Debug.Log($"[RulesEngine] DETONATION BONUS: '{pw.Word}' explodes for +{bonus} pts " +
                                  // $"(base={BREAKER_BONUS} heat={heatBonus} survived={survivedTurns} chain={chainStep} x{chainMultiplier:F1})");

                        // Remove from registry
                        _primedRegistry.RemovePrimedWord(pid);
                        justPrimedThisResolution.Remove(pid);
                    }

                    totalScore += detonationBonus;
                    detonationBonusAccum += detonationBonus;

                    if (!_splashFiredThisResolution
                        && SurvivalManager.IsSurvivalMode
                        && (chainStep >= 2 || longestPrimedWord >= 6 || primedIdsToExplode.Count >= 2))
                    {
                        _splashFiredThisResolution = true; // ONE splash per resolution
                        int splashBaseBonus = 0;
                        int splashPrimedBonus = 0;
                        var splashPrimedIds = new HashSet<int>();

                        foreach (var cells in primedSplashSources)
                        {
                            if (cells == null || cells.Count == 0) continue;

                            int minCol = int.MaxValue, maxCol = int.MinValue;
                            int minRow = int.MaxValue, maxRow = int.MinValue;
                            foreach (var c in cells)
                            {
                                if (c.x < minCol) minCol = c.x;
                                if (c.x > maxCol) maxCol = c.x;
                                if (c.y < minRow) minRow = c.y;
                                if (c.y > maxRow) maxRow = c.y;
                            }

                            bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;
                            if (vertical)
                            {
                                int sweepCol = minCol;
                                for (int row = 0; row < ROWS; row++)
                                {
                                    var cell = new Vector2Int(sweepCol, row);
                                    if (allCellsToRemove.Contains(cell)) continue;
                                    if (!InBounds(sweepCol, row) || _board[sweepCol, row] == null) continue;

                                    var data = _board[sweepCol, row];
                                    splashBaseBonus += LetterData.GetPoints(data.Letter) * (_bonusCells[sweepCol, row] ? 2 : 1);

                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var hitPw in primedHere) splashPrimedIds.Add(hitPw.Id);

                                    allCellsToRemove.Add(cell);
                                }
                            }
                            else
                            {
                                int sweepRow = minRow;
                                // Loop var renamed from 'col' — that name shadows the
                                // ProcessDrop(int col, ...) parameter and causes CS0136.
                                for (int sc = 0; sc < COLS; sc++)
                                {
                                    var cell = new Vector2Int(sc, sweepRow);
                                    if (allCellsToRemove.Contains(cell)) continue;
                                    if (!InBounds(sc, sweepRow) || _board[sc, sweepRow] == null) continue;

                                    var data = _board[sc, sweepRow];
                                    splashBaseBonus += LetterData.GetPoints(data.Letter) * (_bonusCells[sc, sweepRow] ? 2 : 1);

                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var hitPw in primedHere) splashPrimedIds.Add(hitPw.Id);

                                    allCellsToRemove.Add(cell);
                                }
                            }
                        }

                        foreach (int splashPid in splashPrimedIds)
                        {
                            var splashPw = _primedRegistry.GetById(splashPid);
                            if (splashPw == null) continue;

                            if (splashPw.Cells != null)
                                for (int c = 0; c < splashPw.Cells.Count; c++)
                                    allCellsToRemove.Add(splashPw.Cells[c]);

                            splashPrimedBonus += Mathf.RoundToInt(splashPw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS;
                            _primedRegistry.RemovePrimedWord(splashPid);
                            justPrimedThisResolution.Remove(splashPid);
                        }

                        totalScore += splashBaseBonus + splashPrimedBonus;
                        detonationBonusAccum += splashBaseBonus + splashPrimedBonus;
                    }

                    // Remove cells from board data
                    List<Vector2Int> removedList = new List<Vector2Int>(allCellsToRemove);
                    ExplodeCells(removedList);

                    // Emit TilesExploded
                    var explodeEvt = new TilesExplodedEvent
                    {
                        RemovedCells = removedList,
                        SourceWord   = "triggered",
                        ChainStep    = chainStep,
                    };
                    result.Explosions.Add(explodeEvt);
                    OnTilesExploded?.Invoke(explodeEvt);

                    // Purge scored word keys that reference removed cells
                    PurgeScoredKeysForCells(removedList);

                    // 5e. Apply gravity in data layer
                    var gravityMoves = ApplyGravityInData();

                    if (gravityMoves.Count > 0)
                    {
                        var gravEvt = new GravityCollapseEvent
                        {
                            TileMoves = gravityMoves,
                            ChainStep = chainStep,
                        };
                        result.GravityEvents.Add(gravEvt);
                        OnGravityCollapse?.Invoke(gravEvt);

                        // Seed all occupied cells in columns that had movement
                        var movedCols = new HashSet<int>();
                        foreach (var kvp in gravityMoves)
                            movedCols.Add(kvp.Value.x);
                        localSeedCells = new List<Vector2Int>();
                        foreach (int c in movedCols)
                            for (int r = 0; r < ROWS; r++)
                                if (_board[c, r] != null)
                                    localSeedCells.Add(new Vector2Int(c, r));

//                         Debug.Log($"[RulesEngine] Gravity applied — {gravityMoves.Count} tile(s) moved.");
                    }

                    // Update primed word cell positions after gravity shift
                    if (gravityMoves.Count > 0)
                        _primedRegistry.UpdateCellPositions(gravityMoves);

                    // Remove primed words where tiles no longer match letters
                    RemoveInvalidPrimedWords();
                }
                else
                {
                    // No explosions — no further chaining
                    keepChaining = false;
                }

                chainStep++;
            }

            if (chainStep >= MAX_CHAIN_DEPTH)
                Debug.LogWarning("[RulesEngine] Chain hit MAX_CHAIN_DEPTH — stopping.");

            result.TotalScore = totalScore;
            result.ChainSteps = Mathf.Max(0, chainStep - 1);

            // Emit ResolutionComplete
            OnResolutionComplete?.Invoke(new ResolutionCompleteEvent
            {
                TotalScoreEarned = totalScore,
                TotalChainSteps  = result.ChainSteps,
                WordsScored      = result.ScoredWords.Count,
                PlayerIndex      = playerIndex,
            });

            // 8. Expire old primed words AFTER resolution (moved from step 4).
            // This gives players one last chance to detonate on the expiry turn.
            var expiredList2 = new List<PrimedWordRegistry.PrimedWord>();
            int expired = _primedRegistry.ExpireOldWords(_globalTurn, expiredList2);
            if (expired > 0)
            {
                foreach (var pw in expiredList2)
                    _scoredWordKeys.Remove(pw.Word + "|" + pw.CellsString());
//                 Debug.Log($"[RulesEngine] Expired {expired} primed word(s) at turn {_globalTurn}, scored keys purged");
            }

            // FINAL cleanup: remove any primed words whose letters no longer match
            RemoveInvalidPrimedWords();

            result.BaseWordScoreTotal = baseScoreAccum;
            result.ChainBonusTotal = chainBonusAccum;
            result.DetonationBonusTotal = detonationBonusAccum;

//             Debug.Log($"[RulesEngine] ProcessDrop complete: " +
                      // $"words={result.ScoredWords.Count}, " +
                      // $"explosions={result.Explosions.Count}, " +
                      // $"chains={result.ChainSteps}, " +
                      // $"totalScore={totalScore} (base={baseScoreAccum} chain={chainBonusAccum} det={detonationBonusAccum}), " +
                      // $"primedRemaining={_primedRegistry.Count}");

            return result;
        }

        /// <summary>
        /// Removes primed words whose stored letters no longer match the actual board.
        /// This handles the case where gravity shifted tiles and the primed word's cells
        /// now contain different letters than when the word was originally formed.
        /// </summary>
        private void RemoveInvalidPrimedWords()
        {
            var allPrimed = _primedRegistry.GetAllPrimedWords();
            for (int i = allPrimed.Count - 1; i >= 0; i--)
            {
                var pw = allPrimed[i];
                bool valid = true;

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    int col = pw.Cells[c].x;
                    int row = pw.Cells[c].y;

                    if (!InBounds(col, row) || _board[col, row] == null)
                    {
                        valid = false;
                        break;
                    }

                    char expected = pw.Word[c];
                    char actual = char.ToUpper(_board[col, row].Letter);
                    if (actual != expected)
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    // Enhanced logging: show exactly which cell/letter failed
                    string mismatchDetail = "";
                    for (int c2 = 0; c2 < pw.Cells.Count; c2++)
                    {
                        int cc = pw.Cells[c2].x, rr = pw.Cells[c2].y;
                        char exp = pw.Word[c2];
                        char act = (InBounds(cc, rr) && _board[cc, rr] != null) ? _board[cc, rr].Letter : '?';
                        bool ok = (act == exp);
                        mismatchDetail += $" [{cc},{rr}]:{exp}{(ok ? "=" : "≠")}{act}";
                    }
//                     Debug.Log($"[RulesEngine] Removing invalid primed word '{pw.Word}' (id={pw.Id}) —{mismatchDetail}");

                    // Clear primed glow on surviving tiles so they don't keep glowing
                    if (GridManager.Instance != null)
                    {
                        for (int c3 = 0; c3 < pw.Cells.Count; c3++)
                        {
                            Tile t = GridManager.Instance.GetTile(pw.Cells[c3].x, pw.Cells[c3].y);
                            if (t != null) t.ClearPrimedGlow();
                        }
                    }

                    _primedRegistry.RemovePrimedWord(pw.Id);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // EXPLOSION — Remove cells from board data (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes tiles at given cell positions from the logical board.
        /// Does NOT touch GridManager visuals — emits events for that.
        /// </summary>
        private void ExplodeCells(List<Vector2Int> cells)
        {
            int removed = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                int c = cells[i].x;
                int r = cells[i].y;
                if (InBounds(c, r) && _board[c, r] != null)
                {
                    _board[c, r] = null;
                    _bonusCells[c, r] = false; // gold consumed with tile
                    removed++;
                }
            }
//             Debug.Log($"[RulesEngine] ExplodeCells: removed {removed} cell(s) from data.");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // GRAVITY — Compact columns downward in data layer (Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Compacts each column downward in the logical board data.
        /// Returns a dictionary mapping old positions to new positions for tiles that moved.
        /// </summary>
        private Dictionary<Vector2Int, Vector2Int> ApplyGravityInData()
        {
            var moves = new Dictionary<Vector2Int, Vector2Int>();

            for (int col = 0; col < COLS; col++)
            {
                // Collect surviving cells bottom to top
                List<RulesCellData> surviving = new List<RulesCellData>();
                for (int row = 0; row < ROWS; row++)
                {
                    if (_board[col, row] != null)
                        surviving.Add(_board[col, row]);
                }

                // If column is full or empty, nothing to compact
                bool anyGaps = surviving.Count < ROWS;
                if (!anyGaps)
                {
                    // Check if any gaps exist between tiles
                    bool hasGap = false;
                    for (int row = 0; row < surviving.Count; row++)
                    {
                        if (_board[col, row] == null) { hasGap = true; break; }
                    }
                    if (!hasGap) continue;
                }

                // Clear the column
                for (int row = 0; row < ROWS; row++)
                    _board[col, row] = null;

                // Repack from row 0 upward
                for (int i = 0; i < surviving.Count; i++)
                {
                    RulesCellData cell  = surviving[i];
                    int oldRow          = cell.Row;
                    int newRow          = i;

                    cell.Row = newRow;
                    cell.Col = col;
                    _board[col, newRow] = cell;

                    if (oldRow != newRow)
                    {
                        moves[new Vector2Int(col, oldRow)] = new Vector2Int(col, newRow);
                    }
                }
            }

            return moves;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // FULL BOARD SCAN — Scan every cell for words (used in chain loop)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans the entire board for all valid words (horizontal + vertical, 3-7 letters).
        /// Returns deduplicated list of RulesWordMatch.
        /// </summary>
        private List<RulesWordMatch> ScanEntireBoard()
        {
            // Phase A+B wild resolution: before scanning, commit a letter to every
            // uncommitted wild tile on the board. Algorithm: for each wild, try A-Z
            // and pick the letter that yields the longest valid word passing through
            // that cell (alphabetical tiebreak). Commit is one-way — cell.Letter is
            // written permanently but IsWild stays true for visuals/scoring.
            //
            // Design note: a wild is "uncommitted" iff IsWild && Letter == '\0'.
            // Uncommitted wilds act as run-breakers (like stones) until resolved.
            // Max 1 wild on the board in Phase C, so the O(26 * cells) cost is fine.
            ResolveUncommittedWilds();

            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                for (int startCol = 0; startCol < COLS; startCol++)
                {
                    for (int startRow = 0; startRow < ROWS; startRow++)
                    {
                        if (_board[startCol, startRow] == null) continue;

                        // Find max contiguous run length. Breaks: out of bounds, null,
                        // stone, or an uncommitted wild (Letter == '\0').
                        int maxLen = 0;
                        int safety = 0;
                        while (safety < MAX_WORD_LENGTH + 1)
                        {
                            int c = startCol + dc * maxLen;
                            int r = startRow + dr * maxLen;
                            if (!InBounds(c, r) || _board[c, r] == null || _board[c, r].IsStone) break;
                            if (_board[c, r].Letter == '\0') break; // unresolved wild blocks run
                            maxLen++;
                            safety++;
                        }

                        if (maxLen < MIN_WORD_LENGTH) continue;

                        int maxWordLen = Mathf.Min(maxLen, MAX_WORD_LENGTH);

                        for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxWordLen; wordLen++)
                        {
                            char[]           chars = new char[wordLen];
                            List<Vector2Int> cells = new List<Vector2Int>(wordLen);
                            List<int>        wildIndices = null;
                            bool valid = true;
                            bool hasRealLetter = false;

                            for (int step = 0; step < wordLen; step++)
                            {
                                int c = startCol + dc * step;
                                int r = startRow + dr * step;
                                RulesCellData cell = _board[c, r];
                                if (cell == null) { valid = false; break; }
                                chars[step] = char.ToUpper(cell.Letter);
                                cells.Add(new Vector2Int(c, r));
                                if (cell.IsWild)
                                {
                                    if (wildIndices == null) wildIndices = new List<int>(1);
                                    wildIndices.Add(step);
                                }
                                else
                                {
                                    hasRealLetter = true;
                                }
                            }

                            if (!valid) continue;
                            if (!hasRealLetter) continue; // reject wild-only words

                            string candidate = new string(chars);
                            if (!WordDictionary.IsValidWord(candidate)) continue;

                            // Dedup by sorted cell positions — prevents same word
                            // being found in both directions (e.g. CAT left-to-right
                            // and CAT right-to-left at same cells)
                            string sortedCellKey = BuildSortedCellKey(cells);
                            string dedupKey = candidate + "|" + sortedCellKey;
                            if (seenKeys.Contains(dedupKey)) continue;
                            seenKeys.Add(dedupKey);

                            results.Add(new RulesWordMatch
                            {
                                Word              = candidate,
                                Cells             = cells,
                                Direction         = (WordDirection)(dirIdx % 2),
                                Score             = 0,
                                WildLetterIndices = wildIndices,
                            });
                        }
                    }
                }
            }

            return results;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SEED-CELL-ONLY SCAN — direct detection, no expansion
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Finds all valid words that physically pass through at least one seed
        /// cell. No BFS, no adjacency expansion. Only words whose cell list
        /// includes a seed cell are returned.
        ///
        /// This is the core detection for drops and rewrites: the only words
        /// that should prime are words containing the tile the player placed.
        /// Pre-existing words on the board that don't include the action cell
        /// stay dormant.
        /// </summary>
        private List<RulesWordMatch> ScanSeedCellsOnly(List<Vector2Int> seedCells)
        {
            ResolveUncommittedWilds();

            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            if (seedCells == null || seedCells.Count == 0) return results;

            var seedSet = new HashSet<Vector2Int>(seedCells);

            // STEP 1: Find "direct" words — words physically containing a seed cell.
            // These are the words the player's action created.
            var directWords = new List<RulesWordMatch>();

            for (int s = 0; s < seedCells.Count; s++)
            {
                var seed = seedCells[s];
                if (!InBounds(seed.x, seed.y) || _board[seed.x, seed.y] == null) continue;

                var words = FindNewWords(seed.x, seed.y);
                for (int i = 0; i < words.Count; i++)
                {
                    bool containsSeed = false;
                    for (int c = 0; c < words[i].Cells.Count; c++)
                    {
                        if (seedSet.Contains(words[i].Cells[c]))
                        {
                            containsSeed = true;
                            break;
                        }
                    }
                    if (!containsSeed) continue;

                    string key = words[i].Word + "|" + words[i].CellKey;
                    if (seenKeys.Add(key))
                        results.Add(words[i]);
                }
            }

            return FilterSubstringWords(results);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // WILD TILE RESOLUTION (Phase A+B)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Iterates every unlocked wild cell on the board and commits a letter.
        /// Commit rule: pick the letter A-Z that yields the longest valid word
        /// passing through this cell; alphabetical tiebreak on equal length.
        /// If no valid word exists for any letter, leave/reset uncommitted ('\0').
        ///
        /// Writes _board[col, row].Letter directly. IsWild stays true so the tile
        /// continues to render as a star (Phase C visual) and scoring knows to
        /// zero the wild position.
        ///
        /// Cap: this resolver only resolves wilds in runs containing at least one
        /// real letter. Two adjacent wilds cannot mutually resolve — they stay
        /// uncommitted until a real letter lands next to them.
        /// </summary>
        private void ResolveUncommittedWilds()
        {
            for (int col = 0; col < COLS; col++)
            {
                for (int row = 0; row < ROWS; row++)
                {
                    var cell = _board[col, row];
                    if (cell == null) continue;
                    if (!cell.IsWild) continue;

                    var wildPos = new Vector2Int(col, row);
                    var primedAtWild = _primedRegistry.GetPrimedWordsContaining(wildPos);
                    if (primedAtWild != null && primedAtWild.Count > 0)
                    {
                        Debug.Log($"[WildResolve] SKIP ({col},{row}) — locked by {primedAtWild.Count} primed word(s): " +
                                  string.Join(",", primedAtWild.ConvertAll(p => p.Word)));
                        continue; // locked by an active primed word
                    }

                    int  bestLen    = 0;
                    char bestLetter = '\0';
                    char oldLetter  = cell.Letter;

                    for (char tryCh = 'A'; tryCh <= 'Z'; tryCh++)
                    {
                        cell.Letter = tryCh; // temporary probe
                        int longestThrough = FindLongestValidWordThroughCell(col, row);
                        if (longestThrough > bestLen)
                        {
                            bestLen    = longestThrough;
                            bestLetter = tryCh;
                        }
                        // alpha tiebreak: first letter to hit a length wins,
                        // so the strict > above is correct — don't overwrite.
                    }

                    cell.Letter = bestLetter; // commit (or '\0' if nothing found)
                    if (oldLetter != bestLetter)
                        PurgeScoredKeysForCells(new List<Vector2Int> { wildPos });

                    if (bestLetter != '\0')
                    {
                        Debug.Log($"[WildResolve] OK ({col},{row}) '{oldLetter}'→'{bestLetter}' (longest word len={bestLen})");
                    }
                    else
                    {
                        // Dump neighbor context so we can see why no word was possible.
                        string ctx = DumpWildNeighborhood(col, row);
                        Debug.Log($"[WildResolve] NO-WORD ({col},{row}) stays uncommitted. {ctx}");
                    }
                }
            }
        }

        /// <summary>
        /// Debug helper — returns a short string describing the cells in the same
        /// row and column as (col,row). Used by the wild-resolver trace logs.
        /// </summary>
        private string DumpWildNeighborhood(int col, int row)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("row=[");
            for (int c = 0; c < COLS; c++)
            {
                var bc = _board[c, row];
                char ch = (bc == null) ? '.' : (bc.IsStone ? '#' : (bc.Letter == '\0' ? '?' : bc.Letter));
                sb.Append(c == col ? $"*{ch}*" : ch.ToString());
            }
            sb.Append("] col=[");
            for (int r = 0; r < ROWS; r++)
            {
                var bc = _board[col, r];
                char ch = (bc == null) ? '.' : (bc.IsStone ? '#' : (bc.Letter == '\0' ? '?' : bc.Letter));
                sb.Append(r == row ? $"*{ch}*" : ch.ToString());
            }
            sb.Append("]");
            return sb.ToString();
        }

        /// <summary>
        /// Probe helper for wild resolution. Assumes (col, row) currently holds the
        /// candidate letter (set by caller). Returns the length of the longest valid
        /// word that passes through (col, row), considering both scan directions.
        /// Respects stone and wild-blocker run-break rules.
        /// </summary>
        private int FindLongestValidWordThroughCell(int col, int row)
        {
            int longest = 0;

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                // Walk backward from (col,row) to find how many cells precede it.
                int backExtent = 0;
                while (backExtent < MAX_WORD_LENGTH)
                {
                    int c = col - dc * (backExtent + 1);
                    int r = row - dr * (backExtent + 1);
                    if (!InBounds(c, r)) break;
                    var bc = _board[c, r];
                    if (bc == null || bc.IsStone || bc.Letter == '\0') break;
                    backExtent++;
                }

                // Walk forward from (col,row) to find how many cells follow.
                int fwdExtent = 0;
                while (fwdExtent < MAX_WORD_LENGTH)
                {
                    int c = col + dc * (fwdExtent + 1);
                    int r = row + dr * (fwdExtent + 1);
                    if (!InBounds(c, r)) break;
                    var fc = _board[c, r];
                    if (fc == null || fc.IsStone || fc.Letter == '\0') break;
                    fwdExtent++;
                }

                int maxTotal = backExtent + 1 + fwdExtent;
                if (maxTotal > MAX_WORD_LENGTH) maxTotal = MAX_WORD_LENGTH;
                if (maxTotal < MIN_WORD_LENGTH) continue;

                // Try every sub-run of every length that contains (col,row).
                // backOffset = how many cells to the left/below of the wild the run starts.
                for (int len = MIN_WORD_LENGTH; len <= maxTotal; len++)
                {
                    int maxBack = Mathf.Min(backExtent, len - 1);
                    int minBack = Mathf.Max(0, len - 1 - fwdExtent);
                    for (int backOff = minBack; backOff <= maxBack; backOff++)
                    {
                        int startC = col - dc * backOff;
                        int startR = row - dr * backOff;

                        char[] chars = new char[len];
                        bool ok = true;
                        for (int s = 0; s < len; s++)
                        {
                            int c = startC + dc * s;
                            int r = startR + dr * s;
                            if (!InBounds(c, r)) { ok = false; break; }
                            var pc = _board[c, r];
                            if (pc == null || pc.IsStone || pc.Letter == '\0') { ok = false; break; }
                            chars[s] = char.ToUpper(pc.Letter);
                        }
                        if (!ok) continue;

                        string cand = new string(chars);
                        if (WordDictionary.IsValidWord(cand))
                        {
                            if (len > longest) longest = len;
                        }
                    }
                }
            }

            return longest;
        }

        /// <summary>
        /// DEBUG: Toggle a board cell into a wild tile for Phase A+B playtesting.
        /// Clears the cell's letter (will re-resolve on next scan), sets IsWild,
        /// and clears any gold/cyan bonus on the cell (invariant: wilds carry no
        /// bonuses). The tile must already exist at (col, row).
        ///
        /// This is the ONLY way to place wilds in Phase A+B — HandManager injection
        /// comes in Phase C. Call from a console command or Inspector button.
        /// </summary>
        public void DebugSetWild(int col, int row)
        {
            if (!InBounds(col, row)) { Debug.LogWarning($"[RulesEngine] DebugSetWild: ({col},{row}) out of bounds."); return; }
            var cell = _board[col, row];
            if (cell == null) { Debug.LogWarning($"[RulesEngine] DebugSetWild: no tile at ({col},{row})."); return; }
            if (cell.IsStone) { Debug.LogWarning($"[RulesEngine] DebugSetWild: cannot wildify a stone tile."); return; }

            cell.IsWild = true;
            cell.Letter = '\0'; // forces re-resolution on next scan
            _bonusCells[col, row] = false; // wilds never carry gold
            _cyanCells[col, row] = false;  // wilds never carry cyan refund

            // Purge any scored keys involving this cell so new words through the wild score fresh.
            PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

//             Debug.Log($"[RulesEngine] DebugSetWild: ({col},{row}) is now a wild tile (letter cleared).");
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SCORED KEY MANAGEMENT
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Purges scored word keys that reference any of the removed cell coordinates.
        /// After gravity, those coordinates will hold different tiles, so words
        /// forming at those positions should score fresh.
        /// </summary>
        public void PurgeScoredKeysForCells(List<Vector2Int> removedCells)
        {
            if (removedCells == null || removedCells.Count == 0) return;

            HashSet<string> removedCoords = new HashSet<string>();
            for (int i = 0; i < removedCells.Count; i++)
                removedCoords.Add($"{removedCells[i].x},{removedCells[i].y}");

            List<string> keysToRemove = new List<string>();
            foreach (string key in _scoredWordKeys)
            {
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');
                for (int i = 0; i < coords.Length; i++)
                {
                    if (removedCoords.Contains(coords[i]))
                    {
                        keysToRemove.Add(key);
                        break;
                    }
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
                _scoredWordKeys.Remove(keysToRemove[i]);

        }

        /// <summary>
        /// Purges per-resolution _stepScoredKeys that reference any removed cell.
        /// Mirrors PurgeScoredKeysForCells but for the step-local set, so words
        /// that reform at exploded cells within the same resolution can re-score.
        /// </summary>
        private void PurgeStepScoredKeysForCells(List<Vector2Int> removedCells)
        {
            if (_stepScoredKeys == null || removedCells == null || removedCells.Count == 0) return;

            HashSet<string> removedCoords = new HashSet<string>();
            for (int i = 0; i < removedCells.Count; i++)
                removedCoords.Add($"{removedCells[i].x},{removedCells[i].y}");

            List<string> keysToRemove = new List<string>();
            foreach (string key in _stepScoredKeys)
            {
                int pipeIdx = key.IndexOf('|');
                if (pipeIdx < 0) continue;
                string cellPart = key.Substring(pipeIdx + 1);
                string[] coords = cellPart.Split(';');
                for (int i = 0; i < coords.Length; i++)
                {
                    if (removedCoords.Contains(coords[i]))
                    {
                        keysToRemove.Add(key);
                        break;
                    }
                }
            }

            for (int i = 0; i < keysToRemove.Count; i++)
                _stepScoredKeys.Remove(keysToRemove[i]);

        }

        // ═══════════════════════════════════════════════════════════════════════════
        // WORD DETECTION — FindNewWords (Job 2, kept)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans horizontal and vertical lines passing through the cell at (col, row).
        /// Returns a list of RulesWordMatch structs for all valid words of 3–7 letters found.
        /// </summary>
        public List<RulesWordMatch> FindNewWords(int col, int row)
        {
            var results  = new List<RulesWordMatch>();
            var seenKeys = new HashSet<string>();

            if (!InBounds(col, row) || _board[col, row] == null)
            {
//                 Debug.Log($"[RulesEngine] FindNewWords({col},{row}) — cell is empty or out of bounds.");
                return results;
            }

            // Wild handling (Phase A+B): FindNewWords is called from non-mutating
            // paths (swap/edit validation, drop simulation), so we do NOT call
            // ResolveUncommittedWilds here — that would commit letters as a side
            // effect of a preview. Instead: uncommitted wilds (Letter=='\0') act
            // as run-breakers, and already-committed wilds track WildLetterIndices
            // so scoring stays correct.
            //
            // Phase A+B limitation: swap/edit validation will not "see through"
            // an uncommitted wild. The next real drop → ScanEntireBoard will
            // resolve it. Revisit in Phase C if the debug flow needs it.

            for (int dirIdx = 0; dirIdx < _directions.Length; dirIdx++)
            {
                int dc = _directions[dirIdx][0];
                int dr = _directions[dirIdx][1];

                // If the starting cell itself is an uncommitted wild, we can't
                // build any word through it in this path.
                if (_board[col, row].Letter == '\0') continue;

                // Walk backward (break on null, stone, or uncommitted wild)
                int runStart = 0;
                int safety   = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col - dc * (runStart + 1);
                    int nr = row - dr * (runStart + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    if (_board[nc, nr].IsStone) break;
                    if (_board[nc, nr].Letter == '\0') break;
                    runStart++;
                    safety++;
                }

                // Walk forward (same run-break rules)
                int runEnd = 0;
                safety = 0;
                while (safety < MAX_WORD_LENGTH + 1)
                {
                    int nc = col + dc * (runEnd + 1);
                    int nr = row + dr * (runEnd + 1);
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) break;
                    if (_board[nc, nr].IsStone) break;
                    if (_board[nc, nr].Letter == '\0') break;
                    runEnd++;
                    safety++;
                }

                int runLength = runStart + 1 + runEnd;
                if (runLength < MIN_WORD_LENGTH) continue;

                int absStartCol = col - dc * runStart;
                int absStartRow = row - dr * runStart;

                char[]           runChars     = new char[runLength];
                bool[]           runIsWild    = new bool[runLength];
                List<Vector2Int> runCells     = new List<Vector2Int>(runLength);
                bool runValid = true;

                for (int step = 0; step < runLength && runValid; step++)
                {
                    int nc = absStartCol + dc * step;
                    int nr = absStartRow + dr * step;
                    if (!InBounds(nc, nr) || _board[nc, nr] == null) { runValid = false; break; }
                    runChars[step]  = char.ToUpper(_board[nc, nr].Letter);
                    runIsWild[step] = _board[nc, nr].IsWild;
                    runCells.Add(new Vector2Int(nc, nr));
                }

                if (!runValid) continue;

                int maxLen = Mathf.Min(runLength, MAX_WORD_LENGTH);

                for (int wordLen = MIN_WORD_LENGTH; wordLen <= maxLen; wordLen++)
                {
                    for (int startOffset = 0; startOffset <= runLength - wordLen; startOffset++)
                    {
                        char[]           wordChars   = new char[wordLen];
                        List<Vector2Int> wordCells   = new List<Vector2Int>(wordLen);
                        List<int>        wildIndices = null;
                        bool             hasRealLetter = false;

                        for (int k = 0; k < wordLen; k++)
                        {
                            wordChars[k] = runChars[startOffset + k];
                            wordCells.Add(runCells[startOffset + k]);
                            if (runIsWild[startOffset + k])
                            {
                                if (wildIndices == null) wildIndices = new List<int>(1);
                                wildIndices.Add(k);
                            }
                            else
                            {
                                hasRealLetter = true;
                            }
                        }

                        if (!hasRealLetter) continue; // reject wild-only words

                        string candidate = new string(wordChars);
                        if (!WordDictionary.IsValidWord(candidate))
                            continue;

                        string sortedCellKey = BuildSortedCellKey(wordCells);
                        string dedupKey = candidate + "|" + sortedCellKey;
                        if (seenKeys.Contains(dedupKey)) continue;
                        seenKeys.Add(dedupKey);

                        string dirName = dirIdx % 2 == 0 ? "horizontal" : "vertical";
//                         Debug.Log($"[RulesEngine] Found word '{candidate}' " +
                                  // $"({dirName}) at cells {BuildCellKey(wordCells)}");

                        results.Add(new RulesWordMatch
                        {
                            Word              = candidate,
                            Cells             = wordCells,
                            Direction         = (WordDirection)(dirIdx % 2),
                            Score             = 0,
                            WildLetterIndices = wildIndices,
                        });
                    }
                }
            }

            // if (results.Count > 0)
//                 Debug.Log($"[RulesEngine] FindNewWords({col},{row}) → {results.Count} word(s) found.");

            return results;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // SIMULATE DROP — Non-mutating evaluation for AI (Job 9 enhanced)
        // ═══════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Simulates placing a letter at (col, lowestEmptyRow) without committing.
        /// Returns word matches with scores calculated.
        /// Filters out already-scored words so AI only sees NEW words the drop would create.
        ///
        /// Also checks for overlap with primed words — returned matches that share
        /// cells with primed words indicate a potential trigger.
        ///
        /// Does NOT modify the board state — places and then removes the simulated cell.
        /// </summary>
        public List<RulesWordMatch> SimulateDrop(int col, char letter, int playerIndex)
        {
            int targetRow = GetLowestEmptyRow(col);
            if (targetRow < 0) return new List<RulesWordMatch>();

            var simCell = new RulesCellData
            {
                Letter      = char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
            };
            _board[col, targetRow] = simCell;

            // Find words that physically contain the drop cell
            List<RulesWordMatch> allMatches = FindNewWords(col, targetRow);
            var dropCell = new Vector2Int(col, targetRow);

            // Filter: only words containing the drop cell + not already scored
            List<RulesWordMatch> newMatches = new List<RulesWordMatch>();
            for (int i = 0; i < allMatches.Count; i++)
            {
                bool containsDrop = false;
                for (int c = 0; c < allMatches[i].Cells.Count; c++)
                {
                    if (allMatches[i].Cells[c] == dropCell) { containsDrop = true; break; }
                }
                if (!containsDrop) continue;

                string key = allMatches[i].Word + "|" + allMatches[i].CellKey;
                if (!_scoredWordKeys.Contains(key))
                {
                    var m = allMatches[i];
                    m.Score = CalculateWordScore(m.Word, m.WildLetterIndices);
                    newMatches.Add(m);
                }
            }

            // Restore board — remove the simulated cell
            _board[col, targetRow] = null;

            return newMatches;
        }

        /// <summary>
        /// Enhanced simulation that also reports whether the drop would trigger
        /// any existing primed words. Returns matches and sets wouldTriggerPrimed.
        ///
        /// Non-mutating — restores board state after simulation.
        /// </summary>
        public List<RulesWordMatch> SimulateDropWithTriggerCheck(
            int col, char letter, int playerIndex, out bool wouldTriggerPrimed)
        {
            wouldTriggerPrimed = false;

            List<RulesWordMatch> matches = SimulateDrop(col, letter, playerIndex);

            if (matches.Count > 0 && _primedRegistry.Count > 0)
            {
                for (int w = 0; w < matches.Count && !wouldTriggerPrimed; w++)
                {
                    RulesWordMatch match = matches[w];
                    if (match.Cells == null) continue;

                    for (int c = 0; c < match.Cells.Count && !wouldTriggerPrimed; c++)
                    {
                        Vector2Int cell = match.Cells[c];
                        List<PrimedWordRegistry.PrimedWord> overlapping =
                            _primedRegistry.GetPrimedWordsContaining(cell);

                        if (overlapping.Count > 0)
                            wouldTriggerPrimed = true;
                    }
                }
            }

            return matches;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // UNIT TESTS (updated for Job 4)
        // ═══════════════════════════════════════════════════════════════════════════

        [ContextMenu("Run Unit Tests")]
        public void RunUnitTests()
        {
//             Debug.Log("[RulesEngine] ══ BEGIN UNIT TESTS ══");

            // ── Test 1: 3-cell horizontal CAT ─────────────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 0] = new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = 0 };
            _board[2, 0] = new RulesCellData { Letter = 'T', Col = 2, Row = 0, PlayerIndex = 0 };

            var matches1 = FindNewWords(1, 0);
            bool found1 = ContainsWord(matches1, "CAT");
//             Debug.Log($"[RulesEngine] Test 1 — horizontal 'CAT': " +
                      // $"{(found1 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 2: diagonal CAT — should NOT find ─────────────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 1] = new RulesCellData { Letter = 'A', Col = 1, Row = 1, PlayerIndex = 0 };
            _board[2, 2] = new RulesCellData { Letter = 'T', Col = 2, Row = 2, PlayerIndex = 0 };

            var matches2 = FindNewWords(1, 1);
            bool found2 = ContainsWord(matches2, "CAT");
//             Debug.Log($"[RulesEngine] Test 2 — diagonal 'CAT': " +
                      // $"{(!found2 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 3: CalculateWordScore ─────────────────────────────────────────
            int scoreCat  = CalculateWordScore("CAT");   // C(3)+A(1)+T(1)=5 × 1.0 = 5
            int scoreFire = CalculateWordScore("FIRE");  // F(4)+I(1)+R(1)+E(1)=7 × 1.5 = 11 (rounded)
            int scoreSlate = CalculateWordScore("SLATE"); // S(1)+L(1)+A(1)+T(1)+E(1)=5 × 2.0 = 10
            bool test3 = (scoreCat == 5) && (scoreFire == 11) && (scoreSlate == 10);
//             Debug.Log($"[RulesEngine] Test 3 — CalculateWordScore: CAT={scoreCat}(exp5) " +
                      // $"FIRE={scoreFire}(exp11) SLATE={scoreSlate}(exp10) " +
                      // $"{(test3 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 4: ProcessDrop — form CAT, verify events ──────────────────────
            ClearBoard();
            _board[0, 0] = new RulesCellData { Letter = 'C', Col = 0, Row = 0, PlayerIndex = 0 };
            _board[1, 0] = new RulesCellData { Letter = 'A', Col = 1, Row = 0, PlayerIndex = 0 };

            bool wordScoredFired = false;
            bool wordPrimedFired = false;

            RulesEventHandler<WordScoredEvent> scoredHandler = (evt) =>
            {
                if (evt.Word == "CAT") wordScoredFired = true;
            };
            RulesEventHandler<WordPrimedEvent> primedHandler = (evt) =>
            {
                if (evt.Word == "CAT") wordPrimedFired = true;
            };

            OnWordScored += scoredHandler;
            OnWordPrimed += primedHandler;

            ResolutionResult res4 = ProcessDrop(2, 'T', 0);

            OnWordScored -= scoredHandler;
            OnWordPrimed -= primedHandler;

            bool test4 = wordScoredFired && wordPrimedFired && res4.AnyWordScored;
//             Debug.Log($"[RulesEngine] Test 4 — ProcessDrop 'T' forms CAT: " +
                      // $"scored={wordScoredFired} primed={wordPrimedFired} " +
                      // $"{(test4 ? "✓ PASS" : "✗ FAIL")}");

            // ── Test 5: SimulateDrop filters already-scored words ───────────────────
            // CAT is already scored at cells (0,0),(1,0),(2,0).
            // Simulating a drop that would form CAT again should return empty
            // (since CAT at those cells is already in _scoredWordKeys).
            // But let's test a fresh simulation on a different position.
            List<RulesWordMatch> simMatches = SimulateDrop(3, 'S', 1);
            // 'S' at (3,0) — horizontal: ...T S — "ATS"? unlikely to be valid
            // This mainly verifies SimulateDrop runs without error.
//             Debug.Log($"[RulesEngine] Test 5 — SimulateDrop('S', col=3): " +
                      // $"{simMatches.Count} match(es) found ✓ (no crash)");

            // ── Test 6: SimulateDropWithTriggerCheck ────────────────────────────────
            // CAT is primed. Simulate placing 'C' at col 1, row 1 to form ACE vertically.
            // A at (1,0) is part of primed CAT.
            _board[1, 1] = new RulesCellData { Letter = 'C', Col = 1, Row = 1, PlayerIndex = 0 };

            bool wouldTrigger;
            List<RulesWordMatch> trigMatches = SimulateDropWithTriggerCheck(1, 'E', 0, out wouldTrigger);

            // Clean up the manually placed cell
            _board[1, 1] = null;

//             Debug.Log($"[RulesEngine] Test 6 — SimulateDropWithTriggerCheck: " +
                      // $"wouldTrigger={wouldTrigger} matches={trigMatches.Count}");

            // ── Summary ───────────────────────────────────────────────────────────
            bool allPassed = found1 && !found2 && test3 && test4;
//             Debug.Log($"[RulesEngine] ══ UNIT TESTS {(allPassed ? "ALL PASSED ✓" : "SOME FAILED ✗")} ══");

            ClearBoard();
        }

        // ── Private helpers ───────────────────────────────────────────────────────

        private static bool InBounds(int col, int row)
            => col >= 0 && col < COLS && row >= 0 && row < ROWS;

        /// <summary>
        /// Removes substring words — if TAMER and TAM share cells in the same line,
        /// only keep TAMER. A word is a substring if ALL its cells are contained
        /// in a longer word's cells.
        /// </summary>
        private static List<RulesWordMatch> FilterSubstringWords(List<RulesWordMatch> words)
        {
            if (words.Count <= 1) return words;

            var result = new List<RulesWordMatch>();

            for (int i = 0; i < words.Count; i++)
            {
                bool isSubstring = false;
                HashSet<Vector2Int> myCells = new HashSet<Vector2Int>(words[i].Cells);

                for (int j = 0; j < words.Count; j++)
                {
                    if (i == j) continue;
                    if (words[j].Cells.Count <= words[i].Cells.Count) continue;

                    // Check if all of my cells are in the longer word
                    bool allContained = true;
                    for (int c = 0; c < words[i].Cells.Count; c++)
                    {
                        bool found = false;
                        for (int d = 0; d < words[j].Cells.Count; d++)
                        {
                            if (words[i].Cells[c] == words[j].Cells[d])
                            {
                                found = true;
                                break;
                            }
                        }
                        if (!found) { allContained = false; break; }
                    }

                    if (allContained)
                    {
                        isSubstring = true;
                        break;
                    }
                }

                if (!isSubstring)
                    result.Add(words[i]);
            }

            return result;
        }

        private static string BuildCellKey(List<Vector2Int> cells)
        {
            var sorted = new List<Vector2Int>(cells);
            sorted.Sort((a, b) =>
            {
                int cmp = a.x.CompareTo(b.x);
                return cmp != 0 ? cmp : a.y.CompareTo(b.y);
            });

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (i > 0) sb.Append(';');
                sb.Append(sorted[i].x).Append(',').Append(sorted[i].y);
            }
            return sb.ToString();
        }

        // Alias — BuildCellKey already sorts
        private static string BuildSortedCellKey(List<Vector2Int> cells) => BuildCellKey(cells);

        private static bool ContainsWord(List<RulesWordMatch> matches, string word)
        {
            for (int i = 0; i < matches.Count; i++)
                if (matches[i].Word == word.ToUpper()) return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════════════
        // STEP-BY-STEP RESOLUTION STATE MACHINE
        // ═════════════════════════════════════════════════════════════════════════════

        // ── Resolution phases ────────────────────────────────────────────────────────

        public enum ResolutionPhase
        {
            Idle,           // No resolution in progress
            TileDropped,    // Tile placed, ready for word detection
            WordsDetected,  // Words found, ready to score
            WordsScored,    // Words scored + primed, ready for trigger check
            TriggersFound,  // Triggers identified, ready to explode
            Exploding,      // Tiles exploding, ready for gravity
            GravityApplied, // Gravity done, check for chain words
            Complete        // Resolution finished
        }

        public class StepResult
        {
            public ResolutionPhase Phase;
            public int Row = -1;
            public List<RulesWordMatch> NewWords;
            public List<WordScoredEvent> ScoredWords;
            public List<PrimedTriggeredEvent> Triggers;
            public List<Vector2Int> ExplodedCells;
            public Dictionary<Vector2Int, Vector2Int> GravityMoves;
            public int TotalScore;
            public bool ChainContinues;
            public int ChainTriggeredCount;
            public int DetonationBonus;  // total detonation bonus this step
            public int DetonationHeat;   // heat portion of detonation bonus
            public int ChainDepth;       // chain depth when this detonation fired
            public bool ReplacedTopTile; // true if this drop replaced the top tile in a full column
            public int SwapRefillCount;  // swap refill tiles detonated this step
            public int EditRefillCount;  // edit refill tiles detonated this step
            public int WildRefillCount;  // wild refill tiles detonated this step
            public int LongestWordLength; // longest primed word in this detonation (for VFX tier)
            // Trigger words: the NEW words formed this step that caused the detonation.
            // Exposed so VFX (BigBurstFlash) can blast the player's word too, not only
            // the primed words it ignited. Null when the step didn't trigger anything.
            public List<RulesWordMatch> TriggerWords;
        }

        // ── Step-by-step state ───────────────────────────────────────────────────────

        private ResolutionPhase _currentPhase = ResolutionPhase.Idle;
        private int _stepPlayerIndex;
        private int _stepChainDepth;
        private int _stepTotalScore;
        // Big-moment splash fires ONCE per resolution, not every chain iteration.
        // Prevents the feedback loop where splash clears tiles → gravity creates
        // new words → those chain-score huge → trigger more primed → splash again.
        private bool _splashFiredThisResolution;
        private HashSet<int> _stepJustPrimed;
        private HashSet<string> _stepScoredKeys;
        private List<RulesWordMatch> _stepPendingWords;
        private List<RulesWordMatch> _stepTriggerWords; // words that caused the detonation (for Survival clear-both)
        private List<int> _stepPendingTriggers;
        private int _stepChainTriggeredCount;

        // Seed cells for connected-word detection. Set in BeginDrop/BeginRewrite.
        // After gravity, updated to the new positions of moved tiles.
        private List<Vector2Int> _stepSeedCells;

        // ── Public accessors ─────────────────────────────────────────────────────────

        public ResolutionPhase CurrentPhase => _currentPhase;
        public bool IsResolving => _currentPhase != ResolutionPhase.Idle && _currentPhase != ResolutionPhase.Complete;

        /// <summary>
        /// Lightweight read-only check: will DoCheckTriggers find any triggers?
        /// Call during WordsScored phase to decide whether to play full scoring
        /// animation or skip it in favour of the detonation presentation.
        /// </summary>
        public bool PeekHasTriggers()
        {
            if (_stepPendingWords == null || _stepPendingWords.Count == 0) return false;

            // Phase 9.10: mirror the Phase 9.7 DoCheckTriggers filter-skip so the
            // peek matches actual trigger behavior for cascade self-overlaps.
            // Without this, PeekHasTriggers returns false for gravity-formed words
            // that will self-detonate mid-chain, which caused GameVisualBridge to
            // play the slow non-detonation scoring branch (0.35s) during cascades.
            //
            // Phase 11+ extension: apply to Survival too. Spencer's ask — "I wish
            // the cascade worked the same way in Survival as Level mode" — means
            // gravity-formed words should self-detonate mid-chain instead of
            // sitting there waiting for another trigger. One gate, both modes.
            bool skipJustPrimedFilter =
                (GameManager.IsLevelMode || SurvivalManager.IsSurvivalMode)
                && _stepChainDepth > 0;

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                var match = _stepPendingWords[w];
                for (int c = 0; c < match.Cells.Count; c++)
                {
                    // Check direct overlap
                    var overlapping = _primedRegistry.GetPrimedWordsContaining(match.Cells[c]);
                    for (int p = 0; p < overlapping.Count; p++)
                    {
                        if (skipJustPrimedFilter || !_stepJustPrimed.Contains(overlapping[p].Id))
                            return true;
                    }

                    // Check adjacency (if enabled)
                    if (AdjacencyTriggerEnabled)
                    {
                        var adjacent = _primedRegistry.GetPrimedWordsAdjacentTo(match.Cells[c]);
                        for (int p = 0; p < adjacent.Count; p++)
                        {
                            if (skipJustPrimedFilter || !_stepJustPrimed.Contains(adjacent[p].Id))
                                return true;
                        }
                    }
                }
            }
            return false;
        }

        // ── BeginDrop ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Start a new step-by-step resolution. Places the tile and returns.
        /// Call NextStep() repeatedly to advance through the resolution.
        /// </summary>
        public StepResult BeginDrop(int col, char letter, int playerIndex)
        {
            return BeginDrop(col, letter, playerIndex, isWild: false);
        }

        /// <summary>
        /// Phase C wild-aware drop. When isWild=true the letter arg is ignored and
        /// the cell is placed uncommitted (IsWild=true, Letter='\0'); the next
        /// scan resolves it to the best letter via ResolveUncommittedWilds.
        /// </summary>
        public StepResult BeginDrop(int col, char letter, int playerIndex, bool isWild)
        {
            // Defensive: '*' is the wild sentinel and must never land as a literal
            // letter on the board (no words would form through it). Promote to wild
            // regardless of what the caller passed — covers any upstream desync.
            if (!isWild && letter == TileBag.WILD_CHAR)
            {
                isWild = true;
                Debug.LogWarning("[RulesEngine] BeginDrop: auto-promoted literal '*' drop to wild.");
            }
            if (!isWild && letter == '\0')
            {
                Debug.LogError("[RulesEngine] BeginDrop: REJECTED null letter");
                return null;
            }
            int targetRow = GetLowestEmptyRow(col);
            bool replaced = false;

            if (targetRow < 0)
            {
                // Column full — replace top tile (sacrifice)
                targetRow = ROWS - 1;
                var oldCell = _board[col, targetRow];
                char oldLetter = oldCell != null ? oldCell.Letter : '?';

                // Clear any primed words involving the replaced tile
                if (GridManager.Instance != null)
                {
                    Tile oldTile = GridManager.Instance.GetTile(col, targetRow);
                    if (oldTile != null) oldTile.ClearPrimedGlow();
                }
                _bonusCells[col, targetRow] = false;

                replaced = true;
//                 Debug.Log($"[RulesEngine] BeginDrop: column {col} full — replacing top tile '{oldLetter}' with '{letter}'");
            }

            var cellData = new RulesCellData
            {
                Letter      = isWild ? '\0' : char.ToUpper(letter),
                Col         = col,
                Row         = targetRow,
                PlayerIndex = playerIndex,
                IsWild      = isWild,
            };
            _board[col, targetRow] = cellData;
            // Wilds never carry gold/cyan — clear any bonus on the landing cell.
            if (isWild)
            {
                _bonusCells[col, targetRow] = false;
                _cyanCells[col, targetRow]  = false;
            }

            _stepPlayerIndex   = playerIndex;
            _stepChainDepth    = 0;
            _stepTotalScore    = 0;
            _splashFiredThisResolution = false;
            _stepChainTriggeredCount = 0;
            _stepJustPrimed    = new HashSet<int>();
            _stepScoredKeys    = new HashSet<string>();
            _stepPendingWords    = null;
            _stepTriggerWords    = null;
            _stepPendingTriggers = null;
            _stepSeedCells     = new List<Vector2Int> { new Vector2Int(col, targetRow) };
            _currentPhase      = ResolutionPhase.TileDropped;

            // Expiry moved to FinalizeDrop — gives last-turn detonation chance

//             Debug.Log($"[RulesEngine] BeginDrop: placed '{letter}' at ({col},{targetRow}) player={playerIndex}");

            OnTileDropped?.Invoke(new TileDroppedEvent
            {
                Col         = col,
                Row         = targetRow,
                Letter      = char.ToUpper(letter),
                PlayerIndex = playerIndex,
            });

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = targetRow, ReplacedTopTile = replaced };
        }

        // ── BeginRewrite ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces an existing board tile with a new letter, then enters
        /// the same step-by-step resolution as BeginDrop. Used by the
        /// Rewrite Tile action.
        ///
        /// Returns null if the cell is invalid, empty, not owned by the player,
        /// or contains a primed tile.
        /// </summary>
        public StepResult BeginRewrite(int col, int row, char newLetter, int playerIndex)
        {
            if (col < 0 || col >= COLS || row < 0 || row >= ROWS) return null;
            if (newLetter == '\0') { Debug.LogError("[RulesEngine] BeginRewrite: REJECTED null letter"); return null; }

            RulesCellData existing = _board[col, row];
            if (existing == null) return null;

            // Block rewriting primed tiles
            var primedAtCell = _primedRegistry.GetPrimedWordsContaining(new Vector2Int(col, row));
            if (primedAtCell != null && primedAtCell.Count > 0) return null;

            char oldLetter = existing.Letter;

            // Overwrite the cell
            var cellData = new RulesCellData
            {
                Letter      = char.ToUpper(newLetter),
                Col         = col,
                Row         = row,
                PlayerIndex = playerIndex,
            };
            _board[col, row] = cellData;

            // Purge scored word keys that reference this cell so new words can be detected
            PurgeScoredKeysForCells(new List<Vector2Int> { new Vector2Int(col, row) });

            // Invalidate any primed words that included the old letter at this cell
            RemoveInvalidPrimedWords();

            // Initialize step-by-step resolution (same as BeginDrop)
            _stepPlayerIndex        = playerIndex;
            _stepChainDepth         = 0;
            _stepTotalScore         = 0;
            _stepChainTriggeredCount = 0;
            _splashFiredThisResolution = false;
            _stepJustPrimed         = new HashSet<int>();
            _stepScoredKeys         = new HashSet<string>();
            _stepPendingWords       = null;
            _stepTriggerWords       = null;
            _stepPendingTriggers    = null;
            _stepSeedCells          = new List<Vector2Int> { new Vector2Int(col, row) };
            _currentPhase           = ResolutionPhase.TileDropped;

//             Debug.Log($"[RulesEngine] BeginRewrite: replaced '{oldLetter}' with '{newLetter}' " +
                      // $"at ({col},{row}) player={playerIndex}");

            return new StepResult { Phase = ResolutionPhase.TileDropped, Row = row };
        }

        // ── BeginSwapResolution ──────────────────────────────────────────────────────

        /// <summary>
        /// Initializes the step-by-step resolution for a legal swap.
        /// The swap has already been performed and words detected externally.
        /// This sets up the state so NextStep() can run trigger detection,
        /// detonation, gravity, and chain resolution.
        /// </summary>
        public void BeginSwapResolution(List<RulesWordMatch> detectedWords, int playerIndex, HashSet<int> justPrimedIds)
        {
            _stepPlayerIndex        = playerIndex;
            _stepChainDepth         = 0;
            _stepTotalScore         = 0;
            _stepChainTriggeredCount = 0;
            _splashFiredThisResolution = false;
            _stepJustPrimed         = justPrimedIds ?? new HashSet<int>();
            _stepScoredKeys         = new HashSet<string>();
            _stepPendingWords       = detectedWords;
            _stepTriggerWords       = null;
            _stepPendingTriggers    = null;

            // Seed cells from all detected swap words (needed for post-gravity chain detection)
            var seeds = new List<Vector2Int>();
            if (detectedWords != null)
                foreach (var w in detectedWords)
                    if (w.Cells != null)
                        seeds.AddRange(w.Cells);
            _stepSeedCells = seeds;

            // Skip straight to trigger checking — words are already scored and primed.
            // NextStep() at WordsScored calls DoCheckTriggers().
            _currentPhase = ResolutionPhase.WordsScored;

//             Debug.Log($"[RulesEngine] BeginSwapResolution: {detectedWords.Count} word(s) from swap, " +
                      // $"{_stepJustPrimed.Count} just-primed, player={playerIndex}");
        }

        // ── NextStep ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Advance the resolution by one step. Returns what happened.
        /// Keep calling until Phase == Complete.
        /// </summary>
        public StepResult NextStep()
        {
            switch (_currentPhase)
            {
                case ResolutionPhase.TileDropped:
                case ResolutionPhase.GravityApplied:
                    return DoDetectWords();

                case ResolutionPhase.WordsDetected:
                    return DoScoreAndPrime();

                case ResolutionPhase.WordsScored:
                    return DoCheckTriggers();

                case ResolutionPhase.TriggersFound:
                    return DoExplode();

                case ResolutionPhase.Exploding:
                    return DoGravity();

                default:
                    return new StepResult { Phase = ResolutionPhase.Complete };
            }
        }

        // ── FinalizeDrop ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Clean up after step-by-step resolution. The visual bridge calls this
        /// after all animation is complete. Increments _globalTurn.
        /// </summary>
        public void FinalizeDrop()
        {
            // Guard: FinalizeDrop is NOT safe to call twice — it increments _globalTurn
            // and expires primed words. The safe wrapper and Complete phase both call it,
            // so we must be idempotent.
            if (_currentPhase == ResolutionPhase.Idle)
            {
//                 Debug.Log("[RulesEngine] FinalizeDrop: already Idle — skipping (idempotent guard).");
                return;
            }

            // Expire primed words AFTER resolution (not before).
            // This gives players one last chance to detonate on the expiry turn.
            var expiredList = new List<PrimedWordRegistry.PrimedWord>();
            int expired = _primedRegistry.ExpireOldWords(_globalTurn, expiredList);
            if (expired > 0)
            {
                // Purge scored keys so expired words can be re-primed
                foreach (var pw in expiredList)
                    _scoredWordKeys.Remove(pw.Word + "|" + pw.CellsString());
//                 Debug.Log($"[RulesEngine] FinalizeDrop: expired {expired} primed word(s), scored keys purged");
            }

            RemoveInvalidPrimedWords();
            _globalTurn++;
            _currentPhase = ResolutionPhase.Idle;

            _stepJustPrimed      = null;
            _stepScoredKeys      = null;
            _stepPendingWords    = null;
            _stepPendingTriggers = null;

//             Debug.Log($"[RulesEngine] FinalizeDrop: turn incremented to {_globalTurn}, phase=Idle.");
        }

        // ── Do* step methods ─────────────────────────────────────────────────────────

        private StepResult DoDetectWords()
        {
            // Safety cap on chain depth
            if (_stepChainDepth >= 10)
            {
                Debug.LogWarning("[RulesEngine] Step chain hit depth cap (10) — completing.");
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;
                return new StepResult { Phase = ResolutionPhase.Complete, TotalScore = _stepTotalScore };
            }

            // Seed-cell scan: only detect words physically containing a seed cell.
            // On first pass, seeds = drop position. After gravity, seeds = landing positions.
            List<RulesWordMatch> allWords = ScanSeedCellsOnly(_stepSeedCells);

            // Filter out already-scored words
            List<RulesWordMatch> newWords = new List<RulesWordMatch>();
            for (int i = 0; i < allWords.Count; i++)
            {
                string key = allWords[i].Word + "|" + allWords[i].CellKey;
                if (!_scoredWordKeys.Contains(key) && !_stepScoredKeys.Contains(key))
                    newWords.Add(allWords[i]);
            }

            if (newWords.Count == 0)
            {
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;
//                 Debug.Log($"[RulesEngine] DoDetectWords: no new words — resolution complete (chainDepth={_stepChainDepth}).");
                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingWords = newWords;
            _currentPhase = ResolutionPhase.WordsDetected;

//             Debug.Log($"[RulesEngine] DoDetectWords: found {newWords.Count} new word(s) at chainDepth={_stepChainDepth}.");

            return new StepResult
            {
                Phase = ResolutionPhase.WordsDetected,
                NewWords = new List<RulesWordMatch>(newWords),
                TotalScore = _stepTotalScore,
            };
        }

        private StepResult DoScoreAndPrime()
        {
            var scoredEvents = new List<WordScoredEvent>();

            // Cluster bonus: when multiple primed words trigger in one step
            // (connected-cluster detonation), each word's effective chain depth
            // gets boosted by (count - 1). Without this, a 4-word cluster
            // detonation would score each word at chainStep=0 — massive visual
            // moment, flat score. See decision log 2026-04-17.
            int effectiveChainStep = _stepChainDepth + Mathf.Max(0, _stepPendingWords.Count - 1);

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];
                string key = match.Word + "|" + match.CellKey;

                int baseScore  = CalculateWordScore(match.Word, match.WildLetterIndices);
                // Multiplicative chain bonus — matches the legacy ProcessDrop path.
                // Risky chain plays pay substantially more than safe solo drops.
                float chainMult = (effectiveChainStep > 0)
                    ? 1f + Mathf.Min(effectiveChainStep, CHAIN_DEPTH_SCALE_CAP) * 0.5f
                    : 1f;
                int chainBoosted = Mathf.RoundToInt(baseScore * chainMult);
                int echoBonus  = ConsumeEchoBonus(match.Word, _stepPlayerIndex);
                bool isGoldWord = HasGoldTile(match);
                int bonusMult  = ConsumeGoldAndGetMultiplier(match);
                int finalScore = (chainBoosted + echoBonus) * bonusMult;
                match.Score    = finalScore;
                _stepTotalScore += finalScore;

                Debug.Log($"[RulesEngine] Scored '{match.Word}': base={baseScore}" +
                          (chainBoosted - baseScore > 0 ? $" +chain({chainBoosted - baseScore}, effStep={effectiveChainStep})" : "") +
                          (echoBonus > 0 ? $" +echo({echoBonus})" : "") +
                          $" = {finalScore} pts  [rawStep={_stepChainDepth}, clusterSize={_stepPendingWords.Count}]");

                _scoredWordKeys.Add(key);
                _stepScoredKeys.Add(key);

                // Prime in registry — gold words get gold primed status
                int expiresOn = _globalTurn + GetFuseLength(match.Word.Length);
                int primedId  = _primedRegistry.AddPrimedWord(
                    match.Word,
                    match.Cells,
                    _stepPlayerIndex,
                    _globalTurn,
                    expiresOn,
                    finalScore,
                    isGoldWord);

                _stepJustPrimed.Add(primedId);

                var evt = new WordScoredEvent
                {
                    Word        = match.Word,
                    Cells       = new List<Vector2Int>(match.Cells),
                    BaseScore   = baseScore,
                    FinalScore  = finalScore,
                    PlayerIndex = _stepPlayerIndex,
                    ChainStep   = _stepChainDepth,
                };
                scoredEvents.Add(evt);

                // Fire event so listeners (tutorial, etc.) can react
                OnWordScored?.Invoke(evt);

//                 Debug.Log($"[RulesEngine] DoScoreAndPrime: '{match.Word}' base={baseScore}" +
//                           (chainBonus > 0 ? $" +chain({chainBonus})" : "") +
//                           $" = {finalScore} pts  [chain={_stepChainDepth}] primedId={primedId}");
            }

            // ── Global Fuse Reset ──────────────────────────────────────────
            // When ANY new word primes, reset ALL existing primed words to
            // the same timer. Keeps the entire board in sync.
            if (_stepJustPrimed.Count > 0)
                ResetExistingPrimedWordsForNewPrime(_stepJustPrimed, _globalTurn);

            _currentPhase = ResolutionPhase.WordsScored;

            return new StepResult
            {
                Phase = ResolutionPhase.WordsScored,
                ScoredWords = scoredEvents,
                TotalScore = _stepTotalScore,
            };
        }

        /// <summary>
        /// Public wrapper so non-standard prime paths (BoardSwap) can trigger
        /// the global fuse reset. Normal BeginDrop / BeginRewrite flows already
        /// hit this through DoScoreAndPrime.
        /// </summary>
        public void ResetExistingPrimedWordsExternal(HashSet<int> justPrimedIds)
        {
            ResetExistingPrimedWordsForNewPrime(justPrimedIds, _globalTurn);
        }

        private void ResetExistingPrimedWordsForNewPrime(HashSet<int> justPrimedIds, int currentTurn)
        {
            // DISABLED 2026-04-18 per Spencer's diagnosis. Global fuse reset removed
            // the "fuse pressure" design goal: a player could indefinitely prime new
            // words to reset ALL existing primes' timers, making setups immortal.
            // Without it, primes now have independent timers from creation — players
            // must commit to detonating rather than infinitely stacking.
            return;

#pragma warning disable CS0162 // Unreachable code (kept for easy revert)
            if (justPrimedIds == null || justPrimedIds.Count == 0 || _primedRegistry == null) return;

            int newFuseLength = 0;
            float newMaxAge = 25f;
            foreach (int newId in justPrimedIds)
            {
                var np = _primedRegistry.GetById(newId);
                if (np == null) continue;
                int fuse = GetFuseLength(np.Word.Length);
                if (fuse > newFuseLength) newFuseLength = fuse;
                if (np.MaxAgeSeconds > newMaxAge) newMaxAge = np.MaxAgeSeconds;
            }
            if (newFuseLength <= 0) return;

            float now = Time.time;
            int resetCount = 0;
            for (int p = 0; p < _primedRegistry.Count; p++)
            {
                var pw = _primedRegistry.GetByIndex(p);
                if (pw == null) continue;
                if (justPrimedIds.Contains(pw.Id)) continue;
                if (pw.GlobalFuseResetCount >= MAX_GLOBAL_FUSE_RESETS) continue;

                pw.CreatedAtTime = now;
                pw.MaxAgeSeconds = newMaxAge;
                pw.ExpiresOnTurn = currentTurn + newFuseLength;
                pw.PrimedOnTurn = currentTurn;
                pw.GlobalFuseResetCount++;

                RefreshPrimedWordTiles(pw, currentTurn, heatOverride: 0);
                resetCount++;
            }

            if (resetCount > 0)
                Debug.Log($"[FuseReset] Total: {resetCount} words reset, {_primedRegistry.Count} total primed");
#pragma warning restore CS0162
        }

        public void RefreshAllPrimedWordTiles(int currentTurn = -1)
        {
            if (_primedRegistry == null) return;
            int turn = currentTurn >= 0 ? currentTurn : _globalTurn;

            // DIAGNOSTIC — kept silent in normal play, uncomment to re-enable.
            // Debug.Log($"[RefreshPrimed] called at turn={turn}, globalTurn={_globalTurn}, registryCount={_primedRegistry.Count}");

            for (int p = 0; p < _primedRegistry.Count; p++)
            {
                var pw = _primedRegistry.GetByIndex(p);
                // Debug.Log($"[RefreshPrimed] pw='{pw.Word}' expiresOnTurn={pw.ExpiresOnTurn} primedOnTurn={pw.PrimedOnTurn} → fuseRemaining={Mathf.Max(0, pw.ExpiresOnTurn - turn)}");
                RefreshPrimedWordTiles(pw, turn);
            }
        }

        private void RefreshPrimedWordTiles(PrimedWordRegistry.PrimedWord pw, int currentTurn, int heatOverride = -1)
        {
            if (GridManager.Instance == null || pw == null || pw.Cells == null) return;

            int survived = Mathf.Max(0, currentTurn - pw.PrimedOnTurn);
            int heatLevel = heatOverride >= 0 ? heatOverride : Mathf.Min(survived, HEAT_FUSE_MAX_BONUS);
            int fuseRemaining = Mathf.Max(0, pw.ExpiresOnTurn - currentTurn);
            Color glowColor = pw.IsGold ? Tile.PRIMED_GOLD_GLOW : Tile.PRIMED_GLOW;

            for (int c = 0; c < pw.Cells.Count; c++)
            {
                Tile tile = GridManager.Instance.GetTile(pw.Cells[c].x, pw.Cells[c].y);
                if (tile != null)
                    tile.SetPrimedGlow(glowColor, playFlash: false, heatLevel: heatLevel, fuseRemaining: fuseRemaining, isGold: pw.IsGold, maxAge: pw.MaxAgeSeconds);
            }
        }

        private StepResult DoCheckTriggers()
        {
            var triggeredIds = new HashSet<int>();
            var triggerEvents = new List<PrimedTriggeredEvent>();

            // Bonus Mode redesign (2026-04-18): auto-detonate removed. Bonus is now
            // "5 free moves" — normal gameplay continues, just without rising-row
            // tick or stage-budget cost. No triangular scoring, no forced ignition.

//             Debug.Log($"[RulesEngine] DoCheckTriggers: {_stepPendingWords.Count} new word(s), " +
                      // $"{_primedRegistry.Count} primed word(s) on board, " +
                      // $"{_stepJustPrimed.Count} just-primed this resolution");

            for (int w = 0; w < _stepPendingWords.Count; w++)
            {
                RulesWordMatch match = _stepPendingWords[w];

                for (int c = 0; c < match.Cells.Count; c++)
                {
                    Vector2Int cell = match.Cells[c];

                    // 1. Direct overlap triggers (original behavior — always active)
                    List<PrimedWordRegistry.PrimedWord> overlapping =
                        _primedRegistry.GetPrimedWordsContaining(cell);

                    for (int p = 0; p < overlapping.Count; p++)
                    {
                        PrimedWordRegistry.PrimedWord pw = overlapping[p];

                        // Phase 9.7: cascade rule — at chainDepth 0 (initial
                        // drop/rewrite) the just-primed filter stays (trigger
                        // word shouldn't self-detonate on its own drop). At
                        // chainDepth >= 1 (gravity-formed words during a
                        // cascade) skip the filter so new words can trigger
                        // prior-step primes AND self-overlap to propagate.
                        //
                        // Phase 11+ extension: Survival now uses the same
                        // cascade rule as Level mode. Spencer's ask —
                        // gravity-formed Survival words should fall, prime,
                        // and explode on their own the way Level-mode cascades
                        // do. Stone-chain propagation stays Level-only
                        // (Phase 9.9): Survival's sparse stones + one-hop
                        // clear is still the intended tuning for that axis.
                        bool skipJustPrimedFilter =
                            (GameManager.IsLevelMode || SurvivalManager.IsSurvivalMode)
                            && _stepChainDepth > 0;
                        if (!skipJustPrimedFilter && _stepJustPrimed.Contains(pw.Id)) continue;
                        if (triggeredIds.Contains(pw.Id)) continue;

                        triggeredIds.Add(pw.Id);
//                         Debug.Log($"[RulesEngine] DoCheckTriggers: '{match.Word}' OVERLAPS primed '{pw.Word}' (id={pw.Id}) at ({cell.x},{cell.y})");

                        triggerEvents.Add(new PrimedTriggeredEvent
                        {
                            TriggeredWord    = pw.Word,
                            TriggeredCells   = new List<Vector2Int>(pw.Cells),
                            TriggerWord      = match.Word,
                            OverlapCell      = cell,
                            OwnerPlayerIndex = pw.OwnerPlayer,
                            PrimedWordId     = pw.Id,
                        });
                        OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                    }

                    // 2. Adjacency triggers (new — behind kill switch)
                    // A new word TOUCHING a primed word (orthogonal neighbor) triggers it.
                    // This makes buried primes reachable and improves flow.
                    if (AdjacencyTriggerEnabled)
                    {
                        List<PrimedWordRegistry.PrimedWord> adjacent =
                            _primedRegistry.GetPrimedWordsAdjacentTo(cell);

                        for (int p = 0; p < adjacent.Count; p++)
                        {
                            PrimedWordRegistry.PrimedWord pw = adjacent[p];

                            if (_stepJustPrimed.Contains(pw.Id)) continue;
                            if (triggeredIds.Contains(pw.Id)) continue;

                            triggeredIds.Add(pw.Id);
//                             Debug.Log($"[RulesEngine] DoCheckTriggers: '{match.Word}' ADJACENT to primed '{pw.Word}' (id={pw.Id}) at ({cell.x},{cell.y})");

                            triggerEvents.Add(new PrimedTriggeredEvent
                            {
                                TriggeredWord    = pw.Word,
                                TriggeredCells   = new List<Vector2Int>(pw.Cells),
                                TriggerWord      = match.Word,
                                OverlapCell      = cell,
                                OwnerPlayerIndex = pw.OwnerPlayer,
                                PrimedWordId     = pw.Id,
                            });
                            OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                        }
                    }
                }
            }

            // ── Connected chain expansion ──
            // If any primed words were directly triggered, find all connected primed words
            // (transitively via shared tiles) and include them in the detonation group.
            int chainTriggeredCount = 0;
            if (triggeredIds.Count > 0)
            {
                var connectedGroup = _primedRegistry.FindConnectedGroup(triggeredIds, _stepJustPrimed);
                for (int g = 0; g < connectedGroup.Count; g++)
                {
                    var pw = connectedGroup[g];
                    if (triggeredIds.Contains(pw.Id)) continue; // already a direct trigger

                    triggeredIds.Add(pw.Id);
                    chainTriggeredCount++;

//                     Debug.Log($"[PrimedChain] Chain-connected: '{pw.Word}' (id={pw.Id}) added to detonation group");

                    triggerEvents.Add(new PrimedTriggeredEvent
                    {
                        TriggeredWord    = pw.Word,
                        TriggeredCells   = new List<Vector2Int>(pw.Cells),
                        TriggerWord      = "chain",
                        OverlapCell      = pw.Cells[0],
                        OwnerPlayerIndex = pw.OwnerPlayer,
                        PrimedWordId     = pw.Id,
                        IsChainTrigger   = true,
                    });

                    OnPrimedTriggered?.Invoke(triggerEvents[triggerEvents.Count - 1]);
                }

                if (chainTriggeredCount > 0)
                {
//                     Debug.Log($"[PrimedChain] ConnectedGroup size={triggeredIds.Count} " +
                              // $"(direct={triggeredIds.Count - chainTriggeredCount}, chain={chainTriggeredCount}) " +
                              // $"words=[{string.Join(", ", triggerEvents.ConvertAll(e => e.TriggeredWord))}]");
                }
            }

            if (triggeredIds.Count == 0)
            {
                // No triggers — resolution done for this chain
                RemoveInvalidPrimedWords();
                _currentPhase = ResolutionPhase.Complete;

//                 Debug.Log($"[RulesEngine] DoCheckTriggers: no triggers — resolution complete.");

                return new StepResult
                {
                    Phase = ResolutionPhase.Complete,
                    TotalScore = _stepTotalScore,
                };
            }

            _stepPendingTriggers = new List<int>(triggeredIds);
            _stepChainTriggeredCount = chainTriggeredCount;
            _stepTriggerWords = _stepPendingWords != null ? new List<RulesWordMatch>(_stepPendingWords) : null;
            _currentPhase = ResolutionPhase.TriggersFound;

            // Compute longest primed word NOW so BigBurstFlash's big-moment gate
            // can evaluate it at TriggersFound. Without this, LongestWordLength
            // defaults to 0 here (only Exploding populates it), causing the gate
            // to silently fail on word-length qualification.
            int longestInCluster = 0;
            foreach (int pid in triggeredIds)
            {
                var pw = _primedRegistry.GetById(pid);
                if (pw != null && pw.Word != null && pw.Word.Length > longestInCluster)
                    longestInCluster = pw.Word.Length;
            }

            return new StepResult
            {
                Phase = ResolutionPhase.TriggersFound,
                Triggers = triggerEvents,
                TotalScore = _stepTotalScore,
                ChainTriggeredCount = chainTriggeredCount,
                // Chain depth at trigger time — drives BigBurstFlash's big-moment gate.
                // Exploding's own _stepChainDepth increments PER CLUSTER WORD; we need
                // the iteration-level depth here, which is what ProcessDrop's chainStep
                // tracks. _stepChainDepth at this point reflects the chain iteration.
                ChainDepth = _stepChainDepth,
                LongestWordLength = longestInCluster,
                // The words the player/chain just formed that caused the trigger.
                // HandManager blasts each one separately so the "your word" gets a
                // flash too, not only the primed words it ignited.
                TriggerWords = _stepTriggerWords != null ? new List<RulesWordMatch>(_stepTriggerWords) : null,
            };
        }

        private StepResult DoExplode()
        {
            var allExplodedCells = new List<Vector2Int>();
            int detonationBonus = 0;
            int totalHeat = 0;
            int swapRefillCount = 0;
            int editRefillCount = 0;
            int wildRefillCount = 0;
            int longestPrimedWord = 0; // track for splash scaling
            // Cluster-size boost in DoExplode removed 2026-04-18 — combined with
            // DoScoreAndPrime's cluster boost + raised cap=8, it made mega-combos
            // the only viable path. DoScoreAndPrime cluster boost alone is enough.

            // Snapshot splash sources BEFORE the loop destroys cells. Include both
            // detonating primed words and the trigger words that ignited them so
            // each word can contribute its own row/column sweep axis.
            var primedSplashSources = new List<List<Vector2Int>>();
            if (_stepPendingTriggers != null && _primedRegistry != null)
            {
                foreach (int pid in _stepPendingTriggers)
                {
                    var snapshotPw = _primedRegistry.GetById(pid);
                    if (snapshotPw?.Cells != null && snapshotPw.Cells.Count > 0)
                        primedSplashSources.Add(new List<Vector2Int>(snapshotPw.Cells));
                }
            }
            if (_stepTriggerWords != null)
            {
                foreach (var trigWord in _stepTriggerWords)
                    if (trigWord?.Cells != null && trigWord.Cells.Count > 0)
                        primedSplashSources.Add(new List<Vector2Int>(trigWord.Cells));
            }

            for (int i = 0; i < _stepPendingTriggers.Count; i++)
            {
                int pid = _stepPendingTriggers[i];
                PrimedWordRegistry.PrimedWord pw = _primedRegistry.GetById(pid);
                if (pw == null) continue;

                if (pw.Word.Length > longestPrimedWord)
                    longestPrimedWord = pw.Word.Length;

                for (int c = 0; c < pw.Cells.Count; c++)
                {
                    Vector2Int cell = pw.Cells[c];
                    if (InBounds(cell.x, cell.y) && _board[cell.x, cell.y] != null)
                    {
                        // Check for refill tiles before destroying
                        var cellData = _board[cell.x, cell.y];
                        if (cellData.IsSwapRefill) swapRefillCount++;
                        if (cellData.IsEditRefill) editRefillCount++;
                        if (cellData.IsWildRefill) wildRefillCount++;

                        _board[cell.x, cell.y] = null;
                        _bonusCells[cell.x, cell.y] = false; // gold consumed with tile
                        _cyanCells[cell.x, cell.y] = false;  // cyan consumed with tile
                        allExplodedCells.Add(cell);
                    }
                }

                int survivedTurns = Mathf.Max(0, _globalTurn - pw.PrimedOnTurn);
                int heatBonus = Mathf.Min(survivedTurns * HEAT_FUSE_PER_TURN, HEAT_FUSE_MAX_BONUS);
                int rawBonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS + heatBonus;

                // Chain-depth scaling: each word in a cluster escalates the chain.
                // Evaluate at depth+1 so the OPENING boom is 2x, not a flat 1x.
                // First detonation = 2x, second = 4x, third = 7x (capped by CHAIN_DEPTH_SCALE_CAP=3).
                float chainMultiplier = TriangularChainMultiplier(_stepChainDepth + 1);
                float goldMultiplier = pw.IsGold ? 2f : 1f;
                int bonus = Mathf.RoundToInt(rawBonus * chainMultiplier * goldMultiplier);

                detonationBonus += bonus;
                totalHeat += heatBonus;

                // Fire per-detonation event with FINAL bonus (post-multiplier) so
                // downstream systems (ChainMeter, etc.) see actual scoring impact
                // instead of raw pw.Score — which undercounts big-cluster detonations
                // by the full triangular + cluster multiplier.
                OnDetonationScored?.Invoke(bonus);

//                 Debug.Log($"[RulesEngine] DoExplode: '{pw.Word}' (id={pid}) exploded, +{bonus} pts " +
                          // $"(rescore={Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER)} base={BREAKER_BONUS} heat={heatBonus} chain={_stepChainDepth} x{chainMultiplier:F1})");

                _primedRegistry.RemovePrimedWord(pid);
                _stepJustPrimed.Remove(pid);

                // Escalate chain depth for each word in the cluster.
                // Connected cluster = cascading chain, not simultaneous pop.
                _stepChainDepth++;
            }

            _stepTotalScore += detonationBonus;

            // Phase 9.7: trigger-word tiles clear in Survival AND Level mode.
            // Survival uses this to free more cells per detonation (board-survival mechanic).
            // Level mode uses it so the player's drop-formed trigger word doesn't occupy
            // cells that gravity needs to fill with falling letters forming cascade words.
            // The big-moment row/column splash below stays Survival-only via its own gate.
            if (SurvivalManager.IsSurvivalMode || GameManager.IsLevelMode)
            {
                var alreadyExploded = new HashSet<Vector2Int>(allExplodedCells);
                if (_stepTriggerWords != null)
                {
                    for (int tw = 0; tw < _stepTriggerWords.Count; tw++)
                    {
                        var trigWord = _stepTriggerWords[tw];
                        if (trigWord.Cells == null) continue;
                        for (int tc = 0; tc < trigWord.Cells.Count; tc++)
                        {
                            var cell = trigWord.Cells[tc];
                            if (alreadyExploded.Contains(cell)) continue;
                            if (InBounds(cell.x, cell.y) && _board[cell.x, cell.y] != null)
                            {
                                _board[cell.x, cell.y] = null;
                                _bonusCells[cell.x, cell.y] = false;
                                _cyanCells[cell.x, cell.y] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                    }

                    // Remove any primed words that used the trigger word cells
                    if (_primedRegistry != null)
                    {
                        for (int tw = 0; tw < _stepTriggerWords.Count; tw++)
                        {
                            var trigWord = _stepTriggerWords[tw];
                            if (trigWord.Cells == null) continue;
                            for (int tc = 0; tc < trigWord.Cells.Count; tc++)
                            {
                                var primedAt = _primedRegistry.GetPrimedWordsContaining(trigWord.Cells[tc]);
                                if (primedAt != null)
                                    for (int p = primedAt.Count - 1; p >= 0; p--)
                                        _primedRegistry.RemovePrimedWord(primedAt[p].Id);
                            }
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // BIG-MOMENT ROW/COLUMN SPLASH — ties the BigBurstFlash visual to
                // actual gameplay impact. Horizontal detonated word → clear its full
                // row. Vertical detonated word → clear its full column. Splash tiles score at
                // base letter value × gold mult (no chain mult — they're collateral).
                // Any primed word caught in the splash detonates as chain
                // continuation. Gated to match BigBurstFlash: chain depth ≥ 2 OR
                // longest primed word ≥ 6 OR 2+ primed cluster.
                // ═══════════════════════════════════════════════════════════════
                // Phase 9.7: big-moment splash stays Survival-only even though the
                // outer trigger-word-clear block now runs in Level mode too.
                bool splashGate = SurvivalManager.IsSurvivalMode
                    && !_splashFiredThisResolution
                    && ((_stepChainDepth >= 2)
                        || (longestPrimedWord >= 6)
                        || (_stepPendingTriggers != null && _stepPendingTriggers.Count >= 2));

                if (splashGate)
                {
                    _splashFiredThisResolution = true; // ONE splash per drop — prevents feedback loop
                    int splashBaseBonus = 0;
                    var splashPrimedIds = new HashSet<int>();

                    // Splash sources were snapshotted before the main detonation loop
                    // destroyed cells. Each source word's orientation determines its
                    // splash axis.
                    var splashSources = primedSplashSources;
                    // Fallback to trigger words if no primed info (defensive — should
                    // be non-empty since splashGate requires at least one trigger).
                    if (splashSources.Count == 0)
                    {
                        splashSources = new List<List<Vector2Int>>();
                        if (_stepTriggerWords != null)
                        {
                            foreach (var trigWord in _stepTriggerWords)
                                if (trigWord?.Cells != null && trigWord.Cells.Count > 0)
                                    splashSources.Add(trigWord.Cells);
                        }
                    }

                    foreach (var cells in splashSources)
                    {
                        if (cells == null || cells.Count == 0) continue;

                        // Detect orientation — same logic as BigBurstFlash uses so
                        // the splash axis matches the sweep axis exactly.
                        int minCol = int.MaxValue, maxCol = int.MinValue;
                        int minRow = int.MaxValue, maxRow = int.MinValue;
                        foreach (var c in cells)
                        {
                            if (c.x < minCol) minCol = c.x;
                            if (c.x > maxCol) maxCol = c.x;
                            if (c.y < minRow) minRow = c.y;
                            if (c.y > maxRow) maxRow = c.y;
                        }
                        bool vertical = (maxCol - minCol) == 0 && (maxRow - minRow) > 0;

                        if (vertical)
                        {
                            int col = minCol;
                            for (int row = 0; row < ROWS; row++)
                            {
                                var cell = new Vector2Int(col, row);
                                if (alreadyExploded.Contains(cell)) continue;
                                if (!InBounds(col, row) || _board[col, row] == null) continue;

                                var data = _board[col, row];
                                int letterPts = LetterData.GetPoints(data.Letter);
                                int goldMult  = _bonusCells[col, row] ? 2 : 1;
                                splashBaseBonus += letterPts * goldMult;

                                // Gather any primed words this cell belongs to —
                                // they'll chain-detonate after the splash pass.
                                if (_primedRegistry != null)
                                {
                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var pw in primedHere) splashPrimedIds.Add(pw.Id);
                                }

                                _board[col, row] = null;
                                _bonusCells[col, row] = false;
                                _cyanCells[col, row] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                        else
                        {
                            int row = minRow;
                            for (int col = 0; col < COLS; col++)
                            {
                                var cell = new Vector2Int(col, row);
                                if (alreadyExploded.Contains(cell)) continue;
                                if (!InBounds(col, row) || _board[col, row] == null) continue;

                                var data = _board[col, row];
                                int letterPts = LetterData.GetPoints(data.Letter);
                                int goldMult  = _bonusCells[col, row] ? 2 : 1;
                                splashBaseBonus += letterPts * goldMult;

                                if (_primedRegistry != null)
                                {
                                    var primedHere = _primedRegistry.GetPrimedWordsContaining(cell);
                                    if (primedHere != null)
                                        foreach (var pw in primedHere) splashPrimedIds.Add(pw.Id);
                                }

                                _board[col, row] = null;
                                _bonusCells[col, row] = false;
                                _cyanCells[col, row] = false;
                                allExplodedCells.Add(cell);
                                alreadyExploded.Add(cell);
                            }
                        }
                    }

                    // Chain-detonate any primed words the splash hit. Score each
                    // at their stored pw.Score × DETONATION_SCORE_MULTIPLIER + BREAKER_BONUS
                    // (no chain mult — splash is collateral, not a chain tier).
                    int splashPrimedBonus = 0;
                    if (_primedRegistry != null)
                    {
                        foreach (int pid in splashPrimedIds)
                        {
                            var pw = _primedRegistry.GetById(pid);
                            if (pw == null) continue;

                            // Sweep remaining cells of this primed word (splash may
                            // have cleared some already).
                            if (pw.Cells != null)
                            {
                                foreach (var cell in pw.Cells)
                                {
                                    if (alreadyExploded.Contains(cell)) continue;
                                    if (!InBounds(cell.x, cell.y) || _board[cell.x, cell.y] == null) continue;
                                    _board[cell.x, cell.y] = null;
                                    _bonusCells[cell.x, cell.y] = false;
                                    _cyanCells[cell.x, cell.y] = false;
                                    allExplodedCells.Add(cell);
                                    alreadyExploded.Add(cell);
                                }
                            }

                            int pwBonus = Mathf.RoundToInt(pw.Score * DETONATION_SCORE_MULTIPLIER) + BREAKER_BONUS;
                            splashPrimedBonus += pwBonus;
                            _primedRegistry.RemovePrimedWord(pid);
                        }
                    }

                    _stepTotalScore += splashBaseBonus + splashPrimedBonus;
//                     Debug.Log($"[DoExplode] Splash: +{splashBaseBonus} base + {splashPrimedBonus} primed detonation = {splashBaseBonus + splashPrimedBonus}");
                }

                _stepTriggerWords = null;
//                 Debug.Log($"[RulesEngine] DoExplode: Survival trigger-word clear — total exploded cells: {allExplodedCells.Count}");
            }

            // NOTE: _stepPendingTriggers nulled AFTER splash/row-clear logic below
            // (7-letter row clear at line ~3619 reads it).
            _currentPhase = ResolutionPhase.Exploding;

            // Stone splash: destroy stone tiles adjacent to any exploded cell.
            // Phase 9.9 (Level mode only): chain-propagate so a newly-cleared stone
            // becomes a splash source for the next pass. Enables stone-column
            // cascade catalysts where a single detonation rips through a stone
            // run, opening the column for gravity-fed follow-ups. Survival path
            // stays single-pass — its stones are sparse (max 2 per row) and the
            // existing tuning depends on the one-hop limit.
            int stoneClearCount = 0;
            var stoneCleared = new List<Vector2Int>();
            var checkedStones = new HashSet<Vector2Int>();

            // Seed the first pass from cells already in allExplodedCells
            var currentSources = new List<Vector2Int>(allExplodedCells.Count);
            for (int i = 0; i < allExplodedCells.Count; i++)
                currentSources.Add(allExplodedCells[i]);

            bool stoneChainPropagate = GameManager.IsLevelMode;
            const int STONE_CHAIN_SAFETY_CAP = 50;
            int stoneChainIterations = 0;
            int[] sdx = { 1, -1, 0, 0 };
            int[] sdy = { 0, 0, 1, -1 };

            while (currentSources.Count > 0 && stoneChainIterations < STONE_CHAIN_SAFETY_CAP)
            {
                stoneChainIterations++;
                var nextSources = new List<Vector2Int>();

                for (int ec = 0; ec < currentSources.Count; ec++)
                {
                    var cell = currentSources[ec];
                    for (int d = 0; d < 4; d++)
                    {
                        int sx = cell.x + sdx[d];
                        int sy = cell.y + sdy[d];
                        if (!InBounds(sx, sy)) continue;
                        var stonePos = new Vector2Int(sx, sy);
                        if (checkedStones.Contains(stonePos)) continue;
                        checkedStones.Add(stonePos);
                        var adj = _board[sx, sy];
                        if (adj != null && adj.IsStone)
                        {
                            _board[sx, sy] = null;
                            stoneCleared.Add(stonePos);
                            stoneClearCount++;
                            nextSources.Add(stonePos);
                        }
                    }
                }

                // Survival: one pass only. Level mode: chain until no new stones clear.
                if (!stoneChainPropagate) break;
                currentSources = nextSources;
            }

            if (stoneChainIterations >= STONE_CHAIN_SAFETY_CAP)
                Debug.LogWarning($"[RulesEngine] Stone chain-clear hit safety cap ({STONE_CHAIN_SAFETY_CAP}) — terminating chain.");

            // Now safe to add stone positions to the exploded list
            allExplodedCells.AddRange(stoneCleared);
            // if (stoneClearCount > 0)
//                 Debug.Log($"[RulesEngine] DoExplode: cleared {stoneClearCount} stone tile(s) via splash damage");

            // ── Junk splash: scales with PRIMED word length ──
            // 3-letter: 0 splash (tactical glue, not artillery)
            // 4-letter: 1 splash
            // 5-letter: 2 splash
            // 6-letter: 3 splash
            // 7-letter: 4 splash + full row clear
            if (JunkSplashEnabled && SurvivalManager.IsSurvivalMode)
            {
                int maxSplash = longestPrimedWord <= 3 ? 0
                              : longestPrimedWord == 4 ? 1
                              : longestPrimedWord == 5 ? 2
                              : longestPrimedWord == 6 ? 3
                              : 4; // 7+
                int junkCleared = 0;
                var junkCandidates = new List<Vector2Int>();
                var alreadyExplodedSet = new HashSet<Vector2Int>(allExplodedCells);

                // Collect all junk neighbors of exploded cells
                int snapCount = allExplodedCells.Count;
                for (int ec = 0; ec < snapCount; ec++)
                {
                    var cell = allExplodedCells[ec];
                    int[] jdx = { 1, -1, 0, 0 };
                    int[] jdy = { 0, 0, 1, -1 };
                    for (int d = 0; d < 4; d++)
                    {
                        int jx = cell.x + jdx[d];
                        int jy = cell.y + jdy[d];
                        if (!InBounds(jx, jy)) continue;
                        var jPos = new Vector2Int(jx, jy);
                        if (alreadyExplodedSet.Contains(jPos)) continue;
                        var jCell = _board[jx, jy];
                        if (jCell == null) continue;
                        if (jCell.IsStone) continue; // stones handled separately
                        if (jCell.IsSwapRefill || jCell.IsEditRefill || jCell.IsWildRefill) continue; // protect specials
                        if (_bonusCells[jx, jy]) continue; // protect gold

                        // Check if this tile is part of a primed word — protect it
                        var primedAt = _primedRegistry.GetPrimedWordsContaining(jPos);
                        if (primedAt != null && primedAt.Count > 0) continue;

                        if (!junkCandidates.Contains(jPos))
                            junkCandidates.Add(jPos);
                    }
                }

                // Shuffle and pick up to maxSplash
                for (int i = junkCandidates.Count - 1; i > 0; i--)
                {
                    int j = Random.Range(0, i + 1);
                    var tmp = junkCandidates[i]; junkCandidates[i] = junkCandidates[j]; junkCandidates[j] = tmp;
                }

                for (int i = 0; i < junkCandidates.Count && junkCleared < maxSplash; i++)
                {
                    var jPos = junkCandidates[i];
                    _board[jPos.x, jPos.y] = null;
                    _bonusCells[jPos.x, jPos.y] = false;
                    _cyanCells[jPos.x, jPos.y] = false;
                    allExplodedCells.Add(jPos);
                    alreadyExplodedSet.Add(jPos);
                    junkCleared++;
                }

                // if (junkCleared > 0)
//                     Debug.Log($"[RulesEngine] DoExplode: junk splash cleared {junkCleared} tile(s) (wordLen={longestPrimedWord})");
            }

            // ── 7-letter jackpot: clear the entire row of the longest primed word ──
            if (longestPrimedWord >= 7 && SurvivalManager.IsSurvivalMode)
            {
                // Find which row the longest word was on (use first exploded cell from that word)
                int clearRow = -1;
                for (int i = 0; i < _stepPendingTriggers.Count && clearRow < 0; i++)
                {
                    var pw = _primedRegistry.GetById(_stepPendingTriggers[i]);
                    if (pw != null && pw.Word.Length >= 7 && pw.Cells.Count > 0)
                        clearRow = pw.Cells[0].y;
                }
                // Note: primed words already removed from registry above, so GetById may return null
                // Use the allExplodedCells to find the row instead
                if (clearRow < 0 && allExplodedCells.Count > 0)
                    clearRow = allExplodedCells[0].y;

                if (clearRow >= 0)
                {
                    var alreadyExploded = new HashSet<Vector2Int>(allExplodedCells);
                    int rowCleared = 0;
                    for (int col = 0; col < COLS; col++)
                    {
                        var pos = new Vector2Int(col, clearRow);
                        if (alreadyExploded.Contains(pos)) continue;
                        if (_board[col, clearRow] != null)
                        {
                            _board[col, clearRow] = null;
                            _bonusCells[col, clearRow] = false;
                            _cyanCells[col, clearRow] = false;
                            allExplodedCells.Add(pos);
                            rowCleared++;
                        }
                    }
                    // if (rowCleared > 0)
//                         Debug.Log($"[RulesEngine] DoExplode: 7-LETTER JACKPOT — cleared row {clearRow} ({rowCleared} extra tiles)");
                }
            }

            _stepPendingTriggers = null; // safe to null now — row-clear logic above is done

            // Purge scored word keys AFTER all splash damage finalized
            PurgeScoredKeysForCells(allExplodedCells);
            PurgeStepScoredKeysForCells(allExplodedCells);

            // if (swapRefillCount > 0 || editRefillCount > 0 || wildRefillCount > 0)
//                 Debug.Log($"[RulesEngine] DoExplode: refills collected — swap={swapRefillCount} edit={editRefillCount} wild={wildRefillCount}");

            return new StepResult
            {
                Phase = ResolutionPhase.Exploding,
                ExplodedCells = allExplodedCells,
                TotalScore = _stepTotalScore,
                DetonationBonus = detonationBonus,
                DetonationHeat = totalHeat,
                ChainDepth = _stepChainDepth,
                ChainTriggeredCount = _stepChainTriggeredCount,
                SwapRefillCount = swapRefillCount,
                EditRefillCount = editRefillCount,
                WildRefillCount = wildRefillCount,
                LongestWordLength = longestPrimedWord,
            };
        }

        private StepResult DoGravity()
        {
            var gravityMoves = ApplyGravityInData();

            if (gravityMoves.Count > 0)
            {
                _primedRegistry.UpdateCellPositions(gravityMoves);

                // Purge scored keys for all cells involved in gravity movement
                // (both old and new positions) so words at new positions can be
                // re-evaluated as fresh words in the chain loop.
                var affectedCells = new List<Vector2Int>(gravityMoves.Count * 2);
                foreach (var kvp in gravityMoves)
                {
                    affectedCells.Add(kvp.Key);   // old position (tile left here)
                    affectedCells.Add(kvp.Value); // new position (tile arrived here)
                }
                PurgeScoredKeysForCells(affectedCells);
                PurgeStepScoredKeysForCells(affectedCells);

                // Seed ALL occupied cells in columns that had gravity movement.
                // Not just the tiles that moved — tiles already below them can
                // now form new words with the landed tiles (e.g. AMP where A and M
                // were already in place but P fell into position).
                var movedCols = new HashSet<int>();
                foreach (var kvp in gravityMoves)
                    movedCols.Add(kvp.Value.x);

                _stepSeedCells = new List<Vector2Int>();
                foreach (int col in movedCols)
                {
                    for (int row = 0; row < ROWS; row++)
                    {
                        if (_board[col, row] != null)
                            _stepSeedCells.Add(new Vector2Int(col, row));
                    }
                }

//                 Debug.Log($"[RulesEngine] DoGravity: {gravityMoves.Count} tile(s) moved.");
            }

            RemoveInvalidPrimedWords();

            // ── Post-gravity fertility repair — DISABLED ──────────────────────
            // This was mutating board tiles mid-chain without updating visuals,
            // causing phantom "words" that aren't real words on screen.
            // Replaced by PostClearBoost in SurvivalManager which biases future
            // draws instead of mutating existing tiles.
            // if (PostGravityFertilityEnabled && SurvivalManager.IsSurvivalMode && gravityMoves.Count > 0)
            // {
            //     int nearWords = CountNearWords();
            //     if (nearWords < 2)
            //     {
            //         int nudged = NudgeTilesForFertility(2 - nearWords);
            //         if (nudged > 0)
            //             Debug.Log($"[RulesEngine] PostGravityFertility: nudged {nudged} tile(s) (nearWords was {nearWords})");
            //     }
            // }

            _stepChainDepth++;
            _currentPhase = ResolutionPhase.GravityApplied;

//             Debug.Log($"[RulesEngine] DoGravity: chainDepth now {_stepChainDepth}, looping back to detect.");

            return new StepResult
            {
                Phase = ResolutionPhase.GravityApplied,
                GravityMoves = gravityMoves,
                TotalScore = _stepTotalScore,
                ChainContinues = true,
            };
        }
    }

    // ── Supporting types ──────────────────────────────────────────────────────────

    public class RulesCellData
    {
        public char Letter        { get; set; }
        public int  Col           { get; set; }
        public int  Row           { get; set; }
        public int  PlayerIndex   { get; set; }
        public bool IsWild        { get; set; }
        public bool IsCyan        { get; set; }
        public bool IsSwapRefill  { get; set; }
        public bool IsEditRefill  { get; set; }
        public bool IsWildRefill  { get; set; }
        public bool IsStone       { get; set; } // grey junk tile — can't be used in words, cleared by adjacent detonation
    }

    public enum WordDirection
    {
        Horizontal = 0,
        Vertical   = 1,
    }

    public class RulesWordMatch
    {
        public string           Word      { get; set; }
        public List<Vector2Int> Cells     { get; set; }
        public WordDirection    Direction { get; set; }
        public int              Score     { get; set; }

        // Positions within Word (0..Word.Length-1) that were resolved from a wild
        // tile during scanning. These positions score 0. Null/empty when no wilds.
        public List<int>        WildLetterIndices { get; set; }

        public string CellKey
        {
            get
            {
                if (Cells == null || Cells.Count == 0) return "";
                var sorted = new List<Vector2Int>(Cells);
                sorted.Sort((a, b) =>
                {
                    int cmp = a.x.CompareTo(b.x);
                    return cmp != 0 ? cmp : a.y.CompareTo(b.y);
                });
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < sorted.Count; i++)
                {
                    if (i > 0) sb.Append(';');
                    sb.Append(sorted[i].x).Append(',').Append(sorted[i].y);
                }
                return sb.ToString();
            }
        }
    }
}
