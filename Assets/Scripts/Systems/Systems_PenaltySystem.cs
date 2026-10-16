using PoFootball.Models;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// The officials. Watches what the bodies do and throws a flag when they break a
    /// rule — and, like a real crew, does not see every one of them.
    ///
    /// WHY THIS EXISTS (revision 14). Through revision 13 there was no foul in the
    /// game at all. A defender could run through a receiver before the ball got
    /// there, a beaten guard could hang on to his man for as long as he liked, a
    /// late hit on a quarterback cost nothing. Every one of those is now something a
    /// policy can be caught doing.
    ///
    /// DETECTED FROM THE BODIES, CALLED WITH A CHANCE. Each foul is a geometric test
    /// on what actually happened — who was in contact with whom, for how long, from
    /// which side, with the ball where — and an official who could see it flags it
    /// with the probability in Systems_SimConstants. Each engagement is judged once,
    /// so a long hold is one chance at a flag rather than one per tick.
    ///
    /// IT ONLY RECORDS. The flag goes on Systems_PlayModel's foul list and the play
    /// goes on; what it costs is somebody else's business — the reward in training
    /// (Reward_Terminal), the yardage in a game (Systems_GameFlowSystem). The three
    /// pre-snap fouls are the exception: <see cref="CheckPreSnap"/> returns one and
    /// the referee kills the play before it starts.
    ///
    /// The same in both modes, and on its own seeded stream: an official's eye is a
    /// rule of the game, not a decision any policy makes, and a brain fitted where
    /// holding was never called would meet different football in a played game.
    /// </summary>
    public sealed class Systems_PenaltySystem
    {
        /// <summary>Arbitrary odd constant; keeps this stream independent of the others.</summary>
        private const uint STREAM_OFFSET = 0x165667B1u;

        private const int SIDE = Systems_FormationBook.SLOTS_PER_SIDE;

        private const int PAIRS = SIDE * SIDE;

        private readonly Systems_PlayModel _play;
        private readonly Systems_BallModel _ball;
        private readonly Systems_PlayerRegistry _registry;

        private Random _rng;

        // Per offense-defense pair, indexed offense * SIDE + defense. Fixed arrays,
        // written from collision callbacks several times a tick: no allocation.
        private readonly int[] _contactTicks = new int[PAIRS];
        private readonly int[] _lastContactTick = new int[PAIRS];
        private readonly bool[] _holdingJudged = new bool[PAIRS];
        private readonly bool[] _contactJudged = new bool[PAIRS];

        public Systems_PenaltySystem(
            Systems_PlayModel play, Systems_BallModel ball, Systems_PlayerRegistry registry)
        {
            _play = play;
            _ball = ball;
            _registry = registry;

            uint seed = Systems_EpisodeSeed.Value ^ STREAM_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        /// <summary>Called by Systems_EpisodeDirector as each play is formed.</summary>
        public void BeginPlay()
        {
            for (int pair = 0; pair < PAIRS; pair++)
            {
                _contactTicks[pair] = 0;
                _lastContactTick[pair] = -100;
                _holdingJudged[pair] = false;
                _contactJudged[pair] = false;
            }
        }

        // =====================================================================
        // Before the snap
        // =====================================================================

        /// <summary>
        /// Whether anybody has jumped. An offensive player moving after he was set
        /// is a false start; a defender whose body is in the neutral zone is
        /// offside. The bodies are frozen while the teams are set, so this only ever
        /// fires on a flinch Systems_IDisciplineModel drew — but it is the movement
        /// that is refereed, not the draw.
        /// </summary>
        public Systems_Foul CheckPreSnap()
        {
            if (_play.Kind == Systems_PlayKind.Kickoff)
            {
                return default;
            }

            float falseStartSquared =
                Systems_SimConstants.FALSE_START_SPEED * Systems_SimConstants.FALSE_START_SPEED;

            float neutralZone =
                _play.LineOfScrimmageY + Systems_SimConstants.NEUTRAL_ZONE_CLEARANCE;

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle player = _registry.Get(slotIndex);

                if (player == null)
                {
                    continue;
                }

                if (player.Side == Systems_TeamSide.Offense
                    && player.Velocity.sqrMagnitude > falseStartSquared)
                {
                    return new Systems_Foul(
                        Systems_FoulType.FalseStart, Systems_TeamSide.Offense,
                        player.Id, _play.LineOfScrimmageY, 0);
                }

                if (player.Side == Systems_TeamSide.Defense
                    && player.Position.y < neutralZone)
                {
                    return new Systems_Foul(
                        Systems_FoulType.Offside, Systems_TeamSide.Defense,
                        player.Id, _play.LineOfScrimmageY, 0);
                }
            }

            return default;
        }

        // =====================================================================
        // Contact
        // =====================================================================

        /// <summary>
        /// Two opponents touching. Called by the agents from both collision
        /// callbacks, once per pair per tick — the lower slot speaks for both.
        /// </summary>
        /// <param name="isNewContact">The first tick of this collision, rather than its continuation.</param>
        public void ReportContact(int slotA, int slotB, float closingSpeed, bool isNewContact)
        {
            if (_play.Phase != Systems_PlayPhase.Live)
            {
                return;
            }

            Systems_IPlayerHandle a = _registry.Get(slotA);
            Systems_IPlayerHandle b = _registry.Get(slotB);

            if (a == null || b == null || a.Side == b.Side)
            {
                return;
            }

            Systems_IPlayerHandle offense = a.Side == Systems_TeamSide.Offense ? a : b;
            Systems_IPlayerHandle defense = a.Side == Systems_TeamSide.Offense ? b : a;

            int pair = (offense.Id * SIDE) + (defense.Id - SIDE);

            if (pair < 0 || pair >= PAIRS)
            {
                return;
            }

            int tick = _play.PhysicsTick;

            if (tick != _lastContactTick[pair])
            {
                bool continues = tick - _lastContactTick[pair]
                    <= Systems_SimConstants.CONTACT_GRACE_TICKS + 1;

                _contactTicks[pair] = continues ? _contactTicks[pair] + 1 : 1;
                _lastContactTick[pair] = tick;
            }

            if (isNewContact || !_contactJudged[pair])
            {
                JudgeContact(pair, offense, defense, closingSpeed);
            }

            JudgeHolding(pair, offense, defense);
        }

        /// <summary>
        /// The fouls a single collision can be: pass interference either way, a late
        /// hit on the passer, a block in the back, kick-catch interference. Each pair
        /// is judged once per play.
        /// </summary>
        private void JudgeContact(
            int pair, Systems_IPlayerHandle offense, Systems_IPlayerHandle defense,
            float closingSpeed)
        {
            if (TryPassInterference(pair, offense, defense, closingSpeed))
            {
                return;
            }

            if (TryRoughingThePasser(pair, offense, defense, closingSpeed))
            {
                return;
            }

            if (TryKickCatchInterference(pair, offense, defense))
            {
                return;
            }

            TryBlockInTheBack(pair, offense, defense, closingSpeed);
        }

        /// <summary>
        /// Contact between a receiver and a defender while the pass is in the air and
        /// near them, past the line. Whoever was driving into the other did it.
        /// </summary>
        private bool TryPassInterference(
            int pair, Systems_IPlayerHandle receiver, Systems_IPlayerHandle defender,
            float closingSpeed)
        {
            if (!_ball.IsInFlight || _ball.PassKind != Systems_PassKind.Forward
                || !IsEligibleReceiver(receiver.Role)
                || defender.Role == Systems_PlayerRole.DefensiveLine
                || receiver.Position.y <= _play.LineOfScrimmageY
                || closingSpeed < Systems_SimConstants.PASS_INTERFERENCE_MIN_SPEED
                || Vector2.Distance(receiver.Position, _ball.Position)
                    > Systems_SimConstants.PASS_INTERFERENCE_BALL_RANGE)
            {
                return false;
            }

            _contactJudged[pair] = true;

            Vector2 receiverToDefender = (defender.Position - receiver.Position).normalized;
            float receiverDrive = Vector2.Dot(receiver.Velocity, receiverToDefender);
            float defenderDrive = Vector2.Dot(defender.Velocity, -receiverToDefender);

            if (defenderDrive >= receiverDrive)
            {
                Flag(Systems_SimConstants.DEFENSIVE_PI_CALL_CHANCE,
                    Systems_FoulType.DefensivePassInterference, Systems_TeamSide.Defense,
                    defender.Id, receiver.Position.y);
            }
            else
            {
                Flag(Systems_SimConstants.OFFENSIVE_PI_CALL_CHANCE,
                    Systems_FoulType.OffensivePassInterference, Systems_TeamSide.Offense,
                    receiver.Id, _play.LineOfScrimmageY);
            }

            return true;
        }

        /// <summary>A hard hit on a quarterback who let the ball go a beat ago.</summary>
        private bool TryRoughingThePasser(
            int pair, Systems_IPlayerHandle offense, Systems_IPlayerHandle defense,
            float closingSpeed)
        {
            if (offense.Role != Systems_PlayerRole.Quarterback
                || !_play.ForwardPassThrown
                || offense.IsCarrier
                || _play.PhysicsTick - _play.ForwardPassTick < Systems_SimConstants.ROUGHING_LATE_TICKS
                || closingSpeed < Systems_SimConstants.ROUGHING_MIN_SPEED)
            {
                return false;
            }

            _contactJudged[pair] = true;

            Flag(Systems_SimConstants.ROUGHING_CALL_CHANCE,
                Systems_FoulType.RoughingThePasser, Systems_TeamSide.Defense,
                defense.Id, _play.LineOfScrimmageY);

            return true;
        }

        /// <summary>A cover man into the returner while the kick is still coming down on him.</summary>
        private bool TryKickCatchInterference(
            int pair, Systems_IPlayerHandle coverMan, Systems_IPlayerHandle returner)
        {
            // Only while the kick is COMING DOWN on him. Right after the kick the
            // ball is still over the punter's head, where rushers and blockers are
            // colliding, and none of that is interference with anybody's catch.
            if (!_ball.IsKicked
                || _play.Kind.IsPlaceKick()
                || _ball.VerticalVelocity >= 0f
                || _ball.Height > Systems_SimConstants.KICK_CATCH_HEIGHT * 3f
                || Vector2.Distance(returner.Position, _ball.Position)
                    > Systems_SimConstants.KICK_CATCH_INTERFERENCE_RANGE)
            {
                return false;
            }

            _contactJudged[pair] = true;

            Flag(Systems_SimConstants.KICK_CATCH_INTERFERENCE_CALL_CHANCE,
                Systems_FoulType.KickCatchInterference, Systems_TeamSide.Offense,
                coverMan.Id, returner.Position.y);

            return true;
        }

        /// <summary>
        /// A blocker on the side with the ball hitting an opponent squarely from
        /// behind — measured against the way the man hit was running.
        /// </summary>
        private void TryBlockInTheBack(
            int pair, Systems_IPlayerHandle offense, Systems_IPlayerHandle defense,
            float closingSpeed)
        {
            if (!_ball.IsHeld || closingSpeed < Systems_SimConstants.BLOCK_IN_BACK_MIN_SPEED)
            {
                return;
            }

            Systems_TeamSide holder = _ball.PossessingSide;

            Systems_IPlayerHandle blocker = holder == Systems_TeamSide.Offense ? offense : defense;
            Systems_IPlayerHandle victim = holder == Systems_TeamSide.Offense ? defense : offense;

            if (blocker.IsCarrier || victim.IsCarrier || victim.Velocity.sqrMagnitude < 1f)
            {
                return;
            }

            Vector2 victimHeading = victim.Velocity.normalized;
            Vector2 fromBlocker = (victim.Position - blocker.Position).normalized;

            if (Vector2.Dot(victimHeading, fromBlocker) < Systems_SimConstants.BLOCK_IN_BACK_COS)
            {
                return;
            }

            _contactJudged[pair] = true;

            Flag(Systems_SimConstants.BLOCK_IN_BACK_CALL_CHANCE,
                Systems_FoulType.IllegalBlockInTheBack, holder, blocker.Id, blocker.Position.y);
        }

        /// <summary>
        /// Holding, judged on the tick an engagement has lasted long enough: a beaten
        /// blocker still hanging on, or a defender riding a receiver downfield
        /// before the ball is thrown.
        /// </summary>
        private void JudgeHolding(
            int pair, Systems_IPlayerHandle offense, Systems_IPlayerHandle defense)
        {
            if (_holdingJudged[pair] || !_ball.IsHeld || offense.IsCarrier || defense.IsCarrier)
            {
                return;
            }

            int ticks = _contactTicks[pair];

            if (_ball.PossessingSide == Systems_TeamSide.Offense
                && IsBlocker(offense.Role)
                && ticks >= Systems_SimConstants.HOLDING_MIN_TICKS)
            {
                _holdingJudged[pair] = true;

                float blockerToBall = Vector2.Distance(offense.Position, _ball.Position);
                float rusherToBall = Vector2.Distance(defense.Position, _ball.Position);

                if (rusherToBall + Systems_SimConstants.HOLDING_BEATEN_MARGIN < blockerToBall)
                {
                    Flag(Systems_SimConstants.HOLDING_CALL_CHANCE,
                        Systems_FoulType.OffensiveHolding, Systems_TeamSide.Offense,
                        offense.Id, _play.LineOfScrimmageY);
                }

                return;
            }

            bool beforeTheThrow = !_play.ForwardPassThrown
                && _ball.CarrierId == Systems_Formation.QUARTERBACK_SLOT_INDEX;

            float holdingDepth = _play.LineOfScrimmageY
                + (Systems_SimConstants.DEFENSIVE_HOLDING_DEPTH_YARDS * Systems_FieldModel.YARD);

            if (beforeTheThrow
                && IsEligibleReceiver(offense.Role)
                && defense.Role != Systems_PlayerRole.DefensiveLine
                && offense.Position.y > holdingDepth
                && ticks >= Systems_SimConstants.DEFENSIVE_HOLDING_MIN_TICKS)
            {
                _holdingJudged[pair] = true;

                Flag(Systems_SimConstants.DEFENSIVE_HOLDING_CALL_CHANCE,
                    Systems_FoulType.DefensiveHolding, Systems_TeamSide.Defense,
                    defense.Id, _play.LineOfScrimmageY);
            }
        }

        /// <summary>
        /// A tackle that may have come by the facemask. Called by the referee as a
        /// tackle completes; only a hard one is a candidate.
        /// </summary>
        public void JudgeTackle(int tacklerId, Systems_IPlayerHandle carrier, float closingSpeed)
        {
            if (closingSpeed < Systems_SimConstants.FACEMASK_MIN_SPEED)
            {
                return;
            }

            Systems_IPlayerHandle tackler = _registry.Get(tacklerId);

            if (tackler == null || tackler.Side == carrier.Side)
            {
                return;
            }

            Flag(Systems_SimConstants.FACEMASK_CHANCE,
                Systems_FoulType.Facemask, tackler.Side, tacklerId, carrier.Position.y);
        }

        /// <summary>The flag, if the official saw it. One draw per judgement, seen or not.</summary>
        private void Flag(
            float chance, Systems_FoulType type, Systems_TeamSide side, int playerId, float spotY)
        {
            bool seen = _rng.NextFloat() < chance;

            if (!seen || _play.HasFoul(side, type))
            {
                return;
            }

            _play.RecordFoul(new Systems_Foul(type, side, playerId, spotY, _play.PhysicsTick));
        }

        private static bool IsEligibleReceiver(Systems_PlayerRole role)
        {
            return role == Systems_PlayerRole.WideReceiver
                || role == Systems_PlayerRole.TightEnd
                || role == Systems_PlayerRole.RunningBack
                || role == Systems_PlayerRole.Fullback;
        }

        private static bool IsBlocker(Systems_PlayerRole role)
        {
            return role == Systems_PlayerRole.OffensiveLine
                || role == Systems_PlayerRole.TightEnd
                || role == Systems_PlayerRole.Fullback
                || role == Systems_PlayerRole.RunningBack;
        }
    }
}
