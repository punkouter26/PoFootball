namespace PoFootball.Models
{
    /// <summary>
    /// Which policy a player is driven by. Six groups, so the trainer config
    /// carries six behaviors each with its own self_play block and its own
    /// Self-play/ELO curve (acceptance criterion #22).
    ///
    /// The string names must match the behavior keys in Config/FootballBase04.yaml
    /// exactly, or the handshake succeeds but the agent never receives actions.
    /// Systems_RoleTableTests asserts that mapping stays total and stable.
    ///
    /// WHY SIX AND NOT FOUR
    /// --------------------
    /// Through football_base03 the quarterback shared OffenseSkill with the backs,
    /// the receivers and the tight end. Only one of those five role types ever read
    /// the play-call branch, the throw trigger or the aim vector, so:
    ///
    ///   - the gradient carrying "which play should I call" was diluted roughly
    ///     five to one by players whose call output is never read;
    ///   - four role types were trained on four continuous and two discrete
    ///     outputs that do nothing, which is capacity spent learning to be ignored;
    ///   - the entropy bonus was spread over six action heads at once, which is
    ///     how Policy/Entropy stayed near 3.5 while the play-call branch collapsed
    ///     to a single option 93% of the time.
    ///
    /// The quarterback is now alone on its own brain with the only non-trivial
    /// action space in the game. The defensive cover brain split for the same
    /// reason at lower stakes: a linebacker filling a gap and a safety playing
    /// centre field want opposite things from the same observation.
    /// </summary>
    public enum Systems_BrainGroup
    {
        OffenseLine = 0,
        OffenseSkill = 1,
        DefenseLine = 2,

        /// <summary>Linebackers. Was folded into DefenseCover before base04.</summary>
        DefenseBox = 3,

        /// <summary>Cornerbacks and safeties. Was folded into DefenseCover before base04.</summary>
        DefenseSecondary = 4,

        /// <summary>The quarterback, alone. The only brain with discrete actions.</summary>
        Quarterback = 5
    }
}
