namespace HollowKnightVision.Windows.Core;

public enum LiveTiledAtlasPixelPolicy
{
    NewestOpaque,
    NewestQuilted,
    PreserveBrighter,
    TemporalAgreement,
    TemporalQuilting
}

public readonly record struct LiveTiledAtlasTemporalDiagnostics(
    int RetainedFrameCount,
    long RetainedDecodedBytes,
    double LastFilterMilliseconds,
    int LastConsensusPixelCount,
    int LastImmediatePixelCount);

public sealed record LiveTiledAtlasObservation(
    long Id,
    BgraFrame MaskedFrame,
    double SolveWidth,
    AtlasPoint CameraPosition,
    long? CaptureIdentity = null,
    double? Timestamp = null,
    int RoomId = 0,
    ulong CaptureGeneration = 0);

public sealed record LiveTiledAtlasTile(
    long Id,
    long Key,
    int TileX,
    int TileY,
    BgraFrame Image,
    StencilRect WorldBounds,
    IReadOnlyList<long> ContributionIds);

public sealed record LiveTiledAtlasSnapshot(
    IReadOnlyList<LiveTiledAtlasTile> Tiles,
    ulong Revision,
    StencilRect TotalBounds,
    StencilRect ContentBounds);

/// <summary>
/// Source-backed tiled atlas. Contributions retain their identity and placement so a
/// complete pose-graph correction can be rendered privately and published atomically.
/// Atlas coordinates are bottom-origin; tile images remain ordinary top-origin BGRA.
/// </summary>
public sealed class LiveTiledAtlas
{
    public const int TileSize = 256;
    private const int TemporalWindowCapacity = 7;
    private const double TemporalWindowDuration = 1;
    private const int TemporalAgreementTolerance = 24;
    private const int NewestQuiltOverlap = 8;
    private const int QuiltPatchSize = 32;
    private const int QuiltOverlap = 8;
    private const int QuiltStride = 24;
    private const int QuiltDescriptorStep = 4;
    private const int QuiltMinimumDescriptorSamples = 24;
    private const int QuiltTextureTolerance = 18;
    private const int QuiltIlluminationTolerance = 72;

    private sealed record Contribution(
        long ObservationId,
        int ImageWidth,
        int ImageHeight,
        double SolveWidth,
        AtlasPoint CameraPosition,
        ulong InsertionOrder,
        long CaptureIdentity,
        double? Timestamp,
        int RoomId,
        ulong CaptureGeneration,
        IReadOnlyList<long> TemporalWindowIds,
        HashSet<AtlasTileCoordinate> TileCoordinates);

    private readonly record struct TemporalCaptureKey(
        long Identity,
        int RoomId,
        ulong CaptureGeneration);

    private readonly record struct TemporalContext(
        int ImageWidth,
        int ImageHeight,
        int RoomId,
        ulong CaptureGeneration);

    private sealed record DecodedSource(
        Contribution Contribution,
        byte[] Pixels,
        int BaseX,
        int BaseY);

    private readonly record struct QuiltPatchMember(
        int SourceIndex,
        int MeanBlue,
        int MeanGreen,
        int MeanRed,
        int Brightness,
        int TextureEnergy);

    private sealed record NewestQuiltSeams(
        int MinSourceX,
        int MinSourceY,
        int[]? Left,
        int[]? Right,
        int[]? Bottom,
        int[]? Top);

    private sealed class TemporalCandidateBuffers
    {
        internal int Width { get; private set; }
        internal int Height { get; private set; }
        internal int OriginX { get; private set; }
        internal int OriginY { get; private set; }
        internal byte[] Bgra { get; private set; } = [];
        internal bool[] HasEnoughSamples { get; private set; } = [];
        internal bool[] HasConsensus { get; private set; } = [];
        internal bool[] HasNeighborhoodConsensus { get; private set; } = [];
        private byte[] horizontalSampleCounts = [];
        private byte[] horizontalConsensusCounts = [];

        internal void Reset(int width, int height, int originX, int originY)
        {
            Width = width;
            Height = height;
            OriginX = originX;
            OriginY = originY;
            var count = checked(width * height);
            var byteCount = checked(count * BgraFrame.BytesPerPixel);
            if (Bgra.Length < byteCount) Bgra = new byte[byteCount];
            else Array.Clear(Bgra, 0, byteCount);
            if (HasEnoughSamples.Length < count) HasEnoughSamples = new bool[count];
            else Array.Clear(HasEnoughSamples, 0, count);
            if (HasConsensus.Length < count) HasConsensus = new bool[count];
            else Array.Clear(HasConsensus, 0, count);
            if (HasNeighborhoodConsensus.Length < count)
                HasNeighborhoodConsensus = new bool[count];
            else Array.Clear(HasNeighborhoodConsensus, 0, count);
            if (horizontalSampleCounts.Length < count)
                horizontalSampleCounts = new byte[count];
            if (horizontalConsensusCounts.Length < count)
                horizontalConsensusCounts = new byte[count];
        }

        internal bool BuildNeighborhoodConsensus(Func<bool> shouldCancel)
        {
            for (var y = 0; y < Height; y++)
            {
                if (shouldCancel()) return false;
                var row = y * Width;
                var sampleCount = 0;
                var consensusCount = 0;
                for (var x = 0; x < Width; x++)
                {
                    if (x == 0)
                    {
                        if (HasEnoughSamples[row]) sampleCount++;
                        if (HasConsensus[row]) consensusCount++;
                        if (Width > 1)
                        {
                            if (HasEnoughSamples[row + 1]) sampleCount++;
                            if (HasConsensus[row + 1]) consensusCount++;
                        }
                    }
                    else
                    {
                        var removed = x - 2;
                        if (removed >= 0)
                        {
                            if (HasEnoughSamples[row + removed]) sampleCount--;
                            if (HasConsensus[row + removed]) consensusCount--;
                        }
                        var added = x + 1;
                        if (added < Width)
                        {
                            if (HasEnoughSamples[row + added]) sampleCount++;
                            if (HasConsensus[row + added]) consensusCount++;
                        }
                    }
                    horizontalSampleCounts[row + x] = (byte)sampleCount;
                    horizontalConsensusCounts[row + x] = (byte)consensusCount;
                }
            }

            for (var x = 0; x < Width; x++)
            {
                if (shouldCancel()) return false;
                var sampleCount = 0;
                var consensusCount = 0;
                for (var y = 0; y < Height; y++)
                {
                    if (y == 0)
                    {
                        sampleCount += horizontalSampleCounts[x];
                        consensusCount += horizontalConsensusCounts[x];
                        if (Height > 1)
                        {
                            sampleCount += horizontalSampleCounts[Width + x];
                            consensusCount += horizontalConsensusCounts[Width + x];
                        }
                    }
                    else
                    {
                        var removed = y - 2;
                        if (removed >= 0)
                        {
                            sampleCount -= horizontalSampleCounts[removed * Width + x];
                            consensusCount -= horizontalConsensusCounts[removed * Width + x];
                        }
                        var added = y + 1;
                        if (added < Height)
                        {
                            sampleCount += horizontalSampleCounts[added * Width + x];
                            consensusCount += horizontalConsensusCounts[added * Width + x];
                        }
                    }
                    HasNeighborhoodConsensus[y * Width + x] = sampleCount > 0
                        && consensusCount * 3 >= sampleCount * 2;
                }
            }
            return true;
        }
    }

    private readonly record struct TemporalWork(
        int ConsensusPixelCount,
        int ImmediatePixelCount);

    private sealed record MutableState(
        Dictionary<long, Contribution> Contributions,
        Dictionary<AtlasTileCoordinate, HashSet<long>> ContributionIdsByTile,
        Dictionary<long, BgraFrame> SourceCache,
        List<long> SourceCacheOrder,
        Dictionary<long, byte[]> DecodedSourceCache,
        List<long> DecodedSourceCacheOrder,
        TemporalContext? TemporalContext,
        List<long> TemporalHistoryIds,
        Dictionary<TemporalCaptureKey, long> ObservationIdByCaptureKey,
        TemporalWork TemporalWork,
        LiveTiledAtlasTemporalDiagnostics TemporalDiagnostics,
        AtlasPoint? AnchorPosition,
        ulong NextInsertionOrder);

    private enum TileRenderKind { Tile, Empty, Cancelled }

    private readonly record struct TileRenderResult(TileRenderKind Kind, LiveTiledAtlasTile? Tile)
    {
        internal static TileRenderResult FromTile(LiveTiledAtlasTile tile) =>
            new(TileRenderKind.Tile, tile);
        internal static TileRenderResult Empty => new(TileRenderKind.Empty, null);
        internal static TileRenderResult Cancelled => new(TileRenderKind.Cancelled, null);
    }

    private readonly object gate = new();
    private readonly Func<long, BgraFrame?>? sourceProvider;
    private readonly int sourceCacheLimit;
    private readonly LiveTiledAtlasPixelPolicy pixelPolicy;
    private readonly Action? insertionEntered;
    private bool UsesTemporalEvidence =>
        pixelPolicy is LiveTiledAtlasPixelPolicy.TemporalAgreement
            or LiveTiledAtlasPixelPolicy.TemporalQuilting;
    private Dictionary<long, Contribution> contributions = [];
    private Dictionary<AtlasTileCoordinate, HashSet<long>> contributionIdsByTile = [];
    private Dictionary<long, BgraFrame> sourceCache = [];
    private List<long> sourceCacheOrder = [];
    private Dictionary<long, byte[]> decodedSourceCache = [];
    private List<long> decodedSourceCacheOrder = [];
    private TemporalContext? temporalContext;
    private List<long> temporalHistoryIds = [];
    private Dictionary<TemporalCaptureKey, long> observationIdByCaptureKey = [];
    private TemporalWork temporalWork;
    private LiveTiledAtlasTemporalDiagnostics temporalDiagnostics;
    private readonly TemporalCandidateBuffers temporalCandidateBuffers = new();
    private AtlasPoint? anchorPosition;
    private ulong nextInsertionOrder;
    private int lastRasterizedContributionCount;
    private LiveTiledAtlasSnapshot published = new([], 0, default, default);

