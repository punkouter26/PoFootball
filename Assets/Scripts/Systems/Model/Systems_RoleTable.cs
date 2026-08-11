namespace PoFootball.Models
{
    /// <summary>
    /// Static lookups from role to side, brain group, behavior name and body
    /// dynamics. Allocation-free: every method is a switch over an enum, no
    /// dictionaries.
    ///
    /// The dynamics half of this table is DERIVED, not tuned. Drive force and
    /// steer torque are computed from the role's target top speed and turn rate
    /// together with its mass and the shared damping, so the physics provably
    /// delivers the speed the table advertises. See DriveForceOf.
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
                case Systems_PlayerRole.Quarterback:
                    return Systems_BrainGroup.Quarterback;
                case Systems_PlayerRole.OffensiveLine:
                    return Systems_BrainGroup.OffenseLine;
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.WideReceiver:
                case Systems_PlayerRole.TightEnd:
                    return Systems_BrainGroup.OffenseSkill;
                case Systems_PlayerRole.DefensiveLine:
                    return Systems_BrainGroup.DefenseLine;
                case Systems_PlayerRole.Linebacker:
                    return Systems_BrainGroup.DefenseBox;
                default:
                    return Systems_BrainGroup.DefenseSecondary;
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
                case Systems_BrainGroup.DefenseBox:
                    return "DefenseBox";
                case Systems_BrainGroup.DefenseSecondary:
                    return "DefenseSecondary";
                default:
                    return "Quarterback";
            }
        }

        // --- Body dynamics ---------------------------------------------------

        /// <summary>
        /// Target terminal speed in m/s — and, because DriveForceOf is derived from
        /// it, the speed the body actually reaches. Linemen are slowest, receivers
        /// and corners fastest, with the heavier skill positions in between.
        /// </summary>
        public static float TopSpeedOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 6.5f;
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                    return 7.5f;
                case Systems_PlayerRole.Quarterback:
                    return 8.0f;
                case Systems_PlayerRole.Linebacker:
                    return 8.2f;
                case Systems_PlayerRole.Safety:
                    return 9.0f;
                case Systems_PlayerRole.RunningBack:
                    return 9.2f;
                default:
                    return 9.6f;
            }
        }

        /// <summary>
        /// Body mass in kg. Real playing weights, which is what makes a collision
        /// between a 140 kg guard and a 92 kg corner resolve the way it should —
        /// before base04 every player massed 100 kg, so momentum in a pileup was
        /// decided purely by who was moving faster.
        /// </summary>
        public static float MassOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 140f;
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                    return 115f;
                case Systems_PlayerRole.Linebacker:
                    return 112f;
                case Systems_PlayerRole.Quarterback:
                    return 100f;
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Safety:
                    return 98f;
                default:
                    return 92f;
            }
        }

        /// <summary>Target terminal turn rate in rad/s. SteerTorqueOf is derived from it.</summary>
        public static float TurnRateOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 2.4f;
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                case Systems_PlayerRole.Linebacker:
                    return 2.9f;
                default:
                    return 3.5f;
            }
        }

        /// <summary>
        /// Newtons applied at full forward action, derived so that terminal
        /// velocity equals TopSpeedOf exactly.
        ///
        /// With linear damping d, Unity integrates v' = (v + F/m * dt) / (1 + d * dt).
        /// At steady state v' = v, which reduces to v = F / (m * d) with no
        /// dependence on dt. Inverting gives F = v * m * d.
        ///
        /// This replaces a single hand-tuned DRIVE_FORCE = 900 N shared by every
        /// role at a shared mass of 100 kg, which pinned every player — guard and
        /// receiver alike — at 900 / (100 * 1.5) = 6.0 m/s. The role top speeds
        /// were therefore unreachable and existed only as an observation
        /// normalizer.
        /// </summary>
        public static float DriveForceOf(Systems_PlayerRole role)
        {
            return TopSpeedOf(role) * MassOf(role) * Systems_SimConstants.LINEAR_DAMPING;
        }

        /// <summary>
        /// Newton-metres applied at full steer action, derived so that terminal
        /// angular velocity equals TurnRateOf.
        ///
        /// Same steady-state argument as DriveForceOf, with the moment of inertia
        /// of a uniform disc, I = 0.5 * m * r^2, in place of the mass. Deriving it
        /// is what keeps heavier roles from turning identically to lighter ones
        /// once their masses stopped being equal.
        /// </summary>
        public static float SteerTorqueOf(Systems_PlayerRole role)
        {
            float momentOfInertia = 0.5f * MassOf(role)
                * Systems_SimConstants.PLAYER_RADIUS * Systems_SimConstants.PLAYER_RADIUS;

            return TurnRateOf(role) * momentOfInertia * Systems_SimConstants.ANGULAR_DAMPING;
        }

        /// <summary>
        /// Only the quarterback's brain carries the play call, the throw trigger
        /// and the aim vector. Every other brain is two continuous outputs and
        /// nothing else, so no policy is trained on dimensions it cannot use.
        /// </summary>
        public static bool HasQuarterbackActions(Systems_BrainGroup group)
        {
            return group == Systems_BrainGroup.Quarterback;
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
