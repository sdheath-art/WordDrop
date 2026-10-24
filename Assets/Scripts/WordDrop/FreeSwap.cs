using UnityEngine;

namespace WordDrop
{
    /// <summary>
    /// FREE SWAP booster (2026-10-23 Spencer: "suggestions for boosters that would work better?" -> "sure" to Free Swap).
    /// Tap one tile, then ANY other tile on the board: they trade places and it costs NO move. The word-game answer to "I
    /// can see the word, the letter is just across the board" — Candy Crush ships the same booster (Free Switch).
    ///
    /// Two taps, so it uses BoosterManager's two-target aim (NeedsTwoTargets): the first tap selects (teal), tapping it
    /// again deselects, the second tap commits. The swap itself is the REAL swap (HandManager.CommitFreeSwap ->
    /// TryBoardSwap with the touching rule and the move cost lifted), so words, specials, gems and goals all behave exactly
    /// as in a normal swap. A refused pair (ice, crate, armed word, same letter) keeps the booster armed, no charge spent.
    /// </summary>
    public class FreeSwap : Booster
    {
        public override string Id               => BoosterManager.ID_FREE_SWAP;
        public override string DisplayName      => "Free Swap";
        public override string ShortDescription => "Swap any two tiles. No move used.";
        public override bool   NeedsTarget      => true;
        public override bool   NeedsTwoTargets  => true;

        public override bool CanResolveAt(int col, int row) => RulesEngine.IsBotMovable(RulesEngine.Instance?.GetCell(col, row))
            || (RulesEngine.Instance?.GetCell(col, row)?.SpecialKind ?? 0) != RulesEngine.SPECIAL_NONE;   // specials can move too

        public override bool TryResolvePair(Vector2Int a, Vector2Int b)
        {
            var hand = HandManager.Instance;
            if (hand == null) return false;
            bool ok = hand.CommitFreeSwap(a, b);
            WDLog.Log($"[FreeSwap] ({a.x},{a.y})<->({b.x},{b.y}) {(ok ? "swapped (no move)" : "refused")}");
            return ok;
        }
    }
}
