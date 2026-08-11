using System;
using MessagePipe;
using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// Rules-layer tests for the down, drive and scoring machine.
    ///
    /// The one that matters most is the mirroring pair. Every policy in this
    /// project was fitted on "the offense attacks +Y", and the game layer only gets
    /// away with two teams because a change of possession reflects the field
    /// through y -> -y. If that reflection is ever wrong, the symptom is not a
    /// crash — it is a team taking over on the wrong end of the field, which looks
    /// almost plausible on screen. These tests pin it to the yard.
    /// </summary>
    public sealed class Systems_GameFlowTests
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

        private sealed class StubSubscriber<T> : ISubscriber<T>
        {
            private sealed class Unsubscriber : IDisposable
            {
                public void Dispose() { }
            }

            private IMessageHandler<T> _handler;

            public IDisposable Subscribe(
                IMessageHandler<T> handler, params MessageHandlerFilter<T>[] filters)
            {
                _handler = handler;
                return new Unsubscriber();
            }

            public void Emit(T message)
            {
                _handler?.Handle(message);
            }
        }

        private Systems_GameModel _game;
        private Systems_PlayModel _play;
        private StubSubscriber<Systems_PlayEndedMessage> _ended;
        private StubPublisher<Systems_DownResolvedMessage> _resolved;
        private StubPublisher<Systems_GameOverMessage> _gameOver;
        private Systems_GameFlowSystem _flow;

        /// <summary>Tolerance in metres. A tenth of a yard is well inside a spot.</summary>
        private const float TOLERANCE = 0.05f;

        [SetUp]
        public void SetUp()
        {
            _game = new Systems_GameModel();
            _play = new Systems_PlayModel();
            _ended = new StubSubscriber<Systems_PlayEndedMessage>();
            _resolved = new StubPublisher<Systems_DownResolvedMessage>();
            _gameOver = new StubPublisher<Systems_GameOverMessage>();

            _flow = new Systems_GameFlowSystem(_game, _play, _ended, _resolved, _gameOver);
            _flow.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _flow.Dispose();
        }

        /// <summary>Y of the offense's own N-yard line, in the attacking frame.</summary>
        private static float OwnYard(float yardLine)
        {
            return Systems_FieldModel.OWN_GOAL_LINE_Y + (yardLine * Systems_FieldModel.YARD);
        }

        /// <summary>How many yards from its own goal line a spot is.</summary>
        private static float YardLineOf(float y)
        {
            return (y - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD;
        }

        private void EndPlayAt(
            float spotY,
            Systems_PlayOutcome outcome = Systems_PlayOutcome.Tackle,
            Systems_PlayCall call = Systems_PlayCall.HandoffHalfback,
            int physicsTicks = 100)
        {
            _ended.Emit(new Systems_PlayEndedMessage(
                outcome, call, new Vector2(0f, spotY), 0f, physicsTicks, false));
        }

        [Test]
        public void KickOff_StartsFirstAndTenFromOwnTwentyFive()
        {
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Playing));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.01f));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(25f).Within(0.1f));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
        }

        [Test]
        public void Gain_PastTheChains_IsAFirstDown()
        {
            EndPlayAt(OwnYard(36f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FirstDown));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.01f));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(36f).Within(0.1f));
        }

        [Test]
        public void Gain_ShortOfTheChains_AdvancesTheDownAndShortensTheDistance()
        {
            EndPlayAt(OwnYard(28f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.NextDown));
            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(7f).Within(0.1f));
        }

        [Test]
        public void Loss_IncreasesTheDistanceToGo()
        {
            EndPlayAt(OwnYard(21f));

            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(14f).Within(0.1f));
        }

        [Test]
        public void Incompletion_LeavesTheBallWhereItWas()
        {
            float before = _game.LineOfScrimmageY;

            EndPlayAt(
                OwnYard(40f),
                Systems_PlayOutcome.Incompletion,
                Systems_PlayCall.Pass);

            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.LineOfScrimmageY, Is.EqualTo(before).Within(TOLERANCE));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.1f));
        }

        [Test]
        public void FourthDownShort_TurnsTheBallOverAndMirrorsTheSpot()
        {
            // Four plays gaining a yard each: 1st, 2nd, 3rd, then the fourth fails.
            EndPlayAt(OwnYard(26f));
            EndPlayAt(OwnYard(27f));
            EndPlayAt(OwnYard(28f));
            Assert.That(_game.Down, Is.EqualTo(4), "expected to reach fourth down");

            EndPlayAt(OwnYard(29f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.TurnoverOnDowns));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));

            // Stopped on its own 29, so the other team takes over 29 yards from the
            // wrong end — its own 71-yard line, i.e. the opponent's 29.
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(71f).Within(0.1f));
        }

        [Test]
        public void Interception_FlipsPossessionAndMirrorsTheSpot()
        {
            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Interception));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));
        }

        [Test]
        public void Mirroring_IsItsOwnInverse()
        {
            // Picked off at the offense's own 40 -> the other team starts on its
            // own 60. Pick it off straight back at that team's own 40 and the first
            // team must be back on its own 60. Any asymmetry in the reflection
            // shows up as a drift here.
            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));
        }

        [Test]
        public void Touchdown_ScoresSevenAndKicksOffToTheOtherTeam()
        {
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Touchdown));
            Assert.That(_game.HomeScore, Is.EqualTo(7));
            Assert.That(_game.AwayScore, Is.EqualTo(0));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(25f).Within(0.1f));
        }

        [Test]
        public void CarrierDownInItsOwnEndZone_IsASafetyForTheDefense()
        {
            EndPlayAt(OwnYard(-1f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Safety));
            Assert.That(_game.AwayScore, Is.EqualTo(2));
            Assert.That(_game.HomeScore, Is.EqualTo(0));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(20f).Within(0.1f));
        }

        [Test]
        public void LineOfScrimmage_IsNeverInsideAnEndZone()
        {
            // A pick six's worth of field position: intercepted on the attacking
            // goal line would mirror to the other team's own goal line exactly.
            EndPlayAt(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y - 0.01f,
                Systems_PlayOutcome.Interception,
                Systems_PlayCall.Pass);

            float yardLine = YardLineOf(_game.LineOfScrimmageY);

            Assert.That(yardLine, Is.GreaterThanOrEqualTo(0.99f));
            Assert.That(yardLine, Is.LessThanOrEqualTo(99.01f));
        }

        [Test]
        public void GoalToGo_ReportsWhenTheGoalLineIsNearerThanTheChains()
        {
            EndPlayAt(OwnYard(95f));

            Assert.That(_game.IsGoalToGo, Is.True);
            Assert.That(_game.YardsToGoal, Is.EqualTo(5f).Within(0.1f));
        }

        [Test]
        public void Clock_RunsDownWhileTheBallIsLive()
        {
            _play.BeginEpisode(_game.LineOfScrimmageY, _game.LineOfScrimmageY);
            _play.Snap();

            float before = _game.SecondsRemaining;

            for (int tick = 0; tick < 50; tick++)
            {
                _flow.FixedTick();
            }

            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(before - 1f).Within(0.01f),
                "fifty ticks at 0.02 s is one second of game clock");
        }

        [Test]
        public void Clock_DoesNotRunWhileTheBallIsDead()
        {
            _play.BeginEpisode(_game.LineOfScrimmageY, _game.LineOfScrimmageY);

            float before = _game.SecondsRemaining;

            for (int tick = 0; tick < 50; tick++)
            {
                _flow.FixedTick();
            }

            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
        }

        [Test]
        public void Incompletion_StopsTheClockSoNoHuddleIsCharged()
        {
            float before = _game.SecondsRemaining;

            EndPlayAt(OwnYard(25f), Systems_PlayOutcome.Incompletion, Systems_PlayCall.Pass);

            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
            Assert.That(_game.IsClockRunning, Is.False);
        }

        [Test]
        public void Tackle_ChargesTheHuddleToTheClock()
        {
            float before = _game.SecondsRemaining;

            EndPlayAt(OwnYard(28f));

            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(before - Systems_GameRules.HUDDLE_SECONDS).Within(0.01f));
        }

        [Test]
        public void QuarterExpiring_AdvancesTheQuarterRatherThanEndingTheGame()
        {
            BurnQuarterToZero();
            EndPlayAt(OwnYard(28f));

            Assert.That(_game.Quarter, Is.EqualTo(2));
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(Systems_GameRules.QUARTER_SECONDS).Within(0.01f));
            Assert.That(_gameOver.Count, Is.Zero);
        }

        [Test]
        public void Halftime_GivesTheBallToWhoeverDidNotReceiveTheOpeningKickoff()
        {
            for (int quarter = 0; quarter < 2; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            Assert.That(_game.Quarter, Is.EqualTo(3));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(25f).Within(0.1f));
        }

        [Test]
        public void FourthQuarterExpiring_EndsTheGameOnce()
        {
            for (int quarter = 0; quarter < 4; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_gameOver.Count, Is.EqualTo(1));

            // Further whistles must not keep scoring after the final gun.
            int playsBefore = _resolved.Count;
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Count, Is.EqualTo(playsBefore));
            Assert.That(_game.HomeScore, Is.Zero);
        }

        // --- The game loop's end state -----------------------------------------

        [Test]
        public void HasNextPlay_IsTrueWhileTheGameIsLive()
        {
            Assert.That(_flow.HasNextPlay, Is.True);

            EndPlayAt(OwnYard(28f));

            Assert.That(_flow.HasNextPlay, Is.True, "a resolved down is not an ending");
        }

        /// <summary>
        /// The halt. Systems_EpisodeDirector asks this before every snap, so a false
        /// answer is the only thing standing between the final whistle and
        /// twenty-two agents playing downs forever behind the FINAL overlay.
        /// </summary>
        [Test]
        public void HasNextPlay_IsFalseOnceTheGameIsFinal()
        {
            PlayToFinalWhistle();

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_flow.HasNextPlay, Is.False);
        }

        /// <summary>
        /// The bug this pair was written for: the play counter kept climbing after
        /// the game was over, because nothing stopped the director asking for spots
        /// and every ask counted a play.
        /// </summary>
        [Test]
        public void PlaysRun_DoesNotAdvanceAfterTheFinalWhistle()
        {
            PlayToFinalWhistle();

            int playsAtFinal = _game.PlaysRun;

            // Exactly what the director would have done if nothing stopped it.
            _flow.NextLineOfScrimmageY();
            _flow.NextLineOfScrimmageY();

            Assert.That(_game.PlaysRun, Is.EqualTo(playsAtFinal));
        }

        /// <summary>Burns all four quarters, ending each on a whistle.</summary>
        private void PlayToFinalWhistle()
        {
            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }
        }

        /// <summary>
        /// Runs the game clock to 0:00 with the ball live, leaving the expiry armed
        /// for the next whistle.
        /// </summary>
        private void BurnQuarterToZero()
        {
            _play.BeginEpisode(_game.LineOfScrimmageY, _game.LineOfScrimmageY);
            _play.Snap();

            int guard = 0;
            int maximumTicks =
                Mathf.CeilToInt(Systems_GameRules.QUARTER_SECONDS / 0.02f) + 10;

            while (_game.SecondsRemaining > 0f && guard < maximumTicks)
            {
                _flow.FixedTick();
                guard++;
            }

            Assert.That(_game.SecondsRemaining, Is.Zero, "clock should have reached 0:00");
        }
    }
}