    public LiveTiledAtlas(
        AtlasPoint? anchorPosition = null,
        Func<long, BgraFrame?>? sourceProvider = null,
        int sourceCacheLimit = 12,
        LiveTiledAtlasPixelPolicy pixelPolicy = LiveTiledAtlasPixelPolicy.NewestOpaque,
        Action? insertionEntered = null)
    {
        this.anchorPosition = IsFinite(anchorPosition) ? anchorPosition : null;
        this.sourceProvider = sourceProvider;
        this.sourceCacheLimit = Math.Max(0, sourceCacheLimit);
        this.pixelPolicy = pixelPolicy;
        this.insertionEntered = insertionEntered;
    }

    public AtlasPoint? FixedAnchorPosition
    {
        get { lock (gate) return anchorPosition; }
    }

    public LiveTiledAtlasSnapshot Snapshot
    {
        get { lock (gate) return CloneSnapshot(published); }
    }

    public IReadOnlyList<LiveTiledAtlasTile> Tiles => Snapshot.Tiles;
    public ulong Revision { get { lock (gate) return published.Revision; } }
    public StencilRect TotalBounds { get { lock (gate) return published.TotalBounds; } }
    public int LastPublicationRasterizedContributionCount
    {
        get { lock (gate) return lastRasterizedContributionCount; }
    }
    public int RetainedContributionTileLinkCount
    {
        get { lock (gate) return contributionIdsByTile.Values.Sum(ids => ids.Count); }
    }
    public LiveTiledAtlasTemporalDiagnostics TemporalDiagnostics
    {
        get { lock (gate) return temporalDiagnostics; }
    }

    public void Reset(AtlasPoint? anchorPosition = null)
    {
        lock (gate)
        {
            contributions.Clear();
            contributionIdsByTile.Clear();
            sourceCache.Clear();
            sourceCacheOrder.Clear();
            decodedSourceCache.Clear();
            decodedSourceCacheOrder.Clear();
            temporalContext = null;
            temporalHistoryIds.Clear();
            observationIdByCaptureKey.Clear();
            temporalWork = default;
            temporalDiagnostics = default;
            this.anchorPosition = IsFinite(anchorPosition) ? anchorPosition : null;
            nextInsertionOrder = 0;
            lastRasterizedContributionCount = 0;
            published = new([], unchecked(published.Revision + 1), default, default);
        }
    }

    public void EndTemporalWindow()
    {
        lock (gate)
        {
            temporalContext = null;
            temporalHistoryIds.Clear();
            decodedSourceCache.Clear();
            decodedSourceCacheOrder.Clear();
            temporalWork = default;
            UpdateTemporalDiagnostics(0);
        }
    }

    public bool Insert(LiveTiledAtlasObservation observation, Func<bool>? shouldCancel = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Insert(
            observation.Id,
            observation.MaskedFrame,
            observation.SolveWidth,
            observation.CameraPosition,
            observation.CaptureIdentity,
            observation.Timestamp,
            observation.RoomId,
            observation.CaptureGeneration,
            shouldCancel);
    }

    public bool Insert(
        long observationId,
        BgraFrame maskedFrame,
        double solveWidth,
        AtlasPoint cameraPosition,
        long? captureIdentity = null,
        double? timestamp = null,
        int roomId = 0,
        ulong captureGeneration = 0,
        Func<bool>? shouldCancel = null)
    {
        ArgumentNullException.ThrowIfNull(maskedFrame);
        shouldCancel ??= static () => false;
        if (observationId < 0
            || !double.IsFinite(solveWidth) || solveWidth <= 0
            || !IsFinite(cameraPosition)
            || timestamp is { } time && !double.IsFinite(time)
            || shouldCancel())
        {
            return false;
        }

        lock (gate)
        {
            insertionEntered?.Invoke();
            if (shouldCancel()) return false;
            var resolvedCaptureIdentity = captureIdentity ?? observationId;
            var captureKey = new TemporalCaptureKey(
                resolvedCaptureIdentity, roomId, captureGeneration);
            if (UsesTemporalEvidence
                && observationIdByCaptureKey.TryGetValue(captureKey, out var existingId)
                && existingId != observationId)
            {
                return true;
            }

            var ownedFrame = PackFrame(maskedFrame);
            var previous = CaptureMutableState();
            if (anchorPosition is null) anchorPosition = cameraPosition;
            if (anchorPosition is not { } anchor) return false;

            var dirty = new HashSet<AtlasTileCoordinate>();
            var replacing = contributions.Remove(observationId, out var oldContribution);
            if (oldContribution is not null)
            {
                dirty.UnionWith(oldContribution.TileCoordinates);
                Unlink(oldContribution);
            }

            IReadOnlyList<long> temporalWindowIds;
            if (oldContribution is not null)
            {
                temporalWindowIds = oldContribution.TemporalWindowIds;
            }
            else if (UsesTemporalEvidence)
            {
                temporalWindowIds = PrepareTemporalWindow(
                    observationId,
                    ownedFrame.Width,
                    ownedFrame.Height,
                    timestamp,
                    roomId,
                    captureGeneration);
                observationIdByCaptureKey[captureKey] = observationId;
            }
            else
            {
                temporalWindowIds = [observationId];
            }

            nextInsertionOrder = unchecked(nextInsertionOrder + 1);
            var contribution = new Contribution(
                observationId,
                ownedFrame.Width,
                ownedFrame.Height,
                solveWidth,
                cameraPosition,
                nextInsertionOrder,
                resolvedCaptureIdentity,
                timestamp,
                roomId,
                captureGeneration,
                temporalWindowIds,
                []);
            contribution = contribution with
            {
                TileCoordinates = ProjectedTiles(contribution, anchor)
            };
            contributions[observationId] = contribution;
            Link(contribution);
            decodedSourceCache.Remove(observationId);
            decodedSourceCacheOrder.Remove(observationId);
            Cache(ownedFrame, observationId);
            dirty.UnionWith(contribution.TileCoordinates);
            temporalWork = default;
            var temporalStarted = System.Diagnostics.Stopwatch.GetTimestamp();

            try
            {
                var succeeded = replacing
                    ? PublishRebuild(dirty, shouldCancel)
                    : UsesTemporalEvidence
                        ? PublishTemporalIncremental(contribution, ownedFrame, shouldCancel)
                        : PublishIncremental(contribution, ownedFrame, shouldCancel);
                if (!succeeded)
                {
                    RestoreMutableState(previous);
                    return false;
                }
                if (UsesTemporalEvidence)
                {
                    var retained = temporalHistoryIds.ToHashSet();
                    foreach (var id in decodedSourceCache.Keys
                                 .Where(id => !retained.Contains(id)).ToArray())
                    {
                        decodedSourceCache.Remove(id);
                    }
                    decodedSourceCacheOrder.RemoveAll(id => !retained.Contains(id));
                    UpdateTemporalDiagnostics(
                        System.Diagnostics.Stopwatch.GetElapsedTime(temporalStarted)
                            .TotalMilliseconds);
                }
                return succeeded;
            }
            catch
            {
                RestoreMutableState(previous);
                throw;
            }
        }
    }

    /// <summary>
    /// Applies a complete pose-graph revision. Stale revisions and incomplete key sets
    /// are rejected; cancellation cannot expose partially corrected tiles or metadata.
    /// </summary>
    public bool ReplaceCameraPositions(
        IReadOnlyDictionary<long, AtlasPoint> positions,
        ulong baseRevision,
        Func<bool>? shouldCancel = null)
    {
        ArgumentNullException.ThrowIfNull(positions);
        shouldCancel ??= static () => false;
        lock (gate)
        {
            if (baseRevision != published.Revision
                || positions.Count != contributions.Count
                || !positions.Keys.ToHashSet().SetEquals(contributions.Keys)
                || positions.Values.Any(position => !IsFinite(position)))
            {
                return false;
            }
            if (anchorPosition is not { } anchor) return contributions.Count == 0;
            if (shouldCancel()) return false;

            var previous = CaptureMutableState();
            var dirty = new HashSet<AtlasTileCoordinate>();
            try
            {
                foreach (var id in contributions.Keys.Order().ToArray())
                {
                    if (shouldCancel())
                    {
                        RestoreMutableState(previous);
                        return false;
                    }
                    var contribution = contributions[id];
                    var position = positions[id];
                    if (contribution.CameraPosition == position) continue;
                    dirty.UnionWith(contribution.TileCoordinates);
                    Unlink(contribution);
                    contribution = contribution with
                    {
                        CameraPosition = position,
                        TileCoordinates = []
                    };
                    contribution = contribution with
                    {
                        TileCoordinates = ProjectedTiles(contribution, anchor)
                    };
                    contributions[id] = contribution;
                    Link(contribution);
                    dirty.UnionWith(contribution.TileCoordinates);
                }

                if (PublishRebuild(dirty, shouldCancel)) return true;
                RestoreMutableState(previous);
                return false;
            }
            catch
            {
                RestoreMutableState(previous);
                throw;
            }
        }
    }

    private bool PublishRebuild(
        IReadOnlySet<AtlasTileCoordinate> dirty,
        Func<bool> shouldCancel)
    {
        lastRasterizedContributionCount = 0;
        var current = published.Tiles.ToDictionary(
            tile => new AtlasTileCoordinate(tile.TileX, tile.TileY));
        foreach (var coordinate in dirty.OrderBy(value => value.Y).ThenBy(value => value.X))
        {
            if (shouldCancel()) return false;
            var rendered = RenderTile(coordinate, shouldCancel);
            if (rendered.Kind == TileRenderKind.Cancelled) return false;
            if (rendered.Kind == TileRenderKind.Empty) current.Remove(coordinate);
            else current[coordinate] = rendered.Tile!;
        }
        Publish(current.Values, unchecked(published.Revision + 1));
        return true;
    }

