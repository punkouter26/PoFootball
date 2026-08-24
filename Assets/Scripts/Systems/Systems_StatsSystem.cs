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
        /// <summary>
        /// Drives already credited. Seeded to a value DriveIndex can never take so
        /// the opening drive is counted like any other — at -1 against an opening
        /// DriveIndex of 0 this worked by luck, and lost the opening drive entirely
        /// whenever the very first play was a turnover (DriveIndex reached 1 before
        /// anything had been counted, and drive 0 was skipped).
        /// </summary>
        private int _lastCountedDriveIndex = int.MinValue;

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

            // A KICK IS NOT A RUSHING ATTEMPT. AddPlay splits on isPass alone, so a
            // punt or a field goal — neither of which is a pass — was booked as a
            // carry for zero yards, padding the rushing line and dragging yards per
            // carry down. Systems_GameFlowSystem's REALISM line has always excluded
            // kicks from its average; the box score the viewer actually reads did
            // not, and with three punts in a measured game that is visible.
            if (IsKick(message.Call))
            {
                offense.AddTimeOfPossession(message.ClockSecondsBurned);
                offense.SetPoints(_game.ScoreOf(message.Offense));
                defense.SetPoints(_game.ScoreOf(message.Offense.Opponent()));
                CountDrive(message);
                RecordResult(message, offense, defense);
                return;
            }

            offense.AddPlay(yards, isPass, completed);
            offense.AddTimeOfPossession(message.ClockSecondsBurned);

            // Scores are read off the game model rather than accumulated from
            // PointsScored, so the box score can never drift from the scoreboard.
            offense.SetPoints(_game.ScoreOf(message.Offense));
            defense.SetPoints(_game.ScoreOf(message.Offense.Opponent()));

            CountDrive(message);

            RecordResult(message, offense, defense);
        }

        private static bool IsKick(Systems_PlayCall call)
        {
            return call == Systems_PlayCall.Punt || call == Systems_PlayCall.FieldGoal;
        }

        /// <summary>
        /// Credits the drive THIS play belonged to, to the team that ran it.
        ///
        /// The old form read Systems_GameModel.DriveIndex and Possession directly,
        /// which are both post-Resolve: on a play that changed hands they already
        /// describe the NEXT drive, so the opening drive was skipped whenever the
        /// game's first play was a turnover. Deriving the drive from the play makes
        /// it order-independent — every play credits its own drive exactly once,
        /// including a team that keeps the ball through an onside recovery.
        /// </summary>
        private void CountDrive(Systems_DownResolvedMessage message)
        {
            bool possessionChanged = message.Offense != _game.Possession;

            int driveOfThisPlay = possessionChanged
                ? _game.DriveIndex - 1
                : _game.DriveIndex;

            if (driveOfThisPlay == _lastCountedDriveIndex)
            {
                return;
            }

            _lastCountedDriveIndex = driveOfThisPlay;
            _boxScore.Team(message.Offense).AddDrive();
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
                case Systems_DownResult.FumbleLost:
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
