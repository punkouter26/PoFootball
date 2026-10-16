using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Sensors
{
    /// <summary>
    /// What the defensive coordinator sees before it calls a front (revision 16):
    /// the situation and the offense's alignment, the two things a real coordinator
    /// calls a front from. Nothing about any one player — the coordinator has no
    /// body and makes one decision a play.
    ///
    /// Every value is in [-1, 1], for the reason Sensor_FootballState's are: the
    /// trainer config runs with normalize: false.
    /// </summary>
    public static class Sensor_DefenseCallState
    {
        /// <summary>
        /// Width of the offense-formation one-hot. A literal because
        /// Tools/promote_brain.py reads it; Systems_ContractTests pins it to
        /// Systems_FormationBook.OffensiveFormationCount.
        /// </summary>
        public const int OFFENSE_FORMATION_SLOTS = 8;

        /// <summary>
        /// Down, distance, yards to the goal, a two-point flag, and the offense's
        /// formation. Must equal the coordinator's VectorObservationSize; read by
        /// Tools/promote_brain.py.
        /// </summary>
        public const int OBSERVATION_SIZE = 4 + OFFENSE_FORMATION_SLOTS;

        /// <summary>The same saturation Sensor_FootballState uses for yards to go.</summary>
        private const float LONG_YARDAGE_YARDS = 15f;

        public static void Write(
            float[] buffer,
            int down,
            float yardsToGo,
            float yardsToGoal,
            bool isTwoPointTry,
            int offenseFormationIndex)
        {
            int cursor = 0;

            buffer[cursor++] =
                Mathf.Clamp01((down - 1f) / (Systems_GameRules.DOWNS_PER_SERIES - 1f));
            buffer[cursor++] = Mathf.Clamp01(yardsToGo / LONG_YARDAGE_YARDS);
            buffer[cursor++] = Mathf.Clamp01(yardsToGoal / 100f);
            buffer[cursor++] = isTwoPointTry ? 1f : 0f;

            for (int formation = 0; formation < OFFENSE_FORMATION_SLOTS; formation++)
            {
                buffer[cursor++] = formation == offenseFormationIndex ? 1f : 0f;
            }
        }
    }
}
