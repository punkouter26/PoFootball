using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// What happens to the learning process when a play ends.
    ///
    /// Systems_EpisodeDirector owns the physical reset — re-forming twenty-two
    /// bodies at a new line of scrimmage — and that is identical in both modes. It
    /// used to also assign terminal rewards and end every agent's episode inline,
    /// unconditionally, which meant a played game ran the entire reward pipeline
    /// every down to produce numbers no policy would ever learn from.
    ///
    /// The director now calls this immediately before re-forming, and what is bound
    /// to it decides whether that means anything:
    /// <see cref="Systems_TrainingEpisodeBoundary"/> in training,
    /// <see cref="Systems_NullEpisodeBoundary"/> in a game.
    /// </summary>
    public interface Systems_ITrainingEpisodeBoundary
    {
        /// <summary>
        /// Called once per play, on the FixedTick the director has decided to
        /// reset, before any body has been moved. The play's state is still exactly
        /// as the whistle left it.
        /// </summary>
        void EndEpisode(Systems_PlayOutcome outcome, float netYards, bool passCompleted);
    }

    /// <summary>
    /// The game-mode binding: a played game has no episodes and no rewards.
    ///
    /// A null object rather than a null check in the director, so there is exactly
    /// one shape of control flow to read and no branch that only ever runs in one
    /// mode.
    /// </summary>
    public sealed class Systems_NullEpisodeBoundary : Systems_ITrainingEpisodeBoundary
    {
        public void EndEpisode(Systems_PlayOutcome outcome, float netYards, bool passCompleted)
        {
        }
    }

    /// <summary>
    /// The training binding: assign every player its terminal reward, then end
    /// every episode together.
    ///
    /// BOTH LOOPS RUN TO COMPLETION SEPARATELY, which is the one thing here that
    /// must not be merged into a single pass. EndEpisode on an ML-Agents Agent
    /// requests a decision for the next step; rewarding player 12 after player 11's
    /// episode has already been closed would attribute it to the wrong episode.
    /// </summary>
    public sealed class Systems_TrainingEpisodeBoundary : Systems_ITrainingEpisodeBoundary
    {
        private readonly Systems_PlayerRegistry _registry;

        public Systems_TrainingEpisodeBoundary(Systems_PlayerRegistry registry)
        {
            _registry = registry;
        }

        public void EndEpisode(Systems_PlayOutcome outcome, float netYards, bool passCompleted)
        {
            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                TrainingHandle(slotIndex)?.ApplyTerminalReward(outcome, netYards, passCompleted);
            }

            for (int slotIndex = 0; slotIndex < Systems_PlayerRegistry.CAPACITY; slotIndex++)
            {
                TrainingHandle(slotIndex)?.EndEpisodeNow();
            }
        }

        /// <summary>
        /// The registry is typed to Systems_IPlayerHandle because that is all the
        /// simulation needs; Agent_FootballPlayer implements both contracts, so the
        /// cast succeeds for every real agent. It returns null rather than throwing
        /// for a test double that only implements the gameplay half — which is
        /// exactly what an EditMode test of the director should be able to pass in.
        ///
        /// Plain C# interfaces, not UnityEngine.Object references, so ?. is safe.
        /// </summary>
        private Systems_ITrainingHandle TrainingHandle(int slotIndex)
        {
            return _registry.Get(slotIndex) as Systems_ITrainingHandle;
        }
    }
}
