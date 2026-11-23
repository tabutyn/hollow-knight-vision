using System.Text.Json;
using System.Text.Json.Serialization;

namespace HollowKnightVision.Windows.Core;

public enum AtlasPathEditTool { Jump, DoubleJump, Delete }

public readonly record struct AtlasPathCalibration(string Room, string Scene, AtlasPoint Origin, AtlasPoint Scale)
{
    internal bool IsValid => !string.IsNullOrWhiteSpace(Room) && !string.IsNullOrWhiteSpace(Scene)
        && AtlasPathPoint.TryQuantize(Origin.X, Origin.Y, out _)
        && Scale.X is >= .001 and <= 10_000 && Scale.Y is >= .001 and <= 10_000;
}

public readonly record struct AtlasPathSelection(string Room, AtlasPathPlatform Floor, AtlasPathPoint Point);

/// <summary>Edits room links using the same scene and legacy room formats as the Mac Path editor.</summary>
public sealed class AtlasPathLinkStore
{
    public const string Filename = "atlas-path-connections-v1.json";
    private readonly string path;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private Document document = new([], []);
    private bool writable;
    public AtlasPathSelection? Selection { get; private set; }
    public string? Issue { get; private set; }
    private int revision;
    public int Revision => Volatile.Read(ref revision);

    private sealed record Point([property: JsonRequired] double X, [property: JsonRequired] double Y);
    private sealed record Link([property: JsonRequired] Guid Id, [property: JsonRequired] Point First,
        [property: JsonRequired] Point Second, [property: JsonRequired] bool DoubleJump);
    private sealed record Document([property: JsonRequired] Dictionary<string, Link[]> Rooms,
        [property: JsonRequired] Dictionary<string, Link[]> Scenes);
    private readonly record struct Projected(Guid Id, AtlasPathConnection Connection);

