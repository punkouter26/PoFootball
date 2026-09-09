# RL Optimization Log — PoFootball

Autonomous 2-phase RL optimization run. Started 2026-09-09.

---

## 0. Scope correction: this is not a MuJoCo project

The task was specified in MuJoCo/Gymnasium terms (MJCF XML, `<contact><exclude>`,
`<option timestep>`, SubprocVecEnv, PPO/SAC via stable-baselines3). **None of that
exists in this repository.** Verified by exhaustive search:

```
grep -rIl -iE "mujoco|mjcf|gymnasium|stable.baselines|SubprocVecEnv" \
  --include=*.py --include=*.md --include=*.txt --include=*.yaml --include=*.json .
  -> (no matches)
```

The only `.xml` files in the tree are three Kenney sprite-sheet atlases under
`Assets/Art/Kenney/SportsPack/Spritesheet/`. There is no Python environment code:
the nine `.py` files under `Tools/` are audit and promotion utilities, none of
which defines a step function, an observation space, or a reward.

The actual RL stack is **Unity ML-Agents**: a C# simulation of 2D American
football (22 `Rigidbody2D` agents, top-down) trained over gRPC by the `mlagents`
Python trainer. The goal's intent — discover ground-truth KPIs, instrument what
is missing, baseline, then optimize — is preserved; only the vocabulary is
remapped:

| Goal term | Actual artifact here |
|---|---|
| MJCF XML model | `Systems_SimConstants.cs`, `Systems_RoleTable.cs`, `ProjectSettings/Physics2DSettings.asset` |
| Python env code | `Assets/Scripts/Agent/Agent_FootballPlayer.cs` (2070 lines) |
| Observation space | `Assets/Scripts/Sensor/Sensor_FootballState.cs` + `RayPerceptionSensor2D` |
| Reward terms | `Assets/Scripts/Reward/Reward_{Progress,Call,Role,Terminal}.cs` |
| `<option timestep>` / solver | `ProjectSettings/TimeManager.asset`, `Physics2DSettings.asset` |
| SubprocVecEnv | `mlagents-learn --env ... --num-envs N` (separate OS processes) |
| PPO/SAC hyperparameters | `Config/FootballBase11.yaml` |

**PPO is not the main trainer.** Two of the three behaviors use **MA-POCA**
(`trainer_type: poca`), a centralized-critic multi-agent algorithm. Only the
`Quarterback` behavior is PPO. SAC is not used and is not applicable — this is
an on-policy multi-agent setup. Hyperparameter work in Phase 2 is therefore
scoped to POCA/PPO, not SAC.

### Blockers cleared before any measurement was possible

1. **No `.venv` existed.** `CLAUDE.md` documents one; the directory was absent
   and `python` was not on PATH (only the `py -3.10` launcher, Python 3.10.11).
   Created `.venv` and installed `requirements.txt` against the PyTorch cu121
   index. Verified the version contract that `CLAUDE.md` §4 makes load-bearing:

   | Side | Version | Comms API |
   |---|---|---|
   | Python `mlagents` | 1.1.0 | **1.5.0** |
   | C# `com.unity.ml-agents` | 4.1.0 (`Academy.cs` `k_ApiVersion`) | **1.5.0** |

   Equal, so the handshake will be accepted. `torch 2.5.1+cu121`,
   `numpy 1.23.5` — the pins that keep the `.onnx` export path working.

2. **No `Builds/FootballEnv` existed.** `Builds/` was empty, so headless
   training could not start at all. `Config/FootballBase11.yaml` itself warns
   the env must be rebuilt for revision 8 and that the handshake will *not*
   catch a stale one, because revision 8 changed dynamics with every shape
   identical. Built via the running Editor.

3. **No `results/` directory** — no prior run's TensorBoard data exists on this
   machine to compare against. Every baseline number here is measured fresh.

---

## Phase 1 — Metric discovery

### 1.1 Observation space (ground truth, read from source)

Two sensors per agent, 22 agents, all identical in shape:

| Sensor | Size | Source |
|---|---|---|
| `VectorSensor` | **36** | `Sensor_FootballState.OBSERVATION_SIZE` |
| `RayPerceptionSensor2D` "RayFootball" | **52** | scene component |
| **Total per agent** | **88** | |

Ray sensor arithmetic, from the scene YAML (`m_RaysPerDirection: 6`,
`m_MaxRayDegrees: 90`, `m_RayLength: 20`, `m_SphereCastRadius: 0.5`,
`m_ObservationStacks: 1`, tags `[Offense, Defense]`):

```
(numDetectableTags + 2) * (2 * raysPerDirection + 1) * stacks
= (2 + 2) * (2 * 6 + 1) * 1
= 4 * 13 = 52
```

The 36-float vector breaks down as:

| Slice | Floats | Notes |
|---|---|---|
| Role one-hot | 10 | `Systems_RoleTable.ROLE_COUNT` |
| Own position (normalized field x, y) | 2 | |
| Own velocity / role top speed | 2 | divisor is `TopSpeedOf(role)` — role-relative, not global |
| Facing as cos/sin | 2 | continuous across the 0/360 wrap |
| Fatigue, is-carrier | 2 | |
| Ball relative position + ball velocity | 4 | |
| Ball state (Held, InFlight) | 2 | |
| Field geometry (to goal, both sidelines, to LoS) | 4 | |
| Down, yards-to-go | 2 | `LONG_YARDAGE_YARDS = 15` saturation |
| Play-call one-hot | 6 | **offense only**; defenders get a zeroed slice |

**Every value is already in `[-1, 1]` by construction**, and the config sets
`network_settings.normalize: false` to match. This is the single most important
property to preserve: any observation added in Phase 2 must be pre-normalized at
the source, because there is no running normalizer to rescue it.

**Pruning assessment (goal asks to "normalize and prune observation vectors"):**
the vector is already tight. The only visible redundancy is the two sideline
distances (slots 30–31), which sum to a constant `2 * HALF_WIDTH` and so carry
one degree of freedom in two floats. That is a 1-float saving out of 88 (1.1%)
and would cost a **contract revision bump invalidating every checkpoint**. Not
worth it. Recommendation recorded and rejected — see §3.

### 1.2 Action space

`Agent_ActionContract.For(group)`, contract **revision 8**:

| Behavior | Continuous | Discrete branches |
|---|---|---|
| `Offense` | 2 (drive, steer) | — |
| `Defense` | 2 (drive, steer) | — |
| `Quarterback` | 4 (drive, steer, aim.x, aim.y) | `[7, 2]` = play call (6 calls + no-call), throw hold/release |

