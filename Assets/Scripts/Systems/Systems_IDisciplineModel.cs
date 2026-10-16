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
    /// A played game. Rates are per snap and come from the NFL's long-run counts:
    /// about one false start per team per game, an offside a little more than every
    /// other game, and a delay of game every second or third.
    /// </summary>
    public sealed class Systems_SeededDisciplineModel : Systems_IDisciplineModel
    {
        /// <summary>Chance per snap that an offensive lineman or the tight end moves early.</summary>
        public const float FALSE_START_CHANCE = 0.014f;

        /// <summary>Chance per snap that a defensive lineman jumps into the neutral zone.</summary>
        public const float OFFSIDE_CHANCE = 0.008f;

        /// <summary>Chance per snap that the play clock runs out.</summary>
        public const float DELAY_OF_GAME_CHANCE = 0.004f;

        /// <summary>Arbitrary odd constant; keeps this stream independent of the others.</summary>
        private const uint STREAM_OFFSET = 0x27D4EB2Fu;

        /// <summary>The offensive line and the tight end: slots 0 to 5.</summary>
        private const int OFFENSIVE_FRONT_SLOTS = 6;

        /// <summary>The defensive line: the first four defensive slots.</summary>
        private const int DEFENSIVE_FRONT_SLOTS = 4;

        private Random _rng;

        public Systems_SeededDisciplineModel()
        {
            uint seed = Systems_EpisodeSeed.Value ^ STREAM_OFFSET;
            _rng = new Random(seed == 0u ? 1u : seed);
        }

        public int DrawFlinch()
        {
            // Two draws, always, so a pinned seed stays aligned however it lands.
            float roll = _rng.NextFloat();
            float pick = _rng.NextFloat();

            if (roll < FALSE_START_CHANCE)
            {
                return (int)(pick * OFFENSIVE_FRONT_SLOTS) % OFFENSIVE_FRONT_SLOTS;
            }

            if (roll < FALSE_START_CHANCE + OFFSIDE_CHANCE)
            {
                return Systems_FormationBook.SLOTS_PER_SIDE
                    + ((int)(pick * DEFENSIVE_FRONT_SLOTS) % DEFENSIVE_FRONT_SLOTS);
            }

            return -1;
        }

        public bool IsDelayOfGame()
        {
            return _rng.NextFloat() < DELAY_OF_GAME_CHANCE;
        }
    }
}
