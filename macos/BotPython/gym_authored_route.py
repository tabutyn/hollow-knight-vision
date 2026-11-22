"""Training-only navigation between authored King's Pass surfaces using measured jump reach."""
from dataclasses import dataclass
import hashlib
import heapq
import json
import math

from gym_platform_teacher import PlatformJump


@dataclass(frozen=True)
class Surface:
    left: float
    right: float
    y: float


@dataclass(frozen=True)
class Hop:
    dest: int
    direction: int
    takeoff: float
    landing: float
    jump: bool
    steer_delay: int = 0


@dataclass(frozen=True)
class PitExit:
    bottom: Surface
    rim: Surface
    direction: int

    def at_wall(self, x, y):
        edge = self.bottom.right if self.direction > 0 else self.bottom.left
        return abs(x - edge) <= .65 and abs(y - self.bottom.y - 1.35) <= .5


def connected_pit_exits(lines, walls):
    """Both ends of the bottom must connect by authored wall corners to higher floors."""
    upper_corner = {}
    for wall in walls:
        low, high = sorted(((wall["worldY"], wall.get("startCornerID")),
                            (wall["endWorldY"], wall.get("endCornerID"))), key=lambda endpoint: endpoint[0])
        if low[1] and high[1] and .5 < high[0] - low[0]:
            upper_corner[low[1]] = high[1]
    by_start = {line["startCornerID"]: line for line in lines if line.get("startCornerID")}
    by_end = {line["endCornerID"]: line for line in lines if line.get("endCornerID")}
    def surface(line):
        return Surface(line["minimumWorldX"], line["maximumWorldX"], line["worldY"])
    exits = []
    for line in lines:
        left = by_end.get(upper_corner.get(line.get("startCornerID")))
        right = by_start.get(upper_corner.get(line.get("endCornerID")))
        if left and right:
            for direction, rim in ((-1, left), (1, right)):
                if .5 < rim["worldY"] - line["worldY"] <= 5.2:
                    exits.append(PitExit(surface(line), surface(rim), direction))
    return exits


class PitJumpPractice:
    """Rehearse one jump edge at a blocked rim and verify that the actual landing stays safe."""
    def __init__(self, exit_lip):
        self.lip = exit_lip
        self.start = self.pressed = self.health = self.landing = self.verified = None
        self.hurt = False

    def control(self, frame, action, previous):
        step, x, y = frame["step"], frame["heroX"], frame["heroY"]
        if self.start is None:
            towards_wall = 2 if self.lip.direction > 0 else 1
            if (not frame["grounded"] or abs(frame["velocityX"]) >= .75 or action[0] != towards_wall
                    or not self.lip.at_wall(x, y)):
                return None
            self.start, self.health = step, frame["health"]
            self.pressed = step + int(bool(previous[2]))
        self.hurt |= frame["health"] < self.health
        if self.hurt or self.verified is not None or step - self.start > 30:
            return None
        rim = self.lip.rim
        edge = rim.left if self.lip.direction > 0 else rim.right
        if (x - edge) * self.lip.direction <= 0 or y < rim.y + .45:
            self.landing = None
        if (self.landing is None and frame["grounded"] and rim.left + .3 < x < rim.right - .3
                and abs(y - rim.y - 1.35) <= .65):
            self.landing = step
        if self.landing is not None and step >= self.landing + 6:
            self.verified = step
        if step < self.pressed + 8:
            return int(self.pressed <= step < self.pressed + 6)
        return None


def wall_clearance_delay(takeoff, span, direction, floor, walls, flight):
    """Count 100 ms jump-only decisions needed to clear walls along this leap."""
    profiles = [(direction * wx - takeoff, bottom, top) for wx, bottom, top in walls
                if .1 < direction * wx - takeoff < span - .1]
    if not profiles:
        return 0
    for delay in range(4):
        if delay * .1 + span / 8.3 > flight:
            break
        for along, bottom, top in profiles:
            t = delay * .1 + along / 8.3
            feet = floor + 23 * t - 23.7 * t*t
            if feet + 1.2 > bottom + .15 and feet < top - .15:
                break
        else:
            return delay
    return None


