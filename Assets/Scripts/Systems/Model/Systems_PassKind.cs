namespace PoFootball.Models
{
    /// <summary>
    /// Which way a thrown ball is legally travelling, which decides what happens if
    /// nobody catches it. A forward pass that hits the ground is incomplete and the
    /// play is dead. A backward pass — a lateral, a pitch — that hits the ground is
    /// a live ball, exactly like a fumble, and either side may fall on it.
    /// </summary>
    public enum Systems_PassKind
    {
        Forward = 0,
        Lateral = 1
    }
}
