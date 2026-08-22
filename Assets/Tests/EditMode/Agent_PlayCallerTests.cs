using System;
using NUnit.Framework;
using PoFootball.Agents;
using PoFootball.Models;
using PoFootball.Systems;

namespace PoFootball.Tests
{
    /// <summary>
    /// The scripted quarterback's play-call policy.
    ///
    /// WHY THESE EXIST. This judgement lived as four private methods on
    /// Agent_FootballPlayer and was therefore untestable without a MonoBehaviour, a
    /// Rigidbody2D, a player registry and a live play — so it was never tested, and
    /// it shipped a bug that any one of the tests below would have caught in a
    /// second. It keyed off field position alone and could not return Punt,
    /// FieldGoal or KeepQuarterback at all, so with no promoted brain (which is the
    /// normal state of this project) four of the seven calls were unreachable in the
    /// only mode the game can run. A 40-play sample came back with twenty
    /// incompletions, ten tackles and not one first down.
    ///
    /// The draw is injected, so every branch is reachable deterministically and none
    /// of these tests is flaky.
    /// </summary>
    public sealed class Agent_PlayCallerTests
    {
        /// <summary>Always draws zero — takes the first branch of every mix.</summary>
        private static Func<float> AlwaysLow => () => 0f;

        /// <summary>Always draws just under one — takes the second branch.</summary>
        private static Func<float> AlwaysHigh => () => 0.999f;

        private const float MIDFIELD = 50f;

        // --- The calls that used to be unreachable ---------------------------

        [Test]
        public void FourthAndLong_OutOfFieldGoalRange_Punts()
        {
            Systems_PlayCall call = Agent_PlayCaller.Choose(
                down: 4, yardsToGo: 8f, yardsToGoal: MIDFIELD,
                isInFieldGoalRange: false, nextUnit: AlwaysLow);

            Assert.That(call, Is.EqualTo(Systems_PlayCall.Punt));
        }

        [Test]
        public void FourthAndLong_InFieldGoalRange_KicksIt()
        {
            Systems_PlayCall call = Agent_PlayCaller.Choose(
                down: 4, yardsToGo: 8f, yardsToGoal: 30f,
                isInFieldGoalRange: true, nextUnit: AlwaysLow);

            Assert.That(call, Is.EqualTo(Systems_PlayCall.FieldGoal));
        }

        [Test]
        public void InchesToGo_TheQuarterbackKeepsIt()
        {
            Systems_PlayCall call = Agent_PlayCaller.Choose(
                down: 3, yardsToGo: 1f, yardsToGoal: MIDFIELD,
                isInFieldGoalRange: false, nextUnit: AlwaysLow);

            Assert.That(call, Is.EqualTo(Systems_PlayCall.KeepQuarterback));
        }

        /// <summary>
        /// The regression test for the original defect, stated as the property that
        /// actually matters: sweep the whole space of situations and assert that
        /// every call in the playbook comes up at least once.
        /// </summary>
        [Test]
        public void AcrossEverySituation_TheWholePlaybookIsReachable()
        {
            bool[] seen = new bool[Enum.GetValues(typeof(Systems_PlayCall)).Length];

            // A draw that walks the unit interval, so both sides of every mix and
            // both handoffs are exercised as the sweep runs.
            int tick = 0;
            Func<float> sweeping = () => ((tick++) % 10) / 10f;

            for (int down = 1; down <= Systems_GameRules.DOWNS_PER_SERIES; down++)
            {
                for (float toGo = 1f; toGo <= 15f; toGo += 1f)
                {
                    for (float toGoal = 5f; toGoal <= 95f; toGoal += 5f)
                    {
                        bool inRange = toGoal + Systems_GameRules.FIELD_GOAL_SNAP_YARDS
                            <= Systems_GameRules.FIELD_GOAL_MAX_YARDS;

                        seen[(int)Agent_PlayCaller.Choose(
                            down, toGo, toGoal, inRange, sweeping)] = true;
                    }
                }
            }

            Assert.That(seen[(int)Systems_PlayCall.Pass], Is.True, "Pass");
            Assert.That(
                seen[(int)Systems_PlayCall.HandoffHalfback], Is.True, "HandoffHalfback");
            Assert.That(
                seen[(int)Systems_PlayCall.HandoffFullback], Is.True, "HandoffFullback");
            Assert.That(
                seen[(int)Systems_PlayCall.KeepQuarterback], Is.True, "KeepQuarterback");
            Assert.That(seen[(int)Systems_PlayCall.Punt], Is.True, "Punt");
            Assert.That(seen[(int)Systems_PlayCall.FieldGoal], Is.True, "FieldGoal");

            Assert.That(
                seen[(int)Systems_PlayCall.None], Is.False,
                "None is the state before a call, never a call.");
        }

        // --- Agreement with the action mask ----------------------------------

