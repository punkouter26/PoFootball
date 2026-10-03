using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Systems;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Tests
{
    /// <summary>
    /// Guards the promise that adding a game layer did not perturb training.
    ///
    /// Systems_EpisodeDirector used to draw the line of scrimmage inline:
    ///
    ///     _rng = new Random(Systems_EpisodeSeed.Value);
    ///     ...
    ///     _rng.NextFloat(LOS_MIN_Y, LOS_MAX_Y)
    ///
    /// That draw now lives behind Systems_ISpotProvider. These tests reproduce the
    /// old expression directly and assert the provider still yields it, value for
    /// value. If someone "improves" the sampling later, this fails — which is the
    /// point, because the failure mode otherwise is a silently different
    /// distribution of starting states and an .onnx that no longer means what its
    /// MANIFEST says.
    /// </summary>
    public sealed class Systems_SpotProviderTests
    {
        [Test]
        public void RandomProvider_ReproducesTheOriginalInlineDraw()
        {
            const uint seed = 20260810u;
            Systems_EpisodeSeed.Set(seed);

            Systems_RandomSpotProvider provider = new Systems_RandomSpotProvider();
            Random expected = new Random(seed);

            for (int episode = 0; episode < 250; episode++)
            {
                // Same draw order as Systems_RandomSpotProvider.NextSituation:
                // spot, then down, then distance. The determinism guarantee covers
                // the whole situation now, not just the spot.
                float expectedY = expected.NextFloat(
                    Systems_FieldModel.LOS_MIN_Y, Systems_FieldModel.LOS_MAX_Y);
                int expectedDown = expected.NextInt(1, Systems_GameRules.DOWNS_PER_SERIES + 1);
                float expectedYardsToGo = expected.NextFloat(1f, 15f);

                Systems_PlaySituation situation = provider.NextSituation();

                Assert.That(
                    situation.LineOfScrimmageY,
                    Is.EqualTo(expectedY),
                    $"episode {episode} drew a different line of scrimmage");
                Assert.That(
                    situation.Down,
                    Is.EqualTo(expectedDown),
                    $"episode {episode} drew a different down");
                Assert.That(
                    situation.YardsToGo,
                    Is.EqualTo(expectedYardsToGo),
                    $"episode {episode} drew a different distance");
            }
        }

        [Test]
        public void RandomProvider_StaysInsideTheAuthoredRange()
        {
            Systems_EpisodeSeed.Set(7u);
            Systems_RandomSpotProvider provider = new Systems_RandomSpotProvider();

            for (int episode = 0; episode < 500; episode++)
            {
                Systems_PlaySituation situation = provider.NextSituation();

                Assert.That(
                    situation.LineOfScrimmageY,
                    Is.GreaterThanOrEqualTo(Systems_FieldModel.LOS_MIN_Y));
                Assert.That(
                    situation.LineOfScrimmageY,
                    Is.LessThanOrEqualTo(Systems_FieldModel.LOS_MAX_Y));
                Assert.That(situation.Down, Is.InRange(1, Systems_GameRules.DOWNS_PER_SERIES));
                Assert.That(situation.YardsToGo, Is.InRange(1f, 15f));
            }
        }

        private sealed class FixedLesson : Systems_ISpotLessonSource
        {
            public FixedLesson(int lesson)
            {
                CurrentLesson = lesson;
            }

            public int CurrentLesson { get; }
        }

        /// <summary>
        /// The full lesson is the pre-curriculum distribution exactly. An installed
        /// source that says LESSON_FULL must change nothing, value for value.
        /// </summary>
        [Test]
        public void TheFullLesson_ReproducesTheUnstagedDraw()
        {
            Systems_EpisodeSeed.Set(20260810u);
            Systems_RandomSpotProvider staged = new Systems_RandomSpotProvider();
            staged.SetLessonSource(new FixedLesson(Systems_RandomSpotProvider.LESSON_FULL));

            Systems_EpisodeSeed.Set(20260810u);
            Systems_RandomSpotProvider unstaged = new Systems_RandomSpotProvider();

            for (int episode = 0; episode < 250; episode++)
            {
                Systems_PlaySituation expected = unstaged.NextSituation();
                Systems_PlaySituation actual = staged.NextSituation();

                Assert.That(actual.LineOfScrimmageY, Is.EqualTo(expected.LineOfScrimmageY));
                Assert.That(actual.Down, Is.EqualTo(expected.Down));
                Assert.That(actual.YardsToGo, Is.EqualTo(expected.YardsToGo));
            }
        }

        [Test]
        public void TheRedZoneLesson_SnapsInsideTheTwentyOnEarlyDowns()
        {
            Systems_EpisodeSeed.Set(7u);
            Systems_RandomSpotProvider provider = new Systems_RandomSpotProvider();
            provider.SetLessonSource(new FixedLesson(Systems_RandomSpotProvider.LESSON_RED_ZONE));

            float twenty = Systems_FieldModel.ATTACKING_GOAL_LINE_Y - (20f * Systems_FieldModel.YARD);

            for (int episode = 0; episode < 500; episode++)
            {
                Systems_PlaySituation situation = provider.NextSituation();

                Assert.That(situation.LineOfScrimmageY, Is.GreaterThanOrEqualTo(twenty));
                Assert.That(situation.LineOfScrimmageY, Is.LessThanOrEqualTo(Systems_FieldModel.LOS_MAX_Y));
                Assert.That(situation.Down, Is.InRange(1, Systems_GameRules.DOWNS_PER_SERIES - 1));
            }
        }

        [Test]
        public void TheOpenFieldLesson_UsesTheWholeFieldButNoFourthDown()
        {
            Systems_EpisodeSeed.Set(7u);
            Systems_RandomSpotProvider provider = new Systems_RandomSpotProvider();
            provider.SetLessonSource(new FixedLesson(Systems_RandomSpotProvider.LESSON_OPEN_FIELD));

            float lowest = float.MaxValue;

            for (int episode = 0; episode < 500; episode++)
            {
                Systems_PlaySituation situation = provider.NextSituation();
                lowest = UnityEngine.Mathf.Min(lowest, situation.LineOfScrimmageY);

                Assert.That(situation.Down, Is.InRange(1, Systems_GameRules.DOWNS_PER_SERIES - 1));
            }

            Assert.That(
                lowest, Is.LessThan(0f),
                "500 open-field snaps never reached the offense's own half");
        }

        [Test]
        public void EpisodeSeed_RejectsZeroBecauseTheGeneratorDoes()
        {
            Systems_EpisodeSeed.Set(0u);

            Assert.That(Systems_EpisodeSeed.Value, Is.Not.Zero);
        }

        /// <summary>
        /// Training must never stop on its own. mlagents-learn owns the step budget;
        /// an environment that stopped producing episodes would hang the trainer
        /// rather than fail it, which is the worst way for this to break.
        /// </summary>
        [Test]
        public void RandomProvider_NeverRunsOutOfPlays()
        {
            Systems_EpisodeSeed.Set(7u);
            Systems_RandomSpotProvider provider = new Systems_RandomSpotProvider();

            for (int episode = 0; episode < 500; episode++)
            {
                Assert.That(provider.HasNextPlay, Is.True);
                provider.NextSituation();
            }
        }
    }
}
