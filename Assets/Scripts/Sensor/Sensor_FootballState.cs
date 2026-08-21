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
        public const int OBSERVATION_SIZE = 36;

        /// <summary>
        /// Number of real play calls, excluding None. Width of the call one-hot.
        ///
        /// Went 4 -> 6 when Punt and FieldGoal were added, which is why
        /// OBSERVATION_SIZE went 32 -> 34: the one-hot is written straight into the
        /// observation vector, so every extra call is another float every agent
        /// reads. Both numbers are checked by Tools/promote_brain.py.
        /// </summary>
        public const int PLAY_CALL_SLOTS = 6;

        /// <summary>
        /// Size of the quarterback's play-call discrete branch: the four real calls
        /// plus index 0, which means "I have not called anything yet".
        ///
        /// That extra slot is the whole point. The branch used to be four wide and
        /// the agent added one to it, so index 0 meant KeepQuarterback — and a
        /// zeroed action buffer, which is exactly what ML-Agents hands back on the
        /// steps between EndEpisode and the next decision, was indistinguishable
        /// from the quarterback deliberately calling a keeper. Run football_base03
        /// latched KeepQuarterback on 8 of 8 instrumented plays for that reason.
        /// The previous defence was a timing guard (latch only once PhysicsTick has
        /// passed DECISION_PERIOD), which depends on the Academy's step counter
        /// happening to line up with the episode boundary.
        ///
        /// With a no-call slot at index 0 the mapping to Systems_PlayCall is the
        /// identity, and a zeroed buffer decodes to Systems_PlayCall.None, which
        /// Systems_PlayModel.LatchCall rejects. A stale buffer can no longer latch
        /// anything, by construction rather than by timing.
        /// </summary>
        public const int PLAY_CALL_BRANCH_SIZE = PLAY_CALL_SLOTS + 1;

        /// <summary>
        /// Distance at which yards-to-go is treated as "long" and the observation
        /// saturates. Past about fifteen the exact number stops changing anyone's
        /// job, and clamping keeps the value inside [0, 1] like every other float
        /// in this vector.
        /// </summary>
        private const float LONG_YARDAGE_YARDS = 15f;

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
            float lineOfScrimmageY,
            int down,
            float yardsToGo)
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

            // The situation: 2. Down normalized to [0, 1] across the four downs,
            // and distance normalized against a long-yardage cap.
            //
            // Every player sees these, not just the offense. A defense that cannot
            // tell third and one from third and fifteen has no basis for playing the
            // run or dropping into coverage, and the down is public information on a
            // real field — it is on the scoreboard. Only the play CALL is hidden,
            // and that stays hidden below.
            buffer[cursor++] =
                Mathf.Clamp01((down - 1f) / (Systems_GameRules.DOWNS_PER_SERIES - 1f));
            buffer[cursor++] = Mathf.Clamp01(yardsToGo / LONG_YARDAGE_YARDS);

            // Play call one-hot: 6, offense only.
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
            float lineOfScrimmageY,
            int down,
            float yardsToGo)
        {
            Write(
                buffer, field, role, position, velocity, rotationDegrees, fatigue,
                isCarrier, ballPosition, ballVelocity, ballState, call, lineOfScrimmageY,
                down, yardsToGo);

            sensor.AddObservation(buffer);
        }

        private static float NormalizeRange(float metres)
        {
            return Mathf.Clamp(
                metres / Systems_SimConstants.OBSERVATION_RANGE, -1f, 1f);
        }
    }
}
