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
        private bool _hasQuarterbackActions;
        private bool _isBlocker;
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
            Systems_Referee referee)
        {
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _field = field;
            _registry = registry;
            _referee = referee;

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
            // seen while bringing football_base04 up. That comes from running this
            // scene with no trainer attached: all 22 agents have BehaviorType
            // Default with a stale Football_v01 .onnx assigned, so ML-Agents falls
            // back to inference against a 25-observation, 2-continuous-action brain
            // and the shapes cannot line up. With a trainer connected the policy is
            // remote and the assigned model is ignored, which is why training runs
            // clean. Clear those model slots before judging anything by playing the
            // training scene directly.
            CacheRole();
            _hasQuarterbackActions =
                Systems_RoleTable.HasQuarterbackActions(Systems_RoleTable.BrainOf(_role));
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
                hasPlay ? _play.LineOfScrimmageY : 0f);
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
            if (!_play.CallIsLatched)
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
                // Defenders converge on the ball — its carrier while it is held, the
                // ball itself while it is in the air, which is what turns a pass
                // into a contested play rather than a free completion.
                return _ball == null ? position : _ball.Position;
            }

            // Blockers meet the nearest defender. Driving at them is enough: the
            // collision response does the blocking, and a lineman that reaches its
            // man is standing exactly where the rules layer wants it.
            if (_isBlocker)
            {
                Systems_IPlayerHandle opponent = NearestOpponent();
                return opponent == null
                    ? new Vector2(position.x, position.y + 4f)
                    : opponent.Position;
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
            // window OnActionReceived enforces.
            if (_play.PhysicsTick < THROW_AT_TICK
                || _play.PhysicsTick > Systems_SimConstants.THROW_WINDOW_TICKS)
            {
                return;
            }

            Systems_IPlayerHandle target = MostOpenReceiver();

            if (target == null)
            {
                return;
            }

            Vector2 aim = (target.Position - Position).normalized;

            discreteActions[1] = 1;
            continuousActions[2] = aim.x;
            continuousActions[3] = aim.y;
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
