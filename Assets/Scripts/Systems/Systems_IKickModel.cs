using PoFootball.Models;
using Random = Unity.Mathematics.Random;

namespace PoFootball.Systems
{
    /// <summary>
    /// How a kick turns out. The one piece of football in this project whose result
    /// is not produced by the simulation — there is no kicking model, for the same
    /// reason Systems_GameRules.EXTRA_POINT_POINTS is awarded rather than played.
    ///
    /// WHY THIS IS AN INTERFACE AND NOT A CONSTANT. It used to be two constants read
    /// straight out of Systems_GameRules: a field goal was good if and only if the
    /// attempt was 55 yards or shorter, and a punt netted exactly 40 yards, always.
    /// Both were deliberate, and the reasoning for both was about TRAINING — a
    /// sampled curve injects variance the offense cannot influence, and a policy can
    /// only learn to exploit a rule that is a pure function of field position.
    ///
    /// That reasoning is sound and it is preserved exactly, in
    /// <see cref="Systems_DeterministicKickModel"/>. What it is not is a good
    /// WATCHING experience. A full played game came back with `Punts 0, FG 0/0` —
    /// but even when kicks do occur, a cliff at 55 means a viewer who knows the
    /// number knows the result before the snap, every time, and no kick in the game
    /// is ever in doubt. A 52-yard attempt is supposed to be the most interesting
    /// moment on the field.
    ///
    /// So the seam is the same one Systems_ISpotProvider already established for the
    /// line of scrimmage: training keeps the pure function it was fitted against,
    /// and a played game gets the version worth watching. Nothing a policy observes
    /// changes, because in Training the deterministic implementation is what is in
    /// the container and it computes exactly what the old constants did.
    /// </summary>
    public interface Systems_IKickModel
    {
        /// <param name="attemptYards">
        /// Distance of the attempt — the yards to the goal line PLUS
        /// Systems_GameRules.FIELD_GOAL_SNAP_YARDS, which is how a kick is measured.
        /// </param>
        bool IsFieldGoalGood(float attemptYards);

        /// <summary>
        /// Net yards a punt gains, from the line of scrimmage to where the receiving
        /// team next snaps it.
        /// </summary>
        float PuntNetYards();

        /// <summary>
        /// Own yard line the receiving team takes over on after a kickoff — i.e.
        /// where the return ended.
        ///
        /// KICKOFFS USED TO NOT EXIST. Every score handed the conceding team the
        /// ball on its own 35 and that was the whole of it, which is fine as far as
        /// field position goes and catastrophic for the shape of a game: a team
        /// eight points down with a minute left had NO MECHANISM to get the ball
        /// back. The onside kick is the only reason a late deficit is ever
        /// recoverable, and there was nothing here to recover.
        ///
        /// Resolved at the rules layer rather than simulated, exactly as a punt and
        /// a field goal already are — a kickoff is not a snap the offense plays.
        /// </summary>
        float KickoffReturnYardLine();

        /// <summary>
        /// Whether an onside kick is recovered by the KICKING team. Real recovery
        /// rates sit near one in five when the receiving team is expecting it, which
        /// is always the case in the only situation this simulation calls one.
        /// </summary>
        bool IsOnsideRecovered();
    }

    /// <summary>
    /// Training. A pure function of field position, with no draw taken at all —
    /// byte-identical to the constants this replaced, so a run's dynamics are
    /// unchanged and no `.onnx` is being evaluated against anything new.
    /// </summary>
    public sealed class Systems_DeterministicKickModel : Systems_IKickModel
    {
        public bool IsFieldGoalGood(float attemptYards)
        {
            return attemptYards <= Systems_GameRules.FIELD_GOAL_MAX_YARDS;
        }

        public float PuntNetYards()
        {
            return Systems_GameRules.PUNT_NET_YARDS;
        }

        /// <summary>The touchback spot, every time — what training always saw.</summary>
        public float KickoffReturnYardLine()
        {
            return Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE;
        }

        /// <summary>
        /// Never. Training has no score and no clock, so it never reaches the
        /// situation that calls one, and a random turnover is exactly the noise the
        /// deterministic models exist to keep out.
        /// </summary>
        public bool IsOnsideRecovered()
        {
            return false;
        }
    }

