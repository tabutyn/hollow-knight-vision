"""Compare one full-width and one compact PPO branch on private King's Pass starts."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time

import torch
from stable_baselines3 import PPO

from bot_policy_inputs import policy_columns
from bot_projected_ppo import warm_start
from bot_training_ipc import exclusive_file_lock
from export_bot_policy import export_policy, write_parity_fixture
from hacker_gym import HackerGym
from hkv_bot_dataset import SCHEMAS
from train_hacker_gym import VISION, gym_checkpoint_path, windows_gym_identity


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, document):
    staged = path.with_suffix(".tmp")
    staged.write_text(json.dumps(document, indent=2, allow_nan=False) + "\n")
    staged.replace(path)


def episode(env, model, seed):
    obs, _ = env.reset(seed=seed)
    visited = [env.last_frame["sceneName"]]
    reward_sum = 0.0
    for step in range(1, env.max_steps + 1):
        action, _ = model.predict(obs, deterministic=True)
        obs, reward, terminated, truncated, info = env.step(action)
        reward_sum += reward
        scene = env.last_frame["sceneName"]
        if scene and scene != visited[-1]:
            visited.append(scene)
        if terminated or truncated:
            return dict(steps=step, reward=reward_sum, reason=info["reason"], rooms=visited,
                        metrics=env.score.metrics())
    raise RuntimeError("Room evaluation exceeded the Gym step limit")


def train_branch(folder, source, env, columns, manifest, seed, steps):
    model = warm_start(source, env, columns, seed)
    env.deadline = time.monotonic() + 1800
    model.learn(total_timesteps=steps)
    env.close()
    folder.mkdir()
    model.save(folder / "model.zip")
    digest = sha(folder / "model.zip")
    metadata = {key: manifest[key] for key in ("schemaVersion", "botDomain", "featureNames", "historyLength", "actionSizes")}
    write_json(folder / "manifest.json", dict(**metadata, name=f"{env.domain}-{folder.name}-input-trial",
               supportsMaskedKnight=env.supports_masked, modelSHA256=digest))
    _, restored = export_policy(folder)
    write_parity_fixture(restored, folder / "parity.json")
    print(f"{folder.name} trained for {model.num_timesteps} steps", flush=True)
    return digest


def compare(args):
    state = json.loads((args.run / "state.json").read_text())
    identity = state["identity"]
    domain = identity["domain"]
    bot_root = args.run.parent.parent
    champion = gym_checkpoint_path(bot_root / "models" / domain, state["champion"], args.run)
    manifest = json.loads((champion / "manifest.json").read_text())
    schema = manifest["schemaVersion"]
    parent = args.run / state["learner"]
    labels = args.run / "authored-ground.json"
    connections = args.run / "path-connections.json"
    files = dict(learner=parent, champion=champion / "model.zip", labels=labels, connections=connections,
                 vision=args.vision, game=args.game)
    if os.name != "nt":
        files["receiver"] = (args.game.parents[1] / "Resources/Data/Managed/Mods"
                             / "HollowKnightVisionInputReceiver/HollowKnightVisionInputReceiver.dll")

    def snapshot():
        hashes = {key: sha(path) for key, path in files.items()}
        if os.name == "nt":
            # The Windows apphost loads the encoder from managed assemblies.
            runtime = argparse.Namespace(vision=args.vision, game=args.game, domain=domain, support=bot_root.parent)
            hashes.update({key: value.lower() for key, value in windows_gym_identity(runtime).items()})
        return hashes

    initial = snapshot()
    if (initial["learner"] != state["learnerSHA256"] or initial["champion"] != manifest["modelSHA256"]
            or manifest["botDomain"] != domain or manifest["featureNames"] != SCHEMAS[schema]
            or initial["labels"] != identity["labels"] or initial["connections"] != identity["pathConnections"]):
        raise ValueError("The selected learner, champion or authored path differs from its recorded identity")
    columns = policy_columns(schema, "motor")
    report_path = args.output / "comparison.json"
    env = HackerGym(game=args.game, vision=args.vision, labels=labels, path_connections=connections,
                    domain=domain, version=schema, supports_masked=manifest.get("supportsMaskedKnight", False),
                    max_steps=identity["max_steps"], stall_steps=identity["stall_steps"],
                    visual_support_root=bot_root.parent if os.name == "nt" else None)
    def verify_files():
        if snapshot() != initial:
            raise RuntimeError("A checkpoint, game input or executable changed during the comparison")
    try:
        if args.stage in ("all", "train"):
            source = PPO.load(parent, device="cpu")
            if args.steps <= 0 or args.steps % source.n_steps:
                raise ValueError(f"Choose a positive multiple of the PPO rollout length ({source.n_steps})")
            args.output.mkdir(parents=True, exist_ok=False)
            report = dict(domain=domain, round=state["round"], learner=state["learner"], champion=champion.name,
                          sha256=initial, schema=schema, inputColumns=columns, trainingSeed=args.seed,
                          stepsPerBranch=args.steps, evaluationSeeds=args.eval_seeds, models={}, trials=[])
            write_json(report_path, report)
            for role, view in (("full", None), ("compact", columns)):
                verify_files()
                digest = train_branch(args.output / role, source, env, view, manifest, args.seed, args.steps)
                verify_files()
                report["models"][role] = digest
                write_json(report_path, report)
            if args.stage == "train":
                return
        report = json.loads(report_path.read_text())
        if (set(report["models"]) != {"full", "compact"} or report["inputColumns"] != columns
                or any(report["sha256"][k] != initial[k] for k in ("learner", "champion", "labels", "connections"))):
            raise ValueError("The comparison requires both trained branches from the recorded run")
        actors = {"champion": PPO.load(files["champion"], device="cpu")}
        for role, digest in report["models"].items():
            verify_files()
            folder = args.output / role
            if sha(folder / "model.zip") != digest:
                raise ValueError(f"The saved {role} branch differs from training")
            actors[role] = PPO.load(folder / "model.zip", device="cpu")
            if args.stage in ("all", "verify"):
                subprocess.run([args.vision, "--hacker-gym-observations", "verify", folder, folder / "parity.json"],
                               check=True, timeout=45)
        if args.stage in ("all", "verify"):
            verify_files()
            report["nativeParityVisionSHA256"] = initial.get("visionSource", initial["vision"])
            write_json(report_path, report)
            if args.stage == "verify":
                return
        if report["trials"] and report["evaluationSHA256"] != initial:
            archive = args.output / f"comparison-{time.time_ns()}.json"
            write_json(archive, report)
            print(f"Archived the previous build's trials in {archive.name}. Replaying all starts.", flush=True)
            report["trials"] = []
        report["evaluationSHA256"] = initial
        write_json(report_path, report)
        for index, seed in enumerate(report["evaluationSeeds"]):
            roles = list(actors)
            for role in roles[index % len(roles):] + roles[:index % len(roles)]:
                if any(row["role"] == role and row["seed"] == seed for row in report["trials"]):
                    continue
                verify_files()
                env.deadline = time.monotonic() + 600
                row = episode(env, actors[role], seed)
                env.close()
                verify_files()
                report["trials"].append(dict(role=role, seed=seed, **row))
                write_json(report_path, report)
                print(seed, role, row["reason"], row["metrics"]["pathProgress"], flush=True)
    finally:
        env.close()


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run", type=Path, required=True, help="Existing PPO run with a verified learner")
    parser.add_argument("--game", type=Path, required=True)
    parser.add_argument("--vision", type=Path, default=VISION)
    parser.add_argument("--output", type=Path, required=True, help="New experiment directory")
    parser.add_argument("--stage", choices=("all", "train", "evaluate", "verify"), default="all",
                        help="Train weights, evaluate paired starts, verify the native export, or run all stages")
    parser.add_argument("--steps", type=int, default=512)
    parser.add_argument("--seed", type=int, default=100071)
    parser.add_argument("--eval-seeds", nargs="+", type=int, default=[23112301, 23112302, 23112303])
    args = parser.parse_args(argv)
    if len(set(args.eval_seeds)) != len(args.eval_seeds) or args.seed in args.eval_seeds:
        parser.error("Training and evaluation require distinct seeds")
    args.run, args.game, args.vision = (path.resolve() for path in (args.run, args.game, args.vision))
    torch.set_num_threads(1)
    with exclusive_file_lock(args.run.parent / "worker.lock", wait=False):
        compare(args)


if __name__ == "__main__":
    main()
