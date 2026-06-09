using System.Collections.Generic;
using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// All event types emitted by RulesEngine / MatchController.
    /// Defined as simple C# classes (reference types) for easy passing via delegates.
    ///
    /// Event flow overview (full system — stubs for future jobs):
    ///
    ///   RulesEngine.ProcessDrop()
    ///     → TileDropped
    ///     → WordScored      (per word found)
    ///     → WordPrimed      (per word primed)
    ///     → PrimedTriggered (when new word overlaps existing primed word)
    ///     → TilesExploded   (after triggered primed word removed)
    ///     → GravityCollapse (after tiles fall)
    ///     → ChainStep       (per cascade iteration)
    ///     → ResolutionComplete
    ///
    ///   MatchController
    ///     → TurnEnd
    ///     → HandRefilled
    ///     → MatchEnd
    ///
    /// All events are plain C# — no UnityEvent, no ScriptableObject.
    /// GameVisualBridge (Job 7) subscribes to these via C# delegates.
    /// </summary>

    // ── Delegate definitions ──────────────────────────────────────────────────────

    /// <summary>Generic no-argument event.</summary>
    public delegate void RulesEventHandler();

    /// <summary>Event carrying a typed payload.</summary>
    public delegate void RulesEventHandler<T>(T evt);

    // ═════════════════════════════════════════════════════════════════════════════
    // DROP & PLACEMENT EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired when a tile is logically placed on the RulesEngine board.
    /// GameVisualBridge uses this to call GridManager.DropTileAndGetTile().
    /// </summary>
    public class TileDroppedEvent
    {
        /// <summary>Column the tile was dropped into (0–6).</summary>
        public int  Col         { get; set; }

        /// <summary>Row the tile landed on after gravity (0–5).</summary>
        public int  Row         { get; set; }

        /// <summary>The letter placed (A-Z).</summary>
        public char Letter      { get; set; }

        /// <summary>0 = human player, 1 = AI.</summary>
        public int  PlayerIndex { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // WORD SCORING EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired for each new valid word found and scored during ProcessDrop().
    /// Includes chain step info for bonus calculation.
    /// </summary>
    public class WordScoredEvent
    {
        /// <summary>The word that was scored (uppercase).</summary>
        public string           Word        { get; set; }

        /// <summary>Board positions of the word's tiles.</summary>
        public List<Vector2Int> Cells       { get; set; }

        /// <summary>Base score before chain bonuses (letter values × length multiplier).</summary>
        public int              BaseScore   { get; set; }

        /// <summary>Final score including chain bonuses.</summary>
        public int              FinalScore  { get; set; }

        /// <summary>0 = human player, 1 = AI.</summary>
        public int              PlayerIndex { get; set; }

        /// <summary>
        /// Chain step index. 0 = first detection (no chain), 1+ = cascade steps.
        /// Chain bonus of +3 per step applies for step >= 1.
        /// </summary>
        public int              ChainStep   { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // PRIMING EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired when a scored word becomes primed (stored in PrimedWordRegistry).
    /// GameVisualBridge applies a colored glow to the tiles.
    /// </summary>
    public class WordPrimedEvent
    {
        /// <summary>The word that is now primed (uppercase).</summary>
        public string           Word        { get; set; }

        /// <summary>Board positions of the primed word's tiles.</summary>
        public List<Vector2Int> Cells       { get; set; }

        /// <summary>0 = human player, 1 = AI. Determines glow color.</summary>
        public int              PlayerIndex { get; set; }

        /// <summary>Turn number on which this word was primed.</summary>
        public int              PrimedOnTurn { get; set; }

        /// <summary>Turn number after which this primed word expires.</summary>
        public int              ExpiresOnTurn { get; set; }

        /// <summary>
        /// Unique ID for this primed word record.
        /// Used by GameVisualBridge to map from PrimedTriggeredEvent back to visual tiles.
        /// </summary>
        public int              PrimedWordId { get; set; }
    }

    /// <summary>
    /// Fired when a newly detected word overlaps an existing primed word,
    /// triggering that primed word's explosion sequence.
    /// </summary>
    public class PrimedTriggeredEvent
    {
        /// <summary>The word text of the primed word being triggered.</summary>
        public string           TriggeredWord   { get; set; }

        /// <summary>Board positions of the triggered primed word's tiles.</summary>
        public List<Vector2Int> TriggeredCells  { get; set; }

        /// <summary>The new word that caused the trigger.</summary>
        public string           TriggerWord     { get; set; }

        /// <summary>
        /// The overlapping cell that caused the trigger.
        /// (col, row) of the shared tile.
        /// </summary>
        public Vector2Int       OverlapCell     { get; set; }

        /// <summary>0 = human player, 1 = AI (owner of the triggered primed word).</summary>
        public int              OwnerPlayerIndex { get; set; }

        /// <summary>Unique ID matching the PrimedWordEvent that created this primed word.</summary>
        public int              PrimedWordId    { get; set; }

        /// <summary>True if this trigger was caused by chain connectivity (shared tile with another triggered primed word), not direct overlap with the new word.</summary>
        public bool             IsChainTrigger  { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // EXPLOSION & GRAVITY EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired after a primed word is triggered and its tiles are removed from the
    /// logical board. GameVisualBridge calls GridManager.RemoveTiles() in response.
    /// </summary>
    public class TilesExplodedEvent
    {
        /// <summary>Board positions of all tiles that were removed.</summary>
        public List<Vector2Int> RemovedCells { get; set; }

        /// <summary>The primed word whose explosion caused these removals.</summary>
        public string           SourceWord   { get; set; }

        /// <summary>Chain step at which this explosion occurred (0-based).</summary>
        public int              ChainStep    { get; set; }

        /// <summary>EVERY primed word destroyed in this explosion — the directly-triggered word
        /// AND ones swept in via connected-group expansion or the splash sweep (which never fire
        /// OnPrimedTriggered). The complete "what actually blew up" list, so objectives can count
        /// a long word however it detonated. 2026-06-08.</summary>
        public List<ExplodedWordInfo> ExplodedWords { get; set; }
    }

    /// <summary>A single primed word that was destroyed in an explosion.</summary>
    public struct ExplodedWordInfo
    {
        public string Word;
        public int    OwnerPlayerIndex;
    }

    /// <summary>
    /// Fired after tiles have been removed and the logical board's columns have
    /// been compacted downward (gravity applied in data layer).
    /// GameVisualBridge calls GridManager.ApplyGravity() in response.
    /// </summary>
    public class GravityCollapseEvent
    {
        /// <summary>
        /// Mapping of tiles that moved: key = old (col, row), value = new (col, row).
        /// Tiles that didn't move are not included.
        /// </summary>
        public Dictionary<Vector2Int, Vector2Int> TileMoves { get; set; }

        /// <summary>Chain step at which gravity occurred.</summary>
        public int ChainStep { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // CHAIN EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired at the start of each cascade iteration during ProcessDrop() resolution.
    /// GameVisualBridge logs this and adds a small delay for readability.
    /// </summary>
    public class ChainStepEvent
    {
        /// <summary>
        /// 0-based chain step index.
        /// Step 0 = initial placement (not a chain).
        /// Step 1+ = cascade caused by gravity collapse.
        /// Chain bonus of +3 per step applies for step >= 1.
        /// </summary>
        public int StepIndex { get; set; }

        /// <summary>Number of new words found in this chain step.</summary>
        public int NewWordsFound { get; set; }
    }

    /// <summary>
    /// Fired when the full resolution of a single drop is complete (board is stable).
    /// MatchController uses this to advance the turn counter and refill the hand.
    /// </summary>
    public class ResolutionCompleteEvent
    {
        /// <summary>Total score earned by the placing player during this resolution.</summary>
        public int  TotalScoreEarned { get; set; }

        /// <summary>Total number of chain steps that occurred (0 = no chain).</summary>
        public int  TotalChainSteps  { get; set; }

        /// <summary>Total number of words scored during this resolution.</summary>
        public int  WordsScored      { get; set; }

        /// <summary>0 = human player, 1 = AI.</summary>
        public int  PlayerIndex      { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // TURN & MATCH EVENTS
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired at the end of each player's turn (after full resolution + hand refill).
    /// HUDManager uses this to update the turn counter display.
    /// </summary>
    public class TurnEndEvent
    {
        /// <summary>0 = human player, 1 = AI.</summary>
        public int PlayerIndex      { get; set; }

        /// <summary>This player's turn count after this turn (1-based).</summary>
        public int PlayerTurnNumber { get; set; }

        /// <summary>Current global turn index (0-based, alternating P0/P1).</summary>
        public int GlobalTurnIndex  { get; set; }
    }

    /// <summary>
    /// Fired when a player's hand has been refilled after their turn.
    /// HandManager (Job 8) uses this to update card visuals.
    /// </summary>
    public class HandRefilledEvent
    {
        /// <summary>0 = human player, 1 = AI.</summary>
        public int    PlayerIndex { get; set; }

        /// <summary>The new set of letters in the hand (length = hand size).</summary>
        public char[] Letters     { get; set; }
    }

    /// <summary>
    /// Fired when the match ends (both players have taken 20 turns each).
    /// GameManager transitions to GameOver on receipt.
    /// </summary>
    public class MatchEndEvent
    {
        /// <summary>0 = P1 wins, 1 = AI wins, -1 = tie.</summary>
        public int WinnerPlayerIndex { get; set; }

        /// <summary>Final score for the human player.</summary>
        public int PlayerScore       { get; set; }

        /// <summary>Final score for the AI.</summary>
        public int AIScore           { get; set; }

        /// <summary>Total turns taken by each player.</summary>
        public int TotalTurnsEach    { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // SWAP EVENT
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired when a player uses a swap (replaces one hand card without dropping a tile).
    /// Counts as that player's turn.
    /// </summary>
    public class SwapUsedEvent
    {
        /// <summary>0 = human player, 1 = AI.</summary>
        public int  PlayerIndex    { get; set; }

        /// <summary>Hand slot index that was swapped (0–4).</summary>
        public int  HandSlot       { get; set; }

        /// <summary>The old letter that was discarded.</summary>
        public char OldLetter      { get; set; }

        /// <summary>The new letter drawn from the bag.</summary>
        public char NewLetter      { get; set; }

        /// <summary>Swaps remaining for this player after this use.</summary>
        public int  SwapsRemaining { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // REWRITE EVENT
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Fired when a player uses the Rewrite Tile action — replaces a board tile
    /// with a hand tile, costing 1 swap charge and consuming the turn.
    /// </summary>
    public class RewriteUsedEvent
    {
        public int  PlayerIndex    { get; set; }
        public int  HandSlot       { get; set; }
        public char HandLetter     { get; set; }
        public char OldBoardLetter { get; set; }
        public int  TargetCol      { get; set; }
        public int  TargetRow      { get; set; }
        public int  SwapsRemaining { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════════════
    // RESULT TYPE (returned by ProcessDrop, not an event)
    // ═════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returned synchronously by RulesEngine.ProcessDrop() (Job 4).
    /// Contains a summary of everything that happened during resolution.
    /// GameVisualBridge and MatchController read this to sequence visual updates.
    /// </summary>
    public class ResolutionResult
    {
        /// <summary>The column the tile was placed in.</summary>
        public int Col { get; set; }

        /// <summary>The row the tile landed on.</summary>
        public int Row { get; set; }

        /// <summary>The letter that was placed.</summary>
        public char Letter { get; set; }

        /// <summary>0 = human player, 1 = AI.</summary>
        public int PlayerIndex { get; set; }

        /// <summary>All words scored during this resolution (including chains).</summary>
        public List<WordScoredEvent> ScoredWords { get; set; } = new List<WordScoredEvent>();

        /// <summary>All words primed during this resolution.</summary>
        public List<WordPrimedEvent> PrimedWords { get; set; } = new List<WordPrimedEvent>();

        /// <summary>All primed words that were triggered (exploded) during this resolution.</summary>
        public List<PrimedTriggeredEvent> TriggeredPrimedWords { get; set; } = new List<PrimedTriggeredEvent>();

        /// <summary>All tile explosion events during this resolution.</summary>
        public List<TilesExplodedEvent> Explosions { get; set; } = new List<TilesExplodedEvent>();

        /// <summary>All gravity events during this resolution.</summary>
        public List<GravityCollapseEvent> GravityEvents { get; set; } = new List<GravityCollapseEvent>();

        /// <summary>Total score earned during this resolution (base + chain + detonation).</summary>
        public int TotalScore { get; set; }

        // ── Score breakdown ──────────────────────────────────────────────────────
        /// <summary>Sum of base word scores (letter values only).</summary>
        public int BaseWordScoreTotal { get; set; }
        /// <summary>Sum of chain bonuses across all chain steps.</summary>
        public int ChainBonusTotal { get; set; }
        /// <summary>Sum of detonation bonuses (BREAKER_BONUS per triggered word).</summary>
        public int DetonationBonusTotal { get; set; }

        /// <summary>Number of chain steps that occurred (0 = no cascade).</summary>
        public int ChainSteps { get; set; }

        /// <summary>True if the drop caused at least one word to be scored.</summary>
        public bool AnyWordScored => ScoredWords != null && ScoredWords.Count > 0;

        /// <summary>True if any primed word was triggered during this resolution.</summary>
        public bool AnyExplosion => Explosions != null && Explosions.Count > 0;
    }
}
