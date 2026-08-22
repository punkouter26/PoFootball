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
        /// Full drive settles at exactly the role's advertised top speed. This is
        /// the assertion that the drive-force derivation actually holds against
        /// Unity's integrator rather than only on paper.
        ///
        /// Before base04 this test settled at 6.0 m/s for EVERY role, because
        /// drive force and mass were single shared constants. TopSpeedOf's 9.6 /
        /// 7.5 / 6.5 m/s were unreachable and lived only as an observation
        /// normalizer, so a receiver and a guard were the same body.
        ///
        /// The body is configured exactly as Agent_FootballPlayer.ConfigureBody
        /// does, deliberately by hand: the point is to pin the dynamics those
        /// constants produce, not to re-test the method that applies them.
        /// </summary>
        [UnityTest]
        public IEnumerator FullDrive_SettlesAtTheRoleTopSpeed(
            [Values(
                Systems_PlayerRole.WideReceiver,
                Systems_PlayerRole.Fullback,
                Systems_PlayerRole.OffensiveLine)]
            Systems_PlayerRole role)
        {
            Rigidbody2D rigidbody = CreateConfiguredBody(role);
            float driveForce = Systems_RoleTable.DriveForceOf(role);

            // 12 s at 50 Hz. The response is first-order with a time constant of
            // 1/LINEAR_DAMPING, so this is 9.6 time constants — the residual is
            // ~7e-4 m/s, well inside the tolerance below.
            //
            // THE WINDOW IS DERIVED FROM THE DAMPING, NOT TYPED IN. It was a flat
            // 300 ticks against the pre-revision-3 damping of 1.5 (tau = 0.67 s),
            // where 6 s really was nine time constants. Revision 3 dropped damping
            // to 0.8 and nearly doubled tau to 1.25 s without moving this loop, so
            // 6 s became 4.8 time constants and left a 0.8% residual — every role
            // landed just outside the tolerance and the failure read as a dynamics
            // regression when the dynamics were exactly right. Computing the tick
            // count keeps the two in step the next time damping moves.
            int settleTicks = SettleTicks();

            for (int tick = 0; tick < settleTicks; tick++)
            {
                rigidbody.AddForce(rigidbody.transform.up * driveForce);
                yield return new WaitForFixedUpdate();
            }

            Assert.That(
                rigidbody.linearVelocity.magnitude,
                Is.EqualTo(Systems_RoleTable.TopSpeedOf(role)).Within(0.05f),
                $"{role} does not reach the speed Systems_RoleTable advertises for it.");
        }

        /// <summary>
        /// SteerTorqueOf derives its torque from I = 0.5 * m * r^2, the moment of
        /// inertia of a uniform disc. That is an assumption about what Unity
        /// computes for a circle collider, not something the C# can observe, so it
        /// is pinned here — if Unity ever computes it differently, every role's
        /// turn rate is wrong by that factor and nothing else would say so.
        /// </summary>
        [UnityTest]
        public IEnumerator RotationalInertia_MatchesTheUniformDiscTheTorqueIsDerivedFrom()
        {
            Rigidbody2D rigidbody = CreateConfiguredBody(Systems_PlayerRole.Linebacker);

            // Inertia is recomputed from the colliders during the physics step.
            yield return new WaitForFixedUpdate();

            float expected = 0.5f * Systems_RoleTable.MassOf(Systems_PlayerRole.Linebacker)
                * Systems_SimConstants.PLAYER_RADIUS * Systems_SimConstants.PLAYER_RADIUS;

            Assert.That(
                rigidbody.inertia, Is.EqualTo(expected).Within(0.01f),
                "Systems_RoleTable.SteerTorqueOf derives torque from a uniform-disc "
                + "moment of inertia that Unity does not agree with.");
        }

        /// <summary>
        /// Full steer settles at exactly the role's advertised turn rate — the
        /// rotational half of the same derivation, which is easy to get wrong
        /// because the moment of inertia moves with mass and the mass is now
        /// per role.
        /// </summary>
        [UnityTest]
        public IEnumerator FullSteer_SettlesAtTheRoleTurnRate(
            [Values(Systems_PlayerRole.Cornerback, Systems_PlayerRole.DefensiveLine)]
            Systems_PlayerRole role)
        {
            Rigidbody2D rigidbody = CreateConfiguredBody(role);
            float steerTorque = Systems_RoleTable.SteerTorqueOf(role);

            for (int tick = 0; tick < 300; tick++)
            {
                rigidbody.AddTorque(steerTorque);
                yield return new WaitForFixedUpdate();
            }

            // Rigidbody2D.angularVelocity is degrees per second; TurnRateOf is rad/s.
            float radiansPerSecond = rigidbody.angularVelocity * Mathf.Deg2Rad;

            Assert.That(
                radiansPerSecond,
                Is.EqualTo(Systems_RoleTable.TurnRateOf(role)).Within(0.05f),
                $"{role} does not turn at the rate Systems_RoleTable advertises for it.");
        }

        /// <summary>
        /// MAX_BODY_SPEED is a pileup-explosion guard (criterion #13), not a
        /// throttle on normal running. If any role's terminal speed reaches it, the
        /// clamp starts firing every tick during ordinary play and quietly becomes
        /// part of the dynamics.
        /// </summary>
        [Test]
        public void EveryRoleTerminalSpeed_LeavesHeadroomUnderThePileupClamp()
        {
            foreach (Systems_PlayerRole role in System.Enum.GetValues(typeof(Systems_PlayerRole)))
            {
                float terminalSpeed = Systems_RoleTable.DriveForceOf(role)
                    / Systems_RoleTable.MassOf(role)
                    / Systems_SimConstants.LINEAR_DAMPING;

                Assert.That(
                    terminalSpeed,
                    Is.LessThan(Systems_SimConstants.MAX_BODY_SPEED),
                    $"{role}'s terminal speed under full drive reached the pileup clamp. "
                    + "The clamp is now shaping every play, not just collisions.");
            }
        }

        /// <summary>
        /// Fatigue must be able to accumulate under loads the simulation actually
        /// produces. Load is the applied force over the role's own maximum plus the
        /// applied torque over its own maximum, so it spans [0, 2] and the
        /// break-even point is RECOVERY / GAIN.
        ///
        /// This assertion is the inverse of the one it replaces. The previous pair
        /// of constants put break-even at 0.06 / 1.2e-5 = 5000 against a maximum
        /// producible load of DRIVE_FORCE + STEER_TORQUE = 1200 N — a quantity that
        /// was not even dimensionally meaningful, being newtons added to
        /// newton-metres. `_fatigue` was pinned at 0 for every player in every play
        /// of base01 through base03: the observation slot was constant,
        /// FATIGUE_MAX_PENALTY never scaled anything, and the fatigue shader
        /// parameter never moved. The old test asserted that as a characterization,
        /// deferring the fix because it changes the dynamics every promoted brain
        /// was fitted against. base04 is that retraining.
        /// </summary>
        [Test]
        public void Fatigue_AccumulatesUnderLoadsTheSimulationProduces()
        {
            const float maximumLoad = 2f;

            float breakEvenLoad = Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND
                / Systems_SimConstants.FATIGUE_GAIN_PER_UNIT_LOAD;

            Assert.That(
                breakEvenLoad,
                Is.LessThan(maximumLoad),
                "Fatigue can never accumulate: recovery outruns the gain at every load "
                + "the simulation can produce, so the fatigue observation is a constant.");

            // Sustained full effort must also be worth something over one play, not
            // merely non-zero. A play is capped at MAX_PHYSICS_TICKS ticks.
            float playSeconds = Systems_PlayModel.MAX_PHYSICS_TICKS * FIXED_DELTA_TIME;
            float netPerSecond = (maximumLoad * Systems_SimConstants.FATIGUE_GAIN_PER_UNIT_LOAD)
                - Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND;

            Assert.That(
                netPerSecond * playSeconds,
                Is.GreaterThan(0.25f),
                "Fatigue accumulates but too slowly to reach a meaningful level within "
                + "a single play, and it is cleared at every episode boundary.");
        }

        /// <summary>
        /// Recovery must not be so strong that ordinary running is free, nor so
        /// weak that a player who lets off never recovers within a play.
        /// </summary>
        [Test]
        public void Fatigue_CruisingIsCheaperThanCutting()
        {
            const float cruisingLoad = 1f;
            const float cuttingLoad = 2f;

            float cruising = (cruisingLoad * Systems_SimConstants.FATIGUE_GAIN_PER_UNIT_LOAD)
                - Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND;
            float cutting = (cuttingLoad * Systems_SimConstants.FATIGUE_GAIN_PER_UNIT_LOAD)
                - Systems_SimConstants.FATIGUE_RECOVERY_PER_SECOND;

            Assert.That(cutting, Is.GreaterThan(cruising * 2f));
        }

        /// <summary>
        /// Fixed-update ticks needed for a first-order drive response to settle to
        /// within a ten-thousandth of its terminal speed, derived from the damping
        /// that sets the time constant rather than hard-coded against one value of
        /// it. tau = 1 / LINEAR_DAMPING; SETTLE_TIME_CONSTANTS of those leave a
        /// residual of e^-9.6, about 7e-4 of top speed.
        /// </summary>
        private static int SettleTicks()
        {
            const float SETTLE_TIME_CONSTANTS = 9.6f;

            float timeConstant = 1f / Systems_SimConstants.LINEAR_DAMPING;
            float seconds = SETTLE_TIME_CONSTANTS * timeConstant;

            return Mathf.CeilToInt(seconds / Time.fixedDeltaTime);
        }

        private Rigidbody2D CreateConfiguredBody(Systems_PlayerRole role)
        {
            _body = new GameObject("PhysicsContractBody");
            Rigidbody2D rigidbody = _body.AddComponent<Rigidbody2D>();

            // The collider is not decoration. Rigidbody2D derives its rotational
            // inertia from the attached colliders, and a body with none is given a
            // flat inertia of 1 regardless of its mass — under which every role
            // turns at torque/damping and SteerTorqueOf's derivation appears to be
            // wrong by exactly a factor of the moment of inertia. The players in
            // the scene carry a CircleCollider2D of this radius.
            CircleCollider2D collider = _body.AddComponent<CircleCollider2D>();
            collider.radius = Systems_SimConstants.PLAYER_RADIUS;

            rigidbody.gravityScale = 0f;
            rigidbody.mass = Systems_RoleTable.MassOf(role);
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

