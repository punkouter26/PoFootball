using System;
using System.IO;
using NUnit.Framework;
using PoFootball.Agents;
using PoFootball.Models;
using PoFootball.Sensors;
using Unity.MLAgents.Actuators;
using UnityEngine;

namespace PoFootball.Tests
{
    /// <summary>
    /// The observation and action contract, asserted rather than assumed.
    ///
    /// This suite exists because of what shipped in Assets/Agents/Football_v01.
    /// Every one of those four .onnx files came from run football_base02 at about
    /// 500k steps — an OLDER contract, with 25 observations and two continuous
    /// actions and no discrete branches at all — while the scene had moved on to 32
    /// observations and, for the quarterback, four continuous actions plus two
    /// discrete branches. Nothing anywhere compared the two.
    ///
    /// The failure was completely silent and the symptom looked like a training
    /// problem: the quarterback's discrete branch read zero on every step, zero
    /// decoded to KeepQuarterback under the old encoding, and so the ball never
    /// changed hands on any play in SCN_GAME. It presented as a collapsed policy.
    ///
    /// Nothing here needs the Editor or the scene — the contract is data, and data
    /// can be checked in EditMode on every run.
    /// </summary>
    public sealed class Systems_ContractTests
    {
        /// <summary>
        /// The CURRENT anchor config. This said FootballBase04.yaml long after that
        /// file moved to Config/archive/, and every test that reads it had been
        /// failing on "config not found" ever since — which is to say the four
        /// guards standing between the trainer config and the code contract were
        /// dead, silently, in exactly the way the class note above describes.
        /// Point it at whatever Config/ actually holds when the anchor moves.
        /// </summary>
        private const string CONFIG_FILE_NAME = "FootballBase06.yaml";

        /// <summary>
        /// The undershoot case, which the existing size test cannot see: the caller
        /// allocates the buffer, so asserting its Length only re-checks the
        /// constant. Pre-filling with NaN and demanding none survive is what
        /// actually proves the cursor wrote every slot.
        ///
        /// A short write is silent and poisonous — the tail of the vector holds
        /// whatever was in the reused buffer from the previous tick, so the policy
        /// trains on a stale observation that looks perfectly well-formed.
        /// </summary>
        [Test]
        public void Write_FillsEverySlotOfTheObservationVector()
        {
            foreach (Systems_PlayerRole role in Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                float[] buffer = new float[Sensor_FootballState.OBSERVATION_SIZE];
                for (int index = 0; index < buffer.Length; index++)
                {
                    buffer[index] = float.NaN;
                }

                Sensor_FootballState.Write(
                    buffer, new Systems_FieldModel(), role,
                    new Vector2(3f, -8f), new Vector2(1f, 2f), 45f, 0.3f, false,
                    new Vector2(-2f, 5f), new Vector2(0f, 9f),
                    Systems_BallState.InFlight, Systems_PlayCall.Pass, 4f);

                for (int index = 0; index < buffer.Length; index++)
                {
                    Assert.That(
                        float.IsNaN(buffer[index]), Is.False,
                        $"observation {index} was never written for role {role}");
                }
            }
        }

        /// <summary>
        /// And the overshoot case. A cursor that runs past the end throws
        /// IndexOutOfRange against an exactly-sized buffer, which is loud; given a
        /// larger buffer it would write silently past the declared size and the
        /// extra values would simply never reach the network.
        /// </summary>
        [Test]
        public void Write_DoesNotWritePastTheDeclaredObservationSize()
        {
            const int padding = 8;
            float[] buffer = new float[Sensor_FootballState.OBSERVATION_SIZE + padding];

            for (int index = 0; index < buffer.Length; index++)
            {
                buffer[index] = float.NaN;
            }

            Sensor_FootballState.Write(
                buffer, new Systems_FieldModel(), Systems_PlayerRole.Quarterback,
                Vector2.zero, Vector2.zero, 0f, 0f, true,
                Vector2.zero, Vector2.zero, Systems_BallState.Held,
                Systems_PlayCall.KeepQuarterback, 0f);

            for (int index = Sensor_FootballState.OBSERVATION_SIZE; index < buffer.Length; index++)
            {
                Assert.That(
                    float.IsNaN(buffer[index]), Is.True,
                    $"observation {index} was written past OBSERVATION_SIZE");
            }
        }

        // --- Action contract ---------------------------------------------------

