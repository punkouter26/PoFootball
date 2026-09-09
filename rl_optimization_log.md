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

