namespace PoFootball.Systems
{
    /// <summary>
    /// Decides where the next play is snapped from.
    ///
    /// This interface is the entire seam between the training rig and the game.
    /// <see cref="Systems_EpisodeDirector"/> used to draw the line of scrimmage
    /// from its own RNG inline; it now asks whoever is bound here. Training binds
    /// <see cref="Systems_RandomSpotProvider"/>, which reproduces that RNG call
    /// for call, so the distribution of starting states a policy trains against is
    /// unchanged. A game binds Systems_GameFlowSystem, which returns wherever the
    /// chains and the last whistle left the ball.
    ///
    /// The director does not know which it got, and neither does any agent.
    /// </summary>
    public interface Systems_ISpotProvider
    {
        /// <summary>
        /// Whether there is another play to snap at all.
        ///
        /// Training is endless and always answers true. A game answers false once
        /// the clock has run out in the fourth quarter, and that is what stops the
        /// simulation — without it the director kept re-forming the teams and
        /// snapping downs forever behind the FINAL overlay, incrementing the play
        /// count past the box score that had already been totalled.
        ///
        /// Asked here rather than checked inside the director because the director
        /// has no game model to consult; whether the contest is over is knowledge
        /// that lives on the same side of this seam as the chains do.
        /// </summary>
        bool HasNextPlay { get; }

        /// <summary>
        /// Y of the next line of scrimmage, in the attacking frame (offense drives
        /// toward +Y). Called by the director exactly once per episode, immediately
        /// before the formation is laid out, and only when <see cref="HasNextPlay"/>
        /// is true.
        /// </summary>
        float NextLineOfScrimmageY();
    }
}