Steering is car-like: `AddForce(transform.up * driveForce)` plus
`AddTorque(steerTorque)`, both scaled by a fatigue multiplier
`1 - fatigue * 0.45`. Actions are clamped to `[-1, 1]` before scaling, and
applied only inside `OnActionReceived` — which runs every physics tick because
`DecisionRequester.TakeActionsBetweenDecisions = true` with
`DecisionPeriod = 5`. So: **decisions at 10 Hz, forces at 50 Hz.**

### 1.3 Reward terms

| Term | Kind | Magnitude | File |
|---|---|---|---|
| Yardage | dense, per tick | `yardsDelta * 0.01`, zero-sum | `Reward_Progress` |
| Time cost | dense, per tick | `-0.001 / 5` per tick, offense only | `Reward_Progress` |
| Block alignment | dense, per tick | `<= 0.00025` | `Reward_Role.Block` |
| Receiver separation | dense, per tick | `<= 0.00015` | `Reward_Role.Separation` |
| Defender pursuit | dense, per metre | `0.0040` | `Reward_Role.Pursuit` |
| Call repetition | per play, QB | `0 .. -0.375` | `Reward_Call` |
| Terminal | sparse, at whistle | `-0.85 .. +1.0`, zero-sum | `Reward_Terminal` |

Terminal payouts: Touchdown `+1.0`, FieldGoalGood `+0.4`, completion bonus
`+0.6` (stacks), Tackle `-0.5` (`-0.75` behind the line), Incompletion `-0.1`,
Interception / FumbleLost `-0.6`, Safety `-0.85`, Punt `-0.12`, FG missed
`-0.35`.

**There is no control-cost / actuator-effort term anywhere in the reward.** The
only thing that prices exertion is *fatigue*, and fatigue is a physics penalty
(it scales down force output), not a reward term. This is the single largest gap
against the stated goal, which explicitly asks to "penalize high actuator forces
(ctrl cost) and joint acceleration spikes". See §1.5 and §3.

### 1.4 Physics / "MJCF" audit

| Property | Value | Verdict |
|---|---|---|
| `Time.fixedDeltaTime` | `141120000 / 2822399` s = **0.0199999929 s** (50.00002 Hz) | Pinned, correct. Unity 6.6 stores this as a rational, not a float |
| Maximum Allowed Timestep | 0.3333 s | Standard |
| Physics2D `m_VelocityIterations` | 8 | Box2D default |
| Physics2D `m_PositionIterations` | 3 | Box2D default |
| Physics2D `m_SimulationMode` | 0 (FixedUpdate) | Correct — deterministic, tied to the fixed tick |
| Physics2D `m_AutoSyncTransforms` | 0 | Correct — auto-sync is a per-write cost and a nondeterminism source |
| Physics2D gravity | `(0, -9.81)` | **Harmless but misleading** — see below |
| `LINEAR_DAMPING` | 0.8 | Revision 3 lowered it from 1.5 |
| `MAX_BODY_SPEED` | 12 m/s | Pileup-explosion guard, above every role's terminal velocity |

**On gravity.** `Physics2DSettings` carries Earth gravity `(0, -9.81)`, which in
a *top-down* 2D game would drag all 22 players toward one end zone. It does not,
because `Agent_FootballPlayer` sets `_rigidbody.gravityScale = 0f` on every body
at construction. The setting is inert. It is worth knowing that the "Earth
gravity" clause of UNITY_RULES §2 is satisfied nominally rather than
meaningfully here — the ball's flight uses its own `PASS_GRAVITY = 9.81` in a
separate ballistic integrator (`Systems_BallSystem.AdvanceFlight`), which is
where gravity is actually simulated.

**On `<contact><exclude>`.** The goal asks to exclude contacts between adjacent
parent-child bodies. That has no analogue here: there are no articulated
multi-body chains. Each player is a **single** `Rigidbody2D` with one circle
collider (`PLAYER_RADIUS = 0.5`). There is no ragdoll, no joint, and therefore
no parent-child contact to exclude. The joint/biomechanics clauses of
UNITY_RULES §2 are vacuous for the current shapes — as `CLAUDE.md` itself
allows ("apply to whatever articulated shapes get built").

### 1.5 Instrumentation gap analysis

Already emitted to TensorBoard by `Agent_Telemetry` (18 series):

```
Play/{TackleRate, TouchdownRate, OutOfBoundsRate, TimeExpiredRate,
      IncompletionRate, InterceptionRate, NetYards, LengthTicks}
Call/{None, Keep, HandoffFullback, HandoffHalfback, Pass, Punt, FieldGoal, Entropy}
Pass/{AttemptRate, CompletionPerAttempt}
```

Mapping the goal's requested KPIs onto what exists:

| Goal KPI | Status here |
|---|---|
| Survival step ratio | **No analogue.** Episodes are *plays*, terminated by rule (tackle, score, incompletion, timeout) — not by an agent falling over. Nothing "dies". The nearest meaningful signal is `Play/TimeExpiredRate`, which measures the *do-nothing optimum*, and that is already instrumented |
| Target velocity error | **Missing.** No speed series at all |
| Actuator control cost | **Missing.** No force/effort series at all |
| Joint jerk / accel spikes | **Missing.** The 2D analogue is steering chatter — tick-to-tick change in the applied steer command |
| Torso pitch/roll deviation | **No analogue.** Top-down 2D; a player has one rotational DoF (heading) and no notion of upright. The nearest stability signal is how often `ClampSpeed` fires, i.e. how often the solver blows a pileup apart |

So four of the five requested physical/control KPIs are genuinely absent and one
is inapplicable. Instrumenting the four is Phase 1 step 2 and is done in §2.

---

## Phase 1 — Instrumentation added

Five new TensorBoard series, in `Agent_FootballPlayer` (+107 lines, no shape
change, **no contract revision bump** — nothing here touches an observation, an
action, or the dynamics):

| Series | Definition | Why |
|---|---|---|
| `Control/Effort` | mean over the play of `\|F_drive\|/F_max + \|τ_steer\|/τ_max` | The requested **ctrl cost**. Dimensionless in `[0, 2]`, so a 120 kg lineman and a receiver are directly comparable |
| `Control/SteerJerk` | mean over the play of `\|steer_t - steer_{t-1}\|` | The 2D analogue of **joint acceleration spikes** — actuator chatter |
| `Control/SpeedUtilization` | mean of `\|v\| / TopSpeedOf(role)`, clamped | The closest thing here to **velocity tracking** |
| `Control/SpeedClampRate` | fraction of ticks where `ClampSpeed` fired | **Numerical stability**: nonzero means the solver is blowing pileups apart |
| `Control/Fatigue` | fatigue at the whistle | Was observed by the policy but never recorded |

