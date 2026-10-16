namespace PoFootball.Models
{
    /// <summary>
    /// What a completed play did to the *game*, as opposed to
    /// <see cref="Systems_PlayOutcome"/>, which says how the ball became dead.
    ///
    /// The two are independent axes and both are needed. A tackle
    /// (Systems_PlayOutcome.Tackle) can be a first down, a routine next down, or a
    /// turnover on downs depending entirely on where the chains were. The HUD
    /// announces this enum; telemetry records the other.
    /// </summary>
    public enum Systems_DownResult
    {
        /// <summary>Short of the chains, another down to play.</summary>
        NextDown = 0,

        /// <summary>Chains moved. Fresh set of downs, same offense.</summary>
        FirstDown = 1,

        Touchdown = 2,

        /// <summary>Fourth down came up short. Opponent takes over at the spot.</summary>
        TurnoverOnDowns = 3,

        /// <summary>A defender caught the pass. Opponent takes over where it was caught.</summary>
        Interception = 4,

        /// <summary>Carrier ruled down behind its own goal line. Two points the other way.</summary>
        Safety = 5,

        /// <summary>The play ended the quarter before it could change the down.</summary>
        EndOfQuarter = 6,

        /// <summary>Regulation expired.</summary>
        EndOfGame = 7,

        /// <summary>The offense punted it away. Not a turnover: giving the ball up
        /// on purpose to win field position is a decision, not a mistake, and the
        /// box score should not read it as one.</summary>
        Punt = 8,

        FieldGoalGood = 9,

        FieldGoalMissed = 10,

        /// <summary>Ball stripped and the defense came up with it; it takes over where the return ended.</summary>
        FumbleLost = 11,

        // --- Revision 14 -----------------------------------------------------

        /// <summary>A kickoff returned and downed. The receiving team's drive begins.</summary>
        Kickoff = 12,

        /// <summary>The kicking team came up with its own kick: an onside kick, a muffed punt.</summary>
        KickRecovered = 13,

        /// <summary>A kick blocked and the other side has it.</summary>
        BlockedKick = 14,

        /// <summary>Dead in the end zone off the other side's play: the ball at the 20, or the 35 off a kickoff.</summary>
        Touchback = 15,

        /// <summary>A flag decided the down. Systems_DownResolvedMessage says which.</summary>
        Penalty = 16,

        ExtraPointGood = 17,

        ExtraPointMissed = 18,

        TwoPointGood = 19,

        TwoPointFailed = 20,

        /// <summary>The defense returned a try the length of the field: two points to it.</summary>
        DefensiveTry = 21,

        /// <summary>A pass play where the quarterback was brought down before he threw.</summary>
        Sack = 22
    }
}
