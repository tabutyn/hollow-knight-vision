import copy
import json
import pathlib
import runpy
import tempfile
import unittest

from camera_diagnostics import (
    exact_frames,
    low_resolution_motion_demand,
    summarize_exact,
)

paired = runpy.run_path(str(pathlib.Path(__file__).with_name('diagnose-ground-camera.py')))


def recording(camera_errors=(0, 0, 0)):
    tracks, truth = [], []
    for i, error in enumerate(camera_errors):
        tracks.append({'captureTimestamp': 100+i/60, 'offset': i/60+.2,
                       'captureOffset': i/60, 'publishedCameraX': 50+i*10+error,
                       'publishedCameraY': 20, 'cameraX': -999, 'cameraY': -999,
                       'poseVerified': True, 'renderedGameFrame': 100+i, 'roomID': 0})
        truth.append({'offset': i/60+5, 'sample': {'unityFrame': 100+i, 'sessionID': 'a',
                       'sceneName': 'scene', 'cameraAvailable': True,
                       'cameraX': 10+i, 'cameraY': 8, 'projectionPixelWidth': 640,
                       'projectionPixelHeight': 360, 'pixelsPerWorldUnitX': 10,
                       'pixelsPerWorldUnitY': 10}})
    return {'trackingSamples': tracks, 'groundTruthSamples': truth}


class ExactCameraTests(unittest.TestCase):
    def test_receipt_delay_has_no_effect_and_published_pose_is_used(self):
        run = recording()
        report = summarize_exact(run)
        self.assertEqual(report['errorPixels']['max'], 0)
        self.assertEqual(report['matchedSamples'], 3)

    def test_correction_is_not_reanchored_away(self):
        run = recording((0, 16, 32))
        run['trackingSamples'][1]['globalCorrectionX'] = 16
        report = summarize_exact(run)
        self.assertEqual(report['endErrorXY'], [32, 0])
        self.assertEqual(len(report['epochs']), 1)
        self.assertAlmostEqual(report['verifiedErrorOver16Percent'], 100/3)

    def test_missing_marker_and_missing_telemetry_are_not_time_matched(self):
        run = recording()
        run['trackingSamples'][1].pop('renderedGameFrame')
        run['groundTruthSamples'].pop()
        frames, coverage = exact_frames(run)
        self.assertEqual(len(frames), 1)
        self.assertEqual(coverage['rejected'], {'missingMarker': 1, 'missingTelemetry': 1})

    def test_private_candidate_is_not_substituted(self):
        run = recording()
        run['trackingSamples'][1]['publishedCameraX'] = None
        self.assertEqual(exact_frames(run)[1]['rejected'], {'publishedPoseUnavailable': 1})

    def test_24_bit_collision_is_rejected(self):
        run = recording()
        extra = copy.deepcopy(run['groundTruthSamples'][1])
        extra['sample']['unityFrame'] += 1 << 24
        run['groundTruthSamples'].append(extra)
        self.assertEqual(exact_frames(run)[1]['rejected'], {'ambiguousTelemetry': 1})

    def test_duplicate_identical_receipt_is_allowed(self):
        run = recording()
        run['groundTruthSamples'].append(copy.deepcopy(run['groundTruthSamples'][1]))
        self.assertEqual(exact_frames(run)[1]['matchedSamples'], 3)

    def test_scene_change_starts_explicit_new_epoch(self):
        run = recording((0, 1, 99))
        run['groundTruthSamples'][2]['sample']['sceneName'] = 'different'
        report = summarize_exact(run)
        self.assertEqual(len(report['epochs']), 2)
        self.assertEqual(report['endErrorXY'], [0, 0])

    def test_roundoff_scale_noise_does_not_reset_epoch(self):
        run = recording((0, 1, 2))
        run['groundTruthSamples'][1]['sample']['pixelsPerWorldUnitX'] += .00001
        self.assertEqual(len(exact_frames(run)[1]['epochs']), 1)

    def test_zoom_starts_explicit_new_epoch(self):
        run = recording()
        run['groundTruthSamples'][2]['sample']['pixelsPerWorldUnitX'] = 20
        self.assertEqual(len(exact_frames(run)[1]['epochs']), 2)

    def test_unverified_output_remains_in_accuracy_metrics(self):
        run = recording((0, 0, 70))
        run['trackingSamples'][2]['poseVerified'] = False
        report = summarize_exact(run)
        self.assertEqual(report['errorPixels']['max'], 70)
        self.assertEqual(report['verifiedErrorPixels']['max'], 0)

    def test_missing_frame_interval_does_not_inflate_error_duration(self):
        run = recording((0, 30, 30))
        run['trackingSamples'][2]['captureTimestamp'] += 10
        run['trackingSamples'][2]['captureOffset'] += 10
        spans = summarize_exact(run)['errorOver16Spans']
        self.assertEqual(len(spans), 2)
        self.assertEqual(sum(s['observedSpanSeconds'] for s in spans), 0)
        gap = summarize_exact(run)['processedCaptureGapsOver100ms'][0]
        self.assertGreater(gap['intervalSeconds'], 10)

    def test_verified_candidate_is_not_called_an_accepted_ground_output(self):
        run = recording((0, 0, 70))
        run['trackingSamples'][2]['poseSource'] = 'motionBridge'
        report = summarize_exact(run)
        self.assertEqual(report['verifiedErrorPixels']['max'], 70)
        self.assertIsNone(report['verifiedGroundOutputErrorPixels']['max'])


