namespace PoFootball.Models
{
    /// <summary>
    /// Which policy a player is driven by. Four groups, so the trainer config
    /// carries four behaviors each with its own self_play block and its own
    /// Self-play/ELO curve (acceptance criterion #22).
    /// The string names must match the behavior keys in Config/FootballBase01.yaml
    /// exactly, or the handshake succeeds but the agent never receives actions.
    /// </summary>
    public enum Systems_BrainGroup
    {
        OffenseLine = 0,
        OffenseSkill = 1,
        DefenseLine = 2,
        DefenseCover = 3
    }
}
