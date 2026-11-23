namespace HollowKnightVision.Windows.Core;

/// <summary>Projects human-authored room geometry into the path graph used by Hacker and Gym.</summary>
public static class AuthoredAtlasGraph
{
    public static AtlasPlatformGraph Build(GroundTruthLineDocument document, AtlasPathCalibration room,
        AtlasPathLinkStore? links = null, AtlasPlatformGraph? previous = null)
    {
        var floors = new List<AtlasPathPlatform>();
        var walls = new List<AtlasPathWall>();
        var spikes = new List<AtlasPathPlatform>();
        var doors = new List<AtlasPathDoor>();
        foreach (var line in document.AuthoredLines.Where(line => line.SceneName == room.Scene))
        {
            if (!AtlasPathPoint.TryQuantize(line.Start.X * room.Scale.X, line.Start.Y * room.Scale.Y, out var a)
                || !AtlasPathPoint.TryQuantize(line.End.X * room.Scale.X, line.End.Y * room.Scale.Y, out var b)) continue;
            switch (line.Kind)
            {
                case GroundTruthLineKind.Positive:
                    floors.Add(new(Math.Min(a.X, b.X), Math.Max(a.X, b.X), a.Y));
                    break;
                case GroundTruthLineKind.Wall:
                    walls.Add(new(a.X, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                    break;
                case GroundTruthLineKind.Spike:
                    spikes.Add(new(Math.Min(a.X, b.X), Math.Max(a.X, b.X), a.Y));
                    break;
                case GroundTruthLineKind.Door:
                case GroundTruthLineKind.DoorVertical:
                    doors.Add(new(a, b));
                    break;
            }
        }

        return new(floors, walls, AtlasPathPoint.Round(1.28 * room.Scale.Y),
            AtlasPathPoint.Round(.5 * room.Scale.X), connections: links?.Connections(room),
            pixelsPerWorldUnit: room.Scale, previous: previous, spikes: spikes, doors: doors);
    }
}
