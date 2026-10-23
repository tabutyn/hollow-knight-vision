using HollowKnightVision.Windows.Core;

internal static class GroundTrackingEdgeParityTests
{
    private static void Check(bool value, string message = "Ground edge parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Close(double expected, double actual, double tolerance = .001)
    {
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected}, got {actual}.");
    }

    public static void TestFastDescentOntoNewPlatformUsesIndependentTextureSupport()
    {
        Check(GroundPoseContinuityGate.Accepts(
            new AtlasPoint(3, -26), new AtlasPoint(0, 0), 640,
            globalMatchCount: 0, hasGlobalCorrection: false,
            localInlierCount: 0, localTextureSupport: 12, localTextureError: 7));
        Check(!GroundPoseContinuityGate.Accepts(
            new AtlasPoint(3, -26), new AtlasPoint(0, 0), 640,
            globalMatchCount: 0, hasGlobalCorrection: false,
            localInlierCount: 0, localTextureSupport: 3, localTextureError: 7));
        Check(!GroundPoseContinuityGate.Accepts(
            new AtlasPoint(3, -100), new AtlasPoint(0, 0), 640,
            globalMatchCount: 0, hasGlobalCorrection: false,
            localInlierCount: 0, localTextureSupport: 12, localTextureError: 7));
    }

    public static void TestLineIndexPreservesOffscreenEvidenceWithoutCountingMisses()
    {
        var ledger = new GroundLinePresence(minimumObservationSeconds: .1);
        var line = new CleanFloorLine(40, new PixelSpan(16, 143));
        for (var index = 0; index < 10; index++)
        {
            ledger.Update([line], new AtlasPoint(0, 0), 160, 100,
                index * .02, poseVerified: true, []);
        }
        var before = ledger.Reviews(new AtlasPoint(0, 0), 160, 100).ToArray();
        for (var index = 10; index < 30; index++)
        {
            ledger.Update([], new AtlasPoint(0, 1_000), 160, 100,
                index * .02, poseVerified: true, []);
        }
        var after = ledger.Reviews(new AtlasPoint(0, 0), 160, 100).ToArray();
        Check(before.SequenceEqual(after));
        Check(after.FirstOrDefault().State == GroundLinePresenceState.Confirmed);
    }

    public static void TestMeasuredCaptureGapRequiresBroadPreciseBoundedEvidence()
    {
        bool Accepts(double? elapsed, int support = 9, double error = 15.4,
            double y = 604) => GroundPoseContinuityGate.Accepts(
                new AtlasPoint(1248, y),
                new AtlasPoint(1228, 549),
                640,
                globalMatchCount: 0,
                hasGlobalCorrection: false,
                localTextureSupport: support,
                localTextureError: error,
                captureElapsed: elapsed);

        Check(Accepts(.202));
        foreach (var elapsed in new double?[] { null, .05, .5, double.NaN,
                     double.NegativeInfinity })
        {
            Check(!Accepts(elapsed));
        }
        Check(!Accepts(.202, support: 7));
        Check(!Accepts(.202, error: 22));
        Check(!Accepts(.202, error: double.NaN));
        Check(!Accepts(.202, y: 670));
    }

    public static void TestRetainedPixelMotionCanConfirmRecoveryBeyondHeldPose()
    {
        const int width = 640;
        const int height = 200;
        var solver = new GroundCameraSolver();
        var gate = new SparseGroundRecoveryGate();
        var measured = new AtlasPoint(0, 40);
        var timestamp = 0d;
        for (var frame = 0; frame < 24; frame++)
        {
            timestamp = frame / 60d;
            _ = solver.Solve(Pixels(width, height, 40), width, height,
                [new GroundTextureLine(80, 16, 623)], timestamp, []);
        }

        var positions = new[] { 64, 68, 72 };
        for (var index = 0; index < positions.Length; index++)
        {
            var actualY = positions[index];
            timestamp += index == 0 ? .05 : 1d / 60;
            var solution = solver.Solve(
                    Pixels(width, height, actualY), width, height,
                    [new GroundTextureLine(40 + actualY, 16, 623)], timestamp, [])
                ?? throw new Exception("Expected retained-pixel ground solution.");
            measured = new AtlasPoint(
                measured.X - solution.ImageTranslation.X,
                measured.Y + solution.ImageTranslation.Y);
            Close(actualY, measured.Y);
            var accepted = gate.Accepts(
                measured, new AtlasPoint(0, 0), 640, timestamp,
                solution.Support, solution.Error);
            Check(accepted == (index == 1),
                $"Unexpected recovery result at observation {index}.");
        }
    }

    private static byte[] Pixels(int width, int height, int cameraY)
    {
        var result = new byte[checked(width * height)];
        for (var index = 0; index < result.Length; index++)
        {
            var x = index % width;
            var y = index / width - cameraY;
            var hash = unchecked((ulong)((long)x * 73_856_093L))
                ^ unchecked((ulong)((long)y * 19_349_663L));
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            result[index] = unchecked((byte)(hash >> 11));
        }
        return result;
    }
}
