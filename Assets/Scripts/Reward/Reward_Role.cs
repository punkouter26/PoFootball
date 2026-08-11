using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Rewards
{
    /// <summary>
    /// Per-agent credit assignment: the small dense terms that tell one player on a
    /// side apart from the other ten.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// Reward_Terminal pays every player on a side the same number, and
    /// Reward_Progress pays every player on a side the same number. Up to base03
    /// that was the entire reward, which means the optimizer had no signal
    /// whatsoever distinguishing a guard who held his block from a guard who
    /// jogged sideways, or a receiver who beat his corner from one who stood
    /// still — a touchdown smeared its credit evenly across all eleven. The only
    /// player with an individually identifiable contribution was whoever happened
    /// to be carrying the ball.
    ///
    /// These terms are deliberately an order of magnitude below the terminal
    /// rewards. They exist to shape which behaviours get EXPLORED, not to decide
    /// who wins the play; if a shaped term ever becomes worth more than a
    /// touchdown, the policy will farm it and ignore the game.
    ///
    /// Pure scalar functions — no state, no allocation, no registry access. The
    /// caller does the geometry, which keeps PoFootball.Rewards a leaf assembly
    /// that depends on nothing but PoFootball.Models.
    /// </summary>
    public static class Reward_Role
    {
        /// <summary>
        /// Whether this role earns the blocking term. Linemen only — a receiver
        /// standing near a defender is running a route, not blocking.
        /// </summary>
        public static bool IsBlocker(Systems_PlayerRole role)
        {
            return role == Systems_PlayerRole.OffensiveLine;
        }

        /// <summary>
        /// Whether this role earns the separation term while it is not the carrier.
        /// </summary>
        public static bool IsReceiver(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.WideReceiver:
                case Systems_PlayerRole.TightEnd:
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Fullback:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Reward for one tick of shielding the ball from the nearest rusher.
        ///
        /// Paid only when the blocker is BETWEEN the rusher and the ball, which is
        /// what "block" actually means — standing next to a defender earns nothing.
        /// Alignment is the cosine between (blocker - rusher) and (ball - rusher):
        /// at 1 the blocker sits squarely on the rusher's path to the ball, at 0 it
        /// is off to the side, and below 0 it has been beaten.
        /// </summary>
        public static float Block(
            Vector2 blockerPosition, Vector2 rusherPosition, Vector2 ballPosition)
        {
            Vector2 toBlocker = blockerPosition - rusherPosition;
            float distance = toBlocker.magnitude;

            if (distance > Systems_SimConstants.BLOCK_ENGAGE_RANGE || distance < 1e-4f)
            {
                return 0f;
            }

            Vector2 toBall = ballPosition - rusherPosition;
            if (toBall.sqrMagnitude < 1e-4f)
            {
                return 0f;
            }

            float alignment = Vector2.Dot(toBlocker / distance, toBall.normalized);
            if (alignment <= 0f)
            {
                return 0f;
            }

            return alignment * Systems_SimConstants.BLOCK_REWARD_PER_TICK;
        }

        /// <summary>
        /// Reward for one tick of separation from the nearest defender, saturating
        /// at SEPARATION_SATURATION_RANGE so a receiver cannot farm it by running
        /// to an empty corner of the field — past the saturation point extra
        /// distance is worth nothing.
        /// </summary>
        public static float Separation(float distanceToNearestDefender)
        {
            float saturated = Mathf.Clamp01(
                distanceToNearestDefender / Systems_SimConstants.SEPARATION_SATURATION_RANGE);

            return saturated * Systems_SimConstants.SEPARATION_REWARD_PER_TICK;
        }

        /// <summary>
        /// Signed reward for closing on the ball over one tick. metresClosed is the
        /// previous distance minus the current one, so drifting away is a small
        /// negative — which is the half that matters. A purely positive pursuit
        /// term is satisfied by a defender who sits still and lets the play come to
        /// it; making retreat cost something is what forces the defense to commit.
        /// </summary>
        public static float Pursuit(float metresClosed)
        {
            return metresClosed * Systems_SimConstants.PURSUIT_REWARD_PER_METRE;
        }
    }
}
