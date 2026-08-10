namespace PoFootball.Models
{
    /// <summary>
    /// Every tuned constant in the simulation, in one place. Values come from the
    /// Constants section of docs/BRIEF_SinglePlay.md.
    ///
    /// These are applied to Rigidbody2D from code at Awake rather than serialized
    /// in the scene, so a scene edit cannot silently change the dynamics that an
    /// existing .onnx was fitted against (CLAUDE.md section 2).
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
        public const float PLAYER_MASS = 100f;
        public const float PLAYER_RADIUS = 0.5f;
        public const float LINEAR_DAMPING = 1.5f;
        public const float ANGULAR_DAMPING = 6f;

        /// <summary>Newtons applied at full forward action.</summary>
        public const float DRIVE_FORCE = 900f;

        /// <summary>Newton-metres applied at full steer action.</summary>
        public const float STEER_TORQUE = 300f;

        /// <summary>Hard velocity ceiling — the pileup-explosion guard (criterion #13).</summary>
        public const float MAX_BODY_SPEED = 15f;

        // --- Fatigue ---------------------------------------------------------
        /// <summary>
        /// Fatigue accumulates from APPLIED FORCE, never from the action vector —
        /// isometric bracing is a near-zero action at near-maximum force
        /// (CLAUDE.md section 2).
        /// </summary>
        public const float FATIGUE_GAIN_PER_NEWTON_SECOND = 1.2e-5f;
        public const float FATIGUE_RECOVERY_PER_SECOND = 0.06f;

        /// <summary>Drive force is scaled by (1 - fatigue * this) so it never reaches zero.</summary>
        public const float FATIGUE_MAX_PENALTY = 0.45f;

        // --- Rewards ---------------------------------------------------------
        /// <summary>Dense per-decision reward per yard of forward progress.</summary>
        public const float YARD_REWARD_SCALE = 0.05f;

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
        public const float COMPLETION_REWARD = 0.2f;
        public const float INCOMPLETION_PENALTY = 0.3f;

        /// <summary>An interception is the largest single swing available to the defense.</summary>
        public const float INTERCEPTION_REWARD = 1.0f;

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
