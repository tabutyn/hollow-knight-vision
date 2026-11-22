#!/usr/bin/env python3
"""Validate Vision demonstration sessions and export behavior-cloning examples.

Inspect uses only the standard library. Export additionally needs NumPy. Times
used for action alignment are host monotonic availableAt and input timestamps;
ScreenCaptureKit presentation timestamps are retained as separate provenance.
"""
from __future__ import annotations

from array import array
import argparse
import bisect
import base64
from collections import Counter, deque
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import uuid

SCHEMA_VERSION = 14
GROUND_CONTACT_POLICY_VERSION = 15
LANDING_GEOMETRY_POLICY_VERSION = 16
HISTORY_LENGTH = 4
ACTION_SIZES = [3, 3, 2, 2]
HELD_ATTACK_COST = 0.05
SUPPORTED = {"left", "right", "up", "down", "z", "x"}
RECOVERY_START_WINDOW = 1.2
BASE_FEATURES = [
    "knight_x", "knight_y", "knight_width", "knight_height", "knight_confidence",
    "detection_age", "terrain_valid", "capture_age", "room_changed",
    "previous_horizontal", "previous_aim", "previous_jump", "previous_attack",
    "target_x", "target_y", "target_valid",
]
LEGACY_FEATURE_NAMES = BASE_FEATURES + [
    f"terrain_{i}_{field}" for i in range(16)
    for field in ("start_x", "end_x", "y", "confidence", "present")
] + [
    f"enemy_{i}_{field}" for i in range(8)
    for field in ("x", "y", "width", "height", "confidence", "present")
]

WALL_FEATURE_NAMES = ["walls_analyzed"] + [
    f"wall_{side}_{field}" for side in ("left", "right")
    for field in ("x", "bottom_y", "top_y", "solid_side", "present")
]
RECORDING_FEATURE_NAMES = LEGACY_FEATURE_NAMES + WALL_FEATURE_NAMES
GRID_COLUMNS, GRID_ROWS = 24, 14
GRID_CELLS = GRID_COLUMNS * GRID_ROWS
FLOOR_GRID_START = len(RECORDING_FEATURE_NAMES)
WALL_GRID_START = FLOOR_GRID_START + GRID_CELLS
SCREEN_FEATURE_NAMES = RECORDING_FEATURE_NAMES + [
    f"screen_{kind}_{i}" for kind in ("floor", "wall") for i in range(GRID_CELLS)
]
BLOCKADE_FEATURE_NAMES = SCREEN_FEATURE_NAMES.copy()
BLOCKADE_FEATURE_NAMES[13:16] = ["blockade_dx", "blockade_dy", "blockade_direction"]
GEO_START = len(BLOCKADE_FEATURE_NAMES)
GEO_FEATURE_NAMES = BLOCKADE_FEATURE_NAMES + ["geo_cache_dx", "geo_cache_dy", "geo_cache_present"]
FRONTIER_NAMES = GEO_FEATURE_NAMES + ["atlas_present", "atlas_left", "atlas_right", "health_known", "health_fraction"]
ATLAS_MAP_NAMES = FRONTIER_NAMES + ["atlas_grid_present"] + [f"atlas_missing_{i}" for i in range(15)]
ROAD_NAMES = ["road_present", "road_dx", "road_dy", "road_clearance", "road_distance", "road_frontier"]
ROAD_FEATURE_NAMES = ATLAS_MAP_NAMES + ROAD_NAMES
PICKUP_NAMES = ROAD_FEATURE_NAMES + ["joni_blessing_dx", "joni_blessing_dy", "joni_blessing_present"]
COCOON_NAMES = PICKUP_NAMES + ["lifeblood_cocoon_dx", "lifeblood_cocoon_dy", "lifeblood_cocoon_present"]
DOOR_FEATURE_NAMES = COCOON_NAMES + [f"screen_door_{i}" for i in range(GRID_CELLS)]
SHARED_PATH_NAMES = DOOR_FEATURE_NAMES + ["shared_path_present", "shared_path_dx", "shared_path_dy"]
from compact_markings import LANDING_NAMES, LEGACY_NAMES as LEGACY_MARKING_NAMES, NAMES as MARKING_NAMES
from compact_markings import encode as encode_markings, landing_features, recover_recorded_spikes
COMPACT_SCALAR_INDICES = [i for i, name in enumerate(SHARED_PATH_NAMES)
    if not (name.startswith("terrain_") and name != "terrain_valid")
    and not name.startswith("wall_") and name != "walls_analyzed"
    and not name.startswith("screen_") and not name.startswith("joni_blessing_")]
FEATURE_NAMES = [SHARED_PATH_NAMES[i] for i in COMPACT_SCALAR_INDICES] + MARKING_NAMES
ENEMY_START = FEATURE_NAMES.index("enemy_0_x")
MARKING_START = FEATURE_NAMES.index("marking_0_start_dx")
FEATURE_COUNT = len(FEATURE_NAMES)
RECORDING_SCHEMAS = {1: LEGACY_FEATURE_NAMES, 2: RECORDING_FEATURE_NAMES}
SCHEMAS = {**RECORDING_SCHEMAS, 3: SCREEN_FEATURE_NAMES, 4: BLOCKADE_FEATURE_NAMES, 5: GEO_FEATURE_NAMES, 6: FRONTIER_NAMES, 7: ATLAS_MAP_NAMES, 8: ROAD_FEATURE_NAMES, 9: PICKUP_NAMES, 10: COCOON_NAMES, 11: DOOR_FEATURE_NAMES, 12: SHARED_PATH_NAMES, 13: [SHARED_PATH_NAMES[i] for i in COMPACT_SCALAR_INDICES] + LEGACY_MARKING_NAMES, SCHEMA_VERSION: FEATURE_NAMES}
SCHEMAS[GROUND_CONTACT_POLICY_VERSION] = FEATURE_NAMES + ["knight_ground_contact"]
SCHEMAS[LANDING_GEOMETRY_POLICY_VERSION] = SCHEMAS[GROUND_CONTACT_POLICY_VERSION] + LANDING_NAMES
COMPACT_POLICY_WIDTHS = frozenset(len(names) for names in SCHEMAS.values() if "marking_0_start_dx" in names)


