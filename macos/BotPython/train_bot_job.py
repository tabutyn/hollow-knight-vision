"""Snapshot selected demonstrations, train, and compare a candidate without activating it."""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import uuid

from hkv_bot_dataset import FEATURE_COUNT, FEATURE_NAMES, HELD_ATTACK_COST, HISTORY_LENGTH, SCHEMA_VERSION, SCHEMAS, collect, demonstration_controls, read_session, recover_exit_path, apply_exit_path_evidence, main as export_dataset
from bot_audit import inspect as inspect_archive
from train_behavior_cloning import fighting_frames, train
from export_bot_policy import export_policy, write_parity_fixture
from bot_action_history import sequential_inputs
from compact_markings import wall_present, mask_walls
from bot_policy_continuation import gym_observations, playback_checkpoint, publication_lock, save_gym_observations


def evaluate(model, observations, actions):
    import numpy as np
    import torch
    model.policy.set_training_mode(False)
    with torch.no_grad():
        distribution = model.policy.get_distribution(torch.as_tensor(observations))
        prediction = distribution.get_actions(deterministic=True).cpu().numpy()
        nll = float(-distribution.log_prob(torch.as_tensor(actions)).mean())
    names = ['movement', 'aim', 'jump', 'attack']
    heads = []
    for index, (name, size) in enumerate(zip(names, [3, 3, 2, 2])):
        matrix = np.zeros((size, size), dtype=int)
        np.add.at(matrix, (actions[:, index], prediction[:, index]), 1)
        support = matrix.sum(axis=1)
        recall = np.divide(matrix.diagonal(), support, out=np.zeros(size), where=support > 0)
        heads.append({'name': name, 'accuracy': float((prediction[:, index] == actions[:, index]).mean()),
                      'balancedAccuracy': float(recall[support > 0].mean()),
                      'support': support.tolist(), 'predictedCounts': matrix.sum(axis=0).tolist(),
                      'confusionMatrix': matrix.tolist()})
    return {'negativeLogLikelihood': nll, 'exactActionAccuracy': float((prediction == actions).all(axis=1).mean()),
            'heads': heads}


def promotion_check(candidate, baseline):
    if baseline is None:
        return False, 'No verified baseline was available for comparison.'
    improved = candidate['negativeLogLikelihood'] < baseline['negativeLogLikelihood'] * 0.99
    agreement = candidate['exactActionAccuracy'] >= baseline['exactActionAccuracy']
    heads = all(c['balancedAccuracy'] + 0.02 >= b['balancedAccuracy']
                for c, b in zip(candidate['heads'], baseline['heads']))
    if improved and agreement and heads:
        return True, 'Better held-out imitation loss and action agreement, without a material per-control regression.'
    return False, 'The candidate did not pass the held-out improvement checks.'


def used_sessions(manifest):
    known = set(manifest.get('ancestorSessions', [])) | set(manifest.get('trainingSessions', [])) | set(manifest.get('validationSessions', []))
    known.update(x['id'] for x in manifest.get('sourceReport', {}).get('sessions', []))
    if manifest.get('validationStrategy', {}).get('sourceSession'):
        known.add(manifest['validationStrategy']['sourceSession'])
    return {x.upper() for x in known}


def recording_domain(manifest, observations):
    authored = [o.get('terrainSource') == 'human-authored' for o in observations]
    inferred = 'hacker' if authored and all(authored) else 'gameplay'
    if any(authored) and not all(authored):
        raise ValueError('A recording mixes Hacker and Gameplay terrain')
    declared = manifest.get('botDomain')
    if declared is not None and declared != inferred:
        raise ValueError('Recording domain disagrees with its terrain evidence')
    return inferred


def split_blockade_clips(held, eligible, contact_counts):
    """Retain demonstrated door attacks for learning and, when possible, an independent encounter for validation."""
    held = list(held)
    attack_clips = set(contact_counts)
    if attack_clips and attack_clips.issubset(held):
        keep = max(attack_clips, key=lambda s: (contact_counts[s], s))
        replacement = next((s for s in eligible if s not in held and s not in attack_clips), None)
        if replacement is not None:
            held[held.index(keep)] = replacement
        elif len(held) > 1:
            held.remove(keep)
    if len(attack_clips) > 1 and attack_clips.isdisjoint(held):
        available = attack_clips.intersection(eligible)
        if available:
            held.append(min(available, key=lambda s: (contact_counts[s], s)))
    return held


