namespace PoFootball.Models
{
    /// <summary>
    /// Every tuned constant in the simulation, in one place. Values come from the
    /// Constants section of docs/BRIEF_SinglePlay.md.
    ///
    /// These are applied to Rigidbody2D from code at Awake rather than serialized
    /// in the scene, so a scene edit cannot silently change the dynamics that an
    /// existing .onnx was fitted against (CLAUDE.md section 2).
    ///
    /// Anything that must satisfy a relationship with another constant is DERIVED
    /// from it in Systems_RoleTable rather than tuned independently — see
    /// DriveForceOf and SteerTorqueOf. Two constants that have to agree and are
    /// typed in separately will eventually stop agreeing, silently.
    /// </summary>
    public static class Systems_SimConstants
    {
        // --- Tackle rule -----------------------------------------------------
        /// <summary>Closing speed (m/s) at which a defender drops the carrier on impact.</summary>
        public const float TACKLE_CLOSING_SPEED = 1.5f;

        /// <summary>
        /// Consecutive physics ticks of contact that bring the carrier down
        /// regardless of closing speed — 10 ticks = 0.2 s, a wrap-up tackle.
        ///
        /// Without this, pursuit tackles are impossible. Two bodies travelling the
        /// same direction at similar speed have a relative velocity near zero, so
        /// a defender running the carrier down from behind at 8.5 m/s against a
        /// 9 m/s carrier reads 0.5 m/s and never clears TACKLE_CLOSING_SPEED,
        /// however long it stays in contact. Run football_base01 learned to exploit
        /// exactly that: the offense averaged tens of yards per play because only
        /// head-on hits could stop it.
        /// </summary>
        public const int SUSTAINED_TACKLE_TICKS = 10;

        // --- Body dynamics ---------------------------------------------------
        /// <summary>
        /// Translational damping, shared by every role. This is the stand-in for
        /// ground friction in a top-down sim: with a constant drive force it is
        /// what sets terminal velocity, at v = F / (m * d).
        ///
        /// Because that relationship is exact, per-role drive force is DERIVED
        /// from the role's top speed rather than tuned (Systems_RoleTable
        /// .DriveForceOf). Before that derivation existed, every role shared
        /// DRIVE_FORCE = 900 N at mass 100 kg, which pins terminal velocity at
        /// 900 / (100 * 1.5) = 6.0 m/s for EVERY role — so the 9.0 / 7.5 / 6.5 m/s
        /// top speeds in Systems_RoleTable were unreachable, a receiver and a
        /// guard were dynamically identical, and the MAX_BODY_SPEED guard could
        /// never bind. TopSpeedOf was live only in observation normalization.
        /// </summary>
        public const float LINEAR_DAMPING = 1.5f;

        public const float ANGULAR_DAMPING = 6f;

        /// <summary>Collider radius. Also the lever arm in the moment of inertia.</summary>
        public const float PLAYER_RADIUS = 0.5f;

        /// <summary>
        /// Hard velocity ceiling — the pileup-explosion guard (criterion #13).
        /// Set above the fastest role's 9.0 m/s so ordinary running never touches
        /// it and a collision may legitimately overshoot; it exists to catch the
        /// solver blowing a stack of bodies apart, not to cap sprinting.
        /// </summary>
        public const float MAX_BODY_SPEED = 12f;

        // --- Fatigue ---------------------------------------------------------
        /// <summary>
        /// Load is the sum of applied force and applied torque, each divided by
        /// this role's own maximum, so it is dimensionless and lands in [0, 2].
        ///
        /// It is still read from what was APPLIED, never from the action vector —
        /// a player braced against a block is a near-zero action at near-maximum
        /// force (CLAUDE.md section 2). Normalizing per role is what makes the
        /// number comparable between a 140 kg guard and a 95 kg receiver, and it
        /// fixes a dimensional bug in the previous form, which summed newtons and
        /// newton-metres as though they were the same quantity.
        ///
        /// Fatigue is also self-limiting: the applied force is already scaled by
        /// (1 - fatigue * FATIGUE_MAX_PENALTY), so a tiring player generates less
        /// load and the curve flattens instead of running away.
        /// </summary>
        public const float FATIGUE_GAIN_PER_UNIT_LOAD = 0.030f;

        /// <summary>
        /// Recovery must stay well below the gain at realistic load or fatigue is
        /// dead on arrival. The previous pair could not accumulate at all: peak
        /// gain was (900 N + 300 N.m) * 1.2e-5 = 0.0144/s against 0.06/s of
        /// recovery, so _fatigue was pinned at zero for the entire simulation —
        /// the observation slot was a constant, FATIGUE_MAX_PENALTY never applied,
        /// and the fatigue shader parameter never moved.
        ///
        /// At these values a hard cut (load ~2.0) nets 0.045/s and reaches ~0.67
        /// over a full 15 s play, while cruising (load ~1.0) nets 0.015/s and
        /// barely registers. Fatigue is cleared every episode, so only within-play
        /// exertion matters.
        /// </summary>
        public const float FATIGUE_RECOVERY_PER_SECOND = 0.015f;

