namespace PoFootball.Models
{
    /// <summary>
    /// One flag. Which foul, which side of the snap committed it, where, and when.
    ///
    /// Side is the side of the SNAP — Offense means the bodies that attack +Y on
    /// this play — never the team. The game layer turns it into a team at the
    /// whistle, the same way it turns every other spot on this play into one.
    /// </summary>
    public readonly struct Systems_Foul
    {
        public readonly Systems_FoulType Type;

        public readonly Systems_TeamSide Side;

        /// <summary>Formation slot of the player flagged, or -1.</summary>
        public readonly int PlayerId;

        /// <summary>Y where it happened, in the play's frame.</summary>
        public readonly float SpotY;

        /// <summary>Physics tick of the play it was flagged on; 0 before the snap.</summary>
        public readonly int Tick;

        public Systems_Foul(
            Systems_FoulType type, Systems_TeamSide side, int playerId, float spotY, int tick)
        {
            Type = type;
            Side = side;
            PlayerId = playerId;
            SpotY = spotY;
            Tick = tick;
        }

        public bool IsNone => Type == Systems_FoulType.None;
    }
}
