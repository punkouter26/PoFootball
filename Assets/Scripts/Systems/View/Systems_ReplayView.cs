using System;
using MessagePipe;
using PoFootball.Models;
using PoFootball.Systems;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace PoFootball.Views
{
    /// <summary>
    /// The broadcast truck: records every tick of live play, keeps the game's
    /// three best plays, and offers them on a scrubber at the final whistle.
    ///
    /// THERE IS NO INSTANT REPLAY ANY MORE. Through 2026-10-04 a big hit or a
    /// touchdown was run back in slow motion during the dead ball, by ghosts
    /// standing in for the real bodies. It was removed outright rather than
    /// switched off: at a 1.2 s dead ball it had 0.4 s of play to show, and the
    /// game is meant to re-form and snap. The tape and the ghosts stay, because
    /// the highlight reel is built from one and drawn by the other. Nothing here
    /// writes Time, moves the camera or touches any other global.
    ///
    /// RECORDING IS FIFTY WRITES OF A FEW FLOATS. Positions and headings are read
    /// off each player's Rigidbody2D (see Record for why not the transform), the
    /// ball off Systems_BallModel. Everything is preallocated — the live tape, three
    /// highlight tapes and the ghosts — so a tick of play allocates nothing, and
    /// none of it exists when Systems_PresentationBudget says no. This is Game
    /// mode only by that same gate: a trainer never pays for a recorder.
    ///
    /// THE UI IS ITS OWN DOCUMENT, above Systems_HudView's and below the status
    /// HUD's, so neither of those is edited: the highlight reel docked at the
    /// bottom at the whistle (Systems_HighlightReel explains the docking).
    ///
    /// THE HIGHLIGHTS PLAY ON THE FIELD, WITH THE GHOSTS. While the reel is open this view
    /// shows the ghosts, poses them from the reel's cursor every frame, and
    /// publishes Systems_HighlightPlaybackMessage with the ghost ball's position.
    /// Systems_HudView takes the final overlay down on that, so the field can be
    /// seen, and Systems_BroadcastCameraView frames the ghost ball rather than
    /// the real one lying at the last play's spot. When the reel closes, or this
    /// view is disabled or destroyed, the ghosts hand back to the real bodies and
    /// one IsPlaying = false goes out, which brings the overlay back. Nothing here
    /// touches the overlay or the camera itself: views do not reach into views.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class Systems_ReplayView : Systems_ScreenView, Systems_IInjectableView
    {
        /// <summary>
        /// Above Systems_HudView (0), below Systems_StatusHudView (300). The reel's button has to draw over the
        /// final scrim; the five-corner chrome has to draw over the reel.
        /// </summary>
        private const float PANEL_SORT_ORDER = 200f;

        /// <summary>Six seconds of play at the pinned 50 Hz — the longest highlight kept.</summary>
        private const int TAPE_TICKS = 300;

        private const int HIGHLIGHT_COUNT = 3;

        /// <summary>Half a second. A kick resolves on the tick it is called and is never one.</summary>
        private const int MIN_HIGHLIGHT_TICKS = 25;

        /// <summary>See Systems_ReplayHighlight for the scoring and why it is this simple.</summary>
        private const float TOUCHDOWN_BONUS = 20f;
        private const float TAKEAWAY_BONUS = 12f;

        private Systems_PresentationBudget _budget;
        private Systems_PlayModel _play;
        private Systems_BallModel _ball;
        private Systems_PlayerRegistry _registry;

        private ISubscriber<Systems_TackleMessage> _tackleSubscriber;

        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;
        private ISubscriber<Systems_GameOverMessage> _gameOverSubscriber;
        private IPublisher<Systems_HighlightPlaybackMessage> _highlightPublisher;

        private IDisposable _tackleSubscription;

        private IDisposable _resolvedSubscription;
        private IDisposable _snappedSubscription;
        private IDisposable _gameOverSubscription;

        private Systems_IPlayerHandle[] _handles;
        private SpriteRenderer[] _renderers;
        private Transform[] _transforms;
        private Rigidbody2D[] _bodies;
        private int _playerCount;
        private SpriteRenderer _ballRenderer;

        private Systems_ReplayTape _tape;
        private Systems_ReplayHighlight[] _highlights;
        private Systems_ReplayGhosts _ghosts;

        private VisualElement _layer;

        private Systems_HighlightReel _reel;

        /// <summary>Ticks recorded since the last snap, so a highlight never reaches into the previous play.</summary>
        private int _playFrames;

        /// <summary>Hardest tackle of the current play, m/s. Feeds the highlight score.</summary>
        private float _playPeakClosing;

        private bool _gameOver;

        /// <summary>
        /// The colours the offense and defense units wore when the current play
        /// was snapped, handed to a highlight if the play becomes one. Read at the
        /// snap because by the time a down is resolved Systems_RoleShapeApplier
        /// may already have repainted the field for the next possession — it
        /// repaints on the same message this view keeps highlights on.
        /// </summary>
        private bool _playHasColors;
        private Color _playOffenseColor;
        private Color _playDefenseColor;

        /// <summary>True while the reel's highlight is drawn by the ghosts.</summary>
        private bool _highlightOnField;

        /// <summary>The highlight the ghosts were last repainted for, so a repaint happens once per highlight.</summary>
        private Systems_ReplayHighlight _paintedHighlight;

        [Inject]
        public void Construct(
            Systems_PresentationBudget budget,
            Systems_PlayModel play,
            Systems_BallModel ball,
            Systems_PlayerRegistry registry,
            ISubscriber<Systems_TackleMessage> tackleSubscriber,

            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber,
            ISubscriber<Systems_GameOverMessage> gameOverSubscriber,
            IPublisher<Systems_HighlightPlaybackMessage> highlightPublisher)
        {
            _budget = budget;
            _play = play;
            _ball = ball;
            _registry = registry;
            _tackleSubscriber = tackleSubscriber;

            _resolvedSubscriber = resolvedSubscriber;
            _snappedSubscriber = snappedSubscriber;
            _gameOverSubscriber = gameOverSubscriber;
            _highlightPublisher = highlightPublisher;
        }

        protected override void Awake()
        {
            base.Awake();

            if (TryGetComponent(out UIDocument document))
            {
                document.sortingOrder = PANEL_SORT_ORDER;
            }
        }

        /// <summary>
        /// Gated before anything is built — the tapes, the ghosts and the UI
        /// alike. "Do not construct", per Systems_PresentationBudget, not "do not
        /// play".
        /// </summary>
        protected override void Start()
        {
            if (_budget == null || !_budget.EffectsEnabled
                || _play == null || _ball == null || _registry == null)
            {
                enabled = false;
                return;
            }

            if (!CollectBodies())
            {
                enabled = false;
                return;
            }

            _tape = new Systems_ReplayTape(TAPE_TICKS, _playerCount);
            _highlights = new Systems_ReplayHighlight[HIGHLIGHT_COUNT];

            for (int slot = 0; slot < HIGHLIGHT_COUNT; slot++)
            {
                _highlights[slot] = new Systems_ReplayHighlight(TAPE_TICKS, _playerCount);
            }

            _ghosts = new Systems_ReplayGhosts(_handles, _renderers, _playerCount, _ballRenderer);

            // For the opening play, in case its snap is published before this
            // subscribes. Systems_RoleShapeApplier (order -100) has painted the
            // kickoff colours in its own Start by now.
            _playHasColors = ReadTeamColors(out _playOffenseColor, out _playDefenseColor);

            base.Start();

            _tackleSubscription = _tackleSubscriber?.Subscribe(OnTackle);

            _resolvedSubscription = _resolvedSubscriber?.Subscribe(OnDownResolved);
            _snappedSubscription = _snappedSubscriber?.Subscribe(OnSnapped);
            _gameOverSubscription = _gameOverSubscriber?.Subscribe(OnGameOver);
        }

        private void FixedUpdate()
        {
            if (!_gameOver && _play.Phase == Systems_PlayPhase.Live)
            {
                Record();
            }
        }

        /// <summary>
        /// The reel runs on wall-clock time, so its highlight is posed here rather
        /// than in LateUpdate — and here is also early
        /// enough that the message reaches Systems_BroadcastCameraView before its
        /// LateUpdate frames this same frame.
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (_reel == null)
            {
                return;
            }

            _reel.Tick(Time.unscaledDeltaTime);

            if (_reel.IsOpen)
            {
                ShowHighlight();
            }
            else
            {
                StopHighlight();
            }
        }

        /// <summary>
        /// After Systems_BallView (order 0) has positioned and re-enabled the real
        /// ball for this frame, which is why this class runs at a positive order.
        /// </summary>
        private void LateUpdate()
        {
            if (_highlightOnField)
            {
                _ghosts.EnforceHidden();
            }
        }

        private void OnDisable()
        {
            // A disabled view must not leave the real players hidden, nor the
            // final overlay down with nothing on the field to watch.
            if (_reel != null)
            {
                _reel.Close();
            }

            StopHighlight();
        }

        private void OnDestroy()
        {
            _tackleSubscription?.Dispose();
            _tackleSubscription = null;


            _resolvedSubscription?.Dispose();
            _resolvedSubscription = null;

            _snappedSubscription?.Dispose();
            _snappedSubscription = null;

            _gameOverSubscription?.Dispose();
            _gameOverSubscription = null;

            // Normally already done by OnDisable; idempotent if so.
            StopHighlight();

            if (_ghosts != null)
            {
                _ghosts.Destroy();
                _ghosts = null;
            }
        }

        // --- UI -----------------------------------------------------------------

        protected override void BuildUi()
        {
            _layer = Systems_UiTheme.Layer();
            Root.Add(_layer);
        }

        // --- Recording ------------------------------------------------------------

        /// <summary>
        /// Walks the registry rather than the scene, as Systems_StadiumRigView does:
        /// the registry is the project's answer to "who is a player", and its slot
        /// order is the formation order, so tape index N is the same body all game.
        /// </summary>
        private bool CollectBodies()
        {
            int capacity = Systems_PlayerRegistry.CAPACITY;

            _handles = new Systems_IPlayerHandle[capacity];
            _renderers = new SpriteRenderer[capacity];
            _transforms = new Transform[capacity];
            _bodies = new Rigidbody2D[capacity];
            _playerCount = 0;

            for (int slot = 0; slot < capacity; slot++)
            {
                Systems_IPlayerHandle handle = _registry.Get(slot);

                if (!(handle is MonoBehaviour behaviour))
                {
                    continue;
                }

                if (!behaviour.TryGetComponent(out SpriteRenderer renderer))
                {
                    continue;
                }

                behaviour.TryGetComponent(out Rigidbody2D body);

                _handles[_playerCount] = handle;
                _renderers[_playerCount] = renderer;
                _transforms[_playerCount] = behaviour.transform;
                _bodies[_playerCount] = body;
                _playerCount++;
            }

            if (_playerCount == 0)
            {
                Debug.LogWarning(
                    $"{nameof(Systems_ReplayView)}: found no players to record. Highlights are off.");
                return false;
            }

            // Once, at setup. The ball view is not in the container — it is a
            // scene component like this one — and its renderer is the only thing
            // wanted from it.
            Systems_BallView[] ballViews =
                FindObjectsByType<Systems_BallView>(FindObjectsInactive.Include);

            if (ballViews.Length > 0)
            {
                ballViews[0].TryGetComponent(out _ballRenderer);
            }

            if (_ballRenderer == null)
            {
                Debug.LogWarning(
                    $"{nameof(Systems_ReplayView)}: no Systems_BallView with a SpriteRenderer. "
                    + "Highlights will show the players without the ball.");
            }

            return true;
        }

        /// <summary>One tick of live play onto the tape. Allocates nothing.</summary>
        private void Record()
        {
            int slot = _tape.Append();
            int carrier = Systems_ReplayTape.NO_CARRIER;

            for (int player = 0; player < _playerCount; player++)
            {
                // The body, not the transform. Systems_EpisodeDirector re-forms the
                // teams by writing Rigidbody2D.position, and the transform does not
                // follow until the next physics step — so on the snap tick a
                // transform still reads the previous play's whistle spot, and every
                // highlight would open with the formation teleporting into place.
                // Read-only: nothing here writes to a body.
                Rigidbody2D body = _bodies[player];

                if (body != null)
                {
                    _tape.WritePlayer(slot, player, body.position, body.rotation);
                }
                else if (_transforms[player] != null)
                {
                    Transform fallback = _transforms[player];
                    Vector3 position = fallback.position;
                    _tape.WritePlayer(
                        slot, player, new Vector2(position.x, position.y),
                        fallback.eulerAngles.z);
                }

                // The agent's latched flag rather than the ball model's holder: the
                // thrower stays highlighted while the pass is in the air, and the
                // replay should look the way the play did.
                if (carrier == Systems_ReplayTape.NO_CARRIER && _handles[player].IsCarrier)
                {
                    carrier = player;
                }
            }

            _tape.WriteCarrier(slot, carrier);
            _tape.WriteBall(slot, _ball.Position, _ball.Height);

            if (_playFrames < TAPE_TICKS)
            {
                _playFrames++;
            }
        }

        // --- Messages -------------------------------------------------------------

        private void OnTackle(Systems_TackleMessage message)
        {
            _playPeakClosing = Mathf.Max(_playPeakClosing, message.ClosingSpeed);
        }

        /// <summary>
        /// The play is over and the game has decided what it meant. Published
        /// synchronously from the whistle — possibly inside a collision callback —
        /// so this only copies.
        /// The tape is not appended to again until the next snap, so its newest
        /// frames are exactly this play's.
        /// </summary>
        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            if (_gameOver)
            {
                return;
            }

            ConsiderHighlight(message);
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            _playPeakClosing = 0f;
            _playFrames = 0;

            _playHasColors = ReadTeamColors(out _playOffenseColor, out _playDefenseColor);
        }

        /// <summary>
        /// The final play's Systems_DownResolvedMessage is published before this
        /// one, so it has already been considered for the reel.
        /// </summary>
        private void OnGameOver(Systems_GameOverMessage message)
        {
            _gameOver = true;

            BuildReel();
        }

        // --- Highlights -----------------------------------------------------------

        /// <summary>
        /// Keeps this play if it beats the weakest of the three held. The copy is
        /// a handful of Array.Copy calls into a tape allocated at Start.
        /// </summary>
        private void ConsiderHighlight(Systems_DownResolvedMessage message)
        {
            if (IsKick(message.Outcome))
            {
                return;
            }

            int frames = Mathf.Min(_playFrames, _tape.Count);

            if (frames < MIN_HIGHLIGHT_TICKS)
            {
                return;
            }

            float score = ScoreOf(message);

            int target = -1;
            float weakest = float.MaxValue;

            for (int slot = 0; slot < HIGHLIGHT_COUNT; slot++)
            {
                Systems_ReplayHighlight held = _highlights[slot];

                if (!held.IsFilled)
                {
                    target = slot;
                    break;
                }

                if (held.Score < weakest)
                {
                    weakest = held.Score;
                    target = slot;
                }
            }

            if (target < 0 || (_highlights[target].IsFilled && score <= weakest))
            {
                return;
            }

            _highlights[target].Fill(
                _tape, frames, score, message,
                _playHasColors, _playOffenseColor, _playDefenseColor);
        }

        /// <summary>
        /// The base colour of each unit as the field is painted right now: the
        /// first player on each side who is not carrying, because the carrier is
        /// under Agent_FootballPlayer's white highlight. False if either side has
        /// no such player, in which case a highlight keeps the real renderers'
        /// look. Twenty-two reads, no allocation.
        /// </summary>
        private bool ReadTeamColors(out Color offenseColor, out Color defenseColor)
        {
            offenseColor = Color.white;
            defenseColor = Color.white;

            bool foundOffense = false;
            bool foundDefense = false;

            for (int player = 0; player < _playerCount; player++)
            {
                Systems_IPlayerHandle handle = _handles[player];
                SpriteRenderer renderer = _renderers[player];

                if (handle == null || renderer == null || handle.IsCarrier)
                {
                    continue;
                }

                if (handle.Side == Systems_TeamSide.Offense)
                {
                    if (!foundOffense)
                    {
                        offenseColor = renderer.color;
                        foundOffense = true;
                    }
                }
                else if (!foundDefense)
                {
                    defenseColor = renderer.color;
                    foundDefense = true;
                }

                if (foundOffense && foundDefense)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>See Systems_ReplayHighlight for what this adds up and why.</summary>
        private float ScoreOf(Systems_DownResolvedMessage message)
        {
            float score = Mathf.Abs(message.YardsGained) + _playPeakClosing;

            if (message.Outcome == Systems_PlayOutcome.Touchdown)
            {
                score += TOUCHDOWN_BONUS;
            }

            if (message.Outcome == Systems_PlayOutcome.Interception
                || message.Outcome == Systems_PlayOutcome.FumbleLost
                || message.Outcome == Systems_PlayOutcome.Safety)
            {
                score += TAKEAWAY_BONUS;
            }

            return score;
        }

        private static bool IsKick(Systems_PlayOutcome outcome)
        {
            return outcome == Systems_PlayOutcome.Punt
                || outcome == Systems_PlayOutcome.FieldGoalGood
                || outcome == Systems_PlayOutcome.FieldGoalMissed;
        }

        /// <summary>
        /// Once, at the final whistle: orders the held highlights best first and
        /// builds the reel. The only allocation outside Start, and it happens when
        /// the game is already over.
        /// </summary>
        private void BuildReel()
        {
            if (_reel != null || _layer == null)
            {
                return;
            }

            Systems_ReplayHighlight[] ordered = new Systems_ReplayHighlight[HIGHLIGHT_COUNT];
            int count = 0;

            for (int slot = 0; slot < HIGHLIGHT_COUNT; slot++)
            {
                if (_highlights[slot].IsFilled)
                {
                    ordered[count] = _highlights[slot];
                    count++;
                }
            }

            if (count == 0)
            {
                return;
            }

            // Insertion sort, descending. Three elements.
            for (int index = 1; index < count; index++)
            {
                Systems_ReplayHighlight current = ordered[index];
                int scan = index - 1;

                while (scan >= 0 && ordered[scan].Score < current.Score)
                {
                    ordered[scan + 1] = ordered[scan];
                    scan--;
                }

                ordered[scan + 1] = current;
            }

            _reel = new Systems_HighlightReel(ordered, count);

            _layer.Insert(0, _reel.Root);
            _reel.Show();
        }

        /// <summary>
        /// One frame of the open reel on the field: take over from the real bodies
        /// the first time, repaint for the highlight's possession when the
        /// highlight changes, pose at the reel's cursor, and tell the HUD and the
        /// camera where the ghost ball is. Allocates nothing per frame.
        /// </summary>
        private void ShowHighlight()
        {
            if (_ghosts == null)
            {
                return;
            }

            Systems_ReplayHighlight highlight = _reel.Current;

            if (!_highlightOnField)
            {
                _ghosts.Show();
                _highlightOnField = true;
                _paintedHighlight = null;
            }

            if (highlight != _paintedHighlight)
            {
                _paintedHighlight = highlight;

                if (highlight.HasTeamColors)
                {
                    _ghosts.PaintSides(highlight.OffenseColor, highlight.DefenseColor);
                }
            }

            float cursor = _reel.Cursor;
            _ghosts.Pose(highlight.Tape, cursor);

            // The plane position, not the drawn one: the ghost ball is lifted up
            // the screen by its height, and the camera frames the real ball by
            // Systems_BallModel.Position, which is not.
            highlight.Tape.SampleBall(cursor, out Vector2 focus, out float _);

            if (_highlightPublisher != null)
            {
                _highlightPublisher.Publish(new Systems_HighlightPlaybackMessage(true, focus));
            }
        }

        /// <summary>
        /// Every way a highlight leaves the field comes through here: the reel
        /// closing, OnDisable and OnDestroy. Publishes the one IsPlaying = false,
        /// and only on the edge, so the final overlay is not re-shown every frame
        /// the reel sits closed. Idempotent.
        /// </summary>
        private void StopHighlight()
        {
            if (!_highlightOnField)
            {
                return;
            }

            _highlightOnField = false;
            _paintedHighlight = null;

            if (_ghosts != null)
            {
                _ghosts.Hide();
            }

            if (_highlightPublisher != null)
            {
                _highlightPublisher.Publish(
                    new Systems_HighlightPlaybackMessage(false, Vector2.zero));
            }
        }
    }
}
