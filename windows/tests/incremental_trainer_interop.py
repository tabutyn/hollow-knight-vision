"""Run object training through the Windows launcher using the native dataset fixture."""

import copy
import json
from pathlib import Path
import shutil
import subprocess
import sys

import torch


KNIGHT = "game.playable-knight"
CRAWLID = "enemies.crawlid"
VENGFLY = "enemies.vengfly"
GEO = "world.geo-deposit"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path, value):
    path.write_text(json.dumps(value), encoding="utf-8")


def train(launcher, dataset, output, checkpoint=None):
    command = [
        "pwsh", "-NoProfile", "-File", str(launcher),
        "-Dataset", str(dataset), "-Output", str(output), "-Iterations", "1",
    ]
    if checkpoint is not None:
        command += ["-BaseCheckpoint", str(checkpoint)]
    subprocess.run(command, check=True, timeout=180)


def incremental_dataset(source, destination):
    shutil.copytree(source, destination)
    training_path = destination / "training/annotations.json"
    validation_path = destination / "validation/annotations.json"
    training = read_json(training_path)
    validation = read_json(validation_path)
    box, = [
        label
        for row in training for label in row["annotations"]
        if label["label"] == CRAWLID and not label.get("isNegative")
    ]
    box["label"] = GEO
    # Older snapshots can include positive examples only in validation. Crawlid
    # has a valid certificate from the first run. Vengfly has only a legacy head.
    validation[0]["annotations"].extend([
        copy.deepcopy(box) | {"label": CRAWLID},
        copy.deepcopy(box) | {"label": VENGFLY},
    ])
    write_json(training_path, training)
    write_json(validation_path, validation)
    manifest_path = destination / "dataset.json"
    manifest = read_json(manifest_path)
    manifest["classIdentifiers"] = sorted([KNIGHT, CRAWLID, VENGFLY, GEO])
    by_image = {row["image"]: row["annotations"] for row in training + validation}
    for item in manifest["items"]:
        labels = by_image[item["imageFilename"]]
        item["annotationCount"] = sum(not label.get("isNegative") for label in labels)
        item["knownClassIdentifiers"] = sorted(
            set(item["knownClassIdentifiers"] or []) | {label["label"] for label in labels}
        )
    write_json(manifest_path, manifest)


def main():
    source, dataset, launcher, root = map(Path, sys.argv[1:])
    sys.path.insert(0, str(source))
    import hkv_incremental_trainer as trainer

    initial = root / "detector-initial"
    train(launcher, dataset, initial)
    base = torch.load(initial / "Detector.pt", map_location="cpu", weights_only=False)
    assert base["class_names"] == base["trained_class_names"] == sorted([KNIGHT, CRAWLID])

    expanded = sorted([KNIGHT, CRAWLID, VENGFLY, GEO])
    legacy = root / "legacy-checkpoint.pt"
    torch.save(base | {
        "class_names": expanded,
        "state_dict": trainer.make_model(expanded, base).state_dict(),
    }, legacy)
    revised_dataset = root / "incremental-dataset"
    incremental_dataset(dataset, revised_dataset)

    result = root / "detector-incremental"
    train(launcher, revised_dataset, result, checkpoint=legacy)
    expected = sorted([KNIGHT, CRAWLID, GEO])
    summary = read_json(result / "training.json")
    assert summary["baseRunIdentifier"] == initial.name
    assert summary["trainingDevice"] == "cpu" and summary["modelFormat"] == "onnx"
    assert summary["optimizerStepCount"] > 0 and all(loss >= 0 for loss in summary["epochLosses"])
    assert summary["classIdentifiers"] == summary["trainedClassIdentifiers"] == expected
    learned = torch.load(result / summary["checkpointFilename"], map_location="cpu", weights_only=False)
    assert learned["class_names"] == learned["trained_class_names"] == expected
    assert learned["state_dict"]["class_head.output.weight"].shape[0] == len(expected)
    review = read_json(result / "predictions.json")
    assert any(box["label"] == VENGFLY for frame in review["examples"] for box in frame["human"])
    print("PASS Windows incremental training retains certified classes, learns positive classes, and excludes a validation-only legacy head")


if __name__ == "__main__":
    main()
