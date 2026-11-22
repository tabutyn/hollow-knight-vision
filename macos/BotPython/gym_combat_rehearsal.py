"""Train from actual damage-free nail kills made with a visible enemy in policy input."""
from collections import deque

import numpy as np


def protected_landing(samples, fields, strikes, hit):
    """A downward strike at the previous collision followed by a safe landing beyond it."""
    if not hit or not hit.get("descendingOnVisibleEnemy"):
        return None
    x, y, vx, hp, grounded = [fields.index(k) for k in ("x", "y", "vx", "health", "grounded")]
    for start, end in strikes:
        origin = samples[start]
        if (abs(origin[x] - hit["x"]) > 5 or not -1 < origin[y] - hit["y"] < 7 or
                origin[hp] != samples[0][hp] or abs(origin[vx]) < 1):
            continue
        direction = 1 if origin[vx] > 0 else -1
        for landing in range(end, min(len(samples) - 5, end + 26)):
            if min(row[hp] for row in samples[start:landing + 6]) < origin[hp]:
                break
            if samples[landing][grounded] and (samples[landing][x] - hit["x"]) * direction >= 2:
                return start, end, landing
    return None


def sees_opponent(row, frame, approaching=0, *, approach_distance=4.6):
    """Verify a policy-visible enemy slot against the same projected enemy."""
    actors = frame.get("actors") or {}
    knight = actors.get("hero")
    width = frame.get("projectionPixelWidth", 0)
    sx, sy = frame.get("pixelsPerWorldUnitX", 0), frame.get("pixelsPerWorldUnitY", 0)
    if not knight or min(sx, sy, width) <= 0:
        return False
    for enemy in actors.get("enemies", []):
        dx, dy = enemy["screenX"] - knight["screenX"], enemy["screenY"] - knight["screenY"]
        if approaching and (not frame.get("grounded") or not 1 <= approaching * dx / sx <= approach_distance
                            or abs(dy / sy) > 1.2):
            continue
        if abs(dx) <= 8 * sx and abs(dy) <= 4 * sy:
            from hkv_bot_dataset import COMPACT_POLICY_WIDTHS, ENEMY_START
            start = ENEMY_START if len(row) in COMPACT_POLICY_WIDTHS else 96
            for i in range(start, start + 48, 6):
                if row[i + 5] and abs(row[i] * width - dx) <= 1.5 * sx and abs(row[i + 1] * width - dy) <= 1.5 * sy:
                    return True
    return False


def protected_approach(samples, fields, strikes, hit):
    """Require a real nail kill at the previous collision and six unharmed decisions after it."""
    if not hit or not hit.get("approachingVisibleEnemy"):
        return None
    x, y, hp, kills = [fields.index(k) for k in ("x", "y", "health", "enemyKills")]
    for start, end in strikes:
        origin = samples[start]
        if (origin[kills] is None or abs(origin[x] - hit["x"]) > 6 or
                abs(origin[y] - hit["y"]) > 2 or origin[hp] != samples[0][hp]):
            continue
        for step in range(start + 1, min(end + 6, len(samples) - 6)):
            if (samples[step][kills] is not None and samples[step][kills] > origin[kills]
                    and all(r[hp] == origin[hp] for r in samples[start:step + 7])):
                return start, end, step
    return None


class CombatRehearsal:
    def __init__(self):
        self.history = deque(maxlen=11)
        self.pending = []
        self.confirmed = []

    def record(self, step, observation, row, applied, frame, hurt, killed):
        if hurt or observation is None:
            self.history.clear()
            self.pending.clear()
            return
        self.history.append((observation, tuple(applied), sees_opponent(row, frame)))
        if (killed and len(self.history) >= 5 and sum(visible for _, _, visible in self.history) >= 2
                and any(action[3] for _, action, _ in self.history)):
            controls = [(obs, action) for obs, action, _ in self.history]
            self.pending.append((step + 6, (frame["heroX"], frame["heroY"], controls)))
        while self.pending and self.pending[0][0] <= step:
            self.confirmed.append(self.pending.pop(0)[1])


def save_combat(work, seed, examples):
    if not 100_000 <= seed < 1_000_000 or not examples:
        return
    folder = work / "confirmed-combat"
    folder.mkdir(exist_ok=True)
    for x, y, controls in examples:
        path = folder / f"{round(x / 4)}_{round(y / 3)}_{seed}.npz"
        if path.exists():
            continue
        temporary = path.with_suffix(".tmp")
        with temporary.open("wb") as stream:
            np.savez_compressed(stream, observations=np.stack([r for r, _ in controls]),
                                actions=np.asarray([a for _, a in controls], dtype=np.int8))
        temporary.replace(path)


def nearby_combat(work, damage, previous=()):
    if damage is None:
        return []
    choices = []
    for priority, source in enumerate((work, *previous)):
        for path in (source / "confirmed-combat").glob("*.npz"):
            zone, level, seed = map(int, path.stem.split("_"))
            dx, dy = abs(zone * 4 - damage["x"]), abs(level * 3 - damage["y"])
            if dx <= 18 and dy <= 4 and 100_000 <= seed < 1_000_000:
                choices.append((dx + 2 * dy, priority, seed, path))
    examples, seen = [], set()
    for _, _, seed, path in sorted(choices):
        if seed in seen:
            continue
        seen.add(seed)
        with np.load(path, allow_pickle=False) as record:
            examples.extend(zip(record["observations"], record["actions"].tolist()))
        if len(seen) == 3:
            break
    return examples
