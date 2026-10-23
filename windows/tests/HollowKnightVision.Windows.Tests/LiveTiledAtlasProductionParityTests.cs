using HollowKnightVision.Windows.Core;

internal static class LiveTiledAtlasProductionParityTests
{
    private const int TileSize = 256;

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }

    public static void TestLargeWorldKeepsBothEndsAndStableTileIDs()
    {
        var atlas = new GroundTruthAtlasMosaic(
            pixelsPerWorldUnit: 1,
            tileSize: TileSize,
            sourceTopInsetFraction: 0);
        var image = SolidFrame(64, 32, red: 255, captureId: 1);

        Check(atlas.AddFrame(image, Telemetry(0, 0, image, 1)),
            "Initial atlas observation was rejected.");
        var firstCoordinates = atlas.Snapshot()
            .Select(tile => tile.Coordinate)
            .ToHashSet();

        Check(atlas.AddFrame(image, Telemetry(5_000, 0, image, 2)),
            "Far atlas observation was rejected.");
        var snapshot = atlas.Snapshot();
        var coordinates = snapshot.Select(tile => tile.Coordinate).ToArray();

        Check(atlas.TryReadPixel(0, 0, out _, out _, out var nearRed, out _)
                && nearRed == 255,
            "The near end of the large world was not retained.");
        Check(atlas.TryReadPixel(5_000, 0, out _, out _, out var farRed, out _)
                && farRed == 255,
            "The far end of the large world was not retained.");
        var pixelSpan = checked(
            (coordinates.Max(coordinate => coordinate.X)
             - coordinates.Min(coordinate => coordinate.X) + 1) * TileSize);
        Check(pixelSpan > 4_096, $"Expected a world span above 4096 pixels; got {pixelSpan}.");
        Check(coordinates.Length >= 2, "The sparse large world did not retain multiple tiles.");
        Check(coordinates.Distinct().Count() == coordinates.Length,
            "Published tile coordinates were not unique.");
        Check(firstCoordinates.All(coordinates.Contains),
            "Adding the far end changed the stable coordinate identity of an existing tile.");

        // Mac packs each signed tile coordinate into equal id/key values. Windows uses the
        // AtlasTileCoordinate value itself as both dictionary key and stable snapshot identity.
    }

    public static void TestTemporalQuiltingAveragesTextureAndAttenuatesOneFrameParticle()
    {
        const int width = 64;
        const int height = 64;
        var atlas = new LiveTiledAtlas(
            pixelPolicy: LiveTiledAtlasPixelPolicy.TemporalQuilting);

        for (var frame = 0; frame < 4; frame++)
        {
            var image = TexturedFrame(
                width,
                height,
                brightnessOffset: frame * 8,
                particle: frame == 3 ? (X: 20, Y: 20) : null,
                captureId: frame);
            Check(atlas.Insert(
                    frame,
                    image,
                    width,
                    new AtlasPoint(0, 0),
                    timestamp: frame * .1),
                $"Texture observation {frame} was rejected.");
        }

        var clean = ReadMacWorldPixel(atlas, x: 10, y: 10);
        var baseClean = TexturedComponent(10, 10, 0);
        var averageClean = (byte)(baseClean + 12);
        Check(clean == (averageClean, averageClean, averageClean),
            $"Clean texture average differed: {clean}.");

        var repaired = ReadMacWorldPixel(atlas, x: 20, y: 20);
        var baseParticle = TexturedComponent(20, 20, 0);
        var averageParticle = (byte)((baseParticle * 3 + 24 + 255 + 2) / 4);
        Check(repaired == (averageParticle, averageParticle, averageParticle),
            $"One-frame particle was not attenuated by temporal quilting: {repaired}.");
        Check(repaired.Red < 100, "The one-frame particle remained too bright.");
    }

    private static (byte Red, byte Green, byte Blue) ReadMacWorldPixel(
        LiveTiledAtlas atlas,
        int x,
        int y)
    {
        var tileX = Math.DivRem(x, TileSize, out var remainderX);
        if (remainderX < 0) tileX--;
        var tileY = Math.DivRem(y, TileSize, out var remainderY);
        if (remainderY < 0) tileY--;
        var tile = atlas.Tiles.Single(value => value.TileX == tileX && value.TileY == tileY);
        var localX = x - tileX * TileSize;
        var localY = y - tileY * TileSize;
        var offset = checked(
            (TileSize - 1 - localY) * tile.Image.Stride
            + localX * BgraFrame.BytesPerPixel);
        return (
            tile.Image.Pixels[offset + 2],
            tile.Image.Pixels[offset + 1],
            tile.Image.Pixels[offset]);
    }

    private static BgraFrame TexturedFrame(
        int width,
        int height,
        int brightnessOffset,
        (int X, int Y)? particle,
        long captureId)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var topY = 0; topY < height; topY++)
        {
            var worldY = height - 1 - topY;
            for (var x = 0; x < width; x++)
            {
                var isParticle = particle is { } center
                    && Math.Abs(x - center.X) <= 1
                    && Math.Abs(worldY - center.Y) <= 1;
                var component = isParticle
                    ? (byte)255
                    : TexturedComponent(x, worldY, brightnessOffset);
                var offset = checked((topY * width + x) * BgraFrame.BytesPerPixel);
                pixels[offset] = component;
                pixels[offset + 1] = component;
                pixels[offset + 2] = component;
                pixels[offset + 3] = 255;
            }
        }
        return new BgraFrame(
            width, height, checked(width * BgraFrame.BytesPerPixel),
            pixels, captureId, DateTimeOffset.UnixEpoch);
    }

    private static byte TexturedComponent(int x, int y, int brightnessOffset) =>
        checked((byte)(30 + (x * 7 + y * 11) % 40 + brightnessOffset));

    private static BgraFrame SolidFrame(
        int width,
        int height,
        byte red,
        long captureId)
    {
        var pixels = new byte[checked(width * height * BgraFrame.BytesPerPixel)];
        for (var offset = 0; offset < pixels.Length; offset += BgraFrame.BytesPerPixel)
        {
            pixels[offset + 2] = red;
            pixels[offset + 3] = 255;
        }
        return new BgraFrame(
            width, height, checked(width * BgraFrame.BytesPerPixel),
            pixels, captureId, DateTimeOffset.UnixEpoch);
    }

    private static ReceiverGroundTruthSample Telemetry(
        double cameraX,
        double cameraY,
        BgraFrame frame,
        long unityFrame) => new(
        2,
        "groundTruth",
        Guid.Empty,
        checked((ulong)unityFrame),
        unityFrame,
        unityFrame / 10d,
        "Tutorial_01",
        true,
        0,
        0,
        0,
        0,
        0,
        true,
        true,
        true,
        cameraX,
        cameraY,
        -38,
        cameraX,
        cameraY,
        0,
        frame.Height / 2d,
        1,
        1,
        null,
        null,
        frame.Width,
        frame.Height,
        frame.Width,
        frame.Height);
}
