"""Drive the installed Python Gym wrapper with a deterministic receiver process."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from unittest.mock import patch

import numpy as np
from stable_baselines3 import PPO
import torch

source, vision, native_tests, fixture_python = (Path(arg).absolute() for arg in sys.argv[1:])
sys.path.insert(0, str(source))
from bot_policy_continuation import playback_checkpoint
from export_bot_policy import export_policy
from hacker_gym import HackerGym, save_json
from hkv_bot_dataset import ACTION_SIZES, HELD_ATTACK_COST, HISTORY_LENGTH, LANDING_GEOMETRY_POLICY_VERSION, SCHEMAS
from train_hacker_gym import sha


def register_fixture_detector(support, detector):
    model_id = "shared-object-model"
    registry = support / "models-v1"
    shutil.copytree(detector, registry / model_id / "v1.0")
    manifest = json.loads((detector / "training.json").read_bytes())
    point = dict(classIdentifier=model_id, version=dict(major=1, minor=0),
                 sourceRunIdentifier=manifest["id"], createdAt="2026-01-01T00:00:00Z",
                 modelFilename=manifest["modelFilename"], trainedClassIdentifiers=manifest["trainedClassIdentifiers"])
    save_json(registry / "registry.json", dict(schemaVersion=1, objects={
        model_id: dict(restorePoints=[point], activeMajorVersion=1)
    }))


def verify_receiver_lifecycle(root, source, vision, native_tests, fixture_python):
    fixture = root / "receiver-lifecycle"
    worker = native_tests.with_suffix(".exe" if os.name == "nt" else "")
    subprocess.run(["dotnet", native_tests, "--export-gym-receiver-probe", fixture], check=True, timeout=30)
    subprocess.run([fixture_python, "-B", Path(__file__).with_name("gameplay_gym_interop.py"),
                    source, fixture, "--prepare-receiver-fixture"], check=True, timeout=30)
    detector = fixture / "visual-onnx"
    visual_support = fixture / "visual-support"
    register_fixture_detector(visual_support, detector)
    version = LANDING_GEOMETRY_POLICY_VERSION
    fields = SCHEMAS[version]
    attack = np.array([2, 0, 0, 1])
    release = np.array([2, 0, 0, 0])
    configuration = dict(game=worker, vision=vision, labels=fixture / "labels.json", version=version,
                         supports_masked=True, max_steps=3, visual_support_root=visual_support)
    variables = dict(HKV_GYM_TEST_FIXTURE=str(fixture), HKV_GYM_TEST_INVINCIBLE="")
    with patch.dict(os.environ, variables):
        os.environ.pop("HKV_GYM_VISUAL_MODEL_DIR", None)
        starts = {}
        for domain in ("hacker", "gameplay"):
            env = HackerGym(**configuration, domain=domain)
            try:
                observation, _ = env.reset(seed=100_008, options={"render_hz": 120})
                starts[domain] = observation.copy()
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

        with patch.dict(os.environ, HKV_GYM_TEST_ROLLOUT="1"):
            for domain, observation in starts.items():
                support = root / f"ppo-resume-{domain}"
                if domain == "gameplay":
                    register_fixture_detector(support, detector)
                verify_learner_resume(support, source, configuration | {"domain": domain}, observation)


def verify_learner_resume(support, source, configuration, first_observation):
    torch.set_num_threads(1)
    worker, vision = configuration["game"], configuration["vision"]
    domain = configuration["domain"]
    models = support / "bot-v1/models" / domain
    base = models / "bot-base"
    base.mkdir(parents=True)
    shutil.copyfile(configuration["labels"], support / "world-ground-lines-v2.json")
    run = support / "bot-v1/training-runs/base"
    run.mkdir(parents=True)
    dataset = run / "dataset.npz"
    np.savez(dataset, observations=np.stack([first_observation, first_observation]),
             actions=np.zeros((2, 4), dtype=np.int64), session_ids=["train", "held"], segments=[0, 0])
    policy = PPO("MlpPolicy", HackerGym(**configuration), device="cpu", n_steps=128, batch_size=64, seed=4)
    policy.save(base / "model.zip")
    base_hash = sha(base / "model.zip")
    version = configuration["version"]
    save_json(base / "manifest.json", dict(name="PPO resume fixture", botDomain=domain, schemaVersion=version,
        featureNames=SCHEMAS[version], historyLength=HISTORY_LENGTH, actionSizes=ACTION_SIZES,
        modelSHA256=base_hash, datasetSHA256=sha(dataset), trainingExamples=1,
        trainingSessions=["train"], validationSessions=["held"]))
    export_policy(base)
    save_json(base / "training-result.json", dict(botDomain=domain, modelDirectory=base.name,
        createdAt="2026-01-01T00:00:00Z", nativeParity=True))

    # The runtime probe requires a complete mod layout. The test apphost remains
    # the executable; these files are markers for the Windows preflight only.
    game_data = worker.parent / "hollow_knight_Data"
    game_data.mkdir()
    try:
        managed = game_data / "Managed"
        receiver = managed / "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
        receiver.parent.mkdir(parents=True)
        for path in (managed / "Assembly-CSharp.dll", managed / "MMHOOK_Assembly-CSharp.dll", receiver):
            path.write_text("Gym receiver fixture\n", encoding="utf-8")

        command = [sys.executable, "-B", source / "train_hacker_gym.py", "--game", worker, "--vision", vision, "--domain", domain,
                   "--support", support, "--rounds", "1", "--training-steps", "128",
                   "--max-steps", "128", "--stall-steps", "140", "--hours", ".05"]
        previous_hash, previous_policy = base_hash, policy
        for number in range(2):
            subprocess.run(command, check=True, timeout=200)
            (state_path,) = (support / "bot-v1/gym").glob("*/state.json")
            state = json.loads(state_path.read_bytes())
            evidence = json.loads((state_path.parent / f"comparison-{number:04d}.json").read_bytes())
            assert state["round"] == number + 1 and "pendingPPO" not in state
            assert state["identity"]["domain"] == domain
            assert ("visualDetectorAndHUD" in state["identity"]) == (domain == "gameplay")
            assert evidence["learnerSourceSHA256"] == previous_hash and evidence["trainingSeeds"]
            assert evidence["method"] == "ppo" and evidence["trainingSteps"] == 128
            assert not evidence["matched"] and state["champion"] == base.name
            assert evidence["incumbent"]["missing"] == evidence["candidate"]["missing"] == 0
            learner_path = state_path.parent / state["learner"]
            assert sha(learner_path) == state["learnerSHA256"] != previous_hash
            learner = PPO.load(learner_path, device="cpu")
            assert learner.num_timesteps == (number + 1) * 128
            assert not torch.equal(previous_policy.policy.action_net.weight, learner.policy.action_net.weight)
            selected = playback_checkpoint(models, domain, native_verifier=vision if os.name == "nt" else None)
            assert selected is not None and selected.samefile(base)
            previous_hash, previous_policy = state["learnerSHA256"], learner
        assert sha(base / "model.zip") == base_hash
        print(f"PASS installed Windows {domain} Gym resumes the saved PPO learner and keeps the verified playback policy")
    finally:
        shutil.rmtree(game_data)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="hkv-gym-lifecycle-") as directory:
        verify_receiver_lifecycle(Path(directory), source, vision, native_tests, fixture_python)
