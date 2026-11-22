import contextlib
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest

import hkv_bot_dataset as dataset
from test_bot_dataset import event, manifest, observation


@unittest.skipUnless(importlib.util.find_spec("stable_baselines3"), "Run in macos/.bot-venv for SB3 integration")
class BehaviorCloningTests(unittest.TestCase):
    def test_redundant_attack_penalizes_only_the_attack_head(self):
        import torch
        from stable_baselines3.common.distributions import MultiCategoricalDistribution
        from train_behavior_cloning import demonstration_loss
        logits = torch.zeros(3, sum(dataset.ACTION_SIZES), requires_grad=True)
        distribution = MultiCategoricalDistribution(dataset.ACTION_SIZES).proba_distribution(logits)
        targets = torch.tensor([[2, 0, 0, 0], [2, 0, 0, 1], [2, 0, 0, 0]])
        losses = demonstration_loss(distribution, targets, torch.tensor([True, False, True]), torch.tensor([1., 0., 0.]))
        losses.sum().backward()
        self.assertGreater(logits.grad[0, -1].item(), 0)  # Gradient descent makes another hold less likely.
        self.assertLess(logits.grad[1, -1].item(), 0)
        self.assertEqual(logits.grad[2, -1].item(), 0)
        self.assertTrue(torch.equal(logits.grad[0, :-2], logits.grad[1, :-2]))

    def test_start_rehearsal_preserves_expert_buttons_and_reset_boundaries(self):
        import numpy as np
        import torch
        from train_behavior_cloning import cold_start_inputs, demonstration_starts

        sessions = np.array(list("aaaabbcc"))
        segments = np.array([0, 0, 1, 1, 0, 0, 0, 1])
        actions = np.zeros((len(sessions), 4), dtype=int)
        actions[1, 3] = 1  # First action can be a stationary swing.
        actions[3, 0] = 1
        actions[4, 0] = 2
        actions[7, 0] = 1  # Movement after a discontinuity is not a clip start.
        indices = demonstration_starts(sessions, segments, actions)
        self.assertEqual(indices.tolist(), [1, 4])
        raw = torch.ones((2, dataset.HISTORY_LENGTH * dataset.FEATURE_COUNT))
        raw[:, -dataset.FEATURE_COUNT + 8] = 0
        inputs = cold_start_inputs(raw)
        self.assertTrue((inputs[:, :-dataset.FEATURE_COUNT] == 0).all())
        self.assertTrue((inputs[:, -dataset.FEATURE_COUNT + 8] == 1).all())
        self.assertTrue((inputs[:, -dataset.FEATURE_COUNT + 9:-dataset.FEATURE_COUNT + 13] == 0).all())
        self.assertTrue((inputs[:, -dataset.FEATURE_COUNT + 13:] == raw[:, -dataset.FEATURE_COUNT + 13:]).all())
        self.assertTrue((raw[:, :dataset.FEATURE_COUNT] == 1).all())

    def test_setup_pause_ends_at_first_action_and_keeps_tactical_waits(self):
        import numpy as np
        from train_behavior_cloning import recording_lead_in

        frames = np.zeros((15, dataset.FEATURE_COUNT * dataset.HISTORY_LENGTH))
        latest = frames[:, -dataset.FEATURE_COUNT:]
        latest[:, 4] = 1
        latest[3:6, dataset.ENEMY_START + 5] = 1  # Enemy in view.
        latest[6:9, 0] = [0.5, 0.55, 0.6]  # Already moving.
        times = np.arange(15) / 10
        times[11] += 2  # A longer deliberate wait.
        sessions = np.repeat(np.arange(5), 3)
        marked = recording_lead_in(frames, sessions, np.zeros(15), times, [2, 5, 8, 11, 12])
        self.assertEqual(np.flatnonzero(marked).tolist(), [0, 1])

    def test_fight_labels_stop_at_visibility_segment_and_clip_boundaries(self):
        import numpy as np
        from train_behavior_cloning import fighting_frames
        frames = np.zeros((8, dataset.FEATURE_COUNT * dataset.HISTORY_LENGTH))
        latest = frames[:, -dataset.FEATURE_COUNT:]
        latest[:, 4] = 1
        latest[:, dataset.ENEMY_START:dataset.ENEMY_START + 6] = [.04, 0, .03, .04, .9, 1]
        latest[3, dataset.ENEMY_START + 5] = 0
        latest[7, 4] = 0
        actions = np.zeros((8, 4), dtype=int)
        actions[[1, 7], 3] = 1
        result = fighting_frames(frames, actions, np.array(list("aaaaaabb")), np.array([0,0,0,0,0,1,1,1]))
        self.assertEqual(result.tolist(), [True, True, True, False, False, False, False, False])

    def test_export_train_save_and_reload_with_session_holdout(self):
        from stable_baselines3 import PPO
        import numpy as np
        from train_behavior_cloning import train
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for index in range(3):
                session = root / f"demo-synthetic-{index}"
                session.mkdir()
                m = manifest()
                m["id"] = f"synthetic-{index}"
                rows = [observation(100.1 + offset * 0.1) for offset in range(35)]
                for row in rows:
                    row["features"][0] = 0.4 + index * 0.01
                inputs = [event(100, "right")]
                m.update(observationCount=len(rows), usableObservationCount=len(rows), inputEventCount=1)
                (session / "manifest.json").write_text(json.dumps(m))
                (session / "events.jsonl").write_text("\n".join(json.dumps(x) for x in inputs + rows) + "\n")
            archive = root / "dataset.npz"
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(dataset.main(["export", str(root), "--output", str(archive)]), 0)
                report = train(archive, root / "policy", epochs=3, batch_size=16)
            self.assertTrue(set(report["trainingSessions"]).isdisjoint(report["validationSessions"]))
            self.assertEqual(report["stage"], "behavior-cloning")
            self.assertFalse(report["criticTrained"])
            self.assertLess(report["metrics"][-1]["validation_nll"], report["metrics"][0]["validation_nll"])
            loaded = PPO.load(root / "policy/model.zip", device="cpu")
            with np.load(archive, allow_pickle=False) as data:
                predicted, _ = loaded.predict(data["observations"][:2], deterministic=True)
            self.assertEqual(predicted.shape, (2, 4))
            self.assertTrue(((predicted >= 0) & (predicted < np.asarray([3, 3, 2, 2]))).all())
            with self.assertRaises(ValueError):
                train(archive, root / "policy", epochs=1)  # preserve existing models


if __name__ == "__main__":
    unittest.main()
