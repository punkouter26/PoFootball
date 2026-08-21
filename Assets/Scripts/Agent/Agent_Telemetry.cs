using System;
using MessagePipe;
using PoFootball.Sensors;
using PoFootball.Models;
using PoFootball.Systems;
using Unity.MLAgents;
using UnityEngine;
using VContainer;

namespace PoFootball.Agents
{
    /// <summary>
    /// Emits the outcome distribution of every play to TensorBoard, alongside the
    /// ELO curves.
    ///
    /// This exists because run football_base01 could not be diagnosed. Mean reward
    /// said the offense was gaining a great deal of ground, but nothing recorded
    /// how plays actually ended, so the cause had to be inferred from the reward
    /// scale rather than measured. `Play/TackleRate` answers directly whether the
    /// wrap-up tackle rule works, and `Play/TimeExpiredRate` is acceptance
    /// criterion #23 — the test of whether the do-nothing optimum was escaped.
    ///
    /// StatsRecorder averages within a summary window, so writing 1 for the
    /// outcome that occurred and 0 for the others turns each series into a rate.
    /// The corollary is used deliberately below: a series that is written ONLY on
    /// the plays where its denominator applies averages to a conditional rate.
    ///
    /// This is the StatsRecorder half of CLAUDE.md section 4. The HTTP half is
    /// still not built.
    /// </summary>
    /// <remarks>
    /// WHY IT LIVES HERE AND WHY IT IS A MonoBehaviour. This was a plain C# class
    /// in PoFootball.Systems, registered as an entry point by the lifetime scope —
    /// and it was the only reason the core gameplay assembly referenced
    /// Unity.ML-Agents at all. One call to Academy.Instance put the trainer inside
    /// the assembly that is supposed to know nothing about training, and shipped
    /// ML-Agents into a retail build's gameplay code.
    ///
    /// It is now a scene component in PoFootball.Agents, which already depends on
    /// ML-Agents, and it reaches the container through
    /// Systems_IInjectableBehaviour — the same inversion Agent_FootballPlayer uses.
    /// Put it in SCN_TRAIN_FOOTBALL only: a played game has no trainer to report
    /// to, and its absence is exactly how it should be switched off.
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class Agent_Telemetry : MonoBehaviour, Systems_IInjectableBehaviour
    {
        /// <summary>
        /// Plays retained in the rolling histogram behind `Call/Entropy`. Wide
        /// enough that the entropy estimate is not dominated by sampling noise,
        /// short enough to show a collapse forming rather than the run average.
        /// </summary>
        private const int CALL_WINDOW_PLAYS = 200;

        /// <summary>
        /// Width of the call histogram: every real call plus None.
        ///
        /// DERIVED, NOT TYPED. This was the literal 5 — the four calls of the day
        /// plus None — and when Punt and FieldGoal were added it silently became
        /// wrong in the worst possible way: RecordCall drops any index at or past
        /// this bound, so both new calls vanished from the histogram entirely.
        /// `Call/Entropy` kept reporting a healthy number computed over the old five
        /// while the two calls the whole contract revision existed to introduce were
        /// invisible. Reading it off Sensor_FootballState.PLAY_CALL_BRANCH_SIZE means
        /// the histogram widens with the branch, by construction.
        /// </summary>
        private const int CALL_SLOT_COUNT = Sensor_FootballState.PLAY_CALL_BRANCH_SIZE;

        private ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;

        /// <summary>
        /// Rolling histogram of latched calls, indexed by Systems_PlayCall. Slot 0
        /// (None) counts plays where the quarterback never committed to anything,
        /// which is itself a failure worth seeing.
        /// </summary>
        private readonly int[] _callCounts = new int[CALL_SLOT_COUNT];
        private readonly Systems_PlayCall[] _callWindow =
            new Systems_PlayCall[CALL_WINDOW_PLAYS];

        private int _callWindowCursor;
        private int _callWindowFilled;

        private IDisposable _subscription;

        [Inject]
        public void Construct(ISubscriber<Systems_PlayEndedMessage> endedSubscriber)
        {
            _endedSubscriber = endedSubscriber;
        }

        /// <summary>
        /// Start rather than the constructor, for the reason Systems_EpisodeDirector
        /// documents: a handler registered during container build is live before the
        /// rest of the graph exists.
        /// </summary>
        private void Start()
        {
            _subscription = _endedSubscriber?.Subscribe(OnPlayEnded);
        }

        private void OnDestroy()
        {
            _subscription?.Dispose();
        }

        private void OnPlayEnded(Systems_PlayEndedMessage message)
        {
            StatsRecorder stats = Academy.Instance.StatsRecorder;

            stats.Add("Play/TackleRate", Rate(message.Outcome, Systems_PlayOutcome.Tackle));
            stats.Add("Play/TouchdownRate", Rate(message.Outcome, Systems_PlayOutcome.Touchdown));
            stats.Add("Play/OutOfBoundsRate", Rate(message.Outcome, Systems_PlayOutcome.OutOfBounds));
            stats.Add("Play/TimeExpiredRate", Rate(message.Outcome, Systems_PlayOutcome.TimeExpired));
            stats.Add("Play/IncompletionRate", Rate(message.Outcome, Systems_PlayOutcome.Incompletion));
            stats.Add("Play/InterceptionRate", Rate(message.Outcome, Systems_PlayOutcome.Interception));

            stats.Add("Play/NetYards", message.NetYards);
            stats.Add("Play/LengthTicks", message.PhysicsTicks);

            // What the quarterback actually chose. Without this a policy that
            // collapses to always calling the same play is invisible — every
            // outcome rate still looks plausible while three quarters of the
            // playbook goes unused.
            stats.Add("Call/None", CallRate(message.Call, Systems_PlayCall.None));
            stats.Add("Call/Keep", CallRate(message.Call, Systems_PlayCall.KeepQuarterback));
            stats.Add("Call/HandoffFullback", CallRate(message.Call, Systems_PlayCall.HandoffFullback));
            stats.Add("Call/HandoffHalfback", CallRate(message.Call, Systems_PlayCall.HandoffHalfback));
            stats.Add("Call/Pass", CallRate(message.Call, Systems_PlayCall.Pass));

            // The two calls contract revision 6 exists to introduce. Without these
            // a thirty-hour run produces no evidence at all about whether the
            // quarterback learned when to kick.
            stats.Add("Call/Punt", CallRate(message.Call, Systems_PlayCall.Punt));
            stats.Add("Call/FieldGoal", CallRate(message.Call, Systems_PlayCall.FieldGoal));

            RecordCall(message.Call);
            stats.Add("Call/Entropy", CallEntropy());

            RecordPassing(stats, message);
        }

        /// <summary>
        /// Passing rates conditioned on the quarterback having actually called a
        /// pass. Unconditioned they are uninterpretable and actively misleading:
        /// `Play/CompletionRate` used to be written on EVERY play, so a policy that
        /// threw twice in a thousand downs and completed both reported 0.002 —
        /// indistinguishable from one that threw constantly and completed nothing.
        ///
        /// Written only on pass attempts, the same series averages to
        /// completions per attempt within the summary window. The attempt rate is
        /// published separately so the denominator is never a guess.
        /// </summary>
        private static void RecordPassing(StatsRecorder stats, Systems_PlayEndedMessage message)
        {
            bool isPassCall = message.Call == Systems_PlayCall.Pass;

            stats.Add("Pass/AttemptRate", isPassCall ? 1f : 0f);

            if (!isPassCall)
            {
                return;
            }

            stats.Add("Pass/CompletionPerAttempt", message.PassCompleted ? 1f : 0f);
            stats.Add(
                "Pass/InterceptionPerAttempt",
                message.Outcome == Systems_PlayOutcome.Interception ? 1f : 0f);
            stats.Add(
                "Pass/IncompletionPerAttempt",
                message.Outcome == Systems_PlayOutcome.Incompletion ? 1f : 0f);
        }

        private void RecordCall(Systems_PlayCall call)
        {
            int index = (int)call;
            if (index < 0 || index >= CALL_SLOT_COUNT)
            {
                return;
            }

            // Fixed-size ring: subtract whatever is being evicted, add the new one.
            // No allocation, and the window is a constant cost per play.
            if (_callWindowFilled == CALL_WINDOW_PLAYS)
            {
                _callCounts[(int)_callWindow[_callWindowCursor]]--;
            }
            else
            {
                _callWindowFilled++;
            }

            _callWindow[_callWindowCursor] = call;
            _callCounts[index]++;
            _callWindowCursor = (_callWindowCursor + 1) % CALL_WINDOW_PLAYS;
        }

        /// <summary>
        /// Shannon entropy of the recent play-call distribution, in nats.
        ///
        /// This is the metric that would have caught football_base03 on day one.
        /// The trainer's own `Policy/Entropy` sums across every action head at
        /// once — for the old shared OffenseSkill brain that meant four continuous
        /// outputs plus two discrete branches — so it sat at a healthy-looking 3.5
        /// while the play-call branch had collapsed to a single option on 93% of
        /// downs. Measured over the calls alone the collapse is unmissable: 0.33
        /// nats against a 1.61 maximum for five slots.
        /// </summary>
        private float CallEntropy()
        {
            if (_callWindowFilled == 0)
            {
                return 0f;
            }

            float total = _callWindowFilled;
            float entropy = 0f;

            for (int index = 0; index < CALL_SLOT_COUNT; index++)
            {
                int count = _callCounts[index];
                if (count <= 0)
                {
                    continue;
                }

                float probability = count / total;
                entropy -= probability * Mathf.Log(probability);
            }

            return entropy;
        }

        private static float Rate(Systems_PlayOutcome actual, Systems_PlayOutcome candidate)
        {
            return actual == candidate ? 1f : 0f;
        }

        private static float CallRate(Systems_PlayCall actual, Systems_PlayCall candidate)
        {
            return actual == candidate ? 1f : 0f;
        }
    }
}
