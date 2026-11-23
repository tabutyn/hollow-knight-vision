namespace HollowKnightVision.Windows.Core;

public static class AtlasPathRouting
{
    public static IReadOnlyList<AtlasPathPoint> FindRoute(AtlasPlatformGraph graph, AtlasPathStroke startStroke,
        AtlasPathPoint start, AtlasPathStroke targetStroke, AtlasPathPoint target)
    {
        if (!startStroke.Contains(start) || !targetStroke.Contains(target)) return [];
        if (start == target) return [start];

        // Attach exact cursor positions to the containing strokes without changing the cached graph.
        var outgoing = graph.Vertices(startStroke).Where(point => startStroke.AllowsTraversal(start, point)).ToArray();
        var incoming = graph.Vertices(targetStroke).Where(point => targetStroke.AllowsTraversal(point, target)).ToHashSet();
        var costs = new Dictionary<AtlasPathPoint, int> { [start] = 0 };
        var predecessors = new Dictionary<AtlasPathPoint, AtlasPathPoint>();
        var pending = new PriorityQueue<AtlasPathPoint, (int Cost, AtlasPathPoint Point)>();
        pending.Enqueue(start, (0, start));
        while (pending.TryDequeue(out var point, out var priority))
        {
            if (costs[point] != priority.Cost) continue;
            if (point == target)
            {
                var path = new List<AtlasPathPoint> { target };
                while (predecessors.TryGetValue(point, out var prior))
                {
                    path.Add(prior);
                    point = prior;
                }
                path.Reverse();
                return path;
            }
            if (graph.Nodes.TryGetValue(point, out var neighbors))
                foreach (var next in neighbors) Visit(next);
            if (point == start)
            {
                foreach (var next in outgoing) Visit(next);
                if (startStroke == targetStroke && startStroke.AllowsTraversal(start, target)) Visit(target);
            }
            if (incoming.Contains(point)) Visit(target);

            void Visit(AtlasPathPoint next)
            {
                var cost = priority.Cost + point.DistanceTo(next);
                if (costs.TryGetValue(next, out var previous) && previous <= cost) return;
                costs[next] = cost;
                predecessors[next] = point;
                pending.Enqueue(next, (cost, next));
            }
        }
        return [];
    }

    // Overlapping drop branches share a visible shaft. Keep direction and connectivity in the underlying graph.
    public static IReadOnlyList<AtlasPathStroke> DisplayStrokes(IReadOnlyList<AtlasPathStroke> strokes)
    {
        var result = strokes.Where(stroke => stroke.Kind != AtlasPathKind.Drop).ToList();
        var falls = strokes.Where(stroke => stroke.Kind == AtlasPathKind.Drop)
            .GroupBy(stroke => (stroke.Horizontal, Axis: stroke.Horizontal ? stroke.A.Y : stroke.A.X, stroke.Forward));
        foreach (var branch in falls)
        {
            AtlasPathStroke? pending = null;
            foreach (var stroke in branch.OrderBy(stroke => stroke.A).ThenBy(stroke => stroke.B))
            {
                if (pending is { } span && stroke.A.CompareTo(span.B) <= 0)
                {
                    if (stroke.B.CompareTo(span.B) > 0) pending = span with { B = stroke.B };
                    continue;
                }
                if (pending.HasValue) result.Add(pending.Value);
                pending = stroke;
            }
            if (pending.HasValue) result.Add(pending.Value);
        }
        return result;
    }
}