class LowResolutionMotionDemandTests(unittest.TestCase):
    @staticmethod
    def truth(offset, camera_x, scene='Tutorial_01'):
        return {'offset': offset, 'sample': {
            'sessionID': 'a', 'sceneName': scene, 'cameraAvailable': True,
            'cameraX': camera_x, 'cameraY': 0,
            'projectionPixelWidth': 640, 'projectionPixelHeight': 360,
            'pixelsPerWorldUnitX': 10, 'pixelsPerWorldUnitY': 10,
        }}

    def test_reports_short_baseline_search_coverage(self):
        run = {'groundTruthSamples': [
            self.truth(0, 0), self.truth(.05, 1), self.truth(.10, 2),
        ]}
        report = low_resolution_motion_demand(run)
        self.assertEqual(report['samples'], 2)
        self.assertEqual(report['representablePercent'], 100)
        self.assertAlmostEqual(report['horizontalCells']['p50'], 1)
        self.assertEqual(report['horizontalCells']['outsideSearchPercent'], 0)

    def test_scene_change_is_not_mistaken_for_motion_or_loop_closure(self):
        run = {'groundTruthSamples': [
            self.truth(0, 0), self.truth(.05, 1),
            self.truth(.10, 100, scene='Town'),
            self.truth(.15, 101, scene='Town'),
        ]}
        report = low_resolution_motion_demand(run)
        self.assertEqual(report['samples'], 2)
        self.assertEqual(report['horizontalCells']['max'], 1)
        self.assertEqual(report['peakHorizontal']['sceneName'], 'Tutorial_01')

    def test_reports_motion_outside_horizontal_bound(self):
        run = {'groundTruthSamples': [self.truth(0, 0), self.truth(.05, 10)]}
        report = low_resolution_motion_demand(run)
        self.assertEqual(report['representablePercent'], 0)
        self.assertEqual(report['horizontalCells']['outsideSearchPercent'], 100)

    def test_splits_ground_unverified_demand_by_exact_rendered_frame(self):
        truth = [
            self.truth(0, 0), self.truth(.05, 1), self.truth(.10, 11),
        ]
        for frame, recorded in enumerate(truth, start=10):
            recorded['sample']['unityFrame'] = frame
        run = {
            'groundTruthSamples': truth,
            'trackingSamples': [
                {
                    'renderedGameFrame': 11,
                    'poseVerified': False,
                    'hasConfirmedGround': False,
                },
                {
                    'renderedGameFrame': 12,
                    'poseVerified': True,
                    'hasConfirmedGround': True,
                },
            ],
        }

        report = low_resolution_motion_demand(run)

        self.assertEqual(report['groundReliabilityJoin'], {
            'matchedSamples': 2,
            'unmatchedSamples': 0,
        })
        self.assertEqual(report['groundUnverifiedDemand']['samples'], 1)
        self.assertEqual(
            report['groundUnverifiedDemand']['representablePercent'], 100
        )
        self.assertEqual(report['groundReliableDemand']['samples'], 1)
        self.assertEqual(
            report['groundReliableDemand']['representablePercent'], 0
        )


