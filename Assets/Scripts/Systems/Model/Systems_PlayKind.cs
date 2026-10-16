namespace PoFootball.Models
{
    /// <summary>
    /// What sort of snap this is. Public information, like the down: every player
    /// on a real field can see a punt formation, a kicking tee or a try from the
    /// two, so every player observes it (Sensor_FootballState), unlike the play
    /// CALL, which stays hidden from the defense.
    ///
    /// Revision 14 made the kicking game PLAYED rather than resolved at the rules
    /// layer. Through revision 13 a punt, a field goal, a kickoff and the try after
    /// a touchdown were dice rolls taken the instant the call latched, and nobody on
    /// the field ever chased a kick, returned one, or blocked one.
    ///
    /// <see cref="Punt"/> and <see cref="FieldGoal"/> are never handed out by a spot
    /// provider: a snap starts as <see cref="Scrimmage"/> and becomes one of them when
    /// the quarterback commits to the kick before the snap
    /// (Systems_PlayModel.LatchCall during the pre-snap). Every other kind is decided
    /// by whoever chooses the situation — the chains in a game, the seeded draw in
    /// training.
    /// </summary>
    public enum Systems_PlayKind
    {
        /// <summary>An ordinary down: run, pass, or — on fourth down — a kick called pre-snap.</summary>
        Scrimmage = 0,

        /// <summary>
        /// A free kick from the kicking team's own 35, or its own 20 after a safety.
        /// The kicking team is the offense for the play — the bodies that attack +Y —
        /// so the receiving team returns toward −Y and the frame mirror takes over at
        /// the whistle exactly as it does for any change of possession.
        /// </summary>
        Kickoff = 1,

        /// <summary>A punt formation: the quarterback kicks from fourteen yards deep.</summary>
        Punt = 2,

        /// <summary>A field-goal formation: the quarterback kicks from seven yards deep.</summary>
        FieldGoal = 3,

        /// <summary>The kicked try after a touchdown, from the fifteen. One point.</summary>
        ExtraPoint = 4,

        /// <summary>The scrimmage try after a touchdown, from the two. Two points.</summary>
        TwoPoint = 5,

        /// <summary>
        /// Victory formation. The quarterback takes the snap and goes down where he
        /// stands. A coach's decision about the clock, so game only — training has
        /// no clock for it to manage.
        /// </summary>
        KneelDown = 6,

        /// <summary>
        /// The quarterback throws the snap straight into the ground to stop the
        /// clock. Game only, for the reason <see cref="KneelDown"/> is.
        /// </summary>
        Spike = 7
    }

    public static class Systems_PlayKindExtensions
    {
        /// <summary>A snap whose ball leaves the formation off a foot rather than a hand.</summary>
        public static bool IsKick(this Systems_PlayKind kind)
        {
            return kind == Systems_PlayKind.Kickoff
                || kind == Systems_PlayKind.Punt
                || kind == Systems_PlayKind.FieldGoal
                || kind == Systems_PlayKind.ExtraPoint;
        }

        /// <summary>The kicks that score by going through the uprights.</summary>
        public static bool IsPlaceKick(this Systems_PlayKind kind)
        {
            return kind == Systems_PlayKind.FieldGoal || kind == Systems_PlayKind.ExtraPoint;
        }

        /// <summary>The untimed downs after a touchdown.</summary>
        public static bool IsTry(this Systems_PlayKind kind)
        {
            return kind == Systems_PlayKind.ExtraPoint || kind == Systems_PlayKind.TwoPoint;
        }
    }
}
