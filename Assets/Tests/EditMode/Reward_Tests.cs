using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Rewards;
using PoFootball.Sensors;
using UnityEngine;

namespace PoFootball.Tests
{
    public sealed class Reward_Tests
    {
        /// <summary>
        /// Self-play ELO rates offense against defense. If any outcome paid both
        /// sides positively the two policies could collude into a shared local
        /// optimum instead of competing, and the ELO curves would stop meaning
        /// anything.
        /// </summary>
        [Test]
        public void TerminalReward_IsZeroSumForEveryOutcome()
        {
            float[] yardages = { -20f, -1f, 0f, 1f, 45f };
            bool[] completions = { false, true };

            foreach (Systems_PlayOutcome outcome in
                     System.Enum.GetValues(typeof(Systems_PlayOutcome)))
            {
                foreach (float netYards in yardages)
                {
                    foreach (bool completed in completions)
                    {
                        float offense = Reward_Terminal.For(
                            Systems_TeamSide.Offense, outcome, netYards, completed);
                        float defense = Reward_Terminal.For(
                            Systems_TeamSide.Defense, outcome, netYards, completed);

                        Assert.That(
                            offense + defense, Is.EqualTo(0f).Within(1e-5f),
                            $"{outcome} at {netYards} yards (completed {completed}) "
                            + $"pays {offense} / {defense}");
                    }
                }
            }
        }