        /// <summary>
        /// Exactly one brain carries discrete actions, and it is the quarterback's.
        /// </summary>
        [Test]
        public void OnlyTheQuarterbackBrain_HasDiscreteActions()
        {
            foreach (Systems_BrainGroup group in Enum.GetValues(typeof(Systems_BrainGroup)))
            {
                ActionSpec spec = Agent_ActionContract.For(group);
                bool isQuarterback = group == Systems_BrainGroup.Quarterback;

                Assert.That(
                    spec.NumDiscreteActions, Is.EqualTo(isQuarterback ? 2 : 0),
                    $"{group} discrete branch count");
                Assert.That(
                    spec.NumContinuousActions,
                    Is.EqualTo(isQuarterback
                        ? Agent_ActionContract.QUARTERBACK_CONTINUOUS_ACTIONS
                        : Agent_ActionContract.BASE_CONTINUOUS_ACTIONS),
                    $"{group} continuous action count");
            }
        }

        /// <summary>
        /// The guard that replaced a timing heuristic. Branch index 0 must decode
        /// to Systems_PlayCall.None, and LatchCall must refuse it — that is what
        /// makes a zeroed action buffer incapable of committing the offense to a
        /// play it never chose.
        /// </summary>
        [Test]
        public void PlayCallBranchIndexZero_DecodesToNoCallAndCannotBeLatched()
        {
            Assert.That((Systems_PlayCall)0, Is.EqualTo(Systems_PlayCall.None));

            Systems_PlayModel play = new Systems_PlayModel();
            play.BeginEpisode(0f, 0f);
            play.Snap();

            play.LatchCall((Systems_PlayCall)0);

            Assert.That(
                play.CallIsLatched, Is.False,
                "a zeroed action buffer latched a play call");
        }

        /// <summary>
        /// Every other index must decode to a real call, or part of the branch is
        /// unreachable and the policy is exploring options that do nothing.
        /// </summary>
        [Test]
        public void EveryPlayCallBranchIndex_DecodesToADistinctCall()
        {
            Assert.That(
                Sensor_FootballState.PLAY_CALL_BRANCH_SIZE,
                Is.EqualTo(Enum.GetValues(typeof(Systems_PlayCall)).Length),
                "the branch is not the same width as the play-call enum");

            for (int index = 1; index < Sensor_FootballState.PLAY_CALL_BRANCH_SIZE; index++)
            {
                Systems_PlayCall call = (Systems_PlayCall)index;

                Assert.That(
                    Enum.IsDefined(typeof(Systems_PlayCall), call), Is.True,
                    $"branch index {index} decodes to no defined call");

                Systems_PlayModel play = new Systems_PlayModel();
                play.BeginEpisode(0f, 0f);
                play.Snap();
                play.LatchCall(call);

                Assert.That(play.Call, Is.EqualTo(call), $"branch index {index} did not latch");
            }
        }

        /// <summary>
        /// The observation one-hot covers the REAL calls only, so it is one narrower
        /// than the branch. Getting this backwards silently shifts every observation
        /// after it.
        /// </summary>
        [Test]
        public void ThePlayCallOneHot_IsOneNarrowerThanTheBranch()
        {
            Assert.That(
                Sensor_FootballState.PLAY_CALL_SLOTS,
                Is.EqualTo(Sensor_FootballState.PLAY_CALL_BRANCH_SIZE - 1));
        }

        /// <summary>
        /// A latched call is final. The quarterback reads the defensive alignment,
        /// commits, and lives with it — and OnActionReceived calls LatchCall on
        /// every subsequent step, so this is load-bearing rather than decorative.
        /// </summary>
        [Test]
        public void ALatchedCall_IgnoresEverySubsequentCall()
        {
            Systems_PlayModel play = new Systems_PlayModel();
            play.BeginEpisode(0f, 0f);
            play.Snap();

            play.LatchCall(Systems_PlayCall.Pass);
            play.LatchCall(Systems_PlayCall.HandoffFullback);
            play.LatchCall(Systems_PlayCall.KeepQuarterback);

            Assert.That(play.Call, Is.EqualTo(Systems_PlayCall.Pass));
        }

        // --- Trainer config ----------------------------------------------------

