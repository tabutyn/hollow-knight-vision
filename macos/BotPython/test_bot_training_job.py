import contextlib
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import uuid

from train_bot_job import promotion_check, split_blockade_clips, used_sessions


class PromotionTests(unittest.TestCase):
    def test_door_examples_learn_from_whole_clips_and_keep_comparison_sessions_eligible(self):
        counts = {'short': 6, 'long': 19}
        self.assertEqual(split_blockade_clips(['walk'], ['walk', 'short', 'long'], counts), ['walk', 'short'])
        self.assertEqual(split_blockade_clips(['short', 'long'], ['walk', 'short', 'long'], counts), ['short', 'walk'])
        self.assertEqual(split_blockade_clips(['door'], ['walk', 'door'], {'door': 14}), ['walk'])
        self.assertEqual(split_blockade_clips(['walk'], ['walk', 'jump'], counts), ['walk'])

    def test_loss_improvement_cannot_hide_a_jump_regression(self):
        baseline = {'negativeLogLikelihood': 1.0, 'exactActionAccuracy': 0.5,
                    'heads': [{'balancedAccuracy': 0.7}] * 4}
        candidate = copy.deepcopy(baseline)
        candidate.update(negativeLogLikelihood=0.5, exactActionAccuracy=0.8)
        self.assertTrue(promotion_check(candidate, baseline)[0])
        candidate['heads'][2] = {'balancedAccuracy': 0.3}
        self.assertFalse(promotion_check(candidate, baseline)[0])
        self.assertFalse(promotion_check(candidate, None)[0])

    def test_legacy_and_new_provenance_count_as_previously_used(self):
        self.assertEqual(used_sessions({'trainingSessions': ['abc'], 'validationSessions': ['def'],
                                      'sourceReport': {'sessions': [{'id': 'ghi'}]},
                                      'validationStrategy': {'sourceSession': 'jkl'}}), {'ABC', 'DEF', 'GHI', 'JKL'})


