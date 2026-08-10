namespace PoFootball.Models
{
    /// <summary>
    /// Where the ball is. Replaces the run-only invariant from milestone 1, where
    /// the ball was permanently attached to one player and acceptance criterion #7
    /// asserted it could never come loose.
    /// </summary>
    public enum Systems_BallState
    {
        /// <summary>Carried by a player. CarrierId is valid.</summary>
        Held = 0,

        /// <summary>Thrown and travelling. CarrierId is invalid; position and velocity are.</summary>
        InFlight = 1,

        /// <summary>Hit the ground uncaught. The play is over.</summary>
        Incomplete = 2
    }
}
