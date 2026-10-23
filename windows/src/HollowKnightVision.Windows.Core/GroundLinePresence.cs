namespace HollowKnightVision.Windows.Core;

public enum GroundLinePresenceState
{
    Observing,
    Confirmed,
    Rejected,
}

public readonly record struct GroundLinePresenceReview(
    int Id,
    CleanFloorLine Line,
    GroundLinePresenceState State,
    double VisibleSeconds,
    double DetectedFraction);

/// <summary>
/// Counts elapsed, observable screen time in world coordinates. Occlusions,
/// off-screen time and uncertain camera poses contribute neither hits nor misses.
/// Classification follows all accumulated observable time. A line initially
/// rejected during an uncertain entrance can recover only by accumulating
/// enough later, pose-verified detections to cross the same 50% rule.
/// </summary>
public sealed class GroundLinePresence
{
    private readonly List<Record> records = [];

    // World Y buckets preserve the complete evidence ledger while restricting
    // each live query to nearby surfaces. Indices remain stable until reset.
    private readonly Dictionary<int, List<int>> rowBuckets = [];
    private readonly List<int> lastObservableIndices = [];
    private int nextId = 1;
    private double? lastTimestamp;

    public GroundLinePresence(
        double minimumObservationSeconds = 2,
        double confirmedLossGraceSeconds = 0)
    {
        MinimumObservationSeconds = Math.Max(0, minimumObservationSeconds);
        ConfirmedLossGraceSeconds = Math.Max(0, confirmedLossGraceSeconds);
    }

    public double MinimumObservationSeconds { get; }

    private double ConfirmedLossGraceSeconds { get; }

    public void Reset()
    {
        records.Clear();
        rowBuckets.Clear();
        lastObservableIndices.Clear();
        nextId = 1;
        lastTimestamp = null;
    }

    public void Update(
        IReadOnlyList<CleanFloorLine> lines,
        AtlasPoint camera,
        int width,
        int height,
        double timestamp,
        bool poseVerified,
        IReadOnlyList<PixelRect> exclusions)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(exclusions);

        var dt = lastTimestamp is { } previous ? timestamp - previous : 0;
        lastTimestamp = timestamp;
        if (!poseVerified)
        {
            foreach (var index in lastObservableIndices)
            {
                records[index].LastObservable = false;
            }
            lastObservableIndices.Clear();
            return;
        }

        // A long gap provides no evidence about intervening screen contents.
        var elapsed = dt is > 0 and <= 0.25 ? dt : 0;
        var seen = new HashSet<int>();
        foreach (var line in lines)
        {
            var x0 = camera.X + line.XSpan.Start;
            var x1 = camera.X + line.XSpan.EndInclusive;
            var worldY = line.Row - camera.Y;
            var matching = Indices(worldY - 4, worldY + 4)
                .Where(index => Math.Abs(records[index].WorldY - worldY) <= 4
                    && Math.Min(records[index].MaximumX, x1)
                        - Math.Max(records[index].MinimumX, x0) >= 8)
                .OrderBy(index => records[index].MinimumX)
                .ToArray();
            var uncoveredStart = x0;
            var additions = new List<(double MinimumX, double MaximumX)>();
            foreach (var index in matching)
            {
                seen.Add(records[index].Id);
                if (records[index].MinimumX - uncoveredStart >= 16)
                {
                    additions.Add((uncoveredStart, records[index].MinimumX - 1));
                }
                uncoveredStart = Math.Max(uncoveredStart, records[index].MaximumX + 1);
            }
            if (x1 - uncoveredStart + 1 >= 16)
            {
                additions.Add((uncoveredStart, x1));
            }

            foreach (var span in additions)
            {
                // Extensions earn their own screen-time evidence; one long
                // false detection cannot enlarge an established platform.
                var record = new Record(nextId, span.MinimumX, span.MaximumX, worldY);
                var bucket = Bucket(worldY);
                if (!rowBuckets.TryGetValue(bucket, out var bucketIndices))
                {
                    bucketIndices = [];
                    rowBuckets[bucket] = bucketIndices;
                }
                bucketIndices.Add(records.Count);
                records.Add(record);
                seen.Add(nextId);
                nextId++;
            }
        }

