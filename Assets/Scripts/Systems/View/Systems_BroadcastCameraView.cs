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
        /// Widest framing, in metres of half-height. Shows about 65 yards of field.
        ///
        /// WAS 36, AND BEFORE THAT 46. 36 was still far too wide to WATCH. At the
        /// 9:16 the panel is designed for, half-width is only 0.466 x the ortho
        /// size, so 36 put 78 yards of field down a 6-inch screen and every player
        /// on it rendered about ten pixels across — the play read as a cluster of
        /// dots drifting on green, and the ball was essentially invisible. Framing
        /// is not a correctness bug so nothing failed; it just was not worth
        /// looking at, which for a game whose whole proposition is "watch this" is
        /// the more serious failure.
        ///
        /// The floor on both sizes is the FORMATION, not the field. Systems_Formation
        /// splits its widest receivers to about x = +/-11 m, so the tight shot has to
        /// cover 22 m of width to keep a snap whole: 22 / (2 x 0.466) = 23.6, which
        /// is where TIGHT_SIZE 22 sits once the lateral tracking below is allowed to
        /// carry the rest. Anything tighter starts cropping receivers off the snap.
        ///
        /// AND IT WAS 46 BEFORE THAT, WHICH IS WHERE THE ORIGINAL BUG LIVED. A
        /// portrait screen
        /// cannot show the full 53.3 yd width without also showing most of the
        /// length — at 9:16 the visible width is only 1.125 x the ortho size, so
        /// covering the sidelines needs an ortho size around 43, which makes the
        /// visible rectangle 86 m tall against a 109.7 m field. Once the view is
        /// that large, ClampToField has almost no room left to move the camera in,
        /// and the shot stops being able to follow the ball at all. Framing the
        /// sidelines was never worth that: both receivers are inside x = ±11 m at
        /// their widest split, and nothing important happens outside them.
        /// </summary>
        private const float WIDE_SIZE = 30f;

        /// <summary>
        /// Tightest framing — about 48 yards of field, down from 61. Crops the outer
        /// few metres of each sideline, which is empty grass at every formation this
        /// game lines up. See WIDE_SIZE for why it stops here and not tighter.
        /// </summary>
        private const float TIGHT_SIZE = 22f;

        /// <summary>Yards past the line of scrimmage at which the shot is fully wide.</summary>
        private const float FULL_WIDE_YARDS = 22f;

        private const float POSITION_DAMPING = 3.2f;
        private const float ZOOM_DAMPING = 2.4f;

        /// <summary>
        /// How much of the ball's lateral position the camera tracks. Full tracking
        /// makes a receiver running a crossing route swing the whole field sideways;
        /// a third reads as the operator easing over.
        ///
        /// RAISED FROM 0.35 WITH THE ZOOM. The two numbers trade against each other:
        /// a tighter shot shows less width, so it has to follow the ball further
        /// across to keep it in frame. At 0.55 the carrier stays comfortably inside
        /// the tighter rectangle without the field reading as though it is on rails.
        /// </summary>
        private const float LATERAL_TRACKING = 0.55f;

        /// <summary>
        /// How far ahead of the ball the shot sits, as a fraction of the half-height
        /// currently being framed.
        ///
        /// A camera centred exactly on the ball spends half its screen on the ground
        /// the play has already left. Every operator covering football leads the
        /// ball instead, so the room is in front of the carrier where the play is
        /// about to happen — you want to see the defender he is running at, not the
        /// one he beat.
        ///
        /// WHICH WAY IS "AHEAD" IS FREE HERE, AND THAT IS NOT LUCK. The offense
        /// always attacks +Y — Systems_GameFlowSystem mirrors field position through
        /// y -> -y on every change of possession precisely so that stays true — so
        /// downfield is +Y on every snap of every drive for both teams, and this
        /// needs no knowledge of who has the ball.
        ///
        /// Scaled by the live ortho size rather than fixed in metres, so the lead
        /// grows as the shot opens up on a breaking run and stays modest at the snap.
        /// </summary>
        private const float DOWNFIELD_LEAD = 0.22f;

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

            float lead = _camera.orthographicSize * DOWNFIELD_LEAD;

            return new Vector2(ball.x * LATERAL_TRACKING, ball.y + lead);
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
        /// Keeps the visible rectangle inside the field plus a small apron, without
        /// ever refusing to follow the ball.
        ///
        /// WHAT WAS WRONG. The vertical limit was the apron rule alone:
        /// ATTACKING_BACK_LINE_Y + APRON - halfHeight. The field is 54.9 m from the
        /// centre to a back line and the view was 36-46 m tall in half-height, so
        /// that left the camera only ±22.9 m of travel at its tightest and ±12.9 m
        /// at its widest — barely the middle third of a 109.7 m field. Three things
        /// followed, all of them visible on every game:
        ///
        ///   The shot could not centre on a snap outside the 25s. The play sat in
        ///   the bottom fifth of the screen with forty yards of empty grass above
        ///   it, which is exactly backwards.
        ///
        ///   Going wide TIGHTENED the limit, because the limit is a function of
        ///   halfHeight. So the camera was dragged back toward midfield at the
        ///   moment it zoomed out — which is to say, on every pass in flight and
        ///   every run that broke. The one moment the shot most needs to follow the
        ///   ball is the one moment it was pulled off it.
        ///
        ///   Backed up near its own goal, the quarterback fell off the bottom of
        ///   the frame entirely.
        ///
        /// THE FIX. On a portrait screen the view is necessarily a large fraction of
        /// the field's length, so "never show grass beyond the back line" and
        /// "always follow the ball" cannot both hold near a goal line. Following the
        /// ball wins: the limit is now whichever of the two rules is MORE permissive,
        /// so the apron governs in the middle of the field where it costs nothing —
        /// no void is visible inside roughly the 16 yard lines — and gives way near
        /// the goal lines, which is where the football that matters happens.
        /// </summary>
        private Vector2 ClampToField(Vector2 position, float orthographicSize)
        {
            const float APRON = 4f;

            float halfHeight = orthographicSize;
            float halfWidth = orthographicSize * _camera.aspect;

            // Rule one: do not show more than the apron beyond a back line.
            float voidLimitY = Systems_FieldModel.ATTACKING_BACK_LINE_Y + APRON - halfHeight;

            // Rule two: the shot must always be able to reach a goal line, because
            // the ball can be spotted anywhere between them.
            float limitY = Mathf.Max(voidLimitY, Systems_FieldModel.ATTACKING_GOAL_LINE_Y);

            float limitX = Systems_FieldModel.HALF_WIDTH + APRON - halfWidth;

            position.y = Mathf.Clamp(position.y, -limitY, limitY);

            // Laterally the view really is wider than the field at these sizes, and
            // there the only correct position is dead centre.
            position.x = limitX <= 0f ? 0f : Mathf.Clamp(position.x, -limitX, limitX);

            return position;
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            // Snap the shot straight to the new line of scrimmage. Damping across
            // a spot change would send the camera sailing sixty yards down the
            // field between plays.
            _camera.orthographicSize = TIGHT_SIZE;

            // Same downfield lead the steady-state framing uses, or the shot would
            // cut to the line of scrimmage and then immediately drift upfield by the
            // lead on the next frame.
            Vector2 spot = ClampToField(
                new Vector2(0f, message.LineOfScrimmageY + (TIGHT_SIZE * DOWNFIELD_LEAD)),
                TIGHT_SIZE);

            _transform.position = new Vector3(spot.x, spot.y, _homeZ);
        }
    }
}
