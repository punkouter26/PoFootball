namespace PoFootball.Models
{
    /// <summary>
    /// Lifecycle of a single play. Advances PreSnap -> Live -> Dead exactly once
    /// per episode (acceptance criterion #1).
    /// </summary>
    public enum Systems_PlayPhase
    {
        PreSnap = 0,
        Live = 1,
        Dead = 2
    }
}
