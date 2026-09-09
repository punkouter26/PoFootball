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

### Correction: the 400k plateau call was wrong, on two counts

The plateau determination recorded above does not survive more data, and the
correction matters because it reverses the conclusion.

**Count 1 — a bug in the analysis tool.** The first version of `trend.py` printed
`v[0]` for each step: the value from whichever behavior's event file happened to
be read first. Since `Offense`, `Defense` and `Quarterback` all write the same
tag names, a row's value depended on glob order, and any behavior starting or
stopping reporting a tag produced a spurious jump. It is fixed to average across
behaviors; every number in the table below is a mean over the behaviors present.
The corrected 50k row reads `TimeExpiredRate 0.794` where the broken tool printed
either 0.902 or 0.568 depending on when it was run.

**Count 2 — the plateau was a pause.** Progress resumed sharply after 450k.

| step | SpeedUtil | Effort | SteerJerk | Call/Ent | TimeExpired | LengthTicks | TackleRate |
|---|---|---|---|---|---|---|---|
| 50,000 | 0.05586 | 0.53152 | 0.07428 | 1.14719 | 0.79382 | 535.04 | 0.15497 |
| 100,000 | 0.05706 | 0.52943 | 0.07402 | 1.39408 | 0.69196 | 521.18 | 0.25298 |
| 150,000 | 0.06362 | 0.53210 | 0.07368 | 1.46422 | 0.62397 | 501.96 | 0.31350 |
| 200,000 | 0.06261 | 0.53191 | 0.07390 | 1.50768 | 0.62152 | 506.86 | 0.32581 |
| 250,000 | 0.06733 | 0.53800 | 0.07353 | 1.51353 | 0.53302 | 463.20 | 0.41811 |
| 300,000 | 0.06418 | 0.53481 | 0.07348 | 1.51310 | 0.54489 | 465.15 | 0.38883 |
| 350,000 | 0.06251 | 0.53162 | 0.07350 | 1.52488 | 0.51854 | 451.24 | 0.38627 |
| 400,000 | 0.06874 | 0.53448 | 0.07345 | 1.53847 | 0.53712 | 461.93 | 0.42386 |
| 450,000 | 0.07324 | 0.54104 | 0.07337 | 1.53842 | **0.40369** | **415.88** | **0.53739** |
| 500,000 | 0.07742 | 0.54623 | 0.07332 | 1.54043 | 0.44858 | 418.64 | 0.47291 |
| 550,000 | 0.07880 | 0.54826 | 0.07319 | 1.54206 | 0.45403 | 418.57 | 0.47373 |
| 600,000 | 0.07877 | 0.54918 | 0.07324 | 1.54264 | **0.37288** | **384.47** | 0.50847 |

Plateau test, last three windows against the three before:

| KPI | prev 3 | last 3 | change | verdict |
|---|---|---|---|---|
| `Control/SpeedUtilization` | 0.06816 | 0.07833 | **+14.9%** | moving |
| `Play/TimeExpiredRate` | 0.48645 | 0.42516 | **−12.6%** | moving |
| `Play/LengthTicks` | 443.02 | 407.23 | **−8.1%** | moving |
| `Play/TackleRate` | 0.44918 | 0.48504 | **+8.0%** | moving |
| `Control/Effort` | 0.53571 | 0.54789 | +2.3% | plateau (but now *rising*) |
| `Control/SteerJerk` | 0.07344 | 0.07325 | −0.3% | plateau |
| `Call/Entropy` | 1.53392 | 1.54171 | +0.5% | plateau, at a healthy 1.54 |

**Nothing that matters has plateaued.** `SpeedUtilization` is up 14.9% in the
last 150k steps — its fastest growth of the whole run — having looked flat from
150k to 400k. A 250k-step pause followed by renewed improvement is exactly the
shape a premature stop would have missed, and it is the second time in this
exercise that stopping early would have produced the wrong answer (the first
being the 60k sweep).

`Control/Effort` is also finally moving off its Gaussian value for the first time
in the session: 0.5315 → 0.5492. Combined with rising speed, that is the policy
beginning to *use* the actuators rather than sampling them.

**Threshold status at 600k:**

