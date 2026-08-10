namespace PoFootball.Systems
{
    /// <summary>
    /// Seed for line-of-scrimmage randomisation, set once from the composition root
    /// before the container is built.
    ///
    /// Held statically because Unity.Mathematics.Random must be seeded at
    /// construction and the director is built by VContainer, which has no place to
    /// thread a scene-authored value through. Written exactly once per process, at
    /// scene load, and read once — it is configuration, not shared mutable state.
    /// </summary>
    public static class Systems_EpisodeSeed
    {
        /// <summary>Unity.Mathematics.Random rejects a zero seed.</summary>
        public static uint Value { get; private set; } = 1u;

        public static void Set(uint seed)
        {
            Value = seed == 0u ? 1u : seed;
        }
    }
}
