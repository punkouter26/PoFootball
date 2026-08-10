using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Fixed-capacity roster of every player in this environment, indexed by
    /// formation slot rather than by registration order — scene hierarchy order is
    /// not guaranteed, and a shuffled roster would silently scramble the formation.
    ///
    /// Backed by a pre-allocated array and scanned with plain for loops so no
    /// lookup allocates (acceptance criterion #17).
    /// </summary>
    public sealed class Systems_PlayerRegistry
    {
        public const int CAPACITY = Systems_RoleTable.SQUAD_SIZE * 2;

        private readonly Systems_IPlayerHandle[] _players = new Systems_IPlayerHandle[CAPACITY];
        private int _registeredCount;

        public int RegisteredCount => _registeredCount;

        public bool IsComplete => _registeredCount == CAPACITY;

        /// <summary>
        /// Places a player at its formation slot. Registering the same slot twice
        /// is a scene authoring error and is reported by IsComplete never reaching
        /// CAPACITY, which Systems_EpisodeDirector refuses to start on.
        /// </summary>
        public void Register(int slotIndex, Systems_IPlayerHandle handle)
        {
            if (slotIndex < 0 || slotIndex >= CAPACITY)
            {
                return;
            }

            if (_players[slotIndex] == null)
            {
                _registeredCount++;
            }

            _players[slotIndex] = handle;
        }

        public Systems_IPlayerHandle Get(int slotIndex)
        {
            return _players[slotIndex];
        }

        public Systems_IPlayerHandle FindById(int id)
        {
            for (int slotIndex = 0; slotIndex < CAPACITY; slotIndex++)
            {
                Systems_IPlayerHandle candidate = _players[slotIndex];
                if (candidate != null && candidate.Id == id)
                {
                    return candidate;
                }
            }

            return null;
        }
    }
}
