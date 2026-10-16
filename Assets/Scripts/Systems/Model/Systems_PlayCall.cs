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

        /// <summary>
        /// The short game. Since revision 14 the receivers' route reward is paid
        /// underneath Systems_SimConstants.DEEP_SHOT_MIN_YARDS on this call and
        /// beyond it on <see cref="PassDeep"/>; the throw itself is the same.
        /// </summary>
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
        FieldGoal = 6,

        /// <summary>
        /// The deep shot (revision 14). Through revision 13 there was one Pass and
        /// "deep" was something the scripted quarterback did on every fourth
        /// episode index, which no trained offense could ask for or be told about.
        /// Appended rather than placed beside Pass so no existing value moves.
        /// </summary>
        PassDeep = 7
    }

    public static class Systems_PlayCallExtensions
    {
        /// <summary>Either pass call: the ones on which the quarterback may throw.</summary>
        public static bool IsPass(this Systems_PlayCall call)
        {
            return call == Systems_PlayCall.Pass || call == Systems_PlayCall.PassDeep;
        }
    }
}
