# Football_v01

Promoted 2026-10-03 from `results/football_base12` at repository commit `86d9cc7`.

## Contract

- Vector observations: **36**
- Ray observations: **84**
- Continuous actions: **2** (**4** for `Quarterback`)
- Discrete branches: **[7, 2]** on `Quarterback`, none elsewhere

Every file below was validated against these numbers by `Tools/promote_brain.py` before being copied. A brain that does not match is refused, not warned about.

## Run

- Run id: `football_base12`
- `--num-envs`: **4** — changes how experience is batched; runs with different values are not comparable
- Judge on `Call/Entropy` and the `Play/*` and `Pass/*` rates — self-play is not running, so there is no `Self-play/ELO` — then on the REALISM section below (`--check-realism`). Mean reward is zero-sum and stays near 0 however strong the policies get (UNITY_RULES §4).
- `Control/SpeedClampRate` was at most 0.001 for every brain.

## Files

| Brain | Source checkpoint |
|---|---|
| `Defense.onnx` | `results/football_base12/Defense/Defense-7700858.onnx` |
| `Offense.onnx` | `results/football_base12/Offense/Offense-7000780.onnx` |
| `Quarterback.onnx` | `results/football_base12/Quarterback/Quarterback-700078.onnx` |

## Realism

Judged 2026-10-03 from `results/realism/realism-20261003-102921.json`, 3 games: **FAILED**

- yards per play: **18.79** (band 4.50-6.50) OUT OF BAND
- touchdowns per drive: **0.78** (band 0.15-0.40) OUT OF BAND
- fourth downs per game: **9.3** (reported, not judged)
