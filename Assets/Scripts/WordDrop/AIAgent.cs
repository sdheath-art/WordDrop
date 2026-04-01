using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// AI opponent with weighted heuristic move evaluation.
    ///
    /// Each candidate move is scored across multiple dimensions:
    ///   - immediateScore: word points from this drop
    ///   - triggerBonus: bonus for detonating primed words
    ///   - blockValue: disrupting human opportunities
    ///   - setupValue: creating useful partial formations
    ///   - centerBias: early-game preference for central columns
    ///   - antiStallValue: adjacency bonus when no scoring move exists
    ///
    /// Difficulty controls randomness, not intelligence:
    ///   Easy (0):   65% random moves
    ///   Medium (1): 40% random moves
    ///   Hard (2):   always best heuristic move
    /// </summary>
    public class AIAgent : MonoBehaviour
    {
        public static AIAgent Instance { get; private set; }

        // ── Tuning constants ────────────────────────────────────────────────────

        private const float AI_DELAY = 0.3f;

        // ── AI Profiles ────────────────────────────────────────────────────────
        public enum AIProfile { Scorer, Blocker, TriggerHunter }
        public static AIProfile CurrentProfile { get; set; } = AIProfile.Scorer;

        public static string ProfileName => CurrentProfile.ToString();

        private struct ProfileWeights
        {
            public float Immediate, Trigger, Block, Setup, Center, AntiStall;
        }

        private static readonly ProfileWeights[] PROFILES = new ProfileWeights[]
        {
            // Scorer: maximizes immediate points
            new ProfileWeights { Immediate = 1.3f, Trigger = 6.0f, Block = 0.5f, Setup = 0.8f, Center = 0.3f, AntiStall = 0.3f },
            // Blocker: denies human opportunities, plays defensively
            new ProfileWeights { Immediate = 0.9f, Trigger = 5.0f, Block = 4.0f, Setup = 1.0f, Center = 0.2f, AntiStall = 0.4f },
            // TriggerHunter: lives for detonation chains
            new ProfileWeights { Immediate = 0.7f, Trigger = 12.0f, Block = 0.5f, Setup = 1.5f, Center = 0.2f, AntiStall = 0.3f },
        };

        private static ProfileWeights Weights => PROFILES[(int)CurrentProfile];
        private static float W_IMMEDIATE => Weights.Immediate;
        private static float W_TRIGGER   => Weights.Trigger;
        private static float W_BLOCK     => Weights.Block;
        private static float W_SETUP     => Weights.Setup;
        private static float W_CENTER    => Weights.Center;
        private static float W_ANTISTALL => Weights.AntiStall;

        // Center bias decays as board fills
        private const float CENTER_DECAY_THRESHOLD = 0.3f;

        // ── Difficulty ──────────────────────────────────────────────────────────

        public static int Difficulty { get; set; } = 0;

        private static float RandomChance
        {
            get
            {
                switch (Difficulty)
                {
                    case 0: return 0.65f;  // Easy
                    case 1: return 0.40f;  // Medium
                    case 2: return 0.00f;  // Hard
                    default: return 0.40f;
                }
            }
        }

        public static string DifficultyName
        {
            get
            {
                switch (Difficulty)
                {
                    case 0: return "Easy";
                    case 1: return "Medium";
                    case 2: return "Hard";
                    default: return "Medium";
                }
            }
        }

        // ── Move evaluation result ──────────────────────────────────────────────

        private struct MoveEval
        {
            public int Col;
            public int Slot;
            public char Letter;
            public float ImmediateScore;
            public float TriggerBonus;
            public float BlockValue;
            public float SetupValue;
            public float CenterBias;
            public float AntiStallValue;

            public float Total =>
                ImmediateScore * W_IMMEDIATE +
                TriggerBonus   * W_TRIGGER +
                BlockValue     * W_BLOCK +
                SetupValue     * W_SETUP +
                CenterBias     * W_CENTER +
                AntiStallValue * W_ANTISTALL;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Debug.Log("[AIAgent] Awake — new RulesEngine-based AI");
        }

        public float Delay => AI_DELAY;

        // ═══════════════════════════════════════════════════════════════════════════
        // PUBLIC API
        // ═══════════════════════════════════════════════════════════════════════════

        public bool EvaluateBestMove(out int bestCol, out int bestSlot, out char bestLetter)
        {
            bestCol    = -1;
            bestSlot   = -1;
            bestLetter = '\0';

            if (MatchController.Instance == null || RulesEngine.Instance == null)
                return false;

            PlayerHand aiHand = MatchController.Instance.GetHand(MatchController.PLAYER_AI);
            if (aiHand == null) return false;

            RulesEngine rules = RulesEngine.Instance;
            PrimedWordRegistry registry = rules.PrimedRegistry;
            float boardOccupancy = rules.GetBoardOccupancy();

            MoveEval best = default;
            best.ImmediateScore = -999f;
            bool foundAny = false;

            // Evaluate every (slot, column) combination
            for (int slot = 0; slot < PlayerHand.HAND_SIZE; slot++)
            {
                char letter = aiHand.GetSlot(slot);
                if (letter == '\0') continue;

                for (int col = 0; col < RulesEngine.COLS; col++)
                {
                    if (!rules.IsColumnAvailable(col)) continue;

                    MoveEval eval = EvaluateMove(rules, registry, col, letter, slot, boardOccupancy);

                    if (!foundAny || eval.Total > best.Total)
                    {
                        best = eval;
                        foundAny = true;
                    }
                }
            }

            if (!foundAny)
            {
                Debug.Log("[AIAgent] No valid move found.");
                return false;
            }

            // Difficulty gate: chance to play random instead
            if (best.ImmediateScore > 0 && Random.value < RandomChance)
            {
                Debug.Log($"[AIAgent] Difficulty ({DifficultyName}): ignoring best, playing random.");
                if (PickRandomMove(aiHand, out bestCol, out bestSlot, out bestLetter))
                    return true;
            }

            bestCol    = best.Col;
            bestSlot   = best.Slot;
            bestLetter = best.Letter;

            if (Difficulty == 2) // Hard: log breakdown for tuning
            {
                Debug.Log($"[AIAgent] Hard pick: '{best.Letter}'→col{best.Col} " +
                          $"total={best.Total:F1} (imm={best.ImmediateScore:F0} " +
                          $"trig={best.TriggerBonus:F0} blk={best.BlockValue:F1} " +
                          $"set={best.SetupValue:F1} ctr={best.CenterBias:F1} " +
                          $"stl={best.AntiStallValue:F1})");
            }
            else
            {
                string trigStr = best.TriggerBonus > 0 ? " [TRIGGERS!]" : "";
                Debug.Log($"[AIAgent] Best move ({DifficultyName}): '{best.Letter}' → col {best.Col} " +
                          $"(score={best.ImmediateScore:F0}{trigStr}, slot={best.Slot})");
            }

            return bestCol >= 0;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // MOVE EVALUATION
        // ═══════════════════════════════════════════════════════════════════════════

        private MoveEval EvaluateMove(RulesEngine rules, PrimedWordRegistry registry,
            int col, char letter, int slot, float boardOccupancy)
        {
            var eval = new MoveEval
            {
                Col = col,
                Slot = slot,
                Letter = letter,
            };

            // 1. Immediate score
            List<RulesWordMatch> matches = rules.SimulateDrop(col, letter, MatchController.PLAYER_AI);
            float simScore = 0;
            for (int m = 0; m < matches.Count; m++)
                simScore += matches[m].Score;
            eval.ImmediateScore = simScore;

            // 2. Trigger bonus
            if (registry != null && registry.Count > 0 && matches.Count > 0)
            {
                int triggerCount = CountPrimedTriggers(matches, registry);
                eval.TriggerBonus = triggerCount * RulesEngine.BREAKER_BONUS;
            }

            // 3. Block value — does this move occupy a cell the human could use?
            eval.BlockValue = EstimateBlockValue(rules, col, letter);

            // 4. Setup value — does this create useful partial formations?
            eval.SetupValue = EstimateSetupValue(rules, col, letter);

            // 5. Center bias — prefer central columns early
            float centerDist = Mathf.Abs(col - 3f); // col 3 is center of 0-6
            float centerFactor = Mathf.Max(0f, 1f - boardOccupancy / CENTER_DECAY_THRESHOLD);
            eval.CenterBias = (3f - centerDist) * centerFactor;

            // 6. Anti-stall — when nothing scores, prefer adjacent placements
            if (simScore == 0)
                eval.AntiStallValue = EstimateConnectivity(rules, col);

            return eval;
        }

        // ── Heuristic: block value ──────────────────────────────────────────────

        private float EstimateBlockValue(RulesEngine rules, int col, char letter)
        {
            // Check if the human has primed words. Occupying cells near human primed
            // words makes it harder for the human to build trigger words.
            PrimedWordRegistry registry = rules.PrimedRegistry;
            if (registry == null || registry.Count == 0) return 0f;

            int targetRow = rules.GetLowestEmptyRow(col);
            if (targetRow < 0) return 0f;

            float blockScore = 0f;
            var allPrimed = registry.GetAllPrimedWords();

            for (int i = 0; i < allPrimed.Count; i++)
            {
                // Only care about human-owned primed words
                if (allPrimed[i].OwnerPlayer != MatchController.PLAYER_HUMAN) continue;

                for (int c = 0; c < allPrimed[i].Cells.Count; c++)
                {
                    Vector2Int cell = allPrimed[i].Cells[c];
                    // Adjacent to a human primed cell = blocking potential
                    int dx = Mathf.Abs(cell.x - col);
                    int dy = Mathf.Abs(cell.y - targetRow);
                    if (dx <= 1 && dy <= 1)
                        blockScore += 1.0f;
                }
            }

            return blockScore;
        }

        // ── Heuristic: setup value ──────────────────────────────────────────────

        private float EstimateSetupValue(RulesEngine rules, int col, char letter)
        {
            int targetRow = rules.GetLowestEmptyRow(col);
            if (targetRow < 0) return 0f;

            float setup = 0f;

            // Check horizontal: count adjacent occupied cells on same row
            int hRun = 1;
            for (int c = col - 1; c >= 0; c--)
            {
                if (rules.GetCell(c, targetRow) != null) hRun++;
                else break;
            }
            for (int c = col + 1; c < RulesEngine.COLS; c++)
            {
                if (rules.GetCell(c, targetRow) != null) hRun++;
                else break;
            }
            // A run of 2 = one letter away from a word (valuable setup)
            if (hRun == 2) setup += 2.0f;

            // Check vertical: count tiles below in same column
            int vRun = 1;
            for (int r = targetRow - 1; r >= 0; r--)
            {
                if (rules.GetCell(col, r) != null) vRun++;
                else break;
            }
            if (vRun == 2) setup += 1.5f;

            return setup;
        }

        // ── Heuristic: connectivity ─────────────────────────────────────────────

        private float EstimateConnectivity(RulesEngine rules, int col)
        {
            int targetRow = rules.GetLowestEmptyRow(col);
            if (targetRow < 0) return 0f;

            float conn = 0f;

            // Count adjacent occupied cells (8-connected)
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nc = col + dx;
                    int nr = targetRow + dy;
                    if (nc >= 0 && nc < RulesEngine.COLS && nr >= 0 && nr < RulesEngine.ROWS)
                    {
                        if (rules.GetCell(nc, nr) != null)
                            conn += 1.0f;
                    }
                }
            }

            return conn;
        }

        // ── Trigger counting ────────────────────────────────────────────────────

        private int CountPrimedTriggers(List<RulesWordMatch> newWords, PrimedWordRegistry registry)
        {
            if (registry == null || newWords == null) return 0;

            HashSet<int> triggered = new HashSet<int>();

            for (int w = 0; w < newWords.Count; w++)
            {
                if (newWords[w].Cells == null) continue;
                for (int c = 0; c < newWords[w].Cells.Count; c++)
                {
                    var overlapping = registry.GetPrimedWordsContaining(newWords[w].Cells[c]);
                    for (int p = 0; p < overlapping.Count; p++)
                        triggered.Add(overlapping[p].Id);
                }
            }

            return triggered.Count;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // RANDOM FALLBACK
        // ═══════════════════════════════════════════════════════════════════════════

        private bool PickRandomMove(PlayerHand hand, out int col, out int slot, out char letter)
        {
            col = -1; slot = -1; letter = '\0';

            List<int> availCols = new List<int>();
            for (int c = 0; c < RulesEngine.COLS; c++)
                if (RulesEngine.Instance != null && RulesEngine.Instance.IsColumnAvailable(c))
                    availCols.Add(c);
            if (availCols.Count == 0) return false;

            List<int> availSlots = new List<int>();
            for (int s = 0; s < PlayerHand.HAND_SIZE; s++)
                if (hand.GetSlot(s) != '\0')
                    availSlots.Add(s);
            if (availSlots.Count == 0) return false;

            col    = availCols[Random.Range(0, availCols.Count)];
            slot   = availSlots[Random.Range(0, availSlots.Count)];
            letter = hand.GetSlot(slot);
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // LEGACY STUBS
        // ═══════════════════════════════════════════════════════════════════════════

        public void InitialiseHand(TileBag bag) { }
        public System.Collections.IEnumerator TakeTurnCoroutine(TileBag bag, HashSet<string> scoredWordKeys) { yield break; }
        public System.Collections.IEnumerator TakeTurnSimple() { yield break; }

        public Tile LastPlacedTile   { get; private set; }
        public int  LastPlacedCol    { get; private set; } = -1;
        public char LastPlacedLetter { get; private set; }
    }
}
