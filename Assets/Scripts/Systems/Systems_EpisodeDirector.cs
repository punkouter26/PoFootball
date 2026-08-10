using System;
using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer.Unity;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// Owns the episode boundary: assigns terminal rewards, ends every agent's
    /// episode together, re-forms the 22 players at a fresh line of scrimmage, puts
    /// the ball in the quarterback's hands, and snaps the next play.
    ///
    /// The reset is DEFERRED to the next FixedTick rather than run inline when the
    /// whistle fires. A tackle is reported from inside OnCollisionEnter2D, and
    /// teleporting 22 rigidbodies from within a physics callback is undefined
    /// behaviour in Unity. Deferring costs one physics tick and makes the reset
    /// deterministic.
    ///
    /// Agents' OnEpisodeBegin is deliberately empty — all repositioning happens
    /// here, so 22 agents cannot half-reset each other in an arbitrary order.
    /// </summary>
    public sealed class Systems_EpisodeDirector : IStartable, IFixedTickable, IDisposable
    {
        private readonly Systems_PlayModel _play;
        private readonly Systems_BallModel _ball;
        private readonly Systems_FieldModel _field;
        private readonly Systems_PlayerRegistry _registry;
        private readonly Systems_Referee _referee;
        private readonly IPublisher<Systems_PlaySnappedMessage> _snappedPublisher;
        private readonly ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;

        private IDisposable _subscription;
        private Random _rng;
        private bool _resetPending;
        private bool _started;

        public Systems_EpisodeDirector(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_FieldModel field,
            Systems_PlayerRegistry registry,
            Systems_Referee referee,
            IPublisher<Systems_PlaySnappedMessage> snappedPublisher,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber)
        {
            _play = play;
            _ball = ball;
            _field = field;
            _registry = registry;
            _referee = referee;
            _snappedPublisher = snappedPublisher;
            _endedSubscriber = endedSubscriber;
            _rng = new Random(Systems_EpisodeSeed.Value);
        }

        public void Start()
        {
            // Subscribed here rather than in the constructor. The constructor runs
            // during container build, so a handler registered there is live before
            // the rest of the graph exists and before _started is set — OnPlayEnded
            // would arm a reset for a director that has not begun an episode.
            // Systems_Telemetry does the same.
            _subscription = _endedSubscriber.Subscribe(OnPlayEnded);

            Debug.Log(
                $"[PoFootball] EpisodeDirector.Start — registry has "
                + $"{_registry.RegisteredCount}/{Systems_PlayerRegistry.CAPACITY} slots.");

            if (!_registry.IsComplete)
            {
                Debug.LogError(
                    $"{nameof(Systems_EpisodeDirector)}: only {_registry.RegisteredCount} of "
                    + $"{Systems_PlayerRegistry.CAPACITY} formation slots are filled. Every "
                    + "Agent_FootballPlayer needs a unique formation slot index.");
                return;
            }

            _started = true;
            BeginEpisode();
        }

        public void FixedTick()
        {
            if (!_started || !_resetPending)
            {
                return;
            }

            _resetPending = false;

            ApplyTerminalRewards();
            EndAllEpisodes();
            BeginEpisode();
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            _resetPending = true;
        }

        private void ApplyTerminalRewards()
        {
            float netYards = _play.NetYards;
            Systems_PlayOutcome outcome = _play.Outcome;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                _registry.Get(slotIndex).ApplyTerminalReward(outcome, netYards);
            }
        }

        private void EndAllEpisodes()
        {
            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                _registry.Get(slotIndex).EndEpisodeNow();
            }
        }

        private void BeginEpisode()
        {
            float lineOfScrimmageY = _rng.NextFloat(
                Systems_FieldModel.LOS_MIN_Y, Systems_FieldModel.LOS_MAX_Y);

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_FormationSlot slot = Systems_Formation.GetSlot(slotIndex);
                Systems_IPlayerHandle player = _registry.Get(slotIndex);

                // Fatigue is cleared BEFORE the body is restored, per CLAUDE.md
                // section 2 (acceptance criterion #11).
                player.ClearFatigue();
                player.ResetTo(new Vector2(slot.OffsetX, lineOfScrimmageY + slot.OffsetY));
                player.SetCarrier(false);
            }

            // The quarterback takes every snap.
            Systems_IPlayerHandle quarterback =
                _registry.Get(Systems_Formation.QUARTERBACK_SLOT_INDEX);
            quarterback.SetCarrier(true);

            _ball.AttachTo(Systems_Formation.QUARTERBACK_SLOT_INDEX, quarterback.Position);
            _referee.ResetContactTracking();

            _play.BeginEpisode(lineOfScrimmageY, quarterback.Position.y);
            _play.Snap();

            _snappedPublisher.Publish(
                new Systems_PlaySnappedMessage(lineOfScrimmageY, _play.EpisodeIndex));
        }

        public void Dispose()
        {
            // Null when the scope is torn down before Start ran. Plain IDisposable,
            // not a UnityEngine.Object, so ?. is safe here.
            _subscription?.Dispose();
        }
    }
}
