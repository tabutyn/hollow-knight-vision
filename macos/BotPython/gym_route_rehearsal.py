"""Distill completed, real Gym demonstrations into the policy's ordinary observation space."""
import hashlib
from itertools import product
import json

import numpy as np

from hacker_gym import START, buttons, save_json
from hkv_bot_dataset import ACTION_SIZES

KING_PASS_DESTINATION = "Town"


def route_library(shared, identity):
    keys = ["domain", "checkpoint", "receiver"]
    keys += ["labels"] if identity["domain"] == "hacker" else ["visualDetectorAndHUD", "visionSource"]
    content = json.dumps({key: identity[key] for key in keys}, sort_keys=True).encode()
    return shared / "route-rehearsal" / hashlib.sha256(content).hexdigest()[:20]


def playable_arrivals(fields, samples):
    """Record a real scene transition once the Knight can be controlled there."""
    scene, state, available, health, step = (
        fields.index(name) for name in ("scene", "gameState", "heroAvailable", "health", "step")
    )
    previous = None
    arrivals = []
    for row in samples:
        if row[scene] and row[state] == "PLAYING" and row[available] and row[health] > 0 and row[scene] != previous:
            if previous is not None:
                arrivals.append(dict(departure=previous, scene=row[scene], step=row[step]))
            previous = row[scene]
    return arrivals


def reached_king_pass_destination(report):
    return any(item["departure"] == START and item["scene"] == KING_PASS_DESTINATION
               for item in report.get("playableArrivals", ()))


def completed_in_play(report):
    end = report.get("end", {})
    return (reached_king_pass_destination(report)
            or (report["completed"] and report["reason"] == "exit" and end.get("scene") == KING_PASS_DESTINATION
                and end.get("gameState") == "PLAYING" and end.get("heroAvailable")))


def store_route(work, report, rows):
    seed = report["seed"]
    if not rows or not 100_000 <= seed < 1_000_000 or not completed_in_play(report) or report.get("authoredRouteLandings", 0) < 2:
        return
    directory = work / "completed-routes"
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / f"{seed}.npz"
    if path.exists():
        return
    # step, x/y, and damage are truth used only to choose which demonstrations to rehearse.
    # The network sees only the already-encoded domain observations.
    states = np.asarray([r[0] for r in rows])
    hits = states[states[:, 3] > 0, 0]
    keep = np.ones(len(rows), dtype=bool)
    for step in hits:
        keep &= (states[:, 0] < step - 10) | (states[:, 0] > step + 8)
    if not keep.any():
        return
    temporary = path.with_suffix(".tmp")
    with temporary.open("wb") as output:
        np.savez_compressed(output, states=states[keep, :3], observations=np.stack([r[1] for r in rows])[keep],
                            actions=np.array([r[2] for r in rows], dtype=np.int8)[keep])
    temporary.replace(path)


