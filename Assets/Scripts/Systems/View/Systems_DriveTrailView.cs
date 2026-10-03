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
    /// The drive, drawn on the grass it was played on: a spine up the middle from
    /// where the drive began to where the chains are now, and one line per play
    /// from the snap to the spot the ball died.
    ///
    /// WHAT IT SHOWS THAT NOTHING ELSE DOES. Every other mark on the field is about
    /// THIS play — the carrier glow, the read line, the turf under a cut. A drive
    /// is the unit football is actually scored in, and it had no picture at all:
    /// a twelve-play march and a one-play bomb arrived at the same yard line
    /// looking identical, and the only record of how either got there was a row
    /// of numbers in the box score. The trail is that record left where it
    /// happened. Short ribs stacked close together are a ground game grinding;
    /// one long rib is the play that broke it open; a red one is a loss.
    ///
    /// EACH RIB STARTS ON THE SPINE, BECAUSE EACH PLAY DID. The ball is re-spotted
    /// in the middle of the field for every snap (Systems_EpisodeDirector resets
    /// the formation about x = 0), so a play runs from the centre line to wherever
    /// the carrier was brought down, and the next one starts back on the centre
    /// line. Joining the dead-ball spots into one continuous path would be the
    /// prettier drawing and would show a route the ball never took.
    ///
    /// IT IS CLEARED ON THE SNAP AFTER POSSESSION CHANGES, NOT AT THE WHISTLE.
    /// Systems_GameFlowSystem mirrors the field through y -> -y for the new
    /// offense, so a trail left up across that mirror would be lying about which
    /// end the drive went toward. But the last play of a drive is usually the one
    /// worth looking at — the touchdown run, the fourth-down stop — and the
    /// dead-ball hold is the only time a viewer has to look at it. So the finished
    /// drive stays through the hold and goes when the new formation appears.
    ///
    /// REBUILT WHEN A PLAY ENDS AND AT NO OTHER TIME. There is no Update here. The
    /// mesh changes once per down, so it is built in the message handler and then
    /// sits in a static buffer for the whole of the next play: one draw call, and
    /// nothing at all per frame.
    ///
    /// Gated on Systems_PresentationBudget like every other decoration, and in
    /// SCN_GAME only — it reads Systems_GameModel, which training does not
    /// register.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Systems_DriveTrailView : MonoBehaviour, Systems_IInjectableView
    {
        /// <summary>
        /// With the turf scuffs: on the ground, above the field quad on 0. The
        /// offense's bodies are on 1 as well, which is what
        /// <see cref="DEPTH_BEHIND_PLAYERS"/> is for.
        /// </summary>
        private const int SORTING_ORDER = 1;

        /// <summary>
        /// Metres the mesh sits behind the plane of play. Renderers that share a
        /// sorting order are drawn back to front, so this is what keeps a rib from
        /// being painted across the player standing on it.
        /// </summary>
        private const float DEPTH_BEHIND_PLAYERS = 0.05f;

        /// <summary>
        /// Plays kept. A drive of more than twenty is close to unheard of; past
        /// this the oldest rib is dropped, and by then it has faded to the floor
        /// anyway.
        /// </summary>
        private const int MAX_PLAYS = 24;

        private const float SPINE_WIDTH = 0.14f;

        /// <summary>
        /// Thin on purpose. Short gains pile their ribs into the same few yards the
        /// next snap is lined up on, so anything as wide as a body turns the line
        /// of scrimmage into a tangle of bars with players standing in it.
        /// </summary>
        private const float RIB_WIDTH = 0.2f;

        /// <summary>Width of the tick across the spine where a play was snapped.</summary>
        private const float TICK_LENGTH = 1.3f;

        private const float TICK_WIDTH = 0.14f;

        /// <summary>Half-diagonal of the diamond where a play ended.</summary>
        private const float PIP_RADIUS = 0.36f;

        private const float SPINE_ALPHA = 0.2f;

        /// <summary>
        /// Alpha of the play that just ended. 0.6 was tried first and measured
        /// against a capture: on a field this dark the newest rib was the
        /// brightest thing on screen after the ball, which is the wrong way round
        /// for a record of what has already happened.
        /// </summary>
        private const float NEWEST_ALPHA = 0.36f;

        /// <summary>Each play back from the newest is this fraction as strong.</summary>
        private const float ALPHA_FALLOFF = 0.78f;

        /// <summary>
        /// Faintest a rib gets. Above zero so the start of a long drive is still
        /// there to be seen — the length of the march is the point.
        /// </summary>
        private const float ALPHA_FLOOR = 0.14f;

        /// <summary>
        /// How far a team colour is lifted toward white. The jersey colours are
        /// chosen to read on a body; laid flat on dark grass at a quarter alpha
        /// they sink into it.
        /// </summary>
        private const float COLOR_LIFT = 0.35f;

        /// <summary>See Systems_IntentOverlayView.FieldBounds — same reasoning.</summary>
        private static readonly Bounds FieldBounds =
            new Bounds(Vector3.zero, new Vector3(240f, 240f, 1f));

        /// <summary>One play of the drive, as it will be drawn.</summary>
        private readonly struct Rib
        {
            public readonly Vector2 From;
            public readonly Vector2 To;

            public Rib(Vector2 from, Vector2 to)
            {
                From = from;
                To = to;
            }
        }

        private Systems_PlayModel _play;
        private Systems_GameModel _game;
        private Systems_PresentationBudget _budget;
        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;

        private IDisposable _resolvedSubscription;
        private IDisposable _snappedSubscription;

        private Mesh _mesh;
        private MeshRenderer _meshRenderer;

        private readonly List<Vector3> _vertices = new List<Vector3>(512);
        private readonly List<Color32> _colors = new List<Color32>(512);
        private readonly List<int> _indices = new List<int>(768);

        private readonly Rib[] _ribs = new Rib[MAX_PLAYS];
        private int _ribCount;

        /// <summary>False until the first play of a drive has resolved.</summary>
        private bool _driveOpen;

        private float _driveStartY;
        private float _driveHeadY;
        private Systems_TeamId _offense;

        /// <summary>Systems_GameModel.DriveIndex of the drive being drawn.</summary>
        private int _driveIndex;

        /// <summary>Set when the drive on screen is over; acted on at the next snap.</summary>
        private bool _clearOnSnap;

        [Inject]
        public void Construct(
            Systems_PlayModel play,
            Systems_GameModel game,
            Systems_PresentationBudget budget,
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
            _play = play;
            _game = game;
            _budget = budget;
            _resolvedSubscriber = resolvedSubscriber;
            _snappedSubscriber = snappedSubscriber;
        }

        private void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled
                || _play == null || _game == null || _resolvedSubscriber == null)
            {
                enabled = false;
                return;
            }

            if (!Systems_ParticleMaterial.TryLoad(
                    nameof(Systems_DriveTrailView), "No drive trail is drawn.", out Material source))
            {
                enabled = false;
                return;
            }

            BuildMesh(source);

            _driveIndex = _game.DriveIndex;

            _resolvedSubscription = _resolvedSubscriber.Subscribe(OnDownResolved);
            _snappedSubscription = _snappedSubscriber?.Subscribe(OnSnapped);
        }

        private void OnDestroy()
        {
            _resolvedSubscription?.Dispose();
            _snappedSubscription?.Dispose();

            if (_mesh != null)
            {
                Destroy(_mesh);
            }
        }

        private void BuildMesh(Material source)
        {
            _mesh = new Mesh { name = "PoFootball Drive Trail" };
            _mesh.MarkDynamic();

            GameObject host = new GameObject("DriveTrail");
            host.transform.SetParent(transform, false);
            host.transform.position = new Vector3(0f, 0f, DEPTH_BEHIND_PLAYERS);

            MeshFilter filter = host.AddComponent<MeshFilter>();
            filter.sharedMesh = _mesh;

            _meshRenderer = host.AddComponent<MeshRenderer>();

            // sharedMaterial, never material (.claude/rules/performance.md). Every
            // colour arrives through the vertex stream.
            _meshRenderer.sharedMaterial = source;
            _meshRenderer.sortingOrder = SORTING_ORDER;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;
            _meshRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            _meshRenderer.enabled = false;
        }

        /// <summary>
        /// Records the play that just ended and redraws.
        ///
        /// ON THE RESOLVED MESSAGE, NOT THE PLAY-ENDED ONE, although the spot is on
        /// the latter. Systems_GameFlowSystem publishes this from inside its own
        /// play-ended handler, after the chains have moved — so here the play model
        /// still describes the down that just finished and the game model already
        /// describes the next one, in a known order. Subscribing to play-ended
        /// directly would race the game flow for which handler runs first, and
        /// half the time the drive index read below would be a play stale.
        /// </summary>
        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            if (IsKick(message.Outcome))
            {
                // Resolved by the rules from the line of scrimmage; nobody carried
                // anything anywhere. It still ends the drive, below.
                CloseDriveIfOver();
                return;
            }

            Vector2 snap = new Vector2(0f, _play.LineOfScrimmageY);

            if (!_driveOpen)
            {
                _driveOpen = true;
                _driveStartY = snap.y;
                _driveHeadY = snap.y;
                _offense = message.Offense;
            }

            // A pass that fell incomplete or was picked off moved the BALL and not
            // the offense — the same distinction Systems_GameFlowSystem draws when
            // it credits the yards. Those plays keep their tick on the spine and
            // get no rib.
            bool carried = message.Outcome != Systems_PlayOutcome.Incompletion
                && message.Outcome != Systems_PlayOutcome.Interception;

            Vector2 spot = carried ? _play.DeadBallSpot : snap;

            AddRib(new Rib(snap, spot));

            bool driveOver = CloseDriveIfOver();

            // The spine reaches the next line of scrimmage while the drive is
            // alive. Once it is over the game model's line belongs to the other
            // team in the mirrored frame, so the head stops where the ball did.
            _driveHeadY = driveOver ? spot.y : _game.LineOfScrimmageY;

            Rebuild();
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            if (!_clearOnSnap)
            {
                return;
            }

            _clearOnSnap = false;
            _driveOpen = false;
            _ribCount = 0;

            Rebuild();
        }

        /// <summary>
        /// Whether the play that just resolved ended the drive, read off
        /// Systems_GameModel.DriveIndex rather than off the result.
        ///
        /// The index moves on every path that hands the ball over or kicks it off —
        /// a score, a turnover, a punt, halftime, an onside kick the kicking team
        /// recovers — because all of them go through GiveBallTo. A switch over
        /// Systems_DownResult would have to list them and would miss halftime,
        /// which arrives as EndOfQuarter and looks like any other period change.
        /// </summary>
        private bool CloseDriveIfOver()
        {
            if (_game.DriveIndex == _driveIndex)
            {
                return false;
            }

            _driveIndex = _game.DriveIndex;
            _clearOnSnap = true;
            return true;
        }

        private static bool IsKick(Systems_PlayOutcome outcome)
        {
            return outcome == Systems_PlayOutcome.Punt
                || outcome == Systems_PlayOutcome.FieldGoalGood
                || outcome == Systems_PlayOutcome.FieldGoalMissed;
        }

        private void AddRib(Rib rib)
        {
            if (_ribCount == MAX_PLAYS)
            {
                // Drop the oldest. Once a drive, at most, on a drive that long.
                Array.Copy(_ribs, 1, _ribs, 0, MAX_PLAYS - 1);
                _ribCount--;
            }

            _ribs[_ribCount] = rib;
            _ribCount++;
        }

        // --- Geometry ---------------------------------------------------------

        private void Rebuild()
        {
            _vertices.Clear();
            _colors.Clear();
            _indices.Clear();

            if (_driveOpen)
            {
                Color team = Color.Lerp(
                    Systems_UiTheme.ColorOf(_offense), Color.white, COLOR_LIFT);

                AppendSpine(team);

                for (int ribIndex = 0; ribIndex < _ribCount; ribIndex++)
                {
                    int age = _ribCount - 1 - ribIndex;

                    float alpha = Mathf.Max(
                        ALPHA_FLOOR, NEWEST_ALPHA * Mathf.Pow(ALPHA_FALLOFF, age));

                    AppendRib(_ribs[ribIndex], team, alpha);
                }
            }

            Upload();
        }

        private void AppendSpine(Color team)
        {
            Color color = team;
            color.a = SPINE_ALPHA;

            AppendQuad(
                new Vector2(0f, _driveStartY),
                new Vector2(0f, _driveHeadY),
                SPINE_WIDTH,
                color);
        }

        private void AppendRib(Rib rib, Color team, float alpha)
        {
            // Red for a play that went backwards — the colour the HUD already uses
            // for a bad result — and the team's own colour for everything else.
            bool loss = rib.To.y < rib.From.y;

            Color color = loss ? Systems_UiTheme.Negative : team;
            color.a = alpha;

            // The tick is the team's colour even on a loss: it marks where the
            // down was snapped, which is not a thing that went wrong.
            Color tick = team;
            tick.a = alpha;

            Vector2 halfTick = new Vector2(TICK_LENGTH * 0.5f, 0f);
            AppendQuad(rib.From - halfTick, rib.From + halfTick, TICK_WIDTH, tick);

            if ((rib.To - rib.From).sqrMagnitude < 1e-4f)
            {
                return;
            }

            AppendQuad(rib.From, rib.To, RIB_WIDTH, color);
            AppendPip(rib.To, color);
        }

        private void AppendQuad(Vector2 from, Vector2 to, float width, Color32 color)
        {
            Vector2 span = to - from;

            if (span.sqrMagnitude < 1e-6f)
            {
                return;
            }

            Vector2 offset = new Vector2(-span.y, span.x).normalized * (width * 0.5f);

            AppendFourCorners(from - offset, from + offset, to + offset, to - offset, color);
        }

        /// <summary>A diamond on the spot the ball died, so the eye lands on WHERE.</summary>
        private void AppendPip(Vector2 centre, Color32 color)
        {
            AppendFourCorners(
                centre + new Vector2(0f, -PIP_RADIUS),
                centre + new Vector2(-PIP_RADIUS, 0f),
                centre + new Vector2(0f, PIP_RADIUS),
                centre + new Vector2(PIP_RADIUS, 0f),
                color);
        }

        private void AppendFourCorners(
            Vector2 first, Vector2 second, Vector2 third, Vector2 fourth, Color32 color)
        {
            int baseIndex = _vertices.Count;

            _vertices.Add(first);
            _vertices.Add(second);
            _vertices.Add(third);
            _vertices.Add(fourth);

            _colors.Add(color);
            _colors.Add(color);
            _colors.Add(color);
            _colors.Add(color);

            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 1);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex);
            _indices.Add(baseIndex + 2);
            _indices.Add(baseIndex + 3);
        }

        /// <summary>
        /// Clear(false) before the new vertices go in, for the reason
        /// Systems_IntentOverlayView.Upload gives.
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
