"""Drive the installed Python Gym wrapper with a deterministic receiver process."""

import os
from pathlib import Path
import subprocess
import sys
import tempfile
from unittest.mock import patch

import numpy as np

source, vision, native_tests, fixture_python = (Path(arg).absolute() for arg in sys.argv[1:])
sys.path.insert(0, str(source))
from hacker_gym import HackerGym
from hkv_bot_dataset import HELD_ATTACK_COST, LANDING_GEOMETRY_POLICY_VERSION, SCHEMAS


def verify_receiver_lifecycle(root, source, vision, native_tests, fixture_python):
    fixture = root / "receiver-lifecycle"
    worker = native_tests.with_suffix(".exe" if os.name == "nt" else "")
    subprocess.run(["dotnet", native_tests, "--export-gym-receiver-probe", fixture], check=True, timeout=30)
    subprocess.run([fixture_python, "-B", Path(__file__).with_name("gameplay_gym_interop.py"),
                    source, fixture, "--prepare-receiver-fixture"], check=True, timeout=30)
    version = LANDING_GEOMETRY_POLICY_VERSION
    fields = SCHEMAS[version]
    attack = np.array([2, 0, 0, 1])
    release = np.array([2, 0, 0, 0])
    configuration = dict(game=worker, vision=vision, labels=fixture / "labels.json", version=version,
                         supports_masked=True, max_steps=3, visual_support_root=fixture / "visual-support")
    variables = dict(HKV_GYM_TEST_FIXTURE=str(fixture), HKV_GYM_TEST_INVINCIBLE="",
                     HKV_GYM_VISUAL_MODEL_DIR=str(fixture / "visual-onnx"))
    with patch.dict(os.environ, variables):
        for domain in ("hacker", "gameplay"):
            env = HackerGym(**configuration, domain=domain)
            try:
                observation, _ = env.reset(seed=100_008, options={"render_hz": 120})
                game, encoder, episode_directory = env.worker, env.encoder, env.directory
                assert env.usable and env.render_hz == 120, (domain, env.issues)
                last = observation[-len(fields):]
                assert last[fields.index("enemy_0_present")] == 1
                assert last[fields.index("geo_cache_present")] == (domain == "hacker")
                for step, action in enumerate((attack, attack, release), 1):
                    observation, reward, terminated, truncated, info = env.step(action)
                    assert env.usable and env.observation_space.contains(observation), (domain, env.issues)
                    assert info["applied"] == action.tolist() and not terminated and truncated == (step == 3)
                    assert info["heldAttackDecisions"] == (step >= 2) and info["blueHealthGained"] == (step == 3)
                    assert info["reason"] == ("time-limit" if step == 3 else "")
                    route_reward = (env.path_progress or 0) + .5 * (env.path_alignment or 0)
                    expected = -.015 + (1 if step == 3 else -HELD_ATTACK_COST if step == 2 else 0)
                    assert np.isclose(reward - route_reward, expected, atol=1e-7), (domain, step, reward)
                    assert observation[-len(fields):][fields.index("previous_attack")] == action[3]
                if domain == "gameplay":
                    assert env.perception["visual"] == env.perception["floorFrames"] == 4, env.perception
            finally:
                env.close()
            assert game.poll() is not None and encoder.poll() is not None and not episode_directory.exists()
            print(f"PASS installed Windows {domain} Gym reset, receiver controls, rewards, observation history, and cleanup")

        with patch.dict(os.environ, HKV_GYM_TEST_INVINCIBLE="1"):
            env = HackerGym(**configuration)
            try:
                env.reset(seed=100_009)
            except RuntimeError as error:
                assert "Invalid worker, sequence, save or damage state" in str(error)
                assert env.worker is None and env.encoder is None and not env.directory.exists()
            else:
                raise AssertionError("The Gym wrapper accepted an invincible worker.")
            finally:
                env.close()
            print("PASS installed Windows Gym rejects and closes an invincible episode at reset")


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="hkv-gym-lifecycle-") as directory:
        verify_receiver_lifecycle(Path(directory), source, vision, native_tests, fixture_python)
