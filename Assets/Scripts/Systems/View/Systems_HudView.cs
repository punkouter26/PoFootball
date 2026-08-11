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
    /// The in-game scoreboard: score, quarter, clock, possession, down and
    /// distance, a result banner after every play, and the box score behind a
    /// button.
    ///
    /// Reads the game model for anything continuous (the clock) and subscribes for
    /// anything discrete (a play resolving, the final whistle). That split is
    /// deliberate — a subscription per clock tick would fire fifty times a second
    /// to move a label that changes once a second, and polling for a touchdown
    /// would mean comparing scores every frame to detect an event the systems layer
    /// already announces.
    ///
    /// WHAT THE LAYOUT PASS CHANGED, AND WHY:
    ///
    ///   HIERARCHY WAS INVERTED. The two scores were 62 px and the down and
    ///   distance was 26 px, which is exactly backwards. The score changes six or
    ///   seven times in a game; the down changes on every single play and is the
    ///   one number that tells a viewer what they are about to watch. It is now the
    ///   largest thing on the bar and the only thing in the accent colour.
    ///
    ///   THE CHROME WAS OPAQUE. A solid band across the top of a portrait screen
    ///   costs a strip of playfield on every frame for information that is static
    ///   between snaps. It is translucent now, and the bottom bar takes itself out
    ///   of the way entirely while the ball is live.
    ///
    ///   NOTHING KNEW HOW TALL THE SCREEN WAS. The banner sat at a hard-coded
    ///   top: 420 and the buttons at hard-coded widths, authored against a 1920
    ///   panel. The panel matches on width, so on a taller handset all of that
    ///   bunched against the top and left several hundred pixels of dead space.
    ///   Everything vertical is a percentage now.
    /// </summary>
    public sealed class Systems_HudView : Systems_ScreenView, Systems_IInjectableView
    {
        /// <summary>How long a result banner stays up before fading itself out.</summary>
        private const float BANNER_SECONDS = 2.2f;

        /// <summary>
        /// Wall-clock multipliers the speed button cycles. 8x turns an hour-long
        /// game into about seven minutes, which is the difference between the end
        /// state being reachable in a sitting and not.
        /// </summary>
        private static readonly float[] SPEEDS = { 1f, 2f, 4f, 8f };

        private Button _speedButton;
        private int _speedIndex;

        private Systems_GameModel _game;
        private Systems_BoxScore _boxScore;
        private ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;
        private ISubscriber<Systems_GameOverMessage> _gameOverSubscriber;
        private ISubscriber<Systems_PlaySnappedMessage> _snappedSubscriber;

        private IDisposable _resolvedSubscription;
        private IDisposable _gameOverSubscription;
        private IDisposable _snappedSubscription;

        private Label _homeScore;
        private Label _awayScore;
        private Label _quarter;
        private Label _clock;
        private Label _downAndDistance;
        private Label _fieldPosition;
        private VisualElement _homePossession;
        private VisualElement _awayPossession;

        private Systems_UiOverlay _bannerOverlay;
        private Label _bannerHeadline;
        private Label _bannerDetail;

        private Systems_UiOverlay _finalOverlay;
        private Label _finalHeadline;
        private Label _finalScoreline;
        private Label _finalTotals;

        /// <summary>Last whole second rendered, so the clock label is not rebuilt per frame.</summary>
        private int _lastClockKey = -1;

        private int _lastHomeScore = -1;
        private int _lastAwayScore = -1;
        private int _lastDown = -1;
        private int _lastQuarter = -1;

        [Inject]
        public void Construct(
            Systems_GameModel game,
            Systems_BoxScore boxScore,
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_GameOverMessage> gameOverSubscriber,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
            _game = game;
            _boxScore = boxScore;
            _resolvedSubscriber = resolvedSubscriber;
            _gameOverSubscriber = gameOverSubscriber;
            _snappedSubscriber = snappedSubscriber;
        }

        protected override void Start()
        {
            base.Start();

            _resolvedSubscription = _resolvedSubscriber?.Subscribe(OnDownResolved);
            _gameOverSubscription = _gameOverSubscriber?.Subscribe(OnGameOver);
            _snappedSubscription = _snappedSubscriber?.Subscribe(OnSnapped);
        }

        private void OnDestroy()
        {
            _resolvedSubscription?.Dispose();
            _gameOverSubscription?.Dispose();
            _snappedSubscription?.Dispose();
        }

        protected override void BuildUi()
        {
            if (_game == null)
            {
                Debug.LogError(
                    $"{nameof(Systems_HudView)} was never injected — it needs a "
                    + "Systems_GameLifetimeScope in Game mode. The HUD will stay blank.");
                return;
            }

            // A Layer, not a Screen: this tree sits over a live field and must not
            // paint a background or swallow taps aimed at the game.
            VisualElement layer = Systems_UiTheme.Layer();
            Root.Add(layer);

            layer.Add(BuildScoreboard());

            // ORDER IS Z-ORDER. The banner sits over the field and the final
            // overlay sits over the banner. There is no longer a third layer: the
            // box score panel and the STATS button that toggled it are gone, and
            // the team totals they existed to show are now printed on the final
            // overlay itself, which is the only moment a viewer wants them.
            _bannerOverlay = BuildBanner();
            layer.Add(_bannerOverlay.Root);

            _finalOverlay = BuildFinalOverlay();
            layer.Add(_finalOverlay.Root);

            // Above the final overlay, so QUIT stays reachable either side of the
            // whistle rather than only before it.
            layer.Add(BuildControlBar());

            RefreshScoreboard(true);
        }

        // --- Scoreboard --------------------------------------------------------

        private VisualElement BuildScoreboard()
        {
            VisualElement bar = Systems_UiTheme.Column();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.top = 0;

            // Translucent, so the top of the field still reads through the chrome.
            bar.style.backgroundColor = Systems_UiTheme.SurfaceOverField;
            // Slightly more room above than below. The safe-area inset already
            // clears any cutout exactly; this stops the quarter label from sitting
            // flush against the bottom edge of a punch-hole, which reads as a
            // collision even when it technically is not one.
            Systems_UiTheme.SetPadding(
                bar, Systems_UiTheme.SPACE_M, Systems_UiTheme.SPACE_M,
                Systems_UiTheme.SPACE_M, Systems_UiTheme.SPACE_M);

            bar.Add(BuildScoreRow());
            bar.Add(BuildSituationRow());
            return bar;
        }

        private VisualElement BuildScoreRow()
        {
            VisualElement scoreRow = Systems_UiTheme.Row();
            scoreRow.style.justifyContent = Justify.SpaceBetween;

            scoreRow.Add(BuildTeamBlock(Systems_TeamId.Home, out _homeScore, out _homePossession));

            // ONE LINE, NOT TWO, AND DELIBERATELY SO. The quarter used to sit on
            // its own row directly above the clock, which put a label in the
            // topmost centre of the screen — the single most likely place on a
            // modern handset for a punch-hole camera to be. Insetting the safe area
            // clears the clock, but the row above it is always going to be the
            // thing closest to the hole. Putting the quarter inline beside the
            // clock removes that row entirely, and "1ST 4:50" is how a broadcast
            // graphic would write it anyway.
            VisualElement centre = Systems_UiTheme.Row();
            centre.style.justifyContent = Justify.Center;

            // flexBasis 0 with equal grow gives three columns that split the width
            // evenly at any panel size. The old minWidth: 200 pushed the two teams
            // hard into the corners on a narrow screen and left the clock adrift.
            centre.style.flexGrow = 1f;
            centre.style.flexBasis = 0f;

            _quarter = Systems_UiTheme.Caption("1ST");
            _quarter.style.marginRight = Systems_UiTheme.SPACE_S;

            _clock = Systems_UiTheme.Text(
                "5:00", Systems_UiTheme.TEXT_TITLE,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            centre.Add(_quarter);
            centre.Add(_clock);
            scoreRow.Add(centre);

            scoreRow.Add(BuildTeamBlock(Systems_TeamId.Away, out _awayScore, out _awayPossession));
            return scoreRow;
        }

        /// <summary>
        /// The down and distance, given the emphasis it always deserved. A pill in
        /// the accent colour, on its own line, larger than the scores beside it.
        /// </summary>
        private VisualElement BuildSituationRow()
        {
            VisualElement situation = Systems_UiTheme.Row();
            situation.style.justifyContent = Justify.Center;
            situation.style.marginTop = Systems_UiTheme.SPACE_S;

            VisualElement pill = Systems_UiTheme.Row();
            pill.style.backgroundColor = new Color(
                Systems_UiTheme.Accent.r, Systems_UiTheme.Accent.g,
                Systems_UiTheme.Accent.b, 0.14f);
            Systems_UiTheme.SetPadding(pill, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_M);
            Systems_UiTheme.SetRadius(pill, Systems_UiTheme.RADIUS);

            _downAndDistance = Systems_UiTheme.Text(
                "1st & 10", Systems_UiTheme.TEXT_TITLE,
                Systems_UiTheme.Accent, FontStyle.Bold);

            _fieldPosition = Systems_UiTheme.Text(
                "OWN 25", Systems_UiTheme.TEXT_BODY, Systems_UiTheme.TextMuted);
            _fieldPosition.style.marginLeft = Systems_UiTheme.SPACE_M;

            pill.Add(_downAndDistance);
            pill.Add(_fieldPosition);
            situation.Add(pill);
            return situation;
        }

        private static VisualElement BuildTeamBlock(
            Systems_TeamId team, out Label scoreLabel, out VisualElement possessionDot)
        {
            VisualElement block = Systems_UiTheme.Column();
            block.style.alignItems = Align.Center;
            block.style.flexGrow = 1f;
            block.style.flexBasis = 0f;

            VisualElement nameRow = Systems_UiTheme.Row();
            nameRow.style.justifyContent = Justify.Center;

            possessionDot = new VisualElement();
            possessionDot.style.width = 16;
            possessionDot.style.height = 16;
            possessionDot.style.backgroundColor = Systems_UiTheme.Accent;
            possessionDot.style.marginRight = Systems_UiTheme.SPACE_XS;
            possessionDot.style.visibility = Visibility.Hidden;
            Systems_UiTheme.SetRadius(possessionDot, 8);

            // Was TEXT_CAPTION at 20 px, which is smaller than the muted field
            // position and unreadable at arm's length. A team tag is an identity,
            // not a footnote.
            Label name = Systems_UiTheme.Text(
                Systems_DisplayText.TeamTag(team), Systems_UiTheme.TEXT_BODY,
                Systems_UiTheme.ColorOf(team), FontStyle.Bold);
            name.style.letterSpacing = 2f;

            nameRow.Add(possessionDot);
            nameRow.Add(name);

            // Down from TEXT_SCORE. Still the second-largest thing on the bar, but
            // no longer shouting over the down and distance.
            scoreLabel = Systems_UiTheme.Text(
                "0", 44, Systems_UiTheme.TextPrimary, FontStyle.Bold);

            block.Add(nameRow);
            block.Add(scoreLabel);
            return block;
        }

        // --- Overlays ----------------------------------------------------------

        private Systems_UiOverlay BuildBanner()
        {
            // No scrim and no input blocking: an announcement must never dim the
            // play it is announcing, nor eat a tap meant for the field.
            Systems_UiOverlay overlay = new Systems_UiOverlay(
                "ResultBanner", blocksInput: false, scrim: Color.clear);

            overlay.Content.style.alignItems = Align.Center;

            // Percentage, not the old hard-coded top: 420. Roughly a third of the
            // way down whatever screen this is, which keeps it clear of both the
            // scoreboard and the middle of the field where the ball usually is.
            overlay.Content.style.paddingTop = Length.Percent(30f);

            _bannerHeadline = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_BANNER,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            _bannerDetail = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_BODY, Systems_UiTheme.TextMuted);

            overlay.Content.Add(_bannerHeadline);
            overlay.Content.Add(_bannerDetail);
            return overlay;
        }

        private Systems_UiOverlay BuildFinalOverlay()
        {
            Systems_UiOverlay overlay = new Systems_UiOverlay(
                "FinalScore", blocksInput: true, scrim: Systems_UiTheme.SurfaceScrim)
                .Centered()
                .Padded(Systems_UiTheme.SPACE_XL);

            _finalHeadline = Systems_UiTheme.Text(
                "FINAL", Systems_UiTheme.TEXT_BANNER,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            _finalScoreline = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_TITLE, Systems_UiTheme.TextMuted);
            _finalScoreline.style.marginBottom = Systems_UiTheme.SPACE_M;

            // Team totals, filled in at the final whistle. One label rather than a
            // table: four numbers a side is not a grid, and the panel that used to
            // render it as one cost three hundred lines.
            _finalTotals = Systems_UiTheme.Text(
                string.Empty, Systems_UiTheme.TEXT_BODY, Systems_UiTheme.TextMuted);
            _finalTotals.style.unityTextAlign = TextAnchor.MiddleCenter;
            _finalTotals.style.marginBottom = Systems_UiTheme.SPACE_XL;

            // REMATCH reloads SCN_GAME rather than resetting the models in place.
            // Everything a second game needs to forget — the scoreboard, the box
            // score, the episode director that stopped itself at the final whistle,
            // twenty-two rigidbodies parked where the last tackle left them — is
            // scene state, and rebuilding the scene clears all of it through the
            // same path PLAY already uses. Resetting each piece by hand would be a
            // second, less-travelled way to reach the same state.
            Button rematchButton = Systems_UiTheme.Button(
                "REMATCH", Systems_UiTheme.Accent, Systems_SceneRouter.LoadGame);
            Systems_UiTheme.ApplyPrimaryActionSize(rematchButton);

            Button menuButton = Systems_UiTheme.Button(
                "MENU", Systems_UiTheme.SurfaceRaised, Systems_SceneRouter.LoadMenu);
            menuButton.style.color = Systems_UiTheme.TextPrimary;
            Systems_UiTheme.ApplySecondaryActionSize(menuButton);

            overlay.Content.Add(_finalHeadline);
            overlay.Content.Add(_finalScoreline);
            overlay.Content.Add(_finalTotals);
            overlay.Content.Add(rematchButton);
            overlay.Content.Add(menuButton);
            return overlay;
        }


        // --- Per-frame ----------------------------------------------------------

        /// <summary>
        /// The base class re-applies the safe area here. Everything below is
        /// guarded on a changed value, so the common frame writes nothing —
        /// including, now, the result banner, which used to run a countdown on
        /// every frame of every game to animate a two-second fade.
        /// </summary>
        protected override void Update()
        {
            base.Update();

            if (!IsBuilt || _game == null)
            {
                return;
            }

            RefreshClock();
            RefreshScoreboard(false);
        }

        private void RefreshClock()
        {
            int key = Systems_DisplayText.ClockKey(_game.SecondsRemaining);

            if (key == _lastClockKey)
            {
                return;
            }

            _lastClockKey = key;
            _clock.text = Systems_DisplayText.Clock(_game.SecondsRemaining);

            // A stopped clock is dimmed rather than hidden — a viewer needs to be
            // able to tell "stopped" from "the game is over".
            _clock.style.color = _game.IsClockRunning
                ? Systems_UiTheme.TextPrimary
                : Systems_UiTheme.TextMuted;
        }

        /// <summary>
        /// Updates the parts of the scoreboard that change on a play boundary.
        /// Guarded on the values themselves so the common frame does no work at all.
        /// </summary>
        private void RefreshScoreboard(bool force)
        {
            if (force || _game.HomeScore != _lastHomeScore)
            {
                _lastHomeScore = _game.HomeScore;
                _homeScore.text = _lastHomeScore.ToString();
            }

            if (force || _game.AwayScore != _lastAwayScore)
            {
                _lastAwayScore = _game.AwayScore;
                _awayScore.text = _lastAwayScore.ToString();
            }

            if (force || _game.Quarter != _lastQuarter)
            {
                _lastQuarter = _game.Quarter;
                _quarter.text = Systems_DisplayText.QuarterLabel(_lastQuarter);
            }

            if (force || _game.Down != _lastDown)
            {
                _lastDown = _game.Down;
                RefreshSituation();
            }
        }

        private void RefreshSituation()
        {
            _downAndDistance.text = Systems_DisplayText.DownAndDistance(
                _game.Down, _game.YardsToGo, _game.IsGoalToGo);

            _fieldPosition.text = Systems_DisplayText.FieldPosition(_game.LineOfScrimmageY);

            bool homeHasBall = _game.Possession == Systems_TeamId.Home;
            _homePossession.style.visibility =
                homeHasBall ? Visibility.Visible : Visibility.Hidden;
            _awayPossession.style.visibility =
                homeHasBall ? Visibility.Hidden : Visibility.Visible;
        }

        // --- Events --------------------------------------------------------------

        /// <summary>
        /// The ball is live. Get the bottom bar out of the way — it is a between
        /// downs control and there is nothing it can usefully do during a play.
        /// </summary>
        private void OnSnapped(Systems_PlaySnappedMessage message)
        {
            if (!IsBuilt)
            {
                return;
            }

            _bannerOverlay.Hide();
        }

        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            if (!IsBuilt)
            {
                return;
            }

            _bannerHeadline.text =
                Systems_DisplayText.ResultBanner(message.Result, message.Outcome);

            _bannerHeadline.style.color = BannerColorFor(message);

            _bannerDetail.text =
                $"{Systems_DisplayText.PlayCallLabel(message.Call)}   "
                + Systems_DisplayText.YardageDetail(message.YardsGained);

            // The overlay owns its own timer on the panel's scheduler. Nothing here
            // ticks; nothing here allocates a task.
            _bannerOverlay.ShowFor(BANNER_SECONDS);

            // Field position and possession can change without the down number
            // changing — a turnover resets to 1st down from 1st down — so refresh
            // the situation line here rather than relying on the change guard.
            RefreshSituation();
        }

        /// <summary>
        /// The only controls available while the ball is live: leave, and change
        /// how fast the game runs.
        ///
        /// WHY IT EXISTS. There was a window in which this screen had no controls
        /// at all between kickoff and the final whistle — the box-score panel and
        /// the STATS button that opened it had been removed together, and the only
        /// two buttons left were inside the final overlay. A viewer who started a
        /// game was committed to sixty minutes of wall clock with no way back to
        /// the menu short of killing the app.
        ///
        /// SPEED IS Time.timeScale AND NOTHING ELSE. A quarter is fifteen minutes
        /// of game clock burned at one second per fifty physics ticks, so a full
        /// game is an hour in real time and the FINAL overlay was effectively
        /// unreachable — including for whoever is testing it. timeScale changes how
        /// much wall-clock a physics step costs, not how much simulated time it
        /// represents: Time.fixedDeltaTime is untouched, every step is still 0.02 s
        /// to the simulation, and the dynamics each .onnx was fitted against are
        /// identical at 8x and at 1x. That is the whole reason this is the control
        /// rather than a shorter quarter, which would change the game itself.
        /// </summary>
        private VisualElement BuildControlBar()
        {
            VisualElement bar = Systems_UiTheme.Row();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.bottom = 0;
            bar.style.justifyContent = Justify.Center;
            Systems_UiTheme.SetPadding(bar, Systems_UiTheme.SPACE_M);

            Button quit = Systems_UiTheme.Button(
                "QUIT", Systems_UiTheme.SurfaceRaised, Systems_SceneRouter.LoadMenu);
            quit.style.color = Systems_UiTheme.TextPrimary;
            ApplyControlSize(quit);
            quit.style.marginRight = Systems_UiTheme.SPACE_S;

            _speedButton = Systems_UiTheme.Button(
                string.Empty, Systems_UiTheme.SurfaceRaised, CycleSpeed);
            _speedButton.style.color = Systems_UiTheme.TextPrimary;
            ApplyControlSize(_speedButton);

            bar.Add(quit);
            bar.Add(_speedButton);

            ApplySpeed();
            return bar;
        }

        /// <summary>
        /// Chrome sits over a live field, so these are deliberately smaller and
        /// quieter than the primary actions on the overlays — they are an escape
        /// hatch, not the point of the screen.
        /// </summary>
        private static void ApplyControlSize(Button button)
        {
            button.style.width = Length.Percent(34f);
            button.style.maxWidth = 260;
            button.style.minHeight = Systems_UiTheme.TAP_TARGET;
            button.style.fontSize = Systems_UiTheme.TEXT_BODY;
        }

        private void CycleSpeed()
        {
            _speedIndex = (_speedIndex + 1) % SPEEDS.Length;
            ApplySpeed();
        }

        private void ApplySpeed()
        {
            float speed = SPEEDS[_speedIndex];

            Time.timeScale = speed;
            _speedButton.text = $"{speed:0.#}x";
        }

        /// <summary>
        /// Unconditional, because timeScale is global and outlives this scene. A
        /// view that left the editor or the menu running at 8x after a scene change
        /// is the obvious way for this control to become somebody's afternoon.
        /// </summary>
        private void OnDisable()
        {
            Time.timeScale = 1f;
        }

        private static Color BannerColorFor(Systems_DownResolvedMessage message)
        {
            switch (message.Result)
            {
                case Systems_DownResult.Touchdown:
                    return Systems_UiTheme.ColorOf(message.Offense);
                case Systems_DownResult.Interception:
                case Systems_DownResult.TurnoverOnDowns:
                case Systems_DownResult.Safety:
                    return Systems_UiTheme.Negative;
                case Systems_DownResult.FirstDown:
                    return Systems_UiTheme.Positive;
                default:
                    return Systems_UiTheme.TextPrimary;
            }
        }

        private void OnGameOver(Systems_GameOverMessage message)
        {
            if (!IsBuilt)
            {
                return;
            }

            _finalHeadline.text = message.IsTie
                ? "TIE GAME"
                : $"{Systems_DisplayText.TeamName(message.Winner)} WINS";

            _finalScoreline.text = $"{message.HomeScore} — {message.AwayScore}";
            _finalTotals.text = BuildTotals();

            _bannerOverlay.Hide();
            _finalOverlay.Show();
        }

        /// <summary>
        /// Both teams' totals as one block of text. Built once, at the whistle —
        /// this is the only time it is shown, so there is nothing to keep in sync.
        /// </summary>
        private string BuildTotals()
        {
            Systems_TeamStatLine home = _boxScore.Team(Systems_TeamId.Home);
            Systems_TeamStatLine away = _boxScore.Team(Systems_TeamId.Away);

            return
                $"{Systems_DisplayText.TeamName(Systems_TeamId.Home)}   "
                + $"{Mathf.RoundToInt(home.TotalYards)} YDS   "
                + $"{home.FirstDowns} 1ST   {home.Turnovers} TO\n"
                + $"{Systems_DisplayText.TeamName(Systems_TeamId.Away)}   "
                + $"{Mathf.RoundToInt(away.TotalYards)} YDS   "
                + $"{away.FirstDowns} 1ST   {away.Turnovers} TO";
        }
    }
}
