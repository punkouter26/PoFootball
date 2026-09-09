using System;
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
        /// The trainer config the shipped contract is paired with. Bumping this is
        /// part of changing the observation or action contract — the config and the
        /// run-id are paired by name (UNITY_RULES §1).
        /// </summary>
        /// <summary>
        /// Filename prefix of the anchor configs — see the matching note in
        /// Systems_ContractTests. Discovered rather than hardcoded, because the
        /// hardcoded form pointed at an archived file once and at a superseded one
        /// again, and a guard that cannot find its config guards nothing.
        /// </summary>
        private const string CONFIG_FILE_PREFIX = "FootballBase";

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
            string yaml = ReadTrainerConfig();

            foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                string behaviorName = Systems_RoleTable.BehaviorNameOf(group);

                Assert.That(
                    yaml.Contains($"  {behaviorName}:"),
                    Is.True,
                    $"behavior '{behaviorName}' has no matching key in {CurrentConfigName()}");
            }
        }

        /// <summary>
        /// The other direction. A YAML key with no brain behind it is a behavior
        /// the trainer allocates an optimizer and a self-play window for and then
        /// never receives a single experience from — it shows up as a flat ELO
        /// curve rather than as an error.
        /// </summary>
        [Test]
        public void TrainerConfigKeys_AllHaveABrainBehindThem()
        {
            string yaml = ReadTrainerConfig();
            string[] lines = yaml.Split('\n');
            bool inBehaviors = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');

                // Any unindented key ends the behaviors block — env_settings and
                // engine_settings also carry two-space-indented keys, and scanning
                // those would read `seed` as a behavior name.
                if (line.Length > 0 && !line.StartsWith(" ") && !line.StartsWith("#"))
                {
                    inBehaviors = line.StartsWith("behaviors:");
                    continue;
                }

                if (!inBehaviors)
                {
                    continue;
                }

                // Behavior keys are the only two-space-indented mapping keys under
                // `behaviors:`; anything deeper is hyperparameters.
                if (!line.StartsWith("  ") || line.StartsWith("   ") || !line.Contains(":"))
                {
                    continue;
                }

                string key = line.Substring(2, line.IndexOf(':') - 2).Trim();
                if (key.Length == 0 || key.StartsWith("#"))
                {
                    continue;
                }


                bool known = false;
                foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
                {
                    if (Systems_RoleTable.BehaviorNameOf(group) == key)
                    {
                        known = true;
                        break;
                    }
                }

                Assert.That(
                    known, Is.True,
                    $"'{key}' is a behavior in {CurrentConfigName()} that no Systems_BrainGroup maps to");
            }
        }

        [Test]
        public void BehaviorNames_AreDistinct()
        {
            Array groups = Enum.GetValues(typeof(Systems_BrainGroup));
            string[] names = new string[groups.Length];

            for (int index = 0; index < groups.Length; index++)
            {
                names[index] = Systems_RoleTable.BehaviorNameOf((Systems_BrainGroup)groups.GetValue(index));
            }

            Assert.That(names, Is.Unique);
        }

        [Test]
        public void BrainGroups_HaveTheExpectedAgentCounts()
        {
            int[] counts = new int[Enum.GetValues(typeof(Systems_BrainGroup)).Length];

            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                counts[(int)Systems_RoleTable.BrainOf(Systems_Formation.RoleFor(index))]++;
            }

            // Ten and eleven, not five/five and four/three/four: the behaviours
            // merged for MA-POCA, because a multi-agent group cannot span behavior
            // names. The quarterback stays alone — see Systems_BrainGroup.
            Assert.That(counts[(int)Systems_BrainGroup.Offense], Is.EqualTo(10));
            Assert.That(counts[(int)Systems_BrainGroup.Quarterback], Is.EqualTo(1));
            Assert.That(counts[(int)Systems_BrainGroup.Defense], Is.EqualTo(11));
        }

        /// <summary>
        /// A brain with no agent on the field is a behavior the trainer waits on
        /// forever; PPO simply never fills its buffer and the run stalls without an
        /// error message.
        /// </summary>
        [Test]
        public void EveryBrainGroup_HasAtLeastOneAgentOnTheField()
        {
            foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                bool found = false;

                for (int index = 0; index < Systems_Formation.SlotCount; index++)
                {
                    if (Systems_RoleTable.BrainOf(Systems_Formation.RoleFor(index)) == group)
                    {
                        found = true;
                        break;
                    }
                }

                Assert.That(found, Is.True, $"{group} has no player in the formation");
            }
        }

        [Test]
        public void EveryBrainGroup_SitsEntirelyOnOneTeam()
        {
            // Asymmetric self-play depends on this: a behavior spanning both team
            // ids would have no coherent opponent to be rated against.
            foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                bool sideSeen = false;
                Systems_TeamSide expected = Systems_TeamSide.Offense;

                for (int index = 0; index < Systems_Formation.SlotCount; index++)
                {
                    Systems_PlayerRole role = Systems_Formation.RoleFor(index);
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

        /// <summary>
        /// Exactly one brain may carry the play call, the throw trigger and the
        /// aim vector. Two would mean two policies latching calls against each
        /// other; none would mean no play is ever called.
        /// </summary>
        [Test]
        public void ExactlyOneBrain_CarriesTheQuarterbackActions()
        {
            int count = 0;

            foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                if (Systems_RoleTable.HasQuarterbackActions(group))
                {
                    count++;
                }
            }

            Assert.That(count, Is.EqualTo(1));
        }

        /// <summary>
        /// And the role that carries them must be the one player holding the ball
        /// at the snap, or the call is latched by somebody who cannot execute it.
        /// </summary>
        [Test]
        public void TheQuarterbackBrain_BelongsToTheSnapTaker()
        {
            Systems_PlayerRole snapTaker =
                Systems_Formation.RoleFor(Systems_Formation.QUARTERBACK_SLOT_INDEX);

            Assert.That(
                Systems_RoleTable.HasQuarterbackActions(Systems_RoleTable.BrainOf(snapTaker)),
                Is.True);

            // No other role may share that brain, or four backs and receivers are
            // trained on action dimensions nothing reads — which is what diluted
            // the play-call gradient through base03.
            for (int index = 0; index < Systems_Formation.SlotCount; index++)
            {
                Systems_PlayerRole role = Systems_Formation.RoleFor(index);
                if (role == snapTaker)
                {
                    continue;
                }

                Assert.That(
                    Systems_RoleTable.HasQuarterbackActions(Systems_RoleTable.BrainOf(role)),
                    Is.False,
                    $"{role} shares the quarterback's action space but never uses it");
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
        public void LinemenAreHeavierThanSkillPlayers()
        {
            Assert.That(
                Systems_RoleTable.MassOf(Systems_PlayerRole.OffensiveLine),
                Is.GreaterThan(Systems_RoleTable.MassOf(Systems_PlayerRole.WideReceiver)));
        }

        /// <summary>
        /// The derivation that makes the speed table real: F = v * m * d inverts
        /// Unity's damped steady state v = F / (m * d). If this ever stops holding,
        /// TopSpeedOf silently reverts to being nothing but an observation
        /// normalizer — which is exactly what it was through base03.
        /// </summary>
        [Test]
        public void DriveForce_IsDerivedSoTerminalVelocityEqualsTopSpeed()
        {
            foreach (Systems_PlayerRole role in Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                float terminalVelocity = Systems_RoleTable.DriveForceOf(role)
                    / Systems_RoleTable.MassOf(role)
                    / Systems_SimConstants.LINEAR_DAMPING;

                Assert.That(
                    terminalVelocity,
                    Is.EqualTo(Systems_RoleTable.TopSpeedOf(role)).Within(1e-3f),
                    $"{role} cannot reach the top speed the table advertises");
            }
        }

        [Test]
        public void SteerTorque_IsDerivedSoTerminalAngularVelocityEqualsTurnRate()
        {
            foreach (Systems_PlayerRole role in Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                float momentOfInertia = 0.5f * Systems_RoleTable.MassOf(role)
                    * Systems_SimConstants.PLAYER_RADIUS * Systems_SimConstants.PLAYER_RADIUS;

                float terminalAngularVelocity = Systems_RoleTable.SteerTorqueOf(role)
                    / momentOfInertia
                    / Systems_SimConstants.ANGULAR_DAMPING;

                Assert.That(
                    terminalAngularVelocity,
                    Is.EqualTo(Systems_RoleTable.TurnRateOf(role)).Within(1e-3f),
                    $"{role} cannot reach the turn rate the table advertises");
            }
        }

        [Test]
        public void EveryTopSpeed_IsBelowTheBodyVelocityClamp()
        {
            foreach (Systems_PlayerRole role in Enum.GetValues(typeof(Systems_PlayerRole)))
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
                Enum.GetValues(typeof(Systems_PlayerRole)).Length,
                Is.EqualTo(Systems_RoleTable.ROLE_COUNT),
                "ROLE_COUNT drives the one-hot width in Sensor_FootballState");
        }

        private static string ReadTrainerConfig()
        {
            return File.ReadAllText(CurrentConfigPath());
        }

        private static string CurrentConfigName()
        {
            return Path.GetFileName(CurrentConfigPath());
        }

        /// <summary>
        /// The highest-numbered Config/FootballBase*.yaml. Config/archive/ is
        /// deliberately not searched — a superseded config must not be able to
        /// satisfy a guard about the contract the code implements today.
        /// </summary>
        private static string CurrentConfigPath()
        {
            string configDirectory = Path.Combine(Application.dataPath, "..", "Config");

            Assert.That(
                Directory.Exists(configDirectory), Is.True,
                $"Config directory not found at {configDirectory}");

            string[] candidates = Directory.GetFiles(
                configDirectory, CONFIG_FILE_PREFIX + "*.yaml", SearchOption.TopDirectoryOnly);

            Assert.That(
                candidates.Length, Is.GreaterThan(0),
                $"no {CONFIG_FILE_PREFIX}*.yaml in {configDirectory}");

            Array.Sort(candidates, StringComparer.Ordinal);

            return candidates[candidates.Length - 1];
        }
    }
}
