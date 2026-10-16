using PoFootball.Models;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// Whether a player actually pulls off the thing he was in position to do: put
    /// the throw on the receiver, hold the catch, finish the tackle.
    ///
    /// WHY THIS EXISTS (revision 13). Through revision 12 every one of these was a
    /// rule with one answer. A ball inside CATCH_RADIUS was caught, a defender
    /// inside PASS_BREAKUP_RADIUS broke it up, a hit over TACKLE_CLOSING_SPEED was a
    /// tackle and a wrap-up held for its tick count was a tackle. So being in
    /// position WAS the skill, nobody was ever bad at anything, and the only throw
    /// that could miss was the scripted quarterback's — whose scatter was applied
    /// to the aim, which Systems_BallSystem then blended 85% back toward the
    /// perfect lead. football_base14 completed 91-97% of its passes all run.
    ///
    /// THE SAME DRAW IN TRAINING AND IN A PLAYED GAME, unlike Systems_IFumbleModel,
    /// which keeps training a pure function. That one prices a decision a carrier
    /// makes; these price execution, and a brain
    /// fitted against tackles that never miss and passes that never drop would be
    /// evaluated against different dynamics the moment a game gave it either
    /// (CLAUDE.md section 2). It is why the contract revision moved.
    ///
    /// SINCE REVISION 14 IT ALSO KICKS. How far a punt, a kickoff or a placekick
    /// goes and how far off line, whether a rusher's hand gets it, whether a
    /// returner muffs it, whether a man falls on a loose ball, whether a pitch is
    /// held — all execution, all here. The kick model that used to roll a punt's
    /// net and a field goal's result at the rules layer is gone.
    ///
    /// The interface is the seam the referee tests use: they pin the rules with a
    /// stub that never fails, and pin the failures with one that always does.
    /// </summary>
    public interface Systems_ISkillModel
    {
        /// <summary>Signed angle to rotate a throw off its intended line, radians.</summary>
        float ThrowErrorRadians(float throwMetres, float throwerFatigue);

        /// <summary>An uncontested ball in the receiver's hands: does he hold it.</summary>
        bool IsCatchHeld(float airMetres, float catcherFatigue);

        /// <param name="defenderMetres">Nearest eligible defender to the ball,
        /// already known to be inside PASS_BREAKUP_RADIUS.</param>
        bool IsPassBrokenUp(float defenderMetres);

        /// <summary>A defender nearest the ball: does he catch it or bat it down.</summary>
        bool IsInterceptionHeld();

        /// <summary>A hit over TACKLE_CLOSING_SPEED: does it bring the carrier down.</summary>
        bool IsHitTackleMade(
            Systems_PlayerRole carrier, float tacklerFatigue, float carrierFatigue);

        /// <param name="armOnly">No body ever touched him during this wrap-up — it
        /// was all reach (Systems_Referee.CountPursuitReach), a dive at his ankles
        /// rather than a man on him.</param>
        bool IsWrapUpHeld(
            Systems_PlayerRole carrier, int tacklerCount, bool armOnly,
            float tacklerFatigue, float carrierFatigue);

        // --- Revision 14: the kicking game, the loose ball, the pitch ----------

        /// <summary>
        /// Draws one kick: how far it goes along the ground (yards), how long it
        /// hangs (seconds), and how far off its line (radians). A placekick's
        /// distance is its CARRY — where it drops below the crossbar — and its hang
        /// is unused.
        /// </summary>
        Systems_KickDraw DrawKick(Systems_PlayKind kind, bool onside);

        /// <summary>A rusher got to the spot as the kick left the foot: does he get a hand on it.</summary>
        bool IsKickBlocked();

        /// <summary>A returner under the kick: does he put it on the ground.</summary>
        bool IsKickMuffed(bool pressured);

        /// <summary>A man on a loose ball: does he come up with it this tick.</summary>
        bool IsLooseBallRecovered();

        /// <summary>A pitch arriving at the man it was meant for: does he hold it.</summary>
        bool IsLateralCaught();

        /// <summary>Signed angle a bouncing or popped-out ball turns through, radians, within ±maxDegrees.</summary>
        float ScatterRadians(float maxDegrees);
    }

    /// <summary>One kick, as Systems_ISkillModel.DrawKick decided it.</summary>
    public readonly struct Systems_KickDraw
    {
        public readonly float DistanceYards;

        public readonly float HangSeconds;

        public readonly float ErrorRadians;

        public Systems_KickDraw(float distanceYards, float hangSeconds, float errorRadians)
        {
            DistanceYards = distanceYards;
            HangSeconds = hangSeconds;
            ErrorRadians = errorRadians;
        }
    }

    /// <summary>
    /// The one implementation the game runs. Seeded from Systems_EpisodeSeed with
    /// its own stream offset, as every seeded model here is, so a
    /// pinned seed still replays draw for draw and the spot provider's stream is
    /// not disturbed by how many tackles a play happened to contain.
    ///
    /// Every chance is a public pure function beside the draw that uses it, so a
    /// test can pin the curve without sampling it.
    /// </summary>
    public sealed class Systems_SeededSkillModel : Systems_ISkillModel
    {
        /// <summary>Arbitrary odd constant; keeps this stream independent.</summary>
        private const uint STREAM_OFFSET = 0xC2B2AE35u;

        private Random _rng;

        public Systems_SeededSkillModel()
        {
            uint seed = Systems_EpisodeSeed.Value ^ STREAM_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        public float ThrowErrorRadians(float throwMetres, float throwerFatigue)
        {
            // Two draws averaged: triangular on [-1, 1], peaked at 0. Most throws
            // are close and the badly missed one is in the tail, which is how a
            // real quarterback is distributed. A uniform draw would make every
            // pass equally likely to be terrible.
            float unit = (_rng.NextFloat() + _rng.NextFloat()) - 1f;

            return unit * ThrowSpreadDegrees(throwMetres, throwerFatigue) * Mathf.Deg2Rad;
        }

        public bool IsCatchHeld(float airMetres, float catcherFatigue)
        {
            return _rng.NextFloat() >= DropChance(airMetres, catcherFatigue);
        }

        public bool IsPassBrokenUp(float defenderMetres)
        {
            return _rng.NextFloat() < BreakUpChance(defenderMetres);
        }

        public bool IsInterceptionHeld()
        {
            return _rng.NextFloat() < Systems_SimConstants.INTERCEPTION_HOLD_CHANCE;
        }

        public bool IsHitTackleMade(
            Systems_PlayerRole carrier, float tacklerFatigue, float carrierFatigue)
        {
            return _rng.NextFloat()
                >= MissedHitChance(carrier, tacklerFatigue, carrierFatigue);
        }

        public bool IsWrapUpHeld(
            Systems_PlayerRole carrier, int tacklerCount, bool armOnly,
            float tacklerFatigue, float carrierFatigue)
        {
            return _rng.NextFloat() >= BrokenWrapChance(
                carrier, tacklerCount, armOnly, tacklerFatigue, carrierFatigue);
        }

        public Systems_KickDraw DrawKick(Systems_PlayKind kind, bool onside)
        {
            // Three triangular draws, always, in this order, whatever the kind —
            // so the number of draws a play consumes does not depend on which kick
            // it was, and a pinned seed stays aligned.
            float distanceUnit = Triangular();
            float hangUnit = Triangular();
            float errorUnit = Triangular();

            switch (kind)
            {
                case Systems_PlayKind.Punt:
                    return new Systems_KickDraw(
                        Systems_SimConstants.PUNT_DISTANCE_YARDS
                            + (distanceUnit * Systems_SimConstants.PUNT_DISTANCE_SPREAD_YARDS),
                        Systems_SimConstants.PUNT_HANG_SECONDS
                            + (hangUnit * Systems_SimConstants.PUNT_HANG_SPREAD_SECONDS),
                        errorUnit * Systems_SimConstants.PUNT_SCATTER_DEGREES * Mathf.Deg2Rad);

                case Systems_PlayKind.FieldGoal:
                case Systems_PlayKind.ExtraPoint:
                    return new Systems_KickDraw(
                        Systems_SimConstants.KICKER_CARRY_YARDS
                            + (distanceUnit * Systems_SimConstants.KICKER_CARRY_SPREAD_YARDS),
                        0f,
                        errorUnit * Systems_SimConstants.PLACEKICK_SCATTER_DEGREES * Mathf.Deg2Rad);

                default:
                    if (onside)
                    {
                        return new Systems_KickDraw(
                            Systems_SimConstants.ONSIDE_DISTANCE_YARDS
                                + (distanceUnit * Systems_SimConstants.ONSIDE_DISTANCE_SPREAD_YARDS),
                            Systems_SimConstants.ONSIDE_HANG_SECONDS,
                            errorUnit * Systems_SimConstants.ONSIDE_SCATTER_DEGREES * Mathf.Deg2Rad);
                    }

                    return new Systems_KickDraw(
                        Systems_SimConstants.KICKOFF_DISTANCE_YARDS
                            + (distanceUnit * Systems_SimConstants.KICKOFF_DISTANCE_SPREAD_YARDS),
                        Systems_SimConstants.KICKOFF_HANG_SECONDS
                            + (hangUnit * Systems_SimConstants.KICKOFF_HANG_SPREAD_SECONDS),
                        errorUnit * Systems_SimConstants.KICKOFF_SCATTER_DEGREES * Mathf.Deg2Rad);
            }
        }

        public bool IsKickBlocked()
        {
            return _rng.NextFloat() < Systems_SimConstants.KICK_BLOCK_CHANCE;
        }

        public bool IsKickMuffed(bool pressured)
        {
            return _rng.NextFloat() < (pressured
                ? Systems_SimConstants.KICK_MUFF_PRESSURED_CHANCE
                : Systems_SimConstants.KICK_MUFF_CHANCE);
        }

        public bool IsLooseBallRecovered()
        {
            return _rng.NextFloat() < Systems_SimConstants.LOOSE_BALL_RECOVERY_CHANCE;
        }

        public bool IsLateralCaught()
        {
            return _rng.NextFloat() >= Systems_SimConstants.LATERAL_DROP_CHANCE;
        }

        public float ScatterRadians(float maxDegrees)
        {
            return Triangular() * maxDegrees * Mathf.Deg2Rad;
        }

        /// <summary>Triangular on [-1, 1], peaked at 0 — the shape every error here takes.</summary>
        private float Triangular()
        {
            return (_rng.NextFloat() + _rng.NextFloat()) - 1f;
        }

        /// <summary>
        /// Half-width of the throw error, in degrees. A miss is an ANGLE, so the
        /// lateral error is the angle times the throw length: the wobble that is
        /// harmless on a five-yard out is two metres off on a forty-yard shot, and
        /// the completion rate falls off with depth on its own.
        /// </summary>
        public static float ThrowSpreadDegrees(float throwMetres, float throwerFatigue)
        {
            float fresh = Systems_SimConstants.THROW_SCATTER_BASE_DEGREES
                + (Mathf.Max(0f, throwMetres) * Systems_SimConstants.THROW_SCATTER_DEGREES_PER_METRE);

            return fresh * (1f + (Systems_SimConstants.SKILL_FATIGUE_GAIN * Mathf.Clamp01(throwerFatigue)));
        }

        public static float DropChance(float airMetres, float catcherFatigue)
        {
            float chance = Systems_SimConstants.DROP_BASE_CHANCE
                + (Mathf.Max(0f, airMetres) * Systems_SimConstants.DROP_CHANCE_PER_METRE);

            chance *= 1f + (Systems_SimConstants.SKILL_FATIGUE_GAIN * Mathf.Clamp01(catcherFatigue));

            return Mathf.Min(chance, Systems_SimConstants.SKILL_FAILURE_CEILING);
        }

        /// <summary>
        /// Near-certain with the defender on the ball, a coin flip at the edge of
        /// his reach. Still a function of DISTANCE, so "covered" remains something
        /// a policy can see and close, which is what the revision 11 rule was for.
        /// </summary>
        public static float BreakUpChance(float defenderMetres)
        {
            float closeness = 1f - Mathf.Clamp01(
                defenderMetres / Systems_SimConstants.PASS_BREAKUP_RADIUS);

            return Mathf.Lerp(
                Systems_SimConstants.PASS_BREAKUP_CHANCE_AT_EDGE,
                Systems_SimConstants.PASS_BREAKUP_CHANCE_AT_BALL,
                closeness);
        }

        public static float MissedHitChance(
            Systems_PlayerRole carrier, float tacklerFatigue, float carrierFatigue)
        {
            float chance = Systems_SimConstants.MISSED_HIT_CHANCE * ElusivenessOf(carrier);

            return Mathf.Min(
                chance * FatigueEdge(tacklerFatigue, carrierFatigue),
                Systems_SimConstants.SKILL_FAILURE_CEILING);
        }

        public static float BrokenWrapChance(
            Systems_PlayerRole carrier, int tacklerCount, bool armOnly,
            float tacklerFatigue, float carrierFatigue)
        {
            float chance = Systems_SimConstants.BROKEN_WRAP_CHANCE * PowerOf(carrier);

            if (armOnly)
            {
                chance *= Systems_SimConstants.ARM_TACKLE_BREAK_MULTIPLIER;
            }

            // Nobody runs through three men. The same division the wrap-up time
            // itself is NOT given (see Systems_Referee.ReportSustainedContact):
            // there it collapsed the down, here it only removes a lucky escape.
            chance /= Mathf.Max(1, tacklerCount);

            return Mathf.Min(
                chance * FatigueEdge(tacklerFatigue, carrierFatigue),
                Systems_SimConstants.SKILL_FAILURE_CEILING);
        }

        /// <summary>
        /// How much the carrier's chance of getting away moves with who is more
        /// tired: a spent tackler misses more, a spent carrier escapes less.
        /// </summary>
        private static float FatigueEdge(float tacklerFatigue, float carrierFatigue)
        {
            float gain = Systems_SimConstants.SKILL_FATIGUE_GAIN;

            return (1f + (gain * Mathf.Clamp01(tacklerFatigue)))
                * (1f - (gain * Mathf.Clamp01(carrierFatigue)));
        }

        /// <summary>
        /// Multiplier on slipping a HIT — being missed in space. The halfback and
        /// the receivers make people miss; a quarterback in the pocket does not.
        /// </summary>
        private static float ElusivenessOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.RunningBack:
                    return 1.4f;

                case Systems_PlayerRole.WideReceiver:
                    return 1.2f;

                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                    return 0.9f;

                case Systems_PlayerRole.Quarterback:
                    return 0.7f;

                default:
                    return 1f;
            }
        }

        /// <summary>
        /// Multiplier on breaking a WRAP — running through a man who has hold of
        /// him. The opposite ordering to elusiveness on purpose: the fullback is
        /// the one who drags a tackler, the receiver the one who goes down.
        /// </summary>
        private static float PowerOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.Fullback:
                    return 1.6f;

                case Systems_PlayerRole.RunningBack:
                    return 1.3f;

                case Systems_PlayerRole.TightEnd:
                    return 1.1f;

                case Systems_PlayerRole.WideReceiver:
                    return 0.8f;

                case Systems_PlayerRole.Quarterback:
                    return 0.6f;

                default:
                    return 1f;
            }
        }
    }
}
