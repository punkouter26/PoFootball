using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// State of the match: score, clock, possession, down and distance. Exactly one
    /// instance exists per environment. <see cref="Systems_GameFlowSystem"/> is the
    /// only writer; the HUD and the stats layer read.
    ///
    /// Plain fields with private setters rather than ReactiveProperty, matching
    /// <see cref="Systems_PlayModel"/>. The reasoning there was per-tick read
    /// volume; here it is simply consistency — transitions are broadcast through
    /// MessagePipe (Systems_GameStateChangedMessage), which fires a handful of
    /// times per drive, and the HUD polls the clock, which changes every frame and
    /// would be pointless to wrap in a subscription.
    ///
    /// Every Y in this class is in the ATTACKING FRAME: +Y is the direction the
    /// team currently in possession is driving, whichever jersey that is. The
    /// mirroring that keeps this true across a turnover is
    /// Systems_GameFlowSystem's job, and it is what lets a policy trained on
    /// "offense attacks +Y" play both sides of a real game.
    /// </summary>
    public sealed class Systems_GameModel
    {
        public Systems_GamePhase Phase { get; private set; } = Systems_GamePhase.PreGame;

        public int HomeScore { get; private set; }

        public int AwayScore { get; private set; }

        /// <summary>1-based. Reads 4 during the fourth quarter, never 5.</summary>
        public int Quarter { get; private set; } = 1;

        public float SecondsRemaining { get; private set; } = Systems_GameRules.QUARTER_SECONDS;

        public Systems_TeamId Possession { get; private set; } = Systems_TeamId.Home;

        /// <summary>Which team received to open the game. The other receives to open the half.</summary>
        public Systems_TeamId OpeningPossession { get; private set; } = Systems_TeamId.Home;

        public int Down { get; private set; } = 1;

        public float YardsToGo { get; private set; } = Systems_GameRules.YARDS_TO_GAIN;

        /// <summary>Line of scrimmage, attacking frame, metres.</summary>
        public float LineOfScrimmageY { get; private set; }

        /// <summary>Y the offense must reach for a new set of downs, attacking frame, metres.</summary>
        public float FirstDownMarkerY { get; private set; }

        /// <summary>Plays snapped since kickoff. Diagnostic and used to order the drive chart.</summary>
        public int PlaysRun { get; private set; }

        /// <summary>Drives started since kickoff.</summary>
        public int DriveIndex { get; private set; }

        /// <summary>
        /// False between plays that stopped the clock — an incompletion, a play out
        /// of bounds, a score, a change of possession, or the end of a quarter.
        /// </summary>
        public bool IsClockRunning { get; private set; } = true;

        /// <summary>Yards from the line of scrimmage to the goal line being attacked.</summary>
        public float YardsToGoal =>
            (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - LineOfScrimmageY) / Systems_FieldModel.YARD;

        /// <summary>True when the goal line is nearer than the chains — "1st and Goal".</summary>
        public bool IsGoalToGo => YardsToGoal <= YardsToGo;

        public int ScoreOf(Systems_TeamId team)
        {
            return team == Systems_TeamId.Home ? HomeScore : AwayScore;
        }

        // --- Mutators. Systems_GameFlowSystem only. --------------------------

        public void KickOff(Systems_TeamId receivingTeam, float lineOfScrimmageY)
        {
            Phase = Systems_GamePhase.Playing;
            HomeScore = 0;
            AwayScore = 0;
            Quarter = 1;
            SecondsRemaining = Systems_GameRules.QUARTER_SECONDS;
            Possession = receivingTeam;
            OpeningPossession = receivingTeam;
            PlaysRun = 0;
            DriveIndex = 0;
            IsClockRunning = true;
            StartSeries(lineOfScrimmageY);
        }

        /// <summary>Fresh set of downs at a spot. Used by first downs and by every change of possession.</summary>
        public void StartSeries(float lineOfScrimmageY)
        {
            LineOfScrimmageY = lineOfScrimmageY;
            Down = 1;
            YardsToGo = Systems_GameRules.YARDS_TO_GAIN;
            RecomputeMarker();
        }

        /// <summary>Same series, next down, from a new spot.</summary>
        public void AdvanceDown(float lineOfScrimmageY, float yardsToGo)
        {
            LineOfScrimmageY = lineOfScrimmageY;
            Down += 1;
            YardsToGo = yardsToGo;
            RecomputeMarker();
        }

        public void GiveBallTo(Systems_TeamId team, float lineOfScrimmageY)
        {
            Possession = team;
            DriveIndex += 1;
            StartSeries(lineOfScrimmageY);
        }

        public void AddPoints(Systems_TeamId team, int points)
        {
            if (team == Systems_TeamId.Home)
            {
                HomeScore += points;
            }
            else
            {
                AwayScore += points;
            }
        }

        public void CountPlay()
        {
            PlaysRun += 1;
        }

        public void SetClockRunning(bool isRunning)
        {
            IsClockRunning = isRunning;
        }

        /// <summary>
        /// Burns clock. Returns true when this drained the quarter, which the flow
        /// system turns into a quarter change — the model never advances its own
        /// quarter, because what happens at 0:00 depends on which quarter it was.
        /// </summary>
        public bool BurnClock(float seconds)
        {
            if (SecondsRemaining <= 0f)
            {
                return false;
            }

            SecondsRemaining -= seconds;

            if (SecondsRemaining <= 0f)
            {
                SecondsRemaining = 0f;
                return true;
            }

            return false;
        }

        public void BeginQuarter(int quarter)
        {
            Quarter = Mathf.Clamp(quarter, 1, Systems_GameRules.QUARTER_COUNT);
            SecondsRemaining = Systems_GameRules.QUARTER_SECONDS;
        }

        /// <summary>
        /// Regulation ended level. Moves to sudden death with a fresh clock; the
        /// quarter number stays at QUARTER_COUNT so nothing reads "5th".
        /// </summary>
        public void BeginOvertime()
        {
            Phase = Systems_GamePhase.Overtime;
            SecondsRemaining = Systems_GameRules.OVERTIME_SECONDS;
        }

        public void SetPhase(Systems_GamePhase phase)
        {
            Phase = phase;
        }

        private void RecomputeMarker()
        {
            float marker = LineOfScrimmageY + (YardsToGo * Systems_FieldModel.YARD);
            FirstDownMarkerY = Mathf.Min(marker, Systems_FieldModel.ATTACKING_GOAL_LINE_Y);
        }
    }
}
