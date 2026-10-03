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

            // Deterministic, so the punt assertions below stay exact — see
            // Systems_RefereeTests for the same reasoning.
            _flow = new Systems_GameFlowSystem(
                _game, _play, new Systems_DeterministicKickModel(),
                new Systems_SimHealthModel(), _ended, _resolved, _gameOver);
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

        /// <summary>
        /// Reads the touchback spot from the rule rather than a literal, so a change
        /// to KICKOFF_TOUCHBACK_YARD_LINE moves the expectation with it. These tests
        /// hardcoded 25 and all failed together the day the constant became the 2025
        /// dynamic-kickoff 35 — which told us nothing about the chains, only that a
        /// number had moved.
        /// </summary>
        private static float StartYardLine => Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE;

        [Test]
        public void KickOff_StartsFirstAndTenFromTheTouchbackSpot()
        {
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Playing));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.01f));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(StartYardLine).Within(0.1f));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
        }

        [Test]
        public void Gain_PastTheChains_IsAFirstDown()
        {
            // One yard past the sticks.
            EndPlayAt(OwnYard(StartYardLine + 11f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FirstDown));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.01f));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(StartYardLine + 11f).Within(0.1f));
        }

        [Test]
        public void Gain_ShortOfTheChains_AdvancesTheDownAndShortensTheDistance()
        {
            // Three yards gained, seven still to go.
            EndPlayAt(OwnYard(StartYardLine + 3f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.NextDown));
            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(7f).Within(0.1f));
        }

        [Test]
        public void Loss_IncreasesTheDistanceToGo()
        {
            // Four yards lost, so fourteen to go instead of ten.
            EndPlayAt(OwnYard(StartYardLine - 4f));

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
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine).Within(0.1f));
        }

        [Test]
        public void CarrierDownInItsOwnEndZone_IsASafetyForTheDefense()
        {
            EndPlayAt(OwnYard(-1f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Safety));
            Assert.That(_game.AwayScore, Is.EqualTo(2));
            Assert.That(_game.HomeScore, Is.EqualTo(0));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(Systems_GameRules.SAFETY_FREE_KICK_RESULT_YARD_LINE).Within(0.1f));
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
            _play.BeginEpisode(_game.LineOfScrimmageY, _game.LineOfScrimmageY, 1, Systems_GameRules.YARDS_TO_GAIN);
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
            _play.BeginEpisode(_game.LineOfScrimmageY, _game.LineOfScrimmageY, 1, Systems_GameRules.YARDS_TO_GAIN);

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
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine).Within(0.1f));
        }

        [Test]
        public void FourthQuarterExpiring_EndsTheGameOnce()
        {
            PlayToFinalWhistle();

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_gameOver.Count, Is.EqualTo(1));

            // Further whistles must not keep scoring after the final gun.
            int playsBefore = _resolved.Count;
            int scoreAtFinal = _game.HomeScore + _game.AwayScore;

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Count, Is.EqualTo(playsBefore));
            Assert.That(_game.HomeScore + _game.AwayScore, Is.EqualTo(scoreAtFinal));
        }

        // --- Overtime ----------------------------------------------------------

        /// <summary>
        /// A measured game finished 28-28 and simply stopped, because four quarters
        /// expiring returned EndOfGame whatever the score.
        /// </summary>
        [Test]
        public void RegulationEndingLevel_GoesToOvertimeRatherThanEndingTied()
        {
            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            Assert.That(_game.HomeScore, Is.EqualTo(_game.AwayScore), "test premise");
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime));
            Assert.That(_gameOver.Count, Is.Zero, "overtime is not the final whistle");
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(Systems_GameRules.OVERTIME_SECONDS).Within(0.01f));
            Assert.That(_flow.HasNextPlay, Is.True);
        }

        [Test]
        public void AScoreInOvertime_EndsTheGameImmediately()
        {
            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime), "test premise");

            EndPlayAt(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_gameOver.Count, Is.EqualTo(1));
            Assert.That(_game.HomeScore, Is.Not.EqualTo(_game.AwayScore));
        }

        /// <summary>
        /// One overtime period and no more. The NFL regular season allows a tie
        /// after it and so does this — what was wrong before was ending level
        /// without ever playing one.
        /// </summary>
        [Test]
        public void OvertimeExpiringStillLevel_EndsTheGameTied()
        {
            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime), "test premise");

            BurnClockToZero();
            EndPlayAt(OwnYard(28f));

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_game.HomeScore, Is.EqualTo(_game.AwayScore));
        }

        /// <summary>
        /// THE BUG THIS PINS. Sudden death was checked after AdvanceQuarter without
        /// asking whether overtime had already been running, so a touchdown that
        /// TIED the game as regulation expired started overtime and then instantly
        /// won it — the game ended level, having played none of the extra period it
        /// had just begun.
        /// </summary>
        [Test]
        public void AScoreThatLevelsItAsRegulationExpires_StartsOvertimeRatherThanEndingIt()
        {
            // Away goes ahead by a touchdown, then Home answers on the final play of
            // the fourth quarter to level it.
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT - 1; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }

            // Fourth quarter: run it out, and score on the play the clock dies on.
            BurnQuarterToZero();
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(
                _game.HomeScore, Is.EqualTo(_game.AwayScore),
                "test premise: the last score levelled it");

            Assert.That(
                _game.Phase, Is.EqualTo(Systems_GamePhase.Overtime),
                "a tying score at the buzzer must send the game to overtime");

            Assert.That(_gameOver.Count, Is.Zero, "the game must not have ended");
            Assert.That(_flow.HasNextPlay, Is.True);
        }

        // --- Fumbles -----------------------------------------------------------

        [Test]
        public void ALostFumble_FlipsPossessionAndMirrorsTheSpot()
        {
            float spot = OwnYard(42f);

            EndPlayAt(spot, Systems_PlayOutcome.FumbleLost);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(100f - 42f).Within(0.1f));
        }

        [Test]
        public void ALostFumble_StopsTheClockLikeAnyChangeOfPossession()
        {
            EndPlayAt(OwnYard(42f), Systems_PlayOutcome.FumbleLost);

            Assert.That(_game.IsClockRunning, Is.False);
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
            _flow.NextSituation();
            _flow.NextSituation();

            Assert.That(_game.PlaysRun, Is.EqualTo(playsAtFinal));
        }

        /// <summary>Burns all four quarters, ending each on a whistle.</summary>
        /// <summary>
        /// Regulation, played out to a DECIDED result.
        ///
        /// The score has to be broken first now that a level game goes to overtime —
        /// before that this ran four quarters from 0-0 and expected FINAL, which is
        /// exactly the tie Systems_GameRules.OVERTIME_SECONDS was added to remove.
        /// </summary>
        private void PlayToFinalWhistle()
        {
            BreakTheTie();

            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }
        }

        /// <summary>Puts one score on the board so regulation cannot end level.</summary>
        private void BreakTheTie()
        {
            EndPlayAt(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);
        }

        /// <summary>
        /// Runs the game clock to 0:00 with the ball live, leaving the expiry armed
        /// for the next whistle.
        /// </summary>
        private void BurnQuarterToZero()
        {
            BurnClockToZero();
        }

        /// <summary>
        /// Runs whatever period is currently on the clock down to 0:00 with the ball
        /// live. Sized off SecondsRemaining rather than QUARTER_SECONDS so it works
        /// for an overtime period too.
        ///
        /// ASKS THE FLOW FOR THE SNAP, AS THE DIRECTOR DOES. This used to begin the
        /// episode on the play model directly, which skipped NextSituation — the one
        /// call that ends Halftime and restarts a stopped clock. So every test that
        /// burned past the second quarter sat in Halftime with FixedTick refusing to
        /// run the clock, and seven tests failed on "clock should have reached 0:00"
        /// against a game model that was behaving exactly as designed.
        /// </summary>
        private void BurnClockToZero()
        {
            Systems_PlaySituation situation = _flow.NextSituation();
            _play.BeginEpisode(
                situation.LineOfScrimmageY, situation.LineOfScrimmageY,
                situation.Down, situation.YardsToGo);
            _play.Snap();

            int guard = 0;
            int maximumTicks =
                Mathf.CeilToInt(_game.SecondsRemaining / 0.02f) + 10;

            while (_game.SecondsRemaining > 0f && guard < maximumTicks)
            {
                _flow.FixedTick();
                guard++;
            }

            Assert.That(_game.SecondsRemaining, Is.Zero, "clock should have reached 0:00");
        }

        // --- Kicking -----------------------------------------------------------

        /// <summary>
        /// A punt from the middle of the field: forty net yards, then the receiving
        /// team takes over in its own frame. Own 35 + 40 = our 75, which is their 25.
        /// </summary>
        [Test]
        public void APunt_GivesTheBallUpFortyNetYardsDownfield()
        {
            EndPlayAt(
                OwnYard(StartYardLine),
                Systems_PlayOutcome.Punt,
                Systems_PlayCall.Punt);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Punt));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(100f - (StartYardLine + Systems_GameRules.PUNT_NET_YARDS))
                    .Within(0.5f));
        }

        /// <summary>
        /// A punt from inside the opponent's forty would land in the end zone, which
        /// is a touchback at the 20 — NOT the 35 a kickoff touchback uses.
        /// </summary>
        [Test]
        public void APuntIntoTheEndZone_IsATouchbackAtTheTwenty()
        {
            // Drive to our own 70 first. A kick is measured from the LINE OF
            // SCRIMMAGE, not from where the previous ball died, so the spot passed
            // to EndPlayAt below is deliberately irrelevant.
            EndPlayAt(OwnYard(70f));

            EndPlayAt(
                OwnYard(70f),
                Systems_PlayOutcome.Punt,
                Systems_PlayCall.Punt);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.PUNT_TOUCHBACK_YARD_LINE).Within(0.5f));
        }

        [Test]
        public void AMadeFieldGoal_ScoresThreeAndKicksOff()
        {
            int before = _game.HomeScore;

            EndPlayAt(
                OwnYard(70f),
                Systems_PlayOutcome.FieldGoalGood,
                Systems_PlayCall.FieldGoal);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FieldGoalGood));
            Assert.That(
                _game.HomeScore - before,
                Is.EqualTo(Systems_GameRules.FIELD_GOAL_POINTS));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE).Within(0.5f));
        }

        /// <summary>
        /// A miss hands over the ball at the SPOT OF THE KICK — seven yards behind
        /// the line of scrimmage — which is what makes a long attempt a gamble.
        /// </summary>
        [Test]
        public void AMissedFieldGoal_GivesTheBallUpBehindTheLineOfScrimmage()
        {
            EndPlayAt(OwnYard(60f));

            EndPlayAt(
                OwnYard(60f),
                Systems_PlayOutcome.FieldGoalMissed,
                Systems_PlayCall.FieldGoal);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FieldGoalMissed));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.HomeScore, Is.EqualTo(0));

            // Kick from our 60 - 17 = our 43, which is their 57.
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(100f - (60f - Systems_GameRules.FIELD_GOAL_SNAP_YARDS)).Within(0.5f));
        }

        // --- Timeouts ----------------------------------------------------------

        /// <summary>A kick model whose two-point try does what the test says.</summary>
        private sealed class ScriptedKickModel : Systems_IKickModel
        {
            public bool TwoPointGood { get; set; }

            public bool IsFieldGoalGood(float attemptYards) => true;

            public float PuntNetYards() => Systems_GameRules.PUNT_NET_YARDS;

            public float KickoffReturnYardLine() => Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE;

            public bool IsOnsideRecovered() => false;

            public bool IsTwoPointGood() => TwoPointGood;
        }

        /// <summary>
        /// Runs the clock down with the ball live and stops short of 0:00, so the
        /// next whistle lands inside the last minute rather than on the expiry.
        /// </summary>
        private void BurnClockDownTo(float seconds)
        {
            Systems_PlaySituation situation = _flow.NextSituation();
            _play.BeginEpisode(
                situation.LineOfScrimmageY, situation.LineOfScrimmageY,
                situation.Down, situation.YardsToGo);
            _play.Snap();

            int guard = 0;
            int maximumTicks = Mathf.CeilToInt(_game.SecondsRemaining / 0.02f) + 10;

            while (_game.SecondsRemaining > seconds && guard < maximumTicks)
            {
                _flow.FixedTick();
                guard++;
            }
        }

        /// <summary>A tackle eleven yards downfield: a first down, clock running.</summary>
        private void GainAFirstDown()
        {
            EndPlayAt(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD));
        }

        private void AdvanceToQuarter(int quarter)
        {
            while (_game.Quarter < quarter)
            {
                BurnQuarterToZero();
                EndPlayAt(OwnYard(28f));
            }
        }

        [Test]
        public void EveryTeamStartsWithThreeTimeouts()
        {
            Assert.That(_game.HomeTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
            Assert.That(_game.AwayTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
        }

        [Test]
        public void NoTimeoutIsCalled_OutsideTheLastMinuteOfAHalf()
        {
            AdvanceToQuarter(2);

            GainAFirstDown();

            Assert.That(_resolved.Last.TimeoutCalled, Is.False);
            Assert.That(_game.HomeTimeouts + _game.AwayTimeouts, Is.EqualTo(6));
        }

        [Test]
        public void BeforeTheHalf_TheOffenseStopsTheClockAndIsNotChargedTheHuddle()
        {
            AdvanceToQuarter(2);
            BurnClockDownTo(50f);

            Systems_TeamId offense = _game.Possession;
            float before = _game.SecondsRemaining;

            GainAFirstDown();

            Assert.That(_resolved.Last.TimeoutCalled, Is.True);
            Assert.That(_resolved.Last.TimeoutTeam, Is.EqualTo(offense));
            Assert.That(_game.TimeoutsOf(offense), Is.EqualTo(2));
            Assert.That(_game.TimeoutsOf(offense.Opponent()), Is.EqualTo(3));
            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
            Assert.That(_game.IsClockRunning, Is.False);
        }

        [Test]
        public void ATeamWithNoTimeoutsLeft_IsChargedTheHuddleLikeAnyOtherPlay()
        {
            AdvanceToQuarter(2);
            BurnClockDownTo(50f);

            Systems_TeamId offense = _game.Possession;

            for (int play = 0; play < Systems_GameRules.TIMEOUTS_PER_HALF; play++)
            {
                GainAFirstDown();
            }

            Assert.That(_game.TimeoutsOf(offense), Is.Zero, "test premise");

            float before = _game.SecondsRemaining;

            GainAFirstDown();

            Assert.That(_resolved.Last.TimeoutCalled, Is.False);
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(before - Systems_GameRules.HUDDLE_SECONDS).Within(0.01f));
        }

        /// <summary>
        /// The case timeouts were added for: late in the game it is the team that
        /// is BEHIND that stops the clock, whichever side of the ball it is on.
        /// </summary>
        [Test]
        public void LateInTheGame_TheTrailingTeamCallsItEvenOnDefense()
        {
            // Home scores first, so Away trails for the rest of the test.
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);
            BurnClockDownTo(50f);

            // Put the LEADING team on offense, running the clock out.
            if (_game.Possession != Systems_TeamId.Home)
            {
                EndPlayAt(OwnYard(50f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            }

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "test premise");

            float before = _game.SecondsRemaining;

            GainAFirstDown();

            Assert.That(_resolved.Last.TimeoutCalled, Is.True);
            Assert.That(_resolved.Last.TimeoutTeam, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.AwayTimeouts, Is.EqualTo(2));
            Assert.That(_game.HomeTimeouts, Is.EqualTo(3));
            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
        }

        [Test]
        public void Halftime_GivesBothTeamsTheirTimeoutsBack()
        {
            AdvanceToQuarter(2);
            BurnClockDownTo(50f);
            GainAFirstDown();

            Assert.That(_game.HomeTimeouts + _game.AwayTimeouts, Is.EqualTo(5), "test premise");

            AdvanceToQuarter(3);

            Assert.That(_game.HomeTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
            Assert.That(_game.AwayTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
        }

        // --- The try after a touchdown -----------------------------------------

        /// <summary>
        /// Rebuilds the flow on a kick model the test controls, then leaves Away one
        /// point behind a fourth-quarter touchdown: Home 5 (a field goal and a
        /// safety), Away 0, Away's ball. A touchdown there makes it 6-5, which is
        /// the "up one" row of the chart — go for two to make it a field goal game.
        /// </summary>
        private ScriptedKickModel SetUpAwayTouchdownToLeadByOne()
        {
            _flow.Dispose();

            ScriptedKickModel kick = new ScriptedKickModel();

            _game = new Systems_GameModel();
            _play = new Systems_PlayModel();
            _ended = new StubSubscriber<Systems_PlayEndedMessage>();
            _resolved = new StubPublisher<Systems_DownResolvedMessage>();
            _gameOver = new StubPublisher<Systems_GameOverMessage>();

            _flow = new Systems_GameFlowSystem(
                _game, _play, kick, new Systems_SimHealthModel(), _ended, _resolved, _gameOver);
            _flow.Start();

            // Home kicks a field goal, then tackles Away in its own end zone.
            EndPlayAt(
                _game.LineOfScrimmageY, Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);
            EndPlayAt(Systems_FieldModel.OWN_GOAL_LINE_Y - 1f);

            Assert.That(_game.HomeScore, Is.EqualTo(5), "test premise");
            Assert.That(_game.AwayScore, Is.Zero, "test premise");

            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);

            if (_game.Possession != Systems_TeamId.Away)
            {
                EndPlayAt(OwnYard(50f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            }

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away), "test premise");
            return kick;
        }

        [Test]
        public void ALateTouchdownToLeadByOne_GoesForTwoAndScoresEightWhenItConverts()
        {
            ScriptedKickModel kick = SetUpAwayTouchdownToLeadByOne();
            kick.TwoPointGood = true;

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Last.TwoPointAttempted, Is.True);
            Assert.That(_resolved.Last.TwoPointGood, Is.True);
            Assert.That(_resolved.Last.PointsScored, Is.EqualTo(8));
            Assert.That(_game.AwayScore, Is.EqualTo(8));
        }

        [Test]
        public void AFailedTwoPointTry_LeavesTheTouchdownAtSix()
        {
            ScriptedKickModel kick = SetUpAwayTouchdownToLeadByOne();
            kick.TwoPointGood = false;

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Last.TwoPointAttempted, Is.True);
            Assert.That(_resolved.Last.TwoPointGood, Is.False);
            Assert.That(_game.AwayScore, Is.EqualTo(6));
        }

        /// <summary>
        /// The kick is still awarded everywhere the chart says to take it — which
        /// is every touchdown before the fourth quarter.
        /// </summary>
        [Test]
        public void AnEarlyTouchdown_TakesTheKickWhateverTheMargin()
        {
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Last.TwoPointAttempted, Is.False);
            Assert.That(_resolved.Last.PointsScored, Is.EqualTo(7));
        }

    }
}
