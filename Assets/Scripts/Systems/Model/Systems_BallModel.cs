using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// Where the ball is and who has it. Split out from Systems_PlayModel in
    /// milestone 2, when the ball stopped being permanently welded to one player.
    ///
    /// Flight is straight-line at constant speed. This is a top-down view with no
    /// vertical axis to arc through, so a thrown ball travels along the plane and
    /// is either caught or ruled incomplete when it runs out of flight time.
    /// </summary>
    public sealed class Systems_BallModel
    {
        public Systems_BallState State { get; private set; } = Systems_BallState.Held;

        /// <summary>Formation slot of the holder. Meaningful only while Held.</summary>
        public int CarrierId { get; private set; }

        public Vector2 Position { get; private set; }

        /// <summary>Metres per second while InFlight; zero otherwise.</summary>
        public Vector2 Velocity { get; private set; }

        /// <summary>Physics ticks the current pass has been airborne.</summary>
        public int FlightTicks { get; private set; }

        /// <summary>Who threw the live pass, so the completion can be credited.</summary>
        public int ThrowerId { get; private set; } = -1;

        /// <summary>Where the throw was released from, for measuring air yards.</summary>
        public Vector2 ThrowOrigin { get; private set; }

        public bool IsHeld => State == Systems_BallState.Held;

        public bool IsInFlight => State == Systems_BallState.InFlight;

        public void AttachTo(int carrierId, Vector2 position)
        {
            State = Systems_BallState.Held;
            CarrierId = carrierId;
            Position = position;
            Velocity = Vector2.zero;
            FlightTicks = 0;
            ThrowerId = -1;
        }

        /// <summary>Follows the carrier each tick while the ball is held.</summary>
        public void FollowCarrier(Vector2 position)
        {
            Position = position;
        }

        public void Throw(int throwerId, Vector2 origin, Vector2 velocity)
        {
            State = Systems_BallState.InFlight;
            ThrowerId = throwerId;
            CarrierId = -1;
            ThrowOrigin = origin;
            Position = origin;
            Velocity = velocity;
            FlightTicks = 0;
        }

        public void AdvanceFlight(float deltaTime)
        {
            Position += Velocity * deltaTime;
            FlightTicks++;
        }

        public void MarkIncomplete()
        {
            State = Systems_BallState.Incomplete;
            Velocity = Vector2.zero;
        }
    }
}
