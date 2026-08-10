# Feature Brief: Single Play — Snap to Whistle

Produced by `/unity-interview`. This is the requirements brief for the first
end-to-end milestone of PoFootball. No code exists yet — `Assets/Scripts/` is
empty and there are no assembly definitions in the project.

---

## Scope

**Does:**
- Simulates one American football play, snap → whistle, with 22 shapes on a field
- 11 v 11, full squads
- Run only — the ball is attached to a carrier at the snap and never detaches
- A referee system detects the whistle condition, assigns terminal reward, and resets
- Behaviour learned with ML-Agents PPO self-play across per-position-group brains
- Episode repeats indefinitely with a randomised line of scrimmage

**Does NOT:**
- Downs, distance, first downs, series
- Game clock, play clock, quarters, halves
- Kickoffs, punts, field goals, extra points
- Passing, throwing, fumbles, interceptions, any loose-ball state
- Penalties of any kind
- Safeties (carrier exiting own end zone is treated as out of bounds)
- Roster, franchise, season, or persistence of any state
- Networking or multiplayer
- Any direct player control — the human is a pure spectator

**User:** Spectator. Watches the sim; controls only camera and time scale.

**Trigger:** The snap. Episode begins with the formation set at a randomised
line of scrimmage; agents act from the first `FixedUpdate` after the snap.

**Output:** Ball advances or is stopped; a tackle, score, sideline exit, or
timeout ends the play; terminal reward is assigned; the environment resets.

---

## Technical Requirements

| | |
|---|---|
| **Unity** | 6000.5.6f1 |
| **Pipeline** | URP 17.6.0, 2D |
| **Platform** | Windows (dev + headless training). Portrait 9:16 display target |
| **Physics** | 2D, `Rigidbody2D` dynamic, forces applied in `FixedUpdate` only, Δt pinned at 0.02 |
| **ML-Agents** | C# 4.1.0 / Python `mlagents` 1.1.0, comms API 1.5.0 |
| **Persistence** | None |
| **Networking** | None |

### Subsystems in use

| Subsystem | Role |
|---|---|
| Physics 2D | Player bodies, collisions, tackle detection |
| ML-Agents | Agents, `RayPerceptionSensor2D`, `DecisionRequester`, trainer bridge |
| Cinemachine 3.1.7 | Ball-following camera, vertical pan only. **Editor/player builds only** |
| UI Toolkit | HUD, built from C# at runtime. No UGUI, no `.uxml`/`.uss` |
| Input System 1.20.0 | Spectator controls only (camera, time scale). Legacy Input API is hook-blocked |
| Burst + Collections + Mathematics | Non-allocating per-tick sensor and reward work |
| Newtonsoft JSON | HTTP telemetry bodies |

### Performance budget

- **Zero GC allocations** in `CollectObservations → OnActionReceived → Reward_*`
- 60 FPS in Editor play mode: 22 agents, full presentation, `time_scale: 1`, portrait
- Headless: 6 envs at `time_scale: 20`. Throughput baselined on the first run and
  recorded in `MANIFEST.md`; later runs gate at no worse than 10% below baseline
- Draw calls: all 8 shape sprites share one atlas and one material — see
  Developer Setup Steps below

---

## Architecture

`.claude/rules/architecture.md` mandates VContainer, MessagePipe and UniTask.
**None of the three are installed.** All three are to be added. The `Systems_`
layer follows Model-View-System fully; the ML-Agents layer is exempt by
necessity, because `Agent` subclasses are constructed by Unity from the scene
rather than by a container.

```
                        ┌─────────────────────────────────────┐
                        │  MODELS (pure C#, no Unity API)     │
                        │  PlayModel   — phase, tick, carrier │
                        │  FieldModel  — LOS, goal lines      │
                        └────────────▲──────────┬─────────────┘
                                     │ mutate   │ read
                     ┌───────────────┴──────────▼──────────────┐
                     │  SYSTEMS (VContainer singletons)        │
                     │  Systems_Referee         whistle rules  │
                     │  Systems_EpisodeDirector reset/formation│
                     │  Systems_Telemetry       HTTP + Stats   │
                     └──┬──────────────▲────────────────┬──────┘
              publishes │              │ reads state    │ publishes
                        ▼              │                ▼
        ┌───────────────────────┐      │      ┌───────────────────────┐
        │ MessagePipe           │      │      │ VIEWS (MonoBehaviour) │
        │  PlaySnappedMessage   │──────┼─────▶│ Systems_PlayerView    │
        │  PlayEndedMessage     │      │      │ Systems_FieldView     │
        │  TackleMessage        │      │      │ Systems_CameraDirector│
        │  ScoreMessage         │      │      │ Systems_HudView (UITK)│
        └───────────────────────┘      │      └───────────────────────┘
                        ▲              │        ↑ ALL disabled in --no-graphics
                        │ publishes    │ reads
        ┌───────────────┴──────────────┴──────────────────────────────┐
        │  ML-AGENTS LAYER — NOT VContainer-constructed                │
        │                                                              │
        │  Agent_FootballPlayer (abstract MonoBehaviour : Agent)       │
        │    └─ Agent_Lineman / Agent_SkillPlayer / Agent_Coverage …   │
        │         CollectObservations ──▶ Sensor_*  (plain C#)         │
        │         OnActionReceived    ──▶ Rigidbody2D.AddForce         │
        │         AddReward           ◀── Reward_*  (pure functions)   │
        │    + RayPerceptionSensor2D  (component)                      │
        │    + DecisionRequester      (period 5)                       │
        └──────────────────────────────────────────────────────────────┘
```

