using HollowKnightVision.Windows.Core;

internal static class VisualGroundIntegrationTests
{
    private const int Width = 320, Height = 100;
    private static DetectedFloorLine[] Lines(int dy = 0) => [new(40 + dy, new PixelSpan(0, Width - 1))];
    private static BgraFrame Frame(int id, double time) => new(Width, Height, Width * 4,
        new byte[Width * Height * 4], id, DateTimeOffset.UnixEpoch.AddSeconds(time));
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static void Near(double expected, double actual, double tolerance = 1) =>
        Check(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, got {actual}.");

    public static void TestPersistentRecoveryDrivesPublishedCamera()
    {
        var visual = new VisualGameplayTracker();
        var ground = new GroundHypothesisTracker(minimumObservationSeconds: .2,
            backgroundGlobalSearch: true, confirmedLossGraceSeconds: 3, productionCameraSolver: true);
        VisualGameplayObservation? observation = null;
        for (var i = 0; i < 40; i++)
            observation = visual.ObserveGroundAnchored(Frame(i, i / 60d), null,
                Texture(), Lines(), [], true, groundTracker: ground);
        Check(observation!.GroundTracking.GlobalFeatureCount >= 6, "No persistent recovery evidence learned.");
        Check(observation.GroundVerified && observation.AllowsAtlasWrite, "Initial pose never admitted.");
        ground.Reset(keepingGlobalFeatures: true);
        visual.PauseGroundVerification();
        var returned = false;
        for (var i = 0; i < 100; i++)
        {
            observation = visual.ObserveGroundAnchored(Frame(100 + i, 1 + i * .025), null,
                Texture(-62, 5), Lines(5), [], true, groundTracker: ground);
            if (observation.GroundTracking.GlobalMatchCount >= 6 && observation.GroundVerified)
            {
                returned = true;
                break;
            }
            Thread.Sleep(10);
        }
        Check(returned, "Persistent feature recovery never reached the live pose.");
        Near(62, observation!.Telemetry.CameraX);
        Near(5, observation.Telemetry.CameraY);
        Near(observation.GroundTracking.CameraPosition!.Value.X, observation.Telemetry.CameraX, 1e-8);
        Near(observation.GroundTracking.CameraPosition.Value.Y, observation.Telemetry.CameraY, 1e-8);
        Check(observation.AllowsAtlasWrite && observation.Source == "ground", "Recovered pose not used by atlas.");
        Check(ReferenceEquals(visual.LastPatchSearchExecution, ground.LatestPatchSearchExecution),
            "GPU diagnostics did not come from the camera's tracker.");
        ground.Reset();
    }

    public static void TestProvisionalPromotionRequiresBridgeTrust()
    {
        foreach (var allowPromotion in new[] { false, true })
        {
            var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
            for (var i = 0; i < 4; i++)
                tracker.Update(Texture(), Width, Height, Lines(), timestamp: i / 60d);
            tracker.ReanchorLocalCamera(new AtlasPoint(12, 0), Texture(seed: 123),
                Width, Height, Lines(), .1, allowsProvisionalPromotion: allowPromotion);
            var result = GroundHypothesisTrackingResult.Empty;
            var firstVerified = -1;
            for (var i = 1; i <= 25; i++)
            {
                result = tracker.Update(Texture(-i, seed: 123), Width, Height, Lines(),
                    timestamp: .1 + i / 60d);
                if (result.PoseVerified && firstVerified < 0) firstVerified = i;
            }
            if (allowPromotion)
                Check(firstVerified >= 12 && result.PoseVerified,
                    $"Strong bridge did not earn sustained texture trust: first verified {firstVerified}.");
            else Check(firstVerified < 0, "Untrusted bridge promoted provisional texture.");
        }
    }

    public static void TestFloorlessReanchorClearsStaleTexture()
    {
        var tracker = new GroundHypothesisTracker(minimumObservationSeconds: 0);
        for (var i = 0; i < 4; i++)
            tracker.Update(Texture(), Width, Height, Lines(), timestamp: i / 60d);
        tracker.ReanchorLocalCamera(new AtlasPoint(96, 20));
        var observed = tracker.Update(Texture(), Width, Height, [], timestamp: .1);
        Check(!observed.PoseVerified, "A floorless handoff retained stale trusted texture.");
        Near(96, observed.CameraPosition!.Value.X);
        Near(20, observed.CameraPosition.Value.Y);
    }

    private static byte[] Texture(int dx = 0, int dy = 0, uint seed = 0)
    {
        var pixels = new byte[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var sx = x - dx; var sy = y - dy;
            if (sx < 0 || sx >= Width || sy < 0 || sy >= Height) continue;
            var hash = (ulong)sx * 73_856_093 ^ (ulong)sy * 19_349_663 ^ seed;
            hash ^= hash >> 13;
            hash = unchecked(hash * 1_274_126_177);
            pixels[y * Width + x] = unchecked((byte)(hash >> 11));
        }
        return pixels;
    }
}
