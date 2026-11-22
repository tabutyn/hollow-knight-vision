#!/usr/bin/env python3
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    'ground_detection_score', Path(__file__).with_name('score-ground-detection.py'))
scorer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scorer)


class GroundDetectionScoreTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        (self.root / 'frames').mkdir()
        self.reference_path = self.root / 'reference.json'
        self.audit_path = self.root / 'audit.json'
        self.labels_path = self.root / 'labels.json'
        self.identity = '00000000-0000-0000-0000-000000000001'
        self.image = {'index': 0, 'captureTimestamp': 10, 'capturedAt': 10,
                      'renderedGameFrame': 123, 'fileName': '00000000.png',
                      'sha256': 'checked-image-hash', 'width': 640, 'height': 360}
        self.reference = {
            'id': self.identity, 'visualFrameArchive': {'directoryName': 'frames'},
            'coarseMotionSamples': [{'offset': 0}],
            'trackingSamples': [{'publishedCameraX': 999999, 'publishedCameraY': -999999}],
            'groundTruthSamples': [{'sample': {'unityFrame': 123, 'sessionID': 'reference',
                'sceneName': 'Tutorial_01', 'cameraAvailable': True, 'cameraX': 0, 'cameraY': 0,
                'pixelsPerWorldUnitX': 20, 'pixelsPerWorldUnitY': 20,
                'projectionPixelWidth': 640, 'projectionPixelHeight': 360}}],
        }
        self.labels = {'lines': [{'sceneName': 'Tutorial_01', 'kind': 'positive',
                                 'minimumWorldX': -10, 'maximumWorldX': 10, 'worldY': -5}]}
        self.row = {'index': 0, 'captureTimestamp': 10, 'renderedGameFrame': 123,
                    'imageSHA256': 'checked-image-hash', 'width': 640, 'height': 360,
                    'exclusions': [[300, 250, 40, 60]], 'detectorMilliseconds': 2,
                    'semanticLines': [{'row': 280, 'x0': 120, 'x1': 519, 'evidence': [[120, 519]]}],
                    'trackingLines': [{'row': 280, 'x0': 120, 'x1': 519, 'evidence': [[120, 519]]}]}

    def write(self, rows=None, images=None):
        self.reference_path.write_text(json.dumps(self.reference))
        self.labels_path.write_text(json.dumps(self.labels))
        (self.root / 'frames/manifest.json').write_text(json.dumps(
            {'pathID': self.identity, 'frames': images or [self.image]}))
        rows = rows or [self.row]
        self.audit_path.write_text(json.dumps({
            'sourcePathID': self.identity,
            'recordingSHA256': hashlib.sha256(self.reference_path.read_bytes()).hexdigest(),
            'selectedFrames': len(rows), 'processedFrames': len(rows), 'frameStride': 1,
            'missingFreshMaskInputs': 0, 'exactMaskInputs': len(rows), 'frames': rows}))

    def result(self):
        return scorer.score(self.reference_path, self.audit_path, self.labels_path)

    def test_hacker_projection_ignores_wrong_visual_pose_and_excludes_masks(self):
        self.write()
        report = self.result()
        self.assertEqual(report['coverage']['scoredFrames'], 1)
        self.assertEqual(report['scores']['semanticSpans']['truePositivePixelLength'], 360)
        self.assertEqual(report['scores']['semanticSpans']['precision'], 1)
        self.assertEqual(report['scores']['semanticSpans']['recall'], 1)

    def test_close_duplicate_rows_cannot_both_claim_one_ground_line(self):
        self.row['semanticLines'].append({'row': 282, 'x0': 120, 'x1': 519, 'evidence': [[120, 519]]})
        self.write()
        result = self.result()['scores']['semanticSpans']
        self.assertEqual(result['truePositivePixelLength'], 360)
        self.assertEqual(result['falsePositivePixelLength'], 360)
        self.assertEqual(result['precision'], .5)
        self.assertEqual(result['recall'], 1)

    def test_missing_telemetry_is_reported_rather_than_scored_as_correct(self):
        image = dict(self.image, index=1, captureTimestamp=11, capturedAt=11, renderedGameFrame=124)
        row = dict(self.row, index=1, captureTimestamp=11, renderedGameFrame=124)
        self.write(rows=[self.row, row], images=[self.image, image])
        report = self.result()
        self.assertEqual(report['coverage']['processedFrames'], 2)
        self.assertEqual(report['coverage']['scoredFrames'], 1)
        self.assertEqual(report['coverage']['rejected'], {'missingTelemetry': 1})

    def test_inferred_bridge_across_a_real_gap_counts_as_false_ground(self):
        self.labels['lines'] = [
            dict(self.labels['lines'][0], maximumWorldX=-5),
            dict(self.labels['lines'][0], minimumWorldX=5),
        ]
        self.row['semanticLines'][0]['evidence'] = [[120, 219], [420, 519]]
        self.write()
        scores = self.result()['scores']
        self.assertEqual(scores['semanticSpans']['truePositivePixelLength'], 200)
        self.assertEqual(scores['semanticSpans']['falsePositivePixelLength'], 160)
        self.assertEqual(scores['semanticEvidence']['precision'], 1)
        self.assertEqual(scores['semanticEvidence']['recall'], 1)

    def test_wall_projection_and_one_to_one_authored_corners(self):
        self.labels['lines'][0]['startCornerID'] = 'shared'
        self.labels['walls'] = [{'sceneName': 'Tutorial_01', 'minimumWorldX': -10,
                                 'worldY': -5, 'endWorldY': -7,
                                 'startCornerID': 'shared', 'endCornerID': 'free'}]
        self.row['walls'] = [[121, 280, 319]]
        self.row['corners'] = [[121, 280], [119, 281]]
        self.write()
        result = self.result()['terrain']
        self.assertEqual(result['wallSpans']['truePositivePixelLength'], 40)
        self.assertEqual(result['corners']['matched'], 1)
        self.assertEqual(result['corners']['unmatchedPredictions'], 1)
        self.assertEqual(result['corners']['unmatchedReferences'], 0)

    def test_changed_image_association_is_rejected(self):
        self.row['imageSHA256'] = 'another-image'
        self.write()
        with self.assertRaisesRegex(ValueError, 'association changed'):
            self.result()


if __name__ == '__main__':
    unittest.main()