### The DI seam

`Agent_*` MonoBehaviours need read access to `PlayModel` and `FieldModel` but
are instantiated by Unity, not VContainer. `RegisterComponentInHierarchy<T>()`
registers a single component; with 22 agents this needs
`RegisterComponentsInHierarchy<T>()` or an explicit `Container.InjectGameObject()`
pass at scene start. This is the one place where `architecture.md` and ML-Agents
genuinely collide, and it must be solved before the first agent compiles.

---

## Design Decisions

| Area | Decision |
|---|---|
| Milestone | One play, snap to whistle |
| Player role | Pure spectator |
| Squad size | 11 v 11 |
| Rule fidelity | Minimal — movement, tackle, score |
| Passing | None. Ball never detaches from a carrier |
| Tackle rule | Contact **and** closing speed > 1.5 m/s |
| Movement | Dynamic `Rigidbody2D`, force + torque, `FixedUpdate` only |
| Observations | `RayPerceptionSensor2D` + hand-built state vector |
| Decision rate | `DecisionPeriod = 5` (10 Hz), actions repeated between decisions |
| Brains | Per-position-group, 4–6 behaviors |
| ELO | Per-behavior, judged independently |
| Rewards | Dense per-tick yardage + sparse terminal |
| Assemblies | Four, enforcing MVS direction at compile time |
| Field framing | Vertical field, Cinemachine follows ball on one axis |
| Episode variety | Random LOS, fixed formation |

### Action space

2 continuous actions, normalized to [−1, 1]:

```
actions[0]  drive  →  rb.AddForce(transform.up * drive * maxDriveForce)
actions[1]  steer  →  rb.AddTorque(steer * maxSteerTorque)
```

Fatigue derives from **applied force magnitude**, not the action vector
(CLAUDE.md §2 — isometric bracing is a near-zero action at near-maximum force).

### Observations

Per agent: `RayPerceptionSensor2D` (6 rays per direction, 13 total, 90°, 20 m,
tags `Teammate` / `Opponent` / `Ball` / `Boundary`) plus a `Sensor_FootballState`
vector carrying role one-hot, own velocity, relative ball position and velocity,
has-ball flag, yards to goal line, and yards to each sideline.

`network_settings.normalize: false` in the config, so every observation element
must already be in [−1, 1]. This is load-bearing, not cosmetic.

### Reward shape

```
OFFENSE, per decision (10 Hz):
  +k * (yards gained this step)      dense
  -0.001 per step                    time cost
  +1.0  on touchdown                 terminal

DEFENSE, per decision:
  -k * (yards allowed this step)     dense, mirrored
  +0.5  on tackle                    terminal
  +bonus for a tackle behind the LOS
```

---

## Constants

Starting values, tunable during bring-up.

```
FIELD
  length            109.7 m  (120 yd, incl. end zones)
  width              48.8 m  (53.3 yd)
  end zone depth      9.14 m (10 yd)
  scale               1 sprite = 1 m (256 px sprite @ 256 px/unit)

PLAYERS
  skill top speed     9.0 m/s
  lineman top speed   6.5 m/s
  body diameter       1.0 m

PLAY
  max duration       15 s  →  MaxStep = 150 decisions
  tackle threshold    1.5 m/s closing speed
  velocity clamp     15.0 m/s
  LOS range          own 10 → opponent 40 yard line, seeded
  min spawn spacing   1.2 m centre-to-centre
```

---

## Edge Cases