def finite_number(value):
    return isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)


def atlas_features(view):
    if not view or not all(finite_number(view.get(key)) for key in ("left", "right")):
        return [0] * 21
    grid = view.get("missing")
    if grid is not None and (len(grid) != 15 or not all(finite_number(x) and 0 <= x <= 1 for x in grid)):
        raise ValueError("Invalid local Atlas coverage")
    health = view.get("health")
    return [1, min(1, max(0, view["left"])), min(1, max(0, view["right"])),
            int(health is not None), min(16, max(0, health)) / 16 if health is not None else 0] + ([1] + grid if grid is not None else [0] * 16)


def road_features(hint):
    if hint is None:
        return [0.0] * len(ROAD_NAMES)
    fields = [hint.get(k) for k in ("dx", "dy", "clearance", "distance")]
    if not all(finite_number(v) and lo <= v <= 1 for v, lo in zip(fields, [-1, -1, 0, 0])):
        raise ValueError("Invalid Atlas road hint")
    if not isinstance(hint.get("frontier"), bool):
        raise ValueError("Invalid Atlas road goal")
    return [1.0, *fields, float(hint["frontier"])]


def exploration_weights(rows, views, path_progress, path_alignment=None):
    """Weight expert decisions using subsequent path movement, damage and dwell."""
    changes = []
    if path_alignment is None:
        path_alignment = [None] * len(rows)
    previous_segment = anchor = confirmed_health = pending_health = None
    for row, view, progress, alignment in zip(rows, views, path_progress, path_alignment):
        row["outcomeWeight"] = 1.0
        if progress is not None and (not finite_number(progress) or not 0 <= progress <= 100):
            raise ValueError("Invalid Atlas path progress")
        if alignment is not None and (not finite_number(alignment) or abs(alignment) > 3):
            raise ValueError("Invalid shared path alignment")
        if row["segment"] != previous_segment:
            anchor = confirmed_health = pending_health = None
        previous_segment = row["segment"]
        if view is None and progress is None and alignment is None and not row.get("blueHealthKnown", False):
            anchor = confirmed_health = pending_health = None
            changes.append(None)
            continue
        time = row["timestamp"]
        # The value was measured from the previous observation to this one.
        # It is not inferred from the old pixel-road distance or Atlas coverage.
        reward = 4 * ((progress or 0) + 0.5 * (alignment or 0)) + row.get("blueHealthGained", 0)
        if view is not None:
            health = view.get("health")
            if health != pending_health:
                pending_health, health_since = health, time
            if health is not None and time - health_since >= 0.22:
                if confirmed_health is not None:
                    reward -= 32 * max(0, confirmed_health - health)
                confirmed_health = health
            features = row["observation"]
            width = len(features) // HISTORY_LENGTH
            x, y = features[-width:-width + 2] if features[-width + 4] else (0.5, 0.28)
            position = (view["camera"][0] + x * view["width"], view["camera"][1] + y * view["width"])
            if anchor is None or math.dist(position, anchor) >= 80:
                anchor, entered = position, time
            if time - entered >= 2:
                reward -= 0.12
        else:
            anchor = confirmed_health = pending_health = None
        changes.append(reward)
    for i, row in enumerate(rows):
        if changes[i] is None:
            continue
        outcome = 0
        for j in range(i + 1, len(rows)):
            if (changes[j] is None or rows[j]["segment"] != row["segment"]
                    or rows[j]["timestamp"] - row["timestamp"] > 1):
                break
            outcome += changes[j]
        row["outcomeWeight"] = 2 ** max(-1, min(1, outcome / 32))

def credit_enemy_outcomes(rows, outcomes):
    """Use changes in the receiver's confirmed nail damage/death counters as training labels."""
    previous = None
    start = 0
    for i, (row, current) in enumerate(zip(rows, outcomes)):
        row["enemyDamage"] = row["enemyKills"] = 0
        if current is not None and (not isinstance(current.get("epoch"), str) or not current["epoch"]
                or any(type(current.get(key)) is not int or current[key] < 0 for key in ("damage", "kills"))):
            raise ValueError("Invalid enemy outcome counters")
        if (previous is not None and current is not None and current["epoch"] == previous["epoch"]
                and rows[i - 1]["segment"] == row["segment"]
                and current["damage"] >= previous["damage"] and current["kills"] >= previous["kills"]):
            damage = current["damage"] - previous["damage"]
            kills = current["kills"] - previous["kills"]
            row["enemyDamage"], row["enemyKills"] = damage, kills
            if damage or kills:
                # Credit the demonstrated approach, aim and attack leading up to the outcome.
                # Never cross a gap, a missing measurement or the start of this receiver epoch.
                for j in range(i - 1, start - 1, -1):
                    if (rows[j]["segment"] != row["segment"] or not outcomes[j]
                            or outcomes[j]["epoch"] != current["epoch"] or row["timestamp"] - rows[j]["timestamp"] > 1):
                        break
                    rows[j]["outcomeWeight"] = min(4.0, rows[j]["outcomeWeight"] *
                                                  2 ** min(2, (damage + 32 * kills) / 32))
        else:
            start = i
        previous = current


def credit_blue_health_outcomes(rows, outcomes):
    """Attach measured per-observation Lifeblood gains without crossing boundaries."""
    previous = None
    for i, (row, current) in enumerate(zip(rows, outcomes)):
        row["blueHealthGained"] = 0
        row["blueHealthKnown"] = current is not None
        if current is not None and (not isinstance(current, dict) or not isinstance(current.get("epoch"), str)
                                    or not current["epoch"] or type(current.get("gained")) is not int or current["gained"] < 0):
            raise ValueError("Invalid blue health outcome counter")
        if (previous is not None and current is not None and current["epoch"] == previous["epoch"]
                and rows[i - 1]["segment"] == row["segment"]):
            if current["gained"] < previous["gained"]:
                raise ValueError("Blue health outcome counter regressed")
            row["blueHealthGained"] = current["gained"] - previous["gained"]
        previous = current


