using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// The training line-of-scrimmage draw, lifted verbatim out of
    /// <see cref="Systems_EpisodeDirector"/>.
    ///
    /// Every detail here is load-bearing for reproducibility: the same seed source
    /// (<see cref="Systems_EpisodeSeed"/>), the same generator
    /// (Unity.Mathematics.Random), the same single NextFloat call per episode
    /// against the same bounds. A run started before the game layer existed and a
    /// run started after it draw the identical sequence of scrimmage lines.
    ///
    /// If this ever needs to change, that is a new curriculum and it belongs in a
    /// new config with a new run-id — not an edit here.
    /// </summary>
    public sealed class Systems_RandomSpotProvider : Systems_ISpotProvider
    {
        private Random _rng;

        public Systems_RandomSpotProvider()
        {
            _rng = new Random(Systems_EpisodeSeed.Value);
        }

        /// <summary>
        /// Training never ends. There is no clock, no score and no fourth quarter —
        /// mlagents-learn decides when to stop by its own step budget, and the
        /// environment must keep producing episodes until it does.
        /// </summary>
        public bool HasNextPlay => true;

        /// <summary>
        /// Zero, and it must stay zero. A dead-ball pause is wall-clock a trainer
        /// spends producing no experience, multiplied by 4-8 environments and by
        /// time_scale 20. It also changes nothing a policy observes, so paying for
        /// it here would be pure loss.
        ///
        /// At zero the director ends the episode and begins the next one in the same
        /// FixedTick, exactly as it did before the pause existed — so a run started
        /// either side of this change draws the identical sequence of episodes.
        /// </summary>
        public int DeadBallTicks => 0;

        /// <summary>
        /// A seeded spot, and now a seeded SITUATION.
        ///
        /// Training has no chains — Systems_GameFlowSystem is a Game-mode
        /// registration — so before this the down did not exist here at all and a
        /// quarterback could not have learned when to punt if it wanted to. Drawing
        /// down and distance alongside the spot puts the decision in front of the
        /// policy on roughly a quarter of reps without turning training into a full
        /// game simulation: every play is still an independent, seeded rep.
        ///
        /// Distance is drawn from one to fifteen rather than always ten, so 4th and
        /// 1 and 4th and 14 are both in the distribution. A policy that only ever saw
        /// ten would learn one threshold and apply it everywhere.
        /// </summary>
        public Systems_PlaySituation NextSituation()
        {
            float lineOfScrimmageY =
                _rng.NextFloat(Systems_FieldModel.LOS_MIN_Y, Systems_FieldModel.LOS_MAX_Y);

            int down = _rng.NextInt(1, Systems_GameRules.DOWNS_PER_SERIES + 1);
            float yardsToGo = _rng.NextFloat(1f, 15f);

            return new Systems_PlaySituation(lineOfScrimmageY, down, yardsToGo);
        }
    }
}