| Case | Expected behavior |
|---|---|
| No tackle occurs before MaxStep | Outcome `TimeExpired`, spot at carrier position. Explicit outcome, never a silent truncation |
| Carrier exits own end zone | Treated as out of bounds. No safety exists in minimal rules |
| Carrier exits a sideline | Whistle, spot at the crossing point |
| Two agents spawn overlapping | Prevented by construction — fixed formation offsets guarantee ≥ 1.2 m separation |
| Pileup of dynamic bodies | Per-body velocity clamp at 15 m/s; solver iterations pinned in project settings |
| Tunneling at `time_scale: 20` | Physics Δt stays 0.02, so bounded — but bodies use `CollisionDetectionMode2D.Continuous` |
| Fatigue carried across episodes | Cleared on reset **before** motors are restored (CLAUDE.md §2) |
| Presentation running headless | Cinemachine, `SpriteRenderer` updates and UI Toolkit are disabled under `--no-graphics` |
| Editor domain reload with live scope | VContainer `LifetimeScope` and ML-Agents `Academy` both rebuild on reload — verify no duplicate registration |
| Degenerate do-nothing policy | Dense per-tick reward is the primary countermeasure. Gated by acceptance criterion #23 |
| Per-behavior ELO ambiguity | Accepted risk — curves are coupled and there is no single "run health" number |
| Loose ball | Cannot occur. Run-only is an invariant, asserted by criterion #7 |

---

## Integration Points

No existing systems — everything below is new.

| System | Direction | Messages |
|---|---|---|
| `Systems_Referee` | writes `PlayModel`, reads `FieldModel` | publishes `TackleMessage`, `ScoreMessage`, `PlayEndedMessage` |
| `Systems_EpisodeDirector` | writes `PlayModel`, `FieldModel` | subscribes `PlayEndedMessage`; publishes `PlaySnappedMessage` |
| `Systems_Telemetry` | reads `PlayModel` | subscribes `PlayEndedMessage`, `ScoreMessage` |
| `Systems_PlayerView` | reads `PlayModel` | subscribes `PlaySnappedMessage`, `TackleMessage` |
| `Systems_CameraDirector` | reads `PlayModel` | subscribes `PlaySnappedMessage` |
| `Systems_HudView` | reads `PlayModel` | subscribes `PlayEndedMessage`, `ScoreMessage` |
| `Agent_FootballPlayer` | reads `PlayModel`, `FieldModel`; writes own `Rigidbody2D` | none — agents do not publish |

### New dependencies

| Package | Route | Why |
|---|---|---|
| VContainer | OpenUPM (registry already configured) | Mandated by `architecture.md` |
| MessagePipe | Cysharp, git URL | Mandated by `architecture.md` |
| UniTask | Cysharp, git URL | Mandated by `architecture.md`; coroutines are banned |

Exact package IDs and versions are verified at install time, not assumed here.

---

## Assembly Placement

```
PoFootball.Models        no references at all (pure C#)
     ▲
PoFootball.Systems       → Models, VContainer, MessagePipe, UniTask
     ▲
  ┌──┴──┐
Views  Agents            Views  → Models, Systems, Cinemachine, UI Toolkit
                         Agents → Models, ML-Agents, Mathematics, Collections
                         Agents CANNOT reference Views (compile error)

PoFootball.Tests.EditMode  → Models, Systems
PoFootball.Tests.PlayMode  → all runtime assemblies
```

- New runtime scripts go in the assembly matching their `Assets/Scripts/<X>/` folder
- New tests go in `PoFootball.Tests.EditMode` (pure logic) or
  `PoFootball.Tests.PlayMode` (physics, agent lifecycle)

---

## Blocking Work Before Implementation

These must be resolved first — each one invalidates code written before it.

1. **Install VContainer, MessagePipe, UniTask.** Every file's shape depends on this.
2. **Create the four assembly definitions.** Retrofitting assemblies after code
   exists means a large mechanical refactor.
3. **Rewrite `Config/FootballBase01.yaml`.** It currently defines exactly one
   `FootballPlayer` behavior. Per-position brains need 4–6 behaviors, each with
   its own `self_play` block.
4. **Correct `time_horizon`.** It is `1000`; at `DecisionPeriod = 5` a 15-second
   play is 150 decisions. Recommended: `128`. `batch_size: 2048` and
   `buffer_size: 20480` also need revisiting against the new sample rate.
5. **Resolve the DI seam** for injecting models into 22 scene-instantiated agents.

## Developer Setup Steps (manual, Unity Editor)

Per `.claude/rules/performance.md`, these cannot be created by an agent:

1. Right-click in Project → Create → 2D → Sprite Atlas
2. Name it `ShapeAtlas`, save to `Assets/Art/Atlases/`
3. In the Inspector:
   - Add folder `Assets/Art/Shapes` to "Objects for Packing"
   - Max Texture Size 2048
   - Enable Tight Packing and Allow Rotation
   - Click Pack Preview and confirm all 8 sprites fit
