using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// Whether somebody jumps before the snap, and whether the offense gets the snap
    /// off in time.
    ///
    /// WHY THIS IS DRAWN AND NOT LEARNED. The bodies are frozen while the teams are
    /// set (Systems_SimConstants.PRESNAP_TICKS), because ML-Agents' continuous policy
    /// carries one exploration noise for every state: a body free to move before the
    /// snap would jitter across the line on most plays of a fresh run, and every
    /// episode would end in a false start before any football happened. With the
    /// bodies frozen nothing a policy does can cause a pre-snap foul — so this is not
    /// in training at all (<see cref="Systems_NoDisciplineModel"/>), for the same
    /// reason a punt's net was a pure function there through revision 13: a foul no
    /// policy can influence is noise in the reward and nothing else.
    ///
    /// The flinch it draws is still REFEREED. Systems_EpisodeDirector puts the step
    /// into the body, and Systems_PenaltySystem calls the false start or the offside
    /// off what the body then did, exactly as it would off anything else.
    /// </summary>
    public interface Systems_IDisciplineModel
    {
        /// <summary>The formation slot that flinches on this snap, or -1. Asked once per snap.</summary>
        int DrawFlinch();

        /// <summary>Whether the offense lets the play clock run out on this snap. Asked once per snap.</summary>
        bool IsDelayOfGame();
    }

    /// <summary>Training: nobody ever jumps, and the play clock is not a thing.</summary>
    public sealed class Systems_NoDisciplineModel : Systems_IDisciplineModel
    {
        public int DrawFlinch()
        {
            return -1;
        }

        public bool IsDelayOfGame()
        {
            return false;
        }
    }

    /// <summary>
    /// A played game. The rates are Systems_GameRules.FALSE_START_CHANCE and
    /// OFFSIDE_CHANCE per snap for a side of average discipline, from the NFL's
    /// long-run counts.
    ///
    /// WHO JUMPS IS THE ROSTER'S (revision 15). Each man on the front carries an
    /// equal share of his side's rate, moved by his own discipline trait
    /// (Systems_PlayerTraits.Discipline, PENALTY_DISCIPLINE_WEIGHT), so an
    /// undisciplined line is flagged more often and the flag lands on him. This is
    /// the merge of two revision 14s: one drew the flinch here and refereed it, the
    /// other rolled the flag between plays from the traits. The flinch is kept —
    /// it is refereed and enforced like any other foul — and the traits decide it.
    /// </summary>
    public sealed class Systems_SeededDisciplineModel : Systems_IDisciplineModel
    {
        /// <summary>Chance per snap that the play clock runs out.</summary>
        public const float DELAY_OF_GAME_CHANCE = 0.004f;

        /// <summary>Arbitrary odd constant; keeps this stream independent of the others.</summary>
        private const uint STREAM_OFFSET = 0x27D4EB2Fu;

        /// <summary>The offensive line and the tight end: slots 0-5.</summary>
        private const int OFFENSIVE_FRONT_SLOTS = 6;

        /// <summary>The defensive line, from the first defensive slot.</summary>
        private const int DEFENSIVE_FRONT_SLOTS = 4;

        private readonly Systems_Roster _roster;

        private Random _rng;

        public Systems_SeededDisciplineModel(Systems_Roster roster)
        {
            _roster = roster;
            uint seed = Systems_EpisodeSeed.Value ^ STREAM_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        public int DrawFlinch()
        {
            // Two draws, always, so a pinned seed stays aligned however it lands.
            float roll = _rng.NextFloat();
            float pick = _rng.NextFloat();

            float falseStart = FrontChance(0, OFFENSIVE_FRONT_SLOTS, Systems_GameRules.FALSE_START_CHANCE);

            if (roll < falseStart)
            {
                return PickFrom(0, OFFENSIVE_FRONT_SLOTS, pick);
            }

            float offside = FrontChance(
                Systems_FormationBook.SLOTS_PER_SIDE, DEFENSIVE_FRONT_SLOTS, Systems_GameRules.OFFSIDE_CHANCE);

            if (roll < falseStart + offside)
            {
                return PickFrom(Systems_FormationBook.SLOTS_PER_SIDE, DEFENSIVE_FRONT_SLOTS, pick);
            }

            return -1;
        }

        public bool IsDelayOfGame()
        {
            return _rng.NextFloat() < DELAY_OF_GAME_CHANCE;
        }

        /// <summary>One man's weight: 1 for average discipline, less for more.</summary>
        private float WeightOf(int slotIndex)
        {
            float discipline = _roster == null ? 0f : _roster.TraitsOf(slotIndex).Discipline;
            return 1f - (Systems_GameRules.PENALTY_DISCIPLINE_WEIGHT * discipline);
        }

        private float FrontChance(int firstSlot, int count, float sideRate)
        {
            float total = 0f;

            for (int slotIndex = firstSlot; slotIndex < firstSlot + count; slotIndex++)
            {
                total += WeightOf(slotIndex);
            }

            return sideRate * total / count;
        }

        /// <summary>The man who jumped, chosen in proportion to his weight.</summary>
        private int PickFrom(int firstSlot, int count, float pick)
        {
            float total = 0f;

            for (int slotIndex = firstSlot; slotIndex < firstSlot + count; slotIndex++)
            {
                total += WeightOf(slotIndex);
            }

            float target = pick * total;

            for (int slotIndex = firstSlot; slotIndex < firstSlot + count; slotIndex++)
            {
                target -= WeightOf(slotIndex);

                if (target < 0f)
                {
                    return slotIndex;
                }
            }

            return firstSlot + count - 1;
        }
    }
}
