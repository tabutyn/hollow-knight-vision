"""Improve the selected Hacker or Gameplay actor with matched, fresh King's Pass rollouts."""
from __future__ import annotations

import argparse
import copy
from collections import Counter, deque
from contextlib import ExitStack
from dataclasses import dataclass
from datetime import datetime, timezone
import gzip
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import time
import uuid

import numpy as np
import torch
from stable_baselines3 import PPO

from export_bot_policy import export_policy, write_parity_fixture
from gym_authored_route import AuthoredRoute, PitJumpPractice, RoutePractice
from gym_blockade_response import (at_opening, confirmed_strikes, human_strike_edges, learn_blockade_response,
                                  learn_climb_response, measured_strikes, nearby_wall_strikes, opening_steps,
                                  repeated_openings, route_strike_edges, save_wall_strikes, stalled_strikes)
from gym_cache_teacher import CachePractice
from gym_combat_rehearsal import CombatRehearsal, nearby_combat, protected_approach, protected_landing, save_combat, sees_opponent
from gym_demonstration_teacher import DemonstrationPractice, training_routes
from gym_landing_rehearsal import (LandingRehearsal, MINIMUM_SAFE_CLIMB, missed_jump_hold, missed_jump_rearm,
                                   missed_ledge_takeoff, nearby_landings, save_landings)
from gym_platform_teacher import reachable_platform
from gym_general_training import train_batch
from bot_training_ipc import exclusive_file_lock
from bot_policy_continuation import gym_observations, playback_checkpoint, publication_lock, save_gym_observations
from gym_policy_search import SampledFrontier, learn_from_returns
from gym_route_rehearsal import (playable_arrivals, reached_king_pass_destination, route_examples, route_files,
                                 route_library, store_route, store_route_starts)
from hacker_gym import HackerGym, buttons, save_json
from hkv_bot_dataset import ACTION_SIZES, SCHEMAS, demonstration_controls
from train_bot_job import evaluate

if os.name == "nt":
    local_app_data = Path(os.environ["LOCALAPPDATA"])
    SUPPORT = local_app_data / "HollowKnightVision"
    VISION = local_app_data / "Programs/Hollow Knight Vision/HollowKnightVision.Windows.Host.exe"
else:
    SUPPORT = Path.home() / "Library/Application Support/HollowKnightVision"
    VISION = Path("/Applications/Hollow Knight Vision.app/Contents/MacOS/HollowKnightVision")
HEAD_START = np.cumsum([0] + ACTION_SIZES[:-1]).tolist()


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_bytes())


def windows_gym_identity(args):
    command = [args.vision, "--gym-runtime-info", args.domain, args.game, args.support]
    result = subprocess.run(command, capture_output=True, text=True, timeout=30)
    if result.returncode:
        raise ValueError(result.stderr.strip() or "Windows Vision could not validate the Gym runtime.")
    info = json.loads(result.stdout)
    fields = ("visionSource", "receiver", "visualDetectorAndHUD") if args.domain == "gameplay" else ("visionSource", "receiver")
    identity = {key: info[key] for key in fields}
    if any(not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value.lower())
           for value in identity.values()):
        raise ValueError("The installed Windows Vision returned invalid Gym runtime evidence.")
    return identity


def gym_identity_reader(args):
    if os.name == "nt":
        return lambda: windows_gym_identity(args)
    if f"--{args.domain}-gym-observations".encode() not in args.vision.read_bytes():
        raise ValueError("Install the current Vision release with macos/build-app.sh")
    build_info = args.vision.parents[1] / "Resources/runtime-build-info.json"
    receiver = args.game.parents[1] / "Resources/Data/Managed/Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"

    def identity():
        result = dict(visionSource=read(build_info)["swiftSourceSHA256"], receiver=sha(receiver))
        if args.domain == "gameplay":
            result["visualDetectorAndHUD"] = visual_identity(args.support)
        return result

    return identity


def reserve_holdouts(shared, domain):
    """Allocate unused evaluation starts under the single worker lock, across builds and restarts."""
    cursor = shared / f"{domain}-next-evaluation-seed.json"
    if cursor.exists():
        first = read(cursor)["next"]
    else:
        first = 2_000_000
        for log in shared.glob("*/episodes.jsonl"):
            for line in log.read_text().splitlines():
                entry = json.loads(line)
                # Early Gym logs predate the domain field. Reserve their evaluation
                # seeds for both domains, including audits outside the training loop.
                seed = entry.get("seed")
                if entry.get("domain") in (None, domain) and isinstance(seed, int) and seed >= 2_000_000:
                    first = max(first, seed + 1)
    save_json(cursor, {"next": first + 3})
    return range(first, first + 3)


def next_training_round(root, domain):
    latest = 0
    for saved in root.glob("*/state.json"):
        previous = read(saved)
        if previous.get("identity", {}).get("domain") == domain and not previous["identity"].get("method"):
            latest = max(latest, previous["round"])
    return latest


def visual_identity(support):
    root = support / "models-v1"
    records = read(root / "registry.json")["objects"]
    if "shared-object-model" in records:
        records = {"shared-object-model": records["shared-object-model"]}
    digest = hashlib.sha256()
    for name, entry in sorted(records.items()):
        point = next((p for p in entry["restorePoints"] if p["version"]["major"] == entry.get("activeMajorVersion")), None)
        if not point:
            continue
        digest.update(json.dumps(point, sort_keys=True).encode())
        v = point["version"]
        package = root / name / f"v{v['major']}.{v['minor']}" / point["modelFilename"]
        for file in (sorted(p for p in package.rglob("*") if p.is_file()) if package.is_dir() else [package]):
            digest.update(file.read_bytes())
        run_id = point.get("sourceRunIdentifier")
        if not run_id:
            continue
        for actor in ("crawlid", "knight", "geo-deposit"):
            calibration = root / name / "calibrations" / f"{actor}-{run_id.lower()}.json"
            if calibration.is_file():
                contents = calibration.read_bytes()
                digest.update(contents)
                try:
                    recipe = json.loads(contents)
                except (UnicodeDecodeError, ValueError):
                    recipe = None
                localizer = recipe.get("localizer") if isinstance(recipe, dict) else None
                filename = localizer.get("modelFilename") if isinstance(localizer, dict) else None
                if isinstance(filename, str) and filename == Path(filename).name and filename.endswith(".mlmodel"):
                    companion = calibration.parent / filename
                    if companion.is_file():
                        digest.update(companion.read_bytes())
    for path in sorted((support / "labeling-v1/examples").glob("*/example.json")):
        example = read(path)
        if example.get("contextIdentifier") == "gameplay-hud":
            digest.update(path.read_bytes())
            digest.update((path.parent / example["imageFilename"]).read_bytes())
    return digest.hexdigest()


