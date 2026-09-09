using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// The parts of the formation that never change, whichever alignment the teams
    /// happen to be in.
    ///
    /// WHAT MOVED OUT OF HERE AND WHY. This class used to be the one fixed
    /// formation — an I-formation against a 4-3, twenty-two hard-coded offsets and
    /// a single coverage table. The alignments now live in
    /// <see cref="Systems_FormationBook"/>, eight per side, and which pair is on the
    /// field for the current play lives in <see cref="Systems_FormationSelection"/>,
    /// which is injected because it is per-play state.
    ///
    /// WHAT STAYED, AND WHY IT HAD TO. Two things are properties of the SQUAD rather
    /// than of an alignment, and both are read where no injected object is
    /// available:
    ///
    ///   The ROLE of a slot. Agent_FootballPlayer.CacheRole runs from Awake as well
    ///   as from Construct, precisely so that an agent dropped into a scene with no
    ///   lifetime scope still knows what it is. Role cannot depend on a formation
    ///   anyway — a slot index selects the brain, the drawn shape and the one-hot
    ///   role slice of the observation vector, so a formation that changed a role
    ///   would be changing the squad, not the alignment.
    ///
    ///   The widest split, which the broadcast camera uses to frame the snap. It is
    ///   now the widest across EVERY formation rather than the current one, so the
    ///   camera does not resize itself between plays.
    /// </summary>
    public static class Systems_Formation
    {
        /// <summary>The quarterback takes the snap, so it starts with the ball.</summary>
        public const int QUARTERBACK_SLOT_INDEX = Systems_FormationBook.QUARTERBACK_SLOT_INDEX;

        public const int FULLBACK_SLOT_INDEX = Systems_FormationBook.FULLBACK_SLOT_INDEX;

        public const int HALFBACK_SLOT_INDEX = Systems_FormationBook.HALFBACK_SLOT_INDEX;

        /// <summary>Twenty-two: eleven a side, in every formation.</summary>
        public static int SlotCount => Systems_FormationBook.SLOTS_PER_SIDE * 2;

        /// <summary>
        /// See <see cref="Systems_FormationBook.WidestSlotX"/> — the widest split in
        /// any formation, folded over the tables rather than written down, so it
        /// cannot go stale the way a hand-copied constant did before it.
        /// </summary>
        public static float WidestSlotX => Systems_FormationBook.WidestSlotX;

        /// <summary>
        /// The position group of a squad slot. Fixed for the life of the build:
        /// slots 0..10 are the offense and 11..21 the defense, and the order within
        /// each is the same in every formation — <see cref="Systems_FormationBook.Validate"/>
        /// asserts it rather than trusting it.
        ///
        /// Reads the base formation because any of them would give the same answer.
        /// </summary>
        public static Systems_PlayerRole RoleFor(int slotIndex)
        {
            return slotIndex < Systems_FormationBook.SLOTS_PER_SIDE
                ? Systems_FormationBook
                    .OffenseSlot(Systems_OffensiveFormation.ProI, slotIndex).Role
                : Systems_FormationBook
                    .DefenseSlot(
                        Systems_DefensiveFormation.FourThreeBase,
                        slotIndex - Systems_FormationBook.SLOTS_PER_SIDE).Role;
        }
    }
}
