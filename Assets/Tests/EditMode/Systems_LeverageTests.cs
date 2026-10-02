using NUnit.Framework;
using PoFootball.Models;

namespace PoFootball.Tests
{
    /// <summary>
    /// Systems_Leverage drives the crowd and the stadium lights, so nothing in the
    /// simulation depends on its exact values — what matters is the ORDER it puts
    /// situations in. These pin that order: a bigger moment never scores lower
    /// than a smaller one, and a game that is not being played scores nothing.
    /// </summary>
    public sealed class Systems_LeverageTests
    {
        private const float MIDFIELD_Y = 0f;

        private static Systems_GameModel GameAt(float lineOfScrimmageY)
        {
            Systems_GameModel game = new Systems_GameModel();
            game.KickOff(Systems_TeamId.Home, lineOfScrimmageY);
            return game;
        }

        private static float YardsFromGoal(float yards)
        {
            return Systems_FieldModel.ATTACKING_GOAL_LINE_Y - (yards * Systems_FieldModel.YARD);
        }

        /// <summary>Runs the clock down to <paramref name="secondsLeft"/> in the final quarter.</summary>
        private static void PutInFinalQuarter(Systems_GameModel game, float secondsLeft)
        {
            game.BeginQuarter(Systems_GameRules.QUARTER_COUNT);
            game.BurnClock(Systems_GameRules.QUARTER_SECONDS - secondsLeft);
        }

        [Test]
        public void AGameThatHasNotKickedOff_HasNoLeverage()
        {
            Assert.That(Systems_Leverage.Of(new Systems_GameModel()), Is.EqualTo(0f));
        }

        [Test]
        public void FirstAndTenAtMidfieldInTheFirstQuarter_HasNoLeverage()
        {
            Assert.That(Systems_Leverage.Of(GameAt(MIDFIELD_Y)), Is.EqualTo(0f));
        }

        [Test]
        public void ALaterDown_IsNeverLessThanAnEarlierOne()
        {
            Systems_GameModel game = GameAt(MIDFIELD_Y);

            float previous = Systems_Leverage.Of(game);

            for (int down = 2; down <= Systems_GameRules.DOWNS_PER_SERIES; down++)
            {
                game.AdvanceDown(MIDFIELD_Y, Systems_GameRules.YARDS_TO_GAIN);

                float current = Systems_Leverage.Of(game);

                Assert.That(current, Is.GreaterThanOrEqualTo(previous), $"down {down}");
                previous = current;
            }

            Assert.That(
                previous, Is.GreaterThan(Systems_Leverage.Of(GameAt(MIDFIELD_Y))),
                "fourth down must be a bigger moment than first");
        }

        [Test]
        public void TheRedZone_RaisesIt_AndMoreTheCloserTheBallIs()
        {
            float outside = Systems_Leverage.Of(GameAt(YardsFromGoal(30f)));
            float edge = Systems_Leverage.Of(GameAt(YardsFromGoal(15f)));
            float goalLine = Systems_Leverage.Of(GameAt(YardsFromGoal(2f)));

            Assert.That(outside, Is.EqualTo(0f));
            Assert.That(edge, Is.GreaterThan(outside));
            Assert.That(goalLine, Is.GreaterThan(edge));
        }

        [Test]
        public void TheClock_OnlyMattersLateInAOneScoreGame()
        {
            Systems_GameModel close = GameAt(MIDFIELD_Y);
            PutInFinalQuarter(close, 10f);

            Systems_GameModel blowout = GameAt(MIDFIELD_Y);
            blowout.AddPoints(Systems_TeamId.Home, Systems_GameRules.ONSIDE_TRAILING_BY + 1);
            PutInFinalQuarter(blowout, 10f);

            Systems_GameModel early = GameAt(MIDFIELD_Y);
            PutInFinalQuarter(early, Systems_GameRules.QUARTER_SECONDS);

            Assert.That(Systems_Leverage.Of(close), Is.GreaterThan(0f));
            Assert.That(Systems_Leverage.Of(blowout), Is.EqualTo(0f));
            Assert.That(Systems_Leverage.Of(early), Is.EqualTo(0f));
        }

        [Test]
        public void TheSameClock_InAnEarlierQuarter_DoesNotCount()
        {
            Systems_GameModel game = GameAt(MIDFIELD_Y);
            game.BurnClock(Systems_GameRules.QUARTER_SECONDS - 10f);

            Assert.That(Systems_Leverage.Of(game), Is.EqualTo(0f));
        }

        [Test]
        public void Overtime_IsAlwaysLate()
        {
            Systems_GameModel game = GameAt(MIDFIELD_Y);
            game.BeginOvertime();

            Assert.That(Systems_Leverage.Of(game), Is.GreaterThan(0f));
        }

        [Test]
        public void EverythingAtOnce_SaturatesAtOne()
        {
            Systems_GameModel game = GameAt(YardsFromGoal(1f));

            for (int down = 2; down <= Systems_GameRules.DOWNS_PER_SERIES; down++)
            {
                game.AdvanceDown(YardsFromGoal(1f), 1f);
            }

            game.BeginOvertime();

            Assert.That(Systems_Leverage.Of(game), Is.EqualTo(1f));
        }

        [Test]
        public void AFinishedGame_HasNoLeverage()
        {
            Systems_GameModel game = GameAt(YardsFromGoal(1f));
            game.BeginOvertime();
            game.SetPhase(Systems_GamePhase.Final);

            Assert.That(Systems_Leverage.Of(game), Is.EqualTo(0f));
        }
    }
}
