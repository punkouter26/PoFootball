namespace PoFootball.Models
{
    /// <summary>
    /// Static lookups from role to side, brain group, and behavior name.
    /// Allocation-free: every method is a switch over an enum, no dictionaries.
    /// </summary>
    public static class Systems_RoleTable
    {
        /// <summary>Number of distinct roles — the width of the one-hot role slice.</summary>
        public const int ROLE_COUNT = 10;

        /// <summary>Players fielded per side.</summary>
        public const int SQUAD_SIZE = 11;

        public static Systems_TeamSide SideOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.Quarterback:
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.WideReceiver:
                case Systems_PlayerRole.TightEnd:
                case Systems_PlayerRole.OffensiveLine:
                    return Systems_TeamSide.Offense;
                default:
                    return Systems_TeamSide.Defense;
            }
        }

        public static Systems_BrainGroup BrainOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                    return Systems_BrainGroup.OffenseLine;
                case Systems_PlayerRole.Quarterback:
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.WideReceiver:
                case Systems_PlayerRole.TightEnd:
                    return Systems_BrainGroup.OffenseSkill;
                case Systems_PlayerRole.DefensiveLine:
                    return Systems_BrainGroup.DefenseLine;
                default:
                    return Systems_BrainGroup.DefenseCover;
            }
        }

        /// <summary>
        /// Behavior name handed to BehaviorParameters. Must match the YAML keys.
        /// Returned as a literal so no string is built at runtime.
        /// </summary>
        public static string BehaviorNameOf(Systems_BrainGroup group)
        {
            switch (group)
            {
                case Systems_BrainGroup.OffenseLine:
                    return "OffenseLine";
                case Systems_BrainGroup.OffenseSkill:
                    return "OffenseSkill";
                case Systems_BrainGroup.DefenseLine:
                    return "DefenseLine";
                default:
                    return "DefenseCover";
            }
        }

        /// <summary>Top speed in m/s. Linemen are slowest, the fullback in between.</summary>
        public static float TopSpeedOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 6.5f;
                case Systems_PlayerRole.Fullback:
                    return 7.5f;
                default:
                    return 9.0f;
            }
        }

        /// <summary>
        /// Only the OffenseSkill brain carries the quarterback's extra outputs —
        /// the play call, the throw trigger and the aim vector. Giving those to the
        /// linemen and the defense would be dead action dimensions the policy has
        /// to learn to ignore.
        ///
        /// Non-QB skill players share the brain and therefore the action space;
        /// their play-call and throw outputs are simply never read.
        /// </summary>
        public static bool HasQuarterbackActions(Systems_BrainGroup group)
        {
            return group == Systems_BrainGroup.OffenseSkill;
        }

        /// <summary>Which role a handoff call targets, or None if the call is not a handoff.</summary>
        public static Systems_PlayerRole HandoffTargetOf(Systems_PlayCall call)
        {
            switch (call)
            {
                case Systems_PlayCall.HandoffFullback:
                    return Systems_PlayerRole.Fullback;
                case Systems_PlayCall.HandoffHalfback:
                    return Systems_PlayerRole.RunningBack;
                default:
                    return Systems_PlayerRole.Quarterback;
            }
        }
    }
}
