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

        /// <summary>Ball stripped on the tackle; the defense takes over at the spot.</summary>
        FumbleLost = 11
    }
}
