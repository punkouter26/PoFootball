namespace PoFootball.Models
{
    /// <summary>
    /// Every tuned constant of the *game* layer, in one place — the counterpart to
    /// <see cref="Systems_SimConstants"/>, which owns the physics layer.
    ///
    /// The split matters. Nothing here can change the dynamics an .onnx was fitted
    /// against, so these values are safe to tune between builds without
    /// invalidating a trained brain. Anything that changes how a body moves belongs
    /// in Systems_SimConstants and is frozen for the life of a policy.
    /// </summary>
    public static class Systems_GameRules
    {
        // --- Clock -----------------------------------------------------------
        public const int QUARTER_COUNT = 4;

        /// <summary>
        /// Seconds per quarter. Five minutes rather than fifteen, which is what
        /// makes the final whistle reachable in a sitting.
        ///
        /// The arithmetic, since the previous note here had it badly wrong: nearly
        /// all of a play's cost is HUDDLE_SECONDS, and a play is only live for a
        /// few seconds on top of that. At roughly 28 s of game clock per down this
        /// is about eleven plays a quarter and forty-odd in a game — not the
        /// "~25 a quarter" claimed before, which assumed a huddle less than half
        /// the length of the one two lines below it.
        /// </summary>
        public const float QUARTER_SECONDS = 300f;

        /// <summary>
        /// Clock burned between snaps while the clock runs — the huddle. Without
        /// it a quarter would take hundreds of plays, because a play itself only
        /// consumes the handful of seconds it is physically live.
        /// </summary>
        public const float HUDDLE_SECONDS = 25f;

        /// <summary>Seconds per physics tick. Mirrors the pinned fixed timestep.</summary>
        public const float SECONDS_PER_TICK = 0.02f;

        /// <summary>
        /// Physics ticks the ball stays dead between the whistle and the next snap
        /// — 140 ticks = 2.8 s at the pinned 50 Hz.
        ///
        /// Sized to outlast Systems_HudView.BANNER_SECONDS (2.2 s) with room either
        /// side, because the banner is the only place a viewer is ever told what
        /// just happened. Shorter and the announcement is cut off; much longer and
        /// the game stops feeling like it is being played.
        ///
        /// The game clock does not run during it. Football charges the interval
        /// between plays as HUDDLE_SECONDS at the whistle, which is already counted
        /// — burning this as well would bill the offense twice for the same gap.
        /// </summary>
        public const int DEAD_BALL_TICKS = 140;

        // --- Downs -----------------------------------------------------------
        public const int DOWNS_PER_SERIES = 4;

        public const float YARDS_TO_GAIN = 10f;

        // --- Scoring ---------------------------------------------------------
        public const int TOUCHDOWN_POINTS = 6;

        /// <summary>
        /// The try after a touchdown is awarded rather than simulated. There is no
        /// kicking model in this sim and inventing a random one would put noise
        /// into the score that no policy can influence.
        ///
        /// It stays awarded even now that FIELD_GOAL_MAX_YARDS exists, and the two
        /// are consistent rather than contradictory: a real extra point is a 33-yard
        /// kick, comfortably inside the 55-yard range below, so simulating it would
        /// return "good" every single time anyway.
        /// </summary>
        public const int EXTRA_POINT_POINTS = 1;

        public const int SAFETY_POINTS = 2;

        public const int FIELD_GOAL_POINTS = 3;

        // --- Kicking ---------------------------------------------------------
        /// <summary>
        /// Yards added to the distance-to-goal-line to get the real length of a
        /// field goal: ten yards of end zone plus seven yards from the line of
        /// scrimmage back to the hold. A kick from the opponent's 30 is therefore a
        /// 47-yarder, which is how every broadcast and every kicker measures it.
        /// </summary>
        public const float FIELD_GOAL_SNAP_YARDS = 17f;

        /// <summary>
        /// Longest field goal that goes in. Beyond it the attempt is short.
        ///
        /// DETERMINISTIC ON PURPOSE, AND THIS IS A REAL SIMPLIFICATION. Actual NFL
        /// kickers are a curve, not a cliff: roughly 95% inside 30 yards, 85% from
        /// 30-39, 75% from 40-49, 60% from 50-59, and about 35% from 60 and out. A
        /// sampled curve would be more faithful and would also inject variance the
        /// offense cannot influence — exactly the objection EXTRA_POINT_POINTS
        /// already raises against simulating the try. 55 sits in the middle of the
        /// real 50-59 band, so "inside 55" stands in for "makeable" while keeping
        /// the outcome a pure function of field position, which is something a
        /// policy can actually learn to exploit.
        /// </summary>
        public const float FIELD_GOAL_MAX_YARDS = 55f;

        /// <summary>
        /// Net yards a punt gains, measured from the line of scrimmage to where the
        /// receiving team next snaps it. NFL net punting averages sit in the high
        /// thirties to low forties; 40 is the round number in the middle of that.
        ///
        /// Flat rather than a function of field position, so the DECISION to punt is
        /// what gets learned rather than the execution of it. A punter who is better
        /// when backed up is a detail this sim has no model for.
        /// </summary>
        public const float PUNT_NET_YARDS = 40f;

        // --- Field position after a dead ball --------------------------------
        /// <summary>
        /// Where a punt that reaches the end zone is spotted: the receiving team's
        /// own 20. Deliberately NOT the same constant as a kickoff touchback —
        /// they are different rules and different yard lines, and this sim used to
        /// run both through one 25.
        /// </summary>
        public const float PUNT_TOUCHBACK_YARD_LINE = 20f;

        /// <summary>
        /// Where a kickoff touchback is spotted: the receiving team's own 35, per
        /// the 2025 dynamic-kickoff rule. Used at the start of each half and after
        /// every score.
        /// </summary>
        public const float KICKOFF_TOUCHBACK_YARD_LINE = 35f;

        /// <summary>
        /// Where the team that was awarded a safety takes over.
        ///
        /// The conceding team free-kicks from its own 20 and that kick is returned,
        /// so the scoring side starts around its own 40 in practice. This sim used
        /// to hand the scoring team the ball on its OWN 20 — punishing the side that
        /// had just made a play, and making a safety close to a wash.
        /// </summary>
        public const float SAFETY_FREE_KICK_RESULT_YARD_LINE = 40f;

        /// <summary>
        /// A drive may not start inside an end zone, so every computed line of
        /// scrimmage is clamped this many yards clear of both goal lines.
        /// </summary>
        public const float MIN_YARDS_FROM_GOAL_LINE = 1f;
    }
}
