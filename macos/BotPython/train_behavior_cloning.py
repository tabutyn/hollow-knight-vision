#!/usr/bin/env python3
"""Train from demonstrations, continuing the selected PPO policy when available."""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import platform

from hkv_bot_dataset import (ACTION_SIZES, FEATURE_COUNT, FEATURE_NAMES, HISTORY_LENGTH, SCHEMA_VERSION,
                             ENEMY_START, HELD_ATTACK_COST, demonstration_controls)
from bot_action_history import sequential_inputs
from bot_policy_continuation import load_parent, retain_gym_actions


def demonstration_loss(distribution, targets, continued, previous_attack):
    """A verified hold gives only the attack head negative evidence. Using
    log probability still gives a gradient when the actor almost always holds."""
    import torch
    move, aim, jump, attack = distribution.distribution
    # demonstration_controls assigns release to confirmed continuation rows.
    attack_weight = torch.where(continued, HELD_ATTACK_COST * previous_attack, 1.0)
    attack_loss = -attack_weight * attack.log_prob(targets[:, 3])
    return attack_loss - move.log_prob(targets[:, 0]) - aim.log_prob(targets[:, 1]) - jump.log_prob(targets[:, 2])


def fighting_frames(observations, actions, sessions, segments):
    """Visible encounters containing an expert swing, including their approach and releases."""
    import numpy as np
    frames = observations[:, -FEATURE_COUNT:]
    enemies = frames[:, ENEMY_START:ENEMY_START + 48].reshape(-1, 8, 6)
    visible = (frames[:, 4] > 0) & ((enemies[:, :, 5] == 1) & (enemies[:, :, 4] >= 0.5)
                                    & (enemies[:, :, 2] > 0) & (enemies[:, :, 3] > 0)).any(axis=1)
    fights = np.zeros(len(frames), dtype=bool)
    start = 0
    while start < len(frames):
        if not visible[start]:
            start += 1
            continue
        end = start + 1
        while (end < len(frames) and visible[end] and sessions[end] == sessions[start]
               and segments[end] == segments[start]):
            end += 1
        if actions[start:end, 3].any():
            fights[start:end] = True
        start = end
    return fights


def demonstration_starts(sessions, segments, actions):
    """First demonstrated control in the opening continuous segment of each clip."""
    import numpy as np
    starts = []
    for session in np.unique(sessions):
        indices = np.flatnonzero(sessions == session)
        opening = indices[segments[indices] == segments[indices[0]]]
        presses = opening[actions[opening].any(axis=1)]
        if len(presses):
            starts.append(presses[0])
    return np.asarray(starts, dtype=np.int64)


def recording_lead_in(observations, sessions, segments, timestamps, starts):
    """Identify short, stationary K setup pauses before the first demonstrated input."""
    import numpy as np
    setup = np.zeros(len(sessions), dtype=bool)
    for start in starts:
        opening = np.flatnonzero((sessions == sessions[start]) & (segments == segments[start]))
        opening = opening[opening <= start]
        if len(opening) < 2 or timestamps[start] - timestamps[opening[0]] > 1.5:
            continue
        frames = observations[opening, -FEATURE_COUNT:]
        # Keep tactical waits, lost tracking, and a Knight already in motion.
        enemies = frames[:, ENEMY_START + 5:ENEMY_START + 48:6].any()
        if (not enemies and (frames[:, 4] > 0).all() and not frames[:, 15].any()
                and np.ptp(frames[:, :2], axis=0).max() < 0.025):
            setup[opening[:-1]] = True
    return setup


def cold_start_inputs(observations):
    """Present a demonstrated decision with the same empty history B starts with."""
    batch = observations.clone().reshape(-1, HISTORY_LENGTH, FEATURE_COUNT)
    batch[:, :-1] = 0
    batch[:, -1, 8] = 1
    batch[:, -1, 9:13] = 0
    return batch.reshape(-1, HISTORY_LENGTH * FEATURE_COUNT)


