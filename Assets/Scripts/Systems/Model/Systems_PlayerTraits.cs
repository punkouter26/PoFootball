namespace PoFootball.Models
{
    /// <summary>
    /// What makes one athlete differ from another at the same position. Each value
    /// is a deviation in [-1, 1] from the role's own number in
    /// <see cref="Systems_RoleTable"/>, so default(Systems_PlayerTraits) is exactly
    /// the role-average player every brain through revision 13 was fitted against.
    ///
    /// WHY (revision 14). Systems_RoleTable is keyed on role alone, so both
    /// receivers, all five linemen and both corners were the same body eleven
    /// times over, and no matchup on the field was ever better or worse than
    /// another.
    ///
    /// The spreads are small on purpose: they separate athletes within a position
    /// without touching the depth chart between positions, which Systems_RoleTable
    /// measured its way to (the safety is faster than the back, the linebacker is
    /// not). The fastest lineman is still slower than the slowest fullback.
    ///
    /// Every player observes its own four values (Sensor_FootballState), so a
    /// policy is told which body it is driving rather than left to infer it.
    /// </summary>
    public readonly struct Systems_PlayerTraits
    {
        public Systems_PlayerTraits(float speed, float strength, float agility, float discipline)
        {
            Speed = speed;
            Strength = strength;
            Agility = agility;
            Discipline = discipline;
        }

        /// <summary>Top speed. Scales the drive force with it, so acceleration too.</summary>
        public float Speed { get; }

        /// <summary>Mass: what a body brings into a collision and takes out of one.</summary>
        public float Strength { get; }

        /// <summary>Turn rate.</summary>
        public float Agility { get; }

        /// <summary>
        /// How rarely this player jumps the snap. Read only by the game layer's
        /// pre-snap fouls (Systems_Roster.DrawPreSnapFoul); nothing in the dynamics
        /// depends on it.
        /// </summary>
        public float Discipline { get; }

        public float SpeedScale => 1f + (Speed * Systems_SimConstants.TRAIT_SPEED_SPREAD);

        public float MassScale => 1f + (Strength * Systems_SimConstants.TRAIT_MASS_SPREAD);

        public float TurnScale => 1f + (Agility * Systems_SimConstants.TRAIT_TURN_SPREAD);
    }
}
