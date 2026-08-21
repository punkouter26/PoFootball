using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Decides when a play is over. Several independent paths reach a whistle:
    ///
    ///   - the ball itself, via Systems_BallSystem: an interception or an
    ///     incompletion ends the play the moment it resolves;
    ///   - boundary and clock conditions, polled once per FixedUpdate;
    ///   - a tackle, reported by the carrier the instant a collision occurs.
    ///
    /// The tackle path is event-driven rather than polled, so a hit is registered
    /// on the exact physics tick it happens (acceptance criterion #2). The polled
    /// path reads positions produced by the previous physics step, which is a
    /// deterministic one-tick lag and well inside the same tolerance.
    /// </summary>
    public sealed class Systems_Referee : IFixedTickable
    {
        private readonly Systems_PlayModel _play;
        private readonly Systems_BallModel _ball;
        private readonly Systems_BallSystem _ballSystem;
        private readonly Systems_FieldModel _field;
        private readonly Systems_PlayerRegistry _registry;
        private readonly IPublisher<Systems_PlayEndedMessage> _endedPublisher;
        private readonly IPublisher<Systems_TackleMessage> _tacklePublisher;
        private readonly IPublisher<Systems_ScoreMessage> _scorePublisher;

        private int _lastContactTick = -1;
        private int _contactRunTicks;

        public Systems_Referee(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_BallSystem ballSystem,
            Systems_FieldModel field,
            Systems_PlayerRegistry registry,
            IPublisher<Systems_PlayEndedMessage> endedPublisher,
            IPublisher<Systems_TackleMessage> tacklePublisher,
            IPublisher<Systems_ScoreMessage> scorePublisher)
        {
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _field = field;
            _registry = registry;
            _endedPublisher = endedPublisher;
            _tacklePublisher = tacklePublisher;
            _scorePublisher = scorePublisher;
        }

        /// <summary>Called by Systems_EpisodeDirector as each episode begins.</summary>
        public void ResetContactTracking()
        {
            _lastContactTick = -1;
            _contactRunTicks = 0;
        }

        public void FixedTick()
        {
            if (_play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            _play.AdvanceTick();

            // A kick is resolved by the rules, not simulated. There is no kicking
            // model here — the same reason the extra point is awarded rather than
            // played — so the down is over the instant the quarterback commits to
            // one, and Systems_GameFlowSystem works out where the ball goes from
            // the line of scrimmage. Spotting it at the line rather than at the
            // carrier keeps net yards at zero, which is what a kick actually gains
            // the offense.
            if (_play.CallIsLatched && IsKick(_play.Call))
            {
                EndPlay(
                    KickOutcome(_play.Call),
                    new Vector2(0f, _play.LineOfScrimmageY));
                return;
            }

            // The ball resolves first: a catch, an interception or an incompletion
            // must be settled before boundaries are judged against its new position.
            Systems_PlayOutcome ballOutcome = _ballSystem.Tick();
            _play.SetBallY(_ball.Position.y);

            if (ballOutcome != Systems_PlayOutcome.None)
            {
                EndPlay(ballOutcome, _ball.Position);
                return;
            }

            if (_ball.IsInFlight)
            {
                // A pass that sails out of the field of play is incomplete; it
                // cannot score and nobody can catch it.
                if (_field.IsOutsideSidelines(_ball.Position.x)
                    || _ball.Position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y
                    || _ball.Position.y <= Systems_FieldModel.OWN_BACK_LINE_Y)
                {
                    EndPlay(Systems_PlayOutcome.Incompletion, _ball.Position);
                }

                return;
            }

            EvaluateCarrier();
        }

        private static bool IsKick(Systems_PlayCall call)
        {
            return call == Systems_PlayCall.Punt || call == Systems_PlayCall.FieldGoal;
        }

        /// <summary>
        /// A punt is always a punt. A field goal is good or short purely as a
        /// function of its length, so the offense owns the outcome completely —
        /// see Systems_GameRules.FIELD_GOAL_MAX_YARDS for why that is deterministic.
        /// </summary>
        private Systems_PlayOutcome KickOutcome(Systems_PlayCall call)
        {
            if (call == Systems_PlayCall.Punt)
            {
                return Systems_PlayOutcome.Punt;
            }

            float yardsToGoalLine =
                (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - _play.LineOfScrimmageY)
                / Systems_FieldModel.YARD;

            float attempt = yardsToGoalLine + Systems_GameRules.FIELD_GOAL_SNAP_YARDS;

            return attempt <= Systems_GameRules.FIELD_GOAL_MAX_YARDS
                ? Systems_PlayOutcome.FieldGoalGood
                : Systems_PlayOutcome.FieldGoalMissed;
        }

        private void EvaluateCarrier()
        {
            Systems_IPlayerHandle carrier = CurrentCarrier();
            if (carrier == null)
            {
                return;
            }

            Vector2 position = carrier.Position;

            if (_field.HasScored(position.y))
            {
                _scorePublisher.Publish(new Systems_ScoreMessage(carrier.Id, _play.NetYards));
                EndPlay(Systems_PlayOutcome.Touchdown, position);
                return;
            }

            if (_field.IsOutsideSidelines(position.x)
                || _field.HasExitedOwnEndZone(position.y))
            {
                EndPlay(Systems_PlayOutcome.OutOfBounds, position);
                return;
            }

            if (_play.PhysicsTick >= Systems_PlayModel.MAX_PHYSICS_TICKS)
            {
                EndPlay(Systems_PlayOutcome.TimeExpired, position);
            }
        }

        public Systems_IPlayerHandle CurrentCarrier()
        {
            return _ball.IsHeld ? _registry.Get(_ball.CarrierId) : null;
        }

        /// <summary>
        /// Called by the carrier when it collides with an opponent. A contact only
        /// ends the play if the closing speed clears the threshold — a defender
        /// drifting into the carrier is just a collision (acceptance criterion #3).
        /// </summary>
        public void ReportContactWithCarrier(int tacklerId, float closingSpeed)
        {
            if (!CanBeTackled())
            {
                return;
            }

            if (closingSpeed <= Systems_SimConstants.TACKLE_CLOSING_SPEED)
            {
                return;
            }

            CompleteTackle(tacklerId, closingSpeed);
        }

        /// <summary>
        /// Called by the carrier on every physics tick it remains in contact with
        /// an opponent. Contact sustained for SUSTAINED_TACKLE_TICKS consecutive
        /// ticks ends the play whatever the closing speed — this is the wrap-up
        /// tackle, and it is what makes a pursuit from behind possible at all.
        ///
        /// Several defenders touching the carrier on the same tick each call in;
        /// the tick guard counts that as one tick of contact, not several.
        /// </summary>
        public void ReportSustainedContact(int tacklerId, float closingSpeed)
        {
            if (!CanBeTackled())
            {
                return;
            }

            int tick = _play.PhysicsTick;

            if (tick == _lastContactTick)
            {
                return;
            }

            _contactRunTicks = tick == _lastContactTick + 1 ? _contactRunTicks + 1 : 1;
            _lastContactTick = tick;

            if (_contactRunTicks < Systems_SimConstants.SUSTAINED_TACKLE_TICKS)
            {
                return;
            }

            CompleteTackle(tacklerId, closingSpeed);
        }

        /// <summary>A ball in the air cannot be tackled — there is nobody holding it.</summary>
        private bool CanBeTackled()
        {
            return _play.Phase == Systems_PlayPhase.Live && _ball.IsHeld;
        }

        private void CompleteTackle(int tacklerId, float closingSpeed)
        {
            Systems_IPlayerHandle carrier = CurrentCarrier();
            if (carrier == null)
            {
                return;
            }

            Vector2 spot = carrier.Position;
            _play.SetBallY(spot.y);

            _tacklePublisher.Publish(
                new Systems_TackleMessage(tacklerId, carrier.Id, closingSpeed, _play.NetYards));

            // Down on or behind the offense's own goal line is a safety, not a
            // tackle. Systems_GameFlowSystem derives the same thing independently
            // from the spot when it awards the two points, so this changes no rule —
            // it makes the outcome say what happened, which is what lets
            // Reward_Terminal price it as the disaster it is.
            EndPlay(
                spot.y <= Systems_FieldModel.OWN_GOAL_LINE_Y
                    ? Systems_PlayOutcome.Safety
                    : Systems_PlayOutcome.Tackle,
                spot);
        }

        private void EndPlay(Systems_PlayOutcome outcome, Vector2 spot)
        {
            Vector2 clampedSpot = _field.ClampToField(spot);
            int ticks = _play.PhysicsTick;
            float netYards = _play.NetYards;

            _play.EndPlay(outcome, clampedSpot);

            _endedPublisher.Publish(
                new Systems_PlayEndedMessage(
                    outcome, _play.Call, clampedSpot, netYards, ticks, _play.PassCompleted));
        }
    }
}
