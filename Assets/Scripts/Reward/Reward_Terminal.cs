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
        /// <summary>An ordinary scrimmage down with no flags — the shape every caller had before revision 14.</summary>
        public static float For(
            Systems_TeamSide side,
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted,
            bool reachedLineToGain = false)
        {
            return For(
                side, new Systems_PlayResult(outcome, netYards, passCompleted, reachedLineToGain));
        }

        /// <summary>
        /// The reward for <paramref name="side"/> of the snap. Every term is computed
        /// for the offense and negated for the defense, flags included: a foul costs
        /// the side that drew it exactly what it pays the other.
        /// </summary>
        public static float For(Systems_TeamSide side, in Systems_PlayResult result)
        {
            float offenseReward = result.Kind.IsTry()
                ? TryReward(in result)
                : KickOrDownReward(in result);

            offenseReward += FoulReward(in result);

            return side == Systems_TeamSide.Offense ? offenseReward : -offenseReward;
        }

        /// <summary>
        /// The outcomes revision 14 brought, then everything that was already here.
        /// </summary>
        private static float KickOrDownReward(in Systems_PlayResult result)
        {
            switch (result.Outcome)
            {
                // THE OTHER SIDE SCORED. On a kick that is a return the receivers
                // earned; anywhere else it is a turnover taken the whole way, and it
                // costs more than a touchdown is worth — the points AND the ball.
                case Systems_PlayOutcome.Touchdown when result.Side == Systems_TeamSide.Defense:
                    return result.Kind.IsKick()
                        ? -Systems_SimConstants.TOUCHDOWN_REWARD
                        : -Systems_SimConstants.RETURN_TOUCHDOWN_REWARD;

                // A safety the offense of the snap SCORED: a returner downed in his
                // own end zone.
                case Systems_PlayOutcome.Safety when result.Side == Systems_TeamSide.Offense:
                    return Systems_SimConstants.SAFETY_PENALTY;

                case Systems_PlayOutcome.Interception:
                case Systems_PlayOutcome.FumbleLost:
                case Systems_PlayOutcome.KickBlocked:
                    // The turnover, and the return on top of it: running a pick back
                    // forty yards is the defense's to earn and the offense's to stop.
                    return -Systems_SimConstants.INTERCEPTION_REWARD
                        - (Systems_SimConstants.RETURN_YARD_REWARD * UnityEngine.Mathf.Max(0f, result.ReturnYards));

                case Systems_PlayOutcome.Punt:
                    // Nearly free, and deliberately so — punting is the CORRECT call
                    // on most fourth downs — plus whatever the coverage and the
                    // returner made of it against a forty-yard net.
                    return -Systems_SimConstants.PUNT_PENALTY
                        + KickFieldPosition(result.KickNetYards - Systems_SimConstants.PUNT_PAR_NET_YARDS);

                case Systems_PlayOutcome.Kickoff:
                    return KickFieldPosition(
                        Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE - result.ReceivingYardLine);

                case Systems_PlayOutcome.Touchback when result.Side == Systems_TeamSide.Defense:
                    if (result.Kind == Systems_PlayKind.Punt)
                    {
                        return -Systems_SimConstants.PUNT_PENALTY
                            + KickFieldPosition(result.KickNetYards - Systems_SimConstants.PUNT_PAR_NET_YARDS);
                    }

                    if (result.Kind == Systems_PlayKind.Kickoff)
                    {
                        return 0f;
                    }

                    // An interception downed in the end zone: the turnover, no return.
                    return -Systems_SimConstants.INTERCEPTION_REWARD;

                case Systems_PlayOutcome.Touchback:
                    return 0f;

                case Systems_PlayOutcome.KickRecovered:
                    return Systems_SimConstants.KICK_RECOVERED_REWARD;

                case Systems_PlayOutcome.DeadBallFoul:
                    return 0f;
            }

            return OffenseReward(
                result.Outcome, result.NetYards, result.PassCompleted, result.ReachedLineToGain);
        }


        /// <summary>The field-position term of a kick play, capped either way.</summary>
        private static float KickFieldPosition(float yardsBetterThanPar)
        {
            return UnityEngine.Mathf.Clamp(
                yardsBetterThanPar * Systems_SimConstants.KICK_FIELD_POSITION_REWARD,
                -Systems_SimConstants.KICK_FIELD_POSITION_CAP,
                Systems_SimConstants.KICK_FIELD_POSITION_CAP);
        }

        /// <summary>
        /// The try: a point or two made or missed, and the defense taking it back
        /// for two of its own.
        /// </summary>
        private static float TryReward(in Systems_PlayResult result)
        {
            bool twoPoint = result.Kind == Systems_PlayKind.TwoPoint;

            if (result.Outcome == Systems_PlayOutcome.Touchdown)
            {
                return result.Side == Systems_TeamSide.Offense
                    ? Systems_SimConstants.TWO_POINT_REWARD
                    : -1.5f * Systems_SimConstants.TWO_POINT_REWARD;
            }

            if (result.Outcome == Systems_PlayOutcome.FieldGoalGood)
            {
                return Systems_SimConstants.EXTRA_POINT_REWARD;
            }

            return twoPoint
                ? -Systems_SimConstants.TWO_POINT_REWARD
                : -Systems_SimConstants.EXTRA_POINT_REWARD;
        }

        /// <summary>
        /// What the flags on the play cost, from the offense's side: its own count
        /// against it, the defense's for it. Paid in training, where nothing enforces
        /// the yardage, so a policy has a reason to stop.
        /// </summary>
        private static float FoulReward(in Systems_PlayResult result)
        {
            // Priced from the fouls themselves when the result was read off a play
            // (Systems_FoulPrice): what each would have cost in a game.
            if (result.OffenseFoulCost >= 0f && result.DefenseFoulCost >= 0f)
            {
                return result.DefenseFoulCost - result.OffenseFoulCost;
            }

            float extra = Systems_SimConstants.PERSONAL_FOUL_REWARD - Systems_SimConstants.FOUL_REWARD;

            float offenseCost = (result.OffenseFouls * Systems_SimConstants.FOUL_REWARD)
                + (result.OffensePersonalFouls * extra);

            float defenseCost = (result.DefenseFouls * Systems_SimConstants.FOUL_REWARD)
                + (result.DefensePersonalFouls * extra);

            return defenseCost - offenseCost;
        }

        private static float OffenseReward(
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted,
            bool reachedLineToGain)
        {
            // Paid only where the play ended on football — a tackle or a step out
            // of bounds. A touchdown already pays more than this, and a play that
            // ran out the clock is the one ending this must not sweeten.
            float firstDownBonus =
                reachedLineToGain ? Systems_SimConstants.FIRST_DOWN_REWARD : 0f;

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

                // PRICED EXACTLY AS A TACKLE ON THE SAME SPOT, first-down bonus and
                // all. It was -TACKLE_REWARD alone — a tackle without the first down —
                // so that an offense could not stall past the sticks to collect it.
                // But the reward is zero-sum, so a stall priced worse for the offense
                // is priced BETTER for the defense, and football_base20 found it:
                // defenders shadowing a carrier who was past the line to gain, 13 to
                // 29 metres off him, never tackling, until the 600-tick ceiling, on
                // 6.8% of training plays by 1.2M steps. Earlier still it was half a
                // tackle, cheaper for the offense, and a quarter of football_base12's
                // plays ended there. Equal to a tackle is the only price neither side
                // can prefer; the time cost breaks the tie toward finishing the play.
                case Systems_PlayOutcome.TimeExpired:
                case Systems_PlayOutcome.Tackle:
                case Systems_PlayOutcome.Sack:
                case Systems_PlayOutcome.Downed:
                    // A stop behind the line of scrimmage is worth more to the
                    // defense than a stop after a gain. A sack is one; a man who
                    // gave himself up is down like any other.
                    return (netYards < 0f
                        ? -(Systems_SimConstants.TACKLE_REWARD
                            + Systems_SimConstants.TACKLE_FOR_LOSS_BONUS)
                        : -Systems_SimConstants.TACKLE_REWARD)
                        + completionBonus + firstDownBonus;

                case Systems_PlayOutcome.OutOfBounds:
                    return (-Systems_SimConstants.TACKLE_REWARD * 0.5f)
                        + completionBonus + firstDownBonus;

                case Systems_PlayOutcome.Incompletion:
                    return -Systems_SimConstants.INCOMPLETION_PENALTY;

                // The largest swing in the game short of a score. A throw no longer
                // earns dense yardage in the air (Reward_Progress.CountsAsProgress),
                // so a pick costs exactly this and nothing offsets it.
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
