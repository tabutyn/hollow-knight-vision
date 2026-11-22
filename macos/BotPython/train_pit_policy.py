"""Train the optional Gameplay recovery actor with PPO on randomized platform geometry."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.env_util import make_vec_env


class PitEnvironment(gym.Env):
    """One decision per 100 ms. Success requires standing on a lip outside the pit."""
    observation_space = gym.spaces.Box(-4, 4, shape=(8,), dtype=np.float32)
    action_space = gym.spaces.MultiDiscrete([3, 2])  # neutral/left/right, release/hold jump

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        rng = self.np_random
        self.width = rng.uniform(3.0, 11.0)
        self.lips = rng.uniform(1.1, 4.4, size=2)
        if rng.random() < 0.25:
            self.lips[rng.integers(2)] += 3.7
        self.scale = 6 / rng.uniform(26, 32)
        self.speed = rng.uniform(7.7, 8.9)
        self.jump_speed = rng.uniform(15.0, 16.4)
        self.gravity = rng.uniform(42, 51)
        self.full_jump_hold = rng.uniform(0.15, 0.21)
        self.x = rng.uniform(0.28, self.width - 0.28)
        self.y = self.vy = 0.0
        self.grounded = True
        self.direction = self.jump = self.steps = self.settled = 0
        self.held_time = 0.0
        self.max_height = 0.0
        # The camera centers the player with a small offset and lag. A hidden
        # Knight provides no measured height or x, only an approximate view center.
        self.camera_offset = rng.uniform(-1.25, 1.25)
        self.hidden = rng.integers(0, 6) if rng.random() < 0.45 else 0
        return self._observation(), {}

    def _observation(self):
        rng = self.np_random
        visible = self.hidden == 0
        px = self.x + (rng.normal(0, 0.18) if visible else self.camera_offset)
        py = self.y + rng.normal(0, 0.18) if visible else 0
        left = rng.normal(0, 0.18)
        right = self.width + rng.normal(0, 0.18)
        obs = [self.scale*(left-px), self.scale*(right-px),
               self.scale*(self.lips[0]-py), self.scale*(self.lips[1]-py),
               self.scale*py, float(visible), self.direction, self.jump]
        return np.clip(obs, -4, 4).astype(np.float32)

    def step(self, action):
        direction = (0, -1, 1)[action[0]]
        jump = int(action[1])
        reward = -0.025
        if jump and not self.jump and self.grounded:
            self.vy = self.jump_speed
            self.grounded = False
            self.held_time = 0
            reward -= 0.06
        dt = self.np_random.uniform(0.085, 0.116) / 6
        for _ in range(6):
            if not self.grounded:
                if jump and self.vy > 0 and self.held_time < self.full_jump_hold:
                    self.held_time += dt
                else:
                    self.vy -= self.gravity * dt
                if not jump and self.jump and self.vy > 6:
                    self.vy = 6
            x = self.x + direction * self.speed * dt
            # The Knight's body cannot move through the face of either ledge.
            if self.x >= 0 and self.y < self.lips[0] - 0.04:
                x = max(0.0, x)
            if self.x <= self.width and self.y < self.lips[1] - 0.04:
                x = min(self.width, x)
            self.x = x
            surface = self.lips[0] if x < 0 else self.lips[1] if x > self.width else 0.0
            self.y += self.vy * dt
            self.grounded = self.y <= surface and self.vy <= 0
            if self.grounded:
                self.y, self.vy = surface, 0.0
        self.direction, self.jump = direction, jump
        self.steps += 1
        self.hidden = max(0, self.hidden-1)
        if not self.hidden and self.np_random.random() < 0.06:
            self.hidden = self.np_random.integers(1, 4)
        height = min(self.y, min(self.lips))
        reward += 0.12 * max(0, height-self.max_height)
        self.max_height = max(height, self.max_height)
        outside = self.x < -0.45 or self.x > self.width + 0.45
        self.settled = self.settled + 1 if outside and self.grounded else 0
        success = self.settled >= 2
        reward += 8.0 if success else 0.0
        timeout = self.steps >= 45 and not success
        return self._observation(), reward - (2 if timeout else 0), success, timeout, {}


def evaluate(model, seed, episodes):
    # Parallel prediction over independent trials keeps held-out evaluation quick.
    envs = [PitEnvironment() for _ in range(episodes)]
    observations = np.stack([e.reset(seed=seed+i)[0] for i,e in enumerate(envs)])
    finished = np.zeros(episodes, dtype=bool)
    sides, lengths = [], []
    for _ in range(45):
        actions, _ = model.predict(observations, deterministic=True)
        for i in np.flatnonzero(~finished):
            observations[i], _, success, timeout, _ = envs[i].step(actions[i])
            if success:
                sides.append('left' if envs[i].x < 0 else 'right')
                lengths.append(envs[i].steps)
            finished[i] = success or timeout
        if finished.all():
            break
    return dict(episodes=episodes, landed=len(sides), left=sides.count('left'), right=sides.count('right'),
                meanDecisionsToLand=float(np.mean(lengths)) if lengths else None, seed=seed)


def train(output, steps, seed):
    import torch
    torch.set_num_threads(1)
    output.mkdir(parents=True, exist_ok=False)
    env = make_vec_env(PitEnvironment, n_envs=16, seed=seed)
    model = PPO('MlpPolicy', env, policy_kwargs={'net_arch':dict(pi=[32,32], vf=[32,32])},
                seed=seed, n_steps=128, batch_size=256, n_epochs=6, gamma=0.97,
                ent_coef=0.007, learning_rate=0.001, device='cpu')
    before = evaluate(model, 81000, 512)
    model.learn(total_timesteps=steps)
    model.save(output/'pit-model.zip')
    after = evaluate(model, 81000, 512)
    checkpoint_hash = hashlib.sha256((output/'pit-model.zip').read_bytes()).hexdigest()
    report = dict(algorithm='Stable-Baselines3 PPO', seed=seed, environmentSteps=model.num_timesteps,
                  initial=before, heldOut=after, checkpointSHA256=checkpoint_hash)
    (output/'pit-training.json').write_text(json.dumps(report,indent=2)+'\n')
    print(json.dumps(report),flush=True)


def export_pit(directory, report):
    import torch
    checkpoint = directory/'pit-model.zip'
    checkpoint_hash = hashlib.sha256(checkpoint.read_bytes()).hexdigest()
    if checkpoint_hash != report['checkpointSHA256']:
        raise ValueError('The pit policy checkpoint differs from its training report')
    model = PPO.load(checkpoint, device='cpu')
    if model.observation_space.shape != (8,) or model.action_space.nvec.tolist() != [3,2]:
        raise ValueError('Unexpected pit policy observation or action space')
    layers = []
    for layer, activation in [(layer,'tanh') for layer in model.policy.mlp_extractor.policy_net
                              if isinstance(layer,torch.nn.Linear)] + [(model.policy.action_net,'linear')]:
        layers.append(dict(inputSize=int(layer.in_features), outputSize=int(layer.out_features), activation=activation,
                           weights=layer.weight.detach().numpy().ravel().tolist(), biases=layer.bias.detach().numpy().tolist()))
    policy = dict(schemaVersion=1, checkpointSHA256=checkpoint_hash, layers=layers)
    samples = np.random.default_rng(103).uniform(-4,4,(2048,8)).astype(np.float32)
    values = samples
    for layer in layers:
        weights = np.array(layer['weights'],dtype=np.float32).reshape(layer['outputSize'],-1)
        values = values @ weights.T + np.array(layer['biases'],dtype=np.float32)
        if layer['activation'] == 'tanh':
            values = np.tanh(values)
    actions = np.column_stack((values[:,:3].argmax(axis=1), values[:,3:].argmax(axis=1)))
    if not np.array_equal(actions, model.predict(samples,deterministic=True)[0]):
        raise ValueError('Exported pit actions differ from the training checkpoint')
    return policy


def install_pit(candidate, source):
    report = json.loads((source/'pit-training.json').read_text())
    shutil.copyfile(source/'pit-model.zip', candidate/'pit-model.zip')
    shutil.copyfile(source/'pit-training.json', candidate/'pit-training.json')
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('output', type=Path)
    parser.add_argument('--steps', type=int, default=180224)
    parser.add_argument('--seed', type=int, default=47)
    args = parser.parse_args()
    train(args.output, args.steps, args.seed)