    private bool PublishIncremental(
        Contribution contribution,
        BgraFrame source,
        Func<bool> shouldCancel)
    {
        lastRasterizedContributionCount = 0;
        if (shouldCancel()) return false;
        if (anchorPosition is not { } anchor) return false;
        var current = published.Tiles.ToDictionary(
            tile => new AtlasTileCoordinate(tile.TileX, tile.TileY));
        foreach (var coordinate in contribution.TileCoordinates
                     .OrderBy(value => value.Y).ThenBy(value => value.X))
        {
            if (shouldCancel()) return false;
            current.TryGetValue(coordinate, out var existing);
            var rendered = Composite(
                contribution, source, coordinate, existing, anchor, shouldCancel);
            if (rendered.Kind == TileRenderKind.Cancelled) return false;
            if (rendered.Kind == TileRenderKind.Empty) current.Remove(coordinate);
            else current[coordinate] = rendered.Tile!;
        }
        if (shouldCancel()) return false;
        Publish(current.Values, unchecked(published.Revision + 1));
        return true;
    }

    private void Publish(IEnumerable<LiveTiledAtlasTile> source, ulong revision)
    {
        var tiles = source.OrderBy(tile => tile.TileY).ThenBy(tile => tile.TileX).ToArray();
        published = new(
            tiles,
            revision,
            BoundsOfTiles(tiles),
            RegisteredContentBounds());
    }

    private IReadOnlyList<long> PrepareTemporalWindow(
        long observationId,
        int imageWidth,
        int imageHeight,
        double? timestamp,
        int roomId,
        ulong captureGeneration)
    {
        var nextContext = new TemporalContext(
            imageWidth, imageHeight, roomId, captureGeneration);
        var discontinuity = temporalContext is not null
                            && temporalContext != nextContext;
        if (temporalHistoryIds.Count > 0
            && contributions.TryGetValue(temporalHistoryIds[^1], out var last)
            && last.Timestamp is { } lastTimestamp)
        {
            if (timestamp is null)
            {
                temporalContext = nextContext;
                temporalHistoryIds.Clear();
                temporalHistoryIds.Add(observationId);
                return temporalHistoryIds.ToArray();
            }
            if (timestamp.Value < lastTimestamp
                || timestamp.Value - lastTimestamp > TemporalWindowDuration)
            {
                discontinuity = true;
            }
        }
        else if (temporalHistoryIds.Count > 0 && timestamp is not null)
        {
            discontinuity = true;
        }
        if (discontinuity) temporalHistoryIds.Clear();
        temporalContext = nextContext;
        if (timestamp is { } currentTimestamp)
        {
            temporalHistoryIds.RemoveAll(id =>
                !contributions.TryGetValue(id, out var earlier)
                || earlier.Timestamp is not { } earlierTimestamp
                || currentTimestamp < earlierTimestamp
                || currentTimestamp - earlierTimestamp > TemporalWindowDuration);
        }
        temporalHistoryIds.Add(observationId);
        if (temporalHistoryIds.Count > TemporalWindowCapacity)
        {
            temporalHistoryIds.RemoveRange(
                0,
                temporalHistoryIds.Count - TemporalWindowCapacity);
        }
        return temporalHistoryIds.ToArray();
    }

    private bool PublishTemporalIncremental(
        Contribution contribution,
        BgraFrame source,
        Func<bool> shouldCancel)
    {
        lastRasterizedContributionCount = 0;
        if (shouldCancel() || anchorPosition is not { } anchor) return false;
        var currentPixels = DecodedPixels(contribution.ObservationId, source);
        if (currentPixels is null)
        {
            return PublishRebuild(contribution.TileCoordinates, shouldCancel);
        }

        TemporalCandidateBuffers? sharedTemporalCandidates = null;
        if (contribution.TemporalWindowIds.Count >= 3)
        {
            sharedTemporalCandidates = pixelPolicy == LiveTiledAtlasPixelPolicy.TemporalQuilting
                ? TemporalQuiltCandidates(contribution, anchor, shouldCancel)
                : TemporalCandidates(contribution, anchor, shouldCancel);
            if (sharedTemporalCandidates is null) return false;
        }

        var current = published.Tiles.ToDictionary(
            tile => new AtlasTileCoordinate(tile.TileX, tile.TileY));
        foreach (var coordinate in contribution.TileCoordinates
                     .OrderBy(value => value.Y).ThenBy(value => value.X))
        {
            if (shouldCancel()) return false;
            current.TryGetValue(coordinate, out var existing);
            var rendered = CompositeTemporal(
                contribution,
                currentPixels,
                coordinate,
                existing,
                anchor,
                sharedTemporalCandidates,
                shouldCancel);
            if (rendered.Kind == TileRenderKind.Cancelled) return false;
            if (rendered.Kind == TileRenderKind.Empty) current.Remove(coordinate);
            else current[coordinate] = rendered.Tile!;
        }
        if (shouldCancel()) return false;
        Publish(current.Values, unchecked(published.Revision + 1));
        return true;
    }

    private TileRenderResult RenderTemporalTile(
        AtlasTileCoordinate coordinate,
        Func<bool> shouldCancel)
    {
        if (shouldCancel() || anchorPosition is not { } anchor)
            return TileRenderResult.Cancelled;
        if (!contributionIdsByTile.TryGetValue(coordinate, out var ids) || ids.Count == 0)
            return TileRenderResult.Empty;

        var pixels = new byte[checked(TileSize * TileSize * BgraFrame.BytesPerPixel)];
        var selected = new HashSet<long>();
        var observed = false;
        foreach (var contribution in ids.Select(id => contributions[id])
                     .OrderBy(value => value.InsertionOrder)
                     .ThenBy(value => value.ObservationId))
        {
            if (shouldCancel()) return TileRenderResult.Cancelled;
            var currentPixels = DecodedPixels(contribution.ObservationId);
            if (currentPixels is null) continue;
            lastRasterizedContributionCount++;
            var changed = ApplyTemporalContribution(
                contribution,
                currentPixels,
                coordinate,
                anchor,
                pixels,
                selected,
                null,
                shouldCancel);
            if (changed is null) return TileRenderResult.Cancelled;
            observed |= changed.Value;
        }
        return observed
            ? TileRenderResult.FromTile(MakeTile(coordinate, pixels, selected))
            : TileRenderResult.Empty;
    }

    private TileRenderResult CompositeTemporal(
        Contribution contribution,
        byte[] currentPixels,
        AtlasTileCoordinate coordinate,
        LiveTiledAtlasTile? existing,
        AtlasPoint anchor,
        TemporalCandidateBuffers? temporalCandidates,
        Func<bool> shouldCancel)
    {
        var pixels = existing?.Image.Pixels.ToArray()
                     ?? new byte[checked(TileSize * TileSize * BgraFrame.BytesPerPixel)];
        HashSet<long> selected = existing is null
            ? []
            : existing.ContributionIds.ToHashSet();
        lastRasterizedContributionCount++;
        var changed = ApplyTemporalContribution(
            contribution,
            currentPixels,
            coordinate,
            anchor,
            pixels,
            selected,
            temporalCandidates,
            shouldCancel);
        if (changed is null) return TileRenderResult.Cancelled;
        if (!changed.Value)
            return existing is null ? TileRenderResult.Empty : TileRenderResult.FromTile(existing);
        return TileRenderResult.FromTile(MakeTile(coordinate, pixels, selected));
    }

    private bool? ApplyTemporalContribution(
        Contribution contribution,
        byte[] currentPixels,
        AtlasTileCoordinate coordinate,
        AtlasPoint anchor,
        byte[] destinationPixels,
        HashSet<long> selectedIds,
        TemporalCandidateBuffers? precomputedCandidates,
        Func<bool> shouldCancel)
    {
        var placement = PlacementBounds(contribution, anchor);
        var baseX = checked((int)placement.X);
        var baseY = checked((int)placement.Y);
        var tileMinX = checked(coordinate.X * TileSize);
        var tileMinY = checked(coordinate.Y * TileSize);
        var minSourceX = Math.Max(0, tileMinX - baseX);
        var maxSourceX = Math.Min(
            contribution.ImageWidth - 1,
            checked(tileMinX + TileSize - 1 - baseX));
        var minSourceY = Math.Max(0, tileMinY - baseY);
        var maxSourceY = Math.Min(
            contribution.ImageHeight - 1,
            checked(tileMinY + TileSize - 1 - baseY));
        if (minSourceX > maxSourceX || minSourceY > maxSourceY) return false;

        TemporalCandidateBuffers? candidates;
        if (precomputedCandidates is not null)
        {
            candidates = precomputedCandidates;
        }
        else if (contribution.TemporalWindowIds.Count >= 3)
        {
            candidates = pixelPolicy == LiveTiledAtlasPixelPolicy.TemporalQuilting
                ? TemporalQuiltCandidates(contribution, anchor, shouldCancel)
                : TemporalCandidates(contribution, coordinate, anchor, shouldCancel);
            if (candidates is null) return null;
        }
        else
        {
            candidates = null;
        }

        var changed = false;
        var usedConsensus = false;
        var usedImmediate = false;
        var consensusPixelCount = 0;
        var immediatePixelCount = 0;
        for (var sourceY = minSourceY; sourceY <= maxSourceY; sourceY++)
        {
            if (shouldCancel()) return null;
            var sourceTopY = contribution.ImageHeight - 1 - sourceY;
            for (var sourceX = minSourceX; sourceX <= maxSourceX; sourceX++)
            {
                var sourceIndex = checked(
                    (sourceTopY * contribution.ImageWidth + sourceX)
                    * BgraFrame.BytesPerPixel);
                if (currentPixels[sourceIndex + 3] == 0) continue;
                var worldX = baseX + sourceX;
                var worldY = baseY + sourceY;
                var localX = worldX - tileMinX;
                var localY = worldY - tileMinY;
                var destination = checked(
                    ((TileSize - 1 - localY) * TileSize + localX)
                    * BgraFrame.BytesPerPixel);

                if (candidates is not null)
                {
                    var candidateX = worldX - candidates.OriginX;
                    var candidateY = worldY - candidates.OriginY;
                    var candidateIndex = checked(candidateY * candidates.Width + candidateX);
                    if (candidates.HasConsensus[candidateIndex]
                        && (pixelPolicy == LiveTiledAtlasPixelPolicy.TemporalQuilting
                            || candidates.HasNeighborhoodConsensus[candidateIndex]))
                    {
                        var candidate = checked(candidateIndex * BgraFrame.BytesPerPixel);
                        destinationPixels[destination] = candidates.Bgra[candidate];
                        destinationPixels[destination + 1] = candidates.Bgra[candidate + 1];
                        destinationPixels[destination + 2] = candidates.Bgra[candidate + 2];
                        destinationPixels[destination + 3] = candidates.Bgra[candidate + 3];
                        consensusPixelCount++;
                        changed = true;
                        usedConsensus = true;
                        continue;
                    }
                }

                if (destinationPixels[destination + 3] != 0) continue;
                destinationPixels[destination] = currentPixels[sourceIndex];
                destinationPixels[destination + 1] = currentPixels[sourceIndex + 1];
                destinationPixels[destination + 2] = currentPixels[sourceIndex + 2];
                destinationPixels[destination + 3] = currentPixels[sourceIndex + 3];
                immediatePixelCount++;
                changed = true;
                usedImmediate = true;
            }
        }
        temporalWork = new(
            temporalWork.ConsensusPixelCount + consensusPixelCount,
            temporalWork.ImmediatePixelCount + immediatePixelCount);
        if (usedImmediate) selectedIds.Add(contribution.ObservationId);
        if (usedConsensus) selectedIds.UnionWith(contribution.TemporalWindowIds);
        return changed;
    }

