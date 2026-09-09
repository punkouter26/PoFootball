using UnityEngine;

namespace PoFootball.Views
{
    /// <summary>
    /// The mix, as three user-facing levels that survive a restart.
    ///
    /// WHAT THIS REPLACES. The entire mix used to be two serialized floats on
    /// Systems_AudioView, and the comment above them records what that cost: the
    /// levels were cut by 80% at the owner's request, and doing it required editing
    /// the file AND SCN_GAME's serialized copy of the same two fields, because a
    /// serialized value wins over a field initializer. That note ends "Changing
    /// only this file would have looked right in the diff and changed nothing you
    /// can hear" — which is a description of a setting that lives in the wrong
    /// place. A volume is something a player changes, not something a developer
    /// edits in two files and hopes they agree.
    ///
    /// WHY NOT AN AudioMixer. An AudioMixer with exposed parameters is the textbook
    /// answer and it is not reachable from here: AudioMixer assets can only be
    /// created through the Assets/Create menu, there is no public constructor and
    /// no AssetDatabase-friendly scripting API, so a mixer cannot be added by any
    /// tool that is not a human in the Editor. What a mixer would have bought is
    /// bus routing, and with three buses and a single duck envelope the routing fits
    /// in this file. If a mixer asset is ever created by hand, the levels here are
    /// what should drive its exposed parameters.
    ///
    /// PlayerPrefs rather than a save file, because three floats do not justify a
    /// serializer, and PlayerPrefs is the one storage every platform this ships to
    /// already has.
    /// </summary>
    public static class Systems_AudioSettings
    {
        private const string MASTER_KEY = "PoFootball.Audio.Master";
        private const string EFFECTS_KEY = "PoFootball.Audio.Effects";
        private const string CROWD_KEY = "PoFootball.Audio.Crowd";

        /// <summary>
        /// Defaults carried over verbatim from the two fields this replaces, so the
        /// game sounds on first launch exactly as it did before — the 80% cut the
        /// owner asked for is preserved rather than quietly reverted by a
        /// refactor.
        /// </summary>
        private const float DEFAULT_MASTER = 1f;

        private const float DEFAULT_EFFECTS = 0.18f;
        private const float DEFAULT_CROWD = 0.06f;

        private static bool _loaded;

        private static float _master;
        private static float _effects;
        private static float _crowd;

        /// <summary>Overall level. Everything below is scaled by it.</summary>
        public static float Master
        {
            get { Load(); return _master; }
            set { Load(); _master = Mathf.Clamp01(value); Save(MASTER_KEY, _master); }
        }

        /// <summary>One-shots: the whistle, the pad pop, the result cues.</summary>
        public static float Effects
        {
            get { Load(); return _effects; }
            set { Load(); _effects = Mathf.Clamp01(value); Save(EFFECTS_KEY, _effects); }
        }

        /// <summary>The looping stadium bed.</summary>
        public static float Crowd
        {
            get { Load(); return _crowd; }
            set { Load(); _crowd = Mathf.Clamp01(value); Save(CROWD_KEY, _crowd); }
        }

        /// <summary>Effects level after the master fader. What a voice is actually played at.</summary>
        public static float EffectsBus => Effects * Master;

        /// <summary>Crowd level after the master fader, before ducking.</summary>
        public static float CrowdBus => Crowd * Master;

        private static void Load()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;

            _master = PlayerPrefs.GetFloat(MASTER_KEY, DEFAULT_MASTER);
            _effects = PlayerPrefs.GetFloat(EFFECTS_KEY, DEFAULT_EFFECTS);
            _crowd = PlayerPrefs.GetFloat(CROWD_KEY, DEFAULT_CROWD);
        }

        private static void Save(string key, float value)
        {
            PlayerPrefs.SetFloat(key, value);

            // Written through immediately. A volume slider is the kind of setting a
            // player changes and then kills the app to test, and an unflushed
            // PlayerPrefs write is lost on a hard exit on mobile.
            PlayerPrefs.Save();
        }
    }
}