def object_features(observation, field):
    x, y, _, _, tracked = observation["features"][:5]
    caches = observation.get(field) or []
    if not tracked or not caches:
        return [0, 0, 0]
    for c in caches:
        if not all(finite_number(c[k]) and 0 <= c[k] <= 4 for k in ("x", "y", "width", "height")):
            raise ValueError(f"Invalid {field} bounds")
    nearest = min(caches, key=lambda c: math.hypot(c["x"] - x, c["y"] - y))
    return [nearest["x"] - x, nearest["y"] - y, 1]


def terrain_grid(geometry):
    """Rasterize every on-screen segment, in the same bottom-left coordinates as Vision."""
    grid = [0.0] * (2 * GRID_CELLS)
    if geometry is None:
        return grid
    width, height = geometry["width"], geometry["height"]
    if not all(finite_number(n) and n > 0 for n in (width, height)):
        raise ValueError("Invalid terrain frame size")

    def cell(value, extent, count):
        return min(count - 1, int(max(0, min(1, value / extent)) * count))

    for floor in geometry["floors"]:
        x1, x2, y = (floor[k] for k in ("minimumX", "maximumX", "y"))
        if not all(map(finite_number, (x1, x2, y))):
            raise ValueError("Invalid floor coordinates")
        if x1 < x2 and x2 > 0 and x1 < width and 0 <= y <= height:
            offset = cell(y, height, GRID_ROWS) * GRID_COLUMNS
            for x in range(cell(x1, width, GRID_COLUMNS), cell(x2, width, GRID_COLUMNS) + 1):
                grid[offset + x] = 1.0
    for wall in geometry["walls"]:
        x, y1, y2 = (wall[k] for k in ("x", "bottomY", "topY"))
        if not all(map(finite_number, (x, y1, y2))):
            raise ValueError("Invalid wall coordinates")
        if y1 < y2 and y2 > 0 and y1 < height and 0 <= x <= width:
            offset = GRID_CELLS + cell(x, width, GRID_COLUMNS)
            for y in range(cell(y1, height, GRID_ROWS), cell(y2, height, GRID_ROWS) + 1):
                grid[offset + y * GRID_COLUMNS] = 1.0
    return grid


def door_grid(geometry):
    grid = [0.0] * GRID_CELLS
    if geometry is None:
        return grid
    width, height = geometry["width"], geometry["height"]
    def cell(v, size, count):
        return min(count - 1, int(max(0, min(1, v / size)) * count))
    for door in geometry.get("doors") or []:
        x1, y1, x2, y2 = (door[k] for k in ("x1", "y1", "x2", "y2"))
        if not all(map(finite_number, (x1, y1, x2, y2))):
            raise ValueError("Invalid door coordinates")
        if y1 == y2 and 0 <= y1 <= height and max(x1, x2) > 0 and min(x1, x2) < width:
            for x in range(cell(min(x1, x2), width, GRID_COLUMNS), cell(max(x1, x2), width, GRID_COLUMNS) + 1):
                grid[cell(y1, height, GRID_ROWS) * GRID_COLUMNS + x] = 1.0
        elif x1 == x2 and 0 <= x1 <= width and max(y1, y2) > 0 and min(y1, y2) < height:
            for y in range(cell(min(y1, y2), height, GRID_ROWS), cell(max(y1, y2), height, GRID_ROWS) + 1):
                grid[y * GRID_COLUMNS + cell(x1, width, GRID_COLUMNS)] = 1.0
    return grid

def read_session(path, *, verify_images=True):
    path = Path(path)
    manifest = json.loads((path / "manifest.json").read_text())
    names = RECORDING_SCHEMAS.get(manifest.get("schemaVersion"))
    if names is None:
        raise ValueError(f"{path}: unsupported observation schema")
    if (manifest.get("featureNames") != names or manifest.get("actionSizes") != ACTION_SIZES
            or manifest.get("historyLength") != HISTORY_LENGTH
            or manifest.get("sampleInterval") != 0.1):
        raise ValueError(f"{path}: incompatible policy contract")
    if manifest.get("status") not in {"completed", "interrupted"}:
        raise ValueError(f"{path}: session is {manifest.get('status')!r}; finish recording before export")
    start, duration = manifest.get("startedAt"), manifest.get("duration")
    if not finite_number(start) or not finite_number(duration) or duration < 0:
        raise ValueError(f"{path}: invalid session clock")
    observations, inputs = [], []
    last_capture = last_available = -math.inf
    for line_number, line in enumerate((path / "events.jsonl").read_text().splitlines(), 1):
        try:
            record = json.loads(line)
            if record["type"] == "observation":
                features = record["features"]
                if (len(features) != len(names) or not all(finite_number(x) and -4 <= x <= 4 for x in features)
                        or not isinstance(record["issues"], list)
                        or not all(isinstance(issue, str) for issue in record["issues"])):
                    raise ValueError("invalid features or issues")
                capture, available = record["captureTimestamp"], record["availableAt"]
                if (not all(finite_number(record[k]) for k in ("captureTimestamp", "capturedAt", "availableAt"))
                        or capture <= last_capture or available <= last_available
                        or available < start or available > start + duration + 0.001):
                    raise ValueError("invalid or unordered observation clock")
                for key in ("generation", "roomID", "roomRevision"):
                    if not isinstance(record[key], int) or isinstance(record[key], bool) or record[key] < 0:
                        raise ValueError(f"invalid {key}")
                last_capture, last_available = capture, available
                if record.get("imagePath"):
                    image = Path(record["imagePath"])
                    if image.is_absolute() or ".." in image.parts or (verify_images and not (path / image).is_file()):
                        raise ValueError("missing or unsafe review image path")
                observations.append(record)
            elif record["type"] == "input":
                if (not finite_number(record["timestamp"]) or not isinstance(record["button"], str)
                        or not isinstance(record["isPressed"], bool)
                        or not start <= record["timestamp"] <= start + duration + 0.001):
                    raise ValueError("invalid input event")
                inputs.append(record)
            else:
                raise ValueError("unknown record type")
        except (KeyError, TypeError, ValueError) as error:
            raise ValueError(f"{path}/events.jsonl:{line_number}: {error}") from error
    if (len(observations) != manifest["observationCount"]
            or len(inputs) != manifest["inputEventCount"]
            or sum(not x["issues"] for x in observations) != manifest["usableObservationCount"]
            or sum(bool(x.get("imagePath")) for x in observations) != manifest["imageCount"]):
        raise ValueError(f"{path}: manifest counts do not match the evidence")
    return manifest, observations, inputs


