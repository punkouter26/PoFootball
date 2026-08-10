using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// The formation is the one thing that guarantees acceptance criterion #12
    /// (no overlapping spawns) by construction rather than by rejection sampling.
    /// If someone nudges a slot, this suite is what catches it.
    /// </summary>
    public sealed class Systems_FormationTests
    {
        [Test]
        public void SlotCount_IsTwoFullSquads()
        {
            Assert.That(Systems_Formation.SlotCount, Is.EqualTo(Systems_PlayerRegistry.CAPACITY));
            Assert.That(Systems_Formation.SlotCount, Is.EqualTo(22));
        }

        /// <summary>Acceptance criterion #12.</summary>
        [Test]
        public void EverySlotPair_IsAtLeastMinimumSeparationApart()
        {
            for (int a = 0; a < Systems_Formation.SlotCount; a++)
            {
                Systems_FormationSlot slotA = Systems_Formation.GetSlot(a);

                for (int b = a + 1; b < Systems_Formation.SlotCount; b++)
                {
                    Systems_FormationSlot slotB = Systems_Formation.GetSlot(b);

                    float distance = Vector2.Distance(
                        new Vector2(slotA.OffsetX, slotA.OffsetY),
                        new Vector2(slotB.OffsetX, slotB.OffsetY));

                    Assert.That(
                        distance,
                        Is.GreaterThanOrEqualTo(Systems_SimConstants.MIN_SPAWN_SEPARATION),
                        $"slots {a} ({slotA.Role}) and {b} ({slotB.Role}) are {distance:F3} m apart");
                }
            }
        }

        [Test]
        public void Slots_AreElevenOffenceThenElevenDefence()
        {
            for (int index = 0; index < Systems_RoleTable.SQUAD_SIZE; index++)
            {
                Assert.That(
                    Systems_RoleTable.SideOf(Systems_Formation.GetSlot(index).Role),
                    Is.EqualTo(Systems_TeamSide.Offense),
                    $"slot {index} should be offense");
            }

            for (int index = Systems_RoleTable.SQUAD_SIZE; index < Systems_Formation.SlotCount; index++)
            {
                Assert.That(
                    Systems_RoleTable.SideOf(Systems_Formation.GetSlot(index).Role),
                    Is.EqualTo(Systems_TeamSide.Defense),
                    $"slot {index} should be defense");
            }
        }

        [Test]
        public void TheQuarterbackTakesTheSnap()
        {
            Systems_FormationSlot quarterback =
                Systems_Formation.GetSlot(Systems_Formation.QUARTERBACK_SLOT_INDEX);

            Assert.That(quarterback.Role, Is.EqualTo(Systems_PlayerRole.Quarterback));
        }

        [Test]
        public void HandoffTargetSlots_PointAtTheTwoBacks()
        {
            Assert.That(
                Systems_Formation.GetSlot(Systems_Formation.FULLBACK_SLOT_INDEX).Role,
                Is.EqualTo(Systems_PlayerRole.Fullback));
            Assert.That(
                Systems_Formation.GetSlot(Systems_Formation.HALFBACK_SLOT_INDEX).Role,
                Is.EqualTo(Systems_PlayerRole.RunningBack));
        }

        /// <summary>
        /// The I-formation stacks quarterback, fullback and halfback on one line.
        /// A handoff only completes inside HANDOFF_RADIUS, so if the backs ever
        /// spawn further apart than that the run game silently stops working.
        /// </summary>
        [Test]
        public void BacksSpawnCloseEnoughForAHandoffToBeReachable()
        {
            Systems_FormationSlot quarterback =
                Systems_Formation.GetSlot(Systems_Formation.QUARTERBACK_SLOT_INDEX);
            Systems_FormationSlot fullback =
                Systems_Formation.GetSlot(Systems_Formation.FULLBACK_SLOT_INDEX);
            Systems_FormationSlot halfback =
                Systems_Formation.GetSlot(Systems_Formation.HALFBACK_SLOT_INDEX);

            Assert.That(quarterback.OffsetX, Is.EqualTo(fullback.OffsetX).Within(0.01f));
            Assert.That(quarterback.OffsetX, Is.EqualTo(halfback.OffsetX).Within(0.01f));
            Assert.That(fullback.OffsetY, Is.GreaterThan(halfback.OffsetY));
        }

        [Test]
        public void Offense_LinesUpBehindTheLine_DefenseInFront()
        {
            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                Systems_FormationSlot slot = Systems_Formation.GetSlot(index);
                bool isOffense = Systems_RoleTable.SideOf(slot.Role) == Systems_TeamSide.Offense;

                if (isOffense)
                {
                    Assert.That(slot.OffsetY, Is.LessThan(0f), $"offense slot {index} is past the LOS");
                }
                else
                {
                    Assert.That(slot.OffsetY, Is.GreaterThan(0f), $"defense slot {index} is behind the LOS");
                }
            }
        }

        [Test]
        public void RoleCounts_MatchARealElevenManFormation()
        {
            int[] counts = new int[Systems_RoleTable.ROLE_COUNT];

            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                counts[(int)Systems_Formation.GetSlot(index).Role]++;
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

            foreach (float lineOfScrimmageY in linesOfScrimmage)
            {
                for (int index = 0; index < Systems_Formation.SlotCount; index++)
                {
                    Systems_FormationSlot slot = Systems_Formation.GetSlot(index);

                    Assert.That(
                        field.IsOutsideSidelines(slot.OffsetX), Is.False,
                        $"slot {index} spawns outside the sidelines");

                    float y = lineOfScrimmageY + slot.OffsetY;
                    Assert.That(
                        y, Is.GreaterThan(Systems_FieldModel.OWN_BACK_LINE_Y),
                        $"slot {index} spawns behind its own end zone at LOS {lineOfScrimmageY}");
                    Assert.That(
                        y, Is.LessThan(Systems_FieldModel.ATTACKING_BACK_LINE_Y),
                        $"slot {index} spawns beyond the far end zone at LOS {lineOfScrimmageY}");
                }
            }
        }
    }
}