        var updateIndices = Indices(-camera.Y, height - camera.Y)
            .Concat(lastObservableIndices)
            .Distinct()
            .Order()
            .ToArray();
        lastObservableIndices.Clear();
        foreach (var index in updateIndices)
        {
            var record = records[index];
            var y = record.WorldY + camera.Y;
            var x0 = Math.Max(0, record.MinimumX - camera.X);
            var x1 = Math.Min(width - 1d, record.MaximumX - camera.X);
            var visible = 0d;
            if (y >= 0 && y + 12 <= height && x1 >= x0)
            {
                for (var x = (int)x0; x <= (int)x1; x += 8)
                {
                    if (!exclusions.Any(rect => Intersects(x, y - 1, 8, 13, rect)))
                    {
                        visible += 8;
                    }
                }
            }

            var observable = visible >= Math.Min(32, record.MaximumX - record.MinimumX + 1);
            var detected = seen.Contains(record.Id);
            if (observable && record.LastObservable)
            {
                record.VisibleSeconds += elapsed;
                // Trapezoidal integration avoids bias from which endpoint
                // first observed a changing detection.
                record.DetectedSeconds += elapsed
                    * ((record.LastDetected ? 1 : 0) + (detected ? 1 : 0)) / 2d;
            }
            if (detected)
            {
                record.MissingObservableSeconds = 0;
            }
            else if (observable && record.LastObservable)
            {
                record.MissingObservableSeconds += elapsed;
            }

            // Once confirmed, brief foreground cover must not erase geometry
            // or its IDs. Persistent observable misses still reject it.
            var retainingConfirmed = record.State == GroundLinePresenceState.Confirmed
                && ConfirmedLossGraceSeconds > 0
                && record.MissingObservableSeconds < ConfirmedLossGraceSeconds;
            if (!retainingConfirmed
                && record.VisibleSeconds + 0.000_001 >= MinimumObservationSeconds)
            {
                var fraction = record.VisibleSeconds > 0
                    ? record.DetectedSeconds / record.VisibleSeconds
                    : 1;
                if (fraction > 0.5 + 0.000_001)
                {
                    record.State = GroundLinePresenceState.Confirmed;
                }
                else if (fraction < 0.5 - 0.000_001)
                {
                    record.State = GroundLinePresenceState.Rejected;
                }
                else
                {
                    record.State = GroundLinePresenceState.Observing;
                }
            }

            record.LastObservable = observable;
            record.LastDetected = detected;
            if (observable)
            {
                lastObservableIndices.Add(index);
            }
        }
    }

    public GroundLinePresenceState? State(
        double minimumX,
        double maximumX,
        double worldY)
    {
        Record? closest = null;
        var closestDistance = double.MaxValue;
        foreach (var index in Indices(worldY - 4, worldY + 4))
        {
            var record = records[index];
            var distance = Math.Abs(record.WorldY - worldY);
            if (distance > 4)
            {
                continue;
            }
            var overlap = Math.Min(record.MaximumX, maximumX)
                - Math.Max(record.MinimumX, minimumX);
            if (overlap < 8 || distance >= closestDistance)
            {
                continue;
            }
            closest = record;
            closestDistance = distance;
        }
        return closest?.State;
    }

    public IReadOnlyList<GroundLinePresenceReview> Reviews(
        AtlasPoint camera,
        int width,
        int height)
    {
        var reviews = new List<GroundLinePresenceReview>();
        foreach (var index in Indices(-camera.Y - 1, height - camera.Y + 1))
        {
            var record = records[index];
            var row = Round(record.WorldY + camera.Y);
            var lower = Math.Max(0, Round(record.MinimumX - camera.X));
            var upper = Math.Min(width - 1, Round(record.MaximumX - camera.X));
            if (row < 0 || row >= height || upper < lower)
            {
                continue;
            }
            reviews.Add(new GroundLinePresenceReview(
                record.Id,
                new CleanFloorLine(row, new PixelSpan(lower, upper)),
                record.State,
                record.VisibleSeconds,
                record.VisibleSeconds > 0
                    ? record.DetectedSeconds / record.VisibleSeconds
                    : 1));
        }
        return reviews;
    }

    private IReadOnlyList<int> Indices(double lowerBound, double upperBound)
    {
        var lower = Bucket(lowerBound);
        var upper = Bucket(upperBound);
        if (upper < lower)
        {
            return [];
        }

        var indices = new List<int>();
        for (var bucket = lower; bucket <= upper; bucket++)
        {
            if (rowBuckets.TryGetValue(bucket, out var bucketIndices))
            {
                indices.AddRange(bucketIndices);
            }
        }
        indices.Sort();
        return indices;
    }

    private static int Bucket(double worldY) => (int)Math.Floor(worldY / 32);

    private static int Round(double value) =>
        checked((int)Math.Round(value, MidpointRounding.AwayFromZero));

    private static bool Intersects(
        double x,
        double y,
        double width,
        double height,
        PixelRect rect) =>
        x < rect.Right && x + width > rect.X
            && y < rect.Bottom && y + height > rect.Y;

    private sealed class Record(int id, double minimumX, double maximumX, double worldY)
    {
        public int Id { get; } = id;
        public double MinimumX { get; } = minimumX;
        public double MaximumX { get; } = maximumX;
        public double WorldY { get; } = worldY;
        public double VisibleSeconds { get; set; }
        public double DetectedSeconds { get; set; }
        public GroundLinePresenceState State { get; set; } = GroundLinePresenceState.Observing;
        public bool LastObservable { get; set; }
        public bool LastDetected { get; set; }
        public double MissingObservableSeconds { get; set; }
    }
}
