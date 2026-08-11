using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// Where the ball is and who has it. Split out from Systems_PlayModel in
    /// milestone 2, when the ball stopped being permanently welded to one player.
    ///
    /// Flight is straight-line at constant speed across the plane: a thrown ball is
    /// either caught or ruled incomplete when it runs out of flight time.
    ///
    /// <see cref="Height"/> IS PRESENTATION ONLY. The simulation is 2D and stays
    /// 2D — catching, interception and the incompletion rule all measure plane
    /// distance and none of them read the height. It exists so a pass reads as
    /// going OVER the players between thrower and receiver instead of sliding
    /// through them, which in a top-down view is otherwise impossible to see.
    ///
    /// Deliberately NOT a catch gate. Making height decide who can reach the ball
    /// would put a third axis into a contract every brain was fitted against
    /// (Sensor_FootballState emits a 2D ball position), and would change what
    /// Systems_BallSystem.FindCatcher means mid-project. MIN_CATCH_DISTANCE already
    /// stops the "caught instantly by whoever stands next to the quarterback"
    /// failure this would otherwise be solving twice.
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

        /// <summary>
        /// Metres above the turf. Zero unless a pass is in the air. Presentation
        /// only — see the class summary.
        /// </summary>
        public float Height { get; private set; }

        /// <summary>
        /// Vertical speed of the fake third axis, integrated by AdvanceFlight. Held
        /// here rather than recomputed from FlightTicks so the arc is a genuine
        /// projectile that lands on its own, rather than a curve stretched over a
        /// flight length nobody knows in advance: a pass ends when someone catches
        /// it, which is not a time that can be predicted at release.
        /// </summary>
        private float _verticalVelocity;

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
            Height = 0f;
            _verticalVelocity = 0f;
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

            // Loft scales with how hard the ball was thrown, so a short flick stays
            // flat and a deep ball climbs — the arc reads as the throw it belongs to
            // rather than every pass rainbowing identically.
            Height = 0f;
            _verticalVelocity = velocity.magnitude * Systems_SimConstants.PASS_LOFT_RATIO;
        }

        public void AdvanceFlight(float deltaTime)
        {
            Position += Velocity * deltaTime;
            FlightTicks++;

            _verticalVelocity -= Systems_SimConstants.PASS_GRAVITY * deltaTime;
            Height = Mathf.Max(0f, Height + (_verticalVelocity * deltaTime));
        }

        public void MarkIncomplete()
        {
            State = Systems_BallState.Incomplete;
            Velocity = Vector2.zero;
            Height = 0f;
            _verticalVelocity = 0f;
        }
    }
}
