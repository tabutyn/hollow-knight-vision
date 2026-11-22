"""Fit a local response without changing the actor on histories with no blockade."""
import copy
from collections import defaultdict
import gzip
import json

import numpy as np
import torch
from torch import nn
from torch.nn import functional as F
from hkv_bot_dataset import ACTION_SIZES


def opening_steps(trajectory):
    """Identify a stationary nail press followed by walking safely through the contact."""
    fields = trajectory["fields"]
    samples = trajectory["samples"]
    cols = [fields.index(k) for k in ("x", "y", "vx", "grounded", "health", "buttons")]
    points = [tuple(row[i] for i in cols) for row in samples]
    enemy = (fields.index("enemyDX"), fields.index("enemyDY")) if "enemyDX" in fields and "enemyDY" in fields else None
    deposit = fields.index("depositHits") if "depositHits" in fields else None
    for i in range(len(points) - 5):
        x, y, vx, grounded, hp, buttons = points[i]
        follow = points[i + 1:i + 6]
        pressed = follow[0][-1]
        direction = {1: -1, 2: 1}.get(pressed & 3)
        if (not grounded or abs(vx) > .6 or buttons & 64 or not pressed & 64 or
                not direction or buttons & 3 != pressed & 3):
            continue
        if enemy and any(row[enemy[0]] is not None and row[enemy[1]] is not None and
                         abs(row[enemy[0]]) <= 4.5 and abs(row[enemy[1]]) <= 2 for row in samples[i:i + 2]):
            continue
        if deposit is not None and samples[i][deposit] != samples[i + 5][deposit]:
            continue
        # The strike and measured travel prove the passage even if the actor
        # briefly releases steering while recovering from the swing.
        if (all(g and life == hp and abs(py - y) < .55 and not btn & 32 and (btn & 3) in (0, pressed & 3)
                for _, py, _, g, life, btn in follow)
                and (follow[-1][0] - x) * direction > 2.5):
            yield i, (round(x, 1), round(y, 1), direction)


def opened_barriers(trajectory):
    return (contact for _, contact in opening_steps(trajectory))


def confirmed_strikes(trajectory, route):
    """Keep the native observations for actual presses and releases at an opened barrier."""
    fields, samples = trajectory["fields"], trajectory["samples"]
    steps, buttons = fields.index("step"), fields.index("buttons")
    decisions = {step: (obs, action) for step, obs, action in route}
    lessons = []
    for index, contact in opening_steps(trajectory):
        controls = []
        for j in range(max(0, index - 1), index + 4):
            item = decisions.get(samples[j][steps])
            if item is not None and bool(item[1][3]) == bool(samples[j + 1][buttons] & 64):
                controls.append(item)
        if {action[3] for _, action in controls} == {0, 1}:
            lessons.append((contact, controls))
    return lessons


def save_wall_strikes(work, seed, lessons):
    if not 100_000 <= seed < 1_000_000:
        return
    for (x, y, direction), controls in lessons:
        folder = work / "confirmed-wall-strikes" / f"{x}_{y}_{direction}"
        folder.mkdir(parents=True, exist_ok=True)
        path = folder / f"{seed}.npz"
        if path.exists():
            continue
        temporary = path.with_suffix(".tmp")
        with temporary.open("wb") as stream:
            np.savez_compressed(stream, seed=seed, observations=np.stack([o for o, _ in controls]),
                                actions=np.asarray([a for _, a in controls], dtype=np.int8))
        temporary.replace(path)


def nearby_wall_strikes(work, end, *, direction=None):
    """Read measured press and release decisions around this candidate's obstruction."""
    choices = []
    for folder in (work / "confirmed-wall-strikes").glob("*"):
        x, y, side = map(float, folder.name.split("_"))
        dx, dy = abs(x - end["x"]), abs(y - end["y"])
        if dx <= 2 and dy <= .65 and (direction is None or side == direction):
            choices.extend((dx + 2 * dy, path) for path in folder.glob("*.npz"))
    rows, seeds = [], set()
    for _, path in sorted(choices):
        seed = int(path.stem)
        if seed in seeds or not 100_000 <= seed < 1_000_000:
            continue
        with np.load(path, allow_pickle=False) as lesson:
            rows.extend(zip(lesson["observations"], lesson["actions"].tolist()))
        seeds.add(seed)
        if len(seeds) == 3:
            break
    return rows


def repeated_openings(root, identity):
    """Share physical outcomes across matching mod builds, using only training starts."""
    observed = defaultdict(set)
    for state in root.glob("*/state.json"):
        other = json.loads(state.read_bytes()).get("identity", {})
        if other.get("receiver") != identity["receiver"]:
            continue
        trails = state.parent / "trajectories"
        for path in trails.glob("*.json.gz"):
            seed = int(path.name.split("-", 1)[0])
            if not 100_000 <= seed < 1_000_000:
                continue
            with gzip.open(path) as handle:
                for contact in opened_barriers(json.load(handle)):
                    observed[contact].add(seed)
    return {contact for contact, seeds in observed.items() if len(seeds) >= 2}


