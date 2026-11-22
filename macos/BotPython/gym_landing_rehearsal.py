"""Keep short, independently safe climbs even when a later part of the episode fails."""
from collections import deque

import numpy as np

from hacker_gym import START


MINIMUM_SAFE_CLIMB = .7


class LandingRehearsal:
    def __init__(self):
        self.approach = deque(maxlen=6)
        self.jump = None
        self.pending = []
        self.confirmed = []

    def record(self, step, observation, action, before, after):
        if observation is None or after["damage"] != before["damage"]:
            self.approach.clear()
            self.jump = None
            self.pending.clear()
            return
        decision = observation, tuple(action)
        if (before["grounded"] and not after["grounded"] and action[2] and after["velocityY"] > 3
                and len(self.approach) >= 2):
            self.jump = (step, before["heroX"], before["heroY"], list(self.approach))
        if self.jump is not None:
            takeoff, x, y, controls = self.jump
            controls.append(decision)
            if after["grounded"]:
                if 2 <= step - takeoff <= 16 and after["heroY"] > y + MINIMUM_SAFE_CLIMB:
                    self.pending.append((step + 5, (x, y, after["heroX"], after["heroY"], controls)))
                self.jump = None
            elif step - takeoff >= 16:
                self.jump = None
        self.approach.append(decision)
        self.pending = [(due, item) for due, item in self.pending if after["heroY"] >= item[3] - .7]
        while self.pending and self.pending[0][0] <= step:
            self.confirmed.append(self.pending.pop(0)[1])


def save_landings(work, seed, examples):
    if not 100_000 <= seed < 1_000_000 or not examples:
        return
    folder = work / "clean-landings"
    folder.mkdir(exist_ok=True)
    for x, y, end_x, end_y, rows in examples:
        zone = [round(value / 2) for value in (x, y, end_x, end_y)]
        path = folder / ("_".join(map(str, [*zone, seed])) + ".npz")
        if path.exists():
            continue
        temporary = path.with_suffix(".tmp")
        with temporary.open("wb") as stream:
            np.savez_compressed(stream, observations=np.stack([r for r, _ in rows]),
                                actions=np.asarray([a for _, a in rows], dtype=np.int8))
        temporary.replace(path)


def missed_ledge_takeoff(fields, samples, frontier_step):
    """Find a late Jump press followed by an observed loss of the higher shelf."""
    step, scene, state, available, x, y, velocity_y, grounded, mask, health = (
        fields.index(field) for field in
        ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")
    )
    for index in range(len(samples) - 3, 0, -1):
        support, fall = samples[index - 1:index + 1]
        # Progress can be credited during the first few frames of a fall.
        if support[step] < frontier_step - 8:
            break
        if (support[scene] != START or support[state] != "PLAYING" or not support[available]
                or not support[grounded] or fall[grounded] or fall[velocity_y] > 0
                or (support[mask] | fall[mask]) & 32):
            continue
        movement = fall[mask] & 3
        if movement not in (1, 2):
            continue
        direction = -1 if movement == 1 else 1
        if direction * (fall[x] - support[x]) < .15:
            continue
        following = samples[index + 1:index + 13]
        late_jump = next((row for row in following[:2] if not row[grounded] and row[mask] & 32), None)
        if late_jump is None:
            continue
        touchdown = next((i for i, row in enumerate(following) if row[grounded]), None)
        if touchdown is None or following[touchdown][step] <= late_jump[step]:
            continue
        landing = following[touchdown]
        if (support[y] - landing[y] < 1.25 or samples[-1][y] >= support[y] - .5
                or any(row[scene] != support[scene] or row[state] != "PLAYING" or not row[available]
                       or row[health] != support[health] or row[y] > support[y] + .3
                       for row in (fall, *following[:touchdown + 1]))):
            continue
        return dict(step=support[step], x=support[x], y=support[y], direction=direction,
                    jumpStep=late_jump[step], landingStep=landing[step], landingY=landing[y])
    return None



