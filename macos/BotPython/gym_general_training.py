"""On-policy PPO practice in real rooms, using only the normal bot observation and controls."""
from collections import deque
import json
import random

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.policies import ActorCriticPolicy, BasePolicy
import torch

from gym_authored_route import AuthoredRoute, RoutePractice
from gym_blockade_response import confirmed_strikes, save_wall_strikes
from gym_combat_rehearsal import CombatRehearsal, save_combat
from gym_landing_rehearsal import LandingRehearsal, save_landings
from hacker_gym import Dwell, NEUTRAL, START, buttons
from hkv_bot_dataset import ACTION_SIZES, HISTORY_LENGTH, SCHEMAS


def practice_kind(batch, episode, has_routes):
    encounters = ("spawn", "blocked", "enemy", "lower landing", "frontier")
    if not has_routes:
        return encounters[(batch + episode) % len(encounters)]
    if batch % 2:
        cycle = (*encounters, "route")
        return cycle[(batch + episode) % len(cycle)]
    # After two full 256-decision windows, SB3 can reset a route and collect
    # no samples from it. Reserve the opening window in alternating batches.
    if episode == 0:
        return "route"
    if episode == 1:
        return "spawn"
    return encounters[1 + (batch // 2 + episode - 2) % (len(encounters) - 1)]


class PracticeStarts:
    """Keep replays of real training actions that reached useful states."""
    def __init__(self, file):
        self.file = file
        self.starts = json.loads(file.read_text()) if file and file.is_file() else {}
        self.route = []
        if file is not None:
            paths = sorted((file.parent / "route-starts").glob("*.json"),
                           key=lambda path: path.stat().st_mtime_ns, reverse=True)
            for path in paths[:8]:
                self.route.extend(json.loads(path.read_text()))
            retained = file.parent / "retained-route-starts.json"
            if retained.is_file():
                # Older verified course segments remain available as new
                # shorter attempts replace the recent replay list.
                for site in json.loads(retained.read_text()):
                    if site not in self.route:
                        self.route.append(site)

    @staticmethod
    def priority(kind, rank):
        health, progress, duration = rank
        # Keep later frontiers once the Knight can reach them in good enough
        # health to practice. Preserve health-first starts for individual skills.
        return (progress, health, duration) if kind == "frontier" else (health, progress, duration)

    def remember(self, kind, seed, actions, frame, progress, *, checkpoints=()):
        if not actions or frame["health"] < 3 or frame["sceneName"] != "Tutorial_01":
            return
        x, y = frame["heroX"], frame["heroY"]
        cell = f"{round(x / 4)},{round(y / 4)}"
        slots = self.starts.setdefault(kind, {})
        prior = slots.get(cell)
        rank = (frame["health"], progress, -len(actions))
        if prior and self.priority(kind, prior["rank"]) >= self.priority(kind, rank):
            return
        slots[cell] = dict(seed=seed, actions=actions.copy(), x=x, y=y, health=frame["health"],
                           grounded=frame["grounded"], rank=rank)
        if checkpoints:
            slots[cell]["checkpoints"] = list(checkpoints)
        if frame.get("renderHz") in (60, 120):
            slots[cell]["renderHz"] = frame["renderHz"]
        if len(slots) > 16:
            del slots[min(slots, key=lambda key: self.priority(kind, slots[key]["rank"]))]

    def choose(self, kind, generator, limit, avoid_seeds=()):
        entries = self.route if kind == "route" else self.starts.get(kind, {}).values()
        choices = [item for item in entries if len(item["actions"]) <= limit]
        if kind == "frontier" and choices and generator.random() < .75:
            choices.sort(key=lambda item: self.priority(kind, item["rank"]), reverse=True)
            choices = choices[:max(1, len(choices) // 4)]
        unused = [item for item in choices if item["seed"] not in avoid_seeds]
        choices = unused or choices
        if kind == "route" and choices:
            by_seed = {}
            for item in choices:
                by_seed.setdefault(item["seed"], []).append(item)
            choices = generator.choice(list(by_seed.values()))
            if len(choices) > 1 and generator.random() < .65:
                choices = sorted(choices, key=lambda item: len(item["actions"]))[len(choices) // 2:]
        return generator.choice(choices) if choices else None

    def save(self):
        if self.file:
            staged = self.file.with_suffix(".tmp")
            staged.write_text(json.dumps(self.starts, allow_nan=False) + "\n")
            staged.replace(self.file)


class PracticeLessons:
    """Retain live actions with a measured local outcome, even in unfinished episodes."""
    fields = ("step", "x", "y", "vx", "grounded", "health", "buttons", "enemyDX", "enemyDY", "depositHits")

    def __init__(self, work, seed, frame, previous, kills, deposits):
        self.work, self.seed = work, seed
        self.combat = CombatRehearsal()
        self.landing = LandingRehearsal()
        self.kills = kills
        self.route = []
        self.samples = [self.point(frame, previous, deposits)]

    @staticmethod
    def point(frame, action, deposits):
        actors = frame.get("actors") or {}
        hero = actors.get("hero")
        width = frame.get("pixelsPerWorldUnitX", 0)
        height = frame.get("pixelsPerWorldUnitY", 0)
        relative = []
        if hero and min(width, height) > 0:
            relative = [((enemy["screenX"] - hero["screenX"]) / width,
                         (enemy["screenY"] - hero["screenY"]) / height) for enemy in actors.get("enemies", [])]
        dx, dy = min(relative, key=lambda item: item[0] ** 2 + item[1] ** 2, default=(None, None))
        return (frame["step"], frame["heroX"], frame["heroY"], frame["velocityX"], frame["grounded"],
                frame["health"], buttons(action), dx, dy, deposits)

    def record(self, observation, row, action, before, after, kills, deposits):
        if observation is not None:
            self.route.append((before["step"], observation, action))
        self.combat.record(before["step"], observation, row, action, before,
                           after["damage"] > before["damage"], kills > self.kills)
        self.landing.record(before["step"], observation, action, before, after)
        self.kills = kills
        self.samples.append(self.point(after, action, deposits))

    def save(self):
        save_combat(self.work, self.seed, self.combat.confirmed)
        save_landings(self.work, self.seed, self.landing.confirmed)
        lessons = confirmed_strikes(dict(fields=self.fields, samples=self.samples), self.route)
        save_wall_strikes(self.work, self.seed, lessons)
        return dict(combat=len(self.combat.confirmed), landings=len(self.landing.confirmed), wallStrikes=len(lessons))


class EncounterPractice(gym.Wrapper):
    """Mix full starts with naturally reached encounters. Prefix decisions never enter PPO's buffer."""
    def __init__(self, env, opening_policy, batch, log, starts=None, lesson_folder=None, route_graph=None):
        super().__init__(env)
        self.opening_policy = opening_policy
        self.batch = batch
        self.batch_steps = 0
        self.batch_step_limit = None
        self.generator = random.Random(batch)
        self.seeds = []
        self.practice_frames = {}
        self.log = log
        self.starts = starts if starts is not None else PracticeStarts(None)
        self.lesson_folder = lesson_folder
        self.lessons = None
        self.route_graph = route_graph
        schema = SCHEMAS[env.version]
        self.feature_count = len(schema)
        self.enemy = [schema.index(key) - len(schema) for key in ("enemy_0_present", "enemy_0_x", "enemy_0_y")]
        self.wall = schema.index("blockade_direction") - len(schema) if "blockade_direction" in schema else None

    def encounters(self, observation, stationary, horizontal):
        frame = self.env.last_frame
        present, dx, dy = observation[self.enemy]
        return {
            "enemy": present > .5 and abs(dx) <= .35 and abs(dy) <= .35,
            "lower landing": frame["grounded"] and any(y - frame["heroY"] >= 1.5 for y in self.support if y is not None),
            "blocked": frame["grounded"] and bool(horizontal) and stationary >= 8,
        }

    def track_support(self):
        frame = self.env.last_frame
        self.support.append(frame["heroY"] if frame["grounded"] else None)
        step = frame["step"]
        if step and step % 24 == 0 and frame["sceneName"] == START:
            self.checkpoints.append((step, frame["heroX"], frame["heroY"], frame["health"]))

    def start_lessons(self, seed):
        if self.lesson_folder is not None:
            self.lessons = PracticeLessons(self.lesson_folder, seed, self.env.last_frame, [0, 0, 0, 0],
                                           self.env.score.enemy_kills, self.env.score.deposit_hits)

    def record_lesson(self, before, observed, info):
        if self.lessons is not None:
            row = observed[-self.feature_count:] if observed is not None else None
            self.lessons.record(observed, row, info["applied"], before, self.env.last_frame,
                                self.env.score.enemy_kills, self.env.score.deposit_hits)

    def recover_route_start(self, observation, info, target):
        """Navigate to a real nearby platform when an open-loop replay drifts."""
        destination = self.route_graph.support(target["x"], target["y"])
        guide = RoutePractice(self.route_graph, destination=destination)
        initial_step = self.env.last_frame["step"]
        limit = min(initial_step + 180, self.env.max_steps - self.horizon)
        while self.env.last_frame["step"] < limit and not self.env.ended:
            frame = self.env.last_frame
            if frame["sceneName"] != START or frame["health"] < 3:
                break
            support = self.route_graph.support(frame["heroX"], frame["heroY"]) if frame["grounded"] else None
            if (frame["grounded"] and (destination is None or support == destination)
                    and abs(frame["heroX"] - target["x"]) <= 5
                    and abs(frame["heroY"] - target["y"]) <= 1.2):
                return observation, info, True
            previous = self.actions[-1] if self.actions else NEUTRAL
            action, _ = self.opening_policy.predict(observation, deterministic=True)
            observed = observation if self.env.usable else None
            if observed is not None:
                steering = guide.controls(frame, previous)
                if destination is not None and support == destination:
                    steering = (2 if frame["heroX"] < target["x"] else 1), 0
                if steering is not None:
                    action = action.copy()
                    action[0], action[2] = steering
                    sign = {1: -1, 2: 1}.get(steering[0], 0)
                    if (sign and self.wall is not None and observed[self.wall] == sign
                            and frame["grounded"] and abs(frame["velocityX"]) < .6):
                        action[3] = int(frame["step"] % 5 == 0 and not previous[3])
                    if guide.upward_controls is not None:
                        action[1], action[3] = guide.upward_controls
            observation, _, _, _, info = self.env.step(action)
            self.record_lesson(frame, observed, info)
            self.actions.append(info["applied"])
            self.track_support()
        return observation, info, False

    def reset(self, *, seed=None, options=None):
        # SB3 seeds its vector environment itself. Keep every training episode in
        # the training split, including its automatic resets inside collect_rollouts.
        episode_seed = self.generator.randrange(300_000, 980_000)
        batch_finished = self.batch_step_limit is not None and self.batch_steps >= self.batch_step_limit
        # A genuine terminal can still make the vector env reset on the final
        # step. Give it a fresh observation without replaying a practice prefix.
        kind = "spawn" if batch_finished else practice_kind(self.batch, len(self.seeds), bool(self.starts.route))
        position_start = kind in ("frontier", "route")
        self.horizon = 256 if kind == "spawn" or position_start else 128
        # Regular fresh starts remain part of the PPO data. Other starts can
        # replay an earlier learner's discovery on its original training seed.
        replay = self.starts.choose(kind, self.generator, self.env.max_steps - self.horizon, self.seeds)
        if replay:
            episode_seed = replay["seed"]
        if not batch_finished:
            self.seeds.append(episode_seed)
        replay_options = {"render_hz": replay["renderHz"]} if replay and "renderHz" in replay else None
        observation, info = self.env.reset(seed=episode_seed, options=replay_options)
        self.start_lessons(episode_seed)
        frame = self.env.last_frame
        dwell = Dwell(frame["heroX"], frame["heroY"])
        self.support = deque(maxlen=24)
        self.actions = []
        self.checkpoints = []
        self.track_support()
        reached = kind == "spawn"
        replayed = False
        replay_interruption = None
        repaired = False
        if not reached and (not position_start or replay):
            replay_length = len(replay["actions"]) if replay else 0
            checkpoints = {row[0]: row[1:] for row in replay.get("checkpoints", [])} if replay else {}
            opening_limit = replay_length + (0 if position_start else 250)
            for step in range(min(opening_limit, self.env.max_steps - 1)):
                if step < replay_length:
                    action = replay["actions"][step]
                else:
                    action, _ = self.opening_policy.predict(observation, deterministic=kind == "blocked")
                observed = observation if self.env.usable else None
                before = self.env.last_frame
                observation, _, done, cut, info = self.env.step(action)
                self.record_lesson(before, observed, info)
                frame = self.env.last_frame
                if done or cut:
                    # PPO starts over. Independently verified local outcomes
                    # can still supply rehearsal outside the on-policy buffer.
                    if replay and step < replay_length:
                        replay_interruption = dict(step=frame["step"], x=frame["heroX"], y=frame["heroY"],
                                                   reason=info["reason"])
                    self.save_lessons()
                    observation, info = self.env.reset(seed=episode_seed, options=replay_options)
                    self.start_lessons(episode_seed)
                    self.support.clear()
                    self.actions.clear()
                    self.checkpoints.clear()
                    self.track_support()
                    break
                self.actions.append(info["applied"])
                stationary = dwell.advance(frame["heroX"], frame["heroY"], False)
                encountered = self.encounters(observation, stationary, info["applied"][0])
                self.track_support()
                reference = checkpoints.get(step + 1)
                if reference is not None:
                    x, y, health = reference
                    if abs(x - frame["heroX"]) > 4 or abs(y - frame["heroY"]) > 2.5 or frame["health"] < health - 1:
                        replay_interruption = dict(step=frame["step"], x=frame["heroX"], y=frame["heroY"],
                                                   reason="diverged")
                        break
                if step + 1 < replay_length:
                    continue
                matched = (replay and step + 1 == replay_length and replay["grounded"] == frame["grounded"]
                           and abs(replay["x"] - frame["heroX"]) <= 2 and abs(replay["y"] - frame["heroY"]) <= 1
                           and frame["health"] >= replay["health"])
                if (position_start and matched) or encountered.get(kind, False):
                    reached = True
                    replayed = bool(matched)
                    break
                if position_start and replay and step + 1 == replay_length:
                    replay_interruption = dict(step=frame["step"], x=frame["heroX"], y=frame["heroY"],
                                               reason="diverged")
        if (position_start and replay_interruption and replay_interruption["reason"] == "diverged"
                and self.route_graph is not None):
            observation, info, repaired = self.recover_route_start(observation, info, replay)
            reached = repaired
            if self.env.ended:
                self.save_lessons()
                observation, info = self.env.reset(seed=episode_seed, options=replay_options)
                self.start_lessons(episode_seed)
                self.support.clear()
                self.actions.clear()
                self.checkpoints.clear()
                self.track_support()
        frame = self.env.last_frame
        self.seed_used, self.learner_steps = episode_seed, 0
        self.combat_steps = 0
        # Setup decisions keep their real outcomes but cannot use the learner's stall allowance.
        self.env.last_control_gap = frame["step"]
        self.env.score.dwell = Dwell(frame["heroX"], frame["heroY"])
        self.score_at_handoff = self.env.score.metrics()
        self.record(dict(event="reset", batch=self.batch, seed=episode_seed, requested=kind, reached=reached,
                         replayAttempted=bool(replay), replayed=replayed, replayInterrupted=replay_interruption,
                         routeRecovered=repaired,
                         setupSteps=frame["step"], x=frame["heroX"], y=frame["heroY"]))
        self.previous_observation = observation
        return observation, info

    def record(self, row):
        with self.log.open("a") as stream:
            stream.write(json.dumps(row, allow_nan=False) + "\n")

    def episode_metrics(self):
        metrics = self.env.score.metrics()
        for name in ("damage", "enemyDamage", "enemyKills", "heldAttackDecisions"):
            metrics[name] -= self.score_at_handoff[name]
        return dict(batch=self.batch, seed=self.seed_used, steps=self.learner_steps, combatSteps=self.combat_steps, **metrics)

    def step(self, action):
        if self.learner_steps % 8 == 0 and self.env.usable:
            self.practice_frames.setdefault(self.seed_used, []).append(self.previous_observation.copy())
        before, observed = self.env.last_frame, self.previous_observation if self.env.usable else None
        observation, reward, done, cut, info = self.env.step(action)
        self.record_lesson(before, observed, info)
        self.previous_observation = observation
        self.learner_steps += 1
        self.batch_steps += 1
        self.actions.append(info["applied"])
        idle = self.env.score.dwell.steps
        if self.env.usable:
            frame = self.env.last_frame
            progress = self.env.score.path_progress
            encounters = self.encounters(observation, idle, info["applied"][0])
            self.combat_steps += int(encounters["enemy"])
            encounters["frontier"] = (frame["grounded"] and self.env.score.last_progress == frame["step"]
                                      and progress >= self.score_at_handoff["pathProgress"] + 5)
            if not (done or cut):
                for kind, present in encounters.items():
                    if present:
                        self.starts.remember(kind, self.seed_used, self.actions, frame, progress,
                                             checkpoints=self.checkpoints)
        self.track_support()
        if self.env.usable and idle > 12:
            reward -= 0.05
        if info["reason"] == "stalled":
            # This is a failed training outcome. Time-limit truncation would
            # bootstrap the critic at exactly the dead end we want it to leave.
            done, cut = True, False
        has_more_training = self.batch_step_limit is None or self.batch_steps < self.batch_step_limit
        # PPO already bootstraps the value of the final observation at the
        # batch boundary. A curriculum truncation here would reset the game.
        if has_more_training and self.learner_steps >= self.horizon and not (done or cut):
            cut = True
            info = dict(info, reason="practice-horizon")
        if done or cut:
            self.save_lessons()
            self.record(dict(event="finish", **self.episode_metrics(), reason=info["reason"]))
        return observation, reward, done, cut, info

    def save_lessons(self):
        if self.lessons is not None:
            counts = self.lessons.save()
            if any(counts.values()):
                self.record(dict(event="lessons", batch=self.batch, seed=self.lessons.seed, **counts))
            self.lessons = None


def soften_action_sampling(policy, head, temperature):
    """Increase sampling exploration while preserving the starting greedy actions."""
    start = sum(ACTION_SIZES[:head])
    outputs = slice(start, start + ACTION_SIZES[head])
    with torch.no_grad():
        policy.action_net.weight[outputs].div_(temperature)
        policy.action_net.bias[outputs].div_(temperature)


def nearby_combat_context(observations, schema):
    def latest(name):
        return observations[..., schema.index(name) - len(schema)]

    return ((abs(latest("blockade_direction")) <= .5)
            & (latest("enemy_0_present") > .5) & (abs(latest("enemy_0_x")) <= .22)
            & (abs(latest("enemy_0_y")) <= .18))


def blocked_recovery_context(observations, schema):
    history = observations.unflatten(-1, (HISTORY_LENGTH, len(schema)))
    contact = history[..., schema.index("blockade_direction")]
    steering = history[..., schema.index("previous_horizontal")]
    recovery = history[..., schema.index("previous_jump")] > .5
    if "road_dx" in schema:
        road_available = history[..., schema.index("road_present")] > .5
        road_away = history[..., schema.index("road_dx")] * contact < -.5
        recovery |= road_available & road_away
    # Retain the exploration window for the first release or retreat decisions.
    return ((steering * contact > .5) & recovery).sum(dim=-1) >= 2


def idle_navigation_context(observations, schema):
    if "road_present" not in schema:
        return torch.zeros_like(observations[..., 0], dtype=torch.bool)
    recent = observations.unflatten(-1, (HISTORY_LENGTH, len(schema)))[..., -3:, :]

    def column(name):
        return recent[..., schema.index(name)]

    x, y = column("knight_x"), column("knight_y")
    still = (x.amax(-1) - x.amin(-1) < .01) & (y.amax(-1) - y.amin(-1) < .01)
    visible = (column("knight_confidence") > .5) & (column("room_changed") < .5)
    road = (column("road_present") > .5) & (column("road_dx").abs() + column("road_dy").abs() > .5)
    idle = (column("previous_horizontal").abs() < .5) & (column("previous_jump") < .5)
    threat = ((column("enemy_0_present") > .5) & (column("enemy_0_x").abs() <= .22)
              & (column("enemy_0_y").abs() <= .18))
    return still & (visible & road & idle & ~threat).all(dim=-1)


class EncounterExplorationPolicy(ActorCriticPolicy):
    """Use the same state-conditioned action distribution for rollouts and PPO updates."""
    attack_temperature = 1.0
    blocked_recovery_temperature = 1.0
    observation_schema = None

    def encounter_distribution(self, obs, actor):
        if self.attack_temperature <= 1 and self.blocked_recovery_temperature <= 1:
            return self._get_action_dist_from_latent(actor)
        logits = self.action_net(actor)
        normal = torch.ones_like(logits[..., :1])
        blocked = normal
        close_combat = normal
        if self.blocked_recovery_temperature > 1:
            schema = self.observation_schema
            recovery = blocked_recovery_context(obs, schema) | idle_navigation_context(obs, schema)
            blocked = torch.where(recovery.unsqueeze(-1), self.blocked_recovery_temperature, normal)
        if self.attack_temperature > 1:
            eligible = nearby_combat_context(obs, self.observation_schema)
            close_combat = torch.where(eligible.unsqueeze(-1), self.attack_temperature, normal)
        horizontal, aim, jump, attack = logits.split(ACTION_SIZES, dim=-1)
        adjusted = (horizontal / blocked, aim, jump / blocked, attack / torch.maximum(blocked, close_combat))
        return self.action_dist.proba_distribution(action_logits=torch.cat(adjusted, dim=-1))

    def latents(self, obs):
        features = self.extract_features(obs)
        if self.share_features_extractor:
            return self.mlp_extractor(features)
        actor, critic = features
        return self.mlp_extractor.forward_actor(actor), self.mlp_extractor.forward_critic(critic)

    def forward(self, obs, deterministic=False):
        actor, critic = self.latents(obs)
        distribution = self.encounter_distribution(obs, actor)
        actions = distribution.get_actions(deterministic=deterministic)
        return actions.reshape((-1, *self.action_space.shape)), self.value_net(critic), distribution.log_prob(actions)

    def evaluate_actions(self, obs, actions):
        actor, critic = self.latents(obs)
        distribution = self.encounter_distribution(obs, actor)
        return self.value_net(critic), distribution.log_prob(actions), distribution.entropy()

    def get_distribution(self, obs):
        features = BasePolicy.extract_features(self, obs, self.pi_features_extractor)
        actor = self.mlp_extractor.forward_actor(features)
        return self.encounter_distribution(obs, actor)


def practice_temperatures(policy, output, number, schema, attack_temperature, jump_temperature):
    """Use recent encounters to detect when the learner rarely samples a needed input edge."""
    previous_batch = []
    for path in (output / "training-observations").glob(f"{number - 1}_*.npz"):
        with np.load(path, allow_pickle=False) as data:
            previous_batch.append(data["observations"])
    if not previous_batch or "blockade_direction" not in schema:
        return 1.0, 1.0
    observations = np.concatenate(previous_batch)

    def latest(name):
        return observations[:, schema.index(name) - len(schema)]

    contact = np.abs(latest("blockade_direction")) > .5
    nearby = nearby_combat_context(observations, schema)
    holding_attack = latest("previous_attack") > .5
    jump_at_contact = (latest("previous_jump") < .5) & contact
    with torch.no_grad():
        heads = policy.get_distribution(torch.from_numpy(observations)).distribution
        release = heads[3].probs[nearby & holding_attack, 0]
        press = heads[3].probs[nearby & ~holding_attack, 1]
        jump = heads[2].probs[jump_at_contact, 1]

    def rarely_samples(probabilities):
        return probabilities.numel() >= 6 and 2 * (probabilities < .1).sum() > probabilities.numel()

    return (attack_temperature if rarely_samples(press) or rarely_samples(release) else 1.0,
            jump_temperature if jump.numel() >= 4 and jump.mean() < .04 else 1.0)


def train_batch(env, incumbent, previous, output, number, steps, *, attack_temperature=1.0,
                jump_temperature=1.0, blocked_recovery_temperature=1.0, initial_attack_exploration=False):
    """Resume the learner even when its previous weights were still below publication quality."""
    starts = PracticeStarts(output / "practice-starts.json")
    graph = AuthoredRoute.load(env.labels) if starts.route else None
    practice = EncounterPractice(env, incumbent, number, output / "practice.jsonl", starts,
                                 lesson_folder=output, route_graph=graph)
    learner = PPO.load(previous, env=practice, device="cpu", custom_objects={
        "policy_class": EncounterExplorationPolicy,
        "n_steps": 128, "n_epochs": 3, "batch_size": 64, "learning_rate": 0.00004,
        "target_kl": 0.012, "ent_coef": 0.012, "seed": number + 42})
    try:
        # SB3 finishes a full rollout when the requested count is not divisible
        # by its rollout size. This trainer uses one environment.
        practice.batch_step_limit = ((steps + learner.n_steps - 1) // learner.n_steps) * learner.n_steps
        rehearsed = previous.name.startswith("learner-rehearsed-")
        schema = SCHEMAS[env.version]
        if "blockade_direction" not in schema:
            attack_temperature = 1.0
            blocked_recovery_temperature = 1.0
        if rehearsed or (attack_temperature > 1 and not initial_attack_exploration):
            attack_temperature, measured_jump = practice_temperatures(
                learner.policy, output, number, schema, attack_temperature, jump_temperature)
            if rehearsed:
                jump_temperature = measured_jump
        learner.policy.observation_schema = schema
        learner.policy.attack_temperature = attack_temperature
        learner.policy.blocked_recovery_temperature = blocked_recovery_temperature
        if jump_temperature > 1:
            soften_action_sampling(learner.policy, 2, jump_temperature)
        learner.learn(total_timesteps=steps, reset_num_timesteps=False)
        practice.save_lessons()
        practice.record(dict(event="batch-end", attackTemperature=attack_temperature, jumpTemperature=jump_temperature,
                             blockedRecoveryTemperature=blocked_recovery_temperature,
                             trainingDecisions=practice.batch_steps,
                             **practice.episode_metrics()))
        starts.save()
        checkpoint = output / f"learner-{number:04d}.zip"
        staged = output / "learner-in-progress.zip"
        # The policy state dict contains ordinary actor and critic tensors. No
        # sampling mode or custom policy class is needed to load it for playback.
        learner.policy_class = ActorCriticPolicy
        learner.save(staged)
        staged.replace(checkpoint)
        archive = output / "training-observations"
        archive.mkdir(exist_ok=True)
        for seed, frames in practice.practice_frames.items():
            temporary = archive / f"{number}_{seed}.tmp"
            with temporary.open("wb") as stream:
                np.savez_compressed(stream, seed=seed, observations=np.stack(frames))
            temporary.replace(temporary.with_suffix(".npz"))
        return checkpoint, practice.seeds, attack_temperature, jump_temperature
    finally:
        practice.close()
