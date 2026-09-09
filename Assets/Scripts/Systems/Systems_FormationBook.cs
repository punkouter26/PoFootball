using System.Text;
using PoFootball.Models;

namespace PoFootball.Systems
{
    /// <summary>
    /// Every alignment the two teams can line up in, and the man-coverage table
    /// that belongs to each defensive one.
    ///
    /// WHAT IS FIXED AND WHAT VARIES. The eleven roles per side, and the order they
    /// appear in, are fixed forever — a slot index is the index an agent carries in
    /// the scene, and it selects that agent's brain, its drawn shape and the one-hot
    /// role slice of its observation vector. What varies between formations is
    /// nothing but the two offsets. <see cref="Validate"/> asserts exactly that, so
    /// a formation added later cannot quietly re-order the squad and scramble every
    /// brain by one slot.
    ///
    /// THE COVERAGE TRAVELS WITH THE FRONT. Systems_Formation used to carry one
    /// coverage table for the one formation that existed. With eight fronts, a
    /// single shared table would mean a two-deep zone shell whose corners are still
    /// in man across the field — eleven players executing two different defenses.
    /// Each defensive entry therefore owns its assignments, and picking a front
    /// picks the coverage with it.
    ///
    /// GEOMETRY CONVENTION, inherited from the table this replaces: the offense
    /// attacks +Y and lines up at negative Y (behind the line of scrimmage), the
    /// defense attacks -Y and lines up at positive Y, and +X is to the offense's
    /// right. The tight end is on the right, so "strong side" is +X throughout.
    /// </summary>
    public static class Systems_FormationBook
    {
        /// <summary>Slots per side. Eleven, and never anything else.</summary>
        public const int SLOTS_PER_SIDE = 11;

        // Offensive slot indices. Absolute — offense occupies 0..10.
        public const int TIGHT_END_SLOT_INDEX = 5;
        public const int WIDE_RECEIVER_LEFT_SLOT_INDEX = 6;
        public const int WIDE_RECEIVER_RIGHT_SLOT_INDEX = 7;
        public const int QUARTERBACK_SLOT_INDEX = 8;
        public const int FULLBACK_SLOT_INDEX = 9;
        public const int HALFBACK_SLOT_INDEX = 10;

        // Defensive slot indices. Absolute — defense occupies 11..21.
        public const int LINEBACKER_STRONG_SLOT_INDEX = 17;
        public const int CORNERBACK_LEFT_SLOT_INDEX = 18;
        public const int CORNERBACK_RIGHT_SLOT_INDEX = 19;
        public const int FREE_SAFETY_SLOT_INDEX = 20;
        public const int STRONG_SAFETY_SLOT_INDEX = 21;

        /// <summary>Depth every player on the line of scrimmage lines up at.</summary>
        private const float OFFENSE_LINE_Y = -0.8f;
        private const float DEFENSE_LINE_Y = 0.8f;

        // --- Offense -----------------------------------------------------------
        //
        // Role order, identical in every table below and enforced by Validate:
        //   0..4  OffensiveLine    5  TightEnd       6,7  WideReceiver
        //   8     Quarterback      9  Fullback      10    RunningBack (halfback)

        private static readonly Systems_FormationSlot[][] OffenseTables =
        {
            // ProI — the shipped alignment. Everything else is a deviation from it.
            Offense(fullbackX: 0.0f, fullbackY: -4.5f, halfbackX: 0.0f, halfbackY: -6.5f,
                quarterbackY: -2.5f),

            // StrongI — fullback offset to the tight end.
            Offense(fullbackX: 1.9f, fullbackY: -4.5f, halfbackX: 0.0f, halfbackY: -6.5f,
                quarterbackY: -2.5f),

            // WeakI — fullback offset away from the tight end.
            Offense(fullbackX: -1.9f, fullbackY: -4.5f, halfbackX: 0.0f, halfbackY: -6.5f,
                quarterbackY: -2.5f),

            // SplitBacks — both backs level, either can take it.
            Offense(fullbackX: -2.7f, fullbackY: -5.0f, halfbackX: 2.7f, halfbackY: -5.0f,
                quarterbackY: -2.5f),

            // Singleback — one back deep, fullback up as an H-back off the tackle.
            Offense(fullbackX: 3.2f, fullbackY: -2.6f, halfbackX: 0.0f,
                halfbackY: -5.5f, quarterbackY: -2.5f),

            // Shotgun — deep quarterback with both backs flanking him.
            Offense(fullbackX: -2.3f, fullbackY: -6.2f, halfbackX: 2.3f,
                halfbackY: -6.2f, quarterbackY: -6.0f),

            // Pistol — short shotgun, halfback directly behind the quarterback.
            Offense(fullbackX: -2.5f, fullbackY: -5.2f, halfbackX: 0.0f, halfbackY: -6.9f,
                quarterbackY: -4.0f),

            // Wing — fullback as a wingback tucked behind the tight end.
            Offense(fullbackX: 4.6f, fullbackY: -2.6f, halfbackX: -2.2f, halfbackY: -5.2f,
                quarterbackY: -2.5f),
        };

