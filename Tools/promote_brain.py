"""Promote trained .onnx brains into Assets/Agents/, but only if they fit.

WHY THIS EXISTS
---------------
Assets/Agents/Football_v01 shipped four brains taken from run football_base02 at
about 500k steps, against a scene that had since moved to a different contract.
The mismatch was total and completely silent:

    Assets/Agents/Football_v01/OffenseSkill.onnx   obs_1 = 25, continuous = 2,
                                                   no discrete output at all
    what the scene asks for                        obs_1 = 32, continuous = 4,
                                                   discrete branches [5, 2]

Nothing compared them, so the quarterback's play-call branch read zero forever,
zero decoded to KeepQuarterback under the old encoding, and the ball never
changed hands in a single played game. It looked exactly like a collapsed policy
and was diagnosed as one for a long time.

An .onnx is a promotion artifact, not a build output: it is copied in by hand,
it outlives the code it was trained against, and the runtime accepts a
mismatched one without complaint. So the check has to live at the copy.

WHAT IT CHECKS
--------------
Every expected value is READ from the repository rather than typed here, so this
script cannot itself drift from the contract it is guarding:

    OBSERVATION_SIZE, PLAY_CALL_BRANCH_SIZE   Assets/Scripts/Sensor/Sensor_FootballState.cs
    continuous action counts, THROW_BRANCH_SIZE  Assets/Scripts/Agent/Agent_ActionContract.cs
    behavior names, which one carries the QB actions  Assets/Scripts/Systems/Model/Systems_RoleTable.cs
    ray sensor observation width               Assets/Scenes/SCN_TRAIN_FOOTBALL.unity

Usage
-----
    .venv/Scripts/python.exe Tools/promote_brain.py --run football_base04 \
        --version 02 --num-envs 4

    # check without writing anything
    .venv/Scripts/python.exe Tools/promote_brain.py --run football_base04 \
        --version 02 --num-envs 4 --dry-run

The .onnx is overwritten IN PLACE so the .meta GUID survives and every scene
reference keeps pointing at it (UNITY_RULES §4).
"""

from __future__ import annotations

import argparse
import datetime
import re
import shutil
import subprocess
import sys
from pathlib import Path

try:
    import onnx
    from onnx import numpy_helper
except ImportError:  # pragma: no cover - environment problem, not a logic path
    sys.exit(
        "onnx is not installed in this interpreter. Run this with the project venv:\n"
        "    .venv/Scripts/python.exe Tools/promote_brain.py ..."
    )

REPO_ROOT = Path(__file__).resolve().parent.parent

SENSOR_CS = REPO_ROOT / "Assets/Scripts/Sensor/Sensor_FootballState.cs"
ACTION_CS = REPO_ROOT / "Assets/Scripts/Agent/Agent_ActionContract.cs"
ROLE_TABLE_CS = REPO_ROOT / "Assets/Scripts/Systems/Model/Systems_RoleTable.cs"
TRAIN_SCENE = REPO_ROOT / "Assets/Scenes/SCN_TRAIN_FOOTBALL.unity"
RESULTS_DIR = REPO_ROOT / "results"
AGENTS_DIR = REPO_ROOT / "Assets/Agents"


class ContractError(RuntimeError):
    """A value could not be read from the repository. Never guessed around."""


# --------------------------------------------------------------------------
# Reading the contract out of the repository
# --------------------------------------------------------------------------

def _read_int_const(source: Path, name: str) -> int:
    text = source.read_text(encoding="utf-8")
    match = re.search(rf"\b{name}\s*=\s*([0-9]+)\s*;", text)
    if not match:
        raise ContractError(f"could not find const int {name} in {source.name}")
    return int(match.group(1))


def read_behavior_names() -> list[str]:
    """Every string BehaviorNameOf can return, in source order."""
    text = ROLE_TABLE_CS.read_text(encoding="utf-8")
    start = text.find("BehaviorNameOf")
    if start < 0:
        raise ContractError("BehaviorNameOf not found in Systems_RoleTable.cs")

    # The method ends at the next method declaration; the return literals in
    # between are the complete set, including the default case.
    end = text.find("public static", text.find("{", start))
    names = re.findall(r'return\s+"([A-Za-z0-9_]+)"\s*;', text[start:end])
    if not names:
        raise ContractError("BehaviorNameOf returned no string literals")
    return names


def read_quarterback_behavior() -> str:
    """The one behavior whose brain carries the discrete actions."""
    text = ROLE_TABLE_CS.read_text(encoding="utf-8")
    match = re.search(
        r"HasQuarterbackActions[^{]*\{[^}]*group\s*==\s*Systems_BrainGroup\.(\w+)",
        text,
        re.DOTALL,
    )
    if not match:
        raise ContractError("could not determine which brain has the quarterback actions")

    group = match.group(1)
    names = read_behavior_names()
    if group not in names:
        raise ContractError(
            f"HasQuarterbackActions names group '{group}', which BehaviorNameOf never returns"
        )
    return group


