namespace PoFootball.Systems
{
    /// <summary>
    /// Everything the next snap needs to know about the game around it: where the
    /// ball is, which down it is, and how far there is to go.
    ///
    /// WHY THIS REPLACED A BARE FLOAT. The spot provider used to hand back a line of
    /// scrimmage and nothing else, because nothing else was needed — every play was
    /// an isolated rep and the only decisions were run or pass. Punt and field goal
    /// are not like that. They are correct or absurd purely as a function of down and
    /// distance, so a quarterback that cannot see the down cannot learn when to use
    /// them, and the training environment genuinely did not have downs at all:
    /// Systems_GameFlowSystem, which owns the chains, is registered only in
    /// Systems_SimMode.Game.
    ///
    /// Returning the three together rather than adding two more methods keeps them
    /// consistent by construction. Three separate calls would have to be made in the
    /// right order against a provider that samples as it goes, which is exactly the
    /// kind of implicit coupling that breaks the first time somebody reorders two
    /// lines.
    /// </summary>
    public readonly struct Systems_PlaySituation
    {
        public readonly float LineOfScrimmageY;

        /// <summary>One through Systems_GameRules.DOWNS_PER_SERIES.</summary>
        public readonly int Down;

        public readonly float YardsToGo;

        public Systems_PlaySituation(float lineOfScrimmageY, int down, float yardsToGo)
        {
            LineOfScrimmageY = lineOfScrimmageY;
            Down = down;
            YardsToGo = yardsToGo;
        }
    }
}
