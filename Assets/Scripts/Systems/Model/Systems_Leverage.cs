using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// How much the next snap matters, as one number in [0, 1].
    ///
    /// WHY IT IS HERE AND NOT IN A VIEW. Two views want it — the crowd bed and the
    /// stadium rig — and a stadium whose noise and whose lights disagree about
    /// whether this is a big moment is worse than one that reacts to nothing. It
    /// is a pure read of Systems_GameModel, the same kind of derived fact as
    /// YardsToGoal and IsGoalToGo, so it lives beside the model and both views
    /// call the one function.
    ///
    /// THREE THINGS RAISE IT, BECAUSE THOSE ARE THE THREE THINGS A REAL CROWD
    /// STANDS UP FOR: a late down, the ball near the goal line, and a close game
    /// running out of clock. They add rather than multiply, so fourth-and-goal in
    /// the first quarter is still loud and a blowout's final minute is not.
    ///
    /// Presentation only. Nothing a policy observes or is rewarded on reads this.
    /// </summary>
    public static class Systems_Leverage
    {
        private const float THIRD_DOWN = 0.25f;
        private const float FOURTH_DOWN = 0.5f;

        /// <summary>The red zone, in yards from the goal line being attacked.</summary>
        private const float RED_ZONE_YARDS = 20f;

        private const float RED_ZONE_WEIGHT = 0.3f;

        /// <summary>
        /// Fraction of the final quarter over which the clock term ramps in. The
        /// two-minute warning is two of fifteen minutes; that is too short a window
        /// against Systems_GameRules.HUDDLE_SECONDS, which burns twelve seconds a
        /// play — the whole ramp would pass in three snaps.
        /// </summary>
        private const float LATE_FRACTION = 0.4f;

        private const float LATE_WEIGHT = 0.4f;

        public static float Of(Systems_GameModel game)
        {
            if (game.Phase != Systems_GamePhase.Playing
                && game.Phase != Systems_GamePhase.Overtime)
            {
                return 0f;
            }

            float leverage = 0f;

            if (game.Down >= Systems_GameRules.DOWNS_PER_SERIES)
            {
                leverage += FOURTH_DOWN;
            }
            else if (game.Down == Systems_GameRules.DOWNS_PER_SERIES - 1)
            {
                leverage += THIRD_DOWN;
            }

            leverage += RED_ZONE_WEIGHT * Mathf.Clamp01(1f - (game.YardsToGoal / RED_ZONE_YARDS));
            leverage += LATE_WEIGHT * LateAndClose(game);

            return Mathf.Clamp01(leverage);
        }

        /// <summary>
        /// Zero unless the game is within one score in the final quarter, then
        /// ramping to one as the clock runs out. Overtime is sudden death, so every
        /// snap of it is the last minute.
        /// </summary>
        private static float LateAndClose(Systems_GameModel game)
        {
            if (game.Phase == Systems_GamePhase.Overtime)
            {
                return 1f;
            }

            if (game.Quarter < Systems_GameRules.QUARTER_COUNT)
            {
                return 0f;
            }

            // ONSIDE_TRAILING_BY is the project's existing definition of "more than
            // one score behind"; anything inside it is a one-score game.
            if (Mathf.Abs(game.HomeScore - game.AwayScore) > Systems_GameRules.ONSIDE_TRAILING_BY)
            {
                return 0f;
            }

            float window = Systems_GameRules.QUARTER_SECONDS * LATE_FRACTION;

            return Mathf.Clamp01(1f - (game.SecondsRemaining / window));
        }
    }
}
