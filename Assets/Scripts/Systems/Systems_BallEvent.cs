using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// What a tick of the ball decided, when it decided the play is over: how, where,
    /// and — for a score — whose. <see cref="None"/> while the play goes on.
    ///
    /// A bare outcome used to be enough, because the ball could only end a play in
    /// the offense's favour or with an incompletion. Since revision 14 a ball on the
    /// ground in an end zone can be a touchdown for either side, a safety for either
    /// side or a touchback for either side, and the spot a kick is ruled dead at is
    /// often not where the ball is.
    /// </summary>
    public readonly struct Systems_BallEvent
    {
        public static readonly Systems_BallEvent None =
            new Systems_BallEvent(Systems_PlayOutcome.None, Vector2.zero, Systems_TeamSide.Offense);

        public readonly Systems_PlayOutcome Outcome;

        public readonly Vector2 Spot;

        /// <summary>For a Touchdown or a Safety, whose points they are.</summary>
        public readonly Systems_TeamSide ScoringSide;

        public Systems_BallEvent(
            Systems_PlayOutcome outcome, Vector2 spot, Systems_TeamSide scoringSide)
        {
            Outcome = outcome;
            Spot = spot;
            ScoringSide = scoringSide;
        }

        public bool EndsPlay => Outcome != Systems_PlayOutcome.None;
    }
}