class AuthoredRoute:
    def __init__(self, surfaces, walls):
        self.surfaces = surfaces
        self.pit_exits = []
        self.highest = max((s.y for s in surfaces), default=0)
        self.exit_floor = max(surfaces, key=lambda s: (round(s.y), s.right), default=None)
        self.edges = [[] for _ in surfaces]
        for i, source in enumerate(surfaces):
            for j, target in enumerate(surfaces):
                if i != j:
                    self.edges[i].extend(self.connections(source, target, j, walls))

    @classmethod
    def load(cls, path):
        contents = path.read_bytes()
        data = json.loads(contents)
        lines = [r for r in data["lines"] if r["sceneName"] == "Tutorial_01" and r["kind"] == "positive"
                 and not r.get("seededFromDetector") and r["maximumWorldX"] - r["minimumWorldX"] >= .9]
        authored_walls = [r for r in data.get("walls", [])
                          if r["sceneName"] == "Tutorial_01" and not r.get("seededFromDetector")]
        floors = [Surface(r["minimumWorldX"], r["maximumWorldX"], r["worldY"]) for r in lines]
        walls = [(r["minimumWorldX"], min(r["worldY"], r["endWorldY"]), max(r["worldY"], r["endWorldY"]))
                 for r in authored_walls]
        graph = cls(floors, walls)
        graph.pit_exits = connected_pit_exits(lines, authored_walls)
        graph.source_hash = hashlib.sha256(contents).hexdigest()
        return graph

    @staticmethod
    def connections(source, target, index, walls):
        # Fresh-game trajectories give a normal full jump of ~5.55 units in
        # ten decisions at 8.3 units/s. Allow a small clearance at the top.
        rise = target.y - source.y
        if rise > 5.2 or rise < -22:
            return
        for direction in (1, -1):
            start, stop = ((source.left, source.right) if direction > 0 else (-source.right, -source.left))
            near, far = ((target.left, target.right) if direction > 0 else (-target.right, -target.left))
            if far <= start or near > stop + 7.6:
                continue
            inset = min(1., (far - near) / 2)
            if rise < -.35:
                # Stepping off the end must put the Knight over the lower surface.
                landing = max(near + inset, stop + .9)
                if landing > far - .35:
                    continue
                takeoff = stop - .4
                jump = near > stop + .55
            else:
                landing = near + inset
                takeoff = min(stop - .35, max(start + .35, near - (2.0 + .24 * max(0, rise))))
                if takeoff >= near - .3:
                    continue
                jump = rise > .35 or near - stop > .55
            span = landing - takeoff
            # Distance available by the descending crossing of the target height.
            flight = (23 + math.sqrt(23**2 - 2 * 47.4 * rise)) / 47.4
            # A full-speed leap can establish contact at the near rim before
            # reaching the deeper target used for airborne steering.
            if jump and near > stop and span > 8.3 * flight - .25:
                takeoff = stop - .15
                span = near + .2 - takeoff
            if span <= 0 or span > 8.3 * flight - .25:
                continue
            delay = wall_clearance_delay(takeoff, span, direction, source.y, walls, flight) if jump else 0
            if delay is not None:
                yield Hop(index, direction, direction * takeoff, direction * landing, jump, delay)

    def support(self, x, y):
        candidates = [(abs(y - surface.y - 1.35), i) for i, surface in enumerate(self.surfaces)
                      if surface.left - .45 <= x <= surface.right + .45 and .45 <= y - surface.y <= 2.2]
        return min(candidates)[1] if candidates else None

    def route(self, source, x, failed, *, destination=None):
        costs, first = {source: 0.}, {}
        queue = [(0., source, x)]
        while queue:
            cost, current, at = heapq.heappop(queue)
            if cost > costs[current]:
                continue
            if current == destination:
                return first.get(current)
            for hop in self.edges[current]:
                if (current, hop) in failed:
                    continue
                price = cost + .9 + abs(at - hop.takeoff) / 8.3 + abs(hop.landing - hop.takeoff) / 8.3
                if price < costs.get(hop.dest, math.inf):
                    costs[hop.dest] = price
                    first[hop.dest] = hop if current == source else first[current]
                    heapq.heappush(queue, (price, hop.dest, hop.landing))
        if destination is not None or not first:
            return None
        # Prefer a connected climb over an isolated high ledge or a lower cul-de-sac.
        goal = max(costs, key=lambda i: (round(self.surfaces[i].y), self.surfaces[i].right - .5 * costs[i]))
        return first.get(goal)


