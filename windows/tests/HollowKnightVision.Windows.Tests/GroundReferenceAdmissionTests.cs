using HollowKnightVision.Windows.Core;

internal static class GroundReferenceAdmissionTests
{
    private const int Width = 320;
    private const int Height = 180;
    private static readonly DetectedFloorLine EstablishedFloor = new(140, new(0, Width - 1));

    internal static void TestNewPlatformsRemainRecoverableWhenPreferredOldFloorIsOccluded()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2, productionCameraSolver: true);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var frame = 0; frame <= 80; frame++)
        {
            var occluded = frame >= 45;
            var pixels = Texture(frame, occluded ? EstablishedFloor.Row : null, []);
            var lines = new List<DetectedFloorLine>();
            if (!occluded) lines.Add(EstablishedFloor);
            if (frame >= 20)
            {
                lines.Add(new(60, new(32, 287)));
                lines.Add(new(100, new(32, 287)));
            }
            result = tracker.Update(pixels, Width, Height, lines, timestamp: frame / 60d);
        }

        AssertMotion(result);
        Check(result.AtlasLines.Any(line => Math.Abs(line.AtlasStart.Y - 80) < 2),
            "The replacement platform should become recoverable world geometry.");
    }

    internal static void TestStationaryNewOverlayRowsCannotOutvotePreviouslyEstablishedMovingFloor()
    {
        int[] overlayRows = [20, 50, 80, 110];
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: .2, productionCameraSolver: true);
        var result = GroundHypothesisTrackingResult.Empty;
        for (var frame = 0; frame <= 80; frame++)
        {
            var overlays = frame >= 30 ? overlayRows : [];
            var pixels = Texture(frame, hiddenFloor: null, overlays);
            var lines = overlays.Select(row => new DetectedFloorLine(row, new(32, 287))).Append(EstablishedFloor).ToArray();
            result = tracker.Update(pixels, Width, Height, lines, timestamp: frame / 60d);
        }

        AssertMotion(result);
        Check(result.HasConfirmedGround && result.AtlasLines.Count > 0
            && result.AtlasLines.All(line => Math.Abs(line.AtlasStart.Y - 40) < 2),
            "The stationary rows must leave the established moving floor as the only durable line.");
    }

    private static byte[] Texture(int frame, int? hiddenFloor, int[] overlayRows)
    {
        var shift = Math.Max(0, frame - 45) * 2;
        var pixels = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        {
            if (hiddenFloor is { } floor && y >= floor && y < floor + 12) continue;
            var offset = overlayRows.Any(row => y >= row && y < row + 12) ? 0 : shift;
            for (var x = 0; x < Width; x++)
            {
                var hash = (ulong)(x + offset) * 73_856_093UL ^ (ulong)y * 19_349_663UL;
                hash = unchecked((hash ^ (hash >> 13)) * 1_274_126_177UL);
                pixels[y * Width + x] = unchecked((byte)(hash >> 11));
            }
        }
        return pixels;
    }

    private static void AssertMotion(GroundHypothesisTrackingResult result) =>
        Check(result.PoseVerified && result.CameraPosition is { } camera && Math.Abs(camera.X - 70) < 2,
            $"Expected verified camera X near 70. Got {result.CameraPosition}; verified={result.PoseVerified}.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
