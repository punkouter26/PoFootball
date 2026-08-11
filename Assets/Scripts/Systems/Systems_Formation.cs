using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// The single fixed formation used every episode (Episode variety: random LOS,
    /// fixed formation). Offense is an I-formation — quarterback under centre with
    /// the fullback and halfback stacked directly behind — against a 4-3 defense
    /// with two-high safeties.
    ///
    /// Milestone 2 traded the third receiver for the fullback to keep the offense
    /// at eleven.
    ///
    /// Slot order is stable and IS the index each agent carries in the scene:
    /// 0..10 offense, 11..21 defense. Every pair of slots is at least
    /// MIN_SPAWN_SEPARATION apart, which is what makes acceptance criterion #12
    /// hold by construction rather than by rejection sampling.
    /// </summary>
    public static class Systems_Formation
    {
        /// <summary>The quarterback takes the snap, so it starts with the ball.</summary>
        public const int QUARTERBACK_SLOT_INDEX = 8;

        public const int FULLBACK_SLOT_INDEX = 9;

        public const int HALFBACK_SLOT_INDEX = 10;

        // The eligible receivers a defender can be assigned to, and the three
        // defenders that carry an assignment. Named because CoverageAssignmentFor
        // is a table of pairs and a table of bare integers is unreadable and
        // unverifiable.
        private const int TIGHT_END_SLOT_INDEX = 5;
        private const int WIDE_RECEIVER_LEFT_SLOT_INDEX = 6;
        private const int WIDE_RECEIVER_RIGHT_SLOT_INDEX = 7;
        private const int CORNERBACK_LEFT_SLOT_INDEX = 18;
        private const int CORNERBACK_RIGHT_SLOT_INDEX = 19;
        private const int STRONG_SAFETY_SLOT_INDEX = 21;

        private static readonly Systems_FormationSlot[] Slots =
        {
            // --- Offense: attacks +Y, lines up at or behind the LOS -----------
            new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, -3.0f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, -1.5f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 0.0f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 1.5f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 3.0f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.TightEnd, 4.5f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.WideReceiver, -12.0f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.WideReceiver, 12.0f, -0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.Quarterback, 0.0f, -2.5f),
            new Systems_FormationSlot(Systems_PlayerRole.Fullback, 0.0f, -4.5f),
            new Systems_FormationSlot(Systems_PlayerRole.RunningBack, 0.0f, -6.5f),

            // --- Defense: attacks -Y, lines up beyond the LOS ------------------
            new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, -2.5f, 0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, -0.9f, 0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, 0.9f, 0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, 2.5f, 0.8f),
            new Systems_FormationSlot(Systems_PlayerRole.Linebacker, -4.0f, 4.5f),
            new Systems_FormationSlot(Systems_PlayerRole.Linebacker, 0.0f, 4.5f),
            new Systems_FormationSlot(Systems_PlayerRole.Linebacker, 4.0f, 4.5f),
            new Systems_FormationSlot(Systems_PlayerRole.Cornerback, -12.0f, 7.0f),
            new Systems_FormationSlot(Systems_PlayerRole.Cornerback, 12.0f, 7.0f),
            new Systems_FormationSlot(Systems_PlayerRole.Safety, -5.0f, 13.0f),
            new Systems_FormationSlot(Systems_PlayerRole.Safety, 5.0f, 13.0f)
        };

        public static int SlotCount => Slots.Length;

        public static Systems_FormationSlot GetSlot(int index)
        {
            return Slots[index];
        }

        /// <summary>
        /// The offensive slot a given defender covers man-to-man, or -1 for a
        /// defender with no assignment — which means the deep middle.
        ///
        /// Together with the four linemen rushing and the three linebackers holding
        /// a zone, this is Cover 1: corners on the two split receivers, the strong
        /// safety on the tight end, the free safety over the top.
        ///
        /// A FIXED TABLE RATHER THAN A NEAREST-RECEIVER SEARCH, for two reasons.
        /// Nearest-receiver is quadratic in the squad and runs on every defender on
        /// every decision, and worse, it is unstable: two defenders repeatedly claim
        /// the same receiver and abandon it as the geometry crosses over, so the
        /// coverage visibly flickers. A formation plays fixed assignments precisely
        /// because that ambiguity is what offenses attack.
        ///
        /// Slot-indexed rather than role-indexed because both corners share a role
        /// and they do not share an assignment.
        /// </summary>
        public static int CoverageAssignmentFor(int defenderSlotIndex)
        {
            switch (defenderSlotIndex)
            {
                case CORNERBACK_LEFT_SLOT_INDEX:
                    return WIDE_RECEIVER_LEFT_SLOT_INDEX;
                case CORNERBACK_RIGHT_SLOT_INDEX:
                    return WIDE_RECEIVER_RIGHT_SLOT_INDEX;
                case STRONG_SAFETY_SLOT_INDEX:
                    return TIGHT_END_SLOT_INDEX;
                default:
                    return -1;
            }
        }
    }
}
