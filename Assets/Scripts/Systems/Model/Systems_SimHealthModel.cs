namespace PoFootball.Models
{
    /// <summary>
    /// What the simulation knows about its own health, for the DEBUG sheet.
    ///
    /// WHY IT EXISTS. The sheet could say the frame was slow and could not say the
    /// two things that matter most about a build of this game: whether anything on
    /// the field is a trained policy, and whether what is being played is football.
    /// Both answers already existed — one in a console warning logged once at
    /// startup, the other in the REALISM line logged once at the final whistle —
    /// and neither is readable on a handset, mid-game, by someone holding it.
    ///
    /// Three writers, one reader. The agents report whether they got a brain and
    /// when the speed clamp fires; Systems_GameFlowSystem reports each resolved
    /// play; Systems_StatusHudView reads. Nothing in the simulation reads any of
    /// it back, so it cannot change a result.
    ///
    /// Plain fields rather than ReactiveProperty, for the reason Systems_PlayModel
    /// gives: the clamp counter is on the per-tick path, and the one reader polls
    /// four times a second while its sheet is open.
    /// </summary>
    public sealed class Systems_SimHealthModel
    {
        private const int PLAYERS_ON_FIELD = Systems_RoleTable.SQUAD_SIZE * 2;

        /// <summary>Agents that have configured their brain so far. 22 once the scene is up.</summary>
        public int PlayersReported { get; private set; }

        /// <summary>Of those, how many were handed a trained model rather than the heuristic.</summary>
        public int PlayersWithBrain { get; private set; }

        /// <summary>
        /// Why there is no brain, in the registry's own words, or empty when there
        /// is one. Carried as text because the reason is the useful part — "revision
        /// 10 against a build on 11" tells the reader what to do; a boolean does not.
        /// </summary>
        public string BrainStatus { get; private set; } = string.Empty;

        /// <summary>Times the pileup-explosion guard fired this session.</summary>
        public int SpeedClampHits { get; private set; }

        /// <summary>Physics ticks the ball has been live this session.</summary>
        public int LiveTicks { get; private set; }

        public int ScrimmagePlays { get; private set; }

        public float ScrimmageYards { get; private set; }

        public int FourthDowns { get; private set; }

        public int Drives { get; private set; }

        public int Touchdowns { get; private set; }

        public float YardsPerPlay => ScrimmagePlays > 0 ? ScrimmageYards / ScrimmagePlays : 0f;

        public float TouchdownsPerDrive => Drives > 0 ? Touchdowns / (float)Drives : 0f;

        /// <summary>
        /// Clamp hits per player per live tick — the same quantity as the trainer's
        /// Control/SpeedClampRate, which the promotion gate refuses above 0.001.
        /// </summary>
        public float SpeedClampRate =>
            LiveTicks > 0 ? SpeedClampHits / (float)(LiveTicks * PLAYERS_ON_FIELD) : 0f;

        public void ReportBrain(bool hasModel, string status)
        {
            PlayersReported++;

            if (hasModel)
            {
                PlayersWithBrain++;
            }

            BrainStatus = status ?? string.Empty;
        }

        public void CountSpeedClamp()
        {
            SpeedClampHits++;
        }

        public void CountLiveTick()
        {
            LiveTicks++;
        }

        /// <summary>
        /// One resolved play. <paramref name="isScrimmagePlay"/> is false for a
        /// kick, which is spotted at the line and would drag the average down
        /// without describing anything the offense did.
        /// </summary>
        public void RecordPlay(
            bool isScrimmagePlay, float yardsGained, bool facedFourthDown, bool touchdown, int drives)
        {
            if (isScrimmagePlay)
            {
                ScrimmagePlays++;
                ScrimmageYards += yardsGained;
            }

            if (facedFourthDown)
            {
                FourthDowns++;
            }

            if (touchdown)
            {
                Touchdowns++;
            }

            Drives = drives;
        }
    }
}
