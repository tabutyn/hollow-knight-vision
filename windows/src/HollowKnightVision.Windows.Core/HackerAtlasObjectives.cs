namespace HollowKnightVision.Windows.Core;

public readonly record struct HackerObjectivePlacement(string Scene, AtlasPoint Offset, AtlasPoint Scale, AtlasPlatformGraph Graph);

/// <summary>Immutable Objective selections shared by the Atlas and the capture worker.</summary>
public sealed class HackerAtlasObjectives
{
    private readonly Dictionary<string, BotAtlasObjective?> rooms;

    public static HackerAtlasObjectives Default { get; } = new(new(StringComparer.Ordinal));

    private HackerAtlasObjectives(Dictionary<string, BotAtlasObjective?> rooms) => this.rooms = rooms;

    public bool IsAutomatic(string scene) => rooms.TryGetValue(scene, out var selected) && selected is null;
    public BotAtlasObjective? SelectedFor(string scene) => rooms.GetValueOrDefault(scene);

    public AtlasPathPoint? Target(HackerObjectivePlacement room) => rooms.TryGetValue(room.Scene, out var selected)
        ? selected?.Project(room.Scene, room.Scale, room.Graph) : room.Graph.UpperRightObjective;

    public HackerAtlasObjectives? Select(AtlasPoint point, double tolerance, IReadOnlyList<HackerObjectivePlacement> placements)
    {
        if (tolerance is not (>= 0 and <= 10_000)) return null;
        var radius = Math.Max(2, (int)Math.Ceiling(tolerance));
        var distance = radius + 1;
        (HackerObjectivePlacement Room, AtlasPathPoint Point)? hit = null;
        foreach (var room in placements)
        {
            if (room.Scale is not { X: > 0 and < double.PositiveInfinity, Y: > 0 and < double.PositiveInfinity }
                || !AtlasPathPoint.TryQuantize(point.X - room.Offset.X, point.Y - room.Offset.Y, out var local)
                || room.Graph.NearestStroke(local, distance - 1) is not { } stroke) continue;
            var projected = stroke.Closest(local);
            distance = projected.DistanceTo(local);
            hit = (room, projected);
        }
        if (hit is not { } current) return null;

        var pickedRoom = current.Room;
        var edited = new Dictionary<string, BotAtlasObjective?>(rooms, StringComparer.Ordinal);
        foreach (var room in placements)
            if (Target(room) is not null || SelectedFor(room.Scene) is not null) edited[room.Scene] = null;
        var previous = Target(pickedRoom);
        if (previous is not { } target || target.DistanceTo(current.Point) > Math.Min(radius, Math.Max(2, pickedRoom.Graph.KnightWidth / 2)))
            edited[pickedRoom.Scene] = new(pickedRoom.Scene, new(current.Point.X / pickedRoom.Scale.X, current.Point.Y / pickedRoom.Scale.Y));
        return new(edited);
    }
}
