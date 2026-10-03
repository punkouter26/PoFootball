using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The burst at the point of contact when a defender brings the carrier down,
    /// sized by the closing speed that actually satisfied the tackle rule.
    ///
    /// WHY IT IS DRIVEN BY TELEMETRY AND NOT BY A CURVE SOMEBODY LIKED. A tackle
    /// in this simulation already carries a real number — Systems_Referee measures
    /// the closing speed between the two bodies and publishes it on
    /// Systems_TackleMessage, and Reward_Terminal and the fumble model both price
    /// decisions off it. Scaling the burst by the same quantity, normalized over
    /// the same range Systems_AudioView already uses for the impact sound, means a
    /// viewer who learns to read the effect has learned to read the simulation: a
    /// big flash IS a big hit, and it is the same big hit the fumble model is
    /// about to roll against. An effect tuned to look nice would teach nothing and
    /// would drift from the audio the first time either was touched.
    ///
    /// ONE PARTICLE SYSTEM, EMITTED AT WORLD POSITIONS. The obvious construction is
    /// a pooled prefab spawned at the contact point, which is a pool, a prefab, a
    /// lifetime and a draw call per simultaneous hit. A single system simulating in
    /// world space and emitting through EmitParams needs none of those: every
    /// impact on the field is the same buffer and the same material, so a
    /// four-man pile-up costs exactly what one tackle costs.
    ///
    /// A CATCH FLASHES TOO, THROUGH THE SAME SYSTEM AND BY THE SAME ARGUMENT. Hands
    /// on the ball is the other moment of contact in a play, and until
    /// Systems_PassCaughtMessage existed it left no mark on the field at all: the
    /// ball's trail simply stopped. The burst is sized by how far the ball flew,
    /// over the longest throw the simulation permits, so a deep ball brought in
    /// forty yards downfield reads as bigger than a checkdown — and it is gold for
    /// the offense and red for an interception, the two colours the HUD already
    /// uses for a good result and a bad one. No second particle system and no
    /// second draw call: it is two more calls into the buffer the tackle uses.
    ///
    /// A BLOCK PUFFS, AND DOES NOT FLASH. Systems_ContactMessage is every other
    /// collision between opponents — the line engaging, a receiver jammed — sized
    /// by the impulse the solver applied. It gets dust thrown out sideways from
    /// between the two bodies and nothing else: no white flash and no bright
    /// spark, because Systems_PostProcessView's bloom is thresholded so that only
    /// the carrier and the hit that stops him exceed it, and a field that flashes
    /// ten times a play has stopped saying which collision mattered. Same buffer,
    /// same material, still one draw call.
    ///
    /// NO CAMERA SHAKE, DELIBERATELY. Systems_BroadcastCameraView used to kick on a
    /// big hit and drop into slow motion, and both were removed — the slow motion
    /// because a presentation view driving Time.timeScale stranded the editor at
    /// 0.35 when a scene unloaded mid-effect. Nothing here writes a global, and
    /// nothing here touches the camera.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class Systems_ImpactView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>Above the ball on 5, because a hit should never be occluded.</summary>
        private const int SORTING_ORDER = 6;

        /// <summary>Room for several simultaneous bursts plus their tails.</summary>
        private const int MAX_PARTICLES = 256;

        private const int MIN_SPARKS = 5;
        private const int MAX_SPARKS = 18;

        private const float SPARK_SPEED_MIN = 2.4f;
        private const float SPARK_SPEED_MAX = 9f;
        private const float SPARK_LIFETIME = 0.42f;
        private const float SPARK_SIZE = 0.34f;

        private const float FLASH_LIFETIME = 0.16f;
        private const float FLASH_SIZE_MIN = 1.1f;
        private const float FLASH_SIZE_MAX = 2.9f;

        /// <summary>
        /// How much of a spark's direction comes from the line of the collision
        /// rather than from the ring. Purely radial sparks read as an explosion
        /// under the players; biasing them along the hit reads as one body running
        /// through another, which is what happened.
        /// </summary>
        private const float CLOSING_BIAS = 0.55f;

        private static readonly Color SparkColor = new Color(1f, 0.949f, 0.804f, 0.95f);
        private static readonly Color FlashColor = new Color(1f, 1f, 1f, 0.5f);

        /// <summary>
        /// The longest pass the simulation can throw, in yards: top speed for the
        /// whole of the flight cap. The range a catch is sized over, derived rather
        /// than typed so it moves with either constant.
        /// </summary>
        private const float LONGEST_THROW_YARDS =
            Systems_SimConstants.PASS_SPEED_MAX
            * Systems_SimConstants.MAX_FLIGHT_TICKS
            * Systems_GameRules.SECONDS_PER_TICK
            / Systems_FieldModel.YARD;

        /// <summary>
        /// Floor under a catch's size, so a two-yard flip still registers. A hit
        /// has no such floor because a hit that barely clears the tackle threshold
        /// SHOULD barely show.
        /// </summary>
        private const float CATCH_MIN_REACH = 0.25f;

        private const float CATCH_FLASH_ALPHA = 0.55f;

        private const int MIN_DUST = 2;
        private const int MAX_DUST = 7;

        private const float DUST_SPEED_MIN = 1f;
        private const float DUST_SPEED_MAX = 3f;
        private const float DUST_LIFETIME = 0.42f;
        private const float DUST_SIZE = 0.3f;

        /// <summary>
        /// How far along the seam between the two bodies a mote may start, in
        /// metres either side of the contact point — most of a body radius.
        ///
        /// THE DUST IS SPREAD BY WHERE IT STARTS, NOT BY HOW FAST IT LEAVES, and
        /// that was found on a capture rather than reasoned out. The system's
        /// velocity limit is shared with the sparks and pulls everything to about
        /// 1.5 m/s within a few frames, whatever it was launched at: tripling the
        /// launch speed moved the dust by a pixel. Emitted from the point itself
        /// it never got out from between the two players and sat on the contact
        /// like a sticker.
        /// </summary>
        private const float DUST_SEAM = 0.45f;

        /// <summary>
        /// Half-angle of the fan the dust leaves in, in radians, about the line
        /// the two bodies are squeezing it out along.
        /// </summary>
        private const float DUST_SPREAD = 0.5f;

        /// <summary>
        /// Chalk and dry turf. Pale so it shows against the grass, and well short
        /// of opaque white so it stays under the bloom threshold — see the class
        /// note on why a block must not flash.
        /// </summary>
        private static readonly Color DustColor = new Color(0.8f, 0.77f, 0.64f, 0.5f);

        private Systems_PlayerRegistry _registry;
        private Systems_PresentationBudget _budget;
        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private ISubscriber<Systems_PassCaughtMessage> _caughtSubscriber;
        private ISubscriber<Systems_ContactMessage> _contactSubscriber;
        private IDisposable _tackleSubscription;
        private IDisposable _caughtSubscription;
        private IDisposable _contactSubscription;

        private ParticleSystem _particles;
        private ParticleSystem.EmitParams _emit;

        /// <summary>
        /// A private RNG rather than UnityEngine.Random.
        ///
        /// Random is a single global stream. A presentation view drawing from it
        /// would advance it a variable number of times per frame depending on how
        /// many tackles happened, and anything in the project that ever reaches for
        /// it afterwards would get a different number in a game with effects on
        /// than in one with them off. Nothing does today; UNITY_RULES section 2
        /// asks for deterministic execution, and a decoration is the last thing
        /// that should be able to cost it. Xorshift32, seeded off nothing that
        /// matters.
        /// </summary>
        private uint _noise = 0x9E3779B9u;

        [Inject]
        public void Construct(
            Systems_PlayerRegistry registry,
            Systems_PresentationBudget budget,
            ISubscriber<Systems_TackleMessage> tackleSubscriber,
            ISubscriber<Systems_PassCaughtMessage> caughtSubscriber,
            ISubscriber<Systems_ContactMessage> contactSubscriber)
        {
            _registry = registry;
            _budget = budget;
            _tackleSubscriber = tackleSubscriber;
            _caughtSubscriber = caughtSubscriber;
            _contactSubscriber = contactSubscriber;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled || _tackleSubscriber == null)
            {
                enabled = false;
                return;
            }

            if (!Systems_ParticleMaterial.TryLoad(
                    nameof(Systems_ImpactView), "Hits are drawn flat.", out Material source))
            {
                enabled = false;
                return;
            }

            BuildParticles(source);
            _tackleSubscription = _tackleSubscriber.Subscribe(OnTackle);
            _caughtSubscription = _caughtSubscriber?.Subscribe(OnPassCaught);
            _contactSubscription = _contactSubscriber?.Subscribe(OnContact);
        }

        private void OnDestroy()
        {
            // Views dispose their own subscriptions (.claude/rules/architecture.md).
            // OnDestroy rather than OnDisable: this view is never toggled, and
            // dropping the subscription on a disable it can never come back from
            // would silently stop every future hit from being drawn.
            _tackleSubscription?.Dispose();
            _tackleSubscription = null;

            _caughtSubscription?.Dispose();
            _caughtSubscription = null;

            _contactSubscription?.Dispose();
            _contactSubscription = null;
        }

        private void BuildParticles(Material source)
        {
            GameObject host = new GameObject("ImpactBurst");
            host.transform.SetParent(transform, false);

            _particles = host.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = _particles.main;

            // LOOPING, EVEN THOUGH IT NEVER EMITS ON ITS OWN, and this is not a
            // contradiction — it is the fix for a bug this shipped with.
            //
            // A non-looping system stops itself when its duration elapses, five
            // seconds after Play(). A STOPPED system does not simulate, so every
            // Emit after the first five seconds of a session put particles into a
            // buffer that never advanced: the first tackle or two of a game
            // flashed and every one after it drew nothing. Looping keeps the
            // system alive to simulate whatever Emit injects, and costs nothing
            // per frame because the emission module below is disabled — there is
            // no rate and no burst for the loop to fire.
            main.loop = true;
            main.playOnAwake = false;

            // World space, so a burst stays where the hit happened instead of
            // riding this transform. Nothing parents this object to a player, but
            // the default is Local and the difference only shows up once something
            // moves.
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MAX_PARTICLES;
            main.startSpeed = 0f;
            main.startSize = SPARK_SIZE;
            main.startLifetime = SPARK_LIFETIME;
            main.gravityModifier = 0f;

            // Everything is emitted explicitly through Emit. An emission module
            // left on would trickle particles out at the origin forever.
            ParticleSystem.EmissionModule emission = _particles.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = _particles.shape;
            shape.enabled = false;

            // The sparks slow as they travel and fade as they go, which is what
            // makes a burst read as a burst rather than as a ring expanding at
            // constant speed forever.
            ParticleSystem.LimitVelocityOverLifetimeModule drag =
                _particles.limitVelocityOverLifetime;

            drag.enabled = true;
            drag.dampen = 0.35f;
            drag.limit = new ParticleSystem.MinMaxCurve(1.5f);

            ParticleSystem.ColorOverLifetimeModule fade = _particles.colorOverLifetime;
            fade.enabled = true;
            fade.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient());

            ParticleSystem.SizeOverLifetimeModule shrink = _particles.sizeOverLifetime;
            shrink.enabled = true;
            shrink.size = new ParticleSystem.MinMaxCurve(
                1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f));

            ParticleSystemRenderer renderer =
                host.GetComponent<ParticleSystemRenderer>();

            renderer.sharedMaterial = source;
            renderer.sortingOrder = SORTING_ORDER;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.alignment = ParticleSystemRenderSpace.View;

            _particles.Play();
        }

        private static Gradient BuildFadeGradient()
        {
            Gradient gradient = new Gradient();

            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(Color.white, 0f),
                    new GradientColorKey(Color.white, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.85f, 0.25f),
                    new GradientAlphaKey(0f, 1f)
                });

            return gradient;
        }

        /// <summary>
        /// SAME NORMALIZATION AS Systems_AudioView.OnTackle, on purpose.
        /// TACKLE_CLOSING_SPEED is the floor that counts as contact at all and
        /// MAX_BODY_SPEED is the hardest collision the speed clamp permits, so a
        /// hit that sounds like a nine is the hit that looks like a nine. Two
        /// independently chosen ranges would put the flash and the crack out of
        /// step and neither would mean anything.
        /// </summary>
        private void OnTackle(Systems_TackleMessage message)
        {
            Systems_IPlayerHandle carrier = _registry.FindById(message.CarrierId);
            Systems_IPlayerHandle tackler = _registry.FindById(message.TacklerId);

            if (carrier == null)
            {
                return;
            }

            float force = Mathf.InverseLerp(
                Systems_SimConstants.TACKLE_CLOSING_SPEED,
                Systems_SimConstants.MAX_BODY_SPEED,
                message.ClosingSpeed);

            // The contact point, not the carrier's centre. Halfway between the two
            // bodies is where a viewer's eye already is.
            Vector2 point = tackler == null
                ? carrier.Position
                : Vector2.Lerp(carrier.Position, tackler.Position, 0.5f);

            Vector2 closing = tackler == null
                ? Vector2.zero
                : carrier.Position - tackler.Position;

            if (closing.sqrMagnitude > 1e-4f)
            {
                closing = closing.normalized;
            }

            EmitFlash(point, force, FlashColor);
            EmitSparks(point, closing, force, SparkColor);
        }

        /// <summary>
        /// The burst on the hands that caught it. Radial, with no closing bias:
        /// a tackle has a direction — one body running through another — and a
        /// catch does not.
        /// </summary>
        private void OnPassCaught(Systems_PassCaughtMessage message)
        {
            Systems_IPlayerHandle catcher = _registry.FindById(message.CatcherId);

            if (catcher == null)
            {
                return;
            }

            float reach = Mathf.Max(
                CATCH_MIN_REACH, Mathf.Clamp01(message.AirYards / LONGEST_THROW_YARDS));

            Color color = message.Intercepted
                ? Systems_UiTheme.Negative
                : Systems_UiTheme.Accent;

            Color flash = color;
            flash.a = CATCH_FLASH_ALPHA;

            EmitFlash(catcher.Position, reach, flash);
            EmitSparks(catcher.Position, Vector2.zero, reach, color);
        }

        /// <summary>
        /// Dust out of the seam between two bodies.
        ///
        /// ALONG THE TANGENT, NOT THE NORMAL. The normal is the line the two of
        /// them are pushing along, and that line is full of player. What a
        /// collision throws out leaves at right angles to it, both ways — which
        /// is also why it does not matter that the message cannot say which body
        /// its normal points away from.
        /// </summary>
        private void OnContact(Systems_ContactMessage message)
        {
            float strength = message.Strength;
            Vector2 tangent = new Vector2(-message.Normal.y, message.Normal.x);

            int count = Mathf.RoundToInt(Mathf.Lerp(MIN_DUST, MAX_DUST, strength));

            for (int mote = 0; mote < count; mote++)
            {
                float side = (mote & 1) == 0 ? 1f : -1f;
                float spread = ((NextFloat() * 2f) - 1f) * DUST_SPREAD;

                float cosine = Mathf.Cos(spread);
                float sine = Mathf.Sin(spread);

                Vector2 direction = new Vector2(
                    (tangent.x * cosine) - (tangent.y * sine),
                    (tangent.x * sine) + (tangent.y * cosine)) * side;

                float speed = Mathf.Lerp(
                    DUST_SPEED_MIN, DUST_SPEED_MAX, strength * (0.5f + NextFloat()));

                // See DUST_SEAM: this offset, not the speed, is what gets the dust
                // out from between the two bodies.
                Vector2 origin = message.Point
                    + (tangent * (side * DUST_SEAM * (0.35f + (0.65f * NextFloat()))));

                _emit = default;
                _emit.position = origin;
                _emit.velocity = direction * speed;
                _emit.startLifetime = DUST_LIFETIME * (0.7f + (NextFloat() * 0.6f));
                _emit.startSize = DUST_SIZE * (0.6f + (strength * 0.9f));
                _emit.startColor = DustColor;

                _particles.Emit(_emit, 1);
            }
        }

        private void EmitFlash(Vector2 point, float force, Color color)
        {
            _emit = default;
            _emit.position = point;
            _emit.velocity = Vector3.zero;
            _emit.startLifetime = FLASH_LIFETIME;
            _emit.startSize = Mathf.Lerp(FLASH_SIZE_MIN, FLASH_SIZE_MAX, force);
            _emit.startColor = color;

            _particles.Emit(_emit, 1);
        }

        /// <summary>
        /// One Emit per spark rather than one Emit of N.
        ///
        /// EmitParams applies the same values to every particle in the call, so a
        /// single batched emit would fire every spark in one direction at one
        /// speed. The loop runs at most eighteen times and only when a tackle is
        /// published — a handful of times a play, not per frame.
        /// </summary>
        private void EmitSparks(Vector2 point, Vector2 closing, float force, Color color)
        {
            int count = Mathf.RoundToInt(Mathf.Lerp(MIN_SPARKS, MAX_SPARKS, force));

            for (int spark = 0; spark < count; spark++)
            {
                float angle = NextFloat() * Mathf.PI * 2f;

                Vector2 radial = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 direction = Vector2.Lerp(radial, closing, CLOSING_BIAS);

                if (direction.sqrMagnitude < 1e-4f)
                {
                    direction = radial;
                }

                float speed = Mathf.Lerp(
                    SPARK_SPEED_MIN, SPARK_SPEED_MAX, force * (0.55f + NextFloat()));

                _emit = default;
                _emit.position = point;
                _emit.velocity = direction.normalized * speed;
                _emit.startLifetime = SPARK_LIFETIME * (0.7f + (NextFloat() * 0.6f));
                _emit.startSize = SPARK_SIZE * (0.6f + (force * 0.8f));
                _emit.startColor = color;

                _particles.Emit(_emit, 1);
            }
        }

        /// <summary>Xorshift32, in [0, 1). See the note on <see cref="_noise"/>.</summary>
        private float NextFloat()
        {
            _noise ^= _noise << 13;
            _noise ^= _noise >> 17;
            _noise ^= _noise << 5;

            return (_noise & 0x00FFFFFFu) / (float)0x01000000u;
        }
    }
}