def store_route_starts(work, report, fields, samples, max_steps, *, baseline=None):
    """Save real replay prefixes that reach healthy, useful ground for PPO practice."""
    seed = report["seed"]
    complete = completed_in_play(report)
    guide_started = report.get("authoredRouteStartStep")
    partial = (not complete and isinstance(guide_started, int) and 16 <= guide_started < report["steps"]
               and report.get("authoredRouteLandings", 0) >= 3)
    learner_advance = (baseline is not None and baseline.get("seed") == seed
                       and report["reason"] != "observation-gap"
                       and report["pathProgress"] >= baseline["pathProgress"] + 8)
    if (not 100_000 <= seed < 1_000_000 or not (complete or partial or learner_advance) or not samples
            or len(samples) != report["steps"] + 1):
        return 0
    col = {name: fields.index(name) for name in
           ("step", "x", "y", "grounded", "health", "buttons", "scene", "gameState", "heroAvailable")}
    first = samples[0]
    destination = next((item["step"] for item in playable_arrivals(fields, samples)
                        if item["departure"] == START and item["scene"] == KING_PASS_DESTINATION), None)
    if (first[col["scene"]] != START or abs(first[col["x"]] - 34.5) > .25
            or first[col["health"]] != 5 or (complete and destination is None)
            or any(row[col["step"]] != i for i, row in enumerate(samples))):
        return 0
    action_by_buttons = {buttons(action): action for action in product(*(range(n) for n in ACTION_SIZES))}
    if any(row[col["buttons"]] not in action_by_buttons for row in samples[1:]):
        return 0
    actions = [action_by_buttons[row[col["buttons"]]] for row in samples[1:]]
    # One representative per spatial cell, distributed by elapsed game steps.
    stages = {}
    incumbent_cells = set(map(tuple, baseline["groundedCells"])) if learner_advance else set()
    first_site = guide_started if partial else 16
    last_site = destination if complete else report["steps"]
    for i in range(first_site, min(last_site, max(0, max_steps - 255))):
        row = samples[i]
        x, y, health = (row[col[name]] for name in ("x", "y", "health"))
        if (row[col["scene"]] == START and row[col["gameState"]] == "PLAYING"
                and row[col["heroAvailable"]] and row[col["grounded"]] and health >= 3):
            if learner_advance and not complete:
                if (int(x // 4), int(y // 4)) in incumbent_cells or i + 6 >= len(samples):
                    continue
                if any(follow[col["health"]] < health for follow in samples[i+1:i+7]):
                    continue
            stages.setdefault((round(x / 4), round(y / 4)), i)
    if not stages or (learner_advance and not (complete or partial) and len(stages) < 3):
        return 0
    remaining = sorted(stages.values())
    sites = []
    for at in np.linspace(remaining[0], remaining[-1], min(len(remaining), 14)):
        if remaining:
            site = min(remaining, key=lambda step: abs(step - at))
            remaining.remove(site)
            sites.append(site)
    records = []
    for step in sorted(sites):
        row = samples[step]
        checkpoints = [[i, samples[i][col["x"]], samples[i][col["y"]], samples[i][col["health"]]]
                       for i in range(24, step, 24)]
        record = dict(seed=seed, actions=actions[:step], x=row[col["x"]], y=row[col["y"]],
                      health=row[col["health"]], grounded=True, checkpoints=checkpoints)
        if report.get("renderHz") in (60, 120):
            record["renderHz"] = report["renderHz"]
        records.append(record)
    directory = work / "route-starts"
    directory.mkdir(exist_ok=True)
    save_json(directory / f"{seed}.json", records)
    return len(records)


def route_files(library, limit=3):
    paths = [path for path in (library / "completed-routes").glob("*.npz")
             if path.stem.isdecimal() and 100_000 <= int(path.stem) < 1_000_000]
    return sorted(paths, key=lambda p: (p.stat().st_mtime_ns, p.name), reverse=True)[:limit]


def route_examples(paths, end=None, per_route=320):
    examples = []
    for path in paths:
        with np.load(path, allow_pickle=False) as data:
            states, actions = data["states"], data["actions"]
            indices = np.arange(len(states))
            if end is not None:
                # Use the first approach on the same floor. A later pass above the same
                # obstacle can have an entirely different horizontal goal.
                nearby = np.flatnonzero((abs(states[:, 1] - end["x"]) <= 5)
                                        & (abs(states[:, 2] - end["y"]) <= .7))
                if not len(nearby):
                    continue
                first = states[nearby[0], 0]
                indices = indices[(states[:, 0] >= first - 12) & (states[:, 0] < first + 35)]
            if len(indices) > per_route:
                edges = indices[1:][np.any(actions[indices[1:]] != actions[indices[:-1]], axis=1)]
                edges = np.intersect1d(indices, np.union1d(edges, edges - 1))
                edge_count = min(len(edges), per_route // 2)
                selected = edges[np.linspace(0, len(edges) - 1, edge_count, dtype=int)]
                rest = np.setdiff1d(indices, selected)
                indices = np.union1d(selected, rest[np.linspace(0, len(rest) - 1, per_route - edge_count, dtype=int)])
            observations = data["observations"][indices]
            examples.extend(zip(observations, actions[indices].tolist()))
    return examples
