using PoFootball.Models;
using UnityEngine;

namespace PoFootball.Systems
{
    /// <summary>Published once per episode, the moment the whistle blows.</summary>
    public readonly struct Systems_PlayEndedMessage
    {
        public readonly Systems_PlayOutcome Outcome;
        public readonly Systems_PlayCall Call;
        public readonly Vector2 Spot;
        public readonly float NetYards;
        public readonly int PhysicsTicks;

        /// <summary>Whether a receiver caught a pass at any point on this play.</summary>
        public readonly bool PassCompleted;

        public Systems_PlayEndedMessage(
            Systems_PlayOutcome outcome,
            Systems_PlayCall call,
            Vector2 spot,
            float netYards,
            int physicsTicks,
            bool passCompleted)
        {
            Outcome = outcome;
            Call = call;
            Spot = spot;
            NetYards = netYards;
            PhysicsTicks = physicsTicks;
            PassCompleted = passCompleted;
        }
    }
}
