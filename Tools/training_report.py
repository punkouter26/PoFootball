"""Plain-language training report for a PoFootball run.

Reads the run's TensorBoard event files and its stdout log and writes one
self-contained HTML page: an ability grid per brain, effort per position, and the
three charts worth reading, annotated.

    .venv\\Scripts\\python.exe Tools\\training_report.py --run football_base14 `
        --compare football_base13

Writes results/<run>/training_report.html. Re-run it to refresh; it only reads.
The charts are drawn from the same scalars TensorBoard plots, so TensorBoard does
not have to be running (and is not holding file handles when you next --force).

If results/<run>/tb_shots/shots.json exists, the three chart cards show those
TensorBoard screenshots instead, with the numbered notes the file lists drawn over
them. The pins are placed by hand in image pixels, so the file belongs to one
capture: retake the screenshots and it has to be redone.
"""

import base64
import argparse
import glob
import html
import json
import os
import re
import time

import yaml
from tensorboard.backend.event_processing.event_accumulator import EventAccumulator

MAX_STEPS = 7_000_000
LESSON_NAMES = ["Red zone only", "Whole field, downs 1-3", "Full game, incl. 4th down"]
# The first summary after a --resume covers only the plays between the restart and
# the next summary boundary. In football_base14 that was ONE play: -19 yards, 100%
# incompletions. It lands within this many steps of the last summary before the stop.
RESUME_STUB_STEPS = 25_000
# A tenth of the quarterback's 700k budget. Below it, a healthy-looking play mix is
# mostly the starting randomness, so its ratings are capped.
QB_NOVICE_STEPS = 70_000
LEVELS = ["Not yet", "Beginner", "Developing", "Good", "Football-like"]
OFFENSE_ROLES = ["OffensiveLine", "Fullback", "RunningBack", "TightEnd", "WideReceiver"]
DEFENSE_ROLES = ["DefensiveLine", "Linebacker", "Cornerback", "Safety"]
ROLE_NAMES = {
    "Quarterback": "Quarterback", "OffensiveLine": "Offensive line", "Fullback": "Fullback",
    "RunningBack": "Running back", "TightEnd": "Tight end", "WideReceiver": "Wide receiver",
    "DefensiveLine": "Defensive line", "Linebacker": "Linebacker",
    "Cornerback": "Cornerback", "Safety": "Safety",
}
# What two untrained action samples produce: mean |N(0,1) clipped to 3| / 3.
DRIVE_NOISE_FLOOR = 0.26


def load(run):
    """tag -> [(step, value)] for the run. Play/Call/Control stats are global and
    written under every behavior, so the Offense file is as good as any."""
    files = sorted(glob.glob(f"results/{run}/Offense/events.out.tfevents.*"))
    data = {}
    for path in files:
        acc = EventAccumulator(path, size_guidance={"scalars": 0})
        acc.Reload()
        for tag in acc.Tags()["scalars"]:
            points = [(e.step, e.value) for e in acc.Scalars(tag)]
            earlier = data.setdefault(tag, [])
            if earlier and points and points[0][0] - earlier[-1][0] < RESUME_STUB_STEPS:
                points = points[1:]
            earlier.extend(points)
    for tag in data:
        data[tag].sort()
    return data


def brain_steps(run):
    """behavior -> steps that brain has actually trained for. They differ: a resume
    restarts a brain that never reached a checkpoint from zero."""
    path = f"results/{run}/run_logs/training_status.json"
    if not os.path.exists(path):
        return {}
    status = json.load(open(path))
    return {name: entry["checkpoints"][-1]["steps"] for name, entry in status.items()
            if isinstance(entry, dict) and entry.get("checkpoints")}


def lesson_starts(data):
    """[(step, lesson index)] in the order the run entered them. A lesson that was
    jumped over (a resume with a smaller max_steps does that) is simply absent."""
    starts, previous_step = [], 0
    for step, value in data.get("Environment/Lesson Number/spot_lesson", []):
        index = int(round(value))
        if not starts or starts[-1][1] != index:
            starts.append((previous_step, index))
        previous_step = step
    return starts or [(0, 0)]


def recent(data, tag, count=3):
    points = data.get(tag, [])
    if not points:
        return None
    tail = [value for _, value in points[-count:]]
    return sum(tail) / len(tail)


def fmt_steps(steps):
    if steps >= 1_000_000:
        return f"{steps / 1_000_000:.2f}".rstrip("0").rstrip(".") + "M"
    return f"{steps / 1000:.0f}k"


def pct(value):
    return f"{value * 100:.0f}%"


def level_from(value, thresholds, higher_is_better=True):
    """thresholds are the four cut points between the five levels."""
    if value is None:
        return 0
    level = 0
    for cut in thresholds:
        if (value >= cut) if higher_is_better else (value <= cut):
            level += 1
    return level


