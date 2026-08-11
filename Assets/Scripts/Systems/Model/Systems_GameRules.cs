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
        /// Seconds per quarter. Five minutes rather than fifteen: at roughly
        /// twelve seconds of game clock per play this yields ~25 plays a quarter,
        /// which is a watchable game in a few minutes rather than an hour.
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
