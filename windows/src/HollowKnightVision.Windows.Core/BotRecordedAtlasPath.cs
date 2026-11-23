using System.Text.Json;

namespace HollowKnightVision.Windows.Core;

public sealed record BotRecoveredPathFrame(int Index, long Generation, double CaptureTimestamp,
    BotRoadHint? RoadHint, double? PathProgress);

/// <summary>Reconstructs routes causally from the terrain, poses and measurements stored in a demonstration.</summary>
public static class BotRecordedAtlasPath
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<BotRecoveredPathFrame> Recover(string folder)
    {
        var capture = ReadCapture(folder);
        return Recover(capture.Observations, capture.Domain);
    }

    public static (BotCaptureManifest Manifest, BotCaptureDomain Domain, IReadOnlyList<BotCaptureObservation> Observations)
        ReadCapture(string folder)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "manifest.json")));
        var header = manifest.RootElement.Deserialize<BotCaptureManifest>(Json)
            ?? throw new InvalidDataException("Missing bot capture manifest.");
        IReadOnlyList<string> expected = header.SchemaVersion switch
        {
            1 => BotRecordingContract.FeatureNames.Take(BotRecordingContract.LegacyWidth).ToArray(),
            BotRecordingContract.SchemaVersion => BotRecordingContract.FeatureNames,
            _ => throw new InvalidDataException("Unsupported bot capture schema.")
        };
        if (header.Id == Guid.Empty || header.Status != "completed"
            || !manifest.RootElement.TryGetProperty("featureNames", out var names)
            || names.ValueKind != JsonValueKind.Array
            || !names.EnumerateArray().Select(name => name.GetString()).SequenceEqual(expected))
            throw new InvalidDataException("Invalid bot capture manifest.");

        var rows = new List<BotCaptureObservation>();
        var inputs = 0;
        foreach (var line in File.ReadLines(Path.Combine(folder, "events.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var entry = JsonDocument.Parse(line);
            switch (entry.RootElement.GetProperty("type").GetString())
            {
                case "observation":
                    if (entry.RootElement.Deserialize<BotCaptureObservation>(Json) is not { } row
                        || row.Features?.Count != expected.Count)
                        throw new InvalidDataException("Invalid bot capture observation length.");
                    rows.Add(row);
                    break;
                case "input":
                    _ = entry.RootElement.Deserialize<BotCaptureInput>(Json)
                        ?? throw new InvalidDataException("Invalid bot capture input.");
                    inputs++;
                    break;
            }
        }
        if (rows.Count != header.ObservationCount || inputs != header.InputEventCount)
            throw new InvalidDataException("Bot capture counts are incomplete.");
        var domain = header.BotDomain switch
        {
            "gameplay" => BotCaptureDomain.Gameplay,
            "hacker" => BotCaptureDomain.Hacker,
            null => rows.Any(row => row.TerrainSource == "human-authored") ? BotCaptureDomain.Hacker : BotCaptureDomain.Gameplay,
            _ => throw new InvalidDataException("Invalid bot capture domain.")
        };
        return (header, domain, rows);
    }

    public static IReadOnlyList<BotRecoveredPathFrame> Recover(IReadOnlyList<BotCaptureObservation> observations,
        BotCaptureDomain domain)
    {
        var authored = domain == BotCaptureDomain.Hacker;
        var charts = new Dictionary<(long Generation, int Room, string Space), Chart>();
        var rooms = new Dictionary<(long Generation, int Room), double>();
        var frames = new List<BotRecoveredPathFrame>();
        Chart? previous = null;
        for (var index = 0; index < observations.Count; index++)
        {
            var row = observations[index];
            var terrain = authored ? row.AuthoredTerrain : row.DetectedTerrain;
            if (!TryMeasure(row, terrain, authored, out var camera, out var feet, out var scale))
            {
                previous?.Interrupt();
                previous = null;
                continue;
            }
            var key = (row.Generation, row.RoomId, row.BlockadePose!.Space);
            var room = (row.Generation, row.RoomId);
            if (!charts.TryGetValue(key, out var chart)) charts[key] = chart = new(authored);
            if (chart != previous)
            {
                chart.Interrupt();
                previous?.Interrupt();
            }
            previous = chart;
            if (chart.LastFrame is { } last && (row.CaptureTimestamp <= last || row.CaptureTimestamp - last >= .45))
                chart.Interrupt();
            chart.LastFrame = row.CaptureTimestamp;
            var measure = chart.Observe(row, terrain!, camera, feet, scale);
            var progress = measure.Progress;
            if (!authored && progress is > 0)
            {
                chart.TotalProgress += progress.Value;
                var best = rooms.GetValueOrDefault(room);
                progress = Math.Max(0, chart.TotalProgress - best);
                rooms[room] = Math.Max(best, chart.TotalProgress);
            }

            if (row.AtlasPathVersion != 1 && row.HackerPathVersion != 1)
                frames.Add(new(index, row.Generation, row.CaptureTimestamp, measure.Hint, progress));
        }
        return frames;
    }

    private static bool TryMeasure(BotCaptureObservation row, BotScreenTerrain? terrain, bool authored,
        out AtlasPoint camera, out AtlasPathPoint feet, out double scale)
    {
        camera = default;
        feet = default;
        scale = 0;
        if (terrain is not { Width: >= 1 and <= 32768, Height: >= 1 and <= 32768 }
            || row.BlockadePose is not { IsValid: true } pose
            || !pose.Space.StartsWith(authored ? "hacker/" : "visual/", StringComparison.Ordinal)
            || row.Generation < 0 || row.RoomId < 0 || row.Features is not { Count: >= 7 }
            || row.Issues is not { Count: 0 } || row.KnightPrediction is not null
            || !row.Features.Take(7).All(double.IsFinite) || row.Features[6] != 1 || row.Features[4] < .5
            || !double.IsFinite(row.CaptureTimestamp) || !double.IsFinite(row.CapturedAt)
            || row.AvailableAt - row.CapturedAt is not (>= 0 and <= .35)
            || row.DetectionTimestamp is not { } seen || row.CaptureTimestamp - seen is not (>= 0 and <= .2))
            return false;

        scale = terrain.Width / pose.Width;
        camera = new(pose.X * scale, pose.Y * scale);
        return double.IsFinite(scale) && scale > 0 && AtlasPathPoint.TryQuantize(camera.X, camera.Y, out _)
            && AtlasPathPoint.TryQuantize(camera.X + terrain.Width * row.Features[0],
                camera.Y + terrain.Width * (row.Features[1] - row.Features[3] * (authored ? 1 : .5)), out feet);
    }

    private sealed class Chart(bool authored)
    {
        internal double? LastFrame { get; set; }
        internal double TotalProgress { get; set; }

        private readonly AtlasPathNavigator navigation = new();
        private readonly VisualPathWallHistory wallHistory = new();
        private IReadOnlyList<AtlasPathPlatform> floors = [];
        private IReadOnlyList<AtlasPathWall> walls = [];
        private AtlasPlatformGraph? graph;
        private AtlasPathPoint? objective;
        private (AtlasPathHint Route, AtlasPathPoint Position, double Time)? previous;
        private int? bestRemaining;
        private bool measuredSize;

        internal void Interrupt()
        {
            previous = null;
            navigation.Interrupt();
            wallHistory.Reset();
        }

        internal (BotRoadHint? Hint, double? Progress) Observe(BotCaptureObservation row, BotScreenTerrain terrain,
            AtlasPoint camera, AtlasPathPoint feet, double scale)
        {
            var width = graph?.KnightWidth ?? Math.Max(3, (int)(terrain.Width * .028));
            var height = graph?.KnightHeight ?? Math.Max(5, (int)(terrain.Width * .055));
            if (!measuredSize && row.Features[2] is >= .006 and <= .15 && row.Features[3] is >= .014 and <= .22)
            {
                width = Math.Max(3, AtlasPathPoint.Round(terrain.Width * row.Features[2]));
                height = Math.Max(5, AtlasPathPoint.Round(terrain.Width * row.Features[3]));
                measuredSize = true;
            }

            var nextFloors = AtlasPathFloorGeometry.Extend(floors, (terrain.Floors ?? [])
                .Select(floor => ProjectFloor(floor, camera)).OfType<AtlasPathPlatform>(), height);
            var seenWalls = (terrain.Walls ?? []).Select(wall => ProjectWall(wall, camera)).OfType<AtlasPathWall>().ToArray();
            var observed = authored ? seenWalls : wallHistory.Observe(seenWalls, [], row.CaptureTimestamp);
            var nextWalls = observed.Count == 0 ? walls : GameplayPathTracker.RetainWalls(walls, observed);
            if (graph is null || width != graph.KnightWidth || height != graph.KnightHeight
                || !ReferenceEquals(floors, nextFloors) || !walls.SequenceEqual(nextWalls))
            {
                floors = nextFloors;
                walls = nextWalls;
                graph = new(authored ? floors : GroundClearance.FilterPaths(floors, height), walls, height, width,
                    pixelsPerWorldUnit: authored ? new(scale, scale) : null, previous: graph, detectedGround: !authored);
            }
            // Historical reconstruction uses the same fixed upper-right goal as
            // a fresh Mac chart. An unreachable goal never becomes a frontier.
            var target = graph.UpperRightObjective;
            if (objective != target)
            {
                objective = target;
                previous = null;
                bestRemaining = null;
                navigation.Interrupt();
            }
            if (target is not { } goal || navigation.Follow(graph, feet, grounded: null, measured: true,
                    admitted: null, walked: [], fixedObjective: goal) is not { } route)
            {
                previous = null;
                if (navigation.HasStartProjection) bestRemaining = null;
                return (null, null);
            }
            var position = feet with { Y = feet.Y + height };
            var progress = Score(route, position, row.CaptureTimestamp, height);
            bestRemaining = Math.Min(bestRemaining ?? route.Remaining, route.Remaining);
            previous = (route, position, row.CaptureTimestamp);
            return (BotPathMotion.Road(route, height, terrain.Width, frontier: false), progress);
        }

        private double Score(AtlasPathHint route, AtlasPathPoint position, double time, int height)
        {
            if (previous is not { } old || bestRemaining is not { } best
                || route.PriorRemaining is not { } remaining || time - old.Time is <= 0 or >= .45
                || old.Position.DistanceTo(position) > 3 * height) return 0;
            return BotPathMotion.ForwardPixels(old.Route, old.Position, best, position, route.Projection,
                remaining) / (double)height;
        }
    }

    private static AtlasPathPlatform? ProjectFloor(BotFloor floor, AtlasPoint camera) =>
        AtlasPathPoint.TryQuantize(Math.Floor(camera.X + floor.MinimumX), camera.Y + floor.Y, out var left)
        && AtlasPathPoint.TryQuantize(Math.Ceiling(camera.X + floor.MaximumX), camera.Y + floor.Y, out var right)
        && floor.MinimumX < floor.MaximumX ? new(left.X, right.X, left.Y) : null;

    private static AtlasPathWall? ProjectWall(BotWall wall, AtlasPoint camera) =>
        AtlasPathPoint.TryQuantize(camera.X + wall.X, Math.Floor(camera.Y + wall.BottomY), out var low)
        && AtlasPathPoint.TryQuantize(camera.X + wall.X, Math.Ceiling(camera.Y + wall.TopY), out var high)
        && wall.BottomY < wall.TopY && wall.SolidSide is >= -1 and <= 1
            ? new(low.X, low.Y, high.Y, wall.SolidSide) : null;
}
