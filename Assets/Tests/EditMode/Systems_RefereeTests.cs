using MessagePipe;
using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    public sealed class Systems_RefereeTests
    {
        private sealed class StubPublisher<T> : IPublisher<T>
        {
            public int Count { get; private set; }

            public T Last { get; private set; }

            public void Publish(T message)
            {
                Count++;
                Last = message;
            }
        }

        private sealed class StubPlayer : Systems_IPlayerHandle
        {
            public int Id { get; set; }

            public Systems_PlayerRole Role { get; set; }

            public Systems_TeamSide Side => Systems_RoleTable.SideOf(Role);

            public Vector2 Position { get; set; }

            public Vector2 Velocity { get; set; }

            public bool IsCarrier { get; private set; }

            /// <summary>
            /// Presentation-only on the real agent, so the stub reports a body
            /// that never tires. Nothing the referee does reads it.
            /// </summary>
            public float Fatigue => 0f;

            public void ClearFatigue() { }

            public void Freeze() { }

            public void ResetTo(Vector2 position) => Position = position;

            // No ApplyTerminalReward or EndEpisodeNow: those moved to
            // Systems_ITrainingHandle, which this stub deliberately does not
            // implement. A referee test has no business knowing about episodes.

            public void SetCarrier(bool isCarrier) => IsCarrier = isCarrier;

            public void SetTeamColor(Color color)
            {
            }
        }

        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_BallSystem _ballSystem;
        private Systems_PlayerRegistry _registry;
        private StubPublisher<Systems_PlayEndedMessage> _ended;
        private StubPublisher<Systems_TackleMessage> _tackled;
        private StubPublisher<Systems_ScoreMessage> _scored;
        private Systems_Referee _referee;
        private StubPlayer _quarterback;

        [SetUp]
        public void SetUp()
        {
            _play = new Systems_PlayModel();
            _ball = new Systems_BallModel();
            _registry = new Systems_PlayerRegistry();

            for (int slot = 0; slot < Systems_PlayerRegistry.CAPACITY; slot++)
            {
                Systems_FormationSlot definition = Systems_Formation.GetSlot(slot);
                StubPlayer player = new StubPlayer
                {
                    Id = slot,
                    Role = definition.Role,
                    Position = new Vector2(definition.OffsetX, definition.OffsetY)
                };
                _registry.Register(slot, player);
            }

            _quarterback = (StubPlayer)_registry.Get(Systems_Formation.QUARTERBACK_SLOT_INDEX);

            _ballSystem = new Systems_BallSystem(_ball, _play, _registry);
            _ended = new StubPublisher<Systems_PlayEndedMessage>();
            _tackled = new StubPublisher<Systems_TackleMessage>();
            _scored = new StubPublisher<Systems_ScoreMessage>();

            // The DETERMINISTIC model, deliberately: these tests pin the kicking
            // rules a policy is fitted against, and a sampled curve would make them
            // flaky for no gain. Systems_ProbabilisticKickModel is a Game-mode
            // concern — see Systems_IKickModel.
            // Same reasoning for the fumble model: the deterministic one is a pure
            // function of the hit, so a tackle test cannot randomly become a
            // turnover. Systems_ProbabilisticFumbleModel is Game-mode only.
            _referee = new Systems_Referee(
                _play, _ball, _ballSystem, new Systems_FieldModel(), _registry,
                new Systems_DeterministicKickModel(),
                new Systems_DeterministicFumbleModel(),
                _ended, _tackled, _scored);

            _play.BeginEpisode(0f, _quarterback.Position.y, 1, Systems_GameRules.YARDS_TO_GAIN);
            _play.Snap();
            _quarterback.SetCarrier(true);
            _ball.AttachTo(Systems_Formation.QUARTERBACK_SLOT_INDEX, _quarterback.Position);
            _referee.ResetContactTracking();
        }

        // --- Possession -------------------------------------------------------

        [Test]
        public void TheQuarterbackStartsEveryPlayWithTheBall()
        {
            Assert.That(_ball.IsHeld, Is.True);
            Assert.That(_ball.CarrierId, Is.EqualTo(Systems_Formation.QUARTERBACK_SLOT_INDEX));
            Assert.That(_quarterback.IsCarrier, Is.True);
        }

        [Test]
        public void ThePlayCall_LatchesOnceAndIgnoresLaterChanges()
        {
            _play.LatchCall(Systems_PlayCall.HandoffHalfback);
            _play.LatchCall(Systems_PlayCall.Pass);

            Assert.That(_play.Call, Is.EqualTo(Systems_PlayCall.HandoffHalfback));
        }

        [Test]
        public void AHandoffCompletes_WhenTheQuarterbackReachesTheBack()
        {
            _play.LatchCall(Systems_PlayCall.HandoffHalfback);

            StubPlayer halfback = (StubPlayer)_registry.Get(Systems_Formation.HALFBACK_SLOT_INDEX);
            _quarterback.Position = halfback.Position;

            _referee.FixedTick();

            Assert.That(_ball.CarrierId, Is.EqualTo(Systems_Formation.HALFBACK_SLOT_INDEX));
            Assert.That(halfback.IsCarrier, Is.True);
            Assert.That(_quarterback.IsCarrier, Is.False);
        }

        [Test]
        public void AHandoffDoesNotHappen_WhenTheBackIsOutOfReach()
        {
            _play.LatchCall(Systems_PlayCall.HandoffFullback);

            StubPlayer fullback = (StubPlayer)_registry.Get(Systems_Formation.FULLBACK_SLOT_INDEX);
            fullback.Position = _quarterback.Position
                + new Vector2(0f, Systems_SimConstants.HANDOFF_RADIUS + 2f);

            _referee.FixedTick();

            Assert.That(_ball.CarrierId, Is.EqualTo(Systems_Formation.QUARTERBACK_SLOT_INDEX));
        }

        [Test]
        public void NoHandoffHappens_OnAPassCall()
        {
            _play.LatchCall(Systems_PlayCall.Pass);

            StubPlayer halfback = (StubPlayer)_registry.Get(Systems_Formation.HALFBACK_SLOT_INDEX);
            _quarterback.Position = halfback.Position;

            _referee.FixedTick();

            Assert.That(_ball.CarrierId, Is.EqualTo(Systems_Formation.QUARTERBACK_SLOT_INDEX));
        }

        // --- Passing ----------------------------------------------------------

        [Test]
        public void AnUncaughtPass_IsRuledIncomplete()
        {
            _quarterback.Position = new Vector2(0f, 0f);
            MoveEveryoneFarAway();

            _ballSystem.Throw(_quarterback, Vector2.left, 0f);

            for (int tick = 0; tick <= Systems_SimConstants.MAX_FLIGHT_TICKS + 2; tick++)
            {
                _referee.FixedTick();
                if (_play.Phase == Systems_PlayPhase.Dead)
                {
                    break;
                }
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
        }

        [Test]
        public void ADefenderCatchingAPass_IsAnInterception()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            // A RECEIVER HAS TO BE DOWNFIELD FOR THIS TO BE A PASS AT ALL. The aim
            // vector stopped being a raw direction in contract revision 5 — it names
            // whichever eligible receiver best matches the bearing — so throwing at
            // an empty field now picks some far-flung target and the ball never
            // crosses the defender. Putting the intended receiver behind the safety
            // is what the play being tested actually looks like: a throw to a covered
            // man, jumped by the man covering him.
            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 20f);

            StubPlayer safety = (StubPlayer)_registry.Get(20);
            safety.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60; tick++)
            {
                _referee.FixedTick();
                if (_play.Phase == Systems_PlayPhase.Dead)
                {
                    break;
                }
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Interception));
        }

        [Test]
        public void AReceiverCatchingAPass_KeepsThePlayAlive()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60; tick++)
            {
                _referee.FixedTick();
                if (_ball.IsHeld)
                {
                    break;
                }
            }

            Assert.That(_ball.IsHeld, Is.True, "the receiver should have caught it");
            Assert.That(_ball.CarrierId, Is.EqualTo(6));
            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live), "a completion is not a whistle");
        }

        /// <summary>
        /// Regression test for run football_base03. Without a minimum travel
        /// distance a throw resolved on its first tick into whichever lineman
        /// stood beside the quarterback — a "completion" that was really a
        /// handoff, and one that could never fall incomplete or be intercepted.
        /// </summary>
        [Test]
        public void APassIsNotCaughtByAPlayerStandingInThePocket()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer lineman = (StubPlayer)_registry.Get(2);
            lineman.Position = new Vector2(0.4f, 0.4f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);
            _referee.FixedTick();

            Assert.That(_ball.IsInFlight, Is.True, "the ball should still be live");
            Assert.That(lineman.IsCarrier, Is.False, "an adjacent lineman must not catch it");
        }

        [Test]
        public void ACompletionIsRecordedOnThePlayModel()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 8f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60 && !_ball.IsHeld; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsHeld, Is.True);
            Assert.That(_play.PassCompleted, Is.True);
        }

        [Test]
        public void AThrowerCannotCatchItsOwnPass()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);
            _referee.FixedTick();

            Assert.That(_ball.CarrierId, Is.Not.EqualTo(Systems_Formation.QUARTERBACK_SLOT_INDEX));
        }

        [Test]
        public void ABallInFlightCannotBeTackled()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            _ballSystem.Throw(_quarterback, Vector2.left, 0f);
            _referee.ReportContactWithCarrier(11, 99f);

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
            Assert.That(_play.Outcome, Is.Not.EqualTo(Systems_PlayOutcome.Tackle));
        }

        // --- Tackles ----------------------------------------------------------

        /// <summary>Acceptance criterion #2.</summary>
        [Test]
        public void ImpactAboveTheThreshold_EndsThePlayImmediately()
        {
            _referee.ReportContactWithCarrier(
                11, Systems_SimConstants.TACKLE_CLOSING_SPEED + 0.5f);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
            Assert.That(_tackled.Count, Is.EqualTo(1));
        }

        /// <summary>Acceptance criterion #3 — the negative test.</summary>
        [Test]
        public void ImpactBelowTheThreshold_DoesNotEndThePlay()
        {
            _referee.ReportContactWithCarrier(
                11, Systems_SimConstants.TACKLE_CLOSING_SPEED - 0.5f);

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
            Assert.That(_ended.Count, Is.EqualTo(0));
        }

        /// <summary>
        /// The wrap-up rule. Regression test for run football_base01, where a
        /// defender could chase the carrier at matched speed — near-zero relative
        /// velocity — and never end the play, so no play ever ended in a tackle.
        /// </summary>
        [Test]
        public void ContactSustainedAtZeroClosingSpeed_EventuallyEndsThePlay()
        {
            for (int tick = 0; tick < Systems_SimConstants.SUSTAINED_TACKLE_TICKS; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
        }

        [Test]
        public void BriefContact_DoesNotEndThePlay()
        {
            for (int tick = 0; tick < Systems_SimConstants.SUSTAINED_TACKLE_TICKS - 1; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
        }

        /// <summary>
        /// THIS TEST USED TO ASSERT THE OPPOSITE, and the rule it pinned is the one
        /// that made a measured game average 9.3 yards a play. Three defenders
        /// wrapping a carrier up were collapsed into "one tick of contact", so help
        /// arriving was worth precisely nothing and a back only ever had to beat one
        /// man. In real football the second defender is the whole point.
        ///
        /// The wrap-up is now divided between everyone with a hand on him, so a
        /// gang tackle ends the down in a fraction of the ticks one defender needs.
        /// </summary>
        [Test]
        public void SeveralDefendersOnOneTick_BringTheCarrierDownFaster()
        {
            int alone = Systems_RoleTable.TackleTicksOf(_quarterback.Role);

            for (int tick = 0; tick < alone - 1; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
                _referee.ReportSustainedContact(12, 0f);
                _referee.ReportSustainedContact(13, 0f);

                if (_play.Phase == Systems_PlayPhase.Dead)
                {
                    Assert.That(
                        tick + 1,
                        Is.LessThan(alone),
                        "three tacklers must need fewer ticks than one");
                    return;
                }
            }

            Assert.Fail(
                $"three tacklers failed to bring the carrier down inside {alone} ticks");
        }

        /// <summary>
        /// The other half of the same rule: one defender still has to do the whole
        /// job, so the division above cannot be reached by a single man hanging on.
        /// </summary>
        [Test]
        public void OneDefender_StillNeedsTheFullWrapUp()
        {
            int needed = Systems_RoleTable.TackleTicksOf(_quarterback.Role);

            for (int tick = 0; tick < needed - 1; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
        }

        /// <summary>
        /// Contact survives the separation the collision impulse itself causes. Two
        /// discs meeting push each other apart, so requiring strictly consecutive
        /// ticks meant a tackle that had genuinely been made reset to zero and the
        /// carrier ran on.
        /// </summary>
        [Test]
        public void ContactBrokenForATickOrTwo_DoesNotRestartTheWrapUp()
        {
            int needed = Systems_RoleTable.TackleTicksOf(_quarterback.Role);

            // One tick of contact, then a gap inside the grace window, then contact
            // again — the run must carry across the gap rather than restarting.
            for (int tick = 0; tick < needed * 3; tick++)
            {
                _referee.FixedTick();

                bool skip = tick % 3 == 1;

                if (!skip)
                {
                    _referee.ReportSustainedContact(11, 0f);
                }

                if (_play.Phase == Systems_PlayPhase.Dead)
                {
                    return;
                }
            }

            Assert.Fail("a wrap-up broken only by the collision bounce never completed");
        }

        // --- Boundaries and clock --------------------------------------------

        /// <summary>Acceptance criterion #6.</summary>
        [Test]
        public void RunningOutTheClock_EndsAsTimeExpired()
        {
            for (int tick = 0; tick < Systems_PlayModel.MAX_PHYSICS_TICKS; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.TimeExpired));
        }

        /// <summary>Acceptance criterion #4.</summary>
        [Test]
        public void CrossingTheGoalLine_ScoresExactlyOnce()
        {
            _quarterback.Position =
                new Vector2(0f, Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 0.5f);

            _referee.FixedTick();
            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Touchdown));
            Assert.That(_scored.Count, Is.EqualTo(1));
        }

        /// <summary>Acceptance criterion #5.</summary>
        [Test]
        public void CrossingASideline_EndsOutOfBounds()
        {
            _quarterback.Position = new Vector2(Systems_FieldModel.HALF_WIDTH + 1f, 0f);

            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.OutOfBounds));
        }

        [Test]
        public void DeadBallSpot_IsAlwaysInsideTheField()
        {
            _quarterback.Position = new Vector2(9999f, 9999f);

            _referee.FixedTick();

            Assert.That(
                Mathf.Abs(_play.DeadBallSpot.x),
                Is.LessThanOrEqualTo(Systems_FieldModel.HALF_WIDTH + 1e-3f));
        }

        [Test]
        public void TicksDoNotAdvance_WhileThePlayIsDead()
        {
            _referee.FixedTick();
            _referee.ReportContactWithCarrier(11, 5f);

            int ticksAtWhistle = _play.PhysicsTick;
            _referee.FixedTick();
            _referee.FixedTick();

            Assert.That(_play.PhysicsTick, Is.EqualTo(ticksAtWhistle));
        }

        /// <summary>Parks everyone well clear so catch resolution is unambiguous.</summary>
        private void MoveEveryoneFarAway()
        {
            for (int slot = 0; slot < Systems_PlayerRegistry.CAPACITY; slot++)
            {
                if (slot == Systems_Formation.QUARTERBACK_SLOT_INDEX)
                {
                    continue;
                }

                ((StubPlayer)_registry.Get(slot)).Position = new Vector2(-200f, -200f + slot);
            }
        }
    }
}