class ActionTimeline:
    def __init__(self, events):
        self.events = sorted(events, key=lambda x: x["timestamp"])
        self.times = [x["timestamp"] for x in self.events]
        self.states = []
        self.attack_presses = []
        held = set()
        for event in self.events:
            if event["isPressed"]:
                if event["button"] == "x" and "x" not in held:
                    self.attack_presses.append(event["timestamp"])
                held.add(event["button"])
            else:
                held.discard(event["button"])
            self.states.append(frozenset(held))

    def continued_attack(self, previous_end, start, end):
        """Called for an Attack label. A new physical press is always fresh,
        even when two short taps land in adjacent 100 ms decision windows."""
        i = bisect.bisect_left(self.attack_presses, end)
        return i > 0 and self.attack_presses[i - 1] < min(previous_end, start)

    def action_during(self, start, end):
        index = bisect.bisect_right(self.times, start)
        held = set(self.states[index - 1]) if index else set()
        # Preserve taps shorter than a 100 ms decision. Any unsupported state
        # during the decision invalidates its label, including a press/release pair.
        during = self.events[index:bisect.bisect_left(self.times, end)]
        held.update(x["button"] for x in during if x["isPressed"])
        if held - SUPPORTED:
            return None, "unsupported-input"
        if {"left", "right"} <= held or {"up", "down"} <= held:
            return None, "conflicting-directions"
        return [1 if "left" in held else 2 if "right" in held else 0,
                1 if "up" in held else 2 if "down" in held else 0,
                int("z" in held), int("x" in held)], None


