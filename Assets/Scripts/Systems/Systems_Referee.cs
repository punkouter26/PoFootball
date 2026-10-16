using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Decides when a play is over. Several independent paths reach a whistle:
    ///
    ///   - the ball itself, via Systems_BallSystem: an incompletion, a field goal
    ///     through or wide, a kick downed or fair-caught, a loose ball out of bounds
    ///     or fallen on in an end zone;
    ///   - boundary, progress and clock conditions, polled once per FixedUpdate;
    ///   - a tackle, reported by the carrier the instant a collision occurs;
    ///   - a foul before the snap.
    ///
    /// The tackle path is event-driven rather than polled, so a hit is registered
    /// on the exact physics tick it happens (acceptance criterion #2). The polled
    /// path reads positions produced by the previous physics step, which is a
    /// deterministic one-tick lag and well inside the same tolerance.
    ///
    /// REVISION 14. Whoever has the ball may be either side now — an interception
    /// is a return, a kick is fielded by the defense — so every boundary below is
    /// judged against the CARRIER's goal rather than the offense's: a returner
    /// scores at −Y, and is down for a safety or a touchback at +Y. The play also
    /// has a pre-snap now, the kicks are timed from here, and a pile is down by
    /// forward progress rather than by a stall timer.
    /// </summary>
    public sealed class Systems_Referee : IFixedTickable
    {
        /// <summary>Pre-snap tick a drawn flinch puts its step into the body.</summary>
        private const int FLINCH_AT_PRESNAP_TICK = 6;

        private readonly Systems_PlayModel _play;
        private readonly Systems_BallModel _ball;
        private readonly Systems_BallSystem _ballSystem;
        private readonly Systems_FieldModel _field;
        private readonly Systems_PlayerRegistry _registry;
        private readonly Systems_IFumbleModel _fumbleModel;
        private readonly Systems_ISkillModel _skill;
        private readonly Systems_PenaltySystem _penalties;
        private readonly Systems_IDisciplineModel _discipline;
        private readonly IPublisher<Systems_PlayEndedMessage> _endedPublisher;
        private readonly IPublisher<Systems_TackleMessage> _tacklePublisher;
        private readonly IPublisher<Systems_ScoreMessage> _scorePublisher;

        private int _lastContactTick = -1;

        /// <summary>
        /// Last tick the ball carrier was moving at more than STALL_SPEED. The
        /// give-himself-up backstop measures the gap between this and now.
        /// </summary>
        private int _lastCarrierMovingTick;
        private int _contactRunTicks;

        /// <summary>
        /// Whether any BODY has touched the carrier during the current wrap-up run.
        /// False means it has all been reach — see CountPursuitReach — and a
        /// wrap-up finished on reach alone is an arm tackle, which breaks more
        /// easily (Systems_SimConstants.ARM_TACKLE_BREAK_MULTIPLIER).
        /// </summary>
        private bool _bodyContactInRun;

        /// <summary>
        /// Formation slots of every opponent in contact with the carrier on
        /// <see cref="_lastContactTick"/>, one bit per slot. A bitmask rather than a
        /// collection because this is written several times per physics tick and
        /// must not allocate.
        /// </summary>
        private uint _contactMask;

        // Forward progress: the carrier this is being measured for, the furthest he
        // has got toward his goal (in his own direction of attack, metres), and how
        // long he has been held without beating it.
        private int _progressCarrierId = -1;
        private bool _inContactStint;
        private float _furthestProgress;
        private float _furthestProgressY;
        private float _heldUpFrom;
        private int _heldUpTicks;

        /// <summary>The slot that flinches on this snap, or -1. Drawn once per snap.</summary>
        private int _flinchSlot = -1;

        public Systems_Referee(
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_BallSystem ballSystem,
            Systems_FieldModel field,
            Systems_PlayerRegistry registry,
            Systems_IFumbleModel fumbleModel,
            Systems_ISkillModel skill,
            Systems_PenaltySystem penalties,
            Systems_IDisciplineModel discipline,
            IPublisher<Systems_PlayEndedMessage> endedPublisher,
            IPublisher<Systems_TackleMessage> tacklePublisher,
            IPublisher<Systems_ScoreMessage> scorePublisher)
        {
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _field = field;
            _registry = registry;
            _fumbleModel = fumbleModel;
            _skill = skill;
            _penalties = penalties;
            _discipline = discipline;
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
            _bodyContactInRun = false;
            _contactMask = 0u;
            _progressCarrierId = -1;
            _inContactStint = false;
            _heldUpTicks = 0;
            _flinchSlot = -1;
        }

        public void FixedTick()
        {
            if (_play.Phase == Systems_PlayPhase.PreSnap)
            {
                TickPreSnap();
                return;
            }

            if (_play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            _play.AdvanceTick();

            if (TickClockPlay())
            {
                return;
            }

            TickKick();

            // The ball resolves first: a catch, a kick fielded, a ball recovered —
            // all must be settled before boundaries are judged against its new
            // position.
            Systems_BallEvent ballEvent = _ballSystem.Tick();
            _play.SetBallY(_ball.Position.y);
            _play.SetPossession(_ball.PossessingSide, _ball.Position.y, _play.ReturnOutcome);

            if (ballEvent.EndsPlay)
            {
                EndFromBall(ballEvent);
                return;
            }

            if (!_ball.IsHeld)
            {
                // In the air or on the ground. The ball system owns every way that
                // ends; only the trainer's ceiling is left to apply here.
                if (_play.PhysicsTick >= Systems_PlayModel.MAX_PHYSICS_TICKS)
                {
                    EndMapped(Systems_PlayOutcome.TimeExpired, _ball.Position, _ball.PossessingSide);
                }

                return;
            }

            EvaluateCarrier();
        }

        // =====================================================================
        // Before the snap
        // =====================================================================

        /// <summary>
        /// The teams stand set for PRESNAP_TICKS and then the ball is snapped —
        /// unless somebody jumps, or the play clock runs out.
        /// </summary>
        private void TickPreSnap()
        {
            _play.AdvancePreSnap();

            if (_play.PreSnapTick == 1 && _play.Kind != Systems_PlayKind.Kickoff)
            {
                _flinchSlot = _discipline.DrawFlinch();
            }

            if (_play.PreSnapTick == FLINCH_AT_PRESNAP_TICK && _flinchSlot >= 0)
            {
                Systems_IPlayerHandle jumper = _registry.Get(_flinchSlot);

                if (jumper != null)
                {
                    jumper.Nudge(
                        Systems_BallSystem.AttackDirectionOf(jumper.Side)
                            * Systems_SimConstants.FLINCH_SPEED);
                }
            }

            Systems_Foul foul = _penalties.CheckPreSnap();

            if (!foul.IsNone)
            {
                KillBeforeTheSnap(foul);
                return;
            }

            if (_play.PreSnapTick < Systems_SimConstants.PRESNAP_TICKS)
            {
                return;
            }

            if (_play.Kind != Systems_PlayKind.Kickoff && _discipline.IsDelayOfGame())
            {
                KillBeforeTheSnap(new Systems_Foul(
                    Systems_FoulType.DelayOfGame, Systems_TeamSide.Offense,
                    Systems_Formation.QUARTERBACK_SLOT_INDEX, _play.LineOfScrimmageY, 0));
                return;
            }

            _play.Snap();
        }

        private void KillBeforeTheSnap(Systems_Foul foul)
        {
            _play.RecordFoul(foul);
            EndPlay(
                Systems_PlayOutcome.DeadBallFoul,
                new Vector2(0f, _play.LineOfScrimmageY),
                Systems_TeamSide.Offense);
        }

        // =====================================================================
        // Clock plays and kicks
        // =====================================================================

        /// <summary>
        /// A kneel-down is over once the quarterback is on a knee; a spike once the
        /// ball hits the turf. Returns whether the play ended.
        /// </summary>
        private bool TickClockPlay()
        {
            if (_play.Kind == Systems_PlayKind.KneelDown
                && _play.PhysicsTick >= Systems_GameRules.KNEEL_TICKS)
            {
                Systems_IPlayerHandle quarterback = CurrentCarrier();

                if (quarterback != null)
                {
                    _play.SetBallY(quarterback.Position.y);
                    EndMapped(Systems_PlayOutcome.Downed, quarterback.Position, quarterback.Side);
                    return true;
                }
            }

            if (_play.Kind == Systems_PlayKind.Spike
                && _play.PhysicsTick >= Systems_GameRules.SPIKE_TICKS
                && _ball.IsHeld)
            {
                Systems_IPlayerHandle quarterback = CurrentCarrier();

                if (quarterback != null)
                {
                    EndFromBall(_ballSystem.Spike(quarterback));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The kicks are timed from the snap, as a real operation is: the kickoff
        /// after the kicker's approach, the punt after the long snap and the drop,
        /// the placekick after the snap and the hold. Whoever is holding the ball
        /// for it then is the kicker — a fake that has already pitched it does not
        /// kick.
        /// </summary>
        private void TickKick()
        {
            if (!_play.Kind.IsKick() || _play.WasKicked || !_ball.IsHeld)
            {
                return;
            }

            if (_ball.CarrierId != Systems_Formation.QUARTERBACK_SLOT_INDEX)
            {
                return;
            }

            if (_play.PhysicsTick < OperationTicks(_play.Kind))
            {
                return;
            }

            Systems_IPlayerHandle kicker = CurrentCarrier();

            // Past the line he cannot kick it; he is a runner now.
            if (kicker == null || kicker.Position.y > _play.LineOfScrimmageY)
            {
                return;
            }

            _ballSystem.Kick(kicker);
        }

        private static int OperationTicks(Systems_PlayKind kind)
        {
            switch (kind)
            {
                case Systems_PlayKind.Kickoff:
                    return Systems_SimConstants.KICKOFF_RUNUP_TICKS;

                case Systems_PlayKind.Punt:
                    return Systems_SimConstants.PUNT_OPERATION_TICKS;

                default:
                    return Systems_SimConstants.PLACEKICK_OPERATION_TICKS;
            }
        }

        // =====================================================================
        // The carrier
        // =====================================================================

        private void EvaluateCarrier()
        {
            Systems_IPlayerHandle carrier = CurrentCarrier();
            if (carrier == null)
            {
                return;
            }

            Vector2 position = carrier.Position;

            if (HasScored(carrier.Side, position.y))
            {
                _scorePublisher.Publish(new Systems_ScoreMessage(carrier.Id, _play.NetYards));
                EndPlay(Systems_PlayOutcome.Touchdown, position, carrier.Side);
                return;
            }

            if (_field.IsOutsideSidelines(position.x)
                || position.y <= Systems_FieldModel.OWN_BACK_LINE_Y
                || position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y)
            {
                EndCarrierDown(carrier, position, Systems_PlayOutcome.OutOfBounds);
                return;
            }

            if (TrackForwardProgress(carrier))
            {
                return;
            }

            // A CARRIER NOBODY HAS TACKLED IS A LIVE PLAY, AND THE CLOCK MUST NOT
            // TAKE HIM DOWN. The backstop measures whether the play has STOPPED,
            // not how long it has lasted: a carrier moving at a real pace keeps the
            // down alive indefinitely.
            //
            // Since revision 14 it is only for a carrier with NOBODY near him. A
            // pile that is not moving is down by forward progress, above, which is
            // the call an official makes; the stall rule used to end that too, under
            // the name TimeExpired, which is not. What is left is a carrier standing
            // still in space — he has given himself up, as a quarterback who takes a
            // knee or slides does — and that is Downed.
            //
            // A QUARTERBACK STANDING IN THE POCKET IS NOT A DEAD BALL. He is under
            // STALL_SPEED by definition — scanning is standing still — so the stall
            // clock is held while the throw is still legal, and a kicker waiting on
            // his operation is held the same way.
            bool stillLegalToThrow =
                carrier.Id == Systems_Formation.QUARTERBACK_SLOT_INDEX
                && (_play.PhysicsTick <= Systems_SimConstants.THROW_WINDOW_TICKS
                    || (_play.Kind.IsKick() && !_play.WasKicked));

            if (stillLegalToThrow
                || carrier.Velocity.sqrMagnitude
                    >= Systems_SimConstants.STALL_SPEED * Systems_SimConstants.STALL_SPEED)
            {
                _lastCarrierMovingTick = _play.PhysicsTick;
            }

            if (_play.PhysicsTick - _lastCarrierMovingTick
                >= Systems_SimConstants.STALL_TICKS)
            {
                // Stopped in a crowd, his progress has been stopped: a tackle, the
                // way an official whistles a pile that has stopped moving. Stopped
                // in space, he has given himself up.
                EndCarrierDown(
                    carrier, position,
                    IsOpponentNear(carrier) ? Systems_PlayOutcome.Tackle : Systems_PlayOutcome.Downed);
                return;
            }

            // The absolute ceiling, which exists for the trainer rather than for
            // football: an episode that never terminates hangs a rollout. It stays
            // at 600, because it also bounds the ratio between per-tick reward
            // shaping and the terminal reward, and it is paired with time_horizon in
            // every trainer config. See Systems_PlayModel.MAX_PHYSICS_TICKS.
            if (_play.PhysicsTick >= Systems_PlayModel.MAX_PHYSICS_TICKS)
            {
                EndMapped(Systems_PlayOutcome.TimeExpired, position, carrier.Side);
                return;
            }

            CountPursuitReach(carrier);
        }

        /// <summary>
        /// FORWARD PROGRESS (revision 14). A carrier in contact who stops gaining
        /// ground — less than FORWARD_PROGRESS_GAIN_METRES in FORWARD_PROGRESS_TICKS
        /// — or is driven back from the furthest point he reached while in it, is
        /// down, and the ball is spotted where his progress was stopped, not where
        /// he was pushed back to. Returns whether the whistle went.
        ///
        /// MEASURED PER CONTACT STINT. Ground a carrier gave up on his own before
        /// anybody touched him — a quarterback's dropback, a back bouncing a run
        /// outside — is not being driven back, so the furthest point is taken from
        /// when the contact began. And a quarterback in the pocket with the throw
        /// still on is exempt altogether: he is retreating on purpose, and a rusher
        /// who has hold of him brings him down by the ordinary wrap-up.
        /// </summary>
        private bool TrackForwardProgress(Systems_IPlayerHandle carrier)
        {
            Vector2 position = carrier.Position;
            float progress = carrier.Side == Systems_TeamSide.Offense ? position.y : -position.y;

            if (carrier.Id != _progressCarrierId)
            {
                // A new man with the ball — a catch, a handoff, a pick, a pitch. His
                // forward progress and his stall clock both start now.
                _progressCarrierId = carrier.Id;
                _inContactStint = false;
                _heldUpTicks = 0;
                _lastCarrierMovingTick = _play.PhysicsTick;
                return false;
            }

            bool inContact = _lastContactTick >= 0
                && _play.PhysicsTick - _lastContactTick <= Systems_SimConstants.CONTACT_GRACE_TICKS;

            bool inThePocket = carrier.Id == Systems_Formation.QUARTERBACK_SLOT_INDEX
                && _play.Call == Systems_PlayCall.Pass
                && !_play.ForwardPassThrown
                && _play.PhysicsTick <= Systems_SimConstants.THROW_WINDOW_TICKS;

            if (!inContact || inThePocket)
            {
                _inContactStint = false;
                _heldUpTicks = 0;
                return false;
            }

            if (!_inContactStint)
            {
                _inContactStint = true;
                _furthestProgress = progress;
                _furthestProgressY = position.y;
                _heldUpFrom = progress;
                _heldUpTicks = 0;
            }

            if (progress > _furthestProgress)
            {
                _furthestProgress = progress;
                _furthestProgressY = position.y;
            }

            // Real ground gained resets the count; inching does not.
            if (_furthestProgress > _heldUpFrom + Systems_SimConstants.FORWARD_PROGRESS_GAIN_METRES)
            {
                _heldUpFrom = _furthestProgress;
                _heldUpTicks = 0;
            }

            _heldUpTicks++;

            bool drivenBack = _furthestProgress - progress
                >= Systems_SimConstants.FORWARD_PROGRESS_PUSHBACK_METRES;

            if (_heldUpTicks < Systems_SimConstants.FORWARD_PROGRESS_TICKS && !drivenBack)
            {
                return false;
            }

            Vector2 spot = new Vector2(position.x, _furthestProgressY);
            _play.SetBallY(spot.y);

            int tacklerId = LowestSlotIn(_contactMask);

            _tacklePublisher.Publish(new Systems_TackleMessage(
                tacklerId, carrier.Id, 0f, _play.NetYards));

            EndCarrierDown(carrier, spot, Systems_PlayOutcome.Tackle);
            return true;
        }

        private bool IsOpponentNear(Systems_IPlayerHandle carrier)
        {
            float reach = Systems_SimConstants.REACH_TACKLE_RANGE * 2f;
            float reachSquared = reach * reach;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle other = _registry.Get(slotIndex);

                if (other != null && other.Side != carrier.Side
                    && (other.Position - carrier.Position).sqrMagnitude <= reachSquared)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A defender within arm's reach of the carrier, beside or behind him, has a
        /// hand on him — whether or not the two discs are touching this tick.
        ///
        /// WHY (revision 11). The wrap-up rule counted only ticks of PHYSICAL contact,
        /// reported by OnCollisionStay2D. Two discs running level at the same speed
        /// barely touch, and when they do the impulse pushes them apart, so a
        /// receiver caught in stride with a defender a metre off his shoulder could
        /// almost never be brought down: measured, 42 of 96 completions gained more
        /// than thirty yards AFTER the catch, many with a defender 1-3 m away, and a
        /// trained offense learned to throw nothing else (97% completions, 18.8
        /// yards a play). Real tacklers have arms.
        ///
        /// IN FRONT IS LEFT TO THE COLLISION. A defender meeting the carrier head-on
        /// already produces the closing speed TACKLE_CLOSING_SPEED measures, and
        /// giving him reach as well would end every run at the first man standing in
        /// the hole. So only an opponent outside REACH_TACKLE_FRONT_COS of the
        /// carrier's direction of travel counts here.
        ///
        /// It feeds the same count ReportSustainedContact does, so the role's own
        /// wrap-up time, the gang-tackle discount and the grace window all apply
        /// exactly as to a physical contact — except that it does not mark the run
        /// as having a body on him, so a wrap-up finished on reach alone is the arm
        /// tackle it looks like. One pass over the roster, no allocation.
        /// </summary>
        private void CountPursuitReach(Systems_IPlayerHandle carrier)
        {
            if (_play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            Vector2 position = carrier.Position;
            Vector2 velocity = carrier.Velocity;

            // Direction of travel; a carrier barely moving has no "front", and every
            // side then counts.
            bool hasHeading = velocity.sqrMagnitude > 0.25f;
            Vector2 heading = hasHeading ? velocity.normalized : Vector2.zero;

            float reachSquared =
                Systems_SimConstants.REACH_TACKLE_RANGE * Systems_SimConstants.REACH_TACKLE_RANGE;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle other = _registry.Get(slotIndex);

                if (other == null || other.Side == carrier.Side)
                {
                    continue;
                }

                Vector2 offset = other.Position - position;
                float distanceSquared = offset.sqrMagnitude;

                if (distanceSquared > reachSquared || distanceSquared < 0.0001f)
                {
                    continue;
                }

                if (hasHeading
                    && Vector2.Dot(offset / Mathf.Sqrt(distanceSquared), heading)
                        > Systems_SimConstants.REACH_TACKLE_FRONT_COS)
                {
                    continue;
                }

                CountWrapUpTick(
                    other.Id, (other.Velocity - velocity).magnitude, false);

                if (_play.Phase != Systems_PlayPhase.Live || !_ball.IsHeld)
                {
                    return;
                }
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

            // A HIT CAN MISS (revision 13). It used to end the down every time, so
            // arriving fast was the whole of tackling. A carrier who slips one is
            // still in contact with the man who hit him, and that contact counts
            // toward the wrap-up as it always did — a miss costs the defense a
            // moment, not the play. One collision is one call here, so one roll.
            Systems_IPlayerHandle carrier = CurrentCarrier();

            if (carrier != null
                && !_skill.IsHitTackleMade(
                    carrier.Role, FatigueOf(tacklerId), carrier.Fatigue))
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
        /// A SECOND TACKLER COUNTS: contact is counted per DISTINCT TACKLER
        /// (<see cref="_contactMask"/>) and the wrap-up they owe is divided between
        /// them. AND CONTACT NEED NOT BE STRICTLY CONSECUTIVE: a short grace window
        /// (Systems_SimConstants.CONTACT_GRACE_TICKS) keeps the wrap alive across
        /// the separation the collision impulse itself causes.
        /// </summary>
        public void ReportSustainedContact(int tacklerId, float closingSpeed)
        {
            CountWrapUpTick(tacklerId, closingSpeed, true);
        }

        /// <summary>
        /// One opponent's tick of wrap-up, from a body (ReportSustainedContact) or
        /// from reach (CountPursuitReach). Everything the summary above describes
        /// happens here.
        /// </summary>
        private void CountWrapUpTick(int tacklerId, float closingSpeed, bool isBodyContact)
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

                if (!continues)
                {
                    _bodyContactInRun = false;
                }
            }

            _bodyContactInRun |= isBodyContact;

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

            // Help shortens the wrap-up but does not collapse it: 2n/(n+1), so two
            // tacklers need two thirds of the ticks one does, three need a half, and
            // it never falls below half however many arrive. Dividing straight by the
            // count was measured and was far too strong — 2.93 yards a play.
            int tacklers = TacklerCount(_contactMask);

            if (tacklers > 1)
            {
                ticksNeeded = Mathf.Max(1, (ticksNeeded * 2) / (tacklers + 1));
            }

            if (_contactRunTicks < ticksNeeded)
            {
                return;
            }

            // AND A WRAP-UP CAN BE BROKEN (revision 13). The count restarts from
            // zero, so the defense has to wrap him up all over again; a second
            // tackler reporting on this same tick finds the count short and is not
            // rolled, so a wrap-up is one roll however many men are in it.
            if (carrier != null
                && !_skill.IsWrapUpHeld(
                    carrier.Role, tacklers, !_bodyContactInRun,
                    FatigueOf(tacklerId), carrier.Fatigue))
            {
                _contactRunTicks = 0;
                _bodyContactInRun = false;
                return;
            }

            CompleteTackle(tacklerId, closingSpeed);
        }

        /// <summary>Fatigue of the player in this slot; a fresh body if there is none.</summary>
        private float FatigueOf(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= Systems_PlayerRegistry.CAPACITY)
            {
                return 0f;
            }

            Systems_IPlayerHandle player = _registry.Get(slotIndex);

            return player == null ? 0f : player.Fatigue;
        }

        /// <summary>
        /// How many opponents have a hand on the carrier RIGHT NOW. The mask is only
        /// meaningful for the tick it was built on, so a tackle arriving by the
        /// instant-collision path reads one — the man who just hit him.
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

        private static int LowestSlotIn(uint mask)
        {
            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                if ((mask & (1u << slotIndex)) != 0u)
                {
                    return slotIndex;
                }
            }

            return -1;
        }

        /// <summary>A ball in the air or on the ground cannot be tackled — nobody is holding it.</summary>
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

            _penalties.JudgeTackle(tacklerId, carrier, closingSpeed);

            // THE BALL CAN COME OUT, AND SINCE REVISION 14 THE PLAY GOES ON. Through
            // revision 13 a strip was a whistle — the defense's ball where the tackle
            // happened — because there was no loose ball to scramble for. There is
            // now: it is on the ground, live, and either side may fall on it. Even
            // in the carrier's own end zone, where a defender falling on it is a
            // touchdown and the carrier's side falling on it is a safety.
            if (_fumbleModel.IsBallStripped(closingSpeed, TacklersOnCarrier(), carrier.Role))
            {
                _ballSystem.Strip(carrier);
                _contactRunTicks = 0;
                _contactMask = 0u;
                _bodyContactInRun = false;
                return;
            }

            EndCarrierDown(carrier, spot, Systems_PlayOutcome.Tackle);
        }

        // =====================================================================
        // The whistle
        // =====================================================================

        /// <summary>
        /// The carrier is down — tackled, out of bounds, held up, or given himself
        /// up. In the end zone he is defending that is a safety, unless his side got
        /// the ball in there off the other side's play (an interception taken in the
        /// end zone and downed), which is a touchback.
        /// </summary>
        private void EndCarrierDown(
            Systems_IPlayerHandle carrier, Vector2 spot, Systems_PlayOutcome outcome)
        {
            Systems_TeamSide side = carrier.Side;

            bool inOwnEndZone = side == Systems_TeamSide.Offense
                ? spot.y <= Systems_FieldModel.OWN_GOAL_LINE_Y
                : spot.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y;

            if (inOwnEndZone)
            {
                bool gainedThereFromTheOtherSide = side == Systems_TeamSide.Defense
                    && _play.PossessionChanged
                    && _play.PossessionChangeY >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y;

                if (gainedThereFromTheOtherSide)
                {
                    EndPlay(Systems_PlayOutcome.Touchback, spot, side);
                    return;
                }

                // A safety: two points for the other side and the ball. Down on or
                // behind his own goal line, which is what lets Reward_Terminal price
                // it as the disaster it is.
                EndPlay(Systems_PlayOutcome.Safety, spot, Opposite(side));
                return;
            }

            if (outcome == Systems_PlayOutcome.Tackle && IsSack(carrier, spot))
            {
                outcome = Systems_PlayOutcome.Sack;
            }

            EndMapped(outcome, spot, side);
        }

        /// <summary>
        /// The quarterback brought down behind the line on a pass he has not thrown.
        /// Recorded as its own outcome since revision 14; it was a tackle for loss.
        /// </summary>
        private bool IsSack(Systems_IPlayerHandle carrier, Vector2 spot)
        {
            return carrier.Role == Systems_PlayerRole.Quarterback
                && carrier.Side == Systems_TeamSide.Offense
                && _play.Call == Systems_PlayCall.Pass
                && !_play.ForwardPassThrown
                && spot.y < _play.LineOfScrimmageY;
        }

        /// <summary>
        /// A whistle the ball system decided. Down-type endings carry the side that
        /// keeps the ball in their scoring-side slot, and go through the same mapping
        /// as a carrier going down.
        /// </summary>
        private void EndFromBall(Systems_BallEvent ballEvent)
        {
            switch (ballEvent.Outcome)
            {
                case Systems_PlayOutcome.Tackle:
                case Systems_PlayOutcome.OutOfBounds:
                    _play.SetBallY(ballEvent.Spot.y);
                    EndMapped(ballEvent.Outcome, ballEvent.Spot, ballEvent.ScoringSide);
                    return;

                case Systems_PlayOutcome.Touchdown:
                    _scorePublisher.Publish(new Systems_ScoreMessage(-1, _play.NetYards));
                    EndPlay(ballEvent.Outcome, ballEvent.Spot, ballEvent.ScoringSide);
                    return;

                case Systems_PlayOutcome.Punt:
                case Systems_PlayOutcome.Kickoff:
                case Systems_PlayOutcome.KickBlocked:
                case Systems_PlayOutcome.FieldGoalMissed:
                case Systems_PlayOutcome.Touchback:
                    _play.SetBallY(ballEvent.Spot.y);
                    EndPlay(ballEvent.Outcome, ballEvent.Spot, ballEvent.ScoringSide);
                    return;

                default:
                    EndPlay(ballEvent.Outcome, ballEvent.Spot, ballEvent.ScoringSide);
                    return;
            }
        }

        /// <summary>
        /// A down-type ending — tackle, out of bounds, given up, sacked, the trainer's
        /// ceiling — with the defense of the snap holding the ball is not a tackle
        /// for the offense: it is however the defense got it, ended at this spot.
        /// </summary>
        private void EndMapped(
            Systems_PlayOutcome outcome, Vector2 spot, Systems_TeamSide holder)
        {
            if (holder == Systems_TeamSide.Defense && _play.PossessionChanged)
            {
                outcome = _play.ReturnOutcome;
            }

            EndPlay(outcome, spot, holder);
        }

        private void EndPlay(Systems_PlayOutcome outcome, Vector2 spot, Systems_TeamSide side)
        {
            Vector2 clampedSpot = _field.ClampToField(spot);
            int ticks = _play.PhysicsTick;
            float netYards = _play.NetYards;

            _play.EndPlay(outcome, clampedSpot, side);

            _endedPublisher.Publish(
                new Systems_PlayEndedMessage(
                    outcome, _play.Call, clampedSpot, netYards, ticks, _play.PassCompleted,
                    _play.Kind, side, _play.PossessionChanged,
                    _play.PossessionChangeY, _play.FoulCount));
        }

        /// <summary>Whether a carrier on this side at this Y has crossed the goal line he attacks.</summary>
        private static bool HasScored(Systems_TeamSide side, float y)
        {
            return side == Systems_TeamSide.Offense
                ? y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                : y <= Systems_FieldModel.OWN_GOAL_LINE_Y;
        }

        private static Systems_TeamSide Opposite(Systems_TeamSide side)
        {
            return side == Systems_TeamSide.Offense
                ? Systems_TeamSide.Defense
                : Systems_TeamSide.Offense;
        }
    }
}
