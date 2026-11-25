"""Pass original Windows capture evidence through the installed Host and shared exporter."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, sys.argv[1])
from bot_audit import inspect as inspect_clip
from hkv_bot_dataset import collect


def verify_wall_replay(root, vision, fixture):
    clip = Path(fixture["clip"])
    source = inspect_clip(clip)["sourceArchive"]
    assert Path(source) == Path(fixture["archive"])
    events = clip / "events.jsonl"
    checksum = hashlib.sha256(events.read_bytes()).hexdigest()

    def recover(*arguments):
        return subprocess.run([vision, "--recover-bot-walls", *arguments],
                              timeout=45, capture_output=True, text=True)

    result = recover(clip, source)
    assert result.returncode == 0, result.stderr
    rows = json.loads(result.stdout)
    assert [(row["generation"], row["captureTimestamp"]) for row in rows] == [(7, 100.05), (7, 100.05 + .1)]
    assert [row["features"][5] for row in rows] == [1, 0]
    assert recover(clip).returncode == 64
    assert recover(clip, root / "foreign-archive").returncode == 1
    assert json.loads(recover(fixture["hackerClip"], source).stdout) == []

    evidence = root / "recovered-walls"
    evidence.mkdir()
    manifest = json.loads((clip / "manifest.json").read_text())
    payload = dict(id=manifest["id"], eventsSHA256=checksum, rows=rows)
    (evidence / (clip.name + ".json")).write_text(json.dumps(payload))
    _, report = collect(clip, wall_evidence=evidence, compact=True, policy_version=2)
    assert not report["skipped"] and report["examples"] > 2, report
    session = report["sessions"][0]
    assert session["wallAnalyzedExamples"] == 2 and session["wallPresentExamples"] == 1, session
    assert hashlib.sha256(events.read_bytes()).hexdigest() == checksum
    print("PASS installed Windows Host restored exact archived wall evidence for the shared bot exporter.")


if __name__ == "__main__":
    root = Path(sys.argv[2])
    fixture = json.loads((root / "probe.json").read_text(encoding="utf-8-sig"))
    verify_wall_replay(root, Path(sys.argv[3]), fixture)
