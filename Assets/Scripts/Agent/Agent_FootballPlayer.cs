using PoFootball.Models;
using PoFootball.Rewards;
using PoFootball.Sensors;
using PoFootball.Systems;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;
using VContainer;

namespace PoFootball.Agents
{
    /// <summary>
    /// One player. Every player in the game is this component — role, brain,
    /// team id and body dynamics are all derived from a single serialized formation
    /// slot index, so the scene carries one number per player instead of five
    /// fields that can disagree with each other.
    ///
    /// There are no per-position subclasses. The six brain groups differ only by
    /// which policy they are bound to and which action space they carry, both of
    /// which are data. If a position ever needs genuinely different code, that is
    /// the point to introduce one.
    ///
    /// Not constructed by VContainer — Unity instantiates it from the scene, and
    /// Systems_GameLifetimeScope injects it through a build callback.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(BehaviorParameters))]
    [RequireComponent(typeof(DecisionRequester))]
    [DisallowMultipleComponent]
    public sealed class Agent_FootballPlayer
        : Agent, Systems_IPlayerHandle, Systems_ITrainingHandle
    {
        /// <summary>
        /// Physics tick the heuristic quarterback releases on. Late enough that the
        /// routes have separated, comfortably inside
        /// Systems_SimConstants.THROW_WINDOW_TICKS.
        /// </summary>
        private const int THROW_AT_TICK = 60;

        [Tooltip("Index into Systems_Formation. Determines role, brain, team and start position.")]
        [SerializeField] private int _formationSlotIndex;

        private Transform _transform;
        private Rigidbody2D _rigidbody;
        private SpriteRenderer _spriteRenderer;
        private Color _teamColor = Color.white;

        // Role and side are read on nearly every physics tick by the reward
        // geometry below, and resolving them means indexing the formation table and
        // switching on the result. Cached once rather than recomputed 22 times per
        // tick per agent (.claude/rules/performance.md).
        private Systems_PlayerRole _role;
        private Systems_TeamSide _side;

        private float _driveForce;
        private float _steerTorque;
        private float _fatigue;
        private bool _isCarrier;
        private float _previousBallY;
        private float _previousBallDistance;
        private int _lastSeenEpisode = -1;
        /// <summary>
        /// Index of the play-call branch in the quarterback's discrete action space.
        /// Branch 1 is the throw trigger; see Agent_ActionContract.For.
        /// </summary>
        private const int PLAY_CALL_BRANCH = 0;

        private bool _hasQuarterbackActions;
        private bool _isBlocker;

        /// <summary>
        /// Fullback or halfback — the two roles that line up in the quarterback's
        /// retreat lane and have to vacate it on a dropback.
        /// </summary>
        private bool _isBack;
        private bool _isReceiver;

        /// <summary>Reused every tick so building observations allocates nothing.</summary>
        private readonly float[] _observationBuffer =
            new float[Sensor_FootballState.OBSERVATION_SIZE];

        // Rolling play-call history behind the repetition penalty. Quarterback
        // only; every other agent allocates two small arrays it never touches,
        // which is cheaper than branching on role to avoid it.
        private readonly int[] _callCounts =
            new int[Sensor_FootballState.PLAY_CALL_BRANCH_SIZE];

        private readonly Systems_PlayCall[] _callWindow =
            new Systems_PlayCall[Systems_SimConstants.CALL_HISTORY_PLAYS];

        private int _callWindowCursor;
        private int _callWindowFilled;

        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_BallSystem _ballSystem;
        private Systems_FieldModel _field;
        private Systems_PlayerRegistry _registry;
        private Systems_Referee _referee;

        /// <summary>
        /// Training or Game. Read once, in Awake, for exactly one decision — see
        /// <see cref="ConfigureTrainerLink"/>. Defaults to Training, so an agent
        /// dropped into a scene with no lifetime scope behaves as it always has.
        /// </summary>
        private Systems_SimMode _simMode = Systems_SimMode.Training;

        public int Id => _formationSlotIndex;

        public Systems_PlayerRole Role => _role;

        public Systems_TeamSide Side => _side;

        public Vector2 Position => _rigidbody == null ? Vector2.zero : _rigidbody.position;

        public Vector2 Velocity => _rigidbody == null ? Vector2.zero : _rigidbody.linearVelocity;

        public float Fatigue => _fatigue;

        public bool IsCarrier => _isCarrier;

        /// <summary>
        /// Called by Systems_GameLifetimeScope's build callback, which runs from the
        /// scope's Awake at execution order -5000 — before this component's Awake.
        /// Nothing here may touch cached Unity references.
        /// </summary>
        [Inject]
        public void Construct(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_BallSystem ballSystem,
            Systems_FieldModel field,
            Systems_PlayerRegistry registry,
            Systems_Referee referee,
            Systems_SimMode simMode)
        {
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _field = field;
            _registry = registry;
            _referee = referee;
            _simMode = simMode;

            // Before Register, not after: the registry hands this instance to the
            // rest of the graph, and Role and Side are cached fields now rather
            // than live lookups. A reader between here and Awake would otherwise
            // see the enum's default value, which is a real role (Quarterback) and
            // so fails silently rather than loudly.
            CacheRole();
            registry.Register(_formationSlotIndex, this);
        }

        /// <summary>
        /// Idempotent. Called from Construct and again from Awake, because an agent
        /// dropped into a scene with no lifetime scope never gets a Construct.
        /// </summary>
        private void CacheRole()
        {
            _role = Systems_Formation.GetSlot(_formationSlotIndex).Role;
            _side = Systems_RoleTable.SideOf(_role);
        }

        protected override void Awake()
        {
            // Configure the brain BEFORE Agent.Awake rather than after.
            //
            // Every one of the 22 BehaviorParameters in the scene serializes the
            // component default — zero continuous actions and one discrete branch
            // of size one — and this component replaces that at runtime from the
            // formation slot. Doing it before the base call means the actuator
            // manager, the policy and the BrainParameters sent over the wire to
            // Python are all built from one spec, instead of relying on the exact
            // point in Agent's initialisation at which each of them reads it.
            //
            // Ordering was NOT the cause of the `ArgumentException: length` flood
            // seen while bringing football_base04 up. That came from running this
            // scene with no trainer attached: all 22 agents had BehaviorType
            // Default with a stale Football_v01 .onnx assigned, so ML-Agents fell
            // back to inference against a 25-observation, 2-continuous-action brain
            // and the shapes could not line up. With a trainer connected the policy
            // is remote and the assigned model is ignored, which is why training
            // ran clean either way.
            //
            // That flood cannot recur: ConfigureBrain below now assigns the model
            // from Agent_BrainRegistry unconditionally, overwriting whatever the
            // scene serialized, and Football_v01 has been deleted outright.
            CacheRole();
            _hasQuarterbackActions =
                Systems_RoleTable.HasQuarterbackActions(Systems_RoleTable.BrainOf(_role));

            // Before base.Awake, which is where Agent registers the communicator.
            ConfigureTrainerLink();
            ConfigureBrain();

            // Agent.Awake registers the RPC communicator. Hiding it instead of
            // overriding leaves that registration to the Academy alone, which
            // happens to work but is not what ML-Agents intends.
            base.Awake();

            _transform = transform;
            _rigidbody = GetComponent<Rigidbody2D>();

            _driveForce = Systems_RoleTable.DriveForceOf(_role);
            _steerTorque = Systems_RoleTable.SteerTorqueOf(_role);
            _isBlocker = Reward_Role.IsBlocker(_role);
            _isReceiver = Reward_Role.IsReceiver(_role);
            _isBack = _role == Systems_PlayerRole.Fullback
                || _role == Systems_PlayerRole.RunningBack;

            // Team tint is authored in the scene; cache it so the carrier
            // highlight can be reverted to exactly what it was.
            _spriteRenderer = GetComponent<SpriteRenderer>();
            if (_spriteRenderer != null)
            {
                _teamColor = _spriteRenderer.color;
            }

            // Field geometry is pure configuration with no mutable state, so a
            // local instance is equivalent to the injected singleton. Having one
            // guarantees CollectObservations always emits a full vector, even if
            // this agent was dropped into a scene with no lifetime scope.
            if (_field == null)
            {
                _field = new Systems_FieldModel();
            }

            ConfigureBody();
        }

        /// <summary>
        /// Dynamics are set from code rather than serialized in the scene. A scene
        /// edit that quietly changed mass or damping would mean every existing
        /// .onnx is evaluated against different dynamics than it was fitted
        /// against (CLAUDE.md section 2).
        /// </summary>
        private void ConfigureBody()
        {
            // Top-down view: the field is the XY plane, so there is no gravity to
            // fall under. CLAUDE.md's -9.81 applies to side-on articulated bodies.
            _rigidbody.gravityScale = 0f;

            // Mass is per role now. It was a flat 100 kg for everyone through
            // base03, which meant a 92 kg corner and a 140 kg guard carried
            // identical momentum into a collision and the pileup was decided
            // purely by who happened to be moving faster.
            _rigidbody.mass = Systems_RoleTable.MassOf(_role);
            _rigidbody.linearDamping = Systems_SimConstants.LINEAR_DAMPING;
            _rigidbody.angularDamping = Systems_SimConstants.ANGULAR_DAMPING;
            _rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            _rigidbody.interpolation = RigidbodyInterpolation2D.None;
            _rigidbody.sleepMode = RigidbodySleepMode2D.NeverSleep;

            // The scene ships these bodies with FreezeRotation set. Steering is
            // applied as torque, so leaving that constraint on would silently
            // discard half of every action the policy takes.
            _rigidbody.constraints = RigidbodyConstraints2D.None;
        }

        /// <summary>
        /// Decides whether ML-Agents may go looking for a trainer at all.
        ///
        /// WHY: the Academy opens a gRPC connection to port 5004 the first time any
        /// Agent initialises, and when nothing is listening it blocks until the
        /// connect times out. Measured on this project that is about 4.3 seconds —
        /// 4.3 seconds of frozen, empty field after a player taps PLAY, on every
        /// single launch of a shipped game that has no trainer and never will. The
        /// only trace of it is an informational log about performing inference
        /// instead, which reads as routine.
        ///
        /// Set from the scene's own mode rather than probed, and set in BOTH
        /// directions rather than only switched off. A one-way switch would survive
        /// "Enter Play Mode without domain reload" and silently prevent the next
        /// training run in the same editor session from ever reaching mlagents-learn
        /// — a far worse bug than the one being fixed. Writing it every time makes
        /// the last scene loaded authoritative.
        ///
        /// Must run before base.Awake: the flag has no effect once the Academy has
        /// initialised, and Agent.Awake is what initialises it.
        /// </summary>
        private void ConfigureTrainerLink()
        {
            CommunicatorFactory.Enabled = _simMode != Systems_SimMode.Game;
        }

        private void ConfigureBrain()
        {
            Systems_BrainGroup group = Systems_RoleTable.BrainOf(_role);

            BehaviorParameters behaviorParameters = GetComponent<BehaviorParameters>();
            behaviorParameters.BehaviorName = Systems_RoleTable.BehaviorNameOf(group);
            behaviorParameters.TeamId = (int)_side;
            behaviorParameters.BrainParameters.VectorObservationSize =
                Sensor_FootballState.OBSERVATION_SIZE;
            behaviorParameters.BrainParameters.NumStackedVectorObservations = 1;

            // Only the quarterback's brain carries the play call, the throw trigger
            // and the aim vector. Every other brain is two continuous outputs, so
            // no policy spends capacity learning to ignore dimensions it can never
            // use — which is what the four backs and receivers sharing the
            // quarterback's action space were doing through base03.
            //
            // Branch 0 is PLAY_CALL_BRANCH_SIZE wide, not PLAY_CALL_SLOTS: index 0
            // is "no call yet" and maps to Systems_PlayCall.None.
            //
            // Built by Agent_ActionContract rather than inline, so the promotion
            // gate and the contract tests read the shape from the same place the
            // scene does.
            behaviorParameters.BrainParameters.ActionSpec = Agent_ActionContract.For(group);

            // The model is assigned here for the same reason the name and the
            // action spec are: so that nothing in a scene file can contradict the
            // contract. It used to be the one field left to hand-wiring, and the
            // result was a quarterback bound to a two-continuous-action brain
            // throwing ArgumentException out of ActuatorManager on every tick.
            //
            // A null model is a valid, supported outcome — ML-Agents runs
            // Heuristic instead, which is a playable game. See Agent_BrainTable.
            behaviorParameters.Model = Agent_BrainRegistry.ModelFor(group);

            DecisionRequester decisionRequester = GetComponent<DecisionRequester>();
            decisionRequester.DecisionPeriod = Systems_SimConstants.DECISION_PERIOD;
            decisionRequester.TakeActionsBetweenDecisions = true;
        }

        /// <summary>
        /// Deliberately empty. Systems_EpisodeDirector repositions all 22 players
        /// together after ending every episode, so 22 independent OnEpisodeBegin
        /// calls cannot half-reset the formation in an arbitrary order.
        /// </summary>
        public override void OnEpisodeBegin()
        {
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            Vector2 position = Position;
            bool hasPlay = _play != null;
            bool hasBall = _ball != null;

            Sensor_FootballState.Collect(
                sensor,
                _observationBuffer,
                _field,
                _role,
                position,
                Velocity,
                _rigidbody == null ? 0f : _rigidbody.rotation,
                _fatigue,
                _isCarrier,
                hasBall ? _ball.Position : position,
                hasBall ? _ball.Velocity : Vector2.zero,
                hasBall ? _ball.State : Systems_BallState.Held,
                hasPlay ? _play.Call : Systems_PlayCall.None,
                hasPlay ? _play.LineOfScrimmageY : 0f,
                hasPlay ? _play.Down : 1,
                hasPlay ? _play.YardsToGo : Systems_GameRules.YARDS_TO_GAIN);
        }

        /// <summary>
        /// Makes the kicking calls illegal everywhere they would be absurd, which is
        /// how the quarterback learns WHEN to use them rather than only that they
        /// exist.
        ///
        /// Masking rather than reward shaping, for two reasons. A punt on first down
        /// is not a bad decision to be discouraged, it is not a decision at all, and
        /// a policy should never spend exploration on it. And a field goal from
        /// eighty yards is not a choice with a poor expected value — it cannot
        /// happen, so letting the network propose it only teaches it to associate the
        /// action with a penalty it would never have collected on a real field.
        ///
        /// Only the quarterback has this branch; every other brain is continuous-only
        /// and never reaches here.
        /// </summary>
        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (!_hasQuarterbackActions || _play == null)
            {
                return;
            }

            bool isFourthDown = _play.Down >= Systems_GameRules.DOWNS_PER_SERIES;

            actionMask.SetActionEnabled(
                PLAY_CALL_BRANCH, (int)Systems_PlayCall.Punt, isFourthDown);

            actionMask.SetActionEnabled(
                PLAY_CALL_BRANCH,
                (int)Systems_PlayCall.FieldGoal,
                isFourthDown && IsInFieldGoalRange());
        }

