# Football_v03

Promoted 2026-10-17 from `results/football_base22` at repository commit `af3488c`.

## Contract

- Vector observations: **50**
- Ray observations: **84**
- Continuous actions: **2** (**4** for `Quarterback`)
- Discrete branches: **[8, 2]** on `Quarterback`, none elsewhere

Every file below was validated against these numbers by `Tools/promote_brain.py` before being copied. A brain that does not match is refused, not warned about.

## Run

- Run id: `football_base22`
- `--num-envs`: **4** — changes how experience is batched; runs with different values are not comparable
- Judge on `Call/Entropy` and the `Play/*` and `Pass/*` rates — self-play is not running, so there is no `Self-play/ELO` — then on the REALISM section below (`--check-realism`). Mean reward is zero-sum and stays near 0 however strong the policies get (UNITY_RULES §4).
- `Control/SpeedClampRate` was at most 0.001 for every brain.

## Files

| Brain | Source checkpoint |
|---|---|
| `Defense.onnx` | `results/football_base22/Defense/Defense-3441493.onnx` |
| `DefenseCall.onnx` | `results/football_base22/DefenseCall/DefenseCall-5527.onnx` |
| `Offense.onnx` | `results/football_base22/Offense/Offense-3128630.onnx` |
| `Quarterback.onnx` | `results/football_base22/Quarterback/Quarterback-312863.onnx` |

## Realism

Judged 2026-10-17 from `results/realism/realism-20261017-112731.json`, 4 games: **FAILED**

- yards per play: **8.21** (band 4.50-6.50) OUT OF BAND
- touchdowns per drive: **0.42** (band 0.15-0.40) OUT OF BAND
- rush yards per carry: **8.89** (band 3.00-6.00) OUT OF BAND
- completion rate: **0.87** (band 0.50-0.80) OUT OF BAND
- first downs per play: **0.10** (band 0.15-0.45) OUT OF BAND
- fourth downs per game: **11.8** (reported, not judged)