def at_opening(x, y, direction, openings):
    return any(direction == d and abs(x - px) <= .45 and abs(y - py) <= .55 for px, py, d in openings)


def stalled_strikes(contacts, openings):
    """Label the learner's own contacts where independent nail trials opened the passage."""
    examples = []
    for x, y, direction, obs, action, previous_attack in contacts:
        if at_opening(x, y, direction, openings):
            desired = list(action)
            desired[3] = int(not previous_attack)
            examples.append((obs, desired))
    return examples


def human_strike_edges(archive, sessions, schema):
    # These releases also cover histories where the wall cue outlasts the obstruction.
    indices = np.flatnonzero(np.isin(archive["session_ids"], sessions))
    inputs, actions = archive["observations"][indices], archive["actions"][indices]
    history = inputs.reshape(len(inputs), -1, len(schema))
    marked = np.any(history[:, :, schema.index("blockade_direction")] != 0, axis=1)
    edge = history[:, -1, schema.index("previous_attack")] != actions[:, 3]
    positions = np.flatnonzero(marked & edge)
    if len(positions) > 48:
        positions = positions[np.linspace(0, len(positions) - 1, 48, dtype=int)]
    return [(inputs[i], actions[i].tolist()) for i in positions]


def measured_strikes(work, schema):
    if "blockade_direction" not in schema:
        return []
    seeds, rows = set(), []
    paths = sorted((work / "confirmed-wall-strikes").rglob("*.npz"))
    for path in paths + sorted((work / "demonstrated-crossings").rglob("*.npz")):
        with np.load(path, allow_pickle=False) as bank:
            seed = int(bank["seed"])
            if not 100_000 <= seed < 1_000_000 or seed in seeds:
                continue
            actions, inputs = bank["actions"], bank["observations"]
            history = inputs.reshape(len(inputs), -1, len(schema))
            marked = np.any(history[:, :, schema.index("blockade_direction")] != 0, axis=1)
            positions = np.flatnonzero(marked)
            if not np.any((actions[:, 3] == 1) & (history[:, -1, 12] == 0) & marked):
                continue
            seeds.add(seed)
            positions = positions[np.linspace(0, len(positions) - 1, min(32, len(positions)), dtype=int)]
            rows.extend((inputs[i], actions[i].tolist()) for i in positions)
    return rows if len(seeds) >= 2 else []


def route_strike_edges(routes, openings, schema):
    """Recover presses and releases that opened several independently confirmed passages."""
    if not openings or "blockade_direction" not in schema:
        return []
    mark = schema.index("blockade_direction")
    examples, confirmed = [], set()
    for path in routes:
        with np.load(path, allow_pickle=False) as data:
            states, actions, observations = data["states"], data["actions"], data["observations"]
        frames = observations.reshape(len(states), -1, len(schema))
        edges = np.flatnonzero((actions[:, 3] == 1) & (frames[:, -1, schema.index("previous_attack")] == 0))
        selected = set()
        for x, y, direction in openings:
            for i in edges:
                if (abs(states[i, 1] - x) > 1.6 or abs(states[i, 2] - y) > .65 or
                        actions[i, 0] != (2 if direction > 0 else 1)):
                    continue
                follow = states[i:min(i + 11, len(states))]
                passed = np.flatnonzero((follow[:, 1] - x) * direction > 2.5)
                if not len(passed):
                    continue
                end = int(passed[0])
                if follow[end, 0] != states[i, 0] + end or np.any(abs(follow[:end + 1, 2] - y) > .7):
                    continue
                confirmed.add((x, y, direction))
                selected.update(j for j in range(max(0, i - 2), min(i + end + 1, i + 5))
                                if np.any(frames[j, :, mark] != 0))
                break
        examples.extend((observations[i], actions[i].tolist()) for i in sorted(selected))
    return examples if len(confirmed) >= 2 else []


def learn_blockade_response(parent, examples, schema, expert_inputs):
    """Fit a wall-conditioned nail edge and preserve the inherited steering, aim and jump."""
    return learn_contact_response(parent, examples, schema, expert_inputs, head=3, previous="previous_attack")


def learn_climb_response(parent, examples, schema, expert_inputs):
    """Fit jump controls at the start of real upward landings while preserving combat."""
    if "road_dy" not in schema or "blockade_direction" not in schema:
        return None
    wall = schema.index("blockade_direction") - len(schema)
    climb = schema.index("road_dy") - len(schema)
    supported = [(obs, action) for obs, action in examples if abs(obs[wall]) > .5 and obs[climb] >= .65]
    return learn_contact_response(parent, supported, schema, expert_inputs,
                                  head=2, previous="previous_jump", ascent=climb)


