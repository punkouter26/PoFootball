using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published by <see cref="Systems_GameFlowSystem"/> when a pre-snap foul moves
    /// the ball. Its own message rather than a Systems_DownResolvedMessage because
    /// no down was played: the stats layer and the drive chart count plays off that
    /// one, and a flag is not a play.
    /// </summary>
    public readonly struct Systems_PenaltyMessage
    {
        public readonly Systems_PreSnapFoul Foul;

        /// <summary>The team the flag is on.</summary>
        public readonly Systems_TeamId Team;

        /// <summary>Yards actually walked off — less than five near a goal line.</summary>
        public readonly float Yards;

        public Systems_PenaltyMessage(Systems_PreSnapFoul foul, Systems_TeamId team, float yards)
        {
            Foul = foul;
            Team = team;
            Yards = yards;
        }
    }
}