@unittest.skipUnless(importlib.util.find_spec('stable_baselines3'), 'Requires bot training environment')
class TrainingJobTests(unittest.TestCase):
    def test_job_reconstructs_recorded_paths_with_windows_native_host(self):
        from test_bot_dataset import event, manifest, observation
        from train_bot_job import run_job

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            selected = []
            for _ in range(2):
                session = str(uuid.uuid4())
                selected.append(session)
                clip = root / 'demonstrations' / ('demo-' + session)
                clip.mkdir(parents=True)
                header = dict(manifest(), id=session, botDomain='gameplay',
                              observationCount=1, usableObservationCount=1, inputEventCount=1)
                (clip / 'manifest.json').write_text(json.dumps(header))
                rows = [event(100, 'right'), dict(observation(100.1), driveDirection=1)]
                (clip / 'events.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows))
            selection = root / 'selection.json'
            selection.write_text(json.dumps(selected))
            identifier = str(uuid.uuid4())
            verifier = str(root / 'HollowKnightVision.Windows.Host.exe')
            with patch('train_bot_job.export_dataset', side_effect=RuntimeError('export intercepted')) as export:
                with self.assertRaisesRegex(RuntimeError, 'export intercepted'):
                    run_job(root, selection, identifier, None, domain='gameplay', native_verifier=verifier)
            args, = export.call_args.args
            snapshots = root / 'training-runs' / identifier / 'demonstrations'
            self.assertEqual(Path(args[1]), snapshots)
            self.assertEqual(args[args.index('--path-extractor') + 1], verifier)
            for clip in snapshots.iterdir():
                self.assertEqual((clip / 'events.jsonl').read_bytes(),
                                 (root / 'demonstrations' / clip.name / 'events.jsonl').read_bytes())

    def test_job_snapshots_holds_out_sessions_exports_and_preserves_active_model(self):
        import gymnasium as gym
        import numpy as np
        from stable_baselines3 import PPO
        import torch
        from export_bot_policy import export_policy
        from hkv_bot_dataset import LEGACY_FEATURE_NAMES
        from test_bot_dataset import manifest, observation, event
        from train_bot_job import run_job

        class Spec(gym.Env):
            observation_space = gym.spaces.Box(-4, 4, shape=(576,), dtype=np.float32)
            action_space = gym.spaces.MultiDiscrete([3, 3, 2, 2])

        torch.set_num_threads(2)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            baseline = root / 'models/v1'; baseline.mkdir(parents=True)
            model = PPO('MlpPolicy', Spec(), seed=12, device='cpu', policy_kwargs={'net_arch': [8, 8]})
            model.save(baseline / 'model.zip')
            digest = hashlib.sha256((baseline / 'model.zip').read_bytes()).hexdigest()
            (baseline / 'manifest.json').write_text(json.dumps({'schemaVersion': 1, 'featureNames': LEGACY_FEATURE_NAMES,
                'historyLength': 4, 'actionSizes': [3, 3, 2, 2], 'modelSHA256': digest}))
            export_policy(baseline)
            selected = []
            for index in range(4):
                session = str(uuid.uuid4()).upper(); selected.append(session)
                directory = root / 'demonstrations' / ('demo-' + session.lower()); directory.mkdir(parents=True)
                m = manifest(); m['id'] = session
                m['observationCount'] = m['usableObservationCount'] = 20
                inputs = [event(100, 'left' if index % 2 else 'right'), event(100.8, 'z'), event(101.2, 'z', False)]
                if index % 2 == 0:
                    inputs += [event(100.65, 'x'), event(100.72, 'x', False)]
                m['inputEventCount'] = len(inputs)
                records = [observation(100.1 + i * 0.1) for i in range(20)] + inputs
                for row in records:
                    if row["type"] == "observation":
                        row["features"][-11:] = [1, -.1, -.2, .1, -1, 1, .1, -.2, .1, 1, 1]
                        row["terrainSource"] = "detected"
                        row["detectedTerrain"] = dict(width=640, height=360, floors=[],
                            walls=[dict(x=200, bottomY=30, topY=160, solidSide=-1)])
                        if index % 2 == 0:
                            row["features"][96:102] = [.04, 0, .03, .04, .9, 1]
                            row["blockade"] = {"dx": .03, "dy": 0, "mark": {"direction": 1, "crossed": False}}
                (directory / 'manifest.json').write_text(json.dumps(m))
                (directory / 'events.jsonl').write_text(''.join(json.dumps(x) + '\n' for x in records))
            selection = root / 'selection.json'; selection.write_text(json.dumps(selected))
            identifier = str(uuid.uuid4())
            with contextlib.redirect_stdout(io.StringIO()):
                result = run_job(root, selection, identifier, baseline, epochs=2)
            self.assertFalse(set(result['trainingSessions']) & set(result['validationSessions']))
            self.assertEqual(len(result['validationSessions']), 2)
            support = result['candidate']['heads'][0]['support']
            self.assertGreater(support[1], 0); self.assertGreater(support[2], 0)
            self.assertTrue(result['nativeParity'])
            self.assertEqual(result['parentPolicy'], {'directory': baseline.name, 'modelSHA256': digest})
            self.assertEqual(result['combatTraining']['clips'], 1)
            self.assertEqual(result['combatTraining']['validationClips'], 1)
            self.assertIsNotNone(result['combatTraining']['attackRecall'])
            self.assertIsNotNone(result['combatTraining']['releaseRecall'])
            self.assertEqual(result['blockadeTraining']['validationClips'], 1)
            self.assertGreater(result['blockadeTraining']['attackExamples'], 0)
            self.assertIsNotNone(result['blockadeTraining']['releaseRecall'])
            payload = json.loads((root / 'models' / result['modelDirectory'] / 'runtime-policy.json').read_text())
            self.assertTrue(payload['layers'])
            self.assertTrue({'trainedOnCombat', 'pitPolicy', 'atlasPolicy', 'recoveryManeuver'}.isdisjoint(payload))
            self.assertIsNone(result['baselineError'])
            saved = json.loads((root / 'models' / result['modelDirectory'] / 'manifest.json').read_text())
            self.assertLessEqual(saved['startWeightFit']['afterNLL'], saved['startWeightFit']['beforeNLL'])
            self.assertAlmostEqual(result['candidateSequential']['negativeLogLikelihood'],
                                   saved['finalValidation']['validation_feedback_nll'], places=5)
            self.assertAlmostEqual(result['candidateSequential']['exactActionAccuracy'],
                                   saved['finalValidation']['validation_feedback_exact_action_accuracy'], places=5)
            self.assertEqual(result['wallEvidence']['withWall'], result['validationExamples'])
            self.assertTrue((root / 'models' / result['modelDirectory'] / 'runtime-policy.json').is_file())
            self.assertTrue((root / 'training-runs' / identifier / 'demonstrations').is_dir())
            self.assertEqual(hashlib.sha256((baseline / 'model.zip').read_bytes()).hexdigest(), digest)
            self.assertFalse((root / 'models/active.json').exists())
            # A model trained before the hold cost can reuse its existing
            # captures and exact validation split for the scoring upgrade.
            previous_policy = root / 'models' / result['modelDirectory']
            prior_file = previous_policy / 'manifest.json'
            previous_manifest = json.loads(prior_file.read_text())
            previous_manifest.pop('heldAttackCost')
            prior_file.write_text(json.dumps(previous_manifest))
            with contextlib.redirect_stdout(io.StringIO()):
                rescored = run_job(root, selection, str(uuid.uuid4()), previous_policy, epochs=1)
            self.assertEqual(set(rescored['validationSessions']), set(result['validationSessions']))
            self.assertFalse(set(rescored['trainingSessions']) & set(rescored['validationSessions']))
            with contextlib.redirect_stdout(io.StringIO()):
                repeated = run_job(root, selection, str(uuid.uuid4()), root / 'models' / rescored['modelDirectory'],
                                   epochs=1, reuse_validation=True)
            self.assertTrue(repeated['reusedParentValidation'])
            self.assertEqual(repeated['validationSessions'], rescored['validationSessions'])
            # Reusing clips cannot masquerade as improvement on unseen recordings.
            manifest_path = baseline / 'manifest.json'
            m = json.loads(manifest_path.read_text()); m['trainingSessions'] = selected
            manifest_path.write_text(json.dumps(m))
            with self.assertRaisesRegex(ValueError, 'two new clips'), contextlib.redirect_stdout(io.StringIO()):
                run_job(root, selection, str(uuid.uuid4()), baseline, epochs=1)


if __name__ == '__main__':
    unittest.main()
