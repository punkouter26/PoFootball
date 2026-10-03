namespace PoFootball.Systems
{
    /// <summary>
    /// Which stage of the training curriculum the spot draw is in.
    ///
    /// Owned here so Systems_RandomSpotProvider can be staged without PoFootball
    /// .Systems ever naming ML-Agents: the only implementation that reads the
    /// trainer's environment_parameters is Agent_Curriculum, which installs itself
    /// through <see cref="Systems_RandomSpotProvider.SetLessonSource"/> — the same
    /// shape as Agent_TeamGroups installing the team reward sink.
    /// </summary>
    public interface Systems_ISpotLessonSource
    {
        /// <summary>
        /// One of the Systems_RandomSpotProvider lesson constants. Anything out of
        /// range is treated as the full distribution.
        /// </summary>
        int CurrentLesson { get; }
    }
}
