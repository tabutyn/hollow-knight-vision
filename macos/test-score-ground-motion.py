import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('score_ground', Path(__file__).with_name('score-ground-motion.py'))
scorer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(scorer)


class GroundMotionScoreTests(unittest.TestCase):
    def test_verified_private_candidate_cannot_substitute_for_selected_fallback(self):
        base = dict(cameraX=10, cameraY=20, poseVerified=True, hasConfirmedGround=True,
                    localTextureSupport=6, inlierCount=0, globalMatchCount=0)
        output = {'trackingSamples': [dict(base, poseSource='motionBridge'),
                                     dict(base, poseSource='ground', publishedCameraX=999)]}
        trace, selection = scorer.accepted_ground_trace(output, {})
        self.assertEqual(selection['acceptedGroundSamples'], 1)
        self.assertEqual(trace['trackingSamples'][0]['publishedCameraX'], 10)
        self.assertEqual(output['trackingSamples'][1]['publishedCameraX'], 999)

    def test_bootstrap_or_nonfinite_ground_is_explicitly_excluded(self):
        output = {'trackingSamples': [dict(poseSource='ground', poseVerified=True,
            hasConfirmedGround=True, localTextureSupport=0, inlierCount=0, globalMatchCount=0),
            dict(poseSource='ground', poseVerified=True, hasConfirmedGround=True,
                 localTextureSupport=6, inlierCount=0, globalMatchCount=0, cameraX='NaN', cameraY=0)]}
        trace, selection = scorer.accepted_ground_trace(output, {})
        self.assertEqual(selection['excluded'], {'unmeasuredGround': 1, 'rawGroundPoseUnavailable': 1})
        self.assertEqual(trace['trackingSamples'], [])

    def test_same_capture_anchor_does_not_refit_later_drift_or_room_return(self):
        truth = dict(sessionID='s', sceneName='A', projectionPixelWidth=640,
                     projectionPixelHeight=360, cameraX=10, cameraY=20)
        frame = dict(timestamp=1, unityFrame=7, published=[510, 20], truth=truth,
                     scale=[1, 1], error=[0, 0], errorPixels=0, epoch=0)
        epoch = dict(epoch=0, unityFrame=7, sceneName='A', captureTimestamp=1,
                     worldOrigin=[10, 20], publishedOrigin=[10, 20], scale=[1, 1])
        reference = {'groundTruthSamples': [{'sample': dict(truth, unityFrame=7)}]}
        origins, missing = scorer.same_capture_origins([frame], [epoch], reference)
        self.assertFalse(missing)
        self.assertEqual(origins[0]['publishedOrigin'], [510, 20])
        returned = dict(frame, timestamp=10, published=[560, 20])
        result, _ = scorer.relative_frames([frame, returned], {'rejected': {}}, origins)
        self.assertEqual(result[-1]['errorPixels'], 50)

    def test_missing_anchor_cannot_be_replaced_by_a_later_easy_frame(self):
        epoch = dict(unityFrame=7, sceneName='A', captureTimestamp=1)
        origins, missing = scorer.same_capture_origins([], [epoch], {})
        self.assertEqual(origins, [])
        self.assertEqual(missing, [dict(scene='A', captureTimestamp=1)])


if __name__ == '__main__':
    unittest.main()