def abilities(data, steps):
    qb_steps = steps.get("Quarterback")
    early = qb_steps is not None and qb_steps < QB_NOVICE_STEPS
    net = recent(data, "Play/NetYards")
    touchdown = recent(data, "Play/TouchdownRate")
    stall = recent(data, "Play/TimeExpiredRate")
    tackle = recent(data, "Play/TackleRate")
    completion = recent(data, "Pass/CompletionPerAttempt")
    interception = recent(data, "Pass/InterceptionPerAttempt")
    pass_share = recent(data, "Call/Pass")
    lesson = recent(data, "Environment/Lesson Number/spot_lesson", 1) or 0

    def drive(roles):
        values = [recent(data, f"Control/Drive/{role}") for role in roles]
        values = [v for v in values if v is not None]
        return sum(values) / len(values) if values else None

    def drive_cell(roles):
        value = drive(roles)
        return dict(
            name="Running with purpose",
            value="n/a" if value is None else f"{value:.2f} throttle",
            target="0.26 is random twitching; 0.6+ is really running",
            level=level_from(value, [0.30, 0.40, 0.50, 0.65]),
            note="Still at the random-twitch level." if value is not None and value < 0.30 else "")

    calls = {name: recent(data, f"Call/{name}") or 0 for name in
             ("Keep", "HandoffFullback", "HandoffHalfback", "Pass", "Punt", "FieldGoal")}
    top_call = max(calls.values()) if calls else 1

    if completion is None:
        completion_level = 0
    elif completion > 0.90:
        completion_level = 1
    elif completion > 0.72:
        completion_level = 3
    elif completion >= 0.55:
        completion_level = 4
    else:
        completion_level = 2

    if pass_share is None:
        balance_level = 0
    elif 0.50 <= pass_share <= 0.62:
        balance_level = 4
    elif 0.40 <= pass_share <= 0.70:
        balance_level = 3
    elif 0.30 <= pass_share <= 0.80:
        balance_level = 2
    else:
        balance_level = 1

    luck = ("Looks fine, but this early it is guessing, not judgment."
            if early and balance_level >= 3 else "")
    kicks = calls["Punt"] + calls["FieldGoal"]
    kick_level = level_from(kicks, [0.01, 0.03, 0.05, 0.08])
    defense_drive = drive(DEFENSE_ROLES)
    defense_idle = defense_drive is not None and defense_drive < 0.30

    quarterback = [
        dict(name="Variety of play calls",
             value=f"most-used play: {pct(top_call)}",
             target="no single play above ~60%",
             level=min(3, level_from(top_call, [0.85, 0.75, 0.65, 0.0], False)) if not early else 1,
             note="Varied only because it is still choosing at random." if early else ""),
        dict(name="Run / pass balance",
             value=f"{pct(pass_share or 0)} passes", target="real football: 55-60%",
             level=min(balance_level, 1) if early else balance_level, note=luck),
        dict(name="Completing passes",
             value=f"{pct(completion or 0)} caught", target="real football: ~65%",
             level=completion_level,
             note="Too easy: the defense is not contesting throws yet." if completion and completion > 0.9 else ""),
        dict(name="Kicking decisions",
             value="locked" if lesson < 2 else f"{pct(kicks)} of calls",
             target="punts and field goals on 4th down",
             level=0 if lesson < 2 else (min(kick_level, 1) if early else kick_level),
             note="Not taught yet. Fourth downs arrive in the third lesson." if lesson < 2 else
             "It does kick, but it has barely met fourth down, so this is trial and error." if early else ""),
        drive_cell(["Quarterback"]),
    ]

    offense = [
        dict(name="Gaining yards",
             value=f"{net:+.1f} yards a play" if net is not None else "n/a",
             target="real football: ~5.5",
             level=level_from(net, [0.0, 2.0, 4.0, 4.5]),
             note="Losing ground: the ball mostly goes backwards with the quarterback."
             if net is not None and net < 0 else
             "Flattered: the defense is not chasing yet, so these yards come cheap."
             if defense_idle else ""),
        dict(name="Scoring touchdowns",
             value=f"{pct(touchdown or 0)} of plays", target="a few percent of plays",
             level=min(3, level_from(touchdown, [0.002, 0.02, 0.04, 9])), note=""),
        dict(name="Finishing the play",
             value=f"{pct(stall or 0)} run out the clock", target="under 5%",
             level=level_from(stall, [0.60, 0.30, 0.15, 0.05], False),
             note="Most plays end with nobody having done anything." if stall and stall > 0.6 else ""),
        drive_cell(OFFENSE_ROLES),
    ]

    defense = [
        dict(name="Tackling",
             value=f"{pct(tackle or 0)} of plays end in a tackle", target="about 65%",
             level=min(2, level_from(tackle, [0.15, 0.35, 0.50, 0.62])) if defense_idle
             else level_from(tackle, [0.15, 0.35, 0.50, 0.62]),
             note="Tackles happen when the runner arrives. Defenders are not pursuing yet, so the "
                  "rating is capped." if defense_idle else ""),
        dict(name="Breaking up passes",
             value=f"{pct(1 - (completion or 1))} of throws stopped", target="about 35%",
             level=level_from(1 - (completion or 1), [0.10, 0.20, 0.28, 0.33]), note=""),
        dict(name="Interceptions",
             value=f"{(interception or 0) * 100:.1f}% of throws", target="2-3%",
             level=min(3, level_from(interception, [0.005, 0.012, 0.02, 9])), note=""),
        drive_cell(DEFENSE_ROLES),
    ]

    def practice(name):
        return f" {fmt_steps(steps[name])} steps of practice." if name in steps else ""

    return [
        ("Quarterback", "1 player, its own brain. Calls the play, throws, and runs."
         + practice("Quarterback"), quarterback),
        ("Offense", "10 players sharing one brain: linemen, backs, receivers."
         + practice("Offense"), offense),
        ("Defense", "11 players sharing one brain: line, linebackers, secondary."
         + practice("Defense"), defense),
    ]


# --- charts -----------------------------------------------------------------

W, H = 760, 330
LEFT, RIGHT, TOP, BOTTOM = 56, 170, 28, 44


