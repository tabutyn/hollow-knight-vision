namespace HollowKnightVision.Windows.Core;

public readonly record struct AtlasPathHint(AtlasPathPoint Objective, AtlasPathPoint Current, AtlasPathPoint Next)
{
    internal int Remaining { get; init; }
    internal int? PriorRemaining { get; init; }
    internal AtlasPathPoint Projection { get; init; }
    internal int? FallSteering { get; init; }
}

/// <summary>Selects and follows reachable unvisited ground on a directed room chart.</summary>
internal sealed class AtlasPathNavigator
{
    private readonly record struct Leg(AtlasPathStroke Stroke, AtlasPathPoint From, AtlasPathPoint To)
    {
        internal AtlasPathPoint Project(AtlasPathPoint point) =>
            new(Math.Clamp(point.X, Math.Min(From.X, To.X), Math.Max(From.X, To.X)),
                Math.Clamp(point.Y, Math.Min(From.Y, To.Y), Math.Max(From.Y, To.Y)));
    }

    private AtlasPathPoint? objective;
    private Leg[] flight = [];
    private readonly HashSet<AtlasPathPoint> visited = [];
    private int bucketSize;
    private bool automatic = true;
    private AtlasPathPoint? heading;
    private AtlasPathPoint? inheritedHeading;
    internal bool HasStartProjection { get; private set; }
    internal AtlasPathPoint? LastProjection { get; private set; }
    internal int? PriorRemaining { get; private set; }
    internal IReadOnlyList<AtlasPathPoint> SelectedRoute { get; private set; } = [];

    public void Interrupt()
    {
        flight = [];
        SelectedRoute = [];
        LastProjection = null;
        PriorRemaining = null;
    }

    internal void ClearObjective()
    {
        objective = null;
        heading = null;
        inheritedHeading = null;
        Interrupt();
    }

    internal void InheritAutomaticDirection(AtlasPathNavigator source)
    {
        if (!automatic || !source.automatic || (source.heading ?? source.inheritedHeading) is not { } direction) return;
        inheritedHeading = direction;
        Interrupt();
    }

    internal void MergeVisitedFrom(AtlasPathNavigator source, AtlasPathPoint translation, int height)
    {
        if (source.bucketSize == 0) return;
        SetBucketSize(height);
        var center = source.bucketSize / 2;
        foreach (var point in source.visited)
            visited.Add(Bucket(new(point.X * source.bucketSize + center + translation.X,
                point.Y * source.bucketSize + center + translation.Y)));
    }

