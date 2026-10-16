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
    ///
    /// REVISION 14 MOVED THE KICKING GAME OUT OF HERE. The punt's flat forty yards,
    /// the field goal's 55-yard cliff, the kickoff's touchback chance and return
    /// spread, the onside recovery chance and the two-point success rate were all
    /// rules-layer stand-ins for plays nobody played. The plays are played now, and
    /// what decides them — distances, hang times, scatter — is execution, so it
    /// lives in Systems_SimConstants with everything else a brain is fitted against.
    /// What is left here is where the ball is spotted afterwards.
    /// </summary>
    public static class Systems_GameRules
    {
        // --- Clock -----------------------------------------------------------
        public const int QUARTER_COUNT = 4;

        /// <summary>
        /// Seconds of overtime when regulation ends level. Scaled from the NFL's
        /// ten minutes by the same factor QUARTER_SECONDS scales a quarter by
        /// (300 / 900), which puts it at 200.
        ///
        /// SINCE REVISION 14 THIS IS THE NFL'S OVERTIME, NOT SUDDEN DEATH. Each team
        /// gets a possession — a touchdown on the first no longer ends it — unless
        /// the first possession ends in a score by the defense. After both have had
        /// the ball, the next score wins. A period that expires level is a tie.
        /// Systems_GameFlowSystem's overtime bookkeeping has the detail.
        /// </summary>
        public const float OVERTIME_SECONDS = 200f;

        /// <summary>
        /// Seconds per quarter. Five minutes rather than fifteen, which is what
        /// makes the final whistle reachable in a sitting.
        ///
        /// The arithmetic: nearly all of a play's cost is HUDDLE_SECONDS, and a play
        /// is only live for a few seconds on top of that. At the 12 s huddle two
        /// lines below — roughly 15 s of game clock per down — this is about twenty
        /// plays a quarter and eighty in a game.
        /// </summary>
        public const float QUARTER_SECONDS = 300f;

        /// <summary>
        /// Every clock threshold below is a real one scaled by this: a five-minute
        /// quarter is a third of a fifteen-minute one.
        /// </summary>
        public const float CLOCK_SCALE = QUARTER_SECONDS / 900f;

        /// <summary>
        /// Clock burned between snaps while the clock runs — the huddle. Without
        /// it a quarter would take hundreds of plays, because a play itself only
        /// consumes the handful of seconds it is physically live.
        ///
        /// CUT FROM 25 TO 12, AND THAT IS WHAT SETS THE LENGTH OF A GAME. A measured
        /// full game came in at 45 plays across 9 drives — an NFL game is about 130
        /// — because at 25 s a quarter can only physically contain eleven or twelve
        /// snaps. 12 s roughly doubles the game to the eighty or ninety snaps that
        /// let drives actually develop.
        /// </summary>
        public const float HUDDLE_SECONDS = 12f;

        /// <summary>
        /// What a hurry-up to the line costs when a team is about to spike it: the
        /// time to line up and snap without a huddle.
        /// </summary>
        public const float HURRY_UP_SECONDS = 4f;

        /// <summary>Seconds per physics tick. Mirrors the pinned fixed timestep.</summary>
        public const float SECONDS_PER_TICK = 0.02f;

        /// <summary>
        /// Physics ticks the ball stays dead between the whistle and the next snap
        /// — 140 ticks = 2.8 s at the pinned 50 Hz.
        ///
        /// Sized to outlast Systems_HudView.BANNER_SECONDS (2.2 s) with room either
        /// side, because the banner is the only place a viewer is ever told what
        /// just happened.
        /// </summary>
        public const int DEAD_BALL_TICKS = 140;

        /// <summary>
        /// The two-minute warning — an automatic stoppage at 2:00 of the second and
        /// fourth quarters, scaled. Revision 14.
        /// </summary>
        public const float TWO_MINUTE_WARNING_SECONDS = 120f * CLOCK_SCALE;

        /// <summary>
        /// A carrier who goes out of bounds stops the clock only inside the last two
        /// minutes of the first half and the last five of the second; otherwise it
        /// restarts when the ball is spotted, which here means the huddle is
        /// charged as for any other play. Through revision 13 it always stopped,
        /// which handed a team running out the clock a free timeout every time it
        /// went to the sideline.
        /// </summary>
        public const float OUT_OF_BOUNDS_STOPS_CLOCK_FIRST_HALF_SECONDS = 120f * CLOCK_SCALE;

        /// <summary>The second-half counterpart, and overtime's.</summary>
        public const float OUT_OF_BOUNDS_STOPS_CLOCK_SECOND_HALF_SECONDS = 300f * CLOCK_SCALE;

        /// <summary>Physics ticks from the snap to a kneel-down being over.</summary>
        public const int KNEEL_TICKS = 25;

        /// <summary>Physics ticks from the snap to the spike hitting the turf.</summary>
        public const int SPIKE_TICKS = 12;

        // --- Timeouts --------------------------------------------------------
        /// <summary>Timeouts each team has per half, as in the real game.</summary>
        public const int TIMEOUTS_PER_HALF = 3;

        /// <summary>Timeouts each team carries into overtime.</summary>
        public const int TIMEOUTS_IN_OVERTIME = 2;

        /// <summary>
        /// Seconds left in a half under which a team that needs the clock starts
        /// spending timeouts. A timeout is worth exactly HUDDLE_SECONDS here — it
        /// is the huddle that does not get charged — so three of them inside this
        /// window buy back most of it.
        /// </summary>
        public const float TIMEOUT_WINDOW_SECONDS = 60f;

        // --- Downs -----------------------------------------------------------
        public const int DOWNS_PER_SERIES = 4;

        public const float YARDS_TO_GAIN = 10f;

        // --- Scoring ---------------------------------------------------------
        public const int TOUCHDOWN_POINTS = 6;

        /// <summary>A kicked try. Played since revision 14, from EXTRA_POINT_YARD_LINE.</summary>
        public const int EXTRA_POINT_POINTS = 1;

        public const int SAFETY_POINTS = 2;

        /// <summary>
        /// A try from the two that reaches the end zone. Played since revision 14;
        /// WHICH try a team takes is still the score's and the clock's decision.
        /// </summary>
        public const int TWO_POINT_POINTS = 2;

        /// <summary>
        /// What the defense scores by returning a try the whole way — an interception,
        /// a fumble, a blocked kick taken back to the other end.
        /// </summary>
        public const int DEFENSIVE_TRY_POINTS = 2;

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
        /// Longest attempt the quarterback may call. It used to be the longest that
        /// went in — a cliff — and since revision 14 the kick itself decides that
        /// (Systems_SimConstants.KICKER_CARRY_YARDS); this only keeps a quarterback
        /// from calling a sixty-five-yarder.
        /// </summary>
        public const float FIELD_GOAL_MAX_YARDS = 55f;

        /// <summary>The opponent's yard line a kicked try is snapped from: a 33-yard kick.</summary>
        public const float EXTRA_POINT_YARD_LINE = 15f;

        /// <summary>The opponent's yard line a two-point try is snapped from.</summary>
        public const float TWO_POINT_YARD_LINE = 2f;

        // --- Field position after a dead ball --------------------------------
        /// <summary>
        /// Where a touchback that is not a kickoff's is spotted: the team's own 20.
        /// A punt into the end zone, an interception downed in it, a fumble out of
        /// the back. Deliberately NOT the same constant as a kickoff touchback.
        /// </summary>
        public const float TOUCHBACK_YARD_LINE = 20f;

        /// <summary>
        /// Where a kickoff touchback is spotted: the receiving team's own 35, per
        /// the 2025 dynamic-kickoff rule.
        /// </summary>
        public const float KICKOFF_TOUCHBACK_YARD_LINE = 35f;

        /// <summary>The kicking team's own yard line a kickoff is taken from.</summary>
        public const float KICKOFF_YARD_LINE = 35f;

        /// <summary>
        /// The conceding team's own yard line its free kick after a safety is taken
        /// from. Revision 14 plays it; before, the scoring side was simply handed the
        /// ball on its own 40, which is where the return usually ends.
        /// </summary>
        public const float SAFETY_FREE_KICK_YARD_LINE = 20f;

        /// <summary>A kickoff out of bounds untouched: the receiving team's own 40.</summary>
        public const float KICKOFF_OUT_OF_BOUNDS_YARD_LINE = 40f;

        /// <summary>
        /// A missed field goal is spotted at the spot of the kick or here, whichever
        /// is further from the kicking team's goal — the defense never takes over
        /// inside its own 20 off a miss.
        /// </summary>
        public const float MISSED_FIELD_GOAL_MIN_YARD_LINE = 20f;

        /// <summary>
        /// A team trailing by more than this with the clock nearly gone will try an
        /// onside kick. Eight points is one score, so a team inside that still has a
        /// conventional path and should kick deep.
        /// </summary>
        public const int ONSIDE_TRAILING_BY = 8;

        /// <summary>
        /// Seconds left in the fourth quarter under which an onside kick becomes the
        /// right call.
        /// </summary>
        public const float ONSIDE_SECONDS_REMAINING = 60f;

        /// <summary>
        /// A drive may not start inside an end zone, so every computed line of
        /// scrimmage is clamped this many yards clear of both goal lines.
        /// </summary>
        public const float MIN_YARDS_FROM_GOAL_LINE = 1f;

        // --- Penalty enforcement -----------------------------------------------
        /// <summary>
        /// Pass interference in the end zone: the ball at the one, first and goal.
        /// </summary>
        public const float END_ZONE_INTERFERENCE_YARD_LINE = 1f;
    }
}
