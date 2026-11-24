namespace HollowKnightVision.Windows.Core;

/// <summary>Compares successive committed Game Play pose graphs at the bot observation cadence.</summary>
public sealed class BotCommittedAtlasCoverage
{
    private readonly record struct Owner(int Island, bool Measured, bool Pending);

    private sealed record Placement(LiveWorldObservation Observation, Owner? Owner)
    {
        public bool Matches(Placement current) => Owner == current.Owner
            && Observation.RoomId == current.Observation.RoomId
            && Observation.SourceObservationId == current.Observation.SourceObservationId
            && Observation.OptimizedPose == current.Observation.OptimizedPose
            && Observation.SourceWidth == current.Observation.SourceWidth
            && Observation.SourceHeight == current.Observation.SourceHeight
            && Observation.SolveWidth == current.Observation.SolveWidth
            && (ReferenceEquals(Observation.ExcludedRects, current.Observation.ExcludedRects)
                || Observation.ExcludedRects.SequenceEqual(current.Observation.ExcludedRects));
    }

    private Dictionary<int, Placement> placements = [];
    private readonly HashSet<(int Room, int X, int Y)> occupied = [];
    private readonly HashSet<(int Room, int X, int Y)> seen = [];
    private readonly Dictionary<int, AtlasPoint> lastMeasuredByIsland = [];
    private IReadOnlyList<LiveWorldObservation>? previousRows;
    private int revision, schema, earned;

    internal (int Covered, int Earned) Totals => (occupied.Count, earned);

    public void Reset()
    {
        previousRows = null;
        placements.Clear();
        occupied.Clear();
        seen.Clear();
        lastMeasuredByIsland.Clear();
        earned = 0;
    }

    public void Update(LiveWorldSnapshot snapshot, bool creditNewCells = true)
    {
        if (revision == snapshot.MapRevision && ReferenceEquals(previousRows, snapshot.Observations)) return;
        var owners = snapshot.TrackingIslands.SelectMany(island => island.Members.Select(member =>
            (member.ObservationId, Owner: new Owner(island.Id, member.Measured, island.RequiresPlacement))))
            .ToDictionary(pair => pair.ObservationId, pair => pair.Owner);
        var current = snapshot.Observations.ToDictionary(row => row.Id,
            row => new Placement(row, owners.TryGetValue(row.Id, out var owner) ? owner : null));
        var corrected = previousRows is not null && (schema != snapshot.SchemaVersion
            || placements.Any(pair => !current.TryGetValue(pair.Key, out var currentPlacement)
                || !pair.Value.Matches(currentPlacement)));
        if (corrected)
        {
            occupied.Clear();
            lastMeasuredByIsland.Clear();
            // Reproject existing evidence before checking whether a new observation extends it.
            foreach (var row in snapshot.Observations)
                if (placements.ContainsKey(row.Id)) Add(current[row.Id], snapshot, credit: false);
        }
        var continuing = creditNewCells && previousRows is not null && snapshot.MapRevision >= revision;
        foreach (var row in snapshot.Observations)
            if (!placements.ContainsKey(row.Id)) Add(current[row.Id], snapshot, credit: continuing);
        placements = current;
        previousRows = snapshot.Observations;
        revision = snapshot.MapRevision;
        schema = snapshot.SchemaVersion;
    }

    public BotAtlasView? View(AtlasPoint? center, WorldSize size, int room, int? health)
    {
        if (center is not { } point || occupied.Count == 0 || size.Width <= 0 || size.Height <= 0
            || !AtlasPathPoint.TryQuantize(point.X, point.Y, out _)) return null;
        // The Windows compositor places an image around its camera center.
        var pose = new AtlasPoint(point.X - size.Width / 2, point.Y - size.Height / 2);
        return BotAtlasFrontiers.View(pose, size, revision, health, occupied.Count, earned,
            (x, y) => occupied.Contains((room, x, y)));
    }

    private void Add(Placement placement, LiveWorldSnapshot snapshot, bool credit)
    {
        var row = placement.Observation;
        var center = row.OptimizedPose;
        var width = row.SolveWidth;
        var scale = width / row.SourceWidth;
        var height = row.SourceHeight * scale;
        if (placement.Owner is { Pending: true } || width is <= 0 or > 8192 || height is <= 0 or > 8192
            || !AtlasPathPoint.TryQuantize(center.X, center.Y, out _)) return;
        var earns = false;
        if (placement.Owner is { Measured: true } owner)
        {
            if (lastMeasuredByIsland.TryGetValue(owner.Island, out var prior))
            {
                var dx = prior.X - center.X;
                var dy = prior.Y - center.Y;
                var distanceSquared = dx * dx + dy * dy;
                earns = credit && distanceSquared > 1 && distanceSquared <= width * width / 4;
            }
            lastMeasuredByIsland[owner.Island] = center;
        }

        var masks = snapshot.CompositionMasks(row);
        var left = center.X - width / 2;
        var bottom = center.Y - height / 2;
        const double step = BotAtlasFrontiers.CellSize;
        for (var y = BotAtlasFrontiers.FirstCenter(bottom + height * .18);
             y < BotAtlasFrontiers.FirstCenter(bottom + height * .78); y++)
        for (var x = BotAtlasFrontiers.FirstCenter(left); x < BotAtlasFrontiers.FirstCenter(left + width); x++)
        {
            var pixelX = ((x + .5) * step - left) / scale;
            var pixelY = row.SourceHeight - ((y + .5) * step - bottom) / scale;
            if (masks.Any(mask => mask.X <= pixelX && pixelX < mask.Right && mask.Y <= pixelY && pixelY < mask.Bottom))
                continue;
            var cell = (row.RoomId, x, y);
            occupied.Add(cell);
            if (seen.Add(cell) && earns) earned++;
        }
    }
}
