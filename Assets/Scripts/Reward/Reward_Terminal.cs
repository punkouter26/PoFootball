using PoFootball.Models;

namespace PoFootball.Rewards
{
    /// <summary>
    /// The sparse half of the reward: paid once, at the whistle, to every player.
    ///
    /// Deliberately zero-sum between the two sides. Self-play ELO rates offense
    /// against defense, so any outcome that paid both sides positively would let
    /// the two policies collude into a shared local optimum instead of competing.
    /// </summary>
    public static class Reward_Terminal
    {
        public static float For(
            Systems_TeamSide side, Systems_PlayOutcome outcome, float netYards)
        {
            float offenseReward = OffenseReward(outcome, netYards);

            return side == Systems_TeamSide.Offense ? offenseReward : -offenseReward;
        }

        private static float OffenseReward(Systems_PlayOutcome outcome, float netYards)
        {
            switch (outcome)
            {
                case Systems_PlayOutcome.Touchdown:
                    return Systems_SimConstants.TOUCHDOWN_REWARD;

                case Systems_PlayOutcome.Tackle:
                    // A stop behind the line of scrimmage is worth more to the
                    // defense than a stop after a gain.
                    return netYards < 0f
                        ? -(Systems_SimConstants.TACKLE_REWARD
                            + Systems_SimConstants.TACKLE_FOR_LOSS_BONUS)
                        : -Systems_SimConstants.TACKLE_REWARD;

                case Systems_PlayOutcome.OutOfBounds:
                case Systems_PlayOutcome.TimeExpired:
                    return -Systems_SimConstants.TACKLE_REWARD * 0.5f;

                case Systems_PlayOutcome.Incompletion:
                    return -Systems_SimConstants.INCOMPLETION_PENALTY;

                // The largest swing in the game. It has to outweigh the dense
                // yardage a long throw earns on its way to being picked off, or
                // the offense learns that heaving it downfield is free.
                case Systems_PlayOutcome.Interception:
                    return -Systems_SimConstants.INTERCEPTION_REWARD;

                default:
                    return 0f;
            }
        }
    }
}
