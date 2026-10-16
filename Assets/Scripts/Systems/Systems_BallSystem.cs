using MessagePipe;
using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Moves the ball and resolves everything that can happen to it: handoffs while
    /// held; flight, catches and interceptions once thrown; and since revision 14
    /// pitches, kicks and a ball on the ground.
    ///
    /// Driven explicitly from Systems_Referee.FixedTick rather than registered as
    /// its own entry point, so the ball is always resolved before the referee
    /// evaluates boundaries and the clock. Relying on entry-point registration
    /// order for that would be a silent trap.
    ///
    /// REVISION 14: THE BALL CAN CHANGE HANDS WITHOUT ENDING THE PLAY. An
    /// interception used to be a whistle; it is now a return. A fumble used to be a
    /// dice roll that gave the defense the ball on the spot; it is now a ball on the
    /// ground that either side can fall on and run with. A punt used to be "forty
    /// yards, the other team's ball"; it is now a ball in the air for four seconds
    /// that somebody has to field, fair-catch, muff, or let roll. Every one of those
    /// is decided here, on the tick it happens, and reported as a
    /// <see cref="Systems_BallEvent"/> only when it is the end of the play.
    ///
    /// THE TWO PUBLISHERS ARE PRESENTATION TAPS, NOT RULES. A release and a catch
    /// are announced after the model has already been updated, nothing in the
    /// simulation subscribes to either, and in training they are published to
    /// nobody — a handful of empty dispatches per play.
    /// </summary>
    public sealed class Systems_BallSystem
    {
        /// <summary>
        /// The length a throw at nobody is treated as having, for its error. Only
        /// reachable with no eligible receiver on the field, which no formation
        /// here lines up.
        /// </summary>
        private const float UNTARGETED_THROW_METRES = 15f;

        /// <summary>Share of its speed a muffed or batted ball keeps as it hits the turf.</summary>
        private const float DEFLECTED_SPEED_RETAINED = 0.3f;

        private readonly Systems_BallModel _ball;
        private readonly Systems_PlayModel _play;
        private readonly Systems_PlayerRegistry _registry;
        private readonly Systems_ISkillModel _skill;
        private readonly IPublisher<Systems_PassThrownMessage> _thrownPublisher;
        private readonly IPublisher<Systems_PassCaughtMessage> _caughtPublisher;

        /// <summary>
        /// Closest any eligible receiver came to the current forward pass, metres.
        /// Intentional grounding is a pass thrown where no receiver had a chance at
        /// it, and where the ball finally lands does not answer that: a pass that
        /// sails a yard past its man travels on for another thirty.
        /// </summary>
        private float _closestReceiverApproach = float.PositiveInfinity;

        public Systems_BallSystem(
            Systems_BallModel ball,
            Systems_PlayModel play,
            Systems_PlayerRegistry registry,
            Systems_ISkillModel skill,
            IPublisher<Systems_PassThrownMessage> thrownPublisher,
            IPublisher<Systems_PassCaughtMessage> caughtPublisher)
        {
            _ball = ball;
            _play = play;
            _registry = registry;
            _skill = skill;
            _thrownPublisher = thrownPublisher;
            _caughtPublisher = caughtPublisher;
        }

        /// <summary>
        /// Advances the ball one physics tick and reports anything that ends the
        /// play. <see cref="Systems_BallEvent.None"/> while the play should continue.
        /// </summary>
        public Systems_BallEvent Tick()
        {
            switch (_ball.State)
            {
                case Systems_BallState.Held:
                    UpdateHeldBall();
                    return Systems_BallEvent.None;

                case Systems_BallState.InFlight:
                    return _ball.PassKind == Systems_PassKind.Lateral
                        ? UpdateLateral()
                        : UpdateForwardPass();

                case Systems_BallState.Kicked:
                    return UpdateKick();

                case Systems_BallState.Loose:
                    return UpdateLoose();

                default:
                    return Systems_BallEvent.None;
            }
        }

        // =====================================================================
        // Held
        // =====================================================================

        private void UpdateHeldBall()
        {
            Systems_IPlayerHandle carrier = _registry.Get(_ball.CarrierId);
            if (carrier == null)
            {
                return;
            }

            _ball.FollowCarrier(carrier.Position);
            TryHandoff(carrier);
        }

        /// <summary>
        /// A handoff completes when the quarterback, still holding the ball on a
        /// handoff call, gets within HANDOFF_RADIUS of the designated back. There is
        /// no separate handoff action — committing to the call and closing the
        /// distance is the whole mechanic.
        /// </summary>
        private void TryHandoff(Systems_IPlayerHandle carrier)
        {
            if (carrier.Role != Systems_PlayerRole.Quarterback)
            {
                return;
            }

            if (_play.Call != Systems_PlayCall.HandoffFullback
                && _play.Call != Systems_PlayCall.HandoffHalfback)
            {
                return;
            }

            int targetSlot = _play.Call == Systems_PlayCall.HandoffFullback
                ? Systems_Formation.FULLBACK_SLOT_INDEX
                : Systems_Formation.HALFBACK_SLOT_INDEX;

            Systems_IPlayerHandle target = _registry.Get(targetSlot);
            if (target == null)
            {
                return;
            }

            if (Vector2.Distance(carrier.Position, target.Position)
                > Systems_SimConstants.HANDOFF_RADIUS)
            {
                return;
            }

            carrier.SetCarrier(false);
            target.SetCarrier(true);
            _ball.AttachTo(targetSlot, target.Position, target.Side);
        }

        // =====================================================================
        // The forward pass
        // =====================================================================

        private Systems_BallEvent UpdateForwardPass()
        {
            _ball.AdvanceFlight(Time.fixedDeltaTime);
            TrackReceiverApproach();

            Systems_IPlayerHandle catcher = FindCatcher();

            if (catcher != null)
            {
                bool intercepted = catcher.Side == Systems_TeamSide.Defense;

                // Measured before the ball is attached: AttachTo moves it onto the
                // catcher, and the throw's length is to where it was actually met.
                float airMetres = Vector2.Distance(_ball.ThrowOrigin, _ball.Position);
                float airYards = airMetres / Systems_FieldModel.YARD;

                // BEING THERE IS NOT THE SAME AS MAKING THE PLAY (revision 13).
                // Whoever FindCatcher names has the ball in his hands; whether he
                // keeps it is Systems_ISkillModel's. Every branch below ends the
                // flight on this tick, so each arrival is rolled exactly once
                // rather than once per tick the ball spends inside the radius.
                if (!IsCatchMade(catcher, intercepted, airMetres))
                {
                    return EndIncompletion();
                }

                catcher.SetCarrier(true);
                _ball.AttachTo(catcher.Id, catcher.Position, catcher.Side);

                if (intercepted)
                {
                    // NOT A WHISTLE ANY MORE (revision 14). The defender who caught
                    // it is a runner now, and the play goes on until he is down.
                    _play.SetPossession(
                        Systems_TeamSide.Defense, catcher.Position.y,
                        Systems_PlayOutcome.Interception);
                }
                else
                {
                    _play.MarkPassCompleted();
                }

                _caughtPublisher.Publish(
                    new Systems_PassCaughtMessage(catcher.Id, intercepted, airYards));

                return Systems_BallEvent.None;
            }

            // A pass that sails out of the field of play is incomplete; it cannot
            // score and nobody can catch it.
            if (IsOutsideSidelines(_ball.Position.x)
                || _ball.Position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y
                || _ball.Position.y <= Systems_FieldModel.OWN_BACK_LINE_Y)
            {
                return EndIncompletion();
            }

            if (_ball.FlightTicks >= Systems_SimConstants.MAX_FLIGHT_TICKS)
            {
                return EndIncompletion();
            }

            return Systems_BallEvent.None;
        }

        /// <summary>
        /// The ball is dead on the ground — and if it was thrown to nobody from the
        /// pocket, that is a flag.
        /// </summary>
        private Systems_BallEvent EndIncompletion()
        {
            if (_play.Kind != Systems_PlayKind.Spike && IsIntentionalGrounding())
            {
                _play.RecordFoul(new Systems_Foul(
                    Systems_FoulType.IntentionalGrounding,
                    Systems_TeamSide.Offense,
                    _ball.ThrowerId,
                    _ball.ThrowOrigin.y,
                    _play.PhysicsTick));
            }

            _ball.MarkIncomplete();
            return new Systems_BallEvent(
                Systems_PlayOutcome.Incompletion, _ball.Position, Systems_TeamSide.Offense);
        }

        /// <summary>
        /// Thrown where no eligible receiver had a chance at it — unless it was a
        /// legal throwaway: from outside the tackle box, and far enough to reach the
        /// line of scrimmage.
        /// </summary>
        private bool IsIntentionalGrounding()
        {
            bool outsideTheBox =
                Mathf.Abs(_ball.ThrowOrigin.x) > Systems_SimConstants.TACKLE_BOX_HALF_WIDTH;

            bool reachedTheLine = _ball.FurthestFlightY >= _play.LineOfScrimmageY;

            if (outsideTheBox && reachedTheLine)
            {
                return false;
            }

            return _closestReceiverApproach > Systems_SimConstants.GROUNDING_AREA_METRES;
        }

        private void TrackReceiverApproach()
        {
            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Side != Systems_TeamSide.Offense
                    || candidate.Id == _ball.ThrowerId
                    || !IsEligibleReceiver(candidate.Role))
                {
                    continue;
                }

                float distance = Vector2.Distance(candidate.Position, _ball.Position);

                if (distance < _closestReceiverApproach)
                {
                    _closestReceiverApproach = distance;
                }
            }
        }

        /// <summary>
        /// Whether the man FindCatcher named comes up with the ball.
        ///
        /// A DEFENDER ON THE BALL BREAKS UP A CATCH (revision 11): FindCatcher
        /// hands the ball to whoever is nearest inside CATCH_RADIUS, so a receiver
        /// a step closer than his defender always made the catch and the trained
        /// offense completed 97% of its passes, against roughly 65% in real
        /// football. That was a rule with one answer inside PASS_BREAKUP_RADIUS; it
        /// is now a chance that rises as the defender closes
        /// (Systems_SeededSkillModel.BreakUpChance), so tight coverage is still
        /// something a policy can see and earn, and loose coverage is no longer as
        /// good as tight.
        ///
        /// An open receiver can drop it, and a defender who is nearest can fail to
        /// hold the interception — both fall incomplete.
        /// </summary>
        private bool IsCatchMade(
            Systems_IPlayerHandle catcher, bool intercepted, float airMetres)
        {
            if (intercepted)
            {
                return _skill.IsInterceptionHeld();
            }

            float coverageMetres = Mathf.Sqrt(NearestCoverageSquared());

            if (coverageMetres <= Systems_SimConstants.PASS_BREAKUP_RADIUS
                && _skill.IsPassBrokenUp(coverageMetres))
            {
                return false;
            }

            return _skill.IsCatchHeld(airMetres, catcher.Fatigue);
        }

        /// <summary>
        /// Squared distance from the ball to the nearest defender who could catch
        /// it — anyone but the defensive line, as in FindCatcher. Infinity when
        /// there is none.
        /// </summary>
        private float NearestCoverageSquared()
        {
            float nearestSquared = float.PositiveInfinity;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Side != Systems_TeamSide.Defense
                    || candidate.Role == Systems_PlayerRole.DefensiveLine)
                {
                    continue;
                }

                float distanceSquared = (candidate.Position - _ball.Position).sqrMagnitude;

                if (distanceSquared < nearestSquared)
                {
                    nearestSquared = distanceSquared;
                }
            }

            return nearestSquared;
        }

        /// <summary>
        /// Closest player inside the catch radius takes the ball. Both sides are
        /// eligible, so a defender in better position genuinely wins the contest —
        /// that is what makes coverage worth learning.
        /// </summary>
        private Systems_IPlayerHandle FindCatcher()
        {
            // A pass must clear the pocket before it is live. Otherwise it is
            // caught instantly by whoever stands next to the quarterback, which
            // makes every throw a disguised handoff.
            if (Vector2.Distance(_ball.Position, _ball.ThrowOrigin)
                < Systems_SimConstants.MIN_CATCH_DISTANCE)
            {
                return null;
            }

            // THE BALL NO LONGER HAS TO CLEAR THE LINE OF SCRIMMAGE, and the rule
            // that said it did is worth explaining because it was right when it was
            // written and is not any more.
            //
            // It was added because a pass thrown from a seven-yard drop was still
            // BEHIND the line when MIN_CATCH_DISTANCE expired, so the first eligible
            // body it met was a defensive lineman standing in the trenches: a game of
            // forty plays produced thirty-six interceptions and not one completion.
            // Requiring the ball past the line modelled it flying over the trenches.
            //
            // But the trenches were then fixed properly — BOTH lines are excluded
            // from this search a few lines below, on the honest grounds that with no
            // height in the catch test the trenches cannot catch at all. With that
            // in place the line gate stopped guarding anything and started costing
            // something: it made every screen, swing pass and checkdown in football
            // physically impossible, so the only pass this game could throw was one
            // downfield. That is a whole category of real football deleted to fix a
            // bug that has a better fix already applied.
            //
            // MIN_CATCH_DISTANCE above still stops a throw being caught by whoever
            // stands next to the quarterback, which is the failure this is sometimes
            // mistaken for guarding against.

            Systems_IPlayerHandle best = null;
            float bestDistance = Systems_SimConstants.CATCH_RADIUS;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);
                if (candidate == null)
                {
                    continue;
                }

                // The thrower cannot catch its own pass.
                if (candidate.Id == _ball.ThrowerId)
                {
                    continue;
                }

                // NOR CAN EITHER LINE. Linemen block and rush; they do not run
                // routes and they do not cover. Excluding only the OFFENSIVE line
                // was half a rule and it made the game unplayable: the defensive
                // line stands a metre or two PAST the line of scrimmage, so it was
                // still the first eligible body on nearly every throw even after
                // catches were required to clear the line. Measured over forty
                // plays: thirty interceptions, ten touchdowns, and not one
                // incompletion or tackle — every pass was taken by a body in the
                // trenches or sailed to an uncovered man behind the whole defense.
                //
                // A defensive lineman batting a ball down is real football; a
                // defensive tackle leading the league in interceptions is not. The
                // honest model here, with no height check in the catch test and no
                // deflection mechanic, is that the trenches cannot catch.
                if (candidate.Role == Systems_PlayerRole.OffensiveLine
                    || candidate.Role == Systems_PlayerRole.DefensiveLine)
                {
                    continue;
                }

                float distance = Vector2.Distance(candidate.Position, _ball.Position);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Releases a pass. Aim arrives as a raw action vector; a zero-length aim
        /// falls back to straight downfield so a degenerate action still produces a
        /// legal throw rather than a stationary ball.
        ///
        /// SINCE REVISION 14 A PASS CAN BE THROWN AT NOBODY. An aim nowhere near any
        /// receiver's lead (THROWAWAY_OFF_RAY_METRES) is a throwaway: the ball goes
        /// where it was pointed, hard. That is legal from outside the tackle box if
        /// it reaches the line, and intentional grounding otherwise — EndIncompletion
        /// decides which once the ball is down.
        /// </summary>
        public void Throw(Systems_IPlayerHandle thrower, Vector2 aim, float power)
        {
            float speed = ThrowSpeedFor(power);

            Systems_IPlayerHandle target = ResolveThrowTarget(
                thrower, aim, speed, out Vector2 direction, out float offRay);

            bool throwaway = target == null
                || offRay > Systems_SimConstants.THROWAWAY_OFF_RAY_METRES;

            if (throwaway)
            {
                direction = aim.sqrMagnitude < 1e-4f ? Vector2.up : aim.normalized;
                speed = Systems_SimConstants.PASS_SPEED_MAX;
                target = null;
            }
            else
            {
                // The BALL goes where the quarterback actually pointed, blended
                // toward the perfect lead. ResolveThrowTarget stays pure and keeps
                // returning the READ — Systems_IntentOverlayView draws the receiver
                // the passer selected, which is still the truth about its decision
                // even when the throw misses.
                direction = ApplyAimSlack(direction, aim);

                // AND THEN IT MISSES BY A LITTLE (revision 13). The slack above is
                // the quarterback's DECISION being sloppy; this is his arm. It is
                // applied here, to the ball, so it reaches a trained quarterback and
                // a scripted one alike.
                float throwMetres = Vector2.Distance(
                    thrower.Position, LeadPoint(thrower.Position, target, speed));

                direction = Rotate(
                    direction, _skill.ThrowErrorRadians(throwMetres, thrower.Fatigue));
            }

            thrower.SetCarrier(false);
            _ball.Throw(thrower.Id, thrower.Position, direction * speed, Systems_PassKind.Forward);
            _play.MarkForwardPass();
            _closestReceiverApproach = float.PositiveInfinity;

            _thrownPublisher.Publish(new Systems_PassThrownMessage(
                thrower.Id, target == null ? -1 : target.Id, speed));
        }

        /// <summary>
        /// The clock-stopping spike: straight into the turf at the quarterback's
        /// feet. Incomplete on the spot, and never grounding.
        /// </summary>
        public Systems_BallEvent Spike(Systems_IPlayerHandle quarterback)
        {
            quarterback.SetCarrier(false);
            _ball.Throw(
                quarterback.Id, quarterback.Position, Vector2.zero, Systems_PassKind.Forward);
            _play.MarkForwardPass();
            _ball.MarkIncomplete();

            return new Systems_BallEvent(
                Systems_PlayOutcome.Incompletion, quarterback.Position, Systems_TeamSide.Offense);
        }

        private static Vector2 Rotate(Vector2 direction, float radians)
        {
            float cos = Mathf.Cos(radians);
            float sin = Mathf.Sin(radians);

            return new Vector2(
                (direction.x * cos) - (direction.y * sin),
                (direction.x * sin) + (direction.y * cos));
        }

        /// <summary>
        /// How fast a pass thrown at this power flies.
        ///
        /// Public and static so the intent overlay can lead the receiver by the
        /// same flight time the throw will actually use. It is arithmetic on two
        /// constants; the alternative was a view re-deriving it from
        /// Systems_SimConstants and drifting the first time either bound moved.
        /// </summary>
        public static float ThrowSpeedFor(float power)
        {
            return Mathf.Lerp(
                Systems_SimConstants.PASS_SPEED_MIN,
                Systems_SimConstants.PASS_SPEED_MAX,
                Mathf.Clamp01((power + 1f) * 0.5f));
        }

        /// <summary>
        /// Turns the quarterback's aim vector into a throw at an actual eligible
        /// receiver, led for the flight time.
        ///
        /// WHY THIS IS NOT JUST aim.normalized ANY MORE. It used to be, and the
        /// consequence was a quarterback throwing at empty grass: the aim was a free
        /// two-axis continuous action, so every direction on the field was equally
        /// available and only the reward signal discouraged the 359 degrees with
        /// nobody standing in them. Learning to point a continuous vector at a
        /// moving team-mate is a far harder control problem than choosing which
        /// team-mate to throw to, and it is not the problem this project is trying
        /// to study.
        ///
        /// The aim now means "which of my receivers", read as a direction of intent:
        /// the eligible receiver whose bearing best matches the aim wins the ball.
        /// Since revision 14 an aim that matches nobody at all is a throwaway — see
        /// Throw.
        ///
        /// Linemen are excluded because they are ineligible receivers, and the
        /// thrower is excluded because it cannot catch its own pass — the same rule
        /// FindCatcher already enforces on the receiving end.
        ///
        /// PUBLIC SO THE OVERLAY DRAWS THE READ THE QUARTERBACK WILL ACTUALLY MAKE.
        /// Systems_IntentOverlayView shows a line from the passer to the receiver
        /// its aim currently selects, and a view that re-implemented the off-ray
        /// rule below would start lying the first time this one was tuned.
        ///
        /// Read-only despite living on a system: it inspects the registry and
        /// mutates nothing.
        /// </summary>
        public Systems_IPlayerHandle ResolveThrowTarget(
            Systems_IPlayerHandle thrower, Vector2 aim, float speed, out Vector2 direction)
        {
            return ResolveThrowTarget(thrower, aim, speed, out direction, out float _);
        }

        private Systems_IPlayerHandle ResolveThrowTarget(
            Systems_IPlayerHandle thrower, Vector2 aim, float speed,
            out Vector2 direction, out float bestOffRay)
        {
            Vector2 intent = aim.sqrMagnitude < 1e-4f ? Vector2.up : aim.normalized;

            Systems_IPlayerHandle target = null;
            bestOffRay = float.PositiveInfinity;
            Vector2 bestLead = Vector2.zero;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Id == thrower.Id
                    || candidate.Side != thrower.Side
                    || !IsEligibleReceiver(candidate.Role))
                {
                    continue;
                }

                Vector2 lead = LeadPoint(thrower.Position, candidate, speed);
                Vector2 toLead = lead - thrower.Position;

                if (toLead.sqrMagnitude < 1e-4f)
                {
                    continue;
                }

                // HOW FAR OFF THE AIM RAY THIS RECEIVER SITS, not how well its
                // BEARING matches. Scoring by bearing alone ignores distance
                // entirely, so of two receivers nearly in line with the aim the
                // deeper one wins on a rounding error — every checkdown became a
                // bomb. Perpendicular offset answers the question actually being
                // asked: WHICH receiver was this thrown at.
                float along = Vector2.Dot(toLead, intent);

                if (along <= 0f)
                {
                    // Behind the throw. Not a forward pass to this man.
                    continue;
                }

                float offRay = (toLead - (intent * along)).magnitude;

                if (offRay < bestOffRay)
                {
                    bestOffRay = offRay;
                    target = candidate;
                    bestLead = toLead;
                }
            }

            // No eligible receiver on the field at all — a formation this game never
            // lines up, but the ball still has to go somewhere legal.
            direction = target == null ? intent : bestLead.normalized;
            return target;
        }

        /// <summary>
        /// Blends the perfectly-led direction toward the quarterback's raw aim by
        /// <see cref="Systems_SimConstants.PASS_AIM_SLACK"/>.
        ///
        /// A plain normalized lerp rather than a slerp: ResolveThrowTarget only
        /// selects targets whose lead direction has a POSITIVE dot with the intent
        /// (`along > 0`), so the two vectors are always inside 90 degrees and the
        /// lerp cannot degenerate.
        /// </summary>
        private static Vector2 ApplyAimSlack(Vector2 ledDirection, Vector2 aim)
        {
            if (Systems_SimConstants.PASS_AIM_SLACK <= 0f || aim.sqrMagnitude < 1e-4f)
            {
                return ledDirection;
            }

            Vector2 blended = Vector2.Lerp(
                ledDirection, aim.normalized, Systems_SimConstants.PASS_AIM_SLACK);

            return blended.sqrMagnitude < 1e-4f ? ledDirection : blended.normalized;
        }

        /// <summary>
        /// Where the receiver will be when the ball arrives. One pass of
        /// distance-over-speed is enough: the correction is small relative to the
        /// catch radius, and iterating it would chase a moving target for no
        /// visible gain.
        /// </summary>
        public static Vector2 LeadPoint(
            Vector2 origin, Systems_IPlayerHandle receiver, float speed)
        {
            if (speed <= 0f)
            {
                return receiver.Position;
            }

            float flightTime = Vector2.Distance(origin, receiver.Position) / speed;
            return receiver.Position + (receiver.Velocity * flightTime);
        }

        /// <summary>
        /// Everyone on offense except the line. The quarterback is filtered out by
        /// the thrower check rather than here, so a trick play that hands off first
        /// could still find it.
        /// </summary>
        private static bool IsEligibleReceiver(Systems_PlayerRole role)
        {
            return role != Systems_PlayerRole.OffensiveLine;
        }

        // =====================================================================
        // The lateral
        // =====================================================================

        /// <summary>
        /// The team-mate a pitch from <paramref name="carrier"/> along
        /// <paramref name="aim"/> would go to: level with or behind him, inside
        /// LATERAL_MAX_RANGE, nearest the aim ray. Null when nobody qualifies — and
        /// then the pitch is not on, which is what the action mask reads.
        ///
        /// ANYBODY MAY CATCH A BACKWARD PASS, linemen included. Eligibility is a
        /// forward-pass rule.
        /// </summary>
        public Systems_IPlayerHandle ResolveLateralTarget(
            Systems_IPlayerHandle carrier, Vector2 aim)
        {
            Vector2 attack = AttackDirectionOf(carrier.Side);
            bool hasIntent = aim.sqrMagnitude >= 1e-4f;
            Vector2 intent = hasIntent ? aim.normalized : -attack;

            Systems_IPlayerHandle best = null;
            float bestScore = float.PositiveInfinity;

            float rangeSquared =
                Systems_SimConstants.LATERAL_MAX_RANGE * Systems_SimConstants.LATERAL_MAX_RANGE;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null
                    || candidate.Id == carrier.Id
                    || candidate.Side != carrier.Side)
                {
                    continue;
                }

                Vector2 offset = candidate.Position - carrier.Position;
                float distanceSquared = offset.sqrMagnitude;

                if (distanceSquared > rangeSquared || distanceSquared < 1f)
                {
                    continue;
                }

                if (Vector2.Dot(offset, attack) > Systems_SimConstants.LATERAL_LEVEL_TOLERANCE)
                {
                    continue;
                }

                // Off the aim ray, as for a throw; with no aim, simply the nearest.
                float along = Vector2.Dot(offset, intent);
                float score = hasIntent
                    ? (along <= 0f ? float.PositiveInfinity : (offset - (intent * along)).magnitude)
                    : distanceSquared;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>
        /// Pitches the ball to the team-mate <see cref="ResolveLateralTarget"/>
        /// finds. Returns whether it went. Never forward: any forward component the
        /// lead would give it is taken out, so a lateral is a lateral by
        /// construction rather than an illegal forward pass waiting to be flagged.
        /// </summary>
        public bool TryLateral(Systems_IPlayerHandle carrier, Vector2 aim)
        {
            if (!_ball.IsHeld || _ball.CarrierId != carrier.Id)
            {
                return false;
            }

            Systems_IPlayerHandle target = ResolveLateralTarget(carrier, aim);

            if (target == null)
            {
                return false;
            }

            Vector2 attack = AttackDirectionOf(carrier.Side);
            Vector2 lead = LeadPoint(carrier.Position, target, Systems_SimConstants.LATERAL_SPEED);
            Vector2 direction = lead - carrier.Position;

            float forward = Vector2.Dot(direction, attack);

            if (forward > 0f)
            {
                direction -= attack * forward;
            }

            direction = direction.sqrMagnitude < 1e-4f ? -attack : direction.normalized;

            carrier.SetCarrier(false);
            _ball.Throw(
                carrier.Id, carrier.Position, direction * Systems_SimConstants.LATERAL_SPEED,
                Systems_PassKind.Lateral);

            return true;
        }

        private Systems_BallEvent UpdateLateral()
        {
            _ball.AdvanceFlight(Time.fixedDeltaTime);

            Vector2 position = _ball.Position;

            if (IsOutsideSidelines(position.x)
                || position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y
                || position.y <= Systems_FieldModel.OWN_BACK_LINE_Y)
            {
                // Out of bounds: the side that pitched it keeps it where it went out.
                return new Systems_BallEvent(
                    Systems_PlayOutcome.OutOfBounds, position, _ball.PossessingSide);
            }

            if (Vector2.Distance(position, _ball.ThrowOrigin)
                >= Systems_SimConstants.LATERAL_MIN_CATCH_DISTANCE)
            {
                Systems_IPlayerHandle catcher = NearestPlayer(
                    position, Systems_SimConstants.CATCH_RADIUS, _ball.ThrowerId);

                if (catcher != null)
                {
                    bool teammate = catcher.Side == _ball.PossessingSide;

                    bool held = teammate ? _skill.IsLateralCaught() : _skill.IsInterceptionHeld();

                    if (!held)
                    {
                        return Deflect(position);
                    }

                    return TakePossession(catcher, Systems_PlayOutcome.FumbleLost);
                }
            }

            // Hit the turf with nobody under it: a live ball.
            if (_ball.FlightTicks > 2 && _ball.Height <= 0f)
            {
                _ball.MakeLoose(
                    position, _ball.Velocity * Systems_SimConstants.KICK_BOUNCE_RETAINED,
                    _ball.PossessingSide);
            }

            return Systems_BallEvent.None;
        }

        // =====================================================================
        // The kick
        // =====================================================================

        /// <summary>
        /// The kick leaves the foot — or a rusher gets a hand on it first.
        ///
        /// Where it goes is <see cref="Systems_ISkillModel.DrawKick"/>'s: a ground
        /// distance, a hang time and an error off the line, from which the launch
        /// speeds are solved so the ball comes down where the draw said. A
        /// placekick is solved differently, for its CARRY — the distance at which
        /// it drops below the crossbar — because that, and the line, are what
        /// decide whether it is good.
        /// </summary>
        public void Kick(Systems_IPlayerHandle kicker)
        {
            Vector2 origin = kicker.Position;
            Systems_PlayKind kind = _play.Kind;

            kicker.SetCarrier(false);
            _play.MarkKicked(origin.y);

            if (kind != Systems_PlayKind.Kickoff && IsRusherOnTheKick(origin)
                && _skill.IsKickBlocked())
            {
                _play.MarkKickBlocked();

                // Batted back the way it came, roughly.
                Vector2 batted = Rotate(
                    Vector2.down, _skill.ScatterRadians(Systems_SimConstants.KICK_BOUNCE_SCATTER_DEGREES));

                _ball.MakeLoose(
                    origin + (Vector2.up * Systems_SimConstants.KICK_BLOCK_REACH),
                    batted * Systems_SimConstants.FUMBLE_POP_SPEED * 1.5f,
                    Systems_TeamSide.Offense);
                return;
            }

            Systems_KickDraw draw = _skill.DrawKick(kind, _play.IsOnsideKick);

            if (kind.IsPlaceKick())
            {
                Vector2 uprights = new Vector2(0f, Systems_FieldModel.ATTACKING_BACK_LINE_Y);
                Vector2 line = Rotate((uprights - origin).normalized, draw.ErrorRadians);

                float speed = Systems_SimConstants.PLACEKICK_GROUND_SPEED;
                float carrySeconds =
                    Mathf.Max(0.2f, draw.DistanceYards * Systems_FieldModel.YARD / speed);

                float vertical = (Systems_SimConstants.CROSSBAR_HEIGHT
                    - Systems_SimConstants.KICK_LAUNCH_HEIGHT
                    + (0.5f * Systems_SimConstants.KICK_GRAVITY * carrySeconds * carrySeconds))
                    / carrySeconds;

                _ball.Kick(kicker.Id, origin, line * speed, vertical, Systems_TeamSide.Offense);
                return;
            }

            Vector2 direction = Rotate(Vector2.up, draw.ErrorRadians);
            float metres = Mathf.Max(1f, draw.DistanceYards * Systems_FieldModel.YARD);
            float hang = Mathf.Max(0.3f, draw.HangSeconds);

            float upward = ((0.5f * Systems_SimConstants.KICK_GRAVITY * hang * hang)
                - Systems_SimConstants.KICK_LAUNCH_HEIGHT) / hang;

            _ball.Kick(
                kicker.Id, origin, direction * (metres / hang), upward, Systems_TeamSide.Offense);
        }

        /// <summary>Any rusher at the spot just in front of the kicker as the ball leaves the foot.</summary>
        private bool IsRusherOnTheKick(Vector2 kickerPosition)
        {
            Vector2 spot = kickerPosition + (Vector2.up * Systems_SimConstants.KICK_BLOCK_REACH);

            float radiusSquared =
                Systems_SimConstants.KICK_BLOCK_RADIUS * Systems_SimConstants.KICK_BLOCK_RADIUS;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate != null
                    && candidate.Side == Systems_TeamSide.Defense
                    && (candidate.Position - spot).sqrMagnitude <= radiusSquared)
                {
                    return true;
                }
            }

            return false;
        }

        private Systems_BallEvent UpdateKick()
        {
            Vector2 before = _ball.Position;
            _ball.AdvanceFlight(Time.fixedDeltaTime);

            if (_play.Kind.IsPlaceKick())
            {
                return UpdatePlacekick(before);
            }

            Vector2 position = _ball.Position;

            if (IsOutsideSidelines(position.x))
            {
                return KickOutOfBounds(position);
            }

            // Over the back of the end zone in the air: a touchback.
            if (position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y)
            {
                return Touchback(position);
            }

            if (_ball.Height <= Systems_SimConstants.KICK_CATCH_HEIGHT
                && _ball.VerticalVelocity < 0f)
            {
                Systems_BallEvent fielded = TryFieldKick();

                if (fielded.EndsPlay || !_ball.IsKicked)
                {
                    return fielded;
                }
            }

            if (_ball.Height <= 0f)
            {
                return LandKick(position);
            }

            return Systems_BallEvent.None;
        }

        /// <summary>
        /// Good or not is decided where the ball crosses the end line, under the
        /// uprights: between them and over the bar. Anything that lands first, or
        /// leaves the field first, is no good.
        /// </summary>
        private Systems_BallEvent UpdatePlacekick(Vector2 before)
        {
            Vector2 position = _ball.Position;
            float endLine = Systems_FieldModel.ATTACKING_BACK_LINE_Y;

            if (before.y < endLine && position.y >= endLine)
            {
                float share = (endLine - before.y) / Mathf.Max(1e-4f, position.y - before.y);
                float crossingX = Mathf.Lerp(before.x, position.x, share);

                bool good = Mathf.Abs(crossingX) <= Systems_SimConstants.GOALPOST_HALF_WIDTH
                    && _ball.Height >= Systems_SimConstants.CROSSBAR_HEIGHT;

                return new Systems_BallEvent(
                    good ? Systems_PlayOutcome.FieldGoalGood : Systems_PlayOutcome.FieldGoalMissed,
                    new Vector2(crossingX, endLine),
                    Systems_TeamSide.Offense);
            }

            if (_ball.Height <= 0f || IsOutsideSidelines(position.x))
            {
                return new Systems_BallEvent(
                    Systems_PlayOutcome.FieldGoalMissed, position, Systems_TeamSide.Offense);
            }

            return Systems_BallEvent.None;
        }

        /// <summary>
        /// The kick has come down to catching height and somebody is under it.
        ///
        /// A RECEIVER fields it — or fair-catches it, if a cover man is on him and it
        /// is a punt; or takes a knee, in his own end zone; or muffs it. A COVER MAN
        /// cannot field a punt before the receivers have touched it, so his touch
        /// downs it where it is; a kickoff that has gone its ten yards he can play,
        /// but not cleanly — it is batted to the turf and becomes a scramble.
        /// </summary>
        private Systems_BallEvent TryFieldKick()
        {
            Systems_IPlayerHandle fielder = NearestPlayer(
                _ball.Position, Systems_SimConstants.CATCH_RADIUS, _ball.ThrowerId);

            if (fielder == null)
            {
                return Systems_BallEvent.None;
            }

            if (fielder.Side == Systems_TeamSide.Defense)
            {
                _play.MarkKickTouchedByReceivers();

                float coverMetres = NearestOpponentDistance(fielder);
                bool pressured = coverMetres <= Systems_SimConstants.FAIR_CATCH_PRESSURE_METRES * 0.5f;

                if (_skill.IsKickMuffed(pressured))
                {
                    return Deflect(_ball.Position);
                }

                if (_ball.Position.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
                {
                    // Fielded in his own end zone: he takes a knee.
                    return Touchback(_ball.Position);
                }

                if (_play.Kind == Systems_PlayKind.Punt
                    && coverMetres <= Systems_SimConstants.FAIR_CATCH_PRESSURE_METRES)
                {
                    // Fair catch: his ball where he caught it, and nobody may hit him.
                    TakePossession(fielder, Systems_PlayOutcome.Punt);
                    return new Systems_BallEvent(
                        Systems_PlayOutcome.Punt, fielder.Position, Systems_TeamSide.Defense);
                }

                return TakePossession(fielder, KickReturnOutcome());
            }

            if (_play.Kind == Systems_PlayKind.Punt)
            {
                return new Systems_BallEvent(
                    Systems_PlayOutcome.Punt, ClampToField(_ball.Position), Systems_TeamSide.Defense);
            }

            if (KickTravelledYards() >= Systems_SimConstants.KICKOFF_YARDS_BEFORE_RECOVERY)
            {
                return Deflect(_ball.Position);
            }

            return Systems_BallEvent.None;
        }

        private Systems_BallEvent LandKick(Vector2 position)
        {
            if (position.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
            {
                return Touchback(position);
            }

            Vector2 bounce = Rotate(
                _ball.Velocity * Systems_SimConstants.KICK_BOUNCE_RETAINED,
                _skill.ScatterRadians(Systems_SimConstants.KICK_BOUNCE_SCATTER_DEGREES));

            _ball.MakeLoose(position, bounce, Systems_TeamSide.Offense);
            return Systems_BallEvent.None;
        }

        /// <summary>
        /// A kick out of bounds. A kickoff the receivers never touched is theirs at
        /// their own forty; anything else is theirs where it went out.
        /// </summary>
        private Systems_BallEvent KickOutOfBounds(Vector2 position)
        {
            if (_play.Kind == Systems_PlayKind.Kickoff)
            {
                if (!_play.KickTouchedByReceivers)
                {
                    return new Systems_BallEvent(
                        Systems_PlayOutcome.Kickoff,
                        new Vector2(
                            0f,
                            Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                                - (Systems_GameRules.KICKOFF_OUT_OF_BOUNDS_YARD_LINE
                                    * Systems_FieldModel.YARD)),
                        Systems_TeamSide.Defense);
                }

                return new Systems_BallEvent(
                    Systems_PlayOutcome.Kickoff, ClampToField(position), Systems_TeamSide.Defense);
            }

            if (_play.Kind.IsPlaceKick())
            {
                return new Systems_BallEvent(
                    _play.KickWasBlocked ? Systems_PlayOutcome.KickBlocked : Systems_PlayOutcome.FieldGoalMissed,
                    ClampToField(position), Systems_TeamSide.Defense);
            }

            return new Systems_BallEvent(
                Systems_PlayOutcome.Punt, ClampToField(position), Systems_TeamSide.Defense);
        }

        private static Systems_BallEvent Touchback(Vector2 position)
        {
            return new Systems_BallEvent(
                Systems_PlayOutcome.Touchback, position, Systems_TeamSide.Defense);
        }

        private float KickTravelledYards()
        {
            return (_ball.Position.y - _play.KickSpotY) / Systems_FieldModel.YARD;
        }

        private Systems_PlayOutcome KickReturnOutcome()
        {
            if (_play.KickWasBlocked)
            {
                return Systems_PlayOutcome.KickBlocked;
            }

            switch (_play.Kind)
            {
                case Systems_PlayKind.Kickoff:
                    return Systems_PlayOutcome.Kickoff;

                case Systems_PlayKind.Punt:
                    return Systems_PlayOutcome.Punt;

                default:
                    return Systems_PlayOutcome.KickBlocked;
            }
        }

        // =====================================================================
        // The loose ball
        // =====================================================================

        /// <summary>
        /// The carrier lost it: the ball pops out the way the hit sent it and is on
        /// the ground, live. Called by the referee when Systems_IFumbleModel says the
        /// tackle stripped it.
        /// </summary>
        public void Strip(Systems_IPlayerHandle carrier)
        {
            Vector2 heading = carrier.Velocity.sqrMagnitude > 0.01f
                ? carrier.Velocity.normalized
                : AttackDirectionOf(carrier.Side);

            Vector2 pop = Rotate(heading, _skill.ScatterRadians(180f))
                * Systems_SimConstants.FUMBLE_POP_SPEED;

            carrier.SetCarrier(false);
            _ball.MakeLoose(carrier.Position, pop, carrier.Side);
        }

        private Systems_BallEvent UpdateLoose()
        {
            _ball.AdvanceLoose(Time.fixedDeltaTime);
            Vector2 position = _ball.Position;

            if (IsOutsideSidelines(position.x))
            {
                return LooseOutOfBounds(position);
            }

            if (position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y
                || position.y <= Systems_FieldModel.OWN_BACK_LINE_Y)
            {
                return LooseOutTheBack(position);
            }

            // A kick nobody has had that rolls into the receivers' end zone is dead
            // there: a touchback, whoever is standing near it.
            if (_play.KickBallLive && position.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
            {
                return Touchback(position);
            }

            Systems_IPlayerHandle recoverer = NearestPlayer(
                position, Systems_SimConstants.RECOVERY_RADIUS, -1);

            if (recoverer != null && _skill.IsLooseBallRecovered())
            {
                return Recover(recoverer);
            }

            if (_ball.FlightTicks >= Systems_SimConstants.LOOSE_BALL_MAX_TICKS)
            {
                return LooseDiesWhereItLies(position);
            }

            return Systems_BallEvent.None;
        }

        /// <summary>
        /// Somebody has fallen on it. Whose ball it now is, whether the play goes on,
        /// and what it is called, all depend on whether it is still a kick.
        /// </summary>
        private Systems_BallEvent Recover(Systems_IPlayerHandle player)
        {
            Vector2 position = player.Position;
            Systems_TeamSide side = player.Side;

            if (_play.KickBallLive)
            {
                return RecoverKick(player, position, side);
            }

            Systems_TeamSide impetus = _ball.PossessingSide;

            Systems_BallEvent ended = TakePossession(player, Systems_PlayOutcome.FumbleLost);

            if (ended.EndsPlay)
            {
                return ended;
            }

            return EndZoneRuling(position, side, impetus);
        }

        private Systems_BallEvent RecoverKick(
            Systems_IPlayerHandle player, Vector2 position, Systems_TeamSide side)
        {
            if (side == Systems_TeamSide.Defense)
            {
                _play.MarkKickTouchedByReceivers();

                if (position.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
                {
                    return Touchback(position);
                }

                return TakePossession(player, KickReturnOutcome());
            }

            // THE KICKING TEAM. Behind the line it is simply the offense's ball where
            // it was recovered — a blocked kick — and the chains decide the rest.
            if (position.y < _play.LineOfScrimmageY)
            {
                TakePossession(player, Systems_PlayOutcome.FumbleLost);
                return new Systems_BallEvent(
                    Systems_PlayOutcome.Tackle, position, Systems_TeamSide.Offense);
            }

            if (_play.Kind == Systems_PlayKind.Kickoff)
            {
                bool recoverable = _play.KickTouchedByReceivers
                    || KickTravelledYardsTo(position) >= Systems_SimConstants.KICKOFF_YARDS_BEFORE_RECOVERY;

                return new Systems_BallEvent(
                    recoverable ? Systems_PlayOutcome.KickRecovered : Systems_PlayOutcome.Kickoff,
                    position,
                    recoverable ? Systems_TeamSide.Offense : Systems_TeamSide.Defense);
            }

            // A punt: the kicking team falling on a ball the receivers muffed keeps
            // it; falling on one they never touched only downs it for them.
            if (_play.KickTouchedByReceivers)
            {
                return new Systems_BallEvent(
                    Systems_PlayOutcome.KickRecovered, position, Systems_TeamSide.Offense);
            }

            return new Systems_BallEvent(
                _play.Kind.IsPlaceKick() ? Systems_PlayOutcome.FieldGoalMissed : Systems_PlayOutcome.Punt,
                position,
                Systems_TeamSide.Defense);
        }

        private float KickTravelledYardsTo(Vector2 position)
        {
            return (position.y - _play.KickSpotY) / Systems_FieldModel.YARD;
        }

        /// <summary>
        /// A ball recovered in an end zone. By the side attacking it, a touchdown.
        /// By the side defending it, a touchback if the other side put it there and
        /// a safety if its own did.
        /// </summary>
        private Systems_BallEvent EndZoneRuling(
            Vector2 position, Systems_TeamSide holder, Systems_TeamSide impetus)
        {
            if (position.y >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y)
            {
                if (holder == Systems_TeamSide.Offense)
                {
                    return new Systems_BallEvent(
                        Systems_PlayOutcome.Touchdown, position, Systems_TeamSide.Offense);
                }

                return impetus == Systems_TeamSide.Offense
                    ? Touchback(position)
                    : new Systems_BallEvent(
                        Systems_PlayOutcome.Safety, position, Systems_TeamSide.Offense);
            }

            if (position.y <= Systems_FieldModel.OWN_GOAL_LINE_Y)
            {
                if (holder == Systems_TeamSide.Defense)
                {
                    return new Systems_BallEvent(
                        Systems_PlayOutcome.Touchdown, position, Systems_TeamSide.Defense);
                }

                return impetus == Systems_TeamSide.Defense
                    ? new Systems_BallEvent(
                        Systems_PlayOutcome.Touchback, position, Systems_TeamSide.Offense)
                    : new Systems_BallEvent(
                        Systems_PlayOutcome.Safety, position, Systems_TeamSide.Defense);
            }

            return Systems_BallEvent.None;
        }

        /// <summary>
        /// A loose ball out of bounds. A kick nobody has had is the receivers' where
        /// it went out (their forty, off an untouched kickoff); anything else stays
        /// with the side that last had it.
        /// </summary>
        private Systems_BallEvent LooseOutOfBounds(Vector2 position)
        {
            if (_play.KickBallLive)
            {
                return KickOutOfBounds(position);
            }

            return new Systems_BallEvent(
                Systems_PlayOutcome.OutOfBounds, ClampToField(position), _ball.PossessingSide);
        }

        /// <summary>
        /// Out the back of an end zone: a touchback if the other side put it there,
        /// a safety if the side defending that end zone did.
        /// </summary>
        private Systems_BallEvent LooseOutTheBack(Vector2 position)
        {
            Systems_TeamSide impetus = _ball.PossessingSide;

            if (position.y >= Systems_FieldModel.ATTACKING_BACK_LINE_Y)
            {
                return impetus == Systems_TeamSide.Offense
                    ? Touchback(position)
                    : new Systems_BallEvent(
                        Systems_PlayOutcome.Safety, position, Systems_TeamSide.Offense);
            }

            return impetus == Systems_TeamSide.Defense
                ? new Systems_BallEvent(
                    Systems_PlayOutcome.Touchback, position, Systems_TeamSide.Offense)
                : new Systems_BallEvent(
                    Systems_PlayOutcome.Safety, position, Systems_TeamSide.Defense);
        }

        /// <summary>
        /// Nobody went near it. The officials blow it dead where it lies, to whoever
        /// last had it — or, for a kick nobody has had, to the receivers.
        /// </summary>
        private Systems_BallEvent LooseDiesWhereItLies(Vector2 position)
        {
            if (_play.KickBallLive)
            {
                Systems_PlayOutcome outcome = _play.Kind == Systems_PlayKind.Kickoff
                    ? Systems_PlayOutcome.Kickoff
                    : _play.Kind.IsPlaceKick()
                        ? Systems_PlayOutcome.FieldGoalMissed
                        : Systems_PlayOutcome.Punt;

                return new Systems_BallEvent(outcome, position, Systems_TeamSide.Defense);
            }

            return new Systems_BallEvent(
                Systems_PlayOutcome.Tackle, position, _ball.PossessingSide);
        }

        /// <summary>
        /// A ball that came off somebody's hands onto the turf — a muff, a dropped
        /// pitch, a cover man's touch on a kickoff. Live, slowed, with the impetus it
        /// already had.
        /// </summary>
        private Systems_BallEvent Deflect(Vector2 position)
        {
            Vector2 velocity = Rotate(
                _ball.Velocity * DEFLECTED_SPEED_RETAINED,
                _skill.ScatterRadians(Systems_SimConstants.KICK_BOUNCE_SCATTER_DEGREES));

            _ball.MakeLoose(position, velocity, _ball.PossessingSide);
            return Systems_BallEvent.None;
        }

        /// <summary>
        /// Puts the ball in <paramref name="player"/>'s hands, live. If the defense of
        /// the snap has just got it for the first time,
        /// <paramref name="howTheDefenseGotIt"/> is what the play will be called if
        /// the return ends short of a score.
        /// </summary>
        private Systems_BallEvent TakePossession(
            Systems_IPlayerHandle player, Systems_PlayOutcome howTheDefenseGotIt)
        {
            _play.EndKickBall();
            player.SetCarrier(true);
            _ball.AttachTo(player.Id, player.Position, player.Side);
            _play.SetPossession(player.Side, player.Position.y, howTheDefenseGotIt);

            return Systems_BallEvent.None;
        }

        // =====================================================================
        // Geometry
        // =====================================================================

        /// <summary>+Y for the offense of the snap, −Y for the defense.</summary>
        public static Vector2 AttackDirectionOf(Systems_TeamSide side)
        {
            return side == Systems_TeamSide.Offense ? Vector2.up : Vector2.down;
        }

        private static bool IsOutsideSidelines(float x)
        {
            return x < -Systems_FieldModel.HALF_WIDTH || x > Systems_FieldModel.HALF_WIDTH;
        }

        private static Vector2 ClampToField(Vector2 position)
        {
            return new Vector2(
                Mathf.Clamp(position.x, -Systems_FieldModel.HALF_WIDTH, Systems_FieldModel.HALF_WIDTH),
                Mathf.Clamp(
                    position.y, Systems_FieldModel.OWN_BACK_LINE_Y, Systems_FieldModel.ATTACKING_BACK_LINE_Y));
        }

        /// <summary>Nearest player of either side within <paramref name="radius"/>, skipping one slot.</summary>
        private Systems_IPlayerHandle NearestPlayer(Vector2 point, float radius, int skipSlot)
        {
            Systems_IPlayerHandle best = null;
            float bestSquared = radius * radius;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                if (slotIndex == skipSlot)
                {
                    continue;
                }

                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null)
                {
                    continue;
                }

                float squared = (candidate.Position - point).sqrMagnitude;

                if (squared <= bestSquared)
                {
                    bestSquared = squared;
                    best = candidate;
                }
            }

            return best;
        }

        private float NearestOpponentDistance(Systems_IPlayerHandle player)
        {
            float bestSquared = float.PositiveInfinity;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _registry.Get(slotIndex);

                if (candidate == null || candidate.Side == player.Side)
                {
                    continue;
                }

                float squared = (candidate.Position - player.Position).sqrMagnitude;

                if (squared < bestSquared)
                {
                    bestSquared = squared;
                }
            }

            return Mathf.Sqrt(bestSquared);
        }
    }
}