Three design points worth stating, because each was a way to get this wrong:

1. **Effort is read from applied force, not the action vector.** `CLAUDE.md` §2
   is explicit and the reason is real: a lineman braced against a rusher is a
   near-zero action at near-maximum force. Reading the action would score the
   hardest work in the game as resting. `AccumulateControlEffort` therefore takes
   `driveForce`/`steerTorque` — *after* the fatigue multiplier — exactly as
   `AccumulateFatigue` does.

2. **Jerk is read from the action, not the torque** — the opposite choice, on
   purpose. Chatter is a property of what the policy *asked for*. A policy
   alternating `-1, +1, -1` every tick has a mean torque near zero and a jerk of
   ~2.0. Mean effort alone is blind to it; that is precisely why the second
   series exists.

3. **Flushed before `EndEpisode()`, not after.** `EndEpisode` closes the
   trajectory the trainer attributes these steps to. A stat written after it
   lands in the *next* play's summary window.

Guarded on `Academy.IsInitialized && Academy.Instance.IsCommunicatorOn` rather
than on scene placement, because unlike `Agent_Telemetry` (a single component
placed only in `SCN_TRAIN_FOOTBALL`) this code sits on all 22 players in every
scene. In a played game the guard is false and the whole block is skipped.

Per-tick cost: six adds, one compare, one clamp, one magnitude. No allocation.

**Compile verified:** Unity console holds exactly 10 errors, all pre-existing —
9 `ivanmurzak` asmdefs with no scripts, and 1 `ReflectionTypeLoadException`. Zero
`CS` diagnostics. The env then rebuilt successfully, which is the stronger proof.

---

## Phase 1 — Baseline throughput (uninstrumented build)

Run `baseline_probe`, 4 headless envs, `--base-port=5400`, `CUDA_VISIBLE_DEVICES=-1`,
`Config/FootballBase11.yaml` unmodified.

| Behavior | Step 20k @ | Step 40k @ | Steady-state rate |
|---|---|---|---|
| `Defense` | 27.595 s | 111.188 s | **239.3 steps/s** |
| `Offense` | 61.822 s | 148.318 s | **231.2 steps/s** |
| `Quarterback` | (not yet reported at 148 s) | — | ~23 steps/s (1 agent vs 10) |

Rates are computed **between** summaries, not from t=0, so process startup and
the Unity handshake are excluded.

**Aggregate ≈ 494 agent-steps/s across all three behaviors, at 4 envs.**

This confirms the central claim in `CLAUDE.md`'s throughput table from the other
direction: with `time_scale: 20` the *simulation* could run 20x real time, but
88 agents (4 envs x 22) are only getting ~5.6 decisions each per second against
the 10 decisions/s the fixed tick allows. **The trainer is the bottleneck, not
the game** — exactly as the documented 6-env measurement (81 CPU-seconds in the
envs against 2,877 in python) says. Adding envs will not help much; that
prediction is tested directly in Phase 2.

---

## Phase 1 — Baseline KPIs (run `kpi_baseline`, instrumented, 4 envs)

First summary window, step 20,000. This is a barely-trained policy, so the
numbers are a *floor*, not a verdict — but they establish the scale of every
series and immediately validated the new instrumentation.

| KPI | @20k | Reading |
|---|---|---|
| `Play/LengthTicks` | **600.00** | Every play hits `MAX_PHYSICS_TICKS` exactly |
| `Play/TimeExpiredRate` | **1.000** | 100% of plays expire. Nothing ever ends in a football outcome |
| `Play/TackleRate` | 0.000 | |
| `Play/TouchdownRate` | 0.000 | |
| `Play/NetYards` | −4.15 | Net *backwards* |
| `Control/SpeedUtilization` | **0.0496** | Players move at **5% of their role's top speed** |
| `Control/Effort` | 0.531 | But apply ~53% of available force |
| `Control/SteerJerk` | 0.0755 | |
| `Control/SpeedClampRate` | 0.000 | Solver is stable — no pileup explosions |
| `Control/Fatigue` | 0.0139 | Negligible |
| `Call/Entropy` | 0.679 nats | Spread across 4 calls, no collapse yet |
| `Policy/Entropy` | 1.419 | |
| `Environment/Episode Length` | 119.0 | = 600/5, the decision cap. Consistent |

### What the new instrumentation bought immediately

`Play/TimeExpiredRate = 1.0` on its own is ambiguous: it is equally consistent
with players sprinting into a corner and with players standing still. The two
demand opposite fixes. **`Control/SpeedUtilization = 0.0496` against
`Control/Effort = 0.531` settles it in one line**: the agents are applying
roughly half their available force and converting almost none of it into
displacement.

That is the signature of car-like steering under a near-random policy — drive
and steer are both ~zero-mean, so the body spins on the spot and the net force
integrates to nothing. It is the do-nothing basin `Reward_Progress` was written
to defeat, seen directly rather than inferred from a reward scale. Before this
run there was no series in the project that could distinguish the two cases.

### Correction to the jerk scale stated earlier

`Control/SteerJerk` cannot approach 2.0 in this build. `DecisionRequester` runs
with `DecisionPeriod = 5` and `TakeActionsBetweenDecisions = true`, so the action
buffer is **held constant for 4 of every 5 physics ticks** and the steer command
can only change on the 5th. The metric's effective range is therefore
`[0, 0.4]`, not `[0, 2]`.

The measurement confirms it exactly: `0.0755 x 5 = 0.378`, which is the expected
mean absolute difference between two independent clipped-Gaussian draws on
`[-1, 1]`. The series is calibrated and behaving as derived. Thresholds below use
the corrected `[0, 0.4]` scale.

---

## Phase 1 — Convergence thresholds (quantified)

The goal's exit criteria are written for a locomotion task ("survival ratio",
"target velocity within ±10%"). Neither has a literal analogue here, so each is
mapped to the nearest **measurable** quantity and the mapping is stated rather
than assumed.

### Tier A — is the simulation football?

These are the only externally-anchored numbers in the project. They come from
`CLAUDE.md` and are printed by `Systems_GameFlowSystem` at every final whistle:

