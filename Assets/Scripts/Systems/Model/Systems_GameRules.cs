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
        /// </summary>
        public const int EXTRA_POINT_POINTS = 1;

        public const int SAFETY_POINTS = 2;

        // --- Field position after a dead ball --------------------------------
        /// <summary>Own yard line a team starts from after conceding a touchdown.</summary>
        public const float TOUCHBACK_YARD_LINE = 25f;

        /// <summary>Own yard line the scoring team's opponent starts from after a safety.</summary>
        public const float SAFETY_RESTART_YARD_LINE = 20f;

        /// <summary>Own yard line each half opens from.</summary>
        public const float KICKOFF_YARD_LINE = 25f;

        /// <summary>
        /// A drive may not start inside an end zone, so every computed line of
        /// scrimmage is clamped this many yards clear of both goal lines.
        /// </summary>
        public const float MIN_YARDS_FROM_GOAL_LINE = 1f;
    }
}
