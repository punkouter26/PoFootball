using PoFootball.Systems;
using Unity.MLAgents;
using UnityEngine;
using VContainer;

namespace PoFootball.Agents
{
    /// <summary>
    /// Hands the trainer's curriculum lesson to the training spot draw.
    ///
    /// mlagents-learn advances `spot_lesson` through the environment_parameters
    /// block of Config/FootballBase12.yaml; Systems_RandomSpotProvider narrows its
    /// draws to match. This component is the only thing between the two, because
    /// PoFootball.Systems must not reference ML-Agents (CLAUDE.md) — it installs a
    /// lesson source the provider owns the interface for, exactly as Agent_TeamGroups
    /// installs the team reward sink.
    ///
    /// Sits on /Systems in SCN_TRAIN_FOOTBALL only. In a played game the spot
    /// provider is Systems_GameFlowSystem, the cast below fails, and this disables
    /// itself.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class Agent_Curriculum : MonoBehaviour, Systems_IInjectableBehaviour
    {
        /// <summary>Must match the key under environment_parameters in the config.</summary>
        public const string LESSON_PARAMETER = "spot_lesson";

        private Systems_ISpotProvider _spotProvider;

        [Inject]
        public void Construct(Systems_ISpotProvider spotProvider)
        {
            _spotProvider = spotProvider;
        }

        private void Start()
        {
            if (!(_spotProvider is Systems_RandomSpotProvider randomSpots))
            {
                enabled = false;
                return;
            }

            randomSpots.SetLessonSource(new Source());
        }

        /// <summary>
        /// A plain object rather than this MonoBehaviour, so the provider never holds
        /// a Unity fake-null after a scene unload (the Agent_TeamGroups.Sink reason).
        ///
        /// Read per episode rather than cached: the trainer moves the lesson whenever
        /// a completion criterion is met, and nothing announces it on the C# side.
        /// Without a trainer — the scene played in the Editor — the default is the
        /// full distribution, so nothing changes for anyone not running a curriculum.
        /// </summary>
        private sealed class Source : Systems_ISpotLessonSource
        {
            public int CurrentLesson
            {
                get
                {
                    if (!Academy.IsInitialized)
                    {
                        return Systems_RandomSpotProvider.LESSON_FULL;
                    }

                    float lesson = Academy.Instance.EnvironmentParameters.GetWithDefault(
                        LESSON_PARAMETER, Systems_RandomSpotProvider.LESSON_FULL);

                    return Mathf.RoundToInt(lesson);
                }
            }
        }
    }
}