| KPI | value | threshold | status |
|---|---|---|---|
| `Control/SpeedClampRate` | 0.00000 | < 0.01 | **PASS** |
| `Control/SteerJerk` | 0.07324 | < 0.20 | **PASS** |
| `Call/Entropy` | 1.54264 | > 1.00 | **PASS** |
| max single call share | ~0.35 | < 0.60 | **PASS** |
| `Play/LengthTicks` | 384.47 | < 400 | **PASS** (first time) |
| `Play/TimeExpiredRate` | 0.37288 | < 0.20 | improving, −12.6%/150k |
| `Control/SpeedUtilization` | 0.07877 | > 0.25 | improving, +14.9%/150k |
| `Control/Effort` | 0.54918 | < 1.20 | **PASS** |

Six of eight Tier B thresholds now pass.

### 600k–900k: an offense/defense arms race the offense is winning

Between 600k and 900k the KPIs stop moving together and start moving *against*
each other:

| step | SpeedUtil | Effort | TimeExpired | LengthTicks | TackleRate |
|---|---|---|---|---|---|
| 600,000 | 0.08106 | 0.54877 | 0.36336 | 379.75 | 0.52347 |
| 650,000 | 0.08484 | 0.54910 | 0.40353 | 402.84 | 0.52076 |
| 700,000 | 0.08662 | 0.54994 | 0.47522 | 452.43 | 0.37148 |
| 750,000 | 0.08393 | 0.55276 | 0.47396 | 432.66 | 0.36719 |
| 800,000 | 0.07903 | 0.55236 | 0.42778 | 418.51 | 0.32870 |
| 850,000 | 0.08160 | 0.55988 | 0.38750 | 394.04 | 0.37857 |
| 900,000 | 0.07956 | 0.56149 | 0.45614 | 398.91 | 0.35088 |

`Control/SpeedUtilization` peaked at 0.0921 around 750k and has drifted back to
~0.080. `Play/TackleRate` fell from 0.523 to 0.351 — a 33% decline — and
`Play/TimeExpiredRate` rose from 0.363 back to 0.456. Plays are running *longer*
again, having got shorter for 600k steps.

**The per-side mean rewards say plainly what is happening.** The terminal reward
is deliberately zero-sum (`Reward_Terminal`), so these cannot both rise:

| step | Offense mean reward | Defense mean reward |
|---|---|---|
| ~500,000 | −0.038 | −0.058 |
| 700,000 | **+0.014** | −0.086 |
| 750,000 | **+0.055** | −0.098 |
| 800,000 | **+0.046** | −0.134 |
| 850,000 | **+0.078** | −0.110 |

**The offense has pulled decisively ahead.** Ball carriers got fast enough that
the defense can no longer close on them, so tackles stop happening and plays run
to the cap — while `Control/Effort` keeps climbing (0.549 → 0.561), i.e. the
defense is working harder for less.

This is not a training pathology; it is the simulation's own balance showing
through, and **it is the same failure the repository already has a name for.**
`Systems_PlayModel.MAX_PHYSICS_TICKS` documents the cap "doing double duty as a
safety net for a carrier nobody could catch, which is a tackling problem", and
contract revision 8 exists because revision 7 produced 11.2 yards a play with one
fourth down in a whole game. Those diagnoses were made against the *heuristic*
carrier. This run reproduces the same imbalance against a **learned** one, which
is new evidence: revision 8's tuning (`TACKLE_CLOSING_SPEED` 0.8 → 4.0,
role-dependent `TackleTicksOf`, tighter coverage) was calibrated on heuristic
play, and a trained offense re-opens the gap.

**This is the most useful thing this session found for the project**, and it is
not a hyperparameter. No setting in `Config/` fixes it: the lever is defensive
pursuit and the tackle rule, in `Systems_SimConstants` and `Systems_RoleTable`.
Acting on it is explicitly out of scope here — `CLAUDE.md` requires balance
changes to be judged on the REALISM line over three-game means, and it records
that two of four confidently-reasoned revision-8 balance changes made the game
measurably *worse*. Recorded as evidence for that work, not acted on.

### 900k–1.6M: the imbalance is sustained, and it deepens as the policy improves

