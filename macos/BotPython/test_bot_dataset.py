import copy
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace

import hkv_bot_dataset as dataset


def manifest():
    return {"schemaVersion": 2, "id": "test-session", "featureNames": dataset.RECORDING_FEATURE_NAMES,
            "actionSizes": [3, 3, 2, 2], "historyLength": 4, "sampleInterval": 0.1,
            "status": "completed", "startedAt": 100.0, "duration": 5.0,
            "observationCount": 0, "inputEventCount": 0, "usableObservationCount": 0, "imageCount": 0}


def observation(time, room=1, issues=None):
    features = [0.0] * len(dataset.RECORDING_FEATURE_NAMES)
    features[4] = 0.9
    features[6] = 1.0
    return {"type": "observation", "availableAt": time, "capturedAt": time - 0.02,
            "captureTimestamp": time - 0.02, "detectionTimestamp": time - 0.05,
            "generation": 1, "roomID": room, "roomRevision": 1, "features": features,
            "issues": issues or []}


def event(time, button, pressed=True):
    return {"type": "input", "timestamp": time, "button": button, "isPressed": pressed}


def road_values(row, offset=0):
    start = len(row["observation"]) - (offset + 1) * dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("road_present")
    return row["observation"][start:start + len(dataset.ROAD_NAMES)]


