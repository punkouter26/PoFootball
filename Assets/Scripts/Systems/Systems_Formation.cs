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
    }
}