        /// <summary>
        /// The furthest a handoff target may line up from the quarterback.
        ///
        /// THIS IS WHY NO FORMATION HERE SPLITS THE FULLBACK OUT WIDE, even though
        /// Singleback and Shotgun sets in real football routinely do. There is no
        /// separate handoff action in this simulation — Systems_BallSystem.TryHandoff
        /// completes the exchange only once the quarterback is within
        /// HANDOFF_RADIUS (2 m) of the designated back, and the play caller picks
        /// that back without consulting the alignment. Flex the fullback out to the
        /// numbers and every fullback run called from that formation becomes a
        /// quarterback jogging eight metres sideways into the rush: not a different
        /// play, a broken one.
        ///
        /// Five metres is roughly two and a half times the handoff radius — close
        /// enough that the quarterback can close it before the front four arrive.
        /// Both backs are inside it in all eight formations, and
        /// Systems_FormationTests asserts it rather than trusting this comment.
        /// Splitting a back out for real would mean teaching the play caller to stop
        /// calling runs to him, which is a change to a discrete action head with its
        /// own brain and its own entropy bonus — a different piece of work.
        /// </summary>
        public const float MAX_HANDOFF_ALIGNMENT_DISTANCE = 5.0f;

        /// <summary>
        /// Builds an offensive table. Only the backfield and the quarterback's depth
        /// ever move between these formations — the line and the split receivers are
        /// the same five gaps and two edges in all of them — so those are the only
        /// parameters. Writing all eleven slots out eight times would bury the four
        /// numbers that actually differ.
        /// </summary>
        private static Systems_FormationSlot[] Offense(
            float fullbackX, float fullbackY, float halfbackX, float halfbackY,
            float quarterbackY)
        {
            return new[]
            {
                new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, -3.0f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, -1.5f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 0.0f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 1.5f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.OffensiveLine, 3.0f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.TightEnd, 4.5f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.WideReceiver, -12.0f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.WideReceiver, 12.0f, OFFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.Quarterback, 0.0f, quarterbackY),
                new Systems_FormationSlot(Systems_PlayerRole.Fullback, fullbackX, fullbackY),
                new Systems_FormationSlot(Systems_PlayerRole.RunningBack, halfbackX, halfbackY),
            };
        }

        // --- Defense -----------------------------------------------------------
        //
        // Role order, identical in every table below and enforced by Validate:
        //   0..3  DefensiveLine   4..6  Linebacker
        //   7,8   Cornerback      9,10  Safety   (9 = free, 10 = strong)

