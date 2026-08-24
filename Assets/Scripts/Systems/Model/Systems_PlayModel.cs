using UnityEngine;

namespace PoFootball.Models
{
    /// <summary>
    /// Mutable state of the play currently being simulated. Exactly one instance
    /// exists per environment; Systems_Referee and Systems_EpisodeDirector are the
    /// only writers, everything else reads.
    ///
    /// Deliberately plain fields rather than ReactiveProperty (see
    /// .claude/rules/architecture.md): this state is read by 22 agents every
    /// physics tick at time_scale 20, and per-change subscription callbacks would
    /// allocate, breaking acceptance criterion #17. Play-lifecycle transitions are
    /// broadcast through MessagePipe instead, which fires a handful of times per
    /// episode rather than thousands.
    /// </summary>
    public sealed class Systems_PlayModel
    {
        /// <summary>
        /// Physics ticks a play may run before it is declared TimeExpired — 330
        /// ticks = 6.6 s at the pinned 50 Hz.
        ///
        /// CUT FROM 750 (15 s), BUT NOT AS FAR AS 330. A fifteen-second down is not
        /// football — real snaps live four to seven seconds — and the cap was doing
        /// double duty as a safety net for a carrier nobody could catch, which is a
        /// tackling problem and is fixed where tackling lives
        /// (Systems_Referee.ReportSustainedContact).
        ///
        /// 330 (6.6 s) WAS MEASURED AND WAS TOO TIGHT: it did not trim outliers, it
        /// truncated ordinary downs. A full game ended 34 of its 80 scrimmage plays
        /// on the cap — nearly half the game reported as TimeExpired, an outcome
        /// that means nothing in football and which the reward layer prices as a
        /// half-tackle. Plays in this simulation genuinely run longer than a real
        /// one's because the bodies accelerate slowly and the carrier jukes.
        ///
        /// 500 (10 s) WAS ALSO MEASURED AND STILL CLIPPED 28 OF 74 SCRIMMAGE PLAYS.
        /// Plays here genuinely run long: the bodies accelerate slowly, the carrier
        /// jukes, and a quarterback who does not find a receiver holds the ball. The
        /// cap is a backstop, not a balance lever — shortening it does not make
        /// plays end sooner, it relabels unfinished ones as TimeExpired, an outcome
        /// that means nothing in football.
        ///
        /// 600 ticks is 12 s: a real trim from 750 that still lets an honest play
        /// finish. Getting plays to actually END is tackling's job.
        /// </summary>
        public const int MAX_PHYSICS_TICKS = 600;

        /// <summary>Agent decisions per play at DecisionPeriod = 5 (750 / 5).</summary>
        public const int MAX_DECISIONS = MAX_PHYSICS_TICKS / 5;

        public Systems_PlayPhase Phase { get; private set; } = Systems_PlayPhase.PreSnap;

        public Systems_PlayOutcome Outcome { get; private set; } = Systems_PlayOutcome.None;

        /// <summary>
        /// What the quarterback committed to. Latched once on its first decision
        /// after the snap and immutable thereafter.
        /// </summary>
        public Systems_PlayCall Call { get; private set; } = Systems_PlayCall.None;

        /// <summary>Physics ticks elapsed since the snap.</summary>
        public int PhysicsTick { get; private set; }

        /// <summary>Episodes completed since the environment started. Diagnostic only.</summary>
        public int EpisodeIndex { get; private set; }

        /// <summary>Y of the line of scrimmage for this play.</summary>
        public float LineOfScrimmageY { get; private set; }

        /// <summary>Where the ball was ruled dead. Only meaningful once Phase is Dead.</summary>
        public Vector2 DeadBallSpot { get; private set; }

        /// <summary>Y of the ball, held or airborne. Mirrored from Systems_BallModel each tick.</summary>
        public float BallY { get; private set; }

        /// <summary>Net yards relative to the snap spot. Negative is a loss.</summary>
        public float NetYards => (BallY - LineOfScrimmageY) / Systems_FieldModel.YARD;

        public bool CallIsLatched => Call != Systems_PlayCall.None;

        /// <summary>True once a receiver has caught a pass on this play.</summary>
        public bool PassCompleted { get; private set; }

        /// <summary>
        /// Which down this snap is, one through Systems_GameRules.DOWNS_PER_SERIES.
        ///
        /// Lives here rather than only on Systems_GameModel because the game model
        /// does not exist in training — Systems_GameFlowSystem is a Game-mode
        /// registration — and the quarterback has to be able to see the down in both
        /// modes to learn when a punt or a field goal is the right call.
        /// </summary>
        public int Down { get; private set; } = 1;

        /// <summary>Yards needed for a new series from this snap.</summary>
        public float YardsToGo { get; private set; } = Systems_GameRules.YARDS_TO_GAIN;

        public void MarkPassCompleted()
        {
            PassCompleted = true;
        }

        /// <summary>Called by Systems_EpisodeDirector before the snap.</summary>
        public void BeginEpisode(
            float lineOfScrimmageY, float ballY, int down, float yardsToGo)
        {
            Phase = Systems_PlayPhase.PreSnap;
            Outcome = Systems_PlayOutcome.None;
            Call = Systems_PlayCall.None;
            PassCompleted = false;
            PhysicsTick = 0;
            Down = Mathf.Clamp(down, 1, Systems_GameRules.DOWNS_PER_SERIES);
            YardsToGo = Mathf.Max(yardsToGo, 0.1f);
            LineOfScrimmageY = lineOfScrimmageY;
            BallY = ballY;
            DeadBallSpot = new Vector2(0f, ballY);
        }

        public void Snap()
        {
            Phase = Systems_PlayPhase.Live;
        }

        /// <summary>
        /// Records the quarterback's commitment. Ignores every call after the
        /// first, so the QB cannot change its mind mid-play.
        /// </summary>
        public void LatchCall(Systems_PlayCall call)
        {
            if (Call != Systems_PlayCall.None || call == Systems_PlayCall.None)
            {
                return;
            }

            Call = call;
        }

        /// <summary>Advanced once per FixedUpdate by Systems_Referee while the play is live.</summary>
        public void AdvanceTick()
        {
            PhysicsTick++;
        }

        public void SetBallY(float ballY)
        {
            BallY = ballY;
        }

        public void EndPlay(Systems_PlayOutcome outcome, Vector2 spot)
        {
            Phase = Systems_PlayPhase.Dead;
            Outcome = outcome;
            DeadBallSpot = spot;
            EpisodeIndex++;
        }
    }
}
