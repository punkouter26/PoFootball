using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>
    /// The contract an agent exposes to the systems layer. Implemented by
    /// Agent_FootballPlayer. Systems drive resets and rewards through this
    /// interface so they never need a reference to the Agent type itself —
    /// which keeps PoFootball.Systems free of any ML-Agents dependency.
    /// </summary>
    public interface Systems_IPlayerHandle
    {
        int Id { get; }

        Systems_PlayerRole Role { get; }

        Systems_TeamSide Side { get; }

        Vector2 Position { get; }

        Vector2 Velocity { get; }

        /// <summary>
        /// Zeroes accumulated fatigue. Must be called BEFORE ResetTo restores the
        /// body, per CLAUDE.md section 2 — otherwise episode two starts pre-tired
        /// (acceptance criterion #11).
        /// </summary>
        void ClearFatigue();

        /// <summary>Teleports the body to its formation slot and zeroes velocity.</summary>
        void ResetTo(Vector2 position);

        /// <summary>
        /// Applies the sparse end-of-play reward. The agent owns the reward maths
        /// (it references PoFootball.Rewards); the director only says what happened.
        /// </summary>
        void ApplyTerminalReward(Systems_PlayOutcome outcome, float netYards);

        /// <summary>Ends this agent's ML-Agents episode. Repositioning is the director's job.</summary>
        void EndEpisodeNow();

        void SetCarrier(bool isCarrier);
    }
}