def learn_contact_response(parent, examples, schema, expert_inputs, *, head, previous, ascent=None):
    policy = parent.policy
    actor = policy.mlp_extractor.policy_net
    if (len(actor) != 4 or not isinstance(actor[0], nn.Linear) or not isinstance(actor[2], nn.Linear)
            or not all(isinstance(actor[i], nn.Tanh) for i in (1, 3))
            or len({action[head] for _, action in examples}) != 2):
        return None
    # Each basis state distinguishes the wall side and the previous two levels
    # of the button, so releasing and pressing can have different effects.
    offset = sum(ACTION_SIZES[:head])
    n = len(schema)
    last = actor[0].in_features - n
    if last < n or max(actor[0].out_features, actor[2].out_features) + 16 > 512:
        return None
    wall = last + schema.index("blockade_direction")
    direction = last + schema.index("previous_horizontal")
    button = last + schema.index(previous)
    inputs = torch.as_tensor(np.stack([obs for obs, _ in examples]))
    labels = torch.tensor([action[head] for _, action in examples], dtype=torch.long)
    anchors = torch.as_tensor(expert_inputs)
    states = torch.cat((inputs, anchors))
    templates = [(side, current, earlier) for side in (-1, 1) for current in (-1, 1) for earlier in (-1, 1)]
    visible = states.new_zeros(len(templates), states.shape[1])
    bias = states.new_empty(len(templates))
    for i, (side, current, earlier) in enumerate(templates):
        # A match needs the Knight pushing toward this marked wall and both
        # button levels in agreement. Center the binary button inputs on zero.
        visible[i, wall] = visible[i, direction] = 6 * side
        visible[i, button], visible[i, button - n] = 12 * current, 12 * earlier
        bias[i] = 6 * (-3.5 - current - earlier)
        if ascent is not None:
            visible[i, ascent] = 14
            bias[i] -= 10
    cleared = visible.clone()
    cleared[:, wall] = 0
    with torch.no_grad():
        def basis(weight):
            return torch.tanh(torch.tanh(F.linear(states, weight, bias)))
        signals = basis(visible) - basis(cleared)
        original = policy.action_net(actor(states))
        margins = original[:, offset + 1] - original[:, offset]
    gains = states.new_zeros(len(templates), requires_grad=True)
    optimizer = torch.optim.Adam([gains], lr=.12)
    weights = len(labels) / (2 * labels.bincount(minlength=2).float())
    signed = 2 * labels.float() - 1
    for _ in range(240):
        delta = signals @ gains
        loss = (weights[labels] * F.softplus(1.5 - signed * (margins[:len(inputs)] + delta[:len(inputs)]))).mean()
        if len(anchors):
            held = delta[len(inputs):]
            loss += F.smooth_l1_loss(held, torch.zeros_like(held))
        loss += .0006 * gains.square().mean()
        optimizer.zero_grad()
        loss.backward()
        optimizer.step()
    if not torch.isfinite(gains).all():
        return None

    # Fold the difference into ordinary Linear/Tanh layers for native export.
    # With no wall cue, the two branches cancel. During contact, only the two
    # selected logits can change.
    child = copy.deepcopy(parent)
    fitted = child.policy
    layers = fitted.mlp_extractor.policy_net
    with torch.no_grad(), torch.random.fork_rng(devices=[]):
        def linear(weight, bias):
            layer = nn.Linear(weight.shape[1], weight.shape[0])
            layer.weight.copy_(weight)
            layer.bias.copy_(bias)
            return layer
        first, second, output = layers[0], layers[2], fitted.action_net
        layers[0] = linear(torch.cat((first.weight, visible, cleared)), torch.cat((first.bias, bias, bias)))
        extra = len(templates) * 2
        layers[2] = linear(torch.block_diag(second.weight, torch.eye(extra)), torch.cat((second.bias, torch.zeros(extra))))
        correction = output.weight.new_zeros(output.out_features, len(templates))
        correction[offset], correction[offset + 1] = -.5 * gains, .5 * gains
        fitted.action_net = linear(torch.cat((output.weight, correction, -correction), dim=1), output.bias)
    fitted.mlp_extractor.latent_dim_pi = layers[2].out_features
    original_arch = child.policy_kwargs["net_arch"]
    arch = dict(original_arch) if isinstance(original_arch, dict) else {"vf": list(original_arch)}
    arch["pi"] = [layers[0].out_features, layers[2].out_features]
    child.policy_kwargs["net_arch"] = fitted.net_arch = arch
    old_optimizer = fitted.optimizer
    fitted.optimizer = fitted.optimizer_class(fitted.parameters(), lr=old_optimizer.param_groups[0]["lr"],
                                              **fitted.optimizer_kwargs)
    for parameter in fitted.parameters():
        if parameter in old_optimizer.state:
            fitted.optimizer.state[parameter] = old_optimizer.state[parameter]
    return child