def fit_start_weights(model, x, y, training, validation, max_change=None):
    """Fit the newest frame's reset-flag weights on human starts. Continuous inputs have a zero in this column."""
    import torch

    if not len(training) or not len(validation):
        return None
    layer = model.policy.mlp_extractor.policy_net[0]
    reset_column = x.shape[1] - FEATURE_COUNT + 8
    delta = torch.zeros_like(layer.bias, requires_grad=True)
    optimizer = torch.optim.Adam([delta], lr=0.015)
    train_x, valid_x = cold_start_inputs(x[training]), cold_start_inputs(x[validation])
    # All these frames have reset=1. Adding delta to the first-layer activation
    # is exactly a change to its reset column, with all other weights frozen.
    model.policy.requires_grad_(False)
    hook = layer.register_forward_hook(lambda _layer, _inputs, output: output + delta)
    best = delta.detach().clone()
    try:
        def validation_loss():
            return float(-model.policy.get_distribution(valid_x).log_prob(y[validation]).mean())
        with torch.no_grad():
            initial_loss = best_loss = validation_loss()
        for _ in range(48):
            loss = -model.policy.get_distribution(train_x).log_prob(y[training]).mean()
            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
            with torch.no_grad():
                if max_change is not None:
                    delta.clamp_(-max_change, max_change)
                score = validation_loss()
                if score < best_loss:
                    best_loss, best = score, delta.detach().clone()
    finally:
        hook.remove()
        model.policy.requires_grad_(True)
    with torch.no_grad():
        layer.weight[:, reset_column].add_(best)
    return {"beforeNLL": initial_loss, "afterNLL": best_loss}


