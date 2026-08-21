using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// Moves the ball and resolves everything that can happen to it: handoffs while
    /// held, and flight, catches and interceptions once thrown.
    ///
    /// Driven explicitly from Systems_Referee.FixedTick rather than registered as
    /// its own entry point, so the ball is always resolved before the referee
    /// evaluates boundaries and the clock. Relying on entry-point registration
    /// order for that would be a silent trap.
    /// </summary>
    public sealed class Systems_BallSystem
    {
        private readonly Systems_BallModel _ball;
        private readonly Systems_PlayModel _play;
        private readonly Systems_PlayerRegistry _registry;

        public Systems_BallSystem(
            Systems_BallModel ball,
            Systems_PlayModel play,
            Systems_PlayerRegistry registry)
        {
            _ball = ball;
            _play = play;
            _registry = registry;
        }

        /// <summary>
        /// Advances the ball one physics tick and reports any outcome that ends the
        /// play. Returns None while the play should continue.
        /// </summary>
        public Systems_PlayOutcome Tick()
        {
            if (_ball.IsHeld)
            {
                UpdateHeldBall();
                return Systems_PlayOutcome.None;
            }

            if (_ball.IsInFlight)
            {
                return UpdateFlight();
            }

            return Systems_PlayOutcome.None;
        }

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
            _ball.AttachTo(targetSlot, target.Position);
        }

        private Systems_PlayOutcome UpdateFlight()
        {
            _ball.AdvanceFlight(Time.fixedDeltaTime);

            Systems_IPlayerHandle catcher = FindCatcher();

            if (catcher != null)
            {
                bool intercepted = catcher.Side == Systems_TeamSide.Defense;

                catcher.SetCarrier(true);
                _ball.AttachTo(catcher.Id, catcher.Position);

                if (!intercepted)
                {
                    _play.MarkPassCompleted();
                }

                // An offensive catch is a completion and the play continues. A
                // defensive catch is a turnover and ends it.
                return intercepted
                    ? Systems_PlayOutcome.Interception
                    : Systems_PlayOutcome.None;
            }

            if (_ball.FlightTicks >= Systems_SimConstants.MAX_FLIGHT_TICKS)
            {
                _ball.MarkIncomplete();
                return Systems_PlayOutcome.Incompletion;
            }

            return Systems_PlayOutcome.None;
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

            // AND IT MUST CLEAR THE LINE OF SCRIMMAGE. This is the rule that makes
            // a passing game possible at all, and its absence produced a game of
            // forty plays in which thirty-six were interceptions and not one pass
            // was completed.
            //
            // The arithmetic: the quarterback drops DROPBACK_DEPTH_YARDS (7 yd,
            // 6.4 m) behind the line, so MIN_CATCH_DISTANCE — measured from the
            // THROW ORIGIN — expires while the ball is still three and a half metres
            // BEHIND the line of scrimmage. The next bodies it meets are the two
            // lines, and a 25 m/s throw is only about 1.7 m up as it crosses them,
            // which FindCatcher could not tell anyway because it compares plane
            // distance and never reads Height. So the first eligible catcher on
            // every single pass was a defensive lineman standing in the trenches.
            //
            // It went unnoticed while offensive linemen were eligible receivers:
            // they stand in front of the defensive line and simply won the
            // nearest-body contest, turning those throws into bizarre completions to
            // a guard instead of interceptions. Making them ineligible — which is
            // correct, they are ineligible receivers — handed every one of those
            // balls to the defender standing behind them.
            //
            // Requiring the ball to be past the line models it flying over the
            // trenches, which is what actually happens on a forward pass. A defender
            // who has dropped into coverage downfield can still take it.
            if (_ball.Position.y < _play.LineOfScrimmageY)
            {
                return null;
            }

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
                // Excluding them from the TARGET list was not enough on its own:
                // this method hands the ball to whoever is nearest inside the catch
                // radius, and a pass thrown over the middle passes directly through
                // the five bodies standing at the line — so the guard was catching
                // throws that were aimed past him. Only the offensive line is
                // filtered; DefensiveLine is a separate role and keeps its
                // interception, which is the whole reason a rusher batting a ball
                // down has to stay possible.
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
        /// </summary>
        public void Throw(Systems_IPlayerHandle thrower, Vector2 aim, float power)
        {
            float speed = Mathf.Lerp(
                Systems_SimConstants.PASS_SPEED_MIN,
                Systems_SimConstants.PASS_SPEED_MAX,
                Mathf.Clamp01((power + 1f) * 0.5f));

            Vector2 direction = ResolveThrowDirection(thrower, aim, speed);

            thrower.SetCarrier(false);
            _ball.Throw(thrower.Id, thrower.Position, direction * speed);
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
        /// Every throw therefore goes to someone on the throwing side — it can still
        /// be covered, late, or picked off, which is football, but it can no longer
        /// be thrown at nobody. A degenerate or zero aim now picks whichever
        /// receiver is most nearly straight downfield instead of firing at the
        /// sideline.
        ///
        /// Linemen are excluded because they are ineligible receivers, and the
        /// thrower is excluded because it cannot catch its own pass — the same rule
        /// FindCatcher already enforces on the receiving end.
        /// </summary>
        private Vector2 ResolveThrowDirection(
            Systems_IPlayerHandle thrower, Vector2 aim, float speed)
        {
            Vector2 intent = aim.sqrMagnitude < 1e-4f ? Vector2.up : aim.normalized;

            Systems_IPlayerHandle target = null;
            float bestOffRay = float.PositiveInfinity;
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
                // BEARING matches.
                //
                // Scoring by bearing alone — a bare dot product — ignores distance
                // entirely, so of two receivers nearly in line with the aim the
                // deeper one wins on a rounding error. Every checkdown became a
                // bomb, and a game of forty plays produced twelve touchdowns and
                // twenty-seven interceptions with nothing whatsoever in between:
                // either the deep man was uncovered or a safety took it.
                //
                // Perpendicular offset from the ray answers the question actually
                // being asked — WHICH receiver was this thrown at — and a near
                // target squarely on the ray now beats a distant one merely close
                // to its bearing.
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
            return target == null ? intent : bestLead.normalized;
        }

        /// <summary>
        /// Where the receiver will be when the ball arrives. One pass of
        /// distance-over-speed is enough: the correction is small relative to the
        /// catch radius, and iterating it would chase a moving target for no
        /// visible gain.
        /// </summary>
        private static Vector2 LeadPoint(
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
    }
}
