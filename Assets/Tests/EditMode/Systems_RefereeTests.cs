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

            public void RestBetweenPlays() { }

            public void Freeze() { }

            public void ResetTo(Vector2 position) => Position = position;

            // No ApplyTerminalReward or EndEpisodeNow: those moved to
            // Systems_ITrainingHandle, which this stub deliberately does not
            // implement. A referee test has no business knowing about episodes.

            public void SetCarrier(bool isCarrier) => IsCarrier = isCarrier;

            public void SetTeamColor(Color color)
            {
            }

            public void Nudge(Vector2 velocity) => Velocity = velocity;
        }

        /// <summary>
        /// Every skill succeeds unless a test says otherwise, so the suite still
        /// pins the RULES — where a catch, a break-up and a tackle are possible —
        /// and the tests at the bottom pin what happens when one is not made.
        /// Systems_SeededSkillModel's curves are pinned without sampling them.
        /// </summary>
        private sealed class StubSkill : Systems_ISkillModel
        {
            public bool CatchHeld = true;

            public bool BrokenUp = true;

            public bool InterceptionHeld = true;

            public bool HitMade = true;

            /// <summary>How many wrap-ups the carrier breaks before one holds.</summary>
            public int WrapUpsToBreak;

            public int WrapUpRolls { get; private set; }

            public bool LastWrapUpWasArmOnly { get; private set; }

            public float ThrowErrorRadians(float throwMetres, float throwerFatigue) => 0f;

            public bool IsCatchHeld(float airMetres, float catcherFatigue) => CatchHeld;

            public bool IsPassBrokenUp(float defenderMetres) => BrokenUp;

            public bool IsInterceptionHeld() => InterceptionHeld;

            public bool IsHitTackleMade(
                Systems_PlayerRole carrier, float tacklerFatigue, float carrierFatigue) => HitMade;

            public bool IsWrapUpHeld(
                Systems_PlayerRole carrier, int tacklerCount, bool armOnly,
                float tacklerFatigue, float carrierFatigue)
            {
                WrapUpRolls++;
                LastWrapUpWasArmOnly = armOnly;
                return WrapUpRolls > WrapUpsToBreak;
            }

            // Revision 14. Every kick goes exactly where it is aimed, nothing is
            // blocked or muffed or dropped, and a loose ball is fallen on at once —
            // the rules, without the luck. Tests that want the luck set these.
            public float KickDistanceYards = -1f;

            public bool KickBlocked;

            public bool KickMuffed;

            public bool LooseBallRecovered = true;

            public bool LateralCaught = true;

            public Systems_KickDraw DrawKick(Systems_PlayKind kind, bool onside)
            {
                float distance = KickDistanceYards;

                if (distance < 0f)
                {
                    distance = kind == Systems_PlayKind.Punt
                        ? Systems_SimConstants.PUNT_DISTANCE_YARDS
                        : kind == Systems_PlayKind.Kickoff
                            ? (onside
                                ? Systems_SimConstants.ONSIDE_DISTANCE_YARDS
                                : Systems_SimConstants.KICKOFF_DISTANCE_YARDS)
                            : Systems_SimConstants.KICKER_CARRY_YARDS;
                }

                float hang = kind == Systems_PlayKind.Punt
                    ? Systems_SimConstants.PUNT_HANG_SECONDS
                    : onside
                        ? Systems_SimConstants.ONSIDE_HANG_SECONDS
                        : Systems_SimConstants.KICKOFF_HANG_SECONDS;

                return new Systems_KickDraw(distance, hang, 0f);
            }

            public bool IsKickBlocked() => KickBlocked;

            public bool IsKickMuffed(bool pressured) => KickMuffed;

            public bool IsLooseBallRecovered() => LooseBallRecovered;

            public bool IsLateralCaught() => LateralCaught;

            public float ScatterRadians(float maxDegrees) => 0f;
        }

        private StubSkill _skill;
        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_BallSystem _ballSystem;
        private Systems_PlayerRegistry _registry;
        private StubPublisher<Systems_PlayEndedMessage> _ended;
        private StubPublisher<Systems_TackleMessage> _tackled;
        private StubPublisher<Systems_ScoreMessage> _scored;
        private StubPublisher<Systems_PassThrownMessage> _thrown;
        private StubPublisher<Systems_PassCaughtMessage> _caught;
        private Systems_Referee _referee;
        private Systems_PenaltySystem _penalties;
        private StubPlayer _quarterback;

        [SetUp]
        public void SetUp()
        {
            _play = new Systems_PlayModel();
            _ball = new Systems_BallModel();
            _registry = new Systems_PlayerRegistry();

            // A freshly constructed selection is the base I-formation against the
            // 4-3 — exactly the alignment this suite was written against before
            // formations became a set, so every expectation below still holds.
            Systems_FormationSelection formations = new Systems_FormationSelection();

            for (int slot = 0; slot < Systems_PlayerRegistry.CAPACITY; slot++)
            {
                Systems_FormationSlot definition = formations.GetSlot(slot);
                StubPlayer player = new StubPlayer
                {
                    Id = slot,
                    Role = definition.Role,
                    Position = new Vector2(definition.OffsetX, definition.OffsetY)
                };
                _registry.Register(slot, player);
            }

            _quarterback = (StubPlayer)_registry.Get(Systems_Formation.QUARTERBACK_SLOT_INDEX);

            _thrown = new StubPublisher<Systems_PassThrownMessage>();
            _caught = new StubPublisher<Systems_PassCaughtMessage>();
            _skill = new StubSkill();
            _ballSystem = new Systems_BallSystem(
                _ball, _play, _registry, _skill, _thrown, _caught);
            _ended = new StubPublisher<Systems_PlayEndedMessage>();
            _tackled = new StubPublisher<Systems_TackleMessage>();
            _scored = new StubPublisher<Systems_ScoreMessage>();

            // The DETERMINISTIC fumble model, deliberately: it is a pure function
            // of the hit, so a tackle test cannot randomly become a turnover.
            // Systems_ProbabilisticFumbleModel is Game-mode only. The same goes for
            // the pre-snap discipline model — nobody ever jumps here.
            _penalties = new Systems_PenaltySystem(_play, _ball, _registry);
            _penalties.BeginPlay();

            _referee = new Systems_Referee(
                _play, _ball, _ballSystem, new Systems_FieldModel(), _registry,
                new Systems_DeterministicFumbleModel(),
                _skill,
                _penalties,
                new Systems_NoDisciplineModel(),
                _ended, _tackled, _scored);

            _play.BeginEpisode(0f, _quarterback.Position.y, 1, Systems_GameRules.YARDS_TO_GAIN);
            _play.Snap();
            _quarterback.SetCarrier(true);
            _ball.ResetForSnap(Systems_Formation.QUARTERBACK_SLOT_INDEX, _quarterback.Position);
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

            // Since revision 14 the pick is returned; this safety stands still with
            // nobody near him, so he gives himself up a moment later — and the play
            // is reported as the interception it was.
            for (int tick = 0; tick < 200; tick++)
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

        // --- Pass announcements -----------------------------------------------
        //
        // Presentation taps: the camera frames the receiver named on the release,
        // and the catch is the only thing that tells a viewer a completion
        // happened before the tackle does. Neither changes a rule, so these pin
        // only what the subscribers rely on.

        [Test]
        public void AThrow_AnnouncesTheReceiverItWasResolvedTo()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 12f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            Assert.That(_thrown.Count, Is.EqualTo(1));
            Assert.That(
                _thrown.Last.ThrowerId, Is.EqualTo(Systems_Formation.QUARTERBACK_SLOT_INDEX));
            Assert.That(_thrown.Last.TargetId, Is.EqualTo(6));
            Assert.That(
                _thrown.Last.Speed, Is.EqualTo(Systems_BallSystem.ThrowSpeedFor(0f)).Within(1e-4f));
        }

        [Test]
        public void ACompletion_IsAnnouncedOnce_WithTheDistanceTheBallFlew()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 9f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60 && !_ball.IsHeld; tick++)
            {
                _referee.FixedTick();
            }

            // A few more ticks with the receiver now carrying: the announcement
            // belongs to the catch, not to every tick the ball is held after it.
            for (int tick = 0; tick < 5; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_caught.Count, Is.EqualTo(1));
            Assert.That(_caught.Last.CatcherId, Is.EqualTo(6));
            Assert.That(_caught.Last.Intercepted, Is.False);

            // Caught somewhere inside the catch radius short of the receiver, so
            // the throw is a little under his nine metres and never over it.
            float receiverYards = 9f / Systems_FieldModel.YARD;
            float radiusYards = Systems_SimConstants.CATCH_RADIUS / Systems_FieldModel.YARD;

            Assert.That(_caught.Last.AirYards, Is.LessThanOrEqualTo(receiverYards));
            Assert.That(
                _caught.Last.AirYards, Is.GreaterThanOrEqualTo(receiverYards - radiusYards - 1f));
        }

        [Test]
        public void AnInterception_IsAnnouncedAsOne()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 20f);

            StubPlayer safety = (StubPlayer)_registry.Get(20);
            safety.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60 && _play.Phase != Systems_PlayPhase.Dead; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_caught.Count, Is.EqualTo(1));
            Assert.That(_caught.Last.CatcherId, Is.EqualTo(20));
            Assert.That(_caught.Last.Intercepted, Is.True);
        }

        [Test]
        public void AnIncompletion_AnnouncesNoCatch()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            _ballSystem.Throw(_quarterback, Vector2.left, 0f);

            for (int tick = 0;
                 tick <= Systems_SimConstants.MAX_FLIGHT_TICKS + 2
                 && _play.Phase != Systems_PlayPhase.Dead;
                 tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_thrown.Count, Is.EqualTo(1));
            Assert.That(_caught.Count, Is.EqualTo(0));
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

        /// <summary>
        /// Revision 14: a carrier standing still with nobody near him has given
        /// himself up — down where he is, the way a quarterback who takes a knee is.
        /// It used to run to the trainer's ceiling and be called TimeExpired.
        /// </summary>
        [Test]
        public void AStationaryCarrierWithADefenderOnHim_IsDownWhereHeStopped()
        {
            StubPlayer hovering = (StubPlayer)_registry.Get(Systems_PlayerRegistry.CAPACITY - 1);
            hovering.Position = _quarterback.Position + new Vector2(2.5f, 0f);

            for (int tick = 0; tick < Systems_PlayModel.MAX_PHYSICS_TICKS; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
        }

        [Test]
        public void AStationaryCarrierWithNobodyNear_GivesHimselfUp()
        {
            for (int tick = 0; tick < Systems_PlayModel.MAX_PHYSICS_TICKS; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Downed));
        }

        /// <summary>
        /// Acceptance criterion #6. A carrier who keeps moving with nobody laying a
        /// hand on him is a live play, and only the trainer's ceiling ends it.
        /// </summary>
        [Test]
        public void RunningOutTheClock_EndsAsTimeExpired()
        {
            MoveEveryoneFarAway();
            _quarterback.Velocity = new Vector2(Systems_SimConstants.STALL_SPEED * 2f, 0f);

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

        // --- Skill error (revision 13) ---------------------------------------
        //
        // Being in position used to be the whole of every skill. These pin what
        // happens when the man in position does not make the play.

        [Test]
        public void AMissedHit_DoesNotEndThePlay()
        {
            _skill.HitMade = false;

            _referee.ReportContactWithCarrier(
                11, Systems_SimConstants.TACKLE_CLOSING_SPEED + 0.5f);

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
            Assert.That(_tackled.Count, Is.EqualTo(0));
        }

        [Test]
        public void ABrokenWrapUp_HasToBeMadeAllOverAgain()
        {
            int needed = Systems_RoleTable.TackleTicksOf(_quarterback.Role);
            _skill.WrapUpsToBreak = 1;

            for (int tick = 0; tick < needed; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(_skill.WrapUpRolls, Is.EqualTo(1));
            Assert.That(
                _play.Phase, Is.EqualTo(Systems_PlayPhase.Live), "he broke the first one");

            for (int tick = 0; tick < needed - 1; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(
                _play.Phase, Is.EqualTo(Systems_PlayPhase.Live),
                "a broken wrap-up must restart the count, not resume it");

            _referee.FixedTick();
            _referee.ReportSustainedContact(11, 0f);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
            Assert.That(_skill.WrapUpRolls, Is.EqualTo(2), "one roll per wrap-up, not per tick");
        }

        [Test]
        public void AWrapUpWithABodyOnHim_IsNotAnArmTackle()
        {
            int needed = Systems_RoleTable.TackleTicksOf(_quarterback.Role);

            for (int tick = 0; tick < needed; tick++)
            {
                _referee.FixedTick();
                _referee.ReportSustainedContact(11, 0f);
            }

            Assert.That(_skill.WrapUpRolls, Is.EqualTo(1));
            Assert.That(_skill.LastWrapUpWasArmOnly, Is.False);
        }

        /// <summary>
        /// A defender within reach who never touches the carrier — the dive at his
        /// ankles. It still brings him down, and it is rolled as the weaker tackle.
        /// </summary>
        [Test]
        public void AWrapUpOnReachAlone_IsAnArmTackle()
        {
            MoveEveryoneFarAway();

            StubPlayer defender = (StubPlayer)_registry.Get(11);
            defender.Position = _quarterback.Position
                + new Vector2(Systems_SimConstants.REACH_TACKLE_RANGE * 0.5f, 0f);

            for (int tick = 0;
                 tick < Systems_SimConstants.STALL_TICKS && _play.Phase != Systems_PlayPhase.Dead;
                 tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
            Assert.That(_skill.LastWrapUpWasArmOnly, Is.True);
        }

        [Test]
        public void ADroppedPass_IsIncomplete()
        {
            _skill.CatchHeld = false;
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 8f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);
            RunUntilTheBallIsDown();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
            Assert.That(receiver.IsCarrier, Is.False);
            Assert.That(_caught.Count, Is.EqualTo(0));
        }

        [Test]
        public void ADroppedInterception_IsIncomplete()
        {
            _skill.InterceptionHeld = false;
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 20f);

            StubPlayer safety = (StubPlayer)_registry.Get(20);
            safety.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);
            RunUntilTheBallIsDown();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
        }

        /// <summary>
        /// The revision 11 rule, kept: a defender beside the ball, not close enough
        /// to catch it himself, takes it away from a receiver who is.
        /// </summary>
        [Test]
        public void ACoveredCatch_IsBrokenUp_WhenTheDefenderGetsAHandOnIt()
        {
            ThrowAtACoveredReceiver();
            RunUntilTheBallIsDown();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
        }

        [Test]
        public void ACoveredCatch_IsMade_WhenTheDefenderDoesNot()
        {
            _skill.BrokenUp = false;

            ThrowAtACoveredReceiver();
            RunUntilTheBallIsDown();

            Assert.That(_ball.IsHeld, Is.True);
            Assert.That(_ball.CarrierId, Is.EqualTo(6));
        }

        // --- The curves Systems_SeededSkillModel draws against ----------------

        [Test]
        public void ThrowError_GrowsWithDistanceAndFatigue_AndStaysInsideItsSpread()
        {
            float near = Systems_SeededSkillModel.ThrowSpreadDegrees(5f, 0f);
            float far = Systems_SeededSkillModel.ThrowSpreadDegrees(35f, 0f);
            float tired = Systems_SeededSkillModel.ThrowSpreadDegrees(35f, 1f);

            Assert.That(far, Is.GreaterThan(near));
            Assert.That(tired, Is.GreaterThan(far));

            Systems_SeededSkillModel model = new Systems_SeededSkillModel();
            float limit = far * Mathf.Deg2Rad;
            bool anyError = false;

            for (int draw = 0; draw < 500; draw++)
            {
                float error = model.ThrowErrorRadians(35f, 0f);

                Assert.That(Mathf.Abs(error), Is.LessThanOrEqualTo(limit + 1e-5f));
                anyError |= Mathf.Abs(error) > 1e-4f;
            }

            Assert.That(anyError, Is.True, "a throw that never misses is the defect this fixes");
        }

        [Test]
        public void ADefenderOnTheBall_BreaksUpMoreThanOneAtArmsLength()
        {
            float onTheBall = Systems_SeededSkillModel.BreakUpChance(0f);
            float atTheEdge = Systems_SeededSkillModel.BreakUpChance(
                Systems_SimConstants.PASS_BREAKUP_RADIUS);

            Assert.That(
                onTheBall,
                Is.EqualTo(Systems_SimConstants.PASS_BREAKUP_CHANCE_AT_BALL).Within(1e-4f));
            Assert.That(
                atTheEdge,
                Is.EqualTo(Systems_SimConstants.PASS_BREAKUP_CHANCE_AT_EDGE).Within(1e-4f));
            Assert.That(onTheBall, Is.GreaterThan(atTheEdge));
        }

        [Test]
        public void NoSkill_FailsMoreOftenThanItsCeiling()
        {
            float ceiling = Systems_SimConstants.SKILL_FAILURE_CEILING;

            Assert.That(
                Systems_SeededSkillModel.DropChance(500f, 1f), Is.LessThanOrEqualTo(ceiling));
            Assert.That(
                Systems_SeededSkillModel.MissedHitChance(
                    Systems_PlayerRole.RunningBack, 1f, 0f),
                Is.LessThanOrEqualTo(ceiling));
            Assert.That(
                Systems_SeededSkillModel.BrokenWrapChance(
                    Systems_PlayerRole.Fullback, 1, true, 1f, 0f),
                Is.LessThanOrEqualTo(ceiling));
        }

        [Test]
        public void AnArmTackle_BreaksMoreEasily_AndHelpMakesAnyWrapUpHarderToBreak()
        {
            Systems_PlayerRole back = Systems_PlayerRole.RunningBack;

            float body = Systems_SeededSkillModel.BrokenWrapChance(back, 1, false, 0f, 0f);
            float arm = Systems_SeededSkillModel.BrokenWrapChance(back, 1, true, 0f, 0f);
            float gang = Systems_SeededSkillModel.BrokenWrapChance(back, 3, false, 0f, 0f);

            Assert.That(arm, Is.GreaterThan(body));
            Assert.That(gang, Is.LessThan(body));
        }

        [Test]
        public void ATiredCarrier_EscapesLess_AndATiredTacklerMissesMore()
        {
            Systems_PlayerRole back = Systems_PlayerRole.RunningBack;
            float fresh = Systems_SeededSkillModel.MissedHitChance(back, 0f, 0f);

            Assert.That(
                Systems_SeededSkillModel.MissedHitChance(back, 0f, 1f), Is.LessThan(fresh));
            Assert.That(
                Systems_SeededSkillModel.MissedHitChance(back, 1f, 0f), Is.GreaterThan(fresh));
        }

        /// <summary>
        /// Receiver 6 straight downfield with safety 20 beside the ball's path:
        /// outside CATCH_RADIUS when it arrives, inside PASS_BREAKUP_RADIUS.
        /// </summary>
        private void ThrowAtACoveredReceiver()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer receiver = (StubPlayer)_registry.Get(6);
            receiver.Position = new Vector2(0f, 8f);

            StubPlayer safety = (StubPlayer)_registry.Get(20);
            safety.Position = new Vector2(1.3f, 7f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);
        }

        private void RunUntilTheBallIsDown()
        {
            for (int tick = 0;
                 tick <= Systems_SimConstants.MAX_FLIGHT_TICKS + 2
                 && _play.Phase != Systems_PlayPhase.Dead
                 && !_ball.IsHeld;
                 tick++)
            {
                _referee.FixedTick();
            }
        }

        // =====================================================================
        // Revision 14: returns, the loose ball, kicks, pitches, forward progress,
        // grounding and the pre-snap
        // =====================================================================

        private StubPlayer Player(int slot)
        {
            return (StubPlayer)_registry.Get(slot);
        }

        /// <summary>Ticks the referee until the play is dead or the budget runs out.</summary>
        private void RunUntilDead(int budget = Systems_PlayModel.MAX_PHYSICS_TICKS)
        {
            for (int tick = 0; tick < budget && _play.Phase != Systems_PlayPhase.Dead; tick++)
            {
                _referee.FixedTick();
            }
        }

        /// <summary>
        /// Begins a kicking snap: the quarterback, the kicker, set at his depth, the
        /// ball in his hands, everyone else far away unless the test moves them.
        /// </summary>
        private void BeginKick(Systems_PlayKind kind, float lineOfScrimmageY, float kickerDepthMetres)
        {
            MoveEveryoneFarAway();

            _quarterback.Position = new Vector2(0f, lineOfScrimmageY - kickerDepthMetres);

            if (kind == Systems_PlayKind.Punt)
            {
                _play.BeginEpisode(
                    lineOfScrimmageY, _quarterback.Position.y,
                    Systems_GameRules.DOWNS_PER_SERIES, 8f);
                _play.LatchCall(Systems_PlayCall.Punt);
                Assert.That(_play.Kind, Is.EqualTo(Systems_PlayKind.Punt), "test premise");
            }
            else
            {
                _play.BeginEpisode(
                    lineOfScrimmageY, _quarterback.Position.y, 1,
                    Systems_GameRules.YARDS_TO_GAIN, kind, false);
            }

            _play.Snap();
            _quarterback.SetCarrier(true);
            _ball.ResetForSnap(Systems_Formation.QUARTERBACK_SLOT_INDEX, _quarterback.Position);
            _referee.ResetContactTracking();
        }

        [Test]
        public void AnInterception_IsReturnedRatherThanWhistled()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            Player(6).Position = new Vector2(0f, 20f);
            StubPlayer safety = Player(20);
            safety.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60 && !_ball.IsHeld; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsHeld, Is.True, "test premise: the safety caught it");
            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live), "a pick is a return now");
            Assert.That(_ball.PossessingSide, Is.EqualTo(Systems_TeamSide.Defense));
            Assert.That(_play.PossessionChanged, Is.True);
            Assert.That(_play.ReturnOutcome, Is.EqualTo(Systems_PlayOutcome.Interception));

            // And he takes it all the way back the other way.
            safety.Position = new Vector2(0f, Systems_FieldModel.OWN_GOAL_LINE_Y - 0.5f);
            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Touchdown));
            Assert.That(_play.ScoringSide, Is.EqualTo(Systems_TeamSide.Defense));
        }

        [Test]
        public void AReturnerDownedShortOfAScore_IsTheInterceptionWhereHeWentDown()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            Player(6).Position = new Vector2(0f, 20f);
            StubPlayer safety = Player(20);
            safety.Position = new Vector2(0f, 6f);

            _ballSystem.Throw(_quarterback, Vector2.up, 0f);

            for (int tick = 0; tick < 60 && !_ball.IsHeld; tick++)
            {
                _referee.FixedTick();
            }

            // Run back to midfield and met head-on there by the receiver.
            safety.Position = new Vector2(0f, -10f);
            Player(6).Position = new Vector2(0f, -10.8f);
            _referee.FixedTick();
            _referee.ReportContactWithCarrier(6, Systems_SimConstants.TACKLE_CLOSING_SPEED + 1f);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Interception));
            Assert.That(_play.DeadBallSpot.y, Is.EqualTo(-10f).Within(0.1f));
        }

        [Test]
        public void AStrippedBall_IsLooseAndTheOffenseCanFallOnIt()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            _ballSystem.Strip(_quarterback);

            Assert.That(_ball.IsLoose, Is.True);
            Assert.That(_quarterback.IsCarrier, Is.False);

            // The man who lost it is knocked away from it.
            _quarterback.Position = new Vector2(0f, -30f);

            StubPlayer lineman = Player(2);
            _referee.FixedTick();
            lineman.Position = _ball.Position;
            _referee.FixedTick();

            Assert.That(_ball.IsHeld, Is.True);
            Assert.That(_ball.CarrierId, Is.EqualTo(2));
            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
            Assert.That(_play.PossessionChanged, Is.False);
        }

        [Test]
        public void ALooseBallTheDefenseFallsOn_IsItsToReturn()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            _ballSystem.Strip(_quarterback);
            _quarterback.Position = new Vector2(0f, -30f);
            _referee.FixedTick();

            StubPlayer linebacker = Player(16);
            linebacker.Position = _ball.Position;
            _referee.FixedTick();

            Assert.That(_ball.CarrierId, Is.EqualTo(16));
            Assert.That(_play.PossessionChanged, Is.True);
            Assert.That(_play.ReturnOutcome, Is.EqualTo(Systems_PlayOutcome.FumbleLost));
        }

        [Test]
        public void ABallTheOffenseKnocksOutTheBackOfItsOwnEndZone_IsASafety()
        {
            MoveEveryoneFarAway();
            _quarterback.Position = new Vector2(0f, -100f);
            _quarterback.SetCarrier(false);

            _ball.MakeLoose(
                new Vector2(0f, Systems_FieldModel.OWN_BACK_LINE_Y + 0.02f),
                new Vector2(0f, -3f),
                Systems_TeamSide.Offense);

            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Safety));
            Assert.That(_play.ScoringSide, Is.EqualTo(Systems_TeamSide.Defense));
        }

        [Test]
        public void APunt_LeavesTheFootAtItsOperationTime()
        {
            BeginKick(Systems_PlayKind.Punt, 0f, Systems_SimConstants.PUNT_DEPTH_YARDS * Systems_FieldModel.YARD);

            for (int tick = 0; tick < Systems_SimConstants.PUNT_OPERATION_TICKS - 1; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsHeld, Is.True, "not before the operation time");

            _referee.FixedTick();
            _referee.FixedTick();

            Assert.That(_ball.IsKicked, Is.True);
            Assert.That(_play.WasKicked, Is.True);
        }

        [Test]
        public void AnUnfieldedPunt_TouchedByTheCoverTeam_IsTheReceiversBallThere()
        {
            BeginKick(Systems_PlayKind.Punt, -20f, Systems_SimConstants.PUNT_DEPTH_YARDS * Systems_FieldModel.YARD);

            for (int tick = 0; tick < 400 && !_ball.IsLoose && _play.Phase == Systems_PlayPhase.Live; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsLoose, Is.True, "test premise: the punt landed and nobody fielded it");

            Player(6).Position = _ball.Position;
            RunUntilDead(10);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Punt));
            Assert.That(_play.ScoringSide, Is.EqualTo(Systems_TeamSide.Defense), "the receivers' ball");
        }

        [Test]
        public void APuntFieldedWithACoverManOnHim_IsAFairCatch()
        {
            BeginKick(Systems_PlayKind.Punt, -20f, Systems_SimConstants.PUNT_DEPTH_YARDS * Systems_FieldModel.YARD);

            // Where an unblocked punt of the stub's length comes down.
            float landingY = _quarterback.Position.y
                + (Systems_SimConstants.PUNT_DISTANCE_YARDS * Systems_FieldModel.YARD);

            StubPlayer returner = Player(20);
            returner.Position = new Vector2(0f, landingY - 1f);
            Player(6).Position = new Vector2(2f, landingY - 2f);

            RunUntilDead(400);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Punt));
            Assert.That(_play.DeadBallSpot.y, Is.EqualTo(returner.Position.y).Within(0.1f));
        }

        [Test]
        public void AFieldGoalStraightDownTheMiddle_IsGood()
        {
            float lineOfScrimmage = Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                - (20f * Systems_FieldModel.YARD);

            BeginKick(
                Systems_PlayKind.ExtraPoint, lineOfScrimmage,
                Systems_SimConstants.PLACEKICK_DEPTH_YARDS * Systems_FieldModel.YARD);

            RunUntilDead();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.FieldGoalGood));
        }

        [Test]
        public void AFieldGoalBeyondTheKickersLeg_IsNoGood()
        {
            _skill.KickDistanceYards = 30f;

            float lineOfScrimmage = Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                - (35f * Systems_FieldModel.YARD);

            BeginKick(
                Systems_PlayKind.ExtraPoint, lineOfScrimmage,
                Systems_SimConstants.PLACEKICK_DEPTH_YARDS * Systems_FieldModel.YARD);

            RunUntilDead();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.FieldGoalMissed));
        }

        [Test]
        public void ARusherOnTheKick_CanBlockIt()
        {
            _skill.KickBlocked = true;

            BeginKick(
                Systems_PlayKind.ExtraPoint, 30f,
                Systems_SimConstants.PLACEKICK_DEPTH_YARDS * Systems_FieldModel.YARD);

            // The rusher arrives as the ball is kicked — any earlier and he would
            // simply tackle the kicker.
            for (int tick = 0; tick < Systems_SimConstants.PLACEKICK_OPERATION_TICKS - 1; tick++)
            {
                _referee.FixedTick();
            }

            Player(11).Position = _quarterback.Position
                + new Vector2(0f, Systems_SimConstants.KICK_BLOCK_REACH);

            _referee.FixedTick();
            _referee.FixedTick();

            Assert.That(_play.KickWasBlocked, Is.True);
            Assert.That(_ball.IsLoose || _ball.IsHeld, Is.True, "a blocked kick is a live ball");
        }

        [Test]
        public void AKickoffIntoTheEndZone_IsATouchback()
        {
            _skill.KickDistanceYards = 70f;

            float ownThirtyFive = Systems_FieldModel.OWN_GOAL_LINE_Y
                + (Systems_GameRules.KICKOFF_YARD_LINE * Systems_FieldModel.YARD);

            BeginKick(Systems_PlayKind.Kickoff, ownThirtyFive, 0.3f);
            RunUntilDead();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Touchback));
        }

        [Test]
        public void APitchToATrailingTeamMate_KeepsThePlayAlive()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            StubPlayer halfback = Player(Systems_Formation.HALFBACK_SLOT_INDEX);
            halfback.Position = new Vector2(-3f, -2f);

            Assert.That(_ballSystem.TryLateral(_quarterback, Vector2.zero), Is.True);

            for (int tick = 0; tick < 40 && !_ball.IsHeld; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsHeld, Is.True);
            Assert.That(_ball.CarrierId, Is.EqualTo(Systems_Formation.HALFBACK_SLOT_INDEX));
            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
        }

        [Test]
        public void ADroppedPitch_IsALiveBall()
        {
            _skill.LateralCaught = false;
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            Player(Systems_Formation.HALFBACK_SLOT_INDEX).Position = new Vector2(-3f, -2f);

            _ballSystem.TryLateral(_quarterback, Vector2.zero);

            for (int tick = 0; tick < 40 && _ball.IsInFlight; tick++)
            {
                _referee.FixedTick();
            }

            Assert.That(_ball.IsLoose, Is.True);
            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live), "not an incompletion");
        }

        [Test]
        public void NobodyAheadOfTheCarrier_CanBePitchedTo()
        {
            _quarterback.Position = Vector2.zero;
            MoveEveryoneFarAway();

            Player(Systems_Formation.HALFBACK_SLOT_INDEX).Position = new Vector2(1f, 4f);

            Assert.That(_ballSystem.TryLateral(_quarterback, Vector2.zero), Is.False);
            Assert.That(_ball.IsHeld, Is.True);
        }

        [Test]
        public void ACarrierHeldUpInContact_IsDownByForwardProgress()
        {
            _skill.WrapUpsToBreak = 100000;
            MoveEveryoneFarAway();

            StubPlayer back = Player(Systems_Formation.HALFBACK_SLOT_INDEX);
            back.Position = new Vector2(0f, 3f);
            _quarterback.SetCarrier(false);
            back.SetCarrier(true);
            _ball.AttachTo(back.Id, back.Position, Systems_TeamSide.Offense);
            _play.LatchCall(Systems_PlayCall.HandoffHalfback);

            StubPlayer linebacker = Player(16);
            linebacker.Position = new Vector2(0f, 3.9f);

            for (int tick = 0; tick < 200 && _play.Phase == Systems_PlayPhase.Live; tick++)
            {
                _referee.FixedTick();

                if (_play.Phase == Systems_PlayPhase.Live)
                {
                    _referee.ReportSustainedContact(16, 0.5f);
                }
            }

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Tackle));
            Assert.That(_play.DeadBallSpot.y, Is.EqualTo(3f).Within(0.1f));
            Assert.That(
                _play.PhysicsTick,
                Is.LessThan(Systems_SimConstants.FORWARD_PROGRESS_TICKS + 10),
                "forward progress, not the ceiling");
        }

        [Test]
        public void AThrowAtNobodyFromThePocket_IsIntentionalGrounding()
        {
            _quarterback.Position = new Vector2(0f, -5f);
            MoveEveryoneFarAway();
            _play.LatchCall(Systems_PlayCall.Pass);

            _ballSystem.Throw(_quarterback, Vector2.right, 1f);
            RunUntilDead(200);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
            Assert.That(_play.HasFoul(Systems_TeamSide.Offense, Systems_FoulType.IntentionalGrounding), Is.True);
        }

        [Test]
        public void AThrowawayFromOutsideTheBoxPastTheLine_IsLegal()
        {
            _quarterback.Position = new Vector2(
                Systems_SimConstants.TACKLE_BOX_HALF_WIDTH + 2f, -2f);
            MoveEveryoneFarAway();
            _play.LatchCall(Systems_PlayCall.Pass);

            _ballSystem.Throw(_quarterback, new Vector2(1f, 1f), 1f);
            RunUntilDead(200);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Incompletion));
            Assert.That(_play.FoulCount, Is.Zero);
        }

        [Test]
        public void TheQuarterbackDownedBehindTheLineBeforeHeThrows_IsASack()
        {
            _quarterback.Position = new Vector2(0f, -5f);
            MoveEveryoneFarAway();
            _play.LatchCall(Systems_PlayCall.Pass);

            Player(11).Position = new Vector2(0f, -4.2f);
            _referee.FixedTick();
            _referee.ReportContactWithCarrier(11, Systems_SimConstants.TACKLE_CLOSING_SPEED + 1f);

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.Sack));
        }

        [Test]
        public void AnOffensivePlayerMovingBeforeTheSnap_IsAFalseStart()
        {
            _play.BeginEpisode(0f, _quarterback.Position.y, 1, Systems_GameRules.YARDS_TO_GAIN);

            Player(2).Nudge(new Vector2(0f, Systems_SimConstants.FLINCH_SPEED));
            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.DeadBallFoul));
            Assert.That(_play.HasFoul(Systems_TeamSide.Offense, Systems_FoulType.FalseStart), Is.True);
        }

        [Test]
        public void ADefenderInTheNeutralZoneBeforeTheSnap_IsOffside()
        {
            _play.BeginEpisode(0f, _quarterback.Position.y, 1, Systems_GameRules.YARDS_TO_GAIN);

            Player(11).Position = new Vector2(Player(11).Position.x, 0.1f);
            _referee.FixedTick();

            Assert.That(_play.Outcome, Is.EqualTo(Systems_PlayOutcome.DeadBallFoul));
            Assert.That(_play.HasFoul(Systems_TeamSide.Defense, Systems_FoulType.Offside), Is.True);
        }

        [Test]
        public void SetTeams_AreSnappedAfterThePreSnap()
        {
            _play.BeginEpisode(0f, _quarterback.Position.y, 1, Systems_GameRules.YARDS_TO_GAIN);

            for (int tick = 0; tick < Systems_SimConstants.PRESNAP_TICKS; tick++)
            {
                Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.PreSnap));
                _referee.FixedTick();
            }

            Assert.That(_play.Phase, Is.EqualTo(Systems_PlayPhase.Live));
        }

        [Test]
        public void AKickIsCalledBeforeTheSnapOnFourthDownOnly()
        {
            Systems_PlayModel fourth = new Systems_PlayModel();
            fourth.BeginEpisode(0f, 0f, 4, 8f);
            fourth.LatchCall(Systems_PlayCall.Punt);

            Assert.That(fourth.Kind, Is.EqualTo(Systems_PlayKind.Punt));
            Assert.That(fourth.KickFormationPending, Is.True);

            Systems_PlayModel third = new Systems_PlayModel();
            third.BeginEpisode(0f, 0f, 3, 8f);
            third.LatchCall(Systems_PlayCall.Punt);

            Assert.That(third.CallIsLatched, Is.False);

            Systems_PlayModel afterTheSnap = new Systems_PlayModel();
            afterTheSnap.BeginEpisode(0f, 0f, 4, 8f);
            afterTheSnap.Snap();
            afterTheSnap.LatchCall(Systems_PlayCall.Punt);

            Assert.That(afterTheSnap.CallIsLatched, Is.False, "a kick is a pre-snap decision");
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
