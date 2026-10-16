using PoFootball.Models;
using PoFootball.Sensors;
using PoFootball.Systems;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Policies;
using Unity.MLAgents.Sensors;
using UnityEngine;

namespace PoFootball.Agents
{
    /// <summary>
    /// The defense's front, called by a brain of its own (revision 16).
    ///
    /// WHY IT IS NOT THE MIDDLE LINEBACKER ANY MORE. Revision 15 read the front
    /// off a second branch of the Defense brain, sampled from one of its eleven
    /// players. That brain's every other sample is a defender's steering, so the
    /// front got a sliver of the gradient and the entropy bonus tuned for
    /// steering: football_base17 called the zero blitz on 37% of plays at 1M
    /// steps, 87% at 1.3M and 100% by 2M, in every situation. It is the trap the
    /// quarterback's play call fell into while it shared OffenseSkill, and the
    /// same way out: a behavior of its own, here a coordinator with no body, one
    /// decision a play, and an exploration bonus of its own in the trainer config.
    ///
    /// ONE STEP A PLAY. It asks for its decision once the quarterback's kick
    /// window has closed (KICK_CALL_WINDOW_TICKS), so it knows whether a front is
    /// wanted at all — a kick lines up its special-teams units instead, and a
    /// sample on a kick would be reward the front never earned. The play's
    /// defensive terminal reward is its reward, and every play is an episode
    /// (Agent_TeamGroups pays it). Created at runtime by the middle linebacker's
    /// Agent_FootballPlayer, so no scene carries it.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class Agent_DefensiveCoordinator : Agent
    {
        private Systems_PlayModel _play;
        private Systems_FormationSelection _formations;
        private Systems_FieldModel _field;

        private readonly float[] _observation = new float[Sensor_DefenseCallState.OBSERVATION_SIZE];

        private string[] _statKeys;

        /// <summary>The play this coordinator last asked to call.</summary>
        private int _askedEpisode = -1;

        /// <summary>A front was called on this play and its reward is still owed.</summary>
        internal bool AwaitingReward { get; private set; }

        /// <summary>
        /// Builds the coordinator under <paramref name="parent"/>: BehaviorParameters
        /// first, on an inactive object, so the Agent reads a finished spec when it
        /// is enabled.
        /// </summary>
        internal static Agent_DefensiveCoordinator Create(
            Transform parent,
            Systems_PlayModel play,
            Systems_FormationSelection formations,
            Systems_FieldModel field)
        {
            GameObject host = new GameObject(nameof(Agent_DefensiveCoordinator));
            host.SetActive(false);
            host.transform.SetParent(parent, false);

            BehaviorParameters behaviorParameters = host.AddComponent<BehaviorParameters>();
            behaviorParameters.BehaviorName =
                Systems_RoleTable.BehaviorNameOf(Systems_BrainGroup.DefenseCall);
            behaviorParameters.TeamId = (int)Systems_TeamSide.Defense;
            behaviorParameters.BrainParameters.VectorObservationSize =
                Sensor_DefenseCallState.OBSERVATION_SIZE;
            behaviorParameters.BrainParameters.NumStackedVectorObservations = 1;
            behaviorParameters.BrainParameters.ActionSpec =
                Agent_ActionContract.For(Systems_BrainGroup.DefenseCall);
            behaviorParameters.Model = Agent_BrainRegistry.ModelFor(Systems_BrainGroup.DefenseCall);

            Agent_DefensiveCoordinator coordinator = host.AddComponent<Agent_DefensiveCoordinator>();
            coordinator._play = play;
            coordinator._formations = formations;
            coordinator._field = field ?? new Systems_FieldModel();

            host.SetActive(true);
            return coordinator;
        }

        public override void Initialize()
        {
            // Episodes end on the whistle, one decision each; never on a step count.
            MaxStep = 0;

            _statKeys = new string[Agent_ActionContract.DEFENSE_CALL_SLOTS];

            for (int front = 0; front < _statKeys.Length; front++)
            {
                _statKeys[front] = "DefenseCall/" + (Systems_DefensiveFormation)front;
            }
        }

        private void FixedUpdate()
        {
            if (_play == null
                || _play.Phase != Systems_PlayPhase.PreSnap
                || _askedEpisode == _play.EpisodeIndex
                || _play.PreSnapTick < Systems_SimConstants.KICK_CALL_WINDOW_TICKS)
            {
                return;
            }

            _askedEpisode = _play.EpisodeIndex;

            // Ordinary downs and two-point tries only: a kick, a kneel and a spike
            // have no front to call.
            if (_play.Kind != Systems_PlayKind.Scrimmage && _play.Kind != Systems_PlayKind.TwoPoint)
            {
                return;
            }

            RequestDecision();
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            if (_play == null || _formations == null)
            {
                sensor.AddObservation(new float[Sensor_DefenseCallState.OBSERVATION_SIZE]);
                return;
            }

            float yardsToGoal = _field.YardsToAttackingGoal(_play.LineOfScrimmageY);

            Sensor_DefenseCallState.Write(
                _observation,
                _play.Down,
                _play.YardsToGo,
                yardsToGoal,
                _play.Kind == Systems_PlayKind.TwoPoint,
                (int)_formations.Offense);

            sensor.AddObservation(_observation);
        }

        /// <summary>A front must be called: index 0, "no call", is masked.</summary>
        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            actionMask.SetActionEnabled(0, 0, false);
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            int branchIndex = actions.DiscreteActions[0];

            if (_play == null
                || branchIndex <= 0
                || branchIndex > Agent_ActionContract.DEFENSE_CALL_SLOTS
                || _play.Phase != Systems_PlayPhase.PreSnap)
            {
                return;
            }

            _play.CallDefense(branchIndex - 1);
            AwaitingReward = true;

            if (Academy.IsInitialized && Academy.Instance.IsCommunicatorOn && _statKeys != null)
            {
                StatsRecorder stats = Academy.Instance.StatsRecorder;

                for (int front = 0; front < _statKeys.Length; front++)
                {
                    stats.Add(_statKeys[front], front == branchIndex - 1 ? 1f : 0f);
                }
            }
        }

        /// <summary>
        /// The scripted coordinator calls the front that was drawn, so the scripted
        /// game is the one it was before anybody could call one.
        /// </summary>
        public override void Heuristic(in ActionBuffers actionsOut)
        {
            ActionSegment<int> discrete = actionsOut.DiscreteActions;
            discrete[0] = _formations == null ? 1 : (int)_formations.Defense + 1;
        }

        /// <summary>The play's defensive terminal reward, and the end of its one-step episode.</summary>
        internal void Settle(float reward)
        {
            if (!AwaitingReward)
            {
                return;
            }

            AwaitingReward = false;
            AddReward(reward);
            EndEpisode();
        }
    }
}
