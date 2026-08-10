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
    /// team id and top speed are all derived from a single serialized formation
    /// slot index, so the scene carries one number per player instead of five
    /// fields that can disagree with each other.
    ///
    /// There are no per-position subclasses. The four brain groups differ only by
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
    public sealed class Agent_FootballPlayer : Agent, Systems_IPlayerHandle
    {
        [Tooltip("Index into Systems_Formation. Determines role, brain, team and start position.")]
        [SerializeField] private int _formationSlotIndex;

        private Transform _transform;
        private Rigidbody2D _rigidbody;
        private SpriteRenderer _spriteRenderer;
        private Color _teamColor = Color.white;
        private float _topSpeed;
        private float _fatigue;
        private bool _isCarrier;
        private float _previousBallY;
        private int _lastSeenEpisode = -1;
        private bool _hasQuarterbackActions;

        /// <summary>Reused every tick so building observations allocates nothing.</summary>
        private readonly float[] _observationBuffer =
            new float[Sensor_FootballState.OBSERVATION_SIZE];

        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_BallSystem _ballSystem;
        private Systems_FieldModel _field;
        private Systems_Referee _referee;

        public int Id => _formationSlotIndex;

        public Systems_PlayerRole Role => Systems_Formation.GetSlot(_formationSlotIndex).Role;

        public Systems_TeamSide Side => Systems_RoleTable.SideOf(Role);

        public Vector2 Position => _rigidbody == null ? Vector2.zero : _rigidbody.position;

        public Vector2 Velocity => _rigidbody == null ? Vector2.zero : _rigidbody.linearVelocity;

        private bool IsQuarterback => Role == Systems_PlayerRole.Quarterback;

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
            _referee = referee;
            registry.Register(_formationSlotIndex, this);
        }

        protected override void Awake()
        {
            // Agent.Awake registers the RPC communicator. Hiding it instead of
            // overriding leaves that registration to the Academy alone, which
            // happens to work but is not what ML-Agents intends.
            base.Awake();

            _transform = transform;
            _rigidbody = GetComponent<Rigidbody2D>();
            _topSpeed = Systems_RoleTable.TopSpeedOf(Role);

            // Team tint is authored in the scene; cache it so the carrier
            // highlight can be reverted to exactly what it was.
            _spriteRenderer = GetComponent<SpriteRenderer>();
            if (_spriteRenderer != null)
            {
                _teamColor = _spriteRenderer.color;
            }
            _hasQuarterbackActions =
                Systems_RoleTable.HasQuarterbackActions(Systems_RoleTable.BrainOf(Role));

            // Field geometry is pure configuration with no mutable state, so a
            // local instance is equivalent to the injected singleton. Having one
            // guarantees CollectObservations always emits a full vector, even if
            // this agent was dropped into a scene with no lifetime scope.
            if (_field == null)
            {
                _field = new Systems_FieldModel();
            }

            ConfigureBody();
            ConfigureBrain();
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
            _rigidbody.mass = Systems_SimConstants.PLAYER_MASS;
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
            Systems_BrainGroup group = Systems_RoleTable.BrainOf(Role);

            BehaviorParameters behaviorParameters = GetComponent<BehaviorParameters>();
            behaviorParameters.BehaviorName = Systems_RoleTable.BehaviorNameOf(group);
            behaviorParameters.TeamId = (int)Side;
            behaviorParameters.BrainParameters.VectorObservationSize =
                Sensor_FootballState.OBSERVATION_SIZE;
            behaviorParameters.BrainParameters.NumStackedVectorObservations = 1;

            // Only the skill brain carries the quarterback's extra outputs. Giving
            // linemen and defenders four continuous actions and two discrete
            // branches would be dead dimensions they must learn to ignore.
            behaviorParameters.BrainParameters.ActionSpec = _hasQuarterbackActions
                ? new ActionSpec(4, new[] { Sensor_FootballState.PLAY_CALL_SLOTS, 2 })
                : ActionSpec.MakeContinuous(2);

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
                Role,
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
            float driveForce = drive * Systems_SimConstants.DRIVE_FORCE * fatigueScale;
            float steerTorque = steer * Systems_SimConstants.STEER_TORQUE * fatigueScale;

            _rigidbody.AddForce(_transform.up * driveForce);
            _rigidbody.AddTorque(steerTorque);

            if (_hasQuarterbackActions && IsQuarterback)
            {
                HandleQuarterback(actions);
            }

            AccumulateFatigue(driveForce, steerTorque);
            ClampSpeed();
            AwardProgress();
        }

        /// <summary>
        /// The quarterback commits to a call on its first decision after the snap
        /// and then executes it. The call is latched by the model, so anything sent
        /// on later steps is ignored.
        /// </summary>
        private void HandleQuarterback(ActionBuffers actions)
        {
            ActionSegment<int> discrete = actions.DiscreteActions;

            // Wait one full decision period before committing. EndEpisode clears
            // the stored action buffer to zeros and the director re-snaps in the
            // same tick, so the first OnActionReceived of a new play carries a
            // zeroed action rather than a policy decision — and zero maps to
            // KeepQuarterback. Latching on it made the quarterback call Keep on
            // 100% of plays in run football_base03, which read as a collapsed
            // policy but was really the buffer.
            if (!_play.CallIsLatched
                && _play.PhysicsTick >= Systems_SimConstants.DECISION_PERIOD)
            {
                _play.LatchCall((Systems_PlayCall)(discrete[0] + 1));
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
        /// </summary>
        private void AccumulateFatigue(float driveForce, float steerTorque)
        {
            float load = Mathf.Abs(driveForce) + Mathf.Abs(steerTorque);

            _fatigue += load
                * Systems_SimConstants.FATIGUE_GAIN_PER_NEWTON_SECOND
                * Time.fixedDeltaTime;
            _fatigue -= Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND * Time.fixedDeltaTime;
            _fatigue = Mathf.Clamp01(_fatigue);
        }

        /// <summary>Pileup-explosion guard (acceptance criterion #13).</summary>
        private void ClampSpeed()
        {
            Vector2 velocity = _rigidbody.linearVelocity;
            float maxSpeed = Systems_SimConstants.MAX_BODY_SPEED;

            if (velocity.sqrMagnitude > maxSpeed * maxSpeed)
            {
                _rigidbody.linearVelocity = velocity.normalized * maxSpeed;
            }
        }

        private void AwardProgress()
        {
            float ballY = _play.BallY;

            // First tick of a new episode: seed the baseline, do not pay out the
            // jump from the previous play's dead-ball spot to this play's snap.
            if (_lastSeenEpisode != _play.EpisodeIndex)
            {
                _lastSeenEpisode = _play.EpisodeIndex;
                _previousBallY = ballY;
                return;
            }

            float yardsDelta = (ballY - _previousBallY) / Systems_FieldModel.YARD;
            _previousBallY = ballY;

            AddReward(Reward_Progress.PerTick(Side, yardsDelta));
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

            if (other.Side == Side)
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

            if (other.Side == Side)
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
            float rotation = Side == Systems_TeamSide.Offense ? 0f : 180f;

            _transform.SetPositionAndRotation(
                new Vector3(position.x, position.y, 0f),
                Quaternion.Euler(0f, 0f, rotation));

            _rigidbody.position = position;
            _rigidbody.rotation = rotation;
            _rigidbody.linearVelocity = Vector2.zero;
            _rigidbody.angularVelocity = 0f;
        }

        public void ApplyTerminalReward(Systems_PlayOutcome outcome, float netYards)
        {
            AddReward(Reward_Terminal.For(Side, outcome, netYards));
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

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<float> continuousActions = actionsOut.ContinuousActions;
            for (int index = 0; index < continuousActions.Length; index++)
            {
                continuousActions[index] = 0f;
            }

            ActionSegment<int> discreteActions = actionsOut.DiscreteActions;
            for (int index = 0; index < discreteActions.Length; index++)
            {
                discreteActions[index] = 0;
            }
        }
    }
}
