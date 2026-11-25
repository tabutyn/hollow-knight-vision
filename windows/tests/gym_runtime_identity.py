"""Verify that the installed Host fingerprints the effective Gym detector inputs."""

import hashlib
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

    def fingerprint(domain="gameplay", override=None, localizers=()):
        environment = os.environ.copy()
        environment.pop("HKV_GYM_VISUAL_MODEL_DIR", None)
        if override is not None:
            environment["HKV_GYM_VISUAL_MODEL_DIR"] = str(override)
        result = subprocess.run([host, "--gym-runtime-info", domain, game, support, *localizers],
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

    restore["trainedClassIdentifiers"] = classes
    write_json(registry, state)
    calibration_dir = detector.parent / "calibrations"
    calibration_dir.mkdir()
    run = restore["sourceRunIdentifier"]
    heatmap = calibration_dir / "localizer.onnx"
    heatmap.write_bytes(b"initial heatmap payload")
    recipe = dict(sourceRunIdentifier=run, weakConfidence=.25, referenceConfidence=.975, correctHUDMatches=True,
        localizer=dict(modelFilename=heatmap.name, sha256=hashlib.sha256(heatmap.read_bytes()).hexdigest(),
                       standaloneConfidence=.99, boxWidth=.045, boxHeight=.14))
    write_json(calibration_dir / f"geo-deposit-{run}.json", recipe)
    assert fingerprint()["visualDetectorAndHUD"] == gameplay, "Untrained actor calibrations cannot change Gym input identity."
    receipt = calibration_dir / f"knight-{run}.json"
    write_json(receipt, recipe)
    calibrated = fingerprint()["visualDetectorAndHUD"]
    assert calibrated != gameplay, "A verified calibration changes effective Gym inputs."
    write_json(receipt, recipe | dict(correctHUDMatches=False))
    assert fingerprint()["visualDetectorAndHUD"] != calibrated, "Calibration thresholds and rules are part of the identity."
    heatmap.write_bytes(b"replacement heatmap payload")
    assert fingerprint()["visualDetectorAndHUD"] == gameplay, "A replacement with the old digest cannot become active."
    recipe["localizer"]["sha256"] = hashlib.sha256(heatmap.read_bytes()).hexdigest()
    write_json(receipt, recipe)
    revised = fingerprint()["visualDetectorAndHUD"]
    assert revised not in (gameplay, calibrated), "The verified replacement must produce a distinct identity."
    assert fingerprint("hacker") == hacker, "Hacker uses no visual calibration."
    write_json(receipt, recipe | dict(sourceRunIdentifier=str(uuid4())))
    assert fingerprint()["visualDetectorAndHUD"] == gameplay, "Only the active source run can enable a calibration."
    override_receipt = calibration_dir / "operator-choice.json"
    write_json(override_receipt, recipe)
    assert fingerprint(localizers=("-", override_receipt))["visualDetectorAndHUD"] == revised

    replacement = root / "Explicit Detector"
    write_json(replacement / "training.json", manifest | dict(trainedClassIdentifiers=classes[:1]))
    replacement_model = replacement / "detector.onnx"
    replacement_model.write_bytes(b"replacement detector")
    replacement_hash = fingerprint(localizers=("-", replacement))["visualDetectorAndHUD"]
    assert replacement_hash not in (revised, gameplay), "A legacy replacement directory also contributes model identity."
    replacement_model.write_bytes(b"updated replacement detector")
    assert fingerprint(localizers=("-", replacement))["visualDetectorAndHUD"] != replacement_hash

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
    print("PASS Windows Gym fingerprints detector training evidence, verified calibrations, and explicit replacements.")
