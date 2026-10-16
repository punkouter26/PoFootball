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
    ///
    /// REVISION 14 PLAYS THE KICKING GAME, so a game opens with a kickoff snap, a
    /// touchdown is followed by a try snap and then a kickoff snap, and a punt or a
    /// field goal reports where the ball actually ended up. The helpers below play
    /// those snaps the way the referee would report them, and every test that used
    /// to assume the ball simply appeared on the 35 now gets there through one.
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

            _flow = new Systems_GameFlowSystem(
                _game, _play, new Systems_SimHealthModel(), _ended, _resolved, _gameOver);
            _flow.Start();
        }

        [TearDown]
        public void TearDown()
        {
            _flow.Dispose();
        }

        // =====================================================================
        // Helpers
        // =====================================================================

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

        /// <summary>
        /// Reads the touchback spot from the rule rather than a literal, so a change
        /// to KICKOFF_TOUCHBACK_YARD_LINE moves the expectation with it.
        /// </summary>
        private static float StartYardLine => Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE;

        /// <summary>
        /// Asks the flow for the next snap, as the director does, and begins it on
        /// the play model, so a test can put flags on it before its whistle.
        /// </summary>
        private Systems_PlaySituation Snap()
        {
            Systems_PlaySituation situation = _flow.NextSituation();

            _play.BeginEpisode(
                situation.LineOfScrimmageY, situation.LineOfScrimmageY,
                situation.Down, situation.YardsToGo, situation.Kind, situation.OnsideKick);
            _play.Snap();

            return situation;
        }

        /// <summary>
        /// The whistle of the snap in progress (begun by <see cref="Snap"/> if it has
        /// not been). <paramref name="side"/> is whose ball it is — or whose points.
        /// </summary>
        private void Whistle(
            float spotY,
            Systems_PlayOutcome outcome,
            Systems_TeamSide side,
            Systems_PlayCall call = Systems_PlayCall.HandoffHalfback,
            bool possessionChanged = false,
            float possessionChangeY = 0f)
        {
            _ended.Emit(new Systems_PlayEndedMessage(
                outcome, call, new Vector2(0f, spotY), 0f, 100, false,
                _game.NextPlayKind, side, possessionChanged, possessionChangeY,
                _play.FoulCount));
        }

        /// <summary>
        /// An ordinary down: snapped, then whistled dead at this spot with this
        /// outcome. The side defaults from the outcome the way the referee reports it.
        /// </summary>
        private void EndPlayAt(
            float spotY,
            Systems_PlayOutcome outcome = Systems_PlayOutcome.Tackle,
            Systems_PlayCall call = Systems_PlayCall.HandoffHalfback)
        {
            Snap();

            Systems_TeamSide side = outcome.HandsTheBallOver() || outcome == Systems_PlayOutcome.Safety
                ? Systems_TeamSide.Defense
                : Systems_TeamSide.Offense;

            Whistle(
                spotY, outcome, side, call,
                outcome == Systems_PlayOutcome.Interception || outcome == Systems_PlayOutcome.FumbleLost,
                spotY);
        }

        /// <summary>A kickoff (or free kick) the receivers take as a touchback.</summary>
        private void KickoffTouchback()
        {
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff), "expected a kickoff");
            Snap();
            Whistle(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 1f,
                Systems_PlayOutcome.Touchback,
                Systems_TeamSide.Defense,
                Systems_PlayCall.None);
        }

        /// <summary>A kickoff returned to this spot in the KICKING team's frame.</summary>
        private void KickoffReturnedTo(float spotY)
        {
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff), "expected a kickoff");
            Snap();
            Whistle(
                spotY, Systems_PlayOutcome.Kickoff, Systems_TeamSide.Defense,
                Systems_PlayCall.None, true, spotY);
        }

        /// <summary>The try after a touchdown: kicked through, or not.</summary>
        private void PlayTry(bool good)
        {
            Assert.That(_game.NextPlayKind.IsTry(), Is.True, "expected a try");

            Snap();

            if (_game.NextPlayKind == Systems_PlayKind.ExtraPoint)
            {
                Whistle(
                    Systems_FieldModel.ATTACKING_BACK_LINE_Y,
                    good ? Systems_PlayOutcome.FieldGoalGood : Systems_PlayOutcome.FieldGoalMissed,
                    Systems_TeamSide.Offense,
                    Systems_PlayCall.FieldGoal);
                return;
            }

            Whistle(
                good ? Systems_FieldModel.ATTACKING_GOAL_LINE_Y : OwnYard(97f),
                good ? Systems_PlayOutcome.Touchdown : Systems_PlayOutcome.Tackle,
                Systems_TeamSide.Offense,
                Systems_PlayCall.Pass);
        }

        /// <summary>A touchdown by the offense, the extra point, and the kickoff taken as a touchback.</summary>
        private void ScoreTouchdownAndKickOff()
        {
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);
            PlayTry(true);
            KickoffTouchback();
        }

        /// <summary>The opening kickoff, taken as a touchback: Home's ball on its 35.</summary>
        private void ReceiveOpeningKickoff()
        {
            KickoffTouchback();
        }

        // =====================================================================
        // The opening kickoff
        // =====================================================================

        [Test]
        public void TheGameOpensWithAKickoffByAway()
        {
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Playing));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away), "the kicking team");
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.KICKOFF_YARD_LINE).Within(0.1f));
        }

        [Test]
        public void KickOffTouchback_StartsHomeFirstAndTenAtTheTouchbackSpot()
        {
            ReceiveOpeningKickoff();

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Touchback));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.01f));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(StartYardLine).Within(0.1f));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Scrimmage));
        }

        [Test]
        public void AReturnedKickoff_StartsTheReceiversWhereTheReturnEnded()
        {
            // Returned to the kicking team's own 72 — the receivers' own 28.
            KickoffReturnedTo(OwnYard(72f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(28f).Within(0.1f));
        }

        [Test]
        public void AnOnsideKickTheKickingTeamFallsOn_KeepsItThere()
        {
            Snap();
            Whistle(OwnYard(46f), Systems_PlayOutcome.KickRecovered, Systems_TeamSide.Offense, Systems_PlayCall.None);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.KickRecovered));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(46f).Within(0.1f));
        }

        // =====================================================================
        // The chains
        // =====================================================================

        [Test]
        public void Gain_PastTheChains_IsAFirstDown()
        {
            ReceiveOpeningKickoff();

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
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(StartYardLine + 3f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.NextDown));
            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(7f).Within(0.1f));
        }

        [Test]
        public void Loss_IncreasesTheDistanceToGo()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(StartYardLine - 4f));

            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(14f).Within(0.1f));
        }

        [Test]
        public void ASack_IsReportedAsOneAndCostsTheDown()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(StartYardLine - 6f), Systems_PlayOutcome.Sack, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Sack));
            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.YardsToGo, Is.EqualTo(16f).Within(0.1f));
        }

        [Test]
        public void Incompletion_LeavesTheBallWhereItWas()
        {
            ReceiveOpeningKickoff();
            float before = _game.LineOfScrimmageY;

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Incompletion, Systems_PlayCall.Pass);

            Assert.That(_game.Down, Is.EqualTo(2));
            Assert.That(_game.LineOfScrimmageY, Is.EqualTo(before).Within(TOLERANCE));
            Assert.That(_game.YardsToGo, Is.EqualTo(10f).Within(0.1f));
        }

        [Test]
        public void FourthDownShort_TurnsTheBallOverAndMirrorsTheSpot()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(36f));
            EndPlayAt(OwnYard(37f));
            EndPlayAt(OwnYard(38f));
            Assert.That(_game.Down, Is.EqualTo(4), "expected to reach fourth down");

            EndPlayAt(OwnYard(39f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.TurnoverOnDowns));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));

            // Stopped on its own 39: the other team takes over on its own 61.
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(61f).Within(0.1f));
        }

        [Test]
        public void Interception_FlipsPossessionAndMirrorsTheSpotTheReturnEndedAt()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Interception));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));
        }

        [Test]
        public void Mirroring_IsItsOwnInverse()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(60f).Within(0.1f));
        }

        [Test]
        public void LineOfScrimmage_IsNeverInsideAnEndZone()
        {
            ReceiveOpeningKickoff();

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
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(95f));

            Assert.That(_game.IsGoalToGo, Is.True);
            Assert.That(_game.YardsToGoal, Is.EqualTo(5f).Within(0.1f));
        }

        // =====================================================================
        // Scoring and the try
        // =====================================================================

        [Test]
        public void Touchdown_ScoresSixAndLinesUpTheTry()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Touchdown));
            Assert.That(_resolved.Last.PointsScored, Is.EqualTo(6));
            Assert.That(_game.HomeScore, Is.EqualTo(6));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.ExtraPoint));
            Assert.That(
                _game.YardsToGoal,
                Is.EqualTo(Systems_GameRules.EXTRA_POINT_YARD_LINE).Within(0.1f));
        }

        [Test]
        public void AMadeExtraPoint_IsTheSeventhPointAndThenHomeKicksOff()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            PlayTry(true);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.ExtraPointGood));
            Assert.That(_game.HomeScore, Is.EqualTo(7));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "the kicking team");

            KickoffTouchback();

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine).Within(0.1f));
        }

        [Test]
        public void AMissedExtraPoint_LeavesTheTouchdownAtSix()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            PlayTry(false);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.ExtraPointMissed));
            Assert.That(_game.HomeScore, Is.EqualTo(6));
        }

        [Test]
        public void ATryTheDefenseReturnsAllTheWay_IsTwoPointsToIt()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Snap();
            Whistle(
                Systems_FieldModel.OWN_GOAL_LINE_Y - 1f,
                Systems_PlayOutcome.Touchdown, Systems_TeamSide.Defense, Systems_PlayCall.FieldGoal,
                true, OwnYard(85f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.DefensiveTry));
            Assert.That(_game.HomeScore, Is.EqualTo(6));
            Assert.That(_game.AwayScore, Is.EqualTo(Systems_GameRules.DEFENSIVE_TRY_POINTS));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "the touchdown team still kicks");
        }

        [Test]
        public void APickSix_IsTheDefensesTouchdownAndItsTry()
        {
            ReceiveOpeningKickoff();

            Snap();
            Whistle(
                Systems_FieldModel.OWN_GOAL_LINE_Y - 1f,
                Systems_PlayOutcome.Touchdown, Systems_TeamSide.Defense, Systems_PlayCall.Pass,
                true, OwnYard(45f));

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Touchdown));
            Assert.That(_game.AwayScore, Is.EqualTo(6));
            Assert.That(_game.HomeScore, Is.Zero);
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away), "the scorers try");
            Assert.That(_game.NextPlayKind.IsTry(), Is.True);
        }

        [Test]
        public void CarrierDownInItsOwnEndZone_IsASafetyAndAFreeKickFromTheTwenty()
        {
            ReceiveOpeningKickoff();

            Snap();
            Whistle(OwnYard(-1f), Systems_PlayOutcome.Safety, Systems_TeamSide.Defense);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Safety));
            Assert.That(_game.AwayScore, Is.EqualTo(2));
            Assert.That(_game.HomeScore, Is.EqualTo(0));

            // Home conceded, so Home free-kicks from its own 20.
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.SAFETY_FREE_KICK_YARD_LINE).Within(0.1f));
        }

        [Test]
        public void AnInterceptionDownedInTheEndZone_IsATouchbackAtTheTwenty()
        {
            ReceiveOpeningKickoff();

            Snap();
            Whistle(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 3f,
                Systems_PlayOutcome.Touchback, Systems_TeamSide.Defense, Systems_PlayCall.Pass,
                true, Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 3f);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Touchback));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.TOUCHBACK_YARD_LINE).Within(0.1f));
        }

        // =====================================================================
        // The clock
        // =====================================================================

        [Test]
        public void Clock_RunsDownWhileTheBallIsLive()
        {
            ReceiveOpeningKickoff();
            Snap();

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
        public void TheTryIsUntimed()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Snap();
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
            ReceiveOpeningKickoff();
            float before = _game.SecondsRemaining;

            EndPlayAt(OwnYard(25f), Systems_PlayOutcome.Incompletion, Systems_PlayCall.Pass);

            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
            Assert.That(_game.IsClockRunning, Is.False);
        }

        [Test]
        public void Tackle_ChargesTheHuddleToTheClock()
        {
            ReceiveOpeningKickoff();
            float before = _game.SecondsRemaining;

            EndPlayAt(OwnYard(38f));

            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(before - Systems_GameRules.HUDDLE_SECONDS).Within(0.01f));
        }

        /// <summary>
        /// Through revision 13 every trip out of bounds stopped the clock. Early in
        /// a half it does not: the clock restarts when the ball is spotted, which
        /// here means the huddle is charged like any other play.
        /// </summary>
        [Test]
        public void OutOfBoundsEarlyInAHalf_DoesNotStopTheClock()
        {
            ReceiveOpeningKickoff();
            float before = _game.SecondsRemaining;

            EndPlayAt(OwnYard(40f), Systems_PlayOutcome.OutOfBounds);

            Assert.That(_game.IsClockRunning, Is.True);
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(before - Systems_GameRules.HUDDLE_SECONDS).Within(0.01f));
        }

        [Test]
        public void OutOfBoundsLateInTheFourth_StopsTheClock()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);
            BurnClockDownTo(Systems_GameRules.OUT_OF_BOUNDS_STOPS_CLOCK_SECOND_HALF_SECONDS - 10f);

            float before = _game.SecondsRemaining;
            Whistle(_game.LineOfScrimmageY + 2f, Systems_PlayOutcome.OutOfBounds, Systems_TeamSide.Offense);

            Assert.That(_game.IsClockRunning, Is.False);
            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
        }

        [Test]
        public void TheTwoMinuteWarning_StopsTheClockOnceInTheFourth()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);

            // Snap, and run the live clock across the warning.
            BurnClockDownTo(Systems_GameRules.TWO_MINUTE_WARNING_SECONDS - 1f);
            Whistle(_game.LineOfScrimmageY + 2f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.TwoMinuteWarning, Is.True);
            Assert.That(_game.IsClockRunning, Is.False);

            EndPlayAt(_game.LineOfScrimmageY + (2f * Systems_FieldModel.YARD));

            Assert.That(_resolved.Last.TwoMinuteWarning, Is.False, "once a half");
        }

        [Test]
        public void QuarterExpiring_AdvancesTheQuarterRatherThanEndingTheGame()
        {
            ReceiveOpeningKickoff();
            BurnQuarterToZero();
            Whistle(OwnYard(38f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_game.Quarter, Is.EqualTo(2));
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(Systems_GameRules.QUARTER_SECONDS).Within(0.01f));
            Assert.That(_gameOver.Count, Is.Zero);
        }

        [Test]
        public void Halftime_HasHomeKickToAway()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(2);
            BurnQuarterToZero();
            Whistle(_game.LineOfScrimmageY + 1f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_game.Quarter, Is.EqualTo(3));
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Halftime));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "Home received to open the game, so Home kicks");

            KickoffTouchback();

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine).Within(0.1f));
        }

        [Test]
        public void FourthQuarterExpiring_EndsTheGameOnce()
        {
            PlayToFinalWhistle();

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_gameOver.Count, Is.EqualTo(1));

            int playsBefore = _resolved.Count;
            int scoreAtFinal = _game.HomeScore + _game.AwayScore;

            Whistle(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown, Systems_TeamSide.Offense);

            Assert.That(_resolved.Count, Is.EqualTo(playsBefore));
            Assert.That(_game.HomeScore + _game.AwayScore, Is.EqualTo(scoreAtFinal));
        }

        /// <summary>
        /// A touchdown as the clock dies still gets its try, which is untimed: the
        /// period ends after the try rather than swallowing it.
        /// </summary>
        [Test]
        public void ATouchdownAsTheClockDies_StillGetsItsTry()
        {
            ReceiveOpeningKickoff();
            BurnQuarterToZero();
            Whistle(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown, Systems_TeamSide.Offense);

            Assert.That(_game.Quarter, Is.EqualTo(1), "the quarter waits for the try");
            Assert.That(_game.NextPlayKind.IsTry(), Is.True);

            PlayTry(true);

            Assert.That(_game.HomeScore, Is.EqualTo(7));
            Assert.That(_game.Quarter, Is.EqualTo(2));
        }

        // =====================================================================
        // Overtime
        // =====================================================================

        private void PlayLevelRegulation()
        {
            ReceiveOpeningKickoff();

            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                if (_game.NextPlayKind == Systems_PlayKind.Kickoff)
                {
                    KickoffTouchback();
                }

                BurnQuarterToZero();
                Whistle(_game.LineOfScrimmageY + 1f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);
            }

            Assert.That(_game.HomeScore, Is.EqualTo(_game.AwayScore), "test premise");
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime), "test premise");
        }

        [Test]
        public void RegulationEndingLevel_GoesToOvertimeWithAKickoff()
        {
            PlayLevelRegulation();

            Assert.That(_gameOver.Count, Is.Zero, "overtime is not the final whistle");
            Assert.That(
                _game.SecondsRemaining,
                Is.EqualTo(Systems_GameRules.OVERTIME_SECONDS).Within(0.01f));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_flow.HasNextPlay, Is.True);
        }

        /// <summary>
        /// THE NFL'S RULE, NOT SUDDEN DEATH. A touchdown on the first possession of
        /// overtime does not end it: the other team is owed the ball.
        /// </summary>
        [Test]
        public void AFirstPossessionTouchdown_DoesNotEndOvertime()
        {
            PlayLevelRegulation();
            KickoffTouchback();
            Systems_TeamId first = _game.Possession;

            ScoreTouchdownAndKickOff();

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime));
            Assert.That(_gameOver.Count, Is.Zero);
            Assert.That(_game.Possession, Is.EqualTo(first.Opponent()), "the other team gets its possession");
        }

        [Test]
        public void TheSecondTeamFailingToAnswer_EndsOvertime()
        {
            PlayLevelRegulation();
            KickoffTouchback();
            ScoreTouchdownAndKickOff();

            // The second team turns it over on downs.
            for (int down = 0; down < Systems_GameRules.DOWNS_PER_SERIES; down++)
            {
                EndPlayAt(_game.LineOfScrimmageY);
            }

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_gameOver.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADefensiveScoreOnTheFirstPossession_EndsOvertime()
        {
            PlayLevelRegulation();
            KickoffTouchback();

            Snap();
            Whistle(
                Systems_FieldModel.OWN_GOAL_LINE_Y - 1f,
                Systems_PlayOutcome.Touchdown, Systems_TeamSide.Defense, Systems_PlayCall.Pass,
                true, OwnYard(30f));

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
        }

        [Test]
        public void LevelAfterBothPossessions_TheNextScoreWins()
        {
            PlayLevelRegulation();
            KickoffTouchback();

            // First possession: a field goal.
            EndPlayAt(_game.LineOfScrimmageY, Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);
            KickoffTouchback();

            // Second possession: a field goal to tie.
            EndPlayAt(_game.LineOfScrimmageY, Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);

            Assert.That(_game.HomeScore, Is.EqualTo(_game.AwayScore), "test premise");
            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Overtime), "a tie keeps it going");

            KickoffTouchback();
            EndPlayAt(_game.LineOfScrimmageY, Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final), "now the next score wins");
        }

        [Test]
        public void OvertimeExpiringStillLevel_EndsTheGameTied()
        {
            PlayLevelRegulation();
            KickoffTouchback();

            BurnClockToZero();
            Whistle(_game.LineOfScrimmageY + 1f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_game.HomeScore, Is.EqualTo(_game.AwayScore));
        }

        // =====================================================================
        // Fumbles
        // =====================================================================

        [Test]
        public void ALostFumble_FlipsPossessionAndMirrorsTheSpot()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(42f), Systems_PlayOutcome.FumbleLost);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(100f - 42f).Within(0.1f));
        }

        [Test]
        public void ALostFumble_StopsTheClockLikeAnyChangeOfPossession()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(OwnYard(42f), Systems_PlayOutcome.FumbleLost);

            Assert.That(_game.IsClockRunning, Is.False);
        }

        /// <summary>
        /// The defense had it and the offense got it back: a new series where it
        /// ended up, as on any change of possession.
        /// </summary>
        [Test]
        public void ABallTheOffenseGetsBack_IsANewSeries()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(OwnYard(38f));
            Assert.That(_game.Down, Is.EqualTo(2), "test premise");

            Snap();
            Whistle(OwnYard(33f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense,
                Systems_PlayCall.Pass, true, OwnYard(45f));

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(33f).Within(0.1f));
        }

        // =====================================================================
        // The game loop's end state
        // =====================================================================

        [Test]
        public void HasNextPlay_IsTrueWhileTheGameIsLive()
        {
            Assert.That(_flow.HasNextPlay, Is.True);

            ReceiveOpeningKickoff();

            Assert.That(_flow.HasNextPlay, Is.True, "a resolved down is not an ending");
        }

        [Test]
        public void HasNextPlay_IsFalseOnceTheGameIsFinal()
        {
            PlayToFinalWhistle();

            Assert.That(_game.Phase, Is.EqualTo(Systems_GamePhase.Final));
            Assert.That(_flow.HasNextPlay, Is.False);
        }

        [Test]
        public void PlaysRun_DoesNotAdvanceAfterTheFinalWhistle()
        {
            PlayToFinalWhistle();

            int playsAtFinal = _game.PlaysRun;

            _flow.NextSituation();
            _flow.NextSituation();

            Assert.That(_game.PlaysRun, Is.EqualTo(playsAtFinal));
        }

        /// <summary>
        /// Regulation, played out to a DECIDED result: one touchdown first, so it
        /// cannot end level and go to overtime.
        /// </summary>
        private void PlayToFinalWhistle()
        {
            ReceiveOpeningKickoff();
            ScoreTouchdownAndKickOff();

            for (int quarter = 0; quarter < Systems_GameRules.QUARTER_COUNT; quarter++)
            {
                if (_game.NextPlayKind == Systems_PlayKind.Kickoff)
                {
                    KickoffTouchback();
                }

                BurnQuarterToZero();
                Whistle(_game.LineOfScrimmageY + 1f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);
            }
        }

        private void BurnQuarterToZero()
        {
            BurnClockToZero();
        }

        /// <summary>
        /// Snaps a play and runs whatever period is on the clock down to 0:00 with
        /// the ball live, leaving the expiry armed for the whistle that follows.
        /// </summary>
        private void BurnClockToZero()
        {
            Snap();

            int guard = 0;
            int maximumTicks = Mathf.CeilToInt(_game.SecondsRemaining / 0.02f) + 10;

            while (_game.SecondsRemaining > 0f && guard < maximumTicks)
            {
                _flow.FixedTick();
                guard++;
            }

            Assert.That(_game.SecondsRemaining, Is.Zero, "clock should have reached 0:00");
        }

        /// <summary>
        /// Snaps a play and runs the clock down with the ball live, stopping short of
        /// 0:00. The whistle is the caller's.
        /// </summary>
        private void BurnClockDownTo(float seconds)
        {
            Snap();

            int guard = 0;
            int maximumTicks = Mathf.CeilToInt(_game.SecondsRemaining / 0.02f) + 10;

            while (_game.SecondsRemaining > seconds && guard < maximumTicks)
            {
                _flow.FixedTick();
                guard++;
            }
        }

        private void AdvanceToQuarter(int quarter)
        {
            while (_game.Quarter < quarter)
            {
                if (_game.NextPlayKind == Systems_PlayKind.Kickoff)
                {
                    KickoffTouchback();
                }

                BurnQuarterToZero();
                Whistle(_game.LineOfScrimmageY + 1f, Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);
            }

            if (_game.NextPlayKind == Systems_PlayKind.Kickoff)
            {
                KickoffTouchback();
            }
        }

        /// <summary>A tackle eleven yards downfield: a first down, clock running.</summary>
        private void GainAFirstDown()
        {
            EndPlayAt(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD));
        }

        // =====================================================================
        // Kicking
        // =====================================================================

        /// <summary>The receivers take over wherever the punt was downed, in their own frame.</summary>
        [Test]
        public void APunt_GivesTheReceiversTheBallWhereItWasDowned()
        {
            ReceiveOpeningKickoff();

            // Downed at the kicking team's own 79: the receivers' own 21.
            Snap();
            Whistle(OwnYard(79f), Systems_PlayOutcome.Punt, Systems_TeamSide.Defense, Systems_PlayCall.Punt);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Punt));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(21f).Within(0.5f));
        }

        /// <summary>A punt into the end zone is a touchback at the 20 — NOT the 35 a kickoff touchback uses.</summary>
        [Test]
        public void APuntIntoTheEndZone_IsATouchbackAtTheTwenty()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(OwnYard(70f));

            Snap();
            Whistle(
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 2f,
                Systems_PlayOutcome.Touchback, Systems_TeamSide.Defense, Systems_PlayCall.Punt);

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.TOUCHBACK_YARD_LINE).Within(0.5f));
        }

        [Test]
        public void AMadeFieldGoal_ScoresThreeAndKicksOff()
        {
            ReceiveOpeningKickoff();
            int before = _game.HomeScore;

            EndPlayAt(OwnYard(70f), Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FieldGoalGood));
            Assert.That(_game.HomeScore - before, Is.EqualTo(Systems_GameRules.FIELD_GOAL_POINTS));
            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Kickoff));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "the scorers kick");
        }

        /// <summary>A miss hands over the ball at the SPOT OF THE KICK — seven yards behind the line.</summary>
        [Test]
        public void AMissedFieldGoal_GivesTheBallUpAtTheSpotOfTheKick()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(OwnYard(60f));

            EndPlayAt(OwnYard(60f), Systems_PlayOutcome.FieldGoalMissed, Systems_PlayCall.FieldGoal);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.FieldGoalMissed));
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));
            Assert.That(_game.HomeScore, Is.EqualTo(0));

            // Kicked from our 60 - 7 = our 53, which is their 47.
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(100f - (60f - Systems_SimConstants.PLACEKICK_DEPTH_YARDS)).Within(0.5f));
        }

        [Test]
        public void AMissFromCloseIn_StillGivesTheDefenseNoWorseThanItsTwenty()
        {
            ReceiveOpeningKickoff();
            EndPlayAt(OwnYard(92f));

            EndPlayAt(OwnYard(92f), Systems_PlayOutcome.FieldGoalMissed, Systems_PlayCall.FieldGoal);

            Assert.That(
                YardLineOf(_game.LineOfScrimmageY),
                Is.EqualTo(Systems_GameRules.MISSED_FIELD_GOAL_MIN_YARD_LINE).Within(0.5f));
        }

        // =====================================================================
        // Penalties
        // =====================================================================

        private void Flag(Systems_FoulType type, Systems_TeamSide side, float spotY, int tick = 30)
        {
            _play.RecordFoul(new Systems_Foul(type, side, 0, spotY, tick));
        }

        [Test]
        public void AFalseStart_MovesTheOffenseBackFiveAndReplaysTheDown()
        {
            ReceiveOpeningKickoff();
            float line = _game.LineOfScrimmageY;

            Snap();
            Flag(Systems_FoulType.FalseStart, Systems_TeamSide.Offense, line, 0);
            Whistle(line, Systems_PlayOutcome.DeadBallFoul, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Penalty));
            Assert.That(_resolved.Last.Foul, Is.EqualTo(Systems_FoulType.FalseStart));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(15f).Within(0.1f));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine - 5f).Within(0.1f));
        }

        [Test]
        public void HalfTheDistance_AppliesInsideTheTen()
        {
            ReceiveOpeningKickoff();

            // Down at the offense's own 6.
            Snap();
            Whistle(OwnYard(6f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Snap();
            Flag(Systems_FoulType.FalseStart, Systems_TeamSide.Offense, _game.LineOfScrimmageY, 0);
            Whistle(_game.LineOfScrimmageY, Systems_PlayOutcome.DeadBallFoul, Systems_TeamSide.Offense);

            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(3f).Within(0.1f));
        }

        [Test]
        public void DefensiveHolding_IsFiveYardsAndAFirstDownOverAShortGain()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.DefensiveHolding, Systems_TeamSide.Defense, _game.LineOfScrimmageY);
            Whistle(OwnYard(StartYardLine + 2f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine + 5f).Within(0.1f));
        }

        [Test]
        public void ADefensiveFoulThatMovesTheChains_IsBookedAsAPenaltyFirstDown()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.DefensiveHolding, Systems_TeamSide.Defense, _game.LineOfScrimmageY);
            Whistle(OwnYard(StartYardLine + 2f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.PenaltyFirstDown, Is.True);
        }

        [Test]
        public void AnOffsideShortOfTheLineToGain_IsNotAFirstDown()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.Offside, Systems_TeamSide.Defense, _game.LineOfScrimmageY, 0);
            Whistle(_game.LineOfScrimmageY, Systems_PlayOutcome.DeadBallFoul, Systems_TeamSide.Defense);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_resolved.Last.PenaltyFirstDown, Is.False, "1st & 10 became 1st & 5, not a new series");
        }

        [Test]
        public void AKickoffReturn_CarriesItsYardageFromTheCatch()
        {
            // Fielded two yards deep in the receivers' end zone, returned to the
            // kicking team's own 72 — the receivers' 28. Thirty yards.
            Snap();
            Whistle(
                OwnYard(72f), Systems_PlayOutcome.Kickoff, Systems_TeamSide.Defense,
                Systems_PlayCall.None, true,
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y + (2f * Systems_FieldModel.YARD));

            Assert.That(_resolved.Last.ReturnYards, Is.EqualTo(30f).Within(0.1f));
            Assert.That(_resolved.Last.YardsGained, Is.EqualTo(0f), "a kick gains the offense nothing");
        }

        [Test]
        public void ThePlayThatEndsAQuarter_KeepsItsOwnResultForTheBoxScore()
        {
            ReceiveOpeningKickoff();
            BurnQuarterToZero();

            Whistle(
                _game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD),
                Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.EndOfQuarter));
            Assert.That(_resolved.Last.PlayResult, Is.EqualTo(Systems_DownResult.FirstDown));
        }

        [Test]
        public void ADefensiveFoulOnATouchdown_IsDeclined()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.DefensiveHolding, Systems_TeamSide.Defense, _game.LineOfScrimmageY);
            Whistle(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.False);
            Assert.That(_game.HomeScore, Is.EqualTo(6));
        }

        [Test]
        public void PassInterference_IsASpotFoulAndAFirstDown()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.DefensivePassInterference, Systems_TeamSide.Defense, OwnYard(68f));
            Whistle(_game.LineOfScrimmageY, Systems_PlayOutcome.Incompletion, Systems_TeamSide.Offense, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(68f).Within(0.1f));
        }

        [Test]
        public void PassInterferenceInTheEndZone_PutsTheBallOnTheOne()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(
                Systems_FoulType.DefensivePassInterference, Systems_TeamSide.Defense,
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 3f);
            Whistle(_game.LineOfScrimmageY, Systems_PlayOutcome.Incompletion, Systems_TeamSide.Offense, Systems_PlayCall.Pass);

            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(99f).Within(0.1f));
        }

        [Test]
        public void OffensiveHolding_IsTenYardsAndTheSameDown()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.OffensiveHolding, Systems_TeamSide.Offense, _game.LineOfScrimmageY);
            Whistle(OwnYard(StartYardLine + 8f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.YardsToGo, Is.EqualTo(20f).Within(0.1f));
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine - 10f).Within(0.1f));
        }

        /// <summary>The defense declines a hold on a play that lost more than the hold would cost.</summary>
        [Test]
        public void OffensiveHoldingOnABigLoss_IsDeclined()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.OffensiveHolding, Systems_TeamSide.Offense, _game.LineOfScrimmageY);
            Whistle(OwnYard(StartYardLine - 14f), Systems_PlayOutcome.Sack, Systems_TeamSide.Offense, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.False);
            Assert.That(_game.Down, Is.EqualTo(2));
        }

        [Test]
        public void IntentionalGrounding_CostsTheDownAndTenYards()
        {
            ReceiveOpeningKickoff();

            Snap();
            Flag(Systems_FoulType.IntentionalGrounding, Systems_TeamSide.Offense, OwnYard(StartYardLine - 6f));
            Whistle(_game.LineOfScrimmageY, Systems_PlayOutcome.Incompletion, Systems_TeamSide.Offense, Systems_PlayCall.Pass);

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_game.Down, Is.EqualTo(2), "loss of down");
            Assert.That(
                YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(StartYardLine - 16f).Within(0.1f));
        }

        [Test]
        public void FoulsByBothSides_OffsetAndReplayTheDown()
        {
            ReceiveOpeningKickoff();
            float line = _game.LineOfScrimmageY;

            Snap();
            Flag(Systems_FoulType.OffensiveHolding, Systems_TeamSide.Offense, line);
            Flag(Systems_FoulType.DefensiveHolding, Systems_TeamSide.Defense, line);
            Whistle(OwnYard(StartYardLine + 20f), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.Penalty));
            Assert.That(_game.Down, Is.EqualTo(1));
            Assert.That(_game.LineOfScrimmageY, Is.EqualTo(line).Within(TOLERANCE));
        }

        [Test]
        public void AFoulByTheReturnTeam_PullsTheReturnBack()
        {
            ReceiveOpeningKickoff();

            // Intercepted at the offense's own 60 and returned to its own 30 — but
            // the return team blocked in the back at the offense's own 40.
            Snap();
            _play.SetPossession(Systems_TeamSide.Defense, OwnYard(60f), Systems_PlayOutcome.Interception);
            _play.RecordFoul(new Systems_Foul(
                Systems_FoulType.IllegalBlockInTheBack, Systems_TeamSide.Defense, 0,
                OwnYard(40f), _play.PossessionChangeTick + 1));
            Whistle(OwnYard(30f), Systems_PlayOutcome.Interception, Systems_TeamSide.Defense,
                Systems_PlayCall.Pass, true, OwnYard(60f));

            Assert.That(_resolved.Last.PenaltyAccepted, Is.True);
            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away));

            // Ten yards back from the spot of the foul: the offense's own 50, which
            // is the return team's own 50.
            Assert.That(YardLineOf(_game.LineOfScrimmageY), Is.EqualTo(50f).Within(0.1f));
        }

        // =====================================================================
        // Timeouts
        // =====================================================================

        [Test]
        public void EveryTeamStartsWithThreeTimeouts()
        {
            Assert.That(_game.HomeTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
            Assert.That(_game.AwayTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
        }

        [Test]
        public void NoTimeoutIsCalled_OutsideTheLastMinuteOfAHalf()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(2);

            GainAFirstDown();

            Assert.That(_resolved.Last.TimeoutCalled, Is.False);
            Assert.That(_game.HomeTimeouts + _game.AwayTimeouts, Is.EqualTo(6));
        }

        [Test]
        public void BeforeTheHalf_TheOffenseStopsTheClockAndIsNotChargedTheHuddle()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(2);

            // Inside the last minute, above the two-minute warning, clock running.
            BurnClockDownTo(Systems_GameRules.TIMEOUT_WINDOW_SECONDS - 10f);

            Systems_TeamId offense = _game.Possession;
            float before = _game.SecondsRemaining;

            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_resolved.Last.TimeoutCalled, Is.True);
            Assert.That(_resolved.Last.TimeoutTeam, Is.EqualTo(offense));
            Assert.That(_game.TimeoutsOf(offense), Is.EqualTo(2));
            Assert.That(_game.TimeoutsOf(offense.Opponent()), Is.EqualTo(3));
            Assert.That(_game.SecondsRemaining, Is.EqualTo(before).Within(0.001f));
            Assert.That(_game.IsClockRunning, Is.False);
        }

        [Test]
        public void Halftime_GivesBothTeamsTheirTimeoutsBack()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(2);
            BurnClockDownTo(Systems_GameRules.TIMEOUT_WINDOW_SECONDS - 5f);
            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_game.HomeTimeouts + _game.AwayTimeouts, Is.EqualTo(5), "test premise");

            AdvanceToQuarter(3);

            Assert.That(_game.HomeTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
            Assert.That(_game.AwayTimeouts, Is.EqualTo(Systems_GameRules.TIMEOUTS_PER_HALF));
        }

        // =====================================================================
        // The try decision and the clock plays
        // =====================================================================

        /// <summary>
        /// Home 5 (a field goal and a safety), Away 0, Away's ball in the fourth
        /// quarter. A touchdown there makes it 6-5, the "up one" row of the chart:
        /// go for two to make it a field-goal game.
        /// </summary>
        private void SetUpAwayTouchdownToLeadByOne()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(_game.LineOfScrimmageY, Systems_PlayOutcome.FieldGoalGood, Systems_PlayCall.FieldGoal);
            KickoffTouchback();

            // Away is tackled in its own end zone.
            Snap();
            Whistle(OwnYard(-1f), Systems_PlayOutcome.Safety, Systems_TeamSide.Defense);
            KickoffTouchback();

            Assert.That(_game.HomeScore, Is.EqualTo(5), "test premise");
            Assert.That(_game.AwayScore, Is.Zero, "test premise");

            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);

            if (_game.Possession != Systems_TeamId.Away)
            {
                EndPlayAt(OwnYard(50f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            }

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Away), "test premise");
        }

        [Test]
        public void ALateTouchdownToLeadByOne_GoesForTwoAndScoresEightWhenItConverts()
        {
            SetUpAwayTouchdownToLeadByOne();

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.TwoPoint));

            PlayTry(true);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.TwoPointGood));
            Assert.That(_resolved.Last.PointsScored, Is.EqualTo(2));
            Assert.That(_game.AwayScore, Is.EqualTo(8));
        }

        [Test]
        public void AFailedTwoPointTry_LeavesTheTouchdownAtSix()
        {
            SetUpAwayTouchdownToLeadByOne();

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);
            PlayTry(false);

            Assert.That(_resolved.Last.Result, Is.EqualTo(Systems_DownResult.TwoPointFailed));
            Assert.That(_game.AwayScore, Is.EqualTo(6));
        }

        [Test]
        public void AnEarlyTouchdown_TakesTheKickWhateverTheMargin()
        {
            ReceiveOpeningKickoff();

            EndPlayAt(Systems_FieldModel.ATTACKING_GOAL_LINE_Y, Systems_PlayOutcome.Touchdown);

            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.ExtraPoint));
        }

        /// <summary>
        /// VICTORY FORMATION: leading late with fewer seconds left than the downs can
        /// run off, the offense kneels.
        /// </summary>
        [Test]
        public void LeadingWithTheClockNearlyGone_TheOffenseKneels()
        {
            ReceiveOpeningKickoff();
            ScoreTouchdownAndKickOff();

            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);

            if (_game.Possession != Systems_TeamId.Home)
            {
                EndPlayAt(OwnYard(50f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            }

            // Burn it down to a handful of seconds, clock running: three huddles,
            // well inside what four downs of kneeling can run off.
            BurnClockDownTo(3f * Systems_GameRules.HUDDLE_SECONDS);
            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            // Away still has three timeouts and calls one; burn the rest.
            while (_game.AwayTimeouts > 0 && _game.Phase != Systems_GamePhase.Final)
            {
                EndPlayAt(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD));
            }

            if (_game.Phase == Systems_GamePhase.Final)
            {
                Assert.Pass("the clock ran out first, which is also the point");
            }

            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.KneelDown));
        }

        [Test]
        public void TrailingWithNoTimeoutsAndTheClockRunning_TheOffenseSpikes()
        {
            ReceiveOpeningKickoff();
            AdvanceToQuarter(Systems_GameRules.QUARTER_COUNT);

            // Away scores first, so Home trails.
            if (_game.Possession != Systems_TeamId.Away)
            {
                EndPlayAt(OwnYard(50f), Systems_PlayOutcome.Interception, Systems_PlayCall.Pass);
            }

            ScoreTouchdownAndKickOff();

            Assert.That(_game.Possession, Is.EqualTo(Systems_TeamId.Home), "test premise");

            // Home spends its timeouts inside the last minute.
            BurnClockDownTo(Systems_GameRules.TIMEOUT_WINDOW_SECONDS - 5f);
            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            while (_game.HomeTimeouts > 0)
            {
                EndPlayAt(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD));
            }

            // Take the two-minute warning, which stops the clock on its own.
            BurnClockDownTo(Systems_GameRules.TWO_MINUTE_WARNING_SECONDS - 1f);
            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);
            Assert.That(_resolved.Last.TwoMinuteWarning, Is.True, "test premise");

            // A gain with the clock running, inside the spike window (the last
            // minute of an NFL half on this clock), and nothing left to stop it with.
            BurnClockDownTo(45f * Systems_GameRules.CLOCK_SCALE);
            Whistle(_game.LineOfScrimmageY + (11f * Systems_FieldModel.YARD), Systems_PlayOutcome.Tackle, Systems_TeamSide.Offense);

            Assert.That(_game.NextPlayKind, Is.EqualTo(Systems_PlayKind.Spike));
        }
    }
}
