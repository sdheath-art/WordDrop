using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Records detonation chain data during gameplay for the kill-cam replay.
    /// Lightweight data-only singleton — no GameObjects, no visuals.
    ///
    /// Usage:
    ///   - Call SnapshotBoard() before each resolution begins
    ///   - Call RecordStep() for each StepResult during resolution
    ///   - Call FinalizeChain() when a resolution completes (if it had detonations)
    ///   - Call Reset() at match start
    ///   - Call GetBestChain() on game over to retrieve replay data
    /// </summary>
    public class DetonationRecorder : MonoBehaviour
    {
        public static DetonationRecorder Instance { get; private set; }

        // ── Recorded data types ─────────────────────────────────────────────────

        /// <summary>
        /// Snapshot of a single cell on the board: letter + owner.
        /// Null means empty cell.
        /// </summary>
        public class CellSnapshot
        {
            public char Letter;
            public int PlayerIndex; // 0 = player, 1 = AI
            public bool IsPrimed;
            public int HeatLevel;
        }

        /// <summary>
        /// One step in a recorded detonation chain.
        /// </summary>
        public class RecordedStep
        {
            public RulesEngine.ResolutionPhase Phase;
            public List<WordScoredEvent> ScoredWords;
            public List<PrimedTriggeredEvent> Triggers;
            public List<Vector2Int> ExplodedCells;
            public Dictionary<Vector2Int, Vector2Int> GravityMoves;
            public int ChainDepth;
            public int DetonationBonus;
            public int ChainTriggeredCount;
        }

        /// <summary>
        /// A complete recorded detonation chain, ready for replay.
        /// </summary>
        public class RecordedChain
        {
            /// <summary>Board state before the chain started (7x5 grid).</summary>
            public CellSnapshot[,] BoardSnapshot;

            /// <summary>The tile that was dropped to trigger this chain.</summary>
            public char DroppedLetter;
            public int DroppedCol;
            public int DroppedRow;

            /// <summary>All recorded steps that had detonation activity.</summary>
            public List<RecordedStep> Steps = new List<RecordedStep>();

            /// <summary>Total detonation bonus earned in this chain.</summary>
            public int TotalDetonationBonus;

            /// <summary>Maximum chain depth reached.</summary>
            public int MaxChainDepth;

            /// <summary>Total number of primed words triggered.</summary>
            public int TotalTriggeredCount;
        }

        // ── State ───────────────────────────────────────────────────────────────

        private RecordedChain _bestChain;
        private RecordedChain _currentChain;
        private CellSnapshot[,] _pendingSnapshot;
        private bool _currentChainHasDetonations;

        // ── Unity lifecycle ─────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[DetonationRecorder] Awake");
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Call at match start to clear all recorded data.
        /// </summary>
        public void Reset()
        {
            _bestChain = null;
            _currentChain = null;
            _pendingSnapshot = null;
            _currentChainHasDetonations = false;
            Debug.Log("[DetonationRecorder] Reset");
        }

        /// <summary>
        /// Captures the current board state before a resolution begins.
        /// Call this right before BeginDrop / BeginRewrite.
        /// </summary>
        public void SnapshotBoard()
        {
            RulesEngine rules = RulesEngine.Instance;
            if (rules == null) return;

            _pendingSnapshot = new CellSnapshot[RulesEngine.COLS, RulesEngine.ROWS];
            PrimedWordRegistry registry = rules.PrimedRegistry;
            int currentTurn = rules.GlobalTurn;

            for (int col = 0; col < RulesEngine.COLS; col++)
            {
                for (int row = 0; row < RulesEngine.ROWS; row++)
                {
                    var cell = rules.GetCell(col, row);
                    if (cell != null)
                    {
                        // Check if this cell is part of a primed word
                        bool isPrimed = false;
                        int heatLevel = 0;
                        if (registry != null)
                        {
                            var primedWords = registry.GetPrimedWordsContaining(new Vector2Int(col, row));
                            if (primedWords != null && primedWords.Count > 0)
                            {
                                isPrimed = true;
                                int survived = Mathf.Max(0, currentTurn - primedWords[0].PrimedOnTurn);
                                heatLevel = Mathf.Min(survived, RulesEngine.HEAT_FUSE_MAX_BONUS);
                            }
                        }

                        _pendingSnapshot[col, row] = new CellSnapshot
                        {
                            Letter = cell.Letter,
                            PlayerIndex = cell.PlayerIndex,
                            IsPrimed = isPrimed,
                            HeatLevel = heatLevel
                        };
                    }
                }
            }

            // Start a new chain recording
            _currentChain = new RecordedChain
            {
                BoardSnapshot = _pendingSnapshot
            };
            _currentChainHasDetonations = false;
        }

        /// <summary>
        /// Records which tile was dropped to start this resolution.
        /// Call right after BeginDrop with the drop details.
        /// </summary>
        public void RecordDrop(char letter, int col, int row)
        {
            if (_currentChain == null) return;
            _currentChain.DroppedLetter = letter;
            _currentChain.DroppedCol = col;
            _currentChain.DroppedRow = row;
        }

        /// <summary>
        /// Records a single resolution step. Call for every NextStep() result.
        /// Only steps with detonation activity are stored.
        /// </summary>
        public void RecordStep(RulesEngine.StepResult step)
        {
            if (_currentChain == null || step == null) return;

            // We only care about steps that involve detonation activity
            bool hasDetonation = false;

            switch (step.Phase)
            {
                case RulesEngine.ResolutionPhase.TriggersFound:
                    if (step.Triggers != null && step.Triggers.Count > 0)
                        hasDetonation = true;
                    break;

                case RulesEngine.ResolutionPhase.Exploding:
                    if (step.ExplodedCells != null && step.ExplodedCells.Count > 0)
                        hasDetonation = true;
                    break;

                case RulesEngine.ResolutionPhase.GravityApplied:
                    // Record gravity if we've had detonations (needed for replay)
                    if (_currentChainHasDetonations && step.GravityMoves != null && step.GravityMoves.Count > 0)
                        hasDetonation = true;
                    break;

                case RulesEngine.ResolutionPhase.WordsScored:
                    // Record scored words if they will lead to detonation
                    // (we record all of them; they're lightweight)
                    if (step.ScoredWords != null && step.ScoredWords.Count > 0)
                    {
                        var recorded = new RecordedStep
                        {
                            Phase = step.Phase,
                            ScoredWords = step.ScoredWords != null
                                ? new List<WordScoredEvent>(step.ScoredWords) : null,
                            ChainDepth = step.ChainDepth
                        };
                        _currentChain.Steps.Add(recorded);
                    }
                    return;
            }

            if (!hasDetonation) return;

            _currentChainHasDetonations = true;

            var recordedStep = new RecordedStep
            {
                Phase = step.Phase,
                ScoredWords = step.ScoredWords != null
                    ? new List<WordScoredEvent>(step.ScoredWords) : null,
                Triggers = step.Triggers != null
                    ? new List<PrimedTriggeredEvent>(step.Triggers) : null,
                ExplodedCells = step.ExplodedCells != null
                    ? new List<Vector2Int>(step.ExplodedCells) : null,
                GravityMoves = step.GravityMoves != null
                    ? new Dictionary<Vector2Int, Vector2Int>(step.GravityMoves) : null,
                ChainDepth = step.ChainDepth,
                DetonationBonus = step.DetonationBonus,
                ChainTriggeredCount = step.ChainTriggeredCount
            };

            _currentChain.Steps.Add(recordedStep);

            // Track running totals
            _currentChain.TotalDetonationBonus += step.DetonationBonus;
            _currentChain.TotalTriggeredCount += step.ChainTriggeredCount;
            if (step.ChainDepth > _currentChain.MaxChainDepth)
                _currentChain.MaxChainDepth = step.ChainDepth;
        }

        /// <summary>
        /// Call when a resolution completes. Compares to best and keeps the winner.
        /// </summary>
        public void FinalizeChain()
        {
            if (_currentChain == null || !_currentChainHasDetonations)
            {
                _currentChain = null;
                return;
            }

            // Remove WordsScored steps that weren't followed by detonation
            // (they were recorded speculatively)
            PruneNonDetonationWords();

            bool isBetter = false;
            if (_bestChain == null)
            {
                isBetter = true;
            }
            else
            {
                // Prefer deeper chains, then higher bonus, then more triggers
                if (_currentChain.MaxChainDepth > _bestChain.MaxChainDepth)
                    isBetter = true;
                else if (_currentChain.MaxChainDepth == _bestChain.MaxChainDepth
                         && _currentChain.TotalDetonationBonus > _bestChain.TotalDetonationBonus)
                    isBetter = true;
                else if (_currentChain.MaxChainDepth == _bestChain.MaxChainDepth
                         && _currentChain.TotalDetonationBonus == _bestChain.TotalDetonationBonus
                         && _currentChain.TotalTriggeredCount > _bestChain.TotalTriggeredCount)
                    isBetter = true;
            }

            if (isBetter)
            {
                _bestChain = _currentChain;
                Debug.Log($"[DetonationRecorder] New best chain! depth={_bestChain.MaxChainDepth} " +
                          $"bonus={_bestChain.TotalDetonationBonus} triggers={_bestChain.TotalTriggeredCount} " +
                          $"steps={_bestChain.Steps.Count}");
            }

            _currentChain = null;
        }

        /// <summary>
        /// Returns the best recorded chain for this match, or null if no detonations occurred.
        /// </summary>
        public RecordedChain GetBestChain()
        {
            return _bestChain;
        }

        /// <summary>
        /// Returns true if a replay-worthy chain was recorded.
        /// </summary>
        public bool HasReplay => _bestChain != null && _bestChain.Steps.Count > 0;

        // ── Private helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Removes WordsScored steps that sit at the end of the list
        /// (never followed by a detonation in this chain).
        /// </summary>
        private void PruneNonDetonationWords()
        {
            if (_currentChain == null) return;
            var steps = _currentChain.Steps;

            // Walk backwards and remove trailing WordsScored that had no detonation after them
            while (steps.Count > 0
                   && steps[steps.Count - 1].Phase == RulesEngine.ResolutionPhase.WordsScored)
            {
                steps.RemoveAt(steps.Count - 1);
            }
        }
    }
}
