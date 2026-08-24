using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// Whether a tackle knocks the ball loose and the defense comes up with it.
    ///
    /// WHY FUMBLES EXIST AT ALL NOW. Before this, the only ways possession changed
    /// were an interception and a turnover on downs, and measured games produced
    /// one or two turnovers in total against a real football game's three or so.
    /// Ball security simply was not a thing a carrier could be bad at, so a back
    /// running into a crowd risked nothing.
    ///
    /// ONLY LOST FUMBLES ARE MODELLED. A fumble the offense recovers is, in every
    /// respect this simulation can observe, a tackle at the same spot — there is no
    /// loose-ball scramble here and inventing one would be a whole physics mode for
    /// an outcome that changes nothing. So this answers the only question that
    /// matters: does the defense get the ball.
    ///
    /// THE SEAM IS THE ONE Systems_IKickModel AND Systems_ISpotProvider ALREADY
    /// ESTABLISHED. Training gets a PURE FUNCTION of the collision — a big hit with
    /// help — which a policy can actually learn to avoid by not running into
    /// traffic. A played game gets a probability, because a fumble that is certain
    /// whenever its trigger is met is one a viewer can predict.
    /// </summary>
    public interface Systems_IFumbleModel
    {
        /// <param name="closingSpeed">Relative speed of the hit, m/s.</param>
        /// <param name="tacklerCount">Opponents in contact on the deciding tick.</param>
        /// <param name="carrier">Who is carrying — a back protects it better.</param>
        bool IsFumbleLost(
            float closingSpeed, int tacklerCount, Systems_PlayerRole carrier);
    }

    /// <summary>
    /// Training. A pure function of the hit: a genuine collision, with a second
    /// defender arriving, strips the ball. Nothing is drawn, so a run replays
    /// exactly and the trigger is something a policy can see coming and avoid.
    /// </summary>
    public sealed class Systems_DeterministicFumbleModel : Systems_IFumbleModel
    {
        public bool IsFumbleLost(
            float closingSpeed, int tacklerCount, Systems_PlayerRole carrier)
        {
            if (tacklerCount < Systems_SimConstants.FUMBLE_MIN_TACKLERS)
            {
                return false;
            }

            return closingSpeed >= Systems_SimConstants.FUMBLE_CLOSING_SPEED
                * SecurityOf(carrier);
        }

        /// <summary>
        /// Multiplier on how hard a carrier has to be hit before it comes loose.
        /// Backs carry the ball for a living; a receiver or a lineman who has ended
        /// up with it does not.
        /// </summary>
        internal static float SecurityOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.RunningBack:
                    return 1.35f;

                case Systems_PlayerRole.Quarterback:
                    return 1.15f;

                case Systems_PlayerRole.OffensiveLine:
                    return 0.7f;

                default:
                    return 1f;
            }
        }
    }

    /// <summary>
    /// A played game. Chance scales with how hard the hit was and how many people
    /// arrived, around a base rate taken from real football: roughly one fumble
    /// lost per team per game across about sixty-five snaps, so a shade under 2% of
    /// plays — see Systems_SimConstants.FUMBLE_BASE_CHANCE.
    ///
    /// Seeded from Systems_EpisodeSeed with its own stream offset, exactly as
    /// Systems_ProbabilisticKickModel is, so fumbles vary between games and replay
    /// within one.
    /// </summary>
    public sealed class Systems_ProbabilisticFumbleModel : Systems_IFumbleModel
    {
        /// <summary>Arbitrary odd constant; keeps this stream independent.</summary>
        private const uint STREAM_OFFSET = 0x85EBCA6Bu;

        private Random _rng;

        public Systems_ProbabilisticFumbleModel()
        {
            _rng = new Random(Systems_EpisodeSeed.Value ^ STREAM_OFFSET);
        }

        public bool IsFumbleLost(
            float closingSpeed, int tacklerCount, Systems_PlayerRole carrier)
        {
            float chance = Systems_SimConstants.FUMBLE_BASE_CHANCE;

            // A real collision is what strips a ball; a man sliding off the carrier
            // is not. Scale by the hit, capped so a huge closing speed cannot make
            // it a certainty.
            float hit = UnityEngine.Mathf.Clamp01(
                closingSpeed / Systems_SimConstants.FUMBLE_CLOSING_SPEED);

            chance *= 0.4f + (1.6f * hit);

            // Help arriving multiplies it — the second man is who rips it out.
            if (tacklerCount > 1)
            {
                chance *= 1f + (0.6f * (tacklerCount - 1));
            }

            chance /= Systems_DeterministicFumbleModel.SecurityOf(carrier);

            return _rng.NextFloat() < UnityEngine.Mathf.Clamp01(chance);
        }
    }
}
