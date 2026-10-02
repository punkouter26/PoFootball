namespace PoFootball.Systems
{
    /// <summary>
    /// Published the tick a pass leaves the quarterback's hand.
    ///
    /// Carries the receiver the throw was RESOLVED to, not merely the direction it
    /// went. Systems_BallSystem.ResolveThrowTarget picks that receiver at release
    /// and then forgets it — the ball model only stores a velocity — so by the next
    /// frame nothing in the simulation can say who the pass was for. The broadcast
    /// camera needs exactly that to frame both ends of the throw, and re-resolving
    /// it from a view would mean asking the question again with players who have
    /// already moved.
    /// </summary>
    public readonly struct Systems_PassThrownMessage
    {
        public readonly int ThrowerId;

        /// <summary>
        /// Formation slot of the intended receiver, or -1 when no eligible receiver
        /// was on the field and the ball simply went where it was pointed.
        /// </summary>
        public readonly int TargetId;

        /// <summary>Release speed in metres per second, for scaling the cue.</summary>
        public readonly float Speed;

        public Systems_PassThrownMessage(int throwerId, int targetId, float speed)
        {
            ThrowerId = throwerId;
            TargetId = targetId;
            Speed = speed;
        }
    }
}