def read_ray_observation_size() -> int:
    """
    Width of the RayPerceptionSensor2D observation, computed from the scene.

    ML-Agents emits (2 * raysPerDirection + 1) rays, each contributing one float
    per detectable tag plus a nothing-hit flag and a hit fraction. It is authored
    in the scene rather than in code, so a component tweak silently resizes every
    policy's input — the same class of bug this whole script exists for.
    """
    text = TRAIN_SCENE.read_text(encoding="utf-8", errors="replace")

    block = re.search(
        r"m_DetectableTags:(.*?)m_RaysPerDirection:\s*(\d+)", text, re.DOTALL
    )
    if not block:
        raise ContractError("no RayPerceptionSensor2D found in the training scene")

    tag_count = len(re.findall(r"^\s*-\s+\S+", block.group(1), re.MULTILINE))
    rays_per_direction = int(block.group(2))

    return (2 * rays_per_direction + 1) * (tag_count + 2)


def expected_contract() -> dict:
    observation_size = _read_int_const(SENSOR_CS, "OBSERVATION_SIZE")
    play_call_slots = _read_int_const(SENSOR_CS, "PLAY_CALL_SLOTS")

    base_continuous = _read_int_const(ACTION_CS, "BASE_CONTINUOUS_ACTIONS")
    qb_continuous = _read_int_const(ACTION_CS, "QUARTERBACK_CONTINUOUS_ACTIONS")
    throw_branch = _read_int_const(ACTION_CS, "THROW_BRANCH_SIZE")

    return {
        "vector_observations": observation_size,
        "ray_observations": read_ray_observation_size(),
        "base_continuous": base_continuous,
        "quarterback_continuous": qb_continuous,
        "quarterback_branches": [play_call_slots + 1, throw_branch],
        "behaviors": read_behavior_names(),
        "quarterback_behavior": read_quarterback_behavior(),
    }


# --------------------------------------------------------------------------
# Reading the contract out of an .onnx
# --------------------------------------------------------------------------

def _tensor_dims(value_info) -> list:
    return [
        dim.dim_value if dim.dim_value else dim.dim_param
        for dim in value_info.type.tensor_type.shape.dim
    ]


def inspect_onnx(path: Path) -> dict:
    model = onnx.load(str(path))
    graph = model.graph

    inputs = {i.name: _tensor_dims(i) for i in graph.input}
    outputs = {o.name: _tensor_dims(o) for o in graph.output}

    # ML-Agents names vector-observation inputs obs_N in sensor order. The ray
    # sensor and the vector sensor are told apart by width, not by index, because
    # sensor ordering is not part of any documented contract.
    observation_widths = sorted(
        dims[1] for name, dims in inputs.items()
        if name.startswith("obs_") and len(dims) == 2 and isinstance(dims[1], int)
    )

    continuous = None
    if "continuous_actions" in outputs:
        dims = outputs["continuous_actions"]
        if len(dims) == 2 and isinstance(dims[1], int):
            continuous = dims[1]

    branches = []
    if "discrete_actions" in outputs:
        for initializer in graph.initializer:
            if initializer.name.startswith("discrete_action_output_shape"):
                branches = [int(v) for v in numpy_helper.to_array(initializer).flatten()]
                break
        if not branches:
            # Fall back to the action mask width, which is the sum of the
            # branches — enough to detect a mismatch even if it cannot name it.
            mask = inputs.get("action_masks")
            if mask and len(mask) == 2 and isinstance(mask[1], int):
                branches = [mask[1]]

    return {
        "observation_widths": observation_widths,
        "continuous": continuous,
        "branches": branches,
        "has_discrete": "discrete_actions" in outputs,
    }


def validate(behavior: str, path: Path, contract: dict) -> list[str]:
    """Returns a list of human-readable problems. Empty means the brain fits."""
    actual = inspect_onnx(path)
    problems: list[str] = []

    is_quarterback = behavior == contract["quarterback_behavior"]

    wanted_observations = sorted(
        [contract["ray_observations"], contract["vector_observations"]]
    )
    if actual["observation_widths"] != wanted_observations:
        problems.append(
            f"observation widths {actual['observation_widths']} "
            f"!= expected {wanted_observations} "
            f"(ray {contract['ray_observations']}, vector {contract['vector_observations']})"
        )

    wanted_continuous = (
        contract["quarterback_continuous"] if is_quarterback else contract["base_continuous"]
    )
    if actual["continuous"] != wanted_continuous:
        problems.append(
            f"continuous actions {actual['continuous']} != expected {wanted_continuous}"
        )

    if is_quarterback:
        if not actual["has_discrete"]:
            problems.append(
                "no discrete_actions output — this brain cannot call a play at all, "
                "so every down decodes to whatever index 0 means"
            )
        elif actual["branches"] != contract["quarterback_branches"]:
            problems.append(
                f"discrete branches {actual['branches']} "
                f"!= expected {contract['quarterback_branches']}"
            )
    elif actual["has_discrete"]:
        problems.append("has a discrete_actions output but this brain should have none")

    return problems


