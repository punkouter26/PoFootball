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
    /// distance, a result banner after every play, and the full box score on the
    /// final overlay.
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
    ///
    ///   THE BAR WAS TWO ROWS, AND THE TOP ONE WAS MOSTLY EMPTY. The scores sat at
    ///   the outside edges of a row whose centre was deliberately blank, with the
    ///   clock and the down on a second row beneath it: 229 panel units of chrome
    ///   under a 76-unit status bar, a sixth of a 16:9 screen. It is one row now —
    ///   score, clock, down, call, score. See BuildScoreboard for why the blank
    ///   centre is no longer needed.
    ///
    ///   QUIT IS GONE. It did exactly what the status HUD's MENU chip does, from a
    ///   96-unit button parked over the field, and it showed through the final
    ///   scrim as a third button. The top-right corner is the one way out.
    /// </summary>
    public sealed class Systems_HudView : Systems_ScreenView, Systems_IInjectableView
    {
        /// <summary>
        /// Width of each team's block at the two ends of the bar. Fixed and equal,
        /// so the situation group between them is centred on the screen rather
        /// than on whichever score happens to be wider.
        /// </summary>
        private const int TEAM_BLOCK_WIDTH = 120;

        /// <summary>
        /// Width reserved on EACH side of the down-and-distance pill: the quarter
        /// and clock on its left, the play call on its right. Equal on purpose —
        /// the pill is then centred however wide either neighbour's text is, and
        /// neither the clock ticking nor the call chip appearing at the snap can
        /// move it. Sized for the widest of the two: "14:22" at TEXT_TITLE needs
        /// about 110 panel units and the "FIELD GOAL" chip 141.
        /// </summary>
        private const int FLANK_WIDTH = 160;

        /// <summary>
        /// Distance from the bottom of the status bar to the top of the result
        /// banner: the scoreboard's own height plus a gap.
        /// </summary>
        private const int BANNER_CLEARANCE = 156;

        /// <summary>How long a result banner stays up before fading itself out.</summary>
        private const float BANNER_SECONDS = 2.2f;

        private Systems_GameModel _game;

        /// <summary>
        /// Read for one thing only: the play call the quarterback has committed to.
        ///
        /// The game model carries the down, the clock and the score — everything a
        /// broadcast scoreboard has ever shown. It does not and should not carry
        /// what the offense decided to do, because that is not a rules fact, it is
        /// a policy's output, and it lives on the play.
        /// </summary>
        private Systems_PlayModel _play;

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

        /// <summary>
        /// The down-and-distance pill. Held so the final whistle can take it down —
        /// see <see cref="OnGameOver"/>. The clock beside it is deliberately left up.
        /// </summary>
        private VisualElement _situationPill;
        private VisualElement _callChip;
        private Label _callLabel;
        private Label _finalHeadline;

        /// <summary>
        /// Where the box score card goes at the whistle. Empty until then: the
        /// numbers do not exist before the game ends, and a card of zeroes built
        /// at kickoff would only have to be thrown away.
        /// </summary>
        private VisualElement _finalCardHost;

        /// <summary>Last whole second rendered, so the clock label is not rebuilt per frame.</summary>
        private int _lastClockKey = -1;

        private int _lastHomeScore = -1;
        private int _lastAwayScore = -1;
        private int _lastDown = -1;
        private int _lastQuarter = -1;

        /// <summary>
        /// Set the first time the game is seen in overtime, and never cleared.
        /// Sticky because the phase moves on to Final at the whistle, and a game
        /// that was settled in overtime should still say so under the result.
        /// </summary>
        private bool _sawOvertime;

        /// <summary>
        /// Deliberately seeded to a value the enum does not define, so the first
        /// comparison in RefreshCall always misses and the chip is initialised
        /// once. None is a real state — "the quarterback has not decided" — and
        /// starting there would leave the chip unbuilt until the first call.
        /// </summary>
        private Systems_PlayCall _lastCall = (Systems_PlayCall)(-1);

        [Inject]
        public void Construct(
            Systems_GameModel game,
            Systems_PlayModel play,
            Systems_BoxScore boxScore,
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber,
            ISubscriber<Systems_GameOverMessage> gameOverSubscriber,
            ISubscriber<Systems_PlaySnappedMessage> snappedSubscriber)
        {
            _game = game;
            _play = play;
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
            // overlay sits over the banner. Nothing else is on this layer: the way
            // out of a live game is the status HUD's MENU chip, on its own panel
            // above all of this.
            _bannerOverlay = BuildBanner();
            layer.Add(_bannerOverlay.Root);

            _finalOverlay = BuildFinalOverlay();
            layer.Add(_finalOverlay.Root);

            RefreshScoreboard(true);
        }

        // --- Scoreboard --------------------------------------------------------

        /// <summary>
        /// One row: home score, the situation, away score.
        ///
        /// WHY THE TOP CENTRE IS USABLE AGAIN. The previous layout kept the middle
        /// of its first row empty because a centre punch-hole clipped the clock's
        /// leading digit on a 1440x3088 handset. That was true when it was
        /// written — Systems_ScreenView's safe-area inset was never being applied
        /// at all, a bug fixed since. With the inset working, and the status bar
        /// taking the 76 units directly beneath it, the first thing this bar draws
        /// is already a full row clear of any cutout. The blank centre was
        /// protecting against a failure that can no longer reach it, and it cost a
        /// second row to do so.
        /// </summary>
        private VisualElement BuildScoreboard()
        {
            VisualElement bar = Systems_UiTheme.Row();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;
            bar.style.justifyContent = Justify.SpaceBetween;

            // BELOW THE STATUS HUD, NOT UNDER IT. Systems_StatusHudView reserves the
            // top strip on every screen for the title, the frame rate and MENU; at
            // top: 0 the score row rendered underneath it and the away team's points
            // came out behind the MENU chip.
            bar.style.top = Systems_UiTheme.STATUS_BAR_HEIGHT;

            // Translucent, so the top of the field still reads through the chrome.
            bar.style.backgroundColor = Systems_UiTheme.SurfaceOverField;

            // A lit top edge and a dark bottom one, which is the only way to say
            // "this is a surface in front of the field" on a panel that has no
            // shadows. Without it a translucent bar over dark turf reads as a patch
            // where the grass happens to be a different colour.
            Systems_UiTheme.ApplyElevation(bar);
            Systems_UiTheme.SetPadding(
                bar, Systems_UiTheme.SPACE_S, Systems_UiTheme.SPACE_M);

            bar.Add(BuildTeamBlock(Systems_TeamId.Home, out _homeScore, out _homePossession));
            bar.Add(BuildSituation());
            bar.Add(BuildTeamBlock(Systems_TeamId.Away, out _awayScore, out _awayPossession));
            return bar;
        }

        /// <summary>
        /// Clock, down and distance, play call — in that order, with the down in
        /// the middle of the screen.
        ///
        /// The down and distance keeps the emphasis it was given: a pill in the
        /// accent colour, the only thing on the bar that is. The two groups either
        /// side of it are the same fixed width, which is what holds it still.
        /// </summary>
        private VisualElement BuildSituation()
        {
            VisualElement situation = Systems_UiTheme.Row();

            // The quarter ABOVE the clock, both against the pill. Side by side they
            // needed a reserved box for the clock to stop the row re-flowing every
            // second, and the box left a four-glyph time floating a hundred units
            // from its own quarter — "1ST        3:14" read as two unrelated
            // labels. Stacked and right-aligned in a fixed-width column there is
            // nothing beside the digits to push: the text changes width and
            // nothing moves, which is the same guarantee without the gap.
            VisualElement clockGroup = Systems_UiTheme.Column();
            clockGroup.style.width = FLANK_WIDTH;
            clockGroup.style.alignItems = Align.FlexEnd;

            _quarter = Systems_UiTheme.Caption("1ST");

            _clock = Systems_UiTheme.Text(
                "5:00", Systems_UiTheme.TEXT_TITLE,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);

            clockGroup.Add(_quarter);
            clockGroup.Add(_clock);
            situation.Add(clockGroup);

            VisualElement pill = Systems_UiTheme.Row();
            pill.style.marginLeft = Systems_UiTheme.SPACE_M;
            pill.style.marginRight = Systems_UiTheme.SPACE_M;
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

            Systems_UiTheme.ApplyElevation(pill);
            Systems_UiTheme.EnablePulse(pill);

            pill.Add(_downAndDistance);
            pill.Add(_fieldPosition);
            situation.Add(pill);

            VisualElement callSlot = Systems_UiTheme.Row();
            callSlot.style.width = FLANK_WIDTH;
            callSlot.Add(BuildCallChip());
            situation.Add(callSlot);

            _situationPill = pill;
            return situation;
        }

        /// <summary>
        /// The play the quarterback committed to, the moment it commits.
        ///
        /// WHY IT IS WORTH A PLACE ON A PORTRAIT SCOREBOARD. The call is the single
        /// most consequential decision in the game and it is made by a discrete
        /// action head that has its own brain, its own entropy bonus and its own
        /// telemetry channel (Agent_Telemetry writes Call/Entropy precisely because
        /// a collapsed play-caller is invisible in the trainer's own numbers). On
        /// screen it was invisible too: the only way to know a pass had been called
        /// was to watch for a throw, which is the outcome, not the decision.
        ///
        /// IT APPEARS LATE, AND THAT IS THE SIMULATION SHOWING THROUGH. Nothing is
        /// committed until DROPBACK_TICKS have elapsed — the quarterback reads the
        /// rush first (see Agent_ActionContract, revision 2) — so the chip is empty
        /// for the first fraction of a second of every snap. That gap is the read,
        /// and it is worth seeing.
        /// </summary>
        private VisualElement BuildCallChip()
        {
            VisualElement chip = Systems_UiTheme.Row();
            chip.style.backgroundColor = new Color(
                Systems_UiTheme.TextPrimary.r,
                Systems_UiTheme.TextPrimary.g,
                Systems_UiTheme.TextPrimary.b,
                0.10f);

            Systems_UiTheme.SetPadding(
                chip, Systems_UiTheme.SPACE_XS, Systems_UiTheme.SPACE_S);

            Systems_UiTheme.SetRadius(chip, Systems_UiTheme.RADIUS);
            Systems_UiTheme.ApplyElevation(chip);

            _callLabel = Systems_UiTheme.Text(
                string.Empty,
                Systems_UiTheme.TEXT_BODY,
                Systems_UiTheme.TextPrimary,
                FontStyle.Bold);

            chip.Add(_callLabel);

            // Hidden, NOT removed from layout. As display: none the chip took its
            // width out of a centred row at every snap and put it back at every
            // whistle, so the clock and the pill stepped sideways twice a play.
            // The slot it sits in is a fixed width either way; visibility only
            // decides whether anything is drawn in it.
            chip.style.visibility = Visibility.Hidden;

            _callChip = chip;
            return chip;
        }

        /// <summary>
        /// Guarded on the call itself, so a frame in which nothing was decided
        /// writes nothing — the same discipline the rest of Update follows.
        /// </summary>
        private void RefreshCall()
        {
            // Nothing is being called once the game is over, and the final overlay
            // has taken the chip down — see OnGameOver.
            if (_play == null || _callChip == null || _finalOverlay.IsVisible)
            {
                return;
            }

            Systems_PlayCall call = _play.Call;

            if (call == _lastCall)
            {
                return;
            }

            _lastCall = call;

            if (call == Systems_PlayCall.None)
            {
                _callChip.style.visibility = Visibility.Hidden;
                return;
            }

            _callLabel.text = Systems_DisplayText.PlayCall(call);
            _callChip.style.visibility = Visibility.Visible;
        }

        private static VisualElement BuildTeamBlock(
            Systems_TeamId team, out Label scoreLabel, out VisualElement possessionDot)
        {
            VisualElement block = Systems_UiTheme.Column();
            block.style.alignItems = Align.Center;
            block.style.width = TEAM_BLOCK_WIDTH;

            VisualElement nameRow = Systems_UiTheme.Row();
            nameRow.style.justifyContent = Justify.Center;

            // The team's own colour, not the accent. This dot answers "who has the
            // ball", so it should be the same colour as that team's shapes on the
            // field and its column in the box score — and the accent is reserved
            // for the chains, which this is not.
            possessionDot = new VisualElement();
            possessionDot.style.width = Systems_UiTheme.DOT_SIZE;
            possessionDot.style.height = Systems_UiTheme.DOT_SIZE;
            possessionDot.style.backgroundColor = Systems_UiTheme.ColorOf(team);
            possessionDot.style.marginRight = Systems_UiTheme.SPACE_XS;
            possessionDot.style.visibility = Visibility.Hidden;
            Systems_UiTheme.SetRadius(possessionDot, Systems_UiTheme.DOT_SIZE / 2);

            // Was TEXT_CAPTION at 20 px, which is smaller than the muted field
            // position and unreadable at arm's length. A team tag is an identity,
            // not a footnote.
            Label name = Systems_UiTheme.Text(
                Systems_DisplayText.TeamTag(team), Systems_UiTheme.TEXT_BODY,
                Systems_UiTheme.ColorOf(team), FontStyle.Bold);
            name.style.letterSpacing = 2f;

            nameRow.Add(possessionDot);
            nameRow.Add(name);

            // Still the second-largest thing on the bar, but no longer shouting over
            // the down and distance.
            scoreLabel = Systems_UiTheme.Text(
                "0", Systems_UiTheme.TEXT_SCORE, Systems_UiTheme.TextPrimary, FontStyle.Bold);

            // Scoring is the rarest and most important thing that happens. It used
            // to redraw a glyph in place and nothing else.
            Systems_UiTheme.EnablePulse(scoreLabel);

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

            // DIRECTLY UNDER THE SCOREBOARD, AND SAID SO IN UNITS. This was
            // paddingTop: 30%, described as "a third of the way down whatever screen
            // this is". It never was: a percentage padding resolves against the
            // WIDTH of the containing block, on every axis, and this panel is 1080
            // wide on every device — so it was a fixed 324 units that only looked
            // like a proportion. The banner belongs just below the bar on every
            // screen, clear of the middle of the field where the ball usually is,
            // and that is a distance, not a ratio.
            overlay.Content.style.paddingTop =
                Systems_UiTheme.STATUS_BAR_HEIGHT + BANNER_CLEARANCE;

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

            _finalHeadline.style.marginBottom = Systems_UiTheme.SPACE_M;

            // The whole box score, on the screen that announces the result. It used
            // to be two lines of totals here and the full table on the menu, a tap
            // and a scene load away — and not at all for anyone who chose REMATCH.
            // Stretched to the overlay's width rather than centred to its content,
            // so the three columns have room to line up.
            _finalCardHost = new VisualElement();
            _finalCardHost.style.alignSelf = Align.Stretch;
            _finalCardHost.style.marginBottom = Systems_UiTheme.SPACE_XL;
            _finalCardHost.pickingMode = PickingMode.Ignore;

            // REMATCH reloads SCN_GAME rather than resetting the models in place.
            // Everything a second game needs to forget — the scoreboard, the box
            // score, the episode director that stopped itself at the final whistle,
            // twenty-two rigidbodies parked where the last tackle left them — is
            // scene state, and rebuilding the scene clears all of it through the
            // same path PLAY already uses. Resetting each piece by hand would be a
            // second, less-travelled way to reach the same state.
            Button rematchButton = Systems_UiTheme.Button(
                "REMATCH", Systems_UiTheme.Action, Systems_SceneRouter.LoadGame);
            Systems_UiTheme.ApplyPrimaryActionSize(rematchButton);

            // The same destination as the status HUD's MENU chip above it, and now
            // the same result: the summary is offered to the router at the whistle
            // (see OnGameOver), so neither button has to remember to carry it.
            Button menuButton = Systems_UiTheme.Button(
                "MENU", Systems_UiTheme.SurfaceRaised, Systems_SceneRouter.LoadMenu);
            menuButton.style.color = Systems_UiTheme.TextPrimary;
            Systems_UiTheme.ApplySecondaryActionSize(menuButton);

            overlay.Content.Add(_finalHeadline);
            overlay.Content.Add(_finalCardHost);
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
            RefreshCall();
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
            //
            // NO TWO-MINUTE COLOUR, DELIBERATELY. An amber clock inside two minutes
            // was written here and taken out again: amber is Systems_UiTheme.Accent,
            // which that class reserves for the chains "so it always means the
            // line", and the palette note is explicit that every other hue is
            // already spoken for by the teams or by good and bad news. There is no
            // free colour for urgency, and quietly spending the chains' one to
            // invent a signal nobody asked for is how the accent came to mean four
            // things the last time.
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
            // Pulsed only when the value actually moved, never on the forced build.
            // A HUD that animated everything on the opening frame would spend the
            // kickoff drawing attention to numbers nobody has read yet.
            if (force || _game.HomeScore != _lastHomeScore)
            {
                _lastHomeScore = _game.HomeScore;
                _homeScore.text = _lastHomeScore.ToString();

                if (!force)
                {
                    Systems_UiTheme.Pulse(_homeScore);
                }
            }

            if (force || _game.AwayScore != _lastAwayScore)
            {
                _lastAwayScore = _game.AwayScore;
                _awayScore.text = _lastAwayScore.ToString();

                if (!force)
                {
                    Systems_UiTheme.Pulse(_awayScore);
                }
            }

            // OVERTIME IS A PHASE, NOT A FIFTH QUARTER. Systems_GameModel.BeginOvertime
            // resets the clock and sets the phase and leaves Quarter at four, so
            // keying this label on Quarter alone showed "4TH" beside a fresh clock
            // for the whole of sudden death — and Systems_DisplayText's "OT" branch
            // had never once been reached. Seen on a 38-45 game that ended with
            // "4TH 2:28" on the board.
            _sawOvertime |= _game.Phase == Systems_GamePhase.Overtime;

            int period = _sawOvertime
                ? Systems_GameRules.QUARTER_COUNT + 1
                : _game.Quarter;

            if (force || period != _lastQuarter)
            {
                _lastQuarter = period;
                _quarter.text = Systems_DisplayText.QuarterLabel(period);
            }

            if (force || _game.Down != _lastDown)
            {
                _lastDown = _game.Down;
                RefreshSituation();

                if (!force && _situationPill != null)
                {
                    Systems_UiTheme.Pulse(_situationPill);
                }
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
        /// The ball is live. Take the last play's banner down — it is describing
        /// a down that is over, on top of the one that has started.
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

            // Frozen once, here, and used twice: the card on this overlay, and the
            // menu's LAST GAME card after the scene that owns the box score is gone.
            // Offered to the router now rather than by a button later, so MENU on
            // the overlay and MENU in the status bar lead to the same screen.
            Systems_GameSummary summary = Systems_GameSummary.From(_boxScore);
            Systems_SceneRouter.OfferSummary(summary);

            _finalCardHost.Clear();
            _finalCardHost.Add(Systems_BoxScoreCard.Build("FINAL", summary));

            _bannerOverlay.Hide();

            // NEITHER THE DOWN NOR THE CALL IS TRUE ANY MORE. Systems_GameFlowSystem
            // resolves the last play like any other, so the chains are left showing
            // whatever the next snap WOULD have been — a finished game sat under a
            // "1st & 10 OPP 46" pill above a 0:00 clock, describing a down that will
            // never be played, beside the call from a play that is over. Hidden
            // rather than removed, so the clock beside them does not move; it
            // stays, because 0:00 is the true and interesting fact about a game
            // that has ended.
            if (_situationPill != null)
            {
                _situationPill.style.visibility = Visibility.Hidden;
            }

            if (_callChip != null)
            {
                _callChip.style.visibility = Visibility.Hidden;
            }

            _finalOverlay.Show();
        }
    }
}
