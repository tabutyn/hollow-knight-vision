"""Exercise the paired PPO experiment without starting Hollow Knight."""
from contextlib import redirect_stderr
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
import torch

import compare_policy_inputs as comparison
from bot_training_ipc import exclusive_file_lock
from hkv_bot_dataset import ACTION_SIZES, HISTORY_LENGTH, SCHEMA_VERSION, SCHEMAS


class PairedRoom(gym.Env):
    domain = "hacker"
    supports_masked = False
    max_steps = 2
    observation_space = gym.spaces.Box(-1, 1, (len(SCHEMAS[SCHEMA_VERSION]) * HISTORY_LENGTH,), np.float32)
    action_space = gym.spaces.MultiDiscrete(ACTION_SIZES)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self.steps = 0
        self.last_frame = {"sceneName": "Tutorial_01"}
        self.score = self
        return np.zeros(self.observation_space.shape, dtype=np.float32), {}

    def step(self, action):
        self.steps += 1
        self.last_frame = {"sceneName": "Town"}
        return (np.zeros(self.observation_space.shape, dtype=np.float32), 1.0, False,
                self.steps == self.max_steps, {"reason": "limit"})

    def metrics(self):
        return {"pathProgress": self.steps}


class PolicyComparisonTests(unittest.TestCase):
    def setUp(self):
        directory = TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        self.run_dir = root / "bot-v1/gym/paired run"
        self.run_dir.mkdir(parents=True)
        self.output = root / "paired report"
        if os.name == "nt":
            self.game = root / "Game/hollow_knight.exe"
            managed = self.game.parent / "hollow_knight_Data/Managed"
        else:
            self.game = root / "Game.app/Contents/MacOS/hollow_knight"
            managed = root / "Game.app/Contents/Resources/Data/Managed"
        self.receiver = managed / "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
        self.receiver.parent.mkdir(parents=True)
        self.game.parent.mkdir(parents=True, exist_ok=True)
        for path in (self.game, self.receiver, managed / "Assembly-CSharp.dll", managed / "MMHOOK_Assembly-CSharp.dll"):
            path.write_bytes(path.name.encode())
        self.native = os.environ.get("HKV_TEST_COMPARE_VISION")
        if self.native:
            self.vision = Path(self.native)
        else:
            self.vision = root / "vision"
            self.vision.write_bytes(b"vision")
            if os.name == "nt":
                self.enterContext(patch.object(comparison, "windows_gym_identity", side_effect=lambda args: {
                    "visionSource": comparison.sha(args.vision), "receiver": comparison.sha(self.receiver)}))
        self.arguments = ["--run", str(self.run_dir), "--game", str(self.game), "--vision", str(self.vision),
                          "--output", str(self.output), "--steps", "4", "--eval-seeds", "17", "18"]

    def make_run(self):
        torch.set_num_threads(1)
        # A private winner exercises the same lookup used by the Gym trainer.
        champion = self.run_dir / "winners/bot-fixture"
        champion.mkdir(parents=True)
        actor = PPO("MlpPolicy", PairedRoom(), device="cpu", seed=7, n_steps=4, batch_size=4, n_epochs=1,
                    policy_kwargs={"net_arch": [8, 8]})
        actor.save(champion / "model.zip")
        learner = self.run_dir / "learner.zip"
        shutil.copyfile(champion / "model.zip", learner)
        digest = comparison.sha(learner)
        comparison.write_json(champion / "manifest.json", dict(schemaVersion=SCHEMA_VERSION, botDomain="hacker",
                              featureNames=SCHEMAS[SCHEMA_VERSION], historyLength=HISTORY_LENGTH,
                              actionSizes=ACTION_SIZES, modelSHA256=digest))
        labels, connections = (self.run_dir / name for name in ("authored-ground.json", "path-connections.json"))
        for path in (labels, connections):
            path.write_text("{}")
        identity = dict(domain="hacker", labels=comparison.sha(labels), pathConnections=comparison.sha(connections),
                        max_steps=2, stall_steps=2)
        comparison.write_json(self.run_dir / "state.json", dict(round=1, identity=identity, learner=learner.name,
                              learnerSHA256=digest, champion=champion.name))

    def report(self):
        return json.loads((self.output / "comparison.json").read_bytes())

    def test_comparison_trains_verifies_and_resumes_matched_starts(self):
        self.make_run()
        self.enterContext(patch.object(comparison, "HackerGym", return_value=PairedRoom()))
        comparison.main(self.arguments + ["--stage", "train"])
        trained = self.report()
        self.assertEqual(trained["trials"], [])
        self.assertEqual(trained["sha256"]["receiver"], comparison.sha(self.receiver))
        for role in ("full", "compact"):
            folder = self.output / role
            self.assertEqual(trained["models"][role], comparison.sha(folder / "model.zip"))
            policy = json.loads((folder / "runtime-policy.json").read_bytes())
            self.assertEqual(policy.get("inputColumns"), trained["inputColumns"] if role == "compact" else None)

        if self.native:
            # Exercise the installed entry point when the build job supplies it.
            script = os.environ.get("HKV_TEST_COMPARE_SCRIPT", comparison.__file__)
            subprocess.run([sys.executable, "-B", script, *self.arguments, "--stage", "verify"], check=True, timeout=90)
        else:
            with patch.object(comparison.subprocess, "run") as verify:
                comparison.main(self.arguments + ["--stage", "verify"])
            for role, call in zip(("full", "compact"), verify.call_args_list, strict=True):
                folder = self.output / role
                self.assertEqual(call.args[0], [self.vision, "--hacker-gym-observations", "verify", folder,
                                                folder / "parity.json"])
        hashes = trained["sha256"]
        self.assertEqual(self.report()["nativeParityVisionSHA256"], hashes.get("visionSource", hashes["vision"]))
        comparison.main(self.arguments + ["--stage", "evaluate"])
        evaluated = self.report()
        self.assertEqual([(row["seed"], row["role"]) for row in evaluated["trials"]],
                         [(17, "champion"), (17, "full"), (17, "compact"),
                          (18, "full"), (18, "compact"), (18, "champion")])
        for row in evaluated["trials"]:
            self.assertEqual((row["rooms"], row["reward"], row["reason"], row["metrics"]["pathProgress"]),
                             (["Tutorial_01", "Town"], 2.0, "limit", 2))
        with patch.object(comparison, "episode", side_effect=AssertionError("Replayed a completed start")):
            comparison.main(self.arguments + ["--stage", "evaluate"])
        self.receiver.write_bytes(b"new receiver")
        comparison.main(self.arguments + ["--stage", "evaluate"])
        archive, = self.output.glob("comparison-*.json")
        self.assertEqual(json.loads(archive.read_bytes()), evaluated)
        self.assertEqual(self.report()["evaluationSHA256"]["receiver"], comparison.sha(self.receiver))
        (self.output / "compact/model.zip").write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "saved compact branch"):
            comparison.main(self.arguments + ["--stage", "evaluate"])

    def test_cli_refuses_busy_worker_and_releases_after_failed_preflight(self):
        with exclusive_file_lock(self.run_dir.parent / "worker.lock"):
            with self.assertRaises(BlockingIOError):
                comparison.main(self.arguments)
        self.make_run()
        with self.assertRaisesRegex(ValueError, "rollout length"):
            comparison.main(self.arguments + ["--steps", "3"])
        (self.run_dir / "authored-ground.json").write_text("changed")
        with self.assertRaisesRegex(ValueError, "recorded identity"):
            comparison.main(self.arguments)
        self.assertFalse(self.output.exists())

    def test_cli_uses_platform_verifier_and_rejects_overlapping_seeds(self):
        args = [arg for arg in self.arguments if arg not in ("--vision", str(self.vision))]
        with patch.object(comparison, "compare") as compare:
            comparison.main(args)
        expected = (Path(os.environ["LOCALAPPDATA"]) / "Programs/Hollow Knight Vision/HollowKnightVision.Windows.Host.exe"
                    if os.name == "nt" else Path("/Applications/Hollow Knight Vision.app/Contents/MacOS/HollowKnightVision"))
        self.assertEqual(compare.call_args.args[0].vision, expected)
        with redirect_stderr(io.StringIO()) as error, self.assertRaises(SystemExit) as rejected:
            comparison.main(args + ["--seed", "17"])
        self.assertEqual(rejected.exception.code, 2)
        self.assertIn("distinct seeds", error.getvalue())

    def test_changed_receiver_during_training_leaves_branch_unrecorded(self):
        self.make_run()
        self.enterContext(patch.object(comparison, "HackerGym", return_value=PairedRoom()))
        with patch.object(comparison, "train_branch", side_effect=lambda *args: self.receiver.write_bytes(b"changed")):
            with self.assertRaisesRegex(RuntimeError, "changed during the comparison"):
                comparison.main(self.arguments)
        self.assertEqual(self.report()["models"], {})


if __name__ == "__main__":
    unittest.main()
