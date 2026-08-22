using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Every noise the game makes: a whistle at the end of a play, a pad pop on a
    /// tackle, one cue per scoring result, and a crowd bed underneath.
    ///
    /// A View rather than a System. Audio is presentation — it reads what happened
    /// and makes a noise, it never decides anything — and it owns AudioSources,
    /// which are components.
    ///
    /// SCOPE. This deliberately stops at the five cues in Systems_ToneBank. The
    /// previous version carried four crossfaded crowd beds, a four-bus mixer with
    /// snapshots, per-cue variant sets, cleat scuffs walked over the registry every
    /// frame, an organ and a PA — about 1,800 lines to make synthesised audio sound
    /// less synthesised. None of it changed what a viewer understood about the game,
    /// and the per-frame footfall walk was the only audio cost the simulation could
    /// actually feel. What survives is the part that carries information: you can
    /// hear a play end, hear how hard the hit was, and hear whether the result was
    /// good or bad.
    ///
    /// Voices are pooled and fixed in number: a tackle can land on the same tick as
    /// a whistle, so one shared source would cut its own tail off, and
    /// PlayClipAtPoint allocates a GameObject per hit.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Systems_AudioView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// Concurrent one-shot voices. Four is enough for the worst real overlap:
        /// an impact, the whistle that follows it, and the result cue.
        /// </summary>
        private const int VOICE_COUNT = 4;

        /// <summary>
        /// Stereo width. A tackle on the near sideline is nudged toward that ear,
        /// but only slightly — the camera sits well back from a fifty-metre-wide
        /// field, and full panning would put half the game in one ear.
        /// </summary>
        private const float MAX_PAN = 0.45f;

        [Header("Optional overrides")]
        [Tooltip("Leave empty to use the synthesised fallback in Systems_ToneBank.")]
        [SerializeField] private AudioClip _whistleOverride;
        [SerializeField] private AudioClip _impactOverride;
        [SerializeField] private AudioClip _touchdownOverride;
        [SerializeField] private AudioClip _firstDownOverride;
        [SerializeField] private AudioClip _turnoverOverride;

        // BOTH GAINS WERE CUT BY 80% ON 2026-08-22, AT THE OWNER'S REQUEST.
        // These two floats are the whole mix: every one-shot is scaled by
        // _effectsVolume in Play, and the looping bed is _crowdVolume outright, so
        // scaling the pair by 0.2 is an exact 80% cut across every sound the game
        // makes. The previous levels were 0.9 and 0.3.
        //
        // The scene carries its own serialized copies of both, and a serialized
        // value wins over the initializer here — so SCN_GAME was edited to match.
        // Changing only this file would have looked right in the diff and changed
        // nothing you can hear.
        [Header("Mix")]
        [Range(0f, 1f)]
        [SerializeField] private float _effectsVolume = 0.18f;

        [Range(0f, 1f)]
        [SerializeField] private float _crowdVolume = 0.06f;

        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;
        private ISubscriber<Systems_GameOverMessage> _gameOverSubscriber;

        private IDisposable _resolvedSubscription;
        private IDisposable _tackleSubscription;
        private IDisposable _endedSubscription;
        private IDisposable _gameOverSubscription;

        private Systems_PlayerRegistry _registry;
        private Systems_PresentationBudget _budget;

        private AudioSource[] _voices;
        private AudioSource _crowd;
        private int _nextVoice;

        // Cues. Built once at Start, never reallocated.
        private AudioClip _whistle;
        private AudioClip _impact;
        private AudioClip _touchdown;
        private AudioClip _firstDown;
        private AudioClip _turnover;

        [Inject]
        public void Construct(
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_TackleMessage> tackleSubscriber,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber,
            ISubscriber<Systems_GameOverMessage> gameOverSubscriber,
            Systems_PlayerRegistry registry,
            Systems_PresentationBudget budget)
        {
            _resolvedSubscriber = resolvedSubscriber;
            _tackleSubscriber = tackleSubscriber;
            _endedSubscriber = endedSubscriber;
            _gameOverSubscriber = gameOverSubscriber;
            _registry = registry;
            _budget = budget;
        }

        private void Start()
        {
            // Start rather than Awake, because the budget arrives by injection and
            // synthesising the clips is the most expensive thing this view does.
            // In training there is no reason to build them for a device that is
            // not connected.
            if (_budget == null || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            BuildClips();
            BuildVoices();
            BuildCrowdBed();

            _resolvedSubscription = _resolvedSubscriber?.Subscribe(OnDownResolved);
            _tackleSubscription = _tackleSubscriber?.Subscribe(OnTackle);
            _endedSubscription = _endedSubscriber?.Subscribe(OnPlayEnded);
            _gameOverSubscription = _gameOverSubscriber?.Subscribe(OnGameOver);
        }

        private void OnDestroy()
        {
            _resolvedSubscription?.Dispose();
            _tackleSubscription?.Dispose();
            _endedSubscription?.Dispose();
            _gameOverSubscription?.Dispose();
        }

        private void BuildClips()
        {
            _whistle = _whistleOverride != null ? _whistleOverride : Systems_ToneBank.Whistle();
            _impact = _impactOverride != null ? _impactOverride : Systems_ToneBank.Impact();

            _touchdown = _touchdownOverride != null
                ? _touchdownOverride
                : Systems_ToneBank.Touchdown();

            _firstDown = _firstDownOverride != null
                ? _firstDownOverride
                : Systems_ToneBank.FirstDown();

            _turnover = _turnoverOverride != null
                ? _turnoverOverride
                : Systems_ToneBank.Turnover();
        }

        private void BuildVoices()
        {
            _voices = new AudioSource[VOICE_COUNT];

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                AudioSource source = gameObject.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;

                // 2D with manual panning. Real spatialisation would attenuate by
                // distance from the listener, and the listener is a camera forty
                // metres up — every sound on the field would be equally far away
                // and equally quiet.
                source.spatialBlend = 0f;

                _voices[index] = source;
            }
        }

        /// <summary>
        /// One looping bed at a fixed level. It exists so the discrete cues have
        /// something to cut through rather than landing in silence; it is not
        /// trying to react to the game.
        /// </summary>
        private void BuildCrowdBed()
        {
            _crowd = gameObject.AddComponent<AudioSource>();
            _crowd.clip = Systems_ToneBank.Crowd();
            _crowd.loop = true;
            _crowd.playOnAwake = false;
            _crowd.spatialBlend = 0f;
            _crowd.volume = _crowdVolume;
            _crowd.Play();
        }

        // --- Event handlers ---------------------------------------------------

        private void OnTackle(Systems_TackleMessage message)
        {
            // Volume tracks the closing speed that satisfied the tackle rule, so a
            // big hit sounds like one. TACKLE_CLOSING_SPEED is the floor that
            // counts as contact at all; MAX_BODY_SPEED is the hardest possible.
            float force = Mathf.InverseLerp(
                Systems_SimConstants.TACKLE_CLOSING_SPEED,
                Systems_SimConstants.MAX_BODY_SPEED,
                message.ClosingSpeed);

            float pan = 0f;

            if (_registry != null)
            {
                Systems_IPlayerHandle carrier = _registry.FindById(message.CarrierId);

                if (carrier != null)
                {
                    pan = PanFor(carrier.Position.x);
                }
            }

            Play(_impact, 0.45f + 0.55f * force, 0.94f + 0.12f * force, pan);
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            Play(_whistle, 0.7f, 1f, 0f);
        }

        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            switch (message.Result)
            {
                case Systems_DownResult.Touchdown:
                    Play(_touchdown, 1f, 1f, 0f);
                    break;

                case Systems_DownResult.FirstDown:
                    Play(_firstDown, 0.8f, 1f, 0f);
                    break;

                case Systems_DownResult.Interception:
                case Systems_DownResult.TurnoverOnDowns:
                case Systems_DownResult.Safety:
                    Play(_turnover, 0.9f, 1f, 0f);
                    break;
            }
        }

        private void OnGameOver(Systems_GameOverMessage message)
        {
            // Pitched down from the play whistle so the final one is recognisably
            // different from the two hundred that preceded it.
            Play(_whistle, 1f, 0.92f, 0f);
        }

        // --- Playback ----------------------------------------------------------

        /// <summary>
        /// Field X to stereo pan. The field is 53.3 yards wide and the shot is a
        /// long way back, so this is a hint rather than a position.
        /// </summary>
        private static float PanFor(float fieldX)
        {
            return Mathf.Clamp(fieldX / Systems_FieldModel.HALF_WIDTH, -1f, 1f) * MAX_PAN;
        }

        /// <summary>
        /// Plays a one-shot on the next voice in the ring. Overwrites the oldest
        /// voice when all are busy, which is the right failure for short cues —
        /// dropping the newest would mean the loudest moments go silent.
        /// </summary>
        private void Play(AudioClip clip, float volume, float pitch, float pan)
        {
            if (clip == null || _voices == null)
            {
                return;
            }

            AudioSource source = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;

            source.pitch = pitch;
            source.panStereo = pan;

            source.PlayOneShot(clip, Mathf.Clamp01(volume * _effectsVolume));
        }
    }
}
