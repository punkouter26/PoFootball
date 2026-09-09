using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Atmosphere for the front end: a distant crowd, so the menu sounds like a
    /// stadium an hour before kickoff rather than like a dialog box.
    ///
    /// Separate from Systems_AudioView rather than a mode of it. That view
    /// subscribes to four messages that do not exist in SCN_MENU; reusing it here
    /// would mean most of it being switched off, and the switched-off parts would
    /// still be built.
    ///
    /// FADES IN, NEVER CUTS. Starting a loop at full volume the instant a scene
    /// loads is the single most common way a menu ends up feeling cheap.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Systems_MenuAudioView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// The menu bed as a fraction of the in-game crowd level.
        ///
        /// A RATIO NOW, NOT AN ABSOLUTE. This used to be a serialized 0.024, cut by
        /// 80% on 2026-08-22 alongside the two gains in Systems_AudioView and
        /// mirrored by hand into SCN_MENU's serialized copy — three places holding
        /// one decision. Those gains have moved to Systems_AudioSettings, where a
        /// player owns them, so this only has to say what it always meant: the menu
        /// is the same stadium, heard from outside. Multiplying keeps that
        /// relationship true at any volume the player picks.
        /// </summary>
        private const float MENU_BED_RATIO = 0.4f;

        [Tooltip("Seconds for the bed to reach full level from silence.")]
        [Range(0.1f, 8f)]
        [SerializeField] private float _fadeInSeconds = 2.5f;

        private Systems_PresentationBudget _budget;

        private AudioSource _crowd;

        private float _fade;

        [Inject]
        public void Construct(Systems_PresentationBudget budget)
        {
            _budget = budget;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            _crowd = BuildLoop(Systems_ToneBank.Crowd());
        }

        private AudioSource BuildLoop(AudioClip clip)
        {
            AudioSource source = gameObject.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.playOnAwake = false;
            source.spatialBlend = 0f;
            source.volume = 0f;
            source.Play();
            return source;
        }

        private void Update()
        {
            if (_crowd == null)
            {
                return;
            }

            if (_fade < 1f)
            {
                // Unscaled: the menu should not care what the last scene left
                // Time.timeScale at.
                _fade = Mathf.Min(1f, _fade + Time.unscaledDeltaTime / _fadeInSeconds);
            }

            // Squared, so the fade is gentle where it is most audible — a linear
            // ramp from silence sounds like it arrives all at once.
            float shaped = _fade * _fade;

            // Re-applied after the fade completes as well, because the level is now
            // a user setting that can move while this screen is open.
            _crowd.volume = shaped * Systems_AudioSettings.CrowdBus * MENU_BED_RATIO;
        }
    }
}
