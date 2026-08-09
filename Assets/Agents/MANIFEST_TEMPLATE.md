# <Name>_v<NN>

Copy this file into `Assets/Agents/<Name>_v<NN>/MANIFEST.md` when a brain is
promoted out of `results/`. One folder per promoted brain; the folder is the
unit of record, and the `.onnx` inside it is overwritten **in place** on
re-promotion so its `.meta` GUID (and every scene reference to it) survives.

## Provenance

| Field | Value |
|---|---|
| Run id | `football_base01` |
| Config | `Config/FootballBase01.yaml` |
| Promoted from | `results/football_base01/FootballPlayer-<steps>.onnx` |
| Promoted on | YYYY-MM-DD |
| Trainer | mlagents 1.1.0 / comms 1.5.0 |
| C# package | com.unity.ml-agents 4.1.0 / comms 1.5.0 |
| Torch | 2.5.1+cu121 |

## How it was trained

| Field | Value |
|---|---|
| `--num-envs` | 6 |
| `--base-port` | 5010 |
| `--no-graphics` | yes |
| Env build | `Builds/FootballEnv/PoFootball.exe` |
| Steps | |
| Wall clock | |
| Seed | 1 |

`--num-envs` changes how experience is batched, so it is part of the run's
identity — two runs off the same YAML with different values are not comparable.

## Result

| Metric | Value |
|---|---|
| **ELO (final)** | |
| ELO (peak) | |
| Mean reward | (zero-sum; not the criterion) |
| Episode length | |
| Policy loss / value loss | |

Self-play is judged on **ELO**. Mean reward in an offense-vs-defense run sits
near 0 regardless of how strong the policy is, because one side's gain is the
other's loss.

## Observation / action contract

Record the exact shapes — a brain is only loadable by an agent whose sensors and
actuators still match what it was fitted against.

| | |
|---|---|
| Vector observation size | |
| Stacked vectors | |
| Continuous actions | |
| Discrete branches | |
| Sensors (`Sensor_*.cs`) | |
| Reward terms (`Reward_*.cs`) | |

## Notes

Anything that would surprise the next person: curriculum stage, what it learned
to exploit, known failure modes, whether it was initialised from an earlier run
(`--initialize-from`).