# --------------------------------------------------------------------------
# Promotion
# --------------------------------------------------------------------------

def latest_checkpoint(run_dir: Path, behavior: str) -> Path:
    """
    The behavior's final export. mlagents-learn writes <behavior>.onnx at the end
    of a run and <behavior>-<steps>.onnx at each checkpoint; prefer the former and
    fall back to the highest-numbered checkpoint if the run was interrupted.
    """
    behavior_dir = run_dir / behavior
    if not behavior_dir.is_dir():
        raise ContractError(f"no directory for behavior '{behavior}' in {run_dir}")

    final = behavior_dir / f"{behavior}.onnx"
    if final.is_file():
        return final

    checkpoints = sorted(
        behavior_dir.glob(f"{behavior}-*.onnx"),
        key=lambda p: int(re.search(r"-(\d+)\.onnx$", p.name).group(1)),
    )
    if not checkpoints:
        raise ContractError(f"no .onnx files for behavior '{behavior}' in {behavior_dir}")

    return checkpoints[-1]


def git_commit() -> str:
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "--short", "HEAD"], cwd=REPO_ROOT, text=True
        ).strip()
    except (subprocess.CalledProcessError, FileNotFoundError):
        return "unknown"


def write_manifest(
    target_dir: Path, run: str, num_envs: int, sources: dict, contract: dict
) -> None:
    """
    The manifest CLAUDE.md §1 requires. Football_v01 shipped without one, which is
    why nobody could tell that its brains predated the contract they ran against.
    --num-envs is recorded because it changes how experience is batched, so two
    runs with the same YAML and different --num-envs are not comparable.
    """
    lines = [
        f"# {target_dir.name}",
        "",
        f"Promoted {datetime.date.today().isoformat()} from `results/{run}` "
        f"at repository commit `{git_commit()}`.",
        "",
        "## Contract",
        "",
        f"- Vector observations: **{contract['vector_observations']}**",
        f"- Ray observations: **{contract['ray_observations']}**",
        f"- Continuous actions: **{contract['base_continuous']}** "
        f"(**{contract['quarterback_continuous']}** for "
        f"`{contract['quarterback_behavior']}`)",
        f"- Discrete branches: **{contract['quarterback_branches']}** on "
        f"`{contract['quarterback_behavior']}`, none elsewhere",
        "",
        "Every file below was validated against these numbers by "
        "`Tools/promote_brain.py` before being copied. A brain that does not "
        "match is refused, not warned about.",
        "",
        "## Run",
        "",
        f"- Run id: `{run}`",
        f"- `--num-envs`: **{num_envs}** — changes how experience is batched; runs "
        "with different values are not comparable",
        "- Judge on `Self-play/ELO`, `Call/Entropy` and the `Play/*` and `Pass/*` "
        "rates. Mean reward is zero-sum here and stays near 0 however strong the "
        "policies get (UNITY_RULES §4).",
        "",
        "## Files",
        "",
        "| Brain | Source checkpoint |",
        "|---|---|",
    ]
    for behavior, source in sorted(sources.items()):
        lines.append(f"| `{behavior}.onnx` | `{source.relative_to(REPO_ROOT).as_posix()}` |")

    lines.append("")
    (target_dir / "MANIFEST.md").write_text("\n".join(lines), encoding="utf-8")


