using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// How the viewer wants the next game set up. One setting so far: whether
    /// every game is a new one, or the same one each time.
    ///
    /// WHY A SEED IS A SETTING. Everything downstream of the seed is deterministic
    /// — fixed delta time, seeded spots, seeded kicks, and policies with no
    /// randomness of their own — so a seed IS a game, start to finish. Fixing it
    /// is what lets someone show a play to somebody else, check whether a change
    /// altered a result, or simply watch the comeback again.
    ///
    /// PlayerPrefs, static, loaded lazily and saved on every write, for exactly the
    /// reasons Systems_AudioSettings gives: it is two values, and a setting changed
    /// immediately before the app is killed must survive.
    ///
    /// The seed is stored as a string. PlayerPrefs has no unsigned type, and a
    /// seed above int.MaxValue — half of everything the clock-derived default
    /// produces — would not round-trip through SetInt.
    /// </summary>
    public static class Systems_GameSettings
    {
        private const string SEED_FIXED_KEY = "PoFootball.Game.SeedFixed";
        private const string SEED_KEY = "PoFootball.Game.Seed";

        private const uint DEFAULT_SEED = 1u;

        private static bool _loaded;
        private static bool _seedFixed;
        private static uint _seed;

        /// <summary>True when every game should be played on <see cref="Seed"/>.</summary>
        public static bool SeedFixed
        {
            get
            {
                Load();
                return _seedFixed;
            }

            set
            {
                Load();
                _seedFixed = value;
                PlayerPrefs.SetInt(SEED_FIXED_KEY, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }

        /// <summary>The seed a fixed game is played on. Never zero.</summary>
        public static uint Seed
        {
            get
            {
                Load();
                return _seed;
            }

            set
            {
                Load();
                _seed = value == 0u ? DEFAULT_SEED : value;
                PlayerPrefs.SetString(SEED_KEY, _seed.ToString());
                PlayerPrefs.Save();
            }
        }

        /// <summary>What to ask the next game for: the fixed seed, or zero for "draw one".</summary>
        public static uint SeedForNextGame => SeedFixed ? Seed : 0u;

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _seedFixed = PlayerPrefs.GetInt(SEED_FIXED_KEY, 0) != 0;

            if (!uint.TryParse(PlayerPrefs.GetString(SEED_KEY, string.Empty), out _seed)
                || _seed == 0u)
            {
                _seed = DEFAULT_SEED;
            }
        }

        /// <summary>
        /// Cleared on a domain reload, so prefs edited or wiped between play
        /// sessions are read again rather than served from the last session's cache.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            _loaded = false;
        }
    }
}
