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

        /// <summary>
        /// Physics ticks to hold the dead ball before re-forming for the next snap.
        ///
        /// WHY THIS IS ON THIS INTERFACE. It is the same question the rest of this
        /// seam answers — "is anybody watching?" — and it has the same two answers.
        /// Training returns zero and the episode loop is exactly what it always was,
        /// tick for tick. A game returns a real pause.
        ///
        /// IT IS NOT COSMETIC, IT IS THE WHOLE READABILITY OF THE GAME. The director
        /// used to re-snap on the very FixedTick after the whistle, so every dead
        /// ball lasted 20 ms. The HUD's result banner is raised by the down
        /// resolving and taken down again by the next snap, which meant it was
        /// raised and lowered inside a single physics step: a viewer never once saw
        /// which down it was, what the call had been, or how many yards it gained,
        /// on any play of any game. Systems_HudView.BANNER_SECONDS had been dead
        /// code since the game layer was added.
        ///
        /// The play is Dead throughout, so the referee and the game clock both stand
        /// down and the pause costs the offense nothing — which is correct football
        /// as well as correct code.
        /// </summary>
        int DeadBallTicks { get; }
    }
}