        /// <summary>
        /// Agent_FootballPlayer.WriteDiscreteActionMask disables Punt and FieldGoal
        /// on downs one to three. A call that proposed one there would be silently
        /// overridden by the mask, which reads as the quarterback ignoring its own
        /// decision — so the policy must never produce one.
        /// </summary>
        [Test]
        public void OnFirstThroughThirdDown_NeitherKickIsEverCalled(
            [Values(1, 2, 3)] int down)
        {
            int tick = 0;
            Func<float> sweeping = () => ((tick++) % 10) / 10f;

            for (float toGo = 1f; toGo <= 15f; toGo += 0.5f)
            {
                for (float toGoal = 5f; toGoal <= 95f; toGoal += 5f)
                {
                    Systems_PlayCall call = Agent_PlayCaller.Choose(
                        down, toGo, toGoal, isInFieldGoalRange: true,
                        nextUnit: sweeping);

                    Assert.That(
                        call,
                        Is.Not.EqualTo(Systems_PlayCall.Punt).And
                            .Not.EqualTo(Systems_PlayCall.FieldGoal),
                        $"down {down}, {toGo} to go, {toGoal} from the goal");
                }
            }
        }

        /// <summary>
        /// The mask also disables the field goal out of range, on every down.
        /// </summary>
        [Test]
        public void OutOfRange_TheFieldGoalIsNeverCalled()
        {
            int tick = 0;
            Func<float> sweeping = () => ((tick++) % 10) / 10f;

            for (int down = 1; down <= Systems_GameRules.DOWNS_PER_SERIES; down++)
            {
                for (float toGo = 1f; toGo <= 15f; toGo += 0.5f)
                {
                    Systems_PlayCall call = Agent_PlayCaller.Choose(
                        down, toGo, yardsToGoal: 80f, isInFieldGoalRange: false,
                        nextUnit: sweeping);

                    Assert.That(call, Is.Not.EqualTo(Systems_PlayCall.FieldGoal));
                }
            }
        }

        // --- Down weighting ---------------------------------------------------

        /// <summary>
        /// Early downs lean run and late downs lean pass. Asserted as an ordering
        /// rather than against the literals, so tuning the shares does not break the
        /// test but inverting them does.
        /// </summary>
        [Test]
        public void TheRunShareFallsAsTheDownRises()
        {
            Assert.That(
                Agent_PlayCaller.RunShareFor(1),
                Is.GreaterThan(Agent_PlayCaller.RunShareFor(2)));

            Assert.That(
                Agent_PlayCaller.RunShareFor(2),
                Is.GreaterThan(Agent_PlayCaller.RunShareFor(3)));
        }

        [Test]
        public void OnThirdAndLong_ItPassesWhateverTheDraw()
        {
            Assert.That(
                Agent_PlayCaller.Choose(
                    down: 3, yardsToGo: 10f, yardsToGoal: MIDFIELD,
                    isInFieldGoalRange: false, nextUnit: AlwaysLow),
                Is.EqualTo(Systems_PlayCall.Pass));

            Assert.That(
                Agent_PlayCaller.Choose(
                    down: 3, yardsToGo: 10f, yardsToGoal: MIDFIELD,
                    isInFieldGoalRange: false, nextUnit: AlwaysHigh),
                Is.EqualTo(Systems_PlayCall.Pass));
        }

        /// <summary>
        /// Inside the red zone the offense runs regardless of distance — a pass into
        /// a compressed field is the harder throw, and the run is what scores here.
        /// </summary>
        [Test]
        public void InTheRedZone_ItRuns()
        {
            Systems_PlayCall call = Agent_PlayCaller.Choose(
                down: 2, yardsToGo: 9f, yardsToGoal: 12f,
                isInFieldGoalRange: true, nextUnit: AlwaysLow);

            Assert.That(
                call,
                Is.EqualTo(Systems_PlayCall.HandoffHalfback).Or
                    .EqualTo(Systems_PlayCall.HandoffFullback));
        }

        /// <summary>
        /// Both backs carry. The draw is the only thing that separates them, so a
        /// low draw and a high draw must not produce the same runner.
        /// </summary>
        [Test]
        public void BothBacksGetTheBall()
        {
            Systems_PlayCall low = Agent_PlayCaller.Choose(
                down: 1, yardsToGo: 2f, yardsToGoal: MIDFIELD,
                isInFieldGoalRange: false, nextUnit: AlwaysLow);

            Systems_PlayCall high = Agent_PlayCaller.Choose(
                down: 1, yardsToGo: 2f, yardsToGoal: MIDFIELD,
                isInFieldGoalRange: false, nextUnit: AlwaysHigh);

            Assert.That(low, Is.EqualTo(Systems_PlayCall.HandoffHalfback));
            Assert.That(high, Is.EqualTo(Systems_PlayCall.HandoffFullback));
        }

        // --- Determinism ------------------------------------------------------

        /// <summary>
        /// The policy holds no state of its own: the same situation and the same
        /// draw give the same call, every time. That is what lets a training run
        /// with a pinned seed replay call for call while a played game, drawing from
        /// a clock-seeded stream, does not.
        /// </summary>
        [Test]
        public void TheSameSituationAndDrawAlwaysGiveTheSameCall()
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                Assert.That(
                    Agent_PlayCaller.Choose(
                        down: 2, yardsToGo: 6f, yardsToGoal: 40f,
                        isInFieldGoalRange: true, nextUnit: AlwaysLow),
                    Is.EqualTo(
                        Agent_PlayCaller.Choose(
                            down: 2, yardsToGo: 6f, yardsToGoal: 40f,
                            isInFieldGoalRange: true, nextUnit: AlwaysLow)));
            }
        }
    }
}
