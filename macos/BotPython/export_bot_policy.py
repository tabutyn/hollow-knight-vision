"""Export a supported SB3 PPO actor for deterministic native inference."""
from __future__ import annotations
import argparse
import hashlib
import json
from pathlib import Path

from hkv_bot_dataset import ACTION_SIZES, HISTORY_LENGTH, SCHEMAS


def export_policy(model_directory, output_directory=None):
    import numpy as np
    import torch
    from stable_baselines3 import PPO
    from stable_baselines3.common.torch_layers import FlattenExtractor
    from stable_baselines3.common.distributions import MultiCategoricalDistribution
    from bot_projected_ppo import ColumnExtractor

    root = Path(model_directory)
    output = Path(output_directory) if output_directory else root
    manifest = json.loads((root / 'manifest.json').read_text())
    digest = hashlib.sha256((root / 'model.zip').read_bytes()).hexdigest()
    version = manifest.get('schemaVersion')
    names = SCHEMAS.get(version)
    if (names is None or manifest.get('featureNames') != names
            or manifest.get('historyLength') != HISTORY_LENGTH or manifest.get('actionSizes') != ACTION_SIZES
            or manifest.get('modelSHA256') != digest):
        raise ValueError('The checkpoint manifest or observation contract is incompatible')
    model = PPO.load(root / 'model.zip', device='cpu')
    model.policy.set_training_mode(False)
    if (model.observation_space.shape != (len(names) * HISTORY_LENGTH,)
            or model.action_space.nvec.tolist() != ACTION_SIZES
            or type(model.policy.pi_features_extractor) not in (FlattenExtractor, ColumnExtractor)
            or not isinstance(model.policy.action_dist, MultiCategoricalDistribution)):
        raise ValueError('Only flat observations and MultiDiscrete PPO actors are supported')
    modules = list(model.policy.mlp_extractor.policy_net)
    layers = []
    for index in range(0, len(modules), 2):
        if (index + 1 >= len(modules) or not isinstance(modules[index], torch.nn.Linear)
                or not isinstance(modules[index + 1], torch.nn.Tanh)):
            raise ValueError('The policy hidden layers must be Linear/Tanh pairs')
        layers.append((modules[index], 'tanh'))
    if not isinstance(model.policy.action_net, torch.nn.Linear):
        raise ValueError('Unsupported action head')
    layers.append((model.policy.action_net, 'linear'))
    payload = {'schemaVersion': 1, 'observationSchemaVersion': version,
               'modelName': manifest.get('name', root.name), 'featureNames': names,
               'historyLength': HISTORY_LENGTH, 'actionSizes': ACTION_SIZES,
               'sourceCheckpointSHA256': digest, 'layers': []}
    if isinstance(model.policy.pi_features_extractor, ColumnExtractor):
        payload.update(schemaVersion=2, inputColumns=model.policy.pi_features_extractor.indices.tolist())
    if (manifest.get('supportsMaskedKnight') or manifest.get('maskedKnightTrainingExamples', 0) > 0) and manifest.get('botDomain') == 'gameplay':
        payload['supportsMaskedKnight'] = True
    for module, activation in layers:
        weights = module.weight.detach().cpu().numpy().astype(np.float32)
        biases = module.bias.detach().cpu().numpy().astype(np.float32)
        if not np.isfinite(weights).all() or not np.isfinite(biases).all():
            raise ValueError('Non-finite policy weights')
        payload['layers'].append({'inputSize': int(module.in_features), 'outputSize': int(module.out_features),
                                  'activation': activation, 'weights': weights.reshape(-1).tolist(),
                                  'biases': biases.tolist()})
    output.mkdir(parents=True, exist_ok=True)
    raw = (json.dumps(payload, separators=(',', ':'), allow_nan=False) + '\n').encode()
    policy_path = output / 'runtime-policy.json'
    temporary = output / '.runtime-policy.json.tmp'
    temporary.write_bytes(raw); temporary.replace(policy_path)
    runtime = {'schemaVersion': 1, 'policyFile': policy_path.name,
               'policySHA256': hashlib.sha256(raw).hexdigest(), 'sourceCheckpointSHA256': digest}
    temporary = output / '.runtime-manifest.json.tmp'
    temporary.write_text(json.dumps(runtime, indent=2) + '\n')
    temporary.replace(output / 'runtime-manifest.json')
    return payload, model


def write_parity_fixture(model, output, dataset=None):
    import numpy as np
    import torch
    rng = np.random.default_rng(1234)
    width = model.observation_space.shape[0]
    inputs = np.concatenate([np.zeros((1, width), dtype=np.float32),
                             rng.uniform(-1, 1, (15, width)).astype(np.float32)])
    if dataset:
        with np.load(dataset, allow_pickle=False) as archive:
            recorded = archive['observations']
            if recorded.ndim != 2 or recorded.shape[1] != width or not np.isfinite(recorded).all():
                raise ValueError('Invalid parity dataset')
            selected = np.linspace(0, len(recorded) - 1, min(32, len(recorded)), dtype=int)
            inputs = np.concatenate([inputs, recorded[selected].astype(np.float32)])
    logits, actions = [], []
    with torch.no_grad():
        # Playback makes one decision at a time. Batched matrix multiplication can
        # accumulate differently from the single-observation call used in the game.
        for observation in inputs:
            tensor = torch.as_tensor(observation).unsqueeze(0)
            features = model.policy.extract_features(tensor, model.policy.pi_features_extractor)
            latent = model.policy.mlp_extractor.forward_actor(features)
            logits.append(model.policy.action_net(latent)[0].cpu().tolist())
            action, _ = model.predict(observation, deterministic=True)
            actions.append(action.tolist())
    Path(output).write_text(json.dumps({'inputs': inputs.tolist(), 'logits': logits,
                                       'actions': actions}, allow_nan=False))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('model_directory', type=Path)
    parser.add_argument('--output-directory', type=Path)
    parser.add_argument('--parity-fixture', type=Path)
    parser.add_argument('--parity-dataset', type=Path)
    args = parser.parse_args()
    payload, model = export_policy(args.model_directory, args.output_directory)
    if args.parity_fixture:
        write_parity_fixture(model, args.parity_fixture, args.parity_dataset)
    print(json.dumps({'model': payload['modelName'], 'layers': len(payload['layers']),
                      'sourceCheckpointSHA256': payload['sourceCheckpointSHA256']}))
