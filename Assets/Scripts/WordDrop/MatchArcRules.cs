namespace WordDrop
{
    /// <summary>
    /// Match arc bonuses that make every game crescendo.
    /// Final turns give bonus points, and trailing players get a comeback chance.
    /// </summary>
    public static class MatchArcRules
    {
        // Final push: bonus points in last N turns to reward late-game aggression
        public const int FINAL_PUSH_THRESHOLD = 3; // turns remaining to activate

        /// <summary>Bonus points for scoring in the final turns. 0 if not in final push.</summary>
        public static int GetFinalPushBonus(int turnsRemaining)
        {
            if (turnsRemaining > FINAL_PUSH_THRESHOLD) return 0;
            switch (turnsRemaining)
            {
                case 3: return 2;
                case 2: return 4;
                case 1: return 6;
                default: return 0;
            }
        }

        // Comeback: trailing player gets bonus when far behind in final turns
        public const int COMEBACK_SCORE_GAP = 12;
        public const int COMEBACK_BONUS = 4;
        public const int COMEBACK_TURNS_THRESHOLD = 3;

        /// <summary>Bonus for a trailing player scoring in the final turns. 0 if not eligible.</summary>
        public static int GetComebackBonus(int playerScore, int opponentScore, int turnsRemaining)
        {
            if (turnsRemaining > COMEBACK_TURNS_THRESHOLD) return 0;
            if (playerScore >= opponentScore) return 0; // not trailing
            if (opponentScore - playerScore < COMEBACK_SCORE_GAP) return 0; // gap not big enough
            return COMEBACK_BONUS;
        }
    }
}