def examples(manifest, observations, inputs, action_delay=0.0, *, compact=False, policy_version=SCHEMA_VERSION):
    if not finite_number(action_delay) or not 0 <= action_delay <= 0.5:
        raise ValueError("action delay must be between zero and 0.5 seconds")
    names = SCHEMAS[policy_version]
    width = len(names)
    timeline = ActionTimeline(inputs)
    history = deque(maxlen=HISTORY_LENGTH)
    previous = None
    segment = -1
    rejected = Counter()
    rows = []
    atlas_views = []
    path_progress = []
    path_alignment = []
    enemy_outcomes = []
    blue_outcomes = []
    visual_anchor = None
    end = manifest["startedAt"] + manifest["duration"]
    for observation in observations:
        time = observation["availableAt"]
        identity = (observation["generation"], observation["roomID"])
        issues = list(observation["issues"])
        # Independently validate ages even if a producer incorrectly marked a row usable.
        if not 0 <= time - observation["capturedAt"] <= 0.35:
            issues.append("stale-capture")
        detection_time = observation.get("detectionTimestamp")
        fresh_detection = finite_number(detection_time) and 0 <= observation["captureTimestamp"] - detection_time <= 0.2
        features = observation["features"]
        masked = (manifest.get("botDomain") != "hacker" and observation.get("terrainSource") == "detected"
                  and observation.get("knightSource") is None and observation.get("knightPrediction") is None
                  and observation["issues"] == ["missing-knight"] and features[:5] == [0] * 5
                  and features[6] == 1 and (any(features[i] == 1 for i in range(20, 96, 5))
                      or bool((observation.get("detectedTerrain") or {}).get("walls")))
                  and fresh_detection and not (set(issues) - {"missing-knight"}))
        if masked:
            issues.remove("missing-knight")
        predicted = observation.get("knightSource") == "visual-predicted"
        valid_prediction = False
        if predicted:
            evidence = observation.get("knightPrediction")
            context = observation.get("visualContext")
            if isinstance(evidence, dict) and isinstance(context, dict):
                age, seen, uncertainty = (evidence.get(k) for k in ("age", "lastSeenAt", "uncertainty"))
                compensated = evidence.get("cameraCompensated")
                observed_issues = evidence.get("observedIssues")
                valid_prediction = (
                    visual_anchor is not None and finite_number(seen) and abs(visual_anchor[0] - seen) <= 1e-6
                    and visual_anchor[1] == identity and finite_number(age) and isinstance(compensated, bool)
                    and 0 < age <= (0.6 if compensated else 0.35) + 1e-6
                    and age + time - observation["capturedAt"] < (0.6 if compensated else 0.35)
                    and abs(observation["captureTimestamp"] - seen - age) <= 1e-6
                    and finite_number(uncertainty) and 0 <= uncertainty <= 0.06
                    and isinstance(observed_issues, list) and "missing-knight" in observed_issues
                    and all(x in {"missing-knight", "stale-or-missing-detection"} for x in observed_issues)
                    and observation.get("terrainSource") == "detected"
                    and finite_number(context.get("frameWidth")) and context["frameWidth"] > 0
                    and finite_number(context.get("frameHeight")) and context["frameHeight"] > 0
                    and (not compensated or all(finite_number(context.get(k)) for k in ("cameraX", "cameraY")))
                    and observation["features"][4] >= 0.2)
            if not valid_prediction:
                issues.append("invalid-knight-prediction")
        if not fresh_detection and not valid_prediction:
            issues.append("stale-or-missing-detection")
        if (not masked and features[4] < (0.2 if valid_prediction else 0.5)) or features[6] != 1:
            issues.append("missing-perception")
        if issues:
            visual_anchor = None
        elif masked:
            visual_anchor = None
        elif observation.get("knightSource") == "visual" and fresh_detection:
            visual_anchor = (detection_time, identity)
        action_start = time + action_delay
        if action_start + 0.1 > end:
            issues.append("incomplete-action-window")
        action, reason = timeline.action_during(action_start, action_start + 0.1)
        if reason:
            issues.append(reason)
        if issues:
            rejected.update(set(issues))
            history.clear()
            previous = None
            continue
        boundary = previous is None or previous[0] != identity or time - previous[1] > 0.35
        if boundary:
            history.clear()
            segment += 1
        # This interval ends at the current observation. Using the previous
        # target would leak future edges when processing jitter overlaps label
        # windows, or when --action-delay moves targets into the future.
        previous_action = [0, 0, 0, 0] if boundary else (
            timeline.action_during(previous[1], time)[0] or [0, 0, 0, 0])
        features = list(observation["features"])
        if len(features) == len(LEGACY_FEATURE_NAMES):
            features.extend([0.0] * len(WALL_FEATURE_NAMES))
        # Earlier Hacker captures recorded pixel detections in the wall slots.
        hacker = manifest.get("botDomain") == "hacker" or observation.get("terrainSource") == "human-authored"
        if masked or (hacker and observation.get("authoredTerrain") is None):
            features[len(LEGACY_FEATURE_NAMES):] = [0.0] * len(WALL_FEATURE_NAMES)
        geometry = observation.get("authoredTerrain" if hacker else "detectedTerrain")
        if policy_version >= 13:
            features = features[:16] + features[96:144]
        else:
            features.extend(terrain_grid(geometry))
        features.extend(object_features(observation, "geoCaches"))
        view = observation.get("atlasView")
        features.extend(atlas_features(view))
        shared_path = observation.get("hackerPathVersion") == 1
        progress = observation.get("pathProgress")
        alignment = observation.get("pathAlignment") if shared_path else None
        # Retain route guidance across short occlusions, even when the
        # displacement cannot be scored. Legacy brightness hints have no marker.
        ground_path = shared_path or observation.get("atlasPathVersion") == 1 or progress is not None
        features.extend(road_features(observation.get("roadHint") if ground_path else None))
        if policy_version < 13:
            features.extend(object_features(observation, "joniBlessings"))
        features.extend(object_features(observation, "lifebloodCocoons"))
        if policy_version < 13:
            features.extend(door_grid(geometry))
        shared_road = road_features(observation.get("roadHint")) if shared_path else [0] * 6
        features.extend(shared_road[:3] if any(shared_road[1:3]) else [0, 0, 0])
        atlas_views.append(view)
        path_progress.append(progress)
        path_alignment.append(alignment)
        enemy_outcomes.append(observation.get("enemyOutcomes"))
        blue_outcomes.append(observation.get("blueHealthOutcomes"))
        obstacle = observation.get("blockade")
        if obstacle is not None:
            dx, dy, side = obstacle["dx"], obstacle["dy"], obstacle["mark"]["direction"]
            if (side not in (-1, 1) or obstacle["mark"]["crossed"] or
                    not all(finite_number(v) and abs(v) <= 1 for v in (dx, dy))):
                raise ValueError("Invalid blockade evidence")
            features[13:16] = [dx, dy, side]
        features[8] = int(boundary)
        features[9:13] = [0 if previous_action[0] == 0 else -1 if previous_action[0] == 1 else 1,
                         0 if previous_action[1] == 0 else 1 if previous_action[1] == 1 else -1,
                         previous_action[2], previous_action[3]]
        if policy_version >= 13:
            assert len(features) == len(COMPACT_SCALAR_INDICES)
            markings = encode_markings(geometry, features[0], features[1], version=policy_version)
            features += markings
            if policy_version >= GROUND_CONTACT_POLICY_VERSION:
                grounded = observation.get("grounded")
                features.append(1 if grounded is True else -1 if grounded is False else 0)
            if policy_version == LANDING_GEOMETRY_POLICY_VERSION:
                prior_landing = history[-1][-len(LANDING_NAMES):] if history else None
                features += landing_features(markings, features[3], grounded, prior_landing)
        else:
            features = features[:width]
            if policy_version < 4: features[13:16] = [0, 0, 0]
        history.append(features)
        zeros = array("f", [0.0]) if compact else [0.0]
        stacked = zeros * (width * (HISTORY_LENGTH - len(history)))
        stacked.extend(value for item in history for value in item)
        rows.append({"observation": stacked, "action": action, "timestamp": time,
                     "continuedAttack": bool(action[3] and not boundary and
                         timeline.continued_attack(previous[1] + action_delay + 0.1, action_start, action_start + 0.1)),
                     "segment": segment, "session": manifest["id"]})
        previous = (identity, time)
    credit_blue_health_outcomes(rows, blue_outcomes)
    exploration_weights(rows, atlas_views, path_progress, path_alignment)
    credit_enemy_outcomes(rows, enemy_outcomes)
    return rows, dict(rejected)


def demonstration_controls(archive):
    """Keep raw actions in the archive. Score a continued physical X hold as a missed release."""
    import numpy as np
    actions = archive["actions"].copy()
    continued = archive["continued_attacks"] if "continued_attacks" in archive else np.zeros(len(actions), dtype=bool)
    if (actions.ndim != 2 or actions.shape[1] != len(ACTION_SIZES) or continued.dtype != np.bool_
            or continued.shape != (len(actions),) or (actions[continued, 3] != 1).any()):
        raise ValueError("Invalid demonstrated attack controls")
    actions[continued, 3] = 0
    return actions, continued


def session_paths(root):
    root = Path(root)
    if (root / "manifest.json").exists():
        return [root]
    return sorted(p.parent for p in root.glob("demo-*/manifest.json"))


