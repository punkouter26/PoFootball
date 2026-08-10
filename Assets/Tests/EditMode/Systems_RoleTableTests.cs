using System.IO;
using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    public sealed class Systems_RoleTableTests
    {
        /// <summary>
        /// The single highest-value assertion in the suite. Behavior names are
        /// C# string literals on one side and YAML keys on the other, with no
        /// compiler check between them. If they drift apart the handshake still
        /// succeeds and training still appears to run — the affected agents simply
        /// never receive actions, silently, for the whole run.
        /// </summary>
        [Test]
        public void BehaviorNames_MatchTheTrainerConfigKeys()
        {
            string configPath = Path.Combine(
                Application.dataPath, "..", "Config", "FootballBase02.yaml");

            Assert.That(File.Exists(configPath), Is.True, $"config not found at {configPath}");

            string yaml = File.ReadAllText(configPath);

            foreach (Systems_BrainGroup group in System.Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                string behaviorName = Systems_RoleTable.BehaviorNameOf(group);

                Assert.That(
                    yaml.Contains($"  {behaviorName}:"),
                    Is.True,
                    $"behavior '{behaviorName}' has no matching key in FootballBase02.yaml");
            }
        }

        [Test]
        public void BehaviorNames_AreDistinct()
        {
            string offenseLine = Systems_RoleTable.BehaviorNameOf(Systems_BrainGroup.OffenseLine);
            string offenseSkill = Systems_RoleTable.BehaviorNameOf(Systems_BrainGroup.OffenseSkill);
            string defenseLine = Systems_RoleTable.BehaviorNameOf(Systems_BrainGroup.DefenseLine);
            string defenseCover = Systems_RoleTable.BehaviorNameOf(Systems_BrainGroup.DefenseCover);

            Assert.That(
                new[] { offenseLine, offenseSkill, defenseLine, defenseCover },
                Is.Unique);
        }

        [Test]
        public void BrainGroups_HaveTheExpectedAgentCounts()
        {
            int[] counts = new int[4];

            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                counts[(int)Systems_RoleTable.BrainOf(Systems_Formation.GetSlot(index).Role)]++;
            }

            Assert.That(counts[(int)Systems_BrainGroup.OffenseLine], Is.EqualTo(5));
            Assert.That(counts[(int)Systems_BrainGroup.OffenseSkill], Is.EqualTo(6));
            Assert.That(counts[(int)Systems_BrainGroup.DefenseLine], Is.EqualTo(4));
            Assert.That(counts[(int)Systems_BrainGroup.DefenseCover], Is.EqualTo(7));
        }

        [Test]
        public void EveryBrainGroup_SitsEntirelyOnOneTeam()
        {
            // Asymmetric self-play depends on this: a behavior spanning both team
            // ids would have no coherent opponent to be rated against.
            foreach (Systems_BrainGroup group in System.Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                bool sideSeen = false;
                Systems_TeamSide expected = Systems_TeamSide.Offense;

                for (int index = 0; index < Systems_Formation.SlotCount; index++)
                {
                    Systems_PlayerRole role = Systems_Formation.GetSlot(index).Role;
                    if (Systems_RoleTable.BrainOf(role) != group)
                    {
                        continue;
                    }

                    Systems_TeamSide side = Systems_RoleTable.SideOf(role);
                    if (!sideSeen)
                    {
                        expected = side;
                        sideSeen = true;
                        continue;
                    }

                    Assert.That(side, Is.EqualTo(expected), $"{group} spans both teams");
                }
            }
        }

        [Test]
        public void LinemenAreSlowerThanSkillPlayers()
        {
            Assert.That(
                Systems_RoleTable.TopSpeedOf(Systems_PlayerRole.OffensiveLine),
                Is.LessThan(Systems_RoleTable.TopSpeedOf(Systems_PlayerRole.WideReceiver)));

            Assert.That(
                Systems_RoleTable.TopSpeedOf(Systems_PlayerRole.DefensiveLine),
                Is.LessThan(Systems_RoleTable.TopSpeedOf(Systems_PlayerRole.Cornerback)));
        }

        [Test]
        public void EveryTopSpeed_IsBelowTheBodyVelocityClamp()
        {
            foreach (Systems_PlayerRole role in System.Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                Assert.That(
                    Systems_RoleTable.TopSpeedOf(role),
                    Is.LessThan(Systems_SimConstants.MAX_BODY_SPEED),
                    $"{role} top speed exceeds the pileup clamp, which would make the clamp the real limit");
            }
        }

        [Test]
        public void RoleCount_CoversEveryEnumValue()
        {
            Assert.That(
                System.Enum.GetValues(typeof(Systems_PlayerRole)).Length,
                Is.EqualTo(Systems_RoleTable.ROLE_COUNT),
                "ROLE_COUNT drives the one-hot width in Sensor_FootballState");
        }
    }
}
