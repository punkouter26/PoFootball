using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// The viewer's controls: picking a player, moving the shot, the speed cycle
    /// and the numbers the DEBUG sheet reads.
    ///
    /// None of these touch the simulation, which is the property worth pinning:
    /// every one of them is driven here with no camera, no panel and no physics,
    /// because the systems never learn where their input came from.
    /// </summary>
    public sealed class Systems_SpectatorTests
    {
        private sealed class StubPlayer : Systems_IPlayerHandle
        {
            public int Id { get; set; }

            public Systems_PlayerRole Role { get; set; }

            public Systems_TeamSide Side => Systems_RoleTable.SideOf(Role);

            public Vector2 Position { get; set; }

            public Vector2 Velocity { get; set; }

            public bool IsCarrier { get; private set; }

            public float Fatigue => 0f;

            public void ClearFatigue() { }

            public void Freeze() { }

            public void ResetTo(Vector2 position) => Position = position;

            public void SetCarrier(bool isCarrier) => IsCarrier = isCarrier;

            public void SetTeamColor(Color color) { }
        }

        private Systems_SpectatorModel _model;
        private Systems_PlayerRegistry _registry;
        private Systems_SpectatorSystem _spectator;

        [SetUp]
        public void SetUp()
        {
            _model = new Systems_SpectatorModel();
            _registry = new Systems_PlayerRegistry();
            _spectator = new Systems_SpectatorSystem(_model, _registry);

            _registry.Register(0, new StubPlayer { Id = 0, Position = new Vector2(0f, 0f) });
            _registry.Register(1, new StubPlayer { Id = 1, Position = new Vector2(10f, 0f) });
        }

        [TearDown]
        public void TearDown()
        {
            // Systems_SimSpeedSystem writes the one global these tests can leave behind.
            Time.timeScale = 1f;
        }

        [Test]
        public void ATapNearAPlayer_FocusesTheNearestOne()
        {
            _spectator.TapAt(new Vector2(9f, 1f));

            Assert.That(_model.FocusedPlayerId, Is.EqualTo(1));
        }

        [Test]
        public void TappingTheFocusedPlayerAgain_LetsHimGo()
        {
            _spectator.TapAt(new Vector2(10f, 0f));
            _spectator.TapAt(new Vector2(10f, 0f));

            Assert.That(_model.HasFocus, Is.False);
        }

        [Test]
        public void ATapOnOpenGrass_ClearsTheFocus()
        {
            _spectator.TapAt(new Vector2(0f, 0f));
            Assert.That(_model.HasFocus, Is.True, "test premise");

            _spectator.TapAt(new Vector2(0f, 40f));

            Assert.That(_model.HasFocus, Is.False);
        }

        [Test]
        public void ATapJustOutsideThePickRadius_SelectsNobody()
        {
            _spectator.TapAt(new Vector2(0f, Systems_SpectatorSystem.PICK_RADIUS + 0.1f));

            Assert.That(_model.HasFocus, Is.False);
        }

        [Test]
        public void Zoom_IsClampedAtBothEnds()
        {
            _spectator.Zoom(0.01f);
            Assert.That(_model.ZoomScale, Is.EqualTo(Systems_SpectatorSystem.MIN_ZOOM));

            _spectator.Zoom(1000f);
            Assert.That(_model.ZoomScale, Is.EqualTo(Systems_SpectatorSystem.MAX_ZOOM));
        }

        [Test]
        public void Pan_AccumulatesAndIsClamped()
        {
            _spectator.Pan(new Vector2(3f, 4f));
            _spectator.Pan(new Vector2(3f, 4f));

            Assert.That(_model.PanOffset, Is.EqualTo(new Vector2(6f, 8f)));
            Assert.That(_model.IsManual, Is.True);

            _spectator.Pan(new Vector2(0f, 1000f));

            Assert.That(
                _model.PanOffset.magnitude,
                Is.EqualTo(Systems_SpectatorSystem.MAX_PAN).Within(0.001f));
        }

        [Test]
        public void ResetView_HandsTheShotBackAndKeepsTheFocus()
        {
            _spectator.TapAt(new Vector2(0f, 0f));
            _spectator.Pan(new Vector2(5f, 5f));
            _spectator.Zoom(0.5f);

            _spectator.ResetView();

            Assert.That(_model.IsManual, Is.False);
            Assert.That(_model.ZoomScale, Is.EqualTo(1f));
            Assert.That(_model.PanOffset, Is.EqualTo(Vector2.zero));
            Assert.That(_model.FocusedPlayerId, Is.EqualTo(0));
        }

        // --- Playback speed ------------------------------------------------------

        [Test]
        public void Speed_CyclesOneTwoFourHalfAndBackToOne()
        {
            using (Systems_SimSpeedSystem speed = new Systems_SimSpeedSystem())
            {
                Assert.That(speed.Speed, Is.EqualTo(1f));

                speed.Cycle();
                Assert.That(Time.timeScale, Is.EqualTo(2f));

                speed.Cycle();
                Assert.That(Time.timeScale, Is.EqualTo(4f));

                speed.Cycle();
                Assert.That(Time.timeScale, Is.EqualTo(0.5f));

                speed.Cycle();
                Assert.That(Time.timeScale, Is.EqualTo(1f));
            }
        }

        [Test]
        public void Pause_StopsTimeAndResumesAtTheChosenSpeed()
        {
            using (Systems_SimSpeedSystem speed = new Systems_SimSpeedSystem())
            {
                speed.Cycle();
                speed.TogglePause();

                Assert.That(speed.IsPaused, Is.True);
                Assert.That(Time.timeScale, Is.Zero);
                Assert.That(speed.Speed, Is.EqualTo(2f), "a pause must not forget the speed");

                speed.TogglePause();

                Assert.That(Time.timeScale, Is.EqualTo(2f));
            }
        }

        /// <summary>
        /// The reset this class exists to own: leaving a game paused, or at 4x, must
        /// not leave the menu that way.
        /// </summary>
        [Test]
        public void Dispose_PutsTimeBackToNormalEvenFromAPause()
        {
            Systems_SimSpeedSystem speed = new Systems_SimSpeedSystem();
            speed.Cycle();
            speed.TogglePause();

            speed.Dispose();

            Assert.That(Time.timeScale, Is.EqualTo(1f));
        }

        // --- The viewer's seed ---------------------------------------------------

        [Test]
        public void ARequestedSeed_IsHandedOverExactlyOnce()
        {
            Systems_EpisodeSeed.RequestGameSeed(4242u);

            Assert.That(Systems_EpisodeSeed.TakeRequestedGameSeed(), Is.EqualTo(4242u));
            Assert.That(Systems_EpisodeSeed.TakeRequestedGameSeed(), Is.Zero);
        }

        // --- Simulation health ---------------------------------------------------

        [Test]
        public void Health_AveragesScrimmagePlaysAndLeavesKicksOut()
        {
            Systems_SimHealthModel health = new Systems_SimHealthModel();

            health.RecordPlay(true, 4f, false, false, 1);
            health.RecordPlay(true, 8f, true, true, 1);
            health.RecordPlay(false, 0f, true, false, 2);

            Assert.That(health.ScrimmagePlays, Is.EqualTo(2));
            Assert.That(health.YardsPerPlay, Is.EqualTo(6f).Within(0.001f));
            Assert.That(health.FourthDowns, Is.EqualTo(2));
            Assert.That(health.TouchdownsPerDrive, Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void Health_CountsWhoHasABrainAndKeepsTheReason()
        {
            Systems_SimHealthModel health = new Systems_SimHealthModel();

            health.ReportBrain(false, "no brain table has been built");
            health.ReportBrain(true, string.Empty);

            Assert.That(health.PlayersReported, Is.EqualTo(2));
            Assert.That(health.PlayersWithBrain, Is.EqualTo(1));
        }
    }
}
