using System;
using System.Collections.Generic;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// Draws what each policy just decided: a curved arrow per player showing the
    /// drive and steer it emitted, a read line from the quarterback to the
    /// receiver its aim vector currently selects, and the pocket clock round the
    /// quarterback that says how long that read has left.
    ///
    /// WHY THIS IS THE FEATURE AND NOT DECORATION. Twenty-two shapes moving around
    /// a green rectangle is a simulation you can watch; it is not a simulation you
    /// can READ. Nothing on screen distinguishes a linebacker that has diagnosed
    /// the play and is driving downhill from one that is drifting because its
    /// policy has no opinion, and nothing shows that the quarterback has been
    /// staring at the same covered receiver for two seconds. Those are the only
    /// interesting events in an AI football game, and until now every one of them
    /// was invisible.
    ///
    /// IT DRAWS THE ACTION VECTOR, WHICH IS THE HONEST THING TO DRAW. See
    /// Systems_PlayerIntent: the alternative was the heuristic's own target point,
    /// which is a much prettier signal and which a trained brain never produces.
    /// An overlay that silently fell back to the scripted plan whenever a real
    /// policy was loaded would be worse than no overlay, because it would look
    /// right.
    ///
    /// ONE MESH, ONE DRAW CALL. Twenty-three LineRenderers would be twenty-three
    /// draw calls on a mobile target that already spends twenty-two on the players
    /// (see PoFootball_Player.shader for why that trade was accepted there and is
    /// not worth repeating here). Every arrow, every dash and every head is
    /// appended to one vertex buffer with per-vertex colour, so the whole overlay
    /// is a single unlit sprite material — and the buffers are Lists that are
    /// cleared rather than reallocated, so a steady-state frame allocates nothing.
    ///
    /// NOTHING HERE WRITES TO THE SIMULATION. It reads the models, the registry
    /// and one throw-resolution helper, and it is gated off entirely in training
    /// and headless by Systems_PresentationBudget — where Systems_IIntentSource
    /// reports Available false and there is nothing to draw in the first place.
    /// </summary>
    [DefaultExecutionOrder(-45)]
    [DisallowMultipleComponent]
    public sealed class Systems_IntentOverlayView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// Whether to draw the per-player drive/steer arrows.
        ///
        /// OFF: they read as debug annotation over a game you are meant to watch,
        /// which is the opposite of what the class docstring above argues for. That
        /// argument is about a developer READING the simulation, and it is still
        /// correct for that job — which is why this is a switch rather than a
        /// deletion. Set it back to true to get the arrows for diagnosing what a
        /// policy is actually doing.
        ///
        /// The quarterback read line is separate and still drawn: it shows WHICH
        /// receiver the aim has selected, not where a body is heading, and it is
        /// the one piece of this overlay that has no other way of being seen.
        /// </summary>
        private const bool SHOW_PLAYER_ARROWS = false;

        /// <summary>
        /// Above both player groups (Systems_RoleShapeApplier puts offense on 1 and
        /// defense on 2) and below the ball trail on 4.
        ///
        /// OVER THE BODIES RATHER THAN UNDER THEM, deliberately. An arrow tucked
        /// behind the players disappears in exactly the situation it is most needed
        /// — a seven-man pile at the line, where the whole question is who is
        /// driving and who has stopped. The arrows start outside the 0.5 m body
        /// radius and point away from it, so in the open field they overlap almost
        /// nothing anyway.
        /// </summary>
        private const int SORTING_ORDER = 3;

        /// <summary>
        /// Just past the collider radius. Arrows start here rather than at the
        /// centre so they never sit on top of the shape whose decision they
        /// describe.
        /// </summary>
        private const float BODY_RADIUS = 0.62f;

        /// <summary>Arrow length in metres at |drive| = 1, past the body radius.</summary>
        private const float MAX_ARROW_LENGTH = 2.4f;

        /// <summary>
        /// Total bend across the arrow at |steer| = 1, in degrees.
        ///
        /// The arrow is drawn as an arc rather than a straight line because steer
        /// is a TORQUE, not a strafe: a player at full steer is not moving
        /// sideways, it is turning while it drives. A straight arrow with a
        /// sideways kink would say the wrong thing about car-like steering, and 55
        /// degrees of visible curve says the right one without the tip swinging so
        /// far that two neighbouring arrows cross.
        /// </summary>
        private const float MAX_ARC_DEGREES = 55f;

        /// <summary>Segments in the arc. Six is smooth at the size these draw.</summary>
        private const int ARC_SEGMENTS = 6;

        private const float SHAFT_WIDTH = 0.17f;
        private const float HEAD_WIDTH = 0.42f;
        private const float HEAD_LENGTH = 0.5f;

        /// <summary>
        /// Below this the player is idling and no arrow is drawn at all.
        ///
        /// Not a rendering nicety — a floor of zero means twenty-two stub arrows
        /// twitching around the formation before anyone has moved, which reads as
        /// noise and teaches the eye to ignore the overlay.
        /// </summary>
        private const float MIN_DRIVE_TO_DRAW = 0.08f;

        /// <summary>
        /// Ticks an intent may be stale before it stops being drawn.
        ///
        /// Agents decide every fifth physics tick, so anything older than about two
        /// decisions is a player that has stopped reporting — which happens at the
        /// whistle. The comparison also rejects an intent from the PREVIOUS play
        /// for free, because Systems_PlayModel.PhysicsTick restarts at zero every
        /// snap and last play's stamp then reads as being from the future. That is
        /// why nothing has to clear the array on reset.
        /// </summary>
        private const int MAX_STALE_TICKS = 12;

        /// <summary>How fast the drawn arrow chases the decision, per second.</summary>
        private const float CHASE_RATE = 11f;

        private const float READ_LINE_WIDTH = 0.13f;
        private const float READ_DASH_LENGTH = 1.1f;
        private const float READ_GAP_LENGTH = 0.7f;

        /// <summary>Alpha of the read line while a throw is not currently legal.</summary>
        private const float READ_LINE_BLOCKED_ALPHA = 0.3f;

        /// <summary>
        /// Radius of the pocket clock, in metres. Outside the 0.5 m body and the
        /// carrier glow PoFootball/Player draws around it, inside the nearest
        /// lineman, so the ring belongs unmistakably to the quarterback.
        /// </summary>
        private const float POCKET_RING_RADIUS = 1.2f;

        private const float POCKET_RING_WIDTH = 0.17f;

        /// <summary>
        /// Chords in a full ring. The arc is drawn as straight quads, and at this
        /// radius forty of them are each under a fifth of a metre long — a couple
        /// of pixels at broadcast framing, which is below where a chord reads as
        /// one.
        /// </summary>
        private const int POCKET_RING_SEGMENTS = 40;

        /// <summary>
        /// Distance to the nearest rusher at which the ring starts to redden, and
        /// the distance at which it is fully red. The near figure is a body width
        /// past contact (two 0.5 m radii): by then the hit is the next thing that
        /// happens.
        /// </summary>
        private const float PRESSURE_FAR = 8f;
        private const float PRESSURE_NEAR = 1.6f;

        /// <summary>
        /// The mesh is rebuilt every frame and its real bounds change every frame
        /// with it. Recomputing them costs a pass over the vertices to buy culling
        /// that can never fire — the overlay covers wherever the players are, and
        /// the camera is always looking at the players. A fixed box larger than the
        /// field skips both.
        /// </summary>
        private static readonly Bounds FieldBounds =
            new Bounds(Vector3.zero, new Vector3(240f, 240f, 1f));

        /// <summary>
        /// Radius of the landing ring at the release, in metres: twice
        /// Systems_SimConstants.CATCH_RADIUS, so it starts visibly bigger than the
        /// zone the ball can be taken in and closes past it as the ball arrives.
        /// </summary>
        private const float PASS_RING_RADIUS = Systems_SimConstants.CATCH_RADIUS * 2f;

        private const float PASS_RING_WIDTH = 0.15f;
        private const int PASS_RING_SEGMENTS = 32;

        /// <summary>
        /// The longest a pass can be in the air before it is ruled incomplete. The
        /// ring never outlasts the ball.
        /// </summary>
        private const float MAX_FLIGHT_SECONDS =
            Systems_SimConstants.MAX_FLIGHT_TICKS * Systems_GameRules.SECONDS_PER_TICK;

        /// <summary>
        /// Chevron length, as a fraction of the frame's half-height, so it is the
        /// same size on screen whether the shot is tight or wide.
        /// </summary>
        private const float CHEVRON_SIZE = 0.05f;

        private const float CHEVRON_WIDTH = 0.012f;

        /// <summary>
        /// Clearance from the side edges, as a fraction of the half-height.
        /// </summary>
        private const float CHEVRON_EDGE_MARGIN = 0.06f;

        /// <summary>
        /// Extra clearance at the top and bottom, as a fraction of the half-height:
        /// the status strips and the scoreboard sit over those edges, and a chevron
        /// under them is one nobody sees. An estimate of the strips at the 9:16
        /// design size, not a measurement of them.
        /// </summary>
        private const float CHEVRON_HUD_CLEARANCE = 0.14f;

        private Systems_IIntentSource _intent;
        private Systems_PlayerRegistry _registry;
        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_BallSystem _ballSystem;
        private Systems_PresentationBudget _budget;
        private ISubscriber<Systems_PassThrownMessage> _thrownSubscriber;
        private IDisposable _thrownSubscription;

        private Camera _camera;
        private Transform _cameraTransform;

        private Mesh _mesh;
        private MeshRenderer _meshRenderer;

        // The pass in the air: where it was led to, how long it had to get there,
        // and how long it has left. Zero remaining means there is no ring.
        private Vector2 _passRingCentre;
        private float _passRingDuration;
        private float _passRingRemaining;

        private readonly List<Vector3> _vertices = new List<Vector3>(2048);
        private readonly List<Color32> _colors = new List<Color32>(2048);
        private readonly List<int> _indices = new List<int>(3072);

        private SpriteRenderer[] _bodies;
        private float[] _shownDrive;
        private float[] _shownSteer;
        private float[] _shownAlpha;

        [Inject]
        public void Construct(
            Systems_IIntentSource intent,
            Systems_PlayerRegistry registry,
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_BallSystem ballSystem,
            Systems_PresentationBudget budget,
            ISubscriber<Systems_PassThrownMessage> thrownSubscriber)
        {
            _intent = intent;
            _registry = registry;
            _play = play;
            _ball = ball;
            _ballSystem = ballSystem;
            _budget = budget;
            _thrownSubscriber = thrownSubscriber;
        }

        /// <summary>
        /// Start rather than Awake, for the reason Systems_PlayerAppearanceView
        /// gives: injection has not run by Awake, and the sprite renderers this
        /// reads its colours from are assigned by Systems_RoleShapeApplier at
        /// execution order -100.
        /// </summary>
        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled
                || _intent == null || !_intent.Available)
            {
                enabled = false;
                return;
            }

            if (!Systems_ParticleMaterial.TryLoad(
                    nameof(Systems_IntentOverlayView), "No intent is drawn.", out Material source))
            {
                enabled = false;
                return;
            }

            CollectBodies();
            BuildMesh(source);

            // Cached once; Camera.main is a scene search. Null leaves the chevrons
            // off and everything else drawing.
            _camera = Camera.main;
            _cameraTransform = _camera == null ? null : _camera.transform;

            _thrownSubscription = _thrownSubscriber?.Subscribe(OnPassThrown);
        }

        private void OnDestroy()
        {
            _thrownSubscription?.Dispose();
            _thrownSubscription = null;

            if (_mesh != null)
            {
                Destroy(_mesh);
            }
        }

        /// <summary>
        /// One scan for the twenty-two sprite renderers, indexed by the handle's
        /// own Id so the registry and this array share a subscript.
        ///
        /// THE ARROW TAKES ITS COLOUR FROM THE BODY IT BELONGS TO, read live rather
        /// than resolved once. Systems_RoleShapeApplier repaints all twenty-two on
        /// every change of possession — offense is whichever team has the ball, not
        /// a fixed side — and Agent_FootballPlayer swaps the carrier to white on top
        /// of that. Reading the renderer means the overlay follows both for free and
        /// cannot disagree with the field about who is on which team, which a cached
        /// side-to-colour table would do the first time the ball changed hands.
        /// </summary>
        private void CollectBodies()
        {
            int capacity = Systems_PlayerRegistry.CAPACITY;

            _bodies = new SpriteRenderer[capacity];
            _shownDrive = new float[capacity];
            _shownSteer = new float[capacity];
            _shownAlpha = new float[capacity];

            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include);

            for (int index = 0; index < behaviours.Length; index++)
            {
                if (!(behaviours[index] is Systems_IPlayerHandle handle))
                {
                    continue;
                }

                if (handle.Id < 0 || handle.Id >= capacity)
                {
                    continue;
                }

                if (behaviours[index].TryGetComponent(out SpriteRenderer body))
                {
                    _bodies[handle.Id] = body;
                }
            }
        }

        private void BuildMesh(Material source)
        {
            _mesh = new Mesh { name = "PoFootball Intent Overlay" };
            _mesh.MarkDynamic();

            GameObject host = new GameObject("IntentOverlay");
            host.transform.SetParent(transform, false);

            MeshFilter filter = host.AddComponent<MeshFilter>();
            filter.sharedMesh = _mesh;

            _meshRenderer = host.AddComponent<MeshRenderer>();

            // sharedMaterial, never material: the setter clones, and a clone per
            // scene load is a leak nothing here would ever free
            // (.claude/rules/performance.md). Nothing on this material is written
            // per instance — every colour arrives through the vertex stream.
            _meshRenderer.sharedMaterial = source;
            _meshRenderer.sortingOrder = SORTING_ORDER;
            _meshRenderer.shadowCastingMode =
                UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
        }

        /// <summary>
        /// LateUpdate for the reason Systems_PlayerAppearanceView uses it: the
        /// bodies have finished moving for this frame, so an arrow drawn from a
        /// player's position lands on the player rather than a frame behind it.
        /// </summary>
        private void LateUpdate()
        {
            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();

            bool live = _play != null && _play.Phase == Systems_PlayPhase.Live;

            // Guarded rather than removed: AppendPlayerArrows still owns the steer
            // smoothing state, so re-enabling SHOW_PLAYER_ARROWS restores the
            // overlay exactly as it was with no other change.
            // CS0162 is the compiler noticing the switch above is off, which is
            // the point of it. Silenced here and nowhere else, so a genuinely
            // unreachable line elsewhere in this file still warns.
#pragma warning disable CS0162
            if (SHOW_PLAYER_ARROWS)
            {
                float chase = 1f - Mathf.Exp(-CHASE_RATE * Time.deltaTime);
                AppendPlayerArrows(chase, live);
            }
#pragma warning restore CS0162

            if (live)
            {
                AppendQuarterbackRead();
                AppendPassRing();
            }
            else
            {
                // The whistle clears the ring as well as the ball's state does.
                _passRingRemaining = 0f;
            }

            if (_play != null && _play.Phase != Systems_PlayPhase.Dead)
            {
                AppendOffscreenChevrons();
            }

            Upload();
        }

        /// <summary>
        /// Where the pass was led to, and how long the ball has to get there.
        ///
        /// THE LEAD POINT FROM Systems_BallSystem.LeadPoint, for the reason the
        /// read line asks the ball system rather than copying its rule. It is the
        /// point the quarterback READ, not the ball's own landing spot: the ball
        /// carries the aim slack, the ring carries the decision — the same split
        /// Systems_BallSystem.Throw draws between the overlay and the throw.
        /// </summary>
        private void OnPassThrown(Systems_PassThrownMessage message)
        {
            _passRingRemaining = 0f;

            if (message.TargetId < 0 || message.Speed <= 0f || _ball == null)
            {
                return;
            }

            Systems_IPlayerHandle target = _registry.FindById(message.TargetId);

            if (target == null)
            {
                return;
            }

            // The ball model is written before the message goes out, so this is
            // the release point.
            Vector2 origin = _ball.ThrowOrigin;
            Vector2 lead = Systems_BallSystem.LeadPoint(origin, target, message.Speed);

            _passRingCentre = lead;
            _passRingDuration = Mathf.Min(
                Vector2.Distance(origin, lead) / message.Speed, MAX_FLIGHT_SECONDS);
            _passRingRemaining = _passRingDuration;
        }

        /// <summary>
        /// A ring on the spot the pass was led to, closing to nothing as the ball
        /// arrives — the countdown on a throw the way the pocket clock is the
        /// countdown on holding it.
        ///
        /// GAME TIME, NOT REAL TIME. It is a measure of the ball's flight, which
        /// runs on physics ticks, so at Sim Speed 8x it has to close eight times
        /// as fast or it would describe a ball that had already landed.
        ///
        /// The ball leaving flight — caught by either side, or ruled incomplete —
        /// clears it here; the whistle clears it in LateUpdate.
        /// </summary>
        private void AppendPassRing()
        {
            if (_passRingRemaining <= 0f)
            {
                return;
            }

            if (!_ball.IsInFlight)
            {
                _passRingRemaining = 0f;
                return;
            }

            _passRingRemaining -= Time.deltaTime;

            float fraction = _passRingDuration <= 0f
                ? 0f
                : Mathf.Clamp01(_passRingRemaining / _passRingDuration);

            float radius = PASS_RING_RADIUS * fraction;

            if (radius < 0.05f)
            {
                return;
            }

            Color color = Systems_UiTheme.Accent;
            color.a = 0.85f;
            Color32 packed = color;

            float step = Mathf.PI * 2f / PASS_RING_SEGMENTS;
            Vector2 from = _passRingCentre + new Vector2(radius, 0f);

            for (int segment = 1; segment <= PASS_RING_SEGMENTS; segment++)
            {
                float angle = step * segment;

                Vector2 to = _passRingCentre
                    + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);

                AppendTaperedQuad(from, to, PASS_RING_WIDTH, PASS_RING_WIDTH, packed, packed);

                from = to;
            }
        }

        /// <summary>
        /// A chevron on the edge of the screen for every receiver and corner the
        /// shot has cropped, pointing at him.
        ///
        /// THE CASE IT EXISTS FOR IS THE SNAP ON A TALL PHONE. The broadcast
        /// camera's shot is sized as a half-HEIGHT, and on a portrait screen
        /// taller than 9:16 the half-width that buys is narrower again — on 20:9
        /// the split receivers and the corners across from them, every player the
        /// pass game is about, can start the play out of frame. A chevron says
        /// they exist and which way they are without the camera giving up the
        /// shot it chose. It tests the frame, not the aspect, so it is equally
        /// right for a route run off the top of the screen mid-play.
        ///
        /// It READS the camera and never moves it. This runs at execution order
        /// -45 and the camera moves at -40, so the frame read here is the previous
        /// one; the margin is far larger than one frame of camera damping, and the
        /// difference is not visible.
        ///
        /// One pass over the roster per frame, as NearestOpponentDistance does, and
        /// the chevrons are two quads each in the same mesh — no draw call.
        /// </summary>
        private void AppendOffscreenChevrons()
        {
            if (_camera == null || _registry == null)
            {
                return;
            }

            Vector3 cameraPosition = _cameraTransform.position;
            Vector2 centre = new Vector2(cameraPosition.x, cameraPosition.y);

            float halfHeight = _camera.orthographicSize;
            float halfWidth = halfHeight * _camera.aspect;

            float insetX = halfWidth - (halfHeight * CHEVRON_EDGE_MARGIN);
            float insetY = halfHeight * (1f - CHEVRON_EDGE_MARGIN - CHEVRON_HUD_CLEARANCE);

            if (insetX <= 0f || insetY <= 0f)
            {
                return;
            }

            float size = halfHeight * CHEVRON_SIZE;
            float width = halfHeight * CHEVRON_WIDTH;

            for (int slotIndex = 0;
                 slotIndex < Systems_PlayerRegistry.CAPACITY;
                 slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                if (handle == null
                    || (handle.Role != Systems_PlayerRole.WideReceiver
                        && handle.Role != Systems_PlayerRole.Cornerback))
                {
                    continue;
                }

                Vector2 offset = handle.Position - centre;

                if (Mathf.Abs(offset.x) <= halfWidth && Mathf.Abs(offset.y) <= halfHeight)
                {
                    continue;
                }

                // Along the line from the middle of the screen to the player, out
                // to whichever inset edge that line meets first.
                float scaleX = Mathf.Abs(offset.x) > 1e-4f
                    ? insetX / Mathf.Abs(offset.x)
                    : float.PositiveInfinity;

                float scaleY = Mathf.Abs(offset.y) > 1e-4f
                    ? insetY / Mathf.Abs(offset.y)
                    : float.PositiveInfinity;

                float scale = Mathf.Min(scaleX, scaleY);

                if (float.IsInfinity(scale))
                {
                    continue;
                }

                Vector2 direction = offset.normalized;
                Vector2 at = centre + (offset * scale);

                AppendChevron(at, direction, size, width, ChevronColor(handle));
            }
        }

        /// <summary>
        /// The body's own colour, so a chevron says whose man is out there — read
        /// live for the reason ArrowColor reads it live.
        /// </summary>
        private Color32 ChevronColor(Systems_IPlayerHandle handle)
        {
            SpriteRenderer body = _bodies[handle.Id];
            Color color = body == null ? Color.white : body.color;
            color.a = 0.9f;
            return color;
        }

        /// <summary>Two arms meeting at a point: an open "›" aimed along direction.</summary>
        private void AppendChevron(
            Vector2 centre, Vector2 direction, float size, float width, Color32 color)
        {
            Vector2 normal = new Vector2(-direction.y, direction.x);

            Vector2 tip = centre + (direction * (size * 0.5f));
            Vector2 back = centre - (direction * (size * 0.5f));
            Vector2 flank = normal * (size * 0.6f);

            AppendTaperedQuad(back + flank, tip, width, width, color, color);
            AppendTaperedQuad(back - flank, tip, width, width, color, color);
        }

        private void AppendPlayerArrows(float chase, bool live)
        {
            for (int slotIndex = 0;
                 slotIndex < Systems_PlayerRegistry.CAPACITY;
                 slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                if (handle == null)
                {
                    continue;
                }

                Systems_PlayerIntent intent = _intent.Read(handle.Id);
                bool fresh = live && IsFresh(intent);

                // All three drawn quantities are chased rather than assigned.
                // Decisions land every fifth physics tick and the frame rate is
                // higher than that, so a direct write makes every arrow snap and
                // hold, snap and hold. Fading the alpha the same way is what makes
                // an arrow leave at the whistle instead of vanishing.
                float targetAlpha = fresh ? 1f : 0f;
                _shownAlpha[handle.Id] = Mathf.Lerp(
                    _shownAlpha[handle.Id], targetAlpha, chase);

                if (fresh)
                {
                    _shownDrive[handle.Id] = Mathf.Lerp(
                        _shownDrive[handle.Id], intent.Drive, chase);

                    _shownSteer[handle.Id] = Mathf.Lerp(
                        _shownSteer[handle.Id], intent.Steer, chase);
                }

                float alpha = _shownAlpha[handle.Id];
                float drive = _shownDrive[handle.Id];

                if (alpha < 0.02f || Mathf.Abs(drive) < MIN_DRIVE_TO_DRAW)
                {
                    continue;
                }

                AppendArrow(handle, intent, drive, _shownSteer[handle.Id], alpha);
            }
        }

        private bool IsFresh(Systems_PlayerIntent intent)
        {
            int age = _play.PhysicsTick - intent.Tick;
            return age >= 0 && age <= MAX_STALE_TICKS;
        }

        /// <summary>
        /// The arc. Drive sets the length and which way the arrow points; steer
        /// bends it.
        ///
        /// A NEGATIVE DRIVE POINTS BACKWARDS AND STILL BENDS THE SAME WAY. Drive is
        /// a force along the body's up axis, so reversing it reverses the arrow —
        /// but the steer torque turns the body in world space regardless, so a
        /// backpedalling defender at positive steer is still rotating
        /// counter-clockwise. Bending the reversed arrow by the same world angle is
        /// what makes it read as one body turning rather than as two unrelated
        /// controls.
        /// </summary>
        private void AppendArrow(
            Systems_IPlayerHandle handle,
            Systems_PlayerIntent intent,
            float drive,
            float steer,
            float alpha)
        {
            Vector2 facing = intent.Facing.sqrMagnitude < 1e-4f
                ? Vector2.up
                : intent.Facing.normalized;

            float magnitude = Mathf.Abs(drive);
            Vector2 direction = drive < 0f ? -facing : facing;

            float length = magnitude * MAX_ARROW_LENGTH;
            float step = length / ARC_SEGMENTS;

            float turnPerSegment =
                steer * MAX_ARC_DEGREES * Mathf.Deg2Rad / ARC_SEGMENTS;

            float turnCos = Mathf.Cos(turnPerSegment);
            float turnSin = Mathf.Sin(turnPerSegment);

            Color32 color = ArrowColor(handle, magnitude, alpha);

            Vector2 cursor = handle.Position + (direction * BODY_RADIUS);

            for (int segment = 0; segment < ARC_SEGMENTS; segment++)
            {
                Vector2 next = cursor + (direction * step);

                // Tapers along the shaft. A constant-width bar reads as a ruler; a
                // taper reads as a vector.
                float fromWidth = Mathf.Lerp(
                    SHAFT_WIDTH,
                    SHAFT_WIDTH * 0.55f,
                    segment / (float)ARC_SEGMENTS);

                float toWidth = Mathf.Lerp(
                    SHAFT_WIDTH,
                    SHAFT_WIDTH * 0.55f,
                    (segment + 1) / (float)ARC_SEGMENTS);

                AppendTaperedQuad(cursor, next, fromWidth, toWidth, color, color);

                cursor = next;
                direction = Rotate(direction, turnCos, turnSin);
            }

            AppendHead(cursor, direction, HEAD_LENGTH * magnitude, color);
        }

        /// <summary>
        /// The body's own colour, lifted toward white by how hard the player is
        /// driving so a full-effort arrow is brighter than a coasting one, and
        /// faded by the alpha the caller has been chasing.
        ///
        /// THE LIFT IS DELIBERATELY SMALL, AND IT WAS NOT AT FIRST. It started at
        /// 0.25 + 0.45x, which puts a full-drive arrow seventy per cent of the way
        /// to white — and a screen capture of an actual snap showed why that is the
        /// wrong number. Every lineman fires off the line at full drive, so the
        /// entire interior of the formation went white at once and the one thing
        /// the colour is carrying, which side each arrow belongs to, was gone in
        /// exactly the frame where the play is hardest to read. Effort is already
        /// encoded twice over, in the arrow's length and its alpha; brightness only
        /// has to reinforce it, so it now tops out at forty-five per cent and the
        /// team colour survives.
        /// </summary>
        private Color32 ArrowColor(
            Systems_IPlayerHandle handle, float magnitude, float alpha)
        {
            SpriteRenderer body = _bodies[handle.Id];
            Color baseColor = body == null ? Color.white : body.color;

            Color lifted = Color.Lerp(
                baseColor, Color.white, 0.15f + (0.30f * magnitude));

            lifted.a = alpha * (0.45f + (0.5f * magnitude));

            return lifted;
        }

        /// <summary>
        /// The quarterback's read: a dashed line from the passer to the point the
        /// ball would be thrown at right now.
        ///
        /// THE TARGET COMES FROM Systems_BallSystem.ResolveThrowTarget, not from a
        /// copy of its rule. The aim vector is a direction of INTENT that the ball
        /// system resolves to whichever eligible receiver sits closest to the aim
        /// ray, led for the flight time — a rule that has already been retuned once
        /// (see that method) and that a duplicate here would silently desynchronise
        /// from the next time it moves. Asking the system that will throw the ball
        /// is the only way this line stays true.
        ///
        /// It is dimmed rather than hidden when the throw is not currently legal —
        /// past the throw window, or with the quarterback scrambled across the line
        /// of scrimmage. A policy that keeps asking to throw from ten yards
        /// downfield is worth being able to see.
        /// </summary>
        private void AppendQuarterbackRead()
        {
            if (!_play.Call.IsPass() || _ball == null || !_ball.IsHeld)
            {
                return;
            }

            Systems_IPlayerHandle passer = FindAimingCarrier();

            if (passer == null)
            {
                return;
            }

            Systems_PlayerIntent intent = _intent.Read(passer.Id);

            if (!IsFresh(intent))
            {
                return;
            }

            bool legal = _play.PhysicsTick <= Systems_SimConstants.THROW_WINDOW_TICKS
                && passer.Position.y <= _play.LineOfScrimmageY;

            // Before the target test, not after: the clock is about the
            // quarterback, and it keeps running whether or not he has found anyone.
            if (legal)
            {
                AppendPocketClock(passer, intent.ThrowArmed);
            }

            float speed = Systems_BallSystem.ThrowSpeedFor(
                Mathf.Clamp01(intent.Aim.magnitude));

            Systems_IPlayerHandle target = _ballSystem.ResolveThrowTarget(
                passer, intent.Aim, speed, out Vector2 _);

            if (target == null)
            {
                return;
            }

            Color color = Systems_UiTheme.Accent;

            // Armed means the policy asked to release on this decision step. A
            // brighter, fatter line then is what turns "the quarterback is looking
            // here" into "the quarterback is throwing here", one frame before the
            // ball leaves.
            color.a = (legal ? 1f : READ_LINE_BLOCKED_ALPHA)
                * (intent.ThrowArmed ? 1f : 0.65f);

            float width = READ_LINE_WIDTH * (intent.ThrowArmed ? 1.6f : 1f);

            Vector2 from = passer.Position;
            Vector2 to = Systems_BallSystem.LeadPoint(from, target, speed);

            AppendDashedLine(from, to, width, color);
        }

        /// <summary>
        /// The pocket clock: a ring round the quarterback that drains as the throw
        /// window closes and reddens as the rush arrives.
        ///
        /// BOTH HALVES ARE THINGS THE SIMULATION ALREADY ENFORCES AND NEVER SHOWED.
        /// Systems_SimConstants.THROW_WINDOW_TICKS is a hard rule — past it the
        /// release is refused and the quarterback is a runner — and nothing on
        /// screen said it existed, so a throw that came out at the last tick and
        /// one that came out in rhythm looked the same, and a quarterback who held
        /// the ball until the window shut simply appeared to give up. The pressure
        /// is the distance to the nearest man on the other side, which is the thing
        /// every sack is a failure to read.
        ///
        /// Together they are the tension of a passing down in one shape: the ring
        /// is how long he has, the colour is how long he actually has.
        ///
        /// It drains CLOCKWISE FROM THE TOP, because that is how every clock a
        /// viewer has ever seen runs down. Top is +Y, which is downfield on every
        /// snap for the reason Systems_BroadcastCameraView gives.
        ///
        /// Appended to the same mesh as the read line, so it is no extra draw call.
        /// </summary>
        private void AppendPocketClock(Systems_IPlayerHandle passer, bool armed)
        {
            float remaining = 1f - Mathf.Clamp01(
                _play.PhysicsTick / (float)Systems_SimConstants.THROW_WINDOW_TICKS);

            int segments = Mathf.CeilToInt(POCKET_RING_SEGMENTS * remaining);

            if (segments <= 0)
            {
                return;
            }

            float pressure = 1f - Mathf.InverseLerp(
                PRESSURE_NEAR, PRESSURE_FAR, NearestOpponentDistance(passer));

            Color color = Color.Lerp(
                Systems_UiTheme.Accent, Systems_UiTheme.Negative, pressure);

            color.a = 0.55f + (0.35f * pressure);

            // Same emphasis the read line gets on the decision step that asks for
            // the release.
            float width = POCKET_RING_WIDTH * (armed ? 1.6f : 1f);

            Color32 packed = color;
            Vector2 centre = passer.Position;

            // Negative: the sweep runs clockwise. The chords are resized to fit the
            // sweep exactly rather than drawn at a fixed angle, so the arc ends
            // where the fraction says and not at the next whole segment — a fixed
            // step would make the ring tick down forty times instead of draining.
            float sweep = -2f * Mathf.PI * remaining;
            float step = sweep / segments;

            Vector2 from = centre + (Vector2.up * POCKET_RING_RADIUS);

            for (int segment = 1; segment <= segments; segment++)
            {
                float angle = (Mathf.PI * 0.5f) + (step * segment);

                Vector2 to = centre + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle))
                    * POCKET_RING_RADIUS);

                AppendTaperedQuad(from, to, width, width, packed, packed);

                from = to;
            }
        }

        /// <summary>
        /// Metres from the passer to the closest player on the other side. One scan
        /// of the roster per frame, and only on a passing down with the ball still
        /// in the quarterback's hands.
        /// </summary>
        private float NearestOpponentDistance(Systems_IPlayerHandle passer)
        {
            float nearestSqr = PRESSURE_FAR * PRESSURE_FAR;

            for (int slotIndex = 0;
                 slotIndex < Systems_PlayerRegistry.CAPACITY;
                 slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                if (handle == null || handle.Side == passer.Side)
                {
                    continue;
                }

                float sqrDistance = (handle.Position - passer.Position).sqrMagnitude;

                if (sqrDistance < nearestSqr)
                {
                    nearestSqr = sqrDistance;
                }
            }

            return Mathf.Sqrt(nearestSqr);
        }

        /// <summary>
        /// The one player carrying the ball that also has the quarterback's aim
        /// slots. Both conditions matter: a halfback can carry, and the offense's
        /// quarterback can be standing there having already handed off.
        /// </summary>
        private Systems_IPlayerHandle FindAimingCarrier()
        {
            for (int slotIndex = 0;
                 slotIndex < Systems_PlayerRegistry.CAPACITY;
                 slotIndex++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slotIndex);

                if (handle == null || !handle.IsCarrier)
                {
                    continue;
                }

                if (_intent.Read(handle.Id).HasAim)
                {
                    return handle;
                }
            }

            return null;
        }

        // --- Geometry ---------------------------------------------------------

        private static Vector2 Rotate(Vector2 value, float cos, float sin)
        {
            return new Vector2(
                (value.x * cos) - (value.y * sin),
                (value.x * sin) + (value.y * cos));
        }

        private void AppendDashedLine(
            Vector2 from, Vector2 to, float width, Color color)
        {
            Vector2 span = to - from;
            float distance = span.magnitude;

            if (distance < 1e-3f)
            {
                return;
            }

            Vector2 direction = span / distance;
            float stride = READ_DASH_LENGTH + READ_GAP_LENGTH;
            Color32 packed = color;

            for (float travelled = 0f; travelled < distance; travelled += stride)
            {
                float dashEnd = Mathf.Min(travelled + READ_DASH_LENGTH, distance);

                AppendTaperedQuad(
                    from + (direction * travelled),
                    from + (direction * dashEnd),
                    width,
                    width,
                    packed,
                    packed);
            }

            // A solid pip on the receiver, so the eye lands on WHO rather than on
            // the line that got there.
            AppendHead(to - (direction * HEAD_LENGTH), direction, HEAD_LENGTH, packed);
        }

        private void AppendTaperedQuad(
            Vector2 from,
            Vector2 to,
            float fromWidth,
            float toWidth,
            Color32 fromColor,
            Color32 toColor)
        {
            Vector2 span = to - from;

            if (span.sqrMagnitude < 1e-6f)
            {
                return;
            }

            Vector2 normal = new Vector2(-span.y, span.x).normalized;

            Vector2 fromOffset = normal * (fromWidth * 0.5f);
            Vector2 toOffset = normal * (toWidth * 0.5f);

            int baseIndex = _vertices.Count;

            _vertices.Add(from - fromOffset);
            _vertices.Add(from + fromOffset);
            _vertices.Add(to + toOffset);
            _vertices.Add(to - toOffset);

            _colors.Add(fromColor);
            _colors.Add(fromColor);
            _colors.Add(toColor);
            _colors.Add(toColor);

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        private void AppendHead(
            Vector2 origin, Vector2 direction, float length, Color32 color)
        {
            if (length < 1e-3f)
            {
                return;
            }

            Vector2 normal = new Vector2(-direction.y, direction.x);
            Vector2 tip = origin + (direction * length);
            Vector2 flank = normal * (HEAD_WIDTH * 0.5f);

            int baseIndex = _vertices.Count;

            _vertices.Add(origin - flank);
            _vertices.Add(origin + flank);
            _vertices.Add(tip);

            _colors.Add(color);
            _colors.Add(color);
            _colors.Add(color);

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
        }

        /// <summary>
        /// Clear(false) BEFORE the new vertices go in, or a frame that shrinks the
        /// buffer hands Unity a triangle list still pointing past the end of it.
        /// The false keeps the vertex layout, so nothing is reallocated.
        /// </summary>
        private void Upload()
        {
            _mesh.Clear(false);

            if (_vertices.Count == 0)
            {
                _meshRenderer.enabled = false;
                return;
            }

            _meshRenderer.enabled = true;

            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_indices, 0, false);
            _mesh.bounds = FieldBounds;
        }
    }
}
