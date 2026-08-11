"""Watch a run's play-call entropy and shout before it collapses.

WHY THIS EXISTS
---------------
The trainer's own `Policy/Entropy` sums across every action head at once. In
football_base03 it sat at a healthy 3.5 for the whole run while the quarterback's
play call had collapsed onto a single option on 93% of downs — the four
continuous steering heads carried enough entropy to hide it completely.

`Call/Entropy` is the per-branch number for the play call alone, emitted through
StatsRecorder by Agent_Telemetry. It is the only figure that answers "is the
quarterback still choosing?", and it is worth knowing hours before the run ends
rather than afterwards (CLAUDE.md section 4).

WHAT IT DOES
------------
Polls the run's TensorBoard event files and exits nonzero the moment the call has
collapsed, so it can gate a shell script. Collapse is defined as N consecutive
summaries under the floor, not a single dip: entropy is noisy between summaries
and one low reading is not a trend.

Exits zero when the target step is reached without a collapse.

Usage
-----
    .venv/Scripts/python.exe Tools/watch_entropy.py --run football_base06
    .venv/Scripts/python.exe Tools/watch_entropy.py --run football_base06 \
        --behavior Quarterback --target 3200000 --floor 0.80

Reads event files only; it never writes to results/ and is safe to run against a
live run. Unlike a wipe or a prune it does not hold Windows handles in a way that
matters, but TensorBoard reading the same directory is still the friendlier
setup.
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
RESULTS_DIR = REPO_ROOT / "results"

ENTROPY_TAG = "Call/Entropy"


def read_entropy(behavior_dir: Path) -> list:
    """The Call/Entropy series, or an empty list if it cannot be read yet."""
    try:
        from tensorboard.backend.event_processing.event_accumulator import EventAccumulator
    except ImportError:
        print("tensorboard is not installed in this environment.", file=sys.stderr)
        raise SystemExit(2)

    if not behavior_dir.is_dir():
        return []

    try:
        accumulator = EventAccumulator(str(behavior_dir), size_guidance={"scalars": 0})
        accumulator.Reload()

        if ENTROPY_TAG not in accumulator.Tags().get("scalars", []):
            return []

        return accumulator.Scalars(ENTROPY_TAG)
    except Exception:
        # A half-written event file mid-flush is normal against a live run.
        # Returning empty means "no reading this poll", not "collapsed".
        return []


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", required=True, help="run id under results/")
    parser.add_argument(
        "--behavior",
        default="Quarterback",
        help="behavior whose Call/Entropy to watch (default: Quarterback, the "
             "only brain with a discrete play call since base04)")
    parser.add_argument(
        "--target", type=int, default=3_200_000,
        help="stop watching once this step is reached (default: 3.2M, past "
             "base05's 3.1M collapse point)")
    parser.add_argument("--floor", type=float, default=0.80, help="collapse threshold")
    parser.add_argument(
        "--consecutive", type=int, default=3,
        help="summaries under the floor before calling it a collapse")
    parser.add_argument("--interval", type=int, default=300, help="seconds between polls")
    args = parser.parse_args()

    behavior_dir = RESULTS_DIR / args.run / args.behavior
    if not (RESULTS_DIR / args.run).is_dir():
        print(f"No run at results/{args.run}", file=sys.stderr)
        return 2

    print(
        f"Watching {ENTROPY_TAG} for {args.run}/{args.behavior} — "
        f"floor {args.floor}, {args.consecutive} consecutive, target {args.target:,}")

    while True:
        events = read_entropy(behavior_dir)

        if events:
            latest = events[-1]
            tail = [point.value for point in events[-args.consecutive:]]

            if len(tail) == args.consecutive and all(value < args.floor for value in tail):
                print(
                    f"COLLAPSE: {ENTROPY_TAG} {[round(v, 3) for v in tail]} "
                    f"under {args.floor} at step {latest.step:,}")
                return 1

            if latest.step >= args.target:
                print(
                    f"PASSED target: step {latest.step:,}, "
                    f"{ENTROPY_TAG} {latest.value:.3f}")
                return 0

        time.sleep(args.interval)


if __name__ == "__main__":
    sys.exit(main())
