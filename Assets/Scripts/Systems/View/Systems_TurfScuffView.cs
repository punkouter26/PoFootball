using System;
using MessagePipe;
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
    ///
    /// AND THE GROUND REMEMBERS (2026-10-04). The spray is gone in half a second,
    /// and the turf shader's own wear is one soft patch that follows the line of
    /// scrimmage, so nothing on the field said where the game had actually been
    /// played. The wear layer below is a field-sized texture drawn once over the
    /// grass, in bare-earth sand: every player who is running leaves a footprint
    /// in it five times a second, a cut leaves a scuff and a tackle a worn patch,
    /// and none of it ever fades. By the fourth quarter the hash marks and the
    /// lines of scrimmage are visibly churned and the corners are still green.
    ///
    /// THIS ONE DOES WALK ALL TWENTY-TWO, on a timer rather than a frame: 110
    /// position reads a second and a few byte writes each, against a texture that
    /// is uploaded only on the ticks something was written. It is one quad and one
    /// draw call however worn the field gets.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class Systems_TurfScuffView : MonoBehaviour, Systems_IInjectableView
    {
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

        // --- The wear layer ------------------------------------------------------

        /// <summary>
        /// Metres of field per texel. A quarter of a metre is about one boot, so a
        /// single texel reads as a footprint; the whole field is 195 x 439.
        /// </summary>
        private const float WEAR_TEXEL_METRES = 0.25f;

        /// <summary>
        /// Seconds between footprints. At a run that is a stride and a half, which
        /// is what makes a trail read as prints and not a painted line.
        /// </summary>
        private const float FOOTPRINT_INTERVAL = 0.2f;

        /// <summary>Slower than this a player is standing or shuffling and marks nothing.</summary>
        private const float FOOTPRINT_MIN_SPEED = 1.5f;

        /// <summary>How far either foot lands from the body's line of travel.</summary>
        private const float FOOTPRINT_HALF_STANCE = 0.22f;

        /// <summary>Opacity one footprint adds. Several passes over a spot wear it bare.</summary>
        private const byte FOOTPRINT_ALPHA = 46;

        private const float CUT_SCUFF_RADIUS = 0.7f;
        private const byte CUT_SCUFF_ALPHA = 90;

        private const float TACKLE_SCUFF_RADIUS = 1.4f;
        private const byte TACKLE_SCUFF_ALPHA = 150;

        /// <summary>The grass never disappears entirely, so the yard lines stay readable.</summary>
        private const byte WEAR_MAX_ALPHA = 205;

        /// <summary>Bare sandy earth. Light against the green on purpose: it has to read from the broadcast camera.</summary>
        private static readonly Color32 DirtColor = new Color32(196, 168, 118, 0);

        private Texture2D _wearTexture;
        private Sprite _wearSprite;
        private Color32[] _wearPixels;
        private int _wearWidth;
        private int _wearHeight;
        private bool _wearDirty;
        private float _footprintCountdown;

        /// <summary>Which foot each player lands next. One bit per formation slot.</summary>
        private uint _leftFootNext;

        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private IDisposable _tackleSubscription;

        private Systems_BallModel _ball;
        private Systems_PlayerRegistry _registry;
        private Systems_PresentationBudget _budget;

        /// <summary>
        /// Tells Systems_AudioView a cut happened. Published from the spray itself,
        /// so the sound inherits SPRAY_INTERVAL and this view's one-player watch —
        /// the audio view never has to look for a cut on its own.
        /// </summary>
        private IPublisher<Systems_TurfCutMessage> _cutPublisher;

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
            Systems_PresentationBudget budget,
            IPublisher<Systems_TurfCutMessage> cutPublisher,
            ISubscriber<Systems_TackleMessage> tackleSubscriber)
        {
            _tackleSubscriber = tackleSubscriber;
            _ball = ball;
            _registry = registry;
            _budget = budget;
            _cutPublisher = cutPublisher;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled || _ball == null)
            {
                enabled = false;
                return;
            }

            // Before the particle material is asked for: the wear layer does not
            // need it, and a missing material should cost the spray, not the field.
            BuildWearLayer();
            _tackleSubscription = _tackleSubscriber?.Subscribe(OnTackle);

            if (!Systems_ParticleMaterial.TryLoad(
                    nameof(Systems_TurfScuffView), "Cuts are drawn flat.", out Material source))
            {
                return;
            }

            BuildParticles(source);
        }

        private void OnDestroy()
        {
            _tackleSubscription?.Dispose();
            _tackleSubscription = null;

            // Runtime-created assets are not collected with the scene.
            if (_wearSprite != null)
            {
                Destroy(_wearSprite);
            }

            if (_wearTexture != null)
            {
                Destroy(_wearTexture);
            }
        }

        /// <summary>
        /// One transparent, field-sized sprite over the grass. Sorting order 0 with
        /// the turf, a hair nearer the camera so it draws after it — and so under
        /// the players (1 and 2), the ball and every other effect.
        /// </summary>
        private void BuildWearLayer()
        {
            _wearWidth = Mathf.CeilToInt(Systems_FieldModel.FIELD_WIDTH / WEAR_TEXEL_METRES);
            _wearHeight = Mathf.CeilToInt(Systems_FieldModel.TOTAL_LENGTH / WEAR_TEXEL_METRES);

            _wearPixels = new Color32[_wearWidth * _wearHeight];

            for (int texel = 0; texel < _wearPixels.Length; texel++)
            {
                _wearPixels[texel] = DirtColor;
            }

            _wearTexture = new Texture2D(
                _wearWidth, _wearHeight, TextureFormat.RGBA32, mipChain: false)
            {
                name = "TurfWear",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            _wearTexture.SetPixels32(_wearPixels);
            _wearTexture.Apply(false);

            _wearSprite = Sprite.Create(
                _wearTexture,
                new Rect(0f, 0f, _wearWidth, _wearHeight),
                new Vector2(0.5f, 0.5f),
                1f / WEAR_TEXEL_METRES);

            GameObject host = new GameObject("TurfWear");
            host.transform.SetParent(transform, false);
            host.transform.position = new Vector3(0f, 0f, -0.01f);

            // The texture is a whole number of texels and the field is not, so the
            // quad is squeezed the last few centimetres onto the field exactly.
            host.transform.localScale = new Vector3(
                Systems_FieldModel.FIELD_WIDTH / (_wearWidth * WEAR_TEXEL_METRES),
                Systems_FieldModel.TOTAL_LENGTH / (_wearHeight * WEAR_TEXEL_METRES),
                1f);

            SpriteRenderer renderer = host.AddComponent<SpriteRenderer>();
            renderer.sprite = _wearSprite;
            renderer.sortingOrder = 0;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>
        /// Published from inside the collision that made the tackle, so this only
        /// writes bytes; the upload waits for the next footprint tick.
        /// </summary>
        private void OnTackle(Systems_TackleMessage message)
        {
            if (_wearPixels == null || _ball == null)
            {
                return;
            }

            StampScuff(_ball.Position, TACKLE_SCUFF_RADIUS, TACKLE_SCUFF_ALPHA);
        }

        /// <summary>
        /// One footprint per moving player, alternating feet either side of his
        /// line of travel, then the upload if anything changed since the last one.
        /// </summary>
        private void TickWear()
        {
            if (_wearPixels == null)
            {
                return;
            }

            _footprintCountdown -= Time.deltaTime;

            if (_footprintCountdown > 0f)
            {
                return;
            }

            _footprintCountdown = FOOTPRINT_INTERVAL;

            if (_registry != null)
            {
                float minimumSquared = FOOTPRINT_MIN_SPEED * FOOTPRINT_MIN_SPEED;

                for (int slot = 0; slot < Systems_PlayerRegistry.CAPACITY; slot++)
                {
                    Systems_IPlayerHandle player = _registry.Get(slot);

                    if (player == null)
                    {
                        continue;
                    }

                    Vector2 velocity = player.Velocity;
                    float speedSquared = velocity.sqrMagnitude;

                    if (speedSquared < minimumSquared)
                    {
                        continue;
                    }

                    uint bit = 1u << slot;
                    float side = (_leftFootNext & bit) != 0u ? -1f : 1f;
                    _leftFootNext ^= bit;

                    Vector2 heading = velocity / Mathf.Sqrt(speedSquared);
                    Vector2 foot = player.Position
                        + (new Vector2(-heading.y, heading.x) * (FOOTPRINT_HALF_STANCE * side));

                    AddWear(foot, FOOTPRINT_ALPHA);
                }
            }

            if (_wearDirty)
            {
                _wearDirty = false;
                _wearTexture.SetPixels32(_wearPixels);
                _wearTexture.Apply(false);
            }
        }

        /// <summary>
        /// A ragged round patch: strongest in the middle, thinned by the private
        /// RNG so no two scuffs are the same disc.
        /// </summary>
        private void StampScuff(Vector2 centre, float radius, byte alpha)
        {
            int reach = Mathf.CeilToInt(radius / WEAR_TEXEL_METRES);

            for (int offsetY = -reach; offsetY <= reach; offsetY++)
            {
                for (int offsetX = -reach; offsetX <= reach; offsetX++)
                {
                    Vector2 offset = new Vector2(offsetX, offsetY) * WEAR_TEXEL_METRES;
                    float falloff = 1f - (offset.magnitude / radius);

                    if (falloff <= 0f)
                    {
                        continue;
                    }

                    AddWear(
                        centre + offset,
                        (byte)(alpha * falloff * (0.45f + (0.55f * NextFloat()))));
                }
            }
        }

        /// <summary>Wears one texel a little barer. Off the field marks nothing.</summary>
        private void AddWear(Vector2 worldPoint, byte alpha)
        {
            int texelX = Mathf.FloorToInt(
                ((worldPoint.x + Systems_FieldModel.HALF_WIDTH) / Systems_FieldModel.FIELD_WIDTH)
                * _wearWidth);

            int texelY = Mathf.FloorToInt(
                ((worldPoint.y - Systems_FieldModel.OWN_BACK_LINE_Y) / Systems_FieldModel.TOTAL_LENGTH)
                * _wearHeight);

            if (texelX < 0 || texelX >= _wearWidth || texelY < 0 || texelY >= _wearHeight)
            {
                return;
            }

            int index = (texelY * _wearWidth) + texelX;
            int worn = _wearPixels[index].a + alpha;

            _wearPixels[index].a = (byte)(worn > WEAR_MAX_ALPHA ? WEAR_MAX_ALPHA : worn);
            _wearDirty = true;
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
            TickWear();

            if (_particles == null)
            {
                return;
            }

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
            StampScuff(_carrier.Position, CUT_SCUFF_RADIUS, (byte)(CUT_SCUFF_ALPHA * force));

            _cutPublisher?.Publish(new Systems_TurfCutMessage(_carrier.Position, force));
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