def verify_promoted(contract: dict) -> int:
    """
    Audit what is ALREADY in Assets/Agents/ against the current contract.

    This is the mode that matters most day to day. Promotion happens once; the
    code around a promoted brain then keeps moving, and nothing tells you when it
    has moved far enough that the brain no longer fits. Football_v01 sat
    mismatched through an entire debugging effort that was looking for a training
    problem.
    """
    directories = sorted(
        d for d in AGENTS_DIR.iterdir() if d.is_dir() and any(d.glob("*.onnx"))
    )
    if not directories:
        print(f"No promoted brains found under {AGENTS_DIR.relative_to(REPO_ROOT).as_posix()}.")
        return 0

    failed = False

    for directory in directories:
        print(f"{directory.relative_to(REPO_ROOT).as_posix()}")

        if not (directory / "MANIFEST.md").exists():
            print("  MANIFEST.md is missing (CLAUDE.md section 1 requires one)")
            failed = True

        onnx_files = sorted(directory.glob("*.onnx"))
        if not onnx_files:
            print("  no .onnx files")
            continue

        promoted_names = {p.stem for p in onnx_files}
        missing = [b for b in contract["behaviors"] if b not in promoted_names]
        if missing:
            print(f"  no brain promoted for: {', '.join(missing)}")
            failed = True

        for path in onnx_files:
            behavior = path.stem
            if behavior not in contract["behaviors"]:
                print(f"  {behavior:<20} ORPHAN   no Systems_BrainGroup maps to this name")
                failed = True
                continue

            problems = validate(behavior, path, contract)
            if problems:
                failed = True
                print(f"  {behavior:<20} STALE")
                for problem in problems:
                    print(f"    - {problem}")
            else:
                print(f"  {behavior:<20} ok")
        print()

    if failed:
        print(
            "At least one promoted brain does not match the current contract. "
            "It will load without complaint and return zeros or garbage."
        )
        return 1

    print("Every promoted brain matches the current contract.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", help="run id under results/, e.g. football_base04")
    parser.add_argument("--version", help="two-digit version, e.g. 02")
    parser.add_argument(
        "--num-envs", type=int,
        help="the --num-envs the run used. Recorded in MANIFEST.md; it changes batching.",
    )
    parser.add_argument("--name", default="Football", help="agent family name")
    parser.add_argument("--dry-run", action="store_true", help="validate, write nothing")
    parser.add_argument(
        "--verify", action="store_true",
        help="audit the brains already in Assets/Agents/ against the current contract",
    )
    args = parser.parse_args()

    try:
        contract = expected_contract()
    except ContractError as error:
        print(f"FAILED to read the contract from the repository: {error}")
        return 2

    if args.verify:
        return verify_promoted(contract)

    missing_arguments = [
        name for name, value in
        (("--run", args.run), ("--version", args.version), ("--num-envs", args.num_envs))
        if value is None
    ]
    if missing_arguments:
        parser.error(f"promotion requires {', '.join(missing_arguments)}")

    run_dir = RESULTS_DIR / args.run
    if not run_dir.is_dir():
        print(f"FAILED: no run at {run_dir}")
        return 2

    print("Contract read from source:")
    print(f"  vector observations   {contract['vector_observations']}")
    print(f"  ray observations      {contract['ray_observations']}")
    print(f"  continuous actions    {contract['base_continuous']} "
          f"({contract['quarterback_continuous']} for {contract['quarterback_behavior']})")
    print(f"  discrete branches     {contract['quarterback_branches']} "
          f"on {contract['quarterback_behavior']} only")
    print()

    sources: dict[str, Path] = {}
    failures: dict[str, list[str]] = {}

    for behavior in contract["behaviors"]:
        try:
            source = latest_checkpoint(run_dir, behavior)
        except ContractError as error:
            failures[behavior] = [str(error)]
            print(f"  {behavior:<20} MISSING  {error}")
            continue

        problems = validate(behavior, source, contract)
        sources[behavior] = source

        if problems:
            failures[behavior] = problems
            print(f"  {behavior:<20} REFUSED  {source.name}")
            for problem in problems:
                print(f"    - {problem}")
        else:
            print(f"  {behavior:<20} ok       {source.name}")

    print()
    if failures:
        print(
            f"REFUSED: {len(failures)} of {len(contract['behaviors'])} brains do not match "
            "the contract. Nothing was copied."
        )
        print(
            "This is the check that Football_v01 never had. Retrain against the "
            "current contract rather than promoting these."
        )
        return 1

    target_dir = AGENTS_DIR / f"{args.name}_v{args.version}"

    if args.dry_run:
        print(f"All {len(sources)} brains match the contract. "
              f"--dry-run set, so {target_dir.name} was not written.")
        return 0

    target_dir.mkdir(parents=True, exist_ok=True)

    for behavior, source in sources.items():
        destination = target_dir / f"{behavior}.onnx"
        # Copied over the top so the .meta beside it keeps its GUID and every
        # scene reference stays valid (UNITY_RULES §4).
        shutil.copyfile(source, destination)
        print(f"  wrote {destination.relative_to(REPO_ROOT).as_posix()}")

    write_manifest(target_dir, args.run, args.num_envs, sources, contract)
    print(f"  wrote {(target_dir / 'MANIFEST.md').relative_to(REPO_ROOT).as_posix()}")

    print()
    print(
        f"Promoted {len(sources)} brains to {target_dir.relative_to(REPO_ROOT).as_posix()}. "
        "Assign them in the scene and re-run the EditMode contract tests."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
