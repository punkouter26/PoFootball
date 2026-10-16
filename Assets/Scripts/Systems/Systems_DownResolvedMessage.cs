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

        /// <summary>Points this play put on the board. A try is its own play since revision 14.</summary>
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

        /// <summary>
        /// The flag that decided the down, or None. Revision 14: before it no foul
        /// existed to announce.
        /// </summary>
        public readonly Systems_FoulType Foul;

        /// <summary>The team flagged. Meaningless when <see cref="Foul"/> is None.</summary>
        public readonly Systems_TeamId FoulTeam;

        /// <summary>Whether the other team took the penalty rather than the play.</summary>
        public readonly bool PenaltyAccepted;

        /// <summary>Yards the accepted penalty moved the ball, after half-the-distance.</summary>
        public readonly float PenaltyYards;

        /// <summary>The clock stopped for the two-minute warning after this play.</summary>
        public readonly bool TwoMinuteWarning;

        /// <summary>
        /// What sort of snap it was. A kickoff or a try is not a down, and the box
        /// score must not book one as a carry.
        /// </summary>
        public readonly Systems_PlayKind Kind;

        /// <summary>
        /// Whose points <see cref="PointsScored"/> are. Not always the offense's
        /// since revision 14: a pick-six, a safety by a returner, a try taken back.
        /// </summary>
        public readonly Systems_TeamId ScoringTeam;

        /// <summary>
        /// What the down itself came to, before the clock had its say. Equal to
        /// <see cref="Result"/> except on the play a period or the game ended on:
        /// the HUD announces that as END OF QUARTER or FINAL, but the box score
        /// still owes the first down or the turnover that happened on it, and the
        /// crowd still owes the touchdown its roar.
        /// </summary>
        public readonly Systems_DownResult PlayResult;

        /// <summary>
        /// Yards the side that took the ball away carried it after it got it — a
        /// kick return, an interception or fumble return. Zero when the ball never
        /// changed hands. Never negative: a returner who gave ground was still
        /// returned for nothing.
        /// </summary>
        public readonly float ReturnYards;

        /// <summary>An accepted foul by the defense moved the chains.</summary>
        public readonly bool PenaltyFirstDown;

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
            Systems_FoulType foul = Systems_FoulType.None,
            Systems_TeamId foulTeam = Systems_TeamId.Home,
            bool penaltyAccepted = false,
            float penaltyYards = 0f,
            bool twoMinuteWarning = false,
            Systems_PlayKind kind = Systems_PlayKind.Scrimmage,
            Systems_TeamId? scoringTeam = null,
            Systems_DownResult? playResult = null,
            float returnYards = 0f,
            bool penaltyFirstDown = false)
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
            Foul = foul;
            FoulTeam = foulTeam;
            PenaltyAccepted = penaltyAccepted;
            PenaltyYards = penaltyYards;
            TwoMinuteWarning = twoMinuteWarning;
            Kind = kind;
            ScoringTeam = scoringTeam ?? offense;
            PlayResult = playResult ?? result;
            ReturnYards = returnYards;
            PenaltyFirstDown = penaltyFirstDown;
        }
    }
}