    internal AtlasPathHint? Follow(AtlasPlatformGraph graph, AtlasPathPoint feet, bool? grounded, bool measured,
        Func<AtlasPathPoint, AtlasPathPoint, bool>? admitted, IReadOnlyList<AtlasPathPlatform> walked,
        AtlasPathPoint? fixedObjective = null, bool fixedObjectiveOnGround = true, int? previewRadius = null)
    {
        automatic = !fixedObjective.HasValue;
        SelectedRoute = [];
        LastProjection = null;
        PriorRemaining = null;
        var h = graph.KnightHeight;
        var position = new AtlasPathPoint(feet.X, feet.Y + h);
        var floor = grounded == false ? null : graph.NearestStroke(position, grounded == true ? h : Math.Max(2, h / 3), walkOnly: true);
        var airborne = floor is null && grounded != true ? ProjectFlight(graph, position) : null;
        var nearest = floor ?? airborne?.Stroke ?? graph.NearestStroke(position, previewRadius ?? h,
            walkOnly: previewRadius.HasValue && grounded == true);
        HasStartProjection = nearest.HasValue;
        if (nearest is not { } startStroke)
        {
            flight = [];
            return null;
        }
        var start = airborne?.Project(position) ?? startStroke.Closest(position);
        LastProjection = start;
        SetBucketSize(h);
        if (measured && floor.HasValue)
            foreach (var vertex in graph.Vertices(startStroke))
                if (vertex.DistanceTo(start) <= Math.Max(4, h / 2))
                    visited.Add(Bucket(vertex));

        var previousObjective = fixedObjective ?? objective;
        var oldGoalLine = previousObjective is { } previous
            ? graph.NearestStroke(previous, h, walkOnly: !fixedObjective.HasValue || fixedObjectiveOnGround) : null;
        var oldGoal = oldGoalLine is { } prior && previousObjective is { } target ? prior.Closest(target) : (AtlasPathPoint?)null;
        var search = new AtlasPathSearch(graph, startStroke, start, admitted, oldGoalLine, oldGoal, stopAtTarget: fixedObjective.HasValue);
        if (oldGoal is { } reachableGoal && search.Reaches(reachableGoal)) PriorRemaining = search.DistanceTo(reachableGoal);
        if (fixedObjective.HasValue)
        {
            if (oldGoal is { } goal && search.Reaches(goal)) return Guide(goal, search.RouteTo(goal));
            flight = [];
            return null;
        }
        var retained = oldGoal is { } previousGoal && PriorRemaining.HasValue && IsUnvisited(previousGoal)
            && start.DistanceTo(previousGoal) > Math.Max(3, h / 3) && FollowsHeading(previousGoal) ? oldGoal : null;
        if (retained is { } goalToKeep && (!startStroke.Contains(goalToKeep) || start.DistanceTo(goalToKeep) > h))
        {
            return Guide(goalToKeep, search.RouteTo(goalToKeep));
        }
        var candidates = graph.WalkNodes.Where(point => search.Reaches(point) && IsUnvisited(point)).ToArray();
        if (candidates.Length == 0 && !retained.HasValue)
        {
            objective = null;
            flight = [];
            return null;
        }
        if (inheritedHeading.HasValue)
        {
            var aligned = candidates.Where(FollowsHeading).ToArray();
            if (aligned.Length > 0) candidates = aligned;
        }

        if (oldGoal is { } lip && startStroke.Kind == AtlasPathKind.Walk && startStroke.Contains(lip) && start.DistanceTo(lip) <= h)
        {
            foreach (var candidate in candidates.Where(point => !startStroke.Contains(point))
                         .OrderByDescending(point => (search.DistanceTo(point), point)))
            {
                var route = search.RouteTo(candidate);
                if (route.TakeWhile(startStroke.Contains).Last().DistanceTo(lip) <= Math.Max(2, graph.KnightWidth))
                    return Guide(candidate, route);
            }
        }
        if (retained is { } frontier) return Guide(frontier, search.RouteTo(frontier));
        var destination = candidates.MaxBy(point => (search.DistanceTo(point), point));
        return Guide(destination, search.RouteTo(destination));

        AtlasPathHint Guide(AtlasPathPoint target, IReadOnlyList<AtlasPathPoint> route)
        {
            var origin = start;
            var next = route.Count > 1 ? route[1] : start;
            if (airborne is null || target != oldGoal)
            {
                var crossing = new List<Leg>();
                for (var i = 1; i < route.Count; i++)
                {
                    var from = route[i - 1];
                    var to = route[i];
                    if (crossing.Count == 0)
                    {
                        if (startStroke.Kind == AtlasPathKind.Walk && startStroke.Contains(to)) continue;
                        if (start.DistanceTo(from) > 2 * h) break;
                    }
                    var stroke = graph.Strokes.FirstOrDefault(stroke => stroke.Kind != AtlasPathKind.Walk
                        && stroke.AllowsTraversal(from, to));
                    if (stroke.A == stroke.B) break;
                    crossing.Add(new(stroke, from, to));
                    if (graph.WalkNodes.Contains(to)) break;
                }
                flight = crossing.ToArray();
                var tolerance = Math.Max(2, Math.Min(graph.KnightWidth / 2, h / 2));
                if (flight.Length > 0 && (startStroke.Kind != AtlasPathKind.Walk || start.DistanceTo(flight[0].From) <= tolerance))
                {
                    origin = flight[0].From;
                    next = flight[0].To;
                }
            }
            objective = target;
            if (fixedObjective.HasValue) SelectedRoute = route;
            if (next != origin)
            {
                heading = new(Math.Sign(next.X - origin.X), Math.Sign(next.Y - origin.Y));
                inheritedHeading = null;
            }
            return new(target, origin, next)
            {
                Projection = start,
                Remaining = search.DistanceTo(target),
                PriorRemaining = PriorRemaining,
                FallSteering = floor is null && grounded != true ? DriftDirection(position, graph.KnightHeight) : null
            };
        }

        bool IsUnvisited(AtlasPathPoint point) => point != start && !visited.Contains(Bucket(point))
            && !walked.Any(span => Math.Abs(span.Y + h - point.Y) <= Math.Max(2, h / 5) && span.Contains(point.X));

        bool FollowsHeading(AtlasPathPoint point) => inheritedHeading is not { } desired
            || search.DistanceTo(point) >= Math.Max(3, h / 3) && search.FirstHopTo(point) is { } next
            && (next.X - start.X) * desired.X + (next.Y - start.Y) * desired.Y > 0;
    }

