namespace HollowKnightVision.Windows.Core;

public readonly record struct AtlasTileCoordinate(int X, int Y);

public sealed record AtlasTileSnapshot(
    AtlasTileCoordinate Coordinate,
    BgraFrame Frame,
    int WrittenPixelCount,
    long ObservationCount);

public sealed record AtlasMosaicDelta(
    long Revision,
    IReadOnlyList<AtlasTileSnapshot> Tiles);

/// <summary>
/// CPU tiled mosaic positioned by receiver camera telemetry. The live frame is
/// resampled into fixed world pixels and temporally averaged per atlas pixel.
/// </summary>
public sealed class GroundTruthAtlasMosaic
{
    private sealed class Tile
    {
        internal Tile(int size)
        {
            Pixels = new byte[checked(size * size * BgraFrame.BytesPerPixel)];
            Weights = new ushort[checked(size * size)];
        }

        internal byte[] Pixels { get; }
        internal ushort[] Weights { get; }
        internal long ObservationCount { get; set; }
        internal long Revision { get; set; }
    }

    private readonly object gate = new();
    private readonly Dictionary<AtlasTileCoordinate, Tile> tiles = [];
    private long revision;
    private long acceptedFrameCount;

    public GroundTruthAtlasMosaic(
        double pixelsPerWorldUnit = 64,
        int tileSize = 256,
        double sourceTopInsetFraction = 0.20)
    {
        if (!double.IsFinite(pixelsPerWorldUnit) || pixelsPerWorldUnit <= 0
            || pixelsPerWorldUnit > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelsPerWorldUnit));
        }
        if (tileSize is < 32 or > 2048) throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (!double.IsFinite(sourceTopInsetFraction)
            || sourceTopInsetFraction is < 0 or >= 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTopInsetFraction));
        }
        PixelsPerWorldUnit = pixelsPerWorldUnit;
        TileSize = tileSize;
        SourceTopInsetFraction = sourceTopInsetFraction;
    }

    public double PixelsPerWorldUnit { get; }
    public int TileSize { get; }
    public double SourceTopInsetFraction { get; }
    public long AcceptedFrameCount { get { lock (gate) return acceptedFrameCount; } }
    public int TileCount { get { lock (gate) return tiles.Count; } }
    public long Revision { get { lock (gate) return revision; } }

    public bool AddFrame(BgraFrame frame, ReceiverGroundTruthSample telemetry)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!telemetry.CameraAvailable || !telemetry.HasFiniteCoordinates()) return false;
        var sourceScale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (sourceScale is not > 0 || !double.IsFinite(sourceScale.Value)) return false;

        lock (gate)
        {
        var halfWorldWidth = frame.Width / (2 * sourceScale.Value);
        var halfWorldHeight = frame.Height / (2 * sourceScale.Value);
        var globalLeft = checked((int)Math.Floor(
            (telemetry.CameraX - halfWorldWidth) * PixelsPerWorldUnit));
        var globalRight = checked((int)Math.Ceiling(
            (telemetry.CameraX + halfWorldWidth) * PixelsPerWorldUnit));
        var globalTop = checked((int)Math.Floor(
            -(telemetry.CameraY + halfWorldHeight) * PixelsPerWorldUnit));
        var globalBottom = checked((int)Math.Ceiling(
            -(telemetry.CameraY - halfWorldHeight) * PixelsPerWorldUnit));
        var sourceTopInset = checked((int)Math.Round(frame.Height * SourceTopInsetFraction));
        var touched = new HashSet<AtlasTileCoordinate>();

        for (var globalY = globalTop; globalY < globalBottom; globalY++)
        {
            var worldY = -(globalY + 0.5) / PixelsPerWorldUnit;
            var sourceY = checked((int)Math.Round(
                (telemetry.CameraY - worldY) * sourceScale.Value + frame.Height / 2.0 - 0.5));
            if (sourceY < sourceTopInset || sourceY >= frame.Height) continue;
            var sourceRow = frame.Row(sourceY);
            for (var globalX = globalLeft; globalX < globalRight; globalX++)
            {
                var worldX = (globalX + 0.5) / PixelsPerWorldUnit;
                var sourceX = checked((int)Math.Round(
                    (worldX - telemetry.CameraX) * sourceScale.Value + frame.Width / 2.0 - 0.5));
                if ((uint)sourceX >= (uint)frame.Width) continue;

                var tileX = FloorDivide(globalX, TileSize);
                var tileY = FloorDivide(globalY, TileSize);
                var coordinate = new AtlasTileCoordinate(tileX, tileY);
                if (!tiles.TryGetValue(coordinate, out var tile))
                {
                    tile = new Tile(TileSize);
                    tiles.Add(coordinate, tile);
                }
                touched.Add(coordinate);
                var localX = globalX - tileX * TileSize;
                var localY = globalY - tileY * TileSize;
                var pixelIndex = checked(localY * TileSize + localX);
                var destination = checked(pixelIndex * BgraFrame.BytesPerPixel);
                var source = checked(sourceX * BgraFrame.BytesPerPixel);
                var weight = tile.Weights[pixelIndex];
                var blendWeight = weight == ushort.MaxValue ? ushort.MaxValue - 1 : weight;
                var nextWeight = blendWeight + 1;
                for (var channel = 0; channel < 3; channel++)
                {
                    var blended = (tile.Pixels[destination + channel] * (long)blendWeight
                                   + sourceRow[source + channel]) / nextWeight;
                    tile.Pixels[destination + channel] = (byte)blended;
                }
                tile.Pixels[destination + 3] = 255;
                tile.Weights[pixelIndex] = (ushort)nextWeight;
            }
        }

        var nextRevision = checked(++revision);
        foreach (var coordinate in touched)
        {
            tiles[coordinate].ObservationCount++;
            tiles[coordinate].Revision = nextRevision;
        }
        acceptedFrameCount++;
        return true;
        }
    }

    public IReadOnlyList<AtlasTileSnapshot> Snapshot()
    {
        lock (gate) return SnapshotCore(tiles);
    }

    public AtlasMosaicDelta SnapshotSince(long afterRevision)
    {
        lock (gate)
        {
            return new AtlasMosaicDelta(
                revision,
                SnapshotCore(tiles.Where(pair => pair.Value.Revision > afterRevision)));
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            tiles.Clear();
            acceptedFrameCount = 0;
            revision = checked(revision + 1);
        }
    }

    /// <summary>Seeds a restored tile. Non-transparent pixels receive one observation.</summary>
    public void ImportTile(AtlasTileCoordinate coordinate, BgraFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width != TileSize || frame.Height != TileSize)
        {
            throw new ArgumentException("Restored atlas tile size does not match the mosaic.", nameof(frame));
        }
        lock (gate)
        {
            var tile = new Tile(TileSize);
            Buffer.BlockCopy(frame.Pixels, 0, tile.Pixels, 0, tile.Pixels.Length);
            for (var index = 0; index < tile.Weights.Length; index++)
            {
                if (tile.Pixels[index * BgraFrame.BytesPerPixel + 3] != 0) tile.Weights[index] = 1;
            }
            tile.ObservationCount = 1;
            tile.Revision = checked(++revision);
            tiles[coordinate] = tile;
        }
    }

    public void RestoreAcceptedFrameCount(long count)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        lock (gate) acceptedFrameCount = Math.Max(acceptedFrameCount, count);
    }

    private IReadOnlyList<AtlasTileSnapshot> SnapshotCore(
        IEnumerable<KeyValuePair<AtlasTileCoordinate, Tile>> source)
    {
        var observedAt = DateTimeOffset.UtcNow;
        return source
            .OrderBy(pair => pair.Key.Y)
            .ThenBy(pair => pair.Key.X)
            .Select(pair =>
            {
                var pixels = pair.Value.Pixels.ToArray();
                var written = pair.Value.Weights.Count(weight => weight > 0);
                return new AtlasTileSnapshot(
                    pair.Key,
                    new BgraFrame(
                        TileSize,
                        TileSize,
                        checked(TileSize * BgraFrame.BytesPerPixel),
                        pixels,
                        acceptedFrameCount,
                        observedAt),
                    written,
                    pair.Value.ObservationCount);
            })
            .ToArray();
    }

    public bool TryReadPixel(
        int globalX,
        int globalY,
        out byte blue,
        out byte green,
        out byte red,
        out ushort observations)
    {
        lock (gate)
        {
        var tileX = FloorDivide(globalX, TileSize);
        var tileY = FloorDivide(globalY, TileSize);
        if (!tiles.TryGetValue(new AtlasTileCoordinate(tileX, tileY), out var tile))
        {
            blue = green = red = 0;
            observations = 0;
            return false;
        }
        var localX = globalX - tileX * TileSize;
        var localY = globalY - tileY * TileSize;
        var pixel = checked(localY * TileSize + localX);
        observations = tile.Weights[pixel];
        var offset = checked(pixel * BgraFrame.BytesPerPixel);
        blue = tile.Pixels[offset];
        green = tile.Pixels[offset + 1];
        red = tile.Pixels[offset + 2];
        return observations > 0;
        }
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
