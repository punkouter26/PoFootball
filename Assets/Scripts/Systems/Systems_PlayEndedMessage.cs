using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published once per episode, the moment the whistle blows.
    ///
    /// Revision 14 added what a game needs to know about a play the ball changed
    /// hands on: what sort of snap it was, whose a score was, whether and where the
    /// other side got the ball, and how many flags are on the play (they are on
    /// Systems_PlayModel's foul list, which is still intact when this is read).
    /// </summary>
    public readonly struct Systems_PlayEndedMessage
    {
        public readonly Systems_PlayOutcome Outcome;
        public readonly Systems_PlayCall Call;
        public readonly Vector2 Spot;
        public readonly float NetYards;
        public readonly int PhysicsTicks;

        /// <summary>Whether a receiver caught a pass at any point on this play.</summary>
        public readonly bool PassCompleted;

        public readonly Systems_PlayKind Kind;

        /// <summary>
        /// For a Touchdown or Safety, whose points; for any other outcome, the side
        /// holding the ball at the whistle.
        /// </summary>
        public readonly Systems_TeamSide Side;

        /// <summary>Whether the defense of the snap had the ball at any point.</summary>
        public readonly bool PossessionChanged;

        /// <summary>Where the defense of the snap first had it — where its return began.</summary>
        public readonly float PossessionChangeY;

        /// <summary>Flags on the play; read them from Systems_PlayModel.</summary>
        public readonly int FoulCount;

        public Systems_PlayEndedMessage(
            Systems_PlayOutcome outcome,
            Systems_PlayCall call,
            Vector2 spot,
            float netYards,
            int physicsTicks,
            bool passCompleted)
            : this(
                outcome, call, spot, netYards, physicsTicks, passCompleted,
                Systems_PlayKind.Scrimmage, DefaultSideFor(outcome), IsReturnOutcome(outcome),
                spot.y, 0)
        {
        }

        public Systems_PlayEndedMessage(
            Systems_PlayOutcome outcome,
            Systems_PlayCall call,
            Vector2 spot,
            float netYards,
            int physicsTicks,
            bool passCompleted,
            Systems_PlayKind kind,
            Systems_TeamSide side,
            bool possessionChanged,
            float possessionChangeY,
            int foulCount)
        {
            Outcome = outcome;
            Call = call;
            Spot = spot;
            NetYards = netYards;
            PhysicsTicks = physicsTicks;
            PassCompleted = passCompleted;
            Kind = kind;
            Side = side;
            PossessionChanged = possessionChanged;
            PossessionChangeY = possessionChangeY;
            FoulCount = foulCount;
        }

        /// <summary>
        /// What the short constructor assumes about who has the ball, for the callers
        /// that predate revision 14: the defense after a turnover or a kick, and for
        /// a safety the defense's points; otherwise the offense.
        /// </summary>
        private static Systems_TeamSide DefaultSideFor(Systems_PlayOutcome outcome)
        {
            if (outcome == Systems_PlayOutcome.Safety || outcome.HandsTheBallOver())
            {
                return Systems_TeamSide.Defense;
            }

            return Systems_TeamSide.Offense;
        }

        private static bool IsReturnOutcome(Systems_PlayOutcome outcome)
        {
            return outcome == Systems_PlayOutcome.Interception
                || outcome == Systems_PlayOutcome.FumbleLost;
        }
    }
}
