#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import tempfile
import json
import unittest

spec = importlib.util.spec_from_file_location('score_visual_replay', Path(__file__).with_name('score-visual-replay.py'))
scorer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scorer)


class ScreenshotErrorTests(unittest.TestCase):
    def motion_frame(self, time, expected, error, epoch=0):
        return dict(timestamp=time, offset=time, expected=expected, error=error,
                    errorPixels=sum(x*x for x in error)**.5, epoch=epoch,
                    truth={'sceneName': str(epoch)}, tracking={'poseSource': 'ground'})

    def test_windows_world_joins_uptime_and_scores_object_coordinates(self):
        frames = [self.motion_frame(120.02, [10, 0], [0, 0]),
                  self.motion_frame(120.04, [12, 0], [0, 0])]
        snapshot = {
            'observations': [
                {'id': 3, 'timestamp': 120.02, 'roomId': 7,
                 'rawPose': {'x': 12, 'y': 0}, 'optimizedPose': {'x': 10, 'y': 4}},
                {'id': 4, 'timestamp': 120.04, 'roomId': 7,
                 'optimizedPose': {'x': 12, 'y': 0}},
            ],
            'keyframes': [{'id': 1, 'observationId': 3}, {'id': 2, 'observationId': 4}],
            'loopClosureEdges': [{'id': 0, 'fromKeyframeId': 1, 'toKeyframeId': 2,
                                  'deltaX': 3, 'deltaY': 0, 'support': 9}],
        }
        result = scorer.screenshot_errors(snapshot, frames, 100, 1000, world_uses_capture_uptime=True)
        self.assertEqual(result['matched'], 2)
        self.assertEqual(result['rows'][0]['roomID'], 7)
        self.assertEqual(result['rows'][0]['errorPixels'], 4)
        self.assertEqual(result['rows'][0]['rawErrorPixels'], 2)
        self.assertEqual(scorer.loop_constraint_errors(snapshot, result)['edges'][0]['errorPixels'], 1)
        tracking = [{'captureTimestamp': 120.02, 'poseVerified': True, 'hasConfirmedGround': True},
                    {'captureTimestamp': 120.04, 'poseVerified': True, 'hasConfirmedGround': False}]
        self.assertEqual(scorer.local_measurement_availability(tracking, 2, ground_only=True)['locallyMeasuredFrames'], 1)

    def test_displacement_diagnostics_keep_fixed_offset_in_overall_scene_score(self):
        frames = [self.motion_frame(i*.05, [i, 0], [100, 20]) for i in range(31)]
        result = scorer.motion_diagnostics(frames)
        self.assertEqual(result['adjacentDisplacementErrorPixels']['max'], 0)
        self.assertEqual(result['windowDisplacementErrorPixels']['1.0']['max'], 0)
        self.assertGreater(result['scenes']['0']['fixedOriginErrorPixels']['p50'], 100)

    def test_displacement_windows_cannot_cross_missing_reference_or_scene_cut(self):
        frames = [self.motion_frame(0, [0, 0], [0, 0]),
                  self.motion_frame(.05, [100, 0], [100, 0]),
                  self.motion_frame(.3, [0, 0], [0, 0]),
                  self.motion_frame(.35, [0, 0], [999, 0], epoch=1)]
        result = scorer.motion_diagnostics(frames)
        self.assertEqual(result['adjacentPairs'], 1)
        self.assertEqual(result['largeReferenceStepsOver64Pixels'], 1)
        self.assertEqual(result['adjacentDisplacementErrorPixels']['max'], 100)
        self.assertEqual(result['windowDisplacementErrorPixels']['0.25']['count'], 0)
        self.assertEqual(result['continuousSpanCount'], 3)

    def test_missing_archive_images_remain_in_coverage_denominator(self):
        report = {'frames': 2, 'selectedFrameCount': 3, 'missingFrameIndices': [1]}
        manifest = {'frames': [0, 1, 2, 3], 'summary': {'savedFrameCount': 3, 'complete': False}}
        result = scorer.archive_coverage(report, manifest)
        self.assertFalse(result['archiveComplete'])
        self.assertEqual(result['selectedImageCoverage'], 2 / 3)
        self.assertEqual(result['missingSelectedFrameIndices'], [1])
        self.assertEqual(result['archiveFrames'], 4)

    def test_local_support_availability_keeps_missing_images_and_unmeasured_spans(self):
        samples = [dict(captureTimestamp=100+t, island={'measured': measured})
                   for t, measured in [(0, False), (.05, True), (.1, False), (.3, False), (.35, True)]]
        result = scorer.local_measurement_availability(samples, 6)
        self.assertEqual(result['locallyMeasuredFrames'], 2)
        self.assertAlmostEqual(result['locallyMeasuredSelectedFramePercent'], 100/3)
        self.assertEqual(result['unavailableImageFrames'], 1)
        self.assertEqual(result['unmeasuredOutputSpans'], 2)
        self.assertAlmostEqual(result['longestSpans'][0]['durationSeconds'], .25)
        self.assertEqual(result['captureIntervalsOver100ms']['count'], 1)

    def test_capture_identity_wins_over_delayed_wall_clock(self):
        frame = {'timestamp': 110.1, 'offset': 10.1, 'expected': [100, -50]}
        snapshot = {'observations': [{'id': 3, 'roomID': 0, 'timestamp': 99999,
            'captureTimestamp': 110.1, 'rawPose': [100, -50], 'optimizedPose': [112, -50]}]}
        result = scorer.screenshot_errors(snapshot, [frame], 100, 1000)
        self.assertEqual(result['matched'], 1)
        self.assertEqual(result['rows'][0]['errorPixels'], 12)
        modified = json.loads(json.dumps(snapshot))
        modified['observations'][0]['captureTimestamp'] += .01
        with self.assertRaisesRegex(ValueError, 'immutable'):
            scorer.validate_corrected_world(snapshot, modified)

    def test_corrected_pose_uses_fixed_reference_not_raw_capture_pose(self):
        frame = {'timestamp': 110.1, 'offset': 10.1, 'expected': [100, -50]}
        snapshot = {'observations': [{'id': 3, 'roomID': 0, 'timestamp': 1010.1,
                                     'rawPose': [900, 400], 'optimizedPose': [112, -50]}]}
        result = scorer.screenshot_errors(snapshot, [frame], 100, 1000)
        self.assertEqual(result['matched'], 1)
        self.assertEqual(result['rows'][0]['errorPixels'], 12)

    def test_nearby_receipt_time_is_not_an_exact_image_join(self):
        snapshot = {'observations': [{'id': 1, 'roomID': 0, 'timestamp': 1010.01,
                                     'optimizedPose': [10, 20]}]}
        result = scorer.screenshot_errors(snapshot,
            [{'timestamp': 110, 'offset': 10, 'expected': [10, 20]}], 100, 1000)
        self.assertEqual(result['matched'], 0)
        self.assertEqual(result['missingExactReference'], 1)
        self.assertIsNone(result['errorPixels']['p95'])

    def test_missing_telemetry_does_not_create_zero_error(self):
        result = scorer.screenshot_errors({'observations': [
            {'id': 1, 'roomID': 0, 'timestamp': 1010, 'optimizedPose': [0, 0]}]}, [], 100, 1000)
        self.assertEqual(result['matched'], 0)
        self.assertEqual(result['missingExactReference'], 1)

    def test_unknown_island_error_is_reported_without_becoming_global_success(self):
        def sample(identity, placement, measured, error):
            return {'offset': identity, 'errorPixels': error, 'tracking': {'island': {
                'id': identity, 'roomID': 0, 'measured': measured, 'placement': placement,
                'uncertainty': {}, 'breakReason': 'test'}}}
        report = scorer.island_diagnostics([
            sample(0, 'reference', True, 3), sample(1, 'estimated', True, 200),
            sample(2, 'reference', False, 40)], {'trackingIslands': []})
        self.assertEqual(report['globallyPlacedMeasuredCameraErrorPixels']['count'], 1)
        self.assertEqual(report['globallyPlacedMeasuredCameraErrorPixels']['max'], 3)
        self.assertEqual(report['estimatedOrUnmeasuredCameraErrorPixels']['count'], 2)
        self.assertEqual(report['estimatedOrUnmeasuredCameraErrorPixels']['max'], 200)

    def test_loop_constraints_use_fixed_reference_geometry(self):
        world = {'keyframes': [{'id': i, 'observationID': i} for i in range(3)],
                 'loopClosureEdges': [
                     {'id': 0, 'fromKeyframeID': 0, 'toKeyframeID': 1, 'support': 8, 'deltaX': 15, 'deltaY': -10},
                     {'id': 1, 'fromKeyframeID': 1, 'toKeyframeID': 2, 'support': 8, 'deltaX': 0, 'deltaY': 0}]}
        rows = [{'observationID': 0, 'expectedPose': [100, 40], 'offset': 1, 'referenceEpoch': 0, 'sceneName': 'A'},
                {'observationID': 1, 'expectedPose': [112, 26], 'offset': 10, 'referenceEpoch': 0, 'sceneName': 'A'},
                {'observationID': 2, 'expectedPose': [112, 26], 'offset': 20, 'referenceEpoch': 1, 'sceneName': 'B'}]
        result = scorer.loop_constraint_errors(world, {'rows': rows})
        self.assertEqual(result['errorPixels']['max'], 5)
        self.assertEqual(result['errorPixels']['count'], 1)
        self.assertEqual(result['differentReferenceFrames'], 1)
        self.assertIsNone(result['edges'][1]['errorPixels'])

    def test_correction_cannot_drop_captures_or_change_the_raw_path(self):
        original = {'observations': [{'id': 1, 'sourceObservationID': 1, 'rawPose': [100, 0],
                                     'optimizedPose': [100, 0]}]}
        corrected = json.loads(json.dumps(original))
        corrected['observations'][0]['optimizedPose'] = [0, 0]
        scorer.validate_corrected_world(original, corrected)
        corrected['observations'][0]['rawPose'] = [0, 0]
        with self.assertRaisesRegex(ValueError, 'immutable'):
            scorer.validate_corrected_world(original, corrected)
        with self.assertRaisesRegex(ValueError, 'immutable'):
            scorer.validate_corrected_world(original, {'observations': []})

    def test_correction_cannot_rewrite_captured_ground_geometry(self):
        original = {'observations': [{'id': 1, 'sourceObservationID': 1,
            'rawPose': [0, 0], 'optimizedPose': [0, 0], 'groundGeometry': {'lines': [
                {'segmentID': 2, 'start': [5, 10], 'end': [60, 10]}]}}]}
        corrected = json.loads(json.dumps(original))
        corrected['observations'][0]['optimizedPose'] = [20, 40]
        scorer.validate_corrected_world(original, corrected)
        corrected['observations'][0]['groundGeometry']['lines'][0]['start'] = [25, 50]
        with self.assertRaisesRegex(ValueError, 'immutable'):
            scorer.validate_corrected_world(original, corrected)

    def test_correction_cannot_rewrite_captured_player_bounds(self):
        original = {'observations': [{'id': 1, 'rawPose': [0, 0], 'optimizedPose': [0, 0],
                                     'observedPlayerBounds': [[100, 40], [20, 30]]}]}
        corrected = json.loads(json.dumps(original))
        corrected['observations'][0]['optimizedPose'] = [20, 40]
        scorer.validate_corrected_world(original, corrected)
        corrected['observations'][0]['observedPlayerBounds'][0][1] = 0
        with self.assertRaisesRegex(ValueError, 'immutable'):
            scorer.validate_corrected_world(original, corrected)

    def test_new_blackout_samples_cannot_move_existing_scene_reference_origin(self):
        origin = {'identity': ['session', 'room', 640, 360], 'scale': [2, 2], 'epoch': 1,
                  'publishedOrigin': [100, 50], 'worldOrigin': [10, 20]}
        def frame(time, camera, published):
            return {'timestamp': time, 'offset': time, 'scale': [2, 2], 'published': published,
                    'truth': {'sessionID': 'session', 'sceneName': 'room',
                              'projectionPixelWidth': 640, 'projectionPixelHeight': 360,
                              'cameraX': camera[0], 'cameraY': camera[1]}}
        old = frame(2, [15, 22], [113, 58])
        new_blackout = frame(1, [0, 0], [900, 900])
        coverage = {'rejected': {}, 'matchedSamples': 1}
        first, _ = scorer.fixed_origin_frames([old], coverage, [origin])
        second, _ = scorer.fixed_origin_frames([new_blackout, old], coverage, [origin])
        self.assertEqual(first[0]['errorPixels'], 5)
        self.assertEqual(first[0], second[1])
        self.assertGreater(second[0]['errorPixels'], 100)
        unknown, report = scorer.fixed_origin_frames([old], coverage, [])
        self.assertEqual(unknown, [])
        self.assertEqual(report['rejected']['missingFixedReferenceOrigin'], 1)

    def test_wrong_reference_recording_is_rejected(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root/'reference.json').write_text(json.dumps({'id': 'wrong-recording'}))
            (root/'report.json').write_text(json.dumps({'sourcePathID': 'actual-recording'}))
            with self.assertRaisesRegex(ValueError, 'does not match'):
                scorer.score(root/'reference.json', root)


if __name__ == '__main__':
    unittest.main()
