using PoFootball.Models;
using PoFootball.Sensors;
using Unity.MLAgents.Actuators;

namespace PoFootball.Agents
{
    /// <summary>
    /// The single place the action space of every brain is defined.
    ///
    /// This is a separate type purely so the contract has a seam a test can reach.
    /// It used to be an expression inside Agent_FootballPlayer.ConfigureBrain,
    /// which meant the only way to find out what shape a policy expected was to
    /// instantiate a MonoBehaviour and read it back off BehaviorParameters — so in
    /// practice nothing checked, and an .onnx built against a stale action space
    /// loaded without complaint and returned zeros forever.
    ///
    /// Tools/promote_brain.py validates promoted .onnx files against exactly these
    /// numbers before overwriting anything in Assets/Agents/.
    /// </summary>
    public static class Agent_ActionContract
    {
        /// <summary>
        /// Bumped whenever the observation or action shape changes — i.e. whenever
        /// every existing `.onnx` stops being valid.
        ///
        /// <see cref="Agent_BrainTable"/> carries the revision its brains were
        /// fitted against and is ignored wholesale if it does not match this, so a
        /// stale brain is never loaded. That is the mechanism that was missing when
        /// Football_v01 (a football_base02 export, 25 observations, no discrete
        /// head) stayed bound in both scenes long after the contract moved to 32
        /// observations and a separate quarterback action space.
        ///
        /// Revision 1 is the first stamped contract: 32 vector observations, 2
        /// continuous actions, and 4 continuous + 2 discrete branches for the
        /// quarterback. No promoted brain matches it yet.
        /// </summary>
        public const int CONTRACT_REVISION = 1;

        /// <summary>Continuous outputs every brain has: drive and steer.</summary>
        public const int BASE_CONTINUOUS_ACTIONS = 2;

        /// <summary>
        /// The quarterback's two extra continuous outputs: the aim vector. They sit
        /// after drive and steer, so index 2 and 3.
        /// </summary>
        public const int QUARTERBACK_CONTINUOUS_ACTIONS = 4;

        /// <summary>Size of the quarterback's throw-trigger branch: hold or release.</summary>
        public const int THROW_BRANCH_SIZE = 2;

        public static ActionSpec For(Systems_BrainGroup group)
        {
            if (!Systems_RoleTable.HasQuarterbackActions(group))
            {
                return ActionSpec.MakeContinuous(BASE_CONTINUOUS_ACTIONS);
            }

            return new ActionSpec(
                QUARTERBACK_CONTINUOUS_ACTIONS,
                new[] { Sensor_FootballState.PLAY_CALL_BRANCH_SIZE, THROW_BRANCH_SIZE });
        }
    }
}
