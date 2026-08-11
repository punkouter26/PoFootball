using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Rewards
{
    /// <summary>
    /// The price of predictability: what an over-used play call costs the
    /// quarterback.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// Run football_base05 collapsed. By 3.1M steps the quarterback called the
    /// halfback handoff on 99% of downs — Call/Entropy 0.10 against a possible
    /// 1.609, Call/Pass and Call/Keep both exactly 0.000. The defense then spent
    /// four million steps learning to stop that one play and the offense had
    /// nothing to switch to, so TouchdownRate fell 0.82 -> 0.08.
    ///
    /// The policy was not malfunctioning. Under the reward of the time, one call
    /// really was worth several times the others, and the trainer's entropy bonus
    /// — already four times every other brain's — could not out-argue that gap.
    /// The underlying imbalance is fixed in Systems_SimConstants, but a reward
    /// that merely makes the alternatives competitive still leaves a policy free
    /// to converge on whichever one edges ahead. Football does not work that way:
    /// a play that is known is a play that is stopped, and that property has to be
    /// somewhere in the reward or the optimizer cannot see it.
    ///
    /// WHY NOT JUST RAISE beta. The entropy bonus applies to the whole action
    /// distribution, and the quarterback's distribution includes the continuous
    /// aim vector. Raising beta far enough to keep four discrete calls in play
    /// would inject the same magnitude of noise into where the ball is thrown.
    /// Splitting the quarterback into its own brain made beta usable; it did not
    /// make it precise. This term prices exactly one thing.
    ///
    /// Pure scalar function — no state, no allocation. The caller owns the
    /// history, which keeps PoFootball.Rewards a leaf assembly.
    /// </summary>
    public static class Reward_Call
    {
        /// <summary>
        /// The share of recent calls a single call would have under an even split.
        /// <see cref="Systems_PlayCall.None"/> is excluded — it is "no call yet",
        /// not a play, and a quarterback is never meant to choose it.
        /// </summary>
        public const float EVEN_SHARE = 1f / 4f;

        /// <summary>
        /// Penalty for having called this play <paramref name="share"/> of the
        /// time recently. Zero at or below an even split, growing linearly to
        /// -CALL_REPETITION_PENALTY * (1 - EVEN_SHARE) at total collapse.
        ///
        /// Returned as a signed reward, so the caller adds it like any other term.
        /// </summary>
        public static float Repetition(float share)
        {
            float excess = Mathf.Clamp01(share) - EVEN_SHARE;

            if (excess <= 0f)
            {
                return 0f;
            }

            return -excess * Systems_SimConstants.CALL_REPETITION_PENALTY;
        }

        /// <summary>
        /// Share of <paramref name="counts"/> held by <paramref name="call"/>.
        /// Returns 0 when nothing has been recorded yet, so the first plays of a
        /// run are never penalised.
        ///
        /// counts is indexed by Systems_PlayCall, including slot 0 for None; a
        /// None that was never a real choice would otherwise dilute the shares and
        /// quietly weaken the penalty.
        /// </summary>
        public static float ShareOf(int[] counts, Systems_PlayCall call)
        {
            if (counts == null)
            {
                return 0f;
            }

            int index = (int)call;

            if (index < 0 || index >= counts.Length)
            {
                return 0f;
            }

            int total = 0;

            for (int slot = 0; slot < counts.Length; slot++)
            {
                if (slot != (int)Systems_PlayCall.None)
                {
                    total += counts[slot];
                }
            }

            return total <= 0 ? 0f : counts[index] / (float)total;
        }
    }
}