        /// <summary>
        /// The [-1, 1] guarantee in Sensor_FootballState is only worth anything
        /// while the trainer is told not to normalize on top of it. Running
        /// normalization over self-play fights the shifting input distribution
        /// (acceptance criterion #14), and the two settings live in different files
        /// with nothing connecting them.
        /// </summary>
        [Test]
        public void TrainerConfig_LeavesNormalizationOff()
        {
            string yaml = ReadTrainerConfig();

            Assert.That(
                yaml.Contains("normalize: false"), Is.True,
                "the config enables normalization on observations that are already in [-1, 1]");
            Assert.That(
                yaml.Contains("normalize: true"), Is.False,
                "at least one behavior normalizes pre-normalized observations");
        }

        /// <summary>
        /// time_horizon must cover a whole play, or credit for a touchdown cannot
        /// reach back to the play call that set it up — which is the one decision
        /// the quarterback's brain exists to make.
        /// </summary>
        [Test]
        public void TrainerConfig_HorizonCoversAWholePlay()
        {
            string yaml = ReadTrainerConfig();

            Assert.That(
                yaml.Contains($"time_horizon: {Systems_PlayModel.MAX_DECISIONS}"), Is.True,
                $"time_horizon must be {Systems_PlayModel.MAX_DECISIONS} to span a full play "
                + $"({Systems_PlayModel.MAX_PHYSICS_TICKS} ticks at DecisionPeriod "
                + $"{Systems_SimConstants.DECISION_PERIOD})");
        }

        /// <summary>
        /// The dropback has to fit inside the throw window, and inside the play.
        ///
        /// These are three independent constants that only mean anything relative to
        /// each other. If DROPBACK_TICKS ever reached THROW_WINDOW_TICKS the
        /// quarterback would be forbidden from throwing on the exact tick it was
        /// first allowed to decide, and every pass call would silently become a
        /// scramble — a dead branch of the action space that nothing would report.
        /// </summary>
        [Test]
        public void Dropback_EndsWellInsideTheThrowWindow()
        {
            Assert.That(
                Systems_SimConstants.DROPBACK_TICKS,
                Is.LessThan(Systems_SimConstants.THROW_WINDOW_TICKS),
                "the call must latch before the throw window shuts, or Pass is unreachable");

            Assert.That(
                Systems_SimConstants.DROPBACK_TICKS,
                Is.LessThan(Systems_PlayModel.MAX_PHYSICS_TICKS),
                "the call must latch before the play is force-ended");

            // A call latched on the very first decision step is the behaviour the
            // dropback exists to replace, so the window has to be at least one
            // decision long or nothing has changed.
            Assert.That(
                Systems_SimConstants.DROPBACK_TICKS,
                Is.GreaterThanOrEqualTo(Systems_SimConstants.DECISION_PERIOD),
                "a dropback shorter than one decision period never defers anything");
        }

        /// <summary>
        /// The ball's fake vertical axis must return to the turf on its own.
        ///
        /// Height is integrated, not interpolated over a known flight length, so a
        /// non-positive gravity would leave a thrown ball climbing forever and the
        /// shadow pinned at its smallest for the rest of the game.
        /// </summary>
        [Test]
        public void PassArc_FallsBackToTheGround()
        {
            Assert.That(Systems_SimConstants.PASS_GRAVITY, Is.GreaterThan(0f));
            Assert.That(Systems_SimConstants.PASS_LOFT_RATIO, Is.GreaterThan(0f));

            Systems_BallModel ball = new Systems_BallModel();
            ball.Throw(0, Vector2.zero, new Vector2(0f, Systems_SimConstants.PASS_SPEED_MAX));

            Assert.That(ball.Height, Is.EqualTo(0f), "a ball leaves the hand at ground level");

            float peak = 0f;
            for (int tick = 0; tick < Systems_SimConstants.MAX_FLIGHT_TICKS; tick++)
            {
                ball.AdvanceFlight(Time.fixedDeltaTime);
                peak = Mathf.Max(peak, ball.Height);
            }

            Assert.That(peak, Is.GreaterThan(1f), "a pass has to clear the players it passes over");
            Assert.That(
                ball.Height, Is.EqualTo(0f),
                "the ball must be back on the ground by MAX_FLIGHT_TICKS, when the "
                + "pass is ruled incomplete");
        }

        private static string ReadTrainerConfig()
        {
            string configPath = Path.Combine(
                Application.dataPath, "..", "Config", CONFIG_FILE_NAME);

            Assert.That(File.Exists(configPath), Is.True, $"config not found at {configPath}");

            return File.ReadAllText(configPath);
        }
    }
}