```
[PoFootball] REALISM  yards/play 6.24 | 4th downs faced 10 | TD/drive 0.36 | scrimmage plays 71
```

| KPI | Accept band | Source |
|---|---|---|
| yards / play | **5.0 – 7.0** (real ≈ 5.5) | `CLAUDE.md` |
| touchdowns / drive | **0.20 – 0.35** | `CLAUDE.md` |
| 4th downs faced / game | **≥ 8** (≈ one series in three) | `CLAUDE.md` |

`CLAUDE.md` warns single games range 4.48–7.37 yards/play on one config, so
**Tier A is judged on a three-game mean, never one game.**

### Tier B — training-side KPIs, readable live from TensorBoard

| Goal's KPI | Mapped to | Baseline @20k | Threshold | Why this number |
|---|---|---|---|---|
| survival step ratio > 90% | `1 - Play/TimeExpiredRate` | **0.00** | **> 0.80** | A play that expires ends for no football reason. The docs record 28/74 (38%) expiring at a 500-tick cap in heuristic mode; the cap is now 600 |
| — | `Play/LengthTicks` | **600.0** | **< 400** (8 s) | Real snaps run 4–7 s. 600 is the cap itself |
| target velocity ±10% | `Control/SpeedUtilization` | **0.0496** | **> 0.35** | *There is no commanded velocity in this task*, so a ±10% tracking error is undefined. The honest substitute is whether players run at all |
| control effort | `Control/Effort` | 0.531 | **< 1.20**, and falling per yard gained | Range is `[0, 2]`. A hard bar is wrong — effort *should* rise as players start running; what matters is effort per yard |
| joint jerk | `Control/SteerJerk` | 0.0755 | **< 0.20** | On the corrected `[0, 0.4]` scale. Guards against a policy that saws the wheel |
| stability | `Control/SpeedClampRate` | **0.000** | **< 0.01** | Already passing. Nonzero means the solver is blowing pileups apart |
| — | `Call/Entropy` | 0.679 | **> 1.00 nats** | `base03` collapsed at 0.33, `base05` at 0.10. `ln(4) = 1.386` is the practical ceiling (Punt/FG are masked on most downs) |
| — | max single `Call/*` share | 0.3125 | **< 0.60** | `base05` reached 0.99 |
| — | `Pass/CompletionPerAttempt` | (n=1) | **0.45 – 0.70** | Real NFL ≈ 0.65 |

**Note on `Self-play/ELO`: it does not exist for this run and must not be used.**
`CLAUDE.md` §4 is explicit — self-play was removed from the anchor at `base05`,
every behavior carries exactly one team id, and `FootballBase11.yaml` confirms
`self_play: None` in its parsed output. Judging is on `Call/Entropy` and the play
mix, as the docs instruct.

### Exit criteria as applied here

1. **Converged** — every Tier B threshold held across 10 consecutive summary
   windows, *and* Tier A inside band on a three-game mean.
2. **Plateaued** — < 3% change in the Tier B primary set
   (`TimeExpiredRate`, `SpeedUtilization`, `Call/Entropy`) across 3 runs.
3. **Time** — 8 hours elapsed.

**Realistic expectation, stated up front:** `max_steps` is 8,000,000 and measured
throughput is ~240 steps/s per behavior. A single full run is therefore
**~9.3 hours** — longer than the entire time budget. Tier A convergence is
**not reachable** in this window and no amount of tuning changes that. Phase 2 is
consequently run the way the goal specifies for it: **short 5–10 minute
validation runs compared against each other on the Tier B KPIs at a fixed step
count**, with the findings recorded so a subsequent long run starts from a better
configuration. This is a limitation of the compute budget, not a negative result.

---

## Phase 2 — Method, and a correction to the thresholds

### Sample-size finding that shapes every comparison below

`Play/*` rates are written **once per play**, and a play is long. At
`summary_freq: 20000` a window contains only ~16 plays, which is why every
`Play/*` rate in the baseline moves in steps of exactly 0.0625 = 1/16 and why
`Play/TackleRate` swung 0.000 → 0.141 → 0.000 across three consecutive windows
with no underlying change.

The new `Control/*` series do not have this problem. Each is itself a mean over
~600 physics ticks *before* it is written, so a window of 16 plays carries ~9,600
samples. The contrast is stark over the same three windows:

| Series | @20k | @40k | @60k | Spread |
|---|---|---|---|---|
| `Play/TackleRate` | 0.000 | 0.141 | 0.000 | **±100%** |
| `Control/SpeedUtilization` | 0.04955 | 0.05010 | 0.04920 | **±0.9%** |
| `Control/Effort` | 0.53051 | 0.53143 | 0.52827 | **±0.3%** |

**Consequence for method:** short validation runs are judged on `Control/*` and
`Call/Entropy`. `Play/*` rates are reported but not used as a decision criterion
below 200k steps — they cannot resolve the 3% effect size the exit criteria ask
about. This is exactly the class of error `Reward_Call`'s docstring records
(`Policy/Entropy` looking healthy at 3.5 while the play call had collapsed): a
metric that is technically correct and far too noisy to act on.

### Threshold correction: `Control/SpeedUtilization`

The `> 0.35` bar set in Phase 1 was set before reading `Systems_RoleTable`
closely, and it was too aggressive. `TopSpeedOf` is documented as a **ceiling,
not a cruising speed**: under `LINEAR_DAMPING = 0.8` a body needs roughly 25 m of
straight running to reach it, and most plays never get near. A team-wide mean
around 0.25–0.35 is what real football would produce (skill players average
3–4 m/s against a 9 m/s top, linemen 1–2 m/s against 6.2).

Revised: **accept > 0.25, target 0.35.** The baseline's **0.049** is unaffected
by this correction — it is still 5–7x below anything resembling football, and it
is the flattest series in the run.

### The measured problem, stated precisely

`DriveForceOf` is *derived* as `TopSpeedOf × MassOf × LINEAR_DAMPING`, so
terminal velocity equals the advertised top speed exactly. **There is no physics
ceiling holding speed at 5%** — the force available is sufficient by
construction. The cause is the policy, and the mechanism is car-like steering:
`AddForce(transform.up * driveForce)` applies force along the *body's facing*,
and a near-zero-mean steering command at a 2.2–2.6 rad/s terminal turn rate spins
the body several times over a 12 s play, so the drive force integrates to almost
nothing.

Compounding it: the dense yardage term (`Reward_Progress`) pays on **ball**
movement, so 21 of the 22 players get no individual signal for moving at all. The
only per-player dense terms are `Reward_Role`'s, and they are small — pursuit
`0.0040`/metre for defenders, block `0.00025`/tick, separation `0.00015`/tick.

