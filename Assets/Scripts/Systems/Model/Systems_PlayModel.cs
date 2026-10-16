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
        /// Physics ticks a play may run before it is declared TimeExpired — 600
        /// ticks = 12 s at the pinned 50 Hz.
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
        ///
        /// IT IS NO LONGER THE THING THAT ENDS ORDINARY DOWNS. Systems_Referee now
        /// whistles a play dead when the CARRIER STOPS — under STALL_SPEED for
        /// STALL_TICKS — instead of when a timer runs out, so a runner nobody has
        /// tackled keeps the down alive for as long as he keeps running. That was
        /// reported from a device: a back breaking into open field with no defender
        /// near him, and the whistle going anyway. What remains here is a ceiling for
        /// the TRAINER rather than for football, because an episode that never
        /// terminates hangs a rollout.
        ///
        /// IT STAYS AT 600, AND RAISING IT WAS TRIED AND REVERTED. 1200 looked free —
        /// the stall rule had taken over the job, so why not let an exceptional play
        /// run — and it broke two invariants that are load-bearing and that no amount
        /// of watching the game would have surfaced:
        ///
        ///   Systems_ContractTests.TrainerConfig_HorizonCoversAWholePlay. time_horizon
        ///   has to span a whole play; at DecisionPeriod 5 a 1200-tick cap demands 240
        ///   and every config in Config/ says 120. Doubling this silently un-pairs the
        ///   cap from every trainer config in the repository.
        ///
        ///   Reward_Tests.RoleShapedTerms_CannotOutweighTheTerminalRewardOverAWholePlay.
        ///   Role shaping is paid per tick and the terminal reward once, so the cap is
        ///   what bounds their ratio. Over 1200 ticks the shaped terms reach 0.30
        ///   against a 0.25 ceiling — shaping would outweigh the outcome, which is the
        ///   precise failure that test exists to prevent.
        ///
        /// Twelve seconds of CONTINUOUS running is an exceptional play, and the cost
        /// of clipping it is one rare down. The cost of raising it is a reward signal
        /// that no longer prefers scoring, in a project whose whole purpose is to
        /// learn football. Anyone raising it must move time_horizon in every config
        /// and re-balance Reward_Role in the same commit.
        /// </summary>
        public const int MAX_PHYSICS_TICKS = 600;

        /// <summary>
        /// Agent decisions per play: 124 at DECISION_PERIOD 5 — the play's own 120
        /// and the four taken while the teams stand set before the snap (revision
        /// 14, Systems_SimConstants.PRESNAP_TICKS). Derived rather than typed, so a
        /// change to either moves it and Systems_ContractTests then flags every
        /// config whose time_horizon no longer spans a play.
        /// </summary>
        public const int MAX_DECISIONS =
            (MAX_PHYSICS_TICKS + Systems_SimConstants.PRESNAP_TICKS) / Systems_SimConstants.DECISION_PERIOD;

        /// <summary>Most flags one play can carry. Real plays almost never carry two.</summary>
        public const int MAX_FOULS = 4;

        public Systems_PlayPhase Phase { get; private set; } = Systems_PlayPhase.PreSnap;

        public Systems_PlayOutcome Outcome { get; private set; } = Systems_PlayOutcome.None;

        /// <summary>
        /// What sort of snap this is. Starts as whatever the spot provider said and
        /// becomes Punt or FieldGoal if the quarterback calls a kick pre-snap.
        /// </summary>
        public Systems_PlayKind Kind { get; private set; } = Systems_PlayKind.Scrimmage;

        /// <summary>On a kickoff: an onside kick rather than a deep one.</summary>
        public bool IsOnsideKick { get; private set; }

        /// <summary>Ticks the teams have stood set. Advances only while Phase is PreSnap.</summary>
        public int PreSnapTick { get; private set; }

        /// <summary>
        /// Set when a pre-snap kick call means the offense has to re-form in its kick
        /// formation. The director consumes it.
        /// </summary>
        public bool KickFormationPending { get; private set; }

        /// <summary>
        /// The side with the ball right now — or, while it is loose or in the air
        /// off a kick, the side that last had it. Offense at the snap.
        /// </summary>
        public Systems_TeamSide PossessingSide { get; private set; } = Systems_TeamSide.Offense;

        /// <summary>True once the defense of the snap has had the ball in its hands.</summary>
        public bool PossessionChanged { get; private set; }

        /// <summary>Y where the defense first got its hands on the ball — where a return starts.</summary>
        public float PossessionChangeY { get; private set; }

        /// <summary>Tick the ball changed sides. A defensive foul before it lets the offense keep the ball.</summary>
        public int PossessionChangeTick { get; private set; }

        /// <summary>Whose points a Touchdown or Safety outcome are. Meaningful only for those two.</summary>
        public Systems_TeamSide ScoringSide { get; private set; } = Systems_TeamSide.Offense;

        /// <summary>Y the kick left the foot from. Meaningful once <see cref="WasKicked"/>.</summary>
        public float KickSpotY { get; private set; }

        public bool WasKicked { get; private set; }

        /// <summary>
        /// Whether the receiving team has touched the kick. A punt the receivers
        /// never touched and the kicking team downs is the receivers' ball; one they
        /// muffed and the kicking team falls on is the kicking team's.
        /// </summary>
        public bool KickTouchedByReceivers { get; private set; }

        /// <summary>Tick a forward pass left the hand, or -1. Roughing the passer is timed from it.</summary>
        public int ForwardPassTick { get; private set; } = -1;

        /// <summary>Whether the play's forward pass has gone; there is only ever one.</summary>
        public bool ForwardPassThrown => ForwardPassTick >= 0;

        private readonly Systems_Foul[] _fouls = new Systems_Foul[MAX_FOULS];

        /// <summary>Flags thrown on this play so far.</summary>
        public int FoulCount { get; private set; }

        /// <summary>Y of the line the offense needs to reach for a new series.</summary>
        public float LineToGainY =>
            Mathf.Min(
                LineOfScrimmageY + (YardsToGo * Systems_FieldModel.YARD),
                Systems_FieldModel.ATTACKING_GOAL_LINE_Y);

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
            BeginEpisode(
                lineOfScrimmageY, ballY, down, yardsToGo, Systems_PlayKind.Scrimmage, false);
        }

        /// <summary>Called by Systems_EpisodeDirector before the snap.</summary>
        public void BeginEpisode(
            float lineOfScrimmageY, float ballY, int down, float yardsToGo,
            Systems_PlayKind kind, bool onsideKick)
        {
            Phase = Systems_PlayPhase.PreSnap;
            Outcome = Systems_PlayOutcome.None;
            Call = Systems_PlayCall.None;
            PassCompleted = false;
            PhysicsTick = 0;
            PreSnapTick = 0;
            Down = Mathf.Clamp(down, 1, Systems_GameRules.DOWNS_PER_SERIES);
            YardsToGo = Mathf.Max(yardsToGo, 0.1f);
            LineOfScrimmageY = lineOfScrimmageY;
            BallY = ballY;
            DeadBallSpot = new Vector2(0f, ballY);

            Kind = kind;
            IsOnsideKick = kind == Systems_PlayKind.Kickoff && onsideKick;
            KickFormationPending = false;
            PossessingSide = Systems_TeamSide.Offense;
            PossessionChanged = false;
            PossessionChangeY = ballY;
            PossessionChangeTick = 0;
            ReturnOutcome = Systems_PlayOutcome.FumbleLost;
            ScoringSide = Systems_TeamSide.Offense;
            KickSpotY = lineOfScrimmageY;
            WasKicked = false;
            KickBallLive = false;
            KickWasBlocked = false;
            KickTouchedByReceivers = false;
            ForwardPassTick = -1;
            FoulCount = 0;

            // The calls that are not the quarterback's to make. A try by kick is a
            // field goal by another name; the clock plays are the coach's.
            switch (kind)
            {
                case Systems_PlayKind.ExtraPoint:
                    Call = Systems_PlayCall.FieldGoal;
                    break;

                case Systems_PlayKind.KneelDown:
                    Call = Systems_PlayCall.KeepQuarterback;
                    break;

                case Systems_PlayKind.Spike:
                    Call = Systems_PlayCall.Pass;
                    break;
            }
        }

        public void Snap()
        {
            Phase = Systems_PlayPhase.Live;
        }

        /// <summary>One tick of standing set. Called by Systems_Referee.</summary>
        public void AdvancePreSnap()
        {
            PreSnapTick++;
        }

        /// <summary>
        /// Whether a kick may still be called: before the snap, early enough for the
        /// kick team to line up, on fourth down of an ordinary snap.
        /// </summary>
        public bool CanCallKick =>
            Phase == Systems_PlayPhase.PreSnap
            && Kind == Systems_PlayKind.Scrimmage
            && Call == Systems_PlayCall.None
            && Down >= Systems_GameRules.DOWNS_PER_SERIES
            && PreSnapTick < Systems_SimConstants.KICK_CALL_WINDOW_TICKS;

        /// <summary>
        /// Records the quarterback's commitment. Ignores every call after the
        /// first, so the QB cannot change its mind mid-play.
        ///
        /// SINCE REVISION 14 THERE ARE TWO WINDOWS. A kick is called BEFORE the
        /// snap, because a punt is a formation and not a decision a quarterback can
        /// make with the rush already on him; anything else is called after
        /// DROPBACK_TICKS, as it always was. Each window refuses the other's calls.
        /// </summary>
        public void LatchCall(Systems_PlayCall call)
        {
            if (Call != Systems_PlayCall.None || call == Systems_PlayCall.None)
            {
                return;
            }

            bool isKick = call == Systems_PlayCall.Punt || call == Systems_PlayCall.FieldGoal;

            if (Phase == Systems_PlayPhase.PreSnap)
            {
                if (!isKick || !CanCallKick)
                {
                    return;
                }

                Call = call;
                Kind = call == Systems_PlayCall.Punt
                    ? Systems_PlayKind.Punt
                    : Systems_PlayKind.FieldGoal;
                KickFormationPending = true;
                return;
            }

            if (isKick || Kind == Systems_PlayKind.Kickoff)
            {
                return;
            }

            Call = call;
        }

        /// <summary>The director has re-formed the teams for the kick.</summary>
        public void ClearKickFormationRequest()
        {
            KickFormationPending = false;
        }

        /// <summary>
        /// What the play is reported as if the defense of the snap is holding the
        /// ball when it ends short of a score — how it got it. Interception,
        /// FumbleLost, Punt, Kickoff or KickBlocked.
        /// </summary>
        public Systems_PlayOutcome ReturnOutcome { get; private set; } = Systems_PlayOutcome.FumbleLost;

        /// <summary>
        /// The ball is in a new side's hands. The first time the defense of the snap
        /// gets it is where its return is measured from, and
        /// <paramref name="howTheDefenseGotIt"/> is what the play is called if the
        /// return ends without a score.
        /// </summary>
        public void SetPossession(
            Systems_TeamSide side, float y, Systems_PlayOutcome howTheDefenseGotIt)
        {
            if (side == Systems_TeamSide.Defense && !PossessionChanged)
            {
                PossessionChanged = true;
                PossessionChangeY = y;
                PossessionChangeTick = PhysicsTick;
                ReturnOutcome = howTheDefenseGotIt;
            }

            PossessingSide = side;
        }

        /// <summary>Yards the defense of the snap has returned the ball since it got it.</summary>
        public float ReturnYards =>
            PossessionChanged ? (PossessionChangeY - BallY) / Systems_FieldModel.YARD : 0f;

        /// <summary>
        /// Whether the ball is still a KICK — in the air off the foot or loose
        /// without anybody having possessed it since. The kicking-team rules (a punt
        /// it touches is downed, a kickoff it recovers past ten yards is its own)
        /// apply only while this holds; once somebody has it, a loose ball is a
        /// fumble like any other.
        /// </summary>
        public bool KickBallLive { get; private set; }

        /// <summary>A rusher got a hand on the kick as it left the foot.</summary>
        public bool KickWasBlocked { get; private set; }

        /// <summary>The kick has left the foot.</summary>
        public void MarkKicked(float y)
        {
            WasKicked = true;
            KickBallLive = true;
            KickSpotY = y;
        }

        public void MarkKickBlocked()
        {
            KickWasBlocked = true;
        }

        /// <summary>Somebody has possessed the kick; it is an ordinary ball from here.</summary>
        public void EndKickBall()
        {
            KickBallLive = false;
        }

        public void MarkKickTouchedByReceivers()
        {
            KickTouchedByReceivers = true;
        }

        public void MarkForwardPass()
        {
            if (ForwardPassTick < 0)
            {
                ForwardPassTick = PhysicsTick;
            }
        }

        /// <summary>A flag. Ignored past MAX_FOULS, which a real play never reaches.</summary>
        public void RecordFoul(Systems_Foul foul)
        {
            if (FoulCount >= MAX_FOULS || foul.IsNone)
            {
                return;
            }

            _fouls[FoulCount++] = foul;
        }

        public Systems_Foul GetFoul(int index)
        {
            return index >= 0 && index < FoulCount ? _fouls[index] : default;
        }

        /// <summary>Whether this side has already been flagged for this foul on this play.</summary>
        public bool HasFoul(Systems_TeamSide side, Systems_FoulType type)
        {
            for (int index = 0; index < FoulCount; index++)
            {
                if (_fouls[index].Side == side && _fouls[index].Type == type)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Flags this side has drawn on this play.</summary>
        public int FoulsBy(Systems_TeamSide side)
        {
            int count = 0;

            for (int index = 0; index < FoulCount; index++)
            {
                if (_fouls[index].Side == side)
                {
                    count++;
                }
            }

            return count;
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
            EndPlay(outcome, spot, PossessingSide);
        }

        /// <summary>
        /// The whistle. <paramref name="scoringSide"/> is read only for a Touchdown
        /// or a Safety.
        /// </summary>
        public void EndPlay(
            Systems_PlayOutcome outcome, Vector2 spot, Systems_TeamSide scoringSide)
        {
            Phase = Systems_PlayPhase.Dead;
            Outcome = outcome;
            DeadBallSpot = spot;
            ScoringSide = scoringSide;
            EpisodeIndex++;
        }
    }
}
