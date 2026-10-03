using PoFootball.Models;

namespace PoFootball.Rewards
{
    /// <summary>
    /// The dense half of the reward: paid every physics tick on ball movement.
    ///
    /// This exists specifically to defeat the do-nothing optimum. With 22 agents
    /// and minimal rules, a purely terminal reward leaves a very large basin where
    /// no side gains anything by acting, and self-play settles into it. Paying per
    /// yard means standing still earns the offense only the time cost.
    ///
    /// Pure functions — no state, no allocation.
    /// </summary>
    public static class Reward_Progress
    {
        /// <summary>
        /// Reward for one physics tick. yardsDelta is the carrier's forward
        /// progress since the previous tick, signed toward the offense's goal.
        ///
        /// The time cost is divided by DECISION_PERIOD so that accumulating it
        /// every tick totals the same as charging it once per decision.
        /// </summary>
        public static float PerTick(Systems_TeamSide side, float yardsDelta)
        {
            float progress = yardsDelta * Systems_SimConstants.YARD_REWARD_SCALE;

            if (side == Systems_TeamSide.Offense)
            {
                return progress
                    + (Systems_SimConstants.TIME_COST_PER_DECISION
                        / Systems_SimConstants.DECISION_PERIOD);
            }

            return -progress;
        }

        /// <summary>
        /// Whether ball movement on this tick counts as progress at all.
        ///
        /// NOT WHILE A PASS IS IN THE AIR. The ball's Y is mirrored every tick of
        /// its flight, so paying on it handed the offense up to half a touchdown
        /// for a throw that then fell incomplete: a forty-yard miss netted +0.30
        /// against -0.42 for an eight-yard run that ended in a tackle. The caller
        /// holds its baseline at the release point instead, so a catch is paid
        /// the whole air distance on the tick it is made, and a miss or a pick —
        /// both of which end the play on the spot — is paid nothing.
        /// </summary>
        public static bool CountsAsProgress(Systems_BallState ballState)
        {
            return ballState != Systems_BallState.InFlight;
        }
    }
}