        /// <summary>
        /// Distance to the goal line plus the seventeen yards every real field goal
        /// carries — ten of end zone and seven back to the hold.
        /// </summary>
        private bool IsInFieldGoalRange()
        {
            float yardsToGoalLine =
                (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - _play.LineOfScrimmageY)
                / Systems_FieldModel.YARD;

            return yardsToGoalLine + Systems_GameRules.FIELD_GOAL_SNAP_YARDS
                <= Systems_GameRules.FIELD_GOAL_MAX_YARDS;
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (_play == null || _play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            ActionSegment<float> continuous = actions.ContinuousActions;

            float drive = Mathf.Clamp(continuous[0], -1f, 1f);
            float steer = Mathf.Clamp(continuous[1], -1f, 1f);

            float fatigueScale = 1f - (_fatigue * Systems_SimConstants.FATIGUE_MAX_PENALTY);
            float driveForce = drive * _driveForce * fatigueScale;
            float steerTorque = steer * _steerTorque * fatigueScale;

            _rigidbody.AddForce(_transform.up * driveForce);
            _rigidbody.AddTorque(steerTorque);

            if (_hasQuarterbackActions)
            {
                HandleQuarterback(actions);
            }

            AccumulateFatigue(driveForce, steerTorque);
            ClampSpeed();
            AwardDenseRewards();
        }

        /// <summary>
        /// The quarterback commits to a call on its first decision after the snap
        /// and then executes it. The call is latched by the model, so anything sent
        /// on later steps is ignored.
        /// </summary>
        private void HandleQuarterback(ActionBuffers actions)
        {
            ActionSegment<int> discrete = actions.DiscreteActions;

            // Index 0 of the branch means "no call yet" and casts to
            // Systems_PlayCall.None, which LatchCall rejects. That is the entire
            // guard, and it holds regardless of timing.
            //
            // What it replaces: the branch was four wide, the agent added one to
            // the index, and so a ZERO — which is what ML-Agents stores between
            // EndEpisode and the next decision — decoded to a deliberate
            // KeepQuarterback. The old defence was to wait DECISION_PERIOD physics
            // ticks before latching, which assumes the Academy's step counter lines
            // up with an episode boundary the director chose independently. In
            // football_base03 the quarterback latched Keep on 8 of 8 instrumented
            // plays and possession never changed hands once.
            // The dropback. Nothing is committed until the quarterback has had
            // DROPBACK_TICKS to retreat and read the rush — before that, whatever
            // the policy emits on branch 0 is ignored, exactly as index 0 is.
            // Committing on the first decision step meant choosing off the pre-snap
            // alignment alone; see Systems_SimConstants.DROPBACK_TICKS.
            if (!_play.CallIsLatched
                && _play.PhysicsTick >= Systems_SimConstants.DROPBACK_TICKS)
            {
                _play.LatchCall((Systems_PlayCall)discrete[0]);

                // Priced the moment the call is committed, not at the whistle, so
                // the penalty lands on the decision that earned it rather than
                // being smeared across whatever the play then did.
                if (_play.CallIsLatched)
                {
                    ChargeCallRepetition(_play.Call);
                }
            }

            if (_play.Call != Systems_PlayCall.Pass || !_isCarrier || !_ball.IsHeld)
            {
                return;
            }

            if (_play.PhysicsTick > Systems_SimConstants.THROW_WINDOW_TICKS)
            {
                return;
            }

            if (discrete[1] != 1)
            {
                return;
            }

            ActionSegment<float> continuous = actions.ContinuousActions;
            Vector2 aim = new Vector2(continuous[2], continuous[3]);

            _ballSystem.Throw(this, aim, Mathf.Clamp01(aim.magnitude));
        }

        /// <summary>
        /// Load comes from APPLIED FORCE, never from the action vector. A player
        /// braced against a block is a near-zero action at near-maximum force, and
        /// reading the action would score that as resting (CLAUDE.md section 2).
        ///
        /// Each term is divided by this role's own maximum, so load is
        /// dimensionless and lands in [0, 2]. The previous form summed newtons and
        /// newton-metres directly, which is not a quantity, and the constants it
        /// was scaled by could not accumulate against the recovery rate at all —
        /// fatigue was pinned at zero for the whole of base01 through base03.
        /// </summary>
        private void AccumulateFatigue(float driveForce, float steerTorque)
        {
            float load = (Mathf.Abs(driveForce) / _driveForce)
                + (Mathf.Abs(steerTorque) / _steerTorque);

            _fatigue += load
                * Systems_SimConstants.FATIGUE_GAIN_PER_UNIT_LOAD
                * Time.fixedDeltaTime;
            _fatigue -= Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND * Time.fixedDeltaTime;
            _fatigue = Mathf.Clamp01(_fatigue);
        }

        /// <summary>
        /// Pileup-explosion guard (acceptance criterion #13). Sits above the
        /// fastest role's terminal velocity, so it only ever fires on a collision
        /// the solver has blown apart — it is not what limits running speed. That
        /// is LINEAR_DAMPING against the role's derived drive force.
        /// </summary>
        private void ClampSpeed()
        {
            Vector2 velocity = _rigidbody.linearVelocity;
            float maxSpeed = Systems_SimConstants.MAX_BODY_SPEED;

            if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
            {
                _rigidbody.linearVelocity = velocity.normalized * maxSpeed;
            }
        }

        /// <summary>
        /// Every dense term, paid per physics tick: the shared yardage signal and
        /// this player's own role-shaped term.
        ///
        /// Both baselines are seeded on the first tick of a new episode rather than
        /// paid out, so neither the jump from the previous play's dead-ball spot to
        /// this play's snap nor the re-formation of all 22 bodies is scored as
        /// progress or pursuit.
        /// </summary>
        private void AwardDenseRewards()
        {
            float ballY = _play.BallY;
            Vector2 ballPosition = _ball == null ? Position : _ball.Position;
            float ballDistance = Vector2.Distance(Position, ballPosition);

            if (_lastSeenEpisode != _play.EpisodeIndex)
            {
                _lastSeenEpisode = _play.EpisodeIndex;
                _previousBallY = ballY;
                _previousBallDistance = ballDistance;
                return;
            }

            float yardsDelta = (ballY - _previousBallY) / Systems_FieldModel.YARD;
            _previousBallY = ballY;

            AddReward(Reward_Progress.PerTick(_side, yardsDelta));
            AddReward(RoleReward(ballPosition, ballDistance));

            _previousBallDistance = ballDistance;
        }

        /// <summary>
        /// The part of the reward that is this player's alone. Everything else on
        /// the sheet is identical for all eleven team-mates, so without this a
        /// guard who held his block and a guard who wandered off are the same
        /// gradient (see Reward_Role).
        /// </summary>
        private float RoleReward(Vector2 ballPosition, float ballDistance)
        {
            if (_side == Systems_TeamSide.Defense)
            {
                return Reward_Role.Pursuit(_previousBallDistance - ballDistance);
            }

            if (_isBlocker)
            {
                Systems_IPlayerHandle rusher = NearestOpponent();
                return rusher == null
                    ? 0f
                    : Reward_Role.Block(Position, rusher.Position, ballPosition);
            }

            if (_isReceiver && !_isCarrier)
            {
                Systems_IPlayerHandle defender = NearestOpponent();
                return defender == null
                    ? 0f
                    : Reward_Role.Separation(Vector2.Distance(Position, defender.Position));
            }

            return 0f;
        }

        /// <summary>
        /// Nearest player on the other side. A linear scan of the fixed-capacity
        /// registry — 21 comparisons, no allocation, no physics query. A
        /// Physics2D.OverlapCircle would be the obvious alternative and is worse
        /// here: it allocates unless given a preallocated buffer, and it reads
        /// collider positions from the previous solver step rather than the
        /// rigidbody positions the rest of the reward geometry uses.
        /// </summary>
        private Systems_IPlayerHandle NearestOpponent()
        {
            if (_registry == null)
            {
                return null;
            }

            Vector2 position = Position;
            Systems_IPlayerHandle best = null;
            float bestSquared = float.MaxValue;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);
                if (candidate == null || candidate.Side == _side)
                {
                    continue;
                }

                float squared = (candidate.Position - position).sqrMagnitude;
                if (squared < bestSquared)
                {
                    bestSquared = squared;
                    best = candidate;
                }
            }

