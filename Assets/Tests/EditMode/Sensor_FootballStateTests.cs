using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Sensors;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// Acceptance criterion #14. The trainer config sets
    /// network_settings.normalize: false, on the grounds that the sensors already
    /// emit [-1, 1]. That is an assumption the network depends on, so it is worth
    /// asserting against deliberately hostile inputs rather than typical ones.
    /// </summary>
    public sealed class Sensor_FootballStateTests
    {
        private static float[] Collect(
            Systems_PlayerRole role,
            Vector2 position,
            Vector2 velocity,
            float rotationDegrees,
            float fatigue,
            bool isCarrier,
            Vector2 ballPosition,
            Vector2 ballVelocity,
            float lineOfScrimmageY,
            Systems_BallState ballState = Systems_BallState.Held,
            Systems_PlayCall call = Systems_PlayCall.None)
        {
            float[] buffer = new float[Sensor_FootballState.OBSERVATION_SIZE];

            Sensor_FootballState.Write(
                buffer, new Systems_FieldModel(), role, position, velocity,
                rotationDegrees, fatigue, isCarrier, ballPosition, ballVelocity,
                ballState, call, lineOfScrimmageY);

            return buffer;
        }

        /// <summary>
        /// The quarterback commits to a call and every team-mate is told. Leaking
        /// it to the defense would let coverage cheat, and the whole point of a
        /// pre-snap read is that the other side has to guess.
        /// </summary>
        [Test]
        public void ThePlayCall_IsVisibleToTheOffenseAndHiddenFromTheDefense()
        {
            int callStart =
                Sensor_FootballState.OBSERVATION_SIZE - Sensor_FootballState.PLAY_CALL_SLOTS;

            float[] offense = Collect(
                Systems_PlayerRole.WideReceiver, Vector2.zero, Vector2.zero, 0f, 0f, false,
                Vector2.zero, Vector2.zero, 0f, Systems_BallState.Held, Systems_PlayCall.Pass);

            float[] defense = Collect(
                Systems_PlayerRole.Cornerback, Vector2.zero, Vector2.zero, 0f, 0f, false,
                Vector2.zero, Vector2.zero, 0f, Systems_BallState.Held, Systems_PlayCall.Pass);

            float offenseSum = 0f;
            float defenseSum = 0f;
            for (int i = callStart; i < Sensor_FootballState.OBSERVATION_SIZE; i++)
            {
                offenseSum += offense[i];
                defenseSum += defense[i];
            }

            Assert.That(offenseSum, Is.EqualTo(1f).Within(1e-5f), "offense should see the call");
            Assert.That(defenseSum, Is.EqualTo(0f).Within(1e-5f), "defense must not see the call");
        }

        [Test]
        public void BallStateFlags_AreMutuallyExclusive()
        {
            float[] held = Collect(
                Systems_PlayerRole.Safety, Vector2.zero, Vector2.zero, 0f, 0f, false,
                Vector2.zero, Vector2.zero, 0f, Systems_BallState.Held);
            float[] flying = Collect(
                Systems_PlayerRole.Safety, Vector2.zero, Vector2.zero, 0f, 0f, false,
                Vector2.zero, Vector2.zero, 0f, Systems_BallState.InFlight);

            const int heldIndex = Systems_RoleTable.ROLE_COUNT + 12;
            const int flightIndex = heldIndex + 1;

            Assert.That(held[heldIndex], Is.EqualTo(1f).Within(1e-5f));
            Assert.That(held[flightIndex], Is.EqualTo(0f).Within(1e-5f));
            Assert.That(flying[heldIndex], Is.EqualTo(0f).Within(1e-5f));
            Assert.That(flying[flightIndex], Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void ObservationSize_MatchesTheDeclaredConstant()
        {
            float[] observations = Collect(
                Systems_PlayerRole.RunningBack, Vector2.zero, Vector2.zero,
                0f, 0f, true, Vector2.zero, Vector2.zero, 0f);

            Assert.That(observations.Length, Is.EqualTo(Sensor_FootballState.OBSERVATION_SIZE));
        }

        [Test]
        public void EveryObservation_StaysInRange_UnderExtremeInputs()
        {
            Vector2[] positions =
            {
                Vector2.zero,
                new Vector2(9999f, 9999f),
                new Vector2(-9999f, -9999f),
                new Vector2(Systems_FieldModel.HALF_WIDTH, Systems_FieldModel.ATTACKING_BACK_LINE_Y)
            };

            Vector2[] velocities =
            {
                Vector2.zero,
                new Vector2(500f, -500f),
                new Vector2(-500f, 500f)
            };

            float[] rotations = { -3600f, 0f, 187f, 3600f };
            float[] fatigues = { -5f, 0f, 0.5f, 5f };

            foreach (Systems_PlayerRole role in System.Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                foreach (Vector2 position in positions)
                {
                    foreach (Vector2 velocity in velocities)
                    {
                        foreach (float rotation in rotations)
                        {
                            foreach (float fatigue in fatigues)
                            {
                                float[] observations = Collect(
                                    role, position, velocity, rotation, fatigue,
                                    false, -position, -velocity, 9999f);

                                for (int i = 0; i < observations.Length; i++)
                                {
                                    Assert.That(
                                        observations[i], Is.InRange(-1f, 1f),
                                        $"index {i} = {observations[i]} for role {role}, "
                                        + $"pos {position}, vel {velocity}, rot {rotation}, fatigue {fatigue}");
                                }
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void NoObservation_IsNaNOrInfinite()
        {
            float[] observations = Collect(
                Systems_PlayerRole.Safety,
                new Vector2(9999f, -9999f), new Vector2(-500f, 500f),
                720f, 3f, true, new Vector2(-9999f, 9999f), Vector2.zero, -9999f);

            for (int i = 0; i < observations.Length; i++)
            {
                Assert.That(float.IsNaN(observations[i]), Is.False, $"index {i} is NaN");
                Assert.That(float.IsInfinity(observations[i]), Is.False, $"index {i} is infinite");
            }
        }

        [Test]
        public void RoleOneHot_HasExactlyOneHotBit()
        {
            foreach (Systems_PlayerRole role in System.Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                float[] observations = Collect(
                    role, Vector2.zero, Vector2.zero, 0f, 0f, false,
                    Vector2.zero, Vector2.zero, 0f);

                float sum = 0f;
                for (int i = 0; i < Systems_RoleTable.ROLE_COUNT; i++)
                {
                    sum += observations[i];
                }

                Assert.That(sum, Is.EqualTo(1f).Within(1e-5f), $"{role} one-hot sums to {sum}");
                Assert.That(observations[(int)role], Is.EqualTo(1f).Within(1e-5f));
            }
        }

        [Test]
        public void CarrierFlag_IsSetOnlyForTheCarrier()
        {
            const int carrierFlagIndex = Systems_RoleTable.ROLE_COUNT + 7;

            float[] asCarrier = Collect(
                Systems_PlayerRole.RunningBack, Vector2.zero, Vector2.zero,
                0f, 0f, true, Vector2.zero, Vector2.zero, 0f);
            float[] notCarrier = Collect(
                Systems_PlayerRole.RunningBack, Vector2.zero, Vector2.zero,
                0f, 0f, false, Vector2.zero, Vector2.zero, 0f);

            Assert.That(asCarrier[carrierFlagIndex], Is.EqualTo(1f).Within(1e-5f));
            Assert.That(notCarrier[carrierFlagIndex], Is.EqualTo(0f).Within(1e-5f));
        }
    }
}
