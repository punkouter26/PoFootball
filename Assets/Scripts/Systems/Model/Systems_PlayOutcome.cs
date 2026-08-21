namespace PoFootball.Models
{
    /// <summary>
    /// How a play ended. Every episode terminates with exactly one of these —
    /// there is no silent truncation (acceptance criterion #6).
    ///
    /// Incompletion and Interception were added in milestone 2 with the passing
    /// game; before that the ball could never leave a carrier's hands.
    /// </summary>
    public enum Systems_PlayOutcome
    {
        None = 0,
        Tackle = 1,
        Touchdown = 2,
        OutOfBounds = 3,
        TimeExpired = 4,

        /// <summary>A pass hit the ground uncaught.</summary>
        Incompletion = 5,

        /// <summary>A defender caught a pass. The biggest single swing in the game.</summary>
        Interception = 6,

        /// <summary>
        /// The carrier was ruled down on or behind its own goal line: two points to
        /// the defense and the ball back.
        ///
        /// A DISTINCT OUTCOME BECAUSE THE REWARD LAYER HAD NO WAY TO SEE IT. The
        /// rules layer has always recognised a safety — Systems_GameFlowSystem
        /// derives one from where the ball stopped — but it did so AFTER the play
        /// outcome had already been reported as an ordinary Tackle, so
        /// Reward_Terminal priced conceding two points and the ball exactly the
        /// same as a one-yard loss at midfield. Nothing in the gradient told a
        /// quarterback that the grass behind it was different from any other grass,
        /// and a quarterback that had not yet learned otherwise would happily
        /// retreat into its own end zone.
        /// </summary>
        Safety = 7,

        /// <summary>The offense punted. Possession changes; nobody scores.</summary>
        Punt = 8,

        /// <summary>Field goal attempt that was inside range. Three points.</summary>
        FieldGoalGood = 9,

        /// <summary>
        /// Field goal attempt that was not. No points, and the defense takes over
        /// at the spot of the kick rather than at the spot of the ball — missing
        /// from distance costs field position, which is what makes the attempt a
        /// real decision rather than a free roll.
        /// </summary>
        FieldGoalMissed = 10
    }
}
