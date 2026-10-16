namespace PoFootball.Models
{
    /// <summary>
    /// The fouls this simulation can flag. Through revision 13 there were none —
    /// "there is no penalty system to flag it" was written into the throw mask — so
    /// a defender could bury a receiver before the ball arrived and the only thing
    /// that happened was an incompletion.
    ///
    /// Each is DETECTED from what the bodies actually did (Systems_PenaltySystem),
    /// and an official sees it with a chance rather than always, the way an
    /// official misses some. The pre-snap three are the exception: the bodies are
    /// set before the snap and no policy drives them, so whether somebody flinches
    /// is drawn (Systems_IDisciplineModel) and the flinch is then refereed like any
    /// other movement.
    /// </summary>
    public enum Systems_FoulType
    {
        None = 0,

        // --- Pre-snap: the play never happens ------------------------------

        /// <summary>An offensive player moved after he was set. Five yards, replay the down.</summary>
        FalseStart = 1,

        /// <summary>A defender was across the neutral zone at the snap. Five yards.</summary>
        Offside = 2,

        /// <summary>The play clock ran out before the snap. Five yards. Game only.</summary>
        DelayOfGame = 3,

        // --- Live ball ------------------------------------------------------

        /// <summary>A blocker who was beaten kept hold of his man. Ten yards from the previous spot.</summary>
        OffensiveHolding = 4,

        /// <summary>A defender held a receiver before the throw. Five yards and a first down.</summary>
        DefensiveHolding = 5,

        /// <summary>
        /// A defender went through a receiver before the ball got there. Spot foul
        /// and a first down; at the one if the spot is in the end zone.
        /// </summary>
        DefensivePassInterference = 6,

        /// <summary>A receiver pushed off. Ten yards from the previous spot.</summary>
        OffensivePassInterference = 7,

        /// <summary>A late hit on a quarterback who had already thrown. Fifteen and a first down.</summary>
        RoughingThePasser = 8,

        /// <summary>A tackle by the facemask. Fifteen from the end of the run.</summary>
        Facemask = 9,

        /// <summary>A return-team block from behind. Ten from the spot of the foul.</summary>
        IllegalBlockInTheBack = 10,

        /// <summary>A cover man hit the returner before the kick came down. Fifteen yards.</summary>
        KickCatchInterference = 11,

        /// <summary>
        /// A pass thrown from the pocket to nobody, to dodge a sack. Loss of down and
        /// ten yards; a safety if the passer was in his own end zone.
        /// </summary>
        IntentionalGrounding = 12
    }

    public static class Systems_FoulTypeExtensions
    {
        /// <summary>Fouls that kill the play before the snap.</summary>
        public static bool IsDeadBall(this Systems_FoulType foul)
        {
            return foul == Systems_FoulType.FalseStart
                || foul == Systems_FoulType.Offside
                || foul == Systems_FoulType.DelayOfGame;
        }

        /// <summary>Yards the foul is worth before half-the-distance is applied.</summary>
        public static float Yards(this Systems_FoulType foul)
        {
            switch (foul)
            {
                case Systems_FoulType.FalseStart:
                case Systems_FoulType.Offside:
                case Systems_FoulType.DelayOfGame:
                case Systems_FoulType.DefensiveHolding:
                    return 5f;

                case Systems_FoulType.OffensiveHolding:
                case Systems_FoulType.OffensivePassInterference:
                case Systems_FoulType.IllegalBlockInTheBack:
                case Systems_FoulType.IntentionalGrounding:
                    return 10f;

                case Systems_FoulType.RoughingThePasser:
                case Systems_FoulType.Facemask:
                case Systems_FoulType.KickCatchInterference:
                    return 15f;

                // Spot foul: the yardage is wherever the foul happened, which the
                // enforcement reads from the foul's own spot.
                case Systems_FoulType.DefensivePassInterference:
                    return 0f;

                default:
                    return 0f;
            }
        }

        /// <summary>Defensive fouls that also hand the offense a new set of downs.</summary>
        public static bool IsAutomaticFirstDown(this Systems_FoulType foul)
        {
            return foul == Systems_FoulType.DefensiveHolding
                || foul == Systems_FoulType.DefensivePassInterference
                || foul == Systems_FoulType.RoughingThePasser
                || foul == Systems_FoulType.Facemask;
        }

        /// <summary>Enforced from where it happened rather than from the previous spot.</summary>
        public static bool IsSpotFoul(this Systems_FoulType foul)
        {
            return foul == Systems_FoulType.DefensivePassInterference
                || foul == Systems_FoulType.Facemask
                || foul == Systems_FoulType.IllegalBlockInTheBack
                || foul == Systems_FoulType.KickCatchInterference
                || foul == Systems_FoulType.IntentionalGrounding;
        }

        /// <summary>The ones the training reward prices higher: they hurt somebody.</summary>
        public static bool IsPersonalFoul(this Systems_FoulType foul)
        {
            return foul == Systems_FoulType.RoughingThePasser
                || foul == Systems_FoulType.Facemask
                || foul == Systems_FoulType.KickCatchInterference;
        }
    }
}
