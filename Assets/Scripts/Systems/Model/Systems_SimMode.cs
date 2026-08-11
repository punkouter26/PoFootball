namespace PoFootball.Models
{
    /// <summary>
    /// Which job this environment is doing. Set once in the scene on the
    /// composition root and never changed at runtime.
    ///
    /// This exists to keep one promise: adding a game layer must not perturb
    /// training. In <see cref="Training"/> the line of scrimmage is drawn from the
    /// same seeded RNG, in the same call order, as it was before the game layer
    /// existed — so an .onnx trained yesterday sees an identical distribution of
    /// starting states today. The scoreboard, the clock and the chains are simply
    /// not in the container.
    /// </summary>
    public enum Systems_SimMode
    {
        /// <summary>
        /// Endless independent plays from a randomised line of scrimmage. What
        /// mlagents-learn drives. No score, no clock, no downs.
        /// </summary>
        Training = 0,

        /// <summary>
        /// Plays chained into drives, drives into a scored game against a clock.
        /// What a human watches.
        /// </summary>
        Game = 1
    }
}
