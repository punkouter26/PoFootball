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
        private readonly Systems_IKickModel _kickModel;
        private readonly Systems_IFumbleModel _fumbleModel;
        private readonly IPublisher<Systems_PlayEndedMessage> _endedPublisher;
        private readonly IPublisher<Systems_TackleMessage> _tacklePublisher;
        private readonly IPublisher<Systems_ScoreMessage> _scorePublisher;

        private int _lastContactTick = -1;

        /// <summary>
        /// Last tick the ball carrier was moving at more than STALL_SPEED. The
        /// dead-ball backstop measures the gap between this and now, so a play only
        /// expires once the ball has actually stopped — see EvaluateCarrier.
        /// </summary>
        private int _lastCarrierMovingTick;
        private int _contactRunTicks;

        /// <summary>
        /// Formation slots of every opponent in contact with the carrier on
        /// <see cref="_lastContactTick"/>, one bit per slot. A bitmask rather than a
        /// collection because this is written several times per physics tick and
        /// must not allocate.
        /// </summary>
        private uint _contactMask;

        public Systems_Referee(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_BallSystem ballSystem,
            Systems_FieldModel field,
            Systems_PlayerRegistry registry,
            Systems_IKickModel kickModel,
            Systems_IFumbleModel fumbleModel,
            IPublisher<Systems_PlayEndedMessage> endedPublisher,
            IPublisher<Systems_TackleMessage> tacklePublisher,
            IPublisher<Systems_ScoreMessage> scorePublisher)
        {
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _field = field;
            _registry = registry;
            _kickModel = kickModel;
            _fumbleModel = fumbleModel;
            _endedPublisher = endedPublisher;
            _tacklePublisher = tacklePublisher;
            _scorePublisher = scorePublisher;
        }

        /// <summary>Called by Systems_EpisodeDirector as each episode begins.</summary>
        public void ResetContactTracking()
        {
            _lastContactTick = -1;
            _lastCarrierMovingTick = 0;
            _contactRunTicks = 0;
            _contactMask = 0u;
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
        /// A punt is always a punt. Whether a field goal goes in is the kick model's
        /// call — see Systems_IKickModel for why that stopped being a bare
        /// comparison against Systems_GameRules.FIELD_GOAL_MAX_YARDS. In Training the
        /// model is the deterministic one and this computes exactly what it used to.
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

            return _kickModel.IsFieldGoalGood(attempt)
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

            // A CARRIER NOBODY HAS TACKLED IS A LIVE PLAY, AND THE CLOCK MUST NOT
            // TAKE HIM DOWN.
            //
            // This used to be a flat `PhysicsTick >= MAX_PHYSICS_TICKS`, which ended
            // the down on a fixed timer regardless of what was happening on the
            // field. Watching a game on a phone, that reads exactly as reported: a
            // back breaks into open field, nobody is near him, and the whistle goes
            // for no visible reason. Systems_PlayModel's own comments had already
            // circled this three times — 330 ticks clipped 34 of 80 plays, 500
            // clipped 28 of 74, 600 was the third guess — and reached the right
            // conclusion without acting on it: "the cap is a backstop, not a balance
            // lever" and "getting plays to actually END is tackling's job."
            //
            // So the backstop now measures the thing it was always pretending to
            // measure: whether the play has STOPPED, not how long it has lasted. A
            // carrier moving at a real pace keeps the down alive indefinitely; only
            // once the ball has been effectively stationary for STALL_TICKS does the
            // whistle go, which is a genuinely dead ball — a pile that is not moving,
            // or a quarterback standing behind the line with nobody open.
            //
            // SPEED, NOT FORWARD PROGRESS, is the test on purpose. A quarterback
            // scrambling backwards out of a collapsing pocket and a back bouncing a
            // run outside are both live football and neither gains a yard while it
            // is happening; ending those on a progress rule would be the same defect
            // wearing a better disguise.
            // A QUARTERBACK STANDING IN THE POCKET IS NOT A DEAD BALL. He is under
            // STALL_SPEED by definition — scanning is standing still — and without
            // this the whistle would go at 1.2 s while the throw is legal until
            // THROW_WINDOW_TICKS, which is 5 s. That would not have looked like a
            // fix; it would have replaced "the play ends for no reason" with "the
            // quarterback can never hold the ball", and the pass game with it.
            //
            // Holding the timer at the current tick rather than skipping the check
            // means the stall clock starts when the window CLOSES, so a quarterback
            // who stood in the pocket and then breaks contain at tick 255 still gets
            // his full STALL_TICKS to start running.
            bool passStillLegal =
                carrier.Id == Systems_Formation.QUARTERBACK_SLOT_INDEX
                && _play.PhysicsTick <= Systems_SimConstants.THROW_WINDOW_TICKS;

            if (passStillLegal
                || carrier.Velocity.sqrMagnitude
                    >= Systems_SimConstants.STALL_SPEED * Systems_SimConstants.STALL_SPEED)
            {
                _lastCarrierMovingTick = _play.PhysicsTick;
            }

            if (_play.PhysicsTick - _lastCarrierMovingTick
                >= Systems_SimConstants.STALL_TICKS)
            {
                EndPlay(Systems_PlayOutcome.TimeExpired, position);
                return;
            }

            // The absolute ceiling, which exists for the trainer rather than for
            // football: an episode that never terminates hangs a rollout, and
            // SCN_TRAIN_FOOTBALL has to be able to reach the do-nothing basin and
            // climb back out of it without stalling a sweep. It is now reached only
            // by a play that has genuinely run twelve seconds rather than by every
            // ordinary down — but it stays at 600, because it also bounds the ratio
            // between per-tick reward shaping and the terminal reward, and it is
            // paired with time_horizon in every trainer config. See
            // Systems_PlayModel.MAX_PHYSICS_TICKS for what raising it broke.
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
        /// an opponent. Contact sustained for the carrier's own tick count — see
        /// Systems_RoleTable.TackleTicksOf — ends the play whatever the closing
        /// speed. This is the wrap-up tackle, it is what makes a pursuit from behind
        /// possible at all, and since TACKLE_CLOSING_SPEED was raised to mean a real
        /// collision it is now the ordinary way a down ends rather than the fallback.
        ///
        /// TWO THINGS HERE USED TO MAKE THE CARRIER FAR TOO HARD TO STOP, and
        /// together they are most of why a measured game averaged 9.3 yards a play
        /// against real football's 5.5.
        ///
        /// A SECOND TACKLER COUNTED FOR NOTHING. Every defender in contact called in
        /// on the same tick and the tick guard collapsed them all into one tick of
        /// contact, so three men wrapping up a back was worth exactly as much as
        /// one. In real football the second defender is the whole point — a runner
        /// breaks an arm tackle and is stopped dead by the man arriving behind it.
        /// Contact is now counted per DISTINCT TACKLER (<see cref="_contactMask"/>)
        /// and the wrap-up they owe is divided between them, so a gang tackle ends
        /// the play in a fraction of the time one defender needs.
        ///
        /// AND CONTACT HAD TO BE STRICTLY CONSECUTIVE. Two discs colliding push each
        /// other apart, so a defender who landed a hit bounced off, missed a tick,
        /// and the run counter reset to one — the carrier shrugged off a tackle that
        /// had actually been made. A short grace window
        /// (Systems_SimConstants.CONTACT_GRACE_TICKS) keeps the wrap alive across
        /// the separation the collision impulse itself causes.
        /// </summary>
        public void ReportSustainedContact(int tacklerId, float closingSpeed)
        {
            if (!CanBeTackled())
            {
                return;
            }

            int tick = _play.PhysicsTick;

            if (tick != _lastContactTick)
            {
                // A new tick of contact. The run survives a gap of up to
                // CONTACT_GRACE_TICKS — see the summary — and restarts beyond it.
                bool continues = tick - _lastContactTick
                    <= Systems_SimConstants.CONTACT_GRACE_TICKS;

                _contactRunTicks = continues ? _contactRunTicks + 1 : 1;
                _lastContactTick = tick;
                _contactMask = 0u;
            }

            if (tacklerId >= 0 && tacklerId < Systems_PlayerRegistry.CAPACITY)
            {
                _contactMask |= 1u << tacklerId;
            }

            // How long THIS carrier takes to bring down, not a single number for
            // everybody — a fullback fights through better than twice the contact a
            // receiver does. See Systems_RoleTable.TackleTicksOf.
            Systems_IPlayerHandle carrier = CurrentCarrier();

            int ticksNeeded = carrier == null
                ? Systems_SimConstants.SUSTAINED_TACKLE_TICKS
                : Systems_RoleTable.TackleTicksOf(carrier.Role);

            // Help shortens the wrap-up but does not collapse it. Dividing straight
            // by the tackler count was MEASURED AND WAS FAR TOO STRONG: three men
            // arriving cut the requirement to a third, a carrier went down the
            // instant a crowd formed, and a full game came back at 2.93 yards a play
            // against real football's 5.5 — with five safeties in it, because the
            // offense could not get off its own goal line.
            //
            // 2n/(n+1) is the gentler curve: two tacklers need two thirds of the
            // ticks one does, three need a half, and it never falls below half
            // however many arrive. The second man still matters, which is the whole
            // point; he just does not end the down by himself.
            int tacklers = TacklerCount(_contactMask);

            if (tacklers > 1)
            {
                ticksNeeded = Mathf.Max(1, (ticksNeeded * 2) / (tacklers + 1));
            }

            if (_contactRunTicks < ticksNeeded)
            {
                return;
            }

            CompleteTackle(tacklerId, closingSpeed);
        }

        /// <summary>
        /// How many opponents have a hand on the carrier RIGHT NOW. The mask is only
        /// meaningful for the tick it was built on, so a tackle arriving by the
        /// instant-collision path (ReportContactWithCarrier, which never touches the
        /// mask) reads one — the man who just hit him — rather than a stale count
        /// from an earlier tick.
        /// </summary>
        private int TacklersOnCarrier()
        {
            if (_lastContactTick != _play.PhysicsTick)
            {
                return 1;
            }

            return Mathf.Max(1, TacklerCount(_contactMask));
        }

        /// <summary>
        /// Population count of the contact mask — how many distinct opponents have a
        /// hand on the carrier this tick. Kernighan's method: loops once per SET bit
        /// rather than once per slot, and allocates nothing.
        /// </summary>
        private static int TacklerCount(uint mask)
        {
            int count = 0;

            while (mask != 0u)
            {
                mask &= mask - 1u;
                count++;
            }

            return count;
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
            //
            // Safety takes precedence over a strip: a ball coming loose in your own
            // end zone is a whole rule of its own (the defense recovering it is a
            // touchdown) and this simulation has no model for it. Two points and the
            // ball is the right answer and already the worst outcome available.
            if (spot.y <= Systems_FieldModel.OWN_GOAL_LINE_Y)
            {
                EndPlay(Systems_PlayOutcome.Safety, spot);
                return;
            }

            bool stripped = _fumbleModel.IsFumbleLost(
                closingSpeed, TacklersOnCarrier(), carrier.Role);

            EndPlay(
                stripped ? Systems_PlayOutcome.FumbleLost : Systems_PlayOutcome.Tackle,
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