class PairedFloorTests(unittest.TestCase):
    def test_full_audit_join_separates_detection_pose_and_persistence(self):
        run = recording((0, 20))
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            audit = root / 'audit'
            audit.mkdir()
            replay, labels = root / 'replay.json', root / 'labels.json'
            replay.write_text(json.dumps(run))
            labels.write_text(json.dumps({'lines': [{'kind': 'positive', 'sceneName': 'scene',
                              'minimumWorldX': 5, 'maximumWorldX': 15, 'worldY': 18}]}))
            for i, tracking in enumerate(run['trackingSamples']):
                (audit / f'{i}.json').write_text(json.dumps({
                    'timestamp': tracking['captureTimestamp'], 'width': 640, 'height': 360,
                    'lines': [{'x0': 270-i*10, 'x1': 369-i*10, 'row': 80}],
                    'atlasLines': [{'id': 1, 'x0': 320, 'x1': 420, 'y': 300}]}))
            report, _, _ = paired['diagnose'](audit, replay, labels)
            self.assertEqual(report['scoredFrames'], 2)
            self.assertEqual(report['scores']['hackerObservations']['f1'], 1)
            self.assertAlmostEqual(report['scores']['gameplayObservations']['f1'], .9)
            self.assertEqual(report['scores']['persistentGameplay']['f1'], 1)
            run['trackingSamples'][1].pop('renderedGameFrame')
            replay.write_text(json.dumps(run))
            report, _, _ = paired['diagnose'](audit, replay, labels)
            self.assertEqual(report['scoredFrames'], 1)
            self.assertEqual(report['auditRejected']['auditWithoutUniqueExactTrackingSample'], 1)

    def test_camera_translation_alone_changes_floor_accuracy(self):
        lines = [(0, 100, 80)]
        changed = paired['paired_observations'](lines, {'error': [20, 0]})
        self.assertEqual(paired['score'](lines, lines), [100, 0, 0])
        self.assertEqual(paired['score'](lines, changed), [80, 20, 20])

    def test_world_y_and_atlas_y_have_correct_sign(self):
        moved = paired['paired_observations']([(0, 100, 80)], {'error': [0, 20]})
        self.assertEqual(moved, [(0, 100, 60)])
        atlas = paired['atlas_projection']([{'x0': 50, 'x1': 150, 'y': 300}],
                                          {'expected': [50, 20]}, 360)
        self.assertEqual(atlas, [(0, 100, 80)])

    def test_masks_cut_only_intersecting_rows(self):
        lines = [(0, 100, 80), (0, 100, 100)]
        self.assertEqual(paired['visible'](lines, 640, 360, [(20, 70, 20, 20)]),
                         [(0, 20, 80), (40, 100, 80), (0, 100, 100)])

    def test_duplicate_rows_cannot_both_match_one_floor(self):
        self.assertEqual(paired['score']([(0, 100, 80)], [(0, 100, 79), (0, 100, 81)]),
                         [100, 100, 0])

    def test_overlapping_fragments_are_a_union(self):
        self.assertEqual(paired['score']([(0, 100, 80)], [(0, 60, 80), (40, 100, 80)]),
                         [100, 0, 0])

    def test_empty_frame_scores_false_detections(self):
        self.assertEqual(paired['score']([], [(0, 100, 80)]), [0, 100, 0])

    def test_displaced_prediction_inside_unknown_mask_is_ignored(self):
        lines = paired['visible']([(-20, 120, 80)], 640, 360, [(20, 70, 20, 20)])
        self.assertEqual(lines, [(0, 20, 80), (40, 120, 80)])

    def test_offscreen_shift_is_not_called_false_against_clipped_truth(self):
        truth = [(0, 640, 80)]
        shifted = paired['paired_observations'](truth, {'error': [20, 0]})
        self.assertEqual(paired['score'](truth, paired['visible'](shifted, 640, 360)),
                         [620, 0, 20])

    def test_world_projection_respects_scene_and_upward_y(self):
        labels = [{'kind': 'positive', 'sceneName': 'a', 'minimumWorldX': 5,
                   'maximumWorldX': 15, 'worldY': 10},
                  {'kind': 'positive', 'sceneName': 'b', 'minimumWorldX': 5,
                   'maximumWorldX': 15, 'worldY': 10}]
        self.assertEqual(paired['truth_lines'](labels, {'sceneName': 'a', 'cameraX': 10,
                         'cameraY': 8}, 640, 360, (10, 10)), [(270, 370, 160)])


if __name__ == '__main__':
    unittest.main()
