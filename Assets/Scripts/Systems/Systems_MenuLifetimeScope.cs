using PoFootball.Models;
using UnityEngine;
using VContainer;
using VContainer.Unity;

namespace PoFootball.Systems
{
    /// <summary>
    /// Composition root for the front-end scene. Far smaller than
    /// <see cref="Systems_GameLifetimeScope"/> — the menu has no simulation, so all
    /// it needs is the presentation budget and a way to inject it into the screen.
    ///
    /// A separate scope rather than a flag on the game scope, because the two
    /// scenes genuinely have nothing in common: the menu has no players to
    /// register, no referee, and no MessagePipe traffic.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class Systems_MenuLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            // The menu is always "a game being watched" as far as presentation is
            // concerned — mlagents-learn never loads this scene — so the only
            // thing the budget filters out here is a headless run, which is
            // exactly what it should do: a batch-mode build has no audio device
            // to play the menu bed through.
            builder.RegisterInstance(
                new Systems_PresentationBudget(Systems_SimMode.Game, true));

            builder.RegisterBuildCallback(InjectViews);
        }

        private void InjectViews(IObjectResolver container)
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include);

            for (int index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is Systems_IInjectableView)
                {
                    container.Inject(behaviours[index]);
                }
            }
        }
    }
}
