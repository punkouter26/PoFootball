namespace PoFootball.Models
{
    /// <summary>
    /// Everything the terminal reward needs to know about how a play went, in one
    /// value. It used to be four loose arguments — outcome, net yards, whether a
    /// pass was completed, whether the line to gain was reached — passed through
    /// three interfaces; revision 14 needed six more (whose score, who had the ball,
    /// how far it was returned, the flags each side drew, where a kick left the
    /// receivers) and a struct is where they stop multiplying signatures.
    ///
    /// Every field is in the frame of the SNAP: Offense is the side that attacked +Y.
    /// </summary>
    public readonly struct Systems_PlayResult
    {
        public readonly Systems_PlayOutcome Outcome;

        public readonly Systems_PlayKind Kind;

        /// <summary>Net yards of the ball from the line at the whistle, toward +Y.</summary>
        public readonly float NetYards;

        public readonly bool PassCompleted;

        public readonly bool ReachedLineToGain;

        /// <summary>For a Touchdown or Safety, whose points. Otherwise who holds the ball.</summary>
        public readonly Systems_TeamSide Side;

        /// <summary>Yards the defense of the snap ran it back after it got it; 0 if it never did.</summary>
        public readonly float ReturnYards;

        public readonly int OffenseFouls;

        public readonly int OffensePersonalFouls;

        public readonly int DefenseFouls;

        public readonly int DefensePersonalFouls;

        /// <summary>
        /// What the offense's flags on this play would have cost it in a game, in
        /// reward units (Systems_FoulPrice). Negative means "not priced": the flat
        /// FOUL_REWARD per count, for callers that only know the counts.
        /// </summary>
        public readonly float OffenseFoulCost;

        /// <summary>The defense's, likewise.</summary>
        public readonly float DefenseFoulCost;

        /// <summary>
        /// For a kick play that ended with the receivers holding the ball: the yard
        /// line they will start from, measured from their own goal line.
        /// </summary>
        public readonly float ReceivingYardLine;

        /// <summary>
        /// For a kick play: yards from the line of scrimmage to where the receivers
        /// will start — a touchback counted at its touchback spot, not where the
        /// ball died. A punt's net.
        /// </summary>
        public readonly float KickNetYards;

        public Systems_PlayResult(
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted,
            bool reachedLineToGain)
            : this(
                outcome, Systems_PlayKind.Scrimmage, netYards, passCompleted,
                reachedLineToGain, LegacySideFor(outcome), 0f, 0, 0, 0, 0,
                Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE,
                Systems_SimConstants.PUNT_PAR_NET_YARDS)
        {
        }

        /// <summary>
        /// What a pre-revision-14 caller meant by an outcome: a safety is the
        /// defense's two points, a turnover or a kick leaves the defense with the
        /// ball, and everything else is the offense's. A kick is taken at par.
        /// </summary>
        private static Systems_TeamSide LegacySideFor(Systems_PlayOutcome outcome)
        {
            return outcome == Systems_PlayOutcome.Safety || outcome.HandsTheBallOver()
                ? Systems_TeamSide.Defense
                : Systems_TeamSide.Offense;
        }

        public Systems_PlayResult(
            Systems_PlayOutcome outcome,
            Systems_PlayKind kind,
            float netYards,
            bool passCompleted,
            bool reachedLineToGain,
            Systems_TeamSide side,
            float returnYards,
            int offenseFouls,
            int offensePersonalFouls,
            int defenseFouls,
            int defensePersonalFouls,
            float receivingYardLine,
            float kickNetYards,
            float offenseFoulCost = -1f,
            float defenseFoulCost = -1f)
        {
            OffenseFoulCost = offenseFoulCost;
            DefenseFoulCost = defenseFoulCost;
            Outcome = outcome;
            Kind = kind;
            NetYards = netYards;
            PassCompleted = passCompleted;
            ReachedLineToGain = reachedLineToGain;
            Side = side;
            ReturnYards = returnYards;
            OffenseFouls = offenseFouls;
            OffensePersonalFouls = offensePersonalFouls;
            DefenseFouls = defenseFouls;
            DefensePersonalFouls = defensePersonalFouls;
            ReceivingYardLine = receivingYardLine;
            KickNetYards = kickNetYards;
        }

        /// <summary>Reads the result of the play that has just ended off the play model.</summary>
        public static Systems_PlayResult From(Systems_PlayModel play)
        {
            int offenseFouls = 0;
            int offensePersonal = 0;
            int defenseFouls = 0;
            int defensePersonal = 0;
            float offenseCost = 0f;
            float defenseCost = 0f;

            bool reachedLine = !play.PossessionChanged
                && play.Outcome != Systems_PlayOutcome.Incompletion
                && play.DeadBallSpot.y >= play.LineToGainY - 0.01f;

            for (int index = 0; index < play.FoulCount; index++)
            {
                Systems_Foul foul = play.GetFoul(index);
                bool personal = foul.Type.IsPersonalFoul();

                float cost = Systems_FoulPrice.Of(
                    foul, play.LineOfScrimmageY, play.PassCompleted, reachedLine);

                if (foul.Side == Systems_TeamSide.Offense)
                {
                    offenseFouls++;
                    offensePersonal += personal ? 1 : 0;
                    offenseCost += cost;
                }
                else
                {
                    defenseFouls++;
                    defensePersonal += personal ? 1 : 0;
                    defenseCost += cost;
                }
            }

            // The receivers' own yard line is the distance of the dead ball from the
            // goal line they defend, which is +Y in the frame of the snap — or, for a
            // touchback, the touchback spot.
            float startY = play.DeadBallSpot.y;

            if (play.Outcome == Systems_PlayOutcome.Touchback
                && play.ScoringSide == Systems_TeamSide.Defense)
            {
                float yardLine = play.Kind == Systems_PlayKind.Kickoff
                    ? Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE
                    : Systems_GameRules.TOUCHBACK_YARD_LINE;

                startY = Systems_FieldModel.ATTACKING_GOAL_LINE_Y - (yardLine * Systems_FieldModel.YARD);
            }

            float receivingYardLine =
                (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - startY) / Systems_FieldModel.YARD;

            float kickNetYards = (startY - play.LineOfScrimmageY) / Systems_FieldModel.YARD;

            return new Systems_PlayResult(
                play.Outcome,
                play.Kind,
                play.NetYards,
                play.PassCompleted,
                reachedLine,
                play.ScoringSide,
                play.ReturnYards,
                offenseFouls,
                offensePersonal,
                defenseFouls,
                defensePersonal,
                receivingYardLine,
                kickNetYards,
                offenseCost,
                defenseCost);
        }
    }
}
