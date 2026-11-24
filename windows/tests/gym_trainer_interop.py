"""Exercise the installed runtime and PPO publication without opening a game or a user save."""
from argparse import Namespace
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

import gymnasium as gym
import numpy as np
from stable_baselines3 import PPO
import torch

install = Path(sys.argv[1]).resolve()
sys.path.insert(0, str(install / "training/BotPython"))
from bot_policy_continuation import playback_checkpoint
from bot_projected_ppo import warm_start
from export_bot_policy import export_policy, write_parity_fixture
from gym_general_training import train_batch
from hacker_gym import EpisodeScore, save_json
from hkv_bot_dataset import ACTION_SIZES, HISTORY_LENGTH, LANDING_GEOMETRY_POLICY_VERSION, SCHEMAS
from train_atlas_policy import AtlasCorridor, export_atlas
from train_hacker_gym import publish, sha, windows_gym_identity


class ReplayRoom(gym.Env):
    version = LANDING_GEOMETRY_POLICY_VERSION
    max_steps = 96
    usable = True
    action_space = gym.spaces.MultiDiscrete(ACTION_SIZES)
    observation_space = gym.spaces.Box(-4, 4, (len(SCHEMAS[version]) * HISTORY_LENGTH,), np.float32)

    def reset(self, *, seed=None, options=None):
        super().reset(seed=seed)
        self.last_frame = dict(step=0, heroX=34.5, heroY=11.41, velocityX=0., grounded=True, health=5, damage=0,
                               heroAvailable=True, sceneName="Tutorial_01", gameState="PLAYING")
        self.score = EpisodeScore(self.last_frame)
        return self.observation(), {}

    def observation(self):
        row = np.zeros(self.observation_space.shape, np.float32)
        row[-len(SCHEMAS[self.version])] = self.last_frame["heroX"] / 100
        return row

    def step(self, action):
        frame = self.last_frame
        move = (0, -.3, .3)[action[0]]
        self.last_frame = frame | dict(step=frame["step"] + 1, heroX=frame["heroX"] + move, velocityX=move * 10)
        reward, _ = self.score.advance(self.last_frame, attack_down=bool(action[3]))
        cut = self.last_frame["step"] == self.max_steps
        return self.observation(), reward, False, cut, dict(reason="limit" if cut else "", applied=action.tolist())


def verify_installed_runtime(root, vision, detector_source):
    game = root / "Fixture Game" / "hollow_knight.exe"
    managed = game.parent / "hollow_knight_Data/Managed"
    receiver = managed / "Mods/HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll"
    receiver.parent.mkdir(parents=True)
    for file in (game, managed / "Assembly-CSharp.dll", managed / "MMHOOK_Assembly-CSharp.dll", receiver):
        file.write_text(file.name)
    support = root / "Fixture Support"
    support.mkdir()
    detector = root / "Fixture Detector"
    shutil.copytree(detector_source, detector)
    os.environ["HKV_GYM_VISUAL_MODEL_DIR"] = str(detector)
    runtime = Namespace(game=game, vision=vision, domain="hacker", support=support)
    hacker = windows_gym_identity(runtime)
    assert hacker["receiver"].lower() == sha(receiver)
    assert "visualDetectorAndHUD" not in hacker
    runtime.domain = "gameplay"
    visual = windows_gym_identity(runtime)
    assert visual["visionSource"] == hacker["visionSource"]
    (support / "initialization-language-v1.txt").write_text("ja")
    assert windows_gym_identity(runtime)["visualDetectorAndHUD"] != visual["visualDetectorAndHUD"]
    receiver.unlink()
    try:
        windows_gym_identity(runtime)
    except ValueError as error:
        assert "HollowKnightVisionInputReceiver" in str(error)
    else:
        raise AssertionError("A missing receiver passed preflight.")

    if os.name == "nt":
        launcher = install / "training/train-hacker-gym-windows.ps1"
        command = ["pwsh", "-NoProfile", "-File", launcher, "-Game", game, "-Python", sys.executable, "--help"]
        result = subprocess.run(command, capture_output=True, text=True, timeout=45)
        assert result.returncode == 0 and "--training-steps" in result.stdout, result.stderr