        private static readonly Systems_FormationSlot[][] DefenseTables =
        {
            // FourThreeBase — even front, two-high shell.
            Defense(
                lineX: new[] { -2.5f, -0.9f, 0.9f, 2.5f },
                backerX: new[] { -4.0f, 0.0f, 4.0f },
                backerY: new[] { 4.5f, 4.5f, 4.5f },
                cornerX: 12.0f, cornerY: 7.0f,
                freeSafetyX: -5.0f, freeSafetyY: 13.0f,
                strongSafetyX: 5.0f, strongSafetyY: 13.0f),

            // FourThreeOver — front shifted toward the tight end.
            Defense(
                lineX: new[] { -1.5f, 0.1f, 1.7f, 3.6f },
                backerX: new[] { -4.6f, -1.1f, 3.1f },
                backerY: new[] { 4.5f, 4.5f, 4.5f },
                cornerX: 12.0f, cornerY: 7.0f,
                freeSafetyX: -5.0f, freeSafetyY: 13.0f,
                strongSafetyX: 5.0f, strongSafetyY: 13.0f),

            // FourThreeUnder — front shifted away from the tight end.
            Defense(
                lineX: new[] { -3.6f, -1.7f, -0.1f, 1.5f },
                backerX: new[] { -3.1f, 1.1f, 4.6f },
                backerY: new[] { 4.5f, 4.5f, 4.5f },
                cornerX: 12.0f, cornerY: 7.0f,
                freeSafetyX: -5.0f, freeSafetyY: 13.0f,
                strongSafetyX: 5.0f, strongSafetyY: 13.0f),

            // Cover2 — corners press and sink, safeties split the deep halves.
            Defense(
                lineX: new[] { -2.5f, -0.9f, 0.9f, 2.5f },
                backerX: new[] { -4.5f, 0.0f, 4.5f },
                backerY: new[] { 4.8f, 4.0f, 4.8f },
                cornerX: 11.0f, cornerY: 2.2f,
                freeSafetyX: -8.5f, freeSafetyY: 13.4f,
                strongSafetyX: 8.5f, strongSafetyY: 13.4f),

            // Cover3 — one deep middle, corners in the deep thirds, strong safety
            // rolled down into the box.
            Defense(
                lineX: new[] { -2.5f, -0.9f, 0.9f, 2.5f },
                backerX: new[] { -4.0f, 0.0f, 4.0f },
                backerY: new[] { 4.5f, 4.5f, 4.5f },
                cornerX: 12.0f, cornerY: 9.0f,
                freeSafetyX: 0.0f, freeSafetyY: 13.5f,
                strongSafetyX: 5.2f, strongSafetyY: 6.2f),

            // Nickel — the strong-side backer walks out over the slot.
            Defense(
                lineX: new[] { -2.5f, -0.9f, 0.9f, 2.5f },
                backerX: new[] { -3.6f, 0.6f, 7.8f },
                backerY: new[] { 4.5f, 4.5f, 3.0f },
                cornerX: 12.0f, cornerY: 7.0f,
                freeSafetyX: -4.0f, freeSafetyY: 13.4f,
                strongSafetyX: 4.6f, strongSafetyY: 9.0f),

            // Bear46 — crowd the line, one man deep.
            Defense(
                lineX: new[] { -2.4f, -0.8f, 0.8f, 2.4f },
                backerX: new[] { -4.4f, 0.0f, 4.4f },
                backerY: new[] { 2.4f, 4.6f, 2.4f },
                cornerX: 11.0f, cornerY: 5.5f,
                freeSafetyX: 0.0f, freeSafetyY: 13.4f,
                strongSafetyX: -4.6f, strongSafetyY: 8.6f),

            // ZeroBlitz — every backer on the line, press man, no help but one.
            Defense(
                lineX: new[] { -2.5f, -0.9f, 0.9f, 2.5f },
                backerX: new[] { -5.6f, 0.0f, 5.6f },
                backerY: new[] { 2.3f, 2.3f, 2.3f },
                cornerX: 11.0f, cornerY: 1.8f,
                freeSafetyX: 0.0f, freeSafetyY: 12.0f,
                strongSafetyX: 5.4f, strongSafetyY: 5.4f),
        };

