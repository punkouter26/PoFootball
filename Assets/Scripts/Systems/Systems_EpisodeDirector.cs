using System;
using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Re-forms the 22 players at a fresh line of scrimmage, puts the ball in the
    /// quarterback's hands, and snaps the next play.
    ///
    /// The reset is DEFERRED to the next FixedTick rather than run inline when the
    /// whistle fires. A tackle is reported from inside OnCollisionEnter2D, and
    /// teleporting 22 rigidbodies from within a physics callback is undefined
    /// behaviour in Unity. Deferring costs one physics tick and makes the reset
    /// deterministic.
    ///
    /// Agents' OnEpisodeBegin is deliberately empty — all repositioning happens
    /// here, so 22 agents cannot half-reset each other in an arbitrary order.
    ///
    /// NOTHING HERE IS SPECIFIC TO LEARNING. This class used to assign terminal
    /// rewards and end every agent's ML-Agents episode itself, on every play, in
    /// both modes. Those two steps are now behind
    /// <see cref="Systems_ITrainingEpisodeBoundary"/> and are a no-op during a
    /// played game. What is left is the part a game and a training run genuinely
    /// share: put the bodies back and snap the ball.
    /// </summary>
    public sealed class Systems_EpisodeDirector : IStartable, IFixedTickable, IDisposable
    {
        private readonly Systems_PlayModel _play;
        private readonly Systems_BallModel _ball;
        private readonly Systems_PlayerRegistry _registry;
        private readonly Systems_Referee _referee;
        private readonly Systems_ISpotProvider _spotProvider;
        private readonly Systems_ITrainingEpisodeBoundary _episodeBoundary;
        private readonly IPublisher<Systems_PlaySnappedMessage> _snappedPublisher;
        private readonly ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;

        private IDisposable _subscription;
        private bool _resetPending;
        private bool _started;

        public Systems_EpisodeDirector(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_PlayerRegistry registry,
            Systems_Referee referee,
            Systems_ISpotProvider spotProvider,
            Systems_ITrainingEpisodeBoundary episodeBoundary,
            IPublisher<Systems_PlaySnappedMessage> snappedPublisher,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber)
        {
            _play = play;
            _ball = ball;
            _registry = registry;
            _referee = referee;
            _spotProvider = spotProvider;
            _episodeBoundary = episodeBoundary;
            _snappedPublisher = snappedPublisher;
            _endedSubscriber = endedSubscriber;
        }

        public void Start()
        {
            // Subscribed here rather than in the constructor. The constructor runs
            // during container build, so a handler registered there is live before
            // the rest of the graph exists and before _started is set — OnPlayEnded
            // would arm a reset for a director that has not begun an episode.
            // Agent_Telemetry does the same.
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

            // Read before anything is disturbed: BeginEpisode overwrites the play
            // model, and in training the reward for the play that just ended has to
            // be computed from how it ended.
            _episodeBoundary.EndEpisode(
                _play.Outcome, _play.NetYards, _play.PassCompleted);

            // The contest may be over. The terminal reward above still had to be
            // paid — the last play of a game is as real as any other — but there is
            // nothing left to snap, so the bodies stay exactly where the final
            // whistle left them and this director never ticks again.
            if (!_spotProvider.HasNextPlay)
            {
                _started = false;
                return;
            }

            BeginEpisode();
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            _resetPending = true;
        }

        private void BeginEpisode()
        {
            // Where the next snap comes from is the ONE thing that differs
            // between training and a played game, and it is entirely behind this
            // call. Systems_RandomSpotProvider reproduces the seeded draw this
            // method used to make inline, so a training run is unchanged;
            // Systems_GameFlowSystem returns whatever the chains say instead.
            float lineOfScrimmageY = _spotProvider.NextLineOfScrimmageY();

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
