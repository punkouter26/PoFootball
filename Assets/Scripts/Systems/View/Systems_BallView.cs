using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Moves the ball sprite to wherever the ball actually is.
    ///
    /// Before this existed the sprite sat wherever the scene author left it and
    /// never moved, so the yellow dot on screen had nothing to do with possession.
    /// It was also a live Rigidbody2D, making it a loose obstacle that 22 agents
    /// could shove around; the physics components are removed from the prefab.
    ///
    /// Reads the model directly in LateUpdate rather than subscribing, because the
    /// ball moves every tick and a per-change callback would allocate.
    ///
    /// THE ARC IS DRAWN, NOT SIMULATED. Systems_BallModel.Height is a presentation
    /// value (see that class), and this is the only thing that reads it. The sprite
    /// is offset up the screen by the height and a shadow is drawn at the ball's
    /// true plane position, so the gap between the two is what sells "the ball went
    /// over him". Without the shadow a lifted sprite just reads as a ball that
    /// drifted north, because a top-down view has no other depth cue.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Systems_BallView : MonoBehaviour, Systems_IInjectableView
    {
        [SerializeField] private float _zOffset = -1f;

        /// <summary>
        /// Screen-space metres the sprite rises per metre of real height. Below 1
        /// because a straight-up offset in a top-down view reads as distance
        /// upfield, and an honest 1:1 lift makes a deep ball look like it teleported
        /// ten yards downfield before coming back.
        /// </summary>
        [SerializeField] private float _heightToScreen = 0.6f;

        /// <summary>How much the shadow shrinks at the top of the arc.</summary>
        [SerializeField] private float _shadowMinScale = 0.45f;

        /// <summary>Height in metres at which the shadow reaches its smallest.</summary>
        [SerializeField] private float _shadowFadeHeight = 4f;

        private Systems_BallModel _ball;
        private Transform _transform;
        private SpriteRenderer _renderer;

        private Transform _shadowTransform;
        private SpriteRenderer _shadowRenderer;
        private Vector3 _shadowBaseScale;

        [Inject]
        public void Construct(Systems_BallModel ball)
        {
            _ball = ball;
        }

        private void Awake()
        {
            _transform = transform;
            _renderer = GetComponent<SpriteRenderer>();

            BuildShadow();
        }

        /// <summary>
        /// The shadow is created here rather than authored into the scene for the
        /// same reason Systems_RoleShapeApplier paints from code: a second object
        /// somebody has to remember to keep in step with the ball is a thing that
        /// silently stops matching it. It is not a child of this transform — a child
        /// would inherit the height offset and rise with the ball, which is the one
        /// thing it must not do.
        /// </summary>
        private void BuildShadow()
        {
            if (_renderer == null)
            {
                return;
            }

            GameObject shadow = new GameObject("BallShadow");
            _shadowTransform = shadow.transform;
            _shadowTransform.SetParent(_transform.parent, false);

            _shadowRenderer = shadow.AddComponent<SpriteRenderer>();
            _shadowRenderer.sprite = _renderer.sprite;
            _shadowRenderer.sortingLayerID = _renderer.sortingLayerID;

            // Under the players, so a ball passing over a defender shows its shadow
            // crossing him rather than sitting on top of him.
            _shadowRenderer.sortingOrder = _renderer.sortingOrder - 3;
            _shadowRenderer.color = new Color(0f, 0f, 0f, 0.35f);

            _shadowBaseScale = _transform.localScale * 0.8f;
            _shadowTransform.localScale = _shadowBaseScale;
            _shadowRenderer.enabled = false;
        }

        private void LateUpdate()
        {
            if (_ball == null)
            {
                return;
            }

            Vector2 position = _ball.Position;
            float height = _ball.Height;

            _transform.position = new Vector3(
                position.x, position.y + (height * _heightToScreen), _zOffset);

            // A held ball is redundant with the white carrier highlight, so only
            // show the sprite when the ball is genuinely separate from a player.
            bool inFlight = _ball.IsInFlight;

            if (_renderer != null)
            {
                _renderer.enabled = inFlight;
            }

            UpdateShadow(position, height, inFlight);
        }

        /// <summary>
        /// The shadow stays on the turf at the ball's real plane position and
        /// shrinks as the ball climbs. That separation is the depth cue; the scale
        /// change is what stops a high ball and a low one looking identical.
        /// </summary>
        private void UpdateShadow(Vector2 position, float height, bool inFlight)
        {
            if (_shadowTransform == null)
            {
                return;
            }

            _shadowRenderer.enabled = inFlight;

            if (!inFlight)
            {
                return;
            }

            _shadowTransform.position = new Vector3(position.x, position.y, _zOffset + 0.5f);

            float climb = _shadowFadeHeight <= 0f
                ? 1f
                : Mathf.Clamp01(height / _shadowFadeHeight);

            _shadowTransform.localScale =
                _shadowBaseScale * Mathf.Lerp(1f, _shadowMinScale, climb);
        }

        private void OnDestroy()
        {
            // Built here, so destroyed here — it is not a child of this transform
            // and would otherwise outlive the ball it belongs to.
            if (_shadowTransform != null)
            {
                Destroy(_shadowTransform.gameObject);
            }
        }
    }
}