def chart(chart_id, series, x_max, y_min, y_max, y_fmt, band=None, notes=(), lessons=()):
    """series: [(name, css_var, [(step, value)], dashed)]. Returns svg + json."""
    plot_w, plot_h = W - LEFT - RIGHT, H - TOP - BOTTOM

    def sx(step):
        return LEFT + plot_w * min(step, x_max) / x_max

    def sy(value):
        clamped = max(y_min, min(y_max, value))
        return TOP + plot_h * (1 - (clamped - y_min) / (y_max - y_min))

    parts = [f'<svg viewBox="0 0 {W} {H}" role="img" class="chart" id="{chart_id}">']

    if band:
        low, high, label = band
        parts.append(
            f'<rect x="{LEFT}" y="{sy(high):.1f}" width="{plot_w}" '
            f'height="{sy(low) - sy(high):.1f}" class="band"/>'
            f'<text x="{LEFT + plot_w - 6}" y="{sy(high) + 14:.1f}" text-anchor="end" '
            f'class="bandlabel">{html.escape(label)}</text>')

    for tick in range(5):
        value = y_min + (y_max - y_min) * tick / 4
        y = sy(value)
        parts.append(
            f'<line x1="{LEFT}" x2="{LEFT + plot_w}" y1="{y:.1f}" y2="{y:.1f}" class="grid"/>'
            f'<text x="{LEFT - 8}" y="{y + 4:.1f}" text-anchor="end" class="tick">{y_fmt(value)}</text>')

    for tick in range(5):
        step = x_max * tick / 4
        parts.append(
            f'<text x="{sx(step):.1f}" y="{H - BOTTOM + 18}" text-anchor="middle" '
            f'class="tick">{fmt_steps(step) if step else "0"}</text>')
    parts.append(
        f'<text x="{LEFT + plot_w / 2}" y="{H - 6}" text-anchor="middle" class="axis">'
        'practice so far (training steps)</text>')

    for step, label in lessons:
        if 0 < step < x_max:
            parts.append(
                f'<line x1="{sx(step):.1f}" x2="{sx(step):.1f}" y1="{TOP}" y2="{TOP + plot_h}" '
                f'class="lesson"/><text x="{sx(step) + 4:.1f}" y="{TOP + 11}" class="lessonlabel">'
                f'{html.escape(label)}</text>')

    payload = []
    label_ys = []
    for name, color, points, dashed in series:
        points = [(s, v) for s, v in points if s <= x_max]
        if not points:
            continue
        path = " ".join(
            f'{"M" if index == 0 else "L"}{sx(s):.1f},{sy(v):.1f}'
            for index, (s, v) in enumerate(points))
        dash = ' stroke-dasharray="5 4"' if dashed else ""
        parts.append(f'<path d="{path}" fill="none" stroke="var({color})" stroke-width="2"{dash} '
                     'stroke-linejoin="round" stroke-linecap="round"/>')
        end_step, end_value = points[-1]
        end_x, end_y = sx(end_step), sy(end_value)
        parts.append(f'<circle cx="{end_x:.1f}" cy="{end_y:.1f}" r="4.5" fill="var({color})" '
                     'stroke="var(--surface)" stroke-width="2"/>')
        label_y = end_y + 4
        while any(abs(label_y - other) < 15 for other in label_ys):
            label_y += 15
        label_ys.append(label_y)
        parts.append(f'<text x="{min(end_x + 10, LEFT + plot_w + 8):.1f}" y="{label_y:.1f}" '
                     f'class="serieslabel">{html.escape(name)} {y_fmt(end_value)}</text>')
        payload.append(dict(name=name, color=color, pts=[[s, round(v, 4)] for s, v in points]))

    for step, value, text, dx, dy in notes:
        x, y = sx(step), sy(value)
        lines = text.split("\n")
        tx, ty = x + dx, y + dy
        parts.append(f'<line x1="{x:.1f}" y1="{y:.1f}" x2="{tx:.1f}" y2="{ty:.1f}" class="leader"/>')
        box_w = max(len(line) for line in lines) * 6.4 + 16
        box_h = len(lines) * 15 + 10
        box_y = ty - (box_h if dy < 0 else 0)
        parts.append(f'<rect x="{tx:.1f}" y="{box_y:.1f}" width="{box_w:.0f}" height="{box_h}" '
                     'rx="6" class="notebox"/>')
        for index, line in enumerate(lines):
            parts.append(f'<text x="{tx + 8:.1f}" y="{box_y + 18 + index * 15:.1f}" class="note">'
                         f'{html.escape(line)}</text>')

    parts.append(f'<line class="cross" x1="0" x2="0" y1="{TOP}" y2="{TOP + plot_h}"/>')
    parts.append(f'<rect class="hit" x="{LEFT}" y="{TOP}" width="{plot_w}" height="{plot_h}"/>')
    parts.append("</svg>")

    meta = dict(id=chart_id, left=LEFT, plotW=plot_w, xMax=x_max, width=W, series=payload)
    return "".join(parts), meta


