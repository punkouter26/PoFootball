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
    ///
    /// WHAT CHANGED IN THE AUDIO PASS, AND WHY EACH IS NOT A RETURN OF THE 1,800
    /// LINES ABOVE:
    ///
    ///   THE FIELD SOUNDS ARE POSITIONAL NOW. This class used to build every voice
    ///   at spatialBlend = 0 and fake position with a stereo pan clamped to 0.45,
    ///   and its comment explained that real spatialisation was pointless because
    ///   "the listener is a camera forty metres up". That was true and it was a
    ///   statement about the LISTENER, not about spatial audio.
    ///   Systems_BroadcastMicView now puts the listener nine metres above the ball
    ///   instead of wherever the camera drifted to, which makes distance mean
    ///   something, so the two sounds that happen at a place on the field — the hit
    ///   and the whistle — are emitted at that place. The manual pan is gone; it was
    ///   a worse approximation of a thing the engine does properly.
    ///
    ///   THE RESULT CUES STAY 2D, DELIBERATELY. A touchdown fanfare does not happen
    ///   anywhere on the field. It is a broadcast stinger, and panning it to
    ///   wherever the ball ended up would be a positional claim about a sound that
    ///   has no position.
    ///
    ///   THE CROWD DUCKS. One envelope, one multiply — the bed drops under a cue
    ///   and recovers. This is the single thing the deleted four-bus mixer did that
    ///   carried information: it is what makes a whistle audible over a stadium
    ///   without either sound being mixed loud.
    ///
    ///   THE TWO VOLUME FLOATS ARE GONE, into Systems_AudioSettings. They were
    ///   serialized in this file AND in SCN_GAME, and the comment that used to sit
    ///   here recorded the consequence: changing one without the other "would have
    ///   looked right in the diff and changed nothing you can hear". A level a
    ///   player should own does not belong in two files.
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
        /// Distance at which a 3D voice is at full volume. Generous, because the
        /// microphone already sits nine metres up — a tighter figure would make the
        /// mic's own height the dominant term in every attenuation.
        /// </summary>
        private const float VOICE_MIN_DISTANCE = 12f;

        /// <summary>
        /// Distance at which a 3D voice has fallen away. Slightly more than the
        /// length of the field, so a hit in the far end zone is quiet rather than
        /// silent — an inaudible tackle reads as a missing sound effect.
        /// </summary>
        private const float VOICE_MAX_DISTANCE = 130f;

        /// <summary>
        /// How far the crowd bed drops under a cue, as a fraction of its own level.
        /// </summary>
        private const float DUCK_DEPTH = 0.65f;

        /// <summary>
        /// How fast the duck recovers, per second. About a second and a half back
        /// to full, which is the pace a real broadcast mix returns at — fast enough
        /// not to leave a hole, slow enough not to pump.
        /// </summary>
        private const float DUCK_RECOVERY_RATE = 0.7f;

        [Header("Optional overrides")]
        [Tooltip("Leave empty to use the synthesised fallback in Systems_ToneBank.")]
        [SerializeField] private AudioClip _whistleOverride;
        [SerializeField] private AudioClip _impactOverride;
        [SerializeField] private AudioClip _touchdownOverride;
        [SerializeField] private AudioClip _firstDownOverride;
        [SerializeField] private AudioClip _turnoverOverride;

        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;
        private ISubscriber<Systems_GameOverMessage> _gameOverSubscriber;

        private IDisposable _resolvedSubscription;
        private IDisposable _tackleSubscription;
        private IDisposable _endedSubscription;
        private IDisposable _gameOverSubscription;

        private Systems_PlayerRegistry _registry;
        private Systems_BallModel _ball;
        private Systems_PresentationBudget _budget;

        private AudioSource[] _voices;
        private AudioSource _crowd;
        private int _nextVoice;

        /// <summary>
        /// Current duck amount in [0, 1], where 1 is fully ducked. Decays toward 0
        /// in Update; a cue slams it back to 1.
        /// </summary>
        private float _duck;

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
            Systems_BallModel ball,
            Systems_PresentationBudget budget)
        {
            _resolvedSubscriber = resolvedSubscriber;
            _tackleSubscriber = tackleSubscriber;
            _endedSubscriber = endedSubscriber;
            _gameOverSubscriber = gameOverSubscriber;
            _registry = registry;
            _ball = ball;
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

        /// <summary>
        /// Recovers the duck and re-applies the crowd level.
        ///
        /// The level is recomputed every frame rather than only when it changes,
        /// because it is the product of the duck envelope and a user setting that
        /// can move underneath this view at any time. It is one multiply and one
        /// property write on a single AudioSource — cheaper than the bookkeeping
        /// that would be needed to skip it.
        /// </summary>
        private void Update()
        {
            if (_crowd == null)
            {
                return;
            }

            if (_duck > 0f)
            {
                _duck = Mathf.Max(0f, _duck - (DUCK_RECOVERY_RATE * Time.deltaTime));
            }

            _crowd.volume = Systems_AudioSettings.CrowdBus * (1f - (DUCK_DEPTH * _duck));
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

        /// <summary>
        /// Builds the voice pool, one AudioSource per child GameObject.
        ///
        /// EACH VOICE NEEDS ITS OWN TRANSFORM, which is why these are children
        /// rather than four components on this one object as they used to be. A 3D
        /// AudioSource is heard at its transform's position, so four sources
        /// sharing one transform can only ever be at one place — and the whole
        /// point of the pool is that a hit at the near hash and a whistle at the far
        /// numbers can be in flight at the same moment.
        /// </summary>
        private void BuildVoices()
        {
            _voices = new AudioSource[VOICE_COUNT];

            for (int index = 0; index < VOICE_COUNT; index++)
            {
                GameObject holder = new GameObject($"Voice{index}");
                holder.transform.SetParent(transform, false);

                AudioSource source = holder.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = false;

                // Logarithmic, which is how sound actually falls off. Unity's
                // default is a custom curve that is close to linear and makes
                // everything past the min distance drop away far too evenly.
                source.rolloffMode = AudioRolloffMode.Logarithmic;
                source.minDistance = VOICE_MIN_DISTANCE;
                source.maxDistance = VOICE_MAX_DISTANCE;

                // Blend is set per cue in Play — field sounds are 3D, broadcast
                // stingers are 2D. See the class note.
                source.spatialBlend = 0f;

                _voices[index] = source;
            }
        }

        /// <summary>
        /// One looping bed. It exists so the discrete cues have something to cut
        /// through rather than landing in silence; it is not trying to react to the
        /// game beyond ducking under them.
        /// </summary>
        private void BuildCrowdBed()
        {
            _crowd = gameObject.AddComponent<AudioSource>();
            _crowd.clip = Systems_ToneBank.Crowd();
            _crowd.loop = true;
            _crowd.playOnAwake = false;

            // The crowd is everywhere. A stadium bed with a position would swing
            // around the listener as the mic follows the ball.
            _crowd.spatialBlend = 0f;
            _crowd.volume = Systems_AudioSettings.CrowdBus;
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

            Vector3 at = ContactPoint(message.CarrierId);

            PlayAt(_impact, 0.45f + (0.55f * force), 0.94f + (0.12f * force), at);
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            // The referee is standing over the ball, so that is where the whistle
            // comes from.
            PlayAt(_whistle, 0.7f, 1f, BallPoint());
        }

        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            switch (message.Result)
            {
                case Systems_DownResult.Touchdown:
                    PlayFlat(_touchdown, 1f, 1f);
                    break;

                case Systems_DownResult.FirstDown:
                    PlayFlat(_firstDown, 0.8f, 1f);
                    break;

                case Systems_DownResult.Interception:
                case Systems_DownResult.TurnoverOnDowns:
                case Systems_DownResult.Safety:
                    PlayFlat(_turnover, 0.9f, 1f);
                    break;
            }
        }

        private void OnGameOver(Systems_GameOverMessage message)
        {
            // Pitched down from the play whistle so the final one is recognisably
            // different from the two hundred that preceded it. Flat rather than
            // positional: the game is over, there is no play to locate it at.
            PlayFlat(_whistle, 1f, 0.92f);
        }

        // --- Playback ----------------------------------------------------------

        /// <summary>
        /// Where a tackle happened. Falls back to the ball when the carrier cannot
        /// be resolved, which is the right answer anyway — the ball is at the
        /// contact point during a tackle.
        /// </summary>
        private Vector3 ContactPoint(int carrierId)
        {
            if (_registry != null)
            {
                Systems_IPlayerHandle carrier = _registry.FindById(carrierId);

                if (carrier != null)
                {
                    return new Vector3(carrier.Position.x, carrier.Position.y, 0f);
                }
            }

            return BallPoint();
        }

        private Vector3 BallPoint()
        {
            if (_ball == null)
            {
                return Vector3.zero;
            }

            return new Vector3(_ball.Position.x, _ball.Position.y, 0f);
        }

        /// <summary>A sound that happens somewhere on the field.</summary>
        private void PlayAt(AudioClip clip, float volume, float pitch, Vector3 position)
        {
            AudioSource source = TakeVoice();

            if (source == null || clip == null)
            {
                return;
            }

            source.transform.position = position;
            source.spatialBlend = 1f;

            Emit(source, clip, volume, pitch);
        }

        /// <summary>A broadcast stinger, which happens nowhere.</summary>
        private void PlayFlat(AudioClip clip, float volume, float pitch)
        {
            AudioSource source = TakeVoice();

            if (source == null || clip == null)
            {
                return;
            }

            source.spatialBlend = 0f;

            Emit(source, clip, volume, pitch);
        }

        private void Emit(AudioSource source, AudioClip clip, float volume, float pitch)
        {
            source.pitch = pitch;

            // Every cue ducks the bed. Scaled by the cue's own volume so a glancing
            // tackle dips the crowd less than a touchdown does.
            _duck = Mathf.Max(_duck, Mathf.Clamp01(volume));

            source.PlayOneShot(
                clip, Mathf.Clamp01(volume * Systems_AudioSettings.EffectsBus));
        }

        /// <summary>
        /// Next voice in the ring. Overwrites the oldest when all are busy, which
        /// is the right failure for short cues — dropping the newest would mean the
        /// loudest moments go silent.
        /// </summary>
        private AudioSource TakeVoice()
        {
            if (_voices == null)
            {
                return null;
            }

            AudioSource source = _voices[_nextVoice];
            _nextVoice = (_nextVoice + 1) % _voices.Length;
            return source;
        }
    }
}
