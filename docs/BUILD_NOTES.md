# Build notes — single-play milestone

What was actually built against [BRIEF_SinglePlay.md](BRIEF_SinglePlay.md), where
it deviates, and what is still open. Written during the overnight session of
2026-08-09.

Starting state was **zero C# scripts** and no assembly definitions. Everything
below is new.

---

## What now exists

```
Assets/Scripts/
  Systems/Model/     PoFootball.Models    9 files — enums, RoleTable, FieldModel,
                                          PlayModel, SimConstants
  Systems/           PoFootball.Systems   10 files — 4 messages, IPlayerHandle,
                                          Registry, FormationSlot, Formation,
                                          Referee, EpisodeDirector, EpisodeSeed,
                                          GameLifetimeScope
  Systems/View/      PoFootball.Views     EMPTY — presentation not built
  Sensor/            PoFootball.Sensors   Sensor_FootballState (25 floats)
  Reward/            PoFootball.Rewards   Reward_Progress, Reward_Terminal
  Agent/             PoFootball.Agents    Agent_FootballPlayer
```

All five non-empty assemblies compile clean. `PoFootball.Agents` cannot
reference `PoFootball.Views` — the MVS direction is enforced by the compiler,
not by convention.

Packages added: **VContainer**, **MessagePipe** (+ `MessagePipe.VContainer`),
**UniTask** — all via git URL, all resolved and locked.

Scene `SCN_TRAIN_FOOTBALL` now carries the composition root on `/Systems` and 22
configured agents. The trainer config was rewritten from one behavior to four.

---

## Deviations from the brief

Each of these is a decision I made while building, not something the brief
specified. Listed so they can be reversed if you disagree.

### 1. Models hold plain state; MessagePipe carries lifecycle events

`.claude/rules/architecture.md` specifies `ReactiveProperty<T>` in Models. That
type ships with R3/UniRx, **neither of which is installed**, and MessagePipe does
not provide it.

More substantively: this state is read by 22 agents every physics tick at
`time_scale: 20`. Per-change subscription callbacks would allocate on every
change and break acceptance criterion #17 (zero GC in the hot path). So Models
expose plain properties, and MessagePipe carries only play-lifecycle events —
snap, tackle, score, play-ended — which fire a handful of times per episode
rather than thousands.

### 2. Six assemblies, not four

The brief said Models / Systems / Agents / Views. CLAUDE.md §1 independently
mandates four folders by prefix (`Agent_`, `Sensor_`, `Reward_`, `Systems_`).
These do not map 1:1 — `Systems_` alone covers referee, presentation and UI.

Resolution: `Models` and `Views` are nested as sub-assemblies under
`Scripts/Systems/`, so every file there still carries the `Systems_` prefix,
while `Sensor/`, `Reward/` and `Agent/` each get their own assembly. Both rule
sets hold, and `Agents` still cannot see `Views`.

### 3. No per-position agent subclasses

The brief sketched `Agent_FootballPlayer` as abstract with `Agent_Lineman` /
`Agent_SkillPlayer` / `Agent_Coverage` beneath it. The four brain groups differ
only in which policy they bind to, which is *data* (a behavior name string), not
behaviour. Subclasses would have been empty.

Instead there is one `sealed` agent, and role, side, brain group, team id and top
speed are all derived from a single serialized `_formationSlotIndex`. The scene
carries one number per player rather than five fields that can disagree. If a
position ever needs genuinely different code, that is the moment to add a
subclass.

### 4. `gravityScale = 0`

CLAUDE.md §2 says Earth gravity −9.81. That clause is written for side-on
articulated bodies; this is a top-down view of a field, where gravity would pull
every player off the bottom of the pitch. CLAUDE.md's own "invariants that always
hold" list does not include gravity, so this is consistent with it.

### 5. `time_horizon: 150`, not the brief's 128

A play is capped at 750 physics ticks and agents decide every 5th tick, so
`Systems_PlayModel.MAX_DECISIONS` is exactly 150. Using 150 keeps an entire play
inside one horizon, which is what the original config's comment was protecting
("keep the whole play inside one horizon so credit reaches the snap"). 128 would
truncate the longest plays.

### 6. `--num-envs 4`, not 6

The box has 6 physical cores and each env simulates 22 agents. See
`results/football_base01/MANIFEST.md`.

### 7. Ray tags are absolute, not relative

The brief listed detectable tags as `Teammate` / `Opponent`. Tags are per-object
and cannot differ per observer, so the sensor uses `Offense` / `Defense` and the
agent infers the relationship from its own role one-hot. Same information, no
per-frame tag rewriting.

---

