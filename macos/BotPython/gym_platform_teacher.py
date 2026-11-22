"""Training-only approach, jump and landing using the policy's observed platform lines."""
from dataclasses import dataclass
import math


@dataclass
class PlatformJump:
    direction: int
    takeoff: float
    landing: float
    top: float
    walk_to: float
    takeoff_tolerance: float = 0.85
    jump_step: int | None = None
    landed: bool = False
    steer_delay: int = 0

    def controls(self, frame, previous):
        """Approach an exposed edge, then hold the jump and steer onto the platform."""
        step, x = frame["step"], frame["heroX"]
        if self.jump_step is None:
            gap = (self.takeoff - x) * self.direction
            if abs(gap) > self.takeoff_tolerance:
                return (2 if self.takeoff > x else 1), 0
            if not frame["grounded"]:
                return 0, 0
            self.jump_step = step + int(bool(previous[2]))
        phase = step - self.jump_step
        if phase < 0:
            # Releasing on the last safe step must not walk off the platform.
            return 0, 0
        if phase > 3 and frame["grounded"]:
            if not self.landed:
                self.landed = bool(abs(x - self.landing) < 1.5 and 0.3 <= frame["heroY"] - self.top <= 2.5)
            if self.landed and (self.walk_to - x) * self.direction > 0.5:
                return (2 if self.direction > 0 else 1), 0
            return None
        if phase > 13 or self.landed:
            return None
        waiting_to_steer = self.steer_delay > 0 and phase < self.steer_delay
        horizontal = 0 if waiting_to_steer or abs(self.landing - x) <= 0.45 else (2 if self.landing > x else 1)
        return horizontal, int(0 <= phase < 6)


def reachable_platform(observation, frame, visited, failed_landings=()):
    """Choose an unvisited landing if possible, including platforms behind the Knight."""
    if not frame["grounded"] or observation[4] < 0.5 or not observation[6]:
        return None
    sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
    width = frame.get("projectionPixelWidth", 0)
    if min(sx, sy, width) <= 0:
        return None
    from hkv_bot_dataset import COMPACT_POLICY_WIDTHS, MARKING_START, SCHEMAS
    from compact_markings import CAPACITY
    if len(observation) in COMPACT_POLICY_WIDTHS:
        legacy = len(observation) == len(SCHEMAS[13])
        capacity = 64 if legacy else CAPACITY
        slots = observation[MARKING_START:MARKING_START + 6 * capacity].reshape(capacity, 6)
        floors = [(x1, x2, y1, 1., present) for x1, y1, x2, y2, kind, present in slots if abs(float(kind) - (.25 if legacy else .33)) < 1e-6 and y1 == y2]
    else:
        floors = observation[16:96].reshape(16, 5)
    lines = [(left * width / sx, right * width / sx, dy * width / sy)
             for left, right, dy, confidence, present in floors
             if present and confidence >= 0.45 and right > left]
    height = float(observation[3]) * width / sy
    if not 0.4 <= height <= 3.5:
        return None
    support = max((dy for l, r, dy in lines if l <= 0.25 and r >= -0.25 and -2 * height <= dy <= 0),
                  default=-height)
    x, y = frame["heroX"], frame["heroY"]
    choices = []
    for left, right, dy in lines:
        rise = dy - support
        if (dy < -0.3 * height or not 0.6 * height <= rise <= min(5.2, 4.2 * height)
                or right - left < 0.65 * height):
            continue
        # Approaching from underneath a solid platform first requires backing away
        # from the edge. A narrow ledge needs a landing inside both of its endpoints.
        direction = 1 if abs(left) <= abs(right) else -1
        edge = left if direction > 0 else right
        takeoff = edge - direction * min(3.4, 1.6 + 0.28 * rise)
        if abs(takeoff) > 14:
            continue
        landing = edge + direction * min(1.5, (right - left) / 2)
        # A landing just over the lip can belong to the same exploration cell as
        # the pit. Walk onto the observed surface before handing steering back.
        room_to_walk = max(0, (right - left) - abs(landing - edge) - 0.8)
        walk_to = landing + direction * min(6, room_to_walk)
        if sum(abs(x + landing - px) < 2 and abs(y + dy - py) < 1 for px, py in failed_landings) >= 2:
            continue
        cell = (math.floor((x + landing) / 4), math.floor((y + rise) / 4))
        priority = (cell in visited, abs(takeoff) + 0.2 * rise + (0.6 if direction < 0 else 0))
        choices.append((priority, PlatformJump(direction, x + takeoff, x + landing, y + dy, x + walk_to)))
    return min(choices, key=lambda item: item[0])[1] if choices else None
