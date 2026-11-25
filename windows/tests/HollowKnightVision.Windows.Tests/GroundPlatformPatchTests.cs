using HollowKnightVision.Windows.Core;

internal static class GroundPlatformPatchTests
{
    private static readonly GroundEdgeAnalysis Textured = Scene();
    private static readonly DetectedFloorLine Ledge = new(24, new(9, 24));
    private static readonly PixelRect Box = new(9, 24, 16, 12);

    public static void TestShortLedgeRequiresAdjacentUnmaskedTextureAndFullWidth()
    {
        var tracker = new GroundPlatformPatches();
        IReadOnlyList<PixelRect> Observe(double time, DetectedFloorLine floor, params PixelRect[] masks) =>
            tracker.Observe(Textured, [floor], masks, default(AtlasPoint), time);
        Check(Observe(0, Ledge).Count == 0);
        Check(Observe(.04, Ledge).SequenceEqual([Box]));
        Check(Observe(.08, Ledge, Box).Count == 0);
        Check(Observe(.12, Ledge).Count == 0);
        Check(Observe(.16, new(24, new(9, 23))).Count == 0);
        Check(Observe(.20, Ledge).Count == 0);
        Check(Observe(.40, Ledge).Count == 0);
        Check(Observe(.44, Ledge).SequenceEqual([Box]));
        Check(tracker.Observe(Textured, [Ledge], [], null, .48).Count == 0);
        Check(Observe(.52, Ledge).Count == 0);
    }

    public static void TestScrollingPlatformPatchesFollowTheCamera()
    {
        var tracker = new GroundPlatformPatches();
        Check(tracker.Observe(Textured, [Ledge], [], new(0, 0), 0).Count == 0);
        var moving = new DetectedFloorLine(25, new(7, 22));
        Check(tracker.Observe(Scene(2, 1), [moving], [], new(2, 1), .04)
            .SequenceEqual([new PixelRect(7, 25, 16, 12)]));
        var solid = new GroundEdgeAnalysis(Textured.Width, Textured.Height, Textured.GroundEdgePixels,
            new byte[Textured.Width * Textured.Height]);
        Check(tracker.Observe(solid, [moving], [], new(2, 1), .08).Count == 0);
        tracker.Reset();
        Check(tracker.Observe(Textured, [Ledge], [], new(0, 0), .12).Count == 0);
        Check(tracker.Observe(Textured, [Ledge], [], new(300, 0), .16).Count == 0);
    }

    public static void TestGameplayShowsScrollingPlatformPatchesBeforeAtlasAdmission()
    {
        const int width = 160, height = 100;
        var tracker = new GameplayTrackingPipeline(new VisualGameplayTracker(), new MaskedFrameMotionTracker(),
            deterministicGlobalSearch: true);
        var standing = new DetectedFloorLine(80, new(0, width - 1));
        var rejected = new DetectedFloorLine(70, new(50, 100));
        var knight = new PixelRect(0, 0, 10, 20);
        var signal = new FrameSignalProfile(2304, 2304, 2304, 100);
        var sawPlatform = false;
        for (var index = 0; index < 12; index++)
        {
            var x = 60 - 2 * index;
            var platform = new DetectedFloorLine(40, new(x, x + 39),
                [new(x, x + 7), new(x + 16, x + 23), new(x + 32, x + 39)]);
            var floors = GroundClearance.Filter([standing, platform, rejected], height, knight);
            var luma = new byte[width * height];
            for (var row = 0; row < height; row++)
            for (var column = 0; column < width - 2 * index; column++)
            {
                var hash = (ulong)(column + 2 * index) * 73_856_093UL ^ (ulong)row * 19_349_663UL;
                luma[row * width + column] = unchecked((byte)(((hash ^ (hash >> 13)) * 1_274_126_177UL) >> 11));
            }
            var zeros = new byte[luma.Length];
            var edges = new GroundEdgeAnalysis(width, height, zeros, luma);
            var comparison = new GroundComparisonAnalysis(width, height, zeros, luma);
            var theory = new GroundTheoryAnalysis(comparison, GroundTheoryTuning.Default, [], floors);
            var prepared = new GameplayTrackingPreparation(new(edges, theory, floors, [standing]), null, [], []);
            var time = index / 60d;
            var frame = new BgraFrame(width, height, width * 4, new byte[width * height * 4], index,
                DateTimeOffset.UnixEpoch.AddSeconds(time));
            var result = tracker.Track(frame, prepared, knight, new(true, true, false, new(true, false)),
                signal, time, 1, "fixture", false, buttons: null, resetWorldCoordinatesForCorrection: false,
                captureWorldEvidence: true);

            Check(result.Ground.AtlasLines.Count == 0 && !result.AllowsAtlasWrite && result.WorldEvidence is null,
                "A displayed texture match cannot add unconfirmed ground to the Atlas.");
            Check(result.PlatformPatches.All(patch => patch.Y != rejected.Row
                && floors.Any(floor => GroundPlatformPatches.Contains(floor, patch.X, patch.Y))),
                "A tracking box must belong to an eligible floor.");
            sawPlatform |= result.PlatformPatches.Any(patch => patch.Y == platform.Row);
        }
        Check(sawPlatform, "Expected the scrolling platform to show measured boxes before Atlas admission.");
    }

    public static void TestCameraReferenceNeedsFloorSupportWithoutACompetingLongerRow()
    {
        var ground = new DetectedFloorLine(50, new(0, 100));
        var background = new DetectedFloorLine(41, new(30, 75));
        var top = new DetectedFloorLine(50, new(12, 33));
        var offset = new DetectedFloorLine(50, new(92, 140));
        var adjacentTexture = new DetectedFloorLine(48, new(15, 38));
        var meaningfulHeight = new DetectedFloorLine(27, new(0, 50));
        var choices = new[] { background, top, offset, adjacentTexture, meaningfulHeight };
        Check(GroundLineDetector.CameraReferenceFloors(choices, [ground], 128, new(0, 0, 10, 20))
            .SequenceEqual([top, adjacentTexture]));
        Check(GroundLineDetector.CameraReferenceFloors(choices, [], 128, null).Count == 0);
        Check(GroundLineDetector.CameraReferenceFloors(choices, [ground], 128, null,
            [new PixelRect(12, 45, 30, 9)]).Count == 0);
        var bridge = new DetectedFloorLine(50, new(0, 100), [new(0, 9), new(90, 100)]);
        Check(GroundLineDetector.CameraReferenceFloors([background], [bridge], 128, null).SequenceEqual([background]));
    }

    private static GroundEdgeAnalysis Scene(int cameraX = 0, int cameraY = 0)
    {
        const int width = 96, height = 64;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            pixels[y * width + x] = (byte)(((x + cameraX) * 137 + (y - cameraY) * 93) & 255);
        return new(width, height, new byte[pixels.Length], pixels);
    }

    private static void Check(bool success, string message = "Unexpected ground patch or camera reference.")
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
