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
        ///
        /// Revision 5 changes what the quarterback's aim actions MEAN. Continuous
        /// slots 2 and 3 used to be the throw direction, taken literally: the ball
        /// left the hand along that vector whether or not anyone was standing on it.
        /// They are now a direction of INTENT, resolved by
        /// Systems_BallSystem.ResolveThrowDirection to whichever eligible receiver
        /// on the throwing side best matches that bearing, led for the flight time.
        /// Every shape is identical — 4 continuous, branches [5, 2] — so a revision
        /// 4 brain would load without a murmur and then be steering a control it was
        /// never fitted against. That is the same class of silent behavioural
        /// mismatch as revision 2's latch timing, and the same reason this number
        /// exists.
        ///
        /// It invalidates Assets/Agents/Football_v01 (football_base08). Those
        /// checkpoints were fitted against the free-aim throw and will not load
        /// here — Resources/PoFootballBrains.asset still stamps revision 4, so
        /// Agent_BrainTable.MatchesCurrentContract is false and every player falls
        /// back to Heuristic until a matching run is trained and promoted.
        ///
        /// Revision 6 changes the SHAPES, so unlike 2, 3, 4 and 5 it is caught by a
        /// shape check as well as by this stamp. The observation vector grew 32 -> 36
        /// (down and distance, for every player), and the quarterback's play-call
        /// branch grew 5 -> 7 with Punt and FieldGoal. Both are resolved at the rules
        /// layer rather than simulated, and both are masked out wherever they are
        /// illegal — see Agent_FootballPlayer.WriteDiscreteActionMask — so the
        /// quarterback learns WHEN to kick from the mask rather than from a penalty
        /// for proposing the absurd.
        ///
        /// Training gained downs in the same revision, which is what made a punt
        /// learnable at all: Systems_GameFlowSystem owns the chains and is a
        /// Game-mode registration, so until then the training environment had no
        /// down and no distance and a punt decision was not merely unlearned, it was
        /// unrepresentable. Systems_RandomSpotProvider now draws a seeded down and
        /// distance with every spot. A safety also became a distinct outcome rather
        /// than an ordinary tackle for loss. results/football_base09 is the only run
        /// against revision 6, and it did not finish — its workers died with a
        /// BrokenPipeError partway through.
        ///
        /// Revision 7 collapses the BRAIN GROUPS from six to three: Offense, Defense
        /// and Quarterback, in place of the OffenseLine / OffenseSkill / DefenseLine
        /// / DefenseBox / DefenseSecondary split. Every shape is unchanged, so
        /// nothing about an observation or an action vector would catch it — but a
        /// six-entry table maps group 3, 4 and 5 onto enum values that no longer
        /// exist, and maps 0, 1 and 2 onto entirely different populations than the
        /// names suggest. That is exactly the silent behavioural mismatch this stamp
        /// exists for, and Agent_BrainTable now checks the group SET as well as this
        /// number, because the number alone cannot see it.
        ///
        /// Revision 8 changes the DYNAMICS, the way 3 and 4 did, and like them it
        /// leaves every shape identical — so nothing about an observation or an
        /// action vector would catch it and only this number can.
        ///
        /// It exists because a measured full game was not football. It ended
        /// `FINAL 21-28 after 45 plays, 9 drives. Punts 0, FG 0/0, safeties 0,
        /// turnovers on downs 0` at 11.2 yards per play, with ONE fourth down in the
        /// entire game and 82% of drives ending in a touchdown. The whole kicking
        /// game — Agent_PlayCaller.ChooseFourthDown, Systems_Referee.KickOutcome and
        /// the Punt / FieldGoal / Safety branches of Systems_GameFlowSystem.Resolve —
        /// never executed once, because an offense that converts on half its snaps
        /// never reaches fourth down.
        ///
        /// What moved, all in Systems_SimConstants and Systems_RoleTable:
        ///
        ///     TACKLE_CLOSING_SPEED     0.8 -> 4.0   (no more one-touch tackles)
        ///     SUSTAINED_TACKLE_TICKS   10 -> 8      (now a BASE; see TackleTicksOf)
        ///     TackleTicksOf            new          (FB 2x, HB 1.75x, WR 0.75x)
        ///     COVERAGE_CUSHION         1.5 -> 1.0
        ///     LINEBACKER_DROP_YARDS    5 -> 3.5
        ///     SAFETY_DEPTH_YARDS       14 -> 11
        ///     ZONE_BALL_LEAN           0.35 -> 0.5
        ///     Safety top speed         8.7 -> 9.2   (linebacker deliberately not)
        ///
        /// Measured over three-game batches, that took the game from 11.2 yards a
        /// play, 1 fourth down and 0.82 touchdowns per drive to 6.2, 10.3 and 0.36 —
        /// against real-football reference values of about 5.5, one series in three,
        /// and 0.20-0.35. Punts, field goals, missed field goals, safeties and
        /// turnovers on downs all now occur in an ordinary game.
        ///
        /// Two further changes were part of the same work and are NOT dynamics: the
        /// ball carrier's evasion and the zone defenders' arrival damping both live
        /// in Agent_FootballPlayer's Heuristic path, which a trained policy never
        /// runs. They were the single largest effect of the lot — the carrier used to
        /// sidestep the nearest defender perfectly on every tick — and they cost the
        /// contract nothing, which is why they are recorded there rather than here.
        ///
        /// NOTHING IS PROMOTED AGAINST REVISION 8, and nothing was against 7 either.
        /// Every player runs Heuristic until a revision 8 run is trained against the
        /// three-behavior config and promoted with Tools/promote_brain.py. Any
        /// `.onnx` trained before this stamp is fitted against different dynamics and
        /// Agent_BrainTable will refuse it, which is the point.
        /// </summary>
        public const int CONTRACT_REVISION = 8;

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
