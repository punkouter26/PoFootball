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
                case Systems_PlayerRole.RunningBack:
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.WideReceiver:
                case Systems_PlayerRole.TightEnd:
                    return Systems_BrainGroup.Offense;
                default:
                    return Systems_BrainGroup.Defense;
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
                case Systems_BrainGroup.Offense:
                    return "Offense";
                case Systems_BrainGroup.Defense:
                    return "Defense";
                default:
                    return "Quarterback";
            }
        }

        // --- Body dynamics ---------------------------------------------------

        /// <summary>
        /// Target terminal speed in m/s — and, because DriveForceOf is derived from
        /// it, the speed the body actually reaches. Linemen are slowest, receivers
        /// and corners fastest, with the heavier skill positions in between.
        ///
        /// These are real in-play maxima, not sprint records. Player tracking puts
        /// the fastest receivers and corners a shade over 9 m/s (about 21 mph) on
        /// the handful of plays a game where they are genuinely running free, and
        /// linemen around 6 m/s. They are a CEILING, not a cruising speed: under
        /// the acceleration curve set by Systems_SimConstants.LINEAR_DAMPING it
        /// takes roughly 25 m to reach one, so most plays never touch these numbers
        /// at all — which is the point, and is what the previous pairing of these
        /// speeds with a 0.67 s time constant destroyed.
        /// </summary>
        public static float TopSpeedOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 6.2f;
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                    return 7.3f;
                case Systems_PlayerRole.Quarterback:
                    return 7.6f;
                // RAISED FOR CONTRACT REVISION 8, AND THE ORDER AGAINST THE BACK IS
                // THE WHOLE POINT. At 8.0 and 8.7 both of these sat BELOW the
                // running back's 8.9, so a carrier who cleared the line could not be
                // run down by either of the two levels whose job that is — only a
                // cornerback was faster, and corners are occupied covering the
                // receivers. A measured game showed what that costs: 13 touchdowns
                // in 17 drives, roughly 15 yards a play, and seven plays that hit
                // the 750-tick cap with nobody having caught the carrier at all.
                //
                // The safety is now 9.2 — above the back, still below the corner's
                // 9.3, so the depth chart is unchanged and the last line of defense
                // can actually be the last line of defense. A real free safety is
                // not faster than a feature back, but a real defense also has
                // pursuit angles from eleven bodies rather than the handful this
                // sim's spacing produces; 9.2 is buying back that geometry.
                case Systems_PlayerRole.Linebacker:
                    return 8.5f;
                case Systems_PlayerRole.Safety:
                    return 9.2f;
                case Systems_PlayerRole.RunningBack:
                    return 8.9f;
                default:
                    return 9.3f;
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

        /// <summary>
        /// Target terminal turn rate in rad/s. SteerTorqueOf is derived from it.
        ///
        /// BOUNDED BY GRIP, NOT BY THE BODY. A player running at v and turning at
        /// omega is pulling a lateral acceleration of v * omega, and that has to
        /// come from friction against the turf. A cutting athlete manages somewhere
        /// around 8-10 m/s², call it one g. At the old 3.5 rad/s a receiver holding
        /// 9.6 m/s was turning at 34 m/s² — three and a half g, sustained, which no
        /// surface on earth supplies. That is why players looked like they were
        /// swivelling rather than running: the turn was free.
        ///
        /// At 2.6 the same receiver at full speed pulls 24 m/s², which is still
        /// generous — but the model has no speed dependence, and pricing the turn
        /// for full speed would leave a stationary player unable to pivot to face
        /// anything. These are set for a player at cruising speed and deliberately
        /// left flattering at the top end.
        /// </summary>
        public static float TurnRateOf(Systems_PlayerRole role)
        {
            switch (role)
            {
                case Systems_PlayerRole.OffensiveLine:
                case Systems_PlayerRole.DefensiveLine:
                    return 1.9f;
                case Systems_PlayerRole.Fullback:
                case Systems_PlayerRole.TightEnd:
                case Systems_PlayerRole.Linebacker:
                    return 2.2f;
                default:
                    return 2.6f;
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
        /// receiver alike — at 900 / (100 * d) m/s. The role top speeds were
        /// therefore unreachable and existed only as an observation normalizer.
        ///
        /// Because d appears on both sides of the physics — it sets terminal
        /// velocity here and the acceleration curve in the integrator — lowering
        /// LINEAR_DAMPING for realistic acceleration lowers every applied force by
        /// the same factor and leaves every top speed exactly where the table says.
        /// That is the whole reason the force is derived rather than typed in.
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
    }
}
