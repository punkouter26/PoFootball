using System;
using MessagePipe;
using PoFootball.Models;
using Unity.MLAgents;
using VContainer.Unity;

namespace PoFootball.Systems
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
    ///
    /// This is the StatsRecorder half of CLAUDE.md section 4. The HTTP half is
    /// still not built.
    /// </summary>
    public sealed class Systems_Telemetry : IStartable, IDisposable
    {
        private readonly ISubscriber<Systems_PlayEndedMessage> _endedSubscriber;
        private IDisposable _subscription;

        public Systems_Telemetry(ISubscriber<Systems_PlayEndedMessage> endedSubscriber)
        {
            _endedSubscriber = endedSubscriber;
        }

        public void Start()
        {
            _subscription = _endedSubscriber.Subscribe(OnPlayEnded);
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
            stats.Add("Call/Keep", CallRate(message.Call, Systems_PlayCall.KeepQuarterback));
            stats.Add("Call/HandoffFullback", CallRate(message.Call, Systems_PlayCall.HandoffFullback));
            stats.Add("Call/HandoffHalfback", CallRate(message.Call, Systems_PlayCall.HandoffHalfback));
            stats.Add("Call/Pass", CallRate(message.Call, Systems_PlayCall.Pass));

            // Completion rate is only meaningful read against Call/Pass — this
            // counts completions over ALL plays, not over pass attempts.
            stats.Add("Play/CompletionRate", message.PassCompleted ? 1f : 0f);
        }

        private static float Rate(Systems_PlayOutcome actual, Systems_PlayOutcome candidate)
        {
            return actual == candidate ? 1f : 0f;
        }

        private static float CallRate(Systems_PlayCall actual, Systems_PlayCall candidate)
        {
            return actual == candidate ? 1f : 0f;
        }

        public void Dispose()
        {
            _subscription?.Dispose();
        }
    }
}
