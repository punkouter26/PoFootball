using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Views;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// Locks the rule that team colour follows POSSESSION, not side of the ball.
    ///
    /// WHY THIS EXISTS. Systems_TeamSide is a property of a body — slot 8 is the
    /// quarterback and is always on the offense — and the tint used to be painted
    /// from it. The same eleven bodies play offense for both teams and the field
    /// mirrors underneath them (Systems_TeamId), so blue was permanently the
    /// offense and the red team could never be seen with the ball. A turnover on
    /// downs moved the possession indicator on the HUD and changed nothing on the
    /// field, which reads as "the switch did not happen" even though the rules
    /// layer had already flipped possession and mirrored the spot.
    ///
    /// These assert the mapping only. That a fourth-down failure flips
    /// Systems_GameModel.Possession in the first place is
    /// Systems_GameFlowTests.FourthDownShort_TurnsTheBallOverAndMirrorsTheSpot;
    /// this suite covers the half that turns that flip into something visible.
    /// </summary>
    public sealed class Systems_PossessionTintTests
    {
        private Systems_RoleShapeSet _shapeSet;

        [SetUp]
        public void SetUp()
        {
            // The real shared asset, not a fresh instance: the point is that the
            // colours an artist authored are distinguishable, and a default-
            // constructed ScriptableObject would only prove the field initialisers
            // are.
            _shapeSet = Resources.Load<Systems_RoleShapeSet>("PoFootballRoleShapes");
            Assert.That(
                _shapeSet, Is.Not.Null,
                "no shape set at Resources/PoFootballRoleShapes");
        }

        [Test]
        public void TheTwoTeamsAreDistinguishable()
        {
            Color home = _shapeSet.ColorOf(Systems_TeamId.Home);
            Color away = _shapeSet.ColorOf(Systems_TeamId.Away);

            Assert.That(
                home, Is.Not.EqualTo(away),
                "home and away must not share a tint — possession would be invisible");
        }

        [Test]
        public void BeforeAnyPossession_OffenseReadsAsHome()
        {
            // The pre-kickoff paint runs in Awake, before the scoreboard has decided
            // anything. It must land on exactly the colours the field carried when
            // the tint was still side-driven, or the opening frame flickers.
            Assert.That(
                _shapeSet.ColorOf(Systems_TeamSide.Offense),
                Is.EqualTo(_shapeSet.ColorOf(Systems_TeamId.Home)));

            Assert.That(
                _shapeSet.ColorOf(Systems_TeamSide.Defense),
                Is.EqualTo(_shapeSet.ColorOf(Systems_TeamId.Away)));
        }

        [Test]
        public void PossessionFlip_SwapsWhichTintTheOffenseWears()
        {
            // What the applier does on a resolved down, reduced to the lookup it is
            // built from: offense wears whoever has the ball.
            Color offenseWithHomeBall = _shapeSet.ColorOf(Systems_TeamId.Home);
            Color defenseWithHomeBall = _shapeSet.ColorOf(Systems_TeamId.Home.Opponent());

            Color offenseWithAwayBall = _shapeSet.ColorOf(Systems_TeamId.Away);
            Color defenseWithAwayBall = _shapeSet.ColorOf(Systems_TeamId.Away.Opponent());

            Assert.That(offenseWithAwayBall, Is.EqualTo(defenseWithHomeBall));
            Assert.That(defenseWithAwayBall, Is.EqualTo(offenseWithHomeBall));
            Assert.That(offenseWithHomeBall, Is.Not.EqualTo(offenseWithAwayBall));
        }
    }
}