        /// <summary>Drive force is scaled by (1 - fatigue * this) so it never reaches zero.</summary>
        public const float FATIGUE_MAX_PENALTY = 0.45f;

        // --- Rewards ---------------------------------------------------------
        /// <summary>
        /// Dense per-decision reward per yard of forward progress.
        ///
        /// WAS 0.05, AND THAT INVERTED THE WHOLE REWARD. At 0.05 a hundred-yard
        /// drive paid 5.0 against a TOUCHDOWN_REWARD of 1.0, so yardage was worth
        /// five times the score it produced and the optimal policy was to maximise
        /// yards and ignore the end zone. Run football_base05 did exactly that: by
        /// 7.5M steps the offense's mean reward was +0.702 and its yardage term
        /// alone was +0.675 — the terminal rewards had become rounding error.
        ///
        /// The quarterback then collapsed onto the single safest high-yardage play
        /// (Call/HandoffHalfback 0.99, Call/Pass 0.000, Call/Entropy 0.10 of a
        /// possible 1.609), the defense spent four million steps learning to stop
        /// that one play, and the offense had nothing left to switch to:
        /// TouchdownRate fell 0.82 -> 0.08 and NetYards 52 -> 13.5.
        ///
        /// At 0.01 a full hundred yards is worth exactly one touchdown, so scoring
        /// can never be dominated by the ground covered getting there. Reward_Role
        /// already states the principle this restores: a shaped term that outgrows
        /// the terminal reward gets farmed while the game gets ignored.
        /// </summary>
        public const float YARD_REWARD_SCALE = 0.01f;

        /// <summary>Per-decision cost, applied to the offense so standing still loses.</summary>
        public const float TIME_COST_PER_DECISION = -0.001f;

        public const float TOUCHDOWN_REWARD = 1.0f;
        public const float TACKLE_REWARD = 0.5f;

        /// <summary>Extra defensive reward for a stop behind the line of scrimmage.</summary>
        public const float TACKLE_FOR_LOSS_BONUS = 0.25f;

        // --- Passing and handoffs (milestone 2) ------------------------------
        /// <summary>How close the quarterback must be to a back to hand the ball over.</summary>
        public const float HANDOFF_RADIUS = 2.0f;

        /// <summary>Ball speed at minimum throw power (m/s).</summary>
        public const float PASS_SPEED_MIN = 12f;

        /// <summary>Ball speed at maximum throw power (m/s).</summary>
        public const float PASS_SPEED_MAX = 25f;

        /// <summary>A player this close to a live ball catches it.</summary>
        public const float CATCH_RADIUS = 1.2f;

        /// <summary>
        /// How far a pass must travel from the release point before anyone may
        /// catch it.
        ///
        /// Without this the throw resolves on its first tick into whichever
        /// offensive lineman happens to be standing beside the quarterback — a
        /// "completion" that is really a handoff, and one that can never fall
        /// incomplete or be intercepted. Run football_base03 showed exactly that:
        /// IncompletionRate 0.006 and InterceptionRate 0.000 after 240k steps.
        /// A pass now has to clear the pocket to be live.
        /// </summary>
        public const float MIN_CATCH_DISTANCE = 3.0f;

        /// <summary>
        /// Flight ticks before a pass is ruled incomplete. 100 ticks = 2 s, which
        /// at PASS_SPEED_MAX is a 50 m throw — beyond any realistic attempt.
        /// </summary>
        public const int MAX_FLIGHT_TICKS = 100;

        /// <summary>
        /// Ticks after the snap during which the quarterback may still throw. After
        /// this it must run, which stops it from circling forever behind the line.
        /// </summary>
        public const int THROW_WINDOW_TICKS = 250;

        // --- Passing rewards -------------------------------------------------
        /// <summary>
        /// Paid to the offense at the whistle when a pass was caught on this play.
        ///
        /// This constant existed before but was read by NOTHING — Reward_Terminal
        /// had no Completion branch, so a completed pass paid the offense exactly
        /// its yardage and no more. Combined with an incompletion penalty LARGER
        /// than this reward, the expected value of throwing was negative before
        /// aim could possibly have been learned, and run football_base03 settled
        /// on Call/Pass = 0.000 and stayed there for 2.4M steps.
        ///
        /// A completion is now worth appreciably more than a tackle costs, and an
        /// incompletion costs appreciably less, so an early clumsy passing game is
        /// roughly break-even and improves from there.
        /// </summary>
        /// <remarks>
        /// Raised 0.5 -> 0.6 alongside dropping INTERCEPTION_REWARD, because the
        /// arithmetic still did not close. At 0.5 completion / 0.1 incompletion /
        /// 1.0 interception, a pass needed a 39% completion rate merely to break
        /// even, and football_base05 threw at 27-44% — so throwing was EV-negative
        /// against a handoff that reliably paid its yardage, and Call/Pass went to
        /// 0.000 for the second run running. Break-even is now 25%.
        /// </remarks>
        public const float COMPLETION_REWARD = 0.6f;

