using NUnit.Framework;
using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Tests
{
    public sealed class Systems_FieldModelTests
    {
        private Systems_FieldModel _field;

        [SetUp]
        public void SetUp()
        {
            _field = new Systems_FieldModel();
        }

        [Test]
        public void Dimensions_MatchARegulationField()
        {
            Assert.That(Systems_FieldModel.PLAYING_LENGTH, Is.EqualTo(91.44f).Within(0.01f));
            Assert.That(Systems_FieldModel.TOTAL_LENGTH, Is.EqualTo(109.73f).Within(0.02f));
            Assert.That(Systems_FieldModel.FIELD_WIDTH, Is.EqualTo(48.74f).Within(0.02f));
            Assert.That(Systems_FieldModel.END_ZONE_DEPTH, Is.EqualTo(9.144f).Within(0.01f));
        }

        [Test]
        public void LineOfScrimmageRange_SitsBetweenTheGoalLines()
        {
            Assert.That(
                Systems_FieldModel.LOS_MIN_Y,
                Is.GreaterThan(Systems_FieldModel.OWN_GOAL_LINE_Y));
            Assert.That(
                Systems_FieldModel.LOS_MAX_Y,
                Is.LessThan(Systems_FieldModel.ATTACKING_GOAL_LINE_Y));
            Assert.That(Systems_FieldModel.LOS_MIN_Y, Is.LessThan(Systems_FieldModel.LOS_MAX_Y));
        }

        /// <summary>
        /// Supports acceptance criterion #14. The trainer config sets
        /// normalize: false, so anything leaving [-1, 1] here reaches the network raw.
        /// </summary>
        [Test]
        public void Normalization_StaysInRange_EvenWellOffTheField()
        {
            float[] wildY = { -500f, -60f, 0f, 60f, 500f };
            float[] wildX = { -500f, -30f, 0f, 30f, 500f };

            foreach (float y in wildY)
            {
                float n = _field.NormalizeY(y);
                Assert.That(n, Is.InRange(-1f, 1f), $"NormalizeY({y}) = {n}");
            }

            foreach (float x in wildX)
            {
                float n = _field.NormalizeX(x);
                Assert.That(n, Is.InRange(-1f, 1f), $"NormalizeX({x}) = {n}");
            }
        }

        [Test]
        public void HasScored_OnlyPastTheAttackingGoalLine()
        {
            Assert.That(_field.HasScored(Systems_FieldModel.ATTACKING_GOAL_LINE_Y + 0.01f), Is.True);
            Assert.That(_field.HasScored(Systems_FieldModel.ATTACKING_GOAL_LINE_Y - 0.01f), Is.False);
            Assert.That(_field.HasScored(0f), Is.False);
        }

        [Test]
        public void Sidelines_AreDetectedOnBothEdges()
        {
            Assert.That(_field.IsOutsideSidelines(Systems_FieldModel.HALF_WIDTH + 0.01f), Is.True);
            Assert.That(_field.IsOutsideSidelines(-Systems_FieldModel.HALF_WIDTH - 0.01f), Is.True);
            Assert.That(_field.IsOutsideSidelines(0f), Is.False);
        }

        [Test]
        public void ExitingOwnEndZone_IsDetected()
        {
            Assert.That(_field.HasExitedOwnEndZone(Systems_FieldModel.OWN_BACK_LINE_Y - 0.01f), Is.True);
            Assert.That(_field.HasExitedOwnEndZone(0f), Is.False);
        }

        [Test]
        public void YardsToGoal_IsZeroAtTheGoalLine_And100AtTheOwn()
        {
            Assert.That(
                _field.YardsToAttackingGoal(Systems_FieldModel.ATTACKING_GOAL_LINE_Y),
                Is.EqualTo(0f).Within(0.01f));
            Assert.That(
                _field.YardsToAttackingGoal(Systems_FieldModel.OWN_GOAL_LINE_Y),
                Is.EqualTo(100f).Within(0.05f));
        }

        [Test]
        public void ClampToField_PullsAnyPointBackInsideTheBoundary()
        {
            Vector2 clamped = _field.ClampToField(new Vector2(999f, 999f));

            Assert.That(clamped.x, Is.LessThanOrEqualTo(Systems_FieldModel.HALF_WIDTH));
            Assert.That(clamped.y, Is.LessThanOrEqualTo(Systems_FieldModel.ATTACKING_BACK_LINE_Y));

            Vector2 clampedLow = _field.ClampToField(new Vector2(-999f, -999f));

            Assert.That(clampedLow.x, Is.GreaterThanOrEqualTo(-Systems_FieldModel.HALF_WIDTH));
            Assert.That(clampedLow.y, Is.GreaterThanOrEqualTo(Systems_FieldModel.OWN_BACK_LINE_Y));
        }
    }
}
