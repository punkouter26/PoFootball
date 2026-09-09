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
        /// <summary>
        /// Reserved width for the clock, in panel units at the 1080-wide reference.
        /// Sized for "14:22" — five glyphs at TEXT_TITLE in the condensed display
        /// face — so the label never resizes as the clock counts down. See
        /// BuildSituationRow.
        /// </summary>
        private const int CLOCK_MIN_WIDTH = 150;

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
        private Label _finalScoreline;
        private Label _finalTotals;

        /// <summary>Last whole second rendered, so the clock label is not rebuilt per frame.</summary>
        private int _lastClockKey = -1;

        private int _lastHomeScore = -1;
        private int _lastAwayScore = -1;
        private int _lastDown = -1;
        private int _lastQuarter = -1;

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
            // overlay sits over the banner. There is no longer a third layer: the
            // box score panel and the STATS button that toggled it are gone, and
            // the team totals they existed to show are now printed on the final
            // overlay itself, which is the only moment a viewer wants them.
            _bannerOverlay = BuildBanner();
            layer.Add(_bannerOverlay.Root);

            // BELOW THE FINAL OVERLAY, AND THAT IS THE WHOLE POINT. This used to be
            // added last, so QUIT drew ON TOP of the final scrim next to REMATCH and
            // MENU — three buttons at the whistle, two of which looked identical and
            // were not. MENU carries the finished game's numbers to the front end
            // via Systems_GameSummary.From; QUIT calls the summary-less LoadMenu
            // overload, so tapping the wrong one silently threw the LAST GAME card
            // away. Ordering it under the overlay means the whistle covers it and
            // the end of a game offers exactly the two endings it should.
            layer.Add(BuildControlBar());

            _finalOverlay = BuildFinalOverlay();
            layer.Add(_finalOverlay.Root);

            RefreshScoreboard(true);
        }

        // --- Scoreboard --------------------------------------------------------

        private VisualElement BuildScoreboard()
        {
            VisualElement bar = Systems_UiTheme.Column();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;

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
            // AN EMPTY COLUMN, BECAUSE THE TOP CENTRE IS NOT OURS TO USE. The
            // quarter and clock lived here, inline, specifically to avoid putting a
            // second row under a punch-hole. It did not work: on a centre-cutout
            // handset the hole clips the clock's LEADING DIGIT, so 1:07 reads ":07"
            // and 14:22 loses the quarter-hour entirely. Verified on a 1440x3088
            // centre punch-hole device at 1:39, 1:52 and 1:07.
            //
            // The safe-area inset is supposed to prevent exactly this and does not
            // reach far enough here, so the layout no longer depends on it: the
            // topmost row now spans the two scores at the OUTSIDE edges with
            // nothing between them, and the clock has moved down beside the
            // down-and-distance pill. A cutout can only ever eat empty space.
            VisualElement centre = new VisualElement();
            centre.style.flexGrow = 1f;
            centre.style.flexBasis = 0f;
            centre.pickingMode = PickingMode.Ignore;
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
            situation.style.alignItems = Align.Center;
            situation.style.marginTop = Systems_UiTheme.SPACE_S;

            // The clock, relocated off the top-centre strip — see BuildScoreRow.
            // Beside the situation pill it still reads as one broadcast lower-third
            // and it is now a full row clear of any cutout.
            _quarter = Systems_UiTheme.Caption("1ST");
            _quarter.style.marginRight = Systems_UiTheme.SPACE_XS;

            _clock = Systems_UiTheme.Text(
                "5:00", Systems_UiTheme.TEXT_TITLE,
                Systems_UiTheme.TextPrimary, FontStyle.Bold);
            _clock.style.marginRight = Systems_UiTheme.SPACE_L;

            // A FIXED BOX, BECAUSE THE CLOCK IS THE ONE LABEL THAT CHANGES EVERY
            // SECOND. Its text steps between four and five glyphs — "9:58" then
            // "10:02" — and the digits are not the same width, so on every tick the
            // row it sits in re-flowed and the down-and-distance pill beside it
            // twitched sideways. UI Toolkit exposes no tabular-figure font feature,
            // so the fix is to stop the label from resizing at all: reserve the
            // widest case and centre inside it.
            _clock.style.minWidth = CLOCK_MIN_WIDTH;
            _clock.style.unityTextAlign = TextAnchor.MiddleCenter;

            situation.Add(_quarter);
            situation.Add(_clock);

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

            Systems_UiTheme.ApplyElevation(pill);
            Systems_UiTheme.EnablePulse(pill);

            pill.Add(_downAndDistance);
            pill.Add(_fieldPosition);
            situation.Add(pill);

            situation.Add(BuildCallChip());

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
            chip.style.marginLeft = Systems_UiTheme.SPACE_S;
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
            chip.style.display = DisplayStyle.None;

            _callChip = chip;
            return chip;
        }

        /// <summary>
        /// Guarded on the call itself, so a frame in which nothing was decided
        /// writes nothing — the same discipline the rest of Update follows.
        /// </summary>
        private void RefreshCall()
        {
            if (_play == null || _callChip == null)
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
                _callChip.style.display = DisplayStyle.None;
                return;
            }

            _callLabel.text = Systems_DisplayText.PlayCall(call);
            _callChip.style.display = DisplayStyle.Flex;
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
                "REMATCH", Systems_UiTheme.Action, Systems_SceneRouter.LoadGame);
            Systems_UiTheme.ApplyPrimaryActionSize(rematchButton);

            // Carries the finished game's numbers to the menu, which outlives this
            // scene's box score by the width of a scene load. Only this button does
            // it — the in-game QUIT button leaves a game that has no result yet.
            Button menuButton = Systems_UiTheme.Button(
                "MENU",
                Systems_UiTheme.SurfaceRaised,
                () => Systems_SceneRouter.LoadMenu(Systems_GameSummary.From(_boxScore)));
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

            if (force || _game.Quarter != _lastQuarter)
            {
                _lastQuarter = _game.Quarter;
                _quarter.text = Systems_DisplayText.QuarterLabel(_lastQuarter);
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
        /// The one control available while the ball is live: leave.
        ///
        /// WHY IT EXISTS. There was a window in which this screen had no controls at
        /// all between kickoff and the final whistle — the box-score panel and the
        /// STATS button that opened it had been removed together, and the only two
        /// buttons left were inside the final overlay. A viewer who started a game
        /// was committed to the whole thing with no way back to the menu short of
        /// killing the app.
        ///
        /// THE SPEED CYCLE IS GONE, AND ITS JUSTIFICATION WENT WITH IT. It existed
        /// because "a full game is an hour in real time and the FINAL overlay is
        /// effectively unreachable" — a claim that was already false when it was
        /// written and is not close now. Systems_GameRules.QUARTER_SECONDS is 300,
        /// not 900, and almost all of it is burned in huddles rather than in real
        /// time, so a full game is a few minutes at 1x. A control that multiplies
        /// wall-clock by eight is a strange thing to put on the front of a game
        /// nobody has to wait for, and it invited a viewer to watch the simulation
        /// at a speed the animation was never composed for.
        ///
        /// Nothing writes Time.timeScale anywhere in the project now, which is why
        /// the OnDisable that used to reset it is gone too rather than left as a
        /// guard against a writer that no longer exists.
        /// </summary>
        private VisualElement BuildControlBar()
        {
            VisualElement bar = Systems_UiTheme.Row();
            bar.style.position = Position.Absolute;
            bar.style.left = 0;
            bar.style.right = 0;

            // ABOVE THE STATUS HUD'S FOOTER, which owns DEBUG on the left and the
            // version on the right. QUIT is centred so it never overlapped either
            // horizontally, but at bottom: 0 all three sat in the same band and read
            // as one row of three unrelated controls.
            bar.style.bottom = Systems_UiTheme.STATUS_FOOTER_HEIGHT;
            bar.style.justifyContent = Justify.Center;
            Systems_UiTheme.SetPadding(bar, Systems_UiTheme.SPACE_M);

            Button quit = Systems_UiTheme.Button(
                "QUIT", Systems_UiTheme.SurfaceRaised, Systems_SceneRouter.LoadMenu);
            quit.style.color = Systems_UiTheme.TextMuted;
            Systems_UiTheme.ApplyControlActionSize(quit);

            bar.Add(quit);
            return bar;
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

            // THE DOWN AND DISTANCE IS NOT TRUE ANY MORE. Systems_GameFlowSystem
            // resolves the last play like any other, so the chains are left showing
            // whatever the next snap WOULD have been — a finished game sat under a
            // "1st & 10 OPP 46" pill above a 0:00 clock, describing a down that will
            // never be played. There is no next situation, so nothing should claim
            // there is; the clock beside it stays, because 0:00 is the true and
            // interesting fact about a game that has ended.
            if (_situationPill != null)
            {
                _situationPill.style.display = DisplayStyle.None;
            }

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
