namespace PoFootball.Models
{
    /// <summary>
    /// One jersey's team totals for a game. Split by
    /// <see cref="Systems_TeamId"/> — every play is attributed to whichever team
    /// held possession when it was snapped.
    /// </summary>
    public sealed class Systems_TeamStatLine
    {
        public Systems_TeamId Team { get; private set; }

        public int Points { get; private set; }

        public int Drives { get; private set; }

        public int PlaysRun { get; private set; }

        public float TotalYards { get; private set; }

        public int FirstDowns { get; private set; }

        public int Touchdowns { get; private set; }

        public int Safeties { get; private set; }

        public int Turnovers { get; private set; }

        public int PassAttempts { get; private set; }

        public int Completions { get; private set; }

        public float PassingYards { get; private set; }

        public int Carries { get; private set; }

        public float RushingYards { get; private set; }

        /// <summary>Seconds of game clock burned while this team held the ball.</summary>
        public float TimeOfPossession { get; private set; }

        public float YardsPerPlay => PlaysRun > 0 ? TotalYards / PlaysRun : 0f;

        public float CompletionPercentage =>
            PassAttempts > 0 ? (100f * Completions) / PassAttempts : 0f;

        public void Initialize(Systems_TeamId team)
        {
            Team = team;
            Reset();
        }

        public void Reset()
        {
            Points = 0;
            Drives = 0;
            PlaysRun = 0;
            TotalYards = 0f;
            FirstDowns = 0;
            Touchdowns = 0;
            Safeties = 0;
            Turnovers = 0;
            PassAttempts = 0;
            Completions = 0;
            PassingYards = 0f;
            Carries = 0;
            RushingYards = 0f;
            TimeOfPossession = 0f;
        }

        public void SetPoints(int points)
        {
            Points = points;
        }

        public void AddDrive()
        {
            Drives += 1;
        }

        public void AddFirstDown()
        {
            FirstDowns += 1;
        }

        public void AddTouchdown()
        {
            Touchdowns += 1;
        }

        public void AddSafety()
        {
            Safeties += 1;
        }

        public void AddTurnover()
        {
            Turnovers += 1;
        }

        public void AddTimeOfPossession(float seconds)
        {
            TimeOfPossession += seconds;
        }

        /// <summary>
        /// Records one snap. <paramref name="isPass"/> distinguishes the rushing
        /// and passing splits; a sack-like loss on a called pass still counts as a
        /// pass attempt, which is what the play-call latch reports.
        /// </summary>
        public void AddPlay(float yards, bool isPass, bool completed)
        {
            PlaysRun += 1;
            TotalYards += yards;

            if (isPass)
            {
                PassAttempts += 1;

                if (completed)
                {
                    Completions += 1;
                    PassingYards += yards;
                }
            }
            else
            {
                Carries += 1;
                RushingYards += yards;
            }
        }
    }
}
