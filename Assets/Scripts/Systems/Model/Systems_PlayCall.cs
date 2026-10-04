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

        /// <summary>
        /// The short game. Since revision 14 the receivers' route reward is paid
        /// underneath Systems_SimConstants.DEEP_SHOT_MIN_YARDS on this call and
        /// beyond it on <see cref="PassDeep"/>; the throw itself is the same.
        /// </summary>
        Pass = 4,

        /// <summary>
        /// Kick it away and give the other side the ball. Resolved at the rules
        /// layer rather than simulated — there is no kicking model here, and
        /// inventing a random one would put noise into field position that no
        /// policy can influence (the same reason the extra point is awarded).
        /// Legal on fourth down only; Agent_FootballPlayer masks it everywhere else.
        /// </summary>
        Punt = 5,

        /// <summary>
        /// Three points from where the ball is spotted. Masked out on downs one to
        /// three, and masked out on fourth down whenever the attempt would be
        /// longer than Systems_GameRules.FIELD_GOAL_MAX_YARDS.
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
