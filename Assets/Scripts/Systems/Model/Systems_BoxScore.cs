namespace PoFootball.Models
{
    /// <summary>
    /// The statistical record of one game: two team lines, pre-allocated at
    /// construction.
    ///
    /// TEAM TOTALS ONLY. This also carried twenty-two individual player lines —
    /// carries, receptions, tackles, a leading-rusher scan — feeding a scrolling
    /// per-player table in the HUD. The players in this simulation are unnamed
    /// shapes filling formation slots, so a rushing line attributed to "slot 14"
    /// was a number without a subject, and the attribution logic that produced it
    /// was the most intricate part of the stats layer. Team totals are what the
    /// scoreboard can actually stand behind.
    ///
    /// Nothing here allocates after the constructor. Stats are written from
    /// Systems_StatsSystem on the whistle, which is once per play rather than once
    /// per tick, but the no-allocation rule is cheap to honour here and keeps the
    /// class safe to use from a headless training env where a GC spike costs real
    /// wall-clock across six environments.
    /// </summary>
    public sealed class Systems_BoxScore
    {
        private readonly Systems_TeamStatLine[] _teams = new Systems_TeamStatLine[2];

        public Systems_BoxScore()
        {
            for (int teamIndex = 0; teamIndex < _teams.Length; teamIndex++)
            {
                _teams[teamIndex] = new Systems_TeamStatLine();
                _teams[teamIndex].Initialize((Systems_TeamId)teamIndex);
            }
        }

        public Systems_TeamStatLine Team(Systems_TeamId team)
        {
            return _teams[(int)team];
        }

        public void Reset()
        {
            for (int teamIndex = 0; teamIndex < _teams.Length; teamIndex++)
            {
                _teams[teamIndex].Reset();
            }
        }
    }
}
