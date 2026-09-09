using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Where a player reports the decision it just applied.
    ///
    /// THE POINT OF THE SEAM. Drawing what a policy decided means getting the
    /// action vector out of PoFootball.Agents and into PoFootball.Views, and those
    /// two assemblies cannot see each other — Views has no ML-Agents reference and
    /// must never gain one (CLAUDE.md). Both of them already reference
    /// PoFootball.Systems, so the intent travels through an interface this
    /// assembly owns, exactly the way Systems_ITrainingHandle carries rewards the
    /// other way. Nothing in the simulation reads what is written here; it is a
    /// one-way tap for presentation.
    /// </summary>
    public interface Systems_IIntentSink
    {
        /// <summary>
        /// Called once per decision step per player, from
        /// Agent_FootballPlayer.OnActionReceived, on the hottest path in the
        /// project. Implementations must not allocate and must not branch on
        /// anything expensive.
        /// </summary>
        void Write(int playerId, in Systems_PlayerIntent intent);
    }

    /// <summary>
    /// The read side, for the one view that draws it.
    ///
    /// Separate from the sink so a reader cannot write and a writer cannot read,
    /// rather than for any technical reason — the same object satisfies both.
    /// </summary>
    public interface Systems_IIntentSource
    {
        /// <summary>
        /// False when intent is not being collected at all, which is every
        /// training run and every headless environment. A view must switch itself
        /// off on this rather than draw twenty-two arrows of stale zero.
        /// </summary>
        bool Available { get; }

        Systems_PlayerIntent Read(int playerId);
    }

    /// <summary>
    /// The presentation binding: a fixed array of the last decision every player
    /// made.
    ///
    /// Indexed by Systems_IPlayerHandle.Id, which Agent_FootballPlayer defines as
    /// its formation slot — the same index Systems_PlayerRegistry uses, so a
    /// caller that has one has the other and no lookup is needed.
    ///
    /// NOT A ReactiveProperty, for the reason Systems_PlayModel gives at length:
    /// this is written twenty-two times per decision step and a subscription
    /// callback per change would allocate. The reader polls in LateUpdate, which
    /// is where it wants the value anyway.
    /// </summary>
    public sealed class Systems_IntentModel : Systems_IIntentSink, Systems_IIntentSource
    {
        private readonly Systems_PlayerIntent[] _intents =
            new Systems_PlayerIntent[Systems_PlayerRegistry.CAPACITY];

        public bool Available => true;

        public void Write(int playerId, in Systems_PlayerIntent intent)
        {
            if (playerId < 0 || playerId >= Systems_PlayerRegistry.CAPACITY)
            {
                return;
            }

            _intents[playerId] = intent;
        }

        public Systems_PlayerIntent Read(int playerId)
        {
            if (playerId < 0 || playerId >= Systems_PlayerRegistry.CAPACITY)
            {
                return default;
            }

            return _intents[playerId];
        }
    }

    /// <summary>
    /// The training and headless binding: throw it away.
    ///
    /// A null object rather than a null check in the agent, for the same reason
    /// Systems_NullEpisodeBoundary is one — there is then exactly one shape of
    /// control flow in OnActionReceived and no branch that only ever runs in one
    /// mode. The cost is a non-inlined interface call per player per decision,
    /// which at twenty-two players and a decision period of five is roughly
    /// twenty-two thousand empty calls a second per environment at time_scale 20.
    /// That is microseconds; building the struct is the larger half and the
    /// compiler cannot elide it, which is the honest price of not putting an
    /// `if (presentation)` inside the simulation.
    /// </summary>
    public sealed class Systems_NullIntentSink : Systems_IIntentSink, Systems_IIntentSource
    {
        public bool Available => false;

        public void Write(int playerId, in Systems_PlayerIntent intent)
        {
        }

        public Systems_PlayerIntent Read(int playerId)
        {
            return default;
        }
    }
}
