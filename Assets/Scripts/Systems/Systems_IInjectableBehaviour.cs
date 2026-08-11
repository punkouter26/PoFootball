namespace PoFootball.Systems
{
    /// <summary>
    /// Marker for any MonoBehaviour outside this assembly that needs container
    /// injection.
    ///
    /// Systems_GameLifetimeScope lives in this assembly and cannot reference
    /// PoFootball.Views or PoFootball.Agents without inverting the dependency
    /// direction. Those behaviours implement this interface — which Systems owns —
    /// so the scope's build callback can find and inject them without ever naming
    /// a type it is not allowed to see.
    ///
    /// <see cref="Systems_IInjectableView"/> narrows this to presentation, which is
    /// the common case; implement this one directly for a behaviour that is not a
    /// view, such as the training telemetry sink in PoFootball.Agents.
    /// </summary>
    public interface Systems_IInjectableBehaviour
    {
    }
}
