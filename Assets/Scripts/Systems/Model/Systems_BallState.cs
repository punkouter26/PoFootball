namespace PoFootball.Models
{
    /// <summary>
    /// Where the ball is. Replaces the run-only invariant from milestone 1, where
    /// the ball was permanently attached to one player and acceptance criterion #7
    /// asserted it could never come loose.
    ///
    /// Revision 14 added the two states a ball reaches without anybody's hands on
    /// it: off a foot, and on the ground. Through revision 13 a fumble was a dice
    /// roll that ended the play where the tackle happened and a kick was never in
    /// the air at all.
    /// </summary>
    public enum Systems_BallState
    {
        /// <summary>Carried by a player. CarrierId is valid.</summary>
        Held = 0,

        /// <summary>
        /// Thrown and travelling — a forward pass or a lateral, which
        /// Systems_BallModel.PassKind tells apart. CarrierId is invalid.
        /// </summary>
        InFlight = 1,

        /// <summary>A forward pass that hit the ground uncaught. The play is over.</summary>
        Incomplete = 2,

        /// <summary>
        /// Kicked and in the air. Unlike a pass it has a real height — whether it
        /// can be fielded depends on it (Systems_SimConstants.KICK_CATCH_HEIGHT).
        /// </summary>
        Kicked = 3,

        /// <summary>
        /// On the ground and live: a fumble, a muff, a lateral nobody caught, a kick
        /// that landed, a blocked kick. Either side may recover it.
        /// </summary>
        Loose = 4
    }
}
