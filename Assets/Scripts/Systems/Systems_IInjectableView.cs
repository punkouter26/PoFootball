namespace PoFootball.Systems
{
    /// <summary>
    /// Marker for presentation MonoBehaviours that need container injection.
    ///
    /// Narrows <see cref="Systems_IInjectableBehaviour"/> to the presentation
    /// layer. It carries no members of its own — the distinction is documentary,
    /// so that a reader of a view can see it is a view, and so that a future rule
    /// about what views may inject has something to attach to.
    /// </summary>
    public interface Systems_IInjectableView : Systems_IInjectableBehaviour
    {
    }
}
