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
        ///
        /// Revision 2 keeps every shape identical and moves WHEN the play call is
        /// read: it latches after Systems_SimConstants.DROPBACK_TICKS rather than on
        /// the first decision step after the snap. Shapes alone would let a revision
        /// 1 brain load here without complaint, and it would then be committing to a
        /// call eight decision steps earlier than it was fitted to — a silent
        /// behavioural mismatch of exactly the kind this stamp exists to catch. Runs
        /// up to and including football_base06 are revision 1.
        ///
        /// Revision 3 also keeps every shape identical and changes the DYNAMICS: the
        /// players were rebuilt for realistic acceleration. Systems_SimConstants
        /// .LINEAR_DAMPING fell 1.5 -> 0.8, which nearly doubles the time constant on
        /// every body in the game, and Systems_RoleTable trimmed both the top speeds
        /// and the turn rates.
        ///
        /// That is a contract change for two separate reasons, and either alone would
        /// justify the bump. A policy is a function fitted against a specific set of
        /// dynamics, and these are not those dynamics — CLAUDE.md section 2 is
        /// explicit that Systems_SimConstants is frozen for the life of a policy.
        /// Worse, TopSpeedOf is the divisor Sensor_FootballState uses to normalize
        /// the velocity observation, so a brain fitted at the old speeds would read
        /// every velocity on a different scale than it was trained on while the
        /// vector remained exactly 32 floats wide — invisible to any shape check.
        ///
        /// Nothing was invalidated in practice: no brain is promoted, which is
        /// precisely why this was the moment to fix the physics.
        ///
        /// Revision 4 is revision 3 plus the tackle threshold that revision 3 should
        /// have carried. TACKLE_CLOSING_SPEED is a speed, and revision 3 changed
        /// what speeds are reachable without rescaling it — so run football_base07
        /// measured a tackle rate less than half of the two runs before it and
        /// nearly three quarters of plays timing out. See the constant for the
        /// numbers. Checkpoints exist on disk under results/football_base07 fitted
        /// against that, and they must not load here, which is the whole job of this
        /// stamp.
        /// </summary>
        public const int CONTRACT_REVISION = 4;

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