        /// <summary>
        /// An incompletion wastes the down and nothing more, so it must not cost
        /// the offense more than being tackled for no gain does.
        /// </summary>
        public const float INCOMPLETION_PENALTY = 0.1f;

        /// <summary>
        /// An interception is the largest single swing available to the defense.
        ///
        /// Lowered 1.0 -> 0.6 as a direct consequence of YARD_REWARD_SCALE falling
        /// five-fold. This existed to outweigh "the dense yardage a long throw
        /// earns on its way to being picked off" — but that yardage is now a fifth
        /// of what it was, so a forty-yard heave earns 0.4 rather than 2.0 and 0.6
        /// still comfortably outweighs it. Left at 1.0 it would simply have been
        /// the new reason never to throw.
        /// </summary>
        public const float INTERCEPTION_REWARD = 0.6f;

        // --- Play-call diversity ---------------------------------------------
        /// <summary>
        /// Plays of play-call history the quarterback keeps for the repetition
        /// penalty. Long enough that a genuine preference is not punished as a
        /// collapse, short enough that a collapse is felt within a summary window.
        /// </summary>
        public const int CALL_HISTORY_PLAYS = 40;

        /// <summary>
        /// How hard an over-used play call is penalised, per unit of share above
        /// what an even split would give it.
        ///
        /// THIS IS AN ENTROPY FLOOR, ENFORCED THROUGH THE REWARD RATHER THAN THE
        /// TRAINER. PPO's entropy bonus (`beta`) is the usual lever and it was not
        /// enough: the quarterback ran at beta 2.0e-2, four times every other
        /// brain, and still collapsed to one call on 99% of downs because the
        /// advantage gap between that call and the rest was enormous. Raising beta
        /// far enough to beat that gap would have injected matching noise into the
        /// aim vector, which shares the distribution.
        ///
        /// A reward term is on-policy and precise: it prices exactly the thing that
        /// is wrong — over-repetition — and nothing else. At the observed 0.99
        /// share it costs 0.74 * this, which is real money against a play worth
        /// about 0.14 of yardage under the corrected YARD_REWARD_SCALE, while an
        /// even 25% split costs exactly zero.
        ///
        /// It is deliberately not large enough to make a genuinely dominant call
        /// unattractive at a sane share — a quarterback running 40% halfback is
        /// charged 0.15 * this, which good play easily covers.
        /// </summary>
        public const float CALL_REPETITION_PENALTY = 0.5f;

        // --- Role-shaped rewards (per-agent credit assignment) ---------------
        /// <summary>
        /// Per-tick reward an offensive lineman earns for keeping itself between
        /// the ball and the nearest pass rusher.
        ///
        /// The terminal reward is identical for all eleven offensive players, so
        /// without a term like this a pulling guard's block and a receiver
        /// standing still are indistinguishable to the optimizer — the credit for
        /// a touchdown is smeared evenly over everyone on the field. These terms
        /// are small relative to the terminal rewards on purpose: they shape which
        /// behaviour is explored, they do not decide who wins the play.
        /// </summary>
        public const float BLOCK_REWARD_PER_TICK = 0.00025f;

        /// <summary>Metres inside which a lineman counts as engaged with a rusher.</summary>
        public const float BLOCK_ENGAGE_RANGE = 2.5f;

        /// <summary>
        /// Per-tick reward a receiver earns for separation from the nearest
        /// defender while the ball is live and it is not the carrier.
        /// </summary>
        public const float SEPARATION_REWARD_PER_TICK = 0.00015f;

        /// <summary>Separation is measured against this distance and saturates there.</summary>
        public const float SEPARATION_SATURATION_RANGE = 6.0f;

        /// <summary>
        /// Per-tick reward a defender earns for closing on the ball. Signed: drifting
        /// away from the ball is a small negative, which is what stops the defense
        /// from learning to stand in its assigned spot and wait.
        /// </summary>
        public const float PURSUIT_REWARD_PER_METRE = 0.0040f;

        // --- Spawning --------------------------------------------------------
        /// <summary>Minimum centre-to-centre separation at spawn (criterion #12).</summary>
        public const float MIN_SPAWN_SEPARATION = 1.2f;

        // --- Observations ----------------------------------------------------
        /// <summary>Metres used to normalize relative position observations to [-1, 1].</summary>
        public const float OBSERVATION_RANGE = 40f;

        /// <summary>Agent decisions per second is 50 / this (criterion #15).</summary>
        public const int DECISION_PERIOD = 5;
    }
}
