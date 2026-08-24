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
    ///
    /// MODE. The same scene graph serves training and a played game; what differs
    /// is what is in the container. In <see cref="Systems_SimMode.Training"/> the
    /// scoreboard, the chains and the stats are simply never
    /// registered, and the line of scrimmage comes from the same seeded RNG it
    /// always did. Nothing a policy observes changes between the two, which is the
    /// point — a brain trained in one is being evaluated on identical dynamics in
    /// the other.
    /// </summary>
    [DefaultExecutionOrder(-5000)]
    public sealed class Systems_GameLifetimeScope : LifetimeScope
    {
        [Tooltip(
            "Seed for the line-of-scrimmage RNG. Used verbatim in Training. In "
            + "Game it is only the fallback for when Vary Seed Per Game is off.")]
        [SerializeField] private uint _episodeSeed = 1u;

        [Tooltip(
            "Game mode only: draw a fresh seed each session so no two playthroughs "
            + "are identical. Always ignored in Training, where a reproducible spot "
            + "sequence is the point.")]
        [SerializeField] private bool _varySeedPerGame = true;

        [Tooltip(
            "Training: endless random-spot plays for mlagents-learn. "
            + "Game: downs, clock and a scoreboard.")]
        [SerializeField] private Systems_SimMode _simMode = Systems_SimMode.Training;

        [Tooltip(
            "Stadium lights, shadows, post-processing, particles, the broadcast "
            + "camera and the crowd. Off in Training and headless regardless of "
            + "this setting; clear it to profile the simulation on its own.")]
        [SerializeField] private bool _presentationEffects = true;

        protected override void Configure(IContainerBuilder builder)
        {
            Systems_EpisodeSeed.Set(ResolveSeed());

            // Registered in both modes. Every presentation view injects it and
            // switches itself off when it says no, which is why there is exactly
            // one place that decides whether a graphics pass is allowed to cost
            // a training run anything.
            builder.RegisterInstance(
                new Systems_PresentationBudget(_simMode, _presentationEffects));

            // The mode itself, for the one consumer that needs the distinction and
            // cannot get it from the presentation budget: Agent_FootballPlayer turns
            // the ML-Agents trainer link off in Game mode, and "am I decoration" is
            // not the question it is asking. Registering the enum rather than
            // widening Systems_PresentationBudget keeps each of them answering
            // exactly one thing (.claude/rules/architecture.md).
            builder.RegisterInstance(_simMode);

            builder.Register<Systems_PlayModel>(Lifetime.Singleton);
            builder.Register<Systems_BallModel>(Lifetime.Singleton);
            builder.Register<Systems_FieldModel>(Lifetime.Singleton);
            builder.Register<Systems_PlayerRegistry>(Lifetime.Singleton);
            builder.Register<Systems_BallSystem>(Lifetime.Singleton);

            MessagePipeOptions messagePipeOptions = builder.RegisterMessagePipe();
            builder.RegisterMessageBroker<Systems_PlaySnappedMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_PlayEndedMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_TackleMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_ScoreMessage>(messagePipeOptions);

            // Registered in both modes so a view can subscribe without caring which
            // one it is in. In training nothing ever publishes them.
            builder.RegisterMessageBroker<Systems_DownResolvedMessage>(messagePipeOptions);
            builder.RegisterMessageBroker<Systems_GameOverMessage>(messagePipeOptions);

            if (_simMode == Systems_SimMode.Game)
            {
                ConfigureGameLayer(builder);

                // A played game has no episodes and no rewards to assign. The
                // director calls the boundary either way; here it does nothing.
                builder.Register<
                    Systems_ITrainingEpisodeBoundary, Systems_NullEpisodeBoundary>(
                    Lifetime.Singleton);

                // Kicks that are worth watching: a distance-weighted field goal and
                // a punt that varies. Game only — see Systems_IKickModel.
                builder.Register<Systems_IKickModel, Systems_ProbabilisticKickModel>(
                    Lifetime.Singleton);

                // A strip that is a chance rather than a certainty, for the same
                // reason — see Systems_IFumbleModel.
                builder.Register<Systems_IFumbleModel, Systems_ProbabilisticFumbleModel>(
                    Lifetime.Singleton);
            }
            else
            {
                builder.Register<Systems_ISpotProvider, Systems_RandomSpotProvider>(
                    Lifetime.Singleton);

                builder.Register<
                    Systems_ITrainingEpisodeBoundary, Systems_TrainingEpisodeBoundary>(
                    Lifetime.Singleton);

                // The pure function every brain was fitted against: good inside 55,
                // 40-yard punts, no draw taken. Training dynamics are unchanged.
                builder.Register<Systems_IKickModel, Systems_DeterministicKickModel>(
                    Lifetime.Singleton);

                // A fumble trigger that is a pure function of the hit, so a policy
                // can learn to avoid it rather than being taxed at random.
                builder.Register<Systems_IFumbleModel, Systems_DeterministicFumbleModel>(
                    Lifetime.Singleton);
            }

            // Registration order is execution order for IStartable and
            // IFixedTickable. The director must tick first so a deferred reset is
            // consumed before the referee evaluates the new play. The game layer is
            // registered above it, so the scoreboard has kicked off before the
            // director asks it where the first snap goes.
            builder.RegisterEntryPoint<Systems_EpisodeDirector>().AsSelf();
            builder.RegisterEntryPoint<Systems_Referee>().AsSelf();

            builder.RegisterBuildCallback(InjectSceneBehaviours);
        }

        private static void ConfigureGameLayer(IContainerBuilder builder)
        {
            builder.Register<Systems_GameModel>(Lifetime.Singleton);
            builder.Register<Systems_BoxScore>(Lifetime.Singleton);

            // ONE registration carrying three roles: the entry point that starts
            // the game and ticks the clock, the spot provider the director pulls
            // from, and itself. It has to be a single chained registration — a
            // separate Register plus RegisterEntryPoint would build two instances,
            // and the director would then be reading a scoreboard that no play ever
            // reached, silently, with a plausible-looking 1st & 10 forever.
            builder.RegisterEntryPoint<Systems_GameFlowSystem>()
                .As<Systems_ISpotProvider>()
                .AsSelf();

            builder.RegisterEntryPoint<Systems_StatsSystem>();
        }

        /// <summary>
        /// Injects every scene MonoBehaviour this assembly is not allowed to name:
        /// the agents, the views, and the training telemetry sink. One scan, two
        /// marker interfaces — Systems_IInjectableView derives from
        /// Systems_IInjectableBehaviour, so views are covered by the second test.
        /// </summary>
        /// <summary>
        /// The seed the line-of-scrimmage RNG starts from.
        ///
        /// TRAINING TAKES THE SERIALIZED VALUE, ALWAYS. A run has to be able to
        /// replay its own spot sequence call for call — UNITY_RULES section 2 asks
        /// for deterministic execution, and two runs of the same config that saw
        /// different field positions are not comparable.
        ///
        /// A PLAYED GAME DOES NOT, AND THAT WAS A REAL BUG. Everything downstream
        /// of this seed is deterministic: fixed delta time, seeded spots, and a
        /// heuristic with no randomness in it. With the seed pinned at 1 as well,
        /// every session of SCN_GAME played out identically — two runs produced the
        /// same play tally to the digit and the same score. A game that cannot
        /// surprise anyone twice is not much of a game.
        ///
        /// Time-derived rather than System.Random so there is no second RNG to seed;
        /// zero is folded away because Systems_EpisodeSeed rejects it.
        /// </summary>
        private uint ResolveSeed()
        {
            if (_simMode != Systems_SimMode.Game || !_varySeedPerGame)
            {
                return _episodeSeed;
            }

            return (uint)System.DateTime.Now.Ticks;
        }

        private void InjectSceneBehaviours(IObjectResolver container)
        {
            MonoBehaviour[] behaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include);

            int injected = 0;

            for (int index = 0; index < behaviours.Length; index++)
            {
                if (behaviours[index] is Systems_IPlayerHandle
                    || behaviours[index] is Systems_IInjectableBehaviour)
                {
                    container.Inject(behaviours[index]);
                    injected++;
                }
            }

            Debug.Log($"[PoFootball] LifetimeScope injected {injected} scene behaviours.");
        }
    }
}
