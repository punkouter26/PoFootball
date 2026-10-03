using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published once per play by <see cref="Systems_GameFlowSystem"/>, after the
    /// whistle has been turned into a change of down, a score, or a turnover.
    ///
    /// Distinct from <see cref="Systems_PlayEndedMessage"/>, which the referee
    /// publishes and which says only how the ball became dead. This one says what
    /// it meant. Presentation subscribes here — the HUD banner, the crowd reaction
    /// and the stats layer all key off the game consequence, not the physics
    /// event.
    /// </summary>
    public readonly struct Systems_DownResolvedMessage
    {
        public readonly Systems_DownResult Result;

        /// <summary>The team that was on offense for this play.</summary>
        public readonly Systems_TeamId Offense;

        public readonly Systems_PlayOutcome Outcome;

        public readonly Systems_PlayCall Call;

        public readonly float YardsGained;

        /// <summary>Points this play put on the board, including the automatic try.</summary>
        public readonly int PointsScored;

        /// <summary>Down about to be played once the ball is spotted, 1-based.</summary>
        public readonly int NextDown;

        /// <summary>Yards needed on that next down.</summary>
        public readonly float NextYardsToGo;

        /// <summary>
        /// Game clock this play consumed, huddle included. Carried on the message
        /// so the stats layer can bill time of possession without duplicating the
        /// clock rules that Systems_GameFlowSystem owns.
        /// </summary>
        public readonly float ClockSecondsBurned;

        /// <summary>
        /// A team stopped the clock after this play. Carried here rather than as a
        /// message of its own because it is a fact about this whistle: the banner
        /// that announces the play is the only place a viewer would look for it.
        /// </summary>
        public readonly bool TimeoutCalled;

        /// <summary>Who called it. Meaningless unless <see cref="TimeoutCalled"/>.</summary>
        public readonly Systems_TeamId TimeoutTeam;

        /// <summary>The scoring team went for two rather than taking the kick.</summary>
        public readonly bool TwoPointAttempted;

        public readonly bool TwoPointGood;

        public Systems_DownResolvedMessage(
            Systems_DownResult result,
            Systems_TeamId offense,
            Systems_PlayOutcome outcome,
            Systems_PlayCall call,
            float yardsGained,
            int pointsScored,
            int nextDown,
            float nextYardsToGo,
            float clockSecondsBurned,
            bool timeoutCalled = false,
            Systems_TeamId timeoutTeam = Systems_TeamId.Home,
            bool twoPointAttempted = false,
            bool twoPointGood = false)
        {
            Result = result;
            Offense = offense;
            Outcome = outcome;
            Call = call;
            YardsGained = yardsGained;
            PointsScored = pointsScored;
            NextDown = nextDown;
            NextYardsToGo = nextYardsToGo;
            ClockSecondsBurned = clockSecondsBurned;
            TimeoutCalled = timeoutCalled;
            TimeoutTeam = timeoutTeam;
            TwoPointAttempted = twoPointAttempted;
            TwoPointGood = twoPointGood;
        }
    }
}
