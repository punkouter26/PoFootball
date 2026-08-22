using PoFootball.Models;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoFootball.Agents
{
    /// <summary>
    /// Loads <see cref="Agent_BrainTable"/> once per run and answers "which model
    /// does this brain group run".
    ///
    /// Static because twenty-two agents ask the same question during the same
    /// Awake pass and the answer cannot change while the game is running. This is
    /// not shared mutable state and not a service locator — it is a cache in front
    /// of Resources.Load with no setter, which is why it does not fall under the
    /// no-singletons rule in .claude/rules/architecture.md.
    ///
    /// It reports the state of the brain set exactly once per run rather than
    /// twenty-two times, because the thing it most needs to say — "these brains are
    /// stale, you are watching the heuristic" — is useless if it scrolls.
    /// </summary>
    internal static class Agent_BrainRegistry
    {
        private static Agent_BrainTable _table;
        private static bool _loaded;

        internal static ModelAsset ModelFor(Systems_BrainGroup group)
        {
            EnsureLoaded();

            return _table == null ? null : _table.ModelFor(group);
        }

        private static void EnsureLoaded()
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            _table = Resources.Load<Agent_BrainTable>(Agent_BrainTable.RESOURCE_PATH);

            Report();
        }

        private static void Report()
        {
            if (_table == null)
            {
                Debug.LogWarning(
                    "[PoFootball] No brain table at Resources/"
                    + $"{Agent_BrainTable.RESOURCE_PATH}. Every player is running the "
                    + "built-in heuristic. This is a playable game, not a trained one — "
                    + "promote brains with Tools/promote_brain.py to replace it.");
                return;
            }

            if (!_table.MatchesCurrentContract)
            {
                Debug.LogWarning(
                    $"[PoFootball] Brain table refused because {_table.RejectionReason}. "
                    + "Every brain in it is ignored and every player is running the "
                    + "heuristic. Re-promote against the current contract and rebuild "
                    + "the table with Tools > PoFootball > Build Brain Table, rather "
                    + "than editing the stamp by hand — the shapes really do not line "
                    + "up, and ML-Agents throws rather than degrading when they do "
                    + "not.");
                return;
            }

            Debug.Log(
                "[PoFootball] Brain table matches contract revision "
                + $"{Agent_ActionContract.CONTRACT_REVISION}.");
        }

        /// <summary>
        /// Cleared on a domain reload so a table edited between play sessions is
        /// picked up, and so entering play mode twice reports twice.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            _table = null;
            _loaded = false;
        }
    }
}
