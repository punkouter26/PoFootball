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
        /// <summary>
        /// Closing speed (m/s) at which a defender drops the carrier on impact.
        ///
        /// SCALED WITH LINEAR_DAMPING, BECAUSE IT IS A SPEED THRESHOLD AND DAMPING
        /// SETS WHAT SPEEDS ARE REACHABLE. Dropping damping 1.5 -> 0.8 for realistic
        /// acceleration nearly doubled the time constant, so a body pushing for a
        /// fixed short window now reaches roughly 0.8/1.5 of the speed it used to —
        /// the achieved speed over a short push scales about linearly with d. The
        /// threshold was calibrated against the old regime, so leaving it at 1.5
        /// silently raised the bar for every hit in the game.
        ///
        /// It was not a small effect. Measured at step 20,000 of an untrained run,
        /// where the policy is near-random and the heuristic is not involved at all,
        /// so the dynamics are the only variable:
        ///
        ///     run           TackleRate   TimeExpiredRate   LengthTicks
        ///     base05          0.276          0.546            542
        ///     base06          0.216          0.605            544
        ///     base07 (d=0.8)  0.104          0.730            615
        ///
        /// Tackling more than halved and nearly three quarters of plays ran out the
        /// 750-tick cap instead of ending in football. 1.5 * (0.8 / 1.5) = 0.8
        /// restores the threshold to the same fraction of a reachable speed it
        /// always represented.
        ///
        /// RAISED FROM 0.8 TO 4.0 FOR CONTRACT REVISION 8, AND THAT IS A CHANGE OF
        /// MEANING, NOT A RECALIBRATION. At 0.8 m/s this path was a ONE-TOUCH
        /// TACKLE: essentially any defender who brushed the carrier while moving
        /// ended the down on that tick, so the sustained-contact rule below almost
        /// never got to run and no carrier ever fought through anything.
        ///
        /// This path now means what its name says — a genuine collision. Two bodies
        /// meeting head-on at 8-9 m/s close at something like 17 m/s, so a real hit
        /// still ends the play instantly; a defender running the carrier down from
        /// behind closes at well under 1 m/s and now has to WRAP HIM UP, which is
        /// the sustained rule below and is where per-role resistance lives.
        /// </summary>
        public const float TACKLE_CLOSING_SPEED = 4.0f;

        /// <summary>
        /// Consecutive physics ticks of contact that bring the carrier down
        /// regardless of closing speed — the BASE count, 8 ticks = 0.16 s. What a
        /// given carrier actually needs is Systems_RoleTable.TackleTicksOf(role),
        /// which scales this: a fullback takes more than twice as long to bring
        /// down as a receiver does.
        ///
        /// Without this, pursuit tackles are impossible. Two bodies travelling the
        /// same direction at similar speed have a relative velocity near zero, so
        /// a defender running the carrier down from behind at 8.5 m/s against a
        /// 9 m/s carrier reads 0.5 m/s and never clears TACKLE_CLOSING_SPEED,
        /// however long it stays in contact. Run football_base01 learned to exploit
        /// exactly that: the offense averaged tens of yards per play because only
        /// head-on hits could stop it.
        ///
        /// CUT FROM 10 TO 6 FOR CONTRACT REVISION 8, AND THIS IS THE KNOB THAT MAKES
        /// FOURTH DOWN EXIST. A measured full game returned `Punts 0, FG 0/0,
        /// safeties 0, turnovers on downs 0` — every kicking rule in the project,
        /// and the whole of Agent_PlayCaller.ChooseFourthDown, went unexecuted for
        /// an entire game. The cause was not the kicking code: the offense simply
        /// never reached fourth down. 17 of 45 plays gained a first down outright
        /// and 7 of 9 drives ended in a touchdown, against an NFL rate near 1 in 5.
        ///
        /// 0.2 s of contact is a long time at 9 m/s — nearly two metres of free
        /// running after a defender has already arrived — and it is what turned
        /// every stop into a four-yard gain and every set of downs into a formality.
        ///
        /// THE HISTORY IS WORTH KEEPING, BECAUSE TWO OF THESE MOVES WERE WRONG.
        /// 10 -> 6 helped: first downs fell from one every 2.6 plays to one every
        /// 3.3 and punts appeared for the first time. 6 -> 4, together with faster
        /// linebackers and safeties, made it WORSE — plays ending TimeExpired
        /// doubled — and the speeds were reverted (see Systems_RoleTable).
        ///
        /// The number then went UP to 8, on a different argument. Chasing a lower
        /// tick count was chasing the wrong thing: with TACKLE_CLOSING_SPEED at 0.8
        /// almost every tackle was being made on the instant-contact path anyway, so
        /// this constant was barely load-bearing. With that bar raised to a real
        /// collision, sustained contact is now the ordinary way a play ends, and it
        /// should take a beat and depend on WHO IS CARRYING — which is what
        /// Systems_RoleTable.TackleTicksOf adds.
        /// </summary>
        /// RAISED 8 -> 10 WHEN GANG TACKLING ARRIVED, and the two changes belong
        /// together. Systems_Referee now shortens the wrap-up when more than one
        /// defender is in contact, which is a large effective cut to this number on
        /// most tackles — measured, it took a game from 12 first downs to 4 and left
        /// drives going three-and-out all afternoon. Raising the base restores the
        /// room a carrier needs while keeping the property the gang rule was added
        /// for: help arriving is what ends the down.
        ///
        /// Walked up in two measured steps. 8 gave 4 first downs a game and 7.1
        /// yards a play; 10 gave 5 first downs and 4.44. Both were short of real
        /// football's roughly twenty first downs, and the second had overshot the
        /// yardage target in the other direction — the gains were arriving in a few
        /// long plays rather than the steady four-and-five-yard carries that
        /// actually move chains. 12 was then measured at ~9.8 yards a play across
        /// two games — no better than the 9.3 this work started from, i.e. it undid
        /// the tackling fix entirely. 10 is the setting that kept the structural
        /// gains (plays resolving instead of timing out, a sane drive count) and it
        /// is what ships.
        public const int SUSTAINED_TACKLE_TICKS = 10;

        /// <summary>
        /// How many physics ticks of BROKEN contact a wrap-up survives before the
        /// count restarts. 3 ticks = 0.06 s.
        ///
        /// The sustained-tackle rule used to require strictly consecutive ticks, and
        /// in a 2D sim of colliding discs that is a rule the physics itself breaks:
        /// the collision impulse pushes the tackler off the carrier, the next tick
        /// registers no contact, and a tackle that had genuinely been made reset to
        /// zero. The carrier then ran on. This is the separation the hit itself
        /// caused, not the carrier escaping, so it should not cost the defense the
        /// wrap it had already earned.
        ///
        /// Deliberately short. Long enough to bridge a bounce, far too short to
        /// bridge a defender being beaten and having to re-establish contact.
        /// </summary>
        public const int CONTACT_GRACE_TICKS = 2;

        // --- Fumbles ---------------------------------------------------------
        /// <summary>
        /// Closing speed at which a hit is hard enough to strip the ball, m/s.
        /// Sits above TACKLE_CLOSING_SPEED: every fumble is a real collision, but
        /// not every real collision is a fumble.
        /// </summary>
        public const float FUMBLE_CLOSING_SPEED = 6.0f;

        /// <summary>
        /// Defenders that must be in contact before the deterministic (training)
        /// fumble trigger fires. The second man is who rips the ball out, and
        /// requiring him is what makes the trigger something a carrier can avoid by
        /// not running into a crowd.
        /// </summary>
        public const int FUMBLE_MIN_TACKLERS = 2;

        /// <summary>
        /// Base chance a tackle produces a lost fumble in a played game, before the
        /// hit, the help and the carrier's ball security scale it.
        ///
        /// Real football loses roughly one fumble per team per game over about
        /// sixty-five snaps. 1.2% is that rate, left low deliberately: the
        /// multipliers in Systems_ProbabilisticFumbleModel can triple it on a big
        /// hit with help, which is where fumbles actually come from.
        /// </summary>
        public const float FUMBLE_BASE_CHANCE = 0.012f;

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
        ///
        /// IT ALSO SETS ACCELERATION, AND THAT IS WHY IT MOVED. Under linear drag
        /// the equation of motion is v' = d * (v_top - v), so damping is the whole
        /// of the acceleration curve: initial acceleration is d * v_top and the
        /// speed-up time constant is exactly 1 / d.
        ///
        /// At the old 1.5 a receiver left the line at 1.5 * 9.6 = 14.4 m/s², which
        /// is about 1.5 g — roughly double what a human sprinter produces — and hit
        /// 63% of top speed in 0.67 s and 95% in 2 s. Every player therefore
        /// snapped to full speed almost on the snap, which is what made the game
        /// read as fast-forward however sane the top speeds looked on paper.
        ///
        /// At 0.8 the same receiver leaves at 7.7 m/s² (0.78 g, which is what
        /// sprint-start force plates actually measure) with a 1.25 s time constant,
        /// so it takes about 3.7 s and 25 m to be genuinely at top speed — a
        /// realistic build-up, and the reason a linebacker now has time to close.
        ///
        /// Drive force is derived from this, so lowering it lowers every role's
        /// applied force by the same factor and no role's top speed changes.
        /// </summary>
        public const float LINEAR_DAMPING = 0.8f;

        /// <summary>
        /// Rotational damping. Same relationship as LINEAR_DAMPING: the turn-rate
        /// time constant is 1 / this, so 6 means a player reaches its terminal
        /// turn rate in about 0.17 s. Left alone — the unrealistic part of turning
        /// was the terminal rate itself, not how quickly it was reached, and that
        /// is Systems_RoleTable.TurnRateOf.
        /// </summary>
        public const float ANGULAR_DAMPING = 6f;

        /// <summary>Collider radius. Also the lever arm in the moment of inertia.</summary>
        public const float PLAYER_RADIUS = 0.5f;

        /// <summary>
        /// Hard velocity ceiling — the pileup-explosion guard (criterion #13).
        /// Set above the fastest role's 9.3 m/s so ordinary running never touches
        /// it and a collision may legitimately overshoot; it exists to catch the
        /// solver blowing a stack of bodies apart, not to cap sprinting.
        ///
        /// It matters more than it did. Halving LINEAR_DAMPING halves how quickly
        /// the solver bleeds off the velocity a bad contact injects, so a body
        /// kicked out of a pileup now coasts for over a second rather than a third
        /// of one. The ceiling is what stops that becoming a player leaving the
        /// stadium.
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

        // --- The ball's fake third axis (presentation only) ------------------
        /// <summary>
        /// Vertical launch speed as a fraction of the throw's horizontal speed.
        ///
        /// 0.32 of a PASS_SPEED_MAX throw is roughly 8 m/s up, which peaks around
        /// 3.3 m and hangs for about 1.6 s — a ball that clears twenty-two shapes
        /// convincingly without floating like a punt.
        ///
        /// This drives <see cref="Systems_BallModel.Height"/>, which nothing in the
        /// simulation reads. Changing it cannot affect a trained policy.
        /// </summary>
        public const float PASS_LOFT_RATIO = 0.32f;

        /// <summary>
        /// Gravity for that fake axis, m/s². Earth, matching CLAUDE.md section 2 —
        /// the arc should fall at the rate everything else in the world would.
        ///
        /// Deliberately a separate constant from Physics2D.gravity, which is zero
        /// here: the players are a top-down plane with no gravity at all, and
        /// borrowing the engine's value would tie a cosmetic arc to a physics
        /// setting that every brain was fitted against.
        /// </summary>
        public const float PASS_GRAVITY = 9.81f;

        /// <summary>
        /// Ticks after the snap before the quarterback's play call is allowed to
        /// latch — the dropback.
        ///
        /// WHY THIS EXISTS. The call used to latch on the FIRST decision step after
        /// the snap, which meant the quarterback committed before it had moved,
        /// before the rush arrived, and before any receiver had run a step. It was
        /// choosing off the pre-snap alignment alone. A quarterback with no
        /// information to separate a good pass from a good handoff has little reason
        /// to prefer either, and collapsing onto whichever one paid last is cheap —
        /// which is what football_base05 did on the halfback handoff, 99% of downs.
        ///
        /// 40 ticks is 0.8 s at the pinned 50 Hz, and eight decision steps at
        /// DECISION_PERIOD 5, so the policy gets several observations of the pocket
        /// collapsing before it has to commit. It is well inside THROW_WINDOW_TICKS.
        ///
        /// This changes WHEN the call is read, not the shape of anything. The
        /// observation vector and the action spec are untouched, so the promotion
        /// gate still passes — but a brain fitted before this change latched on a
        /// different tick and is not interchangeable with one fitted after it, which
        /// is why Agent_ActionContract.CONTRACT_REVISION moves with it.
        /// </summary>
        public const int DROPBACK_TICKS = 40;

        /// <summary>
        /// How far behind the line of scrimmage the quarterback retreats during
        /// DROPBACK_TICKS, in yards. Seven is a conventional pass-set depth: far
        /// enough to see over the line, near enough that the tackles can still wall
        /// off the edge before the quarterback is reached.
        ///
        /// Read by the heuristic only. A trained policy is free to drop back further,
        /// less, or not at all — this is where the scripted quarterback goes, not a
        /// constraint on the learned one.
        /// </summary>
        public const float DROPBACK_DEPTH_YARDS = 7f;

        /// <summary>
        /// Yards of room the scripted quarterback keeps between the back of its
        /// pocket and its own goal line.
        ///
        /// A full DROPBACK_DEPTH_YARDS drop from inside our own seven finishes
        /// behind the goal line, and the quarterback took it — conceding a safety
        /// on purpose whenever the offense was backed up, because the target point
        /// was an unclamped offset from the line of scrimmage. Two yards is enough
        /// that a tackle at the back of the pocket is still only a long loss rather
        /// than two points.
        /// </summary>
        public const float POCKET_GOAL_LINE_CUSHION_YARDS = 2f;

        /// <summary>
        /// How often the scripted quarterback takes a deep shot instead of the
        /// safest throw: one pass play in this many. 2 means every other one.
        ///
        /// WHY THIS EXISTS. MostOpenReceiver scores candidates on separation alone,
        /// so it always preferred a back in the flat with nobody near him over a
        /// receiver twenty yards downfield with a safety five yards off. Every throw
        /// was the checkdown, the ball never travelled, and net yards per play sat
        /// negative because the offense had no deep threat to respect. Alternating
        /// gives the defense both problems to solve.
        ///
        /// The choice is made from Systems_PlayModel.EpisodeIndex, not a random
        /// draw, because execution here is deterministic — the same seed must
        /// produce the same play, call for call.
        ///
        /// Read by the heuristic only. A trained policy chooses its own targets.
        /// </summary>
        /// SETTLED AT 4 BY BISECTION, WITH BOTH ENDPOINTS MEASURED OVER THREE GAMES
        /// EACH — single games here range 6.94 to 9.34 on one config, so nothing
        /// below a three-game mean is evidence:
        ///
        ///     N = 2   8.09 yards a play, 0.47 touchdowns a drive
        ///     N = 4   4.89 yards a play, 0.33 touchdowns a drive   <- ships
        ///     N = 6   ~4.1 yards a play, 0.23 touchdowns a drive
        ///
        /// Real football is 5.5 and 0.20-0.35. N = 4 puts touchdowns per drive
        /// inside that band and yards per play about eleven percent under target,
        /// which is comfortably inside the spread of the three games behind it
        /// (5.35, 4.03, 5.29). Closing that last half-yard is a real option — N = 3
        /// is the obvious next probe — but the difference is smaller than the noise
        /// on a three-game mean, so it needs more games than it is worth to confirm.
        ///
        /// RAISED FROM 2, AND THE MEASUREMENT IS THE POINT. Alternate
        /// plays meant HALF of all snaps were a scripted deep shot, which is not a
        /// mix any offense has ever run — the NFL throws past fifteen yards on
        /// roughly one attempt in six.
        ///
        /// What it produced, from a live box score across a full game: completions
        /// averaging 48.6 yards for one side and 12.2 for the other, against 2.6 and
        /// 0.9 yards a CARRY. So the offense was not hard to stop on the ground at
        /// all — it was scoring almost entirely on explosive throws, and that is
        /// where a three-game mean of 8.09 yards a play against real football's 5.5
        /// was coming from.
        ///
        /// This matters because the obvious reading of "too many yards per play" is
        /// that tackling is too weak, and every knob for that — SUSTAINED_TACKLE_
        /// TICKS, TACKLE_CLOSING_SPEED, EVASION_LATERAL — would have been turned the
        /// wrong way. A run game already at 0.9 yards a carry does not need to be
        /// made harder. The deep shot is the term that was out of proportion, so the
        /// deep shot is the term that moved.
        ///
        /// Six keeps the deep threat the constant exists for — the summary above is
        /// still true, and a defense that never has to respect anything behind it
        /// collapses onto the run — while making it the exception a deep shot
        /// actually is.
        public const int DEEP_SHOT_EVERY_N_PLAYS = 4;

        /// <summary>
        /// How far past the line of scrimmage a receiver must be, in yards, before
        /// the scripted quarterback will count it as a deep target.
        ///
        /// Fifteen is past the safeties' usual depth and roughly 0.6 s of flight at
        /// PASS_SPEED_MAX, which is long enough that the lead solve in LeadAim
        /// actually matters. Anything shorter is the checkdown the deep shot exists
        /// to avoid.
        /// </summary>
        public const float DEEP_SHOT_MIN_YARDS = 12f;

        /// <summary>
        /// The least separation, in metres, a deep receiver needs before the
        /// scripted quarterback will throw it. Below this the ball is a gift to the
        /// safety, so the quarterback takes the ordinary read instead.
        ///
        /// Three metres is two and a half CATCH_RADIUS — enough that the receiver
        /// reaches the ball first without the throw being uncontested.
        /// </summary>
        public const float DEEP_SHOT_MIN_ROOM = 2.5f;

        /// <summary>
        /// The tick a deep shot is released on, against THROW_AT_TICK for an
        /// ordinary throw.
        ///
        /// A fifteen-yard route needs longer to develop than a flat route, and
        /// releasing on the same tick as the checkdown meant the deep receiver was
        /// still eight yards downfield when the ball left. 110 ticks is 2.2 s at the
        /// pinned 50 Hz — a realistic hold, and well inside THROW_WINDOW_TICKS so
        /// the quarterback still throws rather than scrambling.
        /// </summary>
        public const int DEEP_SHOT_THROW_AT_TICK = 150;

        /// <summary>
        /// How far goalside of a rusher a blocker tries to stand, in metres.
        ///
        /// WHY POSITION AND NOT FORCE. A blocker used to drive at the rusher's
        /// current position, which means arriving where the rusher just was and
        /// shoving from behind. Standing on the line BETWEEN the rusher and the ball
        /// cuts the angle instead, and an offensive line that cuts angles buys the
        /// quarterback the time a dropback needs.
        ///
        /// The alternative — making linemen heavier or stronger — would change the
        /// dynamics every brain is fitted against (CLAUDE.md section 2) to fix
        /// something that is really a targeting problem. Offensive and defensive
        /// linemen keep identical mass and identical top speed.
        ///
        /// 1.2 m is just over two body radii (PLAYER_RADIUS 0.5), so the blocker
        /// occupies the rusher's path rather than overlapping him.
        /// </summary>
        public const float BLOCK_CUSHION = 1.2f;

        /// <summary>
        /// How far off the middle of the field the backs slide while the quarterback
        /// is dropping back, in metres.
        ///
        /// The formation stacks the quarterback, fullback and halfback on x = 0 at
        /// -2.5, -4.5 and -6.5, and DROPBACK_DEPTH_YARDS retreats the quarterback
        /// straight through both of them. They clear the lane instead of being run
        /// over, which is also what a back actually does on a pass: release to the
        /// flat rather than stand in the pocket.
        ///
        /// 3 m is six body radii off centre — clear of the quarterback, still inside
        /// HANDOFF_RADIUS of the lane it will cross if the call comes back a run.
        /// </summary>
        public const float POCKET_LANE_X = 3.0f;

        // --- Ball carrier (heuristic offense) --------------------------------
        /// <summary>
        /// Metres at which a chasing defender starts to make the carrier cut. Beyond
        /// it he runs straight at the goal line and ignores everybody.
        /// </summary>
        public const float EVASION_RANGE = 6f;

        /// <summary>
        /// Widest sidestep the carrier will aim for, in metres, reached only when a
        /// defender is right on top of him. Was effectively 8 and unconditional —
        /// see the carrier branch of Agent_FootballPlayer.TargetPoint.
        /// </summary>
        public const float EVASION_LATERAL = 4f;

        // --- Coverage (heuristic defense) ------------------------------------
        /// <summary>
        /// How far goalside of its assigned receiver a cover defender tries to
        /// stand, in metres.
        ///
        /// Goalside, not on top of: a defender occupying the receiver's own square
        /// metre is beaten by whichever of the two moves first, because it has to
        /// react and the receiver does not. Standing between the receiver and the
        /// end zone means the defender is already where the play has to go.
        ///
        /// TIGHTENED FROM 1.5 TO 1.0 FOR CONTRACT REVISION 8. At 1.5 m the throw was
        /// essentially uncontested: a measured full game produced ONE incompletion
        /// and ONE interception in 45 plays, so a pass was very nearly a guaranteed
        /// completion and the coverage the defense spends the whole play running was
        /// worth nothing. Systems_BallSystem.FindCatcher awards the ball to the
        /// nearest eligible body, and a defender parked a metre and a half away
        /// simply never was the nearest.
        ///
        /// 1.0 m is two body radii — still goalside, still not occupying the
        /// receiver's own square metre, and now inside the range where the defender
        /// actually wins some of the contests it is in position for.
        /// </summary>
        public const float COVERAGE_CUSHION = 1.0f;

        /// <summary>
        /// How far beyond the line of scrimmage a linebacker sets up before the ball
        /// declares, in yards.
        ///
        /// Linebackers are the one group with no man assignment and no pass rush, so
        /// without a landmark they simply joined the rush — which is what turned the
        /// whole defense into eleven bodies converging on the quarterback and left
        /// every receiver running free.
        ///
        /// PULLED IN FROM 5 TO 3.5, AND THIS IS THE CONSTANT THAT MADE FOURTH DOWN A
        /// REAL EVENT. At five yards the second level was consistently arriving after
        /// the run had already made the line to gain: fourth downs faced went from
        /// 2.3 a game to 8.7 on this change alone, and it is the first setting under
        /// which the field-goal code in Systems_IKickModel ever executed, because an
        /// offense has to actually stall in the opponent's half to attempt one.
        ///
        /// It costs something and the cost is honest: a linebacker this close is
        /// beaten more completely when he IS beaten, so the yardage distribution gets
        /// more bimodal. SAFETY_DEPTH_YARDS below is what covers that.
        /// </summary>
        public const float LINEBACKER_DROP_YARDS = 3.5f;

        /// <summary>
        /// How far beyond the line of scrimmage the free safety plays, in yards.
        /// Deep enough that nothing gets behind it, which is the entire job.
        ///
        /// BROUGHT UP FROM 14 TO 11 — the counterweight to LINEBACKER_DROP_YARDS. A
        /// shallower second level lets more runs break clean, and at 14 the safety
        /// was too far off to clean them up: yards per play sat at 7.5 with the
        /// linebackers pulled in. At 11 it fell to 6.2 and touchdowns per drive to
        /// 0.36, which is inside the range a real season posts.
        ///
        /// 9 was tried and is worse than either — yards per play 9.6. A safety that
        /// shallow is no longer the last man, and anything past him is a touchdown.
        /// The number is a genuine optimum rather than a direction to keep pushing.
        /// </summary>
        public const float SAFETY_DEPTH_YARDS = 11f;

        /// <summary>
        /// How much of the ball's lateral position a zone defender leans toward,
        /// as a fraction. Leaning, not tracking: a linebacker that mirrors the
        /// quarterback step for step vacates the middle it is standing in.
        ///
        /// Raised 0.35 -> 0.5 alongside LINEBACKER_DROP_YARDS. A second level playing
        /// this close to the line has to flow to the ball harder to be worth being
        /// there; at 0.35 it was downhill but stationary, which is the worst of both.
        /// </summary>
        public const float ZONE_BALL_LEAN = 0.5f;

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

        /// <summary>
        /// What conceding a safety costs the offense.
        ///
        /// Above INTERCEPTION_REWARD (0.6) because a safety is strictly worse than a
        /// turnover: the defense takes two points AND the ball, where an
        /// interception only takes the ball. Below TOUCHDOWN_REWARD (1.0) so that
        /// scoring stays the largest single number in the reward function and a
        /// policy is never better off avoiding its own end zone than attacking the
        /// other one.
        ///
        /// THIS IS THE TERM THAT WAS MISSING ENTIRELY. Before it, a quarterback
        /// tackled in its own end zone was scored as a tackle for loss — worth
        /// -(0.5 + 0.25) — so two points and possession cost 0.75, barely more than
        /// being dropped for a yard anywhere else on the field. Retreating was
        /// nearly free, and an undertrained quarterback duly retreated.
        /// </summary>
        public const float SAFETY_PENALTY = 0.85f;

        /// <summary>
        /// What a made field goal pays the offense.
        ///
        /// Roughly three sevenths of TOUCHDOWN_REWARD (1.0), because that is the
        /// ratio of the points. The ratio is the part that matters: price three
        /// points anywhere near seven and a policy learns to stop driving at the
        /// twenty and take the kick, which is the single most common way a football
        /// sim ends up looking nothing like football.
        /// </summary>
        public const float FIELD_GOAL_REWARD = 0.4f;

        /// <summary>
        /// What a missed field goal costs. Larger than PUNT_PENALTY because the miss
        /// really is worse than a punt from the same spot — the defense takes over at
        /// the spot of the kick, seven yards behind the line of scrimmage, instead of
        /// forty yards downfield. That gap is what makes a long attempt a decision
        /// rather than a free roll.
        /// </summary>
        public const float FIELD_GOAL_MISS_PENALTY = 0.35f;

        /// <summary>
        /// What punting costs the offense. Small on purpose.
        ///
        /// Punting is the RIGHT call on most fourth downs, and a reward function that
        /// treats it like a turnover teaches the offense to avoid the correct
        /// decision — you get a policy that goes for it on 4th and 12 from its own 15
        /// because the alternative was priced as a mistake. What is left here is just
        /// the cost of having failed to convert, so a first down from the same spot
        /// still beats a punt, and a punt still beats being tackled short of the
        /// sticks (-0.75).
        /// </summary>
        public const float PUNT_PENALTY = 0.12f;

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
