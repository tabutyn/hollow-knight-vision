namespace HollowKnightVision.Windows.Core;

/// <summary>Rigid Atlas components backed by image links and axis-specific doorway evidence.</summary>
internal sealed class LiveIslandComponents
{
    internal sealed record LinkPlan(
        IReadOnlyDictionary<int, AtlasPoint> Shifts, IReadOnlySet<int> PlacedIslands);

    private sealed record Group(IReadOnlyList<int> Islands, bool Fixed);
    private sealed record Join(Group From, Group To, Group? Moving, double Shift, double Unresolved)
    {
        internal double ShiftFor(int island) => Moving?.Islands.Contains(island) == true ? Shift : 0;
        internal IEnumerable<int> Placed() => From.Fixed || To.Fixed ? From.Islands.Union(To.Islands) : [];
    }

    private readonly Dictionary<int, LiveTrackingIsland> owners;
    private readonly Dictionary<int, Group> horizontal;
    private readonly Dictionary<int, Group> vertical;
    private readonly HashSet<Group> doorwayGroups;

    internal LiveIslandComponents(LiveWorldSnapshot snapshot, IReadOnlySet<int>? fixedObservations = null)
    {
        owners = snapshot.TrackingIslands.SelectMany(island =>
            island.Members.Select(member => (member.ObservationId, Island: island)))
            .ToDictionary(pair => pair.ObservationId, pair => pair.Island);
        var keyframes = snapshot.Keyframes.ToDictionary(keyframe => keyframe.Id);
        var visual = new List<(int From, int To)>();
        foreach (var link in snapshot.LoopClosureEdges)
        {
            if (Owner(keyframes[link.FromKeyframeId].ObservationId) is { } from
                && Owner(keyframes[link.ToKeyframeId].ObservationId) is { } to && from.Id != to.Id
                && (fixedObservations is null
                    || from.Placement != LiveIslandPlacement.LegacyUnknown && to.Placement != LiveIslandPlacement.LegacyUnknown))
                visual.Add((from.Id, to.Id));
        }
        // Ordinary recovery holds the displayed map fixed. When solving a
        // measured doorway or a connected image loop, estimates can move.
        var fixedIds = snapshot.TrackingIslands.Where(island => fixedObservations is null
            ? !island.RequiresPlacement
            : island.Placement is LiveIslandPlacement.Reference or LiveIslandPlacement.Connected or LiveIslandPlacement.LegacyUnknown
                || island.Members.Any(member => fixedObservations.Contains(member.ObservationId)))
            .Select(island => island.Id).ToHashSet();
        var doors = snapshot.DoorwayFloorConstraints.Select(link =>
            (From: owners[link.DepartureObservationId].Id, To: owners[link.ArrivalObservationId].Id)).ToArray();
        horizontal = Groups(snapshot.TrackingIslands, visual, fixedIds);
        vertical = Groups(snapshot.TrackingIslands, visual.Concat(doors), fixedIds);
        doorwayGroups = doors.Select(link => vertical[link.From]).ToHashSet();
    }

    internal LiveTrackingIsland? Owner(int observation) => owners.GetValueOrDefault(observation);

    internal bool HasDoorwayElevation(int observation) =>
        Owner(observation) is { } island && doorwayGroups.Contains(vertical[island.Id]);

    internal bool SameRigidComponent(int first, int second) =>
        ReferenceEquals(horizontal[first], horizontal[second]) && ReferenceEquals(vertical[first], vertical[second]);

    internal LinkPlan? Plan(int from, int to, AtlasPoint correction, double tolerance)
    {
        if (!LiveWorldSnapshot.Finite(correction)) return null;
        var x = JoinAxis(horizontal[from], horizontal[to], correction.X);
        var y = JoinAxis(vertical[from], vertical[to], correction.Y);
        if (x.Unresolved * x.Unresolved + y.Unresolved * y.Unresolved > tolerance * tolerance)
            return null;

        var shifts = new Dictionary<int, AtlasPoint>();
        foreach (var island in (x.Moving?.Islands ?? []).Union(y.Moving?.Islands ?? []))
            shifts.Add(island, new AtlasPoint(x.ShiftFor(island), y.ShiftFor(island)));
        return new(shifts, x.Placed().Intersect(y.Placed()).ToHashSet());
    }

