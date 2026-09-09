using UnityEngine;
using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// Which formations the two teams are lined up in for the play about to be
    /// snapped, and the draw that chooses them.
    ///
    /// INJECTED RATHER THAN STATIC, and that is the whole reason this is a class
    /// and not two fields on <see cref="Systems_FormationBook"/>. This is per-play
    /// mutable state read by twenty-two agents; a static holder for it would be
    /// precisely the shared mutable global that .claude/rules/architecture.md rules
    /// out. Systems_EpisodeSeed is static and stays static because it is written
    /// once per process and is configuration; this is written once per play.
    ///
    /// ITS OWN RNG, SEEDED FROM THE EPISODE SEED, AND DELIBERATELY NOT THE SPOT
    /// PROVIDER'S. CLAUDE.md's standing invariant is that the line of scrimmage is
    /// the only thing that differs between training and a played game — training
    /// draws it from a seeded RNG call for call, a game takes it from the chains.
    /// Drawing formations from the spot provider's stream would have broken that
    /// twice over: it would put a second draw inside the one call training
    /// reproduces, and it would give the two modes different formation sequences
    /// because the game's provider makes no draw at all.
    ///
    /// A separate stream keeps the invariant exactly as it was. Both modes advance
    /// this RNG once per play, in the same order, from the same seed, so the
    /// formation sequence is identical in training and in a game — and the line of
    /// scrimmage remains the only difference between them.
    ///
    /// The offset seed is so the formation stream is not a copy of the spot
    /// stream; seeded identically, both would draw the same normalised sequence and
    /// the formation would correlate with the field position on every single play.
    /// </summary>
    public sealed class Systems_FormationSelection
    {
        /// <summary>
        /// Mixed into the episode seed so this stream differs from the spot
        /// provider's. An arbitrary odd constant — its only requirement is that it
        /// is not zero and not shared with another stream.
        /// </summary>
        private const uint SEED_OFFSET = 0x9E3779B9u;

        private Random _rng;

        public Systems_FormationSelection()
        {
            // Random rejects a zero seed, and the offset can wrap to zero.
            uint seed = Systems_EpisodeSeed.Value + SEED_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        /// <summary>The offense's alignment for the current play.</summary>
        public Systems_OffensiveFormation Offense { get; private set; }
            = Systems_OffensiveFormation.ProI;

        /// <summary>The defense's front and coverage for the current play.</summary>
        public Systems_DefensiveFormation Defense { get; private set; }
            = Systems_DefensiveFormation.FourThreeBase;

        /// <summary>
        /// Draws the next pair. Called by <see cref="Systems_EpisodeDirector"/>
        /// exactly once per episode, immediately before the formation is laid out —
        /// the same place and the same cadence as the line of scrimmage.
        ///
        /// Independent draws for the two sides: a defense does not get to see the
        /// offense's alignment before it lines up, and pairing them would make eight
        /// of the sixty-four matchups impossible for no reason a viewer could name.
        /// </summary>
        public void DrawNext()
        {
            Offense = (Systems_OffensiveFormation)_rng.NextInt(
                0, Systems_FormationBook.OffensiveFormationCount);

            Defense = (Systems_DefensiveFormation)_rng.NextInt(
                0, Systems_FormationBook.DefensiveFormationCount);

            // One line per snap, naming both calls.
            //
            // WITHOUT IT THE FEATURE IS UNVERIFIABLE ON A DEVICE. Twenty-two shapes
            // on a phone screen do not say which of sixty-four pairings is on the
            // field — three formations differ only by where the fullback stands, and
            // at the size a handset draws them that is a few pixels. This is the
            // same reason Systems_GameFlowSystem prints a REALISM verdict rather
            // than leaving someone to judge the balance by watching.
            //
            // GATED ON isDebugBuild, WHICH IS THE POINT. A retail player has no
            // business logging a line a second, and — the case that actually
            // matters — a training run must not: SCN_TRAIN_FOOTBALL snaps a fresh
            // episode several times a second per env across twelve envs, and
            // Debug.Log is not free. The training env is not a development build, so
            // this compiles to a branch that is never taken there.
            if (Debug.isDebugBuild)
            {
                Debug.Log($"[PoFootball] Formation: {Offense} vs {Defense}");
            }
        }

        /// <summary>
        /// The starting position of a squad slot, 0..21, under the formations
        /// currently drawn. Offense occupies 0..10 and defense 11..21, which is the
        /// same absolute indexing every agent in the scene already carries.
        /// </summary>
        public Systems_FormationSlot GetSlot(int slotIndex)
        {
            return slotIndex < Systems_FormationBook.SLOTS_PER_SIDE
                ? Systems_FormationBook.OffenseSlot(Offense, slotIndex)
                : Systems_FormationBook.DefenseSlot(
                    Defense, slotIndex - Systems_FormationBook.SLOTS_PER_SIDE);
        }

        /// <summary>
        /// The offensive slot a defender covers man-to-man under the current
        /// defensive call, or -1 for zone — which the agent layer reads as "drop to
        /// your area" rather than "chase a man".
        /// </summary>
        public int CoverageAssignmentFor(int defenderSlotIndex)
        {
            return Systems_FormationBook.CoverageAssignmentFor(
                Defense, defenderSlotIndex);
        }
    }
}
