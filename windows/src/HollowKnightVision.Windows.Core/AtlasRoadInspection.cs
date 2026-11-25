namespace HollowKnightVision.Windows.Core;

/// <summary>Inspects the same authored room graph used by the Hacker workspace.</summary>
public static class AtlasRoadInspection
{
    public static IReadOnlyList<object> Inspect(string archive, GroundTruthLineDocument document, AtlasPathLinkStore links)
    {
        if (links.Issue is { } issue) throw new InvalidDataException(issue);
        return HackerAtlasArchiveStore.ReadRoadRooms(archive).Select(room =>
        {
            var (calibration, tileCount) = room;
            var scene = calibration.Scene;
            var roomID = calibration.Room;
            var graph = AuthoredAtlasGraph.Build(document, calibration, links);
            if (graph.Floors.Count == 0)
                return (object)new
                {
                    scene, roomID, tileCount, issue = "No ground geometry is available.",
                    floors = Array.Empty<object>(), strokes = Array.Empty<object>(), nodes = Array.Empty<object>()
                };
            return new
            {
                scene, roomID, tileCount,
                calibration = new { origin = new[] { calibration.Origin.X, calibration.Origin.Y },
                    scale = new[] { calibration.Scale.X, calibration.Scale.Y } },
                graph.KnightHeight, graph.KnightWidth,
                floors = graph.Floors.Select(floor => new[] { new[] { floor.Left, floor.Y }, new[] { floor.Right, floor.Y } }).ToArray(),
                strokes = graph.Strokes.Select(stroke => new { from = XY(stroke.A), to = XY(stroke.B), stroke.Kind, stroke.Forward }).ToArray(),
                nodes = graph.Nodes.OrderBy(pair => pair.Key)
                    .Select(pair => new { point = XY(pair.Key), neighbors = pair.Value.Select(XY).ToArray() }).ToArray()
            };
        }).ToArray();
    }

    private static int[] XY(AtlasPathPoint point) => [point.X, point.Y];
}
