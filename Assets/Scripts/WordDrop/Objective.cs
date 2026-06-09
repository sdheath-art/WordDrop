using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// Base for a level objective (2026-06-08): holds the goal, tracks progress from gameplay
    /// events, decides completion, and surfaces a short HUD label. Each objective TYPE is a
    /// small plug-in subclass; ObjectiveManager owns the active instance and feeds it events.
    /// The win-check (Objective.IsComplete) is what replaces the old "score >= target"
    /// condition — see project_wordrop_objective_direction. Override only the hooks a given
    /// objective needs; the rest are no-ops.
    /// </summary>
    public abstract class Objective
    {
        /// <summary>Short HUD title, e.g. "5+ letter words".</summary>
        public abstract string Title { get; }

        /// <summary>Progress readout, e.g. "1 / 3".</summary>
        public abstract string ProgressText { get; }

        /// <summary>True once the goal is met. ObjectiveManager fires the win on the rising edge.</summary>
        public abstract bool IsComplete { get; }

        // ── Event hooks — ObjectiveManager calls these. Override what you need. ──
        /// <summary>A word was scored/PRIMED (made). Use for "make N words" goals.</summary>
        public virtual void OnWordScored(WordScoredEvent evt) { }
        /// <summary>A primed word's tiles EXPLODED — fires for every word destroyed in a blast,
        /// whether it was the trigger, the triggered, or swept in via the connected-group /
        /// splash sweep. Use for "explode N words" goals; priming is trivial, detonating is the
        /// real achievement. Called once per exploded word. 2026-06-08.</summary>
        public virtual void OnWordExploded(string word, int ownerPlayerIndex) { }
        public virtual void OnTilesExploded(List<Vector2Int> cells) { }
        /// <summary>A drop-target tile reached the bottom row and was collected (count this tick).
        /// Use for "bring N to the bottom" / hero-word goals.</summary>
        public virtual void OnDropTargetCollected(int count) { }
        /// <summary>Per-frame hook from ObjectiveManager — for objectives that need to set up board
        /// state once it's ready (e.g. spawn drop-targets) or poll. No-op by default.</summary>
        public virtual void Tick() { }
        public virtual void Reset() { }
    }

    /// <summary>
    /// "Explode N words of L+ letters." Counts DETONATIONS, not primes — priming a long word
    /// is trivial, but detonating one requires the full setup→trigger→cascade play, so this is
    /// the real achievement (Spencer, 2026-06-08). Hooks OnWordDetonated (PrimedTriggeredEvent).
    /// </summary>
    public sealed class LongWordObjective : Objective
    {
        private readonly int _minLen;
        private readonly int _goal;
        private int _count;

        public LongWordObjective(int minLen, int goal)
        {
            _minLen = Mathf.Max(2, minLen);
            _goal   = Mathf.Max(1, goal);
        }

        public override string Title        => _minLen <= 2 ? "Explode words" : $"Explode {_minLen}+ letter words";
        public override string ProgressText => $"{Mathf.Min(_count, _goal)} / {_goal}";
        public override bool   IsComplete   => _count >= _goal;

        // Counts a word whenever its tiles actually blow up — doesn't matter if it was the
        // primed word that got set off or the word that set it off; if it explodes, it counts.
        public override void OnWordExploded(string word, int ownerPlayerIndex)
        {
            if (IsComplete) return;
            if (ownerPlayerIndex == MatchController.PLAYER_HUMAN
                && !string.IsNullOrEmpty(word) && word.Length >= _minLen)
                _count++;
        }

        public override void Reset() => _count = 0;
    }

    /// <summary>
    /// "Bring N to the bottom" — the flagship hero-word / drop-to-bottom goal (Candy-Crush ingredient
    /// drop). Spawns N drop-target tiles once the board is ready; the player clears beneath them so
    /// gravity escorts them to row 0, where ObjectiveManager collects them. Feel-tested positive as a
    /// prototype (2026-06-01); ported into the framework 2026-06-09.
    /// </summary>
    public sealed class HeroWordObjective : Objective
    {
        private readonly int _goal;
        private int  _collected;
        private bool _spawned;

        public HeroWordObjective(int goal) => _goal = Mathf.Max(1, goal);

        public override string Title        => $"Drop {_goal} to the bottom";
        public override string ProgressText => $"{Mathf.Min(_collected, _goal)} / {_goal}";
        public override bool   IsComplete   => _collected >= _goal;

        public override void OnDropTargetCollected(int count)
        {
            if (!IsComplete) _collected += count;
        }

        // Spawn the targets once the board actually has tiles to sit on.
        public override void Tick()
        {
            if (_spawned || RulesEngine.Instance == null) return;
            if (RulesEngine.Instance.GetBoardOccupancy() <= 0f) return; // board not seeded yet
            RulesEngine.Instance.SpawnDropTargetsForTest(_goal);
            GridManager.Instance?.SyncToRulesState(RulesEngine.Instance);
            _spawned = true;
        }

        public override void Reset()
        {
            _collected = 0;
            _spawned   = false;
        }
    }

    /// <summary>
    /// "Land a K-chain cascade." Reuses ChainStep on OnWordScored (ChainStep+1 = words in the
    /// cascade). Cheap to build, teaches the setup→trigger→cascade wow loop.
    /// </summary>
    public sealed class ChainObjective : Objective
    {
        private readonly int _goalChain;
        private int _best;

        public ChainObjective(int goalChain) => _goalChain = Mathf.Max(2, goalChain);

        public override string Title        => $"Land a {_goalChain}-chain";
        public override string ProgressText => $"best {_best} / {_goalChain}";
        public override bool   IsComplete   => _best >= _goalChain;

        public override void OnWordScored(WordScoredEvent evt)
        {
            if (IsComplete || evt == null || evt.PlayerIndex != MatchController.PLAYER_HUMAN) return;
            int chainLen = evt.ChainStep + 1; // ChainStep 0 = first word, so a k-chain reaches ChainStep k-1
            if (chainLen > _best) _best = chainLen;
        }

        public override void Reset() => _best = 0;
    }
}
