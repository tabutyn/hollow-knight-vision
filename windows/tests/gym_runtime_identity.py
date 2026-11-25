"""Verify that the installed Host fingerprints the effective Gym detector inputs."""

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from uuid import uuid4


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding="utf-8")


def verify(host, root):
    game = root / "Fixture Game" / "hollow_knight.exe"
    managed = game.parent / "hollow_knight_Data" / "Managed"
    receiver = managed / "Mods" / "HollowKnightVisionInputReceiver" / "HollowKnightVisionInputReceiver.dll"
    receiver.parent.mkdir(parents=True)
    for file in (game, managed / "Assembly-CSharp.dll", managed / "MMHOOK_Assembly-CSharp.dll", receiver):
        file.write_text(file.name, encoding="utf-8")

    support = root / "Fixture Support"
    models = support / "models-v1"
    registry = models / "registry.json"
    model_class = "shared-object-model"
    detector = models / model_class / "v1.0"
    classes = ["game.playable-knight", "enemies.crawlid"]
    manifest = dict(modelFormat="onnx", modelFilename="detector.onnx", classIdentifiers=classes)
    write_json(detector / "training.json", manifest)
    (detector / "detector.onnx").write_bytes(b"unchanged model payload")

    def fingerprint(domain="gameplay", override=None):
        environment = os.environ.copy()
        environment.pop("HKV_GYM_VISUAL_MODEL_DIR", None)
        if override is not None:
            environment["HKV_GYM_VISUAL_MODEL_DIR"] = str(override)
        result = subprocess.run([host, "--gym-runtime-info", domain, game, support],
                                capture_output=True, text=True, env=environment, timeout=30)
        assert result.returncode == 0, result.stderr
        return json.loads(result.stdout)

    restore = dict(classIdentifier=model_class, version=dict(major=1, minor=0),
                   sourceRunIdentifier=str(uuid4()), createdAt="2026-01-01T00:00:00Z",
                   modelFilename=manifest["modelFilename"], trainedClassIdentifiers=classes)
    record = dict(restorePoints=[restore], activeMajorVersion=1)
    state = dict(schemaVersion=1, objects={model_class: record})
    write_json(registry, state)
    gameplay = fingerprint()["visualDetectorAndHUD"]
    hacker = fingerprint("hacker")

    restore["trainedClassIdentifiers"] = list(reversed(classes))
    record["developmentRunIdentifier"] = str(uuid4())
    write_json(registry, state)
    assert fingerprint()["visualDetectorAndHUD"] == gameplay, "Class order and inactive candidates must be ignored."

    restore["trainedClassIdentifiers"] = classes[:1]
    write_json(registry, state)
    reduced = fingerprint()["visualDetectorAndHUD"]
    assert reduced != gameplay, "The active class mask changed with identical ONNX files."
    restore["trainedClassIdentifiers"] = classes[:1] + ["world.geo-deposit"]
    write_json(registry, state)
    assert fingerprint()["visualDetectorAndHUD"] == reduced, "The detector does not declare the extra class."
    assert fingerprint("hacker") == hacker, "Hacker observations do not use visual detector class evidence."

    dataset = uuid4()
    training_root = support / "training-v1"
    legacy = training_root / "runs" / str(uuid4())
    write_json(legacy / "training.json", manifest | dict(datasetIdentifier=str(dataset)))
    (legacy / "detector.onnx").write_bytes(b"unchanged legacy model payload")
    annotations = training_root / "datasets" / str(dataset) / "training" / "annotations.json"
    positives = [dict(label=label, coordinates=dict(width=10, height=10)) for label in classes]
    write_json(annotations, [dict(annotations=positives)])
    legacy_fingerprint = fingerprint(override=legacy)["visualDetectorAndHUD"]
    write_json(annotations, [dict(annotations=list(reversed(positives)))])
    assert fingerprint(override=legacy)["visualDetectorAndHUD"] == legacy_fingerprint
    write_json(annotations, [dict(annotations=positives[:1] + [positives[1] | dict(isNegative=True)])])
    assert fingerprint(override=legacy)["visualDetectorAndHUD"] != legacy_fingerprint


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="hkv-gym-identity-") as temporary:
        verify(Path(sys.argv[1]).resolve(), Path(temporary))
    print("PASS installed Windows Gym fingerprints active and legacy detector training evidence.")
