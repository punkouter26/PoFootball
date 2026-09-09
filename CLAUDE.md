# CLAUDE.md — PoFootball

2D American football simulation. Players are flat 2D shapes whose geometry encodes
position (QB, receiver, lineman, defender, …); the game simulates a football game
being played rather than being directly controlled. Behaviour is learned with
Unity ML-Agents self-play.

| Property | Value |
|---|---|
| Unity | 6000.6.0f1 |
| Render pipeline | URP 17.6.0 |
| ML-Agents (C#) | `com.unity.ml-agents` 4.1.0 — comms API **1.5.0** |
| ML-Agents (Python) | `mlagents` 1.1.0 — comms API **1.5.0** |
| Python | 3.10.11, venv at `.venv/` |
| Torch | 2.5.1+cu121 — **do not upgrade**; 2.11 breaks the `.onnx` export |
| GPU | RTX 5070 Ti Laptop (Blackwell, sm_120) — unused; the CPU is 6x faster |

---

# UNITY_RULES — ML-Agents Physics Simulation

## 1. Naming
- `Assets/Agents/<Name>_v<NN>/` — `.onnx`, `*_Character.asset`, `MANIFEST.md`.
- Four script prefixes, each matching its folder under `Assets/Scripts/`: `Agent_`,
  `Sensor_`, `Reward_`, `Systems_` (referees, presentation, UI — the largest).
  `Assets/Editor/` follows the same folder-matching rule: everything there is
  `Editor_`, and every menu item hangs off the single root `Tools/PoFootball/`.
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
                               — no PoFootballBrains yet; Build Brain Table writes it
Builds/<Name>Env/              headless training envs (git-ignored) — FootballEnv only
Config/<Name><Phase><NN>.yaml  trainer configs, 1:1 with run-id <name>_<phase><nn>
Config/archive/                configs for superseded contracts; they will NOT run
results/<run-id>/MANIFEST.md   one per run — records --num-envs, which is part of the run's identity
```

**No brain is promoted, so every player runs `Heuristic`.** `Agent_BrainRegistry`
finds no `Resources/PoFootballBrains.asset`, `ModelFor` returns null, and the
built-in heuristic drives all 22 players.

`Assets/Agents/Football_v01` and that table both existed until 2026-08-22 and were
deleted, for the reason the stamp exists. They came from `football_base08` at
**contract revision 4** against a build now on **revision 7**. The table was worse
than merely old: it carried six entries numbered 0..5 from the six-brain-group era,
and `Systems_BrainGroup` has had three members since revision 7, so entries 3-5 were
undefined enum values and 0-2 pointed at entirely different populations than their
names suggested. Only the revision mismatch stood between it and a silently
scrambled load — and re-stamping the revision by hand, which is exactly what someone
does when they want the brains to load, would have removed it.

So `Agent_BrainTable.MatchesCurrentContract` now checks the **group set** as well as
the revision stamp, because the stamp is one integer and cannot see the entries
under it.

To promote: train against `Config/FootballBase11.yaml` (revision 8), run
`Tools/promote_brain.py`, then **`Tools > PoFootball > Build Brain Table`** in the
Editor. That last step is not optional — the Python side copies `.onnx` files but
cannot write the ScriptableObject that lists them.

Note that `m_Model` references serialized in the scenes are dead either way:
`Agent_FootballPlayer` assigns `behaviorParameters.Model` from
`Agent_BrainRegistry` at `Awake`, overwriting whatever the scene held.

Heuristic-only is a supported, playable state, not a bug — but nothing you watch
right now is a trained policy. **To change that, train a revision 8 run against the
three-behavior config and promote it.** `football_base11` is that run.

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

**Presentation** — the stadium light rig and 2D shadows, the post-processing
stack, the diagnostic overlay, the broadcast microphone and the UI typography all
live in `PoFootball.Views` and are built at runtime, gated on
`Systems_PresentationBudget`. That class gates **construction, not playback**: a
view that says no must not build its rig at all, because the cost a training sweep
must not pay is the setup. Why each is shaped the way it is — and why this project
deliberately has no sprite atlas and no `AudioMixer` asset — is in
[docs/PRESENTATION.md](docs/PRESENTATION.md).

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
$env:CUDA_VISIBLE_DEVICES = "-1"     # MANDATORY on this machine. See below.
mlagents-learn Config\FootballBase11.yaml --run-id=football_base11

# Headless sweep — envs take CONSECUTIVE ports from --base-port.
# REBUILD Builds/FootballEnv FIRST whenever the contract revision moved:
#   Unity: Tools > PoFootball > Build Training Env
# Revision 8 changed the DYNAMICS with every shape left identical, so a stale env
# is NOT refused by the handshake. That rebuild is on you, not on the stamp.
mlagents-learn Config\FootballBase11.yaml --run-id=football_base11 `
  --env=Builds\FootballEnv\PoFootball.exe --no-graphics `
  --base-port=5400 --num-envs=12

tensorboard --logdir results
```

### Judging whether the SIMULATION is football

`Systems_GameFlowSystem` logs a verdict line at every final whistle:

```
[PoFootball] REALISM  yards/play 6.24 | 4th downs faced 10 | TD/drive 0.36 | scrimmage plays 71
```

Reference values from real football: **~5.5 yards a play, a fourth down on roughly
one series in three, and 0.20-0.35 touchdowns per drive.**

Balance changes are judged on that line and nothing else — four were reasoned about
confidently during the revision 8 work and two of them made the game measurably
worse. **Single games are very noisy** (4.48 to 7.37 yards/play on one config), so
compare three-game means.

`Tools > PoFootball > Sim Speed > 8x` (editor-only, `Assets/Editor/Editor_SimSpeed.cs`)
turns a full game from ~10 minutes into ~100 seconds. It raises `Time.timeScale`
only — `fixedDeltaTime` is untouched, so it is the same measurement — and it always
resets to 1x on leaving play mode.

**Torch is pinned at 2.5.1+cu121 and upgrading it breaks promotion.** 2.11.0+cu128
was tried: it trained happily for 80,000 steps, then died at the first checkpoint with
`ModuleNotFoundError: No module named 'onnxscript'`. torch >= 2.6 exports ONNX via
onnxscript, which pulls `onnx>=1.17` -> numpy 2.x and protobuf 7.x, against the
`onnx==1.15.0` / `numpy==1.23.5` / `protobuf==3.20.3` that mlagents 1.1.0 requires. A
trainer that cannot write an `.onnx` cannot feed `Tools/promote_brain.py`, so the
faster-looking option is the useless one.

**Train on the CPU, and pass `CUDA_VISIBLE_DEVICES=-1` to make that stick.**

Measured on this machine, `OffenseSkill` steps 20k -> 60k, headless env:

| setup | steps/s |
|---|---|
| Editor, 1 env | 243 |
| headless, 3 envs | 395 |
| headless, 6 envs | 337 |
| headless, 12 envs | 404 |
| headless, 12 envs, `hidden_units: 256` | 563 |
| headless, 24 envs | **fails** — paging file too small |
| headless, 6 envs, **on the GPU** | **55** |

Two things fall out of that. The GPU is 6x *slower* — the net is 512 x 2 at batch
2048, so launch overhead and host transfers cost more than the matmuls save. And
env count barely matters, because the trainer is the bottleneck, not the game: at 6
envs the six `PoFootball.exe` copies burned 81 CPU-seconds while python burned 2,877.
Use a handful of envs and do not expect much from adding more. 24 envs does not run
at all — each worker loads its own copy of torch and Windows runs out of paging file.

`--torch-device=cpu` and `torch_settings: device: cpu` do NOT work on their own. Both
were tried against live runs and both still died on CUDA. `mlagents/torch_utils/torch.py`
calls `set_torch_config(TorchSettings(device=None))` at **import** time; with no device
given it picks `"cuda"` whenever a GPU is visible and calls
`torch.set_default_device("cuda")`. When the real config arrives moments later asking
for `cpu`, the `else` branch only sets the dtype — it never puts the default device
back. Hiding the GPU is what fixes it. Use `-1`; an empty string is ignored on Windows.

`FootballBase06.yaml` (long superseded — the current config is
`FootballBase11.yaml`, three behaviors, revision 8) carries **six** behaviors. The quarterback has its own brain
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

---

## Android release & Play internal testing

Ported from the PoRacer pipeline on 2026-08-28; PoSumo is the original, already
shipping on the punkouter27 Play account.

### Identity (permanent — do not change after the first upload)

| Property | Value |
|---|---|
| Application id | `com.punkoutersoftware.pofootball` |
| Version / code | `1.0.0` / `1` — bump `VERSION_CODE` in `Editor_ConfigureAndroidRelease` for every upload; Play rejects a reused code |
| min / target SDK | 26 / 36 (Play requires target 36 for new uploads from 2026-08-31) |
| Architecture | ARM64, IL2CPP, Release |
| Orientation | Portrait is locked in `Editor_ConfigureAndroidRelease`. |

### Secrets live OUTSIDE the repo

`C:/Users/punko/Downloads/PoFootball-Release/`

- `pofootball-upload.jks` — the upload key. **Losing it means losing the ability to
  update the app.** Back it up somewhere other than this machine.
- `pofootball-upload.pass` — the store/alias password, one line.
- `upload_certificate.pem` — the public cert, for Play App Signing.
- `play-service-account.json` — NOT created yet; see the SETUP block at the top of
  `Tools/play_publish.py`.

Unity does not serialize keystore passwords into `ProjectSettings`, so both Android
builders read `POFOOTBALL_KEYSTORE_PASS` first and fall back to the `.pass` file.
Without either, the build **aborts** rather than producing an unsigned artifact.

### The tools

| Tool | What it does |
|---|---|
| *Tools → PoFootball → Configure Android Release Settings* | One-shot: identity, SDK levels, orientation, and the launcher icons (adaptive + round + legacy, 6 densities) from `Assets/Icons/`. Re-run after changing icon art |
| *Tools → PoFootball → Build Android AAB (Play release)* | Signed bundle → `Builds/Android/PoFootball.aab`. Logs `AAB BUILD RESULT:` |
| *Tools → PoFootball → Build Android APK* | Sideloadable APK on the SAME key, so it installs over a Play build → `Builds/Android/PoFootball.apk`. Logs `BUILD RESULT:` |
| `Tools/play_publish.py` | Uploads a built AAB. Defaults to the `internal` track as a `draft`; `--dry-run` rehearses and discards |

`Tools/play_publish.py` needs its own venv (`Tools/publish-venv`). Do not install it
into `.venv` — that one carries load-bearing ml-agents/torch pins, and the C#/Python
ml-agents versions must stay in exact parity.

### The shipped scene list is explicit

`Editor_BuildAndroidAAB.SHIP_SCENES` names the player's scenes in boot order:

  0. `Assets/Scenes/SCN_MENU.unity`
  1. `Assets/Scenes/SCN_GAME.unity`

It is a hardcoded list, not whatever is ticked in Build Settings, because Build
Settings also carries SCN_TRAIN_FOOTBALL — training scenes that would bloat the bundle
and, depending on order, boot a tester straight into a training rig. A scene named
here that is missing on disk **aborts** the build.

### The icons

`Assets/Icons/` holds `AppIcon_Adaptive_Background.png` and
`AppIcon_Adaptive_Foreground.png` (432x432, the API 26+ pair) and
`AppIcon_Legacy.png` (512x512, round and pre-adaptive launchers). The adaptive
FOREGROUND art must stay inside the middle 66% of its canvas — every OEM launcher
masks the outside to a different shape.

The Play STORE icon is a different file, in `StoreAssets/PlayStoreIcon_512.png`:
full-bleed, because Play rounds it itself. Do not swap the two.

### What still needs a human in a browser

1. Play Console → Create app, with the application id above.
2. Store listing, content rating and data-safety forms — drafted in
   `StoreAssets/play-listing.md`.
3. Upload the first bundle by hand; Play refuses an API upload before the app is set up.
4. Create a service account, grant it release permission ON THE APP, and drop its
   JSON key next to the keystore.

After that, `python Tools/play_publish.py --track internal` owns every upload.

### Headless

```
Unity.exe -batchmode -quit -nographics -projectPath <root> -buildTarget Android ^
  -executeMethod PoFootball.EditorTools.Editor_BuildAndroidAAB.Build -logFile <log>
```

Grep the log for `AAB BUILD RESULT:` — that line is the outcome.