| step | SpeedUtil | Effort | Call/Ent | TimeExpired | LengthTicks | TackleRate |
|---|---|---|---|---|---|---|
| 900,000 | 0.09403 | 0.57745 | 1.55907 | 0.41140 | 405.24 | 0.36711 |
| 1,000,000 | 0.11266 | 0.59985 | 1.56935 | 0.37037 | 411.72 | 0.38889 |
| 1,100,000 | 0.10600 | 0.59508 | 1.56958 | 0.40337 | 400.66 | 0.42827 |
| 1,200,000 | 0.10569 | 0.59616 | 1.57145 | 0.41818 | 410.02 | 0.41818 |
| 1,300,000 | 0.11812 | 0.60395 | 1.57975 | 0.65957 | 490.32 | 0.21277 |
| 1,400,000 | 0.11736 | 0.60923 | 1.58784 | 0.33898 | 386.24 | 0.35593 |
| 1,500,000 | 0.12952 | 0.62506 | 1.59488 | 0.49020 | 448.47 | 0.31373 |
| 1,600,000 | **0.14334** | **0.63344** | 1.58700 | 0.59184 | 464.33 | **0.28571** |

Two groups of series, moving in opposite directions and doing so consistently for
900,000 steps:

**Still improving, monotonically** — `Control/SpeedUtilization` 0.0559 → 0.1433
(**+156%** over the run) and `Control/Effort` 0.5315 → 0.6334 (**+19%**). The
policies are unambiguously learning; the agents run harder and faster the longer
they train.

**Getting worse** — `Play/TackleRate` 0.523 (at 600k) → 0.286, nearly halved.
`Play/TimeExpiredRate` and `Play/LengthTicks` drift back up with it.

And the per-side rewards never cross back:

| step | Offense | Defense |
|---|---|---|
| 700,000 | +0.014 | −0.086 |
| 1,000,000 | — | −0.122 |
| 1,300,000 | +0.054 | −0.164 |
| 1,500,000 | +0.075 | −0.170 |
| 1,600,000 | — | −0.148 |

**The conclusion is not that training failed. It is that training worked, and
revealed a balance problem.** As the offense learns to run, the defense's ability
to bring it down does not keep pace, so the better the policies get, the *less*
like football the game becomes on the outcome metrics. More training under this
configuration will not reach `Play/TimeExpiredRate < 0.20`; it is moving away
from it while `SpeedUtilization` climbs.

### Correction at 2M steps: it is an oscillation, not a runaway

The "offense has pulled decisively ahead / will not reach the threshold" reading
recorded at 1.6M does **not** survive to 2M, and the correction changes the
conclusion. Between 1.7M and 2M the defense adapted and clawed most of it back:

| step | TackleRate | SpeedUtil | TimeExpired |
|---|---|---|---|
| 600,000 | 0.5235 | 0.08106 | **0.3634** |
| 1,450,000 | 0.4089 | **0.13044** | 0.4923 |
| 1,700,000 | **0.2162** | 0.11518 | **0.6490** |
| 1,850,000 | 0.3400 | 0.10432 | 0.5200 |
| 2,000,000 | **0.4386** | 0.11791 | **0.3684** |

Read over the whole 2,000,000 steps, the run has three phases:

1. **0 → 600k — both sides learn, everything improves together.** `TackleRate`
   0.155 → 0.524, `TimeExpiredRate` 0.794 → 0.363, `SpeedUtilization` 0.056 →
   0.081.
2. **600k → 1.7M — the offense pulls ahead.** `SpeedUtilization` climbs to a
   peak of 0.130 while `TackleRate` falls to 0.216 and `TimeExpiredRate` rises
   back to 0.649.
3. **1.7M → 2M — the defense adapts.** `TackleRate` recovers to 0.439 and
   `TimeExpiredRate` returns to 0.368 — essentially its 600k best — while
   `SpeedUtilization` eases from its peak to 0.118.

So this is a **competitive oscillation with a period of roughly 1.2M steps**, not
a monotonic collapse. At 2M the game is back at its best measured state.

**What survives the correction, and what does not.**

*Does not survive:* the claim that outcome metrics are diverging from their
thresholds. They are cycling, and at 2M they are at the good end of the cycle.

*Survives, and is the real finding:* **the defense's mean reward is negative in
every single window from 700k to 2M** (−0.086, −0.122, −0.164, −0.170, −0.148,
−0.154, −0.188, −0.167) while the offense's is positive. The terminal reward is
zero-sum by construction, so that asymmetry cannot be both sides improving — it
is a structural advantage to the offense that persists *through* the oscillation,
including at 2M when the outcome metrics look healthy. The cycle is the defense
repeatedly catching up and falling behind again, never getting ahead.

### The whole run, smoothed: one complete competitive cycle

Window-to-window `Play/*` values swing by more than their own signal (E3's noise
floor), so the run is best read through a 5-window moving mean:

