using System;
using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Turns a stream of independent plays into a game: downs, chains, turnovers,
    /// the scoreboard and the clock. Registered only in
    /// <see cref="Systems_SimMode.Game"/> — in training this class is not in the
    /// container at all and the episode loop is exactly what it always was.
    ///
    /// Two things here are worth understanding before changing anything.
    ///
    /// FRAME MIRRORING. Every policy in this project was fitted on "the offense
    /// attacks +Y". A real game needs two teams driving in opposite directions,
    /// which would put the defense's drives entirely outside the distribution
    /// every brain was trained on. Instead the world stays fixed — offense always
    /// attacks +Y — and a change of possession mirrors the field position through
    /// y -> -y. <see cref="Mirror"/> is the whole of it. Since revision 14 the
    /// team on "offense" for a snap is the KICKING team on a kickoff, and the
    /// receivers return toward −Y; the mirror at the whistle is the same one.
    ///
    /// ORDERING. This subscribes to Systems_PlayEndedMessage, which MessagePipe
    /// delivers synchronously at publish time — inside the referee's tick, or
    /// inside a collision callback for a tackle. The director defers its reset to
    /// the following FixedTick and only then calls NextSituation. So the game
    /// state is always fully resolved before the next snap is asked for, and the
    /// play's foul list — read here — is still intact.
    ///
    /// REVISION 14 PLAYS THE WHOLE GAME. A kickoff, a punt, a field goal and the try
    /// after a touchdown are snaps now, so what this class does after one is put the
    /// ball where the play left it rather than roll dice for a result. It also
    /// enforces penalties, keeps the out-of-bounds and two-minute-warning clock
    /// rules, sends out the kneel-down and the spike when the clock calls for them,
    /// and runs overtime the way the NFL does.
    /// </summary>
    public sealed class Systems_GameFlowSystem
        : Systems_ISpotProvider, IStartable, IFixedTickable, IDisposable
    {
        private readonly Systems_GameModel _game;
        private readonly Systems_PlayModel _play;
        private readonly Systems_SimHealthModel _health;
        private readonly ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;
        private readonly IPublisher<Systems_DownResolvedMessage> _resolvedPublisher;
        private readonly IPublisher<Systems_GameOverMessage> _gameOverPublisher;

        private IDisposable _subscription;

        /// <summary>
        /// Set when the clock hits 0:00 with the ball still live. The period does
        /// not end on the tick it expires — football lets the down finish — so the
        /// expiry is remembered here and acted on at the whistle.
        /// </summary>
        private bool _quarterExpiredMidPlay;

        /// <summary>
        /// A period that ended on a touchdown still owes the try, which is untimed.
        /// The quarter is advanced once the try has been played.
        /// </summary>
        private bool _quarterEndAfterTry;

        /// <summary>The clock crossed the two-minute mark during the live ball; it stops at the whistle.</summary>
        private bool _twoMinuteWarningDue;

        /// <summary>Whether this half's two-minute warning has been given.</summary>
        private bool _twoMinuteWarningGiven;

        /// <summary>Ticks the ball has been live this play, for time of possession.</summary>
        private int _liveTicks;

        /// <summary>
        /// Possessions each team has had in overtime, indexed by Systems_TeamId.
        /// Both teams are owed one; after that the next score wins.
        /// </summary>
        private readonly int[] _overtimePossessions = new int[2];

        public Systems_GameFlowSystem(
            Systems_GameModel game,
            Systems_PlayModel play,
            Systems_SimHealthModel health,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber,
            IPublisher<Systems_DownResolvedMessage> resolvedPublisher,
            IPublisher<Systems_GameOverMessage> gameOverPublisher)
        {
            _game = game;
            _play = play;
            _health = health;
            _endedSubscriber = endedSubscriber;
            _resolvedPublisher = resolvedPublisher;
            _gameOverPublisher = gameOverPublisher;
        }

        /// <summary>
        /// Runs the game clock down in real time while the ball is live, so the
        /// scoreboard ticks the way a broadcast clock does. A try is untimed.
        /// </summary>
        public void FixedTick()
        {
            if (!IsLive(_game.Phase) || _play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            _liveTicks += 1;
            _health.CountLiveTick();

            if (!_game.IsClockRunning || _play.Kind.IsTry())
            {
                return;
            }

            float before = _game.SecondsRemaining;

            if (_game.BurnClock(Systems_GameRules.SECONDS_PER_TICK))
            {
                _quarterExpiredMidPlay = true;
            }

            if (CrossesTwoMinuteWarning(before, _game.SecondsRemaining))
            {
                _twoMinuteWarningDue = true;
            }
        }

        /// <summary>
        /// Phases in which the ball can be snapped and the clock can run. Overtime
        /// is as live as regulation; Halftime deliberately is not, which is what
        /// keeps the clock still across the interval.
        /// </summary>
        private static bool IsLive(Systems_GamePhase phase)
        {
            return phase == Systems_GamePhase.Playing
                || phase == Systems_GamePhase.Overtime;
        }

        public void Start()
        {
            // Subscribed here rather than in the constructor, for the reason
            // Systems_EpisodeDirector documents: a handler registered during
            // container build is live before the rest of the graph exists.
            _subscription = _endedSubscriber.Subscribe(OnPlayEnded);
            _liveTicks = 0;
            _quarterExpiredMidPlay = false;
            _quarterEndAfterTry = false;
            _twoMinuteWarningDue = false;
            _twoMinuteWarningGiven = false;

            // Home receives the opening kickoff, so Away kicks it — a played
            // kickoff since revision 14, where it used to be Home's ball on its 35.
            _game.KickOff(
                Systems_TeamId.Home, OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
            _game.PrepareKick(
                Systems_TeamId.Away, OwnYardLineToY(Systems_GameRules.KICKOFF_YARD_LINE), false);
        }

        /// <summary>
        /// Systems_ISpotProvider. False from the moment the final whistle sets
        /// <see cref="Systems_GamePhase.Final"/>, which is what brings the
        /// simulation to a stop.
        /// </summary>
        public bool HasNextPlay => _game.Phase != Systems_GamePhase.Final;

        /// <summary>
        /// Systems_ISpotProvider. A played game holds the dead ball long enough for
        /// the HUD to say what just happened.
        /// </summary>
        public int DeadBallTicks => Systems_GameRules.DEAD_BALL_TICKS;

        /// <summary>
        /// Systems_ISpotProvider. The director asks; the chains answer. Every branch
        /// of OnPlayEnded leaves Systems_GameModel holding the next snap, so the
        /// model is the single source of truth.
        /// </summary>
        public Systems_PlaySituation NextSituation()
        {
            // Belt and braces against HasNextPlay being ignored: this has side
            // effects — it counts a play and restarts the clock.
            if (_game.Phase == Systems_GamePhase.Final)
            {
                return Situation();
            }

            // The first snap of the second half ends the interval.
            if (_game.Phase == Systems_GamePhase.Halftime)
            {
                _game.SetPhase(Systems_GamePhase.Playing);
            }

            _game.CountPlay();
            _liveTicks = 0;

            // A stopped clock restarts on the snap — except on a try, which is
            // untimed. Set here rather than at the whistle so the HUD can show it
            // stopped for the whole dead-ball period.
            _game.SetClockRunning(!_game.NextPlayKind.IsTry());

            return Situation();
        }

        private Systems_PlaySituation Situation()
        {
            return new Systems_PlaySituation(
                _game.LineOfScrimmageY, _game.Down, _game.YardsToGo,
                _game.NextPlayKind, _game.NextOnside);
        }

        // =====================================================================
        // The whistle
        // =====================================================================

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            if (_game.Phase == Systems_GamePhase.Final)
            {
                return;
            }

            Systems_TeamId offense = _game.Possession;
            Systems_PlayKind kind = message.Kind;
            float spotY = ClampToPlayableY(message.Spot.y);

            float yardsGained = YardsGainedBy(message, spotY);

            // Read before Resolve, which advances or resets it. Counting fourth
            // downs is the single most direct measure of whether drives actually
            // stall.
            int downBefore = _game.Down;
            bool wasInOvertime = _game.Phase == Systems_GamePhase.Overtime;
            bool bothHadOvertimeBallBefore = BothHaveHadOvertimeBall();

            int pointsScored = 0;
            Systems_TeamId scorer = offense;

            Systems_DownResult result = Resolve(
                message, offense, spotY, ref pointsScored, ref scorer,
                out Systems_FoulRuling ruling);

            // Kept before the clock can rename it: see Systems_DownResolvedMessage.PlayResult.
            Systems_DownResult playResult = result;

            // The chains moved on a flag, not a gain: an accepted defensive foul
            // that gave the offense a new series. Decided where it was enforced.
            bool penaltyFirstDown = result == Systems_DownResult.Penalty
                && ruling.Decision == Systems_FoulDecision.Enforced
                && ruling.FirstDown;

            float returnYards = ReturnYardsOf(message);

            bool clockStops = StopsClock(message, ruling);

            // A TIMEOUT IS THE HUDDLE THAT DOES NOT GET CHARGED. Asked only when the
            // clock would otherwise have kept running, and never on a play the
            // period already expired during. After Resolve, because who needs the
            // clock depends on who now has the ball.
            Systems_TeamId timeoutTeam = offense;

            bool timeoutCalled = !clockStops
                && !_quarterExpiredMidPlay
                && TryCallTimeout(out timeoutTeam);

            if (timeoutCalled)
            {
                clockStops = true;
                _timeoutsCalled++;
            }

            // The two-minute warning is a stoppage of its own: it stops a clock that
            // would have run, at the whistle of the play it fell during.
            bool twoMinuteWarning = false;

            if (_twoMinuteWarningDue)
            {
                _twoMinuteWarningDue = false;
                _twoMinuteWarningGiven = true;
                twoMinuteWarning = true;
                clockStops = true;
            }

            // The coach's clock calls for the next snap: the spike when the clock is
            // running and there is nothing else to stop it with, the kneel when
            // there is nothing left for the other side to stop.
            bool spikeNext = !clockStops && ShouldSpike(_game.Possession);

            if (!spikeNext && ShouldKneel(_game.Possession))
            {
                _game.SetNextPlay(Systems_PlayKind.KneelDown);
            }
            else if (spikeNext)
            {
                _game.SetNextPlay(Systems_PlayKind.Spike);
            }

            // Clock is burned AFTER the down is resolved, so a play that both
            // scores and ends the quarter still counts its points. Live seconds
            // were already burned tick by tick in FixedTick; only the huddle is left
            // to charge, and only if the clock kept running. A try is untimed.
            float huddle = spikeNext
                ? Systems_GameRules.HURRY_UP_SECONDS
                : Systems_GameRules.HUDDLE_SECONDS;

            bool huddleExpired = false;
            float huddleCharged = 0f;

            if (!clockStops && !kind.IsTry())
            {
                float before = _game.SecondsRemaining;
                huddleExpired = BurnHuddle(huddle, ref twoMinuteWarning);
                huddleCharged = before - _game.SecondsRemaining;

                // The warning fell inside the huddle: the clock ran to it and stopped.
                if (twoMinuteWarning)
                {
                    clockStops = true;
                }
            }

            float clockBurned = (kind.IsTry() ? 0f : _liveTicks * Systems_GameRules.SECONDS_PER_TICK)
                + huddleCharged;

            bool quarterExpired = _quarterExpiredMidPlay || huddleExpired;
            _quarterExpiredMidPlay = false;

            _game.SetClockRunning(!clockStops);

            // A touchdown as the clock dies still gets its try; the period ends after it.
            if (quarterExpired && _game.NextPlayKind.IsTry())
            {
                _quarterEndAfterTry = true;
                quarterExpired = false;
            }
            else if (kind.IsTry() && _quarterEndAfterTry)
            {
                _quarterEndAfterTry = false;
                quarterExpired = true;
            }

            if (quarterExpired)
            {
                result = AdvanceQuarter(result);
            }

            if (wasInOvertime && _game.Phase == Systems_GamePhase.Overtime
                && OvertimeIsOver(message, kind, offense, scorer, pointsScored, bothHadOvertimeBallBefore))
            {
                result = Systems_DownResult.EndOfGame;
            }

            _resolvedPublisher.Publish(new Systems_DownResolvedMessage(
                result,
                offense,
                message.Outcome,
                message.Call,
                yardsGained,
                pointsScored,
                _game.Down,
                _game.YardsToGo,
                clockBurned,
                timeoutCalled,
                timeoutTeam,
                ruling.Foul.Type,
                ruling.FoulTeam,
                ruling.Accepted,
                ruling.Yards,
                twoMinuteWarning,
                kind,
                scorer,
                playResult,
                returnYards,
                penaltyFirstDown));

            Tally(result, message, ruling);

            // One line per snap, in the Editor and development builds only
            // (Systems_Log is conditional). The tally every forty plays says WHAT a
            // game is made of; this says how each down came out, which is what it
            // takes to see why drives stall or flags pile up.
            Systems_Log.Info(
                $"[PoFootball] PLAY Q{_game.Quarter} {_game.SecondsRemaining:F0}s {offense} "
                + $"{kind}/{message.Call} -> {message.Outcome} ({message.Side}, {message.PhysicsTicks}t)"
                + $" = {result} {yardsGained:F1}yd | next {_game.Possession} {_game.NextPlayKind} "
                + $"{_game.Down}&{_game.YardsToGo:F1} own {(_game.LineOfScrimmageY - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD:F0}"
                + (ruling.Foul.IsNone
                    ? string.Empty
                    : $" | FLAG {ruling.Foul.Type} {ruling.FoulTeam} {(ruling.Accepted ? "accepted" : "declined")}"));

            // A fourth down is a fourth down whether the offense went for it or
            // kicked — a kick called before the snap turns the snap into a Punt or
            // a FieldGoal, and those are exactly the fourth downs worth counting.
            bool facedFourthDown = downBefore >= Systems_GameRules.DOWNS_PER_SERIES
                && (kind == Systems_PlayKind.Scrimmage
                    || kind == Systems_PlayKind.Punt
                    || kind == Systems_PlayKind.FieldGoal);

            if (facedFourthDown)
            {
                _fourthDowns++;
            }

            // Scrimmage plays only, and only snaps that were played: a kick gains
            // nothing for the offense by construction, a try is not a down, and a
            // kneel or a spike is the clock, not football.
            // ...nor a down a flag replaced, which the box score does not book either.
            bool isScrimmagePlay = kind == Systems_PlayKind.Scrimmage
                && message.Outcome != Systems_PlayOutcome.DeadBallFoul
                && playResult != Systems_DownResult.Penalty;

            if (isScrimmagePlay)
            {
                _scrimmagePlays++;
                _scrimmageYards += yardsGained;

                // THE SPLIT THE MEAN HIDES. Two measured games averaged a healthy
                // 6-8 yards a play out of runs that LOST a yard and completions that
                // went seventy: football's mean, nothing like football's plays. A
                // carry is any scrimmage snap where the ball did not leave the
                // passer's hand, as in Systems_StatsSystem; a sack is neither.
                bool thrown = message.Call.IsPass()
                    && (message.PassCompleted
                        || message.Outcome == Systems_PlayOutcome.Incompletion
                        || message.Outcome == Systems_PlayOutcome.Interception);

                if (thrown)
                {
                    _passAttempts++;

                    if (message.PassCompleted)
                    {
                        _completions++;
                    }
                }
                else if (message.Outcome != Systems_PlayOutcome.Sack)
                {
                    _rushCarries++;
                    _rushYards += yardsGained;
                }
            }

            if (playResult == Systems_DownResult.FirstDown || penaltyFirstDown)
            {
                _firstDowns++;
            }

            _outcomeCounts[(int)message.Outcome]++;

            // The same three numbers the REALISM line reports at the whistle, kept
            // current so the DEBUG sheet can give the verdict mid-game.
            _health.RecordPlay(
                isScrimmagePlay,
                yardsGained,
                facedFourthDown,
                result == Systems_DownResult.Touchdown && scorer == offense && !kind.IsKick(),
                _game.DriveIndex);

            if (_game.PlaysRun % PLAYS_PER_TALLY == 0)
            {
                Systems_Log.Info(
                    $"[PoFootball] After {_game.PlaysRun} plays / {_game.DriveIndex} drives"
                    + $" — outcomes: {Tally<Systems_PlayOutcome>(_outcomeCounts)}"
                    + $" | results: {Tally<Systems_DownResult>(_resultCounts)}");
            }

            if (result == Systems_DownResult.EndOfGame)
            {
                FinishGame();
            }
        }

        /// <summary>
        /// Yards this play is credited with in the box score and the REALISM line.
        ///
        /// AN INCOMPLETE PASS GAINS NOTHING, and an interception gains nothing for
        /// the team that threw it: the ball travelled, the offense did not. A kick
        /// gains nothing for the offense either. A fumble the other side recovered
        /// is credited to where it came out — the carrier really did carry it there
        /// — not to where the return ended.
        /// </summary>
        private float YardsGainedBy(Systems_PlayEndedMessage message, float spotY)
        {
            if (message.Kind != Systems_PlayKind.Scrimmage && message.Kind != Systems_PlayKind.TwoPoint)
            {
                return 0f;
            }

            switch (message.Outcome)
            {
                case Systems_PlayOutcome.Incompletion:
                case Systems_PlayOutcome.Interception:
                case Systems_PlayOutcome.DeadBallFoul:
                    return 0f;
            }

            float reached = message.PossessionChanged ? message.PossessionChangeY : spotY;

            return (ClampToPlayableY(reached) - _game.LineOfScrimmageY) / Systems_FieldModel.YARD;
        }

        /// <summary>
        /// How far the side that took the ball away carried it: from where it got
        /// the ball to where the return ended, toward −Y in the frame of the snap.
        /// A return that scored ended on the goal line it crossed.
        /// </summary>
        private static float ReturnYardsOf(Systems_PlayEndedMessage message)
        {
            if (!message.PossessionChanged || message.Side != Systems_TeamSide.Defense)
            {
                return 0f;
            }

            // Measured from the catch, end zone included, as the record books do:
            // a kickoff taken five deep and run back all the way is 105 yards.
            float end = Mathf.Max(message.Spot.y, Systems_FieldModel.OWN_GOAL_LINE_Y);

            return Mathf.Max(0f, (message.PossessionChangeY - end) / Systems_FieldModel.YARD);
        }

        // =====================================================================
        // Resolving a play
        // =====================================================================

        /// <summary>
        /// Applies one play to the game state and returns what it meant. Leaves the
        /// game model holding the next snap.
        /// </summary>
        private Systems_DownResult Resolve(
            Systems_PlayEndedMessage message,
            Systems_TeamId offense,
            float spotY,
            ref int pointsScored,
            ref Systems_TeamId scorer,
            out Systems_FoulRuling ruling)
        {
            ruling = Adjudicate(message, offense, spotY);

            if (ruling.Decision == Systems_FoulDecision.Enforced
                || ruling.Decision == Systems_FoulDecision.Offset)
            {
                return ruling.Result;
            }

            Systems_PlayOutcome outcome = message.Outcome;
            Systems_TeamSide side = message.Side;

            if (ruling.Decision == Systems_FoulDecision.ReturnSpotMoved)
            {
                outcome = ruling.Outcome;
                side = ruling.Side;
                spotY = ruling.SpotY;
            }

            switch (message.Kind)
            {
                case Systems_PlayKind.ExtraPoint:
                case Systems_PlayKind.TwoPoint:
                    return ResolveTry(message.Kind, outcome, side, offense, ref pointsScored, ref scorer);

                default:
                    return ResolvePlay(
                        message, outcome, side, offense, spotY, ref pointsScored, ref scorer);
            }
        }

        /// <summary>
        /// Every snap that is not a try: a down, a punt, a field goal, a kickoff, a
        /// kneel, a spike. The outcome says how the ball died and the side says who
        /// had it, so this is one switch for all of them.
        /// </summary>
        private Systems_DownResult ResolvePlay(
            Systems_PlayEndedMessage message,
            Systems_PlayOutcome outcome,
            Systems_TeamSide side,
            Systems_TeamId offense,
            float spotY,
            ref int pointsScored,
            ref Systems_TeamId scorer)
        {
            Systems_TeamId defense = offense.Opponent();
            Systems_PlayKind kind = message.Kind;

            switch (outcome)
            {
                case Systems_PlayOutcome.Touchdown:
                {
                    scorer = side == Systems_TeamSide.Offense ? offense : defense;

                    // A kick returned for a score is the receivers' possession, not a
                    // score against the run of play — overtime counts it as theirs.
                    if (scorer == defense && kind.IsKick())
                    {
                        NoteOvertimePossession(defense);
                    }

                    pointsScored = Systems_GameRules.TOUCHDOWN_POINTS;
                    _game.AddPoints(scorer, pointsScored);

                    float tryYardLine = ShouldGoForTwo(scorer)
                        ? Systems_GameRules.TWO_POINT_YARD_LINE
                        : Systems_GameRules.EXTRA_POINT_YARD_LINE;

                    _game.PrepareTry(
                        scorer,
                        Systems_FieldModel.ATTACKING_GOAL_LINE_Y - (tryYardLine * Systems_FieldModel.YARD),
                        ShouldGoForTwo(scorer) ? Systems_PlayKind.TwoPoint : Systems_PlayKind.ExtraPoint);

                    return Systems_DownResult.Touchdown;
                }

                case Systems_PlayOutcome.Safety:
                {
                    scorer = side == Systems_TeamSide.Offense ? offense : defense;
                    pointsScored = Systems_GameRules.SAFETY_POINTS;
                    _game.AddPoints(scorer, pointsScored);

                    // The side that conceded free-kicks from its own 20.
                    _game.PrepareKick(
                        scorer.Opponent(),
                        OwnYardLineToY(Systems_GameRules.SAFETY_FREE_KICK_YARD_LINE),
                        false);

                    return Systems_DownResult.Safety;
                }

                case Systems_PlayOutcome.FieldGoalGood:
                    pointsScored = Systems_GameRules.FIELD_GOAL_POINTS;
                    _game.AddPoints(offense, pointsScored);
                    PrepareKickoff(offense);
                    return Systems_DownResult.FieldGoalGood;

                case Systems_PlayOutcome.FieldGoalMissed:
                {
                    // The defense takes over at the SPOT OF THE KICK, seven yards
                    // behind the line — or at its own 20 if that is further out.
                    // That is what makes a long attempt a gamble instead of a free
                    // roll.
                    float spotOfKick = _game.LineOfScrimmageY
                        - (Systems_SimConstants.PLACEKICK_DEPTH_YARDS * Systems_FieldModel.YARD);

                    float takeover = Mathf.Max(
                        Mirror(spotOfKick),
                        OwnYardLineToY(Systems_GameRules.MISSED_FIELD_GOAL_MIN_YARD_LINE));

                    GiveBall(defense, ClampSeriesStart(takeover));
                    return Systems_DownResult.FieldGoalMissed;
                }

                case Systems_PlayOutcome.Touchback:
                {
                    Systems_TeamId receiver = side == Systems_TeamSide.Offense ? offense : defense;

                    float yardLine = kind == Systems_PlayKind.Kickoff
                        ? Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE
                        : Systems_GameRules.TOUCHBACK_YARD_LINE;

                    GiveBall(receiver, OwnYardLineToY(yardLine));
                    return Systems_DownResult.Touchback;
                }

                case Systems_PlayOutcome.Punt:
                    GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                    return Systems_DownResult.Punt;

                case Systems_PlayOutcome.Kickoff:
                    GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                    return Systems_DownResult.Kickoff;

                case Systems_PlayOutcome.KickBlocked:
                    GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                    return Systems_DownResult.BlockedKick;

                case Systems_PlayOutcome.KickRecovered:
                    // The kicking team keeps its own kick: a new series where it fell on it.
                    GiveBall(offense, ClampSeriesStart(spotY));
                    return Systems_DownResult.KickRecovered;

                case Systems_PlayOutcome.Interception:
                    GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                    return Systems_DownResult.Interception;

                case Systems_PlayOutcome.FumbleLost:
                    GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                    return Systems_DownResult.FumbleLost;
            }

            // Every other way a ball dies — a tackle, a sack, out of bounds, an
            // incompletion, a kneel, the trainer's ceiling — with the offense of the
            // snap holding it.
            if (side == Systems_TeamSide.Defense)
            {
                GiveBall(defense, ClampSeriesStart(Mirror(spotY)));
                return Systems_DownResult.FumbleLost;
            }

            // The ball went to the other side and came back: a new series for the
            // offense where it ended up, as on any change of possession.
            if (message.PossessionChanged)
            {
                GiveBall(offense, ClampSeriesStart(spotY));
                return Systems_DownResult.FirstDown;
            }

            return AdvanceChains(outcome, spotY, offense);
        }

        /// <summary>The chains: a first down, the next down, or the ball over on downs.</summary>
        private Systems_DownResult AdvanceChains(
            Systems_PlayOutcome outcome, float spotY, Systems_TeamId offense)
        {
            // An incompletion returns the ball to the previous spot; everything
            // else is spotted where it died.
            float nextSpot = outcome == Systems_PlayOutcome.Incompletion
                ? _game.LineOfScrimmageY
                : spotY;

            nextSpot = ClampSeriesStart(nextSpot);

            if (nextSpot >= _game.FirstDownMarkerY)
            {
                _game.StartSeries(nextSpot);
                return Systems_DownResult.FirstDown;
            }

            if (_game.Down >= Systems_GameRules.DOWNS_PER_SERIES)
            {
                GiveBall(offense.Opponent(), ClampSeriesStart(Mirror(nextSpot)));
                return Systems_DownResult.TurnoverOnDowns;
            }

            float yardsToGo = (_game.FirstDownMarkerY - nextSpot) / Systems_FieldModel.YARD;
            _game.AdvanceDown(nextSpot, Mathf.Max(yardsToGo, 0.1f));

            return outcome == Systems_PlayOutcome.Sack
                ? Systems_DownResult.Sack
                : Systems_DownResult.NextDown;
        }

        /// <summary>
        /// The try after a touchdown, played: one for a kick through, two for the
        /// ball in the end zone, two to the defense for taking it all the way back,
        /// nothing otherwise. Then the team that scored the touchdown kicks off.
        /// </summary>
        private Systems_DownResult ResolveTry(
            Systems_PlayKind kind,
            Systems_PlayOutcome outcome,
            Systems_TeamSide side,
            Systems_TeamId offense,
            ref int pointsScored,
            ref Systems_TeamId scorer)
        {
            Systems_DownResult result;

            // A try the offense fouled on is no good, whatever happened on it.
            bool offenseFouled = _play.FoulsBy(Systems_TeamSide.Offense) > 0;

            if (outcome == Systems_PlayOutcome.Touchdown && side == Systems_TeamSide.Defense)
            {
                scorer = offense.Opponent();
                pointsScored = Systems_GameRules.DEFENSIVE_TRY_POINTS;
                result = Systems_DownResult.DefensiveTry;
            }
            else if (!offenseFouled && outcome == Systems_PlayOutcome.FieldGoalGood)
            {
                pointsScored = Systems_GameRules.EXTRA_POINT_POINTS;
                result = Systems_DownResult.ExtraPointGood;
            }
            else if (!offenseFouled && outcome == Systems_PlayOutcome.Touchdown)
            {
                pointsScored = Systems_GameRules.TWO_POINT_POINTS;
                result = Systems_DownResult.TwoPointGood;

                if (kind == Systems_PlayKind.TwoPoint)
                {
                    _twoPointsMade++;
                }
            }
            else
            {
                result = kind == Systems_PlayKind.ExtraPoint
                    ? Systems_DownResult.ExtraPointMissed
                    : Systems_DownResult.TwoPointFailed;
            }

            if (kind == Systems_PlayKind.TwoPoint)
            {
                _twoPointsAttempted++;
            }

            if (pointsScored > 0)
            {
                _game.AddPoints(scorer, pointsScored);
            }

            PrepareKickoff(offense);
            return result;
        }

        /// <summary>
        /// The kickoff that follows a score, by the team that scored. Short when it
        /// is still more than a score down with the game nearly gone.
        /// </summary>
        private void PrepareKickoff(Systems_TeamId kickingTeam)
        {
            _game.PrepareKick(
                kickingTeam,
                OwnYardLineToY(Systems_GameRules.KICKOFF_YARD_LINE),
                ShouldTryOnside(kickingTeam));
        }

        /// <summary>
        /// Whether the scoring team goes for two. Fourth quarter only, and only at
        /// the margins where the kick leaves the game a score apart and two does
        /// not: down two (ties it), down five (inside a field goal), down ten
        /// (inside one score), up one (makes it a field goal) and up five (makes it
        /// a touchdown). That is the late-game column of the chart every staff
        /// carries; earlier in a game the kick is right at every one of them.
        ///
        /// Called AFTER the six points are on the board.
        /// </summary>
        private bool ShouldGoForTwo(Systems_TeamId scorer)
        {
            if (_game.Quarter < Systems_GameRules.QUARTER_COUNT)
            {
                return false;
            }

            int margin = _game.ScoreOf(scorer) - _game.ScoreOf(scorer.Opponent());

            if (_game.Phase == Systems_GamePhase.Overtime)
            {
                // Down one after the touchdown: two wins it, a kick only ties it and
                // the other side gets the ball back. Every staff goes for it.
                return margin == -1;
            }

            return margin == -10 || margin == -5 || margin == -2 || margin == 1 || margin == 5;
        }

        /// <summary>
        /// Whether the team that just scored should kick short: it is still behind by
        /// more than one score, and there is not enough time left to get the ball
        /// back any other way.
        /// </summary>
        private bool ShouldTryOnside(Systems_TeamId kickingTeam)
        {
            if (_game.Quarter < Systems_GameRules.QUARTER_COUNT
                || _game.Phase == Systems_GamePhase.Overtime)
            {
                return false;
            }

            if (_game.SecondsRemaining > Systems_GameRules.ONSIDE_SECONDS_REMAINING)
            {
                return false;
            }

            int deficit = _game.ScoreOf(kickingTeam.Opponent()) - _game.ScoreOf(kickingTeam);

            return deficit > Systems_GameRules.ONSIDE_TRAILING_BY;
        }

        private void GiveBall(Systems_TeamId team, float lineOfScrimmageY)
        {
            _game.GiveBallTo(team, lineOfScrimmageY);
            NoteOvertimePossession(team);
        }

        // =====================================================================
        // Penalties
        // =====================================================================

        /// <summary>
        /// Decides what the flags on this play mean: nothing, the down replayed, the
        /// penalty instead of the play, or the play standing with the return spot
        /// moved. When it enforces, the game model is left holding the next snap.
        ///
        /// THE OFFENDED TEAM CHOOSES, and it chooses by asking which of the two
        /// resulting situations is worth more — <see cref="ExpectedPoints"/> — the
        /// way a coach decides at a glance: decline a holding call on a play that
        /// lost twelve, take interference on third and long.
        /// </summary>
        private Systems_FoulRuling Adjudicate(
            Systems_PlayEndedMessage message, Systems_TeamId offense, float spotY)
        {
            if (_play.FoulCount == 0)
            {
                return default;
            }

            if (message.Outcome == Systems_PlayOutcome.DeadBallFoul)
            {
                return EnforceDeadBall(_play.GetFoul(0), offense, message.Kind);
            }

            Systems_Foul offenseFoul = WorstFoulBy(Systems_TeamSide.Offense);
            Systems_Foul defenseFoul = WorstFoulBy(Systems_TeamSide.Defense);

            // Tries resolve their own fouls: an offensive one voids the try.
            if (message.Kind.IsTry())
            {
                Systems_Foul any = offenseFoul.IsNone ? defenseFoul : offenseFoul;
                return Systems_FoulRuling.Declined(any, TeamOf(any.Side, offense));
            }

            if (!offenseFoul.IsNone && !defenseFoul.IsNone)
            {
                // Offsetting: the down is played again from where it started.
                _game.SetNextPlay(message.Kind == Systems_PlayKind.Kickoff
                    ? Systems_PlayKind.Kickoff
                    : Systems_PlayKind.Scrimmage, _game.NextOnside);

                return new Systems_FoulRuling(
                    Systems_FoulDecision.Offset, offenseFoul, TeamOf(offenseFoul.Side, offense),
                    0f, Systems_DownResult.Penalty);
            }

            return defenseFoul.IsNone
                ? AdjudicateOffensiveFoul(message, offense, spotY, offenseFoul)
                : AdjudicateDefensiveFoul(message, offense, spotY, defenseFoul);
        }

        /// <summary>
        /// The defense of the snap fouled. If it fouled while returning the ball,
        /// the return is pulled back; otherwise the offense takes the better of the
        /// play and the penalty.
        /// </summary>
        private Systems_FoulRuling AdjudicateDefensiveFoul(
            Systems_PlayEndedMessage message, Systems_TeamId offense, float spotY,
            Systems_Foul foul)
        {
            Systems_TeamId foulTeam = offense.Opponent();

            bool defenseEndedWithBall = DefenseEndedWithBall(message);

            if (defenseEndedWithBall && message.PossessionChanged
                && foul.Tick >= _play.PossessionChangeTick)
            {
                // A foul by the return team during its return, enforced from the
                // spot of the foul back toward its own goal (+Y in the frame of the
                // snap). A return touchdown it fouled on comes back.
                float yards = HalfTheDistance(
                    foul.Type.Yards(),
                    (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - foul.SpotY) / Systems_FieldModel.YARD);

                float spot = foul.SpotY + (yards * Systems_FieldModel.YARD);

                return Systems_FoulRuling.ReturnMoved(
                    foul, foulTeam, yards, ReturnOutcomeOf(message), Systems_TeamSide.Defense, spot);
            }

            if (message.Kind.IsKick() || message.Kind == Systems_PlayKind.Kickoff)
            {
                // A defensive foul on a kick the kicking team does not want back:
                // decline it and keep the field position the kick won.
                return Systems_FoulRuling.Declined(foul, foulTeam);
            }

            // The offense's choice.
            float lineOfScrimmage = _game.LineOfScrimmageY;
            float marker = _game.FirstDownMarkerY;

            float enforcedFrom = foul.Type.IsSpotFoul() ? foul.SpotY : lineOfScrimmage;
            float yardsAwarded;
            float newLine;

            if (foul.Type == Systems_FoulType.DefensivePassInterference)
            {
                newLine = foul.SpotY >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                    ? Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                        - (Systems_GameRules.END_ZONE_INTERFERENCE_YARD_LINE * Systems_FieldModel.YARD)
                    : Mathf.Max(foul.SpotY, lineOfScrimmage + Systems_FieldModel.YARD);

                yardsAwarded = (newLine - lineOfScrimmage) / Systems_FieldModel.YARD;
            }
            else
            {
                yardsAwarded = HalfTheDistance(
                    foul.Type.Yards(),
                    (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - enforcedFrom) / Systems_FieldModel.YARD);

                newLine = enforcedFrom + (yardsAwarded * Systems_FieldModel.YARD);
            }

            newLine = ClampSeriesStart(newLine);

            bool firstDown = foul.Type.IsAutomaticFirstDown() || newLine >= marker;

            bool scoredAnyway = message.Outcome == Systems_PlayOutcome.Touchdown
                && message.Side == Systems_TeamSide.Offense;

            bool accept;

            if (scoredAnyway)
            {
                accept = false;
            }
            else if (defenseEndedWithBall)
            {
                // It fouled before it got the ball: the offense keeps it.
                accept = true;
            }
            else
            {
                float withPenalty = ExpectedPoints(
                    newLine,
                    firstDown ? 1 : _game.Down,
                    firstDown ? Systems_GameRules.YARDS_TO_GAIN : (marker - newLine) / Systems_FieldModel.YARD);

                accept = withPenalty > ExpectedPointsOfPlay(message, spotY);
            }

            if (!accept)
            {
                return Systems_FoulRuling.Declined(foul, foulTeam);
            }

            if (firstDown)
            {
                _game.StartSeries(newLine);
            }
            else
            {
                _game.ReplayDown(newLine);
            }

            _game.SetNextPlay(Systems_PlayKind.Scrimmage);

            return new Systems_FoulRuling(
                Systems_FoulDecision.Enforced, foul, foulTeam, yardsAwarded, Systems_DownResult.Penalty,
                firstDown);
        }

        /// <summary>
        /// The offense of the snap fouled. If it fouled after the other side got the
        /// ball, the return is extended; if before, the defense keeps the turnover;
        /// otherwise the defense takes the worse of the play and the penalty for
        /// the offense.
        /// </summary>
        private Systems_FoulRuling AdjudicateOffensiveFoul(
            Systems_PlayEndedMessage message, Systems_TeamId offense, float spotY,
            Systems_Foul foul)
        {
            bool defenseEndedWithBall = DefenseEndedWithBall(message);
            bool defenseScored = message.Outcome == Systems_PlayOutcome.Touchdown
                && message.Side == Systems_TeamSide.Defense;

            if (defenseScored)
            {
                return Systems_FoulRuling.Declined(foul, offense);
            }

            if (defenseEndedWithBall
                && (foul.Tick >= _play.PossessionChangeTick
                    || foul.Type == Systems_FoulType.KickCatchInterference
                    || message.Kind.IsKick()))
            {
                // A foul by the offense of the snap against a returner — a facemask,
                // kick-catch interference, a cover man's hold — adds yardage to the
                // return, toward −Y. Interference is enforced from where it happened.
                float from = foul.Type == Systems_FoulType.KickCatchInterference
                    ? foul.SpotY
                    : spotY;

                float yards = HalfTheDistance(
                    foul.Type.Yards(),
                    (from - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD);

                float spot = from - (yards * Systems_FieldModel.YARD);

                return Systems_FoulRuling.ReturnMoved(
                    foul, offense, yards, ReturnOutcomeOf(message), Systems_TeamSide.Defense, spot);
            }

            if (defenseEndedWithBall)
            {
                // It fouled and then lost the ball anyway: the defense keeps it.
                return Systems_FoulRuling.Declined(foul, offense);
            }

            float lineOfScrimmage = _game.LineOfScrimmageY;
            float marker = _game.FirstDownMarkerY;
            float enforcedFrom = foul.Type.IsSpotFoul() ? foul.SpotY : lineOfScrimmage;

            bool lossOfDown = foul.Type == Systems_FoulType.IntentionalGrounding;

            // Grounding from the passer's own end zone is a safety.
            if (lossOfDown && enforcedFrom <= Systems_FieldModel.OWN_GOAL_LINE_Y)
            {
                _game.AddPoints(offense.Opponent(), Systems_GameRules.SAFETY_POINTS);
                _game.PrepareKick(
                    offense, OwnYardLineToY(Systems_GameRules.SAFETY_FREE_KICK_YARD_LINE), false);

                return new Systems_FoulRuling(
                    Systems_FoulDecision.Enforced, foul, offense, 0f, Systems_DownResult.Safety);
            }

            float yardsLost = HalfTheDistance(
                foul.Type.Yards(),
                (enforcedFrom - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD);

            float newLine = ClampSeriesStart(enforcedFrom - (yardsLost * Systems_FieldModel.YARD));

            int nextDown = lossOfDown ? _game.Down + 1 : _game.Down;
            bool overOnDowns = nextDown > Systems_GameRules.DOWNS_PER_SERIES;

            bool scoredOnIt = message.Outcome == Systems_PlayOutcome.Touchdown;

            bool accept;

            if (scoredOnIt)
            {
                accept = true;
            }
            else
            {
                float withPenalty = overOnDowns
                    ? -ExpectedPoints(Mirror(newLine), 1, Systems_GameRules.YARDS_TO_GAIN)
                    : ExpectedPoints(newLine, nextDown, (marker - newLine) / Systems_FieldModel.YARD);

                accept = withPenalty < ExpectedPointsOfPlay(message, spotY);
            }

            if (!accept)
            {
                return Systems_FoulRuling.Declined(foul, offense);
            }

            if (overOnDowns)
            {
                GiveBall(offense.Opponent(), ClampSeriesStart(Mirror(newLine)));

                return new Systems_FoulRuling(
                    Systems_FoulDecision.Enforced, foul, offense, yardsLost,
                    Systems_DownResult.TurnoverOnDowns);
            }

            if (lossOfDown)
            {
                _game.AdvanceDown(newLine, Mathf.Max((marker - newLine) / Systems_FieldModel.YARD, 0.1f));
            }
            else
            {
                _game.ReplayDown(newLine);
            }

            _game.SetNextPlay(Systems_PlayKind.Scrimmage);

            return new Systems_FoulRuling(
                Systems_FoulDecision.Enforced, foul, offense, yardsLost, Systems_DownResult.Penalty);
        }

        /// <summary>
        /// A false start, an offside, a delay of game: five yards, the down replayed.
        /// An offside that reaches the line to gain is a first down.
        /// </summary>
        private Systems_FoulRuling EnforceDeadBall(
            Systems_Foul foul, Systems_TeamId offense, Systems_PlayKind kind)
        {
            float lineOfScrimmage = _game.LineOfScrimmageY;
            bool againstOffense = foul.Side == Systems_TeamSide.Offense;

            float toward = againstOffense
                ? (lineOfScrimmage - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD
                : (Systems_FieldModel.ATTACKING_GOAL_LINE_Y - lineOfScrimmage) / Systems_FieldModel.YARD;

            float yards = HalfTheDistance(foul.Type.Yards(), toward);
            float newLine = ClampSeriesStart(
                lineOfScrimmage + ((againstOffense ? -yards : yards) * Systems_FieldModel.YARD));

            Systems_TeamId foulTeam = TeamOf(foul.Side, offense);
            bool deadBallFirstDown = false;

            if (kind.IsTry())
            {
                _game.PrepareTry(offense, newLine, kind);
            }
            else if (kind == Systems_PlayKind.Kickoff)
            {
                _game.PrepareKick(offense, newLine, _game.NextOnside);
            }
            else if (!againstOffense && newLine >= _game.FirstDownMarkerY)
            {
                _game.StartSeries(newLine);
                _game.SetNextPlay(Systems_PlayKind.Scrimmage);
                deadBallFirstDown = true;
            }
            else
            {
                _game.ReplayDown(newLine);
                _game.SetNextPlay(Systems_PlayKind.Scrimmage);
            }

            return new Systems_FoulRuling(
                Systems_FoulDecision.Enforced, foul, foulTeam, yards, Systems_DownResult.Penalty,
                deadBallFirstDown);
        }

        /// <summary>The foul on this side worth the most yards — the one an offended team would take.</summary>
        private Systems_Foul WorstFoulBy(Systems_TeamSide side)
        {
            Systems_Foul worst = default;
            float worstYards = -1f;

            for (int index = 0; index < _play.FoulCount; index++)
            {
                Systems_Foul foul = _play.GetFoul(index);

                if (foul.Side != side)
                {
                    continue;
                }

                // Interference is worth wherever it happened; rank it as a long one.
                float yards = foul.Type == Systems_FoulType.DefensivePassInterference
                    ? 20f
                    : foul.Type.Yards();

                if (yards > worstYards)
                {
                    worst = foul;
                    worstYards = yards;
                }
            }

            return worst;
        }

        private bool DefenseEndedWithBall(Systems_PlayEndedMessage message)
        {
            if (message.Outcome == Systems_PlayOutcome.Touchdown
                || message.Outcome == Systems_PlayOutcome.Safety)
            {
                return message.Side == Systems_TeamSide.Defense;
            }

            return message.Outcome.HandsTheBallOver() || message.Side == Systems_TeamSide.Defense;
        }

        /// <summary>What the play is called once a return has been pulled back or extended short of a score.</summary>
        private Systems_PlayOutcome ReturnOutcomeOf(Systems_PlayEndedMessage message)
        {
            if (message.Outcome == Systems_PlayOutcome.Touchdown
                || message.Outcome == Systems_PlayOutcome.Touchback
                || message.Outcome == Systems_PlayOutcome.Safety)
            {
                if (_play.PossessionChanged)
                {
                    return _play.ReturnOutcome;
                }

                // Nobody on the receiving side ever had it — a kick into the end
                // zone — so what it ends as is the kick's own result.
                return message.Kind == Systems_PlayKind.Kickoff
                    ? Systems_PlayOutcome.Kickoff
                    : message.Kind == Systems_PlayKind.Punt
                        ? Systems_PlayOutcome.Punt
                        : Systems_PlayOutcome.FumbleLost;
            }

            return message.Outcome;
        }

        /// <summary>
        /// The yards a penalty moves the ball: its full distance, or half the
        /// distance to the goal line it is moving toward if that is less.
        /// </summary>
        private static float HalfTheDistance(float yards, float yardsToGoal)
        {
            return Mathf.Min(yards, Mathf.Max(0f, yardsToGoal) * 0.5f);
        }

        private static Systems_TeamId TeamOf(Systems_TeamSide side, Systems_TeamId offense)
        {
            return side == Systems_TeamSide.Offense ? offense : offense.Opponent();
        }

        /// <summary>
        /// A rough expected-points value for the offense of having the ball here, on
        /// this down, with this far to go: about minus one and a half at its own
        /// goal line rising to five at the other one, less for each down used and
        /// each yard still to gain. Only ever compared with itself, to accept or
        /// decline a penalty, so the shape matters and the scale does not.
        /// </summary>
        private static float ExpectedPoints(float lineY, int down, float yardsToGo)
        {
            float yardLine = (lineY - Systems_FieldModel.OWN_GOAL_LINE_Y) / Systems_FieldModel.YARD;

            return -1.6f + (0.068f * yardLine)
                - (0.45f * (Mathf.Clamp(down, 1, 4) - 1))
                - (0.04f * (yardsToGo - Systems_GameRules.YARDS_TO_GAIN));
        }

        /// <summary>
        /// What the play as it happened is worth to the offense, before any flag —
        /// the other side of the accept-or-decline comparison.
        /// </summary>
        private float ExpectedPointsOfPlay(Systems_PlayEndedMessage message, float spotY)
        {
            if (message.Outcome == Systems_PlayOutcome.Touchdown)
            {
                return message.Side == Systems_TeamSide.Offense ? 6.9f : -6.9f;
            }

            if (DefenseEndedWithBall(message))
            {
                return -ExpectedPoints(Mirror(spotY), 1, Systems_GameRules.YARDS_TO_GAIN);
            }

            float lineOfScrimmage = _game.LineOfScrimmageY;
            float marker = _game.FirstDownMarkerY;

            float nextSpot = message.Outcome == Systems_PlayOutcome.Incompletion
                ? lineOfScrimmage
                : spotY;

            if (nextSpot >= marker)
            {
                return ExpectedPoints(nextSpot, 1, Systems_GameRules.YARDS_TO_GAIN);
            }

            int nextDown = _game.Down + 1;

            if (nextDown > Systems_GameRules.DOWNS_PER_SERIES)
            {
                return -ExpectedPoints(Mirror(nextSpot), 1, Systems_GameRules.YARDS_TO_GAIN);
            }

            return ExpectedPoints(nextSpot, nextDown, (marker - nextSpot) / Systems_FieldModel.YARD);
        }

        // =====================================================================
        // The clock
        // =====================================================================

        /// <summary>
        /// Whether the clock stops at this whistle. Every change of possession,
        /// every score, an incompletion, a flag — and going out of bounds only late
        /// in a half: the last two minutes of the first, the last five of the
        /// second. Through revision 13 it stopped on every one, which handed a team
        /// running out the clock a free timeout every time it went to the sideline.
        /// </summary>
        private bool StopsClock(Systems_PlayEndedMessage message, Systems_FoulRuling ruling)
        {
            if (ruling.Decision == Systems_FoulDecision.Enforced
                || ruling.Decision == Systems_FoulDecision.Offset)
            {
                return true;
            }

            if (message.PossessionChanged || message.Outcome.HandsTheBallOver())
            {
                return true;
            }

            switch (message.Outcome)
            {
                case Systems_PlayOutcome.Incompletion:
                case Systems_PlayOutcome.Touchdown:
                case Systems_PlayOutcome.Safety:
                case Systems_PlayOutcome.FieldGoalGood:
                case Systems_PlayOutcome.KickRecovered:
                case Systems_PlayOutcome.DeadBallFoul:
                    return true;

                case Systems_PlayOutcome.OutOfBounds:
                    return IsLateInHalf();

                default:
                    return false;
            }
        }

        private bool IsLateInHalf()
        {
            if (_game.Quarter == Systems_GameRules.QUARTER_COUNT / 2)
            {
                return _game.SecondsRemaining
                    <= Systems_GameRules.OUT_OF_BOUNDS_STOPS_CLOCK_FIRST_HALF_SECONDS;
            }

            if (_game.Phase == Systems_GamePhase.Overtime)
            {
                return _game.SecondsRemaining
                    <= Systems_GameRules.OUT_OF_BOUNDS_STOPS_CLOCK_FIRST_HALF_SECONDS;
            }

            return _game.Quarter >= Systems_GameRules.QUARTER_COUNT
                && _game.SecondsRemaining
                    <= Systems_GameRules.OUT_OF_BOUNDS_STOPS_CLOCK_SECOND_HALF_SECONDS;
        }

        /// <summary>Whether running the clock from one reading to the next passes this half's two-minute warning.</summary>
        private bool CrossesTwoMinuteWarning(float before, float after)
        {
            if (_twoMinuteWarningGiven || _game.Phase == Systems_GamePhase.Overtime)
            {
                return false;
            }

            if (_game.Quarter != Systems_GameRules.QUARTER_COUNT / 2
                && _game.Quarter != Systems_GameRules.QUARTER_COUNT)
            {
                return false;
            }

            return before > Systems_GameRules.TWO_MINUTE_WARNING_SECONDS
                && after <= Systems_GameRules.TWO_MINUTE_WARNING_SECONDS;
        }

        /// <summary>
        /// Charges the time between snaps. If the warning falls inside it the clock
        /// runs to the warning and stops there. Returns whether the period expired.
        /// </summary>
        private bool BurnHuddle(float seconds, ref bool twoMinuteWarning)
        {
            float before = _game.SecondsRemaining;
            float after = before - seconds;

            if (CrossesTwoMinuteWarning(before, after))
            {
                _game.BurnClock(before - Systems_GameRules.TWO_MINUTE_WARNING_SECONDS);
                _twoMinuteWarningGiven = true;
                twoMinuteWarning = true;
                _game.SetClockRunning(false);
                return false;
            }

            return _game.BurnClock(seconds);
        }

        /// <summary>
        /// Whether somebody stops the clock, and who.
        ///
        /// Only inside the last minute of a half, and only the team the clock is
        /// hurting. Before the half that is whoever has the ball. At the end of the
        /// game it is whoever is not ahead: the offense when it is level or behind,
        /// and otherwise the DEFENSE.
        ///
        /// Reads possession AFTER Resolve, so a turnover on downs is judged for the
        /// team that now has the ball.
        /// </summary>
        private bool TryCallTimeout(out Systems_TeamId caller)
        {
            caller = _game.Possession;

            if (_game.SecondsRemaining > Systems_GameRules.TIMEOUT_WINDOW_SECONDS
                || _game.SecondsRemaining <= 0f)
            {
                return false;
            }

            bool endOfGame = _game.Quarter >= Systems_GameRules.QUARTER_COUNT;
            bool endOfHalf = _game.Quarter == Systems_GameRules.QUARTER_COUNT / 2;

            if (!endOfGame && !endOfHalf)
            {
                return false;
            }

            // Nobody needs a timeout before a kickoff or a try.
            if (_game.NextPlayKind != Systems_PlayKind.Scrimmage)
            {
                return false;
            }

            if (endOfGame
                && _game.ScoreOf(caller) > _game.ScoreOf(caller.Opponent()))
            {
                caller = caller.Opponent();
            }

            if (_game.TimeoutsOf(caller) <= 0)
            {
                return false;
            }

            _game.UseTimeout(caller);
            return true;
        }

        /// <summary>
        /// VICTORY FORMATION. The team with the ball and the lead takes a knee when
        /// the downs it has left can run out the clock the other side cannot stop:
        /// a huddle a down, less a huddle for each timeout the other side still has.
        /// And at the end of the first half, from its own territory, with a single
        /// snap's worth left.
        /// </summary>
        private bool ShouldKneel(Systems_TeamId team)
        {
            if (_game.NextPlayKind != Systems_PlayKind.Scrimmage
                || _game.Phase == Systems_GamePhase.Overtime)
            {
                return false;
            }

            if (_game.Quarter == Systems_GameRules.QUARTER_COUNT / 2)
            {
                return _game.SecondsRemaining <= Systems_GameRules.HUDDLE_SECONDS
                    && _game.LineOfScrimmageY < 0f;
            }

            if (_game.Quarter < Systems_GameRules.QUARTER_COUNT
                || _game.ScoreOf(team) <= _game.ScoreOf(team.Opponent()))
            {
                return false;
            }

            int downsLeft = Systems_GameRules.DOWNS_PER_SERIES - _game.Down + 1;
            int stoppable = Mathf.Min(downsLeft, _game.TimeoutsOf(team.Opponent()));

            float canRunOff = (downsLeft - stoppable) * Systems_GameRules.HUDDLE_SECONDS
                + (downsLeft * Systems_GameRules.KNEEL_TICKS * Systems_GameRules.SECONDS_PER_TICK);

            return _game.SecondsRemaining <= canRunOff;
        }

        /// <summary>
        /// THE SPIKE. The clock is running late in a half, the team with the ball has
        /// no timeout left to stop it and a down to spare, and it needs the clock
        /// more than the down — level or behind at the end of the game, anyone before
        /// the half.
        /// </summary>
        private bool ShouldSpike(Systems_TeamId team)
        {
            if (_game.NextPlayKind != Systems_PlayKind.Scrimmage
                || _game.Phase == Systems_GamePhase.Overtime
                || _game.Down >= Systems_GameRules.DOWNS_PER_SERIES
                || _game.TimeoutsOf(team) > 0)
            {
                return false;
            }

            float seconds = _game.SecondsRemaining;

            if (seconds > SPIKE_WINDOW_SECONDS || seconds <= Systems_GameRules.HURRY_UP_SECONDS + 1f)
            {
                return false;
            }

            if (_game.Quarter == Systems_GameRules.QUARTER_COUNT / 2)
            {
                return true;
            }

            return _game.Quarter >= Systems_GameRules.QUARTER_COUNT
                && _game.ScoreOf(team) <= _game.ScoreOf(team.Opponent());
        }

        /// <summary>
        /// Seconds left in a half under which a team without timeouts spikes it: the
        /// last minute of an NFL half, on this game's clock (CLOCK_SCALE). A flat
        /// 20 s was a sixth of a two-minute quarter.
        /// </summary>
        private const float SPIKE_WINDOW_SECONDS = 60f * Systems_GameRules.CLOCK_SCALE;

        // =====================================================================
        // Periods and overtime
        // =====================================================================

        /// <summary>
        /// Handles a quarter reaching 0:00 and returns the result the HUD should
        /// announce instead of the down that just happened.
        /// </summary>
        private Systems_DownResult AdvanceQuarter(Systems_DownResult resultSoFar)
        {
            // A score at the buzzer still counts; announce the score, not the
            // period, and let the quarter roll silently underneath.
            bool announceScore = resultSoFar == Systems_DownResult.Touchdown
                || resultSoFar == Systems_DownResult.Safety
                || resultSoFar == Systems_DownResult.FieldGoalGood
                || resultSoFar == Systems_DownResult.ExtraPointGood
                || resultSoFar == Systems_DownResult.TwoPointGood
                || resultSoFar == Systems_DownResult.DefensiveTry;

            // Overtime expiring ends the game however it stands. The NFL regular
            // season allows a tie after one overtime period and so does this.
            if (_game.Phase == Systems_GamePhase.Overtime)
            {
                return Systems_DownResult.EndOfGame;
            }

            if (_game.Quarter >= Systems_GameRules.QUARTER_COUNT)
            {
                // LEVEL AFTER FOUR QUARTERS IS NOT THE END. Overtime settles it —
                // see Systems_GameRules.OVERTIME_SECONDS.
                if (_game.HomeScore == _game.AwayScore)
                {
                    _game.BeginOvertime();
                    _game.ResetTimeouts(Systems_GameRules.TIMEOUTS_IN_OVERTIME);
                    _overtimePossessions[0] = 0;
                    _overtimePossessions[1] = 0;

                    // The team that received the opening kickoff kicks, standing in
                    // for the overtime coin toss.
                    _game.PrepareKick(
                        _game.OpeningPossession,
                        OwnYardLineToY(Systems_GameRules.KICKOFF_YARD_LINE),
                        false);

                    return announceScore ? resultSoFar : Systems_DownResult.EndOfQuarter;
                }

                return Systems_DownResult.EndOfGame;
            }

            int nextQuarter = _game.Quarter + 1;
            _game.BeginQuarter(nextQuarter);

            // Halftime resets field position: whoever received the opening kickoff
            // kicks off the second half. Between the first and second, and the third
            // and fourth, only the period changes — the drive carries on.
            if (nextQuarter == 3)
            {
                _game.SetPhase(Systems_GamePhase.Halftime);
                _game.SetClockRunning(false);
                _game.ResetTimeouts(Systems_GameRules.TIMEOUTS_PER_HALF);
                _twoMinuteWarningGiven = false;
                _quarterEndAfterTry = false;

                _game.PrepareKick(
                    _game.OpeningPossession,
                    OwnYardLineToY(Systems_GameRules.KICKOFF_YARD_LINE),
                    false);
            }

            return announceScore ? resultSoFar : Systems_DownResult.EndOfQuarter;
        }

        private void NoteOvertimePossession(Systems_TeamId team)
        {
            if (_game.Phase == Systems_GamePhase.Overtime)
            {
                _overtimePossessions[(int)team]++;
            }
        }

        private bool BothHaveHadOvertimeBall()
        {
            return _overtimePossessions[0] > 0 && _overtimePossessions[1] > 0;
        }

        /// <summary>
        /// OVERTIME AS THE NFL PLAYS IT (revision 14). Both teams are owed a
        /// possession, a touchdown on the first included. It ends:
        ///
        ///   - on a score by the side WITHOUT the ball — a pick-six, a safety — at any
        ///     point, because the side with the ball has had its chance and lost it;
        ///   - on any score that puts a team ahead once both have had the ball;
        ///   - when a possession ends without a score, both teams have had the ball,
        ///     and the scores differ — the trailing side has used its chance.
        ///
        /// A kick returned for a score counts as the RECEIVING side's possession,
        /// not as a score against the run of play. Through revision 13 overtime was
        /// sudden death, because nothing in the game could count a possession owed.
        /// </summary>
        private bool OvertimeIsOver(
            Systems_PlayEndedMessage message,
            Systems_PlayKind kind,
            Systems_TeamId offense,
            Systems_TeamId scorer,
            int pointsScored,
            bool bothHadBallBefore)
        {
            bool level = _game.HomeScore == _game.AwayScore;

            if (pointsScored > 0)
            {
                bool againstTheRunOfPlay = scorer != offense && !kind.IsKick() && !kind.IsTry();

                if (againstTheRunOfPlay)
                {
                    return true;
                }

                // A touchdown that does not yet decide it still gets its try; the
                // try itself is judged below once it has been played.
                return BothHaveHadOvertimeBall() && !level
                    && (_game.ScoreOf(scorer) > _game.ScoreOf(scorer.Opponent()) || kind.IsTry());
            }

            if (kind.IsTry())
            {
                return BothHaveHadOvertimeBall() && !level;
            }

            bool possessionEnded = _game.Possession != offense
                && kind != Systems_PlayKind.Kickoff;

            return possessionEnded && bothHadBallBefore && !level;
        }

        private void FinishGame()
        {
            _game.SetPhase(Systems_GamePhase.Final);
            _gameOverPublisher.Publish(
                new Systems_GameOverMessage(_game.HomeScore, _game.AwayScore));

            // The only machine-readable account of a finished game. The final
            // overlay says all of this on screen, but the screen is UI Toolkit in
            // Overlay mode and does not appear in any camera capture.
            Debug.Log(
                $"[PoFootball] FINAL {_game.HomeScore}-{_game.AwayScore} "
                + $"after {_game.PlaysRun} plays, {_game.DriveIndex} drives. "
                + $"Punts {_punts}, FG {_fieldGoalsMade}/{_fieldGoalsAttempted}, "
                + $"XP {_extraPointsMade}/{_extraPointsAttempted}, "
                + $"2-pt {_twoPointsMade}/{_twoPointsAttempted}, "
                + $"safeties {_safeties}, turnovers on downs {_turnoversOnDowns}, "
                + $"interceptions {_interceptions}, fumbles lost {_fumblesLost}, "
                + $"return TDs {_returnTouchdowns}, blocked kicks {_blockedKicks}, "
                + $"kicks recovered {_kicksRecovered}, sacks {_sacks}, "
                + $"penalties {_penaltiesAccepted} accepted / {_penaltiesFlagged} flagged, "
                + $"timeouts {_timeoutsCalled}.");

            float yardsPerPlay = _scrimmagePlays > 0
                ? _scrimmageYards / _scrimmagePlays
                : 0f;

            float touchdownsPerDrive = _game.DriveIndex > 0
                ? _offensiveTouchdowns / (float)_game.DriveIndex
                : 0f;

            // THE THREE NUMBERS THAT SAY WHETHER THIS IS FOOTBALL. Real football
            // runs about 5.5 yards a play, faces a fourth down on roughly one series
            // in three, and scores a touchdown on about one drive in five.
            // "n/a", not 0.00, when there was nothing to measure: a game with no
            // carries ran for no yards a carry, it did not run for zero.
            string yardsPerCarry = _rushCarries > 0
                ? (_rushYards / _rushCarries).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                : "n/a";
            string completionRate = _passAttempts > 0
                ? (_completions / (float)_passAttempts).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                : "n/a";

            // The last three were added after a game read 6.4 yards a play from
            // minus-yardage runs and seventy-yard completions. Appended, so a parser
            // of the first four (Editor_RealismEval) reads old and new lines alike.
            Debug.Log(
                $"[PoFootball] REALISM  yards/play {yardsPerPlay:F2}"
                + $" | 4th downs faced {_fourthDowns}"
                + $" | TD/drive {touchdownsPerDrive:F2}"
                + $" | scrimmage plays {_scrimmagePlays}"
                + $" | rush yd/carry {yardsPerCarry}"
                + $" | first downs {_firstDowns}"
                + $" | completion {completionRate}");

            Systems_Log.Info($"[PoFootball] Down results: {Tally<Systems_DownResult>(_resultCounts)}");
            Systems_Log.Info($"[PoFootball] Play outcomes: {Tally<Systems_PlayOutcome>(_outcomeCounts)}");
        }

        // =====================================================================
        // Tallies
        // =====================================================================

        /// <summary>
        /// Every down result the game produced, indexed by Systems_DownResult, and
        /// every play outcome indexed by Systems_PlayOutcome. Diagnostic: a final
        /// score alone cannot tell you that a game reached the whistle by throwing
        /// four hundred interceptions.
        /// </summary>
        private readonly int[] _resultCounts =
            new int[System.Enum.GetValues(typeof(Systems_DownResult)).Length];

        private readonly int[] _outcomeCounts =
            new int[System.Enum.GetValues(typeof(Systems_PlayOutcome)).Length];

        private const int PLAYS_PER_TALLY = 40;

        private int _punts;
        private int _fourthDowns;
        private int _scrimmagePlays;
        private float _scrimmageYards;
        private int _rushCarries;
        private float _rushYards;
        private int _passAttempts;
        private int _completions;
        private int _firstDowns;
        private int _fieldGoalsAttempted;
        private int _fieldGoalsMade;
        private int _extraPointsAttempted;
        private int _extraPointsMade;
        private int _safeties;
        private int _turnoversOnDowns;
        private int _interceptions;
        private int _fumblesLost;
        private int _timeoutsCalled;
        private int _twoPointsAttempted;
        private int _twoPointsMade;
        private int _returnTouchdowns;
        private int _offensiveTouchdowns;
        private int _blockedKicks;
        private int _kicksRecovered;
        private int _sacks;
        private int _penaltiesFlagged;
        private int _penaltiesAccepted;

        /// <summary>
        /// Tallies the rare results, purely so the final log line can prove a game
        /// actually exercised the rules rather than running forty handoffs.
        /// </summary>
        private void Tally(
            Systems_DownResult result, Systems_PlayEndedMessage message, Systems_FoulRuling ruling)
        {
            _resultCounts[(int)result]++;
            _penaltiesFlagged += _play.FoulCount;

            if (ruling.Accepted)
            {
                _penaltiesAccepted++;
            }

            if (message.Outcome == Systems_PlayOutcome.Touchdown && !message.Kind.IsTry())
            {
                if (message.Side == Systems_TeamSide.Offense && message.Kind == Systems_PlayKind.Scrimmage)
                {
                    _offensiveTouchdowns++;
                }
                else
                {
                    _returnTouchdowns++;
                }
            }

            if (message.Outcome == Systems_PlayOutcome.Sack)
            {
                _sacks++;
            }

            if (message.Kind == Systems_PlayKind.ExtraPoint)
            {
                _extraPointsAttempted++;

                if (result == Systems_DownResult.ExtraPointGood)
                {
                    _extraPointsMade++;
                }
            }

            switch (result)
            {
                case Systems_DownResult.Punt:
                    _punts++;
                    break;
                case Systems_DownResult.FieldGoalGood:
                    _fieldGoalsAttempted++;
                    _fieldGoalsMade++;
                    break;
                case Systems_DownResult.FieldGoalMissed:
                    _fieldGoalsAttempted++;
                    break;
                case Systems_DownResult.Safety:
                    _safeties++;
                    break;
                case Systems_DownResult.TurnoverOnDowns:
                    _turnoversOnDowns++;
                    break;
                case Systems_DownResult.Interception:
                    _interceptions++;
                    break;
                case Systems_DownResult.FumbleLost:
                    _fumblesLost++;
                    break;
                case Systems_DownResult.BlockedKick:
                    _blockedKicks++;
                    break;
                case Systems_DownResult.KickRecovered:
                    _kicksRecovered++;
                    break;
            }

            if (message.Kind == Systems_PlayKind.FieldGoal && result == Systems_DownResult.BlockedKick)
            {
                _fieldGoalsAttempted++;
            }
        }

        private static string Tally<TEnum>(int[] counts) where TEnum : System.Enum
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder();

            for (int index = 0; index < counts.Length; index++)
            {
                if (counts[index] == 0)
                {
                    continue;
                }

                if (text.Length > 0)
                {
                    text.Append(", ");
                }

                text.Append((TEnum)System.Enum.ToObject(typeof(TEnum), index));
                text.Append(' ');
                text.Append(counts[index]);
            }

            return text.Length == 0 ? "none" : text.ToString();
        }

        // =====================================================================
        // Geometry
        // =====================================================================

        /// <summary>
        /// Reflects a field position into the opposing team's attacking frame.
        /// Y = 0 is midfield, so the reflection is a plain negation — the whole
        /// trick that lets one set of policies play both sides.
        /// </summary>
        private static float Mirror(float y)
        {
            return -y;
        }

        /// <summary>Own N-yard line expressed in the attacking frame.</summary>
        private static float OwnYardLineToY(float yardLine)
        {
            return Systems_FieldModel.OWN_GOAL_LINE_Y + (yardLine * Systems_FieldModel.YARD);
        }

        /// <summary>Keeps a dead-ball spot inside the field, end zones included.</summary>
        private static float ClampToPlayableY(float y)
        {
            return Mathf.Clamp(
                y, Systems_FieldModel.OWN_BACK_LINE_Y, Systems_FieldModel.ATTACKING_BACK_LINE_Y);
        }

        /// <summary>
        /// Keeps a line of scrimmage out of both end zones. A snap from inside one
        /// would put half the formation behind the back line.
        /// </summary>
        private static float ClampSeriesStart(float y)
        {
            float margin = Systems_GameRules.MIN_YARDS_FROM_GOAL_LINE * Systems_FieldModel.YARD;

            return Mathf.Clamp(
                y,
                Systems_FieldModel.OWN_GOAL_LINE_Y + margin,
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y - margin);
        }

        public void Dispose()
        {
            _subscription?.Dispose();
        }
    }

    /// <summary>What the officials' flags came to on one play.</summary>
    internal enum Systems_FoulDecision
    {
        /// <summary>No flag, or one the offended team declined. The play stands.</summary>
        None = 0,

        /// <summary>The penalty replaced the play; the game model holds the next snap.</summary>
        Enforced = 1,

        /// <summary>Both sides fouled: the down is replayed from where it started.</summary>
        Offset = 2,

        /// <summary>The play stands, but the return it ended in is moved by the penalty.</summary>
        ReturnSpotMoved = 3
    }

    /// <summary>The ruling on a play's flags, for Systems_GameFlowSystem and its message.</summary>
    internal readonly struct Systems_FoulRuling
    {
        public readonly Systems_FoulDecision Decision;
        public readonly Systems_Foul Foul;
        public readonly Systems_TeamId FoulTeam;
        public readonly float Yards;
        public readonly Systems_DownResult Result;
        public readonly Systems_PlayOutcome Outcome;
        public readonly Systems_TeamSide Side;
        public readonly float SpotY;

        /// <summary>
        /// The enforcement started a new series for the offended offense. Decided
        /// where the penalty is walked off, not inferred afterwards from the line to
        /// gain moving: the marker is clamped at the goal line, so an automatic
        /// first down inside the ten left it where it was and went uncounted.
        /// </summary>
        public readonly bool FirstDown;

        public Systems_FoulRuling(
            Systems_FoulDecision decision, Systems_Foul foul, Systems_TeamId foulTeam,
            float yards, Systems_DownResult result, bool firstDown = false)
            : this(decision, foul, foulTeam, yards, result,
                Systems_PlayOutcome.None, Systems_TeamSide.Offense, 0f, firstDown)
        {
        }

        private Systems_FoulRuling(
            Systems_FoulDecision decision, Systems_Foul foul, Systems_TeamId foulTeam,
            float yards, Systems_DownResult result,
            Systems_PlayOutcome outcome, Systems_TeamSide side, float spotY,
            bool firstDown = false)
        {
            FirstDown = firstDown;
            Decision = decision;
            Foul = foul;
            FoulTeam = foulTeam;
            Yards = yards;
            Result = result;
            Outcome = outcome;
            Side = side;
            SpotY = spotY;
        }

        /// <summary>Whether a flag was taken rather than the play.</summary>
        public bool Accepted =>
            Decision == Systems_FoulDecision.Enforced
            || Decision == Systems_FoulDecision.ReturnSpotMoved;

        public static Systems_FoulRuling Declined(Systems_Foul foul, Systems_TeamId foulTeam)
        {
            return new Systems_FoulRuling(
                Systems_FoulDecision.None, foul, foulTeam, 0f, Systems_DownResult.NextDown);
        }

        public static Systems_FoulRuling ReturnMoved(
            Systems_Foul foul, Systems_TeamId foulTeam, float yards,
            Systems_PlayOutcome outcome, Systems_TeamSide side, float spotY)
        {
            return new Systems_FoulRuling(
                Systems_FoulDecision.ReturnSpotMoved, foul, foulTeam, yards,
                Systems_DownResult.NextDown, outcome, side, spotY);
        }
    }
}
