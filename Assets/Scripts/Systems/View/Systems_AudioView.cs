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
    /// SCOPE. This deliberately stops at the eight cues in Systems_ToneBank. The
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
    ///
    /// AND IN THE BROADCAST PASS, UNDER THE SAME TEST — DOES IT CARRY INFORMATION:
    ///
    ///   A PASS MAKES TWO SOUNDS NOW. The release and the catch were both silent,
    ///   so the passing game was the one part of a play you could not follow with
    ///   your eyes shut: a completion sounded like nothing, then a tackle. The
    ///   catch is the same pop for either side, pitched down when a defender takes
    ///   it — the turnover stinger follows on the same tick and says the rest.
    ///
    ///   THE CROWD FOLLOWS THE GAME. Not the four crossfaded beds this file once
    ///   had: the one bed, one more multiply. It rises with Systems_Leverage —
    ///   a late down, the red zone, a close game running out of clock — and swells
    ///   while a pass is in the air. A stadium that sounds the same on first and
    ///   ten in the first quarter as on fourth and goal with a minute left is
    ///   telling the viewer that neither matters.
    ///
    ///   THE LINE CAN BE HEARD. Systems_ContactMessage is every collision between
    ///   opponents away from the ball, sized by the impulse the solver applied,
    ///   and it is a duller sound than the tackle so the two cannot be confused.
    ///   This is NOT the cleat scuff coming back. That walked the registry every
    ///   frame looking for something to sound; this is pushed, by the physics
    ///   callback that fired anyway, and it is rate limited to one voice at a time
    ///   so seven linemen engaging on the same tick are one crunch, not seven.
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

        /// <summary>
        /// How much louder the bed is at full leverage, as a fraction of the
        /// player's own crowd level. Close to double: a change of a few decibels,
        /// which is about the least a listener reliably notices in a noise bed.
        ///
        /// A LIFT ABOVE THE SETTING, NOT A CUT BELOW IT. The other way to make room
        /// is to treat the setting as the peak and rest the bed lower, and that
        /// would quietly halve the level the owner chose for every ordinary down.
        /// The price is headroom: AudioSource.volume stops at 1, so a crowd fader
        /// pushed past about a half has less lift left to give. The default is
        /// 0.06.
        /// </summary>
        private const float CROWD_LEVERAGE_GAIN = 0.9f;

        /// <summary>
        /// Extra lift while a pass is in the air, on top of the leverage. The
        /// intake of breath: it starts at the release and is gone a moment after
        /// the catch, whichever way that went.
        /// </summary>
        private const float CROWD_FLIGHT_SWELL = 0.5f;

        /// <summary>
        /// How fast the bed chases its level, per second. Quick enough that the
        /// swell arrives inside a one-second throw, slow enough that a change of
        /// down does not step the level audibly.
        /// </summary>
        private const float CROWD_LIFT_RATE = 3f;

        /// <summary>
        /// Least time between two block sounds, in real seconds. The line engages
        /// as five or six collisions inside a tick or two; played individually
        /// they would take every voice in the pool and still read as one noise.
        ///
        /// REAL SECONDS, NOT GAME SECONDS. It is a limit on what an ear is asked
        /// to separate, and at Sim Speed 8x a game-time interval would allow
        /// eighty of them a second.
        /// </summary>
        private const float CONTACT_INTERVAL = 0.1f;

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
        private ISubscriber<Systems_PassThrownMessage> _thrownSubscriber;
        private ISubscriber<Systems_PassCaughtMessage> _caughtSubscriber;
        private ISubscriber<Systems_ContactMessage> _contactSubscriber;

        private IDisposable _resolvedSubscription;
        private IDisposable _tackleSubscription;
        private IDisposable _endedSubscription;
        private IDisposable _gameOverSubscription;
        private IDisposable _thrownSubscription;
        private IDisposable _caughtSubscription;
        private IDisposable _contactSubscription;

        private Systems_PlayerRegistry _registry;
        private Systems_BallModel _ball;

        /// <summary>
        /// Read for the leverage of the coming snap and nothing else. A Game-mode
        /// registration, which is fine for the reason it is fine in Systems_HudView:
        /// this component is in SCN_GAME only.
        /// </summary>
        private Systems_GameModel _game;

        private Systems_PresentationBudget _budget;

        private AudioSource[] _voices;
        private AudioSource _crowd;
        private int _nextVoice;

        /// <summary>
        /// Current duck amount in [0, 1], where 1 is fully ducked. Decays toward 0
        /// in Update; a cue slams it back to 1.
        /// </summary>
        private float _duck;

        /// <summary>
        /// The bed's current level as a multiple of the crowd setting: 1 on an
        /// ordinary down, more when the moment is bigger. Chased, never assigned.
        /// </summary>
        private float _crowdLift = 1f;

        /// <summary>
        /// The hardest off-ball contact reported since the last block sound, and
        /// where it was. Zero strength means nothing is waiting.
        /// </summary>
        private float _pendingContactStrength;
        private Vector3 _pendingContactPoint;

        /// <summary>Real seconds until another block sound may play.</summary>
        private float _contactCooldown;

        // Cues. Built once at Start, never reallocated.
        private AudioClip _whistle;
        private AudioClip _impact;
        private AudioClip _touchdown;
        private AudioClip _firstDown;
        private AudioClip _turnover;
        private AudioClip _throw;
        private AudioClip _catch;
        private AudioClip _block;

        [Inject]
        public void Construct(
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_TackleMessage> tackleSubscriber,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber,
            ISubscriber<Systems_GameOverMessage> gameOverSubscriber,
            ISubscriber<Systems_PassThrownMessage> thrownSubscriber,
            ISubscriber<Systems_PassCaughtMessage> caughtSubscriber,
            ISubscriber<Systems_ContactMessage> contactSubscriber,
            Systems_PlayerRegistry registry,
            Systems_BallModel ball,
            Systems_GameModel game,
            Systems_PresentationBudget budget)
        {
            _resolvedSubscriber = resolvedSubscriber;
            _tackleSubscriber = tackleSubscriber;
            _endedSubscriber = endedSubscriber;
            _gameOverSubscriber = gameOverSubscriber;
            _thrownSubscriber = thrownSubscriber;
            _caughtSubscriber = caughtSubscriber;
            _contactSubscriber = contactSubscriber;
            _registry = registry;
            _ball = ball;
            _game = game;
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
            _thrownSubscription = _thrownSubscriber?.Subscribe(OnPassThrown);
            _caughtSubscription = _caughtSubscriber?.Subscribe(OnPassCaught);
            _contactSubscription = _contactSubscriber?.Subscribe(OnContact);
        }

        private void OnDestroy()
        {
            _resolvedSubscription?.Dispose();
            _tackleSubscription?.Dispose();
            _endedSubscription?.Dispose();
            _gameOverSubscription?.Dispose();
            _thrownSubscription?.Dispose();
            _caughtSubscription?.Dispose();
            _contactSubscription?.Dispose();
        }

        /// <summary>
        /// Recovers the duck, chases the crowd's lift and re-applies its level.
        ///
        /// The level is recomputed every frame rather than only when it changes,
        /// because it is the product of the duck envelope, the lift and a user
        /// setting that can move underneath this view at any time. It is a few
        /// multiplies and one property write on a single AudioSource — cheaper
        /// than the bookkeeping that would be needed to skip it.
        ///
        /// THE LEVERAGE IS POLLED, NOT SUBSCRIBED TO, and that is what makes the
        /// timing right. Systems_GameFlowSystem moves the down and the spot at the
        /// whistle, so by the time the dead-ball hold begins the game model already
        /// describes the NEXT snap — and the crowd comes up through the huddle,
        /// before a fourth down, which is when a real one does.
        /// </summary>
        private void Update()
        {
            FlushContact();

            if (_crowd == null)
            {
                return;
            }

            float deltaTime = Time.deltaTime;

            if (_duck > 0f)
            {
                _duck = Mathf.Max(0f, _duck - (DUCK_RECOVERY_RATE * deltaTime));
            }

            float lift = 1f + (CROWD_LEVERAGE_GAIN * Leverage());

            if (_ball != null && _ball.IsInFlight)
            {
                lift += CROWD_FLIGHT_SWELL;
            }

            _crowdLift = Mathf.Lerp(
                _crowdLift, lift, 1f - Mathf.Exp(-CROWD_LIFT_RATE * deltaTime));

            _crowd.volume = Mathf.Clamp01(
                Systems_AudioSettings.CrowdBus * _crowdLift * (1f - (DUCK_DEPTH * _duck)));
        }

        private float Leverage()
        {
            return _game == null ? 0f : Systems_Leverage.Of(_game);
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

            // No override slots for these two. The five above have one because
            // somebody may one day import a recording of a whistle; nobody is
            // going to go looking for a recording of a football being caught.
            _throw = Systems_ToneBank.Throw();
            _catch = Systems_ToneBank.Catch();
            _block = Systems_ToneBank.Block();
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
        /// through rather than landing in silence. It reacts to the game in level
        /// only — ducking under a cue, rising with the moment (see Update) — and
        /// it is still one clip on one source.
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

        private void OnPassThrown(Systems_PassThrownMessage message)
        {
            // Louder and a little higher for a ball thrown harder, over the same
            // range Systems_BallSystem.ThrowSpeedFor produces it from.
            float power = Mathf.InverseLerp(
                Systems_SimConstants.PASS_SPEED_MIN,
                Systems_SimConstants.PASS_SPEED_MAX,
                message.Speed);

            // IT DOES NOT DUCK THE BED. The release is what starts the crowd's
            // swell (see Update), and a duck on the same frame would pull the bed
            // down at the exact moment it is supposed to be rising — the two would
            // cancel and the flight would sound like nothing at all.
            PlayAtUnderTheBed(
                _throw, 0.35f + (0.3f * power), 0.92f + (0.16f * power), BallPoint());
        }

        /// <summary>
        /// Keeps the hardest contact since the last block sound. Played from
        /// Update, not from here: this runs inside the physics step, possibly
        /// several times in one tick, and the point of the cooldown is that those
        /// become one sound.
        /// </summary>
        private void OnContact(Systems_ContactMessage message)
        {
            float strength = message.Strength;

            if (strength <= _pendingContactStrength)
            {
                return;
            }

            _pendingContactStrength = strength;
            _pendingContactPoint = new Vector3(message.Point.x, message.Point.y, 0f);
        }

        private void FlushContact()
        {
            if (_contactCooldown > 0f)
            {
                _contactCooldown -= Time.unscaledDeltaTime;
            }

            if (_pendingContactStrength <= 0f || _contactCooldown > 0f)
            {
                return;
            }

            float strength = _pendingContactStrength;

            _pendingContactStrength = 0f;
            _contactCooldown = CONTACT_INTERVAL;

            // Quieter than the weakest tackle at its loudest, and lower as it
            // gets harder: a heavy collision is a deeper sound, not a higher one.
            //
            // UNDER THE BED, like the throw and for a different reason. This plays
            // several times a play; if each one ducked the crowd the bed would
            // pump for as long as the line was engaged.
            PlayAtUnderTheBed(
                _block, 0.14f + (0.3f * strength), 1.06f - (0.18f * strength),
                _pendingContactPoint);
        }

        private void OnPassCaught(Systems_PassCaughtMessage message)
        {
            // By now the ball model is attached to whoever caught it, so the ball
            // point IS the catch point.
            PlayAt(_catch, 0.7f, message.Intercepted ? 0.84f : 1.08f, BallPoint());
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

        /// <summary>
        /// A field sound that leaves the crowd where it is. Every cue ducks the
        /// bed in <see cref="Emit"/>; the two that must not are the exceptions, and
        /// this is them putting it back.
        /// </summary>
        private void PlayAtUnderTheBed(
            AudioClip clip, float volume, float pitch, Vector3 position)
        {
            float duckBefore = _duck;

            PlayAt(clip, volume, pitch, position);

            _duck = duckBefore;
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
