using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// One player's most recent policy output, recorded verbatim so presentation
    /// can draw it.
    ///
    /// THIS IS THE ACTION VECTOR, NOT A DERIVED GUESS AT INTENT. The obvious way
    /// to draw "where is this player trying to go" is to read
    /// Agent_FootballPlayer.TargetPoint, which is a clean world-space destination
    /// — and which only exists on the Heuristic path. A trained .onnx never runs
    /// it, so an overlay built on it would show the scripted fallback's plan while
    /// claiming to show the brain's. Drive and steer are what BOTH paths emit and
    /// what OnActionReceived actually applies to the rigidbody, so an arrow drawn
    /// from them is the policy's decision and nothing else.
    ///
    /// STEERING IS CAR-LIKE, which is why this carries a facing as well as the two
    /// scalars. Drive is a force along the body's own up axis and steer is a
    /// torque, so neither number means anything without knowing which way the body
    /// was pointing when they were chosen — and by the time a view runs, the
    /// rigidbody has already turned.
    ///
    /// A struct, and copied by value into a fixed array, because it is written
    /// twenty-two times per decision step at time_scale 20 and nothing in that
    /// path may allocate (acceptance criterion #17).
    /// </summary>
    public readonly struct Systems_PlayerIntent
    {
        /// <summary>Continuous action 0, clamped to [-1, 1]. Forward force.</summary>
        public readonly float Drive;

        /// <summary>Continuous action 1, clamped to [-1, 1]. Turn torque.</summary>
        public readonly float Steer;

        /// <summary>The body's up axis at the instant the decision was applied.</summary>
        public readonly Vector2 Facing;

        /// <summary>
        /// The quarterback's continuous actions 2 and 3 — the aim vector. Zero for
        /// every other player, which <see cref="HasAim"/> distinguishes from a
        /// quarterback that genuinely emitted zero.
        /// </summary>
        public readonly Vector2 Aim;

        /// <summary>Whether this player has the quarterback's extra action slots.</summary>
        public readonly bool HasAim;

        /// <summary>Discrete branch 1 came back as "release" on this decision.</summary>
        public readonly bool ThrowArmed;

        /// <summary>
        /// Systems_PlayModel.PhysicsTick when this was written. The only thing that
        /// separates a live intent from one left over by the previous play — the
        /// tick counter restarts at zero every snap, so a stale entry reads as
        /// being from the future and is rejected without anyone having to clear
        /// the array on reset.
        /// </summary>
        public readonly int Tick;

        public Systems_PlayerIntent(
            float drive,
            float steer,
            Vector2 facing,
            Vector2 aim,
            bool hasAim,
            bool throwArmed,
            int tick)
        {
            Drive = drive;
            Steer = steer;
            Facing = facing;
            Aim = aim;
            HasAim = hasAim;
            ThrowArmed = throwArmed;
            Tick = tick;
        }
    }
}
