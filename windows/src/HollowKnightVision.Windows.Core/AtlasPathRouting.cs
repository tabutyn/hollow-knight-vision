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
        // Only new positions split a stroke. An existing node can be the end of
        // a different jump shelf that crosses this one without a transfer.
        var attachments = new Dictionary<AtlasPathPoint, List<AtlasPathPoint>>();
        Attach(startStroke, start);
        if (targetStroke is { } goal && target is { } endpoint)
        {
            Attach(goal, endpoint);
            var (left, right) = start.CompareTo(endpoint) < 0 ? (start, endpoint) : (endpoint, start);
            if (goal == startStroke && start != endpoint && goal.AllowsTraversal(start, endpoint)
                && !graph.Vertices(goal).Any(point => left.CompareTo(point) < 0 && point.CompareTo(right) < 0))
                Connect(start, endpoint);
        }
        var pending = new PriorityQueue<AtlasPathPoint, (int Cost, AtlasPathPoint Point)>();
        costs[start] = 0;
        pending.Enqueue(start, (0, start));
        while (pending.TryDequeue(out var point, out var priority))
        {
            if (costs[point] != priority.Cost) continue;
            if (stopAtTarget && point == target) return;
            if (graph.Nodes.TryGetValue(point, out var neighbors))
                foreach (var next in neighbors) Visit(next);
            if (attachments.TryGetValue(point, out var adjacent))
                foreach (var next in adjacent) Visit(next);

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

        void Attach(AtlasPathStroke stroke, AtlasPathPoint point)
        {
            if (graph.Nodes.ContainsKey(point) || attachments.ContainsKey(point)) return;
            AtlasPathPoint? lower = null;
            foreach (var vertex in graph.Vertices(stroke))
            {
                var side = vertex.CompareTo(point);
                if (side < 0) lower = vertex;
                if (side <= 0) continue;
                AddNeighbor(vertex);
                break;
            }
            if (lower is { } prior) AddNeighbor(prior);

            void AddNeighbor(AtlasPathPoint vertex)
            {
                if (stroke.AllowsTraversal(point, vertex)) Connect(point, vertex);
                if (stroke.AllowsTraversal(vertex, point)) Connect(vertex, point);
            }
        }

        void Connect(AtlasPathPoint from, AtlasPathPoint to)
        {
            if (!attachments.TryGetValue(from, out var neighbors)) attachments[from] = neighbors = [];
            neighbors.Add(to);
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
