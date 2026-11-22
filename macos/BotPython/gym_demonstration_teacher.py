"""Rehearse actual recorded human maneuvers at matching locations in the training game."""
from bisect import bisect_right
from dataclasses import dataclass
import json
import math

import numpy as np
from hkv_bot_dataset import demonstration_controls


@dataclass
class Decision:
    x: float
    y: float
    vx: float
    grounded: bool
    action: tuple


def training_routes(support, archive, training_sessions):
    """Join verified training controls to contemporaneous Unity positions, respecting all gaps."""
    paths = {p.stem[-36:]: p for p in (support / "input-paths-v1").glob("path-*.json")}
    sessions, times, segments = (archive[key] for key in ("session_ids", "timestamps", "segments"))
    actions, _ = demonstration_controls(archive)
    routes = []
    for clip in training_sessions:
        context = support / "bot-v1/demonstrations" / ("demo-" + clip.lower()) / "audit-context.json"
        if not context.is_file():
            continue
        path = paths.get(json.loads(context.read_bytes()).get("pathID"))
        if path is None:
            continue
        recording = json.loads(path.read_bytes())
        samples = recording.get("groundTruthSamples") or []
        stamps = [r["receivedTimestamp"] for r in samples]
        route, previous = [], None
        for i in np.flatnonzero(sessions == clip):
            j = bisect_right(stamps, times[i]) - 1
            sample = samples[j]["sample"] if j >= 0 and times[i] - stamps[j] <= 0.08 else {}
            valid = sample.get("heroAvailable") and sample.get("sceneName") == "Tutorial_01"
            if previous is not None and (not valid or segments[i] != segments[previous] or
                                         not 0.025 <= times[i] - times[previous] <= 0.18):
                if len(route) >= 6:
                    routes.append((clip, route))
                route = []
            if valid:
                route.append(Decision(sample["heroX"], sample["heroY"], sample["velocityX"],
                                      sample["grounded"], tuple(map(int, actions[i]))))
            previous = i if valid else None
        if len(route) >= 6:
            routes.append((clip, route))
    return routes


class DemonstrationPractice:
    def __init__(self, routes):
        self.routes = routes
        self.windows, self.clips = [], []
        self.plan = []
        self.tried = set()

    def pick(self, frame, visited):
        """Prefer a nearby human route that reached fresh ground, including climbs and backtracking."""
        x, y = frame["heroX"], frame["heroY"]
        best, chosen = None, None
        for index, (clip, route) in enumerate(self.routes):
            for start, state in enumerate(route):
                if ((index, start) in self.tried or abs(x - state.x) > 2 or abs(y - state.y) > 0.9
                        or frame["grounded"] != state.grounded):
                    continue
                fresh = set()
                for end in range(start + 1, min(len(route), start + 25)):
                    dest = route[end]
                    if not dest.grounded:
                        continue
                    cell = (math.floor(dest.x / 4), math.floor(dest.y / 4))
                    if cell not in visited:
                        fresh.add(cell)
                    distance = math.hypot(dest.x - x, dest.y - y)
                    if end - start < 5 or distance < 3:
                        continue
                    error = abs(x - state.x) + 2 * abs(y - state.y)
                    error += 0.08 * abs(frame["velocityX"] - state.vx)
                    score = (min(3, len(fresh)), min(distance, 8) - 2.5 * error - 0.08 * (end - start))
                    if best is None or score > best:
                        best, chosen = score, (index, start, end, clip)
        return chosen

    def controls(self, frame, visited, stall, previous):
        step = frame["step"]
        if not self.plan:
            if stall < 7 or self.windows and step <= self.windows[-1][1] + 5:
                return None
            path = self.pick(frame, visited)
            if path is None:
                return None
            index, start, end, clip = path
            self.tried.update((index, j) for j in range(max(0, start - 3), start + 4))
            self.plan = self.routes[index][1][start:end]
            self.clips.append(clip)
            self.windows.append((step, step))
        start = self.windows[-1][0]
        offset = step - start
        if offset >= len(self.plan):
            self.plan = []
            return None
        state = self.plan[offset]
        if abs(state.x - frame["heroX"]) > 3 or abs(state.y - frame["heroY"]) > 2.5:
            self.plan = []
            return None
        action = list(state.action)
        if offset == 0:
            # Supply a release first if the live learner was already holding the button.
            for head in (2, 3):
                if previous[head] and action[head]:
                    action[head] = 0
        self.windows[-1] = (start, step + 1)
        return np.asarray(action, dtype=np.int64)
