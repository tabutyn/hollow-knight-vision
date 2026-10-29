import contextlib, importlib.util, io, json, math, tempfile, unittest
from pathlib import Path
spec = importlib.util.spec_from_file_location("evaluator", Path(__file__).with_name("evaluate-replay-frames.py"))
evaluator = importlib.util.module_from_spec(spec); spec.loader.exec_module(evaluator)

class FrameEvaluationTests(unittest.TestCase):
    def run_case(self, mode):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            truth=[dict(offset=i/60, receivedTimestamp=100+i/60, sample=dict(unityFrame=i+1,sceneName="room",heroX=i,heroY=0,cameraX=i,cameraY=0,pixelsPerWorldUnitX=1,pixelsPerWorldUnitY=1,projectionPixelWidth=640,projectionPixelHeight=360)) for i in range(4)]
            tracking=[dict(offset=0, renderedGameFrame=1,publishedCameraX=100,publishedCameraY=0,poseVerified=True,poseSource="ground"),dict(offset=.04,renderedGameFrame=3,publishedCameraX=110,publishedCameraY=0,poseVerified=True,poseSource="ground")]
            trace=dict(id="test",duration=.06,events=[],groundTruthSamples=truth,trackingSamples=tracking)
            samples=[]
            for key,time,x in [(1,.001,100 if mode=="Gameplay" else .75),(3,.032,110 if mode=="Gameplay" else 5)]:
                for kind in ["capture","present"]:
                    samples.append(dict(Kind=kind,GameFrame=key,CaptureId=key,CapturedAt=100+time-.001,ObservedAt=100+time,Width=640,Height=360,CameraX=x,CameraY=0,Registered=True))
            audit=dict(startedAt=100,workspace=mode,omitted=0,samples=samples)
            for name,data in [("source",trace),("replay",trace),("audit",audit)]: (root/(name+".json")).write_text(json.dumps(data))
            with contextlib.redirect_stdout(io.StringIO()): return evaluator.evaluate(root/"source.json",root/"replay.json",root/"audit.json",root/"out")
    def test_gameplay_keeps_drift_and_counts_missing_frames(self):
        result=self.run_case("Gameplay")
        self.assertEqual(result["telemetryFrames"],4)
        self.assertEqual(result["captureFrames"],2)
        self.assertEqual(result["analysisFrames"],2)
        self.assertEqual(result["presentedFrames"],2)
        self.assertEqual(result["solverEndpointError"],8)
        self.assertEqual(result["displayEndpointError"],8)
        self.assertEqual(result["at30fps"]["ticks"],2)
        self.assertEqual(result["at30fps"]["valid"],1)
        self.assertEqual(result["at30fps"]["within2px"],0)
    def test_hacker_absolute_error_is_not_aligned_away(self):
        result=self.run_case("Hacker")
        self.assertEqual(result["displayEndpointError"],3)
        self.assertEqual(result["displayError"]["mean"],1.875)
        self.assertEqual(result["at30fps"]["within2px"],0)

if __name__=="__main__": unittest.main()