| step | SpeedUtil | Effort | TackleRate | TimeExpired | LengthTicks | Call/Ent |
|---|---|---|---|---|---|---|
| 250,000 | 0.0703 | 0.5461 | 0.3048 | 0.6143 | 488.29 | 1.4264 |
| 400,000 | 0.0682 | 0.5401 | 0.3880 | 0.5405 | 464.47 | 1.5223 |
| 550,000 | 0.0721 | 0.5403 | 0.4588 | 0.4724 | 433.25 | 1.5369 |
| 700,000 | 0.0817 | 0.5485 | **0.4725** | 0.4289 | 414.45 | 1.5395 |
| 850,000 | 0.0832 | 0.5528 | 0.3933 | 0.4336 | 420.10 | 1.5396 |
| **1,000,000** | 0.0931 | 0.5741 | 0.3731 | **0.4001** | **403.07** | 1.5569 |
| 1,150,000 | 0.1059 | 0.5923 | 0.4038 | 0.4199 | 411.30 | 1.5694 |
| 1,300,000 | 0.1109 | 0.6013 | 0.3706 | 0.4721 | 423.08 | 1.5761 |
| 1,450,000 | 0.1204 | 0.6139 | 0.3466 | 0.4759 | 425.56 | 1.5867 |
| 1,600,000 | **0.1269** | 0.6231 | 0.2966 | 0.4901 | 439.60 | 1.5872 |
| **1,750,000** | 0.1187 | 0.6193 | **0.2552** | **0.5344** | **458.39** | 1.5728 |
| 1,900,000 | 0.1107 | 0.6169 | 0.3029 | 0.4997 | 443.10 | 1.5543 |
| 2,050,000 | 0.1106 | 0.6227 | 0.3550 | 0.4383 | 419.62 | 1.5403 |
| 2,200,000 | 0.1080 | 0.6250 | 0.3483 | 0.4156 | 409.72 | 1.5351 |

Three phases, unambiguous once smoothed:

1. **0 → 1.0M, improving.** Every outcome metric gets better. Best football of
   the run is around 1.0M: `TimeExpiredRate` 0.400, `LengthTicks` 403,
   `TackleRate` 0.373.
2. **1.0M → 1.75M, degrading.** `SpeedUtilization` climbs to its 0.127 peak and
   the outcome metrics all worsen with it — `TackleRate` down to 0.255,
   `TimeExpiredRate` up to 0.534, `LengthTicks` back to 458.
3. **1.75M → 2.2M, recovering.** `SpeedUtilization` eases off its peak to 0.108
   and the outcome metrics return most of the way: `TimeExpiredRate` 0.416,
   `LengthTicks` 410.

**The cycle returns to roughly where it started rather than beating it.** At
2.2M the smoothed `TimeExpiredRate` of 0.416 is still slightly worse than the
0.400 reached at 1.0M. One full cycle, no net gain across it.

`Control/Effort` is the exception: it rises monotonically 0.540 → 0.625 and then
holds. That is the clearest single measure that the policies really did learn —
it sat at 0.529–0.534 in every 60k run in this session, and it has now moved 18%
off that value and stayed there.

---

## Conclusions

### Exit criterion reached

**Plateau**, on the terms the goal set (< 3% change over 3 comparison points),
for the KPIs that can carry a 3% test at all:

| KPI | last-3 vs prev-3 at 2.25M | verdict |
|---|---|---|
| `Control/SpeedUtilization` | −2.75% | **plateau** |
| `Control/Effort` | −0.56% | **plateau** |
| `Control/SteerJerk` | +0.27% | **plateau** |
| `Call/Entropy` | −0.14% | **plateau** |
| `Play/TimeExpiredRate` | +15.13% | cycling, not trending |
| `Play/LengthTicks` | +4.53% | cycling, not trending |
| `Play/TackleRate` | −3.78% | cycling, not trending |

The three that fail the test fail it by **oscillating**, not by improving: over
2.2M steps they completed one full cycle and returned slightly worse than their
1.0M best. They also cannot support a 3% test — E3 measured their
reproducibility floor at 2–16%.

### Threshold status at the end of the run

