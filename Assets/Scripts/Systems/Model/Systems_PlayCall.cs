namespace PoFootball.Models
{
    /// <summary>
    /// What the offense intends to do with the ball. The quarterback emits this as
    /// a discrete action and it is LATCHED for the rest of the play — the QB
    /// commits and cannot change its mind mid-down. A kick is latched before the
    /// snap; everything else at the dropback, having seen the defensive alignment.
    ///
    /// Every offensive player observes the call so blockers and receivers know
    /// whether to protect or to run routes. Defenders observe a zeroed slot, so the
    /// call is not leaked to them.
    /// </summary>
    public enum Systems_PlayCall
    {
        /// <summary>No call yet — the state between the snap and the QB's first decision.</summary>
        None = 0,

        /// <summary>The quarterback keeps the ball and runs.</summary>
        KeepQuarterback = 1,

        HandoffFullback = 2,

        HandoffHalfback = 3,

        Pass = 4,

        /// <summary>
        /// Kick it away. Since revision 14 a punt is PLAYED — the punt team lines up
        /// for it and the ball is fielded, fair-caught, muffed, downed or returned —
        /// and it is called BEFORE the snap, on fourth down only, because a punt is
        /// a formation rather than a decision made with the rush already on the
        /// quarterback. Agent_FootballPlayer masks it everywhere else.
        /// </summary>
        Punt = 5,

        /// <summary>
        /// Three points if the kick clears the crossbar between the uprights — a
        /// played kick since revision 14, called before the snap on fourth down, and
        /// masked out whenever the attempt would be longer than
        /// Systems_GameRules.FIELD_GOAL_MAX_YARDS. The try after a touchdown latches
        /// it on its own (Systems_PlayKind.ExtraPoint).
        /// </summary>
        FieldGoal = 6
    }
}