def train(dataset_path, output, *, epochs=20, batch_size=128, seed=42, name=None, validation_session_ids=None,
          action_history_dropout=0.5, recovery_session=None, parent=None, gym_states=None, gym_routes=None):
    import copy
    import gymnasium as gym
    import numpy as np
    import stable_baselines3
    from stable_baselines3 import PPO
    import torch

    if epochs < 1 or batch_size < 1:
        raise ValueError("epochs and batch size must be positive")
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError("Choose a new output directory; existing model artifacts are preserved")
    dataset_path = Path(dataset_path)
    report = json.loads(dataset_path.with_suffix(".json").read_text())
    dataset_hash = hashlib.sha256(dataset_path.read_bytes()).hexdigest()
    if (report.get("schemaVersion") != SCHEMA_VERSION or report.get("skipped")
            or report.get("featureCount") != FEATURE_COUNT or report.get("historyLength") != HISTORY_LENGTH
            or report.get("actionSizes") != ACTION_SIZES or report.get("datasetSHA256") != dataset_hash):
        raise ValueError("Incompatible dataset report")
    with np.load(dataset_path, allow_pickle=False) as data:
        observations = data["observations"].copy()
        actions, continued_attacks = demonstration_controls(data)
        outcome_weights = data["outcome_weights"].copy()
        enemy_outcomes = data["enemy_outcomes"].copy()
        timestamps = data["timestamps"].copy()
        sessions = data["session_ids"].copy()
        segments = data["segments"].copy()
    count = len(observations)
    if (observations.shape != (count, FEATURE_COUNT * HISTORY_LENGTH) or actions.shape != (count, 4)
            or outcome_weights.shape != (count,) or not ((0.5 <= outcome_weights) & (outcome_weights <= 4)).all()
            or sessions.shape != (count,) or count != report["examples"]
            or not np.isfinite(observations).all() or np.abs(observations).max(initial=0) > 4
            or not np.issubdtype(actions.dtype, np.integer)
            or not ((actions >= 0) & (actions < np.asarray(ACTION_SIZES))).all()):
        raise ValueError("Invalid dataset arrays")
    unique = np.unique(sessions)
    if len(unique) < 2:
        raise ValueError("Record at least two separate demonstrations so validation holds out whole sessions")
    rng = np.random.default_rng(seed)
    rng.shuffle(unique)
    validation_sessions = unique[:max(1, len(unique) // 5)] if validation_session_ids is None else np.asarray(validation_session_ids)
    if not len(validation_sessions) or not set(validation_sessions).issubset(set(unique)):
        raise ValueError("Invalid held-out session selection")
    validation_mask = np.isin(sessions, validation_sessions)
    starts = demonstration_starts(sessions, segments, actions)
    training_starts = starts[~validation_mask[starts]]
    validation_starts = starts[validation_mask[starts]]
    setup = recording_lead_in(observations, sessions, segments, timestamps, training_starts)
    train_indices = np.flatnonzero(~validation_mask & ~setup)
    validation_indices = np.flatnonzero(validation_mask)
    if len(train_indices) < 4 or len(validation_indices) < 2:
        raise ValueError("Collect longer demonstrations before training")
    masked = observations[:, -FEATURE_COUNT + 4] == 0
    masked_training_count = int(masked[train_indices].sum())
    fights = fighting_frames(observations, actions, sessions, segments)
    combat_train = train_indices[fights[train_indices]]
    blocked = observations[:, -FEATURE_COUNT + 15] != 0
    blockade_train = train_indices[blocked[train_indices]]
    blockade_held = blocked[validation_indices]
    contact_sessions = np.unique(sessions[blockade_train])
    contact_traces = np.flatnonzero(np.isin(sessions, contact_sessions))
    combat_held = fights[validation_indices]
    pauses, strikes = [combat_train[actions[combat_train, 3] == button] for button in (0, 1)]
    recovery_indices = train_indices[sessions[train_indices] == recovery_session]
    if recovery_session and (not len(recovery_indices) or not masked[recovery_indices].any()):
        raise ValueError("The recovery clip must include masked Knight training rows and be outside validation")

    # The few contact offsets are measured in fractions of a screen beside hundreds
    # of binary terrain inputs. Normalize using the training split only. Bound the
    # gain at a Knight-sized displacement, including domains with zero vertical motion.
    scale = np.ones(FEATURE_COUNT, dtype=np.float32)
    if len(blockade_train):
        contact_features = observations[train_indices, -FEATURE_COUNT + 13:-FEATURE_COUNT + 16]
        rms = np.sqrt(np.mean(contact_features ** 2, axis=0))
        scale[13:16] = 1 / np.maximum(rms, [0.025, 0.025, 0.125])
    # No example in an older library contains the new local coverage grid.
    # Leave those connections dormant until training actually sees a grid.
    training_frames = observations[train_indices].reshape(-1, HISTORY_LENGTH, FEATURE_COUNT)
    grid = FEATURE_NAMES.index("atlas_grid_present")
    road = FEATURE_NAMES.index("road_present")
    if not training_frames[:, :, grid].any():
        scale[grid:road] = 0
    if not training_frames[:, :, road].any():
        scale[road:FEATURE_NAMES.index("road_frontier") + 1] = 0
    if not training_frames[:, :, FEATURE_NAMES.index("lifeblood_cocoon_present")].any():
        scale[FEATURE_NAMES.index("lifeblood_cocoon_dx"):FEATURE_NAMES.index("lifeblood_cocoon_present") + 1] = 0
    if not training_frames[:, :, FEATURE_NAMES.index("shared_path_present")].any():
        scale[FEATURE_NAMES.index("shared_path_present"):FEATURE_NAMES.index("shared_path_dy") + 1] = 0
    if parent is not None:
        # Exported Gym weights already consume raw observations. Preserve every column,
        # including atlas inputs that may be absent from the new human recordings.
        scale.fill(1)
    input_scale = np.tile(scale, HISTORY_LENGTH)
    observations *= input_scale

    class ObservationSpec(gym.Env):
        observation_space = gym.spaces.Box(-4, 4, shape=(FEATURE_COUNT * HISTORY_LENGTH,), dtype=np.float32)
        action_space = gym.spaces.MultiDiscrete(ACTION_SIZES)

        def reset(self, *, seed=None, options=None):
            raise RuntimeError("This observation specification is for offline cloning, not a game simulator")

        def step(self, action):
            raise RuntimeError("Live game stepping has not been attached")

    torch.set_num_threads(2)
    torch.manual_seed(seed)
    parent_manifest = {}
    retention = {}
    if parent is not None:
        model, parent_manifest = load_parent(parent, ObservationSpec())
        reference = copy.deepcopy(model.policy).requires_grad_(False).eval()
        # Use a separate optimizer for imitation. Preserve PPO's critic and optimizer archive.
        optimizer = torch.optim.Adam([*model.policy.mlp_extractor.policy_net.parameters(),
                                      *model.policy.action_net.parameters()], lr=3e-5)
    else:
        model = PPO("MlpPolicy", ObservationSpec(), seed=seed, device="cpu", learning_rate=3e-4,
                    n_steps=128, batch_size=64, policy_kwargs={"net_arch": {"pi": [128, 128], "vf": [128, 128]}})
        optimizer = model.policy.optimizer
    gym_inputs = torch.as_tensor(gym_states) if gym_states is not None else torch.empty((0, observations.shape[1]))
    routes = torch.as_tensor(gym_routes) if gym_routes is not None else gym_inputs[:0]
    anchors = torch.cat((gym_inputs, routes))
    x = torch.as_tensor(observations, dtype=torch.float32)
    y = torch.as_tensor(actions, dtype=torch.long)
    # Supplement the original clips with actual human starts. B can be pressed
    # on a ready-to-move frame with no preceding expert controls or frames.
    # Soften frequent opening combinations without treating rare ones as equally common.
    _, start_groups, counts = np.unique(actions[training_starts], axis=0, return_inverse=True, return_counts=True)
    start_weights = 1 / np.sqrt(counts[start_groups])
    cold = cold_start_inputs(x[training_starts])
    standard_count = len(x)
    x = torch.cat((x, cold))
    y = torch.cat((y, y[training_starts]))
    continued = torch.as_tensor(np.concatenate((continued_attacks, np.zeros(len(training_starts), dtype=bool))))
    outcome_weights = np.concatenate((outcome_weights, outcome_weights[training_starts]))
    blocked = np.concatenate((blocked, blocked[training_starts]))
    output.mkdir(parents=True, exist_ok=True)
    metrics = []
    best_loss = float("inf")
    for epoch in range(epochs):
        model.policy.set_training_mode(True)
        order = train_indices
        if len(training_starts):
            rehearsal = min(len(training_starts) * 12, max(1, len(train_indices) // 5))
            choices = rng.choice(len(training_starts), size=rehearsal, p=start_weights / start_weights.sum())
            # Retain natural history on half the starts, including settled idle frames.
            starts_batch = np.where(rng.random(rehearsal) < 0.5, standard_count + choices, training_starts[choices])
            order = np.concatenate((order, starts_batch))
        if recovery_session:
            # Retain the general task while rehearsing an explicitly identified correction.
            order = np.concatenate([order, rng.choice(recovery_indices, size=len(order) // 3)])
        # Keep the original traversal mix. Rehearse both attack states from training clips only.
        for examples in (pauses, strikes):
            if len(examples):
                extra = min(len(examples) * 6, max(1, len(train_indices) // 4))
                order = np.concatenate([order, rng.choice(examples, size=extra)])
        # A short press often spans just two windows. Give press and release equal
        # rehearsal at contacts, using the expert's entire action on each sampled row.
        contact_budget = min(len(blockade_train) * 8, len(train_indices) // 6)
        for button in (0, 1):
            examples = blockade_train[actions[blockade_train, 3] == button]
            if len(examples) and contact_budget:
                order = np.concatenate([order, rng.choice(examples, size=contact_budget)])
        order = rng.permutation(order)
        for offset in range(0, len(order), batch_size):
            indices = order[offset:offset + batch_size]
            batch = x[indices].clone().reshape(-1, HISTORY_LENGTH, FEATURE_COUNT)
            # Avoid learning to simply copy an expert's held buttons forever.
            keep = torch.as_tensor(rng.random(len(indices)) >= action_history_dropout, dtype=torch.float32)
            batch[:, :, 9:13] *= keep[:, None, None]
            at_contact = blocked[indices]
            if at_contact.any():
                # The same contact can appear against different scenery and a noisy
                # camera origin. Preserve its side, recent controls and expert label.
                context = batch[at_contact].clone()
                gain = torch.as_tensor(rng.uniform(0, 1, (len(context), 1, 1)), dtype=torch.float32)
                context[:, :, :9] *= gain
                context[:, :, 16:] *= gain
                seen = (context[:, :, 15:16] != 0)
                offset = rng.uniform(-0.015, 0.015, (len(context), 1, 2)).astype(np.float32)
                context[:, :, 13:15] += seen * torch.from_numpy(offset * scale[13:15])
                batch[at_contact] = context
            distribution = model.policy.get_distribution(batch.reshape(-1, FEATURE_COUNT * HISTORY_LENGTH))
            weights = torch.from_numpy(outcome_weights[indices])
            losses = demonstration_loss(distribution, y[indices], continued[indices], batch[:, -1, 12])
            loss = (losses * weights).sum() / weights.sum()
            if parent is not None:
                anchor = x[indices]
                if len(anchors):
                    anchor = torch.cat((anchor, anchors[rng.integers(len(anchors), size=len(indices))]))
                prior = reference.get_distribution(anchor).distribution
                updated = model.policy.get_distribution(anchor).distribution
                loss += 4 * sum(torch.distributions.kl_divergence(a, b).mean() for a, b in zip(prior, updated))
            optimizer.zero_grad()
            loss.backward()
            torch.nn.utils.clip_grad_norm_(model.policy.parameters(), 0.5)
            optimizer.step()
        model.policy.set_training_mode(False)
        with torch.no_grad():
            losses, exact, heads = [], 0, np.zeros(4, dtype=np.int64)
            for offset in range(0, len(validation_indices), batch_size):
                indices = validation_indices[offset:offset + batch_size]
                distribution = model.policy.get_distribution(x[indices])
                losses.extend((-distribution.log_prob(y[indices])).tolist())
                prediction = distribution.get_actions(deterministic=True)
                matches = (prediction == y[indices]).cpu().numpy()
                exact += int(matches.all(axis=1).sum())
                heads += matches.sum(axis=0)
            validation_loss = float(np.mean(losses))
            feedback = sequential_inputs(model, observations[validation_indices], sessions[validation_indices], segments[validation_indices])
            feedback_distribution = model.policy.get_distribution(torch.as_tensor(feedback))
            feedback_losses = -feedback_distribution.log_prob(y[validation_indices])
            feedback_loss = float(feedback_losses.mean())
            predicted = feedback_distribution.get_actions(deterministic=True)
            feedback_exact = float((predicted == y[validation_indices]).all(dim=1).float().mean())
        metric = {"epoch": epoch + 1, "validation_nll": validation_loss,
                  "validation_feedback_nll": feedback_loss, "validation_feedback_exact_action_accuracy": feedback_exact,
                  "validation_exact_action_accuracy": exact / len(validation_indices),
                  "validation_head_accuracy": (heads / len(validation_indices)).tolist()}
        selection_loss = (validation_loss + feedback_loss) / 2
        if combat_held.any():
            expected = actions[validation_indices, 3]
            matches = predicted[:, 3].cpu().numpy() == expected
            combat_losses = []
            for button, label in enumerate(("release", "attack")):
                subset = combat_held & (expected == button)
                if subset.any():
                    metric[f"combat_{label}_recall"] = float(matches[subset].mean())
                    combat_losses.append(float(feedback_losses[subset].mean()))
            selection_loss = (2 * selection_loss + float(np.mean(combat_losses))) / 3
        if blockade_held.any():
            expected = actions[validation_indices, 3]
            contact_losses = []
            button_losses = -feedback_distribution.distribution[3].log_prob(y[validation_indices, 3])
            for button, label in enumerate(("release", "attack")):
                subset = blockade_held & (expected == button)
                if subset.any():
                    metric[f"blockade_{label}_recall"] = float((predicted[subset, 3].cpu().numpy() == button).mean())
                    contact_losses.append(float(button_losses[subset].mean()))
            if len(contact_losses) == 2:
                selection_loss = selection_loss / len(ACTION_SIZES) + float(np.mean(contact_losses))
        if len(validation_starts):
            with torch.no_grad():
                start_policy = model.policy.get_distribution(cold_start_inputs(x[validation_starts]))
                start_loss = float(-start_policy.log_prob(y[validation_starts]).mean())
                start_buttons = start_policy.get_actions(deterministic=True)
                expected_starts = y[validation_starts]
                horizontal = expected_starts[:, 0]
                movement_matches = (start_buttons[:, 0] == horizontal).float()
                metric["validation_start_horizontal_accuracy"] = float(movement_matches.mean())
                metric["validation_start_horizontal_balanced_accuracy"] = float(torch.stack(
                    [movement_matches[horizontal == side].mean() for side in horizontal.unique()]).mean())
                # A mostly right-going library should still select a model that
                # can begin by going left, jumping, attacking, or staying put.
                match = (start_buttons == expected_starts).all(dim=1)
                groups = expected_starts.unique(dim=0)
                start_agreement = torch.stack([match[(expected_starts == group).all(dim=1)].float().mean()
                                               for group in groups]).mean()
                metric["validation_start_balanced_agreement"] = float(start_agreement)
            selection_loss = (3 * selection_loss + start_loss) / 4
        if len(contact_traces):
            # Check whether a checkpoint can actually reproduce the rare recorded
            # presses with its own controls fed back. Score each contact clip once.
            trace_inputs = sequential_inputs(model, observations[contact_traces], sessions[contact_traces], segments[contact_traces])
            buttons = model.predict(trace_inputs, deterministic=True)[0][:, 3]
            press = (buttons == 1) & (trace_inputs[:, -FEATURE_COUNT + 12] == 0)
            expert = actions[contact_traces, 3]
            expert_press = (expert == 1) & (observations[contact_traces, -FEATURE_COUNT + 12] == 0)
            matched = []
            for session in contact_sessions:
                contact = (sessions[contact_traces] == session) & blocked[contact_traces]
                matched.append((press[contact].any() == expert_press[contact].any())
                               and ((buttons[contact] == 0).any() == (expert[contact] == 0).any()))
            metric["blockade_training_clip_agreement"] = float(np.mean(matched))
            selection_loss = 1 - metric["blockade_training_clip_agreement"] + selection_loss / (1 + selection_loss)
        if len(validation_starts):
            selection_loss += 1 - metric["validation_start_horizontal_balanced_accuracy"]
            selection_loss += 1 - (metric["validation_start_balanced_agreement"] + feedback_exact
                                   + metric["validation_exact_action_accuracy"]) / 3
        if recovery_session:
            focused = sequential_inputs(model, observations[recovery_indices], sessions[recovery_indices], segments[recovery_indices])
            with torch.no_grad():
                distribution = model.policy.get_distribution(torch.as_tensor(focused))
                recovery_loss = float(-distribution.log_prob(y[recovery_indices]).mean())
            metric["recovery_training_feedback_nll"] = recovery_loss
            selection_loss = (selection_loss + recovery_loss) / 2
        metric["selection_loss"] = selection_loss
        metrics.append(metric)
        print(json.dumps(metric), flush=True)
        if selection_loss < best_loss:
            best_loss = selection_loss
            model.save(output / "model.zip")
    best = PPO.load(output / "model.zip", device="cpu")
    start_fit = fit_start_weights(best, x, y, training_starts, validation_starts, max_change=.025 if parent else None)
    if parent is not None:
        retention = retain_gym_actions(best, reference, gym_inputs, routes)
    with torch.no_grad():
        feedback = sequential_inputs(best, observations[validation_indices], sessions[validation_indices],
                                     segments[validation_indices])
        final_distribution = best.policy.get_distribution(torch.as_tensor(feedback))
        final_buttons = final_distribution.get_actions(deterministic=True)
        expected = y[validation_indices]
        final_validation = {"validation_feedback_nll": float(-final_distribution.log_prob(expected).mean()),
                            "validation_feedback_exact_action_accuracy": float((final_buttons == expected).all(dim=1).float().mean())}
        for context, mask in (("combat", combat_held), ("blockade", blockade_held)):
            for button, label in enumerate(("release", "attack")):
                subset = torch.as_tensor(mask) & (expected[:, 3] == button)
                if subset.any():
                    final_validation[f"{context}_{label}_recall"] = float((final_buttons[subset, 3] == button).float().mean())
        best.policy.mlp_extractor.policy_net[0].weight.mul_(torch.from_numpy(input_scale))
    best.save(output / "model.zip")
    model_manifest = {
        "schemaVersion": SCHEMA_VERSION, "stage": "demonstration-fine-tuning" if parent else "behavior-cloning", "actionSource": "general-policy",
        "enemyOutcomes": {"damage": int(enemy_outcomes[train_indices, 0].sum()),
                          "kills": int(enemy_outcomes[train_indices, 1].sum())},
        "outcomeWeightedTrainingExamples": int((outcome_weights[train_indices] != 1).sum()), "criticTrained": bool(parent_manifest.get("schemaVersion") == SCHEMA_VERSION and (parent_manifest.get("criticTrained") or parent_manifest.get("gymEvidence", {}).get("method") == "ppo")),
        "name": name or output.name,
        "algorithm": "Stable-Baselines3 PPO", "stableBaselines3Version": stable_baselines3.__version__,
        "pythonVersion": platform.python_version(), "featureNames": FEATURE_NAMES,
        "historyLength": HISTORY_LENGTH, "actionSizes": ACTION_SIZES,
        "datasetSHA256": dataset_hash,
        "sourceReport": report, "seed": seed, "epochs": epochs,
        "actionHistoryDropout": action_history_dropout,
        "heldAttackCost": HELD_ATTACK_COST, "continuedAttackTrainingDecisions": int(continued_attacks[train_indices].sum()),
        "recordingSetupWindows": int(setup.sum()), "startWeightFit": start_fit, "finalValidation": final_validation,
        "startupTrainingClips": len(training_starts), "startupValidationClips": len(validation_starts),
        "maskedKnightTrainingExamples": masked_training_count,
        "blockadeTraining": {"clips": len(np.unique(sessions[blockade_train])),
                            "examples": len(blockade_train), "attackExamples": int(actions[blockade_train, 3].sum())},
        "combatTraining": {"clips": int(len(np.unique(sessions[combat_train]))),
                           "attackExamples": len(strikes), "releaseExamples": len(pauses),
                           "validationClips": int(len(np.unique(sessions[validation_indices][combat_held]))),
                           "validationExamples": int(combat_held.sum())},
        "recoveryTrainingSession": recovery_session,
        "checkpointSelection": "demonstrated blockade presses and releases, held-out startup controls and imitation with own-action feedback"
                               + (" and designated recovery rehearsal" if recovery_session else ""),
        "trainingSessions": sorted(set(sessions[train_indices].tolist())),
        "validationSessions": sorted(validation_sessions.tolist()),
        "trainingExamples": len(train_indices), "validationExamples": len(validation_indices),
        "metrics": metrics, "modelSHA256": hashlib.sha256((output / "model.zip").read_bytes()).hexdigest(),
    }
    if parent is not None:
        model_manifest["supportsMaskedKnight"] = bool(masked_training_count or parent_manifest.get("supportsMaskedKnight")
                                                     or parent_manifest.get("maskedKnightTrainingExamples", 0))
        source = dict(directory=Path(parent).name, modelSHA256=parent_manifest["modelSHA256"])
        model_manifest["parentPolicy"] = source
        last_gym = source if parent_manifest.get("gymEvidence") else parent_manifest.get("lastGymPolicy")
        if last_gym:
            model_manifest["lastGymPolicy"] = last_gym
        bases = set(parent_manifest.get("gymTrainingBases", []))
        bases.add(parent_manifest.get("gymEvidence", {}).get("base", Path(parent).name))
        model_manifest["gymTrainingBases"] = sorted(bases)
        model_manifest["gymRetention"] = retention
    (output / "manifest.json").write_text(json.dumps(model_manifest, indent=2) + "\n")
    return model_manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dataset", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--epochs", type=int, default=20)
    parser.add_argument("--batch-size", type=int, default=128)
    parser.add_argument("--seed", type=int, default=42)
    args = parser.parse_args()
    train(args.dataset, args.output, epochs=args.epochs, batch_size=args.batch_size, seed=args.seed)
