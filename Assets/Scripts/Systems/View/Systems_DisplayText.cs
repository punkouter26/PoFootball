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
                case Systems_PlayCall.HandoffFullback: return "DIVE";
                case Systems_PlayCall.HandoffHalfback: return "HANDOFF";
                case Systems_PlayCall.Pass: return "PASS";
                default: return "NO CALL";
            }
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
                case Systems_DownResult.TurnoverOnDowns: return "TURNOVER ON DOWNS";
                case Systems_DownResult.FirstDown: return "FIRST DOWN";
                case Systems_DownResult.EndOfQuarter: return "END OF QUARTER";
                case Systems_DownResult.EndOfGame: return "FINAL";
            }

            switch (outcome)
            {
                case Systems_PlayOutcome.Incompletion: return "INCOMPLETE";
                case Systems_PlayOutcome.OutOfBounds: return "OUT OF BOUNDS";
                case Systems_PlayOutcome.TimeExpired: return "PLAY OVER";
                default: return "TACKLED";
            }
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
    }
}
