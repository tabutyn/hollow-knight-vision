"""Report measured Swift host coverage, not coverage of embedded Metal shaders."""
import json
import pathlib
import sys

TRACKING_FILES = {
    "GroundMetalProcessing.swift", "GroundCameraSolver.swift",
    "GroundHypothesisTracker.swift", "LiveCaptureModel.swift",
    "LiveTrackingTiming.swift", "LayerSceneViewport.swift",
}


def report(document):
    files = []
    for data in document["data"]:
        for entry in data["files"]:
            name = pathlib.PurePosixPath(entry["filename"]).name
            if name not in TRACKING_FILES:
                continue
            files.append({"file": name, "summary": entry["summary"]})
    if "GroundMetalProcessing.swift" not in {entry["file"] for entry in files}:
        raise ValueError("Coverage contains no GroundMetalProcessing.swift; refusing an empty report")
    return {
        "scope": "Measured Swift host code; runtime-compiled Metal shader strings are not instrumented",
        "shader_evidence": "GroundMetalProcessingTests must assert the Metal backend and CPU/GPU parity",
        "files": sorted(files, key=lambda entry: entry["file"]),
    }


if __name__ == "__main__":
    source, destination = map(pathlib.Path, sys.argv[1:])
    result = report(json.loads(source.read_text()))
    destination.write_text(json.dumps(result, indent=2) + "\n")
    for entry in result["files"]:
        lines = entry["summary"]["lines"]
        functions = entry["summary"]["functions"]
        print(f"{entry['file']}: lines {lines['covered']}/{lines['count']} "
              f"({lines['percent']:.2f}%), functions {functions['covered']}/{functions['count']}")
