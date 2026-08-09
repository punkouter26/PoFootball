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
| Torch | 2.5.1+cu121 (RTX 2060) |

---

# UNITY_RULES — ML-Agents Physics Simulation

## 1. Naming
- `Assets/Agents/<Name>_v<NN>/` — `.onnx`, `*_Character.asset`, `MANIFEST.md`.
- Four script prefixes, each matching its folder under `Assets/Scripts/`: `Agent_`,
  `Sensor_`, `Reward_`, `Systems_` (referees, presentation, persistence, UI — the largest).
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
- Telemetry over HTTP *and* `StatsRecorder`. Kill TensorBoard before `--force`: it holds Windows handles and the wipe silently no-ops. Clean up trainer → envs → TensorBoard.
- **Judge self-play on ELO, not mean reward.**

---

## How these rules land in this repo

**Layout** (created; `Assets/Scripts/<X>/` holds the `<X>_*.cs` files):

```
Assets/Agents/                 promoted brains: <Name>_v<NN>/{*.onnx, *_Character.asset, MANIFEST.md}
Assets/Scripts/Agent/          Agent_*.cs      — Agent subclasses, action application
Assets/Scripts/Sensor/         Sensor_*.cs     — observation collection
Assets/Scripts/Reward/         Reward_*.cs     — reward terms
Assets/Scripts/Systems/        Systems_*.cs    — referee, presentation, persistence, UI
Assets/Scenes/                 SCN_*.unity, SCN_TRAIN_*.unity
Builds/<Name>Env/              headless training envs (git-ignored)
Config/<Name><Phase><NN>.yaml  trainer configs, 1:1 with run-id <name>_<phase><nn>
```

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
mlagents-learn Config\FootballBase01.yaml --run-id=football_base01

# Headless sweep — envs take CONSECUTIVE ports from --base-port.
mlagents-learn Config\FootballBase01.yaml --run-id=football_base01 `
  --env=Builds\FootballEnv\PoFootball.exe --no-graphics `
  --base-port=5010 --num-envs=6

tensorboard --logdir results
```

Record `--num-envs` in the run's `MANIFEST.md` — it changes how experience is
batched, so two runs with the same YAML and different `--num-envs` are not
comparable. Before `--force`, kill TensorBoard: it holds Windows file handles and
the wipe silently no-ops. Shut down in order: trainer → envs → TensorBoard.

Judge a self-play run on the `Self-play/ELO` curve. Mean reward is zero-sum
across offense and defense and stays near 0 however strong the policy becomes.

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