def recovery_maneuver(path):
    """Extract a short, visually anchored correction from a designated expert clip."""
    manifest, observations, events = read_session(path)
    if manifest.get("botDomain") != "gameplay" or manifest["status"] != "completed" or len(observations) < 5:
        return None
    first = observations[0]
    valid, _ = examples(manifest, observations, events)
    accepted = {r["timestamp"] for r in valid}
    start = first["availableAt"]
    if (not first.get("imagePath") or first["issues"] != ["missing-knight"]
            or first["availableAt"] - manifest["startedAt"] > 0.15):
        return None
    duration = min(2.6, manifest["startedAt"] + manifest["duration"] - start - 0.1)
    track = []
    for o in observations:
        t = o["availableAt"] - start
        if t > duration: break
        if (o["availableAt"] not in accepted or o["roomID"] != first["roomID"]
                or o["generation"] != first["generation"] or (track and t - track[-1]["time"] > 0.3)):
            break
        f = o["features"]
        floors = [[f[0]+f[i], f[0]+f[i+1], f[1]+f[i+2]] for i in range(16,96,5) if f[i+4] == 1]
        track.append({"time": t, "floors": floors, "knight": f[:2] if f[4] >= 0.5 else None})
    seen = next((row["time"] for row in track if row["knight"] is not None), None)
    if seen is None or not 0.2 <= seen <= RECOVERY_START_WINDOW or track[-1]["time"] - seen < 0.4:
        return None
    duration = track[-1]["time"]
    if not any(e["isPressed"] and e["button"] in {"left","right","z"} and e["timestamp"] < start + seen for e in events):
        return None
    timeline = ActionTimeline(events)
    times = sorted({0.0} | {e["timestamp"]-start for e in events if start < e["timestamp"] < start+duration})
    controls = []
    for t in times:
        action, error = timeline.action_during(start+t, start+t+1e-7)
        if error: return None
        controls.append({"time": t, "values": action})
    image = (Path(path) / first["imagePath"]).read_bytes()
    if len(image) > 512 * 1024: return None
    return {"session": manifest["id"], "duration": duration, "firstSighting": seen, "track": track,
            "controls": controls, "startImage": base64.b64encode(image).decode("ascii")}


def apply_recovered_walls(evidence_path, path, manifest, observations):
    evidence = json.loads(evidence_path.read_text())
    if (manifest["schemaVersion"] != 1 or evidence["id"] != manifest["id"]
            or evidence["eventsSHA256"] != hashlib.sha256((path / "events.jsonl").read_bytes()).hexdigest()):
        raise ValueError("Wall evidence belongs to a different recording")
    indexed = {(o["generation"], o["captureTimestamp"]): o for o in observations}
    for row in evidence["rows"]:
        observation = indexed.pop((row["generation"], row["captureTimestamp"]), None)
        features = row["features"]
        if (observation is None or len(features) != len(WALL_FEATURE_NAMES) or features[0] != 1
                or not all(finite_number(x) and -4 <= x <= 4 for x in features)):
            raise ValueError("Invalid recovered wall evidence")
        observation["features"].extend(features)


def paired_measurements(manifest, observations, partner, partner_rows):
    """Only copy past measurements from the reciprocal K recording on the same host clock."""
    if (not manifest.get("pairedClipID") or manifest["pairedClipID"].upper() != partner["id"].upper()
            or (partner.get("pairedClipID") or "").upper() != manifest["id"].upper()
            or manifest.get("replayID") != partner.get("replayID") or partner.get("status") != "completed"
            or manifest.get("botDomain") == partner.get("botDomain")):
        return
    hacker = manifest.get("botDomain") == "hacker"
    source_field = "atlasView" if hacker else "enemyOutcomes"
    cursor = -1
    for row in observations:
        while cursor + 1 < len(partner_rows):
            following = partner_rows[cursor + 1]
            if following["availableAt"] > row["availableAt"] or following["capturedAt"] > row["capturedAt"]:
                break
            cursor += 1
        if cursor < 0 or row.get(source_field) is not None:
            continue
        other = partner_rows[cursor]
        if (row["generation"] != other["generation"] or row["capturedAt"] - other["capturedAt"] > 0.2
                or "room-transition" in other["issues"] or "room-transition" in row["issues"]):
            continue
        if other.get(source_field) is not None:
            row[source_field] = other[source_field]
            row["pairedMeasurement"] = source_field