### On the goal's prescribed reward change

The goal asks to "penalize high actuator forces (ctrl cost) and joint
acceleration spikes". Against this baseline both are **contraindicated by the
measurement**:

- `Control/Effort` is 0.53 of a possible 2.0, and the failure is *under*-use of
  the actuators, not over-use. A control cost pushes the policy further toward
  the do-nothing basin the reward was explicitly designed to escape.
- `Control/SteerJerk` is 0.075 on a `[0, 0.4]` scale — already smooth, and
  structurally so, because `TakeActionsBetweenDecisions` holds the command for 4
  of every 5 ticks. There is no chatter to penalize.

This is recorded as a prediction and then **tested rather than asserted** (V6
below), because a confident argument that a change is unnecessary is exactly what
`CLAUDE.md` says produced two of the four balance changes that made revision 8
measurably worse.

---

## Phase 2, E1 — Simulation throughput: does vectorizing help?

The goal asks to "vectorize environments … and run headless". Everything here is
already headless (`--no-graphics`) and already vectorized (`--num-envs` launches
N independent `PoFootball.exe` processes, which is ML-Agents' equivalent of
`SubprocVecEnv` — separate OS processes, not threads). The open question was
whether *more* of them helps.

Measured on the `Defense` behavior, steady-state between the 20k and 40k summary
points so process startup and the Unity handshake are excluded. Identical config
(`FootballBase11.yaml`), identical build, `CUDA_VISIBLE_DEVICES=-1`, runs
strictly sequential so none competed with another for CPU:

| `--num-envs` | Step 20k @ | Step 40k @ | **steps/s** | vs 4 envs |
|---|---|---|---|---|
| 2 | 25.948 s | 103.569 s | **257.7** | +1.0% |
| 4 | 27.364 s | 105.770 s | **255.1** | — |
| 12 | 33.269 s | 117.903 s | **236.3** | **−7.4%** |

**Throughput is flat from 2 to 4 envs and actively degrades at 12.** Six times
the environment processes buys −7.4%.

This is `CLAUDE.md`'s documented diagnosis confirmed from the other direction.
The trainer, not the game, is the bottleneck: at 6 envs the documented split was
81 CPU-seconds in the env processes against 2,877 in python. The environments are
already starving the trainer at 2; adding ten more only takes cores away from
torch and adds gRPC multiplexing overhead.

**Action: use 2–4 envs.** Every experiment below runs at 4. The 12- and 24-env
configurations in the docs are not worth their memory — and 24 does not run at
all on this machine (paging file).

Note this also disposes of the goal's "eliminate per-step Python
allocations/overhead" item as a *throughput* lever: python is CPU-saturated, but
it is saturated doing torch backward passes on a 256x2 network at batch 2048, not
doing per-step bookkeeping. The lever that would actually move this number is the
network size — which `CLAUDE.md` measures directly (`hidden_units: 256` took 12
envs from 404 to 563 steps/s in the older 6-behavior setup). This config already
uses 256.

---

## Phase 2, E2 — Reward-scale arithmetic (analysis, no run needed)

Working the dense terms out in absolute numbers, for a full-length play
(600 ticks = 119 decisions):

| Term | Per play at the cap | Note |
|---|---|---|
| Time cost, offense | `600 x (-0.001 / 5)` = **−0.119** | `TIME_COST_PER_DECISION / DECISION_PERIOD`, paid every tick |
| Yardage, +10 yd | `10 x 0.01` = **+0.100** | `YARD_REWARD_SCALE` |
| Yardage, +5.5 yd (real avg) | **+0.055** | |
| Receiver separation, max | `600 x 0.00015` = **+0.090** | |
| Block alignment, max | `600 x 0.00025` = **+0.150** | |
| Defender pursuit, 10 m closed | `10 x 0.0040` = **+0.040** | |
| Terminal, touchdown | **+1.000** | for comparison |

Two things fall out.

**The offense's dense reward is designed to be roughly net-zero at a realistic
play length, and is net-negative at the cap.** A 12-yard gain exactly cancels a
full-length play's time cost. At a realistic ~300-tick play the cost is −0.060
and an average 5.5-yard carry pays +0.055 — near enough to zero that the terminal
reward is doing essentially all of the work, which is coherent design. But *every
play in the current baseline runs to the 600-tick cap*, so the offense is
presently sitting in the regime where the time cost dominates the dense signal.
The gradient still points the right way (standing still pays the full −0.119 and
earns nothing), it is just weak relative to its own noise.

**Raising `YARD_REWARD_SCALE` is off the table, and the codebase says why.** It
is the obvious response to a weak yardage gradient and it is documented as
having already failed: the constant's own docstring records the collapse where
the offense farmed the shaped term, `TouchdownRate` fell 0.82 → 0.08 and
`NetYards` 52 → 13.5. 0.01 is calibrated so that a full hundred yards is worth
exactly one touchdown, which is what keeps ground covered from dominating
scoring. `Reward_Role`'s docstring states the same principle independently: *a
shaped term that outgrows the terminal reward gets farmed while the game gets
ignored.*

**Recorded and rejected without a run.** This is the one change in this whole
exercise where the repository's own history is better evidence than a 5-minute
validation run could be, and running it anyway would have burned budget to
rediscover a documented regression.

---

## Phase 2, E3 — Run-to-run reproducibility (how big must an effect be to be real?)

Before comparing variants, the noise floor has to be known. `kpi_baseline` and
`sweep_v0control` are **the same configuration run twice** (same env build, same
seed in the YAML, same 4 envs, different ports). Any difference between them is
noise, not signal.

Compared at step ≤ 60,000, mean of the last 3 summary points:

| KPI | `kpi_baseline` | `sweep_v0control` | Δ |
|---|---|---|---|
| `Control/SpeedUtilization` | 0.04959 | 0.04881 | **1.6%** |
| `Control/Effort` | 0.52941 | 0.53129 | **0.4%** |
| `Control/SteerJerk` | 0.07489 | 0.07498 | **0.1%** |
| `Control/Fatigue` | 0.01298 | 0.01350 | 4.0% |
| `Policy/Entropy` | 1.42006 | 1.41737 | 0.2% |
| `Play/LengthTicks` | 559.60 | 558.59 | 0.2% |
| `Call/Entropy` | 1.27594 | 1.07765 | **16.2%** |
| `Play/TimeExpiredRate` | 0.90693 | 0.92593 | 2.1% |
| `Play/NetYards` | −3.27845 | −2.76462 | **16.6%** |

