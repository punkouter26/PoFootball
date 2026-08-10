using MessagePipe;
using PoFootball.Models;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Composition root. The only place binding and resolution happen — there is no
    /// GameContext and no service locator (.claude/rules/architecture.md).
    ///
    /// Solves the ML-Agents DI seam: the 22 Agent_FootballPlayer components are
    /// instantiated by Unity from the scene, so VContainer cannot construct them.
    /// A build callback injects every MonoBehaviour that implements
    /// Systems_IPlayerHandle — an interface this assembly owns — which lets the
    /// scope inject agents without PoFootball.Systems ever referencing
    /// PoFootball.Agents. The dependency direction stays intact.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class Systems_GameLifetimeScope : LifetimeScope
    {
        [SerializeField] private uint _episodeSeed = 1u;

        protected override void Configure(IContainerBuilder builder)
        {
            Systems_EpisodeSeed.Set(_episodeSeed);

            builder.Register<Systems_PlayModel>(Lifetime.Singleton);
            builder.Register<Systems_BallModel>(Lifetime.Singleton);
            builder.Register<Systems_FieldModel>(Lifetime.Singleton);
            builder.Register<Systems_PlayerRegistry>(Lifetime.Singleton);
            builder.Register<Systems_BallSystem>(Lifetime.Singleton);

            // Registration order is execution order for IFixedTickable. The
            // director must run first so a deferred reset is consumed before the
            // referee evaluates the new play.
            builder.RegisterEntryPoint<Systems_EpisodeDirector>().AsSelf();
            builder.RegisterEntryPoint<Systems_Referee>().AsSelf();
            builder.RegisterEntryPoint<Systems_Telemetry>();

            MessagePipeOptions messagePipeOptions = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<Systems_PlaySnappedMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_PlayEndedMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_TackleMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_ScoreMessage>(messagePipeOptions);

            builder.RegisterBuildCallback(InjectPlayerHandles);
        }

        private void InjectPlayerHandles(IObjectResolver container)
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include);

            int injected = 0;

            for (int index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is Systems_IPlayerHandle
                    || behaviours[index] is Systems_IInjectableView)
                {
                    container.Inject(behaviours[index]);
                    injected++;
                }
            }

            Debug.Log($"[PoFootball] LifetimeScope injected {injected} player handles.");
        }
    }
}