def verify_ppo_publication(root, vision):
    torch.set_num_threads(1)
    models = root / "bot-v1/models/hacker"
    base = models / "bot-base"
    base.mkdir(parents=True)
    work = root / "bot-v1/gym/fixture"
    work.mkdir(parents=True)
    room = ReplayRoom()
    policy = PPO("MlpPolicy", room, device="cpu", n_steps=128, batch_size=64, seed=4)
    policy.save(base / "model.zip")
    manifest = dict(name="Fixture", botDomain="hacker", schemaVersion=room.version,
                    featureNames=SCHEMAS[room.version], historyLength=HISTORY_LENGTH,
                    actionSizes=ACTION_SIZES, modelSHA256=sha(base / "model.zip"),
                    trainingExamples=4, validationSessions=["held"])
    save_json(base / "manifest.json", manifest)
    export_policy(base)
    save_json(base / "training-result.json", dict(botDomain="hacker", modelDirectory=base.name,
              createdAt="2026-01-01T00:00:00Z", nativeParity=True))
    dataset = work / "dataset.npz"
    observations = [room.reset()[0]]
    action = np.array([2, 0, 0, 0], dtype=np.int64)
    observations.extend(room.step(action)[0] for _ in range(3))
    np.savez(dataset, observations=np.stack(observations), session_ids=["held"] * 4,
             actions=np.tile(action, (4, 1)))
    learner_path, seeds, _, _ = train_batch(room, policy, base / "model.zip", work, 0, 128)
    learner = PPO.load(learner_path, device="cpu")
    assert seeds and learner.num_timesteps == 128
    assert any(not torch.equal(value, learner.policy.state_dict()[key]) for key, value in policy.policy.state_dict().items())
    checkpoint = publish(learner, base, work, models, dataset, dict(method="ppo", parent=base.name, base=base.name), vision)
    assert checkpoint.parent == models and playback_checkpoint(models, "hacker") == checkpoint
    assert sha(checkpoint / "model.zip") != manifest["modelSHA256"]
    print("PASS installed Windows Gym trained 128 PPO decisions, verified native export, and selected the new checkpoint.")
    verify_atlas_direction(work, vision, checkpoint)

    projection = [0, room.observation_space.shape[0] - 1]
    projected = warm_start(learner, room, projection, seed=11)
    compact = work / "projected"
    compact.mkdir()
    projected.save(compact / "model.zip")
    save_json(compact / "manifest.json", manifest | dict(modelSHA256=sha(compact / "model.zip")))
    runtime, exported = export_policy(compact)
    assert runtime["schemaVersion"] == 2 and runtime["inputColumns"] == projection
    fixture = compact / "parity.json"
    write_parity_fixture(exported, fixture, observations=np.stack(observations))
    subprocess.run([vision, "--hacker-gym-observations", "verify", compact, fixture], check=True, timeout=45)
    print("PASS installed Windows runtime matched PPO logits and decisions with projected observation columns.")


def verify_atlas_direction(work, vision, checkpoint):
    folder = work / "Atlas direction fixture"
    folder.mkdir()
    for name in ("model.zip", "runtime-policy.json", "runtime-manifest.json"):
        shutil.copyfile(checkpoint / name, folder / name)

    class WorldFixture(AtlasCorridor):
        def world_observation(self):
            return self.row.copy()

    room = WorldFixture()
    policy = PPO("MlpPolicy", room, device="cpu", n_steps=16, batch_size=8, seed=7)
    atlas_checkpoint = folder / "atlas-model.zip"
    policy.save(atlas_checkpoint)
    runtime_path = folder / "runtime-policy.json"
    runtime = json.loads(runtime_path.read_text(encoding="utf-8"))
    runtime["atlasPolicy"] = export_atlas(folder, {"checkpointSHA256": sha(atlas_checkpoint)})
    save_json(runtime_path, runtime)
    manifest_path = folder / "runtime-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    save_json(manifest_path, manifest | {"policySHA256": sha(runtime_path)})

    inputs = np.random.default_rng(42).uniform(0, 1, (128, 8)).astype(np.float32)
    inputs[:, 7] = np.tile([-1, 0, 1, 0], 32)
    inputs[:4, 1] = inputs[:4, 0]
    canonical, mirrored = [], []
    for row in inputs:
        room.row = row
        room.previous = row[7]
        canonical.append(room.observation())
        mirrored.append(room.mirrored)
    actions, _ = policy.predict(np.stack(canonical), deterministic=True)
    world_actions = []
    for action, mirror in zip(actions, mirrored, strict=True):
        room.mirrored = mirror
        world_actions.append(room.world_action(action))

    fixture = folder / "world-directions.json"
    save_json(fixture, {"inputs": inputs.tolist(), "worldActions": world_actions})
    result = subprocess.run([vision, "--hacker-gym-observations", "verify-atlas-direction", folder, fixture],
                            check=True, capture_output=True, text=True, timeout=45)
    assert f"Verified {len(inputs)} Atlas direction observations." in result.stdout
    print(f"PASS installed Windows runtime matched {len(inputs)} Stable-Baselines3 Atlas directions.")


if __name__ == "__main__":
    program = install / ("HollowKnightVision.Windows.Host.exe" if os.name == "nt" else "HollowKnightVision.Windows.Host")
    with tempfile.TemporaryDirectory(prefix="hkv-gym-trainer-") as directory:
        root = Path(directory)
        verify_installed_runtime(root, program, Path(sys.argv[2]))
        verify_ppo_publication(root, program)
