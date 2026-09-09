using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// The formations are the one thing that guarantees acceptance criterion #12
    /// (no overlapping spawns) by construction rather than by rejection sampling.
    /// If someone nudges a slot, this suite is what catches it.
    ///
    /// EVERY TEST HERE NOW RUNS OVER EVERY FORMATION, and several of them over
    /// every offense-defense PAIRING, because that is what the subject became. The
    /// suite was written against a single fixed I-formation against a single 4-3;
    /// there are now eight alignments a side and sixty-four ways they can meet, and
    /// a suite that checked only the first of those would pass while sixty-three
    /// untested pairings shipped.
    /// </summary>
    public sealed class Systems_FormationTests
    {
        private static Systems_OffensiveFormation[] OffensiveFormations =>
            (Systems_OffensiveFormation[])System.Enum.GetValues(
                typeof(Systems_OffensiveFormation));

        private static Systems_DefensiveFormation[] DefensiveFormations =>
            (Systems_DefensiveFormation[])System.Enum.GetValues(
                typeof(Systems_DefensiveFormation));

        /// <summary>
        /// The whole squad, 0..21, laid out for one pairing — the same absolute
        /// indexing every agent in the scene carries.
        /// </summary>
        private static Systems_FormationSlot SlotOf(
            Systems_OffensiveFormation offense, Systems_DefensiveFormation defense,
            int slotIndex)
        {
            return slotIndex < Systems_FormationBook.SLOTS_PER_SIDE
                ? Systems_FormationBook.OffenseSlot(offense, slotIndex)
                : Systems_FormationBook.DefenseSlot(
                    defense, slotIndex - Systems_FormationBook.SLOTS_PER_SIDE);
        }

        [Test]
        public void SlotCount_IsTwoFullSquads()
        {
            Assert.That(Systems_Formation.SlotCount, Is.EqualTo(Systems_PlayerRegistry.CAPACITY));
            Assert.That(Systems_Formation.SlotCount, Is.EqualTo(22));
        }

        /// <summary>
        /// Every enum value must have a table behind it. Cast an enum member with no
        /// row and the lookup throws an IndexOutOfRange on a random play hours into
        /// a run, which is the worst possible time to find out.
        /// </summary>
        [Test]
        public void EveryFormation_HasATable()
        {
            Assert.That(
                Systems_FormationBook.OffensiveFormationCount,
                Is.EqualTo(OffensiveFormations.Length));
            Assert.That(
                Systems_FormationBook.DefensiveFormationCount,
                Is.EqualTo(DefensiveFormations.Length));
        }

        /// <summary>
        /// The book's own validator, run as a test. It covers role order, table
        /// sizes, the coverage/front count match and separation across all sixty-four
        /// pairings — the same check the episode director runs on a development
        /// build, asserted here so it fails in CI rather than on a phone.
        /// </summary>
        [Test]
        public void TheBook_ValidatesItself()
        {
            Assert.That(Systems_FormationBook.Validate(), Is.Null);
        }

        /// <summary>Acceptance criterion #12, across every pairing.</summary>
        [Test]
        public void EverySlotPair_IsAtLeastMinimumSeparationApart()
        {
            foreach (Systems_OffensiveFormation offense in OffensiveFormations)
            {
                foreach (Systems_DefensiveFormation defense in DefensiveFormations)
                {
                    for (int a = 0; a < Systems_Formation.SlotCount; a++)
                    {
                        Systems_FormationSlot slotA = SlotOf(offense, defense, a);

                        for (int b = a + 1; b < Systems_Formation.SlotCount; b++)
                        {
                            Systems_FormationSlot slotB = SlotOf(offense, defense, b);

                            float distance = Vector2.Distance(
                                new Vector2(slotA.OffsetX, slotA.OffsetY),
                                new Vector2(slotB.OffsetX, slotB.OffsetY));

                            Assert.That(
                                distance,
                                Is.GreaterThanOrEqualTo(Systems_SimConstants.MIN_SPAWN_SEPARATION),
                                $"{offense} vs {defense}: slots {a} ({slotA.Role}) and "
                                + $"{b} ({slotB.Role}) are {distance:F3} m apart");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A slot index selects the brain, the drawn shape and the one-hot role
        /// slice of the observation vector. If two formations disagree about what
        /// slot 9 is, every agent keeps its index and gets a different body — the
        /// game still runs and the whole squad is one seat out of place.
        /// </summary>
        [Test]
        public void EveryFormation_UsesTheSameRolesInTheSameOrder()
        {
            foreach (Systems_OffensiveFormation offense in OffensiveFormations)
            {
                for (int slot = 0; slot < Systems_FormationBook.SLOTS_PER_SIDE; slot++)
                {
                    Assert.That(
                        Systems_FormationBook.OffenseSlot(offense, slot).Role,
                        Is.EqualTo(Systems_Formation.RoleFor(slot)),
                        $"{offense} slot {slot} has the wrong role");
                }
            }

            foreach (Systems_DefensiveFormation defense in DefensiveFormations)
            {
                for (int slot = 0; slot < Systems_FormationBook.SLOTS_PER_SIDE; slot++)
                {
                    Assert.That(
                        Systems_FormationBook.DefenseSlot(defense, slot).Role,
                        Is.EqualTo(Systems_Formation.RoleFor(
                            slot + Systems_FormationBook.SLOTS_PER_SIDE)),
                        $"{defense} slot {slot} has the wrong role");
                }
            }
        }

        [Test]
        public void Slots_AreElevenOffenceThenElevenDefence()
        {
            for (int index = 0; index < Systems_RoleTable.SQUAD_SIZE; index++)
            {
                Assert.That(
                    Systems_RoleTable.SideOf(Systems_Formation.RoleFor(index)),
                    Is.EqualTo(Systems_TeamSide.Offense),
                    $"slot {index} should be offense");
            }

            for (int index = Systems_RoleTable.SQUAD_SIZE;
                index < Systems_Formation.SlotCount;
                index++)
            {
                Assert.That(
                    Systems_RoleTable.SideOf(Systems_Formation.RoleFor(index)),
                    Is.EqualTo(Systems_TeamSide.Defense),
                    $"slot {index} should be defense");
            }
        }

        [Test]
        public void TheQuarterbackTakesTheSnap()
        {
            Assert.That(
                Systems_Formation.RoleFor(Systems_Formation.QUARTERBACK_SLOT_INDEX),
                Is.EqualTo(Systems_PlayerRole.Quarterback));
        }

        [Test]
        public void HandoffTargetSlots_PointAtTheTwoBacks()
        {
            Assert.That(
                Systems_Formation.RoleFor(Systems_Formation.FULLBACK_SLOT_INDEX),
                Is.EqualTo(Systems_PlayerRole.Fullback));
            Assert.That(
                Systems_Formation.RoleFor(Systems_Formation.HALFBACK_SLOT_INDEX),
                Is.EqualTo(Systems_PlayerRole.RunningBack));
        }

        /// <summary>
        /// THE TEST THAT DECIDES WHICH FORMATIONS ARE LEGAL HERE.
        ///
        /// The original asserted that the quarterback, fullback and halfback shared
        /// an X — true of the I-formation and of nothing else — with the stated
        /// reason that a handoff only completes inside HANDOFF_RADIUS, so backs that
        /// spawn too far away silently stop the run game working. That reason
        /// generalises even though the assertion does not: Systems_BallSystem picks
        /// the handoff target from the play call alone and never looks at the
        /// alignment, so a formation that splits a back out wide turns every run
        /// called to him into a quarterback jogging into the rush.
        ///
        /// So the invariant is the distance, not the shared lane.
        /// </summary>
        [Test]
        public void BothBacks_LineUpWithinHandoffReachOfTheQuarterback()
        {
            foreach (Systems_OffensiveFormation offense in OffensiveFormations)
            {
                Systems_FormationSlot quarterback = Systems_FormationBook.OffenseSlot(
                    offense, Systems_Formation.QUARTERBACK_SLOT_INDEX);

                AssertWithinHandoffReach(
                    offense, quarterback, Systems_Formation.FULLBACK_SLOT_INDEX, "fullback");
                AssertWithinHandoffReach(
                    offense, quarterback, Systems_Formation.HALFBACK_SLOT_INDEX, "halfback");
            }
        }

        private static void AssertWithinHandoffReach(
            Systems_OffensiveFormation offense, Systems_FormationSlot quarterback,
            int backSlotIndex, string label)
        {
            Systems_FormationSlot back =
                Systems_FormationBook.OffenseSlot(offense, backSlotIndex);

            float distance = Vector2.Distance(
                new Vector2(quarterback.OffsetX, quarterback.OffsetY),
                new Vector2(back.OffsetX, back.OffsetY));

            Assert.That(
                distance,
                Is.LessThanOrEqualTo(Systems_FormationBook.MAX_HANDOFF_ALIGNMENT_DISTANCE),
                $"{offense}: the {label} lines up {distance:F2} m from the quarterback, "
                + "so a run called to him cannot be handed off");
        }

        [Test]
        public void Offense_LinesUpBehindTheLine_DefenseInFront()
        {
            foreach (Systems_OffensiveFormation offense in OffensiveFormations)
            {
                foreach (Systems_DefensiveFormation defense in DefensiveFormations)
                {
                    for (int index = 0; index < Systems_Formation.SlotCount; index++)
                    {
                        Systems_FormationSlot slot = SlotOf(offense, defense, index);
                        bool isOffense =
                            Systems_RoleTable.SideOf(slot.Role) == Systems_TeamSide.Offense;

                        if (isOffense)
                        {
                            Assert.That(
                                slot.OffsetY, Is.LessThan(0f),
                                $"{offense}: offense slot {index} is past the LOS");
                        }
                        else
                        {
                            Assert.That(
                                slot.OffsetY, Is.GreaterThan(0f),
                                $"{defense}: defense slot {index} is behind the LOS");
                        }
                    }
                }
            }
        }

        [Test]
        public void RoleCounts_MatchARealElevenManFormation()
        {
            int[] counts = new int[Systems_RoleTable.ROLE_COUNT];

            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                counts[(int)Systems_Formation.RoleFor(index)]++;
            }

            Assert.That(counts[(int)Systems_PlayerRole.OffensiveLine], Is.EqualTo(5));
            Assert.That(counts[(int)Systems_PlayerRole.TightEnd], Is.EqualTo(1));
            Assert.That(counts[(int)Systems_PlayerRole.WideReceiver], Is.EqualTo(2));
            Assert.That(counts[(int)Systems_PlayerRole.Quarterback], Is.EqualTo(1));
            Assert.That(counts[(int)Systems_PlayerRole.Fullback], Is.EqualTo(1));
            Assert.That(counts[(int)Systems_PlayerRole.RunningBack], Is.EqualTo(1));
            Assert.That(counts[(int)Systems_PlayerRole.DefensiveLine], Is.EqualTo(4));
            Assert.That(counts[(int)Systems_PlayerRole.Linebacker], Is.EqualTo(3));
            Assert.That(counts[(int)Systems_PlayerRole.Cornerback], Is.EqualTo(2));
            Assert.That(counts[(int)Systems_PlayerRole.Safety], Is.EqualTo(2));
        }

        [Test]
        public void EverySlot_StartsInsideTheSidelines_AtAnyLegalLineOfScrimmage()
        {
            float[] linesOfScrimmage =
            {
                Systems_FieldModel.LOS_MIN_Y,
                0f,
                Systems_FieldModel.LOS_MAX_Y
            };

            Systems_FieldModel field = new Systems_FieldModel();

            foreach (Systems_OffensiveFormation offense in OffensiveFormations)
            {
                foreach (Systems_DefensiveFormation defense in DefensiveFormations)
                {
                    foreach (float lineOfScrimmageY in linesOfScrimmage)
                    {
                        for (int index = 0; index < Systems_Formation.SlotCount; index++)
                        {
                            Systems_FormationSlot slot = SlotOf(offense, defense, index);

                            Assert.That(
                                field.IsOutsideSidelines(slot.OffsetX), Is.False,
                                $"{offense}/{defense}: slot {index} spawns outside the sidelines");

                            float y = lineOfScrimmageY + slot.OffsetY;
                            Assert.That(
                                y, Is.GreaterThan(Systems_FieldModel.OWN_BACK_LINE_Y),
                                $"{offense}/{defense}: slot {index} spawns behind its own end "
                                + $"zone at LOS {lineOfScrimmageY}");
                            Assert.That(
                                y, Is.LessThan(Systems_FieldModel.ATTACKING_BACK_LINE_Y),
                                $"{offense}/{defense}: slot {index} spawns beyond the far end "
                                + $"zone at LOS {lineOfScrimmageY}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A defender may only be assigned to an eligible receiver, and no two
        /// defenders may be assigned the same one. Double-covering the tight end
        /// while a split end runs free is the exact bug a hand-written coverage
        /// table produces, and it is invisible until someone watches the play.
        /// </summary>
        [Test]
        public void EveryCoverageAssignment_IsAnEligibleReceiver_CoveredOnce()
        {
            foreach (Systems_DefensiveFormation defense in DefensiveFormations)
            {
                var covered = new System.Collections.Generic.HashSet<int>();

                for (int slot = Systems_RoleTable.SQUAD_SIZE;
                    slot < Systems_Formation.SlotCount;
                    slot++)
                {
                    int assigned =
                        Systems_FormationBook.CoverageAssignmentFor(defense, slot);

                    if (assigned < 0)
                    {
                        continue;
                    }

                    Systems_PlayerRole role = Systems_Formation.RoleFor(assigned);

                    Assert.That(
                        role,
                        Is.EqualTo(Systems_PlayerRole.WideReceiver)
                            .Or.EqualTo(Systems_PlayerRole.TightEnd),
                        $"{defense}: defender {slot} is assigned slot {assigned}, a {role}");

                    Assert.That(
                        covered.Add(assigned), Is.True,
                        $"{defense}: slot {assigned} is covered by more than one defender");
                }
            }
        }
    }
}
