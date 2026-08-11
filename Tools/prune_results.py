"""Prune results/ down to the checkpoints that are still worth keeping.

WHY THIS EXISTS
---------------
results/ reached 926 MB across four runs — 104 .onnx and 112 .pt files, of which
about eight are ever loaded again. `keep_checkpoints: 40` at a 500k interval
retains every checkpoint of a 50M-step run, and nothing removes them afterwards.

WHAT IS KEPT
------------
Per behavior, per run:

  * the final export, `<behavior>.onnx` at the run root or in the behavior folder
  * the highest-numbered checkpoint, which is what a promotion falls back to
  * the checkpoint at peak `Self-play/ELO`, read from the run's TensorBoard event
    files — self-play is judged on ELO, not mean reward (UNITY_RULES section 4),
    and ELO can and does fall over a run: football_base03 went 1165 -> 737, so
    its last checkpoint is emphatically not its best one
  * everything outside the checkpoint pattern: configuration.yaml, run_logs/,
    and the event files themselves, which are small and are the only record of
    what happened

Event files are never deleted. They are the run's evidence and they are tiny
next to the weights.

Usage
-----
    .venv/Scripts/python.exe Tools/prune_results.py                # report only
    .venv/Scripts/python.exe Tools/prune_results.py --apply        # delete
    .venv/Scripts/python.exe Tools/prune_results.py --apply --run football_base01

Kill TensorBoard first. It holds Windows file handles on the event directories
and deletions silently fail against them (UNITY_RULES section 4) — the same
reason `--force` has to be run with TensorBoard down.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
RESULTS_DIR = REPO_ROOT / "results"

CHECKPOINT_PATTERN = re.compile(r"^(?P<behavior>.+)-(?P<steps>\d+)\.(?P<extension>onnx|pt)$")

ELO_TAG = "Self-play/ELO"


def human_size(byte_count: int) -> str:
    size = float(byte_count)
    for unit in ("B", "KB", "MB", "GB"):
        if size < 1024 or unit == "GB":
            return f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} GB"


def peak_elo_step(behavior_dir: Path) -> int | None:
    """
    The step at which this behavior's ELO peaked, or None if it cannot be read.

    Returning None is deliberately conservative: an unreadable event file means
    the peak-ELO checkpoint is not identified, and the caller then keeps more
    than it strictly needs rather than deleting something irreplaceable.
    """
    try:
        from tensorboard.backend.event_processing.event_accumulator import EventAccumulator
    except ImportError:
        return None

    event_files = list(behavior_dir.glob("events.out.tfevents*"))
    if not event_files:
        return None

    try:
        accumulator = EventAccumulator(str(behavior_dir))
        accumulator.Reload()

        if ELO_TAG not in accumulator.Tags().get("scalars", []):
            return None

        events = accumulator.Scalars(ELO_TAG)
    except Exception:
        return None

    if not events:
        return None

    return max(events, key=lambda event: event.value).step


def nearest_checkpoint(steps: list[int], target: int) -> int | None:
    """Checkpoints land near, not on, a summary step, so match the closest."""
    if not steps:
        return None
    return min(steps, key=lambda step: abs(step - target))


def plan_for_behavior(behavior_dir: Path) -> tuple[list[Path], list[Path], str]:
    """Returns (keep, remove, reason) for one behavior directory."""
    checkpoints: dict[int, list[Path]] = {}
    keep: list[Path] = []

    for path in sorted(behavior_dir.iterdir()):
        if not path.is_file():
            continue

        match = CHECKPOINT_PATTERN.match(path.name)
        if match and match.group("behavior") == behavior_dir.name:
            checkpoints.setdefault(int(match.group("steps")), []).append(path)
        else:
            # Final exports, .pt state, event files, anything unrecognised.
            keep.append(path)

    if not checkpoints:
        return keep, [], "no numbered checkpoints"

    steps = sorted(checkpoints)
    keep_steps = {steps[-1]}
    reason = f"latest {steps[-1]}"

    peak = peak_elo_step(behavior_dir)
    matched_peak = nearest_checkpoint(steps, peak) if peak is not None else None
    if matched_peak is not None:
        keep_steps.add(matched_peak)
        reason += f", peak ELO near {peak} -> {matched_peak}"
    else:
        # Without an ELO curve there is no basis for calling any middle
        # checkpoint better than another, so keep the earliest as well: it is the
        # only other one with independent meaning.
        keep_steps.add(steps[0])
        reason += f", earliest {steps[0]} (no ELO curve readable)"

    remove: list[Path] = []
    for step in steps:
        if step in keep_steps:
            keep.extend(checkpoints[step])
        else:
            remove.extend(checkpoints[step])

    return keep, remove, reason


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apply", action="store_true", help="actually delete; default is a report")
    parser.add_argument("--run", help="limit to one run id under results/")
    args = parser.parse_args()

    if not RESULTS_DIR.is_dir():
        print(f"No results directory at {RESULTS_DIR}")
        return 0

    runs = [RESULTS_DIR / args.run] if args.run else sorted(
        d for d in RESULTS_DIR.iterdir() if d.is_dir()
    )

    total_removed = 0
    total_bytes = 0

    for run_dir in runs:
        if not run_dir.is_dir():
            print(f"no run at {run_dir}")
            return 2

        print(f"{run_dir.name}")
        run_bytes = 0
        run_count = 0

        for behavior_dir in sorted(d for d in run_dir.iterdir() if d.is_dir()):
            if behavior_dir.name == "run_logs":
                continue

            _, remove, reason = plan_for_behavior(behavior_dir)
            if not remove:
                print(f"  {behavior_dir.name:<20} nothing to remove ({reason})")
                continue

            removed_bytes = sum(path.stat().st_size for path in remove)
            run_bytes += removed_bytes
            run_count += len(remove)

            print(
                f"  {behavior_dir.name:<20} remove {len(remove):>3} files, "
                f"{human_size(removed_bytes):>9}  (keeping {reason})"
            )

            if args.apply:
                for path in remove:
                    path.unlink()

        if run_count:
            print(f"  {'':<20} {'-' * 46}")
            print(f"  {'total':<20} {run_count} files, {human_size(run_bytes)}")

        total_removed += run_count
        total_bytes += run_bytes
        print()

    verb = "Removed" if args.apply else "Would remove"
    print(f"{verb} {total_removed} files, {human_size(total_bytes)}.")

    if not args.apply and total_removed:
        print("Re-run with --apply to delete. Kill TensorBoard first: it holds "
              "Windows handles on these directories and the deletes silently fail.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
