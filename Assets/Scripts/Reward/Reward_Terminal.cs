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
            Systems_TeamSide side,
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted)
        {
            float offenseReward = OffenseReward(outcome, netYards, passCompleted);

            return side == Systems_TeamSide.Offense ? offenseReward : -offenseReward;
        }

        private static float OffenseReward(
            Systems_PlayOutcome outcome, float netYards, bool passCompleted)
        {
            // A completed pass pays on top of however the play then ended, because
            // the completion and the tackle that follows it are separate events —
            // a 20 yard catch brought down immediately is a good play, and scoring
            // it as a plain tackle is what made throwing look worthless.
            //
            // COMPLETION_REWARD used to be declared and read by nothing at all:
            // there was no Completion branch here, so the only pass outcomes that
            // touched the reward were the two BAD ones, incompletion and
            // interception. Throwing was strictly dominated before aim could be
            // learned, and football_base03 duly settled on Call/Pass = 0.000.
            float completionBonus =
                passCompleted ? Systems_SimConstants.COMPLETION_REWARD : 0f;

            switch (outcome)
            {
                case Systems_PlayOutcome.Touchdown:
                    return Systems_SimConstants.TOUCHDOWN_REWARD + completionBonus;

                case Systems_PlayOutcome.Tackle:
                    // A stop behind the line of scrimmage is worth more to the
                    // defense than a stop after a gain.
                    return (netYards < 0f
                        ? -(Systems_SimConstants.TACKLE_REWARD
                            + Systems_SimConstants.TACKLE_FOR_LOSS_BONUS)
                        : -Systems_SimConstants.TACKLE_REWARD) + completionBonus;

                case Systems_PlayOutcome.OutOfBounds:
                case Systems_PlayOutcome.TimeExpired:
                    return (-Systems_SimConstants.TACKLE_REWARD * 0.5f) + completionBonus;

                case Systems_PlayOutcome.Incompletion:
                    return -Systems_SimConstants.INCOMPLETION_PENALTY;

                // The largest swing in the game. It has to outweigh the dense
                // yardage a long throw earns on its way to being picked off, or
                // the offense learns that heaving it downfield is free.
                case Systems_PlayOutcome.Interception:
                    return -Systems_SimConstants.INTERCEPTION_REWARD;

                // Priced exactly like an interception, because it is one: the
                // defense takes over at the spot. Without this the switch fell
                // through to `default` and a lost fumble cost the offense NOTHING,
                // which would teach a carrier that running into a crowd is free.
                case Systems_PlayOutcome.FumbleLost:
                    return -Systems_SimConstants.INTERCEPTION_REWARD;

                // Flat, and deliberately not stacked with the yardage term the way
                // Tackle is. A safety is already the worst thing on this list; how
                // far backwards the carrier went to get there does not make it
                // worse, and letting netYards pile on top would make the penalty
                // depend on where the drive happened to start.
                case Systems_PlayOutcome.Safety:
                    return -Systems_SimConstants.SAFETY_PENALTY;

                // A field goal is worth less than a touchdown by design, and the
                // ratio matters more than the numbers: if three points paid nearly
                // as well as seven, a policy would settle for the kick from inside
                // the twenty and never learn to finish a drive.
                case Systems_PlayOutcome.FieldGoalGood:
                    return Systems_SimConstants.FIELD_GOAL_REWARD;

                // Worse than a punt from the same spot, because it is: the defense
                // takes over seven yards further back than the line of scrimmage.
                case Systems_PlayOutcome.FieldGoalMissed:
                    return -Systems_SimConstants.FIELD_GOAL_MISS_PENALTY;

                // Nearly free, and deliberately so. Punting is the CORRECT call on
                // most fourth downs, and pricing it like a turnover would teach the
                // offense to avoid the one decision that is usually right. The small
                // cost that remains is the cost of not having converted — enough
                // that a policy still prefers a first down to a punt from the same
                // spot, and not so much that it never kicks.
                case Systems_PlayOutcome.Punt:
                    return -Systems_SimConstants.PUNT_PENALTY;

                default:
                    return completionBonus;
            }
        }
    }
}
