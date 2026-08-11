namespace PoFootball.Models
{
    /// <summary>
    /// Where the match as a whole has got to. Distinct from
    /// <see cref="Systems_PlayPhase"/>, which is the lifecycle of one snap.
    /// </summary>
    public enum Systems_GamePhase
    {
        /// <summary>Built but not kicked off. The menu sits here.</summary>
        PreGame = 0,

        /// <summary>Clock live, downs being played.</summary>
        Playing = 1,

        /// <summary>Between the second and third quarters.</summary>
        Halftime = 2,

        /// <summary>Regulation over. The box score is final.</summary>
        Final = 3
    }
}
