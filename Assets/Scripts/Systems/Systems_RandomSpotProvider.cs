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

        public float NextLineOfScrimmageY()
        {
            return _rng.NextFloat(Systems_FieldModel.LOS_MIN_Y, Systems_FieldModel.LOS_MAX_Y);
        }
    }
}