        private static Systems_FormationSlot[] Defense(
            float[] lineX, float[] backerX, float[] backerY,
            float cornerX, float cornerY,
            float freeSafetyX, float freeSafetyY,
            float strongSafetyX, float strongSafetyY)
        {
            return new[]
            {
                new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, lineX[0], DEFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, lineX[1], DEFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, lineX[2], DEFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.DefensiveLine, lineX[3], DEFENSE_LINE_Y),
                new Systems_FormationSlot(Systems_PlayerRole.Linebacker, backerX[0], backerY[0]),
                new Systems_FormationSlot(Systems_PlayerRole.Linebacker, backerX[1], backerY[1]),
                new Systems_FormationSlot(Systems_PlayerRole.Linebacker, backerX[2], backerY[2]),
                new Systems_FormationSlot(Systems_PlayerRole.Cornerback, -cornerX, cornerY),
                new Systems_FormationSlot(Systems_PlayerRole.Cornerback, cornerX, cornerY),
                new Systems_FormationSlot(Systems_PlayerRole.Safety, freeSafetyX, freeSafetyY),
                new Systems_FormationSlot(Systems_PlayerRole.Safety, strongSafetyX, strongSafetyY),
            };
        }

        // --- Coverage ----------------------------------------------------------

        /// <summary>
        /// Man assignments per defensive formation, as (defender slot, receiver
        /// slot) pairs. A defender absent from its formation's list plays zone,
        /// which the agent layer reads as -1.
        ///
        /// FIXED PAIRS RATHER THAN A NEAREST-RECEIVER SEARCH, for the reason the
        /// single table this replaces already gave: nearest-receiver is quadratic in
        /// the squad, runs on every defender on every decision, and is unstable —
        /// two defenders claim the same receiver and abandon it as the geometry
        /// crosses over, so the coverage visibly flickers. A defense plays fixed
        /// assignments precisely because that ambiguity is what offenses attack.
        ///
        /// EVERY ELIGIBLE MUST BE COVERED, AND THIS SIMULATION CANNOT PLAY ZONE.
        /// Agent_FootballPlayer.CoverageSpot has exactly two behaviours: chase your
        /// assigned man, or — for an assignment of -1 — go to the deep middle. That
        /// second branch was written for ONE free safety and is not a zone drop.
        ///
        /// So a defense that leaves receivers unassigned does not play zone against
        /// them, it does not cover them at all, and every unassigned defender piles
        /// onto the same deep-middle spot. That was shipped and measured: Cover2 was
        /// given an empty table on the reasoning that two-deep is a zone shell, and a
        /// full game came back at 14.08 yards per play, 0.62 touchdowns per drive and
        /// a 73-77 final, against references of 5.5 and 0.20-0.35. Both split ends
        /// and the tight end were running free on a quarter of the snaps.
        ///
        /// Until the agent layer learns real zone drops, the coverage difference
        /// between these fronts lives in the ALIGNMENT — depth, leverage, who is in
        /// the box — and every one of them assigns both split ends and the tight end.
        /// Systems_FormationTests asserts it rather than trusting this comment.
        /// </summary>
        private static readonly int[][] CoverageTables =
        {
            // FourThreeBase — Cover 1: corners on the split ends, strong safety on
            // the tight end, free safety over the top.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // FourThreeOver — same coverage, different front.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // FourThreeUnder — same.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // Cover2 — the SHELL is two-deep, but the assignments are not empty, and
            // that is a limitation of this simulation rather than a coaching choice.
            // See EVERY ELIGIBLE MUST BE COVERED below.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // Cover3 — corners carry the split ends, the rolled-down strong safety
            // carries the tight end, the free safety has the deep middle.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // Nickel — corners man-up outside, the walked-out backer takes the tight
            // end, and both safeties play over the top.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                LINEBACKER_STRONG_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // Bear46 — corners man outside, strong safety on the tight end, free
            // safety alone over the top. The front is what makes this the 46; the
            // coverage behind it still has to account for all three eligibles.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },

            // ZeroBlitz — everybody eligible is covered man-to-man.
            new[]
            {
                CORNERBACK_LEFT_SLOT_INDEX, WIDE_RECEIVER_LEFT_SLOT_INDEX,
                CORNERBACK_RIGHT_SLOT_INDEX, WIDE_RECEIVER_RIGHT_SLOT_INDEX,
                STRONG_SAFETY_SLOT_INDEX, TIGHT_END_SLOT_INDEX,
            },
        };

        // --- Lookup ------------------------------------------------------------

        public static int OffensiveFormationCount => OffenseTables.Length;

        public static int DefensiveFormationCount => DefenseTables.Length;

        /// <summary>Offensive slot 0..10 for the given formation.</summary>
        public static Systems_FormationSlot OffenseSlot(
            Systems_OffensiveFormation formation, int slotIndex)
        {
            return OffenseTables[(int)formation][slotIndex];
        }

        /// <summary>Defensive slot, indexed 0..10 within the defense.</summary>
        public static Systems_FormationSlot DefenseSlot(
            Systems_DefensiveFormation formation, int slotIndexWithinDefense)
        {
            return DefenseTables[(int)formation][slotIndexWithinDefense];
        }

        /// <summary>
        /// The offensive slot this defender covers man-to-man, or -1 for zone.
        /// Linear scan of at most three pairs — a dictionary per formation would
        /// allocate eight of them to answer a question that is six comparisons.
        /// </summary>
        public static int CoverageAssignmentFor(
            Systems_DefensiveFormation formation, int defenderSlotIndex)
        {
            int[] table = CoverageTables[(int)formation];

            for (int index = 0; index < table.Length; index += 2)
            {
                if (table[index] == defenderSlotIndex)
                {
                    return table[index + 1];
                }
            }

            return -1;
        }

        /// <summary>
        /// How far the widest player in ANY formation lines up from the centre of
        /// the field. Read by Systems_BroadcastCameraView to size the snap shot.
        ///
        /// Across every formation rather than the current one, deliberately: a
        /// camera that resized itself per play would breathe in and out at every
        /// snap. One framing that fits the widest alignment fits all of them.
        /// </summary>
        public static readonly float WidestSlotX = ComputeWidestSlotX();

        private static float ComputeWidestSlotX()
        {
            float widest = 0f;

            for (int table = 0; table < OffenseTables.Length; table++)
            {
                widest = WidestIn(OffenseTables[table], widest);
            }

            for (int table = 0; table < DefenseTables.Length; table++)
            {
                widest = WidestIn(DefenseTables[table], widest);
            }

            return widest;
        }

        private static float WidestIn(Systems_FormationSlot[] slots, float widest)
        {
            for (int index = 0; index < slots.Length; index++)
            {
                float distance = slots[index].OffsetX < 0f
                    ? -slots[index].OffsetX
                    : slots[index].OffsetX;

                if (distance > widest)
                {
                    widest = distance;
                }
            }

            return widest;
        }

        // --- Validation --------------------------------------------------------

        /// <summary>
        /// Checks every formation against the two invariants that cannot be allowed
        /// to break silently, and returns a description of the first batch of
        /// failures, or null when everything holds.
        ///
        /// WHY THIS EXISTS RATHER THAN A COMMENT ASKING PEOPLE TO BE CAREFUL. Both
        /// invariants fail invisibly. Re-order a role and every agent keeps its slot
        /// index but gets a different body, so the game still runs and the brains
        /// are all one seat out of place. Put two players 1.1 m apart and they spawn
        /// inside each other's collider, which the physics resolves by flinging one
        /// of them across the field on the first tick of a play, occasionally, on
        /// one formation out of sixty-four.
        ///
        /// Sixty-four offense-defense pairings is far too many to check by eye, and
        /// checking them by eye is what a person adding a ninth formation would do.
        /// Called by Systems_EpisodeDirector on start in development builds, where
        /// the result reaches both the log and the on-device DEBUG sheet.
        /// </summary>
        public static string Validate()
        {
            var failures = new StringBuilder();

            CheckRoleOrder(failures, OffenseTables, "offense", ExpectedOffenseRoles);
            CheckRoleOrder(failures, DefenseTables, "defense", ExpectedDefenseRoles);

            if (CoverageTables.Length != DefenseTables.Length)
            {
                failures.Append("coverage tables (").Append(CoverageTables.Length)
                    .Append(") do not match defensive formations (")
                    .Append(DefenseTables.Length).Append("); ");
            }

            CheckSeparation(failures);

            return failures.Length == 0 ? null : failures.ToString();
        }

        private static readonly Systems_PlayerRole[] ExpectedOffenseRoles =
        {
            Systems_PlayerRole.OffensiveLine, Systems_PlayerRole.OffensiveLine,
            Systems_PlayerRole.OffensiveLine, Systems_PlayerRole.OffensiveLine,
            Systems_PlayerRole.OffensiveLine, Systems_PlayerRole.TightEnd,
            Systems_PlayerRole.WideReceiver, Systems_PlayerRole.WideReceiver,
            Systems_PlayerRole.Quarterback, Systems_PlayerRole.Fullback,
            Systems_PlayerRole.RunningBack,
        };

        private static readonly Systems_PlayerRole[] ExpectedDefenseRoles =
        {
            Systems_PlayerRole.DefensiveLine, Systems_PlayerRole.DefensiveLine,
            Systems_PlayerRole.DefensiveLine, Systems_PlayerRole.DefensiveLine,
            Systems_PlayerRole.Linebacker, Systems_PlayerRole.Linebacker,
            Systems_PlayerRole.Linebacker, Systems_PlayerRole.Cornerback,
            Systems_PlayerRole.Cornerback, Systems_PlayerRole.Safety,
            Systems_PlayerRole.Safety,
        };

        private static void CheckRoleOrder(
            StringBuilder failures, Systems_FormationSlot[][] tables, string side,
            Systems_PlayerRole[] expected)
        {
            for (int table = 0; table < tables.Length; table++)
            {
                if (tables[table].Length != SLOTS_PER_SIDE)
                {
                    failures.Append(side).Append(' ').Append(table).Append(" has ")
                        .Append(tables[table].Length).Append(" slots, not ")
                        .Append(SLOTS_PER_SIDE).Append("; ");
                    continue;
                }

                for (int slot = 0; slot < SLOTS_PER_SIDE; slot++)
                {
                    if (tables[table][slot].Role != expected[slot])
                    {
                        failures.Append(side).Append(' ').Append(table)
                            .Append(" slot ").Append(slot).Append(" is ")
                            .Append(tables[table][slot].Role).Append(", expected ")
                            .Append(expected[slot]).Append("; ");
                    }
                }
            }
        }

        /// <summary>
        /// Every offense-defense pairing, every pair of players, against
        /// MIN_SPAWN_SEPARATION. Sixty-four pairings of 231 pairs each is about
        /// fifteen thousand distance tests, once, on start, in development only.
        /// </summary>
        private static void CheckSeparation(StringBuilder failures)
        {
            float minimum = Systems_SimConstants.MIN_SPAWN_SEPARATION;
            float minimumSquared = minimum * minimum;

            for (int offense = 0; offense < OffenseTables.Length; offense++)
            {
                for (int defense = 0; defense < DefenseTables.Length; defense++)
                {
                    if (TooClose(
                            OffenseTables[offense], DefenseTables[defense],
                            minimumSquared, out int slotA, out int slotB,
                            out float distance))
                    {
                        failures.Append("offense ").Append(offense)
                            .Append(" vs defense ").Append(defense)
                            .Append(": slots ").Append(slotA).Append(" and ")
                            .Append(slotB).Append(" are ")
                            .Append(distance.ToString("F2")).Append(" m apart, under ")
                            .Append(minimum.ToString("F2")).Append("; ");
                    }
                }
            }
        }

        private static bool TooClose(
            Systems_FormationSlot[] offense, Systems_FormationSlot[] defense,
            float minimumSquared, out int slotA, out int slotB, out float distance)
        {
            for (int first = 0; first < SLOTS_PER_SIDE * 2; first++)
            {
                Systems_FormationSlot a = first < SLOTS_PER_SIDE
                    ? offense[first]
                    : defense[first - SLOTS_PER_SIDE];

                for (int second = first + 1; second < SLOTS_PER_SIDE * 2; second++)
                {
                    Systems_FormationSlot b = second < SLOTS_PER_SIDE
                        ? offense[second]
                        : defense[second - SLOTS_PER_SIDE];

                    float deltaX = a.OffsetX - b.OffsetX;
                    float deltaY = a.OffsetY - b.OffsetY;
                    float squared = (deltaX * deltaX) + (deltaY * deltaY);

                    if (squared < minimumSquared)
                    {
                        slotA = first;
                        slotB = second;
                        distance = UnityEngine.Mathf.Sqrt(squared);
                        return true;
                    }
                }
            }

            slotA = -1;
            slotB = -1;
            distance = 0f;
            return false;
        }
    }
}
