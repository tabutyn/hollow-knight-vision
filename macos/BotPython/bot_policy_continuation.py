"""Carry an exported policy into the next demonstration update."""
from contextlib import contextmanager
import hashlib
import json
from pathlib import Path

from bot_training_ipc import exclusive_file_lock
from hkv_bot_dataset import ACTION_SIZES, FEATURE_NAMES, HISTORY_LENGTH, SCHEMAS


@contextmanager
def publication_lock(models):
    """Serialize same-domain demonstration updates and the final publication of Gym winners."""
    models.mkdir(parents=True, exist_ok=True)
    with exclusive_file_lock(models / ".publication.lock"):
        yield


def playback_checkpoint(models, domain, fallback=None):
    """Select the same ready local version as playback, honoring an explicit rollback."""
    file = models / "active.json"
    selection = json.loads(file.read_bytes()) if file.is_file() else {}
    current = models / selection["directory"] if selection.get("directory") else fallback
    if fallback and current and not current.is_dir() and current.name == fallback.name:
        current = fallback
    candidates = []
    for path in models.glob("bot-*/training-result.json"):
        result = json.loads(path.read_bytes())
        if (result.get("nativeParity") and result.get("botDomain") == domain
                and result.get("modelDirectory") == path.parent.name and (path.parent / "runtime-manifest.json").is_file()):
            candidates.append((result["createdAt"], path.parent))
    if not candidates:
        return current
    latest = max(candidates)[1]
    return current if current and latest.name == selection.get("manualChoiceThrough") else latest


def load_parent(directory, environment):
    import torch
    from stable_baselines3 import PPO

    directory = Path(directory)
    path = directory / "model.zip"
    manifest = json.loads((directory / "manifest.json").read_bytes())
    names = SCHEMAS.get(manifest.get("schemaVersion"))
    if (names is None or names != manifest.get("featureNames") or manifest.get("historyLength") != HISTORY_LENGTH
            or manifest.get("actionSizes") != ACTION_SIZES or hashlib.sha256(path.read_bytes()).hexdigest() != manifest.get("modelSHA256")):
        raise ValueError("Training parent checkpoint or observation contract failed verification")
    parent = PPO.load(path, device="cpu")
    if parent.observation_space.shape != (len(names) * HISTORY_LENGTH,) or parent.action_space.nvec.tolist() != ACTION_SIZES:
        raise ValueError("Training parent network does not match its manifest")
    if names == FEATURE_NAMES:
        return parent, manifest

    # An observation upgrade can add inputs or retire old fields. Transfer both
    # networks by feature name in each history frame. New columns start silent.
    child = PPO("MlpPolicy", environment, device="cpu", policy_kwargs=parent.policy_kwargs)
    old = parent.policy.state_dict()
    weights = child.policy.state_dict()
    shared = [(i, FEATURE_NAMES.index(name)) for i, name in enumerate(names) if name in FEATURE_NAMES
              and not (manifest.get("schemaVersion") == 13 and name.startswith("marking_") and name.endswith("_kind"))]
    with torch.no_grad():
        for key, tensor in weights.items():
            if key in ("mlp_extractor.policy_net.0.weight", "mlp_extractor.value_net.0.weight"):
                tensor.zero_()
                for frame in range(HISTORY_LENGTH):
                    before = [frame * len(names) + i for i, _ in shared]
                    after = [frame * len(FEATURE_NAMES) + j for _, j in shared]
                    tensor[:, after] = old[key][:, before]
                    if manifest.get("schemaVersion") == 13:
                        for name in ("marking_ground_count", "marking_wall_count", "marking_spike_count", "marking_door_count"):
                            tensor[:, frame * len(FEATURE_NAMES) + FEATURE_NAMES.index(name)] *= .25
                        # Overflow has a different threshold and cannot be transferred.
                        tensor[:, frame * len(FEATURE_NAMES) + FEATURE_NAMES.index("marking_overflow")] = 0
            else:
                tensor.copy_(old[key])
    child.policy.load_state_dict(weights)
    child.num_timesteps = parent.num_timesteps
    return child, manifest


