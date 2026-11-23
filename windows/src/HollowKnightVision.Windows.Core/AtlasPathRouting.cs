namespace HollowKnightVision.Windows.Core;

public static class AtlasPathRouting
{
    public static IReadOnlyList<AtlasPathPoint> FindRoute(AtlasPlatformGraph graph, AtlasPathStroke startStroke,
        AtlasPathPoint start, AtlasPathStroke targetStroke, AtlasPathPoint target)
    {
        if (!startStroke.Contains(start) || !targetStroke.Contains(target)) return [];
        return new AtlasPathSearch(graph, startStroke, start, targetStroke: targetStroke, target: target, stopAtTarget: true).RouteTo(target);
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

internal sealed class AtlasPathSearch
{
    private readonly Dictionary<AtlasPathPoint, int> costs = [];
    private readonly Dictionary<AtlasPathPoint, AtlasPathPoint> predecessors = [];

    internal AtlasPathSearch(AtlasPlatformGraph graph, AtlasPathStroke startStroke, AtlasPathPoint start,
        Func<AtlasPathPoint, AtlasPathPoint, bool>? admits = null, AtlasPathStroke? targetStroke = null,
        AtlasPathPoint? target = null, bool stopAtTarget = false)
    {
        // Splitting a stroke at an exact cursor position leaves the cached graph unchanged.
        var incoming = targetStroke is { } goal && target is { } endpoint
            ? graph.Vertices(goal).Where(point => goal.AllowsTraversal(point, endpoint)).ToHashSet()
            : [];
        var outgoing = graph.Vertices(startStroke).Where(point => startStroke.AllowsTraversal(start, point)).ToArray();
        var pending = new PriorityQueue<AtlasPathPoint, (int Cost, AtlasPathPoint Point)>();
        costs[start] = 0;
        pending.Enqueue(start, (0, start));
        while (pending.TryDequeue(out var point, out var priority))
        {
            if (costs[point] != priority.Cost) continue;
            if (stopAtTarget && point == target) return;
            if (graph.Nodes.TryGetValue(point, out var neighbors))
                foreach (var next in neighbors) Visit(next);
            if (point == start)
            {
                foreach (var next in outgoing) Visit(next);
                if (startStroke == targetStroke && target is { } here && startStroke.AllowsTraversal(start, here))
                    Visit(here);
            }
            if (target is { } destination && incoming.Contains(point)) Visit(destination);

            void Visit(AtlasPathPoint next)
            {
                var cost = priority.Cost + point.DistanceTo(next);
                if (costs.TryGetValue(next, out var previous) && previous <= cost) return;
                if (admits is not null && !admits(point, next)) return;
                costs[next] = cost;
                predecessors[next] = point;
                pending.Enqueue(next, (cost, next));
            }
        }
    }

    internal bool Reaches(AtlasPathPoint point) => costs.ContainsKey(point);
    internal int DistanceTo(AtlasPathPoint point) => costs[point];

    internal IReadOnlyList<AtlasPathPoint> RouteTo(AtlasPathPoint point)
    {
        if (!costs.ContainsKey(point)) return [];
        var path = new List<AtlasPathPoint> { point };
        while (predecessors.TryGetValue(point, out var previous))
        {
            path.Add(previous);
            point = previous;
        }
        path.Reverse();
        return path;
    }
}