def screenshot(folder, shot):
    """A TensorBoard screenshot with numbered pins over it. The image is always
    light, so the overlay uses fixed inks, not the page's theme variables."""
    width, height = shot["width"], shot["height"]
    png = base64.b64encode(open(os.path.join(folder, shot["png"]), "rb").read()).decode()
    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" class="shot" '
             f'aria-label="TensorBoard screenshot of {html.escape(shot["tag"])}">'
             f'<image href="data:image/png;base64,{png}" width="{width}" height="{height}"/>']
    for x0, x1, y0, y1, label in shot.get("spans", []):
        parts.append(f'<rect x="{x0}" y="{y0}" width="{x1 - x0}" height="{y1 - y0}" class="span"/>'
                     f'<line x1="{x0}" x2="{x0}" y1="{y0}" y2="{y1}" class="spanedge"/>'
                     f'<text x="{x1}" y="{y1 + 110}" text-anchor="end" class="spanlabel">'
                     f'{html.escape(label)}</text>')
    items = []
    for number, pin in enumerate(shot["pins"], 1):
        x, y = pin["x"], pin["y"]
        bx, by = x + pin["dx"], y + pin["dy"]
        parts.append(f'<line x1="{x}" y1="{y}" x2="{bx}" y2="{by}" class="pinline"/>'
                     f'<circle cx="{x}" cy="{y}" r="15" class="pinring"/>'
                     f'<circle cx="{bx}" cy="{by}" r="25" class="pinbadge"/>'
                     f'<text x="{bx}" y="{by + 10}" text-anchor="middle" class="pinnum">{number}</text>')
        items.append(f"<li>{pin['text']}</li>")
    parts.append("</svg>")
    return ('<div class="shotframe">' + "".join(parts) + "</div>"
            f'<ol class="pins">{"".join(items)}</ol>')


def table(series, y_fmt):
    steps = sorted({s for _, _, points, _ in series for s, _ in points})
    rows = ["<tr><th>Steps</th>" + "".join(f"<th>{html.escape(n)}</th>" for n, _, _, _ in series) + "</tr>"]
    lookups = [dict(points) for _, _, points, _ in series]
    for step in steps:
        cells = "".join(f"<td>{y_fmt(lookup[step]) if step in lookup else ''}</td>" for lookup in lookups)
        rows.append(f"<tr><td>{fmt_steps(step)}</td>{cells}</tr>")
    return "<details><summary>Show the numbers</summary><table class='data'>" + "".join(rows) + "</table></details>"


def speed_from_log(run):
    path = f"results/{run}_stdout.log"
    if not os.path.exists(path):
        return None
    found = re.findall(r"Offense\. Step: (\d+)\. Time Elapsed: ([\d.]+) s", open(path, errors="ignore").read())
    if not found:
        return None
    step, seconds = int(found[-1][0]), float(found[-1][1])
    return step / seconds if seconds else None


