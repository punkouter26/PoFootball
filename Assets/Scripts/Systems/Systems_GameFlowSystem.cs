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
    /// y -> -y. A carrier tackled at the opponent's 30 leaves the ball at Y = +20
    /// yd; the opponent takes over on its own 30, which in ITS attacking frame is
    /// Y = -20 yd. The mirror is exact, no policy sees anything unfamiliar, and
    /// the scoreboard is still a true account of the game. <see cref="Mirror"/> is
    /// the whole of it.
    ///
    /// ORDERING. This subscribes to Systems_PlayEndedMessage, which MessagePipe
    /// delivers synchronously at publish time — inside the referee's tick, or
    /// inside a collision callback for a tackle. The director defers its reset to
    /// the following FixedTick and only then calls NextLineOfScrimmageY. So the
    /// game state is always fully resolved before the next spot is asked for,
    /// whichever order the two handlers happen to be invoked in.
    /// </summary>
    public sealed class Systems_GameFlowSystem
        : Systems_ISpotProvider, IStartable, IFixedTickable, IDisposable
    {
        private readonly Systems_GameModel _game;
        private readonly Systems_PlayModel _play;
        private readonly Systems_IKickModel _kickModel;
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

        /// <summary>Ticks the ball has been live this play, for time of possession.</summary>
        private int _liveTicks;

        public Systems_GameFlowSystem(
            Systems_GameModel game,
            Systems_PlayModel play,
            Systems_IKickModel kickModel,
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber,
            IPublisher<Systems_DownResolvedMessage> resolvedPublisher,
            IPublisher<Systems_GameOverMessage> gameOverPublisher)
        {
            _game = game;
            _play = play;
            _kickModel = kickModel;
            _endedSubscriber = endedSubscriber;
            _resolvedPublisher = resolvedPublisher;
            _gameOverPublisher = gameOverPublisher;
        }

        /// <summary>
        /// Runs the game clock down in real time while the ball is live, so the
        /// scoreboard ticks the way a broadcast clock does. Burning a play's whole
        /// cost at the whistle instead would leave the clock frozen through the
        /// only part of the game anyone is watching, then jump it 25 seconds.
        ///
        /// The huddle between snaps is still charged at the whistle — there is no
        /// real time between plays here to spend it over.
        /// </summary>
        public void FixedTick()
        {
            if (!IsLive(_game.Phase) || _play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            _liveTicks += 1;

            if (!_game.IsClockRunning)
            {
                return;
            }

            if (_game.BurnClock(Systems_GameRules.SECONDS_PER_TICK))
            {
                _quarterExpiredMidPlay = true;
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
            _game.KickOff(
                Systems_TeamId.Home, OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
        }

        /// <summary>
        /// Systems_ISpotProvider. False from the moment the final whistle sets
        /// <see cref="Systems_GamePhase.Final"/>, which is what brings the
        /// simulation to a stop: the director asks this before every snap and
        /// simply stops re-forming the teams once the answer is no.
        /// </summary>
        public bool HasNextPlay => _game.Phase != Systems_GamePhase.Final;

        /// <summary>
        /// Systems_ISpotProvider. A played game holds the dead ball long enough for
        /// the HUD to say what just happened; see the interface for why that is
        /// answered here rather than inside the director.
        /// </summary>
        public int DeadBallTicks => Systems_GameRules.DEAD_BALL_TICKS;

        /// <summary>
        /// Systems_ISpotProvider. The director asks; the chains answer.
        ///
        /// There is deliberately no separate "next spot" field. Every branch of
        /// <see cref="Resolve"/> leaves Systems_GameModel.LineOfScrimmageY at the
        /// spot the next snap belongs on, so the model is the single source of
        /// truth and there is no second copy to fall out of step with it.
        /// </summary>
        public Systems_PlaySituation NextSituation()
        {
            // Belt and braces against HasNextPlay being ignored. This method has
            // side effects — it counts a play and restarts the clock — so a caller
            // that asked for a spot after the final whistle would inflate the
            // statistics of a game that had already been totalled. Nothing in the
            // project does that any more; this makes it harmless if anything ever
            // does again.
            if (_game.Phase == Systems_GamePhase.Final)
            {
                return Situation();
            }

            // The first snap of the second half ends the interval. Halftime is a
            // real phase rather than a decorative one — the clock does not run
            // through it, which is why Systems_GamePhase.Halftime exists at all.
            if (_game.Phase == Systems_GamePhase.Halftime)
            {
                _game.SetPhase(Systems_GamePhase.Playing);
            }

            _game.CountPlay();
            _liveTicks = 0;

            // A stopped clock restarts on the snap. Set here rather than at the
            // whistle so the HUD can show it stopped for the whole dead-ball
            // period, which is when a viewer actually reads it.
            _game.SetClockRunning(true);

            return Situation();
        }

        private Systems_PlaySituation Situation()
        {
            return new Systems_PlaySituation(
                _game.LineOfScrimmageY, _game.Down, _game.YardsToGo);
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            if (_game.Phase == Systems_GamePhase.Final)
            {
                return;
            }

            Systems_TeamId offense = _game.Possession;
            float spotY = ClampToPlayableY(message.Spot.y);
            float yardsGained = (spotY - _game.LineOfScrimmageY) / Systems_FieldModel.YARD;

            bool clockStops = StopsClock(message.Outcome);
            int pointsScored = 0;

            // Read before Resolve, which advances or resets it. Counting fourth
            // downs is the single most direct measure of whether drives actually
            // stall — an offense that never faces one can never punt or kick, which
            // is exactly the pathology these tallies were added to chase.
            int downBefore = _game.Down;

            Systems_DownResult result = Resolve(
                message.Outcome, spotY, offense, ref pointsScored);

            // Clock is burned AFTER the down is resolved, so a play that both
            // scores and ends the quarter still counts its points. Expiry then
            // overrides the result, because what the HUD should announce at 0:00
            // is the end of the period.
            // Live seconds were already burned tick by tick in FixedTick; only
            // the huddle is left to charge, and only if the clock kept running.
            bool huddleExpired = !clockStops
                && _game.BurnClock(Systems_GameRules.HUDDLE_SECONDS);

            float clockBurned = (_liveTicks * Systems_GameRules.SECONDS_PER_TICK)
                + (clockStops ? 0f : Systems_GameRules.HUDDLE_SECONDS);

            bool quarterExpired = _quarterExpiredMidPlay || huddleExpired;
            _quarterExpiredMidPlay = false;

            _game.SetClockRunning(!clockStops);

            if (quarterExpired)
            {
                result = AdvanceQuarter(result);
            }

            // SUDDEN DEATH. Any score in overtime ends it on the spot — checked
            // after Resolve so the points are already on the board, and after
            // AdvanceQuarter so an overtime clock expiring on the same play does not
            // get to overrule a winning score.
            if (_game.Phase == Systems_GamePhase.Overtime && pointsScored > 0)
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
                clockBurned));

            CountKick(result);
            _resultCounts[(int)result]++;

            if (downBefore >= Systems_GameRules.DOWNS_PER_SERIES)
            {
                _fourthDowns++;
            }

            // Scrimmage plays only. A kick is spotted at the line by the referee so
            // it contributes zero yards by construction, and counting it would drag
            // the average down without describing anything the offense did.
            if (message.Outcome != Systems_PlayOutcome.Punt
                && message.Outcome != Systems_PlayOutcome.FieldGoalGood
                && message.Outcome != Systems_PlayOutcome.FieldGoalMissed)
            {
                _scrimmagePlays++;
                _scrimmageYards += yardsGained;
            }
            _outcomeCounts[(int)message.Outcome]++;

            // Progress tally, so a pathological game can be diagnosed without
            // sitting through forty minutes of it to reach the final whistle.
            if (_game.PlaysRun % PLAYS_PER_TALLY == 0)
            {
                Debug.Log(
                    $"[PoFootball] After {_game.PlaysRun} plays / {_game.DriveIndex} drives"
                    + $" — outcomes: {Tally<Systems_PlayOutcome>(_outcomeCounts)}"
                    + $" | results: {Tally<Systems_DownResult>(_resultCounts)}");
            }

            if (result == Systems_DownResult.EndOfGame)
            {
                _game.SetPhase(Systems_GamePhase.Final);
                _gameOverPublisher.Publish(
                    new Systems_GameOverMessage(_game.HomeScore, _game.AwayScore));

                // The only machine-readable account of a finished game. The final
                // overlay says all of this on screen, but the screen is UI Toolkit
                // in Overlay mode and does not appear in any camera capture — so
                // without this line the only way to check that a game reached the
                // whistle with sane football in it is to sit and watch one.
                //
                // Game mode only, by registration: this whole class is absent from
                // the container in Systems_SimMode.Training.
                Debug.Log(
                    $"[PoFootball] FINAL {_game.HomeScore}-{_game.AwayScore} "
                    + $"after {_game.PlaysRun} plays, {_game.DriveIndex} drives. "
                    + $"Punts {_punts}, FG {_fieldGoalsMade}/{_fieldGoalsAttempted}, "
                    + $"safeties {_safeties}, turnovers on downs {_turnoversOnDowns}, "
                    + $"fumbles lost {_fumblesLost}.");

                float yardsPerPlay = _scrimmagePlays > 0
                    ? _scrimmageYards / _scrimmagePlays
                    : 0f;

                float touchdownsPerDrive = _game.DriveIndex > 0
                    ? _resultCounts[(int)Systems_DownResult.Touchdown] / (float)_game.DriveIndex
                    : 0f;

                // THE THREE NUMBERS THAT SAY WHETHER THIS IS FOOTBALL. Real football
                // runs about 5.5 yards a play, faces a fourth down on roughly one
                // series in three, and scores a touchdown on about one drive in five.
                // Everything else in these logs is detail; this line is the verdict.
                Debug.Log(
                    $"[PoFootball] REALISM  yards/play {yardsPerPlay:F2}"
                    + $" | 4th downs faced {_fourthDowns}"
                    + $" | TD/drive {touchdownsPerDrive:F2}"
                    + $" | scrimmage plays {_scrimmagePlays}");

                Debug.Log($"[PoFootball] Down results: {Tally<Systems_DownResult>(_resultCounts)}");
                Debug.Log($"[PoFootball] Play outcomes: {Tally<Systems_PlayOutcome>(_outcomeCounts)}");
            }
        }

        /// <summary>
        /// Applies one play to the game state and returns what it meant. Sets
        /// <paramref name="pointsScored"/> and leaves the game model's line of
        /// scrimmage sitting on wherever the next snap belongs.
        /// </summary>
        private Systems_DownResult Resolve(
            Systems_PlayOutcome outcome,
            float spotY,
            Systems_TeamId offense,
            ref int pointsScored)
        {
            if (outcome == Systems_PlayOutcome.Touchdown)
            {
                pointsScored = Systems_GameRules.TOUCHDOWN_POINTS
                    + Systems_GameRules.EXTRA_POINT_POINTS;
                _game.AddPoints(offense, pointsScored);

                KickOffTo(offense);
                return Systems_DownResult.Touchdown;
            }

            if (outcome == Systems_PlayOutcome.Punt)
            {
                // Net yards from the line of scrimmage, then mirrored into the
                // receiving team's own frame. A punt that reaches the end zone is a
                // touchback at the 20 — which is a different yard line from a
                // kickoff touchback, because they are different rules.
                //
                // The net comes from the kick model rather than the flat constant,
                // so a played game gets a punt that varies and a training run gets
                // the same 40 every time. See Systems_IKickModel.
                float landing = _game.LineOfScrimmageY
                    + (_kickModel.PuntNetYards() * Systems_FieldModel.YARD);

                float receiverSpot =
                    landing >= Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                        ? OwnYardLineToY(Systems_GameRules.PUNT_TOUCHBACK_YARD_LINE)
                        : ClampSeriesStart(Mirror(landing));

                _game.GiveBallTo(offense.Opponent(), receiverSpot);
                return Systems_DownResult.Punt;
            }

            if (outcome == Systems_PlayOutcome.FieldGoalGood)
            {
                pointsScored = Systems_GameRules.FIELD_GOAL_POINTS;
                _game.AddPoints(offense, pointsScored);

                KickOffTo(offense);
                return Systems_DownResult.FieldGoalGood;
            }

            if (outcome == Systems_PlayOutcome.FieldGoalMissed)
            {
                // The defense takes over at the SPOT OF THE KICK, seven yards behind
                // the line of scrimmage, not at the line itself. That is what makes a
                // long attempt a genuine gamble instead of a free roll — miss from 55
                // and the other side starts near midfield.
                float spotOfKick = _game.LineOfScrimmageY
                    - (Systems_GameRules.FIELD_GOAL_SNAP_YARDS * Systems_FieldModel.YARD);

                _game.GiveBallTo(
                    offense.Opponent(), ClampSeriesStart(Mirror(spotOfKick)));
                return Systems_DownResult.FieldGoalMissed;
            }

            if (outcome == Systems_PlayOutcome.FumbleLost)
            {
                // Same shape as an interception: the defense takes over exactly
                // where the ball came loose, mirrored into its own frame.
                _game.GiveBallTo(offense.Opponent(), ClampSeriesStart(Mirror(spotY)));
                return Systems_DownResult.FumbleLost;
            }

            if (outcome == Systems_PlayOutcome.Interception)
            {
                _game.GiveBallTo(offense.Opponent(), ClampSeriesStart(Mirror(spotY)));
                return Systems_DownResult.Interception;
            }

            if (IsSafety(outcome, spotY))
            {
                pointsScored = Systems_GameRules.SAFETY_POINTS;
                _game.AddPoints(offense.Opponent(), pointsScored);

                _game.GiveBallTo(
                    offense.Opponent(),
                    OwnYardLineToY(Systems_GameRules.SAFETY_FREE_KICK_RESULT_YARD_LINE));
                return Systems_DownResult.Safety;
            }

            // An incompletion returns the ball to the previous spot; everything
            // else is spotted where it died.
            float nextSpot = outcome == Systems_PlayOutcome.Incompletion
                ? _game.LineOfScrimmageY
                : spotY;

            nextSpot = ClampSeriesStart(nextSpot);

            bool madeTheLine = nextSpot >= _game.FirstDownMarkerY;

            if (madeTheLine)
            {
                _game.StartSeries(nextSpot);
                return Systems_DownResult.FirstDown;
            }

            if (_game.Down >= Systems_GameRules.DOWNS_PER_SERIES)
            {
                _game.GiveBallTo(offense.Opponent(), ClampSeriesStart(Mirror(nextSpot)));
                return Systems_DownResult.TurnoverOnDowns;
            }

            float yardsToGo = (_game.FirstDownMarkerY - nextSpot) / Systems_FieldModel.YARD;
            _game.AdvanceDown(nextSpot, Mathf.Max(yardsToGo, 0.1f));
            return Systems_DownResult.NextDown;
        }

        /// <summary>
        /// A carrier ruled down on or behind its own goal line is a safety. The
        /// physics layer has no concept of one — Systems_FieldModel treats leaving
        /// the back of the end zone as out of bounds — so it is recognised here, at
        /// the rules layer, purely from where the ball stopped. No dynamics change.
        /// </summary>
        private static bool IsSafety(Systems_PlayOutcome outcome, float spotY)
        {
            if (outcome == Systems_PlayOutcome.Incompletion)
            {
                return false;
            }

            return spotY <= Systems_FieldModel.OWN_GOAL_LINE_Y;
        }

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
        private int _fieldGoalsAttempted;
        private int _fieldGoalsMade;
        private int _safeties;
        private int _turnoversOnDowns;
        private int _fumblesLost;

        /// <summary>
        /// Tallies the rare results, purely so the final log line can prove a game
        /// actually exercised the rules rather than running forty handoffs.
        /// </summary>
        private void CountKick(Systems_DownResult result)
        {
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
                case Systems_DownResult.FumbleLost:
                    _fumblesLost++;
                    break;
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

        private static bool StopsClock(Systems_PlayOutcome outcome)
        {
            switch (outcome)
            {
                case Systems_PlayOutcome.Incompletion:
                case Systems_PlayOutcome.OutOfBounds:
                case Systems_PlayOutcome.Touchdown:
                case Systems_PlayOutcome.Interception:
                case Systems_PlayOutcome.FumbleLost:
                // Every kick changes possession, and the clock stops on a change of
                // possession. A safety stops it too — the free kick that follows is
                // a fresh start, not a continuation of the drive.
                case Systems_PlayOutcome.Safety:
                case Systems_PlayOutcome.Punt:
                case Systems_PlayOutcome.FieldGoalGood:
                case Systems_PlayOutcome.FieldGoalMissed:
                    return true;
                default:
                    return false;
            }
        }

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
                || resultSoFar == Systems_DownResult.FieldGoalGood;

            // Overtime expiring ends the game however it stands. The NFL regular
            // season allows a tie after one overtime period and so does this.
            if (_game.Phase == Systems_GamePhase.Overtime)
            {
                return Systems_DownResult.EndOfGame;
            }

            if (_game.Quarter >= Systems_GameRules.QUARTER_COUNT)
            {
                // LEVEL AFTER FOUR QUARTERS IS NOT THE END ANY MORE. A measured game
                // finished 28-28 and simply stopped, because this returned EndOfGame
                // regardless of the score. Sudden death now settles it — see
                // Systems_GameRules.OVERTIME_SECONDS for why sudden death rather
                // than the real possession-owed rule.
                if (_game.HomeScore == _game.AwayScore)
                {
                    _game.BeginOvertime();

                    // The team that did not receive the opening kickoff receives
                    // again, standing in for the overtime coin toss.
                    _game.GiveBallTo(
                        _game.OpeningPossession.Opponent(),
                        OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));

                    return Systems_DownResult.EndOfQuarter;
                }

                return Systems_DownResult.EndOfGame;
            }

            int nextQuarter = _game.Quarter + 1;
            _game.BeginQuarter(nextQuarter);

            // Halftime resets field position and gives the ball to whoever did not
            // get it to open the game. Between the first and second, and the third
            // and fourth, only the period changes — the drive carries on.
            if (nextQuarter == 3)
            {
                _game.SetPhase(Systems_GamePhase.Halftime);

                _game.GiveBallTo(
                    _game.OpeningPossession.Opponent(),
                    OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
            }

            return announceScore ? resultSoFar : Systems_DownResult.EndOfQuarter;
        }

        /// <summary>
        /// The kickoff that follows a score, resolved at the rules layer the same
        /// way a punt and a field goal already are — it is not a snap anybody plays.
        ///
        /// <paramref name="scorer"/> is the team that just scored, so it is the team
        /// KICKING. Normally the ball goes to its opponent wherever the return ended.
        ///
        /// THE ONSIDE KICK IS THE POINT OF THIS METHOD. Before it, every score handed
        /// the conceding team the ball on its own 35 and a team two scores down with
        /// a minute left had no way whatsoever to get the ball back — the single
        /// largest strategic hole in the simulation. A side that is trailing badly
        /// and nearly out of time now kicks short: recover it and the drive
        /// continues, miss and the opponent starts near midfield, which is what
        /// makes it a desperation call rather than a free roll.
        /// </summary>
        private void KickOffTo(Systems_TeamId scorer)
        {
            Systems_TeamId receiver = scorer.Opponent();

            if (ShouldTryOnside(scorer))
            {
                if (_kickModel.IsOnsideRecovered())
                {
                    // The kicking team keeps it, at the spot a short kick travels to.
                    _game.GiveBallTo(
                        scorer, OwnYardLineToY(Systems_GameRules.ONSIDE_FAILED_YARD_LINE));
                    return;
                }

                _game.GiveBallTo(
                    receiver, OwnYardLineToY(Systems_GameRules.ONSIDE_FAILED_YARD_LINE));
                return;
            }

            _game.GiveBallTo(receiver, OwnYardLineToY(_kickModel.KickoffReturnYardLine()));
        }

        /// <summary>
        /// Whether the team that just scored should kick short: it is still behind by
        /// more than one score, and there is not enough time left to get the ball
        /// back any other way.
        /// </summary>
        private bool ShouldTryOnside(Systems_TeamId scorer)
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

            int deficit = _game.ScoreOf(scorer.Opponent()) - _game.ScoreOf(scorer);

            return deficit > Systems_GameRules.ONSIDE_TRAILING_BY;
        }

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
        /// would put half the formation behind the back line, where the referee
        /// would whistle it dead before anybody moved.
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
}
