using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Published once, when regulation expires. The save layer persists on this,
    /// the HUD hands over to the final-score screen, and the audio layer plays the
    /// closing whistle.
    /// </summary>
    public readonly struct Systems_GameOverMessage
    {
        public readonly int HomeScore;
        public readonly int AwayScore;

        public Systems_GameOverMessage(int homeScore, int awayScore)
        {
            HomeScore = homeScore;
            AwayScore = awayScore;
        }

        public bool IsTie => HomeScore == AwayScore;

        public Systems_TeamId Winner =>
            HomeScore >= AwayScore ? Systems_TeamId.Home : Systems_TeamId.Away;
    }
}
