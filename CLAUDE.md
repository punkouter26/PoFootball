# CLAUDE.md — PoFootball

2D American football simulation. Players are flat 2D shapes whose geometry encodes
position (QB, receiver, lineman, defender, …); the game simulates a football game
being played rather than being directly controlled. Behaviour is learned with
Unity ML-Agents self-play.

| Property | Value |
|---|---|
| Unity | 6000.5.6f1 |
| Render pipeline | URP 17.6.0 |
| ML-Agents (C#) | `com.unity.ml-agents` 4.1.0 — comms API **1.5.0** |
| ML-Agents (Python) | `mlagents` 1.1.0 — comms API **1.5.0** |
| Python | 3.10.11, venv at `.venv/` |
| Torch | 2.5.1+cu121 — **CPU only**; see `--torch-device=cpu` below |
| GPU | RTX 5070 Ti Laptop (Blackwell, sm_120) — **unusable** by this Torch build |

---

# UNITY_RULES — ML-Agents Physics Simulation

## 1. Naming
- `Assets/Agents/<Name>_v<NN>/` — `.onnx`, `*_Character.asset`, `MANIFEST.md`.
- Four script prefixes, each matching its folder under `Assets/Scripts/`: `Agent_`,
  `Sensor_`, `Reward_`, `Systems_` (referees, presentation, UI — the largest).
- Scenes `SCN_`; training `SCN_TRAIN_<NAME>`, no suffixes. Envs `Builds/<Name>Env/`;
  configs `<Name><Phase><NN>.yaml` paired 1:1 with run-id `<name>_<phase><nn>`.

## 2. Physics & Biomechanics
- Earth gravity (−9.81), SI units, realistic friction, deterministic execution.
- Actions apply only in `FixedUpdate`. Lock Δt = 0.02 s and solver iterations — an override silently changes the dynamics every brain was fitted against.
- Normalize actions to [−1, 1], scaled to real joint limits and DoF.
- Fatigue reads load from **applied torque, not the action vector** — isometric bracing is a near-zero action at near-maximum torque. Clear it on reset *before* restoring motors.
- Anchors derive from segment lengths — move a segment, everything above it moves.
- Verify joint ranges **parent-local**: gravity off, the body counter-rotates and a
  world-space test reads drift. Measure the `jointAngle` sign.

## 3. Display
- UI Toolkit only — no UGUI, no IMGUI, no `.uxml`/`.uss`. Screens built from C# at runtime.
- Portrait 9:16, 60 FPS (`vSyncCount = 0` or `targetFrameRate` is ignored).
- `Application.version` top-left of the opening scene: inset layer, outside any ScrollView, non-pickable.
- The panel scales on width — size against a live capture.

## 4. MLOps
- C# and Python `mlagents` versions must match; comms API version **equal** or the
  handshake is refused.
- Overwrite `.onnx` in place to preserve `.meta` GUIDs.
- Headless: `--env --no-graphics` + explicit `--base-port` (envs take consecutive ports; collisions hang). 4–8 envs, leaving cores for torch. `--num-envs` changes how experience is batched — record it.
- Telemetry over HTTP *and* `StatsRecorder` (`Agent_Telemetry`, a component on `/Systems` in `SCN_TRAIN_FOOTBALL` only). Kill TensorBoard before `--force`: it holds Windows handles and the wipe silently no-ops. Clean up trainer → envs → TensorBoard.
- **Judge self-play on ELO, not mean reward** — *when self-play is actually running*. It is not, from base05 onward: ML-Agents self-play does not model two different behaviors playing each other, every behavior here carries exactly one team id, and the `self_play` block was removed from the anchor. `Self-play/ELO` does not exist for base05/base06; judge those on `Call/Entropy` and the play mix. See [docs/TRAINING_NOTES_base05.md](docs/TRAINING_NOTES_base05.md).

---

## How these rules land in this repo

**Layout** (created; `Assets/Scripts/<X>/` holds the `<X>_*.cs` files):

```
Assets/Agents/                 promoted brains: <Name>_v<NN>/{*.onnx, *_Character.asset, MANIFEST.md}
                               — currently EMPTY but for MANIFEST_TEMPLATE.md; see below
Assets/Scripts/Agent/          Agent_*.cs      — Agent subclasses, action application
Assets/Scripts/Sensor/         Sensor_*.cs     — observation collection
Assets/Scripts/Reward/         Reward_*.cs     — reward terms
Assets/Scripts/Systems/        Systems_*.cs    — referee, game flow, UI (no ML-Agents dependency)
Assets/Scripts/Systems/View/   Systems_*.cs    — UI Toolkit screens, audio, shape presentation
Assets/Scenes/                 SCN_*.unity, SCN_TRAIN_*.unity — these three only
Assets/Resources/              PoFootballPanelSettings, PoFootballRoleShapes, M_PoFootball*.mat
Builds/<Name>Env/              headless training envs (git-ignored) — FootballEnv only
Config/<Name><Phase><NN>.yaml  trainer configs, 1:1 with run-id <name>_<phase><nn>
Config/archive/                configs for superseded contracts; they will NOT run
results/<run-id>/MANIFEST.md   one per run — records --num-envs, which is part of the run's identity
```

**No brain is currently promoted.** `Assets/Agents/Football_v01` was deleted: its
four `.onnx` files came from the four-behavior base02 contract and are unloadable
against the six-behavior contract, and the 44 `m_Model` references to them in
`SCN_GAME` and `SCN_TRAIN_FOOTBALL` were already dead — `Agent_FootballPlayer`
assigns `behaviorParameters.Model` from `Agent_BrainRegistry` at `Awake`,
overwriting whatever the scene serialized. There is also no
`Resources/PoFootballBrains.asset`, so `ModelFor` returns null and **every player
runs `Heuristic`**. That is a supported, playable state, not a bug — but it means
nothing you watch right now is a trained policy.

**Scenes.** `SCN_MENU` (front end) → `SCN_GAME` (a scored game) and
`SCN_TRAIN_FOOTBALL` (the trainer's endless single plays). All three share one
composition root; `Systems_GameLifetimeScope._simMode` decides whether the
scoreboard, chains and stats are in the container at all.

**`PoFootball.Systems` must never reference `Unity.ML-Agents`.** Training-only
concerns reach it through interfaces the Systems assembly owns —
`Systems_ITrainingHandle` (terminal reward, end of episode),
`Systems_ITrainingEpisodeBoundary` (bound to a no-op in Game mode) and
`Systems_IInjectableBehaviour` (how `Agent_Telemetry` gets injected without the
scope naming it). Adding the reference back would put the trainer inside the
assembly that is supposed to know nothing about training, and ship ML-Agents in a
retail build's gameplay code — which is exactly what `Systems_Telemetry` did
before it became `Agent_Telemetry`.

The line of scrimmage is the ONLY thing that differs between the two modes, and
it is behind `Systems_ISpotProvider` — training still draws it from the same
seeded RNG, call for call. See [docs/GAME_LAYER.md](docs/GAME_LAYER.md).

**Physics** is 2D here (`Rigidbody2D`), so §2's joint/biomechanics clauses apply to
whatever articulated shapes get built; the invariants that always hold are:
fixed Δt = 0.02, actions only in `FixedUpdate`, actions normalized to [−1, 1],
fatigue derived from applied force/torque rather than the action vector.
`Time.fixedDeltaTime` is pinned in project settings — do not override it at runtime
or from a trainer flag, or every existing `.onnx` is being evaluated against
different dynamics than it was fitted against.

**Training** — the config and run-id are paired by name:

```powershell
.venv\Scripts\Activate.ps1

# In-editor smoke test: start the trainer, then press Play.
mlagents-learn Config\FootballBase06.yaml --run-id=football_base06 --torch-device=cpu

# Headless sweep — envs take CONSECUTIVE ports from --base-port.
mlagents-learn Config\FootballBase06.yaml --run-id=football_base06 --torch-device=cpu `
  --env=Builds\FootballEnv\PoFootball.exe --no-graphics `
  --base-port=5010 --num-envs=6

tensorboard --logdir results
```

**`--torch-device=cpu` is not optional.** The pinned `torch==2.5.1+cu121` carries no
kernels for this machine's RTX 5070 Ti (Blackwell, sm_120). `torch.cuda.is_available()`
still returns `True`, so the trainer will happily select CUDA and then die on the first
step with `CUDA error: no kernel image is available for execution on the device`. Pass
the flag on every run, or upgrade Torch to a CUDA 12.8 build and revisit the pin in
`requirements.txt`.

`FootballBase06.yaml` (the current config) carries **six** behaviors. The quarterback has its own brain
— it is the only one with discrete actions, and while it shared `OffenseSkill`
with the backs and receivers its play-call gradient was diluted five to one and
its entropy bonus could not be raised without injecting noise into four other
players' steering.

Record `--num-envs` in the run's `MANIFEST.md` — it changes how experience is
batched, so two runs with the same YAML and different `--num-envs` are not
comparable. Before `--force`, kill TensorBoard: it holds Windows file handles and
the wipe silently no-ops. Shut down in order: trainer → envs → TensorBoard.

Judge a self-play run on the `Self-play/ELO` curve. Mean reward is zero-sum
across offense and defense and stays near 0 however strong the policy becomes.
Watch `Call/Entropy` alongside it: the trainer's own `Policy/Entropy` sums across
every action head at once and sat at a healthy 3.5 through `football_base03`
while the play call had collapsed onto one option on 93% of downs.

**Promotion is gated.** An `.onnx` outlives the code it was trained against and
the runtime loads a mismatched one without complaint, so never copy one in by
hand:

```powershell
# audit what is already in Assets/Agents/ against the current contract
.venv\Scripts\python.exe Tools\promote_brain.py --verify

# promote — refuses unless every brain's observation and action shapes match,
# then writes MANIFEST.md and overwrites in place to preserve .meta GUIDs
.venv\Scripts\python.exe Tools\promote_brain.py --run football_base06 `
  --version 01 --num-envs 6

# reclaim disk: keeps the final and peak-ELO checkpoint per brain, never events
.venv\Scripts\python.exe Tools\prune_results.py --apply

# watch a live run's play-call entropy; exits nonzero the moment it collapses
.venv\Scripts\python.exe Tools\watch_entropy.py --run football_base06
```

The gate reads every expected value out of `Sensor_FootballState.cs`,
`Agent_ActionContract.cs`, `Systems_RoleTable.cs` and the training scene, so it
cannot drift from the contract it guards. The deleted `Assets/Agents/Football_v01`
is the reason it exists: those four brains came from `football_base02` at ~500k
steps against a contract that had since changed, the shipped `OffenseSkill.onnx`
had no discrete output at all, and the resulting "quarterback calls the same play
every down" was diagnosed as a collapsed policy for a long time. Its provenance is
recorded in [results/football_base02/MANIFEST.md](results/football_base02/MANIFEST.md).

---

## Tooling

Scene and GameObject changes go through **MCP tools**, not hand-edited `.unity`
YAML and not bespoke Editor scripts. Unity CLI (`unity` + `com.unity.pipeline`) is
the preferred surface; see [docs/TOOLING.md](docs/TOOLING.md) for the full
inventory and the precedence order.

- `unity status` — is an Editor connected?
- `unity list` / `unity command` — tools the Pipeline package exposes.
- `unity test` / `unity build` — batch-mode runs.

`.claude/` ships 20 agents, 27 commands, 42 skills and 26 hooks from
everything-claude-unity. The hooks block direct `.unity`/`.meta` edits on purpose —
that is the rule above being enforced, not an obstacle to route around.
