using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// The training line-of-scrimmage draw, lifted verbatim out of
    /// <see cref="Systems_EpisodeDirector"/>.
    ///
    /// Every detail here is load-bearing for reproducibility: the same seed source
    /// (<see cref="Systems_EpisodeSeed"/>), the same generator
    /// (Unity.Mathematics.Random), the same single NextFloat call per episode
    /// against the same bounds. A run started before the game layer existed and a
    /// run started after it draw the identical sequence of scrimmage lines.
    ///
    /// If this ever needs to change, that is a new curriculum and it belongs in a
    /// new config with a new run-id — not an edit here.
    ///
    /// AND IT IS NOW STAGED, WHICH IS EXACTLY THAT. Config/FootballBase12.yaml
    /// carries a three-lesson curriculum over the spot_lesson environment
    /// parameter. The lessons narrow the RANGES the draws land in; they never
    /// change how many draws an episode makes, so a given seed still produces one
    /// fixed sequence of underlying numbers. With no lesson source installed — any
    /// config without the parameter, or a scene without Agent_Curriculum — it is
    /// LESSON_FULL.
    ///
    /// REVISION 14 DRAWS THE KIND OF SNAP TOO. The kicking game is played now, so a
    /// trainer that only ever snapped scrimmage downs would never put a kickoff, a
    /// return or a try in front of a policy. Five draws per episode instead of
    /// three — which makes this a new sequence, as the note above says a change here
    /// must be, and it belongs to Config/FootballBase16.yaml.
    /// </summary>
    public sealed class Systems_RandomSpotProvider : Systems_ISpotProvider
    {
        /// <summary>
        /// Inside the opponent's twenty, first to third down. Short fields end in
        /// touchdowns, so the terminal reward a policy has to discover is reachable
        /// from the first thousand plays instead of being a hundred-yard drive away.
        /// </summary>
        public const int LESSON_RED_ZONE = 0;

        /// <summary>Anywhere on the field, still no fourth down.</summary>
        public const int LESSON_OPEN_FIELD = 1;

        /// <summary>Anywhere, any down — the distribution every run before had.</summary>
        public const int LESSON_FULL = 2;

        private const float RED_ZONE_YARDS = 20f;

        /// <summary>
        /// Share of FULL-lesson episodes that are a kickoff. About one snap in
        /// eleven in a real game is one; a little under that here, because every
        /// kickoff is a long play and the scrimmage game is still most of the work.
        /// </summary>
        public const float KICKOFF_SHARE = 0.08f;

        /// <summary>Share of FULL-lesson episodes that are a kicked try.</summary>
        public const float EXTRA_POINT_SHARE = 0.03f;

        /// <summary>Share of FULL-lesson episodes that are a two-point try.</summary>
        public const float TWO_POINT_SHARE = 0.03f;

        /// <summary>Share of kickoffs that go short.</summary>
        public const float ONSIDE_SHARE = 0.12f;

        private Random _rng;

        private Systems_ISpotLessonSource _lessonSource;

        public Systems_RandomSpotProvider()
        {
            _rng = new Random(Systems_EpisodeSeed.Value);
        }

        public void SetLessonSource(Systems_ISpotLessonSource lessonSource)
        {
            _lessonSource = lessonSource;
        }

        /// <summary>
        /// Training never ends. There is no clock, no score and no fourth quarter —
        /// mlagents-learn decides when to stop by its own step budget, and the
        /// environment must keep producing episodes until it does.
        /// </summary>
        public bool HasNextPlay => true;

        /// <summary>
        /// Zero, and it must stay zero. A dead-ball pause is wall-clock a trainer
        /// spends producing no experience, multiplied by 4-8 environments and by
        /// time_scale 20. It also changes nothing a policy observes, so paying for
        /// it here would be pure loss.
        ///
        /// At zero the director ends the episode and begins the next one in the same
        /// FixedTick, exactly as it did before the pause existed — so a run started
        /// either side of this change draws the identical sequence of episodes.
        /// </summary>
        public int DeadBallTicks => 0;

        /// <summary>
        /// A seeded spot, and now a seeded SITUATION.
        ///
        /// Training has no chains — Systems_GameFlowSystem is a Game-mode
        /// registration — so before this the down did not exist here at all and a
        /// quarterback could not have learned when to punt if it wanted to. Drawing
        /// down and distance alongside the spot puts the decision in front of the
        /// policy on roughly a quarter of reps without turning training into a full
        /// game simulation: every play is still an independent, seeded rep.
        ///
        /// Distance is drawn from one to fifteen rather than always ten, so 4th and
        /// 1 and 4th and 14 are both in the distribution. A policy that only ever saw
        /// ten would learn one threshold and apply it everywhere.
        /// </summary>
        public Systems_PlaySituation NextSituation()
        {
            int lesson = _lessonSource == null ? LESSON_FULL : _lessonSource.CurrentLesson;

            float minimumY = lesson == LESSON_RED_ZONE
                ? Systems_FieldModel.ATTACKING_GOAL_LINE_Y - (RED_ZONE_YARDS * Systems_FieldModel.YARD)
                : Systems_FieldModel.LOS_MIN_Y;

            int lastDown = lesson == LESSON_RED_ZONE || lesson == LESSON_OPEN_FIELD
                ? Systems_GameRules.DOWNS_PER_SERIES - 1
                : Systems_GameRules.DOWNS_PER_SERIES;

            // Five draws, always, in this order — see the class note.
            float kindDraw = _rng.NextFloat();
            float onsideDraw = _rng.NextFloat();
            float lineOfScrimmageY = _rng.NextFloat(minimumY, Systems_FieldModel.LOS_MAX_Y);

            int down = _rng.NextInt(1, lastDown + 1);
            float yardsToGo = _rng.NextFloat(1f, 15f);

            Systems_PlayKind kind = lesson == LESSON_FULL
                ? KindFor(kindDraw)
                : Systems_PlayKind.Scrimmage;

            switch (kind)
            {
                case Systems_PlayKind.Kickoff:
                    return new Systems_PlaySituation(
                        OwnYardLine(Systems_GameRules.KICKOFF_YARD_LINE),
                        1, Systems_GameRules.YARDS_TO_GAIN,
                        kind, onsideDraw < ONSIDE_SHARE);

                case Systems_PlayKind.ExtraPoint:
                    return Try(kind, Systems_GameRules.EXTRA_POINT_YARD_LINE);

                case Systems_PlayKind.TwoPoint:
                    return Try(kind, Systems_GameRules.TWO_POINT_YARD_LINE);

                default:
                    return new Systems_PlaySituation(lineOfScrimmageY, down, yardsToGo);
            }
        }

        private static Systems_PlayKind KindFor(float draw)
        {
            if (draw < KICKOFF_SHARE)
            {
                return Systems_PlayKind.Kickoff;
            }

            if (draw < KICKOFF_SHARE + EXTRA_POINT_SHARE)
            {
                return Systems_PlayKind.ExtraPoint;
            }

            if (draw < KICKOFF_SHARE + EXTRA_POINT_SHARE + TWO_POINT_SHARE)
            {
                return Systems_PlayKind.TwoPoint;
            }

            return Systems_PlayKind.Scrimmage;
        }

        /// <summary>A try is snapped from the opponent's yard line, first and goal.</summary>
        private static Systems_PlaySituation Try(Systems_PlayKind kind, float opponentYardLine)
        {
            float lineOfScrimmageY = Systems_FieldModel.ATTACKING_GOAL_LINE_Y
                - (opponentYardLine * Systems_FieldModel.YARD);

            return new Systems_PlaySituation(lineOfScrimmageY, 1, opponentYardLine, kind, false);
        }

        private static float OwnYardLine(float yardLine)
        {
            return Systems_FieldModel.OWN_GOAL_LINE_Y + (yardLine * Systems_FieldModel.YARD);
        }
    }
}
