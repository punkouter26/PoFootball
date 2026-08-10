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
            Vector2 direction = aim.sqrMagnitude < 1e-4f ? Vector2.up : aim.normalized;

            float speed = Mathf.Lerp(
                Systems_SimConstants.PASS_SPEED_MIN,
                Systems_SimConstants.PASS_SPEED_MAX,
                Mathf.Clamp01((power + 1f) * 0.5f));

            thrower.SetCarrier(false);
            _ball.Throw(thrower.Id, thrower.Position, direction * speed);
        }
    }
}
