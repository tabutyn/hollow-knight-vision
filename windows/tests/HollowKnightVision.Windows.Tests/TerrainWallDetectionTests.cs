using HollowKnightVision.Windows.Core;

internal static class TerrainWallDetectionTests
{
    public static void TestPlatformFacesFollowTheVisibleMask()
    {
        var frame = Frame(160, 90, (x, y) => y is >= 30 and < 65 && x is >= 40 and < 110 ? (byte)8 : (byte)90);
        var floors = new DetectedFloorLine[] { new(30, new(41, 109)) };
        var detected = TerrainWallDetector.Detect(frame, floors);
        Check(detected.Corners.Any(corner => Math.Abs(corner.X - 40) <= 4 && corner.Row == 30),
            "Missing left platform face.");
        Check(detected.Corners.Any(corner => Math.Abs(corner.X - 110) <= 4 && corner.Row == 30),
            "Missing right platform face.");
        Check(TerrainWallDetector.Detect(frame with { ValidSourcePixels = new byte[160 * 90] }, floors).Walls.Count == 0,
            "Masked pixels asserted a terrain edge.");
        Check(TerrainWallDetector.Detect(frame with { SourceLuma = null }, floors) == TerrainWallDetection.Empty,
            "A frame without luminance asserted terrain.");
    }

    public static void TestWallEvidenceUsesTexturedFacesAndStrongIsolatedEdges()
    {
        var slab = Frame(192, 128, (x, y) => x is >= 80 and < 155 && y is >= 30 and < 97
            ? (byte)((x / 4 + y / 5) % 2 == 0 ? 18 : 145) : (byte)95);
        var trace = TerrainWallDetector.Detect(slab, [new(30, new(80, 154))]);
        Check(trace.Walls.Any(wall => wall.SolidOnRight && Math.Abs(wall.X - 80) <= 4
            && wall.Top == 30 && wall.Bottom >= 77), "The textured platform face was fragmented.");

        var strong = Frame(160, 110, (x, _) => x < 58 ? (byte)92 : (byte)8);
        var independent = TerrainWallDetector.Detect(strong, []);
        Check(independent.Walls.Count == 1 && independent.Corners.Count == 0
            && Math.Abs(independent.Walls[0].X - 58) <= 2, "A strong unanchored wall did not survive.");
        var faint = Frame(160, 110, (x, _) => x < 58 ? (byte)32 : (byte)8);
        Check(TerrainWallDetector.Detect(faint, []).Walls.Count == 0, "A faint unanchored edge survived.");
    }

    public static void TestCornerExtrapolationRespectsVisibleEndpoints()
    {
        var wall = new DetectedWall(32, 39, 75, true);
        var top = new DetectedFloorLine(35, new(36, 82));
        var bottom = new DetectedFloorLine(78, new(8, 30));
        var crossing = new DetectedFloorLine(51, new(20, 60));
        var distant = new DetectedFloorLine(25, new(32, 72));
        var joined = TerrainWallDetector.Join([wall], [top, bottom, crossing, distant], 8, (_, _) => true);
        Check(joined.Corners.Select(corner => corner.FloorIndex).Order().SequenceEqual([0, 1]),
            "Joined the wrong floor or used a non-endpoint intersection.");
        Check(joined.Walls.Single() == new DetectedWall(32, 35, 78, true), "Wall tips missed the joined floor rows.");
        Check(TerrainWallDetector.Join([wall], [top], 8, (x, _) => x != 34).Corners.Count == 0,
            "An extrapolated corner crossed a masked horizontal gap.");
        Check(TerrainWallDetector.Join([wall], [top], 8, (_, y) => y != 37).Corners.Count == 0,
            "An extrapolated corner crossed a masked vertical gap.");
        Check(TerrainWallDetector.Join([wall with { SolidOnRight = false }], [top], 8, (_, _) => true).Corners.Count == 0,
            "Joined a floor facing away from the wall's solid side.");
    }

    public static void TestWallContactNeedsCorroboratedBlockedMotion()
    {
        var tracker = new KnightWallContact();
        DetectedWall? Sample(int i, double cameraX, int direction, IReadOnlyList<DetectedWall>? walls = null,
            double y = 130, bool partial = false) =>
            tracker.Observe(new KnightFootLocator.Measurement(new(200, y), partial), new(cameraX, 0),
                direction, i * .04, 640, 360, walls ?? []);

        for (var i = 0; i <= 7; i++)
            Check(Sample(i, i * 3, 1) is null, "Camera following motion was treated as a wall.");
        for (var i = 8; i < 15; i++) _ = Sample(i, 21, 1);
        Check(Sample(15, 21, 1) == new DetectedWall(203, 111, 123, true), "Missed a blocked approach.");
        Check(Sample(16, 21, 0) is null && Sample(17, 21, -1) is null, "Contact survived a release or reversal.");
        tracker.Reset();
        for (var i = 0; i < 10; i++)
            Check(Sample(i, 0, -1) is null, "Motion lock alone created a wall.");
        Check(Sample(10, 0, -1, [new(198, 75, 159, false)])?.SolidOnRight == false,
            "A measured wall in the held direction did not corroborate contact.");
        Check(tracker.Observe(null, new AtlasPoint(0, 0), -1, .44, 640, 360, []) is null,
            "The missing Knight extended contact.");
        Check(Sample(12, 0, -1) is null, "Wall contact resumed without a measured approach.");
    }

    public static void TestContactExtendsTheMatchingFaceAndJoinsWalkedGround()
    {
        var frame = Frame(640, 360, (_, _) => 90);
        var measured = new TerrainWallDetection([new(200, 80, 110, true)], []);
        var assisted = TerrainWallDetector.ApplyContact(measured,
            new(new CleanFloorLine(140, new(130, 198)), new DetectedWall(203, 109, 138, true)), [], frame);
        Check(assisted.Walls.Single() == new DetectedWall(200, 80, 140, true)
            && assisted.Corners.Single().FloorEndpoint == 198, "Close wall evidence did not extend and join the floor.");
        var opposite = TerrainWallDetector.ApplyContact(measured,
            new(null, new DetectedWall(203, 109, 138, false)), [], frame);
        Check(opposite.Walls.Count == 2, "Opposite solid sides were merged.");
        Check(TerrainWallDetector.ApplyContact(measured, default, [], frame) == measured,
            "Empty contact rewrote the detected geometry.");
    }

    private static GroundEdgeAnalysis Frame(int width, int height, Func<int, int, byte> pixel)
    {
        var luma = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++) luma[y * width + x] = pixel(x, y);
        var valid = new byte[luma.Length];
        Array.Fill(valid, (byte)1);
        return new(width, height, [], luma, valid);
    }

    private static void Check(bool success, string message)
    {
        if (!success) throw new InvalidOperationException(message);
    }
}
