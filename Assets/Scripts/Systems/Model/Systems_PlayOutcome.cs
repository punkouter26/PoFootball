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
        Interception = 6
    }
}
