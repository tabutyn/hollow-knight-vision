import base64
import pathlib
import runpy
import unittest

analysis = runpy.run_path(str(pathlib.Path(__file__).with_name("analyze-input-paths.py")))


class TimingTests(unittest.TestCase):
    def test_capture_time_keeps_unverified_published_pose(self):
        path = {"trackingSamples": [{
            "offset": 1.5, "captureTimestamp": 101.0,
            "cameraX": 1, "cameraY": 2, "publishedCameraX": 80,
            "publishedCameraY": 90, "poseVerified": False,
        }], "groundTruthSamples": [{"offset": 0.1, "receivedTimestamp": 100.1}]}
        self.assertEqual(analysis["pose_samples"](path, published=True, capture_time=True), [(1.0, 80, 90)])
        self.assertEqual(analysis["pose_samples"](path, published=True, capture_time=True, verified_only=True), [])

    def test_explicit_capture_offset_works_without_ground_truth(self):
        path = {"trackingSamples": [{"offset": 1.5, "captureOffset": 1.0,
            "cameraX": 1, "cameraY": 2, "poseVerified": True}]}
        self.assertEqual(analysis["pose_samples"](path, capture_time=True), [(1.0, 1, 2)])

    def test_long_missing_interval_does_not_receive_a_nearest_pose(self):
        self.assertIsNone(analysis["nearest_pose"]([(0, 1, 2), (1, 4, 5)], 0.5))

    def test_capture_rate_coarse_trace_maps_to_exact_frame_diagnostics(self):
        path = {"coarseMotionSamples": [{
            "offset": 2.5, "captureTimestamp": 102.5,
            "renderedGameFrame": 77,
            "presentedCameraX": 45, "presentedCameraY": -8,
            "isControlling": True, "roomID": 3,
        }]}
        proxy = analysis["coarse_motion_proxy"](path)
        self.assertEqual(proxy["trackingSamples"], [{
            "offset": 2.5, "captureOffset": 2.5,
            "captureTimestamp": 102.5, "renderedGameFrame": 77,
            "publishedCameraX": 45, "publishedCameraY": -8,
            "poseSource": "coarseCaptureRate", "poseVerified": False,
            "roomID": 3, "atlasLineIDs": [],
        }])

    def test_missing_coarse_trace_has_no_proxy(self):
        self.assertIsNone(analysis["coarse_motion_proxy"]({}))

    def test_low_resolution_trace_reports_coverage_and_payload_validity(self):
        payload = base64.b64encode(bytes([1, 2, 3, 4])).decode()
        path = {"lowResolutionFrames": [
            {"offset": 0, "captureTimestamp": 100, "roomID": 0,
             "width": 2, "height": 2, "luma": payload,
             "groundTrackingReliable": True},
            {"offset": .05, "captureTimestamp": 100.05, "roomID": 0,
             "width": 2, "height": 2, "luma": payload,
             "groundTrackingReliable": False},
            {"offset": .11, "captureTimestamp": 100.11, "roomID": 1,
             "width": 2, "height": 2, "luma": payload,
             "groundTrackingReliable": False},
        ]}
        report = analysis["low_resolution_trace_summary"](path)
        self.assertEqual(report["frames"], 3)
        self.assertEqual(report["validFrames"], 3)
        self.assertEqual(report["evidenceBytes"], 12)
        self.assertAlmostEqual(report["frameRate"], 18.182, places=3)
        self.assertEqual(report["groundLossFrames"], 2)
        self.assertEqual(report["roomIDs"], [0, 1])

    def test_missing_low_resolution_trace_has_no_summary(self):
        self.assertIsNone(analysis["low_resolution_trace_summary"]({}))


class HackerConnectedGroundTruthTests(unittest.TestCase):
    @staticmethod
    def recorded(offset, scene, camera_x, hero_x, *, hero_available=True):
        return {"offset": offset, "sample": {
            "sceneName": scene,
            "cameraAvailable": True,
            "cameraX": camera_x,
            "cameraY": 4,
            "pixelsPerWorldUnitX": 1,
            "pixelsPerWorldUnitY": 1,
            "projectionPixelWidth": 640,
            "projectionPixelHeight": 360,
            "heroAvailable": hero_available,
            "heroScreenX": hero_x,
            "heroScreenY": 180,
            "velocityX": 1,
            "velocityY": 0,
            "facingRight": True,
        }}

    def test_new_scene_aligns_hero_and_reuses_anchor_on_return(self):
        path = {"groundTruthSamples": [
            self.recorded(0, "A", 10, 600),
            self.recorded(1, "A", 18, 600),
            # Camera snaps while the Knight projection is outside the frame.
            self.recorded(2, "B", -150, -100),
            self.recorded(3, "B", -100, 4),
            self.recorded(4, "B", -90, 14),
            self.recorded(5, "A", 18, 600),
        ]}

        samples = analysis["ground_truth_camera_samples"](path)

        self.assertEqual([sample[0] for sample in samples], [0, 1, 3, 4, 5])
        self.assertEqual(samples[0][1:], (0, 0))
        self.assertEqual(samples[1][1:], (8, 0))
        # Old Knight atlas X is 8 + 600. New camera begins at 608 - 4.
        self.assertEqual(samples[2][1:], (604, 0))
        self.assertEqual(samples[3][1:], (614, 0))
        self.assertEqual(samples[4][1:], (8, 0))

    def test_scene_without_hero_never_owns_truth_space(self):
        path = {"groundTruthSamples": [
            self.recorded(0, "A", 10, 300),
            self.recorded(1, "Cutscene", 20, 300, hero_available=False),
            self.recorded(2, "A", 11, 301),
        ]}

        samples = analysis["ground_truth_camera_samples"](path)

        self.assertEqual([sample[0] for sample in samples], [0, 2])
        self.assertEqual(samples[-1][1:], (1, 0))


if __name__ == "__main__":
    unittest.main()
