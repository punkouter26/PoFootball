using PoFootball.Models;
using PoFootball.Rewards;
using PoFootball.Systems;
using Unity.MLAgents;
using UnityEngine;
using VContainer;

namespace PoFootball.Agents
{
    /// <summary>
    /// Owns the MA-POCA multi-agent groups and routes each play's team outcome into
    /// them as a group reward.
    ///
    /// WHAT POCA ACTUALLY NEEDS, AND WHY THE SHAPE HERE IS FORCED. A
    /// SimpleMultiAgentGroup cannot span ML-Agents behavior names: the trainer
    /// builds one AgentManager per behavior and keeps its groupmate tables as
    /// instance state, so agents grouped across two behaviors never appear in each
    /// other's trajectories. The C# side will let you do it and say nothing. That is
    /// why Systems_BrainGroup collapsed to three, and why there are exactly three
    /// groups here — one per behavior, not one per side.
    ///
    /// The quarterback is a group of one. That is not a mistake and it is not a
    /// no-op: it keeps the terminal reward flowing through the same group path as
    /// everyone else, while leaving the quarterback its own policy, its own entropy
    /// bonus and its own play-call gradient — the reason it was split out of the
    /// skill brain to begin with. A centralized critic over a single agent is just
    /// an ordinary critic, which is exactly what it should be.
    ///
    /// An offensive play therefore pays TWO groups: the ten linemen and receivers,
    /// and the quarterback. Both are on offense and both earned the outcome.
    ///
    /// Sits on /Systems in SCN_TRAIN_FOOTBALL only, beside Agent_Telemetry, and for
    /// the same reason — a played game has no trainer to report to and no groups to
    /// keep.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_TeamGroups : MonoBehaviour, Systems_IInjectableBehaviour
    {
        private Systems_PlayerRegistry _registry;
        private Systems_ITrainingEpisodeBoundary _boundary;

        private SimpleMultiAgentGroup _offense;
        private SimpleMultiAgentGroup _defense;
        private SimpleMultiAgentGroup _quarterback;

        private bool _registered;

        [Inject]
        public void Construct(
            Systems_PlayerRegistry registry, Systems_ITrainingEpisodeBoundary boundary)
        {
            _registry = registry;
            _boundary = boundary;
        }

        private void Start()
        {
            // Only a real training boundary has groups to install into. In a played
            // game this is Systems_NullEpisodeBoundary and there is nothing to do.
            if (!(_boundary is Systems_TrainingEpisodeBoundary training))
            {
                enabled = false;
                return;
            }

            _offense = new SimpleMultiAgentGroup();
            _defense = new SimpleMultiAgentGroup();
            _quarterback = new SimpleMultiAgentGroup();

            RegisterAgents();
            training.SetTeamRewardSink(new Sink(this));
        }

        private void OnDestroy()
        {
            _offense?.Dispose();
            _defense?.Dispose();
            _quarterback?.Dispose();
        }

        /// <summary>
        /// Registered once, from the registry rather than a scene scan, so the
        /// grouping is the same twenty-two slots the formation and the referee use.
        /// Agents are never unregistered mid-run: the roster is fixed for the life
        /// of the scene and SimpleMultiAgentGroup already drops an agent that gets
        /// disabled.
        /// </summary>
        private void RegisterAgents()
        {
            if (_registered)
            {
                return;
            }

            int registered = 0;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                if (!(_registry.Get(slotIndex) is Agent_FootballPlayer player))
                {
                    continue;
                }

                GroupFor(Systems_RoleTable.BrainOf(player.Role))?.RegisterAgent(player);
                registered++;
            }

            _registered = true;
            Debug.Log($"[PoFootball] MA-POCA groups registered {registered} agents.");
        }

        private SimpleMultiAgentGroup GroupFor(Systems_BrainGroup group)
        {
            switch (group)
            {
                case Systems_BrainGroup.Offense:
                    return _offense;
                case Systems_BrainGroup.Defense:
                    return _defense;
                default:
                    return _quarterback;
            }
        }

        private void AddTeamTerminal(
            Systems_TeamSide side,
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted)
        {
            if (!_registered)
            {
                return;
            }

            float reward = Reward_Terminal.For(side, outcome, netYards, passCompleted);

            if (side == Systems_TeamSide.Offense)
            {
                // Both offensive groups. AddGroupReward fans out across each group's
                // own members, so this is one call per GROUP — calling it per player
                // would pay everyone ten or eleven times over.
                _offense.AddGroupReward(reward);
                _quarterback.AddGroupReward(reward);
                return;
            }

            _defense.AddGroupReward(reward);
        }

        /// <summary>
        /// A tiny adapter rather than implementing the interface on the MonoBehaviour
        /// directly, so the boundary holds a plain object it can keep across a scene
        /// reload without a Unity fake-null reference pretending to be alive.
        /// </summary>
        private sealed class Sink : Systems_ITeamRewardSink
        {
            private readonly Agent_TeamGroups _owner;

            public Sink(Agent_TeamGroups owner)
            {
                _owner = owner;
            }

            public void AddTeamTerminal(
                Systems_TeamSide side,
                Systems_PlayOutcome outcome,
                float netYards,
                bool passCompleted)
            {
                if (_owner == null)
                {
                    return;
                }

                _owner.AddTeamTerminal(side, outcome, netYards, passCompleted);
            }
        }
    }
}
