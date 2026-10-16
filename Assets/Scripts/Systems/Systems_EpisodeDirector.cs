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
    /// It is then deferred FURTHER by Systems_ISpotProvider.DeadBallTicks — none in
    /// training, a couple of seconds in a game. That interval is the only thing
    /// standing between the whistle and the next snap, and without it the HUD's
    /// result banner went up and came down inside one physics step, so no viewer
    /// ever read the result of any play. The bodies simply stand still through it:
    /// the play is Dead, so no agent is driven and neither the referee nor the game
    /// clock does anything.
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
        private readonly Systems_FormationSelection _formations;
        private readonly Systems_Roster _roster;
        private readonly Systems_ITrainingEpisodeBoundary _episodeBoundary;
        private readonly Systems_PenaltySystem _penalties;
        private readonly IPublisher<Systems_PlaySnappedMessage> _snappedPublisher;
        private readonly ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;

        private IDisposable _subscription;
        private bool _resetPending;
        private bool _started;

        /// <summary>
        /// Ticks left of the dead-ball hold before the next snap. Zero except while
        /// a game is counting one down; training's provider always asks for none, so
        /// this never leaves zero there and the loop is the one it always was.
        /// </summary>
        private int _deadBallTicksRemaining;

        /// <summary>
        /// The front the defense called has been put on the field for this snap, or
        /// there was nothing to put. Cleared by BeginEpisode.
        /// </summary>
        private bool _defenseCallApplied;

        public Systems_EpisodeDirector(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_PlayerRegistry registry,
            Systems_Referee referee,
            Systems_ISpotProvider spotProvider,
            Systems_FormationSelection formations,
            Systems_Roster roster,
            Systems_ITrainingEpisodeBoundary episodeBoundary,
            Systems_PenaltySystem penalties,
            IPublisher<Systems_PlaySnappedMessage> snappedPublisher,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber)
        {
            _play = play;
            _ball = ball;
            _registry = registry;
            _referee = referee;
            _spotProvider = spotProvider;
            _formations = formations;
            _roster = roster;
            _episodeBoundary = episodeBoundary;
            _penalties = penalties;
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

            // Both formation invariants fail invisibly — a re-ordered role puts
            // every brain one seat out of place, and two slots inside
            // MIN_SPAWN_SEPARATION spawn players inside each other's collider and
            // fling one across the field, on one pairing out of sixty-four. Checked
            // once, here, in development builds only, where the result reaches the
            // log and the on-device DEBUG sheet alike. See Systems_FormationBook.
            if (Debug.isDebugBuild)
            {
                string formationFailures = Systems_FormationBook.Validate();

                if (formationFailures != null)
                {
                    Debug.LogError(
                        $"{nameof(Systems_FormationBook)}: " + formationFailures);
                }
            }

            Systems_Log.Info(
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
            _deadBallTicksRemaining = 0;
            BeginEpisode();
        }

        public void FixedTick()
        {
            if (!_started)
            {
                return;
            }

            if (_resetPending)
            {
                ResolveWhistle();
                return;
            }

            // A kick called before the snap: the kicking unit and the return or
            // block team take their places. Here rather than in the call, which
            // arrives from inside an agent's action callback.
            if (_play.Phase == Systems_PlayPhase.PreSnap && _play.KickFormationPending)
            {
                _play.ClearKickFormationRequest();
                _formations.UseFormationFor(_play.Kind);
                FormUp(_play.LineOfScrimmageY, false);
                return;
            }

            // Revision 15: the defense's front, the moment the middle linebacker
            // calls it. The snap does not wait for it — the pre-snap is
            // PRESNAP_TICKS whatever anybody decides — and a front called after a
            // kick was is moot, because the kick formation has replaced it.
            if (_play.Phase == Systems_PlayPhase.PreSnap)
            {
                ApplyCalledDefense();
                return;
            }

            if (_deadBallTicksRemaining > 0)
            {
                _deadBallTicksRemaining--;

                if (_deadBallTicksRemaining == 0)
                {
                    BeginEpisode();
                }
            }
        }

        /// <summary>
        /// The tick after the whistle: settle the episode that just ended, then
        /// either snap the next play or start the dead-ball hold before it.
        ///
        /// The reward is paid here, one tick after the whistle, exactly as it always
        /// was — a dead-ball pause must not change WHEN a policy is told how the
        /// play went, only how long the bodies then stand still. In training the
        /// hold is zero ticks and this method still ends with BeginEpisode, so the
        /// training loop is unchanged.
        /// </summary>
        private void ResolveWhistle()
        {
            _resetPending = false;

            // Read before anything is disturbed: BeginEpisode overwrites the play
            // model, and in training the reward for the play that just ended has to
            // be computed from how it ended.
            _episodeBoundary.EndEpisode(Systems_PlayResult.From(_play));

            // The contest may be over. The terminal reward above still had to be
            // paid — the last play of a game is as real as any other — but there is
            // nothing left to snap, so the bodies stay exactly where the final
            // whistle left them and this director never ticks again.
            if (!_spotProvider.HasNextPlay)
            {
                _started = false;

                // Park the bodies. Nothing is driving them any more — an agent
                // returns early from OnActionReceived unless the play is Live — but
                // whatever speed they carried into the whistle keeps bleeding off
                // against linear damping for a second or two afterwards, and the
                // final overlay goes up over twenty-two players still drifting
                // about. The game is over; they should look like it.
                for (int slotIndex = 0;
                    slotIndex < Systems_PlayerRegistry.CAPACITY;
                    slotIndex++)
                {
                    _registry.Get(slotIndex).Freeze();
                }

                return;
            }

            _deadBallTicksRemaining = _spotProvider.DeadBallTicks;

            if (_deadBallTicksRemaining <= 0)
            {
                _deadBallTicksRemaining = 0;
                BeginEpisode();
            }
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            _resetPending = true;
        }

        private void BeginEpisode()
        {
            // Where the next snap comes from is the ONE thing that differs
            // between training and a played game, and it is entirely behind this
            // call. Systems_RandomSpotProvider draws it from a seeded stream;
            // Systems_GameFlowSystem returns whatever the chains say instead.
            Systems_PlaySituation situation = _spotProvider.NextSituation();
            float lineOfScrimmageY = situation.LineOfScrimmageY;

            // Both teams pick an alignment for this snap, from a stream of their
            // own, and do so on EVERY snap — a kick included — so the formation
            // sequence is the same however the plays fall. A kicking snap then lines
            // its special-teams units up over the top of it.
            _formations.DrawNext();
            _formations.UseFormationFor(situation.Kind);
            _penalties.BeginPlay();

            // Who is on the field. A new twenty-two in training, the same ones all
            // game otherwise; applied by ResetTo in FormUp. See Systems_Roster.
            _roster.NextPlay();
            _defenseCallApplied = false;

            FormUp(lineOfScrimmageY, true);

            Systems_IPlayerHandle quarterback =
                _registry.Get(Systems_Formation.QUARTERBACK_SLOT_INDEX);

            _referee.ResetContactTracking();

            _play.BeginEpisode(
                lineOfScrimmageY, quarterback.Position.y,
                situation.Down, situation.YardsToGo,
                situation.Kind, situation.OnsideKick);

            // NOT SNAPPED HERE ANY MORE (revision 14). The teams stand set for
            // Systems_SimConstants.PRESNAP_TICKS first — that is when a kick can be
            // called and when somebody can jump — and Systems_Referee snaps it.
            _snappedPublisher.Publish(
                new Systems_PlaySnappedMessage(lineOfScrimmageY, _play.EpisodeIndex));
        }

        /// <summary>
        /// Puts all twenty-two bodies in the current alignment at this line and the
        /// ball in the quarterback's hands. <paramref name="restFirst"/> is false when
        /// re-forming for a kick before the same snap: that is not a new play, and
        /// fatigue is not rested twice.
        /// </summary>
        private void FormUp(float lineOfScrimmageY, bool restFirst)
        {
            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_FormationSlot slot = _formations.GetSlot(slotIndex);
                Systems_IPlayerHandle player = _registry.Get(slotIndex);

                // Fatigue is settled BEFORE the body is restored, per CLAUDE.md
                // section 2 (acceptance criterion #11). Rested rather than
                // cleared since revision 12: see FATIGUE_CARRIED_BETWEEN_PLAYS.
                if (restFirst)
                {
                    player.RestBetweenPlays();
                }

                player.ResetTo(ClampToField(
                    new Vector2(slot.OffsetX, lineOfScrimmageY + slot.OffsetY)));
                player.SetCarrier(false);
            }

            // The quarterback takes every snap — the punter and the kicker are him.
            Systems_IPlayerHandle quarterback =
                _registry.Get(Systems_Formation.QUARTERBACK_SLOT_INDEX);
            quarterback.SetCarrier(true);

            _ball.ResetForSnap(Systems_Formation.QUARTERBACK_SLOT_INDEX, quarterback.Position);
        }

        /// <summary>
        /// Keeps a body on the field. A punt returner forty yards deep off a snap at
        /// the opponent's thirty would otherwise line up behind the end zone.
        /// </summary>
        private static Vector2 ClampToField(Vector2 position)
        {
            const float INSIDE = 1f;

            return new Vector2(
                Mathf.Clamp(
                    position.x,
                    -Systems_FieldModel.HALF_WIDTH + INSIDE,
                    Systems_FieldModel.HALF_WIDTH - INSIDE),
                Mathf.Clamp(
                    position.y,
                    Systems_FieldModel.OWN_BACK_LINE_Y + INSIDE,
                    Systems_FieldModel.ATTACKING_BACK_LINE_Y - INSIDE));
        }

        /// <summary>
        /// Lines the defense up in the front it called, if it called one different
        /// from the draw. Once per snap, and not on a kick, whose units are the
        /// special-teams tables rather than any front.
        /// </summary>
        private void ApplyCalledDefense()
        {
            if (_defenseCallApplied || !_play.DefenseIsCalled || _play.Kind.IsKick())
            {
                return;
            }

            _defenseCallApplied = true;

            if (_play.DefenseCallIndex == (int)_formations.Defense
                || _play.DefenseCallIndex >= Systems_FormationBook.DefensiveFormationCount)
            {
                return;
            }

            _formations.CallDefense((Systems_DefensiveFormation)_play.DefenseCallIndex);

            for (int slotIndex = Systems_FormationBook.SLOTS_PER_SIDE;
                slotIndex < Systems_PlayerRegistry.CAPACITY;
                slotIndex++)
            {
                Systems_FormationSlot slot = _formations.GetSlot(slotIndex);

                _registry.Get(slotIndex).ResetTo(ClampToField(
                    new Vector2(slot.OffsetX, _play.LineOfScrimmageY + slot.OffsetY)));
            }
        }

        public void Dispose()
        {
            // Null when the scope is torn down before Start ran. Plain IDisposable,
            // not a UnityEngine.Object, so ?. is safe here.
            _subscription?.Dispose();
        }
    }
}
