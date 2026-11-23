"""Fit a per-skill ability head from curated, extended-label demonstration clips."""
import argparse
import hashlib
import json
from pathlib import Path
import tempfile

import numpy as np
import torch

from hkv_bot_dataset import HISTORY_LENGTH, SCHEMAS, SKILL_BUTTONS
from bot_policy_inputs import policy_columns

ABILITIES = {"explore": ("dash", "superDash"), "traverse": ("dash", "superDash"),
             "fight": ("quickCast", "dash", "dreamNail"), "recover": ("a",), "progress": ("dreamNail",)}


def fit(dataset, domain, skill, validation_clips, epochs=40):
    report = json.loads(dataset.with_suffix(".json").read_text())
    version = report["schemaVersion"]
    if (report.get("skillButtons") != list(SKILL_BUTTONS) or report["historyLength"] != HISTORY_LENGTH
            or report["datasetSHA256"] != hashlib.sha256(dataset.read_bytes()).hexdigest()
            or any(row.get("botDomain") != domain for row in report["sessions"])):
        raise ValueError("Expected a verified extended-label dataset from one bot domain")
    with np.load(dataset, allow_pickle=False) as archive:
        x, actions, clips = (archive[key] for key in ("observations", "actions", "session_ids"))
    if (version not in SCHEMAS or x.shape != (len(clips), len(SCHEMAS[version]) * HISTORY_LENGTH)
            or actions.shape != (len(clips), 4 + len(SKILL_BUTTONS)) or not np.isfinite(x).all()):
        raise ValueError("Incompatible observation or action shape")
    extra = actions[:, 4:]
    if not np.isin(extra, (0, 1)).all() or not set(validation_clips).issubset(clips.tolist()):
        raise ValueError("Invalid button labels or unknown validation clip")
    # Keep whole replay runs together, even when one run was recorded in several clips.
    run_ids = {row["id"]: row["sourceRun"] for row in report["sessions"]}
    held_runs = {run_ids[clip] for clip in validation_clips}
    held = np.asarray([run_ids[clip] in held_runs for clip in clips])
    allowed = [SKILL_BUTTONS.index(name) for name in ABILITIES[skill]]
    other = [i for i in range(len(SKILL_BUTTONS)) if i not in allowed]
    eligible = (extra.sum(axis=1) <= 1) & ~extra[:, other].any(axis=1)
    train, test = eligible & ~held, eligible & held
    labels = ["none"] + [SKILL_BUTTONS[i] for i in allowed if extra[train, i].any()]
    if len(labels) == 1 or not test.any() or not (extra[train].sum(axis=1) == 0).any():
        raise ValueError("Need demonstrated ability and release examples plus a separate validation run")
    indices = [SKILL_BUTTONS.index(name) for name in labels[1:]]
    unseen = [i for i in allowed if i not in indices]
    if unseen and extra[test][:, unseen].any():
        raise ValueError("Validation contains an ability absent from training")
    y = np.column_stack([extra.sum(axis=1) == 0, extra[:, indices]]).argmax(axis=1)
    if any(not (y[test] == i).any() for i in range(len(labels))):
        raise ValueError("Include every trained ability and a release in the held-out runs")
    torch.manual_seed(42)
    columns = policy_columns(version, skill) if version in (14, 15, 16) else None
    features = torch.from_numpy(x if columns is None else x[:, columns]).float()
    targets = torch.from_numpy(y).long()
    model = torch.nn.Sequential(torch.nn.Linear(features.shape[1], 32), torch.nn.Tanh(), torch.nn.Linear(32, len(labels)))
    optimizer = torch.optim.AdamW(model.parameters(), lr=0.002)
    counts = torch.bincount(targets[train], minlength=len(labels))
    weights = counts.float().rsqrt()
    train_rows = torch.from_numpy(np.flatnonzero(train))
    for _ in range(epochs):
        for batch in train_rows[torch.randperm(len(train_rows))].split(128):
            loss = torch.nn.functional.cross_entropy(model(features[batch]), targets[batch], weight=weights)
            optimizer.zero_grad()
            loss.backward()
            optimizer.step()
    with torch.no_grad():
        predicted = model(features[test]).argmax(dim=1).numpy()
    matrix = np.zeros((len(labels), len(labels)), dtype=int)
    np.add.at(matrix, (y[test], predicted), 1)
    layers = [dict(inputSize=layer.in_features, outputSize=layer.out_features, activation=activation,
                   weights=layer.weight.detach().flatten().tolist(), biases=layer.bias.detach().tolist())
              for layer, activation in ((model[0], "tanh"), (model[2], "linear"))]
    layout = dict(version=1) if columns is None else dict(version=2, inputColumns=columns)
    return dict(**layout, domain=domain, skill=skill, observationSchema=version, historyLength=HISTORY_LENGTH,
                labels=labels, layers=layers, training=dict(datasetSHA256=report["datasetSHA256"], seed=42,
                epochs=epochs, trainingExamples=int(train.sum()), validationRuns=sorted(held_runs),
                validationExamples=int(test.sum()), validationConfusion=matrix.tolist()))


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("dataset", type=Path)
    cli.add_argument("--domain", required=True, choices=("gameplay", "hacker"))
    cli.add_argument("--skill", required=True, choices=ABILITIES)
    cli.add_argument("--validation-clip", required=True, action="append")
    cli.add_argument("--output", required=True, type=Path)
    args = cli.parse_args()
    result = fit(args.dataset, args.domain, args.skill, args.validation_clip)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile("w", dir=args.output.parent, delete=False) as staged:
        json.dump(result, staged, allow_nan=False)
    Path(staged.name).replace(args.output)
    print(json.dumps(result["training"], indent=2))


if __name__ == "__main__":
    main()
