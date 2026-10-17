namespace PoFootball.Models
{
    /// <summary>
    /// What a flag costs in reward: what it would have cost in a played game.
    ///
    /// WHY (2026-10-16). Training does not enforce penalties — the play stands and
    /// a flag was a flat FOUL_REWARD (0.15) — while a completed pass alone pays the
    /// offense COMPLETION_REWARD (0.45) plus its yards and maybe a first down. So
    /// interfering with a receiver PAID the defense, holding on a run that moved
    /// the chains paid the offense, and football_base18's flag rate climbed from 4%
    /// of plays to 12% in two hours. A game enforces those fouls as fifteen-yard
    /// spot fouls and wiped-out gains, so the brains were learning a habit every
    /// game would punish. Each flag now costs at least what it gives away:
    ///
    ///     every foul           its yards at YARD_REWARD_SCALE
    ///     defensive, automatic first down     + FIRST_DOWN_REWARD
    ///     defensive pass interference         + COMPLETION_REWARD + the yards to
    ///                                           the spot (the catch it stopped,
    ///                                           which a game hands over anyway)
    ///     offensive, on a completed pass      + COMPLETION_REWARD (wiped out)
    ///     offensive, on a play that moved the chains   + FIRST_DOWN_REWARD (wiped out)
    ///     a personal foul      + the PERSONAL_FOUL_REWARD premium
    ///
    /// and never less than FOUL_REWARD. Reward, not contract: no shape or dynamic
    /// changes, as with every reward change in this project.
    /// </summary>
    public static class Systems_FoulPrice
    {
        public static float Of(
            Systems_Foul foul, float lineOfScrimmageY, bool passCompleted, bool reachedLineToGain)
        {
            Systems_FoulType type = foul.Type;
            float cost = type.Yards() * Systems_SimConstants.YARD_REWARD_SCALE;

            if (foul.Side == Systems_TeamSide.Defense)
            {
                if (type.IsAutomaticFirstDown())
                {
                    cost += Systems_SimConstants.FIRST_DOWN_REWARD;
                }

                if (type == Systems_FoulType.DefensivePassInterference)
                {
                    float spotYards = (foul.SpotY - lineOfScrimmageY) / Systems_FieldModel.YARD;
                    cost += Systems_SimConstants.COMPLETION_REWARD
                        + (System.Math.Max(1f, spotYards) * Systems_SimConstants.YARD_REWARD_SCALE);
                }
            }
            else
            {
                if (passCompleted
                    && (type == Systems_FoulType.OffensivePassInterference
                        || type == Systems_FoulType.OffensiveHolding
                        || type == Systems_FoulType.IllegalBlockInTheBack))
                {
                    cost += Systems_SimConstants.COMPLETION_REWARD;
                }

                if (reachedLineToGain)
                {
                    cost += Systems_SimConstants.FIRST_DOWN_REWARD;
                }
            }

            if (type.IsPersonalFoul())
            {
                cost += Systems_SimConstants.PERSONAL_FOUL_REWARD - Systems_SimConstants.FOUL_REWARD;
            }

            return System.Math.Max(cost, Systems_SimConstants.FOUL_REWARD);
        }
    }
}
