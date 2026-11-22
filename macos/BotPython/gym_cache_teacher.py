"""A training maneuver for a visible, reachable geo deposit after nearby combat."""


class CachePractice:
    def __init__(self, schema, destination=None):
        self.indices = ([schema.index(k) for k in ("geo_cache_present", "geo_cache_dx", "geo_cache_dy")]
                        if "geo_cache_present" in schema else None)
        self.enemy_start = schema.index("enemy_0_x")
        self.windows = []
        self.last_x = None
        self.last_seen = self.last_swing = -100
        self.swings = 0
        self.destination = destination
        self.reached_destination = False

    @staticmethod
    def nearby_enemy(row, frame, enemy_start=96):
        sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
        width = frame.get("projectionPixelWidth", 0)
        if min(sx, sy, width) <= 0:
            return False
        return any(row[i + 5] and abs(row[i] * width / sx) < 6 and abs(row[i + 1] * width / sy) < 3
                   for i in range(enemy_start, enemy_start + 48, 6))

    def target(self, row, frame):
        if self.indices is None or row[self.indices[0]] < 0.5 or row[4] < 0.3:
            return None
        sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
        width = frame.get("projectionPixelWidth", 0)
        if min(sx, sy, width) <= 0:
            return None
        dx, dy = float(row[self.indices[1]]) * width / sx, float(row[self.indices[2]]) * width / sy
        if abs(dx) > 8 or abs(dy) > 2.1 or self.nearby_enemy(row, frame, self.enemy_start):
            return None
        return frame["heroX"] + dx, dy

    def controls(self, row, frame, action, previous):
        step, x = frame["step"], frame["heroX"]
        target = self.target(row, frame)
        if target and self.destination is not None:
            goal_x, goal_y = self.destination
            if abs(target[0] - goal_x) > 2 or abs(frame["heroY"] + target[1] - goal_y) > 1.5:
                target = None
        if target:
            self.last_x, dy = target
            self.last_seen = step
            self.reached_destination = True
        elif (self.last_x is None or step - self.last_seen > 5 or step - self.last_swing > 6
              or self.nearby_enemy(row, frame, self.enemy_start)):
            return None
        else:
            dy = 0.
        if not self.windows or step > self.windows[-1][1] + 1:
            self.windows.append([step, step])
        else:
            self.windows[-1][1] = step
        dx = self.last_x - x
        direction = 2 if dx > 0 else 1
        swing = bool(target and abs(dx) <= 2.1 and abs(dy) <= 1.9 and step - self.last_swing >= 5 and not previous[3])
        if swing:
            self.last_swing = step
            self.swings += 1
        result = action.copy()
        needs_turn = swing and (direction == 2) != frame["facingRight"]
        result[0] = direction if abs(dx) > (0.9 if target else 0.35) or needs_turn else 0
        result[1], result[2], result[3] = 0, 0, int(swing)
        return result
