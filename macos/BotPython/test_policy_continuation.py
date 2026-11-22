import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
import torch

from bot_policy_continuation import gym_observations, load_parent, retain_gym_actions, save_gym_observations, playback_checkpoint
from hkv_bot_dataset import ACTION_SIZES, FEATURE_NAMES, HISTORY_LENGTH, SCHEMAS


class Spec(gym.Env):
    action_space = gym.spaces.MultiDiscrete(ACTION_SIZES)

    def __init__(self, names):
        self.observation_space = gym.spaces.Box(-4, 4, (HISTORY_LENGTH * len(names),), dtype=np.float32)


class ContinuationTests(unittest.TestCase):
    def test_parent_actor_and_critic_survive_reload_and_feature_migration(self):
        torch.set_num_threads(1)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            for version in (1, 7):
                names = SCHEMAS[version]
                parent = PPO('MlpPolicy', Spec(names), policy_kwargs={'net_arch': [8, 8]}, device='cpu', seed=4)
                parent.num_timesteps = 512
                path = directory / 'model.zip'
                parent.save(path)
                manifest = dict(schemaVersion=version, actionSizes=ACTION_SIZES, featureNames=names,
                                historyLength=HISTORY_LENGTH, modelSHA256=hashlib.sha256(path.read_bytes()).hexdigest())
                (directory / 'manifest.json').write_text(json.dumps(manifest))
                child, _ = load_parent(directory, Spec(FEATURE_NAMES))
                raw = torch.randn((9, HISTORY_LENGTH, len(FEATURE_NAMES)))
                # Unavailable legacy target fields had zero input in the comparison contract.
                old = torch.zeros((9, HISTORY_LENGTH, len(names)))
                for i, key in enumerate(names):
                    if key in FEATURE_NAMES:
                        old[:, :, i] = raw[:, :, FEATURE_NAMES.index(key)]
                for j in range(4):
                    torch.testing.assert_close(parent.policy.get_distribution(old.flatten(1)).distribution[j].logits,
                                               child.policy.get_distribution(raw.flatten(1)).distribution[j].logits)
                torch.testing.assert_close(parent.policy.predict_values(old.flatten(1)), child.policy.predict_values(raw.flatten(1)))
                self.assertEqual(child.num_timesteps, 512)
                manifest['modelSHA256'] = 'wrong'
                (directory / 'manifest.json').write_text(json.dumps(manifest))
                with self.assertRaisesRegex(ValueError, 'verification'):
                    load_parent(directory, Spec(FEATURE_NAMES))

    def test_rehearsal_only_uses_matching_family_domain_and_training_seeds(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for name, domain, base in [('valid', 'hacker', 'parent'), ('other', 'gameplay', 'parent'), ('branch', 'hacker', 'other')]:
                run = root/'gym'/name
                shelf = run/'training-observations'
                shelf.mkdir(parents=True)
                (run/'state.json').write_text(json.dumps(dict(identity=dict(base=base, domain=domain))))
                for seed in [123456, 2000000]:
                    value = 1 if seed == 123456 and name=='valid' else 2
                    np.savez(shelf/f'0_{seed}.npz', seed=seed, observations=np.full((3,len(FEATURE_NAMES)*4),value,dtype=np.float32))
            states = gym_observations(root, 'hacker', Path('parent'), {})
            self.assertEqual(states.shape[0], 3)
            self.assertTrue((states == 1).all())

    def test_archived_gym_worker_is_optional_for_subsequent_clip_rehearsal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            parent = root/'models'/'hacker'/'parent'
            parent.mkdir(parents=True)
            manifest = {}
            sample = np.ones((3, len(FEATURE_NAMES)*4), np.float32)
            save_gym_observations(parent, manifest, sample)
            np.testing.assert_array_equal(gym_observations(root, 'hacker', parent, manifest), sample)
            (parent/'gym-rehearsal.npz').write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'does not match'):
                gym_observations(root, 'hacker', parent, manifest)

    def test_training_follows_new_published_version_and_respects_manual_rollback(self):
        with tempfile.TemporaryDirectory() as temp:
            models = Path(temp)
            for i in (1, 2):
                p = models/f'bot-{i}'; p.mkdir()
                (p/'runtime-manifest.json').write_text('{}')
                (p/'training-result.json').write_text(json.dumps(dict(nativeParity=True, botDomain='hacker',
                    modelDirectory=p.name, createdAt=str(i))))
            older, newer = models/'bot-1', models/'bot-2'
            (models/'active.json').write_text(json.dumps(dict(directory=older.name)))
            self.assertEqual(playback_checkpoint(models, 'hacker', older), newer)
            (models/'active.json').write_text(json.dumps(dict(directory=older.name, manualChoiceThrough=newer.name)))
            self.assertEqual(playback_checkpoint(models, 'hacker', newer), older)

    def test_a_clip_update_that_reverses_the_parent_action_is_limited(self):
        import copy
        model = PPO('MlpPolicy', Spec(FEATURE_NAMES), device='cpu', seed=1)
        with torch.no_grad():
            model.policy.action_net.weight.zero_()
            model.policy.action_net.bias.zero_()
            model.policy.action_net.bias[0] = 1
            parent = copy.deepcopy(model.policy)
            model.policy.action_net.bias[1] = 1.5
        info = retain_gym_actions(model, parent, torch.zeros((5,len(FEATURE_NAMES)*4)))
        self.assertLess(info['retainedUpdateFraction'], 1)
        self.assertGreater(info['retainedUpdateFraction'], 0)
        self.assertEqual(info['parentActionAgreement'], 1)

    def test_productive_route_preserves_each_control_even_when_the_parent_has_ties(self):
        import copy
        model = PPO('MlpPolicy', Spec(FEATURE_NAMES), device='cpu', seed=1)
        routes = torch.zeros((3, len(FEATURE_NAMES)*HISTORY_LENGTH))
        with torch.no_grad():
            model.policy.action_net.weight.zero_()
            model.policy.action_net.bias.zero_()
            parent = copy.deepcopy(model.policy)
            model.policy.action_net.bias[1] = 2
        info = retain_gym_actions(model, parent, routes[:0], routes)
        self.assertEqual(info['protectedRouteFrames'], len(routes))
        self.assertEqual(info['retainedUpdateFraction'], 0)
        self.assertTrue(torch.equal(model.policy.get_distribution(routes).mode(), parent.get_distribution(routes).mode()))