def snapshot_exit_path_evidence(source, sidecar, destination, replay_root=None):
    """Validate source identities and archive provenance before snapshotting exact evidence."""
    raw = sidecar.read_bytes()
    evidence = json.loads(raw)
    source_manifest, observations, _ = read_session(source)
    apply_exit_path_evidence(evidence, source, source_manifest, observations)
    if replay_root is None:
        audit = inspect_archive(source)
        replay = Path(audit['replayPath']) if audit['replayPath'] else None
        archive = Path(audit['sourceArchive']) / 'manifest.json' if audit['sourceArchive'] else None
    else:
        replay_id = str(uuid.UUID(source_manifest['replayID'])).lower()
        candidates = list(Path(replay_root).glob('path-*-' + replay_id + '.json'))
        replay = candidates[0] if len(candidates) == 1 else None
        archive = Path(replay_root) / ('frames-' + replay_id) / 'manifest.json'
    if replay is None or archive is None:
        raise ValueError('Exit path provenance is missing its archived replay or frame manifest: ' + source.name)
    for key, file in (("replaySHA256", replay), ("archiveManifestSHA256", archive)):
        if evidence.get(key) != hashlib.sha256(file.read_bytes()).hexdigest():
            raise ValueError('Exit path provenance mismatch: ' + key + ' for ' + source.name)
    for key in ("labelsSHA256", "connectionsSHA256"):
        value = evidence.get(key)
        if not isinstance(value, str) or len(value) != 64 or any(c not in '0123456789abcdef' for c in value):
            raise ValueError('Invalid exit path provenance: ' + key + ' for ' + source.name)
    (destination / sidecar.name).write_bytes(raw)


def run_job(root, selection, run_id, baseline=None, epochs=160, domain=None, wall_extractor=None, recovery_session=None,
            upgrade_observations=False, follow_playback=False, exit_path=None, reuse_validation=False, native_verifier=None):
    models = Path(root) / 'models'
    if domain is not None:
        models /= domain
    with publication_lock(models):
        if follow_playback:
            baseline = playback_checkpoint(models, domain, Path(baseline) if baseline else None,
                                           native_verifier=native_verifier)
        return _run_job(root, selection, run_id, baseline, epochs, domain, wall_extractor, recovery_session,
                        upgrade_observations, exit_path, reuse_validation, native_verifier)


