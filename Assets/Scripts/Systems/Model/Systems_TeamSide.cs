namespace PoFootball.Models
{
    /// <summary>
    /// Maps directly onto the ML-Agents TeamId passed to BehaviorParameters.
    /// Offense attacks +Y; defense attacks -Y.
    /// </summary>
    public enum Systems_TeamSide
    {
        Offense = 0,
        Defense = 1
    }
}