| KPI | final | threshold | status |
|---|---|---|---|
| `Control/SpeedClampRate` | 0.00000 | < 0.01 | **PASS** — every window of every run |
| `Control/SteerJerk` | 0.0709 | < 0.20 | **PASS** |
| `Control/Effort` | 0.625 | < 1.20 | **PASS** |
| `Call/Entropy` | 1.535 | > 1.00 | **PASS** |
| max single call share | ~0.35 | < 0.60 | **PASS** |
| `Play/LengthTicks` | 410 (smoothed) | < 400 | **borderline** — hit 374 at best |
| `Play/TimeExpiredRate` | 0.416 (smoothed) | < 0.20 | **FAIL** — best ever 0.317 |
| `Control/SpeedUtilization` | 0.108 | > 0.25 | **FAIL** — peaked 0.130 |

**Five of eight pass, one borderline, two fail.** Tier A (yards/play, TD/drive,
4th downs) was not measurable: it requires a full scored game in Game mode, and
`CLAUDE.md` requires three-game means. Not attempted.

### What actually limits this system

Not the hyperparameters. Six variants spanning 3.3x learning rate, 20x entropy
bonus, 3.75x time horizon and 4x gradient density were statistically one result
at 60k steps, and the one that separated (`batch_size: 512`) did so by ~6x the
noise floor on a single series.

**The binding constraint is a balance asymmetry between offense and defense.**
The defense's mean reward is negative in *every* summary window from 700k to
2.4M — −0.086, −0.122, −0.164, −0.170, −0.148, −0.154, −0.188, −0.167, −0.108,
−0.173, −0.126, −0.138 — while the offense's is positive. `Reward_Terminal` is
zero-sum by construction, so this is not both sides improving; it is a structural
advantage that survives the whole oscillation. The cycle is the defense
repeatedly catching up and falling behind again, never getting ahead.

This reproduces, against a *learned* offense, the failure `CLAUDE.md` says
contract revision 8 was created to fix against a *heuristic* one — revision 7
producing 11.2 yards a play with a single fourth down in a whole game. Revision
8's counters (`TACKLE_CLOSING_SPEED` 0.8 → 4.0, role-dependent `TackleTicksOf`,
`COVERAGE_CUSHION` 1.5 → 1.0, `LINEBACKER_DROP_YARDS` 5 → 3.5) were tuned against
heuristic play, and a trained offense re-opens the gap.

### Recommended next step — and why this session did not take it

The lever is defensive pursuit and the tackle rule, in `Systems_SimConstants` and
`Systems_RoleTable`. **Deliberately not touched.** `CLAUDE.md` requires balance
changes to be judged on the REALISM line over three-game means, and records that
**two of the four balance changes reasoned about confidently during the revision 8
work made the game measurably worse**. Changing a dynamics constant is also a
`CONTRACT_REVISION` bump that invalidates every checkpoint this run produced.
Making that change on the strength of one 2.4M-step run, without the three-game
Tier A measurement the project's own rules demand, would be exactly the mistake
that history warns about.

What this session provides instead is the *evidence* for that work: a quantified,
reproducible demonstration that the asymmetry exists under a trained policy,
with the instrumentation in place to measure whether a fix helps.

### What was delivered

1. A working training stack — `.venv`, verified comms API 1.5.0 on both sides, a
   revision 8 headless env, and a demonstrated end-to-end ONNX export path.
2. Five new KPI series (`Control/*`) that the project did not have, at no
   measurable throughput cost, which turned out to be the only metrics precise
   enough to answer most of the questions asked.
3. Measured noise floors, so future comparisons know what counts as a result.
4. A measured throughput answer: use 2–4 envs, not 12.
5. Seven validation runs and a 2.4M-step training run, all reproducible from
   committed configs.
6. Two prescribed optimizations tested and rejected on evidence, one rejected on
   the repository's own recorded history.

---

## Final run statistics — `football_long01`, 2,600,000 steps

52 summary windows, 11,270 s wall clock (3h 08m), 4 envs, ~252 steps/s sustained.
Stopped on the plateau criterion: at 2.6M, **7 of 8 tracked KPIs changed by less
than 3%** over the last three windows against the three before.

| KPI | first window | last-5 mean | change |
|---|---|---|---|
| `Control/SpeedUtilization` | 0.0559 | **0.1216** | **+118%** |
| `Control/Effort` | 0.5315 | **0.6338** | **+19%** |
| `Control/SteerJerk` | 0.0743 | 0.0712 | −4% |
| `Control/SpeedClampRate` | 0.0000 | **0.0000** | none, ever |
| `Call/Entropy` | 1.1472 | **1.5016** | **+31%** |
| `Play/TimeExpiredRate` | 0.7938 | **0.4305** | **−46%** |
| `Play/LengthTicks` | 535.04 | **409.42** | **−23%** |
| `Play/TackleRate` | 0.1550 | **0.3859** | **+149%** |
| `Play/TouchdownRate` | 0.0006 | **0.0977** | — |
| `Pass/CompletionPerAttempt` | 1.0000 | **1.0000** | **none** |
| `Play/InterceptionRate` | 0.0000 | **0.0000** | **none** |

