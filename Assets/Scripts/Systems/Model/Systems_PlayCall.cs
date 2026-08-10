namespace PoFootball.Models
{
    /// <summary>
    /// What the offense intends to do with the ball. The quarterback emits this as
    /// a discrete action on the first decision step after the snap, and it is
    /// LATCHED for the rest of the play — the QB commits, having seen the defensive
    /// alignment, and cannot change its mind mid-down.
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

        Pass = 4
    }
}
