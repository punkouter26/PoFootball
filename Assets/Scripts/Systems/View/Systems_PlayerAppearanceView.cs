using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Puts PoFootball/Player on all twenty-two bodies and feeds it the three
    /// pieces of per-player state that change during a play: who has the ball, how
    /// tired each player is, and how hard each one is running.
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

        [Tooltip("Leave empty to load M_PoFootballPlayer from Resources.")]
        [SerializeField] private Material _playerMaterial;

        private Systems_PresentationBudget _budget;

        private Systems_IPlayerHandle[] _handles;
        private SpriteRenderer[] _renderers;
        private float[] _carrierAmount;
        private float[] _fatigueAmount;
        private float[] _leanAmount;
        private int _count;

        private MaterialPropertyBlock _properties;

        [Inject]
        public void Construct(Systems_PresentationBudget budget)
        {
            _budget = budget;
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

                renderer.GetPropertyBlock(_properties);
                _properties.SetFloat(CarrierId, _carrierAmount[index]);
                _properties.SetFloat(FatigueId, _fatigueAmount[index]);
                _properties.SetFloat(LeanId, _leanAmount[index]);
                renderer.SetPropertyBlock(_properties);
            }
        }
    }
}