The policies unambiguously learned. Players run twice as fast, use 19% more
force, plays are 23% shorter, tackles happen 2.5x as often, and touchdowns went
from essentially never (0.0006) to 9.8% of plays.

### FINDING: passing is unfailable, and two reward terms are unreachable code

The observation flagged at 250k is now a finding. Across **all 52 summary
windows, spanning 2.6M steps**:

```
Pass/CompletionPerAttempt   1.0000    (every window)
Play/IncompletionRate       0.0000    (every window)
Play/InterceptionRate       0.0000    (every window)
```

Not "high" — *exactly* 1.0 and *exactly* 0.0, from an untrained policy through to
a defense that by the end tackles on 39% of plays and has more than doubled its
speed. **No pass has been incomplete or intercepted in the entire run.**

The cause is contract revision 5. The quarterback's continuous slots 2 and 3 used
to be a literal throw vector; they are now a *direction of intent*, resolved by
`Systems_BallSystem.ResolveThrowDirection` onto whichever eligible receiver best
matches that bearing, led for the flight time. Aim is therefore auto-corrected
onto a real receiver, and nothing in the resolution appears to let coverage break
it up.

The consequence is that three things in `Reward_Terminal` never execute:
`INCOMPLETION_PENALTY` (0.1), `INTERCEPTION_REWARD` (0.6) and the
`Systems_PlayOutcome.Interception` branch — the last of which the code itself
calls *"the largest swing in the game … it has to outweigh the dense yardage a
long throw earns on its way to being picked off, or the offense learns that
heaving it downfield is free."* That is precisely what has happened: heaving it
downfield **is** free, because it cannot be picked off.

This is the same class of defect revision 8 was created to fix for the kicking
game, where `Agent_PlayCaller.ChooseFourthDown`, `Systems_Referee.KickOutcome`
and the Punt/FieldGoal/Safety branches "never executed once". It is also a
plausible contributor to the offensive advantage in §Conclusions: a free,
unfailable pass is worth `COMPLETION_REWARD` 0.6 with no downside risk.

**Not fixed here.** It is a gameplay change in `Systems_BallSystem`, it is
squarely the balance work this session deliberately stayed out of, and a change
to how a throw resolves is a behavioural contract change requiring a
`CONTRACT_REVISION` bump. Recorded with the evidence.

### Housekeeping

- `results/football_long01/` is 280 MB. `Tools/prune_results.py --apply` keeps
  the final and peak checkpoint per brain and would reclaim most of it. Not run —
  the checkpoints are this session's only artifact and deleting them is the
  user's call.
- Shut down in the order `CLAUDE.md` §4 requires: trainer → envs → TensorBoard.
  Zero `PoFootball.exe` and zero TensorBoard processes remain.
- Nothing was promoted. `Assets/Agents/` is untouched and every player still
  runs `Heuristic`, exactly as before this session. Promoting would require
  `Tools/promote_brain.py --run football_long01` followed by
  **`Tools > PoFootball > Build Brain Table`** in the Editor — and on this
  evidence it should wait until the balance asymmetry is addressed.

---

# Phase 3 — Acting on the findings (user-directed)

The session was extended past the plateau to act on the two balance findings.

## Phase 3, F1 — The unfailable pass: diagnosed and fixed

### Root cause

