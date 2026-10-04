using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// The twenty-two athletes: one <see cref="Systems_PlayerTraits"/> per
    /// formation slot, drawn from its own seeded stream.
    ///
    /// A GAME KEEPS ITS ROSTER, TRAINING REDRAWS IT EVERY PLAY. In a played game
    /// the slot is the player, so the fast corner is fast from kickoff to whistle
    /// and a pinned seed replays the same twenty-two people. A training run that
    /// did the same would fit each brain against twenty-two fixed bodies for
    /// twelve million steps and then meet different ones in every game, so
    /// training draws a fresh roster per episode and the whole range is in the
    /// distribution. The dynamics are the same function of the traits in both
    /// modes, and the traits are observed, so this is the spot provider's kind of
    /// difference — which starting state — not the kick model's.
    ///
    /// Injected rather than static for the reason Systems_FormationSelection is.
    /// </summary>
    public sealed class Systems_Roster
    {
        /// <summary>Arbitrary odd constant; keeps this stream independent.</summary>
        private const uint SEED_OFFSET = 0x85EBCA6Bu;

        private readonly Systems_PlayerTraits[] _traits =
            new Systems_PlayerTraits[Systems_PlayerRegistry.CAPACITY];

        private readonly bool _redrawEveryPlay;

        private Random _rng;

        public Systems_Roster(Systems_SimMode simMode)
        {
            uint seed = Systems_EpisodeSeed.Value + SEED_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
            _redrawEveryPlay = simMode == Systems_SimMode.Training;
            Draw();
        }

        /// <summary>Role-average for a slot that does not exist.</summary>
        public Systems_PlayerTraits TraitsOf(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < _traits.Length
                ? _traits[slotIndex]
                : default;
        }

        /// <summary>
        /// Called by Systems_EpisodeDirector once per play, before the bodies are
        /// restored. Does nothing in a game.
        /// </summary>
        public void NextPlay()
        {
            if (_redrawEveryPlay)
            {
                Draw();
            }
        }

        /// <summary>
        /// Whether somebody jumps before this snap, given a draw on [0, 1). Each
        /// of the twenty-two carries an equal share of his side's rate, moved by
        /// his own discipline, so an undisciplined line is flagged more often.
        ///
        /// Game layer only — Systems_GameFlowSystem is the one caller, and the
        /// draw is Systems_IKickModel.PreSnapFoulDraw. A flag is noise no policy
        /// can influence, which is the reason the kick model keeps training a pure
        /// function, so training never throws one.
        /// </summary>
        public Systems_PreSnapFoul FoulFor(float unit)
        {
            float falseStart = SideChance(
                0, Systems_GameRules.FALSE_START_CHANCE);

            float offside = SideChance(
                Systems_FormationBook.SLOTS_PER_SIDE, Systems_GameRules.OFFSIDE_CHANCE);

            if (unit < falseStart)
            {
                return Systems_PreSnapFoul.FalseStart;
            }

            return unit < falseStart + offside
                ? Systems_PreSnapFoul.Offside
                : Systems_PreSnapFoul.None;
        }

        private float SideChance(int firstSlot, float sideRate)
        {
            float chance = 0f;

            for (int slotIndex = firstSlot;
                slotIndex < firstSlot + Systems_FormationBook.SLOTS_PER_SIDE;
                slotIndex++)
            {
                chance += (sideRate / Systems_FormationBook.SLOTS_PER_SIDE)
                    * (1f - (Systems_GameRules.PENALTY_DISCIPLINE_WEIGHT
                        * _traits[slotIndex].Discipline));
            }

            return chance;
        }

        private void Draw()
        {
            for (int slotIndex = 0; slotIndex < _traits.Length; slotIndex++)
            {
                _traits[slotIndex] = new Systems_PlayerTraits(
                    NextDeviation(), NextDeviation(), NextDeviation(), NextDeviation());
            }
        }

        /// <summary>
        /// Two draws averaged: triangular on [-1, 1], peaked at 0, as the throw
        /// error is. Most players are near their position's average and the
        /// outlier is rare.
        /// </summary>
        private float NextDeviation()
        {
            return (_rng.NextFloat() + _rng.NextFloat()) - 1f;
        }
    }
}