    public AtlasPathLinkStore(string filePath)
    {
        path = Path.GetFullPath(filePath);
        try
        {
            if (File.Exists(path))
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = json.RootElement;
                document = root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("rooms", out _) || root.TryGetProperty("scenes", out _))
                    ? root.Deserialize<Document>(JsonOptions) ?? throw new JsonException("Missing path document")
                    : new(root.Deserialize<Dictionary<string, Link[]>>(JsonOptions) ?? throw new JsonException("Missing path rooms"), []);
                if (!Valid(document.Rooms) || !Valid(document.Scenes)) throw new JsonException("Invalid path connections");
            }
            writable = true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            document = new([], []);
            Issue = $"Path connections could not be loaded: {error.Message}";
        }

        static bool Valid(Dictionary<string, Link[]>? groups) => groups is not null && groups.All(group =>
            group.Key.Length > 0 && group.Value is not null
            && group.Value.All(link => link is not null && link.Id != Guid.Empty && ValidPoint(link.First) && ValidPoint(link.Second))
            && group.Value.Select(link => link.Id).Distinct().Count() == group.Value.Length);

        static bool ValidPoint(Point? point) => point is not null && double.IsFinite(point.X) && double.IsFinite(point.Y);
    }

    public IReadOnlyList<AtlasPathConnection> Connections(AtlasPathCalibration calibration) =>
        Placed(calibration).Select(link => link.Connection).ToArray();

    public void CancelSelection() => Selection = null;

    public int? HitDistance(AtlasPlatformGraph graph, AtlasPathPoint point, AtlasPathCalibration calibration, AtlasPathEditTool tool)
    {
        if (!calibration.IsValid) return null;
        if (tool == AtlasPathEditTool.Delete)
        {
            var links = Placed(calibration);
            return links.Length == 0 ? null : links.Min(link => ShelfDistance(point, link.Connection, graph.KnightHeight));
        }
        return NearestFloor(graph, point)?.Distance;
    }

    public bool Click(AtlasPlatformGraph graph, AtlasPathPoint point, int tolerance, AtlasPathCalibration calibration, AtlasPathEditTool tool)
    {
        if (!calibration.IsValid || tolerance < 1) return false;
        var list = Placed(calibration).ToList();
        if (tool == AtlasPathEditTool.Delete)
        {
            CancelSelection();
            if (list.Count == 0) return false;
            var hit = list.MinBy(link => ShelfDistance(point, link.Connection, graph.KnightHeight));
            if (ShelfDistance(point, hit.Connection, graph.KnightHeight) > tolerance) return false;
            list.Remove(hit);
            return Save(calibration, list);
        }

        if (NearestFloor(graph, point) is not { } nearest || nearest.Distance > tolerance) return false;
        var (floor, ground, _) = nearest;
        if (Selection is not { } first || first.Room != calibration.Room || first.Floor == floor)
        {
            Selection = new(calibration.Room, floor, ground);
            return false;
        }

        var heightTolerance = Math.Max(4, graph.KnightHeight / 3);
        list.RemoveAll(link =>
        {
            var connection = link.Connection;
            return Matches(connection.First, first.Floor) && Matches(connection.Second, floor)
                || Matches(connection.Second, first.Floor) && Matches(connection.First, floor);
        });
        list.Add(new(Guid.NewGuid(), new(first.Point, ground, tool == AtlasPathEditTool.DoubleJump)));
        if (!Save(calibration, list)) return false;
        CancelSelection();
        return true;

        bool Matches(AtlasPathPoint p, AtlasPathPlatform platform) =>
            platform.Contains(p.X) && Math.Abs(p.Y - platform.Y) <= heightTolerance;
    }

    private Projected[] Placed(AtlasPathCalibration calibration)
    {
        if (!calibration.IsValid) return [];
        var current = Volatile.Read(ref document);
        var scenes = Project(current.Scenes.GetValueOrDefault(calibration.Scene) ?? [], world: true).ToArray();
        if (!current.Rooms.TryGetValue(calibration.Room, out var room)) return scenes;
        var byId = scenes.ToDictionary(link => link.Id);
        return Project(room, world: false).Select(link => byId.GetValueOrDefault(link.Id, link)).ToArray();

        IEnumerable<Projected> Project(Link[] source, bool world)
        {
            foreach (var link in source)
                if (TryPoint(link.First, world, out var a) && TryPoint(link.Second, world, out var b))
                    yield return new(link.Id, new(a, b, link.DoubleJump));
        }

        bool TryPoint(Point p, bool world, out AtlasPathPoint projected) =>
            AtlasPathPoint.TryQuantize(world ? calibration.Origin.X + p.X * calibration.Scale.X : p.X,
                world ? calibration.Origin.Y + p.Y * calibration.Scale.Y : p.Y, out projected);
    }

    private bool Save(AtlasPathCalibration calibration, List<Projected> links)
    {
        if (!writable) return false;
        var replaced = Placed(calibration).Select(link => link.Id).ToHashSet();
        var next = new Document(new(document.Rooms), new(document.Scenes));
        next.Rooms[calibration.Room] = links.Select(link => Serialize(link, world: false)).ToArray();
        next.Scenes[calibration.Scene] = (document.Scenes.GetValueOrDefault(calibration.Scene) ?? [])
            .Where(link => !replaced.Contains(link.Id)).Concat(links.Select(link => Serialize(link, world: true))).ToArray();
        try
        {
            AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions));
            Volatile.Write(ref document, next);
            Issue = null;
            Interlocked.Increment(ref revision);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Issue = $"Path connections could not be saved: {error.Message}";
            return false;
        }

        Link Serialize(Projected item, bool world) =>
            new(item.Id, Convert(item.Connection.First, world), Convert(item.Connection.Second, world), item.Connection.DoubleJump);

        Point Convert(AtlasPathPoint point, bool world) => world
            ? new((point.X - calibration.Origin.X) / calibration.Scale.X, (point.Y - calibration.Origin.Y) / calibration.Scale.Y)
            : new(point.X, point.Y);
    }

    private static (AtlasPathPlatform Floor, AtlasPathPoint Ground, int Distance)? NearestFloor(AtlasPlatformGraph graph,
        AtlasPathPoint point)
    {
        (AtlasPathPlatform Floor, AtlasPathPoint Ground, int Distance)? nearest = null;
        foreach (var floor in graph.Floors)
        {
            var x = Math.Clamp(point.X, floor.Left, floor.Right);
            var distance = Math.Abs(x - point.X)
                + Math.Min(Math.Abs(point.Y - floor.Y), Math.Abs(point.Y - floor.Y - graph.KnightHeight));
            if (nearest is { } previous && distance >= previous.Distance) continue;
            nearest = (floor, new(x, floor.Y), distance);
        }
        return nearest;
    }

    private static int ShelfDistance(AtlasPathPoint p, AtlasPathConnection link, int height)
    {
        var first = link.First with { Y = link.First.Y + height };
        var second = link.Second with { Y = link.Second.Y + height };
        var top = Math.Max(first.Y, second.Y) + Math.Max(2, height / 4);
        var left = Math.Min(first.X, second.X);
        var right = Math.Max(first.X, second.X);
        var across = new AtlasPathPoint(Math.Clamp(p.X, left, right), top);
        var a = new AtlasPathPoint(first.X, Math.Clamp(p.Y, first.Y, top));
        var b = new AtlasPathPoint(second.X, Math.Clamp(p.Y, second.Y, top));
        return Math.Min(across.DistanceTo(p), Math.Min(a.DistanceTo(p), b.DistanceTo(p)));
    }
}
