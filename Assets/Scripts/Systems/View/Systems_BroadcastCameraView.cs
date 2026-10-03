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
    /// WHY NOT CINEMACHINE. The package was installed, so this was a real choice
    /// rather than an omission — and since nothing ever referenced it, it has
    /// since been removed from the manifest. Both behaviours here are bespoke — the zoom is a function of yards gained past
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
    /// is now only a camera: it never writes a global, and the two gameplay events
    /// it subscribes to — the snap and the release of a pass — only ever move the
    /// shot.
    ///
    /// WHAT IT FRAMES, WHICH IS NOT JUST THE BALL ANY MORE. An operator covering
    /// football is not following the ball, he is following the CONTEST: the runner
    /// and the man about to hit him, the pass and the man it is going to. A shot
    /// centred on the ball shows the second half of each of those a moment after
    /// it has stopped mattering. Three things are layered on the ball-follow, and
    /// each is a separate small method so any one can be judged on its own:
    ///
    ///   A carrier running sideways is led sideways (<see cref="LateralLead"/>).
    ///
    ///   Defenders closing on the carrier pull the shot toward them and, if one
    ///   would still be out of frame, open it up (<see cref="ThreatPull"/>,
    ///   <see cref="SizeToHold"/>).
    ///
    ///   A pass in the air is framed on the midpoint between the ball and the
    ///   receiver it was thrown to (<see cref="FlightTarget"/>), so the catch
    ///   point is on screen before the ball gets there.
    ///
    /// All three are suppressed at the snap and after the whistle, and the ball is
    /// held inside the frame whatever they ask for (<see cref="KeepBallInFrame"/>).
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
        ///
        /// SERIALIZED RATHER THAN const, FOR TUNING, as Systems_PostProcessView does
        /// for the grade: framing is judged against a live capture on a real aspect,
        /// and a recompile per guess made that loop slow enough to skip.
        /// </summary>
        [Header("Framing (metres of half-height)")]
        [SerializeField] private float _wideSize = 30f;

        /// <summary>
        /// Tightest framing — about 48 yards of field, down from 61. Crops the outer
        /// few metres of each sideline, which is empty grass at every formation this
        /// game lines up. See _wideSize for why it stops here and not tighter.
        /// </summary>
        [SerializeField] private float _tightSize = 22f;

        /// <summary>
        /// The least the shot opens up by between the snap and a breaking run.
        ///
        /// WITHOUT THIS THE ZOOM WAS DEAD ON EVERY TALL PHONE. TightSize grows with the
        /// screen's aspect so the formation fits, and WideSize was only ever
        /// max(_wideSize, TightSize): at 20:9 the formation needs (12.0 + 1.5) / 0.45
        /// = 30.0, exactly _wideSize, and at 21:9 31.5, past it — so tight and wide
        /// were the same number and "tighten on the snap, pull wide when a run breaks"
        /// did nothing on the devices this ships to. Six metres is the travel the 9:16
        /// design already had (24 -> 30), so a 9:16 screen gets exactly the shot it
        /// was tuned with and a taller one gets the same pull-out on top of the
        /// framing its width forces.
        /// </summary>
        [SerializeField] private float _minimumZoomTravel = 6f;

        /// <summary>Yards past the line of scrimmage at which the shot is fully wide.</summary>
        [SerializeField] private float _fullWideYards = 22f;

        [Header("Damping (per second)")]
        [SerializeField] private float _positionDamping = 3.2f;

        [SerializeField] private float _zoomDamping = 2.4f;

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
        [Range(0f, 1f)]
        [SerializeField] private float _lateralTracking = 0.55f;

        /// <summary>
        /// Clearance beyond the widest player, in metres, so a receiver on the
        /// sideline is framed rather than sitting exactly on the edge of the screen.
        /// One player radius plus a little air.
        /// </summary>
        private const float FORMATION_MARGIN = 1.5f;

        /// <summary>
        /// The tight shot, sized so the whole formation actually fits THIS screen.
        ///
        /// WHY THIS IS NOT A CONSTANT ANY MORE, AND WHAT IT WAS HIDING. _tightSize
        /// is a half-HEIGHT; what has to cover the formation is the half-WIDTH, and
        /// those differ by the aspect ratio. At the 9:16 this game is designed for,
        /// 22 x 0.5625 = 12.38 m against a formation that splits to
        /// Systems_Formation.WidestSlotX = 12.0 — it fitted, with 0.38 m to spare,
        /// which is less than a player radius.
        ///
        /// Every portrait screen TALLER than 16:9 has a smaller aspect and therefore
        /// a narrower shot, and modern handsets are essentially all taller than that:
        ///
        ///     9:16  (design)   half-width 12.38 m   fits
        ///     20:9  (typical)  half-width  9.90 m   receivers 2.1 m off screen
        ///     37:16 (tall)     half-width  9.51 m   receivers 2.5 m off screen
        ///
        /// So on the actual shipping target both split receivers and both corners
        /// were cropped out of frame at the snap — six of the twenty-two players on
        /// the field, including every player the pass game is about. Nothing failed
        /// and nothing logged; the shot was simply wrong on every real device.
        ///
        /// Taking the max keeps the designed framing wherever it is already wide
        /// enough and opens up only as far as the screen forces. A 9:16 device gets
        /// exactly the shot that was tuned for it.
        /// </summary>
        private float TightSize()
        {
            float required =
                (Systems_Formation.WidestSlotX + FORMATION_MARGIN) / Mathf.Max(_camera.aspect, 0.01f);

            return Mathf.Max(_tightSize, required);
        }

        /// <summary>
        /// The wide shot: the designed size, or the snap shot plus the minimum
        /// travel, whichever is wider. See _minimumZoomTravel.
        /// </summary>
        private float WideSize()
        {
            return Mathf.Max(_wideSize, TightSize() + _minimumZoomTravel);
        }

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

        /// <summary>
        /// Seconds of the carrier's SIDEWAYS velocity the shot leads by.
        ///
        /// ONLY SIDEWAYS, AND THE REASON IS THE DROPBACK. Leading by the whole
        /// velocity vector is the obvious version and it is wrong on every pass
        /// play: the quarterback retreats seven yards at close to top speed in the
        /// first second, so a shot that led his velocity would swing BACKWARDS off
        /// the routes at exactly the moment they are developing. Downfield is
        /// already covered by DOWNFIELD_LEAD, which never turns round. What that
        /// constant cannot do is see a sweep: a back running at the sideline was
        /// framed with as much grass behind him as in front.
        /// </summary>
        private const float LATERAL_LOOK_AHEAD_SECONDS = 0.5f;

        /// <summary>
        /// Most the lateral lead may move the shot, as a fraction of its half-width.
        /// </summary>
        private const float MAX_LATERAL_LEAD = 0.3f;

        /// <summary>
        /// Metres inside which a defender counts as a threat to the carrier. About
        /// twenty yards — two seconds of closing at pursuit speed, which is as far
        /// ahead as the shot can usefully anticipate a tackle.
        /// </summary>
        private const float THREAT_RANGE = 18f;

        /// <summary>
        /// How hard one defender pulls the shot toward himself.
        ///
        /// Each defender in range contributes his offset from the carrier, weighted
        /// by 1 - distance / THREAT_RANGE. That weight is the whole design: it is
        /// zero at the edge of the range and the OFFSET is zero on top of the
        /// carrier, so a defender's pull rises and falls smoothly as he closes and
        /// nobody ever enters or leaves the sum with a jump. Choosing "the nearest
        /// defender" instead would hand the shot a new target every time two
        /// pursuers swapped places.
        /// </summary>
        private const float THREAT_PULL = 0.5f;

        /// <summary>Most the defenders together may move the shot, in metres.</summary>
        private const float MAX_THREAT_PULL = 3.5f;

        /// <summary>
        /// Clearance kept between the ball and the edge of the frame, in metres.
        /// The lead and the pull are both asked for in good faith and both yield to
        /// this: whatever else the shot is trying to show, the ball is in it.
        /// </summary>
        private const float BALL_FRAME_MARGIN = 2.5f;

        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;
        private ISubscriber<Systems_PassThrownMessage> _thrownSubscriber;
        private IDisposable _snappedSubscription;
        private IDisposable _thrownSubscription;

        private Systems_BallModel _ball;
        private Systems_PlayModel _play;
        private Systems_PlayerRegistry _registry;
        private Systems_PresentationBudget _budget;

        private Camera _camera;
        private Transform _transform;

        private float _homeZ;

        /// <summary>
        /// Who the pass in the air was thrown to, or null. Set by the release and
        /// cleared by the snap; meaningless unless the ball is in flight.
        /// </summary>
        private Systems_IPlayerHandle _passTarget;

        // What ReadThreats found this frame. Fields rather than out parameters
        // because the target and the size both want them and are computed apart.
        private Vector2 _threatPull;
        private Vector2 _nearestThreat;
        private bool _hasNearestThreat;

        [Inject]
        public void Construct(
            Systems_BallModel ball,
            Systems_PlayModel play,
            Systems_PlayerRegistry registry,
            Systems_PresentationBudget budget,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber,
            ISubscriber<Systems_PassThrownMessage> thrownSubscriber)
        {
            _ball = ball;
            _play = play;
            _registry = registry;
            _budget = budget;
            _snappedSubscriber = snappedSubscriber;
            _thrownSubscriber = thrownSubscriber;
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
            _thrownSubscription = _thrownSubscriber?.Subscribe(OnPassThrown);
        }

        private void OnDestroy()
        {
            _snappedSubscription?.Dispose();
            _thrownSubscription?.Dispose();
        }

        /// <summary>
        /// LateUpdate so the camera reads positions the physics step has already
        /// settled.
        /// </summary>
        private void LateUpdate()
        {
            float deltaTime = Time.deltaTime;

            Systems_IPlayerHandle carrier = LiveCarrier();
            float openness = Openness();

            ReadThreats(carrier);

            Vector2 target = FramingTarget(carrier, openness);
            float targetSize = FramingSize(target, openness);

            // Exponential smoothing rather than Lerp with a raw t. Lerp against
            // deltaTime is frame-rate dependent, and this game runs at 60 in the
            // editor and whatever the build gets.
            float positionBlend = 1f - Mathf.Exp(-_positionDamping * deltaTime);
            float zoomBlend = 1f - Mathf.Exp(-_zoomDamping * deltaTime);

            _camera.orthographicSize = Mathf.Lerp(
                _camera.orthographicSize, targetSize, zoomBlend);

            Vector3 current = _transform.position;
            Vector2 eased = Vector2.Lerp(new Vector2(current.x, current.y), target, positionBlend);

            eased = ClampToField(eased, _camera.orthographicSize);

            _transform.position = new Vector3(eased.x, eased.y, _homeZ);
        }

        /// <summary>
        /// The player holding the ball while the play is live, or null.
        ///
        /// Null after the whistle on purpose. The ball stays attached to whoever
        /// was tackled and twenty-two bodies keep coasting for a second, so a shot
        /// that went on reading threats would drift about the dead-ball spot
        /// chasing defenders who are no longer chasing anyone. Without a carrier
        /// the shot is plain ball-follow, which settles.
        /// </summary>
        private Systems_IPlayerHandle LiveCarrier()
        {
            if (_play == null || _registry == null
                || _play.Phase != Systems_PlayPhase.Live || !_ball.IsHeld)
            {
                return null;
            }

            int carrierId = _ball.CarrierId;

            if (carrierId < 0 || carrierId >= Systems_PlayerRegistry.CAPACITY)
            {
                return null;
            }

            return _registry.Get(carrierId);
        }

        /// <summary>
        /// How far the play has got from the line of scrimmage, zero at the snap
        /// and one at FULL_WIDE_YARDS. The one number that says "this has stopped
        /// being a formation and become a chase": it opens the zoom, and it is
        /// what the threat pull is scaled by so that the shot composed for the
        /// snap is not disturbed at the snap.
        /// </summary>
        private float Openness()
        {
            if (_play == null)
            {
                return 1f;
            }

            float yardsFromScrimmage =
                Mathf.Abs(_ball.Position.y - _play.LineOfScrimmageY) / Systems_FieldModel.YARD;

            return Mathf.Clamp01(yardsFromScrimmage / _fullWideYards);
        }

        /// <summary>
        /// One pass over the roster for both things the shot wants to know about
        /// the men chasing the carrier: where they collectively are, and where the
        /// closest one is. Twenty-two iterations and one square root per defender
        /// in range; it allocates nothing.
        /// </summary>
        private void ReadThreats(Systems_IPlayerHandle carrier)
        {
            _threatPull = Vector2.zero;
            _hasNearestThreat = false;

            if (carrier == null)
            {
                return;
            }

            Vector2 origin = carrier.Position;
            float nearestSqr = THREAT_RANGE * THREAT_RANGE;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                // The other side, whichever that is, rather than "Defense" by
                // name: the question is who is trying to stop this carrier.
                if (handle == null || handle.Side == carrier.Side)
                {
                    continue;
                }

                Vector2 offset = handle.Position - origin;
                float sqrDistance = offset.sqrMagnitude;

                if (sqrDistance >= THREAT_RANGE * THREAT_RANGE)
                {
                    continue;
                }

                float weight = 1f - (Mathf.Sqrt(sqrDistance) / THREAT_RANGE);
                _threatPull += offset * (weight * THREAT_PULL);

                if (sqrDistance < nearestSqr)
                {
                    nearestSqr = sqrDistance;
                    _nearestThreat = handle.Position;
                    _hasNearestThreat = true;
                }
            }

            _threatPull = Vector2.ClampMagnitude(_threatPull, MAX_THREAT_PULL);
        }

        /// <summary>
        /// Where the operator wants the shot centred. Vertically the ball, laterally
        /// only a fraction of it so the field does not swing about — then moved
        /// toward where the play is going, and held to wherever keeps the ball on
        /// screen.
        /// </summary>
        private Vector2 FramingTarget(Systems_IPlayerHandle carrier, float openness)
        {
            Vector2 ball = _ball.Position;
            float size = _camera.orthographicSize;

            Vector2 target = _ball.IsInFlight && _passTarget != null
                ? FlightTarget(ball)
                : new Vector2(ball.x * _lateralTracking, ball.y + (size * DOWNFIELD_LEAD));

            if (carrier != null)
            {
                target.x += LateralLead(carrier, size);
                target += _threatPull * openness;
            }

            return KeepBallInFrame(target, ball, size);
        }

        /// <summary>
        /// A pass in the air is framed on the midpoint between the ball and the
        /// receiver it was thrown to.
        ///
        /// Following the ball alone shows a throw the way a spectator in the wrong
        /// seat sees it: the ball crosses the screen and the catch arrives at the
        /// edge of frame, if it arrives in frame at all. The whole suspense of a
        /// pass is whether the man at the far end is open, so he has to be on
        /// screen for the whole flight. The midpoint keeps both ends in the shot,
        /// and it converges on the catch point as the ball gets there — so the
        /// hand-back to following the new carrier is a move of a metre or two, not
        /// a cut.
        ///
        /// Tracked fully across the field rather than by LATERAL_TRACKING: that
        /// fraction exists to stop a crossing route swinging the picture about, and
        /// a midpoint already moves at half the speed of either end.
        /// </summary>
        private Vector2 FlightTarget(Vector2 ball)
        {
            return (ball + _passTarget.Position) * 0.5f;
        }

        /// <summary>See LATERAL_LOOK_AHEAD_SECONDS for why this is sideways only.</summary>
        private float LateralLead(Systems_IPlayerHandle carrier, float size)
        {
            float limit = size * _camera.aspect * MAX_LATERAL_LEAD;

            return Mathf.Clamp(
                carrier.Velocity.x * LATERAL_LOOK_AHEAD_SECONDS, -limit, limit);
        }

        /// <summary>
        /// Pulls a target back to within reach of the ball. The lead and the threat
        /// pull are added without regard to each other, and at a sideline they can
        /// agree to move the shot further than the frame is wide.
        /// </summary>
        private Vector2 KeepBallInFrame(Vector2 target, Vector2 ball, float size)
        {
            float reachX = (size * _camera.aspect) - BALL_FRAME_MARGIN;
            float reachY = size - BALL_FRAME_MARGIN;

            if (reachX > 0f)
            {
                target.x = Mathf.Clamp(target.x, ball.x - reachX, ball.x + reachX);
            }

            if (reachY > 0f)
            {
                target.y = Mathf.Clamp(target.y, ball.y - reachY, ball.y + reachY);
            }

            return target;
        }

        /// <summary>
        /// Tight before and at the snap, opening up as the play gets away from the
        /// line of scrimmage. A pass in the air goes wide immediately — the throw
        /// is the moment the viewer most needs to see both ends of it.
        /// </summary>
        private float FramingSize(Vector2 target, float openness)
        {
            float tight = TightSize();
            float wide = WideSize();

            if (_play == null || _ball.IsInFlight)
            {
                return wide;
            }

            float size = Mathf.Lerp(tight, wide, openness);

            if (_hasNearestThreat)
            {
                size = Mathf.Max(size, SizeToHold(_nearestThreat, target));
            }

            return Mathf.Min(size, wide);
        }

        /// <summary>
        /// The half-height at which a point is inside the frame centred on
        /// <paramref name="target"/>, with the same clearance the formation gets.
        ///
        /// Used for the closest defender only, and capped at the wide shot by the
        /// caller. On a portrait screen the cap bites early sideways — the frame is
        /// barely half as wide as it is tall — which is why the pull exists as
        /// well: a man closing from the far hash is brought into the shot by moving
        /// it, because opening it cannot reach him.
        /// </summary>
        private float SizeToHold(Vector2 point, Vector2 target)
        {
            Vector2 offset = point - target;

            float forHeight = Mathf.Abs(offset.y) + FORMATION_MARGIN;

            float forWidth =
                (Mathf.Abs(offset.x) + FORMATION_MARGIN) / Mathf.Max(_camera.aspect, 0.01f);

            return Mathf.Max(forHeight, forWidth);
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

        private void OnPassThrown(Systems_PassThrownMessage message)
        {
            _passTarget = message.TargetId < 0 ? null : _registry.FindById(message.TargetId);
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            _passTarget = null;

            // Snap the shot straight to the new line of scrimmage. Damping across
            // a spot change would send the camera sailing sixty yards down the
            // field between plays.
            float tight = TightSize();
            _camera.orthographicSize = tight;

            // Same downfield lead the steady-state framing uses, or the shot would
            // cut to the line of scrimmage and then immediately drift upfield by the
            // lead on the next frame.
            Vector2 spot = ClampToField(
                new Vector2(0f, message.LineOfScrimmageY + (tight * DOWNFIELD_LEAD)),
                tight);

            _transform.position = new Vector3(spot.x, spot.y, _homeZ);
        }
    }
}
