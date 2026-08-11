namespace PoFootball.Models
{
    /// <summary>
    /// A finished game, frozen into numbers, so the menu can show what just
    /// happened after the game scene is gone.
    ///
    /// WHY A SNAPSHOT AND NOT THE BOX SCORE. Systems_BoxScore is owned by the game
    /// scene's container and dies with it — `LoadSceneMode.Single` destroys the
    /// scope, the systems and the models together. Handing the menu a live
    /// reference would hand it an object whose owner no longer exists. Copying the
    /// dozen numbers that matter into an immutable value costs nothing once per
    /// game and cannot be mutated by anything downstream.
    ///
    /// It carries only what the final screen shows. Adding a field here because it
    /// "might be useful on the menu one day" is the speculative API the C# rules
    /// forbid — the box score keeps the full record, and this is the view of it.
    /// </summary>
    public sealed class Systems_GameSummary
    {
        public Systems_TeamSummary Home { get; }

        public Systems_TeamSummary Away { get; }

        public Systems_GameSummary(Systems_TeamSummary home, Systems_TeamSummary away)
        {
            Home = home;
            Away = away;
        }

        public bool IsTie => Home.Points == Away.Points;

        public Systems_TeamId Winner =>
            Home.Points >= Away.Points ? Systems_TeamId.Home : Systems_TeamId.Away;

        /// <summary>Copies the live box score into a value the menu can outlive it with.</summary>
        public static Systems_GameSummary From(Systems_BoxScore boxScore)
        {
            return new Systems_GameSummary(
                Systems_TeamSummary.From(boxScore.Team(Systems_TeamId.Home)),
                Systems_TeamSummary.From(boxScore.Team(Systems_TeamId.Away)));
        }
    }

    /// <summary>
    /// One team's final line. A readonly struct because it is a bag of numbers with
    /// no identity, copied once and never changed.
    /// </summary>
    public readonly struct Systems_TeamSummary
    {
        public readonly Systems_TeamId Team;
        public readonly int Points;
        public readonly int FirstDowns;
        public readonly int Turnovers;
        public readonly int Touchdowns;
        public readonly int PlaysRun;
        public readonly float TotalYards;
        public readonly float RushingYards;
        public readonly float PassingYards;
        public readonly int Carries;
        public readonly int PassAttempts;
        public readonly int Completions;
        public readonly float TimeOfPossession;
        public readonly float YardsPerPlay;
        public readonly float CompletionPercentage;

        public Systems_TeamSummary(
            Systems_TeamId team,
            int points,
            int firstDowns,
            int turnovers,
            int touchdowns,
            int playsRun,
            float totalYards,
            float rushingYards,
            float passingYards,
            int carries,
            int passAttempts,
            int completions,
            float timeOfPossession,
            float yardsPerPlay,
            float completionPercentage)
        {
            Team = team;
            Points = points;
            FirstDowns = firstDowns;
            Turnovers = turnovers;
            Touchdowns = touchdowns;
            PlaysRun = playsRun;
            TotalYards = totalYards;
            RushingYards = rushingYards;
            PassingYards = passingYards;
            Carries = carries;
            PassAttempts = passAttempts;
            Completions = completions;
            TimeOfPossession = timeOfPossession;
            YardsPerPlay = yardsPerPlay;
            CompletionPercentage = completionPercentage;
        }

        public static Systems_TeamSummary From(Systems_TeamStatLine line)
        {
            return new Systems_TeamSummary(
                line.Team,
                line.Points,
                line.FirstDowns,
                line.Turnovers,
                line.Touchdowns,
                line.PlaysRun,
                line.TotalYards,
                line.RushingYards,
                line.PassingYards,
                line.Carries,
                line.PassAttempts,
                line.Completions,
                line.TimeOfPossession,
                line.YardsPerPlay,
                line.CompletionPercentage);
        }
    }
}