def gym_observations(root, domain, parent, prior, limit=2048, *, routes=False):
    """Sample actual training observations from this family's Gym runs, excluding evaluation starts."""
    import numpy as np

    # Old raster-only rehearsals cannot recover exact endpoints or spike provenance.
    # Fresh K captures/Gym rollouts supply the new schema; never invent geometry.
    if prior.get('schemaVersion', __import__('hkv_bot_dataset').SCHEMA_VERSION) != __import__('hkv_bot_dataset').SCHEMA_VERSION:
        return None
    width = len(FEATURE_NAMES) * HISTORY_LENGTH
    bases = {parent.name, prior.get("gymEvidence", {}).get("base"), *prior.get("gymTrainingBases", [])}
    bases.discard(None)
    def upgrade(values):
        # Rehearsal from a prior append-only schema keeps all four time steps aligned.
        if values.ndim == 2 and values.shape[1] != width:
            names = next((n for n in SCHEMAS.values() if len(n)*HISTORY_LENGTH == values.shape[1]), None)
            if names and FEATURE_NAMES[:len(names)] == names:
                frames = values.reshape(-1, HISTORY_LENGTH, len(names))
                return np.pad(frames, ((0, 0), (0, 0), (0, len(FEATURE_NAMES)-len(names)))).reshape(-1, width)
        return values

    inherited = np.empty((0, width), np.float32)
    digest = prior.get("gymRoutesSHA256" if routes else "gymRehearsalSHA256")
    if digest:
        path = parent / ("gym-routes.npz" if routes else "gym-rehearsal.npz")
        if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
            raise ValueError("Gym rehearsal archive does not match the parent checkpoint")
        with np.load(path, allow_pickle=False) as archive:
            inherited = upgrade(archive["observations"])
        if inherited.ndim != 2 or inherited.shape[1] != width or not np.isfinite(inherited).all():
            raise ValueError("Incompatible Gym rehearsal observations")
    groups = []
    for state in sorted((root / "gym").glob("*/state.json")):
        identity = json.loads(state.read_bytes()).get("identity", {})
        if identity.get("domain") != domain or identity.get("base") not in bases:
            continue
        shelves = ("protected-routes",) if routes else ("training-observations", "clean-landings", "confirmed-combat",
                                                       "confirmed-wall-strikes", "demonstrated-crossings", "policy-samples")
        for directory in shelves:
            files = sorted((state.parent / directory).rglob("*.npz"))
            # Files from older trainers encode the training seed in their name.
            files = [p for p in files if p.stem.split("_")[-1].isdigit()
                     and 100_000 <= int(p.stem.split("_")[-1]) < 1_000_000]
            if files:
                groups.append(files)
    # Bound disk reads and memory independently of the number of historical episodes.
    rows = []
    if len(inherited):
        n = min(len(inherited), limit // 2 if groups else limit)
        rows.append(inherited[np.linspace(0, len(inherited)-1, n, dtype=int)])
        limit -= n
    if len(groups) > 96:
        groups = [groups[i] for i in np.linspace(0, len(groups)-1, 96, dtype=int)]
    for files in groups:
        n = min(len(files), max(1, 96 // len(groups)))
        count = max(1, limit // len(groups) // n)
        for i in np.linspace(0, len(files)-1, n, dtype=int):
            file = files[i]
            with np.load(file, allow_pickle=False) as data:
                if "seed" in data and int(data["seed"]) != int(file.stem.split("_")[-1]):
                    continue
                values = data["observations"] if "observations" in data else data["inputs"]
                values = upgrade(values)
                if values.ndim == 2 and values.shape[1] == width and len(values):
                    sample = values[np.linspace(0, len(values)-1, min(count, len(values)), dtype=int)]
                    if np.isfinite(sample).all() and np.abs(sample).max() <= 4:
                        rows.append(sample)
    return np.concatenate(rows).astype(np.float32) if rows else np.empty((0, width), np.float32)


def save_gym_observations(directory, manifest, states, *, routes=False):
    import numpy as np

    if states is None or not len(states):
        return
    file = directory / ("gym-routes.npz" if routes else "gym-rehearsal.npz")
    np.savez_compressed(file, observations=states)
    manifest["gymRoutesSHA256" if routes else "gymRehearsalSHA256"] = hashlib.sha256(file.read_bytes()).hexdigest()


def retain_gym_actions(model, reference, states, routes=None):
    """Limit changes to the parent actor on saved Gym practice, including near-tied actions."""
    import torch

    if routes is None:
        routes = states[:0]
    if not len(states) and not len(routes):
        return {}
    with torch.no_grad():
        original = reference.get_distribution(states).mode() if len(states) else None
        route_targets = []
        if len(routes):
            for head in reference.get_distribution(routes).distribution:
                button = head.logits.argmax(1, keepdim=True)
                best = head.logits.gather(1, button).squeeze(1)
                next_best = head.logits.scatter(1, button, -torch.inf).amax(1)
                route_targets.append((button, (best - next_best).clamp(max=.25)))
        trained = [p.detach().clone() for p in model.policy.parameters()]
        for fraction in (1., .5, .25, .125, .0625, .03125, .015625, 1/128, 1/256, 1/512, 1/1024, 1/2048, 0.):
            for p, start, end in zip(model.policy.parameters(), reference.parameters(), trained, strict=True):
                p.copy_(torch.lerp(start, end, fraction))
            agreement = None
            if len(states):
                prediction = model.policy.get_distribution(states).mode()
                agreement = float((prediction == original).all(dim=1).float().mean())
                if agreement < .99:
                    continue
            if len(routes):
                heads = model.policy.get_distribution(routes).distribution
                # One flipped control near spawn can make the rest of a good route unreachable.
                # Keep each winning control ahead of its competitors on productive approaches.
                for head, (button, old_margin) in zip(heads, route_targets):
                    chosen = head.logits.gather(1, button).squeeze(1)
                    competitor = head.logits.scatter(1, button, -torch.inf).amax(dim=1)
                    if (head.logits.argmax(1) != button.squeeze(1)).any() or (chosen-competitor + 1e-6 < .75*old_margin).any():
                        break
                else:
                    return dict(gymPracticeFrames=len(states), parentActionAgreement=agreement,
                                protectedRouteFrames=len(routes), retainedUpdateFraction=fraction)
                continue
            return dict(gymPracticeFrames=len(states), parentActionAgreement=agreement, retainedUpdateFraction=fraction)
    raise ValueError("Could not reproduce the parent actor on saved Gym observations")
