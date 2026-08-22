using System;
using PoFootball.Models;
using PoFootball.Systems;

namespace PoFootball.Agents
{
    /// <summary>
    /// The scripted quarterback's play-call policy: down, distance and field
    /// position in, a <see cref="Systems_PlayCall"/> out.
    ///
    /// WHY THIS IS ITS OWN TYPE AND NOT A METHOD ON Agent_FootballPlayer. It used to
    /// be four private methods there, reading _play, _field and an RNG field off the
    /// agent. That made the one piece of football judgement in the codebase
    /// reachable only by standing up a MonoBehaviour, a Rigidbody2D, a registry and
    /// a live play — so it was never tested, and the bug it shipped with was exactly
    /// the kind a unit test catches in a second: the call keyed off field position
    /// alone, never returned Punt, FieldGoal or KeepQuarterback, and produced forty
    /// plays without a single first down.
    ///
    /// Everything it needs is four numbers and a draw, so it is a pure static
    /// function with no Unity types in its signature at all. Agent_FootballPlayer
    /// keeps the state; this keeps the judgement.
    ///
    /// EVERY BRANCH MIRRORS Agent_FootballPlayer.WriteDiscreteActionMask. The mask is
    /// the authority on what is legal — Punt on fourth down, FieldGoal on fourth down
    /// and in range — and a call that proposed a masked action would be silently
    /// overridden, which reads as the call being ignored. isInFieldGoalRange is
    /// passed in rather than recomputed here so the two cannot drift apart.
    ///
    /// NONE OF THIS CONSTRAINS A TRAINED BRAIN. It runs only under Heuristic, which
    /// drives every player whenever no promoted brain matches the current contract
    /// revision. A trained quarterback learns its own thresholds from the
    /// down-and-distance observations revision 6 added.
    /// </summary>
    internal static class Agent_PlayCaller
    {
        /// <summary>Distance at or under which the quarterback keeps it himself.</summary>
        internal const float SNEAK_YARDS = 1.5f;

        /// <summary>Distance at or under which a run is the obvious call.</summary>
        internal const float SHORT_YARDAGE_YARDS = 3f;

        /// <summary>Distance at or over which a pass is the obvious call.</summary>
        internal const float LONG_YARDAGE_YARDS = 7f;

        /// <summary>
        /// Inside this many yards of the goal line the offense runs by default, and
        /// a fourth-down punt would net almost nothing.
        /// </summary>
        internal const float RED_ZONE_YARDS = 25f;

        /// <summary>Share of first downs run. Early downs lean run; see RunShareFor.</summary>
        internal const float RUN_SHARE_FIRST_DOWN = 0.65f;

        internal const float RUN_SHARE_SECOND_DOWN = 0.5f;

        internal const float RUN_SHARE_LATE_DOWN = 0.25f;

        /// <summary>
        /// Picks the call.
        ///
        /// <paramref name="nextUnit"/> is a source of draws on [0, 1). It is passed
        /// in rather than owned so the caller's seeded stream stays the single source
        /// of randomness — a training run with a pinned seed replays call for call —
        /// and so a test can pin it to a constant and assert on one branch.
        /// </summary>
        internal static Systems_PlayCall Choose(
            int down,
            float yardsToGo,
            float yardsToGoal,
            bool isInFieldGoalRange,
            Func<float> nextUnit)
        {
            if (down >= Systems_GameRules.DOWNS_PER_SERIES)
            {
                return ChooseFourthDown(
                    yardsToGo, yardsToGoal, isInFieldGoalRange, nextUnit);
            }

            // Short yardage is a run everywhere on the field. The quarterback keeps
            // it himself on the shortest of them, which is the only call that ever
            // reaches KeepQuarterback.
            if (yardsToGo <= SNEAK_YARDS)
            {
                return Systems_PlayCall.KeepQuarterback;
            }

            if (yardsToGo <= SHORT_YARDAGE_YARDS || yardsToGoal <= RED_ZONE_YARDS)
            {
                return Handoff(nextUnit);
            }

            // Third and long is the one down that is a pass regardless of anything
            // else: there is no third call that gains eight yards.
            if (down >= 3 && yardsToGo >= LONG_YARDAGE_YARDS)
            {
                return Systems_PlayCall.Pass;
            }

            // OTHERWISE MIX, WEIGHTED BY DOWN — AND LEAN ON THE RUN EARLY.
            //
            // The first version keyed off field position instead: anything beyond
            // sixty yards from the goal was a pass. Every snap from a team's own half
            // is 60+ yards out, so that made first and second down a pass every time,
            // and a 40-play sample came back with twenty incompletions, ten tackles
            // and NOT ONE first down — every drive went three and out. Field position
            // is the wrong axis. The down is what a real offense calls from, and
            // running on early downs is what actually moves the chains here, because
            // a run gains ground every time and a pass gains ten or nothing.
            return nextUnit() < RunShareFor(down)
                ? Handoff(nextUnit)
                : Systems_PlayCall.Pass;
        }

        /// <summary>
        /// Fourth down: kick when kicking is what a real offense would do, and go for
        /// it only when it is short enough or close enough to be worth it.
        /// Deliberately conservative — the point is that the kicking calls actually
        /// occur, not that the heuristic plays optimally.
        /// </summary>
        private static Systems_PlayCall ChooseFourthDown(
            float yardsToGo,
            float yardsToGoal,
            bool isInFieldGoalRange,
            Func<float> nextUnit)
        {
            // Inside field-goal range, take the points unless it is short enough to
            // be a formality.
            if (isInFieldGoalRange && yardsToGo > SNEAK_YARDS)
            {
                return Systems_PlayCall.FieldGoal;
            }

            // Short, or close enough to the goal line that a punt would gain almost
            // nothing: go for it.
            if (yardsToGo <= SHORT_YARDAGE_YARDS || yardsToGoal <= RED_ZONE_YARDS)
            {
                return yardsToGo <= SNEAK_YARDS
                    ? Systems_PlayCall.KeepQuarterback
                    : Handoff(nextUnit);
            }

            return Systems_PlayCall.Punt;
        }

        /// <summary>
        /// Fraction of downs the heuristic runs on, by down. First down leans run,
        /// third down leans pass, which is roughly how a real offense is distributed
        /// and — more to the point here — is what keeps drives alive.
        /// </summary>
        internal static float RunShareFor(int down)
        {
            switch (down)
            {
                case 1:
                    return RUN_SHARE_FIRST_DOWN;
                case 2:
                    return RUN_SHARE_SECOND_DOWN;
                default:
                    return RUN_SHARE_LATE_DOWN;
            }
        }

        /// <summary>
        /// Which back carries. Drawn from the same stream as the call itself so both
        /// stay in the sample without a second source of randomness, and so a pinned
        /// seed still replays exactly.
        /// </summary>
        private static Systems_PlayCall Handoff(Func<float> nextUnit)
        {
            return nextUnit() < 0.5f
                ? Systems_PlayCall.HandoffHalfback
                : Systems_PlayCall.HandoffFullback;
        }
    }
}