def build(run, compare):
    global MAX_STEPS
    saved = f"results/{run}/configuration.yaml"
    if os.path.exists(saved):
        MAX_STEPS = yaml.safe_load(open(saved))["behaviors"]["Offense"]["max_steps"]

    data = load(run)
    steps = brain_steps(run)
    shots_dir = f"results/{run}/tb_shots"
    shots = {}
    if os.path.exists(f"{shots_dir}/shots.json"):
        shots = json.load(open(f"{shots_dir}/shots.json", encoding="utf-8"))
    previous = load(compare) if compare else {}
    net = data.get("Play/NetYards", [])
    if not net:
        raise SystemExit(f"no summaries yet under results/{run}/Offense")

    step = net[-1][0]
    progress = step / MAX_STEPS
    rate = speed_from_log(run)
    finished = step >= MAX_STEPS - 1000
    hours_left = None if finished or not rate else (MAX_STEPS - step) / rate / 3600
    x_max = max(1_000_000, int(step * 1.25))
    starts = lesson_starts(data)
    lessons = [(start, LESSON_NAMES[index]) for start, index in starts[1:]]
    last = lambda tag: recent(data, tag, 1)
    yards, stall, tackle, touchdown, passes = (
        last("Play/NetYards"), last("Play/TimeExpiredRate"), last("Play/TackleRate"),
        last("Play/TouchdownRate"), last("Call/Pass"))
    fatigue = last("Control/Fatigue") or 0
    prev_name = "Last run" if compare else ""

    as_pct = lambda value: f"{value * 100:.0f}%"
    as_yards = lambda value: f"{value:+.1f}" if value else "0"

    def versus(tag, fmt):
        points = [v for s, v in previous.get(tag, []) if s <= step]
        return fmt(points[-1]) if points else None

    charts = []

    # 1 — how plays end
    ends = [("Clock ran out", "--s1", data.get("Play/TimeExpiredRate", []), False),
            ("Tackled", "--s2", data.get("Play/TackleRate", []), False),
            ("Touchdown", "--s3", data.get("Play/TouchdownRate", []), False)]
    svg, meta = chart("ends", ends, x_max, 0, 1, as_pct, lessons=lessons, notes=[
        (step, stall, f"{as_pct(stall)} of plays end with the\nclock running out. This is the\nline that has to fall first.", 40, 30),
        (step, tackle, f"Tackles: {as_pct(tackle)}. Should climb\ntoward ~65% as play becomes real.", 40, -14)])
    was = versus("Play/TimeExpiredRate", as_pct)
    charts.append(dict(
        title="1. How do plays end?", meta=meta, svg=svg, table=table(ends, as_pct),
        what="Every play ends one of a few ways. This chart shows the share that end because "
             "<b>nobody did anything before time ran out</b> (blue), because the ball carrier was "
             "<b>tackled</b> (orange), or with a <b>touchdown</b> (green).",
        now=f"Right now {as_pct(stall)} of plays simply time out, {as_pct(tackle)} end in a tackle and "
            f"{as_pct(touchdown)} are touchdowns. "
            + ("The players are mostly milling around. That is normal at this stage: they start "
               "knowing nothing, not even that they should run." if stall > 0.6 else
               "Most plays now end the way a football play ends, but too many still fizzle out.")
            + (f" The previous run was at {was} timed-out plays at the same point." if was else ""),
        good="Good looks like: blue falls below 5%, orange rises to about 65%."))

    # 2 — yards per play
    yards_series = [("This run", "--s1", net, False)]
    if previous.get("Play/NetYards"):
        yards_series.append((prev_name, "--ref", previous["Play/NetYards"], True))
    svg, meta = chart("yards", yards_series, x_max, -6, 10, lambda v: f"{v:+.0f}" if v else "0",
                      band=(4.5, 6.5, "real football: 4.5 to 6.5 yards"), lessons=lessons, notes=[
        (step, yards, f"Now: {yards:+.1f} yards a play.\nBelow zero means the offense\nis going backwards.", 40, -34)])
    charts.append(dict(
        title="2. How far does the offense move the ball?", meta=meta, svg=svg,
        table=table(yards_series, lambda v: f"{v:+.2f}"),
        what="The average number of yards gained on a play. The shaded band is what real football "
             "looks like. The dashed grey line is the previous training run, for comparison.",
        now=f"The offense is averaging <b>{yards:+.1f} yards</b> a play — it loses ground, because the "
            "quarterback drops back with the ball and then nothing happens. The previous run started "
            "in the same hole and climbed out after roughly 400k steps."
            if yards < 0 else
            f"The offense gained <b>{yards:+.1f} yards</b> a play at the last reading. Single readings "
            "bounce around a lot, so look at the trend rather than the final dot.",
        good="Good looks like: the blue line climbs into the shaded band and stays there. "
             "Far above the band is also a failure — it means the defense has stopped mattering."))

    # 3 — pass share
    pass_series = [("This run", "--s1", data.get("Call/Pass", []), False)]
    if previous.get("Call/Pass"):
        pass_series.append((prev_name, "--ref", previous["Call/Pass"], True))
    svg, meta = chart("passes", pass_series, x_max, 0, 1, as_pct,
                      band=(0.55, 0.60, "real football: 55-60% passes"), lessons=lessons, notes=[
        (step, passes, f"Now: {as_pct(passes)} of calls are passes.\nThis early that is drift,\nnot a decision.", 40, 40)])
    charts.append(dict(
        title="3. What does the quarterback call?", meta=meta, svg=svg, table=table(pass_series, as_pct),
        what="The share of plays where the quarterback chooses to <b>pass</b> rather than run. "
             "Real teams pass a little more than half the time.",
        now=f"The quarterback is calling a pass on <b>{as_pct(passes)}</b> of plays."
            + (" The previous run drifted up to about 73% and stayed there, because catching a pass "
               "was over-rewarded. This run pays less for a catch and more for first downs, which was "
               "meant to pull the line lower." if compare else "")
            + (f" This quarterback brain has only {fmt_steps(steps['Quarterback'])} steps of practice, "
               "so its habits are not settled."
               if steps.get("Quarterback", QB_NOVICE_STEPS) < QB_NOVICE_STEPS else ""),
        good="Good looks like: the line settles inside the shaded band instead of running to 70%+."))

    for card in charts:
        shot = shots.get(card["meta"]["id"])
        if shot:
            card["svg"], card["meta"] = screenshot(shots_dir, shot), None
            # The stock wording describes the drawn chart's colours and bands.
            card["what"], card["good"] = shot.get("what", card["what"]), shot.get("good", card["good"])

    groups = abilities(data, steps)
    drive_rows = []
    for role in ["Quarterback"] + OFFENSE_ROLES + DEFENSE_ROLES:
        value = recent(data, f"Control/Drive/{role}")
        if value is None:
            continue
        side = "Defense" if role in DEFENSE_ROLES else ("Quarterback" if role == "Quarterback" else "Offense")
        drive_rows.append((ROLE_NAMES[role], side, value))

    # Headline numbers are three-summary means; one summary is about a hundred plays.
    return render(run, step, progress, rate, hours_left, groups, drive_rows, charts,
                  dict(yards=recent(data, "Play/NetYards"), stall=recent(data, "Play/TimeExpiredRate"),
                       fatigue=fatigue), compare, starts, steps, bool(shots))


ICONS = ["○", "◔", "◑", "◕", "●"]


