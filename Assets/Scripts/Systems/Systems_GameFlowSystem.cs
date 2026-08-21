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
            ISubscriber<Systems_PlayEndedMessage> endedSubscriber,
            IPublisher<Systems_DownResolvedMessage> resolvedPublisher,
            IPublisher<Systems_GameOverMessage> gameOverPublisher)
        {
            _game = game;
            _play = play;
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
            if (_game.Phase != Systems_GamePhase.Playing
                || _play.Phase != Systems_PlayPhase.Live)
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

            if (result == Systems_DownResult.EndOfGame)
            {
                _game.SetPhase(Systems_GamePhase.Final);
                _gameOverPublisher.Publish(
                    new Systems_GameOverMessage(_game.HomeScore, _game.AwayScore));
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

                // Conceding team takes over at its own 25 — which, in its own
                // attacking frame, needs no mirroring: "own 25" is the same number
                // whoever says it.
                _game.GiveBallTo(
                    offense.Opponent(),
                    OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
                return Systems_DownResult.Touchdown;
            }

            if (outcome == Systems_PlayOutcome.Punt)
            {
                // Net yards from the line of scrimmage, then mirrored into the
                // receiving team's own frame. A punt that reaches the end zone is a
                // touchback at the 20 — which is a different yard line from a
                // kickoff touchback, because they are different rules.
                float landing = _game.LineOfScrimmageY
                    + (Systems_GameRules.PUNT_NET_YARDS * Systems_FieldModel.YARD);

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

                // A score is followed by a kickoff, so the conceding team takes over
                // at the kickoff touchback spot — the 35, not the punt's 20.
                _game.GiveBallTo(
                    offense.Opponent(),
                    OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
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

        private static bool StopsClock(Systems_PlayOutcome outcome)
        {
            switch (outcome)
            {
                case Systems_PlayOutcome.Incompletion:
                case Systems_PlayOutcome.OutOfBounds:
                case Systems_PlayOutcome.Touchdown:
                case Systems_PlayOutcome.Interception:
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
                || resultSoFar == Systems_DownResult.Safety;

            if (_game.Quarter >= Systems_GameRules.QUARTER_COUNT)
            {
                return Systems_DownResult.EndOfGame;
            }

            int nextQuarter = _game.Quarter + 1;
            _game.BeginQuarter(nextQuarter);

            // Halftime resets field position and gives the ball to whoever did not
            // get it to open the game. Between the first and second, and the third
            // and fourth, only the period changes — the drive carries on.
            if (nextQuarter == 3)
            {
                _game.GiveBallTo(
                    _game.OpeningPossession.Opponent(),
                    OwnYardLineToY(Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE));
            }

            return announceScore ? resultSoFar : Systems_DownResult.EndOfQuarter;
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