def _run_job(root, selection, run_id, baseline, epochs, domain, wall_extractor, recovery_session,
             upgrade_observations, exit_path=None, reuse_validation=False, native_verifier=None):
    import numpy as np
    from stable_baselines3 import PPO
    root = Path(root)
    identifier = str(uuid.UUID(run_id)).lower()
    run = root / 'training-runs' / identifier
    # Swift creates selection.json before launching; all training artifacts must be new.
    snapshots = run / 'demonstrations'
    snapshots.mkdir(parents=True, exist_ok=False)
    selected = json.loads(Path(selection).read_text())
    if not isinstance(selected, list) or len(selected) != len(set(selected)) or len(selected) < 2:
        raise ValueError('Select at least two different completed recordings')
    sources = []
    paired_root = run / 'paired-evidence'
    paired_root.mkdir()
    recovered = run / "recovered-walls"
    recovered.mkdir()
    blockades = run / "recovered-blockades"
    blockades.mkdir()
    exit_evidence = run / "exit-path-evidence"
    exit_evidence.mkdir()
    path_extractor = wall_extractor or native_verifier
    if exit_path:
        if not path_extractor:
            raise ValueError("Exit path reconstruction requires a native route extractor")
        replay_root, labels, connections, objective = exit_path
        input_snapshot = run / "exit-path-inputs"
        input_snapshot.mkdir()
        labels_copy, connections_copy = input_snapshot / "labels.json", input_snapshot / "connections.json"
        shutil.copyfile(labels, labels_copy)
        shutil.copyfile(connections, connections_copy)
        upgrade_observations = True
    for session_id in selected:
        session_id = str(uuid.UUID(session_id)).lower()
        source = root / 'demonstrations' / ('demo-' + session_id)
        if domain is not None:
            source_manifest, observations, _ = read_session(source)
            if recording_domain(source_manifest, observations) != domain:
                raise ValueError('Recording belongs to the other bot model: ' + session_id)
        _, report = collect(source, compact=True)
        if report['skipped'] or not report['sessions'] or report['sessions'][0]['status'] != 'completed':
            raise ValueError('Recording is incomplete or invalid: ' + session_id)
        before = report['sessions'][0]['sha256']
        shutil.copytree(source, snapshots / source.name)
        _, copied = collect(snapshots / source.name, compact=True)
        if copied['skipped'] or copied['sessions'][0]['sha256'] != before:
            raise ValueError('Recording changed while snapshotting: ' + session_id)
        snapshot = snapshots / source.name
        manifest = json.loads((snapshot / "manifest.json").read_text())
        blockade_extractor = native_verifier or wall_extractor
        if blockade_extractor and any(
            o.get("driveDirection") is None or (
                native_verifier and o.get("blockadeVersion") is None and o.get("blockadePose") is None
                and o.get("terrainSource") == "detected")
            for o in read_session(snapshot)[1]
        ):
            result = subprocess.run([blockade_extractor, "--recover-bot-blockades", str(snapshot)],
                                    stdout=subprocess.PIPE, check=True)
            evidence = json.loads(result.stdout)
            evidence["eventsSHA256"] = before["events.jsonl"]
            (blockades / (source.name + ".json")).write_text(json.dumps(evidence))
        if wall_extractor and manifest["schemaVersion"] == 1 and manifest.get("botDomain", domain) != "hacker":
            archive = inspect_archive(source)["sourceArchive"]
            if archive:
                result = subprocess.run([wall_extractor, "--recover-bot-walls", str(snapshot), archive],
                                        stdout=subprocess.PIPE, check=True)
                evidence = {"id": manifest["id"], "eventsSHA256": before["events.jsonl"],
                            "rows": json.loads(result.stdout)}
                (recovered / (source.name + ".json")).write_text(json.dumps(evidence))
        if exit_path:
            sidecar = root / "exit-paths-v1" / (source.name + ".json")
            recover_exit_path(path_extractor, snapshot, replay_root, labels_copy, connections_copy, objective, sidecar)
        sidecar = root / "exit-paths-v1" / (source.name + ".json")
        if sidecar.exists():
            snapshot_exit_path_evidence(source, sidecar, exit_evidence, replay_root if exit_path else None)
        provenance = {'id': session_id, 'sha256': before}
        if manifest.get('pairedClipID'):
            partner_name = 'demo-' + str(uuid.UUID(manifest['pairedClipID']))
            partner = root / 'demonstrations' / partner_name
            if (partner / 'manifest.json').is_file():
                destination = paired_root / partner_name
                destination.mkdir(exist_ok=True)
                hashes = {}
                for filename in ('manifest.json', 'events.jsonl'):
                    raw = (partner / filename).read_bytes()
                    (destination / filename).write_bytes(raw)
                    hashes[filename] = hashlib.sha256(raw).hexdigest()
                provenance['pairedMeasurementSHA256'] = hashes
        sources.append(provenance)
    dataset = run / 'dataset.npz'
    export_args = ['export', str(snapshots), '--output', str(dataset), '--wall-evidence', str(recovered),
                   '--blockade-evidence', str(blockades), '--paired-root', str(paired_root)]
    export_args.extend(["--exit-path-evidence", str(exit_evidence)])
    if path_extractor:
        export_args.extend(['--path-extractor', str(path_extractor)])
    export_dataset(export_args)
    candidate_directory = root / 'models'
    if domain is not None:
        candidate_directory /= domain
    candidate_directory /= 'bot-' + identifier
    prior = json.loads((Path(baseline) / 'manifest.json').read_text()) if baseline else {}
    known = used_sessions(prior)
    explicit_recovery = recovery_session is not None
    recovery_session = str(uuid.UUID(recovery_session)).upper() if recovery_session else None
    fresh = np.asarray(sorted(x.upper() for x in selected if x.upper() not in known and x.upper() != recovery_session))
    prior_validation = []
    rescore_holds = baseline and not explicit_recovery and len(fresh) < 2 and prior.get('heldAttackCost') != HELD_ATTACK_COST
    if (explicit_recovery or rescore_holds) and baseline and len(fresh) < 2:
        selected_ids = {x.upper() for x in selected}
        prior_validation = [x for x in prior.get('validationSessions', [])
                            if x.upper() in selected_ids and x.upper() != recovery_session]
        if rescore_holds:
            prior_validation = [x for x in prior_validation if x not in prior.get('trainingSessions', [])]
    if upgrade_observations and len(fresh) < 2 and not prior_validation and prior.get('schemaVersion', SCHEMA_VERSION) < SCHEMA_VERSION:
        # Rebuild the observation contract with a whole-clip holdout. The comparison below
        # independently checks whether the active model ever saw any of these recordings.
        available = sorted({s.upper() for s in selected} - {recovery_session})
        retained = [s for s in prior.get('validationSessions', []) if s in available]
        prior_validation = (retained or available)[:max(1, len(available) // 5)]
    if reuse_validation:
        prior_validation = [s.upper() for s in prior.get('validationSessions', [])]
        held_ids = set(prior_validation)
        selected_ids = {s.upper() for s in selected}
        trained_ids = {s.upper() for s in prior.get('trainingSessions', [])}
        if not held_ids or not held_ids < selected_ids or held_ids & trained_ids or recovery_session in held_ids:
            raise ValueError("A repeat run requires the parent's full held-out split and separate training clips")
    if len(fresh) < 2 and not prior_validation:
        raise ValueError('Record at least two new clips before updating, so comparison uses unseen sessions')
    np.random.default_rng(42).shuffle(fresh)
    with np.load(dataset, allow_pickle=False) as data:
        sessions = data['session_ids']
        actions, _ = demonstration_controls(data)
        fights = fighting_frames(data['observations'], actions, sessions, data['segments'])
        combat_sessions = set(sessions[fights])
        fighters = [s for s in fresh if s in combat_sessions]
        # Reserve one new fight to learn from. A second independent fight can measure imitation.
        available = [s for s in fresh if not fighters or s != fighters[0]]
        held = fighters[1:2]
        direction = {}
        for session in available:
            counts = np.bincount(actions[sessions == session, 0], minlength=3)
            direction[session] = 1 + int(counts[2] > counts[1])
        if len(fresh) >= 4:
            for side in set(direction.values()):
                if not any(direction[s] == side for s in held):
                    held.append(next(s for s in available if direction[s] == side))
        # The comparison needs unseen clips. Allocate holdouts from the new batch
        # so a growing library does not consume nearly every new demonstration.
        target = min(len(fresh) - 1, max(len(held), len(fresh) // 5, 1))
        held += [s for s in available if s not in held][:max(0, target - len(held))]
        contacts = data['observations'][:, -FEATURE_COUNT + 15] != 0
        attack_clips = set(sessions[contacts & (actions[:, 3] == 1)])
        counts = {s: int((contacts & (sessions == s)).sum()) for s in attack_clips}
        # An observation upgrade can reuse existing clips. Ordinary comparisons
        # must keep every new holdout outside the baseline's training history.
        eligible = sorted(set(sessions) - {recovery_session}) if prior_validation else fresh.tolist()
        validation_sessions = (prior_validation if prior_validation and (rescore_holds or reuse_validation)
                               else split_blockade_clips(prior_validation or held, eligible, counts))
    if baseline and domain and prior.get('botDomain', domain) != domain:
        raise ValueError('Training parent belongs to a different bot domain')
    gym_states = gym_observations(root, domain, Path(baseline), prior) if baseline and domain else None
    gym_routes = gym_observations(root, domain, Path(baseline), prior, routes=True) if baseline and domain else None
    manifest = train(dataset, candidate_directory, epochs=epochs, validation_session_ids=validation_sessions,
                     recovery_session=recovery_session, parent=baseline, gym_states=gym_states, gym_routes=gym_routes,
                     name='Hollow Knight Bot · ' + datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M UTC'))
    if baseline:
        manifest['ancestorSessions'] = sorted(known)
        save_gym_observations(candidate_directory, manifest, gym_states)
        save_gym_observations(candidate_directory, manifest, gym_routes, routes=True)
    if domain is not None:
        manifest['botDomain'] = domain
    if baseline or domain:
        (candidate_directory / 'manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    _, candidate = export_policy(candidate_directory)
    with np.load(dataset, allow_pickle=False) as data:
        held_out = np.isin(data['session_ids'], manifest['validationSessions'])
        all_x = data['observations']
        train_frame = all_x[~held_out, -FEATURE_COUNT:]
        training_with_wall = int(wall_present(train_frame, FEATURE_NAMES).sum())
        controls, _ = demonstration_controls(data)
        x, y = all_x[held_out], controls[held_out]
        sessions, segments = data['session_ids'][held_out], data['segments'][held_out]
    candidate_metrics = evaluate(candidate, x, y)
    candidate_sequential = evaluate(candidate, sequential_inputs(candidate, x, sessions, segments), y)
    baseline_metrics, baseline_hash, baseline_error = None, None, None
    baseline_sequential = None
    if baseline:
        try:
            baseline = Path(baseline)
            baseline_manifest = json.loads((baseline / 'manifest.json').read_text())
            baseline_hash = hashlib.sha256((baseline / 'model.zip').read_bytes()).hexdigest()
            if baseline_hash != baseline_manifest['modelSHA256']:
                raise ValueError('Active checkpoint hash mismatch')
            # An old model must never be scored on a session it trained on.
            known = used_sessions(baseline_manifest)
            if known.intersection(manifest['validationSessions']):
                raise ValueError('Comparison clips were already used by the active model; record new clips for a fair comparison')
            baseline_model = PPO.load(baseline / 'model.zip', device='cpu')
            names = SCHEMAS.get(baseline_manifest.get('schemaVersion'))
            if (names is None or names != baseline_manifest.get('featureNames')
                    or baseline_model.observation_space.shape != (len(names) * HISTORY_LENGTH,)):
                raise ValueError('Incompatible baseline observation contract')
            # A compact schema is not a prefix of historical raster schemas.
            # Re-encode the identical recordings with the baseline's own contract.
            baseline_rows, baseline_report = collect(snapshots, compact=True, wall_evidence=recovered,
                blockade_evidence=blockades, paired_root=paired_root, path_extractor=path_extractor,
                exit_path_evidence=exit_evidence, policy_version=baseline_manifest['schemaVersion'])
            if baseline_report['skipped']: raise ValueError('Baseline recordings could not be re-encoded')
            indexed = {(r['session'], r['timestamp']): r['observation'] for r in baseline_rows}
            with np.load(dataset, allow_pickle=False) as baseline_data:
                held_times = baseline_data['timestamps'][held_out]
            baseline_x = np.asarray([indexed[(str(session), float(time))] for session, time in zip(sessions, held_times)], dtype=np.float32)
            if baseline_manifest['schemaVersion'] < 4:
                baseline_x.reshape(-1, HISTORY_LENGTH, len(names))[:, :, 13:16] = 0
            baseline_metrics = evaluate(baseline_model, baseline_x, y)
            baseline_sequential = evaluate(baseline_model, sequential_inputs(baseline_model, baseline_x, sessions, segments), y)
        except (OSError, ValueError, KeyError) as error:
            baseline_error = str(error)
    eligible, reason = promotion_check(candidate_metrics, baseline_metrics)
    if eligible and (candidate_sequential['exactActionAccuracy'] < baseline_sequential['exactActionAccuracy']
                     or candidate_sequential['heads'][2]['balancedAccuracy'] + 0.02 < baseline_sequential['heads'][2]['balancedAccuracy']):
        eligible, reason = False, 'Own-action feedback regressed on recorded states.'
    # Check the actual exported weights on the complete held-out batch.
    payload = json.loads((candidate_directory / 'runtime-policy.json').read_text())
    native = x[:, payload['inputColumns']] if 'inputColumns' in payload else x
    for layer in payload['layers']:
        weights = np.asarray(layer['weights'], dtype=np.float32).reshape(layer['outputSize'], layer['inputSize'])
        native = native @ weights.T + np.asarray(layer['biases'], dtype=np.float32)
        if layer['activation'] == 'tanh':
            native = np.tanh(native)
    parts = np.split(native, [3, 6, 8], axis=1)
    native_actions = np.stack([part.argmax(axis=1) for part in parts], axis=1)
    predicted, _ = candidate.predict(x, deterministic=True)
    if not np.array_equal(native_actions, predicted):
        raise ValueError('Native export disagrees with Stable-Baselines3 on held-out actions')
    if native_verifier:
        fixture = run / "native-verification.json"
        write_parity_fixture(candidate, fixture, observations=x)
        subprocess.run([native_verifier, "--hacker-gym-observations", "verify", candidate_directory, fixture],
                       check=True, timeout=45)
    # Mask only the new inputs across all four frames on the identical held-out decisions.
    frames = x.reshape(-1, HISTORY_LENGTH, FEATURE_COUNT)
    masked = frames.copy()
    mask_walls(masked, FEATURE_NAMES)
    masked_actions, _ = candidate.predict(masked.reshape(x.shape), deterministic=True)
    wall_audit = {'validationExamples': len(x), 'trainingWithWall': training_with_wall,
                  'analyzed': int((frames[:, -1, FEATURE_NAMES.index('marking_wall_known')] == 1).sum()),
                  'withWall': int(wall_present(frames[:, -1], FEATURE_NAMES).sum()),
                  'actionsChangedWhenMasked': int((predicted != masked_actions).any(axis=1).sum())}
    best_epoch = min(manifest['metrics'], key=lambda x: x['selection_loss'])
    final_validation = manifest['finalValidation']
    result = {'schemaVersion': 1, 'id': identifier, 'createdAt': datetime.now(timezone.utc).isoformat(),
              'botDomain': domain,
              'modelDirectory': candidate_directory.name, 'name': manifest['name'], 'reusedParentValidation': reuse_validation,
              'trainingExamples': manifest['trainingExamples'], 'validationExamples': manifest['validationExamples'],
              'trainingSessions': manifest['trainingSessions'], 'validationSessions': manifest['validationSessions'],
              'sources': sources, 'candidate': candidate_metrics, 'baseline': baseline_metrics,
              'candidateSequential': candidate_sequential, 'baselineSequential': baseline_sequential,
              'baselineSHA256': baseline_hash, 'baselineError': baseline_error,
              'eligibleForActivation': eligible, 'reason': reason, 'nativeParity': True,
              'liveGameplayEvaluated': False, 'actionSource': 'general-policy', 'wallEvidence': wall_audit,
              'blockadeTraining': {**manifest['blockadeTraining'],
                                   'validationClips': int(len(set(sessions[frames[:, -1, 15] != 0]))),
                                   'attackRecall': final_validation.get('blockade_attack_recall'),
                                   'releaseRecall': final_validation.get('blockade_release_recall')},
              'enemyOutcomes': manifest['enemyOutcomes'],
              'combatTraining': {**manifest['combatTraining'], 'attackRecall': final_validation.get('combat_attack_recall'),
                                 'releaseRecall': final_validation.get('combat_release_recall')},
              'bestEpoch': best_epoch['epoch']}
    for key in ('parentPolicy', 'lastGymPolicy', 'gymRetention'):
        if key in manifest:
            result[key] = manifest[key]
    temporary = candidate_directory / '.training-result.tmp'
    temporary.write_text(json.dumps(result, indent=2) + '\n')
    temporary.replace(candidate_directory / 'training-result.json')
    print(json.dumps({'resultPath': str(candidate_directory / 'training-result.json'),
                      'eligibleForActivation': eligible}), flush=True)
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', required=True, type=Path)
    parser.add_argument('--selection', required=True, type=Path)
    parser.add_argument('--run-id', required=True)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--follow-playback', action='store_true', help='Use the selected playback policy when this training slot opens.')
    parser.add_argument('--epochs', type=int, default=160)
    parser.add_argument('--domain', choices=['hacker', 'gameplay'])
    parser.add_argument('--wall-extractor', type=Path)
    parser.add_argument('--native-verifier', type=Path, help='Verify with the installed Windows policy host.')
    parser.add_argument('--recovery-session', help='A training clip to emphasize for a known occlusion failure')
    parser.add_argument('--upgrade-observations', action='store_true')
    parser.add_argument('--reuse-validation', action='store_true', help="Continue training with the parent's exact validation split.")
    parser.add_argument('--exit-path-replay-root', type=Path)
    parser.add_argument('--exit-path-labels', type=Path)
    parser.add_argument('--exit-path-connections', type=Path)
    parser.add_argument('--exit-path-objective', type=float, nargs=2, metavar=('X', 'Y'))
    args = parser.parse_args()
    if args.native_verifier and not args.domain:
        parser.error('Native verification requires a bot domain')
    exit_args = (args.exit_path_replay_root, args.exit_path_labels, args.exit_path_connections, args.exit_path_objective)
    if any(x is not None for x in exit_args) and (not all(x is not None for x in exit_args)
                                                 or not (args.wall_extractor or args.native_verifier)):
        parser.error('Exit reconstruction requires a native route extractor and all four --exit-path inputs')
    exit_path = exit_args if all(x is not None for x in exit_args) else None
    run_job(args.root, args.selection, args.run_id, args.baseline, args.epochs, args.domain, args.wall_extractor,
            args.recovery_session, args.upgrade_observations, args.follow_playback, exit_path, args.reuse_validation,
            args.native_verifier)