def render(run, step, progress, rate, hours_left, groups, drive_rows, charts, tiles, compare,
           starts, steps, has_shots):
    lesson_index = starts[-1][1]
    entered = {index: start for start, index in starts}
    grid = []
    for name, blurb, cells in groups:
        cell_html = "".join(
            f'<div class="cell l{c["level"]}"><div class="ability">{html.escape(c["name"])}</div>'
            f'<div class="level"><span class="icon">{ICONS[c["level"]]}</span> {LEVELS[c["level"]]}</div>'
            f'<div class="meter"><i style="width:{c["level"] * 25}%"></i></div>'
            f'<div class="value">{html.escape(c["value"])}</div>'
            f'<div class="target">Goal: {html.escape(c["target"])}</div>'
            + (f'<div class="cellnote">{html.escape(c["note"])}</div>' if c["note"] else "")
            + "</div>" for c in cells)
        grid.append(f'<section class="agent"><header><h3>{name}</h3><p>{blurb}</p></header>'
                    f'<div class="cells">{cell_html}</div></section>')

    bars = "".join(
        f'<div class="bar" title="{name}: {value:.3f}"><span class="barname">{name}'
        f'<em>{side}</em></span><span class="track"><i class="floor" style="left:{DRIVE_NOISE_FLOOR * 100}%"></i>'
        f'<i class="fill" style="width:{value * 100:.1f}%"></i></span>'
        f'<span class="barvalue">{value:.2f}</span></div>'
        for name, side, value in drive_rows)

    chart_html = "".join(
        f'<article class="card"><h3>{c["title"]}</h3><p class="what">{c["what"]}</p>'
        + (f'<div class="chartwrap">{c["svg"]}<div class="tip" hidden></div></div>'
           if c["meta"] else c["svg"]) +
        f'<p class="now"><b>Where it stands:</b> {c["now"]}</p><p class="good">{c["good"]}</p>{c["table"]}</article>'
        for c in charts)

    def lesson_when(index):
        if index in entered:
            return "from the start" if entered[index] == 0 else "from " + fmt_steps(entered[index])
        return "skipped" if index < lesson_index else "not reached yet"

    lesson_html = "".join(
        f'<li class="{"current" if index == lesson_index else "done" if index in entered else ""}">'
        f'<b>Lesson {index + 1}</b> {name}<span>{lesson_when(index)}</span></li>'
        for index, name in enumerate(LESSON_NAMES))

    done = "Training has finished" if hours_left is None else f"Training is {progress * 100:.0f}% done"
    ages = ""
    if steps.get("Quarterback") and steps.get("Offense") and steps["Quarterback"] * 10 < steps["Offense"]:
        ages = (f" The three brains are not the same age: the quarterback has {fmt_steps(steps['Quarterback'])} "
                f"steps of practice against {fmt_steps(steps['Offense'])} for the others, so treat this as a "
                "trial run, not a trained team.")
    idle = [name for name, side, value in drive_rows if side == "Defense" and value < 0.30]
    if len(idle) == len(DEFENSE_ROLES):
        ages += (" The defense has not learned to chase the ball yet, which makes the offense look "
                 "better than it is.")
    if tiles["stall"] > 0.6:
        lede = (f"{done} and the players are still beginners: most plays end with the clock running out "
                f"and the offense averages {tiles['yards']:+.1f} yards. That is the expected starting point, "
                "not a problem. Every player begins knowing nothing and learns by trial and error over "
                "millions of practice plays.")
    else:
        lede = (f"{done}. The offense averages {tiles['yards']:+.1f} yards a play (real football is about 5.5) "
                f"and {tiles['stall'] * 100:.0f}% of plays still end with the clock running out instead of a "
                "tackle or a score." + ages)
    eta = f"about {hours_left:.1f} hours left" if hours_left else "training has stopped"
    speed = f"{rate:.0f} steps a second" if rate else ""
    meta_json = json.dumps([c["meta"] for c in charts if c["meta"]])
    charts_sub = ("These are screenshots of TensorBoard itself, with numbered notes added. Each reads left to "
                  "right: more practice as you move right. The shaded part is after training was restarted."
                  if has_shots else
                  "Each chart reads left to right: more practice as you move right. Hover a chart for exact "
                  "numbers. Dotted vertical lines mark where the next lesson begins.")
    source = ("The three charts are TensorBoard screenshots (Scalars tab, smoothing 0, outliers ignored, which "
              "is why a line can run off the top). Everything else is computed from the same event files."
              if has_shots else
              f"Drawn from the same data TensorBoard plots (results/{run}/Offense event files), rather than "
              "screenshots of TensorBoard itself, so the notes sit on the charts and nothing has to be running "
              "to read this.")

    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>PoFootball training report — {run}</title>