    internal IReadOnlyDictionary<int, double>? PlanDoorwayElevation(int from, int to, double correction, double tolerance)
    {
        if (!double.IsFinite(correction)) return null;
        var height = JoinAxis(vertical[from], vertical[to], correction);
        if (Math.Abs(height.Unresolved) > tolerance) return null;
        return (height.Moving?.Islands ?? []).ToDictionary(id => id, height.ShiftFor);
    }

    internal IReadOnlyDictionary<int, LiveIslandUncertainty> EstimateVisualUncertainty(
        LiveWorldSnapshot snapshot, IReadOnlySet<int> placed)
    {
        var edges = placed.ToDictionary(id => id, _ => new List<(int To, double Cost)>());
        var keyframes = snapshot.Keyframes.ToDictionary(frame => frame.Id, frame => owners[frame.ObservationId].Id);
        foreach (var link in snapshot.LoopClosureEdges)
        {
            var from = keyframes[link.FromKeyframeId];
            var to = keyframes[link.ToKeyframeId];
            if (from == to || !edges.TryGetValue(from, out var left) || !edges.TryGetValue(to, out var right))
                continue;
            var cost = Math.Max(1, link.ResidualPixels ?? 4);
            left.Add((to, cost));
            right.Add((from, cost));
        }
        var origin = snapshot.AnchoredObservationId is { } anchor ? Owner(anchor)?.Id : null;
        var anchors = snapshot.TrackingIslands.Where(island => placed.Contains(island.Id)
            && (island.Id == origin || island.Placement is LiveIslandPlacement.Reference or LiveIslandPlacement.Connected))
            .ToArray();
        var x = ShortestDistances(value => value.X);
        var y = ShortestDistances(value => value.Y);
        return placed.ToDictionary(id => id, id => new LiveIslandUncertainty(x.GetValueOrDefault(id), y.GetValueOrDefault(id)));

        Dictionary<int, double?> ShortestDistances(Func<LiveIslandUncertainty, double?> axis)
        {
            var distances = new Dictionary<int, double?>();
            var pending = new PriorityQueue<int, double>();
            foreach (var island in anchors)
            {
                var value = axis(island.Uncertainty) ?? (island.Id == origin ? 0d : (double?)null);
                if (value is not { } known) continue;
                distances[island.Id] = known;
                pending.Enqueue(island.Id, known);
            }
            while (pending.TryDequeue(out var id, out var distance))
            {
                if (distance != distances[id]) continue;
                foreach (var (next, cost) in edges[id])
                {
                    var candidate = distance + cost;
                    if (candidate >= (distances.GetValueOrDefault(next) ?? double.PositiveInfinity)) continue;
                    distances[next] = candidate;
                    pending.Enqueue(next, candidate);
                }
            }
            return distances;
        }
    }

    private static Join JoinAxis(Group from, Group to, double delta)
    {
        if (ReferenceEquals(from, to) || from.Fixed && to.Fixed)
            return new(from, to, null, 0, delta);
        var moveTo = from.Fixed || !to.Fixed && from.Islands[0] < to.Islands[0];
        return new(from, to, moveTo ? to : from, moveTo ? delta : -delta, 0);
    }

    private static Dictionary<int, Group> Groups(IReadOnlyList<LiveTrackingIsland> islands,
        IEnumerable<(int From, int To)> links, HashSet<int> fixedIds)
    {
        var neighbors = islands.ToDictionary(island => island.Id, _ => new List<int>());
        foreach (var (from, to) in links)
        {
            neighbors[from].Add(to);
            neighbors[to].Add(from);
        }
        var byIsland = new Dictionary<int, Group>();
        var visited = new HashSet<int>();
        foreach (var island in islands)
        {
            if (!visited.Add(island.Id)) continue;
            var members = new List<int>();
            var pending = new Stack<int>();
            pending.Push(island.Id);
            while (pending.TryPop(out var id))
            {
                members.Add(id);
                foreach (var neighbor in neighbors[id])
                    if (visited.Add(neighbor)) pending.Push(neighbor);
            }
            members.Sort();
            var group = new Group(members, members.Any(fixedIds.Contains));
            foreach (var id in members) byIsland.Add(id, group);
        }
        return byIsland;
    }
}
