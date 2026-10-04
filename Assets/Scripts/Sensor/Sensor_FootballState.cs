using PoFootball.Models;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoFootball.Sensors
{
    /// <summary>
    /// The hand-built half of an agent's observation. The other half is the
    /// RayPerceptionSensor2D component, which covers nearby bodies and whose
    /// geometry is fixed by Sensor_RayContract; this covers self, ball and field
    /// geometry.
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
        /// 36 -> 41 in revision 14: one more play call (PassDeep) and the player's
        /// four traits.
        public const int OBSERVATION_SIZE = 41;

        /// <summary>
        /// Number of real play calls, excluding None. Width of the call one-hot.
        ///
        /// Went 4 -> 6 when Punt and FieldGoal were added in revision 6, which is
        /// two of the four floats OBSERVATION_SIZE grew by (32 -> 36; down and
        /// distance are the other two): the one-hot is written straight into the
        /// observation vector, so every extra call is another float every agent
        /// reads. Both numbers are checked by Tools/promote_brain.py. Seven since
        /// revision 14 added PassDeep.
        /// </summary>
        public const int PLAY_CALL_SLOTS = 7;

        /// <summary>Speed, strength, agility, discipline: the last four floats.</summary>
        public const int TRAIT_SLOTS = 4;

        /// <summary>
        /// Size of the quarterback's play-call discrete branch: the six real calls
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
            Systems_PlayerTraits traits,
            Vector2 position,
            Vector2 velocity,
            float rotationDegrees,
            float angularVelocityDegrees,
            float fatigue,
            bool isCarrier,
            Vector2 ballPosition,
            Vector2 ballVelocity,
            Systems_BallState ballState,
            Systems_PlayCall call,
            float lineOfScrimmageY,
            int down,
            float yardsToGo,
            int physicsTick)
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

            // Facing, needed both for the velocity frame below and as cos/sin so it
            // is continuous across the 0/360 wrap.
            float rotationRadians = rotationDegrees * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rotationRadians);
            float sin = Mathf.Sin(rotationRadians);

            // Own velocity relative to this role's top speed, IN THE BODY'S OWN
            // FRAME: sideways, then forward along transform.up. 2
            //
            // World frame through revision 9. The rays are body-frame and the
            // actions are body-frame (drive along up, steer about z), so a world-frame
            // velocity was the one input the network had to rotate through the facing
            // before it meant anything about its own controls — "am I sliding
            // sideways" was a function of four inputs rather than one.
            // This player's own top speed, not his role's: a fast receiver at
            // full stride reads 1, as a slow one does. Which of the two he is is
            // in the traits slice at the end of the vector.
            float topSpeed = Systems_RoleTable.TopSpeedOf(role, traits);
            float sideways = (velocity.x * cos) + (velocity.y * sin);
            float forward = (-velocity.x * sin) + (velocity.y * cos);
            buffer[cursor++] = Mathf.Clamp(sideways / topSpeed, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(forward / topSpeed, -1f, 1f);

            // Facing: 2
            buffer[cursor++] = cos;
            buffer[cursor++] = sin;

            // Fatigue and possession: 2
            buffer[cursor++] = Mathf.Clamp01(fatigue);
            buffer[cursor++] = isCarrier ? 1f : 0f;

            // Ball relative to self, and how fast it is travelling, IN THE BODY'S
            // OWN FRAME — sideways, then forward — like the velocity above: 4
            //
            // World frame through revision 11, which was the defect revision 10
            // fixed for own velocity and left standing here. "The ball is ahead
            // and to my right" is the thing a drive-and-steer body acts on, and it
            // was a function of these four floats AND the facing two above.
            Vector2 toBall = ballPosition - position;
            buffer[cursor++] = NormalizeRange((toBall.x * cos) + (toBall.y * sin));
            buffer[cursor++] = NormalizeRange((-toBall.x * sin) + (toBall.y * cos));
            buffer[cursor++] = Mathf.Clamp(
                ((ballVelocity.x * cos) + (ballVelocity.y * sin))
                    / Systems_SimConstants.PASS_SPEED_MAX,
                -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(
                ((-ballVelocity.x * sin) + (ballVelocity.y * cos))
                    / Systems_SimConstants.PASS_SPEED_MAX,
                -1f, 1f);

            // Ball state: 2. Defenders need to see a live ball in the air.
            buffer[cursor++] = ballState == Systems_BallState.Held ? 1f : 0f;
            buffer[cursor++] = ballState == Systems_BallState.InFlight ? 1f : 0f;

            // Field geometry: 2. Distance to the goal line and to the line of
            // scrimmage.
            //
            // The two sideline distances that sat between them through revision 11
            // are gone: with NormalizeX above they were the same coordinate three
            // times. Their two floats went to the pair below, so OBSERVATION_SIZE
            // did not move — which means nothing but CONTRACT_REVISION refuses a
            // revision 11 brain here.
            buffer[cursor++] =
                Mathf.Clamp(field.YardsToAttackingGoal(position.y) / 100f, -1f, 1f);
            buffer[cursor++] = NormalizeRange(lineOfScrimmageY - position.y);

            // Own spin, against this role's top turn rate: 1.
            //
            // Steer is a torque against ANGULAR_DAMPING, so the turn a body is
            // already in outlasts the decision that started it by a step or two,
            // and observations are not stacked. Without this the policy steered a
            // second-order system while seeing only its position.
            buffer[cursor++] = Mathf.Clamp(
                (angularVelocityDegrees * Mathf.Deg2Rad) / Systems_RoleTable.TurnRateOf(role, traits),
                -1f, 1f);

            // The play clock: 1. Ticks since the snap over MAX_PHYSICS_TICKS.
            //
            // The call latches at DROPBACK_TICKS, the throw is refused after
            // THROW_WINDOW_TICKS and the play is whistled dead at the cap, and
            // none of the three was visible — so a critic could not tell the first
            // second of a play from the last, on a quarter of plays that ended on
            // the cap.
            buffer[cursor++] = Mathf.Clamp01(
                physicsTick / (float)Systems_PlayModel.MAX_PHYSICS_TICKS);

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

            // Play call one-hot: 7, offense only.
            bool isOffense = Systems_RoleTable.SideOf(role) == Systems_TeamSide.Offense;

            for (int callIndex = 0; callIndex < PLAY_CALL_SLOTS; callIndex++)
            {
                bool active = isOffense && (int)call == callIndex + 1;
                buffer[cursor++] = active ? 1f : 0f;
            }

            // This athlete: 4. Already in [-1, 1]; zero is the role's average.
            // Last, so every index before it is where revision 13 had it.
            buffer[cursor++] = Mathf.Clamp(traits.Speed, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(traits.Strength, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(traits.Agility, -1f, 1f);
            buffer[cursor++] = Mathf.Clamp(traits.Discipline, -1f, 1f);
        }

        /// <summary>Fills the buffer and pushes it into the sensor in one call.</summary>
        public static void Collect(
            VectorSensor sensor,
            float[] buffer,
            Systems_FieldModel field,
            Systems_PlayerRole role,
            Systems_PlayerTraits traits,
            Vector2 position,
            Vector2 velocity,
            float rotationDegrees,
            float angularVelocityDegrees,
            float fatigue,
            bool isCarrier,
            Vector2 ballPosition,
            Vector2 ballVelocity,
            Systems_BallState ballState,
            Systems_PlayCall call,
            float lineOfScrimmageY,
            int down,
            float yardsToGo,
            int physicsTick)
        {
            Write(
                buffer, field, role, traits, position, velocity, rotationDegrees,
                angularVelocityDegrees, fatigue,
                isCarrier, ballPosition, ballVelocity, ballState, call, lineOfScrimmageY,
                down, yardsToGo, physicsTick);

            sensor.AddObservation(buffer);
        }

        private static float NormalizeRange(float metres)
        {
            return Mathf.Clamp(
                metres / Systems_SimConstants.OBSERVATION_RANGE, -1f, 1f);
        }
    }
}
