using PoFootball.Models;
using Unity.InferenceEngine;
using UnityEngine;

namespace PoFootball.Agents
{
    /// <summary>
    /// Which `.onnx` each brain group runs, and which contract revision those files
    /// were fitted against.
    ///
    /// WHY THIS EXISTS. `BehaviorName` and the action spec are already assigned from
    /// code in Agent_FootballPlayer.ConfigureBrain, so the scene cannot drift from
    /// Systems_RoleTable or Agent_ActionContract. The model was the exception: it
    /// was dragged onto twenty-two BehaviorParameters by hand and serialized into
    /// two scenes, with nothing checking it against anything.
    ///
    /// That is how the project ended up shipping a scene in which the quarterback —
    /// four continuous actions plus two discrete branches — was bound to a
    /// two-continuous-action brain from football_base02. ML-Agents does not degrade
    /// there. It throws `ArgumentException: length` out of ActuatorManager on every
    /// agent on every physics tick, no action is ever applied, and the game renders
    /// twenty-two shapes standing perfectly still while the console takes a
    /// thousand entries a second.
    ///
    /// THE REVISION STAMP IS THE WHOLE POINT. An `.onnx` outlives the code it was
    /// trained against and the runtime loads a mismatched one without complaint
    /// (CLAUDE.md section 4). <see cref="Agent_ActionContract.CONTRACT_REVISION"/>
    /// is bumped whenever the observation or action shape changes; a table stamped
    /// with anything else is refused wholesale rather than per-brain, because a
    /// half-current set of brains is not a thing anybody wants to debug.
    ///
    /// A refused or absent table is NOT fatal: the model is left null, ML-Agents
    /// falls back to Agent_FootballPlayer.Heuristic, and the game is playable
    /// without any trained brain at all. Training is unaffected either way — with a
    /// trainer attached the policy is remote and the assigned model is ignored.
    ///
    /// Populated by `Tools/promote_brain.py`, which already refuses to promote a
    /// brain whose shapes do not match. Do not fill it in by hand.
    /// </summary>
    [CreateAssetMenu(menuName = "PoFootball/Brain Table", fileName = "PoFootballBrains")]
    public sealed class Agent_BrainTable : ScriptableObject
    {
        /// <summary>Resources path. Loaded once per run, from the first agent to wake.</summary>
        public const string RESOURCE_PATH = "PoFootballBrains";

        [System.Serializable]
        private struct Entry
        {
            [SerializeField] private Systems_BrainGroup _group;
            [SerializeField] private ModelAsset _model;

            public Systems_BrainGroup Group => _group;

            public ModelAsset Model => _model;
        }

        [Tooltip(
            "Agent_ActionContract.CONTRACT_REVISION these brains were trained "
            + "against. Anything else and the whole table is ignored.")]
        [SerializeField] private int _contractRevision;

        [SerializeField] private Entry[] _entries = new Entry[0];

        public int ContractRevision => _contractRevision;

        public bool MatchesCurrentContract =>
            _contractRevision == Agent_ActionContract.CONTRACT_REVISION;

        /// <summary>
        /// The model for a brain group, or null when the table is stale or has no
        /// entry for it. Null is a supported answer — it means "run the heuristic".
        /// </summary>
        public ModelAsset ModelFor(Systems_BrainGroup group)
        {
            if (!MatchesCurrentContract || _entries == null)
            {
                return null;
            }

            for (int index = 0; index < _entries.Length; index++)
            {
                if (_entries[index].Group == group)
                {
                    return _entries[index].Model;
                }
            }

            return null;
        }
    }
}
