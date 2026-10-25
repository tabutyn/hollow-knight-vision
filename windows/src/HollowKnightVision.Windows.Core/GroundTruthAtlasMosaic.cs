using System.Buffers;

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
/// resampled into fixed world pixels. The newest unmasked pixel replaces existing
/// scenery immediately. Temporal agreement is opt-in for diagnostic comparisons.
/// </summary>
public sealed class GroundTruthAtlasMosaic
{
    private const int TemporalWindowCapacity = 7;
    private const double TemporalWindowDuration = 1;
    private const int TemporalAgreementTolerance = 24;

    private readonly record struct TemporalContext(
        int Width,
        int Height,
        string RoomId,
        ulong CaptureGeneration,
        Guid SessionId);

    private sealed class TemporalObservation
    {
        private uint[]? samples;

        internal TemporalObservation(
            double timestamp,
            string captureIdentity,
            int globalLeft,
            int globalTop,
            int width,
            int height)
        {
            Timestamp = timestamp;
            CaptureIdentity = captureIdentity;
            GlobalLeft = globalLeft;
            GlobalTop = globalTop;
            Width = width;
            Height = height;
            LogicalLength = checked(width * height);
            var rented = ArrayPool<uint>.Shared.Rent(LogicalLength);
            try
            {
                Array.Clear(rented);
                samples = rented;
            }
            catch
            {
                ArrayPool<uint>.Shared.Return(rented, clearArray: true);
                throw;
            }
        }

        internal double Timestamp { get; }
        internal string CaptureIdentity { get; }
        internal int GlobalLeft { get; }
        internal int GlobalTop { get; }
        internal int Width { get; }
        internal int Height { get; }
        internal int LogicalLength { get; }
        internal long LogicalByteCount => checked((long)LogicalLength * sizeof(uint));
        internal long CapacityByteCount => checked((long)(samples?.Length ?? 0) * sizeof(uint));

        internal void Write(int globalX, int globalY, uint sample)
        {
            var index = checked(
                (globalY - GlobalTop) * Width + globalX - GlobalLeft);
            samples![index] = sample;
        }

        internal bool TryRead(int globalX, int globalY, out uint sample)
        {
            var x = globalX - GlobalLeft;
            var y = globalY - GlobalTop;
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            {
                sample = 0;
                return false;
            }
            sample = samples![y * Width + x];
            return sample != 0;
        }

        internal uint Read(int x, int y) => samples![y * Width + x];

        internal void Return()
        {
            var rented = Interlocked.Exchange(ref samples, null);
            if (rented is not null) ArrayPool<uint>.Shared.Return(rented, clearArray: true);
        }
    }

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
    private readonly List<TemporalObservation> temporalWindow = [];
    private readonly Queue<(TemporalContext Context, string Identity)> recentCaptures = new();
    private readonly LiveTiledAtlasPixelPolicy pixelPolicy;
    private readonly Action? temporalCommitCheckpoint;
    private TemporalContext? temporalContext;
    private int outstandingObservationBufferCount;
    private long mutationEpoch;
    private long revision;
    private long acceptedFrameCount;

    public GroundTruthAtlasMosaic(
        double pixelsPerUnityUnit,
        int tileSize,
        double maximumAcceptedWorldSpan)
        : this(
            pixelsPerUnityUnit,
            tileSize,
            maximumAcceptedWorldSpan,
            temporalCommitCheckpoint: null) { }