`Systems_BallSystem.Throw` called `ResolveThrowTarget` and flew the ball along the
returned direction — which is `bestLead.normalized`, the direction to the chosen
receiver's **exact** lead point (`receiver.Position + receiver.Velocity *
flightTime`). The quarterback's aim therefore *selected* a receiver and its
precision was then discarded entirely.

The geometry makes the outcome inevitable:

| quantity | value |
|---|---|
| distance from ball to receiver on arrival | **~0 m** (it was aimed there) |
| distance to a covering defender | `COVERAGE_CUSHION` = **1.0 m** |
| `FindCatcher` rule | closest body inside `CATCH_RADIUS` = **1.2 m** wins |

The receiver could not lose. `FindCatcher` and `UpdateFlight` are both written
correctly — defenders *are* eligible, and `catcher.Side == Defense` *does* produce
`Systems_PlayOutcome.Interception`. The branch simply could never be entered. This
was not a tuning problem; it was a geometric certainty.

### Fix — `PASS_AIM_SLACK = 0.35`, contract revision 9

`Systems_BallSystem.Throw` now blends the perfectly-led direction back toward the
quarterback's raw aim:

```csharp
direction = ApplyAimSlack(direction, aim);   // Lerp(led, aim.normalized, 0.35)
```

Three deliberate choices:

- **Applied in `Throw`, not in `ResolveThrowTarget`.** That method is documented
  read-only and is what `Systems_IntentOverlayView` draws. The overlay should keep
  showing the receiver the passer *selected* — that is still the truth about its
  decision even when the throw misses. Putting the slack there would make the
  overlay draw the error instead of the intent.
- **A normalized `Lerp`, not a `Slerp`.** `ResolveThrowTarget` only accepts targets
  with `along > 0`, so intent and led direction are always inside 90° and the lerp
  cannot degenerate.
- **Revision 5's premise is preserved.** The aim still answers "which of my
  receivers", not "solve a continuous control problem" — the hard problem revision
  5 existed to remove. It now merely costs something to answer sloppily.

`CONTRACT_REVISION` bumped **8 → 9**. Shapes are identical (36 obs, 4 continuous,
branches `[7, 2]`), so nothing but this stamp would catch it, and a revision 8
brain would be steering an aim whose precision it was never fitted to care about.
`results/football_long01` is revision 8 and must not load.

### Result — the fix works, immediately and decisively

Run `aimslack01`, same `FootballLong01.yaml`, same 4 envs:

| metric | rev 8, **all 52 windows / 2.6M steps** | rev 9 @600k | real football |
|---|---|---|---|
| `Pass/CompletionPerAttempt` | **exactly 1.0000** | **~0.59** | ~0.65 |
| `Pass/IncompletionPerAttempt` | **exactly 0.0000** | **~0.34** | ~0.33 |
| `Pass/InterceptionPerAttempt` | **exactly 0.0000** | **~0.069** | ~0.025 |
| `Call/Pass` | 0.22–0.31 | 0.21–0.33 | passing not abandoned |

Both dead branches are reachable. `INCOMPLETION_PENALTY`,
`INTERCEPTION_REWARD` and `Systems_PlayOutcome.Interception` all execute now.

**And the aim gradient is learnable**, which was the point — aggregating windows
to beat the ~10-attempts-per-window sample noise:

| | windows ≤200k | windows 250–400k |
|---|---|---|
| `Pass/CompletionPerAttempt` | 0.503 | **0.653** |
| `Pass/InterceptionPerAttempt` | 0.153 | **0.061** |

The quarterback throws better the longer it trains. Under revision 8 there was
nothing to learn: precision had no effect on any outcome.

### UNRESOLVED: this run's non-passing metrics are worse, and I cannot attribute it

Step-matched against `football_long01`:

| step 600k | rev 8 | rev 9 | |
|---|---|---|---|
| `Control/SpeedUtilization` | 0.0811 | 0.0828 | **identical trajectory** |
| `Play/TackleRate` | 0.5235 | **0.2667** | halved |
| `Play/TimeExpiredRate` | 0.3634 | **0.6667** | much worse |

`SpeedUtilization` tracks the baseline almost exactly at every step — expected,
since the change touches only the ball. But tackles and play endings are markedly
worse, and **there is no obvious mechanism**: throw accuracy should not affect
tackling on running plays, and incompletions are only ~3–4% of all plays.

**This is one run per arm and cannot settle it.** Two runs can diverge onto
different learning trajectories from identical settings; E3 measured the
short-horizon reproducibility of `Play/*` at 2–16%, but said nothing about
divergence over 600k steps. `CLAUDE.md` is explicit that single measurements are
noisy and that balance changes are judged on the REALISM line over **three-game
means** — which has not been run.

**So the honest position is: the passing defect is fixed and verified on the
passing metrics; the net effect on game quality is unproven and may be
negative.** `PASS_AIM_SLACK` is a single constant — setting it to 0 restores
revision 8 behaviour exactly, without touching code. Before promoting anything,
the three-game Tier A measurement should decide whether 0.35 is right, or whether
a smaller slack buys the reachability without the disruption.