    /// <summary>
    /// A played game. Distance-weighted odds on a field goal and a punt that is not
    /// the same punt every time.
    ///
    /// THE CURVE IS THE REAL ONE, ROUNDED. Systems_GameRules.FIELD_GOAL_MAX_YARDS
    /// already documents what actual kickers do — roughly 95% inside 30, 85% from
    /// 30-39, 75% from 40-49, 60% from 50-59, about 35% from 60 and out — and then
    /// declines to use it. <see cref="MakeProbability"/> is that table. The cliff
    /// becomes a slope, so a 38-yarder is nearly automatic, a 52-yarder is a real
    /// decision, and a 58-yarder is a gamble that sometimes comes off.
    ///
    /// SEEDED, NOT Random.value. Systems_EpisodeSeed is the project's single source
    /// of randomness and a played game already draws a fresh one per session
    /// (Systems_GameLifetimeScope.ResolveSeed), so kicks vary between games and
    /// replay exactly within one. The seed is mixed with a constant so this stream
    /// cannot march in step with any other consumer of the same seed.
    /// </summary>
    public sealed class Systems_ProbabilisticKickModel : Systems_IKickModel
    {
        /// <summary>
        /// Arbitrary odd constant, mixed into the shared seed so this generator's
        /// sequence is independent of anything else started from the same value.
        /// </summary>
        private const uint STREAM_OFFSET = 0x9E3779B9u;

        /// <summary>Spread of a punt's net yardage, plus or minus, in yards.</summary>
        private const float PUNT_SPREAD_YARDS = 7f;

        /// <summary>
        /// A shanked punt and a career-best one are both real, but neither should be
        /// arbitrarily bad or good — the draw is clamped into a plausible band.
        /// </summary>
        private const float PUNT_MIN_YARDS = 26f;

        private const float PUNT_MAX_YARDS = 56f;

        private Random _rng;

        public Systems_ProbabilisticKickModel()
        {
            _rng = new Random(Systems_EpisodeSeed.Value ^ STREAM_OFFSET);
        }

        public bool IsFieldGoalGood(float attemptYards)
        {
            return _rng.NextFloat() < MakeProbability(attemptYards);
        }

        /// <summary>
        /// A punt's net, drawn from a triangular distribution around
        /// Systems_GameRules.PUNT_NET_YARDS. Two draws summed rather than one, so
        /// the middle of the band is commoner than the ends — a 40-yard punt should
        /// be the ordinary outcome and a 26-yard one should be a bad day, not an
        /// equally likely one.
        /// </summary>
        public float PuntNetYards()
        {
            float unit = (_rng.NextFloat() + _rng.NextFloat()) - 1f;

            float net = Systems_GameRules.PUNT_NET_YARDS + (unit * PUNT_SPREAD_YARDS);

            return UnityEngine.Mathf.Clamp(net, PUNT_MIN_YARDS, PUNT_MAX_YARDS);
        }

        /// <summary>
        /// Where the return ends. Most kickoffs are touchbacks under the current
        /// rule; the rest are a return that usually dies short of the touchback spot
        /// and occasionally breaks for real field position.
        /// </summary>
        public float KickoffReturnYardLine()
        {
            if (_rng.NextFloat() < Systems_GameRules.KICKOFF_TOUCHBACK_CHANCE)
            {
                return Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE;
            }

            // Triangular around the touchback spot, wider on the downside: a return
            // brought out is usually a worse outcome than taking the knee, which is
            // why the touchback rule was written the way it was.
            float unit = (_rng.NextFloat() + _rng.NextFloat()) - 1f;

            float line = Systems_GameRules.KICKOFF_TOUCHBACK_YARD_LINE
                + (unit * Systems_GameRules.KICKOFF_RETURN_SPREAD_YARDS);

            return UnityEngine.Mathf.Clamp(line, 8f, 75f);
        }

        public bool IsOnsideRecovered()
        {
            return _rng.NextFloat() < Systems_GameRules.ONSIDE_RECOVERY_CHANCE;
        }

        /// <summary>
        /// Chance an attempt of this length is good. Bands rather than a fitted
        /// curve: the underlying data is quoted in bands, and a table a reader can
        /// check against the real numbers is worth more here than a polynomial.
        /// </summary>
        private static float MakeProbability(float attemptYards)
        {
            if (attemptYards <= 29f)
            {
                return 0.95f;
            }

            if (attemptYards <= 39f)
            {
                return 0.85f;
            }

            if (attemptYards <= 49f)
            {
                return 0.75f;
            }

            if (attemptYards <= 59f)
            {
                return 0.60f;
            }

            // Beyond 62 nobody is trying, and Agent_PlayCaller will not call one —
            // IsInFieldGoalRange still gates on FIELD_GOAL_MAX_YARDS. This band
            // exists so the model is total rather than because it is reached often.
            return 0.35f;
        }
    }
}
