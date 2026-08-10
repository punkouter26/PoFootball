using PoFootball.Models;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoFootball.Sensors
{
    /// <summary>
    /// The hand-built half of an agent's observation. The other half is the
    /// RayPerceptionSensor2D component, which covers nearby bodies; this covers
    /// self, ball and field geometry.
    ///
    /// Every value written here is already in [-1, 1]. That is load-bearing rather
    /// than cosmetic: the trainer config sets network_settings.normalize: false,
    /// because running normalization on top of self-play fights the shifting input
    /// distribution (acceptance criterion #14).
    ///
    /// Written into a caller-owned buffer rather than straight into the sensor, so
    /// the observation vector is a pure function that tests can inspect —
    /// VectorSensor keeps its contents internal. The agent owns one buffer for its
    /// lifetime, so this still allocates nothing per tick (criterion #17).
    /// </summary>
    public static class Sensor_FootballState
    {
        /// <summary>
        /// Must equal BrainParameters.VectorObservationSize or ML-Agents throws at
        /// the first step. Agent_FootballPlayer sets that field from this constant.
        /// </summary>
        public const int OBSERVATION_SIZE = 32;

        /// <summary>Number of real play calls, excluding None. Width of the call one-hot.</summary>
        public const int PLAY_CALL_SLOTS = 4;

        /// <summary>
        /// Fills <paramref name="buffer"/> with exactly OBSERVATION_SIZE values,
        /// all within [-1, 1].
        ///
        /// The play call is written only for the offense. Defenders get a zeroed
        /// slice, so committing to a call cannot leak it to the other side.
        /// </summary>
        public static void Write(
            float[] buffer,
            Systems_FieldModel field,
            Systems_PlayerRole role,
            Vector2 position,
            Vector2 velocity,
            float rotationDegrees,
            float fatigue,
            bool isCarrier,
            Vector2 ballPosition,
            Vector2 ballVelocity,
            Systems_BallState ballState,
            Systems_PlayCall call,
            float lineOfScrimmageY)
        {
            int cursor = 0;

            // Role one-hot: 10
            for (int roleIndex = 0; roleIndex < Systems_RoleTable.ROLE_COUNT; roleIndex++)
            {
                buffer[cursor++] = roleIndex == (int)role ? 1f : 0f;
            }

            // Own position on the field: 2
            buffer[cursor++] = field.NormalizeX(position.x);
            buffer[cursor++] = field.NormalizeY(position.y);

            // Own velocity relative to this role's top speed: 2
            float topSpeed = Systems_RoleTable.TopSpeedOf(role);
            buffer[cursor++] = Mathf.Clamp(velocity.x / topSpeed, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(velocity.y / topSpeed, -1f, 1f);

            // Facing as cos/sin so it is continuous across the 0/360 wrap: 2
            float rotationRadians = rotationDegrees * Mathf.Deg2Rad;
            buffer[cursor++] = Mathf.Cos(rotationRadians);
            buffer[cursor++] = Mathf.Sin(rotationRadians);

            // Fatigue and possession: 2
            buffer[cursor++] = Mathf.Clamp01(fatigue);
            buffer[cursor++] = isCarrier ? 1f : 0f;

            // Ball relative to self, and how fast it is travelling: 4
            Vector2 toBall = ballPosition - position;
            buffer[cursor++] = NormalizeRange(toBall.x);
            buffer[cursor++] = NormalizeRange(toBall.y);
            buffer[cursor++] = Mathf.Clamp(
                ballVelocity.x / Systems_SimConstants.PASS_SPEED_MAX, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(
                ballVelocity.y / Systems_SimConstants.PASS_SPEED_MAX, -1f, 1f);

            // Ball state: 2. Defenders need to see a live ball in the air.
            buffer[cursor++] = ballState == Systems_BallState.Held ? 1f : 0f;
            buffer[cursor++] = ballState == Systems_BallState.InFlight ? 1f : 0f;

            // Field geometry: 4
            buffer[cursor++] =
                Mathf.Clamp(field.YardsToAttackingGoal(position.y) / 100f, -1f, 1f);
            buffer[cursor++] = NormalizeRange(Systems_FieldModel.HALF_WIDTH - position.x);
            buffer[cursor++] = NormalizeRange(Systems_FieldModel.HALF_WIDTH + position.x);
            buffer[cursor++] = NormalizeRange(lineOfScrimmageY - position.y);

            // Play call one-hot: 4, offense only.
            bool isOffense = Systems_RoleTable.SideOf(role) == Systems_TeamSide.Offense;

            for (int callIndex = 0; callIndex < PLAY_CALL_SLOTS; callIndex++)
            {
                bool active = isOffense && (int)call == callIndex + 1;
                buffer[cursor++] = active ? 1f : 0f;
            }
        }

        /// <summary>Fills the buffer and pushes it into the sensor in one call.</summary>
        public static void Collect(
            VectorSensor sensor,
            float[] buffer,
            Systems_FieldModel field,
            Systems_PlayerRole role,
            Vector2 position,
            Vector2 velocity,
            float rotationDegrees,
            float fatigue,
            bool isCarrier,
            Vector2 ballPosition,
            Vector2 ballVelocity,
            Systems_BallState ballState,
            Systems_PlayCall call,
            float lineOfScrimmageY)
        {
            Write(
                buffer, field, role, position, velocity, rotationDegrees, fatigue,
                isCarrier, ballPosition, ballVelocity, ballState, call, lineOfScrimmageY);

            sensor.AddObservation(buffer);
        }

        private static float NormalizeRange(float metres)
        {
            return Mathf.Clamp(
                metres / Systems_SimConstants.OBSERVATION_RANGE, -1f, 1f);
        }
    }
}
