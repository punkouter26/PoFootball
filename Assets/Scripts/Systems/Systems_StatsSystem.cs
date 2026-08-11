using System;
using MessagePipe;
using PoFootball.Models;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Keeps the box score. Subscribes to the game consequence
    /// (<see cref="Systems_DownResolvedMessage"/>) rather than to the raw whistle,
    /// so a play is only counted once it has a meaning — and so the yardage it
    /// records is the same number the scoreboard used to move the chains.
    ///
    /// TEAM TOTALS ONLY, which is why there is no longer a tackle subscription and
    /// no attribution logic. Crediting an individual meant reading
    /// Systems_BallModel.CarrierId at the whistle to work out who actually finished
    /// with the ball; correct, but it produced statistics about anonymous formation
    /// slots. See Systems_BoxScore.
    /// </summary>
    public sealed class Systems_StatsSystem : IStartable, IDisposable
    {
        private readonly Systems_BoxScore _boxScore;
        private readonly Systems_GameModel _game;
        private readonly Systems_PlayModel _play;
        private readonly ISubscriber<Systems_DownResolvedMessage> _resolvedSubscriber;

        private IDisposable _resolvedSubscription;

        /// <summary>
        /// Drives already credited, so a drive is counted once when it starts
        /// rather than once per play. Systems_GameModel.DriveIndex only ever
        /// increases, so comparing against the last one seen is enough.
        /// </summary>
        private int _lastCountedDriveIndex = -1;

        public Systems_StatsSystem(
            Systems_BoxScore boxScore,
            Systems_GameModel game,
            Systems_PlayModel play,
            ISubscriber<Systems_DownResolvedMessage> resolvedSubscriber)
        {
            _boxScore = boxScore;
            _game = game;
            _play = play;
            _resolvedSubscriber = resolvedSubscriber;
        }

        public void Start()
        {
            _resolvedSubscription = _resolvedSubscriber.Subscribe(OnDownResolved);
        }

        private void OnDownResolved(Systems_DownResolvedMessage message)
        {
            Systems_TeamStatLine offense = _boxScore.Team(message.Offense);
            Systems_TeamStatLine defense = _boxScore.Team(message.Offense.Opponent());

            bool isPass = message.Call == Systems_PlayCall.Pass;
            bool completed = _play.PassCompleted;
            float yards = message.YardsGained;

            offense.AddPlay(yards, isPass, completed);
            offense.AddTimeOfPossession(message.ClockSecondsBurned);

            // Scores are read off the game model rather than accumulated from
            // PointsScored, so the box score can never drift from the scoreboard.
            offense.SetPoints(_game.ScoreOf(message.Offense));
            defense.SetPoints(_game.ScoreOf(message.Offense.Opponent()));

            if (_game.DriveIndex != _lastCountedDriveIndex)
            {
                _lastCountedDriveIndex = _game.DriveIndex;
                _boxScore.Team(_game.Possession).AddDrive();
            }

            RecordResult(message, offense, defense);
        }

        private void RecordResult(
            Systems_DownResolvedMessage message,
            Systems_TeamStatLine offense,
            Systems_TeamStatLine defense)
        {
            switch (message.Result)
            {
                case Systems_DownResult.FirstDown:
                    offense.AddFirstDown();
                    break;

                case Systems_DownResult.Touchdown:
                    offense.AddTouchdown();
                    break;

                case Systems_DownResult.Safety:
                    // The two points go to the defense, and so does the safety.
                    defense.AddSafety();
                    break;

                case Systems_DownResult.Interception:
                case Systems_DownResult.TurnoverOnDowns:
                    offense.AddTurnover();
                    break;
            }
        }

        public void Dispose()
        {
            _resolvedSubscription?.Dispose();
        }
    }
}