4. Verify all player sprites share one material so team tint is applied via
   `MaterialPropertyBlock`, never `renderer.material`

Until this atlas exists, each of the 8 shapes is a separate draw call.

---

## Acceptance Criteria

### Play lifecycle
1. [ ] Snap occurs within 1 decision step of episode start; `PlayModel.Phase` transitions `PreSnap → Live` exactly once per episode
2. [ ] A defender contacting the carrier with closing speed > 1.5 m/s ends the play within 1 physics tick
3. [ ] A defender contacting the carrier with closing speed < 1.5 m/s does **not** end the play *(negative)*
4. [ ] Carrier crossing the defending goal line sets outcome `Touchdown` and awards the terminal reward exactly once
5. [ ] Carrier crossing a sideline ends the play with the spot at the crossing point
6. [ ] An episode reaching 150 decisions (15 s) with no whistle ends as `TimeExpired` with the spot at the carrier's position
7. [ ] The ball is attached to exactly one carrier on every tick of every episode — no `LOOSE` state is ever entered *(negative)*

### Physics & determinism
8. [ ] `Time.fixedDeltaTime` reads exactly 0.02 at every point in a run, in Editor and in a headless build
9. [ ] Forces are applied only inside `FixedUpdate`; no `AddForce` originates from `Update` or a message handler *(negative)*
10. [ ] Two runs with identical seed, identical build and `--num-envs=1` produce byte-identical episode outcomes for 100 consecutive episodes
11. [ ] Fatigue for every agent reads exactly 0 on the first tick of every episode, cleared before motors are restored
12. [ ] No two players overlap at spawn — minimum centre-to-centre separation ≥ 1.2 m across 1000 seeded resets
13. [ ] No `Rigidbody2D` exceeds 15 m/s at any point

### Observations & actions
14. [ ] Every element of every agent's observation vector lies within [−1, 1] across 1000 episodes
15. [ ] Agents request a decision every 5th physics tick and repeat the prior action in between
16. [ ] The action space is exactly 2 continuous actions, and both are consumed

### Performance
17. [ ] Zero GC allocations in the `CollectObservations → OnActionReceived → Reward_*` path, over 1000 ticks in the Profiler
18. [ ] Editor play mode, 22 agents, full presentation, `time_scale: 1`, portrait 9:16 sustains 60 FPS
19. [ ] Headless throughput measured on the first run and recorded in `MANIFEST.md`; later runs gate at no worse than 10% below baseline

### Headless correctness
20. [ ] Under `--no-graphics`, no Cinemachine, `SpriteRenderer` or UI Toolkit code executes — verified by absent profiler markers, not visual inspection *(negative)*
21. [ ] 6 concurrent envs from `--base-port=5010` complete a handshake and run 10k steps without a port collision or hang

### Training
22. [ ] Each of the 4–6 behaviors emits its own `Self-play/ELO` curve to TensorBoard
23. [ ] After 5M steps, fewer than 20% of episodes end as `TimeExpired`

---

## Estimated Complexity

**Complex.**

The single-play loop on its own is moderate — a referee, a spawn system, an
agent, and a reward set. Three things push it to complex:

- **22 agents with sparse minimal rules** is the hardest multi-agent credit
  assignment case, and per-position brains multiply the training surface by 4–6×
- **Four assemblies plus a DI framework, from zero code**, is a substantial
  scaffolding cost before the first play ever runs
- **The zero-allocation and byte-determinism gates** are cheap to design in and
  expensive to retrofit, so they constrain the implementation from line one

The riskiest single item is not any of the above — it is criterion #23. Whether
dense shaping is enough to escape the do-nothing optimum with 22 agents is an
empirical question that the first real training run answers, and no amount of
design work resolves it in advance.

## Recommended Approach

Build in three stages, verifying each before the next.

1. **Scaffold** — install the three packages, create the four assembly
   definitions, write the Models and the LifetimeScope, and solve the DI seam.
   No gameplay yet. *(`unity-coder`)*
2. **Play loop without learning** — spawn the formation, apply constant scripted
   forces, run the referee, verify criteria 1–13 and 20–21 with heuristic agents
   before ML-Agents is involved at all. *(`unity-prototyper`, then
   `unity-test-runner` for the PlayMode tests)*
3. **Wire ML-Agents** — sensors, rewards, decision requester, rewritten config;
   in-editor smoke test at `--num-envs=1`, then the headless sweep.
   *(`unity-coder`, then `unity-build-runner`)*

Stage 2 is where most of the risk actually gets retired: if the referee, spawn
and physics are wrong, training will fail in ways that look like a reward
problem and cost days to diagnose.

Scene work throughout goes through MCP tools, never hand-edited `.unity` YAML —
the `PreToolUse` hooks enforce this.
