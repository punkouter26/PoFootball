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
    /// The broadcast truck: records every tick of live play, runs the big moment
    /// back in slow motion during the dead ball, keeps the game's three best
    /// plays, and offers them on a scrubber at the final whistle.
    ///
    /// SLOW MOTION WITHOUT TOUCHING TIME. This project once had a slow-motion
    /// effect that lowered Time.timeScale, and it was removed because a
    /// presentation view writing that global stranded the Editor at 0.35 when a
    /// scene unloaded mid-effect (see Systems_BroadcastCameraView and
    /// Systems_ImpactView). Nothing here writes Time, moves the camera or touches
    /// any other global. The simulation keeps running at full speed underneath —
    /// it is a dead ball, so it is only counting down to the next snap — and the
    /// replay is a recording of positions drawn by stand-in renderers
    /// (Systems_ReplayGhosts) at 0.4 of a tick per tick. The real bodies are never
    /// moved; why that matters is in the ghosts' own note.
    ///
    /// THE REPLAY FITS THE DEAD BALL BY CONSTRUCTION. In a game the whistle is
    /// followed by one tick in which Systems_EpisodeDirector resolves it and then
    /// Systems_GameRules.DEAD_BALL_TICKS (140) more before it re-forms the
    /// formation and snaps. <see cref="REPLAY_SOURCE_TICKS"/> is derived from
    /// that count, the speed and a margin, so 52 recorded ticks (1.04 s of play)
    /// take 128 fixed ticks to show and the ghosts hand back to the real bodies
    /// about a dozen ticks before anything moves them. The snap message also stops a replay
    /// outright, so a shorter dead ball could cut one short but never leave the
    /// ghosts standing on a live play. Raise DEAD_BALL_TICKS and the replay grows
    /// with it; lower it and it shrinks.
    ///
    /// The clock that drives it is the fixed step, counted in FixedUpdate — the
    /// same clock the dead-ball countdown runs on — so it stays in step with the
    /// snap at any Sim Speed. LateUpdate only interpolates within the current
    /// tick for smooth drawing.
    ///
    /// WHAT COUNTS AS A BIG MOMENT. A tackle at or above
    /// Systems_SimConstants.FUMBLE_CLOSING_SPEED — the speed at which the fumble
    /// model starts rolling, so a replayed hit is one hard enough to have cost
    /// the ball — or a touchdown. Both are announced by the referee before the
    /// whistle resolves; the replay is armed when Systems_DownResolvedMessage
    /// arrives and started on the next FixedUpdate, never inside a collision
    /// callback.
    ///
    /// RECORDING IS FIFTY WRITES OF A FEW FLOATS. Positions and headings are read
    /// off each player's Rigidbody2D (see Record for why not the transform), the
    /// ball off Systems_BallModel. Everything is preallocated — the live tape, three
    /// highlight tapes and the ghosts — so a tick of play allocates nothing, and
    /// none of it exists when Systems_PresentationBudget says no. This is Game
    /// mode only by that same gate: a trainer never pays for a recorder.
    ///
    /// THE UI IS ITS OWN DOCUMENT, above Systems_HudView's and below the status
    /// HUD's, so neither of those is edited: a REPLAY tag in the bottom-left while
    /// a replay runs, and the highlight reel docked at the bottom at the whistle
    /// (Systems_HighlightReel explains the docking).
    ///
    /// THE HIGHLIGHTS PLAY ON THE FIELD, WITH THE SAME GHOSTS. After the final
    /// whistle nothing else uses them — the instant replay is stopped for good at
    /// OnGameOver — so the reel borrows them outright: while it is open this view
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

        /// <summary>Slow-motion rate: recorded ticks shown per fixed tick.</summary>
        private const float REPLAY_SPEED = 0.4f;

        /// <summary>
        /// Fixed ticks the replay leaves unused at the end of the dead ball, so the
        /// real bodies are back on screen before the director moves them.
        /// </summary>
        private const int REPLAY_EXIT_MARGIN_TICKS = 8;

        /// <summary>
        /// How much of the play the instant replay shows: as many recorded ticks
        /// as fit in the dead ball at <see cref="REPLAY_SPEED"/>. 52 today.
        /// </summary>
        private const int REPLAY_SOURCE_TICKS = (int)(
            (Systems_GameRules.DEAD_BALL_TICKS - REPLAY_EXIT_MARGIN_TICKS) * REPLAY_SPEED);

        /// <summary>A play shorter than this has nothing worth slowing down.</summary>
        private const int MIN_REPLAY_TICKS = 10;

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
        private ISubscriber<Systems_ScoreMessage> _scoreSubscriber;
        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;
        private ISubscriber<Systems_GameOverMessage> _gameOverSubscriber;
        private IPublisher<Systems_HighlightPlaybackMessage> _highlightPublisher;

        private IDisposable _tackleSubscription;
        private IDisposable _scoreSubscription;
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
        private Systems_UiOverlay _replayTag;
        private Systems_HighlightReel _reel;

        /// <summary>Ticks recorded since the last snap, so a replay never reaches into the previous play.</summary>
        private int _playFrames;

        /// <summary>Hardest tackle of the current play, m/s. Feeds the highlight score.</summary>
        private float _playPeakClosing;

        private bool _bigMoment;
        private bool _replayArmed;
        private bool _replaying;
        private bool _gameOver;

        private int _replayFirstFrame;
        private int _replayFrameCount;
        private int _replayTicksElapsed;
        private int _replayTicksTotal;

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
            ISubscriber<Systems_ScoreMessage> scoreSubscriber,
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
            _scoreSubscriber = scoreSubscriber;
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
            _scoreSubscription = _scoreSubscriber?.Subscribe(OnScore);
            _resolvedSubscription = _resolvedSubscriber?.Subscribe(OnDownResolved);
            _snappedSubscription = _snappedSubscriber?.Subscribe(OnSnapped);
            _gameOverSubscription = _gameOverSubscriber?.Subscribe(OnGameOver);
        }

        private void FixedUpdate()
        {
            if (_gameOver)
            {
                return;
            }

            if (_replayArmed)
            {
                _replayArmed = false;
                BeginReplay();
            }

            if (_replaying)
            {
                _replayTicksElapsed++;

                if (_replayTicksElapsed >= _replayTicksTotal)
                {
                    StopReplay();
                }

                return;
            }

            if (_play.Phase == Systems_PlayPhase.Live)
            {
                Record();
            }
        }

        /// <summary>
        /// The reel runs on wall-clock time, so its highlight is posed here rather
        /// than in LateUpdate's fixed-tick interpolation — and here is also early
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
                return;
            }

            if (!_replaying)
            {
                return;
            }

            // Where inside the current fixed tick this frame falls, for smooth
            // drawing between the tick-counted steps. Read, never written.
            float withinTick = Mathf.Clamp01(
                (Time.time - Time.fixedTime) / Time.fixedDeltaTime);

            float played = Mathf.Min(
                (_replayTicksElapsed + withinTick) * REPLAY_SPEED,
                _replayFrameCount - 1);

            _ghosts.Pose(_tape, _replayFirstFrame + played);
            _ghosts.EnforceHidden();
        }

        private void OnDisable()
        {
            // A disabled view must not leave the real players hidden, nor the
            // final overlay down with nothing on the field to watch.
            StopReplay();

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

            _scoreSubscription?.Dispose();
            _scoreSubscription = null;

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

            _replaying = false;
        }

        // --- UI -----------------------------------------------------------------

        protected override void BuildUi()
        {
            _layer = Systems_UiTheme.Layer();
            Root.Add(_layer);

            _replayTag = BuildReplayTag();
            _layer.Add(_replayTag.Root);
        }

        /// <summary>
        /// The bug in the corner that says this is not live. Bottom-left, just above
        /// the status footer: the top of the screen is the scoreboard and the
        /// result banner, both of which are saying something during a dead ball.
        /// </summary>
        private static Systems_UiOverlay BuildReplayTag()
        {
            Systems_UiOverlay overlay = new Systems_UiOverlay(
                "ReplayTag", blocksInput: false, scrim: Color.clear);

            overlay.Content.pickingMode = PickingMode.Ignore;

            VisualElement chip = Systems_UiTheme.Row();
            chip.style.position = Position.Absolute;
            chip.style.left = Systems_UiTheme.SPACE_M;
            chip.style.bottom = Systems_UiTheme.STATUS_FOOTER_HEIGHT + Systems_UiTheme.SPACE_M;
            chip.style.backgroundColor = Systems_UiTheme.SurfaceOverField;
            chip.pickingMode = PickingMode.Ignore;
            Systems_UiTheme.SetPadding(chip, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(chip, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(chip);

            VisualElement dot = new VisualElement();
            dot.style.width = Systems_UiTheme.DOT_SIZE;
            dot.style.height = Systems_UiTheme.DOT_SIZE;
            dot.style.marginRight = Systems_UiTheme.SPACE_S;
            dot.style.backgroundColor = Systems_UiTheme.TextPrimary;
            dot.pickingMode = PickingMode.Ignore;
            Systems_UiTheme.SetRadius(dot, Systems_UiTheme.DOT_SIZE / 2);

            Label label = Systems_UiTheme.Text(
                "REPLAY", Systems_UiTheme.TEXT_BODY, Systems_UiTheme.TextPrimary,
                FontStyle.Bold, Systems_UiTheme.Typeface.Display);
            label.style.letterSpacing = 2f;

            chip.Add(dot);
            chip.Add(label);
            overlay.Content.Add(chip);
            return overlay;
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
                    $"{nameof(Systems_ReplayView)}: found no players to record. Replays are off.");
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
                    + "Replays will show the players without the ball.");
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

        // --- Instant replay -------------------------------------------------------

        private void BeginReplay()
        {
            int frames = Mathf.Min(REPLAY_SOURCE_TICKS, Mathf.Min(_playFrames, _tape.Count));

            if (frames < MIN_REPLAY_TICKS || _ghosts == null)
            {
                return;
            }

            _replayFirstFrame = _tape.Count - frames;
            _replayFrameCount = frames;
            _replayTicksElapsed = 0;
            _replayTicksTotal = Mathf.CeilToInt((frames - 1) / REPLAY_SPEED);
            _replaying = true;

            _ghosts.Show();
            _ghosts.Pose(_tape, _replayFirstFrame);

            if (_replayTag != null)
            {
                _replayTag.Show();
            }
        }

        /// <summary>
        /// Every way a replay ends comes through here: its natural end, the snap,
        /// the final whistle, OnDisable and OnDestroy. Idempotent.
        /// </summary>
        private void StopReplay()
        {
            if (!_replaying)
            {
                return;
            }

            _replaying = false;

            if (_ghosts != null)
            {
                _ghosts.Hide();
            }

            if (_replayTag != null)
            {
                _replayTag.Hide();
            }
        }

        // --- Messages -------------------------------------------------------------

        private void OnTackle(Systems_TackleMessage message)
        {
            _playPeakClosing = Mathf.Max(_playPeakClosing, message.ClosingSpeed);

            if (message.ClosingSpeed >= Systems_SimConstants.FUMBLE_CLOSING_SPEED)
            {
                _bigMoment = true;
            }
        }

        private void OnScore(Systems_ScoreMessage message)
        {
            _bigMoment = true;
        }

        /// <summary>
        /// The play is over and the game has decided what it meant. Published
        /// synchronously from the whistle — possibly inside a collision callback —
        /// so this only copies and flags; the replay itself starts in FixedUpdate.
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

            if (_bigMoment)
            {
                _replayArmed = true;
            }

            _bigMoment = false;
        }

        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            // Regardless of how far the replay got: the formation has just been
            // re-formed under it and the ball is live.
            _replayArmed = false;
            StopReplay();

            _bigMoment = false;
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
            _replayArmed = false;
            StopReplay();

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

            // Under the replay tag, which is hidden by now anyway.
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
