using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Where a team's terminal reward goes when the trainer is running MA-POCA.
    ///
    /// WHY THIS INTERFACE HAS TO EXIST AT ALL. Under POCA the shared outcome must
    /// reach the agents as a GROUP reward, not as twenty-two individual ones —
    /// that is the whole mechanism by which the centralized critic learns to
    /// attribute a team result to the agent that caused it. But
    /// <c>Agent.AddGroupReward</c> is `internal` to Unity.ML-Agents, so only a
    /// SimpleMultiAgentGroup can call it, and it must be called ONCE PER GROUP:
    /// SimpleMultiAgentGroup.AddGroupReward already loops its own members, so
    /// calling it inside the existing per-player loop would pay every agent eleven
    /// times over.
    ///
    /// PoFootball.Systems is forbidden from referencing Unity.ML-Agents
    /// (CLAUDE.md), so it cannot hold a group. This is the seam: Systems owns the
    /// interface and calls it once per side, and Agent_TeamGroups — which lives in
    /// the assembly that is allowed to know about ML-Agents — implements it.
    ///
    /// Absent in a played game and in any training run that is not grouped, in
    /// which case Systems_TrainingEpisodeBoundary falls back to the per-agent
    /// reward it always used.
    /// </summary>
    public interface Systems_ITeamRewardSink
    {
        /// <summary>
        /// Applies the play's terminal outcome to every agent on
        /// <paramref name="side"/> as a GROUP reward. Called once per side per play,
        /// never per player.
        ///
        /// Takes the outcome rather than a number because PoFootball.Systems does
        /// not reference PoFootball.Rewards — the reward math is a training concern
        /// and lives on the far side of this seam, with the implementation.
        /// </summary>
        void AddTeamTerminal(
            Systems_TeamSide side,
            Systems_PlayOutcome outcome,
            float netYards,
            bool passCompleted);
    }
}
