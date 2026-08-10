namespace PoFootball.Systems
{
    /// <summary>
    /// Published when a defender brings the carrier down. Carries the closing
    /// speed that satisfied the threshold so presentation can scale the impact
    /// and telemetry can histogram hit strength.
    /// </summary>
    public readonly struct Systems_TackleMessage
    {
        public readonly int TacklerId;
        public readonly int CarrierId;
        public readonly float ClosingSpeed;
        public readonly float YardsFromScrimmage;

        public Systems_TackleMessage(
            int tacklerId, int carrierId, float closingSpeed, float yardsFromScrimmage)
        {
            TacklerId = tacklerId;
            CarrierId = carrierId;
            ClosingSpeed = closingSpeed;
            YardsFromScrimmage = yardsFromScrimmage;
        }
    }
}
