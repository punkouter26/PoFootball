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
        /// Seconds of overtime when regulation ends level. Scaled from the NFL's
        /// ten minutes by the same factor QUARTER_SECONDS scales a quarter by
        /// (300 / 900), which puts it at 200.
        ///
        /// Overtime here is SUDDEN DEATH — the first score of any kind wins. The
        /// real rule is more elaborate (both teams get a possession unless the first
        /// is a touchdown) and modelling it would need a notion of "possession
        /// owed" that nothing else in this game has. Sudden death is the honest
        /// simplification: it ends the game, it rewards scoring, and it removes the
        /// tie — which is what was actually wrong. A game measured 28-28 and simply
        /// stopped.
        /// </summary>
        public const float OVERTIME_SECONDS = 200f;

        /// <summary>
        /// Seconds per quarter. Five minutes rather than fifteen, which is what
        /// makes the final whistle reachable in a sitting.
        ///
        /// The arithmetic: nearly all of a play's cost is HUDDLE_SECONDS, and a play
        /// is only live for a few seconds on top of that. At the 12 s huddle two
        /// lines below — roughly 15 s of game clock per down — this is about twenty
        /// plays a quarter and eighty in a game. It was forty-odd at the 25 s huddle
        /// this used to carry, which was too few for a drive to develop.
        /// </summary>
        public const float QUARTER_SECONDS = 300f;

        /// <summary>
        /// Clock burned between snaps while the clock runs — the huddle. Without
        /// it a quarter would take hundreds of plays, because a play itself only
        /// consumes the handful of seconds it is physically live.
        ///
        /// CUT FROM 25 TO 12, AND THAT IS WHAT SETS THE LENGTH OF A GAME. A measured
        /// full game came in at 45 plays across 9 drives — an NFL game is about 130
        /// — because at 25 s a quarter can only physically contain eleven or twelve
        /// snaps. Every drive was three to five plays, which is why so few of them
        /// ever reached a fourth down, and a viewer barely saw the playbook.
        ///
        /// 12 s is a no-huddle pace rather than a leisurely one, and it roughly
        /// doubles the game to the eighty or ninety snaps that let drives actually
        /// develop. It costs nothing in real time — the wall-clock length of a game
        /// is DEAD_BALL_TICKS plus live play, not this — so the game gets longer in
        /// football and stays the same length in minutes.
        /// </summary>
        public const float HUDDLE_SECONDS = 12f;

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
        /// Share of kickoffs that are simply taken as a touchback. High, because
        /// under the 2025 dynamic-kickoff rule that is what most of them are.
        /// </summary>
        public const float KICKOFF_TOUCHBACK_CHANCE = 0.7f;

        /// <summary>Spread of a returned kickoff around the touchback spot, yards.</summary>
        public const float KICKOFF_RETURN_SPREAD_YARDS = 9f;

        /// <summary>
        /// Chance the kicking team recovers an onside kick. Real rates are about one
        /// in five once the receiving team knows it is coming, and it always does
        /// here — this is only ever called when trailing late.
        /// </summary>
        public const float ONSIDE_RECOVERY_CHANCE = 0.20f;

        /// <summary>
        /// Where the receiving team takes over after an onside kick that FAILS: the
        /// kicking team's own 45, because a short kick hands over excellent field
        /// position. That cost is the entire reason an onside kick is a desperation
        /// call rather than a free roll.
        /// </summary>
        public const float ONSIDE_FAILED_YARD_LINE = 45f;

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
