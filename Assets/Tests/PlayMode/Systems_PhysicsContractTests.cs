using System.Collections;
using NUnit.Framework;
using PoFootball.Models;
using UnityEngine;
using UnityEngine.TestTools;

namespace PoFootball.Tests
{
    /// <summary>
    /// Guards the dynamics contract every promoted .onnx was fitted against.
    ///
    /// These cannot be EditMode tests: nothing here is a pure function. They assert
    /// what the physics loop actually does over real ticks, which is the only place
    /// a changed project setting or solver override shows up. CLAUDE.md section 2
    /// makes the point that an override "silently changes the dynamics every brain
    /// was fitted against" — silently is the problem, and this is the alarm.
    ///
    /// A failure here does not mean the test is wrong. It means every existing
    /// brain is now being evaluated against different physics than it learned on,
    /// and the runs are no longer comparable.
    ///
    /// UnityTest requires IEnumerator. That is the Unity Test Framework's API, not
    /// gameplay code, so the no-coroutines rule in .claude/rules/unity-specifics.md
    /// does not apply.
    /// </summary>
    public sealed class Systems_PhysicsContractTests
    {
        private const float FIXED_DELTA_TIME = 0.02f;

        private GameObject _body;

        [TearDown]
        public void TearDown()
        {
            if (_body != null)
            {
                Object.Destroy(_body);
                _body = null;
            }
        }

        /// <summary>
        /// The pinned tick. Everything below depends on it, and so does every
        /// .onnx in Assets/Agents/.
        /// </summary>
        [Test]
        public void FixedDeltaTime_IsPinnedAt50Hz()
        {
            Assert.That(
                Time.fixedDeltaTime,
                Is.EqualTo(FIXED_DELTA_TIME).Within(1e-4f),
                "Time.fixedDeltaTime moved off 0.02. Every promoted brain was fitted "
                + "at 50 Hz and is now being evaluated against different dynamics.");
        }

        /// <summary>
        /// DECISION_PERIOD is expressed in physics ticks, so its meaning in seconds
        /// only holds while the tick is pinned. Acceptance criterion #15 is 10
        /// decisions per second.
        /// </summary>
        [Test]
        public void DecisionPeriod_YieldsTenDecisionsPerSecond()
        {
            float decisionsPerSecond = 1f / (FIXED_DELTA_TIME * Systems_SimConstants.DECISION_PERIOD);

            Assert.That(decisionsPerSecond, Is.EqualTo(10f).Within(1e-3f));
        }

        /// <summary>
        /// Physics2D must be driven by FixedUpdate. Under Script simulation mode
        /// nothing steps unless something calls Physics2D.Simulate, so actions
        /// applied in FixedUpdate would land on a world that never advances.
        /// </summary>
        [Test]
        public void Physics2D_StepsFromTheFixedUpdateLoop()
        {
            Assert.That(Physics2D.simulationMode, Is.EqualTo(SimulationMode2D.FixedUpdate));
        }

        /// <summary>
        /// Full drive against LINEAR_DAMPING settles at a = F/m over d, i.e.
        /// (900 / 100) / 1.5 = 6 m/s. This is the number the policies' sense of
        /// distance and closing speed is built on — TACKLE_CLOSING_SPEED of
        /// 1.5 m/s is only a meaningful threshold relative to it.
        ///
        /// The body is configured exactly as Agent_FootballPlayer.ConfigureBody
        /// does, deliberately by hand: the point is to pin the dynamics those
        /// constants produce, not to re-test the method that applies them.
        /// </summary>
        [UnityTest]
        public IEnumerator FullDrive_SettlesAtSixMetresPerSecond()
        {
            Rigidbody2D rigidbody = CreateConfiguredBody();

            // 6 s at 50 Hz. The response is first-order with a time constant of
            // 1/damping = 0.67 s, so this is nine time constants — the residual is
            // ~1e-4 m/s, well inside the tolerance below. Three seconds is not
            // enough: it lands at 5.93, which reads as a dynamics change.
            for (int tick = 0; tick < 300; tick++)
            {
                rigidbody.AddForce(rigidbody.transform.up * Systems_SimConstants.DRIVE_FORCE);
                yield return new WaitForFixedUpdate();
            }

            float expected = Systems_SimConstants.DRIVE_FORCE
                / Systems_SimConstants.PLAYER_MASS
                / Systems_SimConstants.LINEAR_DAMPING;

            Assert.That(rigidbody.linearVelocity.magnitude, Is.EqualTo(expected).Within(0.05f));
        }

