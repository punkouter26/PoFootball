using System.Collections.Generic;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoFootball.Sensors
{
    /// <summary>
    /// The geometry of every player's RayPerceptionSensor2D: the half of the
    /// observation Sensor_FootballState does not write.
    ///
    /// WHY THIS IS CODE AND NOT THE SCENE. The rays used to be authored on 22
    /// components in each of two scenes, and Tools/promote_brain.py read their width
    /// back out of SCN_TRAIN_FOOTBALL's YAML. That made a component tweak in the
    /// Inspector a silent contract change — the class of bug the whole promotion
    /// gate exists for — and left SCN_GAME free to disagree with the scene the
    /// brains were trained in. Agent_FootballPlayer now applies this in Awake,
    /// before ML-Agents builds the sensor, exactly as it already does for the
    /// BehaviorParameters, so nothing in a scene file can contradict it; the gate
    /// reads these constants.
    ///
    /// REVISION 10 TURNED THE RAYS AROUND. Through revision 9 they covered the
    /// forward 180 degrees only (6 per side, 90 degrees), so a ball carrier could
    /// not see the pursuit closing from behind — which is the only way a wrap-up
    /// tackle from behind ever happens here — and a defender could not see a
    /// receiver running past him. 10 per side to 165 degrees keeps the spacing at
    /// about the old 15 (16.5) all the way round and leaves a 30 degree gap
    /// directly behind, rather than doubling the 180 degree ray onto itself.
    /// </summary>
    public static class Sensor_RayContract
    {
        public const int RAYS_PER_DIRECTION = 10;

        public const float MAX_RAY_DEGREES = 165f;

        /// <summary>Metres. Unchanged from the scene value every run used.</summary>
        public const float RAY_LENGTH = 20f;

        /// <summary>One player radius, so a ray cannot slip between two bodies.</summary>
        public const float SPHERE_CAST_RADIUS = 0.5f;

        public const string OFFENSE_TAG = "Offense";

        public const string DEFENSE_TAG = "Defense";

        /// <summary>Offense and Defense. Read by Tools/promote_brain.py.</summary>
        public const int DETECTABLE_TAG_COUNT = 2;

        /// <summary>
        /// Floats the ray sensor contributes: one per tag, plus ML-Agents' own
        /// hit-nothing flag and hit fraction, for each of 2N + 1 rays.
        /// </summary>
        public const int RAY_OBSERVATION_SIZE =
            ((2 * RAYS_PER_DIRECTION) + 1) * (DETECTABLE_TAG_COUNT + 2);

        /// <summary>The 22 players' bodies.</summary>
        public const string PLAYER_LAYER = "Player";

        /// <summary>The four walls round the field.</summary>
        public const string BOUNDARY_LAYER = "Boundary";

        /// <summary>
        /// Must run before the agent's sensors are created — RaysPerDirection
        /// cannot change afterwards — which in practice means from Awake.
        ///
        /// The layer mask is the only thing that may fall back. Players and walls
        /// are the only colliders on the field, so Player | Boundary and the
        /// scenes' old everything-but-Ignore-Raycast mask see exactly the same
        /// hits; a project without the two layers keeps the old mask rather than
        /// casting against nothing. Systems_ContractTests asserts the layers exist.
        /// </summary>
        public static void Apply(RayPerceptionSensorComponent2D rays)
        {
            rays.RaysPerDirection = RAYS_PER_DIRECTION;
            rays.MaxRayDegrees = MAX_RAY_DEGREES;
            rays.RayLength = RAY_LENGTH;
            rays.SphereCastRadius = SPHERE_CAST_RADIUS;
            rays.ObservationStacks = 1;
            rays.DetectableTags = new List<string> { OFFENSE_TAG, DEFENSE_TAG };

            int mask = LayerMask.GetMask(PLAYER_LAYER, BOUNDARY_LAYER);

            if (mask != 0)
            {
                rays.RayLayerMask = mask;
            }
        }
    }
}
