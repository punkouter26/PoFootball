using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The streak behind a thrown ball.
    ///
    /// A pass in this simulation is a straight line at constant speed with no arc
    /// to read — Systems_BallModel is explicit that there is no vertical axis in a
    /// top-down view. Without a trail the ball is a small sprite crossing the
    /// screen in well under a second, and on a busy frame the eye simply loses it
    /// between the release and the catch. The trail is what makes a throw legible
    /// as a throw.
    ///
    /// Lives beside Systems_BallView rather than inside it. That view is in
    /// SCN_TRAIN_FOOTBALL too and has to stay a transform write and nothing else;
    /// this one is decoration and is gated on Systems_PresentationBudget.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class Systems_BallTrailView : MonoBehaviour, Systems_IInjectableView
    {
        private const string PARTICLE_MATERIAL_RESOURCE = "M_PoFootballParticle";

        [SerializeField] private Color _headColor = new Color(1f, 0.945f, 0.741f, 0.95f);
        [SerializeField] private Color _tailColor = new Color(0.976f, 0.796f, 0.290f, 0f);

        [Range(0.05f, 1f)]
        [SerializeField] private float _trailSeconds = 0.28f;

        [Range(0.05f, 2f)]
        [SerializeField] private float _headWidth = 0.55f;

        private Systems_BallModel _ball;
        private Systems_PresentationBudget _budget;

        private TrailRenderer _trail;
        private Material _trailMaterial;

        [Inject]
        public void Construct(Systems_BallModel ball, Systems_PresentationBudget budget)
        {
            _ball = ball;
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
                    $"{nameof(Systems_BallTrailView)}: no material at "
                    + $"Resources/{PARTICLE_MATERIAL_RESOURCE}. The ball flies untrailed.");
                enabled = false;
                return;
            }

            _trailMaterial = new Material(source);
            BuildTrail();
        }

        private void OnDestroy()
        {
            if (_trailMaterial != null)
            {
                Destroy(_trailMaterial);
            }
        }

        private void BuildTrail()
        {
            _trail = gameObject.AddComponent<TrailRenderer>();
            _trail.material = _trailMaterial;
            _trail.time = _trailSeconds;

            // Emitting=false by default. The ball transform is moved every frame
            // by Systems_BallView whether or not the ball is live, so a trail left
            // switched on would draw a streak following the carrier around.
            _trail.emitting = false;
            _trail.autodestruct = false;

            _trail.widthCurve = new AnimationCurve(
                new Keyframe(0f, 1f), new Keyframe(1f, 0.05f));
            _trail.widthMultiplier = _headWidth;

            // Straight-line flight means very few corners, so the trail needs
            // almost no subdivision — two points per segment is plenty and keeps
            // the vertex count flat.
            _trail.minVertexDistance = 0.25f;
            _trail.numCapVertices = 4;

            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(_headColor, 0f),
                    new GradientColorKey(_tailColor, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(_headColor.a, 0f),
                    new GradientAlphaKey(0f, 1f)
                });

            _trail.colorGradient = gradient;

            // Above the players, below the confetti.
            _trail.sortingOrder = 4;
        }

        private void LateUpdate()
        {
            if (_trail == null)
            {
                return;
            }

            bool shouldEmit = _ball.IsInFlight;

            if (shouldEmit == _trail.emitting)
            {
                return;
            }

            _trail.emitting = shouldEmit;

            // Clear on the rising edge, not the falling one. The ball teleports
            // from wherever the last play died to the new line of scrimmage; if
            // the old trail were still in the buffer, the first frame of the next
            // throw would draw a line straight across the field.
            if (shouldEmit)
            {
                _trail.Clear();
            }
        }
    }
}