            return best;
        }

        private void OnCollisionEnter2D(Collision2D collision)
        {
            if (!_isCarrier || _referee == null)
            {
                return;
            }

            if (!collision.gameObject.TryGetComponent(out Agent_FootballPlayer other))
            {
                return;
            }

            if (other.Side == _side)
            {
                return;
            }

            _referee.ReportContactWithCarrier(other.Id, collision.relativeVelocity.magnitude);
        }

        /// <summary>
        /// Fires every physics tick the carrier stays in contact. Feeds the
        /// wrap-up tackle rule, which is the only way a defender chasing from
        /// behind can end a play — a pursuit at matched speed has almost no
        /// relative velocity for OnCollisionEnter2D to measure.
        /// </summary>
        private void OnCollisionStay2D(Collision2D collision)
        {
            if (!_isCarrier || _referee == null)
            {
                return;
            }

            if (!collision.gameObject.TryGetComponent(out Agent_FootballPlayer other))
            {
                return;
            }

            if (other.Side == _side)
            {
                return;
            }

            _referee.ReportSustainedContact(other.Id, collision.relativeVelocity.magnitude);
        }

        public void ClearFatigue()
        {
            _fatigue = 0f;
        }

        public void ResetTo(Vector2 position)
        {
            float rotation = _side == Systems_TeamSide.Offense ? 0f : 180f;

            _transform.SetPositionAndRotation(
                new Vector3(position.x, position.y, 0f),
                Quaternion.Euler(0f, 0f, rotation));

            _rigidbody.position = position;
            _rigidbody.rotation = rotation;
            _rigidbody.linearVelocity = Vector2.zero;
            _rigidbody.angularVelocity = 0f;
        }

        public void ApplyTerminalReward(
            Systems_PlayOutcome outcome, float netYards, bool passCompleted)
        {
            AddReward(Reward_Terminal.For(_side, outcome, netYards, passCompleted));
        }

        public void EndEpisodeNow()
        {
            EndEpisode();
        }

        /// <summary>
        /// Possession moves between the quarterback, the backs and any receiver who
        /// makes a catch, so the ball carrier is highlighted white — at the scale
        /// this renders at, a colour swap is the only thing readable.
        ///
        /// Presentation living on the agent rather than in PoFootball.Views is a
        /// deliberate compromise: SetCarrier is already the exact hook where
        /// possession changes, and a separate view would need its own copy of the
        /// formation slot index to know which player it belongs to. SpriteRenderer
        /// .color is per-instance and does not clone the material, so batching is
        /// unaffected (.claude/rules/performance.md).
        /// </summary>
        public void SetCarrier(bool isCarrier)
        {
            _isCarrier = isCarrier;

            if (_spriteRenderer != null)
            {
                _spriteRenderer.color = isCarrier ? Color.white : _teamColor;
            }
        }

        /// <summary>
        /// Rebinds the base colour cached in Awake. Called when possession changes,
        /// so the offense unit wears the colour of the team that actually has the
        /// ball rather than a fixed "offense is blue".
        ///
        /// The carrier check is the whole subtlety: repainting a carrier would erase
        /// the white highlight mid-play. Storing it and letting SetCarrier(false)
        /// apply it later keeps the two writers in one order.
        /// </summary>
        public void SetTeamColor(Color color)
        {
            _teamColor = color;

            if (_spriteRenderer != null && !_isCarrier)
            {
                _spriteRenderer.color = color;
            }
        }

        /// <summary>
        /// Scripted football, used whenever no trained brain is bound — which,
        /// until a set is promoted against the current contract, is always.
        ///
        /// WHY THIS IS NOT A STUB ANY MORE. It used to write zeros for everyone and
        /// a Keep call for the quarterback. That is a legal action vector, so the
        /// down machinery ran and the clock advanced, but nobody moved: every play
        /// hit the 750-tick cap and expired, no tackle ever happened, no pass was
        /// ever thrown, and the entire game was twenty-two stationary shapes. The
        /// app was unreviewable without a trained brain, and there is no trained
        /// brain.
        ///
        /// It is deliberately simple — pursue, block, run, throw — and makes no
        /// attempt to be good. Its job is to exercise every branch of the rules
        /// layer (tackles, incompletions, interceptions, touchdowns, turnovers on
        /// downs, safeties) so a playthrough is worth watching and a bug in the
        /// game layer has somewhere to show itself.
        ///
        /// STEERING IS CAR-LIKE, because that is what OnActionReceived implements:
        /// drive pushes along the body's own up axis and steer is a torque. Aiming
        /// at a point therefore means turning toward it first and only driving hard
        /// once roughly lined up, or the body spirals.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuousActions = actionsOut.ContinuousActions;
            ActionSegment<int> discreteActions = actionsOut.DiscreteActions;

            for (int index = 0; index < continuousActions.Length; index++)
            {
                continuousActions[index] = 0f;
            }

            for (int index = 0; index < discreteActions.Length; index++)
            {
                discreteActions[index] = 0;
            }

            if (_play == null || _play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            Steer(continuousActions, TargetPoint());

            if (_hasQuarterbackActions)
            {
                HeuristicQuarterback(continuousActions, discreteActions);
            }
        }

        /// <summary>
        /// Where this player wants to be. One expression per job, which is the
        /// whole of the "tactics".
        /// </summary>
        private Vector2 TargetPoint()
        {
            Vector2 position = Position;

            // Whoever has the ball runs at the goal line, drifting toward whichever
            // side of the field the nearest defender is not on. That drift is what
            // produces runs that break outside instead of everyone piling up on the
            // hash marks.
            // The scripted dropback, and then the pocket. A quarterback holding the
            // ball retreats off the line rather than running at the goal line like
            // any other carrier, which is what gives the pocket time to form and the
            // receivers time to get downfield.
            //
            // IT USED TO END THE INSTANT THE CALL LATCHED, and that was wrong. The
            // call latches DROPBACK_TICKS after the snap and the heuristic does not
            // release until THROW_AT_TICK, so a quarterback that had just committed
            // to a PASS spent the twenty ticks in between falling through to the
            // ordinary carrier branch below — turning round and running at the goal
            // line, straight into the four linemen rushing it, while still holding
            // the ball it was about to throw. Only once the throw window closes is
            // it really out of options, and only then does it scramble.
            if (_isCarrier && _hasQuarterbackActions && IsHoldingThePocket())
            {
                float dropback = _play.LineOfScrimmageY
                    - (Systems_SimConstants.DROPBACK_DEPTH_YARDS * Systems_FieldModel.YARD);

                // AND NOT INTO OUR OWN END ZONE. The drop used to be an unclamped
                // offset from the line of scrimmage, so any snap inside our own
                // seven put the target point behind the goal line and the
                // quarterback walked backwards over it — a safety, two points, and
                // the ball, conceded by the scripted behaviour on purpose every
                // time the offense was backed up. A real quarterback shortens the
                // drop when there is no room behind him, which is exactly what
                // taking the max does here.
                float floor = Systems_FieldModel.OWN_GOAL_LINE_Y
                    + (Systems_SimConstants.POCKET_GOAL_LINE_CUSHION_YARDS
                        * Systems_FieldModel.YARD);

                return new Vector2(position.x, Mathf.Max(dropback, floor));
            }

            if (_isCarrier)
            {
                Systems_IPlayerHandle chaser = NearestOpponent();
                float lateral = 0f;

                if (chaser != null && Mathf.Abs(chaser.Position.y - position.y) < 6f)
                {
                    lateral = position.x < chaser.Position.x ? -8f : 8f;
                }

                return new Vector2(
                    Mathf.Clamp(
                        position.x + lateral,
                        -Systems_FieldModel.HALF_WIDTH,
                        Systems_FieldModel.HALF_WIDTH),
                    Systems_FieldModel.ATTACKING_GOAL_LINE_Y);
            }

            if (_side == Systems_TeamSide.Defense)
            {
                return DefensiveTarget(position);
            }

            // Blockers stand BETWEEN the rusher and the ball rather than driving at
            // the rusher itself.
            //
            // Driving at him meant arriving where he had just been and shoving from
            // behind, which a rusher running past simply leaves. Taking the inside
            // position puts the blocker's body in the path to the quarterback, which
            // is the whole job — and it is the same thing Reward_Role.Block already
            // measures, so the scripted line and the reward finally want the same
            // thing instead of two different ones.
            if (_isBlocker)
            {
                Systems_IPlayerHandle opponent = NearestOpponent();
                if (opponent == null)
                {
                    return new Vector2(position.x, position.y + 4f);
                }

                Vector2 anchor = _ball == null ? position : _ball.Position;
                Vector2 rusherToAnchor = anchor - opponent.Position;

                // Degenerate only if a rusher is standing exactly on the ball, in
                // which case there is no line to take and meeting him is correct.
                if (rusherToAnchor.sqrMagnitude < 0.0001f)
                {
                    return opponent.Position;
                }

                return opponent.Position
                    + (rusherToAnchor.normalized * Systems_SimConstants.BLOCK_CUSHION);
            }

            // Backs clear the quarterback's retreat lane while the call is still
            // open. All three start on x = 0 — quarterback at -2.5, fullback at
            // -4.5, halfback at -6.5 — so a seven-yard dropback reverses straight
            // through both of them. Slot parity splits them opposite ways, which is
            // also what a back does on a pass: release to the flat.
            if (_isBack && !_play.CallIsLatched)
            {
                float lane = (_formationSlotIndex % 2 == 0) ? -1f : 1f;
                return new Vector2(
                    lane * Systems_SimConstants.POCKET_LANE_X, position.y);
            }

            // Receivers and backs run upfield and spread. Slot index parity fans
            // them left and right so they do not all occupy the same lane.
            float split = (_formationSlotIndex % 2 == 0) ? -1f : 1f;

            return new Vector2(
                Mathf.Clamp(
                    position.x + (split * 10f),
                    -Systems_FieldModel.HALF_WIDTH,
                    Systems_FieldModel.HALF_WIDTH),
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y);
        }

        /// <summary>
        /// Whether the quarterback is still a passer rather than a runner: either it
        /// has not committed to anything yet, or it has called a pass and the throw
        /// window is still open.
        ///
        /// A handoff drops out of here the moment it latches, which is correct — on
        /// a handoff the quarterback's job is to close on the back, and
        /// Systems_BallSystem.TryHandoff completes on proximity alone.
        /// </summary>
        private bool IsHoldingThePocket()
        {
            if (!_play.CallIsLatched)
            {
                return true;
            }

            return _play.Call == Systems_PlayCall.Pass
                && _play.PhysicsTick <= Systems_SimConstants.THROW_WINDOW_TICKS;
        }

        /// <summary>
        /// Where a defender wants to be.
        ///
        /// THIS USED TO BE ONE LINE — every defender, every tick, targeted the ball.
        /// That produced eleven bodies converging on the quarterback the instant the
        /// ball was snapped: no coverage, no leverage, nothing between a receiver and
        /// the end zone. The 4-3 with two-high safeties that Systems_Formation lines
        /// up was cosmetic, dissolving into a swarm before anyone had run a step, and
        /// the two split receivers were uncovered about a second after every snap.
        ///
        /// The structure below is Cover 1 and it is only three ideas deep:
        ///
        ///   THE BALL IN THE AIR OVERRIDES EVERYTHING. Assignments exist to stop the
        ///   ball being caught; once it is thrown, the ball IS the assignment. This
        ///   is also what keeps a pass contested rather than a free completion, which
        ///   is the property Systems_BallSystem.FindCatcher depends on to make
        ///   coverage worth learning at all.
        ///
        ///   SO DOES A BALL PAST THE LINE. A carrier who has crossed the line of
        ///   scrimmage has beaten the call; holding a zone behind him is how a five
        ///   yard gain becomes sixty. Everyone chases.
        ///
        ///   OTHERWISE, PLAY YOUR JOB. Linemen rush, linebackers hold a zone,
        ///   corners and the strong safety play their man, the free safety plays the
        ///   deep middle.
        ///
        /// None of this is reachable by a trained policy — it is the heuristic only.
        /// A brain sees the same observations it always did and is free to invent
        /// something better.
        /// </summary>
        private Vector2 DefensiveTarget(Vector2 position)
        {
            if (_ball == null || _play == null)
            {
                return position;
            }

            if (_ball.IsInFlight)
            {
                return _ball.Position;
            }

            Systems_IPlayerHandle carrier = CurrentCarrier();

            if (carrier == null)
            {
                return _ball.Position;
            }

            if (carrier.Position.y > _play.LineOfScrimmageY)
            {
                return carrier.Position;
            }

            switch (_role)
            {
                case Systems_PlayerRole.DefensiveLine:
                    return carrier.Position;

                case Systems_PlayerRole.Linebacker:
                    return LinebackerZone(carrier);

                case Systems_PlayerRole.Cornerback:
                case Systems_PlayerRole.Safety:
                    return CoverageSpot(carrier);

                default:
                    return carrier.Position;
            }
        }

        /// <summary>
        /// The ball's carrier, or null while it is in the air. Read straight from
        /// the ball model rather than through Systems_Referee so this works for an
        /// agent that was never injected with one.
        /// </summary>
        private Systems_IPlayerHandle CurrentCarrier()
        {
            if (_ball == null || !_ball.IsHeld || _registry == null)
            {
                return null;
            }

            return _registry.Get(_ball.CarrierId);
        }

        /// <summary>
        /// A linebacker's spot: its own alignment, dropped to a fixed depth beyond
        /// the line and leaning part of the way toward the ball.
        ///
        /// Anchored on the FORMATION x, not on the linebacker's current x. Leaning
        /// from where it already stands compounds every tick — each step closes a
        /// fraction of a gap that is then measured again from the new position — so
        /// the zone walks itself onto the quarterback within a second and the
        /// linebacker has silently become a fourth rusher.
        /// </summary>
        private Vector2 LinebackerZone(Systems_IPlayerHandle carrier)
        {
            float anchorX = Systems_Formation.GetSlot(_formationSlotIndex).OffsetX;

            float x = anchorX
                + ((carrier.Position.x - anchorX) * Systems_SimConstants.ZONE_BALL_LEAN);

            float y = _play.LineOfScrimmageY
                + (Systems_SimConstants.LINEBACKER_DROP_YARDS * Systems_FieldModel.YARD);

            return new Vector2(
                Mathf.Clamp(x, -Systems_FieldModel.HALF_WIDTH, Systems_FieldModel.HALF_WIDTH),
                y);
        }

        /// <summary>
        /// A cover defender's spot: goalside of the receiver it was assigned, or the
        /// deep middle if it was assigned nobody.
        /// </summary>
        private Vector2 CoverageSpot(Systems_IPlayerHandle carrier)
        {
            int assignedSlot = Systems_Formation.CoverageAssignmentFor(_formationSlotIndex);

            Systems_IPlayerHandle receiver = assignedSlot < 0 || _registry == null
                ? null
                : _registry.Get(assignedSlot);

            if (receiver == null)
            {
                // The free safety. Deep middle, leaning slightly to the ball, so
                // nothing gets behind the coverage down either seam.
                float deepY = _play.LineOfScrimmageY
                    + (Systems_SimConstants.SAFETY_DEPTH_YARDS * Systems_FieldModel.YARD);

                return new Vector2(
                    carrier.Position.x * Systems_SimConstants.ZONE_BALL_LEAN, deepY);
            }

            // Between the receiver and the end zone it is running at — see
            // Systems_SimConstants.COVERAGE_CUSHION for why not on top of it.
            return new Vector2(
                receiver.Position.x,
                receiver.Position.y + Systems_SimConstants.COVERAGE_CUSHION);
        }

        /// <summary>
        /// Turns a world-space destination into drive and steer.
        ///
        /// Drive is scaled by how well the body is already pointing at the target,
        /// so a player that needs to turn around rotates on the spot instead of
        /// driving away at full force while it does.
        /// </summary>
        private void Steer(ActionSegment<float> continuousActions, Vector2 target)
        {
            Vector2 toTarget = target - Position;

            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return;
            }

            // Signed angle from the body's facing to the target, in degrees.
            float error = Vector2.SignedAngle(_transform.up, toTarget.normalized);

            // Full lock at 45 degrees off. Proportional-only is enough here: the
            // rigidbody's angular drag supplies the damping a derivative term
            // would, and a player that overshoots slightly still reads as a player.
            continuousActions[1] = Mathf.Clamp(error / 45f, -1f, 1f);

            float alignment = Mathf.Cos(error * Mathf.Deg2Rad);
            continuousActions[0] = Mathf.Clamp01(alignment);
        }

        /// <summary>
        /// The quarterback's call and, on a pass, the throw.
        ///
        /// The call is chosen from field position rather than at random so a
        /// playthrough shows the whole playbook without being unwatchably erratic:
        /// throw from deep, run when close. Slot parity picks between the two
        /// handoffs, which is enough to keep both in the sample.
        /// </summary>
        private void HeuristicQuarterback(
            ActionSegment<float> continuousActions, ActionSegment<int> discreteActions)
        {
            if (!_play.CallIsLatched)
            {
                discreteActions[0] = (int)ChooseCall();
                return;
            }

            if (_play.Call != Systems_PlayCall.Pass || !_isCarrier || _ball == null
                || !_ball.IsHeld)
            {
                return;
            }

            // Hold the ball a moment so the routes develop, then release inside the
            // window OnActionReceived enforces. A deep shot holds longer, because a
            // fifteen-yard route is still eight yards downfield at THROW_AT_TICK.
            bool isDeepShot = IsDeepShotPlay();

            int releaseTick = isDeepShot
                ? Systems_SimConstants.DEEP_SHOT_THROW_AT_TICK
                : THROW_AT_TICK;

            if (_play.PhysicsTick < releaseTick
                || _play.PhysicsTick > Systems_SimConstants.THROW_WINDOW_TICKS)
            {
                return;
            }

            // On a deep play, look downfield first and only settle for the ordinary
            // read if nobody got open deep. Without the fallback a covered deep
            // route would hold the ball until the window shut and turn every deep
            // call into a scramble.
            Systems_IPlayerHandle target = isDeepShot ? DeepReceiver() : null;

            if (target == null)
            {
                target = MostOpenReceiver();
            }

            if (target == null)
            {
                return;
            }

            Vector2 aim = LeadAim(target);


            discreteActions[1] = 1;
            continuousActions[2] = aim.x;
            continuousActions[3] = aim.y;
        }

        /// <summary>
        /// Where to throw so that the ball and the receiver arrive together.
        ///
        /// THE PREVIOUS VERSION AIMED AT target.Position, AND THAT MADE MOST PASSES
        /// UNCATCHABLE BY CONSTRUCTION. Systems_BallSystem flies the ball in a
        /// straight line at a fixed speed and completes the catch only when someone
        /// is within CATCH_RADIUS — 1.2 m — of it. A receiver twenty metres downfield
        /// is most of a second of flight away and has run five or six metres in that
        /// time, so the ball arrived four or five catch radii behind him. Every
        /// throw at a moving target was a scripted incompletion, and no amount of
        /// route running or coverage could change that.
        ///
        /// The fix is the standard intercept solve. Guess a flight time from the
        /// present separation, walk the receiver along its own velocity by that
        /// much, remeasure against the new point. It converges geometrically; three
        /// passes is well under a centimetre for any throw this field allows, and it
        /// allocates nothing.
        ///
        /// The release speed is read from the same constant Systems_BallSystem uses,
        /// because HeuristicQuarterback hands it a unit-length aim and unit length
        /// is full power. If either side of that is ever retuned the lead follows it
        /// rather than quietly going stale.
        /// </summary>
        private Vector2 LeadAim(Systems_IPlayerHandle target)
        {
            Vector2 origin = Position;
            Vector2 targetPosition = target.Position;
            Vector2 targetVelocity = target.Velocity;

            const float SPEED = Systems_SimConstants.PASS_SPEED_MAX;

            Vector2 aimPoint = targetPosition;

            for (int iteration = 0; iteration < 3; iteration++)
            {
                float flightSeconds = (aimPoint - origin).magnitude / SPEED;
                aimPoint = targetPosition + (targetVelocity * flightSeconds);
            }

            Vector2 aim = aimPoint - origin;

            // A receiver standing exactly on the quarterback has no direction to be
            // led in; straight downfield is the same fallback Systems_BallSystem
            // applies to a degenerate aim.
            return aim.sqrMagnitude < 1e-4f ? Vector2.up : aim.normalized;
        }

        /// <summary>
        /// Charges the quarterback for repeating itself, and records the call in
        /// the rolling window the charge is computed from.
        ///
        /// The window is this agent's own, deliberately: Agent_Telemetry keeps a
        /// separate 200-play histogram for the Call/Entropy series, and the two
        /// must not share state. That one is a measurement and has to stay a
        /// faithful record of what happened; this one is part of the reward and
        /// feeds back into what happens next. Wiring the reward to read the
        /// telemetry would make the metric that detects a collapse a participant
        /// in causing one.
        /// </summary>
        private void ChargeCallRepetition(Systems_PlayCall call)
        {
            int index = (int)call;

            if (index < 0 || index >= _callCounts.Length)
            {
                return;
            }

            // Share BEFORE recording this call, so a quarterback is charged for the
            // habit it already had rather than for the play it is about to run.
            AddReward(Reward_Call.Repetition(Reward_Call.ShareOf(_callCounts, call)));

            // Fixed-size ring: evict the oldest, add the new one. No allocation.
            if (_callWindowFilled == Systems_SimConstants.CALL_HISTORY_PLAYS)
            {
                _callCounts[(int)_callWindow[_callWindowCursor]]--;
            }
            else
            {
                _callWindowFilled++;
            }

            _callWindow[_callWindowCursor] = call;
            _callCounts[index]++;
            _callWindowCursor =
                (_callWindowCursor + 1) % Systems_SimConstants.CALL_HISTORY_PLAYS;
        }

        private Systems_PlayCall ChooseCall()
        {
            float yardsToGoal = _field == null
                ? 50f
                : _field.YardsToAttackingGoal(Position.y);

            if (yardsToGoal > 25f)
            {
                return Systems_PlayCall.Pass;
            }

            return (_play.EpisodeIndex % 2 == 0)
                ? Systems_PlayCall.HandoffHalfback
                : Systems_PlayCall.HandoffFullback;
        }

        /// <summary>
        /// Roles that can catch a pass. Systems_RoleTable has no such predicate and
        /// this is the only caller, so it lives here rather than widening a model
        /// type for one heuristic.
        /// </summary>
        private static bool IsEligibleReceiver(Systems_PlayerRole role)
        {
            return role == Systems_PlayerRole.WideReceiver
                || role == Systems_PlayerRole.TightEnd
                || role == Systems_PlayerRole.RunningBack
                || role == Systems_PlayerRole.Fullback;
        }

        /// <summary>
        /// The receiver with the most room — furthest from its nearest defender,
        /// and downfield of the throw. Nothing clever, but it means a completion is
        /// a reward for the routes having spread rather than a coin flip.
        /// </summary>
        /// <summary>
        /// Whether this play is one of the scripted deep shots.
        ///
        /// Driven off the episode index rather than a random draw: execution here is
        /// deterministic, so the same seed has to produce the same call every time.
        /// </summary>
        private bool IsDeepShotPlay()
        {
            return _play != null
                && Systems_SimConstants.DEEP_SHOT_EVERY_N_PLAYS > 0
                && (_play.EpisodeIndex % Systems_SimConstants.DEEP_SHOT_EVERY_N_PLAYS) == 0;
        }

        /// <summary>
        /// The best target at least DEEP_SHOT_MIN_YARDS past the line of scrimmage,
        /// or null if nobody is that deep with room to catch it.
        ///
        /// WHY THIS IS SEPARATE FROM MostOpenReceiver. That method ranks on
        /// separation alone, and separation is exactly what a deep receiver does not
        /// have — there is a safety over the top by design, while a back released
        /// into the flat has the whole field to himself. So the most open receiver
        /// was the shortest one on every single snap, the ball never travelled, and
        /// the defense never had to respect anything behind it.
        ///
        /// The depth gate is measured from the line of scrimmage, not from the
        /// quarterback, because the quarterback has retreated DROPBACK_DEPTH_YARDS by
        /// the time it throws — measuring from where it stands would count the seven
        /// yards of its own dropback as route depth and call a flat route deep.
        /// </summary>
        private Systems_IPlayerHandle DeepReceiver()
        {
            if (_registry == null || _play == null)
            {
                return null;
            }

            float depthGate = _play.LineOfScrimmageY
                + (Systems_SimConstants.DEEP_SHOT_MIN_YARDS * Systems_FieldModel.YARD);

            float roomGate = Systems_SimConstants.DEEP_SHOT_MIN_ROOM
                * Systems_SimConstants.DEEP_SHOT_MIN_ROOM;

            Systems_IPlayerHandle best = null;
            float bestRoom = -1f;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Side != Systems_TeamSide.Offense
                    || candidate.Id == Id
                    || !IsEligibleReceiver(candidate.Role)
                    || candidate.Position.y < depthGate)
                {
                    continue;
                }

                float room = float.MaxValue;

                for (int other = 0; other < Systems_PlayerRegistry.CAPACITY; other++)
                {
                    Systems_IPlayerHandle defender = _registry.Get(other);

                    if (defender == null || defender.Side != Systems_TeamSide.Defense)
                    {
                        continue;
                    }

                    room = Mathf.Min(
                        room, (defender.Position - candidate.Position).sqrMagnitude);
                }

                if (room < roomGate)
                {
                    continue;
                }

                if (room > bestRoom)
                {
                    bestRoom = room;
                    best = candidate;
                }
            }

            return best;
        }

        private Systems_IPlayerHandle MostOpenReceiver()
        {
            if (_registry == null)
            {
                return null;
            }

            Systems_IPlayerHandle best = null;
            float bestRoom = -1f;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Side != Systems_TeamSide.Offense
                    || candidate.Id == Id
                    || !IsEligibleReceiver(candidate.Role)
                    || candidate.Position.y <= Position.y)
                {
                    continue;
                }

                float room = float.MaxValue;

                for (int other = 0; other < Systems_PlayerRegistry.CAPACITY; other++)
                {
                    Systems_IPlayerHandle defender = _registry.Get(other);

                    if (defender == null || defender.Side != Systems_TeamSide.Defense)
                    {
                        continue;
                    }

                    room = Mathf.Min(
                        room, (defender.Position - candidate.Position).sqrMagnitude);
                }

                if (room > bestRoom)
                {
                    bestRoom = room;
                    best = candidate;
                }
            }

            return best;
        }
    }
}
