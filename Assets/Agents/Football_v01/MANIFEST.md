# Football_v01

Promoted 2026-08-21 from `results/football_base08` at repository commit `26812b5`.

## Contract

- Vector observations: **32**
- Ray observations: **52**
- Continuous actions: **2** (**4** for `Quarterback`)
- Discrete branches: **[5, 2]** on `Quarterback`, none elsewhere

Every file below was validated against these numbers by `Tools/promote_brain.py` before being copied. A brain that does not match is refused, not warned about.

## Run

- Run id: `football_base08`
- `--num-envs`: **12** — changes how experience is batched; runs with different values are not comparable
- Judge on `Self-play/ELO`, `Call/Entropy` and the `Play/*` and `Pass/*` rates. Mean reward is zero-sum here and stays near 0 however strong the policies get (UNITY_RULES §4).

## Files

| Brain | Source checkpoint |
|---|---|
| `DefenseBox.onnx` | `results/football_base08/DefenseBox/DefenseBox-2199912.onnx` |
| `DefenseLine.onnx` | `results/football_base08/DefenseLine/DefenseLine-2899998.onnx` |
| `DefenseSecondary.onnx` | `results/football_base08/DefenseSecondary/DefenseSecondary-2899998.onnx` |
| `OffenseLine.onnx` | `results/football_base08/OffenseLine/OffenseLine-3699935.onnx` |
| `OffenseSkill.onnx` | `results/football_base08/OffenseSkill/OffenseSkill-3699935.onnx` |
| `Quarterback.onnx` | `results/football_base08/Quarterback/Quarterback-699991.onnx` |
