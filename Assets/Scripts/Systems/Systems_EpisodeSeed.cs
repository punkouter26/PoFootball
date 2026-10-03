using UnityEngine;

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
    ///
    /// IT ALSO CARRIES A VIEWER'S REQUEST ACROSS THE SCENE LOAD. A game with a
    /// pinned seed replays exactly (CLAUDE.md, "Judge balance on fixed seeds"), so
    /// "watch that game again" and "watch the game someone else watched" are both
    /// just a seed. The menu and the final overlay are in SCN_MENU and a dying
    /// SCN_GAME; the scope that consumes the seed is in the SCN_GAME being loaded,
    /// and nothing but a static outlives the load between them — the same reason
    /// Systems_SceneRouter carries the game summary. Taken, not read, so a request
    /// can never leak into a later game or into an Editor measurement run.
    /// </summary>
    public static class Systems_EpisodeSeed
    {
        private static uint _requestedGameSeed;

        /// <summary>Unity.Mathematics.Random rejects a zero seed.</summary>
        public static uint Value { get; private set; } = 1u;

        public static void Set(uint seed)
        {
            Value = seed == 0u ? 1u : seed;
        }

        /// <summary>
        /// Asks the next played game to use this seed. Zero withdraws the request,
        /// which leaves the scope to draw its own.
        /// </summary>
        public static void RequestGameSeed(uint seed)
        {
            _requestedGameSeed = seed;
        }

        /// <summary>The requested seed, or zero when there is none. Clears it.</summary>
        public static uint TakeRequestedGameSeed()
        {
            uint seed = _requestedGameSeed;
            _requestedGameSeed = 0u;
            return seed;
        }

        /// <summary>
        /// Cleared on a domain reload, so a request left by an interrupted play
        /// session cannot pin the first game of the next one.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            _requestedGameSeed = 0u;
        }
    }
}
