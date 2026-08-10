namespace PoFootball.Systems
{
    /// <summary>Published once per episode, the moment the ball is snapped.</summary>
    public readonly struct Systems_PlaySnappedMessage
    {
        public readonly float LineOfScrimmageY;
        public readonly int EpisodeIndex;

        public Systems_PlaySnappedMessage(float lineOfScrimmageY, int episodeIndex)
        {
            LineOfScrimmageY = lineOfScrimmageY;
            EpisodeIndex = episodeIndex;
        }
    }
}