class RoutePractice:
    def __init__(self, graph, *, destination=None):
        self.graph = graph
        self.destination = destination
        self.hop = self.motion = self.origin = None
        self.attempts, self.windows, self.landings = {}, [], []
        self.low_ceilings = set()
        self.upward_controls = None
        self.upward_strikes = 0
        self.struck_this_jump = False
        self.launch_y = None
        self.on_exit_floor = False
        self.started = 0

    def blocked_at_exit(self, frame, horizontal, stalled_steps):
        floor = self.graph.exit_floor
        return (self.on_exit_floor and floor is not None and horizontal == 2 and stalled_steps >= 3
                and frame["grounded"] and abs(frame["velocityX"]) < .6
                and abs(frame["heroY"] - floor.y - 1.35) <= .65
                and floor.right - 3 <= frame["heroX"] <= floor.right + .5)

    def controls(self, frame, previous):
        step, x = frame["step"], frame["heroX"]
        self.upward_controls = None
        support = self.graph.support(x, frame["heroY"]) if frame["grounded"] else None
        if support is not None:
            self.on_exit_floor = self.graph.surfaces[support] == self.graph.exit_floor
        final = self.graph.exit_floor
        if (self.destination is None and self.on_exit_floor and support is None and final.right - .5 <= x <= final.right + 4
                and frame["heroY"] >= final.y - .5):
            # The right transition trigger is just beyond the last authored endpoint.
            if self.windows and self.windows[-1][1] == step:
                self.windows[-1] = self.windows[-1][0], step + 1
            return 2, 0
        if self.hop is not None and support == self.hop.dest:
            self.landings.append((self.started, step, self.graph.surfaces[self.origin].y,
                                  self.graph.surfaces[self.hop.dest].y))
            self.attempts.pop((self.origin, self.hop), None)
            self.low_ceilings.discard((self.origin, self.hop))
            self.hop = None
        if self.hop is not None and (step - self.started > self.budget or
                                     support is not None and support not in (self.origin, self.hop.dest)):
            self.hop = None
        if self.hop is None:
            if support is None:
                return None
            failed = {key for key, n in self.attempts.items() if n >= (5 if key in self.low_ceilings else 2)}
            hop = self.graph.route(support, x, failed, destination=self.destination)
            if hop is None:
                floor = self.graph.surfaces[support]
                if self.destination is None and floor.y >= self.graph.highest - .6 and x < floor.right + 1.5:
                    if self.windows and self.windows[-1][1] == step:
                        self.windows[-1] = (self.windows[-1][0], step + 1)
                    else:
                        self.windows.append((step, step + 1))
                    return 2, 0
                return None
            self.hop, self.origin, self.started = hop, support, step
            previous_attempts = self.attempts.get((support, hop), 0)
            self.attempts[support, hop] = previous_attempts + 1
            self.budget = max(23, int(abs(hop.takeoff - x) / .65) + 20)
            target = self.graph.surfaces[hop.dest]
            delay = hop.steer_delay
            rise = target.y - self.graph.surfaces[support].y
            if previous_attempts and hop.jump and rise > 3:
                # A previous launch can catch the underside of an overhang.
                # Rise first on the retry, keeping enough flight time to reach the rim.
                rim = target.left if hop.direction > 0 else target.right
                to_rim = max(0, (rim - hop.takeoff) * hop.direction + .25) / 8.3
                flight = (23 + math.sqrt(23**2 - 2 * 47.4 * rise)) / 47.4
                delay = max(delay, min(delay + 2, int((flight - to_rim) * 10)))
            self.motion = PlatformJump(hop.direction, hop.takeoff, hop.landing, self.graph.surfaces[hop.dest].y,
                                       hop.landing, takeoff_tolerance=.5, steer_delay=delay)
            self.struck_this_jump = False
            self.launch_y = None
            self.windows.append((step, step))
        self.windows[-1] = (self.started, step + 1)
        if self.hop.jump:
            controls = self.motion.controls(frame, previous)
            if controls is not None and self.motion.jump_step is not None:
                phase = step - self.motion.jump_step
                if phase == 0:
                    self.launch_y = frame["heroY"]
                rise = self.graph.surfaces[self.hop.dest].y - self.graph.surfaces[self.origin].y
                key = self.origin, self.hop
                if (self.launch_y is not None and 2 <= phase <= 4 and not frame["grounded"] and previous[2]
                        and rise >= 3 and frame.get("velocityY", 5) <= 1
                        and .6 < frame["heroY"] - self.launch_y < rise - .4):
                    # A held normal jump lost its ascent before clearing the requested ledge.
                    self.low_ceilings.add(key)
                if key in self.low_ceilings and 1 <= phase <= 4:
                    press = not previous[3] and not self.struck_this_jump
                    self.upward_controls = (1, int(press))
                    if press:
                        self.struck_this_jump = True
                        self.upward_strikes += 1
            if controls is None:
                self.hop = None
            return controls
        if not frame["grounded"] and (self.hop.landing - x) * self.hop.direction < .4:
            return 0, 0
        return (2 if self.hop.direction > 0 else 1), 0
