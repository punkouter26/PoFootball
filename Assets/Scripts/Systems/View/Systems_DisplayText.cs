using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// Turns game state into the short strings a scoreboard shows. Shared by the
    /// HUD and the box score so "3rd &amp; 7" is spelled one way in one place.
    ///
    /// Every method that can return a literal does. Only the ones that have to
    /// interpolate a number allocate, and their callers are expected to invoke them
    /// on change rather than every frame — see Systems_HudView, which rebuilds the
    /// clock only when the displayed second actually differs.
    /// </summary>
    public static class Systems_DisplayText
    {
        /// <summary>Seconds as M:SS, counting down. Rounds up, as a stadium clock does.</summary>
        public static string Clock(float secondsRemaining)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(secondsRemaining));
            int minutes = total / 60;
            int seconds = total % 60;

            return $"{minutes}:{seconds:00}";
        }

        /// <summary>Whole seconds currently displayed, for change detection.</summary>
        public static int ClockKey(float secondsRemaining)
        {
            return Mathf.Max(0, Mathf.CeilToInt(secondsRemaining));
        }

        public static string Ordinal(int value)
        {
            switch (value)
            {
                case 1: return "1st";
                case 2: return "2nd";
                case 3: return "3rd";
                case 4: return "4th";
                default: return value.ToString();
            }
        }

        public static string QuarterLabel(int quarter)
        {
            switch (quarter)
            {
                case 1: return "1ST";
                case 2: return "2ND";
                case 3: return "3RD";
                case 4: return "4TH";
                default: return "OT";
            }
        }

        /// <summary>
        /// The situation chip for the next snap. A kickoff, a try and the clock plays
        /// are named rather than given a down — "1st & 10" before a kickoff is a
        /// number nobody is playing for. Revision 14.
        /// </summary>
        public static string Situation(
            Systems_PlayKind kind, int down, float yardsToGo, bool isGoalToGo)
        {
            switch (kind)
            {
                case Systems_PlayKind.Kickoff: return "KICKOFF";
                case Systems_PlayKind.ExtraPoint: return "PAT";
                case Systems_PlayKind.TwoPoint: return "2-PT TRY";
                case Systems_PlayKind.KneelDown: return "VICTORY";
                default: return DownAndDistance(down, yardsToGo, isGoalToGo);
            }
        }

        public static string DownAndDistance(int down, float yardsToGo, bool isGoalToGo)
        {
            if (isGoalToGo)
            {
                return $"{Ordinal(down)} & Goal";
            }

            return $"{Ordinal(down)} & {Mathf.Max(1, Mathf.RoundToInt(yardsToGo))}";
        }

        /// <summary>
        /// A line of scrimmage as a broadcast would caption it: own half, opponent
        /// half, or the 50. Input is in the attacking frame, so Y = 0 is midfield
        /// and positive Y is the opponent's half by definition.
        /// </summary>
        public static string FieldPosition(float lineOfScrimmageY)
        {
            float yardsFromOwnGoal =
                (lineOfScrimmageY - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD;
            int yardLine = Mathf.RoundToInt(yardsFromOwnGoal);

            if (yardLine == 50)
            {
                return "50";
            }

            if (yardLine < 50)
            {
                return $"OWN {Mathf.Max(1, yardLine)}";
            }

            return $"OPP {Mathf.Max(1, 100 - yardLine)}";
        }

        /// <summary>Three-letter team tag for the scoreboard.</summary>
        public static string TeamTag(Systems_TeamId team)
        {
            return team == Systems_TeamId.Home ? "HOM" : "AWY";
        }

        public static string TeamName(Systems_TeamId team)
        {
            return team == Systems_TeamId.Home ? "HOME" : "AWAY";
        }

        public static string PlayCallLabel(Systems_PlayCall call)
        {
            switch (call)
            {
                case Systems_PlayCall.KeepQuarterback: return "QB KEEP";
                // The same two names the HUD chip uses (see PlayCall below). They
                // were "DIVE" and "HANDOFF" here, so one play was announced as
                // "FB DIVE" at the snap and "DIVE" at the whistle.
                case Systems_PlayCall.HandoffFullback: return "FB DIVE";
                case Systems_PlayCall.HandoffHalfback: return "HB RUN";
                case Systems_PlayCall.Pass: return "PASS";
                case Systems_PlayCall.PassDeep: return "DEEP PASS";
                case Systems_PlayCall.Punt: return "PUNT";
                case Systems_PlayCall.FieldGoal: return "FIELD GOAL";
                // No call was made: the whistle came before the dropback latched
                // one. This used to say "KICKOFF", which every false start read as.
                default: return string.Empty;
            }
        }

        /// <summary>
        /// What the play WAS, for the banner and a highlight caption. The kind of
        /// snap first: a try, a kneel and a spike run on a call the quarterback
        /// was handed (Systems_PlayModel forces one), and naming that call put
        /// "FIELD GOAL" under every extra point, "QB KEEP" under every kneel and
        /// "PASS" under every spike. A flag before the snap is named as such.
        /// </summary>
        public static string PlayLabel(
            Systems_PlayKind kind, Systems_PlayCall call, Systems_PlayOutcome outcome)
        {
            if (outcome == Systems_PlayOutcome.DeadBallFoul)
            {
                return "PRE-SNAP";
            }

            switch (kind)
            {
                case Systems_PlayKind.Kickoff: return "KICKOFF";
                case Systems_PlayKind.Punt: return "PUNT";
                case Systems_PlayKind.FieldGoal: return "FIELD GOAL";
                case Systems_PlayKind.ExtraPoint: return "EXTRA POINT";
                case Systems_PlayKind.TwoPoint: return "2-PT TRY";
                case Systems_PlayKind.KneelDown: return "KNEEL";
                case Systems_PlayKind.Spike: return "SPIKE";
                default: return PlayCallLabel(call);
            }
        }

        /// <summary>
        /// The banner's detail line before any flag or timeout is appended:
        /// "HB RUN   +4 yd", "PUNT   returned 12 yd", "INTERCEPTION ... returned 30 yd".
        /// A kick's yardage is the return, because a kick gains the kicking team
        /// nothing on the books and "no gain" under a sixty-yard runback was the
        /// banner contradicting the play. Allocates; once per resolved down.
        /// </summary>
        public static string PlayDetail(
            Systems_PlayKind kind,
            Systems_PlayCall call,
            Systems_PlayOutcome outcome,
            float yardsGained,
            float returnYards)
        {
            string label = PlayLabel(kind, call, outcome);
            string returned = ReturnDetail(returnYards);

            if (outcome == Systems_PlayOutcome.DeadBallFoul)
            {
                return label;
            }

            switch (kind)
            {
                case Systems_PlayKind.Kickoff:
                case Systems_PlayKind.Punt:
                case Systems_PlayKind.FieldGoal:
                case Systems_PlayKind.ExtraPoint:
                case Systems_PlayKind.Spike:
                    return returned.Length == 0 ? label : label + "   " + returned;
            }

            string yardage = YardageDetail(yardsGained);
            string head = label.Length == 0 ? yardage : label + "   " + yardage;

            return returned.Length == 0 ? head : head + "   " + returned;
        }

        /// <summary>"returned 23 yd", or nothing for a return of under a yard.</summary>
        public static string ReturnDetail(float returnYards)
        {
            int rounded = Mathf.RoundToInt(returnYards);
            return rounded <= 0 ? string.Empty : $"returned {rounded} yd";
        }

        /// <summary>
        /// The big banner after a play. Reads the game consequence first — a tackle
        /// that moved the chains is a first down, not a tackle — and only falls back
        /// to how the ball died when the down itself was unremarkable.
        /// </summary>
        public static string ResultBanner(Systems_DownResult result, Systems_PlayOutcome outcome)
        {
            switch (result)
            {
                case Systems_DownResult.Touchdown: return "TOUCHDOWN";
                case Systems_DownResult.Safety: return "SAFETY";
                case Systems_DownResult.Interception: return "INTERCEPTED";
                case Systems_DownResult.FumbleLost: return "FUMBLE";
                case Systems_DownResult.TurnoverOnDowns: return "TURNOVER ON DOWNS";
                case Systems_DownResult.FirstDown: return "FIRST DOWN";
                case Systems_DownResult.EndOfQuarter: return "END OF QUARTER";
                case Systems_DownResult.EndOfGame: return "FINAL";
                case Systems_DownResult.Kickoff: return "KICKOFF RETURN";
                case Systems_DownResult.KickRecovered: return "KICK RECOVERED";
                case Systems_DownResult.BlockedKick: return "BLOCKED";
                case Systems_DownResult.Touchback: return "TOUCHBACK";
                case Systems_DownResult.Penalty: return "FLAG";
                case Systems_DownResult.ExtraPointGood: return "EXTRA POINT GOOD";
                case Systems_DownResult.ExtraPointMissed: return "EXTRA POINT NO GOOD";
                case Systems_DownResult.TwoPointGood: return "TWO-POINT GOOD";
                case Systems_DownResult.TwoPointFailed: return "TWO-POINT NO GOOD";
                case Systems_DownResult.DefensiveTry: return "DEFENSIVE TWO";
                case Systems_DownResult.Sack: return "SACKED";
            }

            switch (outcome)
            {
                case Systems_PlayOutcome.Incompletion: return "INCOMPLETE";
                case Systems_PlayOutcome.OutOfBounds: return "OUT OF BOUNDS";
                case Systems_PlayOutcome.TimeExpired: return "PLAY OVER";
                case Systems_PlayOutcome.Safety: return "SAFETY";
                case Systems_PlayOutcome.Punt: return "PUNT";
                case Systems_PlayOutcome.FieldGoalGood: return "FIELD GOAL IS GOOD";
                case Systems_PlayOutcome.FieldGoalMissed: return "NO GOOD";
                case Systems_PlayOutcome.Sack: return "SACKED";
                case Systems_PlayOutcome.Downed: return "DOWN";
                // The rest only reach here as a period-ending play's caption; the
                // down result names each of them while the game is on.
                case Systems_PlayOutcome.Kickoff: return "KICKOFF RETURN";
                case Systems_PlayOutcome.KickRecovered: return "KICK RECOVERED";
                case Systems_PlayOutcome.KickBlocked: return "BLOCKED";
                case Systems_PlayOutcome.Touchback: return "TOUCHBACK";
                case Systems_PlayOutcome.DeadBallFoul: return "FLAG";
                default: return "TACKLED";
            }
        }

        /// <summary>
        /// The foul the way an official announces it. Literal for every branch, so
        /// naming a flag allocates nothing.
        /// </summary>
        public static string FoulName(Systems_FoulType foul)
        {
            switch (foul)
            {
                case Systems_FoulType.FalseStart: return "FALSE START";
                case Systems_FoulType.Offside: return "OFFSIDE";
                case Systems_FoulType.DelayOfGame: return "DELAY OF GAME";
                case Systems_FoulType.OffensiveHolding: return "HOLDING";
                case Systems_FoulType.DefensiveHolding: return "DEFENSIVE HOLDING";
                case Systems_FoulType.DefensivePassInterference: return "PASS INTERFERENCE";
                case Systems_FoulType.OffensivePassInterference: return "OFFENSIVE PASS INTERFERENCE";
                case Systems_FoulType.RoughingThePasser: return "ROUGHING THE PASSER";
                case Systems_FoulType.Facemask: return "FACEMASK";
                case Systems_FoulType.IllegalBlockInTheBack: return "BLOCK IN THE BACK";
                case Systems_FoulType.KickCatchInterference: return "KICK-CATCH INTERFERENCE";
                case Systems_FoulType.IntentionalGrounding: return "INTENTIONAL GROUNDING";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// "   FLAG HOLDING HOM 10 yd" or "   FLAG HOLDING HOM declined", appended to
        /// the banner's detail line, or nothing when there was no flag.
        /// </summary>
        public static string FoulDetail(
            Systems_FoulType foul, Systems_TeamId team, bool accepted, float yards)
        {
            if (foul == Systems_FoulType.None)
            {
                return string.Empty;
            }

            string tail = accepted
                ? (yards > 0.05f ? $"{Mathf.RoundToInt(yards)} yd" : "enforced")
                : "declined";

            return $"   FLAG {FoulName(foul)} {TeamTag(team)} {tail}";
        }

        /// <summary>"   TWO-MINUTE WARNING", appended to the banner's detail line, or nothing.</summary>
        public static string TwoMinuteWarningDetail(bool given)
        {
            return given ? "   TWO-MINUTE WARNING" : string.Empty;
        }

        /// <summary>
        /// The quarterback's committed call, for the HUD chip.
        ///
        /// Every branch returns a literal, so showing the call costs no allocation
        /// at all — which matters because it is written on the frame a play
        /// commits and that frame is the busiest one of the snap.
        ///
        /// Named the way a broadcast graphic would name them rather than the way
        /// the enum does: a viewer knows what a keeper and a dive are, and nobody
        /// outside this codebase has heard of a HandoffFullback.
        ///
        /// FG, NOT "FIELD GOAL", ON THE CHIP. It was the one call twice the width of
        /// the others, and the scoreboard reserved that width on both sides of the
        /// down-and-distance pill on every play of the game to fit a call made a
        /// few times a game. The banner after the kick still spells it out
        /// (PlayCallLabel), where there is room.
        /// </summary>
        public static string PlayCall(Systems_PlayKind kind, Systems_PlayCall call)
        {
            // The kinds whose call is forced, named for what they are. Literals.
            switch (kind)
            {
                case Systems_PlayKind.ExtraPoint: return call == Systems_PlayCall.None ? string.Empty : "PAT";
                case Systems_PlayKind.KneelDown: return call == Systems_PlayCall.None ? string.Empty : "KNEEL";
                case Systems_PlayKind.Spike: return call == Systems_PlayCall.None ? string.Empty : "SPIKE";
            }

            return PlayCall(call);
        }

        public static string PlayCall(Systems_PlayCall call)
        {
            switch (call)
            {
                case Systems_PlayCall.KeepQuarterback: return "QB KEEP";
                case Systems_PlayCall.HandoffFullback: return "FB DIVE";
                case Systems_PlayCall.HandoffHalfback: return "HB RUN";
                case Systems_PlayCall.Pass: return "PASS";
                case Systems_PlayCall.PassDeep: return "DEEP";
                case Systems_PlayCall.Punt: return "PUNT";
                case Systems_PlayCall.FieldGoal: return "FG";
                default: return string.Empty;
            }
        }

        /// <summary>The position abbreviation a broadcast uses, for the carrier chip.</summary>
        public static string RoleTag(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.Quarterback: return "QB";
                case Systems_PlayerRole.RunningBack: return "HB";
                case Systems_PlayerRole.Fullback: return "FB";
                case Systems_PlayerRole.WideReceiver: return "WR";
                case Systems_PlayerRole.TightEnd: return "TE";
                case Systems_PlayerRole.OffensiveLine: return "OL";
                case Systems_PlayerRole.DefensiveLine: return "DL";
                case Systems_PlayerRole.Linebacker: return "LB";
                case Systems_PlayerRole.Cornerback: return "CB";
                case Systems_PlayerRole.Safety: return "S";
                default: return string.Empty;
            }
        }

        /// <summary>
        /// "DRIVE  7 plays, 54 yd". Allocates; called once per resolved down.
        /// </summary>
        public static string DriveSummary(int plays, float yards)
        {
            string unit = plays == 1 ? "play" : "plays";
            return $"DRIVE  {plays} {unit}, {Mathf.RoundToInt(yards)} yd";
        }

        /// <summary>Signed yardage the way a caption reads it: "+7 yd", "-2 yd", "no gain".</summary>
        public static string YardageDetail(float yards)
        {
            int rounded = Mathf.RoundToInt(yards);

            if (rounded == 0)
            {
                return "no gain";
            }

            return rounded > 0 ? $"+{rounded} yd" : $"{rounded} yd";
        }

        /// <summary>"   TIMEOUT HOM", appended to the banner's detail line, or nothing.</summary>
        public static string TimeoutDetail(bool timeoutCalled, Systems_TeamId team)
        {
            return timeoutCalled ? "   TIMEOUT " + TeamTag(team) : string.Empty;
        }
    }
}
