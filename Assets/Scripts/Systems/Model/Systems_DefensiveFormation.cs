namespace PoFootball.Models
{
    /// <summary>
    /// The defensive fronts and coverages this game lines up in.
    ///
    /// Same contract as <see cref="Systems_OffensiveFormation"/>: eleven roles —
    /// four linemen, three linebackers, two corners, two safeties — in a fixed slot
    /// order, every time. A formation moves those eleven; it never swaps one for
    /// another. Real nickel and dime packages substitute personnel, so what is
    /// modelled here as <see cref="Nickel"/> is the alignment a 4-3 walks a
    /// linebacker out to when it cannot substitute.
    ///
    /// EACH ENTRY CARRIES ITS OWN COVERAGE, NOT JUST ITS OWN GEOMETRY. A front and
    /// a coverage that disagree is not a formation, it is a busted assignment: a
    /// two-deep shell with a corner in man across the field is eleven players
    /// executing two different defenses. So the man-coverage table lives beside the
    /// alignment in <see cref="PoFootball.Systems.Systems_FormationBook"/> and the
    /// two are chosen together.
    /// </summary>
    public enum Systems_DefensiveFormation
    {
        /// <summary>
        /// Base 4-3 with two-high safeties, playing Cover 1: corners man-up on the
        /// split receivers, strong safety on the tight end, free safety over the
        /// top. The alignment the game shipped with.
        /// </summary>
        FourThreeBase = 0,

        /// <summary>
        /// 4-3 Over. The line shifts toward the tight end, so the strength of the
        /// front matches the strength of the offensive formation.
        /// </summary>
        FourThreeOver = 1,

        /// <summary>
        /// 4-3 Under. The line shifts away from the tight end, loading the weak side
        /// and daring the offense to run into the tight end's shoulder.
        /// </summary>
        FourThreeUnder = 2,

        /// <summary>
        /// Cover 2. Corners press at the line and sink to the flats, both safeties
        /// split the deep half of the field. Pure zone — nobody is in man, which is
        /// what makes the seam between the two halves the place to attack it.
        /// </summary>
        Cover2 = 3,

        /// <summary>
        /// Cover 3. One safety takes the deep middle, the corners take the deep
        /// thirds, and the other safety rolls down into the box — eight in the box
        /// against the run with three deep behind it.
        /// </summary>
        Cover3 = 4,

        /// <summary>
        /// Nickel look. A linebacker walks out over the slot rather than being
        /// substituted for, which is what a 4-3 does when it has to cover a third
        /// receiver with the personnel already on the field.
        /// </summary>
        Nickel = 5,

        /// <summary>
        /// The 46. Eight men crowding the line, one deep safety behind them. Built
        /// to stop the run and to make the quarterback throw before he wants to;
        /// vulnerable over the top by construction.
        /// </summary>
        Bear46 = 6,

        /// <summary>
        /// Cover 0 blitz. All three linebackers on the line, corners pressed in man
        /// with no help, one safety deep and the other on the tight end. Everything
        /// is a race between the rush and the throw.
        /// </summary>
        ZeroBlitz = 7,
    }
}
