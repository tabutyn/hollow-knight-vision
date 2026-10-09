namespace HollowKnightVision.Windows.Core;

public readonly record struct AtlasPoint(double X, double Y);

public readonly record struct PixelSpan
{
    public PixelSpan(int start, int endInclusive)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (endInclusive < start) throw new ArgumentOutOfRangeException(nameof(endInclusive));
        Start = start;
        EndInclusive = endInclusive;
    }

    public int Start { get; }
    public int EndInclusive { get; }
}

public readonly record struct CleanFloorLine(int Row, PixelSpan XSpan);

public readonly record struct GroundHypothesisAtlasLine(
    int SegmentId,
    AtlasPoint AtlasStart,
    AtlasPoint AtlasEnd);

/// <summary>
/// Chooses which stable tracker lines are semantic ground. Evidence follows a
/// reassociated segment only when its replacement occupies the same atlas line.
/// </summary>
public sealed class GroundSemanticAtlas
{
    private readonly Dictionary<int, Record> records = [];

    public void Reset() => records.Clear();

    public IReadOnlyList<GroundHypothesisAtlasLine> Update(
        IReadOnlyList<GroundHypothesisAtlasLine> trackingLines,
        IReadOnlyList<CleanFloorLine> semanticLines,
        AtlasPoint? camera,
        int frameWidth,
        double atlasHeight,
        bool poseVerified)
    {
        ArgumentNullException.ThrowIfNull(trackingLines);
        ArgumentNullException.ThrowIfNull(semanticLines);
        if (frameWidth <= 0) throw new ArgumentOutOfRangeException(nameof(frameWidth));
        if (!double.IsFinite(atlasHeight)) throw new ArgumentOutOfRangeException(nameof(atlasHeight));
        if (atlasHeight <= 0) return [];

        var previous = records.Values.ToArray();
        foreach (var line in trackingLines)
        {
            Validate(line);
            if (records.ContainsKey(line.SegmentId)) continue;
            var inherited = previous.FirstOrDefault(candidate =>
                VerticallyMatches(line, candidate.Geometry)
                && HorizontalOverlap(line, candidate.Geometry) >= 16);
            records[line.SegmentId] = inherited is null
                ? new Record(line)
                : new Record(
                    line,
                    inherited.VisibleFrames,
                    inherited.SupportedFrames);
        }

        if (poseVerified && camera is { } cameraPosition)
        {
            if (!double.IsFinite(cameraPosition.X) || !double.IsFinite(cameraPosition.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(camera));
            }

            var observations = semanticLines.Select(line =>
            {
                if (line.Row < 0) throw new ArgumentOutOfRangeException(nameof(semanticLines));
                return new GroundHypothesisAtlasLine(
                    -1,
                    new AtlasPoint(
                        cameraPosition.X + line.XSpan.Start,
                        atlasHeight - (line.Row - cameraPosition.Y)),
                    new AtlasPoint(
                        cameraPosition.X + line.XSpan.EndInclusive + 1,
                        atlasHeight - (line.Row - cameraPosition.Y)));
            }).ToArray();

            foreach (var line in trackingLines)
            {
                var screenY = atlasHeight - line.AtlasStart.Y + cameraPosition.Y;
                var screenX0 = line.AtlasStart.X - cameraPosition.X;
                var screenX1 = line.AtlasEnd.X - cameraPosition.X;
                var visibleWidth = Math.Max(
                    0,
                    Math.Min(frameWidth, screenX1) - Math.Max(0, screenX0));
                if (screenY < 0 || screenY >= atlasHeight || visibleWidth < 16
                    || !records.TryGetValue(line.SegmentId, out var record))
                {
                    continue;
                }

                record.VisibleFrames++;
                if (observations.Any(observation =>
                        VerticallyMatches(line, observation)
                        && HorizontalOverlap(line, observation) >= 8))
                {
                    record.SupportedFrames++;
                }
                record.Geometry = line;
            }
        }

        var currentIds = trackingLines.Select(line => line.SegmentId).ToHashSet();
        foreach (var staleId in records.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            records.Remove(staleId);
        }

        return trackingLines.Where(line => records[line.SegmentId].Approved).ToArray();
    }

    private static void Validate(GroundHypothesisAtlasLine line)
    {
        if (line.SegmentId < 0
            || !double.IsFinite(line.AtlasStart.X)
            || !double.IsFinite(line.AtlasStart.Y)
            || !double.IsFinite(line.AtlasEnd.X)
            || !double.IsFinite(line.AtlasEnd.Y)
            || line.AtlasEnd.X < line.AtlasStart.X)
        {
            throw new ArgumentOutOfRangeException(nameof(line));
        }
    }

    private static bool VerticallyMatches(
        GroundHypothesisAtlasLine left,
        GroundHypothesisAtlasLine right) =>
        Math.Abs(left.AtlasStart.Y - right.AtlasStart.Y) <= 4;

    private static double HorizontalOverlap(
        GroundHypothesisAtlasLine left,
        GroundHypothesisAtlasLine right) =>
        Math.Max(
            0,
            Math.Min(left.AtlasEnd.X, right.AtlasEnd.X)
                - Math.Max(left.AtlasStart.X, right.AtlasStart.X));

    private sealed class Record(
        GroundHypothesisAtlasLine geometry,
        int visibleFrames = 0,
        int supportedFrames = 0)
    {
        public int VisibleFrames { get; set; } = visibleFrames;
        public int SupportedFrames { get; set; } = supportedFrames;
        public GroundHypothesisAtlasLine Geometry { get; set; } = geometry;
        public bool Approved => VisibleFrames >= 6 && SupportedFrames * 10 > VisibleFrames * 3;
    }
}
