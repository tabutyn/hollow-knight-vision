"""Train the Gameplay horizontal exploration policy on partially mapped corridors."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env


class AtlasCorridor(gym.Env):
    """One camera sees ten 64-pixel cells across. Only first coverage earns credit."""
    observation_space = gym.spaces.Box(-1, 1, shape=(8,), dtype=np.float32)
    action_space = gym.spaces.Discrete(3)

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        rng = self.np_random
        self.length = int(rng.integers(45, 110))
        self.x = float(rng.integers(12, self.length - 12))
        left, right = int(rng.integers(1, 5)), int(rng.integers(8, 30))
        if rng.random() < 0.5:
            left, right = right, left
        self.known = np.zeros(self.length, dtype=bool)
        self.known[max(0, int(self.x)-5-left):int(self.x)+6+right] = True
        self.speed = rng.uniform(0.28, 0.55)
        self.enemy = self.x + rng.choice([-1, 1]) * rng.uniform(0.8, 5) if rng.random() < 0.65 else -999
        self.anchor = self.x
        self.dwell = self.steps = self.previous = self.cooldown = self.lost = self.gained = 0
        self.dwell_steps = 0
        self.start = self.x
        self.blocked = [False, False]
        return self.observation(), {}

    def frontier(self, direction):
        edge = int(self.x) + 5 * direction
        for distance in range(1, 65):
            column = edge + distance * direction
            if not 0 <= column < self.length or not self.known[column]:
                return 1 / (1 + (distance - 1) / 5)
        return 0.

    def world_observation(self):
        dx = self.enemy - self.x
        danger = max(0., 1 - abs(dx) / 3)
        return np.asarray([self.frontier(-1), self.frontier(1),
                           danger if dx < 0 else 0, danger if dx >= 0 else 0,
                           *self.blocked, min(self.dwell / 20, 1), self.previous], dtype=np.float32)

    def observation(self):
        # Express actions relative to the nearer frontier. Mirrored maps share
        # one policy, including mirrored enemies, blocked directions and history.
        obs = self.world_observation()
        self.mirrored = obs[0] > obs[1] or (obs[0] == obs[1] and self.previous < 0)
        if self.mirrored:
            obs = obs[[1, 0, 3, 2, 5, 4, 6, 7]]
            obs[7] *= -1
        return obs

    def world_action(self, action):
        return (0, 2, 1)[int(action)] if self.mirrored else int(action)

    def step(self, action):
        direction = (0, -1, 1)[self.world_action(action)]
        x = float(np.clip(self.x + self.speed * direction, 5, self.length - 6))
        self.blocked = [x <= 5.2, x >= self.length - 6.2]
        loss = int(abs(self.enemy - x) < 0.65 and self.cooldown == 0)
        self.cooldown = 12 if loss else max(0, self.cooldown - 1)
        if loss:
            away = -1 if self.x <= self.enemy else 1
            x = float(np.clip(x + 1.25 * away, 5, self.length - 6))
        self.x = x
        view = self.known[int(x)-5:int(x)+6]
        # Three adjacent rows represent the interior of the screen.
        new = int(np.count_nonzero(~view)) * 3
        view[:] = True
        if abs(x - self.anchor) >= 1.25:
            self.dwell = 0
            self.anchor = x
        else:
            self.dwell += 1
        self.dwell_steps += int(self.dwell >= 20)
        self.gained += new
        self.lost += loss
        self.previous = direction
        self.steps += 1
        reward = new - 32 * loss - (0.12 if self.dwell >= 20 else 0)
        return self.observation(), reward, False, self.steps >= 72, {}


def evaluate(model, seed=60000, baseline=None):
    envs = [AtlasCorridor() for _ in range(256)]
    obs = np.stack([e.reset(seed=seed+i)[0] for i, e in enumerate(envs)])
    # On flat, unobstructed, danger-free starts, check the side initial map coverage favors.
    world = np.stack([e.world_observation() for e in envs])
    clear = (world[:, 2:6] == 0).all(axis=1) & (np.abs(world[:, 0]-world[:, 1]) > 0.03)
    expected = np.where(world[:, 0] > world[:, 1], 1, 2)
    def choose():
        if baseline == 'alwaysRight':
            return np.array([1 if e.mirrored else 2 for e in envs])
        if baseline == 'nearestFrontier':
            return np.full(len(envs), 2)
        return model.predict(obs, deterministic=True)[0]
    first = np.array([e.world_action(a) for e, a in zip(envs, choose())])
    for _ in range(72):
        actions = choose()
        obs = np.stack([e.step(a)[0] for e, a in zip(envs, actions)])
    return dict(seed=seed, episodes=len(envs), meanDwellDecisions=float(np.mean([e.dwell_steps for e in envs])),
                meanNewCells=float(np.mean([e.gained for e in envs])),
                meanDamage=float(np.mean([e.lost for e in envs])), clearStarts=int(clear.sum()),
                choseFrontier=int(np.sum(clear & (first == expected))),
                grewAtlas=sum(e.gained > 0 for e in envs))


def train(output, steps=262144, seed=53):
    import torch
    torch.set_num_threads(1)
    output.mkdir(parents=True, exist_ok=False)
    env = make_vec_env(AtlasCorridor, n_envs=16, seed=seed)
    model = PPO('MlpPolicy', env, policy_kwargs={'net_arch': dict(pi=[32, 32], vf=[32, 32])},
                seed=seed, n_steps=128, n_epochs=6, batch_size=256, learning_rate=0.001,
                ent_coef=0.013, gamma=0.98, device='cpu')
    initial = evaluate(model)
    model.learn(steps)
    model.save(output/'atlas-model.zip')
    result = dict(algorithm='Stable-Baselines3 PPO', seed=seed, environmentSteps=model.num_timesteps,
                  initial=initial, heldOut=evaluate(model), reward=dict(newCell=1, lostMask=-32,
                  sameAreaAfterTwoSecondsPerDecision=-0.12),
                  checkpointSHA256=hashlib.sha256((output/'atlas-model.zip').read_bytes()).hexdigest())
    result['independentComparison'] = {key: evaluate(model, seed=91000, baseline=key)
                                       for key in ['trained', 'alwaysRight', 'nearestFrontier']}
    (output/'atlas-training.json').write_text(json.dumps(result, indent=2)+'\n')
    print(json.dumps(result), flush=True)


def install_atlas(candidate, source):
    for name in ['atlas-training.json', 'atlas-model.zip']:
        shutil.copyfile(source/name, candidate/name)
    return json.loads((candidate/'atlas-training.json').read_text())


def export_atlas(directory, report):
    import torch
    path = directory/'atlas-model.zip'
    if hashlib.sha256(path.read_bytes()).hexdigest() != report['checkpointSHA256']:
        raise ValueError('Atlas checkpoint differs from training report')
    model = PPO.load(path, device='cpu')
    if model.observation_space.shape != (8,) or model.action_space.n != 3:
        raise ValueError('Incompatible Atlas direction policy')
    layers = []
    samples = np.random.default_rng(54).uniform(-1, 1, (2048, 8)).astype('float32')
    outputs = samples
    modules = [*model.policy.mlp_extractor.policy_net, model.policy.action_net]
    for index, layer in enumerate(modules):
        if isinstance(layer, torch.nn.Tanh):
            outputs = np.tanh(outputs)
            continue
        if not isinstance(layer, torch.nn.Linear):
            raise ValueError('Unsupported exploration layer')
        weights = layer.weight.detach().numpy()
        bias = layer.bias.detach().numpy()
        layers.append(dict(inputSize=layer.in_features, outputSize=layer.out_features,
                           activation='linear' if index == len(modules)-1 else 'tanh',
                           weights=weights.ravel().tolist(), biases=bias.tolist()))
        outputs = outputs @ weights.T + bias
    if not np.array_equal(outputs.argmax(axis=1), model.predict(samples, deterministic=True)[0]):
        raise ValueError('Exported Atlas actions differ from checkpoint')
    return dict(schemaVersion=1, checkpointSHA256=report['checkpointSHA256'], layers=layers)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    train(args.output)
