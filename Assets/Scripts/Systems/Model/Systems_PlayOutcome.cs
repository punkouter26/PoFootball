namespace PoFootball.Models
{
    /// <summary>
    /// How a play ended. Every episode terminates with exactly one of these —
    /// there is no silent truncation (acceptance criterion #6).
    ///
    /// Incompletion and Interception were added in milestone 2 with the passing
    /// game; before that the ball could never leave a carrier's hands.
    ///
    /// SINCE REVISION 14 THE BALL CAN CHANGE HANDS AND KEEP GOING. An interception
    /// or a recovered fumble is no longer the end of a play but the start of a
    /// return, so <see cref="Interception"/> and <see cref="FumbleLost"/> now mean
    /// "the ball changed sides and the return ended without a score", with the spot
    /// where the RETURN ended. A return that scores is a <see cref="Touchdown"/>,
    /// and Systems_PlayModel.ScoringSide says whose.
    /// </summary>
    public enum Systems_PlayOutcome
    {
        None = 0,
        Tackle = 1,

        /// <summary>Whoever had the ball crossed the goal line he was attacking. See ScoringSide.</summary>
        Touchdown = 2,

        OutOfBounds = 3,

        /// <summary>
        /// The trainer's ceiling and nothing else: Systems_PlayModel.MAX_PHYSICS_TICKS.
        /// Since revision 14 an ordinary down never ends this way — a carrier held
        /// up is down by forward progress (a Tackle), and one standing still with
        /// nobody near has given himself up (<see cref="Downed"/>).
        /// </summary>
        TimeExpired = 4,

        /// <summary>A forward pass hit the ground uncaught, or was spiked.</summary>
        Incompletion = 5,

        /// <summary>A defender caught a pass and the return ended short of a score.</summary>
        Interception = 6,

        /// <summary>
        /// The carrier was ruled down in the end zone he was defending, with the
        /// impetus his own side's: two points to the other side and the ball. A
        /// return man can concede one too. See ScoringSide.
        ///
        /// A DISTINCT OUTCOME BECAUSE THE REWARD LAYER HAD NO WAY TO SEE IT. The
        /// rules layer has always recognised a safety — Systems_GameFlowSystem
        /// derives one from where the ball stopped — but it did so AFTER the play
        /// outcome had already been reported as an ordinary Tackle, so
        /// Reward_Terminal priced conceding two points and the ball exactly the
        /// same as a one-yard loss at midfield.
        /// </summary>
        Safety = 7,

        /// <summary>A punt play ended with the receiving team holding the ball at the spot.</summary>
        Punt = 8,

        /// <summary>A placekick went over the crossbar and between the uprights.</summary>
        FieldGoalGood = 9,

        /// <summary>
        /// A placekick that did not. The defense takes over at the spot of the kick —
        /// missing from distance costs field position, which is what makes the
        /// attempt a real decision rather than a free roll.
        /// </summary>
        FieldGoalMissed = 10,

        /// <summary>
        /// The ball came loose, the other side recovered it, and the return ended
        /// short of a score. Since revision 14 a fumble the offense falls on is just
        /// the play continuing, and is not this.
        /// </summary>
        FumbleLost = 11,

        /// <summary>The quarterback was brought down behind the line on a pass he never threw.</summary>
        Sack = 12,

        /// <summary>
        /// The ball died in an end zone with the impetus from the other side — a kick
        /// into it, an interception downed in it. The defending team takes over at
        /// its own twenty, or its own thirty-five off a kickoff.
        /// </summary>
        Touchback = 13,

        /// <summary>A kickoff ended with the receiving team holding the ball at the spot.</summary>
        Kickoff = 14,

        /// <summary>
        /// The kicking team came up with its own kick: an onside kick, or a muff it
        /// fell on. It keeps the ball where it recovered it.
        /// </summary>
        KickRecovered = 15,

        /// <summary>
        /// A kick was blocked and the receiving side ended up with it. The ball goes
        /// over where the return ended.
        /// </summary>
        KickBlocked = 16,

        /// <summary>
        /// A foul before the snap killed the play: a false start, an offside, a
        /// delay of game. Systems_PlayModel's foul list says which.
        /// </summary>
        DeadBallFoul = 17,

        /// <summary>
        /// The carrier gave himself up: a kneel-down, or standing still with the play
        /// over and nobody near enough to tackle him. Down where he is.
        /// </summary>
        Downed = 18
    }

    public static class Systems_PlayOutcomeExtensions
    {
        /// <summary>
        /// Outcomes after which the other team has the ball for its own snap — every
        /// one of them stops the clock.
        /// </summary>
        public static bool HandsTheBallOver(this Systems_PlayOutcome outcome)
        {
            switch (outcome)
            {
                case Systems_PlayOutcome.Interception:
                case Systems_PlayOutcome.FumbleLost:
                case Systems_PlayOutcome.Punt:
                case Systems_PlayOutcome.FieldGoalMissed:
                case Systems_PlayOutcome.Touchback:
                case Systems_PlayOutcome.Kickoff:
                case Systems_PlayOutcome.KickBlocked:
                    return true;

                default:
                    return false;
            }
        }
    }
}