## Pre-existing scene bugs found and fixed

Both would have silently corrupted training:

1. **Duplicate colliders.** Every one of the 23 physics bodies (22 players + the
   ball) carried two identical non-trigger `CircleCollider2D`, and all four
   boundary walls carried two `BoxCollider2D`. Duplicates double contact events,
   which would have made the closing-speed tackle rule fire on phantom contacts.
   One of each removed.

2. **`FreezeRotation` on every player rigidbody.** Steering is applied as torque,
   so this would have silently discarded half of every action the policy took —
   the agents would have been able to drive but never turn. `Agent_FootballPlayer`
   now clears `constraints` from code at Awake.

Both dynamics are now set from code rather than serialized, so a scene edit
cannot quietly change the physics an existing `.onnx` was fitted against.

---

## Known broken, not caused by this work

**`com.unity.pipeline`'s `eval` / `eval_file` are unusable.** They throw
`TypeLoadException` because `Assets/Plugins/NuGet/Microsoft.CodeAnalysis.dll`
(4.8.0) shadows the pipeline package's own copy (3.11.0). The
`ReflectionTypeLoadException` in the Editor console at every startup is this same
conflict. It is noisy but harmless to training, and all scene work was done with
the discrete MCP commands instead — which is the documented precedence anyway.
Fixing it means relocating or removing one of the two Roslyn copies, which risks
whichever MCP package depends on the NuGet one.

**Watch `Systems_GameLifetimeScope.cs`.** During this session the file was
overwritten with an empty 179-byte `LifetimeScope` stub by the Editor tooling.
An empty `Configure()` means no registrations, no entry points, and the episode
director never starts — the symptom was training that connected all four brains
and reported "No episode was completed" forever. If episodes ever stop cycling,
check this file's size first (should be ~2.8 KB).

---

## Run 02 — the wrap-up tackle fix (2026-08-10)

`football_base01` ran 9.9 h / ~11 M steps and the offense ran away with it. The
telemetry added for run 02 showed why, and the answer was worse than suspected:
**run 01 recorded zero tackles.** Every play ran the full 750-tick cap.

Cause: the tackle rule fired only on `relativeVelocity.magnitude > 1.5 m/s`. Two
bodies moving the same direction at similar speed have near-zero relative
velocity, so a defender chasing the carrier down from behind stayed in contact
indefinitely and never registered. Only head-on hits counted — and a pursuit is
the most common tackle in football.

Fix: `Systems_SimConstants.SUSTAINED_TACKLE_TICKS = 10`. Contact sustained for 10
consecutive physics ticks (0.2 s) now ends the play regardless of closing speed.
The impact rule is unchanged and still ends a play instantly on a real hit.
Plumbed through `Agent_FootballPlayer.OnCollisionStay2D` →
`Systems_Referee.ReportSustainedContact`, with a tick guard so several defenders
touching on the same tick count as one tick of contact.

Verified against the run-01 trained policies before committing to a long run:

| Metric | run 01 | fixed rule |
|---|---|---|
| `Play/TackleRate` | 0.000 | **0.517** |
| `Play/TimeExpiredRate` | 1.000 | **0.154** |
| `Play/LengthTicks` | 750 (cap) | 380 |
| `Play/TouchdownRate` | — | 0.329 |

`TimeExpiredRate` of 0.154 puts acceptance criterion #23 (< 0.20) **inside
target** for the first time.

Also added: `Systems_Telemetry` (StatsRecorder → TensorBoard). This required
adding `Unity.ML-Agents` to the `PoFootball.Systems` asmdef, which slightly
weakens the "Systems knows nothing about ML-Agents" property that
`Systems_IPlayerHandle` was built to preserve. `StatsRecorder` is a benign
dependency compared to agent types, and the alternative — a second indirection
interface for one call site — was not worth it. The HTTP half of CLAUDE.md §4 is
still not built.

`Config/FootballBase02.yaml` is byte-identical to 01 in hyperparameters; only the
simulation changed. It exists as a separate file because CLAUDE.md §1 pairs
configs 1:1 with run-ids, and the two runs are not comparable.

## Milestone 2 — play calls, handoffs and the passing game (2026-08-10)

Supersedes the brief's "run only, ball never detaches" decision. The quarterback
now takes every snap and commits to one of four calls on its first decision
after it: keep, hand to the fullback, hand to the halfback, or pass.

**The call is latched.** The QB reads the defensive alignment, commits, and
cannot change its mind. Every offensive player observes the call; defenders get
a zeroed slice of the observation vector, so committing to it cannot leak it —
there is a test for exactly that.