    private void SetBucketSize(int height)
    {
        var bin = Math.Max(4, height / 3);
        if (bin == bucketSize) return;
        visited.Clear();
        bucketSize = bin;
    }

    private AtlasPathPoint Bucket(AtlasPathPoint point) =>
        new((int)Math.Floor((double)point.X / bucketSize), (int)Math.Floor((double)point.Y / bucketSize));

    // The route draws horizontal turns at the bottom of each descent. A real
    // fall drifts during the descent, so interpolate by elapsed falling time.
    private int? DriftDirection(AtlasPathPoint position, int height)
    {
        if (flight.Length == 0 || flight[0].Stroke.Kind != AtlasPathKind.Drop) return null;
        var index = Array.FindIndex(flight, leg => leg.From.Y >= position.Y && position.Y > leg.To.Y && leg.From.X == leg.To.X);
        if (index < 0) return null;
        var descent = flight[index];
        var destination = descent.From.X;
        foreach (var turn in flight.Skip(index + 1).TakeWhile(turn => turn.From.Y == descent.To.Y && turn.To.Y == descent.To.Y))
            destination = turn.To.X;
        var top = flight[0].From.Y;
        double Elapsed(int y) => Math.Sqrt(Math.Max(0, top - y));
        var start = Elapsed(descent.From.Y);
        var duration = Elapsed(descent.To.Y) - start;
        if (duration <= 0) return null;
        var lookAhead = Math.Max(descent.To.Y, position.Y - Math.Max(1, height / 4));
        var fraction = (Elapsed(lookAhead) - start) / duration;
        var desired = descent.From.X + (destination - descent.From.X) * fraction;
        var error = desired - position.X;
        return Math.Abs(error) <= .5 ? 0 : Math.Sign(error);
    }

    private Leg? ProjectFlight(AtlasPlatformGraph graph, AtlasPathPoint position)
    {
        if (flight.Length == 0 || flight.Any(leg => !graph.Strokes.Contains(leg.Stroke))) return null;
        var first = flight[0];
        var last = flight[^1];
        var h = graph.KnightHeight;
        var w = graph.KnightWidth;
        var allowance = first.Stroke.Kind switch { AtlasPathKind.DoubleJump => 9 * h, AtlasPathKind.Jump => 5 * h, _ => h };
        if (position.Y < Math.Min(first.From.Y, last.To.Y) - h / 2 || position.Y > Math.Max(first.From.Y, last.To.Y) + allowance)
            return null;
        var beside = flight.Any(leg => Math.Min(leg.From.X, leg.To.X) - w <= position.X && position.X <= Math.Max(leg.From.X, leg.To.X) + w);
        var landing = Math.Abs(position.X - last.To.X) <= 2 * h && graph.Floors.Any(platform =>
            platform.Y + h == last.To.Y && platform.Contains(last.To.X) && platform.Contains(position.X));
        var takeoff = Math.Abs(position.X - first.From.X) <= 2 * h && position.Y >= first.From.Y - h / 2;
        if (!beside && !landing && !takeoff) return null;
        return Enumerable.Reverse(flight).MinBy(leg => leg.Project(position).DistanceTo(position));
    }
}
