using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Puts PoFootball/Player on all twenty-two bodies and feeds it the four
    /// pieces of per-player state that change during a play: who has the ball, how
    /// tired each player is, how hard each one is running, and who was just hit.
    ///
    /// THE HIT FLASH LIVES HERE RATHER THAN IN Systems_ImpactView, which is where
    /// it looks like it belongs. Two components writing MaterialPropertyBlocks to
    /// the same renderer fight: GetPropertyBlock / SetPropertyBlock reads and
    /// writes the whole block, so whichever ran second would erase the carrier
    /// glow, the fatigue tint or the lean depending on frame order. One writer per
    /// renderer is not a style preference, it is the only correct arrangement, and
    /// this class was already it. The burst at the contact point — which touches
    /// no player renderer — stays in Systems_ImpactView.
    ///
    /// ONE SWEEPER, NOT TWENTY-TWO COMPONENTS — the same argument
    /// Systems_RoleShapeApplier makes. Twenty-two identical components would be
    /// twenty-two things a scene edit can desynchronise, and this needs a per-frame
    /// loop anyway, so an array scanned with a plain for is strictly better than
    /// twenty-two Update calls.
    ///
    /// RUNS AFTER THE SHAPE APPLIER. That component assigns each player's sprite
    /// and team tint at execution order -100. The material swap here must land
    /// after the sprite exists, because the shader derives its outline from the
    /// sprite's alpha and would otherwise spend the first frame beveling whatever
    /// placeholder the scene shipped with.
    ///
    /// WHY THE COLLIDER IS NEVER TOUCHED. The obvious way to make a sprinting
    /// player read as sprinting is to squash the transform. That transform also
    /// owns the CircleCollider2D the tackle rule is calibrated against, so scaling
    /// it would change the contact radius mid-play and quietly invalidate every
    /// trained brain. The narrowing is a vertex offset inside the shader instead
    /// (_Lean), which the physics engine never sees.
    /// </summary>
    [DefaultExecutionOrder(-90)]
    [DisallowMultipleComponent]
    public sealed class Systems_PlayerAppearanceView : MonoBehaviour, Systems_IInjectableView
    {
        private const string PLAYER_MATERIAL_RESOURCE = "M_PoFootballPlayer";

        private static readonly int CarrierId = Shader.PropertyToID("_Carrier");
        private static readonly int FatigueId = Shader.PropertyToID("_Fatigue");
        private static readonly int LeanId = Shader.PropertyToID("_Lean");
        private static readonly int ImpactId = Shader.PropertyToID("_Impact");

        /// <summary>
        /// Maximum body narrowing at top speed. Kept small deliberately: this is a
        /// cue, and a shape that visibly deforms stops reading as the position it
        /// is supposed to encode.
        /// </summary>
        private const float MAX_LEAN = 0.22f;

        /// <summary>
        /// How fast the on-screen state chases the simulation state, per second.
        /// Fatigue and lean are both stepped toward their target rather than
        /// assigned, because the underlying values move per physics tick and a
        /// direct write makes the fill shimmer at 50 Hz.
        /// </summary>
        private const float STATE_CHASE_RATE = 6f;

        private const float CARRIER_CHASE_RATE = 9f;

        /// <summary>
        /// How fast the hit flash falls off, in flash units per second.
        ///
        /// NOT CHASED LIKE THE OTHERS. Fatigue and lean track a value the
        /// simulation is continuously publishing; a hit is an instant. It is set
        /// hard on arrival and decays linearly, which is the shape an impact has —
        /// an exponential chase toward zero would give a bright hit a long dim tail
        /// and leave the field faintly glowing through a whole drive.
        ///
        /// 5.5 puts a maximum-force flash at roughly a fifth of a second, which is
        /// about how long the impact sound rings for.
        /// </summary>
        private const float IMPACT_DECAY_RATE = 5.5f;

        [Tooltip("Leave empty to load M_PoFootballPlayer from Resources.")]
        [SerializeField] private Material _playerMaterial;

        private Systems_PresentationBudget _budget;
        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;
        private IDisposable _tackleSubscription;

        private Systems_IPlayerHandle[] _handles;
        private SpriteRenderer[] _renderers;
        private float[] _carrierAmount;
        private float[] _fatigueAmount;
        private float[] _leanAmount;
        private float[] _impactAmount;
        private int _count;

        private MaterialPropertyBlock _properties;

        [Inject]
        public void Construct(
            Systems_PresentationBudget budget,
            ISubscriber<Systems_TackleMessage> tackleSubscriber)
        {
            _budget = budget;
            _tackleSubscriber = tackleSubscriber;
        }

        private void Start()
        {
            // Start rather than Awake: the shape applier owns Awake, and injection
            // has not run by then either.
            if (_budget == null || !_budget.EffectsEnabled)
            {
                enabled = false;
                return;
            }

            if (_playerMaterial == null)
            {
                _playerMaterial = Resources.Load<Material>(PLAYER_MATERIAL_RESOURCE);
            }

            if (_playerMaterial == null)
            {
                Debug.LogError(
                    $"{nameof(Systems_PlayerAppearanceView)}: no player material assigned and "
                    + $"none at Resources/{PLAYER_MATERIAL_RESOURCE}. Players keep the default "
                    + "sprite shader.");
                enabled = false;
                return;
            }

            Collect();

            // After Collect, so a tackle published on the very first frame lands in
            // arrays that exist. Collect can also disable this component, in which
            // case there is nothing to flash and no reason to hold a subscription.
            if (enabled && _tackleSubscriber != null)
            {
                _tackleSubscription = _tackleSubscriber.Subscribe(OnTackle);
            }
        }

        private void OnDestroy()
        {
            _tackleSubscription?.Dispose();
            _tackleSubscription = null;
        }

        /// <summary>
        /// Lights the two bodies that just collided, at the strength the collision
        /// actually had.
        ///
        /// Normalized over exactly the range Systems_AudioView.OnTackle and
        /// Systems_ImpactView use — TACKLE_CLOSING_SPEED to MAX_BODY_SPEED — so the
        /// flash, the burst and the crack are three renderings of one number rather
        /// than three independently tuned effects that drift apart.
        ///
        /// Mathf.Max rather than assignment: a carrier taken down by three
        /// defenders inside a few ticks should keep the hardest hit, not whichever
        /// arrived last.
        /// </summary>
        private void OnTackle(Systems_TackleMessage message)
        {
            float force = Mathf.InverseLerp(
                Systems_SimConstants.TACKLE_CLOSING_SPEED,
                Systems_SimConstants.MAX_BODY_SPEED,
                message.ClosingSpeed);

            // A glancing hit still reads, or the effect would only ever appear on
            // the collisions that were already obvious.
            float flash = Mathf.Clamp01(0.35f + (0.65f * force));

            for (int index = 0; index < _count; index++)
            {
                int id = _handles[index].Id;

                if (id == message.CarrierId || id == message.TacklerId)
                {
                    _impactAmount[index] = Mathf.Max(_impactAmount[index], flash);
                }
            }
        }

        private void Collect()
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include);

            int capacity = Systems_PlayerRegistry.CAPACITY;

            _handles = new Systems_IPlayerHandle[capacity];
            _renderers = new SpriteRenderer[capacity];
            _carrierAmount = new float[capacity];
            _fatigueAmount = new float[capacity];
            _leanAmount = new float[capacity];
            _impactAmount = new float[capacity];
            _properties = new MaterialPropertyBlock();

            for (int index = 0; index < behaviours.Length && _count < capacity; index++)
            {
                if (!(behaviours[index] is Systems_IPlayerHandle handle))
                {
                    continue;
                }

                if (!behaviours[index].TryGetComponent(out SpriteRenderer renderer))
                {
                    continue;
                }

                renderer.sharedMaterial = _playerMaterial;

                _handles[_count] = handle;
                _renderers[_count] = renderer;
                _count++;
            }

            if (_count == 0)
            {
                enabled = false;
                Debug.LogWarning(
                    $"{nameof(Systems_PlayerAppearanceView)}: found no players to shade.");
                return;
            }

            Debug.Log($"[PoFootball] Player shader applied to {_count} players.");
        }

        /// <summary>
        /// LateUpdate, not Update: the rigidbody has settled for the frame and the
        /// carrier flag has been latched by whichever agent caught the ball, so the
        /// values written here are the ones that will actually be rendered.
        ///
        /// Allocates nothing. One shared MaterialPropertyBlock is refilled per
        /// player rather than one being built per player per frame.
        /// </summary>
        private void LateUpdate()
        {
            float chase = 1f - Mathf.Exp(-STATE_CHASE_RATE * Time.deltaTime);
            float carrierChase = 1f - Mathf.Exp(-CARRIER_CHASE_RATE * Time.deltaTime);
            float impactDecay = IMPACT_DECAY_RATE * Time.deltaTime;

            for (int index = 0; index < _count; index++)
            {
                Systems_IPlayerHandle handle = _handles[index];
                SpriteRenderer renderer = _renderers[index];

                if (renderer == null)
                {
                    continue;
                }

                float carrierTarget = handle.IsCarrier ? 1f : 0f;
                _carrierAmount[index] = Mathf.Lerp(
                    _carrierAmount[index], carrierTarget, carrierChase);

                _fatigueAmount[index] = Mathf.Lerp(
                    _fatigueAmount[index], Mathf.Clamp01(handle.Fatigue), chase);

                float speed = handle.Velocity.magnitude;
                float leanTarget = Mathf.InverseLerp(
                    2f, Systems_SimConstants.MAX_BODY_SPEED, speed) * MAX_LEAN;
                _leanAmount[index] = Mathf.Lerp(_leanAmount[index], leanTarget, chase);

                _impactAmount[index] = Mathf.Max(
                    0f, _impactAmount[index] - impactDecay);

                renderer.GetPropertyBlock(_properties);
                _properties.SetFloat(CarrierId, _carrierAmount[index]);
                _properties.SetFloat(FatigueId, _fatigueAmount[index]);
                _properties.SetFloat(LeanId, _leanAmount[index]);
                _properties.SetFloat(ImpactId, _impactAmount[index]);
                renderer.SetPropertyBlock(_properties);
            }
        }
    }
}
