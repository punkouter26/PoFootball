using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Turns the fixed overhead shot into a camera operator: it follows the ball,
    /// tightens on the snap, and pulls wide when a run breaks.
    ///
    /// WHY NOT CINEMACHINE. The package is installed and PoFootball.Views already
    /// references it, so this was a real choice rather than an omission. Both
    /// behaviours here are bespoke — the zoom is a function of yards gained past
    /// the line of scrimmage, and the framing has to stay inside a 120-yard field
    /// on a 9:16 screen. Wiring that into CinemachineCamera plus a follow component
    /// is more moving parts than the sixty lines of damping it replaces.
    ///
    /// WHAT WAS REMOVED, AND WHY. This used to also kick on a big hit and drop into
    /// slow motion for a touchdown. The slow motion drove Time.timeScale, which is
    /// the one global in the project that a presentation view had any business
    /// touching and the one most likely to be left behind — a scene unload
    /// mid-replay stranded the editor at 0.35. Neither effect told a viewer
    /// anything the whistle and the HUD banner did not already say, so the camera
    /// is now only a camera: it never writes a global and never subscribes to a
    /// gameplay event other than the snap.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public sealed class Systems_BroadcastCameraView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// Orthographic size that frames the full width of the field. At 9:16 the
        /// visible width is 1.125 x the ortho size, and the field is 53.3 yd
        /// across — so anything below this crops the sidelines.
        /// </summary>
        private const float WIDE_SIZE = 46f;

        /// <summary>
        /// Tightest framing. Loses roughly five metres of each sideline, which
        /// still leaves both wide receivers on screen at their widest split.
        /// </summary>
        private const float TIGHT_SIZE = 36f;

        /// <summary>Yards past the line of scrimmage at which the shot is fully wide.</summary>
        private const float FULL_WIDE_YARDS = 22f;

        private const float POSITION_DAMPING = 3.2f;
        private const float ZOOM_DAMPING = 2.4f;

        /// <summary>
        /// How much of the ball's lateral position the camera tracks. Full
        /// tracking makes a receiver running a crossing route swing the whole
        /// field sideways; a third reads as the operator easing over.
        /// </summary>
        private const float LATERAL_TRACKING = 0.35f;

        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;
        private IDisposable _snappedSubscription;

        private Systems_BallModel _ball;
        private Systems_PlayModel _play;
        private Systems_PresentationBudget _budget;

        private Camera _camera;
        private Transform _transform;

        private float _homeZ;

        [Inject]
        public void Construct(
            Systems_BallModel ball,
            Systems_PlayModel play,
            Systems_PresentationBudget budget,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
            _ball = ball;
            _play = play;
            _budget = budget;
            _snappedSubscriber = snappedSubscriber;
        }

        private void Awake()
        {
            _camera = GetComponent<Camera>();
            _transform = transform;
            _homeZ = _transform.position.z;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled || _ball == null)
            {
                enabled = false;
                return;
            }

            _snappedSubscription = _snappedSubscriber?.Subscribe(OnSnapped);
        }

        private void OnDestroy()
        {
            _snappedSubscription?.Dispose();
        }

        /// <summary>
        /// LateUpdate so the camera reads positions the physics step has already
        /// settled.
        /// </summary>
        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;

            Vector2 target = FramingTarget();
            float targetSize = FramingSize();

            // Exponential smoothing rather than Lerp with a raw t. Lerp against
            // deltaTime is frame-rate dependent, and this game runs at 60 in the
            // editor and whatever the build gets.
            float positionBlend = 1f - Mathf.Exp(-POSITION_DAMPING * deltaTime);
            float zoomBlend = 1f - Mathf.Exp(-ZOOM_DAMPING * deltaTime);

            _camera.orthographicSize = Mathf.Lerp(
                _camera.orthographicSize, targetSize, zoomBlend);

            Vector3 current = _transform.position;
            Vector2 eased = Vector2.Lerp(new Vector2(current.x, current.y), target, positionBlend);

            eased = ClampToField(eased, _camera.orthographicSize);

            _transform.position = new Vector3(eased.x, eased.y, _homeZ);
        }

        /// <summary>
        /// Where the operator wants the shot centred. Vertically the ball, laterally
        /// only a fraction of it so the field does not swing about.
        /// </summary>
        private Vector2 FramingTarget()
        {
            Vector2 ball = _ball.Position;
            return new Vector2(ball.x * LATERAL_TRACKING, ball.y);
        }

        /// <summary>
        /// Tight before and at the snap, opening up as the play gets away from the
        /// line of scrimmage. A pass in the air goes wide immediately — the throw
        /// is the moment the viewer most needs to see both ends of it.
        /// </summary>
        private float FramingSize()
        {
            if (_play == null)
            {
                return WIDE_SIZE;
            }

            if (_ball.IsInFlight)
            {
                return WIDE_SIZE;
            }

            float yardsFromScrimmage =
                Mathf.Abs(_ball.Position.y - _play.LineOfScrimmageY) / Systems_FieldModel.YARD;

            float openness = Mathf.Clamp01(yardsFromScrimmage / FULL_WIDE_YARDS);
            return Mathf.Lerp(TIGHT_SIZE, WIDE_SIZE, openness);
        }

        /// <summary>
        /// Keeps the visible rectangle inside the field plus a small apron. Without
        /// this, following the ball into the end zone shows a band of empty
        /// background above the back line, which reads as the camera falling off
        /// the world.
        /// </summary>
        private Vector2 ClampToField(Vector2 position, float orthographicSize)
        {
            const float APRON = 4f;

            float halfHeight = orthographicSize;
            float halfWidth = orthographicSize * _camera.aspect;

            float limitY = Systems_FieldModel.ATTACKING_BACK_LINE_Y + APRON - halfHeight;
            float limitX = Systems_FieldModel.HALF_WIDTH + APRON - halfWidth;

            // A negative limit means the view is already wider than the field, so
            // the only correct position on that axis is dead centre.
            position.y = limitY <= 0f ? 0f : Mathf.Clamp(position.y, -limitY, limitY);
            position.x = limitX <= 0f ? 0f : Mathf.Clamp(position.x, -limitX, limitX);

            return position;
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            // Snap the shot straight to the new line of scrimmage. Damping across
            // a spot change would send the camera sailing sixty yards down the
            // field between plays.
            _camera.orthographicSize = TIGHT_SIZE;

            Vector2 spot = ClampToField(
                new Vector2(0f, message.LineOfScrimmageY), TIGHT_SIZE);

            _transform.position = new Vector3(spot.x, spot.y, _homeZ);
        }
    }
}
