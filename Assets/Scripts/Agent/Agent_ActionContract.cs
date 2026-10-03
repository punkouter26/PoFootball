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
        ///
        /// Revision 9 changes what the quarterback's aim DOES, and like 2, 5 and 8
        /// it leaves every shape identical — 36 observations, 4 continuous,
        /// branches [7, 2] — so nothing about an observation or an action vector
        /// would catch it and only this number can.
        ///
        /// Slots 2 and 3 still mean "which of my receivers": ResolveThrowTarget is
        /// unchanged and still picks the eligible receiver whose lead point sits
        /// closest to the aim ray. What changed is that the BALL no longer flies at
        /// that receiver's exact lead point. Systems_BallSystem.Throw now blends the
        /// led direction back toward the raw aim by PASS_AIM_SLACK, so aim PRECISION
        /// affects where the pass ends up, not merely which man it was meant for.
        ///
        /// It exists because a pass could not fail. Across all 52 summary windows of
        /// football_long01 — 2,600,000 steps — Pass/CompletionPerAttempt was exactly
        /// 1.0000 and Play/InterceptionRate exactly 0.0000, at every stage of
        /// training. The ball arrived at the receiver's exact future position, so
        /// the receiver was ~0 m from it while a defender at COVERAGE_CUSHION was
        /// ~1.0 m, and FindCatcher gives the ball to the closest body inside
        /// CATCH_RADIUS. INCOMPLETION_PENALTY, INTERCEPTION_REWARD and the whole
        /// Interception branch of Reward_Terminal were unreachable code — the same
        /// class of defect revision 8 was created to fix for the kicking game.
        ///
        /// A revision 8 brain would load here without complaint and would be
        /// steering an aim whose precision it was never fitted to care about, which
        /// is exactly the silent behavioural mismatch this stamp exists for.
        /// results/football_long01 is fitted against revision 8 and must not load.
        ///
        /// Revision 10 changes the SHAPE of the ray observation and the MEANING of
        /// two vector observations, and masks the quarterback's discrete branches
        /// differently. No run was ever made against revision 9, so it costs
        /// nothing to batch them:
        ///
        ///     Rays          6 per side over 90 degrees -> 10 per side over 165
        ///                   (52 -> 84 floats); now fixed in Sensor_RayContract
        ///                   instead of the scenes, so the gate reads code
        ///     Velocity      world frame -> the body's own frame (sideways, forward)
        ///     Call branch   pinned to None on every decision that cannot latch,
        ///                   and None masked on the ones that can
        ///     Throw branch  release masked whenever HandleQuarterback would refuse
        ///
        /// The reward changed in the same commit — flight yardage paid on the catch,
        /// not in the air; a block no longer outpays the time cost; no pursuit while
        /// the ball is in the air — but rewards are not part of the contract: they
        /// change what a run learns, not what a trained brain expects to be fed.
        ///
        /// Config/FootballBase12.yaml is the config for revision 10, and
        /// Assets/Agents/Football_v01 (football_base12) was promoted against it.
        ///
        /// Revision 11 changes the RULES of tackling, with every shape identical:
        /// a defender within Systems_SimConstants.REACH_TACKLE_RANGE of the carrier,
        /// beside or behind him, now counts as contact for the wrap-up
        /// (Systems_Referee.CountPursuitReach) whether or not the two bodies are
        /// touching that tick. Football_v01 learned that a receiver caught in stride
        /// could almost never be brought down from behind — 97% completions, 18.8
        /// yards a play — and would be steering a game whose tackling it was never
        /// fitted to. So it must not load here, and every player runs Heuristic
        /// until a revision 11 run is promoted.
        ///
        /// Revision 12 changes the MEANING of eight vector observations and the
        /// DYNAMICS of fatigue, with every shape identical — 36 floats, the same
        /// action specs — so, like 2, 5, 8, 9 and 11, only this number can refuse a
        /// stale brain:
        ///
        ///     Ball offset, velocity   world frame -> the body's own frame, the
        ///                             change revision 10 made to own velocity
        ///     Sideline distances      removed; x was already there (NormalizeX)
        ///     Own spin                new, in the first of those two slots
        ///     Play clock              new, in the second
        ///     Fatigue                 rested between plays, not cleared
        ///                             (FATIGUE_CARRIED_BETWEEN_PLAYS); gain
        ///                             0.030 -> 0.050; contact impulse is a third
        ///                             load term
        ///
        /// A revision 11 brain would read the ball in the wrong frame and two
        /// sideline distances out of the spin and clock slots, without one shape
        /// check noticing. results/football_base13 is fitted against revision 11
        /// and must not be promoted from a revision 12 working tree: check out the
        /// commit it was trained at first, or Build Brain Table will stamp it 12.
        ///
        /// The reward moved alongside, and as ever is not part of the contract: a
        /// play that runs out the clock is priced as a tackle, reaching the line to
        /// gain pays FIRST_DOWN_REWARD, and COMPLETION_REWARD fell 0.6 -> 0.45.
        /// Config/FootballBase14.yaml (now archived) was the config for revision 12.
        ///
        /// Revision 13 makes EXECUTION fallible, with every shape identical. Through
        /// revision 12 being in position was the whole of every skill: a ball inside
        /// CATCH_RADIUS was caught, a defender inside PASS_BREAKUP_RADIUS broke it
        /// up, a hit over TACKLE_CLOSING_SPEED was a tackle, a wrap-up held for its
        /// tick count was a tackle, and the only throw error in the game was the
        /// scripted quarterback's, most of which PASS_AIM_SLACK discarded.
        /// football_base14 completed 91-97% of its passes from start to finish.
        /// Systems_ISkillModel now draws, from its own seeded stream and identically
        /// in training and in a played game:
        ///
        ///     Throw           rotated off its line by a triangular error that
        ///                     grows with distance and fatigue, for EVERY passer
        ///     Open catch      can be dropped; likelier on a long throw
        ///     Contested catch broken up with a chance that rises as the defender
        ///                     closes, 0.45 at the edge of his reach to 0.95
        ///     Interception    held 60% of the time, batted down otherwise
        ///     Hit             can miss; the contact still counts to the wrap-up
        ///     Wrap-up         can be broken, restarting the count; likelier for a
        ///                     power back, against one man, and when it was all
        ///                     reach and no body (an arm tackle)
        ///
        /// A revision 12 brain would be steering a game where its throws land
        /// somewhere else and its tackles do not always finish, with no shape check
        /// noticing. Assets/Agents/Football_v02 (football_base14) is fitted against
        /// revision 12 and must not load here; every player runs Heuristic until a
        /// revision 13 run is promoted. Config/FootballBase15.yaml is its config.
        /// </summary>
        public const int CONTRACT_REVISION = 13;

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
