namespace PoFootball.Models
{
    /// <summary>
    /// The offensive formations this game lines up in.
    ///
    /// EVERY ONE OF THESE USES THE SAME ELEVEN ROLES IN THE SAME SLOT ORDER —
    /// five linemen, a tight end, two split receivers, the quarterback, a fullback
    /// and a halfback. That is not a stylistic choice, it is the contract: a slot
    /// index IS the index the agent carries in the scene, it selects the brain,
    /// the shape the presentation layer draws and the one-hot role slice of the
    /// observation vector. A formation may move a player; it may never change what
    /// that player is.
    ///
    /// So these are the real formations reachable with a 2WR-1TE-2RB personnel
    /// group, which is exactly what this squad is. The ones that are not reachable
    /// — four- and five-wide spread sets, empty backfield, six-man lines — would
    /// need a different personnel package, and a personnel package is a change to
    /// the squad rather than to the alignment.
    /// </summary>
    public enum Systems_OffensiveFormation
    {
        /// <summary>
        /// The classic I: quarterback under centre, fullback and halfback stacked
        /// directly behind him. Balanced, and the formation every other entry here
        /// is a deviation from. This is the alignment the game shipped with.
        /// </summary>
        ProI = 0,

        /// <summary>
        /// I-formation with the fullback offset to the tight end's side. Declares
        /// the strong side before the snap and puts a lead blocker in the gap the
        /// run is most likely to go through.
        /// </summary>
        StrongI = 1,

        /// <summary>
        /// I-formation with the fullback offset away from the tight end. The
        /// counter to Strong I: the extra blocker goes where the defense has one
        /// fewer man.
        /// </summary>
        WeakI = 2,

        /// <summary>
        /// Pro Set. Both backs beside each other rather than stacked, so either can
        /// take the handoff and neither tips which.
        /// </summary>
        SplitBacks = 3,

        /// <summary>
        /// Ace. One back behind the quarterback and the fullback flexed out wide as
        /// a third receiver — the personnel is unchanged, the alignment is a
        /// passing look.
        /// </summary>
        Singleback = 4,

        /// <summary>
        /// Shotgun. Quarterback six metres deep with the halfback beside him and the
        /// fullback flexed to the slot. Trades the play-action threat for time to
        /// throw and a clear view of the coverage.
        /// </summary>
        Shotgun = 5,

        /// <summary>
        /// Pistol. A short shotgun — quarterback four metres back with the halfback
        /// directly behind him, so the run game keeps its downhill start while the
        /// quarterback keeps the depth.
        /// </summary>
        Pistol = 6,

        /// <summary>
        /// Wing-T look: the fullback sets up as a wingback just outside and behind
        /// the tight end, giving that edge two extra blockers and a short motion
        /// threat.
        /// </summary>
        Wing = 7,
    }
}