def missed_jump_hold(fields, samples, graph):
    """Find a cut ascent that missed a marked landing and was never recovered."""
    step, scene, state, available, x, y, vy, grounded, buttons, health = (
        fields.index(name) for name in
        ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")
    )
    for index, (start, rise, release) in enumerate(zip(samples, samples[1:], samples[2:])):
        if (not start[grounded] or start[buttons] & 32 or rise[grounded] or release[grounded]
                or not rise[buttons] & 32 or release[buttons] & 32
                or rise[vy] < 10 or release[vy] > 1 or release[y] > rise[y] + .25):
            continue
        held_direction = rise[buttons] & 3
        if held_direction not in (1, 2) or release[buttons] & 3 != held_direction:
            continue
        # A deliberately short hop on continuous floor is valid. Require a
        # lower touchdown after this press and a higher authored continuation.
        tail = samples[index + 3:index + 14]
        touchdown = next((offset for offset, row in enumerate(tail) if row[grounded]), None)
        if touchdown is None:
            continue
        landing = tail[touchdown]
        if (start[y] - landing[y] < 1.25
                or any(row[scene] != START or row[state] != "PLAYING" or not row[available]
                       or row[health] != start[health]
                       for row in samples[index:index + 4 + touchdown])):
            continue
        support = graph.support(start[x], start[y])
        if support is None:
            continue
        onward = graph.route(support, start[x], set())
        direction = -1 if held_direction == 1 else 1
        if (onward is None or not onward.jump or onward.direction != direction
                or abs(onward.takeoff - start[x]) > 1
                or graph.surfaces[onward.dest].y <= graph.surfaces[support].y + MINIMUM_SAFE_CLIMB):
            continue
        if any(row[scene] == START and row[grounded] and graph.support(row[x], row[y]) == onward.dest
               for row in samples[index + 4 + touchdown:]):
            continue
        return dict(step=start[step], x=start[x], y=start[y], direction=direction,
                    jumpStep=rise[step], releaseStep=release[step],
                    landingStep=landing[step], landingY=landing[y])
    return None


def missed_jump_rearm(fields, samples, graph, frontier_step):
    """Find a jump held through a narrow landing and the subsequent fall."""
    step, scene, state, available, x, y, vy, grounded, buttons, health = (
        fields.index(name) for name in
        ("step", "scene", "gameState", "heroAvailable", "x", "y", "vy", "grounded", "buttons", "health")
    )
    for index, (support, fall) in enumerate(zip(samples, samples[1:])):
        if (index < 2 or support[step] < frontier_step - 8 or not support[grounded]
                or fall[grounded] or fall[vy] >= 0 or not (support[buttons] & fall[buttons] & 32)):
            continue
        movement = fall[buttons] & 3
        if movement not in (1, 2):
            continue
        direction = -1 if movement == 1 else 1
        floor = graph.support(support[x], support[y])
        hop = graph.route(floor, support[x], set()) if floor is not None else None
        if (hop is None or not hop.jump or hop.direction != direction
                or abs(hop.takeoff - support[x]) > 1.25 or direction * (fall[x] - support[x]) < .15
                or graph.surfaces[hop.dest].y <= graph.surfaces[floor].y + MINIMUM_SAFE_CLIMB):
            continue
        # Require a real touchdown across which Jump remained held, with at
        # least one grounded decision available to release it.
        landing = index
        while (landing > 0 and index - landing < 7 and samples[landing - 1][grounded]
               and samples[landing - 1][buttons] & 32
               and graph.support(samples[landing - 1][x], samples[landing - 1][y]) == floor):
            landing -= 1
        if (landing == 0 or landing == index or samples[landing - 1][grounded]
                or not samples[landing - 1][buttons] & 32 or samples[landing - 1][vy] >= 0):
            continue
        lower = next((offset for offset in range(index + 2, min(index + 15, len(samples)))
                      if samples[offset][grounded]), None)
        if lower is None or samples[lower][y] >= support[y] - 1.25:
            continue
        if any(row[scene] != START or row[state] != "PLAYING" or not row[available] or row[health] != support[health]
               for row in samples[landing - 1:lower + 1]):
            continue
        if any(row[scene] == START and row[grounded] and graph.support(row[x], row[y]) == hop.dest
               for row in samples[lower + 1:]):
            continue
        arrived = samples[landing]
        return dict(step=arrived[step], x=arrived[x], y=arrived[y], direction=direction,
                    lostSupportStep=fall[step], landingStep=samples[lower][step], landingY=samples[lower][y])
    return None


def nearby_landings(work, end, previous=(), *, direction=None, maximum_distance=7, destination=None):
    paths = []
    for priority, source in enumerate((work, *previous)):
        for path in (source / "clean-landings").glob("*.npz"):
            x, y, landing, level, seed = map(int, path.stem.split("_"))
            dx, dy = abs(2 * x - end["x"]), abs(2 * y - end["y"])
            # The archive keys round world coordinates to two-unit bins.
            on_target = (destination is None or
                         (destination.left - 1 <= landing * 2 <= destination.right + 1
                          and abs(level * 2 - destination.y - 1.35) <= 1.25))
            if (on_target and dx <= maximum_distance and dy <= 1.6 and 100_000 <= seed < 1_000_000
                    and (direction is None or direction * (landing - x) > 0)):
                paths.append((dx + 2 * dy, priority, landing >= x, level, seed, path))
    if not paths:
        return []
    ordered = sorted(paths)
    _, _, direction, level, _, _ = ordered[0]
    examples, seen = [], set()
    for _, _, sign, height, seed, path in ordered:
        if sign != direction or abs(height - level) > 1 or seed in seen:
            continue
        seen.add(seed)
        with np.load(path, allow_pickle=False) as record:
            examples.extend(zip(record["observations"], record["actions"].tolist()))
        if len(seen) == 3:
            break
    return examples