    private TemporalCandidateBuffers? TemporalCandidates(
        Contribution contribution,
        AtlasPoint anchor,
        Func<bool> shouldCancel) =>
        TemporalCandidates(contribution, null, anchor, shouldCancel);

    private TemporalCandidateBuffers? TemporalCandidates(
        Contribution contribution,
        AtlasTileCoordinate coordinate,
        AtlasPoint anchor,
        Func<bool> shouldCancel) =>
        TemporalCandidates(contribution, (AtlasTileCoordinate?)coordinate, anchor, shouldCancel);

    private TemporalCandidateBuffers? TemporalCandidates(
        Contribution contribution,
        AtlasTileCoordinate? coordinate,
        AtlasPoint anchor,
        Func<bool> shouldCancel)
    {
        const int halo = 1;
        var placement = PlacementBounds(contribution, anchor);
        var centralMinX = checked((int)placement.X);
        var centralMaxX = checked((int)placement.Right - 1);
        var centralMinY = checked((int)placement.Y);
        var centralMaxY = checked((int)placement.Bottom - 1);
        if (coordinate is { } tileCoordinate)
        {
            var tileMinX = checked(tileCoordinate.X * TileSize);
            var tileMinY = checked(tileCoordinate.Y * TileSize);
            centralMinX = Math.Max(tileMinX, centralMinX);
            centralMaxX = Math.Min(checked(tileMinX + TileSize - 1), centralMaxX);
            centralMinY = Math.Max(tileMinY, centralMinY);
            centralMaxY = Math.Min(checked(tileMinY + TileSize - 1), centralMaxY);
        }
        if (centralMinX > centralMaxX || centralMinY > centralMaxY) return null;
        var originX = checked(centralMinX - halo);
        var originY = checked(centralMinY - halo);
        var width = checked(centralMaxX - centralMinX + 1 + halo * 2);
        var height = checked(centralMaxY - centralMinY + 1 + halo * 2);
        var sources = new List<DecodedSource>(contribution.TemporalWindowIds.Count);
        foreach (var id in contribution.TemporalWindowIds)
        {
            if (!contributions.TryGetValue(id, out var item)) continue;
            var pixels = DecodedPixels(id);
            if (pixels is null) continue;
            var sourcePlacement = PlacementBounds(item, anchor);
            sources.Add(new(
                item,
                pixels,
                checked((int)sourcePlacement.X),
                checked((int)sourcePlacement.Y)));
        }

        var buffers = temporalCandidateBuffers;
        buffers.Reset(width, height, originX, originY);
        var first = new byte[TemporalWindowCapacity];
        var second = new byte[TemporalWindowCapacity];
        var third = new byte[TemporalWindowCapacity];
        var alpha = new byte[TemporalWindowCapacity];
        var sortedFirst = new byte[TemporalWindowCapacity];
        var sortedSecond = new byte[TemporalWindowCapacity];
        var sortedThird = new byte[TemporalWindowCapacity];
        var sourceRowOffsets = new int[sources.Count];

        for (var y = 0; y < height; y++)
        {
            if (shouldCancel()) return null;
            var worldY = originY + y;
            for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                var source = sources[sourceIndex];
                var sourceY = worldY - source.BaseY;
                sourceRowOffsets[sourceIndex] = sourceY < 0
                    || sourceY >= source.Contribution.ImageHeight
                    ? -1
                    : checked((source.Contribution.ImageHeight - 1 - sourceY)
                              * source.Contribution.ImageWidth * BgraFrame.BytesPerPixel);
            }
            for (var x = 0; x < width; x++)
            {
                var worldX = originX + x;
                var sampleCount = 0;
                for (var sourceNumber = 0; sourceNumber < sources.Count; sourceNumber++)
                {
                    var rowOffset = sourceRowOffsets[sourceNumber];
                    if (rowOffset < 0) continue;
                    var source = sources[sourceNumber];
                    var sourceX = worldX - source.BaseX;
                    if (sourceX < 0 || sourceX >= source.Contribution.ImageWidth)
                    {
                        continue;
                    }
                    var sourceIndex = checked(rowOffset + sourceX * BgraFrame.BytesPerPixel);
                    if (source.Pixels[sourceIndex + 3] == 0) continue;
                    var firstValue = source.Pixels[sourceIndex];
                    var secondValue = source.Pixels[sourceIndex + 1];
                    var thirdValue = source.Pixels[sourceIndex + 2];
                    first[sampleCount] = firstValue;
                    second[sampleCount] = secondValue;
                    third[sampleCount] = thirdValue;
                    alpha[sampleCount] = source.Pixels[sourceIndex + 3];

                    var insert = sampleCount;
                    while (insert > 0 && sortedFirst[insert - 1] > firstValue)
                    {
                        sortedFirst[insert] = sortedFirst[insert - 1];
                        insert--;
                    }
                    sortedFirst[insert] = firstValue;
                    insert = sampleCount;
                    while (insert > 0 && sortedSecond[insert - 1] > secondValue)
                    {
                        sortedSecond[insert] = sortedSecond[insert - 1];
                        insert--;
                    }
                    sortedSecond[insert] = secondValue;
                    insert = sampleCount;
                    while (insert > 0 && sortedThird[insert - 1] > thirdValue)
                    {
                        sortedThird[insert] = sortedThird[insert - 1];
                        insert--;
                    }
                    sortedThird[insert] = thirdValue;
                    sampleCount++;
                }
                if (sampleCount < 3) continue;

                var index = checked(y * width + x);
                buffers.HasEnoughSamples[index] = true;
                var medianIndex = sampleCount / 2;
                var medianFirst = sortedFirst[medianIndex];
                var medianSecond = sortedSecond[medianIndex];
                var medianThird = sortedThird[medianIndex];
                var agreementCount = 0;
                var closestSample = -1;
                var closestDistance = int.MaxValue;
                for (var sample = 0; sample < sampleCount; sample++)
                {
                    if (Math.Abs(first[sample] - medianFirst) > TemporalAgreementTolerance
                        || Math.Abs(second[sample] - medianSecond) > TemporalAgreementTolerance
                        || Math.Abs(third[sample] - medianThird) > TemporalAgreementTolerance)
                    {
                        continue;
                    }
                    agreementCount++;
                    var firstDistance = first[sample] - medianFirst;
                    var secondDistance = second[sample] - medianSecond;
                    var thirdDistance = third[sample] - medianThird;
                    var distance = firstDistance * firstDistance
                                   + secondDistance * secondDistance
                                   + thirdDistance * thirdDistance;
                    if (distance <= closestDistance)
                    {
                        closestDistance = distance;
                        closestSample = sample;
                    }
                }
                if (agreementCount < 3
                    || agreementCount <= sampleCount / 2
                    || closestSample < 0)
                {
                    continue;
                }

                buffers.HasConsensus[index] = true;
                var destination = checked(index * BgraFrame.BytesPerPixel);
                buffers.Bgra[destination] = first[closestSample];
                buffers.Bgra[destination + 1] = second[closestSample];
                buffers.Bgra[destination + 2] = third[closestSample];
                buffers.Bgra[destination + 3] = alpha[closestSample];
            }
        }
        if (!buffers.BuildNeighborhoodConsensus(shouldCancel)) return null;
        return buffers;
    }

    /// <summary>
    /// Builds a world-aligned texture quilt from overlapping 32-pixel patches.
    /// Patch matching ignores uniform illumination shifts. Matching observations
    /// are averaged pixelwise and joined at minimum-error seams.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private TemporalCandidateBuffers? TemporalQuiltCandidates(
        Contribution contribution,
        AtlasPoint anchor,
        Func<bool> shouldCancel)
    {
        var placement = PlacementBounds(contribution, anchor);
        var centralMinX = checked((int)placement.X);
        var centralMaxX = checked((int)placement.Right - 1);
        var centralMinY = checked((int)placement.Y);
        var centralMaxY = checked((int)placement.Bottom - 1);
        if (centralMinX > centralMaxX || centralMinY > centralMaxY) return null;
        var originX = checked(centralMinX - QuiltOverlap);
        var originY = checked(centralMinY - QuiltOverlap);
        var width = checked(centralMaxX - centralMinX + 1 + QuiltOverlap * 2);
        var height = checked(centralMaxY - centralMinY + 1 + QuiltOverlap * 2);
        var sources = new List<DecodedSource>(contribution.TemporalWindowIds.Count);
        foreach (var id in contribution.TemporalWindowIds)
        {
            if (!contributions.TryGetValue(id, out var item)) continue;
            var pixels = DecodedPixels(id);
            if (pixels is null) continue;
            var sourcePlacement = PlacementBounds(item, anchor);
            sources.Add(new(
                item,
                pixels,
                checked((int)sourcePlacement.X),
                checked((int)sourcePlacement.Y)));
        }

        var buffers = temporalCandidateBuffers;
        buffers.Reset(width, height, originX, originY);
        if (sources.Count < 3) return buffers;

        var patchPixelCount = QuiltPatchSize * QuiltPatchSize;
        var patchBgra = new byte[checked(patchPixelCount * BgraFrame.BytesPerPixel)];
        var patchValid = new bool[patchPixelCount];
        var firstPatchX = checked(FloorDivide(originX, QuiltStride) * QuiltStride);
        var firstPatchY = checked(FloorDivide(originY, QuiltStride) * QuiltStride);
        var lastX = checked(originX + width - 1);
        var lastY = checked(originY + height - 1);

        for (var patchY = firstPatchY; patchY <= lastY; patchY += QuiltStride)
        {
            if (shouldCancel()) return null;
            for (var patchX = firstPatchX; patchX <= lastX; patchX += QuiltStride)
            {
                if (shouldCancel()) return null;
                var members = QuiltPatchMembers(patchX, patchY, sources);
                if (members is null) continue;

                Array.Clear(patchBgra);
                Array.Clear(patchValid);
                for (var localY = 0; localY < QuiltPatchSize; localY++)
                {
                    var worldY = patchY + localY;
                    for (var localX = 0; localX < QuiltPatchSize; localX++)
                    {
                        var color = QuiltPixel(
                            patchX + localX,
                            worldY,
                            members,
                            sources);
                        if (color is not { } value) continue;
                        var pixel = localY * QuiltPatchSize + localX;
                        var destination = pixel * BgraFrame.BytesPerPixel;
                        patchBgra[destination] = value.Blue;
                        patchBgra[destination + 1] = value.Green;
                        patchBgra[destination + 2] = value.Red;
                        patchBgra[destination + 3] = value.Alpha;
                        patchValid[pixel] = true;
                    }
                }

                var leftSeam = QuiltVerticalSeam(
                    patchX,
                    patchY,
                    patchBgra,
                    patchValid,
                    buffers);
                var lowerSeam = QuiltHorizontalSeam(
                    patchX,
                    patchY,
                    patchBgra,
                    patchValid,
                    buffers);
                for (var localY = 0; localY < QuiltPatchSize; localY++)
                {
                    var bufferY = patchY + localY - originY;
                    if (bufferY < 0 || bufferY >= height) continue;
                    for (var localX = 0; localX < QuiltPatchSize; localX++)
                    {
                        var bufferX = patchX + localX - originX;
                        if (bufferX < 0 || bufferX >= width) continue;
                        var patchPixel = localY * QuiltPatchSize + localX;
                        if (!patchValid[patchPixel]) continue;
                        var bufferPixel = bufferY * width + bufferX;
                        var useNew = !buffers.HasConsensus[bufferPixel];
                        if (!useNew)
                        {
                            useNew = true;
                            if (localX < QuiltOverlap)
                            {
                                useNew = leftSeam is not null
                                         && localX >= leftSeam[localY];
                            }
                            if (useNew && localY < QuiltOverlap)
                            {
                                useNew = lowerSeam is not null
                                         && localY >= lowerSeam[localX];
                            }
                        }
                        if (!useNew) continue;
                        var source = patchPixel * BgraFrame.BytesPerPixel;
                        var destination = bufferPixel * BgraFrame.BytesPerPixel;
                        buffers.Bgra[destination] = patchBgra[source];
                        buffers.Bgra[destination + 1] = patchBgra[source + 1];
                        buffers.Bgra[destination + 2] = patchBgra[source + 2];
                        buffers.Bgra[destination + 3] = patchBgra[source + 3];
                        buffers.HasEnoughSamples[bufferPixel] = true;
                        buffers.HasConsensus[bufferPixel] = true;
                    }
                }
            }
        }
        return buffers;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static IReadOnlyList<QuiltPatchMember>? QuiltPatchMembers(
        int patchX,
        int patchY,
        IReadOnlyList<DecodedSource> sources)
    {
        var descriptors = new List<QuiltPatchMember>(sources.Count);
        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            var blueTotal = 0;
            var greenTotal = 0;
            var redTotal = 0;
            var sampleCount = 0;
            for (var localY = QuiltDescriptorStep / 2;
                 localY < QuiltPatchSize;
                 localY += QuiltDescriptorStep)
            for (var localX = QuiltDescriptorStep / 2;
                 localX < QuiltPatchSize;
                 localX += QuiltDescriptorStep)
            {
                var index = SourcePixelIndex(source, patchX + localX, patchY + localY);
                if (index is not { } pixel) continue;
                blueTotal += source.Pixels[pixel];
                greenTotal += source.Pixels[pixel + 1];
                redTotal += source.Pixels[pixel + 2];
                sampleCount++;
            }
            if (sampleCount < QuiltMinimumDescriptorSamples) continue;
            var meanBlue = blueTotal / sampleCount;
            var meanGreen = greenTotal / sampleCount;
            var meanRed = redTotal / sampleCount;
            var textureTotal = 0;
            for (var localY = QuiltDescriptorStep / 2;
                 localY < QuiltPatchSize;
                 localY += QuiltDescriptorStep)
            for (var localX = QuiltDescriptorStep / 2;
                 localX < QuiltPatchSize;
                 localX += QuiltDescriptorStep)
            {
                var index = SourcePixelIndex(source, patchX + localX, patchY + localY);
                if (index is not { } pixel) continue;
                textureTotal += Math.Abs(source.Pixels[pixel] - meanBlue);
                textureTotal += Math.Abs(source.Pixels[pixel + 1] - meanGreen);
                textureTotal += Math.Abs(source.Pixels[pixel + 2] - meanRed);
            }
            descriptors.Add(new(
                sourceIndex,
                meanBlue,
                meanGreen,
                meanRed,
                (meanBlue + meanGreen + meanRed) / 3,
                textureTotal / (sampleCount * 3)));
        }
        if (descriptors.Count < 3) return null;

        var descriptorCount = descriptors.Count;
        var errors = Enumerable.Repeat(int.MaxValue, descriptorCount * descriptorCount)
            .ToArray();
        var support = Enumerable.Repeat(1, descriptorCount).ToArray();
        for (var index = 0; index < descriptorCount; index++)
            errors[index * descriptorCount + index] = 0;
        for (var left = 0; left < descriptorCount; left++)
        for (var right = left + 1; right < descriptorCount; right++)
        {
            var error = QuiltPatchError(
                descriptors[left],
                descriptors[right],
                patchX,
                patchY,
                sources);
            if (error is not { } value) continue;
            errors[left * descriptorCount + right] = value;
            errors[right * descriptorCount + left] = value;
            if (value > QuiltTextureTolerance) continue;
            support[left]++;
            support[right]++;
        }

        var minimumSupport = descriptorCount / 2 + 1;
        var selectedIndex = -1;
        for (var index = 0; index < descriptorCount; index++)
        {
            if (support[index] < 3 || support[index] < minimumSupport) continue;
            if (selectedIndex < 0
                || descriptors[index].Brightness > descriptors[selectedIndex].Brightness
                || (descriptors[index].Brightness == descriptors[selectedIndex].Brightness
                    && support[index] > support[selectedIndex])
                || (descriptors[index].Brightness == descriptors[selectedIndex].Brightness
                    && support[index] == support[selectedIndex]
                    && descriptors[index].SourceIndex
                    > descriptors[selectedIndex].SourceIndex))
            {
                selectedIndex = index;
            }
        }
        if (selectedIndex < 0) return null;
        var selected = descriptors[selectedIndex];
        if (selected.Brightness < 8 && selected.TextureEnergy < 2) return null;

        var peers = new List<QuiltPatchMember>(descriptorCount - 1);
        for (var index = 0; index < descriptorCount; index++)
        {
            if (index == selectedIndex
                || errors[selectedIndex * descriptorCount + index] > QuiltTextureTolerance)
            {
                continue;
            }
            peers.Add(descriptors[index]);
        }
        peers.Sort(static (left, right) =>
        {
            var brightness = right.Brightness.CompareTo(left.Brightness);
            return brightness != 0
                ? brightness
                : right.SourceIndex.CompareTo(left.SourceIndex);
        });
        peers.Insert(0, selected);
        return peers;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static int? QuiltPatchError(
        QuiltPatchMember left,
        QuiltPatchMember right,
        int patchX,
        int patchY,
        IReadOnlyList<DecodedSource> sources)
    {
        if (Math.Abs(left.MeanBlue - right.MeanBlue) > QuiltIlluminationTolerance
            || Math.Abs(left.MeanGreen - right.MeanGreen) > QuiltIlluminationTolerance
            || Math.Abs(left.MeanRed - right.MeanRed) > QuiltIlluminationTolerance)
        {
            return null;
        }
        var leftSource = sources[left.SourceIndex];
        var rightSource = sources[right.SourceIndex];
        var total = 0;
        var sampleCount = 0;
        for (var localY = QuiltDescriptorStep / 2;
             localY < QuiltPatchSize;
             localY += QuiltDescriptorStep)
        for (var localX = QuiltDescriptorStep / 2;
             localX < QuiltPatchSize;
             localX += QuiltDescriptorStep)
        {
            var worldX = patchX + localX;
            var worldY = patchY + localY;
            var leftIndex = SourcePixelIndex(leftSource, worldX, worldY);
            var rightIndex = SourcePixelIndex(rightSource, worldX, worldY);
            if (leftIndex is not { } leftPixel || rightIndex is not { } rightPixel)
                continue;
            total += Math.Abs(
                leftSource.Pixels[leftPixel] - left.MeanBlue
                - rightSource.Pixels[rightPixel] + right.MeanBlue);
            total += Math.Abs(
                leftSource.Pixels[leftPixel + 1] - left.MeanGreen
                - rightSource.Pixels[rightPixel + 1] + right.MeanGreen);
            total += Math.Abs(
                leftSource.Pixels[leftPixel + 2] - left.MeanRed
                - rightSource.Pixels[rightPixel + 2] + right.MeanRed);
            sampleCount++;
        }
        return sampleCount >= QuiltMinimumDescriptorSamples
            ? total / (sampleCount * 3)
            : null;
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    private static (byte Blue, byte Green, byte Red, byte Alpha)? QuiltPixel(
        int worldX,
        int worldY,
        IReadOnlyList<QuiltPatchMember> members,
        IReadOnlyList<DecodedSource> sources)
    {
        var blueTotal = 0;
        var greenTotal = 0;
        var redTotal = 0;
        var alphaTotal = 0;
        var sampleCount = 0;
        foreach (var member in members)
        {
            var source = sources[member.SourceIndex];
            var index = SourcePixelIndex(source, worldX, worldY);
            if (index is not { } pixel) continue;
            blueTotal += source.Pixels[pixel];
            greenTotal += source.Pixels[pixel + 1];
            redTotal += source.Pixels[pixel + 2];
            alphaTotal += source.Pixels[pixel + 3];
            sampleCount++;
        }
        if (sampleCount < 3) return null;
        var rounding = sampleCount / 2;
        return (
            (byte)((blueTotal + rounding) / sampleCount),
            (byte)((greenTotal + rounding) / sampleCount),
            (byte)((redTotal + rounding) / sampleCount),
            (byte)((alphaTotal + rounding) / sampleCount));
    }

    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int? SourcePixelIndex(
        DecodedSource source,
        int worldX,
        int worldY)
    {
        var sourceX = worldX - source.BaseX;
        var sourceY = worldY - source.BaseY;
        if (sourceX < 0 || sourceX >= source.Contribution.ImageWidth
            || sourceY < 0 || sourceY >= source.Contribution.ImageHeight)
        {
            return null;
        }
        var topY = source.Contribution.ImageHeight - 1 - sourceY;
        var index = checked(
            (topY * source.Contribution.ImageWidth + sourceX)
            * BgraFrame.BytesPerPixel);
        return source.Pixels[index + 3] > 0 ? index : null;
    }

    private static int[]? QuiltVerticalSeam(
        int patchX,
        int patchY,
        byte[] patchBgra,
        bool[] patchValid,
        TemporalCandidateBuffers buffers)
    {
        const int rows = QuiltPatchSize;
        const int columns = QuiltOverlap;
        var cost = new int[rows * columns];
        var comparable = false;
        for (var row = 0; row < rows; row++)
        {
            var bufferY = patchY + row - buffers.OriginY;
            for (var column = 0; column < columns; column++)
            {
                var bufferX = patchX + column - buffers.OriginX;
                var patchPixel = row * QuiltPatchSize + column;
                var index = row * columns + column;
                if (bufferX < 0 || bufferX >= buffers.Width
                    || bufferY < 0 || bufferY >= buffers.Height
                    || !patchValid[patchPixel])
                {
                    continue;
                }
                var bufferPixel = bufferY * buffers.Width + bufferX;
                if (!buffers.HasConsensus[bufferPixel]) continue;
                comparable = true;
                cost[index] = ColorDistance(
                    patchBgra,
                    patchPixel * BgraFrame.BytesPerPixel,
                    buffers.Bgra,
                    bufferPixel * BgraFrame.BytesPerPixel);
            }
        }
        return comparable ? MinimumVerticalSeam(cost, rows, columns) : null;
    }

    private static int[]? QuiltHorizontalSeam(
        int patchX,
        int patchY,
        byte[] patchBgra,
        bool[] patchValid,
        TemporalCandidateBuffers buffers)
    {
        const int rows = QuiltOverlap;
        const int columns = QuiltPatchSize;
        var cost = new int[rows * columns];
        var comparable = false;
        for (var row = 0; row < rows; row++)
        {
            var bufferY = patchY + row - buffers.OriginY;
            for (var column = 0; column < columns; column++)
            {
                var bufferX = patchX + column - buffers.OriginX;
                var patchPixel = row * QuiltPatchSize + column;
                var index = row * columns + column;
                if (bufferX < 0 || bufferX >= buffers.Width
                    || bufferY < 0 || bufferY >= buffers.Height
                    || !patchValid[patchPixel])
                {
                    continue;
                }
                var bufferPixel = bufferY * buffers.Width + bufferX;
                if (!buffers.HasConsensus[bufferPixel]) continue;
                comparable = true;
                cost[index] = ColorDistance(
                    patchBgra,
                    patchPixel * BgraFrame.BytesPerPixel,
                    buffers.Bgra,
                    bufferPixel * BgraFrame.BytesPerPixel);
            }
        }
        return comparable ? MinimumHorizontalSeam(cost, rows, columns) : null;
    }

    private TileRenderResult RenderTile(
        AtlasTileCoordinate coordinate,
        Func<bool> shouldCancel)
    {
        if (UsesTemporalEvidence) return RenderTemporalTile(coordinate, shouldCancel);
        if (shouldCancel()) return TileRenderResult.Cancelled;
        if (!contributionIdsByTile.TryGetValue(coordinate, out var ids) || ids.Count == 0)
        {
            return TileRenderResult.Empty;
        }
        if (anchorPosition is not { } anchor) return TileRenderResult.Empty;
        var pixels = new byte[checked(TileSize * TileSize * BgraFrame.BytesPerPixel)];
        var selected = new HashSet<long>();
        var observed = false;
        foreach (var contribution in ids.Select(id => contributions[id])
                     .OrderBy(value => value.InsertionOrder)
                     .ThenBy(value => value.ObservationId))
        {
            if (shouldCancel()) return TileRenderResult.Cancelled;
            var source = SourceFrame(contribution.ObservationId);
            if (source is null
                || source.Width != contribution.ImageWidth
                || source.Height != contribution.ImageHeight)
            {
                continue;
            }
            lastRasterizedContributionCount++;
            var contributed = RasterNewestContribution(
                contribution, source, coordinate, anchor, pixels, shouldCancel);
            if (contributed is null) return TileRenderResult.Cancelled;
            observed |= contributed.Value;
            if (contributed.Value) selected.Add(contribution.ObservationId);
        }
        return observed
            ? TileRenderResult.FromTile(MakeTile(coordinate, pixels, selected))
            : TileRenderResult.Empty;
    }

    private TileRenderResult Composite(
        Contribution contribution,
        BgraFrame source,
        AtlasTileCoordinate coordinate,
        LiveTiledAtlasTile? existing,
        AtlasPoint anchor,
        Func<bool> shouldCancel)
    {
        if (shouldCancel()) return TileRenderResult.Cancelled;
        var pixels = existing?.Image.Pixels.ToArray()
                     ?? new byte[checked(TileSize * TileSize * BgraFrame.BytesPerPixel)];
        HashSet<long> selected = existing is null
            ? []
            : existing.ContributionIds.ToHashSet();
        lastRasterizedContributionCount++;
        var contributed = RasterNewestContribution(
            contribution, source, coordinate, anchor, pixels, shouldCancel);
        if (contributed is null) return TileRenderResult.Cancelled;
        if (!contributed.Value)
        {
            return existing is null ? TileRenderResult.Empty : TileRenderResult.FromTile(existing);
        }
        selected.Add(contribution.ObservationId);
        if (shouldCancel()) return TileRenderResult.Cancelled;
        return TileRenderResult.FromTile(MakeTile(coordinate, pixels, selected));
    }

    private bool? RasterNewestContribution(
        Contribution contribution,
        BgraFrame source,
        AtlasTileCoordinate coordinate,
        AtlasPoint anchor,
        byte[] destination,
        Func<bool> shouldCancel)
    {
        var placement = PlacementBounds(contribution, anchor);
        var baseX = checked((int)placement.X);
        var baseY = checked((int)placement.Y);
        var tileMinX = checked(coordinate.X * TileSize);
        var tileMinY = checked(coordinate.Y * TileSize);
        var minSourceX = Math.Max(0, tileMinX - baseX);
        var maxSourceX = Math.Min(
            contribution.ImageWidth - 1,
            checked(tileMinX + TileSize - 1 - baseX));
        var minSourceY = Math.Max(0, tileMinY - baseY);
        var maxSourceY = Math.Min(
            contribution.ImageHeight - 1,
            checked(tileMinY + TileSize - 1 - baseY));
        if (minSourceX > maxSourceX || minSourceY > maxSourceY) return false;
        if (shouldCancel()) return null;
        var seams = pixelPolicy == LiveTiledAtlasPixelPolicy.NewestQuilted
            ? ComputeNewestQuiltSeams(
                contribution,
                source,
                coordinate,
                destination,
                baseX,
                baseY,
                minSourceX,
                maxSourceX,
                minSourceY,
                maxSourceY)
            : null;

        var wrote = false;
        for (var sourceY = minSourceY; sourceY <= maxSourceY; sourceY++)
        {
            if (shouldCancel()) return null;
            var sourceTopY = contribution.ImageHeight - 1 - sourceY;
            for (var sourceX = minSourceX; sourceX <= maxSourceX; sourceX++)
            {
                var sourceIndex = checked(sourceTopY * source.Stride
                                          + sourceX * BgraFrame.BytesPerPixel);
                if (source.Pixels[sourceIndex + 3] == 0) continue;
                var localX = baseX + sourceX - tileMinX;
                var localY = baseY + sourceY - tileMinY;
                var target = checked(
                    ((TileSize - 1 - localY) * TileSize + localX)
                    * BgraFrame.BytesPerPixel);
                if (!ShouldCopyPixel(source.Pixels, sourceIndex, destination, target)) continue;
                if (seams is not null
                    && destination[target + 3] > 0
                    && !NewestQuiltUsesNewPixel(
                        sourceX,
                        sourceY,
                        contribution.ImageWidth,
                        contribution.ImageHeight,
                        seams))
                {
                    continue;
                }
                destination[target] = source.Pixels[sourceIndex];
                destination[target + 1] = source.Pixels[sourceIndex + 1];
                destination[target + 2] = source.Pixels[sourceIndex + 2];
                destination[target + 3] = source.Pixels[sourceIndex + 3];
                wrote = true;
            }
        }
        return wrote;
    }

    private static NewestQuiltSeams ComputeNewestQuiltSeams(
        Contribution contribution,
        BgraFrame source,
        AtlasTileCoordinate coordinate,
        byte[] destination,
        int baseX,
        int baseY,
        int minSourceX,
        int maxSourceX,
        int minSourceY,
        int maxSourceY)
    {
        var leftMax = Math.Min(maxSourceX, NewestQuiltOverlap - 1);
        var left = minSourceX <= leftMax
            ? NewestVerticalEdgeSeam(
                minSourceX,
                leftMax,
                static (_, band) => band,
                contribution,
                source,
                coordinate,
                destination,
                baseX,
                baseY,
                minSourceY,
                maxSourceY)
            : null;

        var rightMin = Math.Max(0, contribution.ImageWidth - 1 - maxSourceX);
        var rightMax = Math.Min(
            NewestQuiltOverlap - 1,
            contribution.ImageWidth - 1 - minSourceX);
        var right = rightMin <= rightMax
            ? NewestVerticalEdgeSeam(
                rightMin,
                rightMax,
                static (width, band) => width - 1 - band,
                contribution,
                source,
                coordinate,
                destination,
                baseX,
                baseY,
                minSourceY,
                maxSourceY)
            : null;

        var bottomMax = Math.Min(maxSourceY, NewestQuiltOverlap - 1);
        var bottom = minSourceY <= bottomMax
            ? NewestHorizontalEdgeSeam(
                minSourceY,
                bottomMax,
                static (_, band) => band,
                contribution,
                source,
                coordinate,
                destination,
                baseX,
                baseY,
                minSourceX,
                maxSourceX)
            : null;

        var topMin = Math.Max(0, contribution.ImageHeight - 1 - maxSourceY);
        var topMax = Math.Min(
            NewestQuiltOverlap - 1,
            contribution.ImageHeight - 1 - minSourceY);
        var top = topMin <= topMax
            ? NewestHorizontalEdgeSeam(
                topMin,
                topMax,
                static (height, band) => height - 1 - band,
                contribution,
                source,
                coordinate,
                destination,
                baseX,
                baseY,
                minSourceX,
                maxSourceX)
            : null;

        return new(minSourceX, minSourceY, left, right, bottom, top);
    }

    private static int[]? NewestVerticalEdgeSeam(
        int bandStart,
        int bandEnd,
        Func<int, int, int> sourceXForBand,
        Contribution contribution,
        BgraFrame source,
        AtlasTileCoordinate coordinate,
        byte[] destination,
        int baseX,
        int baseY,
        int minSourceY,
        int maxSourceY)
    {
        var rows = maxSourceY - minSourceY + 1;
        var columns = bandEnd - bandStart + 1;
        var cost = new int[checked(rows * columns)];
        var comparable = false;
        var tileMinX = checked(coordinate.X * TileSize);
        var tileMinY = checked(coordinate.Y * TileSize);
        for (var row = 0; row < rows; row++)
        {
            var sourceY = minSourceY + row;
            var sourceTopY = contribution.ImageHeight - 1 - sourceY;
            for (var band = bandStart; band <= bandEnd; band++)
            {
                var column = band - bandStart;
                var sourceX = sourceXForBand(contribution.ImageWidth, band);
                var sourceIndex = checked(sourceTopY * source.Stride
                                          + sourceX * BgraFrame.BytesPerPixel);
                if (source.Pixels[sourceIndex + 3] == 0) continue;
                var localX = baseX + sourceX - tileMinX;
                var localY = baseY + sourceY - tileMinY;
                if (localX < 0 || localX >= TileSize || localY < 0 || localY >= TileSize)
                    continue;
                var target = checked(
                    ((TileSize - 1 - localY) * TileSize + localX)
                    * BgraFrame.BytesPerPixel);
                if (destination[target + 3] == 0) continue;
                comparable = true;
                cost[row * columns + column] = ColorDistance(
                    source.Pixels, sourceIndex, destination, target);
            }
        }
        if (!comparable) return null;
        var seam = MinimumVerticalSeam(cost, rows, columns);
        for (var index = 0; index < seam.Length; index++) seam[index] += bandStart;
        return seam;
    }

    private static int[]? NewestHorizontalEdgeSeam(
        int bandStart,
        int bandEnd,
        Func<int, int, int> sourceYForBand,
        Contribution contribution,
        BgraFrame source,
        AtlasTileCoordinate coordinate,
        byte[] destination,
        int baseX,
        int baseY,
        int minSourceX,
        int maxSourceX)
    {
        var rows = bandEnd - bandStart + 1;
        var columns = maxSourceX - minSourceX + 1;
        var cost = new int[checked(rows * columns)];
        var comparable = false;
        var tileMinX = checked(coordinate.X * TileSize);
        var tileMinY = checked(coordinate.Y * TileSize);
        for (var band = bandStart; band <= bandEnd; band++)
        {
            var row = band - bandStart;
            var sourceY = sourceYForBand(contribution.ImageHeight, band);
            var sourceTopY = contribution.ImageHeight - 1 - sourceY;
            for (var column = 0; column < columns; column++)
            {
                var sourceX = minSourceX + column;
                var sourceIndex = checked(sourceTopY * source.Stride
                                          + sourceX * BgraFrame.BytesPerPixel);
                if (source.Pixels[sourceIndex + 3] == 0) continue;
                var localX = baseX + sourceX - tileMinX;
                var localY = baseY + sourceY - tileMinY;
                if (localX < 0 || localX >= TileSize || localY < 0 || localY >= TileSize)
                    continue;
                var target = checked(
                    ((TileSize - 1 - localY) * TileSize + localX)
                    * BgraFrame.BytesPerPixel);
                if (destination[target + 3] == 0) continue;
                comparable = true;
                cost[row * columns + column] = ColorDistance(
                    source.Pixels, sourceIndex, destination, target);
            }
        }
        if (!comparable) return null;
        var seam = MinimumHorizontalSeam(cost, rows, columns);
        for (var index = 0; index < seam.Length; index++) seam[index] += bandStart;
        return seam;
    }

    private static bool NewestQuiltUsesNewPixel(
        int sourceX,
        int sourceY,
        int imageWidth,
        int imageHeight,
        NewestQuiltSeams seams)
    {
        var row = sourceY - seams.MinSourceY;
        var column = sourceX - seams.MinSourceX;
        if (sourceX < NewestQuiltOverlap
            && seams.Left is { } left
            && sourceX < left[row])
        {
            return false;
        }
        var rightBand = imageWidth - 1 - sourceX;
        if (rightBand < NewestQuiltOverlap
            && seams.Right is { } right
            && rightBand < right[row])
        {
            return false;
        }
        if (sourceY < NewestQuiltOverlap
            && seams.Bottom is { } bottom
            && sourceY < bottom[column])
        {
            return false;
        }
        var topBand = imageHeight - 1 - sourceY;
        if (topBand < NewestQuiltOverlap
            && seams.Top is { } top
            && topBand < top[column])
        {
            return false;
        }
        return true;
    }

    private static int[] MinimumVerticalSeam(int[] cost, int rows, int columns)
    {
        var accumulated = cost.ToArray();
        var parent = new int[checked(rows * columns)];
        for (var row = 1; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            var previousColumn = column;
            var previousCost = accumulated[(row - 1) * columns + column];
            if (column > 0
                && accumulated[(row - 1) * columns + column - 1] < previousCost)
            {
                previousColumn = column - 1;
                previousCost = accumulated[(row - 1) * columns + column - 1];
            }
            if (column + 1 < columns
                && accumulated[(row - 1) * columns + column + 1] < previousCost)
            {
                previousColumn = column + 1;
                previousCost = accumulated[(row - 1) * columns + column + 1];
            }
            var index = row * columns + column;
            accumulated[index] += previousCost;
            parent[index] = previousColumn;
        }

        var finalRow = rows - 1;
        var finalColumn = 0;
        for (var column = 1; column < columns; column++)
        {
            if (accumulated[finalRow * columns + column]
                < accumulated[finalRow * columns + finalColumn])
            {
                finalColumn = column;
            }
        }
        var seam = new int[rows];
        for (var row = finalRow; row >= 0; row--)
        {
            seam[row] = finalColumn;
            if (row > 0) finalColumn = parent[row * columns + finalColumn];
        }
        return seam;
    }

    private static int[] MinimumHorizontalSeam(int[] cost, int rows, int columns)
    {
        var transposed = new int[checked(rows * columns)];
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < columns; column++)
        {
            transposed[column * rows + row] = cost[row * columns + column];
        }
        return MinimumVerticalSeam(transposed, columns, rows);
    }

    private static int ColorDistance(
        byte[] left,
        int leftIndex,
        byte[] right,
        int rightIndex)
    {
        var first = left[leftIndex] - right[rightIndex];
        var second = left[leftIndex + 1] - right[rightIndex + 1];
        var third = left[leftIndex + 2] - right[rightIndex + 2];
        return first * first + second * second + third * third;
    }

    private bool ShouldCopyPixel(
        byte[] source,
        int sourceIndex,
        byte[] destination,
        int destinationIndex)
    {
        if (destination[destinationIndex + 3] == 0) return true;
        if (pixelPolicy != LiveTiledAtlasPixelPolicy.PreserveBrighter) return true;
        var sourcePeak = Math.Max(source[sourceIndex],
            Math.Max(source[sourceIndex + 1], source[sourceIndex + 2]));
        var destinationPeak = Math.Max(destination[destinationIndex],
            Math.Max(destination[destinationIndex + 1], destination[destinationIndex + 2]));
        return sourcePeak >= destinationPeak;
    }

    private LiveTiledAtlasTile MakeTile(
        AtlasTileCoordinate coordinate,
        byte[] pixels,
        HashSet<long> selected)
    {
        var stableId = StableId(coordinate);
        var image = new BgraFrame(
            TileSize,
            TileSize,
            checked(TileSize * BgraFrame.BytesPerPixel),
            pixels,
            unchecked((long)(published.Revision + 1)),
            DateTimeOffset.UnixEpoch);
        return new(
            stableId,
            stableId,
            coordinate.X,
            coordinate.Y,
            image,
            TileBounds(coordinate),
            selected.Order().ToArray());
    }

    private HashSet<AtlasTileCoordinate> ProjectedTiles(
        Contribution contribution,
        AtlasPoint anchor)
    {
        var placement = PlacementBounds(contribution, anchor);
        if (!placement.IsFinite || placement.IsEmpty
            || placement.X < int.MinValue || placement.Right > int.MaxValue
            || placement.Y < int.MinValue || placement.Bottom > int.MaxValue)
        {
            return [];
        }
        var minX = checked((int)placement.X);
        var minY = checked((int)placement.Y);
        var maxX = checked((int)placement.Right - 1);
        var maxY = checked((int)placement.Bottom - 1);
        if (minX > maxX || minY > maxY) return [];
        var result = new HashSet<AtlasTileCoordinate>();
        for (var tileY = FloorDivide(minY, TileSize); tileY <= FloorDivide(maxY, TileSize); tileY++)
        for (var tileX = FloorDivide(minX, TileSize); tileX <= FloorDivide(maxX, TileSize); tileX++)
        {
            result.Add(new(tileX, tileY));
        }
        return result;
    }

    private static StencilRect PlacementBounds(Contribution contribution, AtlasPoint anchor)
    {
        var scale = contribution.ImageWidth / Math.Max(1, contribution.SolveWidth);
        var offsetX = (contribution.CameraPosition.X - anchor.X) * scale;
        var offsetY = (contribution.CameraPosition.Y - anchor.Y) * scale;
        if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY)) return default;
        var minX = Math.Floor(offsetX);
        var minY = Math.Floor(offsetY);
        var maxX = Math.Ceiling(offsetX + contribution.ImageWidth);
        var maxY = Math.Ceiling(offsetY + contribution.ImageHeight);
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    private StencilRect RegisteredContentBounds()
    {
        if (anchorPosition is not { } anchor || contributions.Count == 0) return default;
        var hasBounds = false;
        var result = default(StencilRect);
        foreach (var contribution in contributions.Values)
        {
            var placement = PlacementBounds(contribution, anchor);
            if (!placement.IsFinite || placement.IsEmpty) continue;
            result = hasBounds ? Union(result, placement) : placement;
            hasBounds = true;
        }
        return hasBounds ? result : default;
    }

    private static StencilRect BoundsOfTiles(IReadOnlyList<LiveTiledAtlasTile> tiles)
    {
        if (tiles.Count == 0) return default;
        var result = tiles[0].WorldBounds;
        for (var index = 1; index < tiles.Count; index++)
        {
            result = Union(result, tiles[index].WorldBounds);
        }
        return result;
    }

    private static StencilRect Union(StencilRect left, StencilRect right)
    {
        var minX = Math.Min(left.X, right.X);
        var minY = Math.Min(left.Y, right.Y);
        var maxX = Math.Max(left.Right, right.Right);
        var maxY = Math.Max(left.Bottom, right.Bottom);
        return new(minX, minY, maxX - minX, maxY - minY);
    }

    private static StencilRect TileBounds(AtlasTileCoordinate coordinate) => new(
        checked(coordinate.X * TileSize),
        checked(coordinate.Y * TileSize),
        TileSize,
        TileSize);

    private static long StableId(AtlasTileCoordinate coordinate) => unchecked(
        (long)(((ulong)(uint)coordinate.X << 32) | (uint)coordinate.Y));

    private void Link(Contribution contribution)
    {
        foreach (var coordinate in contribution.TileCoordinates)
        {
            if (!contributionIdsByTile.TryGetValue(coordinate, out var ids))
            {
                ids = [];
                contributionIdsByTile.Add(coordinate, ids);
            }
            ids.Add(contribution.ObservationId);
        }
    }

    private void Unlink(Contribution contribution)
    {
        foreach (var coordinate in contribution.TileCoordinates)
        {
            if (!contributionIdsByTile.TryGetValue(coordinate, out var ids)) continue;
            ids.Remove(contribution.ObservationId);
            if (ids.Count == 0) contributionIdsByTile.Remove(coordinate);
        }
    }

    private BgraFrame? SourceFrame(long observationId)
    {
        if (sourceCache.TryGetValue(observationId, out var cached))
        {
            TouchCache(observationId);
            return cached;
        }
        var supplied = sourceProvider?.Invoke(observationId);
        if (supplied is null) return null;
        var owned = PackFrame(supplied);
        Cache(owned, observationId);
        return owned;
    }

    private byte[]? DecodedPixels(long observationId, BgraFrame? suppliedFrame = null)
    {
        if (decodedSourceCache.TryGetValue(observationId, out var cached))
        {
            TouchDecodedCache(observationId);
            return cached;
        }
        var frame = suppliedFrame ?? SourceFrame(observationId);
        if (frame is null) return null;
        var pixels = frame.Pixels.ToArray();
        decodedSourceCache[observationId] = pixels;
        TouchDecodedCache(observationId);
        while (decodedSourceCacheOrder.Count > TemporalWindowCapacity)
        {
            var evicted = decodedSourceCacheOrder[0];
            decodedSourceCacheOrder.RemoveAt(0);
            decodedSourceCache.Remove(evicted);
        }
        return pixels;
    }

    private void TouchDecodedCache(long observationId)
    {
        decodedSourceCacheOrder.Remove(observationId);
        decodedSourceCacheOrder.Add(observationId);
    }

    private void UpdateTemporalDiagnostics(double elapsedMilliseconds)
    {
        temporalDiagnostics = new(
            decodedSourceCache.Count,
            decodedSourceCache.Values.Sum(value => (long)value.Length),
            Math.Max(0, elapsedMilliseconds),
            temporalWork.ConsensusPixelCount,
            temporalWork.ImmediatePixelCount);
    }

    private void Cache(BgraFrame frame, long observationId)
    {
        if (sourceCacheLimit <= 0) return;
        sourceCache[observationId] = frame;
        TouchCache(observationId);
        while (sourceCacheOrder.Count > sourceCacheLimit)
        {
            var evicted = sourceCacheOrder[0];
            sourceCacheOrder.RemoveAt(0);
            sourceCache.Remove(evicted);
        }
    }

    private void TouchCache(long observationId)
    {
        sourceCacheOrder.Remove(observationId);
        sourceCacheOrder.Add(observationId);
    }

    private MutableState CaptureMutableState() => new(
        contributions.ToDictionary(pair => pair.Key, pair => CloneContribution(pair.Value)),
        contributionIdsByTile.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToHashSet()),
        sourceCache.ToDictionary(pair => pair.Key, pair => pair.Value),
        [.. sourceCacheOrder],
        decodedSourceCache.ToDictionary(pair => pair.Key, pair => pair.Value),
        [.. decodedSourceCacheOrder],
        temporalContext,
        [.. temporalHistoryIds],
        observationIdByCaptureKey.ToDictionary(pair => pair.Key, pair => pair.Value),
        temporalWork,
        temporalDiagnostics,
        anchorPosition,
        nextInsertionOrder);

    private void RestoreMutableState(MutableState state)
    {
        contributions = state.Contributions;
        contributionIdsByTile = state.ContributionIdsByTile;
        sourceCache = state.SourceCache;
        sourceCacheOrder = state.SourceCacheOrder;
        decodedSourceCache = state.DecodedSourceCache;
        decodedSourceCacheOrder = state.DecodedSourceCacheOrder;
        temporalContext = state.TemporalContext;
        temporalHistoryIds = state.TemporalHistoryIds;
        observationIdByCaptureKey = state.ObservationIdByCaptureKey;
        temporalWork = state.TemporalWork;
        temporalDiagnostics = state.TemporalDiagnostics;
        anchorPosition = state.AnchorPosition;
        nextInsertionOrder = state.NextInsertionOrder;
    }

    private static Contribution CloneContribution(Contribution value) =>
        value with { TileCoordinates = value.TileCoordinates.ToHashSet() };

    private static LiveTiledAtlasSnapshot CloneSnapshot(LiveTiledAtlasSnapshot value) => new(
        value.Tiles.Select(tile => tile with
        {
            Image = PackFrame(tile.Image),
            ContributionIds = tile.ContributionIds.ToArray()
        }).ToArray(),
        value.Revision,
        value.TotalBounds,
        value.ContentBounds);

    private static BgraFrame PackFrame(BgraFrame frame)
    {
        var stride = checked(frame.Width * BgraFrame.BytesPerPixel);
        var pixels = new byte[checked(stride * frame.Height)];
        for (var y = 0; y < frame.Height; y++)
        {
            frame.Row(y).CopyTo(pixels.AsSpan(y * stride, stride));
        }
        return new(
            frame.Width,
            frame.Height,
            stride,
            pixels,
            frame.CaptureId,
            frame.ObservedAt);
    }

    private static bool IsFinite(AtlasPoint point) =>
        double.IsFinite(point.X) && double.IsFinite(point.Y);

    private static bool IsFinite(AtlasPoint? point) => point is { } value && IsFinite(value);

    private static int FloorDivide(int value, int divisor)
    {
        var quotient = Math.DivRem(value, divisor, out var remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
