from tempfile import TemporaryDirectory
import unittest

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
import torch

from bot_projected_ppo import ColumnExtractor, warm_start


class ProjectedPPOTests(unittest.TestCase):
    def test_actor_and_critic_equal_full_model_with_unused_inputs_zeroed(self):
        env = gym.make("CartPole-v1")
        self.addCleanup(env.close)
        source = PPO("MlpPolicy", env, n_steps=8, batch_size=8, seed=4, device="cpu")
        columns = [0, 3]
        compact = warm_start(source, env, columns, seed=5)
        observation = torch.tensor([[0.13, 1.7, 0.32, -0.42]])
        masked = observation.clone()
        masked[:, 1:3] = 0
        with TemporaryDirectory() as directory:
            compact.save(directory + "/model")
            restored = PPO.load(directory + "/model", device="cpu")
            with torch.no_grad():
                torch.testing.assert_close(restored.policy.predict_values(observation),
                                           source.policy.predict_values(masked))
                torch.testing.assert_close(restored.policy.get_distribution(observation).distribution.logits,
                                           source.policy.get_distribution(masked).distribution.logits)
        self.assertEqual(compact.policy.mlp_extractor.policy_net[0].in_features, 2)
        for invalid in ([], [0, 0], [-1, 2], [1, 0], [0, 4], [np.float32(1)]):
            with self.assertRaises(ValueError):
                ColumnExtractor(env.observation_space, invalid)