        [Test]
        public void Touchdown_IsTheBestOutcomeForTheOffense()
        {
            float touchdown = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Touchdown, 40f, false);
            float tackle = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 4f, false);
            float timeout = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.TimeExpired, 4f, false);

            Assert.That(touchdown, Is.GreaterThan(tackle));
            Assert.That(touchdown, Is.GreaterThan(timeout));
            Assert.That(touchdown, Is.GreaterThan(0f));
        }

        [Test]
        public void TackleBehindTheLine_HurtsTheOffenseMoreThanATackleAfterAGain()
        {
            float forLoss = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, -3f, false);
            float afterGain = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 6f, false);

            Assert.That(forLoss, Is.LessThan(afterGain));
        }

        [Test]
        public void NoOutcome_PaysNothing()
        {
            Assert.That(
                Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.None, 10f, false),
                Is.EqualTo(0f));
        }

        [Test]
        public void ForwardProgress_PaysTheOffenseAndCostsTheDefense()
        {
            float offense = Reward_Progress.PerTick(Systems_TeamSide.Offense, 1f);
            float defense = Reward_Progress.PerTick(Systems_TeamSide.Defense, 1f);

            Assert.That(offense, Is.GreaterThan(0f));
            Assert.That(defense, Is.LessThan(0f));
        }

        /// <summary>
        /// The anti-stalling guarantee: with the ball going nowhere the offense
        /// must still be losing something, or doing nothing becomes free.
        /// </summary>
        [Test]
        public void StandingStill_CostsTheOffense()
        {
            float offense = Reward_Progress.PerTick(Systems_TeamSide.Offense, 0f);

            Assert.That(offense, Is.LessThan(0f));
        }

        [Test]
        public void StandingStill_IsNeutralForTheDefense()
        {
            Assert.That(
                Reward_Progress.PerTick(Systems_TeamSide.Defense, 0f),
                Is.EqualTo(0f).Within(1e-6f));
        }

        [Test]
        public void LosingGround_PaysTheDefense()
        {
            Assert.That(
                Reward_Progress.PerTick(Systems_TeamSide.Defense, -2f),
                Is.GreaterThan(0f));
        }

        [Test]
        public void DenseRewardOverAWholePlay_DoesNotDwarfTheTerminalReward()
        {
            // A tackle after a typical gain should still be a meaningful share of
            // the play's total signal. If dense yardage overwhelms it, the policy
            // optimises yards and stops caring how the play ends — which is what
            // run football_base01 did.
            const float typicalGainYards = 6f;

            float dense = typicalGainYards * Systems_SimConstants.YARD_REWARD_SCALE;
            float terminal =
                Mathf_Abs(Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, typicalGainYards, false));

            Assert.That(
                terminal, Is.GreaterThan(dense),
                $"dense {dense} outweighs terminal {terminal} on a routine play");
        }

        /// <summary>
        /// The test above, at the gain that actually broke football_base05.
        ///
        /// It only ever checked a SIX yard carry, where 0.05/yd came to 0.30
        /// against a 0.50 tackle and passed comfortably. The inversion lived in the
        /// tail: at fifty yards the same scale paid 2.50, and a hundred-yard drive
        /// paid 5.00 against a TOUCHDOWN_REWARD of 1.0 — so the longest possible
        /// run was worth five times the score it produced, and the policy duly
        /// optimised yards and ignored the end zone. The offense's mean reward at
        /// 7.5M steps was +0.702 and its yardage term alone was +0.675.
        ///
        /// A regression guard that only samples the typical case cannot see a
        /// problem that only exists in the tail.
        /// </summary>
        [Test]
        public void ScoringAlwaysOutweighsTheGroundCoveredToReachIt()
        {
            // The longest drive the field allows.
            const float fullFieldYards = 100f;

            float dense = fullFieldYards * Systems_SimConstants.YARD_REWARD_SCALE;

            Assert.That(
                Systems_SimConstants.TOUCHDOWN_REWARD,
                Is.GreaterThanOrEqualTo(dense),
                $"a {fullFieldYards} yd drive pays {dense} in dense yardage against a "
                + $"touchdown worth {Systems_SimConstants.TOUCHDOWN_REWARD}; yardage "
                + "dominates scoring and the policy will farm it");
        }

        // --- Play-call diversity ---------------------------------------------

        [Test]
        public void CallRepetition_IsFreeAtAnEvenSplit()
        {
            Assert.That(Reward_Call.Repetition(Reward_Call.EVEN_SHARE), Is.Zero);
            Assert.That(Reward_Call.Repetition(0f), Is.Zero);
        }

        [Test]
        public void CallRepetition_CostsMoreTheMoreOneCallIsRepeated()
        {
            float half = Reward_Call.Repetition(0.5f);
            float collapsed = Reward_Call.Repetition(0.99f);

            Assert.That(half, Is.LessThan(0f), "an over-used call should cost something");
            Assert.That(
                collapsed, Is.LessThan(half),
                "near-total collapse should cost more than a mere preference");
        }

        /// <summary>
        /// The penalty has to be worth more than the play it is discouraging, or it
        /// is decoration. Priced against a routine gain under the corrected yardage
        /// scale — not against a breakaway, which the policy should still chase.
        /// </summary>
        [Test]
        public void CallRepetition_AtCollapseOutweighsARoutineGain()
        {
            const float routineGainYards = 6f;

            float routine = routineGainYards * Systems_SimConstants.YARD_REWARD_SCALE;
            float collapsed = Mathf_Abs(Reward_Call.Repetition(0.99f));

            Assert.That(
                collapsed, Is.GreaterThan(routine),
                $"collapse costs {collapsed} but a routine carry pays {routine}; the "
                + "quarterback can pay the penalty out of petty cash");
        }

        [Test]
        public void CallShare_IgnoresNoneAndReportsTheRest()
        {
            int[] counts = new int[Sensor_FootballState.PLAY_CALL_BRANCH_SIZE];
            counts[(int)Systems_PlayCall.None] = 50;
            counts[(int)Systems_PlayCall.HandoffHalfback] = 3;
            counts[(int)Systems_PlayCall.Pass] = 1;

            // 3 of the 4 real calls, not 3 of 54.
            Assert.That(
                Reward_Call.ShareOf(counts, Systems_PlayCall.HandoffHalfback),
                Is.EqualTo(0.75f).Within(1e-5f));
        }

        [Test]
        public void CallShare_IsZeroBeforeAnythingHasBeenCalled()
        {
            int[] counts = new int[Sensor_FootballState.PLAY_CALL_BRANCH_SIZE];

            Assert.That(
                Reward_Call.ShareOf(counts, Systems_PlayCall.Pass), Is.Zero,
                "the first call of a run must not be penalised");
        }

        // --- Passing economics, priced -----------------------------------------

        /// <summary>
        /// Throwing has to be worth doing at a completion rate the policy can
        /// actually reach early on.
        ///
        /// football_base05 threw at 27-44% and needed 39% just to break even, so
        /// passing was EV-negative against a handoff that reliably paid its
        /// yardage. Call/Pass reached 0.000 and stayed there — the second run in a
        /// row to abandon the passing game outright, for a different reason than
        /// base03 did.
        /// </summary>
        [Test]
        public void Passing_IsWorthAttemptingAtAReachableCompletionRate()
        {
            const float completionRate = 0.35f;
            const float interceptionRate = 0.15f;
            float incompletionRate = 1f - completionRate - interceptionRate;

            float expected =
                (completionRate * Systems_SimConstants.COMPLETION_REWARD)
                + (incompletionRate * -Systems_SimConstants.INCOMPLETION_PENALTY)
                + (interceptionRate * -Systems_SimConstants.INTERCEPTION_REWARD);

            Assert.That(
                expected, Is.GreaterThan(0f),
                $"a pass completing {completionRate:P0} of the time has expected value "
                + $"{expected}; the quarterback is correct to never throw");
        }

        // --- Passing economics -----------------------------------------------

        /// <summary>
        /// The reason football_base03 never threw a single pass in 2.4M steps.
        ///
        /// COMPLETION_REWARD was declared and read by nothing — Reward_Terminal had
        /// no completion branch at all — while INCOMPLETION_PENALTY was live and
        /// LARGER than it. Every pass outcome the reward could see was a bad one,
        /// so throwing was strictly dominated before aim could possibly have been
        /// learned and Call/Pass sat at 0.000 for the whole run.
        /// </summary>
        [Test]
        public void CompletingAPass_PaysTheOffense()
        {
            float completed = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 8f, true);
            float notCompleted = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 8f, false);

            Assert.That(
                completed, Is.GreaterThan(notCompleted),
                "a completed pass pays the offense nothing over an identical run");
        }

        [Test]
        public void CompletionIsWorthMoreThanAnIncompletionCosts()
        {
            Assert.That(
                Systems_SimConstants.COMPLETION_REWARD,
                Is.GreaterThan(Systems_SimConstants.INCOMPLETION_PENALTY),
                "an untrained throw is negative expected value, so the policy learns "
                + "never to throw long before it could learn to throw accurately");
        }

        [Test]
        public void AnIncompletion_CostsNoMoreThanBeingTackledForNoGain()
        {
            float incompletion = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Incompletion, 0f, false);
            float tackled = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 0f, false);

            Assert.That(
                incompletion, Is.GreaterThanOrEqualTo(tackled),
                "throwing the ball away is punished harder than being dropped for no gain");
        }

        /// <summary>
        /// A safety is the floor of the reward function, and an interception is the
        /// worst thing that can happen short of one.
        ///
        /// This test used to say an interception was the worst outcome outright, and
        /// it was right until a safety became a distinct outcome. It is not a
        /// close call: an interception costs the ball, while a safety costs the ball
        /// AND two points, so anything that ranked them equal was mispricing the
        /// difference between losing possession and being scored on.
        /// </summary>
        [Test]
        public void ASafety_IsTheWorstOutcomeForTheOffense()
        {
            float safety = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Safety, 12f, false);

            foreach (Systems_PlayOutcome outcome in
                     System.Enum.GetValues(typeof(Systems_PlayOutcome)))
            {
                if (outcome == Systems_PlayOutcome.Safety)
                {
                    continue;
                }

                Assert.That(
                    safety,
                    Is.LessThanOrEqualTo(
                        Reward_Terminal.For(Systems_TeamSide.Offense, outcome, 12f, false)),
                    $"{outcome} is worse for the offense than conceding a safety");
            }
        }

        [Test]
        public void AnInterception_IsTheWorstOutcomeShortOfASafety()
        {
            float interception = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Interception, 12f, false);

            foreach (Systems_PlayOutcome outcome in
                     System.Enum.GetValues(typeof(Systems_PlayOutcome)))
            {
                if (outcome == Systems_PlayOutcome.Interception
                    || outcome == Systems_PlayOutcome.Safety)
                {
                    continue;
                }

                Assert.That(
                    interception,
                    Is.LessThanOrEqualTo(
                        Reward_Terminal.For(Systems_TeamSide.Offense, outcome, 12f, false)),
                    $"{outcome} is worse for the offense than a turnover");
            }
        }

        /// <summary>
        /// Punting has to be cheap. It is the correct call on most fourth downs, and
        /// a reward function that prices it like a turnover teaches the offense to
        /// avoid the right decision — which is how you end up with a policy going for
        /// it on 4th and 12 from its own 15.
        /// </summary>
        [Test]
        public void Punting_CostsFarLessThanTurningTheBallOver()
        {
            float punt = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Punt, 0f, false);
            float interception = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Interception, 0f, false);
            float tackledShort = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, -1f, false);

            Assert.That(punt, Is.GreaterThan(interception));
            Assert.That(
                punt, Is.GreaterThan(tackledShort),
                "punting must beat being dropped short of the sticks");
            Assert.That(punt, Is.LessThan(0f), "a punt is still a drive that failed");
        }

        /// <summary>
        /// Three points must stay clearly worth less than seven, or a policy learns
        /// to stop driving at the twenty and take the kick every time.
        /// </summary>
        [Test]
        public void AFieldGoal_PaysWellBelowATouchdown()
        {
            float fieldGoal = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.FieldGoalGood, 0f, false);
            float touchdown = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Touchdown, 0f, false);
            float missed = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.FieldGoalMissed, 0f, false);
            float punt = Reward_Terminal.For(
                Systems_TeamSide.Offense, Systems_PlayOutcome.Punt, 0f, false);

            Assert.That(fieldGoal, Is.GreaterThan(0f));
            Assert.That(fieldGoal, Is.LessThan(touchdown * 0.6f));
            Assert.That(
                missed, Is.LessThan(punt),
                "missing gives the ball up seven yards behind the line; punting does not");
        }

        // --- Role-shaped terms -------------------------------------------------

        /// <summary>
        /// These must stay an order of magnitude below the terminal rewards. A
        /// shaped term worth more than a touchdown is one the policy will farm
        /// instead of playing the game.
        /// </summary>
        [Test]
        public void RoleShapedTerms_CannotOutweighTheTerminalRewardOverAWholePlay()
        {
            float ticks = Systems_PlayModel.MAX_PHYSICS_TICKS;

            float maxBlock = ticks * Systems_SimConstants.BLOCK_REWARD_PER_TICK;
            float maxSeparation = ticks * Systems_SimConstants.SEPARATION_REWARD_PER_TICK;

            // A quarter of a touchdown is the ceiling: these shape which behaviour
            // is explored, they do not decide who wins the play.
            float ceiling = Systems_SimConstants.TOUCHDOWN_REWARD * 0.25f;

            Assert.That(maxBlock, Is.LessThan(ceiling));
            Assert.That(maxSeparation, Is.LessThan(ceiling));
        }

        [Test]
        public void Block_PaysOnlyWhenTheBlockerIsBetweenTheRusherAndTheBall()
        {
            Vector2 rusher = new Vector2(0f, 0f);
            Vector2 ball = new Vector2(0f, -4f);

            // Squarely on the rusher's path to the ball.
            float shielding = Reward_Role.Block(new Vector2(0f, -1.5f), rusher, ball);

            // The same distance away, but the rusher is already past.
            float beaten = Reward_Role.Block(new Vector2(0f, 1.5f), rusher, ball);

            Assert.That(shielding, Is.GreaterThan(0f));
            Assert.That(beaten, Is.EqualTo(0f));
        }

        [Test]
        public void Block_PaysNothingBeyondEngageRange()
        {
            Vector2 rusher = Vector2.zero;
            Vector2 ball = new Vector2(0f, -10f);
            float beyond = Systems_SimConstants.BLOCK_ENGAGE_RANGE + 0.5f;

            Assert.That(
                Reward_Role.Block(new Vector2(0f, -beyond), rusher, ball),
                Is.EqualTo(0f));
        }

        /// <summary>
        /// Separation saturates so a receiver cannot farm it by running to an empty
        /// corner of the field and standing there.
        /// </summary>
        [Test]
        public void Separation_SaturatesBeyondItsRange()
        {
            float atRange = Reward_Role.Separation(
                Systems_SimConstants.SEPARATION_SATURATION_RANGE);
            float wellBeyond = Reward_Role.Separation(
                Systems_SimConstants.SEPARATION_SATURATION_RANGE * 5f);

            Assert.That(wellBeyond, Is.EqualTo(atRange).Within(1e-6f));
            Assert.That(Reward_Role.Separation(0f), Is.EqualTo(0f));
        }

        /// <summary>
        /// The negative half is the half that matters: a purely positive pursuit
        /// term is satisfied by a defender who stands still and waits.
        /// </summary>
        [Test]
        public void Pursuit_PaysForClosingAndCostsForRetreating()
        {
            Assert.That(Reward_Role.Pursuit(1f), Is.GreaterThan(0f));
            Assert.That(Reward_Role.Pursuit(-1f), Is.LessThan(0f));
            Assert.That(Reward_Role.Pursuit(0f), Is.EqualTo(0f));
        }

        [Test]
        public void RoleClassification_DoesNotOverlap()
        {
            foreach (Systems_PlayerRole role in System.Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                Assert.That(
                    Reward_Role.IsBlocker(role) && Reward_Role.IsReceiver(role),
                    Is.False,
                    $"{role} would earn both the blocking and the separation term");
            }
        }

        private static float Mathf_Abs(float value)
        {
            return value < 0f ? -value : value;
        }
    }
}
