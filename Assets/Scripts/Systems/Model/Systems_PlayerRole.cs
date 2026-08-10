namespace PoFootball.Models
{
    /// <summary>
    /// Position group. Encoded as a one-hot slice of every agent's observation
    /// vector and as the sprite shape used by the presentation layer.
    ///
    /// RunningBack is the halfback — the deep back in the I-formation. Fullback was
    /// appended in milestone 2 rather than inserted, so the existing values keep
    /// their numbering and any serialized role data stays valid.
    /// </summary>
    public enum Systems_PlayerRole
    {
        Quarterback = 0,
        RunningBack = 1,
        WideReceiver = 2,
        TightEnd = 3,
        OffensiveLine = 4,
        DefensiveLine = 5,
        Linebacker = 6,
        Cornerback = 7,
        Safety = 8,
        Fullback = 9
    }
}
