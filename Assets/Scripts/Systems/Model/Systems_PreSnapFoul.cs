namespace PoFootball.Models
{
    /// <summary>
    /// A flag thrown before the ball is snapped. Five yards and the down is
    /// replayed; no play is run. See Systems_GameFlowSystem.NextSituation.
    /// </summary>
    public enum Systems_PreSnapFoul
    {
        None = 0,

        /// <summary>On the offense: five yards back.</summary>
        FalseStart = 1,

        /// <summary>On the defense: five yards forward, and a first down if that reaches the line to gain.</summary>
        Offside = 2
    }
}