    public GroundTruthAtlasMosaic(
        double pixelsPerWorldUnit = 64,
        int tileSize = 256,
        double sourceTopInsetFraction = 0.20,
        Action? temporalCommitCheckpoint = null,
        LiveTiledAtlasPixelPolicy pixelPolicy = LiveTiledAtlasPixelPolicy.NewestOpaque)
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
        this.temporalCommitCheckpoint = temporalCommitCheckpoint;
        if (pixelPolicy is not (LiveTiledAtlasPixelPolicy.NewestOpaque
            or LiveTiledAtlasPixelPolicy.TemporalAgreement))
            throw new ArgumentOutOfRangeException(nameof(pixelPolicy));
        this.pixelPolicy = pixelPolicy;
    }

    public double PixelsPerWorldUnit { get; }
    public int TileSize { get; }
    public double SourceTopInsetFraction { get; }
    public long AcceptedFrameCount { get { lock (gate) return acceptedFrameCount; } }
    public int TileCount { get { lock (gate) return tiles.Count; } }
    public long Revision { get { lock (gate) return revision; } }
    public int RetainedObservationCount { get { lock (gate) return temporalWindow.Count; } }
    public long RetainedObservationBytes
    {
        get { lock (gate) return temporalWindow.Sum(item => item.LogicalByteCount); }
    }
    public long RetainedObservationCapacityBytes
    {
        get { lock (gate) return temporalWindow.Sum(item => item.CapacityByteCount); }
    }
    public int OutstandingObservationBufferCount
    {
        get { lock (gate) return outstandingObservationBufferCount; }
    }

    public bool AddFrame(BgraFrame frame, ReceiverGroundTruthSample telemetry,
        IReadOnlyList<PixelRect>? excludedRectangles = null,
        string? roomId = null,
        ulong captureGeneration = 0,
        string? captureIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(telemetry);
        if (!telemetry.CameraAvailable || !telemetry.HasFiniteCoordinates()) return false;
        var sourceScale = telemetry.PixelsPerWorldUnit(frame.Height);
        if (sourceScale is not > 0 || !double.IsFinite(sourceScale.Value)) return false;
        var excluded = HudAtlasMask.Rasterize(frame.Width, frame.Height, excludedRectangles);

        lock (gate)
        {
        unchecked { mutationEpoch++; }
        var resolvedRoomId = roomId ?? telemetry.SceneName ?? string.Empty;
        var resolvedCaptureIdentity = captureIdentity
            ?? $"{telemetry.SessionId:N}:{telemetry.Sequence}:{frame.CaptureId}";
        var nextContext = new TemporalContext(
            frame.Width,
            frame.Height,
            resolvedRoomId,
            captureGeneration,
            telemetry.SessionId);
        var usesTemporalAgreement = pixelPolicy == LiveTiledAtlasPixelPolicy.TemporalAgreement;
        if (!usesTemporalAgreement
            && recentCaptures.Contains((nextContext, resolvedCaptureIdentity))) return true;
        var discontinuity = temporalContext is { } previous && previous != nextContext;
        if (temporalWindow.Count > 0)
        {
            var lastTimestamp = temporalWindow[^1].Timestamp;
            discontinuity = discontinuity
                || telemetry.UnityRealtime < lastTimestamp
                || telemetry.UnityRealtime - lastTimestamp > TemporalWindowDuration;
        }
        var eligibleHistory = discontinuity
            ? []
            : temporalWindow
                .Where(item =>
                    telemetry.UnityRealtime >= item.Timestamp
                    && telemetry.UnityRealtime - item.Timestamp <= TemporalWindowDuration)
                .ToList();
        if (eligibleHistory.Any(item =>
                string.Equals(
                    item.CaptureIdentity,
                    resolvedCaptureIdentity,
                    StringComparison.Ordinal)))
        {
            return true;
        }
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
        var projectedWidth = checked(globalRight - globalLeft);
        var projectedHeight = checked(globalBottom - globalTop);
        if (projectedWidth <= 0 || projectedHeight <= 0) return false;
        var retainedObservation = false;
        var observation = new TemporalObservation(
            telemetry.UnityRealtime,
            resolvedCaptureIdentity,
            globalLeft,
            globalTop,
            projectedWidth,
            projectedHeight);
        outstandingObservationBufferCount++;
        try
        {
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
                if (excluded?[sourceY * frame.Width + sourceX] == true) continue;
                var source = checked(sourceX * BgraFrame.BytesPerPixel);
                if (sourceRow[source + 3] == 0) continue;

                var tileX = FloorDivide(globalX, TileSize);
                var tileY = FloorDivide(globalY, TileSize);
                var coordinate = new AtlasTileCoordinate(tileX, tileY);
                touched.Add(coordinate);
                observation.Write(globalX, globalY, PackSample(sourceRow, source));
            }
        }

        var nextWindow = eligibleHistory
            .Skip(Math.Max(0, eligibleHistory.Count - (TemporalWindowCapacity - 1)))
            .ToList();
        nextWindow.Add(observation);
        var discardedHistory = temporalWindow
            .Where(item => !nextWindow.Contains(item))
            .ToArray();
        var nextRevision = checked(revision + 1);
        var nextAcceptedFrameCount = checked(acceptedFrameCount + 1);
        var stagedTiles = new Dictionary<AtlasTileCoordinate, Tile>(touched.Count);
        foreach (var coordinate in touched)
        {
            var staged = tiles.TryGetValue(coordinate, out var existing)
                ? CloneTile(existing)
                : new Tile(TileSize);
            staged.ObservationCount = checked(staged.ObservationCount + 1);
            staged.Revision = nextRevision;
            stagedTiles.Add(coordinate, staged);
        }
        for (var y = 0; y < observation.Height; y++)
        {
            for (var x = 0; x < observation.Width; x++)
            {
                var sample = observation.Read(x, y);
                if (sample == 0) continue;
                var globalX = observation.GlobalLeft + x;
                var globalY = observation.GlobalTop + y;
                var tileX = FloorDivide(globalX, TileSize);
                var tileY = FloorDivide(globalY, TileSize);
                var staged = stagedTiles[new AtlasTileCoordinate(tileX, tileY)];
                var localX = globalX - tileX * TileSize;
                var localY = globalY - tileY * TileSize;
                var pixelIndex = checked(localY * TileSize + localX);
                var weight = staged.Weights[pixelIndex];
                staged.Weights[pixelIndex] = weight == ushort.MaxValue
                    ? ushort.MaxValue
                    : checked((ushort)(weight + 1));
                if (!usesTemporalAgreement)
                    WriteSample(staged.Pixels, pixelIndex * BgraFrame.BytesPerPixel, sample);
            }
        }
        if (usesTemporalAgreement) ApplyTemporalObservation(observation, nextWindow, stagedTiles);

        var newTileCount = touched.Count(coordinate => !tiles.ContainsKey(coordinate));
        tiles.EnsureCapacity(checked(tiles.Count + newTileCount));
        temporalWindow.EnsureCapacity(TemporalWindowCapacity);
        recentCaptures.EnsureCapacity(65);
        var stagedEpoch = mutationEpoch;
        temporalCommitCheckpoint?.Invoke();
        if (mutationEpoch != stagedEpoch) return false;

        foreach (var pair in stagedTiles) tiles[pair.Key] = pair.Value;
        revision = nextRevision;
        acceptedFrameCount = nextAcceptedFrameCount;
        temporalContext = nextContext;
        temporalWindow.Clear();
        if (usesTemporalAgreement)
        {
            temporalWindow.AddRange(nextWindow);
            retainedObservation = true;
        }
        else
        {
            recentCaptures.Enqueue((nextContext, resolvedCaptureIdentity));
            while (recentCaptures.Count > 64) recentCaptures.Dequeue();
        }
        foreach (var discarded in discardedHistory) ReturnObservation(discarded);
        return true;
        }
        finally
        {
            if (!retainedObservation) ReturnObservation(observation);
        }
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
            unchecked { mutationEpoch++; }
            tiles.Clear();
            ClearTemporalWindow();
            temporalContext = null;
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
            unchecked { mutationEpoch++; }
            ClearTemporalWindow();
            temporalContext = null;
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
        lock (gate)
        {
            unchecked { mutationEpoch++; }
            acceptedFrameCount = Math.Max(acceptedFrameCount, count);
        }
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

    private void ApplyTemporalObservation(
        TemporalObservation current,
        IReadOnlyList<TemporalObservation> window,
        IReadOnlyDictionary<AtlasTileCoordinate, Tile> stagedTiles)
    {
        var candidateWidth = checked(current.Width + 2);
        var candidateHeight = checked(current.Height + 2);
        var candidateLength = checked(candidateWidth * candidateHeight);
        byte[]? states = null;
        uint[]? candidates = null;
        try
        {
            states = ArrayPool<byte>.Shared.Rent(candidateLength);
            candidates = ArrayPool<uint>.Shared.Rent(candidateLength);
            Array.Clear(states, 0, candidateLength);
            for (var y = 0; y < candidateHeight; y++)
            {
                for (var x = 0; x < candidateWidth; x++)
                {
                    var index = y * candidateWidth + x;
                    if (TryTemporalConsensus(
                            window,
                            current.GlobalLeft - 1 + x,
                            current.GlobalTop - 1 + y,
                            out var candidate,
                            out var hasEnoughSamples))
                    {
                        states[index] = 2;
                        candidates[index] = candidate;
                    }
                    else if (hasEnoughSamples)
                    {
                        states[index] = 1;
                    }
                }
            }

            for (var y = 0; y < current.Height; y++)
            {
                for (var x = 0; x < current.Width; x++)
                {
                    var sample = current.Read(x, y);
                    if (sample == 0) continue;
                    var globalX = current.GlobalLeft + x;
                    var globalY = current.GlobalTop + y;
                    var tileX = FloorDivide(globalX, TileSize);
                    var tileY = FloorDivide(globalY, TileSize);
                    if (!stagedTiles.TryGetValue(
                            new AtlasTileCoordinate(tileX, tileY),
                            out var tile))
                    {
                        continue;
                    }
                    var localX = globalX - tileX * TileSize;
                    var localY = globalY - tileY * TileSize;
                    var pixelIndex = checked(localY * TileSize + localX);
                    var destination = checked(pixelIndex * BgraFrame.BytesPerPixel);
                    var stateX = x + 1;
                    var stateY = y + 1;
                    var stateIndex = stateY * candidateWidth + stateX;
                    if (states[stateIndex] == 2
                        && TemporalNeighborhoodAgrees(
                            stateX,
                            stateY,
                            candidateWidth,
                            candidateHeight,
                            states))
                    {
                        WriteSample(tile.Pixels, destination, candidates[stateIndex]);
                    }
                    else if (tile.Pixels[destination + 3] == 0)
                    {
                        WriteSample(tile.Pixels, destination, sample);
                    }
                }
            }
        }
        finally
        {
            if (candidates is not null)
                ArrayPool<uint>.Shared.Return(candidates, clearArray: true);
            if (states is not null)
                ArrayPool<byte>.Shared.Return(states, clearArray: true);
        }
    }

    private bool TryTemporalConsensus(
        IReadOnlyList<TemporalObservation> window,
        int globalX,
        int globalY,
        out uint candidate,
        out bool hasEnoughSamples)
    {
        Span<uint> samples = stackalloc uint[TemporalWindowCapacity];
        var sampleCount = 0;
        foreach (var observation in window)
        {
            if (observation.TryRead(globalX, globalY, out var sample))
            {
                samples[sampleCount++] = sample;
            }
        }

        hasEnoughSamples = sampleCount >= 3;
        candidate = 0;
        if (!hasEnoughSamples) return false;

        Span<byte> blue = stackalloc byte[TemporalWindowCapacity];
        Span<byte> green = stackalloc byte[TemporalWindowCapacity];
        Span<byte> red = stackalloc byte[TemporalWindowCapacity];
        Span<byte> sortedBlue = stackalloc byte[TemporalWindowCapacity];
        Span<byte> sortedGreen = stackalloc byte[TemporalWindowCapacity];
        Span<byte> sortedRed = stackalloc byte[TemporalWindowCapacity];
        for (var index = 0; index < sampleCount; index++)
        {
            blue[index] = sortedBlue[index] = (byte)samples[index];
            green[index] = sortedGreen[index] = (byte)(samples[index] >> 8);
            red[index] = sortedRed[index] = (byte)(samples[index] >> 16);
        }
        InsertionSort(sortedBlue, sampleCount);
        InsertionSort(sortedGreen, sampleCount);
        InsertionSort(sortedRed, sampleCount);
        var medianIndex = sampleCount / 2;
        var medianBlue = sortedBlue[medianIndex];
        var medianGreen = sortedGreen[medianIndex];
        var medianRed = sortedRed[medianIndex];
        var agreementCount = 0;
        var closestSample = -1;
        var closestDistance = int.MaxValue;
        for (var index = 0; index < sampleCount; index++)
        {
            if (Math.Abs(blue[index] - medianBlue) > TemporalAgreementTolerance
                || Math.Abs(green[index] - medianGreen) > TemporalAgreementTolerance
                || Math.Abs(red[index] - medianRed) > TemporalAgreementTolerance)
            {
                continue;
            }
            agreementCount++;
            var blueDistance = blue[index] - medianBlue;
            var greenDistance = green[index] - medianGreen;
            var redDistance = red[index] - medianRed;
            var distance = blueDistance * blueDistance
                + greenDistance * greenDistance
                + redDistance * redDistance;
            if (distance <= closestDistance)
            {
                closestDistance = distance;
                closestSample = index;
            }
        }
        if (agreementCount < 3 || agreementCount <= sampleCount / 2 || closestSample < 0)
        {
            return false;
        }
        candidate = samples[closestSample];
        return true;
    }

    private static bool TemporalNeighborhoodAgrees(
        int centerX,
        int centerY,
        int width,
        int height,
        byte[] states)
    {
        var validCount = 0;
        var agreementCount = 0;
        for (var y = centerY - 1; y <= centerY + 1; y++)
        {
            for (var x = centerX - 1; x <= centerX + 1; x++)
            {
                if ((uint)x >= (uint)width || (uint)y >= (uint)height) continue;
                var state = states[y * width + x];
                if (state == 0) continue;
                validCount++;
                if (state == 2) agreementCount++;
            }
        }
        return validCount > 0 && agreementCount * 3 >= validCount * 2;
    }

    private static uint PackSample(ReadOnlySpan<byte> source, int offset) =>
        source[offset]
        | ((uint)source[offset + 1] << 8)
        | ((uint)source[offset + 2] << 16)
        | ((uint)source[offset + 3] << 24);

    private static void WriteSample(byte[] destination, int offset, uint sample)
    {
        destination[offset] = (byte)sample;
        destination[offset + 1] = (byte)(sample >> 8);
        destination[offset + 2] = (byte)(sample >> 16);
        destination[offset + 3] = (byte)(sample >> 24);
    }

    private Tile CloneTile(Tile source)
    {
        var clone = new Tile(TileSize)
        {
            ObservationCount = source.ObservationCount,
            Revision = source.Revision
        };
        Buffer.BlockCopy(source.Pixels, 0, clone.Pixels, 0, source.Pixels.Length);
        Array.Copy(source.Weights, clone.Weights, source.Weights.Length);
        return clone;
    }

    private void ReturnObservation(TemporalObservation observation)
    {
        observation.Return();
        outstandingObservationBufferCount--;
    }

    private void ClearTemporalWindow()
    {
        recentCaptures.Clear();
        foreach (var observation in temporalWindow) ReturnObservation(observation);
        temporalWindow.Clear();
    }

    private static void InsertionSort(Span<byte> values, int count)
    {
        for (var index = 1; index < count; index++)
        {
            var value = values[index];
            var insertion = index;
            while (insertion > 0 && values[insertion - 1] > value)
            {
                values[insertion] = values[insertion - 1];
                insertion--;
            }
            values[insertion] = value;
        }
    }

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
