import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


@unittest.skipUnless(importlib.util.find_spec('stable_baselines3'), 'Run in the bot training environment')
class BotRuntimeExportTests(unittest.TestCase):
    def test_export_actor_preserves_logits_and_contract(self):
        import gymnasium as gym
        import numpy as np
        import torch
        from stable_baselines3 import PPO
        from export_bot_policy import export_policy, write_parity_fixture
        from hkv_bot_dataset import ACTION_SIZES, FEATURE_NAMES, FEATURE_COUNT, HISTORY_LENGTH, SCHEMA_VERSION
        import hashlib

        class Spec(gym.Env):
            observation_space = gym.spaces.Box(-4, 4, shape=(FEATURE_COUNT * HISTORY_LENGTH,), dtype=np.float32)
            action_space = gym.spaces.MultiDiscrete(ACTION_SIZES)

        torch.set_num_threads(2)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            model = PPO('MlpPolicy', Spec(), seed=12, device='cpu', policy_kwargs={'net_arch': [8, 8]})
            model.save(root / 'model.zip')
            manifest = {'schemaVersion': SCHEMA_VERSION, 'featureNames': FEATURE_NAMES, 'historyLength': 4,
                        'actionSizes': ACTION_SIZES, 'name': 'Test policy',
                        'modelSHA256': hashlib.sha256((root / 'model.zip').read_bytes()).hexdigest()}
            (root / 'manifest.json').write_text(json.dumps(manifest))
            exported, restored = export_policy(root)
            write_parity_fixture(restored, root / 'parity.json')
            parity = json.loads((root / 'parity.json').read_text())
            inputs = np.asarray(parity['inputs'], dtype=np.float32)
            predicted = inputs
            for layer in exported['layers']:
                weights = np.asarray(layer['weights'], dtype=np.float32).reshape(layer['outputSize'], layer['inputSize'])
                predicted = predicted @ weights.T + np.asarray(layer['biases'], dtype=np.float32)
                if layer['activation'] == 'tanh':
                    predicted = np.tanh(predicted)
            np.testing.assert_allclose(predicted, np.asarray(parity['logits']), rtol=1e-4, atol=1e-6)
            self.assertEqual([x['activation'] for x in exported['layers']], ['tanh', 'tanh', 'linear'])
            runtime = json.loads((root / 'runtime-manifest.json').read_text())
            self.assertEqual(runtime['policySHA256'], hashlib.sha256((root / 'runtime-policy.json').read_bytes()).hexdigest())
            manifest['modelSHA256'] = 'incorrect'
            (root / 'manifest.json').write_text(json.dumps(manifest))
            with self.assertRaises(ValueError):
                export_policy(root)


if __name__ == '__main__':
    unittest.main()