def route_anchors(route, limit):
    """Include the first observations and the presses/releases along a successful approach."""
    if len(route) <= limit:
        return [row[1] for row in route]
    edges = [i for i in range(1, len(route)) if route[i][2] != route[i - 1][2]]
    keep = set(range(min(4, limit)))
    if edges:
        count = min(len(edges), max(0, (limit - len(keep)) // 2))
        keep.update(edges[i] for i in np.linspace(0, len(edges) - 1, count, dtype=int))
    remaining = [i for i in range(len(route)) if i not in keep]
    keep.update(remaining[i] for i in np.linspace(0, len(remaining) - 1, limit - len(keep), dtype=int))
    return [route[i][1] for i in sorted(keep)]


def expert_anchors(archive, sessions, limit=256):
    """Rehearse steering, aim, jump and nail transitions alongside steady play in the training clips."""
    session_ids, segments = archive["session_ids"], archive["segments"]
    indices = np.flatnonzero(np.isin(session_ids, sessions))
    if len(indices) <= limit:
        return archive["observations"][indices]
    actions = archive["actions"]
    adjacent = [i for i in indices if i > 0 and session_ids[i] == session_ids[i - 1] and segments[i] == segments[i - 1]]
    selected = set()
    for head in range(len(ACTION_SIZES)):
        edges = [i for i in adjacent if actions[i, head] != actions[i - 1, head]]
        count = min(limit // (2 * len(ACTION_SIZES)), len(edges))
        selected.update(edges[k] for k in np.linspace(0, len(edges) - 1, count, dtype=int))
    other = [i for i in indices if i not in selected]
    selected.update(other[k] for k in np.linspace(0, len(other) - 1, limit - len(selected), dtype=int))
    return archive["observations"][sorted(selected)]


def retain_maneuver(examples, limit=96):
    # Repeated holds can last seconds. Preserve button edges and both sides of a
    # release without putting hundreds of near-identical observations in the fit.
    if len(examples) <= limit:
        return examples
    edges = {0, len(examples) - 1}
    for i in range(1, len(examples)):
        if examples[i - 1][1] != examples[i][1]:
            edges.update((i - 1, i))
    choices = sorted(edges)
    if len(choices) > limit // 2:
        choices = [choices[i] for i in np.linspace(0, len(choices) - 1, limit // 2, dtype=int)]
    selected = set(choices)
    remaining = [i for i in range(len(examples)) if i not in selected]
    choices += [remaining[i] for i in np.linspace(0, len(remaining) - 1, limit - len(choices), dtype=int)]
    return [examples[i] for i in sorted(choices)]


def enemy_maneuver(frame, horizontal):
    """Teacher-only jump and descending nail opportunities from the projected actor boxes."""
    actors = frame.get("actors") or {}
    knight = actors.get("hero")
    sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
    if not knight or min(sx, sy) <= 0:
        return None
    sign = -1 if horizontal == 1 else 1
    for enemy in actors.get("enemies", []):
        ahead = sign * (enemy["screenX"] - knight["screenX"]) / sx
        gap = ahead - (knight["width"] + enemy["width"]) / (2 * sx)
        drop = (knight["screenY"] - enemy["screenY"]) / sy
        reach = (enemy["height"] + knight["height"]) / (2 * sy)
        if frame["grounded"] and ahead >= 0 and gap <= 3.8 and abs(drop) <= reach + 0.25:
            return "jump"
        if (not frame["grounded"] and frame.get("velocityY", 0) < -1 and
                -0.9 <= ahead <= enemy["width"] / (2 * sx) + 1.2 and reach <= drop <= reach + 2.8):
            return "downward nail"
    return None


def damage_lesson(hit, lead, duration):
    if hit.get("descendingOnVisibleEnemy"):
        return (0, 0, "protect descending landings"), (1, 3)
    if hit.get("approachingVisibleEnemy"):
        return (0, 0, "strike approaching enemy", (hit["x"], hit["y"])), (1, 3)
    return (max(0, hit["step"] - lead), duration, "jump", "avoid enemies"), (0, 2, 3)


def safe_crossing_prefix(prior, safe_metrics, first_damage, examples):
    """Practice an earlier obstacle if the teacher crossed it cleanly before a later hit."""
    if prior is None or first_damage is None or not examples:
        return None
    step, cells, distance = safe_metrics
    if step >= first_damage["step"] or cells < prior["new_cells"] + 2 or distance < prior["rightward_distance"] + 8:
        return None
    # Retain only actions that preceded the demonstrated safe landing.
    return step if sum(index < step for index, _, _ in examples) >= 4 else None


def productive_windows(windows, outcomes, cutoff):
    """Keep interventions followed by new ground, a confirmed kill, or reaching the exit."""
    return [(start, min(end, cutoff)) for start, end in windows
            if any(start < earned <= min(end + 22, cutoff) for earned in outcomes)]


def moving_approach_end(trajectory, onset):
    """Exclude waiting at the very contact where a new action must become possible."""
    onset = min(max(0, onset), len(trajectory) - 1)
    x, y = TRAJECTORY_FIELDS.index("x"), TRAJECTORY_FIELDS.index("y")
    contact = trajectory[onset]
    while onset > 0:
        previous = trajectory[onset - 1]
        if abs(contact[x] - previous[x]) > 1.1 or abs(contact[y] - previous[y]) > 0.6:
            break
        onset -= 1
    return onset


@dataclass
class RolloutTrace:
    opening: np.ndarray
    frontier: list
    fault: list
    anchors: list
    maneuver: list
    first_crossing: tuple | None
    sampled: tuple | None
    combat: list
    climbed: list
    contacts: list
    completed_route: list | None = None
    wall_strikes: list | None = None


def first_crossing_lesson(reference, trajectory, windows, examples, route):
    if not reference or not windows:
        return None
    # A long demonstration may cross several hazards. Learn the first new safe landing
    # independently rather than scaling all of its controls together.
    step, x, grounded, hp = (TRAJECTORY_FIELDS.index(k) for k in ("step", "x", "grounded", "health"))
    goal_x = trajectory[0][x] + reference["rightward_distance"]
    health = trajectory[0][hp]
    crossing = None
    for point in trajectory:
        if point[hp] < health:
            break
        if point[grounded] and point[x] >= goal_x + 4:
            crossing = point[step]
            break
    if crossing is None:
        return None
    local = [(start, end) for start, end in windows if start <= crossing and
             any(abs(point[x] - goal_x) <= 5 for point in trajectory[start:min(end + 8, crossing + 1)])]
    if not local:
        return None
    start = min(s for s, _ in local)
    # Retain the immediately following avoidance if the teacher met an enemy just
    # past the obstacle. Stop before a later encounter or any loss of health.
    finish = min(crossing + 18, next((p[step] - 2 for p in trajectory[crossing:] if p[hp] < health), len(trajectory) - 1))
    local_examples = [(obs, action) for k, obs, action in examples if start <= k <= finish]
    if len(local_examples) < 4 or len(local_examples) >= len(examples) * 0.9:
        return None
    approach_end = moving_approach_end(trajectory, start)
    anchors = route_anchors([r for r in route if r[0] < approach_end - 3], 28)
    anchors += route_anchors([r for r in route if r[0] > finish + 4 and
                              all(r[0] < a - 3 or r[0] > b + 4 for a, b in windows)], 12)
    return retain_maneuver(local_examples), anchors


TRAJECTORY_SENSORS = ("knight_confidence", "blockade_direction", "enemy_0_present", "enemy_0_x", "enemy_0_y",
                      "geo_cache_present", "geo_cache_dx", "geo_cache_dy")
TRAJECTORY_FIELDS = ("step", "ticks", "x", "y", "vx", "vy", "grounded", "health", "buttons", "enemyDX", "enemyDY",
                     *TRAJECTORY_SENSORS, "geo", "depositHits", "depositsMined", "enemyKills",
                     "scene", "gameState", "heroAvailable")


def trajectory_point(frame, applied, observation, sensor_indices):
    actors = frame.get("actors") or {}
    hero = actors.get("hero")
    sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
    nearby = []
    if hero and min(sx, sy) > 0:
        nearby = [((e["screenX"] - hero["screenX"]) / sx, (e["screenY"] - hero["screenY"]) / sy)
                  for e in actors.get("enemies", [])]
    dx, dy = min(nearby, key=lambda p: p[0]**2 + p[1]**2) if nearby else (None, None)
    return [frame["step"], frame["ticks"], round(frame["heroX"], 2), round(frame["heroY"], 2),
            round(frame["velocityX"], 2), round(frame["velocityY"], 2), frame["grounded"], frame["health"],
            buttons(applied), None if dx is None else round(dx, 2), None if dy is None else round(dy, 2),
            *(round(float(observation[i]), 3) if i is not None and observation is not None else None for i in sensor_indices),
            frame.get("geo"), frame.get("depositHits"), frame.get("depositsMined"), (actors.get("enemyOutcomes") or {}).get("kills"),
            frame["sceneName"], frame["gameState"], frame["heroAvailable"]]


def rollout(env, model, seed, intervention=None, reference=None, openings=(), practice_inputs=None):
    """Remember the opening and the decisions immediately beyond the last frontier."""
    launched = time.monotonic()
    obs, _ = env.reset(seed=seed)
    ready = time.monotonic()
    schema = SCHEMAS[env.version]
    mining = bool(intervention and intervention[2] == "mine observed cache")
    cache_practice = CachePractice(schema, intervention[3] if mining and len(intervention) > 3 else None)
    combat_practice = CombatRehearsal()
    landing_practice = LandingRehearsal()
    search = SampledFrontier(seed) if intervention and intervention[2] == "sample policy" else None
    human = DemonstrationPractice(intervention[3]) if intervention and intervention[2] == "human recovery" else None
    climb = RoutePractice(intervention[3]) if intervention and intervention[2] == "authored climbing route" else None
    pit = PitJumpPractice(intervention[3]) if intervention and intervention[2] == "escape authored pit" else None
    guided_route = [] if climb is not None and 100_000 <= seed < 1_000_000 else None
    indices = [schema.index(k) - len(schema) if k in schema else None for k in TRAJECTORY_SENSORS]
    blockade_index = schema.index("blockade_direction") - len(schema) if "blockade_direction" in schema else None
    trajectory = [trajectory_point(env.last_frame, [0, 0, 0, 0], obs if env.usable else None, indices)]
    opening = obs.copy()
    examples = []
    frontier, fault, route = [], [], []
    contacts = deque(maxlen=32)
    rescue_start, rescue_windows = -100, []
    rescue_positions, backtracks = [], []
    rescue_direction, retreating = 2, False
    platform, platforms = None, []
    rescue_target = next((value for value in intervention[3:] if isinstance(value, tuple)), None) if intervention else None
    reached_target = rescue_target is None
    dodge_start, dodge_direction, dodge_windows = -100, 2, []
    strike_start, strike_delay, strike_windows = -100, 0, []
    landing_only = bool(intervention and intervention[2] == "protect descending landings")
    approach_only = bool(intervention and intervention[2] == "strike approaching enemy")
    combat_only = landing_only or approach_only
    last_visible_descent = last_visible_approach = -100
    barrier_start, barrier_delay, barrier_windows = -100, 0, []
    barrier_attempts = Counter()
    tail = Counter()
    obstruction_step, contact_streak = None, 0
    outcomes = []
    total, count, missing = 0., 0, 0
    first_damage = None
    unharmed = env.score.metrics()
    safe_ground = deque([(0, 0, 0.)], maxlen=8)
    opening_min_x, cleared_spawn = env.score.start, False
    turned_from_left = returned_from_left = False
    applied_before = [0, 0, 0, 0]
    first = []
    try:
        while True:
            if practice_inputs is not None:
                practice_inputs.append(obs.copy())
            action, _ = model.predict(obs, deterministic=True)
            frame = env.last_frame
            x, step = frame["heroX"], frame["step"]
            direction = int(action[0]) or applied_before[0] or (2 if frame.get("facingRight", True) else 1)
            if (env.usable and not frame["grounded"] and frame.get("velocityY", 0) < -1
                    and enemy_maneuver(frame, direction) == "downward nail" and sees_opponent(obs[-len(schema):], frame)):
                last_visible_descent = step
            if (env.usable and frame["grounded"] and direction in (1, 2)
                    and sees_opponent(obs[-len(schema):], frame, approaching=1 if direction == 2 else -1)):
                last_visible_approach = step
            if not cleared_spawn:
                opening_min_x = min(opening_min_x, x)
                if opening_min_x < env.score.start - 2:
                    turned_from_left |= x >= opening_min_x + 2
                    returned_from_left |= x >= env.score.start
                cleared_spawn = x >= env.score.start + 4
            rescuing = intervention is not None and intervention[2] in ("frontier rescue", "platform rescue")
            barriers = intervention is not None and intervention[2] == "strike marked barriers"
            mining_now = False
            copying = False
            climbing = False
            practicing_jump = False
            if not reached_target:
                reached_target = (abs(x - rescue_target[0]) <= (8 if approach_only else 4) and
                                  abs(frame["heroY"] - rescue_target[1]) <= (.7 if climb else 2) and
                                  (climb is None or frame["grounded"]))
            if search is not None and env.usable:
                sampled = search.choose(model, obs, step, step - env.score.last_progress)
                if sampled is not None:
                    action = sampled
            elif pit is not None and env.usable:
                jump_button = pit.control(frame, action, applied_before)
                if jump_button is not None:
                    action = action.copy()
                    action[2] = jump_button
                    practicing_jump = True
            elif human is not None and env.usable:
                recorded = (human.controls(frame, env.score.cells, step - env.score.last_progress, applied_before)
                            if reached_target else None)
                if recorded is not None:
                    action, copying = recorded, True
            elif climb is not None and env.usable:
                controls = climb.controls(frame, applied_before) if reached_target else None
                if controls is not None:
                    action, climbing = action.copy(), True
                    action[0], action[2] = controls
                    sign = {1: -1, 2: 1}.get(controls[0], 0)
                    known_contact = (contact_streak >= 3 and action[0] == applied_before[0] and
                                     at_opening(x, frame["heroY"], sign, openings))
                    if (frame["grounded"] and abs(frame["velocityX"]) < .6 and
                            (known_contact or climb.blocked_at_exit(frame, action[0], contact_streak)
                             or blockade_index is not None and obs[blockade_index] != 0)):
                        action[3] = int(step % 5 == 0 and not applied_before[3])
                    if climb.upward_controls is not None:
                        action[1], action[3] = climb.upward_controls
            elif mining and env.usable:
                targeted = cache_practice.controls(obs[-len(schema):], frame, action, applied_before)
                if targeted is not None:
                    action, mining_now = targeted, True
            elif barriers and env.usable:
                # Use the same blockade signal available to the policy. One nail edge is
                # sufficient to test a breakable wall while preserving the learner's steering.
                sign = int(obs[blockade_index]) if blockade_index is not None else 0
                direction = 1 if sign < 0 else 2
                zone = (round(x / 2), round(frame["heroY"] / 2), sign)
                at_target = (rescue_target is None or
                             abs(x - rescue_target[0]) <= 2 and abs(frame["heroY"] - rescue_target[1]) <= .65)
                if (at_target and sign and direction in (int(action[0]), applied_before[0]) and frame["grounded"]
                        and abs(frame["velocityX"]) < 0.6 and step - barrier_start >= 6 and barrier_attempts[zone] < 3):
                    barrier_start, barrier_delay = step, int(bool(applied_before[3]))
                    barrier_windows.append((step, step + 3))
                    barrier_attempts[zone] += 1
                if 0 <= step - barrier_start < 3:
                    action = action.copy()
                    action[3] = int(step - barrier_start == barrier_delay)
            elif rescuing and env.usable:
                stalled = step - env.score.last_progress
                if reached_target and stalled >= 9 and (not rescue_windows or step >= rescue_windows[-1][1] + 6):
                    platform = None
                    if rescue_windows or intervention[2] == "platform rescue":
                        failed = [(p["x"], p["floorY"]) for p in platforms if not p["landed"] and step - p["step"] <= 65]
                        platform = reachable_platform(obs[-len(schema):], frame, env.score.cells, failed)
                    if platform:
                        platforms.append(dict(step=step, x=round(float(platform.landing), 2),
                                              floorY=round(float(platform.top), 2), landed=False))
                    repeated = sum(abs(x - px) <= 3 and abs(frame["heroY"] - py) <= 1.5 and at >= env.score.last_progress
                                   for at, px, py in rescue_positions[-3:])
                    retreating = platform is None and repeated >= 2
                    if retreating:
                        rescue_direction = 2 if tail["blockedLeft"] > tail["blockedRight"] else 1
                        backtracks.append(step)
                    else:
                        rescue_direction = 2
                    rescue_positions.append((step, x, frame["heroY"]))
                    rescue_start = step
                    rescue_windows.append((step, step + (32 if platform else 16)))
                phase = step - rescue_start
                if platform:
                    controls = platform.controls(frame, applied_before) if phase < 32 else None
                    if controls is None:
                        platforms[-1]["landed"] = platform.landed
                        rescue_windows[-1] = (rescue_start, step)
                        platform = None
                    else:
                        action = action.copy()
                        action[0], action[2] = controls
                elif rescue_windows and step < rescue_windows[-1][1]:
                    action = action.copy()
                    action[0] = rescue_direction
                    action[3] = 0 if retreating else 1 - applied_before[3]
                    # Strike a barrier first. Jump on a repeated attempt, or out of a depression.
                    jump = not retreating and (len(rescue_windows) > 1 or stalled >= 25)
                    action[2] = int(jump and 1 <= phase % 11 <= 6)
            elif intervention is not None and env.usable and not combat_only:
                onset, duration, maneuver = intervention[:3]
                index = step - onset
                if 0 <= index < duration:
                    # Test a short nail maneuver at the last frontier. Record the
                    # actual resulting states, including the action-release frames.
                    action = action.copy()
                    horizontal = int(action[0]) or applied_before[0] or (2 if frame["facingRight"] else 1)
                    action[0] = horizontal if maneuver == "jump" else 2
                    action[3] = 1 - applied_before[3]
                    if maneuver == "jump":
                        action[2] = int(1 <= index <= 5)
                    elif maneuver == "running jump":
                        # Release before the next jump so the game's edge-triggered input fires.
                        action[2] = int(1 <= index % 11 <= 6)
            if intervention and env.usable and search is None:
                evade = ((rescuing or human is not None or climb is not None) and reached_target) or "avoid enemies" in intervention[3:]
                preparing_strike = False
                if evade or (combat_only and reached_target):
                    direction = int(action[0]) or applied_before[0] or (2 if frame["facingRight"] else 1)
                    if approach_only:
                        maneuver = "horizontal nail" if last_visible_approach == step else None
                        preparing_strike = sees_opponent(obs[-len(schema):], frame,
                                                         approaching=1 if direction == 2 else -1, approach_distance=8)
                        if preparing_strike:
                            # An early swing can still be on cooldown when the enemy reaches the nail.
                            action = action.copy()
                            action[3] = 0
                    elif landing_only:
                        maneuver = "downward nail" if last_visible_descent == step else None
                    else:
                        maneuver = enemy_maneuver(frame, direction)
                    if step - dodge_start >= 10 and maneuver == "jump":
                        dodge_start, dodge_direction = step, direction
                        dodge_windows.append((step, step + 7))
                    phase = step - dodge_start
                    if 0 <= phase < 8:
                        action = action.copy()
                        action[0], action[2] = dodge_direction, int(1 <= phase <= 5)
                        action[3] = int(phase == 0 and not applied_before[3])
                    if step - strike_start >= (4 if approach_only else 5) and maneuver in ("downward nail", "horizontal nail"):
                        strike_start, strike_delay = step, int(bool(applied_before[3]))
                        strike_windows.append((step, step + 3))
                    if 0 <= step - strike_start < 3:
                        action = action.copy()
                        action[1], action[3] = (0 if approach_only else 2), int(step - strike_start == strike_delay)
                recording = (bool(rescue_windows and step <= rescue_windows[-1][1] + 3) if rescuing
                             else 0 <= step - intervention[0] < intervention[1] + 5)
                if barriers:
                    recording = 0 <= step - barrier_start < 6
                if mining:
                    recording = mining_now
                if human is not None:
                    recording = copying
                if climb is not None:
                    recording = climbing
                if pit is not None:
                    recording = practicing_jump
                if combat_only:
                    recording = preparing_strike
                if recording or 0 <= step - dodge_start < 10 or 0 <= step - strike_start < (9 if approach_only else 5):
                    examples.append((step, obs.copy(), action.tolist()))
            if env.usable and (step < 4 or step % 4 == 0 or action.tolist() != applied_before):
                route.append((step, obs.copy(), tuple(action.tolist())))
            if step == env.score.last_progress:
                frontier = []
                tail.clear()
                obstruction_step, contact_streak = None, 0
            if env.usable and len(frontier) < 16:
                frontier.append((obs.copy(), action.tolist()))
            if (100_000 <= seed < 1_000_000 and intervention is None and env.usable and blockade_index is not None
                    and frame["grounded"] and abs(frame["velocityX"]) < .6 and obs[blockade_index]
                    and (contact_streak <= 3 or step % 6 == 0)):
                sign = 1 if int(action[0]) == 2 else -1 if int(action[0]) == 1 else 0
                if sign == int(obs[blockade_index]):
                    contacts.append((x, frame["heroY"], sign, obs.copy(), action.tolist(), applied_before[3]))
            previous_kills, previous_hits = env.score.enemy_kills, env.score.deposit_hits
            was_usable = env.usable
            obs_next, reward, done, truncated, info = env.step(action)
            if (guided_route is not None and was_usable and frame["sceneName"] == "Tutorial_01"
                    and frame["heroAvailable"] and frame["gameState"] == "PLAYING"):
                guided_route.append(((step, x, frame["heroY"], env.last_frame["damage"] - frame["damage"]),
                                     obs, info["applied"]))
            if 100_000 <= seed < 1_000_000:
                combat_practice.record(step, obs if was_usable else None, obs[-len(schema):], info["applied"], frame,
                                       env.last_frame["damage"] > frame["damage"], env.score.enemy_kills > previous_kills)
                landing_practice.record(step, obs if was_usable else None, info["applied"], frame, env.last_frame)
            if search is not None:
                search.rewards.append(float(reward))
            if not env.score.damage:
                unharmed = env.score.metrics()
                previous = safe_ground[-1]
                safe_ground.append((step + 1, unharmed["new_cells"], unharmed["rightward_distance"])
                                   if env.last_frame["grounded"] else previous)
            actual = info["applied"]
            trajectory.append(trajectory_point(env.last_frame, actual, obs_next if env.usable else None, indices))
            tail[("neutral", "left", "right")[actual[0]]] += 1
            tail["jumpPresses"] += int(actual[2] and not applied_before[2])
            tail["nailPresses"] += int(actual[3] and not applied_before[3])
            blocked = actual[0] != 0 and frame["grounded"] and abs(env.last_frame["heroX"] - x) < 0.07
            if blocked:
                tail["blockedLeft" if actual[0] == 1 else "blockedRight"] += 1
            contact_streak = contact_streak + 1 if blocked else 0
            if contact_streak == 4 and obstruction_step is None:
                obstruction_step = step - 3
            if (env.score.last_progress == step + 1 or env.score.enemy_kills > previous_kills
                    or env.score.deposit_hits > previous_hits or env.score.completed):
                outcomes.append(step + 1)
            if len(first) < 5:
                first.append(dict(x=round(x, 2), action=info["applied"]))
            if first_damage is None and env.last_frame["damage"] > frame["damage"]:
                first_damage = dict(step=step + 1, x=round(env.last_frame["heroX"], 2), y=round(env.last_frame["heroY"], 2))
                if step - last_visible_descent <= 2:
                    first_damage["descendingOnVisibleEnemy"] = True
                if step - last_visible_approach <= 4:
                    first_damage["approachingVisibleEnemy"] = True
                if env.usable:
                    fault = [(obs.copy(), action.tolist())]
            applied_before = info["applied"]
            obs = obs_next
            total += reward
            count += 1
            missing += not env.usable
            if done or truncated:
                break
        report = dict(seed=seed, episodeID=env.episode, reward=round(total, 3), steps=count, **env.score.metrics(),
                      startupSeconds=round(ready - launched, 2),
                      decisionsPerSecond=round(count / max(.001, time.monotonic() - ready), 2),
                      reason=info["reason"], first=first, missing=missing, frontier_step=env.score.last_progress,
                      first_damage=first_damage, playableArrivals=playable_arrivals(TRAJECTORY_FIELDS, trajectory),
                      end=dict(scene=env.last_frame["sceneName"],
                          gameState=env.last_frame["gameState"], heroAvailable=env.last_frame["heroAvailable"], ticks=env.last_frame["ticks"],
                          x=round(env.last_frame["heroX"], 2), y=round(env.last_frame["heroY"], 2)))
        report["renderHz"] = env.last_frame.get("renderHz", 60)
        report["frontierActions"] = dict(tail)
        report["groundedCells"] = sorted(env.score.cells)
        if env.domain == "hacker" and intervention is None and not reached_king_pass_destination(report):
            missed = missed_ledge_takeoff(TRAJECTORY_FIELDS, trajectory, report["frontier_step"])
            if missed is not None:
                report["missedTakeoff"] = missed
        if report["reason"] == "stalled" and env.usable:
            ledge = reachable_platform(obs[-len(schema):], env.last_frame, env.score.cells)
            if ledge:
                report["reachablePlatformAtStall"] = dict(x=round(float(ledge.landing), 2), floorY=round(float(ledge.top), 2))
            cache = cache_practice.target(obs[-len(schema):], env.last_frame)
            if cache:
                report["nearbyCacheAtStall"] = dict(x=round(cache[0], 2), dy=round(cache[1], 2))
        report["obstructionStep"] = obstruction_step
        report["unharmedCells"], report["unharmedDistance"] = unharmed["new_cells"], unharmed["rightward_distance"]
        report["openingLeftDistance"] = round(env.score.start - opening_min_x, 2)
        report["turnedFromLeft"] = turned_from_left
        report["returnedFromLeft"] = returned_from_left
        if env.domain == "gameplay":
            report["perception"] = dict(env.perception)
            if not env.perception["usable"]:
                raise RuntimeError(f"No usable image-based observations: {env.perception}")
        if info["reason"] == "unexpected-scene" and not reached_king_pass_destination(report):
            raise RuntimeError(f"Unexpected level transition to {env.last_frame['sceneName']}")
        cutoff = intervention[0] if intervention else env.score.last_progress
        wall_strikes = (confirmed_strikes({"fields": TRAJECTORY_FIELDS, "samples": trajectory}, route)
                       if 100_000 <= seed < 1_000_000 and blockade_index is not None else [])
        if search is not None:
            report["sampledDecisions"] = len(search.steps)
            cutoff = search.steps[0] if search.steps else env.score.last_progress
        if rescue_windows:
            report["rescueWindows"] = rescue_windows
        if platforms:
            report["platformAttempts"] = platforms
        if backtracks:
            report["turnaroundSteps"] = backtracks
        if dodge_windows:
            report["enemyAvoidances"] = len(dodge_windows)
        if approach_only:
            report["horizontalStrikes"] = len(strike_windows)
        elif strike_windows:
            report["downwardStrikes"] = len(strike_windows)
        if barriers:
            report["markedBarrierAttempts"] = len(barrier_windows)
        if mining:
            report["cacheSwings"] = cache_practice.swings
            report["cacheTargetSeen"] = cache_practice.reached_destination
        windows = rescue_windows + dodge_windows + strike_windows + barrier_windows + cache_practice.windows
        if pit is not None and pit.verified is not None:
            report["pitExit"] = dict(contact=pit.start, jumpPress=pit.pressed, landing=pit.landing, verified=pit.verified,
                                     direction=pit.lip.direction)
            windows.append((pit.start, pit.verified))
        if combat_only and reference:
            verify = protected_approach if approach_only else protected_landing
            safe = verify(trajectory, TRAJECTORY_FIELDS, strike_windows, reference.get("first_damage"))
            if safe:
                start, end, landing = safe
                if approach_only:
                    report["protectedApproach"] = {"strikeStep": start, "killStep": landing}
                    # A Crawlid can survive the first strike. Keep the interval
                    # between its recoil and the finishing nail, including release.
                    end = max(end, landing)
                else:
                    report["protectedLanding"] = {"strikeStep": start, "landingStep": landing}
                windows = [(start, end)]
                examples = [row for row in examples if start - (6 if approach_only else 0) <= row[0] <= end + 2]
        if human is not None:
            report["humanRecoveryClips"] = human.clips
            windows.extend(human.windows)
        if climb is not None:
            report["authoredRouteLandings"] = len(climb.landings)
            if climb.windows:
                report["authoredRouteStartStep"] = climb.windows[0][0]
            report["overheadStrikes"] = climb.upward_strikes
            report["teacherGroundSHA256"] = climb.graph.source_hash
            windows.extend(climb.windows)
        if intervention and not (rescuing or barriers or mining or search or human or climb or pit or combat_only):
            windows.append((cutoff, cutoff + intervention[1]))
        if windows:
            cutoff = min(start for start, _ in windows)
        prefix = None
        confirmed_approach = "protectedApproach" in report
        if pit is None and not confirmed_approach and reference and not wins(report, reference, require_crossing=True):
            prefix = safe_crossing_prefix(reference, safe_ground[0], first_damage, examples)
        if prefix is not None:
            report["unharmedCrossingStep"] = prefix
            route = [row for row in route if row[0] <= prefix]
            examples = [row for row in examples if row[0] < prefix]
        if pit is not None:
            # The adjacent rim can be in the same exploration cell. Use its verified
            # landing, including six damage-free decisions, as the training outcome.
            examples = [row for row in examples if pit.verified is not None and pit.start <= row[0] <= pit.verified]
            report["teachingWindows"] = int(bool(examples))
        elif confirmed_approach:
            # The kill and six unharmed steps already verified this sequence, even
            # if the enemy marked the last progress event in the entire episode.
            report["teachingWindows"] = int(bool(examples))
        elif intervention:
            finish = prefix if prefix is not None else max(outcomes, default=0)
            productive = productive_windows(windows, outcomes, finish)
            examples = [row for row in examples if row[0] < finish and
                        any(start <= row[0] <= end + 4 for start, end in productive)]
            route = [row for row in route if row[0] <= finish]
            report["teachingWindows"] = len(productive)
        approach_end = moving_approach_end(trajectory, cutoff) if intervention and windows else cutoff
        approach = [row for row in route if row[0] < approach_end - 5]
        downstream = []
        if windows:
            downstream = [row for row in route if row[0] > cutoff and
                          all(row[0] < start - 3 or row[0] > end + 5 for start, end in windows)]
        anchors = route_anchors(approach, 24) + route_anchors(downstream, 8)
        early = first_crossing_lesson(reference, trajectory, windows, examples, route)
        trace = RolloutTrace(opening, frontier, fault, anchors, retain_maneuver([(obs, act) for _, obs, act in examples]),
                             early, search.experience() if search is not None else None,
                             combat_practice.confirmed, landing_practice.confirmed, list(contacts), guided_route, wall_strikes)
        return report, trace, trajectory
    finally:
        env.close()


def adjust(model, samples, head, target, margin, protected=()):
    """Minimal readout change that favors the desired choice at observed states."""
    with torch.no_grad():
        net = model.policy
        x = torch.as_tensor(np.stack(samples))
        latent = net.mlp_extractor.forward_actor(net.extract_features(x, net.pi_features_extractor))
        layer = net.action_net
        basis = None
        if len(protected):
            anchor = torch.as_tensor(np.stack(protected))
            anchor = net.mlp_extractor.forward_actor(net.extract_features(anchor, net.pi_features_extractor))
            anchor = torch.cat((anchor, torch.ones(len(anchor), 1)), dim=1)
            _, singular, vectors = torch.linalg.svd(anchor, full_matrices=False)
            basis = vectors[singular > singular[0] * 1e-4]
        for state in latent:
            start = HEAD_START[head]
            logits = layer(state)[start:start + ACTION_SIZES[head]]
            rivals = logits.clone()
            rivals[target] = -torch.inf
            rival = int(rivals.argmax())
            deficit = float(logits[rival] - logits[target]) + margin
            if deficit <= 0:
                continue
            direction = torch.cat((state, state.new_ones(1)))
            if basis is not None:
                # A late correction should leave logits unchanged on the approach that worked.
                direction -= basis.T @ (basis @ direction)
            norm = float(direction.dot(direction))
            if norm < 0.25:
                continue
            amount = min(deficit / (2 * norm), 4. / (norm ** 0.5))
            layer.weight[start + target].add_(direction[:-1], alpha=amount)
            layer.weight[start + rival].add_(direction[:-1], alpha=-amount)
            layer.bias[start + target] += amount * direction[-1]
            layer.bias[start + rival] -= amount * direction[-1]


def propose(incumbent, traces, number):
    opening, frontier, fault, safe = traces.opening, traces.frontier, traces.fault, traces.anchors
    if not frontier:
        frontier = [(opening, [0, 0, 0, 0])]
    # Choose a single maneuver for the encountered failure. Adjusting the readout
    # retains the expert's perception, other action heads and temporal input.
    model = copy.deepcopy(incumbent)
    branch = number % 8
    samples = [obs for obs, _ in frontier[::3]]
    if branch == 0:
        adjust(model, samples, 0, 2, 2., protected=safe)
    elif branch in (1, 2, 3):
        head = 2 if branch != 2 else 3
        for obs, action in frontier[::3]:
            adjust(model, [obs], head, 1 - action[head], 1.5 + number // 8 % 3, protected=safe)
        if branch == 3:
            adjust(model, samples, 0, 2, 2., protected=safe)
    elif branch == 4:
        adjust(model, [obs for obs, _ in fault] or samples, 2, 1, 3., protected=safe)
    elif branch == 5:
        adjust(model, samples, 3, 1, 2., protected=safe)
        adjust(model, samples, 0, 2, 2., protected=safe)
    elif branch == 6:
        adjust(model, samples, 0, 1, 1.5, protected=safe)
    else:
        adjust(model, samples, 0, 2, 4., protected=safe)
        adjust(model, samples, 2, 1, 2.)
    return model


def learn_maneuver(incumbent, examples, approach, expert_inputs=None, heads=(0, 3)):
    """Fit the measured button presses and releases with a regularized readout update."""
    if 3 in heads and 1 not in heads and any(action[1] != 0 and action[3] for _, action in examples):
        heads = (*heads, 1)
    model = copy.deepcopy(incumbent)
    policy = model.policy
    layer = policy.action_net
    with torch.no_grad():
        def affine(states):
            tensor = torch.as_tensor(np.stack(states))
            vector = policy.mlp_extractor.forward_actor(policy.extract_features(tensor, policy.pi_features_extractor))
            return torch.cat((vector, vector.new_ones(len(vector), 1)), dim=1)

        x = affine([obs for obs, _ in examples])
        residual = x
        basis = None
        if approach:
            anchor = affine(approach)
            _, singular, vectors = torch.linalg.svd(anchor, full_matrices=False)
            basis = vectors[singular > singular[0] * 1e-4]
            residual = x - (x @ basis.T) @ basis
        actions = torch.tensor([action for _, action in examples])
        gram = residual.T @ residual + 0.08 * torch.eye(residual.shape[1])
        if expert_inputs is not None and len(expert_inputs):
            protected = affine(expert_inputs)
            if basis is not None:
                protected -= (protected @ basis.T) @ basis
            gram += (2 * len(x) / len(protected)) * (protected.T @ protected)
        for head in heads:
            offset, size = HEAD_START[head], ACTION_SIZES[head]
            outputs = slice(offset, offset + size)
            logits = layer(x[:, :-1])[:, outputs]
            # Change a decision only to close a measured margin deficit. Confident
            # correct actions contribute zero, including ordinary steering during a nail lesson.
            chosen = actions[:, head:head+1]
            shortfall = (logits - logits.gather(1, chosen) + 2.5).clamp_min(0)
            shortfall.scatter_(1, chosen, 0.)
            lift = shortfall.amax(dim=1, keepdim=True) / 2
            change = -(shortfall - lift).clamp_min(0)
            change.scatter_(1, chosen, lift)
            change -= change.mean(dim=1, keepdim=True)
            delta = torch.linalg.solve(gram, residual.T @ change).T
            layer.weight[outputs] += delta[:, :-1]
            layer.bias[outputs] += delta[:, -1]
    if expert_inputs is not None:
        # Refit the observed controls, anchoring unattended decisions to the parent.
        # This includes the preceding attack feature, needed to learn button releases.
        observed = torch.as_tensor(np.stack([obs for obs, _ in examples]))
        anchors = np.stack(approach) if approach else np.empty((0, observed.shape[1]), np.float32)
        anchor_inputs = torch.as_tensor(np.concatenate((anchors, expert_inputs)))
        def logits(actor, data):
            features = actor.extract_features(data, actor.pi_features_extractor)
            return actor.action_net(actor.mlp_extractor.forward_actor(features))
        with torch.no_grad():
            reference = logits(incumbent.policy, anchor_inputs)
            kept = [i for head, start in enumerate(HEAD_START) if head not in heads
                    for i in range(start, start + ACTION_SIZES[head])]
            other_heads = logits(incumbent.policy, observed)[:, kept]
        parameters = list(layer.parameters())
        train_shared_layers = len(heads) > 1 and set(heads) != {1, 3}
        if train_shared_layers:
            parameters += list(policy.mlp_extractor.policy_net.parameters())
        optimizer = torch.optim.Adam(parameters, lr=0.00035)
        for _ in range(180):
            values = logits(policy, observed)
            anchored = logits(policy, anchor_inputs)
            keep = (torch.nn.functional.smooth_l1_loss(anchored, reference)
                    if len(anchor_inputs) else values.new_zeros(()))
            if len(anchors):
                keep = keep + 3 * torch.nn.functional.smooth_l1_loss(anchored[:len(anchors)], reference[:len(anchors)])
                for start, size in zip(HEAD_START, ACTION_SIZES):
                    ref = reference[:len(anchors), start:start + size]
                    values_at_anchor = anchored[:len(anchors), start:start + size]
                    chosen = ref.argmax(1, keepdim=True)
                    alternative = values_at_anchor.clone().scatter(1, chosen, float("-inf")).amax(1)
                    keep = keep + (alternative - values_at_anchor.gather(1, chosen).flatten() + .25).clamp_min(0).mean()
            if kept:
                keep += torch.nn.functional.smooth_l1_loss(values[:, kept], other_heads)
            loss = 1.5 * keep
            for head in heads:
                start = HEAD_START[head]
                loss = loss + torch.nn.functional.cross_entropy(values[:, start:start + ACTION_SIZES[head]], actions[:, head])
            optimizer.zero_grad()
            loss.backward()
            if kept and not train_shared_layers:
                layer.weight.grad[kept] = 0
                layer.bias.grad[kept] = 0
            optimizer.step()
    return model


def interpolate(incumbent, candidate, fraction):
    model = copy.deepcopy(incumbent)
    with torch.no_grad():
        for original, edited in zip(model.policy.parameters(), candidate.policy.parameters(), strict=True):
            original.lerp_(edited, fraction)
    return model


def holds_progress(candidate, prior):
    if candidate["damage"] > prior["damage"]:
        return False
    if candidate["reason"] in ("death", "observation-gap") and prior["reason"] != candidate["reason"]:
        return False
    return (candidate["new_cells"] >= prior["new_cells"] and
            candidate["rightward_distance"] + 1 >= prior["rightward_distance"])


def crosses_frontier(candidate, prior):
    if candidate["rightward_distance"] >= prior["rightward_distance"] + 3:
        return True
    if candidate["new_cells"] < prior["new_cells"] + 2:
        return False
    old = set(map(tuple, prior.get("groundedCells", ())))
    new = set(map(tuple, candidate.get("groundedCells", ()))) - old
    if not old:
        return False
    left = min(x for x, _ in old)
    # A climb or a left branch can open new ground while the rightmost point is unchanged.
    # Require two neighboring, previously unoccupied landing cells on that route.
    alternate = {(x, y) for x, y in new if x < left or (x, y - 1) in old or (x, y - 2) in old}
    return any((x + 1, y) in alternate or (x, y + 1) in alternate for x, y in alternate)


def practice_another_start(trial, parent, child, prior, result, primary_trace, round_number, expert_inputs,
                           require_crossing, evidence):
    """Practice a second training start when the policy has learned to reach encounters."""
    seed = 500_000 + round_number
    control, _ = trial(parent, seed, "second training incumbent")
    probe, _ = trial(child, seed, "second training candidate")
    evidence["secondTrainingStart"] = [control, probe]
    if holds_progress(probe, control):
        return child, result, True
    hit = probe.get("first_damage")
    heads = (0, 2, 3)
    reference = control
    if hit and probe["damage"] > control["damage"]:
        intervention, heads = damage_lesson(hit, 5, 9)
        # The incumbent can stop before this encounter. Verify the teacher's
        # response at the collision the candidate actually experienced.
        reference = probe
    elif probe["reason"] == "stalled":
        intervention = (0, 0, "frontier rescue")
    else:
        return child, result, False
    correction, visited = trial(child, seed, "second training correction", intervention, reference=reference)
    evidence["secondTrainingCorrection"] = correction
    protected_encounter = correction.get("protectedApproach") or correction.get("protectedLanding")
    if not visited.maneuver or not (protected_encounter or holds_progress(correction, control)):
        return child, result, False
    protected = visited.anchors + primary_trace.anchors[::3]
    learned = learn_maneuver(child, visited.maneuver, protected, expert_inputs, heads=heads)
    repeat, _ = trial(learned, seed, "second training recovery")
    evidence["secondTrainingRecovery"] = repeat
    if not holds_progress(repeat, control):
        return child, result, False
    retest, _ = trial(learned, prior["seed"], "first training retest")
    evidence["firstTrainingRetest"] = retest
    return learned, retest, wins(retest, prior, require_crossing=require_crossing)


def wins(candidate, prior, require_crossing=False):
    if not holds_progress(candidate, prior):
        return False
    if require_crossing and not crosses_frontier(candidate, prior):
        return False
    return (candidate["reward"] > prior["reward"] + 0.75 and
            (candidate["new_cells"] >= prior["new_cells"] + 1 or candidate["damage"] < prior["damage"] or
             candidate.get("depositsMined", 0) > prior.get("depositsMined", 0) or
             candidate.get("enemyKills", 0) > prior.get("enemyKills", 0)))


PATH_PROGRESS_MARGIN = 0.25


def measured_clear(report):
    return report.get("completed", False) or (
        report.get("kingPassBenchmark", False) and reached_king_pass_destination(report))


def preserves_general_outcomes(candidate, prior, *, allow_longer_dwell=False):
    candidate_clear = measured_clear(candidate)
    prior_clear = measured_clear(prior)
    if candidate_clear != prior_clear:
        return candidate_clear
    # Compare coverage so a shorter run can preserve the same path evidence.
    prior_samples = prior.get("pathSamples", 0)
    candidate_samples = candidate.get("pathSamples", 0)
    prior_steps = prior.get("steps", 0)
    candidate_steps = candidate.get("steps", 0)
    if prior_steps > 0 and candidate_steps > 0:
        prior_samples /= prior_steps
        candidate_samples /= candidate_steps
    safer_or_earned = (candidate["damage"] <= prior["damage"]
                      or (not prior_clear
                          and candidate["damage"] == prior["damage"] + 1
                          and candidate["damage"] <= 2
                          and candidate["enemyKills"] > prior["enemyKills"]
                          and candidate["reward"] > prior["reward"]
                          and candidate.get("pathProgress", 0) >= prior.get("pathProgress", 0) + 8))
    return (safer_or_earned
            and not (candidate["reason"] in ("death", "observation-gap")
                     and prior["reason"] != candidate["reason"])
            and candidate_samples >= prior_samples * 0.8
            and candidate.get("pathProgress", 0) + PATH_PROGRESS_MARGIN >= prior.get("pathProgress", 0)
            and candidate["enemyDamage"] >= prior["enemyDamage"]
            and candidate["enemyKills"] >= prior["enemyKills"]
            and (allow_longer_dwell or candidate["longestDwellDecisions"] <= prior["longestDwellDecisions"] + 10))


def general_improvement(candidate, prior):
    if not preserves_general_outcomes(candidate, prior):
        return False
    return ((measured_clear(candidate) and not measured_clear(prior))
            or candidate.get("pathProgress", 0) > prior.get("pathProgress", 0) + PATH_PROGRESS_MARGIN
            or candidate["damage"] < prior["damage"]
            or candidate["enemyDamage"] > prior["enemyDamage"]
            or candidate["enemyKills"] > prior["enemyKills"]
            or (candidate["reason"] != "stalled" and candidate["reward"] >= prior["reward"]
                and candidate["longestDwellDecisions"] + 15 <= prior["longestDwellDecisions"]))


def advances_learner_past_stall(candidate, prior):
    """Keep a safe route advance for training when both runs stop short."""
    return (not measured_clear(prior) and not measured_clear(candidate)
            and candidate["reason"] == prior["reason"] == "stalled"
            and candidate["damage"] <= prior["damage"]
            and candidate.get("pathProgress", 0) >= prior.get("pathProgress", 0) + 8
            and candidate.get("unharmedCells", 0) >= prior.get("unharmedCells", 0) + 3
            and preserves_general_outcomes(candidate, prior, allow_longer_dwell=True))


def retain_measured_ppo_variant(model, work, number, raw_path, evidence):
    """Resume from the variant that improved on the training comparison."""
    if evidence.get("selectedUpdateFraction") is not None:
        name = f"learner-compared-{number:04d}.zip"
    elif evidence.get("selectedUpdate") == "rehearsal" or "privateRehearsal" in evidence:
        name = f"learner-rehearsed-{number:04d}.zip"
    else:
        return raw_path
    destination = work / name
    temporary = destination.with_suffix(".tmp")
    model.save(temporary)
    temporary.replace(destination)
    evidence["continuationLearner"] = dict(filename=name, sha256=sha(destination))
    return destination


def last_measured_ppo_learner(work, number, identity):
    """Find the latest intact learner selected by a live training comparison."""
    for saved in sorted(work.glob("comparison-*.json"), reverse=True):
        if saved.name >= f"comparison-{number:04d}.json":
            continue
        record = read(saved)
        measured = record.get("continuationLearner")
        if record.get("identity") != identity or not measured:
            continue
        name = measured["filename"]
        if not name.startswith("learner-") or not name.endswith(".zip") or Path(name).name != name:
            continue
        path = work / name
        if path.is_file() and sha(path) == measured["sha256"]:
            return path
    return None


def measured_combat_repair(work, candidate, incumbent):
    """Find safe responses near a fight the PPO update handled worse."""
    if candidate["enemyKills"] >= incumbent["enemyKills"] and candidate["damage"] <= incumbent["damage"]:
        return []
    return nearby_combat(work, candidate.get("first_damage"))


def preserves_ppo_combat_repair(repaired, candidate):
    return ((repaired["enemyKills"] > candidate["enemyKills"] or repaired["damage"] < candidate["damage"])
            and general_improvement(repaired, candidate))


def measured_takeoff_lesson(work, takeoff, graph=None):
    """Select observed jumps to the next reachable shelf from a missed takeoff."""
    if graph is not None:
        support = graph.support(takeoff["x"], takeoff["y"])
        hop = graph.route(support, takeoff["x"], set()) if support is not None else None
        if hop is not None and hop.jump:
            destination = graph.surfaces[hop.dest]
            if destination.y > graph.surfaces[support].y + MINIMUM_SAFE_CLIMB:
                return (nearby_landings(work, takeoff, direction=hop.direction, maximum_distance=3,
                                        destination=destination), (support, hop.dest))
    return nearby_landings(work, takeoff, direction=takeoff["direction"], maximum_distance=3), None


def measured_ppo_lesson(work, prior, graph=None):
    """Find measured practice for the first confirmed cause of a stopped rollout."""
    end = prior["end"]
    damage = prior.get("first_damage")
    takeoff = prior.get("missedJumpRearm") or prior.get("missedTakeoff")
    if takeoff and (damage is None or takeoff["step"] < damage["step"]):
        landing, _ = measured_takeoff_lesson(work, takeoff, graph)
        if landing:
            return "landing", landing, (0, 2)
    if damage:
        combat = nearby_combat(work, damage)
        if combat:
            return "combat", combat, (0, 1, 2, 3)
    wall = nearby_wall_strikes(work, end)
    if wall:
        return "wall strike", wall, (3,)
    landing = nearby_landings(work, end)
    if landing:
        return "landing", landing, (0, 2)
    combat = nearby_combat(work, end)
    if combat:
        return "combat", combat, (0, 1, 2, 3)
    return None


def ppo_barrier_probe(work, prior, schema):
    """Give a repeatedly stalled actor one measured alternate input at contact."""
    tail = prior.get("frontierActions", {})
    blocked = tail.get("blockedRight", 0) + tail.get("blockedLeft", 0)
    if (prior["reason"] != "stalled" or "blockade_direction" not in schema or blocked < 25
            or tail.get("nailPresses", 0) > 1):
        return None
    target = prior["end"]["x"], prior["end"]["y"]
    for saved in sorted(work.glob("comparison-*.json"))[-4:]:
        previous = read(saved).get("barrierProbe")
        if previous and abs(previous["target"][0] - target[0]) <= 1 and abs(previous["target"][1] - target[1]) <= .65:
            return None
    onset = prior.get("obstructionStep")
    return (prior["frontier_step"] if onset is None else onset, 16, "strike marked barriers", target)


def ppo_wall_rehearsal(work, candidate, contacts, incumbent):
    """Rehearse a regressed contact only when the matched incumbent really opened it."""
    tail = candidate.get("frontierActions", {})
    if (candidate["reason"] != "stalled" or tail.get("nailPresses", 0) > 1
            or candidate.get("pathProgress", 0) + 2 >= incumbent.get("pathProgress", 0)):
        return None
    end = candidate["end"]
    for x, y, side, _ in incumbent.get("wallPassages", ()):
        blocked = tail.get("blockedRight" if side > 0 else "blockedLeft", 0)
        opening = (x, y, side)
        if blocked < 25 or not at_opening(end["x"], end["y"], side, [opening]):
            continue
        missed = stalled_strikes(contacts, [opening])
        decisions = nearby_wall_strikes(work, end, direction=side) + missed
        if missed and {action[3] for _, action in decisions} == {0, 1}:
            return opening, decisions
    return None


def verified_ppo_wall_passage(report, target):
    if report["reason"] in ("observation-gap", "unexpected-scene"):
        return False
    damage = (report.get("first_damage") or {}).get("step", float("inf"))
    return any(step + 5 < damage and at_opening(x, y, side, [target])
               for x, y, side, step in report.get("wallPassages", ()))


def rehearse_crossings(work, prior, teacher, examples, *, safe_prefix=False):
    """Retain real obstacle crossings on distinct training starts, including verified unharmed prefixes."""
    seed = teacher["seed"]
    if not 100_000 <= seed < 1_000_000 or not examples:
        return examples
    bank = work / "demonstrated-crossings"
    zone = round(prior["end"]["x"] / 6)
    level = round(prior["end"]["y"] / 3)
    folder = bank / f"{zone}_{level}"
    folder.mkdir(parents=True, exist_ok=True)
    path = folder / f"{seed}.npz"
    damage = 0 if safe_prefix else teacher["damage"]
    distance = teacher["unharmedDistance"] if safe_prefix else teacher["rightward_distance"]
    replace = True
    if path.exists():
        with np.load(path, allow_pickle=False) as old:
            replace = (damage, -distance) <= (int(old["damage"]), -float(old["distance"]))
    if replace:
        temporary = path.with_suffix(".tmp")
        with temporary.open("wb") as stream:
            np.savez_compressed(stream, observations=np.stack([row for row, _ in examples]),
                actions=np.array([action for _, action in examples], np.int8), damage=damage, distance=distance, seed=seed)
        temporary.replace(path)

    peers = {}
    for peer in [*bank.glob("*/*.npz"), *bank.glob("*.npz")]:
        if peer == path:
            continue
        if peer.parent == bank:  # Earlier banks kept a single training seed per horizontal neighborhood.
            other_zone, other_level = int(peer.stem), level
        else:
            other_zone, other_level = map(int, peer.parent.name.split("_"))
        with np.load(peer, allow_pickle=False) as archive:
            other_seed, hits = int(archive["seed"]), int(archive["damage"])
            key = (other_zone, other_level, other_seed)
            if hits <= damage and key != (zone, level, seed):
                rank = (abs(zone - other_zone) + abs(level - other_level), hits, -float(archive["distance"]), peer)
                peers[key] = min(rank, peers.get(key, rank))
    # Up to two samples from the current obstacle and two from other obstacles.
    # Include only one copy of each seed/obstacle despite legacy-format archives.
    ordered = sorted(peers.values())
    chosen = ([entry[-1] for entry in ordered if entry[0] == 0][:2] +
              [entry[-1] for entry in ordered if entry[0] != 0][:2])
    rehearsals = []
    for peer in chosen:
        with np.load(peer, allow_pickle=False) as archive:
            inputs, actions = archive["observations"], archive["actions"]
            maximum = max(4, min(24, len(examples) // len(chosen)))
            ix = np.linspace(0, len(inputs) - 1, min(maximum, len(inputs)), dtype=int)
            rehearsals.extend((inputs[i], actions[i].tolist()) for i in ix)
    return examples + rehearsals


def save_exploration_branch(model, report, prior, parent, work, needs_generalization=False):
    """Retain a farther route and a damage-free route for independent practice."""
    if (report["reason"] in {"death", "observation-gap", "unexpected-scene"}
            or report["rightward_distance"] < prior["rightward_distance"] + 4):
        return
    names = ["unharmed-exploration"] if report["damage"] == 0 else []
    if report["damage"] > prior["damage"] or needs_generalization:
        names.append("exploration")
    for name in names:
        record = work / f"{name}.json"
        previous = read(record) if record.exists() else {}
        if previous.get("parent") == parent:
            old = previous["report"]
            gain = report["rightward_distance"] - old["rightward_distance"]
            if gain < -2 or (gain < 4 and report["damage"] >= old["damage"]
                             and report["new_cells"] <= old["new_cells"]):
                continue
        temporary = work / f"{name}-new.zip"
        model.save(temporary)
        checkpoint = work / f"{name}.zip"
        temporary.replace(checkpoint)
        save_json(record, dict(parent=parent, sha256=sha(checkpoint), report=report))


def prior_practice_sources(work, identity):
    """Allow earlier training demonstrations with the same model family and observation sources."""
    source = "labels" if identity["domain"] == "hacker" else "visualDetectorAndHUD"
    keys = ("domain", "base", "checkpoint", source)
    if not identity.get(source):
        return []
    paths = sorted(work.parent.glob("*/state.json"), key=lambda path: path.stat().st_mtime, reverse=True)
    sources = []
    for path in paths:
        prior = read(path).get("identity", {})
        if path.parent != work and all(prior.get(key) == identity[key] for key in keys):
            sources.append(path.parent)
    return sources


def interrupted_proposal(work, parent, prior):
    """Repeat a saved contender if this training round stopped before advancing."""
    for name in ("unharmed-exploration", "exploration"):
        receipt = work / f"{name}.json"
        weights = work / f"{name}.zip"
        if not receipt.is_file() or not weights.is_file():
            continue
        record = read(receipt)
        measured = record.get("report", {})
        if (record.get("parent") == parent and measured.get("seed") == prior["seed"]
                and measured.get("reason") not in ("death", "observation-gap", "unexpected-scene")
                and sha(weights) == record.get("sha256")):
            return weights
    return None


def measured_candidate(receipt_path, checkpoint, identity):
    """Load an externally rehearsed proposal only for its original actor and observation source."""
    receipt = read(receipt_path)
    source = "labels" if identity["domain"] == "hacker" else "visualDetectorAndHUD"
    keys = ["domain", "receiver", source]
    if identity["domain"] == "gameplay":
        keys.append("visionSource")
    seeds = receipt.get("trainingSeeds", [])
    filename = receipt.get("modelFilename", "")
    if (not isinstance(filename, str) or Path(filename).name != filename or not filename.endswith(".zip")
            or receipt.get("parent") != checkpoint.name or receipt.get("parentSHA256") != sha(checkpoint / "model.zip")
            or not seeds or not all(isinstance(s, int) and 100_000 <= s < 1_000_000 for s in seeds)
            or any(receipt.get("environment", {}).get(k) != identity[k] for k in keys)):
        raise ValueError("The proposed checkpoint does not match this parent, training data or observation source")
    weights = receipt_path.parent / filename
    if sha(weights) != receipt.get("sha256"):
        raise ValueError("The proposed checkpoint changed after its training measurement")
    return weights


def exploration_checkpoint(work, parent, identity, prefer_unharmed):
    choices = []
    for state in work.parent.glob("*/state.json"):
        other = read(state).get("identity", {})
        if any(identity[k] != other.get(k) for k in ("domain", "base", "checkpoint", "max_steps", "stall_steps")):
            continue
        for name in ("exploration", "unharmed-exploration"):
            record = state.parent / f"{name}.json"
            if not record.is_file():
                continue
            entry = read(record)
            if entry.get("parent") != parent:
                continue
            report = entry["report"]
            rank = (prefer_unharmed and report["damage"] == 0, state.parent == work,
                    report["rightward_distance"], -report["damage"], report["new_cells"])
            choices.append((rank, record.with_suffix(".zip"), entry["sha256"]))
    for _, path, digest in sorted(choices, reverse=True):
        if path.is_file() and sha(path) == digest:
            return path, digest
    return None


def gym_checkpoint_path(models, name, work=None):
    """Resolve a saved Gym checkpoint name, including the legacy desktop library."""
    if not isinstance(name, str) or name in ("", ".", "..") or Path(name).name != name:
        raise ValueError("Invalid Gym checkpoint name")
    roots = (models, models.parent) if work is None else (models, work / "winners", models.parent)
    return next((root / name for root in roots if (root / name).is_dir()), models / name)


def publish(model, base, work, models, dataset, evidence, vision, *, parent):
    identifier = str(uuid.uuid4())
    name = "bot-" + identifier
    manifest = read(base / "manifest.json")
    domain = manifest["botDomain"]
    timestamp = datetime.now(timezone.utc)
    with tempfile.TemporaryDirectory(dir=models.parent, prefix=".gym-export-") as temporary:
        stage = Path(temporary)
        model.save(stage / "model.zip")
        manifest.update(name=("Gameplay" if domain == "gameplay" else "Hacker") + " Gym · " + timestamp.strftime("%Y-%m-%d %H:%M UTC"),
                        stage=f"{domain}-gym", modelSHA256=sha(stage / "model.zip"), gymEvidence=evidence)
        parent_manifest = read(parent / "manifest.json")
        manifest["parentPolicy"] = dict(directory=parent.name, modelSHA256=parent_manifest["modelSHA256"])
        states = gym_observations(models.parent.parent, domain, parent, parent_manifest)
        manifest.pop("gymRehearsalSHA256", None)
        save_gym_observations(stage, manifest, states)
        manifest.pop("gymRoutesSHA256", None)
        routes = gym_observations(models.parent.parent, domain, parent, parent_manifest, routes=True)
        save_gym_observations(stage, manifest, routes, routes=True)
        manifest["criticTrained"] = bool(manifest.get("criticTrained") or evidence.get("method") == "ppo")
        manifest["lastGymPolicy"] = dict(directory=name, modelSHA256=manifest["modelSHA256"])
        save_json(stage / "manifest.json", manifest)
        _, exported = export_policy(stage)
        fixture = work / "parity.json"
        try:
            write_parity_fixture(exported, fixture, dataset)
            subprocess.run([vision, "--hacker-gym-observations", "verify", stage, fixture], check=True, timeout=45)
        finally:
            fixture.unlink(missing_ok=True)
        with np.load(dataset, allow_pickle=False) as data:
            held = np.isin(data["session_ids"], manifest["validationSessions"])
            if not held.any():
                raise ValueError("No held-out expert examples for this checkpoint")
            controls, _ = demonstration_controls(data)
            metric = evaluate(exported, data["observations"][held], controls[held])
        save_json(stage / "training-result.json", dict(id=identifier, name=manifest["name"], modelDirectory=name,
                  createdAt=timestamp.isoformat(), botDomain=domain, trainingExamples=manifest["trainingExamples"],
                  validationExamples=int(held.sum()), candidate=metric, eligibleForActivation=True, nativeParity=True,
                  reason="Improved gameplay on matched fresh starts and passed native export verification.",
                  liveGameplayEvaluated=True, gymEvidence=evidence, parentPolicy=manifest["parentPolicy"]))
        # The user can finish a different demonstration training during the night.
        # Retain its selection while this experimental branch remains resumable.
        with publication_lock(models):
            active = playback_checkpoint(models, domain, native_verifier=vision if os.name == "nt" else None)
            destination = models if active and active.resolve() == parent.resolve() else work / "winners"
            destination.mkdir(exist_ok=True)
            stage.rename(destination / name)
    return destination / name


def run(args):
    shared = args.support / "bot-v1/gym"
    shared.mkdir(parents=True, exist_ok=True)
    limit = time.monotonic() + args.hours * 3600
    wait = getattr(args, "wait_for_worker", False)
    with ExitStack() as held:
        try:
            held.enter_context(exclusive_file_lock(shared / "worker.lock", wait=wait, deadline=limit if wait else None))
        except TimeoutError:
            if not wait:
                raise
            print("The time budget expired while waiting for the Gym worker.", flush=True)
            return
        if time.monotonic() < limit:
            train_with_worker(args, shared, limit)


def checkpoint_path_inputs(run, objective):
    """Use the exact authored inputs that produced matching replay evidence."""
    if objective is None:
        return None
    evidence_files = sorted((run / "exit-path-evidence").glob("*.json"))
    evidence = [read(path) for path in evidence_files]
    expected = {"x": objective[0], "y": objective[1]}
    if not any(item.get("objective") == expected for item in evidence):
        return None
    if any(item.get("objective") != expected for item in evidence):
        raise ValueError("The checkpoint combines different exit path objectives")
    labels = run / "exit-path-inputs" / "labels.json"
    connections = run / "exit-path-inputs" / "connections.json"
    if not labels.is_file() or not connections.is_file():
        raise ValueError("The checkpoint's frozen exit path inputs are missing")
    label_hash, connection_hash = sha(labels), sha(connections)
    if any(item.get("labelsSHA256") != label_hash or item.get("connectionsSHA256") != connection_hash
           for item in evidence):
        raise ValueError("The checkpoint's frozen exit path inputs do not match its demonstrations")
    return labels, connections


def train_with_worker(args, shared, limit):
    models = args.support / "bot-v1/models" / args.domain
    selected = args.base or playback_checkpoint(models, args.domain,
                                                native_verifier=args.vision if os.name == "nt" else None)
    if selected is None:
        raise ValueError(f"Train or select a verified {args.domain} checkpoint before launching Gym.")
    manifest = read(selected / "manifest.json")
    # Re-running the launcher after Vision activates a Gym winner resumes its original experiment.
    origin = manifest.get("gymEvidence", {}).get("base")
    base = gym_checkpoint_path(models, origin) if origin else selected
    manifest = read(base / "manifest.json")
    version = manifest["schemaVersion"]
    training_run = args.support / "bot-v1/training-runs" / base.name.removeprefix("bot-")
    frozen_inputs = checkpoint_path_inputs(training_run, args.exit_path_objective)
    labels, connections = frozen_inputs or (args.support / "world-ground-lines-v2.json",
                                            args.support / "atlas-path-connections-v1.json")
    empty_connections = b'{"rooms":{},"scenes":{}}'
    connection_data = connections.read_bytes() if connections.is_file() else empty_connections
    if manifest.get("botDomain") != args.domain or manifest["featureNames"] != SCHEMAS.get(version):
        raise ValueError(f"Select a compatible {args.domain} policy")
    if sha(base / "model.zip") != manifest["modelSHA256"] or not labels.is_file():
        raise ValueError("The base checkpoint or authored ground is missing or inconsistent")
    dataset = training_run / "dataset.npz"
    if not args.baseline_only and (not dataset.is_file() or sha(dataset) != manifest["datasetSHA256"]):
        raise ValueError("The verified expert dataset for the selected Hacker checkpoint is required")
    runtime_identity = gym_identity_reader(args)
    runtime = runtime_identity()
    identity = dict(domain=args.domain, base=base.name, checkpoint=manifest["modelSHA256"],
                    max_steps=args.max_steps, stall_steps=args.stall_steps, **runtime,
                    scoring=9, objective=dict(scene="Tutorial_01", world=args.exit_path_objective) if args.exit_path_objective else None)
    general = args.method == "ppo"
    if general:
        identity.update(method="ppo", trainingSteps=args.training_steps, curriculum=2, startingPolicy=read(selected / "manifest.json").get("gymEvidence", {}).get("ppoOrigin", selected.name))
    if args.training_render_hz != 60:
        identity["trainingRenderHz"] = args.training_render_hz
    identity["labels"] = sha(labels)
    identity["pathConnections"] = hashlib.sha256(connection_data).hexdigest()
    guided_library = route_library(shared, identity)
    root = args.output or shared
    root.mkdir(parents=True, exist_ok=True)
    trainer_source = hashlib.sha256(b"".join(Path(__file__).with_name(name).read_bytes() for name in (
        Path(__file__).name, "hacker_gym.py", "gym_platform_teacher.py", "gym_cache_teacher.py",
        "gym_policy_search.py", "gym_demonstration_teacher.py", "gym_combat_rehearsal.py", "gym_authored_route.py",
        "gym_landing_rehearsal.py", "gym_blockade_response.py", "gym_route_rehearsal.py", "gym_general_training.py"))).hexdigest()
    work = root / hashlib.sha256(json.dumps(identity, sort_keys=True).encode()).hexdigest()[:20]
    work.mkdir(exist_ok=True)
    snapshot = work / "authored-ground.json"
    connection_snapshot = work / "path-connections.json"
    if not snapshot.exists():
        snapshot.write_bytes(labels.read_bytes())
    if sha(snapshot) != identity["labels"]:
        raise ValueError("Authored labels changed during preparation")
    if not connection_snapshot.exists():
        connection_snapshot.write_bytes(connection_data)
    if sha(connection_snapshot) != identity["pathConnections"]:
        raise ValueError("Path connections changed during preparation")
    state_file = work / "state.json"
    state = (read(state_file) if state_file.exists() else
             dict(round=0 if general else next_training_round(root, args.domain), champion=selected.name, identity=identity))
    if state.get("identity") != identity:
        raise ValueError("The saved Gym experiment has a different objective or configuration")
    if not state_file.exists():
        save_json(state_file, state)
    previous_practice = [] if general else prior_practice_sources(work, identity)
    champion = state["champion"]
    checkpoint = selected if champion == selected.name else gym_checkpoint_path(models, champion, work)
    champion_manifest = read(checkpoint / "manifest.json")
    if (champion_manifest.get("botDomain") != args.domain or champion_manifest.get("schemaVersion") != version
            or sha(checkpoint / "model.zip") != champion_manifest["modelSHA256"]):
        raise ValueError("The resumed Hacker checkpoint does not match its manifest")
    supplied = measured_candidate(args.candidate, checkpoint, identity) if args.candidate else None
    model = PPO.load(checkpoint / "model.zip", device="cpu")
    torch.set_num_threads(1)
    expert_inputs = np.empty((0, len(SCHEMAS[version]) * manifest["historyLength"]), np.float32)
    routes = None
    climbing_graph = None
    ppo_graph = AuthoredRoute.load(snapshot) if general and args.domain == "hacker" and not args.baseline_only else None
    human_edges = []
    if not args.baseline_only:
        with np.load(dataset, allow_pickle=False) as archive:
            expert_inputs = expert_anchors(archive, manifest["trainingSessions"])
            if not general and args.domain == "gameplay" and "blockade_direction" in SCHEMAS[version]:
                human_edges = human_strike_edges(archive, manifest["trainingSessions"], SCHEMAS[version])
    env = HackerGym(game=args.game, vision=args.vision, labels=snapshot, version=version, domain=args.domain,
                    supports_masked=read(checkpoint / "runtime-policy.json").get("supportsMaskedKnight", False),
                    max_steps=args.max_steps, stall_steps=args.stall_steps, deadline=limit,
                    training_render_hz=args.training_render_hz,
                    path_connections=connection_snapshot, exit_path_objective=args.exit_path_objective,
                    visual_support_root=args.support if os.name == "nt" else None)

    def verify_environment():
        if runtime_identity() != runtime:
            raise RuntimeError("Vision, receiver, or visual inputs changed during the comparison. Relaunch to evaluate the new inputs.")
        if sha(snapshot) != identity["labels"] or sha(connection_snapshot) != identity["pathConnections"]:
            raise RuntimeError("The path connection snapshot changed during the comparison.")

    def trial(actor, seed, role, intervention=None, reference=None):
        verify_environment()
        # Faster wall-clock stepping is reserved for collecting assisted demonstrations.
        # Even training-seed comparisons between autonomous policies use the game's reference rate.
        env.training_render_hz = args.training_render_hz if intervention is not None else 60
        started = time.monotonic()
        # Only the climbing guide can consult confirmed physical openings. A visual
        # student can miss the wall cue even after a sustained push at a breakable door.
        openings = repeated_openings(shared, identity) if intervention and intervention[2] == "authored climbing route" else ()
        for attempt in range(3):
            try:
                inputs = [] if general and intervention is None and 100_000 <= seed < 1_000_000 else None
                report, trace, trajectory = rollout(env, actor, seed, intervention, reference, openings, inputs)
                verify_environment()
                break
            except (TimeoutError, BrokenPipeError) as error:
                if time.monotonic() >= limit or attempt == 2:
                    raise
                print(f"Retrying interrupted {role} on seed {seed}: {error}", flush=True)
        if (ppo_graph is not None and inputs and report["reason"] == "stalled"
                and not reached_king_pass_destination(report)):
            short_jump = missed_jump_hold(TRAJECTORY_FIELDS, trajectory, ppo_graph)
            if short_jump and measured_takeoff_lesson(work, short_jump, ppo_graph)[0]:
                report["missedJumpHold"] = short_jump
            rearm = missed_jump_rearm(TRAJECTORY_FIELDS, trajectory, ppo_graph, report["frontier_step"])
            if rearm and measured_takeoff_lesson(work, rearm, ppo_graph)[0]:
                report["missedJumpRearm"] = rearm
        if inputs:
            end = min(report["frontier_step"], (report.get("first_damage") or {"step": len(inputs) + 3})["step"] - 3)
            if end > 0:
                shelf = work / "protected-routes" / role.replace(" ", "-")
                shelf.mkdir(parents=True, exist_ok=True)
                with (shelf / f"{seed}.tmp").open("wb") as stream:
                    indices = np.linspace(0, end-1, min(end, 512), dtype=int)
                    np.savez_compressed(stream, seed=seed, observations=np.stack([inputs[i] for i in indices]))
                (shelf / f"{seed}.tmp").replace(shelf / f"{seed}.npz")
        if getattr(args, "king_pass_benchmark", False):
            report["kingPassBenchmark"] = True
        trails = work / "trajectories"
        trails.mkdir(exist_ok=True)
        path = trails / f"{seed}-{role.replace(' ', '-')}-{time.time_ns()}.json.gz"
        with gzip.open(path, "wt", compresslevel=4) as output:
            json.dump(dict(domain=args.domain, role=role, seed=seed, fields=TRAJECTORY_FIELDS, samples=trajectory),
                      output, separators=(",", ":"))
        report["trajectory"] = str(path.relative_to(work))
        if general and intervention is None and 100_000 <= seed < 1_000_000:
            physical = {"fields": TRAJECTORY_FIELDS, "samples": trajectory}
            report["wallPassages"] = [(*contact, trajectory[index][0]) for index, contact in opening_steps(physical)]
        if trace.combat:
            save_combat(work, seed, trace.combat)
            report["visibleUnharmedKills"] = len(trace.combat)
        if trace.climbed:
            save_landings(work, seed, trace.climbed)
            report["cleanClimbs"] = len(trace.climbed)
        if trace.completed_route:
            store_route(guided_library, report, trace.completed_route)
            report["guidedPracticeStarts"] = store_route_starts(
                work, report, TRAJECTORY_FIELDS, trajectory, args.max_steps)
        if general and role == "PPO candidate" and intervention is None and reference is not None:
            starts = store_route_starts(work, report, TRAJECTORY_FIELDS, trajectory, args.max_steps,
                                        baseline=reference)
            if starts:
                report["learnerPracticeStarts"] = starts
        if trace.wall_strikes:
            save_wall_strikes(work, seed, trace.wall_strikes)
            report["observedWallOpenings"] = len(trace.wall_strikes)
        entry = dict(role=role, domain=args.domain, finishedAt=datetime.now(timezone.utc).isoformat(),
                     wallSeconds=round(time.monotonic() - started, 2), trainerSHA256=trainer_source,
                     round=state["round"], champion=state["champion"], **report)
        with (work / "episodes.jsonl").open("a") as log:
            log.write(json.dumps(entry) + "\n")
        print(json.dumps(entry), flush=True)
        return report, trace

    def ppo_round(incumbent, incumbent_path, number):
        verify_environment()
        env.training_render_hz = args.training_render_hz
        previous = work / state["learner"] if state.get("learner") else incumbent_path / "model.zip"
        if state.get("learnerSHA256") and sha(previous) != state["learnerSHA256"]:
            raise ValueError("The private PPO learner has changed since its last comparison")
        pending = state.get("pendingPPO", {})
        # An evaluator-only upgrade can retain the saved rollout with an explicit receipt.
        if (pending.get("round") == number and trainer_source in (pending.get("trainer"), pending.get("comparisonTrainer"))
                and (work / pending["filename"]).is_file() and sha(work / pending["filename"]) == pending["sha256"]):
            path = work / pending["filename"]
            training_seeds = pending["seeds"]
            attack_temperature = pending.get("attackTemperature", 1.0)
            jump_temperature = pending.get("jumpTemperature", 1.0)
            blocked_temperature = pending.get("blockedRecoveryTemperature", 1.0)
        else:
            rehearsed_parent = previous.name.startswith("learner-rehearsed-")
            jump_temperature = (args.jump_exploration_temperature
                                if rehearsed_parent or "jumpExplorationTemperature" not in state else 1.0)
            blocked_temperature = (args.jump_exploration_temperature if args.domain == "hacker"
                                   and "blockade_direction" in SCHEMAS[version] else 1.0)
            print(f"Training {args.domain} PPO batch {number + 1} for {args.training_steps} decisions", flush=True)
            path, training_seeds, attack_temperature, jump_temperature = train_batch(
                env, incumbent, previous, work, number, args.training_steps,
                attack_temperature=args.attack_exploration_temperature, jump_temperature=jump_temperature,
                blocked_recovery_temperature=blocked_temperature,
                initial_attack_exploration="attackExplorationTemperature" not in state)
            verify_environment()
            state["pendingPPO"] = dict(round=number, trainer=trainer_source, filename=path.name,
                                       sha256=sha(path), seeds=training_seeds, attackTemperature=attack_temperature,
                                       jumpTemperature=jump_temperature, blockedRecoveryTemperature=blocked_temperature)
            save_json(state_file, state)
        child = PPO.load(path, device="cpu")
        control_seed = 980_000 + number % 20_000
        prior, prior_trace = trial(incumbent, control_seed, "incumbent")
        result, candidate_trace = trial(child, control_seed, "PPO candidate", reference=prior)
        evidence = dict(method="ppo", base=base.name, ppoOrigin=identity["startingPolicy"],
                        parent=state["champion"], identity=identity, trainerSHA256=trainer_source,
                        fullUpdateTrainerSHA256=state["pendingPPO"]["trainer"],
                        trainingSeeds=training_seeds, learnerSourceSHA256=sha(previous), fullUpdateSHA256=sha(path),
                        trainingSteps=args.training_steps, attackTemperature=attack_temperature, jumpTemperature=jump_temperature,
                        blockedRecoveryTemperature=blocked_temperature,
                        incumbent=prior, candidate=result, matched=[])
        learner_path = path
        private_rehearsal = None
        wall = ppo_wall_rehearsal(work, result, candidate_trace.contacts, prior)
        if wall is not None:
            target, examples = wall
            repaired = learn_blockade_response(child, examples, SCHEMAS[version], expert_inputs)
            if repaired is not None:
                measured, repair_trace = trial(repaired, control_seed, "PPO candidate wall rehearsal")
                opened = verified_ppo_wall_passage(measured, target)
                evidence["candidateWallRepair"] = dict(target=target, decisions=len(examples), passed=opened, result=measured)
                if opened and measured["pathProgress"] > result["pathProgress"] + 2:
                    learner_path = work / f"learner-wall-repair-{number:04d}.zip"
                    staged = learner_path.with_suffix(".tmp")
                    repaired.save(staged)
                    staged.replace(learner_path)
                    evidence["candidateWallRepair"]["learnerSHA256"] = sha(learner_path)
                    child, result, candidate_trace = repaired, measured, repair_trace
                    evidence["selectedUpdate"] = "rehearsal"
        combat = measured_combat_repair(work, result, prior)
        if combat:
            repaired = learn_maneuver(child, combat, candidate_trace.anchors, expert_inputs, heads=(0, 1, 2, 3))
            measured, repair_trace = trial(repaired, control_seed, "PPO candidate combat rehearsal")
            retained = preserves_ppo_combat_repair(measured, result)
            repair = dict(decisions=len(combat), measured=measured, retained=retained)
            evidence["candidateCombatRepair"] = repair
            if retained:
                learner_path = work / f"learner-rehearsed-combat-{number:04d}.zip"
                staged = learner_path.with_suffix(".tmp")
                repaired.save(staged)
                staged.replace(learner_path)
                repair["learnerSHA256"] = sha(learner_path)
                child, result, candidate_trace = repaired, measured, repair_trace
                evidence["selectedUpdate"] = "rehearsal"
        short_jump = result.get("missedJumpHold")
        takeoff = result.get("missedJumpRearm") or short_jump or result.get("missedTakeoff")
        if ppo_graph is not None and takeoff and result["reason"] == "stalled":
            landings, shelves = measured_takeoff_lesson(work, takeoff, ppo_graph)
            if landings and shelves is not None:
                heads = (2,) if takeoff is short_jump else (0, 2)
                repaired = learn_maneuver(child, landings, candidate_trace.anchors, expert_inputs, heads=heads)
                measured, repair_trace = trial(repaired, control_seed, "PPO candidate takeoff rehearsal")
                landed = any((ppo_graph.support(x, y), ppo_graph.support(end_x, end_y)) == shelves
                             for x, y, end_x, end_y, _ in repair_trace.climbed)
                retained = landed and (general_improvement(measured, result) or advances_learner_past_stall(measured, result))
                repair = dict(decisions=len(landings), takeoff=takeoff, heads=heads,
                              landed=landed, result=measured, retained=retained)
                evidence["candidateTakeoffRepair"] = repair
                if retained:
                    learner_path = work / f"learner-rehearsed-takeoff-{number:04d}.zip"
                    staged = learner_path.with_suffix(".tmp")
                    repaired.save(staged)
                    staged.replace(learner_path)
                    repair["learnerSHA256"] = sha(learner_path)
                    child, result, candidate_trace = repaired, measured, repair_trace
                    evidence["selectedUpdate"] = "rehearsal"
        hit = result.get("first_damage") or {}
        if (not general_improvement(result, prior) and hit.get("approachingVisibleEnemy")
                and result["pathProgress"] > prior["pathProgress"] + 3):
            lesson, heads = damage_lesson(hit, 6, 10)
            coached, demonstration = trial(child, control_seed, "PPO approach practice", lesson, reference=result)
            evidence["approachPractice"] = coached
            if coached.get("protectedApproach") and demonstration.maneuver:
                practiced = learn_maneuver(child, demonstration.maneuver, demonstration.anchors, expert_inputs, heads=heads)
                measured, _ = trial(practiced, control_seed, "PPO rehearsed approach")
                evidence["rehearsedApproach"] = measured
                if general_improvement(measured, prior):
                    child, result = practiced, measured
                    evidence["selectedUpdate"] = "rehearsal"
        if not general_improvement(result, prior):
            lesson = measured_ppo_lesson(work, prior, ppo_graph)
            if lesson is None:
                probe = ppo_barrier_probe(work, prior, SCHEMAS[version])
                if probe is not None:
                    measured, _ = trial(incumbent, control_seed, "training barrier probe", probe, reference=prior)
                    evidence["barrierProbe"] = dict(target=probe[-1], result=measured)
                    lesson = measured_ppo_lesson(work, prior, ppo_graph)
            if lesson is not None:
                role, examples, heads = lesson
                climb_fit = (learn_climb_response(incumbent, examples, SCHEMAS[version], expert_inputs)
                             if role == "landing" else None)
                practiced = climb_fit if climb_fit is not None else learn_maneuver(
                    incumbent, examples, prior_trace.anchors, expert_inputs, heads=heads)
                measured, practice_trace = trial(practiced, control_seed, "PPO rehearsal candidate")
                evidence["rehearsal"] = dict(skill=role, decisions=len(examples), result=measured)
                if climb_fit is not None:
                    evidence["rehearsal"]["climbFit"] = "marked ascent"
                    if not general_improvement(measured, prior) and measured["pathProgress"] <= prior["pathProgress"] + .25:
                        broader = learn_maneuver(incumbent, examples, prior_trace.anchors, expert_inputs, heads=heads)
                        alternate, alternate_trace = trial(broader, control_seed, "PPO climb rehearsal with steering")
                        evidence["rehearsal"]["steeringFollowup"] = alternate
                        if general_improvement(alternate, prior) or alternate["pathProgress"] > measured["pathProgress"]:
                            practiced, measured, practice_trace = broader, alternate, alternate_trace
                if (role != "combat" and not general_improvement(measured, prior)
                        and measured["damage"] > prior["damage"]
                        and measured.get("pathProgress", 0) >= prior.get("pathProgress", 0) + 3):
                    combat = nearby_combat(work, measured.get("first_damage"))
                    if combat:
                        supported = learn_maneuver(practiced, combat, practice_trace.anchors, expert_inputs, heads=(0, 1, 2, 3))
                        followup, _ = trial(supported, control_seed, "PPO rehearsal combat follow-up")
                        evidence["rehearsal"]["combatFollowup"] = dict(decisions=len(combat), result=followup)
                        if general_improvement(followup, prior):
                            practiced, measured = supported, followup
                if general_improvement(measured, prior):
                    child, result = practiced, measured
                    evidence["selectedUpdate"] = "rehearsal"
                elif advances_learner_past_stall(measured, prior):
                    private_rehearsal = practiced, measured
        if not general_improvement(result, prior):
            if incumbent.policy.net_arch == child.policy.net_arch:
                # Select the conservative step only on a training split comparison.
                smaller = interpolate(incumbent, child, 0.25)
                measured, _ = trial(smaller, control_seed, "quarter PPO update")
                evidence["quarterUpdate"] = measured
                if general_improvement(measured, prior):
                    child, result = smaller, measured
                    evidence["selectedUpdateFraction"] = 0.25
            else:
                # Rehearsal can widen the actor. Its raw update is still a valid learner.
                evidence["quarterUpdateSkipped"] = "policy architectures differ"
        continuation = None
        if general_improvement(result, prior):
            victories = 0
            for idx, held in enumerate(reserve_holdouts(shared, args.domain)):
                control, _ = trial(incumbent, held, "held-out incumbent")
                tested, _ = trial(child, held, "held-out PPO candidate")
                evidence["matched"].append([control, tested])
                if not preserves_general_outcomes(tested, control):
                    break
                victories += int(general_improvement(tested, control))
                if victories == 2:
                    incumbent_path = publish(child, base, work, models, dataset, evidence, args.vision,
                                             parent=incumbent_path)
                    incumbent, state["champion"] = child, incumbent_path.name
                    print(f"Verified {args.domain} winner {incumbent_path}", flush=True)
                    break
                if victories + 2 - idx < 2:
                    break
            # A verified training comparison can advance learning. Playback still needs held-out wins.
            continuation = child, result
        elif private_rehearsal is not None:
            continuation = private_rehearsal
            evidence["privateRehearsal"] = continuation[1]
        if continuation is not None:
            learner, measured = continuation
            compared_path = retain_measured_ppo_variant(learner, work, number, learner_path, evidence)
            evidence.setdefault("continuationLearner", dict(filename=compared_path.name, sha256=sha(compared_path)))
            if compared_path != learner_path:
                with gzip.open(work / measured["trajectory"], "rt") as capture:
                    measured_route = json.load(capture)
                starts = store_route_starts(work, measured, measured_route["fields"], measured_route["samples"],
                                            args.max_steps, baseline=prior)
                if starts:
                    measured["learnerPracticeStarts"] = starts
                learner_path = compared_path
        elif learner_path == path and not preserves_general_outcomes(result, prior):
            previous_measured = last_measured_ppo_learner(work, number, identity)
            if previous_measured is not None:
                restored = PPO.load(previous_measured, device="cpu")
                measured, _ = trial(restored, control_seed, "previous measured PPO learner")
                retained = general_improvement(measured, result) or advances_learner_past_stall(measured, result)
                evidence["measuredLearnerComparison"] = dict(filename=previous_measured.name,
                                                            sha256=sha(previous_measured), result=measured,
                                                            retained=retained)
                if retained:
                    learner_path = previous_measured
        save_json(work / f"comparison-{number:04d}.json", evidence)
        state.pop("pendingPPO", None)
        if attack_temperature > 1:
            state["attackExplorationTemperature"] = attack_temperature
        if jump_temperature > 1:
            state["jumpExplorationTemperature"] = jump_temperature
        state.update(round=number + 1, learner=learner_path.name, learnerSHA256=sha(learner_path))
        save_json(state_file, state)
        return incumbent, incumbent_path

    start_round = state["round"]
    try:
        while time.monotonic() < limit and (args.rounds is None or state["round"] < start_round + args.rounds):
            number = state["round"]
            seed = 100_000 + number
            if general and not args.baseline_only:
                model, checkpoint = ppo_round(model, checkpoint, number)
                continue
            prior, traces = trial(model, seed, "incumbent")
            if args.baseline_only:
                break
            teacher = None
            require_crossing = False
            resume = interrupted_proposal(work, state["champion"], prior)
            if supplied or resume:
                candidate = PPO.load(supplied or resume, device="cpu")
                if (candidate.observation_space.shape != model.observation_space.shape or
                        not np.array_equal(candidate.action_space.nvec, model.action_space.nvec)):
                    raise ValueError("The proposed policy's observation or action contract differs from its parent")
                require_crossing = True
            elif prior.get("nearbyCacheAtStall") and number % 3 != 1:
                cache = prior["nearbyCacheAtStall"]
                destination = cache["x"], prior["end"]["y"] + cache["dy"]
                teacher, demonstration = trial(model, seed, "mine observed cache", (0, 0, "mine observed cache", destination))
                if (teacher["depositHits"] > prior["depositHits"] and wins(teacher, prior) and demonstration.maneuver):
                    candidate = learn_maneuver(model, demonstration.maneuver, demonstration.anchors, expert_inputs, heads=(0, 3))
                else:
                    candidate = propose(model, traces, number)
            elif prior.get("first_damage") and number % 2 == 0:
                lesson, heads = damage_lesson(prior["first_damage"], 6, 10)
                role = lesson[2] if heads == (1, 3) else "jump before damage"
                teacher, demonstration = trial(model, seed, role, lesson, reference=prior)
                if (wins(teacher, prior) or teacher.get("protectedLanding") or teacher.get("protectedApproach")) and demonstration.maneuver:
                    examples = (demonstration.maneuver if heads == (1, 3) else
                                rehearse_crossings(work, prior, teacher, demonstration.maneuver))
                    candidate = learn_maneuver(model, examples, demonstration.anchors, expert_inputs, heads=heads)
                else:
                    candidate = propose(model, traces, number)
            elif prior["reason"] == "stalled":
                at_left_end = prior["rightward_distance"] < 2 and prior["openingLeftDistance"] >= 4
                jump = number % 3 != 0 and not at_left_end
                contact = prior.get("obstructionStep")
                onset = max(0, prior["frontier_step"] + (-5 if jump else 10))
                if contact is not None and not jump:
                    onset = contact + 2
                duration = 36 if at_left_end or jump else 16
                maneuver = "running jump" if jump else "nail"
                if number % 4 == 2:
                    maneuver, jump = "frontier rescue", True
                elif number % 4 == 3:
                    maneuver, jump = "platform rescue", True
                tail = prior.get("frontierActions", {})
                target = ()
                if ("blockade_direction" in SCHEMAS[version] and number % 3 != 2 and tail.get("nailPresses", 0) <= 1
                        and tail.get("blockedLeft", 0) + tail.get("blockedRight", 0) >= 25):
                    ledge = prior.get("reachablePlatformAtStall")
                    if ledge and (abs(ledge["x"] - prior["end"]["x"]) <= 6 or number % 4 == 3):
                        maneuver, jump = "platform rescue", True
                        target = ((prior["end"]["x"], prior["end"]["y"]),)
                    else:
                        maneuver, jump = "strike marked barriers", False
                route_source = snapshot if args.domain == "hacker" else labels
                # The authored route supplies training controls only. The visual
                # student and every unassisted Gameplay trial still receive only pixels.
                practice_route = (args.domain == "hacker" and number % 4 in (2, 3) or
                                  args.domain == "gameplay" and number >= 4 and number % 4 == 0)
                blocked = tail.get("blockedLeft", 0) + tail.get("blockedRight", 0)
                practice_pit = blocked >= 12 and number % 3 != 0
                if (practice_route or practice_pit) and route_source.is_file():
                    if climbing_graph is None:
                        try:
                            climbing_graph = AuthoredRoute.load(route_source)
                        except (OSError, ValueError, KeyError, TypeError) as error:
                            print(f"Skipping unavailable authored route for this lesson: {error}", flush=True)
                    lip = next((lip for lip in climbing_graph.pit_exits if
                                lip.at_wall(prior["end"]["x"], prior["end"]["y"]) and
                                tail.get("blockedRight" if lip.direction > 0 else "blockedLeft", 0) >= 12),
                               None) if climbing_graph and practice_pit else None
                    if lip:
                        maneuver, jump, target = "escape authored pit", True, (lip,)
                    elif practice_route and climbing_graph and climbing_graph.surfaces:
                        maneuver, jump = "authored climbing route", True
                        target = (climbing_graph,)
                        if len(route_files(guided_library)) >= 2:
                            target += ((prior["end"]["x"], prior["end"]["y"]),)
                if maneuver not in ("escape authored pit", "authored climbing route") and number % 3 == 1:
                    if routes is None:
                        with np.load(dataset, allow_pickle=False) as archive:
                            routes = training_routes(args.support, archive, manifest["trainingSessions"])
                    if routes:
                        maneuver, jump = "human recovery", True
                        target = (routes, (prior["end"]["x"], prior["end"]["y"]))
                teacher, demonstration = trial(model, seed, maneuver, (onset, duration, maneuver, *target), reference=prior)
                hit = teacher["first_damage"]
                if (maneuver not in ("frontier rescue", "platform rescue", "human recovery", "authored climbing route", "escape authored pit") and hit and teacher["damage"] > prior["damage"] and
                        teacher["rightward_distance"] >= prior["rightward_distance"] + 4 and
                        (maneuver == "strike marked barriers" or onset <= hit["step"] <= onset + duration + 60)):
                    avoided, avoidance = trial(model, seed, "nail and avoid contact",
                        (onset, duration, maneuver, "avoid enemies"), reference=prior)
                    if (wins(avoided, prior, require_crossing=True) or "unharmedCrossingStep" not in teacher or
                            ("unharmedCrossingStep" in avoided and avoided["unharmedDistance"] >= teacher["unharmedDistance"])):
                        teacher, demonstration, jump = avoided, avoidance, True
                if teacher.get("pitExit") and demonstration.maneuver:
                    require_crossing = True
                    candidate = learn_maneuver(model, demonstration.maneuver, demonstration.anchors, expert_inputs, heads=(2,))
                elif wins(teacher, prior, require_crossing=True) and demonstration.maneuver:
                    require_crossing = True
                    examples = rehearse_crossings(work, prior, teacher, demonstration.maneuver)
                    heads = (3,) if maneuver == "strike marked barriers" else (0, 3)
                    if jump or len(examples) > len(demonstration.maneuver):
                        heads = (0, 2, 3)
                    candidate = learn_maneuver(model, examples, demonstration.anchors, expert_inputs, heads=heads)
                elif teacher.get("unharmedCrossingStep") is not None and demonstration.maneuver:
                    require_crossing = True
                    heads = (3,) if maneuver == "strike marked barriers" and not jump else (0, 2, 3)
                    examples = rehearse_crossings(work, prior, teacher, demonstration.maneuver, safe_prefix=True)
                    candidate = learn_maneuver(model, examples, demonstration.anchors, expert_inputs, heads=heads)
                else:
                    candidate = propose(model, traces, number)
            else:
                candidate = propose(model, traces, number)
            role = "measured training proposal" if supplied else "resumed proposal" if resume else "proposal"
            result, primary_trace = trial(candidate, seed, role)
            evidence = dict(base=base.name, parent=state["champion"], matched=[], proposal=[prior, result],
                            testedManeuver=teacher, environment=identity, trainerSHA256=trainer_source)
            if supplied:
                evidence["candidateTrainingReceipt"] = read(args.candidate)
            elif resume:
                evidence["resumedCheckpointSHA256"] = sha(resume)
            if not wins(result, prior, require_crossing=require_crossing):
                demonstrations = route_files(guided_library)
                # Try the full route once per set of demonstrations and parent. Later
                # attempts rehearse the part where this particular policy got stuck.
                key = [state["champion"], *sorted(p.stem for p in demonstrations)]
                local = state.get("lastFullRoute") == key
                samples = route_examples(demonstrations, prior["end"] if local else None)
                if samples and (not local or number % 2 == 0):
                    protected = traces.anchors if local else []
                    student = learn_maneuver(model, samples, protected, expert_inputs, heads=(0, 1, 2, 3))
                    measured, student_trace = trial(student, seed, "rehearse completed route" if not local else "rehearse route obstacle")
                    evidence["routeRehearsal"] = dict(seeds=[int(p.stem) for p in demonstrations], localized=local, result=measured)
                    state["lastFullRoute"] = key
                    save_exploration_branch(student, measured, prior, state["champion"], work)
                    if wins(measured, prior, require_crossing=True):
                        candidate, result, primary_trace, require_crossing = student, measured, student_trace, True
            if (teacher and wins(teacher, prior, require_crossing=True) and result["reason"] == "stalled"
                    and not wins(result, prior, require_crossing=require_crossing)
                    and result["rightward_distance"] + 4 <= teacher["rightward_distance"]):
                # Rehearse the control where the learner actually gets stuck. Its approach and
                # input history can differ from the original demonstration even on a matched seed.
                corrected, visited = trial(candidate, seed, "correct learner stall", (0, 0, "frontier rescue"), reference=result)
                evidence["learnerCorrection"] = corrected
                if (wins(corrected, result, require_crossing=True) or "unharmedCrossingStep" in corrected) and visited.maneuver:
                    learned = learn_maneuver(candidate, visited.maneuver, visited.anchors, expert_inputs, heads=(0, 2, 3))
                    measured, measured_trace = trial(learned, seed, "learned stall correction")
                    evidence["correctedProposal"] = measured
                    if wins(measured, result) or wins(measured, prior, require_crossing=require_crossing):
                        candidate, result, primary_trace = learned, measured, measured_trace
            save_exploration_branch(candidate, result, prior, state["champion"], work)
            tail = prior.get("frontierActions", {})
            if (tail.get("blockedLeft", 0) + tail.get("blockedRight", 0) >= 25
                    and tail.get("nailPresses", 0) <= 1 and not wins(result, prior, require_crossing=require_crossing)):
                strikes = measured_strikes(work, SCHEMAS[version])
                openings = repeated_openings(shared, identity)
                contacts = stalled_strikes(traces.contacts, openings)
                route_edges = route_strike_edges(route_files(guided_library), openings, SCHEMAS[version])
                if contacts:
                    strikes += human_edges + contacts + route_edges
                    evidence["matchedBreakableContacts"] = len(contacts)
                    evidence["confirmedRouteStrikeExamples"] = len(route_edges)
                elif args.domain == "hacker":
                    strikes = []
                focused = learn_blockade_response(model, strikes, SCHEMAS[version], expert_inputs) if strikes else None
                if focused is not None:
                    measured, measured_trace = trial(focused, seed, "learned blockade response")
                    evidence["blockadeResponse"] = measured
                    save_exploration_branch(focused, measured, prior, state["champion"], work)
                    if wins(measured, prior, require_crossing=True):
                        candidate, result, primary_trace, require_crossing = focused, measured, measured_trace, True
            if teacher and demonstration.first_crossing and not wins(result, prior, require_crossing=require_crossing):
                local, protected = demonstration.first_crossing
                predicted, _ = model.predict(np.stack([obs for obs, _ in local]), deterministic=True)
                desired = np.array([action for _, action in local])
                heads = tuple(h for h in range(len(ACTION_SIZES)) if np.any(predicted[:, h] != desired[:, h]))
                if heads:
                    focused = learn_maneuver(model, local, protected, expert_inputs, heads=heads)
                    measured, measured_trace = trial(focused, seed, "first crossing only")
                    evidence["firstCrossing"] = measured
                    if wins(measured, prior, require_crossing=True):
                        candidate, result, require_crossing = focused, measured, True
                        primary_trace = measured_trace
            if (not wins(result, prior, require_crossing=require_crossing) and model.policy.net_arch == candidate.policy.net_arch
                    and result["damage"] > prior["damage"]
                    and result["rightward_distance"] >= prior["rightward_distance"] + 4):
                # A useful but costly change can be a search direction without becoming the champion.
                lower, upper = 0., 1.
                for _ in range(3 if "firstCrossing" in evidence else 4):
                    fraction = (lower + upper) / 2
                    cautious = interpolate(model, candidate, fraction)
                    measured, measured_trace = trial(cautious, seed, f"partial update {fraction:g}")
                    evidence.setdefault("partialUpdates", []).append(measured)
                    if wins(measured, prior, require_crossing=require_crossing):
                        candidate, result, primary_trace = cautious, measured, measured_trace
                        break
                    if measured["damage"] > prior["damage"] or measured["reason"] == "death":
                        upper = fraction
                    else:
                        lower = fraction
                else:
                    hit = result.get("first_damage")
                    if hit and hit["step"] >= 6:
                        lesson, heads = damage_lesson(hit, 6, 10)
                        escape, trace = trial(candidate, seed, "damage recovery", lesson, reference=result)
                        evidence["damageRecovery"] = escape
                        if (wins(escape, prior, require_crossing=require_crossing) or escape.get("protectedLanding") or escape.get("protectedApproach") or
                            heads == (1, 3) and escape["damage"] < result["damage"] and
                            escape["rightward_distance"] + 2 >= result["rightward_distance"]) and trace.maneuver:
                            repaired = learn_maneuver(candidate, trace.maneuver, trace.anchors, expert_inputs, heads=heads)
                            measured, repaired_trace = trial(repaired, seed, "learned damage recovery")
                            if wins(measured, result) or wins(measured, prior, require_crossing=require_crossing):
                                candidate, result, primary_trace = repaired, measured, repaired_trace
            if (result["damage"] > prior["damage"] and result["rightward_distance"] >= prior["rightward_distance"] + 4
                    and (examples := nearby_combat(work, result.get("first_damage"), previous_practice))):
                safer = learn_maneuver(candidate, examples, primary_trace.anchors, expert_inputs, heads=(0, 2, 3))
                measured, measured_trace = trial(safer, seed, "rehearse confirmed kills")
                evidence["combatRehearsal"] = measured
                save_exploration_branch(safer, measured, prior, state["champion"], work)
                if wins(measured, prior, require_crossing=require_crossing):
                    candidate, result, primary_trace = safer, measured, measured_trace
            if (prior["reason"] == "stalled" and not wins(result, prior, require_crossing=require_crossing)
                    and (examples := nearby_landings(work, prior["end"], previous_practice))):
                climbing = learn_maneuver(model, examples, traces.anchors, expert_inputs, heads=(0, 2, 3))
                measured, measured_trace = trial(climbing, seed, "rehearse clean landings")
                evidence["landingRehearsal"] = measured
                save_exploration_branch(climbing, measured, prior, state["champion"], work)
                if wins(measured, prior):
                    candidate, result, primary_trace, require_crossing = climbing, measured, measured_trace, False
            if number % 3 == 2 and not wins(result, prior, require_crossing=require_crossing):
                branch = exploration_checkpoint(work, state["champion"], identity, prefer_unharmed=number % 6 == 2)
                if branch is not None:
                    path, digest = branch
                    explorer = PPO.load(path, device="cpu")
                    evidence["explorationSource"] = {"checkpointSHA256": digest, "environment": path.parent.name}
                    run, run_trace = trial(explorer, seed, "retest exploration branch")
                    evidence["explorationBranch"] = run
                    if wins(run, prior, require_crossing=True):
                        candidate, result, require_crossing = explorer, run, True
                        primary_trace = run_trace
                    elif run.get("first_damage") or run["reason"] == "stalled":
                        hit = run.get("first_damage")
                        practice = (max(0, hit["step"] - (4, 6, 8)[number // 3 % 3]), 12, "jump", "avoid enemies") if hit else (
                            max(0, run["frontier_step"] - 3), 25, "running jump")
                        avoided, trace = trial(explorer, seed, "practice exploration branch", practice)
                        evidence["branchRecovery"] = avoided
                        if wins(avoided, prior, require_crossing=True) and trace.maneuver:
                            examples = rehearse_crossings(work, run, avoided, trace.maneuver)
                            learned = learn_maneuver(explorer, examples, trace.anchors, expert_inputs, heads=(0, 2, 3))
                            measured, measured_trace = trial(learned, seed, "learned exploration branch")
                            save_exploration_branch(learned, measured, prior, state["champion"], work)
                            candidate, result, require_crossing = learned, measured, True
                            primary_trace = measured_trace
            if number % 3 == 1 and not wins(result, prior, require_crossing=require_crossing):
                sampled, practice = trial(model, seed, "sample frontier controls", (0, 0, "sample policy"))
                evidence["sampledFrontier"] = sampled
                improved = (learn_from_returns(model, practice.sampled, work, state["champion"], seed, expert_inputs,
                                               practice.anchors) if practice.sampled is not None else None)
                if improved is not None:
                    measured, returned_trace = trial(improved, seed, "policy gradient proposal")
                    evidence["policyGradient"] = measured
                    save_exploration_branch(improved, measured, prior, state["champion"], work)
                    if wins(measured, prior):
                        candidate, result, primary_trace, require_crossing = improved, measured, returned_trace, False
            qualified = wins(result, prior, require_crossing=require_crossing)
            if qualified and (args.domain == "gameplay" and result["rightward_distance"] >= 45 or
                              result["rightward_distance"] >= prior["rightward_distance"] + 12):
                candidate, result, qualified = practice_another_start(
                    trial, model, candidate, prior, result, primary_trace, number, expert_inputs, require_crossing, evidence)
                if not qualified:
                    save_exploration_branch(candidate, result, prior, state["champion"], work, needs_generalization=True)
            if qualified:
                evidence["acceptedProposal"] = result
                held_wins = 0
                # Two independent improvements are required. An exact tie at the same
                # obstacle can use a third pair; damage and exploration regressions reject.
                for offset, held_seed in enumerate(reserve_holdouts(shared, args.domain)):
                    control, _ = trial(model, held_seed, "held-out incumbent")
                    challenger, _ = trial(candidate, held_seed, "held-out candidate")
                    evidence["matched"].append([control, challenger])
                    if not holds_progress(challenger, control):
                        break
                    held_wins += int(wins(challenger, control, require_crossing=require_crossing))
                    if held_wins == 2 or held_wins + (2 - offset) < 2:
                        break
                if held_wins == 2:
                    checkpoint = publish(candidate, base, work, models, dataset, evidence, args.vision,
                                         parent=checkpoint)
                    model = candidate
                    state["champion"] = checkpoint.name
                    print(f"Verified {args.domain} winner {checkpoint}", flush=True)
                else:
                    save_exploration_branch(candidate, result, prior, state["champion"], work, needs_generalization=True)
            state["round"] += 1
            save_json(state_file, state)
    except TimeoutError:
        if time.monotonic() < limit:
            raise
        print("Time budget reached. The last fully verified champion is available.", flush=True)
    finally:
        env.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--exit-path-objective", nargs=2, type=float, metavar=("X", "Y"),
                        help="Fixed Tutorial_01 exit objective in world coordinates")
    parser.add_argument("--king-pass-benchmark", action="store_true",
                        help="Compare policies by verified arrival in playable Dirtmouth without altering Gym rewards.")
    parser.add_argument("--domain", choices=("hacker", "gameplay"), default="hacker")
    parser.add_argument("--game", type=Path, required=True, help="Installed Hollow Knight executable")
    parser.add_argument("--vision", type=Path, default=VISION)
    parser.add_argument("--support", type=Path, default=SUPPORT)
    parser.add_argument("--base", type=Path)
    parser.add_argument("--method", choices=("ppo", "guided"), help="Train from game rewards (default), or run archived guided experiments.")
    parser.add_argument("--training-steps", type=int, default=512, help="PPO learner decisions between autonomous comparisons.")
    parser.add_argument("--attack-exploration-temperature", type=float, default=1.0,
                        help="Soften release sampling during close combat if held Attack rarely releases.")
    parser.add_argument("--jump-exploration-temperature", type=float, default=1.0,
                        help="Soften initial Jump sampling and blocked-ledge recovery sampling during Hacker PPO.")

    parser.add_argument("--candidate", type=Path,
                        help="Try a measured training checkpoint and its JSON receipt as the first proposal of one round.")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--hours", type=float, default=8)
    parser.add_argument("--rounds", type=int)
    parser.add_argument("--max-steps", type=int, default=1200)
    parser.add_argument("--stall-steps", type=int, default=100)
    parser.add_argument("--training-render-hz", type=int, choices=(60, 120), default=120,
                        help="Wall-clock frame rate for assisted training runs. Autonomous comparisons run at 60 Hz.")
    parser.add_argument("--baseline-only", action="store_true")
    parser.add_argument("--wait-for-worker", action="store_true",
                        help="Queue this comparison behind the current Gym worker within the training time budget.")
    args = parser.parse_args()
    args.game, args.vision, args.support = (path.resolve() for path in (args.game, args.vision, args.support))
    if not 0 < args.hours <= 168 or min(args.max_steps, args.stall_steps, 1 if args.rounds is None else args.rounds) < 1:
        parser.error("Use positive durations and limits")
    if args.training_steps < 128 or args.training_steps > 16384 or args.training_steps % 128:
        parser.error("Use 128 to 16384 training steps in multiples of 128")
    if not all(1 <= value <= 4 for value in (args.attack_exploration_temperature, args.jump_exploration_temperature)):
        parser.error("Use exploration temperatures between 1 and 4")
    if args.method is None:
        args.method = "guided" if args.candidate else "ppo"
    if args.candidate and (args.method != "guided" or args.baseline_only or args.rounds != 1):
        parser.error("Use --candidate with --method guided and --rounds 1")
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(KeyboardInterrupt()))
    try:
        run(args)
    except KeyboardInterrupt:
        print("Stopped. Completed comparisons and verified checkpoints are saved.", flush=True)
    except BlockingIOError:
        parser.exit(1, "A Hacker Gym worker is already running.\n")


if __name__ == "__main__":
    main()
