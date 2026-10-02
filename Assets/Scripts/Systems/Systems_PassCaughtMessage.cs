namespace PoFootball.Systems
{
    /// <summary>
    /// Published the tick a thrown ball is caught, by either side.
    ///
    /// WHY A CATCH NEEDED ITS OWN MESSAGE. A completion is not one of the
    /// Systems_PlayOutcome values and never ends a play by itself — the receiver
    /// catches it and keeps running — so until this existed the only trace it left
    /// was a flag on Systems_PlayModel that nothing could subscribe to. The most
    /// watched second of a passing play, the ball in the air, resolved in silence
    /// and the viewer learned whether it had been caught from the whistle that
    /// followed the tackle.
    ///
    /// An interception publishes this AND ends the play on the same tick, so its
    /// subscribers hear it immediately before Systems_PlayEndedMessage.
    /// </summary>
    public readonly struct Systems_PassCaughtMessage
    {
        public readonly int CatcherId;

        /// <summary>True when a defender took it.</summary>
        public readonly bool Intercepted;

        /// <summary>
        /// Straight-line distance the ball flew from release to the catch, in
        /// yards. The length of the throw, not the downfield gain: a screen caught
        /// behind the line still travelled.
        /// </summary>
        public readonly float AirYards;

        public Systems_PassCaughtMessage(int catcherId, bool intercepted, float airYards)
        {
            CatcherId = catcherId;
            Intercepted = intercepted;
            AirYards = airYards;
        }
    }
}
