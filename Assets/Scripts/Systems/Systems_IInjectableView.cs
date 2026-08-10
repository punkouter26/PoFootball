namespace PoFootball.Systems
{
    /// <summary>
    /// Marker for presentation MonoBehaviours that need container injection.
    ///
    /// Systems_GameLifetimeScope lives in this assembly and cannot reference
    /// PoFootball.Views without inverting the dependency direction. Views implement
    /// this interface — which Systems owns — so the scope's build callback can find
    /// and inject them without ever naming a view type. Same trick as
    /// Systems_IPlayerHandle uses for the agents.
    /// </summary>
    public interface Systems_IInjectableView
    {
    }
}