| Change | Detail |
|---|---|
| Formation | I-form. Traded the third receiver for a fullback to stay at eleven; QB / FB / HB stack on one line |
| Ball | `Systems_BallModel` with Held / InFlight / Incomplete. `Systems_BallSystem` owns flight, catches and handoffs |
| Handoff | Completes when the QB gets within `HANDOFF_RADIUS` of the designated back. No separate action — committing and closing the distance is the mechanic |
| Passing | Real in-flight ball. Closest player inside `CATCH_RADIUS` catches it, **either side**, so coverage genuinely contests |
| New outcomes | `Incompletion`, `Interception` |
| Observations | 25 → 32 |
| Actions | OffenseSkill 2 continuous → 4 continuous + discrete `[4, 2]`; other three brains unchanged |
| Carrier highlight | The ball carrier renders **white**. At this scale a colour swap is the only readable indicator, and possession now moves |
| Ball sprite | `Systems_BallView` finally makes it follow the ball, and shows only while airborne |

**Acceptance criterion #7 is dead.** It asserted the ball could never enter a
loose state — the run-only invariant. Milestone 2 inverts it.

### Two bugs this uncovered in the milestone-1 scene

- **The ball sprite never moved.** Nothing in the codebase referenced
  `/Play/Ball`; the yellow dot sat where the scene author left it and had no
  relationship to possession.
- **It was a live Rigidbody2D** — a 0.41 kg dynamic body loose among 22 agents.
  It could not trigger tackles (the collision handler requires an
  `Agent_FootballPlayer`), and at a 1:244 mass ratio its effect was negligible,
  but it was simulation noise that should never have existed. Physics components
  removed.

### Architectural compromises taken knowingly

- **The carrier tint lives on the agent, not in Views.** `SetCarrier` is already
  the exact hook where possession changes, and a separate view would need its own
  copy of the formation slot index to know which player it belongs to.
  `SpriteRenderer.color` is per-instance and does not clone the material, so
  batching is unaffected.
- **`Systems_IInjectableView`** is a marker interface in Systems so the lifetime
  scope can inject views without referencing `PoFootball.Views`. Same inversion
  trick as `Systems_IPlayerHandle` uses for agents.

Run `football_base03` / `Config/FootballBase03.yaml`. Nothing transfers from the
earlier runs — both the observation and action contracts changed, so no earlier
`.onnx` will load and `--initialize-from` is not available.

`football_base02` was stopped at DefenseCover 1.28M steps with
`TackleRate` 0.868 and `TimeExpiredRate` 0.132 — a working run game, kept as a
baseline.

## Open items

| Item | Status |
|---|---|
| Presentation layer (`PoFootball.Views`) | **Not built.** No camera director, no HUD, no UI Toolkit screen. The scene's static camera happens to frame the whole field correctly in 9:16 (ortho size 44 → 49.5 m × 88 m), so the sim is watchable, but there is no follow cam and nothing is styled |
| EditMode tests | **Done — 49 tests, 49 passing.** `Assets/Tests/EditMode`, assembly `PoFootball.Tests.EditMode` |
| PlayMode tests | **Not written.** `Assets/Tests/PlayMode` is empty with no asmdef |
| Acceptance criteria 2, 3, 4, 5, 6, 12, 14 | **Verified by test.** Includes the two negative tests (#3 sub-threshold contact, and the wrap-up regression) |
| Acceptance criteria 10, 17, 18, 19, 20 | **Still unverified** — all need PlayMode tests or profiling. Criteria 1, 7, 8, 9, 11, 13, 15, 16, 21, 22 hold by construction or are observed working |
| Criterion #23 (< 20% `TimeExpired`) | **Now measured** via `Play/TimeExpiredRate`. Sitting at ~0.21–0.23 in run 02 and still falling |
| `Systems_Telemetry` | StatsRecorder half **done**. CLAUDE.md §4 also wants HTTP telemetry; that is still missing |
| Two diagnostic `Debug.Log` calls | Left in `Systems_GameLifetimeScope` and `Systems_EpisodeDirector`. They fire once per session, but `.claude/rules/performance.md` wants them wrapped in `[Conditional("UNITY_EDITOR")]` |
| Sprite atlas | Not created — see the Developer Setup Steps in the brief. Still one draw call per shape |
| `Systems_EpisodeSeed` static | All envs share seed 1, so all four see the same LOS sequence. Harmless (PPO action sampling still diverges them) but not ideal |

The largest gap is **telemetry**. Without it there is no way to answer the
question the whole reward design exists to answer — what fraction of plays end in
a tackle vs a touchdown vs the clock. That should be the next thing built.
