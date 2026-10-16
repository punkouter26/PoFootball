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
    /// <see cref="Height"/> IS PRESENTATION ONLY FOR A PASS. The simulation is 2D
    /// and catching, interception and the incompletion rule all measure plane
    /// distance and none of them read the height of a thrown ball. It exists so a
    /// pass reads as going OVER the players between thrower and receiver instead of
    /// sliding through them, which in a top-down view is otherwise impossible to see.
    ///
    /// A KICKED BALL IS DIFFERENT, and that is revision 14. A punt that hangs four
    /// seconds is not catchable for most of those four seconds, and whether a field
    /// goal clears the crossbar is the whole of whether it is good, so for
    /// <see cref="Systems_BallState.Kicked"/> the height is a real third axis under
    /// real gravity, read by Systems_BallSystem.
    /// </summary>
    public sealed class Systems_BallModel
    {
        public Systems_BallState State { get; private set; } = Systems_BallState.Held;

        /// <summary>Formation slot of the holder. Meaningful only while Held.</summary>
        public int CarrierId { get; private set; }

        public Vector2 Position { get; private set; }

        /// <summary>Metres per second across the turf while InFlight, Kicked or Loose; zero while Held.</summary>
        public Vector2 Velocity { get; private set; }

        /// <summary>Physics ticks the current pass or kick has been airborne, or the ball has been loose.</summary>
        public int FlightTicks { get; private set; }

        /// <summary>Who threw or kicked it last, so the completion can be credited.</summary>
        public int ThrowerId { get; private set; } = -1;

        /// <summary>Where the throw or kick was released from, for measuring its length.</summary>
        public Vector2 ThrowOrigin { get; private set; }

        /// <summary>Forward pass or lateral, for the ball currently in flight.</summary>
        public Systems_PassKind PassKind { get; private set; }

        /// <summary>
        /// Furthest the ball has travelled up the field (+Y) during the current pass.
        /// Intentional grounding asks whether a throwaway reached the line of
        /// scrimmage, and where the ball happens to land does not answer that.
        /// </summary>
        public float FurthestFlightY { get; private set; }

        /// <summary>
        /// The side whose hands, foot or fumble last put the ball where it is — the
        /// IMPETUS, in the rulebook's word. While the ball is held it is simply the
        /// carrier's side. Touchback against safety turns on it.
        /// </summary>
        public Systems_TeamSide PossessingSide { get; private set; } = Systems_TeamSide.Offense;

        /// <summary>
        /// Bumped whenever the ball changes sides, is kicked, or comes loose. The
        /// dense yardage reward re-baselines on a change rather than paying the
        /// distance across it: forty-five yards of punt in the air is not the
        /// offense driving the ball, and an interception is not the defense's gain
        /// until it is returned.
        /// </summary>
        public int PossessionEpoch { get; private set; }

        /// <summary>
        /// Metres above the turf. Zero unless a pass or kick is in the air.
        /// Presentation only for a pass; real for a kick — see the class summary.
        /// </summary>
        public float Height { get; private set; }

        /// <summary>
        /// Vertical speed of the third axis, integrated by AdvanceFlight. Held here
        /// rather than recomputed from FlightTicks so the arc is a genuine
        /// projectile that lands on its own.
        /// </summary>
        public float VerticalVelocity { get; private set; }

        public bool IsHeld => State == Systems_BallState.Held;

        public bool IsInFlight => State == Systems_BallState.InFlight;

        public bool IsKicked => State == Systems_BallState.Kicked;

        public bool IsLoose => State == Systems_BallState.Loose;

        /// <summary>In the air off a hand or a foot.</summary>
        public bool IsAirborne => State == Systems_BallState.InFlight || State == Systems_BallState.Kicked;

        /// <summary>
        /// Puts the ball in a carrier's hands. The side is the carrier's; a change of
        /// side bumps <see cref="PossessionEpoch"/>.
        /// </summary>
        public void AttachTo(int carrierId, Vector2 position, Systems_TeamSide side)
        {
            if (side != PossessingSide || State == Systems_BallState.Kicked
                || State == Systems_BallState.Loose)
            {
                PossessionEpoch++;
            }

            State = Systems_BallState.Held;
            CarrierId = carrierId;
            PossessingSide = side;
            Position = position;
            Velocity = Vector2.zero;
            FlightTicks = 0;
            ThrowerId = -1;
            Height = 0f;
            VerticalVelocity = 0f;
        }

        /// <summary>
        /// The snap: the ball starts a new play in the offense's hands with nothing
        /// carried over from the last one.
        /// </summary>
        public void ResetForSnap(int carrierId, Vector2 position)
        {
            PossessionEpoch++;
            PossessingSide = Systems_TeamSide.Offense;
            AttachTo(carrierId, position, Systems_TeamSide.Offense);
        }

        /// <summary>Follows the carrier each tick while the ball is held.</summary>
        public void FollowCarrier(Vector2 position)
        {
            Position = position;
        }

        public void Throw(
            int throwerId, Vector2 origin, Vector2 velocity, Systems_PassKind kind)
        {
            State = Systems_BallState.InFlight;
            ThrowerId = throwerId;
            CarrierId = -1;
            ThrowOrigin = origin;
            Position = origin;
            Velocity = velocity;
            FlightTicks = 0;
            PassKind = kind;
            FurthestFlightY = origin.y;

            // Loft scales with how hard the ball was thrown, so a short flick stays
            // flat and a deep ball climbs — the arc reads as the throw it belongs to
            // rather than every pass rainbowing identically.
            Height = 0f;
            VerticalVelocity = velocity.magnitude * Systems_SimConstants.PASS_LOFT_RATIO;
        }

        /// <summary>
        /// A kick leaves the foot. <paramref name="groundVelocity"/> is its speed
        /// across the turf and <paramref name="verticalSpeed"/> its launch speed
        /// upward; Systems_BallSystem chose both to land where the kicker meant it,
        /// give or take what Systems_ISkillModel took off it.
        /// </summary>
        public void Kick(
            int kickerId, Vector2 origin, Vector2 groundVelocity, float verticalSpeed,
            Systems_TeamSide side)
        {
            State = Systems_BallState.Kicked;
            ThrowerId = kickerId;
            CarrierId = -1;
            ThrowOrigin = origin;
            Position = origin;
            Velocity = groundVelocity;
            FlightTicks = 0;
            PossessingSide = side;
            PossessionEpoch++;
            Height = Systems_SimConstants.KICK_LAUNCH_HEIGHT;
            VerticalVelocity = verticalSpeed;
        }

        public void AdvanceFlight(float deltaTime)
        {
            Position += Velocity * deltaTime;
            FlightTicks++;

            if (Position.y > FurthestFlightY)
            {
                FurthestFlightY = Position.y;
            }

            if (State == Systems_BallState.Kicked)
            {
                // A real projectile: allowed below zero so the caller sees the tick
                // it landed on rather than a ball that skims the turf forever.
                VerticalVelocity -= Systems_SimConstants.KICK_GRAVITY * deltaTime;
                Height += VerticalVelocity * deltaTime;
                return;
            }

            VerticalVelocity -= Systems_SimConstants.PASS_GRAVITY * deltaTime;
            Height = Mathf.Max(0f, Height + (VerticalVelocity * deltaTime));
        }

        public void MarkIncomplete()
        {
            State = Systems_BallState.Incomplete;
            Velocity = Vector2.zero;
            Height = 0f;
            VerticalVelocity = 0f;
        }

        /// <summary>
        /// The ball is on the ground and live. <paramref name="impetusSide"/> is
        /// whoever put it there — the fumbling side, the kicking side.
        /// </summary>
        public void MakeLoose(Vector2 position, Vector2 velocity, Systems_TeamSide impetusSide)
        {
            State = Systems_BallState.Loose;
            CarrierId = -1;
            Position = position;
            Velocity = velocity;
            FlightTicks = 0;
            Height = 0f;
            VerticalVelocity = 0f;
            PossessingSide = impetusSide;
            PossessionEpoch++;
        }

        /// <summary>A loose ball rolls and slows against the turf.</summary>
        public void AdvanceLoose(float deltaTime)
        {
            Position += Velocity * deltaTime;
            FlightTicks++;

            float speed = Velocity.magnitude;

            if (speed <= 0.0001f)
            {
                Velocity = Vector2.zero;
                return;
            }

            float slowed = Mathf.Max(
                0f, speed - (Systems_SimConstants.LOOSE_BALL_FRICTION * deltaTime));

            Velocity = Velocity * (slowed / speed);
        }
    }
}