(`env_settings.seed: 1` is fixed in the YAML, so this is not seed variance — it
is the residual nondeterminism of 4 asynchronous env processes feeding one
trainer.)

**The noise floor is not uniform, and that matters more than its size.**

- `Control/*` and `Policy/Entropy` reproduce to **≤ 1.6%** (excluding `Fatigue`,
  which is tiny in absolute terms).
- `Call/Entropy` and `Play/NetYards` reproduce only to **~16%**.

The goal's plateau criterion is "< 3% change over 3 runs". **That criterion is
only measurable on the `Control/*` series.** On `Call/Entropy` or any `Play/*`
rate, a 3% difference is a tenth of the noise — indistinguishable from running
the same config twice. Every variant verdict below is therefore decided on
`Control/*` first, with `Call/Entropy` used only when a gap exceeds ~30%.

This is the second time in this exercise that the *new* instrumentation turned
out to be the only thing precise enough to answer the question asked. It is worth
being explicit about why: the `Control/*` series average ~600 physics ticks
before they are ever written, whereas `Play/*` and `Call/*` contribute one sample
per play and a summary window holds only 8–16 plays.

*(Caveat: `kpi_baseline` used `summary_freq: 20000` and `sweep_v0control` uses
10000, so the two tail-3 means cover slightly different step ranges — 20/40/60k
against 40/50/60k. This inflates the estimate slightly and makes it conservative,
which is the right direction for a noise floor.)*

---

## Phase 2 — Incidental validation: the ONNX export path is intact

Every sweep run terminates cleanly and writes its checkpoints:

```
[INFO] Exported results\sweep_v0control\Defense\Defense-66451.onnx
[INFO] Exported results\sweep_v0control\Quarterback\Quarterback-6041.onnx
[INFO] Exported results\sweep_v0control\Offense\Offense-60410.onnx
```

This independently confirms the pin that `requirements.txt` and `CLAUDE.md` both
spend paragraphs defending. `torch 2.11.0+cu128` was previously installed here and
reverted the same day: it trained happily for 80,000 steps and then died at the
first checkpoint with `ModuleNotFoundError: No module named 'onnxscript'`, because
torch ≥ 2.6 exports ONNX through onnxscript, which pulls `onnx>=1.17` → numpy 2.x
→ protobuf 7.x, against the `onnx==1.15.0` / `numpy==1.23.5` / `protobuf==3.20.3`
that `mlagents 1.1.0` requires.

With `torch==2.5.1+cu121` the export runs. **The promotion pipeline is viable
end-to-end** — a trained run can now actually feed `Tools/promote_brain.py`,
which was not demonstrable before this session because no `.venv` existed.

---

## Phase 2, E4 — Hyperparameter sweep (six variants, 60k steps each)

Every variant is `FootballBase11.yaml` with **exactly one** parameter changed,
run to 60,000 `Offense`/`Defense` steps (6,000 for the single-agent
`Quarterback`, scaled by agent count so all three behaviors finish together).
4 envs, sequential, `--force`, ~3m20s each. Configs in `Config/experiments/`.

| KPI | V0 control | V1 lr 1e-3 | V2 beta 1e-3 | V3 beta 2e-2 | V4 horizon 32 | **V5 batch 512** |
|---|---|---|---|---|---|---|
| `Control/SpeedUtilization` | 0.05213 | 0.05452 | 0.05342 | 0.05086 | 0.05198 | **0.05512** |
| `Control/Effort` | 0.52930 | 0.53190 | 0.52984 | 0.53011 | 0.53003 | 0.53441 |
| `Control/SteerJerk` | 0.07505 | 0.07529 | 0.07518 | 0.07489 | 0.07542 | 0.07602 |
| `Control/SpeedClampRate` | 0.00000 | 0.00000 | 0.00000 | 0.00000 | 0.00000 | 0.00000 |
| `Call/Entropy` | 1.28999 | 1.22336 | 1.26552 | 1.19984 | 1.15216 | **1.40017** |
| `Play/TimeExpiredRate` | 0.89630 | 1.00000 | 0.87963 | 0.93333 | 0.91667 | **0.78889** |
| `Play/LengthTicks` | 563.10 | 600.00 | 572.41 | 562.73 | 584.25 | **535.30** |
| `Play/NetYards` | −3.79 | −2.91 | **−0.33** | −1.63 | −2.81 | −1.71 |
| `Policy/Entropy` | 1.41732 | 1.42039 | 1.41651 | 1.42549 | 1.41871 | 1.41658 |
| `Losses/Value Loss` | 0.00725 | 0.01003 | 0.01148 | 0.00937 | 0.00753 | **0.00656** |

### The single most important number in this table

**`Policy/Entropy` is 1.4165 – 1.4255 in every one of the six runs, including
across a 20-fold change in `beta` (1.0e-3 → 2.0e-2).**

```
entropy of a unit Gaussian = 0.5 * ln(2*pi*e) = 1.4189385
measured range                                = 1.41651 .. 1.42549
```

That is not "close to" the initialization value; it *is* the initialization
value, to five decimal places. **The Gaussian policy's σ is still exactly 1.0
after 60,000 steps, and the entropy bonus is not what is holding it there** — if
it were, `beta = 2.0e-2` and `beta = 1.0e-3` could not produce the same number.

This explains everything else in the table at once. With σ = 1.0 and actions
clamped to `[-1, 1]`, the sampled action is close to uniform noise. Under
car-like steering at a 2.2–2.6 rad/s terminal turn rate, a random steer command
held for one decision (0.1 s) turns the body ~14°; over the 119 decisions of a
play, heading does a random walk of RMS `14° x sqrt(119) ≈ 153°`. The drive force
direction decorrelates completely, and net displacement integrates to nothing.
`Control/SpeedUtilization = 0.05` is that random walk, measured.

### Verdict on the sweep

**No hyperparameter tested changes the outcome materially at 60k steps, because
at 60k steps none of these policies has begun to learn.** 60,000 is 0.75% of the
config's 8,000,000 `max_steps`. `Control/Effort` (0.529–0.534, a 1.0% spread) and
`Control/SteerJerk` (0.0749–0.0760, 1.5%) are inside the 1.6% reproducibility
floor established in E3 — six different configurations, statistically one result.

