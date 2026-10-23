using HollowKnightVision.Windows.Core;

internal static class GroundHypothesisProductionParityTests
{
    private static void Check(
        bool value,
        string message = "Ground hypothesis production parity assertion failed.")
    {
        if (!value) throw new Exception(message);
    }

    private static void Near(double expected, double actual, double tolerance = .000_001) =>
        Check(Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected} ± {tolerance}, got {actual}.");

    public static void TestShortPersistentLineInsideKnownForegroundIsRejected()
    {
        var foreground = new PixelRect(48, 36, 64, 32);
        var line = new DetectedFloorLine(40, new PixelSpan(48, 111));

        var filtered = GroundLineDetector.RejectKnownForegroundLines(
            [line],
            imageHeight: 100,
            [foreground]);

        Check(filtered.Count == 0);
    }

    public static void TestOneFrameLineNeverCreatesPersistentFeatures()
    {
        const int width = 160;
        const int height = 100;
        var tracker = new VisualGameplayTracker();
        var frame = new BgraFrame(
            width,
            height,
            width * BgraFrame.BytesPerPixel,
            new byte[width * height * BgraFrame.BytesPerPixel],
            1,
            DateTimeOffset.UnixEpoch);

        var appeared = tracker.ObserveGroundAnchored(
            frame,
            diagnostic: null,
            Texture(width, height, shiftX: 0, shiftY: 0),
            [new DetectedFloorLine(40, new PixelSpan(32, 127))],
            [],
            hasCommittedEvidence: false);

        Check(!appeared.GroundVerified);
        Check(!appeared.AllowsAtlasWrite);
        Check(appeared.Source == "ground-seed");
    }

    public static void TestParallelLinesCannotAlignWithoutFeatureConfirmation()
    {
        const int width = 160;
        const int height = 100;
        var frame = Enumerable.Repeat((byte)90, width * height).ToArray();
        var solver = new GroundCameraSolver();
        _ = solver.Solve(
            frame,
            width,
            height,
            [new GroundTextureLine(40, 0, 159)],
            timestamp: 0,
            []);

        var ambiguous = solver.Solve(
            frame,
            width,
            height,
            [
                new GroundTextureLine(42, 0, 79),
                new GroundTextureLine(70, 0, 159)
            ],
            timestamp: 1.0 / 60,
            []);

        // Windows has no line-only correction channel: no verified texture
        // solution and an unchanged image pose are the equivalent observable.
        Check(ambiguous is null);
        Check(solver.ImagePosition == CameraVector.Zero);
    }

    public static void TestFeatureMatchesChooseCorrectParallelLineBeforeOverlap()
    {
        const int width = 160;
        const int height = 100;
        var solver = new GroundCameraSolver();
        _ = solver.Solve(
            Texture(width, height, shiftX: 0, shiftY: 0),
            width,
            height,
            [new GroundTextureLine(40, 0, 159)],
            timestamp: 0,
            []);

        var aligned = solver.Solve(
            Texture(width, height, shiftX: 0, shiftY: 5),
            width,
            height,
            [
                new GroundTextureLine(45, 0, 79),
                new GroundTextureLine(55, 0, 159)
            ],
            timestamp: 1.0 / 60,
            []);

        Check(aligned is not null);
        Near(0, aligned!.ImageTranslation.X, .5);
        Near(5, aligned.ImageTranslation.Y, .5);
        Near(5, solver.ImagePosition.Y, .5);
    }

    public static void TestDistantUnidentifiedLineCannotMovePersistentGroundPose()
    {
        const int width = 160;
        const int height = 160;
        var frame = Texture(width, height, shiftX: 0, shiftY: 0);
        var solver = new GroundCameraSolver();
        _ = solver.Solve(
            frame,
            width,
            height,
            [new GroundTextureLine(40, 0, 159)],
            timestamp: 0,
            []);

        var unrelated = solver.Solve(
            frame,
            width,
            height,
            [new GroundTextureLine(89, 0, 159)],
            timestamp: 1.0 / 60,
            []);

        Check(unrelated is null);
        Check(solver.ImagePosition == CameraVector.Zero);
    }

    public static void TestBiasedFallbackDoesNotReplaceTrustedGroundReference()
    {
        const int width = 320;
        const int height = 100;
        var solver = new GroundCameraSolver();
        for (var index = 0; index < 3; index++)
        {
            _ = solver.Solve(
                Texture(width, height, shiftX: 0, shiftY: 0),
                width,
                height,
                [new GroundTextureLine(40, 0, 319)],
                index / 60.0,
                []);
        }

        // Mac camera X is the negative of Windows' retained image-space X.
        // The pixels are at the true camera (12, 4), while fallback reports
        // the deliberately biased camera (16, 10).
        solver.FollowMeasuredCamera(
            new CameraVector(16, 10),
            Texture(width, height, shiftX: -12, shiftY: 4),
            width,
            height,
            [new GroundTextureLine(44, 0, 319)],
            timestamp: .05,
            []);

        var resumed = solver.Solve(
            Texture(width, height, shiftX: -16, shiftY: 8),
            width,
            height,
            [new GroundTextureLine(48, 0, 319)],
            timestamp: .07,
            []);

        Check(resumed is { ReferenceTrusted: true });
        Near(-16, solver.ImagePosition.X, .01);
        Near(8, solver.ImagePosition.Y, .01);
    }

    private static byte[] Texture(
        int width,
        int height,
        int shiftX,
        int shiftY)
    {
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceX = x - shiftX;
                var sourceY = y - shiftY;
                if (sourceX < 0 || sourceX >= width || sourceY < 0 || sourceY >= height)
                {
                    continue;
                }
                var hash = (ulong)sourceX * 73_856_093
                    ^ (ulong)sourceY * 19_349_663;
                hash ^= hash >> 13;
                hash = unchecked(hash * 1_274_126_177);
                pixels[y * width + x] = unchecked((byte)(hash >> 11));
            }
        }
        return pixels;
    }
}
