using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// Guards the separation between the learning process and the game.
    ///
    /// Systems_EpisodeDirector used to assign terminal rewards and end every
    /// agent's ML-Agents episode inline on every play, in both modes. A played game
    /// therefore ran the whole reward pipeline once per down to produce numbers no
    /// policy would ever read. These tests pin the two halves of the fix: the
    /// training binding still does the work, and the game binding does nothing at
    /// all.
    /// </summary>
    public sealed class Systems_EpisodeBoundaryTests
    {
        /// <summary>
        /// Implements BOTH contracts, exactly as Agent_FootballPlayer does, and
        /// counts what it was asked to do.
        /// </summary>
        private sealed class StubAgent : Systems_IPlayerHandle, Systems_ITrainingHandle
        {
            public int Id { get; set; }

            public Systems_PlayerRole Role { get; set; }

            public Systems_TeamSide Side => Systems_RoleTable.SideOf(Role);

            public Vector2 Position { get; set; }

            public Vector2 Velocity { get; set; }

            public bool IsCarrier { get; private set; }

            public float Fatigue => 0f;

            public int RewardCount { get; private set; }

            public int EndEpisodeCount { get; private set; }

            /// <summary>
            /// Rewards recorded after this agent's episode was closed. Must stay at
            /// zero: a reward paid after EndEpisode lands on the wrong episode.
            /// </summary>
            public int RewardsAfterEnd { get; private set; }

            public void ClearFatigue() { }

            public void Freeze() { }

            public void ResetTo(Vector2 position) => Position = position;

            public void SetCarrier(bool isCarrier) => IsCarrier = isCarrier;

            public Color TeamColor { get; private set; } = Color.white;

            public void SetTeamColor(Color color) => TeamColor = color;

            public void ApplyTerminalReward(
                Systems_PlayOutcome outcome, float netYards, bool passCompleted)
            {
                RewardCount++;

                if (EndEpisodeCount > 0)
                {
                    RewardsAfterEnd++;
                }
            }

            public void EndEpisodeNow() => EndEpisodeCount++;
        }

        /// <summary>
        /// Only the gameplay half. Stands in for anything in the scene that is a
        /// player but not an agent — and proves the training boundary skips it
        /// rather than throwing.
        /// </summary>
        private sealed class StubNonAgent : Systems_IPlayerHandle
        {
            public int Id { get; set; }

            public Systems_PlayerRole Role { get; set; }

            public Systems_TeamSide Side => Systems_RoleTable.SideOf(Role);

            public Vector2 Position { get; set; }

            public Vector2 Velocity { get; set; }

            public bool IsCarrier { get; private set; }

            public float Fatigue => 0f;

            public void ClearFatigue() { }

            public void Freeze() { }

            public void ResetTo(Vector2 position) => Position = position;

            public void SetCarrier(bool isCarrier) => IsCarrier = isCarrier;

            public void SetTeamColor(Color color)
            {
            }
        }

        private static Systems_PlayerRegistry FillRegistry(out StubAgent[] agents)
        {
            Systems_PlayerRegistry registry = new Systems_PlayerRegistry();
            agents = new StubAgent[Systems_PlayerRegistry.CAPACITY];

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                StubAgent agent = new StubAgent
                {
                    Id = slotIndex,
                    Role = Systems_Formation.GetSlot(slotIndex).Role
                };

                agents[slotIndex] = agent;
                registry.Register(slotIndex, agent);
            }

            return registry;
        }

        [Test]
        public void TrainingBoundary_RewardsAndEndsEveryPlayerExactlyOnce()
        {
            Systems_PlayerRegistry registry = FillRegistry(out StubAgent[] agents);

            Systems_TrainingEpisodeBoundary boundary =
                new Systems_TrainingEpisodeBoundary(registry);

            boundary.EndEpisode(Systems_PlayOutcome.Tackle, 4.5f, false);

            for (int slotIndex = 0; slotIndex < agents.Length; slotIndex++)
            {
                Assert.That(agents[slotIndex].RewardCount, Is.EqualTo(1), $"slot {slotIndex}");
                Assert.That(agents[slotIndex].EndEpisodeCount, Is.EqualTo(1), $"slot {slotIndex}");
            }
        }

        /// <summary>
        /// The ordering constraint the two separate loops exist to satisfy. Ending
        /// an ML-Agents episode requests the next decision, so every reward for a
        /// play has to be paid before any episode for that play is closed.
        /// </summary>
        [Test]
        public void TrainingBoundary_PaysEveryRewardBeforeClosingAnyEpisode()
        {
            Systems_PlayerRegistry registry = FillRegistry(out StubAgent[] agents);

            new Systems_TrainingEpisodeBoundary(registry)
                .EndEpisode(Systems_PlayOutcome.Touchdown, 60f, true);

            for (int slotIndex = 0; slotIndex < agents.Length; slotIndex++)
            {
                Assert.That(
                    agents[slotIndex].RewardsAfterEnd,
                    Is.Zero,
                    $"slot {slotIndex} was rewarded after its episode had already ended");
            }
        }

        [Test]
        public void TrainingBoundary_SkipsPlayersThatDoNotTrain()
        {
            Systems_PlayerRegistry registry = new Systems_PlayerRegistry();

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                registry.Register(slotIndex, new StubNonAgent
                {
                    Id = slotIndex,
                    Role = Systems_Formation.GetSlot(slotIndex).Role
                });
            }

            Systems_TrainingEpisodeBoundary boundary =
                new Systems_TrainingEpisodeBoundary(registry);

            Assert.DoesNotThrow(
                () => boundary.EndEpisode(Systems_PlayOutcome.Tackle, 1f, false));
        }

        /// <summary>
        /// The whole point of the split: in a played game the reward pipeline is
        /// not merely ignored, it is never entered.
        /// </summary>
        [Test]
        public void NullBoundary_TouchesNobody()
        {
            Systems_PlayerRegistry registry = FillRegistry(out StubAgent[] agents);

            new Systems_NullEpisodeBoundary()
                .EndEpisode(Systems_PlayOutcome.Touchdown, 42f, true);

            for (int slotIndex = 0; slotIndex < agents.Length; slotIndex++)
            {
                Assert.That(agents[slotIndex].RewardCount, Is.Zero, $"slot {slotIndex}");
                Assert.That(agents[slotIndex].EndEpisodeCount, Is.Zero, $"slot {slotIndex}");
            }

            // The registry is untouched by the null boundary but still had to be
            // resolvable — a game binds it for real.
            Assert.That(registry.IsComplete, Is.True);
        }
    }
}
