# football_base05 — overnight run, 2026-08-11

Started 00:11 EDT. Watchdog holds it until 08:17 EDT, then stops watching and
leaves training running.

```
mlagents-learn Config\FootballBase05.yaml --run-id=football_base05 `
  --env=Builds\FootballEnv\PoFootball.exe --no-graphics `
  --base-port=5010 --num-envs=6
```

`--num-envs=6`, same as base04, so the two are comparable on batching
(UNITY_RULES §4).

---

## Why base04 was stopped

It was live at ~1.04M steps when the night started, and three of its six brains
had been dead for 74 minutes:

| brain | last step | last written |
|---|---|---|
| OffenseLine | 1,040,000 | 0.7 min ago |
| OffenseSkill | 1,040,000 | 0.7 min ago |
| Quarterback | 200,000 | 3.8 min ago |
| DefenseLine | 320,000 | **73.9 min ago** |
| DefenseSecondary | 320,000 | **73.9 min ago** |
| DefenseBox | 240,000 | **73.9 min ago** |

They were running at 8–11k steps/min when alive, so this is a stall, not slow
progress. Offense gained ~700k steps against a defense that had stopped
learning — i.e. it was overfitting to a frozen opponent.

### Cause

From `Logs/football_base04.log`, one line per brain at startup:

```
[INFO] Connected new brain: OffenseLine?team=0
[INFO] Connected new brain: DefenseLine?team=1
```

Every behavior carries exactly one team id. The behavior name comes from the
brain group (`Systems_RoleTable.BehaviorNameOf`) and the team id from the side,
so an offense behavior is only ever team 0 and a defense behavior only ever
team 1. No behavior ever sees both.

ML-Agents self-play does not model two different behaviors playing each other.
It models **one** behavior playing itself — a learning team and a frozen ghost
team under the same behavior name, swapped every `team_change` steps. Given a
behavior containing only one team, the ghost trainer has a learning team and no
opponent to swap in. At `team_change` (250k) the defense behaviors swapped out
and nothing swapped back: DefenseLine and DefenseSecondary froze at exactly
320k, DefenseBox at 240k.

So self-play was never actually running in base01–base04. It was a ghost
trainer with no ghost, and it was silently killing half the brains.

### Fix

`Config/FootballBase05.yaml` is byte-identical to `FootballBase04.yaml` with
the single `self_play` block removed from the `&football_brain` anchor (all six
behaviors inherited it from there). Everything else — hyperparameters, the
quarterback's raised `beta: 2.0e-2`, `normalize: false`, `time_horizon: 150` —
is unchanged.

**This is a judgement call made while you were asleep.** It is reversible:
`results/football_base04/` is untouched, including its
`OffenseLine-999880.onnx` / `OffenseSkill-999880.onnx` checkpoints. If you would
rather have self-play, the honest way to get it is a behavior that plays both
sides — one brain name shared across offense and defense with the field
mirrored — which is a change to `Systems_RoleTable`, not a config knob.

---

## What to judge this run on

UNITY_RULES §4 says judge self-play on ELO rather than mean reward. That applies
to one behavior playing itself, where reward is zero-sum and cancels. Here
offense and defense are separate behaviors with separate reward streams, ELO no
longer exists, and **mean reward per behavior is the signal** — the two should
move in opposition.

At 60k steps (5 minutes in) it already reads correctly:

```
offense  -0.50      defense  +0.40
```

`Agent_Telemetry` — moved out of `PoFootball.Systems` earlier today — is
recording in the headless build. Confirmed series:

```
Call/Entropy 1.304        (max 1.609 for five slots)
Call/None    0.000        the quarterback always commits to a call
Call/Keep    0.286   Call/Pass 0.200
Call/HandoffHalfback 0.257   Call/HandoffFullback 0.257
Play/TackleRate 0.314   Play/TimeExpiredRate 0.514   Play/TouchdownRate 0.000
Pass/CompletionPerAttempt 0.143
```

**`Call/Entropy` at 1.30 of a possible 1.61 is the headline.** This is the
metric that would have caught football_base03, where the play call had collapsed
onto one option on 93% of downs while the trainer's own `Policy/Entropy` sat at
a healthy-looking 3.5 because it sums every action head at once. All four real
calls are being sampled here and `Call/None` is zero. The quarterback brain
split plus `beta: 2.0e-2` is doing what it was meant to.

`Play/TimeExpiredRate` at 0.51 and `Play/TouchdownRate` at 0.00 are the things
to watch: half of all plays are still running out the 750-tick cap and nobody
has scored. Both are normal five minutes into a run from scratch, and both are
the numbers that should move first if it is learning.

---

## Watchdog

`Tools/watch_training.ps1` polls every 5 minutes and resumes the run with
`--resume` if the trainer has died, after clearing any orphaned envs. It logs to
`Logs/football_base05_watchdog.log`.

To stop the night early, create a file named `STOP_TRAINING` in the project
root — the watchdog exits without touching the run.

To stop training by hand, shut down in order **trainer → envs → TensorBoard**.
Killing the trainer alone leaves six `PoFootball.exe` and six python workers
holding ports 5010–5015, and the next run's handshake then hangs with no error.
That is exactly what had to be cleaned up before this run could start.

---

## In the morning

Nothing is promoted. `Assets/Agents/Football_v01` **has since been deleted** — it
held the stale football_base02 brains, which were unloadable against the
six-behavior contract. No `Resources/PoFootballBrains.asset` exists either, so
`Agent_BrainRegistry.ModelFor` returns null, both scenes run
`Agent_FootballPlayer.Heuristic`, and the game is playable but not trained.

To promote once this run is worth promoting:

```powershell
.venv\Scripts\python.exe Tools\promote_brain.py --verify
.venv\Scripts\python.exe Tools\promote_brain.py --run football_base05 `
    --version 02 --num-envs 6
```

`promote_brain.py` predates `Agent_BrainTable` and will not populate it. Until
it does, a promoted brain still will not load — the table is what binds a model
to a brain group now, and it must be stamped
`Agent_ActionContract.CONTRACT_REVISION` (currently 1) or it is ignored
wholesale. Teaching the promotion tool to write the table is the next piece of
work.
