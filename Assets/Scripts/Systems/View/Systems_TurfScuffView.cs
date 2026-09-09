using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Turf kicked up by the ball carrier when he cuts.
    ///
    /// WHAT IT SHOWS THAT NOTHING ELSE DOES. The simulation already renders speed
    /// (PoFootball/Player narrows the body with _Lean), contact
    /// (Systems_ImpactView's burst) and exhaustion (the fatigue desaturation). The
    /// one thing a viewer cannot see is CHANGE OF DIRECTION, which in football is
    /// most of what separates a good run from a long one — a back who makes one
    /// hard cut and a back who runs straight look identical from forty metres up
    /// until the tackle happens. This puts a spray of turf under the cut, sized by
    /// the lateral acceleration that actually produced it.
    ///
    /// DRIVEN BY A MEASURED QUANTITY, LIKE THE HIT IS. Systems_ImpactView's header
    /// argues that an effect scaled by a real number teaches a viewer to read the
    /// simulation, while one "tuned to look nice would teach nothing". The number
    /// here is the component of the carrier's acceleration perpendicular to his own
    /// velocity, computed from the same Velocity the physics step wrote. A big
    /// spray IS a hard cut.
    ///
    /// IT WATCHES ONE PLAYER, NOT TWENTY-TWO, AND THAT IS THE WHOLE COST ARGUMENT.
    /// The obvious construction walks the registry every frame looking for anyone
    /// who is cutting. This project has deleted that exact pattern once already:
    /// Systems_AudioView's scope note records that its "cleat scuffs walked over
    /// the registry every frame" and were "the only audio cost the simulation could
    /// actually feel". So this tracks the carrier alone — one handle, resolved only
    /// when possession changes, and one vector subtraction per frame. Off the ball
    /// there is nothing to see anyway; the eye is on the run.
    ///
    /// ONE SYSTEM, WORLD SPACE, EMITTED THROUGH EmitParams — the same construction
    /// Systems_ImpactView uses and for the same reasons: no pool, no prefab, no
    /// per-spray draw call.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class Systems_TurfScuffView : MonoBehaviour, Systems_IInjectableView
    {
        private const string PARTICLE_MATERIAL_RESOURCE = "M_PoFootballParticle";

        /// <summary>Below the players and the ball — this is debris on the ground.</summary>
        private const int SORTING_ORDER = 1;

        private const int MAX_PARTICLES = 192;

        /// <summary>
        /// Lateral acceleration, in metres per second squared, below which nothing
        /// is drawn. Set well above the noise floor of ordinary steering so a
        /// receiver drifting into his route does not throw turf on every stride.
        /// </summary>
        private const float CUT_THRESHOLD = 14f;

        /// <summary>The lateral acceleration that produces a full-sized spray.</summary>
        private const float CUT_FULL = 45f;

        /// <summary>
        /// Minimum speed to spray at all. A player pivoting on the spot generates
        /// enormous lateral acceleration in the maths and kicks up nothing in
        /// reality, because there is no momentum behind the foot.
        /// </summary>
        private const float MIN_CUT_SPEED = 3.5f;

        /// <summary>
        /// Seconds between sprays. Without it a sustained cut emits on every frame
        /// of its duration and the field fills with turf.
        /// </summary>
        private const float SPRAY_INTERVAL = 0.09f;

        private const int MIN_CLODS = 3;
        private const int MAX_CLODS = 11;

        private const float CLOD_SPEED_MIN = 1.6f;
        private const float CLOD_SPEED_MAX = 6.5f;
        private const float CLOD_LIFETIME = 0.55f;
        private const float CLOD_SIZE = 0.22f;

        /// <summary>
        /// Torn grass. Deliberately darker and browner than the turf it comes from —
        /// what a cleat exposes is the soil under the sward, and a spray in the
        /// grass's own colour is invisible against the grass.
        /// </summary>
        private static readonly Color ClodColor = new Color(0.36f, 0.31f, 0.19f, 0.9f);

        private Systems_BallModel _ball;
        private Systems_PlayerRegistry _registry;
        private Systems_PresentationBudget _budget;

        private ParticleSystem _particles;
        private ParticleSystem.EmitParams _emit;

        private Systems_IPlayerHandle _carrier;
        private int _carrierId = -1;

        private Vector2 _lastVelocity;
        private bool _hasLastVelocity;
        private float _sprayCountdown;

        /// <summary>
        /// Private RNG, for the reason Systems_ImpactView states at length: a
        /// decoration must never advance UnityEngine.Random, because that would
        /// make a game with effects on diverge from one with them off.
        /// </summary>
        private uint _noise = 0x2545F491u;

        [Inject]
        public void Construct(
            Systems_BallModel ball,
            Systems_PlayerRegistry registry,
            Systems_PresentationBudget budget)
        {
            _ball = ball;
            _registry = registry;
            _budget = budget;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled || _ball == null)
            {
                enabled = false;
                return;
            }

            Material source = Resources.Load<Material>(PARTICLE_MATERIAL_RESOURCE);

            if (source == null)
            {
                Debug.LogWarning(
                    $"{nameof(Systems_TurfScuffView)}: no material at "
                    + $"Resources/{PARTICLE_MATERIAL_RESOURCE}. Cuts are drawn flat.");
                enabled = false;
                return;
            }

            BuildParticles(source);
        }

        private void BuildParticles(Material source)
        {
            GameObject host = new GameObject("TurfScuff");
            host.transform.SetParent(transform, false);

            _particles = host.AddComponent<ParticleSystem>();

            ParticleSystem.MainModule main = _particles.main;

            // Looping for the reason Systems_ImpactView documents: a stopped system
            // does not simulate, so a non-looping one silently stops drawing
            // anything a few seconds into the session.
            main.loop = true;
            main.playOnAwake = false;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = MAX_PARTICLES;
            main.startSpeed = 0f;
            main.startSize = CLOD_SIZE;
            main.startLifetime = CLOD_LIFETIME;

            // Clods fall back to the ground. This is the one effect in the project
            // with gravity, and it is what separates thrown turf from a spark.
            main.gravityModifier = 0.35f;

            ParticleSystem.EmissionModule emission = _particles.emission;
            emission.enabled = false;

            ParticleSystem.ShapeModule shape = _particles.shape;
            shape.enabled = false;

            ParticleSystem.LimitVelocityOverLifetimeModule drag =
                _particles.limitVelocityOverLifetime;

            drag.enabled = true;
            drag.dampen = 0.5f;
            drag.limit = new ParticleSystem.MinMaxCurve(1.2f);

            ParticleSystem.ColorOverLifetimeModule fade = _particles.colorOverLifetime;
            fade.enabled = true;
            fade.color = new ParticleSystem.MinMaxGradient(BuildFadeGradient());

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
                    new GradientAlphaKey(0.9f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                });

            return gradient;
        }

        private void Update()
        {
            ResolveCarrier();

            if (_sprayCountdown > 0f)
            {
                _sprayCountdown -= Time.deltaTime;
            }

            if (_carrier == null)
            {
                _hasLastVelocity = false;
                return;
            }

            Vector2 velocity = _carrier.Velocity;

            if (!_hasLastVelocity)
            {
                _lastVelocity = velocity;
                _hasLastVelocity = true;
                return;
            }

            EvaluateCut(velocity);
            _lastVelocity = velocity;
        }

        /// <summary>
        /// Re-resolves the carrier only when possession actually moves.
        ///
        /// Systems_PlayerRegistry.FindById is a linear scan of forty-four slots.
        /// Doing it every frame would be a walk over the registry — precisely the
        /// per-frame pattern this view's header promises not to reintroduce — so it
        /// runs on the handful of frames a game where CarrierId changes.
        /// </summary>
        private void ResolveCarrier()
        {
            // Nobody carries the ball while it is in the air, and the model reports
            // that honestly. Dropping the handle mid-pass is correct: there is no
            // runner to throw turf.
            int id = _ball.IsHeld ? _ball.CarrierId : -1;

            if (id == _carrierId)
            {
                return;
            }

            _carrierId = id;
            _carrier = id < 0 || _registry == null ? null : _registry.FindById(id);
            _hasLastVelocity = false;
        }

        /// <summary>
        /// Decides whether this frame's velocity change was a cut, and how hard.
        ///
        /// Only the component of acceleration PERPENDICULAR to the direction of
        /// travel counts. Accelerating in a straight line is not a cut, and
        /// including it would make the effect fire hardest at the snap, when every
        /// player goes from nothing to full speed in a straight line at once.
        /// </summary>
        private void EvaluateCut(Vector2 velocity)
        {
            float speed = velocity.magnitude;

            if (speed < MIN_CUT_SPEED || _sprayCountdown > 0f)
            {
                return;
            }

            float deltaTime = Time.deltaTime;

            if (deltaTime <= 0f)
            {
                return;
            }

            Vector2 acceleration = (velocity - _lastVelocity) / deltaTime;
            Vector2 heading = velocity / speed;

            // Reject the along-track component; keep what is left.
            Vector2 lateral = acceleration - (heading * Vector2.Dot(acceleration, heading));
            float lateralMagnitude = lateral.magnitude;

            if (lateralMagnitude < CUT_THRESHOLD)
            {
                return;
            }

            float force = Mathf.InverseLerp(CUT_THRESHOLD, CUT_FULL, lateralMagnitude);

            _sprayCountdown = SPRAY_INTERVAL;

            // Thrown backwards, away from where he is now going. Turf leaves the
            // cleat opposite to the direction the foot drove.
            EmitClods(_carrier.Position, -heading, force);
        }

        private void EmitClods(Vector2 point, Vector2 away, float force)
        {
            int count = Mathf.RoundToInt(Mathf.Lerp(MIN_CLODS, MAX_CLODS, force));

            for (int clod = 0; clod < count; clod++)
            {
                // A cone about the backwards direction rather than a full ring: the
                // spray comes off one foot, not out of the ground in all directions.
                float spread = (NextFloat() - 0.5f) * 1.4f;

                Vector2 direction = new Vector2(
                    (away.x * Mathf.Cos(spread)) - (away.y * Mathf.Sin(spread)),
                    (away.x * Mathf.Sin(spread)) + (away.y * Mathf.Cos(spread)));

                float speed = Mathf.Lerp(
                    CLOD_SPEED_MIN, CLOD_SPEED_MAX, force * (0.5f + NextFloat()));

                _emit = default;
                _emit.position = point;
                _emit.velocity = direction * speed;
                _emit.startLifetime = CLOD_LIFETIME * (0.6f + (NextFloat() * 0.7f));
                _emit.startSize = CLOD_SIZE * (0.5f + (force * 0.9f));
                _emit.startColor = ClodColor;

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