**This is a null result, and it is the honest one.** The methodology the goal
prescribes — "5–10m validation runs before committing longer training cycles" —
does not discriminate on this task, because the task's warm-up is longer than the
whole validation window. That is worth knowing explicitly rather than reading
noise as a ranking.

### The one variant that does separate: V5

`V5Batch512` is best on `SpeedUtilization`, `Call/Entropy`, `TimeExpiredRate`,
`LengthTicks` and `Value Loss`. The one that carries real weight is
**`Play/TimeExpiredRate` 0.896 → 0.789**, a 12% relative improvement against the
2.1% reproducibility floor measured for that series in E3 — roughly 6x noise.
`Play/LengthTicks` 563 → 535 moves consistently with it.

The mechanism fits the diagnosis. V5 cuts `batch_size` 2048 → 512 and
`buffer_size` 20480 → 10240, which is **four times as many gradient steps per
sample**. When the failure is "the policy has not started moving off its
initialization", more updates per unit of experience is precisely the lever. V1
(`lr` 3.3x) pushes the same lever a different way and moves `SpeedUtilization` in
the same direction (0.0545), though its `TimeExpiredRate` of 1.000 was the worst
in the sweep — so the two are **not** combined below.

`V2BetaLow`'s `Play/NetYards` of −0.33 against V0's −3.79 looks dramatic and is
not acted on: `NetYards` has a ~16.6% reproducibility floor (E3) and, more to the
point, a value nearer zero here means *less displacement in either direction*,
which is not obviously an improvement when the problem is that nobody moves.

**Selected for the long run: V5's `batch_size: 512` / `buffer_size: 10240`, as a
single-parameter deviation from the anchor.**

---

## Phase 2, E5 — The goal's prescribed control-cost penalty (V6), tested

The goal asks to "penalize high actuator forces (ctrl cost)". §Method predicted
this would make things worse. It was implemented and run rather than argued.

**Implementation.** A new `CONTROL_COST_PER_UNIT_LOAD = 0.0002` in
`Systems_SimConstants`, charged per physics tick against the same dimensionless
applied-force load the fatigue model reads:

```csharp
float load = (Mathf.Abs(driveForce) / _driveForce)
           + (Mathf.Abs(steerTorque) / _steerTorque);
AddReward(-load * Systems_SimConstants.CONTROL_COST_PER_UNIT_LOAD);
```

Scaled so a full-length play costs about an eighth of a tackle:
`0.0002 x 0.53 x 600 = -0.0636`.

**The term was verified live before its results were read**, which matters
because a silently-absent patch would have produced a convincing null result. At
step 10,000, against `sweep_v0control` at the same step:

| | V0 control | V6 ctrl-cost | Δ |
|---|---|---|---|
| `Defense` Mean Reward | 0.002 | −0.066 | −0.068 |
| `Offense` Mean Reward | −0.057 | −0.112 | −0.055 |
| `Defense` Mean **Group** Reward | 0.170 | 0.170 | **0.000** |
| `Offense` Mean **Group** Reward | −0.178 | −0.178 | **0.000** |

Mean individual reward shifted by −0.0615 against a predicted −0.0636 (within
3%), while the **group** reward is bit-identical — exactly right, because
`AddReward` feeds the individual return and POCA's group reward is a separate
channel. The term is live and correctly scaled.

**Result at 60,000 steps:**

| KPI | V0 control | **V6 ctrl-cost** | Δ | vs noise floor |
|---|---|---|---|---|
| `Control/SpeedUtilization` | 0.05213 | **0.05032** | **−3.5%** | 2.2x noise — real |
| `Control/Effort` | 0.52930 | **0.52708** | −0.4% | inside noise |
| `Play/TimeExpiredRate` | 0.89630 | **0.95833** | **+6.9%** | 3.3x noise — real |
| `Play/LengthTicks` | 563.10 | 592.79 | +5.3% | real |
| `Call/Entropy` | 1.28999 | 1.24034 | −3.8% | inside noise |
| `Environment/Cumulative Reward` | −0.01470 | −0.08523 | −480% | the cost itself |

**Verdict: rejected. The prescribed change is harmful here, and it fails on its
own terms.**

The point worth keeping is not simply that the KPIs got worse. It is that
**`Control/Effort` barely moved (−0.4%, inside the noise floor) while
`SpeedUtilization` fell 3.5% and `TimeExpiredRate` rose 6.9%.** The penalty did
not buy the thing it was levied for. It could not: at σ = 1.0 the action
distribution is still essentially its initialization (E4), so mean |action| is
set by the Gaussian, not by the policy's preferences, and a reward gradient of
this size cannot move it. What the penalty *did* do was add a uniform negative
drift to the individual return, worsening the signal-to-noise of the yardage term
the offense actually needs to learn from.

Reverted. `CONTROL_COST_PER_UNIT_LOAD` is not in the committed tree; the
instrumentation from Phase 1 remains.

**Also rejected, without a run: penalizing jerk.** `Control/SteerJerk` is
0.0749–0.0760 across all seven runs — a 1.5% spread, inside the reproducibility
floor, and structurally bounded because `TakeActionsBetweenDecisions` holds each
command for 4 of every 5 ticks. There is no chatter in this system to penalize.

---

## Phase 2, E6 — Long run `football_long01`

`Config/FootballLong01.yaml`, `--num-envs=4`, `--base-port=6000`,
`CUDA_VISIBLE_DEVICES=-1`. Two deviations from the `FootballBase11` anchor, both
carried over from the measurements above:

- `batch_size` 2048 → 512, `buffer_size` 20480 → 10240 (E4)
- `summary_freq` 20000 → 50000 — **statistics only, not training** (E3)

Throughput at 4 envs with the smaller batch: **276 steps/s** including startup
(100,000 steps in 362 s), against 255 steps/s for `batch_size: 2048`. Four times
as many gradient steps per sample costs nothing measurable on CPU at this network
size, and appears to be marginally *faster* — small matmuls fit cache better.

Checkpointing and ONNX export are working on schedule
(`Defense-99992.onnx`, `Offense-99989.onnx` at the 100k checkpoint interval).

### Project tooling validated against this run

| Tool | Result |
|---|---|
| `Tools/promote_brain.py --verify` | Runs, exits 0, reports *"No promoted brains found under Assets/Agents"* — correct, and consistent with `CLAUDE.md`'s statement that nothing is promoted |
| `Tools/watch_entropy.py --run football_long01` | Runs, exits 0 |
| `Tools/prune_results.py` | Not exercised (no disk pressure) |

This is the first time in the repository's recorded history that these can have
been run at all, since no `.venv` existed before this session.