<style>
:root {{ color-scheme: light; --page:#f4f3ef; --surface:#fcfcfb; --line:#e3e1da; --text:#0b0b0b; --text2:#52514e;
  --muted:#8a8880; --s1:#2a78d6; --s2:#eb6834; --s3:#1baf7a; --ref:#8a8880; --band:#ece9df;
  --l0:#b9b6ad; --l1:#86b6ef; --l2:#5598e7; --l3:#256abf; --l4:#104281; --warn:#b06a00; }}
@media (prefers-color-scheme: dark) {{ :root:where(:not([data-theme="light"])) {{ color-scheme: dark; --page:#121211;
  --surface:#1a1a19; --line:#33332f; --text:#fff; --text2:#c3c2b7; --muted:#8f8e85; --s1:#3987e5; --s2:#d95926;
  --s3:#199e70; --ref:#8f8e85; --band:#262623; --l0:#55544e; --l1:#184f95; --l2:#256abf; --l3:#3987e5; --l4:#86b6ef; --warn:#e0a03a; }} }}
:root[data-theme="dark"] {{ color-scheme: dark; --page:#121211; --surface:#1a1a19; --line:#33332f; --text:#fff;
  --text2:#c3c2b7; --muted:#8f8e85; --s1:#3987e5; --s2:#d95926; --s3:#199e70; --ref:#8f8e85; --band:#262623;
  --l0:#55544e; --l1:#184f95; --l2:#256abf; --l3:#3987e5; --l4:#86b6ef; --warn:#e0a03a; }}
* {{ box-sizing: border-box; }}
body {{ margin:0; background:var(--page); color:var(--text); font:15px/1.5 system-ui, "Segoe UI", sans-serif; }}
main {{ max-width: 1080px; margin: 0 auto; padding: 28px 16px 64px; }}
h1 {{ font-size: 26px; margin: 0 0 4px; letter-spacing: -0.01em; }}
h2 {{ font-size: 19px; margin: 40px 0 6px; }}
h3 {{ font-size: 16px; margin: 0; }}
p {{ margin: 6px 0; }} .sub {{ color: var(--text2); margin: 0 0 18px; }}
.lede {{ background: var(--surface); border: 1px solid var(--line); border-left: 4px solid var(--warn);
  border-radius: 10px; padding: 14px 16px; }}
.tiles {{ display:grid; grid-template-columns: repeat(auto-fit, minmax(190px, 1fr)); gap:12px; margin: 16px 0; }}
.tile {{ background: var(--surface); border:1px solid var(--line); border-radius:10px; padding:14px 16px; }}
.tile b {{ display:block; font-size: 26px; font-variant-numeric: tabular-nums; }}
.tile span {{ color: var(--text2); font-size: 13px; }}
.progress {{ height: 10px; background: var(--band); border-radius: 6px; overflow: hidden; margin-top: 8px; }}
.progress i {{ display:block; height:100%; background: var(--s1); border-radius: 6px; min-width: 6px; }}
ol.lessons {{ list-style:none; padding:0; margin: 12px 0 0; display:grid; grid-template-columns: repeat(auto-fit, minmax(220px,1fr)); gap:10px; }}
ol.lessons li {{ background: var(--surface); border:1px solid var(--line); border-radius:10px; padding:10px 14px; color: var(--text2); }}
ol.lessons li.current {{ border-color: var(--s1); color: var(--text); box-shadow: inset 0 0 0 1px var(--s1); }}
ol.lessons li b {{ display:block; color: var(--text); }} ol.lessons li span {{ display:block; font-size:12px; color: var(--muted); }}
.agent {{ background: var(--surface); border:1px solid var(--line); border-radius: 12px; padding: 16px; margin: 12px 0; }}
.agent header p {{ color: var(--text2); margin: 2px 0 12px; font-size: 13px; }}
.cells {{ display:grid; grid-template-columns: repeat(auto-fit, minmax(185px, 1fr)); gap: 10px; }}
.cell {{ border:1px solid var(--line); border-radius:10px; padding:12px; }}
.ability {{ font-weight: 600; }} .level {{ font-size: 13px; color: var(--text2); margin-top: 2px; }}
.icon {{ font-size: 15px; }}
.meter {{ height: 8px; background: var(--band); border-radius: 5px; margin: 8px 0; overflow: hidden; }}
.meter i {{ display:block; height:100%; border-radius:5px; min-width: 4px; }}
.l0 .meter i {{ background: var(--l0); }} .l1 .meter i {{ background: var(--l1); }} .l2 .meter i {{ background: var(--l2); }}
.l3 .meter i {{ background: var(--l3); }} .l4 .meter i {{ background: var(--l4); }}
.value {{ font-variant-numeric: tabular-nums; }} .target, .cellnote {{ font-size: 12px; color: var(--muted); }}
.cellnote {{ color: var(--text2); margin-top: 4px; font-style: italic; }}
.scale {{ display:flex; flex-wrap:wrap; gap: 14px; font-size: 13px; color: var(--text2); margin: 8px 0; }}
.bars {{ background: var(--surface); border:1px solid var(--line); border-radius: 12px; padding: 16px; }}
.bar {{ display:grid; grid-template-columns: 170px 1fr 44px; align-items:center; gap: 10px; padding: 4px 0; }}
.barname em {{ font-style: normal; color: var(--muted); font-size: 12px; margin-left: 6px; }}
.track {{ position:relative; height: 14px; background: var(--band); border-radius: 4px; }}
.track .fill {{ position:absolute; left:0; top:0; bottom:0; background: var(--s1); border-radius: 0 4px 4px 0; }}
.track .floor {{ position:absolute; top:-4px; bottom:-4px; width:2px; background: var(--text2); z-index: 1; }}
.barvalue {{ font-variant-numeric: tabular-nums; text-align: right; color: var(--text2); }}
.card {{ background: var(--surface); border:1px solid var(--line); border-radius: 12px; padding: 18px; margin: 14px 0; }}
.card .what {{ color: var(--text2); }} .card .good {{ color: var(--text2); font-size: 14px; }}
.chartwrap {{ position: relative; margin: 10px 0; overflow-x: auto; }}
svg.chart {{ width: 100%; min-width: 620px; height: auto; display:block; }}
.grid {{ stroke: var(--line); stroke-width: 1; }} .tick, .axis {{ fill: var(--muted); font-size: 11.5px; }}
.band {{ fill: var(--band); }} .bandlabel {{ fill: var(--text2); font-size: 11.5px; paint-order: stroke; stroke: var(--surface); stroke-width: 4px; }}
.lesson {{ stroke: var(--muted); stroke-dasharray: 2 4; }} .lessonlabel {{ fill: var(--muted); font-size: 11px; }}
.serieslabel {{ fill: var(--text); font-size: 12.5px; font-weight: 600; }}
.leader {{ stroke: var(--text2); stroke-width: 1; }}
.notebox {{ fill: var(--surface); stroke: var(--text2); stroke-width: 1; }} .note {{ fill: var(--text); font-size: 12px; }}
.cross {{ stroke: var(--text2); stroke-width: 1; visibility: hidden; }} .hit {{ fill: transparent; }}
.tip {{ position:absolute; top: 6px; pointer-events:none; background: var(--surface); border:1px solid var(--line);
  border-radius: 8px; padding: 8px 10px; font-size: 12.5px; box-shadow: 0 4px 14px rgba(0,0,0,.12); white-space: nowrap; }}
.tip i {{ display:inline-block; width:10px; height:3px; border-radius:2px; margin-right:6px; vertical-align: middle; }}
.shotframe {{ background:#fff; border:1px solid var(--line); border-radius: 8px; padding: 6px; margin: 10px 0; }}
svg.shot {{ width:100%; height:auto; display:block; }}
.span {{ fill:#104281; fill-opacity:.08; }} .spanedge {{ stroke:#104281; stroke-width:3; stroke-dasharray: 10 8; }}
.spanlabel {{ fill:#104281; font: 600 27px system-ui, "Segoe UI", sans-serif; }}
.pinline {{ stroke:#0b0b0b; stroke-width:3; }} .pinring {{ fill:none; stroke:#0b0b0b; stroke-width:4; }}
.pinbadge {{ fill:#0b0b0b; stroke:#fff; stroke-width:4; }}
.pinnum {{ fill:#fff; font: 700 28px system-ui, "Segoe UI", sans-serif; }}
ol.pins {{ margin: 10px 0 12px; padding-left: 0; list-style: none; counter-reset: pin; display: grid; gap: 8px; }}
ol.pins li {{ counter-increment: pin; position: relative; padding-left: 34px; }}
ol.pins li::before {{ content: counter(pin); position:absolute; left:0; top:1px; width:22px; height:22px; border-radius:50%;
  background: var(--text); color: var(--surface); font-weight:700; font-size:12.5px; display:grid; place-items:center; }}
details {{ margin-top: 8px; color: var(--text2); font-size: 13px; }} summary {{ cursor: pointer; }}
table.data {{ border-collapse: collapse; margin-top: 8px; font-variant-numeric: tabular-nums; }}
table.data th, table.data td {{ border-bottom: 1px solid var(--line); padding: 3px 12px 3px 0; text-align: left; }}
footer {{ color: var(--muted); font-size: 12.5px; margin-top: 36px; }}
</style></head><body><main>
<h1>How the football players are learning</h1>
<p class="sub">Run <b>{run}</b> · snapshot taken {time.strftime("%Y-%m-%d %H:%M")} · {fmt_steps(step)} of {fmt_steps(MAX_STEPS)} practice steps</p>

<div class="lede"><b>In one sentence:</b> {lede}</div>

<div class="tiles">
  <div class="tile"><b>{progress * 100:.0f}%</b><span>of training complete</span><div class="progress"><i style="width:{progress * 100:.1f}%"></i></div></div>
  <div class="tile"><b>{f"{hours_left:.1f} h" if hours_left else "Done"}</b><span>{eta}{" · " + speed if speed and hours_left else ""}</span></div>
  <div class="tile"><b>{tiles["yards"]:+.1f}</b><span>yards gained per play, last three readings (real football: about 5.5)</span></div>
  <div class="tile"><b>{tiles["stall"] * 100:.0f}%</b><span>of plays where the clock just runs out, last three readings (goal: under 5%)</span></div>
  <div class="tile"><b>{tiles["fatigue"] * 100:.0f}%</b><span>tiredness at the end of a play. New in this run: players now get tired and carry it to the next play.</span></div>
</div>

<h2>The three lessons</h2>
<p class="sub">Practice gets harder in stages, like a coach starting near the goal line before using the whole field.</p>
<ol class="lessons">{lesson_html}</ol>

<h2>Who can do what</h2>
<p class="sub">There are three "brains". Each one controls a group of players and is learning its own set of skills.
Ratings come straight from the training numbers and update each time this report is rebuilt.</p>
<div class="scale">{"".join(f"<span>{ICONS[i]} {LEVELS[i]}</span>" for i in range(5))}</div>
{"".join(grid)}

<h2>Is each position actually trying to run?</h2>
<p class="sub">How hard each position pushes the throttle, from 0 (standing still) to 1 (flat out). The vertical tick is
what pure random twitching looks like. A bar sitting on the tick means that position has not learned to run on purpose yet.</p>
<div class="bars">{bars}</div>

<h2>The three charts that matter</h2>
<p class="sub">{charts_sub}</p>
{chart_html}

<footer>{source}
{"Grey dashed lines are " + compare + ", the previous run, stopped at about 2.5M steps. " if compare and not has_shots else ""}
Ratings are three-reading averages. The quarterback's are capped while its brain is under {fmt_steps(QB_NOVICE_STEPS)} steps old,
where a good-looking number is mostly chance.
Rebuild with <code>Tools\\training_report.py --run {run}</code>.</footer>
</main>
<script>
const charts = {meta_json};
for (const c of charts) {{
  const svg = document.getElementById(c.id), wrap = svg.parentElement, tip = wrap.querySelector('.tip');
  const cross = svg.querySelector('.cross'), hit = svg.querySelector('.hit');
  const fmt = v => c.id === 'yards' ? (v >= 0 ? '+' : '') + v.toFixed(1) + ' yd' : Math.round(v * 100) + '%';
  hit.addEventListener('mousemove', e => {{
    const box = svg.getBoundingClientRect(), scale = c.width / box.width;
    const x = (e.clientX - box.left) * scale;
    const step = (x - c.left) / c.plotW * c.xMax;
    let rows = '', snap = null;
    for (const s of c.series) {{
      let best = s.pts[0];
      for (const p of s.pts) if (Math.abs(p[0] - step) < Math.abs(best[0] - step)) best = p;
      if (snap === null) snap = best[0];
      rows += `<div><i style="background:var(${{s.color}})"></i>${{s.name}}: <b>${{fmt(best[1])}}</b></div>`;
    }}
    const cx = c.left + c.plotW * snap / c.xMax;
    cross.setAttribute('x1', cx); cross.setAttribute('x2', cx); cross.style.visibility = 'visible';
    tip.innerHTML = `<div style="color:var(--muted)">${{(snap / 1000).toFixed(0)}}k steps</div>` + rows;
    tip.hidden = false;
    const px = cx / scale;
    tip.style.left = Math.min(px + 12, box.width - tip.offsetWidth - 4) + 'px';
  }});
  hit.addEventListener('mouseleave', () => {{ tip.hidden = true; cross.style.visibility = 'hidden'; }});
}}
</script></body></html>"""


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--run", required=True)
    parser.add_argument("--compare", default=None, help="an earlier run to draw as a grey reference line")
    args = parser.parse_args()

    out = f"results/{args.run}/training_report.html"
    with open(out, "w", encoding="utf-8") as handle:
        handle.write(build(args.run, args.compare))
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
