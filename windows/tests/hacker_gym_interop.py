"""Verify the Windows Hacker Gym executable with the trainer's pipe transport."""
import base64
import json
import os
from pathlib import Path
import struct
import subprocess
import sys

sys.path.insert(0, sys.argv[1])
from bot_training_ipc import EncoderReplies
from hkv_bot_dataset import HISTORY_LENGTH, LANDING_GEOMETRY_POLICY_VERSION, SCHEMAS


def check(encoder_command, root):
    version = LANDING_GEOMETRY_POLICY_VERSION
    labels = root / "labels.json"
    frames = (root / "frames.jsonl").read_bytes().splitlines()
    command = [*encoder_command, "--hacker-gym-observations", str(version), str(labels)]
    env = dict(os.environ, HKV_GYM_EXIT_X="12", HKV_GYM_EXIT_Y="1.28")
    env.pop("HKV_ATLAS_PATH_CONNECTIONS", None)
    replies = EncoderReplies()
    with subprocess.Popen(command, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as encoder:
        try:
            rows = [json.loads(replies.request(encoder, frame + b"\n", timeout=10)) for frame in frames]
            encoder.stdin.close()
            assert encoder.wait(timeout=10) == 0, encoder.stderr.read().decode()
        finally:
            if encoder.poll() is None:
                encoder.kill()
                encoder.wait(timeout=10)
            replies.close()

    names = SCHEMAS[version]
    def last(row):
        values = [v[0] for v in struct.iter_unpack("<f", base64.b64decode(row["input"], validate=True))]
        assert len(values) == len(names) * HISTORY_LENGTH
        return dict(zip(names, values[-len(names):], strict=True))

    assert rows[6] == dict(issues=["missing-camera"], pathReachable=False)
    assert all(row["issues"] == [] and row["pathReachable"] for row in rows[:6] + rows[7:])
    assert last(rows[2])["blockade_direction"] == 1 and last(rows[4])["blockade_direction"] == 0
    assert last(rows[4])["previous_attack"] == 1 and last(rows[7])["previous_attack"] == 0
    assert last(rows[0])["shared_path_dx"] == 1 and last(rows[0])["atlas_grid_present"] == 1
    assert abs(rows[3]["pathProgress"] - 20 / 26) < 1e-6
    assert abs(rows[3]["pathAlignment"] - 20 / 26) < 1e-6

    duplicate = subprocess.run(command, env=env, input=(frames[0] + b"\n") * 2, capture_output=True, timeout=10)
    assert duplicate.returncode == 1 and b"frame ordering" in duplicate.stderr, duplicate.stderr.decode()
    print("PASS Windows Hacker Gym sends seven trainer-ready vectors with measured route reward and resets at a missing camera.")


if __name__ == "__main__":
    check(sys.argv[3:], Path(sys.argv[2]))