def reconstruct_recorded_paths(executable, path, observations):
    """Replay a clip causally through Vision's native ground-path graph in memory."""
    try:
        result = subprocess.run([str(executable), "--recover-bot-atlas-path", str(path)],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        recovered = json.loads(result.stdout)
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        raise ValueError(f"Could not reconstruct recorded paths for {path.name}: {error}") from error
    if not isinstance(recovered, list):
        raise ValueError("Invalid reconstructed path response")
    seen = set()
    for row in recovered:
        if not isinstance(row, dict):
            raise ValueError("Invalid reconstructed path frame")
        index = row.get("index")
        if type(index) is not int or not 0 <= index < len(observations) or index in seen:
            raise ValueError("Invalid reconstructed path index")
        seen.add(index)
        observation = observations[index]
        if (observation.get("atlasPathVersion") == 1 or observation.get("hackerPathVersion") == 1 or row.get("generation") != observation["generation"]
                or row.get("captureTimestamp") != observation["captureTimestamp"]):
            raise ValueError("Reconstructed path does not match the recording")
        hint = row.get("roadHint")
        road_features(hint)
        progress = row.get("pathProgress")
        if progress is not None and (not finite_number(progress) or not 0 <= progress <= 100):
            raise ValueError("Invalid reconstructed path progress")
        observation["roadHint"] = hint
        observation["pathProgress"] = progress
        observation["atlasPathVersion"] = 1


def apply_exit_path_evidence(evidence, path, manifest, observations):
    """Apply only frame-matched evidence tied to this recording's immutable events."""
    if (evidence.get("schemaVersion") != 1 or str(evidence.get("id", "")).upper() != manifest["id"].upper()
            or evidence.get("eventsSHA256") != hashlib.sha256((path / "events.jsonl").read_bytes()).hexdigest()
            or evidence.get("recordingManifestSHA256") != hashlib.sha256((path / "manifest.json").read_bytes()).hexdigest()
            or str(evidence.get("replayID", "")).upper() != str(manifest.get("replayID", "")).upper()):
        raise ValueError("Exit path evidence belongs to another recording")
    rows = evidence.get("rows")
    if not isinstance(rows, list):
        raise ValueError("Invalid exit path evidence")
    seen = set()
    for row in rows:
        if not isinstance(row, dict):
            raise ValueError("Invalid exit path frame")
        index = row.get("index")
        if type(index) is not int or not 0 <= index < len(observations) or index in seen:
            raise ValueError("Invalid exit path index")
        seen.add(index)
        obs = observations[index]
        if (row.get("generation") != obs["generation"] or row.get("captureTimestamp") != obs["captureTimestamp"]
                or row.get("hackerPathVersion") != 1):
            raise ValueError("Exit path does not match the recording")
        road_features(row.get("roadHint"))
        progress, alignment = row.get("pathProgress"), row.get("pathAlignment")
        if progress is not None and (not finite_number(progress) or not 0 <= progress <= 100):
            raise ValueError("Invalid exit path progress")
        if alignment is not None and (not finite_number(alignment) or abs(alignment) > 3):
            raise ValueError("Invalid shared path alignment")
        if obs.get("hackerPathVersion") != 1:
            obs.update({key: row.get(key) for key in ("roadHint", "pathProgress", "pathAlignment")})
            obs["hackerPathVersion"] = 1


def recover_exit_path(executable, path, replay_root, labels, connections, objective, destination):
    """Create a durable, provenance-bound sidecar without changing capture files."""
    path, replay_root, destination = Path(path), Path(replay_root), Path(destination)
    manifest, observations, _ = read_session(path)
    replay_id = str(uuid.UUID(manifest["replayID"])).lower()
    matches = [p for p in replay_root.glob("path-*.json") if p.stem.lower().endswith("-" + replay_id)]
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one archived replay for {replay_id}; found {len(matches)}")
    if len(objective) != 2 or not all(finite_number(v) for v in objective):
        raise ValueError("Invalid exit objective")
    replay = matches[0]
    archive_manifest = replay_root / ("frames-" + replay_id) / "manifest.json"
    files = [replay, Path(labels), Path(connections), archive_manifest, path / "manifest.json", path / "events.jsonl"]
    hashes = [hashlib.sha256(f.read_bytes()).hexdigest() for f in files]
    try:
        result = subprocess.run([str(executable), "--recover-bot-exit-path", str(path), str(replay),
                                 str(labels), str(connections), str(objective[0]), str(objective[1])],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        rows = json.loads(result.stdout)
    except (OSError, subprocess.CalledProcessError, ValueError) as error:
        raise ValueError(f"Could not reconstruct exit path for {path.name}: {error}") from error
    if hashes != [hashlib.sha256(f.read_bytes()).hexdigest() for f in files]:
        raise ValueError("Exit path inputs changed during reconstruction")
    evidence = {"schemaVersion": 1, "id": manifest["id"],
                "eventsSHA256": hashes[5], "recordingManifestSHA256": hashes[4],
                "archiveManifestSHA256": hashes[3],
                "replayID": replay_id, "replaySHA256": hashes[0], "labelsSHA256": hashes[1],
                "connectionsSHA256": hashes[2], "objective": {"x": objective[0], "y": objective[1]}, "rows": rows}
    apply_exit_path_evidence(evidence, path, manifest, observations)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(destination.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        temporary.write_text(json.dumps(evidence, indent=2, allow_nan=False) + "\n")
        temporary.replace(destination)
    finally:
        temporary.unlink(missing_ok=True)
    return evidence


def collect(root, action_delay=0.0, wall_evidence=None, *, compact=False, blockade_evidence=None, paired_root=None,
            path_extractor=None, exit_path_evidence=None, policy_version=SCHEMA_VERSION):
    paths = session_paths(root)
    if not paths:
        raise ValueError(f"No demonstration sessions in {root}")
    all_rows, reports, skipped = [], [], []
    for path in paths:
        try:
            manifest, observations, inputs = read_session(path)
            marking_recovery = recover_recorded_spikes(path, manifest, observations) if policy_version >= 13 else {}
            evidence_file = None
            if exit_path_evidence:
                evidence_file = Path(exit_path_evidence) / (path.name + ".json")
                if evidence_file.is_file():
                    apply_exit_path_evidence(json.loads(evidence_file.read_text()), path, manifest, observations)
            if (path_extractor and not (evidence_file and evidence_file.is_file())
                    and not any(o.get("hackerPathVersion") == 1 for o in observations)
                    and any(o.get("atlasPathVersion") != 1 for o in observations)):
                reconstruct_recorded_paths(path_extractor, path, observations)
            if manifest.get("pairedClipID"):
                paired_id = str(uuid.UUID(manifest["pairedClipID"]))
                partner = Path(paired_root or path.parent) / ("demo-" + paired_id)
                if (partner / "events.jsonl").is_file():
                    other, partner_rows, _ = read_session(partner, verify_images=False)
                    paired_measurements(manifest, observations, other, partner_rows)
            if blockade_evidence:
                file = Path(blockade_evidence) / (path.name + ".json")
                if file.exists():
                    evidence = json.loads(file.read_text())
                    if (evidence["id"].upper() != manifest["id"].upper() or
                            evidence["eventsSHA256"] != hashlib.sha256((path / "events.jsonl").read_bytes()).hexdigest()):
                        raise ValueError("Blockade evidence belongs to another recording")
                    indexed = {(o["generation"], o["captureTimestamp"]): o for o in observations}
                    for row in evidence["rows"]:
                        obs = indexed.pop((row["generation"], row["captureTimestamp"]), None)
                        if obs is None:
                            raise ValueError("Blockade evidence lacks a matching frame")
                        obs["blockade"] = row["blockade"]
            if wall_evidence:
                evidence_path = Path(wall_evidence) / (path.name + ".json")
                if evidence_path.exists():
                    apply_recovered_walls(evidence_path, path, manifest, observations)
            rows, rejected = examples(manifest, observations, inputs, action_delay, compact=compact, policy_version=policy_version)
        except (OSError, ValueError) as error:
            # Active/failed/corrupt recordings are reported, never silently trained.
            skipped.append({"path": str(path), "error": str(error)})
            continue
        hashes = {name: hashlib.sha256((path / name).read_bytes()).hexdigest()
                  for name in ("manifest.json", "events.jsonl")}
        frame_count = len(SCHEMAS[policy_version])
        wall_analyzed, wall_present_count = wall_statistics(rows, policy_version)
        reports.append({"markingRecovery": marking_recovery, "id": manifest["id"], "path": str(path), "status": manifest["status"],
                        "pairedMeasurements": sum("pairedMeasurement" in o for o in observations),
                        "maskedKnightExamples": sum(row["observation"][-frame_count + 4] == 0 for row in rows),
                        "duration": manifest["duration"], "observations": len(observations),
                        "blockadeExamples": sum(row["observation"][-frame_count + 15] != 0 for row in rows),
                        "examples": len(rows), "rejected": rejected,
                        "wallAnalyzedExamples": wall_analyzed,
                        "wallPresentExamples": wall_present_count,
                        "inputs": dict(Counter(x["button"] for x in inputs if x["isPressed"])),
                        "sha256": hashes})
        all_rows.extend(rows)
    return all_rows, {"schemaVersion": policy_version, "featureCount": len(SCHEMAS[policy_version]),
                      "historyLength": HISTORY_LENGTH, "actionSizes": ACTION_SIZES,
                      "actionDelay": action_delay, "examples": len(all_rows),
                      "sessions": reports, "skipped": skipped}


def wall_statistics(rows, version):
    width = len(SCHEMAS[version])
    if version < 2: return 0, 0
    if version < 13:
        frames = [r["observation"][-width:] for r in rows]
        return (sum(bool(f[144] or any(f[WALL_GRID_START:GEO_START])) for f in frames),
                sum(bool(f[149] or f[154] or any(f[WALL_GRID_START:GEO_START])) for f in frames))
    names = SCHEMAS[version]
    known, count = [names.index(n) for n in ("marking_wall_known", "marking_wall_count")]
    return (sum(bool(r["observation"][-width + known]) for r in rows),
            sum(bool(r["observation"][-width + count]) for r in rows))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["inspect", "export"])
    parser.add_argument("root", type=Path, help="Session directory or demonstrations root")
    parser.add_argument("--output", type=Path, help="Output .npz for export")
    parser.add_argument("--policy-version", type=int, choices=sorted(SCHEMAS), default=SCHEMA_VERSION)
    parser.add_argument("--action-delay", type=float, default=0.0,
                        help="Seconds after observation availability to label; recorded raw evidence is unchanged")
    parser.add_argument("--wall-evidence", type=Path, help="Exact-frame wall evidence recovered for this snapshot")
    parser.add_argument("--blockade-evidence", type=Path, help="Blockades reconstructed from recorded movement")
    parser.add_argument("--paired-root", type=Path, help="Snapshotted paired measurements from the same K run")
    parser.add_argument("--path-extractor", type=Path, help="Vision executable for reconstructing paths from older clips")
    parser.add_argument("--exit-path-evidence", type=Path, help="Directory of provenance-bound exit path sidecars")
    parser.add_argument("--exit-path-replay-root", type=Path)
    parser.add_argument("--exit-path-labels", type=Path)
    parser.add_argument("--exit-path-connections", type=Path)
    parser.add_argument("--exit-path-objective", type=float, nargs=2, metavar=("X", "Y"))
    args = parser.parse_args(argv)
    reconstruct = [args.exit_path_replay_root, args.exit_path_labels, args.exit_path_connections, args.exit_path_objective]
    if any(x is not None for x in reconstruct):
        if not all(x is not None for x in reconstruct) or not args.path_extractor or not args.exit_path_evidence:
            parser.error("exit reconstruction requires --path-extractor, --exit-path-evidence and all four --exit-path inputs")
        for path in session_paths(args.root):
            recover_exit_path(args.path_extractor, path, args.exit_path_replay_root, args.exit_path_labels,
                              args.exit_path_connections, args.exit_path_objective,
                              args.exit_path_evidence / (path.name + ".json"))
    rows, report = collect(args.root, args.action_delay, args.wall_evidence, compact=True, blockade_evidence=args.blockade_evidence,
                           paired_root=args.paired_root, path_extractor=args.path_extractor, exit_path_evidence=args.exit_path_evidence, policy_version=args.policy_version)
    if args.command == "export":
        if not args.output or args.output.suffix != ".npz":
            parser.error("export requires --output ending in .npz")
        if report["skipped"]:
            raise ValueError("Export refused: inspect and resolve skipped sessions, or select one completed session")
        if not rows:
            raise ValueError("No valid examples to export")
        import numpy as np
        args.output.parent.mkdir(parents=True, exist_ok=True)
        np.savez_compressed(args.output,
                            observations=np.asarray([x["observation"] for x in rows], dtype=np.float32),
                            actions=np.asarray([x["action"] for x in rows], dtype=np.int64),
                            enemy_outcomes=np.asarray([[x["enemyDamage"], x["enemyKills"]] for x in rows], dtype=np.int64),
                            blue_health_gained=np.asarray([x["blueHealthGained"] for x in rows], dtype=np.int64),
                            outcome_weights=np.asarray([x["outcomeWeight"] for x in rows], dtype=np.float32),
                            continued_attacks=np.asarray([x["continuedAttack"] for x in rows], dtype=np.bool_),
                            timestamps=np.asarray([x["timestamp"] for x in rows], dtype=np.float64),
                            session_ids=np.asarray([x["session"] for x in rows]),
                            segments=np.asarray([x["segment"] for x in rows], dtype=np.int64))
        report["datasetSHA256"] = hashlib.sha256(args.output.read_bytes()).hexdigest()
        args.output.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))
    return 0 if rows and not report["skipped"] else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, ImportError) as error:
        print(f"Bot dataset: {error}", file=sys.stderr)
        sys.exit(1)
