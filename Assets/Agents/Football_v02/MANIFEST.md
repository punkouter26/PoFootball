# Football_v02

Promoted 2026-10-03 from `results/football_base14` at repository commit `be883a3`.

## Contract

- Vector observations: **36**
- Ray observations: **84**
- Continuous actions: **2** (**4** for `Quarterback`)
- Discrete branches: **[7, 2]** on `Quarterback`, none elsewhere

Every file below was validated against these numbers by `Tools/promote_brain.py` before being copied. A brain that does not match is refused, not warned about.

## Run

- Run id: `football_base14`
- `--num-envs`: **4** — changes how experience is batched; runs with different values are not comparable
- Judge on `Call/Entropy` and the `Play/*` and `Pass/*` rates — self-play is not running, so there is no `Self-play/ELO` — then on the REALISM section below (`--check-realism`). Mean reward is zero-sum and stays near 0 however strong the policies get (UNITY_RULES §4).
- `Control/SpeedClampRate` was at most 0.001 for every brain.

## Files

| Brain | Source checkpoint |
|---|---|
| `Defense.onnx` | `results/football_base14/Defense/Defense-1230454.onnx` |
| `Offense.onnx` | `results/football_base14/Offense/Offense-1200410.onnx` |
| `Quarterback.onnx` | `results/football_base14/Quarterback/Quarterback-32057.onnx` |

## Realism

Judged 2026-10-03 from `results/realism/realism-20261003-161208.json`, 3 games: **FAILED**

- yards per play: **6.80** (band 4.50-6.50) OUT OF BAND
- touchdowns per drive: **0.30** (band 0.15-0.40) ok
- fourth downs per game: **13.7** (reported, not judged)