class BotDatasetTests(unittest.TestCase):
    def test_landing_geometry_follows_moving_shelf_in_current_room(self):
        frames = [observation(100.1), observation(100.2), observation(100.3, room=2)]
        for frame, (x, y) in zip(frames, ((320, 175), (336, 182), (336, 182))):
            frame["features"][:4] = [x/640, y/640, 32/640, 32/640]
            frame["grounded"] = False
            frame["terrainSource"] = "human-authored"
            frame["authoredTerrain"] = dict(width=640, height=360, floors=[
                dict(minimumX=328, maximumX=392, y=128, confidence=1.),
                dict(minimumX=100, maximumX=520, y=80, confidence=1.),
            ], walls=[dict(x=200, bottomY=50, topY=100)])
        rows, rejected = dataset.examples(manifest(), frames, [], policy_version=dataset.LANDING_GEOMETRY_POLICY_VERSION)
        self.assertEqual(rejected, {})
        self.assertEqual(len(rows), 3)
        expected = [[1, .25, 2.25, .46875, 0, 0],
                    [1, -.25, 1.75, .6875, .5, .21875],
                    [1, -.25, 1.75, .6875, 0, 0]]
        for row, landing in zip(rows, expected):
            self.assertEqual(len(row["observation"]), 848)
            for measured, wanted in zip(row["observation"][-6:], landing):
                self.assertAlmostEqual(measured, wanted, places=6)
        import numpy as np
        from compact_markings import mask_walls, wall_present
        packed = np.asarray(rows[-1]["observation"][-212:], dtype=np.float32)[None, :]
        names = dataset.SCHEMAS[dataset.LANDING_GEOMETRY_POLICY_VERSION]
        self.assertTrue(wall_present(packed, names)[0])
        mask_walls(packed, names)
        self.assertFalse(wall_present(packed, names)[0])
        np.testing.assert_allclose(packed[0, -6:], expected[-1])

    def test_ground_contact_adds_one_channel_per_frame_and_preserves_legacy_inputs(self):
        frames = [observation(100.1 + i * .1) for i in range(4)]
        frames[0]["grounded"] = True
        frames[1]["grounded"] = False
        frames[3]["grounded"] = True
        old, _ = dataset.examples(manifest(), frames, [])
        version = dataset.GROUND_CONTACT_POLICY_VERSION
        new, _ = dataset.examples(manifest(), frames, [], policy_version=version)
        width = dataset.FEATURE_COUNT + 1
        self.assertEqual(dataset.SCHEMAS[version], dataset.FEATURE_NAMES + ["knight_ground_contact"])
        self.assertEqual(len(new), len(old))
        self.assertEqual(new[-1]["observation"][width - 1::width], [1, -1, 0, 1])
        for legacy, contact in zip(old, new):
            self.assertEqual([v for i, v in enumerate(contact["observation"]) if (i + 1) % width],
                             legacy["observation"])
            self.assertEqual(contact["outcomeWeight"], legacy["outcomeWeight"])

    def test_blue_health_outcomes_credit_only_recorded_gain(self):
        rows = [dict(segment=0, timestamp=i * .1, outcomeWeight=1.) for i in range(5)]
        outcomes = [dict(epoch="a", gained=n) for n in (0, 0, 2, 2, 2)]
        dataset.credit_blue_health_outcomes(rows, outcomes)
        self.assertEqual([row["blueHealthGained"] for row in rows], [0, 0, 2, 0, 0])
        dataset.exploration_weights(rows, [None] * 5, [0] * 5)
        self.assertAlmostEqual(rows[0]["outcomeWeight"], 2 ** (2 / 32))
        self.assertAlmostEqual(rows[1]["outcomeWeight"], 2 ** (2 / 32))
        self.assertEqual(rows[2]["outcomeWeight"], 1.)
        rows = [dict(segment=i // 2, timestamp=i * .1) for i in range(4)]
        dataset.credit_blue_health_outcomes(rows, [dict(epoch="a", gained=n) for n in (0, 0, 2, 2)])
        self.assertTrue(all(row["blueHealthGained"] == 0 for row in rows))

    def test_blue_health_recording_replay_preserves_raw_gain_and_policy_inputs(self):
        frames = [observation(100.1 + i * .1) for i in range(5)]
        for i, item in enumerate(frames):
            item["blueHealthOutcomes"] = dict(epoch="a", gained=0 if i < 2 else 2)
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual([r["blueHealthGained"] for r in rows], [0, 0, 2, 0, 0])
        self.assertAlmostEqual(rows[0]["outcomeWeight"], 2 ** (2 / 32))
        for item in frames:
            del item["blueHealthOutcomes"]
        legacy, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual([r["observation"] for r in rows], [r["observation"] for r in legacy])
        self.assertTrue(all(r["outcomeWeight"] == 1 for r in legacy))

    def test_reconstructed_paths_are_frame_matched_and_preserve_current_evidence(self):
        frames = [observation(100.1), observation(100.2), observation(100.3)]
        legacy = dict(dx=0, dy=-1, clearance=.1, distance=.3, frontier=True)
        current = dict(dx=-1, dy=0, clearance=.1, distance=.2, frontier=False)
        frames[0]["roadHint"] = legacy
        frames[2].update(atlasPathVersion=1, roadHint=current, pathProgress=.2)
        recovered = [
            dict(index=0, generation=1, captureTimestamp=frames[0]["captureTimestamp"],
                 roadHint=dict(dx=1, dy=0, clearance=.1, distance=.2, frontier=True), pathProgress=0),
            dict(index=1, generation=1, captureTimestamp=frames[1]["captureTimestamp"],
                 pathProgress=.25),
        ]
        with patch.object(dataset.subprocess, "run", return_value=SimpleNamespace(stdout=json.dumps(recovered).encode())):
            dataset.reconstruct_recorded_paths("/vision", Path("/clip"), frames)
        self.assertEqual(frames[0]["roadHint"]["dx"], 1)
        self.assertEqual(frames[1]["pathProgress"], .25)
        self.assertEqual(frames[2]["roadHint"], current)
        invalid = [dict(index=0, generation=1, captureTimestamp=999)]
        with patch.object(dataset.subprocess, "run", return_value=SimpleNamespace(stdout=json.dumps(invalid).encode())):
            with self.assertRaisesRegex(ValueError, "does not match"):
                dataset.reconstruct_recorded_paths("/vision", Path("/clip"), frames)

    def test_export_reconstructs_legacy_paths_without_changing_capture(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            frames = [observation(100.1 + i * .1) for i in range(4)]
            for frame in frames:
                frame["roadHint"] = dict(dx=-1, dy=0, clearance=.1, distance=.2, frontier=True)
            m = manifest() | dict(observationCount=len(frames), usableObservationCount=len(frames))
            (root / "manifest.json").write_text(json.dumps(m))
            events = root / "events.jsonl"
            events.write_text("".join(json.dumps(row) + "\n" for row in frames))
            before = events.read_bytes()
            recovered = [dict(index=i, generation=1, captureTimestamp=row["captureTimestamp"],
                              roadHint=dict(dx=1, dy=0, clearance=.1, distance=.2, frontier=True),
                              pathProgress=.25 if i else 0)
                         for i, row in enumerate(frames)]
            with patch.object(dataset.subprocess, "run", return_value=SimpleNamespace(stdout=json.dumps(recovered).encode())) as native:
                rows, report = dataset.collect(root, path_extractor="/vision")
            self.assertEqual(native.call_count, 1)
            self.assertFalse(report["skipped"])
            self.assertEqual(road_values(rows[0]), [1, 1, 0, .1, .2, 1])
            self.assertEqual(events.read_bytes(), before)

    def test_atlas_road_is_causal_and_optional_for_legacy_clips(self):
        frames = [observation(100.1+i*.1) for i in range(4)]
        frames[1]["roadHint"] = dict(dx=-1, dy=0, clearance=.1, distance=.25, frontier=True)
        legacy, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(road_values(legacy[1]), [0]*6)
        frames[1]["pathProgress"] = 0
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(road_values(rows[0]), [0]*6)
        self.assertEqual(road_values(rows[1]), [1, -1, 0, .1, .25, 1])
        self.assertEqual(road_values(rows[2], offset=1), road_values(rows[1]))
        self.assertEqual(dataset.SCHEMAS[7], dataset.ROAD_FEATURE_NAMES[:-6])

    def test_atlas_and_enemy_rewards_change_training_weights_only(self):
        frames = [observation(100.1 + i * 0.1) for i in range(30)]
        for i, frame in enumerate(frames):
            frame["atlasView"] = {"left": 0.4, "right": 1, "health": 5 if i < 10 else 4,
                                  "width": 640, "camera": [0, 0], "earnedCells": 16 if i >= 5 else 0}
        frames[5]["pathProgress"] = 8
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertGreater(rows[0]["outcomeWeight"], 1)
        self.assertLess(rows[9]["outcomeWeight"], 1)
        self.assertLess(rows[23]["outcomeWeight"], 1)
        last_frame = rows[0]["observation"][-dataset.FEATURE_COUNT:]
        self.assertEqual(last_frame[dataset.FEATURE_NAMES.index("atlas_present"):dataset.FEATURE_NAMES.index("atlas_present") + 5], [1, .4, 1, 1, 5 / 16])
        self.assertEqual(last_frame[dataset.FEATURE_NAMES.index("atlas_grid_present"):dataset.FEATURE_NAMES.index("atlas_grid_present") + 16], [0] * 16)
        for i, frame in enumerate(frames):
            frame.pop("pathProgress", None)
            frame.pop("atlasView")
            frame["enemyOutcomes"] = {"epoch": "a" if i < 16 else "b",
                                      "damage": 5 if 5 <= i < 16 else 20 if i >= 16 else 0,
                                      "kills": 1 if 5 <= i < 16 else 3 if i >= 16 else 0}
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertGreater(rows[1]["outcomeWeight"], 2)
        self.assertEqual(sum(row["enemyKills"] for row in rows), 1)
        self.assertEqual(sum(row["enemyDamage"] for row in rows), 5)
        self.assertEqual(rows[7]["outcomeWeight"], 1)
        for frame in frames:
            del frame["enemyOutcomes"]
        unlabelled, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual([row["observation"] for row in rows], [row["observation"] for row in unlabelled])
        self.assertTrue(all(row["outcomeWeight"] == 1 for row in unlabelled))

    def test_path_progress_rewards_forward_outcome_not_brightness_or_coverage(self):
        frames = [observation(100.1 + i * 0.1) for i in range(8)]
        for i, frame in enumerate(frames):
            frame["atlasView"] = {"left": .3, "right": .9, "health": 5, "width": 640,
                                  "camera": [i * 20, 0], "earnedCells": i * 10}
            frame["roadHint"] = dict(dx=1, dy=0, clearance=.1, distance=.2, frontier=True)
        legacy, _ = dataset.examples(manifest(), frames, [])
        self.assertTrue(all(row["outcomeWeight"] == 1 for row in legacy))
        self.assertTrue(all(road_values(row) == [0] * 6 for row in legacy))
        for frame in frames:
            frame["atlasPathVersion"] = 1
        inputs, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(road_values(inputs[2]), [1, 1, 0, .1, .2, 1])
        self.assertTrue(all(row["outcomeWeight"] == 1 for row in inputs))
        for frame in frames:
            frame["pathProgress"] = 0
        frames[4]["pathProgress"] = 4
        recorded, _ = dataset.examples(manifest(), frames, [])
        self.assertGreater(recorded[0]["outcomeWeight"], 1)
        self.assertEqual(recorded[4]["outcomeWeight"], 1)
        self.assertEqual(road_values(recorded[2]), [1, 1, 0, .1, .2, 1])
        frames[4]["pathProgress"] = -1
        with self.assertRaisesRegex(ValueError, "path progress"):
            dataset.examples(manifest(), frames, [])

    def test_shared_path_features_and_signed_causal_credit(self):
        frames = [observation(100.1 + i * .1) for i in range(8)]
        hint = dict(dx=1, dy=0, clearance=.1, distance=.2, frontier=True)
        for frame in frames:
            frame.update(atlasPathVersion=1, roadHint=hint, pathProgress=0)
        legacy, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(legacy[2]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_present"):-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_dy") + 1], [0, 0, 0])
        for frame in frames:
            frame.update(hackerPathVersion=1, pathAlignment=0)
        frames[4]["pathAlignment"] = -2
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(rows[2]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_present"):-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_dy") + 1], [1, 1, 0])
        self.assertLess(rows[0]["outcomeWeight"], 1)
        self.assertEqual(rows[4]["outcomeWeight"], 1)
        frames[4]["pathAlignment"] = 2
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertGreater(rows[0]["outcomeWeight"], 1)
        frames[2]["roadHint"] = dict(hint, dx=0, dy=0)
        zero, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(zero[2]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_present"):-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_dy") + 1], [0, 0, 0])
        frames[4]["pathAlignment"] = float("nan")
        with self.assertRaisesRegex(ValueError, "path alignment"):
            dataset.examples(manifest(), frames, [])

    def test_exit_reconstruction_persists_and_checks_frame_identity(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clip = root / "demo-clip"
            clip.mkdir()
            replay_id = "12345678-1234-1234-1234-123456789abc"
            m = manifest() | {"replayID": replay_id, "observationCount": 2, "usableObservationCount": 2}
            frames = [observation(100.1), observation(100.2)]
            (clip / "manifest.json").write_text(json.dumps(m))
            (clip / "events.jsonl").write_text("".join(json.dumps(f) + "\n" for f in frames))
            replay = root / ("path-test-" + replay_id + ".json")
            replay.write_text("{}")
            archive = root / ("frames-" + replay_id)
            archive.mkdir()
            (archive / "manifest.json").write_text("{}")
            labels, connections = root / "labels.json", root / "connections.json"
            labels.write_text("{}")
            connections.write_text("{}")
            sidecar = root / "sidecars" / "demo-clip.json"
            recovered = [dict(index=i, generation=f["generation"], captureTimestamp=f["captureTimestamp"],
                              hackerPathVersion=1, roadHint=dict(dx=1, dy=0, clearance=.1, distance=.2, frontier=True),
                              pathProgress=.1, pathAlignment=-.2) for i, f in enumerate(frames)]
            before = (clip / "events.jsonl").read_bytes()
            with patch.object(dataset.subprocess, "run", return_value=SimpleNamespace(stdout=json.dumps(recovered).encode())):
                evidence = dataset.recover_exit_path("/vision", clip, root, labels, connections, (3., 4.), sidecar)
            self.assertEqual(json.loads(sidecar.read_text()), evidence)
            from train_bot_job import snapshot_exit_path_evidence
            snapshot = root / "run-evidence"
            snapshot.mkdir()
            snapshot_exit_path_evidence(clip, sidecar, snapshot, root)
            self.assertEqual((snapshot / sidecar.name).read_bytes(), sidecar.read_bytes())
            (archive / "manifest.json").write_text('{"changed":true}')
            with self.assertRaisesRegex(ValueError, "archiveManifestSHA256"):
                snapshot_exit_path_evidence(clip, sidecar, snapshot, root)
            (archive / "manifest.json").write_text("{}")
            self.assertEqual((clip / "events.jsonl").read_bytes(), before)
            empty = root / "empty-evidence"
            empty.mkdir()
            legacy_rows, legacy_report = dataset.collect(clip, exit_path_evidence=empty)
            self.assertFalse(legacy_report["skipped"])
            self.assertEqual(legacy_rows[0]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_present"):-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_dy") + 1], [0, 0, 0])
            rows, report = dataset.collect(clip, exit_path_evidence=sidecar.parent)
            self.assertFalse(report["skipped"])
            self.assertEqual(rows[0]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_present"):-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("shared_path_dy") + 1], [1, 1, 0])
            native = copy.deepcopy(frames)
            native[0].update(hackerPathVersion=1, roadHint=dict(dx=-1, dy=0, clearance=.1, distance=.2, frontier=True), pathProgress=0, pathAlignment=0)
            dataset.apply_exit_path_evidence(evidence, clip, m, native)
            self.assertEqual(native[0]["roadHint"]["dx"], -1)
            self.assertEqual(native[1]["roadHint"]["dx"], 1)
            evidence["rows"][0]["captureTimestamp"] += 1
            sidecar.write_text(json.dumps(evidence))
            _, report = dataset.collect(clip, exit_path_evidence=sidecar.parent)
            self.assertIn("does not match", report["skipped"][0]["error"])

    def test_pairing_never_uses_future_or_stale_map_evidence(self):
        hacker = {"id": "H", "pairedClipID": "G", "botDomain": "hacker", "replayID": "H"}
        gameplay = {"id": "G", "pairedClipID": "H", "botDomain": "gameplay", "replayID": "H", "status": "completed"}
        rows = [observation(t) for t in (1, 1.1, 1.45, 2)]
        ref = [observation(1.02)]
        ref[0]["atlasView"] = {"left": .2, "right": 1}
        dataset.paired_measurements(hacker, rows, gameplay, ref)
        self.assertEqual([bool(row.get("atlasView")) for row in rows], [False, True, False, False])

    def test_door_policy_uses_only_its_domain_terrain(self):
        geometry = {"width": 240, "height": 140, "floors": [], "walls": [],
                    "doors": [{"x1": 20, "y1": 30, "x2": 50, "y2": 30}]}
        for domain, source in (("hacker", "human-authored"), ("gameplay", "detected")):
            row = observation(100.2) | {"terrainSource": source, "authoredTerrain": geometry, "detectedTerrain": None}
            samples, _ = dataset.examples(dict(manifest(), botDomain=domain), [row], [])
            self.assertEqual(samples[0]["observation"][-dataset.FEATURE_COUNT + dataset.FEATURE_NAMES.index("marking_door_count")], 1 / 16 if domain == "hacker" else 0)

    def test_door_grid_keeps_orientations_and_old_recordings(self):
        geometry = {"width": 240, "height": 140, "floors": [], "walls": [],
                    "doors": [{"x1": 20, "y1": 30, "x2": 50, "y2": 30},
                              {"x1": 100, "y1": 40, "x2": 100, "y2": 70}]}
        grid = dataset.door_grid(geometry)
        self.assertEqual(sum(grid), 8)
        self.assertEqual(sum(dataset.door_grid(geometry | {"doors": None})), 0)
        self.assertEqual(dataset.SCHEMAS[10], dataset.DOOR_FEATURE_NAMES[:-dataset.GRID_CELLS])

    def test_current_policy_retires_joni_and_preserves_cocoon_and_geo(self):
        frames = [observation(100.1 + i * .1) for i in range(3)]
        for row in frames:
            row["features"][:2] = [.5, .25]
        frames[0]["joniBlessings"] = [dict(x=.9, y=.25, width=.03, height=.03)]
        frames[1]["lifebloodCocoons"] = [dict(x=.75, y=.5, width=.04, height=.06)]
        frames[2]["lifebloodCocoons"] = []
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(dataset.SCHEMAS[8], dataset.COCOON_NAMES[:-6])
        self.assertEqual(dataset.SCHEMAS[9], dataset.COCOON_NAMES[:-3])
        self.assertFalse(any(name.startswith("joni_") for name in dataset.FEATURE_NAMES))
        old, _ = dataset.examples(manifest(), frames, [], policy_version=12)
        self.assertEqual(old[0]["observation"][-1202:][len(dataset.PICKUP_NAMES)-3:len(dataset.PICKUP_NAMES)], [.4, 0, 1])
        self.assertEqual([row["observation"][-dataset.FEATURE_COUNT:][dataset.FEATURE_NAMES.index("lifeblood_cocoon_dx"):dataset.FEATURE_NAMES.index("lifeblood_cocoon_present") + 1] for row in rows], [[0, 0, 0], [.25, .25, 1], [0, 0, 0]])
        self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT:][dataset.FEATURE_NAMES.index("geo_cache_dx"):dataset.FEATURE_NAMES.index("geo_cache_present") + 1], [0, 0, 0])

    def test_geo_features_preserve_legacy_policy_and_use_nearest_recorded_cache(self):
        frames = [observation(100.1 + i * 0.1) for i in range(3)]
        for frame in frames:
            frame["features"][:2] = [.5, .25]
        frames[1]["geoCaches"] = [{"x": x, "y": .25, "width": .05, "height": .05} for x in (.1, .75)]
        rows, _ = dataset.examples(manifest(), frames, [])
        self.assertEqual(rows[0]["observation"][-dataset.FEATURE_COUNT:][dataset.FEATURE_NAMES.index("geo_cache_dx"):dataset.FEATURE_NAMES.index("geo_cache_present") + 1], [0, 0, 0])
        self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT:][dataset.FEATURE_NAMES.index("geo_cache_dx"):dataset.FEATURE_NAMES.index("geo_cache_present") + 1], [.25, 0, 1])
        self.assertEqual(rows[2]["observation"][-dataset.FEATURE_COUNT:][dataset.FEATURE_NAMES.index("geo_cache_dx"):dataset.FEATURE_NAMES.index("geo_cache_present") + 1], [0, 0, 0])
        self.assertEqual(dataset.SCHEMAS[4], dataset.SHARED_PATH_NAMES[:dataset.GEO_START])

    def test_blockade_only_enters_history_when_observed_and_keeps_the_recorded_attack_label(self):
        frames = [observation(100.1 + i * 0.1) for i in range(4)]
        frames[2]["blockade"] = {"dx": -.02, "dy": .01, "mark": {"direction": -1, "crossed": False}}
        rows, _ = dataset.examples(manifest(), frames, [event(100.33, "x"), event(100.36, "x", False)])
        width = dataset.FEATURE_COUNT
        self.assertEqual(rows[1]["observation"][-width+13:-width+16], [0, 0, 0])
        self.assertEqual(rows[2]["observation"][-width+13:-width+16], [-.02, .01, -1])
        self.assertEqual(rows[2]["action"][3], 1)
        self.assertEqual(rows[3]["observation"][-2*width+13:-2*width+16], [-.02, .01, -1])
        self.assertEqual(rows[3]["observation"][-width+13:-width+16], [0, 0, 0])
        self.assertEqual(dataset.SCHEMAS[3][13:16], ["target_x", "target_y", "target_valid"])

    def test_legacy_pixel_walls_are_excluded_only_from_hacker_training(self):
        sample = observation(100.2)
        wall = [1, -.1, -.2, .2, 0, 1, 0, 0, 0, 0, 0]
        sample["features"][-11:] = wall
        for domain, source, geometry, expected in [
                ("hacker", "human-authored", None, [0] * 11),
                ("hacker", "human-authored", {"width": 100, "height": 80, "floors": [],
                 "walls": [{"x": 25, "bottomY": 0, "topY": 40, "solidSide": 0}]}, wall),
                ("gameplay", "detected", None, wall)]:
            row = dict(sample, terrainSource=source, authoredTerrain=geometry)
            result, issues = dataset.examples(dict(manifest(), botDomain=domain), [row], [], policy_version=12)
            self.assertFalse(issues)
            self.assertEqual(result[0]["observation"][-1202:][144:155], expected)

    def test_compact_markings_keep_closest_segments_through_long_occlusions(self):
        geometry = {"width": 240, "height": 140,
                    "floors": [dict(minimumX=10*i+1, maximumX=10*i+4, y=115) for i in range(20)],
                    "walls": [dict(x=x, bottomY=115, topY=135) for x in (5, 15, 25, 195)]}
        for domain in ("hacker", "gameplay"):
            hacker = domain == "hacker"
            records = [observation(100.1 + i/10) for i in range(30)]
            for row in records:
                row.update(terrainSource="human-authored" if hacker else "detected",
                           authoredTerrain=geometry if hacker else None, detectedTerrain=None if hacker else geometry)
                if not hacker:
                    row["features"][:5] = [0] * 5
                    row["issues"] = ["missing-knight"]
            rows, rejected = dataset.examples(dict(manifest(), botDomain=domain), records,
                [event(100, "right"), event(102, "z")], compact=True)
            self.assertFalse(rejected)
            self.assertEqual(len(rows), 30)
            last = rows[-1]["observation"][-dataset.FEATURE_COUNT:]
            self.assertEqual(last[dataset.FEATURE_NAMES.index("marking_ground_count")], 20 / 16)
            self.assertEqual(last[dataset.FEATURE_NAMES.index("marking_wall_count")], 4 / 16)
            kinds = last[dataset.MARKING_START + 4::6]
            self.assertEqual(sum(abs(kind - .33) < 1e-6 for kind in kinds), 13)
            self.assertEqual(sum(abs(kind - .66) < 1e-6 for kind in kinds), 3)
            slots = [last[i:i + 6] for i in range(dataset.MARKING_START, dataset.FEATURE_COUNT, 6)]
            self.assertFalse(any(abs(s[4] - .33) < 1e-6 and abs(s[0] - 191/240) < 1e-6 for s in slots))
            self.assertFalse(any(abs(s[4] - .66) < 1e-6 and abs(s[0] - 195/240) < 1e-6 for s in slots))
            self.assertEqual(rows[-1]["action"], [2, 0, 1, 0])

    def test_recovery_export_uses_expert_edges_and_requires_an_occluded_start(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            rows = [observation(100.02 + i * 0.1) for i in range(20)]
            for i, row in enumerate(rows):
                row.update(terrainSource="detected", knightSource="visual" if i >= 5 else None)
                row["features"][20] = 1
                if i < 5:
                    row["issues"] = ["missing-knight"]
                    row["features"][4] = 0
            rows[0]["imagePath"] = "start.jpg"
            (root / "start.jpg").write_bytes(b"test review image")
            inputs = [event(100.224, "left"), event(100.452, "left", False)]
            m = manifest() | dict(botDomain="gameplay", imageCount=1, observationCount=20,
                                  usableObservationCount=15, inputEventCount=2)
            (root / "manifest.json").write_text(json.dumps(m))
            (root / "events.jsonl").write_text("\n".join(map(json.dumps, rows + inputs)))
            plan = dataset.recovery_maneuver(root)
            self.assertEqual([v["values"][0] for v in plan["controls"]], [0, 1, 0])
            self.assertAlmostEqual(plan["controls"][2]["time"] - plan["controls"][1]["time"], 0.228)
            m["botDomain"] = "hacker"
            (root / "manifest.json").write_text(json.dumps(m))
            self.assertIsNone(dataset.recovery_maneuver(root))

    def test_control_labels_survive_a_cold_start_occlusion_and_reacquisition(self):
        def hidden(time):
            row = observation(time, issues=["missing-knight"])
            row["terrainSource"] = "detected"
            row["features"][4] = 0
            row["features"][20] = 1
            return row
        first, second = hidden(100.1), hidden(100.2)
        # Some early wall-enabled builds selected a wall relative to (0,0)
        # while the Knight was hidden. Keep the raw record, mask it for learning.
        first["features"][-11:] = [1, 0, 0, 0, 0, 0, 0.04, 0.14, 0.19, -1, 1]
        seen = observation(100.3)
        seen.update(terrainSource="detected", knightSource="visual")
        rows, rejected = dataset.examples(manifest(), [first, second, seen],
                                           [event(100.12, "left"), event(100.25, "left", False), event(100.32, "z")])
        self.assertEqual(rejected, {})
        self.assertEqual([r["segment"] for r in rows], [0, 0, 0])
        self.assertEqual(rows[0]["action"], [1, 0, 0, 0])
        self.assertEqual(rows[-1]["action"], [0, 0, 1, 0])
        self.assertEqual(rows[0]["observation"][-dataset.FEATURE_COUNT:][:5], [0] * 5)
        self.assertEqual(rows[0]["observation"][-dataset.FEATURE_COUNT:][144:155], [0] * 11)
        self.assertEqual(first["features"][-11], 1)
        expired = hidden(101.4)
        rows, _ = dataset.examples(manifest(), [first, expired, expired | {"availableAt": 101.41}], [])
        self.assertEqual(len(rows), 3)
        for change in ("stale", "hacker", "no-lines", "additional-issue"):
            bad = hidden(100.1)
            if change == "stale": bad["detectionTimestamp"] = 98
            if change == "hacker": bad["terrainSource"] = "human-authored"
            if change == "no-lines": bad["features"][20] = 0
            if change == "additional-issue": bad["issues"].append("room-transition")
            self.assertEqual(dataset.examples(manifest(), [bad], [])[0], [], change)

    def test_short_taps_and_simultaneous_actions_survive(self):
        timeline = dataset.ActionTimeline([event(100, "right"), event(100.04, "z"),
                                           event(100.06, "z", False), event(100.08, "x")])
        self.assertEqual(timeline.action_during(100.01, 100.11), ([2, 0, 1, 1], None))
        self.assertEqual(timeline.action_during(100.12, 100.22), ([2, 0, 0, 1], None))

    def test_sustained_attack_and_fresh_taps_have_distinct_training_evidence(self):
        import numpy as np
        events = [event(100, "right"), event(100.12, "x"), event(100.23, "x"),
                  event(100.31, "x", False), event(100.32, "x"), event(100.34, "x", False),
                  event(100.44, "x")]
        times = [100.1, 100.2, 100.3, 100.4, 100.5, 100.6]
        rows, _ = dataset.examples(manifest(), [observation(t, room=2 if t == times[-1] else 1) for t in times], events)
        held = np.array([r["continuedAttack"] for r in rows])
        raw = np.array([r["action"] for r in rows])
        self.assertEqual(held.tolist(), [False, True, False, False, True, False])
        targets, _ = dataset.demonstration_controls(dict(actions=raw, continued_attacks=held))
        self.assertEqual(targets[:, 3].tolist(), [1, 0, 1, 1, 0, 1])
        self.assertTrue((raw[:, 3] == 1).all())
        self.assertTrue((targets[:, :3] == raw[:, :3]).all())
        self.assertTrue((dataset.demonstration_controls(dict(actions=raw))[0] == raw).all())

    def test_conflicts_and_unsupported_actions_are_excluded(self):
        for events, expected in [([event(100, "a"), event(100.04, "a", False)], "unsupported-input"),
                                 ([event(100, "right"), event(100.04, "left")], "conflicting-directions")]:
            self.assertEqual(dataset.ActionTimeline(events).action_during(99.99, 100.1)[1], expected)

    def test_history_and_previous_actions_reset_at_rooms_gaps_and_bad_rows(self):
        observations = [observation(100.1), observation(100.2), observation(100.3, room=2),
                        observation(101.0, room=2), observation(101.1, issues=["room-transition"]),
                        observation(101.2, room=2)]
        rows, rejected = dataset.examples(manifest(), observations, [event(100, "right")])
        self.assertEqual(len(rows), 5)
        self.assertEqual([x["segment"] for x in rows], [0, 0, 1, 2, 3])
        self.assertEqual(rows[0]["observation"][:3 * dataset.FEATURE_COUNT], [0] * (3 * dataset.FEATURE_COUNT))
        self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT + 9], 1)  # previous right
        self.assertEqual(rows[2]["observation"][-dataset.FEATURE_COUNT + 9], 0)
        self.assertEqual(rows[2]["observation"][-dataset.FEATURE_COUNT + 8], 1)
        self.assertEqual(rejected, {"room-transition": 1})

    def test_ages_are_checked_independently_and_last_partial_window_is_dropped(self):
        old = observation(100.1)
        old["detectionTimestamp"] = 99
        future = observation(100.2)
        future["detectionTimestamp"] = 101
        slow = observation(100.3)
        slow["capturedAt"] = 99
        rows, rejected = dataset.examples(manifest(), [old, future, slow, observation(104.95)], [])
        self.assertEqual(rows, [])
        self.assertEqual(rejected["stale-or-missing-detection"], 2)
        self.assertEqual(rejected["stale-capture"], 1)
        self.assertEqual(rejected["incomplete-action-window"], 1)

    def test_export_never_uses_future_controls_as_previous_action(self):
        rows, _ = dataset.examples(manifest(), [observation(100.1), observation(100.2)],
                                   [event(100.25, "z")])
        self.assertEqual(rows[1]["action"][2], 1)
        self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT + 11], 0)

    def prediction_pair(self):
        seen = observation(100.1)
        seen["knightSource"] = "visual"
        predicted = observation(100.43)
        predicted.update(knightSource="visual-predicted", terrainSource="detected", detectionTimestamp=None,
                         visualContext={"frameWidth": 640, "frameHeight": 0.5625, "cameraX": 0, "cameraY": 0},
                         knightPrediction={"lastSeenAt": seen["detectionTimestamp"],
                                           "age": predicted["captureTimestamp"] - seen["detectionTimestamp"],
                                           "uncertainty": 0.02, "cameraCompensated": True,
                                           "observedIssues": ["missing-knight"]})
        predicted["features"][4] = 0.35
        return seen, predicted

    def test_bounded_prediction_keeps_expert_action_history_continuous(self):
        seen, predicted = self.prediction_pair()
        rows, rejected = dataset.examples(manifest(), [seen, predicted], [event(100, "right")])
        self.assertEqual(len(rows), 2)
        self.assertEqual(rejected, {})
        self.assertEqual(rows[0]["segment"], rows[1]["segment"])
        self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT + 9], 1)

    def test_predictions_require_a_real_in_session_anchor_and_bounded_evidence(self):
        seen, valid = self.prediction_pair()
        rows, rejected = dataset.examples(manifest(), [valid], [])
        self.assertEqual(rows, [])
        self.assertIn("invalid-knight-prediction", rejected)
        for key, value in [("age", 0.7), ("lastSeenAt", 99), ("uncertainty", 0.061),
                           ("cameraCompensated", False), ("observedIssues", ["room-transition"])]:
            bad = copy.deepcopy(valid); bad["knightPrediction"][key] = value
            rows, rejected = dataset.examples(manifest(), [seen, bad], [])
            self.assertEqual(len(rows), 1, (key, value))
            self.assertIn("invalid-knight-prediction", rejected)
        bad = copy.deepcopy(valid); bad["roomID"] = 2
        rows, rejected = dataset.examples(manifest(), [seen, bad], [])
        self.assertEqual(len(rows), 1)
        self.assertIn("invalid-knight-prediction", rejected)

    def test_prediction_cannot_override_stale_capture_or_missing_terrain(self):
        seen, valid = self.prediction_pair()
        for stale_capture in (True, False):
            bad = copy.deepcopy(valid)
            if stale_capture:
                bad["capturedAt"] = 99
            else:
                bad["features"][6] = 0
            rows, rejected = dataset.examples(manifest(), [seen, bad], [])
            self.assertEqual(len(rows), 1)
            self.assertIn("stale-capture" if stale_capture else "missing-perception", rejected)
        late = copy.deepcopy(valid)
        late["capturedAt"] = late["availableAt"] - 0.3
        rows, rejected = dataset.examples(manifest(), [seen, late], [])
        self.assertEqual(len(rows), 1)
        self.assertIn("invalid-knight-prediction", rejected)

    def test_previous_action_cannot_leak_overlapping_or_delayed_targets(self):
        for delay in [0.0, 0.2]:
            rows, _ = dataset.examples(manifest(), [observation(100.1), observation(100.195)],
                                       [event(100.198, "z")], action_delay=delay)
            self.assertEqual(rows[1]["observation"][-dataset.FEATURE_COUNT + 11], 0)

    def test_schema_corruption_and_unfinished_sessions_fail_closed(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            m = manifest()
            m["observationCount"] = m["usableObservationCount"] = 1
            sample = observation(100.1)
            (root / "manifest.json").write_text(json.dumps(m))
            (root / "events.jsonl").write_text(json.dumps(sample) + "\n")
            self.assertEqual(len(dataset.read_session(root)[1]), 1)
            for change in [{"status": "recording"}, {"featureNames": []}, {"observationCount": 9}]:
                (root / "manifest.json").write_text(json.dumps({**m, **change}))
                with self.assertRaises(ValueError):
                    dataset.read_session(root)
            (root / "manifest.json").write_text(json.dumps(m))
            sample["features"][0] = float("nan")
            (root / "events.jsonl").write_text(json.dumps(sample) + "\n")
            with self.assertRaises(ValueError):
                dataset.read_session(root)

    def test_exact_replay_upgrades_a_legacy_row_and_keeps_unmatched_rows_unknown(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            m = manifest()
            m.update(schemaVersion=1, featureNames=dataset.LEGACY_FEATURE_NAMES,
                     observationCount=2, usableObservationCount=2)
            samples = [observation(100.1), observation(100.2)]
            for sample in samples:
                sample["features"] = sample["features"][:len(dataset.LEGACY_FEATURE_NAMES)]
            (root / "manifest.json").write_text(json.dumps(m))
            events = root / "events.jsonl"
            events.write_text("".join(json.dumps(row) + "\n" for row in samples))
            recovered = root / "evidence"
            recovered.mkdir()
            wall = [1, -0.1, -0.2, 0.1, -1, 1, 0.05, -0.1, 0.2, 1, 1]
            sidecar = {"id": m["id"], "eventsSHA256": hashlib.sha256(events.read_bytes()).hexdigest(),
                       "rows": [{"generation": 1, "captureTimestamp": samples[0]["captureTimestamp"], "features": wall}]}
            sidecar_path = recovered / (root.name + ".json")
            sidecar_path.write_text(json.dumps(sidecar))
            rows, report = dataset.collect(root, wall_evidence=recovered, policy_version=12)
            self.assertEqual(report["sessions"][0]["wallPresentExamples"], 1)
            self.assertEqual(rows[0]["observation"][-1202:][144:155], wall)
            self.assertEqual(rows[1]["observation"][-1202:][144:155], [0] * 11)
            width = 1202
            self.assertEqual(rows[1]["observation"][-2 * width:][144:155], wall)
            sidecar["rows"][0]["generation"] = 2
            sidecar_path.write_text(json.dumps(sidecar))
            self.assertTrue(dataset.collect(root, wall_evidence=recovered, policy_version=12)[1]["skipped"])

    def test_review_image_paths_cannot_escape_session(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            m = manifest()
            m["observationCount"] = m["usableObservationCount"] = m["imageCount"] = 1
            (root / "manifest.json").write_text(json.dumps(m))
            sample = observation(100.1)
            sample["imagePath"] = "../outside.jpg"
            (root / "events.jsonl").write_text(json.dumps(sample) + "\n")
            with self.assertRaises(ValueError):
                dataset.read_session(root)


if __name__ == "__main__":
    unittest.main()