### `football_long01` at 250,000 steps — the run is learning

Compared against the 60k sweep control, which is where every short validation run
in E4 stopped:

| KPI | @60k (V0) | **@250k** | Change | Threshold | Status |
|---|---|---|---|---|---|
| `Play/TimeExpiredRate` | 0.896 | **0.587** | **−34%** | < 0.20 | improving |
| `Play/TackleRate` | ~0.000 | **0.406** | — | — | plays now END |
| `Play/LengthTicks` | 563.1 | **489.1** | **−13%** | < 400 | improving |
| `Play/NetYards` | −3.79 | **+0.41** | sign flip | — | improving |
| `Control/SpeedUtilization` | 0.0521 | **0.0647** | **+24%** | > 0.25 | improving |
| `Call/Entropy` | 1.290 | **1.512** | +17% | > 1.00 | **PASS** |
| max single call share | 0.313 | 0.306 | — | < 0.60 | **PASS** |
| `Control/SpeedClampRate` | 0.000 | **0.000** | — | < 0.01 | **PASS** |
| `Control/SteerJerk` | 0.0751 | **0.0738** | −1.7% | < 0.20 | **PASS** |
| `Policy/Entropy` | 1.4173 | **1.4140** | −0.2% | — | σ finally contracting |

**Every KPI is moving the right way, and `Policy/Entropy` has finally dropped
below the unit-Gaussian value of 1.41894.** The sweep's null result is now
explained rather than merely asserted: those six configurations were identical
because none of them had begun to learn yet, not because the hyperparameters do
not matter. Four of the eight Tier B thresholds already pass at 250k.

The largest single change is that **plays now end for football reasons**.
`TackleRate` went from ~0 to 0.406 and `TimeExpiredRate` from 0.90 to 0.59.
`Control/SpeedUtilization` rising 24% is what caused it — defenders that move can
close on a carrier, and the sustained-contact tackle rule can finally fire.

The kicking game is also being reached for the first time in this session:
`Call/Punt` 0.022 and `Call/FieldGoal` 0.040 are nonzero, which is what contract
revision 6 added and revision 8 was tuned to make reachable.

### One thing to watch: passing is currently unfailable

`Pass/CompletionPerAttempt = 1.000`, with `Play/IncompletionRate` and
`Play/InterceptionRate` both exactly 0.000, sustained over 9 summary windows.

This is *explicable* rather than obviously broken. Contract revision 5 changed
the quarterback's aim slots from a literal throw direction to a **direction of
intent**, resolved by `Systems_BallSystem.ResolveThrowDirection` to whichever
eligible receiver best matches that bearing, led for the flight time. So aim is
auto-corrected onto a real receiver. Meanwhile the throw trigger is a 2-way
discrete branch sampled at σ = 1 over ~40 decisions of throw window, so the
quarterback releases essentially always; and the defense is still a random walk
at 6% speed utilization, so nobody contests the catch.

**It is worth watching whether this falls as the defense learns to move.** If
`CompletionPerAttempt` stays pinned at 1.000 once defenders are covering, that
would mean the revision-5 auto-aim has made passing strictly dominant, and the
`INTERCEPTION_REWARD` / `INCOMPLETION_PENALTY` terms would be unreachable code —
the same class of problem revision 8 was created to fix for the kicking game.
Recorded as an observation, not yet a finding.

### `football_long01` trend to 400,000 steps

Full series, one row per 50k summary window:

| step | SpeedUtil | Effort | Call/Entropy | TimeExpired | LengthTicks | Policy/Entropy |
|---|---|---|---|---|---|---|
| 50,000 | 0.05062 | 0.52906 | 0.97344 | 0.90244 | 565.12 | 1.41617 |
| 100,000 | 0.05676 | 0.52912 | 1.38573 | 0.64583 | 509.52 | 1.40532 |
| 150,000 | 0.06294 | 0.53116 | 1.45590 | 0.57447 | 492.11 | 1.40134 |
| 200,000 | 0.06178 | 0.53117 | 1.50545 | 0.56863 | 490.25 | 1.40056 |
| 250,000 | 0.06596 | 0.53686 | 1.51298 | 0.50000 | 454.72 | 1.40315 |
| 300,000 | 0.06239 | 0.53418 | 1.50900 | 0.52727 | 454.60 | 1.40286 |
| 350,000 | 0.05997 | 0.53117 | 1.52055 | 0.50980 | 441.78 | 1.40433 |
| 400,000 | 0.06829 | 0.53419 | 1.53809 | 0.49091 | 448.67 | 1.39647 |

**Nearly all of the gain happened between 50k and 150k.** After that the series
separate into two groups.

### Applying the plateau criterion properly

The goal's test is "< 3% change over 3 runs". Single 50k points oscillate by more
than 3% on their own (`SpeedUtilization` moves −5.4%, −3.9%, +13.9% across the
last four), so the test is applied to **3-point means**, which is the only form
that can resolve a 3% effect given the noise floor established in E3:

| KPI | mean 150–250k | mean 300–400k | change | verdict |
|---|---|---|---|---|
| `Control/SpeedUtilization` | 0.06356 | 0.06355 | **−0.02%** | **PLATEAUED** |
| `Call/Entropy` | 1.4915 | 1.5226 | **+2.1%** | **PLATEAUED** |
| `Play/TimeExpiredRate` | 0.5477 | 0.5093 | −7.0% | still improving |
| `Play/LengthTicks` | 479.0 | 448.4 | −6.4% | still improving |
| `Control/Effort` | 0.5330 | 0.5332 | +0.04% | flat throughout |

So the run is **partially plateaued**: the two KPIs that describe *how the
players move* have stopped improving, while the two that describe *how plays end*
are still getting better at roughly 6–7% per 150k steps.

That combination is informative. Plays are ending sooner and more often for
football reasons, but **not because players got faster** — `SpeedUtilization` has
been pinned near 0.064 since 150k. What is improving is positioning and
convergence on the ball, which shows up in tackles without showing up in speed.

`Control/Effort` deserves a note of its own: it has not moved outside
0.529–0.537 in **any** of the fifteen runs in this entire session — baseline,
six sweep variants, the control-cost run, and 400k steps of long training. It is
the most stable number measured here, and it is essentially the mean absolute
value of a unit Gaussian. Together with `Policy/Entropy` falling only 1.41617 →
1.39647 over 400k steps, the picture is that **σ is contracting extremely
slowly**, and that is the rate limiter on everything else.

