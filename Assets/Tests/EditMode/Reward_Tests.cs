using NUnit.Framework;
using PoFootball.Models;
using PoFootball.Rewards;

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

            foreach (Systems_PlayOutcome outcome in
                     System.Enum.GetValues(typeof(Systems_PlayOutcome)))
            {
                foreach (float netYards in yardages)
                {
                    float offense = Reward_Terminal.For(Systems_TeamSide.Offense, outcome, netYards);
                    float defense = Reward_Terminal.For(Systems_TeamSide.Defense, outcome, netYards);

                    Assert.That(
                        offense + defense, Is.EqualTo(0f).Within(1e-5f),
                        $"{outcome} at {netYards} yards pays {offense} / {defense}");
                }
            }
        }

        [Test]
        public void Touchdown_IsTheBestOutcomeForTheOffense()
        {
            float touchdown = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Touchdown, 40f);
            float tackle = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 4f);
            float timeout = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.TimeExpired, 4f);

            Assert.That(touchdown, Is.GreaterThan(tackle));
            Assert.That(touchdown, Is.GreaterThan(timeout));
            Assert.That(touchdown, Is.GreaterThan(0f));
        }

        [Test]
        public void TackleBehindTheLine_HurtsTheOffenseMoreThanATackleAfterAGain()
        {
            float forLoss = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, -3f);
            float afterGain = Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, 6f);

            Assert.That(forLoss, Is.LessThan(afterGain));
        }

        [Test]
        public void NoOutcome_PaysNothing()
        {
            Assert.That(
                Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.None, 10f),
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
                Mathf_Abs(Reward_Terminal.For(Systems_TeamSide.Offense, Systems_PlayOutcome.Tackle, typicalGainYards));

            Assert.That(
                terminal, Is.GreaterThan(dense),
                $"dense {dense} outweighs terminal {terminal} on a routine play");
        }

        private static float Mathf_Abs(float value)
        {
            return value < 0f ? -value : value;
        }
    }
}
