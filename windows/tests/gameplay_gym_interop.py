"""Exercise the Game Play encoder with the same line protocol as the Gym trainer."""
import base64
import json
import os
from pathlib import Path
import subprocess
import sys

import numpy as np
from onnx import TensorProto, helper, save

sys.path.insert(0, sys.argv[1])
from bot_training_ipc import EncoderReplies
from hkv_bot_dataset import HISTORY_LENGTH, LANDING_GEOMETRY_POLICY_VERSION, SCHEMAS

WIDTH, HEIGHT = 640, 360
CLASSES = ["game.playable-knight", "enemies.crawlid", "world.geo-deposit"]


def fixture_detector(directory):
    directory.mkdir(parents=True)
    boxes = [[.26, .61, .80], [.53, .58, .57], [.045, .06, .045], [.14, .11, .11]]
    scores = [[.96, 0, 0], [0, .91, 0], [0, 0, .88]]
    values = [helper.make_tensor("scores", TensorProto.FLOAT, [1, 3, 1, 3], sum(scores, [])),
              helper.make_tensor("boxes", TensorProto.FLOAT, [1, 4, 1, 3], sum(boxes, []))]
    graph = helper.make_graph([], "fixture-detector",
        [helper.make_tensor_value_info("image", TensorProto.FLOAT, [1, 3, HEIGHT, WIDTH])],
        [helper.make_tensor_value_info("scores", TensorProto.FLOAT, [1, 3, 1, 3]),
         helper.make_tensor_value_info("boxes", TensorProto.FLOAT, [1, 4, 1, 3])], values)
    save(helper.make_model(graph, ir_version=9, opset_imports=[helper.make_opsetid("", 17)]),
         directory / "Detector.onnx")
    (directory / "training.json").write_text(json.dumps(dict(modelFormat="onnx",
        modelFilename="Detector.onnx", classIdentifiers=CLASSES)))


def pixels():
    y, x = np.indices((HEIGHT, WIDTH))
    noise = ((x * 23 + y * 37 + (x // 13) * 17) % 33).astype(np.uint8)
    value = np.where(y < 239, 20 + noise // 4, 10 + noise).astype(np.uint8)
    value[239:241, :] = 120 + noise[239:241, :]
    image = np.stack([value, value, value], axis=-1)
    image[191:239, 168:194] = [220, 230, 235]
    image[209:240, 394:429] = [175, 154, 138]
    image[211:240, 514:537] = [167, 169, 192]
    # Unity RGB24 is bottom-up.
    return image[::-1].tobytes()


def run(command, frames, env, rgb, image):
    replies = EncoderReplies()
    with subprocess.Popen(command, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE) as encoder:
        try:
            rows = []
            for index, request in enumerate(frames):
                rgb.write_bytes(bytes(len(image)) if index == 3 else image)
                payload = json.dumps(request).encode() + b"\n"
                reply = replies.request(encoder, payload, timeout=20)
                assert reply, encoder.stderr.read().decode()
                rows.append(json.loads(reply))
            encoder.stdin.close()
            assert encoder.wait(timeout=10) == 0, encoder.stderr.read().decode()
            return rows
        finally:
            if encoder.poll() is None:
                encoder.kill()
                encoder.wait(timeout=10)
            replies.close()


def check(encoder_command, root):
    version = LANDING_GEOMETRY_POLICY_VERSION
    model = root / "visual-onnx"
    fixture_detector(model)
    rgb = root / "frame.rgb"
    image = pixels()
    source = [json.loads(line) for line in (root / "frames.jsonl").read_text().splitlines()]
    frames = [dict(ticks=row["ticks"], previous=row["previous"], sample=row["sample"],
                   width=WIDTH, height=HEIGHT) for row in source]
    last = frames[-1]
    frames.extend(last | dict(ticks=last["ticks"] + 6 * (i + 1)) for i in range(7))
    fake = [row | dict(sample=row["sample"] | dict(cameraAvailable=False, heroAvailable=False,
        heroX=150, heroY=-35, actors=dict(version=1, enemies=[], heroHealth=1))) for row in frames]

    env = dict(os.environ, HKV_GYM_LABELS=str(root / "labels.json"),
        HKV_GYM_VISUAL_MODEL_DIR=str(model), HKV_GYM_VISUAL_SUPPORT_ROOT=str(root / "empty-support"),
        HKV_GYM_EXIT_X="12", HKV_GYM_EXIT_Y="1.28")
    env.pop("HKV_ATLAS_PATH_CONNECTIONS", None)
    command = [*encoder_command, "--gameplay-gym-observations", str(version), str(rgb), "true"]
    baseline = run(command, frames, env, rgb, image)
    changed = run(command, fake, env, rgb, image)
    assert baseline[0]["pathReachable"] and not changed[0]["pathReachable"]
    assert baseline[0]["knight"] == "visual" and baseline[0]["floors"] > 0, baseline[0] | {"input": "omitted"}
    assert baseline[0]["healthKnown"] is False and changed[0]["healthKnown"] is False
    assert baseline[3]["issues"] == ["transition-blackout"] and "input" not in baseline[3]
    assert any("input" in row for row in baseline[4:]), "Visual perception did not recover after the blackout."
    assert any(row["atlas"] for row in baseline[4:]), "Verified pixel geometry never established an Atlas footprint."

    names = SCHEMAS[version]
    route = ("road_", "shared_path_")
    compared = 0
    for left, right in zip(baseline, changed, strict=True):
        assert left["issues"] == right["issues"]
        assert ("input" in left) == ("input" in right)
        if "input" not in left:
            continue
        def vector(row):
            return np.frombuffer(base64.b64decode(row["input"], validate=True), dtype="<f4").reshape(HISTORY_LENGTH, -1)
        a, b = vector(left), vector(right)
        assert a.shape[1] == len(names)
        pixel_columns = [i for i, name in enumerate(names) if not name.startswith(route)]
        np.testing.assert_array_equal(a[:, pixel_columns], b[:, pixel_columns])
        compared += 1
    assert compared >= 4, f"Only {compared} visual decisions had valid terrain."
    print(f"PASS Windows Game Play Gym returned {compared} visual vectors unchanged by altered mod actors, pose, or health.")


if __name__ == "__main__":
    check(sys.argv[3:], Path(sys.argv[2]))
