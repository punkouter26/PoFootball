namespace PoFootball.Systems
{
    /// <summary>Published when the carrier crosses the attacking goal line.</summary>
    public readonly struct Systems_ScoreMessage
    {
        public readonly int CarrierId;
        public readonly float YardsGained;

        public Systems_ScoreMessage(int carrierId, float yardsGained)
        {
            CarrierId = carrierId;
            YardsGained = yardsGained;
        }
    }
}