        /// <summary>
        /// MAX_BODY_SPEED is a pileup-explosion guard (criterion #13), not a
        /// throttle on normal running. If terminal speed ever reaches it, the
        /// clamp starts firing every tick during ordinary play and quietly becomes
        /// part of the dynamics.
        /// </summary>
        [Test]
        public void TerminalSpeed_LeavesHeadroomUnderThePileupClamp()
        {
            float terminalSpeed = Systems_SimConstants.DRIVE_FORCE
                / Systems_SimConstants.PLAYER_MASS
                / Systems_SimConstants.LINEAR_DAMPING;

            Assert.That(
                terminalSpeed,
                Is.LessThan(Systems_SimConstants.MAX_BODY_SPEED),
                "Terminal speed under full drive reached the pileup clamp. The clamp "
                + "is now shaping every play, not just collisions.");
        }

        /// <summary>
        /// Characterizes the fatigue tuning, which does not currently bite.
        ///
        /// Agent_FootballPlayer.AccumulateFatigue integrates gain and recovery
        /// against each other every tick, so fatigue only rises while
        /// load * FATIGUE_GAIN_PER_NEWTON_SECOND exceeds FATIGUE_RECOVERY_PER_SECOND.
        /// That break-even sits at 0.06 / 1.2e-5 = 5000 N, but the largest load the
        /// simulation can produce is DRIVE_FORCE + STEER_TORQUE = 1200 N — about a
        /// quarter of it. `_fatigue` is therefore clamped at 0 for every player in
        /// every play, and FATIGUE_MAX_PENALTY never scales anything.
        ///
        /// This is asserted rather than fixed on purpose: raising the gain changes
        /// the dynamics every promoted .onnx was fitted against, which is a
        /// retraining decision, not a test fix. The test fails the moment either
        /// constant moves — which is exactly when someone is making that decision.
        /// </summary>
        [Test]
        public void Fatigue_BreakEvenLoadIsAboveAnythingTheSimulationProduces()
        {
            float breakEvenLoad = Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND
                / Systems_SimConstants.FATIGUE_GAIN_PER_NEWTON_SECOND;

            float maximumLoad = Systems_SimConstants.DRIVE_FORCE + Systems_SimConstants.STEER_TORQUE;

            Assert.That(breakEvenLoad, Is.EqualTo(5000f).Within(1f));
            Assert.That(maximumLoad, Is.EqualTo(1200f).Within(1f));
            Assert.That(
                maximumLoad,
                Is.LessThan(breakEvenLoad),
                "Fatigue now accumulates. That is the intended behaviour, but it is a "
                + "dynamics change: every brain in Assets/Agents/ was fitted without it.");
        }

        private Rigidbody2D CreateConfiguredBody()
        {
            _body = new GameObject("PhysicsContractBody");
            Rigidbody2D rigidbody = _body.AddComponent<Rigidbody2D>();

            rigidbody.gravityScale = 0f;
            rigidbody.mass = Systems_SimConstants.PLAYER_MASS;
            rigidbody.linearDamping = Systems_SimConstants.LINEAR_DAMPING;
            rigidbody.angularDamping = Systems_SimConstants.ANGULAR_DAMPING;
            rigidbody.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            rigidbody.interpolation = RigidbodyInterpolation2D.None;
            rigidbody.sleepMode = RigidbodySleepMode2D.NeverSleep;
            rigidbody.constraints = RigidbodyConstraints2D.None;

            return rigidbody;
        }
    }
}
