namespace PoFootball.Models
{
    /// <summary>
    /// Which policy a player runs. One entry per ML-Agents behavior name.
    ///
    /// WENT FROM SIX TO THREE FOR MA-POCA, AND THE REASON IS A HARD CONSTRAINT
    /// RATHER THAN A PREFERENCE. A SimpleMultiAgentGroup cannot span behavior
    /// names: mlagents' TrainerController builds one AgentManager per
    /// name_behavior_id and the groupmate dictionaries are instance fields on it,
    /// so agents registered into one group from two behaviors never see each other
    /// as team-mates. C# accepts the registration without complaint — the group is
    /// a bare HashSet&lt;Agent&gt; — and you get a "centralized" critic centralized
    /// over a fraction of the side, silently.
    ///
    /// So OffenseLine and OffenseSkill merged into <see cref="Offense"/>, and
    /// DefenseLine, DefenseBox and DefenseSecondary merged into
    /// <see cref="Defense"/>. All of them already shared an action space (two
    /// continuous outputs) and an observation vector, and the player's role is a
    /// one-hot inside that vector, so a merged policy can still tell a guard from a
    /// receiver.
    ///
    /// The quarterback stayed separate, which is the whole point of keeping three
    /// rather than two. It is the only behavior with discrete actions, and it was
    /// split out of OffenseSkill in the first place because sharing diluted its
    /// play-call gradient five to one and pinned its entropy bonus to whatever
    /// suited four other players' steering. Merging it back to satisfy POCA would
    /// have re-created the exact bug the split fixed, and would have forced ten
    /// linemen and receivers to carry a discrete action space they never use.
    /// </summary>
    public enum Systems_BrainGroup
    {
        Offense = 0,
        Defense = 1,
        Quarterback = 2
    }
}
